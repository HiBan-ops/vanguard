#if SPT_CLIENT

// Responsibility: Defines the user/configuration surface for Runtime Settings Scope Catalog in the F12/runtime options.
// Flow: BepInEx/F12 values are bound, normalized and exposed through getters/snapshots; raid-scoped settings are synchronized to the process that owns runtime execution.
// Authority boundary: Configuration supplies policy inputs only; changing a value does not itself perform gameplay or persistence mutation.
// Invariant: Defaults preserve the published 0.7.0 behavior and synchronized values remain bounded to their declared scope.
namespace Vanguard.Client.Options;

internal enum VanguardRuntimeSettingScope
{
    Local = 0,
    PlayerScoped = 1,
    RaidScoped = 2,
    LegacyUnused = 3
}

/// <summary>
/// Authoritative F12 governance inventory. Tactical Authoring options stay in their dedicated
/// local-only transport and are intentionally excluded from the runtime-audit DTO.
/// </summary>
internal static class VanguardRuntimeSettingsScopeCatalog
{
    public const string GovernanceVersion = "Vanguard persistence/convergence path";
    public const string TacticalAuthoringScope = "LOCAL_SEPARATE_TRANSPORT";

    public static readonly string[] RaidScoped =
    [
        "AuditLevel",
        "CombatDiagnosticsScope",
        "PerformanceTelemetry",
        "DetailedDiagnosticPayloads",
        "Enabled",
        "MovementProbeEnabled",
        "BrainProbeEnabled",
        "SainProbeEnabled",
        "LootingBotsProbeEnabled",
        "OrbitProbeEnabled",
        "SummaryLogEnabled",
        "DecisionSnapshotLogEnabled",
        "IntentDryRunEnabled",
        "ThreatScannerDryRunEnabled",
        "FirstActiveMobileMedicalLeaseEnabled",
        "OperatorPostRaidPersistenceEnabled",
        "HeadlessKeepGcEnabledInRaid",
        "VerboseTransitionLogEnabled",
        "SnapshotIntervalSeconds",
        "SummaryIntervalSeconds",
        "TransitionLogMinIntervalSeconds",
        "ThreatScannerIntervalSeconds",
        "MovementOutsideBubbleRecallEnabled",
        "MovementSainBoundaryReturnEnabled",
        "MovementSuppressExternalDuringRecallEnabled",
        "MovementVerboseDoctrineLogEnabled",
        "MovementTacticalRepositionEnabled",
        "MovementTacticalRepositionCooldownSeconds",
        "MovementTacticalRepositionMinDeltaMeters",
        "MovementTacticalBubbleMeters",
        "MovementSoftCorrectionMeters",
        "MovementHardCorrectionMeters",
        "MovementCombatCohesionForcedCatchupMeters",
        "MovementTravelCatchUpEnterMeters",
        "MovementTravelCatchUpExitMeters",
        "MovementTravelModeDwellSeconds",
        "MovementActionRallyClearMeters",
        "MovementActionRallyAcceptMeters",
        "MovementActionRallyPreferredMeters",
        "MovementLeaseStartCooldownSeconds",
        "MovementLeaseFailureCooldownSeconds",
        "MovementLeaseNoProgressSeconds",
        "MovementLeaseMaxDurationSeconds",
        "MovementActionRallyMaxReanchors"
    ];

    public static readonly string[] PlayerScoped =
    [
        "MovementOpportunisticLootBrokerEnabled",
        "MovementOpportunisticLootMaxDistanceMeters",
        "LootOperationalSessionEnabled",
        "LootBackupLongWeaponEnabled",
        "LootBackupPistolEnabled",
        "LootMedicalItemsEnabled",
        "LootCompatibleMagazinesEnabled",
        "LootCompatibleLooseAmmunitionEnabled",
        "LootGrenadesEnabled",
        "LootMaximumTransactionsPerCorpse",
        "LootMaximumSessionSeconds",
        "LootMaximumMedicalItemsPerSession",
        "LootMaximumMagazinesPerSession",
        "LootMaximumLooseAmmunitionRoundsPerSession",
        "LootMaximumWeaponsPerSession"
    ];

    public static readonly string[] LegacyUnused =
    [
        "MovementOpportunisticLootScanCooldownSeconds",
        "MovementOpportunisticLootGrantSeconds"
    ];
}
#endif
