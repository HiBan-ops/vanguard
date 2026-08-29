// Responsibility: Centralizes stable diagnostic/status identifiers used by the Operator runtime.
// Flow: Callers reference these constants when emitting or correlating diagnostics; no runtime action is performed here.
// Authority boundary: Diagnostic naming only; behavior and state authority remain in the subsystem that emits each status.
// Invariant: Status identifiers stay stable and side-effect free so validators and runtime evidence can correlate the same contract.
namespace Vanguard.Client;

internal static class VanguardCombatTruthStatusTags
{
    public const string PatchStatus = "VANGUARD_COMBAT_TRUTH_CONVERGENCE_STATUS";
    public const string SharedContactSearchOnly = "VANGUARD_SHARED_CONTACT_SEARCH_ONLY_STATUS";
    public const string DeadContactSourceInvalidation = "VANGUARD_DEAD_CONTACT_SOURCE_INVALIDATION_STATUS";
    public const string TargetApplyCircuitBreaker = "VANGUARD_TARGET_APPLY_CIRCUIT_BREAKER_STATUS";
    public const string RuntimeSyncBackoff = "VANGUARD_RUNTIME_SYNC_BACKOFF_STATUS";
    public const string RaidReviewIdentityGuard = "VANGUARD_RAIDREVIEW_IDENTITY_GUARD_STATUS";
    public const string ExtractGuardOneShot = "VANGUARD_EXTRACT_GUARD_ONE_SHOT_STATUS";
    public const string MedicalDeferredEpisode = "VANGUARD_MEDICAL_DEFERRED_EPISODE_STATUS";
}
