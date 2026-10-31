#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Movement.Brain;

// Responsibility: Executes one bounded physical DirectCommit contribution after either strict FullAlt synchronized readiness or the separately-qualified near-commit approach bridge is proven.
// Flow: Exact split-flank readiness, tactical-replan plan epoch, route assignment and current direct-route evidence select one Direct member; strict FullAlt pressure may opportunistically resolve a confirmed HardChoke post-portal entry anchor, including a SAIN-natively traversable shut door, otherwise the ordinary bounded advance path is used; fresh safety/live-SA​​IN checks then create one short disposable movement lease whose path is executed by SAIN BotPathDataManual.
// Authority boundary: TC owns only a short destination/path lease for one Direct member. A read-only consumer may observe the exact live ordinary commit as a bounded relay opportunity; SAIN retains perception, target, aim, fire, suppression, steering and higher-priority local survival movement. No sprint, fire command, target mutation, teleport or synchronized assault action is issued.
// Invariant: Every physical DirectCommit lease is bound to owner + threat + plan epoch + selected Direct member + admission readiness mode and fresh path/window/lease/generation. Approach readiness never asserts FullAlt/D5; any applicable readiness regression, epoch change, local authority conflict or path failure releases immediately and an old lease is never resumed.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class DirectCommitMovementService
{
    public const string StatusTag = "VANGUARD_TC_DIRECT_COMMIT_STATUS";
    public const string RequestKind = DirectCommitAuthorityPolicy.RequestKind;

    // These are safety bounds for the first executable DirectCommit slice, not final gameplay tuning.
    // One lease advances only a short distance along the current direct route and never deliberately
    // closes inside the standoff envelope around the current threat anchor.
    internal const float MaxAdvanceMeters = 8.0f;
    private const float MinimumAdvanceMeters = 1.5f;
    private const string DefaultMovementMode = "bounded_direct_advance";
    private const float MinimumThreatStandoffMeters = 10.0f;
    private const float CommandAnchorRadiusMeters = 0.75f;
    private const float ProgressDistanceMeters = 0.35f;
    private static readonly TimeSpan MaxLeaseDuration = TimeSpan.FromSeconds(3.0d);
    private static readonly TimeSpan NoProgressDuration = TimeSpan.FromSeconds(1.5d);
    // SAIN DoorOpener keeps a native open interaction active for up to 1.25 s. Only a FullAlt
    // entry explicitly proven to require native shut-door traversal receives this bounded allowance.
    private static readonly TimeSpan NativeDoorInteractionAllowance = TimeSpan.FromSeconds(1.5d);
    private static readonly TimeSpan BreachThresholdSupportWaitAllowance = TimeSpan.FromSeconds(5.0d);
    private const float BreachThresholdHoldApproachMeters = 3.5f;
    private const float BreachThresholdSupportMaximumPortalDistanceMeters = 9.0f;
    private const float BreachThresholdSupportMaximumDownstreamSideMeters = 0.75f;
    private static readonly TimeSpan RestartCooldown = TimeSpan.FromSeconds(1.0d);
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(10d);

    private static readonly object Sync = new();
    private static readonly Dictionary<string, DirectCommitLeaseState> ActiveByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> CooldownUntilByOwner = new(StringComparer.OrdinalIgnoreCase);
    // Each plan epoch grants at most one bounded approach contribution and at most one strict FullAlt contribution.
    // Keeping the phases distinct lets a near-commit bridge create visible pressure without consuming the later strict
    // synchronized-commit slice, while still preventing either phase from becoming an unbounded 8 m step loop.
    private static readonly Dictionary<string, long> AttemptedPlanEpochByOwnerThreatPhase = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxExecutionReceipts = 64;
    private static readonly Dictionary<string, DirectCommitExecutionReceipt> ExecutionReceiptByPlanPhase = new(StringComparer.OrdinalIgnoreCase);
    private static DirectCommitExecutionSnapshot executionSnapshot = DirectCommitExecutionSnapshot.Empty;

    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;
    private static int startsTotal;
    private static int completesTotal;
    private static int interruptsTotal;
    private static int failuresTotal;
    private static int schedulerRejectsTotal;
    private static int commandRejectsTotal;
    private static int pathRejectsTotal;
    private static int admissionDeferralsTotal;
    private static int livePreflightDeferralsTotal;
    private static int nativeTicksTotal;
    private static float physicalMetersTotal;

    public static void Tick(DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            ResetForRaidLifecycle("not_raid_authority");
            return;
        }

        UpdateActiveLeases(now);
        TryStartEligibleCommit(now);
        EmitSummaryIfDue(now);
    }

    internal static bool ForgetRuntimePresence(
        string botProfileId,
        DateTimeOffset now,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return false;
        }

        DirectCommitLeaseState? lease = FindActiveByBot(botProfileId);
        if (lease == null)
        {
            return false;
        }

        bool attemptMarkerRemoved = false;
        bool receiptRemoved = false;
        lock (Sync)
        {
            if (ActiveByOwner.TryGetValue(lease.OwnerProfileId, out DirectCommitLeaseState active)
                && string.Equals(active.LeaseId, lease.LeaseId, StringComparison.OrdinalIgnoreCase))
            {
                ActiveByOwner.Remove(lease.OwnerProfileId);
            }

            string epochKey = PlanEpochKey(lease.OwnerProfileId, lease.ThreatId, lease.ReadinessMode);
            if (AttemptedPlanEpochByOwnerThreatPhase.TryGetValue(epochKey, out long attemptedEpoch)
                && attemptedEpoch == lease.PlanEpoch)
            {
                attemptMarkerRemoved = AttemptedPlanEpochByOwnerThreatPhase.Remove(epochKey);
            }

            string receiptKey = ExecutionReceiptKey(lease.OwnerProfileId, lease.ThreatId, lease.PlanEpoch, lease.ReadinessMode);
            receiptRemoved = ExecutionReceiptByPlanPhase.Remove(receiptKey);
            executionSnapshot = new DirectCommitExecutionSnapshot(
                now,
                ExecutionReceiptByPlanPhase.Values
                    .OrderBy(item => item.OwnerProfileId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.ThreatId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.PlanEpoch)
                    .ThenBy(item => item.ReadinessMode, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
        }

        bool releasedBoundPath = SearchTranslationGateBridge.ReleaseTacticalMovementPathBinding(
            lease.BotProfileId,
            RequestKind,
            lease.LeaseId,
            lease.Generation,
            "tc_direct_commit_presence_lost:" + Safe(reason));
        string clearResult = VanguardReturnMovementCommandStore.ClearOwned(
            lease.BotProfileId,
            lease.LeaseId,
            lease.StartedAtUtc,
            "tc_direct_commit_presence_lost:" + Safe(reason));

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_DIRECT_COMMIT_RUNTIME_PRESENCE_FORGOTTEN bot={Safe(botProfileId)}; owner={Safe(lease.OwnerProfileId)}; threat={Safe(lease.ThreatId)}; epoch={lease.PlanEpoch}; readinessMode={Safe(lease.ReadinessMode)}; reason={Safe(reason)}; commandClear={Safe(clearResult)}; sainPathReleased={Bool(releasedBoundPath)}; attemptMarkerRemoved={Bool(attemptMarkerRemoved)}; receiptRemoved={Bool(receiptRemoved)}; normalFinish=false; ownerCooldown=false; executionReceipt=false; schedulerHandback=false; staleResume=false");
        return true;
    }

    internal static bool DrainForHigherAuthority(
        string botProfileId,
        DateTimeOffset now,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return false;
        }

        DirectCommitLeaseState? lease = FindActiveByBot(botProfileId);
        if (lease == null)
        {
            return false;
        }

        FinishLease(
            lease,
            now,
            "Interrupted",
            "higher_authority:" + Safe(reason),
            safetyCooldown: false);
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_DIRECT_COMMIT_HIGHER_AUTHORITY_DRAIN bot={Safe(botProfileId)}; owner={Safe(lease.OwnerProfileId)}; threat={Safe(lease.ThreatId)}; epoch={lease.PlanEpoch}; readinessMode={Safe(lease.ReadinessMode)}; reason={Safe(reason)}; oldPathResume=false; oldLeaseResume=false; oldWindowResume=false; freshReacquireRequiresNewPlanEpochOrDistinctReadinessPhase=true");
        return true;
    }

    internal static bool DrainForTacticalReplan(
        string ownerProfileId,
        string threatId,
        DateTimeOffset now,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(ownerProfileId) || string.IsNullOrWhiteSpace(threatId))
        {
            return false;
        }

        DirectCommitLeaseState? lease = null;
        lock (Sync)
        {
            if (ActiveByOwner.TryGetValue(ownerProfileId, out var active)
                && string.Equals(active.ThreatId, threatId, StringComparison.OrdinalIgnoreCase))
            {
                lease = active;
            }
        }
        if (lease == null)
        {
            return false;
        }

        FinishLease(
            lease,
            now,
            "Interrupted",
            "tactical_replan:" + Safe(reason),
            safetyCooldown: false);
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_DIRECT_COMMIT_REPLAN_DRAIN owner={Safe(ownerProfileId)}; threat={Safe(threatId)}; epoch={lease.PlanEpoch}; readinessMode={Safe(lease.ReadinessMode)}; reason={Safe(reason)}; staleResume=false; freshReacquireRequiresNewPlanEpochOrDistinctReadinessPhase=true; fireCommand=false; targetMutation=false");
        return true;
    }

    public static void ResetForRaidLifecycle(string reason)
    {
        DirectCommitLeaseState[] active;
        lock (Sync)
        {
            active = ActiveByOwner.Values.ToArray();
            ActiveByOwner.Clear();
            CooldownUntilByOwner.Clear();
            AttemptedPlanEpochByOwnerThreatPhase.Clear();
            ExecutionReceiptByPlanPhase.Clear();
            executionSnapshot = DirectCommitExecutionSnapshot.Empty;
        }
        foreach (var lease in active)
        {
            SearchTranslationGateBridge.ReleaseTacticalMovementPathBinding(
                lease.BotProfileId,
                RequestKind,
                lease.LeaseId,
                lease.Generation,
                "tc_direct_commit_raid_reset:" + Safe(reason));
            VanguardReturnMovementCommandStore.ClearOwned(
                lease.BotProfileId,
                lease.LeaseId,
                lease.StartedAtUtc,
                "tc_direct_commit_raid_reset:" + Safe(reason));
            VanguardMainIntentScheduler.FinishPrimaryWindow(
                lease.BotProfileId,
                DateTimeOffset.UtcNow,
                "Interrupted",
                "tc_direct_commit_raid_reset:" + Safe(reason),
                lease.Summary,
                lease.WindowId);
        }

        nextSummaryAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        startsTotal = 0;
        completesTotal = 0;
        interruptsTotal = 0;
        failuresTotal = 0;
        schedulerRejectsTotal = 0;
        commandRejectsTotal = 0;
        pathRejectsTotal = 0;
        admissionDeferralsTotal = 0;
        livePreflightDeferralsTotal = 0;
        nativeTicksTotal = 0;
        physicalMetersTotal = 0f;
    }

    internal static bool TryGetExecutionSnapshot(out DirectCommitExecutionSnapshot snapshot)
    {
        lock (Sync)
        {
            snapshot = executionSnapshot;
            return snapshot.IsAvailable;
        }
    }

    internal static bool TryGetActiveRelayContext(
        string ownerProfileId,
        string threatId,
        DateTimeOffset now,
        out string leadBotProfileId,
        out string sourceLeaseId,
        out long planEpoch,
        out Vector3 leadPosition,
        out string reason)
    {
        leadBotProfileId = string.Empty;
        sourceLeaseId = string.Empty;
        planEpoch = 0L;
        leadPosition = Vector3.zero;
        reason = "direct_commit_missing";
        if (string.IsNullOrWhiteSpace(ownerProfileId) || string.IsNullOrWhiteSpace(threatId))
        {
            reason = "owner_or_threat_missing";
            return false;
        }

        DirectCommitLeaseState? lease;
        lock (Sync)
        {
            ActiveByOwner.TryGetValue(ownerProfileId.Trim(), out lease);
        }
        if (lease == null
            || !string.Equals(lease.ThreatId, threatId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "exact_owner_threat_commit_missing";
            return false;
        }
        if (!string.Equals(lease.MovementMode, DefaultMovementMode, StringComparison.OrdinalIgnoreCase))
        {
            reason = "commit_mode_not_relayable";
            return false;
        }
        if (now >= lease.MaxUntilUtc)
        {
            reason = "commit_expired";
            return false;
        }
        if (!VanguardReturnMovementCommandStore.TryGetExactOwned(
                lease.BotProfileId,
                lease.LeaseId,
                RequestKind,
                lease.Generation,
                now,
                out VanguardReturnMovementCommand command,
                out string commandReason))
        {
            reason = "commit_command_not_current:" + commandReason;
            return false;
        }
        if (!IsLayerContextCurrent(lease.BotProfileId, command, now, out string contextReason))
        {
            reason = "commit_context_not_current:" + contextReason;
            return false;
        }
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(lease.BotProfileId, out VanguardRaidOperatorRuntimeRecord runtime)
            || runtime.BotOwner == null
            || runtime.BotOwner.IsDead)
        {
            reason = "commit_lead_runtime_missing_or_dead";
            return false;
        }

        leadBotProfileId = lease.BotProfileId;
        sourceLeaseId = lease.LeaseId;
        planEpoch = lease.PlanEpoch;
        leadPosition = runtime.BotOwner.Position;
        reason = "current_ordinary_direct_commit";
        return true;
    }

    internal static bool TryGetActiveBreachSupportContext(
        string ownerProfileId,
        string threatId,
        DateTimeOffset now,
        out string leadBotProfileId,
        out string sourceLeaseId,
        out long planEpoch,
        out Vector3 leadPosition,
        out BreachEntryOpportunity breachEntry,
        out string reason)
    {
        leadBotProfileId = string.Empty;
        sourceLeaseId = string.Empty;
        planEpoch = 0L;
        leadPosition = Vector3.zero;
        breachEntry = BreachEntryOpportunity.Empty;
        reason = "breach_entry_commit_missing";
        if (string.IsNullOrWhiteSpace(ownerProfileId) || string.IsNullOrWhiteSpace(threatId))
        {
            reason = "owner_or_threat_missing";
            return false;
        }

        DirectCommitLeaseState? lease;
        lock (Sync)
        {
            ActiveByOwner.TryGetValue(ownerProfileId.Trim(), out lease);
        }
        if (lease == null
            || !string.Equals(lease.ThreatId, threatId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "exact_owner_threat_commit_missing";
            return false;
        }
        if (!string.Equals(lease.MovementMode, BreachEntryOpportunityService.MovementMode, StringComparison.OrdinalIgnoreCase))
        {
            reason = "commit_mode_not_breach_entry";
            return false;
        }
        if (now >= lease.MaxUntilUtc)
        {
            reason = "commit_expired";
            return false;
        }
        if (string.IsNullOrWhiteSpace(lease.BreachEntry.ChokeId)
            || string.IsNullOrWhiteSpace(lease.BreachEntry.BotProfileId)
            || !string.Equals(lease.BreachEntry.BotProfileId, lease.BotProfileId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "breach_entry_context_missing_or_mismatched";
            return false;
        }
        if (!VanguardReturnMovementCommandStore.TryGetExactOwned(
                lease.BotProfileId,
                lease.LeaseId,
                RequestKind,
                lease.Generation,
                now,
                out VanguardReturnMovementCommand command,
                out string commandReason))
        {
            reason = "commit_command_not_current:" + commandReason;
            return false;
        }
        if (!IsLayerContextCurrent(lease.BotProfileId, command, now, out string contextReason))
        {
            reason = "commit_context_not_current:" + contextReason;
            return false;
        }
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(lease.BotProfileId, out VanguardRaidOperatorRuntimeRecord runtime)
            || runtime.BotOwner == null
            || runtime.BotOwner.IsDead)
        {
            reason = "commit_lead_runtime_missing_or_dead";
            return false;
        }

        leadBotProfileId = lease.BotProfileId;
        sourceLeaseId = lease.LeaseId;
        planEpoch = lease.PlanEpoch;
        leadPosition = runtime.BotOwner.Position;
        breachEntry = lease.BreachEntry;
        reason = "current_breach_entry_direct_commit";
        return true;
    }

    internal static SearchTranslationDispatchResult TryHandleSearchTranslationGate(
        object pathData,
        object sainBot,
        BotOwner botOwner,
        DateTimeOffset now,
        out string reason)
    {
        reason = "no_direct_commit_lease";
        if (botOwner == null || string.IsNullOrWhiteSpace(botOwner.ProfileId))
        {
            return SearchTranslationDispatchResult.NoLease;
        }

        DirectCommitLeaseState? lease = FindActiveByBot(botOwner.ProfileId);
        if (lease == null)
        {
            return SearchTranslationDispatchResult.NoLease;
        }

        if (!IsLeasePlanCurrent(lease, now, out string planReason))
        {
            bool released = FinishLease(lease, now, "Interrupted", "plan_invalidated_at_search_gate:" + planReason, safetyCooldown: false);
            reason = "plan_invalidated:" + planReason;
            return released ? SearchTranslationDispatchResult.ReleasedCurrentTick : SearchTranslationDispatchResult.HandledFailOpen;
        }

        if (!VanguardReturnMovementCommandStore.TryGetExactOwned(
                lease.BotProfileId,
                lease.LeaseId,
                RequestKind,
                lease.Generation,
                now,
                out _,
                out string commandReason))
        {
            bool released = FinishLease(lease, now, "Interrupted", "command_changed_at_search_gate:" + commandReason, safetyCooldown: false);
            reason = "command_changed:" + commandReason;
            return released ? SearchTranslationDispatchResult.ReleasedCurrentTick : SearchTranslationDispatchResult.HandledFailOpen;
        }

        if (!VanguardMainIntentScheduler.IsTacticalCoordinatorDirectCommitCurrent(
                lease.BotProfileId,
                now,
                lease.WindowId,
                lease.LeaseId,
                lease.PlanEpoch,
                out string schedulerReason))
        {
            bool released = FinishLease(lease, now, "Interrupted", "scheduler_changed_at_search_gate:" + schedulerReason, safetyCooldown: false);
            reason = "scheduler_changed:" + schedulerReason;
            return released ? SearchTranslationDispatchResult.ReleasedCurrentTick : SearchTranslationDispatchResult.HandledFailOpen;
        }

        string authorityReason = "decision_snapshot_missing";
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(lease.BotProfileId, out var decision)
            || !DirectCommitAuthorityPolicy.IsTranslationCompatible(
                decision,
                lease.OwnerProfileId,
                lease.ThreatId,
                lease.MemberEvidenceCapturedAtUtc,
                now,
                out authorityReason))
        {
            bool released = FinishLease(lease, now, "Interrupted", "local_authority_changed_at_search_gate:" + authorityReason, safetyCooldown: false);
            reason = "local_authority_changed:" + authorityReason;
            return released ? SearchTranslationDispatchResult.ReleasedCurrentTick : SearchTranslationDispatchResult.HandledFailOpen;
        }

        if (!MarksmanForwardRoleDisciplinePolicy.CanAuthorizeForwardTranslation(
                decision,
                lease.ThreatId,
                lease.Destination,
                "DirectCommit",
                now,
                out string roleDisciplineReason))
        {
            bool released = FinishLease(lease, now, "Interrupted", "marksman_role_discipline_changed_at_search_gate:" + roleDisciplineReason, safetyCooldown: false);
            reason = "marksman_role_discipline_changed:" + roleDisciplineReason;
            return released ? SearchTranslationDispatchResult.ReleasedCurrentTick : SearchTranslationDispatchResult.HandledFailOpen;
        }

        if (!TryResolveBreachThresholdSynchronization(
                lease,
                botOwner.Position,
                now,
                out bool suppressForThresholdSupport,
                out string thresholdSupportReason))
        {
            bool released = FinishLease(
                lease,
                now,
                "Interrupted",
                "breach_threshold_support_changed:" + thresholdSupportReason,
                safetyCooldown: false);
            reason = "breach_threshold_support_changed:" + thresholdSupportReason;
            return released ? SearchTranslationDispatchResult.ReleasedCurrentTick : SearchTranslationDispatchResult.HandledFailOpen;
        }
        if (suppressForThresholdSupport)
        {
            lease.NoProgressUntilUtc = now + NoProgressDuration;
            lease.LastProgressAtUtc = now;
            reason = "breach_threshold_hold:" + thresholdSupportReason;
            return SearchTranslationDispatchResult.Suppressed;
        }

        if (!SearchTranslationGateBridge.TryApplyTacticalMovementTranslationGate(
                pathData,
                sainBot,
                botOwner,
                lease.ThreatId,
                RequestKind,
                lease.LeaseId,
                lease.Generation,
                lease.Destination,
                CommandAnchorRadiusMeters,
                lease.PreparedPath,
                out string gateReason,
                allowStandAndShootCombatAdvance: true,
                allowDistantVisibleSearchAction: true))
        {
            bool released = FinishLease(lease, now, "Interrupted", "live_translation_gate:" + gateReason, safetyCooldown: false);
            reason = gateReason;
            return released ? SearchTranslationDispatchResult.ReleasedCurrentTick : SearchTranslationDispatchResult.HandledFailOpen;
        }

        lease.TranslationCompatibleNativeTicks++;
        nativeTicksTotal++;
        reason = "direct_commit_native_sain_path_current";
        return SearchTranslationDispatchResult.PassThroughOwned;
    }

    private static bool TryResolveBreachThresholdSynchronization(
        DirectCommitLeaseState lease,
        Vector3 leadPosition,
        DateTimeOffset now,
        out bool suppressTranslation,
        out string reason)
    {
        suppressTranslation = false;
        reason = "not_required";
        if (!string.Equals(lease.MovementMode, BreachEntryOpportunityService.MovementMode, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(lease.BreachEntry.ChokeId))
        {
            return true;
        }

        Vector3 entryDirection = lease.BreachEntry.EntryAnchor - lease.BreachEntry.ProjectedPortal;
        entryDirection.y = 0f;
        if (entryDirection.sqrMagnitude <= 0.01f)
        {
            reason = "breach_entry_direction_missing";
            return false;
        }
        entryDirection.Normalize();

        if (!string.IsNullOrWhiteSpace(lease.ThresholdSupportBotProfileId))
        {
            if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(
                    lease.ThresholdSupportBotProfileId,
                    out OperatorDecisionSnapshot support)
                || support == null
                || !support.Alive)
            {
                reason = "latched_threshold_support_missing_or_dead";
                return false;
            }
            if (!VanguardOperatorDecisionSnapshotService.IsFresh(support, now, out string supportFreshness))
            {
                reason = "latched_threshold_support_not_current:" + Safe(supportFreshness);
                return false;
            }
            if (!string.Equals(support.OwnerProfileId, lease.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(NormalizeKey(support.Threat.EnemyId), NormalizeKey(lease.ThreatId), StringComparison.OrdinalIgnoreCase))
            {
                reason = "latched_threshold_support_identity_changed";
                return false;
            }
            if (support.Sain.RunningToCover == true
                || support.GrenadeHazard.HasRelevantHazard
                || support.Medical.Actionability.GrenadeThrowing
                || support.Medical.Actionability.AnyMedicineUsing
                || support.Medical.Actionability.Reloading)
            {
                reason = "latched_threshold_support_local_survival_action";
                return false;
            }
            if (!MultiThreatOffensiveSafetyPolicy.IsSupportMateriallyReady(support, now, out string materialReason))
            {
                reason = "latched_threshold_support_material_not_ready:" + Safe(materialReason);
                return false;
            }

            Vector3 supportOffset = support.Position - lease.BreachEntry.ProjectedPortal;
            supportOffset.y = 0f;
            float supportPortalSide = Vector3.Dot(supportOffset, entryDirection);
            float supportPortalDistance = HorizontalDistance(support.Position, lease.BreachEntry.ProjectedPortal);
            if (supportPortalSide > BreachThresholdSupportMaximumDownstreamSideMeters
                || supportPortalDistance > BreachThresholdSupportMaximumPortalDistanceMeters)
            {
                reason = "latched_threshold_support_left_preportal_envelope:side="
                    + supportPortalSide.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":distance=" + supportPortalDistance.ToString("0.0", CultureInfo.InvariantCulture);
                return false;
            }

            reason = "threshold_support_latched_and_current:bot=" + Safe(lease.ThresholdSupportBotProfileId);
            return true;
        }

        Vector3 leadOffset = leadPosition - lease.BreachEntry.ProjectedPortal;
        leadOffset.y = 0f;
        float leadPortalSide = Vector3.Dot(leadOffset, entryDirection);
        if (leadPortalSide < -BreachThresholdHoldApproachMeters)
        {
            reason = "lead_approaching_threshold:portalSide=" + leadPortalSide.ToString("0.0", CultureInfo.InvariantCulture);
            return true;
        }

        if (!lease.ThresholdSupportWaitStartedAtUtc.HasValue)
        {
            lease.ThresholdSupportWaitStartedAtUtc = now;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_DIRECT_COMMIT_BREACH_THRESHOLD_HOLD owner={Safe(lease.OwnerProfileId)}; threat={Safe(lease.ThreatId)}; bot={Safe(lease.BotProfileId)}; lease={Safe(lease.LeaseId)}; choke={Safe(lease.BreachEntry.ChokeId)}; portalSide={leadPortalSide:0.00}; holdApproach={BreachThresholdHoldApproachMeters:0.00}; waitAllowance={BreachThresholdSupportWaitAllowance.TotalSeconds:0.00}; thresholdSupportRequired=true; translationSuppressed=true; targetMutation=false; aimMutation=false; fireCommand=false");
        }
        if (now - lease.ThresholdSupportWaitStartedAtUtc.Value > BreachThresholdSupportWaitAllowance)
        {
            reason = "threshold_support_wait_timeout";
            return false;
        }

        if (!TacticalOpportunityMovementService.TryGetThresholdSupportSynchronization(
                lease.OwnerProfileId,
                lease.ThreatId,
                lease.BotProfileId,
                lease.LeaseId,
                now,
                out string supportBotProfileId,
                out bool physicallyEstablished,
                out string supportReason))
        {
            suppressTranslation = true;
            reason = "waiting_for_threshold_support:" + Safe(supportReason);
            return true;
        }
        if (!physicallyEstablished)
        {
            suppressTranslation = true;
            reason = "threshold_support_not_established:" + Safe(supportReason);
            return true;
        }

        lease.ThresholdSupportBotProfileId = supportBotProfileId;
        lease.ThresholdSupportEstablishedAtUtc = now;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_DIRECT_COMMIT_BREACH_THRESHOLD_SYNC owner={Safe(lease.OwnerProfileId)}; threat={Safe(lease.ThreatId)}; bot={Safe(lease.BotProfileId)}; supportBot={Safe(supportBotProfileId)}; lease={Safe(lease.LeaseId)}; choke={Safe(lease.BreachEntry.ChokeId)}; thresholdSupportEstablished=true; preportal=true; translationReleased=true; targetMutation=false; aimMutation=false; fireCommand=false");
        reason = "threshold_support_established:" + Safe(supportBotProfileId);
        return true;
    }

    private static void TryStartEligibleCommit(DateTimeOffset now)
    {
        if (!SplitFlankStateService.TryGetSnapshot(out var stateSnapshot)
            || !TacticalReplanService.TryGetSnapshot(out var replanSnapshot)
            || !RouteAssignmentService.TryGetSnapshot(out var assignmentSnapshot)
            || !RouteConvergenceService.TryGetSnapshot(out var convergenceSnapshot))
        {
            return;
        }

        foreach (var group in stateSnapshot.ThreatGroups
            .Where(item => item.SynchronizedCommitReady || item.SynchronizedApproachCommitReady)
            .OrderBy(item => item.OwnerProfileId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ThreatId, StringComparer.OrdinalIgnoreCase))
        {
            string owner = group.OwnerProfileId;
            string readinessMode = group.SynchronizedCommitReady ? "full_alt" : "approach";
            lock (Sync)
            {
                if (ActiveByOwner.ContainsKey(owner)
                    || (CooldownUntilByOwner.TryGetValue(owner, out var cooldownUntil) && now < cooldownUntil))
                {
                    continue;
                }
            }

            var lifecycle = replanSnapshot.Plans.FirstOrDefault(item =>
                string.Equals(item.OwnerProfileId, group.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ThreatId, group.ThreatId, StringComparison.OrdinalIgnoreCase));
            if (lifecycle == null
                || lifecycle.PlanEpoch <= 0
                || lifecycle.HardBreakRecommended
                || lifecycle.ReplanRecommended
                || !lifecycle.UpstreamPresent
                || !lifecycle.CommitReadyObserved
                || !string.Equals(lifecycle.AlternateCandidateId, group.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(lifecycle.AlternateBotProfileId, group.AlternateBotProfileId, StringComparison.OrdinalIgnoreCase)
                || !IsLifecycleCommitStateCompatible(lifecycle.CurrentState, readinessMode))
            {
                admissionDeferralsTotal++;
                continue;
            }

            string epochKey = PlanEpochKey(group.OwnerProfileId, group.ThreatId, readinessMode);
            lock (Sync)
            {
                if (AttemptedPlanEpochByOwnerThreatPhase.TryGetValue(epochKey, out long attemptedEpoch)
                    && attemptedEpoch == lifecycle.PlanEpoch)
                {
                    continue;
                }
            }

            var assignment = assignmentSnapshot.ThreatGroups.FirstOrDefault(item =>
                string.Equals(item.OwnerProfileId, group.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ThreatId, group.ThreatId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.AlternateCandidateId, group.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.SelectedAltBotProfileId, group.AlternateBotProfileId, StringComparison.OrdinalIgnoreCase));
            if (assignment == null || assignment.DirectBotProfileIds.Count == 0)
            {
                admissionDeferralsTotal++;
                continue;
            }

            var convergenceGroup = convergenceSnapshot.ThreatGroups.FirstOrDefault(item =>
                string.Equals(item.OwnerProfileId, group.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ThreatId, group.ThreatId, StringComparison.OrdinalIgnoreCase));
            if (convergenceGroup == null)
            {
                admissionDeferralsTotal++;
                continue;
            }

            if (!TrySelectDirectMember(
                    assignment,
                    convergenceGroup,
                    now,
                    out var selected,
                    out DateTimeOffset memberEvidenceCapturedAtUtc,
                    out string selectionReason))
            {
                admissionDeferralsTotal++;
                VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                    $"TC_DIRECT_COMMIT_START_DEFERRED owner={Safe(owner)}; threat={Safe(group.ThreatId)}; epoch={lifecycle.PlanEpoch}; reason={Safe(selectionReason)}; mutation=false");
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(selected.BotProfileId, out var runtime)
                || runtime.BotOwner == null
                || runtime.BotOwner.IsDead)
            {
                admissionDeferralsTotal++;
                continue;
            }

            if (!TacticalIntentContinuityService.CanStartIntent(
                    selected.BotProfileId,
                    group.ThreatId,
                    TacticalIntentContinuityService.DirectCommitIntent,
                    now,
                    out string intentReason))
            {
                admissionDeferralsTotal++;
                VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                    $"TC_DIRECT_COMMIT_START_DEFERRED owner={Safe(owner)}; threat={Safe(group.ThreatId)}; epoch={lifecycle.PlanEpoch}; bot={Safe(selected.BotProfileId)}; reason={Safe(intentReason)}; tacticalIntentConflict=true; mutation=false");
                continue;
            }

            string movementMode = DefaultMovementMode;
            string breachEntryFallbackReason = "not_applicable";
            Vector3 destination;
            NavMeshPath preparedPath;
            float pathLength;
            float sourceRemainingLength;
            float verifiedThreatRemainingLength;
            string pathSummary = "not_attempted";

            BreachEntryOpportunity breachEntry = BreachEntryOpportunity.Empty;
            bool breachEntrySelected = string.Equals(readinessMode, "full_alt", StringComparison.OrdinalIgnoreCase)
                && BreachEntryOpportunityService.TryResolve(
                    assignment,
                    group,
                    convergenceSnapshot,
                    convergenceGroup,
                    selected,
                    runtime.BotOwner.Position,
                    now,
                    out breachEntry,
                    out breachEntryFallbackReason);
            if (breachEntrySelected
                && TryBuildBreachEntryFreshPath(
                    runtime.BotOwner.Position,
                    convergenceGroup.ThreatAnchorPosition,
                    breachEntry,
                    out destination,
                    out preparedPath,
                    out pathLength,
                    out sourceRemainingLength,
                    out verifiedThreatRemainingLength,
                    out pathSummary))
            {
                movementMode = BreachEntryOpportunityService.MovementMode;
            }
            else
            {
                if (breachEntrySelected)
                {
                    breachEntryFallbackReason = "fresh_breach_path_rejected:" + Safe(pathSummary);
                }
                if (!TryBuildBoundedFreshPath(
                        runtime.BotOwner.Position,
                        convergenceGroup.ThreatAnchorPosition,
                        out destination,
                        out preparedPath,
                        out pathLength,
                        out sourceRemainingLength,
                        out verifiedThreatRemainingLength,
                        out pathSummary))
                {
                    pathRejectsTotal++;
                    VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                        $"TC_DIRECT_COMMIT_START_DEFERRED owner={Safe(owner)}; threat={Safe(group.ThreatId)}; epoch={lifecycle.PlanEpoch}; bot={Safe(selected.BotProfileId)}; reason={Safe(pathSummary)}; breachEntryFallback={Safe(breachEntryFallbackReason)}; mutation=false");
                    continue;
                }
            }

            // Expensive NavMesh work may span changing combat evidence. Re-read every authority input
            // before touching scheduler or movement state; the plan epoch must still be exact.
            if (!IsStartPlanStillCurrent(group, lifecycle.PlanEpoch, readinessMode, now, out string currentPlanReason))
            {
                admissionDeferralsTotal++;
                VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                    $"TC_DIRECT_COMMIT_START_DEFERRED owner={Safe(owner)}; threat={Safe(group.ThreatId)}; epoch={lifecycle.PlanEpoch}; bot={Safe(selected.BotProfileId)}; reason=post_path_plan_revalidation:{Safe(currentPlanReason)}; mutation=false");
                continue;
            }
            if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(selected.BotProfileId, out var currentDecision))
            {
                admissionDeferralsTotal++;
                VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                    $"TC_DIRECT_COMMIT_START_DEFERRED owner={Safe(owner)}; threat={Safe(group.ThreatId)}; epoch={lifecycle.PlanEpoch}; bot={Safe(selected.BotProfileId)}; reason=post_path_decision_snapshot_missing; mutation=false");
                continue;
            }
            if (!DirectCommitAuthorityPolicy.IsTranslationCompatible(
                    currentDecision,
                    group.OwnerProfileId,
                    group.ThreatId,
                    memberEvidenceCapturedAtUtc,
                    now,
                    out string finalAuthorityReason))
            {
                admissionDeferralsTotal++;
                VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                    $"TC_DIRECT_COMMIT_START_DEFERRED owner={Safe(owner)}; threat={Safe(group.ThreatId)}; epoch={lifecycle.PlanEpoch}; bot={Safe(selected.BotProfileId)}; reason=post_path_authority_revalidation:{Safe(finalAuthorityReason)}; mutation=false");
                continue;
            }

            if (!MarksmanForwardRoleDisciplinePolicy.CanAuthorizeForwardTranslation(
                    currentDecision,
                    group.ThreatId,
                    destination,
                    "DirectCommit",
                    now,
                    out string roleDisciplineReason))
            {
                admissionDeferralsTotal++;
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"TC_DIRECT_COMMIT_START_DEFERRED owner={Safe(owner)}; threat={Safe(group.ThreatId)}; epoch={lifecycle.PlanEpoch}; bot={Safe(selected.BotProfileId)}; reason=marksman_role_discipline:{Safe(roleDisciplineReason)}; schedulerMutation=false; commandMutation=false; physicalTranslation=false; localCombatAuthority=SAIN");
                continue;
            }

            if (!SearchTranslationGateBridge.TryPreflightTacticalMovementLiveTranslation(
                    runtime.BotOwner,
                    group.ThreatId,
                    out string liveReason,
                    out string liveSummary,
                    allowStandAndShootCombatAdvance: true,
                    allowDistantVisibleSearchAction: true))
            {
                livePreflightDeferralsTotal++;
                VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                    $"TC_DIRECT_COMMIT_LIVE_SAIN_PREFLIGHT_DEFERRED owner={Safe(owner)}; threat={Safe(group.ThreatId)}; epoch={lifecycle.PlanEpoch}; bot={Safe(selected.BotProfileId)}; reason={Safe(liveReason)}; live={Safe(liveSummary)}; mutation=false; schedulerMutation=false; commandMutation=false");
                continue;
            }

            bool nativeDoorTraversalRequired = string.Equals(
                    movementMode,
                    BreachEntryOpportunityService.MovementMode,
                    StringComparison.OrdinalIgnoreCase)
                && breachEntry.NativeDoorTraversalRequired;
            bool breachThresholdSynchronizationRequired = string.Equals(
                movementMode,
                BreachEntryOpportunityService.MovementMode,
                StringComparison.OrdinalIgnoreCase);
            TimeSpan effectiveMaxLeaseDuration = MaxLeaseDuration
                + (breachThresholdSynchronizationRequired ? BreachThresholdSupportWaitAllowance : TimeSpan.Zero)
                + (nativeDoorTraversalRequired ? NativeDoorInteractionAllowance : TimeSpan.Zero);
            TimeSpan effectiveNoProgressDuration = nativeDoorTraversalRequired
                ? NoProgressDuration + NativeDoorInteractionAllowance
                : NoProgressDuration;

            if (!VanguardMainIntentScheduler.TryOpenTacticalCoordinatorDirectCommit(
                    currentDecision,
                    group.ThreatId,
                    lifecycle.PlanEpoch,
                    now,
                    effectiveMaxLeaseDuration,
                    effectiveNoProgressDuration,
                    out string windowId,
                    out string schedulerReason))
            {
                schedulerRejectsTotal++;
                SetCooldown(owner, now);
                VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                    $"TC_DIRECT_COMMIT_START_DEFERRED owner={Safe(owner)}; threat={Safe(group.ThreatId)}; epoch={lifecycle.PlanEpoch}; bot={Safe(selected.BotProfileId)}; reason=scheduler:{Safe(schedulerReason)}; mutation=false");
                continue;
            }

            string leaseId = "tc_direct_commit_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(selected.BotProfileId);
            DateTimeOffset maxUntil = now + effectiveMaxLeaseDuration;
            if (!VanguardReturnMovementCommandStore.Issue(
                    leaseId,
                    selected.OperatorId,
                    selected.BotProfileId,
                    destination,
                    CommandAnchorRadiusMeters,
                    false,
                    now,
                    maxUntil,
                    RequestKind,
                    pathSummary,
                    pathLength,
                    out string commandResult)
                || !VanguardReturnMovementCommandStore.TryGetActive(selected.BotProfileId, now, out var command)
                || !string.Equals(command.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(command.RequestKind, RequestKind, StringComparison.OrdinalIgnoreCase))
            {
                commandRejectsTotal++;
                VanguardMainIntentScheduler.FinishPrimaryWindow(
                    selected.BotProfileId,
                    now,
                    "Failed",
                    "tc_direct_commit_command_rejected:" + Safe(commandResult),
                    pathSummary,
                    windowId);
                SetCooldown(owner, now);
                continue;
            }

            var lease = new DirectCommitLeaseState(
                group.OwnerProfileId,
                group.ThreatId,
                lifecycle.PlanEpoch,
                group.AlternateCandidateId,
                group.AlternateBotProfileId,
                readinessMode,
                movementMode,
                selected.OperatorId,
                selected.BotProfileId,
                leaseId,
                windowId,
                command.Generation,
                destination,
                preparedPath,
                string.Equals(movementMode, BreachEntryOpportunityService.MovementMode, StringComparison.OrdinalIgnoreCase)
                    ? breachEntry
                    : BreachEntryOpportunity.Empty,
                memberEvidenceCapturedAtUtc,
                now,
                maxUntil,
                now + effectiveNoProgressDuration,
                runtime.BotOwner.Position,
                pathLength,
                sourceRemainingLength,
                verifiedThreatRemainingLength);

            if (!VanguardMainIntentScheduler.MarkTacticalCoordinatorDirectCommitStarted(
                    selected.BotProfileId,
                    leaseId,
                    lifecycle.PlanEpoch,
                    now,
                    lease.Summary,
                    windowId))
            {
                commandRejectsTotal++;
                VanguardReturnMovementCommandStore.ClearOwned(selected.BotProfileId, leaseId, now, "tc_direct_commit_scheduler_start_not_confirmed");
                VanguardMainIntentScheduler.FinishPrimaryWindow(selected.BotProfileId, now, "Failed", "tc_direct_commit_scheduler_start_not_confirmed", lease.Summary, windowId);
                SetCooldown(owner, now);
                continue;
            }

            lock (Sync)
            {
                AttemptedPlanEpochByOwnerThreatPhase[epochKey] = lifecycle.PlanEpoch;
                ActiveByOwner[owner] = lease;
            }
            TacticalIntentContinuityService.BeginOrRefresh(
                lease.BotProfileId,
                lease.OwnerProfileId,
                lease.ThreatId,
                TacticalIntentContinuityService.DirectCommitIntent,
                lease.PlanEpoch.ToString(CultureInfo.InvariantCulture),
                now,
                "direct_commit_physical_lease_started",
                protectsFromCohesion: true);
            startsTotal++;
            PublishExecutionReceipt(lease, "active", "movement_started:movement_mode=" + Safe(lease.MovementMode), now);
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_DIRECT_COMMIT_STARTED {lease.Summary}; readinessMode={Safe(readinessMode)}; movementMode={Safe(movementMode)}; breachEntryFallback={Safe(breachEntryFallbackReason)}; freshPath=true; freshWindow=true; freshLease=true; oneContributionPerPlanEpochPhase=true; maxAdvance={MaxAdvanceMeters:0.00}; ordinaryThreatStandoff={MinimumThreatStandoffMeters:0.00}; breachThreatStandoff={BreachEntryOpportunityService.MinimumPostEntryThreatRemainingMeters:0.00}; sourceRemaining={sourceRemainingLength:0.00}; verifiedThreatRemaining={verifiedThreatRemainingLength:0.00}; effectiveMaxLeaseSeconds={effectiveMaxLeaseDuration.TotalSeconds:0.00}; effectiveNoProgressSeconds={effectiveNoProgressDuration.TotalSeconds:0.00}; nativeDoorAllowanceSeconds={(nativeDoorTraversalRequired ? NativeDoorInteractionAllowance.TotalSeconds : 0d):0.00}; live={Safe(liveSummary)}; physicalBackend=SAIN_BotPathDataManual; sprint=false; vanguardDoorInteraction=false; nativeSainDoorTraversal={nativeDoorTraversalRequired}; fireCommand=false; targetMutation=false; localCombatAuthority=SAIN");
            return; // First executable slice permits only one new DirectCommit acquisition per tick.
        }
    }

    private static bool TrySelectDirectMember(
        ThreatGroupRouteAssignment assignment,
        ThreatGroupConvergenceAssessment convergenceGroup,
        DateTimeOffset now,
        out DirectRouteObservation selected,
        out DateTimeOffset memberEvidenceCapturedAtUtc,
        out string reason)
    {
        selected = null!;
        memberEvidenceCapturedAtUtc = DateTimeOffset.MinValue;
        reason = "no_admissible_direct_member";

        var candidates = new List<(DirectRouteObservation Route, OperatorDecisionSnapshot Decision, DateTimeOffset Evidence)>();
        foreach (var route in convergenceGroup.Routes
            .Where(item => assignment.DirectBotProfileIds.Contains(item.BotProfileId, StringComparer.OrdinalIgnoreCase))
            .Where(item => string.Equals(item.PathStatus, "complete", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.PathLengthMeters)
            .ThenBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase))
        {
            if (!RouteAssignmentService.TryResolveMemberEvidenceCapturedAtUtc(assignment, route.BotProfileId, out var evidenceCapturedAtUtc))
            {
                continue;
            }
            if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(route.BotProfileId, out var currentDecision))
            {
                continue;
            }
            if (!DirectCommitAuthorityPolicy.IsTranslationCompatible(
                    currentDecision,
                    assignment.OwnerProfileId,
                    assignment.ThreatId,
                    evidenceCapturedAtUtc,
                    now,
                    out _))
            {
                continue;
            }
            if (AntiCongaService.HasActiveAdvanceGateForBot(route.BotProfileId)
                || ChokeCommitSupportService.HasActiveContributionForBot(route.BotProfileId)
                || VanguardReturnMovementCommandStore.TryGetActive(route.BotProfileId, now, out _))
            {
                continue;
            }
            candidates.Add((route, currentDecision, evidenceCapturedAtUtc));
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        var winner = candidates[0];
        reason = "selected_shortest_current_complete_direct_route";
        if (VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(winner.Route.BotProfileId, out var winnerRuntime)
            && MarksmanForwardRoleDisciplinePolicy.IsDesignatedMarksmanIdentity(winnerRuntime.TacticalRole, winnerRuntime.TacticalSpecialty)
            && MarksmanForwardRoleDisciplinePolicy.TryClassifyHighThreatContact(assignment.ThreatId, out bool highThreat, out _)
            && highThreat)
        {
            foreach (var alternative in candidates)
            {
                if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(alternative.Route.BotProfileId, out var alternativeRuntime)
                    || MarksmanForwardRoleDisciplinePolicy.IsDesignatedMarksmanIdentity(alternativeRuntime.TacticalRole, alternativeRuntime.TacticalSpecialty)
                    || !MarksmanForwardRoleDisciplinePolicy.IsAssaultCapableRole(alternativeRuntime.TacticalRole))
                {
                    continue;
                }

                winner = alternative;
                reason = "selected_assault_capable_direct_member_over_marksman_for_high_threat";
                break;
            }
        }

        selected = winner.Route;
        memberEvidenceCapturedAtUtc = winner.Evidence;
        return true;
    }

    private static bool IsStartPlanStillCurrent(
        ThreatGroupSplitFlankStateObservation expectedGroup,
        long expectedPlanEpoch,
        string readinessMode,
        DateTimeOffset now,
        out string reason)
    {
        reason = "unknown";
        if (!SplitFlankStateService.TryGetSnapshot(out var stateSnapshot))
        {
            reason = "split_flank_state_missing";
            return false;
        }
        var currentGroup = stateSnapshot.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, expectedGroup.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, expectedGroup.ThreatId, StringComparison.OrdinalIgnoreCase));
        if (currentGroup == null || !IsGroupReadinessCompatible(currentGroup, readinessMode))
        {
            reason = "synchronized_readiness_changed:" + Safe(readinessMode);
            return false;
        }
        if (!string.Equals(currentGroup.AlternateCandidateId, expectedGroup.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(currentGroup.AlternateBotProfileId, expectedGroup.AlternateBotProfileId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "split_flank_identity_changed";
            return false;
        }
        if (!TacticalReplanService.TryGetSnapshot(out var replanSnapshot))
        {
            reason = "replan_snapshot_missing";
            return false;
        }
        var lifecycle = replanSnapshot.Plans.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, expectedGroup.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, expectedGroup.ThreatId, StringComparison.OrdinalIgnoreCase));
        if (lifecycle == null
            || lifecycle.PlanEpoch != expectedPlanEpoch
            || lifecycle.HardBreakRecommended
            || lifecycle.ReplanRecommended
            || !lifecycle.UpstreamPresent
            || !lifecycle.CommitReadyObserved
            || !string.Equals(lifecycle.AlternateCandidateId, expectedGroup.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(lifecycle.AlternateBotProfileId, expectedGroup.AlternateBotProfileId, StringComparison.OrdinalIgnoreCase)
            || !IsLifecycleCommitStateCompatible(lifecycle.CurrentState, readinessMode))
        {
            reason = "plan_epoch_or_commit_state_changed";
            return false;
        }
        reason = "current";
        return true;
    }

    private static bool IsLeasePlanCurrent(DirectCommitLeaseState lease, DateTimeOffset now, out string reason)
    {
        reason = "unknown";
        if (!SplitFlankStateService.TryGetSnapshot(out var stateSnapshot))
        {
            reason = "split_flank_state_missing";
            return false;
        }
        var group = stateSnapshot.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, lease.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, lease.ThreatId, StringComparison.OrdinalIgnoreCase));
        if (group == null || !IsGroupReadinessCompatible(group, lease.ReadinessMode))
        {
            reason = "synchronized_readiness_regressed:" + Safe(lease.ReadinessMode);
            return false;
        }
        if (!string.Equals(group.AlternateCandidateId, lease.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(group.AlternateBotProfileId, lease.AlternateBotProfileId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "alternate_assignment_identity_changed";
            return false;
        }
        if (!TacticalReplanService.TryGetSnapshot(out var replanSnapshot))
        {
            reason = "replan_snapshot_missing";
            return false;
        }
        var lifecycle = replanSnapshot.Plans.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, lease.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, lease.ThreatId, StringComparison.OrdinalIgnoreCase));
        if (lifecycle == null || lifecycle.PlanEpoch != lease.PlanEpoch)
        {
            reason = "plan_epoch_changed";
            return false;
        }
        if (lifecycle.HardBreakRecommended
            || lifecycle.ReplanRecommended
            || !lifecycle.UpstreamPresent
            || !lifecycle.CommitReadyObserved
            || !string.Equals(lifecycle.AlternateCandidateId, lease.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(lifecycle.AlternateBotProfileId, lease.AlternateBotProfileId, StringComparison.OrdinalIgnoreCase)
            || !IsLifecycleCommitStateCompatible(lifecycle.CurrentState, lease.ReadinessMode))
        {
            reason = "plan_no_longer_commit_ready:" + Safe(lifecycle.Reason);
            return false;
        }
        reason = "current";
        return true;
    }

    private static bool IsGroupReadinessCompatible(ThreatGroupSplitFlankStateObservation group, string readinessMode)
    {
        if (string.Equals(readinessMode, "full_alt", StringComparison.OrdinalIgnoreCase))
        {
            return group.FullAltEstablished && group.SynchronizedCommitReady;
        }

        return group.SynchronizedApproachCommitReady || group.SynchronizedCommitReady;
    }

    private static bool IsLifecycleCommitStateCompatible(string? state, string readinessMode)
    {
        string normalized = string.IsNullOrWhiteSpace(state) ? string.Empty : state.Trim();
        if (string.Equals(readinessMode, "full_alt", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(normalized, "commit_ready", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "commit_released", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "direct_commit_full_alt_active", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(normalized, "approach_commit_ready", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "approach_commit_released", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "direct_commit_approach_active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "commit_ready", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "commit_released", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "direct_commit_full_alt_active", StringComparison.OrdinalIgnoreCase);
    }

    private static void UpdateActiveLeases(DateTimeOffset now)
    {
        DirectCommitLeaseState[] active;
        lock (Sync)
        {
            active = ActiveByOwner.Values.ToArray();
        }

        foreach (var lease in active)
        {
            TacticalIntentContinuityService.BeginOrRefresh(
                lease.BotProfileId,
                lease.OwnerProfileId,
                lease.ThreatId,
                TacticalIntentContinuityService.DirectCommitIntent,
                lease.PlanEpoch.ToString(CultureInfo.InvariantCulture),
                now,
                "direct_commit_physical_lease_active",
                protectsFromCohesion: true);

            if (now >= lease.MaxUntilUtc)
            {
                if (string.Equals(lease.MovementMode, BreachEntryOpportunityService.MovementMode, StringComparison.OrdinalIgnoreCase))
                {
                    FinishLease(lease, now, "Interrupted", "breach_entry_window_expired_before_anchor", safetyCooldown: false);
                }
                else
                {
                    FinishLease(lease, now, "Completed", "bounded_commit_window_complete", safetyCooldown: false);
                }
                continue;
            }
            if (!IsLeasePlanCurrent(lease, now, out string planReason))
            {
                FinishLease(lease, now, "Interrupted", "plan_invalidated:" + planReason, safetyCooldown: false);
                continue;
            }
            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(lease.BotProfileId, out var runtime)
                || runtime.BotOwner == null
                || runtime.BotOwner.IsDead)
            {
                FinishLease(lease, now, "Interrupted", "operator_missing_or_dead", safetyCooldown: true);
                continue;
            }
            if (!VanguardReturnMovementCommandStore.TryGetExactOwned(
                    lease.BotProfileId,
                    lease.LeaseId,
                    RequestKind,
                    lease.Generation,
                    now,
                    out _,
                    out string commandReason))
            {
                FinishLease(lease, now, "Interrupted", "movement_command_changed:" + commandReason, safetyCooldown: false);
                continue;
            }
            if (!VanguardMainIntentScheduler.IsTacticalCoordinatorDirectCommitCurrent(
                    lease.BotProfileId,
                    now,
                    lease.WindowId,
                    lease.LeaseId,
                    lease.PlanEpoch,
                    out string schedulerReason))
            {
                FinishLease(lease, now, "Interrupted", "scheduler_changed:" + schedulerReason, safetyCooldown: false);
                continue;
            }
            string authorityReason = "decision_snapshot_missing";
            if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(lease.BotProfileId, out var decision)
                || !DirectCommitAuthorityPolicy.IsTranslationCompatible(
                    decision,
                    lease.OwnerProfileId,
                    lease.ThreatId,
                    lease.MemberEvidenceCapturedAtUtc,
                    now,
                    out authorityReason))
            {
                FinishLease(lease, now, "Interrupted", "local_authority_changed:" + authorityReason, safetyCooldown: false);
                continue;
            }

            if (!MarksmanForwardRoleDisciplinePolicy.CanAuthorizeForwardTranslation(
                    decision,
                    lease.ThreatId,
                    lease.Destination,
                    "DirectCommit",
                    now,
                    out string roleDisciplineReason))
            {
                FinishLease(lease, now, "Interrupted", "marksman_role_discipline_changed:" + roleDisciplineReason, safetyCooldown: false);
                continue;
            }

            Vector3 current = runtime.BotOwner.Position;
            float step = HorizontalDistance(current, lease.LastWorldPosition);
            if (step >= ProgressDistanceMeters)
            {
                lease.PhysicalMeters += step;
                physicalMetersTotal += step;
                lease.LastWorldPosition = current;
                lease.LastProgressAtUtc = now;
                lease.NoProgressUntilUtc = now + NoProgressDuration;
            }

            float anchorDistance = HorizontalDistance(current, lease.Destination);
            if (anchorDistance <= CommandAnchorRadiusMeters + 0.35f)
            {
                FinishLease(lease, now, "Completed", "bounded_direct_commit_anchor_reached", safetyCooldown: false);
                continue;
            }
            if (now >= lease.NoProgressUntilUtc)
            {
                FinishLease(lease, now, "Interrupted", "bounded_direct_commit_no_progress", safetyCooldown: false);
                continue;
            }
            if (VanguardReturnMovementCommandStore.TryConsumePathInvalid(lease.BotProfileId, lease.LeaseId, lease.Generation, now, out var pathInvalid))
            {
                FinishLease(lease, now, "Failed", "physical_path_invalid:" + Safe(pathInvalid), safetyCooldown: true);
                continue;
            }
            if (VanguardReturnMovementCommandStore.TryConsumePhysicalBackendFailure(lease.BotProfileId, lease.LeaseId, lease.Generation, now, out var backendFailure))
            {
                FinishLease(lease, now, "Failed", "physical_backend_failure:" + Safe(backendFailure), safetyCooldown: true);
            }
        }
    }

    private static bool TryBuildBreachEntryFreshPath(
        Vector3 start,
        Vector3 threatAnchor,
        BreachEntryOpportunity opportunity,
        out Vector3 destination,
        out NavMeshPath preparedPath,
        out float preparedLength,
        out float sourceRemainingLength,
        out float verifiedThreatRemainingLength,
        out string summary)
    {
        destination = start;
        preparedPath = new NavMeshPath();
        preparedLength = 0f;
        sourceRemainingLength = 0f;
        verifiedThreatRemainingLength = 0f;

        if (opportunity == null || string.IsNullOrWhiteSpace(opportunity.BotProfileId))
        {
            summary = "breach_entry_opportunity_missing";
            return false;
        }
        if (!TryCalculateFreshCompletePath(start, threatAnchor, out _, out sourceRemainingLength, out string sourceSummary))
        {
            summary = "breach_entry_source_" + sourceSummary;
            return false;
        }
        if (!TryCalculateFreshCompletePath(start, opportunity.EntryAnchor, out preparedPath, out preparedLength, out string entrySummary))
        {
            summary = "breach_entry_anchor_" + entrySummary;
            return false;
        }
        if (preparedLength < MinimumAdvanceMeters * 0.65f
            || preparedLength > BreachEntryOpportunityService.MaximumEntryTranslationMeters + 1.25f)
        {
            summary = "breach_entry_path_length_out_of_contract:length=" + preparedLength.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }
        if (!BreachEntryOpportunityService.ValidatePreparedEntryPath(opportunity, preparedPath.corners, out string portalReason))
        {
            summary = "breach_entry_portal_revalidation_failed:" + Safe(portalReason);
            return false;
        }

        destination = preparedPath.corners[preparedPath.corners.Length - 1];
        if (!NavMesh.SamplePosition(threatAnchor + Vector3.up * 0.25f, out var threatHit, 1.5f, NavMesh.AllAreas))
        {
            summary = "breach_entry_final_threat_anchor_navmesh_sample_failed";
            return false;
        }
        var threatPath = new NavMeshPath();
        bool threatCalculated;
        try
        {
            threatCalculated = NavMesh.CalculatePath(destination, threatHit.position, NavMesh.AllAreas, threatPath);
        }
        catch (Exception exception)
        {
            summary = "breach_entry_final_threat_calculate_exception:" + exception.GetType().Name;
            return false;
        }
        if (!threatCalculated
            || threatPath.status != NavMeshPathStatus.PathComplete
            || threatPath.corners == null
            || threatPath.corners.Length < 2)
        {
            summary = "breach_entry_final_threat_path_not_complete:calculated=" + Bool(threatCalculated)
                + ";status=" + threatPath.status
                + ";corners=" + (threatPath.corners?.Length ?? 0).ToString(CultureInfo.InvariantCulture);
            return false;
        }
        verifiedThreatRemainingLength = PathLength(threatPath.corners);
        if (verifiedThreatRemainingLength + 0.01f < BreachEntryOpportunityService.MinimumPostEntryThreatRemainingMeters)
        {
            summary = "breach_entry_final_threat_standoff_below_minimum:remaining="
                + verifiedThreatRemainingLength.ToString("0.00", CultureInfo.InvariantCulture)
                + ";minimum=" + BreachEntryOpportunityService.MinimumPostEntryThreatRemainingMeters.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        summary = "fresh_breach_entry_path;choke=" + Safe(opportunity.ChokeId)
            + ";door=" + Safe(opportunity.DoorId)
            + ";doorState=" + Safe(opportunity.DoorState)
            + ";nativeDoorTraversalRequired=" + Bool(opportunity.NativeDoorTraversalRequired)
            + ";advance=" + preparedLength.ToString("0.00", CultureInfo.InvariantCulture)
            + ";verifiedThreatRemaining=" + verifiedThreatRemainingLength.ToString("0.00", CultureInfo.InvariantCulture)
            + ";sourceProof=" + Safe(sourceSummary)
            + ";entryProof=" + Safe(entrySummary)
            + ";portalProof=" + Safe(portalReason);
        return true;
    }

    private static bool TryBuildBoundedFreshPath(
        Vector3 start,
        Vector3 threatAnchor,
        out Vector3 destination,
        out NavMeshPath preparedPath,
        out float preparedLength,
        out float sourceRemainingLength,
        out float verifiedThreatRemainingLength,
        out string summary)
    {
        destination = start;
        preparedPath = new NavMeshPath();
        preparedLength = 0f;
        sourceRemainingLength = 0f;
        verifiedThreatRemainingLength = 0f;

        if (!TryCalculateFreshCompletePath(start, threatAnchor, out var sourcePath, out sourceRemainingLength, out string sourceSummary))
        {
            summary = "direct_source_" + sourceSummary;
            return false;
        }

        float availableAdvance = Math.Max(0f, sourceRemainingLength - MinimumThreatStandoffMeters);
        float advance = Math.Min(MaxAdvanceMeters, availableAdvance);
        if (advance < MinimumAdvanceMeters)
        {
            summary = "bounded_advance_below_minimum:remaining=" + sourceRemainingLength.ToString("0.00", CultureInfo.InvariantCulture)
                + ";available=" + availableAdvance.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        Vector3 rawDestination = PointAlongPath(sourcePath.corners, advance);
        if (!NavMesh.SamplePosition(rawDestination + Vector3.up * 0.25f, out var destinationHit, 1.25f, NavMesh.AllAreas))
        {
            summary = "bounded_anchor_navmesh_sample_failed";
            return false;
        }
        destination = destinationHit.position;

        if (!TryCalculateFreshCompletePath(start, destination, out preparedPath, out preparedLength, out string boundedSummary))
        {
            summary = "bounded_" + boundedSummary;
            return false;
        }
        if (preparedLength < MinimumAdvanceMeters * 0.65f || preparedLength > MaxAdvanceMeters + 2.0f)
        {
            summary = "bounded_path_length_out_of_contract:length=" + preparedLength.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        // The bounded path helper may re-sample its anchor. Bind the command destination to the actual
        // complete-path endpoint, then prove that this final endpoint still leaves the required path
        // distance to the current threat anchor. This closes the post-SamplePosition standoff hole.
        destination = preparedPath.corners[preparedPath.corners.Length - 1];
        if (!NavMesh.SamplePosition(threatAnchor + Vector3.up * 0.25f, out var finalThreatHit, 1.5f, NavMesh.AllAreas))
        {
            summary = "final_threat_standoff_anchor_navmesh_sample_failed";
            return false;
        }
        var finalThreatPath = new NavMeshPath();
        bool finalThreatCalculated;
        try
        {
            finalThreatCalculated = NavMesh.CalculatePath(destination, finalThreatHit.position, NavMesh.AllAreas, finalThreatPath);
        }
        catch (Exception exception)
        {
            summary = "final_threat_standoff_calculate_exception:" + exception.GetType().Name;
            return false;
        }
        if (!finalThreatCalculated
            || finalThreatPath.status != NavMeshPathStatus.PathComplete
            || finalThreatPath.corners == null
            || finalThreatPath.corners.Length < 2)
        {
            summary = "final_threat_standoff_path_not_complete:calculated=" + Bool(finalThreatCalculated)
                + ";status=" + finalThreatPath.status
                + ";corners=" + (finalThreatPath.corners?.Length ?? 0).ToString(CultureInfo.InvariantCulture);
            return false;
        }
        verifiedThreatRemainingLength = PathLength(finalThreatPath.corners);
        string finalThreatSummary = "complete;length=" + verifiedThreatRemainingLength.ToString("0.00", CultureInfo.InvariantCulture)
            + ";corners=" + finalThreatPath.corners.Length.ToString(CultureInfo.InvariantCulture);
        if (verifiedThreatRemainingLength + 0.01f < MinimumThreatStandoffMeters)
        {
            summary = "final_threat_standoff_below_minimum:remaining=" + verifiedThreatRemainingLength.ToString("0.00", CultureInfo.InvariantCulture)
                + ";minimum=" + MinimumThreatStandoffMeters.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        summary = "fresh_bounded_direct_commit_path;source=" + sourceRemainingLength.ToString("0.00", CultureInfo.InvariantCulture)
            + ";advance=" + preparedLength.ToString("0.00", CultureInfo.InvariantCulture)
            + ";verifiedThreatRemaining=" + verifiedThreatRemainingLength.ToString("0.00", CultureInfo.InvariantCulture)
            + ";corners=" + preparedPath.corners.Length.ToString(CultureInfo.InvariantCulture)
            + ";sourceProof=" + Safe(sourceSummary)
            + ";boundedProof=" + Safe(boundedSummary)
            + ";finalThreatProof=" + Safe(finalThreatSummary);
        return true;
    }

    private static bool TryCalculateFreshCompletePath(
        Vector3 start,
        Vector3 anchor,
        out NavMeshPath path,
        out float length,
        out string summary)
    {
        path = new NavMeshPath();
        length = 0f;
        if (!NavMesh.SamplePosition(start + Vector3.up * 0.25f, out var startHit, 2.0f, NavMesh.AllAreas))
        {
            summary = "start_navmesh_sample_failed";
            return false;
        }
        if (!NavMesh.SamplePosition(anchor + Vector3.up * 0.25f, out var anchorHit, 1.5f, NavMesh.AllAreas))
        {
            summary = "anchor_navmesh_sample_failed";
            return false;
        }
        bool calculated;
        try
        {
            calculated = NavMesh.CalculatePath(startHit.position, anchorHit.position, NavMesh.AllAreas, path);
        }
        catch (Exception exception)
        {
            summary = "calculate_exception:" + exception.GetType().Name;
            return false;
        }
        if (!calculated || path.status != NavMeshPathStatus.PathComplete || path.corners == null || path.corners.Length < 2)
        {
            summary = "path_not_complete:calculated=" + Bool(calculated) + ";status=" + path.status + ";corners=" + (path.corners?.Length ?? 0).ToString(CultureInfo.InvariantCulture);
            return false;
        }
        length = PathLength(path.corners);
        summary = "complete;length=" + length.ToString("0.00", CultureInfo.InvariantCulture) + ";corners=" + path.corners.Length.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static Vector3 PointAlongPath(IReadOnlyList<Vector3> corners, float distance)
    {
        if (corners == null || corners.Count == 0) return Vector3.zero;
        if (corners.Count == 1 || distance <= 0f) return corners[0];
        float remaining = distance;
        for (int index = 1; index < corners.Count; index++)
        {
            Vector3 from = corners[index - 1];
            Vector3 to = corners[index];
            float segment = Vector3.Distance(from, to);
            if (segment <= 0.001f) continue;
            if (remaining <= segment)
            {
                return Vector3.Lerp(from, to, Mathf.Clamp01(remaining / segment));
            }
            remaining -= segment;
        }
        return corners[corners.Count - 1];
    }

    private static float PathLength(IReadOnlyList<Vector3> corners)
    {
        float length = 0f;
        if (corners == null) return length;
        for (int index = 1; index < corners.Count; index++)
        {
            length += Vector3.Distance(corners[index - 1], corners[index]);
        }
        return length;
    }

    private static bool FinishLease(
        DirectCommitLeaseState lease,
        DateTimeOffset now,
        string outcome,
        string reason,
        bool safetyCooldown)
    {
        lock (Sync)
        {
            if (ActiveByOwner.TryGetValue(lease.OwnerProfileId, out var active)
                && string.Equals(active.LeaseId, lease.LeaseId, StringComparison.OrdinalIgnoreCase))
            {
                ActiveByOwner.Remove(lease.OwnerProfileId);
            }
        }

        bool releasedBoundPath = SearchTranslationGateBridge.ReleaseTacticalMovementPathBinding(
            lease.BotProfileId,
            RequestKind,
            lease.LeaseId,
            lease.Generation,
            "tc_direct_commit_finished:" + reason);
        string clearResult = VanguardReturnMovementCommandStore.ClearOwned(
            lease.BotProfileId,
            lease.LeaseId,
            lease.StartedAtUtc,
            "tc_direct_commit_finished:" + reason);
        VanguardMainIntentScheduler.FinishPrimaryWindow(
            lease.BotProfileId,
            now,
            outcome,
            reason,
            lease.Summary + ";clear=" + Safe(clearResult),
            lease.WindowId);

        TacticalIntentContinuityService.EndIntent(
            lease.BotProfileId,
            TacticalIntentContinuityService.DirectCommitIntent,
            now,
            "direct_commit_terminal:" + outcome + ":" + reason);

        if (string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase)) completesTotal++;
        else if (string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase)) failuresTotal++;
        else interruptsTotal++;
        if (safetyCooldown || !string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            SetCooldown(lease.OwnerProfileId, now);
        }

        string receiptReason = reason + ":movement_mode=" + Safe(lease.MovementMode);
        PublishExecutionReceipt(lease, outcome.ToLowerInvariant(), receiptReason, now);
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_DIRECT_COMMIT_FINISHED {lease.Summary}; outcome={Safe(outcome)}; reason={Safe(reason)}; commandClear={Safe(clearResult)}; sainPathReleased={Bool(releasedBoundPath)}; physicalMeters={lease.PhysicalMeters:0.00}; nativeTicks={lease.TranslationCompatibleNativeTicks}; staleResume=false; planEpochBound=true; freshReacquireRequiresNewPlanEpochOrDistinctReadinessPhase=true; samePhaseReacquire=false; fireCommand=false; targetMutation=false");
        return releasedBoundPath;
    }

    private static void PublishExecutionReceipt(
        DirectCommitLeaseState lease,
        string state,
        string reason,
        DateTimeOffset observedAtUtc)
    {
        var receipt = new DirectCommitExecutionReceipt(
            lease.OwnerProfileId,
            lease.ThreatId,
            lease.PlanEpoch,
            lease.ReadinessMode,
            lease.MovementMode,
            lease.AlternateCandidateId,
            lease.AlternateBotProfileId,
            lease.OperatorId,
            lease.BotProfileId,
            lease.LeaseId,
            lease.Generation,
            Safe(state),
            Safe(reason),
            lease.StartedAtUtc,
            observedAtUtc,
            lease.PhysicalMeters,
            lease.TranslationCompatibleNativeTicks);

        DirectCommitExecutionReceipt[] published;
        lock (Sync)
        {
            ExecutionReceiptByPlanPhase[ExecutionReceiptKey(lease.OwnerProfileId, lease.ThreatId, lease.PlanEpoch, lease.ReadinessMode)] = receipt;
            if (ExecutionReceiptByPlanPhase.Count > MaxExecutionReceipts)
            {
                foreach (string staleKey in ExecutionReceiptByPlanPhase
                    .OrderBy(pair => pair.Value.ObservedAtUtc)
                    .Take(ExecutionReceiptByPlanPhase.Count - MaxExecutionReceipts)
                    .Select(pair => pair.Key)
                    .ToArray())
                {
                    ExecutionReceiptByPlanPhase.Remove(staleKey);
                }
            }
            published = ExecutionReceiptByPlanPhase.Values
                .OrderBy(item => item.OwnerProfileId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ThreatId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.PlanEpoch)
                .ThenBy(item => item.ReadinessMode, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            executionSnapshot = new DirectCommitExecutionSnapshot(observedAtUtc, published);
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_DIRECT_COMMIT_EXECUTION_RECEIPT owner={Safe(receipt.OwnerProfileId)}; threat={Safe(receipt.ThreatId)}; epoch={receipt.PlanEpoch}; readinessMode={Safe(receipt.ReadinessMode)}; movementMode={Safe(receipt.MovementMode)}; bot={Safe(receipt.BotProfileId)}; lease={Safe(receipt.LeaseId)}; generation={receipt.Generation}; state={Safe(receipt.State)}; terminal={Bool(receipt.IsTerminal)}; completed={Bool(receipt.IsCompleted)}; freshPlanEpochRequired={Bool(receipt.RequiresFreshPlanEpoch)}; reason={Safe(receipt.Reason)}; physicalMeters={receipt.PhysicalMeters:0.00}; nativeTicks={receipt.NativeTicks}; observationOnly=true; movementAuthority=false; fireCommand=false; targetMutation=false");
    }

    private static string ExecutionReceiptKey(string ownerProfileId, string threatId, long planEpoch, string readinessMode)
        => NormalizeKey(ownerProfileId) + "|" + NormalizeKey(threatId) + "|" + planEpoch.ToString(CultureInfo.InvariantCulture) + "|" + NormalizeKey(readinessMode);

    internal static bool IsLayerContextCurrent(
        string botProfileId,
        VanguardReturnMovementCommand command,
        DateTimeOffset now,
        out string reason)
    {
        reason = "unknown";
        if (string.IsNullOrWhiteSpace(botProfileId)
            || !DirectCommitAuthorityPolicy.IsDirectCommitRequest(command.RequestKind))
        {
            reason = "command_or_identity_missing";
            return false;
        }

        DirectCommitLeaseState? lease = FindActiveByBot(botProfileId);
        if (lease == null
            || !string.Equals(lease.LeaseId, command.LeaseId, StringComparison.OrdinalIgnoreCase)
            || lease.Generation != command.Generation)
        {
            reason = "direct_commit_lease_identity_changed";
            return false;
        }

        if (!IsLeasePlanCurrent(lease, now, out string planReason))
        {
            reason = "plan_invalidated:" + planReason;
            return false;
        }
        if (!VanguardMainIntentScheduler.IsTacticalCoordinatorDirectCommitCurrent(
                lease.BotProfileId,
                now,
                lease.WindowId,
                lease.LeaseId,
                lease.PlanEpoch,
                out string schedulerReason))
        {
            reason = "scheduler_changed:" + schedulerReason;
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(lease.BotProfileId, out var decision))
        {
            reason = "decision_snapshot_missing";
            return false;
        }
        string authorityReason = "decision_snapshot_missing";
        if (!DirectCommitAuthorityPolicy.IsTranslationCompatible(
                decision,
                lease.OwnerProfileId,
                lease.ThreatId,
                lease.MemberEvidenceCapturedAtUtc,
                now,
                out authorityReason))
        {
            reason = "local_authority_changed:" + authorityReason;
            return false;
        }

        if (!MarksmanForwardRoleDisciplinePolicy.CanAuthorizeForwardTranslation(
                decision,
                lease.ThreatId,
                lease.Destination,
                "DirectCommit",
                now,
                out string roleDisciplineReason))
        {
            reason = "marksman_role_discipline_not_current:" + roleDisciplineReason;
            return false;
        }

        reason = "direct_commit_layer_context_current:" + authorityReason;
        return true;
    }

    private static DirectCommitLeaseState? FindActiveByBot(string botProfileId)
    {
        lock (Sync)
        {
            return ActiveByOwner.Values.FirstOrDefault(item => string.Equals(item.BotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void SetCooldown(string ownerProfileId, DateTimeOffset now)
    {
        lock (Sync)
        {
            CooldownUntilByOwner[ownerProfileId] = now + RestartCooldown;
        }
    }

    private static string PlanEpochKey(string ownerProfileId, string threatId, string readinessMode)
        => NormalizeKey(ownerProfileId) + "|" + NormalizeKey(threatId) + "|" + NormalizeKey(readinessMode);

    private static string NormalizeKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();

    private static void EmitSummaryIfDue(DateTimeOffset now)
    {
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_DIRECT_COMMIT_BOOT enabled=true; mode=bounded_synchronized_direct_translation; readinessModes=full_alt_or_near_commit_approach; movementModes={DefaultMovementMode}|{BreachEntryOpportunityService.MovementMode}; breachEntry=confirmed_traversable_HardChoke_after_FullAlt_pressure; closedDoorOwner=SAIN_BotPathDataManual; request={RequestKind}; maxAdvance={MaxAdvanceMeters:0.00}; minAdvance={MinimumAdvanceMeters:0.00}; threatStandoff={MinimumThreatStandoffMeters:0.00}; maxLeaseSeconds={MaxLeaseDuration.TotalSeconds:0.00}; noProgressSeconds={NoProgressDuration.TotalSeconds:0.00}; oneNewCommitPerTick=true; oneActivePerOwner=true; oneContributionPerPlanEpochPhase=true; combatAdvance=Search_or_GroupSearch_or_StandAndShoot_or_distant_visible_Search; seekCoverAdvance=false; closeDogfightAdvance=false; sprint=false; physicalBackend=SAIN_BotPathDataManual; fireCommand=false; targetMutation=false; failOpen=true");
        }
        if (now < nextSummaryAtUtc) return;
        nextSummaryAtUtc = now + SummaryInterval;
        int active;
        lock (Sync) active = ActiveByOwner.Count;
        VanguardClientDiagnosticsLog.DiagnosticSummary(StatusTag, "TC_DIRECT_COMMIT_SUMMARY", () =>
            $"TC_DIRECT_COMMIT_SUMMARY active={active}; startsTotal={startsTotal}; completesTotal={completesTotal}; interruptsTotal={interruptsTotal}; failuresTotal={failuresTotal}; schedulerRejectsTotal={schedulerRejectsTotal}; commandRejectsTotal={commandRejectsTotal}; pathRejectsTotal={pathRejectsTotal}; admissionDeferralsTotal={admissionDeferralsTotal}; livePreflightDeferralsTotal={livePreflightDeferralsTotal}; nativeTicksTotal={nativeTicksTotal}; physicalMetersTotal={physicalMetersTotal:0.00}; directCommitExecution=true; boundedTranslationOnly=true; oneContributionPerPlanEpochPhase=true; synchronizedAssault=false; fireCommand=false; targetMutation=false; staleResume=false");
    }

    private static float HorizontalDistance(Vector3 first, Vector3 second)
    {
        Vector3 delta = first - second;
        delta.y = 0f;
        return delta.magnitude;
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    private static string Bool(bool value) => value ? "true" : "false";

    private sealed class DirectCommitLeaseState
    {
        public DirectCommitLeaseState(
            string ownerProfileId,
            string threatId,
            long planEpoch,
            string alternateCandidateId,
            string alternateBotProfileId,
            string readinessMode,
            string movementMode,
            string operatorId,
            string botProfileId,
            string leaseId,
            string windowId,
            long generation,
            Vector3 destination,
            NavMeshPath preparedPath,
            BreachEntryOpportunity breachEntry,
            DateTimeOffset memberEvidenceCapturedAtUtc,
            DateTimeOffset startedAtUtc,
            DateTimeOffset maxUntilUtc,
            DateTimeOffset noProgressUntilUtc,
            Vector3 startPosition,
            float pathLengthMeters,
            float sourceRemainingLengthMeters,
            float verifiedThreatRemainingLengthMeters)
        {
            OwnerProfileId = ownerProfileId;
            ThreatId = threatId;
            PlanEpoch = planEpoch;
            AlternateCandidateId = alternateCandidateId;
            AlternateBotProfileId = alternateBotProfileId;
            ReadinessMode = readinessMode;
            MovementMode = movementMode;
            OperatorId = operatorId;
            BotProfileId = botProfileId;
            LeaseId = leaseId;
            WindowId = windowId;
            Generation = generation;
            Destination = destination;
            PreparedPath = preparedPath;
            BreachEntry = breachEntry;
            MemberEvidenceCapturedAtUtc = memberEvidenceCapturedAtUtc;
            StartedAtUtc = startedAtUtc;
            MaxUntilUtc = maxUntilUtc;
            NoProgressUntilUtc = noProgressUntilUtc;
            LastWorldPosition = startPosition;
            LastProgressAtUtc = startedAtUtc;
            PathLengthMeters = pathLengthMeters;
            SourceRemainingLengthMeters = sourceRemainingLengthMeters;
            VerifiedThreatRemainingLengthMeters = verifiedThreatRemainingLengthMeters;
        }

        public string OwnerProfileId { get; }
        public string ThreatId { get; }
        public long PlanEpoch { get; }
        public string AlternateCandidateId { get; }
        public string AlternateBotProfileId { get; }
        public string ReadinessMode { get; }
        public string MovementMode { get; }
        public string OperatorId { get; }
        public string BotProfileId { get; }
        public string LeaseId { get; }
        public string WindowId { get; }
        public long Generation { get; }
        public Vector3 Destination { get; }
        public NavMeshPath PreparedPath { get; }
        public BreachEntryOpportunity BreachEntry { get; }
        public DateTimeOffset MemberEvidenceCapturedAtUtc { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public DateTimeOffset MaxUntilUtc { get; }
        public DateTimeOffset NoProgressUntilUtc { get; set; }
        public DateTimeOffset LastProgressAtUtc { get; set; }
        public Vector3 LastWorldPosition { get; set; }
        public float PathLengthMeters { get; }
        public float SourceRemainingLengthMeters { get; }
        public float VerifiedThreatRemainingLengthMeters { get; }
        public float PhysicalMeters { get; set; }
        public int TranslationCompatibleNativeTicks { get; set; }
        public DateTimeOffset? ThresholdSupportWaitStartedAtUtc { get; set; }
        public string ThresholdSupportBotProfileId { get; set; } = string.Empty;
        public DateTimeOffset? ThresholdSupportEstablishedAtUtc { get; set; }
        public string Summary => "owner=" + Safe(OwnerProfileId)
            + ";threat=" + Safe(ThreatId)
            + ";epoch=" + PlanEpoch.ToString(CultureInfo.InvariantCulture)
            + ";altAxis=" + Safe(AlternateCandidateId)
            + ";altBot=" + Safe(AlternateBotProfileId)
            + ";readinessMode=" + Safe(ReadinessMode)
            + ";movementMode=" + Safe(MovementMode)
            + ";breachChoke=" + Safe(BreachEntry.ChokeId)
            + ";breachDoor=" + Safe(BreachEntry.DoorId)
            + ";thresholdSupportBot=" + Safe(ThresholdSupportBotProfileId)
            + ";thresholdSupportEstablished=" + Bool(ThresholdSupportEstablishedAtUtc.HasValue)
            + ";operator=" + Safe(OperatorId)
            + ";bot=" + Safe(BotProfileId)
            + ";lease=" + Safe(LeaseId)
            + ";window=" + Safe(WindowId)
            + ";generation=" + Generation.ToString(CultureInfo.InvariantCulture)
            + ";destination=(" + Destination.x.ToString("0.00", CultureInfo.InvariantCulture) + "," + Destination.y.ToString("0.00", CultureInfo.InvariantCulture) + "," + Destination.z.ToString("0.00", CultureInfo.InvariantCulture) + ")"
            + ";pathLength=" + PathLengthMeters.ToString("0.00", CultureInfo.InvariantCulture)
            + ";sourceRemaining=" + SourceRemainingLengthMeters.ToString("0.00", CultureInfo.InvariantCulture)
            + ";verifiedThreatRemaining=" + VerifiedThreatRemainingLengthMeters.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
#endif
