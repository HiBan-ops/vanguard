// Responsibility: Defines data/state contracts used by the Operator persistence/domain models, centered on Operator Identity Registry.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Server.Operators.Models;

public static class VanguardOperatorIdentityRegistrySchema
{
    public const int CurrentVersion = 1;
}

/// <summary>
/// Server-global reservation ledger. Enrolled/historical Operator identities remain reserved permanently;
/// unaccepted market offers are reserved only for their offer lifetime plus a bounded grace period.
/// This prevents duplicate Operators without exhausting finite callsign/name pools through daily offers.
/// </summary>
public sealed record VanguardOperatorIdentityReservation(
    string OperatorId,
    string OwnerProfileId,
    string FirstName,
    string LastName,
    string Callsign,
    string DisplayName,
    string Side,
    string Source,
    DateTimeOffset ReservedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    bool IsPermanent,
    DateTimeOffset? ExpiresAtUtc,
    int SchemaVersion = VanguardOperatorIdentityRegistrySchema.CurrentVersion);
