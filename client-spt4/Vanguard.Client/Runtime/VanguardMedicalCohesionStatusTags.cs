// Responsibility: Centralizes stable diagnostic/status identifiers used by the Operator runtime.
// Flow: Callers reference these constants when emitting or correlating diagnostics; no runtime action is performed here.
// Authority boundary: Diagnostic naming only; behavior and state authority remain in the subsystem that emits each status.
// Invariant: Status identifiers stay stable and side-effect free so validators and runtime evidence can correlate the same contract.
namespace Vanguard.Client;

internal static class VanguardMedicalCohesionStatusTags
{
    public const string PatchStatus = "VANGUARD_AUTHORITY_SURGERY_COHESION_CONVERGENCE_STATUS";
    public const string MovementLeaseIdentity = "VANGUARD_MOVEMENT_LEASE_IDENTITY_STATUS";
    public const string MobileMedicalSidecarAuthority = "VANGUARD_MOBILE_MEDICAL_SIDECAR_AUTHORITY_STATUS";
    public const string SequentialSurgeryBoundary = "VANGUARD_SEQUENTIAL_SURGERY_BOUNDARY_STATUS";
    public const string SurgeryCoverReselection = "VANGUARD_SURGERY_COVER_RESELECTION_STATUS";
    public const string CachedIncrementalCover = "VANGUARD_CACHED_INCREMENTAL_COVER_STATUS";
}
