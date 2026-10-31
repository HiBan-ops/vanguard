#if SPT_CLIENT
using Vanguard.Client.Diagnostics;

// Responsibility: Clears every non-executor Tactical Coordination publication and influence state at an authoritative raid boundary.
// Flow: The raid controller releases AltDevelop/DirectCommit physical executors first, then this service drains residual spacing/support influence and clears the split-flank lifecycle from downstream to upstream so the next raid cannot consume prior-raid tactical truth.
// Authority boundary: This reset may only release existing TC influence/state. It never starts movement, selects a target, mutates SAIN, creates a route assignment or carries tactical continuity across raids.
// Invariant: A new raid begins with no AntiConga/CommitSupport gate, no RouteAssignment/SplitFlank/TacticalReplan publication and no prior-raid assessment/convergence/choke/route-independence/exposure snapshot available for reacquisition.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class TacticalCoordinationRaidLifecycleService
{
    public const string StatusTag = "VANGUARD_TC_RAID_LIFECYCLE_STATUS";

    public static void ResetForRaidLifecycle(string reason)
    {
        string lifecycleReason = "raid_lifecycle:" + Safe(reason);

        // Release bounded influence before clearing planning truth. The dedicated AltDevelop and
        // DirectCommit executors are reset by the raid controller immediately before this call.
        TacticalIntentContinuityService.ResetForRaidLifecycle(lifecycleReason);
        MultiThreatOffensiveSafetyPolicy.ResetForRaidLifecycle(lifecycleReason);
        BreachEntryOpportunityService.ResetForRaidLifecycle(lifecycleReason);
        MultiAxisAssaultService.ResetForRaidLifecycle(lifecycleReason);
        TacticalOpportunityMovementService.ResetForRaidLifecycle(lifecycleReason);
        ChokeCommitSupportService.Reset(lifecycleReason);
        AntiCongaService.Reset(lifecycleReason);

        // Clear downstream lifecycle first. SplitFlankReadiness cascades through SplitFlankState
        // into TacticalReplan classifier state; reset the executor explicitly so this service remains
        // safe and self-contained even if a future caller does not pass through AltDevelop reset first.
        TacticalReplanExecutionService.Reset(lifecycleReason);
        SplitFlankReadinessService.Reset(lifecycleReason);
        ThreatExposureShadowClassificationService.Reset(lifecycleReason);
        ThreatExposureAssignmentAlignmentService.Reset(lifecycleReason);
        TacticalFitnessShadowService.Reset(lifecycleReason);
        RouteAssignmentService.Reset(lifecycleReason);

        // Finally clear the observation/planning producers. This prevents change-driven services from
        // retaining a prior-raid publication while fresh Operator decision snapshots are still empty.
        ThreatExposureObservationService.Reset(lifecycleReason);
        RouteIndependenceService.Reset(lifecycleReason);
        ChokeDetectionService.Reset(lifecycleReason);
        RouteConvergenceService.Reset(lifecycleReason);
        TacticalAssessmentService.Reset(lifecycleReason);

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"TC_RAID_LIFECYCLE_RESET reason={Safe(reason)}; residualInfluenceReleased=true; tacticalIntentContinuityCleared=true; multiThreatOffensiveSafetyCleared=true; breachEntryOpportunityCleared=true; multiAxisAssaultCleared=true; tacticalOpportunityCleared=true; splitFlankCleared=true; tacticalReplanCleared=true; routeAssignmentCleared=true; tacticalFitnessShadowCleared=true; threatExposureShadowCleared=true; assignmentAlignedExposureCleared=true; upstreamPlanningCleared=true; threatExposureCleared=true; priorRaidContinuity=false; movementStart=false; targetMutation=false; sainMutation=false");
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
