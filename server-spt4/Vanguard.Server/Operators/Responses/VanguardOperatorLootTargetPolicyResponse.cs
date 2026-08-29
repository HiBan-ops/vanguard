// Responsibility: Defines response/projection payloads produced by the Operator API response contracts.
// Flow: Owning services project canonical state into these shapes before serialization to the client or another subsystem.
// Authority boundary: Presentation/transport contract only; canonical authority remains with the source service or persistence store.
// Invariant: Projection code must not mutate the state it describes and serialized fields remain compatibility-conscious.
namespace Vanguard.Server.Operators.Responses;

public sealed record VanguardOperatorLootTargetPolicyResponse(
    bool Success,
    string RequestedProfileId,
    string StorageProfileId,
    string Reason,
    string? OperatorId,
    string LootTargetPolicy,
    DateTimeOffset ProcessedAtUtc,
    string BuildLabel);
