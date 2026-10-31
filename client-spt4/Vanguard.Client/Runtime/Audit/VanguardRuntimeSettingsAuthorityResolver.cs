#if SPT_CLIENT
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;

// Responsibility: Resolve player-scoped runtime settings without sharing one owner's F12 policy with another owner or headless process.
// Flow: Cache cloned settings by owner profile ID, reject server-default placeholders and resolve missing owners through bounded fallbacks.
// Authority boundary: The matching player's published settings own player-scoped policy; process-local F12 values are not global authority.
// Invariant: Server-default buckets never become player-scoped authority and local fallbacks are restricted to the exact local player owner.
namespace Vanguard.Client.Runtime.Audit;

/// <summary>
/// The persistence path owner-keyed cache for PLAYER_SCOPED runtime settings. The headless never projects one
/// player's values into global ConfigEntry instances; future gameplay consumers resolve by
/// OwnerProfileId. The persistence path migrates the opportunistic corpse-loot family to this resolver; legacy
/// static getters remain local fallbacks for unrelated gameplay families until separately reviewed.
/// </summary>
internal static class VanguardRuntimeSettingsAuthorityResolver
{
    private static readonly object LogSync = new();
    private static readonly ConcurrentDictionary<string, VanguardOperatorRuntimeAuditSettingsDto> PlayerSettingsByOwner = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedOwners = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedFallbackOwners = new(StringComparer.Ordinal);

    public static void ResetForRaidLifecycle(string reason)
    {
        PlayerSettingsByOwner.Clear();
        lock (LogSync)
        {
            LoggedOwners.Clear();
            LoggedFallbackOwners.Clear();
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.F12AuthorityConvergenceStatusTag,
            $"VANGUARD_F12_PLAYER_SCOPE_CACHE_RESET reason={Safe(reason)}; governance={VanguardRuntimeSettingsScopeCatalog.GovernanceVersion}");
    }

    public static void ApplyPlayerScoped(string? ownerProfileId, VanguardOperatorRuntimeAuditSettingsDto? settings, string source)
    {
        string owner = NormalizeOwner(ownerProfileId ?? settings?.UpdatedByProfileId);
        if (settings == null || owner == "none")
        {
            return;
        }

        // A server-created owner bucket is only a transport placeholder. It must not become
        // PLAYER_SCOPED authority before that player client has actually published its F12 values.
        if (IsServerDefaultSource(settings.PlayerScopedSource))
        {
            return;
        }

        PlayerSettingsByOwner[owner] = Clone(settings);
        lock (LogSync)
        {
            if (!LoggedOwners.Add(owner))
            {
                return;
            }
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.F12AuthorityConvergenceStatusTag,
            $"VANGUARD_F12_PLAYER_SCOPE_APPLIED owner={Safe(owner)}; source={Safe(source)}; playerSource={Safe(settings.PlayerScopedSource)}; lootRadius={settings.MovementOpportunisticLootMaxDistanceMeters:0.0}; tcWeapon={settings.TacticalCoordinationDangerCloseWeaponMeters:0.0}/{settings.TacticalCoordinationProactiveWeaponMeters:0.0}/{settings.TacticalCoordinationOverwatchMinimumThreatMeters:0.0}; tcBuddy={settings.TacticalCoordinationBuddyIsolationMeters:0.0}/{settings.TacticalCoordinationBuddyHelperMaxMeters:0.0}; tcFlank={settings.TacticalCoordinationFlankLeadCloseMeters:0.0}/{settings.TacticalCoordinationFlankLeadFarMeters:0.0}; tcBoundAdvance={settings.TacticalCoordinationBoundAdvanceDistanceMeters:0.0}; lootSession={settings.LootOperationalSessionEnabled}; lootTx={settings.LootMaximumTransactionsPerCorpse}; governance={Safe(settings.GovernanceVersion)}");
    }

