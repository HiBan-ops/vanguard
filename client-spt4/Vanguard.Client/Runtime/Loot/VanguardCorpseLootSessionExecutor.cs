#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Raid.Persistence;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Movement.Brain;

// Responsibility: performs the bounded inventory interaction after an Operator has reached an authorized corpse-loot target.
// Flow: The session rechecks corpse ownership/safety and inventory need, builds a dry-run transfer plan, executes allowed EFT inventory transactions, verifies source/destination readback, then commits the terminal outcome and releases the corpse claim.
// Authority boundary: evaluators decide whether/what is worth looting and EFT inventory state is transaction authority; this executor may transfer only items present in its validated plan.
// Invariant: one loot window cannot double-commit an item, failed/readback-mismatched transfers remain explicit, and every session/claim has deterministic cleanup.
namespace Vanguard.Client.Runtime.Loot;

/// <summary>
/// The persistence path owns one bounded post-arrival utility-claimed transaction. The physical corpse lease is kept
/// through the single native transaction, while the item-level claim guarantees that the Operator only
/// takes an item currently assigned to it. After one confirmed mutation the lease is released so the
/// whole squad can re-evaluate the new corpse revision before any second pickup.
/// </summary>
internal static class VanguardCorpseLootSessionExecutor
{
    public const string StatusTag = VanguardCorpseLootApproachDoctrine.OperationalLootStatusTag;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, SessionLease> ActiveByBotProfileId = new(StringComparer.OrdinalIgnoreCase);

    public static bool HasInFlightNativeTransaction(out string summary)
    {
        lock (Sync)
        {
            SessionLease[] inFlight = ActiveByBotProfileId.Values
                .Where(lease => lease.TransactionSubmitted)
                .ToArray();
            if (inFlight.Length == 0)
            {
                summary = "none";
                return false;
            }

            summary = string.Join(",", inFlight.Select(lease =>
                $"bot={Safe(lease.BotProfileId)}:corpse={Safe(lease.CorpseId)}:callback={Bool(lease.CallbackReceived)}:returned={Bool(lease.SubmitCallReturned)}"));
            return true;
        }
    }

    public static void ResetForRaidLifecycle(string reason)
    {
        SessionLease[] active;
        lock (Sync)
        {
            active = ActiveByBotProfileId.Values.ToArray();
            ActiveByBotProfileId.Clear();
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (SessionLease lease in active)
        {
            VanguardCorpseLootTransactionOutcome outcome = CaptureOutcome(lease, corpse: null);
            CompleteSessionAndReleaseClaims(
                lease,
                now,
                lease.TransactionSubmitted ? "Failed" : "Interrupted",
                "raid_reset:" + reason,
                finishScheduler: !lease.SchedulerFinished,
                snapshotSignature: "raid_reset",
                preflight: lease.Prepared?.Preflight,
                transaction: outcome);
        }
    }

    public static bool TryBegin(VanguardCorpseLootSessionStart start, OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string summary)
    {
        summary = "none";
        if (start == null || snapshot == null || string.IsNullOrWhiteSpace(start.BotProfileId))
        {
            summary = "session_start_missing";
            return false;
        }

        if (!ClaimMatches(start, now, out string claimReason))
        {
            summary = "claim_invalid:" + claimReason;
            return false;
        }

        string gate = VanguardCorpseLootApproachExecutor.CheckActiveSessionSafetyGate(snapshot, now, activeWindow: true);
        if (!string.Equals(gate, "none", StringComparison.OrdinalIgnoreCase))
        {
            summary = "arrival_gate_blocked:" + gate;
            return false;
        }

        VanguardCorpseLootSessionLimits limits = VanguardCorpseLootSessionLimits.CaptureRuntime(start.OwnerProfileId);
        DateTimeOffset schedulerMax = start.SchedulerMaxUntilUtc;
        DateTimeOffset configuredSessionMax = now + TimeSpan.FromSeconds(limits.MaximumSessionSeconds);
        DateTimeOffset sessionMax = schedulerMax < configuredSessionMax ? schedulerMax : configuredSessionMax;
        DateTimeOffset preparationMax = now + VanguardCorpseLootApproachDoctrine.TransactionPreparationMaximumDuration;
        DateTimeOffset hardMax = sessionMax < preparationMax ? sessionMax : preparationMax;
        if (hardMax <= now)
        {
            summary = "scheduler_window_exhausted";
            return false;
        }

        string commandCleanup = VanguardReturnMovementCommandStore.ClearOwned(
            start.BotProfileId,
            start.LeaseId,
            start.ApproachStartedAtUtc,
            "corpse_loot_arrival_transaction_handoff");

        if (!VanguardMainIntentScheduler.MarkCorpseLootPreflightStarted(
                start.BotProfileId,
                start.LeaseId,
                now,
                start.ApproachSummary,
                start.WindowId))
        {
            summary = "scheduler_preflight_transition_rejected;command=" + Safe(commandCleanup);
            return false;
        }

        var lease = new SessionLease
        {
            ClaimId = start.ClaimId,
            LeaseId = start.LeaseId,
            WindowId = start.WindowId,
            OwnerProfileId = start.OwnerProfileId,
            OperatorId = start.OperatorId,
            BotProfileId = start.BotProfileId,
            CorpseId = start.CorpseId,
            ManifestRevision = start.ManifestRevision,
            InterestRevision = start.InterestRevision,
            NeedSignature = start.NeedSignature,
            StartedAtUtc = now,
            SettleUntilUtc = now + VanguardCorpseLootApproachDoctrine.PreflightSettleDuration,
            PreparationMaxUntilUtc = hardMax,
            SessionMaxUntilUtc = sessionMax,
            NextPrepareAtUtc = now + VanguardCorpseLootApproachDoctrine.PreflightSettleDuration,
            TransactionDeadlineUtc = now + VanguardCorpseLootApproachDoctrine.TransactionMaximumDuration,
            Limits = limits,
            Progress = new VanguardCorpseLootSessionProgress(),
            ApproachSummary = start.ApproachSummary,
            CommandCleanup = commandCleanup
        };

        lock (Sync)
        {
            if (ActiveByBotProfileId.ContainsKey(start.BotProfileId))
            {
                summary = "session_already_active";
                return false;
            }
            ActiveByBotProfileId.Add(start.BotProfileId, lease);
        }

        summary = lease.Summary;
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"VANGUARD_CORPSE_LOOT_SESSION_ACQUIRED {lease.Summary}; commandCleanup={Safe(commandCleanup)}; claimRetained=true; schedulerRetained=true; sequentialTransactions=false; singleUtilityClaimPerVisit=true; operatorCorpseCommitDeferredToRaidClose=true; persistenceMode=raid_close_batch_when_operator");
        return true;
    }

