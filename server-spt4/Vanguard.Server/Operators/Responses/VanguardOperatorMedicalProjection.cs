using Vanguard.Server.Operators.Models;

// Responsibility: Defines response/projection payloads produced by the Operator API response contracts.
// Flow: Owning services project canonical state into these shapes before serialization to the client or another subsystem.
// Authority boundary: Presentation/transport contract only; canonical authority remains with the source service or persistence store.
// Invariant: Projection code must not mutate the state it describes and serialized fields remain compatibility-conscious.
namespace Vanguard.Server.Operators.Responses;

public sealed record VanguardOperatorMedicalProjection(
    string OperatorId,
    string DisplayName,
    string Role,
    int Level,
    string ServiceStatus,
    string MedicalStatus,
    double CurrentHealthRatio,
    string InjurySummary,
    DateTimeOffset? RecoveryUntilUtc,
    int HealCost,
    int RecoveryCost,
    bool DiedInLastRaid,
    DateTimeOffset LastUpdatedUtc,
    string RecoveryState,
    int SchemaVersion);
