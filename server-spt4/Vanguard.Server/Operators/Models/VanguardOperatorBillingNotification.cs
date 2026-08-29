// Responsibility: Defines data/state contracts used by the Operator persistence/domain models, centered on Operator Billing Notification.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Server.Operators.Models;

public sealed record VanguardOperatorBillingNotification(
    string NotificationId,
    string Kind,
    string Title,
    string Message,
    int Amount,
    string CurrencyTpl,
    DateTimeOffset CreatedAtUtc,
    bool Acknowledged,
    int SchemaVersion = VanguardOperatorSchema.CurrentVersion);
