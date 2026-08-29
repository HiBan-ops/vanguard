#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EFT;
using EFT.Interactive;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.External;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.Movement.Brain;
using Vanguard.Client.Runtime.TacticalAuthoring;

// Responsibility: moves an Operator to a world container that the loot selector has already assigned.
// Flow: The assigned container is re-resolved and checked for claim/safety/path validity, a temporary movement lease approaches its interaction point, and reaching usable range hands control to the container-session executor while terminal failures enter bounded cooldown.
// Authority boundary: target choice and item value remain upstream, while this executor owns only approach movement and must yield to stronger combat, medical, grenade or movement authority.
// Invariant: the approach stays bound to one Operator/container/window and releases movement/claim state on success, failure, supersession or lifecycle reset.
namespace Vanguard.Client.Runtime.Loot;

/// <summary>
/// The persistence path owns the physical WorldContainer proof. The persistence path retains that exact authority through a post-open
/// transaction handoff, without introducing another scanner, another target claim, or direct Fika packets.
/// </summary>
internal static class VanguardWorldLootContainerApproachExecutor
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, ApproachLease> ActiveByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> TerminalCooldownByOwnerTarget = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(1.5d);
    private static DateTimeOffset nextTickAtUtc = DateTimeOffset.MinValue;

    public static void ResetForRaidLifecycle(string reason)
    {
        // Retire any post-open transaction session first; its terminal bookkeeping is then discarded
        // together with the physical-approach state below, leaving a genuinely clean raid boundary.
        VanguardWorldLootContainerSessionExecutor.ResetForRaidLifecycle(reason);
        lock (Sync)
        {
            ActiveByBot.Clear();
            TerminalCooldownByOwnerTarget.Clear();
            LastLogByKey.Clear();
            nextTickAtUtc = DateTimeOffset.MinValue;
        }
        VanguardClientDiagnosticsLog.Info(VanguardWorldLootContainerApproachDoctrine.TransactionStatusTag,
            $"VANGUARD_CONTAINER_APPROACH_RESET reason={Safe(reason)}; sharedTargetClaim=true; itemClaim=true; transaction=true; postOpenSession=true");
    }

    public static void Tick(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority || snapshots == null || snapshots.Count == 0) return;
        if (now < nextTickAtUtc) return;
        nextTickAtUtc = now + TimeSpan.FromMilliseconds(120d);

        VanguardWorldLootContainerSessionExecutor.Tick(snapshots, now);
        TickActive(snapshots, now);
        TryStartOne(snapshots, now);
    }

    public static bool TryTerminateSchedulerExpiredWindow(string botProfileId, string windowId, DateTimeOffset now, string timeoutReason, out string summary)
    {
        ApproachLease? lease = null;
        lock (Sync)
        {
            if (ActiveByBot.TryGetValue(Normalize(botProfileId), out var found)
                && string.Equals(found.WindowId, windowId, StringComparison.OrdinalIgnoreCase))
                lease = found;
        }
        if (lease == null)
        {
            return VanguardWorldLootContainerSessionExecutor.TryTerminateSchedulerExpiredWindow(
                botProfileId, windowId, now, timeoutReason, out summary);
        }
        summary = Finish(lease, now, "Timeout", "scheduler_expired:" + timeoutReason, finishScheduler: false);
        return true;
    }

    private static void TryStartOne(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        foreach (OperatorDecisionSnapshot snapshot in snapshots
                     .Where(value => value != null && value.Alive)
                     .OrderBy(value => value.SquadCohesion.OperatorDistanceToOwner))
        {
            if (!VanguardWorldLootContainerReadOnlyEvaluator.TryGetApproachCandidate(snapshot.BotProfileId, now, out var candidate)) continue;
            if (!string.Equals(candidate.OwnerProfileId, snapshot.OwnerProfileId, StringComparison.OrdinalIgnoreCase)) continue;

            lock (Sync) { if (ActiveByBot.ContainsKey(snapshot.BotProfileId)) continue; }
            if (VanguardLootTargetClaimStore.TryGetActiveClaimBot(snapshot.OwnerProfileId, now, out _)) continue;
            if (IsCoolingDown(snapshot.OwnerProfileId, candidate.ContainerId, now)) continue;

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out VanguardRaidOperatorRuntimeRecord record)
                || record.BotOwner == null || record.BotOwner.IsDead) continue;

            VanguardOperatorLootPermissionSnapshot permissions = VanguardOperatorLootPermissionSnapshot.CaptureRuntime(record);
            if (!VanguardOperatorLootTargetPermissionPolicy.AllowsTarget(permissions, VanguardLootTargetKind.WorldContainer, out string permissionReason))
            {
                LogThrottled("permission|" + snapshot.BotProfileId, now,
                    $"VANGUARD_CONTAINER_APPROACH_BLOCKED operator={Safe(snapshot.OperatorId)}; bot={Safe(snapshot.BotProfileId)}; container={Safe(candidate.ContainerId)}; reason={Safe(permissionReason)}; persistentPolicy={permissions.OperatorTargetPolicy}");
                continue;
            }

            string safety = VanguardCorpseLootApproachExecutor.CheckActiveSessionSafetyGate(snapshot, now, activeWindow: false);
            if (!string.Equals(safety, "none", StringComparison.OrdinalIgnoreCase)) continue;

            bool authoredLootExcursion = VanguardTacticalAuthoringHeadlessPreviewService.TryGetLootExcursionContext(
                snapshot.BotProfileId,
                now,
                out _,
                out _);
            bool boundedTravelYield = false;
            string boundedTravelYieldProof = "no_active_travel_command";
            if (VanguardReturnMovementCommandStore.TryGetActive(snapshot.BotProfileId, now, out var activeCommand)
                && VanguardPrimaryExecutionContract.ShouldKeepMovementContractUntilTerminal(snapshot, activeCommand.RequestKind, out _))
            {
                boundedTravelYield = VanguardOpportunisticLootTravelYieldPolicy.CanYield(
                    snapshot,
                    activeCommand.RequestKind,
                    candidate.DirectDistanceMeters,
                    now,
                    out boundedTravelYieldProof);
                bool authoredYield = authoredLootExcursion
                    && string.Equals(activeCommand.RequestKind, VanguardTacticalAuthoringHeadlessPreviewService.RequestKind, StringComparison.OrdinalIgnoreCase);
                if (!authoredYield && !boundedTravelYield)
                {
                    LogThrottled("travel_yield|" + snapshot.BotProfileId + "|" + candidate.ContainerId, now,
                        $"VANGUARD_CONTAINER_LOOT_TRAVEL_YIELD_DENIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; container={Safe(candidate.ContainerId)}; activeRequest={Safe(activeCommand.RequestKind)}; proof={Safe(boundedTravelYieldProof)}; behaviorChanged=false; movementContractPreserved=true");
                    continue;
                }
            }

            if (!TryResolveLive(candidate.ContainerId, now, out VanguardWorldLootContainerSnapshot live, out string liveReason))
            {
                RememberTerminal(snapshot.OwnerProfileId, candidate.ContainerId, now, failure: true);
                LogThrottled("live|" + candidate.ContainerId, now,
                    $"VANGUARD_CONTAINER_APPROACH_BLOCKED operator={Safe(snapshot.OperatorId)}; container={Safe(candidate.ContainerId)}; reason={Safe(liveReason)}");
                continue;
            }

            Vector3 botPosition = ResolveBotPosition(record.BotOwner);
            if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner("WorldContainerLootApproachPathPlan", snapshot.OwnerProfileId, 1, 1, out _)) return;
            if (!VanguardCorpseLootApproachPlanner.TryBuild(snapshot, botPosition, live.Position, out VanguardCorpseLootApproachPlan plan))
            {
                RememberTerminal(snapshot.OwnerProfileId, candidate.ContainerId, now, failure: true);
                continue;
            }

            if (!VanguardLootTargetClaimStore.TryAcquire(snapshot.OwnerProfileId, snapshot.OperatorId, snapshot.BotProfileId,
                    VanguardLootTargetKind.WorldContainer, candidate.ContainerId, candidate.ExecutionScore, now,
                    out VanguardLootTargetClaim claim, out string claimReason))
            {
                LogThrottled("claim|" + snapshot.OwnerProfileId + "|" + candidate.ContainerId, now,
                    $"VANGUARD_CONTAINER_APPROACH_BLOCKED operator={Safe(snapshot.OperatorId)}; container={Safe(candidate.ContainerId)}; reason={Safe(claimReason)}");
                continue;
            }

            if (!VanguardMainIntentScheduler.TryOpenWorldContainerLootApproach(snapshot, now, candidate.ContainerId, candidate.ExecutionScore, authoredLootExcursion, boundedTravelYield, out string windowId, out string windowReason))
            {
                VanguardLootTargetClaimStore.Release(claim.ClaimId, VanguardLootTargetKind.WorldContainer, "scheduler_denied", out _);
                LogThrottled("scheduler|" + snapshot.BotProfileId + "|" + candidate.ContainerId, now,
                    $"VANGUARD_CONTAINER_APPROACH_BLOCKED operator={Safe(snapshot.OperatorId)}; container={Safe(candidate.ContainerId)}; reason=scheduler_denied:{Safe(windowReason)}");
                continue;
            }

            if (boundedTravelYield)
            {
                VanguardClientDiagnosticsLog.Operational(
                    VanguardOpportunisticLootTravelYieldPolicy.StatusTag,
                    () => $"VANGUARD_CONTAINER_LOOT_TRAVEL_COHESION_YIELDED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; container={Safe(candidate.ContainerId)}; proof={Safe(boundedTravelYieldProof)}; schedulerWindow={Safe(windowId)}; criticalSafetyGatesPreserved=true; movementCoreChanged=false");
            }

            if (NeedsExternalPreempt(snapshot))
            {
                VanguardExternalPreemptResult preempt = VanguardExternalAuthorityAdapter.RequestOrbitAuthorityQuiesce(
                    record.BotOwner, snapshot, "world_container_loot_claim_and_approach",
                    TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.SchedulerMaximumWindowSeconds + 3.0f), now);
                if (!preempt.CanDriveMovement)
                {
                    VanguardLootTargetClaimStore.Release(claim.ClaimId, VanguardLootTargetKind.WorldContainer, "external_preempt_denied", out _);
                    VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "external_preempt_not_granted", preempt.Summary, windowId);
                    continue;
                }
            }

            string prevent = VanguardOpportunisticLootBroker.PreventForVanguardOwnedWindow(record.BotOwner,
                VanguardCorpseLootApproachDoctrine.SchedulerMaximumWindowSeconds + 3.0f, "world_container_open_proof");
            string leaseId = "container_loot_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId);
            DateTimeOffset maxUntil = now + TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.MaximumWindowSeconds);
            if (!VanguardReturnMovementCommandStore.Issue(leaseId, snapshot.OperatorId, snapshot.BotProfileId,
                    plan.Anchor, VanguardCorpseLootApproachDoctrine.ApproachAnchorRadiusMeters, false, now, maxUntil,
                    VanguardMovementContractPolicy.WorldContainerLootApproach, plan.PathSummary, plan.PathDistance, out string issueReason))
            {
                VanguardLootTargetClaimStore.Release(claim.ClaimId, VanguardLootTargetKind.WorldContainer, "move_bridge_rejected", out _);
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "move_bridge_rejected:" + issueReason, plan.Summary, windowId);
                continue;
            }

            if (!VanguardReturnMovementCommandStore.TryGetActive(snapshot.BotProfileId, now, out VanguardReturnMovementCommand command)
                || !string.Equals(command.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(command.RequestKind, VanguardMovementContractPolicy.WorldContainerLootApproach, StringComparison.OrdinalIgnoreCase))
            {
                VanguardReturnMovementCommandStore.ClearOwned(snapshot.BotProfileId, leaseId, now, "container_command_identity_not_confirmed");
                VanguardLootTargetClaimStore.Release(claim.ClaimId, VanguardLootTargetKind.WorldContainer, "command_identity_not_confirmed", out _);
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "move_bridge_identity_not_confirmed", plan.Summary, windowId);
                continue;
            }

            var lease = new ApproachLease
            {
                ClaimId = claim.ClaimId, LeaseId = leaseId, WindowId = windowId,
                OwnerProfileId = snapshot.OwnerProfileId, OperatorId = snapshot.OperatorId, BotProfileId = snapshot.BotProfileId,
                ContainerId = candidate.ContainerId, ManifestRevision = candidate.ManifestRevision, CommandGeneration = command.Generation, Anchor = plan.Anchor,
                StartedAtUtc = now, MaxUntilUtc = maxUntil,
                NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.NoProgressSeconds),
                LastWorldPosition = botPosition, LastWorldSampleAtUtc = now,
                LastAnchorDistance = HorizontalDistance(botPosition, plan.Anchor),
                LastTargetDistance = HorizontalDistance(botPosition, live.Position), PlanSummary = plan.Summary, PreventSummary = prevent
            };
            if (!VanguardMainIntentScheduler.MarkWorldContainerLootApproachStarted(snapshot.BotProfileId, leaseId, now, lease.Summary, windowId))
            {
                VanguardReturnMovementCommandStore.ClearOwned(snapshot.BotProfileId, leaseId, now, "container_scheduler_start_not_confirmed");
                VanguardLootTargetClaimStore.Release(claim.ClaimId, VanguardLootTargetKind.WorldContainer, "scheduler_start_not_confirmed", out _);
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "scheduler_start_not_confirmed", lease.Summary, windowId);
                continue;
            }
            lock (Sync) ActiveByBot[snapshot.BotProfileId] = lease;
            VanguardClientDiagnosticsLog.Operational(VanguardWorldLootContainerApproachDoctrine.StatusTag, () =>
                $"VANGUARD_CONTAINER_APPROACH_STARTED {lease.Summary}; plan={Safe(plan.Summary)}; prevent={Safe(prevent)}; targetClaim=shared; itemClaim=false; openingPending=true; transaction=false");
            return;
        }
    }

    private static void TickActive(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        ApproachLease[] active;
        lock (Sync) active = ActiveByBot.Values.ToArray();
        if (active.Length == 0) return;
        var byBot = snapshots.Where(value => value != null).GroupBy(value => value.BotProfileId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (ApproachLease lease in active)
        {
            if (!byBot.TryGetValue(lease.BotProfileId, out OperatorDecisionSnapshot? snapshot) || snapshot == null)
            {
                Finish(lease, now, "Interrupted", "snapshot_missing", true); continue;
            }
            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(lease.BotProfileId, out var record)
                || record.BotOwner == null || record.BotOwner.IsDead)
            {
                Finish(lease, now, "Interrupted", "bot_owner_missing_or_dead", true); continue;
            }
            VanguardOperatorLootPermissionSnapshot permissions = VanguardOperatorLootPermissionSnapshot.CaptureRuntime(record);
            if (!VanguardOperatorLootTargetPermissionPolicy.AllowsTarget(permissions, VanguardLootTargetKind.WorldContainer, out string permissionReason))
            {
                Finish(lease, now, "Interrupted", "permission_revoked:" + permissionReason, true); continue;
            }
            string gate = VanguardCorpseLootApproachExecutor.CheckActiveSessionSafetyGate(snapshot, now, activeWindow: true);
            if (!string.Equals(gate, "none", StringComparison.OrdinalIgnoreCase))
            {
                Finish(lease, now, "Interrupted", "safety_gate:" + gate, true); continue;
            }
            if (!VanguardLootTargetClaimStore.TryGetByBot(lease.BotProfileId, now, out VanguardLootTargetClaim activeClaim)
                || activeClaim.TargetKind != VanguardLootTargetKind.WorldContainer
                || !string.Equals(activeClaim.ClaimId, lease.ClaimId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(activeClaim.TargetId, lease.ContainerId, StringComparison.OrdinalIgnoreCase))
            {
                Finish(lease, now, "Interrupted", "target_claim_lost", true); continue;
            }
            if (!TryResolveLive(lease.ContainerId, now, out VanguardWorldLootContainerSnapshot live, out string liveReason))
            {
                Finish(lease, now, "Failed", "container_live_invalid:" + liveReason, true); continue;
            }

            if (lease.OpenSubmitted)
            {
                if (live.Container.DoorState == EDoorState.Open)
                {
                    HandoffToTransaction(lease, snapshot, now, "door_state_open_proven"); continue;
                }
                if (now >= lease.OpenProofDeadlineUtc)
                {
                    Finish(lease, now, "Failed", "open_proof_timeout:doorState=" + live.Container.DoorState, true); continue;
                }
                continue;
            }

            if (!VanguardReturnMovementCommandStore.TryGetExactOwned(lease.BotProfileId, lease.LeaseId,
                    VanguardMovementContractPolicy.WorldContainerLootApproach, lease.CommandGeneration, now, out _, out string identityReason))
            {
                Finish(lease, now, "Interrupted", "owned_movement_command_lost:" + identityReason, true); continue;
            }

            Vector3 botPosition = ResolveBotPosition(record.BotOwner);
            float anchorDistance = HorizontalDistance(botPosition, lease.Anchor);
            float targetDistance = HorizontalDistance(botPosition, live.Position);
            TimeSpan sampleAge = now - lease.LastWorldSampleAtUtc;
            var physical = VanguardMovementProgressEvaluator.EvaluatePhysical(lease.LastWorldPosition, botPosition,
                lease.LastAnchorDistance, anchorDistance, snapshot.RealSpeed, movementExpected: true, sampleAge);
            if (physical.HasProgress)
            {
                lease.LastWorldPosition = botPosition;
                lease.LastWorldSampleAtUtc = now;
                lease.LastAnchorDistance = anchorDistance;
                lease.LastTargetDistance = targetDistance;
                lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.NoProgressSeconds);
                VanguardLootTargetClaimStore.Refresh(lease.ClaimId, VanguardLootTargetKind.WorldContainer, now);
                VanguardMainIntentScheduler.ReportPrimaryProgress(lease.BotProfileId, now, "world_container_physical_progress", physical.Summary, lease.WindowId);
            }

            bool arrived = targetDistance <= VanguardWorldLootContainerApproachDoctrine.InteractionDistanceMeters
                || anchorDistance <= VanguardCorpseLootApproachDoctrine.ApproachAnchorRadiusMeters;
            if (arrived)
            {
                string cleanup = VanguardReturnMovementCommandStore.ClearOwned(lease.BotProfileId, lease.LeaseId, lease.StartedAtUtc, "container_arrival_open_handoff");
                if (live.Container.DoorState == EDoorState.Open)
                {
                    HandoffToTransaction(lease, snapshot, now, "already_open_at_arrival"); continue;
                }
                if (live.Container.DoorState == EDoorState.Locked)
                {
                    Finish(lease, now, "Failed", "container_locked_at_arrival", true); continue;
                }
                try
                {
                    Player? player = record.BotOwner.GetPlayer;
                    if (player == null) { Finish(lease, now, "Failed", "player_missing_at_open", true); continue; }
                    player.vmethod_1(live.Container, new InteractionResult(EInteractionType.Open));
                    lease.OpenSubmitted = true;
                    lease.OpenSubmittedAtUtc = now;
                    lease.OpenProofDeadlineUtc = now + TimeSpan.FromSeconds(VanguardWorldLootContainerApproachDoctrine.OpenProofTimeoutSeconds);
                    VanguardLootTargetClaimStore.Refresh(lease.ClaimId, VanguardLootTargetKind.WorldContainer, now);
                    VanguardMainIntentScheduler.ReportPrimaryProgress(lease.BotProfileId, now, "world_container_open_submitted", "commandCleanup=" + cleanup, lease.WindowId);
                    VanguardClientDiagnosticsLog.Operational(VanguardWorldLootContainerApproachDoctrine.StatusTag, () =>
                        $"VANGUARD_CONTAINER_OPEN_SUBMITTED operator={Safe(lease.OperatorId)}; bot={Safe(lease.BotProfileId)}; container={Safe(lease.ContainerId)}; distance={targetDistance:0.00}; interaction=Player.vmethod_1; fikaPacketDirect=false; itemClaim=false; transaction=false");
                }
                catch (Exception exception)
                {
                    Finish(lease, now, "Failed", "open_submit_exception:" + exception.GetType().Name, true);
                }
                continue;
            }

            if (now >= lease.MaxUntilUtc) { Finish(lease, now, "Timeout", "max_window_expired", true); continue; }
            if (now >= lease.NoProgressUntilUtc) { Finish(lease, now, "Timeout", "no_progress_timeout", true); }
        }
    }

    private static void HandoffToTransaction(
        ApproachLease lease,
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        string openProofReason)
    {
        if (!VanguardWorldLootContainerSessionExecutor.TryBegin(
                lease.ClaimId, lease.LeaseId, lease.WindowId, lease.OwnerProfileId, lease.OperatorId, lease.BotProfileId,
                lease.ContainerId, lease.ManifestRevision, Math.Max(0f, lease.LastTargetDistance), snapshot, lease.StartedAtUtc, now,
                lease.Summary + ";openProof=" + openProofReason, out string sessionSummary))
        {
            Finish(lease, now, "Failed", "transaction_handoff_rejected:" + sessionSummary, true);
            return;
        }

        lock (Sync) ActiveByBot.Remove(lease.BotProfileId);
        string command = VanguardReturnMovementCommandStore.ClearOwned(lease.BotProfileId, lease.LeaseId, lease.StartedAtUtc, "container_open_transaction_handoff");
        VanguardClientDiagnosticsLog.Operational(VanguardWorldLootContainerApproachDoctrine.TransactionStatusTag, () =>
            $"VANGUARD_CONTAINER_OPEN_TO_TRANSACTION_HANDOFF {lease.Summary}; openProof={Safe(openProofReason)}; commandCleanup={Safe(command)}; targetClaimRetained=true; schedulerRetained=true; session={Safe(sessionSummary)}");
    }

    internal static void RegisterTransactionSessionTerminal(string ownerProfileId, string containerId, DateTimeOffset now, bool failure)
        => RememberTerminal(ownerProfileId, containerId, now, failure);

    private static bool TryResolveLive(string containerId, DateTimeOffset now, out VanguardWorldLootContainerSnapshot snapshot, out string reason)
    {
        snapshot = VanguardWorldLootContainerSnapshotProvider.GetSnapshot(now)
            .FirstOrDefault(value => string.Equals(value.ContainerId, containerId, StringComparison.OrdinalIgnoreCase))!;
        if (snapshot == null || snapshot.Container == null) { reason = "container_missing"; return false; }
        if (!snapshot.Container.isActiveAndEnabled) { reason = "container_inactive"; return false; }
        if (snapshot.Container.ItemOwner?.RootItem == null) { reason = "root_item_missing"; return false; }
        if (snapshot.Container.DoorState == EDoorState.Locked) { reason = "container_locked"; return false; }
        reason = "live";
        return true;
    }

    private static bool NeedsExternalPreempt(OperatorDecisionSnapshot snapshot)
        => snapshot.Orbit.Active || snapshot.Movement.HasPath == true || snapshot.Looting.HasActiveLootable == true
            || snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true;

    private static string Finish(ApproachLease lease, DateTimeOffset now, string outcome, string reason, bool finishScheduler)
    {
        lock (Sync) ActiveByBot.Remove(lease.BotProfileId);
        string command = VanguardReturnMovementCommandStore.ClearOwned(lease.BotProfileId, lease.LeaseId, lease.StartedAtUtc, "container_loot_finished:" + reason);
        VanguardLootTargetClaimStore.Release(lease.ClaimId, VanguardLootTargetKind.WorldContainer, reason, out _);
        RememberTerminal(lease.OwnerProfileId, lease.ContainerId, now, !string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase));
        if (finishScheduler) VanguardMainIntentScheduler.FinishPrimaryWindow(lease.BotProfileId, now, outcome, reason, lease.Summary, lease.WindowId);
        VanguardClientDiagnosticsLog.Operational(VanguardWorldLootContainerApproachDoctrine.StatusTag, () =>
            $"VANGUARD_CONTAINER_APPROACH_TERMINAL {lease.Summary}; outcome={Safe(outcome)}; reason={Safe(reason)}; commandCleanup={Safe(command)}; openSubmitted={Bool(lease.OpenSubmitted)}; mutation=false; itemClaim=false; inventoryPreview=false; transaction=false");
        return "lease=" + Safe(lease.LeaseId) + ";outcome=" + Safe(outcome) + ";reason=" + Safe(reason);
    }

    private static bool IsCoolingDown(string owner, string target, DateTimeOffset now)
    {
        string key = Normalize(owner) + "|" + Normalize(target);
        lock (Sync)
        {
            if (TerminalCooldownByOwnerTarget.TryGetValue(key, out DateTimeOffset until) && until > now) return true;
            TerminalCooldownByOwnerTarget.Remove(key);
            return false;
        }
    }

    private static void RememberTerminal(string owner, string target, DateTimeOffset now, bool failure)
    {
        double seconds = failure ? Math.Min(15d, VanguardWorldLootContainerApproachDoctrine.TerminalCooldownSeconds) : VanguardWorldLootContainerApproachDoctrine.TerminalCooldownSeconds;
        lock (Sync) TerminalCooldownByOwnerTarget[Normalize(owner) + "|" + Normalize(target)] = now + TimeSpan.FromSeconds(seconds);
    }

    private static Vector3 ResolveBotPosition(BotOwner botOwner)
    {
        object? player = VanguardOperatorRuntimeAuditReflection.GetMember(botOwner, "GetPlayer", "Player");
        object? transform = VanguardOperatorRuntimeAuditReflection.GetDeep(player, "PlayerBones", "BodyTransform");
        object? position = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(transform, "position");
        if (position is Vector3 vector) return vector;
        object? playerTransform = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player, "Transform", "transform");
        position = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(playerTransform, "position");
        return position is Vector3 fallback ? fallback : Vector3.zero;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return Vector3.Distance(a, b); }
    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    private static string Bool(bool value) => value ? "true" : "false";
    private static void LogThrottled(string key, DateTimeOffset now, string message)
    {
        lock (Sync)
        {
            if (LastLogByKey.TryGetValue(key, out DateTimeOffset last) && now - last < LogInterval) return;
            LastLogByKey[key] = now;
        }
        VanguardClientDiagnosticsLog.Operational(VanguardWorldLootContainerApproachDoctrine.StatusTag, () => message);
    }

    private sealed class ApproachLease
    {
        public string ClaimId = "none";
        public string LeaseId = "none";
        public string WindowId = "none";
        public string OwnerProfileId = "none";
        public string OperatorId = "none";
        public string BotProfileId = "none";
        public string ContainerId = "none";
        public long ManifestRevision;
        public long CommandGeneration;
        public Vector3 Anchor;
        public DateTimeOffset StartedAtUtc;
        public DateTimeOffset MaxUntilUtc;
        public DateTimeOffset NoProgressUntilUtc;
        public Vector3 LastWorldPosition;
        public DateTimeOffset LastWorldSampleAtUtc;
        public float LastAnchorDistance;
        public float LastTargetDistance;
        public bool OpenSubmitted;
        public DateTimeOffset OpenSubmittedAtUtc;
        public DateTimeOffset OpenProofDeadlineUtc;
        public string PlanSummary = "none";
        public string PreventSummary = "none";
        public string Summary => $"lease={Safe(LeaseId)}; window={Safe(WindowId)}; claim={Safe(ClaimId)}; owner={Safe(OwnerProfileId)}; operator={Safe(OperatorId)}; bot={Safe(BotProfileId)}; container={Safe(ContainerId)}; manifestRevision={ManifestRevision}; generation={CommandGeneration}; anchor={Anchor.x:0.00},{Anchor.y:0.00},{Anchor.z:0.00}; openSubmitted={Bool(OpenSubmitted)}; max={MaxUntilUtc:O}";
    }
}
#endif
