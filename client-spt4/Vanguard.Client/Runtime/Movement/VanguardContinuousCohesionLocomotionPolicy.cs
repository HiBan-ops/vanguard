#if SPT_CLIENT
using System;

// Responsibility: Encodes the deterministic rules for Continuous Cohesion Locomotion Policy within the movement/cohesion runtime.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Runtime.Movement;

/// <summary>
/// The runtime isolates the locomotion semantics of an active travel corridor from the distance bands
/// used to select pace. A TravelCohesionFollowThrough command is a continuous pursuit command:
/// its destination may advance, but the backend must not brake at every intermediate anchor and
/// must never be reset merely because the Operator entered CatchUp or EmergencyCatchUp.
/// </summary>
internal static class VanguardContinuousCohesionLocomotionPolicy
{
    public const string StatusTag = "VANGUARD_CONTINUOUS_COHESION_LOCOMOTION_STATUS";
    public const string AtomicDeploymentStatusTag = "VANGUARD_CONTINUOUS_PURSUIT_ATOMIC_DEPLOYMENT_STATUS";
    public const string SeamlessAuthorityContinuityStatusTag = "VANGUARD_SEAMLESS_AUTHORITY_CONTINUITY_STATUS";

    public static bool IsContinuousTravelRequest(string? requestKind)
        => string.Equals(
            requestKind?.Trim(),
            VanguardMovementContractPolicy.TravelCohesionFollowThrough,
            StringComparison.OrdinalIgnoreCase);

    public static float ResolveTravelReachDistance(float anchorRadiusMeters)
    {
        // The corridor executor retargets before the intermediate anchor is consumed. Keep the
        // physical reach radius deliberately small so the Mover does not consider a far-behind
        // Operator finished while route debt still exists.
        return Math.Max(0.65f, Math.Min(1.25f, anchorRadiusMeters * 0.20f));
    }

    public static float ResolveTargetMoveSpeed(bool sprint)
        => sprint ? 1.25f : 1.0f;

    public static TimeSpan MissingPathReissueCooldown => TimeSpan.FromSeconds(0.75d);

    public static float ContinuousBlockedConfirmationSeconds => 8.0f;

    public static float ContinuousNoProgressSeconds => 12.0f;

    // Claims may be prepared after a brief still period, but Travel is not released until the
    // stop is deliberate and a current stationary claim has a complete path. This absorbs inventory,
    // doorway and observation micro-pauses without destroying the continuous pursuit lease.
    public static float ObservationDeploymentOwnerStillSeconds => 0.75f;

    public static float ObservationDeploymentCommitStillSeconds => 2.25f;

    public static int CohesionNavMeshPathsPerFrame => 3;

    // NavMesh.CalculatePath may return a useful partial path. A partial endpoint is accepted only
    // as a temporary bridge when it gives substantial progress toward the desired corridor anchor.
    public static float PartialPathBridgeMinimumPathMeters => 3.50f;

    public static float PartialPathBridgeMinimumGoalGainMeters => 2.50f;

    public static float PartialPathBridgeMinimumAnchorDeltaMeters => 2.25f;

    public static float PartialPathBridgeRetrySeconds => 0.75f;

    public static float PartialPathBridgeMaxFormationPathMeters => 34.0f;

    public static float PartialPathBridgeMaxCatchUpPathMeters => 58.0f;

    public static float PartialPathBridgeMaxEmergencyPathMeters => 86.0f;

    // Block/no-progress time is accumulated only across contiguous physical samples. A long frame
    // or deferred tick breaks the proof instead of being misclassified as continuous immobilization.
    public static float LivenessMaximumContiguousSampleGapSeconds => 1.35f;

    // Generic stationary cohesion slots are tactical positions, not broad proximity bands. Keep
    // their completion radius close enough to produce visible front/lateral deployment while still
    // tolerating EFT agent avoidance and imperfect NavMesh samples.
    public static float ObservationDeploymentAnchorRadiusMeters => 2.75f;

    public static float ObservationDeploymentFallbackAnchorRadiusMeters => 3.25f;

    public static float ObservationDeploymentClaimToleranceMeters => 0.85f;

    // Travel owns the final approach. A stationary claim may take over only when the Operator is
    // already close enough that the authority boundary cannot create a second visible locomotion leg.
    public static float ObservationDeploymentTravelHandoffMeters => 5.25f;

    public static float ObservationDeploymentApproachRetargetMeters => 0.85f;

    public static float StationaryClaimOwnerResumeSpeedMetersPerSecond => 0.55f;

    public static float StationaryClaimOwnerResumeSampleMeters => 0.65f;

    public static float StationaryClaimOwnerResumeConfirmSeconds => 0.45f;

    public static float ClaimObservedRestartSeconds => 3.0f;

    public static float ClaimObservedBlockedTerminalSeconds => 6.0f;

    public static float ClaimObservedNoProgressTerminalSeconds => 9.0f;

    public static float ClaimReplanCooldownSeconds => 0.75f;
}
#endif
