#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EFT;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Movement.Brain;

// Responsibility: Implements the bounded Choke Commit + Support physical family, including ordinary corridor crossing and strict assignment-aligned HeldAccess Direct containment.
// Flow: Ordinary crossing stages upstream Direct members behind one native-moving commit member; HeldAccessFix instead requires stable strict exposure plus the exact selected AltDevelop logical maneuver, then holds only locally admissible Direct members near the same converged choke through physical arrival/stable establishment until current split-flank pressure/commit readiness releases or invalidates the hold.
// Authority boundary: this family never chooses aim/fire, creates suppression, assigns alternate routes, moves the selected Alt member or interacts with a door. The native-moving commit member may remain eligible in bounded ordinary SeekCover/StandAndShoot combat, while StageHold/DirectHold/HeldAccessDirectHold remain strict Search-translation-only holds and SAIN target/aim/fire/steering plus stronger local survival authority stay live.
// Invariant: corridor crossing and HeldAccessFix are mutually exclusive for one owner/threat, every physical hold is exact-plan/member bounded, HeldAccess follows logical AltDevelop intent rather than one short path lease, current evidence/local safety are revalidated before renewal, synchronized split-flank readiness releases holds before DirectCommit, and all exits fail open without stale physical resume.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class ChokeCommitSupportService
{
    public const string StatusTag = "VANGUARD_CHOKE_COMMIT_SUPPORT_STATUS";
    public const string RequestKind = "TacticalCoordinatorChokeCommitStageHold";
    public const string HeldAccessFixRequestKind = "TacticalCoordinatorHeldAccessFix";

    private static readonly TimeSpan PlanTtl = TimeSpan.FromSeconds(1.75d);
    private static readonly TimeSpan ReacquireCooldown = TimeSpan.FromSeconds(3.00d);
    private static readonly TimeSpan SynchronizedFlankReleaseSuppression = TimeSpan.FromSeconds(1.25d);
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(12.0d);
    private static readonly TimeSpan AcquisitionInterval = TimeSpan.FromSeconds(0.20d);
    private const float MaximumOperatorRouteCrossTrackMeters = 2.75f;
    private const float MinimumBeforeThreatMeters = 0.20f;
    private const float CommitApproachPaddingMeters = 2.50f;
    private const float CommitClearedCorridorMeters = 1.00f;
    // anti-conga can acquire through 6.5 m and releases at 7 m. commit support keeps 6.5 m only as the
    // ownership seam between close anti-conga spacing and staged crossing participation.
    // There is deliberately no pairwise maximum headway in : the outer relevance bound
    // is passage-relative (shared corridor far edge + commit padding + support extra).
    private const float StageParticipationMinimumHeadwayMeters = 6.50f;
    private const float SupportApproachExtraMeters = 20.00f;
    private const float MaximumStageAnchorDriftMeters = 1.40f;
    private const float MinimumMovingSpeedForAdmission = 0.10f;
    private const string HeldAccessFixStrictCalibrationProfile = "strict_probe";
    private const string HeldAccessFixBalancedCalibrationProfile = "balanced_probe";

    // Distribution telemetry is intentionally summary-only. It records route-relative
    // headways before and after the current passage-relative outer envelope without
    // issuing extra NavMesh queries or changing tactical authority.
    private static readonly string[] HeadwayBucketLabels =
    {
        "0_4", "4_6p5", "6p5_10", "10_14", "14_18", "18_24", "24_32", "32_plus"
    };
    private static readonly int[] HeadwayObservedCounts = new int[HeadwayBucketLabels.Length];
    private static readonly int[] HeadwayWithinOuterEnvelopeCounts = new int[HeadwayBucketLabels.Length];
    private static readonly int[] HeadwayCommitReadyCounts = new int[HeadwayBucketLabels.Length];
    private static readonly int[] HeadwaySupportReadyCounts = new int[HeadwayBucketLabels.Length];
    private static readonly int[] HeadwayPlansIssuedCounts = new int[HeadwayBucketLabels.Length];

    private static readonly object Sync = new();
    private static readonly Dictionary<string, ActiveChokeCommitSupportPlan> ActiveByGroup = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ActiveDirectHoldContribution> ActiveDirectHoldByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ActiveHeldAccessFixPlan> ActiveHeldAccessFixByOwnerThreat = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ActiveHeldAccessFixMember> ActiveHeldAccessFixMemberByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> HeldAccessFixCooldownUntilByOwnerThreat = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> CooldownUntilBySupportBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> SynchronizedFlankReleaseUntilByOwnerThreat = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> AdmissionBlockCounts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> AdmissionBlockDetails = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> CommitRejectCounts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> SupportRejectCounts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> DirectHoldRejectCounts = new(StringComparer.OrdinalIgnoreCase);
    private static ChokeCommitSupportSnapshot current = ChokeCommitSupportSnapshot.Empty;
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextAcquisitionAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextHeldAccessFixAcquisitionAtUtc = DateTimeOffset.MinValue;
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
    private static long planGenerationCounter;

    private static int admissibleChokesEvaluated;
    private static int acquisitionPassesTotal;
    private static int convergedGroupsMatched;
    private static int routeWitnessesMatched;
    private static int liveCommitWindows;
    private static int pairGeometryWindows;
    private static int commitCandidatesEvaluated;
    private static int commitCandidatesAdmissible;
    private static int supportCandidatesEvaluated;
    private static int supportCandidatesAdmissible;
    private static int schedulerPairsReady;
    private static int plansIssued;
    private static int translationSuppressedTicks;
    private static int organicSuppressionTicks;
    private static int directHoldCandidatesEvaluated;
    private static int directHoldCandidatesAdmissible;
    private static int directHoldsIssued;
    private static int directHoldTranslationSuppressedTicks;
    private static int directHoldFailOpenReleases;
    private static int directHoldCompletions;
    private static int completions;
    private static int failOpenReleases;
    private static int livePatchFailOpenReleases;
    private static int synchronizedFlankReleasePlansTotal;
    private static int synchronizedFlankReleaseDirectHoldsTotal;
    private static int synchronizedFlankAcquisitionSuppressedTotal;
    private static int tacticalReplanReleasePlansTotal;
    private static int tacticalReplanReleaseDirectHoldsTotal;
    private static int heldAccessFixAcquisitionPassesTotal;
    private static int heldAccessFixOpportunityCandidatesTotal;
    private static int heldAccessFixAltManeuverCandidatesTotal;
    private static int heldAccessFixPlansIssuedTotal;
    private static int heldAccessFixMembersIssuedTotal;
    private static int heldAccessFixContainmentRenewalsTotal;
    private static int heldAccessFixTranslationSuppressedTicksTotal;
    private static int heldAccessFixPlanCompletionsTotal;
    private static int heldAccessFixPlanFailOpenReleasesTotal;
    private static int heldAccessFixMemberFailOpenReleasesTotal;
    private static int heldAccessFixSynchronizedReleasesTotal;
    private static int heldAccessFixTacticalReplanReleasesTotal;
    private static readonly Dictionary<string, int> HeldAccessFixAdmissionRejectCounts = new(StringComparer.OrdinalIgnoreCase);

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
            PublishEmpty();
            return;
        }

        TickActivePlans(convergenceSnapshot, chokeSnapshot, routeSnapshot, now);
        if (now >= nextAcquisitionAtUtc)
        {
            nextAcquisitionAtUtc = now + AcquisitionInterval;
            acquisitionPassesTotal++;
            TryAcquireOnePlanPerEligibleGroup(convergenceSnapshot, chokeSnapshot, routeSnapshot, now);
        }
        Publish(convergenceSnapshot, now);
    }

    internal static void TickHeldAccessFix(DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            ReleaseAllHeldAccessFix(now, "runtime_authority_unavailable", failOpen: true, applyCooldown: false);
            return;
        }

        TickActiveHeldAccessFixPlans(now);
        if (now < nextHeldAccessFixAcquisitionAtUtc)
        {
            return;
        }

        nextHeldAccessFixAcquisitionAtUtc = now + AcquisitionInterval;
        heldAccessFixAcquisitionPassesTotal++;
        TryAcquireHeldAccessFixPlans(now);
    }

    internal static void ReleaseHeldAccessFixFailOpen(DateTimeOffset now, string reason)
        => ReleaseAllHeldAccessFix(
            now,
            string.IsNullOrWhiteSpace(reason) ? "held_access_fix_fail_open" : reason,
            failOpen: true,
            applyCooldown: true);

    public static bool TryGetSnapshot(out ChokeCommitSupportSnapshot snapshot)
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
            ActiveDirectHoldByBot.Clear();
            ActiveHeldAccessFixByOwnerThreat.Clear();
            ActiveHeldAccessFixMemberByBot.Clear();
            HeldAccessFixCooldownUntilByOwnerThreat.Clear();
            HeldAccessFixAdmissionRejectCounts.Clear();
            CooldownUntilBySupportBot.Clear();
            SynchronizedFlankReleaseUntilByOwnerThreat.Clear();
            AdmissionBlockCounts.Clear();
            AdmissionBlockDetails.Clear();
            CommitRejectCounts.Clear();
            SupportRejectCounts.Clear();
            DirectHoldRejectCounts.Clear();
            Array.Clear(HeadwayObservedCounts, 0, HeadwayObservedCounts.Length);
            Array.Clear(HeadwayWithinOuterEnvelopeCounts, 0, HeadwayWithinOuterEnvelopeCounts.Length);
            Array.Clear(HeadwayCommitReadyCounts, 0, HeadwayCommitReadyCounts.Length);
            Array.Clear(HeadwaySupportReadyCounts, 0, HeadwaySupportReadyCounts.Length);
            Array.Clear(HeadwayPlansIssuedCounts, 0, HeadwayPlansIssuedCounts.Length);
            current = ChokeCommitSupportSnapshot.Empty;
        }

        nextSummaryAtUtc = DateTimeOffset.MinValue;
        nextAcquisitionAtUtc = DateTimeOffset.MinValue;
        nextHeldAccessFixAcquisitionAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        authorityWasActive = false;
        planGenerationCounter = 0;
        admissibleChokesEvaluated = 0;
        acquisitionPassesTotal = 0;
        convergedGroupsMatched = 0;
        routeWitnessesMatched = 0;
        liveCommitWindows = 0;
        pairGeometryWindows = 0;
        commitCandidatesEvaluated = 0;
        commitCandidatesAdmissible = 0;
        supportCandidatesEvaluated = 0;
        supportCandidatesAdmissible = 0;
        schedulerPairsReady = 0;
        plansIssued = 0;
        translationSuppressedTicks = 0;
        organicSuppressionTicks = 0;
        directHoldCandidatesEvaluated = 0;
        directHoldCandidatesAdmissible = 0;
        directHoldsIssued = 0;
        directHoldTranslationSuppressedTicks = 0;
        directHoldFailOpenReleases = 0;
        directHoldCompletions = 0;
        completions = 0;
        failOpenReleases = 0;
        livePatchFailOpenReleases = 0;
        synchronizedFlankReleasePlansTotal = 0;
        synchronizedFlankReleaseDirectHoldsTotal = 0;
        synchronizedFlankAcquisitionSuppressedTotal = 0;
        tacticalReplanReleasePlansTotal = 0;
        tacticalReplanReleaseDirectHoldsTotal = 0;
        heldAccessFixAcquisitionPassesTotal = 0;
        heldAccessFixOpportunityCandidatesTotal = 0;
        heldAccessFixAltManeuverCandidatesTotal = 0;
        heldAccessFixPlansIssuedTotal = 0;
        heldAccessFixMembersIssuedTotal = 0;
        heldAccessFixContainmentRenewalsTotal = 0;
        heldAccessFixTranslationSuppressedTicksTotal = 0;
        heldAccessFixPlanCompletionsTotal = 0;
        heldAccessFixPlanFailOpenReleasesTotal = 0;
        heldAccessFixMemberFailOpenReleasesTotal = 0;
        heldAccessFixSynchronizedReleasesTotal = 0;
        heldAccessFixTacticalReplanReleasesTotal = 0;

        if (!string.IsNullOrWhiteSpace(reason))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_CHOKE_COMMIT_SUPPORT_RESET reason={Safe(reason)}; activePlans=0; activeDirectHolds=0; contributions=DirectCommit+StageHold+DirectHold+HeldAccessDirectHold; physicalFamily=shared_sain_search_translation_gate; routeAssignment=false; suppressionCommand=false; staleResume=false; failOpen=true");
        }
    }

    internal static bool HasActiveStageHoldForBot(string botProfileId)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return false;
        }
        lock (Sync)
        {
            return ActiveByGroup.Values.Any(item =>
                    string.Equals(item.SupportBotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase))
                || ActiveDirectHoldByBot.ContainsKey(botProfileId)
                || ActiveHeldAccessFixMemberByBot.ContainsKey(botProfileId);
        }
    }

    internal static bool HasActiveContributionForBot(string botProfileId)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return false;
        }
        lock (Sync)
        {
            return ActiveByGroup.Values.Any(item =>
                    string.Equals(item.CommitBotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.SupportBotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase))
                || ActiveDirectHoldByBot.ContainsKey(botProfileId)
                || ActiveHeldAccessFixMemberByBot.ContainsKey(botProfileId);
        }
    }

    internal static int ReleaseSynchronizedFlankHolds(
        string ownerProfileId,
        string threatId,
        DateTimeOffset now,
        out int releasedDirectHoldCount)
    {
        releasedDirectHoldCount = 0;
        if (!VanguardFikaCompat.IsRaidAuthority
            || string.IsNullOrWhiteSpace(ownerProfileId)
            || string.IsNullOrWhiteSpace(threatId))
        {
            return 0;
        }

        string suppressionKey = OwnerThreatKey(ownerProfileId, threatId);
        ActiveChokeCommitSupportPlan[] matchingPlans;
        lock (Sync)
        {
            SynchronizedFlankReleaseUntilByOwnerThreat[suppressionKey] = now + SynchronizedFlankReleaseSuppression;
            matchingPlans = ActiveByGroup.Values
                .Where(item => string.Equals(item.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.ThreatId, threatId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.GroupKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        int releasedPlans = 0;
        foreach (var plan in matchingPlans)
        {
            int linkedDirectHolds;
            lock (Sync)
            {
                linkedDirectHolds = ActiveDirectHoldByBot.Values.Count(item =>
                    string.Equals(item.PlanId, plan.PlanId, StringComparison.OrdinalIgnoreCase));
            }

            if (!ReleasePlan(plan, now, "synchronized_flank_hold_release", failOpen: false))
            {
                continue;
            }

            releasedPlans++;
            releasedDirectHoldCount += linkedDirectHolds;
        }

        int heldAccessMembersReleased;
        int heldAccessPlansReleased = ReleaseHeldAccessFixForOwnerThreat(
            ownerProfileId,
            threatId,
            now,
            "synchronized_flank_hold_release",
            failOpen: false,
            applyCooldown: true,
            out heldAccessMembersReleased);
        if (heldAccessPlansReleased > 0)
        {
            heldAccessFixSynchronizedReleasesTotal += heldAccessPlansReleased;
            releasedPlans += heldAccessPlansReleased;
            releasedDirectHoldCount += heldAccessMembersReleased;
        }

        if (releasedPlans > 0)
        {
            synchronizedFlankReleasePlansTotal += releasedPlans;
            synchronizedFlankReleaseDirectHoldsTotal += releasedDirectHoldCount;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_CHOKE_COMMIT_SUPPORT_SYNCHRONIZED_FLANK_RELEASE owner={Safe(ownerProfileId)}; threat={Safe(threatId)}; plansReleased={releasedPlans}; directHoldsReleased={releasedDirectHoldCount}; reason=synchronized_commit_ready; movementCommand=false; fireCommand=false; sainMutation=false; releaseOnly=true");
        }

        return releasedPlans;
    }

    internal static int ReleaseForTacticalReplan(
        string ownerProfileId,
        string threatId,
        DateTimeOffset now,
        string reason,
        out int releasedDirectHoldCount)
    {
        releasedDirectHoldCount = 0;
        if (!VanguardFikaCompat.IsRaidAuthority
            || string.IsNullOrWhiteSpace(ownerProfileId)
            || string.IsNullOrWhiteSpace(threatId))
        {
            return 0;
        }

        string suppressionKey = OwnerThreatKey(ownerProfileId, threatId);
        ActiveChokeCommitSupportPlan[] matchingPlans;
        lock (Sync)
        {
            SynchronizedFlankReleaseUntilByOwnerThreat[suppressionKey] = now + SynchronizedFlankReleaseSuppression;
            matchingPlans = ActiveByGroup.Values
                .Where(item => string.Equals(item.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.ThreatId, threatId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.GroupKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        int releasedPlans = 0;
        foreach (var plan in matchingPlans)
        {
            int linkedDirectHolds;
            lock (Sync)
            {
                linkedDirectHolds = ActiveDirectHoldByBot.Values.Count(item =>
                    string.Equals(item.PlanId, plan.PlanId, StringComparison.OrdinalIgnoreCase));
            }

            if (!ReleasePlan(plan, now, "tactical_replan:" + Safe(reason), failOpen: true))
            {
                continue;
            }
            releasedPlans++;
            releasedDirectHoldCount += linkedDirectHolds;
        }

        int heldAccessMembersReleased;
        int heldAccessPlansReleased = ReleaseHeldAccessFixForOwnerThreat(
            ownerProfileId,
            threatId,
            now,
            "tactical_replan:" + Safe(reason),
            failOpen: true,
            applyCooldown: true,
            out heldAccessMembersReleased);
        if (heldAccessPlansReleased > 0)
        {
            heldAccessFixTacticalReplanReleasesTotal += heldAccessPlansReleased;
            releasedPlans += heldAccessPlansReleased;
            releasedDirectHoldCount += heldAccessMembersReleased;
        }

        if (releasedPlans > 0)
        {
            tacticalReplanReleasePlansTotal += releasedPlans;
            tacticalReplanReleaseDirectHoldsTotal += releasedDirectHoldCount;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_CHOKE_COMMIT_SUPPORT_REPLAN_RELEASE owner={Safe(ownerProfileId)}; threat={Safe(threatId)}; plansReleased={releasedPlans}; directHoldsReleased={releasedDirectHoldCount}; reason={Safe(reason)}; reacquireSuppressionSeconds={SynchronizedFlankReleaseSuppression.TotalSeconds:0.00}; releaseOnly=true; movementStart=false; fireCommand=false; sainMutation=false");
        }

        return releasedPlans;
    }

    internal static SearchTranslationDispatchResult TryHandleSharedSearchTranslationGate(
        object pathData,
        object sainBot,
        BotOwner botOwner,
        DateTimeOffset now,
        out string reason)
    {
        reason = "no_commit_support_gate_for_bot";
        if (!SearchTranslationPatchReady || pathData == null || sainBot == null || botOwner == null)
        {
            return SearchTranslationDispatchResult.NoLease;
        }

        ActiveChokeCommitSupportPlan? plan;
        ActiveDirectHoldContribution? directHold;
        ActiveHeldAccessFixMember? heldAccessFixMember;
        lock (Sync)
        {
            plan = ActiveByGroup.Values.FirstOrDefault(item =>
                string.Equals(item.SupportBotProfileId, botOwner.ProfileId, StringComparison.OrdinalIgnoreCase));
            ActiveDirectHoldByBot.TryGetValue(botOwner.ProfileId, out directHold);
            ActiveHeldAccessFixMemberByBot.TryGetValue(botOwner.ProfileId, out heldAccessFixMember);
        }

        if (plan != null)
        {
            try
            {
                if (now >= plan.ExpiresAtUtc)
                {
                    ReleasePlan(plan, now, "ttl_complete", failOpen: false);
                    reason = "ttl_complete";
                    return SearchTranslationDispatchResult.HandledFailOpen;
                }

                if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(plan.SupportBotProfileId, out var liveSupportDecision)
                    || liveSupportDecision == null
                    || !liveSupportDecision.Alive)
                {
                    livePatchFailOpenReleases++;
                    ReleasePlan(plan, now, "live_support_missing_or_dead", failOpen: true);
                    reason = "live_support_missing_or_dead";
                    return SearchTranslationDispatchResult.HandledFailOpen;
                }

                string liveSupportGate = ValidateSearchContributorDuringInfluence(liveSupportDecision, plan.ThreatId, now);
                if (liveSupportGate.Length > 0)
                {
                    livePatchFailOpenReleases++;
                    ReleasePlan(plan, now, "live_support_gate:" + liveSupportGate, failOpen: true);
                    reason = "live_support_gate:" + liveSupportGate;
                    return SearchTranslationDispatchResult.HandledFailOpen;
                }

                if (HorizontalDistance(botOwner.Position, plan.StageAnchor) > MaximumStageAnchorDriftMeters)
                {
                    livePatchFailOpenReleases++;
                    ReleasePlan(plan, now, "live_stage_anchor_drift", failOpen: true);
                    reason = "live_stage_anchor_drift";
                    return SearchTranslationDispatchResult.HandledFailOpen;
                }

                if (!SearchTranslationGateBridge.TryApplyExactSearchGate(
                        pathData,
                        sainBot,
                        botOwner,
                        now,
                        plan.ThreatId,
                        plan.SupportSchedulerWindowId,
                        out bool organicSuppressionActive,
                        out string liveGateReason))
                {
                    livePatchFailOpenReleases++;
                    ReleasePlan(plan, now, "live_sain_gate:" + liveGateReason, failOpen: true);
                    reason = liveGateReason;
                    return SearchTranslationDispatchResult.HandledFailOpen;
                }

                translationSuppressedTicks++;
                if (organicSuppressionActive)
                {
                    organicSuppressionTicks++;
                }
                reason = organicSuppressionActive
                    ? "stage_hold_translation_suppressed_sain_steering_preserved+organic_suppression"
                    : "stage_hold_translation_suppressed_sain_steering_preserved";
                return SearchTranslationDispatchResult.Suppressed;
            }
            catch (Exception exception)
            {
                livePatchFailOpenReleases++;
                ReleasePlan(plan, now, "live_patch_exception:" + exception.GetType().Name, failOpen: true);
                reason = "exception:" + exception.GetType().Name;
                return SearchTranslationDispatchResult.HandledFailOpen;
            }
        }

        if (directHold == null)
        {
            return heldAccessFixMember == null
                ? SearchTranslationDispatchResult.NoLease
                : TryHandleHeldAccessFixMemberGate(pathData, sainBot, botOwner, heldAccessFixMember, now, out reason);
        }

        try
        {
            if (now >= directHold.ExpiresAtUtc)
            {
                ReleaseDirectHold(directHold, now, "ttl_complete", failOpen: false);
                reason = "direct_hold_ttl_complete";
                return SearchTranslationDispatchResult.HandledFailOpen;
            }

            bool parentPlanActive;
            lock (Sync)
            {
                parentPlanActive = ActiveByGroup.TryGetValue(directHold.GroupKey, out var parent)
                    && string.Equals(parent.PlanId, directHold.PlanId, StringComparison.OrdinalIgnoreCase);
            }
            if (!parentPlanActive)
            {
                ReleaseDirectHold(directHold, now, "parent_plan_not_active", failOpen: true);
                reason = "direct_hold_parent_plan_not_active";
                return SearchTranslationDispatchResult.HandledFailOpen;
            }

            if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(directHold.BotProfileId, out var directHoldSnapshot)
                || directHoldSnapshot == null
                || !directHoldSnapshot.Alive)
            {
                ReleaseDirectHold(directHold, now, "operator_missing_or_dead", failOpen: true);
                reason = "direct_hold_operator_missing_or_dead";
                return SearchTranslationDispatchResult.HandledFailOpen;
            }
            string directHoldGate = ValidateSearchContributorDuringInfluence(directHoldSnapshot, directHold.ThreatId, now);
            if (directHoldGate.Length > 0)
            {
                ReleaseDirectHold(directHold, now, "local_gate:" + directHoldGate, failOpen: true);
                reason = "direct_hold_local_gate:" + directHoldGate;
                return SearchTranslationDispatchResult.HandledFailOpen;
            }
            if (AntiCongaService.HasActiveAdvanceGateForBot(directHold.BotProfileId))
            {
                ReleaseDirectHold(directHold, now, "anti_conga_gate_became_active", failOpen: true);
                reason = "direct_hold_anti_conga_gate_became_active";
                return SearchTranslationDispatchResult.HandledFailOpen;
            }
            if (HorizontalDistance(botOwner.Position, directHold.StageAnchor) > MaximumStageAnchorDriftMeters)
            {
                ReleaseDirectHold(directHold, now, "stage_anchor_drift", failOpen: true);
                reason = "direct_hold_stage_anchor_drift";
                return SearchTranslationDispatchResult.HandledFailOpen;
            }

            if (!SearchTranslationGateBridge.TryApplyExactSearchGate(
                    pathData,
                    sainBot,
                    botOwner,
                    now,
                    directHold.ThreatId,
                    directHold.SchedulerWindowId,
                    out bool directHoldSuppressionActive,
                    out string directHoldLiveGateReason))
            {
                ReleaseDirectHold(directHold, now, "live_sain_gate:" + directHoldLiveGateReason, failOpen: true);
                reason = "direct_hold:" + directHoldLiveGateReason;
                return SearchTranslationDispatchResult.HandledFailOpen;
            }

            directHoldTranslationSuppressedTicks++;
            if (directHoldSuppressionActive)
            {
                organicSuppressionTicks++;
            }
            reason = directHoldSuppressionActive
                ? "direct_hold_translation_suppressed_sain_steering_preserved+organic_suppression"
                : "direct_hold_translation_suppressed_sain_steering_preserved";
            return SearchTranslationDispatchResult.Suppressed;
        }
        catch (Exception exception)
        {
            ReleaseDirectHold(directHold, now, "live_patch_exception:" + exception.GetType().Name, failOpen: true);
            reason = "direct_hold_exception:" + exception.GetType().Name;
            return SearchTranslationDispatchResult.HandledFailOpen;
        }
    }

    private static SearchTranslationDispatchResult TryHandleHeldAccessFixMemberGate(
        object pathData,
        object sainBot,
        BotOwner botOwner,
        ActiveHeldAccessFixMember member,
        DateTimeOffset now,
        out string reason)
    {
        ActiveHeldAccessFixPlan? plan;
        lock (Sync)
        {
            ActiveHeldAccessFixByOwnerThreat.TryGetValue(member.OwnerThreatKey, out plan);
        }

        if (plan == null || !string.Equals(plan.PlanId, member.PlanId, StringComparison.OrdinalIgnoreCase))
        {
            ReleaseHeldAccessFixMember(member, now, "parent_plan_not_active", failOpen: true, applyCooldown: true);
            reason = "held_access_fix_parent_plan_not_active";
            return SearchTranslationDispatchResult.HandledFailOpen;
        }
        if (now >= member.ExpiresAtUtc || now >= plan.ExpiresAtUtc)
        {
            ReleaseHeldAccessFixMember(member, now, "containment_slice_expired", failOpen: true, applyCooldown: true);
            reason = "held_access_fix_containment_slice_expired";
            return SearchTranslationDispatchResult.HandledFailOpen;
        }
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(member.BotProfileId, out var snapshot)
            || snapshot == null
            || !snapshot.Alive)
        {
            ReleaseHeldAccessFixMember(member, now, "operator_missing_or_dead", failOpen: true, applyCooldown: true);
            reason = "held_access_fix_operator_missing_or_dead";
            return SearchTranslationDispatchResult.HandledFailOpen;
        }

        string localGate = ValidateSearchContributorDuringInfluence(snapshot, member.ThreatId, now);
        if (localGate.Length > 0)
        {
            ReleaseHeldAccessFixMember(member, now, "local_gate:" + localGate, failOpen: true, applyCooldown: true);
            reason = "held_access_fix_local_gate:" + localGate;
            return SearchTranslationDispatchResult.HandledFailOpen;
        }
        if (AntiCongaService.HasActiveAdvanceGateForBot(member.BotProfileId))
        {
            ReleaseHeldAccessFixMember(member, now, "anti_conga_gate_became_active", failOpen: true, applyCooldown: true);
            reason = "held_access_fix_anti_conga_gate_became_active";
            return SearchTranslationDispatchResult.HandledFailOpen;
        }
        if (HorizontalDistance(botOwner.Position, member.StageAnchor) > MaximumStageAnchorDriftMeters)
        {
            ReleaseHeldAccessFixMember(member, now, "stage_anchor_drift", failOpen: true, applyCooldown: true);
            reason = "held_access_fix_stage_anchor_drift";
            return SearchTranslationDispatchResult.HandledFailOpen;
        }

        if (!SearchTranslationGateBridge.TryApplyExactSearchGate(
                pathData,
                sainBot,
                botOwner,
                now,
                member.ThreatId,
                member.SchedulerWindowId,
                out bool organicSuppressionActive,
                out string liveGateReason))
        {
            ReleaseHeldAccessFixMember(member, now, "live_sain_gate:" + liveGateReason, failOpen: true, applyCooldown: true);
            reason = "held_access_fix:" + liveGateReason;
            return SearchTranslationDispatchResult.HandledFailOpen;
        }

        heldAccessFixTranslationSuppressedTicksTotal++;
        if (organicSuppressionActive)
        {
            organicSuppressionTicks++;
        }
        reason = organicSuppressionActive
            ? "held_access_fix_translation_suppressed_sain_steering_preserved+organic_suppression"
            : "held_access_fix_translation_suppressed_sain_steering_preserved";
        return SearchTranslationDispatchResult.Suppressed;
    }

    private static void TickActiveHeldAccessFixPlans(DateTimeOffset now)
    {
        ActiveHeldAccessFixPlan[] plans;
        lock (Sync)
        {
            plans = ActiveHeldAccessFixByOwnerThreat.Values.ToArray();
        }

        foreach (ActiveHeldAccessFixPlan plan in plans)
        {
            if (!TryResolveHeldAccessFixContext(
                    plan.AssignmentIdentity,
                    now,
                    plan.AltDevelopManeuverEpochId,
                    plan.StableEstablishmentContinuationObserved,
                    out HeldAccessFixContext context,
                    out string reason))
            {
                ReleaseHeldAccessFixPlan(plan, now, "evidence_or_alt_maneuver_not_current:" + reason, failOpen: true, applyCooldown: true);
                continue;
            }
            if (!string.Equals(context.Choke.ChokeId, plan.ChokeId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(context.Assignment.AlternateCandidateId, plan.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(context.Assignment.SelectedAltBotProfileId, plan.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(context.AltDevelopManeuverEpochId, plan.AltDevelopManeuverEpochId, StringComparison.OrdinalIgnoreCase))
            {
                ReleaseHeldAccessFixPlan(plan, now, "plan_or_alt_maneuver_identity_changed", failOpen: true, applyCooldown: true);
                continue;
            }

            bool stableContinuationPromoted = !plan.StableEstablishmentContinuationObserved
                && context.StableEstablishmentContinuation;
            DateTimeOffset refreshedExpiry = now + PlanTtl;
            lock (Sync)
            {
                if (!ActiveHeldAccessFixByOwnerThreat.TryGetValue(plan.OwnerThreatKey, out ActiveHeldAccessFixPlan? currentPlan)
                    || !string.Equals(currentPlan.PlanId, plan.PlanId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                currentPlan.ExpiresAtUtc = refreshedExpiry;
                currentPlan.StableEstablishmentContinuationObserved |= context.StableEstablishmentContinuation;
                foreach (ActiveHeldAccessFixMember member in ActiveHeldAccessFixMemberByBot.Values
                    .Where(item => string.Equals(item.PlanId, plan.PlanId, StringComparison.OrdinalIgnoreCase)))
                {
                    member.ExpiresAtUtc = refreshedExpiry;
                }
            }
            if (stableContinuationPromoted)
            {
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"TC_HELD_ACCESS_FIX_ESTABLISHMENT_CONTINUITY owner={Safe(plan.OwnerProfileId)}; threat={Safe(plan.ThreatId)}; axis={Safe(plan.AlternateCandidateId)}; altBot={Safe(plan.SelectedAltBotProfileId)}; altManeuver={Safe(plan.AltDevelopManeuverEpochId)}; choke={Safe(plan.ChokeId)}; positionEstablished=true; physicalAltLeaseRequired=false; holdReleaseAuthority=SplitFlankState; directTranslationStillSuppressed=true; fireCommand=false; targetMutation=false");
            }
            heldAccessFixContainmentRenewalsTotal++;
            TryAcquireMissingHeldAccessFixMembers(plan, context, now);
        }
    }

    private static void TryAcquireHeldAccessFixPlans(DateTimeOffset now)
    {
        if (!SearchTranslationPatchReady
            || !ThreatExposureShadowClassificationService.TryGetSnapshot(out ThreatExposureShadowSnapshot shadow)
            || !shadow.IsAvailable)
        {
            return;
        }

        foreach (ThreatExposureShadowGroupObservation group in shadow.Groups
            .OrderBy(item => item.OwnerProfileId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ThreatId, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryGetHeldAccessFixOpportunityProfile(group, out _))
            {
                continue;
            }
            heldAccessFixOpportunityCandidatesTotal++;

            string ownerThreatKey = OwnerThreatKey(group.OwnerProfileId, group.ThreatId);
            lock (Sync)
            {
                if (ActiveHeldAccessFixByOwnerThreat.ContainsKey(ownerThreatKey)
                    || ActiveByGroup.Values.Any(item =>
                        string.Equals(item.OwnerProfileId, group.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(item.ThreatId, group.ThreatId, StringComparison.OrdinalIgnoreCase)))
                {
                    RecordHeldAccessFixReject("physical_family_already_active");
                    continue;
                }
            }
            if (IsSynchronizedFlankReleaseSuppressed(group.OwnerProfileId, group.ThreatId, now))
            {
                RecordHeldAccessFixReject("synchronized_release_suppression_active");
                continue;
            }
            if (IsHeldAccessFixCooldownActive(ownerThreatKey, now))
            {
                RecordHeldAccessFixReject("held_access_fix_reacquire_cooldown");
                continue;
            }
            if (!TryResolveHeldAccessFixContext(
                    group.AssignmentIdentity,
                    now,
                    expectedManeuverEpochId: string.Empty,
                    establishedContinuationAlreadyProven: false,
                    out HeldAccessFixContext context,
                    out string contextReason))
            {
                RecordHeldAccessFixReject(contextReason);
                continue;
            }
            heldAccessFixAltManeuverCandidatesTotal++;

            long generation;
            lock (Sync)
            {
                generation = ++planGenerationCounter;
            }
            string planId = "tc_held_access_fix_"
                + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
                + "_" + Safe(group.OwnerProfileId)
                + "_" + generation.ToString(CultureInfo.InvariantCulture);
            DateTimeOffset sliceExpiry = now + PlanTtl;

            var plan = new ActiveHeldAccessFixPlan
            {
                PlanId = planId,
                OwnerThreatKey = ownerThreatKey,
                OwnerProfileId = group.OwnerProfileId,
                ThreatId = group.ThreatId,
                AssignmentIdentity = group.AssignmentIdentity,
                AlternateCandidateId = group.AlternateCandidateId,
                SelectedAltBotProfileId = group.SelectedAltBotProfileId,
                SelectedAltOperatorId = group.SelectedAltOperatorId,
                AltDevelopManeuverEpochId = context.AltDevelopManeuverEpochId,
                StableEstablishmentContinuationObserved = context.StableEstablishmentContinuation,
                DirectBotProfileIds = group.DirectBotProfileIds.ToArray(),
                ChokeId = context.Choke.ChokeId,
                ChokeConfidence = context.Choke.Confidence,
                PlanGeneration = generation,
                StartedAtUtc = now,
                ExpiresAtUtc = sliceExpiry,
            };

            lock (Sync)
            {
                if (ActiveHeldAccessFixByOwnerThreat.ContainsKey(ownerThreatKey))
                {
                    continue;
                }
                ActiveHeldAccessFixByOwnerThreat[ownerThreatKey] = plan;
            }

            int acquired = TryAcquireMissingHeldAccessFixMembers(plan, context, now);
            if (acquired <= 0)
            {
                lock (Sync)
                {
                    ActiveHeldAccessFixByOwnerThreat.Remove(ownerThreatKey);
                }
                RecordHeldAccessFixReject("no_direct_member_admitted");
                continue;
            }

            heldAccessFixPlansIssuedTotal++;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_HELD_ACCESS_FIX_PLAN_ACQUIRED owner={Safe(plan.OwnerProfileId)}; threat={Safe(plan.ThreatId)}; axis={Safe(plan.AlternateCandidateId)}; altBot={Safe(plan.SelectedAltBotProfileId)}; directTargets={plan.DirectBotProfileIds.Length}; heldMembers={acquired}; assignment={Safe(plan.AssignmentIdentity)}; choke={Safe(plan.ChokeId)}; chokeConfidence={Safe(plan.ChokeConfidence)}; plan={Safe(plan.PlanId)}; planGeneration={plan.PlanGeneration}; request={HeldAccessFixRequestKind}; opportunityProfile=strict_or_balanced; sensitiveProfileAllowed=false; heldAccessStable=true; splitOpportunityStable=true; directChokeWitness=exact_convergence_generation_subset; exactAltDevelopManeuverRequired=true; altManeuver={Safe(plan.AltDevelopManeuverEpochId)}; physicalArrivalObserved={BoolText(context.AltDevelopPhysicalArrivalObserved)}; stableEstablishmentContinuation={BoolText(context.StableEstablishmentContinuation)}; leaseSliceSeconds={PlanTtl.TotalSeconds:0.00}; holdLifetime=rolling_exact_split_state_until_synchronized_release_or_invalidation; doorState={Safe(context.Choke.StructuralDoor?.DoorState)}; doorExecutability={Safe(context.Choke.StructuralDoor?.RouteExecutability)}; nativeTraversalEligible={BoolText(context.Choke.StructuralDoor?.NativeTraversalEligible == true)}; vanguardDoorInteraction=false; physicalMode=shared_sain_search_translation_gate; contribution=HeldAccessDirectHold; selectedAltMovementMutation=false; directFireCommand=false; directAimCommand=false; targetMutation=false; sainSteeringPreserved=true; failOpen=true");
        }
    }

    private static int TryAcquireMissingHeldAccessFixMembers(
        ActiveHeldAccessFixPlan plan,
        HeldAccessFixContext context,
        DateTimeOffset now)
    {
        int acquired = 0;
        foreach (string botProfileId in plan.DirectBotProfileIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            lock (Sync)
            {
                if (ActiveHeldAccessFixMemberByBot.ContainsKey(botProfileId))
                {
                    continue;
                }
            }
            if (TryAcquireHeldAccessFixMember(plan, context, botProfileId, now, out string rejectReason))
            {
                acquired++;
            }
            else
            {
                RecordHeldAccessFixReject("member:" + rejectReason);
            }
        }
        return acquired;
    }

    private static bool TryAcquireHeldAccessFixMember(
        ActiveHeldAccessFixPlan plan,
        HeldAccessFixContext context,
        string botProfileId,
        DateTimeOffset now,
        out string reason)
    {
        reason = "unknown";
        if (string.IsNullOrWhiteSpace(botProfileId)
            || string.Equals(botProfileId, plan.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "invalid_direct_member_identity";
            return false;
        }
        if (HasAnyTcSearchTranslationGate(botProfileId) || HasActiveContributionForBot(botProfileId))
        {
            reason = "already_search_translation_gated";
            return false;
        }
        if (IsCooldownActive(botProfileId, now))
        {
            reason = "member_reacquire_cooldown";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(botProfileId, out OperatorDecisionSnapshot? snapshot)
            || snapshot == null
            || !snapshot.Alive)
        {
            reason = "decision_snapshot_missing_or_dead";
            return false;
        }
        string localGate = ValidateContributorCommon(snapshot, plan.ThreatId, now, allowOpportunityCombatCommit: false);
        if (localGate.Length > 0)
        {
            reason = "local_gate:" + localGate;
            return false;
        }
        DirectRouteObservation? route = context.ConvergenceGroup.Routes.FirstOrDefault(item =>
            string.Equals(item.BotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase)
            && item.HasUsableGeometry);
        if (route == null)
        {
            reason = "direct_route_missing";
            return false;
        }
        if (!TryBuildRouteProgress(route, snapshot, context.Choke, allowPastThreat: false, now, out RouteProgress progress, out string progressReason))
        {
            reason = "route_progress:" + progressReason;
            return false;
        }
        float outerApproach = context.Choke.SharedSegmentFarDistanceFromThreatMeters
            + CommitApproachPaddingMeters
            + SupportApproachExtraMeters;
        if (progress.DistanceFromThreatMeters > outerApproach)
        {
            reason = "outside_existing_choke_support_envelope";
            return false;
        }
        if (progress.DistanceFromThreatMeters < context.Choke.SharedSegmentNearDistanceFromThreatMeters - CommitClearedCorridorMeters)
        {
            reason = "already_past_shared_corridor";
            return false;
        }
        string pathGate = ValidateSainSearchPathPresent(botProfileId, out string pathSummary);
        if (pathGate.Length > 0)
        {
            reason = pathGate;
            return false;
        }
        if (!VanguardMainIntentScheduler.TryGrantTacticalCoordinatorCombatMovementSidecar(
                snapshot,
                now,
                plan.ThreatId,
                out string schedulerWindowId,
                out string schedulerReason))
        {
            reason = "scheduler:" + schedulerReason;
            return false;
        }
        if (VanguardReturnMovementCommandStore.TryGetActive(botProfileId, now, out var existingCommand))
        {
            reason = "existing_movement_command:" + Safe(existingCommand.RequestKind);
            return false;
        }

        var member = new ActiveHeldAccessFixMember
        {
            BotProfileId = botProfileId,
            PlanId = plan.PlanId,
            OwnerThreatKey = plan.OwnerThreatKey,
            OwnerProfileId = plan.OwnerProfileId,
            ThreatId = plan.ThreatId,
            ChokeId = plan.ChokeId,
            SchedulerWindowId = schedulerWindowId,
            PathSummary = pathSummary,
            StageAnchor = progress.LivePosition,
            StartedAtUtc = now,
            ExpiresAtUtc = plan.ExpiresAtUtc,
        };
        lock (Sync)
        {
            if (!ActiveHeldAccessFixByOwnerThreat.TryGetValue(plan.OwnerThreatKey, out ActiveHeldAccessFixPlan? parent)
                || !string.Equals(parent.PlanId, plan.PlanId, StringComparison.OrdinalIgnoreCase)
                || ActiveHeldAccessFixMemberByBot.ContainsKey(botProfileId))
            {
                reason = "parent_plan_changed_during_admission";
                return false;
            }
            ActiveHeldAccessFixMemberByBot[botProfileId] = member;
        }

        heldAccessFixMembersIssuedTotal++;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_HELD_ACCESS_FIX_MEMBER_ACQUIRED owner={Safe(plan.OwnerProfileId)}; threat={Safe(plan.ThreatId)}; axis={Safe(plan.AlternateCandidateId)}; altBot={Safe(plan.SelectedAltBotProfileId)}; holdBot={Safe(botProfileId)}; choke={Safe(plan.ChokeId)}; plan={Safe(plan.PlanId)}; schedulerWindow={Safe(schedulerWindowId)}; sainPath={Safe(pathSummary)}; threatDistance={progress.DistanceFromThreatMeters:0.00}; corridorNear={context.Choke.SharedSegmentNearDistanceFromThreatMeters:0.00}; corridorFar={context.Choke.SharedSegmentFarDistanceFromThreatMeters:0.00}; ttl={(plan.ExpiresAtUtc - now).TotalSeconds:0.00}; contribution=HeldAccessDirectHold; physicalMode=shared_sain_search_translation_gate; localCombatAuthority=SAIN; fireCommand=false; aimCommand=false; failOpen=true");
        reason = "ok";
        return true;
    }

    private static bool TryResolveHeldAccessFixContext(
        string assignmentIdentity,
        DateTimeOffset now,
        string expectedManeuverEpochId,
        bool establishedContinuationAlreadyProven,
        out HeldAccessFixContext context,
        out string reason)
    {
        context = null!;
        reason = "unknown";
        if (string.IsNullOrWhiteSpace(assignmentIdentity))
        {
            reason = "assignment_identity_missing";
            return false;
        }
        if (!ThreatExposureShadowClassificationService.TryGetSnapshot(out ThreatExposureShadowSnapshot shadow)
            || !shadow.IsAvailable)
        {
            reason = "shadow_snapshot_unavailable";
            return false;
        }
        ThreatExposureShadowGroupObservation? shadowGroup = shadow.Groups.FirstOrDefault(item =>
            string.Equals(item.AssignmentIdentity, assignmentIdentity, StringComparison.Ordinal));
        if (shadowGroup == null || shadowGroup.ContactExpiresAtUtc <= now)
        {
            reason = "shadow_group_missing_or_contact_expired";
            return false;
        }
        if (!TryGetHeldAccessFixOpportunityProfile(shadowGroup, out _))
        {
            reason = "strict_or_balanced_probe_not_stably_qualified";
            return false;
        }
        if (!RouteAssignmentService.TryGetSnapshot(out RouteAssignmentSnapshot assignmentSnapshot)
            || !assignmentSnapshot.IsAvailable
            || assignmentSnapshot.PublishedAtUtc != shadow.RouteAssignmentPublishedAtUtc)
        {
            reason = "route_assignment_not_current";
            return false;
        }
        ThreatGroupRouteAssignment? assignment = assignmentSnapshot.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, shadowGroup.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, shadowGroup.ThreatId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.AlternateCandidateId, shadowGroup.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.SelectedAltBotProfileId, shadowGroup.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.SelectedAltOperatorId, shadowGroup.SelectedAltOperatorId, StringComparison.OrdinalIgnoreCase));
        if (assignment == null
            || !assignment.SelectedAltPlanningViable
            || !assignment.SelectedPairPathProven
            || !DirectCompositionMatches(assignment.DirectBotProfileIds, shadowGroup.DirectBotProfileIds))
        {
            reason = "exact_route_assignment_missing_or_changed";
            return false;
        }

        if (!SplitFlankReadinessService.TryGetSnapshot(out SplitFlankReadinessSnapshot readinessSnapshot)
            || !readinessSnapshot.IsAvailable
            || readinessSnapshot.AssignmentPublishedAtUtc != assignmentSnapshot.PublishedAtUtc)
        {
            reason = "split_flank_readiness_not_current";
            return false;
        }
        ThreatGroupReadinessObservation? readinessGroup = readinessSnapshot.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, shadowGroup.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, shadowGroup.ThreatId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.AlternateCandidateId, shadowGroup.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.AlternateBotProfileId, shadowGroup.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase));
        if (readinessGroup == null
            || readinessGroup.AssignmentChanged
            || !readinessGroup.DependencyEnvelopeReady)
        {
            reason = "split_flank_dependency_not_current_or_ready";
            return false;
        }

        if (!SplitFlankStateService.TryGetSnapshot(out SplitFlankStateSnapshot splitStateSnapshot)
            || !splitStateSnapshot.IsAvailable
            || splitStateSnapshot.SourcePublishedAtUtc != readinessSnapshot.PublishedAtUtc)
        {
            reason = "split_flank_state_not_current";
            return false;
        }
        ThreatGroupSplitFlankStateObservation? splitStateGroup = splitStateSnapshot.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, shadowGroup.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, shadowGroup.ThreatId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.AlternateCandidateId, shadowGroup.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.AlternateBotProfileId, shadowGroup.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase));
        if (splitStateGroup == null
            || splitStateGroup.AssignmentChanged
            || !splitStateGroup.AlternateDevelopmentObserved
            || !splitStateGroup.AlternateHardSafetyReady
            || splitStateGroup.DirectCommitReadyCount <= 0)
        {
            reason = "split_flank_live_safety_not_ready";
            return false;
        }

        bool maneuverCurrent = AltDevelopMovementService.TryGetCurrentDevelopmentManeuverProof(
            assignment,
            now,
            out string maneuverEpochId,
            out bool physicalArrivalObserved,
            out AltDevelopEvidence developmentEvidence);
        bool stableEstablishmentContinuation = false;
        if (maneuverCurrent)
        {
            if (!string.IsNullOrWhiteSpace(expectedManeuverEpochId)
                && !string.Equals(maneuverEpochId, expectedManeuverEpochId, StringComparison.OrdinalIgnoreCase))
            {
                reason = "alt_develop_maneuver_epoch_changed";
                return false;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(expectedManeuverEpochId))
            {
                reason = "exact_alt_develop_maneuver_not_active";
                return false;
            }
            if (!splitStateGroup.PositionEstablishedObserved)
            {
                reason = "alt_develop_maneuver_ended_without_current_position_established";
                return false;
            }
            if (!establishedContinuationAlreadyProven
                && !AltDevelopMovementService.TryGetStableDevelopmentCompletionProof(
                    assignment,
                    expectedManeuverEpochId,
                    now,
                    out developmentEvidence))
            {
                reason = "stable_establishment_not_proven_for_expected_alt_maneuver";
                return false;
            }

            maneuverEpochId = expectedManeuverEpochId;
            physicalArrivalObserved = true;
            stableEstablishmentContinuation = true;
        }

        if (!TryGetCoherentInputs(out RouteConvergenceSnapshot convergenceSnapshot, out ChokeDetectionSnapshot chokeSnapshot, out RouteIndependenceSnapshot routeSnapshot)
            || convergenceSnapshot.PublishedAtUtc != shadow.ConvergencePublishedAtUtc
            || chokeSnapshot.PublishedAtUtc != shadow.ChokePublishedAtUtc
            || assignmentSnapshot.RoutePublishedAtUtc != routeSnapshot.PublishedAtUtc)
        {
            reason = "geometry_generation_not_current";
            return false;
        }
        ThreatGroupConvergenceAssessment? convergenceGroup = convergenceSnapshot.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, shadowGroup.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, shadowGroup.ThreatId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Classification, "shared_threat_paths_converged", StringComparison.Ordinal));
        if (convergenceGroup == null || convergenceGroup.Routes.Count < 2)
        {
            reason = "convergence_group_missing";
            return false;
        }
        if (shadowGroup.DirectBotProfileIds == null
            || shadowGroup.DirectBotProfileIds.Count == 0
            || shadowGroup.DirectBotProfileIds.Any(bot => !convergenceGroup.Routes.Any(route =>
                route.HasUsableGeometry && string.Equals(route.BotProfileId, bot, StringComparison.OrdinalIgnoreCase))))
        {
            reason = "direct_route_membership_not_proven";
            return false;
        }

        // ChokeDetection derives one shared segment from every DirectRoute in this exact owner/threat
        // convergence generation. HeldAccess is containment, not crossing authority: a shut structural
        // portal may therefore be held when the exact live link is SAIN-native traversable, while ordinary
        // ChokeCommit crossing remains under the stricter IsChokeTraversalReady gate below.
        ChokeObservation? choke = chokeSnapshot.Chokes
            .Where(item => string.Equals(item.OwnerProfileId, shadowGroup.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ThreatId, shadowGroup.ThreatId, StringComparison.OrdinalIgnoreCase)
                && item.RouteCount == convergenceGroup.Routes.Count
                && IsAdmissibleChoke(item)
                && IsHeldAccessContainmentReady(item, out _))
            .OrderByDescending(item => item.IsHardChoke && item.IsConfirmed)
            .ThenByDescending(item => item.ObservedRouteConvergenceRatio)
            .FirstOrDefault();
        if (choke == null)
        {
            reason = "exact_convergence_choke_not_containable";
            return false;
        }

        context = new HeldAccessFixContext(
            shadow,
            shadowGroup,
            assignment,
            readinessGroup,
            splitStateGroup,
            convergenceGroup,
            choke,
            developmentEvidence,
            maneuverEpochId,
            physicalArrivalObserved,
            stableEstablishmentContinuation);
        reason = stableEstablishmentContinuation
            ? "ok_stable_establishment_continuation"
            : "ok_current_alt_develop_maneuver";
        return true;
    }

    private static bool TryGetHeldAccessFixOpportunityProfile(
        ThreatExposureShadowGroupObservation group,
        out ThreatExposureShadowProfileObservation? profile)
    {
        profile = null;
        if (!group.ChokeEvidenceAvailable || !group.ChokePresent)
        {
            return false;
        }

        // Opportunity-first policy: keep the proven strict profile as first preference, but allow
        // the already-calibrated balanced profile to create the same bounded hold when both HeldAccess
        // and SplitOpportunity are stable. The sensitive probe remains observation-only.
        foreach (string profileId in new[] { HeldAccessFixStrictCalibrationProfile, HeldAccessFixBalancedCalibrationProfile })
        {
            ThreatExposureShadowProfileObservation? candidate = group.Profiles.FirstOrDefault(item =>
                string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
            if (candidate != null
                && candidate.HeldAccessCandidate
                && candidate.SplitOpportunityCandidate
                && candidate.HeldAccessStableQualified
                && candidate.SplitStableQualified)
            {
                profile = candidate;
                return true;
            }
        }
        return false;
    }

    private static bool DirectCompositionMatches(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        if (first == null || second == null || first.Count != second.Count)
        {
            return false;
        }
        var firstSet = new HashSet<string>(first.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
        var secondSet = new HashSet<string>(second.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
        return firstSet.Count == secondSet.Count && firstSet.SetEquals(secondSet);
    }

    private static bool IsHeldAccessFixCooldownActive(string ownerThreatKey, DateTimeOffset now)
    {
        lock (Sync)
        {
            if (!HeldAccessFixCooldownUntilByOwnerThreat.TryGetValue(ownerThreatKey, out DateTimeOffset until))
            {
                return false;
            }
            if (until <= now)
            {
                HeldAccessFixCooldownUntilByOwnerThreat.Remove(ownerThreatKey);
                return false;
            }
            return true;
        }
    }

    private static void RecordHeldAccessFixReject(string reason)
    {
        string key = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
        lock (Sync)
        {
            HeldAccessFixAdmissionRejectCounts.TryGetValue(key, out int count);
            HeldAccessFixAdmissionRejectCounts[key] = count + 1;
        }
    }


    private static void TickActivePlans(
        RouteConvergenceSnapshot convergenceSnapshot,
        ChokeDetectionSnapshot chokeSnapshot,
        RouteIndependenceSnapshot routeSnapshot,
        DateTimeOffset now)
    {
        ActiveChokeCommitSupportPlan[] plans;
        lock (Sync)
        {
            plans = ActiveByGroup.Values.ToArray();
        }

        foreach (var plan in plans)
        {
            if (now >= plan.ExpiresAtUtc)
            {
                ReleasePlan(plan, now, "ttl_complete", failOpen: false);
                continue;
            }

            string releaseReason = ValidateActivePlan(plan, convergenceSnapshot, chokeSnapshot, routeSnapshot, now);
            if (releaseReason.Length == 0)
            {
                continue;
            }

            bool completed = releaseReason.StartsWith("complete:", StringComparison.Ordinal);
            ReleasePlan(plan, now, completed ? releaseReason[9..] : releaseReason, failOpen: !completed);
        }
    }

    private static string ValidateActivePlan(
        ActiveChokeCommitSupportPlan plan,
        RouteConvergenceSnapshot convergenceSnapshot,
        ChokeDetectionSnapshot chokeSnapshot,
        RouteIndependenceSnapshot routeSnapshot,
        DateTimeOffset now)
    {
        if (!SearchTranslationPatchReady)
        {
            return "search_translation_patch_unavailable:" + Safe(SearchTranslationPatchState);
        }
        if (!InputIdentityMatches(plan, convergenceSnapshot, chokeSnapshot, routeSnapshot))
        {
            return "observation_generation_changed";
        }

        var choke = chokeSnapshot.Chokes.FirstOrDefault(item =>
            string.Equals(item.ChokeId, plan.ChokeId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.OwnerProfileId, plan.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, plan.ThreatId, StringComparison.OrdinalIgnoreCase));
        if (choke == null || !IsAdmissibleChoke(choke))
        {
            return "admissible_choke_lost";
        }
        if (!IsChokeTraversalReady(choke, out string traversalReason))
        {
            return "choke_traversal_changed:" + traversalReason;
        }

        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(plan.CommitBotProfileId, out var commit)
            || commit == null
            || !commit.Alive)
        {
            return "commit_operator_missing_or_dead";
        }
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(plan.SupportBotProfileId, out var support)
            || support == null
            || !support.Alive)
        {
            return "support_operator_missing_or_dead";
        }

        string commitGate = ValidateCommitContributorDuringInfluence(commit, plan.ThreatId, now);
        if (commitGate.Length > 0)
        {
            return "commit:" + commitGate;
        }
        string supportGate = ValidateSearchContributorDuringInfluence(support, plan.ThreatId, now);
        if (supportGate.Length > 0)
        {
            return "support:" + supportGate;
        }
        if (!MultiThreatOffensiveSafetyPolicy.IsSupportMateriallyReady(support, now, out string supportMaterialReason))
        {
            return "support_material_not_ready:" + Safe(supportMaterialReason);
        }
        if (!MultiThreatOffensiveSafetyPolicy.IsOffensiveTranslationSafe(
                commit,
                plan.ThreatId,
                "ChokeCommit",
                now,
                out string commitSafetyReason))
        {
            return "commit_multi_threat_safety:" + Safe(commitSafetyReason);
        }
        if (AntiCongaService.HasActiveAdvanceGateForBot(plan.SupportBotProfileId))
        {
            return "support_anti_conga_gate_became_active";
        }

        string commitPathGate = ValidateSainSearchPathPresent(commit.BotProfileId, out _);
        if (commitPathGate.Length > 0)
        {
            return "commit:" + commitPathGate;
        }
        string supportPathGate = ValidateSainSearchPathPresent(support.BotProfileId, out _);
        if (supportPathGate.Length > 0)
        {
            return "support:" + supportPathGate;
        }
        if (!VanguardMainIntentScheduler.IsTacticalCoordinatorCombatMovementSidecarCurrent(
                support.BotProfileId,
                now,
                plan.ThreatId,
                plan.SupportSchedulerWindowId,
                out string schedulerReason))
        {
            return "support_scheduler_sidecar_changed:" + schedulerReason;
        }
        if (!TryGetLivePosition(plan.SupportBotProfileId, out var supportLivePosition, out string supportPositionReason))
        {
            return "support_live_position_unavailable:" + supportPositionReason;
        }
        if (HorizontalDistance(supportLivePosition, plan.StageAnchor) > MaximumStageAnchorDriftMeters)
        {
            return "stage_anchor_drift";
        }

        var group = convergenceSnapshot.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, plan.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, plan.ThreatId, StringComparison.OrdinalIgnoreCase));
        if (group == null)
        {
            return "path_progress_group_missing";
        }
        var commitRoute = group.Routes.FirstOrDefault(item => string.Equals(item.BotProfileId, plan.CommitBotProfileId, StringComparison.OrdinalIgnoreCase));
        var supportRoute = group.Routes.FirstOrDefault(item => string.Equals(item.BotProfileId, plan.SupportBotProfileId, StringComparison.OrdinalIgnoreCase));
        if (commitRoute == null || supportRoute == null)
        {
            return "path_progress_route_missing";
        }
        if (!TryBuildRouteProgress(commitRoute, commit, choke, allowPastThreat: true, now, out var commitProgress, out string commitProgressReason))
        {
            return "path_progress_unavailable:" + Safe(commitProgressReason + "+");
        }
        if (!TryBuildRouteProgress(supportRoute, support, choke, allowPastThreat: true, now, out var supportProgress, out string supportProgressReason))
        {
            return "path_progress_unavailable:" + Safe(commitProgressReason + "+" + supportProgressReason);
        }

        if (commitProgress.DistanceFromThreatMeters <= choke.SharedSegmentNearDistanceFromThreatMeters - CommitClearedCorridorMeters)
        {
            return "complete:commit_cleared_shared_corridor";
        }

        float supportMaxDistanceFromThreat = choke.SharedSegmentFarDistanceFromThreatMeters
            + CommitApproachPaddingMeters
            + SupportApproachExtraMeters;
        if (supportProgress.DistanceFromThreatMeters > supportMaxDistanceFromThreat)
        {
            return "complete:support_left_corridor_participation_envelope";
        }

        float headway = supportProgress.DistanceFromThreatMeters - commitProgress.DistanceFromThreatMeters;
        if (headway < -0.10f)
        {
            return "path_ordering_flipped";
        }
        return string.Empty;
    }

    private static void TryAcquireOnePlanPerEligibleGroup(
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
                if (ActiveHeldAccessFixByOwnerThreat.ContainsKey(OwnerThreatKey(choke.OwnerProfileId, choke.ThreatId)))
                {
                    RecordAdmissionBlock("held_access_fix_physical_family_active");
                    continue;
                }
            }
            if (IsSynchronizedFlankReleaseSuppressed(choke.OwnerProfileId, choke.ThreatId, now))
            {
                synchronizedFlankAcquisitionSuppressedTotal++;
                RecordAdmissionBlock("synchronized_flank_release_gate_active");
                continue;
            }

            admissibleChokesEvaluated++;
            if (!IsChokeTraversalReady(choke, out string traversalReason))
            {
                RecordAdmissionBlock("choke_traversal_not_ready:" + traversalReason);
                continue;
            }

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

            bool routeWitness = routeSnapshot.ThreatGroups.Any(item =>
                string.Equals(item.OwnerProfileId, choke.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ThreatId, choke.ThreatId, StringComparison.OrdinalIgnoreCase));
            if (!routeWitness)
            {
                RecordAdmissionBlock("route_generation_witness_missing");
                continue;
            }
            routeWitnessesMatched++;

            float commitMaxDistanceFromThreat = choke.SharedSegmentFarDistanceFromThreatMeters + CommitApproachPaddingMeters;
            float supportMaxDistanceFromThreat = commitMaxDistanceFromThreat + SupportApproachExtraMeters;
            var projected = new List<RouteProgress>();
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
                if (progress.DistanceFromThreatMeters < choke.SharedSegmentNearDistanceFromThreatMeters - CommitClearedCorridorMeters)
                {
                    RecordAdmissionBlock("route_progress_past_shared_corridor");
                    continue;
                }

                projected.Add(progress);
                if (progress.DistanceFromThreatMeters > supportMaxDistanceFromThreat)
                {
                    RecordAdmissionBlock("route_progress_outside_support_window");
                    continue;
                }
                live.Add(progress);
            }

            RecordHeadwayOpportunityDistribution(
                group,
                choke,
                projected,
                commitMaxDistanceFromThreat,
                supportMaxDistanceFromThreat);

            if (live.Count < 2)
            {
                RecordAdmissionBlock("path_progress_candidates_lt2");
                continue;
            }
            liveCommitWindows++;

            if (!HasCorridorParticipationPair(group, choke, live, commitMaxDistanceFromThreat))
            {
                RecordAdmissionBlock("no_pair_in_corridor_participation_envelope");
                continue;
            }
            pairGeometryWindows++;

            if (!TrySelectCommitSupportPair(
                    group,
                    choke,
                    live,
                    commitMaxDistanceFromThreat,
                    now,
                    out var commit,
                    out var support,
                    out string supportSchedulerWindowId,
                    out string supportPathSummary,
                    out string pairReason))
            {
                RecordAdmissionBlock(pairReason);
                continue;
            }

            schedulerPairsReady++;
            string planId = BuildPlanId(convergenceSnapshot, choke, commit.Snapshot.BotProfileId, support.Snapshot.BotProfileId, now);
            long planGeneration;
            lock (Sync)
            {
                planGeneration = ++planGenerationCounter;
            }

            var plan = new ActiveChokeCommitSupportPlan
            {
                PlanId = planId,
                GroupKey = groupKey,
                OwnerProfileId = choke.OwnerProfileId,
                ThreatId = choke.ThreatId,
                ChokeId = choke.ChokeId,
                ChokeKind = choke.Kind,
                ChokeConfidence = choke.Confidence,
                CommitBotProfileId = commit.Snapshot.BotProfileId,
                SupportBotProfileId = support.Snapshot.BotProfileId,
                SupportSchedulerWindowId = supportSchedulerWindowId,
                SupportPathSummary = supportPathSummary,
                PlanGeneration = planGeneration,
                StageAnchor = support.LivePosition,
                StartedAtUtc = now,
                ExpiresAtUtc = now + PlanTtl,
                MapId = convergenceSnapshot.MapId,
                TopologyRevision = convergenceSnapshot.TopologyRevision,
                TopologyContentHash = convergenceSnapshot.TopologyContentHash,
                OperatorBatchAtUtc = convergenceSnapshot.OperatorBatchAtUtc,
                ConvergencePublishedAtUtc = convergenceSnapshot.PublishedAtUtc,
                ChokePublishedAtUtc = chokeSnapshot.PublishedAtUtc,
                RoutePublishedAtUtc = routeSnapshot.PublishedAtUtc,
            };

            lock (Sync)
            {
                ActiveByGroup[groupKey] = plan;
            }
            plansIssued++;
            TryAcquireAdditionalDirectHolds(
                plan,
                group,
                choke,
                live,
                commit,
                support,
                now);

            float headway = support.DistanceFromThreatMeters - commit.DistanceFromThreatMeters;
            IncrementHeadwayBucket(HeadwayPlansIssuedCounts, headway);
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_CHOKE_COMMIT_SUPPORT_PLAN_ACQUIRED owner={Safe(choke.OwnerProfileId)}; threat={Safe(choke.ThreatId)}; choke={Safe(choke.ChokeId)}; commitBot={Safe(commit.Snapshot.BotProfileId)}; supportBot={Safe(support.Snapshot.BotProfileId)}; commitContribution=DirectCommit; supportContribution=StageHold; additionalContribution=DirectHold; commitThreatDistance={commit.DistanceFromThreatMeters:0.00}; supportThreatDistance={support.DistanceFromThreatMeters:0.00}; headway={headway:0.00}; corridorNear={choke.SharedSegmentNearDistanceFromThreatMeters:0.00}; corridorFar={choke.SharedSegmentFarDistanceFromThreatMeters:0.00}; chokeKind={Safe(choke.Kind)}; chokeConfidence={Safe(choke.Confidence)}; plan={Safe(planId)}; planGeneration={planGeneration}; schedulerWindow={Safe(supportSchedulerWindowId)}; sainPath={Safe(supportPathSummary)}; ttl={PlanTtl.TotalSeconds:0.00}; request={RequestKind}; physicalMode=shared_sain_search_translation_gate; commitMovementMutation=false; supportTranslationOnly=true; additionalHoldsTranslationOnly=true; sainSteeringPreserved=true; suppressionCommand=false; routeAssignment=false; flank=false; failOpen=true");
        }
    }

    private static void TryAcquireAdditionalDirectHolds(
        ActiveChokeCommitSupportPlan plan,
        ThreatGroupConvergenceAssessment group,
        ChokeObservation choke,
        IReadOnlyList<RouteProgress> candidates,
        RouteProgress commit,
        RouteProgress support,
        DateTimeOffset now)
    {
        foreach (var candidate in candidates
            .Where(item =>
                !string.Equals(item.Snapshot.BotProfileId, commit.Snapshot.BotProfileId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.Snapshot.BotProfileId, support.Snapshot.BotProfileId, StringComparison.OrdinalIgnoreCase)
                && item.DistanceFromThreatMeters >= support.DistanceFromThreatMeters)
            .OrderBy(item => item.DistanceFromThreatMeters))
        {
            directHoldCandidatesEvaluated++;
            string botProfileId = candidate.Snapshot.BotProfileId;
            if (!IsConvergedPair(group, commit.Snapshot.BotProfileId, botProfileId))
            {
                RecordDirectHoldReject("pair_not_converged");
                continue;
            }
            if (HasAnyTcSearchTranslationGate(botProfileId) || HasActiveContributionForBot(botProfileId))
            {
                RecordDirectHoldReject("already_contributed");
                continue;
            }
            if (IsCooldownActive(botProfileId, now))
            {
                RecordDirectHoldReject("reacquire_cooldown");
                continue;
            }

            string localGate = ValidateSearchContributorForAdmission(candidate.Snapshot, choke.ThreatId, now);
            if (localGate.Length > 0)
            {
                RecordDirectHoldReject(localGate);
                continue;
            }
            string pathGate = ValidateSainSearchPathPresent(botProfileId, out string pathSummary);
            if (pathGate.Length > 0)
            {
                RecordDirectHoldReject(pathGate);
                continue;
            }
            if (!VanguardMainIntentScheduler.TryGrantTacticalCoordinatorCombatMovementSidecar(
                    candidate.Snapshot,
                    now,
                    choke.ThreatId,
                    out string schedulerWindowId,
                    out string schedulerReason))
            {
                RecordDirectHoldReject("scheduler:" + schedulerReason);
                continue;
            }
            if (VanguardReturnMovementCommandStore.TryGetActive(botProfileId, now, out var existingCommand))
            {
                RecordDirectHoldReject("existing_movement_command:" + Safe(existingCommand.RequestKind));
                continue;
            }

            directHoldCandidatesAdmissible++;
            var hold = new ActiveDirectHoldContribution
            {
                BotProfileId = botProfileId,
                PlanId = plan.PlanId,
                GroupKey = plan.GroupKey,
                OwnerProfileId = plan.OwnerProfileId,
                ThreatId = plan.ThreatId,
                ChokeId = plan.ChokeId,
                SchedulerWindowId = schedulerWindowId,
                PathSummary = pathSummary,
                StageAnchor = candidate.LivePosition,
                StartedAtUtc = now,
                ExpiresAtUtc = plan.ExpiresAtUtc,
            };

            lock (Sync)
            {
                if (ActiveDirectHoldByBot.ContainsKey(botProfileId))
                {
                    continue;
                }
                ActiveDirectHoldByBot[botProfileId] = hold;
            }
            directHoldsIssued++;

            float commitHeadway = candidate.DistanceFromThreatMeters - commit.DistanceFromThreatMeters;
            float supportHeadway = candidate.DistanceFromThreatMeters - support.DistanceFromThreatMeters;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_CHOKE_COMMIT_SUPPORT_DIRECT_HOLD_ACQUIRED owner={Safe(plan.OwnerProfileId)}; threat={Safe(plan.ThreatId)}; choke={Safe(plan.ChokeId)}; commitBot={Safe(plan.CommitBotProfileId)}; supportBot={Safe(plan.SupportBotProfileId)}; holdBot={Safe(botProfileId)}; contribution=DirectHold; commitHeadway={commitHeadway:0.00}; supportHeadway={supportHeadway:0.00}; plan={Safe(plan.PlanId)}; schedulerWindow={Safe(schedulerWindowId)}; sainPath={Safe(pathSummary)}; ttl={(plan.ExpiresAtUtc - now).TotalSeconds:0.00}; physicalMode=shared_sain_search_translation_gate; membership=same_crossing_upstream; memberLocalRelease=true; routeAssignment=false; flank=false; failOpen=true");
        }
    }

    private static bool HasCorridorParticipationPair(
        ThreatGroupConvergenceAssessment group,
        ChokeObservation choke,
        IReadOnlyList<RouteProgress> candidates,
        float commitMaxDistanceFromThreat)
    {
        foreach (var first in candidates)
        {
            if (first.DistanceFromThreatMeters > commitMaxDistanceFromThreat
                || first.DistanceFromThreatMeters < choke.SharedSegmentNearDistanceFromThreatMeters - CommitClearedCorridorMeters)
            {
                continue;
            }
            foreach (var second in candidates)
            {
                if (string.Equals(first.Snapshot.BotProfileId, second.Snapshot.BotProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                float headway = second.DistanceFromThreatMeters - first.DistanceFromThreatMeters;
                if (headway < StageParticipationMinimumHeadwayMeters)
                {
                    continue;
                }
                if (IsConvergedPair(group, first.Snapshot.BotProfileId, second.Snapshot.BotProfileId))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TrySelectCommitSupportPair(
        ThreatGroupConvergenceAssessment group,
        ChokeObservation choke,
        IReadOnlyList<RouteProgress> candidates,
        float commitMaxDistanceFromThreat,
        DateTimeOffset now,
        out RouteProgress commit,
        out RouteProgress support,
        out string supportSchedulerWindowId,
        out string supportPathSummary,
        out string reason)
    {
        commit = default;
        support = default;
        supportSchedulerWindowId = "none";
        supportPathSummary = "none";
        reason = "commit_support_pair_unavailable";
        PairCandidate? best = null;
        string dominantFailure = "no_pair_in_corridor_participation_envelope";

        foreach (var first in candidates.OrderBy(item => item.DistanceFromThreatMeters))
        {
            if (first.DistanceFromThreatMeters > commitMaxDistanceFromThreat)
            {
                continue;
            }
            if (first.DistanceFromThreatMeters < choke.SharedSegmentNearDistanceFromThreatMeters - CommitClearedCorridorMeters)
            {
                continue;
            }

            commitCandidatesEvaluated++;
            string commitGate = ValidateCommitContributorForAdmission(first.Snapshot, choke.ThreatId, now);
            if (commitGate.Length > 0)
            {
                dominantFailure = "commit_local_gate:" + commitGate;
                RecordCommitReject(commitGate);
                continue;
            }
            if (HasAnyTcSearchTranslationGate(first.Snapshot.BotProfileId))
            {
                dominantFailure = "commit_already_search_translation_gated";
                RecordCommitReject("already_search_translation_gated");
                continue;
            }
            if (HasActiveContributionForBot(first.Snapshot.BotProfileId))
            {
                dominantFailure = "commit_already_support_contribution";
                RecordCommitReject("already_commit_support_contribution");
                continue;
            }
            string commitPathGate = ValidateSainSearchPathPresent(first.Snapshot.BotProfileId, out _);
            if (commitPathGate.Length > 0)
            {
                dominantFailure = "commit_path_gate:" + commitPathGate;
                RecordCommitReject(commitPathGate);
                continue;
            }
            if (!VanguardMainIntentScheduler.TryGrantTacticalCoordinatorCombatMovementSidecar(
                    first.Snapshot,
                    now,
                    choke.ThreatId,
                    out _,
                    out string commitSchedulerReason))
            {
                dominantFailure = "commit_scheduler_gate:" + commitSchedulerReason;
                RecordCommitReject("scheduler:" + commitSchedulerReason);
                continue;
            }
            commitCandidatesAdmissible++;

            foreach (var second in candidates)
            {
                if (string.Equals(first.Snapshot.BotProfileId, second.Snapshot.BotProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                float headway = second.DistanceFromThreatMeters - first.DistanceFromThreatMeters;
                if (headway < StageParticipationMinimumHeadwayMeters)
                {
                    continue;
                }
                if (!IsConvergedPair(group, first.Snapshot.BotProfileId, second.Snapshot.BotProfileId))
                {
                    continue;
                }

                IncrementHeadwayBucket(HeadwayCommitReadyCounts, headway);
                supportCandidatesEvaluated++;
                if (HasAnyTcSearchTranslationGate(second.Snapshot.BotProfileId))
                {
                    dominantFailure = "support_already_search_translation_gated";
                    RecordSupportReject("already_search_translation_gated");
                    continue;
                }
                if (HasActiveContributionForBot(second.Snapshot.BotProfileId))
                {
                    dominantFailure = "support_already_support_contribution";
                    RecordSupportReject("already_commit_support_contribution");
                    continue;
                }
                if (IsCooldownActive(second.Snapshot.BotProfileId, now))
                {
                    dominantFailure = "support_reacquire_cooldown";
                    RecordSupportReject("reacquire_cooldown");
                    continue;
                }

                string supportGate = ValidateSearchContributorForAdmission(second.Snapshot, choke.ThreatId, now);
                if (supportGate.Length > 0)
                {
                    dominantFailure = "support_local_gate:" + supportGate;
                    RecordSupportReject(supportGate);
                    continue;
                }
                if (!MultiThreatOffensiveSafetyPolicy.IsSupportMateriallyReady(second.Snapshot, now, out string supportMaterialReason))
                {
                    dominantFailure = "support_material_not_ready:" + supportMaterialReason;
                    RecordSupportReject("material_not_ready:" + supportMaterialReason);
                    continue;
                }
                if (!MultiThreatOffensiveSafetyPolicy.IsOffensiveTranslationSafeWithProspectiveSupport(
                        first.Snapshot,
                        second.Snapshot,
                        choke.ThreatId,
                        "ChokeCommit",
                        now,
                        out string pairSafetyReason))
                {
                    dominantFailure = "commit_multi_threat_safety:" + pairSafetyReason;
                    RecordCommitReject("multi_threat_safety:" + pairSafetyReason);
                    continue;
                }
                string supportPathGate = ValidateSainSearchPathPresent(second.Snapshot.BotProfileId, out string pathSummary);
                if (supportPathGate.Length > 0)
                {
                    dominantFailure = "support_path_gate:" + supportPathGate;
                    RecordSupportReject(supportPathGate);
                    continue;
                }
                if (!VanguardMainIntentScheduler.TryGrantTacticalCoordinatorCombatMovementSidecar(
                        second.Snapshot,
                        now,
                        choke.ThreatId,
                        out string schedulerWindowId,
                        out string supportSchedulerReason))
                {
                    dominantFailure = "support_scheduler_gate:" + supportSchedulerReason;
                    RecordSupportReject("scheduler:" + supportSchedulerReason);
                    continue;
                }
                if (VanguardReturnMovementCommandStore.TryGetActive(second.Snapshot.BotProfileId, now, out var existingCommand))
                {
                    dominantFailure = "support_existing_movement_command:" + Safe(existingCommand.RequestKind);
                    RecordSupportReject("existing_movement_command:" + Safe(existingCommand.RequestKind));
                    continue;
                }
                supportCandidatesAdmissible++;
                IncrementHeadwayBucket(HeadwaySupportReadyCounts, headway);

                var pair = new PairCandidate(first, second, headway, schedulerWindowId, pathSummary);
                if (best == null || IsPreferredPair(pair, best.Value))
                {
                    best = pair;
                }
            }
        }

        if (best == null)
        {
            reason = dominantFailure;
            return false;
        }

        commit = best.Value.Commit;
        support = best.Value.Support;
        supportSchedulerWindowId = best.Value.SupportSchedulerWindowId;
        supportPathSummary = best.Value.SupportPathSummary;
        reason = "ok";
        return true;
    }

    private static bool IsPreferredPair(PairCandidate candidate, PairCandidate current)
    {
        // Prefer the most advanced valid commit member. For equal progress, prefer the nearest
        // eligible upstream support member. Distance is therefore a preference, not episode identity.
        return candidate.Commit.DistanceFromThreatMeters < current.Commit.DistanceFromThreatMeters
            || (Mathf.Approximately(candidate.Commit.DistanceFromThreatMeters, current.Commit.DistanceFromThreatMeters)
                && candidate.HeadwayMeters < current.HeadwayMeters);
    }

    private static bool HasAnyTcSearchTranslationGate(string botProfileId)
        => AntiCongaService.HasActiveAdvanceGateForBot(botProfileId)
            || HasActiveStageHoldForBot(botProfileId);

    private static string ValidateCommitContributorForAdmission(OperatorDecisionSnapshot snapshot, string threatId, DateTimeOffset now)
    {
        string common = ValidateContributorCommon(snapshot, threatId, now, allowOpportunityCombatCommit: true);
        if (common.Length > 0)
        {
            return common;
        }
        if (snapshot.RealSpeed < MinimumMovingSpeedForAdmission)
        {
            return "commit_native_movement_not_observed";
        }
        return string.Empty;
    }

    private static string ValidateCommitContributorDuringInfluence(OperatorDecisionSnapshot snapshot, string threatId, DateTimeOffset now)
    {
        string common = ValidateContributorCommon(snapshot, threatId, now, allowOpportunityCombatCommit: true);
        if (common.Length > 0)
        {
            return common;
        }
        return string.Empty;
    }

    private static string ValidateSearchContributorForAdmission(OperatorDecisionSnapshot snapshot, string threatId, DateTimeOffset now)
    {
        string common = ValidateContributorCommon(snapshot, threatId, now, allowOpportunityCombatCommit: false);
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

    private static string ValidateSearchContributorDuringInfluence(OperatorDecisionSnapshot snapshot, string threatId, DateTimeOffset now)
    {
        string common = ValidateContributorCommon(snapshot, threatId, now, allowOpportunityCombatCommit: false);
        if (common.Length > 0)
        {
            return common;
        }
        return string.Empty;
    }

    private static string ValidateContributorCommon(OperatorDecisionSnapshot snapshot, string threatId, DateTimeOffset now, bool allowOpportunityCombatCommit)
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
            if (!allowOpportunityCombatCommit
                || !searchContextReason.StartsWith("sain_action_not_search:", StringComparison.OrdinalIgnoreCase)
                || !IsOpportunityCombatCommitAction(snapshot.Sain.CurrentAction))
            {
                return searchContextReason;
            }

            float? threatDistance = snapshot.Threat.Distance;
            if (snapshot.Threat.EnemyVisible == true
                && threatDistance.HasValue
                && threatDistance.Value >= 0f
                && threatDistance.Value <= AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters)
            {
                return "close_visible_threat_survival_precedes_choke_commit";
            }
        }
        if (snapshot.Sain.HasEnemy != true || snapshot.Sain.IsInCombat != true)
        {
            return "sain_combat_context_missing";
        }
        return string.Empty;
    }

    private static bool IsOpportunityCombatCommitAction(string? value)
    {
        string token = TacticalSearchPolicy.NormalizeToken(value);
        return string.Equals(token, "SeekCoverAction", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "StandAndShootAction", StringComparison.OrdinalIgnoreCase);
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
        object? activePath = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "ActivePath");
        if (activePath == null)
        {
            summary = "activePath=null";
            return "sain_search_path_missing";
        }

        string stateGate = SearchTranslationGateBridge.ValidateSainManualPathGateState(activePath, out string pathStateSummary);
        summary = "activePath=" + Safe(VanguardOperatorRuntimeAuditReflection.TypeName(activePath)) + ":" + pathStateSummary;
        return stateGate;
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
        if (!TryProjectOntoRoute(route.Corners, livePosition, out var projection))
        {
            reason = "operator_projection_failed";
            return false;
        }
        if (projection.CrossTrackMeters > MaximumOperatorRouteCrossTrackMeters)
        {
            reason = "operator_cross_track_exceeded";
            return false;
        }

        float distanceFromThreat = Mathf.Max(0f, projection.TotalLengthMeters - projection.AlongMeters);
        if (!allowPastThreat && distanceFromThreat < MinimumBeforeThreatMeters)
        {
            reason = "operator_at_threat_endpoint";
            return false;
        }

        progress = new RouteProgress(route, snapshot, livePosition, projection.CrossTrackMeters, distanceFromThreat);
        reason = "ok";
        return true;
    }

    private static bool TryProjectOntoRoute(IReadOnlyList<Vector3> corners, Vector3 point, out RouteProjection projection)
    {
        projection = default;
        if (corners == null || corners.Count < 2)
        {
            return false;
        }

        float bestCrossTrack = float.MaxValue;
        float bestAlong = 0f;
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
            }
            cumulative += length;
        }

        if (!found)
        {
            return false;
        }
        projection = new RouteProjection(bestAlong, bestCrossTrack, cumulative);
        return true;
    }

    private static bool IsConvergedPair(ThreatGroupConvergenceAssessment group, string firstBotProfileId, string secondBotProfileId)
    {
        return group.Pairs.Any(pair =>
            ((string.Equals(pair.FirstBotProfileId, firstBotProfileId, StringComparison.OrdinalIgnoreCase)
              && string.Equals(pair.SecondBotProfileId, secondBotProfileId, StringComparison.OrdinalIgnoreCase))
             || (string.Equals(pair.FirstBotProfileId, secondBotProfileId, StringComparison.OrdinalIgnoreCase)
                 && string.Equals(pair.SecondBotProfileId, firstBotProfileId, StringComparison.OrdinalIgnoreCase)))
            && string.Equals(pair.Classification, "converged", StringComparison.Ordinal));
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

    private static bool IsHeldAccessContainmentReady(ChokeObservation choke, out string reason)
    {
        reason = "dynamic_choke_no_structural_door";
        if (choke.StructuralDoor == null)
        {
            return true;
        }

        string executability = choke.StructuralDoor.RouteExecutability ?? string.Empty;
        if (string.Equals(executability, "structural_passage_open_or_breached", StringComparison.OrdinalIgnoreCase))
        {
            reason = "structural_passage_open_or_breached";
            return true;
        }
        if (string.Equals(executability, "locked_requires_route_revalidation", StringComparison.OrdinalIgnoreCase))
        {
            reason = "locked";
            return false;
        }
        if (string.Equals(executability, "structural_passage_closed_native_traversal_required", StringComparison.OrdinalIgnoreCase))
        {
            if (choke.StructuralDoor.NativeTraversalEligible)
            {
                reason = "closed_native_traversable_containment_only";
                return true;
            }

            reason = "closed_native_door_not_eligible";
            return false;
        }

        reason = "structural_state_ambiguous:" + Safe(executability);
        return false;
    }

    private static bool IsChokeTraversalReady(ChokeObservation choke, out string reason)
    {
        reason = "dynamic_choke_no_structural_door";
        if (choke.StructuralDoor == null)
        {
            return true;
        }

        string executability = choke.StructuralDoor.RouteExecutability ?? string.Empty;
        if (string.Equals(executability, "structural_passage_open_or_breached", StringComparison.OrdinalIgnoreCase))
        {
            reason = "structural_passage_open_or_breached";
            return true;
        }
        if (string.Equals(executability, "locked_requires_route_revalidation", StringComparison.OrdinalIgnoreCase))
        {
            reason = "locked";
            return false;
        }
        if (string.Equals(executability, "structural_passage_closed_native_traversal_required", StringComparison.OrdinalIgnoreCase))
        {
            reason = "closed_native_door_interaction_required";
            return false;
        }

        // Unknown structural state is not sufficient authority for a bounded crossing plan.
        // Dynamic inferred chokes without a structural door remain admissible above.
        reason = "structural_state_ambiguous:" + Safe(executability);
        return false;
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
        ActiveChokeCommitSupportPlan plan,
        RouteConvergenceSnapshot convergenceSnapshot,
        ChokeDetectionSnapshot chokeSnapshot,
        RouteIndependenceSnapshot routeSnapshot)
    {
        return string.Equals(plan.MapId, convergenceSnapshot.MapId, StringComparison.OrdinalIgnoreCase)
            && plan.TopologyRevision == convergenceSnapshot.TopologyRevision
            && string.Equals(plan.TopologyContentHash, convergenceSnapshot.TopologyContentHash, StringComparison.Ordinal)
            && plan.OperatorBatchAtUtc == convergenceSnapshot.OperatorBatchAtUtc
            && plan.ConvergencePublishedAtUtc == convergenceSnapshot.PublishedAtUtc
            && plan.ChokePublishedAtUtc == chokeSnapshot.PublishedAtUtc
            && plan.RoutePublishedAtUtc == routeSnapshot.PublishedAtUtc;
    }

    internal static int ForgetRuntimePresence(
        string botProfileId,
        string reason,
        out int removedDirectHoldCount)
    {
        removedDirectHoldCount = 0;
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return 0;
        }

        int removedPlans = 0;
        lock (Sync)
        {
            foreach (ActiveChokeCommitSupportPlan plan in ActiveByGroup.Values
                .Where(item => string.Equals(item.CommitBotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.SupportBotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase))
                .ToArray())
            {
                if (!ActiveByGroup.Remove(plan.GroupKey))
                {
                    continue;
                }

                removedPlans++;
                foreach (ActiveDirectHoldContribution hold in ActiveDirectHoldByBot.Values
                    .Where(item => string.Equals(item.PlanId, plan.PlanId, StringComparison.OrdinalIgnoreCase))
                    .ToArray())
                {
                    if (ActiveDirectHoldByBot.Remove(hold.BotProfileId))
                    {
                        removedDirectHoldCount++;
                    }
                }
            }

            // If the disappearing bot was only a DirectHold trailer, remove only its own contribution.
            if (ActiveDirectHoldByBot.Remove(botProfileId))
            {
                removedDirectHoldCount++;
            }

            CooldownUntilBySupportBot.Remove(botProfileId);
        }

        ActiveHeldAccessFixPlan[] heldAccessPresencePlans;
        lock (Sync)
        {
            heldAccessPresencePlans = ActiveHeldAccessFixByOwnerThreat.Values
                .Where(item => string.Equals(item.SelectedAltBotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase)
                    || item.DirectBotProfileIds.Any(value => string.Equals(value, botProfileId, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
        foreach (ActiveHeldAccessFixPlan heldPlan in heldAccessPresencePlans)
        {
            int linkedMembers;
            lock (Sync)
            {
                linkedMembers = ActiveHeldAccessFixMemberByBot.Values.Count(item =>
                    string.Equals(item.PlanId, heldPlan.PlanId, StringComparison.OrdinalIgnoreCase));
            }
            if (ReleaseHeldAccessFixPlan(heldPlan, DateTimeOffset.UtcNow, "runtime_presence_lost:" + Safe(reason), failOpen: true, applyCooldown: false))
            {
                removedPlans++;
                removedDirectHoldCount += linkedMembers;
            }
        }

        if (removedPlans > 0 || removedDirectHoldCount > 0)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_CHOKE_COMMIT_SUPPORT_RUNTIME_PRESENCE_FORGOTTEN bot={Safe(botProfileId)}; plansRemoved={removedPlans}; directHoldsRemoved={removedDirectHoldCount}; reason={Safe(reason)}; normalRelease=false; survivorCooldown=false; staleResume=false");
        }
        return removedPlans;
    }

    internal static int ReleaseForHigherAuthority(
        string botProfileId,
        DateTimeOffset now,
        string reason,
        out int releasedDirectHoldCount)
    {
        releasedDirectHoldCount = 0;
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return 0;
        }

        ActiveChokeCommitSupportPlan[] matchingPlans;
        lock (Sync)
        {
            matchingPlans = ActiveByGroup.Values
                .Where(item => string.Equals(item.CommitBotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.SupportBotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        int releasedPlans = 0;
        foreach (var plan in matchingPlans)
        {
            int linkedDirectHolds;
            lock (Sync)
            {
                linkedDirectHolds = ActiveDirectHoldByBot.Values.Count(item =>
                    string.Equals(item.PlanId, plan.PlanId, StringComparison.OrdinalIgnoreCase));
            }
            if (!ReleasePlan(plan, now, "higher_authority:" + Safe(reason), failOpen: true))
            {
                continue;
            }
            releasedPlans++;
            releasedDirectHoldCount += linkedDirectHolds;
        }

        // A DirectHold is member-local. If this bot was only a held trailer rather than the
        // commit/support pair, release exactly that contribution without destroying the rest of
        // the still-valid shared plan.
        ActiveDirectHoldContribution? directHold = null;
        lock (Sync)
        {
            if (ActiveDirectHoldByBot.TryGetValue(botProfileId, out var currentHold))
            {
                directHold = currentHold;
            }
        }
        if (directHold != null)
        {
            ReleaseDirectHold(directHold, now, "higher_authority:" + Safe(reason), failOpen: true);
            releasedDirectHoldCount++;
        }

        ActiveHeldAccessFixPlan[] heldAccessAltPlans;
        ActiveHeldAccessFixMember? heldAccessMember = null;
        lock (Sync)
        {
            heldAccessAltPlans = ActiveHeldAccessFixByOwnerThreat.Values
                .Where(item => string.Equals(item.SelectedAltBotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            ActiveHeldAccessFixMemberByBot.TryGetValue(botProfileId, out heldAccessMember);
        }
        foreach (ActiveHeldAccessFixPlan heldPlan in heldAccessAltPlans)
        {
            int linkedMembers;
            lock (Sync)
            {
                linkedMembers = ActiveHeldAccessFixMemberByBot.Values.Count(item =>
                    string.Equals(item.PlanId, heldPlan.PlanId, StringComparison.OrdinalIgnoreCase));
            }
            if (ReleaseHeldAccessFixPlan(heldPlan, now, "higher_authority_selected_alt:" + Safe(reason), failOpen: true, applyCooldown: true))
            {
                releasedPlans++;
                releasedDirectHoldCount += linkedMembers;
            }
        }
        if (heldAccessMember != null)
        {
            ReleaseHeldAccessFixMember(heldAccessMember, now, "higher_authority:" + Safe(reason), failOpen: true, applyCooldown: true);
            releasedDirectHoldCount++;
        }

        if (releasedPlans > 0 || releasedDirectHoldCount > 0)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_CHOKE_COMMIT_SUPPORT_HIGHER_AUTHORITY_RELEASE bot={Safe(botProfileId)}; plansReleased={releasedPlans}; directHoldsReleased={releasedDirectHoldCount}; reason={Safe(reason)}; staleResume=false; releaseOnly=true; movementStart=false; fireCommand=false; sainMutation=false");
        }
        return releasedPlans;
    }

    private static void ReleaseAll(DateTimeOffset now, string reason, bool failOpen)
    {
        ReleaseAllHeldAccessFix(now, reason, failOpen, applyCooldown: false);
        ActiveChokeCommitSupportPlan[] plans;
        lock (Sync)
        {
            plans = ActiveByGroup.Values.ToArray();
        }
        foreach (var plan in plans)
        {
            ReleasePlan(plan, now, reason, failOpen);
        }
    }

    private static bool ReleasePlan(ActiveChokeCommitSupportPlan plan, DateTimeOffset now, string reason, bool failOpen)
    {
        bool released;
        ActiveDirectHoldContribution[] linkedDirectHolds;
        lock (Sync)
        {
            released = ActiveByGroup.TryGetValue(plan.GroupKey, out var active)
                && string.Equals(active.PlanId, plan.PlanId, StringComparison.OrdinalIgnoreCase);
            linkedDirectHolds = released
                ? ActiveDirectHoldByBot.Values
                    .Where(item => string.Equals(item.PlanId, plan.PlanId, StringComparison.OrdinalIgnoreCase))
                    .ToArray()
                : Array.Empty<ActiveDirectHoldContribution>();
            if (released)
            {
                ActiveByGroup.Remove(plan.GroupKey);
                CooldownUntilBySupportBot[plan.SupportBotProfileId] = now + ReacquireCooldown;
                foreach (var hold in linkedDirectHolds)
                {
                    ActiveDirectHoldByBot.Remove(hold.BotProfileId);
                    CooldownUntilBySupportBot[hold.BotProfileId] = now + ReacquireCooldown;
                }
            }
        }
        if (!released)
        {
            return false;
        }

        if (failOpen)
        {
            failOpenReleases++;
            directHoldFailOpenReleases += linkedDirectHolds.Length;
        }
        else
        {
            completions++;
            directHoldCompletions += linkedDirectHolds.Length;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_CHOKE_COMMIT_SUPPORT_PLAN_RELEASED owner={Safe(plan.OwnerProfileId)}; threat={Safe(plan.ThreatId)}; choke={Safe(plan.ChokeId)}; commitBot={Safe(plan.CommitBotProfileId)}; supportBot={Safe(plan.SupportBotProfileId)}; plan={Safe(plan.PlanId)}; planGeneration={plan.PlanGeneration}; schedulerWindow={Safe(plan.SupportSchedulerWindowId)}; directHoldsReleased={linkedDirectHolds.Length}; age={(now - plan.StartedAtUtc).TotalSeconds:0.00}; outcome={(failOpen ? "fail_open" : "completed")}; reason={Safe(reason)}; physicalMode=shared_sain_search_translation_gate; staleResume=false; cooldown={ReacquireCooldown.TotalSeconds:0.00}");
        return true;
    }

    private static void ReleaseDirectHold(
        ActiveDirectHoldContribution hold,
        DateTimeOffset now,
        string reason,
        bool failOpen)
    {
        bool released;
        lock (Sync)
        {
            released = ActiveDirectHoldByBot.TryGetValue(hold.BotProfileId, out var active)
                && string.Equals(active.PlanId, hold.PlanId, StringComparison.OrdinalIgnoreCase);
            if (released)
            {
                ActiveDirectHoldByBot.Remove(hold.BotProfileId);
                CooldownUntilBySupportBot[hold.BotProfileId] = now + ReacquireCooldown;
            }
        }
        if (!released)
        {
            return;
        }

        if (failOpen)
        {
            directHoldFailOpenReleases++;
        }
        else
        {
            directHoldCompletions++;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_CHOKE_COMMIT_SUPPORT_DIRECT_HOLD_RELEASED owner={Safe(hold.OwnerProfileId)}; threat={Safe(hold.ThreatId)}; choke={Safe(hold.ChokeId)}; holdBot={Safe(hold.BotProfileId)}; plan={Safe(hold.PlanId)}; schedulerWindow={Safe(hold.SchedulerWindowId)}; age={(now - hold.StartedAtUtc).TotalSeconds:0.00}; outcome={(failOpen ? "fail_open" : "completed")}; reason={Safe(reason)}; physicalMode=shared_sain_search_translation_gate; staleResume=false; cooldown={ReacquireCooldown.TotalSeconds:0.00}");
    }

    private static int ReleaseHeldAccessFixForOwnerThreat(
        string ownerProfileId,
        string threatId,
        DateTimeOffset now,
        string reason,
        bool failOpen,
        bool applyCooldown,
        out int releasedMemberCount)
    {
        releasedMemberCount = 0;
        ActiveHeldAccessFixPlan[] plans;
        lock (Sync)
        {
            plans = ActiveHeldAccessFixByOwnerThreat.Values
                .Where(item => string.Equals(item.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.ThreatId, threatId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        int releasedPlans = 0;
        foreach (ActiveHeldAccessFixPlan plan in plans)
        {
            int linkedMembers;
            lock (Sync)
            {
                linkedMembers = ActiveHeldAccessFixMemberByBot.Values.Count(item =>
                    string.Equals(item.PlanId, plan.PlanId, StringComparison.OrdinalIgnoreCase));
            }
            if (!ReleaseHeldAccessFixPlan(plan, now, reason, failOpen, applyCooldown))
            {
                continue;
            }
            releasedPlans++;
            releasedMemberCount += linkedMembers;
        }
        return releasedPlans;
    }

    private static void ReleaseAllHeldAccessFix(
        DateTimeOffset now,
        string reason,
        bool failOpen,
        bool applyCooldown)
    {
        ActiveHeldAccessFixPlan[] plans;
        lock (Sync)
        {
            plans = ActiveHeldAccessFixByOwnerThreat.Values.ToArray();
        }
        foreach (ActiveHeldAccessFixPlan plan in plans)
        {
            ReleaseHeldAccessFixPlan(plan, now, reason, failOpen, applyCooldown);
        }
    }

    private static bool ReleaseHeldAccessFixPlan(
        ActiveHeldAccessFixPlan plan,
        DateTimeOffset now,
        string reason,
        bool failOpen,
        bool applyCooldown)
    {
        ActiveHeldAccessFixMember[] linkedMembers;
        bool released;
        lock (Sync)
        {
            released = ActiveHeldAccessFixByOwnerThreat.TryGetValue(plan.OwnerThreatKey, out ActiveHeldAccessFixPlan? active)
                && string.Equals(active.PlanId, plan.PlanId, StringComparison.OrdinalIgnoreCase);
            linkedMembers = released
                ? ActiveHeldAccessFixMemberByBot.Values
                    .Where(item => string.Equals(item.PlanId, plan.PlanId, StringComparison.OrdinalIgnoreCase))
                    .ToArray()
                : Array.Empty<ActiveHeldAccessFixMember>();
            if (released)
            {
                ActiveHeldAccessFixByOwnerThreat.Remove(plan.OwnerThreatKey);
                foreach (ActiveHeldAccessFixMember member in linkedMembers)
                {
                    ActiveHeldAccessFixMemberByBot.Remove(member.BotProfileId);
                    if (applyCooldown)
                    {
                        CooldownUntilBySupportBot[member.BotProfileId] = now + ReacquireCooldown;
                    }
                }
                if (applyCooldown)
                {
                    HeldAccessFixCooldownUntilByOwnerThreat[plan.OwnerThreatKey] = now + ReacquireCooldown;
                }
            }
        }
        if (!released)
        {
            return false;
        }

        if (failOpen)
        {
            heldAccessFixPlanFailOpenReleasesTotal++;
            heldAccessFixMemberFailOpenReleasesTotal += linkedMembers.Length;
        }
        else
        {
            heldAccessFixPlanCompletionsTotal++;
        }
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_HELD_ACCESS_FIX_PLAN_RELEASED owner={Safe(plan.OwnerProfileId)}; threat={Safe(plan.ThreatId)}; axis={Safe(plan.AlternateCandidateId)}; altBot={Safe(plan.SelectedAltBotProfileId)}; choke={Safe(plan.ChokeId)}; plan={Safe(plan.PlanId)}; membersReleased={linkedMembers.Length}; age={(now - plan.StartedAtUtc).TotalSeconds:0.00}; outcome={(failOpen ? "fail_open" : "completed")}; reason={Safe(reason)}; cooldown={(applyCooldown ? ReacquireCooldown.TotalSeconds : 0d):0.00}; staleResume=false; movementStart=false; fireCommand=false; sainMutation=false");
        return true;
    }

    private static void ReleaseHeldAccessFixMember(
        ActiveHeldAccessFixMember member,
        DateTimeOffset now,
        string reason,
        bool failOpen,
        bool applyCooldown)
    {
        bool released;
        lock (Sync)
        {
            released = ActiveHeldAccessFixMemberByBot.TryGetValue(member.BotProfileId, out ActiveHeldAccessFixMember? active)
                && string.Equals(active.PlanId, member.PlanId, StringComparison.OrdinalIgnoreCase);
            if (released)
            {
                ActiveHeldAccessFixMemberByBot.Remove(member.BotProfileId);
                if (applyCooldown)
                {
                    CooldownUntilBySupportBot[member.BotProfileId] = now + ReacquireCooldown;
                }
            }
        }
        if (!released)
        {
            return;
        }
        if (failOpen)
        {
            heldAccessFixMemberFailOpenReleasesTotal++;
        }
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_HELD_ACCESS_FIX_MEMBER_RELEASED owner={Safe(member.OwnerProfileId)}; threat={Safe(member.ThreatId)}; choke={Safe(member.ChokeId)}; holdBot={Safe(member.BotProfileId)}; plan={Safe(member.PlanId)}; age={(now - member.StartedAtUtc).TotalSeconds:0.00}; outcome={(failOpen ? "fail_open" : "completed")}; reason={Safe(reason)}; memberLocal=true; staleResume=false");
    }


    private static bool IsSynchronizedFlankReleaseSuppressed(string ownerProfileId, string threatId, DateTimeOffset now)
    {
        string key = OwnerThreatKey(ownerProfileId, threatId);
        lock (Sync)
        {
            if (!SynchronizedFlankReleaseUntilByOwnerThreat.TryGetValue(key, out var until))
            {
                return false;
            }
            if (until <= now)
            {
                SynchronizedFlankReleaseUntilByOwnerThreat.Remove(key);
                return false;
            }
            return true;
        }
    }

    private static bool IsCooldownActive(string botProfileId, DateTimeOffset now)
    {
        lock (Sync)
        {
            if (!CooldownUntilBySupportBot.TryGetValue(botProfileId, out var until))
            {
                return false;
            }
            if (until <= now)
            {
                CooldownUntilBySupportBot.Remove(botProfileId);
                return false;
            }
            return true;
        }
    }

    private static void Publish(RouteConvergenceSnapshot convergenceSnapshot, DateTimeOffset now)
    {
        ChokeCommitSupportPlanObservation[] observations;
        lock (Sync)
        {
            observations = ActiveByGroup.Values
                .OrderBy(item => item.GroupKey, StringComparer.OrdinalIgnoreCase)
                .Select(item => new ChokeCommitSupportPlanObservation(
                    item.PlanId,
                    item.OwnerProfileId,
                    item.ThreatId,
                    item.ChokeId,
                    item.CommitBotProfileId,
                    item.SupportBotProfileId,
                    ActiveDirectHoldByBot.Values
                        .Where(hold => string.Equals(hold.PlanId, item.PlanId, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(hold => hold.BotProfileId, StringComparer.OrdinalIgnoreCase)
                        .Select(hold => hold.BotProfileId)
                        .ToArray(),
                    item.SupportSchedulerWindowId,
                    item.PlanGeneration,
                    item.StageAnchor,
                    item.StartedAtUtc,
                    item.ExpiresAtUtc,
                    "DirectCommit",
                    "StageHold",
                    "active_commit_support"))
                .ToArray();
            current = new ChokeCommitSupportSnapshot(
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
                $"TC_CHOKE_COMMIT_SUPPORT_BOOT enabled=true; mode=choke_commit_support_corridor_participation+held_access_fix; contributions=DirectCommit+StageHold+DirectHold+HeldAccessDirectHold; directCommitMovementMutation=false; stageHoldPhysicalFamily=shared_sain_search_translation_gate; directHoldPhysicalFamily=shared_sain_search_translation_gate; sharedWithAntiConga=true; onePlanPerOwnerThreatChoke=true; ttl={PlanTtl.TotalSeconds:0.00}; acquisitionCadenceSeconds={AcquisitionInterval.TotalSeconds:0.00}; stageHeadwayMin={StageParticipationMinimumHeadwayMeters:0.00}; stageHeadwayMax=none_pairwise; stageHeadwayRelease=ttl_or_passage_exit; supportEnvelopeExtra={SupportApproachExtraMeters:0.00}; commitApproachPadding={CommitApproachPaddingMeters:0.00}; corridorParticipation=true; directHoldMembership=same_crossing_upstream; headwayBuckets=0_4,4_6p5,6p5_10,10_14,14_18,18_24,24_32,32_plus; schedulerCombatSidecarRequired=true; commitSainWindow=Search_or_GroupSearch_or_bounded_SeekCover_StandAndShoot; holdSainWindow=Search_or_GroupSearch; memberLocalVeto=true; suppressionCommand=false; organicSuppressionObserved=true; routeAssignment=false; flank=false; heldAccessFixBoundedInfluence=true; heldAccessFixProbe=strict_probe_or_balanced_probe; heldAccessFixSensitiveProbe=false; heldAccessFixRequiresExactAltManeuverAtAcquisition=true; heldAccessFixPersistsThroughStableEstablishment=true; heldAccessFixRollingSliceSeconds={PlanTtl.TotalSeconds:0.00}; heldAccessFixClosedNativeDoorContainment=true; heldAccessFixDoorInteraction=false; synchronizedFlankReleaseApi=true; synchronizedFlankReleaseSuppressionSeconds={SynchronizedFlankReleaseSuppression.TotalSeconds:0.00}; releaseOnly=true; authoredPassageAuthority=false; searchTranslationPatchReady={BoolText(SearchTranslationPatchReady)}; searchTranslationPatchState={Safe(SearchTranslationPatchState)}; staleResume=false");
        }

        if (now >= nextSummaryAtUtc)
        {
            nextSummaryAtUtc = now + SummaryInterval;
            int activePlans;
            int activeDirectHolds;
            int activeHeldAccessFixPlans;
            int activeHeldAccessFixMembers;
            int activeSynchronizedFlankReleaseSuppressions;
            lock (Sync)
            {
                activePlans = ActiveByGroup.Count;
                activeDirectHolds = ActiveDirectHoldByBot.Count;
                activeHeldAccessFixPlans = ActiveHeldAccessFixByOwnerThreat.Count;
                activeHeldAccessFixMembers = ActiveHeldAccessFixMemberByBot.Count;
                activeSynchronizedFlankReleaseSuppressions = SynchronizedFlankReleaseUntilByOwnerThreat.Count(pair => pair.Value > now);
            }
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_CHOKE_COMMIT_SUPPORT_SUMMARY activePlans={activePlans}; activeDirectHolds={activeDirectHolds}; activeHeldAccessFixPlans={activeHeldAccessFixPlans}; activeHeldAccessFixMembers={activeHeldAccessFixMembers}; heldAccessFixAcquisitionPasses={heldAccessFixAcquisitionPassesTotal}; heldAccessFixOpportunityCandidates={heldAccessFixOpportunityCandidatesTotal}; heldAccessFixAltManeuverCandidates={heldAccessFixAltManeuverCandidatesTotal}; heldAccessFixPlansIssued={heldAccessFixPlansIssuedTotal}; heldAccessFixMembersIssued={heldAccessFixMembersIssuedTotal}; heldAccessFixContainmentRenewals={heldAccessFixContainmentRenewalsTotal}; heldAccessFixTranslationSuppressedTicks={heldAccessFixTranslationSuppressedTicksTotal}; heldAccessFixPlanCompletions={heldAccessFixPlanCompletionsTotal}; heldAccessFixPlanFailOpenReleases={heldAccessFixPlanFailOpenReleasesTotal}; heldAccessFixMemberFailOpenReleases={heldAccessFixMemberFailOpenReleasesTotal}; heldAccessFixSynchronizedReleases={heldAccessFixSynchronizedReleasesTotal}; heldAccessFixTacticalReplanReleases={heldAccessFixTacticalReplanReleasesTotal}; heldAccessFixRejects={FormatCounts(HeldAccessFixAdmissionRejectCounts, 10)}; acquisitionPasses={acquisitionPassesTotal}; admissibleChokes={admissibleChokesEvaluated}; convergedGroups={convergedGroupsMatched}; routeWitnesses={routeWitnessesMatched}; liveCommitWindows={liveCommitWindows}; pairGeometryWindows={pairGeometryWindows}; commitCandidatesEvaluated={commitCandidatesEvaluated}; commitCandidatesAdmissible={commitCandidatesAdmissible}; supportCandidatesEvaluated={supportCandidatesEvaluated}; supportCandidatesAdmissible={supportCandidatesAdmissible}; schedulerPairsReady={schedulerPairsReady}; plansIssued={plansIssued}; translationSuppressedTicks={translationSuppressedTicks}; organicSuppressionTicks={organicSuppressionTicks}; directHoldCandidatesEvaluated={directHoldCandidatesEvaluated}; directHoldCandidatesAdmissible={directHoldCandidatesAdmissible}; directHoldsIssued={directHoldsIssued}; directHoldTranslationSuppressedTicks={directHoldTranslationSuppressedTicks}; directHoldCompletions={directHoldCompletions}; directHoldFailOpenReleases={directHoldFailOpenReleases}; completions={completions}; failOpenReleases={failOpenReleases}; livePatchFailOpenReleases={livePatchFailOpenReleases}; synchronizedFlankReleasePlansTotal={synchronizedFlankReleasePlansTotal}; synchronizedFlankReleaseDirectHoldsTotal={synchronizedFlankReleaseDirectHoldsTotal}; activeSynchronizedFlankReleaseSuppressions={activeSynchronizedFlankReleaseSuppressions}; synchronizedFlankAcquisitionSuppressedTotal={synchronizedFlankAcquisitionSuppressedTotal}; tacticalReplanReleasePlansTotal={tacticalReplanReleasePlansTotal}; tacticalReplanReleaseDirectHoldsTotal={tacticalReplanReleaseDirectHoldsTotal}; admissionBlocks={FormatCounts(AdmissionBlockCounts, 12)}; admissionDetails={FormatCounts(AdmissionBlockDetails, 10)}; commitRejects={FormatCounts(CommitRejectCounts, 10)}; supportRejects={FormatCounts(SupportRejectCounts, 10)}; directHoldRejects={FormatCounts(DirectHoldRejectCounts, 10)}; headwayObserved={FormatHeadwayCounts(HeadwayObservedCounts)}; headwayWithinOuterEnvelope={FormatHeadwayCounts(HeadwayWithinOuterEnvelopeCounts)}; headwayCommitReady={FormatHeadwayCounts(HeadwayCommitReadyCounts)}; headwaySupportReady={FormatHeadwayCounts(HeadwaySupportReadyCounts)}; headwayPlansIssued={FormatHeadwayCounts(HeadwayPlansIssuedCounts)}; corridorParticipation=true; familyValidation=anti-conga+CommitSupport_StageHold+CommitSupport_DirectHold+HeldAccessFix_DirectHold; physicalMode=shared_sain_search_translation_gate; noFireCommand=true; routeAssignment=false; flank=false; synchronizedFlankReleaseApi=true; synchronizedFlankReacquireSuppression=true; releaseOnly=true; failOpen=true");
        }
    }

    private static void PublishEmpty()
    {
        lock (Sync)
        {
            current = ChokeCommitSupportSnapshot.Empty;
        }
    }

    private static void RecordHeadwayOpportunityDistribution(
        ThreatGroupConvergenceAssessment group,
        ChokeObservation choke,
        IReadOnlyList<RouteProgress> candidates,
        float commitMaxDistanceFromThreat,
        float supportMaxDistanceFromThreat)
    {
        for (int firstIndex = 0; firstIndex < candidates.Count; firstIndex++)
        {
            RouteProgress first = candidates[firstIndex];
            if (first.DistanceFromThreatMeters > commitMaxDistanceFromThreat
                || first.DistanceFromThreatMeters < choke.SharedSegmentNearDistanceFromThreatMeters - CommitClearedCorridorMeters)
            {
                continue;
            }

            for (int secondIndex = 0; secondIndex < candidates.Count; secondIndex++)
            {
                if (firstIndex == secondIndex)
                {
                    continue;
                }

                RouteProgress second = candidates[secondIndex];
                float headway = second.DistanceFromThreatMeters - first.DistanceFromThreatMeters;
                if (headway < -0.01f
                    || (Mathf.Abs(headway) <= 0.01f
                        && string.Compare(first.Snapshot.BotProfileId, second.Snapshot.BotProfileId, StringComparison.OrdinalIgnoreCase) >= 0)
                    || !IsConvergedPair(group, first.Snapshot.BotProfileId, second.Snapshot.BotProfileId))
                {
                    continue;
                }

                IncrementHeadwayBucket(HeadwayObservedCounts, headway);
                if (second.DistanceFromThreatMeters <= supportMaxDistanceFromThreat)
                {
                    IncrementHeadwayBucket(HeadwayWithinOuterEnvelopeCounts, headway);
                }
            }
        }
    }

    private static void IncrementHeadwayBucket(int[] counters, float headwayMeters)
    {
        int index = HeadwayBucketIndex(headwayMeters);
        lock (Sync)
        {
            counters[index]++;
        }
    }

    private static int HeadwayBucketIndex(float headwayMeters)
    {
        if (headwayMeters < 4.0f) return 0;
        if (headwayMeters < 6.5f) return 1;
        if (headwayMeters < 10.0f) return 2;
        if (headwayMeters < 14.0f) return 3;
        if (headwayMeters < 18.0f) return 4;
        if (headwayMeters < 24.0f) return 5;
        if (headwayMeters < 32.0f) return 6;
        return 7;
    }

    private static string FormatHeadwayCounts(int[] counters)
    {
        lock (Sync)
        {
            return string.Join(",", HeadwayBucketLabels.Select((label, index) =>
                label + "=" + counters[index].ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static void RecordCommitReject(string reason)
    {
        lock (Sync)
        {
            IncrementCounter(CommitRejectCounts, Safe(string.IsNullOrWhiteSpace(reason) ? "unknown" : reason), 96);
        }
    }

    private static void RecordSupportReject(string reason)
    {
        lock (Sync)
        {
            IncrementCounter(SupportRejectCounts, Safe(string.IsNullOrWhiteSpace(reason) ? "unknown" : reason), 96);
        }
    }

    private static void RecordDirectHoldReject(string reason)
    {
        lock (Sync)
        {
            IncrementCounter(DirectHoldRejectCounts, Safe(string.IsNullOrWhiteSpace(reason) ? "unknown" : reason), 96);
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
        lock (Sync)
        {
            IncrementCounter(AdmissionBlockCounts, Safe(canonical), 64);
            if (separator > 0)
            {
                IncrementCounter(AdmissionBlockDetails, Safe(safeReason), 96);
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

    private static string FormatCounts(Dictionary<string, int> counters, int maxItems)
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

    private static string BuildPlanId(
        RouteConvergenceSnapshot convergenceSnapshot,
        ChokeObservation choke,
        string commitBotProfileId,
        string supportBotProfileId,
        DateTimeOffset now)
    {
        return "tc_choke_commit_support_" + convergenceSnapshot.TopologyRevision.ToString(CultureInfo.InvariantCulture)
            + "_" + StableToken(choke.ChokeId)
            + "_" + StableToken(commitBotProfileId)
            + "_" + StableToken(supportBotProfileId)
            + "_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
    }

    private static string OwnerThreatKey(string ownerProfileId, string threatId)
        => Normalize(ownerProfileId) + "|" + NormalizeThreat(threatId);

    private static string GroupKey(string ownerProfileId, string threatId, string chokeId)
        => OwnerThreatKey(ownerProfileId, threatId) + "|" + Normalize(chokeId);

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
    private static string BoolText(bool value) => value ? "true" : "false";

    private readonly struct RouteProjection
    {
        public RouteProjection(float alongMeters, float crossTrackMeters, float totalLengthMeters)
        {
            AlongMeters = alongMeters;
            CrossTrackMeters = crossTrackMeters;
            TotalLengthMeters = totalLengthMeters;
        }
        public float AlongMeters { get; }
        public float CrossTrackMeters { get; }
        public float TotalLengthMeters { get; }
    }

    private readonly struct RouteProgress
    {
        public RouteProgress(
            DirectRouteObservation route,
            OperatorDecisionSnapshot snapshot,
            Vector3 livePosition,
            float operatorCrossTrackMeters,
            float distanceFromThreatMeters)
        {
            Route = route;
            Snapshot = snapshot;
            LivePosition = livePosition;
            OperatorCrossTrackMeters = operatorCrossTrackMeters;
            DistanceFromThreatMeters = distanceFromThreatMeters;
        }
        public DirectRouteObservation Route { get; }
        public OperatorDecisionSnapshot Snapshot { get; }
        public Vector3 LivePosition { get; }
        public float OperatorCrossTrackMeters { get; }
        public float DistanceFromThreatMeters { get; }
    }

    private readonly struct PairCandidate
    {
        public PairCandidate(RouteProgress commit, RouteProgress support, float headwayMeters, string supportSchedulerWindowId, string supportPathSummary)
        {
            Commit = commit;
            Support = support;
            HeadwayMeters = headwayMeters;
            SupportSchedulerWindowId = supportSchedulerWindowId;
            SupportPathSummary = supportPathSummary;
        }
        public RouteProgress Commit { get; }
        public RouteProgress Support { get; }
        public float HeadwayMeters { get; }
        public string SupportSchedulerWindowId { get; }
        public string SupportPathSummary { get; }
    }

    private sealed class HeldAccessFixContext
    {
        public HeldAccessFixContext(
            ThreatExposureShadowSnapshot shadow,
            ThreatExposureShadowGroupObservation shadowGroup,
            ThreatGroupRouteAssignment assignment,
            ThreatGroupReadinessObservation readinessGroup,
            ThreatGroupSplitFlankStateObservation splitStateGroup,
            ThreatGroupConvergenceAssessment convergenceGroup,
            ChokeObservation choke,
            AltDevelopEvidence developmentEvidence,
            string altDevelopManeuverEpochId,
            bool altDevelopPhysicalArrivalObserved,
            bool stableEstablishmentContinuation)
        {
            Shadow = shadow;
            ShadowGroup = shadowGroup;
            Assignment = assignment;
            ReadinessGroup = readinessGroup;
            SplitStateGroup = splitStateGroup;
            ConvergenceGroup = convergenceGroup;
            Choke = choke;
            DevelopmentEvidence = developmentEvidence;
            AltDevelopManeuverEpochId = altDevelopManeuverEpochId;
            AltDevelopPhysicalArrivalObserved = altDevelopPhysicalArrivalObserved;
            StableEstablishmentContinuation = stableEstablishmentContinuation;
        }

        public ThreatExposureShadowSnapshot Shadow { get; }
        public ThreatExposureShadowGroupObservation ShadowGroup { get; }
        public ThreatGroupRouteAssignment Assignment { get; }
        public ThreatGroupReadinessObservation ReadinessGroup { get; }
        public ThreatGroupSplitFlankStateObservation SplitStateGroup { get; }
        public ThreatGroupConvergenceAssessment ConvergenceGroup { get; }
        public ChokeObservation Choke { get; }
        public AltDevelopEvidence DevelopmentEvidence { get; }
        public string AltDevelopManeuverEpochId { get; }
        public bool AltDevelopPhysicalArrivalObserved { get; }
        public bool StableEstablishmentContinuation { get; }
    }

    private sealed class ActiveHeldAccessFixMember
    {
        public string BotProfileId = "none";
        public string PlanId = "none";
        public string OwnerThreatKey = "none";
        public string OwnerProfileId = "none";
        public string ThreatId = "none";
        public string ChokeId = "none";
        public string SchedulerWindowId = "none";
        public string PathSummary = "none";
        public Vector3 StageAnchor;
        public DateTimeOffset StartedAtUtc;
        public DateTimeOffset ExpiresAtUtc;
    }

    private sealed class ActiveHeldAccessFixPlan
    {
        public string PlanId = "none";
        public string OwnerThreatKey = "none";
        public string OwnerProfileId = "none";
        public string ThreatId = "none";
        public string AssignmentIdentity = "none";
        public string AlternateCandidateId = "none";
        public string SelectedAltBotProfileId = "none";
        public string SelectedAltOperatorId = "none";
        public string AltDevelopManeuverEpochId = "none";
        public bool StableEstablishmentContinuationObserved;
        public string[] DirectBotProfileIds = Array.Empty<string>();
        public string ChokeId = "none";
        public string ChokeConfidence = "none";
        public long PlanGeneration;
        public DateTimeOffset StartedAtUtc;
        public DateTimeOffset ExpiresAtUtc;
    }

    private sealed class ActiveDirectHoldContribution
    {
        public string BotProfileId = "none";
        public string PlanId = "none";
        public string GroupKey = "none";
        public string OwnerProfileId = "none";
        public string ThreatId = "none";
        public string ChokeId = "none";
        public string SchedulerWindowId = "none";
        public string PathSummary = "none";
        public Vector3 StageAnchor;
        public DateTimeOffset StartedAtUtc;
        public DateTimeOffset ExpiresAtUtc;
    }

    private sealed class ActiveChokeCommitSupportPlan
    {
        public string PlanId = "none";
        public string GroupKey = "none";
        public string OwnerProfileId = "none";
        public string ThreatId = "none";
        public string ChokeId = "none";
        public string ChokeKind = "none";
        public string ChokeConfidence = "none";
        public string CommitBotProfileId = "none";
        public string SupportBotProfileId = "none";
        public string SupportSchedulerWindowId = "none";
        public string SupportPathSummary = "none";
        public long PlanGeneration;
        public Vector3 StageAnchor;
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
#endif
