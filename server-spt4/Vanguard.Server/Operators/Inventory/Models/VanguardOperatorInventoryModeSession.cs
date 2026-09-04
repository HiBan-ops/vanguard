using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Profile;

// Responsibility: Defines data/state contracts used by the server Operator inventory mode, centered on Operator Inventory Mode Session.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Server.Operators.Inventory.Models;

public sealed record VanguardOperatorInventoryModeSession(
    MongoId PlayerProfileId,
    string StorageProfileId,
    string OperatorId,
    string OperatorDisplayName,
    string OperatorCallsign,
    string OperatorInventoryProfileId,
    string ProfilePath,
    SptProfile Profile,
    JsonObject ClientSessionProfileNode,
    DateTimeOffset EnteredAtUtc);
