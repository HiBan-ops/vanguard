#if SPT_CLIENT
using System;
using System.Globalization;
using UnityEngine;

// Responsibility: Decides whether an active movement command is producing real physical progress instead of being fooled by a moving owner, anchor or destination.
// Flow: Consecutive world/anchor/bubble/destination distances plus speed/path facts are compared, then one progress classification and diagnostic reason is returned to the movement lease logic.
// Authority boundary: Evaluation is read-only; it reports progress/stall evidence but never refreshes, retargets or cancels a movement lease itself.
// Invariant: Only actual Operator displacement can prove execution progress; owner movement or anchor retargeting alone cannot keep a blocked lease alive.
namespace Vanguard.Client.Runtime.Movement;

/// <summary>
/// Centralizes movement diagnostics and physical progress classification. Anchor, bubble and
/// destination deltas remain context only; execution progress requires Operator world displacement.
/// This keeps owner movement and anchor retargets from refreshing a blocked movement lease.
/// </summary>
internal static class VanguardMovementProgressEvaluator
{
    public static VanguardMovementProgressEvaluation Evaluate(
        float previousAnchorDistance,
        float currentAnchorDistance,
        float previousBubbleDistance,
        float currentBubbleDistance,
        float? previousDestinationDistance,
        float? currentDestinationDistance,
        float realSpeed,
        float pathDistanceMeters,
        TimeSpan sinceLastProgress)
    {
        float anchorGain = previousAnchorDistance - currentAnchorDistance;
        float bubbleGain = previousBubbleDistance - currentBubbleDistance;
        float destinationGain = 0f;
        if (previousDestinationDistance.HasValue && currentDestinationDistance.HasValue)
        {
            destinationGain = previousDestinationDistance.Value - currentDestinationDistance.Value;
        }

        bool anchorProgress = anchorGain >= 1.25f;
        bool bubbleProgress = bubbleGain >= 1.25f;
        bool destinationProgress = destinationGain >= 1.25f;
        // Runtime invariant: animation speed is intent, not physical progress. The old speed-only branch
        // could keep a blocked command alive while the bot walked against geometry. Hard-return
        // callers retain cumulative distance/path gains; movement executors that need finer-grained
        // evidence use EvaluatePhysical below.
        bool hasProgress = anchorProgress || bubbleProgress || destinationProgress;

        string kind = bubbleProgress
            ? "bubble_distance_improved"
            : anchorProgress
                ? "anchor_distance_improved"
                : destinationProgress
                    ? "destination_distance_improved"
                    : "none";

        string noProgressReason = "anchorGain=" + anchorGain.ToString("0.00", CultureInfo.InvariantCulture)
            + ";bubbleGain=" + bubbleGain.ToString("0.00", CultureInfo.InvariantCulture)
            + ";destinationGain=" + destinationGain.ToString("0.00", CultureInfo.InvariantCulture)
            + ";speed=" + realSpeed.ToString("0.00", CultureInfo.InvariantCulture)
            + ";pathDist=" + pathDistanceMeters.ToString("0.00", CultureInfo.InvariantCulture)
            + ";sinceProgress=" + sinceLastProgress.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);

