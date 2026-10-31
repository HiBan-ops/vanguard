#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Patches;

// Responsibility: Implements generation-safe choke detection over shared-threat/direct-route convergence plus passive EFT structural door-link evidence.
// Flow: Qualifying converged convergence groups are checked for low observed DirectRoute diversity and a persistent shared route segment; live AICoversData voxels are then read passively to associate consistent NavMeshDoorLinks with that exact segment before publishing Choke/HardChoke confidence.
// Authority boundary: choke detection has zero execution authority. It cannot gate/wait Operators, select routes, acquire leases, mutate doors, call SAIN door behavior, write Tactical Coordination persistence, or synthesize movement intent.
// Invariant: A door is never a tactical choke by itself. HardChoke CONFIRMED requires relevant shared threat + convergent/low-diversity routes + explicit route-to-live-structural-link association; missing/stale/contradictory structural evidence stays INFERRED or yields.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class ChokeDetectionService
{
    public const string StatusTag = "VANGUARD_CHOKE_DETECTION_STATUS";

    private static readonly TimeSpan OptionalMaxDeferral = TimeSpan.FromSeconds(1.2d);
    private static readonly TimeSpan SummaryLogInterval = TimeSpan.FromSeconds(15.0d);
    private static readonly float[] SharedSegmentProbeDistances = { 3f, 6f, 9f, 12f, 15f, 18f };

    // Qualification parameters. The frozen KB intentionally leaves exact spatial tolerances for choke detection
    // runtime falsification/calibration; none of these values grant execution authority.
    private const float MinimumConvergedPairRatio = 0.50f;
    private const float SharedRouteDiameterMeters = 6.0f;
    private const float MinimumSharedSegmentMeters = 6.0f;
    private const float DoorRouteRelationToleranceMeters = 1.75f;
    private const float DoorSearchSectionStepMeters = 1.5f;
    private const float InferredIdentityGridMeters = 4.0f;

    private static readonly object Sync = new();

    private static ChokeDetectionSnapshot current = ChokeDetectionSnapshot.Empty;
    private static string activeInputIdentity = string.Empty;
    private static string publishedInputIdentity = string.Empty;
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static AICoversData? coversCache;
    private static bool bootLogged;
    private static bool transientInputGapActive;
    private static int transientInputGapTransitionsTotal;

    public static void Tick(DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            ClearForAuthorityLoss();
            return;
        }

        if (!RouteConvergenceService.TryGetSnapshot(out var convergenceSnapshot))
        {
            InvalidatePublishedForTransientInputGap();
            return;
        }

        transientInputGapActive = false;
        string inputIdentity = InputIdentity(convergenceSnapshot);
        if (string.Equals(inputIdentity, publishedInputIdentity, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.Equals(inputIdentity, activeInputIdentity, StringComparison.Ordinal))
        {
            activeInputIdentity = inputIdentity;
            lock (Sync)
            {
                current = ChokeDetectionSnapshot.Empty;
            }
        }

        if (!VanguardRuntimeFrameBudgetGuard.ShouldRunOptional("ChokeDetection", now, OptionalMaxDeferral, out string optionalBudgetReason))
        {
            return;
        }

        var chokes = new List<ChokeObservation>();
        int convergedInputGroups = 0;
        int rejectedDiversity = 0;
        int rejectedSharedSegment = 0;
        int structuralLinksSeen = 0;
        int structuralLinksConsistent = 0;
        int structuralLinksContradictory = 0;
        int structuralLinksAssociated = 0;

        foreach (var group in convergenceSnapshot.ThreatGroups)
        {
            if (!string.Equals(group.Classification, "shared_threat_paths_converged", StringComparison.Ordinal))
            {
                continue;
            }

            convergedInputGroups++;
            if (!TryMeasureObservedDirectRouteDiversity(group, out int convergedPairs, out int ambiguousPairs, out int independentPairs, out float convergenceRatio))
            {
                rejectedDiversity++;
                continue;
            }

            if (!TryBuildSharedSegment(group.Routes, out var sharedSegment))
            {
                rejectedSharedSegment++;
                continue;
            }

            ChokeDoorEvidence? doorEvidence = null;
            if (TryResolveCovers(out var covers))
            {
                var structural = FindStructuralDoorEvidence(covers, group.Routes, sharedSegment);
                structuralLinksSeen += structural.LinksSeen;
                structuralLinksConsistent += structural.ConsistentLinks;
                structuralLinksContradictory += structural.ContradictoryLinks;
                structuralLinksAssociated += structural.AssociatedLinks;
                doorEvidence = structural.BestEvidence;
            }

            string kind = doorEvidence?.IsConfirmed == true ? "hard_choke" : "choke";
            string confidence = doorEvidence?.IsConfirmed == true ? "CONFIRMED" : "INFERRED";
            string chokeId = doorEvidence?.IsConfirmed == true
                ? BuildHardChokeId(convergenceSnapshot.MapId, doorEvidence)
                : BuildInferredChokeId(convergenceSnapshot.MapId, sharedSegment.Center);
            string reason = doorEvidence?.IsConfirmed == true
                ? "shared_threat_convergence_low_direct_route_diversity_structural_door_relation"
                : "shared_threat_convergence_low_direct_route_diversity_geometry_only";

            chokes.Add(new ChokeObservation(
                chokeId,
                group.OwnerProfileId,
                group.ThreatId,
                kind,
                confidence,
                sharedSegment.Near,
                sharedSegment.Far,
                sharedSegment.Center,
                sharedSegment.NearDistanceFromThreatMeters,
                sharedSegment.FarDistanceFromThreatMeters,
                sharedSegment.LengthMeters,
                sharedSegment.MaxSpreadMeters,
                group.Routes.Count,
                convergedPairs,
                ambiguousPairs,
                independentPairs,
                convergenceRatio,
                doorEvidence,
                reason));
        }

        // Structural reads and route-section analysis may outlive the exact convergence generation that
        // triggered them. Publish only if that generation remains current and complete.
        if (!RouteConvergenceService.TryGetSnapshot(out var latestConvergence)
            || !string.Equals(inputIdentity, InputIdentity(latestConvergence), StringComparison.Ordinal))
        {
            return;
        }

        var next = new ChokeDetectionSnapshot(
            convergenceSnapshot.MapId,
            convergenceSnapshot.TopologyRevision,
            convergenceSnapshot.TopologyContentHash,
            convergenceSnapshot.OperatorBatchAtUtc,
            convergenceSnapshot.PublishedAtUtc,
            now,
            chokes.ToArray());

        lock (Sync)
        {
            current = next;
        }
        publishedInputIdentity = inputIdentity;

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_CHOKE_DETECTION_BOOT enabled=true; mode=choke_detection; observationOnly=true; executionAllowed=false; movementAuthority=false; intentAuthority=false; doorMutation=false; operatorGate=false; confidence=INFERRED_or_CONFIRMED; build={VanguardBuildVersion.BuildLabel}");
        }

        if (now >= nextSummaryAtUtc)
        {
            nextSummaryAtUtc = now + SummaryLogInterval;
            int inferred = chokes.Count(item => !item.IsConfirmed);
            int confirmed = chokes.Count(item => item.IsConfirmed);
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_CHOKE_DETECTION_SUMMARY map={Safe(convergenceSnapshot.MapId)}; topologyRevision={convergenceSnapshot.TopologyRevision}; topologyHash={ShortHash(convergenceSnapshot.TopologyContentHash)}; convergenceThreatGroups={convergenceSnapshot.ThreatGroups.Count}; convergedInputGroups={convergedInputGroups}; chokes={chokes.Count}; inferred={inferred}; confirmedHardChokes={confirmed}; rejectedDiversity={rejectedDiversity}; rejectedSharedSegment={rejectedSharedSegment}; structuralLinksSeen={structuralLinksSeen}; structuralLinksConsistent={structuralLinksConsistent}; structuralLinksContradictory={structuralLinksContradictory}; structuralLinksAssociated={structuralLinksAssociated}; transientInputGaps={transientInputGapTransitionsTotal}; optionalBudget={Safe(optionalBudgetReason)}; observationOnly=true; executionAllowed=false; operatorGate=false");
        }
    }

    public static bool TryGetSnapshot(out ChokeDetectionSnapshot snapshot)
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
            current = ChokeDetectionSnapshot.Empty;
        }

        activeInputIdentity = string.Empty;
        publishedInputIdentity = string.Empty;
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        coversCache = null;
        bootLogged = false;
        transientInputGapActive = false;
        transientInputGapTransitionsTotal = 0;

        if (!string.IsNullOrWhiteSpace(reason))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_CHOKE_DETECTION_RESET reason={Safe(reason)}; observationOnly=true; executionAllowed=false; structuralCache=cleared");
        }
    }

    private static bool TryMeasureObservedDirectRouteDiversity(
        ThreatGroupConvergenceAssessment group,
        out int convergedPairs,
        out int ambiguousPairs,
        out int independentPairs,
        out float convergenceRatio)
    {
        convergedPairs = 0;
        ambiguousPairs = 0;
        independentPairs = 0;
        convergenceRatio = 0f;

        if (group.Routes.Count < 2 || group.Routes.Any(route => !route.HasUsableGeometry))
        {
            return false;
        }

        int expectedPairs = (group.Routes.Count * (group.Routes.Count - 1)) / 2;
        if (group.Pairs.Count != expectedPairs || expectedPairs <= 0)
        {
            return false;
        }

        foreach (var pair in group.Pairs)
        {
            if (string.Equals(pair.Classification, "converged", StringComparison.Ordinal))
            {
                convergedPairs++;
            }
            else if (string.Equals(pair.Classification, "independent", StringComparison.Ordinal))
            {
                independentPairs++;
            }
            else if (string.Equals(pair.Classification, "ambiguous", StringComparison.Ordinal))
            {
                ambiguousPairs++;
            }
            else
            {
                // Missing/insufficient pair evidence means route diversity is not known well enough
                // to promote a tactical choke.
                return false;
            }
        }

        convergenceRatio = expectedPairs == 0 ? 0f : (float)convergedPairs / expectedPairs;
        return convergedPairs > 0
            && independentPairs == 0
            && convergenceRatio >= MinimumConvergedPairRatio;
    }

    private static bool TryBuildSharedSegment(
        IReadOnlyList<DirectRouteObservation> routes,
        out SharedSegment segment)
    {
        segment = default;
        if (routes.Count < 2 || routes.Any(route => !route.HasUsableGeometry))
        {
            return false;
        }

        var samples = new List<SharedSegmentSample>(SharedSegmentProbeDistances.Length);
        foreach (float distance in SharedSegmentProbeDistances)
        {
            var points = new Vector3[routes.Count];
            bool complete = true;
            for (int index = 0; index < routes.Count; index++)
            {
                if (routes[index].PathLengthMeters + 0.01f < distance
                    || !TryPointAtDistanceFromEnd(routes[index].Corners, distance, out points[index]))
                {
                    complete = false;
                    break;
                }
            }

            if (!complete)
            {
                samples.Add(new SharedSegmentSample(distance, false, Vector3.zero, float.MaxValue));
                continue;
            }

            Vector3 centroid = Average(points);
            float maxSpread = MaxPairDistance(points);
            samples.Add(new SharedSegmentSample(distance, maxSpread <= SharedRouteDiameterMeters, centroid, maxSpread));
        }

        int bestStart = -1;
        int bestEnd = -1;
        float bestSpan = 0f;
        int runStart = -1;
        for (int index = 0; index <= samples.Count; index++)
        {
            bool converged = index < samples.Count && samples[index].Converged;
            if (converged && runStart < 0)
            {
                runStart = index;
                continue;
            }

            if (converged || runStart < 0)
            {
                continue;
            }

            int runEnd = index - 1;
            float span = samples[runEnd].DistanceFromThreatMeters - samples[runStart].DistanceFromThreatMeters;
            if (runEnd > runStart && span > bestSpan)
            {
                bestStart = runStart;
                bestEnd = runEnd;
                bestSpan = span;
            }
            runStart = -1;
        }

        if (bestStart < 0 || bestEnd < 0 || bestSpan < MinimumSharedSegmentMeters)
        {
            return false;
        }

        float maxObservedSpread = 0f;
        for (int index = bestStart; index <= bestEnd; index++)
        {
            maxObservedSpread = Math.Max(maxObservedSpread, samples[index].MaxSpreadMeters);
        }

        Vector3 near = samples[bestStart].Centroid;
        Vector3 far = samples[bestEnd].Centroid;
        segment = new SharedSegment(
            near,
            far,
            (near + far) * 0.5f,
            bestSpan,
            maxObservedSpread,
            samples[bestStart].DistanceFromThreatMeters,
            samples[bestEnd].DistanceFromThreatMeters);
        return true;
    }

    private static StructuralDoorScanResult FindStructuralDoorEvidence(
        AICoversData covers,
        IReadOnlyList<DirectRouteObservation> routes,
        SharedSegment sharedSegment)
    {
        int linksSeen = 0;
        int consistent = 0;
        int contradictory = 0;
        int associated = 0;
        var candidates = new Dictionary<int, NavMeshDoorLink>();

        foreach (var route in routes)
        {
            foreach (Vector3 point in SampleRouteSection(route.Corners, sharedSegment.NearDistanceFromThreatMeters, sharedSegment.FarDistanceFromThreatMeters))
            {
                if (!TryCalculateVoxelIndexes(covers, point, out int x, out int y, out int z))
                {
                    continue;
                }

                // Adjacent voxels are inspected only to avoid edge-of-voxel misses. Same-voxel
                // presence is never accepted as route relation; explicit portal distance is checked below.
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (!TryGetVoxel(covers, x + dx, y + dy, z + dz, out var voxel)
                                || voxel.DoorLinks == null)
                            {
                                continue;
                            }

                            foreach (var link in voxel.DoorLinks)
                            {
                                if (link != null && !candidates.ContainsKey(link.Id))
                                {
                                    candidates.Add(link.Id, link);
                                }
                            }
                        }
                    }
                }
            }
        }

        ChokeDoorEvidence? best = null;
        float bestRelation = float.MaxValue;

        foreach (var link in candidates.Values)
        {
            linksSeen++;
            if (link.Door == null
                || string.IsNullOrWhiteSpace(link.DoorId)
                || !string.Equals(link.DoorId, link.Door.Id, StringComparison.Ordinal))
            {
                contradictory++;
                continue;
            }

            if (!HasUsablePortalGeometry(link))
            {
                contradictory++;
                continue;
            }

            consistent++;
            int relatedRoutes = 0;
            float relationDistance = 0f;
            foreach (var route in routes)
            {
                float routeDistance = RouteSectionToDoorDistance(route.Corners, sharedSegment, link);
                relationDistance = Math.Max(relationDistance, routeDistance);
                if (routeDistance <= DoorRouteRelationToleranceMeters)
                {
                    relatedRoutes++;
                }
            }

            int requiredRoutes = routes.Count;
            if (relatedRoutes < requiredRoutes)
            {
                continue;
            }

            associated++;
            // All direct routes must explicitly relate to the same structural portal. Rank by the
            // worst member relation, not by a single fortuitously close route.
            if (relationDistance >= bestRelation)
            {
                continue;
            }

            bestRelation = relationDistance;
            Vector3 portalCenter = ClosestPortalCenter(link, sharedSegment.Center);
            string doorState = link.Door.DoorState.ToString();
            bool nativeTraversalEligible = IsNativeDoorTraversalEligible(link);
            best = new ChokeDoorEvidence(
                link.Id,
                link.DoorId,
                doorState,
                portalCenter,
                relationDistance,
                relatedRoutes,
                requiredRoutes,
                "consistent_live_link",
                DescribeRouteExecutability(doorState),
                nativeTraversalEligible);
        }

        return new StructuralDoorScanResult(best, linksSeen, consistent, contradictory, associated);
    }

    private static bool TryResolveCovers(out AICoversData covers)
    {
        // A transient convergence publication gap must not force a structural rescan, but the cached
        // cover graph may never outlive the active BotsController instance that owns it.
        AICoversData? activeCovers = VanguardBotsControllerStatePatch.ActiveController?.CoversData;
        if (activeCovers == null
            || activeCovers.Voxels == null
            || activeCovers.VoxelesArray == null
            || activeCovers.VoxelesArray.Length <= 1)
        {
            coversCache = null;
            covers = null!;
            return false;
        }

        if (!ReferenceEquals(coversCache, activeCovers))
        {
            coversCache = activeCovers;
        }

        covers = activeCovers;
        return true;
    }

    private static bool TryCalculateVoxelIndexes(AICoversData covers, Vector3 position, out int x, out int y, out int z)
    {
        x = (int)((float)(int)(position.x - covers.MinVoxelesValues.x) / 10f);
        y = (int)((float)(int)(position.y - covers.MinVoxelesValues.y) / 5f);
        z = (int)((float)(int)(position.z - covers.MinVoxelesValues.z) / 10f);
        return x >= 0 && x < covers.MaxX
            && y >= 0 && y < covers.MaxY
            && z >= 0 && z < covers.MaxZ;
    }

    private static bool TryGetVoxel(AICoversData covers, int x, int y, int z, out NavGraphVoxelSimple voxel)
    {
        voxel = null!;
        if (x < 0 || x >= covers.MaxX || y < 0 || y >= covers.MaxY || z < 0 || z >= covers.MaxZ)
        {
            return false;
        }

        try
        {
            voxel = covers.VoxelesArray[x, y, z];
            return voxel != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasUsablePortalGeometry(NavMeshDoorLink link)
    {
        return IsFinite(link.Open1)
            && IsFinite(link.Open2)
            && IsFinite(link.Close1)
            && IsFinite(link.Close2_Normal)
            && ((link.Open2 - link.Open1).sqrMagnitude > 0.04f
                || (link.Close2_Normal - link.Close1).sqrMagnitude > 0.04f);
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
            && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }

    private static float RouteSectionToDoorDistance(
        IReadOnlyList<Vector3> route,
        SharedSegment segment,
        NavMeshDoorLink link)
    {
        var points = SampleRouteSection(route, segment.NearDistanceFromThreatMeters, segment.FarDistanceFromThreatMeters).ToArray();
        if (points.Length == 0)
        {
            return float.MaxValue;
        }

        float best = float.MaxValue;
        foreach (Vector3 point in points)
        {
            best = Math.Min(best, PointToSegmentDistance(point, link.Open1, link.Open2));
            best = Math.Min(best, PointToSegmentDistance(point, link.Close1, link.Close2_Normal));
        }
        return best;
    }

    private static IEnumerable<Vector3> SampleRouteSection(
        IReadOnlyList<Vector3> corners,
        float nearDistanceFromThreat,
        float farDistanceFromThreat)
    {
        if (corners == null || corners.Count < 2)
        {
            yield break;
        }

        float near = Math.Min(nearDistanceFromThreat, farDistanceFromThreat);
        float far = Math.Max(nearDistanceFromThreat, farDistanceFromThreat);
        for (float distance = near; distance <= far + 0.01f; distance += DoorSearchSectionStepMeters)
        {
            if (TryPointAtDistanceFromEnd(corners, distance, out Vector3 point))
            {
                yield return point;
            }
        }

        if (TryPointAtDistanceFromEnd(corners, far, out Vector3 finalPoint))
        {
            yield return finalPoint;
        }
    }

    private static Vector3 ClosestPortalCenter(NavMeshDoorLink link, Vector3 reference)
    {
        Vector3 openCenter = (link.Open1 + link.Open2) * 0.5f;
        Vector3 closeCenter = (link.Close1 + link.Close2_Normal) * 0.5f;
        return Vector3.Distance(openCenter, reference) <= Vector3.Distance(closeCenter, reference)
            ? openCenter
            : closeCenter;
    }


    private static bool IsNativeDoorTraversalEligible(NavMeshDoorLink link)
    {
        return link != null
            && link.Door != null
            && link.Door.enabled
            && link.Door.gameObject.activeInHierarchy
            && link.Door.Operatable
            && link.ShallInteract();
    }

    private static string DescribeRouteExecutability(string doorState)
    {
        if (doorState.IndexOf("locked", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "locked_requires_route_revalidation";
        }
        if (doorState.IndexOf("open", StringComparison.OrdinalIgnoreCase) >= 0
            || doorState.IndexOf("breach", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "structural_passage_open_or_breached";
        }
        if (doorState.IndexOf("shut", StringComparison.OrdinalIgnoreCase) >= 0
            || doorState.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "structural_passage_closed_native_traversal_required";
        }
        return "structural_passage_state_observed";
    }

    private static float PointToSegmentDistance(Vector3 point, Vector3 start, Vector3 end)
    {
        Vector3 delta = end - start;
        float denominator = delta.sqrMagnitude;
        if (denominator <= 0.0001f)
        {
            return Vector3.Distance(point, start);
        }

        float t = Mathf.Clamp01(Vector3.Dot(point - start, delta) / denominator);
        return Vector3.Distance(point, start + delta * t);
    }

    private static Vector3 Average(IReadOnlyList<Vector3> points)
    {
        Vector3 total = Vector3.zero;
        for (int index = 0; index < points.Count; index++)
        {
            total += points[index];
        }
        return points.Count == 0 ? Vector3.zero : total / points.Count;
    }

    private static float MaxPairDistance(IReadOnlyList<Vector3> points)
    {
        float max = 0f;
        for (int first = 0; first < points.Count; first++)
        {
            for (int second = first + 1; second < points.Count; second++)
            {
                max = Math.Max(max, Vector3.Distance(points[first], points[second]));
            }
        }
        return max;
    }

    private static bool TryPointAtDistanceFromEnd(IReadOnlyList<Vector3> corners, float distanceFromEnd, out Vector3 point)
    {
        point = default;
        if (corners == null || corners.Count == 0)
        {
            return false;
        }
        if (corners.Count == 1 || distanceFromEnd <= 0f)
        {
            point = corners[corners.Count - 1];
            return true;
        }

        float remaining = distanceFromEnd;
        for (int index = corners.Count - 1; index > 0; index--)
        {
            Vector3 end = corners[index];
            Vector3 start = corners[index - 1];
            float segmentLength = Vector3.Distance(start, end);
            if (segmentLength <= 0.001f)
            {
                continue;
            }
            if (remaining <= segmentLength)
            {
                float t = remaining / segmentLength;
                point = Vector3.Lerp(end, start, t);
                return true;
            }
            remaining -= segmentLength;
        }

        point = corners[0];
        return true;
    }

    private static string BuildHardChokeId(string mapId, ChokeDoorEvidence door)
    {
        string structuralId = string.IsNullOrWhiteSpace(door.DoorId)
            ? "link_" + door.LinkId.ToString(CultureInfo.InvariantCulture)
            : Safe(door.DoorId);
        return "hard:" + Safe(mapId) + ":" + structuralId;
    }

    private static string BuildInferredChokeId(string mapId, Vector3 center)
    {
        int x = Mathf.RoundToInt(center.x / InferredIdentityGridMeters);
        int y = Mathf.RoundToInt(center.y / InferredIdentityGridMeters);
        int z = Mathf.RoundToInt(center.z / InferredIdentityGridMeters);
        return string.Join(":", "choke", Safe(mapId), x.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture), z.ToString(CultureInfo.InvariantCulture));
    }

    private static string InputIdentity(RouteConvergenceSnapshot snapshot)
    {
        return string.Join("|",
            snapshot.TopologyRevision.ToString(CultureInfo.InvariantCulture),
            snapshot.TopologyContentHash ?? string.Empty,
            snapshot.OperatorBatchAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            snapshot.PublishedAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture));
    }

    private static void InvalidatePublishedForTransientInputGap()
    {
        lock (Sync)
        {
            current = ChokeDetectionSnapshot.Empty;
        }

        // Fail closed for consumers, but preserve lifecycle identity, summary cadence and the
        // live-controller structural cache. A coherent convergence snapshot returning with the same
        // generation must be recomputed/published without masquerading as a new choke detection boot.
        publishedInputIdentity = string.Empty;
        if (!transientInputGapActive)
        {
            transientInputGapActive = true;
            transientInputGapTransitionsTotal++;
        }
    }

    private static void ClearForAuthorityLoss()
    {
        lock (Sync)
        {
            current = ChokeDetectionSnapshot.Empty;
        }
        activeInputIdentity = string.Empty;
        publishedInputIdentity = string.Empty;
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        coversCache = null;
        bootLogged = false;
        transientInputGapActive = false;
        transientInputGapTransitionsTotal = 0;
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');

    private static string ShortHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }
        string trimmed = value.Trim();
        return trimmed.Length <= 12 ? trimmed : trimmed.Substring(0, 12);
    }

    private readonly struct SharedSegmentSample
    {
        public SharedSegmentSample(float distanceFromThreatMeters, bool converged, Vector3 centroid, float maxSpreadMeters)
        {
            DistanceFromThreatMeters = distanceFromThreatMeters;
            Converged = converged;
            Centroid = centroid;
            MaxSpreadMeters = maxSpreadMeters;
        }

        public float DistanceFromThreatMeters { get; }
        public bool Converged { get; }
        public Vector3 Centroid { get; }
        public float MaxSpreadMeters { get; }
    }

    private readonly struct SharedSegment
    {
        public SharedSegment(
            Vector3 near,
            Vector3 far,
            Vector3 center,
            float lengthMeters,
            float maxSpreadMeters,
            float nearDistanceFromThreatMeters,
            float farDistanceFromThreatMeters)
        {
            Near = near;
            Far = far;
            Center = center;
            LengthMeters = lengthMeters;
            MaxSpreadMeters = maxSpreadMeters;
            NearDistanceFromThreatMeters = nearDistanceFromThreatMeters;
            FarDistanceFromThreatMeters = farDistanceFromThreatMeters;
        }

        public Vector3 Near { get; }
        public Vector3 Far { get; }
        public Vector3 Center { get; }
        public float LengthMeters { get; }
        public float MaxSpreadMeters { get; }
        public float NearDistanceFromThreatMeters { get; }
        public float FarDistanceFromThreatMeters { get; }
    }

    private readonly struct StructuralDoorScanResult
    {
        public StructuralDoorScanResult(
            ChokeDoorEvidence? bestEvidence,
            int linksSeen,
            int consistentLinks,
            int contradictoryLinks,
            int associatedLinks)
        {
            BestEvidence = bestEvidence;
            LinksSeen = linksSeen;
            ConsistentLinks = consistentLinks;
            ContradictoryLinks = contradictoryLinks;
            AssociatedLinks = associatedLinks;
        }

        public ChokeDoorEvidence? BestEvidence { get; }
        public int LinksSeen { get; }
        public int ConsistentLinks { get; }
        public int ContradictoryLinks { get; }
        public int AssociatedLinks { get; }
    }
}
#endif
