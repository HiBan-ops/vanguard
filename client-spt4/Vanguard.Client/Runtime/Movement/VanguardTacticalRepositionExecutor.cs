#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EFT;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Movement.Brain;

// Responsibility: executes a short tactical reposition that has already been requested by the movement/decision layer during active squad combat.
// Flow: Eligible intents are scored, the selected target is checked against nearby combat pressure and current safety, a temporary movement lease drives toward the target, and progress/interrupts determine success, cooldown or cancellation.
// Authority boundary: the executor does not invent tactical goals or enemy truth; it consumes an existing reposition contract and yields immediately to higher-priority combat-safety, grenade, medical or hard-return ownership.
// Invariant: only one active reposition lease may own an Operator, and each lease is target/generation/timeout bounded with explicit terminal cleanup.
namespace Vanguard.Client.Runtime.Movement;

internal static class VanguardTacticalRepositionExecutor
{
    public const string StatusTag = "VANGUARD_TACTICAL_REPOSITION_ACTIVE_OK";
    public const string ClientBuildStatusTag = "VANGUARD_CLIENT_BUILD_OK";
    public const string TacticalTuningStatusTag = "VANGUARD_TACTICAL_TUNING_OK";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, TacticalRepositionLeaseState> ActiveByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> CooldownByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> SuccessCooldownByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(0.35d);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(1.75d);
    private static DateTimeOffset nextTickAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            ActiveByBotProfileId.Clear();
            CooldownByBotProfileId.Clear();
            SuccessCooldownByBotProfileId.Clear();
            LastLogByKey.Clear();
        }

        bootLogged = false;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_TACTICAL_REPOSITION_RESET reason={Safe(reason)}; active=0; cooldowns=cleared; doctrine=environment_aware_sector_anchor_apply_once; tag={StatusTag}; clientBuildTag={ClientBuildStatusTag}; tuningTag={TacticalTuningStatusTag}; solverTag={VanguardTacticalPlacementSolver.StatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
    }

    public static void Tick()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now < nextTickAtUtc)
        {
            return;
        }

        nextTickAtUtc = now + TickInterval;
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_TACTICAL_REPOSITION_BOOT enabled={Bool(VanguardMovementAuthorityDoctrine.TacticalRepositionActiveEnabled)}; scope=inside_bubble_environment_aware_sector_reposition; backend=BigBrain_GoToSomePointData; applyPolicy=apply_once_no_tick_reapply; excludes=directThreat_medical_hardReturn_orbit_loot_existingPath; minDelta={VanguardMovementAuthorityDoctrine.TacticalRepositionMinDeltaMeters:0.0}; cooldown={VanguardMovementAuthorityDoctrine.TacticalRepositionCooldownSeconds:0.0}; max={VanguardMovementAuthorityDoctrine.TacticalRepositionMaxDurationSeconds:0.0}; successCooldown={VanguardMovementAuthorityDoctrine.TacticalRepositionSuccessCooldownSeconds:0.0}; squadPressureMeters={VanguardMovementAuthorityDoctrine.TacticalSquadPressureBlockMeters:0.0}; botPathPolicy=env_capped; tag={StatusTag}; clientBuildTag={ClientBuildStatusTag}; tuningTag={TacticalTuningStatusTag}; solverTag={VanguardTacticalPlacementSolver.StatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}; build={VanguardBuildVersion.BuildLabel}");
        }

        var snapshots = VanguardOperatorDecisionSnapshotService.GetLatestSnapshots();
        TickActiveLeases(snapshots, now);
        if (!VanguardMovementAuthorityDoctrine.TacticalRepositionActiveEnabled)
        {
            return;
        }

        if (VanguardRuntimeFrameBudgetGuard.ShouldRunOptional(
            "TacticalRepositionPlanning",
            now,
            TimeSpan.FromSeconds(1.5d),
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

            if (!IsTacticalContract(snapshot))
            {
                continue;
            }

            bool activeAlready;
            bool inCooldown;
            bool recentSuccessCooldown;
            DateTimeOffset cooldownUntil;
            lock (Sync)
            {
                activeAlready = ActiveByBotProfileId.ContainsKey(snapshot.BotProfileId);
                inCooldown = CooldownByBotProfileId.TryGetValue(snapshot.BotProfileId, out cooldownUntil) && cooldownUntil > now;
                recentSuccessCooldown = SuccessCooldownByBotProfileId.TryGetValue(snapshot.BotProfileId, out var successUntil) && successUntil > now;
            }

            if (activeAlready)
            {
                continue;
            }

            if (inCooldown)
            {
                LogThrottled("rejectCooldown|" + snapshot.BotProfileId, now,
                    $"VANGUARD_TACTICAL_REPOSITION_REJECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={(recentSuccessCooldown ? "reject_recent_reposition_success" : "reject_recent_reposition_cooldown")}; cooldownRemaining={(cooldownUntil - now).TotalSeconds:0.0}; tag={TacticalTuningStatusTag}; tacticalTag={StatusTag}");
                continue;
            }

            if (IsAlreadyUsefulAndClose(snapshot))
            {
                LogThrottled("rejectUseful|" + snapshot.BotProfileId, now,
                    $"VANGUARD_TACTICAL_REPOSITION_REJECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=reject_already_useful; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; sector={Safe(snapshot.SquadCohesion.Sector)}; useful={Bool(snapshot.SquadCohesion.UsefulPosition)}; duplicate={Bool(snapshot.SquadCohesion.SectorDuplicate)}; topology={Safe(snapshot.SquadCohesion.SectorTopologyReason)}; tag={TacticalTuningStatusTag}; tacticalTag={StatusTag}");
                continue;
            }

            if (HasNearbySquadCombatPressure(snapshots, snapshot, out var pressureReason))
            {
                LogThrottled("rejectPressure|" + snapshot.BotProfileId + "|" + pressureReason, now,
                    $"VANGUARD_TACTICAL_REPOSITION_REJECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=reject_squad_pressure:{Safe(pressureReason)}; tag={TacticalTuningStatusTag}; tacticalTag={StatusTag}");
                continue;
            }

            if (VanguardMainIntentScheduler.HasBlockingPrimaryWindow(snapshot.BotProfileId, now, out var blockingReason))
            {
                LogThrottled("blocked|" + snapshot.BotProfileId, now,
                    $"VANGUARD_TACTICAL_REPOSITION_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=primary_window_busy:{Safe(blockingReason)}; tuningTag={TacticalTuningStatusTag}; tag={StatusTag}; solverTag={VanguardTacticalPlacementSolver.StatusTag}");
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record) || record.BotOwner == null || record.BotOwner.IsDead)
            {
                continue;
            }

            if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner("MovementPathPlan", snapshot.OwnerProfileId, 1, 2, out _))
            {
                // Do not open a scheduler window until the frame can afford the placement solve.
                return;
            }

            if (!VanguardMainIntentScheduler.TryOpenTacticalReposition(snapshot, now, out var windowId, out var openReason))
            {
                LogThrottled("openDenied|" + snapshot.BotProfileId + "|" + openReason, now,
                    $"VANGUARD_TACTICAL_REPOSITION_OPEN_DENIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(openReason)}; tuningTag={TacticalTuningStatusTag}; tag={StatusTag}; solverTag={VanguardTacticalPlacementSolver.StatusTag}");
                continue;
            }

            Vector3 botPosition = ResolveBotPosition(record.BotOwner);
            if (!VanguardTacticalPlacementSolver.TryResolve(snapshot, botPosition, now, out var plan))
            {
                SetCooldown(snapshot.BotProfileId, now, VanguardMovementAuthorityDoctrine.TacticalRepositionCooldownSeconds);
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "tactical_anchor_failed:" + plan.Reason, plan.Summary, windowId);
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"VANGUARD_TACTICAL_ANCHOR_REJECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(plan.Reason)}; env={Safe(snapshot.SquadCohesion.TacticalEnvironmentKind)}; currentSector={Safe(snapshot.SquadCohesion.Sector)}; topology={Safe(snapshot.SquadCohesion.SectorTopologyReason)}; window={Safe(windowId)}; tuningTag={TacticalTuningStatusTag}; tag={StatusTag}; solverTag={VanguardTacticalPlacementSolver.StatusTag}");
                return;
            }

            string leaseId = "tactical_sector_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId);
            DateTimeOffset maxUntil = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TacticalRepositionMaxDurationSeconds);
            bool sprint = false;
            bool issued = VanguardReturnMovementCommandStore.Issue(
                leaseId,
                snapshot.OperatorId,
                snapshot.BotProfileId,
                plan.Anchor,
                VanguardMovementAuthorityDoctrine.TacticalAnchorRadiusMeters,
                sprint,
                now,
                maxUntil,
                VanguardMovementContractPolicy.TacticalRepositionToUsefulSector,
                plan.PathSummary,
                plan.BotPathDistance,
                out var commandResult);
            if (!issued)
            {
                SetCooldown(snapshot.BotProfileId, now, VanguardMovementAuthorityDoctrine.TacticalRepositionCooldownSeconds);
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "move_bridge_rejected:" + commandResult, plan.Summary, windowId);
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"VANGUARD_TACTICAL_COMMAND_REJECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; command={Safe(commandResult)}; plan={Safe(plan.Summary)}; window={Safe(windowId)}; tuningTag={TacticalTuningStatusTag}; tag={StatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
                return;
            }

            var lease = new TacticalRepositionLeaseState
            {
                LeaseId = leaseId,
                WindowId = windowId,
                OperatorId = snapshot.OperatorId,
                BotProfileId = snapshot.BotProfileId,
                DesiredSector = plan.DesiredSector,
                EnvironmentKind = plan.EnvironmentKind,
                Anchor = plan.Anchor,
                AnchorRadiusMeters = VanguardMovementAuthorityDoctrine.TacticalAnchorRadiusMeters,
                StartedAtUtc = now,
                MinUntilUtc = now + TimeSpan.FromSeconds(2.25d),
                MaxUntilUtc = maxUntil,
                NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TacticalRepositionNoProgressSeconds),
                LastProgressAtUtc = now,
                InitialAnchorDistance = HorizontalDistance(botPosition, plan.Anchor),
                LastAnchorDistance = HorizontalDistance(botPosition, plan.Anchor),
                LastWorldPosition = botPosition,
                LastWorldSampleAtUtc = now,
                PhysicalBlockedSinceUtc = DateTimeOffset.MinValue,
                PhysicalRestartCount = 0,
                PathDistanceMeters = plan.BotPathDistance,
                PlanSummary = plan.Summary
            };

            lock (Sync)
            {
                ActiveByBotProfileId[snapshot.BotProfileId] = lease;
            }

            VanguardMainIntentScheduler.MarkTacticalRepositionStarted(snapshot.BotProfileId, leaseId, now, lease.Summary, windowId);
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_TACTICAL_REPOSITION_STARTED {lease.Summary}; plan={Safe(plan.Summary)}; applyOnce=true; sprint={Bool(sprint)}; tuningTag={TacticalTuningStatusTag}; tag={StatusTag}; solverTag={VanguardTacticalPlacementSolver.StatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
            return;
        }
    }

    private static void TickActiveLeases(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        TacticalRepositionLeaseState[] active;
        lock (Sync)
        {
            active = ActiveByBotProfileId.Values.ToArray();
        }

        for (int leaseIndex = 0; leaseIndex < active.Length; leaseIndex++)
        {
            var lease = active[leaseIndex];
            var snapshot = snapshots?.FirstOrDefault(item => string.Equals(item.BotProfileId, lease.BotProfileId, StringComparison.OrdinalIgnoreCase));
            if (snapshot == null)
            {
                FinishLease(lease, now, "Failed", "snapshot_missing", true, "none");
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(lease.BotProfileId, out var record) || record.BotOwner == null || record.BotOwner.IsDead)
            {
                FinishLease(lease, now, "Failed", "botowner_missing_or_dead", true, "none");
                continue;
            }

            string interrupt = CheckInterrupt(snapshot);
            if (!string.Equals(interrupt, "none", StringComparison.OrdinalIgnoreCase))
            {
                FinishLease(lease, now, "Interrupted", interrupt, true, snapshot.DecisionSignature);
                continue;
            }

            Vector3 botPosition = ResolveBotPosition(record.BotOwner);
            float anchorDistance = HorizontalDistance(botPosition, lease.Anchor);
            bool anchorReached = anchorDistance <= lease.AnchorRadiusMeters;
            bool usefulStable = now >= lease.MinUntilUtc && snapshot.SquadCohesion.UsefulPosition && snapshot.RealSpeed <= 0.35f;
            if (anchorReached || usefulStable)
            {
                FinishLease(lease, now, "Completed", anchorReached ? "anchor_reached" : "useful_sector_stable", false, snapshot.DecisionSignature);
                continue;
            }

            TimeSpan physicalSampleAge = now - lease.LastWorldSampleAtUtc;
            var physical = VanguardMovementProgressEvaluator.EvaluatePhysical(
                lease.LastWorldPosition,
                botPosition,
                lease.LastAnchorDistance,
                anchorDistance,
                snapshot.RealSpeed,
                true,
                physicalSampleAge);
            if (physicalSampleAge >= TimeSpan.FromSeconds(0.45d))
            {
                lease.LastWorldPosition = botPosition;
                lease.LastWorldSampleAtUtc = now;
            }
            if (physical.HasProgress)
            {
                if (physical.GoalGainMeters > 0f)
                {
                    lease.LastAnchorDistance = Math.Min(lease.LastAnchorDistance, anchorDistance);
                }
                lease.LastProgressAtUtc = now;
                lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TacticalRepositionNoProgressSeconds);
                lease.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
                lease.LastWorldPosition = botPosition;
                lease.LastWorldSampleAtUtc = now;
                lock (Sync)
                {
                    ActiveByBotProfileId[lease.BotProfileId] = lease;
                }

                VanguardMainIntentScheduler.ReportPrimaryProgress(lease.BotProfileId, now, "tactical_reposition_" + physical.ProgressKind, lease.Summary, lease.WindowId);
                LogThrottled("progress|" + lease.BotProfileId, now,
                    $"VANGUARD_TACTICAL_REPOSITION_PROGRESS {lease.Summary}; anchorDist={anchorDistance:0.00}; speed={snapshot.RealSpeed:0.00}; physical={Safe(physical.Summary)}; progress={Safe(physical.ProgressKind)}; useful={Bool(snapshot.SquadCohesion.UsefulPosition)}; physicalTag={VanguardPrimaryExecutionContract.PhysicalMovementProgressStatusTag}; tuningTag={TacticalTuningStatusTag}; tag={StatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
                continue;
            }

            if (physical.LocomotionBlocked)
            {
                if (lease.PhysicalBlockedSinceUtc == DateTimeOffset.MinValue)
                {
                    lease.PhysicalBlockedSinceUtc = now;
                }

                double blockedSeconds = Math.Max(0d, (now - lease.PhysicalBlockedSinceUtc).TotalSeconds);
                if (blockedSeconds >= 1.0d && lease.PhysicalRestartCount < 1)
                {
                    if (VanguardReturnMovementCommandStore.TryRestartOwned(lease.LeaseId, lease.BotProfileId, now, physical.Summary, out var restartResult))
                    {
                        lease.PhysicalRestartCount++;
                        lease.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
                        lease.LastWorldPosition = botPosition;
                        lease.LastWorldSampleAtUtc = now;
                        lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TacticalRepositionNoProgressSeconds);
                        lock (Sync)
                        {
                            ActiveByBotProfileId[lease.BotProfileId] = lease;
                        }
                        LogThrottled("physicalRestart|" + lease.BotProfileId, now,
                            $"VANGUARD_PHYSICAL_MOVEMENT_RESTART {lease.Summary}; physical={Safe(physical.Summary)}; result={Safe(restartResult)}; tag={VanguardPrimaryExecutionContract.PhysicalMovementProgressStatusTag}");
                        continue;
                    }

                    FinishLease(lease, now, "Failed", "physical_restart_rejected:" + restartResult, true, snapshot.DecisionSignature);
                    continue;
                }

                if (blockedSeconds >= 3.0d && lease.PhysicalRestartCount >= 1)
                {
                    FinishLease(lease, now, "Timeout", "locomotion_blocked_world_delta_after_restart:" + physical.Summary, true, snapshot.DecisionSignature);
                    continue;
                }
            }
            else if (physicalSampleAge >= TimeSpan.FromSeconds(0.45d))
            {
                lease.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
            }

            lock (Sync)
            {
                ActiveByBotProfileId[lease.BotProfileId] = lease;
            }

            if (now >= lease.NoProgressUntilUtc)
            {
                FinishLease(lease, now, "Timeout", "no_progress_timeout", true, snapshot.DecisionSignature);
                continue;
            }

            if (now >= lease.MaxUntilUtc)
            {
                FinishLease(lease, now, "Timeout", "max_window_expired", true, snapshot.DecisionSignature);
            }
        }
    }

    private static bool IsAlreadyUsefulAndClose(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.Alive)
        {
            return false;
        }

        if (!snapshot.SquadCohesion.UsefulPosition || !snapshot.SquadCohesion.SectorTopologyValid)
        {
            return false;
        }

        if (snapshot.SquadCohesion.RearOverstacked || !string.Equals(snapshot.SquadCohesion.SectorTopologyReason, "topology_valid_same_tactical_volume", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return snapshot.SquadCohesion.OperatorDistanceToOwner <= Math.Min(VanguardMovementAuthorityDoctrine.ActionRallyAcceptMeters, 34.0f);
    }

    private static bool HasNearbySquadCombatPressure(IReadOnlyList<OperatorDecisionSnapshot> snapshots, OperatorDecisionSnapshot candidate, out string reason)
    {
        reason = "none";
        if (snapshots == null || candidate == null)
        {
            return false;
        }

        foreach (var other in snapshots)
        {
            if (other == null || !other.Alive)
            {
                continue;
            }

            if (string.Equals(other.BotProfileId, candidate.BotProfileId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(other.OwnerProfileId, candidate.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (other.SquadCohesion.OperatorDistanceToOwner > VanguardMovementAuthorityDoctrine.TacticalSquadPressureBlockMeters)
            {
                continue;
            }

            if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(other))
            {
                reason = "operator=" + Safe(other.OperatorId)
                    + ":botProfile=" + Safe(other.BotProfileId)
                    + ":dist=" + other.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":classification=" + Safe(other.Threat.Classification);
                return true;
            }
        }

        return false;
    }

    private static string CheckInterrupt(OperatorDecisionSnapshot snapshot)
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

        if (!snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement)
        {
            return "owner_unreliable";
        }

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot))
        {
            return "true_direct_threat";
        }

        if (VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(snapshot))
        {
            return "stationary_medical_authority";
        }

        if (snapshot.MovementAuthority.HardOutsideBubble)
        {
            return "hard_outside_bubble_higher_priority";
        }

        return "none";
    }

    private static bool IsTacticalContract(OperatorDecisionSnapshot snapshot)
    {
        return snapshot.Alive
            && snapshot.MovementAuthority.BrokerPlan.Contract.RequestKind == VanguardMovementContractPolicy.TacticalRepositionToUsefulSector
            && snapshot.MovementAuthority.BrokerPlan.LeasePlan.Eligible
            && snapshot.MovementAuthority.BrokerPlan.LeasePlan.ApplyEnabled;
    }

    private static float ScoreStartCandidate(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot == null || !IsTacticalContract(snapshot))
        {
            return -1f;
        }

        float score = 10f;
        if (snapshot.SquadCohesion.SectorDuplicate) score += 8f;
        if (snapshot.SquadCohesion.RearOverstacked) score += 8f;
        if (!snapshot.SquadCohesion.UsefulPosition) score += 6f;
        if (!snapshot.SquadCohesion.SectorTopologyValid) score += 4f;
        score += Math.Min(12f, snapshot.SquadCohesion.OperatorDistanceToOwner * 0.10f);
        return score;
    }

    private static void FinishLease(TacticalRepositionLeaseState lease, DateTimeOffset now, string outcome, string reason, bool failureCooldown, string snapshotSignature)
    {
        lock (Sync)
        {
            ActiveByBotProfileId.Remove(lease.BotProfileId);
        }

        VanguardReturnMovementCommandStore.ClearOwned(lease.BotProfileId, lease.LeaseId, lease.StartedAtUtc, "tactical_reposition_finished:" + reason);
        float cooldownSeconds = failureCooldown
            ? VanguardMovementAuthorityDoctrine.TacticalRepositionCooldownSeconds
            : VanguardMovementAuthorityDoctrine.TacticalRepositionSuccessCooldownSeconds;
        SetCooldown(lease.BotProfileId, now, cooldownSeconds);
        if (!failureCooldown)
        {
            lock (Sync)
            {
                SuccessCooldownByBotProfileId[lease.BotProfileId] = now + TimeSpan.FromSeconds(cooldownSeconds);
            }
        }

        VanguardMainIntentScheduler.FinishPrimaryWindow(lease.BotProfileId, now, outcome, reason, lease.Summary, lease.WindowId);
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_TACTICAL_REPOSITION_FINISHED {lease.Summary}; outcome={Safe(outcome)}; reason={Safe(reason)}; snapshot={Safe(snapshotSignature)}; cooldown={cooldownSeconds:0.00}; tuningTag={TacticalTuningStatusTag}; tag={StatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
    }

    private static void SetCooldown(string botProfileId, DateTimeOffset now, float seconds)
    {
        lock (Sync)
        {
            CooldownByBotProfileId[botProfileId] = now + TimeSpan.FromSeconds(Math.Max(1f, seconds));
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

    private struct TacticalRepositionLeaseState
    {
        public string LeaseId;
        public string WindowId;
        public string OperatorId;
        public string BotProfileId;
        public string DesiredSector;
        public string EnvironmentKind;
        public Vector3 Anchor;
        public float AnchorRadiusMeters;
        public DateTimeOffset StartedAtUtc;
        public DateTimeOffset MinUntilUtc;
        public DateTimeOffset MaxUntilUtc;
        public DateTimeOffset NoProgressUntilUtc;
        public DateTimeOffset LastProgressAtUtc;
        public float InitialAnchorDistance;
        public float LastAnchorDistance;
        public Vector3 LastWorldPosition;
        public DateTimeOffset LastWorldSampleAtUtc;
        public DateTimeOffset PhysicalBlockedSinceUtc;
        public int PhysicalRestartCount;
        public float PathDistanceMeters;
        public string PlanSummary;

        public string Summary => "lease=" + Safe(LeaseId)
            + ";window=" + Safe(WindowId)
            + ";operator=" + Safe(OperatorId)
            + ";botProfile=" + Safe(BotProfileId)
            + ";sector=" + Safe(DesiredSector)
            + ";env=" + Safe(EnvironmentKind)
            + ";anchor=" + Anchor.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + Anchor.y.ToString("0.0", CultureInfo.InvariantCulture) + "," + Anchor.z.ToString("0.0", CultureInfo.InvariantCulture)
            + ";radius=" + AnchorRadiusMeters.ToString("0.0", CultureInfo.InvariantCulture)
            + ";initialDist=" + InitialAnchorDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";lastDist=" + LastAnchorDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";physicalRestarts=" + PhysicalRestartCount.ToString(CultureInfo.InvariantCulture)
            + ";blockedSince=" + PhysicalBlockedSinceUtc.ToString("O", CultureInfo.InvariantCulture)
            + ";path=" + PathDistanceMeters.ToString("0.0", CultureInfo.InvariantCulture)
            + ";plan=" + Safe(PlanSummary);
    }
}
#endif
