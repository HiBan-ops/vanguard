using System;
using System.Collections.Generic;

// Responsibility: Defines the primary client-side DTO graph for persistent Operator identity, contracts, service state, medical state, billing and Off-Raid projections.
// Flow: Server JSON is deserialized into these transport shapes and then converted by UI/runtime foundation code into canonical client views.
// Authority boundary: DTOs carry data only; server services and stores remain authoritative for the represented Operator state.
// Invariant: Defaults tolerate compatible/missing fields, serialization stays side-effect free, and no DTO is treated as independent persistence authority.
namespace Vanguard.Client.Api.Dtos;

internal sealed class VanguardOperatorStateResponseDto
{
    public string? RequestedProfileId { get; set; }
    public string? StorageProfileId { get; set; }
    public VanguardOperatorDeploymentLimitsDto? Limits { get; set; }
    public List<VanguardOperatorProfileDto>? Operators { get; set; }
    public List<VanguardActiveServiceRecordDto>? ActiveService { get; set; }
    public List<VanguardOperatorContractOfferDto>? Contracts { get; set; }
    public List<VanguardOperatorContactRecordDto>? Contacts { get; set; }
    public List<VanguardOperatorMedicalRecordDto>? MedicalRecords { get; set; }
    public List<VanguardOperatorServiceProjectionDto>? ServiceProjections { get; set; }
    public List<VanguardOperatorMedicalProjectionDto>? MedicalProjections { get; set; }
    public List<VanguardOperatorRaidProjectionDto>? RaidProjections { get; set; }
    public VanguardCareerProjectionReadModelDto? CareerProjection { get; set; }
    public VanguardCanonicalRaidHistoryReadModelDto? CanonicalRaidHistory { get; set; }
    public VanguardOperatorBillingSnapshotDto? Billing { get; set; }
    public VanguardOperatorStateMetadataDto? Metadata { get; set; }
}

internal sealed class VanguardOperatorDeploymentLimitsDto
{
    public static readonly VanguardOperatorDeploymentLimitsDto Empty = new();

    public int PlayerLevel { get; set; }
    public int MaxHiredOperators { get; set; }
    public int MaxDeployableOperators { get; set; }
    public string? Tier { get; set; }
}

internal sealed class VanguardOperatorStateMetadataDto
{
    public static readonly VanguardOperatorStateMetadataDto Empty = new();

    public int OperatorCount { get; set; }
    public int ActiveServiceCount { get; set; }
    public int ContractOfferCount { get; set; }
    public int MedicalRecordCount { get; set; }
    public int ContactCount { get; set; }
    public DateTimeOffset? GeneratedAtUtc { get; set; }
    public string? StorageVersion { get; set; }
    public string? BuildLabel { get; set; }
}