    public static void Tick(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        SessionLease[] active;
        lock (Sync)
        {
            active = ActiveByBotProfileId.Values.ToArray();
        }
        if (active.Length == 0)
        {
            return;
        }

        var snapshotByBot = (snapshots ?? Array.Empty<OperatorDecisionSnapshot>())
            .Where(snapshot => snapshot != null && !string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            .GroupBy(snapshot => snapshot.BotProfileId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (SessionLease lease in active)
        {
            snapshotByBot.TryGetValue(lease.BotProfileId, out OperatorDecisionSnapshot? snapshot);
            if (lease.TransactionSubmitted)
            {
                TickSubmittedTransaction(lease, snapshot, now);
                continue;
            }

            if (snapshot == null)
            {
                Finish(lease, now, "Interrupted", "snapshot_missing", finishScheduler: true, snapshotSignature: "snapshot_missing");
                continue;
            }

            string gate = VanguardCorpseLootApproachExecutor.CheckActiveSessionSafetyGate(snapshot, now, activeWindow: true);
            if (!string.Equals(gate, "none", StringComparison.OrdinalIgnoreCase))
            {
                VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
                    $"VANGUARD_CORPSE_LOOT_ACTIVE_INTERRUPTION {lease.Summary}; phase=pre_submit; reason={Safe(gate)}; operationPrepared=false; operationSubmitted=false; mutationObserved=false; authorityYielded=true");
                Finish(lease, now, "Interrupted", "active_pre_submit_interrupt:" + gate, true, snapshot.DecisionSignature);
                continue;
            }

            if (!ClaimMatches(lease, now, out string claimReason))
            {
                Finish(lease, now, "Interrupted", "claim_lost:" + claimReason, true, snapshot.DecisionSignature);
                continue;
            }

            if (!VanguardCorpseRegistry.TryGet(lease.CorpseId, now, out VanguardCorpseRegistryEntry entry) || entry.Corpse == null)
            {
                Finish(lease, now, "Failed", "corpse_missing_before_submit", true, snapshot.DecisionSignature);
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(lease.BotProfileId, out VanguardRaidOperatorRuntimeRecord record)
                || record.BotOwner == null
                || record.BotOwner.IsDead)
            {
                Finish(lease, now, "Interrupted", "bot_owner_missing_or_dead", true, snapshot.DecisionSignature);
                continue;
            }

            if (now >= lease.SessionMaxUntilUtc)
            {
                Finish(lease, now, "Completed", "loot_session_duration_limit_reached", true, snapshot.DecisionSignature);
                continue;
            }
            if (lease.Progress.CommittedTransactions >= lease.Limits.MaximumTransactions)
            {
                Finish(lease, now, "Completed", "loot_session_transaction_limit_reached", true, snapshot.DecisionSignature);
                continue;
            }
            if (now >= lease.PreparationMaxUntilUtc)
            {
                Finish(lease, now, "Failed", "transaction_preparation_cycle_exhausted", true, snapshot.DecisionSignature);
                continue;
            }

            if (now < lease.NextPrepareAtUtc || now < lease.SettleUntilUtc)
            {
                continue;
            }

            VanguardOwnerLootInterestSnapshot currentInterest = VanguardOwnerLootInterestSyncService.Resolve(lease.OwnerProfileId);
            VanguardOperatorLootNeedSnapshot currentLootNeed = VanguardOperatorLootNeedReader.Capture(record.BotOwner);
            string currentNeedSignature = currentLootNeed.DecisionSignature + "||medical=" + (snapshot.Medical?.Need?.Summary ?? "none");
            if ((currentInterest.Known && currentInterest.Revision != lease.InterestRevision)
                || !string.Equals(NormalizeSignature(currentNeedSignature), NormalizeSignature(lease.NeedSignature), StringComparison.Ordinal))
            {
                Finish(
                    lease,
                    now,
                    "Completed",
                    "utility_context_changed_before_prepare",
                    true,
                    snapshot.DecisionSignature);
                continue;
            }

            bool preparedReady;
            VanguardCorpseLootPreparedTransaction prepared;
            VanguardCorpseLootTransactionPreflightResult preflight;
            long preflightStarted = VanguardRuntimePerformanceGuard.Begin();
            try
            {
                preparedReady = VanguardCorpseLootTransactionPreflight.TryPrepare(
                    entry.Corpse,
                    record.BotOwner,
                    lease.Limits,
                    lease.Progress,
                    lease.OwnerProfileId,
                    lease.BotProfileId,
                    lease.CorpseId,
                    lease.ManifestRevision,
                    lease.InterestRevision,
                    lease.NeedSignature,
                    out prepared,
                    out preflight);
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(StatusTag, () =>
                    $"VANGUARD_CORPSE_LOOT_PREFLIGHT_EXCEPTION_ISOLATED {lease.Summary}; type={Safe(exception.GetType().Name)}; message={Safe(exception.Message)}; operationPrepared=false; operationSubmitted=false; mutationAttempted=false; failClosed=true; sessionReleased=true");
                Finish(lease, now, "Failed", "transaction_preflight_exception:" + exception.GetType().Name, true, snapshot.DecisionSignature);
                continue;
            }
            finally
            {
                VanguardRuntimePerformanceGuard.End("CorpseLootTransactionPreflight", preflightStarted);
            }
            VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
                $"{(preparedReady ? "VANGUARD_CORPSE_LOOT_TRANSACTION_PREPARED" : "VANGUARD_CORPSE_LOOT_TRANSACTION_PREPARE_REJECTED")} {lease.Summary}; {preflight.Summary}; claimStillHeld=true; sequentialSession=false; singleUtilityClaimPerVisit=true; " + lease.Progress.Summary);

            if (!preparedReady)
            {
                // Assignment TTL may expire during a long approach. Do not convert that transient absence into
                // context exhaustion here; release the physical lease and let the next full squad read cycle decide.
                bool assignmentMissing = string.Equals(preflight.Reason, "no_current_item_assignment_for_operator", StringComparison.OrdinalIgnoreCase);
                Finish(lease, now, assignmentMissing ? "Completed" : "Failed", "loot_session_assignment_unavailable:" + preflight.Reason, true, snapshot.DecisionSignature, preflight);
                continue;
            }

            if (entry.VictimWasOperator && !VanguardRaidOperatorPersistenceService.IsArmedForOperatorCorpseTransactions)
            {
                VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
                    $"VANGUARD_OPERATOR_CORPSE_TRANSACTION_BLOCKED {lease.Summary}; {preflight.Summary}; reason=persistence_reconciliation_not_armed; preflightAllowed=true; operationSubmitted=false; mutationAttempted=false; failClosed=true; transactionSafetyPreserved=true");
                Finish(lease, now, "Completed", "operator_corpse_transaction_blocked_persistence_not_armed", true, snapshot.DecisionSignature, preflight);
                continue;
            }

            if (entry.VictimWasOperator)
            {
                VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
                    $"VANGUARD_OPERATOR_CORPSE_TRANSACTION_ALLOWED {lease.Summary}; {preflight.Summary}; persistenceReconciliationArmed=true; sharedNativeTransactionEngine=true; transactionSafetyPreserved=true; directFikaPackets=false");
            }

