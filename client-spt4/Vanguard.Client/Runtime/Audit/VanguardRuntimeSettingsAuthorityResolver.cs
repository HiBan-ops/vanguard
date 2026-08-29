#if SPT_CLIENT
using System;
using System.Collections.Generic;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;

// Responsibility: Provides Runtime Settings Authority Resolver support for the runtime audit.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Audit;

/// <summary>
/// The persistence path owner-keyed cache for PLAYER_SCOPED runtime settings. The headless never projects one
/// player's values into global ConfigEntry instances; future gameplay consumers resolve by
/// OwnerProfileId. The persistence path migrates the opportunistic corpse-loot family to this resolver; legacy
/// static getters remain local fallbacks for unrelated gameplay families until separately reviewed.
/// </summary>
internal static class VanguardRuntimeSettingsAuthorityResolver
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, VanguardOperatorRuntimeAuditSettingsDto> PlayerSettingsByOwner = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedOwners = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedFallbackOwners = new(StringComparer.Ordinal);

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            PlayerSettingsByOwner.Clear();
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

        lock (Sync)
        {
            PlayerSettingsByOwner[owner] = Clone(settings);
            if (!LoggedOwners.Add(owner))
            {
                return;
            }
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.F12AuthorityConvergenceStatusTag,
            $"VANGUARD_F12_PLAYER_SCOPE_APPLIED owner={Safe(owner)}; source={Safe(source)}; playerSource={Safe(settings.PlayerScopedSource)}; lootRadius={settings.MovementOpportunisticLootMaxDistanceMeters:0.0}; lootSession={settings.LootOperationalSessionEnabled}; lootTx={settings.LootMaximumTransactionsPerCorpse}; governance={Safe(settings.GovernanceVersion)}");
    }

    public static bool TryGetPlayerScoped(string? ownerProfileId, out VanguardOperatorRuntimeAuditSettingsDto settings)
    {
        string owner = NormalizeOwner(ownerProfileId);
        lock (Sync)
        {
            if (owner != "none" && PlayerSettingsByOwner.TryGetValue(owner, out VanguardOperatorRuntimeAuditSettingsDto? cached))
            {
                // Cache entries are cloned on write and treated as immutable afterwards. Returning the
                // stable reference avoids allocating a 60+ field DTO during each loot evaluation.
                settings = cached;
                return true;
            }
        }

        settings = new VanguardOperatorRuntimeAuditSettingsDto();
        return false;
    }

    public static VanguardOperatorRuntimeAuditSettingsDto ResolvePlayerScoped(string? ownerProfileId, string source)
    {
        if (TryGetPlayerScoped(ownerProfileId, out VanguardOperatorRuntimeAuditSettingsDto cached))
        {
            return cached;
        }

        // A direct player host can safely use its own local F12 values immediately. A dedicated
        // headless cannot: its local ConfigEntry values do not belong to any player OwnerProfileId.
        // Fail closed for opportunistic loot until the first owner pull completes rather than risk
        // cross-owner behavior during bootstrap or a transient server-sync failure.
        string owner = NormalizeOwner(ownerProfileId);
        bool actualHeadless = VanguardFikaCompat.IsActualHeadlessProcess;
        bool logFallback;
        lock (Sync)
        {
            logFallback = LoggedFallbackOwners.Add(owner + "|" + source);
        }

        VanguardOperatorRuntimeAuditSettingsDto fallback = VanguardOperatorRuntimeAuditOptions.CurrentLocalSettings(ownerProfileId, source);
        if (actualHeadless)
        {
            fallback.MovementOpportunisticLootBrokerEnabled = false;
            fallback.LootOperationalSessionEnabled = false;
        }

        if (logFallback)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.F12AuthorityConvergenceStatusTag,
                $"VANGUARD_F12_PLAYER_SCOPE_FALLBACK owner={Safe(owner)}; source={Safe(source)}; authority={(actualHeadless ? "headless_fail_closed_until_owner_sync" : "local_player_config")}; lootAdmission={fallback.MovementOpportunisticLootBrokerEnabled}; mutation=false");
        }
        return fallback;
    }

    public static IReadOnlyList<string> GetCachedOwnerProfileIds()
    {
        lock (Sync)
        {
            return new List<string>(PlayerSettingsByOwner.Keys);
        }
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
