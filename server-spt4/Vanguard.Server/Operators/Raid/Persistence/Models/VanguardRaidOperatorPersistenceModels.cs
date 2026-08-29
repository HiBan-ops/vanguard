using SPTarkov.Server.Core.Models.Utils;

// Responsibility: Defines data/state contracts used by the raid persistence, centered on Raid Operator Persistence Models.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Server.Operators.Raid.Persistence.Models;

public sealed record VanguardRaidOperatorPersistenceEntryRequest(
    string? OperatorId,
    string? OwnerProfileId,
    string? BotProfileId,
    bool Died,
    double HealthRatio,
    string? ProfileDescriptorJson,
    string? SnapshotSource,
    int ClientItemCount,
    string? CorpseId = null,
    int CorpseEquipmentItemCount = -1,
    IReadOnlyList<string>? CorpseEquipmentItemIds = null,
    string? StatisticsManagerType = null) : IRequestData;

public sealed record VanguardRaidOperatorPersistenceBatchRequest(
    string? RaidSessionId,
    IReadOnlyList<VanguardRaidOperatorPersistenceEntryRequest>? Operators,
    string? AuthorityKind,
    string? ClientBuild,
    string? ClientLabel,
    VanguardCareerRaidLedgerCommitRequest? CareerLedger = null) : IRequestData;

public sealed record VanguardRaidOperatorPersistenceEntryResponse(
    string OperatorId,
    string OwnerProfileId,
    bool Died,
    bool Success,
    string Reason,
    int EquipmentItemCount,
    string EquipmentFingerprint,
    string SnapshotSource,
    VanguardOperatorCareerTruthProbe? CareerTruthProbe = null,
    VanguardRaidSkillCommitResult? SkillProgression = null);

public sealed record VanguardRaidOperatorPersistenceBatchResponse(
    bool Success,
    string Reason,
    string RaidSessionId,
    int RequestedOperatorCount,
    int CommittedOperatorCount,
    bool IdempotentReplay,
    bool RolledBack,
    IReadOnlyList<VanguardRaidOperatorPersistenceEntryResponse> Operators,
    DateTimeOffset CommittedAtUtc,
    string BuildLabel,
    VanguardCareerRaidLedgerCommitResult? CareerLedger = null);

public sealed record VanguardRaidInventoryPreparedSnapshot(
    System.Text.Json.Nodes.JsonObject SnapshotInventory,
    string EquipmentId,
    IReadOnlyList<string> EquipmentItemIds,
    int SnapshotItemCount,
    int EquipmentItemCount,
    string EquipmentFingerprint);

public sealed record VanguardRaidInventoryCommitResult(
    bool Success,
    string Reason,
    string StorageProfileId,
    string OperatorId,
    int EquipmentItemCount,
    string EquipmentFingerprint,
    string ProfilePath);

/// <summary>
/// Prepared EFT skill state captured from the final runtime profile descriptor.
/// Common Progress/LastAccess and Mastering Progress are durable state.
/// Common PointsEarnedDuringSession remains session evidence and is reset at the raid boundary.
/// </summary>
public sealed record VanguardRaidSkillPreparedSnapshot(
    System.Text.Json.Nodes.JsonObject SnapshotSkills,
    int CommonSkillCount,
    int MasteringSkillCount,
    string RuntimeFingerprint);

public sealed record VanguardRaidSkillCommitResult(
    bool Success,
    string Reason,
    string StorageProfileId,
    string OperatorId,
    int CommonSkillCount,
    int CommonProgressedCount,
    double CommonProgressDelta,
    int MasteringSkillCount,
    int MasteringProgressedCount,
    double MasteringProgressDelta,
    string RuntimeFingerprint,
    string PersistentFingerprint,
    string ProfilePath);

/// <summary>
/// Read-only persistence diagnostic contract returned to the client alongside the authoritative persistence response.
/// It exposes descriptor/native-session evidence for diagnosis only and is never itself durable Operator Career state.
/// </summary>
public sealed record VanguardOperatorCareerTruthProbe(
    string Status,
    bool DescriptorParsed,
    string DescriptorReason,
    int PersistentLevelBefore,
    int PersistentExperienceBefore,
    bool InfoPresent,
    int DescriptorReportedLevel,
    int DescriptorExperience,
    int DescriptorExperienceDeltaFromPersistent,
    int ExperienceCurveResolvedLevel,
    bool ExperienceCurveAuthoritative,
    string ExperienceCurveSource,
    bool ExperienceLevelCoherent,
    string DescriptorExperienceSemantics,
    bool DescriptorExperienceIsCareerAuthority,
    string StatisticsManagerType,
    string NativeSessionExperienceAuthorityState,
    bool NativeSessionExperienceAuthorityAvailable,
    bool StatsEftPresent,
    string SessionCountersState,
    int SessionCounterItemCount,
    int SessionCounterNonZeroCount,
    long? SessionKills,
    long? SessionDeaths,
    long? SessionExpKill,
    long? SessionExpExitStatus,
    string OverallCountersState,
    int OverallCounterItemCount,
    int OverallCounterNonZeroCount,
    int TotalSessionExperience,
    string VictimsState,
    int VictimCount,
    IReadOnlyList<VanguardOperatorCareerTruthVictim> Victims,
    string DeathCauseState,
    string DeathCauseDamageType,
    string DeathCauseSide,
    string DeathCauseRole,
    string DeathCauseWeaponId,
    string AggressorState,
    string AggressorProfileId,
    string AggressorAccountId,
    string AggressorName,
    string AggressorSide,
    string AggressorRole,
    bool DiedRuntimeTruth,
    string DiedTruthSource,
    string ExitStatusState,
    string ExitStatusValue,
    string RaidOutcomeState,
    string SkillsCommonState,
    int SkillCommonCount,
    int SkillsWithSessionPoints,
    double SkillSessionPointsTotal,
    IReadOnlyList<VanguardOperatorCareerTruthSkill> SkillsWithSessionPointEntries,
    IReadOnlyList<string> MissingOrUnreliable,
    int SchemaVersion = 2);

public sealed record VanguardOperatorCareerTruthVictim(
    string ProfileId,
    string AccountId,
    string Name,
    string Side,
    int Level,
    string Role,
    string Weapon,
    string BodyPart,
    double Distance,
    string Location,
    string Time);

public sealed record VanguardOperatorCareerTruthSkill(
    string Id,
    double Progress,
    double PointsEarnedDuringSession);

