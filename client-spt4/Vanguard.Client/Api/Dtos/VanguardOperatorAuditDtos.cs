using System;

// Responsibility: Defines client/server transport DTOs used by the client/server API contracts.
// Flow: API responses and requests are normalized into these data-only shapes before being consumed by higher-level client logic.
// Authority boundary: Transport data only; server persistence and in-raid runtime services remain authoritative for the represented state.
// Invariant: DTOs remain serialization-safe, side-effect free, and tolerant of compatible server data.
namespace Vanguard.Client.Api.Dtos;

internal sealed class VanguardOperatorRuntimeAuditSettingsDto
{
    public string AuditLevel { get; set; } = "Operational";
    public string CombatDiagnosticsScope { get; set; } = "Off";
    public string PerformanceTelemetry { get; set; } = "SlowCallsOnly";
    public bool DetailedDiagnosticPayloads { get; set; }
    public bool Enabled { get; set; }
    public bool MovementProbeEnabled { get; set; }
    public bool BrainProbeEnabled { get; set; }
    public bool SainProbeEnabled { get; set; }
    public bool LootingBotsProbeEnabled { get; set; }
    public bool OrbitProbeEnabled { get; set; }
    public bool SummaryLogEnabled { get; set; }
    public bool DecisionSnapshotLogEnabled { get; set; }
    public bool IntentDryRunEnabled { get; set; }
    public bool ThreatScannerDryRunEnabled { get; set; }
    public bool FirstActiveMobileMedicalLeaseEnabled { get; set; } = true;
    public bool OperatorPostRaidPersistenceEnabled { get; set; } = true;
    public bool HeadlessKeepGcEnabledInRaid { get; set; }
    public bool VerboseTransitionLogEnabled { get; set; }
    public float SnapshotIntervalSeconds { get; set; }
    public float SummaryIntervalSeconds { get; set; }
    public float TransitionLogMinIntervalSeconds { get; set; }
    public float ThreatScannerIntervalSeconds { get; set; }
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
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string? BuildLabel { get; set; }
    public string? RaidAuthorityProfileId { get; set; }
    public string? PlayerScopedSource { get; set; }
    public string? RaidScopedSource { get; set; }
    public string GovernanceVersion { get; set; } = "Vanguard persistence/convergence path";
}

internal sealed class VanguardOperatorRuntimeAuditSettingsRequestDto
{
    public string? OwnerProfileId { get; set; }
    public string AuditLevel { get; set; } = "Operational";
    public string CombatDiagnosticsScope { get; set; } = "Off";
    public string PerformanceTelemetry { get; set; } = "SlowCallsOnly";
    public bool DetailedDiagnosticPayloads { get; set; }
    public bool Enabled { get; set; }
    public bool MovementProbeEnabled { get; set; }
    public bool BrainProbeEnabled { get; set; }
    public bool SainProbeEnabled { get; set; }
    public bool LootingBotsProbeEnabled { get; set; }
    public bool OrbitProbeEnabled { get; set; }
    public bool SummaryLogEnabled { get; set; }
    public bool DecisionSnapshotLogEnabled { get; set; }
    public bool IntentDryRunEnabled { get; set; }
    public bool ThreatScannerDryRunEnabled { get; set; }
    public bool FirstActiveMobileMedicalLeaseEnabled { get; set; } = true;
    public bool OperatorPostRaidPersistenceEnabled { get; set; } = true;
    public bool HeadlessKeepGcEnabledInRaid { get; set; }
    public bool VerboseTransitionLogEnabled { get; set; }
    public float SnapshotIntervalSeconds { get; set; }
    public float SummaryIntervalSeconds { get; set; }
    public float TransitionLogMinIntervalSeconds { get; set; }
    public float ThreatScannerIntervalSeconds { get; set; }
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
    public string? Source { get; set; }
    public string? ClientBuild { get; set; }
    public string? ClientLabel { get; set; }
    public bool RequesterIsFikaInstalled { get; set; }
    public bool RequesterIsActualHeadlessProcess { get; set; }
    public bool RequesterIsHeadlessRequester { get; set; }
    public bool RequesterIsHost { get; set; }
    public bool RequesterRaidHostedByHeadless { get; set; }
}

internal sealed class VanguardOperatorRuntimeAuditSettingsGetRequestDto
{
    public string? OwnerProfileId { get; set; }
    public string? Source { get; set; }
    public string? ClientBuild { get; set; }
    public string? ClientLabel { get; set; }
    public bool RequesterIsFikaInstalled { get; set; }
    public bool RequesterIsActualHeadlessProcess { get; set; }
    public bool RequesterIsHeadlessRequester { get; set; }
    public bool RequesterIsHost { get; set; }
    public bool RequesterRaidHostedByHeadless { get; set; }
}

internal sealed class VanguardOperatorRuntimeAuditSettingsResponseDto
{
    public bool Success { get; set; }
    public string? Reason { get; set; }
    public VanguardOperatorRuntimeAuditSettingsDto? Settings { get; set; }
}
