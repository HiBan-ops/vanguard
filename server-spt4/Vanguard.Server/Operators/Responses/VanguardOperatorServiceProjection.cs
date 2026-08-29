using Vanguard.Server.Operators.Models;

// Responsibility: Defines response/projection payloads produced by the Operator API response contracts.
// Flow: Owning services project canonical state into these shapes before serialization to the client or another subsystem.
// Authority boundary: Presentation/transport contract only; canonical authority remains with the source service or persistence store.
// Invariant: Projection code must not mutate the state it describes and serialized fields remain compatibility-conscious.
namespace Vanguard.Server.Operators.Responses;

public sealed record VanguardOperatorServiceProjection(
    string OperatorId,
    string DisplayName,
    string Side,
    string Role,
    string Specialty,
    string VisualFamily,
    int Level,
    int Experience,
    string ContractStatus,
    string ServiceStatus,
    bool IsSelectedForRaid,
    bool IsDeployed,
    int SalaryPerRaid,
    int RaidCount,
    int SurvivedRaidCount,
    int KillCount,
    string PersonaKey,
    string Doctrine,
    string Temperament,
    IReadOnlyList<string> Traits,
    string SainProfileFamily,
    string SainTuningPlan,
    int Trust,
    int Loyalty,
    string EligibilityState,
    string EligibilityReason,
    int ExperienceIntoLevel,
    int ExperienceRequiredForNextLevel,
    int NextLevelExperience,
    string ExperienceCurveSource,
    int ExperienceCurveResolvedLevel,
    bool ExperienceLevelCoherent,
    string ExperienceProgressState,
    int SchemaVersion);
