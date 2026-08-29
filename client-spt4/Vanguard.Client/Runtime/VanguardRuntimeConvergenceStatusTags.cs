// Responsibility: Centralizes stable diagnostic/status identifiers used by the Operator runtime.
// Flow: Callers reference these constants when emitting or correlating diagnostics; no runtime action is performed here.
// Authority boundary: Diagnostic naming only; behavior and state authority remain in the subsystem that emits each status.
// Invariant: Status identifiers stay stable and side-effect free so validators and runtime evidence can correlate the same contract.
namespace Vanguard.Client;

internal static class VanguardRuntimeConvergenceStatusTags
{
    public const string PatchStatus = "VANGUARD_RUNTIME_CONVERGENCE_GUARD_STATUS";
    public const string SainLayerVeto = "VANGUARD_SAIN_EXTRACT_LAYER_VETO_STATUS";
    public const string SainExtractTimeVeto = "VANGUARD_SAIN_EXTRACT_TIME_VETO_STATUS";
    public const string IncrementalCohesionPlanning = "VANGUARD_INCREMENTAL_COHESION_PLANNING_STATUS";
    public const string BoundedCohesionPathBudget = "VANGUARD_BOUNDED_COHESION_PATH_BUDGET_STATUS";
    public const string AsyncRuntimeSync = "VANGUARD_ASYNC_RUNTIME_SYNC_STATUS";
    public const string StationaryMedicalHysteresis = "VANGUARD_STATIONARY_MEDICAL_HYSTERESIS_STATUS";
    public const string TargetClearBackoff = "VANGUARD_TARGET_CLEAR_BACKOFF_STATUS";
    public const string FikaDogtagGuard = "VANGUARD_FIKA_DOGTAG_GUARD_STATUS";
}
