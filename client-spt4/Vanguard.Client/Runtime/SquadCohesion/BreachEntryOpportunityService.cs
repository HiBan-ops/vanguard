#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Vanguard.Client.Diagnostics;

// Responsibility: Resolves a bounded post-portal entry anchor for an already-ready multi-axis DirectCommit when a confirmed HardChoke lies on that Direct member's proven route, including a shut structural door only when the same live NavMeshDoorLink is proven eligible for SAIN-native traversal.
// Flow: Exact SplitFlank FullAlt/pressure truth plus current RouteConvergence geometry are intersected with a confirmed structural doorway that is already open/breached or explicitly eligible for SAIN-native closed-door traversal; the resolver projects the current Direct member and portal onto the same proven route and proposes a short anchor just beyond the portal.
// Authority boundary: this service is planning/advisory only. It never opens a scheduler window, issues movement, changes a door, chooses aim/fire/target, releases HeldAccess or creates a separate tactical intent; DirectCommit remains the only physical writer.
// Invariant: entry requires current synchronized FullAlt pressure, a confirmed executable HardChoke, the selected Direct member still upstream of the portal, a bounded post-portal translation and safe remaining route distance to the threat. Locked, non-operable or non-interactable closed doors remain rejected. Vanguard never interacts with the door; an admitted shut door is handled only by the already-owning SAIN BotPathDataManual executor. Any uncertainty returns NO_ENTRY and the caller may fall back to ordinary DirectCommit/SAIN.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class BreachEntryOpportunityService
{
    public const string MovementMode = "hard_choke_entry";
    public const string StatusTag = "VANGUARD_TC_BREACH_ENTRY_OPPORTUNITY_STATUS";

    internal const float PostPortalDepthMeters = 2.25f;
    internal const float MaximumEntryTranslationMeters = 9.50f;
    internal const float MinimumPostEntryThreatRemainingMeters = 6.00f;
    private const float MinimumPortalAheadMeters = 0.75f;
    private const float MaximumPortalRouteRelationMeters = 2.50f;
    private const float MaximumCurrentRouteAttachmentMeters = 3.50f;
    private const float MaximumProjectedPortalSeparationMeters = 2.50f;
    private const float MinimumEntryTranslationMeters = 1.25f;

    private static bool bootLogged;
    private static int attemptsTotal;
    private static int selectedTotal;
    private static int noHardChokeTotal;
    private static int routeMismatchTotal;
    private static int geometryRejectedTotal;

    public static bool TryResolve(
        ThreatGroupRouteAssignment assignment,
        ThreatGroupSplitFlankStateObservation splitState,
        RouteConvergenceSnapshot convergenceSnapshot,
        ThreatGroupConvergenceAssessment convergenceGroup,
        DirectRouteObservation directRoute,
        Vector3 currentPosition,
        DateTimeOffset now,
        out BreachEntryOpportunity opportunity,
        out string reason)
    {
        opportunity = BreachEntryOpportunity.Empty;
        attemptsTotal++;
        LogBootOnce();

        if (assignment == null
            || splitState == null
            || convergenceSnapshot == null
            || convergenceGroup == null
            || directRoute == null)
        {
            reason = "context_missing";
            return false;
        }
        if (!string.Equals(assignment.OwnerProfileId, splitState.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(assignment.ThreatId, splitState.ThreatId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(assignment.AlternateCandidateId, splitState.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(assignment.SelectedAltBotProfileId, splitState.AlternateBotProfileId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "assignment_split_state_identity_mismatch";
            return false;
        }
        if (!splitState.SynchronizedCommitReady
            || !splitState.FullAltEstablished
            || !splitState.PositionEstablishedObserved
            || !splitState.PressureProofObserved
            || splitState.DirectCommitCycleCompletedObserved)
        {
            reason = "synchronized_pressure_window_not_ready";
            return false;
        }
        if (!assignment.DirectBotProfileIds.Contains(directRoute.BotProfileId, StringComparer.OrdinalIgnoreCase)
            || !string.Equals(directRoute.PathStatus, "complete", StringComparison.OrdinalIgnoreCase)
            || directRoute.Corners == null
            || directRoute.Corners.Count < 2)
        {
            reason = "direct_route_not_current_or_complete";
            return false;
        }
        if (!string.Equals(convergenceGroup.OwnerProfileId, assignment.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(convergenceGroup.ThreatId, assignment.ThreatId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "convergence_identity_mismatch";
            return false;
        }
        if (!ChokeDetectionService.TryGetSnapshot(out ChokeDetectionSnapshot chokeSnapshot))
        {
            noHardChokeTotal++;
            reason = "choke_snapshot_unavailable";
            return false;
        }
        if (chokeSnapshot.TopologyRevision != convergenceSnapshot.TopologyRevision
            || !string.Equals(chokeSnapshot.MapId, convergenceSnapshot.MapId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(chokeSnapshot.TopologyContentHash, convergenceSnapshot.TopologyContentHash, StringComparison.OrdinalIgnoreCase)
            || chokeSnapshot.ConvergencePublishedAtUtc != convergenceSnapshot.PublishedAtUtc)
        {
            noHardChokeTotal++;
            reason = "choke_snapshot_not_exact_current_convergence_generation";
            return false;
        }

        if (!TryProjectOnPolyline(directRoute.Corners, currentPosition, out float currentProgress, out float currentSeparation, out _, out float routeLength)
            || currentSeparation > MaximumCurrentRouteAttachmentMeters)
        {
            routeMismatchTotal++;
            reason = "current_position_not_attached_to_direct_route";
            return false;
        }

        BreachEntryOpportunity? best = null;
        foreach (ChokeObservation choke in chokeSnapshot.Chokes
            .Where(item => item.IsHardChoke
                && item.IsConfirmed
                && item.StructuralDoor?.IsConfirmed == true
                && string.Equals(item.OwnerProfileId, assignment.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ThreatId, assignment.ThreatId, StringComparison.OrdinalIgnoreCase)))
        {
            ChokeDoorEvidence door = choke.StructuralDoor!;
            bool openOrBreached = string.Equals(
                door.RouteExecutability,
                "structural_passage_open_or_breached",
                StringComparison.OrdinalIgnoreCase);
            bool nativeClosedTraversal = string.Equals(
                    door.RouteExecutability,
                    "structural_passage_closed_native_traversal_required",
                    StringComparison.OrdinalIgnoreCase)
                && door.NativeTraversalEligible;
            if ((!openOrBreached && !nativeClosedTraversal)
                || door.RouteRelationDistanceMeters > MaximumPortalRouteRelationMeters)
            {
                continue;
            }
            if (!TryProjectOnPolyline(directRoute.Corners, door.PortalCenter, out float portalProgress, out float portalSeparation, out Vector3 projectedPortal, out _)
                || portalSeparation > MaximumProjectedPortalSeparationMeters)
            {
                continue;
            }

            float portalAhead = portalProgress - currentProgress;
            if (portalAhead < MinimumPortalAheadMeters)
            {
                continue;
            }
            float entryProgress = portalProgress + PostPortalDepthMeters;
            float translation = entryProgress - currentProgress;
            float remainingToThreat = routeLength - entryProgress;
            if (translation < MinimumEntryTranslationMeters
                || translation > MaximumEntryTranslationMeters
                || remainingToThreat < MinimumPostEntryThreatRemainingMeters
                || entryProgress >= routeLength)
            {
                geometryRejectedTotal++;
                continue;
            }

            Vector3 anchor = PointAlongPath(directRoute.Corners, entryProgress);
            var candidate = new BreachEntryOpportunity(
                assignment.OwnerProfileId,
                assignment.ThreatId,
                assignment.AlternateCandidateId,
                assignment.SelectedAltBotProfileId,
                directRoute.OperatorId,
                directRoute.BotProfileId,
                choke.ChokeId,
                door.DoorId,
                door.DoorState,
                nativeClosedTraversal,
                door.PortalCenter,
                projectedPortal,
                anchor,
                currentProgress,
                portalProgress,
                entryProgress,
                translation,
                remainingToThreat,
                portalSeparation,
                splitState.PressureProofReason,
                now);

            if (best == null || candidate.PortalAheadMeters < best.PortalAheadMeters)
            {
                best = candidate;
            }
        }

        if (best == null)
        {
            noHardChokeTotal++;
            reason = "no_admissible_traversable_hard_choke_ahead";
            return false;
        }

        selectedTotal++;
        opportunity = best;
        reason = "breach_entry_opportunity_selected";
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_BREACH_ENTRY_OPPORTUNITY_SELECTED owner={Safe(best.OwnerProfileId)}; threat={Safe(best.ThreatId)}; axis={Safe(best.AlternateCandidateId)}; altBot={Safe(best.AlternateBotProfileId)}; directBot={Safe(best.BotProfileId)}; choke={Safe(best.ChokeId)}; door={Safe(best.DoorId)}; doorState={Safe(best.DoorState)}; nativeDoorTraversalRequired={best.NativeDoorTraversalRequired}; portalAhead={best.PortalAheadMeters:0.00}; entryDepth={PostPortalDepthMeters:0.00}; translation={best.TranslationMeters:0.00}; remainingThreatRoute={best.RemainingToThreatMeters:0.00}; portalRouteSeparation={best.PortalRouteSeparationMeters:0.00}; pressure={Safe(best.PressureProofReason)}; attemptsTotal={attemptsTotal}; selectedTotal={selectedTotal}; noHardChokeTotal={noHardChokeTotal}; routeMismatchTotal={routeMismatchTotal}; geometryRejectedTotal={geometryRejectedTotal}; movementMode={MovementMode}; vanguardDoorInteraction=false; nativeSainDoorTraversal={best.NativeDoorTraversalRequired}; roomSemanticModel=false; fireCommand=false; aimCommand=false; targetMutation=false; physicalWriter=DirectCommit");
        return true;
    }


    internal static bool ValidatePreparedEntryPath(
        BreachEntryOpportunity opportunity,
        IReadOnlyList<Vector3> preparedCorners,
        out string reason)
    {
        if (opportunity == null || preparedCorners == null || preparedCorners.Count < 2)
        {
            reason = "prepared_entry_path_missing";
            return false;
        }
        if (!TryProjectOnPolyline(preparedCorners, opportunity.PortalCenter, out float portalProgress, out float portalSeparation, out _, out float pathLength)
            || portalSeparation > MaximumProjectedPortalSeparationMeters)
        {
            reason = "fresh_entry_path_does_not_cross_confirmed_portal";
            return false;
        }
        float postPortalDistance = pathLength - portalProgress;
        if (portalProgress < MinimumPortalAheadMeters
            || postPortalDistance < Math.Max(1.0f, PostPortalDepthMeters * 0.60f))
        {
            reason = "fresh_entry_path_does_not_finish_beyond_portal";
            return false;
        }
        reason = "fresh_entry_path_crosses_confirmed_portal";
        return true;
    }

    public static void ResetForRaidLifecycle(string reason)
    {
        bootLogged = false;
        attemptsTotal = 0;
        selectedTotal = 0;
        noHardChokeTotal = 0;
        routeMismatchTotal = 0;
        geometryRejectedTotal = 0;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_BREACH_ENTRY_OPPORTUNITY_RESET reason={Safe(reason)}; stateCleared=true; movementStart=false; vanguardDoorInteraction=false; targetMutation=false");
    }

    private static void LogBootOnce()
    {
        if (bootLogged)
        {
            return;
        }
        bootLogged = true;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_BREACH_ENTRY_OPPORTUNITY_BOOT enabled=true; source=confirmed_HardChoke_plus_current_DirectRoute_plus_FullAlt_pressure; postPortalDepth={PostPortalDepthMeters:0.00}; maxTranslation={MaximumEntryTranslationMeters:0.00}; minThreatRemaining={MinimumPostEntryThreatRemainingMeters:0.00}; portalRouteRelationMax={MaximumPortalRouteRelationMeters:0.00}; openOrBreachedOrNativeTraversableShutDoor=true; lockedOrNonOperableClosedDoor=false; vanguardDoorInteraction=false; nativeDoorInteractionOwner=SAIN_BotPathDataManual; roomSemanticModel=false; physicalWriter=DirectCommit; targetMutation=false; aimCommand=false; fireCommand=false");
    }

    private static bool TryProjectOnPolyline(
        IReadOnlyList<Vector3> corners,
        Vector3 point,
        out float progressMeters,
        out float separationMeters,
        out Vector3 projectedPoint,
        out float routeLengthMeters)
    {
        progressMeters = 0f;
        separationMeters = float.PositiveInfinity;
        projectedPoint = corners != null && corners.Count > 0 ? corners[0] : Vector3.zero;
        routeLengthMeters = 0f;
        if (corners == null || corners.Count < 2)
        {
            return false;
        }

        float cumulative = 0f;
        for (int index = 1; index < corners.Count; index++)
        {
            Vector3 a = corners[index - 1];
            Vector3 b = corners[index];
            Vector3 segment = b - a;
            float length = segment.magnitude;
            if (length <= 0.001f)
            {
                continue;
            }
            float t = Mathf.Clamp01(Vector3.Dot(point - a, segment) / (length * length));
            Vector3 projection = a + segment * t;
            float separation = Vector3.Distance(point, projection);
            if (separation < separationMeters)
            {
                separationMeters = separation;
                progressMeters = cumulative + length * t;
                projectedPoint = projection;
            }
            cumulative += length;
        }
        routeLengthMeters = cumulative;
        return routeLengthMeters > 0.01f && !float.IsInfinity(separationMeters);
    }

    private static Vector3 PointAlongPath(IReadOnlyList<Vector3> corners, float distanceMeters)
    {
        if (corners == null || corners.Count == 0)
        {
            return Vector3.zero;
        }
        if (corners.Count == 1 || distanceMeters <= 0f)
        {
            return corners[0];
        }

        float remaining = distanceMeters;
        for (int index = 1; index < corners.Count; index++)
        {
            Vector3 a = corners[index - 1];
            Vector3 b = corners[index];
            float segment = Vector3.Distance(a, b);
            if (segment <= 0.001f)
            {
                continue;
            }
            if (remaining <= segment)
            {
                return Vector3.Lerp(a, b, remaining / segment);
            }
            remaining -= segment;
        }
        return corners[corners.Count - 1];
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

internal sealed class BreachEntryOpportunity
{
    public static readonly BreachEntryOpportunity Empty = new(
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
        string.Empty, string.Empty, string.Empty, false, Vector3.zero, Vector3.zero, Vector3.zero,
        0f, 0f, 0f, 0f, 0f, 0f, string.Empty, DateTimeOffset.MinValue);

    public BreachEntryOpportunity(
        string ownerProfileId,
        string threatId,
        string alternateCandidateId,
        string alternateBotProfileId,
        string operatorId,
        string botProfileId,
        string chokeId,
        string doorId,
        string doorState,
        bool nativeDoorTraversalRequired,
        Vector3 portalCenter,
        Vector3 projectedPortal,
        Vector3 entryAnchor,
        float currentProgressMeters,
        float portalProgressMeters,
        float entryProgressMeters,
        float translationMeters,
        float remainingToThreatMeters,
        float portalRouteSeparationMeters,
        string pressureProofReason,
        DateTimeOffset observedAtUtc)
    {
        OwnerProfileId = ownerProfileId;
        ThreatId = threatId;
        AlternateCandidateId = alternateCandidateId;
        AlternateBotProfileId = alternateBotProfileId;
        OperatorId = operatorId;
        BotProfileId = botProfileId;
        ChokeId = chokeId;
        DoorId = doorId;
        DoorState = doorState;
        NativeDoorTraversalRequired = nativeDoorTraversalRequired;
        PortalCenter = portalCenter;
        ProjectedPortal = projectedPortal;
        EntryAnchor = entryAnchor;
        CurrentProgressMeters = currentProgressMeters;
        PortalProgressMeters = portalProgressMeters;
        EntryProgressMeters = entryProgressMeters;
        TranslationMeters = translationMeters;
        RemainingToThreatMeters = remainingToThreatMeters;
        PortalRouteSeparationMeters = portalRouteSeparationMeters;
        PressureProofReason = pressureProofReason;
        ObservedAtUtc = observedAtUtc;
    }

    public string OwnerProfileId { get; }
    public string ThreatId { get; }
    public string AlternateCandidateId { get; }
    public string AlternateBotProfileId { get; }
    public string OperatorId { get; }
    public string BotProfileId { get; }
    public string ChokeId { get; }
    public string DoorId { get; }
    public string DoorState { get; }
    public bool NativeDoorTraversalRequired { get; }
    public Vector3 PortalCenter { get; }
    public Vector3 ProjectedPortal { get; }
    public Vector3 EntryAnchor { get; }
    public float CurrentProgressMeters { get; }
    public float PortalProgressMeters { get; }
    public float EntryProgressMeters { get; }
    public float TranslationMeters { get; }
    public float RemainingToThreatMeters { get; }
    public float PortalRouteSeparationMeters { get; }
    public string PressureProofReason { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public float PortalAheadMeters => PortalProgressMeters - CurrentProgressMeters;
}
#endif
