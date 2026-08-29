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
using Vanguard.Client.Runtime.External;
using Vanguard.Client.Runtime.Grenades;
using Vanguard.Client.Runtime.Medical.Execution;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.Movement.Brain;
using Vanguard.Client.Runtime.TacticalAuthoring;

// Responsibility: moves an Operator to the corpse that the loot evaluator/squad allocator has already selected.
// Flow: The selected corpse, claim, permissions, safety and path are revalidated; a movement lease approaches a reachable interaction point while target existence and higher-priority interrupts are monitored; reaching range hands control to the corpse-loot session executor.
// Authority boundary: selection/value policy and inventory transfer live elsewhere; this executor owns only the approach movement and cannot bypass combat, medical, grenade or ownership gates.
// Invariant: an approach remains tied to the same Operator/corpse/window generation and always releases its lease/claim on terminal failure, supersession or raid reset.
namespace Vanguard.Client.Runtime.Loot;

/// <summary>
/// Owns the bounded corpse claim and physical approach while retaining the same scheduler execution window.
/// A successful arrival transfers authority to one bounded native item-transaction session. One confirmed EFT mutation
/// ends the visit and returns the corpse to squad-wide reallocation; this executor never adds a parallel inventory engine,
/// world-container authority, Operator-corpse persistence or direct hands mutation.
/// </summary>
internal static class VanguardCorpseLootApproachExecutor
{
    public const string StatusTag = VanguardCorpseLootApproachDoctrine.StatusTag;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, ApproachLease> ActiveByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(1.5d);
    private static DateTimeOffset nextTickAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    public static void ResetForRaidLifecycle(string reason)
    {
        ApproachLease[] active;
        lock (Sync)
        {
            active = ActiveByBotProfileId.Values.ToArray();
            ActiveByBotProfileId.Clear();
            LastLogByKey.Clear();
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (ApproachLease lease in active)
        {
            VanguardReturnMovementCommandStore.ClearOwned(lease.BotProfileId, lease.LeaseId, lease.StartedAtUtc, "corpse_loot_raid_reset:" + reason);
            VanguardCorpseLootClaimStore.Release(lease.ClaimId, "raid_reset", out _);
            VanguardCorpseLootOperationalTelemetry.RecordApproachTerminal("Interrupted", "raid_reset", lease.BotProfileId, lease.CorpseId);
        }

        VanguardCorpseLootSessionExecutor.ResetForRaidLifecycle(reason);
        VanguardCorpseLootClaimStore.ResetForRaidLifecycle(reason);
        VanguardLootItemClaimStore.ResetForRaidLifecycle(reason);
        VanguardCorpseLootOutcomeMemory.ResetForRaidLifecycle(reason);
        nextTickAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"VANGUARD_CORPSE_LOOT_APPROACH_RESET reason={Safe(reason)}; activeCleared={active.Length}; claimsCleared=true; transactionsEnabled=true; interactionAtReset=false; atomicPerItem=true; sequentialSession=false; singleUtilityClaimPerVisit=true; operatorCorpseCommit=false");
    }

