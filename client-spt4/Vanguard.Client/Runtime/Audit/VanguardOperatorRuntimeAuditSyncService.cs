#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vanguard.Client.Api;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Raid.Services;

// Responsibility: Synchronizes F12 settings so the runtime authority uses the player-authorized raid values without mixing them with client-local presentation preferences.
// Flow: Player clients serialize their settings to the server; the actual Headless/direct host pulls owner-keyed state, applies only raid-scoped entries, and caches player-scoped entries separately.
// Authority boundary: The player is authority for configurable choices, the Headless/direct host is authority for raid execution, and local HUD/presentation settings never become shared gameplay state.
// Invariant: No latest-client-wins ambiguity: values stay owner-scoped, scope rules are enforced, and a transport failure keeps safe/default runtime behavior rather than inventing authority.
namespace Vanguard.Client.Runtime.Audit;

/// <summary>
/// F12 governance synchronization. Server I/O remains serialized on one background lane. Player clients
/// push their own F12 settings; the actual headless/direct player host pulls owner-keyed settings.
/// Only RAID_SCOPED values are projected into global ConfigEntry state on the runtime authority.
/// PLAYER_SCOPED values are cached by OwnerProfileId and never use latest-client-wins semantics.
/// </summary>
internal static class VanguardOperatorRuntimeAuditSyncService
{
    private static readonly VanguardApiClient ApiClient = new();
    private static readonly object IoSync = new();
    private static readonly TimeSpan PushRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PullInitialInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PullChangedInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PullStableInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PullFailureInterval = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PullInterOwnerInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RoleObservationInterval = TimeSpan.FromMilliseconds(250);

    private static readonly Dictionary<string, string> RemoteSignatureByOwner = new(StringComparer.Ordinal);
    private static readonly List<string> PullOwners = new();

    private static DateTimeOffset nextPullAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextPushAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextRoleObservationAtUtc = DateTimeOffset.MinValue;
    private static Task? activeIoTask;
    private static PendingIoResult? pendingIoResult;
    private static bool subscribed;
    private static bool pushPending;
    private static bool bootstrapLogged;
    private static bool lastEffectiveEnabled;
    private static string pendingPushSource = "client_f12_bootstrap";
    private static int nextPullOwnerIndex;
    private static int pullBurstRemaining;
    private static bool pullBurstChanged;
    private static int syncGeneration;
    private static long desiredPushRevision;
    private static FikaRoleSnapshot? lastObservedRoles;

    public static bool EffectiveEnabled => VanguardOperatorRuntimeAuditOptions.GetEnabled();

    public static void ResetForRaidLifecycle(string reason)
    {
        syncGeneration++;
        PullOwners.Clear();
        RemoteSignatureByOwner.Clear();
        nextPullOwnerIndex = 0;
        pullBurstRemaining = 0;
        pullBurstChanged = false;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        nextPullAtUtc = now + PullInitialInterval;
        nextPushAtUtc = DateTimeOffset.MaxValue;
        nextRoleObservationAtUtc = DateTimeOffset.MinValue;
        lastObservedRoles = null;
        if (!VanguardFikaCompat.IsActualHeadlessProcess)
        {
            MarkPushPending("client_f12_raid_start", now);
        }
        else
        {
            pushPending = false;
            pendingPushSource = "headless_no_push";
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.F12AuthorityConvergenceStatusTag,
            $"VANGUARD_F12_SYNC_RESET reason={Safe(reason)}; generation={syncGeneration}; pushPending={pushPending}; pushRevision={desiredPushRevision}; actualHeadless={VanguardFikaCompat.IsActualHeadlessProcess}");
    }

    public static void Initialize()
    {
        if (subscribed)
        {
            return;
        }

        VanguardOperatorRuntimeAuditOptions.Changed += OnLocalOptionsChanged;
        subscribed = true;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!VanguardFikaCompat.IsActualHeadlessProcess)
        {
            MarkPushPending("client_f12_bootstrap", now);
        }
        else
        {
            pushPending = false;
            nextPushAtUtc = DateTimeOffset.MaxValue;
        }

