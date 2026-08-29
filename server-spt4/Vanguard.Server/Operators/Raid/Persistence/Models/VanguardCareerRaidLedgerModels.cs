// Responsibility: Defines data/state contracts used by the raid persistence, centered on Career Raid Ledger Models.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Server.Operators.Raid.Persistence.Models;

public static class VanguardCareerRaidLedgerSchema
{
    public const int CurrentVersion = 1;
    public const string TruthVersion = "truth_v1";
}

public sealed record VanguardCareerRaidLedgerEntry(
    string LedgerEntryId,
    string RaidSessionId,
    string OwnerProfileId,
    string OperatorId,
    string BotProfileId,
    bool Participated,
    bool AliveAtRaidEnd,
    bool Died,
    string RaidExitStatus,
    string RaidExitName,
    string ExitBoundarySource,
    string ExitBoundaryProfileId,
    float ExitBoundaryDelay,
    DateTimeOffset ExitBoundaryObservedAtUtc,
    IReadOnlyList<VanguardCareerRaidLedgerKillEvent> Kills,
    VanguardCareerRaidLedgerDeathEvent? Death,
    IReadOnlyList<VanguardCareerRaidLedgerSkillSessionPoint> SkillSessionPoints,
    string SourceFingerprint,
    DateTimeOffset CommittedAtUtc,
    string TruthVersion = VanguardCareerRaidLedgerSchema.TruthVersion,
    int SchemaVersion = VanguardCareerRaidLedgerSchema.CurrentVersion,
    VanguardCareerRaidLedgerTerminalDeathTruth? TerminalDeathTruth = null,
    string TerminalDeathTruthFingerprint = "",
    IReadOnlyList<VanguardCareerRaidLedgerXpKillCredit>? XpKillCredits = null,
    string XpKillCreditsFingerprint = "");

public sealed record VanguardCareerRaidLedgerKillEvent(
    string EventId,
    long Ordinal,
    DateTimeOffset ObservedAtUtc,
    string TargetProfileId,
    string TargetAccountId,
    string TargetName,
    string TargetSide,
    string TargetRawRole,
    int TargetInfoLevel,
    int TargetInfoExperience,
    int TargetSettingsExperience);

public sealed record VanguardCareerRaidLedgerDeathEvent(
    string EventId,
    long Ordinal,
    DateTimeOffset ObservedAtUtc,
    string KillerProfileId,
    string KillerAccountId,
    string KillerName,
    string KillerSide,
    string KillerRawRole,
    int KillerInfoLevel,
    int KillerInfoExperience,
    int KillerSettingsExperience);

public static class VanguardCareerTerminalDeathTruthSchema
{
    public const int CurrentVersion = 1;
    public const string TruthVersion = "terminal_death_truth_v1";
}

/// <summary>
/// Additive terminal-death truth. LastAggressor is contextual provenance only and
/// must never be promoted to DirectKiller without the independently captured kill event.
/// This extension has its own fingerprint so the immutable base SourceFingerprint remains stable.
/// </summary>
public sealed record VanguardCareerRaidLedgerTerminalDeathTruth(
    string EventId,
    DateTimeOffset ObservedAtUtc,
    string TerminalDamageType,
    int TerminalDamageTypeValue,
    string LastDamageInfoType,
    int LastDamageInfoTypeValue,
    string LastDamageBodyPart,
    int LastDamageBodyPartValue,
    bool DirectKillEventObservedAtCapture,
    string LastAggressorProfileId,
    string LastAggressorAccountId,
    string LastAggressorName,
    string LastAggressorSide,
    string LastAggressorRawRole,
    int LastAggressorInfoLevel,
    int LastAggressorInfoExperience,
    int LastAggressorSettingsExperience,
    string Source,
    string TruthVersion = VanguardCareerTerminalDeathTruthSchema.TruthVersion,
    int SchemaVersion = VanguardCareerTerminalDeathTruthSchema.CurrentVersion);

public static class VanguardCareerXpKillCreditTruthSchema
{
    public const int CurrentVersion = 1;
    public const string TruthVersion = "xp_kill_credit_truth_v1";
}

public sealed record VanguardCareerRaidLedgerXpKillCredit(
    string EventId,
    DateTimeOffset ObservedAtUtc,
    string XpRecipientProfileId,
    string TargetProfileId,
    int KillSequence,
    string TargetSide,
    string TargetRawRole,
    int TargetLevel,
    int KillExpInput,
    string BodyPart,
    int BodyPartValue,
    bool SameGroup,
    bool TargetIsAi,
    bool XpRecipientHasMarkOfUnknown,
    float MarkOfUnknownScavKillExpPenalty,
    bool CalculationAvailable,
    bool Awarded,
    string CalculationReason,
    int BaseXp,
    int BodyPartBonusXp,
    int StreakBonusXp,
    int KillXpSubtotal,
    string Source,
    string TruthVersion = VanguardCareerXpKillCreditTruthSchema.TruthVersion,
    int SchemaVersion = VanguardCareerXpKillCreditTruthSchema.CurrentVersion);

