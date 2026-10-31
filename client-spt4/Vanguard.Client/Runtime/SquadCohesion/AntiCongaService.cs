#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Movement.Brain;

// Responsibility: Implements Anti-Conga as the first bounded Tactical Coordinator locomotion influence.
// Flow: A coherent convergence/choke detection/route independence generation identifies one shared choke corridor, leaves the lead Operator untouched, and may issue one short Advance Gate to a trailing Operator already moving under SAIN SearchAction.
// Authority boundary: anti-conga never selects a flank, route family, aim/fire target, door action or persistent state. The gate suppresses only translation for the admitted SAIN path tick; SAIN keeps its SearchAction steering, aiming, firing and suppression callbacks.
// Invariant: admission is conservative, TTL-bounded and generation-stamped; live SAIN movement/survival postconditions are rechecked inside the patched path tick so ambiguity always fails open without callback-order dependence or stale resume.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class AntiCongaService
{
    public const string StatusTag = "VANGUARD_ANTI_CONGA_STATUS";
    public const string RequestKind = "TacticalCoordinatorAntiCongaAdvanceGate";

    // Qualification parameters: deliberately explicit and logged. They are first-tranche values,
    // not hidden doctrine, and remain subject to runtime calibration after anti-conga influence evidence.
    private static readonly TimeSpan GateTtl = TimeSpan.FromSeconds(1.35d);
    private static readonly TimeSpan ReacquireCooldown = TimeSpan.FromSeconds(2.75d);
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(12.0d);
    private static readonly TimeSpan AcquisitionInterval = TimeSpan.FromSeconds(0.20d);
    // anti-conga is path-progress based: each Operator is projected onto its immutable convergence DirectRoute,
    // then normalized onto the shared choke detection distance-from-threat corridor axis. This keeps ordering
    // comparable across slightly different NavMesh routes through the same stair/corridor funnel.
    private const float MaximumOperatorRouteCrossTrackMeters = 2.75f;
    private const float MinimumBeforeThreatMeters = 0.20f;
    private const float SevereStackHeadwayMeters = 0.80f;
    private const float AcquireHeadwayMaximumFloorMeters = 4.00f;
    private const float AcquireHeadwayMaximumCeilingMeters = 6.50f;
    private const float SafeHeadwayReleaseMeters = 7.00f;
    private const float LeaderClearedChokeMeters = 1.00f;
    private const float LeadApproachWindowMinimumMeters = 8.00f;
    private const float LeadApproachWindowMaximumMeters = 16.00f;
    private const float LeadApproachWindowPaddingMeters = 6.00f;
    private const float TrailingApproachExtraMeters = 7.00f;
    private const float MaximumGateAnchorDriftMeters = 1.40f;
    private const float MinimumMovingSpeedForAdmission = 0.10f;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, ActiveAdvanceGateLease> ActiveByGroup = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> CooldownUntilByHeldBot = new(StringComparer.OrdinalIgnoreCase);
    private static AntiCongaSnapshot current = AntiCongaSnapshot.Empty;
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextAcquisitionAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;
    private static bool authorityWasActive;
    private static bool SearchTranslationPatchReady => SearchTranslationGateBridge.IsPatchReady(out _);
    private static string SearchTranslationPatchState
    {
        get
        {
            SearchTranslationGateBridge.IsPatchReady(out string state);
            return state;
        }
    }
    private static long gateGenerationCounter;
    private static int acquisitions;
    private static int acquisitionPassesTotal;
    private static int completions;
    private static int failOpenReleases;
    private static int admissionRejects;
    private static int commandConflicts;
    private static int admissionGateBlocks;
    private static int admissibleChokesEvaluated;
    private static int convergedGroupsMatched;
    private static int routeWitnessesMatched;
    private static int livePairWindows;
    private static int heldCandidatesEvaluated;
    private static int heldCandidatesLocallyAdmissible;
    private static int heldCandidateRejects;
    private static int localFallbackSelections;
    private static int pairSelections;
    private static int pairGeometryPassed;
    private static int cooldownPassed;
    private static int localGatePassed;
    private static int searchPathPassed;
    private static int schedulerGatePassed;
    private static int movementConflictClear;
    private static int gateIssued;
    private static int translationSuppressedTicks;
    private static int livePatchFailOpenReleases;
    private static readonly Dictionary<string, int> AdmissionBlockCounts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> AdmissionBlockDetails = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> HeldCandidateRejectCounts = new(StringComparer.OrdinalIgnoreCase);

    public static void Tick(DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            if (authorityWasActive)
            {
                Reset("runtime_authority_lost");
            }
            return;
        }

        authorityWasActive = true;

        if (!TryGetCoherentInputs(out var convergenceSnapshot, out var chokeSnapshot, out var routeSnapshot))
        {
            ReleaseAll(now, "coherent_observation_generation_unavailable", failOpen: true);
            PublishEmptyIfNeeded(now);
            return;
        }

        TickActiveLeases(convergenceSnapshot, chokeSnapshot, routeSnapshot, now);
        if (now >= nextAcquisitionAtUtc)
        {
            nextAcquisitionAtUtc = now + AcquisitionInterval;
            acquisitionPassesTotal++;
            TryAcquireOneLeasePerEligibleGroup(convergenceSnapshot, chokeSnapshot, routeSnapshot, now);
        }
        Publish(convergenceSnapshot, now);
    }

    public static bool TryGetSnapshot(out AntiCongaSnapshot snapshot)
    {
        lock (Sync)
        {
            snapshot = current;
            return snapshot.IsAvailable;
        }
    }

    public static void Reset(string reason)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ReleaseAll(now, string.IsNullOrWhiteSpace(reason) ? "reset" : reason, failOpen: true);
        lock (Sync)
        {
            ActiveByGroup.Clear();
            CooldownUntilByHeldBot.Clear();
            current = AntiCongaSnapshot.Empty;
        }
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        nextAcquisitionAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        authorityWasActive = false;
        acquisitions = 0;
        acquisitionPassesTotal = 0;
        completions = 0;
        failOpenReleases = 0;
        admissionRejects = 0;
        commandConflicts = 0;
        admissionGateBlocks = 0;
        admissibleChokesEvaluated = 0;
        convergedGroupsMatched = 0;
        routeWitnessesMatched = 0;
        livePairWindows = 0;
        heldCandidatesEvaluated = 0;
        heldCandidatesLocallyAdmissible = 0;
        heldCandidateRejects = 0;
        localFallbackSelections = 0;
        pairSelections = 0;
        pairGeometryPassed = 0;
        cooldownPassed = 0;
        localGatePassed = 0;
        searchPathPassed = 0;
        schedulerGatePassed = 0;
        movementConflictClear = 0;
        gateIssued = 0;
        translationSuppressedTicks = 0;
        livePatchFailOpenReleases = 0;
        gateGenerationCounter = 0;
        lock (Sync)
        {
            AdmissionBlockCounts.Clear();
            AdmissionBlockDetails.Clear();
            HeldCandidateRejectCounts.Clear();
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ANTI_CONGA_RESET reason={Safe(reason)}; activeGates=0; physicalMode=sain_search_translation_gate; movementInfluence=true; staleResume=false; failOpen=true");
        }
    }

    private static void TickActiveLeases(
        RouteConvergenceSnapshot convergenceSnapshot,
        ChokeDetectionSnapshot chokeSnapshot,
        RouteIndependenceSnapshot routeSnapshot,
        DateTimeOffset now)
    {
        ActiveAdvanceGateLease[] leases;
        lock (Sync)
        {
            leases = ActiveByGroup.Values.ToArray();
        }

        foreach (var lease in leases)
        {
            // TTL completion is evaluated before all other lineage/readback checks so a normal
            // bounded expiry is classified as completion rather than as a fail-open state change.
            if (now >= lease.ExpiresAtUtc)
            {
                ReleaseLease(lease, now, "ttl_complete", failOpen: false);
                continue;
            }

            string releaseReason = ValidateActiveLease(lease, convergenceSnapshot, chokeSnapshot, routeSnapshot, now);
            if (releaseReason.Length > 0)
            {
                bool completed = releaseReason.StartsWith("complete:", StringComparison.Ordinal);
                ReleaseLease(lease, now, completed ? releaseReason[9..] : releaseReason, failOpen: !completed);
            }
        }
    }

    private static string ValidateActiveLease(
        ActiveAdvanceGateLease lease,
        RouteConvergenceSnapshot convergenceSnapshot,
        ChokeDetectionSnapshot chokeSnapshot,
        RouteIndependenceSnapshot routeSnapshot,
        DateTimeOffset now)
    {
        if (!SearchTranslationPatchReady)
        {
            return "search_translation_patch_unavailable:" + Safe(SearchTranslationPatchState);
        }
        if (!InputIdentityMatches(lease, convergenceSnapshot, chokeSnapshot, routeSnapshot))
        {
            return "observation_generation_changed";
        }

        var choke = chokeSnapshot.Chokes.FirstOrDefault(item =>
            string.Equals(item.ChokeId, lease.ChokeId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.OwnerProfileId, lease.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, lease.ThreatId, StringComparison.OrdinalIgnoreCase));
        if (choke == null || !IsAdmissibleChoke(choke))
        {
            return "admissible_choke_lost";
        }

        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(lease.HeldBotProfileId, out var held)
            || held == null
            || !held.Alive)
        {
            return "held_operator_missing_or_dead";
        }

        string localGate = ValidateHeldOperatorDuringInfluence(held, lease.ThreatId, now);
        if (localGate.Length > 0)
        {
            return localGate;
        }

        string pathGate = ValidateSainSearchPathPresent(held.BotProfileId, out _);
        if (pathGate.Length > 0)
        {
            return pathGate;
        }

        if (!VanguardMainIntentScheduler.TryGrantTacticalCoordinatorCombatMovementSidecar(
                held,
                now,
                lease.ThreatId,
                out string schedulerWindowId,
                out string schedulerReason))
        {
            return "scheduler_sidecar_revoked:" + schedulerReason;
        }

        if (!string.Equals(schedulerWindowId, lease.SchedulerWindowId, StringComparison.OrdinalIgnoreCase))
        {
            return "scheduler_window_generation_changed";
        }

        if (!TryGetLivePosition(lease.HeldBotProfileId, out var heldLivePosition, out string heldPositionReason))
        {
            return "held_live_position_unavailable:" + heldPositionReason;
        }
        if (HorizontalDistance(heldLivePosition, lease.GateAnchor) > MaximumGateAnchorDriftMeters)
        {
            return "advance_gate_anchor_drift";
        }

        var group = convergenceSnapshot.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, lease.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, lease.ThreatId, StringComparison.OrdinalIgnoreCase));
        if (group == null)
        {
            return "path_progress_group_missing";
        }

        var leadRoute = group.Routes.FirstOrDefault(item => string.Equals(item.BotProfileId, lease.LeadBotProfileId, StringComparison.OrdinalIgnoreCase));
        var heldRoute = group.Routes.FirstOrDefault(item => string.Equals(item.BotProfileId, lease.HeldBotProfileId, StringComparison.OrdinalIgnoreCase));
        if (leadRoute == null || heldRoute == null)
        {
            return "path_progress_route_missing";
        }
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(lease.LeadBotProfileId, out var lead)
            || lead == null
            || !lead.Alive)
        {
            return "lead_operator_missing_or_dead";
        }

        bool leadProgressAvailable = TryBuildRouteProgress(leadRoute, lead, choke, allowPastThreat: true, now, out var leadProgress, out string leadProgressReason);
        bool heldProgressAvailable = TryBuildRouteProgress(heldRoute, held, choke, allowPastThreat: true, now, out var heldProgress, out string heldProgressReason);
        if (!leadProgressAvailable || !heldProgressAvailable)
        {
            return "path_progress_unavailable:" + Safe(leadProgressReason + "+" + heldProgressReason);
        }

        if (leadProgress.DistanceFromThreatMeters <= choke.SharedSegmentNearDistanceFromThreatMeters - LeaderClearedChokeMeters)
        {
            return "complete:leader_cleared_shared_corridor";
        }

        float headway = heldProgress.DistanceFromThreatMeters - leadProgress.DistanceFromThreatMeters;
        if (headway >= SafeHeadwayReleaseMeters)
        {
            return "complete:safe_path_headway_reached";
        }
        if (headway < -0.10f)
        {
            return "path_ordering_flipped";
        }

        return string.Empty;
    }

    private static void TryAcquireOneLeasePerEligibleGroup(
        RouteConvergenceSnapshot convergenceSnapshot,
        ChokeDetectionSnapshot chokeSnapshot,
        RouteIndependenceSnapshot routeSnapshot,
        DateTimeOffset now)
    {
        if (!SearchTranslationPatchReady)
        {
            RecordAdmissionBlock("search_translation_patch_unavailable:" + SearchTranslationPatchState);
            return;
        }

        foreach (var choke in chokeSnapshot.Chokes
            .Where(IsAdmissibleChoke)
            .OrderByDescending(item => item.IsConfirmed)
            .ThenBy(item => item.OwnerProfileId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ThreatId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ChokeId, StringComparer.OrdinalIgnoreCase))
        {
            string groupKey = GroupKey(choke.OwnerProfileId, choke.ThreatId, choke.ChokeId);
            lock (Sync)
            {
                if (ActiveByGroup.ContainsKey(groupKey))
                {
                    continue;
                }
            }

            admissibleChokesEvaluated++;
            var group = convergenceSnapshot.ThreatGroups.FirstOrDefault(item =>
                string.Equals(item.OwnerProfileId, choke.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ThreatId, choke.ThreatId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Classification, "shared_threat_paths_converged", StringComparison.Ordinal));
            if (group == null || group.Routes.Count < 2)
            {
                RecordAdmissionBlock("converged_group_missing_or_lt2_routes");
                continue;
            }
            convergedGroupsMatched++;

            // route independence remains a generation/coherence witness only. anti-conga still does not assign Direct vs Alt.
            bool routeWitness = routeSnapshot.ThreatGroups.Any(item =>
                string.Equals(item.OwnerProfileId, choke.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ThreatId, choke.ThreatId, StringComparison.OrdinalIgnoreCase));
            if (!routeWitness)
            {
                RecordAdmissionBlock("route_generation_witness_missing");
                continue;
            }
            routeWitnessesMatched++;

            float leadApproachExtra = Mathf.Clamp(
                choke.SharedSegmentMeters + LeadApproachWindowPaddingMeters,
                LeadApproachWindowMinimumMeters,
                LeadApproachWindowMaximumMeters);
            float leadMaximumDistanceFromThreat = choke.SharedSegmentFarDistanceFromThreatMeters + leadApproachExtra;
            float trailingMaximumDistanceFromThreat = leadMaximumDistanceFromThreat + TrailingApproachExtraMeters;
            float acquireHeadwayMaximum = Mathf.Clamp(
                2.0f + choke.SharedSegmentMeters * 0.60f,
                AcquireHeadwayMaximumFloorMeters,
                AcquireHeadwayMaximumCeilingMeters);

            var live = new List<RouteProgress>();
            foreach (var route in group.Routes.Where(item => item.HasUsableGeometry))
            {
                if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(route.BotProfileId, out var snapshot)
                    || snapshot == null
                    || !snapshot.Alive
                    || !string.Equals(snapshot.OwnerProfileId, choke.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(NormalizeThreat(snapshot.Threat.EnemyId), NormalizeThreat(choke.ThreatId), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryBuildRouteProgress(route, snapshot, choke, allowPastThreat: false, now, out var progress, out string progressReason))
                {
                    RecordAdmissionBlock("route_progress_rejected:" + progressReason);
                    continue;
                }
                if (progress.DistanceFromThreatMeters > trailingMaximumDistanceFromThreat)
                {
                    RecordAdmissionBlock("route_progress_outside_approach_window");
                    continue;
                }
                if (progress.DistanceFromThreatMeters < choke.SharedSegmentNearDistanceFromThreatMeters - LeaderClearedChokeMeters)
                {
                    RecordAdmissionBlock("route_progress_past_shared_corridor");
                    continue;
                }
                live.Add(progress);
            }

            if (live.Count < 2)
            {
                RecordAdmissionBlock("path_progress_candidates_lt2");
                continue;
            }
            livePairWindows++;

            // Member-local veto doctrine: evaluate every possible trailing member independently.
            // Geometry then chooses among members that can actually accept the translation gate.
            var admissibleHeld = new Dictionary<string, HeldAdmission>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in live)
            {
                heldCandidatesEvaluated++;
                if (IsHeldBotAlreadyGated(candidate.Snapshot.BotProfileId))
                {
                    RecordHeldCandidateReject("held_operator_already_gated");
                    continue;
                }
                if (IsCooldownActive(candidate.Snapshot.BotProfileId, now))
                {
                    RecordHeldCandidateReject("held_operator_reacquire_cooldown");
                    continue;
                }

                string candidateGate = ValidateHeldOperatorForAdmission(candidate.Snapshot, choke.ThreatId, now);
                if (candidateGate.Length > 0)
                {
                    RecordHeldCandidateReject(candidateGate);
                    continue;
                }
                heldCandidatesLocallyAdmissible++;

                string pathGate = ValidateSainSearchPathPresent(candidate.Snapshot.BotProfileId, out string pathSummary);
                if (pathGate.Length > 0)
                {
                    RecordHeldCandidateReject(pathGate + ":" + pathSummary);
                    continue;
                }

                if (!VanguardMainIntentScheduler.TryGrantTacticalCoordinatorCombatMovementSidecar(
                        candidate.Snapshot,
                        now,
                        choke.ThreatId,
                        out string schedulerWindowId,
                        out string schedulerReason))
                {
                    RecordHeldCandidateReject("scheduler_sidecar_rejected:" + schedulerReason);
                    continue;
                }

                if (VanguardReturnMovementCommandStore.TryGetActive(candidate.Snapshot.BotProfileId, now, out var existingCommand))
                {
                    RecordHeldCandidateReject("existing_movement_command:" + Safe(existingCommand.RequestKind));
                    continue;
                }

                admissibleHeld[candidate.Snapshot.BotProfileId] = new HeldAdmission(schedulerWindowId, pathSummary);
            }

            if (admissibleHeld.Count == 0)
            {
                admissionRejects++;
                RecordAdmissionBlock("no_locally_admissible_trailing_candidate");
                continue;
            }

            if (!TrySelectPathAwarePair(
                    group,
                    live,
                    admissibleHeld.Keys,
                    leadMaximumDistanceFromThreat,
                    trailingMaximumDistanceFromThreat,
                    acquireHeadwayMaximum,
                    out var lead,
                    out var held,
                    out bool localFallbackUsed,
                    out string pairReason))
            {
                admissionRejects++;
                RecordAdmissionBlock(pairReason);
                continue;
            }
            pairSelections++;
            if (localFallbackUsed)
            {
                localFallbackSelections++;
            }

            float pathHeadway = Mathf.Max(0f, held.DistanceFromThreatMeters - lead.DistanceFromThreatMeters);
            pairGeometryPassed++;
            cooldownPassed++;
            localGatePassed++;
            searchPathPassed++;
            schedulerGatePassed++;
            movementConflictClear++;

            HeldAdmission heldAdmission = admissibleHeld[held.Snapshot.BotProfileId];
            string leaseId = BuildLeaseId(convergenceSnapshot, choke, held.Snapshot.BotProfileId, now);
            Vector3 gateAnchor = held.LivePosition;
            DateTimeOffset expires = now + GateTtl;
            long gateGeneration;
            lock (Sync)
            {
                gateGeneration = ++gateGenerationCounter;
            }

            var lease = new ActiveAdvanceGateLease
            {
                LeaseId = leaseId,
                GroupKey = groupKey,
                OwnerProfileId = choke.OwnerProfileId,
                ThreatId = choke.ThreatId,
                ChokeId = choke.ChokeId,
                ChokeKind = choke.Kind,
                ChokeConfidence = choke.Confidence,
                LeadBotProfileId = lead.Snapshot.BotProfileId,
                HeldBotProfileId = held.Snapshot.BotProfileId,
                SchedulerWindowId = heldAdmission.SchedulerWindowId,
                GateGeneration = gateGeneration,
                GateAnchor = gateAnchor,
                StartedAtUtc = now,
                ExpiresAtUtc = expires,
                MapId = convergenceSnapshot.MapId,
                TopologyRevision = convergenceSnapshot.TopologyRevision,
                TopologyContentHash = convergenceSnapshot.TopologyContentHash,
                OperatorBatchAtUtc = convergenceSnapshot.OperatorBatchAtUtc,
                ConvergencePublishedAtUtc = convergenceSnapshot.PublishedAtUtc,
                ChokePublishedAtUtc = chokeSnapshot.PublishedAtUtc,
                RoutePublishedAtUtc = routeSnapshot.PublishedAtUtc
            };

            lock (Sync)
            {
                ActiveByGroup[groupKey] = lease;
            }
            gateIssued++;
            acquisitions++;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ANTI_CONGA_LEASE_ACQUIRED owner={Safe(choke.OwnerProfileId)}; threat={Safe(choke.ThreatId)}; choke={Safe(choke.ChokeId)}; leadBot={Safe(lead.Snapshot.BotProfileId)}; heldBot={Safe(held.Snapshot.BotProfileId)}; leadThreatDistance={lead.DistanceFromThreatMeters:0.00}; heldThreatDistance={held.DistanceFromThreatMeters:0.00}; pathHeadway={pathHeadway:0.00}; severeStack={pathHeadway <= SevereStackHeadwayMeters}; leadCrossTrack={lead.OperatorCrossTrackMeters:0.00}; heldCrossTrack={held.OperatorCrossTrackMeters:0.00}; corridorNear={choke.SharedSegmentNearDistanceFromThreatMeters:0.00}; corridorFar={choke.SharedSegmentFarDistanceFromThreatMeters:0.00}; chokeKind={Safe(choke.Kind)}; chokeConfidence={Safe(choke.Confidence)}; lease={Safe(leaseId)}; gateGeneration={gateGeneration}; schedulerWindow={Safe(heldAdmission.SchedulerWindowId)}; sainPath={Safe(heldAdmission.PathSummary)}; ttl={GateTtl.TotalSeconds:0.00}; request={RequestKind}; physicalMode=sain_search_translation_gate; sainActionPreserved=true; sainSteeringPreserved=true; aimFireAuthority=false; routeAssignment=false; flank=false; failOpen=true");
        }
    }

    private static bool TrySelectPathAwarePair(
        ThreatGroupConvergenceAssessment group,
        IReadOnlyList<RouteProgress> candidates,
        IEnumerable<string> admissibleHeldBotIds,
        float leadMaximumDistanceFromThreat,
        float trailingMaximumDistanceFromThreat,
        float acquireHeadwayMaximum,
        out RouteProgress lead,
        out RouteProgress held,
        out bool localFallbackUsed,
        out string reason)
    {
        lead = default;
        held = default;
        localFallbackUsed = false;
        reason = "path_pair_unavailable";
        var admissibleHeld = new HashSet<string>(admissibleHeldBotIds, StringComparer.OrdinalIgnoreCase);
        RoutePairCandidate? best = null;
        RoutePairCandidate? bestUnrestricted = null;

        for (int i = 0; i < candidates.Count; i++)
        {
            for (int j = 0; j < candidates.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                RouteProgress first = candidates[i];
                RouteProgress second = candidates[j];
                float delta = second.DistanceFromThreatMeters - first.DistanceFromThreatMeters;
                if (delta < -0.01f)
                {
                    continue;
                }
                if (Mathf.Abs(delta) <= 0.01f
                    && string.Compare(first.Snapshot.BotProfileId, second.Snapshot.BotProfileId, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }
                if (first.DistanceFromThreatMeters > leadMaximumDistanceFromThreat
                    || second.DistanceFromThreatMeters > trailingMaximumDistanceFromThreat)
                {
                    continue;
                }

                float headway = Mathf.Max(0f, delta);
                if (headway > acquireHeadwayMaximum)
                {
                    continue;
                }
                if (!IsConvergedPair(group, first.Snapshot.BotProfileId, second.Snapshot.BotProfileId))
                {
                    continue;
                }

                var candidate = new RoutePairCandidate(first, second, headway);
                if (IsPreferredPair(candidate, bestUnrestricted))
                {
                    bestUnrestricted = candidate;
                }
                if (!admissibleHeld.Contains(second.Snapshot.BotProfileId))
                {
                    continue;
                }
                if (IsPreferredPair(candidate, best))
                {
                    best = candidate;
                }
            }
        }

        if (best == null)
        {
            if (bestUnrestricted != null)
            {
                reason = "no_locally_admissible_trailing_pair_in_headway_window";
                return false;
            }

            bool anyConvergedPair = false;
            for (int i = 0; i < candidates.Count && !anyConvergedPair; i++)
            {
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    if (IsConvergedPair(group, candidates[i].Snapshot.BotProfileId, candidates[j].Snapshot.BotProfileId))
                    {
                        anyConvergedPair = true;
                        break;
                    }
                }
            }
            reason = anyConvergedPair ? "path_headway_outside_acquire_window" : "path_pair_not_converged";
            return false;
        }

        localFallbackUsed = bestUnrestricted != null
            && (!string.Equals(bestUnrestricted.Value.Lead.Snapshot.BotProfileId, best.Value.Lead.Snapshot.BotProfileId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(bestUnrestricted.Value.Held.Snapshot.BotProfileId, best.Value.Held.Snapshot.BotProfileId, StringComparison.OrdinalIgnoreCase));
        lead = best.Value.Lead;
        held = best.Value.Held;
        reason = "ok";
        return true;
    }

    private static bool IsPreferredPair(RoutePairCandidate candidate, RoutePairCandidate? current)
    {
        return current == null
            || candidate.Lead.DistanceFromThreatMeters < current.Value.Lead.DistanceFromThreatMeters
            || (Mathf.Approximately(candidate.Lead.DistanceFromThreatMeters, current.Value.Lead.DistanceFromThreatMeters)
                && candidate.HeadwayMeters < current.Value.HeadwayMeters);
    }

    private static bool IsConvergedPair(
        ThreatGroupConvergenceAssessment group,
        string firstBotProfileId,
        string secondBotProfileId)
    {
        return group.Pairs.Any(pair =>
            ((string.Equals(pair.FirstBotProfileId, firstBotProfileId, StringComparison.OrdinalIgnoreCase)
              && string.Equals(pair.SecondBotProfileId, secondBotProfileId, StringComparison.OrdinalIgnoreCase))
             || (string.Equals(pair.FirstBotProfileId, secondBotProfileId, StringComparison.OrdinalIgnoreCase)
                 && string.Equals(pair.SecondBotProfileId, firstBotProfileId, StringComparison.OrdinalIgnoreCase)))
            && string.Equals(pair.Classification, "converged", StringComparison.Ordinal));
    }

    private static bool TryBuildRouteProgress(
        DirectRouteObservation route,
        OperatorDecisionSnapshot snapshot,
        ChokeObservation choke,
        bool allowPastThreat,
        DateTimeOffset now,
        out RouteProgress progress,
        out string reason)
    {
        progress = default;
        reason = "unknown";
        if (route == null || snapshot == null || !route.HasUsableGeometry || route.Corners.Count < 2)
        {
            reason = "route_geometry_unavailable";
            return false;
        }
        if (choke.SharedSegmentFarDistanceFromThreatMeters <= choke.SharedSegmentNearDistanceFromThreatMeters)
        {
            reason = "shared_corridor_scalar_invalid";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(snapshot, now, out var freshnessReason))
        {
            reason = freshnessReason;
            return false;
        }
        if (!TryGetLivePosition(snapshot.BotProfileId, out var livePosition, out string livePositionReason))
        {
            reason = "live_position_unavailable:" + livePositionReason;
            return false;
        }
        if (!TryProjectOntoRoute(route.Corners, livePosition, out var operatorProjection))
        {
            reason = "operator_projection_failed";
            return false;
        }
        if (operatorProjection.CrossTrackMeters > MaximumOperatorRouteCrossTrackMeters)
        {
            reason = "operator_cross_track_exceeded";
            return false;
        }

        // choke detection already derives the shared corridor on a canonical scalar: distance from the common
        // threat endpoint. Preserve that scalar here instead of projecting one world-space choke
        // center independently onto each route, which made different DirectRoute odometers
        // incomparable in stairs/corridors.
        float distanceFromThreat = Mathf.Max(0f, operatorProjection.TotalLengthMeters - operatorProjection.AlongMeters);
        if (!allowPastThreat && distanceFromThreat < MinimumBeforeThreatMeters)
        {
            reason = "operator_at_threat_endpoint";
            return false;
        }

        progress = new RouteProgress(
            route,
            snapshot,
            livePosition,
            operatorProjection.AlongMeters,
            operatorProjection.CrossTrackMeters,
            operatorProjection.TotalLengthMeters,
            distanceFromThreat);
        reason = "ok";
        return true;
    }

    private static bool TryProjectOntoRoute(
        IReadOnlyList<Vector3> corners,
        Vector3 point,
        out RouteProjection projection)
    {
        projection = default;
        if (corners == null || corners.Count < 2)
        {
            return false;
        }

        float bestCrossTrack = float.MaxValue;
        float bestAlong = 0f;
        Vector3 bestPoint = Vector3.zero;
        float cumulative = 0f;
        bool found = false;
        for (int i = 1; i < corners.Count; i++)
        {
            Vector3 start = corners[i - 1];
            Vector3 end = corners[i];
            Vector3 segment = end - start;
            float length = segment.magnitude;
            if (length <= 0.001f)
            {
                continue;
            }
            float t = Mathf.Clamp01(Vector3.Dot(point - start, segment) / (length * length));
            Vector3 projected = start + segment * t;
            float crossTrack = Vector3.Distance(point, projected);
            if (!found || crossTrack < bestCrossTrack)
            {
                found = true;
                bestCrossTrack = crossTrack;
                bestAlong = cumulative + length * t;
                bestPoint = projected;
            }
            cumulative += length;
        }

        if (!found)
        {
            return false;
        }
        projection = new RouteProjection(bestAlong, bestCrossTrack, bestPoint, cumulative);
        return true;
    }

    private static string ValidateHeldOperatorForAdmission(OperatorDecisionSnapshot snapshot, string threatId, DateTimeOffset now)
    {
        string common = ValidateHeldOperatorCommon(snapshot, threatId, now);
        if (common.Length > 0)
        {
            return common;
        }
        if (snapshot.RealSpeed < MinimumMovingSpeedForAdmission)
        {
            return "search_translation_not_moving";
        }
        return string.Empty;
    }

    private static string ValidateHeldOperatorDuringInfluence(OperatorDecisionSnapshot snapshot, string threatId, DateTimeOffset now)
    {
        string common = ValidateHeldOperatorCommon(snapshot, threatId, now);
        if (common.Length > 0)
        {
            return common;
        }
        return string.Empty;
    }

    private static string ValidateHeldOperatorCommon(OperatorDecisionSnapshot snapshot, string threatId, DateTimeOffset now)
    {
        if (snapshot == null || !snapshot.Alive)
        {
            return "operator_dead_or_missing";
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(snapshot, now, out var freshnessReason))
        {
            return freshnessReason;
        }
        if (snapshot.Sain == null || !snapshot.Sain.ComponentPresent)
        {
            return "sain_component_missing";
        }
        if (snapshot.Sain.Active != true)
        {
            return "sain_inactive_or_unknown";
        }
        if (!string.Equals(NormalizeThreat(snapshot.Threat.EnemyId), NormalizeThreat(threatId), StringComparison.OrdinalIgnoreCase))
        {
            return "shared_threat_identity_changed";
        }
        if (snapshot.GrenadeHazard.HasRelevantHazard || snapshot.Medical.Actionability.GrenadeThrowing)
        {
            return "grenade_emergency_or_throw";
        }
        if (snapshot.Medical.Actionability.AnyMedicineUsing)
        {
            return "medical_controller_active";
        }
        if (snapshot.Medical.Actionability.Reloading)
        {
            return "reload_active";
        }
        if (VanguardExecutionLeaseCoordinator.HasActiveLease(snapshot.BotProfileId))
        {
            return "existing_execution_lease";
        }
        string searchContextReason = TacticalSearchPolicy.EvaluateSnapshot(snapshot.Sain);
        if (searchContextReason.Length > 0)
        {
            return searchContextReason;
        }
        if (snapshot.Sain.HasEnemy != true || snapshot.Sain.IsInCombat != true)
        {
            return "sain_combat_context_missing";
        }
        return string.Empty;
    }

    private static bool TryGetLivePosition(string botProfileId, out Vector3 position, out string reason)
    {
        position = Vector3.zero;
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botProfileId, out var runtime)
            || runtime.BotOwner == null
            || runtime.BotOwner.IsDead)
        {
            reason = "runtime_botowner_missing";
            return false;
        }
        position = runtime.BotOwner.Position;
        reason = "ok";
        return true;
    }

    private static bool IsAdmissibleChoke(ChokeObservation choke)
    {
        if (choke == null
            || choke.RouteCount < 2
            || choke.SharedSegmentMeters <= 0f
            || choke.SharedSegmentFarDistanceFromThreatMeters <= choke.SharedSegmentNearDistanceFromThreatMeters)
        {
            return false;
        }

        return (string.Equals(choke.Kind, "choke", StringComparison.OrdinalIgnoreCase)
                && string.Equals(choke.Confidence, "INFERRED", StringComparison.OrdinalIgnoreCase))
            || (choke.IsHardChoke && choke.IsConfirmed);
    }

    private static string ValidateSainSearchPathPresent(string botProfileId, out string summary)
    {
        summary = "unknown";
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botProfileId, out var runtime)
            || runtime.BotOwner == null
            || runtime.BotOwner.IsDead)
        {
            summary = "runtime_missing";
            return "runtime_botowner_missing";
        }

        object? sain = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(runtime.BotOwner, "SAIN.Components.BotComponent");
        if (sain == null)
        {
            summary = "sain_component_missing";
            return "sain_component_missing";
        }

        object? mover = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "Mover");
        if (mover == null)
        {
            summary = "sain_mover_missing";
            return "sain_mover_missing";
        }

        object? activePath = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "ActivePath");
        if (activePath == null)
        {
            summary = "activePath=null";
            return "sain_search_path_missing";
        }

        string pathType = VanguardOperatorRuntimeAuditReflection.TypeName(activePath);
        string stateGate = ValidateSainManualPathGateState(activePath, out string pathStateSummary);
        summary = "activePath=" + Safe(pathType) + ":" + pathStateSummary;
        if (!IsBotPathDataManual(pathType))
        {
            return "sain_search_path_type_unexpected";
        }
        return stateGate;
    }

    private static string ValidateSainManualPathGateState(object activePath, out string summary)
    {
        string typeName = VanguardOperatorRuntimeAuditReflection.TypeName(activePath);
        string status = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "Status"));
        bool pausedRequested = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "PausedRequested")) == true;
        bool cancelRequested = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "CancelRequested")) == true;
        bool running = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "Running")) == true;
        summary = "type=" + Safe(typeName)
            + ":status=" + Safe(status)
            + ":pausedRequested=" + Bool(pausedRequested)
            + ":cancelRequested=" + Bool(cancelRequested)
            + ":running=" + Bool(running);

        if (!IsBotPathDataManual(typeName))
        {
            return "sain_search_path_type_unexpected";
        }
        if (cancelRequested)
        {
            return "sain_search_path_cancel_pending";
        }
        if (pausedRequested || string.Equals(status, "Paused", StringComparison.OrdinalIgnoreCase))
        {
            return "sain_search_path_paused_by_sain";
        }
        if (string.Equals(status, "DoorInteraction", StringComparison.OrdinalIgnoreCase))
        {
            return "sain_search_path_door_interaction";
        }
        // SAIN BotPathDataManual.Running is Moving && WantToSprint: it represents the path's
        // sprint intent, not proof that translation must remain physically active this tick.
        // anti-conga may gate translation without mutating WantToSprint; the live hook clears only the
        // character controller's physical sprint request for the suppressed tick.
        if (!string.Equals(status, "Moving", StringComparison.OrdinalIgnoreCase))
        {
            return "sain_search_path_not_normal_moving:" + Safe(status);
        }
        return string.Empty;
    }

    private static bool IsHeldBotAlreadyGated(string botProfileId)
    {
        if (HasActiveAdvanceGateForBot(botProfileId))
        {
            return true;
        }
        return ChokeCommitSupportService.HasActiveContributionForBot(botProfileId);
    }

    internal static SearchTranslationDispatchResult TryHandleSharedSearchTranslationGate(
        object pathData,
        object sainBot,
        BotOwner botOwner,
        DateTimeOffset now,
        out string reason)
    {
        reason = "no_anti_conga_gate_for_bot";
        if (!SearchTranslationPatchReady || pathData == null || sainBot == null || botOwner == null)
        {
            return SearchTranslationDispatchResult.NoLease;
        }

        ActiveAdvanceGateLease? lease;
        lock (Sync)
        {
            lease = ActiveByGroup.Values.FirstOrDefault(item =>
                string.Equals(item.HeldBotProfileId, botOwner.ProfileId, StringComparison.OrdinalIgnoreCase));
        }
        if (lease == null)
        {
            return SearchTranslationDispatchResult.NoLease;
        }

        try
        {
            if (now >= lease.ExpiresAtUtc)
            {
                ReleaseLease(lease, now, "ttl_complete", failOpen: false);
                reason = "ttl_complete";
                return SearchTranslationDispatchResult.HandledFailOpen;
            }

            if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(lease.HeldBotProfileId, out var liveDecision)
                || liveDecision == null
                || !liveDecision.Alive)
            {
                livePatchFailOpenReleases++;
                ReleaseLease(lease, now, "live_operator_missing_or_dead", failOpen: true);
                reason = "live_operator_missing_or_dead";
                return SearchTranslationDispatchResult.HandledFailOpen;
            }

            string liveDecisionGate = ValidateHeldOperatorDuringInfluence(liveDecision, lease.ThreatId, now);
            if (liveDecisionGate.Length > 0)
            {
                livePatchFailOpenReleases++;
                ReleaseLease(lease, now, "live_local_gate:" + liveDecisionGate, failOpen: true);
                reason = "live_local_gate:" + liveDecisionGate;
                return SearchTranslationDispatchResult.HandledFailOpen;
            }

            if (HorizontalDistance(botOwner.Position, lease.GateAnchor) > MaximumGateAnchorDriftMeters)
            {
                livePatchFailOpenReleases++;
                ReleaseLease(lease, now, "live_gate_anchor_drift", failOpen: true);
                reason = "live_gate_anchor_drift";
                return SearchTranslationDispatchResult.HandledFailOpen;
            }

            if (!SearchTranslationGateBridge.TryApplyExactSearchGate(
                    pathData,
                    sainBot,
                    botOwner,
                    now,
                    lease.ThreatId,
                    lease.SchedulerWindowId,
                    out _,
                    out string liveGateReason))
            {
                livePatchFailOpenReleases++;
                ReleaseLease(lease, now, "live_sain_gate:" + liveGateReason, failOpen: true);
                reason = liveGateReason;
                return SearchTranslationDispatchResult.HandledFailOpen;
            }

            translationSuppressedTicks++;
            reason = "translation_suppressed_sain_steering_preserved";
            return SearchTranslationDispatchResult.Suppressed;
        }
        catch (Exception exception)
        {
            livePatchFailOpenReleases++;
            ReleaseLease(lease, now, "live_patch_exception:" + exception.GetType().Name, failOpen: true);
            reason = "exception:" + exception.GetType().Name;
            return SearchTranslationDispatchResult.HandledFailOpen;
        }
    }

    internal static bool HasActiveAdvanceGateForBot(string botProfileId)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return false;
        }
        lock (Sync)
        {
            return ActiveByGroup.Values.Any(item =>
                string.Equals(item.HeldBotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static bool? Bool(object? value)
    {
        if (value is bool typed)
        {
            return typed;
        }
        return bool.TryParse(value?.ToString(), out bool parsed) ? parsed : null;
    }

    private static string Text(object? value)
        => value?.ToString()?.Trim() ?? "none";


    private static bool IsBotPathDataManual(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return false;
        }
        string trimmed = typeName.Trim();
        int separator = Math.Max(trimmed.LastIndexOf('.'), trimmed.LastIndexOf('+'));
        string token = separator >= 0 && separator + 1 < trimmed.Length ? trimmed[(separator + 1)..] : trimmed;
        return string.Equals(token, "BotPathDataManual", StringComparison.Ordinal);
    }

    private static bool TryGetCoherentInputs(
        out RouteConvergenceSnapshot convergenceSnapshot,
        out ChokeDetectionSnapshot chokeSnapshot,
        out RouteIndependenceSnapshot routeSnapshot)
    {
        convergenceSnapshot = RouteConvergenceSnapshot.Empty;
        chokeSnapshot = ChokeDetectionSnapshot.Empty;
        routeSnapshot = RouteIndependenceSnapshot.Empty;
        if (!RouteConvergenceService.TryGetSnapshot(out var currentConvergence)
            || !ChokeDetectionService.TryGetSnapshot(out var currentChoke)
            || !RouteIndependenceService.TryGetSnapshot(out var currentRouteState))
        {
            return false;
        }

        if (currentConvergence.TopologyRevision != currentChoke.TopologyRevision
            || currentConvergence.TopologyRevision != currentRouteState.TopologyRevision
            || !string.Equals(currentConvergence.TopologyContentHash, currentChoke.TopologyContentHash, StringComparison.Ordinal)
            || !string.Equals(currentConvergence.TopologyContentHash, currentRouteState.TopologyContentHash, StringComparison.Ordinal)
            || currentConvergence.OperatorBatchAtUtc != currentChoke.OperatorBatchAtUtc
            || currentConvergence.OperatorBatchAtUtc != currentRouteState.OperatorBatchAtUtc
            || currentConvergence.PublishedAtUtc != currentChoke.ConvergencePublishedAtUtc
            || currentConvergence.PublishedAtUtc != currentRouteState.ConvergencePublishedAtUtc
            || currentChoke.PublishedAtUtc != currentRouteState.ChokePublishedAtUtc)
        {
            return false;
        }

        convergenceSnapshot = currentConvergence;
        chokeSnapshot = currentChoke;
        routeSnapshot = currentRouteState;
        return true;
    }

    private static bool InputIdentityMatches(
        ActiveAdvanceGateLease lease,
        RouteConvergenceSnapshot convergenceSnapshot,
        ChokeDetectionSnapshot chokeSnapshot,
        RouteIndependenceSnapshot routeSnapshot)
    {
        return string.Equals(lease.MapId, convergenceSnapshot.MapId, StringComparison.OrdinalIgnoreCase)
            && lease.TopologyRevision == convergenceSnapshot.TopologyRevision
            && string.Equals(lease.TopologyContentHash, convergenceSnapshot.TopologyContentHash, StringComparison.Ordinal)
            && lease.OperatorBatchAtUtc == convergenceSnapshot.OperatorBatchAtUtc
            && lease.ConvergencePublishedAtUtc == convergenceSnapshot.PublishedAtUtc
            && lease.ChokePublishedAtUtc == chokeSnapshot.PublishedAtUtc
            && lease.RoutePublishedAtUtc == routeSnapshot.PublishedAtUtc;
    }

    internal static int ForgetRuntimePresence(string botProfileId, string reason)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return 0;
        }

        int removed = 0;
        lock (Sync)
        {
            foreach (ActiveAdvanceGateLease lease in ActiveByGroup.Values
                .Where(item => string.Equals(item.HeldBotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.LeadBotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase))
                .ToArray())
            {
                if (ActiveByGroup.Remove(lease.GroupKey))
                {
                    removed++;
                }
            }

            // The disappearing incarnation must not keep its own admission memory. No survivor receives
            // a normal release cooldown: this is lifecycle cleanup, not a tactical completion/failure.
            CooldownUntilByHeldBot.Remove(botProfileId);
        }

        if (removed > 0)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ANTI_CONGA_RUNTIME_PRESENCE_FORGOTTEN bot={Safe(botProfileId)}; leasesRemoved={removed}; reason={Safe(reason)}; normalRelease=false; survivorCooldown=false; staleResume=false");
        }
        return removed;
    }

    internal static int ReleaseForHigherAuthority(string botProfileId, DateTimeOffset now, string reason)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return 0;
        }

        ActiveAdvanceGateLease[] matching;
        lock (Sync)
        {
            matching = ActiveByGroup.Values
                .Where(item => string.Equals(item.HeldBotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.LeadBotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        int released = 0;
        foreach (var lease in matching)
        {
            bool wasCurrent;
            lock (Sync)
            {
                wasCurrent = ActiveByGroup.TryGetValue(lease.GroupKey, out var currentLease)
                    && string.Equals(currentLease.LeaseId, lease.LeaseId, StringComparison.OrdinalIgnoreCase);
            }
            ReleaseLease(lease, now, "higher_authority:" + Safe(reason), failOpen: true);
            if (wasCurrent)
            {
                released++;
            }
        }

        if (released > 0)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ANTI_CONGA_HIGHER_AUTHORITY_RELEASE bot={Safe(botProfileId)}; leasesReleased={released}; reason={Safe(reason)}; staleResume=false; releaseOnly=true");
        }
        return released;
    }

    private static void ReleaseAll(DateTimeOffset now, string reason, bool failOpen)
    {
        ActiveAdvanceGateLease[] leases;
        lock (Sync)
        {
            leases = ActiveByGroup.Values.ToArray();
        }
        foreach (var lease in leases)
        {
            ReleaseLease(lease, now, reason, failOpen);
        }
    }

    private static void ReleaseLease(ActiveAdvanceGateLease lease, DateTimeOffset now, string reason, bool failOpen)
    {
        bool released;
        lock (Sync)
        {
            released = ActiveByGroup.TryGetValue(lease.GroupKey, out var currentLease)
                && string.Equals(currentLease.LeaseId, lease.LeaseId, StringComparison.OrdinalIgnoreCase);
            if (released)
            {
                ActiveByGroup.Remove(lease.GroupKey);
                CooldownUntilByHeldBot[lease.HeldBotProfileId] = now + ReacquireCooldown;
            }
        }
        if (!released)
        {
            return;
        }

        if (failOpen)
        {
            failOpenReleases++;
        }
        else
        {
            completions++;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_ANTI_CONGA_LEASE_RELEASED owner={Safe(lease.OwnerProfileId)}; threat={Safe(lease.ThreatId)}; choke={Safe(lease.ChokeId)}; chokeKind={Safe(lease.ChokeKind)}; chokeConfidence={Safe(lease.ChokeConfidence)}; heldBot={Safe(lease.HeldBotProfileId)}; leadBot={Safe(lease.LeadBotProfileId)}; lease={Safe(lease.LeaseId)}; gateGeneration={lease.GateGeneration}; schedulerWindow={Safe(lease.SchedulerWindowId)}; age={(now - lease.StartedAtUtc).TotalSeconds:0.00}; outcome={(failOpen ? "fail_open" : "completed")}; reason={Safe(reason)}; physicalMode=sain_search_translation_gate; staleResume=false; cooldown={ReacquireCooldown.TotalSeconds:0.00}");
    }

    private static bool IsCooldownActive(string botProfileId, DateTimeOffset now)
    {
        lock (Sync)
        {
            if (!CooldownUntilByHeldBot.TryGetValue(botProfileId, out var until))
            {
                return false;
            }
            if (until <= now)
            {
                CooldownUntilByHeldBot.Remove(botProfileId);
                return false;
            }
            return true;
        }
    }

    private static void Publish(RouteConvergenceSnapshot convergenceSnapshot, DateTimeOffset now)
    {
        AntiCongaLeaseObservation[] observations;
        lock (Sync)
        {
            observations = ActiveByGroup.Values
                .OrderBy(item => item.GroupKey, StringComparer.OrdinalIgnoreCase)
                .Select(item => new AntiCongaLeaseObservation(
                    item.LeaseId,
                    item.OwnerProfileId,
                    item.ThreatId,
                    item.ChokeId,
                    item.LeadBotProfileId,
                    item.HeldBotProfileId,
                    item.SchedulerWindowId,
                    item.GateGeneration,
                    item.GateAnchor,
                    item.StartedAtUtc,
                    item.ExpiresAtUtc,
                    "active_advance_gate"))
                .ToArray();
            current = new AntiCongaSnapshot(
                convergenceSnapshot.MapId,
                convergenceSnapshot.TopologyRevision,
                convergenceSnapshot.TopologyContentHash,
                convergenceSnapshot.OperatorBatchAtUtc,
                now,
                observations);
        }

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ANTI_CONGA_BOOT enabled=true; mode=anti_conga_advance_gate; firstInfluence=true; request={RequestKind}; publishedChokeRequired=true; inferredChokeAllowed=true; hardChokePreferred=true; oneGatePerOwnerThreatChoke=true; ttl={GateTtl.TotalSeconds:0.00}; acquisitionCadenceSeconds={AcquisitionInterval.TotalSeconds:0.00}; sharedThreatAxis=true; sharedCorridorBounds=true; maxOperatorCrossTrack={MaximumOperatorRouteCrossTrackMeters:0.00}; minHeadway=0.00; severeStackHeadway={SevereStackHeadwayMeters:0.00}; acquireHeadwayFloor={AcquireHeadwayMaximumFloorMeters:0.00}; acquireHeadwayCeiling={AcquireHeadwayMaximumCeilingMeters:0.00}; safeHeadwayRelease={SafeHeadwayReleaseMeters:0.00}; schedulerCombatSidecarRequired=true; sainWindow=Search_or_GroupSearch; searchMustBeMovingAtAdmission=true; translationOnly=true; sainPauseMutation=false; sainDestinationMutation=false; sainTargetMutation=false; sainSteeringPreserved=true; memberLocalFallback=true; searchTranslationPatchReady={Bool(SearchTranslationPatchReady)}; searchTranslationPatchState={Safe(SearchTranslationPatchState)}; routeAssignment=false; flank=false; staleResume=false; build={VanguardBuildVersion.BuildLabel}");
        }

        if (now >= nextSummaryAtUtc)
        {
            nextSummaryAtUtc = now + SummaryInterval;
            string blockCounts = FormatAdmissionCounts(AdmissionBlockCounts, 12);
            string blockDetails = FormatAdmissionCounts(AdmissionBlockDetails, 10);
            string heldCandidateRejectCounts = FormatAdmissionCounts(HeldCandidateRejectCounts, 10);
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ANTI_CONGA_SUMMARY map={Safe(convergenceSnapshot.MapId)}; topologyRevision={convergenceSnapshot.TopologyRevision}; topologyHash={ShortHash(convergenceSnapshot.TopologyContentHash)}; activeGates={observations.Length}; acquisitionPasses={acquisitionPassesTotal}; acquisitions={acquisitions}; completions={completions}; failOpenReleases={failOpenReleases}; livePatchFailOpenReleases={livePatchFailOpenReleases}; translationSuppressedTicks={translationSuppressedTicks}; admissionRejects={admissionRejects}; gateBlocks={admissionGateBlocks}; commandConflicts={commandConflicts}; funnelChokes={admissibleChokesEvaluated}; funnelConverged={convergedGroupsMatched}; funnelRouteWitness={routeWitnessesMatched}; funnelLivePair={livePairWindows}; heldCandidatesEvaluated={heldCandidatesEvaluated}; heldCandidatesLocallyAdmissible={heldCandidatesLocallyAdmissible}; heldCandidateRejects={heldCandidateRejects}; localFallbackSelections={localFallbackSelections}; funnelPairSelected={pairSelections}; funnelPairGeometryPassed={pairGeometryPassed}; funnelCooldownPassed={cooldownPassed}; funnelLocalGatePassed={localGatePassed}; funnelSearchPathPassed={searchPathPassed}; funnelSchedulerPassed={schedulerGatePassed}; funnelMovementConflictClear={movementConflictClear}; funnelGateIssued={gateIssued}; rejectByGate={blockCounts}; rejectDetails={blockDetails}; heldCandidateRejectByReason={heldCandidateRejectCounts}; searchTranslationPatchReady={Bool(SearchTranslationPatchReady)}; searchTranslationPatchState={Safe(SearchTranslationPatchState)}; movementInfluence=true; physicalMode=sain_search_translation_gate; sharedThreatAxis=true; translationOnly=true; sainSteeringPreserved=true; sainPauseMutation=false; routeAssignment=false; flank=false; ttlBounded=true; staleResume=false");
        }
    }

    private static void PublishEmptyIfNeeded(DateTimeOffset now)
    {
        lock (Sync)
        {
            current = AntiCongaSnapshot.Empty;
        }
    }

    private static void LogAdmissionRejectThrottled(string botProfileId, string chokeId, string reason, DateTimeOffset now)
    {
        // Admission rejects are summarized; only noteworthy scheduler/command conflicts are emitted
        // individually in Diagnostic to keep normal qualification logs bounded.
        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
            $"TC_ANTI_CONGA_ADMISSION_REJECTED botProfile={Safe(botProfileId)}; choke={Safe(chokeId)}; reason={Safe(reason)}; influence=false; failOpen=true; at={now:O}");
    }


    private static void RecordHeldCandidateReject(string reason)
    {
        string safeReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
        heldCandidateRejects++;
        lock (Sync)
        {
            IncrementCounter(HeldCandidateRejectCounts, Safe(safeReason), maxDistinct: 64);
        }
    }

    private static void RecordAdmissionBlock(string reason)
    {
        string safeReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
        string canonical = safeReason;
        int separator = safeReason.IndexOf(':');
        if (separator > 0)
        {
            canonical = safeReason[..separator];
        }

        admissionGateBlocks++;
        lock (Sync)
        {
            IncrementCounter(AdmissionBlockCounts, Safe(canonical), maxDistinct: 48);
            if (separator > 0)
            {
                IncrementCounter(AdmissionBlockDetails, Safe(safeReason), maxDistinct: 64);
            }
        }
    }

    private static void IncrementCounter(Dictionary<string, int> counters, string key, int maxDistinct)
    {
        if (counters.TryGetValue(key, out int existing))
        {
            counters[key] = existing + 1;
            return;
        }

        if (counters.Count < maxDistinct)
        {
            counters[key] = 1;
            return;
        }

        const string overflow = "other";
        counters[overflow] = counters.TryGetValue(overflow, out int overflowCount) ? overflowCount + 1 : 1;
    }

    private static string FormatAdmissionCounts(Dictionary<string, int> counters, int maxItems)
    {
        lock (Sync)
        {
            if (counters.Count == 0)
            {
                return "none";
            }

            return string.Join(",", counters
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, maxItems))
                .Select(item => Safe(item.Key) + "=" + item.Value.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static string BuildLeaseId(
        RouteConvergenceSnapshot convergenceSnapshot,
        ChokeObservation choke,
        string heldBotProfileId,
        DateTimeOffset now)
    {
        return "tc_anti_conga_" + convergenceSnapshot.TopologyRevision.ToString(CultureInfo.InvariantCulture)
            + "_" + StableToken(choke.ChokeId)
            + "_" + StableToken(heldBotProfileId)
            + "_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
    }

    private static string GroupKey(string ownerProfileId, string threatId, string chokeId)
        => Normalize(ownerProfileId) + "|" + NormalizeThreat(threatId) + "|" + Normalize(chokeId);

    private static float HorizontalDistance(Vector3 first, Vector3 second)
    {
        first.y = 0f;
        second.y = 0f;
        return Vector3.Distance(first, second);
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string NormalizeThreat(string? value) => Normalize(value);
    private static string StableToken(string? value)
    {
        string safe = Safe(value);
        return safe.Length <= 24 ? safe : safe[..24];
    }
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    private static string ShortHash(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Length <= 12 ? value : value[..12];

    private readonly struct RouteProjection
    {
        public RouteProjection(float alongMeters, float crossTrackMeters, Vector3 point, float totalLengthMeters)
        {
            AlongMeters = alongMeters;
            CrossTrackMeters = crossTrackMeters;
            Point = point;
            TotalLengthMeters = totalLengthMeters;
        }

        public float AlongMeters { get; }
        public float CrossTrackMeters { get; }
        public Vector3 Point { get; }
        public float TotalLengthMeters { get; }
    }

    private readonly struct RouteProgress
    {
        public RouteProgress(
            DirectRouteObservation route,
            OperatorDecisionSnapshot snapshot,
            Vector3 livePosition,
            float operatorAlongMeters,
            float operatorCrossTrackMeters,
            float routeLengthMeters,
            float distanceFromThreatMeters)
        {
            Route = route;
            Snapshot = snapshot;
            LivePosition = livePosition;
            OperatorAlongMeters = operatorAlongMeters;
            OperatorCrossTrackMeters = operatorCrossTrackMeters;
            RouteLengthMeters = routeLengthMeters;
            DistanceFromThreatMeters = distanceFromThreatMeters;
        }

        public DirectRouteObservation Route { get; }
        public OperatorDecisionSnapshot Snapshot { get; }
        public Vector3 LivePosition { get; }
        public float OperatorAlongMeters { get; }
        public float OperatorCrossTrackMeters { get; }
        public float RouteLengthMeters { get; }
        public float DistanceFromThreatMeters { get; }
    }

    private readonly struct HeldAdmission
    {
        public HeldAdmission(string schedulerWindowId, string pathSummary)
        {
            SchedulerWindowId = schedulerWindowId;
            PathSummary = pathSummary;
        }

        public string SchedulerWindowId { get; }
        public string PathSummary { get; }
    }

    private readonly struct RoutePairCandidate
    {
        public RoutePairCandidate(RouteProgress lead, RouteProgress held, float headwayMeters)
        {
            Lead = lead;
            Held = held;
            HeadwayMeters = headwayMeters;
        }

        public RouteProgress Lead { get; }
        public RouteProgress Held { get; }
        public float HeadwayMeters { get; }
    }

    private sealed class ActiveAdvanceGateLease
    {
        public string LeaseId = "none";
        public string GroupKey = "none";
        public string OwnerProfileId = "none";
        public string ThreatId = "none";
        public string ChokeId = "none";
        public string ChokeKind = "none";
        public string ChokeConfidence = "none";
        public string LeadBotProfileId = "none";
        public string HeldBotProfileId = "none";
        public string SchedulerWindowId = "none";
        public long GateGeneration;
        public Vector3 GateAnchor;
        public DateTimeOffset StartedAtUtc;
        public DateTimeOffset ExpiresAtUtc;
        public string MapId = "none";
        public long TopologyRevision;
        public string TopologyContentHash = "none";
        public DateTimeOffset OperatorBatchAtUtc;
        public DateTimeOffset ConvergencePublishedAtUtc;
        public DateTimeOffset ChokePublishedAtUtc;
        public DateTimeOffset RoutePublishedAtUtc;
    }
}

/// <summary>
/// Exact SAIN translation interception shared by AltDevelop, anti-conga and choke support through the TC Search/GroupSearch gate bridge. Stationary gates skip the original TickPath only after live revalidation and preserve SAIN steering callbacks; AltDevelop instead binds its exact fresh destination/path into the native BotPathDataManual and lets TickPath execute normally. No SAIN Pause/UnPause state, target or decision is mutated.
/// </summary>
internal sealed class SainSearchTranslationGatePatch : ModulePatch
{
    private const string TargetTypeName = "SAIN.SAINComponent.Classes.Mover.BotPathDataManual";
    private const string TargetMethodName = "TickPath";

    protected override MethodBase GetTargetMethod()
    {
        Type targetType = AccessTools.TypeByName(TargetTypeName)
            ?? throw new InvalidOperationException(TargetTypeName + " not found for TC Search Translation Gate.");
        MethodInfo method = AccessTools.Method(targetType, TargetMethodName, new[] { typeof(float), typeof(float) })
            ?? throw new InvalidOperationException(TargetTypeName + "." + TargetMethodName + "(float,float) not found for TC Search Translation Gate.");
        VanguardClientDiagnosticsLog.Info(SearchTranslationGateBridge.StatusTag,
            $"TC_SEARCH_TRANSLATION_GATE_BIND_OK type={targetType.FullName}; method={method.Name}; parameters=2; patch=prefix; originalSkippedOnlyWhenGateValid=true; sainPauseMutation=false; altDevelopDestinationBinding=true; stationaryGateDestinationMutation=false; targetMutation=false; steeringPreserved=true; failOpen=true");
        return method;
    }

    [PatchPrefix]
    private static bool PatchPrefix(object __instance)
    {
        try
        {
            return !SearchTranslationGateBridge.TrySuppressAny(
                __instance,
                DateTimeOffset.UtcNow,
                out _);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                SearchTranslationGateBridge.StatusTag,
                $"TC_SEARCH_TRANSLATION_GATE_PATCH_EXCEPTION reason={exception.GetType().Name}:{exception.Message}; failOpen=true");
            return true;
        }
    }
}
#endif
