using Vanguard.Server.Operators.Inventory.Models;

// Responsibility: Defines response/projection payloads produced by the server Operator inventory mode.
// Flow: Owning services project canonical state into these shapes before serialization to the client or another subsystem.
// Authority boundary: Presentation/transport contract only; canonical authority remains with the source service or persistence store.
// Invariant: Projection code must not mutate the state it describes and serialized fields remain compatibility-conscious.
namespace Vanguard.Server.Operators.Inventory.Responses;

public sealed class VanguardOperatorInventorySummaryResponse
{
    public string RequestedProfileId { get; set; } = string.Empty;

    public string StorageProfileId { get; set; } = string.Empty;

    public IReadOnlyList<VanguardOperatorInventorySummary> Summaries { get; set; } = Array.Empty<VanguardOperatorInventorySummary>();

    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
