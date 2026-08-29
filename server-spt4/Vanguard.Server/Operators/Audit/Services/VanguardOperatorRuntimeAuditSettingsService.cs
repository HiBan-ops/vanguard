using SPTarkov.DI.Annotations;
using Vanguard.Server.Operators.Audit.Models;
using Vanguard.Server.Operators.Audit.Requests;
using Vanguard.Server.Operators.Audit.Responses;

// Responsibility: Stores and serves owner-scoped runtime/F12 settings exchanged between player clients and the Headless/direct-host execution authority.
// Flow: Incoming settings are schema/scope validated, normalized into the owning profile record with revision/hash metadata, and read back by the runtime authority using owner identity.
// Authority boundary: The server is transport/persistence authority for synchronized settings; it does not interpret them as gameplay commands and client-local presentation values remain outside this path.
// Invariant: Writes are owner-scoped and revision-safe, unknown/out-of-scope fields cannot acquire authority, and missing settings fall back safely instead of borrowing another player's values.
namespace Vanguard.Server.Operators.Audit.Services;

/// <summary>
/// Vanguard persistence/convergence path F12 governance authority.
/// PLAYER_SCOPED loot doctrine is stored strictly per OwnerProfileId.
/// RAID_SCOPED diagnostics, general movement doctrine and the medical lease switch are writable only
/// by the player raid authority and are merged into reads.
/// No latest-client promotion or cross-owner bootstrap is permitted.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorRuntimeAuditSettingsService
{
    private const string GovernanceVersion = "Vanguard persistence/convergence path";

    private readonly object sync = new();
    private readonly Dictionary<string, VanguardOperatorRuntimeAuditSettings> settingsByOwnerProfileId = new(StringComparer.Ordinal);
    private VanguardOperatorRuntimeAuditSettings? raidScopedSettings;
    private string? raidAuthorityProfileId;

    public VanguardOperatorRuntimeAuditSettingsResponse Get(string? requestedByProfileId = null, VanguardOperatorRuntimeAuditSettingsGetRequest? request = null)
    {
        string ownerProfileId = NormalizeProfileId(request?.OwnerProfileId, requestedByProfileId);
        lock (sync)
        {
            VanguardOperatorRuntimeAuditSettings ownerSettings = GetOrCreateOwnerSettings(ownerProfileId);
            return Response(MergeForResponse(ownerSettings, ownerProfileId), "runtime_audit_config_owner_scoped_loaded");
        }
    }

    public VanguardOperatorRuntimeAuditSettingsResponse Set(VanguardOperatorRuntimeAuditSettingsRequest? request, string? requestedByProfileId)
    {
        var safeRequest = request ?? new VanguardOperatorRuntimeAuditSettingsRequest();
        string ownerProfileId = NormalizeProfileId(safeRequest.OwnerProfileId ?? safeRequest.UpdatedByProfileId, requestedByProfileId);
        string source = NormalizeSource(safeRequest.Source ?? safeRequest.UpdatedBySource);
        bool clientCommand = IsClientCommandSource(source);
        bool compatibleClientBuild = IsCompatibleClientBuild(safeRequest.ClientLabel, safeRequest.ClientBuild);

        lock (sync)
        {
            VanguardOperatorRuntimeAuditSettings currentOwner = GetOrCreateOwnerSettings(ownerProfileId);
            if (clientCommand && !compatibleClientBuild)
            {
                return Response(
                    MergeForResponse(currentOwner, ownerProfileId),
                    "runtime_audit_config_build_mismatch_rejected_no_authority_mutation");
            }

            VanguardOperatorRuntimeAuditSettings incoming = BuildIncoming(safeRequest, ownerProfileId, source);
            VanguardOperatorRuntimeAuditSettings nextOwner = Clone(currentOwner);
            CopyPlayerScoped(incoming, nextOwner);
            nextOwner.UpdatedByProfileId = ownerProfileId;
            nextOwner.UpdatedBySource = source;
            nextOwner.PlayerScopedSource = source;
            nextOwner.UpdatedAtUtc = DateTimeOffset.UtcNow;
            nextOwner.BuildLabel = VanguardBuildVersion.BuildLabel;
            nextOwner.GovernanceVersion = GovernanceVersion;
            settingsByOwnerProfileId[ownerProfileId] = Clone(nextOwner);

            bool raidScopeAccepted = CanWriteRaidScoped(safeRequest);
            if (raidScopeAccepted)
            {
                raidAuthorityProfileId = ownerProfileId;
                raidScopedSettings ??= CreateDefault(ownerProfileId, "server_default_raid_scope");
                CopyRaidScoped(incoming, raidScopedSettings);
                raidScopedSettings.RaidAuthorityProfileId = ownerProfileId;
                raidScopedSettings.RaidScopedSource = source;
                raidScopedSettings.UpdatedAtUtc = DateTimeOffset.UtcNow;
                raidScopedSettings.BuildLabel = VanguardBuildVersion.BuildLabel;
                raidScopedSettings.GovernanceVersion = GovernanceVersion;
            }

            string reason = raidScopeAccepted
                ? "runtime_audit_config_player_and_raid_scopes_set"
                : "runtime_audit_config_player_scope_set_raid_scope_ignored_non_authority";
            return Response(MergeForResponse(nextOwner, ownerProfileId), reason);
        }
    }

    private VanguardOperatorRuntimeAuditSettings GetOrCreateOwnerSettings(string ownerProfileId)
    {
        if (settingsByOwnerProfileId.TryGetValue(ownerProfileId, out VanguardOperatorRuntimeAuditSettings? existing))
        {
            return existing;
        }

        VanguardOperatorRuntimeAuditSettings defaults = CreateDefault(ownerProfileId, "server_default_owner_scope");
        settingsByOwnerProfileId[ownerProfileId] = Clone(defaults);
        return settingsByOwnerProfileId[ownerProfileId];
    }

    private VanguardOperatorRuntimeAuditSettings MergeForResponse(VanguardOperatorRuntimeAuditSettings ownerSettings, string ownerProfileId)
    {
        VanguardOperatorRuntimeAuditSettings merged = Clone(ownerSettings);
        VanguardOperatorRuntimeAuditSettings raid = raidScopedSettings ?? CreateDefault(ownerProfileId, "server_default_no_raid_authority");
        CopyRaidScoped(raid, merged);
        merged.UpdatedByProfileId = ownerProfileId;
        merged.UpdatedBySource = ownerSettings.PlayerScopedSource ?? ownerSettings.UpdatedBySource;
        merged.PlayerScopedSource = ownerSettings.PlayerScopedSource ?? ownerSettings.UpdatedBySource ?? "server_default_owner_scope";
        merged.RaidAuthorityProfileId = raidAuthorityProfileId;
        merged.RaidScopedSource = raidScopedSettings?.RaidScopedSource ?? "server_default_no_raid_authority";
        merged.GovernanceVersion = GovernanceVersion;
        merged.BuildLabel = VanguardBuildVersion.BuildLabel;
        return merged;
    }

    private static VanguardOperatorRuntimeAuditSettings BuildIncoming(VanguardOperatorRuntimeAuditSettingsRequest request, string ownerProfileId, string source)
    {
        var settings = new VanguardOperatorRuntimeAuditSettings
        {
            AuditLevel = NormalizeAuditLevel(request.AuditLevel),
            CombatDiagnosticsScope = NormalizeCombatDiagnosticsScope(request.CombatDiagnosticsScope),
            PerformanceTelemetry = NormalizePerformanceTelemetry(request.PerformanceTelemetry),
            DetailedDiagnosticPayloads = request.DetailedDiagnosticPayloads,
            Enabled = request.Enabled,
            MovementProbeEnabled = request.MovementProbeEnabled,
            BrainProbeEnabled = request.BrainProbeEnabled,
            SainProbeEnabled = request.SainProbeEnabled,
            LootingBotsProbeEnabled = request.LootingBotsProbeEnabled,
            OrbitProbeEnabled = request.OrbitProbeEnabled,
            SummaryLogEnabled = request.SummaryLogEnabled,
            DecisionSnapshotLogEnabled = request.DecisionSnapshotLogEnabled,
            IntentDryRunEnabled = request.IntentDryRunEnabled,
            ThreatScannerDryRunEnabled = request.ThreatScannerDryRunEnabled,
            FirstActiveMobileMedicalLeaseEnabled = request.FirstActiveMobileMedicalLeaseEnabled,
            OperatorPostRaidPersistenceEnabled = request.OperatorPostRaidPersistenceEnabled,
            HeadlessKeepGcEnabledInRaid = request.HeadlessKeepGcEnabledInRaid,
            VerboseTransitionLogEnabled = request.VerboseTransitionLogEnabled,
            SnapshotIntervalSeconds = Clamp(request.SnapshotIntervalSeconds, 0.5f, 5.0f),
            SummaryIntervalSeconds = Clamp(request.SummaryIntervalSeconds, 30.0f, 120.0f),
            TransitionLogMinIntervalSeconds = Clamp(request.TransitionLogMinIntervalSeconds, 0.5f, 10.0f),
            ThreatScannerIntervalSeconds = Clamp(request.ThreatScannerIntervalSeconds, 0.5f, 3.0f),
            MovementOutsideBubbleRecallEnabled = request.MovementOutsideBubbleRecallEnabled,
            MovementSainBoundaryReturnEnabled = request.MovementSainBoundaryReturnEnabled,
            MovementSuppressExternalDuringRecallEnabled = request.MovementSuppressExternalDuringRecallEnabled,
            MovementVerboseDoctrineLogEnabled = request.MovementVerboseDoctrineLogEnabled,
            MovementTacticalRepositionEnabled = request.MovementTacticalRepositionEnabled,
            MovementTacticalRepositionCooldownSeconds = Clamp(request.MovementTacticalRepositionCooldownSeconds, 3.0f, 30.0f),
            MovementTacticalRepositionMinDeltaMeters = Clamp(request.MovementTacticalRepositionMinDeltaMeters, 3.0f, 18.0f),
            MovementTacticalBubbleMeters = Clamp(request.MovementTacticalBubbleMeters, 35.0f, 120.0f),
            MovementSoftCorrectionMeters = Clamp(request.MovementSoftCorrectionMeters, 40.0f, 135.0f),
            MovementHardCorrectionMeters = Clamp(request.MovementHardCorrectionMeters, 45.0f, 150.0f),
            MovementCombatCohesionForcedCatchupMeters = Clamp(request.MovementCombatCohesionForcedCatchupMeters, 16.0f, 80.0f),
            MovementTravelCatchUpEnterMeters = Clamp(request.MovementTravelCatchUpEnterMeters, 18.0f, 60.0f),
            MovementTravelCatchUpExitMeters = Clamp(request.MovementTravelCatchUpExitMeters, 10.0f, 55.0f),
            MovementTravelModeDwellSeconds = Clamp(request.MovementTravelModeDwellSeconds, 0.25f, 5.0f),
            MovementActionRallyClearMeters = Clamp(request.MovementActionRallyClearMeters, 12.0f, 70.0f),
            MovementActionRallyAcceptMeters = Clamp(request.MovementActionRallyAcceptMeters, 15.0f, 80.0f),
            MovementActionRallyPreferredMeters = Clamp(request.MovementActionRallyPreferredMeters, 8.0f, 55.0f),
            MovementLeaseStartCooldownSeconds = Clamp(request.MovementLeaseStartCooldownSeconds, 2.0f, 30.0f),
            MovementLeaseFailureCooldownSeconds = Clamp(request.MovementLeaseFailureCooldownSeconds, 4.0f, 45.0f),
            MovementLeaseNoProgressSeconds = Clamp(request.MovementLeaseNoProgressSeconds, 3.0f, 20.0f),
            MovementLeaseMaxDurationSeconds = Clamp(request.MovementLeaseMaxDurationSeconds, 18.0f, 90.0f),
            MovementActionRallyMaxReanchors = ClampInt(request.MovementActionRallyMaxReanchors, 0, 5),
            MovementOpportunisticLootBrokerEnabled = request.MovementOpportunisticLootBrokerEnabled,
            MovementOpportunisticLootMaxDistanceMeters = Clamp(request.MovementOpportunisticLootMaxDistanceMeters, 6.0f, 42.0f),
            MovementOpportunisticLootScanCooldownSeconds = Clamp(request.MovementOpportunisticLootScanCooldownSeconds, 8.0f, 60.0f),
            MovementOpportunisticLootGrantSeconds = Clamp(request.MovementOpportunisticLootGrantSeconds, 4.0f, 20.0f),
            LootOperationalSessionEnabled = request.LootOperationalSessionEnabled,
            LootBackupLongWeaponEnabled = request.LootBackupLongWeaponEnabled,
            LootBackupPistolEnabled = request.LootBackupPistolEnabled,
            LootMedicalItemsEnabled = request.LootMedicalItemsEnabled,
            LootCompatibleMagazinesEnabled = request.LootCompatibleMagazinesEnabled,
            LootCompatibleLooseAmmunitionEnabled = request.LootCompatibleLooseAmmunitionEnabled,
            LootGrenadesEnabled = request.LootGrenadesEnabled,
            LootMaximumTransactionsPerCorpse = ClampInt(request.LootMaximumTransactionsPerCorpse, 1, 12),
            LootMaximumSessionSeconds = Clamp(request.LootMaximumSessionSeconds, 3.0f, 20.0f),
            LootMaximumMedicalItemsPerSession = ClampInt(request.LootMaximumMedicalItemsPerSession, 0, 8),
            LootMaximumMagazinesPerSession = ClampInt(request.LootMaximumMagazinesPerSession, 0, 8),
            LootMaximumLooseAmmunitionRoundsPerSession = ClampInt(request.LootMaximumLooseAmmunitionRoundsPerSession, 0, 600),
            LootMaximumWeaponsPerSession = ClampInt(request.LootMaximumWeaponsPerSession, 0, 2),
            UpdatedByProfileId = ownerProfileId,
            UpdatedBySource = source,
            PlayerScopedSource = source,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            BuildLabel = VanguardBuildVersion.BuildLabel,
            GovernanceVersion = GovernanceVersion
        };

        NormalizeTravelModeHysteresis(settings);
        return settings;
    }

    private static VanguardOperatorRuntimeAuditSettings CreateDefault(string ownerProfileId, string source)
    {
        return new VanguardOperatorRuntimeAuditSettings
        {
            UpdatedByProfileId = ownerProfileId,
            UpdatedBySource = source,
            PlayerScopedSource = source,
            RaidScopedSource = source,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            BuildLabel = VanguardBuildVersion.BuildLabel,
            GovernanceVersion = GovernanceVersion
        };
    }

    private static void CopyRaidScoped(VanguardOperatorRuntimeAuditSettings source, VanguardOperatorRuntimeAuditSettings target)
    {
        target.AuditLevel = source.AuditLevel;
        target.CombatDiagnosticsScope = source.CombatDiagnosticsScope;
        target.PerformanceTelemetry = source.PerformanceTelemetry;
        target.DetailedDiagnosticPayloads = source.DetailedDiagnosticPayloads;
        target.Enabled = source.Enabled;
        target.MovementProbeEnabled = source.MovementProbeEnabled;
        target.BrainProbeEnabled = source.BrainProbeEnabled;
        target.SainProbeEnabled = source.SainProbeEnabled;
        target.LootingBotsProbeEnabled = source.LootingBotsProbeEnabled;
        target.OrbitProbeEnabled = source.OrbitProbeEnabled;
        target.SummaryLogEnabled = source.SummaryLogEnabled;
        target.DecisionSnapshotLogEnabled = source.DecisionSnapshotLogEnabled;
        target.IntentDryRunEnabled = source.IntentDryRunEnabled;
        target.ThreatScannerDryRunEnabled = source.ThreatScannerDryRunEnabled;
        target.FirstActiveMobileMedicalLeaseEnabled = source.FirstActiveMobileMedicalLeaseEnabled;
        target.OperatorPostRaidPersistenceEnabled = source.OperatorPostRaidPersistenceEnabled;
        target.HeadlessKeepGcEnabledInRaid = source.HeadlessKeepGcEnabledInRaid;
        target.VerboseTransitionLogEnabled = source.VerboseTransitionLogEnabled;
        target.SnapshotIntervalSeconds = source.SnapshotIntervalSeconds;
        target.SummaryIntervalSeconds = source.SummaryIntervalSeconds;
        target.TransitionLogMinIntervalSeconds = source.TransitionLogMinIntervalSeconds;
        target.ThreatScannerIntervalSeconds = source.ThreatScannerIntervalSeconds;
        target.MovementOutsideBubbleRecallEnabled = source.MovementOutsideBubbleRecallEnabled;
        target.MovementSainBoundaryReturnEnabled = source.MovementSainBoundaryReturnEnabled;
        target.MovementSuppressExternalDuringRecallEnabled = source.MovementSuppressExternalDuringRecallEnabled;
        target.MovementVerboseDoctrineLogEnabled = source.MovementVerboseDoctrineLogEnabled;
        target.MovementTacticalRepositionEnabled = source.MovementTacticalRepositionEnabled;
        target.MovementTacticalRepositionCooldownSeconds = source.MovementTacticalRepositionCooldownSeconds;
        target.MovementTacticalRepositionMinDeltaMeters = source.MovementTacticalRepositionMinDeltaMeters;
        target.MovementTacticalBubbleMeters = source.MovementTacticalBubbleMeters;
        target.MovementSoftCorrectionMeters = source.MovementSoftCorrectionMeters;
        target.MovementHardCorrectionMeters = source.MovementHardCorrectionMeters;
        target.MovementCombatCohesionForcedCatchupMeters = source.MovementCombatCohesionForcedCatchupMeters;
        target.MovementTravelCatchUpEnterMeters = source.MovementTravelCatchUpEnterMeters;
        target.MovementTravelCatchUpExitMeters = source.MovementTravelCatchUpExitMeters;
        target.MovementTravelModeDwellSeconds = source.MovementTravelModeDwellSeconds;
        target.MovementActionRallyClearMeters = source.MovementActionRallyClearMeters;
        target.MovementActionRallyAcceptMeters = source.MovementActionRallyAcceptMeters;
        target.MovementActionRallyPreferredMeters = source.MovementActionRallyPreferredMeters;
        target.MovementLeaseStartCooldownSeconds = source.MovementLeaseStartCooldownSeconds;
        target.MovementLeaseFailureCooldownSeconds = source.MovementLeaseFailureCooldownSeconds;
        target.MovementLeaseNoProgressSeconds = source.MovementLeaseNoProgressSeconds;
        target.MovementLeaseMaxDurationSeconds = source.MovementLeaseMaxDurationSeconds;
        target.MovementActionRallyMaxReanchors = source.MovementActionRallyMaxReanchors;
    }

    private static void CopyPlayerScoped(VanguardOperatorRuntimeAuditSettings source, VanguardOperatorRuntimeAuditSettings target)
    {
        target.MovementOpportunisticLootBrokerEnabled = source.MovementOpportunisticLootBrokerEnabled;
        target.MovementOpportunisticLootMaxDistanceMeters = source.MovementOpportunisticLootMaxDistanceMeters;
        target.LootOperationalSessionEnabled = source.LootOperationalSessionEnabled;
        target.LootBackupLongWeaponEnabled = source.LootBackupLongWeaponEnabled;
        target.LootBackupPistolEnabled = source.LootBackupPistolEnabled;
        target.LootMedicalItemsEnabled = source.LootMedicalItemsEnabled;
        target.LootCompatibleMagazinesEnabled = source.LootCompatibleMagazinesEnabled;
        target.LootCompatibleLooseAmmunitionEnabled = source.LootCompatibleLooseAmmunitionEnabled;
        target.LootGrenadesEnabled = source.LootGrenadesEnabled;
        target.LootMaximumTransactionsPerCorpse = source.LootMaximumTransactionsPerCorpse;
        target.LootMaximumSessionSeconds = source.LootMaximumSessionSeconds;
        target.LootMaximumMedicalItemsPerSession = source.LootMaximumMedicalItemsPerSession;
        target.LootMaximumMagazinesPerSession = source.LootMaximumMagazinesPerSession;
        target.LootMaximumLooseAmmunitionRoundsPerSession = source.LootMaximumLooseAmmunitionRoundsPerSession;
        target.LootMaximumWeaponsPerSession = source.LootMaximumWeaponsPerSession;
        // Deprecated scan/grant fields are intentionally not copied: they are non-authoritative compatibility residue.
    }

    private static VanguardOperatorRuntimeAuditSettings Clone(VanguardOperatorRuntimeAuditSettings source)
    {
        var clone = new VanguardOperatorRuntimeAuditSettings();
        CopyRaidScoped(source, clone);
        CopyPlayerScoped(source, clone);
        clone.UpdatedByProfileId = source.UpdatedByProfileId;
        clone.UpdatedBySource = source.UpdatedBySource;
        clone.UpdatedAtUtc = source.UpdatedAtUtc;
        clone.BuildLabel = source.BuildLabel;
        clone.RaidAuthorityProfileId = source.RaidAuthorityProfileId;
        clone.PlayerScopedSource = source.PlayerScopedSource;
        clone.RaidScopedSource = source.RaidScopedSource;
        clone.GovernanceVersion = source.GovernanceVersion;
        return clone;
    }

    private static VanguardOperatorRuntimeAuditSettingsResponse Response(VanguardOperatorRuntimeAuditSettings settings, string reason)
    {
        return new VanguardOperatorRuntimeAuditSettingsResponse
        {
            Success = true,
            Reason = reason,
            Settings = Clone(settings)
        };
    }

    private static bool CanWriteRaidScoped(VanguardOperatorRuntimeAuditSettingsRequest request)
    {
        if (request.RequesterIsActualHeadlessProcess)
        {
            return false;
        }

        if (!request.RequesterIsFikaInstalled)
        {
            return true;
        }

        return request.RequesterIsHeadlessRequester
            || (request.RequesterIsHost && !request.RequesterRaidHostedByHeadless);
    }

    private static void NormalizeTravelModeHysteresis(VanguardOperatorRuntimeAuditSettings settings)
    {
        float effectiveHardCorrection = Math.Max(settings.MovementSoftCorrectionMeters + 1.0f, settings.MovementHardCorrectionMeters);
        settings.MovementTravelCatchUpEnterMeters = Math.Min(
            Clamp(settings.MovementTravelCatchUpEnterMeters, 18.0f, 60.0f),
            Math.Max(18.0f, effectiveHardCorrection - 4.0f));
        settings.MovementTravelCatchUpExitMeters = Math.Min(
            Clamp(settings.MovementTravelCatchUpExitMeters, 10.0f, 55.0f),
            Math.Max(10.0f, settings.MovementTravelCatchUpEnterMeters - 2.0f));
        settings.MovementTravelModeDwellSeconds = Clamp(settings.MovementTravelModeDwellSeconds, 0.25f, 5.0f);
    }

    private static bool IsCompatibleClientBuild(string? clientLabel, string? clientBuild)
    {
        if (!string.IsNullOrWhiteSpace(clientLabel))
        {
            return string.Equals(clientLabel.Trim(), VanguardBuildVersion.BuildLabel, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(clientBuild))
        {
            return string.Equals(clientBuild.Trim(), VanguardBuildVersion.Value, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string NormalizeProfileId(string? profileId, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            return profileId.Trim();
        }

        return string.IsNullOrWhiteSpace(fallback) ? "server_session_owner" : fallback.Trim();
    }

    private static string NormalizeSource(string? source) => string.IsNullOrWhiteSpace(source) ? "client_f12" : source.Trim();

    private static bool IsClientCommandSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        return source.Contains("client", StringComparison.OrdinalIgnoreCase)
            || source.Contains("bepinex", StringComparison.OrdinalIgnoreCase)
            || source.Contains("f12", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAuditLevel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off" => "Off",
        "diagnostic" => "Diagnostic",
        "trace" => "Trace",
        _ => "Operational"
    };

    private static string NormalizeCombatDiagnosticsScope(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "operatorsonly" => "OperatorsOnly",
        "allbots" => "AllBots",
        _ => "Off"
    };

    private static string NormalizePerformanceTelemetry(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off" => "Off",
        "full" => "Full",
        _ => "SlowCallsOnly"
    };

    private static float Clamp(float value, float min, float max)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return min;
        }

        return Math.Max(min, Math.Min(max, value));
    }

    private static int ClampInt(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
}
