#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.External;
using Vanguard.Client.Runtime.Movement.Brain;

// Responsibility: maintains one monotonic owner-travel lease per Operator along the owner corridor.
// Flow: Owner motion and Operator separation select a forward corridor anchor; the executor validates and acquires a travel lease, drives toward that anchor, retargets inside the same generation as the owner moves, and ends the lease once useful cohesion is restored.
// Authority boundary: distance bands tune pace inside the same lease; combat/medical interruptions remain external authorities and are observed through heartbeats.
// Invariant: valid travel retargets must preserve command generation and route continuity rather than restart the movement backend.

namespace Vanguard.Client.Runtime.Movement;

/// <summary>
/// The runtime canonical movement authority for owner travel. Each Operator keeps one lease while the
/// owner is moving and advances monotonically along the owner's NavMesh breadcrumb corridor.
/// FormationTravel, CatchUp and EmergencyCatchUp are modes of that lease, not competing executors.
/// TacticalVolumeJoin remains a bounded topology repair for stationary/split-volume cases.
/// The runtime keeps one locomotion episode across every travel distance band: mode changes affect
/// pace only, route retargets preserve command generation, and valid Travel never resets the backend.
/// </summary>
internal static class VanguardSquadTravelCohesionExecutor
{
    public const string StatusTag = "VANGUARD_SQUAD_TRAVEL_COMBAT_AUTHORITY_STATUS";
    public const string OrbitQuiesceStatusTag = "VANGUARD_ORBIT_AUTHORITY_QUIESCE_STATUS";
    public const string PhysicalLivenessStatusTag = "VANGUARD_TRAVEL_PHYSICAL_LIVENESS_STATUS";
    public const string RecoveryTruthStatusTag = "VANGUARD_TRAVEL_RECOVERY_TRUTH_STATUS";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, TravelLeaseState> ActiveByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> CooldownByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, TravelPhysicalFailureMemory> PhysicalFailureByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    // Execution-only heartbeat. Unlike route cursor projection, this is never touched by scoring or
    // target probing, so it can prove a real combat/medical/other-authority interruption.
    private static readonly Dictionary<string, DateTimeOffset> LastTravelAuthorityHeartbeatByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(0.45d);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(1.75d);
    private static readonly TimeSpan ProgressLogInterval = TimeSpan.FromSeconds(4.0d);
    private static readonly TimeSpan ChurnDiagnosticLogInterval = TimeSpan.FromSeconds(8.0d);
    private static DateTimeOffset nextTickAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    public static bool HasActiveTravelAuthority(string botProfileId)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return false;
        }

        lock (Sync)
        {
            return ActiveByBotProfileId.ContainsKey(botProfileId);
        }
    }

    public static bool ShouldOwnTravelRecovery(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            reason = "snapshot_or_bot_missing";
            return false;
        }

        if (HasActiveTravelAuthority(snapshot.BotProfileId))
        {
            reason = "active_monotonic_travel_lease";
            return true;
        }

        lock (Sync)
        {
            if (CooldownByBotProfileId.TryGetValue(snapshot.BotProfileId, out var cooldownUntil) && cooldownUntil > now)
            {
                reason = "travel_route_failure_cooldown";
                return false;
            }
        }

        if (!IsSupportedContract(snapshot) || IsVolumeJoinRequest(snapshot))
        {
            reason = "request_not_route_travel";
            return false;
        }

        if (!VanguardSquadTravelRouteMemory.IsRouteUsable(snapshot, now, out var routeReason))
        {
            reason = routeReason;
            return false;
        }

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot)
            || VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(snapshot)
            || snapshot.Looting.BotLooting == true
            || snapshot.Looting.LootTaskRunning == true)
        {
            reason = "critical_interrupt_owns_authority";
            return false;
        }

        reason = "fresh_monotonic_route_available:" + routeReason;
        return true;
    }

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            ActiveByBotProfileId.Clear();
            CooldownByBotProfileId.Clear();
            PhysicalFailureByBotProfileId.Clear();
            LastTravelAuthorityHeartbeatByBotProfileId.Clear();
            LastLogByKey.Clear();
        }

        bootLogged = false;
        VanguardSquadTravelRouteMemory.ResetForRaidLifecycle(reason);
        VanguardSquadTravelCohesionAuthority.ResetForRaidLifecycle(reason);
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_TRAVEL_COHESION_RESET reason={Safe(reason)}; active=0; cooldowns=cleared; physicalFailureMemory=cleared; travelAuthorityHeartbeats=cleared; doctrine=travel_follow_through_and_same_volume_join_no_slot_churn; tag={StatusTag}; physicalTag={PhysicalLivenessStatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
    }

    public static void Tick()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        VanguardSquadTravelCohesionAuthority.Tick(now);
        if (now < nextTickAtUtc)
        {
            return;
        }

        nextTickAtUtc = now + TickInterval;
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_TRAVEL_COHESION_BOOT enabled={Bool(VanguardMovementAuthorityDoctrine.TacticalRepositionActiveEnabled)}; scope=monotonic_travel_corridor_same_volume_join; noSlotRedistribution=true; routeMemory=append_only_breadcrumbs; sprint=mode_hysteresis; catchUpEnter={VanguardMovementAuthorityDoctrine.TravelCatchUpEnterMeters:0.0}; catchUpExit={VanguardMovementAuthorityDoctrine.TravelCatchUpExitMeters:0.0}; modeDwell={VanguardMovementAuthorityDoctrine.TravelModeDwellSeconds:0.00}; emergencyEnter={VanguardMovementAuthorityDoctrine.HardCorrectionMeters:0.0}; emergencyExit={VanguardMovementAuthorityDoctrine.SoftCorrectionMeters:0.0}; travelStart={VanguardMovementAuthorityDoctrine.TravelCohesionStartMeters:0.0}; postReturnHold={VanguardMovementAuthorityDoctrine.TravelCohesionPostReturnHoldSeconds:0.0}; travelTarget={VanguardMovementAuthorityDoctrine.TravelCohesionTargetMeters:0.0}; volumeTarget={VanguardMovementAuthorityDoctrine.TacticalVolumeJoinTargetMeters:0.0}; maxTravel={VanguardMovementAuthorityDoctrine.TravelCohesionMaxDurationSeconds:0.0}; maxVolume={VanguardMovementAuthorityDoctrine.TacticalVolumeJoinMaxDurationSeconds:0.0}; physicalSample={VanguardMovementAuthorityDoctrine.TravelPhysicalSampleSeconds:0.00}; physicalMeaningful={VanguardMovementAuthorityDoctrine.TravelPhysicalMeaningfulDisplacementMeters:0.00}; blockedDetect={VanguardMovementAuthorityDoctrine.TravelPhysicalBlockedDetectSeconds:0.00}; continuousBlockedConfirm={VanguardContinuousCohesionLocomotionPolicy.ContinuousBlockedConfirmationSeconds:0.00}; continuousNoProgress={VanguardContinuousCohesionLocomotionPolicy.ContinuousNoProgressSeconds:0.00}; retargetLead={VanguardMovementAuthorityDoctrine.TravelRetargetLeadDistanceMeters:0.0}; schedulerHeartbeat={VanguardMovementAuthorityDoctrine.TravelSchedulerHeartbeatSeconds:0.00}; schedulerHeartbeatTimeout={VanguardMovementAuthorityDoctrine.TravelSchedulerHeartbeatTimeoutSeconds:0.00}; recentReacquireAuthorityGap={VanguardMovementAuthorityDoctrine.TravelRecentReacquireMinimumPauseSeconds:0.00}; postInterruptionReconcileGap={VanguardMovementAuthorityDoctrine.TravelPostInterruptionReconcileMinimumPauseSeconds:0.00}; postInterruptionMaxOwnerDistance={VanguardMovementAuthorityDoctrine.TravelPostInterruptionReconcileMaximumOwnerDistanceMeters:0.0}; postInterruptionMinDebt={VanguardMovementAuthorityDoctrine.TravelPostInterruptionReconcileMinimumDebtMeters:0.0}; excludes=productiveDirectThreat_stationaryMedical_criticalLoot; continuousBackend=direct_GoToPoint_slowAtEnd_false; travelRestart=forbidden; hardReturn=fallback_only_when_route_unavailable; Tag={VanguardMovementAuthorityDoctrine.CombatCohesionAuthorityStatusTag}; physicalTag={PhysicalLivenessStatusTag}; tag={StatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}; build={VanguardBuildVersion.BuildLabel}");
        }

        var snapshots = VanguardOperatorDecisionSnapshotService.GetLatestSnapshots();
        VanguardSquadTravelRouteMemory.Update(snapshots, now);
        TickActiveLeases(snapshots, now);
        if (!VanguardMovementAuthorityDoctrine.TacticalRepositionActiveEnabled)
        {
            return;
        }

        // Runtime invariant: owner travel admission is latency-sensitive and may not sit behind the
        // optional planning budget.  Expensive path work remains bounded per owner and per frame
        // inside TryStartLeases, but every eligible Operator is reconsidered on each travel tick.
        TryStartLeases(snapshots, now);
    }

    private static void TryStartLeases(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        if (snapshots == null || snapshots.Count == 0)
        {
            return;
        }

        int activeCount;
        lock (Sync)
        {
            activeCount = ActiveByBotProfileId.Count;
        }

        int maxNewStarts = Math.Max(0, snapshots.Count - activeCount);
        if (maxNewStarts <= 0)
        {
            return;
        }

        foreach (var snapshot in snapshots.OrderByDescending(ScoreStartCandidate))
        {
            if (maxNewStarts <= 0)
            {
                return;
            }

            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId) || !IsSupportedContract(snapshot))
            {
                continue;
            }

            DateTimeOffset cooldownUntil;
            lock (Sync)
            {
                if (ActiveByBotProfileId.ContainsKey(snapshot.BotProfileId))
                {
                    continue;
                }

                if (CooldownByBotProfileId.TryGetValue(snapshot.BotProfileId, out cooldownUntil) && cooldownUntil > now)
                {
                    if (!ShouldBypassCooldownForOrbitQuiesce(snapshot, now, out var bypassReason))
                    {
                        LogThrottled("cooldown|" + snapshot.BotProfileId, now,
                            $"VANGUARD_TRAVEL_COHESION_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; request={Safe(snapshot.MovementAuthority.BrokerPlan.Contract.RequestKind)}; reason=cooldown; remaining={(cooldownUntil - now).TotalSeconds:0.0}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; tag={StatusTag}; orbitQuiesceTag={OrbitQuiesceStatusTag}");
                        continue;
                    }

                    LogThrottled("cooldownBypass|" + snapshot.BotProfileId, now,
                        $"VANGUARD_ORBIT_AUTHORITY_COOLDOWN_BYPASS operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; request={Safe(snapshot.MovementAuthority.BrokerPlan.Contract.RequestKind)}; reason={Safe(bypassReason)}; remaining={(cooldownUntil - now).TotalSeconds:0.0}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; orbit={Bool(snapshot.Orbit.Active)}; path={Bool(snapshot.Movement.HasPath == true)}; tag={OrbitQuiesceStatusTag}; travelTag={StatusTag}");
                }
            }

            string gate = CheckStartGate(snapshot, now);
            if (!string.Equals(gate, "none", StringComparison.OrdinalIgnoreCase))
            {
                LogThrottled("gate|" + snapshot.BotProfileId + "|" + gate, now,
                    $"VANGUARD_TRAVEL_COHESION_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; request={Safe(snapshot.MovementAuthority.BrokerPlan.Contract.RequestKind)}; reason={Safe(gate)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; path={snapshot.SquadCohesion.OwnerToOperatorPathDistance:0.0}; ratio={snapshot.SquadCohesion.OwnerToOperatorPathRatio:0.00}; vertical={snapshot.SquadCohesion.VerticalDelta:0.0}; env={Safe(snapshot.SquadCohesion.TacticalEnvironmentKind)}; tag={StatusTag}");
                continue;
            }

            if (VanguardMainIntentScheduler.HasBlockingPrimaryWindowForTravel(snapshot.BotProfileId, now, out var blockingReason))
            {
                LogThrottled("primary|" + snapshot.BotProfileId + "|" + blockingReason, now,
                    $"VANGUARD_TRAVEL_COHESION_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=primary_window_busy:{Safe(blockingReason)}; tag={StatusTag}");
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record) || record.BotOwner == null || record.BotOwner.IsDead)
            {
                LogThrottled("botowner|" + snapshot.BotProfileId, now,
                    $"VANGUARD_TRAVEL_COHESION_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=bot_owner_missing_or_dead; tag={StatusTag}");
                continue;
            }

            Vector3 botPosition = ResolveBotPosition(record.BotOwner);
            bool volumeJoin = IsVolumeJoinRequest(snapshot);
            if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner(
                    "CohesionNavMeshPath",
                    snapshot.OwnerProfileId,
                    1,
                    VanguardContinuousCohesionLocomotionPolicy.CohesionNavMeshPathsPerFrame,
                    out var travelBudgetReason))
            {
                // Per-Operator deferral only.  Never serialize the rest of the squad behind one
                // denied path budget as the runtime did through an early return.
                LogThrottled("travelAdmissionBudget|" + snapshot.BotProfileId, now,
                    $"VANGUARD_TRAVEL_ADMISSION_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(travelBudgetReason)}; continueSquadLoop=true; noCooldown=true; tag=VANGUARD_TRAVEL_RESPONSIVENESS_STATUS");
                continue;
            }

            if (!TryResolveAnchor(snapshot, botPosition, volumeJoin, now, out var plan))
            {
                bool retryableReconciliationDeferral = plan.Reason.StartsWith(
                        "post_interruption_reconciliation_path_deferred",
                        StringComparison.OrdinalIgnoreCase)
                    || plan.Reason.StartsWith(
                        "post_interruption_reconciliation_candidate_deferred",
                        StringComparison.OrdinalIgnoreCase);
                if (!retryableReconciliationDeferral)
                {
                    SetCooldown(snapshot.BotProfileId, now, FailureCooldownSeconds(volumeJoin));
                }

                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"VANGUARD_TRAVEL_COHESION_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; request={Safe(snapshot.MovementAuthority.BrokerPlan.Contract.RequestKind)}; reason=anchor_failed:{Safe(plan.Reason)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; env={Safe(snapshot.SquadCohesion.TacticalEnvironmentKind)}; topology={Safe(snapshot.SquadCohesion.SectorTopologyReason)}; retryable={Bool(retryableReconciliationDeferral)}; cooldownApplied={Bool(!retryableReconciliationDeferral)}; summary={Safe(plan.Summary)}; tag={StatusTag}");
                continue;
            }

            if (!VanguardMainIntentScheduler.TryOpenTravelCorridor(snapshot, now, out var windowId, out var openReason))
            {
                LogThrottled("open|" + snapshot.BotProfileId + "|" + openReason, now,
                    $"VANGUARD_TRAVEL_COHESION_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=scheduler_denied:{Safe(openReason)}; plan={Safe(plan.Summary)}; tag={StatusTag}");
                continue;
            }

            if (NeedsExternalPreempt(snapshot))
            {
                var preempt = VanguardExternalAuthorityAdapter.RequestOrbitAuthorityQuiesce(
                    record.BotOwner,
                    snapshot,
                    volumeJoin ? "tactical_volume_join" : "travel_cohesion_follow_through",
                    TimeSpan.FromSeconds(MaxDurationSeconds(volumeJoin) + VanguardMovementAuthorityDoctrine.OrbitQuiesceRefreshSeconds + 3.0f),
                    now);
                string softDriveReason = string.Empty;
                if (!preempt.CanDriveMovement && !CanSoftDriveAfterNonCriticalPreempt(snapshot, preempt, volumeJoin, out softDriveReason))
                {
                    SetCooldown(snapshot.BotProfileId, now, FailureCooldownSeconds(volumeJoin));
                    VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "external_preempt_not_granted:" + preempt.Outcome, preempt.Summary, windowId);
                    VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
                        $"VANGUARD_TRAVEL_COHESION_ABORTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; request={Safe(snapshot.MovementAuthority.BrokerPlan.Contract.RequestKind)}; reason=external_preempt_not_granted; outcome={preempt.Outcome}; fullPreemptPayload=false; fullPlanPayload=false; tag={StatusTag}");
                    VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                        $"VANGUARD_TRAVEL_COHESION_ABORTED_TRACE botProfile={Safe(snapshot.BotProfileId)}; preempt={Safe(preempt.Summary)}; plan={Safe(plan.Summary)}; tag={StatusTag}");
                    continue;
                }

                if (!preempt.CanDriveMovement)
                {
                    VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
                        $"VANGUARD_TRAVEL_COHESION_SOFT_PREEMPT_GRANTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; request={Safe(snapshot.MovementAuthority.BrokerPlan.Contract.RequestKind)}; reason={Safe(softDriveReason)}; outcome={preempt.Outcome}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; fullPreemptPayload=false; fullPlanPayload=false; tag={StatusTag}");
                    VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                        $"VANGUARD_TRAVEL_COHESION_SOFT_PREEMPT_GRANTED_TRACE botProfile={Safe(snapshot.BotProfileId)}; preempt={Safe(preempt.Summary)}; plan={Safe(plan.Summary)}; tag={StatusTag}");
                }
            }

            // Keep the current claim intact until the replacement command has been issued and
            // its exact identity has been confirmed. This makes the handoff transactional: a
            // rejected Travel command cannot leave the Operator without its previous movement.
            string claimYieldSummary = "claim_not_yet_yielded";

            string leaseId = (volumeJoin ? "volume_join_" : "travel_corridor_") + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId);
            DateTimeOffset maxUntil = now + TimeSpan.FromSeconds(MaxDurationSeconds(volumeJoin));
            float anchorRadius = volumeJoin
                ? VanguardMovementAuthorityDoctrine.TacticalVolumeJoinAnchorRadiusMeters
                : plan.RouteTarget.AnchorRadiusMeters;
            bool sprint = !volumeJoin
                && !string.Equals(plan.RouteTarget.Mode, VanguardTravelRouteModes.FormationTravel, StringComparison.OrdinalIgnoreCase);
            bool issued = VanguardReturnMovementCommandStore.Issue(
                leaseId,
                snapshot.OperatorId,
                snapshot.BotProfileId,
                plan.Anchor,
                anchorRadius,
                sprint: sprint,
                now,
                maxUntil,
                volumeJoin ? VanguardMovementContractPolicy.TacticalVolumeJoin : VanguardMovementContractPolicy.TravelCohesionFollowThrough,
                plan.PathSummary,
                plan.BotPathDistance,
                out var commandResult);
            if (!issued)
            {
                SetCooldown(snapshot.BotProfileId, now, FailureCooldownSeconds(volumeJoin));
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "move_bridge_rejected:" + commandResult, plan.Summary, windowId);
                VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
                    $"VANGUARD_TRAVEL_COHESION_ABORTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; request={Safe(snapshot.MovementAuthority.BrokerPlan.Contract.RequestKind)}; reason=move_bridge_rejected:{Safe(commandResult)}; fullPlanPayload=false; tag={StatusTag}");
                VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                    $"VANGUARD_TRAVEL_COHESION_ABORTED_TRACE botProfile={Safe(snapshot.BotProfileId)}; plan={Safe(plan.Summary)}; tag={StatusTag}");
                continue;
            }

            string requestKind = volumeJoin
                ? VanguardMovementContractPolicy.TacticalVolumeJoin
                : VanguardMovementContractPolicy.TravelCohesionFollowThrough;
            if (!VanguardReturnMovementCommandStore.TryGetActive(snapshot.BotProfileId, now, out var ownedCommand)
                || !string.Equals(ownedCommand.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ownedCommand.RequestKind, requestKind, StringComparison.OrdinalIgnoreCase))
            {
                VanguardReturnMovementCommandStore.ClearOwned(snapshot.BotProfileId, leaseId, now, "travel_command_identity_not_confirmed");
                SetCooldown(snapshot.BotProfileId, now, FailureCooldownSeconds(volumeJoin));
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "move_bridge_identity_not_confirmed", plan.Summary, windowId);
                VanguardClientDiagnosticsLog.Warning(StatusTag,
                    $"VANGUARD_TRAVEL_COMMAND_IDENTITY_REJECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(leaseId)}; request={Safe(requestKind)}; commandResult={Safe(commandResult)}; doctrine=movement_lease_requires_exact_owned_command_generation; tag={VanguardMedicalCohesionStatusTags.MovementLeaseIdentity}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
                continue;
            }

            if (!VanguardReturnMovementCommandStore.TryGetExactOwned(
                    snapshot.BotProfileId,
                    leaseId,
                    requestKind,
                    ownedCommand.Generation,
                    now,
                    out ownedCommand,
                    out var exactCommandReason))
            {
                VanguardReturnMovementCommandStore.ClearOwned(snapshot.BotProfileId, leaseId, now, "travel_exact_command_identity_not_confirmed");
                SetCooldown(snapshot.BotProfileId, now, FailureCooldownSeconds(volumeJoin));
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "move_bridge_exact_identity_not_confirmed:" + exactCommandReason, plan.Summary, windowId);
                VanguardClientDiagnosticsLog.Warning(StatusTag,
                    $"VANGUARD_TRAVEL_COMMAND_IDENTITY_REJECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(leaseId)}; request={Safe(requestKind)}; generation={ownedCommand.Generation}; commandResult={Safe(commandResult)}; reason={Safe(exactCommandReason)}; commandCleared=true; doctrine=movement_lease_requires_exact_owned_command_generation; tag={VanguardMedicalCohesionStatusTags.MovementLeaseIdentity}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
                continue;
            }

            bool recentReacquireCommand = !volumeJoin
                && plan.RouteTarget.Reason.StartsWith("accepted_recent_corridor_reacquire", StringComparison.OrdinalIgnoreCase);
            bool postInterruptionReconciliationCommand = !volumeJoin
                && plan.RouteTarget.Reason.StartsWith("accepted_post_interruption_corridor_reconciliation", StringComparison.OrdinalIgnoreCase);
            bool cursorCommitAccepted = true;
            string cursorCommitReason = "not_required";
            string cursorCommitKind = "none";
            if (postInterruptionReconciliationCommand)
            {
                cursorCommitKind = "post_interruption";
                cursorCommitAccepted = VanguardSquadTravelRouteMemory.TryCommitPostInterruptionReconciliation(
                    snapshot,
                    plan.RouteTarget,
                    botPosition,
                    now,
                    out cursorCommitReason);
            }
            else if (recentReacquireCommand)
            {
                cursorCommitKind = "recent_far";
                cursorCommitAccepted = VanguardSquadTravelRouteMemory.TryCommitRecentReacquire(
                    snapshot,
                    plan.RouteTarget,
                    botPosition,
                    now,
                    out cursorCommitReason);
            }

            if (!cursorCommitAccepted)
            {
                VanguardReturnMovementCommandStore.ClearOwned(snapshot.BotProfileId, leaseId, now, "admission_cursor_reconciliation_commit_rejected");
                SetCooldown(snapshot.BotProfileId, now, FailureCooldownSeconds(volumeJoin));
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "admission_cursor_reconciliation_commit_rejected:" + cursorCommitReason, plan.Summary, windowId);
                if (postInterruptionReconciliationCommand)
                {
                    VanguardClientDiagnosticsLog.Warning(RecoveryTruthStatusTag,
                        $"VANGUARD_POST_INTERRUPTION_CORRIDOR_RECONCILIATION_REJECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(leaseId)}; target={Safe(plan.RouteTarget.Summary)}; reason={Safe(cursorCommitReason)}; commandCleared=true; cursorMutation=false; transactional=true; tag={RecoveryTruthStatusTag}; routeTag={VanguardSquadTravelRouteMemory.PostInterruptionReconciliationStatusTag}");
                }
                else
                {
                    VanguardClientDiagnosticsLog.Warning(RecoveryTruthStatusTag,
                        $"VANGUARD_RECENT_CORRIDOR_REACQUIRE_REJECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(leaseId)}; target={Safe(plan.RouteTarget.Summary)}; reason={Safe(cursorCommitReason)}; commandCleared=true; cursorMutation=false; transactional=true; tag={RecoveryTruthStatusTag}; routeTag={VanguardSquadTravelRouteMemory.ReacquireStatusTag}");
                }
                continue;
            }

            // The physical command is now the confirmed Travel generation. Retire only the
            // previous logical/static claim state. ClearOwned is generation-safe and therefore
            // cannot clear the newly confirmed Travel command.
            VanguardSquadCohesionClaimExecutor.YieldActiveClaimToOwnerTravel(
                snapshot.BotProfileId,
                now,
                volumeJoin ? "tactical_volume_join" : "monotonic_owner_travel",
                out claimYieldSummary);

            var lease = new TravelLeaseState
            {
                LeaseId = leaseId,
                WindowId = windowId,
                OperatorId = snapshot.OperatorId,
                BotProfileId = snapshot.BotProfileId,
                RequestKind = requestKind,
                CommandGeneration = ownedCommand.Generation,
                VolumeJoin = volumeJoin,
                Anchor = plan.Anchor,
                AnchorRadiusMeters = anchorRadius,
                StartedAtUtc = now,
                MinUntilUtc = now + TimeSpan.FromSeconds(volumeJoin ? 3.0d : 1.0d),
                MaxUntilUtc = maxUntil,
                NoProgressUntilUtc = now + TimeSpan.FromSeconds(NoProgressSeconds(volumeJoin)),
                LastProgressAtUtc = now,
                InitialAnchorDistance = HorizontalDistance(botPosition, plan.Anchor),
                LastAnchorDistance = HorizontalDistance(botPosition, plan.Anchor),
                InitialOwnerDistance = snapshot.SquadCohesion.OperatorDistanceToOwner,
                LastOwnerDistance = snapshot.SquadCohesion.OperatorDistanceToOwner,
                ExtremeOwnerLagSinceUtc = snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.TravelExtremeOwnerLagDistanceMeters ? now : DateTimeOffset.MinValue,
                LastMeaningfulOwnerClosingAtUtc = now,
                InitialOwnerPathDistance = snapshot.SquadCohesion.OwnerToOperatorPathDistance,
                LastOwnerPathDistance = snapshot.SquadCohesion.OwnerToOperatorPathDistance,
                ConsumedAnchorPathFailureSinceUtc = DateTimeOffset.MinValue,
                ConsumedAnchorPathFailureCount = 0,
                ConsumedAnchorStaleGenerationSinceUtc = DateTimeOffset.MinValue,
                ConsumedAnchorStaleOwnerDistanceAtStart = 0f,
                LastWorldPosition = botPosition,
                LastWorldSampleAtUtc = now,
                LastObservedBotPosition = botPosition,
                PhysicalProgressOriginPosition = botPosition,
                PhysicalProgressOriginRouteMeters = plan.RouteTarget.OperatorProgressMeters,
                PhysicalProgressOriginGoalDistance = HorizontalDistance(botPosition, plan.Anchor),
                PhysicalTravelSinceProgressMeters = 0f,
                LastPhysicalProgressAtUtc = now,
                PhysicalBlockedSinceUtc = DateTimeOffset.MinValue,
                LastLivenessObservationAtUtc = now,
                ObservedBlockedSeconds = 0f,
                ObservedNoProgressSeconds = 0f,
                PhysicalRestartCount = 0,
                ObservationApproachActive = false,
                ObservationApproachClaimId = string.Empty,
                ObservationApproachLane = string.Empty,
                NextObservationDeploymentProbeAtUtc = now,
                PathDistanceMeters = plan.BotPathDistance,
                NextExternalQuiesceAtUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.OrbitQuiesceRefreshSeconds),
                NextRetargetAllowedAtUtc = now + TimeSpan.FromSeconds(volumeJoin ? VanguardMovementAuthorityDoctrine.MovementRetargetCooldownSeconds : VanguardMovementAuthorityDoctrine.TravelRetargetCooldownSeconds),
                NextWindowRefreshAtUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TravelSchedulerHeartbeatSeconds),
                RetargetCount = 0,
                RouteEpoch = plan.RouteTarget.RouteEpoch,
                RouteVersion = plan.RouteTarget.RouteVersion,
                RouteProgressMeters = plan.RouteTarget.OperatorProgressMeters,
                TargetProgressMeters = plan.RouteTarget.TargetProgressMeters,
                TravelMode = volumeJoin ? "TacticalVolumeJoin" : plan.RouteTarget.Mode,
                OwnerMoving = plan.RouteTarget.OwnerMoving,
                OwnerStationarySeconds = plan.RouteTarget.OwnerStationarySeconds,
                PlanSummary = plan.Summary
            };

            lock (Sync)
            {
                ActiveByBotProfileId[snapshot.BotProfileId] = lease;
                LastTravelAuthorityHeartbeatByBotProfileId[snapshot.BotProfileId] = now;
            }

            VanguardMainIntentScheduler.MarkCloseCohesionStarted(snapshot.BotProfileId, leaseId, now, lease.Summary, windowId);
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_TRAVEL_COHESION_STARTED {lease.Summary}; plan={Safe(plan.Summary)}; claimYield={Safe(claimYieldSummary)}; cursorCommitKind={Safe(cursorCommitKind)}; cursorCommitReason={Safe(cursorCommitReason)}; applyOnce=true; sprint={Bool(sprint)}; noSlotRedistribution=true; mode={(volumeJoin ? "tactical_volume_join" : "travel_follow_through")}; Tag={VanguardMovementAuthorityDoctrine.CombatCohesionAuthorityStatusTag}; tag={StatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
            maxNewStarts--;
        }
    }

    private static void TickActiveLeases(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        TravelLeaseState[] active;
        lock (Sync)
        {
            active = ActiveByBotProfileId.Values.ToArray();
        }

        if (active.Length == 0)
        {
            return;
        }

        var byProfile = snapshots == null
            ? new Dictionary<string, OperatorDecisionSnapshot>(StringComparer.OrdinalIgnoreCase)
            : snapshots.Where(item => item != null && !string.IsNullOrWhiteSpace(item.BotProfileId))
                .GroupBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var lease in active)
        {
            if (!byProfile.TryGetValue(lease.BotProfileId, out var snapshot))
            {
                FinishLease(lease, now, "Interrupted", "snapshot_missing", failureCooldown: true, snapshotSignature: "missing");
                continue;
            }

            if (VanguardReturnMovementCommandStore.TryConsumePathInvalid(
                    lease.BotProfileId,
                    lease.LeaseId,
                    lease.CommandGeneration,
                    now,
                    out var pathInvalidSummary))
            {
                FinishLease(
                    lease,
                    now,
                    "Interrupted",
                    "path_invalid_feedback:" + pathInvalidSummary,
                    failureCooldown: false,
                    snapshot.DecisionSignature);
                continue;
            }

            string interrupt = CheckInterrupt(snapshot, lease, now);
            if (!string.Equals(interrupt, "none", StringComparison.OrdinalIgnoreCase))
            {
                FinishLease(
                    lease,
                    now,
                    "Interrupted",
                    interrupt,
                    failureCooldown: !IsExpectedAuthorityInterruption(interrupt),
                    snapshot.DecisionSignature);
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(lease.BotProfileId, out var record) || record.BotOwner == null || record.BotOwner.IsDead)
            {
                FinishLease(lease, now, "Interrupted", "bot_owner_missing_or_dead", failureCooldown: true, snapshot.DecisionSignature);
                continue;
            }

            var refreshedLease = RefreshOrbitAuthorityIfNeeded(lease, snapshot, record.BotOwner, now);
            if (refreshedLease.NextExternalQuiesceAtUtc != lease.NextExternalQuiesceAtUtc)
            {
                lock (Sync)
                {
                    ActiveByBotProfileId[lease.BotProfileId] = refreshedLease;
                }
            }

            Vector3 botPosition = ResolveBotPosition(record.BotOwner);
            lock (Sync)
            {
                LastTravelAuthorityHeartbeatByBotProfileId[lease.BotProfileId] = now;
            }
            float anchorDistance = HorizontalDistance(botPosition, refreshedLease.Anchor);
            float ownerDistance = snapshot.SquadCohesion.OperatorDistanceToOwner;
            float ownerPathDistance = snapshot.SquadCohesion.OwnerToOperatorPathDistance;
            var mutable = refreshedLease;
            if (!mutable.VolumeJoin)
            {
                TickMonotonicTravelLease(mutable, snapshot, botPosition, now);
                continue;
            }

            // TacticalVolumeJoin keeps its fixed topology anchor. Monotonic route travel is handled
            // above by the single persistent corridor lease.
            TimeSpan physicalSampleAge = now - mutable.LastWorldSampleAtUtc;
            var physical = VanguardMovementProgressEvaluator.EvaluatePhysical(
                mutable.LastWorldPosition,
                botPosition,
                mutable.LastAnchorDistance,
                anchorDistance,
                snapshot.RealSpeed,
                true,
                physicalSampleAge);
            if (physicalSampleAge >= TimeSpan.FromSeconds(0.45d))
            {
                mutable.LastWorldPosition = botPosition;
                mutable.LastWorldSampleAtUtc = now;
            }
            bool anchorProgress = anchorDistance < mutable.LastAnchorDistance - 0.65f;
            bool ownerProgress = ownerDistance < mutable.LastOwnerDistance - 1.00f
                || ownerDistance < mutable.InitialOwnerDistance - 2.0f;
            bool pathProgress = ownerPathDistance > 0.1f
                && mutable.LastOwnerPathDistance > 0.1f
                && ownerPathDistance < mutable.LastOwnerPathDistance - 3.0f;
            if (physical.HasProgress)
            {
                if (anchorProgress || physical.GoalGainMeters > 0f)
                {
                    mutable.LastAnchorDistance = Math.Min(mutable.LastAnchorDistance, anchorDistance);
                }

                if (ownerProgress)
                {
                    mutable.LastOwnerDistance = Math.Min(mutable.LastOwnerDistance, ownerDistance);
                }

                if (pathProgress)
                {
                    mutable.LastOwnerPathDistance = Math.Min(mutable.LastOwnerPathDistance, ownerPathDistance);
                }

                mutable.LastProgressAtUtc = now;
                mutable.NoProgressUntilUtc = now + TimeSpan.FromSeconds(NoProgressSeconds(lease.VolumeJoin));
                mutable.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
                mutable.LastWorldPosition = botPosition;
                mutable.LastWorldSampleAtUtc = now;
                lock (Sync)
                {
                    ActiveByBotProfileId[lease.BotProfileId] = mutable;
                }

                string progressKind = physical.ProgressKind;
                VanguardMainIntentScheduler.ReportPrimaryProgress(lease.BotProfileId, now, "travel_" + progressKind, mutable.Summary, lease.WindowId);
                LogThrottled("progress|" + lease.BotProfileId, now,
                    $"VANGUARD_TRAVEL_COHESION_PROGRESS {mutable.Summary}; ownerDistance={ownerDistance:0.0}; ownerPath={ownerPathDistance:0.0}; anchorDistance={anchorDistance:0.0}; progress={Safe(progressKind)}; physical={Safe(physical.Summary)}; speed={snapshot.RealSpeed:0.00}; physicalTag={VanguardPrimaryExecutionContract.PhysicalMovementProgressStatusTag}; tag={StatusTag}");
            }
            else if (physical.LocomotionBlocked)
            {
                if (mutable.PhysicalBlockedSinceUtc == DateTimeOffset.MinValue)
                {
                    mutable.PhysicalBlockedSinceUtc = now;
                }

                double blockedSeconds = Math.Max(0d, (now - mutable.PhysicalBlockedSinceUtc).TotalSeconds);
                if (blockedSeconds >= 1.0d && mutable.PhysicalRestartCount < 1)
                {
                    if (VanguardReturnMovementCommandStore.TryRestartOwned(mutable.LeaseId, mutable.BotProfileId, now, physical.Summary, out var restartResult))
                    {
                        if (!VanguardReturnMovementCommandStore.TryGetActive(mutable.BotProfileId, now, out var restartedCommand)
                            || !string.Equals(restartedCommand.LeaseId, mutable.LeaseId, StringComparison.OrdinalIgnoreCase)
                            || !string.Equals(restartedCommand.RequestKind, mutable.RequestKind, StringComparison.OrdinalIgnoreCase))
                        {
                            FinishLease(mutable, now, "Failed", "physical_restart_identity_lost", failureCooldown: true, snapshot.DecisionSignature);
                            continue;
                        }

                        mutable.PhysicalRestartCount++;
                        mutable.CommandGeneration = restartedCommand.Generation;
                        mutable.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
                        mutable.LastWorldPosition = botPosition;
                        mutable.LastWorldSampleAtUtc = now;
                        mutable.NoProgressUntilUtc = now + TimeSpan.FromSeconds(NoProgressSeconds(lease.VolumeJoin));
                        lock (Sync)
                        {
                            ActiveByBotProfileId[lease.BotProfileId] = mutable;
                        }
                        LogThrottled("physicalRestart|" + lease.BotProfileId, now,
                            $"VANGUARD_PHYSICAL_MOVEMENT_RESTART {mutable.Summary}; physical={Safe(physical.Summary)}; result={Safe(restartResult)}; tag={VanguardPrimaryExecutionContract.PhysicalMovementProgressStatusTag}");
                        continue;
                    }

                    FinishLease(mutable, now, "Failed", "physical_restart_rejected:" + restartResult, failureCooldown: true, snapshot.DecisionSignature);
                    continue;
                }

                if (blockedSeconds >= 3.0d && mutable.PhysicalRestartCount >= 1)
                {
                    FinishLease(mutable, now, "Timeout", "locomotion_blocked_world_delta_after_restart:" + physical.Summary, failureCooldown: true, snapshot.DecisionSignature);
                    continue;
                }

                lock (Sync)
                {
                    ActiveByBotProfileId[lease.BotProfileId] = mutable;
                }
            }
            else if (physicalSampleAge >= TimeSpan.FromSeconds(0.45d))
            {
                mutable.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
                lock (Sync)
                {
                    ActiveByBotProfileId[lease.BotProfileId] = mutable;
                }
            }

            if (mutable.VolumeJoin && anchorDistance <= mutable.AnchorRadiusMeters && now >= mutable.MinUntilUtc)
            {
                FinishLease(mutable, now, "Completed", "volume_anchor_reached", failureCooldown: false, snapshot.DecisionSignature);
                continue;
            }

            if (mutable.VolumeJoin && IsVolumeJoinRecovered(snapshot) && now >= mutable.MinUntilUtc)
            {
                FinishLease(mutable, now, "Completed", "same_volume_or_access_covered", failureCooldown: false, snapshot.DecisionSignature);
                continue;
            }

            if (now >= mutable.MaxUntilUtc)
            {
                FinishLease(mutable, now, "Timeout", "max_window_expired", failureCooldown: true, snapshot.DecisionSignature);
                continue;
            }

            if (now >= mutable.NoProgressUntilUtc)
            {
                FinishLease(mutable, now, "Timeout", "no_progress_timeout", failureCooldown: true, snapshot.DecisionSignature);
            }
        }
    }


    private static void TickMonotonicTravelLease(
        TravelLeaseState lease,
        OperatorDecisionSnapshot snapshot,
        Vector3 botPosition,
        DateTimeOffset now)
    {
        var mutable = lease;
        float ownerDistance = snapshot.SquadCohesion.OperatorDistanceToOwner;
        float ownerPathDistance = snapshot.SquadCohesion.OwnerToOperatorPathDistance;

        if (!VanguardSquadTravelRouteMemory.TryResolveTarget(snapshot, botPosition, now, out var routeTarget))
        {
            FinishLease(
                mutable,
                now,
                "Interrupted",
                "monotonic_route_unavailable:" + routeTarget.Reason,
                failureCooldown: true,
                snapshot.DecisionSignature);
            return;
        }

        float maxRetargetAdvance = ResolveTravelRetargetMaxAdvanceMeters(routeTarget.Mode);
        if (routeTarget.TargetProgressMeters > mutable.TargetProgressMeters + maxRetargetAdvance
            && VanguardSquadTravelRouteMemory.TryResolveBoundedTarget(
                snapshot,
                botPosition,
                now,
                mutable.TargetProgressMeters + maxRetargetAdvance,
                out var boundedRouteTarget))
        {
            LogThrottled("BoundedTarget|" + mutable.BotProfileId, now, ChurnDiagnosticLogInterval, () =>
                $"VANGUARD_TRAVEL_TARGET_BOUNDED lease={Safe(mutable.LeaseId)}; botProfile={Safe(mutable.BotProfileId)}; mode={Safe(mutable.TravelMode)}; desiredTargetProgress={routeTarget.TargetProgressMeters:0.00}; boundedTargetProgress={boundedRouteTarget.TargetProgressMeters:0.00}; maxAdvance={maxRetargetAdvance:0.00}; fullLeasePayload=false; anchorPayload=false; tag={PhysicalLivenessStatusTag}");
            routeTarget = boundedRouteTarget;
        }

        mutable.RouteProgressMeters = Math.Max(mutable.RouteProgressMeters, routeTarget.OperatorProgressMeters);
        mutable.OwnerMoving = routeTarget.OwnerMoving;
        mutable.OwnerStationarySeconds = routeTarget.OwnerStationarySeconds;

        if (now >= mutable.NextWindowRefreshAtUtc)
        {
            mutable.MaxUntilUtc = now + TimeSpan.FromSeconds(MaxDurationSeconds(volumeJoin: false));
            mutable.NextWindowRefreshAtUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TravelSchedulerHeartbeatSeconds);
            VanguardReturnMovementCommandStore.RefreshLeaseWindow(
                mutable.BotProfileId,
                mutable.MaxUntilUtc,
                "monotonic_travel_corridor");
            VanguardMainIntentScheduler.RefreshTravelCorridorWindow(
                mutable.BotProfileId,
                now,
                "corridor_window_refresh",
                mutable.Summary,
                mutable.WindowId);
        }

        float currentAnchorDistance = HorizontalDistance(botPosition, mutable.Anchor);
        float targetAnchorDelta = HorizontalDistance(mutable.Anchor, routeTarget.Anchor);
        float maxRetargetAnchorDelta = ResolveTravelRetargetMaxAnchorDeltaMeters(routeTarget.Mode);
        if (targetAnchorDelta > maxRetargetAnchorDelta)
        {
            if (VanguardSquadTravelRouteMemory.TryResolvePhysicalRecoveryTarget(snapshot, botPosition, now, out var jumpRecoveryTarget)
                && HorizontalDistance(mutable.Anchor, jumpRecoveryTarget.Anchor) <= maxRetargetAnchorDelta
                && HorizontalDistance(botPosition, jumpRecoveryTarget.Anchor) <= VanguardMovementAuthorityDoctrine.TravelPhysicalRecoveryMaxAnchorDistanceMeters)
            {
                LogThrottled("AnchorJumpBounded|" + mutable.BotProfileId, now,
                    $"VANGUARD_TRAVEL_ANCHOR_JUMP_BOUNDED {mutable.Summary}; desiredAnchor={FormatVector(routeTarget.Anchor)}; desiredDelta={targetAnchorDelta:0.00}; recoveryAnchor={FormatVector(jumpRecoveryTarget.Anchor)}; recoveryDelta={HorizontalDistance(mutable.Anchor, jumpRecoveryTarget.Anchor):0.00}; maxDelta={maxRetargetAnchorDelta:0.00}; doctrine=active_command_never_retargeted_across_unbounded_jump; tag={PhysicalLivenessStatusTag}");
                routeTarget = jumpRecoveryTarget;
                targetAnchorDelta = HorizontalDistance(mutable.Anchor, routeTarget.Anchor);
            }
            else if (TryResolveProgressiveAnchorJumpTarget(
                snapshot,
                botPosition,
                now,
                mutable,
                routeTarget,
                maxRetargetAnchorDelta,
                maxRetargetAdvance,
                out var progressiveTarget,
                out var progressiveReason))
            {
                LogThrottled("ProgressiveAnchorJump|" + mutable.BotProfileId, now, ChurnDiagnosticLogInterval,
                    $"VANGUARD_PROGRESSIVE_ANCHOR_JUMP {mutable.Summary}; desiredAnchor={FormatVector(routeTarget.Anchor)}; desiredDelta={targetAnchorDelta:0.00}; progressiveAnchor={FormatVector(progressiveTarget.Anchor)}; progressiveDelta={HorizontalDistance(mutable.Anchor, progressiveTarget.Anchor):0.00}; reason={Safe(progressiveReason)}; sameEpoch=true; freshLease=false; doctrine=large_same_corridor_jump_is_split_inside_existing_generation; tag={VanguardContinuousCohesionLocomotionPolicy.SeamlessAuthorityContinuityStatusTag}");
                routeTarget = progressiveTarget;
                targetAnchorDelta = HorizontalDistance(mutable.Anchor, routeTarget.Anchor);
            }
            else if (mutable.PartialBridgeActive || mutable.ObservationApproachActive)
            {
                // A temporary partial bridge deliberately lags the real corridor target. Do not
                // interpret that bridge-to-target span as an authority jump and destroy the lease.
                // Continue into the bounded path probe below: a complete path may reconnect to the
                // real target, another useful partial endpoint may advance the bridge, and the
                // observed liveness watchdog still remains active when neither can progress.
                LogThrottled("PartialBridgeJumpAllowed|" + mutable.BotProfileId, now, ProgressLogInterval,
                    $"VANGUARD_TRANSITIONAL_TARGET_JUMP_ALLOWED {mutable.Summary}; desiredAnchor={FormatVector(routeTarget.Anchor)}; desiredDelta={targetAnchorDelta:0.00}; maxDelta={maxRetargetAnchorDelta:0.00}; partialBridge={Bool(mutable.PartialBridgeActive)}; observationApproach={Bool(mutable.ObservationApproachActive)}; transitionalCommandPreservedUntilValidatedRetarget=true; sameLease=true; sameGeneration=true; doctrine=temporary_transition_may_probe_real_corridor_without_fresh_authority; tag={VanguardContinuousCohesionLocomotionPolicy.SeamlessAuthorityContinuityStatusTag}");
            }
            else
            {
                FinishLease(
                    mutable,
                    now,
                    "Interrupted",
                    "route_target_anchor_jump_requires_fresh_lease:delta=" + targetAnchorDelta.ToString("0.00", CultureInfo.InvariantCulture),
                    failureCooldown: false,
                    snapshot.DecisionSignature,
                    recordPostReturnHold: false);
                return;
            }
        }

        bool routeEpochChanged = routeTarget.RouteEpoch != lease.RouteEpoch;
        bool modeChanged = !string.Equals(routeTarget.Mode, mutable.TravelMode, StringComparison.OrdinalIgnoreCase);
        bool targetAdvanced = routeTarget.TargetProgressMeters >= mutable.TargetProgressMeters + 1.25f;
        bool currentAnchorConsumed = currentAnchorDistance <= mutable.AnchorRadiusMeters + 1.25f
            && routeTarget.TargetProgressMeters > mutable.TargetProgressMeters + 0.75f;

        if (modeChanged)
        {
            bool sprintForMode = !string.Equals(routeTarget.Mode, VanguardTravelRouteModes.FormationTravel, StringComparison.OrdinalIgnoreCase);
            if (VanguardReturnMovementCommandStore.TryUpdateActiveParameters(
                    mutable.LeaseId,
                    mutable.BotProfileId,
                    routeTarget.AnchorRadiusMeters,
                    sprintForMode,
                    now,
                    mutable.MaxUntilUtc,
                    "travel_mode_transition:" + mutable.TravelMode + "_to_" + routeTarget.Mode,
                    out var parameterUpdate))
            {
                mutable.AnchorRadiusMeters = routeTarget.AnchorRadiusMeters;
                mutable.TravelMode = routeTarget.Mode;
                LogThrottled("ModeParameters|" + mutable.BotProfileId + "|" + routeTarget.Mode, now, () =>
                    $"VANGUARD_TRAVEL_MODE_PARAMETERS lease={Safe(mutable.LeaseId)}; botProfile={Safe(mutable.BotProfileId)}; desiredMode={Safe(routeTarget.Mode)}; result={Safe(parameterUpdate)}; anchorPreserved=true; noPathQuery=true; noSetPointRetarget=true; fullLeasePayload=false; tag=VANGUARD_TRAVEL_RESPONSIVENESS_STATUS");
            }
        }

        bool observationApproachMustRejoin = mutable.ObservationApproachActive && routeTarget.OwnerMoving;
        bool commandLeadReady = observationApproachMustRejoin
            || routeEpochChanged
            || currentAnchorConsumed
            || currentAnchorDistance <= VanguardMovementAuthorityDoctrine.TravelRetargetLeadDistanceMeters;
        bool shouldRetarget = now >= mutable.NextRetargetAllowedAtUtc
            && (!mutable.ObservationApproachActive || routeTarget.OwnerMoving)
            && routeTarget.RequiresMovement
            && commandLeadReady
            && (observationApproachMustRejoin || routeEpochChanged || targetAdvanced || currentAnchorConsumed)
            && (observationApproachMustRejoin
                || targetAnchorDelta >= VanguardMovementAuthorityDoctrine.TravelRetargetMaterialMeters
                || routeEpochChanged);

        if (shouldRetarget
            && VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner(
                "CohesionNavMeshPath",
                snapshot.OwnerProfileId,
                1,
                VanguardContinuousCohesionLocomotionPolicy.CohesionNavMeshPathsPerFrame,
                out _))
        {
            if (TryPath(
                    botPosition,
                    routeTarget.Anchor,
                    out var routePathDistance,
                    out var routePathCorners,
                    out var routePathStatus,
                    out var partialPathEndpoint,
                    out var partialPathAvailable))
            {
                string pathSummary = "route_status=" + routePathStatus
                    + ";route_corners=" + routePathCorners.ToString(CultureInfo.InvariantCulture)
                    + ";route=" + routeTarget.Summary;
                bool sprint = !string.Equals(routeTarget.Mode, VanguardTravelRouteModes.FormationTravel, StringComparison.OrdinalIgnoreCase);
                var retargetResult = VanguardReturnMovementCommandStore.TryRetargetActive(
                    mutable.LeaseId,
                    mutable.BotProfileId,
                    routeTarget.Anchor,
                    routeTarget.AnchorRadiusMeters,
                    sprint,
                    now,
                    mutable.MaxUntilUtc,
                    pathSummary,
                    routePathDistance,
                    routeEpochChanged ? "route_epoch_changed" : currentAnchorConsumed ? "route_anchor_consumed" : "route_progress_advanced",
                    VanguardMovementAuthorityDoctrine.TravelRetargetMaterialMeters,
                    TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TravelRetargetCooldownSeconds));
                if (retargetResult.Applied)
                {
                    if (!VanguardReturnMovementCommandStore.TryGetExactOwned(
                            mutable.BotProfileId,
                            mutable.LeaseId,
                            mutable.RequestKind,
                            mutable.CommandGeneration,
                            now,
                            out var activeCommand,
                            out var identityReason))
                    {
                        FinishLease(
                            mutable,
                            now,
                            "Failed",
                            "route_retarget_identity_lost:" + identityReason,
                            failureCooldown: true,
                            snapshot.DecisionSignature);
                        return;
                    }

                    mutable.Anchor = activeCommand.Anchor;
                    mutable.AnchorRadiusMeters = activeCommand.AnchorRadiusMeters;
                    mutable.PathDistanceMeters = activeCommand.PathDistanceMeters;
                    mutable.RouteEpoch = routeTarget.RouteEpoch;
                    mutable.RouteVersion = routeTarget.RouteVersion;
                    mutable.TargetProgressMeters = Math.Max(mutable.TargetProgressMeters, routeTarget.TargetProgressMeters);
                    mutable.PartialBridgeActive = false;
                    mutable.PartialBridgeDesiredTargetProgressMeters = 0f;
                    mutable.ObservationApproachActive = false;
                    mutable.ObservationApproachClaimId = string.Empty;
                    mutable.ObservationApproachLane = string.Empty;
                    mutable.TravelMode = routeTarget.Mode;
                    mutable.InitialAnchorDistance = HorizontalDistance(botPosition, mutable.Anchor);
                    mutable.LastAnchorDistance = mutable.InitialAnchorDistance;
                    mutable.InitialOwnerDistance = ownerDistance;
                    mutable.LastOwnerDistance = ownerDistance;
                    mutable.InitialOwnerPathDistance = ownerPathDistance;
                    mutable.LastOwnerPathDistance = ownerPathDistance;
                    // Runtime invariant: target movement is not Operator movement. Keep the physical
                    // liveness origin, blocked timer and no-progress deadline intact across
                    // retargets so a moving owner cannot keep an inert command alive.
                    mutable.NextRetargetAllowedAtUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TravelRetargetCooldownSeconds);
                    mutable.RetargetCount++;
                    mutable.ConsumedAnchorPathFailureSinceUtc = DateTimeOffset.MinValue;
                    mutable.ConsumedAnchorPathFailureCount = 0;
                    mutable.PlanSummary = pathSummary + ";retarget=" + retargetResult;
                    VanguardMainIntentScheduler.ReportPrimaryProgress(
                        mutable.BotProfileId,
                        now,
                        "route_retarget",
                        mutable.Summary,
                        mutable.WindowId);
                    var retargetLogLease = mutable;
                    LogThrottled("RouteRetarget|" + mutable.BotProfileId, now,
                        () => $"VANGUARD_TRAVEL_ROUTE_RETARGET_APPLIED {retargetLogLease.Summary}; anchorDelta={targetAnchorDelta:0.0}; desiredTargetProgress={routeTarget.TargetProgressMeters:0.0}; commandedTargetProgress={retargetLogLease.TargetProgressMeters:0.0}; target={Safe(routeTarget.Summary)}; result={Safe(retargetResult.ToString())}; sameLease=true; sameGeneration=true; logicalProgressApplied=true; physicalWatchdogPreserved=true; tag={PhysicalLivenessStatusTag}");
                }
                else if (retargetResult.Outcome == VanguardMovementRetargetOutcome.ExtendedOnlyNotMaterial)
                {
                    mutable.NextRetargetAllowedAtUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TravelRetargetCooldownSeconds);
                    mutable.PlanSummary = pathSummary + ";retarget=" + retargetResult;
                    LogThrottled("RouteRetargetExtended|" + mutable.BotProfileId, now,
                        $"VANGUARD_TRAVEL_RETARGET_EXTENDED_ONLY {mutable.Summary}; desiredAnchor={FormatVector(routeTarget.Anchor)}; commandedAnchor={FormatVector(mutable.Anchor)}; desiredTargetProgress={routeTarget.TargetProgressMeters:0.0}; commandedTargetProgress={mutable.TargetProgressMeters:0.0}; desiredMode={Safe(routeTarget.Mode)}; commandedMode={Safe(mutable.TravelMode)}; result={Safe(retargetResult.ToString())}; logicalProgressApplied=false; commandExpiryExtended=true; tag={VanguardSquadTravelRouteMemory.StatusTag}");
                }
                else
                {
                    mutable.NextRetargetAllowedAtUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TravelRetargetCooldownSeconds);
                    LogThrottled("RouteRetargetRejected|" + mutable.BotProfileId + "|" + retargetResult.Outcome, now,
                        $"VANGUARD_TRAVEL_RETARGET_REJECTED {mutable.Summary}; desiredAnchor={FormatVector(routeTarget.Anchor)}; desiredTargetProgress={routeTarget.TargetProgressMeters:0.0}; commandedTargetProgress={mutable.TargetProgressMeters:0.0}; result={Safe(retargetResult.ToString())}; logicalProgressApplied=false; keepCurrentCommand=true; tag={VanguardSquadTravelRouteMemory.StatusTag}");
                }
            }
            else
            {
                mutable.NextRetargetAllowedAtUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TravelRetargetCooldownSeconds);
                bool partialBridgeApplied = false;
                if (currentAnchorConsumed
                    && partialPathAvailable
                    && TryValidatePartialPathBridge(
                        mutable,
                        botPosition,
                        routeTarget,
                        partialPathEndpoint,
                        routePathDistance,
                        out var partialBridgeReason))
                {
                    string partialPathSummary = "partial_bridge_status=" + routePathStatus
                        + ";partial_bridge_corners=" + routePathCorners.ToString(CultureInfo.InvariantCulture)
                        + ";partial_bridge_reason=" + partialBridgeReason
                        + ";desired_route=" + routeTarget.Summary;
                    bool sprint = !string.Equals(routeTarget.Mode, VanguardTravelRouteModes.FormationTravel, StringComparison.OrdinalIgnoreCase);
                    var bridgeResult = VanguardReturnMovementCommandStore.TryRetargetActive(
                        mutable.LeaseId,
                        mutable.BotProfileId,
                        partialPathEndpoint,
                        routeTarget.AnchorRadiusMeters,
                        sprint,
                        now,
                        mutable.MaxUntilUtc,
                        partialPathSummary,
                        routePathDistance,
                        "route_partial_path_bridge",
                        VanguardContinuousCohesionLocomotionPolicy.PartialPathBridgeMinimumAnchorDeltaMeters,
                        TimeSpan.FromSeconds(VanguardContinuousCohesionLocomotionPolicy.PartialPathBridgeRetrySeconds));
                    VanguardReturnMovementCommand bridgeCommand = default;
                    string bridgeIdentityReason = "retarget_not_applied";
                    if (bridgeResult.Applied
                        && VanguardReturnMovementCommandStore.TryGetExactOwned(
                            mutable.BotProfileId,
                            mutable.LeaseId,
                            mutable.RequestKind,
                            mutable.CommandGeneration,
                            now,
                            out bridgeCommand,
                            out bridgeIdentityReason))
                    {
                        mutable.Anchor = bridgeCommand.Anchor;
                        mutable.AnchorRadiusMeters = bridgeCommand.AnchorRadiusMeters;
                        mutable.PathDistanceMeters = bridgeCommand.PathDistanceMeters;
                        mutable.RouteEpoch = routeTarget.RouteEpoch;
                        mutable.RouteVersion = routeTarget.RouteVersion;
                        // A bridge is physical progress only. The logical route target is not
                        // committed until a complete path to the real corridor anchor is issued.
                        mutable.PartialBridgeActive = true;
                        mutable.ObservationApproachActive = false;
                        mutable.ObservationApproachClaimId = string.Empty;
                        mutable.ObservationApproachLane = string.Empty;
                        mutable.PartialBridgeCount++;
                        mutable.PartialBridgeDesiredTargetProgressMeters = Math.Max(
                            mutable.PartialBridgeDesiredTargetProgressMeters,
                            routeTarget.TargetProgressMeters);
                        mutable.TravelMode = routeTarget.Mode;
                        mutable.InitialAnchorDistance = HorizontalDistance(botPosition, mutable.Anchor);
                        mutable.LastAnchorDistance = mutable.InitialAnchorDistance;
                        mutable.NextRetargetAllowedAtUtc = now + TimeSpan.FromSeconds(VanguardContinuousCohesionLocomotionPolicy.PartialPathBridgeRetrySeconds);
                        mutable.RetargetCount++;
                        mutable.ConsumedAnchorPathFailureSinceUtc = DateTimeOffset.MinValue;
                        mutable.ConsumedAnchorPathFailureCount = 0;
                        mutable.PlanSummary = partialPathSummary + ";retarget=" + bridgeResult;
                        partialBridgeApplied = true;
                        VanguardMainIntentScheduler.ReportPrimaryProgress(
                            mutable.BotProfileId,
                            now,
                            "partial_path_bridge",
                            mutable.Summary,
                            mutable.WindowId);
                        VanguardClientDiagnosticsLog.Info(VanguardContinuousCohesionLocomotionPolicy.AtomicDeploymentStatusTag,
                            $"VANGUARD_PARTIAL_PATH_BRIDGE_APPLIED {mutable.Summary}; bridge={FormatVector(partialPathEndpoint)}; desiredAnchor={FormatVector(routeTarget.Anchor)}; desiredTargetProgress={routeTarget.TargetProgressMeters:0.00}; logicalProgressCommitted=false; sameLease=true; sameGeneration=true; path={Safe(routePathStatus)}; reason={Safe(partialBridgeReason)}; doctrine=partial_path_is_temporary_physical_bridge_not_route_truth; tag={VanguardContinuousCohesionLocomotionPolicy.AtomicDeploymentStatusTag}");
                    }
                    else
                    {
                        partialBridgeReason += ";retarget=" + bridgeResult + ";identity=" + bridgeIdentityReason;
                    }
                }

                if (currentAnchorConsumed && !partialBridgeApplied)
                {
                    if (mutable.ConsumedAnchorPathFailureSinceUtc == DateTimeOffset.MinValue)
                    {
                        mutable.ConsumedAnchorPathFailureSinceUtc = now;
                        mutable.ConsumedAnchorPathFailureCount = 1;
                    }
                    else
                    {
                        mutable.ConsumedAnchorPathFailureCount++;
                    }
                }
                LogThrottled("RoutePath|" + mutable.BotProfileId + "|" + routePathStatus, now, ChurnDiagnosticLogInterval,
                    $"VANGUARD_TRAVEL_ROUTE_PATH_DEFERRED {mutable.Summary}; target={Safe(routeTarget.Summary)}; pathStatus={Safe(routePathStatus)}; currentAnchorConsumed={Bool(currentAnchorConsumed)}; partialPathAvailable={Bool(partialPathAvailable)}; partialBridgeApplied={Bool(partialBridgeApplied)}; doctrine=keep_current_valid_command_or_same_generation_partial_bridge_while_route_is_unresolved; tag={VanguardSquadTravelRouteMemory.StatusTag}");
            }
        }

        // Runtime invariant: PathPartial/temporary path failure is not terminal truth. When the current anchor
        // is consumed the same lease remains alive; a useful partial bridge may advance physically,
        // otherwise the observed liveness watchdog decides only after contiguous no-progress proof.

        float anchorDistance = HorizontalDistance(botPosition, mutable.Anchor);
        bool ownerDistanceUsable = ownerDistance > 0f && ownerDistance < 10000f;
        bool consumedAnchorGenerationContradiction = !mutable.VolumeJoin
            && anchorDistance <= mutable.AnchorRadiusMeters + 0.75f
            && !routeTarget.RequiresMovement
            && ownerDistanceUsable
            && ownerDistance >= VanguardMovementAuthorityDoctrine.TravelCohesionStartMeters
            && (routeTarget.OwnerMoving
                || ownerDistance >= VanguardMovementAuthorityDoctrine.TravelCohesionForceMeters);
        if (consumedAnchorGenerationContradiction)
        {
            if (mutable.ConsumedAnchorStaleGenerationSinceUtc == DateTimeOffset.MinValue)
            {
                mutable.ConsumedAnchorStaleGenerationSinceUtc = now;
                mutable.ConsumedAnchorStaleOwnerDistanceAtStart = ownerDistance;
                LogThrottled(
                    "ConsumedAnchorContradiction|" + mutable.BotProfileId,
                    now,
                    ChurnDiagnosticLogInterval,
                    $"VANGUARD_TRAVEL_CONSUMED_ANCHOR_CONTRADICTION {mutable.Summary}; ownerDistance={ownerDistance:0.00}; anchorDistance={anchorDistance:0.00}; route={Safe(routeTarget.Summary)}; action=observe_before_exact_generation_release; releaseAfter={VanguardMovementAuthorityDoctrine.TravelConsumedAnchorStaleGenerationReleaseSeconds:0.00}; doctrine=consumed_anchor_outside_travel_envelope_cannot_reset_liveness_forever; tag=VANGUARD_RUNTIME_BOUNDARY_CONVERGENCE_STATUS");
            }
            else if ((now - mutable.ConsumedAnchorStaleGenerationSinceUtc).TotalSeconds
                >= VanguardMovementAuthorityDoctrine.TravelConsumedAnchorStaleGenerationReleaseSeconds)
            {
                float ownerDistanceDelta = ownerDistance - mutable.ConsumedAnchorStaleOwnerDistanceAtStart;
                VanguardClientDiagnosticsLog.Warning(
                    "VANGUARD_RUNTIME_BOUNDARY_CONVERGENCE_STATUS",
                    $"VANGUARD_TRAVEL_STALE_GENERATION_RELEASED {mutable.Summary}; ownerDistance={ownerDistance:0.00}; ownerDistanceAtDetection={mutable.ConsumedAnchorStaleOwnerDistanceAtStart:0.00}; ownerDistanceDelta={ownerDistanceDelta:0.00}; anchorDistance={anchorDistance:0.00}; route={Safe(routeTarget.Summary)}; cooldown=0; next=fresh_scheduler_generation; doctrine=release_exact_stale_generation_without_reusing_consumed_anchor; tag=VANGUARD_RUNTIME_BOUNDARY_CONVERGENCE_STATUS");
                FinishLease(
                    mutable,
                    now,
                    "Interrupted",
                    "consumed_anchor_stale_generation_requires_fresh_route_target",
                    failureCooldown: false,
                    snapshot.DecisionSignature,
                    recordPostReturnHold: false);
                return;
            }
        }
        else
        {
            mutable.ConsumedAnchorStaleGenerationSinceUtc = DateTimeOffset.MinValue;
            mutable.ConsumedAnchorStaleOwnerDistanceAtStart = 0f;
        }

        bool unresolvedRouteDebt = routeTarget.RequiresMovement
            && routeTarget.TargetProgressMeters > mutable.TargetProgressMeters + 0.75f;
        bool observationApproachRequiresMovement = mutable.ObservationApproachActive
            && anchorDistance > mutable.AnchorRadiusMeters + 0.35f;
        bool movementRequired = consumedAnchorGenerationContradiction
            || observationApproachRequiresMovement
            || (routeTarget.RequiresMovement
                && (anchorDistance > mutable.AnchorRadiusMeters + 0.75f || unresolvedRouteDebt));
        TimeSpan physicalSampleAge = now - mutable.LastWorldSampleAtUtc;
        bool physicalSampleReady = physicalSampleAge >= TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TravelPhysicalSampleSeconds);
        if (physicalSampleReady)
        {
            float sampleDelta = HorizontalDistance(mutable.LastWorldPosition, botPosition);
            if (sampleDelta >= VanguardMovementAuthorityDoctrine.TravelPhysicalJitterMeters)
            {
                mutable.PhysicalTravelSinceProgressMeters += sampleDelta;
            }
        }

        TimeSpan sincePhysicalProgress = now - mutable.LastPhysicalProgressAtUtc;
        var physical = VanguardMovementProgressEvaluator.EvaluateTravelPhysicalLiveness(
            mutable.PhysicalProgressOriginPosition,
            mutable.LastWorldPosition,
            botPosition,
            mutable.PhysicalTravelSinceProgressMeters,
            mutable.PhysicalProgressOriginRouteMeters,
            routeTarget.OperatorProgressMeters,
            mutable.PhysicalProgressOriginGoalDistance,
            anchorDistance,
            snapshot.RealSpeed,
            movementRequired,
            physicalSampleAge,
            sincePhysicalProgress);

        float contiguousObservedSeconds = 0f;
        bool contiguousLivenessSample = false;
        if (physicalSampleReady)
        {
            double observationGapSeconds = mutable.LastLivenessObservationAtUtc == DateTimeOffset.MinValue
                ? 0d
                : Math.Max(0d, (now - mutable.LastLivenessObservationAtUtc).TotalSeconds);
            contiguousLivenessSample = observationGapSeconds > 0d
                && observationGapSeconds <= VanguardContinuousCohesionLocomotionPolicy.LivenessMaximumContiguousSampleGapSeconds;
            contiguousObservedSeconds = contiguousLivenessSample ? (float)observationGapSeconds : 0f;
            mutable.LastLivenessObservationAtUtc = now;
            if (!contiguousLivenessSample)
            {
                // Missing observations are not evidence that the Operator remained blocked.
                mutable.ObservedBlockedSeconds = 0f;
                mutable.ObservedNoProgressSeconds = 0f;
                mutable.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
            }
        }

        if (physicalSampleReady)
        {
            mutable.LastWorldPosition = botPosition;
            mutable.LastWorldSampleAtUtc = now;
        }
        mutable.LastObservedBotPosition = botPosition;

        bool ownerClosingNow = ownerDistance < mutable.LastOwnerDistance - 1.0f;
        bool ownerProgress = ownerClosingNow
            || ownerDistance < mutable.InitialOwnerDistance - 2.0f;
        bool pathProgress = ownerPathDistance > 0.1f
            && mutable.LastOwnerPathDistance > 0.1f
            && ownerPathDistance < mutable.LastOwnerPathDistance - 3.0f;
        if (ownerDistance >= VanguardMovementAuthorityDoctrine.TravelExtremeOwnerLagDistanceMeters)
        {
            if (mutable.ExtremeOwnerLagSinceUtc == DateTimeOffset.MinValue)
            {
                mutable.ExtremeOwnerLagSinceUtc = now;
                mutable.LastMeaningfulOwnerClosingAtUtc = now;
            }
            if (ownerClosingNow || pathProgress)
            {
                mutable.LastMeaningfulOwnerClosingAtUtc = now;
            }
        }
        else
        {
            mutable.ExtremeOwnerLagSinceUtc = DateTimeOffset.MinValue;
            mutable.LastMeaningfulOwnerClosingAtUtc = now;
        }

        bool extremeLagClosingRequired = mutable.ExtremeOwnerLagSinceUtc != DateTimeOffset.MinValue
            && now - mutable.ExtremeOwnerLagSinceUtc >= TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TravelExtremeOwnerLagObservationSeconds)
            && now - mutable.LastMeaningfulOwnerClosingAtUtc >= TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TravelExtremeOwnerLagNoClosingSeconds);
        bool physicalCountsAsCorridorProgress = physical.HasProgress
            && (!extremeLagClosingRequired || ownerClosingNow || pathProgress);
        if (physicalCountsAsCorridorProgress)
        {
            mutable.LastAnchorDistance = Math.Min(mutable.LastAnchorDistance, anchorDistance);
            if (ownerProgress) mutable.LastOwnerDistance = Math.Min(mutable.LastOwnerDistance, ownerDistance);
            if (pathProgress) mutable.LastOwnerPathDistance = Math.Min(mutable.LastOwnerPathDistance, ownerPathDistance);
            mutable.LastProgressAtUtc = now;
            ResetTravelPhysicalLivenessBaseline(ref mutable, botPosition, routeTarget.OperatorProgressMeters, anchorDistance, now);
            mutable.NoProgressUntilUtc = now + TimeSpan.FromSeconds(NoProgressSeconds(volumeJoin: false));
            mutable.ObservedBlockedSeconds = 0f;
            mutable.ObservedNoProgressSeconds = 0f;
            ClearPhysicalFailureMemory(mutable.BotProfileId);
            VanguardMainIntentScheduler.ReportPrimaryProgress(
                mutable.BotProfileId,
                now,
                "travel_" + physical.ProgressKind,
                mutable.Summary,
                mutable.WindowId);
            var physicalProgressLogLease = mutable;
            LogThrottled("Progress|" + mutable.BotProfileId, now, ProgressLogInterval,
                () => $"VANGUARD_TRAVEL_PHYSICAL_PROGRESS {physicalProgressLogLease.Summary}; ownerDistance={ownerDistance:0.0}; anchorDistance={anchorDistance:0.0}; physical={Safe(physical.Summary)}; route={Safe(routeTarget.Summary)}; ownerDistanceIsTelemetryOnly=true; doctrine=real_world_and_route_progress_keeps_continuous_corridor_alive_while_owner_moves; tag={VanguardBuildVersion.TravelContinuityRepairStatusTag}; physicalTag={PhysicalLivenessStatusTag}");
        }
        else if (physical.HasProgress && extremeLagClosingRequired)
        {
            if (physicalSampleReady && contiguousLivenessSample)
            {
                mutable.ObservedNoProgressSeconds += contiguousObservedSeconds;
            }
            mutable.NextRetargetAllowedAtUtc = now;
            LogThrottled("ExtremeLagNoClosing|" + mutable.BotProfileId, now, ProgressLogInterval,
                $"VANGUARD_EXTREME_LAG_NO_CLOSING {mutable.Summary}; ownerDistance={ownerDistance:0.0}; ownerClosingNow={Bool(ownerClosingNow)}; pathProgress={Bool(pathProgress)}; physical={Safe(physical.Summary)}; observedNoProgress={mutable.ObservedNoProgressSeconds:0.00}; corridorProgressAccepted=false; retargetReleased=true; authorityMutation=false; doctrine=world_motion_does_not_refresh_extreme_catchup_without_owner_closing; tag={VanguardBuildVersion.RuntimeLivenessConvergenceStatusTag}");
        }
        else if (!movementRequired)
        {
            mutable.LastAnchorDistance = anchorDistance;
            mutable.LastOwnerDistance = ownerDistance;
            mutable.LastOwnerPathDistance = ownerPathDistance;
            ResetTravelPhysicalLivenessBaseline(ref mutable, botPosition, routeTarget.OperatorProgressMeters, anchorDistance, now);
            mutable.NoProgressUntilUtc = now + TimeSpan.FromSeconds(NoProgressSeconds(volumeJoin: false));
            mutable.ObservedBlockedSeconds = 0f;
            mutable.ObservedNoProgressSeconds = 0f;
        }
        else
        {
            if (physicalSampleReady && contiguousLivenessSample)
            {
                mutable.ObservedNoProgressSeconds += contiguousObservedSeconds;
            }

            if (physical.LocomotionBlocked)
            {
                if (mutable.PhysicalBlockedSinceUtc == DateTimeOffset.MinValue)
                {
                    mutable.PhysicalBlockedSinceUtc = now;
                }

                if (physicalSampleReady && contiguousLivenessSample)
                {
                    mutable.ObservedBlockedSeconds += contiguousObservedSeconds;
                }

                // A blocked sample may simply mean that the previous look-ahead anchor was consumed or
                // temporarily occluded by local avoidance. Release only the existing corridor retarget;
                // never stop the Mover, never increment command generation and never reset the backend.
                mutable.NextRetargetAllowedAtUtc = now;
                LogThrottled("ObservedBlocked|" + mutable.BotProfileId, now, ProgressLogInterval,
                    $"VANGUARD_OBSERVED_TRAVEL_BLOCKED {mutable.Summary}; observedBlocked={mutable.ObservedBlockedSeconds:0.00}; observedNoProgress={mutable.ObservedNoProgressSeconds:0.00}; contiguousSample={Bool(contiguousLivenessSample)}; physical={Safe(physical.Summary)}; backendReset=false; commandRestart=false; generationPreserved=true; retargetReleased=true; doctrine=missing_samples_are_not_blocked_time; tag={VanguardContinuousCohesionLocomotionPolicy.AtomicDeploymentStatusTag}; continuousTag={VanguardContinuousCohesionLocomotionPolicy.StatusTag}; physicalTag={PhysicalLivenessStatusTag}");

                if (mutable.ObservedBlockedSeconds >= VanguardContinuousCohesionLocomotionPolicy.ContinuousBlockedConfirmationSeconds)
                {
                    FinishLease(
                        mutable,
                        now,
                        "Timeout",
                        "continuous_travel_observed_blocked_confirmed_without_backend_reset:" + physical.Summary,
                        failureCooldown: true,
                        snapshot.DecisionSignature);
                    return;
                }
            }
            else
            {
                // Block confirmation must be contiguous. Intermittent local-avoidance pauses or a
                // freshly advanced corridor target cannot accumulate stale blocked time across samples.
                mutable.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
                mutable.ObservedBlockedSeconds = 0f;
            }
        }

        mutable.LastOwnerDistance = ownerDistance;
        mutable.LastOwnerPathDistance = ownerPathDistance;
        mutable.RouteProgressMeters = Math.Max(mutable.RouteProgressMeters, routeTarget.OperatorProgressMeters);
        mutable.OwnerMoving = routeTarget.OwnerMoving;
        mutable.OwnerStationarySeconds = routeTarget.OwnerStationarySeconds;
        mutable.PlanSummary = routeTarget.Summary;

        float settleDistance = Math.Max(
            VanguardMovementAuthorityDoctrine.CloseCohesionStartMinMeters,
            VanguardMovementAuthorityDoctrine.TravelCohesionTargetMeters + 2.0f);
        if (!routeTarget.OwnerMoving
            && routeTarget.OwnerStationarySeconds >= VanguardContinuousCohesionLocomotionPolicy.ObservationDeploymentCommitStillSeconds
            && ownerDistance <= settleDistance
            && now >= mutable.MinUntilUtc
            && now >= mutable.NextObservationDeploymentProbeAtUtc)
        {
            mutable.NextObservationDeploymentProbeAtUtc = now + TimeSpan.FromSeconds(0.65d);
            if (VanguardSquadCohesionClaimExecutor.TryResolveObservationDeploymentApproach(
                    snapshot,
                    botPosition,
                    now,
                    out var deploymentPlan))
            {
                if (deploymentPlan.ReadyForHandoff)
                {
                    FinishLease(
                        mutable,
                        now,
                        "Completed",
                        "owner_stationary_atomic_claim_ready:" + deploymentPlan.Summary,
                        failureCooldown: false,
                        snapshot.DecisionSignature);
                    return;
                }

                float approachDelta = HorizontalDistance(mutable.Anchor, deploymentPlan.Anchor);
                bool alreadyApproaching = approachDelta
                    < VanguardContinuousCohesionLocomotionPolicy.ObservationDeploymentApproachRetargetMeters;
                if (alreadyApproaching)
                {
                    mutable.ObservationApproachActive = true;
                    mutable.ObservationApproachClaimId = deploymentPlan.ClaimId;
                    mutable.ObservationApproachLane = deploymentPlan.Lane;
                    mutable.PlanSummary = deploymentPlan.Summary;
                }
                else
                {
                    bool approachSprint = deploymentPlan.SprintAllowed
                        && deploymentPlan.AnchorDistanceMeters
                            >= VanguardMovementAuthorityDoctrine.ClaimedCohesionAnchorSprintDistanceMeters;
                    var approachResult = VanguardReturnMovementCommandStore.TryRetargetActive(
                        mutable.LeaseId,
                        mutable.BotProfileId,
                        deploymentPlan.Anchor,
                        deploymentPlan.AnchorRadiusMeters,
                        approachSprint,
                        now,
                        mutable.MaxUntilUtc,
                        "observation_final_approach;" + deploymentPlan.PathSummary,
                        deploymentPlan.PathDistanceMeters,
                        "observation_deployment_final_approach",
                        VanguardContinuousCohesionLocomotionPolicy.ObservationDeploymentApproachRetargetMeters,
                        TimeSpan.FromSeconds(0.65d));
                    string approachIdentityReason = "retarget_not_applied";
                    VanguardReturnMovementCommand approachCommand = default;
                    if (approachResult.Applied
                        && VanguardReturnMovementCommandStore.TryGetExactOwned(
                            mutable.BotProfileId,
                            mutable.LeaseId,
                            mutable.RequestKind,
                            mutable.CommandGeneration,
                            now,
                            out approachCommand,
                            out approachIdentityReason))
                    {
                        mutable.Anchor = approachCommand.Anchor;
                        mutable.AnchorRadiusMeters = approachCommand.AnchorRadiusMeters;
                        mutable.PathDistanceMeters = approachCommand.PathDistanceMeters;
                        mutable.InitialAnchorDistance = HorizontalDistance(botPosition, mutable.Anchor);
                        mutable.LastAnchorDistance = mutable.InitialAnchorDistance;
                        mutable.ObservationApproachActive = true;
                        mutable.ObservationApproachClaimId = deploymentPlan.ClaimId;
                        mutable.ObservationApproachLane = deploymentPlan.Lane;
                        mutable.PartialBridgeActive = false;
                        mutable.PartialBridgeDesiredTargetProgressMeters = 0f;
                        mutable.RetargetCount++;
                        mutable.NextRetargetAllowedAtUtc = now + TimeSpan.FromSeconds(0.65d);
                        mutable.PlanSummary = deploymentPlan.Summary + ";retarget=" + approachResult;
                        VanguardMainIntentScheduler.ReportPrimaryProgress(
                            mutable.BotProfileId,
                            now,
                            "observation_final_approach",
                            mutable.Summary,
                            mutable.WindowId);
                        VanguardClientDiagnosticsLog.Info(VanguardContinuousCohesionLocomotionPolicy.SeamlessAuthorityContinuityStatusTag,
                            $"VANGUARD_OBSERVATION_FINAL_APPROACH {mutable.Summary}; claim={Safe(deploymentPlan.ClaimId)}; lane={Safe(deploymentPlan.Lane)}; distance={deploymentPlan.AnchorDistanceMeters:0.00}; path={deploymentPlan.PathDistanceMeters:0.00}; sameLease=true; sameGeneration=true; sprint={Bool(approachSprint)}; logicalCorridorProgressCommitted=false; claimAuthority=false; doctrine=travel_completes_final_leg_before_atomic_handoff; tag={VanguardContinuousCohesionLocomotionPolicy.SeamlessAuthorityContinuityStatusTag}");
                    }
                    else
                    {
                        LogThrottled("ObservationApproachDeferred|" + mutable.BotProfileId, now, ProgressLogInterval,
                            $"VANGUARD_OBSERVATION_FINAL_APPROACH_DEFERRED {mutable.Summary}; claim={Safe(deploymentPlan.ClaimId)}; result={Safe(approachResult.ToString())}; identity={Safe(approachIdentityReason)}; currentTravelPreserved=true; claimAuthority=false; tag={VanguardContinuousCohesionLocomotionPolicy.SeamlessAuthorityContinuityStatusTag}");
                    }
                }
            }
            else
            {
                LogThrottled("AtomicDeploymentWarm|" + mutable.BotProfileId, now, ProgressLogInterval,
                    $"VANGUARD_ATOMIC_DEPLOYMENT_PENDING {mutable.Summary}; ownerDistance={ownerDistance:0.00}; ownerStationary={routeTarget.OwnerStationarySeconds:0.00}; readiness={Safe(deploymentPlan.Summary)}; travelCommandPreserved=true; travelWindowPreserved=true; stopIssued=false; resetPath=false; doctrine=prepare_stationary_claim_and_complete_final_approach_before_releasing_continuous_pursuit; tag={VanguardContinuousCohesionLocomotionPolicy.SeamlessAuthorityContinuityStatusTag}");
            }
        }

        if (movementRequired
            && mutable.ObservedNoProgressSeconds >= VanguardContinuousCohesionLocomotionPolicy.ContinuousNoProgressSeconds)
        {
            FinishLease(
                mutable,
                now,
                "Timeout",
                "monotonic_route_no_meaningful_observed_physical_progress",
                failureCooldown: true,
                snapshot.DecisionSignature);
            return;
        }

        lock (Sync)
        {
            ActiveByBotProfileId[mutable.BotProfileId] = mutable;
        }
    }


    private static bool ShouldBypassCooldownForOrbitQuiesce(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (!VanguardMovementAuthorityDoctrine.ShouldQuiesceOrbitForSquadTravel(snapshot, out var quiesceReason))
        {
            reason = quiesceReason;
            return false;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.OrbitQuiesceCooldownBypassMeters)
        {
            reason = "distance_breaks_cooldown:" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", CultureInfo.InvariantCulture) + ";" + quiesceReason;
            return true;
        }

        if (VanguardMovementAuthorityDoctrine.IsOrbitObjectiveOpposingOwner(snapshot, out var alignment))
        {
            reason = "opposing_orbit_breaks_cooldown:dot=" + alignment.ToString("0.00", CultureInfo.InvariantCulture) + ";" + quiesceReason;
            return true;
        }

        reason = "quiesce_pressure_but_cooldown_kept:" + quiesceReason;
        return false;
    }

    private static TravelLeaseState RefreshOrbitAuthorityIfNeeded(TravelLeaseState lease, OperatorDecisionSnapshot snapshot, BotOwner botOwner, DateTimeOffset now)
    {
        if (now < lease.NextExternalQuiesceAtUtc)
        {
            return lease;
        }

        var mutable = lease;
        mutable.NextExternalQuiesceAtUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.OrbitQuiesceRefreshSeconds);
        if (!NeedsExternalPreempt(snapshot) || !VanguardMovementAuthorityDoctrine.ShouldQuiesceOrbitForSquadTravel(snapshot, out var quiesceReason))
        {
            return mutable;
        }

        var result = VanguardExternalAuthorityAdapter.RequestOrbitAuthorityQuiesce(
            botOwner,
            snapshot,
            "active_lease_refresh:" + lease.RequestKind + ":" + quiesceReason,
            TimeSpan.FromSeconds(Math.Max(2.0f, VanguardMovementAuthorityDoctrine.OrbitQuiesceRefreshSeconds + 1.50f)),
            now);

        LogThrottled("orbitRefresh|" + lease.BotProfileId + "|" + result.Outcome, now,
            $"VANGUARD_ORBIT_AUTHORITY_REFRESH {lease.Summary}; outcome={result.Outcome}; canDriveMovement={Bool(result.CanDriveMovement)}; reason={Safe(quiesceReason)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; orbit={Bool(snapshot.Orbit.Active)}; path={Bool(snapshot.Movement.HasPath == true)}; tag={OrbitQuiesceStatusTag}; travelTag={StatusTag}");
        return mutable;
    }

    private static bool IsSupportedContract(OperatorDecisionSnapshot snapshot)
    {
        if (!snapshot.Alive || !snapshot.MovementAuthority.BrokerPlan.LeasePlan.Eligible || !snapshot.MovementAuthority.BrokerPlan.LeasePlan.ApplyEnabled)
        {
            return false;
        }

        string request = snapshot.MovementAuthority.BrokerPlan.Contract.RequestKind;
        return request == VanguardMovementContractPolicy.TravelCohesionFollowThrough
            || request == VanguardMovementContractPolicy.TacticalVolumeJoin;
    }

    private static bool IsTravelSupersedableCommand(string? requestKind)
    {
        return string.Equals(requestKind, VanguardMovementContractPolicy.ClaimedCohesionSlot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(requestKind, VanguardMovementContractPolicy.CloseCohesionMicroAdjust, StringComparison.OrdinalIgnoreCase)
            || string.Equals(requestKind, VanguardMovementContractPolicy.TacticalRepositionToUsefulSector, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVolumeJoinRequest(OperatorDecisionSnapshot snapshot)
    {
        return snapshot != null
            && string.Equals(
                snapshot.MovementAuthority.BrokerPlan.Contract.RequestKind,
                VanguardMovementContractPolicy.TacticalVolumeJoin,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string CheckStartGate(OperatorDecisionSnapshot snapshot, DateTimeOffset now, TravelLeaseState? currentLease = null)
    {
        if (VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out var combatWindowReason))
        {
            return "sain_combat_primary_protected:" + combatWindowReason;
        }

        if (!snapshot.Alive)
        {
            return "operator_dead";
        }

        if (!snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement || !snapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            return "owner_unreliable";
        }

        bool combatProductive = VanguardMovementAuthorityDoctrine.IsCombatProductive(snapshot, out var productiveReason);
        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot) || (combatProductive && VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(snapshot)))
        {
            return "productive_combat_awareness:" + productiveReason;
        }

        if (VanguardCombatAwarenessBridge.HasFreshSquadCombatContact(snapshot, now, out var contactReason)
            && combatProductive
            && snapshot.SquadCohesion.OperatorDistanceToOwner < VanguardMovementAuthorityDoctrine.CombatCohesionForcedCatchupMeters)
        {
            return "fresh_productive_squad_contact:" + contactReason + ":" + productiveReason;
        }

        if (VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(snapshot))
        {
            return "stationary_medical_authority";
        }

        if (snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true)
        {
            return "critical_loot_active";
        }

        if (currentLease.HasValue)
        {
            if (!VanguardReturnMovementCommandStore.TryGetExactOwned(
                    snapshot.BotProfileId,
                    currentLease.Value.LeaseId,
                    currentLease.Value.RequestKind,
                    currentLease.Value.CommandGeneration,
                    now,
                    out _,
                    out var identityReason))
            {
                return "owned_movement_command_lost:" + identityReason;
            }
        }
        else if (VanguardReturnMovementCommandStore.TryGetActive(snapshot.BotProfileId, now, out var activeCommand)
            && VanguardPrimaryExecutionContract.ShouldKeepMovementContractUntilTerminal(snapshot, activeCommand.RequestKind, out var contractReason))
        {
            // Runtime invariant: a tactical claim is advisory during owner travel. The continuous travel lease
            // may atomically replace that command; the retiring claim executor will observe the
            // exact lease/generation mismatch and close without clearing the new command.
            if (!IsTravelSupersedableCommand(activeCommand.RequestKind))
            {
                return "active_movement_contract_preserved:" + contractReason;
            }
        }

        return "none";
    }

    private static string CheckInterrupt(OperatorDecisionSnapshot snapshot, TravelLeaseState lease, DateTimeOffset now)
    {
        string gate = CheckStartGate(snapshot, now, lease);
        if (!string.Equals(gate, "none", StringComparison.OrdinalIgnoreCase))
        {
            return gate;
        }

        return "none";
    }

    private static bool IsExpectedAuthorityInterruption(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        return reason.StartsWith("sain_combat_primary_protected", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("productive_combat_awareness", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("fresh_productive_squad_contact", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("stationary_medical_authority", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("critical_loot_active", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NeedsExternalPreempt(OperatorDecisionSnapshot snapshot)
    {
        return snapshot.Orbit.Active || snapshot.Movement.HasPath == true || snapshot.Looting.HasActiveLootable == true;
    }

    private static float ScoreStartCandidate(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot == null || !IsSupportedContract(snapshot))
        {
            return -1f;
        }

        float score = snapshot.SquadCohesion.OperatorDistanceToOwner;
        if (snapshot.MovementAuthority.BrokerPlan.Contract.RequestKind == VanguardMovementContractPolicy.TacticalVolumeJoin)
        {
            score += 18f + Math.Abs(snapshot.SquadCohesion.VerticalDelta) * 3f + Math.Max(0f, snapshot.SquadCohesion.OwnerToOperatorPathRatio - 1f) * 6f;
        }

        if (VanguardSquadTravelCohesionAuthority.IsPostReturnHoldActive(snapshot.BotProfileId, DateTimeOffset.UtcNow, out _)) score += 12f;
        if (snapshot.Orbit.Active) score += 6f;
        if (snapshot.Movement.HasPath == true) score += 4f;
        if (!snapshot.SquadCohesion.UsefulPosition) score += 4f;
        return score;
    }

    private static double ResolveTravelAuthorityGapSeconds(string botProfileId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return 0d;
        }

        lock (Sync)
        {
            return LastTravelAuthorityHeartbeatByBotProfileId.TryGetValue(botProfileId, out var lastAuthorityAtUtc)
                ? Math.Max(0d, (now - lastAuthorityAtUtc).TotalSeconds)
                : 0d;
        }
    }

    private static bool TryResolveAnchor(
        OperatorDecisionSnapshot snapshot,
        Vector3 botPosition,
        bool volumeJoin,
        DateTimeOffset now,
        out TravelAnchorPlan plan)
    {
        plan = TravelAnchorPlan.Invalid("not_evaluated");
        if (!snapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            plan = TravelAnchorPlan.Invalid("owner_position_missing");
            return false;
        }

        Vector3 owner = snapshot.SquadCohesion.OwnerPosition.Value;
        if (!volumeJoin)
        {
            // Recent-corridor reacquisition is admission-only and requires an execution heartbeat gap.
            // Route scoring/projection cannot create or erase that proof.
            VanguardTravelRouteTarget routeTarget = VanguardTravelRouteTarget.Invalid("travel_target_not_resolved");
            bool admissionCursorReconciliationPrepared = false;
            string admissionCursorReconciliationKind = "none";
            double travelAuthorityGapSeconds = ResolveTravelAuthorityGapSeconds(snapshot.BotProfileId, now);
            float botPathDistance = 0f;
            int botCorners = 0;
            string botPathStatus = "none";

            // Runtime invariant: first handle the inverse stale-cursor case proved by runtime qualification.
            // The Operator may already be physically near the owner after combat while the stored
            // cursor remains far behind. Candidate resolution is side-effect free; a complete path
            // and exact command identity are required before any cursor commit.
            bool postInterruptionCandidateResolved = VanguardSquadTravelRouteMemory.TryResolvePostInterruptionReconciliationTarget(
                snapshot,
                botPosition,
                now,
                travelAuthorityGapSeconds,
                out var postInterruptionTarget,
                out var postInterruptionReconciliationRequired);
            if (postInterruptionCandidateResolved)
            {
                if (TryPath(
                        botPosition,
                        postInterruptionTarget.Anchor,
                        out var reconciliationPathDistance,
                        out var reconciliationCorners,
                        out var reconciliationPathStatus))
                {
                    routeTarget = postInterruptionTarget;
                    botPathDistance = reconciliationPathDistance;
                    botCorners = reconciliationCorners;
                    botPathStatus = reconciliationPathStatus;
                    admissionCursorReconciliationPrepared = true;
                    admissionCursorReconciliationKind = "post_interruption";
                    LogThrottled("PostInterruptionPrepared|" + snapshot.BotProfileId, now,
                        $"VANGUARD_POST_INTERRUPTION_CORRIDOR_RECONCILIATION_PREPARED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; ownerDistance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; authorityGap={travelAuthorityGapSeconds:0.00}; target={Safe(postInterruptionTarget.Summary)}; path={Safe(reconciliationPathStatus)}; pathDistance={reconciliationPathDistance:0.00}; corners={reconciliationCorners}; cursorMutation=false; exactCommandRequiredBeforeCommit=true; tag={VanguardSquadTravelRouteMemory.PostInterruptionReconciliationStatusTag}");
                }
                else
                {
                    LogThrottled("PostInterruptionPath|" + snapshot.BotProfileId, now,
                        $"VANGUARD_POST_INTERRUPTION_CORRIDOR_RECONCILIATION_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; ownerDistance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; authorityGap={travelAuthorityGapSeconds:0.00}; target={Safe(postInterruptionTarget.Summary)}; path={Safe(reconciliationPathStatus)}; retryNextTravelTick=true; noCooldown=true; staleCorridorFallback=false; cursorMutation=false; tag={VanguardSquadTravelRouteMemory.PostInterruptionReconciliationStatusTag}");
                    plan = TravelAnchorPlan.Invalid("post_interruption_reconciliation_path_deferred:" + reconciliationPathStatus);
                    return false;
                }
            }
            else if (postInterruptionReconciliationRequired)
            {
                LogThrottled("PostInterruptionCandidateDeferred|" + snapshot.BotProfileId, now,
                    $"VANGUARD_POST_INTERRUPTION_CORRIDOR_RECONCILIATION_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; ownerDistance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; authorityGap={travelAuthorityGapSeconds:0.00}; reason={Safe(postInterruptionTarget.Reason)}; retryNextTravelTick=true; noCooldown=true; staleCorridorFallback=false; cursorMutation=false; tag={VanguardSquadTravelRouteMemory.PostInterruptionReconciliationStatusTag}");
                plan = TravelAnchorPlan.Invalid("post_interruption_reconciliation_candidate_deferred:" + postInterruptionTarget.Reason);
                return false;
            }

            for (int candidateIndex = 0;
                !admissionCursorReconciliationPrepared
                    && candidateIndex < VanguardMovementAuthorityDoctrine.TravelRecentReacquireCandidateCount;
                candidateIndex++)
            {
                float setback = candidateIndex * VanguardMovementAuthorityDoctrine.TravelRecentReacquireCandidateStepMeters;
                if (!VanguardSquadTravelRouteMemory.TryResolveRecentReacquireTarget(
                        snapshot,
                        now,
                        setback,
                        travelAuthorityGapSeconds,
                        out var recentTarget))
                {
                    break;
                }

                if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner(
                        "CohesionNavMeshPath",
                        snapshot.OwnerProfileId,
                        1,
                        VanguardContinuousCohesionLocomotionPolicy.CohesionNavMeshPathsPerFrame,
                        out var reacquireBudgetReason))
                {
                    LogThrottled("ReacquireBudget|" + snapshot.BotProfileId, now,
                        $"VANGUARD_RECENT_CORRIDOR_REACQUIRE_BUDGET_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; candidate={candidateIndex}; reason={Safe(reacquireBudgetReason)}; fallbackCurrentCorridor=true; cursorMutation=false; tag={VanguardSquadTravelRouteMemory.ReacquireStatusTag}; frameBudgetTag={VanguardRuntimeFrameBudgetGuard.StatusTag}");
                    break;
                }

                if (!TryPath(botPosition, recentTarget.Anchor, out var recentPathDistance, out var recentCorners, out var recentPathStatus))
                {
                    LogThrottled("ReacquirePath|" + snapshot.BotProfileId + "|" + candidateIndex.ToString(CultureInfo.InvariantCulture), now,
                        $"VANGUARD_RECENT_CORRIDOR_REACQUIRE_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; candidate={candidateIndex}; setback={setback:0.0}; target={Safe(recentTarget.Summary)}; path={Safe(recentPathStatus)}; keepMonotonicFallback=true; cursorMutation=false; tag={VanguardSquadTravelRouteMemory.ReacquireStatusTag}");
                    continue;
                }

                routeTarget = recentTarget;
                botPathDistance = recentPathDistance;
                botCorners = recentCorners;
                botPathStatus = recentPathStatus;
                admissionCursorReconciliationPrepared = true;
                admissionCursorReconciliationKind = "recent_far";
                break;
            }

            if (!admissionCursorReconciliationPrepared
                && !VanguardSquadTravelRouteMemory.TryResolveTarget(snapshot, botPosition, now, out routeTarget))
            {
                plan = TravelAnchorPlan.Invalid("route_target_failed:" + routeTarget.Reason);
                return false;
            }

            if (!admissionCursorReconciliationPrepared
                && TryGetRecentPhysicalFailure(snapshot.BotProfileId, now, out var failureMemory)
                && HorizontalDistance(routeTarget.Anchor, failureMemory.FailedAnchor) <= VanguardMovementAuthorityDoctrine.TravelPhysicalRecoverySameAnchorMeters)
            {
                if (!VanguardSquadTravelRouteMemory.TryResolvePhysicalRecoveryTarget(snapshot, botPosition, now, out var recoveryTarget))
                {
                    plan = TravelAnchorPlan.Invalid("recent_physical_failure_recovery_target_unavailable:" + recoveryTarget.Reason);
                    return false;
                }

                float recoveryAnchorDistance = HorizontalDistance(botPosition, recoveryTarget.Anchor);
                float failedAnchorDelta = HorizontalDistance(failureMemory.FailedAnchor, recoveryTarget.Anchor);
                if (recoveryAnchorDistance > VanguardMovementAuthorityDoctrine.TravelPhysicalRecoveryMaxAnchorDistanceMeters
                    || failedAnchorDelta < VanguardMovementAuthorityDoctrine.TravelPhysicalRecoverySameAnchorMeters)
                {
                    plan = TravelAnchorPlan.Invalid("recent_physical_failure_same_anchor_suppressed:recoveryDistance="
                        + recoveryAnchorDistance.ToString("0.0", CultureInfo.InvariantCulture)
                        + ":failedAnchorDelta=" + failedAnchorDelta.ToString("0.0", CultureInfo.InvariantCulture));
                    return false;
                }

                routeTarget = recoveryTarget;
                LogThrottled("StartRecovery|" + snapshot.BotProfileId, now,
                    $"VANGUARD_TRAVEL_START_RECOVERY operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; failedAnchor={FormatVector(failureMemory.FailedAnchor)}; recoveryAnchor={FormatVector(routeTarget.Anchor)}; failedAnchorDelta={failedAnchorDelta:0.00}; recoveryDistance={recoveryAnchorDistance:0.00}; failureAge={(now - failureMemory.FailedAtUtc).TotalSeconds:0.00}; doctrine=recent_physical_failure_cannot_reissue_same_anchor; tag={PhysicalLivenessStatusTag}");
            }

            if (!admissionCursorReconciliationPrepared
                && !TryPath(botPosition, routeTarget.Anchor, out botPathDistance, out botCorners, out botPathStatus))
            {
                plan = TravelAnchorPlan.Invalid("route_bot_path_failed:" + botPathStatus);
                return false;
            }

            float ownerDistance = HorizontalDistance(owner, routeTarget.Anchor);
            string pathSummary = "route_bot_status=" + botPathStatus
                + ";route_bot_corners=" + botCorners.ToString(CultureInfo.InvariantCulture)
                + ";admissionReconciliation=" + Safe(admissionCursorReconciliationKind)
                + ";route=" + routeTarget.Summary;
            plan = new TravelAnchorPlan(
                true,
                routeTarget.Anchor,
                ownerDistance,
                snapshot.SquadCohesion.OwnerToOperatorPathDistance,
                botPathDistance,
                routeTarget.Reason,
                pathSummary,
                1000f - botPathDistance,
                routeTarget);
            return true;
        }

        Vector3 forward = snapshot.SquadCohesion.OwnerForward.HasValue ? Flatten(snapshot.SquadCohesion.OwnerForward.Value) : Vector3.forward;
        if (forward.sqrMagnitude <= 0.001f) forward = Vector3.forward;
        forward.Normalize();

        Vector3 radial = Flatten(botPosition - owner);
        if (radial.sqrMagnitude <= 0.25f) radial = -forward;
        radial.Normalize();

        float target = VanguardMovementAuthorityDoctrine.TacticalVolumeJoinTargetMeters;
        var candidates = new List<Vector3>();
        AddCandidates(candidates, owner, -forward, target, new[] { 0f, 18f, -18f, 36f, -36f, 72f, -72f });
        AddCandidates(candidates, owner, radial, Math.Max(target, 18f), new[] { 0f, 22f, -22f, 45f, -45f });
        candidates.Add(owner + forward * Math.Min(target, 14f));

        TravelAnchorScore best = TravelAnchorScore.Invalid("no_candidate");
        foreach (var raw in candidates)
        {
            if (!TryScoreAnchor(snapshot, owner, botPosition, raw, target, volumeJoin: true, relaxed: false, out var scored))
            {
                if (scored.Score > best.Score) best = scored;
                continue;
            }

            if (!best.Valid || scored.Score > best.Score) best = scored;
        }

        if (!best.Valid)
        {
            foreach (var raw in candidates)
            {
                if (!TryScoreAnchor(snapshot, owner, botPosition, raw, target, volumeJoin: true, relaxed: true, out var scored))
                {
                    if (scored.Score > best.Score) best = scored;
                    continue;
                }

                if (!best.Valid || scored.Score > best.Score) best = scored;
            }
        }

        if (!best.Valid)
        {
            plan = TravelAnchorPlan.Invalid(best.Reason);
            return false;
        }

        plan = new TravelAnchorPlan(
            true,
            best.Anchor,
            best.OwnerDistance,
            best.OwnerPathDistance,
            best.BotPathDistance,
            best.Reason,
            best.PathSummary,
            best.Score,
            VanguardTravelRouteTarget.Invalid("volume_join_fixed_anchor"));
        return true;
    }

    private static bool TryScoreAnchor(OperatorDecisionSnapshot snapshot, Vector3 owner, Vector3 botPosition, Vector3 rawAnchor, float target, bool volumeJoin, bool relaxed, out TravelAnchorScore score)
    {
        score = TravelAnchorScore.Invalid("not_scored");
        if (!TrySample(rawAnchor, relaxed ? 5.0f : 3.0f, out var sampled))
        {
            score = TravelAnchorScore.Invalid("reject_navmesh_sample_failed");
            return false;
        }

        float ownerDirect = HorizontalDistance(owner, sampled);
        float minOwnerRadius = volumeJoin ? 5.5f : 12.0f;
        float maxOwnerRadius = volumeJoin ? (relaxed ? 34.0f : 27.0f) : (relaxed ? 44.0f : 38.0f);
        if (ownerDirect < minOwnerRadius || ownerDirect > maxOwnerRadius)
        {
            score = TravelAnchorScore.Invalid("reject_owner_radius_" + ownerDirect.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (!TryPath(owner, sampled, out var ownerPathDistance, out var ownerCorners, out var ownerPathStatus))
        {
            score = TravelAnchorScore.Invalid("reject_owner_path_" + ownerPathStatus);
            return false;
        }

        if (!TryPath(botPosition, sampled, out var botPathDistance, out var botCorners, out var botPathStatus))
        {
            score = TravelAnchorScore.Invalid("reject_bot_path_" + botPathStatus);
            return false;
        }

        float ownerRatio = ownerDirect <= 0.25f ? 1.0f : ownerPathDistance / ownerDirect;
        float maxOwnerPath = volumeJoin ? (relaxed ? 52f : 42f) : (relaxed ? 58f : 45f);
        float maxBotPath = volumeJoin ? (relaxed ? 220f : 170f) : (relaxed ? 150f : 115f);
        int maxOwnerCorners = volumeJoin ? (relaxed ? 15 : 11) : (relaxed ? 12 : 9);
        int maxBotCorners = volumeJoin ? (relaxed ? 34 : 28) : (relaxed ? 24 : 18);
        float maxOwnerRatio = volumeJoin ? (relaxed ? 5.0f : 4.0f) : (relaxed ? 3.6f : 2.8f);
        if (ownerRatio > maxOwnerRatio)
        {
            score = TravelAnchorScore.Invalid("reject_owner_detour_ratio_" + ownerRatio.ToString("0.00", CultureInfo.InvariantCulture));
            return false;
        }

        if (ownerPathDistance > maxOwnerPath)
        {
            score = TravelAnchorScore.Invalid("reject_owner_path_too_long_" + ownerPathDistance.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (botPathDistance > maxBotPath)
        {
            score = TravelAnchorScore.Invalid("reject_bot_path_too_long_" + botPathDistance.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (ownerCorners > maxOwnerCorners || botCorners > maxBotCorners)
        {
            score = TravelAnchorScore.Invalid("reject_too_many_corners_owner_" + ownerCorners.ToString(CultureInfo.InvariantCulture) + "_bot_" + botCorners.ToString(CultureInfo.InvariantCulture));
            return false;
        }

        float value = 120f;
        value -= Math.Abs(ownerDirect - target) * (volumeJoin ? 2.0f : 2.8f);
        value -= ownerPathDistance * 0.38f;
        value -= botPathDistance * (volumeJoin ? 0.18f : 0.28f);
        value -= Math.Max(0f, ownerRatio - 1f) * 7f;
        value -= ownerCorners * 0.6f;
        value -= botCorners * (volumeJoin ? 0.35f : 0.5f);

        string pathSummary = "ownerDirect=" + ownerDirect.ToString("0.0", CultureInfo.InvariantCulture)
            + ";ownerPath=" + ownerPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";ownerRatio=" + ownerRatio.ToString("0.00", CultureInfo.InvariantCulture)
            + ";ownerCorners=" + ownerCorners.ToString(CultureInfo.InvariantCulture)
            + ";botPath=" + botPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";botCorners=" + botCorners.ToString(CultureInfo.InvariantCulture)
            + ";target=" + target.ToString("0.0", CultureInfo.InvariantCulture)
            + ";volumeJoin=" + Bool(volumeJoin)
            + ";relaxed=" + Bool(relaxed);
        score = new TravelAnchorScore(true, sampled, ownerDirect, ownerPathDistance, botPathDistance, volumeJoin ? "accepted_tactical_volume_anchor" : "accepted_travel_follow_anchor", pathSummary, value - (relaxed ? 6f : 0f));
        return true;
    }

    private static bool CanSoftDriveAfterNonCriticalPreempt(OperatorDecisionSnapshot snapshot, VanguardExternalPreemptResult preempt, bool volumeJoin, out string reason)
    {
        reason = "none";
        if (preempt.IsCombatDefer || VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot) || VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(snapshot))
        {
            reason = "combat_or_direct_threat";
            return false;
        }

        if (snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true || snapshot.Looting.HasActiveLootable == true)
        {
            reason = "loot_activity_not_soft_drivable";
            return false;
        }

        if (!volumeJoin && snapshot.SquadCohesion.OperatorDistanceToOwner < VanguardMovementAuthorityDoctrine.TravelCohesionSoftPathPreemptMeters)
        {
            reason = "distance_too_low_for_travel_soft_drive";
            return false;
        }

        bool supportedResidue = preempt.Outcome == VanguardExternalPreemptOutcome.Pending
            || preempt.Outcome == VanguardExternalPreemptOutcome.FailedOrbitStillActive
            || preempt.Outcome == VanguardExternalPreemptOutcome.FailedPathStillActive
            || preempt.Outcome == VanguardExternalPreemptOutcome.FailedMoverBusy;
        if (!supportedResidue)
        {
            reason = "outcome_not_soft_drivable:" + preempt.Outcome;
            return false;
        }

        if (preempt.After.LootingBotsActive || preempt.After.LootingBotsTaskRunning || preempt.After.LootingBotsHasActiveLootable)
        {
            reason = "loot_after_preempt_active";
            return false;
        }

        if (preempt.After.MoverMoving && preempt.After.RealSpeed > 1.15f && !volumeJoin)
        {
            reason = "external_mover_still_productive";
            return false;
        }

        reason = preempt.Outcome == VanguardExternalPreemptOutcome.FailedPathStillActive
            ? "noncritical_path_residue_soft_drive"
            : preempt.After.OrbitBrainLayerActive || preempt.After.OrbitSemanticActive || preempt.After.IsOrbitObjectiveResidue
                ? "noncritical_orbit_residue_soft_drive"
                : "noncritical_external_residue_soft_drive";
        return true;
    }

    private static bool TryResolveProgressiveAnchorJumpTarget(
        OperatorDecisionSnapshot snapshot,
        Vector3 botPosition,
        DateTimeOffset now,
        TravelLeaseState lease,
        VanguardTravelRouteTarget desiredTarget,
        float maxAnchorDelta,
        float maxAdvance,
        out VanguardTravelRouteTarget progressiveTarget,
        out string reason)
    {
        progressiveTarget = desiredTarget;
        reason = "no_progressive_candidate";
        if (snapshot == null
            || desiredTarget.RouteEpoch != lease.RouteEpoch
            || maxAdvance <= 1.0f)
        {
            return false;
        }

        float[] fractions = { 0.75f, 0.50f, 0.25f };
        foreach (float fraction in fractions)
        {
            float progressCeiling = lease.TargetProgressMeters + maxAdvance * fraction;
            if (!VanguardSquadTravelRouteMemory.TryResolveBoundedTarget(
                    snapshot,
                    botPosition,
                    now,
                    progressCeiling,
                    out var candidate))
            {
                continue;
            }

            float progressGain = candidate.TargetProgressMeters - lease.TargetProgressMeters;
            float anchorDelta = HorizontalDistance(lease.Anchor, candidate.Anchor);
            if (candidate.RouteEpoch == lease.RouteEpoch
                && progressGain >= 0.75f
                && anchorDelta >= VanguardMovementAuthorityDoctrine.TravelRetargetMaterialMeters
                && anchorDelta <= maxAnchorDelta)
            {
                progressiveTarget = candidate;
                reason = "fraction=" + fraction.ToString("0.00", CultureInfo.InvariantCulture)
                    + ":progressGain=" + progressGain.ToString("0.00", CultureInfo.InvariantCulture)
                    + ":anchorDelta=" + anchorDelta.ToString("0.00", CultureInfo.InvariantCulture);
                return true;
            }
        }

        return false;
    }

    private static float ResolveTravelRetargetMaxAdvanceMeters(string mode)
    {
        if (string.Equals(mode, VanguardTravelRouteModes.EmergencyCatchUp, StringComparison.OrdinalIgnoreCase))
        {
            return VanguardMovementAuthorityDoctrine.TravelRetargetMaxAdvanceEmergencyMeters;
        }

        if (string.Equals(mode, VanguardTravelRouteModes.CatchUp, StringComparison.OrdinalIgnoreCase))
        {
            return VanguardMovementAuthorityDoctrine.TravelRetargetMaxAdvanceCatchUpMeters;
        }

        return VanguardMovementAuthorityDoctrine.TravelRetargetMaxAdvanceFormationMeters;
    }

    private static float ResolveTravelRetargetMaxAnchorDeltaMeters(string mode)
    {
        if (string.Equals(mode, VanguardTravelRouteModes.EmergencyCatchUp, StringComparison.OrdinalIgnoreCase))
        {
            return VanguardMovementAuthorityDoctrine.TravelRetargetMaxAnchorDeltaEmergencyMeters;
        }

        if (string.Equals(mode, VanguardTravelRouteModes.CatchUp, StringComparison.OrdinalIgnoreCase))
        {
            return VanguardMovementAuthorityDoctrine.TravelRetargetMaxAnchorDeltaCatchUpMeters;
        }

        return VanguardMovementAuthorityDoctrine.TravelRetargetMaxAnchorDeltaFormationMeters;
    }

    private static void ResetTravelPhysicalLivenessBaseline(
        ref TravelLeaseState lease,
        Vector3 botPosition,
        float routeProgressMeters,
        float goalDistanceMeters,
        DateTimeOffset now)
    {
        lease.PhysicalProgressOriginPosition = botPosition;
        lease.PhysicalProgressOriginRouteMeters = routeProgressMeters;
        lease.PhysicalProgressOriginGoalDistance = goalDistanceMeters;
        lease.PhysicalTravelSinceProgressMeters = 0f;
        lease.LastPhysicalProgressAtUtc = now;
        lease.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
        lease.LastLivenessObservationAtUtc = now;
        lease.ObservedBlockedSeconds = 0f;
        lease.ObservedNoProgressSeconds = 0f;
        lease.PhysicalRestartCount = 0;
    }

    private static bool TryGetRecentPhysicalFailure(string botProfileId, DateTimeOffset now, out TravelPhysicalFailureMemory memory)
    {
        lock (Sync)
        {
            if (PhysicalFailureByBotProfileId.TryGetValue(botProfileId, out memory))
            {
                if (now - memory.FailedAtUtc <= TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TravelPhysicalFailureMemorySeconds))
                {
                    return true;
                }

                PhysicalFailureByBotProfileId.Remove(botProfileId);
            }
        }

        memory = TravelPhysicalFailureMemory.Empty;
        return false;
    }

    private static void RecordPhysicalFailureMemory(TravelLeaseState lease, DateTimeOffset now, string reason)
    {
        var memory = new TravelPhysicalFailureMemory(
            lease.Anchor,
            lease.LastObservedBotPosition,
            lease.TargetProgressMeters,
            now,
            reason);
        lock (Sync)
        {
            PhysicalFailureByBotProfileId[lease.BotProfileId] = memory;
        }

        VanguardClientDiagnosticsLog.Info(PhysicalLivenessStatusTag,
            $"VANGUARD_TRAVEL_PHYSICAL_FAILURE_MEMORY operator={Safe(lease.OperatorId)}; botProfile={Safe(lease.BotProfileId)}; failedAnchor={FormatVector(memory.FailedAnchor)}; botPosition={FormatVector(memory.BotPosition)}; targetProgress={memory.TargetProgressMeters:0.00}; reason={Safe(reason)}; expiresIn={VanguardMovementAuthorityDoctrine.TravelPhysicalFailureMemorySeconds:0.00}; doctrine=next_lease_cannot_reissue_same_failed_anchor; tag={PhysicalLivenessStatusTag}");
    }

    private static void ClearPhysicalFailureMemory(string botProfileId)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return;
        }

        lock (Sync)
        {
            PhysicalFailureByBotProfileId.Remove(botProfileId);
        }
    }

    private static bool IsPhysicalLivenessFailure(string reason)
    {
        return !string.IsNullOrWhiteSpace(reason)
            && (reason.Contains("physical_liveness", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("no_meaningful_physical_progress", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("locomotion_blocked", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The runtime scheduler/executor terminal handshake. A scheduler timeout must retire the exact
    /// physical travel generation in the same decision cycle; it may never leave a stale BigBrain
    /// command or executor lease alive after the primary window has disappeared.
    /// </summary>
    public static bool TryTerminateSchedulerExpiredWindow(
        string? botProfileId,
        string? expectedWindowId,
        DateTimeOffset now,
        string reason,
        out string summary)
    {
        summary = "not_found";
        string botKey = string.IsNullOrWhiteSpace(botProfileId) ? string.Empty : botProfileId.Trim();
        string expected = string.IsNullOrWhiteSpace(expectedWindowId) ? string.Empty : expectedWindowId.Trim();
        if (botKey.Length == 0 || expected.Length == 0)
        {
            summary = "invalid_identity";
            return false;
        }

        TravelLeaseState lease;
        lock (Sync)
        {
            if (!ActiveByBotProfileId.TryGetValue(botKey, out lease))
            {
                summary = "executor_lease_missing";
                return false;
            }

            if (!string.Equals(lease.WindowId, expected, StringComparison.OrdinalIgnoreCase))
            {
                summary = "foreign_window:active=" + Safe(lease.WindowId) + ":expected=" + Safe(expected);
                return false;
            }

            ActiveByBotProfileId.Remove(botKey);
        }

        VanguardReturnMovementCommandStore.ClearOwned(
            lease.BotProfileId,
            lease.LeaseId,
            lease.StartedAtUtc,
            "scheduler_terminal:" + reason);
        SetCooldown(lease.BotProfileId, now, 0f);
        VanguardSquadTravelCohesionAuthority.ClearHold(
            lease.BotProfileId,
            now,
            "scheduler_terminal_cleanup:" + reason);
        summary = lease.Summary + ";schedulerReason=" + Safe(reason) + ";physicalCommandCleared=true";
        VanguardClientDiagnosticsLog.Warning(RecoveryTruthStatusTag,
            $"VANGUARD_TRAVEL_SCHEDULER_ATOMIC_TERMINAL {summary}; schedulerWindowAlreadyRemoved=true; executorLeaseRemoved=true; commandCleared=true; cooldown=0; freshRescore=true; doctrine=one_primary_window_one_physical_generation_one_terminal_event; tag={RecoveryTruthStatusTag}; schedulerTag={VanguardMainIntentScheduler.StatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
        return true;
    }

    private static void FinishLease(
        TravelLeaseState lease,
        DateTimeOffset now,
        string outcome,
        string reason,
        bool failureCooldown,
        string snapshotSignature,
        bool recordPostReturnHold = true)
    {
        if (failureCooldown && !lease.VolumeJoin && IsPhysicalLivenessFailure(reason))
        {
            RecordPhysicalFailureMemory(lease, now, reason);
        }
        else if (!failureCooldown)
        {
            ClearPhysicalFailureMemory(lease.BotProfileId);
        }

        lock (Sync)
        {
            ActiveByBotProfileId.Remove(lease.BotProfileId);
        }

        VanguardReturnMovementCommandStore.ClearOwned(lease.BotProfileId, lease.LeaseId, lease.StartedAtUtc, "travel_finished:" + reason);
        float cooldownSeconds = failureCooldown ? FailureCooldownSeconds(lease.VolumeJoin) : SuccessCooldownSeconds(lease.VolumeJoin);
        SetCooldown(lease.BotProfileId, now, cooldownSeconds);
        if (!failureCooldown && recordPostReturnHold)
        {
            if (lease.LastOwnerDistance <= VanguardMovementAuthorityDoctrine.CloseCohesionStartMinMeters)
            {
                VanguardSquadTravelCohesionAuthority.ClearHold(lease.BotProfileId, now, "travel_or_volume_settled:" + reason);
            }
            else
            {
                VanguardSquadTravelCohesionAuthority.RecordTravelAuthorityHold(lease.BotProfileId, lease.OperatorId, lease.LastOwnerDistance, now, "travel_or_volume_completed_keep_quiesce:" + reason);
            }
        }

        VanguardMainIntentScheduler.FinishPrimaryWindow(lease.BotProfileId, now, outcome, reason, lease.Summary, lease.WindowId);
        string log = string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase)
            ? "VANGUARD_TRAVEL_COHESION_COMPLETED"
            : "VANGUARD_TRAVEL_COHESION_ABORTED";
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"{log} lease={Safe(lease.LeaseId)}; operator={Safe(lease.OperatorId)}; botProfile={Safe(lease.BotProfileId)}; request={Safe(lease.RequestKind)}; mode={Safe(lease.TravelMode)}; outcome={Safe(outcome)}; reason={Safe(reason)}; ownerDistance={lease.LastOwnerDistance:0.0}; cooldown={cooldownSeconds:0.0}; fullLeasePayload=false; snapshotPayload=false; tag={StatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
        VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
            $"VANGUARD_TRAVEL_COHESION_TERMINAL_TRACE leaseSummary={Safe(lease.Summary)}; snapshot={Safe(snapshotSignature)}; tag={StatusTag}");
    }

    private static bool IsVolumeJoinRecovered(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.TacticalVolumeJoinTargetMeters + 8.0f
            && snapshot.SquadCohesion.OwnerToOperatorPathRatio <= Math.Max(2.0f, VanguardMovementAuthorityDoctrine.TacticalVolumeJoinPathRatio - 0.75f))
        {
            return true;
        }

        return snapshot.SquadCohesion.SectorTopologyValid
            && string.Equals(snapshot.SquadCohesion.SectorTopologyReason, "topology_valid_same_tactical_volume", StringComparison.OrdinalIgnoreCase)
            && snapshot.SquadCohesion.OperatorDistanceToOwner <= 34.0f;
    }

    private static void SetCooldown(string botProfileId, DateTimeOffset now, float seconds)
    {
        lock (Sync)
        {
            if (seconds <= 0.01f)
            {
                CooldownByBotProfileId.Remove(botProfileId);
                return;
            }

            CooldownByBotProfileId[botProfileId] = now + TimeSpan.FromSeconds(Math.Max(1.0f, seconds));
        }
    }

    private static float MaxDurationSeconds(bool volumeJoin) => volumeJoin
        ? VanguardMovementAuthorityDoctrine.TacticalVolumeJoinMaxDurationSeconds
        : 30.0f;
    private static float NoProgressSeconds(bool volumeJoin) => volumeJoin
        ? VanguardMovementAuthorityDoctrine.TacticalVolumeJoinNoProgressSeconds
        : VanguardContinuousCohesionLocomotionPolicy.ContinuousNoProgressSeconds;
    private static float SuccessCooldownSeconds(bool volumeJoin) => volumeJoin ? VanguardMovementAuthorityDoctrine.TacticalVolumeJoinSuccessCooldownSeconds : 0f;
    private static float FailureCooldownSeconds(bool volumeJoin) => volumeJoin ? VanguardMovementAuthorityDoctrine.TacticalVolumeJoinFailureCooldownSeconds : VanguardMovementAuthorityDoctrine.TravelCohesionFailureCooldownSeconds;

    private static void AddCandidates(List<Vector3> candidates, Vector3 owner, Vector3 baseDirection, float distance, float[] angles)
    {
        Vector3 dir = Flatten(baseDirection);
        if (dir.sqrMagnitude <= 0.001f) dir = Vector3.back;
        dir.Normalize();
        foreach (float angle in angles)
        {
            candidates.Add(owner + Rotate(dir, angle) * distance);
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

    private static bool TrySample(Vector3 raw, float radius, out Vector3 sampled)
    {
        if (NavMesh.SamplePosition(raw + Vector3.up * 0.30f, out var hit, radius, NavMesh.AllAreas))
        {
            sampled = hit.position;
            return true;
        }

        sampled = Vector3.zero;
        return false;
    }

    private static bool TryPath(Vector3 start, Vector3 end, out float distance, out int corners, out string status)
    {
        return TryPath(
            start,
            end,
            out distance,
            out corners,
            out status,
            out _,
            out _);
    }

    private static bool TryPath(
        Vector3 start,
        Vector3 end,
        out float distance,
        out int corners,
        out string status,
        out Vector3 partialEndpoint,
        out bool partialAvailable)
    {
        distance = 0f;
        corners = 0;
        status = "none";
        partialEndpoint = Vector3.zero;
        partialAvailable = false;
        if (!TrySample(start, 4.0f, out var sampledStart))
        {
            status = "start_sample_failed";
            return false;
        }

        if (!TrySample(end, 4.0f, out var sampledEnd))
        {
            status = "end_sample_failed";
            return false;
        }

        var path = new NavMeshPath();
        bool calculated = NavMesh.CalculatePath(sampledStart, sampledEnd, NavMesh.AllAreas, path);
        corners = path.corners == null ? 0 : path.corners.Length;
        distance = PathDistance(path);
        if (calculated
            && path.status == NavMeshPathStatus.PathPartial
            && path.corners != null
            && path.corners.Length >= 2)
        {
            partialEndpoint = path.corners[path.corners.Length - 1];
            partialAvailable = true;
        }

        status = "calculated=" + Bool(calculated)
            + ";status=" + path.status
            + ";corners=" + corners.ToString(CultureInfo.InvariantCulture)
            + ";partialAvailable=" + Bool(partialAvailable);
        return calculated && path.status == NavMeshPathStatus.PathComplete && corners >= 2;
    }

    private static bool TryValidatePartialPathBridge(
        TravelLeaseState lease,
        Vector3 botPosition,
        VanguardTravelRouteTarget routeTarget,
        Vector3 partialEndpoint,
        float partialPathDistance,
        out string reason)
    {
        reason = "none";
        if (partialEndpoint == Vector3.zero
            || float.IsNaN(partialPathDistance)
            || float.IsInfinity(partialPathDistance))
        {
            reason = "partial_endpoint_or_distance_invalid";
            return false;
        }

        float physicalAdvance = HorizontalDistance(botPosition, partialEndpoint);
        if (physicalAdvance < VanguardContinuousCohesionLocomotionPolicy.PartialPathBridgeMinimumPathMeters)
        {
            reason = "partial_bridge_too_short:advance="
                + physicalAdvance.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        float currentGoalDistance = HorizontalDistance(botPosition, routeTarget.Anchor);
        float bridgedGoalDistance = HorizontalDistance(partialEndpoint, routeTarget.Anchor);
        float goalGain = currentGoalDistance - bridgedGoalDistance;
        if (goalGain < VanguardContinuousCohesionLocomotionPolicy.PartialPathBridgeMinimumGoalGainMeters)
        {
            reason = "partial_bridge_no_material_goal_gain:gain="
                + goalGain.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        float commandDelta = HorizontalDistance(lease.Anchor, partialEndpoint);
        if (commandDelta < VanguardContinuousCohesionLocomotionPolicy.PartialPathBridgeMinimumAnchorDeltaMeters)
        {
            reason = "partial_bridge_not_material_from_command:delta="
                + commandDelta.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        float maxPathDistance = ResolvePartialPathBridgeMaxMeters(routeTarget.Mode);
        if (partialPathDistance > maxPathDistance)
        {
            reason = "partial_bridge_path_ceiling:distance="
                + partialPathDistance.ToString("0.00", CultureInfo.InvariantCulture)
                + ":ceiling=" + maxPathDistance.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        if (lease.PartialBridgeActive
            && HorizontalDistance(lease.Anchor, partialEndpoint)
                < VanguardContinuousCohesionLocomotionPolicy.PartialPathBridgeMinimumAnchorDeltaMeters)
        {
            reason = "partial_bridge_repeats_current_endpoint";
            return false;
        }

        reason = "partial_bridge_valid"
            + ":advance=" + physicalAdvance.ToString("0.00", CultureInfo.InvariantCulture)
            + ":goalGain=" + goalGain.ToString("0.00", CultureInfo.InvariantCulture)
            + ":commandDelta=" + commandDelta.ToString("0.00", CultureInfo.InvariantCulture)
            + ":pathDistance=" + partialPathDistance.ToString("0.00", CultureInfo.InvariantCulture)
            + ":ceiling=" + maxPathDistance.ToString("0.00", CultureInfo.InvariantCulture);
        return true;
    }

    private static float ResolvePartialPathBridgeMaxMeters(string? mode)
    {
        if (string.Equals(mode, VanguardTravelRouteModes.EmergencyCatchUp, StringComparison.OrdinalIgnoreCase))
        {
            return VanguardContinuousCohesionLocomotionPolicy.PartialPathBridgeMaxEmergencyPathMeters;
        }

        if (string.Equals(mode, VanguardTravelRouteModes.CatchUp, StringComparison.OrdinalIgnoreCase))
        {
            return VanguardContinuousCohesionLocomotionPolicy.PartialPathBridgeMaxCatchUpPathMeters;
        }

        return VanguardContinuousCohesionLocomotionPolicy.PartialPathBridgeMaxFormationPathMeters;
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

    private static Vector3 Rotate(Vector3 vector, float degrees)
    {
        float radians = degrees * Mathf.Deg2Rad;
        float cos = Mathf.Cos(radians);
        float sin = Mathf.Sin(radians);
        return new Vector3(vector.x * cos - vector.z * sin, 0f, vector.x * sin + vector.z * cos);
    }

    private static Vector3 Flatten(Vector3 vector)
    {
        vector.y = 0f;
        return vector;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static string FormatVector(Vector3 value)
    {
        return value.x.ToString("0.0", CultureInfo.InvariantCulture) + ","
            + value.y.ToString("0.0", CultureInfo.InvariantCulture) + ","
            + value.z.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static void LogThrottled(string key, DateTimeOffset now, Func<string> messageFactory)
    {
        LogThrottled(key, now, LogInterval, messageFactory);
    }

    private static void LogThrottled(string key, DateTimeOffset now, TimeSpan interval, Func<string> messageFactory)
    {
        if (!VanguardClientDiagnosticsLog.IsEnabled(VanguardAuditLevel.Trace))
        {
            return;
        }

        lock (Sync)
        {
            if (LastLogByKey.TryGetValue(key, out var last) && now - last < interval)
            {
                return;
            }

            LastLogByKey[key] = now;
        }

        VanguardClientDiagnosticsLog.Trace(StatusTag, messageFactory);
    }

    private static void LogThrottled(string key, DateTimeOffset now, string message)
        => LogThrottled(key, now, LogInterval, message);

    private static void LogThrottled(string key, DateTimeOffset now, TimeSpan interval, string message)
    {
        lock (Sync)
        {
            if (LastLogByKey.TryGetValue(key, out var last) && now - last < interval)
            {
                return;
            }

            LastLogByKey[key] = now;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag, message);
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }

    private readonly struct TravelAnchorPlan
    {
        public TravelAnchorPlan(
            bool valid,
            Vector3 anchor,
            float ownerDistance,
            float ownerPathDistance,
            float botPathDistance,
            string reason,
            string pathSummary,
            float score,
            VanguardTravelRouteTarget routeTarget)
        {
            Valid = valid;
            Anchor = anchor;
            OwnerDistance = ownerDistance;
            OwnerPathDistance = ownerPathDistance;
            BotPathDistance = botPathDistance;
            Reason = reason;
            PathSummary = pathSummary;
            Score = score;
            RouteTarget = routeTarget;
        }

        public static TravelAnchorPlan Invalid(string reason) => new(
            false,
            Vector3.zero,
            0f,
            0f,
            0f,
            reason,
            "none",
            -9999f,
            VanguardTravelRouteTarget.Invalid(reason));
        public bool Valid { get; }
        public Vector3 Anchor { get; }
        public float OwnerDistance { get; }
        public float OwnerPathDistance { get; }
        public float BotPathDistance { get; }
        public string Reason { get; }
        public string PathSummary { get; }
        public float Score { get; }
        public VanguardTravelRouteTarget RouteTarget { get; }
        public string Summary => "ownerDistance=" + OwnerDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";ownerPath=" + OwnerPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";botPath=" + BotPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";score=" + Score.ToString("0.0", CultureInfo.InvariantCulture)
            + ";reason=" + Safe(Reason)
            + ";path=" + Safe(PathSummary)
            + ";route=" + Safe(RouteTarget.Summary);
    }

    private readonly struct TravelAnchorScore
    {
        public TravelAnchorScore(bool valid, Vector3 anchor, float ownerDistance, float ownerPathDistance, float botPathDistance, string reason, string pathSummary, float score)
        {
            Valid = valid;
            Anchor = anchor;
            OwnerDistance = ownerDistance;
            OwnerPathDistance = ownerPathDistance;
            BotPathDistance = botPathDistance;
            Reason = reason;
            PathSummary = pathSummary;
            Score = score;
        }

        public static TravelAnchorScore Invalid(string reason) => new(false, Vector3.zero, 0f, 0f, 0f, reason, "none", -9999f);
        public bool Valid { get; }
        public Vector3 Anchor { get; }
        public float OwnerDistance { get; }
        public float OwnerPathDistance { get; }
        public float BotPathDistance { get; }
        public string Reason { get; }
        public string PathSummary { get; }
        public float Score { get; }
    }

    private readonly struct TravelPhysicalFailureMemory
    {
        public static readonly TravelPhysicalFailureMemory Empty = new(Vector3.zero, Vector3.zero, 0f, DateTimeOffset.MinValue, "none");

        public TravelPhysicalFailureMemory(Vector3 failedAnchor, Vector3 botPosition, float targetProgressMeters, DateTimeOffset failedAtUtc, string reason)
        {
            FailedAnchor = failedAnchor;
            BotPosition = botPosition;
            TargetProgressMeters = targetProgressMeters;
            FailedAtUtc = failedAtUtc;
            Reason = reason;
        }

        public Vector3 FailedAnchor { get; }
        public Vector3 BotPosition { get; }
        public float TargetProgressMeters { get; }
        public DateTimeOffset FailedAtUtc { get; }
        public string Reason { get; }
    }

    private struct TravelLeaseState
    {
        public string LeaseId;
        public string WindowId;
        public string OperatorId;
        public string BotProfileId;
        public string RequestKind;
        public long CommandGeneration;
        public bool VolumeJoin;
        public Vector3 Anchor;
        public float AnchorRadiusMeters;
        public DateTimeOffset StartedAtUtc;
        public DateTimeOffset MinUntilUtc;
        public DateTimeOffset MaxUntilUtc;
        public DateTimeOffset NoProgressUntilUtc;
        public DateTimeOffset LastProgressAtUtc;
        public float InitialAnchorDistance;
        public float LastAnchorDistance;
        public float InitialOwnerDistance;
        public float LastOwnerDistance;
        public DateTimeOffset ExtremeOwnerLagSinceUtc;
        public DateTimeOffset LastMeaningfulOwnerClosingAtUtc;
        public float InitialOwnerPathDistance;
        public float LastOwnerPathDistance;
        public DateTimeOffset ConsumedAnchorPathFailureSinceUtc;
        public int ConsumedAnchorPathFailureCount;
        public DateTimeOffset ConsumedAnchorStaleGenerationSinceUtc;
        public float ConsumedAnchorStaleOwnerDistanceAtStart;
        public Vector3 LastWorldPosition;
        public DateTimeOffset LastWorldSampleAtUtc;
        public Vector3 LastObservedBotPosition;
        public Vector3 PhysicalProgressOriginPosition;
        public float PhysicalProgressOriginRouteMeters;
        public float PhysicalProgressOriginGoalDistance;
        public float PhysicalTravelSinceProgressMeters;
        public DateTimeOffset LastPhysicalProgressAtUtc;
        public DateTimeOffset PhysicalBlockedSinceUtc;
        public DateTimeOffset LastLivenessObservationAtUtc;
        public float ObservedBlockedSeconds;
        public float ObservedNoProgressSeconds;
        public int PhysicalRestartCount;
        public bool PartialBridgeActive;
        public int PartialBridgeCount;
        public float PartialBridgeDesiredTargetProgressMeters;
        public bool ObservationApproachActive;
        public string ObservationApproachClaimId;
        public string ObservationApproachLane;
        public DateTimeOffset NextObservationDeploymentProbeAtUtc;
        public float PathDistanceMeters;
        public DateTimeOffset NextExternalQuiesceAtUtc;
        public DateTimeOffset NextRetargetAllowedAtUtc;
        public DateTimeOffset NextWindowRefreshAtUtc;
        public int RetargetCount;
        public int RouteEpoch;
        public long RouteVersion;
        public float RouteProgressMeters;
        public float TargetProgressMeters;
        public string TravelMode;
        public bool OwnerMoving;
        public float OwnerStationarySeconds;
        public string PlanSummary;

        public string Summary => "lease=" + Safe(LeaseId)
            + ";window=" + Safe(WindowId)
            + ";operator=" + Safe(OperatorId)
            + ";botProfile=" + Safe(BotProfileId)
            + ";request=" + Safe(RequestKind)
            + ";commandGeneration=" + CommandGeneration.ToString(CultureInfo.InvariantCulture)
            + ";volumeJoin=" + Bool(VolumeJoin)
            + ";anchor=" + Anchor.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + Anchor.y.ToString("0.0", CultureInfo.InvariantCulture) + "," + Anchor.z.ToString("0.0", CultureInfo.InvariantCulture)
            + ";radius=" + AnchorRadiusMeters.ToString("0.0", CultureInfo.InvariantCulture)
            + ";initialAnchorDist=" + InitialAnchorDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";lastAnchorDist=" + LastAnchorDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";initialOwnerDist=" + InitialOwnerDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";lastOwnerDist=" + LastOwnerDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";extremeOwnerLagSince=" + ExtremeOwnerLagSinceUtc.ToString("O", CultureInfo.InvariantCulture)
            + ";lastOwnerClosing=" + LastMeaningfulOwnerClosingAtUtc.ToString("O", CultureInfo.InvariantCulture)
            + ";initialOwnerPath=" + InitialOwnerPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";lastOwnerPath=" + LastOwnerPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";consumedPathFailures=" + ConsumedAnchorPathFailureCount.ToString(CultureInfo.InvariantCulture)
            + ";consumedPathFailureSince=" + ConsumedAnchorPathFailureSinceUtc.ToString("O", CultureInfo.InvariantCulture)
            + ";consumedAnchorStaleSince=" + ConsumedAnchorStaleGenerationSinceUtc.ToString("O", CultureInfo.InvariantCulture)
            + ";consumedAnchorStaleOwnerDistance=" + ConsumedAnchorStaleOwnerDistanceAtStart.ToString("0.0", CultureInfo.InvariantCulture)
            + ";physicalRestarts=" + PhysicalRestartCount.ToString(CultureInfo.InvariantCulture)
            + ";physicalTravelSinceProgress=" + PhysicalTravelSinceProgressMeters.ToString("0.00", CultureInfo.InvariantCulture)
            + ";lastPhysicalProgress=" + LastPhysicalProgressAtUtc.ToString("O", CultureInfo.InvariantCulture)
            + ";blockedSince=" + PhysicalBlockedSinceUtc.ToString("O", CultureInfo.InvariantCulture)
            + ";observedBlocked=" + ObservedBlockedSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + ";observedNoProgress=" + ObservedNoProgressSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + ";path=" + PathDistanceMeters.ToString("0.0", CultureInfo.InvariantCulture)
            + ";partialBridge=" + Bool(PartialBridgeActive)
            + ";partialBridgeCount=" + PartialBridgeCount.ToString(CultureInfo.InvariantCulture)
            + ";partialBridgeDesiredProgress=" + PartialBridgeDesiredTargetProgressMeters.ToString("0.0", CultureInfo.InvariantCulture)
            + ";observationApproach=" + Bool(ObservationApproachActive)
            + ";observationClaim=" + Safe(ObservationApproachClaimId)
            + ";observationLane=" + Safe(ObservationApproachLane)
            + ";retargets=" + RetargetCount.ToString(CultureInfo.InvariantCulture)
            + ";routeEpoch=" + RouteEpoch.ToString(CultureInfo.InvariantCulture)
            + ";routeVersion=" + RouteVersion.ToString(CultureInfo.InvariantCulture)
            + ";routeProgress=" + RouteProgressMeters.ToString("0.0", CultureInfo.InvariantCulture)
            + ";targetProgress=" + TargetProgressMeters.ToString("0.0", CultureInfo.InvariantCulture)
            + ";travelMode=" + Safe(TravelMode)
            + ";ownerMoving=" + Bool(OwnerMoving)
            + ";ownerStationary=" + OwnerStationarySeconds.ToString("0.0", CultureInfo.InvariantCulture)
            + ";nextRetargetUtc=" + NextRetargetAllowedAtUtc.ToString("O", CultureInfo.InvariantCulture)
            + ";nextWindowRefreshUtc=" + NextWindowRefreshAtUtc.ToString("O", CultureInfo.InvariantCulture)
            + ";nextOrbitQuiesceUtc=" + NextExternalQuiesceAtUtc.ToString("O", CultureInfo.InvariantCulture)
            + ";plan=" + Safe(PlanSummary);
    }
}
#endif