            gate = VanguardCorpseLootApproachExecutor.CheckActiveSessionSafetyGate(snapshot, now, activeWindow: true);
            if (!string.Equals(gate, "none", StringComparison.OrdinalIgnoreCase))
            {
                VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
                    $"VANGUARD_CORPSE_LOOT_ACTIVE_INTERRUPTION {lease.Summary}; phase=prepared_before_submit; reason={Safe(gate)}; operationPrepared=true; operationSubmitted=false; mutationObserved=false; authorityYielded=true");
                Finish(lease, now, "Interrupted", "active_pre_submit_interrupt:" + gate, true, snapshot.DecisionSignature, preflight);
                continue;
            }

            if (!ClaimMatches(lease, now, out claimReason))
            {
                Finish(lease, now, "Interrupted", "claim_lost_before_submit:" + claimReason, true, snapshot.DecisionSignature, preflight);
                continue;
            }

            if (!VanguardCorpseLootTransactionPreflight.Revalidate(
                    prepared,
                    entry.Corpse,
                    record.BotOwner,
                    lease.Limits,
                    lease.Progress,
                    lease.OwnerProfileId,
                    lease.BotProfileId,
                    lease.CorpseId,
                    lease.ManifestRevision,
                    lease.InterestRevision,
                    lease.NeedSignature,
                    out string revalidationReason))
            {
                Finish(lease, now, "Failed", "final_revalidation_failed:" + revalidationReason, true, snapshot.DecisionSignature, preflight);
                continue;
            }

            if (!VanguardCorpseLootClaimStore.Refresh(lease.ClaimId, now))
            {
                Finish(lease, now, "Interrupted", "claim_refresh_failed_before_submit", true, snapshot.DecisionSignature, preflight);
                continue;
            }
            if (!ClaimMatches(lease, now, out claimReason))
            {
                Finish(lease, now, "Interrupted", "claim_mismatch_after_refresh:" + claimReason, true, snapshot.DecisionSignature, preflight);
                continue;
            }

            if (!VanguardMainIntentScheduler.MarkCorpseLootTransactionStarted(
                    lease.BotProfileId,
                    lease.LeaseId,
                    now,
                    preflight.Summary,
                    lease.WindowId))
            {
                Finish(lease, now, "Failed", "scheduler_transaction_transition_rejected", true, snapshot.DecisionSignature, preflight);
                continue;
            }

            SubmitTransaction(lease, prepared, entry.Corpse, record.BotOwner, entry.VictimWasOperator, now, snapshot.DecisionSignature);
        }
    }

    public static bool TryTerminateSchedulerExpiredWindow(string botProfileId, string windowId, DateTimeOffset now, string timeoutReason, out string summary)
    {
        SessionLease lease;
        lock (Sync)
        {
            if (!ActiveByBotProfileId.TryGetValue(botProfileId, out SessionLease found)
                || !string.Equals(found.WindowId, windowId, StringComparison.OrdinalIgnoreCase))
            {
                summary = "active_corpse_session_not_found";
                return false;
            }

            lease = found;
            if (lease.TransactionSubmitted)
            {
                lease.SchedulerFinished = true;
                lease.AuthorityYieldReason = "scheduler_expired_during_inflight:" + timeoutReason;
                summary = "transaction_inflight_monitor_retained;" + lease.Summary;
                return true;
            }
        }

        summary = Finish(lease, now, "Timeout", "scheduler_expired:" + timeoutReason, false, "scheduler_terminal");
        return true;
    }

    private static void SubmitTransaction(
        SessionLease lease,
        VanguardCorpseLootPreparedTransaction prepared,
        EFT.Interactive.Corpse corpse,
        BotOwner botOwner,
        bool victimWasOperator,
        DateTimeOffset now,
        string snapshotSignature)
    {
        lock (Sync)
        {
            if (!ActiveByBotProfileId.TryGetValue(lease.BotProfileId, out SessionLease current)
                || !ReferenceEquals(current, lease)
                || lease.TransactionSubmitted)
            {
                return;
            }

            lease.Prepared = prepared;
            lease.Corpse = corpse;
            lease.BotOwner = botOwner;
            lease.TransactionSubmitted = true;
            lease.SubmittedAtUtc = now;
            lease.TransactionDeadlineUtc = now + VanguardCorpseLootApproachDoctrine.TransactionMaximumDuration;
            lease.SnapshotSignatureAtSubmit = snapshotSignature;
        }

        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"VANGUARD_CORPSE_LOOT_TRANSACTION_SUBMIT_ATTEMPTED {lease.Summary}; {prepared.Preflight.Summary}; claimStillHeld=true; itemClaim={Safe(prepared.ItemClaim.ClaimId)}; itemClaimHeld=true; claimRefreshedBeforeSubmit=true; transactionOrdinal={lease.Progress.CommittedTransactions + 1}; fikaViaInventoryController=true; callbackPending=true; operatorCorpsePersistenceArmed={Bool(victimWasOperator && VanguardRaidOperatorPersistenceService.IsArmedForOperatorCorpseTransactions)}; persistenceMode={(victimWasOperator ? "raid_close_batch" : "not_applicable")}");

        try
        {
            prepared.Inventory.RunNetworkTransaction(
                prepared.Operation,
                new Callback(result => OnTransactionCallback(lease.BotProfileId, lease.LeaseId, result)));
            lock (Sync)
            {
                lease.SubmitCallReturned = true;
            }
            VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
                $"VANGUARD_CORPSE_LOOT_TRANSACTION_SUBMIT_CALL_RETURNED {lease.Summary}; submitCallReturned=true; callbackMayBePending=true; networkSubmissionUncertain=false; secondSubmitForbidden=true");
        }
        catch (Exception exception)
        {
            lock (Sync)
            {
                lease.SubmitExceptionObserved = true;
                lease.CallbackReason = "submit_exception:" + exception.GetType().Name;
            }
            VanguardClientDiagnosticsLog.Warning(StatusTag, () =>
                $"VANGUARD_CORPSE_LOOT_TRANSACTION_SUBMIT_EXCEPTION {lease.Summary}; type={Safe(exception.GetType().Name)}; message={Safe(exception.Message)}; claimStillHeld=true; networkSubmissionUncertain=true; reconciliationRequired=true; secondSubmitForbidden=true");
        }
    }

    private static void OnTransactionCallback(string botProfileId, string leaseId, IResult? result)
    {
        SessionLease? lease = null;
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(botProfileId, out SessionLease current)
                && string.Equals(current.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase)
                && current.TransactionSubmitted)
            {
                lease = current;
                current.CallbackReceived = true;
                current.CallbackSucceeded = result?.Succeed == true;
                current.CallbackReason = result == null
                    ? "callback_result_null"
                    : current.CallbackSucceeded ? "callback_success" : "callback_failed:" + Safe(result.ToString());
                current.CallbackAtUtc = DateTimeOffset.UtcNow;
            }
        }

        if (lease != null)
        {
            VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
                $"VANGUARD_CORPSE_LOOT_TRANSACTION_CALLBACK {lease.Summary}; callbackSucceeded={Bool(lease.CallbackSucceeded)}; callbackReason={Safe(lease.CallbackReason)}; claimStillHeld=true; mutationRequiresReconciliation=true");
        }
    }

    private static void TickSubmittedTransaction(SessionLease lease, OperatorDecisionSnapshot? snapshot, DateTimeOffset now)
    {
        if (!lease.SchedulerFinished)
        {
            string gate = snapshot == null
                ? "snapshot_missing_during_transaction"
                : VanguardCorpseLootApproachExecutor.CheckActiveSessionSafetyGate(snapshot, now, activeWindow: true);
            if (!string.Equals(gate, "none", StringComparison.OrdinalIgnoreCase))
            {
                lease.SchedulerFinished = VanguardMainIntentScheduler.FinishPrimaryWindow(
                    lease.BotProfileId,
                    now,
                    "Interrupted",
                    "transaction_inflight_authority_yield:" + gate,
                    lease.Summary,
                    lease.WindowId);
                lease.AuthorityYieldReason = gate;
                VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
                    $"VANGUARD_CORPSE_LOOT_ACTIVE_INTERRUPTION {lease.Summary}; phase=transaction_inflight; reason={Safe(gate)}; operationPrepared=true; submitAttempted=true; transactionCannotBeFictitiouslyCancelled=true; authorityYielded={Bool(lease.SchedulerFinished)}");
            }
        }

        if (!ClaimMatches(lease, now, out string claimReason) && !lease.ClaimLossLogged)
        {
            lease.ClaimLossLogged = true;
            VanguardClientDiagnosticsLog.Warning(StatusTag, () =>
                $"VANGUARD_CORPSE_LOOT_INFLIGHT_CLAIM_ANOMALY {lease.Summary}; reason={Safe(claimReason)}; transactionCannotBeCancelled=true; resultReconciliationFailClosed=true; ownerSquadTerminal=false");
        }

        VanguardCorpseLootTransactionOutcome outcome = CaptureOutcome(lease, lease.Corpse);
        if (outcome.MutationConfirmed)
        {
            CompleteConfirmedItemAndContinueOrFinish(lease, snapshot, now, outcome);
            return;
        }

        DateTimeOffset callbackAtUtc;
        lock (Sync)
        {
            callbackAtUtc = lease.CallbackAtUtc;
        }
        DateTimeOffset reconcileAfter = callbackAtUtc == DateTimeOffset.MinValue
            ? lease.TransactionDeadlineUtc
            : callbackAtUtc + VanguardCorpseLootApproachDoctrine.TransactionReconciliationGrace;
        if (outcome.CallbackReceived
            && !outcome.CallbackSucceeded
            && outcome.OperatorInventoryObserved
            && outcome.CorpseInventoryObserved
            && !outcome.ItemInOperatorInventory
            && outcome.ItemStillInCorpseInventory
            && now >= reconcileAfter)
        {
            Finish(
                lease,
                now,
                "Failed",
                "transaction_callback_failed_no_mutation",
                finishScheduler: !lease.SchedulerFinished,
                snapshotSignature: snapshot?.DecisionSignature ?? lease.SnapshotSignatureAtSubmit,
                preflight: lease.Prepared?.Preflight,
                transaction: outcome);
            return;
        }

        if (now < lease.TransactionDeadlineUtc && (!outcome.CallbackReceived || now < reconcileAfter))
        {
            return;
        }

        VanguardCorpseLootTransactionOutcome uncertain = new()
        {
            State = "uncertain_fail_closed",
            Reason = outcome.CallbackReceived
                ? "callback_without_confirmed_inventory_mutation"
                : "transaction_callback_timeout_without_confirmed_mutation",
            SubmitAttempted = outcome.SubmitAttempted,
            SubmitCallReturned = outcome.SubmitCallReturned,
            NetworkSubmissionUncertain = outcome.NetworkSubmissionUncertain,
            CallbackReceived = outcome.CallbackReceived,
            CallbackSucceeded = outcome.CallbackSucceeded,
            OperatorInventoryObserved = outcome.OperatorInventoryObserved,
            CorpseInventoryObserved = outcome.CorpseInventoryObserved,
            ItemInOperatorInventory = outcome.ItemInOperatorInventory,
            ItemStillInCorpseInventory = outcome.ItemStillInCorpseInventory,
            MutationConfirmed = false,
            ResultUncertain = true,
            NetworkTransactionSubmitted = outcome.NetworkTransactionSubmitted
        };
        Finish(
            lease,
            now,
            "Failed",
            "transaction_result_uncertain_fail_closed",
            finishScheduler: !lease.SchedulerFinished,
            snapshotSignature: snapshot?.DecisionSignature ?? lease.SnapshotSignatureAtSubmit,
            preflight: lease.Prepared?.Preflight,
            transaction: uncertain);
    }

    private static VanguardCorpseLootTransactionOutcome CaptureOutcome(SessionLease lease, EFT.Interactive.Corpse? corpse)
    {
        VanguardCorpseLootPreparedTransaction? prepared;
        bool transactionSubmitted;
        bool submitCallReturned;
        bool submitExceptionObserved;
        bool callbackReceived;
        bool callbackSucceeded;
        string callbackReason;
        lock (Sync)
        {
            prepared = lease.Prepared;
            transactionSubmitted = lease.TransactionSubmitted;
            submitCallReturned = lease.SubmitCallReturned;
            submitExceptionObserved = lease.SubmitExceptionObserved;
            callbackReceived = lease.CallbackReceived;
            callbackSucceeded = lease.CallbackSucceeded;
            callbackReason = lease.CallbackReason;
        }
        if (!transactionSubmitted || prepared == null)
        {
            return new VanguardCorpseLootTransactionOutcome
            {
                State = "not_submitted",
                Reason = "no_network_transaction",
                SubmitAttempted = false,
                SubmitCallReturned = false,
                NetworkSubmissionUncertain = false,
                NetworkTransactionSubmitted = false
            };
        }

        bool operatorInventoryObserved = false;
        bool itemInOperator = false;
        try
        {
            bool found = prepared.Inventory.TryFindItem(prepared.Item.Id, out Item foundItem);
            operatorInventoryObserved = true;
            itemInOperator = found && ReferenceEquals(foundItem, prepared.Item);
        }
        catch
        {
        }

        bool corpseInventoryObserved = false;
        bool itemStillInCorpse = false;
        if (corpse != null)
        {
            try
            {
                bool found = VanguardCorpseLootLiveItemResolver.TryResolve(
                    corpse,
                    prepared.Item.Id,
                    out Item corpseItem,
                    out _,
                    out _);
                corpseInventoryObserved = true;
                itemStillInCorpse = found && ReferenceEquals(corpseItem, prepared.Item);
            }
            catch
            {
            }
        }

        bool confirmed = operatorInventoryObserved
            && corpseInventoryObserved
            && itemInOperator
            && !itemStillInCorpse;
        bool networkSubmissionProven = submitCallReturned || callbackReceived || confirmed;
        bool networkSubmissionUncertain = transactionSubmitted
            && !networkSubmissionProven
            && submitExceptionObserved;
        return new VanguardCorpseLootTransactionOutcome
        {
            State = confirmed ? "mutation_confirmed" : callbackReceived ? "callback_observed_pending_reconciliation" : "inflight",
            Reason = confirmed ? "item_transferred_to_operator_inventory" : Safe(callbackReason),
            SubmitAttempted = transactionSubmitted,
            SubmitCallReturned = submitCallReturned,
            NetworkSubmissionUncertain = networkSubmissionUncertain,
            CallbackReceived = callbackReceived,
            CallbackSucceeded = callbackSucceeded,
            OperatorInventoryObserved = operatorInventoryObserved,
            CorpseInventoryObserved = corpseInventoryObserved,
            ItemInOperatorInventory = itemInOperator,
            ItemStillInCorpseInventory = itemStillInCorpse,
            MutationConfirmed = confirmed,
            ResultUncertain = false,
            NetworkTransactionSubmitted = networkSubmissionProven
        };
    }

    private static void CompleteConfirmedItemAndContinueOrFinish(
        SessionLease lease,
        OperatorDecisionSnapshot? snapshot,
        DateTimeOffset now,
        VanguardCorpseLootTransactionOutcome outcome)
    {
        VanguardCorpseLootPreparedTransaction? prepared = lease.Prepared;
        if (prepared == null)
        {
            Finish(lease, now, "Failed", "confirmed_mutation_without_prepared_item", !lease.SchedulerFinished, snapshot?.DecisionSignature ?? lease.SnapshotSignatureAtSubmit, transaction: outcome);
            return;
        }

        VanguardCorpseLootPostCommitReadBackResult readBack = VanguardCorpseLootPostCommitReadBack.RefreshAndVerify(prepared, lease.BotOwner);
        lease.Progress.Record(prepared.Preflight);
        lease.LastReadBack = readBack;
        RecordCurrentTransactionTelemetry(lease, outcome);
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"VANGUARD_CORPSE_LOOT_ITEM_COMMITTED {lease.Summary}; {prepared.Preflight.Summary}; {outcome.Summary}; {readBack.Summary}; progress={Safe(lease.Progress.Summary)}; claimStillHeld=true; rescanPending=false; squadReassignmentPending={Bool(readBack.Success)}");

        if (!readBack.Success)
        {
            Finish(
                lease,
                now,
                "Failed",
                "post_commit_runtime_readback_failed:" + readBack.Reason,
                finishScheduler: !lease.SchedulerFinished,
                snapshotSignature: snapshot?.DecisionSignature ?? lease.SnapshotSignatureAtSubmit,
                preflight: prepared.Preflight,
                transaction: outcome);
            return;
        }

        if (string.Equals(prepared.Preflight.Category, "long_weapon", StringComparison.OrdinalIgnoreCase))
        {
            VanguardUnifiedOpportunisticLootReadModelService.InvalidateWeaponContext(lease.BotProfileId, "corpse_long_weapon_commit");
        }

        VanguardCorpseLootOutcomeMemory.ClearContextExhaustion(lease.BotProfileId, lease.CorpseId, "confirmed_item_mutation");
        _ = VanguardLootItemClaimStore.Release(prepared.ItemClaim.ClaimId, "confirmed_item_mutation", out _);

        Finish(
            lease,
            now,
            "Completed",
            lease.SchedulerFinished ? "utility_claim_item_committed_after_authority_yield" : "utility_claim_item_committed_reassign_squad",
            finishScheduler: !lease.SchedulerFinished,
            snapshotSignature: snapshot?.DecisionSignature ?? lease.SnapshotSignatureAtSubmit,
            preflight: prepared.Preflight,
            transaction: outcome);
    }

    private static void RecordCurrentTransactionTelemetry(SessionLease lease, VanguardCorpseLootTransactionOutcome? transaction)
    {
        if (lease.TransactionTerminalRecorded || transaction == null) return;
        VanguardCorpseLootOperationalTelemetry.RecordTransactionTerminal(transaction);
        lease.TransactionTerminalRecorded = true;
    }

    private static string Finish(
        SessionLease lease,
        DateTimeOffset now,
        string outcome,
        string reason,
        bool finishScheduler,
        string snapshotSignature,
        VanguardCorpseLootTransactionPreflightResult? preflight = null,
        VanguardCorpseLootTransactionOutcome? transaction = null)
    {
        lock (Sync)
        {
            ActiveByBotProfileId.Remove(lease.BotProfileId);
        }

        return CompleteSessionAndReleaseClaims(lease, now, outcome, reason, finishScheduler, snapshotSignature, preflight, transaction);
    }

    private static string CompleteSessionAndReleaseClaims(
        SessionLease lease,
        DateTimeOffset now,
        string outcome,
        string reason,
        bool finishScheduler,
        string snapshotSignature,
        VanguardCorpseLootTransactionPreflightResult? preflight = null,
        VanguardCorpseLootTransactionOutcome? transaction = null)
    {
        string outcomeMemoryScope = "no_outcome_memory_change";
        bool failureLike = string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outcome, "Interrupted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outcome, "Timeout", StringComparison.OrdinalIgnoreCase);
        if (failureLike)
        {
            _ = VanguardCorpseLootOutcomeMemory.Record(
                lease.OwnerProfileId, lease.OperatorId, lease.BotProfileId, lease.CorpseId, now, outcome, reason, false,
                VanguardCorpseLootApproachDoctrine.FailureCooldownSeconds, out outcomeMemoryScope);
        }

        if (lease.Prepared?.ItemClaim != null)
        {
            _ = VanguardLootItemClaimStore.Release(lease.Prepared.ItemClaim.ClaimId, reason, out _);
        }
        VanguardLootItemClaimStore.ReleaseByBot(lease.BotProfileId, "session_finished:" + reason);
        bool claimReleased = VanguardCorpseLootClaimStore.Release(lease.ClaimId, reason, out _);
        if (finishScheduler)
        {
            VanguardMainIntentScheduler.FinishPrimaryWindow(lease.BotProfileId, now, outcome, reason, lease.Summary, lease.WindowId);
        }
        VanguardCorpseLootOperationalTelemetry.RecordApproachTerminal(outcome, "transaction_session:" + reason, lease.BotProfileId, lease.CorpseId);
        RecordCurrentTransactionTelemetry(lease, transaction);

        string preflightSummary = preflight?.Summary ?? "preflight=none";
        string transactionSummary = transaction?.Summary ?? "transactionState=not_submitted; networkTransactionSubmitted=false; mutationConfirmed=false";
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"VANGUARD_CORPSE_LOOT_SESSION_COMPLETED {lease.Summary}; outcome={Safe(outcome)}; reason={Safe(reason)}; snapshot={Safe(snapshotSignature)}; {preflightSummary}; {transactionSummary}; outcomeMemoryScope={Safe(outcomeMemoryScope)}; ownerSquadTerminalRemoved=true; contextRevisionBound=true; itemClaimReleased=true; corpseClaimReleased={Bool(claimReleased)}; authorityRestored=true; sequentialSession=false; singleUtilityClaimPerVisit=true; sessionProgress={Safe(lease.Progress.Summary)}; limits={Safe(lease.Limits.Summary)}; readBack={Safe(lease.LastReadBack?.Summary ?? "none")}; operatorCorpseCommitDeferredToRaidClose=true; persistenceMode=raid_close_batch_when_operator");
        return "session=" + Safe(lease.LeaseId) + ";outcome=" + Safe(outcome) + ";reason=" + Safe(reason);
    }

    private static bool ClaimMatches(VanguardCorpseLootSessionStart start, DateTimeOffset now, out string reason)
    {
        if (!VanguardCorpseLootClaimStore.TryGetByBot(start.BotProfileId, now, out VanguardCorpseLootClaim claim))
        {
            reason = "claim_not_found";
            return false;
        }
        bool matches = string.Equals(claim.ClaimId, start.ClaimId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.OwnerProfileId, start.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.CorpseId, start.CorpseId, StringComparison.OrdinalIgnoreCase);
        reason = matches ? "claim_matches" : "claim_identity_mismatch";
        return matches;
    }

    private static bool ClaimMatches(SessionLease lease, DateTimeOffset now, out string reason)
    {
        if (!VanguardCorpseLootClaimStore.TryGetByBot(lease.BotProfileId, now, out VanguardCorpseLootClaim claim))
        {
            reason = "claim_not_found";
            return false;
        }
        bool matches = string.Equals(claim.ClaimId, lease.ClaimId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.OwnerProfileId, lease.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.CorpseId, lease.CorpseId, StringComparison.OrdinalIgnoreCase);
        reason = matches ? "claim_matches" : "claim_identity_mismatch";
        return matches;
    }

    private static string Bool(bool value) => value ? "true" : "false";
    private static string NormalizeSignature(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private sealed class SessionLease
    {
        public string ClaimId = "none";
        public string LeaseId = "none";
        public string WindowId = "none";
        public string OwnerProfileId = "none";
        public string OperatorId = "none";
        public string BotProfileId = "none";
        public string CorpseId = "none";
        public long ManifestRevision;
        public long InterestRevision;
        public string NeedSignature = "none";
        public DateTimeOffset StartedAtUtc;
        public DateTimeOffset SettleUntilUtc;
        public DateTimeOffset PreparationMaxUntilUtc;
        public DateTimeOffset SessionMaxUntilUtc;
        public DateTimeOffset NextPrepareAtUtc;
        public DateTimeOffset TransactionDeadlineUtc;
        public DateTimeOffset SubmittedAtUtc;
        public DateTimeOffset CallbackAtUtc;
        public string ApproachSummary = "none";
        public string CommandCleanup = "none";
        public string SnapshotSignatureAtSubmit = "none";
        public string CallbackReason = "none";
        public string AuthorityYieldReason = "none";
        public bool TransactionSubmitted;
        public bool SubmitCallReturned;
        public bool SubmitExceptionObserved;
        public bool CallbackReceived;
        public bool CallbackSucceeded;
        public bool SchedulerFinished;
        public bool TransactionTerminalRecorded;
        public bool ClaimLossLogged;
        public VanguardCorpseLootSessionLimits Limits = new();
        public VanguardCorpseLootSessionProgress Progress = new();
        public VanguardCorpseLootPostCommitReadBackResult? LastReadBack;
        public VanguardCorpseLootPreparedTransaction? Prepared;
        public EFT.Interactive.Corpse? Corpse;
        public BotOwner? BotOwner;

        public string Summary => $"claim={Safe(ClaimId)}; lease={Safe(LeaseId)}; window={Safe(WindowId)}; owner={Safe(OwnerProfileId)}; operator={Safe(OperatorId)}; botProfile={Safe(BotProfileId)}; corpse={Safe(CorpseId)}; manifestRevision={ManifestRevision}; interestRevision={InterestRevision}; need={Safe(NeedSignature)}; started={StartedAtUtc:O}; settle={SettleUntilUtc:O}; prepareMax={PreparationMaxUntilUtc:O}; sessionMax={SessionMaxUntilUtc:O}; nextPrepare={NextPrepareAtUtc:O}; transactionDeadline={TransactionDeadlineUtc:O}; progress={Safe(Progress.Summary)}; limits={Safe(Limits.Summary)}; submitAttempted={Bool(TransactionSubmitted)}; submitCallReturned={Bool(SubmitCallReturned)}; callback={Bool(CallbackReceived)}; schedulerFinished={Bool(SchedulerFinished)}; authorityYield={Safe(AuthorityYieldReason)}; approach={Safe(ApproachSummary)}; commandCleanup={Safe(CommandCleanup)}";
    }
}
#endif
