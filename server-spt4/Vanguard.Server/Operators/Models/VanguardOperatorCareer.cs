// Responsibility: Defines data/state contracts used by the Operator persistence/domain models, centered on Operator Career.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Server.Operators.Models;

/// <summary>
/// Career state is additive: raw events, derived aggregates, achievements and persona evidence remain separated
/// so future features can be added without rewriting historical truth. Reversible legacy XP-baseline evidence
/// coexists with a forward-only, auditable
/// commit state for verified EFT kill-XP components without pretending to reconstruct full session XP.
/// </summary>
public sealed record VanguardOperatorCareer(
    DateTimeOffset EnrolledAtUtc,
    int EnrollmentLevel,
    int EnrollmentExperience,
    DateTimeOffset TrackingStartedAtUtc,
    string TrackingOrigin,
    string HistoryCompleteness,
    long ExperienceEarnedSinceEnrollment,
    VanguardOperatorCareerStatistics Statistics,
    IReadOnlyList<VanguardOperatorRaidHistoryEntry>? RaidHistory = null,
    IReadOnlyDictionary<string, VanguardOperatorTargetCareerStatistics>? TargetStatistics = null,
    IReadOnlyList<VanguardOperatorAchievementRecord>? Achievements = null,
    IReadOnlyList<VanguardOperatorPersonaEvidence>? PersonaEvidence = null,
    VanguardOperatorExperienceReconciliation? ExperienceReconciliation = null,
    VanguardOperatorCareerXpCommitState? XpCommitState = null,
    int SchemaVersion = VanguardOperatorCareerSchema.CurrentVersion)
{
    public static VanguardOperatorCareer NewEnrollment(DateTimeOffset enrolledAtUtc, int level, int experience) => new(
        enrolledAtUtc,
        Math.Max(level, 1),
        Math.Max(experience, 0),
        enrolledAtUtc,
        "native_enrollment",
        "complete_since_enrollment",
        0,
        VanguardOperatorCareerStatistics.Empty,
        Array.Empty<VanguardOperatorRaidHistoryEntry>(),
        new Dictionary<string, VanguardOperatorTargetCareerStatistics>(StringComparer.OrdinalIgnoreCase),
        Array.Empty<VanguardOperatorAchievementRecord>(),
        Array.Empty<VanguardOperatorPersonaEvidence>(),
        null,
        VanguardOperatorCareerXpCommitState.NewEnrollment(enrolledAtUtc));

    public static VanguardOperatorCareer MigratedLegacy(VanguardOperatorProfile profile, DateTimeOffset trackingStartedAtUtc) => new(
        profile.CreatedAtUtc,
        Math.Max(profile.Progression.Level, 1),
        Math.Max(profile.Progression.Experience, 0),
        trackingStartedAtUtc,
        "legacy_profile_migration",
        "partial_from_legacy_migration",
        0,
        new VanguardOperatorCareerStatistics(
            Math.Max(profile.Progression.RaidCount, 0),
            Math.Max(profile.Progression.SurvivedRaidCount, 0),
            Math.Max(profile.Progression.FailedRaidCount, 0),
            0,
            Math.Max(profile.Progression.KillCount, 0),
            Math.Max(profile.Progression.AssistCount, 0),
            0,
            0,
            0,
            0,
            VanguardOperatorCareerSchema.CurrentVersion),
        Array.Empty<VanguardOperatorRaidHistoryEntry>(),
        new Dictionary<string, VanguardOperatorTargetCareerStatistics>(StringComparer.OrdinalIgnoreCase),
        Array.Empty<VanguardOperatorAchievementRecord>(),
        Array.Empty<VanguardOperatorPersonaEvidence>());
}

public static class VanguardOperatorCareerSchema
{
    public const int CurrentVersion = 3;
}

public static class VanguardOperatorCareerXpCommitPolicy
{
    public const string PolicyId = "verified_eft_kill_xp_forward_only_v1";
    public const int PolicyVersion = 1;
    public const string ActiveState = "active_forward_only_verified_eft_kill_components";
    public const string CoverageBoundary = "verified_eft_kill_components_only";
}

public static class VanguardOperatorCareerXpCommitSchema
{
    public const int CurrentVersion = 1;
}