internal sealed class VanguardOperatorProfileDto
{
    public string? OperatorId { get; set; }
    public VanguardOperatorIdentityDto? Identity { get; set; }
    public string? Role { get; set; }
    public string? Specialty { get; set; }
    public string? ContractStatus { get; set; }
    public string? ServiceStatus { get; set; }
    public int SalaryPerRaid { get; set; }
    public int HirePrice { get; set; }
    public string? CurrencyTpl { get; set; }
    public VanguardOperatorPersonaDto? Persona { get; set; }
    public VanguardOperatorProgressionDto? Progression { get; set; }
    public VanguardOperatorCareerDto? Career { get; set; }
    public DateTimeOffset? CreatedAtUtc { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
    public string? LootTargetPolicy { get; set; }
}

internal sealed class VanguardOperatorIdentityDto
{
    public string? OperatorId { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Callsign { get; set; }
    public string? DisplayName { get; set; }
    public string? Side { get; set; }
    public string? NameCulture { get; set; }
    public string? VisualFamily { get; set; }
}

internal sealed class VanguardOperatorPersonaDto
{
    public string? BasePersona { get; set; }
    public string? Doctrine { get; set; }
    public string? Temperament { get; set; }
    public string? SainProfileFamily { get; set; }
    public string? SainTuningPlan { get; set; }
    public List<string>? Traits { get; set; }
    public string? BehaviorSummary { get; set; }
    public string? CombatStyle { get; set; }
    public string? EngagementRange { get; set; }
    public string? SquadRole { get; set; }
}

internal sealed class VanguardOperatorProgressionDto
{
    public int Level { get; set; }
    public int Experience { get; set; }
    public int RaidCount { get; set; }
    public int SurvivedRaidCount { get; set; }
    public int FailedRaidCount { get; set; }
    public int KillCount { get; set; }
    public int AssistCount { get; set; }
    public int Trust { get; set; }
    public int Loyalty { get; set; }
    public int Respect { get; set; }
}

internal sealed class VanguardOperatorCareerDto
{
    public DateTimeOffset? EnrolledAtUtc { get; set; }
    public int EnrollmentLevel { get; set; }
    public int EnrollmentExperience { get; set; }
    public DateTimeOffset? TrackingStartedAtUtc { get; set; }
    public string? TrackingOrigin { get; set; }
    public string? HistoryCompleteness { get; set; }
    public long ExperienceEarnedSinceEnrollment { get; set; }
    public VanguardOperatorCareerStatisticsDto? Statistics { get; set; }
    public List<VanguardOperatorRaidHistoryEntryDto>? RaidHistory { get; set; }
    public Dictionary<string, VanguardOperatorTargetCareerStatisticsDto>? TargetStatistics { get; set; }
    public List<VanguardOperatorAchievementRecordDto>? Achievements { get; set; }
    public List<VanguardOperatorPersonaEvidenceDto>? PersonaEvidence { get; set; }
    public VanguardOperatorExperienceReconciliationDto? ExperienceReconciliation { get; set; }
    public VanguardOperatorCareerXpCommitStateDto? XpCommitState { get; set; }
    public int SchemaVersion { get; set; }
}

internal sealed class VanguardOperatorCareerXpCommitStateDto
{
    public string? PolicyId { get; set; }
    public int PolicyVersion { get; set; }
    public string? State { get; set; }
    public DateTimeOffset? ActivatedAtUtc { get; set; }
    public string? CoverageBoundary { get; set; }
    public string? CurveSource { get; set; }
    public bool CurveAuthoritative { get; set; }
    public bool LifetimeCoverageFromEnrollment { get; set; }
    public int PreActivationVerifiedEntryCount { get; set; }
    public int PreActivationXpCreditCount { get; set; }
    public long PreActivationXpSubtotalNotCommitted { get; set; }
    public List<string>? PreActivationExcludedCreditTokens { get; set; }
    public int AppliedCreditCount { get; set; }
    public long TotalCommittedExperience { get; set; }
    public List<string>? AppliedCreditTokens { get; set; }
    public string? LastAppliedRaidSessionId { get; set; }
    public DateTimeOffset? LastAppliedAtUtc { get; set; }
    public int SchemaVersion { get; set; }
}

internal sealed class VanguardOperatorExperienceReconciliationDto
{
    public string? PolicyId { get; set; }
    public int PolicyVersion { get; set; }
    public string? State { get; set; }
    public int PreviousProgressionLevel { get; set; }
    public int PreviousProgressionExperience { get; set; }
    public int PreviousEnrollmentLevel { get; set; }
    public int PreviousEnrollmentExperience { get; set; }
    public int PreservedLevel { get; set; }
    public int ReconciledExperience { get; set; }
    public int CurrentLevelFloorExperience { get; set; }
    public int NextLevelExperience { get; set; }
    public string? CurveSource { get; set; }
    public bool CurveAuthoritative { get; set; }
    public long ExperienceEarnedSinceEnrollmentPreserved { get; set; }
    public bool Reversible { get; set; }
    public DateTimeOffset? AppliedAtUtc { get; set; }
    public string? Reason { get; set; }
    public int SchemaVersion { get; set; }
}

internal sealed class VanguardOperatorCareerStatisticsDto
{
    public int RaidCount { get; set; }
    public int SurvivedRaidCount { get; set; }
    public int FailedRaidCount { get; set; }
    public int DeathCount { get; set; }
    public int KillCount { get; set; }
    public int AssistCount { get; set; }
    public int BossKillCount { get; set; }
    public int SpecialTargetKillCount { get; set; }
    public int CurrentSurvivalStreak { get; set; }
    public int BestSurvivalStreak { get; set; }
}

internal sealed class VanguardOperatorRaidHistoryEntryDto
{
    public string? EventId { get; set; }
    public string? RaidSessionId { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public string? LocationId { get; set; }
    public string? ExitStatus { get; set; }
    public int ExperienceBefore { get; set; }
    public int ExperienceAfter { get; set; }
    public int ExperienceGained { get; set; }
    public int KillCount { get; set; }
    public int AssistCount { get; set; }
    public bool Died { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

internal sealed class VanguardOperatorTargetCareerStatisticsDto
{
    public string? TargetStableId { get; set; }
    public string? TargetKind { get; set; }
    public string? DisplayName { get; set; }
    public int EncounterCount { get; set; }
    public int KillCount { get; set; }
    public int AssistCount { get; set; }
    public int DeathsToTarget { get; set; }
    public string? FirstKillRaidSessionId { get; set; }
    public string? LastKillRaidSessionId { get; set; }
    public DateTimeOffset? FirstKillAtUtc { get; set; }
    public DateTimeOffset? LastKillAtUtc { get; set; }
}

internal sealed class VanguardOperatorAchievementRecordDto
{
    public string? AchievementId { get; set; }
    public int DefinitionVersion { get; set; }
    public string? State { get; set; }
    public long Progress { get; set; }
    public long Target { get; set; }
    public DateTimeOffset? UnlockedAtUtc { get; set; }
    public Dictionary<string, string>? Evidence { get; set; }
}

internal sealed class VanguardOperatorPersonaEvidenceDto
{
    public string? EvidenceId { get; set; }
    public string? Dimension { get; set; }
    public double Delta { get; set; }
    public string? SourceEventId { get; set; }
    public DateTimeOffset? RecordedAtUtc { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

internal sealed class VanguardActiveServiceRecordDto
{
    public string? OperatorId { get; set; }
    public string? DisplayName { get; set; }
    public string? Side { get; set; }
    public string? Role { get; set; }
    public string? Specialty { get; set; }
    public string? Status { get; set; }
    public bool IsSelectedForRaid { get; set; }
    public bool IsDeployed { get; set; }
    public DateTimeOffset? HiredAtUtc { get; set; }
    public int SalaryPerRaid { get; set; }
    public DateTimeOffset? LastRaidAtUtc { get; set; }
    public DateTimeOffset? RecoveryUntilUtc { get; set; }
}

internal sealed class VanguardOperatorContractOfferDto
{
    public string? OfferId { get; set; }
    public string? OperatorId { get; set; }
    public string? DisplayName { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Callsign { get; set; }
    public string? Side { get; set; }
    public string? Role { get; set; }
    public string? Specialty { get; set; }
    public int Level { get; set; }
    public int Experience { get; set; }
    public int HirePrice { get; set; }
    public int SalaryPerRaid { get; set; }
    public string? CurrencyTpl { get; set; }
    public string? Rarity { get; set; }
    public string? VisualFamily { get; set; }
    public string? BasePersona { get; set; }
    public string? Doctrine { get; set; }
    public string? Temperament { get; set; }
    public string? SainProfileFamily { get; set; }
    public string? SainTuningPlan { get; set; }
    public List<string>? Traits { get; set; }
    public string? CombatStyle { get; set; }
    public string? EngagementRange { get; set; }
    public string? SquadRole { get; set; }
    public string? BehaviorSummary { get; set; }
    public bool CanHire { get; set; } = true;
    public string? MarketStatus { get; set; }
    public string? RelationshipSummary { get; set; }
    public DateTimeOffset? AvailableUntilUtc { get; set; }
}

internal sealed class VanguardOperatorContactRecordDto
{
    public string? OperatorId { get; set; }
    public string? DisplayName { get; set; }
    public string? ContactStatus { get; set; }
    public int ActiveServiceCount { get; set; }
    public int RaidTogetherCount { get; set; }
    public int Trust { get; set; }
    public int Loyalty { get; set; }
    public int Respect { get; set; }
    public int Grudge { get; set; }
    public string? NarrativeSummary { get; set; }
}

internal sealed class VanguardOperatorMedicalRecordDto
{
    public string? OperatorId { get; set; }
    public string? DisplayName { get; set; }
    public string? Status { get; set; }
    public double CurrentHealthRatio { get; set; }
    public DateTimeOffset? RecoveryUntilUtc { get; set; }
    public int HealCost { get; set; }
    public int RecoveryCost { get; set; }
    public bool DiedInLastRaid { get; set; }
    public string? InjurySummary { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
}

internal sealed class VanguardOperatorServiceProjectionDto
{
    public string? OperatorId { get; set; }
    public string? DisplayName { get; set; }
    public string? Side { get; set; }
    public string? Role { get; set; }
    public string? Specialty { get; set; }
    public string? VisualFamily { get; set; }
    public int Level { get; set; }
    public int Experience { get; set; }
    public string? ContractStatus { get; set; }
    public string? ServiceStatus { get; set; }
    public bool IsSelectedForRaid { get; set; }
    public bool IsDeployed { get; set; }
    public int SalaryPerRaid { get; set; }
    public int RaidCount { get; set; }
    public int SurvivedRaidCount { get; set; }
    public int KillCount { get; set; }
    public string? PersonaKey { get; set; }
    public string? Doctrine { get; set; }
    public string? Temperament { get; set; }
    public List<string>? Traits { get; set; }
    public string? SainProfileFamily { get; set; }
    public string? SainTuningPlan { get; set; }
    public int Trust { get; set; }
    public int Loyalty { get; set; }
    public string? EligibilityState { get; set; }
    public string? EligibilityReason { get; set; }
    public int ExperienceIntoLevel { get; set; }
    public int ExperienceRequiredForNextLevel { get; set; }
    public int NextLevelExperience { get; set; }
    public string? ExperienceCurveSource { get; set; }
    public int ExperienceCurveResolvedLevel { get; set; }
    public bool ExperienceLevelCoherent { get; set; }
    public string? ExperienceProgressState { get; set; }
}

internal sealed class VanguardOperatorMedicalProjectionDto
{
    public string? OperatorId { get; set; }
    public string? DisplayName { get; set; }
    public string? Role { get; set; }
    public int Level { get; set; }
    public string? ServiceStatus { get; set; }
    public string? MedicalStatus { get; set; }
    public double CurrentHealthRatio { get; set; }
    public string? InjurySummary { get; set; }
    public DateTimeOffset? RecoveryUntilUtc { get; set; }
    public int HealCost { get; set; }
    public int RecoveryCost { get; set; }
    public bool DiedInLastRaid { get; set; }
    public string? RecoveryState { get; set; }
}

internal sealed class VanguardOperatorRaidProjectionDto
{
    public string? ProjectionId { get; set; }
    public string? OperatorId { get; set; }
    public string? DisplayName { get; set; }
    public string? Side { get; set; }
    public int Level { get; set; }
    public string? Role { get; set; }
    public string? Specialty { get; set; }
    public string? Persona { get; set; }
    public string? Doctrine { get; set; }
    public string? Temperament { get; set; }
    public List<string>? Traits { get; set; }
    public string? SainProfileFamily { get; set; }
    public string? SainTuningPlan { get; set; }
    public bool IsActiveService { get; set; }
    public bool IsSelectedForRaid { get; set; }
    public bool IsEligibleForRaid { get; set; }
    public string? EligibilityReason { get; set; }
    public string? MedicalStatus { get; set; }
    public double HealthRatio { get; set; }
    public string? RuntimeInjectionState { get; set; }
}


internal sealed class VanguardCareerProjectionReadModelDto
{
    public static readonly VanguardCareerProjectionReadModelDto Empty = new();

    public string? ProjectionVersion { get; set; }
    public string? CoverageBoundary { get; set; }
    public string? CombatMethodCoverageState { get; set; }
    public string? TerminalDeathTruthCoverageState { get; set; }
    public string? CoverageState { get; set; }
    public string? LedgerReadState { get; set; }
    public bool ActiveLedgerFilePresent { get; set; }
    public bool QuarantineEvidencePresent { get; set; }
    public int SupportedLedgerSchemaVersion { get; set; }
    public string? SupportedLedgerTruthVersion { get; set; }
    public int SourceEntryCount { get; set; }
    public int VerifiedEntryCount { get; set; }
    public int RejectedEntryCount { get; set; }
    public int DuplicateEntryCount { get; set; }
    public int UnsupportedEntryCount { get; set; }
    public int IntegrityRejectedEntryCount { get; set; }
    public int SemanticRejectedEntryCount { get; set; }
    public int OwnerMismatchEntryCount { get; set; }
    public int UnprojectedVerifiedEntryCount { get; set; }
    public List<VanguardOperatorCareerProjectionDto>? Operators { get; set; }
    public int SchemaVersion { get; set; }
}

internal sealed class VanguardOperatorCareerProjectionDto
{
    public string? OperatorId { get; set; }
    public string? DisplayName { get; set; }
    public int SourceEntryCount { get; set; }
    public int VerifiedEntryCount { get; set; }
    public int RejectedEntryCount { get; set; }
    public int VerifiedRaidCount { get; set; }
    public int VerifiedSurvivedRaidCount { get; set; }
    public int VerifiedKiaCount { get; set; }
    public int VerifiedSelfInflictedDeathCount { get; set; }
    public int VerifiedKillCount { get; set; }
    public List<VanguardCareerNamedCombatantProjectionDto>? ConfirmedVictims { get; set; }
    public List<VanguardCareerDeathSourceProjectionDto>? ConfirmedDeathSources { get; set; }
    public Dictionary<string, int>? KillCountByTargetRawRole { get; set; }
    public Dictionary<string, int>? DeathCountByKillerRawRole { get; set; }
    public double SkillSessionPointsEarnedTotal { get; set; }
    public Dictionary<string, double>? SkillSessionPointsEarnedBySkill { get; set; }
}


internal sealed class VanguardCareerNamedCombatantProjectionDto
{
    public string? DisplayName { get; set; }
    public string? Side { get; set; }
    public string? RawRole { get; set; }
    public int Count { get; set; }
}

internal sealed class VanguardCareerDeathSourceProjectionDto
{
    public string? DisplayName { get; set; }
    public string? Side { get; set; }
    public string? RawRole { get; set; }
    public bool SelfInflicted { get; set; }
    public int Count { get; set; }
}

internal sealed class VanguardCanonicalRaidHistoryReadModelDto
{
    public static readonly VanguardCanonicalRaidHistoryReadModelDto Empty = new()
    {
        CareerParity = VanguardCanonicalRaidHistoryParityCheckDto.Empty
    };

    public string? ProjectionVersion { get; set; }
    public string? CoverageBoundary { get; set; }
    public string? CoverageState { get; set; }
    public string? LedgerReadState { get; set; }
    public string? RaidOrderingState { get; set; }
    public string? TimestampSemantics { get; set; }
    public string? LocationCoverageState { get; set; }
    public string? StartTimeCoverageState { get; set; }
    public string? CareerXpCoverageState { get; set; }
    public string? CombatMethodCoverageState { get; set; }
    public string? TerminalDeathTruthCoverageState { get; set; }
    public bool ActiveLedgerFilePresent { get; set; }
    public bool QuarantineEvidencePresent { get; set; }
    public int SupportedLedgerSchemaVersion { get; set; }
    public string? SupportedLedgerTruthVersion { get; set; }
    public int SourceEntryCount { get; set; }
    public int VerifiedEntryCount { get; set; }
    public int RejectedEntryCount { get; set; }
    public int DuplicateEntryCount { get; set; }
    public int UnsupportedEntryCount { get; set; }
    public int IntegrityRejectedEntryCount { get; set; }
    public int SemanticRejectedEntryCount { get; set; }
    public int OwnerMismatchEntryCount { get; set; }
    public int UnprojectedVerifiedEntryCount { get; set; }
    public List<VanguardOperatorCanonicalRaidHistoryDto>? Operators { get; set; }
    public VanguardCanonicalRaidHistoryParityCheckDto? CareerParity { get; set; }
    public int SchemaVersion { get; set; }
}

internal sealed class VanguardOperatorCanonicalRaidHistoryDto
{
    public string? OperatorId { get; set; }
    public string? DisplayName { get; set; }
    public int SourceEntryCount { get; set; }
    public int VerifiedEntryCount { get; set; }
    public int RejectedEntryCount { get; set; }
    public List<VanguardCanonicalRaidHistoryEntryDto>? Raids { get; set; }
}

internal sealed class VanguardCanonicalRaidHistoryEntryDto
{
    public string? EventId { get; set; }
    public string? SourceLedgerEntryId { get; set; }
    public string? RaidSessionId { get; set; }
    public string? OwnerProfileId { get; set; }
    public string? OperatorId { get; set; }
    public string? BotProfileId { get; set; }
    public bool Participated { get; set; }
    public bool AliveAtRaidEnd { get; set; }
    public bool Died { get; set; }
    public string? Outcome { get; set; }
    public string? RaidExitStatusTelemetry { get; set; }
    public string? RaidExitNameTelemetry { get; set; }
    public string? ExitBoundarySourceTelemetry { get; set; }
    public string? ExitBoundaryProfileIdTelemetry { get; set; }
    public float ExitBoundaryDelayTelemetry { get; set; }
    public DateTimeOffset ExitBoundaryObservedAtUtcTelemetry { get; set; }
    public DateTimeOffset LedgerCommittedAtUtcTelemetry { get; set; }
    public List<VanguardCanonicalRaidHistoryKillDto>? ConfirmedKills { get; set; }
    public VanguardCanonicalRaidHistoryDeathDto? Death { get; set; }
    public VanguardCanonicalRaidHistoryTerminalDeathTruthDto? TerminalDeathTruth { get; set; }
    public List<VanguardCanonicalRaidHistorySkillPointDto>? SkillSessionPoints { get; set; }
    public List<VanguardCanonicalRaidHistoryNotableEventDto>? NotableEvents { get; set; }
    public string? DeathSourceCoverageState { get; set; }
    public string? SourceFingerprint { get; set; }
    public string? TerminalDeathTruthFingerprint { get; set; }
}

internal sealed class VanguardCanonicalRaidHistoryKillDto
{
    public string? EventId { get; set; }
    public long Ordinal { get; set; }
    public DateTimeOffset ObservedAtUtcTelemetry { get; set; }
    public string? TargetProfileId { get; set; }
    public string? TargetAccountId { get; set; }
    public string? TargetDisplayName { get; set; }
    public string? TargetSide { get; set; }
    public string? TargetRawRole { get; set; }
}

internal sealed class VanguardCanonicalRaidHistoryDeathDto
{
    public string? EventId { get; set; }
    public long Ordinal { get; set; }
    public DateTimeOffset ObservedAtUtcTelemetry { get; set; }
    public string? KillerProfileId { get; set; }
    public string? KillerAccountId { get; set; }
    public string? KillerDisplayName { get; set; }
    public string? KillerSide { get; set; }
    public string? KillerRawRole { get; set; }
    public bool SelfInflicted { get; set; }
}

internal sealed class VanguardCanonicalRaidHistoryTerminalDeathTruthDto
{
    public string? EventId { get; set; }
    public DateTimeOffset ObservedAtUtcTelemetry { get; set; }
    public string? TerminalDamageType { get; set; }
    public int TerminalDamageTypeValue { get; set; }
    public string? LastDamageInfoType { get; set; }
    public int LastDamageInfoTypeValue { get; set; }
    public string? LastDamageBodyPart { get; set; }
    public int LastDamageBodyPartValue { get; set; }
    public bool DirectKillEventObservedAtCapture { get; set; }
    public string? DirectKillCorrelationState { get; set; }
    public string? LastAggressorProfileId { get; set; }
    public string? LastAggressorAccountId { get; set; }
    public string? LastAggressorDisplayName { get; set; }
    public string? LastAggressorSide { get; set; }
    public string? LastAggressorRawRole { get; set; }
    public int LastAggressorInfoLevel { get; set; }
    public int LastAggressorInfoExperience { get; set; }
    public int LastAggressorSettingsExperience { get; set; }
    public string? LastAggressorSemantics { get; set; }
    public string? Source { get; set; }
    public string? TruthVersion { get; set; }
    public int TruthSchemaVersion { get; set; }
}

internal sealed class VanguardCanonicalRaidHistorySkillPointDto
{
    public string? SkillId { get; set; }
    public double Progress { get; set; }
    public double PointsEarnedDuringSession { get; set; }
}

// Transport-only mirror of the server's structured notable-event contract. Do not place localized sentences
// here: event kind, evidence, actors and facts must remain machine-readable so different presentation layers
// (Off-Raid UI, VisitAPI add-ons, relationship systems) can interpret the same verified observation.
internal sealed class VanguardCanonicalRaidHistoryNotableEventDto
{
    public string? EventId { get; set; }
    public string? Kind { get; set; }
    public DateTimeOffset ObservedAtUtcTelemetry { get; set; }
    public string? EvidenceState { get; set; }
    public string? Source { get; set; }
    public List<VanguardCanonicalRaidHistoryEventActorDto>? Actors { get; set; }
    public Dictionary<string, string>? Facts { get; set; }
}

// Actor references are explicit rather than embedded in prose. This keeps future narratives able to resolve
// an Operator, player/client, enemy or other participant without parsing a display string.
internal sealed class VanguardCanonicalRaidHistoryEventActorDto
{
    public string? Role { get; set; }
    public string? ProfileId { get; set; }
    public string? OperatorId { get; set; }
    public string? DisplayName { get; set; }
}

internal sealed class VanguardCanonicalRaidHistoryParityCheckDto
{
    public static readonly VanguardCanonicalRaidHistoryParityCheckDto Empty = new();

    public bool IsMatch { get; set; }
    public int ComparedOperatorCount { get; set; }
    public int MismatchCount { get; set; }
    public double SkillPointComparisonTolerance { get; set; }
    public List<VanguardCanonicalRaidHistoryParityMismatchDto>? Mismatches { get; set; }
}

internal sealed class VanguardCanonicalRaidHistoryParityMismatchDto
{
    public string? OperatorId { get; set; }
    public string? Field { get; set; }
    public string? CareerValue { get; set; }
    public string? RaidHistoryValue { get; set; }
}

internal sealed class VanguardOperatorBillingSnapshotDto
{
    public static readonly VanguardOperatorBillingSnapshotDto Empty = new();

    public int OutstandingDebt { get; set; }
    public int PendingSignatureDebt { get; set; }
    public int SignedPendingSettlementDebt { get; set; }
    public int PaidTotal { get; set; }
    public int OpenInvoiceCount { get; set; }
    public List<VanguardOperatorBillingInvoiceDto>? OpenInvoices { get; set; }
    public List<VanguardOperatorBillingInvoiceDto>? RecentPaidInvoices { get; set; }
    public List<VanguardOperatorBillingNotificationDto>? Notifications { get; set; }
    public DateTimeOffset? GeneratedAtUtc { get; set; }
}

internal sealed class VanguardOperatorBillingInvoiceDto
{
    public string? InvoiceId { get; set; }
    public string? Type { get; set; }
    public string? Status { get; set; }
    public string? OperatorId { get; set; }
    public string? OperatorName { get; set; }
    public string? ContractId { get; set; }
    public int Amount { get; set; }
    public string? CurrencyTpl { get; set; }
    public DateTimeOffset? CreatedAtUtc { get; set; }
    public DateTimeOffset? SignedAtUtc { get; set; }
    public DateTimeOffset? AppliedAtUtc { get; set; }
    public string? Narrative { get; set; }
}

internal sealed class VanguardOperatorBillingNotificationDto
{
    public string? NotificationId { get; set; }
    public string? Kind { get; set; }
    public string? Title { get; set; }
    public string? Message { get; set; }
    public int Amount { get; set; }
    public string? CurrencyTpl { get; set; }
    public DateTimeOffset? CreatedAtUtc { get; set; }
    public bool Acknowledged { get; set; }
}
