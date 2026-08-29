using System;
using System.Collections.Generic;

// Responsibility: Defines client/server transport DTOs used by the client/server API contracts.
// Flow: API responses and requests are normalized into these data-only shapes before being consumed by higher-level client logic.
// Authority boundary: Transport data only; server persistence and in-raid runtime services remain authoritative for the represented state.
// Invariant: DTOs remain serialization-safe, side-effect free, and tolerant of compatible server data.
namespace Vanguard.Client.Api.Dtos;

internal sealed class VanguardCareerRaidLedgerCommitRequestDto
{
    public string? RaidSessionId { get; set; }
    public string? StopSource { get; set; }
    public string? StopProfileId { get; set; }
    public string? ExitStatus { get; set; }
    public string? ExitName { get; set; }
    public float StopDelay { get; set; }
    public DateTimeOffset StopObservedAtUtc { get; set; }
    public List<VanguardCareerRaidLedgerKillEventDto>? KillEvents { get; set; }
    public List<VanguardCareerRaidTerminalDeathTruthEventDto>? TerminalDeathTruthEvents { get; set; }
    public List<VanguardCareerRaidXpKillCreditEventDto>? XpKillCreditEvents { get; set; }
    public int SchemaVersion { get; set; } = 1;
}

internal sealed class VanguardCareerRaidLedgerKillEventDto
{
    public string? EventId { get; set; }
    public string? RaidSessionId { get; set; }
    public long Ordinal { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public string? KillerProfileId { get; set; }
    public string? KillerAccountId { get; set; }
    public string? KillerName { get; set; }
    public string? KillerSide { get; set; }
    public string? KillerRawRole { get; set; }
    public int KillerInfoLevel { get; set; }
    public int KillerInfoExperience { get; set; }
    public int KillerSettingsExperience { get; set; }
    public string? TargetProfileId { get; set; }
    public string? TargetAccountId { get; set; }
    public string? TargetName { get; set; }
    public string? TargetSide { get; set; }
    public string? TargetRawRole { get; set; }
    public int TargetInfoLevel { get; set; }
    public int TargetInfoExperience { get; set; }
    public int TargetSettingsExperience { get; set; }
}

internal sealed class VanguardCareerRaidTerminalDeathTruthEventDto
{
    public string? EventId { get; set; }
    public string? RaidSessionId { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public string? VictimProfileId { get; set; }
    public string? TerminalDamageType { get; set; }
    public int TerminalDamageTypeValue { get; set; }
    public string? LastDamageInfoType { get; set; }
    public int LastDamageInfoTypeValue { get; set; }
    public string? LastDamageBodyPart { get; set; }
    public int LastDamageBodyPartValue { get; set; }
    public bool DirectKillEventObservedAtCapture { get; set; }
    public string? LastAggressorProfileId { get; set; }
    public string? LastAggressorAccountId { get; set; }
    public string? LastAggressorName { get; set; }
    public string? LastAggressorSide { get; set; }
    public string? LastAggressorRawRole { get; set; }
    public int LastAggressorInfoLevel { get; set; }
    public int LastAggressorInfoExperience { get; set; }
    public int LastAggressorSettingsExperience { get; set; }
    public string? Source { get; set; }
}

internal sealed class VanguardCareerRaidXpKillCreditEventDto
{
    public string? EventId { get; set; }
    public string? RaidSessionId { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public string? XpRecipientProfileId { get; set; }
    public string? TargetProfileId { get; set; }
    public int KillSequence { get; set; }
    public string? TargetSide { get; set; }
    public string? TargetRawRole { get; set; }
    public int TargetLevel { get; set; }
    public int KillExpInput { get; set; }
    public string? BodyPart { get; set; }
    public int BodyPartValue { get; set; }
    public bool SameGroup { get; set; }
    public bool TargetIsAi { get; set; }
    public bool XpRecipientHasMarkOfUnknown { get; set; }
    public float MarkOfUnknownScavKillExpPenalty { get; set; }
    public bool CalculationAvailable { get; set; }
    public bool Awarded { get; set; }
    public string? CalculationReason { get; set; }
    public int BaseXp { get; set; }
    public int BodyPartBonusXp { get; set; }
    public int StreakBonusXp { get; set; }
    public int KillXpSubtotal { get; set; }
    public string? Source { get; set; }
}

internal sealed class VanguardCareerRaidLedgerCommitResponseDto
{
    public string? Status { get; set; }
    public bool Admitted { get; set; }
    public bool Committed { get; set; }
    public bool IdempotentReplay { get; set; }
    public int AddedEntryCount { get; set; }
    public int ExistingEntryCount { get; set; }
    public int OwnerCount { get; set; }
    public string? Reason { get; set; }
    public int SchemaVersion { get; set; }
}
