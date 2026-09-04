#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.External;
using Vanguard.Client.Runtime.Movement.Brain;

// Responsibility: physically brings an Operator back toward the squad when the movement policy has already declared a hard-return recovery necessary.
// Flow: The granted return command is rechecked against current distance/threat state, a safe owner-relative NavMesh anchor is chosen, conflicting external movement is paused narrowly, and progress is driven until the Operator is recovered or the command is safely aborted.
// Authority boundary: this executor consumes an existing hard-return grant; combat, grenade and medical safety may interrupt it, and it cannot create its own return intent.
// Invariant: every return lease has a bounded generation/timeout and deterministic cleanup, so stale recovery movement cannot survive a superseding command or raid reset.
namespace Vanguard.Client.Runtime.Movement;

/// <summary>
/// Vanguard keeps the Vanguard-Vanguard active scope but replaces the unreliable one-shot reflection GoToPoint bridge
/// with a BigBrain movement layer that owns GoToSomePointData.SetPoint/UpdateToGo while the Vanguard
/// MovementLease is active.  The executor remains the orchestrator: it validates eligibility/path/authority,
/// issues a command to the bridge, monitors completion/interruption/timeout and clears the command on outcome.
/// </summary>
internal static class VanguardHardReturnMovementExecutor
{
    public const string StatusTag = "VANGUARD_RETURN_AUTHORITY_LOCK_OK";
    private const string MoveBridgeStatusTag = "VANGUARD_MOVE_BRIDGE_LAYER_OK";
    private const string ReturnContinuationStatusTag = "VANGUARD_RETURN_CONTINUATION_OK";
    private const string AnchorScoreStatusTag = "VANGUARD_ANCHOR_SCORE_OK";
    private const string MainSchedulerStatusTag = "VANGUARD_CORE_DECISION_OK";
    private const string CleanAuthStatusTag = "VANGUARD_CLEAN_AUTH_OK";
    private const string GoToSomePointBridgeStatusTag = "VANGUARD_GOTOSOMEPOINT_BRIDGE_OK";
    private const string ActionRallyStatusTag = "VANGUARD_ACTION_RALLY_RETURN_OK";
    private const string ReturnPathValidationCompatibilityTag = "VANGUARD_RETURN_PATH_VALIDATION_OK";
    private const string PendingStatusTag = "VANGUARD_PREEMPT_PENDING_OK";
    private const string HardReturnCompatibilityTag = "VANGUARD_HARD_RETURN_ACTIVE_OK";
    private const string SainBoundaryCompatibilityTag = "VANGUARD_SAIN_BOUNDARY_RETURN_ACTIVE_OK";
    private const string IsolatedCombatReleaseStatusTag = "VANGUARD_ISOLATED_COMBAT_RELEASE_OK";
    private const string HardRegroupSprintStatusTag = "VANGUARD_HARD_REGROUP_SPRINT_OK";

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(0.30d);
    private static TimeSpan StartCooldown => TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementLeaseStartCooldownSeconds);
    private static TimeSpan FailureCooldown => TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementLeaseFailureCooldownSeconds);
    private static TimeSpan AbortCooldown => TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementLeaseAbortCooldownSeconds);
    private static TimeSpan PreemptPendingDelay => TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.PreemptPendingDelaySeconds);
    private static readonly TimeSpan SuppressionRetryDelay = TimeSpan.FromSeconds(0.65d);
    private static TimeSpan PreemptPendingMaxWindow => TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.PreemptPendingMaxSeconds);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(2.25d);
    private static readonly TimeSpan IsolatedCombatGrace = TimeSpan.FromSeconds(6.0d);
    private static readonly TimeSpan IsolatedCombatReleaseWindow = TimeSpan.FromSeconds(10.0d);
    private static readonly TimeSpan PhysicalRecoveryMemoryWindow = TimeSpan.FromSeconds(18.0d);
    private const int MaxAnchorPathValidationsPerResolve = 5;
    private const int MaxRawAnchorSamplesPerResolve = 18;
    private const int MaxEscapePathValidationsPerResolve = 4;
    private const float FailedAnchorExclusionMeters = 7.5f;
    public const string CrossLeasePhysicalRecoveryStatusTag = "VANGUARD_CROSS_LEASE_PHYSICAL_RECOVERY_STATUS";
    public const string BoundedPathComputationStatusTag = "VANGUARD_BOUNDED_PATH_COMPUTATION_STATUS";
    private static TimeSpan AuthorityRefreshInterval => TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementAuthorityRefreshSeconds);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, HardReturnLeaseState> ActiveByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PendingReturnState> PendingByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> CooldownUntilByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> CombatBackoffUntilByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IsolatedCombatWatchState> IsolatedCombatWatchByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PhysicalRecoveryState> PhysicalRecoveryByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogByKey = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset lastTickAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            ActiveByBotProfileId.Clear();
            PendingByBotProfileId.Clear();
            CooldownUntilByBotProfileId.Clear();
            CombatBackoffUntilByBotProfileId.Clear();
            IsolatedCombatWatchByBotProfileId.Clear();
            PhysicalRecoveryByBotProfileId.Clear();
            LastLogByKey.Clear();
        }

        lastTickAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        VanguardReturnMovementCommandStore.ResetForRaidLifecycle(reason);
        VanguardMovementOutcomeMemory.ResetForRaidLifecycle(reason);
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_ACTION_RALLY_RETURN_RESET reason={Safe(reason)}; activeMovement=true; applyOnce=true; preemptPending=true; combatBackoff=true; pathValidation=required; actionRally=true; closeReturn=true; authorityHeldUntilOutcome=true; cleanAuth=true; isolatedCombatRelease=true; hardRegroupSprint=true; tag={MoveBridgeStatusTag}; returnAuthorityTag={StatusTag}; goToSomePointTag={GoToSomePointBridgeStatusTag}; actionRallyTag={ActionRallyStatusTag}; continuationTag={ReturnContinuationStatusTag}; returnPathTag={ReturnPathValidationCompatibilityTag}; pendingTag={PendingStatusTag}; anchorScoreTag={AnchorScoreStatusTag}; cleanAuthTag={CleanAuthStatusTag}");
    }

    public static bool IsCrossLeasePhysicalRecoveryActive(string? botProfileId, DateTimeOffset now, out string reason)
    {
        var state = GetPhysicalRecovery(botProfileId, now);
        if (state == null)
        {
            reason = "none";
            return false;
        }

        reason = "cross_lease_physical_recovery:failures=" + state.FailureCount.ToString(CultureInfo.InvariantCulture)
            + ":until=" + state.UntilUtc.ToString("O", CultureInfo.InvariantCulture)
            + ":failedAnchor=" + FormatVector(state.FailedAnchor);
        return true;
    }

    public static bool TryRegisterPathSafeFallback(
        OperatorDecisionSnapshot snapshot,
        BotOwner botOwner,
        string leaseId,
        Vector3 anchor,
        float anchorRadiusMeters,
        DateTimeOffset now,
        DateTimeOffset expiresAtUtc,
        string pathSummary,
        float pathDistanceMeters,
        string commandResult,
        out string result)
    {
        result = "none";
        if (snapshot == null || botOwner == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId) || string.IsNullOrWhiteSpace(leaseId))
        {
            result = "missing_snapshot_botowner_or_lease";
            return false;
        }

        Vector3 botPosition = ResolveBotPosition(botOwner);
        lock (Sync)
        {
            if (PendingByBotProfileId.ContainsKey(snapshot.BotProfileId))
            {
                result = "canonical_hard_return_pending";
                return false;
            }

            if (ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var existing))
            {
                if (string.Equals(existing.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase))
                {
                    result = "already_registered_same_lease";
                    return true;
                }

                result = "different_hard_return_lease_active:" + Safe(existing.LeaseId);
                return false;
            }

            float noProgressSeconds = VanguardMovementAuthorityDoctrine.MovementLeaseNoProgressSeconds;
            ActiveByBotProfileId[snapshot.BotProfileId] = new HardReturnLeaseState
            {
                LeaseId = leaseId,
                OperatorId = snapshot.OperatorId,
                BotProfileId = snapshot.BotProfileId,
                ContractKey = "path_safe_hard_return_fallback",
                RequestKind = VanguardMovementContractPolicy.ActionRallyHardReturn,
                MoveOwnerAtStart = snapshot.MovementAuthority.CurrentAuthority,
                Anchor = anchor,
                AnchorRadiusMeters = anchorRadiusMeters,
                ActionRallyClearMeters = VanguardMovementAuthorityDoctrine.ActionRallyClearMeters,
                ActionRallyAcceptMeters = VanguardMovementAuthorityDoctrine.ActionRallyAcceptMeters,
                CompletionMeters = VanguardMovementAuthorityDoctrine.HardReturnCompletionMeters,
                NoProgressSeconds = noProgressSeconds,
                MaxReanchorsPerLease = 1,
                StartedAtUtc = now,
                MinUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementLeaseMinDurationSeconds),
                MaxUntilUtc = expiresAtUtc,
                HardMaxUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementLeaseHardMaxSeconds),
                NoProgressUntilUtc = now + TimeSpan.FromSeconds(noProgressSeconds),
                LastProgressAtUtc = now,
                NextAuthorityRefreshAtUtc = now + AuthorityRefreshInterval,
                NextReanchorAllowedAtUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.ActionRallyReanchorCooldownSeconds),
                OwnerPositionAtAnchor = snapshot.SquadCohesion.OwnerPosition ?? anchor,
                InitialAnchorDistance = HorizontalDistance(botPosition, anchor),
                LastAnchorDistance = HorizontalDistance(botPosition, anchor),
                InitialBubbleDistance = snapshot.SquadCohesion.OperatorDistanceToOwner,
                LastBubbleDistance = snapshot.SquadCohesion.OperatorDistanceToOwner,
                LastDestinationDistance = pathDistanceMeters > 0.01f ? pathDistanceMeters : snapshot.Movement.DistanceToDestination,
                CommandResult = commandResult,
                ExternalPreemptOutcome = "path_safe_fallback_preempted_by_scheduler",
                SchedulerWindowId = leaseId,
                PathValidationSummary = pathSummary,
                AnchorScoreSummary = "path_safe_fallback",
                PathDistanceMeters = pathDistanceMeters,
                LastProgressKind = "path_safe_fallback_registered",
                LastWorldPosition = botPosition,
                LastWorldSampleAtUtc = now,
                Sprint = true,
                PathSafeFallback = true
            };
        }

        if (!VanguardMainIntentScheduler.MarkHardReturnStarted(snapshot.BotProfileId, leaseId, now, "path_safe_fallback_registered", leaseId))
        {
            lock (Sync)
            {
                if (ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var registered)
                    && string.Equals(registered.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase))
                {
                    ActiveByBotProfileId.Remove(snapshot.BotProfileId);
                }
            }

            result = "scheduler_window_ownership_lost_before_registration_commit";
            return false;
        }

        result = "registered_and_monitored";
        return true;
    }

    public static void Tick()
    {
        if (!VanguardOperatorRuntimeAuditLoadGuard.IsOpen() || !VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - lastTickAtUtc < TickInterval)
        {
            return;
        }

        lastTickAtUtc = now;
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_MOVE_BRIDGE_BOOT enabled=true; activeMovement=true; scope=hard_outside_bubble_external_or_sain_boundary; movementF12Sync=true; bubbleRadius={VanguardMovementAuthorityDoctrine.TacticalBubbleMeters:0}; hardCorrection={VanguardMovementAuthorityDoctrine.HardCorrectionMeters:0}; actionRallyClear={VanguardMovementAuthorityDoctrine.ActionRallyClearMeters:0}; actionRallyAccept={VanguardMovementAuthorityDoctrine.ActionRallyAcceptMeters:0}; hardReturnCompletion={VanguardMovementAuthorityDoctrine.HardReturnCompletionMeters:0}; backend=BIGBRAIN_GOTOSOMEPOINT; pathValidation=NavMesh.CalculatePath_PathComplete_required; anchorScoring=true; pathDistancePenalty=true; outcomeMemory=true; noProgressTyped=true; preemptPending=true; preemptDelay={VanguardMovementAuthorityDoctrine.PreemptPendingDelaySeconds:0.00}; preemptMax={VanguardMovementAuthorityDoctrine.PreemptPendingMaxSeconds:0.00}; authorityRefresh={VanguardMovementAuthorityDoctrine.MovementAuthorityRefreshSeconds:0.00}; combatBackoff={VanguardMovementAuthorityDoctrine.HardReturnCombatBackoffSeconds:0.00}; maxWindow={VanguardMovementAuthorityDoctrine.MovementLeaseMaxDurationSeconds:0}; hardMax={VanguardMovementAuthorityDoctrine.MovementLeaseHardMaxSeconds:0}; reapplyPolicy=apply_once_plus_bounded_same_generation_retarget; actionRally=true; noAnchorOnlyCompletion=true; noGoToPointNoWaySuccess=true; noReflectionGoToPoint=true; legacyReflectionBridgeDisabled=true; guards=owner_reliable_true_threat_effective_stationary_medical_only_mobile_medical_sidecar_allowed; doctrineSnapshot=per_lease; movementSyncTag={VanguardBuildVersion.MovementDoctrineF12SyncStatusTag}; tag={MoveBridgeStatusTag}; returnAuthorityTag={StatusTag}; goToSomePointTag={GoToSomePointBridgeStatusTag}; actionRallyTag={ActionRallyStatusTag}; continuationTag={ReturnContinuationStatusTag}; returnPathTag={ReturnPathValidationCompatibilityTag}; pendingTag={PendingStatusTag}; anchorScoreTag={AnchorScoreStatusTag}; schedulerTag={MainSchedulerStatusTag}; Tag={HardReturnCompatibilityTag}; Tag={SainBoundaryCompatibilityTag}; isolatedCombatReleaseTag={IsolatedCombatReleaseStatusTag}; hardRegroupSprintTag={HardRegroupSprintStatusTag}");
            VanguardClientDiagnosticsLog.Info(PendingStatusTag,
                $"VANGUARD_PREEMPT_PENDING_BOOT enabled=true; commandSameTick=false; commandAfterPreemptTick=true; revalidateBeforeCommand=true; externalQuiesceRequired=true; pathCompleteRequired=true; actionRally=true; tag={PendingStatusTag}; returnAuthorityTag={StatusTag}; actionRallyTag={ActionRallyStatusTag}; continuationTag={ReturnContinuationStatusTag}; returnPathTag={ReturnPathValidationCompatibilityTag}");
        }

        var snapshots = VanguardOperatorDecisionSnapshotService.GetLatestSnapshots();
        TickActiveLeases(snapshots, now);
        TickPendingPreempts(snapshots, now);
        TryStartNewLeases(snapshots, now);
    }

    private static void TickActiveLeases(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        HardReturnLeaseState[] leases;
        lock (Sync)
        {
            leases = new List<HardReturnLeaseState>(ActiveByBotProfileId.Values).ToArray();
        }

        foreach (var lease in leases)
        {
            var snapshot = FindSnapshot(snapshots, lease.BotProfileId);
            if (snapshot == null)
            {
                FinishLease(lease, now, "Failed", "snapshot_missing", AbortCooldown, null);
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record) || record.BotOwner == null)
            {
                FinishLease(lease, now, "Failed", "botowner_missing", AbortCooldown, snapshot);
                continue;
            }

            string interrupt = CheckInterrupt(snapshot, now);
            if (!string.Equals(interrupt, "none", StringComparison.OrdinalIgnoreCase))
            {
                FinishLease(lease, now, "Interrupted", interrupt, AbortCooldown, snapshot, record.BotOwner);
                continue;
            }

            Vector3 position = ResolveBotPosition(record.BotOwner);
            float anchorDistance = HorizontalDistance(position, lease.Anchor);
            float bubbleDistance = snapshot.SquadCohesion.OperatorDistanceToOwner;

            // Runtime invariant: a hard return is an emergency recovery, not a broad bubble re-entry. Vanguard
            // showed that completing at 38-45 m handed the bot back to generic/external movement too
            // early and allowed repeated drift. The emergency window now remains authoritative until
            // the Operator reaches a useful 28 m transition band, after which travel/close cohesion
            // can continue without pretending the full return was already complete.
            if (bubbleDistance <= lease.CompletionMeters)
            {
                // The runtime terminal truth: the lease summary and outcome memory must use the exact fresh
                // snapshot that satisfied completion, not the last progress sample from a prior tick.
                lease.LastBubbleDistance = bubbleDistance;
                lease.LastAnchorDistance = anchorDistance;
                lease.LastDestinationDistance = snapshot.Movement.DistanceToDestination;
                lease.LastProgressKind = "terminal_completion_snapshot";
                string completionReason = anchorDistance <= lease.AnchorRadiusMeters
                    ? "inside_hard_return_completion_and_anchor"
                    : "inside_hard_return_completion_band";
                FinishLease(lease, now, "Completed", completionReason, StartCooldown, snapshot, record.BotOwner);
                continue;
            }

            if (now >= lease.NextAuthorityRefreshAtUtc)
            {
                if (!RefreshMovementAuthorityHold(lease, snapshot, record.BotOwner, now, out var refreshReason))
                {
                    TimeSpan cooldown = IsCombatBackoffReason(refreshReason) ? TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.HardReturnCombatBackoffSeconds) : AbortCooldown;
                    if (IsCombatBackoffReason(refreshReason))
                    {
                        SetCombatBackoff(snapshot.BotProfileId, now, "authority_refresh:" + refreshReason);
                    }
                    FinishLease(lease, now, "Interrupted", "external_authority_reacquired:" + refreshReason, cooldown, snapshot, record.BotOwner);
                    continue;
                }
            }

            var progress = VanguardMovementProgressEvaluator.Evaluate(
                lease.LastAnchorDistance,
                anchorDistance,
                lease.LastBubbleDistance,
                bubbleDistance,
                lease.LastDestinationDistance,
                snapshot.Movement.DistanceToDestination,
                snapshot.RealSpeed,
                lease.PathDistanceMeters,
                now - lease.LastProgressAtUtc);
            TimeSpan physicalSampleAge = now - lease.LastWorldSampleAtUtc;
            var physical = VanguardMovementProgressEvaluator.EvaluatePhysical(
                lease.LastWorldPosition,
                position,
                lease.LastAnchorDistance,
                anchorDistance,
                snapshot.RealSpeed,
                movementExpected: true,
                physicalSampleAge);
            if (physicalSampleAge >= TimeSpan.FromSeconds(0.45d))
            {
                lease.LastWorldPosition = position;
                lease.LastWorldSampleAtUtc = now;
            }
            if (physical.HasProgress)
            {
                lease.LastProgressAtUtc = now;
                lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(lease.NoProgressSeconds);
                lease.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
                if (physical.WorldDeltaMeters >= 1.0f)
                {
                    ClearPhysicalRecovery(lease.BotProfileId, "meaningful_operator_world_progress");
                }
                ExtendLeaseWindowForProgress(lease, now);

                lease.LastAnchorDistance = anchorDistance;
                lease.LastBubbleDistance = bubbleDistance;
                lease.LastDestinationDistance = snapshot.Movement.DistanceToDestination;
                lease.LastProgressKind = physical.ProgressKind;
                LogThrottled("progress|" + lease.BotProfileId, now,
                    $"VANGUARD_RETURN_PROGRESS {lease.Summary}; anchorDist={anchorDistance:0.00}; bubbleDist={bubbleDistance:0.00}; anchorGain={progress.AnchorGainMeters:0.00}; bubbleGain={progress.BubbleGainMeters:0.00}; destinationGain={progress.DestinationGainMeters:0.00}; progressKind={Safe(lease.LastProgressKind)}; physical={Safe(physical.Summary)}; speed={snapshot.RealSpeed:0.00}; noProgressReason={Safe(progress.NoProgressReason)}; maxLeft={(lease.MaxUntilUtc - now).TotalSeconds:0.00}; hardMaxLeft={(lease.HardMaxUntilUtc - now).TotalSeconds:0.00}; tag={AnchorScoreStatusTag}; schedulerTag={MainSchedulerStatusTag}; moveBridgeTag={MoveBridgeStatusTag}; returnAuthorityTag={StatusTag}; goToSomePointTag={GoToSomePointBridgeStatusTag}; actionRallyTag={ActionRallyStatusTag}; continuationTag={ReturnContinuationStatusTag}; returnPathTag={ReturnPathValidationCompatibilityTag}; sainBoundaryCompatibilityTag={SainBoundaryCompatibilityTag}");
                VanguardMainIntentScheduler.ReportPrimaryProgress(lease.BotProfileId, now, lease.LastProgressKind, lease.Summary, lease.SchedulerWindowId);
            }
            else if (physical.LocomotionBlocked)
            {
                if (lease.PhysicalBlockedSinceUtc == DateTimeOffset.MinValue)
                {
                    lease.PhysicalBlockedSinceUtc = now;
                }

                double blockedSeconds = Math.Max(0d, (now - lease.PhysicalBlockedSinceUtc).TotalSeconds);
                if (blockedSeconds >= 1.0d && lease.PhysicalRestartCount < 1)
                {
                    if (TryRestartHardReturnCommand(lease, now, physical.Summary, out var restartResult))
                    {
                        lease.PhysicalRestartCount++;
                        lease.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
                        lease.LastWorldPosition = position;
                        lease.LastWorldSampleAtUtc = now;
                        lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(lease.NoProgressSeconds);
                        lease.LastProgressKind = "hard_return_physical_path_restart_pending_evidence";
                        LogThrottled("physicalRestart|" + lease.BotProfileId, now,
                            $"VANGUARD_HARD_RETURN_PHYSICAL_RESTART {lease.Summary}; physical={Safe(physical.Summary)}; result={Safe(restartResult)}; sameLease=true; boundedRestartCount=1; fallback={Bool(lease.PathSafeFallback)}; tag={VanguardPrimaryExecutionContract.HardReturnPhysicalProgressStatusTag}");
                        continue;
                    }

                    // A rejected restart means this executor no longer owns a usable command. Do not
                    // leave the scheduler window alive until the broad no-progress timeout and do not
                    // retry against a foreign generation on every tick.
                    FinishLease(lease, now, "Failed", "PhysicalRestartRejected:" + restartResult, FailureCooldown, snapshot, record.BotOwner);
                    continue;
                }

                if (blockedSeconds >= 3.0d && lease.PhysicalRestartCount >= 1)
                {
                    FinishLease(lease, now, "Timeout", "PhysicalBlockedAfterRestart:" + physical.Summary, FailureCooldown, snapshot, record.BotOwner);
                    continue;
                }
            }
            else if (physicalSampleAge >= TimeSpan.FromSeconds(0.45d))
            {
                // A sub-sample tick carries no evidence either way. Preserve a previously observed
                // blocked episode until the next mature world sample; otherwise a 300 ms executor tick
                // could reset a 450 ms physical detector forever.
                lease.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
            }

            float ownerAnchorDistance = snapshot.SquadCohesion.OwnerPosition.HasValue
                ? HorizontalDistance(snapshot.SquadCohesion.OwnerPosition.Value, lease.Anchor)
                : 0f;
            float ownerMovedSinceAnchor = snapshot.SquadCohesion.OwnerPosition.HasValue
                ? HorizontalDistance(snapshot.SquadCohesion.OwnerPosition.Value, lease.OwnerPositionAtAnchor)
                : 0f;
            bool ownerAnchorStale = bubbleDistance > lease.CompletionMeters
                && now >= lease.MinUntilUtc
                && ownerMovedSinceAnchor >= VanguardMovementAuthorityDoctrine.HardReturnRetargetOwnerMoveMeters
                && ownerAnchorDistance >= VanguardMovementAuthorityDoctrine.HardReturnRetargetAnchorOwnerDistanceMeters
                && (bubbleDistance >= lease.LastBubbleDistance - 0.50f
                    || now - lease.LastProgressAtUtc >= TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.HardReturnRetargetNoProgressSeconds)
                    || anchorDistance <= lease.AnchorRadiusMeters + 4.0f);
            if (ownerAnchorStale
                && TryReanchorActionRally(lease, snapshot, record.BotOwner, position, now, "owner_anchor_stale", out var staleRetargetReason))
            {
                LogThrottled("OwnerRetarget|" + lease.BotProfileId, now,
                    $"VANGUARD_HARD_RETURN_RETARGET {lease.Summary}; cause=owner_anchor_stale; ownerMoved={ownerMovedSinceAnchor:0.00}; ownerAnchorDistance={ownerAnchorDistance:0.00}; bubbleDist={bubbleDistance:0.00}; anchorDist={anchorDistance:0.00}; result={Safe(staleRetargetReason)}; sameLease=true; sameGeneration=true; RetargetTag={VanguardPrimaryExecutionContract.MovementRetargetStatusTag}; CompletionTag={VanguardPrimaryExecutionContract.HardReturnCompletionStatusTag}; tag={StatusTag}");
                continue;
            }

            if (anchorDistance <= lease.AnchorRadiusMeters && bubbleDistance > lease.CompletionMeters)
            {
                VanguardMovementOutcomeMemory.RecordLeaseOutcome(lease.BotProfileId, lease.Anchor, "Reanchor", "anchor_reached_but_bubble_far", lease.PathDistanceMeters, now);
                if (TryReanchorActionRally(lease, snapshot, record.BotOwner, position, now, "anchor_reached_but_bubble_far", out var reanchorReason))
                {
                    LogThrottled("reanchor|" + lease.BotProfileId, now,
                        $"VANGUARD_BOUNDARY_RETURN_REANCHOR {lease.Summary}; reason=anchor_reached_but_bubble_far; reanchor={Safe(reanchorReason)}; anchorDist={anchorDistance:0.00}; bubbleDist={bubbleDistance:0.00}; tag={MoveBridgeStatusTag}; returnAuthorityTag={StatusTag}; goToSomePointTag={GoToSomePointBridgeStatusTag}; actionRallyTag={ActionRallyStatusTag}; continuationTag={ReturnContinuationStatusTag}; returnPathTag={ReturnPathValidationCompatibilityTag}");
                    continue;
                }

                LogThrottled("anchorFar|" + lease.BotProfileId, now,
                    $"VANGUARD_BOUNDARY_RETURN_ANCHOR_REACHED_BUT_FAR {lease.Summary}; reason=anchor_reached_but_bubble_far; reanchor={Safe(reanchorReason)}; anchorDist={anchorDistance:0.00}; bubbleDist={bubbleDistance:0.00}; completed=false; tag={StatusTag}; actionRallyTag={ActionRallyStatusTag}");
            }

            if (now >= lease.NoProgressUntilUtc)
            {
                string noProgressReason = progress.NoProgressReason;
                VanguardMovementOutcomeMemory.RecordLeaseOutcome(lease.BotProfileId, lease.Anchor, "Timeout", "NoProgressTimeout:" + noProgressReason, lease.PathDistanceMeters, now);
                if (TryReanchorActionRally(lease, snapshot, record.BotOwner, position, now, "no_progress_timeout", out var reanchorReason))
                {
                    LogThrottled("reanchorNoProgress|" + lease.BotProfileId, now,
                        $"VANGUARD_RETURN_REANCHOR {lease.Summary}; reason=no_progress_timeout; noProgressReason={Safe(noProgressReason)}; reanchor={Safe(reanchorReason)}; anchorDist={anchorDistance:0.00}; bubbleDist={bubbleDistance:0.00}; tag={AnchorScoreStatusTag}; moveBridgeTag={MoveBridgeStatusTag}; returnAuthorityTag={StatusTag}; goToSomePointTag={GoToSomePointBridgeStatusTag}; actionRallyTag={ActionRallyStatusTag}; continuationTag={ReturnContinuationStatusTag}; returnPathTag={ReturnPathValidationCompatibilityTag}");
                    continue;
                }

                if (reanchorReason.IndexOf("frame_path_budget_pending", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(0.50d);
                    lease.NextReanchorAllowedAtUtc = now + TimeSpan.FromSeconds(0.15d);
                    continue;
                }

                TimeSpan timeoutCooldown = reanchorReason.StartsWith("reanchor_limit_reached", StringComparison.OrdinalIgnoreCase)
                    ? TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.ActionRallyReanchorLimitCooldownSeconds)
                    : FailureCooldown;
                FinishLease(lease, now, "Timeout", "NoProgressTimeout:" + noProgressReason + ":reanchor=" + reanchorReason, timeoutCooldown, snapshot, record.BotOwner);
                continue;
            }

            if (now >= lease.MaxUntilUtc)
            {
                FinishLease(lease, now, "Timeout", now >= lease.HardMaxUntilUtc ? "HardMaxWindowExpired" : "MaxWindowExpired", FailureCooldown, snapshot, record.BotOwner);
            }
        }
    }

    private static void TickPendingPreempts(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        PendingReturnState[] pending;
        lock (Sync)
        {
            pending = new List<PendingReturnState>(PendingByBotProfileId.Values).ToArray();
        }

        foreach (var state in pending)
        {
            var snapshot = FindSnapshot(snapshots, state.BotProfileId);
            if (snapshot == null)
            {
                FinishPending(state, now, "snapshot_missing", AbortCooldown, null, null);
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record) || record.BotOwner == null)
            {
                FinishPending(state, now, "botowner_missing", AbortCooldown, snapshot, null);
                continue;
            }

            string interrupt = CheckInterrupt(snapshot, now);
            if (!string.Equals(interrupt, "none", StringComparison.OrdinalIgnoreCase))
            {
                TimeSpan cooldown = IsCombatBackoffReason(interrupt) ? TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.HardReturnCombatBackoffSeconds) : AbortCooldown;
                if (IsCombatBackoffReason(interrupt))
                {
                    SetCombatBackoff(snapshot.BotProfileId, now, "pending_interrupt:" + interrupt);
                }
                FinishPending(state, now, interrupt, cooldown, snapshot, record.BotOwner);
                continue;
            }

            if (now >= state.ExpiresAtUtc)
            {
                FinishPending(state, now, "preempt_pending_timeout", FailureCooldown, snapshot, record.BotOwner);
                continue;
            }

            if (!IsStillEligibleAfterPreempt(snapshot, state, out var recheckReason))
            {
                FinishPending(state, now, "recheck_failed:" + recheckReason, AbortCooldown, snapshot, record.BotOwner);
                continue;
            }

            if (now < state.CommandAfterUtc)
            {
                LogThrottled("pendingWait|" + state.BotProfileId, now,
                    $"VANGUARD_PREEMPT_PENDING_WAIT {state.Summary}; wait={(state.CommandAfterUtc - now).TotalSeconds:0.00}; bubbleDist={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; tag={PendingStatusTag}; returnAuthorityTag={StatusTag}; returnPathTag={ReturnPathValidationCompatibilityTag}");
                continue;
            }

            if (!TryRefreshPreemptForCommand(state, snapshot, record.BotOwner, now, out var commandPreemptOutcome, out var commandPreemptSummary))
            {
                if (IsRetriableExternalQuiesceOutcome(commandPreemptOutcome) && now < state.ExpiresAtUtc)
                {
                    state.ExternalPreemptOutcome = commandPreemptOutcome;
                    state.CommandAfterUtc = now + SuppressionRetryDelay;
                    VanguardMainIntentScheduler.ReportPrimaryProgress(snapshot.BotProfileId, now, "pending_external_quiesce:" + commandPreemptOutcome, commandPreemptSummary, state.SchedulerWindowId);
                    LogThrottled("externalQuiescePending|" + state.BotProfileId + "|" + commandPreemptOutcome, now,
                        $"VANGUARD_RETURN_AUTHORITY_QUIESCE_PENDING {state.Summary}; outcome={Safe(commandPreemptOutcome)}; retryIn={SuppressionRetryDelay.TotalSeconds:0.00}; expiresIn={(state.ExpiresAtUtc - now).TotalSeconds:0.00}; preempt={Safe(commandPreemptSummary)}; bubbleDist={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; command=false; noStartFailRestart=true; tag={VanguardMovementAuthorityDoctrine.ExternalPreemptPendingStatusTag}; moveBridgeTag={MoveBridgeStatusTag}; returnAuthorityTag={StatusTag}; goToSomePointTag={GoToSomePointBridgeStatusTag}; actionRallyTag={ActionRallyStatusTag}; pendingTag={PendingStatusTag}");
                    continue;
                }

                FinishPending(state, now, "external_quiesce_not_granted:" + commandPreemptOutcome, FailureCooldown, snapshot, record.BotOwner);
                LogThrottled("externalQuiesceFail|" + state.BotProfileId + "|" + commandPreemptOutcome, now,
                    $"VANGUARD_RETURN_AUTHORITY_QUIESCE_FAILED {state.Summary}; outcome={Safe(commandPreemptOutcome)}; preempt={Safe(commandPreemptSummary)}; bubbleDist={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; command=false; tag={MoveBridgeStatusTag}; returnAuthorityTag={StatusTag}; goToSomePointTag={GoToSomePointBridgeStatusTag}; actionRallyTag={ActionRallyStatusTag}; pendingTag={PendingStatusTag}");
                continue;
            }

            Vector3 botPosition = ResolveBotPosition(record.BotOwner);
            if (!TryResolveReturnAnchor(snapshot, botPosition, now, out var anchor, out var anchorReason, out var pathSummary, out var pathDistance, out var anchorScoreSummary))
            {
                if (anchorReason.StartsWith("frame_path_budget_pending", StringComparison.OrdinalIgnoreCase))
                {
                    state.CommandAfterUtc = now + TimeSpan.FromSeconds(0.15d);
                    VanguardMainIntentScheduler.ReportPrimaryProgress(snapshot.BotProfileId, now, "hard_return_path_budget_pending", pathSummary, state.SchedulerWindowId);
                    continue;
                }

                FinishPending(state, now, "anchor_path_failed:" + anchorReason, FailureCooldown, snapshot, record.BotOwner);
                LogThrottled("pathFailCommand|" + state.BotProfileId + "|" + anchorReason, now,
                    $"VANGUARD_RETURN_PATH_FAILED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; phase=before_command; reason={Safe(anchorReason)}; path={Safe(pathSummary)}; bubbleDist={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; tag={MoveBridgeStatusTag}; returnAuthorityTag={StatusTag}; goToSomePointTag={GoToSomePointBridgeStatusTag}; actionRallyTag={ActionRallyStatusTag}; continuationTag={ReturnContinuationStatusTag}; returnPathTag={ReturnPathValidationCompatibilityTag}; pendingTag={PendingStatusTag}");
                continue;
            }

            bool sprint = ShouldSprintHardRegroup(snapshot);
            string leaseId = "return_bridge_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId);
            float windowSeconds = CalculateLeaseWindowSeconds(snapshot.SquadCohesion.OperatorDistanceToOwner, pathDistance);
            DateTimeOffset maxUntil = now + TimeSpan.FromSeconds(windowSeconds);
            float anchorRadius = Math.Max(4f, Math.Min(VanguardMovementAuthorityDoctrine.HardReturnAnchorRadiusMeters, snapshot.MovementAuthority.BrokerPlan.LeasePlan.AnchorRadiusMeters));
            bool commanded = VanguardReturnMovementCommandStore.Issue(
                leaseId,
                snapshot.OperatorId,
                snapshot.BotProfileId,
                anchor,
                anchorRadius,
                sprint,
                now,
                maxUntil,
                state.RequestKind,
                pathSummary,
                pathDistance,
                out var commandResult);
            if (!commanded)
            {
                FinishPending(state, now, "move_bridge_rejected:" + commandResult, FailureCooldown, snapshot, record.BotOwner);
                LogThrottled("commandRejected|" + state.BotProfileId + "|" + commandResult, now,
                    $"VANGUARD_MOVE_BRIDGE_COMMAND_REJECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; anchor={FormatVector(anchor)}; sprint={Bool(sprint)}; command={Safe(commandResult)}; path={Safe(pathSummary)}; bubbleDist={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; tag={MoveBridgeStatusTag}; goToSomePointTag={GoToSomePointBridgeStatusTag}; returnAuthorityTag={StatusTag}; pendingTag={PendingStatusTag}");
                continue;
            }

            lock (Sync)
            {
                PendingByBotProfileId.Remove(state.BotProfileId);
            }

            float leaseActionRallyClearMeters = VanguardMovementAuthorityDoctrine.ActionRallyClearMeters;
            float leaseActionRallyAcceptMeters = VanguardMovementAuthorityDoctrine.ActionRallyAcceptMeters;
            float leaseNoProgressSeconds = VanguardMovementAuthorityDoctrine.MovementLeaseNoProgressSeconds;
            int leaseMaxReanchors = VanguardMovementAuthorityDoctrine.ActionRallyMaxReanchorsPerLease;

            var lease = new HardReturnLeaseState
            {
                LeaseId = leaseId,
                OperatorId = snapshot.OperatorId,
                BotProfileId = snapshot.BotProfileId,
                ContractKey = snapshot.MovementAuthority.BrokerPlan.Contract.ContractKey,
                RequestKind = snapshot.MovementAuthority.BrokerPlan.RequestKind,
                MoveOwnerAtStart = snapshot.MovementAuthority.CurrentAuthority,
                Anchor = anchor,
                AnchorRadiusMeters = anchorRadius,
                ActionRallyClearMeters = leaseActionRallyClearMeters,
                ActionRallyAcceptMeters = leaseActionRallyAcceptMeters,
                CompletionMeters = VanguardMovementAuthorityDoctrine.HardReturnCompletionMeters,
                NoProgressSeconds = leaseNoProgressSeconds,
                MaxReanchorsPerLease = leaseMaxReanchors,
                StartedAtUtc = now,
                MinUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementLeaseMinDurationSeconds),
                MaxUntilUtc = maxUntil,
                HardMaxUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementLeaseHardMaxSeconds),
                NoProgressUntilUtc = now + TimeSpan.FromSeconds(leaseNoProgressSeconds),
                NextAuthorityRefreshAtUtc = now + AuthorityRefreshInterval,
                NextReanchorAllowedAtUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.ActionRallyReanchorCooldownSeconds),
                LastProgressAtUtc = now,
                InitialAnchorDistance = HorizontalDistance(botPosition, anchor),
                LastAnchorDistance = HorizontalDistance(botPosition, anchor),
                InitialBubbleDistance = snapshot.SquadCohesion.OperatorDistanceToOwner,
                LastBubbleDistance = snapshot.SquadCohesion.OperatorDistanceToOwner,
                CommandResult = commandResult,
                ExternalPreemptOutcome = state.ExternalPreemptOutcome,
                SchedulerWindowId = state.SchedulerWindowId,
                PathValidationSummary = pathSummary,
                AnchorScoreSummary = anchorScoreSummary,
                PathDistanceMeters = pathDistance,
                PendingElapsedSeconds = (float)(now - state.PreemptedAtUtc).TotalSeconds,
                LastDestinationDistance = snapshot.Movement.DistanceToDestination,
                LastProgressKind = "command_applied_once_after_preempt_pending",
                OwnerPositionAtAnchor = snapshot.SquadCohesion.OwnerPosition ?? anchor,
                LastWorldPosition = botPosition,
                LastWorldSampleAtUtc = now,
                Sprint = sprint,
                PathSafeFallback = false
            };

            lock (Sync)
            {
                ActiveByBotProfileId[snapshot.BotProfileId] = lease;
            }

            VanguardMainIntentScheduler.MarkHardReturnStarted(snapshot.BotProfileId, lease.LeaseId, now, lease.Summary, lease.SchedulerWindowId);

            LogThrottled("started|" + snapshot.BotProfileId, now,
                $"VANGUARD_BOUNDARY_RETURN_LEASE_STARTED {lease.Summary}; anchor={FormatVector(anchor)}; anchorReason={Safe(anchorReason)}; path={Safe(pathSummary)}; pathDist={pathDistance:0.00}; anchorScore={Safe(anchorScoreSummary)}; sprint={Bool(sprint)}; command={Safe(commandResult)}; backend=BigBrain_GoToSomePoint; commandPreempt={Safe(commandPreemptOutcome)}; preemptPendingElapsed={lease.PendingElapsedSeconds:0.00}; applyOnce=true; actionRally=true; noPeriodicReapply=true; authorityHeldUntilOutcome=true; tag={MoveBridgeStatusTag}; returnAuthorityTag={StatusTag}; goToSomePointTag={GoToSomePointBridgeStatusTag}; actionRallyTag={ActionRallyStatusTag}; continuationTag={ReturnContinuationStatusTag}; returnPathTag={ReturnPathValidationCompatibilityTag}; pendingTag={PendingStatusTag}; anchorScoreTag={AnchorScoreStatusTag}; schedulerTag={MainSchedulerStatusTag}; Tag={HardReturnCompatibilityTag}; Tag={SainBoundaryCompatibilityTag}; isolatedCombatReleaseTag={IsolatedCombatReleaseStatusTag}; hardRegroupSprintTag={HardRegroupSprintStatusTag}");
            // Runtime invariant: one expensive command activation per executor tick. Other pending Operators
            // remain queued and retain their scheduler windows, preventing same-frame NavMesh bursts.
            return;
        }
    }

    private static void TryStartNewLeases(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        foreach (var snapshot in snapshots)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            {
                continue;
            }

            // Vanguard: do not short-circuit here.  TryOpenHardReturnPending is the only place that can
            // decide whether an active close/travel/claim window is weak and should be preempted.
            // The previous pre-check made Vanguard preemption unreachable and converted hard returns into
            // repeated suppressed attempts while external fallback commands kept being recalculated.
            if (!IsEligibleToStart(snapshot, now, out string reason))
            {
                if (snapshot.MovementAuthority.HardOutsideBubble && !string.Equals(reason, "not_boundary_return_contract", StringComparison.OrdinalIgnoreCase))
                {
                    LogThrottled("startBlocked|" + snapshot.BotProfileId + "|" + reason, now,
                        $"VANGUARD_ACTION_RALLY_START_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; bubbleDist={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; contract={Safe(snapshot.MovementAuthority.BrokerPlan.Contract.ContractKey)}; request={Safe(snapshot.MovementAuthority.BrokerPlan.RequestKind)}; moveOwner={Safe(snapshot.MovementAuthority.CurrentAuthority)}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
                }
                continue;
            }

            if (!VanguardMainIntentScheduler.TryOpenHardReturnPending(snapshot, now, out var schedulerWindowId, out var schedulerReason))
            {
                LogThrottled("schedulerDenied|" + snapshot.BotProfileId + "|" + schedulerReason, now,
                    $"VANGUARD_HARD_RETURN_START_DENIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(schedulerReason)}; bubbleDist={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; contract={Safe(snapshot.MovementAuthority.BrokerPlan.Contract.ContractKey)}; request={Safe(snapshot.MovementAuthority.BrokerPlan.RequestKind)}; tag={MainSchedulerStatusTag}; returnAuthorityTag={StatusTag}");
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record) || record.BotOwner == null)
            {
                SetCooldown(snapshot.BotProfileId, now + AbortCooldown);
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "botowner_missing_before_preempt", "none", schedulerWindowId);
                LogThrottled("startNoBot|" + snapshot.BotProfileId, now,
                    $"VANGUARD_ACTION_RALLY_START_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=botowner_missing; schedulerWindow={Safe(schedulerWindowId)}; tag={StatusTag}; schedulerTag={MainSchedulerStatusTag}");
                continue;
            }

            Vector3 botPosition = ResolveBotPosition(record.BotOwner);
            if (!TryResolveReturnAnchor(snapshot, botPosition, now, out var anchor, out var anchorReason, out var pathSummary, out var pathDistance, out var anchorScoreSummary))
            {
                if (anchorReason.StartsWith("frame_path_budget_pending", StringComparison.OrdinalIgnoreCase))
                {
                    VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Deferred", anchorReason, pathSummary, schedulerWindowId);
                    // One resolver already consumed this frame's path budget. Stop admitting new
                    // hard-return paths until the next Unity frame instead of churning windows.
                    return;
                }

                SetCooldown(snapshot.BotProfileId, now + FailureCooldown);
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "anchor_path_failed:" + anchorReason, pathSummary, schedulerWindowId);
                LogThrottled("anchorFail|" + snapshot.BotProfileId + "|" + anchorReason, now,
                    $"VANGUARD_RETURN_PATH_FAILED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; phase=start; reason={Safe(anchorReason)}; path={Safe(pathSummary)}; bubbleDist={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; schedulerWindow={Safe(schedulerWindowId)}; tag={MoveBridgeStatusTag}; schedulerTag={MainSchedulerStatusTag}; returnAuthorityTag={StatusTag}; goToSomePointTag={GoToSomePointBridgeStatusTag}; actionRallyTag={ActionRallyStatusTag}; continuationTag={ReturnContinuationStatusTag}; returnPathTag={ReturnPathValidationCompatibilityTag}");
                continue;
            }

            TimeSpan authorityTtl = TimeSpan.FromSeconds(Math.Min(VanguardMovementAuthorityDoctrine.MovementLeaseHardMaxSeconds + 5f, CalculateLeaseWindowSeconds(snapshot.SquadCohesion.OperatorDistanceToOwner, pathDistance) + 8f));
            string scheduledPreemptReason = IsIsolatedCombatReleaseActive(snapshot.BotProfileId, now)
                ? "scheduler_preempt_pending:isolated_combat_release"
                : "scheduler_preempt_pending";
            var external = IsSainBoundaryReturnRequest(snapshot)
                ? VanguardExternalAuthorityAdapter.RequestScheduledSainBoundaryReturnPreempt(record.BotOwner, snapshot, "sain_boundary_return_" + scheduledPreemptReason, authorityTtl, now)
                : VanguardExternalAuthorityAdapter.RequestScheduledMovementHardReturnPreempt(record.BotOwner, snapshot, "hard_return_" + scheduledPreemptReason, authorityTtl, now);
            if (!IsPreemptOutcomeUsableForPending(external.Outcome))
            {
                if (external.Outcome == VanguardExternalPreemptOutcome.RejectedCombatOwner)
                {
                    SetCombatBackoff(snapshot.BotProfileId, now, "external_preempt_rejected_combat_owner:" + external.Reason);
                }
                SetCooldown(snapshot.BotProfileId, now + (external.Outcome == VanguardExternalPreemptOutcome.RejectedCombatOwner ? TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.HardReturnCombatBackoffSeconds) : AbortCooldown));
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "external_preempt_" + external.Outcome, external.Summary, schedulerWindowId);
                LogThrottled("externalRejected|" + snapshot.BotProfileId + "|" + external.Outcome, now,
                    $"VANGUARD_PREEMPT_PENDING_ABORTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=external_preempt_{external.Outcome}; schedulerWindow={Safe(schedulerWindowId)}; {external.Summary}; tag={PendingStatusTag}; schedulerTag={MainSchedulerStatusTag}; returnAuthorityTag={StatusTag}; returnPathTag={ReturnPathValidationCompatibilityTag}");
                continue;
            }

            var pending = new PendingReturnState
            {
                PendingId = "preempt_pending_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId),
                OperatorId = snapshot.OperatorId,
                BotProfileId = snapshot.BotProfileId,
                ContractKey = snapshot.MovementAuthority.BrokerPlan.Contract.ContractKey,
                RequestKind = snapshot.MovementAuthority.BrokerPlan.RequestKind,
                MoveOwnerAtStart = snapshot.MovementAuthority.CurrentAuthority,
                Anchor = anchor,
                AnchorReason = anchorReason,
                AnchorScoreSummary = anchorScoreSummary,
                PathValidationSummary = pathSummary,
                PathDistanceMeters = pathDistance,
                ExternalPreemptOutcome = external.Outcome.ToString(),
                SchedulerWindowId = schedulerWindowId,
                PreemptedAtUtc = now,
                CommandAfterUtc = now + PreemptPendingDelay,
                ExpiresAtUtc = now + PreemptPendingMaxWindow
            };

            lock (Sync)
            {
                PendingByBotProfileId[snapshot.BotProfileId] = pending;
            }

            LogThrottled("pending|" + snapshot.BotProfileId, now,
                $"VANGUARD_PREEMPT_PENDING {pending.Summary}; external={external.Outcome}; externalCanDrive={Bool(external.CanDriveMovement)}; waitBeforeCommand={PreemptPendingDelay.TotalSeconds:0.00}; path={Safe(pathSummary)}; pathDist={pathDistance:0.00}; anchorScore={Safe(anchorScoreSummary)}; authorityTtl={authorityTtl.TotalSeconds:0.00}; noSameTickCommand=true; actionRally=true; tag={PendingStatusTag}; schedulerWindow={Safe(schedulerWindowId)}; schedulerTag={MainSchedulerStatusTag}; returnAuthorityTag={StatusTag}; actionRallyTag={ActionRallyStatusTag}; returnPathTag={ReturnPathValidationCompatibilityTag}; Tag={HardReturnCompatibilityTag}; Tag={SainBoundaryCompatibilityTag}");
            // Runtime invariant: admit at most one new hard-return path/preempt per tick.
            return;
        }
    }

    private static bool IsEligibleToStart(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "not_alive";
            return false;
        }

        if (VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out var combatProtectionReason))
        {
            reason = "sain_combat_primary_protected:" + combatProtectionReason;
            return false;
        }

        var authority = snapshot.MovementAuthority;
        var broker = authority.BrokerPlan;
        var contract = broker.Contract;
        var lease = broker.LeasePlan;
        if (!authority.HardOutsideBubble || snapshot.SquadCohesion.OperatorDistanceToOwner < VanguardMovementAuthorityDoctrine.HardCorrectionMeters)
        {
            reason = "not_hard_outside_bubble";
            return false;
        }

        if (!snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement || !snapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            reason = "owner_unreliable";
            return false;
        }

        if (VanguardSquadTravelCohesionExecutor.ShouldOwnTravelRecovery(snapshot, now, out var travelReason))
        {
            reason = "monotonic_travel_corridor_owns_recovery:" + travelReason;
            return false;
        }

        if (IsCombatBackoffBlocked(snapshot.BotProfileId, now, out var combatUntil))
        {
            reason = "combat_backoff_until_" + combatUntil.ToString("O", CultureInfo.InvariantCulture);
            return false;
        }

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot))
        {
            if (!TryAllowIsolatedCombatRelease(snapshot, now, "start_gate", out var releaseReason))
            {
                SetCombatBackoff(snapshot.BotProfileId, now, "true_direct_threat_start_gate");
                reason = "combat_backoff_true_direct_threat";
                return false;
            }

            reason = "isolated_combat_release_allowed:" + releaseReason;
        }
        else
        {
            ClearIsolatedCombatWatch(snapshot.BotProfileId);
        }

        if (VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(snapshot))
        {
            reason = "stationary_medical_authority";
            return false;
        }

        if (IsCooldownBlocked(snapshot.BotProfileId, now, out var until))
        {
            reason = "cooldown_until_" + until.ToString("O", CultureInfo.InvariantCulture);
            return false;
        }

        lock (Sync)
        {
            if (ActiveByBotProfileId.ContainsKey(snapshot.BotProfileId))
            {
                reason = "lease_already_active";
                return false;
            }

            if (PendingByBotProfileId.ContainsKey(snapshot.BotProfileId))
            {
                reason = "preempt_already_pending";
                return false;
            }
        }

        if (!lease.Eligible || !lease.ApplyEnabled)
        {
            reason = "lease_not_apply_enabled";
            return false;
        }

        bool supportedRequest = contract.RequestKind == VanguardMovementContractPolicy.SuppressExternalAndReturn
            || contract.RequestKind == VanguardMovementContractPolicy.ReturnToBubbleHard
            || contract.RequestKind == VanguardMovementContractPolicy.BreakSainSearchReturnBubble;
        if (!supportedRequest)
        {
            reason = "not_boundary_return_contract";
            return false;
        }

        if (contract.RequestKind == VanguardMovementContractPolicy.BreakSainSearchReturnBubble
            && !IsIsolatedCombatReleaseActive(snapshot.BotProfileId, now)
            && !VanguardMovementAuthorityDoctrine.IsSainBoundaryReturnEligible(snapshot, out var boundaryReason))
        {
            reason = "sain_boundary_not_eligible:" + boundaryReason;
            return false;
        }

        if (!IsSupportedHardReturnBackend(lease.Backend))
        {
            reason = "unsupported_backend:" + lease.Backend;
            return false;
        }

        reason = "eligible";
        return true;
    }

    private static bool IsStillEligibleAfterPreempt(OperatorDecisionSnapshot snapshot, PendingReturnState state, out string reason)
    {
        if (!snapshot.Alive)
        {
            reason = "not_alive";
            return false;
        }

        if (VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, DateTimeOffset.UtcNow, out var combatProtectionReason))
        {
            reason = "sain_combat_primary_protected:" + combatProtectionReason;
            return false;
        }

        // Runtime invariant: the hard-outside threshold is a start condition only.  After the preempt was accepted,
        // the pending command is allowed to continue while the Operator is still outside the action-rally
        // accept ring.  This prevents a 103m -> 84m improvement from aborting before the bridge command.
        if (snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.HardReturnCompletionMeters)
        {
            reason = "already_inside_hard_return_completion";
            return false;
        }

        if (!snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement || !snapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            reason = "owner_unreliable";
            return false;
        }

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot))
        {
            if (!TryAllowIsolatedCombatRelease(snapshot, DateTimeOffset.UtcNow, "continuation", out var releaseReason))
            {
                reason = "true_direct_threat";
                return false;
            }

            reason = "isolated_combat_release_allowed:" + releaseReason;
        }

        reason = "eligible_continuation";
        return true;
    }

    private static bool IsSainBoundaryReturnRequest(OperatorDecisionSnapshot snapshot)
    {
        return snapshot.MovementAuthority.BrokerPlan.Contract.RequestKind == VanguardMovementContractPolicy.BreakSainSearchReturnBubble;
    }

    private static bool IsSainBoundaryReturnRequest(string requestKind)
    {
        return string.Equals(requestKind, VanguardMovementContractPolicy.BreakSainSearchReturnBubble, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedHardReturnBackend(string backend)
    {
        return string.Equals(backend, "BIGBRAIN_GOTOSOMEPOINT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backend, "BIGBRAIN_GOTOSOMEPOINTDATA", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backend, "EFT_GO_TO_POINT", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryRefreshPreemptForCommand(PendingReturnState state, OperatorDecisionSnapshot snapshot, BotOwner botOwner, DateTimeOffset now, out string outcome, out string summary)
    {
        TimeSpan ttl = TimeSpan.FromSeconds(Math.Max(8f, VanguardMovementAuthorityDoctrine.MovementLeaseMaxDurationSeconds));
        string preCommandReason = IsIsolatedCombatReleaseActive(snapshot.BotProfileId, now)
            ? "before_command_continuation:isolated_combat_release"
            : "before_command_continuation";
        var result = IsSainBoundaryReturnRequest(state.RequestKind)
            ? VanguardExternalAuthorityAdapter.RequestSainBoundaryReturnContinuationPreempt(botOwner, snapshot, "sain_boundary_" + preCommandReason, ttl, now, allowActiveVanguardPathResidue: false)
            : VanguardExternalAuthorityAdapter.RequestMovementHardReturnContinuationPreempt(botOwner, snapshot, "hard_return_" + preCommandReason, ttl, now, allowActiveVanguardPathResidue: false);
        outcome = result.Outcome.ToString();
        summary = result.Summary;
        if (IsStrictAuthorityGranted(result.Outcome))
        {
            return true;
        }

        // Vanguard: after the main scheduler has already opened the primary HardReturn window,
        // stale EFT path/mover residue is not allowed to become a terminal pre-command abort.
        // LootingBots, ORBIT semantic activity and direct threat are still hard blockers.
        if (result.Outcome == VanguardExternalPreemptOutcome.FailedPathStillActive
            || result.Outcome == VanguardExternalPreemptOutcome.FailedMoverBusy
            || result.Outcome == VanguardExternalPreemptOutcome.Pending)
        {
            bool orbitNonDriveResidue = result.After.OrbitBrainLayerActive
                && !result.After.DirectThreatLikely
                && !result.After.LootingBotsActive
                && !result.After.LootingBotsTaskRunning
                && !result.After.LootingBotsHasActiveLootable
                && !result.After.MoverMoving;
            bool safeScheduledResidue = !result.After.DirectThreatLikely
                && !result.After.LootingBotsActive
                && !result.After.LootingBotsTaskRunning
                && !result.After.LootingBotsHasActiveLootable
                && (!(result.After.OrbitSemanticActive || result.After.IsOrbitObjectiveResidue) || orbitNonDriveResidue);
            if (safeScheduledResidue)
            {
                outcome = "ScheduledResidueAccepted:" + result.Outcome;
                summary = result.Summary + ";schedulerPrimaryWindow=true;residueAcceptedBeforeCommand=true;orbitNonDriveResidueAccepted=true;cleanAuth=true";
                VanguardMainIntentScheduler.ReportPrimaryProgress(snapshot.BotProfileId, now, "scheduled_residue_accepted_before_command", summary, state.SchedulerWindowId);
                return true;
            }
        }

        return false;
    }

    private static bool RefreshMovementAuthorityHold(HardReturnLeaseState lease, OperatorDecisionSnapshot snapshot, BotOwner botOwner, DateTimeOffset now, out string reason)
    {
        if (!IsHardReturnContinuationAllowed(snapshot, out var continuationReason))
        {
            lease.NextAuthorityRefreshAtUtc = now + AuthorityRefreshInterval;
            reason = "continuation_not_allowed:" + continuationReason;
            LogThrottled("authorityContinuationBlocked|" + lease.BotProfileId + "|" + continuationReason, now,
                $"VANGUARD_RETURN_CONTINUATION_BLOCKED {lease.Summary}; reason={Safe(continuationReason)}; bubbleDist={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; startThreshold={VanguardMovementAuthorityDoctrine.HardCorrectionMeters:0.00}; completion={VanguardMovementAuthorityDoctrine.HardReturnCompletionMeters:0.00}; startEligibilityNotReused=true; tag={ReturnContinuationStatusTag}; returnAuthorityTag={StatusTag}; actionRallyTag={ActionRallyStatusTag}");
            return false;
        }

        TimeSpan ttl = TimeSpan.FromSeconds(Math.Max(6f, Math.Min(12f, (lease.MaxUntilUtc - now).TotalSeconds + 2.0d)));
        string continuationPreemptReason = IsIsolatedCombatReleaseActive(snapshot.BotProfileId, now)
            ? "active_lease_continuation:isolated_combat_release"
            : "active_lease_continuation";
        var result = IsSainBoundaryReturnRequest(lease.RequestKind)
            ? VanguardExternalAuthorityAdapter.RequestSainBoundaryReturnContinuationPreempt(botOwner, snapshot, "sain_boundary_" + continuationPreemptReason, ttl, now, allowActiveVanguardPathResidue: true)
            : VanguardExternalAuthorityAdapter.RequestMovementHardReturnContinuationPreempt(botOwner, snapshot, "hard_return_" + continuationPreemptReason, ttl, now, allowActiveVanguardPathResidue: true);

        lease.NextAuthorityRefreshAtUtc = now + AuthorityRefreshInterval;
        lease.ExternalPreemptOutcome = result.Outcome.ToString();
        reason = result.Outcome + ":" + result.Reason;
        if (!IsStrictAuthorityGranted(result.Outcome))
        {
            LogThrottled("authorityRefreshBlocked|" + lease.BotProfileId + "|" + result.Outcome, now,
                $"VANGUARD_RETURN_AUTHORITY_REFRESH_BLOCKED {lease.Summary}; outcome={result.Outcome}; reason={Safe(result.Reason)}; ttl={ttl.TotalSeconds:0.00}; preempt={Safe(result.Summary)}; continuation=true; startEligibilityNotReused=true; activeVanguardPathPreserved=true; tag={StatusTag}; continuationTag={ReturnContinuationStatusTag}; actionRallyTag={ActionRallyStatusTag}");
            return false;
        }

        LogThrottled("authorityRefresh|" + lease.BotProfileId, now,
            $"VANGUARD_RETURN_CONTINUATION_REFRESHED {lease.Summary}; outcome={result.Outcome}; ttl={ttl.TotalSeconds:0.00}; authorityHeld=true; bubbleDist={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; continueUntilCompletion={lease.CompletionMeters:0.00}; broadAcceptDiagnostic={lease.ActionRallyAcceptMeters:0.00}; startEligibilityNotReused=true; activeVanguardPathPreserved=true; tag={ReturnContinuationStatusTag}; returnAuthorityTag={StatusTag}; actionRallyTag={ActionRallyStatusTag}");
        return true;
    }

    private static bool IsHardReturnContinuationAllowed(OperatorDecisionSnapshot snapshot, out string reason)
    {
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "not_alive";
            return false;
        }

        if (!snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement || !snapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            reason = "owner_unreliable";
            return false;
        }

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot))
        {
            if (!TryAllowIsolatedCombatRelease(snapshot, DateTimeOffset.UtcNow, "continuation", out var releaseReason))
            {
                reason = "true_direct_threat";
                return false;
            }

            reason = "isolated_combat_release_allowed:" + releaseReason;
        }

        if (VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(snapshot))
        {
            reason = "stationary_medical_authority";
            return false;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.HardReturnCompletionMeters)
        {
            reason = "inside_hard_return_completion";
            return false;
        }

        // Critical Vanguard rule: do not reapply HardCorrectionMeters/HardOutsideBubble while a lease is active.
        reason = "eligible_continuation_not_start_threshold";
        return true;
    }

    private static void ExtendLeaseWindowForProgress(HardReturnLeaseState lease, DateTimeOffset now)
    {
        DateTimeOffset candidate = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementLeaseProgressExtendSeconds);
        if (candidate > lease.MaxUntilUtc && candidate <= lease.HardMaxUntilUtc)
        {
            lease.MaxUntilUtc = candidate;
            VanguardReturnMovementCommandStore.RefreshLeaseWindow(lease.BotProfileId, lease.MaxUntilUtc, "progress_extend");
        }
    }

    private static bool ShouldSprintHardRegroup(OperatorDecisionSnapshot snapshot)
    {
        bool sprint = snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.HardCorrectionMeters;
        if (sprint)
        {
            LogThrottled("hardRegroupSprint|" + snapshot.BotProfileId, DateTimeOffset.UtcNow,
                $"VANGUARD_HARD_REGROUP_SPRINT_ENABLED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; bubbleDist={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; threshold={VanguardMovementAuthorityDoctrine.HardCorrectionMeters:0.00}; reason=hard_return_outside_threshold; tag={HardRegroupSprintStatusTag}; returnAuthorityTag={StatusTag}");
        }

        return sprint;
    }

    private static float CalculateLeaseWindowSeconds(float bubbleDistance, float pathDistance)
    {
        float distance = Math.Max(bubbleDistance, pathDistance);
        float estimated = 12f + distance / 3.2f;
        return Math.Max(VanguardMovementAuthorityDoctrine.MovementLeaseMaxDurationSeconds, Math.Min(VanguardMovementAuthorityDoctrine.MovementLeaseHardMaxSeconds, estimated));
    }

    private static bool TryRestartHardReturnCommand(HardReturnLeaseState lease, DateTimeOffset now, string physicalSummary, out string result)
    {
        if (VanguardReturnMovementCommandStore.TryRestartOwned(lease.LeaseId, lease.BotProfileId, now, physicalSummary, out result))
        {
            lease.CommandResult = result;
            return true;
        }

        if (!result.StartsWith("active_command_missing_or_expired", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool reissued = VanguardReturnMovementCommandStore.Issue(
            lease.LeaseId,
            lease.OperatorId,
            lease.BotProfileId,
            lease.Anchor,
            lease.AnchorRadiusMeters,
            lease.Sprint,
            now,
            lease.MaxUntilUtc,
            lease.RequestKind,
            lease.PathValidationSummary + ";physical_reissue=true",
            lease.PathDistanceMeters,
            out var issueResult);
        result = reissued
            ? "reissued_missing_owned_command_same_lease:" + issueResult
            : "missing_owned_command_reissue_failed:" + issueResult;
        if (reissued)
        {
            lease.CommandResult = issueResult;
        }
        return reissued;
    }

    private static bool TryReanchorActionRally(HardReturnLeaseState lease, OperatorDecisionSnapshot snapshot, BotOwner botOwner, Vector3 botPosition, DateTimeOffset now, string cause, out string reason)
    {
        reason = "none";
        if (lease.ReanchorCount >= lease.MaxReanchorsPerLease)
        {
            reason = "reanchor_limit_reached:" + lease.ReanchorCount.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        if (now < lease.MinUntilUtc)
        {
            reason = "min_window_not_elapsed";
            return false;
        }

        if (now < lease.NextReanchorAllowedAtUtc)
        {
            reason = "reanchor_cooldown:" + (lease.NextReanchorAllowedAtUtc - now).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        if (!RefreshMovementAuthorityHold(lease, snapshot, botOwner, now, out var refreshReason))
        {
            reason = "authority_refresh_failed:" + refreshReason;
            return false;
        }

        if (!TryResolveReturnAnchor(snapshot, botPosition, now, out var newAnchor, out var anchorReason, out var pathSummary, out var pathDistance, out var anchorScoreSummary))
        {
            reason = "anchor_path_failed:" + anchorReason;
            return false;
        }

        bool sprint = ShouldSprintHardRegroup(snapshot);
        VanguardReturnMovementCommandStore.RefreshLeaseWindow(lease.BotProfileId, lease.MaxUntilUtc, "reanchor_before_command");
        var retargetResult = VanguardReturnMovementCommandStore.TryRetargetActive(
            lease.LeaseId,
            lease.BotProfileId,
            newAnchor,
            lease.AnchorRadiusMeters,
            sprint,
            now,
            lease.MaxUntilUtc,
            pathSummary,
            pathDistance,
            cause);
        bool retargeted = retargetResult.Applied;
        string commandResult = retargetResult.ToString();
        if (!retargeted && retargetResult.Outcome == VanguardMovementRetargetOutcome.RejectedMissingCommand)
        {
            retargeted = VanguardReturnMovementCommandStore.Issue(
                lease.LeaseId,
                lease.OperatorId,
                lease.BotProfileId,
                newAnchor,
                lease.AnchorRadiusMeters,
                sprint,
                now,
                lease.MaxUntilUtc,
                lease.RequestKind,
                pathSummary,
                pathDistance,
                out commandResult);
        }

        if (retargetResult.Outcome == VanguardMovementRetargetOutcome.ExtendedOnlyNotMaterial)
        {
            // The runtime typed result: expiry extension is accepted by the command store, but no physical
            // anchor mutation occurred and the lease must not advance its logical target.
            lease.NextReanchorAllowedAtUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.ActionRallyReanchorCooldownSeconds);
            reason = "reanchor_not_material:" + commandResult;
            LogThrottled("reanchorNotMaterial|" + lease.BotProfileId, now,
                $"VANGUARD_HARD_RETURN_RETARGET_NOT_MATERIAL {lease.Summary}; desiredAnchor={FormatVector(newAnchor)}; commandedAnchor={FormatVector(retargetResult.CommandedAnchor)}; desiredDelta={HorizontalDistance(retargetResult.CommandedAnchor, newAnchor):0.00}; cause={Safe(cause)}; command={Safe(commandResult)}; logicalAnchorAdvanced=false; doctrine=expiry_extension_is_not_physical_retarget; tag={VanguardPrimaryExecutionContract.MovementRetargetStatusTag}");
            return false;
        }

        if (!retargeted)
        {
            reason = "move_bridge_reanchor_rejected:" + commandResult;
            LogThrottled("reanchorCommandFail|" + lease.BotProfileId + "|" + commandResult, now,
                $"VANGUARD_BOUNDARY_RETURN_REANCHOR_COMMAND_REJECTED {lease.Summary}; cause={Safe(cause)}; anchor={FormatVector(newAnchor)}; path={Safe(pathSummary)}; anchorScore={Safe(anchorScoreSummary)}; command={Safe(commandResult)}; tag={MoveBridgeStatusTag}; returnAuthorityTag={StatusTag}; goToSomePointTag={GoToSomePointBridgeStatusTag}; actionRallyTag={ActionRallyStatusTag}; continuationTag={ReturnContinuationStatusTag}; returnPathTag={ReturnPathValidationCompatibilityTag}");
            return false;
        }

        lease.ReanchorCount++;
        lease.Anchor = newAnchor;
        lease.InitialAnchorDistance = HorizontalDistance(botPosition, newAnchor);
        lease.LastAnchorDistance = lease.InitialAnchorDistance;
        lease.LastBubbleDistance = snapshot.SquadCohesion.OperatorDistanceToOwner;
        lease.OwnerPositionAtAnchor = snapshot.SquadCohesion.OwnerPosition ?? newAnchor;
        lease.PathValidationSummary = pathSummary;
        lease.AnchorScoreSummary = anchorScoreSummary;
        lease.PathDistanceMeters = pathDistance;
        lease.CommandResult = commandResult;
        lease.Sprint = sprint;
        lease.LastProgressKind = "controlled_reanchor:" + cause;
        lease.LastWorldPosition = botPosition;
        lease.LastWorldSampleAtUtc = now;
        lease.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
        lease.PhysicalRestartCount = 0;
        lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(lease.NoProgressSeconds);
        lease.NextReanchorAllowedAtUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.ActionRallyReanchorCooldownSeconds);
        reason = anchorReason + ":" + commandResult;
        return true;
    }

    private static bool TryAllowIsolatedCombatRelease(OperatorDecisionSnapshot snapshot, DateTimeOffset now, string phase, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "not_alive";
            return false;
        }

        float bubble = snapshot.SquadCohesion.OperatorDistanceToOwner;
        if (bubble < VanguardMovementAuthorityDoctrine.HardCorrectionMeters)
        {
            reason = "not_hard_outside_bubble";
            ClearIsolatedCombatWatch(snapshot.BotProfileId);
            return false;
        }

        if (HasImmediateHardStopThreat(snapshot))
        {
            reason = "immediate_hard_stop_threat";
            MarkIsolatedCombatWatch(snapshot, now, phase, immediateBlocked: true);
            return false;
        }

        bool searchLike = VanguardMovementAuthorityDoctrine.IsSainSearchLike(snapshot)
            || string.Equals(snapshot.MovementAuthority.CurrentAuthority, "SAIN_OUT_OF_ENVELOPE_READONLY", StringComparison.OrdinalIgnoreCase)
            || string.Equals(snapshot.MovementAuthority.BrokerPlan.RequestKind, VanguardMovementContractPolicy.BreakSainSearchReturnBubble, StringComparison.OrdinalIgnoreCase)
            || string.Equals(snapshot.Sain.Classification, "sain_search", StringComparison.OrdinalIgnoreCase)
            || snapshot.Sain.Searching == true;
        if (!searchLike)
        {
            reason = "not_sain_search_like";
            return false;
        }

        var watch = MarkIsolatedCombatWatch(snapshot, now, phase, immediateBlocked: false);
        if (now < watch.ReleaseEligibleAtUtc && !watch.ReleaseActiveUntilUtc.HasValue)
        {
            reason = "watching_grace:" + (watch.ReleaseEligibleAtUtc - now).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        ActivateIsolatedCombatRelease(snapshot.BotProfileId, now, watch, phase);
        reason = "isolated_search_release:bubble=" + bubble.ToString("0.00", CultureInfo.InvariantCulture)
            + ";age=" + (now - watch.FirstSeenAtUtc).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + ";phase=" + Safe(phase);
        return true;
    }

    private static IsolatedCombatWatchState MarkIsolatedCombatWatch(OperatorDecisionSnapshot snapshot, DateTimeOffset now, string phase, bool immediateBlocked)
    {
        lock (Sync)
        {
            string key = snapshot.BotProfileId;
            if (!IsolatedCombatWatchByBotProfileId.TryGetValue(key, out var watch) || (now - watch.LastSeenAtUtc).TotalSeconds > 15.0d)
            {
                watch = new IsolatedCombatWatchState(snapshot.BotProfileId, now, now, now + IsolatedCombatGrace, null, 0, 0);
            }

            watch = watch.WithSeen(now, immediateBlocked);
            IsolatedCombatWatchByBotProfileId[key] = watch;
            LogThrottled("isolatedCombatWatch|" + key + "|" + Safe(phase), now,
                $"VANGUARD_ISOLATED_COMBAT_WATCH operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; phase={Safe(phase)}; bubbleDist={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; immediateBlocked={Bool(immediateBlocked)}; firstSeenAgo={(now - watch.FirstSeenAtUtc).TotalSeconds:0.00}; releaseEligibleIn={(watch.ReleaseEligibleAtUtc - now).TotalSeconds:0.00}; blockedCount={watch.ImmediateBlockedCount}; observedCount={watch.ObservedCount}; tag={IsolatedCombatReleaseStatusTag}; returnAuthorityTag={StatusTag}");
            return watch;
        }
    }

    private static void ActivateIsolatedCombatRelease(string botProfileId, DateTimeOffset now, IsolatedCombatWatchState watch, string phase)
    {
        var active = watch.WithRelease(now + IsolatedCombatReleaseWindow);
        lock (Sync)
        {
            IsolatedCombatWatchByBotProfileId[botProfileId] = active;
        }

        LogThrottled("isolatedCombatRelease|" + botProfileId + "|" + Safe(phase), now,
            $"VANGUARD_ISOLATED_COMBAT_RELEASE_GRANTED botProfile={Safe(botProfileId)}; phase={Safe(phase)}; releaseUntilUtc={active.ReleaseActiveUntilUtc:O}; window={IsolatedCombatReleaseWindow.TotalSeconds:0.00}; reason=hard_outside_non_immediate_sain_search; tag={IsolatedCombatReleaseStatusTag}; returnAuthorityTag={StatusTag}");
    }

    private static bool IsIsolatedCombatReleaseActive(string botProfileId, DateTimeOffset now)
    {
        lock (Sync)
        {
            return IsolatedCombatWatchByBotProfileId.TryGetValue(botProfileId, out var watch)
                && watch.ReleaseActiveUntilUtc.HasValue
                && watch.ReleaseActiveUntilUtc.Value > now;
        }
    }

    private static void ClearIsolatedCombatWatch(string botProfileId)
    {
        lock (Sync)
        {
            IsolatedCombatWatchByBotProfileId.Remove(botProfileId);
        }
    }

    private static bool HasImmediateHardStopThreat(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot.Medical.Safety.EnemyCanShoot || snapshot.Medical.Safety.IncomingFireRecent || snapshot.Threat.EnemyCanShoot == true || snapshot.Threat.ShotMeRecently == true || snapshot.Threat.ShotAtMeRecently == true)
        {
            return true;
        }

        return snapshot.Threat.Distance.HasValue
            && snapshot.Threat.Distance.Value <= 12.0f
            && (snapshot.Threat.EnemyVisible == true || snapshot.Threat.EnemyLineOfSight == true)
            && snapshot.Threat.TimeSinceSeen.HasValue
            && snapshot.Threat.TimeSinceSeen.Value >= 0f
            && snapshot.Threat.TimeSinceSeen.Value <= 2.0f;
    }

    private static bool IsPreemptOutcomeUsableForPending(VanguardExternalPreemptOutcome outcome)
    {
        return outcome == VanguardExternalPreemptOutcome.Granted
            || outcome == VanguardExternalPreemptOutcome.Pending
            || outcome == VanguardExternalPreemptOutcome.FailedOrbitStillActive
            || outcome == VanguardExternalPreemptOutcome.FailedLootingBotsStillActive;
    }

    private static bool IsRetriableExternalQuiesceOutcome(string outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome))
        {
            return false;
        }

        return outcome.IndexOf("FailedOrbitStillActive", StringComparison.OrdinalIgnoreCase) >= 0
            || outcome.IndexOf("FailedLootingBotsStillActive", StringComparison.OrdinalIgnoreCase) >= 0
            || outcome.IndexOf("Pending", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsStrictAuthorityGranted(VanguardExternalPreemptOutcome outcome)
    {
        return outcome == VanguardExternalPreemptOutcome.Granted;
    }

    private static string CheckInterrupt(OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot == null || !snapshot.Alive)
        {
            return "operator_dead";
        }

        if (VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out var combatProtectionReason))
        {
            return "sain_combat_primary_protected:" + combatProtectionReason;
        }

        if (!snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement)
        {
            return "owner_unreliable";
        }

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot))
        {
            if (TryAllowIsolatedCombatRelease(snapshot, now, "interrupt", out var releaseReason))
            {
                LogThrottled("isolatedInterruptRelease|" + snapshot.BotProfileId, now,
                    $"VANGUARD_ISOLATED_COMBAT_INTERRUPT_RELEASED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(releaseReason)}; bubbleDist={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; immediateHardStop={Bool(HasImmediateHardStopThreat(snapshot))}; tag={IsolatedCombatReleaseStatusTag}; returnAuthorityTag={StatusTag}");
                return "none";
            }

            return "true_direct_threat";
        }

        if (VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(snapshot))
        {
            return "stationary_medical_authority";
        }

        return "none";
    }

    private static bool TryResolveReturnAnchor(OperatorDecisionSnapshot snapshot, Vector3 botPosition, DateTimeOffset now, out Vector3 anchor, out string reason, out string pathSummary, out float pathDistance, out string scoreSummary)
    {
        anchor = Vector3.zero;
        reason = "none";
        pathSummary = "none";
        pathDistance = 0f;
        scoreSummary = "none";
        if (!snapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            reason = "owner_position_missing";
            return false;
        }

        if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner("HardReturnAnchorResolve", snapshot.OwnerProfileId, 1, 1, out var resolveBudgetReason))
        {
            reason = "frame_path_budget_pending:" + resolveBudgetReason;
            pathSummary = resolveBudgetReason;
            scoreSummary = "deferred_without_failure=true";
            return false;
        }

        Vector3 owner = snapshot.SquadCohesion.OwnerPosition.Value;
        Vector3 approach = botPosition - owner;
        approach.y = 0f;
        if (approach.sqrMagnitude <= 0.25f)
        {
            Vector3 fallback = snapshot.SquadCohesion.OwnerForward ?? Vector3.forward;
            approach = -Vector3.ProjectOnPlane(fallback, Vector3.up);
        }

        if (approach.sqrMagnitude <= 0.25f)
        {
            reason = "direction_unavailable";
            return false;
        }

        approach.Normalize();
        Vector3 forward = snapshot.SquadCohesion.OwnerForward ?? Vector3.forward;
        forward = Vector3.ProjectOnPlane(forward, Vector3.up);
        if (forward.sqrMagnitude <= 0.25f)
        {
            forward = -approach;
        }
        forward.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        if (right.sqrMagnitude <= 0.25f)
        {
            right = Quaternion.Euler(0f, 90f, 0f) * forward;
        }
        right.Normalize();

        float[] radii =
        {
            VanguardMovementAuthorityDoctrine.ActionRallyPreferredMeters,
            VanguardMovementAuthorityDoctrine.ActionRallyNearMeters,
            VanguardMovementAuthorityDoctrine.ActionRallyWideMeters,
            VanguardMovementAuthorityDoctrine.ActionRallyTightMeters,
            VanguardMovementAuthorityDoctrine.ActionRallyOuterMeters
        };

        Vector3[] directions =
        {
            approach,
            Quaternion.Euler(0f, 25f, 0f) * approach,
            Quaternion.Euler(0f, -25f, 0f) * approach,
            right,
            -right,
            -forward,
            Quaternion.Euler(0f, 45f, 0f) * approach,
            Quaternion.Euler(0f, -45f, 0f) * approach,
            forward
        };

        PhysicalRecoveryState? recovery = GetPhysicalRecovery(snapshot.BotProfileId, now);
        if (recovery != null
            && TryResolvePhysicalEscapeWaypoint(snapshot, botPosition, owner, forward, right, recovery, now, out anchor, out reason, out pathSummary, out pathDistance, out scoreSummary))
        {
            return true;
        }

        var rawCandidates = new List<RawRallyCandidate>(Math.Min(MaxRawAnchorSamplesPerResolve, radii.Length * directions.Length));
        string lastFailure = "none";
        int excludedFailedAnchor = 0;
        for (int radiusIndex = 0; radiusIndex < radii.Length && rawCandidates.Count < MaxRawAnchorSamplesPerResolve; radiusIndex++)
        {
            float radius = radii[radiusIndex];
            for (int directionIndex = 0; directionIndex < directions.Length && rawCandidates.Count < MaxRawAnchorSamplesPerResolve; directionIndex++)
            {
                Vector3 dir = directions[directionIndex];
                dir.y = 0f;
                if (dir.sqrMagnitude <= 0.25f)
                {
                    continue;
                }

                dir.Normalize();
                Vector3 raw = owner + dir * radius;
                raw.y = owner.y;
                if (!TrySampleNavmesh(raw, 7.0f, out var sampled))
                {
                    lastFailure = "sample_failed_rally_radius_" + radius.ToString("0", CultureInfo.InvariantCulture) + "_dir_" + directionIndex.ToString(CultureInfo.InvariantCulture);
                    continue;
                }

                float sampledOwnerDistance = HorizontalDistance(sampled, owner);
                if (sampledOwnerDistance > VanguardMovementAuthorityDoctrine.ActionRallyAcceptMeters + 6f)
                {
                    lastFailure = "sample_too_far_from_action_rally:" + sampledOwnerDistance.ToString("0.00", CultureInfo.InvariantCulture);
                    continue;
                }

                if (recovery != null && HorizontalDistance(sampled, recovery.FailedAnchor) < FailedAnchorExclusionMeters)
                {
                    excludedFailedAnchor++;
                    continue;
                }

                float preferredPenalty = Mathf.Abs(radius - VanguardMovementAuthorityDoctrine.ActionRallyPreferredMeters) * 1.5f;
                float travelEstimate = HorizontalDistance(botPosition, sampled);
                float preliminary = 200f - preferredPenalty - travelEstimate * 0.18f - directionIndex * 0.35f - radiusIndex * 0.20f;
                rawCandidates.Add(new RawRallyCandidate(sampled, radius, radiusIndex, directionIndex, sampledOwnerDistance, preliminary));
            }
        }

        rawCandidates.Sort((left, rightCandidate) => rightCandidate.PreliminaryScore.CompareTo(left.PreliminaryScore));
        var candidates = new List<VanguardActionRallyAnchorCandidate>(MaxAnchorPathValidationsPerResolve + 1);
        int pathValidations = 0;
        int rejectedByScore = 0;
        foreach (var raw in rawCandidates)
        {
            if (pathValidations >= MaxAnchorPathValidationsPerResolve)
            {
                break;
            }

            pathValidations++;
            if (!TryValidateCompletePath(botPosition, raw.Anchor, out var candidatePathSummary, out var candidatePathDistance))
            {
                lastFailure = candidatePathSummary;
                continue;
            }

            var scored = VanguardActionRallyAnchorScorer.Score(
                snapshot.BotProfileId,
                botPosition,
                owner,
                raw.Anchor,
                raw.Radius,
                raw.RadiusIndex,
                raw.DirectionIndex,
                snapshot.SquadCohesion.OperatorDistanceToOwner,
                raw.OwnerDistanceMeters,
                candidatePathSummary,
                candidatePathDistance,
                now);
            if (scored.Accepted)
            {
                candidates.Add(scored);
            }
            else
            {
                rejectedByScore++;
                lastFailure = scored.ScoreSummary;
            }
        }

        // Owner fallback is evaluated only when the bounded candidate set produced no accepted route.
        if (candidates.Count == 0
            && pathValidations < MaxAnchorPathValidationsPerResolve
            && TrySampleNavmesh(owner, 10.0f, out var ownerSample))
        {
            float ownerSampleDistance = HorizontalDistance(ownerSample, owner);
            bool excluded = recovery != null && HorizontalDistance(ownerSample, recovery.FailedAnchor) < FailedAnchorExclusionMeters;
            if (!excluded && ownerSampleDistance <= VanguardMovementAuthorityDoctrine.ActionRallyAcceptMeters
                && TryValidateCompletePath(botPosition, ownerSample, out var ownerPathSummary, out var ownerPathDistance))
            {
                pathValidations++;
                var scoredOwner = VanguardActionRallyAnchorScorer.Score(
                    snapshot.BotProfileId,
                    botPosition,
                    owner,
                    ownerSample,
                    0f,
                    radii.Length,
                    0,
                    snapshot.SquadCohesion.OperatorDistanceToOwner,
                    ownerSampleDistance,
                    ownerPathSummary,
                    ownerPathDistance,
                    now,
                    ownerFallback: true);
                if (scoredOwner.Accepted)
                {
                    candidates.Add(scoredOwner);
                }
                else
                {
                    rejectedByScore++;
                    lastFailure = scoredOwner.ScoreSummary;
                }
            }
        }

        if (VanguardActionRallyAnchorScorer.TrySelectBest(candidates, out var best))
        {
            anchor = best.Anchor;
            reason = best.AnchorReason + (recovery == null ? string.Empty : ":cross_lease_failed_anchor_excluded");
            pathSummary = best.PathSummary + ";boundedPathValidations=" + pathValidations.ToString(CultureInfo.InvariantCulture);
            pathDistance = best.PathDistanceMeters;
            scoreSummary = best.ScoreSummary + ";rawCandidates=" + rawCandidates.Count.ToString(CultureInfo.InvariantCulture) + ";excludedFailedAnchor=" + excludedFailedAnchor.ToString(CultureInfo.InvariantCulture);
            LogThrottled("anchorScore|" + snapshot.BotProfileId, now,
                $"VANGUARD_BOUNDED_ANCHOR_SELECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; selected={Safe(reason)}; score={best.Score:0.00}; pathDist={best.PathDistanceMeters:0.00}; ownerDist={best.OwnerDistanceMeters:0.00}; rawCandidates={rawCandidates.Count}; maxRawCandidates={MaxRawAnchorSamplesPerResolve}; pathValidations={pathValidations}; maxPathValidations={MaxAnchorPathValidationsPerResolve}; excludedFailedAnchor={excludedFailedAnchor}; recoveryFailures={(recovery?.FailureCount ?? 0)}; summary={Safe(scoreSummary)}; tag={BoundedPathComputationStatusTag}; recoveryTag={CrossLeasePhysicalRecoveryStatusTag}");
            return true;
        }

        reason = rawCandidates.Count == 0 ? "no_raw_action_rally_anchor" : "no_accepted_action_rally_anchor";
        pathSummary = "lastFailure=" + lastFailure
            + ";rawCandidates=" + rawCandidates.Count.ToString(CultureInfo.InvariantCulture)
            + ";pathValidations=" + pathValidations.ToString(CultureInfo.InvariantCulture)
            + ";excludedFailedAnchor=" + excludedFailedAnchor.ToString(CultureInfo.InvariantCulture)
            + ";rejectedByScore=" + rejectedByScore.ToString(CultureInfo.InvariantCulture);
        scoreSummary = pathSummary;
        return false;
    }

    private static bool TryResolvePhysicalEscapeWaypoint(OperatorDecisionSnapshot snapshot, Vector3 botPosition, Vector3 owner, Vector3 forward, Vector3 right, PhysicalRecoveryState recovery, DateTimeOffset now, out Vector3 anchor, out string reason, out string pathSummary, out float pathDistance, out string scoreSummary)
    {
        anchor = Vector3.zero;
        reason = "none";
        pathSummary = "none";
        pathDistance = 0f;
        scoreSummary = "none";

        Vector3 towardOwner = owner - botPosition;
        towardOwner.y = 0f;
        if (towardOwner.sqrMagnitude <= 0.25f)
        {
            towardOwner = forward;
        }
        towardOwner.Normalize();

        Vector3 awayFromFailedAnchor = botPosition - recovery.FailedAnchor;
        awayFromFailedAnchor.y = 0f;
        if (awayFromFailedAnchor.sqrMagnitude <= 0.25f)
        {
            awayFromFailedAnchor = -towardOwner;
        }
        awayFromFailedAnchor.Normalize();

        Vector3[] escapeDirections =
        {
            right,
            -right,
            towardOwner,
            Quaternion.Euler(0f, 45f, 0f) * towardOwner,
            Quaternion.Euler(0f, -45f, 0f) * towardOwner,
            awayFromFailedAnchor
        };
        float[] escapeRadii = { 2.5f, 4.25f };
        var candidates = new List<RawRallyCandidate>(escapeDirections.Length * escapeRadii.Length);
        for (int radiusIndex = 0; radiusIndex < escapeRadii.Length; radiusIndex++)
        {
            for (int directionIndex = 0; directionIndex < escapeDirections.Length; directionIndex++)
            {
                Vector3 direction = escapeDirections[directionIndex];
                direction.y = 0f;
                if (direction.sqrMagnitude <= 0.25f)
                {
                    continue;
                }

                direction.Normalize();
                Vector3 raw = botPosition + direction * escapeRadii[radiusIndex];
                if (!TrySampleNavmesh(raw, 1.75f, out var sampled))
                {
                    continue;
                }

                float worldDelta = HorizontalDistance(botPosition, sampled);
                if (worldDelta < 1.50f || HorizontalDistance(sampled, recovery.LastWorldPosition) < 1.0f)
                {
                    continue;
                }

                float ownerGain = HorizontalDistance(botPosition, owner) - HorizontalDistance(sampled, owner);
                float preliminary = 100f + ownerGain * 4f - radiusIndex * 0.5f - directionIndex * 0.25f;
                candidates.Add(new RawRallyCandidate(sampled, escapeRadii[radiusIndex], radiusIndex, directionIndex, HorizontalDistance(sampled, owner), preliminary));
            }
        }

        candidates.Sort((left, rightCandidate) => rightCandidate.PreliminaryScore.CompareTo(left.PreliminaryScore));
        int pathValidations = 0;
        foreach (var candidate in candidates)
        {
            if (pathValidations >= MaxEscapePathValidationsPerResolve)
            {
                break;
            }

            pathValidations++;
            if (!TryValidateCompletePath(botPosition, candidate.Anchor, out var candidatePathSummary, out var candidatePathDistance))
            {
                continue;
            }

            anchor = candidate.Anchor;
            reason = "cross_lease_physical_escape_waypoint";
            pathSummary = candidatePathSummary + ";escapeWaypoint=true;recoveryFailures=" + recovery.FailureCount.ToString(CultureInfo.InvariantCulture);
            pathDistance = candidatePathDistance;
            scoreSummary = "escapePreliminary=" + candidate.PreliminaryScore.ToString("0.00", CultureInfo.InvariantCulture)
                + ";ownerDistance=" + candidate.OwnerDistanceMeters.ToString("0.00", CultureInfo.InvariantCulture)
                + ";pathValidations=" + pathValidations.ToString(CultureInfo.InvariantCulture)
                + ";failedAnchor=" + FormatVector(recovery.FailedAnchor);
            LogThrottled("physicalEscape|" + snapshot.BotProfileId, now,
                $"VANGUARD_PHYSICAL_ESCAPE_WAYPOINT operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={FormatVector(anchor)}; pathDist={pathDistance:0.00}; ownerDist={candidate.OwnerDistanceMeters:0.00}; recoveryFailures={recovery.FailureCount}; failedAnchor={FormatVector(recovery.FailedAnchor)}; pathValidations={pathValidations}; maxPathValidations={MaxEscapePathValidationsPerResolve}; next=local_escape_then_full_hard_return; tag={CrossLeasePhysicalRecoveryStatusTag}; boundedTag={BoundedPathComputationStatusTag}");
            return true;
        }

        return false;
    }

    private static bool TrySampleNavmesh(Vector3 raw, float radius, out Vector3 sampled)
    {
        if (NavMesh.SamplePosition(raw + Vector3.up * 0.35f, out var hit, radius, NavMesh.AllAreas))
        {
            sampled = hit.position;
            return true;
        }

        sampled = Vector3.zero;
        return false;
    }

    private static bool TryValidateCompletePath(Vector3 botPosition, Vector3 target, out string summary)
    {
        return TryValidateCompletePath(botPosition, target, out summary, out _);
    }

    private static bool TryValidateCompletePath(Vector3 botPosition, Vector3 target, out string summary, out float distance)
    {
        summary = "none";
        distance = 0f;
        if (!NavMesh.SamplePosition(botPosition + Vector3.up * 0.25f, out var botHit, 4.0f, NavMesh.AllAreas))
        {
            summary = "bot_navmesh_sample_failed";
            return false;
        }

        if (!NavMesh.SamplePosition(target + Vector3.up * 0.25f, out var targetHit, 2.0f, NavMesh.AllAreas))
        {
            summary = "target_navmesh_sample_failed";
            return false;
        }

        var path = new NavMeshPath();
        bool calculated = NavMesh.CalculatePath(botHit.position, targetHit.position, NavMesh.AllAreas, path);
        distance = PathDistance(path);
        int corners = path.corners == null ? 0 : path.corners.Length;
        summary = "calculated=" + Bool(calculated)
            + ";status=" + path.status
            + ";corners=" + corners.ToString(CultureInfo.InvariantCulture)
            + ";dist=" + distance.ToString("0.00", CultureInfo.InvariantCulture);
        return calculated && path.status == NavMeshPathStatus.PathComplete && corners >= 2;
    }

    private static float PathDistance(NavMeshPath path)
    {
        if (path.corners == null || path.corners.Length < 2)
        {
            return 0f;
        }

        float distance = 0f;
        for (int index = 1; index < path.corners.Length; index++)
        {
            distance += HorizontalDistance(path.corners[index - 1], path.corners[index]);
        }

        return distance;
    }

    private static OperatorDecisionSnapshot? FindSnapshot(IReadOnlyList<OperatorDecisionSnapshot> snapshots, string botProfileId)
    {
        foreach (var snapshot in snapshots)
        {
            if (string.Equals(snapshot.BotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase))
            {
                return snapshot;
            }
        }

        return null;
    }

    private static void FinishPending(PendingReturnState state, DateTimeOffset now, string reason, TimeSpan cooldown, OperatorDecisionSnapshot? snapshot, BotOwner? botOwner)
    {
        lock (Sync)
        {
            PendingByBotProfileId.Remove(state.BotProfileId);
        }

        SetCooldown(state.BotProfileId, now + cooldown);
        VanguardMovementOutcomeMemory.RecordLeaseOutcome(state.BotProfileId, state.Anchor, "PendingAbort", reason, state.PathDistanceMeters, now);
        string commandClear = "pending_has_no_owned_command";
        string release = VanguardExternalAuthorityAdapter.ReleaseMovementHardReturnPreempt(botOwner, state.BotProfileId, now, "pending_abort:" + reason);
        VanguardMainIntentScheduler.FinishPrimaryWindow(state.BotProfileId, now, "Failed", "pending_abort:" + reason, state.Summary, state.SchedulerWindowId);
        float elapsed = (float)(now - state.PreemptedAtUtc).TotalSeconds;
        string bubbleDist = snapshot == null ? "unknown" : snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.00", CultureInfo.InvariantCulture);
        VanguardClientDiagnosticsLog.Info(PendingStatusTag,
            $"VANGUARD_PREEMPT_PENDING_ABORTED {state.Summary}; reason={Safe(reason)}; elapsed={elapsed:0.00}; bubbleDist={bubbleDist}; cooldown={cooldown.TotalSeconds:0.00}; commandClear={Safe(commandClear)}; release={Safe(release)}; tag={PendingStatusTag}; returnAuthorityTag={StatusTag}; returnPathTag={ReturnPathValidationCompatibilityTag}");
    }

    private static void FinishLease(HardReturnLeaseState lease, DateTimeOffset now, string outcome, string reason, TimeSpan cooldown, OperatorDecisionSnapshot? snapshot, BotOwner? botOwner = null)
    {
        lock (Sync)
        {
            ActiveByBotProfileId.Remove(lease.BotProfileId);
        }

        SetCooldown(lease.BotProfileId, now + cooldown);
        string commandClear = VanguardReturnMovementCommandStore.ClearOwned(lease.BotProfileId, lease.LeaseId, lease.StartedAtUtc, outcome + ":" + reason);
        string release = VanguardExternalAuthorityAdapter.ReleaseMovementHardReturnPreempt(botOwner, lease.BotProfileId, now, outcome + ":" + reason);
        string outcomeMemory = VanguardMovementOutcomeMemory.RecordLeaseOutcome(lease.BotProfileId, lease.Anchor, outcome, reason, lease.PathDistanceMeters, now);
        VanguardMainIntentScheduler.FinishPrimaryWindow(lease.BotProfileId, now, outcome, reason, lease.Summary + ";movementOutcome=" + outcomeMemory, lease.SchedulerWindowId);
        if (string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase) && snapshot != null)
        {
            ClearPhysicalRecovery(lease.BotProfileId, "hard_return_completed");
            VanguardSquadTravelCohesionAuthority.RecordHardReturnCompleted(lease.BotProfileId, lease.OperatorId, snapshot.SquadCohesion.OperatorDistanceToOwner, now, reason);
        }
        else if (snapshot != null
            && snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.HardCorrectionMeters
            && (reason.IndexOf("PhysicalBlocked", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("NoProgressTimeout", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("PhysicalRestartRejected", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            RecordPhysicalRecovery(lease, snapshot, now, outcome + ":" + reason);
        }

        float elapsed = (float)(now - lease.StartedAtUtc).TotalSeconds;
        string bubbleDist = snapshot == null ? "unknown" : snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.00", CultureInfo.InvariantCulture);
        string logName = outcome == "Completed" ? "VANGUARD_BOUNDARY_RETURN_LEASE_COMPLETED" : outcome == "Interrupted" ? "VANGUARD_BOUNDARY_RETURN_LEASE_INTERRUPTED" : outcome == "Timeout" ? "VANGUARD_BOUNDARY_RETURN_LEASE_TIMEOUT" : "VANGUARD_BOUNDARY_RETURN_LEASE_FAILED";
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"{logName} {lease.Summary}; outcome={Safe(outcome)}; reason={Safe(reason)}; elapsed={elapsed:0.00}; bubbleDist={bubbleDist}; lastProgress={Safe(lease.LastProgressKind)}; cooldown={cooldown.TotalSeconds:0.00}; outcomeMemory={Safe(outcomeMemory)}; commandClear={Safe(commandClear)}; release={Safe(release)}; tag={MoveBridgeStatusTag}; returnAuthorityTag={StatusTag}; goToSomePointTag={GoToSomePointBridgeStatusTag}; actionRallyTag={ActionRallyStatusTag}; continuationTag={ReturnContinuationStatusTag}; returnPathTag={ReturnPathValidationCompatibilityTag}; sainBoundaryCompatibilityTag={SainBoundaryCompatibilityTag}");
    }

    private static PhysicalRecoveryState? GetPhysicalRecovery(string? botProfileId, DateTimeOffset now)
    {
        string key = Safe(botProfileId);
        lock (Sync)
        {
            if (PhysicalRecoveryByBotProfileId.TryGetValue(key, out var state))
            {
                if (state.UntilUtc > now)
                {
                    return state;
                }
                PhysicalRecoveryByBotProfileId.Remove(key);
            }
        }
        return null;
    }

    private static void RecordPhysicalRecovery(HardReturnLeaseState lease, OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason)
    {
        string key = Safe(lease.BotProfileId);
        PhysicalRecoveryState state;
        lock (Sync)
        {
            if (!PhysicalRecoveryByBotProfileId.TryGetValue(key, out state))
            {
                state = new PhysicalRecoveryState();
                PhysicalRecoveryByBotProfileId[key] = state;
            }
            state.FailureCount++;
            state.FailedAnchor = lease.Anchor;
            state.LastWorldPosition = lease.LastWorldPosition;
            state.LastFailureUtc = now;
            state.UntilUtc = now + PhysicalRecoveryMemoryWindow;
            state.LastReason = reason;
            state.FailedPathSignature = Safe(lease.PathValidationSummary);
        }
        LogThrottled("crossLeasePhysical|" + key, now,
            $"VANGUARD_CROSS_LEASE_PHYSICAL_RECOVERY operator={Safe(snapshot.OperatorId)}; botProfile={key}; failures={state.FailureCount}; failedAnchor={FormatVector(state.FailedAnchor)}; bubbleDist={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; memorySeconds={PhysicalRecoveryMemoryWindow.TotalSeconds:0.0}; reason={Safe(reason)}; sameAnchorExcludedMeters={FailedAnchorExclusionMeters:0.0}; tag={CrossLeasePhysicalRecoveryStatusTag}");
    }

    private static void ClearPhysicalRecovery(string? botProfileId, string reason)
    {
        string key = Safe(botProfileId);
        lock (Sync)
        {
            PhysicalRecoveryByBotProfileId.Remove(key);
        }
    }

    private static Vector3 ResolveBotPosition(BotOwner botOwner)
    {
        object? player = VanguardOperatorRuntimeAuditReflection.GetMember(botOwner, "GetPlayer", "Player");
        object? transform = VanguardOperatorRuntimeAuditReflection.GetDeep(player, "PlayerBones", "BodyTransform");
        object? position = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(transform, "position");
        if (position is Vector3 vector)
        {
            return vector;
        }

        object? playerTransform = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player, "Transform", "transform");
        position = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(playerTransform, "position");
        return position is Vector3 transformVector ? transformVector : Vector3.zero;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }


    private static bool IsCombatBackoffReason(string? reason)
    {
        return !string.IsNullOrWhiteSpace(reason)
            && (reason.IndexOf("true_direct_threat", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("RejectedCombatOwner", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("sain_combat_or_direct_threat", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static void SetCombatBackoff(string botProfileId, DateTimeOffset now, string reason)
    {
        DateTimeOffset until = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.HardReturnCombatBackoffSeconds);
        lock (Sync)
        {
            CombatBackoffUntilByBotProfileId[botProfileId] = until;
            CooldownUntilByBotProfileId[botProfileId] = until;
        }

        LogThrottled("combatBackoff|" + botProfileId + "|" + Safe(reason), now,
            $"VANGUARD_HARD_RETURN_COMBAT_BACKOFF botProfile={Safe(botProfileId)}; reason={Safe(reason)}; until={until:O}; cooldown={VanguardMovementAuthorityDoctrine.HardReturnCombatBackoffSeconds:0.00}; noImmediateRestart=true; tag={VanguardMovementAuthorityDoctrine.HardReturnCombatBackoffStatusTag}; returnAuthorityTag={StatusTag}; schedulerTag={MainSchedulerStatusTag}");
    }

    private static bool IsCombatBackoffBlocked(string botProfileId, DateTimeOffset now, out DateTimeOffset until)
    {
        lock (Sync)
        {
            if (CombatBackoffUntilByBotProfileId.TryGetValue(botProfileId, out until) && until > now)
            {
                return true;
            }

            if (CombatBackoffUntilByBotProfileId.ContainsKey(botProfileId))
            {
                CombatBackoffUntilByBotProfileId.Remove(botProfileId);
            }
        }

        until = DateTimeOffset.MinValue;
        return false;
    }

    private static bool IsCooldownBlocked(string botProfileId, DateTimeOffset now, out DateTimeOffset until)
    {
        lock (Sync)
        {
            if (CooldownUntilByBotProfileId.TryGetValue(botProfileId, out until) && until > now)
            {
                return true;
            }

            if (CooldownUntilByBotProfileId.ContainsKey(botProfileId))
            {
                CooldownUntilByBotProfileId.Remove(botProfileId);
            }
        }

        until = DateTimeOffset.MinValue;
        return false;
    }

    private static void SetCooldown(string botProfileId, DateTimeOffset until)
    {
        lock (Sync)
        {
            CooldownUntilByBotProfileId[botProfileId] = until;
        }
    }

    private static void LogThrottled(string key, DateTimeOffset now, string message)
    {
        lock (Sync)
        {
            if (LastLogByKey.TryGetValue(key, out var last) && now - last < LogInterval)
            {
                return;
            }

            LastLogByKey[key] = now;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag, message);
    }

    private static string FormatVector(Vector3 vector)
    {
        return vector.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + vector.y.ToString("0.0", CultureInfo.InvariantCulture) + "," + vector.z.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }

    private readonly struct IsolatedCombatWatchState
    {
        public IsolatedCombatWatchState(string botProfileId, DateTimeOffset firstSeenAtUtc, DateTimeOffset lastSeenAtUtc, DateTimeOffset releaseEligibleAtUtc, DateTimeOffset? releaseActiveUntilUtc, int observedCount, int immediateBlockedCount)
        {
            BotProfileId = Safe(botProfileId);
            FirstSeenAtUtc = firstSeenAtUtc;
            LastSeenAtUtc = lastSeenAtUtc;
            ReleaseEligibleAtUtc = releaseEligibleAtUtc;
            ReleaseActiveUntilUtc = releaseActiveUntilUtc;
            ObservedCount = observedCount;
            ImmediateBlockedCount = immediateBlockedCount;
        }

        public string BotProfileId { get; }
        public DateTimeOffset FirstSeenAtUtc { get; }
        public DateTimeOffset LastSeenAtUtc { get; }
        public DateTimeOffset ReleaseEligibleAtUtc { get; }
        public DateTimeOffset? ReleaseActiveUntilUtc { get; }
        public int ObservedCount { get; }
        public int ImmediateBlockedCount { get; }

        public IsolatedCombatWatchState WithSeen(DateTimeOffset now, bool immediateBlocked)
        {
            return new IsolatedCombatWatchState(BotProfileId, FirstSeenAtUtc, now, ReleaseEligibleAtUtc, ReleaseActiveUntilUtc, ObservedCount + 1, immediateBlocked ? ImmediateBlockedCount + 1 : ImmediateBlockedCount);
        }

        public IsolatedCombatWatchState WithRelease(DateTimeOffset releaseUntilUtc)
        {
            return new IsolatedCombatWatchState(BotProfileId, FirstSeenAtUtc, LastSeenAtUtc, ReleaseEligibleAtUtc, releaseUntilUtc, ObservedCount, ImmediateBlockedCount);
        }
    }

    private sealed class PendingReturnState
    {
        public string PendingId = "none";
        public string OperatorId = "none";
        public string BotProfileId = "none";
        public string ContractKey = "none";
        public string RequestKind = "none";
        public string MoveOwnerAtStart = "none";
        public Vector3 Anchor;
        public string AnchorReason = "none";
        public string AnchorScoreSummary = "none";
        public string PathValidationSummary = "none";
        public float PathDistanceMeters;
        public string ExternalPreemptOutcome = "none";
        public string SchedulerWindowId = "none";
        public DateTimeOffset PreemptedAtUtc;
        public DateTimeOffset CommandAfterUtc;
        public DateTimeOffset ExpiresAtUtc;

        public string Summary => "pending=" + Safe(PendingId)
            + ";operator=" + Safe(OperatorId)
            + ";botProfile=" + Safe(BotProfileId)
            + ";contract=" + Safe(ContractKey)
            + ";request=" + Safe(RequestKind)
            + ";moveOwner0=" + Safe(MoveOwnerAtStart)
            + ";anchor=" + Anchor.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + Anchor.y.ToString("0.0", CultureInfo.InvariantCulture) + "," + Anchor.z.ToString("0.0", CultureInfo.InvariantCulture)
            + ";anchorReason=" + Safe(AnchorReason)
            + ";anchorScore=" + Safe(AnchorScoreSummary)
            + ";path=" + Safe(PathValidationSummary)
            + ";pathDist=" + PathDistanceMeters.ToString("0.00", CultureInfo.InvariantCulture)
            + ";external=" + Safe(ExternalPreemptOutcome)
            + ";schedulerWindow=" + Safe(SchedulerWindowId);
    }

    private sealed class RawRallyCandidate
    {
        public RawRallyCandidate(Vector3 anchor, float radius, int radiusIndex, int directionIndex, float ownerDistanceMeters, float preliminaryScore)
        {
            Anchor = anchor;
            Radius = radius;
            RadiusIndex = radiusIndex;
            DirectionIndex = directionIndex;
            OwnerDistanceMeters = ownerDistanceMeters;
            PreliminaryScore = preliminaryScore;
        }

        public Vector3 Anchor { get; }
        public float Radius { get; }
        public int RadiusIndex { get; }
        public int DirectionIndex { get; }
        public float OwnerDistanceMeters { get; }
        public float PreliminaryScore { get; }
    }

    private sealed class PhysicalRecoveryState
    {
        public int FailureCount;
        public Vector3 FailedAnchor;
        public Vector3 LastWorldPosition;
        public DateTimeOffset LastFailureUtc;
        public DateTimeOffset UntilUtc;
        public string LastReason = "none";
        public string FailedPathSignature = "none";
    }

    private sealed class HardReturnLeaseState
    {
        public string LeaseId = "none";
        public string OperatorId = "none";
        public string BotProfileId = "none";
        public string ContractKey = "none";
        public string RequestKind = "none";
        public string MoveOwnerAtStart = "none";
        public Vector3 Anchor;
        public float AnchorRadiusMeters;
        public float ActionRallyClearMeters;
        public float ActionRallyAcceptMeters;
        public float CompletionMeters;
        public float NoProgressSeconds;
        public int MaxReanchorsPerLease;
        public DateTimeOffset StartedAtUtc;
        public DateTimeOffset MinUntilUtc;
        public DateTimeOffset MaxUntilUtc;
        public DateTimeOffset HardMaxUntilUtc;
        public DateTimeOffset NoProgressUntilUtc;
        public DateTimeOffset LastProgressAtUtc;
        public DateTimeOffset NextAuthorityRefreshAtUtc;
        public DateTimeOffset NextReanchorAllowedAtUtc;
        public Vector3 OwnerPositionAtAnchor;
        public float InitialAnchorDistance;
        public float LastAnchorDistance;
        public float InitialBubbleDistance;
        public float LastBubbleDistance;
        public float? LastDestinationDistance;
        public string CommandResult = "none";
        public string ExternalPreemptOutcome = "none";
        public string SchedulerWindowId = "none";
        public string PathValidationSummary = "none";
        public string AnchorScoreSummary = "none";
        public float PathDistanceMeters;
        public float PendingElapsedSeconds;
        public int ReanchorCount;
        public string LastProgressKind = "none";
        public Vector3 LastWorldPosition;
        public DateTimeOffset LastWorldSampleAtUtc;
        public DateTimeOffset PhysicalBlockedSinceUtc;
        public int PhysicalRestartCount;
        public bool Sprint;
        public bool PathSafeFallback;

        public string Summary => "lease=" + Safe(LeaseId)
            + ";operator=" + Safe(OperatorId)
            + ";botProfile=" + Safe(BotProfileId)
            + ";contract=" + Safe(ContractKey)
            + ";request=" + Safe(RequestKind)
            + ";moveOwner0=" + Safe(MoveOwnerAtStart)
            + ";anchor=" + Anchor.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + Anchor.y.ToString("0.0", CultureInfo.InvariantCulture) + "," + Anchor.z.ToString("0.0", CultureInfo.InvariantCulture)
            + ";radius=" + AnchorRadiusMeters.ToString("0.00", CultureInfo.InvariantCulture)
            + ";clear=" + ActionRallyClearMeters.ToString("0.00", CultureInfo.InvariantCulture)
            + ";accept=" + ActionRallyAcceptMeters.ToString("0.00", CultureInfo.InvariantCulture)
            + ";completion=" + CompletionMeters.ToString("0.00", CultureInfo.InvariantCulture)
            + ";noProgress=" + NoProgressSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + ";maxReanchors=" + MaxReanchorsPerLease.ToString(CultureInfo.InvariantCulture)
            + ";anchorDist0=" + InitialAnchorDistance.ToString("0.00", CultureInfo.InvariantCulture)
            + ";anchorDistLast=" + LastAnchorDistance.ToString("0.00", CultureInfo.InvariantCulture)
            + ";bubble0=" + InitialBubbleDistance.ToString("0.00", CultureInfo.InvariantCulture)
            + ";bubbleLast=" + LastBubbleDistance.ToString("0.00", CultureInfo.InvariantCulture)
            + ";external=" + Safe(ExternalPreemptOutcome)
            + ";schedulerWindow=" + Safe(SchedulerWindowId)
            + ";path=" + Safe(PathValidationSummary)
            + ";anchorScore=" + Safe(AnchorScoreSummary)
            + ";pathDist=" + PathDistanceMeters.ToString("0.00", CultureInfo.InvariantCulture)
            + ";pendingElapsed=" + PendingElapsedSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + ";reanchors=" + ReanchorCount.ToString(CultureInfo.InvariantCulture)
            + ";physicalRestarts=" + PhysicalRestartCount.ToString(CultureInfo.InvariantCulture)
            + ";sprint=" + Bool(Sprint)
            + ";pathSafeFallback=" + Bool(PathSafeFallback)
            + ";command=" + Safe(CommandResult);
    }
}
#endif
