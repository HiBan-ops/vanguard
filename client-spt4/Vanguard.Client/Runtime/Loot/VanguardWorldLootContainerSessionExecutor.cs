#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;

// Responsibility: performs the bounded interaction and inventory transfer once an Operator has reached an authorized world-loot container.
// Flow: The container is re-resolved, ownership/safety and current inventory needs are checked, eligible contents are planned and transferred through EFT inventory operations, then readback determines success/failure before the session and target claim are closed.
// Authority boundary: container selection and loot desirability are decided upstream; EFT inventory state remains transaction authority and this executor cannot transfer items outside the validated plan.
// Invariant: one session owns one Operator/container/window, item commits are readback-checked, and every terminal path releases interaction/claim state without leaving a stale loot lock.
namespace Vanguard.Client.Runtime.Loot;

/// <summary>
/// Executes a bounded world-container micro-session while retaining the exact target claim and scheduler window.
/// Player-interest sessions may commit at most two admitted items; other utility tiers remain one useful commit per visit.
/// The item transaction pipeline is the sole inventory-mutation authority, and every native EFT/Fika submission is
/// reconciled independently by source/destination readback.
/// </summary>
internal static class VanguardWorldLootContainerSessionExecutor
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Session> ActiveByBot = new(StringComparer.OrdinalIgnoreCase);

    public static bool HasInFlightNativeTransaction(out string summary)
    {
        lock (Sync)
        {
            Session[] inFlight = ActiveByBot.Values.Where(session => session.Submitted).ToArray();
            if (inFlight.Length == 0)
            {
                summary = "none";
                return false;
            }

            summary = string.Join(",", inFlight.Select(session =>
                $"bot={Safe(session.BotProfileId)}:container={Safe(session.ContainerId)}:callback={Bool(session.CallbackReceived)}:returned={Bool(session.SubmitCallReturned)}"));
            return true;
        }
    }

    public static void ResetForRaidLifecycle(string reason)
    {
        Session[] active;
        lock (Sync) { active = ActiveByBot.Values.ToArray(); ActiveByBot.Clear(); }
        foreach (Session session in active) Complete(session, DateTimeOffset.UtcNow, "Interrupted", "raid_reset:" + reason, true, null);
    }

    public static bool TryBegin(
        string claimId, string leaseId, string windowId, string ownerProfileId, string operatorId, string botProfileId,
        string containerId, long approachManifestRevision, float handoffDistanceMeters, OperatorDecisionSnapshot snapshot,
        DateTimeOffset approachStartedAtUtc, DateTimeOffset now, string approachSummary, out string summary)
    {
        summary = "none";
        if (!VanguardLootTargetClaimStore.TryGetByBot(botProfileId, now, out VanguardLootTargetClaim targetClaim)
            || targetClaim.TargetKind != VanguardLootTargetKind.WorldContainer
            || !string.Equals(targetClaim.ClaimId, claimId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(targetClaim.TargetId, containerId, StringComparison.OrdinalIgnoreCase))
        { summary = "target_claim_invalid"; return false; }
        if (!TryResolveContainer(containerId, now, out VanguardWorldLootContainerSnapshot container, out string liveReason))
        { summary = "container_invalid:" + liveReason; return false; }
        if (container.Container.DoorState != EFT.Interactive.EDoorState.Open)
        { summary = "container_not_open:" + container.Container.DoorState; return false; }
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botProfileId, out VanguardRaidOperatorRuntimeRecord record)
            || record.BotOwner == null || record.BotOwner.IsDead)
        { summary = "bot_owner_missing_or_dead"; return false; }

        if (snapshot == null || !string.Equals(snapshot.BotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase))
        { summary = "handoff_snapshot_identity_mismatch"; return false; }

        if (!TryRefreshAssignmentContext(
                record,
                container,
                snapshot,
                Math.Max(0f, handoffDistanceMeters),
                now,
                null,
                out VanguardUnifiedLootReadModelObservation observation,
                out VanguardSquadLootItemAssignment best,
                out string assignmentReason))
        {
            summary = "assignment_missing_after_open_handoff_refresh:" + assignmentReason;
            return false;
        }

        var session = new Session
        {
            ClaimId = claimId, LeaseId = leaseId, WindowId = windowId, OwnerProfileId = ownerProfileId, OperatorId = operatorId,
            BotProfileId = botProfileId, ContainerId = containerId, ApproachManifestRevision = approachManifestRevision,
            ManifestRevision = observation.ManifestRevision, InterestRevision = observation.InterestRevision,
            NeedSignature = observation.NeedSignature, HandoffDistanceMeters = Math.Max(0f, handoffDistanceMeters), StartedAtUtc = now,
            ApproachStartedAtUtc = approachStartedAtUtc, AssignmentObservedAtUtc = best.ObservedAtUtc,
            PreparationDeadlineUtc = now + VanguardCorpseLootApproachDoctrine.TransactionPreparationMaximumDuration,
            TransactionDeadlineUtc = now + VanguardCorpseLootApproachDoctrine.TransactionMaximumDuration,
            Limits = VanguardCorpseLootSessionLimits.CaptureRuntime(ownerProfileId),
            Progress = new VanguardCorpseLootSessionProgress(), ApproachSummary = approachSummary
        };
        VanguardClientDiagnosticsLog.Operational(
            VanguardWorldLootContainerApproachDoctrine.TransactionStatusTag,
            () => $"VANGUARD_CONTAINER_OPEN_HANDOFF_ASSIGNMENT_REFRESHED owner={Safe(ownerProfileId)}; operator={Safe(operatorId)}; bot={Safe(botProfileId)}; container={Safe(containerId)}; approachManifestRevision={approachManifestRevision}; refreshedManifestRevision={observation.ManifestRevision}; refreshedInterestRevision={observation.InterestRevision}; item={Safe(best.ItemId)}; score={best.ExecutionScore:0.0}; reason={Safe(assignmentReason)}; staleAssignmentCommit=false; targetClaimRetained=true");
        if (!VanguardLootTargetClaimStore.Refresh(claimId, VanguardLootTargetKind.WorldContainer, now))
        {
            summary = "target_claim_refresh_failed_at_handoff";
            return false;
        }
        lock (Sync)
        {
            if (ActiveByBot.ContainsKey(botProfileId)) { summary = "container_session_already_active"; return false; }
            ActiveByBot.Add(botProfileId, session);
        }
        VanguardMainIntentScheduler.ReportPrimaryProgress(botProfileId, now, "world_container_transaction_handoff", session.Summary, windowId);
        VanguardClientDiagnosticsLog.Operational(VanguardWorldLootContainerApproachDoctrine.TransactionStatusTag, () =>
            $"VANGUARD_CONTAINER_TRANSACTION_SESSION_ACQUIRED {session.Summary}; targetClaimRetained=true; schedulerRetained=true; itemClaimPending=true; fikaPacketDirect=false; nativeInventoryController=true");
        summary = session.Summary;
        return true;
    }


    public static bool TryTerminateSchedulerExpiredWindow(string botProfileId, string windowId, DateTimeOffset now, string timeoutReason, out string summary)
    {
        Session session;
        lock (Sync)
        {
            if (!ActiveByBot.TryGetValue(botProfileId, out Session found)
                || !string.Equals(found.WindowId, windowId, StringComparison.OrdinalIgnoreCase))
            {
                summary = "active_world_container_session_not_found";
                return false;
            }

            session = found;
            if (session.Submitted)
            {
                session.SchedulerYielded = true;
                summary = "transaction_inflight_monitor_retained;" + session.Summary;
                return true;
            }
        }

        summary = Finish(session, now, "Timeout", "scheduler_expired:" + timeoutReason, finishScheduler: false, preflight: null);
        return true;
    }

    public static void Tick(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        Session[] active; lock (Sync) active = ActiveByBot.Values.ToArray();
        if (active.Length == 0) return;
        var byBot = (snapshots ?? Array.Empty<OperatorDecisionSnapshot>()).Where(x => x != null)
            .GroupBy(x => x.BotProfileId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (Session session in active)
        {
            byBot.TryGetValue(session.BotProfileId, out OperatorDecisionSnapshot? snapshot);
            if (session.Submitted) { TickSubmitted(session, snapshot, now); continue; }
            if (snapshot == null) { Finish(session, now, "Interrupted", "snapshot_missing", true, null); continue; }
            string gate = VanguardCorpseLootApproachExecutor.CheckActiveSessionSafetyGate(snapshot, now, activeWindow: true);
            if (!string.Equals(gate, "none", StringComparison.OrdinalIgnoreCase)) { Finish(session, now, "Interrupted", "pre_submit_safety_gate:" + gate, true, null); continue; }
            if (!TargetClaimMatches(session, now)) { Finish(session, now, "Interrupted", "target_claim_lost_before_submit", true, null); continue; }
            if (!TryResolveContainer(session.ContainerId, now, out VanguardWorldLootContainerSnapshot container, out string liveReason)) { Finish(session, now, "Failed", "container_invalid_before_submit:" + liveReason, true, null); continue; }
            if (container.Container.DoorState != EFT.Interactive.EDoorState.Open) { Finish(session, now, "Failed", "container_not_open_before_submit", true, null); continue; }
            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(session.BotProfileId, out VanguardRaidOperatorRuntimeRecord record) || record.BotOwner == null || record.BotOwner.IsDead)
            { Finish(session, now, "Interrupted", "bot_owner_missing_or_dead", true, null); continue; }
            VanguardOperatorLootPermissionSnapshot permissions = VanguardOperatorLootPermissionSnapshot.CaptureRuntime(record);
            if (!VanguardOperatorLootTargetPermissionPolicy.AllowsTarget(permissions, VanguardLootTargetKind.WorldContainer, out string permissionReason))
            { Finish(session, now, "Interrupted", "permission_revoked:" + permissionReason, true, null); continue; }
            if (now >= session.PreparationDeadlineUtc) { Finish(session, now, "Failed", "transaction_preparation_timeout", true, null); continue; }

            VanguardOwnerLootInterestSnapshot interest = VanguardOwnerLootInterestSyncService.Resolve(session.OwnerProfileId);
            VanguardOperatorLootNeedSnapshot need = VanguardOperatorLootNeedReader.Capture(record.BotOwner);
            string currentNeedSignature = need.DecisionSignature + "||medical=" + (snapshot.Medical?.Need?.Summary ?? "none");
            if ((interest.Known && interest.Revision != session.InterestRevision)
                || !string.Equals(Norm(currentNeedSignature), Norm(session.NeedSignature), StringComparison.Ordinal))
            {
                long previousManifestRevision = session.ManifestRevision;
                long previousInterestRevision = session.InterestRevision;
                string previousNeedSignature = session.NeedSignature;
                if (!TryRefreshAssignmentContext(
                        record,
                        container,
                        snapshot,
                        session.HandoffDistanceMeters,
                        now,
                        null,
                        out VanguardUnifiedLootReadModelObservation refreshedObservation,
                        out VanguardSquadLootItemAssignment refreshedAssignment,
                        out string refreshReason))
                {
                    Finish(session, now, "Completed", "utility_context_changed_no_current_assignment:" + refreshReason, true, null);
                    continue;
                }

                session.ManifestRevision = refreshedObservation.ManifestRevision;
                session.InterestRevision = refreshedObservation.InterestRevision;
                session.NeedSignature = refreshedObservation.NeedSignature;
                session.AssignmentObservedAtUtc = refreshedAssignment.ObservedAtUtc;
                VanguardClientDiagnosticsLog.Operational(
                    VanguardWorldLootContainerApproachDoctrine.TransactionStatusTag,
                    () => $"VANGUARD_CONTAINER_TRANSACTION_CONTEXT_REBASED {session.Summary}; previousManifestRevision={previousManifestRevision}; previousInterestRevision={previousInterestRevision}; previousNeed={Safe(previousNeedSignature)}; refreshedManifestRevision={refreshedObservation.ManifestRevision}; refreshedInterestRevision={refreshedObservation.InterestRevision}; refreshedNeed={Safe(refreshedObservation.NeedSignature)}; item={Safe(refreshedAssignment.ItemId)}; reason={Safe(refreshReason)}; staleCommit=false");
            }

            if (!VanguardCorpseLootTransactionPreflight.TryPrepareWorldContainer(container, record.BotOwner, session.Limits, session.Progress,
                    session.OwnerProfileId, session.BotProfileId, session.ContainerId, session.ManifestRevision, session.InterestRevision, session.NeedSignature,
                    out VanguardCorpseLootPreparedTransaction prepared, out VanguardCorpseLootTransactionPreflightResult preflight))
            { Finish(session, now, "Completed", "container_item_preflight_unavailable:" + preflight.Reason, true, preflight); continue; }

            gate = VanguardCorpseLootApproachExecutor.CheckActiveSessionSafetyGate(snapshot, now, activeWindow: true);
            if (!string.Equals(gate, "none", StringComparison.OrdinalIgnoreCase)) { VanguardLootItemClaimStore.Release(prepared.ItemClaim.ClaimId, "pre_submit_safety_gate", out _); Finish(session, now, "Interrupted", "prepared_pre_submit_safety_gate:" + gate, true, preflight); continue; }
            if (!TargetClaimMatches(session, now)) { VanguardLootItemClaimStore.Release(prepared.ItemClaim.ClaimId, "target_claim_lost", out _); Finish(session, now, "Interrupted", "target_claim_lost_after_prepare", true, preflight); continue; }
            if (!VanguardCorpseLootTransactionPreflight.RevalidateWorldContainer(prepared, container, record.BotOwner, session.Limits, session.Progress,
                    session.OwnerProfileId, session.BotProfileId, session.ContainerId, session.ManifestRevision, session.InterestRevision, session.NeedSignature, out string revalidateReason))
            { VanguardLootItemClaimStore.Release(prepared.ItemClaim.ClaimId, "revalidation_failed", out _); Finish(session, now, "Failed", "final_revalidation_failed:" + revalidateReason, true, preflight); continue; }
            if (!VanguardLootTargetClaimStore.Refresh(session.ClaimId, VanguardLootTargetKind.WorldContainer, now)) { VanguardLootItemClaimStore.Release(prepared.ItemClaim.ClaimId, "target_refresh_failed", out _); Finish(session, now, "Interrupted", "target_claim_refresh_failed", true, preflight); continue; }
            Submit(session, prepared, container, record.BotOwner, now);
        }
    }

    private static bool TryRefreshAssignmentContext(
        VanguardRaidOperatorRuntimeRecord record,
        VanguardWorldLootContainerSnapshot container,
        OperatorDecisionSnapshot snapshot,
        float directDistanceMeters,
        DateTimeOffset now,
        VanguardLootUtilityTier? requiredTier,
        out VanguardUnifiedLootReadModelObservation observation,
        out VanguardSquadLootItemAssignment assignment,
        out string reason)
    {
        observation = null!;
        assignment = null!;
        if (record.BotOwner == null || record.BotOwner.IsDead)
        {
            reason = "bot_owner_missing_or_dead";
            return false;
        }

        try
        {
            VanguardOperatorLootNeedSnapshot need = VanguardOperatorLootNeedReader.Capture(record.BotOwner);
            VanguardMedicalInventoryReadResult medicalInventory = VanguardMedicalInventoryReader.Capture(record.BotOwner);
            observation = VanguardUnifiedOpportunisticLootReadModelService.Observe(
                record,
                container,
                need,
                snapshot.Medical,
                medicalInventory,
                Math.Max(0f, directDistanceMeters),
                now);

            IReadOnlyList<VanguardSquadLootItemAssignment> assignments = VanguardSquadLootAssignmentService.GetAssignmentsForBot(
                record.OwnerProfileId,
                VanguardLootTargetKind.WorldContainer,
                container.ContainerId,
                record.BotProfileId,
                observation.ManifestRevision,
                now);
            VanguardSquadLootItemAssignment? best = assignments
                .Where(value => !requiredTier.HasValue || value.Tier == requiredTier.Value)
                .OrderByDescending(value => value.ExecutionScore)
                .ThenBy(value => value.ItemId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (best == null)
            {
                reason = $"no_current_utility_assignment:requiredTier={(requiredTier?.ToString() ?? "any")}:manifest={observation.ManifestRevision}:interest={observation.InterestRevision}:need={Safe(observation.NeedSignature)}";
                return false;
            }

            assignment = best;
            reason = $"current_assignment_refreshed:requiredTier={(requiredTier?.ToString() ?? "any")}:manifest={observation.ManifestRevision}:interest={observation.InterestRevision}:item={Safe(best.ItemId)}";
            return true;
        }
        catch (Exception exception)
        {
            reason = "assignment_refresh_exception:" + exception.GetType().Name;
            return false;
        }
    }

    private static void Submit(Session session, VanguardCorpseLootPreparedTransaction prepared, VanguardWorldLootContainerSnapshot container, BotOwner botOwner, DateTimeOffset now)
    {
        session.Prepared = prepared;
        session.Container = container;
        session.BotOwner = botOwner;
        session.Submitted = true;
        session.SubmittedAtUtc = now;
        session.SubmissionGeneration++;
        session.SubmitCallReturned = false;
        session.SubmitException = false;
        session.CallbackReceived = false;
        session.CallbackSucceeded = false;
        session.CallbackReason = "none";
        session.CallbackAtUtc = DateTimeOffset.MinValue;
        session.TransactionDeadlineUtc = now + VanguardCorpseLootApproachDoctrine.TransactionMaximumDuration;
        int submissionGeneration = session.SubmissionGeneration;
        VanguardMainIntentScheduler.ReportPrimaryProgress(session.BotProfileId, now, "world_container_transaction_submitted", prepared.Preflight.Summary, session.WindowId);
        VanguardClientDiagnosticsLog.Operational(VanguardWorldLootContainerApproachDoctrine.TransactionStatusTag, () =>
            $"VANGUARD_CONTAINER_TRANSACTION_SUBMIT_ATTEMPTED {session.Summary}; {prepared.Preflight.Summary}; itemClaim={Safe(prepared.ItemClaim.ClaimId)}; submissionGeneration={submissionGeneration}; nativeInventoryController=true; fikaPacketDirect=false; callbackPending=true");
        try
        {
            prepared.Inventory.RunNetworkTransaction(prepared.Operation, new Callback(result => OnCallback(session.BotProfileId, session.LeaseId, submissionGeneration, result)));
            session.SubmitCallReturned = true;
        }
        catch (Exception exception)
        {
            session.SubmitException = true;
            session.CallbackReason = "submit_exception:" + exception.GetType().Name;
            VanguardClientDiagnosticsLog.Warning(VanguardWorldLootContainerApproachDoctrine.TransactionStatusTag, () =>
                $"VANGUARD_CONTAINER_TRANSACTION_SUBMIT_EXCEPTION {session.Summary}; type={Safe(exception.GetType().Name)}; submissionGeneration={submissionGeneration}; duplicateSubmitForbiddenWithinGeneration=true; reconciliationRequired=true");
        }
    }

    private static void OnCallback(string botProfileId, string leaseId, int submissionGeneration, IResult? result)
    {
        lock (Sync)
        {
            if (!ActiveByBot.TryGetValue(botProfileId, out Session? session)
                || !string.Equals(session.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase)
                || !session.Submitted
                || session.SubmissionGeneration != submissionGeneration)
            {
                return;
            }
            session.CallbackReceived = true;
            session.CallbackSucceeded = result?.Succeed == true;
            session.CallbackReason = result == null ? "callback_result_null" : session.CallbackSucceeded ? "callback_success" : "callback_failed:" + Safe(result.ToString());
            session.CallbackAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private static void TickSubmitted(Session session, OperatorDecisionSnapshot? snapshot, DateTimeOffset now)
    {
        // The physical target claim remains authoritative while the native transaction converges.
        // Losing it after submission must not trigger a second submit; reconciliation remains readback-only.
        VanguardLootTargetClaimStore.Refresh(session.ClaimId, VanguardLootTargetKind.WorldContainer, now);
        if (!session.SchedulerYielded && snapshot != null)
        {
            string gate = VanguardCorpseLootApproachExecutor.CheckActiveSessionSafetyGate(snapshot, now, activeWindow: true);
            if (!string.Equals(gate, "none", StringComparison.OrdinalIgnoreCase))
            {
                session.SchedulerYielded = VanguardMainIntentScheduler.FinishPrimaryWindow(session.BotProfileId, now, "Interrupted", "container_transaction_inflight_authority_yield:" + gate, session.Summary, session.WindowId);
            }
        }
        VanguardCorpseLootPreparedTransaction? prepared = session.Prepared;
        if (prepared == null) { Finish(session, now, "Failed", "submitted_without_prepared", !session.SchedulerYielded, null); return; }
        bool destinationObserved = false, itemInOperator = false, sourceObserved = false, itemStillInSource = false;
        try { destinationObserved = true; itemInOperator = prepared.Inventory.TryFindItem(prepared.Item.Id, out Item found) && ReferenceEquals(found, prepared.Item); } catch { }
        if (session.Container != null)
        {
            try { sourceObserved = true; itemStillInSource = VanguardWorldLootContainerLiveItemResolver.TryResolve(session.Container, prepared.Item.Id, out Item sourceItem, out _, out _) && ReferenceEquals(sourceItem, prepared.Item); } catch { }
        }
        bool confirmed = destinationObserved && sourceObserved && itemInOperator && !itemStillInSource;
        if (confirmed)
        {
            VanguardCorpseLootPostCommitReadBackResult readBack = VanguardCorpseLootPostCommitReadBack.RefreshAndVerify(prepared, session.BotOwner);
            if (!readBack.Success) { Finish(session, now, "Failed", "post_commit_runtime_readback_failed:" + readBack.Reason, !session.SchedulerYielded, prepared.Preflight); return; }
            session.Progress.Record(prepared.Preflight);
            if (string.Equals(prepared.Preflight.Category, "long_weapon", StringComparison.OrdinalIgnoreCase))
            {
                VanguardUnifiedOpportunisticLootReadModelService.InvalidateWeaponContext(session.BotProfileId, "container_long_weapon_commit");
            }
            VanguardLootItemClaimStore.Release(prepared.ItemClaim.ClaimId, "confirmed_container_item_mutation", out _);
            double assignmentToCommitSeconds = session.AssignmentObservedAtUtc == DateTimeOffset.MinValue ? -1d : Math.Max(0d, (now - session.AssignmentObservedAtUtc).TotalSeconds);
            double approachToCommitSeconds = session.ApproachStartedAtUtc == DateTimeOffset.MinValue ? -1d : Math.Max(0d, (now - session.ApproachStartedAtUtc).TotalSeconds);
            double handoffToCommitSeconds = Math.Max(0d, (now - session.StartedAtUtc).TotalSeconds);
            double submitToCommitSeconds = session.SubmittedAtUtc == DateTimeOffset.MinValue ? -1d : Math.Max(0d, (now - session.SubmittedAtUtc).TotalSeconds);
            VanguardClientDiagnosticsLog.Operational(VanguardWorldLootContainerApproachDoctrine.TransactionStatusTag, () =>
                $"VANGUARD_CONTAINER_ITEM_COMMITTED {session.Summary}; item={Safe(prepared.Item.Id)}; sourceAbsent=true; destinationPresent=true; runtimeReadBack=true; squadReassignmentPending=true");
            VanguardClientDiagnosticsLog.Operational(VanguardOpportunisticLootTravelYieldPolicy.StatusTag, () =>
                $"VANGUARD_CONTAINER_LOOT_LATENCY owner={Safe(session.OwnerProfileId)}; operator={Safe(session.OperatorId)}; bot={Safe(session.BotProfileId)}; container={Safe(session.ContainerId)}; item={Safe(prepared.Item.Id)}; tier={Safe(prepared.Preflight.AssignmentTier)}; commitOrdinal={session.Progress.CommittedTransactions}; assignmentToCommitSeconds={assignmentToCommitSeconds:0.000}; approachToCommitSeconds={approachToCommitSeconds:0.000}; handoffToCommitSeconds={handoffToCommitSeconds:0.000}; submitToCommitSeconds={submitToCommitSeconds:0.000}; measurement=runtime_authority_latency_not_animation_latency");

            if (TryContinuePlayerInterestMicroSession(session, snapshot, now, prepared.Preflight, out string continuationReason))
            {
                VanguardClientDiagnosticsLog.Operational(VanguardWorldLootContainerApproachDoctrine.TransactionStatusTag, () =>
                    $"VANGUARD_CONTAINER_PLAYER_INTEREST_MICRO_SESSION_CONTINUED {session.Summary}; previousItem={Safe(prepared.Item.Id)}; reason={Safe(continuationReason)}; maximumCommits={VanguardWorldLootContainerApproachDoctrine.PlayerInterestMaximumCommitsPerVisit}; nonPlayerInterestStillSingleCommit=true; targetClaimRetained=true; schedulerRetained=true");
                return;
            }

            Finish(session, now, "Completed", "utility_claim_item_committed_reassign_squad:" + continuationReason, !session.SchedulerYielded, prepared.Preflight);
            return;
        }
        DateTimeOffset reconcileAfter = session.CallbackAtUtc == DateTimeOffset.MinValue ? session.TransactionDeadlineUtc : session.CallbackAtUtc + VanguardCorpseLootApproachDoctrine.TransactionReconciliationGrace;
        if (now < session.TransactionDeadlineUtc && (!session.CallbackReceived || now < reconcileAfter)) return;
        Finish(session, now, "Failed", "transaction_result_uncertain_fail_closed", !session.SchedulerYielded, prepared.Preflight);
    }

    private static bool TryContinuePlayerInterestMicroSession(
        Session session,
        OperatorDecisionSnapshot? snapshot,
        DateTimeOffset now,
        VanguardCorpseLootTransactionPreflightResult committed,
        out string reason)
    {
        reason = "micro_session_not_eligible";
        if (!string.Equals(committed.AssignmentTier, VanguardLootUtilityTier.PlayerInterest.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            reason = "committed_tier_not_player_interest:" + Safe(committed.AssignmentTier);
            return false;
        }
        if (session.Progress.CommittedTransactions >= VanguardWorldLootContainerApproachDoctrine.PlayerInterestMaximumCommitsPerVisit)
        {
            reason = "player_interest_commit_cap_reached:" + session.Progress.CommittedTransactions;
            return false;
        }
        if (session.SchedulerYielded)
        {
            reason = "scheduler_already_yielded";
            return false;
        }
        if (snapshot == null || !string.Equals(snapshot.BotProfileId, session.BotProfileId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "snapshot_missing_for_micro_session";
            return false;
        }
        if ((now - session.StartedAtUtc).TotalSeconds > VanguardWorldLootContainerApproachDoctrine.PlayerInterestMicroSessionMaximumSeconds)
        {
            reason = "micro_session_time_cap_reached";
            return false;
        }

        string gate = VanguardCorpseLootApproachExecutor.CheckActiveSessionSafetyGate(snapshot, now, activeWindow: true);
        if (!string.Equals(gate, "none", StringComparison.OrdinalIgnoreCase))
        {
            reason = "micro_session_safety_gate:" + gate;
            return false;
        }
        if (!TargetClaimMatches(session, now))
        {
            reason = "target_claim_lost_before_micro_session_refresh";
            return false;
        }
        if (!TryResolveContainer(session.ContainerId, now, out VanguardWorldLootContainerSnapshot container, out string liveReason))
        {
            reason = "container_invalid_for_micro_session:" + liveReason;
            return false;
        }
        if (container.Container.DoorState != EFT.Interactive.EDoorState.Open)
        {
            reason = "container_closed_before_micro_session_refresh";
            return false;
        }
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(session.BotProfileId, out VanguardRaidOperatorRuntimeRecord record)
            || record.BotOwner == null
            || record.BotOwner.IsDead)
        {
            reason = "bot_owner_missing_for_micro_session";
            return false;
        }

        _ = VanguardCorpseLootManifestService.Invalidate(VanguardLootTargetKind.WorldContainer, session.ContainerId);
        if (!TryRefreshAssignmentContext(
                record,
                container,
                snapshot,
                session.HandoffDistanceMeters,
                now,
                VanguardLootUtilityTier.PlayerInterest,
                out VanguardUnifiedLootReadModelObservation observation,
                out VanguardSquadLootItemAssignment next,
                out string refreshReason))
        {
            reason = "no_second_player_interest_assignment:" + refreshReason;
            return false;
        }

        session.ManifestRevision = observation.ManifestRevision;
        session.InterestRevision = observation.InterestRevision;
        session.NeedSignature = observation.NeedSignature;
        session.AssignmentObservedAtUtc = next.ObservedAtUtc;
        session.Prepared = null;
        session.Container = container;
        session.BotOwner = record.BotOwner;
        session.Submitted = false;
        session.SubmitCallReturned = false;
        session.SubmitException = false;
        session.CallbackReceived = false;
        session.CallbackSucceeded = false;
        session.CallbackReason = "none";
        session.CallbackAtUtc = DateTimeOffset.MinValue;
        session.SubmittedAtUtc = DateTimeOffset.MinValue;
        session.PreparationDeadlineUtc = now + VanguardCorpseLootApproachDoctrine.TransactionPreparationMaximumDuration;
        session.TransactionDeadlineUtc = now + VanguardCorpseLootApproachDoctrine.TransactionMaximumDuration;
        if (!VanguardLootTargetClaimStore.Refresh(session.ClaimId, VanguardLootTargetKind.WorldContainer, now))
        {
            reason = "target_claim_refresh_failed_for_micro_session";
            return false;
        }
        VanguardMainIntentScheduler.ReportPrimaryProgress(
            session.BotProfileId,
            now,
            "world_container_player_interest_micro_session_continued",
            "nextItem=" + Safe(next.ItemId) + ";manifest=" + observation.ManifestRevision,
            session.WindowId);
        reason = "second_player_interest_assignment_ready:item=" + Safe(next.ItemId)
            + ":manifest=" + observation.ManifestRevision
            + ":interest=" + observation.InterestRevision;
        return true;
    }

    private static string Finish(Session session, DateTimeOffset now, string outcome, string reason, bool finishScheduler, VanguardCorpseLootTransactionPreflightResult? preflight)
    {
        lock (Sync) ActiveByBot.Remove(session.BotProfileId);
        return Complete(session, now, outcome, reason, finishScheduler, preflight);
    }

    private static string Complete(Session session, DateTimeOffset now, string outcome, string reason, bool finishScheduler, VanguardCorpseLootTransactionPreflightResult? preflight)
    {
        if (session.Prepared?.ItemClaim != null) VanguardLootItemClaimStore.Release(session.Prepared.ItemClaim.ClaimId, reason, out _);
        VanguardLootItemClaimStore.ReleaseByBot(session.BotProfileId, "container_session_finished:" + reason);
        bool targetReleased = VanguardLootTargetClaimStore.Release(session.ClaimId, VanguardLootTargetKind.WorldContainer, reason, out _);
        bool terminalFailure = !string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase);
        VanguardWorldLootContainerApproachExecutor.RegisterTransactionSessionTerminal(session.OwnerProfileId, session.ContainerId, now, terminalFailure);
        if (finishScheduler) VanguardMainIntentScheduler.FinishPrimaryWindow(session.BotProfileId, now, outcome, reason, session.Summary, session.WindowId);
        VanguardClientDiagnosticsLog.Operational(VanguardWorldLootContainerApproachDoctrine.TransactionStatusTag, () =>
            $"VANGUARD_CONTAINER_TRANSACTION_SESSION_TERMINAL {session.Summary}; outcome={Safe(outcome)}; reason={Safe(reason)}; preflight={Safe(preflight?.Summary ?? "none")}; targetClaimReleased={Bool(targetReleased)}; itemClaimReleased=true; maximumPlayerInterestCommitsPerVisit={VanguardWorldLootContainerApproachDoctrine.PlayerInterestMaximumCommitsPerVisit}; nonPlayerInterestSingleCommit=true; nativeInventoryController=true; fikaPacketDirect=false");
        return "session=" + Safe(session.LeaseId) + ";outcome=" + Safe(outcome) + ";reason=" + Safe(reason);
    }

    private static bool TargetClaimMatches(Session session, DateTimeOffset now)
        => VanguardLootTargetClaimStore.TryGetByBot(session.BotProfileId, now, out VanguardLootTargetClaim claim)
            && claim.TargetKind == VanguardLootTargetKind.WorldContainer
            && string.Equals(claim.ClaimId, session.ClaimId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.TargetId, session.ContainerId, StringComparison.OrdinalIgnoreCase);

    private static bool TryResolveContainer(string containerId, DateTimeOffset now, out VanguardWorldLootContainerSnapshot snapshot, out string reason)
    {
        snapshot = VanguardWorldLootContainerSnapshotProvider.GetSnapshot(now).FirstOrDefault(value => string.Equals(value.ContainerId, containerId, StringComparison.OrdinalIgnoreCase))!;
        if (snapshot == null || snapshot.Container == null) { reason = "container_missing"; return false; }
        if (!snapshot.Container.isActiveAndEnabled) { reason = "container_inactive"; return false; }
        if (snapshot.RootItem == null) { reason = "root_item_missing"; return false; }
        reason = "live"; return true;
    }

    private static string Norm(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    private static string Bool(bool value) => value ? "true" : "false";

    private sealed class Session
    {
        public string ClaimId = "none", LeaseId = "none", WindowId = "none", OwnerProfileId = "none", OperatorId = "none", BotProfileId = "none", ContainerId = "none";
        public long ApproachManifestRevision, ManifestRevision, InterestRevision;
        public float HandoffDistanceMeters;
        public string NeedSignature = "none", ApproachSummary = "none";
        public DateTimeOffset StartedAtUtc, ApproachStartedAtUtc, AssignmentObservedAtUtc, PreparationDeadlineUtc, TransactionDeadlineUtc, SubmittedAtUtc, CallbackAtUtc;
        public VanguardCorpseLootSessionLimits Limits = null!; public VanguardCorpseLootSessionProgress Progress = null!;
        public VanguardCorpseLootPreparedTransaction? Prepared; public VanguardWorldLootContainerSnapshot? Container; public BotOwner? BotOwner;
        public int SubmissionGeneration;
        public bool Submitted, SubmitCallReturned, SubmitException, CallbackReceived, CallbackSucceeded, SchedulerYielded; public string CallbackReason = "none";
        public string Summary => $"lease={Safe(LeaseId)}; window={Safe(WindowId)}; claim={Safe(ClaimId)}; owner={Safe(OwnerProfileId)}; operator={Safe(OperatorId)}; bot={Safe(BotProfileId)}; container={Safe(ContainerId)}; approachManifestRevision={ApproachManifestRevision}; manifestRevision={ManifestRevision}; interestRevision={InterestRevision}; handoffDistance={HandoffDistanceMeters:0.00}; started={StartedAtUtc:O}; approachStarted={ApproachStartedAtUtc:O}; assignmentObserved={AssignmentObservedAtUtc:O}; committed={Progress?.CommittedTransactions ?? 0}; submitted={Bool(Submitted)}; submissionGeneration={SubmissionGeneration}; submittedAt={SubmittedAtUtc:O}; submitReturned={Bool(SubmitCallReturned)}; submitException={Bool(SubmitException)}; callbackReceived={Bool(CallbackReceived)}; callbackSucceeded={Bool(CallbackSucceeded)}; callbackReason={Safe(CallbackReason)}; approach={Safe(ApproachSummary)}";
    }
}
#endif
