using Vanguard.Server.Operators.Raid.Models;

// Responsibility: Defines response/projection payloads produced by the raid Operator server workflow.
// Flow: Owning services project canonical state into these shapes before serialization to the client or another subsystem.
// Authority boundary: Presentation/transport contract only; canonical authority remains with the source service or persistence store.
// Invariant: Projection code must not mutate the state it describes and serialized fields remain compatibility-conscious.
namespace Vanguard.Server.Operators.Raid.Responses;

public sealed record VanguardRaidOperatorManifestResponse(
    string RequestedProfileId,
    string StorageProfileId,
    string RaidSessionId,
    IReadOnlyList<VanguardRaidOperatorSnapshot> Operators,
    int ActiveServiceCount,
    int SelectedForRaidCount,
    int ReturnedCount,
    int SkippedCount,
    bool Success,
    string Reason,
    DateTimeOffset GeneratedAtUtc,
    string BuildLabel)
{
    public int OperatorCount => Operators.Count;
}

public sealed record VanguardRaidOperatorManifestForProfilesResponse(
    string RequesterProfileId,
    string RaidSessionId,
    IReadOnlyDictionary<string, VanguardRaidOperatorManifestResponse> ManifestsByOwnerProfileId,
    int OwnerCount,
    int OperatorCount,
    bool Success,
    string Reason,
    DateTimeOffset GeneratedAtUtc,
    string BuildLabel);
