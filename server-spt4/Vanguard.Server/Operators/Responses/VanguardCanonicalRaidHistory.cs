// Responsibility: Defines response/projection payloads produced by the Operator API response contracts.
// Flow: Owning services project canonical state into these shapes before serialization to the client or another subsystem.
// Authority boundary: Presentation/transport contract only; canonical authority remains with the source service or persistence store.
// Invariant: Projection code must not mutate the state it describes and serialized fields remain compatibility-conscious.
namespace Vanguard.Server.Operators.Responses;

public static class VanguardCanonicalRaidHistorySchema
{
    public const int CurrentVersion = 2;
    public const string ProjectionVersion = "canonical_raid_history_v2";
    public const string CoverageBoundary = "forward_committed_entries_only_no_backfill_guarantee";
    public const string RaidOrderingState = "deterministic_raid_session_id_then_ledger_entry_id_not_chronological";
    public const string TimestampSemantics = "raw_process_telemetry_not_global_causal_order";
    public const string LocationCoverageState = "location_not_persisted";
    public const string StartTimeCoverageState = "authoritative_start_not_persisted";
    public const string CareerXpCoverageState = "career_xp_not_persisted";
    public const string CombatMethodCoverageState = "exact_combat_method_not_persisted";
    public const string TerminalDeathTruthCoverageState = "terminal_death_truth_forward_only_when_observed";
}

public sealed record VanguardCanonicalRaidHistoryReadModel(
    string ProjectionVersion,
    string CoverageBoundary,
    string CoverageState,
    string LedgerReadState,
    string RaidOrderingState,
    string TimestampSemantics,
    string LocationCoverageState,
    string StartTimeCoverageState,
    string CareerXpCoverageState,
    string CombatMethodCoverageState,
    string TerminalDeathTruthCoverageState,
    bool ActiveLedgerFilePresent,
    bool QuarantineEvidencePresent,
    int SupportedLedgerSchemaVersion,
    string SupportedLedgerTruthVersion,
    int SourceEntryCount,
    int VerifiedEntryCount,
    int RejectedEntryCount,
    int DuplicateEntryCount,
    int UnsupportedEntryCount,
    int IntegrityRejectedEntryCount,
    int SemanticRejectedEntryCount,
    int OwnerMismatchEntryCount,
    int UnprojectedVerifiedEntryCount,
    IReadOnlyList<VanguardOperatorCanonicalRaidHistory> Operators,
    VanguardCanonicalRaidHistoryParityCheck CareerParity,
    int SchemaVersion = VanguardCanonicalRaidHistorySchema.CurrentVersion);

public sealed record VanguardOperatorCanonicalRaidHistory(
    string OperatorId,
    string DisplayName,
    int SourceEntryCount,
    int VerifiedEntryCount,
    int RejectedEntryCount,
    IReadOnlyList<VanguardCanonicalRaidHistoryEntry> Raids);

public sealed record VanguardCanonicalRaidHistoryEntry(
    string EventId,
    string SourceLedgerEntryId,
    string RaidSessionId,
    string OwnerProfileId,
    string OperatorId,
    string BotProfileId,
    bool Participated,
    bool AliveAtRaidEnd,
    bool Died,
    string Outcome,
    string RaidExitStatusTelemetry,
    string RaidExitNameTelemetry,
    string ExitBoundarySourceTelemetry,
    string ExitBoundaryProfileIdTelemetry,
    float ExitBoundaryDelayTelemetry,
    DateTimeOffset ExitBoundaryObservedAtUtcTelemetry,
    DateTimeOffset LedgerCommittedAtUtcTelemetry,
    IReadOnlyList<VanguardCanonicalRaidHistoryKill> ConfirmedKills,
    VanguardCanonicalRaidHistoryDeath? Death,
    VanguardCanonicalRaidHistoryTerminalDeathTruth? TerminalDeathTruth,
    IReadOnlyList<VanguardCanonicalRaidHistorySkillPoint> SkillSessionPoints,
    string DeathSourceCoverageState,
    string SourceFingerprint,
    string TerminalDeathTruthFingerprint);

public sealed record VanguardCanonicalRaidHistoryKill(
    string EventId,
    long Ordinal,
    DateTimeOffset ObservedAtUtcTelemetry,
    string TargetProfileId,
    string TargetAccountId,
    string TargetDisplayName,
    string TargetSide,
    string TargetRawRole);

public sealed record VanguardCanonicalRaidHistoryDeath(
    string EventId,
    long Ordinal,
    DateTimeOffset ObservedAtUtcTelemetry,
    string KillerProfileId,
    string KillerAccountId,
    string KillerDisplayName,
    string KillerSide,
    string KillerRawRole,
    bool SelfInflicted);

public sealed record VanguardCanonicalRaidHistoryTerminalDeathTruth(
    string EventId,
    DateTimeOffset ObservedAtUtcTelemetry,
    string TerminalDamageType,
    int TerminalDamageTypeValue,
    string LastDamageInfoType,
    int LastDamageInfoTypeValue,
    string LastDamageBodyPart,
    int LastDamageBodyPartValue,
    bool DirectKillEventObservedAtCapture,
    string DirectKillCorrelationState,
    string LastAggressorProfileId,
    string LastAggressorAccountId,
    string LastAggressorDisplayName,
    string LastAggressorSide,
    string LastAggressorRawRole,
    int LastAggressorInfoLevel,
    int LastAggressorInfoExperience,
    int LastAggressorSettingsExperience,
    string LastAggressorSemantics,
    string Source,
    string TruthVersion,
    int TruthSchemaVersion);

public sealed record VanguardCanonicalRaidHistorySkillPoint(
    string SkillId,
    double Progress,
    double PointsEarnedDuringSession);

public sealed record VanguardCanonicalRaidHistoryParityCheck(
    bool IsMatch,
    int ComparedOperatorCount,
    int MismatchCount,
    double SkillPointComparisonTolerance,
    IReadOnlyList<VanguardCanonicalRaidHistoryParityMismatch> Mismatches);

public sealed record VanguardCanonicalRaidHistoryParityMismatch(
    string OperatorId,
    string Field,
    string CareerValue,
    string RaidHistoryValue);