    public static bool TryGetPlayerScoped(string? ownerProfileId, out VanguardOperatorRuntimeAuditSettingsDto settings)
    {
        string owner = NormalizeOwner(ownerProfileId);
        if (owner != "none" && PlayerSettingsByOwner.TryGetValue(owner, out VanguardOperatorRuntimeAuditSettingsDto? cached))
        {
            // Cache entries are cloned on write and treated as immutable afterwards. Returning the
            // stable reference avoids allocating a 60+ field DTO during owner-scoped gameplay evaluation.
            settings = cached;
            return true;
        }

        settings = new VanguardOperatorRuntimeAuditSettingsDto();
        return false;
    }

    public static VanguardOperatorRuntimeAuditSettingsDto ResolvePlayerScoped(string? ownerProfileId, string source)
    {
        string owner = NormalizeOwner(ownerProfileId);
        if (owner != "none"
            && PlayerSettingsByOwner.TryGetValue(owner, out VanguardOperatorRuntimeAuditSettingsDto? cached))
        {
            return cached;
        }

        // Local F12 may be used only for the exact MainPlayer owner. Any other owner, including on a
        // direct host, receives independent defaults until its owner-keyed server pull arrives. A
        // dedicated headless never projects process-local ConfigEntry values into player policy.
        bool actualHeadless = VanguardFikaCompat.IsActualHeadlessProcess;
        bool logFallback;
        lock (LogSync)
        {
            logFallback = LoggedFallbackOwners.Add(owner + "|" + source);
        }

        bool exactLocalPlayerOwner = !actualHeadless && IsExactLocalPlayerOwner(owner);
        string fallbackAuthority = exactLocalPlayerOwner
            ? "exact_local_player_config"
            : actualHeadless
                ? "headless_owner_defaults_until_sync"
                : "remote_owner_defaults_until_sync";
        VanguardOperatorRuntimeAuditSettingsDto fallback = exactLocalPlayerOwner
            ? VanguardOperatorRuntimeAuditOptions.CurrentLocalSettings(ownerProfileId, source)
            : CreateUnsyncedOwnerDefaults(owner, fallbackAuthority);

        if (logFallback)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.F12AuthorityConvergenceStatusTag,
                $"VANGUARD_F12_PLAYER_SCOPE_FALLBACK owner={Safe(owner)}; source={Safe(source)}; authority={fallbackAuthority}; exactLocalPlayerOwner={exactLocalPlayerOwner}; lootAdmission={fallback.MovementOpportunisticLootBrokerEnabled}; mutation=false");
        }
        return fallback;
    }

    private static VanguardOperatorRuntimeAuditSettingsDto CreateUnsyncedOwnerDefaults(string owner, string source)
    {
        return new VanguardOperatorRuntimeAuditSettingsDto
        {
            UpdatedByProfileId = owner == "none" ? null : owner,
            UpdatedBySource = source,
            PlayerScopedSource = source,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            BuildLabel = VanguardBuildVersion.BuildLabel,
            GovernanceVersion = VanguardRuntimeSettingsScopeCatalog.GovernanceVersion,
            MovementOpportunisticLootBrokerEnabled = false,
            LootOperationalSessionEnabled = false
        };
    }

    private static bool IsExactLocalPlayerOwner(string owner)
    {
        if (owner == "none")
        {
            return false;
        }

        try
        {
            string? localProfileId = Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId;
            return !string.IsNullOrWhiteSpace(localProfileId)
                && string.Equals(localProfileId.Trim(), owner, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<string> GetCachedOwnerProfileIds()
    {
        return new List<string>(PlayerSettingsByOwner.Keys);
    }

    private static VanguardOperatorRuntimeAuditSettingsDto Clone(VanguardOperatorRuntimeAuditSettingsDto source)
    {
        return new VanguardOperatorRuntimeAuditSettingsDto
        {
            AuditLevel = source.AuditLevel,
            CombatDiagnosticsScope = source.CombatDiagnosticsScope,
            PerformanceTelemetry = source.PerformanceTelemetry,
            DetailedDiagnosticPayloads = source.DetailedDiagnosticPayloads,
            Enabled = source.Enabled,
            MovementProbeEnabled = source.MovementProbeEnabled,
            BrainProbeEnabled = source.BrainProbeEnabled,
            SainProbeEnabled = source.SainProbeEnabled,
            LootingBotsProbeEnabled = source.LootingBotsProbeEnabled,
            OrbitProbeEnabled = source.OrbitProbeEnabled,
            SummaryLogEnabled = source.SummaryLogEnabled,
            DecisionSnapshotLogEnabled = source.DecisionSnapshotLogEnabled,
            IntentDryRunEnabled = source.IntentDryRunEnabled,
            ThreatScannerDryRunEnabled = source.ThreatScannerDryRunEnabled,
            FirstActiveMobileMedicalLeaseEnabled = source.FirstActiveMobileMedicalLeaseEnabled,
            VerboseTransitionLogEnabled = source.VerboseTransitionLogEnabled,
            SnapshotIntervalSeconds = source.SnapshotIntervalSeconds,
            SummaryIntervalSeconds = source.SummaryIntervalSeconds,
            TransitionLogMinIntervalSeconds = source.TransitionLogMinIntervalSeconds,
            ThreatScannerIntervalSeconds = source.ThreatScannerIntervalSeconds,
            MovementOutsideBubbleRecallEnabled = source.MovementOutsideBubbleRecallEnabled,
            MovementSainBoundaryReturnEnabled = source.MovementSainBoundaryReturnEnabled,
            MovementSuppressExternalDuringRecallEnabled = source.MovementSuppressExternalDuringRecallEnabled,
            MovementVerboseDoctrineLogEnabled = source.MovementVerboseDoctrineLogEnabled,
            MovementTacticalRepositionEnabled = source.MovementTacticalRepositionEnabled,
            MovementTacticalRepositionCooldownSeconds = source.MovementTacticalRepositionCooldownSeconds,
            MovementTacticalRepositionMinDeltaMeters = source.MovementTacticalRepositionMinDeltaMeters,
            MovementTacticalBubbleMeters = source.MovementTacticalBubbleMeters,
            MovementSoftCorrectionMeters = source.MovementSoftCorrectionMeters,
            MovementHardCorrectionMeters = source.MovementHardCorrectionMeters,
            MovementCombatCohesionForcedCatchupMeters = source.MovementCombatCohesionForcedCatchupMeters,
            MovementTravelCatchUpEnterMeters = source.MovementTravelCatchUpEnterMeters,
            MovementTravelCatchUpExitMeters = source.MovementTravelCatchUpExitMeters,
            MovementTravelModeDwellSeconds = source.MovementTravelModeDwellSeconds,
            MovementActionRallyClearMeters = source.MovementActionRallyClearMeters,
            MovementActionRallyAcceptMeters = source.MovementActionRallyAcceptMeters,
            MovementActionRallyPreferredMeters = source.MovementActionRallyPreferredMeters,
            MovementLeaseStartCooldownSeconds = source.MovementLeaseStartCooldownSeconds,
            MovementLeaseFailureCooldownSeconds = source.MovementLeaseFailureCooldownSeconds,
            MovementLeaseNoProgressSeconds = source.MovementLeaseNoProgressSeconds,
            MovementLeaseMaxDurationSeconds = source.MovementLeaseMaxDurationSeconds,
            MovementActionRallyMaxReanchors = source.MovementActionRallyMaxReanchors,
            MovementOpportunisticLootBrokerEnabled = source.MovementOpportunisticLootBrokerEnabled,
            MovementOpportunisticLootMaxDistanceMeters = source.MovementOpportunisticLootMaxDistanceMeters,
            MovementOpportunisticLootScanCooldownSeconds = source.MovementOpportunisticLootScanCooldownSeconds,
            MovementOpportunisticLootGrantSeconds = source.MovementOpportunisticLootGrantSeconds,
            TacticalCoordinationDangerCloseWeaponMeters = source.TacticalCoordinationDangerCloseWeaponMeters,
            TacticalCoordinationProactiveWeaponMeters = source.TacticalCoordinationProactiveWeaponMeters,
            TacticalCoordinationOverwatchMinimumThreatMeters = source.TacticalCoordinationOverwatchMinimumThreatMeters,
            TacticalCoordinationOverwatchStandoffMeters = source.TacticalCoordinationOverwatchStandoffMeters,
            TacticalCoordinationBuddyIsolationMeters = source.TacticalCoordinationBuddyIsolationMeters,
            TacticalCoordinationBuddyHelperMaxMeters = source.TacticalCoordinationBuddyHelperMaxMeters,
            TacticalCoordinationFlankLeadCloseMeters = source.TacticalCoordinationFlankLeadCloseMeters,
            TacticalCoordinationFlankLeadFarMeters = source.TacticalCoordinationFlankLeadFarMeters,
            TacticalCoordinationBoundAdvanceDistanceMeters = source.TacticalCoordinationBoundAdvanceDistanceMeters,
            TacticalSearchShadowComparisonEnabled = source.TacticalSearchShadowComparisonEnabled,
            TacticalSearchShadowOwnerLimitMeters = source.TacticalSearchShadowOwnerLimitMeters,
            TacticalSearchShadowRequireOwnerDistanceImprovement = source.TacticalSearchShadowRequireOwnerDistanceImprovement,
            TacticalSearchShadowRetreatStepMeters = source.TacticalSearchShadowRetreatStepMeters,
            TacticalSearchShadowLateralOffsetMeters = source.TacticalSearchShadowLateralOffsetMeters,
            TacticalSearchShadowMaxPathMeters = source.TacticalSearchShadowMaxPathMeters,
            LootOperationalSessionEnabled = source.LootOperationalSessionEnabled,
            LootBackupLongWeaponEnabled = source.LootBackupLongWeaponEnabled,
            LootBackupPistolEnabled = source.LootBackupPistolEnabled,
            LootMedicalItemsEnabled = source.LootMedicalItemsEnabled,
            LootCompatibleMagazinesEnabled = source.LootCompatibleMagazinesEnabled,
            LootCompatibleLooseAmmunitionEnabled = source.LootCompatibleLooseAmmunitionEnabled,
            LootGrenadesEnabled = source.LootGrenadesEnabled,
            LootMaximumTransactionsPerCorpse = source.LootMaximumTransactionsPerCorpse,
            LootMaximumSessionSeconds = source.LootMaximumSessionSeconds,
            LootMaximumMedicalItemsPerSession = source.LootMaximumMedicalItemsPerSession,
            LootMaximumMagazinesPerSession = source.LootMaximumMagazinesPerSession,
            LootMaximumLooseAmmunitionRoundsPerSession = source.LootMaximumLooseAmmunitionRoundsPerSession,
            LootMaximumWeaponsPerSession = source.LootMaximumWeaponsPerSession,
            UpdatedByProfileId = source.UpdatedByProfileId,
            UpdatedBySource = source.UpdatedBySource,
            UpdatedAtUtc = source.UpdatedAtUtc,
            BuildLabel = source.BuildLabel,
            RaidAuthorityProfileId = source.RaidAuthorityProfileId,
            PlayerScopedSource = source.PlayerScopedSource,
            RaidScopedSource = source.RaidScopedSource,
            GovernanceVersion = source.GovernanceVersion
        };
    }

    private static bool IsServerDefaultSource(string? source)
    {
        return !string.IsNullOrWhiteSpace(source)
            && source.Trim().StartsWith("server_default_", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeOwner(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
}
#endif
