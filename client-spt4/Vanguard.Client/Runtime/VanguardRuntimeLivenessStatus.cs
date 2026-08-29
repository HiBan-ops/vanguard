#if SPT_CLIENT

// Responsibility: Defines data/state contracts used by the Operator runtime, centered on Runtime Liveness Status.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime;

/// <summary>
/// Bounded liveness guard shared by medical readiness and cohesion recovery. It distinguishes
/// persistent capability from transient hands readiness and allows owner-closing movement only
/// inside an already-authorized extreme cohesion corridor. It never creates movement, combat,
/// medical or fire authority by itself.
/// </summary>
internal static class VanguardRuntimeLivenessStatus
{
    public const string StatusTag = "VANGUARD_RUNTIME_LIVENESS_CONVERGENCE_STATUS";
}
#endif