        nextPullAtUtc = now + PullInitialInterval;
        nextRoleObservationAtUtc = DateTimeOffset.MinValue;
        LogBootstrapOnce();
    }

    public static void Tick()
    {
        Initialize();
        DrainCompletedIoOnMainThread();
        if (VanguardHeadlessPostRaidQuiescenceService.IsActive)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        ObserveFikaRoleTransitions(now);
        if (!VanguardFikaCompat.IsActualHeadlessProcess && pushPending && now >= nextPushAtUtc)
        {
            TryQueuePush(now, pendingPushSource);
        }
        else if (ShouldPullOwnerScopedSettings() && now >= nextPullAtUtc)
        {
            TryQueuePull(now);
        }

        bool enabled = EffectiveEnabled;
        if (enabled != lastEffectiveEnabled)
        {
            lastEffectiveEnabled = enabled;
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.OperatorRuntimeAuditStatusTag,
                $"audit effective state changed enabled={enabled}; actualHeadless={VanguardFikaCompat.IsActualHeadlessProcess}; headlessRaid={VanguardFikaCompat.IsRaidHostedByHeadless}; requester={VanguardFikaCompat.IsHeadlessRequester}; client={VanguardFikaCompat.IsClient}; host={VanguardFikaCompat.IsHost}");
        }
    }

    private static bool ShouldPullOwnerScopedSettings()
    {
        return VanguardFikaCompat.IsInstalled && VanguardFikaCompat.IsRuntimeSettingsConsumerAuthority;
    }

    private static void OnLocalOptionsChanged()
    {
        if (VanguardFikaCompat.IsActualHeadlessProcess)
        {
            // Headless ConfigEntry changes are never player authority for synchronized runtime settings.
            return;
        }

        MarkPushPending("client_f12_changed", DateTimeOffset.UtcNow);
    }

    private static void MarkPushPending(string source, DateTimeOffset now)
    {
        pushPending = true;
        pendingPushSource = string.IsNullOrWhiteSpace(source) ? "client_f12" : source;
        desiredPushRevision++;
        if (nextPushAtUtc > now)
        {
            nextPushAtUtc = now;
        }
    }

    private static void ObserveFikaRoleTransitions(DateTimeOffset now)
    {
        if (now < nextRoleObservationAtUtc)
        {
            return;
        }

        nextRoleObservationAtUtc = now + RoleObservationInterval;
        FikaRoleSnapshot current = CaptureFikaRolesOnMainThread();
        FikaRoleSnapshot? previousNullable = lastObservedRoles;
        lastObservedRoles = current;
        if (!previousNullable.HasValue)
        {
            return;
        }

        FikaRoleSnapshot previous = previousNullable.Value;
        if (current.Equals(previous))
        {
            return;
        }

        bool authorityAcquired = !previous.CanWriteRaidScopedSettings && current.CanWriteRaidScopedSettings;
        bool requesterEvidenceAcquired = !previous.IsHeadlessRequester && current.IsHeadlessRequester;
        bool runtimeConsumerAcquired = !previous.IsRuntimeSettingsConsumerAuthority && current.IsRuntimeSettingsConsumerAuthority;

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.F12AuthorityConvergenceStatusTag,
            $"VANGUARD_F12_ROLE_TRANSITION previous={Safe(previous.ToDiagnosticString())}; current={Safe(current.ToDiagnosticString())}; authorityAcquired={authorityAcquired}; requesterEvidenceAcquired={requesterEvidenceAcquired}; runtimeConsumerAcquired={runtimeConsumerAcquired}; mutation=sync_only");

        if (!current.IsActualHeadlessProcess && (authorityAcquired || requesterEvidenceAcquired))
        {
            MarkPushPending("client_f12_role_authority_acquired", now);
        }

        if (runtimeConsumerAcquired && nextPullAtUtc > now)
        {
            nextPullAtUtc = now;
        }
    }

    private static void LogBootstrapOnce()
    {
        if (bootstrapLogged)
        {
            return;
        }

        bootstrapLogged = true;
        VanguardClientDiagnosticsLog.Diagnostic(
            VanguardBuildVersion.F12AuthorityConvergenceStatusTag,
            () => $"VANGUARD_F12_AUTHORITY_BOOT singleBackgroundIoLane=true; latestClientWins=false; playerScope=owner_profile_id; raidScopeWriter=headless_requester_or_direct_player_host; actualHeadlessCanWriteRaidScope=false; actualHeadless={VanguardFikaCompat.IsActualHeadlessProcess}; raidHostedByHeadless={VanguardFikaCompat.IsRaidHostedByHeadless}; headlessRequester={VanguardFikaCompat.IsHeadlessRequester}; requesterNative={VanguardFikaCompat.IsHeadlessRequesterNative}; requesterSocket={VanguardFikaCompat.IsHeadlessRequesterSocketEvidence}; requesterEvidence={VanguardFikaCompat.HeadlessRequesterEvidenceSource}; directPlayerHost={VanguardFikaCompat.IsDirectPlayerRaidHost}; runtimeConsumerAuthority={VanguardFikaCompat.IsRuntimeSettingsConsumerAuthority}; roleRequalification=true; pushRevisionCoalescing=true; liveF12Rearm=true; governance={VanguardRuntimeSettingsScopeCatalog.GovernanceVersion}; tacticalAuthoring={VanguardRuntimeSettingsScopeCatalog.TacticalAuthoringScope}");
    }

    private static void TryQueuePull(DateTimeOffset now)
    {
        if (!TryReserveIoSlot())
        {
            return;
        }

        if (pullBurstRemaining <= 0 || PullOwners.Count == 0)
        {
            RefreshPullOwnersOnMainThread();
        }

        if (PullOwners.Count == 0)
        {
            ReleaseIoReservation();
            nextPullAtUtc = now + PullFailureInterval;
            return;
        }

        nextPullOwnerIndex = Math.Abs(nextPullOwnerIndex) % PullOwners.Count;
        string ownerProfileId = PullOwners[nextPullOwnerIndex];
        nextPullOwnerIndex = (nextPullOwnerIndex + 1) % PullOwners.Count;
        if (pullBurstRemaining <= 0)
        {
            pullBurstRemaining = PullOwners.Count;
            pullBurstChanged = false;
        }

        FikaRoleSnapshot roles = CaptureFikaRolesOnMainThread();
        nextPullAtUtc = now + PullFailureInterval;
        StartIoTask(PendingIoKind.Pull, "runtime_f12_owner_pull", ownerProfileId, request: null, roles, pushRevision: 0);
    }

    private static void RefreshPullOwnersOnMainThread()
    {
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (string profileId in VanguardFikaCompat.GetFikaPlayerProfileIds())
        {
            if (!string.IsNullOrWhiteSpace(profileId))
            {
                owners.Add(profileId.Trim());
            }
        }

        foreach (string profileId in VanguardRaidOperatorRuntimeRegistry.GetKnownOwnerProfileIds())
        {
            if (!string.IsNullOrWhiteSpace(profileId))
            {
                owners.Add(profileId.Trim());
            }
        }

        PullOwners.Clear();
        PullOwners.AddRange(owners.OrderBy(value => value, StringComparer.Ordinal));
        if (nextPullOwnerIndex >= PullOwners.Count)
        {
            nextPullOwnerIndex = 0;
        }

        pullBurstRemaining = PullOwners.Count;
        pullBurstChanged = false;
    }

    private static void TryQueuePush(DateTimeOffset now, string source)
    {
        if (!TryReserveIoSlot())
        {
            return;
        }

        FikaRoleSnapshot roles = CaptureFikaRolesOnMainThread();
        long pushRevision = desiredPushRevision;
        VanguardOperatorRuntimeAuditSettingsRequestDto request;
        try
        {
            request = BuildLocalSettingsRequest(source, roles);
        }
        catch (Exception exception)
        {
            ReleaseIoReservation();
            nextPushAtUtc = now + PushRetryInterval;
            pushPending = true;
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.F12AuthorityConvergenceStatusTag,
                $"VANGUARD_F12_REQUEST_BUILD_FAILED kind=push; pushRevision={pushRevision}; type={exception.GetType().Name}; message={Safe(exception.Message)}; retrySeconds={PushRetryInterval.TotalSeconds:0}");
            return;
        }

        nextPushAtUtc = now + PushRetryInterval;
        StartIoTask(PendingIoKind.Push, source, targetOwnerProfileId: null, request, roles, pushRevision);
    }

    private static bool TryReserveIoSlot()
    {
        lock (IoSync)
        {
            if (activeIoTask != null && !activeIoTask.IsCompleted)
            {
                return false;
            }

            if (pendingIoResult != null)
            {
                return false;
            }

            activeIoTask = Task.CompletedTask;
            return true;
        }
    }

    private static void ReleaseIoReservation()
    {
        lock (IoSync)
        {
            activeIoTask = null;
        }
    }

    private static void StartIoTask(
        PendingIoKind kind,
        string source,
        string? targetOwnerProfileId,
        VanguardOperatorRuntimeAuditSettingsRequestDto? request,
        FikaRoleSnapshot roles,
        long pushRevision)
    {
        int generation = syncGeneration;
        Task task = Task.Run(() =>
        {
            PendingIoResult result;
            try
            {
                VanguardOperatorRuntimeAuditSettingsResponseDto response = kind == PendingIoKind.Pull
                    ? ApiClient.GetRuntimeAuditSettings(
                        targetOwnerProfileId,
                        roles.IsInstalled,
                        roles.IsActualHeadlessProcess,
                        roles.IsHeadlessRequester,
                        roles.IsHost,
                        roles.IsRaidHostedByHeadless)
                    : ApiClient.SetRuntimeAuditSettings(request ?? throw new InvalidOperationException("runtime audit push request missing"));
                result = PendingIoResult.FromResponse(kind, source, targetOwnerProfileId, response, generation, roles, pushRevision);
            }
            catch (Exception exception)
            {
                result = PendingIoResult.FromException(kind, source, targetOwnerProfileId, exception, generation, roles, pushRevision);
            }

            lock (IoSync)
            {
                pendingIoResult = result;
            }
        });

        lock (IoSync)
        {
            activeIoTask = task;
        }
    }

    private static void DrainCompletedIoOnMainThread()
    {
        PendingIoResult? result;
        lock (IoSync)
        {
            result = pendingIoResult;
            if (result == null)
            {
                return;
            }

            pendingIoResult = null;
            activeIoTask = null;
        }

        if (result.Generation != syncGeneration)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (result.Exception != null)
        {
            HandleIoFailure(result, now, result.Exception.GetType().Name, result.Exception.Message);
            return;
        }

        VanguardOperatorRuntimeAuditSettingsResponseDto? response = result.Response;
        if (response == null || !response.Success || response.Settings == null)
        {
            HandleIoFailure(result, now, "server_response", response?.Reason ?? "missing_settings");
            return;
        }

        if (result.Kind == PendingIoKind.Push)
        {
            bool superseded = result.PushRevision < desiredPushRevision;
            pushPending = superseded;
            nextPushAtUtc = superseded ? now : DateTimeOffset.MaxValue;
            string owner = response.Settings.UpdatedByProfileId ?? result.TargetOwnerProfileId ?? "none";
            VanguardRuntimeSettingsAuthorityResolver.ApplyPlayerScoped(owner, response.Settings, result.Source);
            bool raidScopeConfirmed = result.Roles.CanWriteRaidScopedSettings
                && string.Equals(response.Settings.RaidAuthorityProfileId, owner, StringComparison.Ordinal);
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.F12AuthorityConvergenceStatusTag,
                $"VANGUARD_F12_PUSH_OK owner={Safe(owner)}; pushRevision={result.PushRevision}; desiredRevision={desiredPushRevision}; superseded={superseded}; submittedCanWriteRaidScope={result.Roles.CanWriteRaidScopedSettings}; submittedRequester={result.Roles.IsHeadlessRequester}; requesterEvidence={Safe(result.Roles.HeadlessRequesterEvidenceSource)}; raidScopeConfirmed={raidScopeConfirmed}; raidAuthority={Safe(response.Settings.RaidAuthorityProfileId)}; playerSource={Safe(response.Settings.PlayerScopedSource)}; raidSource={Safe(response.Settings.RaidScopedSource)}; reason={Safe(response.Reason)}");

            if (result.Roles.CanWriteRaidScopedSettings && !raidScopeConfirmed)
            {
                pushPending = true;
                nextPushAtUtc = now + PushRetryInterval;
                VanguardClientDiagnosticsLog.Warning(
                    VanguardBuildVersion.F12AuthorityConvergenceStatusTag,
                    $"VANGUARD_F12_RAID_SCOPE_NOT_CONFIRMED owner={Safe(owner)}; submittedRequester={result.Roles.IsHeadlessRequester}; requesterEvidence={Safe(result.Roles.HeadlessRequesterEvidenceSource)}; responseAuthority={Safe(response.Settings.RaidAuthorityProfileId)}; retrySeconds={PushRetryInterval.TotalSeconds:0}; mutation=sync_only");
            }
            else if (raidScopeConfirmed)
            {
                VanguardClientDiagnosticsLog.Info(
                    VanguardBuildVersion.F12AuthorityConvergenceStatusTag,
                    $"VANGUARD_F12_RAID_SCOPE_CONFIRMED owner={Safe(owner)}; requesterEvidence={Safe(result.Roles.HeadlessRequesterEvidenceSource)}; raidSource={Safe(response.Settings.RaidScopedSource)}; governance={Safe(response.Settings.GovernanceVersion)}");
            }

            return;
        }

        string targetOwner = response.Settings.UpdatedByProfileId ?? result.TargetOwnerProfileId ?? "none";
        VanguardRuntimeSettingsAuthorityResolver.ApplyPlayerScoped(targetOwner, response.Settings, result.Source);
        VanguardOperatorRuntimeAuditOptions.ApplyRemoteRaidScoped(response.Settings);
        bool changed = LogRemoteSettingsChange(targetOwner, response.Settings, response.Reason);
        pullBurstChanged |= changed;
        pullBurstRemaining = Math.Max(0, pullBurstRemaining - 1);
        if (pullBurstRemaining > 0)
        {
            nextPullAtUtc = now + PullInterOwnerInterval;
        }
        else
        {
            nextPullAtUtc = now + (pullBurstChanged ? PullChangedInterval : PullStableInterval);
            pullBurstChanged = false;
        }
    }

    private static void HandleIoFailure(PendingIoResult result, DateTimeOffset now, string type, string message)
    {
        if (result.Kind == PendingIoKind.Pull)
        {
            pullBurstRemaining = 0;
            nextPullAtUtc = now + PullFailureInterval;
        }
        else
        {
            pushPending = true;
            nextPushAtUtc = now + PushRetryInterval;
        }

        VanguardClientDiagnosticsLog.Warning(
            VanguardBuildVersion.F12AuthorityConvergenceStatusTag,
            $"VANGUARD_F12_SYNC_FAILED kind={result.Kind}; owner={Safe(result.TargetOwnerProfileId)}; source={Safe(result.Source)}; type={Safe(type)}; message={Safe(message)}; retrySeconds={(result.Kind == PendingIoKind.Pull ? PullFailureInterval.TotalSeconds : PushRetryInterval.TotalSeconds):0}; mainThreadBlocked=false");
    }

    private static VanguardOperatorRuntimeAuditSettingsRequestDto BuildLocalSettingsRequest(string source, FikaRoleSnapshot roles)
    {
        VanguardOperatorRuntimeAuditSettingsDto local = VanguardOperatorRuntimeAuditOptions.CurrentLocalSettings(null, source);
        return new VanguardOperatorRuntimeAuditSettingsRequestDto
        {
            OwnerProfileId = local.UpdatedByProfileId,
            AuditLevel = local.AuditLevel,
            CombatDiagnosticsScope = local.CombatDiagnosticsScope,
            PerformanceTelemetry = local.PerformanceTelemetry,
            DetailedDiagnosticPayloads = local.DetailedDiagnosticPayloads,
            Enabled = local.Enabled,
            MovementProbeEnabled = local.MovementProbeEnabled,
            BrainProbeEnabled = local.BrainProbeEnabled,
            SainProbeEnabled = local.SainProbeEnabled,
            LootingBotsProbeEnabled = local.LootingBotsProbeEnabled,
            OrbitProbeEnabled = local.OrbitProbeEnabled,
            SummaryLogEnabled = local.SummaryLogEnabled,
            DecisionSnapshotLogEnabled = local.DecisionSnapshotLogEnabled,
            IntentDryRunEnabled = local.IntentDryRunEnabled,
            ThreatScannerDryRunEnabled = local.ThreatScannerDryRunEnabled,
            FirstActiveMobileMedicalLeaseEnabled = local.FirstActiveMobileMedicalLeaseEnabled,
            OperatorPostRaidPersistenceEnabled = local.OperatorPostRaidPersistenceEnabled,
            HeadlessKeepGcEnabledInRaid = local.HeadlessKeepGcEnabledInRaid,
            VerboseTransitionLogEnabled = local.VerboseTransitionLogEnabled,
            SnapshotIntervalSeconds = local.SnapshotIntervalSeconds,
            SummaryIntervalSeconds = local.SummaryIntervalSeconds,
            TransitionLogMinIntervalSeconds = local.TransitionLogMinIntervalSeconds,
            ThreatScannerIntervalSeconds = local.ThreatScannerIntervalSeconds,
            MovementOutsideBubbleRecallEnabled = local.MovementOutsideBubbleRecallEnabled,
            MovementSainBoundaryReturnEnabled = local.MovementSainBoundaryReturnEnabled,
            MovementSuppressExternalDuringRecallEnabled = local.MovementSuppressExternalDuringRecallEnabled,
            MovementVerboseDoctrineLogEnabled = local.MovementVerboseDoctrineLogEnabled,
            MovementTacticalRepositionEnabled = local.MovementTacticalRepositionEnabled,
            MovementTacticalRepositionCooldownSeconds = local.MovementTacticalRepositionCooldownSeconds,
            MovementTacticalRepositionMinDeltaMeters = local.MovementTacticalRepositionMinDeltaMeters,
            MovementTacticalBubbleMeters = local.MovementTacticalBubbleMeters,
            MovementSoftCorrectionMeters = local.MovementSoftCorrectionMeters,
            MovementHardCorrectionMeters = local.MovementHardCorrectionMeters,
            MovementCombatCohesionForcedCatchupMeters = local.MovementCombatCohesionForcedCatchupMeters,
            MovementTravelCatchUpEnterMeters = local.MovementTravelCatchUpEnterMeters,
            MovementTravelCatchUpExitMeters = local.MovementTravelCatchUpExitMeters,
            MovementTravelModeDwellSeconds = local.MovementTravelModeDwellSeconds,
            MovementActionRallyClearMeters = local.MovementActionRallyClearMeters,
            MovementActionRallyAcceptMeters = local.MovementActionRallyAcceptMeters,
            MovementActionRallyPreferredMeters = local.MovementActionRallyPreferredMeters,
            MovementLeaseStartCooldownSeconds = local.MovementLeaseStartCooldownSeconds,
            MovementLeaseFailureCooldownSeconds = local.MovementLeaseFailureCooldownSeconds,
            MovementLeaseNoProgressSeconds = local.MovementLeaseNoProgressSeconds,
            MovementLeaseMaxDurationSeconds = local.MovementLeaseMaxDurationSeconds,
            MovementActionRallyMaxReanchors = local.MovementActionRallyMaxReanchors,
            MovementOpportunisticLootBrokerEnabled = local.MovementOpportunisticLootBrokerEnabled,
            MovementOpportunisticLootMaxDistanceMeters = local.MovementOpportunisticLootMaxDistanceMeters,
            MovementOpportunisticLootScanCooldownSeconds = local.MovementOpportunisticLootScanCooldownSeconds,
            MovementOpportunisticLootGrantSeconds = local.MovementOpportunisticLootGrantSeconds,
            LootOperationalSessionEnabled = local.LootOperationalSessionEnabled,
            LootBackupLongWeaponEnabled = local.LootBackupLongWeaponEnabled,
            LootBackupPistolEnabled = local.LootBackupPistolEnabled,
            LootMedicalItemsEnabled = local.LootMedicalItemsEnabled,
            LootCompatibleMagazinesEnabled = local.LootCompatibleMagazinesEnabled,
            LootCompatibleLooseAmmunitionEnabled = local.LootCompatibleLooseAmmunitionEnabled,
            LootGrenadesEnabled = local.LootGrenadesEnabled,
            LootMaximumTransactionsPerCorpse = local.LootMaximumTransactionsPerCorpse,
            LootMaximumSessionSeconds = local.LootMaximumSessionSeconds,
            LootMaximumMedicalItemsPerSession = local.LootMaximumMedicalItemsPerSession,
            LootMaximumMagazinesPerSession = local.LootMaximumMagazinesPerSession,
            LootMaximumLooseAmmunitionRoundsPerSession = local.LootMaximumLooseAmmunitionRoundsPerSession,
            LootMaximumWeaponsPerSession = local.LootMaximumWeaponsPerSession,
            UpdatedByProfileId = local.UpdatedByProfileId,
            UpdatedBySource = local.UpdatedBySource,
            Source = local.UpdatedBySource,
            ClientBuild = VanguardBuildVersion.Value,
            ClientLabel = VanguardBuildVersion.BuildLabel,
            RequesterIsFikaInstalled = roles.IsInstalled,
            RequesterIsActualHeadlessProcess = roles.IsActualHeadlessProcess,
            RequesterIsHeadlessRequester = roles.IsHeadlessRequester,
            RequesterIsHost = roles.IsHost,
            RequesterRaidHostedByHeadless = roles.IsRaidHostedByHeadless
        };
    }

    private static FikaRoleSnapshot CaptureFikaRolesOnMainThread()
    {
        bool installed = VanguardFikaCompat.IsInstalled;
        bool actualHeadless = VanguardFikaCompat.IsActualHeadlessProcess;
        bool raidHostedByHeadless = VanguardFikaCompat.IsRaidHostedByHeadless;
        bool requesterNative = VanguardFikaCompat.IsHeadlessRequesterNative;
        bool requesterSocket = VanguardFikaCompat.IsHeadlessRequesterSocketEvidence;
        bool requester = requesterNative || requesterSocket;
        bool host = VanguardFikaCompat.IsHost;
        string evidence = requesterNative
            ? "fika_native_flag"
            : requesterSocket
                ? "connected_requester_websocket_headless_raid"
                : "none";
        return new FikaRoleSnapshot(
            installed,
            actualHeadless,
            requester,
            requesterNative,
            requesterSocket,
            host,
            raidHostedByHeadless,
            evidence);
    }

    private static bool LogRemoteSettingsChange(string owner, VanguardOperatorRuntimeAuditSettingsDto settings, string? reason)
    {
        string signature = string.Join("|",
            settings.AuditLevel,
            settings.CombatDiagnosticsScope,
            settings.PerformanceTelemetry,
            settings.DetailedDiagnosticPayloads,
            settings.Enabled,
            settings.FirstActiveMobileMedicalLeaseEnabled,
            settings.OperatorPostRaidPersistenceEnabled,
            settings.HeadlessKeepGcEnabledInRaid,
            settings.MovementTacticalBubbleMeters.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            settings.MovementHardCorrectionMeters.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            settings.MovementCombatCohesionForcedCatchupMeters.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            settings.MovementOpportunisticLootBrokerEnabled,
            settings.MovementOpportunisticLootMaxDistanceMeters.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            settings.LootOperationalSessionEnabled,
            settings.LootMaximumTransactionsPerCorpse,
            settings.RaidAuthorityProfileId,
            settings.PlayerScopedSource,
            settings.RaidScopedSource,
            settings.GovernanceVersion);

        if (RemoteSignatureByOwner.TryGetValue(owner, out string? previous)
            && string.Equals(previous, signature, StringComparison.Ordinal))
        {
            return false;
        }

        RemoteSignatureByOwner[owner] = signature;
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.F12AuthorityConvergenceStatusTag,
            $"VANGUARD_F12_OWNER_SETTINGS_APPLIED owner={Safe(owner)}; raidAuthority={Safe(settings.RaidAuthorityProfileId)}; playerSource={Safe(settings.PlayerScopedSource)}; raidSource={Safe(settings.RaidScopedSource)}; auditLevel={Safe(settings.AuditLevel)}; firstMedicalLease={settings.FirstActiveMobileMedicalLeaseEnabled}; postRaidPersistence={settings.OperatorPostRaidPersistenceEnabled}; headlessGcEnabledInRaid={settings.HeadlessKeepGcEnabledInRaid}; bubble={settings.MovementTacticalBubbleMeters:0.0}; hard={settings.MovementHardCorrectionMeters:0.0}; combatPursuitCohesion={settings.MovementCombatCohesionForcedCatchupMeters:0.0}; lootBroker={settings.MovementOpportunisticLootBrokerEnabled}; lootRadius={settings.MovementOpportunisticLootMaxDistanceMeters:0.0}; lootSession={settings.LootOperationalSessionEnabled}; lootTx={settings.LootMaximumTransactionsPerCorpse}; reason={Safe(reason)}; governance={Safe(settings.GovernanceVersion)}");
        return true;
    }

    private enum PendingIoKind
    {
        Pull,
        Push
    }

    private readonly record struct FikaRoleSnapshot(
        bool IsInstalled,
        bool IsActualHeadlessProcess,
        bool IsHeadlessRequester,
        bool IsHeadlessRequesterNative,
        bool IsHeadlessRequesterSocketEvidence,
        bool IsHost,
        bool IsRaidHostedByHeadless,
        string HeadlessRequesterEvidenceSource)
    {
        public bool IsDirectPlayerRaidHost => !IsActualHeadlessProcess && IsHost && !IsRaidHostedByHeadless;

        public bool CanWriteRaidScopedSettings => !IsInstalled || IsHeadlessRequester || IsDirectPlayerRaidHost;

        public bool IsRuntimeSettingsConsumerAuthority => !IsInstalled || IsActualHeadlessProcess || IsDirectPlayerRaidHost;

        public string ToDiagnosticString() => $"installed={IsInstalled},actualHeadless={IsActualHeadlessProcess},headlessRaid={IsRaidHostedByHeadless},requester={IsHeadlessRequester},requesterNative={IsHeadlessRequesterNative},requesterSocket={IsHeadlessRequesterSocketEvidence},requesterEvidence={HeadlessRequesterEvidenceSource},host={IsHost},directHost={IsDirectPlayerRaidHost},canWriteRaid={CanWriteRaidScopedSettings},consumer={IsRuntimeSettingsConsumerAuthority}";
    }

    private sealed class PendingIoResult
    {
        public PendingIoKind Kind { get; private init; }
        public string Source { get; private init; } = "none";
        public string? TargetOwnerProfileId { get; private init; }
        public VanguardOperatorRuntimeAuditSettingsResponseDto? Response { get; private init; }
        public Exception? Exception { get; private init; }
        public int Generation { get; private init; }
        public FikaRoleSnapshot Roles { get; private init; }
        public long PushRevision { get; private init; }

        public static PendingIoResult FromResponse(PendingIoKind kind, string source, string? targetOwnerProfileId, VanguardOperatorRuntimeAuditSettingsResponseDto response, int generation, FikaRoleSnapshot roles, long pushRevision)
        {
            return new PendingIoResult { Kind = kind, Source = source, TargetOwnerProfileId = targetOwnerProfileId, Response = response, Generation = generation, Roles = roles, PushRevision = pushRevision };
        }

        public static PendingIoResult FromException(PendingIoKind kind, string source, string? targetOwnerProfileId, Exception exception, int generation, FikaRoleSnapshot roles, long pushRevision)
        {
            return new PendingIoResult { Kind = kind, Source = source, TargetOwnerProfileId = targetOwnerProfileId, Exception = exception, Generation = generation, Roles = roles, PushRevision = pushRevision };
        }
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }
}
#endif
