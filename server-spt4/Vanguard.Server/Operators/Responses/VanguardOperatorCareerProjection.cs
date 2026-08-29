// Responsibility: Defines response/projection payloads produced by the Operator API response contracts.
// Flow: Owning services project canonical state into these shapes before serialization to the client or another subsystem.
// Authority boundary: Presentation/transport contract only; canonical authority remains with the source service or persistence store.
// Invariant: Projection code must not mutate the state it describes and serialized fields remain compatibility-conscious.
namespace Vanguard.Server.Operators.Responses;

public static class VanguardCareerProjectionSchema
{
    public const int CurrentVersion = 3;
    public const string ProjectionVersion = "projection_v3";
    public const string CombatMethodCoverageState = "exact_combat_method_not_persisted";
    public const string CoverageBoundary = "forward_committed_entries_only_no_backfill_guarantee";
}

public sealed record VanguardCareerProjectionReadModel(
    string ProjectionVersion,
    string CoverageBoundary,
    string CombatMethodCoverageState,
    string CoverageState,
    string LedgerReadState,
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
    IReadOnlyList<VanguardOperatorCareerProjection> Operators,
    int SchemaVersion = VanguardCareerProjectionSchema.CurrentVersion);

public sealed record VanguardOperatorCareerProjection(
    string OperatorId,
    string DisplayName,
    int SourceEntryCount,
    int VerifiedEntryCount,
    int RejectedEntryCount,
    int VerifiedRaidCount,
    int VerifiedSurvivedRaidCount,
    int VerifiedKiaCount,
    int VerifiedSelfInflictedDeathCount,
    int VerifiedKillCount,
    IReadOnlyList<VanguardCareerNamedCombatantProjection> ConfirmedVictims,
    IReadOnlyList<VanguardCareerDeathSourceProjection> ConfirmedDeathSources,
    IReadOnlyDictionary<string, int> KillCountByTargetRawRole,
    IReadOnlyDictionary<string, int> DeathCountByKillerRawRole,
    double SkillSessionPointsEarnedTotal,
    IReadOnlyDictionary<string, double> SkillSessionPointsEarnedBySkill);

public sealed record VanguardCareerNamedCombatantProjection(
    string DisplayName,
    string Side,
    string RawRole,
    int Count);

public sealed record VanguardCareerDeathSourceProjection(
    string DisplayName,
    string Side,
    string RawRole,
    bool SelfInflicted,
    int Count);
