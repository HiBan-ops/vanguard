#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Diagnostics;

// Responsibility: Implements tactical replan lifecycle truth over split-flank readiness and bounded DirectCommit execution receipts.
// Flow: Each owner/threat plan receives a monotonic epoch; assignment/safety/upstream invalidations plus non-success DirectCommit terminal outcomes are classified so the separate executor can drain stale authority and request fresh planning.
// Authority boundary: this classifier never mutates movement, scheduler windows, SAIN state, target identity, holds or DirectCommit. It only publishes lifecycle truth; TacticalReplanExecutionService owns the bounded drain + fresh-plan mutation.
// Invariant: successful DirectCommit completion is a terminal lifecycle fact for its exact phase, while interrupted/failed DirectCommit cannot silently strand the consumed phase in the same PlanEpoch; fresh physical retry requires a fresh epoch.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class TacticalReplanService
{
    public const string StatusTag = "VANGUARD_TACTICAL_REPLAN_STATUS";

    private static readonly TimeSpan MissingObservationCleanupDelay = TimeSpan.FromMilliseconds(750d);
    // A meaningful split-flank plan must not be declared stale before the same bounded upstream
    // continuity grace shared by RouteAssignment, SplitFlank readiness/state and AltDevelop expires.
    private static readonly TimeSpan MissingReplanDelay = RouteAssignmentService.PhysicalConsumerContinuityGraceDuration;
    private static readonly TimeSpan MissingRetention = TimeSpan.FromSeconds(4d);
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(15d);

    private static readonly object Sync = new();
    private static readonly Dictionary<string, PlanTracker> TrackerByThreatGroup = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> LastDetailSignatureByThreatGroup = new(StringComparer.OrdinalIgnoreCase);
    private static TacticalReplanSnapshot current = TacticalReplanSnapshot.Empty;
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;
    private static long planEpochSequence;
    private static int observedPlansTotal;
    private static int epochChangesTotal;
    private static int replanRecommendationsTotal;
    private static int hardBreakRecommendationsTotal;
    private static int missingUpstreamReplansTotal;
    private static int commitReadyObservedTotal;
    private static int directExecutionActiveObservedTotal;
    private static int directExecutionCompletedObservedTotal;
    private static int directExecutionFreshEpochReplansTotal;

    internal static void Observe(SplitFlankStateSnapshot stateSnapshot, DateTimeOffset now)
    {
        var observations = new List<PlanLifecycleObservation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int epochChanges = 0;
        int replans = 0;
        int hardBreaks = 0;
        int commitReady = 0;
        int directExecutionObserved = 0;
        int directExecutionTerminal = 0;
        int directExecutionActive = 0;
        int directExecutionCompleted = 0;
        int directExecutionFreshEpochReplans = 0;
        DirectCommitMovementService.TryGetExecutionSnapshot(out var directExecutionSnapshot);

        foreach (var group in stateSnapshot.ThreatGroups)
        {
            string key = ThreatGroupKey(group.OwnerProfileId, group.ThreatId);
            seen.Add(key);
            string assignmentIdentity = AssignmentIdentity(group);
            PlanTracker tracker;
            bool assignmentIdentityChanged = false;
            lock (Sync)
            {
                if (!TrackerByThreatGroup.TryGetValue(key, out tracker!))
                {
                    tracker = new PlanTracker(group.OwnerProfileId, group.ThreatId, assignmentIdentity, NextPlanEpochLocked(), now);
                    TrackerByThreatGroup[key] = tracker;
                }
                else if (!string.Equals(tracker.AssignmentIdentity, assignmentIdentity, StringComparison.OrdinalIgnoreCase))
                {
                    tracker.PlanEpoch = NextPlanEpochLocked();
                    tracker.AssignmentIdentity = assignmentIdentity;
                    tracker.LastMeaningfulState = "observing";
                    tracker.LastStateChangedAtUtc = now;
                    assignmentIdentityChanged = true;
                    epochChanges++;
                }
                tracker.LastSeenAtUtc = now;
                tracker.MissingSinceUtc = DateTimeOffset.MinValue;
                tracker.AlternateCandidateId = group.AlternateCandidateId;
                tracker.AlternateBotProfileId = group.AlternateBotProfileId;
            }

            long observedPlanEpoch = tracker.PlanEpoch;
            DirectCommitExecutionReceipt? approachExecution = FindDirectCommitExecution(
                directExecutionSnapshot,
                group.OwnerProfileId,
                group.ThreatId,
                group.AlternateCandidateId,
                group.AlternateBotProfileId,
                observedPlanEpoch,
                "approach");
            DirectCommitExecutionReceipt? fullAltExecution = FindDirectCommitExecution(
                directExecutionSnapshot,
                group.OwnerProfileId,
                group.ThreatId,
                group.AlternateCandidateId,
                group.AlternateBotProfileId,
                observedPlanEpoch,
                "full_alt");
            DirectCommitExecutionReceipt? directExecution = SelectLifecycleExecution(group, approachExecution, fullAltExecution);
            DirectCommitExecutionReceipt? terminalFreshEpochExecution = SelectFreshEpochRequiredExecution(approachExecution, fullAltExecution);
            bool successfulFullAltCommit = fullAltExecution?.IsCompleted == true;
            bool terminalFreshEpochReplan = false;

            string baseState = ResolveBaseState(group, directExecution, successfulFullAltCommit);
            bool hardBreak = !group.AlternateHardSafetyReady && IsHardBreakReason(group.AlternateHardSafetyReason);
            bool replan = group.AssignmentChanged || assignmentIdentityChanged;
            string reason = "state_continues";

            if (hardBreak)
            {
                reason = "hard_local_break:" + Safe(group.AlternateHardSafetyReason);
            }
            else if (group.AssignmentChanged || assignmentIdentityChanged)
            {
                replan = true;
                reason = "assignment_identity_changed";
            }
            else if (terminalFreshEpochExecution != null)
            {
                replan = true;
                terminalFreshEpochReplan = true;
                directExecution = terminalFreshEpochExecution;
                reason = "direct_commit_terminal_requires_fresh_epoch:"
                    + Safe(terminalFreshEpochExecution.ReadinessMode) + ":"
                    + Safe(terminalFreshEpochExecution.State) + ":"
                    + Safe(terminalFreshEpochExecution.Reason);
                directExecutionFreshEpochReplans++;
            }
            else if (!successfulFullAltCommit
                && StateRank(tracker.LastMeaningfulState) >= StateRank("full_alt_established")
                && !group.FullAltEstablished)
            {
                replan = true;
                reason = "full_alt_establishment_regressed:" + Safe(group.Reason);
            }
            else if (!successfulFullAltCommit
                && StateRank(tracker.LastMeaningfulState) >= StateRank("commit_ready")
                && !group.SynchronizedCommitReady)
            {
                replan = true;
                reason = "commit_readiness_regressed:" + Safe(group.Reason);
            }
            else if (!successfulFullAltCommit
                && group.FullAltEstablished
                && group.DirectMemberCount > 0
                && group.DirectCommitReadyCount == 0)
            {
                replan = true;
                reason = "direct_element_unavailable_after_full_alt:" + Safe(string.Join(",", group.DirectCommitBlockingReasons));
            }
            else if (!group.AlternateHardSafetyReady && StateRank(tracker.LastMeaningfulState) >= StateRank("position_established"))
            {
                replan = true;
                reason = "alternate_temporarily_unavailable:" + Safe(group.AlternateHardSafetyReason);
            }

            string state = hardBreak
                ? "break_required"
                : replan
                    ? "replan_recommended"
                    : baseState;

            string previousState = tracker.LastState;
            bool changed = !string.Equals(previousState, state, StringComparison.OrdinalIgnoreCase);
            if (terminalFreshEpochReplan)
            {
                // A non-success DirectCommit terminal receipt has consumed this exact phase in the observed
                // epoch. Always advance the plan epoch before publishing the recovery observation, even when
                // the tracker was already in an invalidation state for another reason. Otherwise the fresh
                // route computation could remain bound to an epoch whose DirectCommit phase is already spent.
                lock (Sync)
                {
                    tracker.PlanEpoch = NextPlanEpochLocked();
                }
                epochChanges++;
            }
            else if (changed
                && (hardBreak || replan)
                && !assignmentIdentityChanged
                && !IsInvalidationState(previousState))
            {
                lock (Sync)
                {
                    tracker.PlanEpoch = NextPlanEpochLocked();
                }
                epochChanges++;
            }
            if (changed || terminalFreshEpochReplan)
            {
                tracker.LastState = state;
                tracker.LastStateChangedAtUtc = now;
            }
            if (!hardBreak && !replan && StateRank(baseState) > StateRank(tracker.LastMeaningfulState))
            {
                tracker.LastMeaningfulState = baseState;
            }

            if (hardBreak) hardBreaks++;
            if (replan) replans++;
            if (group.DirectCommitPermissionObserved) commitReady++;
            if (directExecution != null) directExecutionObserved++;
            if (directExecution?.IsTerminal == true) directExecutionTerminal++;
            if (directExecution?.IsActive == true) directExecutionActive++;
            if (directExecution?.IsCompleted == true) directExecutionCompleted++;

            var observation = new PlanLifecycleObservation(
                group.OwnerProfileId,
                group.ThreatId,
                group.AlternateCandidateId,
                group.AlternateBotProfileId,
                tracker.PlanEpoch,
                state,
                previousState,
                changed,
                hardBreak,
                replan,
                group.DirectCommitPermissionObserved,
                group.SynchronizedHoldReleaseApplied,
                group.FullAltEstablished,
                directExecution,
                true,
                0f,
                reason);
            observations.Add(observation);
            EmitDetailIfChanged(observation, key);
        }

        PlanTracker[] missing;
        lock (Sync)
        {
            missing = TrackerByThreatGroup
                .Where(pair => !seen.Contains(pair.Key))
                .Select(pair => pair.Value)
                .ToArray();
        }

        foreach (var tracker in missing)
        {
            if (tracker.MissingSinceUtc == DateTimeOffset.MinValue)
            {
                tracker.MissingSinceUtc = now;
            }
            float missingSeconds = (float)Math.Max(0d, (now - tracker.MissingSinceUtc).TotalSeconds);
            bool meaningful = StateRank(tracker.LastMeaningfulState) >= StateRank("position_established");
            if (!meaningful && now - tracker.LastSeenAtUtc > MissingObservationCleanupDelay)
            {
                RemoveTracker(tracker);
                continue;
            }
            if (now - tracker.LastSeenAtUtc > MissingRetention)
            {
                RemoveTracker(tracker);
                continue;
            }

            bool replan = meaningful && now - tracker.MissingSinceUtc >= MissingReplanDelay;
            string state = replan ? "replan_recommended" : "upstream_gap_grace";
            string previousState = tracker.LastState;
            bool changed = !string.Equals(previousState, state, StringComparison.OrdinalIgnoreCase);
            if (changed)
            {
                if (replan && !IsInvalidationState(previousState))
                {
                    lock (Sync)
                    {
                        tracker.PlanEpoch = NextPlanEpochLocked();
                    }
                    epochChanges++;
                }
                tracker.LastState = state;
                tracker.LastStateChangedAtUtc = now;
            }
            if (replan)
            {
                replans++;
                missingUpstreamReplansTotal++;
            }

            DirectCommitExecutionReceipt? missingApproachExecution = FindDirectCommitExecution(
                directExecutionSnapshot,
                tracker.OwnerProfileId,
                tracker.ThreatId,
                tracker.AlternateCandidateId,
                tracker.AlternateBotProfileId,
                tracker.PlanEpoch,
                "approach");
            DirectCommitExecutionReceipt? missingFullAltExecution = FindDirectCommitExecution(
                directExecutionSnapshot,
                tracker.OwnerProfileId,
                tracker.ThreatId,
                tracker.AlternateCandidateId,
                tracker.AlternateBotProfileId,
                tracker.PlanEpoch,
                "full_alt");
            DirectCommitExecutionReceipt? directExecution = missingFullAltExecution ?? missingApproachExecution;
            if (directExecution != null) directExecutionObserved++;
            if (directExecution?.IsTerminal == true) directExecutionTerminal++;

            var observation = new PlanLifecycleObservation(
                tracker.OwnerProfileId,
                tracker.ThreatId,
                tracker.AlternateCandidateId,
                tracker.AlternateBotProfileId,
                tracker.PlanEpoch,
                state,
                previousState,
                changed,
                false,
                replan,
                StateRank(tracker.LastMeaningfulState) >= StateRank("commit_ready"),
                StateRank(tracker.LastMeaningfulState) >= StateRank("commit_released"),
                StateRank(tracker.LastMeaningfulState) >= StateRank("full_alt_established"),
                directExecution,
                false,
                missingSeconds,
                replan ? "meaningful_plan_upstream_missing" : "bounded_upstream_gap_grace");
            observations.Add(observation);
            EmitDetailIfChanged(observation, ThreatGroupKey(tracker.OwnerProfileId, tracker.ThreatId));
        }

        var next = new TacticalReplanSnapshot(now, observations.ToArray());
        lock (Sync)
        {
            current = next;
        }

        observedPlansTotal += observations.Count;
        epochChangesTotal += epochChanges;
        replanRecommendationsTotal += replans;
        hardBreakRecommendationsTotal += hardBreaks;
        commitReadyObservedTotal += commitReady;
        directExecutionActiveObservedTotal += directExecutionActive;
        directExecutionCompletedObservedTotal += directExecutionCompleted;
        directExecutionFreshEpochReplansTotal += directExecutionFreshEpochReplans;

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_REPLAN_BOOT enabled=true; mode=direct_commit_lifecycle_closure; missingReplanMs={MissingReplanDelay.TotalMilliseconds:0}; missingObservationCleanupMs={MissingObservationCleanupDelay.TotalMilliseconds:0}; missingRetentionSeconds={MissingRetention.TotalSeconds:0.0}; planEpoch=true; assignmentIdentityEpoch=true; hardBreakClassification=true; replanClassification=true; executionAllowed=false; schedulerMutation=false; movementMutation=false; sainMutation=false; directCommitExecutionLifecycle=true");
        }

        if (now >= nextSummaryAtUtc)
        {
            nextSummaryAtUtc = now + SummaryInterval;
            VanguardClientDiagnosticsLog.DiagnosticSummary(StatusTag, "TC_REPLAN_SUMMARY", () =>
                $"TC_REPLAN_SUMMARY plans={observations.Count}; epochChanges={epochChanges}; replans={replans}; hardBreaks={hardBreaks}; commitReady={commitReady}; observedPlansTotal={observedPlansTotal}; epochChangesTotal={epochChangesTotal}; replanRecommendationsTotal={replanRecommendationsTotal}; hardBreakRecommendationsTotal={hardBreakRecommendationsTotal}; missingUpstreamReplansTotal={missingUpstreamReplansTotal}; commitReadyObservedTotal={commitReadyObservedTotal}; directCommitExecutionObserved={directExecutionObserved}; directCommitExecutionTerminal={directExecutionTerminal}; directCommitExecutionActive={directExecutionActive}; directCommitExecutionCompleted={directExecutionCompleted}; directCommitFreshEpochReplans={directExecutionFreshEpochReplans}; directCommitExecutionActiveObservedTotal={directExecutionActiveObservedTotal}; directCommitExecutionCompletedObservedTotal={directExecutionCompletedObservedTotal}; directCommitExecutionFreshEpochReplansTotal={directExecutionFreshEpochReplansTotal}; advisoryOnly=true; executionAllowed=false; mutation=false");
        }
    }

    public static bool TryGetSnapshot(out TacticalReplanSnapshot snapshot)
    {
        lock (Sync)
        {
            snapshot = current;
            return snapshot.IsAvailable;
        }
    }

    public static void Reset(string reason)
    {
        bool hadState;
        lock (Sync)
        {
            hadState = current.IsAvailable || TrackerByThreatGroup.Count > 0;
            current = TacticalReplanSnapshot.Empty;
            TrackerByThreatGroup.Clear();
            LastDetailSignatureByThreatGroup.Clear();
            nextSummaryAtUtc = DateTimeOffset.MinValue;
        }
        bootLogged = false;
        observedPlansTotal = 0;
        epochChangesTotal = 0;
        replanRecommendationsTotal = 0;
        hardBreakRecommendationsTotal = 0;
        missingUpstreamReplansTotal = 0;
        commitReadyObservedTotal = 0;
        directExecutionActiveObservedTotal = 0;
        directExecutionCompletedObservedTotal = 0;
        directExecutionFreshEpochReplansTotal = 0;
        if (hadState && !string.IsNullOrWhiteSpace(reason))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_REPLAN_RESET reason={Safe(reason)}; advisoryOnly=true; executionAllowed=false; mutation=false");
        }
    }

    private static long NextPlanEpochLocked()
    {
        planEpochSequence++;
        if (planEpochSequence <= 0)
        {
            planEpochSequence = 1;
        }
        return planEpochSequence;
    }

    private static string ResolveBaseState(
        ThreatGroupSplitFlankStateObservation group,
        DirectCommitExecutionReceipt? directExecution,
        bool successfulFullAltCommit)
    {
        if (directExecution?.IsActive == true)
        {
            return string.Equals(directExecution.ReadinessMode, "full_alt", StringComparison.OrdinalIgnoreCase)
                ? "direct_commit_full_alt_active"
                : "direct_commit_approach_active";
        }
        if (successfulFullAltCommit) return "direct_commit_full_alt_completed";
        if (directExecution?.IsCompleted == true
            && string.Equals(directExecution.ReadinessMode, "approach", StringComparison.OrdinalIgnoreCase)
            && !group.SynchronizedCommitReady)
        {
            return "direct_commit_approach_completed";
        }
        if (group.SynchronizedCommitReady && group.SynchronizedHoldReleaseApplied) return "commit_released";
        if (group.SynchronizedCommitReady) return "commit_ready";
        if (group.SynchronizedApproachCommitReady && group.SynchronizedHoldReleaseApplied) return "approach_commit_released";
        if (group.SynchronizedApproachCommitReady) return "approach_commit_ready";
        if (group.FullAltEstablished) return "full_alt_established";
        if (group.PositionEstablishedObserved) return "position_established";
        if (group.AlternateDevelopmentObserved) return "alt_developing";
        return "observing";
    }

    private static bool IsHardBreakReason(string? reason)
    {
        string value = Safe(reason);
        return value.StartsWith("operator_dead_or_missing", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("owner_identity_changed", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("competing_relevant_threat_identity_changed", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("competing_sain_goal_identity_changed", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("grenade_hazard_active", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("hard_outside_bubble_recovery_authority", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("decision_snapshot_stale", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("decision_snapshot_from_future", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("decision_snapshot_missing_capture_time", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInvalidationState(string? state)
    {
        string value = Safe(state).ToLowerInvariant();
        return value == "break_required" || value == "replan_recommended";
    }

    private static int StateRank(string? state)
    {
        return Safe(state).ToLowerInvariant() switch
        {
            "direct_commit_full_alt_completed" => 12,
            "direct_commit_full_alt_active" => 11,
            "commit_released" => 10,
            "commit_ready" => 9,
            "full_alt_established" => 8,
            "position_established" => 7,
            "direct_commit_approach_completed" => 6,
            "direct_commit_approach_active" => 5,
            "approach_commit_released" => 4,
            "approach_commit_ready" => 3,
            "alt_developing" => 2,
            "observing" => 1,
            _ => 0
        };
    }

    private static DirectCommitExecutionReceipt? FindDirectCommitExecution(
        DirectCommitExecutionSnapshot snapshot,
        string ownerProfileId,
        string threatId,
        string alternateCandidateId,
        string alternateBotProfileId,
        long planEpoch,
        string readinessMode)
    {
        if (!snapshot.IsAvailable || planEpoch <= 0)
        {
            return null;
        }
        return snapshot.Executions
            .Where(item => item.PlanEpoch == planEpoch
                && string.Equals(item.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ThreatId, threatId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.AlternateCandidateId, alternateCandidateId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.AlternateBotProfileId, alternateBotProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ReadinessMode, readinessMode, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.ObservedAtUtc)
            .FirstOrDefault();
    }

    private static DirectCommitExecutionReceipt? SelectLifecycleExecution(
        ThreatGroupSplitFlankStateObservation group,
        DirectCommitExecutionReceipt? approachExecution,
        DirectCommitExecutionReceipt? fullAltExecution)
    {
        if (fullAltExecution?.IsActive == true) return fullAltExecution;
        if (approachExecution?.IsActive == true) return approachExecution;
        if (fullAltExecution?.IsCompleted == true) return fullAltExecution;
        if (group.SynchronizedCommitReady) return fullAltExecution;
        return approachExecution ?? fullAltExecution;
    }

    private static DirectCommitExecutionReceipt? SelectFreshEpochRequiredExecution(
        DirectCommitExecutionReceipt? approachExecution,
        DirectCommitExecutionReceipt? fullAltExecution)
    {
        return new[] { approachExecution, fullAltExecution }
            .Where(item => item?.RequiresFreshPlanEpoch == true)
            .OrderByDescending(item => item!.ObservedAtUtc)
            .FirstOrDefault();
    }

    private static void RemoveTracker(PlanTracker tracker)
    {
        string key = ThreatGroupKey(tracker.OwnerProfileId, tracker.ThreatId);
        lock (Sync)
        {
            TrackerByThreatGroup.Remove(key);
            LastDetailSignatureByThreatGroup.Remove(key);
        }
    }

    private static void EmitDetailIfChanged(PlanLifecycleObservation observation, string key)
    {
        string signature = string.Join("|",
            observation.PlanEpoch,
            observation.CurrentState,
            observation.HardBreakRecommended,
            observation.ReplanRecommended,
            observation.UpstreamPresent,
            observation.CommitReadyObserved,
            observation.SynchronizedHoldReleaseObserved,
            observation.FullAltEstablishedObserved,
            observation.DirectCommitExecutionObserved,
            observation.DirectCommitExecutionTerminalObserved,
            observation.DirectCommitExecutionCompletedObserved,
            observation.DirectCommitExecutionRequiresFreshPlanEpoch,
            observation.DirectCommitExecution?.PlanEpoch ?? 0L,
            Safe(observation.DirectCommitExecution?.ReadinessMode),
            Safe(observation.DirectCommitExecution?.State),
            Safe(observation.DirectCommitExecution?.Reason),
            Safe(observation.Reason));
        lock (Sync)
        {
            if (LastDetailSignatureByThreatGroup.TryGetValue(key, out string last)
                && string.Equals(last, signature, StringComparison.Ordinal))
            {
                return;
            }
            LastDetailSignatureByThreatGroup[key] = signature;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_REPLAN_PLAN_STATE owner={Safe(observation.OwnerProfileId)}; threat={Safe(observation.ThreatId)}; axis={Safe(observation.AlternateCandidateId)}; altBot={Safe(observation.AlternateBotProfileId)}; epoch={observation.PlanEpoch}; previous={Safe(observation.PreviousState)}; current={Safe(observation.CurrentState)}; changed={observation.StateChanged}; hardBreak={observation.HardBreakRecommended}; replan={observation.ReplanRecommended}; commitReadyObserved={observation.CommitReadyObserved}; synchronizedHoldReleaseObserved={observation.SynchronizedHoldReleaseObserved}; fullAltEstablishedObserved={observation.FullAltEstablishedObserved}; directCommitExecutionObserved={observation.DirectCommitExecutionObserved}; directCommitExecutionTerminal={observation.DirectCommitExecutionTerminalObserved}; directCommitExecutionCompleted={observation.DirectCommitExecutionCompletedObserved}; directCommitFreshEpochRequired={observation.DirectCommitExecutionRequiresFreshPlanEpoch}; directCommitExecutionEpoch={observation.DirectCommitExecution?.PlanEpoch ?? 0L}; directCommitPhase={Safe(observation.DirectCommitExecution?.ReadinessMode)}; directCommitMovementMode={Safe(observation.DirectCommitExecution?.MovementMode)}; directCommitState={Safe(observation.DirectCommitExecution?.State)}; directCommitReason={Safe(observation.DirectCommitExecution?.Reason)}; directCommitPhysicalMeters={Number(observation.DirectCommitExecution?.PhysicalMeters ?? 0f)}; directCommitNativeTicks={observation.DirectCommitExecution?.NativeTicks ?? 0}; upstreamPresent={observation.UpstreamPresent}; upstreamMissingSeconds={Number(observation.UpstreamMissingSeconds)}; reason={Safe(observation.Reason)}; advisoryOnly=true; executionAllowed=false; mutation=false");
    }

    private static string ThreatGroupKey(string ownerProfileId, string threatId)
        => Safe(ownerProfileId).ToLowerInvariant() + "|" + Safe(threatId).ToLowerInvariant();

    private static string AssignmentIdentity(ThreatGroupSplitFlankStateObservation group)
        => string.Join("|", Safe(group.AlternateCandidateId).ToLowerInvariant(), Safe(group.AlternateBotProfileId).ToLowerInvariant());

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    private static string Number(float value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class PlanTracker
    {
        public PlanTracker(string ownerProfileId, string threatId, string assignmentIdentity, long planEpoch, DateTimeOffset now)
        {
            OwnerProfileId = ownerProfileId;
            ThreatId = threatId;
            AssignmentIdentity = assignmentIdentity;
            PlanEpoch = planEpoch;
            LastSeenAtUtc = now;
            LastStateChangedAtUtc = now;
        }

        public string OwnerProfileId { get; }
        public string ThreatId { get; }
        public string AlternateCandidateId { get; set; } = "none";
        public string AlternateBotProfileId { get; set; } = "none";
        public string AssignmentIdentity { get; set; }
        public long PlanEpoch { get; set; }
        public string LastState { get; set; } = "observing";
        public string LastMeaningfulState { get; set; } = "observing";
        public DateTimeOffset LastSeenAtUtc { get; set; }
        public DateTimeOffset MissingSinceUtc { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastStateChangedAtUtc { get; set; }
    }
}
#endif