    public static void Tick(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        if (!VanguardOperatorRuntimeAuditLoadGuard.IsOpen() || !VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }

        if (now < nextTickAtUtc)
        {
            return;
        }
        nextTickAtUtc = now + VanguardCorpseLootApproachDoctrine.TickInterval;

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
                $"VANGUARD_CORPSE_LOOT_APPROACH_BOOT enabled={Bool(IsFeatureEnabled())}; authority=raid_authority_only; corpseLease=one_physical_looter_per_owner_and_corpse; itemClaims=assignment_bound; maxOwnerStart={VanguardCorpseLootApproachDoctrine.MaximumStartOwnerDistanceMeters:0.0}; maxOwnerActive={VanguardCorpseLootApproachDoctrine.MaximumActiveOwnerDistanceMeters:0.0}; maxDirect=player_scoped_owner_resolved; maxPath={VanguardCorpseLootApproachDoctrine.MaximumPathDistanceMeters:0.0}; maxDetour={VanguardCorpseLootApproachDoctrine.MaximumAddedDetourMeters:0.0}; maxRatio={VanguardCorpseLootApproachDoctrine.MaximumPathRatio:0.00}; arrival={VanguardCorpseLootApproachDoctrine.CorpseInteractionDistanceMeters:0.00}; maxWindow={VanguardCorpseLootApproachDoctrine.MaximumWindowSeconds:0.0}; noProgress={VanguardCorpseLootApproachDoctrine.NoProgressSeconds:0.0}; movement=true; formationTopologyHardGate=false; corpseInteractionAtApproach=false; transactionsAfterArrival=true; atomicPerItem=true; sequentialSession=false; singleUtilityClaimPerVisit=true; operatorCorpseCommit=false; persistence=false; build={VanguardBuildVersion.BuildLabel}; tag={StatusTag}");
        }

        long sessionTickStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            VanguardCorpseLootSessionExecutor.Tick(snapshots, now);
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("CorpseLootSessionTick", sessionTickStarted);
        }

        long activeApproachStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            TickActive(snapshots, now);
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("CorpseLootActiveApproach", activeApproachStarted);
        }
        if (!IsFeatureEnabled() || snapshots == null || snapshots.Count == 0)
        {
            return;
        }

        if (VanguardRuntimeFrameBudgetGuard.ShouldRunOptional("CorpseLootApproachPlanning", now, TimeSpan.FromSeconds(0.75d), out _))
        {
            long approachPlanningStarted = VanguardRuntimePerformanceGuard.Begin();
            try
            {
                TryStartOne(snapshots, now);
            }
            catch (Exception exception)
            {
                string recovery = RecoverOrphanedActivationState(snapshots, now, exception);
                VanguardClientDiagnosticsLog.Warning(StatusTag, () =>
                    $"VANGUARD_CORPSE_LOOT_APPROACH_START_FAILED type={exception.GetType().Name}; reason={Safe(exception.Message)}; recovery={Safe(recovery)}; failOpen=true; activeEstablishedLeasesPreserved=true; interactions=false; transactionSessionMayRemainActive=true");
            }
            finally
            {
                VanguardRuntimePerformanceGuard.End("CorpseLootApproachPlanning", approachPlanningStarted);
            }
        }
    }

    private static string RecoverOrphanedActivationState(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now, Exception exception)
    {
        int claimsReleased = 0;
        int commandsCleared = 0;
        int schedulerWindowsAborted = 0;
        foreach (OperatorDecisionSnapshot snapshot in snapshots ?? Array.Empty<OperatorDecisionSnapshot>())
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            {
                continue;
            }

            bool established;
            lock (Sync)
            {
                established = ActiveByBotProfileId.ContainsKey(snapshot.BotProfileId);
            }
            if (established)
            {
                continue;
            }

            if (!VanguardCorpseLootClaimStore.TryGetByBot(snapshot.BotProfileId, now, out VanguardCorpseLootClaim claim))
            {
                continue;
            }

            if (VanguardReturnMovementCommandStore.TryGetActive(snapshot.BotProfileId, now, out VanguardReturnMovementCommand command)
                && string.Equals(command.RequestKind, VanguardCorpseLootApproachDoctrine.RequestKind, StringComparison.OrdinalIgnoreCase))
            {
                string cleared = VanguardReturnMovementCommandStore.ClearOwned(
                    snapshot.BotProfileId,
                    command.LeaseId,
                    command.IssuedAtUtc,
                    "corpse_loot_activation_exception:" + exception.GetType().Name);
                if (cleared.StartsWith("cleared:", StringComparison.OrdinalIgnoreCase))
                {
                    commandsCleared++;
                }
            }

            if (VanguardCorpseLootClaimStore.Release(claim.ClaimId, "activation_exception", out _))
            {
                claimsReleased++;
            }

            if (VanguardMainIntentScheduler.AbortCorpseLootApproachActivation(
                    snapshot.BotProfileId,
                    now,
                    "activation_exception:" + exception.GetType().Name,
                    out _))
            {
                schedulerWindowsAborted++;
            }

            _ = VanguardCorpseLootOutcomeMemory.Record(
                claim.OwnerProfileId,
                claim.OperatorId,
                snapshot.BotProfileId,
                claim.CorpseId,
                now,
                "Failed",
                "activation_exception:" + exception.GetType().Name,
                false,
                VanguardCorpseLootApproachDoctrine.FailureCooldownSeconds,
                out _);
        }

        return $"claimsReleased={claimsReleased};commandsCleared={commandsCleared};schedulerWindowsAborted={schedulerWindowsAborted}";
    }

    public static bool TryTerminateSchedulerExpiredWindow(string botProfileId, string windowId, DateTimeOffset now, string timeoutReason, out string summary)
    {
        ApproachLease lease;
        lock (Sync)
        {
            if (!ActiveByBotProfileId.TryGetValue(botProfileId, out lease)
                || !string.Equals(lease.WindowId, windowId, StringComparison.OrdinalIgnoreCase))
            {
                return VanguardCorpseLootSessionExecutor.TryTerminateSchedulerExpiredWindow(botProfileId, windowId, now, timeoutReason, out summary);
            }
        }

        summary = FinishLease(lease, now, "Timeout", "scheduler_expired:" + timeoutReason, failureCooldown: true, snapshotSignature: "scheduler_terminal", finishScheduler: false);
        return true;
    }

    // Start at most one new corpse visit per tick. Candidates are ordered by current utility, but every
    // mutable fact is rechecked in sequence: active lease, squad claim, outcome cooldown, live assignment,
    // safety/authoring yield, path viability and finally scheduler admission. Any failed gate simply moves
    // to the next candidate; no claim or movement authority is kept from a rejected attempt.
    private static void TryStartOne(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        foreach (OperatorDecisionSnapshot snapshot in snapshots
                     .Where(item => item != null && item.Alive && item.CorpseLoot.CandidateFound)
                     .OrderByDescending(ActiveCandidateScore)
                     .ThenBy(item => item.CorpseLoot.PathDistanceMeters)
                     .ThenBy(item => item.SquadCohesion.OperatorDistanceToOwner))
        {
            if (!snapshot.CorpseLoot.ExecutionEnabled || !snapshot.CorpseLoot.EligibleIfActivated)
            {
                continue;
            }

            lock (Sync)
            {
                if (ActiveByBotProfileId.ContainsKey(snapshot.BotProfileId))
                {
                    continue;
                }
            }

            if (VanguardCorpseLootClaimStore.TryGetActiveClaimBot(snapshot.OwnerProfileId, now, out _))
            {
                continue;
            }

            if (!VanguardCorpseLootOutcomeMemory.CanStartContext(
                    snapshot.OwnerProfileId,
                    snapshot.BotProfileId,
                    snapshot.CorpseLoot.CandidateCorpseId,
                    now,
                    snapshot.CorpseLoot.ManifestRevision,
                    snapshot.CorpseLoot.InterestRevision,
                    snapshot.CorpseLoot.LootNeedSignature,
                    out string outcomeGate))
            {
                LogThrottled("outcome|" + snapshot.BotProfileId + "|" + snapshot.CorpseLoot.CandidateCorpseId, now,
                    $"VANGUARD_CORPSE_LOOT_APPROACH_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; corpse={Safe(snapshot.CorpseLoot.CandidateCorpseId)}; reason=outcome_memory:{Safe(outcomeGate)}");
                continue;
            }

            IReadOnlyList<VanguardSquadLootItemAssignment> liveAssignments = VanguardSquadLootAssignmentService.GetAssignmentsForBot(
                snapshot.OwnerProfileId, snapshot.CorpseLoot.CandidateCorpseId, snapshot.BotProfileId, snapshot.CorpseLoot.ManifestRevision, now);
            bool hasAssignedPlannedItem = liveAssignments.Any(assignment => snapshot.CorpseLoot.Plan.Entries.Any(entry =>
                entry.PlacementPossible && string.Equals(entry.ItemId, assignment.ItemId, StringComparison.OrdinalIgnoreCase)));
            if (!hasAssignedPlannedItem)
            {
                LogThrottled("assignment|" + snapshot.BotProfileId + "|" + snapshot.CorpseLoot.CandidateCorpseId, now,
                    $"VANGUARD_CORPSE_LOOT_APPROACH_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; corpse={Safe(snapshot.CorpseLoot.CandidateCorpseId)}; reason=no_current_utility_assignment; manifestRevision={snapshot.CorpseLoot.ManifestRevision}; interestRevision={snapshot.CorpseLoot.InterestRevision}");
                continue;
            }

            VanguardTacticalAuthoringLootExcursionContext authoredContext = default;
            string authoredContextReason = "not_in_authored_stationary_hold";
            bool authoredLootExcursion = VanguardTacticalAuthoringHeadlessPreviewService.TryGetCorpseLootExcursionContext(
                snapshot.BotProfileId,
                now,
                out authoredContext,
                out authoredContextReason);

            string gate = CheckSafetyGate(snapshot, now, currentLease: null, allowAuthoringPreviewYield: authoredLootExcursion);
            if (!string.Equals(gate, "none", StringComparison.OrdinalIgnoreCase))
            {
                string gateLogKey = gate.StartsWith("active_movement_contract_preserved:", StringComparison.OrdinalIgnoreCase)
                    ? "active_movement_contract_preserved"
                    : gate;
                LogThrottled("gate|" + snapshot.BotProfileId + "|" + gateLogKey, now,
                    $"VANGUARD_CORPSE_LOOT_APPROACH_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; corpse={Safe(snapshot.CorpseLoot.CandidateCorpseId)}; reason={Safe(gate)}; ownerDistance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; corpseDirect={snapshot.CorpseLoot.DirectDistanceMeters:0.0}; corpsePath={snapshot.CorpseLoot.PathDistanceMeters:0.0}; medical={Safe(snapshot.Medical.Classification)}; threat={Safe(snapshot.Threat.Classification)}");
                continue;
            }

            if (!VanguardCorpseRegistry.TryGet(snapshot.CorpseLoot.CandidateCorpseId, now, out VanguardCorpseRegistryEntry entry)
                || entry.Corpse == null)
            {
                _ = VanguardCorpseLootOutcomeMemory.Record(snapshot.OwnerProfileId, snapshot.OperatorId, snapshot.BotProfileId, snapshot.CorpseLoot.CandidateCorpseId, now, "Failed", "corpse_missing", false, VanguardCorpseLootApproachDoctrine.FailureCooldownSeconds, out _);
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out VanguardRaidOperatorRuntimeRecord record)
                || record.BotOwner == null
                || record.BotOwner.IsDead)
            {
                continue;
            }

            if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner("CorpseLootApproachPathPlan", snapshot.OwnerProfileId, 1, 1, out _))
            {
                return;
            }

            Vector3 botPosition = ResolveBotPosition(record.BotOwner);
            if (!VanguardCorpseLootApproachPlanner.TryBuild(snapshot, botPosition, entry.Corpse.transform.position, out VanguardCorpseLootApproachPlan plan))
            {
                _ = VanguardCorpseLootOutcomeMemory.Record(snapshot.OwnerProfileId, snapshot.OperatorId, snapshot.BotProfileId, entry.CorpseId, now, "Failed", "approach_plan_failed:" + plan.Reason, false, VanguardCorpseLootApproachDoctrine.FailureCooldownSeconds, out _);
                VanguardCorpseLootOperationalTelemetry.RecordApproachPlanRejected(snapshot.BotProfileId, entry.CorpseId, plan.Reason);
                LogThrottled("plan|" + snapshot.BotProfileId + "|" + entry.CorpseId, now,
                    $"VANGUARD_CORPSE_LOOT_APPROACH_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; corpse={Safe(entry.CorpseId)}; reason=approach_plan_failed:{Safe(plan.Reason)}; plan={Safe(plan.Summary)}");
                continue;
            }

            if (!VanguardCorpseLootClaimStore.TryAcquire(
                    snapshot.OwnerProfileId,
                    snapshot.OperatorId,
                    snapshot.BotProfileId,
                    entry.CorpseId,
                    snapshot.CorpseLoot.UtilityScore,
                    now,
                    out VanguardCorpseLootClaim claim,
                    out string claimReason))
            {
                LogThrottled("claim|" + snapshot.OwnerProfileId + "|" + entry.CorpseId, now,
                    $"VANGUARD_CORPSE_LOOT_APPROACH_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; corpse={Safe(entry.CorpseId)}; reason=claim_denied:{Safe(claimReason)}");
                continue;
            }

            bool boundedTravelYield = CanYieldCurrentTravelCohesion(
                snapshot,
                snapshot.CorpseLoot.DirectDistanceMeters,
                now,
                out string boundedTravelYieldProof);

            if (!VanguardMainIntentScheduler.TryOpenCorpseLootApproach(
                    snapshot,
                    now,
                    entry.CorpseId,
                    authoredLootExcursion,
                    boundedTravelYield,
                    out string windowId,
                    out string openReason))
            {
                VanguardCorpseLootClaimStore.Release(claim.ClaimId, "scheduler_denied", out _);
                LogThrottled("scheduler|" + snapshot.BotProfileId + "|" + openReason, now,
                    $"VANGUARD_CORPSE_LOOT_APPROACH_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; corpse={Safe(entry.CorpseId)}; reason=scheduler_denied:{Safe(openReason)}");
                continue;
            }

            if (boundedTravelYield)
            {
                VanguardClientDiagnosticsLog.Operational(
                    VanguardOpportunisticLootTravelYieldPolicy.StatusTag,
                    () => $"VANGUARD_CORPSE_LOOT_TRAVEL_COHESION_YIELDED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; corpse={Safe(entry.CorpseId)}; proof={Safe(boundedTravelYieldProof)}; schedulerWindow={Safe(windowId)}; criticalSafetyGatesPreserved=true; movementCoreChanged=false");
            }

            if (NeedsExternalPreempt(snapshot))
            {
                VanguardExternalPreemptResult preempt = VanguardExternalAuthorityAdapter.RequestOrbitAuthorityQuiesce(
                    record.BotOwner,
                    snapshot,
                    "corpse_loot_claim_and_approach",
                    TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.SchedulerMaximumWindowSeconds + 3.0f),
                    now);
                if (!preempt.CanDriveMovement)
                {
                    VanguardCorpseLootClaimStore.Release(claim.ClaimId, "external_preempt_denied", out _);
                    VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "external_preempt_not_granted:" + preempt.Outcome, preempt.Summary, windowId);
                    _ = VanguardCorpseLootOutcomeMemory.Record(snapshot.OwnerProfileId, snapshot.OperatorId, snapshot.BotProfileId, entry.CorpseId, now, "Failed", "external_preempt_not_granted", false, VanguardCorpseLootApproachDoctrine.FailureCooldownSeconds, out _);
                    continue;
                }
            }

            string preventSummary = VanguardOpportunisticLootBroker.PreventForVanguardOwnedWindow(record.BotOwner, VanguardCorpseLootApproachDoctrine.SchedulerMaximumWindowSeconds + 3.0f, "corpse_loot_operational_session");
            string leaseId = "corpse_loot_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId);
            DateTimeOffset maxUntil = now + TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.MaximumWindowSeconds);
            bool issued = VanguardReturnMovementCommandStore.Issue(
                leaseId,
                snapshot.OperatorId,
                snapshot.BotProfileId,
                plan.Anchor,
                VanguardCorpseLootApproachDoctrine.ApproachAnchorRadiusMeters,
                sprint: false,
                now,
                maxUntil,
                VanguardCorpseLootApproachDoctrine.RequestKind,
                plan.PathSummary,
                plan.PathDistance,
                out string commandResult);
            if (!issued)
            {
                VanguardCorpseLootClaimStore.Release(claim.ClaimId, "move_bridge_rejected", out _);
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "move_bridge_rejected:" + commandResult, plan.Summary, windowId);
                _ = VanguardCorpseLootOutcomeMemory.Record(snapshot.OwnerProfileId, snapshot.OperatorId, snapshot.BotProfileId, entry.CorpseId, now, "Failed", "move_bridge_rejected", false, VanguardCorpseLootApproachDoctrine.FailureCooldownSeconds, out _);
                continue;
            }

            if (!VanguardReturnMovementCommandStore.TryGetActive(snapshot.BotProfileId, now, out VanguardReturnMovementCommand command)
                || !string.Equals(command.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(command.RequestKind, VanguardCorpseLootApproachDoctrine.RequestKind, StringComparison.OrdinalIgnoreCase))
            {
                VanguardReturnMovementCommandStore.ClearOwned(snapshot.BotProfileId, leaseId, now, "corpse_loot_command_identity_not_confirmed");
                VanguardCorpseLootClaimStore.Release(claim.ClaimId, "command_identity_not_confirmed", out _);
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "move_bridge_identity_not_confirmed", plan.Summary, windowId);
                _ = VanguardCorpseLootOutcomeMemory.Record(snapshot.OwnerProfileId, snapshot.OperatorId, snapshot.BotProfileId, entry.CorpseId, now, "Failed", "command_identity_not_confirmed", false, VanguardCorpseLootApproachDoctrine.FailureCooldownSeconds, out _);
                continue;
            }

            var lease = new ApproachLease
            {
                ClaimId = claim.ClaimId,
                LeaseId = leaseId,
                WindowId = windowId,
                OwnerProfileId = snapshot.OwnerProfileId,
                OperatorId = snapshot.OperatorId,
                BotProfileId = snapshot.BotProfileId,
                CorpseId = entry.CorpseId,
                CommandGeneration = command.Generation,
                Anchor = plan.Anchor,
                CorpsePosition = entry.Corpse.transform.position,
                StartedAtUtc = now,
                MinUntilUtc = now + TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.MinimumWindowSeconds),
                MaxUntilUtc = maxUntil,
                NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.NoProgressSeconds),
                LastProgressAtUtc = now,
                LastWorldPosition = botPosition,
                LastWorldSampleAtUtc = now,
                InitialAnchorDistance = HorizontalDistance(botPosition, plan.Anchor),
                LastAnchorDistance = HorizontalDistance(botPosition, plan.Anchor),
                LastCorpseDistance = HorizontalDistance(botPosition, entry.Corpse.transform.position),
                PlanSummary = plan.Summary,
                PreventSummary = preventSummary,
                AuthoringExcursion = authoredLootExcursion,
                AuthoringContextSummary = authoredLootExcursion ? authoredContext.Summary : authoredContextReason
            };
            if (!VanguardMainIntentScheduler.MarkCorpseLootApproachStarted(snapshot.BotProfileId, leaseId, now, lease.Summary, windowId))
            {
                VanguardReturnMovementCommandStore.ClearOwned(snapshot.BotProfileId, leaseId, now, "corpse_loot_scheduler_start_not_confirmed");
                VanguardCorpseLootClaimStore.Release(claim.ClaimId, "scheduler_start_not_confirmed", out _);
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "scheduler_start_not_confirmed", lease.Summary, windowId);
                _ = VanguardCorpseLootOutcomeMemory.Record(snapshot.OwnerProfileId, snapshot.OperatorId, snapshot.BotProfileId, entry.CorpseId, now, "Failed", "scheduler_start_not_confirmed", false, VanguardCorpseLootApproachDoctrine.FailureCooldownSeconds, out _);
                continue;
            }

            lock (Sync)
            {
                ActiveByBotProfileId[snapshot.BotProfileId] = lease;
            }

            VanguardCorpseLootOperationalTelemetry.RecordApproachStarted(snapshot.BotProfileId, entry.CorpseId, plan.PathDistance, plan.AddedDetour, plan.OwnerAnchorDistance);
            VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
                $"VANGUARD_CORPSE_LOOT_APPROACH_STARTED {lease.Summary}; claim={Safe(claim.Summary)}; plan={Safe(plan.Summary)}; prevent={Safe(preventSummary)}; interaction=false; transactions=false; equipmentMutation=false");
            return;
        }
    }

    // Revalidate every active approach against live raid truth before driving it again. This loop handles
    // disappearance/death, safety preemption, claim/assignment loss, path failure, arrival and handoff into
    // the native item-transaction session. Terminal paths all funnel through FinishLease so scheduler state,
    // claims and cooldown/outcome memory cannot diverge.
    private static void TickActive(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        ApproachLease[] active;
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

        foreach (ApproachLease original in active)
        {
            if (!byProfile.TryGetValue(original.BotProfileId, out OperatorDecisionSnapshot snapshot))
            {
                FinishLease(original, now, "Interrupted", "snapshot_missing", true, "missing", true);
                continue;
            }

            string interrupt = CheckActiveSessionSafetyGate(snapshot, now, activeWindow: true);
            if (!string.Equals(interrupt, "none", StringComparison.OrdinalIgnoreCase))
            {
                float cooldown = IsThreatReason(interrupt)
                    ? VanguardCorpseLootApproachDoctrine.ThreatInterruptCooldownSeconds
                    : VanguardCorpseLootApproachDoctrine.FailureCooldownSeconds;
                VanguardClientDiagnosticsLog.Operational(VanguardCorpseLootApproachDoctrine.TransactionStatusTag, () =>
                    $"VANGUARD_CORPSE_LOOT_ACTIVE_INTERRUPTION {original.Summary}; phase=approach; reason={Safe(interrupt)}; operationPrepared=false; operationSubmitted=false; mutationObserved=false; authorityYielded=true");
                FinishLease(original, now, "Interrupted", interrupt, true, snapshot.DecisionSignature, true, cooldown);
                continue;
            }

            if (!VanguardCorpseLootClaimStore.TryGetByBot(original.BotProfileId, now, out VanguardCorpseLootClaim claim)
                || !string.Equals(claim.ClaimId, original.ClaimId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(claim.CorpseId, original.CorpseId, StringComparison.OrdinalIgnoreCase))
            {
                FinishLease(original, now, "Interrupted", "claim_lost", true, snapshot.DecisionSignature, true);
                continue;
            }

            if (!VanguardCorpseRegistry.TryGet(original.CorpseId, now, out VanguardCorpseRegistryEntry entry) || entry.Corpse == null)
            {
                FinishLease(original, now, "Interrupted", "corpse_missing", true, snapshot.DecisionSignature, true);
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(original.BotProfileId, out VanguardRaidOperatorRuntimeRecord record)
                || record.BotOwner == null
                || record.BotOwner.IsDead)
            {
                FinishLease(original, now, "Interrupted", "bot_owner_missing_or_dead", true, snapshot.DecisionSignature, true);
                continue;
            }

            var lease = original;
            Vector3 botPosition = ResolveBotPosition(record.BotOwner);
            Vector3 corpsePosition = entry.Corpse.transform.position;
            float anchorDistance = HorizontalDistance(botPosition, lease.Anchor);
            float corpseDistance = HorizontalDistance(botPosition, corpsePosition);

            bool exactOwnedCommand = VanguardReturnMovementCommandStore.TryGetExactOwned(
                original.BotProfileId,
                original.LeaseId,
                VanguardCorpseLootApproachDoctrine.RequestKind,
                original.CommandGeneration,
                now,
                out _,
                out string commandReason);
            bool arrivedAfterOwnedCommandTerminal = !exactOwnedCommand
                && IsBoundedArrivalAfterOwnedCommandTerminal(commandReason, corpseDistance, anchorDistance);
            if (!exactOwnedCommand && !arrivedAfterOwnedCommandTerminal)
            {
                FinishLease(original, now, "Interrupted", "owned_movement_command_lost:" + commandReason, true, snapshot.DecisionSignature, true);
                continue;
            }

            if (arrivedAfterOwnedCommandTerminal)
            {
                VanguardCorpseLootClaimStore.Refresh(lease.ClaimId, now);
                if (now < lease.MinUntilUtc)
                {
                    lock (Sync)
                    {
                        ActiveByBotProfileId[lease.BotProfileId] = lease;
                    }

                    LogThrottled("arrival_grace|" + lease.BotProfileId + "|" + lease.CorpseId, now,
                        $"VANGUARD_CORPSE_ARRIVAL_COMMAND_TERMINAL_GRACE {lease.Summary}; commandReason={Safe(commandReason)}; corpseDistance={corpseDistance:0.00}; anchorDistance={anchorDistance:0.00}; minRemainingSeconds={(lease.MinUntilUtc - now).TotalSeconds:0.000}; exactOwnedCommand=false; foreignCommandAccepted=false; criticalSafetyGatesPreserved=true; failureCooldownApplied=false");
                    continue;
                }

                if (TryHandoffToTransactionSession(
                        lease,
                        snapshot,
                        now,
                        "owned_command_terminal_after_spatial_arrival:" + commandReason,
                        out string terminalArrivalSessionSummary))
                {
                    continue;
                }

                FinishLease(lease, now, "Failed", "arrival_transaction_handoff_failed:" + terminalArrivalSessionSummary, true, snapshot.DecisionSignature, true);
                continue;
            }

            TimeSpan sampleAge = now - lease.LastWorldSampleAtUtc;
            VanguardPhysicalMovementProgressEvaluation physical = VanguardMovementProgressEvaluator.EvaluatePhysical(
                lease.LastWorldPosition,
                botPosition,
                lease.LastAnchorDistance,
                anchorDistance,
                snapshot.RealSpeed,
                movementExpected: true,
                sampleAge);
            bool meaningfulGoalGain = anchorDistance < lease.LastAnchorDistance - VanguardCorpseLootApproachDoctrine.ProgressGainMeters
                || corpseDistance < lease.LastCorpseDistance - VanguardCorpseLootApproachDoctrine.ProgressGainMeters;

            if (sampleAge >= TimeSpan.FromSeconds(0.45d))
            {
                lease.LastWorldPosition = botPosition;
                lease.LastWorldSampleAtUtc = now;
            }

            if (physical.HasProgress && (meaningfulGoalGain || physical.GoalGainMeters > 0f))
            {
                lease.LastAnchorDistance = Math.Min(lease.LastAnchorDistance, anchorDistance);
                lease.LastCorpseDistance = Math.Min(lease.LastCorpseDistance, corpseDistance);
                lease.LastProgressAtUtc = now;
                lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.NoProgressSeconds);
                lease.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
                VanguardCorpseLootClaimStore.Refresh(lease.ClaimId, now);
                VanguardReturnMovementCommandStore.RefreshLeaseWindow(lease.BotProfileId, lease.MaxUntilUtc, "corpse_loot_approach_progress");
                VanguardMainIntentScheduler.ReportPrimaryProgress(lease.BotProfileId, now, "corpse_loot_" + physical.ProgressKind, lease.Summary, lease.WindowId);
                VanguardCorpseLootOperationalTelemetry.RecordApproachProgress(lease.BotProfileId, lease.CorpseId, corpseDistance);
                LogThrottled("progress|" + lease.BotProfileId, now,
                    $"VANGUARD_CORPSE_LOOT_APPROACH_PROGRESS {lease.Summary}; anchorDistance={anchorDistance:0.00}; corpseDistance={corpseDistance:0.00}; physical={Safe(physical.Summary)}; interactions=false; transactions=false");
            }
            else if (physical.LocomotionBlocked)
            {
                if (lease.PhysicalBlockedSinceUtc == DateTimeOffset.MinValue)
                {
                    lease.PhysicalBlockedSinceUtc = now;
                }
                double blockedSeconds = (now - lease.PhysicalBlockedSinceUtc).TotalSeconds;
                if (blockedSeconds >= VanguardCorpseLootApproachDoctrine.PhysicalRestartAfterSeconds && lease.PhysicalRestartCount < 1)
                {
                    if (VanguardReturnMovementCommandStore.TryRestartOwned(lease.LeaseId, lease.BotProfileId, now, physical.Summary, out string restartResult)
                        && VanguardReturnMovementCommandStore.TryGetActive(lease.BotProfileId, now, out VanguardReturnMovementCommand restarted)
                        && string.Equals(restarted.LeaseId, lease.LeaseId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(restarted.RequestKind, VanguardCorpseLootApproachDoctrine.RequestKind, StringComparison.OrdinalIgnoreCase))
                    {
                        lease.PhysicalRestartCount++;
                        lease.CommandGeneration = restarted.Generation;
                        lease.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
                        lease.LastWorldPosition = botPosition;
                        lease.LastWorldSampleAtUtc = now;
                        lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.NoProgressSeconds);
                        LogThrottled("restart|" + lease.BotProfileId, now,
                            $"VANGUARD_CORPSE_LOOT_APPROACH_RESTARTED {lease.Summary}; result={Safe(restartResult)}; boundedRestart=1");
                    }
                    else
                    {
                        FinishLease(lease, now, "Failed", "physical_restart_rejected", true, snapshot.DecisionSignature, true);
                        continue;
                    }
                }
                else if (blockedSeconds >= VanguardCorpseLootApproachDoctrine.PhysicalFailAfterRestartSeconds && lease.PhysicalRestartCount >= 1)
                {
                    FinishLease(lease, now, "Timeout", "locomotion_blocked_after_restart", true, snapshot.DecisionSignature, true);
                    continue;
                }
            }
            else if (sampleAge >= TimeSpan.FromSeconds(0.45d))
            {
                lease.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
            }

            lock (Sync)
            {
                ActiveByBotProfileId[lease.BotProfileId] = lease;
            }

            if (now >= lease.MinUntilUtc
                && (corpseDistance <= VanguardCorpseLootApproachDoctrine.CorpseInteractionDistanceMeters
                    || anchorDistance <= VanguardCorpseLootApproachDoctrine.ApproachAnchorRadiusMeters))
            {
                if (TryHandoffToTransactionSession(
                        lease,
                        snapshot,
                        now,
                        "exact_owned_command_spatial_arrival",
                        out string sessionSummary))
                {
                    continue;
                }

                FinishLease(lease, now, "Failed", "arrival_transaction_handoff_failed:" + sessionSummary, true, snapshot.DecisionSignature, true);
                continue;
            }

            if (now >= lease.MaxUntilUtc)
            {
                FinishLease(lease, now, "Timeout", "max_window_expired", true, snapshot.DecisionSignature, true);
                continue;
            }

            if (now >= lease.NoProgressUntilUtc)
            {
                FinishLease(lease, now, "Timeout", "no_progress_timeout", true, snapshot.DecisionSignature, true);
            }
        }
    }

    private static bool IsBoundedArrivalAfterOwnedCommandTerminal(
        string commandReason,
        float corpseDistance,
        float anchorDistance)
    {
        if (!string.Equals(commandReason, "active_command_missing_or_expired", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return corpseDistance <= VanguardCorpseLootApproachDoctrine.CorpseInteractionDistanceMeters
            || anchorDistance <= VanguardCorpseLootApproachDoctrine.ApproachAnchorRadiusMeters;
    }

    private static bool TryHandoffToTransactionSession(
        ApproachLease lease,
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        string arrivalProof,
        out string sessionSummary)
    {
        var sessionStart = new VanguardCorpseLootSessionStart
        {
            ClaimId = lease.ClaimId,
            LeaseId = lease.LeaseId,
            WindowId = lease.WindowId,
            OwnerProfileId = lease.OwnerProfileId,
            OperatorId = lease.OperatorId,
            BotProfileId = lease.BotProfileId,
            CorpseId = lease.CorpseId,
            ManifestRevision = snapshot.CorpseLoot.ManifestRevision,
            InterestRevision = snapshot.CorpseLoot.InterestRevision,
            NeedSignature = snapshot.CorpseLoot.LootNeedSignature,
            ApproachStartedAtUtc = lease.StartedAtUtc,
            SchedulerMaxUntilUtc = lease.StartedAtUtc + TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.SchedulerMaximumWindowSeconds),
            ApproachSummary = lease.Summary
        };
        if (!VanguardCorpseLootSessionExecutor.TryBegin(sessionStart, snapshot, now, out sessionSummary))
        {
            return false;
        }

        lock (Sync)
        {
            ActiveByBotProfileId.Remove(lease.BotProfileId);
        }

        var committedSessionSummary = sessionSummary;
        VanguardClientDiagnosticsLog.Operational(VanguardCorpseLootApproachDoctrine.TransactionStatusTag, () =>
            $"VANGUARD_CORPSE_LOOT_APPROACH_HANDOFF {lease.Summary}; session={Safe(committedSessionSummary)}; arrivalProof={Safe(arrivalProof)}; claimRetained=true; schedulerRetained=true; terminalDeferredToTransactionSession=true; interaction=false; sequentialAtomicTransactionsEligible=true");
        if (arrivalProof.StartsWith("owned_command_terminal_after_spatial_arrival", StringComparison.OrdinalIgnoreCase))
        {
            VanguardClientDiagnosticsLog.Operational(VanguardCorpseLootApproachDoctrine.StatusTag, () =>
                $"VANGUARD_CORPSE_ARRIVAL_HANDOFF_CONVERGED {lease.Summary}; arrivalProof={Safe(arrivalProof)}; failureCooldownApplied=false; exactOwnedCommandRequiredBeforeArrival=true; foreignCommandAccepted=false; criticalSafetyGatesPreserved=true");
        }

        return true;
    }

    private static bool CanYieldCurrentTravelCohesion(
        OperatorDecisionSnapshot snapshot,
        float targetDirectDistanceMeters,
        DateTimeOffset now,
        out string proof)
    {
        proof = "no_active_travel_command";
        return VanguardReturnMovementCommandStore.TryGetActive(snapshot.BotProfileId, now, out VanguardReturnMovementCommand activeCommand)
            && VanguardOpportunisticLootTravelYieldPolicy.CanYield(
                snapshot,
                activeCommand.RequestKind,
                targetDirectDistanceMeters,
                now,
                out proof);
    }

    private static string CheckSafetyGate(
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        ApproachLease? currentLease,
        bool allowAuthoringPreviewYield = false)
    {
        string criticalGate = CheckActiveSessionSafetyGate(snapshot, now, currentLease.HasValue);
        if (!string.Equals(criticalGate, "none", StringComparison.OrdinalIgnoreCase))
        {
            return criticalGate;
        }

        if (!currentLease.HasValue)
        {
            if (!snapshot.CorpseLoot.CandidateFound || !snapshot.CorpseLoot.ExecutionEnabled || !snapshot.CorpseLoot.EligibleIfActivated)
                return "corpse_candidate_not_executable:" + snapshot.CorpseLoot.Gate;
            if (VanguardReturnMovementCommandStore.TryGetActive(snapshot.BotProfileId, now, out VanguardReturnMovementCommand activeCommand)
                && VanguardPrimaryExecutionContract.ShouldKeepMovementContractUntilTerminal(snapshot, activeCommand.RequestKind, out string contractReason))
            {
                bool authoredYield = allowAuthoringPreviewYield
                    && string.Equals(activeCommand.RequestKind, VanguardTacticalAuthoringHeadlessPreviewService.RequestKind, StringComparison.OrdinalIgnoreCase);
                bool travelYield = VanguardOpportunisticLootTravelYieldPolicy.CanYield(
                    snapshot,
                    activeCommand.RequestKind,
                    snapshot.CorpseLoot.DirectDistanceMeters,
                    now,
                    out string travelYieldProof);
                if (!authoredYield && !travelYield)
                {
                    return "active_movement_contract_preserved:" + contractReason + ":loot_yield=" + travelYieldProof;
                }
            }
        }

        return "none";
    }

    internal static string CheckActiveSessionSafetyGate(OperatorDecisionSnapshot snapshot, DateTimeOffset now, bool activeWindow)
    {
        if (VanguardMainIntentScheduler.TryGetActiveEmergencyWindow(snapshot.BotProfileId, now, out _, out string grenadeKey, out _))
            return "grenade_emergency_primary:" + grenadeKey;
        if (!snapshot.Alive) return "operator_dead";
        if (!snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement || !snapshot.SquadCohesion.OwnerPosition.HasValue)
            return "owner_anchor_unreliable";
        float ownerLimit = activeWindow
            ? VanguardCorpseLootApproachDoctrine.MaximumActiveOwnerDistanceMeters
            : VanguardCorpseLootApproachDoctrine.MaximumStartOwnerDistanceMeters;
        if (snapshot.SquadCohesion.OperatorDistanceToOwner > ownerLimit)
            return "owner_hard_distance_exceeded:" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", CultureInfo.InvariantCulture);

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot)
            || snapshot.Threat.DirectThreat
            || snapshot.Threat.EnemyVisible == true
            || snapshot.Threat.EnemyCanShoot == true
            || snapshot.Threat.ShotMeRecently == true
            || snapshot.Threat.ShotAtMeRecently == true
            || snapshot.Sain.IsInCombat == true
            || snapshot.Sain.HasEnemy == true)
            return "combat_or_direct_threat";
        if (IsExplicitCombatBrainNode(snapshot))
            return "explicit_bigbrain_combat_node";
        if (IsCorroboratedCombatProductive(snapshot, out string productiveReason))
            return "corroborated_productive_combat:" + productiveReason;
        if (VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out string combatWindowReason))
            return "sain_combat_primary_protected:" + combatWindowReason;

        if (VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(snapshot)) return "stationary_medical_authority";
        if (VanguardExecutionLeaseCoordinator.HasActiveLease(snapshot.BotProfileId)) return "active_medical_lease";
        if (VanguardSurgeryDebtService.HasDueDebt(snapshot, out string debtReason)) return "surgery_debt:" + debtReason;
        if (snapshot.Medical.Need.HasHeavyBleed || snapshot.Medical.Need.HasLightBleed || snapshot.Medical.Safety.ImmediateCombatBlock) return "urgent_medical_or_bleeding";
        if (snapshot.Medical.Actionability.AnyMedicineUsing || snapshot.Medical.Actionability.Reloading || snapshot.Medical.Actionability.GrenadeThrowing) return "hands_or_medical_busy";
        if (snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true) return "lootingbots_authority_anomaly";
        if (snapshot.Orbit.Active) return "orbit_authority_anomaly";
        return "none";
    }

    private static string FinishLease(ApproachLease lease, DateTimeOffset now, string outcome, string reason, bool failureCooldown, string snapshotSignature, bool finishScheduler, float? cooldownOverride = null)
    {
        lock (Sync)
        {
            ActiveByBotProfileId.Remove(lease.BotProfileId);
        }

        string commandCleanup = VanguardReturnMovementCommandStore.ClearOwned(lease.BotProfileId, lease.LeaseId, lease.StartedAtUtc, "corpse_loot_finished:" + reason);
        bool completed = string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase);
        float cooldown = cooldownOverride ?? (failureCooldown
            ? VanguardCorpseLootApproachDoctrine.FailureCooldownSeconds
            : VanguardCorpseLootApproachDoctrine.SuccessCooldownSeconds);
        string outcomeMemoryScope = "no_outcome_memory_change";
        if (failureCooldown)
        {
            _ = VanguardCorpseLootOutcomeMemory.Record(
                lease.OwnerProfileId, lease.OperatorId, lease.BotProfileId, lease.CorpseId, now, outcome, reason, false, cooldown, out outcomeMemoryScope);
        }
        VanguardCorpseLootClaimStore.Release(lease.ClaimId, reason, out _);
        VanguardLootItemClaimStore.ReleaseByBot(lease.BotProfileId, "approach_finished:" + reason);
        if (finishScheduler)
        {
            VanguardMainIntentScheduler.FinishPrimaryWindow(lease.BotProfileId, now, outcome, reason, lease.Summary, lease.WindowId);
        }
        VanguardCorpseLootOperationalTelemetry.RecordApproachTerminal(outcome, reason, lease.BotProfileId, lease.CorpseId);
        string eventName = completed
            ? "VANGUARD_CORPSE_LOOT_APPROACH_COMPLETED"
            : "VANGUARD_CORPSE_LOOT_APPROACH_ABORTED";
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"{eventName} {lease.Summary}; outcome={Safe(outcome)}; reason={Safe(reason)}; commandCleanup={Safe(commandCleanup)}; snapshot={Safe(snapshotSignature)}; cooldown={cooldown:0.0}; arrivedOnly={Bool(completed)}; outcomeMemoryScope={Safe(outcomeMemoryScope)}; ownerSquadTerminalRemoved=true; terminalCommittedBeforeClaimRelease=false; interaction=false; transactions=false; equipmentMutation=false");
        return "lease=" + Safe(lease.LeaseId) + ";outcome=" + Safe(outcome) + ";reason=" + Safe(reason) + ";command=" + Safe(commandCleanup);
    }

    private static bool NeedsExternalPreempt(OperatorDecisionSnapshot snapshot)
        => snapshot.Orbit.Active
            || snapshot.Movement.HasPath == true
            || snapshot.Looting.HasActiveLootable == true
            || snapshot.Looting.BotLooting == true
            || snapshot.Looting.LootTaskRunning == true;

    private static bool IsFeatureEnabled()
        => VanguardCorpseLootApproachDoctrine.ApproachExecutionEnabled
            && VanguardCorpseLootApproachDoctrine.ClaimAuthorityEnabled;

    private static float ActiveCandidateScore(OperatorDecisionSnapshot snapshot)
    {
        float score = Math.Max(0f, snapshot.CorpseLoot.UtilityScore);
        // Formation topology chooses the least disruptive looter; it never blocks admission.
        if (snapshot.SquadCohesion.SectorDuplicate) score += 7f;
        if (snapshot.SquadCohesion.RearOverstacked) score += 5f;
        if (!snapshot.SquadCohesion.UsefulPosition) score += 3f;
        if (!snapshot.SquadCohesion.SectorTopologyValid) score += 1f;
        score -= Math.Min(12f, Math.Max(0f, snapshot.SquadCohesion.OperatorDistanceToOwner) * 0.12f);
        return score;
    }

    private static bool IsCorroboratedCombatProductive(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (!VanguardMovementAuthorityDoctrine.IsCombatProductive(snapshot, out string productiveReason))
        {
            return false;
        }

        bool qualifiedThreat = (!string.IsNullOrWhiteSpace(snapshot.Threat.EnemyId)
                && !string.Equals(snapshot.Threat.EnemyId, "none", StringComparison.OrdinalIgnoreCase)
                && !snapshot.Threat.StaleThreat)
            || snapshot.Awareness.IncomingFireFresh
            || snapshot.Awareness.CandidateCanShoot
            || snapshot.ThreatScan.CandidateIncomingFireFresh
            || snapshot.ThreatScan.CandidateCanShoot
            || snapshot.ThreatScan.CandidateShotMeRecently
            || snapshot.ThreatScan.CandidateShotAtMeRecently;
        if (!qualifiedThreat)
        {
            return false;
        }

        reason = productiveReason + ":corroborated_by_local_threat_evidence";
        return true;
    }

    private static bool IsExplicitCombatBrainNode(OperatorDecisionSnapshot snapshot)
    {
        static bool ContainsCombat(string? value)
            => !string.IsNullOrWhiteSpace(value)
                && value.IndexOf("combat", StringComparison.OrdinalIgnoreCase) >= 0;

        return ContainsCombat(snapshot.Brain.Node)
            || ContainsCombat(snapshot.Brain.ActiveLayer)
            || ContainsCombat(snapshot.Brain.CustomAction)
            || ContainsCombat(snapshot.Brain.CustomLayer);
    }

    private static bool IsThreatReason(string reason)
        => reason.IndexOf("threat", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("combat", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("assignment", StringComparison.OrdinalIgnoreCase) >= 0;

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
            if (LastLogByKey.TryGetValue(key, out DateTimeOffset last) && now - last < LogInterval) return;
            LastLogByKey[key] = now;
        }
        VanguardClientDiagnosticsLog.Operational(StatusTag, () => message);
    }

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private struct ApproachLease
    {
        public string ClaimId;
        public string LeaseId;
        public string WindowId;
        public string OwnerProfileId;
        public string OperatorId;
        public string BotProfileId;
        public string CorpseId;
        public long CommandGeneration;
        public Vector3 Anchor;
        public Vector3 CorpsePosition;
        public DateTimeOffset StartedAtUtc;
        public DateTimeOffset MinUntilUtc;
        public DateTimeOffset MaxUntilUtc;
        public DateTimeOffset NoProgressUntilUtc;
        public DateTimeOffset LastProgressAtUtc;
        public Vector3 LastWorldPosition;
        public DateTimeOffset LastWorldSampleAtUtc;
        public DateTimeOffset PhysicalBlockedSinceUtc;
        public int PhysicalRestartCount;
        public float InitialAnchorDistance;
        public float LastAnchorDistance;
        public float LastCorpseDistance;
        public string PlanSummary;
        public string PreventSummary;
        public bool AuthoringExcursion;
        public string AuthoringContextSummary;

        public string Summary => $"claim={Safe(ClaimId)}; lease={Safe(LeaseId)}; window={Safe(WindowId)}; owner={Safe(OwnerProfileId)}; operator={Safe(OperatorId)}; botProfile={Safe(BotProfileId)}; corpse={Safe(CorpseId)}; generation={CommandGeneration}; anchor={Anchor.x:0.00},{Anchor.y:0.00},{Anchor.z:0.00}; anchorInitial={InitialAnchorDistance:0.00}; anchorLast={LastAnchorDistance:0.00}; corpseLast={LastCorpseDistance:0.00}; restarts={PhysicalRestartCount}; authoringExcursion={Bool(AuthoringExcursion)}; authoringContext={Safe(AuthoringContextSummary)}; started={StartedAtUtc:O}; max={MaxUntilUtc:O}; plan={Safe(PlanSummary)}";
    }
}
#endif
