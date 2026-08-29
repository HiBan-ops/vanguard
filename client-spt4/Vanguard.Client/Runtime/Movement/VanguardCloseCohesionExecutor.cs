#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.External;
using Vanguard.Client.Runtime.Movement.Brain;

// Responsibility: closes a short squad-spacing gap by moving an already-authorized Operator to a safe nearby position around the player owner.
// Flow: The current cohesion request is rechecked, a close owner-relative anchor is sampled and path-validated, a temporary movement lease drives the Operator into the desired band, and the lease ends when spacing is acceptable or a higher-priority condition interrupts it.
// Authority boundary: cohesion policy decides when close recovery is needed; this executor only performs that bounded movement and must yield to combat, medical, grenade and stronger return authority.
// Invariant: anchors must remain reachable and non-stacking, and every lease is generation/timeout bounded so close-cohesion movement cannot outlive the condition that authorized it.
namespace Vanguard.Client.Runtime.Movement;

/// <summary>
/// Vanguard adds a deliberately non-invasive close cohesion layer. It does not assign persistent slots
/// and does not reshuffle the squad every few seconds. It only pulls a safe, over-wide Operator
/// inward along his current radial lane around the player, after HardReturn brought him back into
/// the tactical bubble and only when combat/medical/critical loot gates are clear.
/// </summary>
internal static class VanguardCloseCohesionExecutor
{
    public const string StatusTag = "VANGUARD_CLOSE_COHESION_STATUS";
    public const string RuntimeTuningStatusTag = "VANGUARD_CLOSE_COHESION_RUNTIME_TUNING_STATUS";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, CloseCohesionLeaseState> ActiveByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> CooldownByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(1.50d);
    private static DateTimeOffset nextTickAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            ActiveByBotProfileId.Clear();
            CooldownByBotProfileId.Clear();
            LastLogByKey.Clear();
        }

        bootLogged = false;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_CLOSE_COHESION_RESET reason={Safe(reason)}; active=0; cooldowns=cleared; doctrine=radial_micro_adjust_no_slot_churn; tag={StatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
    }

    public static void Tick()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now < nextTickAtUtc)
        {
            return;
        }

        nextTickAtUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CloseCohesionTickSeconds);
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_CLOSE_COHESION_BOOT enabled={Bool(VanguardMovementAuthorityDoctrine.TacticalRepositionActiveEnabled)}; scope=inside_bubble_safe_micro_adjust; slotPolicy=none_radial_lane_preserved; backend=BigBrain_GoToSomePointData; startBand={VanguardMovementAuthorityDoctrine.CloseCohesionStartMinMeters:0.0}-{VanguardMovementAuthorityDoctrine.CloseCohesionStartMaxMeters:0.0}; forceStart={VanguardMovementAuthorityDoctrine.CloseCohesionForceStartMeters:0.0}; targets=indoor_{VanguardMovementAuthorityDoctrine.CloseCohesionIndoorTargetMeters:0.0}_outdoor_{VanguardMovementAuthorityDoctrine.CloseCohesionOutdoorTargetMeters:0.0}; max={VanguardMovementAuthorityDoctrine.CloseCohesionMaxDurationSeconds:0.0}; noProgress={VanguardMovementAuthorityDoctrine.CloseCohesionNoProgressSeconds:0.0}; cooldownSuccess={VanguardMovementAuthorityDoctrine.CloseCohesionSuccessCooldownSeconds:0.0}; cooldownFailure={VanguardMovementAuthorityDoctrine.CloseCohesionFailureCooldownSeconds:0.0}; softProgress=anchor_{VanguardMovementAuthorityDoctrine.CloseCohesionSoftProgressGainMeters:0.00}_owner_{VanguardMovementAuthorityDoctrine.CloseCohesionOwnerProgressGainMeters:0.00}; softCompleteExtra={VanguardMovementAuthorityDoctrine.CloseCohesionSoftCompleteExtraMeters:0.0}; excludes=productiveDirectThreat_stationaryMedical_hardReturn_criticalLoot; orbitPreempt=bounded_soft_drive_if_noncritical; pathPreempt=bounded_if_far; WeakWindowPreempt=true; tag={StatusTag}; Tag={VanguardMovementAuthorityDoctrine.CombatCohesionAuthorityStatusTag}; runtimeTuningTag={RuntimeTuningStatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}; build={VanguardBuildVersion.BuildLabel}");
        }

        var snapshots = VanguardOperatorDecisionSnapshotService.GetLatestSnapshots();
        TickActiveLeases(snapshots, now);
        if (!VanguardMovementAuthorityDoctrine.TacticalRepositionActiveEnabled)
        {
            return;
        }

        if (VanguardRuntimeFrameBudgetGuard.ShouldRunOptional(
            "CloseCohesionPlanning",
            now,
            TimeSpan.FromSeconds(1.0d),
            out _))
        {
            TryStartOneLease(snapshots, now);
        }
    }

    private static void TryStartOneLease(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        if (snapshots == null || snapshots.Count == 0)
        {
            return;
        }

        foreach (var snapshot in snapshots.OrderByDescending(ScoreStartCandidate))
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            {
                continue;
            }

            if (!IsCloseCohesionContract(snapshot))
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
                    LogThrottled("cooldown|" + snapshot.BotProfileId, now,
                        $"VANGUARD_CLOSE_COHESION_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=cooldown; remaining={(cooldownUntil - now).TotalSeconds:0.0}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; sector={Safe(snapshot.SquadCohesion.Sector)}; tag={StatusTag}");
                    continue;
                }
            }

            string gate = CheckStartGate(snapshot);
            if (!string.Equals(gate, "none", StringComparison.OrdinalIgnoreCase))
            {
                LogThrottled("gate|" + snapshot.BotProfileId + "|" + gate, now,
                    $"VANGUARD_CLOSE_COHESION_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(gate)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; ownerKnown={Bool(snapshot.SquadCohesion.OwnerKnown)}; ownerReliable={Bool(snapshot.SquadCohesion.OwnerReliableForActiveMovement)}; threat={Safe(snapshot.Threat.Classification)}; medical={Safe(snapshot.Medical.Classification)}; loot={Safe(snapshot.Looting.Classification)}; orbit={Bool(snapshot.Orbit.Active)}; path={Bool(snapshot.Movement.HasPath == true)}; tag={StatusTag}");
                continue;
            }

            if (VanguardMainIntentScheduler.HasBlockingPrimaryWindow(snapshot.BotProfileId, now, out var blockingReason))
            {
                LogThrottled("primary|" + snapshot.BotProfileId + "|" + blockingReason, now,
                    $"VANGUARD_CLOSE_COHESION_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=primary_window_busy:{Safe(blockingReason)}; tag={StatusTag}");
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record) || record.BotOwner == null || record.BotOwner.IsDead)
            {
                LogThrottled("botowner|" + snapshot.BotProfileId, now,
                    $"VANGUARD_CLOSE_COHESION_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=bot_owner_missing_or_dead; tag={StatusTag}");
                continue;
            }

            Vector3 botPosition = ResolveBotPosition(record.BotOwner);
            if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner("MovementPathPlan", snapshot.OwnerProfileId, 1, 2, out _))
            {
                // Budget exhaustion is a short deferral, never a failed anchor or cooldown.
                return;
            }

            if (!TryResolveCloseAnchor(snapshot, botPosition, out var plan))
            {
                SetCooldown(snapshot.BotProfileId, now, VanguardMovementAuthorityDoctrine.CloseCohesionFailureCooldownSeconds);
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"VANGUARD_CLOSE_COHESION_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=anchor_failed:{Safe(plan.Reason)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; env={Safe(snapshot.SquadCohesion.TacticalEnvironmentKind)}; sector={Safe(snapshot.SquadCohesion.Sector)}; summary={Safe(plan.Summary)}; tag={StatusTag}");
                continue;
            }

            if (!VanguardMainIntentScheduler.TryOpenCloseCohesion(snapshot, now, out var windowId, out var openReason))
            {
                LogThrottled("open|" + snapshot.BotProfileId + "|" + openReason, now,
                    $"VANGUARD_CLOSE_COHESION_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=scheduler_denied:{Safe(openReason)}; plan={Safe(plan.Summary)}; tag={StatusTag}");
                continue;
            }

            if (NeedsExternalPreempt(snapshot))
            {
                var preempt = VanguardExternalAuthorityAdapter.RequestOrbitAuthorityQuiesce(
                    record.BotOwner,
                    snapshot,
                    "close_cohesion_micro_adjust",
                    TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CloseCohesionMaxDurationSeconds + VanguardMovementAuthorityDoctrine.OrbitQuiesceRefreshSeconds + 2.0f),
                    now);
                string softDriveReason = "none";
                if (!preempt.CanDriveMovement && !CanSoftDriveAfterNonCriticalPreempt(snapshot, preempt, out softDriveReason))
                {
                    SetCooldown(snapshot.BotProfileId, now, VanguardMovementAuthorityDoctrine.CloseCohesionFailureCooldownSeconds);
                    VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "external_preempt_not_granted:" + preempt.Outcome, preempt.Summary, windowId);
                    VanguardClientDiagnosticsLog.Info(StatusTag,
                        $"VANGUARD_CLOSE_COHESION_ABORTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=external_preempt_not_granted; outcome={preempt.Outcome}; preempt={Safe(preempt.Summary)}; plan={Safe(plan.Summary)}; tag={StatusTag}; runtimeTuningTag={RuntimeTuningStatusTag}");
                    continue;
                }

                if (!preempt.CanDriveMovement)
                {
                    VanguardClientDiagnosticsLog.Info(StatusTag,
                        $"VANGUARD_CLOSE_COHESION_ORBIT_SOFT_OVERRIDE operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(softDriveReason)}; outcome={preempt.Outcome}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; preempt={Safe(preempt.Summary)}; plan={Safe(plan.Summary)}; tag={VanguardMovementAuthorityDoctrine.OrbitAuthorityQuiesceStatusTag}; runtimeTuningTag={RuntimeTuningStatusTag}; baseTag={StatusTag}");
                }
            }

            string leaseId = "close_cohesion_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId);
            DateTimeOffset maxUntil = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CloseCohesionMaxDurationSeconds);
            bool issued = VanguardReturnMovementCommandStore.Issue(
                leaseId,
                snapshot.OperatorId,
                snapshot.BotProfileId,
                plan.Anchor,
                VanguardMovementAuthorityDoctrine.CloseCohesionAnchorRadiusMeters,
                sprint: false,
                now,
                maxUntil,
                VanguardMovementContractPolicy.CloseCohesionMicroAdjust,
                plan.PathSummary,
                plan.BotPathDistance,
                out var commandResult);
            if (!issued)
            {
                SetCooldown(snapshot.BotProfileId, now, VanguardMovementAuthorityDoctrine.CloseCohesionFailureCooldownSeconds);
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "move_bridge_rejected:" + commandResult, plan.Summary, windowId);
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"VANGUARD_CLOSE_COHESION_ABORTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=move_bridge_rejected:{Safe(commandResult)}; plan={Safe(plan.Summary)}; tag={StatusTag}");
                continue;
            }

            if (!VanguardReturnMovementCommandStore.TryGetActive(snapshot.BotProfileId, now, out var ownedCommand)
                || !string.Equals(ownedCommand.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ownedCommand.RequestKind, VanguardMovementContractPolicy.CloseCohesionMicroAdjust, StringComparison.OrdinalIgnoreCase))
            {
                VanguardReturnMovementCommandStore.ClearOwned(snapshot.BotProfileId, leaseId, now, "close_cohesion_command_identity_not_confirmed");
                SetCooldown(snapshot.BotProfileId, now, VanguardMovementAuthorityDoctrine.CloseCohesionFailureCooldownSeconds);
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "move_bridge_identity_not_confirmed", plan.Summary, windowId);
                VanguardClientDiagnosticsLog.Warning(StatusTag,
                    $"VANGUARD_CLOSE_COMMAND_IDENTITY_REJECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(leaseId)}; request={VanguardMovementContractPolicy.CloseCohesionMicroAdjust}; commandResult={Safe(commandResult)}; doctrine=movement_lease_requires_exact_owned_command_generation; tag={VanguardMedicalCohesionStatusTags.MovementLeaseIdentity}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
                continue;
            }

            var lease = new CloseCohesionLeaseState
            {
                LeaseId = leaseId,
                WindowId = windowId,
                OperatorId = snapshot.OperatorId,
                BotProfileId = snapshot.BotProfileId,
                CommandGeneration = ownedCommand.Generation,
                Anchor = plan.Anchor,
                AnchorRadiusMeters = VanguardMovementAuthorityDoctrine.CloseCohesionAnchorRadiusMeters,
                StartedAtUtc = now,
                MinUntilUtc = now + TimeSpan.FromSeconds(1.75d),
                MaxUntilUtc = maxUntil,
                NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CloseCohesionNoProgressSeconds),
                LastProgressAtUtc = now,
                InitialAnchorDistance = HorizontalDistance(botPosition, plan.Anchor),
                LastAnchorDistance = HorizontalDistance(botPosition, plan.Anchor),
                InitialOwnerDistance = snapshot.SquadCohesion.OperatorDistanceToOwner,
                LastOwnerDistance = snapshot.SquadCohesion.OperatorDistanceToOwner,
                LastWorldPosition = botPosition,
                LastWorldSampleAtUtc = now,
                PhysicalBlockedSinceUtc = DateTimeOffset.MinValue,
                PhysicalRestartCount = 0,
                TargetOwnerDistance = plan.OwnerDistance,
                PathDistanceMeters = plan.BotPathDistance,
                PlanSummary = plan.Summary
            };

            lock (Sync)
            {
                ActiveByBotProfileId[snapshot.BotProfileId] = lease;
            }

            VanguardMainIntentScheduler.MarkCloseCohesionStarted(snapshot.BotProfileId, leaseId, now, lease.Summary, windowId);
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_CLOSE_COHESION_STARTED {lease.Summary}; plan={Safe(plan.Summary)}; applyOnce=true; sprint=false; radialLanePreserved=true; noSlotRedistribution=true; tag={StatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
            return;
        }
    }

    private static void TickActiveLeases(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        CloseCohesionLeaseState[] active;
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

            if (IsSoftCloseCompletion(snapshot, lease, now, out var softCompletionReason))
            {
                FinishLease(lease, now, "Completed", softCompletionReason, failureCooldown: false, snapshot.DecisionSignature);
                continue;
            }

            string interrupt = CheckInterrupt(snapshot, lease);
            if (!string.Equals(interrupt, "none", StringComparison.OrdinalIgnoreCase))
            {
                FinishLease(lease, now, "Interrupted", interrupt, failureCooldown: true, snapshot.DecisionSignature);
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(lease.BotProfileId, out var record) || record.BotOwner == null || record.BotOwner.IsDead)
            {
                FinishLease(lease, now, "Interrupted", "bot_owner_missing_or_dead", failureCooldown: true, snapshot.DecisionSignature);
                continue;
            }

            Vector3 botPosition = ResolveBotPosition(record.BotOwner);
            float anchorDistance = HorizontalDistance(botPosition, lease.Anchor);
            float ownerDistance = snapshot.SquadCohesion.OperatorDistanceToOwner;
            var mutable = lease;
            bool anchorProgress = anchorDistance < mutable.LastAnchorDistance - VanguardMovementAuthorityDoctrine.CloseCohesionSoftProgressGainMeters;
            bool ownerProgress = ownerDistance < mutable.LastOwnerDistance - VanguardMovementAuthorityDoctrine.CloseCohesionOwnerProgressGainMeters
                || ownerDistance < mutable.InitialOwnerDistance - Math.Max(1.25f, VanguardMovementAuthorityDoctrine.CloseCohesionOwnerProgressGainMeters * 1.5f);
            TimeSpan physicalSampleAge = now - mutable.LastWorldSampleAtUtc;
            var physical = VanguardMovementProgressEvaluator.EvaluatePhysical(
                mutable.LastWorldPosition,
                botPosition,
                mutable.LastAnchorDistance,
                anchorDistance,
                snapshot.RealSpeed,
                movementExpected: true,
                physicalSampleAge);
            if (physicalSampleAge >= TimeSpan.FromSeconds(0.45d))
            {
                mutable.LastWorldPosition = botPosition;
                mutable.LastWorldSampleAtUtc = now;
            }

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

                mutable.LastProgressAtUtc = now;
                mutable.NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CloseCohesionNoProgressSeconds);
                mutable.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
                VanguardMainIntentScheduler.ReportPrimaryProgress(lease.BotProfileId, now, "close_cohesion_" + physical.ProgressKind, mutable.Summary, lease.WindowId);
                LogThrottled("progress|" + lease.BotProfileId, now,
                    $"VANGUARD_CLOSE_COHESION_PROGRESS {mutable.Summary}; ownerDistance={ownerDistance:0.0}; anchorDistance={anchorDistance:0.0}; progress={Safe(physical.ProgressKind)}; anchorContext={Bool(anchorProgress)}; ownerContext={Bool(ownerProgress)}; physical={Safe(physical.Summary)}; physicalTruth=true; tag={StatusTag}; runtimeTuningTag={RuntimeTuningStatusTag}");
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
                            || !string.Equals(restartedCommand.RequestKind, VanguardMovementContractPolicy.CloseCohesionMicroAdjust, StringComparison.OrdinalIgnoreCase))
                        {
                            FinishLease(mutable, now, "Failed", "physical_restart_identity_lost", failureCooldown: true, snapshot.DecisionSignature);
                            continue;
                        }

                        mutable.PhysicalRestartCount++;
                        mutable.CommandGeneration = restartedCommand.Generation;
                        mutable.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
                        mutable.LastWorldPosition = botPosition;
                        mutable.LastWorldSampleAtUtc = now;
                        mutable.NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CloseCohesionNoProgressSeconds);
                        lock (Sync)
                        {
                            ActiveByBotProfileId[lease.BotProfileId] = mutable;
                        }
                        LogThrottled("physicalRestart|" + lease.BotProfileId, now,
                            $"VANGUARD_CLOSE_COHESION_PHYSICAL_RESTART {mutable.Summary}; physical={Safe(physical.Summary)}; result={Safe(restartResult)}; progressClaimed=false; boundedRestartCount=1; tag={VanguardMedicalMovementStatusTags.PhysicalCohesionTruth}");
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
            }
            else if (physicalSampleAge >= TimeSpan.FromSeconds(0.45d))
            {
                mutable.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
            }

            lock (Sync)
            {
                ActiveByBotProfileId[lease.BotProfileId] = mutable;
            }

            if (anchorDistance <= lease.AnchorRadiusMeters && now >= lease.MinUntilUtc)
            {
                FinishLease(mutable, now, "Completed", "anchor_reached", failureCooldown: false, snapshot.DecisionSignature);
                continue;
            }

            if (ownerDistance <= Math.Max(lease.TargetOwnerDistance + VanguardMovementAuthorityDoctrine.CloseCohesionSoftCompleteExtraMeters, VanguardMovementAuthorityDoctrine.CloseCohesionIndoorTargetMeters + 5.0f) && now >= lease.MinUntilUtc)
            {
                FinishLease(mutable, now, "Completed", "owner_distance_recovered", failureCooldown: false, snapshot.DecisionSignature);
                continue;
            }

            if (now >= lease.MaxUntilUtc)
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

    private static bool IsCloseCohesionContract(OperatorDecisionSnapshot snapshot)
    {
        return snapshot.Alive
            && snapshot.MovementAuthority.BrokerPlan.Contract.RequestKind == VanguardMovementContractPolicy.CloseCohesionMicroAdjust
            && snapshot.MovementAuthority.BrokerPlan.LeasePlan.Eligible
            && snapshot.MovementAuthority.BrokerPlan.LeasePlan.ApplyEnabled;
    }

    private static string CheckStartGate(OperatorDecisionSnapshot snapshot, CloseCohesionLeaseState? currentLease = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
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

        if (combatProductive && VanguardCombatAwarenessBridge.HasFreshSquadCombatContact(snapshot, DateTimeOffset.UtcNow, out var squadContactReason))
        {
            return "fresh_productive_squad_contact:" + squadContactReason + ":" + productiveReason;
        }

        if (VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(snapshot))
        {
            return "stationary_medical_authority";
        }

        if (VanguardMovementAuthorityDoctrine.ShouldPreemptWeakCohesionForHardReturn(snapshot, out var hardReturnReason)
            || snapshot.MovementAuthority.HardOutsideBubble
            || snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.HardCorrectionMeters)
        {
            return "hard_return_higher_priority:" + hardReturnReason;
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
                    VanguardMovementContractPolicy.CloseCohesionMicroAdjust,
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
            return "active_movement_contract_preserved:" + contractReason;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner < VanguardMovementAuthorityDoctrine.CloseCohesionStartMinMeters)
        {
            return "already_close";
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner > VanguardMovementAuthorityDoctrine.CloseCohesionStartMaxMeters)
        {
            return "outside_close_cohesion_band";
        }

        return "none";
    }

    private static string CheckInterrupt(OperatorDecisionSnapshot snapshot, CloseCohesionLeaseState lease)
    {
        string gate = CheckStartGate(snapshot, lease);
        if (string.Equals(gate, "already_close", StringComparison.OrdinalIgnoreCase))
        {
            return "none";
        }

        if (string.Equals(gate, "outside_close_cohesion_band", StringComparison.OrdinalIgnoreCase)
            && snapshot.SquadCohesion.OperatorDistanceToOwner < VanguardMovementAuthorityDoctrine.CombatCohesionForcedCatchupMeters)
        {
            return "none";
        }

        if (!string.Equals(gate, "none", StringComparison.OrdinalIgnoreCase))
        {
            return gate;
        }

        if (snapshot.Threat.DirectThreat)
        {
            return "direct_threat";
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.CombatCohesionForcedCatchupMeters)
        {
            return "forced_catchup_preempts_close_cohesion";
        }

        return "none";
    }

    private static bool NeedsExternalPreempt(OperatorDecisionSnapshot snapshot)
    {
        return snapshot.Orbit.Active
            || snapshot.Movement.HasPath == true
            || snapshot.Looting.HasActiveLootable == true;
    }

    private static float ScoreStartCandidate(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot == null || !IsCloseCohesionContract(snapshot))
        {
            return -1f;
        }

        float distance = snapshot.SquadCohesion.OperatorDistanceToOwner;
        float score = distance;
        if (distance >= VanguardMovementAuthorityDoctrine.CloseCohesionForceStartMeters) score += 12f;
        if (!snapshot.SquadCohesion.UsefulPosition) score += 8f;
        if (snapshot.SquadCohesion.SectorDuplicate) score += 5f;
        if (snapshot.SquadCohesion.RearOverstacked) score += 5f;
        if (snapshot.Orbit.Active) score += 3f;
        return score;
    }

    private static bool TryResolveCloseAnchor(OperatorDecisionSnapshot snapshot, Vector3 botPosition, out CloseCohesionPlan plan)
    {
        plan = CloseCohesionPlan.Invalid("not_evaluated");
        if (!snapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            plan = CloseCohesionPlan.Invalid("owner_position_missing");
            return false;
        }

        Vector3 owner = snapshot.SquadCohesion.OwnerPosition.Value;
        Vector3 forward = snapshot.SquadCohesion.OwnerForward.HasValue ? Flatten(snapshot.SquadCohesion.OwnerForward.Value) : Vector3.forward;
        if (forward.sqrMagnitude <= 0.001f)
        {
            forward = Vector3.forward;
        }
        forward.Normalize();

        Vector3 radial = Flatten(botPosition - owner);
        if (radial.sqrMagnitude <= 0.25f)
        {
            radial = -forward;
        }
        radial.Normalize();

        float targetDistance = TargetDistance(snapshot);
        float relaxedTargetDistance = RelaxedTargetDistance(snapshot);
        var candidates = new List<Vector3>();
        AddRadialCandidates(candidates, owner, radial, targetDistance, new[] { 0.0f, 12.0f, -12.0f, 24.0f, -24.0f, 36.0f, -36.0f });
        AddRadialCandidates(candidates, owner, radial, relaxedTargetDistance, new[] { 0.0f, 18.0f, -18.0f, 36.0f, -36.0f, 54.0f, -54.0f });
        candidates.Add(owner - forward * Math.Min(relaxedTargetDistance, 16.0f));
        candidates.Add(owner + Rotate(-forward, 18.0f) * Math.Min(relaxedTargetDistance, 18.0f));
        candidates.Add(owner + Rotate(-forward, -18.0f) * Math.Min(relaxedTargetDistance, 18.0f));

        CloseCohesionCandidateScore best = CloseCohesionCandidateScore.Invalid("no_candidate");
        foreach (var raw in candidates)
        {
            if (!TryScoreAnchor(snapshot, owner, botPosition, raw, targetDistance, relaxed: false, out var scored))
            {
                if (scored.Score > best.Score)
                {
                    best = scored;
                }
                continue;
            }

            if (!best.Valid || scored.Score > best.Score)
            {
                best = scored;
            }
        }

        if (!best.Valid)
        {
            foreach (var raw in candidates)
            {
                if (!TryScoreAnchor(snapshot, owner, botPosition, raw, relaxedTargetDistance, relaxed: true, out var scored))
                {
                    if (scored.Score > best.Score)
                    {
                        best = scored;
                    }
                    continue;
                }

                if (!best.Valid || scored.Score > best.Score)
                {
                    best = scored;
                }
            }
        }

        if (!best.Valid)
        {
            plan = CloseCohesionPlan.Invalid(best.Reason);
            return false;
        }

        plan = new CloseCohesionPlan(true, best.Anchor, best.OwnerDistance, best.OwnerPathDistance, best.BotPathDistance, best.Reason, best.PathSummary, best.Score);
        return true;
    }

    private static bool TryScoreAnchor(OperatorDecisionSnapshot snapshot, Vector3 owner, Vector3 botPosition, Vector3 rawAnchor, float targetDistance, bool relaxed, out CloseCohesionCandidateScore score)
    {
        score = CloseCohesionCandidateScore.Invalid("not_scored");
        if (!TrySample(rawAnchor, relaxed ? 4.25f : 2.75f, out var sampled))
        {
            score = CloseCohesionCandidateScore.Invalid("reject_navmesh_sample_failed");
            return false;
        }

        float ownerDirect = HorizontalDistance(owner, sampled);
        float minOwnerRadius = relaxed ? 6.0f : 7.0f;
        float maxOwnerRadius = relaxed ? Math.Max(targetDistance + 14.0f, 30.0f) : Math.Max(targetDistance + 8.0f, 24.0f);
        if (ownerDirect < minOwnerRadius || ownerDirect > maxOwnerRadius)
        {
            score = CloseCohesionCandidateScore.Invalid("reject_owner_radius_" + ownerDirect.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        float botDirect = HorizontalDistance(botPosition, sampled);
        if (botDirect < (relaxed ? 4.25f : 5.5f))
        {
            score = CloseCohesionCandidateScore.Invalid("reject_delta_too_small_" + botDirect.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (!TryPath(owner, sampled, out var ownerPathDistance, out var ownerCorners, out var ownerPathStatus))
        {
            score = CloseCohesionCandidateScore.Invalid("reject_owner_path_" + ownerPathStatus);
            return false;
        }

        if (!TryPath(botPosition, sampled, out var botPathDistance, out var botCorners, out var botPathStatus))
        {
            score = CloseCohesionCandidateScore.Invalid("reject_bot_path_" + botPathStatus);
            return false;
        }

        float ownerRatio = ownerDirect <= 0.25f ? 1.0f : ownerPathDistance / ownerDirect;
        bool indoor = IsIndoor(snapshot);
        float maxOwnerPath = relaxed ? (indoor ? 28.0f : 48.0f) : (indoor ? 23.0f : 34.0f);
        float maxBotPath = relaxed ? (indoor ? 56.0f : 76.0f) : (indoor ? 44.0f : 58.0f);
        int maxBotCorners = relaxed ? (indoor ? 9 : 14) : (indoor ? 7 : 10);
        int maxOwnerCorners = relaxed ? (indoor ? 8 : 11) : (indoor ? 6 : 8);
        float maxOwnerRatio = relaxed ? (indoor ? 2.65f : 3.15f) : (indoor ? 2.10f : 2.35f);
        if (ownerRatio > maxOwnerRatio)
        {
            score = CloseCohesionCandidateScore.Invalid("reject_owner_detour_ratio_" + ownerRatio.ToString("0.00", CultureInfo.InvariantCulture));
            return false;
        }

        if (ownerPathDistance > maxOwnerPath)
        {
            score = CloseCohesionCandidateScore.Invalid("reject_owner_path_too_long_" + ownerPathDistance.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (botPathDistance > maxBotPath)
        {
            score = CloseCohesionCandidateScore.Invalid("reject_bot_path_too_long_" + botPathDistance.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (botCorners > maxBotCorners || ownerCorners > maxOwnerCorners)
        {
            score = CloseCohesionCandidateScore.Invalid("reject_too_many_corners_owner_" + ownerCorners.ToString(CultureInfo.InvariantCulture) + "_bot_" + botCorners.ToString(CultureInfo.InvariantCulture));
            return false;
        }

        float value = 100.0f;
        value -= Math.Abs(ownerDirect - targetDistance) * 3.0f;
        value -= botPathDistance * 0.40f;
        value -= ownerPathDistance * 0.55f;
        value -= Math.Max(0.0f, ownerRatio - 1.0f) * 12.0f;
        value -= botCorners * 0.75f;
        value -= ownerCorners * 0.50f;

        string pathSummary = "ownerDirect=" + ownerDirect.ToString("0.0", CultureInfo.InvariantCulture)
            + ";ownerPath=" + ownerPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";ownerRatio=" + ownerRatio.ToString("0.00", CultureInfo.InvariantCulture)
            + ";ownerCorners=" + ownerCorners.ToString(CultureInfo.InvariantCulture)
            + ";botDirect=" + botDirect.ToString("0.0", CultureInfo.InvariantCulture)
            + ";botPath=" + botPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";botCorners=" + botCorners.ToString(CultureInfo.InvariantCulture)
            + ";target=" + targetDistance.ToString("0.0", CultureInfo.InvariantCulture);
        score = new CloseCohesionCandidateScore(true, sampled, ownerDirect, ownerPathDistance, botPathDistance, relaxed ? "accepted_relaxed_radial_lane_anchor" : "accepted_radial_lane_anchor", pathSummary + ";relaxed=" + Bool(relaxed), value - (relaxed ? 5.0f : 0.0f));
        return true;
    }

    private static bool IsSoftCloseCompletion(OperatorDecisionSnapshot snapshot, CloseCohesionLeaseState lease, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (now < lease.MinUntilUtc)
        {
            return false;
        }

        float ownerDistance = snapshot.SquadCohesion.OperatorDistanceToOwner;
        if (ownerDistance <= VanguardMovementAuthorityDoctrine.CloseCohesionStartMinMeters)
        {
            reason = "already_close_soft_completed";
            return true;
        }

        float softCompleteDistance = Math.Max(
            lease.TargetOwnerDistance + VanguardMovementAuthorityDoctrine.CloseCohesionSoftCompleteExtraMeters,
            VanguardMovementAuthorityDoctrine.CloseCohesionIndoorTargetMeters + 5.0f);
        if (ownerDistance <= softCompleteDistance && ownerDistance < lease.InitialOwnerDistance - 1.25f)
        {
            reason = "owner_distance_soft_recovered";
            return true;
        }

        return false;
    }

    private static bool CanSoftDriveAfterNonCriticalPreempt(OperatorDecisionSnapshot snapshot, VanguardExternalPreemptResult preempt, out string reason)
    {
        reason = "none";
        if (preempt.IsCombatDefer || VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot))
        {
            reason = "combat_or_direct_threat";
            return false;
        }

        if (snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true || snapshot.Looting.HasActiveLootable == true)
        {
            reason = "loot_activity_not_soft_drivable";
            return false;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner < VanguardMovementAuthorityDoctrine.CloseCohesionOrbitPreemptMinMeters)
        {
            reason = "distance_too_low_for_soft_orbit_drive";
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

        if (preempt.After.MoverMoving && preempt.After.RealSpeed > 0.90f && snapshot.SquadCohesion.OperatorDistanceToOwner < VanguardMovementAuthorityDoctrine.CloseCohesionForceStartMeters)
        {
            reason = "external_mover_still_productive";
            return false;
        }

        if (preempt.After.LootingBotsActive || preempt.After.LootingBotsTaskRunning || preempt.After.LootingBotsHasActiveLootable)
        {
            reason = "loot_after_preempt_active";
            return false;
        }

        reason = preempt.Outcome == VanguardExternalPreemptOutcome.FailedPathStillActive
            ? "noncritical_path_residue_soft_drive"
            : preempt.After.OrbitBrainLayerActive
                ? "noncritical_orbit_layer_residue_soft_drive"
                : "noncritical_orbit_objective_residue_soft_drive";
        return true;
    }

    private static void FinishLease(CloseCohesionLeaseState lease, DateTimeOffset now, string outcome, string reason, bool failureCooldown, string snapshotSignature)
    {
        lock (Sync)
        {
            ActiveByBotProfileId.Remove(lease.BotProfileId);
        }

        VanguardReturnMovementCommandStore.ClearOwned(lease.BotProfileId, lease.LeaseId, lease.StartedAtUtc, "close_cohesion_finished:" + reason);
        float cooldownSeconds = failureCooldown
            ? VanguardMovementAuthorityDoctrine.CloseCohesionFailureCooldownSeconds
            : VanguardMovementAuthorityDoctrine.CloseCohesionSuccessCooldownSeconds;
        SetCooldown(lease.BotProfileId, now, cooldownSeconds);
        VanguardMainIntentScheduler.FinishPrimaryWindow(lease.BotProfileId, now, outcome, reason, lease.Summary, lease.WindowId);
        string log = string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase)
            ? "VANGUARD_CLOSE_COHESION_COMPLETED"
            : "VANGUARD_CLOSE_COHESION_ABORTED";
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"{log} {lease.Summary}; outcome={Safe(outcome)}; reason={Safe(reason)}; snapshot={Safe(snapshotSignature)}; cooldown={cooldownSeconds:0.0}; tag={StatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
    }

    private static void SetCooldown(string botProfileId, DateTimeOffset now, float seconds)
    {
        lock (Sync)
        {
            CooldownByBotProfileId[botProfileId] = now + TimeSpan.FromSeconds(Math.Max(1.0f, seconds));
        }
    }

    private static void AddRadialCandidates(List<Vector3> candidates, Vector3 owner, Vector3 radial, float distance, float[] angles)
    {
        foreach (float angle in angles)
        {
            candidates.Add(owner + Rotate(radial, angle) * distance);
        }
    }

    private static float RelaxedTargetDistance(OperatorDecisionSnapshot snapshot)
    {
        return IsIndoor(snapshot)
            ? VanguardMovementAuthorityDoctrine.CloseCohesionIndoorRelaxedTargetMeters
            : VanguardMovementAuthorityDoctrine.CloseCohesionOutdoorRelaxedTargetMeters;
    }

    private static float TargetDistance(OperatorDecisionSnapshot snapshot)
    {
        return IsIndoor(snapshot)
            ? VanguardMovementAuthorityDoctrine.CloseCohesionIndoorTargetMeters
            : VanguardMovementAuthorityDoctrine.CloseCohesionOutdoorTargetMeters;
    }

    private static bool IsIndoor(OperatorDecisionSnapshot snapshot)
    {
        string env = snapshot.SquadCohesion.TacticalEnvironmentKind ?? string.Empty;
        return env.IndexOf("corridor", StringComparison.OrdinalIgnoreCase) >= 0
            || env.IndexOf("room", StringComparison.OrdinalIgnoreCase) >= 0
            || env.IndexOf("urban_wraparound", StringComparison.OrdinalIgnoreCase) >= 0;
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
        distance = 0f;
        corners = 0;
        status = "none";
        if (!TrySample(start, 3.0f, out var sampledStart))
        {
            status = "start_sample_failed";
            return false;
        }

        if (!TrySample(end, 3.0f, out var sampledEnd))
        {
            status = "end_sample_failed";
            return false;
        }

        var path = new NavMeshPath();
        bool calculated = NavMesh.CalculatePath(sampledStart, sampledEnd, NavMesh.AllAreas, path);
        corners = path.corners == null ? 0 : path.corners.Length;
        distance = PathDistance(path);
        status = "calculated=" + Bool(calculated) + ";status=" + path.status + ";corners=" + corners.ToString(CultureInfo.InvariantCulture);
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

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }

    private readonly struct CloseCohesionPlan
    {
        public CloseCohesionPlan(bool valid, Vector3 anchor, float ownerDistance, float ownerPathDistance, float botPathDistance, string reason, string pathSummary, float score)
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

        public static CloseCohesionPlan Invalid(string reason) => new(false, Vector3.zero, 0f, 0f, 0f, reason, "none", -9999f);

        public bool Valid { get; }
        public Vector3 Anchor { get; }
        public float OwnerDistance { get; }
        public float OwnerPathDistance { get; }
        public float BotPathDistance { get; }
        public string Reason { get; }
        public string PathSummary { get; }
        public float Score { get; }

        public string Summary => "ownerDistance=" + OwnerDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";ownerPath=" + OwnerPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";botPath=" + BotPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";score=" + Score.ToString("0.0", CultureInfo.InvariantCulture)
            + ";reason=" + Safe(Reason)
            + ";path=" + Safe(PathSummary);
    }

    private readonly struct CloseCohesionCandidateScore
    {
        public CloseCohesionCandidateScore(bool valid, Vector3 anchor, float ownerDistance, float ownerPathDistance, float botPathDistance, string reason, string pathSummary, float score)
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

        public static CloseCohesionCandidateScore Invalid(string reason) => new(false, Vector3.zero, 0f, 0f, 0f, reason, "none", -9999f);

        public bool Valid { get; }
        public Vector3 Anchor { get; }
        public float OwnerDistance { get; }
        public float OwnerPathDistance { get; }
        public float BotPathDistance { get; }
        public string Reason { get; }
        public string PathSummary { get; }
        public float Score { get; }
    }

    private struct CloseCohesionLeaseState
    {
        public string LeaseId;
        public string WindowId;
        public string OperatorId;
        public string BotProfileId;
        public long CommandGeneration;
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
        public Vector3 LastWorldPosition;
        public DateTimeOffset LastWorldSampleAtUtc;
        public DateTimeOffset PhysicalBlockedSinceUtc;
        public int PhysicalRestartCount;
        public float TargetOwnerDistance;
        public float PathDistanceMeters;
        public string PlanSummary;

        public string Summary => "lease=" + Safe(LeaseId)
            + ";window=" + Safe(WindowId)
            + ";operator=" + Safe(OperatorId)
            + ";botProfile=" + Safe(BotProfileId)
            + ";commandGeneration=" + CommandGeneration.ToString(CultureInfo.InvariantCulture)
            + ";anchor=" + Anchor.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + Anchor.y.ToString("0.0", CultureInfo.InvariantCulture) + "," + Anchor.z.ToString("0.0", CultureInfo.InvariantCulture)
            + ";radius=" + AnchorRadiusMeters.ToString("0.0", CultureInfo.InvariantCulture)
            + ";initialAnchorDist=" + InitialAnchorDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";lastAnchorDist=" + LastAnchorDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";initialOwnerDist=" + InitialOwnerDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";lastOwnerDist=" + LastOwnerDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";physicalRestarts=" + PhysicalRestartCount.ToString(CultureInfo.InvariantCulture)
            + ";targetOwnerDist=" + TargetOwnerDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";path=" + PathDistanceMeters.ToString("0.0", CultureInfo.InvariantCulture)
            + ";plan=" + Safe(PlanSummary);
    }
}
#endif
