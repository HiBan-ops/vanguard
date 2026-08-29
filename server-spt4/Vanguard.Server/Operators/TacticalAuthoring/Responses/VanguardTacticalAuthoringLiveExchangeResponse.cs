using Vanguard.Server.Operators.TacticalAuthoring.Requests;

// Responsibility: Defines response/projection payloads produced by the server tactical-authoring relay.
// Flow: Owning services project canonical state into these shapes before serialization to the client or another subsystem.
// Authority boundary: Presentation/transport contract only; canonical authority remains with the source service or persistence store.
// Invariant: Projection code must not mutate the state it describes and serialized fields remain compatibility-conscious.
namespace Vanguard.Server.Operators.TacticalAuthoring.Responses;

public sealed class VanguardTacticalAuthoringLiveExchangeResponse
{
    public bool Success { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<VanguardTacticalAuthoringLiveAuthorSnapshot> Authors { get; set; } = [];
    public List<VanguardTacticalAuthoringLiveHeadlessResult> HeadlessResults { get; set; } = [];
    public DateTimeOffset ServerTimeUtc { get; set; } = DateTimeOffset.UtcNow;
}