        return new VanguardMovementProgressEvaluation(hasProgress, kind, noProgressReason, anchorGain, bubbleGain, destinationGain);
    }
    public static VanguardPhysicalMovementProgressEvaluation EvaluatePhysical(
        Vector3 previousWorldPosition,
        Vector3 currentWorldPosition,
        float previousGoalDistance,
        float currentGoalDistance,
        float realSpeed,
        bool movementExpected,
        TimeSpan sampleAge)
    {
        float worldDelta = HorizontalDistance(previousWorldPosition, currentWorldPosition);
        float goalGain = previousGoalDistance - currentGoalDistance;
        bool goalProgress = goalGain >= 0.35f;
        // The runtime physical truth: an anchor/goal can approach a stationary Operator because the owner
        // moved or the target was retargeted. Only world displacement by the Operator is progress.
        bool physicalProgress = worldDelta >= 0.20f && currentGoalDistance <= previousGoalDistance + 0.75f;
        bool hasProgress = physicalProgress;
        bool locomotionIntent = movementExpected || realSpeed > 0.55f;
        bool blocked = locomotionIntent
            && worldDelta < 0.15f
            && goalGain < 0.10f
            && sampleAge >= TimeSpan.FromSeconds(0.45d);
        string kind = physicalProgress && goalProgress
            ? "world_and_goal_advanced"
            : physicalProgress
                ? "world_position_advanced"
                : blocked
                    ? "locomotion_blocked_world_delta"
                    : "none";
        string summary = "worldDelta=" + worldDelta.ToString("0.00", CultureInfo.InvariantCulture)
            + ";goalGain=" + goalGain.ToString("0.00", CultureInfo.InvariantCulture)
            + ";speed=" + realSpeed.ToString("0.00", CultureInfo.InvariantCulture)
            + ";movementExpected=" + (movementExpected ? "true" : "false")
            + ";sampleAge=" + sampleAge.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);
        return new VanguardPhysicalMovementProgressEvaluation(hasProgress, blocked, kind, summary, worldDelta, goalGain);
    }


    public static VanguardTravelPhysicalLivenessEvaluation EvaluateTravelPhysicalLiveness(
        Vector3 progressOriginPosition,
        Vector3 previousSamplePosition,
        Vector3 currentWorldPosition,
        float accumulatedTravelMeters,
        float progressOriginRouteMeters,
        float currentRouteMeters,
        float progressOriginGoalDistance,
        float currentGoalDistance,
        float realSpeed,
        bool movementExpected,
        TimeSpan sampleAge,
        TimeSpan sinceMeaningfulProgress)
    {
        float sampleDelta = HorizontalDistance(previousSamplePosition, currentWorldPosition);
        float netDisplacement = HorizontalDistance(progressOriginPosition, currentWorldPosition);
        float routeGain = currentRouteMeters - progressOriginRouteMeters;
        float goalGain = progressOriginGoalDistance - currentGoalDistance;
        bool sampleReady = sampleAge >= TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TravelPhysicalSampleSeconds);
        bool meaningfulDisplacement = netDisplacement >= VanguardMovementAuthorityDoctrine.TravelPhysicalMeaningfulDisplacementMeters
            || (accumulatedTravelMeters >= VanguardMovementAuthorityDoctrine.TravelPhysicalCurvedPathTravelMeters
                && netDisplacement >= VanguardMovementAuthorityDoctrine.TravelPhysicalCurvedPathNetMeters);
        bool corridorAdvance = routeGain >= VanguardMovementAuthorityDoctrine.TravelPhysicalMeaningfulRouteGainMeters
            || goalGain >= VanguardMovementAuthorityDoctrine.TravelPhysicalMeaningfulGoalGainMeters
            || currentGoalDistance <= progressOriginGoalDistance + 1.25f;
        bool hasProgress = sampleReady && meaningfulDisplacement && corridorAdvance;
        bool jitterOnly = sampleReady
            && sampleDelta < VanguardMovementAuthorityDoctrine.TravelPhysicalJitterMeters
            && netDisplacement < VanguardMovementAuthorityDoctrine.TravelPhysicalBlockedNetDisplacementMeters;
        bool blocked = movementExpected
            && sampleReady
            && sinceMeaningfulProgress >= TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TravelPhysicalBlockedDetectSeconds)
            && !meaningfulDisplacement;

        string kind = hasProgress && routeGain >= VanguardMovementAuthorityDoctrine.TravelPhysicalMeaningfulRouteGainMeters
            ? "world_and_route_advanced"
            : hasProgress && goalGain >= VanguardMovementAuthorityDoctrine.TravelPhysicalMeaningfulGoalGainMeters
                ? "world_and_goal_advanced"
                : hasProgress
                    ? "world_displacement_advanced"
                    : blocked
                        ? "travel_physical_liveness_blocked"
                        : jitterOnly
                            ? "jitter_only"
                            : "none";
        string summary = "sampleDelta=" + sampleDelta.ToString("0.00", CultureInfo.InvariantCulture)
            + ";netDisplacement=" + netDisplacement.ToString("0.00", CultureInfo.InvariantCulture)
            + ";accumulatedTravel=" + accumulatedTravelMeters.ToString("0.00", CultureInfo.InvariantCulture)
            + ";routeGain=" + routeGain.ToString("0.00", CultureInfo.InvariantCulture)
            + ";goalGain=" + goalGain.ToString("0.00", CultureInfo.InvariantCulture)
            + ";speed=" + realSpeed.ToString("0.00", CultureInfo.InvariantCulture)
            + ";movementExpected=" + (movementExpected ? "true" : "false")
            + ";sampleAge=" + sampleAge.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + ";sinceMeaningful=" + sinceMeaningfulProgress.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);
        return new VanguardTravelPhysicalLivenessEvaluation(
            hasProgress,
            blocked,
            jitterOnly,
            kind,
            summary,
            sampleDelta,
            netDisplacement,
            accumulatedTravelMeters,
            routeGain,
            goalGain);
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        float dx = left.x - right.x;
        float dz = left.z - right.z;
        return Mathf.Sqrt((dx * dx) + (dz * dz));
    }

}

