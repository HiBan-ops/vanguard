#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Implements split-flank readiness as shadow-only observation of alternate-establishment assignment lifecycle and spatial dependency.
// Flow: A selected planning pair is sampled periodically, planning viability is revalidated independently from execution authority, live position is compared with the establishment region, and Direct safety/readiness is observed in parallel.
// Authority boundary: split-flank has zero movement/intent/lease/hold/commit authority and does not command aim, fire, suppression, route execution or synchronization.
// Invariant: Temporary execution vetoes do not erase a valid route plan. Stable presence in the establishment region is position evidence only; full AltEstablished still requires tactical pressure and synchronized readiness.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class SplitFlankReadinessService
{
    public const string StatusTag = "VANGUARD_SPLIT_FLANK_READINESS_STATUS";

    private static readonly TimeSpan OptionalMaxDeferral = TimeSpan.FromSeconds(1.5d);
    private static readonly TimeSpan ObservationSampleInterval = TimeSpan.FromMilliseconds(250d);
    private static readonly TimeSpan EstablishmentStableDwell = TimeSpan.FromMilliseconds(500d);
    private static readonly TimeSpan SpatialHistoryRetention = TimeSpan.FromSeconds(8d);
    private static readonly TimeSpan SummaryLogInterval = TimeSpan.FromSeconds(15.0d);
    private static readonly TimeSpan UpstreamSnapshotGraceDuration = RouteAssignmentService.PhysicalConsumerContinuityGraceDuration;
    private const int EstablishmentStableSampleCount = 3;
    private const float MinimumEstablishmentRadiusMeters = 0.5f;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, EstablishmentTracker> EstablishmentByAssignment = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, AssignmentTracker> AssignmentByThreatGroup = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> LastDetailSignatureByThreatGroup = new(StringComparer.Ordinal);

    private static SplitFlankReadinessSnapshot current = SplitFlankReadinessSnapshot.Empty;
    private static DateTimeOffset nextObservationAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset upstreamGapStartedAtUtc = DateTimeOffset.MinValue;
    private static string lastAssignmentMapId = string.Empty;
    private static long lastAssignmentTopologyRevision;
    private static string lastAssignmentTopologyContentHash = string.Empty;
    private static bool upstreamGapHistoryExpired;
    private static bool bootLogged;
    private static int inputAssignmentsTotal;
    private static int dependencyEnvelopeReadyTotal;
    private static int alternateVetoTotal;
    private static int directUnavailableTotal;
    private static int degradedDirectTotal;
    private static int replacementSelectionPressureTotal;
    private static int insideRegionTotal;
    private static int positionEstablishedTotal;
    private static int positionDependencyEnvelopeObservedTotal;
    private static int assignmentChangeTotal;
    private static int establishmentTransitionTotal;
    private static int upstreamGapRetainedTicksTotal;
    private static int upstreamGapRestoredTotal;
    private static int upstreamGapExpiredTotal;
    private static int upstreamGapTopologyBreakTotal;

    public static void Tick(DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            ResetInternal();
            return;
        }

        if (!RouteAssignmentService.TryGetSnapshot(out var assignmentSnapshot))
        {
            if (now >= nextObservationAtUtc)
            {
                nextObservationAtUtc = now + ObservationSampleInterval;
                ObserveUpstreamSnapshotGap(now);
            }
            return;
        }

        if (now < nextObservationAtUtc)
        {
            return;
        }

        ResolveUpstreamSnapshotGapOnRestore(assignmentSnapshot, now);
        RememberAssignmentTopology(assignmentSnapshot);

        if (!VanguardRuntimeFrameBudgetGuard.ShouldRunOptional("SplitFlankReadiness", now, OptionalMaxDeferral, out string optionalBudgetReason))
        {
            return;
        }
        nextObservationAtUtc = now + ObservationSampleInterval;

        PruneHistory(now);

        var observations = new List<ThreatGroupReadinessObservation>();
        int inputAssignments = 0;
        int alternateReady = 0;
        int alternateVetoes = 0;
        int directElementReady = 0;
        int directUnavailable = 0;
        int degradedDirect = 0;
        int dependencyEnvelopeReady = 0;
        int replacementSelectionPressure = 0;
        int insideRegion = 0;
        int positionEstablished = 0;
        int positionDependencyEnvelopeObserved = 0;
        int assignmentChanges = 0;
        int establishmentTransitions = 0;

        foreach (var assignment in assignmentSnapshot.ThreatGroups)
        {
            if (!assignment.HasShadowAltSelection)
            {
                continue;
            }

            inputAssignments++;
            string threatGroupKey = ThreatGroupKey(assignment.OwnerProfileId, assignment.ThreatId);
            string assignmentKey = AssignmentKey(assignment);
            bool assignmentChanged = ObserveAssignment(threatGroupKey, assignmentKey, now, out float assignmentAgeSeconds);
            if (assignmentChanged)
            {
                assignmentChanges++;
            }

            bool altPlanningViable = false;
            string altPlanningReason;
            bool altPositionKnown = VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(
                    assignment.SelectedAltBotProfileId,
                    out var altRuntime)
                && altRuntime.BotOwner != null
                && !altRuntime.BotOwner.IsDead;
            Vector3 altPosition = altPositionKnown ? altRuntime.BotOwner!.Position : Vector3.zero;
            if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(assignment.SelectedAltBotProfileId, out var altDecision))
            {
                altPlanningReason = "decision_snapshot_missing";
            }
            else
            {
                if (!RouteAssignmentService.TryResolveMemberEvidenceCapturedAtUtc(
                        assignment,
                        assignment.SelectedAltBotProfileId,
                        out var altEvidenceCapturedAtUtc))
                {
                    altPlanningReason = "member_evidence_generation_missing";
                }
                else
                {
                    altPlanningReason = RouteAssignmentService.ValidatePlanningCandidate(
                        altDecision,
                        assignment.OwnerProfileId,
                        altEvidenceCapturedAtUtc);
                }
                altPlanningViable = altPlanningReason.Length == 0;
                if (altPlanningViable)
                {
                    altPlanningReason = "ok";
                    alternateReady++;
                }
            }

            if (!altPlanningViable)
            {
                alternateVetoes++;
            }

            bool selectedPathProven = assignment.SelectedPairPathProven;
            bool altDevelopEligible = selectedPathProven && altPlanningViable;
            float regionRadius = Math.Max(MinimumEstablishmentRadiusMeters, assignment.AlternateEstablishmentRegionRadiusMeters);
            float distance3D = altPositionKnown
                ? Vector3.Distance(altPosition, assignment.AlternateEstablishmentRegionCenter)
                : float.PositiveInfinity;
            float horizontalDistance = altPositionKnown
                ? HorizontalDistance(altPosition, assignment.AlternateEstablishmentRegionCenter)
                : float.PositiveInfinity;
            float verticalDelta = altPositionKnown
                ? Math.Abs(altPosition.y - assignment.AlternateEstablishmentRegionCenter.y)
                : float.PositiveInfinity;
            bool inside = altPositionKnown && distance3D <= regionRadius;
            if (inside)
            {
                insideRegion++;
            }

            ObserveEstablishment(
                assignmentKey,
                inside,
                now,
                out int stableSamples,
                out float dwellSeconds,
                out bool positionEstablishedObserved,
                out bool establishmentTransition);
            if (positionEstablishedObserved)
            {
                positionEstablished++;
            }
            if (establishmentTransition)
            {
                establishmentTransitions++;
            }

            int directReady = 0;
            int directVetoed = 0;
            int directUnknown = 0;
            var directReasons = new List<string>();
            foreach (string botProfileId in assignment.DirectBotProfileIds)
            {
                if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(botProfileId, out var directDecision))
                {
                    directUnknown++;
                    directReasons.Add("decision_snapshot_missing");
                    continue;
                }

                string directReason;
                if (!RouteAssignmentService.TryResolveMemberEvidenceCapturedAtUtc(
                        assignment,
                        botProfileId,
                        out var directEvidenceCapturedAtUtc))
                {
                    directReason = "member_evidence_generation_missing";
                }
                else
                {
                    directReason = SplitFlankSafetyPolicy.Evaluate(
                        directDecision,
                        assignment.OwnerProfileId,
                        assignment.ThreatId,
                        directEvidenceCapturedAtUtc,
                        now,
                        allowExactOwnedDirectCommitPrimary: true);
                }
                if (directReason.Length == 0)
                {
                    directReady++;
                }
                else
                {
                    directVetoed++;
                    directReasons.Add(directReason);
                }
            }

            bool directReadyNow = directReady > 0;
            if (directReadyNow)
            {
                directElementReady++;
            }
            else
            {
                directUnavailable++;
            }

            bool degraded = directReadyNow && (directVetoed > 0 || directUnknown > 0);
            if (degraded)
            {
                degradedDirect++;
            }

            bool envelopeReady = altDevelopEligible && directReadyNow;
            if (envelopeReady)
            {
                dependencyEnvelopeReady++;
            }

            // Spatial dependency observation only. It is explicitly not DirectCommit permission.
            bool positionDependencyEnvelope = positionEstablishedObserved && directReadyNow;
            if (positionDependencyEnvelope)
            {
                positionDependencyEnvelopeObserved++;
            }

            bool replacementPressure = assignment.PotentialBetterRolePathProvenCount > 0;
            if (replacementPressure)
            {
                replacementSelectionPressure++;
            }

            string classification;
            string reason;
            if (positionDependencyEnvelope)
            {
                classification = "shadow_alt_position_established_direct_position_envelope_observed";
                reason = degraded
                    ? "stable_alt_position_in_establishment_region_with_degraded_direct_element"
                    : "stable_alt_position_in_establishment_region_and_direct_member_ready";
            }
            else if (positionEstablishedObserved)
            {
                classification = "shadow_alt_position_established_direct_unavailable";
                reason = "spatial_establishment_observed_but_no_direct_member_currently_ready";
            }
            else if (altDevelopEligible && inside)
            {
                classification = "shadow_alt_inside_region_stabilizing";
                reason = "inside_establishment_region_waiting_for_stable_observation";
            }
            else if (altDevelopEligible)
            {
                classification = "shadow_alt_develop_eligible";
                reason = "selected_pair_current_and_path_proven_but_not_position_established";
            }
            else if (!altPlanningViable && !directReadyNow)
            {
                classification = "shadow_alt_and_direct_unavailable";
                reason = "planning_viability_or_direct_safety_blocks_both_elements";
            }
            else if (!altPlanningViable)
            {
                classification = "shadow_alt_unavailable";
                reason = altPlanningReason;
            }
            else if (!selectedPathProven)
            {
                classification = "shadow_alt_path_unproven";
                reason = "selected_pair_path_no_longer_proven";
            }
            else
            {
                classification = "shadow_direct_element_unavailable";
                reason = directReasons.Count == 0 ? "no_direct_member_ready" : directReasons[0];
            }

            string[] distinctReasons = directReasons
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var observation = new ThreatGroupReadinessObservation(
                assignment.OwnerProfileId,
                assignment.ThreatId,
                assignment.AlternateCandidateId,
                assignment.SelectedAltBotProfileId,
                assignment.AlternateEstablishmentRegionCenter,
                regionRadius,
                altPositionKnown,
                altPosition,
                distance3D,
                horizontalDistance,
                verticalDelta,
                altPlanningViable,
                altPlanningReason,
                selectedPathProven,
                inside,
                stableSamples,
                dwellSeconds,
                positionEstablishedObserved,
                assignment.DirectBotProfileIds.Count,
                directReady,
                directVetoed,
                directUnknown,
                distinctReasons,
                envelopeReady,
                positionDependencyEnvelope,
                degraded,
                replacementPressure,
                assignmentChanged,
                assignmentAgeSeconds,
                classification,
                reason);
            observations.Add(observation);
            EmitDetailIfChanged(observation, assignment, threatGroupKey);
        }

        var next = new SplitFlankReadinessSnapshot(
            assignmentSnapshot.MapId,
            assignmentSnapshot.TopologyRevision,
            assignmentSnapshot.TopologyContentHash,
            assignmentSnapshot.OperatorBatchAtUtc,
            assignmentSnapshot.PublishedAtUtc,
            now,
            observations.ToArray());

        lock (Sync)
        {
            current = next;
        }

        try
        {
            SplitFlankStateService.Observe(next, assignmentSnapshot, now);
        }
        catch (Exception ex)
        {
            VanguardClientDiagnosticsLog.Warning(StatusTag,
                $"TC_SPLIT_FLANK_STATE_OBSERVE_FAIL_OPEN type={Safe(ex.GetType().Name)}; message={Safe(ex.Message)}; action=reset_shadow_only");
            SplitFlankStateService.Reset("split_flank_readiness_observe_exception_fail_open");
        }

        inputAssignmentsTotal += inputAssignments;
        dependencyEnvelopeReadyTotal += dependencyEnvelopeReady;
        alternateVetoTotal += alternateVetoes;
        directUnavailableTotal += directUnavailable;
        degradedDirectTotal += degradedDirect;
        replacementSelectionPressureTotal += replacementSelectionPressure;
        insideRegionTotal += insideRegion;
        positionEstablishedTotal += positionEstablished;
        positionDependencyEnvelopeObservedTotal += positionDependencyEnvelopeObserved;
        assignmentChangeTotal += assignmentChanges;
        establishmentTransitionTotal += establishmentTransitions;

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_SPLIT_FLANK_READINESS_BOOT enabled=true; mode=alt_establishment_dependency_shadow; input=alt_establishment_member_assignment; sampleMs={ObservationSampleInterval.TotalMilliseconds:0}; stableDwellMs={EstablishmentStableDwell.TotalMilliseconds:0}; stableSamples={EstablishmentStableSampleCount}; spatialMetric=full_3d_region_distance; upstreamGapGraceSeconds={UpstreamSnapshotGraceDuration.TotalSeconds:0.0}; upstreamGapCurrentPublicationFailClosed=true; upstreamGapSpatialHistoryBounded=true; upstreamGapStateHistoryBounded=true; leadAssignmentIdentity=owner_threat_axis_lead; optionalFlankSupportIdentity=pressure_state_only; assignmentLifecycleObservation=true; positionEstablishmentObservation=true; pressureProof=false; fullAltEstablished=false; synchronizedCommitReady=false; executionAllowed=false; movementAuthority=false; holdAuthority=false; commitAuthority=false; build={VanguardBuildVersion.BuildLabel}");
        }

        if (now >= nextSummaryAtUtc)
        {
            nextSummaryAtUtc = now + SummaryLogInterval;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_SPLIT_FLANK_READINESS_SUMMARY map={Safe(assignmentSnapshot.MapId)}; topologyRevision={assignmentSnapshot.TopologyRevision}; topologyHash={ShortHash(assignmentSnapshot.TopologyContentHash)}; inputAssignments={inputAssignments}; publishedGroups={observations.Count}; alternateReady={alternateReady}; alternateVetoes={alternateVetoes}; directElementReady={directElementReady}; directUnavailable={directUnavailable}; degradedDirect={degradedDirect}; dependencyEnvelopeReady={dependencyEnvelopeReady}; insideEstablishmentRegion={insideRegion}; positionEstablishedObserved={positionEstablished}; positionDependencyEnvelopeObserved={positionDependencyEnvelopeObserved}; assignmentChanges={assignmentChanges}; establishmentTransitions={establishmentTransitions}; replacementSelectionPressure={replacementSelectionPressure}; inputAssignmentsTotal={inputAssignmentsTotal}; dependencyEnvelopeReadyTotal={dependencyEnvelopeReadyTotal}; alternateVetoTotal={alternateVetoTotal}; directUnavailableTotal={directUnavailableTotal}; degradedDirectTotal={degradedDirectTotal}; insideRegionTotal={insideRegionTotal}; positionEstablishedTotal={positionEstablishedTotal}; positionDependencyEnvelopeObservedTotal={positionDependencyEnvelopeObservedTotal}; assignmentChangeTotal={assignmentChangeTotal}; establishmentTransitionTotal={establishmentTransitionTotal}; replacementSelectionPressureTotal={replacementSelectionPressureTotal}; upstreamGapRetainedTicksTotal={upstreamGapRetainedTicksTotal}; upstreamGapRestoredTotal={upstreamGapRestoredTotal}; upstreamGapExpiredTotal={upstreamGapExpiredTotal}; upstreamGapTopologyBreakTotal={upstreamGapTopologyBreakTotal}; optionalBudget={Safe(optionalBudgetReason)}; dependencyModel=AltDevelopEligible_then_PositionEstablishedObserved_then_PositionDependencyEnvelopeObserved; altDevelopmentCommanded=false; pressureProof=false; fullAltEstablished=false; synchronizedCommitReady=false; planAuthority=false; shadowOnly=true; executionAllowed=false; movementMutation=false");
        }
    }

    public static bool TryGetSnapshot(out SplitFlankReadinessSnapshot snapshot)
    {
        lock (Sync)
        {
            snapshot = current;
            return snapshot.IsAvailable;
        }
    }

    // AltDevelop completion consumes the existing stable spatial truth instead of
    // inventing a second dwell timer inside the movement executor. The snapshot must be at
    // least as new as the RouteAssignment publication being completed and must match the exact
    // current owner/threat/candidate/member tuple. This remains observation-only.
    internal static bool TryGetCurrentEstablishedObservation(
        ThreatGroupRouteAssignment assignment,
        DateTimeOffset minimumAssignmentPublishedAtUtc,
        out ThreatGroupReadinessObservation observation,
        out string reason)
    {
        observation = null!;
        if (assignment == null)
        {
            reason = "assignment_missing";
            return false;
        }

        lock (Sync)
        {
            if (!current.IsAvailable)
            {
                reason = "readiness_snapshot_unavailable";
                return false;
            }
            if (current.AssignmentPublishedAtUtc < minimumAssignmentPublishedAtUtc)
            {
                reason = "readiness_snapshot_older_than_current_assignment";
                return false;
            }

            ThreatGroupReadinessObservation? matched = current.ThreatGroups.FirstOrDefault(item =>
                string.Equals(item.OwnerProfileId, assignment.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ThreatId, assignment.ThreatId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.AlternateCandidateId, assignment.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.AlternateBotProfileId, assignment.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase));
            if (matched == null)
            {
                reason = "current_assignment_readiness_observation_missing";
                return false;
            }

            observation = matched;
            if (!matched.AlternatePositionEstablishedObserved)
            {
                reason = matched.AlternateInsideEstablishmentRegion
                    ? "inside_region_waiting_stable_establishment"
                    : "alternate_not_inside_current_establishment_region";
                return false;
            }

            reason = "stable_position_established";
            return true;
        }
    }

    public static void Reset(string reason)
    {
        ResetInternal();
        if (!string.IsNullOrWhiteSpace(reason))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_SPLIT_FLANK_READINESS_RESET reason={Safe(reason)}; shadowOnly=true; positionEstablishmentObservation=true; fullAltEstablished=false; executionAllowed=false; synchronizationApplied=false; staleResume=false");
        }
    }

    private static bool ObserveAssignment(
        string threatGroupKey,
        string assignmentKey,
        DateTimeOffset now,
        out float assignmentAgeSeconds)
    {
        bool changed = false;
        if (!AssignmentByThreatGroup.TryGetValue(threatGroupKey, out var tracker))
        {
            tracker = new AssignmentTracker(assignmentKey, now, now, 0);
            AssignmentByThreatGroup[threatGroupKey] = tracker;
        }
        else if (!string.Equals(tracker.AssignmentKey, assignmentKey, StringComparison.Ordinal))
        {
            changed = true;
            tracker = new AssignmentTracker(assignmentKey, now, now, tracker.ChangeCount + 1);
            AssignmentByThreatGroup[threatGroupKey] = tracker;
        }
        else
        {
            tracker.LastSeenAtUtc = now;
        }

        assignmentAgeSeconds = (float)Math.Max(0d, (now - tracker.FirstSeenAtUtc).TotalSeconds);
        return changed;
    }

    private static void ObserveEstablishment(
        string assignmentKey,
        bool insideRegion,
        DateTimeOffset now,
        out int stableSamples,
        out float dwellSeconds,
        out bool established,
        out bool transition)
    {
        if (!EstablishmentByAssignment.TryGetValue(assignmentKey, out var tracker))
        {
            tracker = new EstablishmentTracker();
            EstablishmentByAssignment[assignmentKey] = tracker;
        }

        bool wasEstablished = tracker.Established;
        if (insideRegion)
        {
            bool contiguous = tracker.Inside
                && tracker.LastObservedAtUtc != DateTimeOffset.MinValue
                && now - tracker.LastObservedAtUtc <= ObservationSampleInterval + ObservationSampleInterval;
            if (!contiguous)
            {
                tracker.EnteredAtUtc = now;
                tracker.StableSamples = 1;
            }
            else
            {
                tracker.StableSamples++;
            }
            tracker.Inside = true;
            tracker.LastObservedAtUtc = now;
        }
        else
        {
            tracker.Inside = false;
            tracker.EnteredAtUtc = DateTimeOffset.MinValue;
            tracker.StableSamples = 0;
            tracker.LastObservedAtUtc = now;
        }

        TimeSpan dwell = tracker.Inside && tracker.EnteredAtUtc != DateTimeOffset.MinValue
            ? now - tracker.EnteredAtUtc
            : TimeSpan.Zero;
        tracker.Established = tracker.Inside
            && tracker.StableSamples >= EstablishmentStableSampleCount
            && dwell >= EstablishmentStableDwell;

        stableSamples = tracker.StableSamples;
        dwellSeconds = (float)Math.Max(0d, dwell.TotalSeconds);
        established = tracker.Established;
        transition = tracker.Established != wasEstablished;
    }

    private static void EmitDetailIfChanged(
        ThreatGroupReadinessObservation observation,
        ThreatGroupRouteAssignment assignment,
        string threatGroupKey)
    {
        string signature = string.Join("|",
            observation.AlternateCandidateId,
            observation.AlternateBotProfileId,
            observation.AlternatePlanningViable.ToString(),
            observation.SelectedPairPathProven.ToString(),
            observation.AlternateInsideEstablishmentRegion.ToString(),
            observation.AlternatePositionEstablishedObserved.ToString(),
            observation.DirectReadyCount.ToString(),
            observation.DirectVetoedCount.ToString(),
            observation.DirectUnknownCount.ToString(),
            observation.DependencyEnvelopeReady.ToString(),
            observation.PositionDependencyEnvelopeObserved.ToString(),
            observation.Classification,
            observation.ReplacementSelectionPressureObserved.ToString());
        if (LastDetailSignatureByThreatGroup.TryGetValue(threatGroupKey, out string lastSignature)
            && string.Equals(signature, lastSignature, StringComparison.Ordinal))
        {
            return;
        }
        LastDetailSignatureByThreatGroup[threatGroupKey] = signature;

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_SPLIT_FLANK_READINESS_ESTABLISHMENT owner={Safe(observation.OwnerProfileId)}; threat={Safe(observation.ThreatId)}; axis={Safe(observation.AlternateCandidateId)}; altBot={Safe(observation.AlternateBotProfileId)}; assignmentChanged={observation.AssignmentChanged}; assignmentAge={Number(observation.AssignmentAgeSeconds)}; altPlanningViable={observation.AlternatePlanningViable}; altPlanningReason={Safe(observation.AlternatePlanningReason)}; selectedPathProven={observation.SelectedPairPathProven}; establishmentCenter={VectorText(observation.AlternateEstablishmentRegionCenter)}; establishmentRadius={Number(observation.AlternateEstablishmentRegionRadiusMeters)}; altPositionKnown={observation.AlternatePositionKnown}; positionEvidence=live_botowner; altPosition={VectorText(observation.AlternatePosition)}; distance3d={FiniteNumber(observation.AlternateDistanceToRegionCenterMeters)}; horizontalDistance={FiniteNumber(observation.AlternateHorizontalDistanceMeters)}; verticalDelta={FiniteNumber(observation.AlternateVerticalDeltaMeters)}; insideRegion={observation.AlternateInsideEstablishmentRegion}; stableSamples={observation.EstablishmentStableSamples}; dwellSeconds={Number(observation.EstablishmentDwellSeconds)}; positionEstablishedObserved={observation.AlternatePositionEstablishedObserved}; directMembers={observation.DirectMemberCount}; directReady={observation.DirectReadyCount}; directVetoed={observation.DirectVetoedCount}; directUnknown={observation.DirectUnknownCount}; directReasons={Safe(string.Join(",", observation.DirectBlockingReasons))}; dependencyEnvelopeReady={observation.DependencyEnvelopeReady}; positionDependencyEnvelopeObserved={observation.PositionDependencyEnvelopeObserved}; directElementDegraded={observation.DirectElementDegraded}; replacementSelectionPressure={observation.ReplacementSelectionPressureObserved}; betterRolePathProven={assignment.PotentialBetterRolePathProvenCount}; classification={Safe(observation.Classification)}; reason={Safe(observation.Reason)}; altDevelopEligible={observation.AltDevelopEligible}; altDevelopmentCommanded=false; pressureProof=false; fullAltEstablished=false; synchronizedCommitReady=false; shadowOnly=true; executionAllowed=false; movementMutation=false");
    }

    private static void PruneHistory(DateTimeOffset now)
    {
        string[] staleAssignments = EstablishmentByAssignment
            .Where(pair => pair.Value.LastObservedAtUtc != DateTimeOffset.MinValue
                && now - pair.Value.LastObservedAtUtc > SpatialHistoryRetention)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (string key in staleAssignments)
        {
            EstablishmentByAssignment.Remove(key);
        }

        string[] staleThreatGroups = AssignmentByThreatGroup
            .Where(pair => now - pair.Value.LastSeenAtUtc > SpatialHistoryRetention)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (string key in staleThreatGroups)
        {
            AssignmentByThreatGroup.Remove(key);
            LastDetailSignatureByThreatGroup.Remove(key);
        }
    }

    private static void ObserveUpstreamSnapshotGap(DateTimeOffset now)
    {
        lock (Sync)
        {
            current = SplitFlankReadinessSnapshot.Empty;
        }

        if (upstreamGapStartedAtUtc == DateTimeOffset.MinValue)
        {
            upstreamGapStartedAtUtc = now;
            upstreamGapHistoryExpired = false;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_SPLIT_FLANK_READINESS_UPSTREAM_GAP_STARTED map={Safe(lastAssignmentMapId)}; topologyRevision={lastAssignmentTopologyRevision}; topologyHash={ShortHash(lastAssignmentTopologyContentHash)}; graceSeconds={UpstreamSnapshotGraceDuration.TotalSeconds:0.0}; currentPublication=false; spatialHistoryRetained=true; stateHistoryRetained=true; executionAllowed=false");
        }

        TimeSpan gap = now - upstreamGapStartedAtUtc;
        bool retainHistory = !upstreamGapHistoryExpired && gap <= UpstreamSnapshotGraceDuration;
        if (retainHistory)
        {
            upstreamGapRetainedTicksTotal++;
        }
        else if (!upstreamGapHistoryExpired)
        {
            upstreamGapHistoryExpired = true;
            upstreamGapExpiredTotal++;
            ClearSpatialHistory();
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_SPLIT_FLANK_READINESS_UPSTREAM_GAP_EXPIRED gapSeconds={Math.Max(0d, gap.TotalSeconds):0.00}; graceSeconds={UpstreamSnapshotGraceDuration.TotalSeconds:0.0}; currentPublication=false; spatialHistoryRetained=false; stateHistoryRetained=false; executionAllowed=false");
        }

        SplitFlankStateService.InvalidateUpstream(
            now,
            retainHistory ? "split_flank_readiness_input_gap_grace" : "split_flank_readiness_input_gap_expired",
            preserveTrackerHistory: retainHistory);
    }

    private static void ResolveUpstreamSnapshotGapOnRestore(RouteAssignmentSnapshot assignmentSnapshot, DateTimeOffset now)
    {
        if (upstreamGapStartedAtUtc == DateTimeOffset.MinValue)
        {
            return;
        }

        TimeSpan gap = now - upstreamGapStartedAtUtc;
        bool sameTopology = string.Equals(lastAssignmentMapId, assignmentSnapshot.MapId, StringComparison.OrdinalIgnoreCase)
            && lastAssignmentTopologyRevision == assignmentSnapshot.TopologyRevision
            && string.Equals(lastAssignmentTopologyContentHash, assignmentSnapshot.TopologyContentHash, StringComparison.Ordinal);
        bool retainHistory = !upstreamGapHistoryExpired
            && gap <= UpstreamSnapshotGraceDuration
            && sameTopology;

        if (retainHistory)
        {
            upstreamGapRestoredTotal++;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_SPLIT_FLANK_READINESS_UPSTREAM_GAP_RESTORED gapSeconds={Math.Max(0d, gap.TotalSeconds):0.00}; graceSeconds={UpstreamSnapshotGraceDuration.TotalSeconds:0.0}; map={Safe(assignmentSnapshot.MapId)}; topologyRevision={assignmentSnapshot.TopologyRevision}; topologyHash={ShortHash(assignmentSnapshot.TopologyContentHash)}; currentPublication=true; spatialHistoryRetained=true; stateHistoryRetained=true; staleReadinessPublished=false");
        }
        else
        {
            if (!sameTopology)
            {
                upstreamGapTopologyBreakTotal++;
            }
            if (!upstreamGapHistoryExpired)
            {
                upstreamGapExpiredTotal++;
                ClearSpatialHistory();
                SplitFlankStateService.InvalidateUpstream(
                    now,
                    sameTopology ? "split_flank_readiness_gap_restore_after_grace" : "split_flank_readiness_gap_restore_topology_changed",
                    preserveTrackerHistory: false);
            }
        }

        upstreamGapStartedAtUtc = DateTimeOffset.MinValue;
        upstreamGapHistoryExpired = false;
    }

    private static void RememberAssignmentTopology(RouteAssignmentSnapshot assignmentSnapshot)
    {
        lastAssignmentMapId = assignmentSnapshot.MapId ?? string.Empty;
        lastAssignmentTopologyRevision = assignmentSnapshot.TopologyRevision;
        lastAssignmentTopologyContentHash = assignmentSnapshot.TopologyContentHash ?? string.Empty;
    }

    private static void ClearSpatialHistory()
    {
        EstablishmentByAssignment.Clear();
        AssignmentByThreatGroup.Clear();
        LastDetailSignatureByThreatGroup.Clear();
    }

    private static void ResetInternal()
    {
        lock (Sync)
        {
            current = SplitFlankReadinessSnapshot.Empty;
        }
        EstablishmentByAssignment.Clear();
        AssignmentByThreatGroup.Clear();
        LastDetailSignatureByThreatGroup.Clear();
        nextObservationAtUtc = DateTimeOffset.MinValue;
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        upstreamGapStartedAtUtc = DateTimeOffset.MinValue;
        lastAssignmentMapId = string.Empty;
        lastAssignmentTopologyRevision = 0;
        lastAssignmentTopologyContentHash = string.Empty;
        upstreamGapHistoryExpired = false;
        bootLogged = false;
        inputAssignmentsTotal = 0;
        dependencyEnvelopeReadyTotal = 0;
        alternateVetoTotal = 0;
        directUnavailableTotal = 0;
        degradedDirectTotal = 0;
        replacementSelectionPressureTotal = 0;
        insideRegionTotal = 0;
        positionEstablishedTotal = 0;
        positionDependencyEnvelopeObservedTotal = 0;
        assignmentChangeTotal = 0;
        establishmentTransitionTotal = 0;
        upstreamGapRetainedTicksTotal = 0;
        upstreamGapRestoredTotal = 0;
        upstreamGapExpiredTotal = 0;
        upstreamGapTopologyBreakTotal = 0;
        SplitFlankStateService.Reset("split_flank_readiness_reset");
    }

    private static string ThreatGroupKey(string ownerProfileId, string threatId)
        => Safe(ownerProfileId) + "|" + Safe(threatId);

    // FlankSupport is optional and may legitimately rotate while the selected alternate lead and axis remain
    // unchanged. Do not promote support-only churn into a new lead assignment: doing so would reset the
    // PositionEstablished history and can drain an otherwise-current AltDevelop maneuver. SplitFlankState keeps
    // support identity in its own tracker key so support-derived pressure/full-alt stability still reacquires
    // independently when the support member changes.
    private static string AssignmentKey(ThreatGroupRouteAssignment assignment)
        => string.Join("|",
            Safe(assignment.OwnerProfileId),
            Safe(assignment.ThreatId),
            Safe(assignment.AlternateCandidateId),
            Safe(assignment.SelectedAltBotProfileId));

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Replace(';', '_').Replace('\n', ' ').Replace('\r', ' ');

    private static string ShortHash(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Length <= 12 ? value : value.Substring(0, 12);

    private static string Number(float value)
        => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string FiniteNumber(float value)
        => float.IsNaN(value) || float.IsInfinity(value) ? "unknown" : Number(value);

    private static string VectorText(Vector3 value)
        => $"{Number(value.x)},{Number(value.y)},{Number(value.z)}";

    private sealed class EstablishmentTracker
    {
        public bool Inside { get; set; }
        public bool Established { get; set; }
        public int StableSamples { get; set; }
        public DateTimeOffset EnteredAtUtc { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastObservedAtUtc { get; set; } = DateTimeOffset.MinValue;
    }

    private sealed class AssignmentTracker
    {
        public AssignmentTracker(string assignmentKey, DateTimeOffset firstSeenAtUtc, DateTimeOffset lastSeenAtUtc, int changeCount)
        {
            AssignmentKey = assignmentKey;
            FirstSeenAtUtc = firstSeenAtUtc;
            LastSeenAtUtc = lastSeenAtUtc;
            ChangeCount = changeCount;
        }

        public string AssignmentKey { get; }
        public DateTimeOffset FirstSeenAtUtc { get; }
        public DateTimeOffset LastSeenAtUtc { get; set; }
        public int ChangeCount { get; }
    }
}
#endif
