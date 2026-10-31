#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;

// Responsibility: Implements synchronized split-flank readiness as a non-authoritative state layer above spatial establishment.
// Flow: Split-flank position truth is fused with route assignment physical AltDevelop provenance, hard local-safety gates and Direct-element readiness. Strict FullAlt still requires establishment plus same-threat pressure and hard safety; a sub-second bridge lets pressure observed immediately before stable arrival participate in that same tactical event, while a separate near-commit bridge can expose one bounded Direct permission before D5 when the alternate is already within one DirectCommit slice of its region. A successful strict DirectCommit receipt for the exact current PlanEpoch consumes synchronized readiness and ends repeated hold-release suppression for that cycle.
// Authority boundary: This service owns no locomotion, fire, suppression, SAIN target/decision mutation or DirectCommit execution. Its only bounded mutation is releasing an already-existing commit support Vanguard StageHold/DirectHold for the same owner/threat after synchronized readiness is stable; SAIN then resumes naturally.
// Invariant: Position alone never becomes FullAltEstablished; current or sub-second recent pressure/engagement evidence for the same assignment/threat plus hard-safety readiness are mandatory. A terminal receipt can close only the exact current assignment/PlanEpoch and cannot be reused by a later epoch; every future executor must revalidate local authority instead of trusting this snapshot as a lease.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class SplitFlankStateService
{
    public const string StatusTag = "VANGUARD_SPLIT_FLANK_STATE_STATUS";

    private static readonly TimeSpan FullAltStableDwell = TimeSpan.FromMilliseconds(250d);
    private static readonly TimeSpan SynchronizedCommitStableDwell = TimeSpan.FromMilliseconds(250d);
    private static readonly TimeSpan SynchronizedApproachCommitStableDwell = TimeSpan.FromMilliseconds(250d);
    private static readonly TimeSpan PressureEstablishmentBridge = TimeSpan.FromMilliseconds(750d);
    private static readonly TimeSpan PressureGrace = TimeSpan.FromSeconds(1.5d);
    private static readonly TimeSpan TrackerRetention = TimeSpan.FromSeconds(10d);
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(15d);
    private const int StableSampleCount = 2;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, ReadinessTracker> TrackerByAssignment = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> LastDetailSignatureByAssignment = new(StringComparer.OrdinalIgnoreCase);
    private static SplitFlankStateSnapshot current = SplitFlankStateSnapshot.Empty;
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;
    private static int inputGroupsTotal;
    private static int developmentObservedTotal;
    private static int developmentCompletedTotal;
    private static int pressureProofTotal;
    private static int flankSupportPressureProofTotal;
    private static int fullAltEstablishedTotal;
    private static int fullAltPressureBridgeTotal;
    private static int fullAltGraceRetainedTotal;
    private static int synchronizedCommitReadyTotal;
    private static int nearCommitObservedTotal;
    private static int synchronizedApproachCommitReadyTotal;
    private static int directReadyMemberTotal;
    private static int directBlockedMemberTotal;
    private static int synchronizedHoldReleasePlansTotal;
    private static int synchronizedHoldReleaseDirectHoldsTotal;
    private static int successfulDirectCommitCycleCompletionsTotal;
    private static int terminalFinalHoldReleasePlansTotal;
    private static int terminalFinalHoldReleaseDirectHoldsTotal;

    internal static void Observe(
        SplitFlankReadinessSnapshot readinessSnapshot,
        RouteAssignmentSnapshot assignmentSnapshot,
        DateTimeOffset now)
    {
        Prune(now);

        var observations = new List<ThreatGroupSplitFlankStateObservation>();
        int developmentObserved = 0;
        int developmentCompleted = 0;
        int pressureProof = 0;
        int flankSupportPressureProof = 0;
        int fullAltEstablished = 0;
        int pressureBridged = 0;
        int graceRetained = 0;
        int synchronizedReady = 0;
        int nearCommitObserved = 0;
        int synchronizedApproachReady = 0;
        int directReadyMembers = 0;
        int directBlockedMembers = 0;
        int synchronizedHoldReleasePlans = 0;
        int synchronizedHoldReleaseDirectHolds = 0;
        int successfulDirectCommitCycleCompletions = 0;
        int terminalFinalHoldReleasePlans = 0;
        int terminalFinalHoldReleaseDirectHolds = 0;

        foreach (var readinessGroup in readinessSnapshot.ThreatGroups)
        {
            var assignment = assignmentSnapshot.ThreatGroups.FirstOrDefault(item =>
                string.Equals(item.OwnerProfileId, readinessGroup.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ThreatId, readinessGroup.ThreatId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.AlternateCandidateId, readinessGroup.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.SelectedAltBotProfileId, readinessGroup.AlternateBotProfileId, StringComparison.OrdinalIgnoreCase));
            if (assignment == null)
            {
                continue;
            }

            string key = AssignmentKey(
                readinessGroup.OwnerProfileId,
                readinessGroup.ThreatId,
                readinessGroup.AlternateCandidateId,
                readinessGroup.AlternateBotProfileId,
                assignment.SelectedAltSupportBotProfileId);
            var tracker = GetOrCreateTracker(key, now);
            tracker.LastObservedAtUtc = now;

            bool hasDevelopmentEvidence = AltDevelopMovementService.TryGetDevelopmentEvidence(
                assignment,
                now,
                out var developmentEvidence);
            bool developmentSeen = hasDevelopmentEvidence && developmentEvidence.DevelopmentObserved;
            bool developmentDone = hasDevelopmentEvidence && developmentEvidence.IsCompleted;
            bool physicalProgressObserved = hasDevelopmentEvidence && developmentEvidence.PhysicalProgressObserved;
            string physicalProgressKind = hasDevelopmentEvidence ? developmentEvidence.PhysicalProgressKind : "none";
            float closestRegionDistanceMeters = hasDevelopmentEvidence ? developmentEvidence.ClosestRegionDistanceMeters : float.PositiveInfinity;
            string developmentState = hasDevelopmentEvidence ? developmentEvidence.State : "none";
            string developmentReason = hasDevelopmentEvidence ? developmentEvidence.Reason : "no_physical_development_evidence";
            if (developmentSeen) developmentObserved++;
            if (developmentDone) developmentCompleted++;

            bool altSnapshotAvailable = VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(readinessGroup.AlternateBotProfileId, out var altDecision);
            string altSafetyReason;
            if (!altSnapshotAvailable)
            {
                altSafetyReason = "alternate_decision_snapshot_missing";
            }
            else if (!RouteAssignmentService.TryResolveMemberEvidenceCapturedAtUtc(
                         assignment,
                         readinessGroup.AlternateBotProfileId,
                         out var altEvidenceCapturedAtUtc))
            {
                altSafetyReason = "member_evidence_generation_missing";
            }
            else
            {
                altSafetyReason = SplitFlankSafetyPolicy.Evaluate(
                    altDecision,
                    readinessGroup.OwnerProfileId,
                    readinessGroup.ThreatId,
                    altEvidenceCapturedAtUtc,
                    now);
            }
            bool altSafetyReady = altSafetyReason.Length == 0;
            if (altSafetyReady) altSafetyReason = "ok";

            string pressureReason = "alternate_decision_snapshot_missing";
            bool altPressureSnapshotFresh = altSnapshotAvailable
                && VanguardOperatorDecisionSnapshotService.IsFresh(altDecision, now, out _);
            bool pressureObserved = altPressureSnapshotFresh
                && TryObservePressureProof(altDecision, readinessGroup.ThreatId, out pressureReason);
            DateTimeOffset pressureEvidenceAtUtc = pressureObserved ? altDecision.CapturedAtUtc : DateTimeOffset.MinValue;
            if (altSnapshotAvailable && !altPressureSnapshotFresh)
            {
                pressureReason = "alternate_pressure_snapshot_not_fresh";
            }
            if (!pressureObserved
                && TryObserveEstablishedFlankSupportPressureProof(
                    assignment,
                    readinessGroup.ThreatId,
                    now,
                    out DateTimeOffset supportPressureAtUtc,
                    out string supportPressureReason))
            {
                pressureObserved = true;
                pressureEvidenceAtUtc = supportPressureAtUtc;
                pressureReason = "flank_support_pressure:" + supportPressureReason;
                flankSupportPressureProof++;
            }
            if (pressureObserved)
            {
                if (pressureEvidenceAtUtc > tracker.LastPressureAtUtc)
                {
                    tracker.LastPressureAtUtc = pressureEvidenceAtUtc;
                }
                pressureProof++;
            }

            bool recentPressureForEstablishment = tracker.LastPressureAtUtc != DateTimeOffset.MinValue
                && now - tracker.LastPressureAtUtc <= PressureEstablishmentBridge;
            bool pressureEligibleForEstablishment = pressureObserved || recentPressureForEstablishment;
            bool establishedUsingRecentPressureBridge = readinessGroup.AlternatePositionEstablishedObserved
                && altSafetyReady
                && !pressureObserved
                && recentPressureForEstablishment;
            bool rawFullAlt = readinessGroup.AlternatePositionEstablishedObserved && altSafetyReady && pressureEligibleForEstablishment;
            DateTimeOffset fullAltEvidenceAtUtc = altSnapshotAvailable ? altDecision.CapturedAtUtc : DateTimeOffset.MinValue;
            if (pressureEvidenceAtUtc > fullAltEvidenceAtUtc)
            {
                fullAltEvidenceAtUtc = pressureEvidenceAtUtc;
            }
            AdvanceStableGate(rawFullAlt, now, fullAltEvidenceAtUtc, FullAltStableDwell, tracker.FullAltGate);
            bool fullAlt = tracker.FullAltGate.Stable;
            bool retainedByGrace = false;
            if (!rawFullAlt
                && tracker.FullAltGate.WasEverStable
                && readinessGroup.AlternatePositionEstablishedObserved
                && altSafetyReady
                && tracker.LastPressureAtUtc != DateTimeOffset.MinValue
                && now - tracker.LastPressureAtUtc <= PressureGrace)
            {
                fullAlt = true;
                retainedByGrace = true;
            }
            else if (!readinessGroup.AlternatePositionEstablishedObserved || !altSafetyReady)
            {
                tracker.FullAltGate.ClearStableHistory();
                fullAlt = false;
            }

            if (fullAlt) fullAltEstablished++;
            if (establishedUsingRecentPressureBridge && fullAlt) pressureBridged++;
            if (retainedByGrace) graceRetained++;

            int directReady = 0;
            int directBlocked = 0;
            DateTimeOffset newestDirectReadyEvidenceAtUtc = DateTimeOffset.MinValue;
            var directBlockingReasons = new List<string>();
            foreach (string directBotProfileId in assignment.DirectBotProfileIds)
            {
                if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(directBotProfileId, out var directDecision))
                {
                    directBlocked++;
                    directBlockingReasons.Add("decision_snapshot_missing");
                    continue;
                }

                string directReason;
                if (!RouteAssignmentService.TryResolveMemberEvidenceCapturedAtUtc(
                        assignment,
                        directBotProfileId,
                        out var directEvidenceCapturedAtUtc))
                {
                    directReason = "member_evidence_generation_missing";
                }
                else
                {
                    directReason = SplitFlankSafetyPolicy.Evaluate(
                        directDecision,
                        readinessGroup.OwnerProfileId,
                        readinessGroup.ThreatId,
                        directEvidenceCapturedAtUtc,
                        now,
                        allowExactOwnedDirectCommitPrimary: true);
                }
                if (directReason.Length == 0)
                {
                    directReady++;
                    if (directDecision.CapturedAtUtc > newestDirectReadyEvidenceAtUtc)
                    {
                        newestDirectReadyEvidenceAtUtc = directDecision.CapturedAtUtc;
                    }
                }
                else
                {
                    directBlocked++;
                    directBlockingReasons.Add(directReason);
                }
            }
            directReadyMembers += directReady;
            directBlockedMembers += directBlocked;

            long currentPlanEpoch = 0;
            bool successfulFullAltCycleCompleted = false;
            string directCommitCycleCompletionReason = readinessGroup.AssignmentChanged
                ? "assignment_changed_before_terminal_cycle_resolution"
                : "current_plan_cycle_context_unavailable";
            bool currentPlanResolved = false;
            if (!readinessGroup.AssignmentChanged)
            {
                currentPlanResolved = TryResolveCurrentPlanCycleState(
                    readinessGroup.OwnerProfileId,
                    readinessGroup.ThreatId,
                    readinessGroup.AlternateCandidateId,
                    readinessGroup.AlternateBotProfileId,
                    out currentPlanEpoch,
                    out successfulFullAltCycleCompleted,
                    out directCommitCycleCompletionReason);
            }
            bool firstSuccessfulCycleTerminalObservation = false;
            if (currentPlanResolved)
            {
                if (tracker.TerminalFullAltPlanEpoch != 0
                    && tracker.TerminalFullAltPlanEpoch != currentPlanEpoch)
                {
                    tracker.TerminalFullAltPlanEpoch = 0;
                    tracker.TerminalFullAltCompletionReason = "none";
                    tracker.SynchronizedHoldReleaseApplied = false;
                    tracker.SynchronizedHoldReleasePlanCount = 0;
                    tracker.SynchronizedHoldReleaseDirectHoldCount = 0;
                }
                if (successfulFullAltCycleCompleted
                    && tracker.TerminalFullAltPlanEpoch != currentPlanEpoch)
                {
                    tracker.TerminalFullAltPlanEpoch = currentPlanEpoch;
                    tracker.TerminalFullAltCompletionReason = directCommitCycleCompletionReason;
                    firstSuccessfulCycleTerminalObservation = true;
                    successfulDirectCommitCycleCompletions++;
                }
                if (tracker.TerminalFullAltPlanEpoch == currentPlanEpoch)
                {
                    successfulFullAltCycleCompleted = true;
                    directCommitCycleCompletionReason = tracker.TerminalFullAltCompletionReason;
                }
            }
            else if (!readinessGroup.AssignmentChanged && tracker.TerminalFullAltPlanEpoch != 0)
            {
                // A transient lifecycle-snapshot gap must not resurrect synchronized readiness for an already
                // consumed plan epoch. This is fail-open with respect to TC authority: it suppresses new TC
                // movement until an exact new epoch is observed rather than reviving a stale completed cycle.
                currentPlanEpoch = tracker.TerminalFullAltPlanEpoch;
                successfulFullAltCycleCompleted = true;
                directCommitCycleCompletionReason = tracker.TerminalFullAltCompletionReason + ":current_plan_context_gap";
            }
            else
            {
                currentPlanEpoch = 0;
                successfulFullAltCycleCompleted = false;
            }

            float nearCommitThresholdMeters = readinessGroup.AlternateEstablishmentRegionRadiusMeters + DirectCommitMovementService.MaxAdvanceMeters;
            bool nearCommitObservedNow = readinessGroup.AlternatePositionKnown
                && readinessGroup.AltDevelopEligible
                && developmentSeen
                && physicalProgressObserved
                && closestRegionDistanceMeters <= nearCommitThresholdMeters
                && readinessGroup.AlternateDistanceToRegionCenterMeters <= nearCommitThresholdMeters
                && readinessGroup.AlternateVerticalDeltaMeters <= readinessGroup.AlternateEstablishmentRegionRadiusMeters
                && altSafetyReady;
            if (nearCommitObservedNow) nearCommitObserved++;

            bool approachCommitRaw = nearCommitObservedNow && directReady > 0 && !readinessGroup.AssignmentChanged;
            DateTimeOffset approachEvidenceAtUtc = fullAltEvidenceAtUtc == DateTimeOffset.MinValue || newestDirectReadyEvidenceAtUtc == DateTimeOffset.MinValue
                ? DateTimeOffset.MinValue
                : (fullAltEvidenceAtUtc <= newestDirectReadyEvidenceAtUtc ? fullAltEvidenceAtUtc : newestDirectReadyEvidenceAtUtc);
            bool synchronizedApproachCommitReady;
            if (successfulFullAltCycleCompleted)
            {
                tracker.ApproachCommitGate.ClearStableHistory();
                synchronizedApproachCommitReady = false;
            }
            else
            {
                AdvanceStableGate(approachCommitRaw, now, approachEvidenceAtUtc, SynchronizedApproachCommitStableDwell, tracker.ApproachCommitGate);
                synchronizedApproachCommitReady = tracker.ApproachCommitGate.Stable;
                if (!approachCommitRaw)
                {
                    tracker.ApproachCommitGate.ClearStableHistory();
                    synchronizedApproachCommitReady = false;
                }
            }
            if (synchronizedApproachCommitReady) synchronizedApproachReady++;

            bool synchronizedRaw = fullAlt && directReady > 0 && !readinessGroup.AssignmentChanged;
            DateTimeOffset synchronizedEvidenceAtUtc = fullAltEvidenceAtUtc == DateTimeOffset.MinValue || newestDirectReadyEvidenceAtUtc == DateTimeOffset.MinValue
                ? DateTimeOffset.MinValue
                : (fullAltEvidenceAtUtc <= newestDirectReadyEvidenceAtUtc ? fullAltEvidenceAtUtc : newestDirectReadyEvidenceAtUtc);
            bool synchronizedCommitReady;
            if (successfulFullAltCycleCompleted)
            {
                tracker.CommitGate.ClearStableHistory();
                synchronizedCommitReady = false;
            }
            else
            {
                AdvanceStableGate(synchronizedRaw, now, synchronizedEvidenceAtUtc, SynchronizedCommitStableDwell, tracker.CommitGate);
                synchronizedCommitReady = tracker.CommitGate.Stable;
                if (!fullAlt || directReady == 0 || readinessGroup.AssignmentChanged)
                {
                    tracker.CommitGate.ClearStableHistory();
                    synchronizedCommitReady = false;
                }
            }
            if (synchronizedCommitReady) synchronizedReady++;

            bool anyPhysicalCommitRaw = synchronizedRaw || approachCommitRaw;
            bool anyPhysicalCommitReady = synchronizedCommitReady || synchronizedApproachCommitReady;
            if (successfulFullAltCycleCompleted)
            {
                // A successful strict full-alt DirectCommit is terminal for this exact plan epoch. Perform one
                // final hold release/suppression tail, then stop refreshing synchronized readiness so support
                // systems can resume naturally after the bounded tail expires. A new epoch must restabilize.
                if (firstSuccessfulCycleTerminalObservation)
                {
                    int finalReleasedDirectHolds;
                    int finalReleasedPlans = ChokeCommitSupportService.ReleaseSynchronizedFlankHolds(
                        readinessGroup.OwnerProfileId,
                        readinessGroup.ThreatId,
                        now,
                        out finalReleasedDirectHolds);
                    if (finalReleasedPlans > 0)
                    {
                        tracker.SynchronizedHoldReleaseApplied = true;
                        tracker.SynchronizedHoldReleasePlanCount += finalReleasedPlans;
                        tracker.SynchronizedHoldReleaseDirectHoldCount += finalReleasedDirectHolds;
                        synchronizedHoldReleasePlans += finalReleasedPlans;
                        synchronizedHoldReleaseDirectHolds += finalReleasedDirectHolds;
                    }
                    terminalFinalHoldReleasePlans += finalReleasedPlans;
                    terminalFinalHoldReleaseDirectHolds += finalReleasedDirectHolds;
                    VanguardClientDiagnosticsLog.Info(StatusTag,
                        $"TC_SPLIT_FLANK_STATE_CYCLE_COMPLETED owner={Safe(readinessGroup.OwnerProfileId)}; threat={Safe(readinessGroup.ThreatId)}; axis={Safe(readinessGroup.AlternateCandidateId)}; altBot={Safe(readinessGroup.AlternateBotProfileId)}; planEpoch={currentPlanEpoch}; completionReason={Safe(directCommitCycleCompletionReason)}; finalPlansReleased={finalReleasedPlans}; finalDirectHoldsReleased={finalReleasedDirectHolds}; synchronizedReadinessConsumed=true; finalSuppressionTail=true; repeatedSuppressionRefresh=false; sameEpochReacquire=false; movementCommand=false; fireCommand=false; sainMutation=false");
                }
            }
            else if (!anyPhysicalCommitRaw)
            {
                tracker.SynchronizedHoldReleaseApplied = false;
                tracker.SynchronizedHoldReleasePlanCount = 0;
                tracker.SynchronizedHoldReleaseDirectHoldCount = 0;
            }
            else if (anyPhysicalCommitReady)
            {
                // Refresh the bounded commit support reacquisition suppression on every stable-readiness observation.
                // The commit support API is idempotent when no matching holds exist, but will release a newly-created
                // matching hold if ordering/race conditions allowed one to appear between observations.
                int releasedDirectHolds;
                int releasedPlans = ChokeCommitSupportService.ReleaseSynchronizedFlankHolds(
                    readinessGroup.OwnerProfileId,
                    readinessGroup.ThreatId,
                    now,
                    out releasedDirectHolds);
                if (releasedPlans > 0)
                {
                    tracker.SynchronizedHoldReleaseApplied = true;
                    tracker.SynchronizedHoldReleasePlanCount += releasedPlans;
                    tracker.SynchronizedHoldReleaseDirectHoldCount += releasedDirectHolds;
                    synchronizedHoldReleasePlans += releasedPlans;
                    synchronizedHoldReleaseDirectHolds += releasedDirectHolds;
                    VanguardClientDiagnosticsLog.Info(StatusTag,
                        $"TC_SPLIT_FLANK_STATE_SYNCHRONIZED_HOLD_RELEASE owner={Safe(readinessGroup.OwnerProfileId)}; threat={Safe(readinessGroup.ThreatId)}; axis={Safe(readinessGroup.AlternateCandidateId)}; altBot={Safe(readinessGroup.AlternateBotProfileId)}; plansReleased={releasedPlans}; directHoldsReleased={releasedDirectHolds}; cumulativePlansReleased={tracker.SynchronizedHoldReleasePlanCount}; cumulativeDirectHoldsReleased={tracker.SynchronizedHoldReleaseDirectHoldCount}; fullAltEstablished={fullAlt}; synchronizedApproachCommitReady={synchronizedApproachCommitReady}; synchronizedCommitReady={synchronizedCommitReady}; releaseOnly=true; reacquireSuppressionRefreshed=true; movementCommand=false; fireCommand=false; sainMutation=false; directCommitExecution=false");
                }
            }

            string establishmentSource = developmentDone
                ? "alt_develop_completed"
                : developmentSeen
                    ? "alt_develop_active_or_interrupted_evidence"
                    : "organic_or_prepositioned_establishment";
            if (establishedUsingRecentPressureBridge)
            {
                establishmentSource += "+recent_pressure_bridge";
            }

            string classification;
            string reason;
            if (successfulFullAltCycleCompleted)
            {
                classification = "direct_commit_full_alt_cycle_completed";
                reason = directCommitCycleCompletionReason;
            }
            else if (synchronizedCommitReady)
            {
                classification = tracker.SynchronizedHoldReleaseApplied
                    ? "synchronized_direct_hold_release_applied"
                    : "synchronized_commit_ready_shadow";
                reason = tracker.SynchronizedHoldReleaseApplied
                    ? "existing_vanguard_holds_released_for_synchronized_flank"
                    : retainedByGrace
                        ? "full_alt_established_pressure_grace_and_direct_member_ready"
                        : "full_alt_established_and_direct_member_ready";
            }
            else if (synchronizedApproachCommitReady)
            {
                classification = tracker.SynchronizedHoldReleaseApplied
                    ? "synchronized_approach_commit_hold_release_applied"
                    : "synchronized_approach_commit_ready";
                reason = "alternate_within_one_direct_commit_slice_and_direct_member_ready";
            }
            else if (nearCommitObservedNow)
            {
                classification = "alternate_near_commit_envelope";
                reason = directReady > 0
                    ? "approach_commit_readiness_stabilizing"
                    : "alternate_near_commit_but_no_direct_member_ready";
            }
            else if (fullAlt)
            {
                classification = "full_alt_established_shadow";
                reason = directReady > 0
                    ? "commit_readiness_stabilizing"
                    : "no_direct_member_currently_commit_ready";
            }
            else if (readinessGroup.AlternatePositionEstablishedObserved && altSafetyReady && !pressureObserved)
            {
                classification = "alt_position_established_waiting_pressure";
                reason = pressureReason;
            }
            else if (readinessGroup.AlternatePositionEstablishedObserved && !altSafetyReady)
            {
                classification = "alt_position_established_local_safety_block";
                reason = altSafetyReason;
            }
            else if (developmentSeen)
            {
                classification = "alt_development_observed_not_established";
                reason = readinessGroup.Reason;
            }
            else
            {
                classification = "split_flank_observing";
                reason = readinessGroup.Reason;
            }

            var distinctDirectReasons = directBlockingReasons
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var observation = new ThreatGroupSplitFlankStateObservation(
                readinessGroup.OwnerProfileId,
                readinessGroup.ThreatId,
                readinessGroup.AlternateCandidateId,
                readinessGroup.AlternateBotProfileId,
                readinessGroup.AssignmentChanged,
                readinessGroup.AssignmentAgeSeconds,
                developmentSeen,
                developmentDone,
                physicalProgressObserved,
                physicalProgressKind,
                closestRegionDistanceMeters,
                developmentState,
                developmentReason,
                readinessGroup.AlternatePositionEstablishedObserved,
                nearCommitObservedNow,
                nearCommitThresholdMeters,
                pressureObserved,
                pressureReason,
                altSafetyReady,
                altSafetyReason,
                tracker.FullAltGate.SampleCount,
                tracker.FullAltGate.DwellSeconds(now),
                retainedByGrace,
                fullAlt,
                establishmentSource,
                assignment.DirectBotProfileIds.Count,
                directReady,
                directBlocked,
                distinctDirectReasons,
                approachCommitRaw,
                tracker.ApproachCommitGate.SampleCount,
                tracker.ApproachCommitGate.DwellSeconds(now),
                synchronizedApproachCommitReady,
                synchronizedRaw,
                tracker.CommitGate.SampleCount,
                tracker.CommitGate.DwellSeconds(now),
                synchronizedCommitReady,
                tracker.SynchronizedHoldReleaseApplied,
                tracker.SynchronizedHoldReleasePlanCount,
                tracker.SynchronizedHoldReleaseDirectHoldCount,
                successfulFullAltCycleCompleted,
                successfulFullAltCycleCompleted ? currentPlanEpoch : 0L,
                successfulFullAltCycleCompleted ? directCommitCycleCompletionReason : "none",
                classification,
                reason);
            observations.Add(observation);
            EmitDetailIfChanged(observation, key);
        }

        var next = new SplitFlankStateSnapshot(
            readinessSnapshot.MapId,
            readinessSnapshot.TopologyRevision,
            readinessSnapshot.TopologyContentHash,
            readinessSnapshot.PublishedAtUtc,
            now,
            observations.ToArray());

        lock (Sync)
        {
            current = next;
        }

        inputGroupsTotal += observations.Count;
        developmentObservedTotal += developmentObserved;
        developmentCompletedTotal += developmentCompleted;
        pressureProofTotal += pressureProof;
        flankSupportPressureProofTotal += flankSupportPressureProof;
        fullAltEstablishedTotal += fullAltEstablished;
        fullAltPressureBridgeTotal += pressureBridged;
        fullAltGraceRetainedTotal += graceRetained;
        synchronizedCommitReadyTotal += synchronizedReady;
        nearCommitObservedTotal += nearCommitObserved;
        synchronizedApproachCommitReadyTotal += synchronizedApproachReady;
        directReadyMemberTotal += directReadyMembers;
        directBlockedMemberTotal += directBlockedMembers;
        synchronizedHoldReleasePlansTotal += synchronizedHoldReleasePlans;
        synchronizedHoldReleaseDirectHoldsTotal += synchronizedHoldReleaseDirectHolds;
        successfulDirectCommitCycleCompletionsTotal += successfulDirectCommitCycleCompletions;
        terminalFinalHoldReleasePlansTotal += terminalFinalHoldReleasePlans;
        terminalFinalHoldReleaseDirectHoldsTotal += terminalFinalHoldReleaseDirectHolds;

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_SPLIT_FLANK_STATE_BOOT enabled=true; input=split_flank_readiness+alt_develop_physical_provenance; fullAltStableSamples={StableSampleCount}; fullAltStableMs={FullAltStableDwell.TotalMilliseconds:0}; pressureEstablishmentBridgeMs={PressureEstablishmentBridge.TotalMilliseconds:0}; pressureGraceSeconds={PressureGrace.TotalSeconds:0.0}; synchronizedCommitStableSamples={StableSampleCount}; synchronizedCommitStableMs={SynchronizedCommitStableDwell.TotalMilliseconds:0}; fullAltRequires=position+same_threat_current_or_recent_pressure+hard_safety; recentPressureBridgeRequires=same_assignment_same_threat+fresh_pressure+fresh_position_safety; approachCommitRequires=physical_progress+distance_within_region_plus_one_direct_slice+same_vertical_band+hard_safety+direct_ready; directCommitReadiness=shadow_only; synchronizedSupportHoldRelease=true; supportReacquireSuppressionRefresh=true; successfulFullAltDirectCommitTerminalConsumesReadiness=true; terminalHoldReleaseOneShot=true; terminalSuppressionTailThenExpire=true; releaseOnly=true; directCommitExecution=false; movementCommand=false; fireMutation=false; sainMutation=false");
        }

        if (now >= nextSummaryAtUtc)
        {
            nextSummaryAtUtc = now + SummaryInterval;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_SPLIT_FLANK_STATE_SUMMARY publishedGroups={observations.Count}; developmentObserved={developmentObserved}; developmentCompleted={developmentCompleted}; pressureProof={pressureProof}; flankSupportPressureProof={flankSupportPressureProof}; fullAltEstablished={fullAltEstablished}; fullAltPressureBridge={pressureBridged}; fullAltGraceRetained={graceRetained}; nearCommitObserved={nearCommitObserved}; synchronizedApproachCommitReady={synchronizedApproachReady}; synchronizedCommitReady={synchronizedReady}; directReadyMembers={directReadyMembers}; directBlockedMembers={directBlockedMembers}; inputGroupsTotal={inputGroupsTotal}; developmentObservedTotal={developmentObservedTotal}; developmentCompletedTotal={developmentCompletedTotal}; pressureProofTotal={pressureProofTotal}; flankSupportPressureProofTotal={flankSupportPressureProofTotal}; fullAltEstablishedTotal={fullAltEstablishedTotal}; fullAltPressureBridgeTotal={fullAltPressureBridgeTotal}; fullAltGraceRetainedTotal={fullAltGraceRetainedTotal}; nearCommitObservedTotal={nearCommitObservedTotal}; synchronizedApproachCommitReadyTotal={synchronizedApproachCommitReadyTotal}; synchronizedCommitReadyTotal={synchronizedCommitReadyTotal}; directReadyMemberTotal={directReadyMemberTotal}; directBlockedMemberTotal={directBlockedMemberTotal}; synchronizedHoldReleasePlans={synchronizedHoldReleasePlans}; synchronizedHoldReleaseDirectHolds={synchronizedHoldReleaseDirectHolds}; synchronizedHoldReleasePlansTotal={synchronizedHoldReleasePlansTotal}; synchronizedHoldReleaseDirectHoldsTotal={synchronizedHoldReleaseDirectHoldsTotal}; successfulDirectCommitCycleCompletions={successfulDirectCommitCycleCompletions}; successfulDirectCommitCycleCompletionsTotal={successfulDirectCommitCycleCompletionsTotal}; terminalFinalHoldReleasePlans={terminalFinalHoldReleasePlans}; terminalFinalHoldReleaseDirectHolds={terminalFinalHoldReleaseDirectHolds}; terminalFinalHoldReleasePlansTotal={terminalFinalHoldReleasePlansTotal}; terminalFinalHoldReleaseDirectHoldsTotal={terminalFinalHoldReleaseDirectHoldsTotal}; directCommitExecution=false; synchronizedAssault=false; existingHoldReleaseOnly=true; planStateOnly=false");
        }

        bool replanObserved = false;
        try
        {
            TacticalReplanService.Observe(next, now);
            replanObserved = true;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                TacticalReplanService.StatusTag,
                $"Tactical replan shadow observe failed fail-open: {exception.GetType().Name}: {exception.Message}; executionAllowed=false; mutation=false");
            TacticalReplanService.Reset("observe_exception_fail_open");
        }

        if (replanObserved)
        {
            try
            {
                TacticalReplanExecutionService.Tick(now);
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(
                    TacticalReplanExecutionService.StatusTag,
                    $"Tactical replan execution failed fail-open: {exception.GetType().Name}: {exception.Message}; drainOnly=true; movementStart=false; sainMutation=false");
                TacticalReplanExecutionService.Reset("execute_exception_fail_open");
            }
        }
    }

    public static bool TryGetSnapshot(out SplitFlankStateSnapshot snapshot)
    {
        lock (Sync)
        {
            snapshot = current;
            return snapshot.IsAvailable;
        }
    }

    internal static void InvalidateUpstream(DateTimeOffset now, string reason, bool preserveTrackerHistory = false)
    {
        bool hadPublishedState;
        bool hadTrackerHistory;
        lock (Sync)
        {
            hadPublishedState = current.IsAvailable;
            hadTrackerHistory = TrackerByAssignment.Count > 0;
            current = SplitFlankStateSnapshot.Empty;
            if (!preserveTrackerHistory)
            {
                TrackerByAssignment.Clear();
                LastDetailSignatureByAssignment.Clear();
            }
        }

        bool replanObserved = false;
        try
        {
            TacticalReplanService.Observe(SplitFlankStateSnapshot.Empty, now);
            replanObserved = true;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                TacticalReplanService.StatusTag,
                $"Tactical replan upstream-gap observe failed fail-open: {exception.GetType().Name}: {exception.Message}; executionAllowed=false; mutation=false");
        }

        if (replanObserved)
        {
            try
            {
                TacticalReplanExecutionService.Tick(now);
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(
                    TacticalReplanExecutionService.StatusTag,
                    $"Tactical replan upstream-gap execution failed fail-open: {exception.GetType().Name}: {exception.Message}; drainOnly=true; movementStart=false; sainMutation=false");
                TacticalReplanExecutionService.Reset("upstream_gap_execute_exception_fail_open");
            }
        }

        if ((hadPublishedState || (!preserveTrackerHistory && hadTrackerHistory)) && !string.IsNullOrWhiteSpace(reason))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_SPLIT_FLANK_STATE_UPSTREAM_INVALIDATED reason={Safe(reason)}; trackerHistoryPreserved={preserveTrackerHistory}; replanHistoryPreserved=true; currentPublication=false; executionAllowed=false; mutation=false");
        }
    }

    public static void Reset(string reason)
    {
        bool hadState;
        lock (Sync)
        {
            hadState = current.IsAvailable || TrackerByAssignment.Count > 0;
            current = SplitFlankStateSnapshot.Empty;
            TrackerByAssignment.Clear();
            LastDetailSignatureByAssignment.Clear();
            nextSummaryAtUtc = DateTimeOffset.MinValue;
        }
        bootLogged = false;
        inputGroupsTotal = 0;
        developmentObservedTotal = 0;
        developmentCompletedTotal = 0;
        pressureProofTotal = 0;
        flankSupportPressureProofTotal = 0;
        fullAltEstablishedTotal = 0;
        fullAltPressureBridgeTotal = 0;
        fullAltGraceRetainedTotal = 0;
        synchronizedCommitReadyTotal = 0;
        nearCommitObservedTotal = 0;
        synchronizedApproachCommitReadyTotal = 0;
        directReadyMemberTotal = 0;
        directBlockedMemberTotal = 0;
        synchronizedHoldReleasePlansTotal = 0;
        synchronizedHoldReleaseDirectHoldsTotal = 0;
        successfulDirectCommitCycleCompletionsTotal = 0;
        terminalFinalHoldReleasePlansTotal = 0;
        terminalFinalHoldReleaseDirectHoldsTotal = 0;
        TacticalReplanService.Reset("split_flank_state_reset:" + Safe(reason));
        if (hadState && !string.IsNullOrWhiteSpace(reason))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_SPLIT_FLANK_STATE_RESET reason={Safe(reason)}; planStateOnly=true; executionAllowed=false; mutation=false");
        }
    }

    private static bool TryResolveCurrentPlanCycleState(
        string ownerProfileId,
        string threatId,
        string alternateCandidateId,
        string alternateBotProfileId,
        out long planEpoch,
        out bool successfulFullAltCycleCompleted,
        out string reason)
    {
        planEpoch = 0;
        successfulFullAltCycleCompleted = false;
        reason = "tactical_replan_snapshot_missing";
        if (!TacticalReplanService.TryGetSnapshot(out var replanSnapshot))
        {
            return false;
        }

        var plan = replanSnapshot.Plans.FirstOrDefault(item =>
            item.PlanEpoch > 0
            && item.UpstreamPresent
            && !item.HardBreakRecommended
            && !item.ReplanRecommended
            && string.Equals(item.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, threatId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.AlternateCandidateId, alternateCandidateId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.AlternateBotProfileId, alternateBotProfileId, StringComparison.OrdinalIgnoreCase));
        if (plan == null)
        {
            reason = "exact_current_plan_context_missing_or_invalidated";
            return false;
        }

        long resolvedPlanEpoch = plan.PlanEpoch;
        planEpoch = resolvedPlanEpoch;
        reason = "current_plan_without_successful_full_alt_terminal";
        if (!DirectCommitMovementService.TryGetExecutionSnapshot(out var executionSnapshot))
        {
            return true;
        }

        var receipt = executionSnapshot.Executions
            .Where(item => item.PlanEpoch == resolvedPlanEpoch
                && item.IsCompleted
                && string.Equals(item.ReadinessMode, "full_alt", StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ThreatId, threatId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.AlternateCandidateId, alternateCandidateId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.AlternateBotProfileId, alternateBotProfileId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.ObservedAtUtc)
            .FirstOrDefault();
        if (receipt == null)
        {
            return true;
        }

        successfulFullAltCycleCompleted = true;
        reason = "successful_full_alt_direct_commit:" + Safe(receipt.Reason);
        return true;
    }

    private static bool TryObserveEstablishedFlankSupportPressureProof(
        ThreatGroupRouteAssignment assignment,
        string threatId,
        DateTimeOffset now,
        out DateTimeOffset evidenceAtUtc,
        out string reason)
    {
        evidenceAtUtc = DateTimeOffset.MinValue;
        reason = "flank_support_not_selected";
        if (assignment == null || !assignment.HasShadowAltSupportSelection)
        {
            return false;
        }
        AltRouteFeasibilityObservation? candidate = assignment.AltCandidateFeasibility.FirstOrDefault(item =>
            string.Equals(item.AxisCandidateId, assignment.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.BotProfileId, assignment.SelectedAltSupportBotProfileId, StringComparison.OrdinalIgnoreCase)
            && item.IsFeasiblePair
            && item.CandidateSpecificRouteProven);
        if (candidate == null)
        {
            reason = "flank_support_same_axis_path_not_current";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(assignment.SelectedAltSupportBotProfileId, out OperatorDecisionSnapshot support)
            || support == null
            || !support.Alive)
        {
            reason = "flank_support_snapshot_missing_or_dead";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(support, now, out string freshnessReason))
        {
            reason = "flank_support_" + freshnessReason;
            return false;
        }
        if (!string.Equals(support.OwnerProfileId, assignment.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "flank_support_owner_changed";
            return false;
        }
        float dx = support.Position.x - assignment.AlternateEstablishmentRegionCenter.x;
        float dz = support.Position.z - assignment.AlternateEstablishmentRegionCenter.z;
        float regionDistance = (float)Math.Sqrt(dx * dx + dz * dz);
        if (regionDistance > assignment.AlternateEstablishmentRegionRadiusMeters + 6f)
        {
            reason = "flank_support_not_near_alternate_entry";
            return false;
        }
        if (!TryObservePressureProof(support, threatId, out string localReason))
        {
            reason = localReason;
            return false;
        }
        evidenceAtUtc = support.CapturedAtUtc;
        reason = localReason + ":same_axis_established_support";
        return true;
    }

    private static bool TryObservePressureProof(OperatorDecisionSnapshot snapshot, string threatId, out string reason)
    {
        string expected = Normalize(threatId);
        string current = Normalize(snapshot.Threat.EnemyId);
        bool currentMatches = current.Length > 0 && string.Equals(current, expected, StringComparison.OrdinalIgnoreCase);
        if (currentMatches)
        {
            if (snapshot.Threat.EnemyCanShoot == true)
            {
                reason = "current_threat_can_shoot";
                return true;
            }
            if (snapshot.Threat.ShotAtMeRecently == true || snapshot.Threat.ShotMeRecently == true)
            {
                reason = "current_threat_incoming_engagement";
                return true;
            }
            if (IsEngagementCombatDecision(snapshot.Sain.CombatDecision))
            {
                reason = "sain_engagement_decision:" + Safe(snapshot.Sain.CombatDecision);
                return true;
            }
            if (IsPressureSquadDecision(snapshot.Sain.SquadDecision))
            {
                reason = "sain_pressure_squad_decision:" + Safe(snapshot.Sain.SquadDecision);
                return true;
            }
        }

        string scanCandidate = Normalize(snapshot.ThreatScan.CandidateThreatId);
        if (scanCandidate.Length > 0 && string.Equals(scanCandidate, expected, StringComparison.OrdinalIgnoreCase))
        {
            if (snapshot.ThreatScan.CandidateCanShoot)
            {
                reason = "scan_candidate_can_shoot";
                return true;
            }
            if (snapshot.ThreatScan.CandidateIncomingFireFresh
                || snapshot.ThreatScan.CandidateShotAtMeRecently
                || snapshot.ThreatScan.CandidateShotMeRecently)
            {
                reason = "scan_candidate_incoming_engagement";
                return true;
            }
        }

        reason = currentMatches
            ? "same_threat_without_pressure_or_engagement_proof"
            : "shared_threat_without_local_pressure_proof";
        return false;
    }

    private static bool IsEngagementCombatDecision(string? value)
    {
        string normalized = NormalizeToken(value);
        return normalized is "standandshoot" or "dogfight" or "rushenemy" or "movetoengage" or "shootdistantenemy";
    }

    private static bool IsPressureSquadDecision(string? value)
    {
        string normalized = NormalizeToken(value);
        return normalized is "suppress" or "pushsuppressedenemy";
    }

    private static string NormalizeToken(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static void AdvanceStableGate(bool rawReady, DateTimeOffset now, DateTimeOffset evidenceAtUtc, TimeSpan dwell, StableGate gate)
    {
        if (!rawReady || evidenceAtUtc == DateTimeOffset.MinValue)
        {
            gate.SampleCount = 0;
            gate.FirstReadyAtUtc = DateTimeOffset.MinValue;
            gate.LastEvidenceAtUtc = DateTimeOffset.MinValue;
            gate.Stable = false;
            return;
        }

        if (gate.FirstReadyAtUtc == DateTimeOffset.MinValue)
        {
            gate.FirstReadyAtUtc = now;
            gate.LastEvidenceAtUtc = evidenceAtUtc;
            gate.SampleCount = 1;
        }
        else if (evidenceAtUtc > gate.LastEvidenceAtUtc)
        {
            gate.LastEvidenceAtUtc = evidenceAtUtc;
            gate.SampleCount++;
        }

        if (gate.SampleCount >= StableSampleCount && now - gate.FirstReadyAtUtc >= dwell)
        {
            gate.Stable = true;
            gate.WasEverStable = true;
        }
    }

    private static ReadinessTracker GetOrCreateTracker(string key, DateTimeOffset now)
    {
        lock (Sync)
        {
            if (!TrackerByAssignment.TryGetValue(key, out var tracker))
            {
                tracker = new ReadinessTracker { LastObservedAtUtc = now };
                TrackerByAssignment[key] = tracker;
            }
            return tracker;
        }
    }

    private static void Prune(DateTimeOffset now)
    {
        lock (Sync)
        {
            string[] stale = TrackerByAssignment
                .Where(pair => now - pair.Value.LastObservedAtUtc > TrackerRetention)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (string key in stale)
            {
                TrackerByAssignment.Remove(key);
                LastDetailSignatureByAssignment.Remove(key);
            }
        }
    }

    private static void EmitDetailIfChanged(ThreatGroupSplitFlankStateObservation observation, string key)
    {
        string signature = string.Join("|",
            observation.AlternateDevelopmentState,
            observation.AlternatePhysicalProgressObserved,
            observation.PositionEstablishedObserved,
            observation.AlternateNearCommitObserved,
            observation.SynchronizedApproachCommitReady,
            observation.PressureProofObserved,
            observation.AlternateHardSafetyReady,
            observation.FullAltEstablished,
            observation.FullAltRetainedByGrace,
            observation.DirectCommitReadyCount,
            observation.DirectCommitBlockedCount,
            observation.SynchronizedCommitReady,
            observation.SynchronizedHoldReleaseApplied,
            observation.SynchronizedHoldReleasePlanCount,
            observation.SynchronizedHoldReleaseDirectHoldCount,
            observation.DirectCommitCycleCompletedObserved,
            observation.DirectCommitCyclePlanEpoch,
            observation.Classification);
        lock (Sync)
        {
            if (LastDetailSignatureByAssignment.TryGetValue(key, out string last)
                && string.Equals(last, signature, StringComparison.Ordinal))
            {
                return;
            }
            LastDetailSignatureByAssignment[key] = signature;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_SPLIT_FLANK_STATE_STATE owner={Safe(observation.OwnerProfileId)}; threat={Safe(observation.ThreatId)}; axis={Safe(observation.AlternateCandidateId)}; altBot={Safe(observation.AlternateBotProfileId)}; assignmentChanged={observation.AssignmentChanged}; assignmentAge={Number(observation.AssignmentAgeSeconds)}; altDevelopmentObserved={observation.AlternateDevelopmentObserved}; altDevelopmentCompleted={observation.AlternateDevelopmentCompletedObserved}; altPhysicalProgress={observation.AlternatePhysicalProgressObserved}; altPhysicalProgressKind={Safe(observation.AlternatePhysicalProgressKind)}; altClosestRegionDistance={FiniteNumber(observation.AlternateClosestRegionDistanceMeters)}; altDevelopmentState={Safe(observation.AlternateDevelopmentState)}; altDevelopmentReason={Safe(observation.AlternateDevelopmentReason)}; positionEstablished={observation.PositionEstablishedObserved}; nearCommit={observation.AlternateNearCommitObserved}; nearCommitThreshold={FiniteNumber(observation.AlternateNearCommitThresholdMeters)}; approachCommitRaw={observation.SynchronizedApproachCommitRawReady}; approachCommitSamples={observation.SynchronizedApproachCommitStableSamples}; approachCommitDwell={Number(observation.SynchronizedApproachCommitStableDwellSeconds)}; synchronizedApproachCommitReady={observation.SynchronizedApproachCommitReady}; pressureProof={observation.PressureProofObserved}; pressureReason={Safe(observation.PressureProofReason)}; altHardSafetyReady={observation.AlternateHardSafetyReady}; altSafetyReason={Safe(observation.AlternateHardSafetyReason)}; fullAltSamples={observation.FullAltStableSamples}; fullAltDwell={Number(observation.FullAltStableDwellSeconds)}; fullAltGrace={observation.FullAltRetainedByGrace}; fullAltEstablished={observation.FullAltEstablished}; fullAltSource={Safe(observation.FullAltEstablishmentSource)}; directMembers={observation.DirectMemberCount}; directCommitReady={observation.DirectCommitReadyCount}; directCommitBlocked={observation.DirectCommitBlockedCount}; directCommitReasons={Safe(string.Join(",", observation.DirectCommitBlockingReasons))}; synchronizedRaw={observation.SynchronizedCommitRawReady}; synchronizedSamples={observation.SynchronizedCommitStableSamples}; synchronizedDwell={Number(observation.SynchronizedCommitStableDwellSeconds)}; synchronizedCommitReady={observation.SynchronizedCommitReady}; synchronizedHoldReleaseApplied={observation.SynchronizedHoldReleaseApplied}; synchronizedHoldReleasePlans={observation.SynchronizedHoldReleasePlanCount}; synchronizedHoldReleaseDirectHolds={observation.SynchronizedHoldReleaseDirectHoldCount}; directCommitCycleCompleted={observation.DirectCommitCycleCompletedObserved}; directCommitCyclePlanEpoch={observation.DirectCommitCyclePlanEpoch}; directCommitCycleCompletionReason={Safe(observation.DirectCommitCycleCompletionReason)}; classification={Safe(observation.Classification)}; reason={Safe(observation.Reason)}; directCommitExecution=false; synchronizedAssault=false; existingHoldReleaseOnly=true");
    }

    private static string AssignmentKey(
        string ownerProfileId,
        string threatId,
        string candidateId,
        string botProfileId,
        string supportBotProfileId)
        => string.Join("|",
            Normalize(ownerProfileId),
            Normalize(threatId),
            Normalize(candidateId),
            Normalize(botProfileId),
            Normalize(supportBotProfileId));

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    private static string Number(float value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    private static string FiniteNumber(float value) => float.IsNaN(value) || float.IsInfinity(value) ? "unknown" : Number(value);

    private sealed class ReadinessTracker
    {
        public StableGate FullAltGate { get; } = new();
        public StableGate ApproachCommitGate { get; } = new();
        public StableGate CommitGate { get; } = new();
        public DateTimeOffset LastPressureAtUtc { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastObservedAtUtc { get; set; } = DateTimeOffset.MinValue;
        public bool SynchronizedHoldReleaseApplied { get; set; }
        public int SynchronizedHoldReleasePlanCount { get; set; }
        public int SynchronizedHoldReleaseDirectHoldCount { get; set; }
        public long TerminalFullAltPlanEpoch { get; set; }
        public string TerminalFullAltCompletionReason { get; set; } = "none";
    }

    private sealed class StableGate
    {
        public int SampleCount { get; set; }
        public DateTimeOffset FirstReadyAtUtc { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastEvidenceAtUtc { get; set; } = DateTimeOffset.MinValue;
        public bool Stable { get; set; }
        public bool WasEverStable { get; set; }

        public float DwellSeconds(DateTimeOffset now)
            => FirstReadyAtUtc == DateTimeOffset.MinValue
                ? 0f
                : (float)Math.Max(0d, (now - FirstReadyAtUtc).TotalSeconds);

        public void ClearStableHistory()
        {
            SampleCount = 0;
            FirstReadyAtUtc = DateTimeOffset.MinValue;
            LastEvidenceAtUtc = DateTimeOffset.MinValue;
            Stable = false;
            WasEverStable = false;
        }
    }
}
#endif
