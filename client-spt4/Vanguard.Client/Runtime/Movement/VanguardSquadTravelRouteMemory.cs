#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Maintains short-lived route/progress memory for squad travel so Operators can continue useful movement across ticks without replanning every frame.
// Flow: Accepted travel plans record anchors, path/progress evidence, retries and failure reasons; subsequent ticks reuse valid memory, advance it on progress and invalidate it when geometry, authority or destination truth changes.
// Authority boundary: Route memory is an optimization/state aid, not movement authority; executors must still own the active lease and validate the current path before driving the bot.
// Invariant: All entries are raid/Operator scoped, stale or repeatedly failing routes are discarded, and memory cannot force movement after the underlying intent is gone.
namespace Vanguard.Client.Runtime.Movement;

/// <summary>
/// Read-only owner locomotion truth exported by the canonical NavMesh route memory.
/// Stationary admission consumers must use this snapshot instead of deriving a second
/// movement timer from replicated Transform deltas.
/// </summary>
internal readonly struct VanguardOwnerTravelStationaryState
{
    public VanguardOwnerTravelStationaryState(
        string ownerProfileId,
        Vector3 sampledPosition,
        DateTimeOffset observedAtUtc,
        DateTimeOffset stationarySinceUtc,
        float stationarySeconds,
        bool ownerMovingRecently,
        int routeEpoch,
        long routeVersion)
    {
        OwnerProfileId = ownerProfileId ?? string.Empty;
        SampledPosition = sampledPosition;
        ObservedAtUtc = observedAtUtc;
        StationarySinceUtc = stationarySinceUtc;
        StationarySeconds = stationarySeconds;
        OwnerMovingRecently = ownerMovingRecently;
        RouteEpoch = routeEpoch;
        RouteVersion = routeVersion;
    }

    public string OwnerProfileId { get; }
    public Vector3 SampledPosition { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public DateTimeOffset StationarySinceUtc { get; }
    public float StationarySeconds { get; }
    public bool OwnerMovingRecently { get; }
    public int RouteEpoch { get; }
    public long RouteVersion { get; }
}

/// <summary>
/// The runtime canonical travel route memory.
///
/// The owner route is an append-only logical sequence of NavMesh breadcrumbs. Operators advance
/// monotonically through that sequence. Geographic U-turns by the player are represented by new
/// breadcrumbs at a higher route progress; an Operator is therefore never sent back to an obsolete
/// breadcrumb merely because the player looked or rotated in another direction.
///
/// This service observes and resolves route targets only. The travel executor remains the sole
/// movement authority and the command store remains the sole BigBrain handoff.
/// </summary>
internal static class VanguardSquadTravelRouteMemory
{
    public const string StatusTag = "VANGUARD_MONOTONIC_TRAVEL_CORRIDOR_STATUS";
    public const string ReacquireStatusTag = "VANGUARD_RECENT_CORRIDOR_REACQUIRE_STATUS";
    public const string PostInterruptionReconciliationStatusTag = "VANGUARD_POST_INTERRUPTION_CORRIDOR_RECONCILIATION_STATUS";

    private const float BreadcrumbSpacingMeters = 1.75f;
    private const float TurnBreadcrumbMinMeters = 0.85f;
    private const float TurnBreadcrumbDegrees = 22.0f;
    private const float VerticalBreadcrumbMeters = 1.10f;
    private const float OwnerMovementEpsilonMeters = 0.40f;
    private const float OwnerTeleportResetMeters = 42.0f;
    private const float RetainedRouteMeters = 900.0f;
    private const int MaxRouteNodes = 420;
    private const float CursorBacktrackToleranceMeters = 1.25f;
    private const float FormationProjectionAheadMeters = 48.0f;
    private const float CatchUpProjectionAheadMeters = 90.0f;
    private const float EmergencyProjectionAheadMeters = 165.0f;
    private const float FormationCaptureRadiusMeters = 12.0f;
    private const float CatchUpCaptureRadiusMeters = 20.0f;
    private const float EmergencyCaptureRadiusMeters = 32.0f;
    private const float FormationLookAheadMeters = 15.0f;
    private const float CatchUpLookAheadMeters = 31.0f;
    private const float EmergencyLookAheadMeters = 52.0f;
    private const float MinimumTargetAdvanceMeters = 1.5f;
    private const float CursorProjectionSampleSeconds = 0.35f;
    private const float CursorWorldJitterMeters = 0.18f;
    private const float CursorProgressSlackMeters = 0.75f;
    private const float CursorProgressWorldScale = 1.75f;
    private const float FormationLateralOffsetMeters = 2.4f;
    private const float NarrowLateralOffsetMeters = 1.25f;
    private const float RouteSampleRadiusMeters = 2.25f;
    private const float ProjectionVerticalToleranceMeters = 2.75f;
    private const float SampleVerticalToleranceMeters = 2.25f;
    private static readonly TimeSpan RouteFreshness = TimeSpan.FromSeconds(2.5d);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(4.0d);

    private static readonly object Sync = new();
    private static readonly Dictionary<string, OwnerRouteState> RouteByOwnerProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, OperatorRouteCursor> CursorByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogByKey = new(StringComparer.OrdinalIgnoreCase);

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            RouteByOwnerProfileId.Clear();
            CursorByBotProfileId.Clear();
            LastLogByKey.Clear();
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_TRAVEL_ROUTE_RESET reason={Safe(reason)}; owners=cleared; cursors=cleared; doctrine=append_only_owner_breadcrumbs_monotonic_operator_progress_no_transform_forward_anchor; tag={StatusTag}");
    }

    public static void Update(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        if (snapshots == null || snapshots.Count == 0)
        {
            return;
        }

        var owners = snapshots
            .Where(snapshot => snapshot != null
                && snapshot.Alive
                && !string.IsNullOrWhiteSpace(snapshot.OwnerProfileId)
                && snapshot.SquadCohesion.OwnerKnown
                && snapshot.SquadCohesion.OwnerReliableForActiveMovement
                && snapshot.SquadCohesion.OwnerPosition.HasValue)
            .GroupBy(snapshot => snapshot.OwnerProfileId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        foreach (var snapshot in owners)
        {
            ObserveOwner(snapshot, now);
        }
    }

    public static bool TryGetOwnerStationaryState(
        string ownerProfileId,
        DateTimeOffset now,
        out VanguardOwnerTravelStationaryState state,
        out string reason)
    {
        state = default;
        reason = "none";
        if (string.IsNullOrWhiteSpace(ownerProfileId))
        {
            reason = "owner_profile_missing";
            return false;
        }

        lock (Sync)
        {
            if (!RouteByOwnerProfileId.TryGetValue(ownerProfileId, out var route))
            {
                reason = "owner_route_missing";
                return false;
            }

            if (now - route.LastValidSampleAtUtc > RouteFreshness)
            {
                reason = "owner_route_stale";
                return false;
            }

            DateTimeOffset stationarySinceUtc = route.LastMovementAtUtc == DateTimeOffset.MinValue
                ? route.CreatedAtUtc
                : route.LastMovementAtUtc;
            float stationarySeconds = Math.Max(0f, (float)(now - stationarySinceUtc).TotalSeconds);
            bool ownerMovingRecently = route.LastMovementAtUtc != DateTimeOffset.MinValue
                && now - route.LastMovementAtUtc <= TimeSpan.FromSeconds(1.25d);
            state = new VanguardOwnerTravelStationaryState(
                route.OwnerProfileId,
                route.LastObservedPosition,
                route.LastValidSampleAtUtc,
                stationarySinceUtc,
                stationarySeconds,
                ownerMovingRecently,
                route.Epoch,
                route.Version);
            reason = "canonical_owner_motion:epoch=" + route.Epoch.ToString(CultureInfo.InvariantCulture)
                + ":version=" + route.Version.ToString(CultureInfo.InvariantCulture)
                + ":moving=" + (ownerMovingRecently ? "true" : "false")
                + ":stationary=" + stationarySeconds.ToString("0.00", CultureInfo.InvariantCulture);
            return true;
        }
    }

    public static bool IsRouteUsable(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.OwnerProfileId))
        {
            reason = "owner_profile_missing";
            return false;
        }

        lock (Sync)
        {
            if (!RouteByOwnerProfileId.TryGetValue(snapshot.OwnerProfileId, out var route))
            {
                reason = "owner_route_missing";
                return false;
            }

            if (route.Nodes.Count < 2 || route.OwnerProgressMeters - route.Nodes[0].ProgressMeters < BreadcrumbSpacingMeters)
            {
                reason = "owner_route_not_established";
                return false;
            }

            if (now - route.LastValidSampleAtUtc > RouteFreshness)
            {
                reason = "owner_route_stale";
                return false;
            }

            reason = "route_usable:epoch=" + route.Epoch.ToString(CultureInfo.InvariantCulture)
                + ":version=" + route.Version.ToString(CultureInfo.InvariantCulture)
                + ":progress=" + route.OwnerProgressMeters.ToString("0.0", CultureInfo.InvariantCulture);
            return true;
        }
    }

    public static bool ShouldDriveTravel(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (snapshot == null
            || !snapshot.Alive
            || string.IsNullOrWhiteSpace(snapshot.OwnerProfileId)
            || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            reason = "snapshot_owner_or_bot_unavailable";
            return false;
        }

        lock (Sync)
        {
            if (!RouteByOwnerProfileId.TryGetValue(snapshot.OwnerProfileId, out var route)
                || route.Nodes.Count < 2
                || now - route.LastValidSampleAtUtc > RouteFreshness)
            {
                reason = "owner_route_unavailable";
                return false;
            }

            bool ownerMoving = route.LastMovementAtUtc != DateTimeOffset.MinValue
                && now - route.LastMovementAtUtc <= TimeSpan.FromSeconds(1.25d);
            if (!ownerMoving)
            {
                reason = "owner_not_travelling";
                return false;
            }

            OperatorRouteCursor cursor;
            if (!CursorByBotProfileId.TryGetValue(snapshot.BotProfileId, out cursor)
                || cursor.RouteEpoch != route.Epoch
                || !string.Equals(cursor.OwnerProfileId, route.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
            {
                cursor = InitializeCursor(route, snapshot, snapshot.Position, now);
            }

            string mode = ResolveMode(ref cursor, snapshot.SquadCohesion.OperatorDistanceToOwner, now, out var modeReason);
            cursor = AdvanceCursor(route, cursor, snapshot.Position, mode, now);
            CursorByBotProfileId[snapshot.BotProfileId] = cursor;
            LogModeTransition(snapshot, cursor, modeReason, now);
            float longitudinalOffset = ResolveLongitudinalOffset(cursor, mode);
            float desiredSlotProgress = Math.Max(route.Nodes[0].ProgressMeters, route.OwnerProgressMeters - longitudinalOffset);
            float progressGap = Math.Max(0f, desiredSlotProgress - cursor.ProgressMeters);
            float distanceEnvelope = longitudinalOffset + ResolveAnchorRadius(mode) + 3.0f;
            bool requiresMovement = progressGap >= MinimumTargetAdvanceMeters
                || snapshot.SquadCohesion.OperatorDistanceToOwner > distanceEnvelope;
            if (!requiresMovement)
            {
                reason = "operator_inside_travel_corridor:epoch=" + route.Epoch.ToString(CultureInfo.InvariantCulture)
                    + ":operatorProgress=" + cursor.ProgressMeters.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":desiredProgress=" + desiredSlotProgress.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":ownerDistance=" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", CultureInfo.InvariantCulture);
                return false;
            }

            reason = "monotonic_travel_required:epoch=" + route.Epoch.ToString(CultureInfo.InvariantCulture)
                + ":mode=" + mode
                + ":operatorProgress=" + cursor.ProgressMeters.ToString("0.0", CultureInfo.InvariantCulture)
                + ":desiredProgress=" + desiredSlotProgress.ToString("0.0", CultureInfo.InvariantCulture)
                + ":progressGap=" + progressGap.ToString("0.0", CultureInfo.InvariantCulture)
                + ":ownerDistance=" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", CultureInfo.InvariantCulture);
            return true;
        }
    }

    public static bool TryResolveTarget(
        OperatorDecisionSnapshot snapshot,
        Vector3 botPosition,
        DateTimeOffset now,
        out VanguardTravelRouteTarget target)
        => TryResolveTargetCore(snapshot, botPosition, now, null, false, out target);

    public static bool TryResolveBoundedTarget(
        OperatorDecisionSnapshot snapshot,
        Vector3 botPosition,
        DateTimeOffset now,
        float maxTargetProgressMeters,
        out VanguardTravelRouteTarget target)
        => TryResolveTargetCore(snapshot, botPosition, now, maxTargetProgressMeters, false, out target);

    public static bool TryResolvePhysicalRecoveryTarget(
        OperatorDecisionSnapshot snapshot,
        Vector3 botPosition,
        DateTimeOffset now,
        out VanguardTravelRouteTarget target)
        => TryResolveTargetCore(snapshot, botPosition, now, null, true, out target);

    /// <summary>
    /// The runtime inverse stale-cursor reconciliation. This is admission-only and side-effect
    /// free: it handles the post-interruption case where the Operator is physically near the owner,
    /// while the monotonic cursor still points far behind and the normal next target would send the
    /// Operator back through obsolete route history. The executor must validate a complete path and
    /// confirm the exact movement command before committing the cursor advance.
    /// </summary>
    public static bool TryResolvePostInterruptionReconciliationTarget(
        OperatorDecisionSnapshot snapshot,
        Vector3 botPosition,
        DateTimeOffset now,
        double travelAuthorityGapSeconds,
        out VanguardTravelRouteTarget target,
        out bool reconciliationRequired)
    {
        reconciliationRequired = false;
        target = VanguardTravelRouteTarget.Invalid("post_interruption_reconciliation_not_evaluated");
        if (snapshot == null
            || string.IsNullOrWhiteSpace(snapshot.OwnerProfileId)
            || string.IsNullOrWhiteSpace(snapshot.BotProfileId)
            || !snapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            target = VanguardTravelRouteTarget.Invalid("post_interruption_reconciliation_snapshot_owner_or_bot_missing");
            return false;
        }

        lock (Sync)
        {
            if (!RouteByOwnerProfileId.TryGetValue(snapshot.OwnerProfileId, out var route)
                || route.Nodes.Count < 2
                || now - route.LastValidSampleAtUtc > RouteFreshness)
            {
                target = VanguardTravelRouteTarget.Invalid("post_interruption_reconciliation_owner_route_unavailable");
                return false;
            }

            if (!CursorByBotProfileId.TryGetValue(snapshot.BotProfileId, out var cursor)
                || cursor.RouteEpoch != route.Epoch
                || !string.Equals(cursor.OwnerProfileId, route.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
            {
                target = VanguardTravelRouteTarget.Invalid("post_interruption_reconciliation_cursor_unavailable");
                return false;
            }

            if (travelAuthorityGapSeconds < VanguardMovementAuthorityDoctrine.TravelPostInterruptionReconcileMinimumPauseSeconds)
            {
                target = VanguardTravelRouteTarget.Invalid("post_interruption_reconciliation_requires_real_authority_gap:pause="
                    + travelAuthorityGapSeconds.ToString("0.0", CultureInfo.InvariantCulture));
                return false;
            }

            float ownerDistance = snapshot.SquadCohesion.OperatorDistanceToOwner;
            if (ownerDistance > VanguardMovementAuthorityDoctrine.TravelPostInterruptionReconcileMaximumOwnerDistanceMeters)
            {
                target = VanguardTravelRouteTarget.Invalid("post_interruption_reconciliation_operator_not_physically_close:ownerDistance="
                    + ownerDistance.ToString("0.0", CultureInfo.InvariantCulture));
                return false;
            }

            string mode = cursor.TravelMode;
            if (ownerDistance >= VanguardMovementAuthorityDoctrine.HardCorrectionMeters)
            {
                mode = VanguardTravelRouteModes.EmergencyCatchUp;
            }
            else if (ownerDistance >= VanguardMovementAuthorityDoctrine.TravelCatchUpEnterMeters
                && string.Equals(mode, VanguardTravelRouteModes.FormationTravel, StringComparison.OrdinalIgnoreCase))
            {
                mode = VanguardTravelRouteModes.CatchUp;
            }

            float longitudinalOffset = ResolveLongitudinalOffset(cursor, mode);
            float desiredSlotProgress = Math.Max(route.Nodes[0].ProgressMeters, route.OwnerProgressMeters - longitudinalOffset);
            float routeDebt = Math.Max(0f, desiredSlotProgress - cursor.ProgressMeters);
            if (routeDebt < VanguardMovementAuthorityDoctrine.TravelPostInterruptionReconcileMinimumDebtMeters)
            {
                target = VanguardTravelRouteTarget.Invalid("post_interruption_reconciliation_debt_below_threshold:debt="
                    + routeDebt.ToString("0.0", CultureInfo.InvariantCulture));
                return false;
            }

            float staleTargetProgress = Math.Min(desiredSlotProgress, cursor.ProgressMeters + ResolveLookAhead(mode));
            staleTargetProgress = Math.Max(cursor.ProgressMeters, staleTargetProgress);
            if (!TryPointAndTangentAtProgress(route, staleTargetProgress, out var staleCenter, out _))
            {
                target = VanguardTravelRouteTarget.Invalid("post_interruption_reconciliation_stale_target_projection_failed");
                return false;
            }

            float staleAnchorDistance = HorizontalDistance(botPosition, staleCenter);
            float ownerRelativeDivergence = staleAnchorDistance - ownerDistance;
            if (staleAnchorDistance < VanguardMovementAuthorityDoctrine.TravelPostInterruptionReconcileMinimumStaleAnchorDistanceMeters
                || ownerRelativeDivergence < VanguardMovementAuthorityDoctrine.TravelPostInterruptionReconcileMinimumOwnerRelativeDivergenceMeters)
            {
                target = VanguardTravelRouteTarget.Invalid("post_interruption_reconciliation_stale_geometry_not_dangerous:staleDistance="
                    + staleAnchorDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":ownerRelative=" + ownerRelativeDivergence.ToString("0.0", CultureInfo.InvariantCulture));
                return false;
            }

            // From this point onward, falling back to the stale target would reproduce the proven
            // regression. If no recent candidate can be validated, the executor must defer Travel
            // admission and retry without mutating the cursor.
            reconciliationRequired = true;

            float recentWindowStart = Math.Max(
                route.Nodes[0].ProgressMeters,
                route.OwnerProgressMeters - VanguardMovementAuthorityDoctrine.TravelPostInterruptionReconcileRecentWindowMeters);
            if (!TryProjectMostRecentProgress(
                    route,
                    botPosition,
                    recentWindowStart,
                    route.OwnerProgressMeters,
                    VanguardMovementAuthorityDoctrine.TravelPostInterruptionReconcileProjectionCaptureMeters,
                    out var recentPhysicalProgress,
                    out var recentProjectionDistance))
            {
                target = VanguardTravelRouteTarget.Invalid("post_interruption_reconciliation_recent_projection_failed");
                return false;
            }

            if (recentProjectionDistance > VanguardMovementAuthorityDoctrine.TravelPostInterruptionReconcileProjectionCaptureMeters)
            {
                target = VanguardTravelRouteTarget.Invalid("post_interruption_reconciliation_recent_projection_too_far:distance="
                    + recentProjectionDistance.ToString("0.0", CultureInfo.InvariantCulture));
                return false;
            }

            if (recentPhysicalProgress
                < desiredSlotProgress - VanguardMovementAuthorityDoctrine.TravelPostInterruptionReconcileMaximumBehindSlotMeters)
            {
                target = VanguardTravelRouteTarget.Invalid("post_interruption_reconciliation_projection_not_recent_enough:projected="
                    + recentPhysicalProgress.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":desired=" + desiredSlotProgress.ToString("0.0", CultureInfo.InvariantCulture));
                return false;
            }

            // The cursor is a physical truth, not a desired-slot debt counter. Reconcile it to the
            // newest plausible physical projection even when that projection is slightly ahead of
            // the nominal formation slot. Normal Travel will simply hold that monotonic progress
            // until the owner advances; it must never send the Operator backward to repay spacing.
            float targetProgress = Math.Max(cursor.ProgressMeters + MinimumTargetAdvanceMeters, recentPhysicalProgress);
            targetProgress = Math.Min(route.OwnerProgressMeters, targetProgress);
            if (targetProgress <= cursor.ProgressMeters + MinimumTargetAdvanceMeters)
            {
                target = VanguardTravelRouteTarget.Invalid("post_interruption_reconciliation_candidate_not_ahead");
                return false;
            }

            if (!TryPointAndTangentAtProgress(route, targetProgress, out var center, out var tangent))
            {
                target = VanguardTravelRouteTarget.Invalid("post_interruption_reconciliation_target_projection_failed");
                return false;
            }

            // Reconciliation starts on the route spine. Formation lateral offset is restored by the
            // normal active Travel resolver after the cursor and movement command have converged.
            if (!TrySampleAnchor(center, center, 0f, out var anchor, out var sampleReason))
            {
                target = VanguardTravelRouteTarget.Invalid("post_interruption_reconciliation_anchor_sample_failed:" + sampleReason);
                return false;
            }

            float candidateDistance = HorizontalDistance(botPosition, anchor);
            float anchorImprovement = staleAnchorDistance - candidateDistance;
            if (anchorImprovement < VanguardMovementAuthorityDoctrine.TravelPostInterruptionReconcileMinimumAnchorImprovementMeters
                || candidateDistance > VanguardMovementAuthorityDoctrine.TravelPostInterruptionReconcileMaximumCandidateDistanceMeters)
            {
                target = VanguardTravelRouteTarget.Invalid("post_interruption_reconciliation_geometry_not_divergent:staleDistance="
                    + staleAnchorDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":candidateDistance=" + candidateDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":improvement=" + anchorImprovement.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":ownerRelative=" + ownerRelativeDivergence.ToString("0.0", CultureInfo.InvariantCulture));
                return false;
            }

            Vector3 ownerDirection = Flatten(snapshot.SquadCohesion.OwnerPosition.Value - botPosition);
            Vector3 staleDirection = Flatten(staleCenter - botPosition);
            float staleOwnerDot = ownerDirection.sqrMagnitude <= 0.01f || staleDirection.sqrMagnitude <= 0.01f
                ? 0f
                : Vector3.Dot(ownerDirection.normalized, staleDirection.normalized);
            float stationarySeconds = route.LastMovementAtUtc == DateTimeOffset.MinValue
                ? Math.Max(0f, (float)(now - route.CreatedAtUtc).TotalSeconds)
                : Math.Max(0f, (float)(now - route.LastMovementAtUtc).TotalSeconds);
            bool ownerMoving = route.LastMovementAtUtc != DateTimeOffset.MinValue
                && now - route.LastMovementAtUtc <= TimeSpan.FromSeconds(1.25d);

            target = new VanguardTravelRouteTarget(
                true,
                anchor,
                center,
                tangent,
                route.Epoch,
                route.Version,
                targetProgress,
                route.OwnerProgressMeters,
                targetProgress,
                mode,
                0f,
                ResolveAnchorRadius(mode),
                ownerMoving,
                stationarySeconds,
                true,
                sampleReason,
                "accepted_post_interruption_corridor_reconciliation:previousProgress="
                    + cursor.ProgressMeters.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":recentProjection=" + recentPhysicalProgress.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":recentProjectionDistance=" + recentProjectionDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":routeDebt=" + routeDebt.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":staleTargetProgress=" + staleTargetProgress.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":staleAnchorDistance=" + staleAnchorDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":candidateDistance=" + candidateDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":staleOwnerDot=" + staleOwnerDot.ToString("0.00", CultureInfo.InvariantCulture)
                    + ":authorityGap=" + travelAuthorityGapSeconds.ToString("0.0", CultureInfo.InvariantCulture));
            return true;
        }
    }

    /// <summary>
    /// The runtime controlled travel reacquisition. This does not mutate the cursor. It proposes a
    /// recent route-spine anchor only when the Operator has accumulated a large monotonic route
    /// debt after another primary authority (combat/medical). The travel executor must validate a
    /// complete NavMesh path before committing the cursor jump.
    /// </summary>
    public static bool TryResolveRecentReacquireTarget(
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        float setbackMeters,
        double travelAuthorityGapSeconds,
        out VanguardTravelRouteTarget target)
    {
        target = VanguardTravelRouteTarget.Invalid("recent_reacquire_not_evaluated");
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.OwnerProfileId) || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            target = VanguardTravelRouteTarget.Invalid("recent_reacquire_snapshot_owner_or_bot_missing");
            return false;
        }

        lock (Sync)
        {
            if (!RouteByOwnerProfileId.TryGetValue(snapshot.OwnerProfileId, out var route)
                || route.Nodes.Count < 2
                || now - route.LastValidSampleAtUtc > RouteFreshness)
            {
                target = VanguardTravelRouteTarget.Invalid("recent_reacquire_owner_route_unavailable");
                return false;
            }

            if (!CursorByBotProfileId.TryGetValue(snapshot.BotProfileId, out var cursor)
                || cursor.RouteEpoch != route.Epoch
                || !string.Equals(cursor.OwnerProfileId, route.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
            {
                target = VanguardTravelRouteTarget.Invalid("recent_reacquire_cursor_unavailable");
                return false;
            }

            // Authority gap is supplied by the Travel executor's execution-only heartbeat. Route
            // cursor timestamps are intentionally ignored here because scoring and target resolution
            // also project the cursor and must never fabricate or erase an authority interruption.
            if (travelAuthorityGapSeconds < VanguardMovementAuthorityDoctrine.TravelRecentReacquireMinimumPauseSeconds)
            {
                target = VanguardTravelRouteTarget.Invalid("recent_reacquire_requires_real_authority_gap:pause="
                    + travelAuthorityGapSeconds.ToString("0.0", CultureInfo.InvariantCulture));
                return false;
            }

            // Candidate probing remains side-effect free. Resolve only the urgency escalation implied
            // by current distance; do not mutate the stored mode or cursor before command confirmation.
            string mode = cursor.TravelMode;
            if (snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.HardCorrectionMeters)
            {
                mode = VanguardTravelRouteModes.EmergencyCatchUp;
            }
            else if (snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.TravelCatchUpEnterMeters
                && string.Equals(mode, VanguardTravelRouteModes.FormationTravel, StringComparison.OrdinalIgnoreCase))
            {
                mode = VanguardTravelRouteModes.CatchUp;
            }

            bool catchUpMode = string.Equals(mode, VanguardTravelRouteModes.CatchUp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, VanguardTravelRouteModes.EmergencyCatchUp, StringComparison.OrdinalIgnoreCase);
            float longitudinalOffset = ResolveLongitudinalOffset(cursor, mode);
            float desiredSlotProgress = Math.Max(route.Nodes[0].ProgressMeters, route.OwnerProgressMeters - longitudinalOffset);
            float routeDebt = Math.Max(0f, desiredSlotProgress - cursor.ProgressMeters);
            if (!catchUpMode
                || snapshot.SquadCohesion.OperatorDistanceToOwner < VanguardMovementAuthorityDoctrine.TravelRecentReacquireOwnerDistanceMeters
                || routeDebt < VanguardMovementAuthorityDoctrine.TravelRecentReacquireMinimumDebtMeters)
            {
                target = VanguardTravelRouteTarget.Invalid("recent_reacquire_not_required:mode=" + mode
                    + ":ownerDistance=" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":routeDebt=" + routeDebt.ToString("0.0", CultureInfo.InvariantCulture));
                return false;
            }

            float boundedSetback = Math.Max(0f, Math.Min(VanguardMovementAuthorityDoctrine.TravelRecentReacquireMaxSetbackMeters, setbackMeters));
            float targetProgress = Math.Max(cursor.ProgressMeters + MinimumTargetAdvanceMeters, desiredSlotProgress - boundedSetback);
            targetProgress = Math.Min(desiredSlotProgress, targetProgress);
            if (targetProgress <= cursor.ProgressMeters + MinimumTargetAdvanceMeters)
            {
                target = VanguardTravelRouteTarget.Invalid("recent_reacquire_candidate_not_ahead");
                return false;
            }

            if (!TryPointAndTangentAtProgress(route, targetProgress, out var center, out var tangent))
            {
                target = VanguardTravelRouteTarget.Invalid("recent_reacquire_projection_failed");
                return false;
            }

            // Reacquisition always targets the real route spine. Formation lanes are restored only
            // after the Operator has converged back into normal FormationTravel.
            if (!TrySampleAnchor(center, center, 0f, out var anchor, out var sampleReason))
            {
                target = VanguardTravelRouteTarget.Invalid("recent_reacquire_anchor_sample_failed:" + sampleReason);
                return false;
            }

            float stationarySeconds = route.LastMovementAtUtc == DateTimeOffset.MinValue
                ? Math.Max(0f, (float)(now - route.CreatedAtUtc).TotalSeconds)
                : Math.Max(0f, (float)(now - route.LastMovementAtUtc).TotalSeconds);
            bool ownerMoving = route.LastMovementAtUtc != DateTimeOffset.MinValue
                && now - route.LastMovementAtUtc <= TimeSpan.FromSeconds(1.25d);
            target = new VanguardTravelRouteTarget(
                true,
                anchor,
                center,
                tangent,
                route.Epoch,
                route.Version,
                targetProgress,
                route.OwnerProgressMeters,
                targetProgress,
                mode,
                0f,
                ResolveAnchorRadius(mode),
                ownerMoving,
                stationarySeconds,
                true,
                sampleReason,
                "accepted_recent_corridor_reacquire:setback=" + boundedSetback.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":previousProgress=" + cursor.ProgressMeters.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":routeDebt=" + routeDebt.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":authorityGap=" + travelAuthorityGapSeconds.ToString("0.0", CultureInfo.InvariantCulture));
            return true;
        }
    }

    /// <summary>
    /// Commits a previously path-validated recent route anchor. The cursor remains monotonic and
    /// may only move forward within the same owner route epoch.
    /// </summary>
    public static bool TryCommitRecentReacquire(
        OperatorDecisionSnapshot snapshot,
        VanguardTravelRouteTarget target,
        Vector3 botPosition,
        DateTimeOffset now,
        out string reason)
        => TryCommitAdmissionCursorAdvance(
            snapshot,
            target,
            botPosition,
            now,
            "accepted_recent_corridor_reacquire",
            "recent_reacquire",
            "VANGUARD_RECENT_CORRIDOR_REACQUIRE_COMMITTED",
            ReacquireStatusTag,
            out reason);

    public static bool TryCommitPostInterruptionReconciliation(
        OperatorDecisionSnapshot snapshot,
        VanguardTravelRouteTarget target,
        Vector3 botPosition,
        DateTimeOffset now,
        out string reason)
        => TryCommitAdmissionCursorAdvance(
            snapshot,
            target,
            botPosition,
            now,
            "accepted_post_interruption_corridor_reconciliation",
            "post_interruption_reconciliation",
            "VANGUARD_POST_INTERRUPTION_CORRIDOR_RECONCILIATION_COMMITTED",
            PostInterruptionReconciliationStatusTag,
            out reason);

    private static bool TryCommitAdmissionCursorAdvance(
        OperatorDecisionSnapshot snapshot,
        VanguardTravelRouteTarget target,
        Vector3 botPosition,
        DateTimeOffset now,
        string expectedReasonPrefix,
        string reasonPrefix,
        string commitEventName,
        string statusTag,
        out string reason)
    {
        reason = "none";
        if (snapshot == null
            || !target.Valid
            || !target.Reason.StartsWith(expectedReasonPrefix, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(snapshot.OwnerProfileId)
            || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            reason = reasonPrefix + "_invalid_snapshot_or_target";
            return false;
        }

        lock (Sync)
        {
            if (!RouteByOwnerProfileId.TryGetValue(snapshot.OwnerProfileId, out var route)
                || route.Epoch != target.RouteEpoch
                || target.TargetProgressMeters > route.OwnerProgressMeters + 0.75f)
            {
                reason = reasonPrefix + "_route_epoch_or_progress_changed";
                return false;
            }

            if (!CursorByBotProfileId.TryGetValue(snapshot.BotProfileId, out var cursor)
                || cursor.RouteEpoch != route.Epoch
                || !string.Equals(cursor.OwnerProfileId, route.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
            {
                cursor = InitializeCursor(route, snapshot, botPosition, now);
            }

            float previous = cursor.ProgressMeters;
            cursor.ProgressMeters = Math.Max(previous, Math.Min(route.OwnerProgressMeters, target.TargetProgressMeters));
            cursor.LastPhysicalPosition = botPosition;
            cursor.LastPhysicalProjectionAtUtc = now;
            CursorByBotProfileId[snapshot.BotProfileId] = cursor;
            reason = reasonPrefix + "_committed:previous=" + previous.ToString("0.0", CultureInfo.InvariantCulture)
                + ":committed=" + cursor.ProgressMeters.ToString("0.0", CultureInfo.InvariantCulture)
                + ":epoch=" + route.Epoch.ToString(CultureInfo.InvariantCulture);
            LogThrottled(reasonPrefix + "Commit|" + snapshot.BotProfileId, now,
                $"{commitEventName} kind={Safe(reasonPrefix)}; operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; previousProgress={previous:0.00}; committedProgress={cursor.ProgressMeters:0.00}; ownerProgress={route.OwnerProgressMeters:0.00}; ownerDistance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; target={Safe(target.Summary)}; pathValidatedBeforeCommit=true; exactCommandConfirmed=true; monotonic=true; historicalDebtForgiven=true; tag={statusTag}; corridorTag={StatusTag}");
            return true;
        }
    }

    private static bool TryResolveTargetCore(
        OperatorDecisionSnapshot snapshot,
        Vector3 botPosition,
        DateTimeOffset now,
        float? maxTargetProgressMeters,
        bool recoverySpine,
        out VanguardTravelRouteTarget target)
    {
        target = VanguardTravelRouteTarget.Invalid("not_evaluated");
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.OwnerProfileId) || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            target = VanguardTravelRouteTarget.Invalid("snapshot_owner_or_bot_missing");
            return false;
        }

        lock (Sync)
        {
            if (!RouteByOwnerProfileId.TryGetValue(snapshot.OwnerProfileId, out var route)
                || route.Nodes.Count < 2
                || now - route.LastValidSampleAtUtc > RouteFreshness)
            {
                target = VanguardTravelRouteTarget.Invalid("owner_route_unavailable");
                return false;
            }

            if (!CursorByBotProfileId.TryGetValue(snapshot.BotProfileId, out var cursor)
                || cursor.RouteEpoch != route.Epoch
                || !string.Equals(cursor.OwnerProfileId, route.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
            {
                cursor = InitializeCursor(route, snapshot, botPosition, now);
            }

            // Normal target resolution owns the physical monotonic cursor update. This must remain
            // active during a running Travel lease: otherwise the cursor freezes at an old breadcrumb,
            // creates artificial route debt and repeatedly reopens CatchUp windows.
            string mode = ResolveMode(ref cursor, snapshot.SquadCohesion.OperatorDistanceToOwner, now, out var modeReason);
            cursor = AdvanceCursor(route, cursor, botPosition, mode, now);
            CursorByBotProfileId[snapshot.BotProfileId] = cursor;
            LogModeTransition(snapshot, cursor, modeReason, now);

            float ownerProgress = route.OwnerProgressMeters;
            float longitudinalOffset = ResolveLongitudinalOffset(cursor, mode);
            float desiredSlotProgress = Math.Max(route.Nodes[0].ProgressMeters, ownerProgress - longitudinalOffset);
            float lookAhead = recoverySpine ? ResolveRecoveryLookAhead(mode) : ResolveLookAhead(mode);
            float targetProgress = Math.Min(desiredSlotProgress, cursor.ProgressMeters + lookAhead);
            targetProgress = Math.Max(cursor.ProgressMeters, targetProgress);

            bool ownerMoving = route.LastMovementAtUtc != DateTimeOffset.MinValue
                && now - route.LastMovementAtUtc <= TimeSpan.FromSeconds(1.25d);
            if (ownerMoving && desiredSlotProgress > cursor.ProgressMeters + MinimumTargetAdvanceMeters)
            {
                targetProgress = Math.Max(targetProgress, Math.Min(desiredSlotProgress, cursor.ProgressMeters + MinimumTargetAdvanceMeters));
            }

            if (maxTargetProgressMeters.HasValue)
            {
                float boundedMaximum = Math.Max(cursor.ProgressMeters, maxTargetProgressMeters.Value);
                targetProgress = Math.Min(targetProgress, boundedMaximum);
            }

            if (!TryPointAndTangentAtProgress(route, targetProgress, out var center, out var tangent))
            {
                target = VanguardTravelRouteTarget.Invalid("route_target_projection_failed");
                return false;
            }

            float lateralOffset = recoverySpine ? 0f : ResolveLateralOffset(snapshot, cursor, mode);
            Vector3 lateral = new(-tangent.z, 0f, tangent.x);
            Vector3 rawAnchor = center + lateral * lateralOffset;
            if (!TrySampleAnchor(rawAnchor, center, lateralOffset, out var anchor, out var sampleReason))
            {
                target = VanguardTravelRouteTarget.Invalid("route_anchor_sample_failed:" + sampleReason);
                return false;
            }

            float stationarySeconds = route.LastMovementAtUtc == DateTimeOffset.MinValue
                ? Math.Max(0f, (float)(now - route.CreatedAtUtc).TotalSeconds)
                : Math.Max(0f, (float)(now - route.LastMovementAtUtc).TotalSeconds);
            bool requiresMovement = targetProgress >= cursor.ProgressMeters + MinimumTargetAdvanceMeters
                || HorizontalDistance(botPosition, anchor) > ResolveAnchorRadius(mode) + 1.0f;
            string reason = recoverySpine
                ? "accepted_physical_recovery_spine_target"
                : maxTargetProgressMeters.HasValue
                    ? "accepted_bounded_monotonic_route_target"
                    : "accepted_monotonic_route_target";

            target = new VanguardTravelRouteTarget(
                true,
                anchor,
                center,
                tangent,
                route.Epoch,
                route.Version,
                cursor.ProgressMeters,
                ownerProgress,
                targetProgress,
                mode,
                lateralOffset,
                ResolveAnchorRadius(mode),
                ownerMoving,
                stationarySeconds,
                requiresMovement,
                sampleReason,
                reason);
            return true;
        }
    }

    private static void ObserveOwner(OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        Vector3 rawPosition = snapshot.SquadCohesion.OwnerPosition!.Value;
        bool sampledOk = TrySampleOwnerPosition(rawPosition, out var sampledPosition, out var sampleReason);

        lock (Sync)
        {
            if (!sampledOk)
            {
                if (RouteByOwnerProfileId.TryGetValue(snapshot.OwnerProfileId, out var existing))
                {
                    existing.LastRawObservedPosition = rawPosition;
                    existing.LastObservedAtUtc = now;
                    existing.ConsecutiveSampleFailures++;
                    RouteByOwnerProfileId[snapshot.OwnerProfileId] = existing;
                    LogThrottled("routeOwnerSampleFailed|" + snapshot.OwnerProfileId, now,
                        $"VANGUARD_OWNER_BREADCRUMB_REJECTED owner={Safe(snapshot.OwnerProfileId)}; raw={FormatVector(rawPosition)}; lastValid={FormatVector(existing.LastObservedPosition)}; failures={existing.ConsecutiveSampleFailures}; reason={Safe(sampleReason)}; rawBreadcrumbAppended=false; routeFresh={(now - existing.LastValidSampleAtUtc <= RouteFreshness ? "true" : "false")}; doctrine=retain_last_valid_navmesh_node_never_append_world_position; tag={StatusTag}");
                }
                else
                {
                    LogThrottled("routeOwnerSampleInitialFailed|" + snapshot.OwnerProfileId, now,
                        $"VANGUARD_OWNER_ROUTE_START_DEFERRED owner={Safe(snapshot.OwnerProfileId)}; raw={FormatVector(rawPosition)}; reason={Safe(sampleReason)}; routeCreated=false; doctrine=route_requires_valid_navmesh_sample; tag={StatusTag}");
                }
                return;
            }

            if (!RouteByOwnerProfileId.TryGetValue(snapshot.OwnerProfileId, out var route))
            {
                route = OwnerRouteState.Create(snapshot.OwnerProfileId, sampledPosition, rawPosition, now);
                RouteByOwnerProfileId[snapshot.OwnerProfileId] = route;
                LogThrottled("routeStart|" + snapshot.OwnerProfileId, now,
                    $"VANGUARD_TRAVEL_ROUTE_STARTED owner={Safe(snapshot.OwnerProfileId)}; epoch={route.Epoch}; position={FormatVector(sampledPosition)}; sample={Safe(sampleReason)}; tag={StatusTag}");
                return;
            }

            Vector3 observedDelta = sampledPosition - route.LastObservedPosition;
            float observedDistance = observedDelta.magnitude;
            if (observedDistance >= OwnerTeleportResetMeters)
            {
                int nextEpoch = route.Epoch + 1;
                route = OwnerRouteState.Create(snapshot.OwnerProfileId, sampledPosition, rawPosition, now, nextEpoch);
                RouteByOwnerProfileId[snapshot.OwnerProfileId] = route;
                RemoveOwnerCursors(snapshot.OwnerProfileId);
                LogThrottled("routeReset|" + snapshot.OwnerProfileId, now,
                    $"VANGUARD_TRAVEL_ROUTE_EPOCH_RESET owner={Safe(snapshot.OwnerProfileId)}; epoch={nextEpoch}; reason=owner_discontinuity; delta={observedDistance:0.0}; position={FormatVector(sampledPosition)}; sample={Safe(sampleReason)}; tag={StatusTag}");
                return;
            }

            if (observedDistance >= OwnerMovementEpsilonMeters)
            {
                route.LastMovementAtUtc = now;
            }

            RouteNode last = route.Nodes[route.Nodes.Count - 1];
            Vector3 nodeDelta = sampledPosition - last.Position;
            float nodeDistance = nodeDelta.magnitude;
            Vector3 horizontalDirection = Flatten(nodeDelta);
            float turnDegrees = horizontalDirection.sqrMagnitude <= 0.01f || route.SmoothedDirection.sqrMagnitude <= 0.01f
                ? 0f
                : Vector3.Angle(route.SmoothedDirection, horizontalDirection.normalized);
            bool shouldAppend = nodeDistance >= BreadcrumbSpacingMeters
                || (nodeDistance >= TurnBreadcrumbMinMeters && turnDegrees >= TurnBreadcrumbDegrees)
                || Math.Abs(nodeDelta.y) >= VerticalBreadcrumbMeters;

            if (shouldAppend && nodeDistance >= 0.20f)
            {
                float progress = last.ProgressMeters + nodeDistance;
                route.Nodes.Add(new RouteNode(sampledPosition, progress));
                route.OwnerProgressMeters = progress;
                route.Version++;
                if (horizontalDirection.sqrMagnitude > 0.01f)
                {
                    Vector3 direction = horizontalDirection.normalized;
                    route.SmoothedDirection = route.SmoothedDirection.sqrMagnitude <= 0.01f
                        ? direction
                        : Vector3.Slerp(route.SmoothedDirection, direction, 0.45f).normalized;
                }

                TrimRoute(route);
            }

            route.LastObservedPosition = sampledPosition;
            route.LastRawObservedPosition = rawPosition;
            route.LastObservedAtUtc = now;
            route.LastValidSampleAtUtc = now;
            route.ConsecutiveSampleFailures = 0;
            RouteByOwnerProfileId[snapshot.OwnerProfileId] = route;
        }
    }

    private static OperatorRouteCursor InitializeCursor(OwnerRouteState route, OperatorDecisionSnapshot snapshot, Vector3 botPosition, DateTimeOffset now)
    {
        float projected = ProjectNearestProgress(route, botPosition, route.Nodes[0].ProgressMeters, route.OwnerProgressMeters, out _);
        string initialMode = ResolveInitialMode(snapshot.SquadCohesion.OperatorDistanceToOwner);
        return new OperatorRouteCursor
        {
            OwnerProfileId = route.OwnerProfileId,
            RouteEpoch = route.Epoch,
            ProgressMeters = projected,
            FormationLongitudinalOffsetMeters = ResolveFormationLongitudinalOffset(snapshot),
            FormationLaneSign = ResolveFormationLaneSign(snapshot),
            TravelMode = initialMode,
            PendingTravelMode = string.Empty,
            PendingTravelModeSinceUtc = DateTimeOffset.MinValue,
            LastTravelModeChangeAtUtc = now,
            LastPhysicalPosition = botPosition,
            LastPhysicalProjectionAtUtc = now
        };
    }

    private static OperatorRouteCursor AdvanceCursor(OwnerRouteState route, OperatorRouteCursor cursor, Vector3 botPosition, string mode, DateTimeOffset now)
    {
        TimeSpan sampleAge = cursor.LastPhysicalProjectionAtUtc == DateTimeOffset.MinValue
            ? TimeSpan.MaxValue
            : now - cursor.LastPhysicalProjectionAtUtc;
        float worldDelta = HorizontalDistance(cursor.LastPhysicalPosition, botPosition);
        bool sampleReady = sampleAge >= TimeSpan.FromSeconds(CursorProjectionSampleSeconds)
            || worldDelta >= CursorWorldJitterMeters * 2.0f;
        if (!sampleReady)
        {
            return cursor;
        }

        float searchAhead = ResolveProjectionAhead(mode);
        float minProgress = Math.Max(route.Nodes[0].ProgressMeters, cursor.ProgressMeters - CursorBacktrackToleranceMeters);
        float maxProgress = Math.Min(route.OwnerProgressMeters, cursor.ProgressMeters + searchAhead);
        float projected = ProjectNearestProgress(route, botPosition, minProgress, maxProgress, out var lateralDistance);
        float captureRadius = ResolveCaptureRadius(mode);
        if (projected >= cursor.ProgressMeters && lateralDistance <= captureRadius)
        {
            float physicalAdvanceLimit = worldDelta < CursorWorldJitterMeters
                ? 0f
                : worldDelta * CursorProgressWorldScale + CursorProgressSlackMeters;
            cursor.ProgressMeters = Math.Min(projected, cursor.ProgressMeters + physicalAdvanceLimit);
        }

        cursor.ProgressMeters = Math.Max(route.Nodes[0].ProgressMeters, Math.Min(route.OwnerProgressMeters, cursor.ProgressMeters));
        cursor.LastPhysicalPosition = botPosition;
        cursor.LastPhysicalProjectionAtUtc = now;
        return cursor;
    }

    private static float ProjectNearestProgress(OwnerRouteState route, Vector3 point, float minProgress, float maxProgress, out float bestDistance)
    {
        float bestProgress = Math.Max(route.Nodes[0].ProgressMeters, minProgress);
        bestDistance = float.MaxValue;
        for (int index = 1; index < route.Nodes.Count; index++)
        {
            RouteNode a = route.Nodes[index - 1];
            RouteNode b = route.Nodes[index];
            if (b.ProgressMeters < minProgress || a.ProgressMeters > maxProgress)
            {
                continue;
            }

            Vector3 aFlat = Flatten(a.Position);
            Vector3 bFlat = Flatten(b.Position);
            Vector3 pFlat = Flatten(point);
            Vector3 segment = bFlat - aFlat;
            float segmentLengthSquared = segment.sqrMagnitude;
            float t = segmentLengthSquared <= 0.0001f ? 0f : Mathf.Clamp01(Vector3.Dot(pFlat - aFlat, segment) / segmentLengthSquared);
            float progress = Mathf.Lerp(a.ProgressMeters, b.ProgressMeters, t);
            if (progress < minProgress || progress > maxProgress)
            {
                continue;
            }

            Vector3 projected = Vector3.Lerp(a.Position, b.Position, t);
            float verticalDelta = Math.Abs(point.y - projected.y);
            if (verticalDelta > ProjectionVerticalToleranceMeters)
            {
                continue;
            }

            float distance = HorizontalDistance(point, projected) + verticalDelta * 1.5f;
            if (distance < bestDistance - 0.05f || (Math.Abs(distance - bestDistance) <= 0.05f && progress > bestProgress))
            {
                bestDistance = distance;
                bestProgress = progress;
            }
        }

        if (bestDistance == float.MaxValue)
        {
            bestDistance = HorizontalDistance(point, route.Nodes[0].Position);
        }

        return bestProgress;
    }

    private static bool TryProjectMostRecentProgress(
        OwnerRouteState route,
        Vector3 point,
        float minProgress,
        float maxProgress,
        float maximumProjectionDistanceMeters,
        out float projectedProgress,
        out float projectedDistance)
    {
        projectedProgress = Math.Max(route.Nodes[0].ProgressMeters, minProgress);
        projectedDistance = float.MaxValue;
        float maximumDistance = Math.Max(0f, maximumProjectionDistanceMeters);
        bool found = false;

        // Select the newest physically plausible route branch, not the geometrically nearest old
        // branch. This is deliberate: after a combat loop or U-turn the Operator can stand directly
        // on an obsolete breadcrumb while the current route spine remains only a squad-scale distance
        // away. The outer reconciliation gates (real authority gap, large debt, close owner and stale
        // target divergence) prevent this recency preference from affecting normal active Travel.
        for (int index = 1; index < route.Nodes.Count; index++)
        {
            RouteNode a = route.Nodes[index - 1];
            RouteNode b = route.Nodes[index];
            if (b.ProgressMeters < minProgress || a.ProgressMeters > maxProgress)
            {
                continue;
            }

            if (!TryProjectPointOnRouteSegment(point, a, b, minProgress, maxProgress, out var progress, out var distance)
                || distance > maximumDistance)
            {
                continue;
            }

            if (!found
                || progress > projectedProgress + 0.05f
                || (Math.Abs(progress - projectedProgress) <= 0.05f && distance < projectedDistance))
            {
                found = true;
                projectedProgress = progress;
                projectedDistance = distance;
            }
        }

        return found;
    }

    private static bool TryProjectPointOnRouteSegment(
        Vector3 point,
        RouteNode a,
        RouteNode b,
        float minProgress,
        float maxProgress,
        out float progress,
        out float distance)
    {
        progress = 0f;
        distance = float.MaxValue;
        Vector3 aFlat = Flatten(a.Position);
        Vector3 bFlat = Flatten(b.Position);
        Vector3 pFlat = Flatten(point);
        Vector3 segment = bFlat - aFlat;
        float segmentLengthSquared = segment.sqrMagnitude;
        float t = segmentLengthSquared <= 0.0001f
            ? 0f
            : Mathf.Clamp01(Vector3.Dot(pFlat - aFlat, segment) / segmentLengthSquared);
        progress = Mathf.Lerp(a.ProgressMeters, b.ProgressMeters, t);
        if (progress < minProgress || progress > maxProgress)
        {
            return false;
        }

        Vector3 projected = Vector3.Lerp(a.Position, b.Position, t);
        float verticalDelta = Math.Abs(point.y - projected.y);
        if (verticalDelta > ProjectionVerticalToleranceMeters)
        {
            return false;
        }

        distance = HorizontalDistance(point, projected) + verticalDelta * 1.5f;
        return true;
    }

    private static bool TryPointAndTangentAtProgress(OwnerRouteState route, float progress, out Vector3 point, out Vector3 tangent)
    {
        point = route.Nodes[route.Nodes.Count - 1].Position;
        tangent = route.SmoothedDirection.sqrMagnitude > 0.01f ? route.SmoothedDirection.normalized : Vector3.forward;
        if (route.Nodes.Count < 2)
        {
            return false;
        }

        float clamped = Math.Max(route.Nodes[0].ProgressMeters, Math.Min(route.OwnerProgressMeters, progress));
        for (int index = 1; index < route.Nodes.Count; index++)
        {
            RouteNode a = route.Nodes[index - 1];
            RouteNode b = route.Nodes[index];
            if (clamped > b.ProgressMeters && index < route.Nodes.Count - 1)
            {
                continue;
            }

            float span = Math.Max(0.001f, b.ProgressMeters - a.ProgressMeters);
            float t = Mathf.Clamp01((clamped - a.ProgressMeters) / span);
            point = Vector3.Lerp(a.Position, b.Position, t);
            Vector3 direction = Flatten(b.Position - a.Position);
            if (direction.sqrMagnitude > 0.01f)
            {
                tangent = direction.normalized;
            }
            return true;
        }

        return true;
    }

    private static bool TrySampleAnchor(Vector3 rawAnchor, Vector3 center, float lateralOffset, out Vector3 sampled, out string reason)
    {
        if (TrySamplePosition(rawAnchor, Math.Abs(lateralOffset) > 0.1f ? 2.6f : 2.0f, out sampled))
        {
            reason = Math.Abs(lateralOffset) > 0.1f ? "lateral_slot_sampled" : "route_spine_sampled";
            return true;
        }

        if (Math.Abs(lateralOffset) > 0.1f)
        {
            Vector3 halfway = Vector3.Lerp(center, rawAnchor, 0.5f);
            if (TrySamplePosition(halfway, 2.4f, out sampled))
            {
                reason = "lateral_slot_reduced";
                return true;
            }
        }

        if (TrySamplePosition(center, 2.5f, out sampled))
        {
            reason = "route_spine_fallback";
            return true;
        }

        sampled = Vector3.zero;
        reason = "navmesh_sample_failed";
        return false;
    }

    private static bool TrySampleOwnerPosition(Vector3 raw, out Vector3 sampled, out string reason)
    {
        float[] boundedRadii = { 0.75f, 1.50f, RouteSampleRadiusMeters };
        foreach (float radius in boundedRadii)
        {
            if (TrySamplePosition(raw, radius, out sampled))
            {
                reason = "owner_navmesh_sample_radius_" + radius.ToString("0.00", CultureInfo.InvariantCulture);
                return true;
            }
        }

        sampled = Vector3.zero;
        reason = "owner_navmesh_sample_failed_bounded";
        return false;
    }

    private static bool TrySamplePosition(Vector3 raw, float radius, out Vector3 sampled)
    {
        if (NavMesh.SamplePosition(raw + Vector3.up * 0.25f, out var hit, radius, NavMesh.AllAreas)
            && Math.Abs(hit.position.y - raw.y) <= SampleVerticalToleranceMeters)
        {
            sampled = hit.position;
            return true;
        }

        sampled = Vector3.zero;
        return false;
    }

    private static string ResolveInitialMode(float ownerDistance)
    {
        if (ownerDistance >= VanguardMovementAuthorityDoctrine.HardCorrectionMeters)
        {
            return VanguardTravelRouteModes.EmergencyCatchUp;
        }

        return ownerDistance >= VanguardMovementAuthorityDoctrine.TravelCatchUpEnterMeters
            ? VanguardTravelRouteModes.CatchUp
            : VanguardTravelRouteModes.FormationTravel;
    }

    private static string ResolveMode(ref OperatorRouteCursor cursor, float ownerDistance, DateTimeOffset now, out string reason)
    {
        string current = string.IsNullOrWhiteSpace(cursor.TravelMode)
            ? ResolveInitialMode(ownerDistance)
            : cursor.TravelMode;

        if (ownerDistance >= VanguardMovementAuthorityDoctrine.HardCorrectionMeters)
        {
            bool changed = !string.Equals(current, VanguardTravelRouteModes.EmergencyCatchUp, StringComparison.OrdinalIgnoreCase);
            cursor.TravelMode = VanguardTravelRouteModes.EmergencyCatchUp;
            cursor.PendingTravelMode = string.Empty;
            cursor.PendingTravelModeSinceUtc = DateTimeOffset.MinValue;
            if (changed) cursor.LastTravelModeChangeAtUtc = now;
            reason = changed ? "emergency_enter_immediate" : "emergency_maintained";
            return cursor.TravelMode;
        }

        string candidate = string.Empty;
        if (string.Equals(current, VanguardTravelRouteModes.EmergencyCatchUp, StringComparison.OrdinalIgnoreCase)
            && ownerDistance <= VanguardMovementAuthorityDoctrine.SoftCorrectionMeters)
        {
            candidate = VanguardTravelRouteModes.CatchUp;
        }
        else if (string.Equals(current, VanguardTravelRouteModes.FormationTravel, StringComparison.OrdinalIgnoreCase)
            && ownerDistance >= VanguardMovementAuthorityDoctrine.TravelCatchUpEnterMeters)
        {
            candidate = VanguardTravelRouteModes.CatchUp;
        }
        else if (string.Equals(current, VanguardTravelRouteModes.CatchUp, StringComparison.OrdinalIgnoreCase)
            && ownerDistance <= VanguardMovementAuthorityDoctrine.TravelCatchUpExitMeters)
        {
            candidate = VanguardTravelRouteModes.FormationTravel;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            cursor.TravelMode = current;
            cursor.PendingTravelMode = string.Empty;
            cursor.PendingTravelModeSinceUtc = DateTimeOffset.MinValue;
            reason = "hysteresis_band_maintained";
            return current;
        }

        if (!string.Equals(cursor.PendingTravelMode, candidate, StringComparison.OrdinalIgnoreCase))
        {
            cursor.PendingTravelMode = candidate;
            cursor.PendingTravelModeSinceUtc = now;
            cursor.TravelMode = current;
            reason = "transition_pending:" + candidate;
            return current;
        }

        double dwell = Math.Max(0d, (now - cursor.PendingTravelModeSinceUtc).TotalSeconds);
        if (dwell < VanguardMovementAuthorityDoctrine.TravelModeDwellSeconds)
        {
            cursor.TravelMode = current;
            reason = "transition_dwelling:" + candidate + ":" + dwell.ToString("0.00", CultureInfo.InvariantCulture);
            return current;
        }

        cursor.TravelMode = candidate;
        cursor.PendingTravelMode = string.Empty;
        cursor.PendingTravelModeSinceUtc = DateTimeOffset.MinValue;
        cursor.LastTravelModeChangeAtUtc = now;
        reason = "transition_committed:" + current + "_to_" + candidate;
        return candidate;
    }

    private static void LogModeTransition(OperatorDecisionSnapshot snapshot, OperatorRouteCursor cursor, string reason, DateTimeOffset now)
    {
        string normalizedReason = reason ?? string.Empty;
        bool meaningful = normalizedReason.StartsWith("transition_pending", StringComparison.OrdinalIgnoreCase)
            || normalizedReason.StartsWith("transition_committed", StringComparison.OrdinalIgnoreCase)
            || normalizedReason.StartsWith("emergency_enter_immediate", StringComparison.OrdinalIgnoreCase);
        if (!meaningful)
        {
            return;
        }

        string reasonKey = normalizedReason.StartsWith("transition_pending", StringComparison.OrdinalIgnoreCase)
            ? "transition_pending"
            : normalizedReason.StartsWith("transition_committed", StringComparison.OrdinalIgnoreCase)
                ? "transition_committed"
                : normalizedReason;
        LogThrottled("Mode|" + snapshot.BotProfileId + "|" + reasonKey + "|" + cursor.TravelMode + "|" + cursor.PendingTravelMode, now,
            $"VANGUARD_TRAVEL_MODE_HYSTERESIS operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; mode={Safe(cursor.TravelMode)}; pending={Safe(cursor.PendingTravelMode)}; ownerDistance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; enter={VanguardMovementAuthorityDoctrine.TravelCatchUpEnterMeters:0.00}; exit={VanguardMovementAuthorityDoctrine.TravelCatchUpExitMeters:0.00}; dwell={VanguardMovementAuthorityDoctrine.TravelModeDwellSeconds:0.00}; reason={Safe(normalizedReason)}; emergencyEnterImmediate=true; emergencyExit={VanguardMovementAuthorityDoctrine.SoftCorrectionMeters:0.00}; emergencyExitUsesDwell=true; tag={StatusTag}");
    }

    private static float ResolveLongitudinalOffset(OperatorRouteCursor cursor, string mode)
    {
        if (string.Equals(mode, VanguardTravelRouteModes.EmergencyCatchUp, StringComparison.OrdinalIgnoreCase))
        {
            return 5.0f;
        }

        if (string.Equals(mode, VanguardTravelRouteModes.CatchUp, StringComparison.OrdinalIgnoreCase))
        {
            return 8.0f;
        }

        return cursor.FormationLongitudinalOffsetMeters;
    }

    private static float ResolveLateralOffset(OperatorDecisionSnapshot snapshot, OperatorRouteCursor cursor, string mode)
    {
        if (!string.Equals(mode, VanguardTravelRouteModes.FormationTravel, StringComparison.OrdinalIgnoreCase))
        {
            return 0f;
        }

        float magnitude = snapshot.SquadCohesion.CorridorLike || !snapshot.SquadCohesion.WideLateralAllowed
            ? NarrowLateralOffsetMeters
            : FormationLateralOffsetMeters;
        return cursor.FormationLaneSign * magnitude;
    }

    private static float ResolveFormationLongitudinalOffset(OperatorDecisionSnapshot snapshot)
    {
        string role = (snapshot.SquadCohesion.TacticalRole + "|" + snapshot.SquadCohesion.Sector).ToLowerInvariant();
        if (role.Contains("rear")) return 17.0f;
        if (role.Contains("front") || role.Contains("forward")) return 8.0f;
        if (role.Contains("left") || role.Contains("right") || role.Contains("flank")) return 12.0f;
        return 13.5f;
    }

    private static float ResolveFormationLaneSign(OperatorDecisionSnapshot snapshot)
    {
        string role = (snapshot.SquadCohesion.TacticalRole + "|" + snapshot.SquadCohesion.Sector).ToLowerInvariant();
        if (role.Contains("left")) return -1f;
        if (role.Contains("right")) return 1f;
        return 0f;
    }

    private static float ResolveLookAhead(string mode)
    {
        if (string.Equals(mode, VanguardTravelRouteModes.EmergencyCatchUp, StringComparison.OrdinalIgnoreCase)) return EmergencyLookAheadMeters;
        if (string.Equals(mode, VanguardTravelRouteModes.CatchUp, StringComparison.OrdinalIgnoreCase)) return CatchUpLookAheadMeters;
        return FormationLookAheadMeters;
    }

    private static float ResolveRecoveryLookAhead(string mode)
    {
        if (string.Equals(mode, VanguardTravelRouteModes.EmergencyCatchUp, StringComparison.OrdinalIgnoreCase)) return VanguardMovementAuthorityDoctrine.TravelPhysicalRecoveryEmergencyLookAheadMeters;
        if (string.Equals(mode, VanguardTravelRouteModes.CatchUp, StringComparison.OrdinalIgnoreCase)) return VanguardMovementAuthorityDoctrine.TravelPhysicalRecoveryCatchUpLookAheadMeters;
        return VanguardMovementAuthorityDoctrine.TravelPhysicalRecoveryFormationLookAheadMeters;
    }

    private static float ResolveProjectionAhead(string mode)
    {
        if (string.Equals(mode, VanguardTravelRouteModes.EmergencyCatchUp, StringComparison.OrdinalIgnoreCase)) return EmergencyProjectionAheadMeters;
        if (string.Equals(mode, VanguardTravelRouteModes.CatchUp, StringComparison.OrdinalIgnoreCase)) return CatchUpProjectionAheadMeters;
        return FormationProjectionAheadMeters;
    }

    private static float ResolveCaptureRadius(string mode)
    {
        if (string.Equals(mode, VanguardTravelRouteModes.EmergencyCatchUp, StringComparison.OrdinalIgnoreCase)) return EmergencyCaptureRadiusMeters;
        if (string.Equals(mode, VanguardTravelRouteModes.CatchUp, StringComparison.OrdinalIgnoreCase)) return CatchUpCaptureRadiusMeters;
        return FormationCaptureRadiusMeters;
    }

    private static float ResolveAnchorRadius(string mode)
    {
        if (string.Equals(mode, VanguardTravelRouteModes.EmergencyCatchUp, StringComparison.OrdinalIgnoreCase)) return 6.0f;
        if (string.Equals(mode, VanguardTravelRouteModes.CatchUp, StringComparison.OrdinalIgnoreCase)) return 6.5f;
        return VanguardMovementAuthorityDoctrine.TravelCohesionAnchorRadiusMeters;
    }

    private static void TrimRoute(OwnerRouteState route)
    {
        while (route.Nodes.Count > 8
            && (route.Nodes.Count > MaxRouteNodes
                || route.OwnerProgressMeters - route.Nodes[0].ProgressMeters > RetainedRouteMeters))
        {
            route.Nodes.RemoveAt(0);
        }
    }

    private static void RemoveOwnerCursors(string ownerProfileId)
    {
        string[] keys = CursorByBotProfileId
            .Where(pair => string.Equals(pair.Value.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (string key in keys)
        {
            CursorByBotProfileId.Remove(key);
        }
    }

    private static Vector3 Flatten(Vector3 vector)
    {
        vector.y = 0f;
        return vector;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static string FormatVector(Vector3 value)
    {
        return value.x.ToString("0.0", CultureInfo.InvariantCulture) + ","
            + value.y.ToString("0.0", CultureInfo.InvariantCulture) + ","
            + value.z.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static void LogThrottled(string key, DateTimeOffset now, string message)
    {
        if (LastLogByKey.TryGetValue(key, out var last) && now - last < LogInterval)
        {
            return;
        }

        LastLogByKey[key] = now;
        VanguardClientDiagnosticsLog.Info(StatusTag, message);
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }

    private sealed class OwnerRouteState
    {
        public string OwnerProfileId = string.Empty;
        public int Epoch;
        public long Version;
        public List<RouteNode> Nodes = new();
        public Vector3 LastObservedPosition;
        public Vector3 LastRawObservedPosition;
        public DateTimeOffset CreatedAtUtc;
        public DateTimeOffset LastObservedAtUtc;
        public DateTimeOffset LastValidSampleAtUtc;
        public DateTimeOffset LastMovementAtUtc;
        public int ConsecutiveSampleFailures;
        public Vector3 SmoothedDirection;
        public float OwnerProgressMeters;

        public static OwnerRouteState Create(string ownerProfileId, Vector3 position, Vector3 rawPosition, DateTimeOffset now, int epoch = 1)
        {
            return new OwnerRouteState
            {
                OwnerProfileId = ownerProfileId,
                Epoch = epoch,
                Version = 1,
                Nodes = new List<RouteNode> { new(position, 0f) },
                LastObservedPosition = position,
                LastRawObservedPosition = rawPosition,
                CreatedAtUtc = now,
                LastObservedAtUtc = now,
                LastValidSampleAtUtc = now,
                LastMovementAtUtc = DateTimeOffset.MinValue,
                ConsecutiveSampleFailures = 0,
                SmoothedDirection = Vector3.zero,
                OwnerProgressMeters = 0f
            };
        }
    }

    private readonly struct RouteNode
    {
        public RouteNode(Vector3 position, float progressMeters)
        {
            Position = position;
            ProgressMeters = progressMeters;
        }

        public Vector3 Position { get; }
        public float ProgressMeters { get; }
    }

    private struct OperatorRouteCursor
    {
        public string OwnerProfileId;
        public int RouteEpoch;
        public float ProgressMeters;
        public float FormationLongitudinalOffsetMeters;
        public float FormationLaneSign;
        public string TravelMode;
        public string PendingTravelMode;
        public DateTimeOffset PendingTravelModeSinceUtc;
        public DateTimeOffset LastTravelModeChangeAtUtc;
        public Vector3 LastPhysicalPosition;
        public DateTimeOffset LastPhysicalProjectionAtUtc;
    }
}

internal static class VanguardTravelRouteModes
{
    public const string FormationTravel = "FormationTravel";
    public const string CatchUp = "CatchUp";
    public const string EmergencyCatchUp = "EmergencyCatchUp";
}

internal readonly struct VanguardTravelRouteTarget
{
    public VanguardTravelRouteTarget(
        bool valid,
        Vector3 anchor,
        Vector3 center,
        Vector3 tangent,
        int routeEpoch,
        long routeVersion,
        float operatorProgressMeters,
        float ownerProgressMeters,
        float targetProgressMeters,
        string mode,
        float lateralOffsetMeters,
        float anchorRadiusMeters,
        bool ownerMoving,
        float ownerStationarySeconds,
        bool requiresMovement,
        string sampleReason,
        string reason)
    {
        Valid = valid;
        Anchor = anchor;
        Center = center;
        Tangent = tangent;
        RouteEpoch = routeEpoch;
        RouteVersion = routeVersion;
        OperatorProgressMeters = operatorProgressMeters;
        OwnerProgressMeters = ownerProgressMeters;
        TargetProgressMeters = targetProgressMeters;
        Mode = mode;
        LateralOffsetMeters = lateralOffsetMeters;
        AnchorRadiusMeters = anchorRadiusMeters;
        OwnerMoving = ownerMoving;
        OwnerStationarySeconds = ownerStationarySeconds;
        RequiresMovement = requiresMovement;
        SampleReason = sampleReason;
        Reason = reason;
    }

    public static VanguardTravelRouteTarget Invalid(string reason) => new(
        false,
        Vector3.zero,
        Vector3.zero,
        Vector3.forward,
        0,
        0,
        0f,
        0f,
        0f,
        VanguardTravelRouteModes.FormationTravel,
        0f,
        VanguardMovementAuthorityDoctrine.TravelCohesionAnchorRadiusMeters,
        false,
        0f,
        false,
        "none",
        reason);

    public bool Valid { get; }
    public Vector3 Anchor { get; }
    public Vector3 Center { get; }
    public Vector3 Tangent { get; }
    public int RouteEpoch { get; }
    public long RouteVersion { get; }
    public float OperatorProgressMeters { get; }
    public float OwnerProgressMeters { get; }
    public float TargetProgressMeters { get; }
    public string Mode { get; }
    public float LateralOffsetMeters { get; }
    public float AnchorRadiusMeters { get; }
    public bool OwnerMoving { get; }
    public float OwnerStationarySeconds { get; }
    public bool RequiresMovement { get; }
    public string SampleReason { get; }
    public string Reason { get; }

    public string Summary => "routeEpoch=" + RouteEpoch.ToString(CultureInfo.InvariantCulture)
        + ";routeVersion=" + RouteVersion.ToString(CultureInfo.InvariantCulture)
        + ";mode=" + Mode
        + ";operatorProgress=" + OperatorProgressMeters.ToString("0.0", CultureInfo.InvariantCulture)
        + ";ownerProgress=" + OwnerProgressMeters.ToString("0.0", CultureInfo.InvariantCulture)
        + ";targetProgress=" + TargetProgressMeters.ToString("0.0", CultureInfo.InvariantCulture)
        + ";lateral=" + LateralOffsetMeters.ToString("0.0", CultureInfo.InvariantCulture)
        + ";anchorRadius=" + AnchorRadiusMeters.ToString("0.0", CultureInfo.InvariantCulture)
        + ";ownerMoving=" + (OwnerMoving ? "true" : "false")
        + ";ownerStationary=" + OwnerStationarySeconds.ToString("0.0", CultureInfo.InvariantCulture)
        + ";requiresMovement=" + (RequiresMovement ? "true" : "false")
        + ";sample=" + SampleReason
        + ";reason=" + Reason;
}
#endif
