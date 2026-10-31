#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;

// Responsibility: Executes only high-confidence TacticalReplan lifecycle invalidations and the bounded post-success planning handoff after a completed strict FullAlt DirectCommit.
// Flow: Current-frame replan observations are filtered into executable or observation-only reasons; invalidations drain stale TC authority before fresh planning, while an exact successful FullAlt completion requests one fresh RouteAssignment recomputation without draining authority or advancing the consumed PlanEpoch.
// Authority boundary: this service cannot create movement, aim, fire, target, SAIN or BigBrain authority. It may only release stale TC authority and request non-authoritative planning recomputation.
// Invariant: An invalidated TC plan is never silently resumed through an old lease/path/window, and successful entry re-arbitrates from current facts without automatically reopening the consumed DirectCommit cycle.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class TacticalReplanExecutionService
{
    public const string StatusTag = "VANGUARD_TACTICAL_REPLAN_EXECUTION_STATUS";

    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(15d);
    private static readonly object Sync = new();
    private static readonly HashSet<string> ConsumedTransitionKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DeferredTransitionKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ConsumedSuccessfulCompletionRefreshKeys = new(StringComparer.OrdinalIgnoreCase);

    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;
    private static int executableInvalidationsTotal;
    private static int advisoryDeferralsTotal;
    private static int altLeaseDrainsTotal;
    private static int altPendingHandoffDrainsTotal;
    private static int altManeuverDrainsTotal;
    private static int altCompatibleAssignmentRefreshPreservedTotal;
    private static int altDeferredAxisRefreshPreservedTotal;
    private static int directDrainsTotal;
    private static int holdReleasePlansTotal;
    private static int holdReleaseDirectHoldsTotal;
    private static int freshReplanRequestsTotal;
    private static int directTerminalFreshEpochReplansTotal;
    private static int successfulFullAltCompletionRefreshRequestsTotal;
    private static int successfulFullAltCompletionRefreshCoalescedTotal;

    internal static void Tick(DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            Reset("not_raid_authority");
            return;
        }

        if (!TacticalReplanService.TryGetSnapshot(out var snapshot) || !snapshot.IsAvailable)
        {
            EmitSummaryIfDue(now);
            return;
        }

        RequestSuccessfulFullAltCompletionRefreshes(snapshot, now);

        foreach (var observation in snapshot.Plans
                     .Where(item => item.HardBreakRecommended || item.ReplanRecommended)
                     .OrderBy(item => item.OwnerProfileId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.ThreatId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.PlanEpoch))
        {
            // Execution deduplication is transition-scoped, not epoch-scoped. A PlanEpoch can
            // legitimately receive a later, distinct executable invalidation (for example an
            // assignment refresh followed by a grenade hard break). Suppress only the exact
            // owner/threat/epoch/state/reason transition so a new safety break in the same plan
            // epoch can still drain stale TC authority.
            string transitionKey = TransitionKey(observation);
            if (IsObservationOnlyTemporalReason(observation.Reason))
            {
                bool firstDeferred;
                lock (Sync)
                {
                    firstDeferred = DeferredTransitionKeys.Add(transitionKey);
                }
                if (firstDeferred)
                {
                    advisoryDeferralsTotal++;
                    VanguardClientDiagnosticsLog.Info(StatusTag,
                        $"TC_REPLAN_EXECUTION_DEFERRED owner={Safe(observation.OwnerProfileId)}; threat={Safe(observation.ThreatId)}; epoch={observation.PlanEpoch}; state={Safe(observation.CurrentState)}; reason={Safe(observation.Reason)}; classification=temporal_evidence_observation_debt; drainApplied=false; freshReplanRequested=false; movementMutation=false; sainMutation=false; failOpen=true");
                }
                continue;
            }

            if (IsObservationOnlySoftReplanReason(observation))
            {
                bool firstDeferred;
                lock (Sync)
                {
                    firstDeferred = DeferredTransitionKeys.Add(transitionKey);
                }
                if (firstDeferred)
                {
                    advisoryDeferralsTotal++;
                    VanguardClientDiagnosticsLog.Info(StatusTag,
                        $"TC_REPLAN_EXECUTION_DEFERRED owner={Safe(observation.OwnerProfileId)}; threat={Safe(observation.ThreatId)}; epoch={observation.PlanEpoch}; state={Safe(observation.CurrentState)}; reason={Safe(observation.Reason)}; classification=soft_replan_observation_debt; drainApplied=false; freshReplanRequested=false; movementMutation=false; sainMutation=false; failOpen=true");
                }
                continue;
            }

            if (!IsExecutableReason(observation))
            {
                continue;
            }

            lock (Sync)
            {
                if (!ConsumedTransitionKeys.Add(transitionKey))
                {
                    continue;
                }
            }

            bool assignmentIdentityChanged = Safe(observation.Reason).StartsWith("assignment_identity_changed", StringComparison.OrdinalIgnoreCase);
            string compatibleAltSummary = "not_assignment_identity_refresh";
            bool preserveCompatibleAlt = assignmentIdentityChanged
                && !observation.HardBreakRecommended
                && AltDevelopMovementService.CanPreserveCompatibleManeuverAcrossAssignmentRefresh(
                    observation.OwnerProfileId,
                    observation.ThreatId,
                    now,
                    out compatibleAltSummary);
            string deferredAxisSummary = "not_deferred_axis_refresh";
            bool preserveDeferredAxisAlt = assignmentIdentityChanged
                && !observation.HardBreakRecommended
                && !preserveCompatibleAlt
                && AltDevelopMovementService.CanPreserveDeferredAxisManeuverAcrossAssignmentRefresh(
                    observation.OwnerProfileId,
                    observation.ThreatId,
                    now,
                    out deferredAxisSummary);
            bool altLeaseDrained = false;
            bool pendingHandoffDrained = false;
            bool maneuverDrained = false;
            bool altDrained = false;
            if (!preserveCompatibleAlt)
            {
                if (!preserveDeferredAxisAlt)
                {
                    altDrained = AltDevelopMovementService.DrainForTacticalReplan(
                        observation.OwnerProfileId,
                        observation.ThreatId,
                        now,
                        observation.Reason,
                        out altLeaseDrained,
                        out pendingHandoffDrained,
                        out maneuverDrained);
                }
                else
                {
                    // Only the dormant maneuver identity survives. DirectCommit and support
                    // still drain normally; no lease, path or movement permission is restored.
                    altDeferredAxisRefreshPreservedTotal++;
                }
            }
            else
            {
                altCompatibleAssignmentRefreshPreservedTotal++;
            }
            bool directDrained = DirectCommitMovementService.DrainForTacticalReplan(
                observation.OwnerProfileId,
                observation.ThreatId,
                now,
                observation.Reason);
            int releasedPlans = ChokeCommitSupportService.ReleaseForTacticalReplan(
                observation.OwnerProfileId,
                observation.ThreatId,
                now,
                observation.Reason,
                out int releasedDirectHolds);
            bool upstreamAssignmentAlreadyFresh = assignmentIdentityChanged;
            bool freshReplanRequested = !upstreamAssignmentAlreadyFresh
                && RouteAssignmentService.RequestFreshTacticalReplan(
                    observation.OwnerProfileId,
                    observation.ThreatId,
                    observation.PlanEpoch,
                    now,
                    observation.Reason);

            executableInvalidationsTotal++;
            if (altLeaseDrained) altLeaseDrainsTotal++;
            if (pendingHandoffDrained) altPendingHandoffDrainsTotal++;
            if (maneuverDrained) altManeuverDrainsTotal++;
            if (directDrained) directDrainsTotal++;
            holdReleasePlansTotal += releasedPlans;
            holdReleaseDirectHoldsTotal += releasedDirectHolds;
            if (freshReplanRequested) freshReplanRequestsTotal++;
            if (Safe(observation.Reason).StartsWith("direct_commit_terminal_requires_fresh_epoch:", StringComparison.OrdinalIgnoreCase))
            {
                directTerminalFreshEpochReplansTotal++;
            }

            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_REPLAN_EXECUTED owner={Safe(observation.OwnerProfileId)}; threat={Safe(observation.ThreatId)}; axis={Safe(observation.AlternateCandidateId)}; altBot={Safe(observation.AlternateBotProfileId)}; epoch={observation.PlanEpoch}; state={Safe(observation.CurrentState)}; reason={Safe(observation.Reason)}; hardBreak={Bool(observation.HardBreakRecommended)}; replan={Bool(observation.ReplanRecommended)}; altDrained={Bool(altDrained)}; altLeaseDrained={Bool(altLeaseDrained)}; pendingHandoffDrained={Bool(pendingHandoffDrained)}; maneuverDrained={Bool(maneuverDrained)}; directDrained={Bool(directDrained)}; supportPlansReleased={releasedPlans}; supportDirectHoldsReleased={releasedDirectHolds}; upstreamAssignmentAlreadyFresh={Bool(upstreamAssignmentAlreadyFresh)}; altCompatibleAssignmentRefreshPreserved={Bool(preserveCompatibleAlt)}; altCompatibleSummary={Safe(compatibleAltSummary)}; altDeferredAxisRefreshPreserved={Bool(preserveDeferredAxisAlt)}; altDeferredAxisSummary={Safe(deferredAxisSummary)}; freshReplanRequested={Bool(freshReplanRequested)}; transitionDedup=owner_threat_epoch_state_reason; drainFirst=true; staleResume=false; movementStart=false; aimFireTargetMutation=false; sainMutation=false");
        }

        EmitSummaryIfDue(now);
    }

    internal static void Reset(string reason)
    {
        bool hadState;
        lock (Sync)
        {
            hadState = ConsumedTransitionKeys.Count > 0
                || DeferredTransitionKeys.Count > 0
                || ConsumedSuccessfulCompletionRefreshKeys.Count > 0;
            ConsumedTransitionKeys.Clear();
            DeferredTransitionKeys.Clear();
            ConsumedSuccessfulCompletionRefreshKeys.Clear();
        }
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        executableInvalidationsTotal = 0;
        advisoryDeferralsTotal = 0;
        altLeaseDrainsTotal = 0;
        altPendingHandoffDrainsTotal = 0;
        altManeuverDrainsTotal = 0;
        altCompatibleAssignmentRefreshPreservedTotal = 0;
        directDrainsTotal = 0;
        holdReleasePlansTotal = 0;
        holdReleaseDirectHoldsTotal = 0;
        freshReplanRequestsTotal = 0;
        directTerminalFreshEpochReplansTotal = 0;
        successfulFullAltCompletionRefreshRequestsTotal = 0;
        successfulFullAltCompletionRefreshCoalescedTotal = 0;

        if (hadState && !string.IsNullOrWhiteSpace(reason))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_REPLAN_EXECUTION_RESET reason={Safe(reason)}; drainOnly=true; freshPlanningOnly=true; movementStart=false; sainMutation=false");
        }
    }

    private static bool IsSuccessfulFullAltCompletionRefreshCandidate(
        PlanLifecycleObservation observation)
    {
        DirectCommitExecutionReceipt? receipt = observation.DirectCommitExecution;
        return observation.PlanEpoch > 0
            && observation.UpstreamPresent
            && !observation.HardBreakRecommended
            && !observation.ReplanRecommended
            && observation.DirectCommitExecutionCompletedObserved
            && receipt != null
            && receipt.PlanEpoch == observation.PlanEpoch
            && string.Equals(receipt.OwnerProfileId, observation.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(receipt.ThreatId, observation.ThreatId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(receipt.AlternateCandidateId, observation.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(receipt.AlternateBotProfileId, observation.AlternateBotProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(receipt.ReadinessMode, "full_alt", StringComparison.OrdinalIgnoreCase)
            && string.Equals(observation.CurrentState, "direct_commit_full_alt_completed", StringComparison.OrdinalIgnoreCase);
    }

    private static void RequestSuccessfulFullAltCompletionRefreshes(
        TacticalReplanSnapshot snapshot,
        DateTimeOffset now)
    {
        foreach (var observation in snapshot.Plans
                     .Where(IsSuccessfulFullAltCompletionRefreshCandidate)
                     .OrderBy(item => item.OwnerProfileId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.ThreatId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.PlanEpoch))
        {
            DirectCommitExecutionReceipt? receipt = observation.DirectCommitExecution;
            if (receipt == null)
            {
                continue;
            }

            string refreshKey = SuccessfulCompletionRefreshKey(observation, receipt);
            lock (Sync)
            {
                if (!ConsumedSuccessfulCompletionRefreshKeys.Add(refreshKey))
                {
                    continue;
                }
            }

            string refreshReason = "successful_full_alt_direct_commit_completion_refresh:" + Safe(receipt.MovementMode);
            bool freshReplanRequested = RouteAssignmentService.RequestFreshTacticalReplan(
                observation.OwnerProfileId,
                observation.ThreatId,
                observation.PlanEpoch,
                now,
                refreshReason);

            if (freshReplanRequested)
            {
                freshReplanRequestsTotal++;
                successfulFullAltCompletionRefreshRequestsTotal++;
            }
            else
            {
                successfulFullAltCompletionRefreshCoalescedTotal++;
            }

            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_REPLAN_SUCCESSFUL_FULLALT_REFRESH owner={Safe(observation.OwnerProfileId)}; threat={Safe(observation.ThreatId)}; axis={Safe(observation.AlternateCandidateId)}; altBot={Safe(observation.AlternateBotProfileId)}; epoch={observation.PlanEpoch}; directLease={Safe(receipt.LeaseId)}; directGeneration={receipt.Generation}; movementMode={Safe(receipt.MovementMode)}; freshReplanRequested={Bool(freshReplanRequested)}; coalescedWithPendingRequest={Bool(!freshReplanRequested)}; completionDedup=owner_threat_epoch_lease_generation_movement_mode; epochAdvanced=false; drainApplied=false; consumedPlanEpochPreserved=true; automaticDirectCommitRestart=false; routeFactsRefreshEnsured=true; movementStart=false; aimFireTargetMutation=false; sainMutation=false");
        }
    }

    private static string SuccessfulCompletionRefreshKey(
        PlanLifecycleObservation observation,
        DirectCommitExecutionReceipt receipt)
        => string.Join("|",
            Safe(observation.OwnerProfileId).ToLowerInvariant(),
            Safe(observation.ThreatId).ToLowerInvariant(),
            observation.PlanEpoch,
            Safe(receipt.LeaseId).ToLowerInvariant(),
            receipt.Generation,
            Safe(receipt.MovementMode).ToLowerInvariant());

    private static bool IsExecutableReason(PlanLifecycleObservation observation)
    {
        string reason = Safe(observation.Reason).ToLowerInvariant();
        if (observation.HardBreakRecommended)
        {
            return reason.StartsWith("hard_local_break:operator_dead_or_missing", StringComparison.Ordinal)
                || reason.StartsWith("hard_local_break:owner_identity_changed", StringComparison.Ordinal)
                || reason.StartsWith("hard_local_break:competing_relevant_threat_identity_changed", StringComparison.Ordinal)
                || reason.StartsWith("hard_local_break:competing_sain_goal_identity_changed", StringComparison.Ordinal)
                || reason.StartsWith("hard_local_break:grenade_hazard_active", StringComparison.Ordinal)
                || reason.StartsWith("hard_local_break:hard_outside_bubble_recovery_authority", StringComparison.Ordinal);
        }

        if (!observation.ReplanRecommended)
        {
            return false;
        }

        return reason.StartsWith("assignment_identity_changed", StringComparison.Ordinal)
            || reason.StartsWith("full_alt_establishment_regressed", StringComparison.Ordinal)
            || reason.StartsWith("meaningful_plan_upstream_missing", StringComparison.Ordinal)
            || reason.StartsWith("direct_commit_terminal_requires_fresh_epoch:", StringComparison.Ordinal);
    }

    private static bool IsObservationOnlySoftReplanReason(PlanLifecycleObservation observation)
    {
        if (!observation.ReplanRecommended)
        {
            return false;
        }
        string reason = Safe(observation.Reason).ToLowerInvariant();
        return reason.StartsWith("commit_readiness_regressed", StringComparison.Ordinal)
            || reason.StartsWith("direct_element_unavailable_after_full_alt", StringComparison.Ordinal)
            || reason.StartsWith("alternate_temporarily_unavailable", StringComparison.Ordinal);
    }

    private static bool IsObservationOnlyTemporalReason(string? reason)
    {
        string value = Safe(reason).ToLowerInvariant();
        return value.Contains("decision_snapshot_from_future", StringComparison.Ordinal)
            || value.Contains("decision_snapshot_stale", StringComparison.Ordinal)
            || value.Contains("decision_snapshot_missing_capture_time", StringComparison.Ordinal);
    }

    private static string TransitionKey(PlanLifecycleObservation observation)
        => string.Join("|",
            Safe(observation.OwnerProfileId).ToLowerInvariant(),
            Safe(observation.ThreatId).ToLowerInvariant(),
            observation.PlanEpoch,
            Safe(observation.CurrentState).ToLowerInvariant(),
            Safe(observation.Reason).ToLowerInvariant());

    private static void EmitSummaryIfDue(DateTimeOffset now)
    {
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                "TC_REPLAN_EXECUTION_BOOT enabled=true; mode=drain_then_fresh_shadow_replan_plus_successful_fullalt_refresh; executableReasons=identity+safety+establishment_regression+upstream_loss+direct_terminal_fresh_epoch; successfulFullAltCompletion=refresh_same_consumed_epoch_without_drain; softReplanReasons=observation_only; temporalFreshnessReasons=observation_only; executionDedup=owner_threat_epoch_state_reason; completionRefreshDedup=owner_threat_epoch_lease_generation_movement_mode; directMovementStart=false; altMovementStart=false; aimFireTargetMutation=false; sainMutation=false; failOpen=true");
        }
        if (now < nextSummaryAtUtc)
        {
            return;
        }
        nextSummaryAtUtc = now + SummaryInterval;
        VanguardClientDiagnosticsLog.DiagnosticSummary(StatusTag, "TC_REPLAN_EXECUTION_SUMMARY", () =>
            $"TC_REPLAN_EXECUTION_SUMMARY executableInvalidationsTotal={executableInvalidationsTotal}; advisoryDeferralsTotal={advisoryDeferralsTotal}; altLeaseDrainsTotal={altLeaseDrainsTotal}; altPendingHandoffDrainsTotal={altPendingHandoffDrainsTotal}; altManeuverDrainsTotal={altManeuverDrainsTotal}; altCompatibleAssignmentRefreshPreservedTotal={altCompatibleAssignmentRefreshPreservedTotal}; altDeferredAxisRefreshPreservedTotal={altDeferredAxisRefreshPreservedTotal}; directDrainsTotal={directDrainsTotal}; holdReleasePlansTotal={holdReleasePlansTotal}; holdReleaseDirectHoldsTotal={holdReleaseDirectHoldsTotal}; freshReplanRequestsTotal={freshReplanRequestsTotal}; directTerminalFreshEpochReplansTotal={directTerminalFreshEpochReplansTotal}; successfulFullAltCompletionRefreshRequestsTotal={successfulFullAltCompletionRefreshRequestsTotal}; successfulFullAltCompletionRefreshCoalescedTotal={successfulFullAltCompletionRefreshCoalescedTotal}; executionDedup=owner_threat_epoch_state_reason; completionRefreshDedup=owner_threat_epoch_lease_generation_movement_mode; drainOnly=true; successfulCompletionRefreshDrains=false; successfulCompletionRefreshAdvancesEpoch=false; freshPlanningOnly=true; staleResume=false; movementStart=false; sainMutation=false");
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private static string Bool(bool value) => value ? "true" : "false";
}
#endif
