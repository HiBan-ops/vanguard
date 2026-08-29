// Responsibility: Centralizes stable diagnostic/status identifiers used by the Operator runtime.
// Flow: Callers reference these constants when emitting or correlating diagnostics; no runtime action is performed here.
// Authority boundary: Diagnostic naming only; behavior and state authority remain in the subsystem that emits each status.
// Invariant: Status identifiers stay stable and side-effect free so validators and runtime evidence can correlate the same contract.
namespace Vanguard.Client;

internal static class VanguardAuthorityCircuitBreakerStatusTags
{
    public const string PatchStatus = "VANGUARD_AUTHORITY_CIRCUIT_BREAKER_STATUS";
    public const string MedicalEffectCircuitBreaker = "VANGUARD_MEDICAL_EFFECT_CIRCUIT_BREAKER_STATUS";
    public const string SainAutonomousExtractVeto = "VANGUARD_SAIN_AUTONOMOUS_EXTRACT_VETO_STATUS";
    public const string SainExtractAuthorityClassification = "VANGUARD_SAIN_EXTRACT_AUTHORITY_CLASSIFICATION_STATUS";
    public const string BoundedCoverCandidateScan = "VANGUARD_BOUNDED_COVER_CANDIDATE_SCAN_STATUS";
}
