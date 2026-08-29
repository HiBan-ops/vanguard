#if SPT_CLIENT

// Responsibility: Defines data/state contracts used by the Operator allegiance runtime, centered on Operator Allegiance Snapshot.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime.Alliance;

internal sealed class VanguardOperatorAllegianceSnapshot
{
    public string ActorProfileId { get; init; } = string.Empty;
    public string TargetProfileId { get; init; } = string.Empty;
    public bool ActorIsVanguardOperator { get; init; }
    public bool TargetIsPlayer { get; init; }
    public bool TargetIsVanguardOperator { get; init; }
    public bool ProtectedByCoopAlliance { get; init; }
    public bool EarlyBindProtection { get; init; }
    public string AllianceId { get; init; } = VanguardRaidAlliancePolicy.DefaultAllianceId;

    public string Summary =>
        $"actor={ActorProfileId}; target={TargetProfileId}; actorOperator={ActorIsVanguardOperator}; targetPlayer={TargetIsPlayer}; targetOperator={TargetIsVanguardOperator}; protected={ProtectedByCoopAlliance}; earlyBind={EarlyBindProtection}; alliance={AllianceId}; mode={VanguardRaidAlliancePolicy.Mode}";
}
#else
namespace Vanguard.Client.Runtime.Alliance;

internal sealed class VanguardOperatorAllegianceSnapshot
{
}
#endif