public sealed record VanguardCareerRaidLedgerSkillSessionPoint(
    string SkillId,
    double Progress,
    double PointsEarnedDuringSession);

public sealed record VanguardCareerRaidLedgerCommitRequest(
    string? RaidSessionId,
    string? StopSource,
    string? StopProfileId,
    string? ExitStatus,
    string? ExitName,
    float StopDelay,
    DateTimeOffset StopObservedAtUtc,
    IReadOnlyList<VanguardCareerRaidLedgerKillEventRequest>? KillEvents,
    IReadOnlyList<VanguardCareerRaidTerminalDeathTruthEventRequest>? TerminalDeathTruthEvents = null,
    IReadOnlyList<VanguardCareerRaidXpKillCreditEventRequest>? XpKillCreditEvents = null,
    int SchemaVersion = VanguardCareerRaidLedgerSchema.CurrentVersion);

public sealed record VanguardCareerRaidLedgerKillEventRequest(
    string? EventId,
    string? RaidSessionId,
    long Ordinal,
    DateTimeOffset ObservedAtUtc,
    string? KillerProfileId,
    string? KillerAccountId,
    string? KillerName,
    string? KillerSide,
    string? KillerRawRole,
    int KillerInfoLevel,
    int KillerInfoExperience,
    int KillerSettingsExperience,
    string? TargetProfileId,
    string? TargetAccountId,
    string? TargetName,
    string? TargetSide,
    string? TargetRawRole,
    int TargetInfoLevel,
    int TargetInfoExperience,
    int TargetSettingsExperience);

public sealed record VanguardCareerRaidTerminalDeathTruthEventRequest(
    string? EventId,
    string? RaidSessionId,
    DateTimeOffset ObservedAtUtc,
    string? VictimProfileId,
    string? TerminalDamageType,
    int TerminalDamageTypeValue,
    string? LastDamageInfoType,
    int LastDamageInfoTypeValue,
    string? LastDamageBodyPart,
    int LastDamageBodyPartValue,
    bool DirectKillEventObservedAtCapture,
    string? LastAggressorProfileId,
    string? LastAggressorAccountId,
    string? LastAggressorName,
    string? LastAggressorSide,
    string? LastAggressorRawRole,
    int LastAggressorInfoLevel,
    int LastAggressorInfoExperience,
    int LastAggressorSettingsExperience,
    string? Source);

public sealed record VanguardCareerRaidXpKillCreditEventRequest(
    string? EventId,
    string? RaidSessionId,
    DateTimeOffset ObservedAtUtc,
    string? XpRecipientProfileId,
    string? TargetProfileId,
    int KillSequence,
    string? TargetSide,
    string? TargetRawRole,
    int TargetLevel,
    int KillExpInput,
    string? BodyPart,
    int BodyPartValue,
    bool SameGroup,
    bool TargetIsAi,
    bool XpRecipientHasMarkOfUnknown,
    float MarkOfUnknownScavKillExpPenalty,
    bool CalculationAvailable,
    bool Awarded,
    string? CalculationReason,
    int BaseXp,
    int BodyPartBonusXp,
    int StreakBonusXp,
    int KillXpSubtotal,
    string? Source);

public sealed record VanguardCareerRaidLedgerCommitResult(
    string Status,
    bool Admitted,
    bool Committed,
    bool IdempotentReplay,
    int AddedEntryCount,
    int ExistingEntryCount,
    int OwnerCount,
    string Reason,
    int SchemaVersion = VanguardCareerRaidLedgerSchema.CurrentVersion);

public sealed record VanguardCareerRaidLedgerOperatorTruth(
    string OwnerProfileId,
    string OperatorId,
    string BotProfileId,
    bool Died,
    VanguardOperatorCareerTruthProbe CareerTruthProbe);

public sealed record VanguardCareerRaidLedgerPreparedOwner(
    string OwnerProfileId,
    IReadOnlyList<VanguardCareerRaidLedgerEntry> Before,
    IReadOnlyList<VanguardCareerRaidLedgerEntry> After,
    IReadOnlyList<VanguardCareerRaidLedgerEntry> ExpectedEntries,
    bool RequiresWrite);

public sealed record VanguardCareerRaidLedgerPreparedBatch(
    string RaidSessionId,
    bool Admitted,
    string Reason,
    IReadOnlyList<VanguardCareerRaidLedgerPreparedOwner> Owners,
    int AddedEntryCount,
    int ExistingEntryCount,
    int OperatorCount,
    int KillEventCount,
    int SchemaVersion = VanguardCareerRaidLedgerSchema.CurrentVersion);

public sealed record VanguardCareerRaidLedgerReadSnapshot(
    string ReadState,
    IReadOnlyList<VanguardCareerRaidLedgerEntry> Entries,
    bool ActiveFilePresent,
    bool QuarantineEvidencePresent);
