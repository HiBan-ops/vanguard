#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Awareness;

// Responsibility: qualifies the previously deferred generic-route-exposure reader without granting gameplay authority.
// Flow: Coherent existing Direct/Alt route geometry is matched to an Awareness-qualified live threat, then a bounded set of final-route samples records facing, physical line-of-sight and distance evidence.
// Authority boundary: Observation only. This service never calculates new NavMesh paths, classifies HeldAccess/KillLane/exposure bands, changes route preference, assigns members, requests replan, starts movement or mutates SAIN.
// Invariant: Only Awareness-authorized target identities may be resolved; incomplete/budget-deferred groups are omitted rather than converted into safety or tactical preference.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class ThreatExposureObservationService
{
    public const string StatusTag = "VANGUARD_TC_THREAT_EXPOSURE_OBSERVATION_STATUS";

    private static readonly object Sync = new();
    private static readonly TimeSpan OptionalMaxDeferral = TimeSpan.FromSeconds(1.0d);
    private static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(0.50d);
    private static readonly TimeSpan SummaryLogInterval = TimeSpan.FromSeconds(10.0d);
    private const int MaxLinecastsPerFrame = 20;
    private const float FinalApproachWindowMeters = 18.0f;
    private const float ThreatEyeHeightMeters = 1.45f;
    private const float RouteSampleHeightMeters = 1.30f;

    private static readonly float[] FinalApproachFractions = { 0.25f, 0.50f, 0.75f, 1.00f };

    private static ThreatExposureObservationSnapshot current = ThreatExposureObservationSnapshot.Empty;
    private static DateTimeOffset nextRefreshAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    public static void Tick(DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            ClearPublished();
            return;
        }

        if (now < nextRefreshAtUtc)
        {
            return;
        }

        if (!TryGetCoherentInputs(out RouteConvergenceSnapshot convergence, out RouteIndependenceSnapshot independence))
        {
            ClearPublished();
            nextRefreshAtUtc = now + MinimumRefreshInterval;
            return;
        }

        if (!VanguardRuntimeFrameBudgetGuard.ShouldRunOptional("ThreatExposureObservation", now, OptionalMaxDeferral, out string optionalBudgetReason))
        {
            return;
        }

        nextRefreshAtUtc = now + MinimumRefreshInterval;
        IReadOnlyList<VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation> qualifiedThreats =
            VanguardCombatAwarenessBridge.GetFreshQualifiedTacticalCoordinationThreats(now);
        var byThreat = qualifiedThreats
            .GroupBy(item => OwnerThreatKey(item.OwnerProfileId, item.TargetProfileId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.ObservedAtUtc).First(), StringComparer.OrdinalIgnoreCase);

        var independenceByThreat = independence.ThreatGroups
            .GroupBy(item => OwnerThreatKey(item.OwnerProfileId, item.ThreatId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var groups = new List<ThreatGroupExposureObservation>();
        int matchedQualifiedGroups = 0;
        int qualifiedWithoutConvergence = 0;
        int convergenceWithoutQualified = 0;
        int budgetDeferrals = 0;
        int linecasts = 0;
        int directRoutesObserved = 0;
        int alternateAxesObserved = 0;

        var convergenceKeys = new HashSet<string>(
            convergence.ThreatGroups.Select(item => OwnerThreatKey(item.OwnerProfileId, item.ThreatId)),
            StringComparer.OrdinalIgnoreCase);
        foreach (string key in byThreat.Keys)
        {
            if (!convergenceKeys.Contains(key))
            {
                qualifiedWithoutConvergence++;
            }
        }

        foreach (ThreatGroupConvergenceAssessment group in convergence.ThreatGroups)
        {
            string key = OwnerThreatKey(group.OwnerProfileId, group.ThreatId);
            if (!byThreat.TryGetValue(key, out var threat))
            {
                convergenceWithoutQualified++;
                continue;
            }

            matchedQualifiedGroups++;
            var direct = new List<RouteExposureObservation>();
            bool groupComplete = true;
            foreach (DirectRouteObservation route in group.Routes)
            {
                if (!route.HasUsableGeometry)
                {
                    continue;
                }

                if (!TryObserveRoute(
                        group.OwnerProfileId,
                        "direct",
                        route.BotProfileId,
                        route.BotProfileId,
                        route.Corners,
                        threat.RuntimePosition,
                        threat.RuntimeLookDirection,
                        ref linecasts,
                        out RouteExposureObservation? observed))
                {
                    budgetDeferrals++;
                    groupComplete = false;
                    break;
                }

                if (observed != null)
                {
                    direct.Add(observed);
                    directRoutesObserved++;
                }
            }

            if (!groupComplete)
            {
                continue;
            }

            var alternate = new List<RouteExposureObservation>();
            if (independenceByThreat.TryGetValue(key, out ThreatGroupRouteObservation? routeGroup))
            {
                AxisCandidateObservation? bestAxis = routeGroup.AxisCandidates
                    .Where(item => item.HasUsableGeometry && item.IsObservedAlternateAxis)
                    .OrderByDescending(item => string.Equals(item.CandidateId, routeGroup.BestAxisCandidateId, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(item => item.AxisEvidenceScore)
                    .ThenBy(item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (bestAxis != null)
                {
                    if (!TryObserveRoute(
                            group.OwnerProfileId,
                            "alternate_axis",
                            bestAxis.CandidateId,
                            bestAxis.SeedBotProfileId,
                            bestAxis.Corners,
                            threat.RuntimePosition,
                            threat.RuntimeLookDirection,
                            ref linecasts,
                            out RouteExposureObservation? observedAxis))
                    {
                        budgetDeferrals++;
                        continue;
                    }

                    if (observedAxis != null)
                    {
                        alternate.Add(observedAxis);
                        alternateAxesObserved++;
                    }
                }
            }

            groups.Add(new ThreatGroupExposureObservation(
                group.OwnerProfileId,
                group.ThreatId,
                threat.ContactKind,
                threat.ObservedAtUtc,
                threat.ExpiresAtUtc,
                threat.RuntimePosition,
                threat.RuntimeLookDirection,
                group.ThreatAnchorPosition,
                Vector3.Distance(group.ThreatAnchorPosition, threat.RuntimePosition),
                direct.ToArray(),
                alternate.ToArray()));
        }

        if (!TryGetCoherentInputs(out RouteConvergenceSnapshot latestConvergence, out RouteIndependenceSnapshot latestIndependence)
            || latestConvergence.PublishedAtUtc != convergence.PublishedAtUtc
            || latestIndependence.PublishedAtUtc != independence.PublishedAtUtc)
        {
            return;
        }

        var next = new ThreatExposureObservationSnapshot(
            convergence.MapId,
            convergence.TopologyRevision,
            convergence.TopologyContentHash,
            convergence.OperatorBatchAtUtc,
            convergence.PublishedAtUtc,
            independence.PublishedAtUtc,
            now,
            groups.ToArray());

        lock (Sync)
        {
            current = next;
        }

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"TC_THREAT_EXPOSURE_BOOT enabled=true; observationOnly=true; executionAllowed=false; source=awareness_qualified_threat_plus_existing_routes; runtimeTruth=position_and_look_direction; routeScope=direct_plus_best_observed_alt_axis; newPathfinding=false; heldAccessClassification=false; killLaneClassification=false; tacticalReplanMutation=false; maxLinecastsPerFrame={MaxLinecastsPerFrame}; refreshSeconds={MinimumRefreshInterval.TotalSeconds:0.00}");
        }

        if (now >= nextSummaryAtUtc)
        {
            nextSummaryAtUtc = now + SummaryLogInterval;
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"TC_THREAT_EXPOSURE_SUMMARY map={Safe(convergence.MapId)}; topologyRevision={convergence.TopologyRevision}; qualifiedRuntimeThreats={qualifiedThreats.Count}; convergenceGroups={convergence.ThreatGroups.Count}; matchedQualifiedGroups={matchedQualifiedGroups}; qualifiedWithoutConvergence={qualifiedWithoutConvergence}; convergenceWithoutQualified={convergenceWithoutQualified}; publishedGroups={groups.Count}; directRoutesObserved={directRoutesObserved}; alternateAxesObserved={alternateAxesObserved}; linecasts={linecasts}; budgetDeferrals={budgetDeferrals}; optionalBudget={Safe(optionalBudgetReason)}; observationOnly=true; executionAllowed=false; heldAccessClassification=false; killLaneClassification=false; replanMutation=false");
            EmitGroupDetails(groups);
        }
    }

    public static bool TryGetSnapshot(out ThreatExposureObservationSnapshot snapshot)
    {
        lock (Sync)
        {
            snapshot = current;
            return snapshot.IsAvailable;
        }
    }

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            current = ThreatExposureObservationSnapshot.Empty;
        }

        nextRefreshAtUtc = DateTimeOffset.MinValue;
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;

        if (!string.IsNullOrWhiteSpace(reason))
        {
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"TC_THREAT_EXPOSURE_RESET reason={Safe(reason)}; observationOnly=true; executionAllowed=false; publication=cleared; pathMutation=false; targetMutation=false; replanMutation=false");
        }
    }

    private static bool TryGetCoherentInputs(
        out RouteConvergenceSnapshot convergence,
        out RouteIndependenceSnapshot independence)
    {
        convergence = RouteConvergenceSnapshot.Empty;
        independence = RouteIndependenceSnapshot.Empty;
        if (!RouteConvergenceService.TryGetSnapshot(out convergence)
            || !RouteIndependenceService.TryGetSnapshot(out independence))
        {
            return false;
        }

        return convergence.TopologyRevision == independence.TopologyRevision
            && string.Equals(convergence.MapId, independence.MapId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(convergence.TopologyContentHash, independence.TopologyContentHash, StringComparison.Ordinal)
            && convergence.OperatorBatchAtUtc == independence.OperatorBatchAtUtc
            && convergence.PublishedAtUtc == independence.ConvergencePublishedAtUtc;
    }

    // Shared observer primitive for downstream observation-only consumers that must evaluate an already-proven existing route.
    // The same TcThreatExposureLos owner budget is preserved so assignment alignment cannot silently add a second LOS budget.
    internal static bool TryObserveExistingRoute(
        string ownerProfileId,
        string routeKind,
        string routeId,
        string botProfileId,
        IReadOnlyList<Vector3> corners,
        Vector3 threatPosition,
        Vector3 threatLookDirection,
        ref int linecasts,
        out RouteExposureObservation? observation)
        => TryObserveRoute(
            ownerProfileId,
            routeKind,
            routeId,
            botProfileId,
            corners,
            threatPosition,
            threatLookDirection,
            ref linecasts,
            out observation);

    // Shared point observer for downstream shadow consumers that must evaluate one already-selected destination.
    // It shares the exact TcThreatExposureLos owner budget and physical LOS semantics used by route observation.
    internal static bool TryObserveExistingPoint(
        string ownerProfileId,
        Vector3 point,
        Vector3 threatPosition,
        Vector3 threatLookDirection,
        ref int linecasts,
        out bool sampleAvailable,
        out bool lineOfSightClear,
        out float facingDot,
        out float facingAngleDegrees,
        out float threatDistanceMeters)
    {
        sampleAvailable = false;
        lineOfSightClear = false;
        facingDot = float.NaN;
        facingAngleDegrees = float.NaN;
        threatDistanceMeters = float.NaN;

        Vector3 planar = point - threatPosition;
        planar.y = 0f;
        float planarDistance = planar.magnitude;
        if (planarDistance <= 0.05f || threatLookDirection.sqrMagnitude <= 0.01f)
        {
            return true;
        }

        Vector3 direction = planar / planarDistance;
        facingDot = Mathf.Clamp(Vector3.Dot(threatLookDirection, direction), -1f, 1f);
        facingAngleDegrees = Mathf.Acos(facingDot) * Mathf.Rad2Deg;
        threatDistanceMeters = Vector3.Distance(threatPosition, point);

        if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner(
                "TcThreatExposureLos",
                ownerProfileId,
                1,
                MaxLinecastsPerFrame,
                out _))
        {
            return false;
        }

        linecasts++;
        try
        {
            Vector3 start = threatPosition + Vector3.up * ThreatEyeHeightMeters;
            Vector3 end = point + Vector3.up * RouteSampleHeightMeters;
            lineOfSightClear = !Physics.Linecast(start, end, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        }
        catch
        {
            lineOfSightClear = false;
        }

        sampleAvailable = true;
        return true;
    }

    private static bool TryObserveRoute(
        string ownerProfileId,
        string routeKind,
        string routeId,
        string botProfileId,
        IReadOnlyList<Vector3> corners,
        Vector3 threatPosition,
        Vector3 threatLookDirection,
        ref int linecasts,
        out RouteExposureObservation? observation)
    {
        observation = null;
        if (corners == null || corners.Count < 2 || threatLookDirection.sqrMagnitude <= 0.01f)
        {
            return true;
        }

        float routeLength = PathLength(corners);
        float window = Math.Min(FinalApproachWindowMeters, routeLength);
        if (window <= 0.01f)
        {
            return true;
        }

        var dots = new List<float>(FinalApproachFractions.Length);
        var angles = new List<float>(FinalApproachFractions.Length);
        var distances = new List<float>(FinalApproachFractions.Length);
        var visibleDots = new List<float>(FinalApproachFractions.Length);
        var visibleAngles = new List<float>(FinalApproachFractions.Length);
        int unobstructed = 0;
        int forwardVisible = 0;
        int routeLinecasts = 0;
        float facingWeightedLosEvidenceSum = 0f;
        float facingWeightedLosEvidenceMax = 0f;

        foreach (float fraction in FinalApproachFractions)
        {
            if (!TryPointAtDistanceFromEnd(corners, window * fraction, out Vector3 sample))
            {
                continue;
            }

            Vector3 planar = sample - threatPosition;
            planar.y = 0f;
            float distance = planar.magnitude;
            if (distance <= 0.05f)
            {
                continue;
            }

            Vector3 direction = planar / distance;
            float dot = Mathf.Clamp(Vector3.Dot(threatLookDirection, direction), -1f, 1f);
            float angle = Mathf.Acos(dot) * Mathf.Rad2Deg;

            if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner(
                    "TcThreatExposureLos",
                    ownerProfileId,
                    1,
                    MaxLinecastsPerFrame,
                    out _))
            {
                return false;
            }

            linecasts++;
            routeLinecasts++;
            Vector3 start = threatPosition + Vector3.up * ThreatEyeHeightMeters;
            Vector3 end = sample + Vector3.up * RouteSampleHeightMeters;
            bool clear;
            try
            {
                clear = !Physics.Linecast(start, end, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            }
            catch
            {
                clear = false;
            }

            float facingWeightedLosEvidence = clear ? Mathf.Max(0f, dot) : 0f;
            facingWeightedLosEvidenceSum += facingWeightedLosEvidence;
            facingWeightedLosEvidenceMax = Math.Max(facingWeightedLosEvidenceMax, facingWeightedLosEvidence);

            if (clear)
            {
                unobstructed++;
                visibleDots.Add(dot);
                visibleAngles.Add(angle);
                if (dot > 0f)
                {
                    forwardVisible++;
                }
            }

            dots.Add(dot);
            angles.Add(angle);
            distances.Add(Vector3.Distance(threatPosition, sample));
        }

        if (dots.Count == 0)
        {
            return true;
        }

        observation = new RouteExposureObservation(
            routeKind,
            routeId,
            botProfileId,
            dots.Count,
            routeLinecasts,
            unobstructed,
            routeLinecasts > 0 ? unobstructed / (float)routeLinecasts : 0f,
            forwardVisible,
            dots.Count > 0 ? forwardVisible / (float)dots.Count : 0f,
            dots.Count > 0 ? facingWeightedLosEvidenceSum / dots.Count : 0f,
            facingWeightedLosEvidenceMax,
            visibleDots.Count > 0 ? visibleDots.Average() : float.NaN,
            visibleAngles.Count > 0 ? visibleAngles.Average() : float.NaN,
            dots.Min(),
            dots.Average(),
            dots.Max(),
            angles.Min(),
            angles.Average(),
            angles.Max(),
            distances.Min(),
            distances.Average(),
            distances.Max());
        return true;
    }

    private static void EmitGroupDetails(IReadOnlyList<ThreatGroupExposureObservation> groups)
    {
        foreach (ThreatGroupExposureObservation group in groups)
        {
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"TC_THREAT_EXPOSURE_GROUP owner={Safe(group.OwnerProfileId)}; threat={Safe(group.ThreatId)}; contactKind={Safe(group.ContactKind)}; contactAge={Number((float)Math.Max(0d, (DateTimeOffset.UtcNow - group.ContactObservedAtUtc).TotalSeconds))}; anchorRuntimeDelta={Number(group.AnchorRuntimeDeltaMeters)}; runtimePosition={Vector(group.RuntimeThreatPosition)}; runtimeLook={Vector(group.RuntimeThreatLookDirection)}; directRoutes={group.DirectRoutes.Count}; alternateAxes={group.AlternateAxes.Count}; directFacingWeightedLosMean={Number(MeanFacingWeightedLos(group.DirectRoutes))}; directFacingWeightedLosMax={Number(MaxFacingWeightedLos(group.DirectRoutes))}; altFacingWeightedLosMean={Number(MeanFacingWeightedLos(group.AlternateAxes))}; directVsAltFacingWeightedDelta={Number(DeltaFacingWeightedLos(group.DirectRoutes, group.AlternateAxes))}; observationOnly=true; executionAllowed=false");

            foreach (RouteExposureObservation route in group.DirectRoutes.Concat(group.AlternateAxes))
            {
                VanguardClientDiagnosticsLog.Info(
                    StatusTag,
                    $"TC_THREAT_EXPOSURE_ROUTE owner={Safe(group.OwnerProfileId)}; threat={Safe(group.ThreatId)}; kind={Safe(route.RouteKind)}; route={Safe(route.RouteId)}; bot={Safe(route.BotProfileId)}; samples={route.SampleCount}; linecasts={route.LinecastCount}; unobstructed={route.UnobstructedSampleCount}; unobstructedRatio={Number(route.UnobstructedSampleRatio)}; forwardVisible={route.ForwardVisibleSampleCount}; forwardVisibleRatio={Number(route.ForwardVisibleSampleRatio)}; facingWeightedLosMean={Number(route.FacingWeightedLosEvidenceMean)}; facingWeightedLosMax={Number(route.FacingWeightedLosEvidenceMax)}; visibleFacingDotMean={Number(route.VisibleFacingDotMean)}; visibleFacingAngleMean={Number(route.VisibleFacingAngleDegreesMean)}; facingDotMin={Number(route.FacingDotMin)}; facingDotMean={Number(route.FacingDotMean)}; facingDotMax={Number(route.FacingDotMax)}; facingAngleMin={Number(route.FacingAngleDegreesMin)}; facingAngleMean={Number(route.FacingAngleDegreesMean)}; facingAngleMax={Number(route.FacingAngleDegreesMax)}; distanceMin={Number(route.ThreatDistanceMetersMin)}; distanceMean={Number(route.ThreatDistanceMetersMean)}; distanceMax={Number(route.ThreatDistanceMetersMax)}; classification=raw_only; heldAccess=false; killLane=false; replanMutation=false");
            }
        }
    }

    private static float MeanFacingWeightedLos(IReadOnlyList<RouteExposureObservation> routes)
    {
        if (routes == null || routes.Count == 0)
        {
            return float.NaN;
        }

        return routes.Average(route => route.FacingWeightedLosEvidenceMean);
    }

    private static float MaxFacingWeightedLos(IReadOnlyList<RouteExposureObservation> routes)
    {
        if (routes == null || routes.Count == 0)
        {
            return float.NaN;
        }

        return routes.Max(route => route.FacingWeightedLosEvidenceMean);
    }

    private static float DeltaFacingWeightedLos(
        IReadOnlyList<RouteExposureObservation> directRoutes,
        IReadOnlyList<RouteExposureObservation> alternateAxes)
    {
        float direct = MeanFacingWeightedLos(directRoutes);
        float alternate = MeanFacingWeightedLos(alternateAxes);
        if (float.IsNaN(direct) || float.IsNaN(alternate))
        {
            return float.NaN;
        }

        return direct - alternate;
    }

    private static float PathLength(IReadOnlyList<Vector3> corners)
    {
        float total = 0f;
        for (int index = 1; index < corners.Count; index++)
        {
            total += Vector3.Distance(corners[index - 1], corners[index]);
        }
        return total;
    }

    private static bool TryPointAtDistanceFromEnd(IReadOnlyList<Vector3> corners, float remainingDistance, out Vector3 point)
    {
        point = default;
        if (corners == null || corners.Count == 0)
        {
            return false;
        }

        float remaining = Math.Max(0f, remainingDistance);
        for (int index = corners.Count - 1; index > 0; index--)
        {
            Vector3 end = corners[index];
            Vector3 start = corners[index - 1];
            float segment = Vector3.Distance(start, end);
            if (segment <= 0.001f)
            {
                continue;
            }

            if (remaining <= segment)
            {
                float t = 1f - (remaining / segment);
                point = Vector3.Lerp(start, end, t);
                return true;
            }
            remaining -= segment;
        }

        point = corners[0];
        return true;
    }

    private static string OwnerThreatKey(string? ownerProfileId, string? threatId)
        => Safe(ownerProfileId).ToLowerInvariant() + "|" + Safe(threatId).ToLowerInvariant();

    private static void ClearPublished()
    {
        lock (Sync)
        {
            current = ThreatExposureObservationSnapshot.Empty;
        }
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private static string Number(float value)
        => float.IsNaN(value) || float.IsInfinity(value)
            ? "unknown"
            : value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Vector(Vector3 value)
        => Number(value.x) + "," + Number(value.y) + "," + Number(value.z);
}
#endif
