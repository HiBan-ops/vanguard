using Vanguard.Server.Operators.Audit.Models;

// Responsibility: Defines response/projection payloads produced by the server runtime-audit settings.
// Flow: Owning services project canonical state into these shapes before serialization to the client or another subsystem.
// Authority boundary: Presentation/transport contract only; canonical authority remains with the source service or persistence store.
// Invariant: Projection code must not mutate the state it describes and serialized fields remain compatibility-conscious.
namespace Vanguard.Server.Operators.Audit.Responses;

public sealed class VanguardOperatorRuntimeAuditSettingsResponse
{
    public bool Success { get; set; }
    public string? Reason { get; set; }
    public VanguardOperatorRuntimeAuditSettings? Settings { get; set; }
}
