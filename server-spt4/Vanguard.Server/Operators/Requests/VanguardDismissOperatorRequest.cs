using SPTarkov.Server.Core.Models.Utils;

// Responsibility: Defines request payloads accepted by the Operator API request contracts.
// Flow: Caller input is deserialized into these data-only shapes, then validated and executed by the owning route/service.
// Authority boundary: Transport contract only; it does not authorize, persist, or execute the requested operation.
// Invariant: Payload defaults remain backward-compatible and contain no hidden side effects.
namespace Vanguard.Server.Operators.Requests;

public sealed record VanguardDismissOperatorRequest(
    string? OperatorId) : IRequestData;