internal readonly struct VanguardMovementProgressEvaluation
{
    public VanguardMovementProgressEvaluation(bool hasProgress, string progressKind, string noProgressReason, float anchorGainMeters, float bubbleGainMeters, float destinationGainMeters)
    {
        HasProgress = hasProgress;
        ProgressKind = progressKind;
        NoProgressReason = noProgressReason;
        AnchorGainMeters = anchorGainMeters;
        BubbleGainMeters = bubbleGainMeters;
        DestinationGainMeters = destinationGainMeters;
    }

    public bool HasProgress { get; }
    public string ProgressKind { get; }
    public string NoProgressReason { get; }
    public float AnchorGainMeters { get; }
    public float BubbleGainMeters { get; }
    public float DestinationGainMeters { get; }
}

internal readonly struct VanguardPhysicalMovementProgressEvaluation
{
    public VanguardPhysicalMovementProgressEvaluation(bool hasProgress, bool locomotionBlocked, string progressKind, string summary, float worldDeltaMeters, float goalGainMeters)
    {
        HasProgress = hasProgress;
        LocomotionBlocked = locomotionBlocked;
        ProgressKind = progressKind;
        Summary = summary;
        WorldDeltaMeters = worldDeltaMeters;
        GoalGainMeters = goalGainMeters;
    }

    public bool HasProgress { get; }
    public bool LocomotionBlocked { get; }
    public string ProgressKind { get; }
    public string Summary { get; }
    public float WorldDeltaMeters { get; }
    public float GoalGainMeters { get; }
}

internal readonly struct VanguardTravelPhysicalLivenessEvaluation
{
    public VanguardTravelPhysicalLivenessEvaluation(
        bool hasProgress,
        bool locomotionBlocked,
        bool jitterOnly,
        string progressKind,
        string summary,
        float sampleDeltaMeters,
        float netDisplacementMeters,
        float accumulatedTravelMeters,
        float routeGainMeters,
        float goalGainMeters)
    {
        HasProgress = hasProgress;
        LocomotionBlocked = locomotionBlocked;
        JitterOnly = jitterOnly;
        ProgressKind = progressKind;
        Summary = summary;
        SampleDeltaMeters = sampleDeltaMeters;
        NetDisplacementMeters = netDisplacementMeters;
        AccumulatedTravelMeters = accumulatedTravelMeters;
        RouteGainMeters = routeGainMeters;
        GoalGainMeters = goalGainMeters;
    }

    public bool HasProgress { get; }
    public bool LocomotionBlocked { get; }
    public bool JitterOnly { get; }
    public string ProgressKind { get; }
    public string Summary { get; }
    public float SampleDeltaMeters { get; }
    public float NetDisplacementMeters { get; }
    public float AccumulatedTravelMeters { get; }
    public float RouteGainMeters { get; }
    public float GoalGainMeters { get; }
}
#endif
