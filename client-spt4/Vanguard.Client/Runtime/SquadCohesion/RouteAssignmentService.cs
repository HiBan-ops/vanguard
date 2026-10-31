#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Implements shadow-only Operator-to-alternate-establishment planning over observed independent route axes.
// Flow: Structurally valid alternate axes are combined with same-owner Operator candidates, planning membership/provenance is checked, then one candidate-specific NavMesh path proves reachability before deterministic preference ordering.
// Authority boundary: Route assignment has zero movement/intent/lease authority. It never asks whether an Operator may move right now; execution authority is evaluated later by the physical behavior that consumes the plan.
// Invariant: A temporary local combat/survival interruption may suspend execution but must not erase a still-valid route plan. Planning viability therefore depends on stable membership/provenance/path truth, not current SAIN action, execution leases, visibility, incoming fire or other transient execution state.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class RouteAssignmentService
{
    public const string StatusTag = "VANGUARD_ROUTE_ASSIGNMENT_STATUS";

    private static readonly TimeSpan OptionalMaxDeferral = TimeSpan.FromSeconds(1.5d);
    // Active physical consumers may bridge at most two complete optional-publication deferral windows.
    // This is continuity authority only: new execution still requires a fresh current RouteAssignment snapshot.
    internal static TimeSpan PhysicalConsumerContinuityGraceDuration
        => TimeSpan.FromTicks(OptionalMaxDeferral.Ticks * 2L);
    private static readonly TimeSpan SummaryLogInterval = TimeSpan.FromSeconds(15.0d);
    private static readonly TimeSpan CandidateProbeCacheLifetime = TimeSpan.FromSeconds(1.25d);
    private static readonly TimeSpan CandidateProbeHistoryLifetime = TimeSpan.FromSeconds(8.0d);
    private const int CandidateProbeCost = 1; // one CalculatePath from the current Operator position to an route independence AltEstablishmentAnchor
    private const int MaxCandidateProbeHeavyWorkPerFrame = 4;
    private const int MaxCandidateProbeCacheEntries = 96;
    private const int MaxObservedAxesPerGroup = 3;
    private const float CandidateProbePositionToleranceMeters = 1.5f;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, CachedCandidateAltProbe> CandidateProbeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, FreshTacticalReplanRequest> FreshReplanRequestsByThreatGroup = new(StringComparer.OrdinalIgnoreCase);

    private static RouteAssignmentSnapshot current = RouteAssignmentSnapshot.Empty;
    // Current publication must fail closed on any upstream incoherence. Separately retain the last
    // coherent publication for the same bounded interval already granted to active physical consumers.
    // This history is planning-continuity evidence only: TryGetSnapshot never exposes it and it can
    // never authorize a new physical start while current RouteAssignment truth is unavailable.
    private static RouteAssignmentSnapshot continuityHistory = RouteAssignmentSnapshot.Empty;
    private static DateTimeOffset continuityHistoryValidUntilUtc = DateTimeOffset.MinValue;
    private static string publishedInputIdentity = string.Empty;
    private static string lastDetailSignature = string.Empty;
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextCoherenceGapLogAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;
    private static int observedAxisInputGroupsTotal;
    private static int observedAxesConsideredTotal;
    private static int feasiblePairsTotal;
    private static int shadowSelectionsTotal;
    private static int planningRejectTotal;
    private static int identityMissingTotal;
    private static int betterRoleUnprovenTotal;
    private static int betterRolePathProvenTotal;
    private static int candidatePathProbesTotal;
    private static int candidatePathCompleteTotal;
    private static int candidatePathIncompleteTotal;
    private static int candidateProbeCacheHitsTotal;
    private static int candidateProbeBudgetDeferralsTotal;
    private static int freshReplanRequestsTotal;
    private static int freshReplanComputationsTotal;
    private static int freshReplanNoGroupTotal;
    private static int continuityHistoryCapturesTotal;
    private static int continuityHistoryLeadUsesTotal;
    private static int continuityHistoryExpiredTotal;
    private static int continuityHistoryRejectedTotal;
    private const int MaxPairLossDiagnosticsPerRaid = 96;
    private static int pairLossDiagnosticsEmitted;
    private static int pairLossDiagnosticsDropped;
    private static string lastPairLossDiagnosticSignature = string.Empty;

    public static void Tick(DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            ResetInternal();
            return;
        }

        if (!TryGetCoherentInputs(out var convergenceSnapshot, out var routeSnapshot))
        {
            LogCoherenceGap(now);
            InvalidatePublished(now);
            return;
        }

        FreshTacticalReplanRequest[] freshRequests;
        lock (Sync)
        {
            freshRequests = FreshReplanRequestsByThreatGroup.Values.ToArray();
        }

        string inputIdentity = InputIdentity(convergenceSnapshot, routeSnapshot);
        if (string.Equals(inputIdentity, publishedInputIdentity, StringComparison.Ordinal)
            && freshRequests.Length == 0)
        {
            return;
        }

        if (!VanguardRuntimeFrameBudgetGuard.ShouldRunOptional("RouteAssignment", now, OptionalMaxDeferral, out string optionalBudgetReason))
        {
            return;
        }

        PruneCandidateProbeCache(now);

        RouteAssignmentSnapshot continuitySnapshot = ResolveSelectionContinuitySnapshot(
            convergenceSnapshot,
            now,
            out bool continuityFromBoundedHistory);

        var assignments = new List<ThreatGroupRouteAssignment>();
        int observedAxisInputGroups = 0;
        int observedAxesConsidered = 0;
        int feasiblePairs = 0;
        int shadowSelections = 0;
        int planningRejects = 0;
        int identityMissing = 0;
        int betterRoleUnproven = 0;
        int betterRolePathProven = 0;
        int candidatePathProbes = 0;
        int candidatePathComplete = 0;
        int candidatePathIncomplete = 0;
        int candidateProbeCacheHits = 0;
        int candidateProbePlanningSkips = 0;
        int candidateProbeIdentityMissing = 0;
        int candidateProbeBudgetDeferrals = 0;

        foreach (var routeGroup in routeSnapshot.ThreatGroups)
        {
            if (!routeGroup.HasObservedAlternateAxis)
            {
                continue;
            }

            observedAxisInputGroups++;
            string threatGroupKey = ThreatGroupKey(routeGroup.OwnerProfileId, routeGroup.ThreatId);
            FreshTacticalReplanRequest? freshRequest = freshRequests.FirstOrDefault(item =>
                string.Equals(item.ThreatGroupKey, threatGroupKey, StringComparison.OrdinalIgnoreCase));
            bool forceFreshPlan = freshRequest != null;
            if (forceFreshPlan)
            {
                ClearCandidateProbeCacheForThreatGroup(routeGroup.OwnerProfileId, routeGroup.ThreatId);
            }

            var convergenceGroup = convergenceSnapshot.ThreatGroups.FirstOrDefault(group =>
                string.Equals(group.OwnerProfileId, routeGroup.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(group.ThreatId, routeGroup.ThreatId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(group.Classification, "shared_threat_paths_converged", StringComparison.OrdinalIgnoreCase));
            if (convergenceGroup == null)
            {
                continue;
            }

            var axes = routeGroup.AxisCandidates
                .Where(candidate => candidate.IsObservedAlternateAxis && candidate.HasUsableGeometry)
                .OrderByDescending(candidate => candidate.AxisEvidenceScore)
                .ThenBy(candidate => candidate.PathLengthMeters)
                .ThenBy(candidate => candidate.CandidateId, StringComparer.OrdinalIgnoreCase)
                .Take(MaxObservedAxesPerGroup)
                .ToArray();
            if (axes.Length == 0)
            {
                continue;
            }
            observedAxesConsidered += axes.Length;

            if (!TryBuildCandidateFeasibilityMatrix(
                    convergenceGroup,
                    routeGroup,
                    axes,
                    now,
                    out var candidateFeasibility,
                    out int betterUnprovenForGroup,
                    out int betterProvenForGroup,
                    out int pathProbesForGroup,
                    out int pathCompleteForGroup,
                    out int pathIncompleteForGroup,
                    out int cacheHitsForGroup,
                    out int planningSkipsForGroup,
                    out int identityMissingForGroup,
                    out int budgetDeferralsForGroup))
            {
                candidateProbeBudgetDeferrals += budgetDeferralsForGroup;
                candidateProbeBudgetDeferralsTotal += budgetDeferralsForGroup;
                return;
            }

            betterRoleUnproven += betterUnprovenForGroup;
            betterRolePathProven += betterProvenForGroup;
            candidatePathProbes += pathProbesForGroup;
            candidatePathComplete += pathCompleteForGroup;
            candidatePathIncomplete += pathIncompleteForGroup;
            candidateProbeCacheHits += cacheHitsForGroup;
            candidateProbePlanningSkips += planningSkipsForGroup;
            candidateProbeIdentityMissing += identityMissingForGroup;
            planningRejects += planningSkipsForGroup;
            identityMissing += identityMissingForGroup;

            AltRouteFeasibilityObservation[] feasibleCandidates = candidateFeasibility
                .Where(candidate => candidate.IsFeasiblePair)
                .OrderByDescending(candidate => candidate.RolePreferenceRank)
                .ThenBy(candidate => candidate.PathLengthMeters)
                .ThenByDescending(candidate => candidate.AxisEvidenceScore)
                .ThenBy(candidate => candidate.AxisCandidateId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.BotProfileId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            AltRouteFeasibilityObservation[] disciplinedLeadCandidates = ApplyMarksmanHighThreatLeadDiscipline(
                feasibleCandidates,
                routeGroup.ThreatId,
                now);

            var selected = SelectPreferredWithPlanContinuity(
                routeGroup.OwnerProfileId,
                routeGroup.ThreatId,
                disciplinedLeadCandidates,
                continuitySnapshot,
                continuityFromBoundedHistory,
                forceFreshPlan,
                now,
                out string selectionContinuityReason);
            if (selectionContinuityReason.IndexOf("previous_pair_no_longer_planning_viable_or_path_proven", StringComparison.Ordinal) >= 0)
            {
                EmitPairLossDiagnostic(routeGroup, routeSnapshot, continuitySnapshot, axes,
                    candidateFeasibility, feasibleCandidates, disciplinedLeadCandidates,
                    selectionContinuityReason, now);
            }

            string[] usableConvergenceBots = convergenceGroup.Routes
                .Where(route => route.HasUsableGeometry && !string.IsNullOrWhiteSpace(route.BotProfileId))
                .Select(route => route.BotProfileId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            AltRouteFeasibilityObservation? selectedSupport = null;
            if (selected != null && usableConvergenceBots.Length >= 3)
            {
                AltRouteFeasibilityObservation[] supportCandidates = feasibleCandidates
                    .Where(candidate => string.Equals(candidate.AxisCandidateId, selected.AxisCandidateId, StringComparison.OrdinalIgnoreCase))
                    .Where(candidate => !string.Equals(candidate.BotProfileId, selected.BotProfileId, StringComparison.OrdinalIgnoreCase))
                    .Where(candidate => usableConvergenceBots.Contains(candidate.BotProfileId, StringComparer.OrdinalIgnoreCase))
                    .Where(candidate => usableConvergenceBots.Count(bot =>
                        !string.Equals(bot, selected.BotProfileId, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(bot, candidate.BotProfileId, StringComparison.OrdinalIgnoreCase)) >= 1)
                    // FlankSupport has no frozen contribution-specific role matrix yet. Keep this
                    // opportunity geometry-first rather than reusing the FlankLead role preference.
                    .OrderBy(candidate => candidate.PathLengthMeters)
                    .ThenBy(candidate => candidate.BotProfileId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                selectedSupport = SelectAltSupportWithPlanContinuity(
                    routeGroup.OwnerProfileId,
                    routeGroup.ThreatId,
                    selected,
                    supportCandidates,
                    continuitySnapshot,
                    forceFreshPlan);
            }

            int feasiblePairCount = candidateFeasibility.Count(candidate => candidate.IsFeasiblePair);
            feasiblePairs += feasiblePairCount;

            AxisCandidateObservation referenceAxis = selected == null
                ? axes[0]
                : axes.First(axis => string.Equals(axis.CandidateId, selected.AxisCandidateId, StringComparison.OrdinalIgnoreCase));

            string selectedAltBot = selected?.BotProfileId ?? string.Empty;
            string selectedAltOperator = selected?.OperatorId ?? string.Empty;
            string selectedAltSupportBot = selectedSupport?.BotProfileId ?? string.Empty;
            string selectedAltSupportOperator = selectedSupport?.OperatorId ?? string.Empty;
            string role = selected?.Role ?? string.Empty;
            string specialty = selected?.Specialty ?? string.Empty;
            string roleBand = selected?.RolePreferenceBand ?? "N";
            string basePersona = string.Empty;
            string doctrine = string.Empty;
            string temperament = string.Empty;
            bool planningViable = selected?.PlanningViable == true;
            string planningReason = selected?.PlanningViabilityReason ?? "no_feasible_member_axis_pair";

            if (selected != null
                && VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(selected.BotProfileId, out var selectedRuntime))
            {
                basePersona = selectedRuntime.BasePersona;
                doctrine = selectedRuntime.Doctrine;
                temperament = selectedRuntime.Temperament;
                shadowSelections++;
            }

            string classification = selected != null
                ? (string.Equals(selected.BotProfileId, referenceAxis.SeedBotProfileId, StringComparison.OrdinalIgnoreCase)
                    ? "shadow_alt_establishment_selected_seed"
                    : "shadow_alt_establishment_selected_replacement")
                : "no_assignment_no_feasible_member_axis_pair";
            string reason = selected != null
                ? "planning_conditions_and_path_passed:" + selectionContinuityReason
                : "no_operator_has_complete_current_path_and_planning_viability_to_observed_alt_establishment";

            string[] directBots = convergenceGroup.Routes
                .Where(route => route.HasUsableGeometry
                    && !string.Equals(route.BotProfileId, selectedAltBot, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(route.BotProfileId, selectedAltSupportBot, StringComparison.OrdinalIgnoreCase))
                .Select(route => route.BotProfileId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var assignment = new ThreatGroupRouteAssignment(
                routeGroup.OwnerProfileId,
                routeGroup.ThreatId,
                referenceAxis.CandidateId,
                referenceAxis.SeedBotProfileId,
                referenceAxis.CandidateSource,
                referenceAxis.ApproachSector,
                referenceAxis.EstablishmentAnchor,
                referenceAxis.EstablishmentRegionCenter,
                referenceAxis.EstablishmentRegionRadiusMeters,
                referenceAxis.AxisEvidenceScore,
                axes.Length,
                feasiblePairCount,
                selectedAltBot,
                selectedAltOperator,
                selectedAltSupportBot,
                selectedAltSupportOperator,
                selectedSupport?.CandidateSpecificRouteProven == true,
                selectedSupport?.PathLengthMeters ?? 0f,
                role,
                specialty,
                basePersona,
                doctrine,
                temperament,
                roleBand,
                planningViable,
                planningReason,
                selected?.CandidateSpecificRouteProven == true,
                selected?.PathLengthMeters ?? 0f,
                selected?.DirectDistanceMeters ?? 0f,
                selected?.PathRatio ?? 0f,
                betterUnprovenForGroup,
                betterProvenForGroup,
                true,
                candidateFeasibility,
                directBots,
                classification,
                reason);
            assignments.Add(assignment);
            EmitDetailIfChanged(assignment, referenceAxis);
            EmitPairObservations(assignment, candidateFeasibility);
        }

        var next = new RouteAssignmentSnapshot(
            convergenceSnapshot.MapId,
            convergenceSnapshot.TopologyRevision,
            convergenceSnapshot.TopologyContentHash,
            convergenceSnapshot.OperatorBatchAtUtc,
            routeSnapshot.PublishedAtUtc,
            now,
            assignments.ToArray());

        lock (Sync)
        {
            current = next;
            continuityHistory = RouteAssignmentSnapshot.Empty;
            continuityHistoryValidUntilUtc = DateTimeOffset.MinValue;
        }
        publishedInputIdentity = inputIdentity;
        ConsumeFreshTacticalReplanRequests(freshRequests, assignments, now);
        observedAxisInputGroupsTotal += observedAxisInputGroups;
        observedAxesConsideredTotal += observedAxesConsidered;
        feasiblePairsTotal += feasiblePairs;
        shadowSelectionsTotal += shadowSelections;
        planningRejectTotal += planningRejects;
        identityMissingTotal += identityMissing;
        betterRoleUnprovenTotal += betterRoleUnproven;
        betterRolePathProvenTotal += betterRolePathProven;
        candidatePathProbesTotal += candidatePathProbes;
        candidatePathCompleteTotal += candidatePathComplete;
        candidatePathIncompleteTotal += candidatePathIncomplete;
        candidateProbeCacheHitsTotal += candidateProbeCacheHits;

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ROUTE_ASSIGNMENT_BOOT enabled=true; mode=alt_establishment_member_assignment_shadow; shadowOnly=true; executionAllowed=false; routeAssignment=false; movementAuthority=false; intentAuthority=false; leaseAuthority=false; routeInput=alternate_tactical_axis_observation; candidateSpecificExpansion=direct_route_plus_same_owner_shared_threat_supplemental_path_to_alt_establishment_anchor; observedAxesPerGroupMax={MaxObservedAxesPerGroup}; replacementSelectionShadow=true; preferencePolicy=planning_conditions_then_simple_distance_role_band_then_path_length_then_axis_evidence_plus_failed_delivery_same_axis_material_delivery_recovery; transientGapPlanContinuity=bounded_history_only_no_stale_publication; flankLeadTuningScope=PLAYER_SCOPED; flankLeadCloseDefaultMeters={VanguardTacticalCoordinationTuningPolicy.DefaultFlankLeadCloseMeters:0}; flankLeadFarDefaultMeters={VanguardTacticalCoordinationTuningPolicy.DefaultFlankLeadFarMeters:0}; distanceMissingFallsBackToRoleDefault=true; preferenceNeverHardRejects=true; equipmentPreferenceApplied=false; splitFlankExecution=false; build={VanguardBuildVersion.BuildLabel}");
        }

        if (now >= nextSummaryAtUtc)
        {
            nextSummaryAtUtc = now + SummaryLogInterval;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ROUTE_ASSIGNMENT_SUMMARY map={Safe(convergenceSnapshot.MapId)}; topologyRevision={convergenceSnapshot.TopologyRevision}; topologyHash={ShortHash(convergenceSnapshot.TopologyContentHash)}; observedAxisInputGroups={observedAxisInputGroups}; observedAxesConsidered={observedAxesConsidered}; shadowGroups={assignments.Count}; feasiblePairs={feasiblePairs}; shadowSelections={shadowSelections}; planningRejects={planningRejects}; identityMissing={identityMissing}; betterRoleUnproven={betterRoleUnproven}; betterRolePathProven={betterRolePathProven}; candidatePathProbes={candidatePathProbes}; candidatePathComplete={candidatePathComplete}; candidatePathIncomplete={candidatePathIncomplete}; candidateProbeCacheHits={candidateProbeCacheHits}; candidateProbePlanningSkips={candidateProbePlanningSkips}; candidateProbeIdentityMissing={candidateProbeIdentityMissing}; candidateProbeBudgetDeferrals={candidateProbeBudgetDeferrals}; candidateProbeCacheEntries={CandidateProbeCache.Count}; observedAxisInputGroupsTotal={observedAxisInputGroupsTotal}; observedAxesConsideredTotal={observedAxesConsideredTotal}; feasiblePairsTotal={feasiblePairsTotal}; shadowSelectionsTotal={shadowSelectionsTotal}; planningRejectTotal={planningRejectTotal}; identityMissingTotal={identityMissingTotal}; betterRoleUnprovenTotal={betterRoleUnprovenTotal}; betterRolePathProvenTotal={betterRolePathProvenTotal}; candidatePathProbesTotal={candidatePathProbesTotal}; candidatePathCompleteTotal={candidatePathCompleteTotal}; candidatePathIncompleteTotal={candidatePathIncompleteTotal}; candidateProbeCacheHitsTotal={candidateProbeCacheHitsTotal}; candidateProbeBudgetDeferralsTotal={candidateProbeBudgetDeferralsTotal}; freshReplanRequestsTotal={freshReplanRequestsTotal}; freshReplanComputationsTotal={freshReplanComputationsTotal}; freshReplanNoGroupTotal={freshReplanNoGroupTotal}; continuityHistoryCapturesTotal={continuityHistoryCapturesTotal}; continuityHistoryLeadUsesTotal={continuityHistoryLeadUsesTotal}; continuityHistoryExpiredTotal={continuityHistoryExpiredTotal}; continuityHistoryRejectedTotal={continuityHistoryRejectedTotal}; continuityHistorySource={(continuityFromBoundedHistory ? "bounded_gap_history" : (continuitySnapshot.IsAvailable ? "current_publication" : "none"))}; pendingFreshReplanRequests={FreshReplanRequestsByThreatGroup.Count}; optionalBudget={Safe(optionalBudgetReason)}; pairLossDiagnosticsEmitted={pairLossDiagnosticsEmitted}; pairLossDiagnosticsDropped={pairLossDiagnosticsDropped}; candidateSpecificAltEvidence=current_same_owner_member_path_to_alt_establishment_anchor; sharedThreatSupplementalCandidates=true; identitySource=raid_manifest_projection; roleMatrix=flank_lead_simple_distance_plus_tactical_role; basePersonaApplied=false; equipmentApplied=false; distancePreferenceNeverHardRejects=true; shadowOnly=true; executionAllowed=false; routeAssignment=false; movementMutation=false; splitFlankExecution=false");
        }
    }

    internal static bool RequestFreshTacticalReplan(
        string ownerProfileId,
        string threatId,
        long planEpoch,
        DateTimeOffset now,
        string reason)
    {
        if (!VanguardFikaCompat.IsRaidAuthority
            || string.IsNullOrWhiteSpace(ownerProfileId)
            || string.IsNullOrWhiteSpace(threatId)
            || planEpoch <= 0)
        {
            return false;
        }

        string key = ThreatGroupKey(ownerProfileId, threatId);
        lock (Sync)
        {
            if (FreshReplanRequestsByThreatGroup.TryGetValue(key, out var existing)
                && existing.PlanEpoch >= planEpoch)
            {
                return false;
            }
            FreshReplanRequestsByThreatGroup[key] = new FreshTacticalReplanRequest(
                key,
                ownerProfileId,
                threatId,
                planEpoch,
                now,
                reason);
        }
        freshReplanRequestsTotal++;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_ROUTE_ASSIGNMENT_FRESH_REPLAN_REQUESTED owner={Safe(ownerProfileId)}; threat={Safe(threatId)}; epoch={planEpoch}; reason={Safe(reason)}; forceCurrentPathReprobe=true; bypassPlanContinuityOnce=true; movementAuthority=false; executionAllowed=false");
        return true;
    }

    public static bool TryGetSnapshot(out RouteAssignmentSnapshot snapshot)
    {
        lock (Sync)
        {
            snapshot = current;
            return snapshot.IsAvailable;
        }
    }

    public static void Reset(string reason)
    {
        ResetInternal();
        if (!string.IsNullOrWhiteSpace(reason))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ROUTE_ASSIGNMENT_RESET reason={Safe(reason)}; shadowOnly=true; executionAllowed=false; routeAssignment=false; staleResume=false");
        }
    }

    private static bool TryGetCoherentInputs(
        out RouteConvergenceSnapshot convergenceSnapshot,
        out RouteIndependenceSnapshot routeSnapshot)
    {
        convergenceSnapshot = RouteConvergenceSnapshot.Empty;
        routeSnapshot = RouteIndependenceSnapshot.Empty;
        if (!RouteConvergenceService.TryGetSnapshot(out var currentConvergence)
            || !RouteIndependenceService.TryGetSnapshot(out var currentRouteState))
        {
            return false;
        }

        if (currentConvergence.TopologyRevision != currentRouteState.TopologyRevision
            || !string.Equals(currentConvergence.TopologyContentHash, currentRouteState.TopologyContentHash, StringComparison.Ordinal)
            || currentConvergence.OperatorBatchAtUtc != currentRouteState.OperatorBatchAtUtc
            || currentConvergence.PublishedAtUtc != currentRouteState.ConvergencePublishedAtUtc)
        {
            return false;
        }

        convergenceSnapshot = currentConvergence;
        routeSnapshot = currentRouteState;
        return true;
    }

    private static RouteAssignmentSnapshot ResolveSelectionContinuitySnapshot(
        RouteConvergenceSnapshot convergenceSnapshot,
        DateTimeOffset now,
        out bool fromBoundedHistory)
    {
        fromBoundedHistory = false;
        lock (Sync)
        {
            if (current.IsAvailable)
            {
                return current;
            }

            if (!continuityHistory.IsAvailable)
            {
                return RouteAssignmentSnapshot.Empty;
            }

            if (now > continuityHistoryValidUntilUtc)
            {
                continuityHistory = RouteAssignmentSnapshot.Empty;
                continuityHistoryValidUntilUtc = DateTimeOffset.MinValue;
                continuityHistoryExpiredTotal++;
                return RouteAssignmentSnapshot.Empty;
            }

            // Roster/evidence batches may legitimately advance during the short producer gap. Current
            // feasibility below re-proves the exact member/path before continuity can retain anything,
            // so only map/topology identity is required for this bounded historical comparison.
            bool compatible = string.Equals(continuityHistory.MapId, convergenceSnapshot.MapId, StringComparison.OrdinalIgnoreCase)
                && continuityHistory.TopologyRevision == convergenceSnapshot.TopologyRevision
                && string.Equals(continuityHistory.TopologyContentHash, convergenceSnapshot.TopologyContentHash, StringComparison.Ordinal);
            if (!compatible)
            {
                continuityHistory = RouteAssignmentSnapshot.Empty;
                continuityHistoryValidUntilUtc = DateTimeOffset.MinValue;
                continuityHistoryRejectedTotal++;
                return RouteAssignmentSnapshot.Empty;
            }

            fromBoundedHistory = true;
            return continuityHistory;
        }
    }

    private static string ContinuityReason(string reason, bool fromBoundedHistory)
        => fromBoundedHistory ? "bounded_gap_history_" + reason : reason;

    private static AltRouteFeasibilityObservation? SelectAltSupportWithPlanContinuity(
        string ownerProfileId,
        string threatId,
        AltRouteFeasibilityObservation selectedLead,
        IReadOnlyList<AltRouteFeasibilityObservation> supportCandidates,
        RouteAssignmentSnapshot continuitySnapshot,
        bool forceFreshPlan)
    {
        if (supportCandidates == null || supportCandidates.Count == 0)
        {
            return null;
        }
        AltRouteFeasibilityObservation best = supportCandidates[0];
        if (forceFreshPlan)
        {
            return best;
        }

        ThreatGroupRouteAssignment? previous = continuitySnapshot.IsAvailable
            ? continuitySnapshot.ThreatGroups.FirstOrDefault(group =>
                string.Equals(group.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(group.ThreatId, threatId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(group.AlternateCandidateId, selectedLead.AxisCandidateId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(group.SelectedAltBotProfileId, selectedLead.BotProfileId, StringComparison.OrdinalIgnoreCase)
                && group.HasShadowAltSupportSelection)
            : null;
        if (previous == null)
        {
            return best;
        }

        AltRouteFeasibilityObservation? retained = supportCandidates.FirstOrDefault(candidate =>
            string.Equals(candidate.BotProfileId, previous.SelectedAltSupportBotProfileId, StringComparison.OrdinalIgnoreCase));
        return retained ?? best;
    }

    internal static string ValidatePlanningCandidate(
        OperatorDecisionSnapshot snapshot,
        string ownerProfileId,
        DateTimeOffset sourceEvidenceCapturedAtUtc)
    {
        if (snapshot == null || !snapshot.Alive)
        {
            return "operator_dead_or_missing";
        }
        if (!string.Equals(snapshot.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return "owner_identity_changed";
        }
        if (sourceEvidenceCapturedAtUtc == DateTimeOffset.MinValue)
        {
            return "member_evidence_generation_missing";
        }
        if (snapshot.CapturedAtUtc < sourceEvidenceCapturedAtUtc)
        {
            return "decision_snapshot_precedes_member_evidence";
        }

        // Planning deliberately ignores transient execution state. A candidate remains a valid
        // route-plan member while SAIN temporarily reloads, heals, reacts to fire, owns another
        // movement action or carries an execution lease. The physical consumer revalidates all
        // of those conditions immediately before and during movement.
        return string.Empty;
    }

    private static AltRouteFeasibilityObservation? SelectPreferredWithPlanContinuity(
        string ownerProfileId,
        string threatId,
        IReadOnlyList<AltRouteFeasibilityObservation> feasibleCandidates,
        RouteAssignmentSnapshot continuitySnapshot,
        bool continuityFromBoundedHistory,
        bool forceFreshPlan,
        DateTimeOffset now,
        out string reason)
    {
        if (feasibleCandidates == null || feasibleCandidates.Count == 0)
        {
            reason = "no_feasible_pair";
            return null;
        }

        AltRouteFeasibilityObservation best = feasibleCandidates[0];
        if (forceFreshPlan)
        {
            reason = "fresh_replan_best_current_proven_pair";
            return best;
        }

        ThreatGroupRouteAssignment? previous = continuitySnapshot.IsAvailable
            ? continuitySnapshot.ThreatGroups.FirstOrDefault(group =>
                string.Equals(group.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(group.ThreatId, threatId, StringComparison.OrdinalIgnoreCase)
                && group.HasShadowAltSelection)
            : null;

        if (previous == null)
        {
            reason = "new_plan_best_preference_pair";
            return best;
        }

        var retained = feasibleCandidates.FirstOrDefault(candidate =>
            string.Equals(candidate.AxisCandidateId, previous.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.BotProfileId, previous.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase));
        if (retained == null)
        {
            if (continuityFromBoundedHistory)
            {
                continuityHistoryLeadUsesTotal++;
            }
            reason = ContinuityReason("previous_pair_no_longer_planning_viable_or_path_proven", continuityFromBoundedHistory);
            return best;
        }

        // A stronger role remains a planning preference only until physical AltDevelop intent exists.
        // Once the exact retained member owns a current AltDevelop intent for this threat, changing the
        // preferred role would become execution churn: TacticalReplan would drain the live maneuver even
        // though the retained pair is still path-proven. Preserve that logical maneuver across its bounded
        // local interruptions; normal stronger-role selection resumes as soon as the intent terminates.
        // Failed-delivery recovery below still owns post-terminal same-axis material replacement.
        // TacticalFitness shadow remains observation-only.
        if (best.RolePreferenceRank > retained.RolePreferenceRank)
        {
            if (TacticalIntentContinuityService.IsCurrentIntentForThreat(
                    retained.BotProfileId,
                    threatId,
                    TacticalIntentContinuityService.AltDevelopIntent,
                    now,
                    out _))
            {
                if (continuityFromBoundedHistory)
                {
                    continuityHistoryLeadUsesTotal++;
                }
                reason = ContinuityReason("stronger_role_preference_deferred_for_current_altdevelop_intent", continuityFromBoundedHistory);
                return retained;
            }

            if (continuityFromBoundedHistory)
            {
                continuityHistoryLeadUsesTotal++;
            }
            reason = ContinuityReason("stronger_role_preference_became_path_proven", continuityFromBoundedHistory);
            return best;
        }

        if (TrySelectFailedDeliveryReplacement(
            previous,
            retained,
            feasibleCandidates,
            now,
            out AltRouteFeasibilityObservation recoveryReplacement,
            out string recoveryReason))
        {
            if (continuityFromBoundedHistory)
            {
                continuityHistoryLeadUsesTotal++;
            }
            reason = ContinuityReason(recoveryReason, continuityFromBoundedHistory);
            return recoveryReplacement;
        }

        if (continuityFromBoundedHistory)
        {
            continuityHistoryLeadUsesTotal++;
        }
        reason = ContinuityReason("previous_pair_retained_for_plan_continuity", continuityFromBoundedHistory);
        return retained;
    }

    private static bool TrySelectFailedDeliveryReplacement(
        ThreatGroupRouteAssignment previous,
        AltRouteFeasibilityObservation retained,
        IReadOnlyList<AltRouteFeasibilityObservation> feasibleCandidates,
        DateTimeOffset now,
        out AltRouteFeasibilityObservation replacement,
        out string reason)
    {
        replacement = retained;
        reason = "previous_delivery_recovery_not_authorized";

        if (!AltDevelopMovementService.TryGetDevelopmentEvidence(previous, now, out AltDevelopEvidence developmentEvidence)
            || !developmentEvidence.DevelopmentObserved)
        {
            reason = "previous_delivery_not_yet_observed";
            return false;
        }

        if (developmentEvidence.IsActive || developmentEvidence.IsCompleted)
        {
            reason = developmentEvidence.IsActive
                ? "previous_delivery_still_active"
                : "previous_delivery_already_completed";
            return false;
        }

        bool physicalProgressObserved = developmentEvidence.PhysicalProgressObserved
            && !string.Equals(developmentEvidence.PhysicalProgressKind, "none", StringComparison.OrdinalIgnoreCase);
        bool closestRemainingDistanceProven = physicalProgressObserved
            && !float.IsNaN(developmentEvidence.ClosestRegionDistanceMeters)
            && !float.IsInfinity(developmentEvidence.ClosestRegionDistanceMeters)
            && developmentEvidence.ClosestRegionDistanceMeters > 0f;

        AltRouteFeasibilityObservation? candidate = feasibleCandidates
            .Where(item => string.Equals(item.AxisCandidateId, retained.AxisCandidateId, StringComparison.OrdinalIgnoreCase))
            .Where(item => !string.Equals(item.BotProfileId, retained.BotProfileId, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.PathLengthMeters < retained.PathLengthMeters)
            .Where(item => item.PathRatio > 0f
                && retained.PathRatio > 0f
                && item.PathRatio <= retained.PathRatio)
            .Where(item => !physicalProgressObserved
                || (closestRemainingDistanceProven
                    && item.PathLengthMeters < developmentEvidence.ClosestRegionDistanceMeters))
            .OrderByDescending(item => item.RolePreferenceRank)
            .ThenBy(item => item.PathLengthMeters)
            .ThenByDescending(item => item.AxisEvidenceScore)
            .ThenBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (candidate == null)
        {
            reason = physicalProgressObserved
                ? "previous_delivery_progress_retained_no_same_axis_material_delivery_dominance"
                : "previous_delivery_failed_without_same_axis_path_dominating_replacement";
            return false;
        }

        replacement = candidate;
        reason = physicalProgressObserved
            ? "previous_delivery_progress_same_axis_material_delivery_dominance"
            : "previous_delivery_terminated_without_progress_same_axis_path_dominance";
        return true;
    }

    internal static bool TryResolveMemberEvidenceCapturedAtUtc(
        ThreatGroupRouteAssignment assignment,
        string botProfileId,
        out DateTimeOffset evidenceCapturedAtUtc)
    {
        evidenceCapturedAtUtc = DateTimeOffset.MinValue;
        if (assignment == null || string.IsNullOrWhiteSpace(botProfileId))
        {
            return false;
        }

        var matching = assignment.AltCandidateFeasibility
            .Where(item => item != null
                && string.Equals(item.AxisCandidateId, assignment.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.BotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase)
                && item.SourceEvidenceCapturedAtUtc != DateTimeOffset.MinValue)
            .OrderByDescending(item => item.SourceEvidenceCapturedAtUtc)
            .FirstOrDefault();

        if (matching == null)
        {
            return false;
        }

        evidenceCapturedAtUtc = matching.SourceEvidenceCapturedAtUtc;
        return true;
    }

    private static bool TryBuildCandidateFeasibilityMatrix(
        ThreatGroupConvergenceAssessment group,
        ThreatGroupRouteObservation routeGroup,
        IReadOnlyList<AxisCandidateObservation> axes,
        DateTimeOffset now,
        out IReadOnlyList<AltRouteFeasibilityObservation> observations,
        out int betterRoleUnproven,
        out int betterRolePathProven,
        out int pathProbes,
        out int pathComplete,
        out int pathIncomplete,
        out int cacheHits,
        out int planningSkips,
        out int identityMissing,
        out int budgetDeferrals)
    {
        var results = new List<AltRouteFeasibilityObservation>();
        betterRoleUnproven = 0;
        betterRolePathProven = 0;
        pathProbes = 0;
        pathComplete = 0;
        pathIncomplete = 0;
        cacheHits = 0;
        planningSkips = 0;
        identityMissing = 0;
        budgetDeferrals = 0;

        var candidateMembers = BuildCandidateMembers(group, routeGroup.OwnerProfileId);
        VanguardTacticalCoordinationTuningPolicy.OwnerTacticalCoordinationTuning tuning =
            VanguardTacticalCoordinationTuningPolicy.ForOwner(routeGroup.OwnerProfileId);

        foreach (var axis in axes)
        {
            int seedRank = 0;
            if (VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(axis.SeedBotProfileId, out var seedRuntime))
            {
                float? seedThreatDistance = VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(axis.SeedBotProfileId, out var seedDecision)
                    ? ResolveFlankLeadThreatDistanceMeters(seedDecision, routeGroup.ThreatId)
                    : null;
                seedRank = RoleBandRank(AlternateRouteRolePreferenceBand(seedRuntime.TacticalRole, seedThreatDistance, tuning));
            }

            foreach (var candidate in candidateMembers)
            {
                bool isSeed = string.Equals(candidate.BotProfileId, axis.SeedBotProfileId, StringComparison.OrdinalIgnoreCase);
                string role = string.Empty;
                string specialty = string.Empty;
                string operatorId = candidate.OperatorId;
                string roleBand = "N";
                int roleRank = RoleBandRank(roleBand);
                bool locallyAdmissible = false;
                string planningReason = "identity_missing";
                bool betterRole = false;

                if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(candidate.BotProfileId, out var runtime))
                {
                    identityMissing++;
                    results.Add(new AltRouteFeasibilityObservation(
                        axis.CandidateId,
                        axis.CandidateSource,
                        axis.SeedBotProfileId,
                        axis.EstablishmentAnchor,
                        axis.EstablishmentRegionCenter,
                        axis.EstablishmentRegionRadiusMeters,
                        axis.AxisEvidenceScore,
                        candidate.BotProfileId,
                        operatorId,
                        candidate.EvidenceCapturedAtUtc,
                        isSeed,
                        role,
                        specialty,
                        roleBand,
                        roleRank,
                        false,
                        "identity_missing",
                        "candidate_specific_probe_not_run",
                        "not_probed",
                        0f,
                        Vector3.Distance(candidate.Position, axis.EstablishmentAnchor),
                        0f,
                        Array.Empty<Vector3>(),
                        false,
                        "viability_unproven",
                        "runtime_identity_missing"));
                    continue;
                }

                operatorId = runtime.OperatorId;
                role = runtime.TacticalRole;
                specialty = runtime.TacticalSpecialty;
                roleBand = AlternateRouteRolePreferenceBand(role, null, tuning);
                roleRank = RoleBandRank(roleBand);

                if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(candidate.BotProfileId, out var decision))
                {
                    planningReason = "decision_snapshot_missing";
                }
                else
                {
                    float? threatDistance = ResolveFlankLeadThreatDistanceMeters(decision, routeGroup.ThreatId);
                    roleBand = AlternateRouteRolePreferenceBand(role, threatDistance, tuning);
                    roleRank = RoleBandRank(roleBand);
                    planningReason = ValidatePlanningCandidate(decision, routeGroup.OwnerProfileId, candidate.EvidenceCapturedAtUtc);
                    locallyAdmissible = planningReason.Length == 0;
                }

                betterRole = roleRank > seedRank;

                if (!locallyAdmissible)
                {
                    planningSkips++;
                    if (betterRole)
                    {
                        betterRoleUnproven++;
                    }
                    results.Add(new AltRouteFeasibilityObservation(
                        axis.CandidateId,
                        axis.CandidateSource,
                        axis.SeedBotProfileId,
                        axis.EstablishmentAnchor,
                        axis.EstablishmentRegionCenter,
                        axis.EstablishmentRegionRadiusMeters,
                        axis.AxisEvidenceScore,
                        candidate.BotProfileId,
                        operatorId,
                        candidate.EvidenceCapturedAtUtc,
                        isSeed,
                        role,
                        specialty,
                        roleBand,
                        roleRank,
                        false,
                        string.IsNullOrWhiteSpace(planningReason) ? "planning_viability_unknown" : planningReason,
                        "candidate_specific_probe_not_run",
                        "not_probed",
                        0f,
                        Vector3.Distance(candidate.Position, axis.EstablishmentAnchor),
                        0f,
                        Array.Empty<Vector3>(),
                        false,
                        "planning_rejected",
                        "planning_membership_or_provenance_condition_precedes_geometry_probe"));
                    continue;
                }

                string key = CandidateProbeCacheKey(group.OwnerProfileId, group.ThreatId, axis.CandidateId, candidate.BotProfileId);
                CachedCandidateAltProbe probe;
                bool cacheHit;
                if (TryGetCachedCandidateProbe(key, candidate.Position, axis.EstablishmentAnchor, now, out var cached))
                {
                    probe = cached;
                    cacheHit = true;
                    cacheHits++;
                }
                else
                {
                    if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner(
                            "TcAltEstablishmentMemberProbe",
                            group.OwnerProfileId,
                            CandidateProbeCost,
                            MaxCandidateProbeHeavyWorkPerFrame,
                            out _))
                    {
                        budgetDeferrals++;
                        observations = Array.Empty<AltRouteFeasibilityObservation>();
                        return false;
                    }

                    pathProbes++;
                    probe = CalculateCandidateAltProbe(candidate.Position, axis.EstablishmentAnchor, now);
                    CandidateProbeCache[key] = probe;
                    cacheHit = false;
                }

                bool proven = string.Equals(probe.PathStatus, "complete", StringComparison.OrdinalIgnoreCase);
                if (proven)
                {
                    pathComplete++;
                    if (betterRole)
                    {
                        betterRolePathProven++;
                    }
                }
                else
                {
                    pathIncomplete++;
                    if (betterRole)
                    {
                        betterRoleUnproven++;
                    }
                }

                float directDistance = Vector3.Distance(candidate.Position, axis.EstablishmentAnchor);
                float pathRatio = directDistance > 0.01f ? probe.PathLengthMeters / directDistance : 0f;
                results.Add(new AltRouteFeasibilityObservation(
                    axis.CandidateId,
                    axis.CandidateSource,
                    axis.SeedBotProfileId,
                    axis.EstablishmentAnchor,
                    axis.EstablishmentRegionCenter,
                    axis.EstablishmentRegionRadiusMeters,
                    axis.AxisEvidenceScore,
                    candidate.BotProfileId,
                    operatorId,
                    candidate.EvidenceCapturedAtUtc,
                    isSeed,
                    role,
                    specialty,
                    roleBand,
                    roleRank,
                    true,
                    "ok",
                    "candidate_specific_current_path_to_alt_establishment_anchor",
                    probe.PathStatus,
                    probe.PathLengthMeters,
                    directDistance,
                    pathRatio,
                    probe.Corners,
                    cacheHit,
                    proven
                        ? (candidate.FromDirectThreatGroupRoute ? "feasible_alt_establishment_pair" : "feasible_alt_establishment_pair_shared_threat_supplemental")
                        : "viability_unproven",
                    proven
                        ? (candidate.FromDirectThreatGroupRoute ? "complete_current_path_to_alt_establishment_anchor" : "complete_current_path_to_alt_establishment_anchor_shared_threat_context")
                        : "candidate_specific_path_not_complete"));
            }
        }

        observations = results.ToArray();
        return true;
    }

    private static IReadOnlyList<AltCandidateMember> BuildCandidateMembers(
        ThreatGroupConvergenceAssessment group,
        string ownerProfileId)
    {
        var byBotProfileId = new Dictionary<string, AltCandidateMember>(StringComparer.OrdinalIgnoreCase);

        foreach (var route in group.Routes
            .Where(item => item.HasUsableGeometry)
            .OrderBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(route.BotProfileId))
            {
                continue;
            }

            byBotProfileId[route.BotProfileId] = new AltCandidateMember(
                route.BotProfileId,
                route.OperatorId,
                route.StartPosition,
                route.EvidenceCapturedAtUtc,
                fromDirectThreatGroupRoute: true);
        }

        foreach (var snapshot in VanguardOperatorDecisionSnapshotService.GetLatestSnapshots()
            .Where(item => item != null
                && item.Alive
                && string.Equals(item.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.BotProfileId))
            .OrderBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase))
        {
            if (byBotProfileId.ContainsKey(snapshot.BotProfileId))
            {
                continue;
            }

            byBotProfileId[snapshot.BotProfileId] = new AltCandidateMember(
                snapshot.BotProfileId,
                snapshot.OperatorId,
                snapshot.Position,
                snapshot.CapturedAtUtc,
                fromDirectThreatGroupRoute: false);
        }

        return byBotProfileId.Values
            .OrderBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static CachedCandidateAltProbe CalculateCandidateAltProbe(Vector3 start, Vector3 targetAnchor, DateTimeOffset now)
    {
        var path = new NavMeshPath();
        bool calculated;
        try
        {
            calculated = NavMesh.CalculatePath(start, targetAnchor, NavMesh.AllAreas, path);
        }
        catch
        {
            calculated = false;
        }

        Vector3[] corners = calculated && path.corners != null ? path.corners : Array.Empty<Vector3>();
        string status = !calculated
            ? "invalid"
            : path.status switch
            {
                NavMeshPathStatus.PathComplete => "complete",
                NavMeshPathStatus.PathPartial => "partial",
                _ => "invalid"
            };
        if (corners.Length < 2 && string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase))
        {
            status = "invalid";
        }

        return new CachedCandidateAltProbe(start, targetAnchor, now, status, PathLength(corners), corners);
    }

    private static bool TryGetCachedCandidateProbe(
        string key,
        Vector3 start,
        Vector3 targetAnchor,
        DateTimeOffset now,
        out CachedCandidateAltProbe probe)
    {
        probe = null!;
        if (!CandidateProbeCache.TryGetValue(key, out var cached)
            || now - cached.CreatedAtUtc > CandidateProbeCacheLifetime
            || Vector3.Distance(start, cached.StartPosition) > CandidateProbePositionToleranceMeters
            || Vector3.Distance(targetAnchor, cached.TargetAnchor) > CandidateProbePositionToleranceMeters)
        {
            return false;
        }

        probe = cached;
        return true;
    }

    private static void PruneCandidateProbeCache(DateTimeOffset now)
    {
        foreach (string key in CandidateProbeCache
            .Where(pair => now - pair.Value.CreatedAtUtc > CandidateProbeHistoryLifetime)
            .Select(pair => pair.Key)
            .ToArray())
        {
            CandidateProbeCache.Remove(key);
        }

        if (CandidateProbeCache.Count <= MaxCandidateProbeCacheEntries)
        {
            return;
        }

        foreach (string key in CandidateProbeCache
            .OrderBy(pair => pair.Value.CreatedAtUtc)
            .Take(CandidateProbeCache.Count - MaxCandidateProbeCacheEntries)
            .Select(pair => pair.Key)
            .ToArray())
        {
            CandidateProbeCache.Remove(key);
        }
    }

    private static string CandidateProbeCacheKey(string ownerProfileId, string threatId, string candidateId, string botProfileId)
        => string.Join("|", NormalizeKey(ownerProfileId), NormalizeThreat(threatId), NormalizeKey(candidateId), NormalizeKey(botProfileId));

    private static float PathLength(IReadOnlyList<Vector3> corners)
    {
        float length = 0f;
        for (int index = 1; index < corners.Count; index++)
        {
            length += Vector3.Distance(corners[index - 1], corners[index]);
        }
        return length;
    }

    private static AltRouteFeasibilityObservation[] ApplyMarksmanHighThreatLeadDiscipline(
        IReadOnlyList<AltRouteFeasibilityObservation> feasibleCandidates,
        string threatId,
        DateTimeOffset now)
    {
        if (feasibleCandidates == null || feasibleCandidates.Count == 0)
        {
            return Array.Empty<AltRouteFeasibilityObservation>();
        }

        AltRouteFeasibilityObservation[] original = feasibleCandidates.ToArray();
        if (!MarksmanForwardRoleDisciplinePolicy.TryClassifyHighThreatContact(threatId, out bool highThreat, out _)
            || !highThreat)
        {
            return original;
        }

        bool viableNonMarksmanAssaultCandidate = original.Any(candidate =>
            candidate != null
            && !MarksmanForwardRoleDisciplinePolicy.IsDesignatedMarksmanIdentity(candidate.Role, candidate.Specialty)
            && MarksmanForwardRoleDisciplinePolicy.IsAssaultCapableRole(candidate.Role));
        if (!viableNonMarksmanAssaultCandidate)
        {
            return original;
        }

        // Preserve the existing exact AltDevelop maneuver continuity contract. A high-threat role rule may
        // prevent unsafe physical translation, but it must not recreate the historical role-only plan churn
        // while the selected maneuver still owns current intent. Once that intent is absent, a fresh plan
        // excludes designated Marksmen from FlankLead selection when an assault-capable path-proven sibling exists.
        bool protectedCurrentMarksmanIntent = original.Any(candidate =>
            candidate != null
            && MarksmanForwardRoleDisciplinePolicy.IsDesignatedMarksmanIdentity(candidate.Role, candidate.Specialty)
            && TacticalIntentContinuityService.IsCurrentIntentForThreat(
                candidate.BotProfileId,
                threatId,
                TacticalIntentContinuityService.AltDevelopIntent,
                now,
                out _));
        if (protectedCurrentMarksmanIntent)
        {
            return original;
        }

        AltRouteFeasibilityObservation[] disciplined = original
            .Where(candidate => candidate == null
                || !MarksmanForwardRoleDisciplinePolicy.IsDesignatedMarksmanIdentity(candidate.Role, candidate.Specialty))
            .ToArray();
        return disciplined.Length > 0 ? disciplined : original;
    }

    private static float? ResolveFlankLeadThreatDistanceMeters(OperatorDecisionSnapshot snapshot, string threatId)
    {
        if (snapshot == null
            || !snapshot.Threat.HasThreat
            || string.IsNullOrWhiteSpace(threatId)
            || !string.Equals(snapshot.Threat.EnemyId, threatId, StringComparison.OrdinalIgnoreCase)
            || !snapshot.Threat.Distance.HasValue)
        {
            return null;
        }

        float distance = snapshot.Threat.Distance.Value;
        return float.IsNaN(distance) || float.IsInfinity(distance) || distance < 0f
            ? null
            : distance;
    }

    private static string AlternateRouteRolePreferenceBand(
        string role,
        float? threatDistanceMeters,
        VanguardTacticalCoordinationTuningPolicy.OwnerTacticalCoordinationTuning tuning)
    {
        string roleKey = NormalizeKey(role);
        bool closeThreat = threatDistanceMeters.HasValue && threatDistanceMeters.Value <= tuning.FlankLeadCloseMeters;
        bool farThreat = threatDistanceMeters.HasValue && threatDistanceMeters.Value >= tuning.FlankLeadFarMeters;

        // FlankLead is intentionally permissive. The role-preference matrix itself stays permissive:
        // distance and tactical role only change preference; they never invalidate an otherwise viable Operator.
        // The separate high-threat Marksman discipline is applied only outside this tuning matrix to fresh lead
        // selection/execution; support selection remains geometry-first. Missing distance falls back to the existing role ordering.
        if (closeThreat)
        {
            if (roleKey == "breacher")
            {
                return "P";
            }
            if (roleKey is "assault" or "veteran")
            {
                return "S";
            }
            if (roleKey == "recon")
            {
                return "N";
            }
            if (roleKey is "marksman" or "support" or "medic")
            {
                return "D";
            }
            return "N";
        }

        if (farThreat)
        {
            if (roleKey == "recon")
            {
                return "P";
            }
            if (roleKey is "assault" or "veteran")
            {
                return "S";
            }
            if (roleKey is "marksman" or "breacher")
            {
                return "N";
            }
            if (roleKey is "support" or "medic")
            {
                return "D";
            }
            return "N";
        }

        if (roleKey == "recon")
        {
            return "P";
        }
        if (roleKey is "assault" or "veteran" or "breacher")
        {
            return "S";
        }
        if (roleKey is "support" or "marksman" or "medic")
        {
            return "D";
        }
        return "N";
    }

    private static int RoleBandRank(string band)
        => band switch
        {
            "P" => 3,
            "S" => 2,
            "N" => 1,
            _ => 0,
        };

    private static void EmitDetailIfChanged(
        ThreatGroupRouteAssignment assignment,
        AxisCandidateObservation axis)
    {
        string signature = string.Join("|",
            assignment.OwnerProfileId,
            assignment.ThreatId,
            assignment.AlternateCandidateId,
            assignment.SelectedAltBotProfileId,
            assignment.SelectedAltSupportBotProfileId,
            assignment.Classification,
            assignment.PlanningViabilityReason,
            assignment.RolePreferenceBand,
            assignment.FeasiblePairCount.ToString(),
            assignment.SelectedPathLengthMeters.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            assignment.AltCandidateFeasibility.Count.ToString());
        if (string.Equals(signature, lastDetailSignature, StringComparison.Ordinal))
        {
            return;
        }
        lastDetailSignature = signature;

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_ROUTE_ASSIGNMENT_SHADOW_ASSIGNMENT owner={Safe(assignment.OwnerProfileId)}; threat={Safe(assignment.ThreatId)}; axis={Safe(assignment.AlternateCandidateId)}; axisSource={Safe(assignment.AlternateCandidateSource)}; axisSector={Safe(assignment.AlternateApproachSector)}; axisSeedBot={Safe(assignment.AlternateSeedBotProfileId)}; axisEvidenceScore={Number(assignment.AlternateAxisEvidenceScore)}; observedAxes={assignment.ObservedAxisCount}; establishment={VectorText(assignment.AlternateEstablishmentAnchor)}; establishmentRadius={Number(assignment.AlternateEstablishmentRegionRadiusMeters)}; selectedAltBot={Safe(assignment.SelectedAltBotProfileId)}; selectedAltOperator={Safe(assignment.SelectedAltOperatorId)}; selectedAltSupportBot={Safe(assignment.SelectedAltSupportBotProfileId)}; selectedAltSupportOperator={Safe(assignment.SelectedAltSupportOperatorId)}; selectedAltSupportPathProven={assignment.SelectedAltSupportPathProven}; selectedAltSupportPathLength={Number(assignment.SelectedAltSupportPathLengthMeters)}; role={Safe(assignment.Role)}; specialty={Safe(assignment.Specialty)}; basePersona={Safe(assignment.BasePersona)}; doctrine={Safe(assignment.Doctrine)}; temperament={Safe(assignment.Temperament)}; rolePreferenceBand={Safe(assignment.RolePreferenceBand)}; planningViable={assignment.SelectedAltPlanningViable}; planningReason={Safe(assignment.PlanningViabilityReason)}; selectedPathProven={assignment.SelectedPairPathProven}; selectedPathLength={Number(assignment.SelectedPathLengthMeters)}; selectedDirectDistance={Number(assignment.SelectedDirectDistanceMeters)}; selectedPathRatio={Number(assignment.SelectedPathRatio)}; feasiblePairs={assignment.FeasiblePairCount}; betterRoleUnproven={assignment.PotentialBetterRoleUnprovenCount}; betterRolePathProven={assignment.PotentialBetterRolePathProvenCount}; candidateMatrixComplete={assignment.CandidateMatrixComplete}; candidateMatrixPairs={assignment.AltCandidateFeasibility.Count}; directMembers={assignment.DirectBotProfileIds.Count}; axisPathCostTelemetry={Number(axis.PathCostRatioTelemetry)}; axisIndependentTail={Number(axis.IndependentTailMeters)}; axisTopologyEvidence={Safe(axis.TopologyEvidence)}; axisPreEstablishmentChoke={Safe(axis.PreEstablishmentChokeRelation)}; axisStableResamples={axis.StableResampleCount}; classification={Safe(assignment.Classification)}; reason={Safe(assignment.Reason)}; selectionPolicy=planning_conditions_then_simple_distance_role_band_then_path_length_then_axis_evidence_plus_failed_delivery_same_axis_material_delivery_recovery; distanceMissingFallsBackToRoleDefault=true; preferenceNeverHardRejects=true; roleCanNotRestoreMissingPath=true; replacementSelectionShadow=true; shadowOnly=true; executionAllowed=false; routeAssignment=false; movementMutation=false; splitFlankExecution=false");
    }

    // Read-only classification of one already-executed continuity rejection. All collections
    // are those consumed by the real selection; no path probe, cache write or reselection occurs.
    private static void EmitPairLossDiagnostic(
        ThreatGroupRouteObservation routeGroup,
        RouteIndependenceSnapshot routeSnapshot,
        RouteAssignmentSnapshot continuitySnapshot,
        IReadOnlyList<AxisCandidateObservation> rankedAxes,
        IReadOnlyList<AltRouteFeasibilityObservation> matrix,
        IReadOnlyList<AltRouteFeasibilityObservation> feasible,
        IReadOnlyList<AltRouteFeasibilityObservation> disciplined,
        string selectionReason,
        DateTimeOffset now)
    {
        if (!continuitySnapshot.IsAvailable)
        {
            return;
        }
        var previous = continuitySnapshot.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, routeGroup.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, routeGroup.ThreatId, StringComparison.OrdinalIgnoreCase)
            && item.HasShadowAltSelection);
        if (previous == null)
        {
            return;
        }

        var priorAxis = routeGroup.AxisCandidates.FirstOrDefault(item =>
            string.Equals(item.CandidateId, previous.AlternateCandidateId, StringComparison.OrdinalIgnoreCase));
        bool inTop = rankedAxes.Any(item =>
            string.Equals(item.CandidateId, previous.AlternateCandidateId, StringComparison.OrdinalIgnoreCase));
        // Reconstruct only the already-published rank; no additional path probe or reselection.
        int currentAxisRank = -1;
        if (priorAxis != null && priorAxis.IsObservedAlternateAxis && priorAxis.HasUsableGeometry)
        {
            var eligibleInCurrentSnapshot = routeGroup.AxisCandidates
                .Where(item => item.IsObservedAlternateAxis && item.HasUsableGeometry)
                .OrderByDescending(item => item.AxisEvidenceScore)
                .ThenBy(item => item.PathLengthMeters)
                .ThenBy(item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            int rankedIndex = Array.FindIndex(eligibleInCurrentSnapshot, item =>
                string.Equals(item.CandidateId, previous.AlternateCandidateId, StringComparison.OrdinalIgnoreCase));
            if (rankedIndex >= 0)
            {
                currentAxisRank = rankedIndex + 1;
            }
        }
        float cutoffEvidenceScore = rankedAxes.Count == MaxObservedAxesPerGroup
            ? rankedAxes[rankedAxes.Count - 1].AxisEvidenceScore
            : 0f;
        var priorPair = matrix.FirstOrDefault(item =>
            string.Equals(item.AxisCandidateId, previous.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.BotProfileId, previous.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase));
        bool inFeasible = feasible.Any(item =>
            string.Equals(item.AxisCandidateId, previous.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.BotProfileId, previous.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase));
        bool inDiscipline = disciplined.Any(item =>
            string.Equals(item.AxisCandidateId, previous.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.BotProfileId, previous.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase));
        string gate = priorAxis == null ? "not_in_independence_snapshot"
            : !priorAxis.HasUsableGeometry ? "geometry_unusable"
            : !priorAxis.IsObservedAlternateAxis ? "in_snapshot_not_observed"
            : !inTop ? "ranked_out_top_axes"
            : priorPair == null ? "pair_not_in_feasibility_matrix"
            : !priorPair.IsFeasiblePair ? "pair_not_feasible"
            : !inFeasible ? "pair_missing_from_feasible_set"
            : !inDiscipline ? "removed_by_lead_discipline"
            : "present_in_disciplined_set_review_required";
        string signature = string.Join("|", routeSnapshot.PublishedAtUtc.UtcDateTime.Ticks,
            previous.AlternateCandidateId, previous.SelectedAltBotProfileId, gate);
        if (string.Equals(signature, lastPairLossDiagnosticSignature, StringComparison.Ordinal))
        {
            return;
        }
        lastPairLossDiagnosticSignature = signature;
        if (pairLossDiagnosticsEmitted >= MaxPairLossDiagnosticsPerRaid)
        {
            pairLossDiagnosticsDropped++;
            return;
        }
        pairLossDiagnosticsEmitted++;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_ROUTE_ASSIGNMENT_PAIR_LOSS_CAUSE groupToken={DiagnosticToken(routeGroup.OwnerProfileId + "|" + routeGroup.ThreatId)}; axisToken={DiagnosticToken(previous.AlternateCandidateId)}; botToken={DiagnosticToken(previous.SelectedAltBotProfileId)}; gate={gate}; priorClass={Safe(priorAxis?.Classification)}; priorReason={Safe(priorAxis?.ReasonCode)}; priorPath={Safe(priorAxis?.PathStatus)}; priorStable={priorAxis?.StableResampleCount ?? 0}; currentAxisRank={currentAxisRank}; currentAxisScore={Number(priorAxis?.AxisEvidenceScore ?? 0f)}; topCutoffScore={Number(cutoffEvidenceScore)}; currentAxisHasObservedProof={priorAxis?.IsObservedAlternateAxis == true}; pairFeasibilityWhenRankedOut=unknown_not_probed; snapshotAxes={routeGroup.AxisCandidates.Count}; topAxes={rankedAxes.Count}; presentInTop={inTop}; pairInMatrix={priorPair != null}; pairFeasible={priorPair?.IsFeasiblePair == true}; matrixReason={Safe(priorPair?.PlanningViabilityReason)}; disciplined={inDiscipline}; independencePublishedTicks={routeSnapshot.PublishedAtUtc.UtcDateTime.Ticks}; previousAssignmentTicks={continuitySnapshot.PublishedAtUtc.UtcDateTime.Ticks}; decisionTicks={now.UtcDateTime.Ticks}; selectionReason={Safe(selectionReason)}; emitted={pairLossDiagnosticsEmitted}; dropped={pairLossDiagnosticsDropped}; observationOnly=true; movementAuthority=false");
    }

    private static string DiagnosticToken(string value)
        => unchecked((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(value ?? string.Empty)).ToString("X8");

    private static void EmitPairObservations(
        ThreatGroupRouteAssignment assignment,
        IReadOnlyList<AltRouteFeasibilityObservation> observations)
    {
        foreach (var observation in observations)
        {
            bool selected = string.Equals(observation.AxisCandidateId, assignment.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(observation.BotProfileId, assignment.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase)
                && assignment.HasShadowAltSelection;
            bool selectedSupport = string.Equals(observation.AxisCandidateId, assignment.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(observation.BotProfileId, assignment.SelectedAltSupportBotProfileId, StringComparison.OrdinalIgnoreCase)
                && assignment.HasShadowAltSupportSelection;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ROUTE_ASSIGNMENT_PAIR owner={Safe(assignment.OwnerProfileId)}; threat={Safe(assignment.ThreatId)}; axis={Safe(observation.AxisCandidateId)}; axisSource={Safe(observation.AxisCandidateSource)}; axisSeedBot={Safe(observation.AxisSeedBotProfileId)}; axisEvidenceScore={Number(observation.AxisEvidenceScore)}; establishment={VectorText(observation.EstablishmentAnchor)}; bot={Safe(observation.BotProfileId)}; operator={Safe(observation.OperatorId)}; isAxisSeed={observation.IsAxisSeed}; role={Safe(observation.Role)}; specialty={Safe(observation.Specialty)}; roleBand={Safe(observation.RolePreferenceBand)}; roleRank={observation.RolePreferenceRank}; planningViable={observation.PlanningViable}; planningReason={Safe(observation.PlanningViabilityReason)}; path={Safe(observation.PathStatus)}; pathLength={Number(observation.PathLengthMeters)}; directDistance={Number(observation.DirectDistanceMeters)}; pathRatio={Number(observation.PathRatio)}; cacheHit={observation.CacheHit}; feasiblePair={observation.IsFeasiblePair}; selected={selected}; selectedAltSupport={selectedSupport}; classification={Safe(observation.Classification)}; reason={Safe(observation.Reason)}; planningConditionsFirst=true; observationOnly=true; executionAllowed=false");
        }
    }

    // Capture why the planning publication was withdrawn, without exposing its bounded history
    // to physical consumers or changing the coherence conditions of the next publication.
    private static void LogCoherenceGap(DateTimeOffset now)
    {
        if (now < nextCoherenceGapLogAtUtc)
        {
            return;
        }
        nextCoherenceGapLogAtUtc = now + SummaryLogInterval;

        bool convergenceAvailable = RouteConvergenceService.TryGetSnapshot(out var convergence);
        bool independenceAvailable = RouteIndependenceService.TryGetSnapshot(out var independence);
        string reason = !convergenceAvailable ? "convergence_unavailable"
            : !independenceAvailable ? "independence_unavailable"
            : convergence.TopologyRevision != independence.TopologyRevision ? "topology_revision_mismatch"
            : !string.Equals(convergence.TopologyContentHash, independence.TopologyContentHash, StringComparison.Ordinal) ? "topology_hash_mismatch"
            : convergence.OperatorBatchAtUtc != independence.OperatorBatchAtUtc ? "operator_batch_mismatch"
            : convergence.PublishedAtUtc != independence.ConvergencePublishedAtUtc ? "convergence_publication_mismatch"
            : "recovered_between_reads";

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_ROUTE_ASSIGNMENT_INPUT_COHERENCE_GAP reason={reason}; convergenceAvailable={convergenceAvailable}; independenceAvailable={independenceAvailable}; convergenceTopology={convergence.TopologyRevision}; independenceTopology={independence.TopologyRevision}; convergenceHash={ShortHash(convergence.TopologyContentHash)}; independenceHash={ShortHash(independence.TopologyContentHash)}; convergenceBatchTicks={convergence.OperatorBatchAtUtc.UtcDateTime.Ticks}; independenceBatchTicks={independence.OperatorBatchAtUtc.UtcDateTime.Ticks}; convergencePublishedTicks={convergence.PublishedAtUtc.UtcDateTime.Ticks}; independenceSourceConvergenceTicks={independence.ConvergencePublishedAtUtc.UtcDateTime.Ticks}; publicationInvalidated=true; executionAuthority=false");
    }

    private static void InvalidatePublished(DateTimeOffset now)
    {
        lock (Sync)
        {
            if (current.IsAvailable)
            {
                continuityHistory = current;
                continuityHistoryValidUntilUtc = now + PhysicalConsumerContinuityGraceDuration;
                continuityHistoryCapturesTotal++;
            }
            current = RouteAssignmentSnapshot.Empty;
        }
        publishedInputIdentity = string.Empty;
    }

    private static void ResetInternal()
    {
        lock (Sync)
        {
            current = RouteAssignmentSnapshot.Empty;
            continuityHistory = RouteAssignmentSnapshot.Empty;
            continuityHistoryValidUntilUtc = DateTimeOffset.MinValue;
        }
        publishedInputIdentity = string.Empty;
        lastDetailSignature = string.Empty;
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        nextCoherenceGapLogAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        observedAxisInputGroupsTotal = 0;
        observedAxesConsideredTotal = 0;
        feasiblePairsTotal = 0;
        shadowSelectionsTotal = 0;
        planningRejectTotal = 0;
        identityMissingTotal = 0;
        betterRoleUnprovenTotal = 0;
        betterRolePathProvenTotal = 0;
        candidatePathProbesTotal = 0;
        candidatePathCompleteTotal = 0;
        candidatePathIncompleteTotal = 0;
        candidateProbeCacheHitsTotal = 0;
        candidateProbeBudgetDeferralsTotal = 0;
        freshReplanRequestsTotal = 0;
        freshReplanComputationsTotal = 0;
        freshReplanNoGroupTotal = 0;
        continuityHistoryCapturesTotal = 0;
        continuityHistoryLeadUsesTotal = 0;
        continuityHistoryExpiredTotal = 0;
        continuityHistoryRejectedTotal = 0;
        pairLossDiagnosticsEmitted = 0;
        pairLossDiagnosticsDropped = 0;
        lastPairLossDiagnosticSignature = string.Empty;
        CandidateProbeCache.Clear();
        FreshReplanRequestsByThreatGroup.Clear();
    }

    private static void ClearCandidateProbeCacheForThreatGroup(string ownerProfileId, string threatId)
    {
        string prefix = NormalizeKey(ownerProfileId) + "|" + NormalizeThreat(threatId) + "|";
        lock (Sync)
        {
            foreach (string key in CandidateProbeCache.Keys
                         .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                CandidateProbeCache.Remove(key);
            }
        }
    }

    private static void ConsumeFreshTacticalReplanRequests(
        IReadOnlyList<FreshTacticalReplanRequest> requests,
        IReadOnlyList<ThreatGroupRouteAssignment> assignments,
        DateTimeOffset now)
    {
        foreach (var request in requests)
        {
            bool groupPublished = assignments.Any(item =>
                string.Equals(item.OwnerProfileId, request.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ThreatId, request.ThreatId, StringComparison.OrdinalIgnoreCase));
            bool consumed = false;
            lock (Sync)
            {
                if (FreshReplanRequestsByThreatGroup.TryGetValue(request.ThreatGroupKey, out var currentRequest)
                    && currentRequest.PlanEpoch == request.PlanEpoch)
                {
                    FreshReplanRequestsByThreatGroup.Remove(request.ThreatGroupKey);
                    consumed = true;
                }
            }
            if (!consumed)
            {
                continue;
            }

            if (groupPublished) freshReplanComputationsTotal++;
            else freshReplanNoGroupTotal++;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ROUTE_ASSIGNMENT_FRESH_REPLAN_CONSUMED owner={Safe(request.OwnerProfileId)}; threat={Safe(request.ThreatId)}; epoch={request.PlanEpoch}; requestedAt={request.RequestedAtUtc:O}; consumedAt={now:O}; groupPublished={groupPublished}; outcome={(groupPublished ? "fresh_assignment_published" : "current_upstream_group_absent")}; reason={Safe(request.Reason)}; candidateCacheReused=false; planContinuityBypassedOnce=true; movementAuthority=false; executionAllowed=false");
        }
    }

    private static string ThreatGroupKey(string ownerProfileId, string threatId)
        => NormalizeKey(ownerProfileId) + "|" + NormalizeThreat(threatId);

    private static string InputIdentity(
        RouteConvergenceSnapshot convergenceSnapshot,
        RouteIndependenceSnapshot routeSnapshot)
        => string.Join("|",
            convergenceSnapshot.MapId,
            convergenceSnapshot.TopologyRevision.ToString(),
            convergenceSnapshot.TopologyContentHash,
            convergenceSnapshot.OperatorBatchAtUtc.UtcDateTime.Ticks.ToString(),
            convergenceSnapshot.PublishedAtUtc.UtcDateTime.Ticks.ToString(),
            routeSnapshot.PublishedAtUtc.UtcDateTime.Ticks.ToString(),
            RouteIndependenceSnapshot.SemanticRevision);

    private static string NormalizeThreat(string? value)
    {
        string normalized = NormalizeKey(value);
        return normalized is "" or "none" or "null" or "unknown" ? string.Empty : normalized;
    }

    private static string NormalizeKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Replace(';', '_').Replace('\n', ' ').Replace('\r', ' ');

    private static string ShortHash(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Length <= 12 ? value : value.Substring(0, 12);

    private static string Number(float value)
        => value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    private static string VectorText(Vector3 value)
        => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.00},{1:0.00},{2:0.00}", value.x, value.y, value.z);

    private sealed class FreshTacticalReplanRequest
    {
        public FreshTacticalReplanRequest(
            string threatGroupKey,
            string ownerProfileId,
            string threatId,
            long planEpoch,
            DateTimeOffset requestedAtUtc,
            string reason)
        {
            ThreatGroupKey = threatGroupKey;
            OwnerProfileId = ownerProfileId;
            ThreatId = threatId;
            PlanEpoch = planEpoch;
            RequestedAtUtc = requestedAtUtc;
            Reason = reason;
        }

        public string ThreatGroupKey { get; }
        public string OwnerProfileId { get; }
        public string ThreatId { get; }
        public long PlanEpoch { get; }
        public DateTimeOffset RequestedAtUtc { get; }
        public string Reason { get; }
    }

    private sealed class AltCandidateMember
    {
        public AltCandidateMember(
            string botProfileId,
            string operatorId,
            Vector3 position,
            DateTimeOffset evidenceCapturedAtUtc,
            bool fromDirectThreatGroupRoute)
        {
            BotProfileId = botProfileId ?? string.Empty;
            OperatorId = operatorId ?? string.Empty;
            Position = position;
            EvidenceCapturedAtUtc = evidenceCapturedAtUtc;
            FromDirectThreatGroupRoute = fromDirectThreatGroupRoute;
        }

        public string BotProfileId { get; }
        public string OperatorId { get; }
        public Vector3 Position { get; }
        public DateTimeOffset EvidenceCapturedAtUtc { get; }
        public bool FromDirectThreatGroupRoute { get; }
    }

    private sealed class CachedCandidateAltProbe
    {
        public CachedCandidateAltProbe(
            Vector3 startPosition,
            Vector3 targetAnchor,
            DateTimeOffset createdAtUtc,
            string pathStatus,
            float pathLengthMeters,
            IReadOnlyList<Vector3> corners)
        {
            StartPosition = startPosition;
            TargetAnchor = targetAnchor;
            CreatedAtUtc = createdAtUtc;
            PathStatus = pathStatus;
            PathLengthMeters = pathLengthMeters;
            Corners = corners ?? Array.Empty<Vector3>();
        }

        public Vector3 StartPosition { get; }
        public Vector3 TargetAnchor { get; }
        public DateTimeOffset CreatedAtUtc { get; }
        public string PathStatus { get; }
        public float PathLengthMeters { get; }
        public IReadOnlyList<Vector3> Corners { get; }
    }
}
#endif
