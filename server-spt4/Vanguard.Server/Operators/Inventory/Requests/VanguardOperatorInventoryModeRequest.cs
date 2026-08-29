using SPTarkov.Server.Core.Models.Utils;

// Responsibility: Defines request payloads accepted by the server Operator inventory mode.
// Flow: Caller input is deserialized into these data-only shapes, then validated and executed by the owning route/service.
// Authority boundary: Transport contract only; it does not authorize, persist, or execute the requested operation.
// Invariant: Payload defaults remain backward-compatible and contain no hidden side effects.
namespace Vanguard.Server.Operators.Inventory.Requests;

public sealed record VanguardOperatorInventoryModeRequest(
    string? OperatorId,
    bool Confirm = false) : IRequestData;
