// Responsibility: Defines response/projection payloads produced by the owner loot-interest API.
// Flow: Owning services project canonical state into these shapes before serialization to the client or another subsystem.
// Authority boundary: Presentation/transport contract only; canonical authority remains with the source service or persistence store.
// Invariant: Projection code must not mutate the state it describes and serialized fields remain compatibility-conscious.
namespace Vanguard.Server.Operators.LootInterests.Responses;

public sealed class VanguardOwnerLootInterestEntry
{
    public string TemplateId { get; set; } = string.Empty;
    public string Group { get; set; } = "Other";
}

public sealed class VanguardOwnerLootInterestResponse
{
    public bool Success { get; set; }
    public string Reason { get; set; } = "none";
    public string OwnerProfileId { get; set; } = string.Empty;
    public long Revision { get; set; }
    public string ContentHash { get; set; } = "none";
    public string Source { get; set; } = "none";
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public string BuildLabel { get; set; } = string.Empty;
    public List<VanguardOwnerLootInterestEntry> Entries { get; set; } = [];
}
