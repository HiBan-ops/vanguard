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
/// Server-global reservation ledger. Every durable Vanguard world Operator identity remains reserved permanently,
/// independently of contract visibility or ActiveService membership. Legacy/pre-materialization market offers may still
/// carry temporary reservations until they are either materialized as world Operators or expire.
/// This prevents duplicate Operators without making the contract market the authority for person existence.
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
