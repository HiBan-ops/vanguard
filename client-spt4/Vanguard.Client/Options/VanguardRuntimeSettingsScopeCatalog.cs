#if SPT_CLIENT

// Responsibility: Defines the user/configuration surface for Runtime Settings Scope Catalog in the F12/runtime options.
// Flow: BepInEx/F12 values are bound, normalized and exposed through getters/snapshots; raid-scoped settings are synchronized to the process that owns runtime execution.
// Authority boundary: Configuration supplies policy inputs only; changing a value does not itself perform gameplay or persistence mutation.
// Invariant: Defaults preserve the established public behavior and synchronized values remain bounded to their declared scope.
namespace Vanguard.Client.Options;

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
        "OperatorPostRaidPersistenceEnabled",
        "HeadlessKeepGcEnabledInRaid",
        "VerboseTransitionLogEnabled",
        "SnapshotIntervalSeconds",
        "SummaryIntervalSeconds",
        "TransitionLogMinIntervalSeconds",
        "ThreatScannerIntervalSeconds",
        "MovementVerboseDoctrineLogEnabled"
    ];

    public static readonly string[] PlayerScoped =
    [
        "FirstActiveMobileMedicalLeaseEnabled",
        "MovementOutsideBubbleRecallEnabled",
        "MovementSainBoundaryReturnEnabled",
        "MovementSuppressExternalDuringRecallEnabled",
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
        "MovementActionRallyMaxReanchors",
        "MovementOpportunisticLootBrokerEnabled",
        "MovementOpportunisticLootMaxDistanceMeters",
        "MovementOpportunisticLootScanCooldownSeconds",
        "MovementOpportunisticLootGrantSeconds",
        "TacticalCoordinationDangerCloseWeaponMeters",
        "TacticalCoordinationProactiveWeaponMeters",
        "TacticalCoordinationOverwatchMinimumThreatMeters",
        "TacticalCoordinationOverwatchStandoffMeters",
        "TacticalCoordinationBuddyIsolationMeters",
        "TacticalCoordinationBuddyHelperMaxMeters",
        "TacticalCoordinationFlankLeadCloseMeters",
        "TacticalCoordinationFlankLeadFarMeters",
        "TacticalCoordinationBoundAdvanceDistanceMeters",
        "TacticalSearchShadowComparisonEnabled",
        "TacticalSearchShadowOwnerLimitMeters",
        "TacticalSearchShadowRequireOwnerDistanceImprovement",
        "TacticalSearchShadowRetreatStepMeters",
        "TacticalSearchShadowLateralOffsetMeters",
        "TacticalSearchShadowMaxPathMeters",
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
    ];
}
#endif
