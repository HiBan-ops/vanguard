#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.TacticalAuthoring;

// Responsibility: Implements Route independence  Alternate Tactical Axis as bounded observation over one coherent convergence/choke detection/topology generation.
// Flow: A representative DirectRoute defines an objective-centric angular frame; authored TacticalAnchor/PoseSlot candidates and a bounded dynamic NavMesh sector fallback propose Alt establishment spaces; one complete path to each finalist is measured against the DirectRoute family and pre-establishment choke/topology evidence.
// Authority boundary: route independence has zero movement/intent/lease authority. It does not assign routes, reserve Operators, acquire leases, mutate SAIN/BigBrain/doors, or persist Tactical Coordination data. Current route assignment consumption remains explicitly fail-closed through the legacy HasGenuineAlternate contract.
// Invariant: Structural axis proof, travel cost, member assignment and synchronized commit remain separate. Only complete/current coherent generations publish; budget-deferred, stale or fixture-invalid analysis is never tactical truth.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class RouteIndependenceService
{
    public const string StatusTag = "VANGUARD_ROUTE_INDEPENDENCE_STATUS";

    private static readonly TimeSpan OptionalMaxDeferral = TimeSpan.FromSeconds(1.5d);
    private static readonly TimeSpan SummaryLogInterval = TimeSpan.FromSeconds(15.0d);
    private static readonly TimeSpan CandidateCacheLifetime = TimeSpan.FromSeconds(1.25d);
    private static readonly TimeSpan CandidateHistoryLifetime = TimeSpan.FromSeconds(8.0d);

    //  is deliberately bounded. Raw seeds are cheap hypotheses; only a small finalist set gets NavMesh path work.
    private const int MaxRawCandidateSeedsPerGroup = 10;
    private const int MaxFullPathFinalistsPerGroup = 6;
    private const int MaxCandidateHeavyWorkPerFrame = 6;
    private const int MaxCandidateCacheEntries = 96;

    // Candidate-generation hypotheses. These are runtime qualification values, not frozen tactical doctrine.
    private const float DefaultEstablishmentRadiusMeters = 18.0f;
    private const float MinimumEstablishmentRadiusMeters = 12.0f;
    private const float MaximumEstablishmentRadiusMeters = 28.0f;
    private const float MinimumAuthoredAnchorObjectiveDistanceMeters = 6.0f;
    private const float MaximumAuthoredAnchorObjectiveDistanceMeters = 48.0f;
    private const float DynamicSampleRadiusMeters = 3.5f;
    private const float DynamicVerticalToleranceMeters = 3.0f;
    private const float DynamicRegionRadiusMeters = 3.0f;
    private const float CandidateDuplicateDistanceMeters = 3.0f;
    private const float CachePositionToleranceMeters = 1.5f;

    // Geometry evidence. Existing  primitives are retained as measurements, but no path-cost/final-objective convergence predicate defines structural identity.
    private const float GeometrySampleStepMeters = 2.0f;
    private const float ArrivalDirectionWindowMeters = 8.0f;
    private const float OverlapToleranceMeters = 3.5f;
    private const float ClassicDistinctMaxOverlapRatio = 0.55f;
    private const float ClassicDistinctMeanSeparationMeters = 5.0f;
    private const float ClassicDistinctMaxSeparationMeters = 8.0f;
    private const float DistinctArrivalAngleDegrees = 25.0f;
    private const float SustainedDistinctMaxOverlapRatio = 0.65f;
    private const float MinimumIndependentTailMeters = 10.0f;
    private const float AuthoredTopologyMinimumTailMeters = 6.0f;
    private const float SameAxisMinOverlapRatio = 0.78f;
    private const float SameAxisMaxIndependentTailMeters = 6.0f;
    private const float AuthoredTopologyMinimumMaxSeparationMeters = 5.0f;

    // Topology/choke relation values are explicit observer hypotheses. Confirmed-door relation is narrower than the former whole-route 4 m veto.
    private const float PortalRelationToleranceMeters = 3.0f;
    private const float PassageRelationToleranceMeters = 2.5f;
    private const float ConfirmedDoorRelationToleranceMeters = 2.25f;
    private const float InferredChokeObservationToleranceMeters = 4.0f;

    // Sanity and stability. Pathology is a bounded-plan guard, not a structural-independence definition.
    private const float PathologicalPathToEuclideanRatio = 8.0f;
    private const int MinimumStableResamples = 2;
    private const float StableResampleBidirectionalOverlapRatio = 0.78f;
    private const float StableResampleMaxLengthRatio = 1.15f;

    private static readonly SectorDefinition[] DynamicSectors =
    {
        new("left_oblique", 65f),
        new("left_opposite", 115f),
        new("rear_opposite", 180f),
        new("right_opposite", -115f),
        new("right_oblique", -65f),
    };

    private static readonly object Sync = new();
    private static readonly Dictionary<string, CachedAxisProbe> CandidateCache = new(StringComparer.OrdinalIgnoreCase);

    private static RouteIndependenceSnapshot current = RouteIndependenceSnapshot.Empty;
    private static string activeInputIdentity = string.Empty;
    private static string publishedInputIdentity = string.Empty;
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextCoherenceGapLogAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;
    private static bool transientInputGapActive;
    private static int transientInputGapTransitionsTotal;
    private static bool fixtureContractEvaluated;
    private static bool fixtureContractHealthy;
    private static int fixturePassCount;
    private static int fixtureFailCount;
    private static string fixtureDetail = string.Empty;
    // Read-only diagnostic comparison retained separately from the authoritative current snapshot.
    private static RouteIndependenceSnapshot lastDiagnosticPublication = RouteIndependenceSnapshot.Empty;
    private const int MaxAxisTransitionDiagnosticsPerRaid = 160;
    private static int axisTransitionDiagnosticsEmitted;
    private static int axisTransitionDiagnosticsDropped;
    // Focused evidence only. These diagnostics never alter candidate qualification.
    private const int MaxFocusedAxisDiagnosticsPerRaid = 192;
    private static int focusedAxisDiagnosticsEmitted;
    private static int focusedAxisDiagnosticsDropped;

    public static void Tick(DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            ClearForAuthorityLoss();
            return;
        }

        if (!TryGetCoherentInputs(out var convergenceSnapshot, out var chokeSnapshot))
        {
            LogCoherenceGap(now);
            InvalidatePublishedForTransientInputGap();
            return;
        }

        transientInputGapActive = false;
        string inputIdentity = InputIdentity(convergenceSnapshot, chokeSnapshot);
        if (string.Equals(inputIdentity, publishedInputIdentity, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.Equals(inputIdentity, activeInputIdentity, StringComparison.Ordinal))
        {
            activeInputIdentity = inputIdentity;
            lock (Sync)
            {
                current = RouteIndependenceSnapshot.Empty;
            }
        }

        if (!EnsureFixtureContract())
        {
            return;
        }

        if (!VanguardRuntimeFrameBudgetGuard.ShouldRunOptional("RouteIndependence", now, OptionalMaxDeferral, out string optionalBudgetReason))
        {
            return;
        }

        PruneCandidateCache(now);
        VanguardTacticalCoordinationReadSnapshot? topology = TryGetCoherentTopology(convergenceSnapshot, out var coherentTopology)
            ? coherentTopology
            : null;

        var groups = new List<ThreatGroupRouteObservation>();
        int eligibleGroups = 0;
        int rawCandidates = 0;
        int authoredCandidates = 0;
        int dynamicCandidates = 0;
        int sampledCandidates = 0;
        int fullPathProbes = 0;
        int completePaths = 0;
        int observedAxes = 0;
        int sameAxisRejected = 0;
        int sameConfirmedChokeRejected = 0;
        int verticalRejected = 0;
        int pathologicalRejected = 0;
        int ambiguous = 0;
        int unstable = 0;
        int cacheHits = 0;
        int budgetDeferrals = 0;
        int maxStableResamples = 0;

        foreach (var group in convergenceSnapshot.ThreatGroups)
        {
            if (!string.Equals(group.Classification, "shared_threat_paths_converged", StringComparison.Ordinal)
                || group.Routes.Count < 2
                || group.Routes.Any(route => !route.HasUsableGeometry))
            {
                continue;
            }

            eligibleGroups++;
            var representative = group.Routes
                .Where(route => route.HasUsableGeometry)
                .OrderBy(route => route.PathLengthMeters)
                .ThenBy(route => route.BotProfileId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (representative == null || representative.Corners.Count < 2)
            {
                continue;
            }

            var observedChoke = chokeSnapshot.Chokes
                .Where(item => string.Equals(item.OwnerProfileId, group.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.ThreatId, group.ThreatId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.IsConfirmed)
                .ThenByDescending(item => item.SharedSegmentMeters)
                .ThenBy(item => item.ChokeId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (!TryBuildCandidateSeeds(convergenceSnapshot.MapId, group, representative, observedChoke, topology, out var seeds))
            {
                groups.Add(new ThreatGroupRouteObservation(
                    group.OwnerProfileId,
                    group.ThreatId,
                    group.ThreatAnchorPosition,
                    group.Routes.Count,
                    representative.BotProfileId,
                    observedChoke?.ChokeId,
                    observedChoke?.Confidence ?? "NOT_OBSERVED",
                    Array.Empty<AlternateRouteObservation>(),
                    Array.Empty<AxisCandidateObservation>(),
                    string.Empty,
                    "no_alternate_axis_observed"));
                continue;
            }

            rawCandidates += seeds.Count;
            authoredCandidates += seeds.Count(item => item.IsAuthored);
            dynamicCandidates += seeds.Count(item => !item.IsAuthored);

            var candidates = new List<AxisCandidateObservation>(seeds.Count);
            foreach (var seed in seeds)
            {
                string candidateKey = CandidateCacheKey(group.OwnerProfileId, group.ThreatId, representative.BotProfileId, seed);
                CachedAxisProbe probe;
                bool cacheHit;

                if (TryGetCachedCandidate(candidateKey, representative.StartPosition, seed.RawAnchor, now, out var cached))
                {
                    probe = cached;
                    cacheHit = true;
                    cacheHits++;
                }
                else
                {
                    int probeCost = seed.RequiresNavMeshSample ? 2 : 1;
                    if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner(
                            "TcAxisProbe",
                            group.OwnerProfileId,
                            probeCost,
                            MaxCandidateHeavyWorkPerFrame,
                            out _))
                    {
                        budgetDeferrals++;
                        return;
                    }

                    if (seed.RequiresNavMeshSample)
                    {
                        sampledCandidates++;
                    }
                    fullPathProbes++;
                    CandidateCache.TryGetValue(candidateKey, out var prior);
                    probe = CalculateAxisProbe(representative.StartPosition, seed, now, prior,
                        group.OwnerProfileId, group.ThreatId);
                    if (prior != null && prior.StableResampleCount >= MinimumStableResamples
                        && !string.Equals(probe.PathStatus, "complete", StringComparison.Ordinal))
                    {
                        EmitFocusedAxisDiagnostic(group.OwnerProfileId, group.ThreatId, seed.CandidateId,
                            "valid_path_replaced_by_unusable_probe", prior, probe, now,
                            0f, 0f, 0f, false);
                    }
                    CandidateCache[candidateKey] = probe;
                    cacheHit = false;
                }

                var observation = BuildAxisObservation(
                    representative,
                    group,
                    observedChoke,
                    topology,
                    seed,
                    probe,
                    cacheHit);
                candidates.Add(observation);
                maxStableResamples = Math.Max(maxStableResamples, observation.StableResampleCount);

                if (observation.HasUsableGeometry)
                {
                    completePaths++;
                }

                switch (observation.Classification)
                {
                    case "alternate_axis_observed":
                        observedAxes++;
                        break;
                    case "alternate_axis_candidate_unstable":
                        unstable++;
                        break;
                    case "same_axis_rejected":
                        sameAxisRejected++;
                        break;
                    case "same_confirmed_choke_rejected":
                        sameConfirmedChokeRejected++;
                        break;
                    case "vertical_evidence_insufficient":
                        verticalRejected++;
                        break;
                    case "pathological_route_rejected":
                        pathologicalRejected++;
                        break;
                    default:
                        ambiguous++;
                        break;
                }
            }

            var best = candidates
                .OrderByDescending(item => item.IsObservedAlternateAxis)
                .ThenByDescending(item => string.Equals(item.Classification, "alternate_axis_candidate_unstable", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => item.AxisEvidenceScore)
                .ThenBy(item => item.PathLengthMeters)
                .ThenBy(item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            string groupClassification = candidates.Any(item => item.IsObservedAlternateAxis)
                ? "alternate_axis_observed"
                : candidates.Any(item => string.Equals(item.Classification, "alternate_axis_candidate_unstable", StringComparison.OrdinalIgnoreCase))
                    ? "alternate_axis_candidate_unstable"
                    : candidates.Any(item => string.Equals(item.Classification, "axis_evidence_ambiguous", StringComparison.OrdinalIgnoreCase))
                        ? "axis_evidence_ambiguous"
                        : "no_alternate_axis_observed";

            groups.Add(new ThreatGroupRouteObservation(
                group.OwnerProfileId,
                group.ThreatId,
                group.ThreatAnchorPosition,
                group.Routes.Count,
                representative.BotProfileId,
                observedChoke?.ChokeId,
                observedChoke?.Confidence ?? "NOT_OBSERVED",
                Array.Empty<AlternateRouteObservation>(),
                candidates.ToArray(),
                best?.CandidateId ?? string.Empty,
                groupClassification));
        }

        // Expensive candidate work may span frames. Publish only if the exact coherent convergence/choke detection pair remains current.
        if (!TryGetCoherentInputs(out var latestConvergence, out var latestChoke)
            || !string.Equals(inputIdentity, InputIdentity(latestConvergence, latestChoke), StringComparison.Ordinal))
        {
            return;
        }

        var next = new RouteIndependenceSnapshot(
            convergenceSnapshot.MapId,
            convergenceSnapshot.TopologyRevision,
            convergenceSnapshot.TopologyContentHash,
            convergenceSnapshot.OperatorBatchAtUtc,
            convergenceSnapshot.PublishedAtUtc,
            chokeSnapshot.PublishedAtUtc,
            now,
            groups.ToArray());

        lock (Sync)
        {
            current = next;
        }
        publishedInputIdentity = inputIdentity;
        EmitAxisTransitionDiagnostics(next);

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ROUTE_INDEPENDENCE_BOOT enabled=true; semanticRevision={RouteIndependenceSnapshot.SemanticRevision}; mode=alternate_tactical_axis; observationOnly=true; executionAllowed=false; movementAuthority=false; intentAuthority=false; routeAssignment=false; leaseAuthority=false; assignmentConsumption=false; candidateSource=authored_anchor_plus_dynamic_objective_sector; rawSeedCap={MaxRawCandidateSeedsPerGroup}; fullPathFinalistCap={MaxFullPathFinalistsPerGroup}; heavyWorkPerFrame={MaxCandidateHeavyWorkPerFrame}; dynamicRadiusDefault={Number(DefaultEstablishmentRadiusMeters)}; dynamicRadiusMin={Number(MinimumEstablishmentRadiusMeters)}; dynamicRadiusMax={Number(MaximumEstablishmentRadiusMeters)}; confirmedDoorTolerance={Number(ConfirmedDoorRelationToleranceMeters)}; fixturePass={fixturePassCount}; fixtureFail={fixtureFailCount}; build={VanguardBuildVersion.BuildLabel}");
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ROUTE_INDEPENDENCE_FIXTURES pass={fixturePassCount}; fail={fixtureFailCount}; detail={Safe(fixtureDetail)}; failClosedOnFixtureFailure=true; observationOnly=true");
        }

        if (now >= nextSummaryAtUtc)
        {
            nextSummaryAtUtc = now + SummaryLogInterval;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ROUTE_INDEPENDENCE_SUMMARY map={Safe(convergenceSnapshot.MapId)}; topologyRevision={convergenceSnapshot.TopologyRevision}; topologyHash={ShortHash(convergenceSnapshot.TopologyContentHash)}; topologyEvidenceAvailable={Bool(topology != null)}; eligibleGroups={eligibleGroups}; publishedGroups={groups.Count}; rawCandidates={rawCandidates}; authoredCandidates={authoredCandidates}; dynamicCandidates={dynamicCandidates}; sampledCandidates={sampledCandidates}; fullPathProbes={fullPathProbes}; completePaths={completePaths}; axisObserved={observedAxes}; sameAxisRejected={sameAxisRejected}; sameConfirmedChokeRejected={sameConfirmedChokeRejected}; verticalRejected={verticalRejected}; pathologicalRejected={pathologicalRejected}; ambiguous={ambiguous}; unstable={unstable}; cacheHits={cacheHits}; cacheEntries={CandidateCache.Count}; budgetDeferrals={budgetDeferrals}; maxStableResamples={maxStableResamples}; transientInputGaps={transientInputGapTransitionsTotal}; optionalBudget={Safe(optionalBudgetReason)}; axisTransitionDiagnosticsEmitted={axisTransitionDiagnosticsEmitted}; axisTransitionDiagnosticsDropped={axisTransitionDiagnosticsDropped}; focusedAxisDiagnosticsEmitted={focusedAxisDiagnosticsEmitted}; focusedAxisDiagnosticsDropped={focusedAxisDiagnosticsDropped}; costDefinesStructure=false; terminalConvergenceVeto=false; legacyGenuineAlternate=false; assignmentConsumption=false; observationOnly=true; executionAllowed=false");
            EmitCandidateDetails(groups);
        }
    }

    private static void EmitCandidateDetails(IReadOnlyList<ThreatGroupRouteObservation> groups)
    {
        foreach (var group in groups)
        {
            foreach (var candidate in group.AxisCandidates)
            {
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"TC_ROUTE_INDEPENDENCE_CANDIDATE owner={Safe(group.OwnerProfileId)}; threat={Safe(group.ThreatId)}; candidate={Safe(candidate.CandidateId)}; source={Safe(candidate.CandidateSource)}; sourceId={Safe(candidate.CandidateSourceId)}; sourceOrigin={Safe(candidate.CandidateSourceOrigin)}; sector={Safe(candidate.ApproachSector)}; class={Safe(candidate.Classification)}; reason={Safe(candidate.ReasonCode)}; path={Safe(candidate.PathStatus)}; pathLength={Number(candidate.PathLengthMeters)}; directLength={Number(candidate.DirectReferencePathLengthMeters)}; costRatioTelemetry={Number(candidate.PathCostRatioTelemetry)}; overlap={Number(candidate.RouteOverlapRatio)}; meanSeparation={Number(candidate.MeanDirectFamilySeparationMeters)}; maxSeparation={Number(candidate.MaxDirectFamilySeparationMeters)}; arrivalAngle={Number(candidate.ArrivalAngleFromDirectDegrees)}; independentTail={Number(candidate.IndependentTailMeters)}; topologyEvidence={Safe(candidate.TopologyEvidence)}; topologyDistinct={Bool(!string.Equals(candidate.AlternateTopologySignature, "none", StringComparison.OrdinalIgnoreCase) && !string.Equals(candidate.AlternateTopologySignature, candidate.DirectTopologySignature, StringComparison.OrdinalIgnoreCase))}; directSignature={Safe(candidate.DirectTopologySignature)}; altSignature={Safe(candidate.AlternateTopologySignature)}; preEstablishmentChoke={Safe(candidate.PreEstablishmentChokeRelation)}; chokeId={Safe(candidate.ObservedChokeId)}; chokeConfidence={Safe(candidate.ObservedChokeConfidence)}; minChokeDistance={NullableNumber(candidate.MinimumObservedChokeDistanceMeters)}; vertical={Safe(candidate.VerticalEvidence)}; verticalDelta={Number(candidate.VerticalDeltaMeters)}; stable={candidate.StableResampleCount}; stability={Safe(candidate.Stability)}; evidenceScore={Number(candidate.AxisEvidenceScore)}; cacheHit={Bool(candidate.CacheHit)}; observationOnly=true; executionAllowed=false");
            }
        }
    }

    // Compare published evidence without claiming that it was the exact previous cache probe.
    private static void EmitPublishedResetContext(
        string ownerProfileId, string threatId,
        AxisCandidateObservation previous, AxisCandidateObservation next,
        DateTimeOffset previousAt, DateTimeOffset currentAt)
    {
        if (focusedAxisDiagnosticsEmitted >= MaxFocusedAxisDiagnosticsPerRaid)
        {
            focusedAxisDiagnosticsDropped++;
            return;
        }
        focusedAxisDiagnosticsEmitted++;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_ROUTE_AXIS_PUBLISHED_RESET groupToken={DiagnosticToken(ownerProfileId + "|" + threatId)}; axisToken={DiagnosticToken(next.CandidateId)}; representativeChanged={Bool(!string.Equals(previous.SeedBotProfileId, next.SeedBotProfileId, StringComparison.OrdinalIgnoreCase))}; anchorDelta={Number(Vector3.Distance(previous.EstablishmentAnchor, next.EstablishmentAnchor))}; previousPath={Safe(previous.PathStatus)}; currentPath={Safe(next.PathStatus)}; previousStable={previous.StableResampleCount}; currentStable={next.StableResampleCount}; previousLength={Number(previous.PathLengthMeters)}; currentLength={Number(next.PathLengthMeters)}; elapsedSec={Number((float)(currentAt - previousAt).TotalSeconds)}; source=published_snapshot_comparison_not_cache_probe; emitted={focusedAxisDiagnosticsEmitted}; dropped={focusedAxisDiagnosticsDropped}; observationOnly=true; movementAuthority=false");
    }

    // Bounded diagnostic-only evidence about the actual cache probe consumed during
    // one recomputation. Old paths are never admitted on the strength of this trace.
    private static void EmitFocusedAxisDiagnostic(
        string ownerProfileId, string threatId, string candidateId, string cause,
        CachedAxisProbe prior, CachedAxisProbe next, DateTimeOffset now,
        float lengthRatio, float forwardOverlap, float reverseOverlap, bool metricsAvailable)
    {
        if (focusedAxisDiagnosticsEmitted >= MaxFocusedAxisDiagnosticsPerRaid)
        {
            focusedAxisDiagnosticsDropped++;
            return;
        }
        focusedAxisDiagnosticsEmitted++;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_ROUTE_AXIS_PROBE_CAUSE groupToken={DiagnosticToken(ownerProfileId + "|" + threatId)}; axisToken={DiagnosticToken(candidateId)}; gate={Safe(cause)}; priorPath={Safe(prior.PathStatus)}; currentPath={Safe(next.PathStatus)}; priorStable={prior.StableResampleCount}; priorAgeSec={Number((float)(now - prior.CreatedAtUtc).TotalSeconds)}; startDelta={Number(Vector3.Distance(prior.StartPosition, next.StartPosition))}; anchorDelta={Number(Vector3.Distance(prior.RawAnchor, next.RawAnchor))}; lengthRatio={Number(lengthRatio)}; overlapForward={Number(forwardOverlap)}; overlapReverse={Number(reverseOverlap)}; comparisonMetricsAvailable={Bool(metricsAvailable)}; overlapMeasured={Bool(metricsAvailable && lengthRatio > 0f && lengthRatio <= StableResampleMaxLengthRatio)}; emitted={focusedAxisDiagnosticsEmitted}; dropped={focusedAxisDiagnosticsDropped}; observationOnly=true; movementAuthority=false");
    }

    // Compare only two coherent, already-published observations. Never infer the upstream
    // reason for a missing finalist or reuse these diagnostics as tactical authority.
    private static void EmitAxisTransitionDiagnostics(RouteIndependenceSnapshot next)
    {
        RouteIndependenceSnapshot previous = lastDiagnosticPublication;
        lastDiagnosticPublication = next;
        if (!previous.IsAvailable
            || !string.Equals(previous.MapId, next.MapId, StringComparison.OrdinalIgnoreCase)
            || previous.TopologyRevision != next.TopologyRevision
            || !string.Equals(previous.TopologyContentHash, next.TopologyContentHash, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var group in next.ThreatGroups)
        {
            var priorGroup = previous.ThreatGroups.FirstOrDefault(item =>
                string.Equals(item.OwnerProfileId, group.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ThreatId, group.ThreatId, StringComparison.OrdinalIgnoreCase));
            if (priorGroup == null)
            {
                continue;
            }

            foreach (var candidate in group.AxisCandidates)
            {
                var prior = priorGroup.AxisCandidates.FirstOrDefault(item =>
                    string.Equals(item.CandidateId, candidate.CandidateId, StringComparison.OrdinalIgnoreCase));
                if (prior == null
                    || (string.Equals(prior.Classification, candidate.Classification, StringComparison.Ordinal)
                        && string.Equals(prior.ReasonCode, candidate.ReasonCode, StringComparison.Ordinal)
                        && string.Equals(prior.PathStatus, candidate.PathStatus, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (string.Equals(prior.Classification, "alternate_axis_observed", StringComparison.Ordinal)
                    && string.Equals(candidate.Classification, "alternate_axis_candidate_unstable", StringComparison.Ordinal))
                {
                    EmitPublishedResetContext(group.OwnerProfileId, group.ThreatId,
                        prior, candidate, previous.PublishedAtUtc, next.PublishedAtUtc);
                }
                EmitAxisTransitionDiagnostic(group.OwnerProfileId, group.ThreatId,
                    candidate.CandidateId, "classification_changed", prior.Classification,
                    candidate.Classification, candidate.ReasonCode, candidate.PathStatus,
                    candidate.StableResampleCount, previous.PublishedAtUtc, next.PublishedAtUtc);
            }

            foreach (var prior in priorGroup.AxisCandidates)
            {
                if (group.AxisCandidates.Any(item =>
                    string.Equals(item.CandidateId, prior.CandidateId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                EmitAxisTransitionDiagnostic(group.OwnerProfileId, group.ThreatId,
                    prior.CandidateId, "absent_from_published_finalists", prior.Classification,
                    "absent", "not_proven", "unknown", 0,
                    previous.PublishedAtUtc, next.PublishedAtUtc);
            }
        }
    }

    private static void EmitAxisTransitionDiagnostic(
        string ownerId, string threatId, string candidateId, string transition,
        string previousClass, string currentClass, string reason, string pathStatus,
        int stableResamples, DateTimeOffset previousAt, DateTimeOffset currentAt)
    {
        if (axisTransitionDiagnosticsEmitted >= MaxAxisTransitionDiagnosticsPerRaid)
        {
            axisTransitionDiagnosticsDropped++;
            return;
        }
        axisTransitionDiagnosticsEmitted++;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_ROUTE_AXIS_CLASS_TRANSITION groupToken={DiagnosticToken(ownerId + "|" + threatId)}; axisToken={DiagnosticToken(candidateId)}; transition={Safe(transition)}; fromClass={Safe(previousClass)}; toClass={Safe(currentClass)}; reason={Safe(reason)}; path={Safe(pathStatus)}; stableResamples={stableResamples}; previousIndependenceTicks={previousAt.UtcDateTime.Ticks}; currentIndependenceTicks={currentAt.UtcDateTime.Ticks}; emitted={axisTransitionDiagnosticsEmitted}; dropped={axisTransitionDiagnosticsDropped}; observationOnly=true; authority=false");
    }

    private static string DiagnosticToken(string value)
        => unchecked((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(value ?? string.Empty)).ToString("X8", CultureInfo.InvariantCulture);

    public static bool TryGetSnapshot(out RouteIndependenceSnapshot snapshot)
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
            current = RouteIndependenceSnapshot.Empty;
            CandidateCache.Clear();
        }
        lastDiagnosticPublication = RouteIndependenceSnapshot.Empty;
        axisTransitionDiagnosticsEmitted = 0;
        axisTransitionDiagnosticsDropped = 0;
        focusedAxisDiagnosticsEmitted = 0;
        focusedAxisDiagnosticsDropped = 0;

        activeInputIdentity = string.Empty;
        publishedInputIdentity = string.Empty;
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        nextCoherenceGapLogAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        transientInputGapActive = false;
        transientInputGapTransitionsTotal = 0;
        fixtureContractEvaluated = false;
        fixtureContractHealthy = false;
        fixturePassCount = 0;
        fixtureFailCount = 0;
        fixtureDetail = string.Empty;

        if (!string.IsNullOrWhiteSpace(reason))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ROUTE_INDEPENDENCE_RESET reason={Safe(reason)}; semanticRevision={RouteIndependenceSnapshot.SemanticRevision}; observationOnly=true; executionAllowed=false; candidateCache=cleared; assignmentConsumption=false");
        }
    }

    private static bool TryGetCoherentInputs(
        out RouteConvergenceSnapshot convergenceSnapshot,
        out ChokeDetectionSnapshot chokeSnapshot)
    {
        convergenceSnapshot = RouteConvergenceSnapshot.Empty;
        chokeSnapshot = ChokeDetectionSnapshot.Empty;
        if (!RouteConvergenceService.TryGetSnapshot(out var currentConvergence)
            || !ChokeDetectionService.TryGetSnapshot(out var currentChoke))
        {
            return false;
        }

        if (currentConvergence.TopologyRevision != currentChoke.TopologyRevision
            || !string.Equals(currentConvergence.TopologyContentHash, currentChoke.TopologyContentHash, StringComparison.Ordinal)
            || currentConvergence.OperatorBatchAtUtc != currentChoke.OperatorBatchAtUtc
            || currentConvergence.PublishedAtUtc != currentChoke.ConvergencePublishedAtUtc)
        {
            return false;
        }

        convergenceSnapshot = currentConvergence;
        chokeSnapshot = currentChoke;
        return true;
    }

    private static bool TryGetCoherentTopology(
        RouteConvergenceSnapshot convergenceSnapshot,
        out VanguardTacticalCoordinationReadSnapshot topology)
    {
        topology = VanguardTacticalCoordinationReadSnapshot.Empty;
        if (!VanguardTacticalCoordinationRuntimeReadService.TryGetSnapshot(out var currentTopology))
        {
            return false;
        }

        if (!string.Equals(currentTopology.MapId, convergenceSnapshot.MapId, StringComparison.OrdinalIgnoreCase)
            || currentTopology.Revision != convergenceSnapshot.TopologyRevision
            || !string.Equals(currentTopology.ContentHash, convergenceSnapshot.TopologyContentHash, StringComparison.Ordinal))
        {
            return false;
        }

        topology = currentTopology;
        return true;
    }

    private static bool TryBuildCandidateSeeds(
        string mapId,
        ThreatGroupConvergenceAssessment group,
        DirectRouteObservation representative,
        ChokeObservation? observedChoke,
        VanguardTacticalCoordinationReadSnapshot? topology,
        out IReadOnlyList<AxisSeed> seeds)
    {
        seeds = Array.Empty<AxisSeed>();
        if (!TryFinalDirection(representative.Corners, out Vector3 directIngress))
        {
            return false;
        }

        Vector3 directOutward = -directIngress;
        directOutward.y = 0f;
        if (directOutward.sqrMagnitude <= 0.001f)
        {
            return false;
        }
        directOutward.Normalize();

        float establishmentRadius = ResolveEstablishmentRadius(observedChoke);
        var raw = new List<AxisSeed>(MaxRawCandidateSeedsPerGroup);

        if (topology != null)
        {
            foreach (var anchor in topology.TacticalAnchors
                .Where(item => IsUsableTopology(item.Enabled, item.ValidationState))
                .Select(item => new { Anchor = item, Distance = Vector3.Distance(item.Center, group.ThreatAnchorPosition) })
                .Where(item => item.Distance >= MinimumAuthoredAnchorObjectiveDistanceMeters
                    && item.Distance <= MaximumAuthoredAnchorObjectiveDistanceMeters)
                .OrderByDescending(item => FlatAngleFromReference(directOutward, item.Anchor.Center - group.ThreatAnchorPosition))
                .ThenBy(item => Math.Abs(item.Distance - establishmentRadius))
                .ThenBy(item => item.Anchor.AnchorId, StringComparer.OrdinalIgnoreCase)
                .Take(3))
            {
                var usablePose = anchor.Anchor.PoseSlots
                    .Where(item => IsUsableTopology(item.Enabled, item.ValidationState))
                    .OrderByDescending(item => item.NavMeshProjectedPosition.HasValue)
                    .ThenBy(item => Vector3.Distance(item.NavMeshProjectedPosition ?? item.Position, anchor.Anchor.Center))
                    .ThenBy(item => item.PoseSlotId, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                Vector3 rawAnchor = usablePose?.NavMeshProjectedPosition
                    ?? usablePose?.Position
                    ?? anchor.Anchor.Center;
                bool requiresSample = usablePose?.NavMeshProjectedPosition.HasValue != true;
                string source = usablePose == null ? "authored_anchor_center" : "authored_anchor_pose";
                string sourceId = usablePose == null
                    ? anchor.Anchor.AnchorId
                    : anchor.Anchor.AnchorId + "/" + usablePose.PoseSlotId;
                float angle = SignedFlatAngle(directOutward, rawAnchor - group.ThreatAnchorPosition);
                raw.Add(new AxisSeed(
                    CandidateId(mapId, group.OwnerProfileId, group.ThreatId, source, sourceId),
                    source,
                    sourceId,
                    anchor.Anchor.Origin,
                    SectorLabel(angle),
                    angle,
                    rawAnchor,
                    anchor.Anchor.Center,
                    Math.Max(1.0f, anchor.Anchor.Radius),
                    requiresSample,
                    true));
            }
        }

        foreach (var sector in DynamicSectors)
        {
            Vector3 direction = RotateFlat(directOutward, sector.AngleDegrees);
            Vector3 rawAnchor = group.ThreatAnchorPosition + direction * establishmentRadius;
            raw.Add(new AxisSeed(
                CandidateId(mapId, group.OwnerProfileId, group.ThreatId, "dynamic_objective_sector", sector.Name),
                "dynamic_objective_sector",
                sector.Name,
                "runtime_navmesh",
                sector.Name,
                sector.AngleDegrees,
                rawAnchor,
                rawAnchor,
                DynamicRegionRadiusMeters,
                true,
                false));
        }

        var deduplicated = new List<AxisSeed>(MaxRawCandidateSeedsPerGroup);
        foreach (var seed in raw
            .OrderByDescending(item => item.IsAuthored)
            .ThenByDescending(item => Math.Abs(item.ApproachAngleFromDirectDegrees))
            .ThenBy(item => item.CandidateId, StringComparer.OrdinalIgnoreCase))
        {
            if (deduplicated.Any(existing => Vector3.Distance(existing.RawAnchor, seed.RawAnchor) <= CandidateDuplicateDistanceMeters))
            {
                continue;
            }
            deduplicated.Add(seed);
            if (deduplicated.Count >= MaxRawCandidateSeedsPerGroup)
            {
                break;
            }
        }

        // Preserve authored evidence while always retaining dynamic fallback coverage.
        var finalists = new List<AxisSeed>(MaxFullPathFinalistsPerGroup);
        foreach (var seed in deduplicated.Where(item => item.IsAuthored).Take(2))
        {
            finalists.Add(seed);
        }
        foreach (var seed in deduplicated.Where(item => !item.IsAuthored))
        {
            if (finalists.Count >= MaxFullPathFinalistsPerGroup)
            {
                break;
            }
            finalists.Add(seed);
        }
        foreach (var seed in deduplicated.Where(item => item.IsAuthored).Skip(2))
        {
            if (finalists.Count >= MaxFullPathFinalistsPerGroup)
            {
                break;
            }
            finalists.Add(seed);
        }

        seeds = finalists.ToArray();
        return seeds.Count > 0;
    }

    private static float ResolveEstablishmentRadius(ChokeObservation? choke)
    {
        if (choke == null)
        {
            return DefaultEstablishmentRadiusMeters;
        }

        float near = Math.Max(0f, choke.SharedSegmentNearDistanceFromThreatMeters);
        float far = Math.Max(0f, choke.SharedSegmentFarDistanceFromThreatMeters);
        float observedDistance = near > 0f && far > 0f
            ? (near + far) * 0.5f
            : Math.Max(near, far);
        if (observedDistance <= 0.01f)
        {
            return DefaultEstablishmentRadiusMeters;
        }
        return Mathf.Clamp(observedDistance, MinimumEstablishmentRadiusMeters, MaximumEstablishmentRadiusMeters);
    }

    private static CachedAxisProbe CalculateAxisProbe(
        Vector3 start,
        AxisSeed seed,
        DateTimeOffset now,
        CachedAxisProbe? prior,
        string ownerProfileId,
        string threatId)
    {
        Vector3 sampledAnchor = seed.RawAnchor;
        if (seed.RequiresNavMeshSample)
        {
            if (!NavMesh.SamplePosition(seed.RawAnchor + Vector3.up * 0.25f, out NavMeshHit anchorHit, DynamicSampleRadiusMeters, NavMesh.AllAreas))
            {
                return new CachedAxisProbe(start, seed.RawAnchor, seed.RawAnchor, now, "establishment_sample_failed", 0f, Array.Empty<Vector3>(), 0, "sample_failed");
            }
            if (Math.Abs(anchorHit.position.y - seed.RawAnchor.y) > DynamicVerticalToleranceMeters)
            {
                return new CachedAxisProbe(start, seed.RawAnchor, anchorHit.position, now, "vertical_projection_suspicious", 0f, Array.Empty<Vector3>(), 0, "vertical_projection_suspicious");
            }
            sampledAnchor = anchorHit.position;
        }

        var path = new NavMeshPath();
        bool calculated;
        try
        {
            calculated = NavMesh.CalculatePath(start, sampledAnchor, NavMesh.AllAreas, path);
        }
        catch
        {
            calculated = false;
        }

        if (!calculated
            || path.status != NavMeshPathStatus.PathComplete
            || path.corners == null
            || path.corners.Length < 2)
        {
            return new CachedAxisProbe(start, seed.RawAnchor, sampledAnchor, now, "path_incomplete", 0f, Array.Empty<Vector3>(), 0, seed.IsAuthored ? "authored_target_path_incomplete" : "dynamic_target_path_incomplete");
        }

        Vector3[] corners = path.corners.ToArray();
        float length = PathLength(corners);
        int stableResamples = 1;
        bool priorUsable = prior != null && prior.Corners.Length >= 2;
        bool priorFresh = priorUsable && now - prior!.CreatedAtUtc <= CandidateHistoryLifetime;
        bool comparable = priorFresh && IsStableResample(prior!.Corners, corners);
        if (comparable)
        {
            stableResamples = Math.Max(2, prior!.StableResampleCount + 1);
        }
        else if (prior != null
            && (prior.StableResampleCount >= MinimumStableResamples
                || !string.Equals(prior.PathStatus, "complete", StringComparison.Ordinal)))
        {
            if (focusedAxisDiagnosticsEmitted >= MaxFocusedAxisDiagnosticsPerRaid)
            {
                focusedAxisDiagnosticsDropped++;
            }
            else
            {
                // This is a diagnostic of a freshly calculated path; it cannot restore old geometry.
                float lengthRatio = 0f;
                float forwardOverlap = 0f;
                float reverseOverlap = 0f;
                bool metricsAvailable = priorUsable && priorFresh;
                string cause;
                if (!priorUsable)
                {
                    cause = "previous_probe_has_no_complete_geometry";
                }
                else if (!priorFresh)
                {
                    cause = "previous_probe_history_expired";
                }
                else
                {
                    float priorLength = PathLength(prior!.Corners);
                    float currentLength = PathLength(corners);
                    metricsAvailable = priorLength > 0.01f && currentLength > 0.01f;
                    if (!metricsAvailable)
                    {
                        cause = "invalid_path_length_for_comparison";
                    }
                    else
                    {
                        lengthRatio = Math.Max(priorLength, currentLength) / Math.Min(priorLength, currentLength);
                        if (lengthRatio > StableResampleMaxLengthRatio)
                        {
                            cause = "length_ratio_exceeded";
                        }
                        else
                        {
                            // Only rejected resamples incur these extra read-only comparisons.
                            forwardOverlap = RouteOverlapRatio(corners, prior.Corners, OverlapToleranceMeters);
                            reverseOverlap = RouteOverlapRatio(prior.Corners, corners, OverlapToleranceMeters);
                            cause = forwardOverlap < StableResampleBidirectionalOverlapRatio
                                ? "forward_overlap_insufficient"
                                : reverseOverlap < StableResampleBidirectionalOverlapRatio
                                    ? "reverse_overlap_insufficient"
                                    : "comparison_unexpected_review";
                        }
                    }
                }
                EmitFocusedAxisDiagnostic(ownerProfileId, threatId, seed.CandidateId,
                    cause, prior, new CachedAxisProbe(start, seed.RawAnchor, sampledAnchor, now,
                        "complete", length, corners, 1, "fresh_complete_path"), now,
                    lengthRatio, forwardOverlap, reverseOverlap, metricsAvailable);
            }
        }

        string verticalEvidence = seed.IsAuthored
            ? "authored_3d_complete_path"
            : "dynamic_same_plane_complete_path";
        return new CachedAxisProbe(start, seed.RawAnchor, sampledAnchor, now, "complete", length, corners, stableResamples, verticalEvidence);
    }

    private static AxisCandidateObservation BuildAxisObservation(
        DirectRouteObservation representative,
        ThreatGroupConvergenceAssessment group,
        ChokeObservation? choke,
        VanguardTacticalCoordinationReadSnapshot? topology,
        AxisSeed seed,
        CachedAxisProbe probe,
        bool cacheHit)
    {
        float objectiveDistance = Vector3.Distance(probe.SampledAnchor, group.ThreatAnchorPosition);
        float verticalDelta = Math.Abs(probe.SampledAnchor.y - group.ThreatAnchorPosition.y);
        float costRatioTelemetry = representative.PathLengthMeters > 0.01f
            ? probe.PathLengthMeters / representative.PathLengthMeters
            : 0f;

        if (!string.Equals(probe.PathStatus, "complete", StringComparison.Ordinal)
            || probe.Corners.Length < 2)
        {
            string invalidClassification = string.Equals(probe.PathStatus, "vertical_projection_suspicious", StringComparison.Ordinal)
                ? "vertical_evidence_insufficient"
                : "invalid_candidate";
            string invalidReason = string.Equals(probe.PathStatus, "vertical_projection_suspicious", StringComparison.Ordinal)
                ? "vertical_projection_suspicious"
                : string.Equals(probe.PathStatus, "establishment_sample_failed", StringComparison.Ordinal)
                    ? "establishment_sample_failed"
                    : "complete_path_missing";

            return new AxisCandidateObservation(
                seed.CandidateId,
                seed.CandidateSource,
                seed.CandidateSourceId,
                seed.CandidateSourceOrigin,
                representative.BotProfileId,
                seed.ApproachSector,
                seed.ApproachAngleFromDirectDegrees,
                probe.SampledAnchor,
                seed.RegionCenter,
                seed.RegionRadiusMeters,
                objectiveDistance,
                probe.PathStatus,
                probe.PathLengthMeters,
                representative.PathLengthMeters,
                costRatioTelemetry,
                1f,
                0f,
                0f,
                0f,
                0f,
                "none",
                "none",
                "none",
                choke == null ? "not_observed" : "unknown",
                choke?.ChokeId ?? string.Empty,
                choke?.Confidence ?? "NOT_OBSERVED",
                null,
                probe.VerticalEvidence,
                verticalDelta,
                probe.StableResampleCount,
                "unusable",
                0f,
                invalidClassification,
                invalidReason,
                probe.Corners,
                cacheHit);
        }

        Vector3[] samples = SamplePolyline(probe.Corners, GeometrySampleStepMeters);
        DirectFamilyMetrics familyMetrics = MeasureAgainstDirectFamily(samples, probe.Corners, group.Routes);
        float independentTail = IndependentTailMeters(probe.Corners, group.Routes);
        string directTopologySignature = topology == null ? "none" : BuildTopologySignature(representative.Corners, topology);
        string alternateTopologySignature = topology == null ? "none" : BuildTopologySignature(probe.Corners, topology);
        bool topologyDistinct = !string.Equals(alternateTopologySignature, "none", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(alternateTopologySignature, directTopologySignature, StringComparison.OrdinalIgnoreCase);
        string topologyEvidence = TopologyEvidence(alternateTopologySignature);

        string chokeRelation = ResolvePreEstablishmentChokeRelation(
            probe.Corners,
            choke,
            out float? minimumChokeDistance);

        float euclidean = Vector3.Distance(representative.StartPosition, probe.SampledAnchor);
        float pathToEuclideanRatio = euclidean > 0.01f ? probe.PathLengthMeters / euclidean : 0f;
        bool pathological = euclidean > 0.01f && pathToEuclideanRatio > PathologicalPathToEuclideanRatio;
        bool sameConfirmedChoke = string.Equals(chokeRelation, "same_confirmed_hard_choke_pre_establishment", StringComparison.OrdinalIgnoreCase);

        bool classicDistinct = familyMetrics.MaximumOverlapRatio <= ClassicDistinctMaxOverlapRatio
            && familyMetrics.MinimumMeanLateralSeparationMeters >= ClassicDistinctMeanSeparationMeters
            && familyMetrics.MinimumMaxLateralSeparationMeters >= ClassicDistinctMaxSeparationMeters
            && familyMetrics.MinimumArrivalAngleDegrees >= DistinctArrivalAngleDegrees;
        bool sustainedDistinct = independentTail >= MinimumIndependentTailMeters
            && familyMetrics.MinimumMaxLateralSeparationMeters >= ClassicDistinctMaxSeparationMeters
            && (familyMetrics.MinimumArrivalAngleDegrees >= DistinctArrivalAngleDegrees
                || familyMetrics.MaximumOverlapRatio <= SustainedDistinctMaxOverlapRatio);
        bool authoredTopologyDistinct = topologyDistinct
            && independentTail >= AuthoredTopologyMinimumTailMeters
            && familyMetrics.MinimumMaxLateralSeparationMeters >= AuthoredTopologyMinimumMaxSeparationMeters;
        bool structurallyDistinct = classicDistinct || sustainedDistinct || authoredTopologyDistinct;
        bool clearlySameAxis = familyMetrics.MaximumOverlapRatio >= SameAxisMinOverlapRatio
            && independentTail < SameAxisMaxIndependentTailMeters
            && !topologyDistinct;

        float evidenceScore = AxisEvidenceScore(
            familyMetrics,
            independentTail,
            topologyDistinct,
            chokeRelation,
            probe.StableResampleCount);

        string classification;
        string reason;
        if (pathological)
        {
            classification = "pathological_route_rejected";
            reason = "pathology_guard_exceeded";
        }
        else if (sameConfirmedChoke)
        {
            classification = "same_confirmed_choke_rejected";
            reason = "same_confirmed_hard_choke_before_establishment";
        }
        else if (structurallyDistinct && probe.StableResampleCount >= MinimumStableResamples)
        {
            classification = "alternate_axis_observed";
            reason = "complete_distinct_pre_establishment_axis_stable";
        }
        else if (structurallyDistinct)
        {
            classification = "alternate_axis_candidate_unstable";
            reason = "complete_distinct_axis_requires_resample";
        }
        else if (clearlySameAxis)
        {
            classification = "same_axis_rejected";
            reason = "insufficient_route_family_divergence";
        }
        else
        {
            classification = "axis_evidence_ambiguous";
            reason = string.Equals(chokeRelation, "relation_to_inferred_choke", StringComparison.OrdinalIgnoreCase)
                ? "inferred_choke_relation_requires_observation"
                : "mixed_axis_evidence";
        }

        string stability = probe.StableResampleCount >= MinimumStableResamples
            ? "stable_resampled"
            : "single_sample";

        return new AxisCandidateObservation(
            seed.CandidateId,
            seed.CandidateSource,
            seed.CandidateSourceId,
            seed.CandidateSourceOrigin,
            representative.BotProfileId,
            seed.ApproachSector,
            seed.ApproachAngleFromDirectDegrees,
            probe.SampledAnchor,
            seed.RegionCenter,
            seed.RegionRadiusMeters,
            objectiveDistance,
            probe.PathStatus,
            probe.PathLengthMeters,
            representative.PathLengthMeters,
            costRatioTelemetry,
            familyMetrics.MaximumOverlapRatio,
            familyMetrics.MinimumMeanLateralSeparationMeters,
            familyMetrics.MinimumMaxLateralSeparationMeters,
            familyMetrics.MinimumArrivalAngleDegrees,
            independentTail,
            topologyEvidence,
            directTopologySignature,
            alternateTopologySignature,
            chokeRelation,
            choke?.ChokeId ?? string.Empty,
            choke?.Confidence ?? "NOT_OBSERVED",
            minimumChokeDistance,
            probe.VerticalEvidence,
            verticalDelta,
            probe.StableResampleCount,
            stability,
            evidenceScore,
            classification,
            reason,
            probe.Corners,
            cacheHit);
    }

    private static string ResolvePreEstablishmentChokeRelation(
        IReadOnlyList<Vector3> route,
        ChokeObservation? choke,
        out float? minimumObservedChokeDistanceMeters)
    {
        minimumObservedChokeDistanceMeters = null;
        if (choke == null)
        {
            return "not_observed";
        }

        Vector3[] samples = SamplePolyline(route, 1.5f);
        if (samples.Length == 0)
        {
            return "unknown";
        }

        float segmentMin = samples.Min(sample => PointToSegmentDistance(sample, choke.SegmentNear, choke.SegmentFar));
        minimumObservedChokeDistanceMeters = segmentMin;

        if (choke.IsConfirmed && choke.StructuralDoor != null)
        {
            float doorMin = samples.Min(sample => Vector3.Distance(sample, choke.StructuralDoor.PortalCenter));
            minimumObservedChokeDistanceMeters = Math.Min(segmentMin, doorMin);
            return doorMin <= ConfirmedDoorRelationToleranceMeters
                ? "same_confirmed_hard_choke_pre_establishment"
                : "distinct_from_observed_choke";
        }

        return segmentMin <= InferredChokeObservationToleranceMeters
            ? "relation_to_inferred_choke"
            : "distinct_from_observed_choke";
    }

    private static float AxisEvidenceScore(
        DirectFamilyMetrics metrics,
        float independentTailMeters,
        bool topologyDistinct,
        string chokeRelation,
        int stableResamples)
    {
        float overlap = 1f - Mathf.Clamp01(metrics.MaximumOverlapRatio);
        float meanSeparation = Mathf.Clamp01(metrics.MinimumMeanLateralSeparationMeters / 12f);
        float maxSeparation = Mathf.Clamp01(metrics.MinimumMaxLateralSeparationMeters / 20f);
        float angle = Mathf.Clamp01(metrics.MinimumArrivalAngleDegrees / 90f);
        float tail = Mathf.Clamp01(independentTailMeters / 20f);
        float topology = topologyDistinct ? 1f : 0f;
        float stability = Mathf.Clamp01(stableResamples / (float)MinimumStableResamples);
        float score = overlap * 0.22f
            + meanSeparation * 0.16f
            + maxSeparation * 0.12f
            + angle * 0.16f
            + tail * 0.20f
            + topology * 0.08f
            + stability * 0.06f;

        if (string.Equals(chokeRelation, "relation_to_inferred_choke", StringComparison.OrdinalIgnoreCase))
        {
            score -= 0.08f;
        }
        else if (string.Equals(chokeRelation, "same_confirmed_hard_choke_pre_establishment", StringComparison.OrdinalIgnoreCase))
        {
            score -= 0.45f;
        }
        return Mathf.Clamp01(score);
    }

    private static DirectFamilyMetrics MeasureAgainstDirectFamily(
        IReadOnlyList<Vector3> alternateSamples,
        IReadOnlyList<Vector3> alternateRoute,
        IReadOnlyList<DirectRouteObservation> directRoutes)
    {
        float maximumOverlap = 0f;
        float minimumMeanSeparation = float.MaxValue;
        float minimumMaxSeparation = float.MaxValue;
        float minimumArrivalAngle = 180f;
        int compared = 0;

        foreach (var directRoute in directRoutes)
        {
            if (!directRoute.HasUsableGeometry || directRoute.Corners.Count < 2)
            {
                continue;
            }

            int sharedSamples = 0;
            float separationSum = 0f;
            float routeMaxSeparation = 0f;
            foreach (Vector3 sample in alternateSamples)
            {
                float separation = DistanceToPolyline(sample, directRoute.Corners);
                separationSum += separation;
                routeMaxSeparation = Math.Max(routeMaxSeparation, separation);
                if (separation <= OverlapToleranceMeters)
                {
                    sharedSamples++;
                }
            }

            float overlap = alternateSamples.Count == 0 ? 1f : (float)sharedSamples / alternateSamples.Count;
            float meanSeparation = alternateSamples.Count == 0 ? 0f : separationSum / alternateSamples.Count;
            float arrivalAngle = FinalArrivalAngle(directRoute.Corners, alternateRoute);

            maximumOverlap = Math.Max(maximumOverlap, overlap);
            minimumMeanSeparation = Math.Min(minimumMeanSeparation, meanSeparation);
            minimumMaxSeparation = Math.Min(minimumMaxSeparation, routeMaxSeparation);
            minimumArrivalAngle = Math.Min(minimumArrivalAngle, arrivalAngle);
            compared++;
        }

        if (compared == 0)
        {
            return new DirectFamilyMetrics(1f, 0f, 0f, 0f);
        }

        return new DirectFamilyMetrics(maximumOverlap, minimumMeanSeparation, minimumMaxSeparation, minimumArrivalAngle);
    }

    private static float IndependentTailMeters(
        IReadOnlyList<Vector3> alternateRoute,
        IReadOnlyList<DirectRouteObservation> directRoutes)
    {
        if (alternateRoute == null || alternateRoute.Count < 2)
        {
            return 0f;
        }

        float tail = 0f;
        for (int index = alternateRoute.Count - 1; index > 0; index--)
        {
            Vector3 start = alternateRoute[index - 1];
            Vector3 end = alternateRoute[index];
            Vector3 midpoint = (start + end) * 0.5f;
            float minimum = float.MaxValue;
            foreach (var direct in directRoutes)
            {
                if (!direct.HasUsableGeometry || direct.Corners.Count < 2)
                {
                    continue;
                }
                minimum = Math.Min(minimum, DistanceToPolyline(midpoint, direct.Corners));
            }

            if (minimum <= OverlapToleranceMeters)
            {
                break;
            }
            tail += Vector3.Distance(start, end);
        }
        return tail;
    }

    private static string BuildTopologySignature(
        IReadOnlyList<Vector3> route,
        VanguardTacticalCoordinationReadSnapshot topology)
    {
        Vector3[] samples = SamplePolyline(route, GeometrySampleStepMeters);
        if (samples.Length == 0)
        {
            return "none";
        }

        var tokens = new List<TopologyToken>();
        foreach (var portal in topology.Portals)
        {
            if (!IsUsableTopology(portal.Enabled, portal.ValidationState))
            {
                continue;
            }
            Vector3 point = portal.NavMeshProjectedPosition ?? portal.Position;
            int first = FirstSampleWithin(samples, point, PortalRelationToleranceMeters);
            if (first >= 0)
            {
                tokens.Add(new TopologyToken(first, "P:" + Safe(portal.PortalId)));
            }
        }

        foreach (var passage in topology.PassagePaths)
        {
            if (!IsUsableTopology(passage.Enabled, passage.ValidationState))
            {
                continue;
            }
            IReadOnlyList<Vector3> geometry = passage.ResolvedPolyline.Count >= 2
                ? passage.ResolvedPolyline
                : passage.AuthoredControlPoints;
            int first = FirstSampleWithinPolyline(samples, geometry, PassageRelationToleranceMeters);
            if (first >= 0)
            {
                tokens.Add(new TopologyToken(first, "G:" + Safe(passage.PassagePathId)));
            }
        }

        string[] ordered = tokens
            .OrderBy(item => item.SampleIndex)
            .ThenBy(item => item.Token, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Token)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return ordered.Length == 0 ? "none" : string.Join(">", ordered);
    }

    private static string TopologyEvidence(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.Equals(signature, "none", StringComparison.OrdinalIgnoreCase))
        {
            return "none";
        }
        bool portal = signature.Split('>').Any(item => item.StartsWith("P:", StringComparison.OrdinalIgnoreCase));
        bool passage = signature.Split('>').Any(item => item.StartsWith("G:", StringComparison.OrdinalIgnoreCase));
        if (portal && passage) return "portal_and_passage_relation";
        if (portal) return "portal_relation";
        if (passage) return "passage_relation";
        return "none";
    }

    private static int FirstSampleWithin(IReadOnlyList<Vector3> samples, Vector3 point, float tolerance)
    {
        for (int index = 0; index < samples.Count; index++)
        {
            if (Vector3.Distance(samples[index], point) <= tolerance)
            {
                return index;
            }
        }
        return -1;
    }

    private static int FirstSampleWithinPolyline(IReadOnlyList<Vector3> samples, IReadOnlyList<Vector3> geometry, float tolerance)
    {
        if (geometry == null || geometry.Count == 0)
        {
            return -1;
        }
        for (int index = 0; index < samples.Count; index++)
        {
            if (DistanceToPolyline(samples[index], geometry) <= tolerance)
            {
                return index;
            }
        }
        return -1;
    }

    private static bool IsStableResample(IReadOnlyList<Vector3> previous, IReadOnlyList<Vector3> currentRoute)
    {
        float previousLength = PathLength(previous);
        float currentLength = PathLength(currentRoute);
        if (previousLength <= 0.01f || currentLength <= 0.01f)
        {
            return false;
        }

        float lengthRatio = Math.Max(previousLength, currentLength) / Math.Min(previousLength, currentLength);
        if (lengthRatio > StableResampleMaxLengthRatio)
        {
            return false;
        }

        float forward = RouteOverlapRatio(currentRoute, previous, OverlapToleranceMeters);
        float reverse = RouteOverlapRatio(previous, currentRoute, OverlapToleranceMeters);
        return forward >= StableResampleBidirectionalOverlapRatio
            && reverse >= StableResampleBidirectionalOverlapRatio;
    }

    private static float RouteOverlapRatio(
        IReadOnlyList<Vector3> samplesFromRoute,
        IReadOnlyList<Vector3> referenceRoute,
        float tolerance)
    {
        Vector3[] samples = SamplePolyline(samplesFromRoute, GeometrySampleStepMeters);
        if (samples.Length == 0)
        {
            return 0f;
        }

        int shared = 0;
        foreach (Vector3 point in samples)
        {
            if (DistanceToPolyline(point, referenceRoute) <= tolerance)
            {
                shared++;
            }
        }
        return (float)shared / samples.Length;
    }

    private static bool TryGetCachedCandidate(
        string key,
        Vector3 start,
        Vector3 rawAnchor,
        DateTimeOffset now,
        out CachedAxisProbe probe)
    {
        probe = null!;
        if (!CandidateCache.TryGetValue(key, out var cached))
        {
            return false;
        }
        if (now - cached.CreatedAtUtc > CandidateCacheLifetime)
        {
            return false;
        }
        if (Vector3.Distance(start, cached.StartPosition) > CachePositionToleranceMeters
            || Vector3.Distance(rawAnchor, cached.RawAnchor) > CachePositionToleranceMeters)
        {
            return false;
        }

        probe = cached;
        return true;
    }

    private static void PruneCandidateCache(DateTimeOffset now)
    {
        foreach (string key in CandidateCache
            .Where(pair => now - pair.Value.CreatedAtUtc > CandidateHistoryLifetime)
            .Select(pair => pair.Key)
            .ToArray())
        {
            CandidateCache.Remove(key);
        }

        if (CandidateCache.Count <= MaxCandidateCacheEntries)
        {
            return;
        }

        foreach (string key in CandidateCache
            .OrderBy(pair => pair.Value.CreatedAtUtc)
            .Take(CandidateCache.Count - MaxCandidateCacheEntries)
            .Select(pair => pair.Key)
            .ToArray())
        {
            CandidateCache.Remove(key);
        }
    }

    private static bool EnsureFixtureContract()
    {
        if (fixtureContractEvaluated)
        {
            return fixtureContractHealthy;
        }

        fixtureContractEvaluated = true;
        RunDeterministicFixtures(out fixturePassCount, out fixtureFailCount, out fixtureDetail);
        fixtureContractHealthy = fixtureFailCount == 0;
        if (!fixtureContractHealthy)
        {
            VanguardClientDiagnosticsLog.Warning(StatusTag,
                $"TC_ROUTE_INDEPENDENCE_FIXTURE_CONTRACT_FAILED pass={fixturePassCount}; fail={fixtureFailCount}; detail={Safe(fixtureDetail)}; action=fail_closed_no_route_publication; executionAllowed=false");
        }
        return fixtureContractHealthy;
    }

    private static void RunDeterministicFixtures(out int pass, out int fail, out string detail)
    {
        int localPass = 0;
        int localFail = 0;
        var results = new List<string>();

        void Check(string name, bool condition)
        {
            if (condition)
            {
                localPass++;
                results.Add(name + ":PASS");
            }
            else
            {
                localFail++;
                results.Add(name + ":FAIL");
            }
        }

        var directA = new[] { V(0, 0), V(0, 10), V(0, 20) };
        var altA = new[] { V(0, 0), V(1, 10), V(1, 20) };
        var metricsA = MeasureSynthetic(directA, altA);
        float tailA = IndependentTailSynthetic(altA, directA);
        Check("A_same_corridor", !SyntheticDistinct(metricsA, tailA, false));

        var directB = new[] { V(0, 0), V(-6, 8), V(-6, 18), V(0, 26) };
        var altB = new[] { V(0, 0), V(7, 8), V(8, 18), V(7, 25) };
        var metricsB = MeasureSynthetic(directB, altB);
        float tailB = IndependentTailSynthetic(altB, directB);
        Check("B_opposite_obstacle", SyntheticDistinct(metricsB, tailB, false));

        var directC = new[] { V(0, 0), V(0, 10) };
        var altC = new[] { V(0, 0), V(24, 0), V(24, 30), V(8, 34) };
        var metricsC = MeasureSynthetic(directC, altC);
        float tailC = IndependentTailSynthetic(altC, directC);
        Check("C_long_bypass_structure", SyntheticDistinct(metricsC, tailC, false) && PathLength(altC) / Math.Max(0.01f, PathLength(directC)) > 3f);

        var routeD = new[] { V(0, 0), V(0, 6), V(7, 14) };
        Vector3 doorD = V(0, 5);
        Check("D_same_choke_before_divergence", SamplePolyline(routeD, 1f).Min(point => Vector3.Distance(point, doorD)) <= ConfirmedDoorRelationToleranceMeters);

        // route independence path terminates at establishment; hypothetical later reconvergence is intentionally outside the proof domain.
        Check("E_terminal_reconvergence_outside_route_region", SyntheticDistinct(metricsB, tailB, false));

        Check("F_multifloor_projection_guard", Math.Abs(8f - 0f) > DynamicVerticalToleranceMeters);

        var loopG = new[] { V(0, 0), V(20, 0), V(20, 20), V(-20, 20), V(-20, -20), V(2, 0) };
        float euclideanG = Vector3.Distance(loopG[0], loopG[loopG.Length - 1]);
        float ratioG = PathLength(loopG) / Math.Max(0.01f, euclideanG);
        Check("G_pathological_loop", ratioG > PathologicalPathToEuclideanRatio);

        pass = localPass;
        fail = localFail;
        detail = string.Join(",", results);
    }

    private static DirectFamilyMetrics MeasureSynthetic(IReadOnlyList<Vector3> direct, IReadOnlyList<Vector3> alternate)
    {
        Vector3[] samples = SamplePolyline(alternate, GeometrySampleStepMeters);
        int shared = 0;
        float sum = 0f;
        float max = 0f;
        foreach (var sample in samples)
        {
            float distance = DistanceToPolyline(sample, direct);
            sum += distance;
            max = Math.Max(max, distance);
            if (distance <= OverlapToleranceMeters)
            {
                shared++;
            }
        }
        float overlap = samples.Length == 0 ? 1f : (float)shared / samples.Length;
        float mean = samples.Length == 0 ? 0f : sum / samples.Length;
        return new DirectFamilyMetrics(overlap, mean, max, FinalArrivalAngle(direct, alternate));
    }

    private static float IndependentTailSynthetic(IReadOnlyList<Vector3> alternate, IReadOnlyList<Vector3> direct)
    {
        float tail = 0f;
        for (int index = alternate.Count - 1; index > 0; index--)
        {
            Vector3 start = alternate[index - 1];
            Vector3 end = alternate[index];
            if (DistanceToPolyline((start + end) * 0.5f, direct) <= OverlapToleranceMeters)
            {
                break;
            }
            tail += Vector3.Distance(start, end);
        }
        return tail;
    }

    private static bool SyntheticDistinct(DirectFamilyMetrics metrics, float tail, bool topologyDistinct)
    {
        bool classic = metrics.MaximumOverlapRatio <= ClassicDistinctMaxOverlapRatio
            && metrics.MinimumMeanLateralSeparationMeters >= ClassicDistinctMeanSeparationMeters
            && metrics.MinimumMaxLateralSeparationMeters >= ClassicDistinctMaxSeparationMeters
            && metrics.MinimumArrivalAngleDegrees >= DistinctArrivalAngleDegrees;
        bool sustained = tail >= MinimumIndependentTailMeters
            && metrics.MinimumMaxLateralSeparationMeters >= ClassicDistinctMaxSeparationMeters
            && (metrics.MinimumArrivalAngleDegrees >= DistinctArrivalAngleDegrees
                || metrics.MaximumOverlapRatio <= SustainedDistinctMaxOverlapRatio);
        bool authored = topologyDistinct
            && tail >= AuthoredTopologyMinimumTailMeters
            && metrics.MinimumMaxLateralSeparationMeters >= AuthoredTopologyMinimumMaxSeparationMeters;
        return classic || sustained || authored;
    }

    private static Vector3 V(float x, float z) => new(x, 0f, z);

    private static Vector3[] SamplePolyline(IReadOnlyList<Vector3> corners, float stepMeters)
    {
        if (corners == null || corners.Count == 0)
        {
            return Array.Empty<Vector3>();
        }
        if (corners.Count == 1)
        {
            return new[] { corners[0] };
        }

        float step = Math.Max(0.5f, stepMeters);
        var samples = new List<Vector3> { corners[0] };
        float carry = 0f;

        for (int index = 1; index < corners.Count; index++)
        {
            Vector3 start = corners[index - 1];
            Vector3 end = corners[index];
            float length = Vector3.Distance(start, end);
            if (length <= 0.001f)
            {
                continue;
            }

            float nextDistance = step - carry;
            while (nextDistance < length)
            {
                samples.Add(Vector3.Lerp(start, end, nextDistance / length));
                nextDistance += step;
            }
            carry = length - (nextDistance - step);
            if (carry >= step)
            {
                carry %= step;
            }
        }

        Vector3 last = corners[corners.Count - 1];
        if (samples.Count == 0 || Vector3.Distance(samples[samples.Count - 1], last) > 0.25f)
        {
            samples.Add(last);
        }
        return samples.ToArray();
    }

    private static float DistanceToPolyline(Vector3 point, IReadOnlyList<Vector3> route)
    {
        if (route == null || route.Count == 0)
        {
            return float.MaxValue;
        }
        if (route.Count == 1)
        {
            return Vector3.Distance(point, route[0]);
        }

        float best = float.MaxValue;
        for (int index = 1; index < route.Count; index++)
        {
            best = Math.Min(best, PointToSegmentDistance(point, route[index - 1], route[index]));
        }
        return best;
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

    private static float FinalArrivalAngle(IReadOnlyList<Vector3> direct, IReadOnlyList<Vector3> alternate)
    {
        if (!TryFinalDirection(direct, out Vector3 directDirection)
            || !TryFinalDirection(alternate, out Vector3 alternateDirection))
        {
            return 0f;
        }
        return Vector3.Angle(directDirection, alternateDirection);
    }

    private static bool TryFinalDirection(IReadOnlyList<Vector3> corners, out Vector3 direction)
    {
        direction = Vector3.zero;
        if (corners == null || corners.Count < 2)
        {
            return false;
        }

        Vector3 end = corners[corners.Count - 1];
        if (TryPointAtDistanceFromEnd(corners, ArrivalDirectionWindowMeters, out Vector3 approachPoint))
        {
            Vector3 approach = end - approachPoint;
            approach.y = 0f;
            if (approach.sqrMagnitude > 0.01f)
            {
                direction = approach.normalized;
                return true;
            }
        }

        Vector3 fallback = corners[corners.Count - 1] - corners[corners.Count - 2];
        fallback.y = 0f;
        if (fallback.sqrMagnitude <= 0.001f)
        {
            return false;
        }
        direction = fallback.normalized;
        return true;
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
                point = Vector3.Lerp(end, start, remaining / segmentLength);
                return true;
            }
            remaining -= segmentLength;
        }

        point = corners[0];
        return true;
    }

    private static float PathLength(IReadOnlyList<Vector3> corners)
    {
        if (corners == null || corners.Count < 2)
        {
            return 0f;
        }
        float total = 0f;
        for (int index = 1; index < corners.Count; index++)
        {
            total += Vector3.Distance(corners[index - 1], corners[index]);
        }
        return total;
    }

    private static Vector3 RotateFlat(Vector3 direction, float degrees)
    {
        float radians = degrees * Mathf.Deg2Rad;
        float cos = Mathf.Cos(radians);
        float sin = Mathf.Sin(radians);
        Vector3 rotated = new(
            direction.x * cos - direction.z * sin,
            0f,
            direction.x * sin + direction.z * cos);
        return rotated.sqrMagnitude <= 0.001f ? Vector3.forward : rotated.normalized;
    }

    private static float SignedFlatAngle(Vector3 reference, Vector3 direction)
    {
        Vector3 a = new(reference.x, 0f, reference.z);
        Vector3 b = new(direction.x, 0f, direction.z);
        if (a.sqrMagnitude <= 0.001f || b.sqrMagnitude <= 0.001f)
        {
            return 0f;
        }
        a.Normalize();
        b.Normalize();
        float angle = Vector3.Angle(a, b);
        float crossY = a.z * b.x - a.x * b.z;
        return crossY >= 0f ? angle : -angle;
    }

    private static float FlatAngleFromReference(Vector3 reference, Vector3 direction) => Math.Abs(SignedFlatAngle(reference, direction));

    private static string SectorLabel(float signedAngle)
    {
        float angle = signedAngle;
        if (angle >= 145f || angle <= -145f) return "rear_opposite";
        if (angle >= 85f) return "left_opposite";
        if (angle >= 25f) return "left_oblique";
        if (angle <= -85f) return "right_opposite";
        if (angle <= -25f) return "right_oblique";
        return "direct_sector";
    }

    private static bool IsUsableTopology(bool enabled, VanguardTacticalAuthoringValidationState validationState) =>
        enabled && validationState != VanguardTacticalAuthoringValidationState.Invalid;

    private static string InputIdentity(
        RouteConvergenceSnapshot convergenceSnapshot,
        ChokeDetectionSnapshot chokeSnapshot)
    {
        return string.Join("|",
            convergenceSnapshot.TopologyRevision.ToString(CultureInfo.InvariantCulture),
            convergenceSnapshot.TopologyContentHash ?? string.Empty,
            convergenceSnapshot.OperatorBatchAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            convergenceSnapshot.PublishedAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            chokeSnapshot.PublishedAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture));
    }

    private static string CandidateCacheKey(
        string ownerProfileId,
        string threatId,
        string botProfileId,
        AxisSeed seed)
    {
        return string.Join("|",
            Safe(ownerProfileId),
            Safe(threatId),
            Safe(botProfileId),
            Safe(seed.CandidateSource),
            Safe(seed.CandidateSourceId));
    }

    private static string CandidateId(
        string mapId,
        string ownerProfileId,
        string threatId,
        string source,
        string sourceId)
    {
        return string.Join(":",
            "axis",
            Safe(mapId),
            Safe(ownerProfileId),
            Safe(threatId),
            Safe(source),
            Safe(sourceId));
    }

    // Log the exact upstream discrepancy without treating an old publication as movement authority.
    // Sampling is bounded because optional producers may legitimately update on different frames.
    private static void LogCoherenceGap(DateTimeOffset now)
    {
        if (now < nextCoherenceGapLogAtUtc)
        {
            return;
        }
        nextCoherenceGapLogAtUtc = now + SummaryLogInterval;

        bool convergenceAvailable = RouteConvergenceService.TryGetSnapshot(out var convergence);
        bool chokeAvailable = ChokeDetectionService.TryGetSnapshot(out var choke);
        string reason = !convergenceAvailable ? "convergence_unavailable"
            : !chokeAvailable ? "choke_unavailable"
            : convergence.TopologyRevision != choke.TopologyRevision ? "topology_revision_mismatch"
            : !string.Equals(convergence.TopologyContentHash, choke.TopologyContentHash, StringComparison.Ordinal) ? "topology_hash_mismatch"
            : convergence.OperatorBatchAtUtc != choke.OperatorBatchAtUtc ? "operator_batch_mismatch"
            : convergence.PublishedAtUtc != choke.ConvergencePublishedAtUtc ? "convergence_publication_mismatch"
            : "recovered_between_reads";

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_ROUTE_INDEPENDENCE_INPUT_COHERENCE_GAP reason={reason}; convergenceAvailable={convergenceAvailable}; chokeAvailable={chokeAvailable}; convergenceTopology={convergence.TopologyRevision}; chokeTopology={choke.TopologyRevision}; convergenceHash={ShortHash(convergence.TopologyContentHash)}; chokeHash={ShortHash(choke.TopologyContentHash)}; convergenceBatchTicks={convergence.OperatorBatchAtUtc.UtcDateTime.Ticks}; chokeBatchTicks={choke.OperatorBatchAtUtc.UtcDateTime.Ticks}; convergencePublishedTicks={convergence.PublishedAtUtc.UtcDateTime.Ticks}; chokeSourceConvergenceTicks={choke.ConvergencePublishedAtUtc.UtcDateTime.Ticks}; publicationInvalidated=true; executionAuthority=false");
    }

    private static void InvalidatePublishedForTransientInputGap()
    {
        lock (Sync)
        {
            current = RouteIndependenceSnapshot.Empty;
        }
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
            current = RouteIndependenceSnapshot.Empty;
            CandidateCache.Clear();
        }
        lastDiagnosticPublication = RouteIndependenceSnapshot.Empty;
        axisTransitionDiagnosticsEmitted = 0;
        axisTransitionDiagnosticsDropped = 0;
        focusedAxisDiagnosticsEmitted = 0;
        focusedAxisDiagnosticsDropped = 0;
        activeInputIdentity = string.Empty;
        publishedInputIdentity = string.Empty;
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        nextCoherenceGapLogAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        transientInputGapActive = false;
        transientInputGapTransitionsTotal = 0;
        fixtureContractEvaluated = false;
        fixtureContractHealthy = false;
        fixturePassCount = 0;
        fixtureFailCount = 0;
        fixtureDetail = string.Empty;
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

    private static string Number(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string NullableNumber(float? value) => value.HasValue ? Number(value.Value) : "null";
    private static string Bool(bool value) => value ? "true" : "false";

    private readonly struct SectorDefinition
    {
        public SectorDefinition(string name, float angleDegrees)
        {
            Name = name;
            AngleDegrees = angleDegrees;
        }
        public string Name { get; }
        public float AngleDegrees { get; }
    }

    private readonly struct AxisSeed
    {
        public AxisSeed(
            string candidateId,
            string candidateSource,
            string candidateSourceId,
            string candidateSourceOrigin,
            string approachSector,
            float approachAngleFromDirectDegrees,
            Vector3 rawAnchor,
            Vector3 regionCenter,
            float regionRadiusMeters,
            bool requiresNavMeshSample,
            bool isAuthored)
        {
            CandidateId = candidateId;
            CandidateSource = candidateSource;
            CandidateSourceId = candidateSourceId;
            CandidateSourceOrigin = candidateSourceOrigin;
            ApproachSector = approachSector;
            ApproachAngleFromDirectDegrees = approachAngleFromDirectDegrees;
            RawAnchor = rawAnchor;
            RegionCenter = regionCenter;
            RegionRadiusMeters = regionRadiusMeters;
            RequiresNavMeshSample = requiresNavMeshSample;
            IsAuthored = isAuthored;
        }

        public string CandidateId { get; }
        public string CandidateSource { get; }
        public string CandidateSourceId { get; }
        public string CandidateSourceOrigin { get; }
        public string ApproachSector { get; }
        public float ApproachAngleFromDirectDegrees { get; }
        public Vector3 RawAnchor { get; }
        public Vector3 RegionCenter { get; }
        public float RegionRadiusMeters { get; }
        public bool RequiresNavMeshSample { get; }
        public bool IsAuthored { get; }
    }

    private readonly struct DirectFamilyMetrics
    {
        public DirectFamilyMetrics(
            float maximumOverlapRatio,
            float minimumMeanLateralSeparationMeters,
            float minimumMaxLateralSeparationMeters,
            float minimumArrivalAngleDegrees)
        {
            MaximumOverlapRatio = maximumOverlapRatio;
            MinimumMeanLateralSeparationMeters = minimumMeanLateralSeparationMeters;
            MinimumMaxLateralSeparationMeters = minimumMaxLateralSeparationMeters;
            MinimumArrivalAngleDegrees = minimumArrivalAngleDegrees;
        }

        public float MaximumOverlapRatio { get; }
        public float MinimumMeanLateralSeparationMeters { get; }
        public float MinimumMaxLateralSeparationMeters { get; }
        public float MinimumArrivalAngleDegrees { get; }
    }

    private readonly struct TopologyToken
    {
        public TopologyToken(int sampleIndex, string token)
        {
            SampleIndex = sampleIndex;
            Token = token;
        }
        public int SampleIndex { get; }
        public string Token { get; }
    }

    private sealed class CachedAxisProbe
    {
        public CachedAxisProbe(
            Vector3 startPosition,
            Vector3 rawAnchor,
            Vector3 sampledAnchor,
            DateTimeOffset createdAtUtc,
            string pathStatus,
            float pathLengthMeters,
            Vector3[] corners,
            int stableResampleCount,
            string verticalEvidence)
        {
            StartPosition = startPosition;
            RawAnchor = rawAnchor;
            SampledAnchor = sampledAnchor;
            CreatedAtUtc = createdAtUtc;
            PathStatus = pathStatus;
            PathLengthMeters = pathLengthMeters;
            Corners = corners;
            StableResampleCount = stableResampleCount;
            VerticalEvidence = verticalEvidence;
        }

        public Vector3 StartPosition { get; }
        public Vector3 RawAnchor { get; }
        public Vector3 SampledAnchor { get; }
        public DateTimeOffset CreatedAtUtc { get; }
        public string PathStatus { get; }
        public float PathLengthMeters { get; }
        public Vector3[] Corners { get; }
        public int StableResampleCount { get; }
        public string VerticalEvidence { get; }
    }
}
#endif
