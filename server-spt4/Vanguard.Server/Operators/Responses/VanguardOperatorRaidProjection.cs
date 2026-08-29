using Vanguard.Server.Operators.Models;

// Responsibility: Defines response/projection payloads produced by the Operator API response contracts.
// Flow: Owning services project canonical state into these shapes before serialization to the client or another subsystem.
// Authority boundary: Presentation/transport contract only; canonical authority remains with the source service or persistence store.
// Invariant: Projection code must not mutate the state it describes and serialized fields remain compatibility-conscious.
namespace Vanguard.Server.Operators.Responses;

public sealed record VanguardOperatorRaidProjection(
    string ProjectionId,
    string OperatorId,
    string DisplayName,
    string Side,
    int Level,
    string Role,
    string Specialty,
    string Persona,
    string Doctrine,
    string Temperament,
    IReadOnlyList<string> Traits,
    string SainProfileFamily,
    string SainTuningPlan,
    bool IsActiveService,
    bool IsSelectedForRaid,
    bool IsEligibleForRaid,
    string EligibilityReason,
    string MedicalStatus,
    double HealthRatio,
    string RuntimeInjectionState,
    string SnapshotVersion,
    int SchemaVersion,
    DateTimeOffset UpdatedAtUtc);