/// <summary>
/// Durable XP-commit evidence. Existing Operators are activated forward-only: credits already
/// present in the verified career ledger at activation remain historical shadow evidence and are not
/// retroactively awarded. New enrollments are covered from enrollment. Applied credit tokens are
/// persisted so crash/replay recovery is idempotent.
/// </summary>
public sealed record VanguardOperatorCareerXpCommitState(
    string PolicyId,
    int PolicyVersion,
    string State,
    DateTimeOffset ActivatedAtUtc,
    string CoverageBoundary,
    string CurveSource,
    bool CurveAuthoritative,
    bool LifetimeCoverageFromEnrollment,
    int PreActivationVerifiedEntryCount,
    int PreActivationXpCreditCount,
    long PreActivationXpSubtotalNotCommitted,
    IReadOnlyList<string> PreActivationExcludedCreditTokens,
    int AppliedCreditCount,
    long TotalCommittedExperience,
    IReadOnlyList<string> AppliedCreditTokens,
    string LastAppliedRaidSessionId,
    DateTimeOffset? LastAppliedAtUtc,
    int SchemaVersion = VanguardOperatorCareerXpCommitSchema.CurrentVersion)
{
    public static VanguardOperatorCareerXpCommitState NewEnrollment(DateTimeOffset enrolledAtUtc) => new(
        VanguardOperatorCareerXpCommitPolicy.PolicyId,
        VanguardOperatorCareerXpCommitPolicy.PolicyVersion,
        VanguardOperatorCareerXpCommitPolicy.ActiveState,
        enrolledAtUtc,
        VanguardOperatorCareerXpCommitPolicy.CoverageBoundary,
        "pending_authoritative_curve_resolution",
        false,
        true,
        0,
        0,
        0,
        Array.Empty<string>(),
        0,
        0,
        Array.Empty<string>(),
        string.Empty,
        null);
}


/// <summary>
/// Career state is additive: raw events, derived aggregates, achievements and persona evidence remain separated.
/// This record never represents XP earned in a raid. It preserves legacy synthetic
/// Level/XP pair and the authoritative EFT cumulative level window used to rebase XP while
/// preserving the historical Operator level. The permanent pre-reconciliation backup makes the change
/// reversible outside the live model as well.
/// </summary>
public sealed record VanguardOperatorExperienceReconciliation(
    string PolicyId,
    int PolicyVersion,
    string State,
    int PreviousProgressionLevel,
    int PreviousProgressionExperience,
    int PreviousEnrollmentLevel,
    int PreviousEnrollmentExperience,
    int PreservedLevel,
    int ReconciledExperience,
    int CurrentLevelFloorExperience,
    int NextLevelExperience,
    string CurveSource,
    bool CurveAuthoritative,
    long ExperienceEarnedSinceEnrollmentPreserved,
    bool Reversible,
    DateTimeOffset AppliedAtUtc,
    string Reason,
    int SchemaVersion = VanguardOperatorExperienceReconciliationSchema.CurrentVersion);

public static class VanguardOperatorExperienceReconciliationSchema
{
    public const int CurrentVersion = 1;
}

public sealed record VanguardOperatorCareerStatistics(
    int RaidCount,
    int SurvivedRaidCount,
    int FailedRaidCount,
    int DeathCount,
    int KillCount,
    int AssistCount,
    int BossKillCount,
    int SpecialTargetKillCount,
    int CurrentSurvivalStreak,
    int BestSurvivalStreak,
    int SchemaVersion = VanguardOperatorCareerSchema.CurrentVersion)
{
    public static readonly VanguardOperatorCareerStatistics Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Legacy semantic raid-history contract retained for compatibility. The durable ledger and its projections provide the
/// verified historical read path; this older shape remains unfilled while required fields lack truth.
/// </summary>
public sealed record VanguardOperatorRaidHistoryEntry(
    string EventId,
    string RaidSessionId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    string LocationId,
    string ExitStatus,
    int ExperienceBefore,
    int ExperienceAfter,
    int ExperienceGained,
    int KillCount,
    int AssistCount,
    bool Died,
    IReadOnlyDictionary<string, string>? Metadata = null,
    int SchemaVersion = VanguardOperatorCareerSchema.CurrentVersion);

public sealed record VanguardOperatorTargetCareerStatistics(
    string TargetStableId,
    string TargetKind,
    string DisplayName,
    int EncounterCount,
    int KillCount,
    int AssistCount,
    int DeathsToTarget,
    string? FirstKillRaidSessionId,
    string? LastKillRaidSessionId,
    DateTimeOffset? FirstKillAtUtc,
    DateTimeOffset? LastKillAtUtc,
    int SchemaVersion = VanguardOperatorCareerSchema.CurrentVersion);

public sealed record VanguardOperatorAchievementRecord(
    string AchievementId,
    int DefinitionVersion,
    string State,
    long Progress,
    long Target,
    DateTimeOffset? UnlockedAtUtc,
    IReadOnlyDictionary<string, string>? Evidence = null,
    int SchemaVersion = VanguardOperatorCareerSchema.CurrentVersion);

public sealed record VanguardOperatorPersonaEvidence(
    string EvidenceId,
    string Dimension,
    double Delta,
    string SourceEventId,
    DateTimeOffset RecordedAtUtc,
    IReadOnlyDictionary<string, string>? Metadata = null,
    int SchemaVersion = VanguardOperatorCareerSchema.CurrentVersion);
