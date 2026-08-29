// Responsibility: Defines data/state contracts used by the server Operator inventory mode, centered on Operator Inventory Summary.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Server.Operators.Inventory.Models;

public sealed record VanguardOperatorInventorySummary(
    string OperatorId,
    string DisplayName,
    string InventoryProfileId,
    bool ProfileExists,
    int ItemCount,
    bool HasPrimaryWeapon,
    bool HasBackpack,
    bool HasTacticalVest,
    bool HasArmorVest,
    string ReadinessState,
    DateTimeOffset? LastSavedUtc);
