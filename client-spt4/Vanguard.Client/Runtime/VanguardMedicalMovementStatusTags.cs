// Responsibility: Centralizes stable diagnostic/status identifiers used by the Operator runtime.
// Flow: Callers reference these constants when emitting or correlating diagnostics; no runtime action is performed here.
// Authority boundary: Diagnostic naming only; behavior and state authority remain in the subsystem that emits each status.
// Invariant: Status identifiers stay stable and side-effect free so validators and runtime evidence can correlate the same contract.
namespace Vanguard.Client;

internal static class VanguardMedicalMovementStatusTags
{
    public const string PatchStatus = "VANGUARD_MEDICAL_MOVEMENT_DEBT_CONVERGENCE_STATUS";
    public const string MedicalAuthorityContract = "VANGUARD_MEDICAL_AUTHORITY_CONTRACT_STATUS";
    public const string MedicalStateBoundOutcome = "VANGUARD_MEDICAL_STATE_BOUND_OUTCOME_STATUS";
    public const string PhysicalCohesionTruth = "VANGUARD_PHYSICAL_COHESION_TRUTH_STATUS";
    public const string StrictIncrementalSurgeryCover = "VANGUARD_STRICT_INCREMENTAL_SURGERY_COVER_STATUS";
}
