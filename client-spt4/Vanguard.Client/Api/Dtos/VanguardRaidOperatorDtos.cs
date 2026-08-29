using System;
using System.Collections.Generic;

// Responsibility: Defines client/server transport DTOs used by the client/server API contracts.
// Flow: API responses and requests are normalized into these data-only shapes before being consumed by higher-level client logic.
// Authority boundary: Transport data only; server persistence and in-raid runtime services remain authoritative for the represented state.
// Invariant: DTOs remain serialization-safe, side-effect free, and tolerant of compatible server data.
namespace Vanguard.Client.Api.Dtos;

internal sealed class VanguardRaidManifestForProfilesRequestDto
{
    public List<string>? ProfileIds { get; set; }
    public string? RaidSessionId { get; set; }
}

internal sealed class VanguardRaidOperatorManifestForProfilesResponseDto
{
    public string? RequesterProfileId { get; set; }
    public string? RaidSessionId { get; set; }
    public Dictionary<string, VanguardRaidOperatorManifestResponseDto>? ManifestsByOwnerProfileId { get; set; }
    public int OwnerCount { get; set; }
    public int OperatorCount { get; set; }
    public bool Success { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public string? BuildLabel { get; set; }
}

internal sealed class VanguardRaidOperatorManifestResponseDto
{
    public string? RequestedProfileId { get; set; }
    public string? StorageProfileId { get; set; }
    public string? RaidSessionId { get; set; }
    public List<VanguardRaidOperatorSnapshotDto>? Operators { get; set; }
    public int ActiveServiceCount { get; set; }
    public int SelectedForRaidCount { get; set; }
    public int ReturnedCount { get; set; }
    public int SkippedCount { get; set; }
    public int OperatorCount => Operators?.Count ?? 0;
    public bool Success { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public string? BuildLabel { get; set; }
}

internal sealed class VanguardRaidOperatorSnapshotDto
{
    public string? OperatorId { get; set; }
    public string? OwnerProfileId { get; set; }
    public string? OwnerNickname { get; set; }
    public string? RaidSessionId { get; set; }
    public string? OperatorInventoryProfileId { get; set; }
    public string? DisplayName { get; set; }
    public string? Callsign { get; set; }
    public string? Side { get; set; }
    public int Level { get; set; }
    public int Experience { get; set; }
    public string? Role { get; set; }
    public string? Specialty { get; set; }
    public string? ServiceStatus { get; set; }
    public bool IsSelectedForRaid { get; set; }
    public bool IsEligibleForRaid { get; set; }
    public string? EligibilityReason { get; set; }
    public string? MedicalStatus { get; set; }
    public double HealthRatio { get; set; }
    public bool InventoryProfileExists { get; set; }
    public int InventoryItemCount { get; set; }
    public bool HasEquipmentRoot { get; set; }
    public VanguardRaidOperatorSainPayloadDto? SainRuntime { get; set; }
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public string? BuildLabel { get; set; }
    public int SchemaVersion { get; set; }
    public string? LootTargetPolicy { get; set; }
}

internal sealed class VanguardRaidOperatorSainPayloadDto
{
    public string? BasePersona { get; set; }
    public string? Doctrine { get; set; }
    public string? Temperament { get; set; }
    public string? SainProfileFamily { get; set; }
    public string? SainTuningPlan { get; set; }
    public string? CombatStyle { get; set; }
    public string? EngagementRange { get; set; }
    public string? SquadRole { get; set; }
    public List<string>? Traits { get; set; }
}

internal sealed class VanguardRaidOperatorPersistenceEntryRequestDto
{
    public string? OperatorId { get; set; }
    public string? OwnerProfileId { get; set; }
    public string? BotProfileId { get; set; }
    public bool Died { get; set; }
    public double HealthRatio { get; set; }
    public string? ProfileDescriptorJson { get; set; }
    public string? SnapshotSource { get; set; }
    public int ClientItemCount { get; set; }
    public string? CorpseId { get; set; }
    public int CorpseEquipmentItemCount { get; set; } = -1;
    public List<string>? CorpseEquipmentItemIds { get; set; }
    public string? StatisticsManagerType { get; set; }
}

internal sealed class VanguardRaidOperatorPersistenceBatchRequestDto
{
    public string? RaidSessionId { get; set; }
    public List<VanguardRaidOperatorPersistenceEntryRequestDto>? Operators { get; set; }
    public string? AuthorityKind { get; set; }
    public string? ClientBuild { get; set; }
    public string? ClientLabel { get; set; }
    public VanguardCareerRaidLedgerCommitRequestDto? CareerLedger { get; set; }
}

internal sealed class VanguardRaidOperatorPersistenceEntryResponseDto
{
    public string? OperatorId { get; set; }
    public string? OwnerProfileId { get; set; }
    public bool Died { get; set; }
    public bool Success { get; set; }
    public string? Reason { get; set; }
    public int EquipmentItemCount { get; set; }
    public string? EquipmentFingerprint { get; set; }
    public string? SnapshotSource { get; set; }
    public VanguardOperatorCareerTruthProbeDto? CareerTruthProbe { get; set; }
    public VanguardRaidSkillCommitResultDto? SkillProgression { get; set; }
}

internal sealed class VanguardRaidSkillCommitResultDto
{
    public bool Success { get; set; }
    public string? Reason { get; set; }
    public string? StorageProfileId { get; set; }
    public string? OperatorId { get; set; }
    public int CommonSkillCount { get; set; }
    public int CommonProgressedCount { get; set; }
    public double CommonProgressDelta { get; set; }
    public int MasteringSkillCount { get; set; }
    public int MasteringProgressedCount { get; set; }
    public double MasteringProgressDelta { get; set; }
    public string? RuntimeFingerprint { get; set; }
    public string? PersistentFingerprint { get; set; }
    public string? ProfilePath { get; set; }
}

internal sealed class VanguardOperatorCareerTruthProbeDto
{
    public string? Status { get; set; }
    public bool DescriptorParsed { get; set; }
    public string? DescriptorReason { get; set; }
    public int PersistentLevelBefore { get; set; }
    public int PersistentExperienceBefore { get; set; }
    public bool InfoPresent { get; set; }
    public int DescriptorReportedLevel { get; set; }
    public int DescriptorExperience { get; set; }
    public int DescriptorExperienceDeltaFromPersistent { get; set; }
    public int ExperienceCurveResolvedLevel { get; set; }
    public bool ExperienceCurveAuthoritative { get; set; }
    public string? ExperienceCurveSource { get; set; }
    public bool ExperienceLevelCoherent { get; set; }
    public string? DescriptorExperienceSemantics { get; set; }
    public bool DescriptorExperienceIsCareerAuthority { get; set; }
    public string? StatisticsManagerType { get; set; }
    public string? NativeSessionExperienceAuthorityState { get; set; }
    public bool NativeSessionExperienceAuthorityAvailable { get; set; }
    public bool StatsEftPresent { get; set; }
    public string? SessionCountersState { get; set; }
    public int SessionCounterItemCount { get; set; }
    public int SessionCounterNonZeroCount { get; set; }
    public long? SessionKills { get; set; }
    public long? SessionDeaths { get; set; }
    public long? SessionExpKill { get; set; }
    public long? SessionExpExitStatus { get; set; }
    public string? OverallCountersState { get; set; }
    public int OverallCounterItemCount { get; set; }
    public int OverallCounterNonZeroCount { get; set; }
    public int TotalSessionExperience { get; set; }
    public string? VictimsState { get; set; }
    public int VictimCount { get; set; }
    public List<VanguardOperatorCareerTruthVictimDto>? Victims { get; set; }
    public string? DeathCauseState { get; set; }
    public string? DeathCauseDamageType { get; set; }
    public string? DeathCauseSide { get; set; }
    public string? DeathCauseRole { get; set; }
    public string? DeathCauseWeaponId { get; set; }
    public string? AggressorState { get; set; }
    public string? AggressorProfileId { get; set; }
    public string? AggressorAccountId { get; set; }
    public string? AggressorName { get; set; }
    public string? AggressorSide { get; set; }
    public string? AggressorRole { get; set; }
    public bool DiedRuntimeTruth { get; set; }
    public string? DiedTruthSource { get; set; }
    public string? ExitStatusState { get; set; }
    public string? ExitStatusValue { get; set; }
    public string? RaidOutcomeState { get; set; }
    public string? SkillsCommonState { get; set; }
    public int SkillCommonCount { get; set; }
    public int SkillsWithSessionPoints { get; set; }
    public double SkillSessionPointsTotal { get; set; }
    public List<VanguardOperatorCareerTruthSkillDto>? SkillsWithSessionPointEntries { get; set; }
    public List<string>? MissingOrUnreliable { get; set; }
    public int SchemaVersion { get; set; }
}

internal sealed class VanguardOperatorCareerTruthVictimDto
{
    public string? ProfileId { get; set; }
    public string? AccountId { get; set; }
    public string? Name { get; set; }
    public string? Side { get; set; }
    public int Level { get; set; }
    public string? Role { get; set; }
    public string? Weapon { get; set; }
    public string? BodyPart { get; set; }
    public double Distance { get; set; }
    public string? Location { get; set; }
    public string? Time { get; set; }
}

internal sealed class VanguardOperatorCareerTruthSkillDto
{
    public string? Id { get; set; }
    public double Progress { get; set; }
    public double PointsEarnedDuringSession { get; set; }
}

internal sealed class VanguardRaidOperatorPersistenceBatchResponseDto
{
    public bool Success { get; set; }
    public string? Reason { get; set; }
    public string? RaidSessionId { get; set; }
    public int RequestedOperatorCount { get; set; }
    public int CommittedOperatorCount { get; set; }
    public bool IdempotentReplay { get; set; }
    public bool RolledBack { get; set; }
    public List<VanguardRaidOperatorPersistenceEntryResponseDto>? Operators { get; set; }
    public DateTimeOffset CommittedAtUtc { get; set; }
    public string? BuildLabel { get; set; }
    public VanguardCareerRaidLedgerCommitResponseDto? CareerLedger { get; set; }
}
