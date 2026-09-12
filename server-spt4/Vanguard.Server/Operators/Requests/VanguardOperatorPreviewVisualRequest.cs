using SPTarkov.Server.Core.Models.Utils;

// Responsibility: requests renderer-only visual projections for Operators owned by the current SPT session.
// Flow: the client supplies only Operator ids; route/session identity remains the owner authority and cannot be overridden by payload data.
// Authority boundary: transport contract only. It cannot request another profile and carries no mutation/repair flags.
// Invariant: at most the explicitly requested Operator ids are considered by the read-only preview service.
namespace Vanguard.Server.Operators.Requests;

public sealed class VanguardOperatorPreviewVisualRequest : IRequestData
{
    public List<string> OperatorIds { get; set; } = [];
}
