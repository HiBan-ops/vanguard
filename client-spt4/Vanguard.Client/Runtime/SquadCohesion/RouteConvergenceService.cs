#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;

// Responsibility: Implements bounded, generation-safe threat/path convergence observation over the shared tactical assessment context.
// Flow: Current assessment evidence is grouped by owner/shared threat, SAIN-owned threat anchors are selected deterministically, direct NavMesh routes are acquired through Vanguard's shared heavy-work budget/cache, route pairs are compared, then one complete immutable snapshot is atomically published.
// Authority boundary: convergence has zero movement/intent authority. It cannot emit commands, reserve movement, change SAIN/BigBrain state, classify chokepoints, or persist any Tactical Coordination data.
// Invariant: Deferred/partial analysis is inert. Publication occurs only if the exact assessment generation is still current after all required work completes; stale generations are discarded rather than replayed.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class RouteConvergenceService
{
    public const string StatusTag = "VANGUARD_ROUTE_CONVERGENCE_STATUS";

    private static readonly TimeSpan OptionalMaxDeferral = TimeSpan.FromSeconds(1.0d);
    private static readonly TimeSpan SummaryLogInterval = TimeSpan.FromSeconds(15.0d);
    private static readonly TimeSpan RouteCacheLifetime = TimeSpan.FromSeconds(1.25d);
    private const int MaxDirectPathCalculationsPerFrame = 4;
    private const int MaxRouteCacheEntries = 64;

    // Qualification parameters, not frozen tactical doctrine. convergence runtime evidence is expected to
    // calibrate these before choke detection consumes the classification as choke evidence.
    private const float RouteReusePositionToleranceMeters = 1.5f;
    private const float FinalApproachWindowMeters = 18.0f;
    private const float MinimumFinalApproachWindowMeters = 2.0f;
    private const float ConvergedMeanSeparationMeters = 4.5f;
    private const float IndependentMeanSeparationMeters = 9.0f;
    private const float ConvergedApproachAngleDegrees = 35.0f;
    private const float IndependentApproachAngleDegrees = 70.0f;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, CachedDirectRoute> RouteCache = new(StringComparer.OrdinalIgnoreCase);

    private static RouteConvergenceSnapshot current = RouteConvergenceSnapshot.Empty;
    private static string activeInputIdentity = string.Empty;
    private static string publishedInputIdentity = string.Empty;
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    public static void Tick(DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            ClearPublished();
            return;
        }

        if (!TacticalAssessmentService.TryGetSnapshot(out var assessmentSnapshot))
        {
            ClearPublished();
            return;
        }

        string inputIdentity = InputIdentity(assessmentSnapshot);
        if (string.Equals(inputIdentity, publishedInputIdentity, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.Equals(inputIdentity, activeInputIdentity, StringComparison.Ordinal))
        {
            activeInputIdentity = inputIdentity;
            lock (Sync)
            {
                current = RouteConvergenceSnapshot.Empty;
            }
        }

        if (!VanguardRuntimeFrameBudgetGuard.ShouldRunOptional("RouteConvergence", now, OptionalMaxDeferral, out string optionalBudgetReason))
        {
            return;
        }

        PruneRouteCache(now);

        var sharedThreatGroups = assessmentSnapshot.Operators
            .Where(IsRelevantThreatMember)
            .GroupBy(item => OwnerThreatKey(item.OwnerProfileId, item.ThreatId), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase).ToArray())
            .Where(group => group.Length >= 2)
            .OrderBy(group => group[0].OwnerProfileId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group[0].ThreatId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var results = new List<ThreatGroupConvergenceAssessment>(sharedThreatGroups.Length);
        int pathCalculations = 0;
        int cacheHits = 0;
        int budgetDeferrals = 0;

        foreach (var members in sharedThreatGroups)
        {
            var anchorMember = SelectThreatAnchorMember(members);
            if (anchorMember == null || !anchorMember.ThreatAnchorPosition.HasValue)
            {
                continue;
            }

            Vector3 anchor = anchorMember.ThreatAnchorPosition.Value;
            var routes = new List<DirectRouteObservation>(members.Length);
            bool groupComplete = true;

            foreach (var member in members)
            {
                string cacheKey = RouteCacheKey(member.OwnerProfileId, member.BotProfileId, member.ThreatId);
                if (TryGetCachedRoute(cacheKey, member.OperatorPosition, anchor, now, out var cached))
                {
                    cacheHits++;
                    routes.Add(ToObservation(member, cached, cacheHit: true));
                    continue;
                }

                if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner(
                        "TcDirectPath",
                        member.OwnerProfileId,
                        1,
                        MaxDirectPathCalculationsPerFrame,
                        out _))
                {
                    budgetDeferrals++;
                    groupComplete = false;
                    continue;
                }

                pathCalculations++;
                CachedDirectRoute calculated = CalculateDirectRoute(member.OperatorPosition, anchor, now);
                RouteCache[cacheKey] = calculated;
                routes.Add(ToObservation(member, calculated, cacheHit: false));
            }

            // Partial multi-frame analysis is intentionally inert. Cached completed routes may
            // contribute on a later frame, but no incomplete group is published now.
            if (!groupComplete || routes.Count != members.Length)
            {
                return;
            }

            var pairs = BuildPairObservations(routes);
            results.Add(new ThreatGroupConvergenceAssessment(
                members[0].OwnerProfileId,
                members[0].ThreatId,
                anchor,
                anchorMember.ThreatAnchorSource,
                anchorMember.ThreatAnchorConfidence,
                members.Length,
                routes.ToArray(),
                pairs,
                ClassifyGroup(pairs)));
        }

        // Coalescing/stale rejection boundary: heavy work may have spanned time while assessment advanced.
        // Only the exact input generation that began this analysis may publish.
        if (!TacticalAssessmentService.TryGetSnapshot(out var latestAssessment)
            || !string.Equals(inputIdentity, InputIdentity(latestAssessment), StringComparison.Ordinal))
        {
            return;
        }

        var next = new RouteConvergenceSnapshot(
            assessmentSnapshot.MapId,
            assessmentSnapshot.TopologyRevision,
            assessmentSnapshot.TopologyContentHash,
            assessmentSnapshot.OperatorBatchAtUtc,
            assessmentSnapshot.PublishedAtUtc,
            now,
            results.ToArray());

        lock (Sync)
        {
            current = next;
        }
        publishedInputIdentity = inputIdentity;

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ROUTE_CONVERGENCE_BOOT enabled=true; mode=threat_path_convergence; observationOnly=true; executionAllowed=false; movementAuthority=false; intentAuthority=false; chokeClassification=false; pathBudgetPerFrame={MaxDirectPathCalculationsPerFrame}; build={VanguardBuildVersion.BuildLabel}");
        }

        if (now >= nextSummaryAtUtc)
        {
            nextSummaryAtUtc = now + SummaryLogInterval;
            int convergedGroups = results.Count(item => string.Equals(item.Classification, "shared_threat_paths_converged", StringComparison.Ordinal));
            int independentGroups = results.Count(item => string.Equals(item.Classification, "shared_threat_paths_independent", StringComparison.Ordinal));
            int ambiguousGroups = results.Count - convergedGroups - independentGroups;
            int convergedPairs = results.Sum(item => item.Pairs.Count(pair => string.Equals(pair.Classification, "converged", StringComparison.Ordinal)));
            int independentPairs = results.Sum(item => item.Pairs.Count(pair => string.Equals(pair.Classification, "independent", StringComparison.Ordinal)));
            int ambiguousPairs = results.Sum(item => item.Pairs.Count(pair => string.Equals(pair.Classification, "ambiguous", StringComparison.Ordinal) || string.Equals(pair.Classification, "insufficient_path_evidence", StringComparison.Ordinal)));
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ROUTE_CONVERGENCE_SUMMARY map={Safe(assessmentSnapshot.MapId)}; topologyRevision={assessmentSnapshot.TopologyRevision}; topologyHash={ShortHash(assessmentSnapshot.TopologyContentHash)}; sharedThreatGroups={results.Count}; convergedGroups={convergedGroups}; independentGroups={independentGroups}; ambiguousGroups={ambiguousGroups}; convergedPairs={convergedPairs}; independentPairs={independentPairs}; ambiguousPairs={ambiguousPairs}; pathCalculations={pathCalculations}; cacheHits={cacheHits}; budgetDeferrals={budgetDeferrals}; optionalBudget={Safe(optionalBudgetReason)}; observationOnly=true; executionAllowed=false; chokeClassification=false");
        }
    }

    public static bool TryGetSnapshot(out RouteConvergenceSnapshot snapshot)
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
            current = RouteConvergenceSnapshot.Empty;
            RouteCache.Clear();
        }

        activeInputIdentity = string.Empty;
        publishedInputIdentity = string.Empty;
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;

        if (!string.IsNullOrWhiteSpace(reason))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ROUTE_CONVERGENCE_RESET reason={Safe(reason)}; observationOnly=true; executionAllowed=false; cachedRoutes=0");
        }
    }

    private static bool IsRelevantThreatMember(TacticalOperatorAssessment item)
    {
        return item != null
            && item.RelevantThreat
            && !string.IsNullOrWhiteSpace(item.OwnerProfileId)
            && !string.IsNullOrWhiteSpace(item.BotProfileId)
            && !string.IsNullOrWhiteSpace(item.ThreatId)
            && !string.Equals(item.ThreatId, "none", StringComparison.OrdinalIgnoreCase)
            && item.ThreatAnchorPosition.HasValue;
    }

    private static TacticalOperatorAssessment? SelectThreatAnchorMember(
        IReadOnlyList<TacticalOperatorAssessment> members)
    {
        return members
            .Where(item => item.ThreatAnchorPosition.HasValue)
            .OrderByDescending(item => item.DirectThreat)
            .ThenByDescending(item => string.Equals(item.ThreatAnchorConfidence, "current_direct", StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => ThreatAgeSort(item.ThreatAnchorAgeSeconds))
            .ThenBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static float ThreatAgeSort(float? value)
    {
        return value.HasValue && value.Value >= 0f && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value)
            ? value.Value
            : float.MaxValue;
    }

    private static CachedDirectRoute CalculateDirectRoute(Vector3 start, Vector3 target, DateTimeOffset now)
    {
        var path = new NavMeshPath();
        bool calculated;
        try
        {
            calculated = NavMesh.CalculatePath(start, target, NavMesh.AllAreas, path);
        }
        catch
        {
            calculated = false;
        }

        Vector3[] corners = calculated && path.corners != null
            ? path.corners.ToArray()
            : Array.Empty<Vector3>();
        string status = !calculated
            ? "invalid"
            : path.status switch
            {
                NavMeshPathStatus.PathComplete => "complete",
                NavMeshPathStatus.PathPartial => "partial",
                _ => "invalid"
            };

        return new CachedDirectRoute(start, target, now, status, PathLength(corners), corners);
    }

    private static DirectRouteObservation ToObservation(
        TacticalOperatorAssessment member,
        CachedDirectRoute route,
        bool cacheHit)
    {
        return new DirectRouteObservation(
            member.OperatorId,
            member.BotProfileId,
            member.EvidenceCapturedAtUtc,
            route.StartPosition,
            route.TargetPosition,
            route.PathStatus,
            route.PathLengthMeters,
            route.Corners,
            cacheHit);
    }

    private static PairConvergenceObservation[] BuildPairObservations(
        IReadOnlyList<DirectRouteObservation> routes)
    {
        var pairs = new List<PairConvergenceObservation>();
        for (int first = 0; first < routes.Count; first++)
        {
            for (int second = first + 1; second < routes.Count; second++)
            {
                pairs.Add(CompareRoutes(routes[first], routes[second]));
            }
        }
        return pairs.ToArray();
    }

    private static PairConvergenceObservation CompareRoutes(
        DirectRouteObservation first,
        DirectRouteObservation second)
    {
        if (!first.HasUsableGeometry || !second.HasUsableGeometry)
        {
            return new PairConvergenceObservation(
                first.BotProfileId,
                second.BotProfileId,
                0,
                null,
                null,
                null,
                "insufficient_path_evidence");
        }

        float sharedWindow = Math.Min(FinalApproachWindowMeters, Math.Min(first.PathLengthMeters, second.PathLengthMeters));
        if (sharedWindow < MinimumFinalApproachWindowMeters)
        {
            return new PairConvergenceObservation(
                first.BotProfileId,
                second.BotProfileId,
                0,
                null,
                null,
                null,
                "insufficient_path_evidence");
        }

        float[] fractions = { 0.25f, 0.50f, 0.75f, 1.00f };
        var separations = new List<float>(fractions.Length);
        foreach (float fraction in fractions)
        {
            float remaining = sharedWindow * fraction;
            if (TryPointAtDistanceFromEnd(first.Corners, remaining, out Vector3 firstPoint)
                && TryPointAtDistanceFromEnd(second.Corners, remaining, out Vector3 secondPoint))
            {
                separations.Add(Vector3.Distance(firstPoint, secondPoint));
            }
        }

        if (separations.Count < 2)
        {
            return new PairConvergenceObservation(
                first.BotProfileId,
                second.BotProfileId,
                separations.Count,
                null,
                null,
                null,
                "insufficient_path_evidence");
        }

        float mean = separations.Average();
        float max = separations.Max();
        float? angle = FinalApproachAngle(first.Corners, second.Corners, sharedWindow);
        string classification;
        if (mean <= ConvergedMeanSeparationMeters
            && (!angle.HasValue || angle.Value <= ConvergedApproachAngleDegrees))
        {
            classification = "converged";
        }
        else if (mean >= IndependentMeanSeparationMeters
            || (angle.HasValue && angle.Value >= IndependentApproachAngleDegrees))
        {
            classification = "independent";
        }
        else
        {
            classification = "ambiguous";
        }

        return new PairConvergenceObservation(
            first.BotProfileId,
            second.BotProfileId,
            separations.Count,
            mean,
            max,
            angle,
            classification);
    }

    private static string ClassifyGroup(IReadOnlyList<PairConvergenceObservation> pairs)
    {
        if (pairs.Count == 0 || pairs.All(item => string.Equals(item.Classification, "insufficient_path_evidence", StringComparison.Ordinal)))
        {
            return "shared_threat_path_evidence_insufficient";
        }
        if (pairs.Any(item => string.Equals(item.Classification, "converged", StringComparison.Ordinal)))
        {
            return "shared_threat_paths_converged";
        }
        if (pairs.Any(item => string.Equals(item.Classification, "insufficient_path_evidence", StringComparison.Ordinal)
            || string.Equals(item.Classification, "ambiguous", StringComparison.Ordinal)))
        {
            return "shared_threat_paths_mixed_or_ambiguous";
        }
        if (pairs.All(item => string.Equals(item.Classification, "independent", StringComparison.Ordinal)))
        {
            return "shared_threat_paths_independent";
        }
        return "shared_threat_paths_mixed_or_ambiguous";
    }

    private static float? FinalApproachAngle(IReadOnlyList<Vector3> first, IReadOnlyList<Vector3> second, float sharedWindow)
    {
        float probeDistance = Math.Min(12f, sharedWindow);
        if (!TryPointAtDistanceFromEnd(first, probeDistance, out Vector3 firstProbe)
            || !TryPointAtDistanceFromEnd(second, probeDistance, out Vector3 secondProbe))
        {
            return null;
        }

        Vector3 firstEnd = first[first.Count - 1];
        Vector3 secondEnd = second[second.Count - 1];
        Vector3 firstDirection = firstEnd - firstProbe;
        Vector3 secondDirection = secondEnd - secondProbe;
        firstDirection.y = 0f;
        secondDirection.y = 0f;
        if (firstDirection.sqrMagnitude < 0.01f || secondDirection.sqrMagnitude < 0.01f)
        {
            return null;
        }
        return Vector3.Angle(firstDirection, secondDirection);
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

    private static float PathLength(IReadOnlyList<Vector3> corners)
    {
        float total = 0f;
        for (int index = 1; index < corners.Count; index++)
        {
            total += Vector3.Distance(corners[index - 1], corners[index]);
        }
        return total;
    }

    private static bool TryGetCachedRoute(
        string key,
        Vector3 currentStart,
        Vector3 currentTarget,
        DateTimeOffset now,
        out CachedDirectRoute route)
    {
        if (RouteCache.TryGetValue(key, out route!)
            && now - route.CreatedAtUtc <= RouteCacheLifetime
            && Vector3.Distance(route.StartPosition, currentStart) <= RouteReusePositionToleranceMeters
            && Vector3.Distance(route.TargetPosition, currentTarget) <= RouteReusePositionToleranceMeters)
        {
            return true;
        }
        route = null!;
        return false;
    }

    private static void PruneRouteCache(DateTimeOffset now)
    {
        string[] expired = RouteCache
            .Where(pair => now - pair.Value.CreatedAtUtc > RouteCacheLifetime)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (string key in expired)
        {
            RouteCache.Remove(key);
        }

        if (RouteCache.Count <= MaxRouteCacheEntries)
        {
            return;
        }

        foreach (string key in RouteCache
            .OrderBy(pair => pair.Value.CreatedAtUtc)
            .Take(RouteCache.Count - MaxRouteCacheEntries)
            .Select(pair => pair.Key)
            .ToArray())
        {
            RouteCache.Remove(key);
        }
    }

    private static void ClearPublished()
    {
        lock (Sync)
        {
            current = RouteConvergenceSnapshot.Empty;
            RouteCache.Clear();
        }
        activeInputIdentity = string.Empty;
        publishedInputIdentity = string.Empty;
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
    }

    private static string InputIdentity(TacticalAssessmentSnapshot snapshot)
    {
        return string.Join("|",
            snapshot.TopologyRevision.ToString(CultureInfo.InvariantCulture),
            snapshot.TopologyContentHash ?? string.Empty,
            snapshot.OperatorBatchAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            snapshot.PublishedAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture));
    }

    private static string OwnerThreatKey(string ownerProfileId, string threatId) => Safe(ownerProfileId) + "|" + Safe(threatId);
    private static string RouteCacheKey(string ownerProfileId, string botProfileId, string threatId) => Safe(ownerProfileId) + "|" + Safe(botProfileId) + "|" + Safe(threatId);

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

    private sealed class CachedDirectRoute
    {
        public CachedDirectRoute(
            Vector3 startPosition,
            Vector3 targetPosition,
            DateTimeOffset createdAtUtc,
            string pathStatus,
            float pathLengthMeters,
            Vector3[] corners)
        {
            StartPosition = startPosition;
            TargetPosition = targetPosition;
            CreatedAtUtc = createdAtUtc;
            PathStatus = pathStatus;
            PathLengthMeters = pathLengthMeters;
            Corners = corners;
        }

        public Vector3 StartPosition { get; }
        public Vector3 TargetPosition { get; }
        public DateTimeOffset CreatedAtUtc { get; }
        public string PathStatus { get; }
        public float PathLengthMeters { get; }
        public Vector3[] Corners { get; }
    }
}
#endif
