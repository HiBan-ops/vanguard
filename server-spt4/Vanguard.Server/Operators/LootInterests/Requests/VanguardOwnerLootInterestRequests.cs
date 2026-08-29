using SPTarkov.Server.Core.Models.Utils;

// Responsibility: Defines request payloads accepted by the owner loot-interest API.
// Flow: Caller input is deserialized into these data-only shapes, then validated and executed by the owning route/service.
// Authority boundary: Transport contract only; it does not authorize, persist, or execute the requested operation.
// Invariant: Payload defaults remain backward-compatible and contain no hidden side effects.
namespace Vanguard.Server.Operators.LootInterests.Requests;

public sealed record VanguardOwnerLootInterestEntryRequest(string TemplateId = "", string Group = "Other");

public sealed record VanguardOwnerLootInterestSetRequest(
    string? OwnerProfileId = null,
    long Revision = 0,
    string? ContentHash = null,
    string? Source = null,
    string? ClientBuild = null,
    List<VanguardOwnerLootInterestEntryRequest>? Entries = null) : IRequestData;

public sealed record VanguardOwnerLootInterestGetRequest(
    string? OwnerProfileId = null,
    string? Source = null,
    string? ClientBuild = null) : IRequestData;
