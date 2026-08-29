// Responsibility: Defines data/state contracts used by the server runtime-audit settings, centered on Operator Runtime Audit Settings.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Server.Operators.Audit.Models;

public sealed class VanguardOperatorRuntimeAuditSettings
{
    public string AuditLevel { get; set; } = "Operational";
    public string CombatDiagnosticsScope { get; set; } = "Off";
    public string PerformanceTelemetry { get; set; } = "SlowCallsOnly";
    public bool DetailedDiagnosticPayloads { get; set; }
    public bool Enabled { get; set; }
    public bool MovementProbeEnabled { get; set; } = true;
    public bool BrainProbeEnabled { get; set; } = true;
    public bool SainProbeEnabled { get; set; } = true;
    public bool LootingBotsProbeEnabled { get; set; } = true;
    public bool OrbitProbeEnabled { get; set; } = true;
    public bool SummaryLogEnabled { get; set; } = true;
    public bool DecisionSnapshotLogEnabled { get; set; } = true;
    public bool IntentDryRunEnabled { get; set; } = true;
    public bool ThreatScannerDryRunEnabled { get; set; } = true;
    public bool FirstActiveMobileMedicalLeaseEnabled { get; set; } = true;
    public bool OperatorPostRaidPersistenceEnabled { get; set; } = true;
    public bool HeadlessKeepGcEnabledInRaid { get; set; }
    public bool VerboseTransitionLogEnabled { get; set; }
    public float SnapshotIntervalSeconds { get; set; } = 1.0f;
    public float SummaryIntervalSeconds { get; set; } = 60.0f;
    public float TransitionLogMinIntervalSeconds { get; set; } = 2.0f;
    public float ThreatScannerIntervalSeconds { get; set; } = 1.0f;
    public bool MovementOutsideBubbleRecallEnabled { get; set; } = true;
    public bool MovementSainBoundaryReturnEnabled { get; set; } = true;
    public bool MovementSuppressExternalDuringRecallEnabled { get; set; } = true;
    public bool MovementVerboseDoctrineLogEnabled { get; set; }
    public bool MovementTacticalRepositionEnabled { get; set; } = true;
    public float MovementTacticalRepositionCooldownSeconds { get; set; } = 8.0f;
    public float MovementTacticalRepositionMinDeltaMeters { get; set; } = 7.0f;
    public float MovementTacticalBubbleMeters { get; set; } = 75.0f;
    public float MovementSoftCorrectionMeters { get; set; } = 80.0f;
    public float MovementHardCorrectionMeters { get; set; } = 88.0f;
    public float MovementCombatCohesionForcedCatchupMeters { get; set; } = 32.0f;
    public float MovementTravelCatchUpEnterMeters { get; set; } = 28.0f;
    public float MovementTravelCatchUpExitMeters { get; set; } = 22.0f;
    public float MovementTravelModeDwellSeconds { get; set; } = 1.25f;
    public float MovementActionRallyClearMeters { get; set; } = 38.0f;
    public float MovementActionRallyAcceptMeters { get; set; } = 45.0f;
    public float MovementActionRallyPreferredMeters { get; set; } = 24.0f;
    public float MovementLeaseStartCooldownSeconds { get; set; } = 10.0f;
    public float MovementLeaseFailureCooldownSeconds { get; set; } = 12.0f;
    public float MovementLeaseNoProgressSeconds { get; set; } = 7.0f;
    public float MovementLeaseMaxDurationSeconds { get; set; } = 45.0f;
    public int MovementActionRallyMaxReanchors { get; set; } = 2;
    public bool MovementOpportunisticLootBrokerEnabled { get; set; } = true;
    public float MovementOpportunisticLootMaxDistanceMeters { get; set; } = 38.0f;
    public float MovementOpportunisticLootScanCooldownSeconds { get; set; } = 18.0f;
    public float MovementOpportunisticLootGrantSeconds { get; set; } = 8.0f;
    public bool LootOperationalSessionEnabled { get; set; } = true;
    public bool LootBackupLongWeaponEnabled { get; set; } = true;
    public bool LootBackupPistolEnabled { get; set; } = true;
    public bool LootMedicalItemsEnabled { get; set; } = true;
    public bool LootCompatibleMagazinesEnabled { get; set; } = true;
    public bool LootCompatibleLooseAmmunitionEnabled { get; set; } = true;
    public bool LootGrenadesEnabled { get; set; } = true;
    public int LootMaximumTransactionsPerCorpse { get; set; } = 8;
    public float LootMaximumSessionSeconds { get; set; } = 10.0f;
    public int LootMaximumMedicalItemsPerSession { get; set; } = 4;
    public int LootMaximumMagazinesPerSession { get; set; } = 4;
    public int LootMaximumLooseAmmunitionRoundsPerSession { get; set; } = 180;
    public int LootMaximumWeaponsPerSession { get; set; } = 1;
    public string? UpdatedByProfileId { get; set; }
    public string? UpdatedBySource { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? BuildLabel { get; set; } = VanguardBuildVersion.BuildLabel;
    public string? RaidAuthorityProfileId { get; set; }
    public string? PlayerScopedSource { get; set; }
    public string? RaidScopedSource { get; set; }
    public string GovernanceVersion { get; set; } = "Vanguard persistence/convergence path";
}
