#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.PostLoot;

// Responsibility: Prevents Vanguard from starting new medical work while the Operator hands/controller are still finishing or recovering from a previous medical action.
// Flow: After a Vanguard medical lease ends, the watchdog tracks native hand/controller state and item continuity, performs only narrowly proven recovery, then waits for stable non-medical/weapon-ready truth before clearing the gate.
// Authority boundary: EFT owns native hands and medical controllers; Vanguard normally recovers only state proven to originate from its own completed lease. A foreign native MedsController may receive bounded recovery only when native use is idle and stable terminal or exhausted-controller evidence is positive; a finished operation with unready selector is restored through the native weapon callback path after a stable grace window.
// Invariant: Active/unproven foreign medical work is never cancelled or claimed. Only positively classified terminal-stale controllers may be recovered; native weapon restoration never overrides selector fields or writes health/item resources.
namespace Vanguard.Client.Runtime.Medical.Execution;

/// <summary>
/// Owns the post-terminal medical-hands boundary.
///
/// The runtime enforces provenance before post-terminal medical recovery can mutate native EFT state.
///
/// A state created from a real Vanguard lease terminal owns the normal recovery authority. Native EFT/SAIN
/// medical activity discovered without that provenance remains foreign and is observe-only while any medicine
/// controller is active or terminal state is unproven. One foreign exception is a bounded native fast-forward
/// after strict proof that a typed MedsController is terminal-stale with the same stable item/resource and no
/// Vanguard lease; a distinct bounded native weapon restore handles a stable Finished operation when the
/// native selector is unready. A real Vanguard post-terminal physical controller may be recovered only while item continuity
/// is proven and no positive terminal truth has been observed in between. A parent-only BotMedecine.Using latch
/// remains a separate, narrowly reconciled native state. Neither recovery request proves success; positive native
/// terminal truth remains required.
/// </summary>
internal static class VanguardMedicalHandsWatchdogService
{
    public const string StatusTag = "VANGUARD_MEDICAL_OWNERSHIP_BOUNDARY_STATUS";
    public const string PostTerminalStatusTag = "VANGUARD_MEDICAL_HANDS_POST_TERMINAL_STATUS";
    public const string MedicalLeaseStatusTag = "VANGUARD_TERMINAL_MEDICAL_CONTROLLER_RECOVERY_STATUS";
    public const string MedicalHandsWatchdogStatusTag = "VANGUARD_CLASSIFIED_MEDICAL_CONTROLLER_RECOVERY_STATUS";
    public const string ResourceOutsideLeaseTag = "VANGUARD_MEDICAL_RESOURCE_CONSUMED_OUTSIDE_LEASE";
    public const string ForeignActivityTag = "VANGUARD_MEDICAL_FOREIGN_ACTIVITY_PRESERVED";

    private static readonly TimeSpan NaturalSettleGrace = TimeSpan.FromSeconds(2.50d);
    private static readonly TimeSpan StableWindow = TimeSpan.FromSeconds(0.50d);
    private static readonly TimeSpan PhysicalRecoveryRetryDelay = TimeSpan.FromSeconds(1.25d);
    private static readonly TimeSpan RecoveryExhaustedLogDelay = TimeSpan.FromSeconds(3.00d);
    private static readonly TimeSpan ForeignStalePhysicalSettleGrace = TimeSpan.FromSeconds(3.00d);
    private static readonly TimeSpan ForeignStaleRecoveryRetryDelay = TimeSpan.FromSeconds(1.50d);
    private const int RequiredSnapshots = 3;
    private const int MaximumPhysicalRecoveryRequests = 2;
    private const int MaximumLatchReconciliations = 1;
    private const int MaximumForeignStaleRecoveryRequests = 2;
    private static readonly TimeSpan FinishedHandsSettleGrace = TimeSpan.FromSeconds(3d);
    private static readonly TimeSpan FinishedHandsRestoreRetryDelay = TimeSpan.FromSeconds(5d);
    private const int MaximumFinishedHandsRestoreRequests = 2;
    private const float ResourceDeltaEpsilon = 0.01f;
    private static readonly Dictionary<string, State> States = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ForeignObservationState> ForeignObservations = new(StringComparer.OrdinalIgnoreCase);

    public static void Reset(string reason)
    {
        States.Clear();
        ForeignObservations.Clear();
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_MEDICAL_HANDS_WATCHDOG_RESET reason={Safe(reason)}; postTerminalOnly=true; recoveryAuthority=real_vanguard_lease_terminal_only; foreignNativeActivity=observe_active_recover_terminal_stale; sameItemContinuityRequired=true; positiveTerminalBreaksRecoveryAuthority=true; parentLatchReconciliation=true; nativeTerminalTruthRequired=true; rawControllerFieldClearForbidden=true; productiveLeaseNeverCancelled=true; tag={StatusTag}; Tag={PostTerminalStatusTag}; Tag={MedicalHandsWatchdogStatusTag}; Tag={MedicalLeaseStatusTag}");
    }

    public static int ForgetRuntimePresence(string? botProfileId, string reason)
    {
        string key = string.IsNullOrWhiteSpace(botProfileId) ? "none" : botProfileId.Trim();
        if (string.Equals(key, "none", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        int removed = 0;
        if (States.Remove(key)) removed++;
        if (ForeignObservations.Remove(key)) removed++;
        return removed;
    }

    public static void NotifyLeaseTerminal(VanguardExecutionLeaseState lease, VanguardMedicalActionOutcomeKind outcome, string reason, DateTimeOffset now)
    {
        if (lease == null || string.IsNullOrWhiteSpace(lease.BotProfileId)) return;

        State state = UpsertState(
            lease.BotProfileId,
            lease.LeaseId,
            lease.MedicalNeed.ToString(),
            outcome.ToString(),
            reason,
            now,
            MedicalRecoveryAuthority.VanguardLeaseTerminal);
        state.ItemInstanceId = Safe(lease.ItemInstanceId);
        state.ItemTemplateId = Safe(lease.ItemTemplateId);

        if (VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(lease.BotProfileId, out VanguardRaidOperatorRuntimeRecord runtime)
            && runtime.BotOwner != null)
        {
            MedicalHandsTruth boundaryTruth = CaptureTruth(runtime.BotOwner);
            if (!boundaryTruth.PositiveTerminalTruth)
            {
                EmitNativeControllerTransitionProbe(runtime.BotOwner, state, "lease_terminal_pending");
            }
            if (boundaryTruth.PositiveTerminalTruth)
            {
                state.PositiveTerminalObservedSinceLeaseTerminal = true;
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"VANGUARD_MEDICAL_POSITIVE_TERMINAL_OBSERVED_AT_LEASE_BOUNDARY botProfile={Safe(lease.BotProfileId)}; lease={Safe(lease.LeaseId)}; recoveryAuthority={state.RecoveryAuthority}; futureNativeMutationByThisLease=false; {boundaryTruth.Summary}; tag={StatusTag}");
            }
        }
    }

    /// <summary>
    /// Observes a native medical controller while no Vanguard medical lease owns it.
    /// Active or unproven foreign work remains observe-only. The separate Tick path may request
    /// either a bounded terminal-stale fast-forward or a native weapon restore for a stably Finished,
    /// idle Meds operation. This observation entry point never authorizes cancellation or mutation.
    /// </summary>
    public static bool ObserveForeignMedicalActivityWithoutLease(
        BotOwner botOwner,
        OperatorDecisionSnapshot snapshot,
        string reason,
        DateTimeOffset now,
        out string summary)
    {
        summary = "foreignObservation=not_required";
        if (botOwner == null || snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            summary = "foreignObservation=invalid_input";
            return false;
        }

        if (VanguardExecutionLeaseStore.TryGetActive(snapshot.BotProfileId, out _))
        {
            summary = "foreignObservation=active_vanguard_lease_owns_controller";
            return false;
        }

        MedicalHandsTruth truth = CaptureTruth(botOwner);
        if (!truth.OrphanMedicalController)
        {
            ClearForeignObservation(snapshot.BotProfileId);
            summary = "foreignObservation=no_native_medical_activity;" + truth.Summary;
            return false;
        }

        ObserveForeignMedicalActivity(snapshot, truth, now, reason);
        summary = "foreignObservation=preserved"
            + ";recoveryAuthority=None"
            + ";watchdogAdmissionStateCreated=false"
            + ";nativeMutation=false"
            + ";" + truth.Summary;
        return true;
    }

    /// <summary>
    /// Requests native cancellation at a known terminal/timeout boundary. The request is idempotent
    /// per lease; recovery remains unconfirmed until Tick observes positive stable hands truth.
    /// </summary>
    public static string RequestTerminalCancellation(
        BotOwner botOwner,
        OperatorDecisionSnapshot snapshot,
        VanguardExecutionLeaseState lease,
        string reason,
        DateTimeOffset now)
    {
        if (botOwner == null || snapshot == null || lease == null || string.IsNullOrWhiteSpace(lease.BotProfileId))
        {
            return "terminalCancellation=invalid_input";
        }

        State state = UpsertState(
            lease.BotProfileId,
            lease.LeaseId,
            lease.MedicalNeed.ToString(),
            "TerminalCancellationRequested",
            reason,
            now - NaturalSettleGrace,
            MedicalRecoveryAuthority.VanguardLeaseTerminal);
        state.ItemInstanceId = Safe(lease.ItemInstanceId);
        state.ItemTemplateId = Safe(lease.ItemTemplateId);
        state.TerminalCancellationRequested = true;

        MedicalHandsTruth truth = CaptureTruth(botOwner);
        if (truth.PositiveTerminalTruth)
        {
            state.PositiveTerminalObservedSinceLeaseTerminal = true;
        }
        if (truth.RecoveryClass == MedicalOrphanRecoveryClass.PhysicalMedicalController)
        {
            // An already Finished, idle native Meds operation must not be cancelled or have
            // its health effects removed. Once the lease is released, Tick can safely qualify
            // the native weapon-restoration path through independent stable observations.
            if (TryReadFinishedIdleMedsOperation(botOwner, truth, out _, out _))
            {
                return "terminalCancellation=finished_idle_meds_deferred_native_weapon_restore"
                    + ";healthMutation=false;childCancellation=false;postTerminalObservationRequired=true;"
                    + truth.Summary;
            }
            if (!HasOwnedPhysicalContinuity(state, truth, out string ownershipReason))
            {
                ObserveForeignMedicalActivity(
                    snapshot,
                    truth,
                    now,
                    "terminal_boundary_physical_activity_preserved:" + ownershipReason);
                return "terminalCancellation=physical_activity_preserved_no_owned_continuity;ownershipReason="
                    + Safe(ownershipReason)
                    + ";nativeMutation=false;"
                    + truth.Summary;
            }

            if (state.PhysicalRecoveryRequestCount == 0)
            {
                PhysicalRecoveryRequest request = RequestPhysicalRecovery(botOwner, truth, state, now, "terminal_boundary_owned:" + Safe(reason));
                return "terminalCancellation=physical_recovery_requested_unconfirmed;ownershipReason=" + Safe(ownershipReason) + ";" + request.Summary + ";" + truth.Summary;
            }

            return "terminalCancellation=physical_recovery_already_requested_unconfirmed;ownershipReason=" + Safe(ownershipReason) + ";requestCount=" + state.PhysicalRecoveryRequestCount + ";" + truth.Summary;
        }

        // A parent-only latch can be a brief native callback transition. It is reconciled only after
        // the exact firearm-ready signature is stable in Tick; terminal notification alone is not proof.
        return "terminalCancellation=classified_observation_pending;recoveryClass=" + truth.RecoveryClass + ";" + truth.Summary;
    }

    public static bool IsMedicalAdmissionBlocked(string? botProfileId, out string summary)
    {
        if (string.IsNullOrWhiteSpace(botProfileId) || !States.TryGetValue(botProfileId, out State? state))
        {
            summary = "post_terminal_hands_clear";
            return false;
        }

        summary = "post_terminal_hands_pending;lease=" + Safe(state.LeaseId)
            + ";outcome=" + Safe(state.Outcome)
            + ";reason=" + Safe(state.Reason)
            + ";recoveryAuthority=" + state.RecoveryAuthority
            + ";positiveTerminalObserved=" + Bool(state.PositiveTerminalObservedSinceLeaseTerminal)
            + ";classifiedRecoveryRequests=" + state.ClassifiedRecoveryRequestCount
            + ";terminalTruthConfirmed=false"
            + ";readySnapshots=" + state.ReadySnapshots
            + ";resourceDropsOutsideLease=" + state.ResourceDropsOutsideLease;
        return true;
    }

    public static void Tick(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        foreach (OperatorDecisionSnapshot snapshot in snapshots ?? Array.Empty<OperatorDecisionSnapshot>())
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            {
                continue;
            }

            if (!snapshot.Alive)
            {
                States.Remove(snapshot.BotProfileId);
                ClearForeignObservation(snapshot.BotProfileId);
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out VanguardRaidOperatorRuntimeRecord runtime)
                || runtime.BotOwner == null)
            {
                continue;
            }

            BotOwner botOwner = runtime.BotOwner;
            bool activeLease = VanguardExecutionLeaseStore.TryGetActive(snapshot.BotProfileId, out _);
            State state;
            if (!States.TryGetValue(snapshot.BotProfileId, out State? existingState) || existingState == null)
            {
                if (activeLease)
                {
                    continue;
                }

                // The runtime ownership boundary: native medicine observed without an active Vanguard lease
                // is not an orphan Vanguard owns. EFT/SAIN may have started it legitimately. Keep the
                // observation diagnostic-only and never create an admission-blocking recovery state.
                MedicalHandsTruth foreignTruth = CaptureTruth(botOwner);
                if (foreignTruth.OrphanMedicalController)
                {
                    // A finished native Meds operation can strand hands while the weapon selector is
                    // unready. Its terminality, not selector readiness, permits a bounded native restore.
                    if (TryReadFinishedIdleMedsOperation(botOwner, foreignTruth, out _, out _))
                        TryRestoreFinishedMedicalHands(botOwner, snapshot, foreignTruth, now, "native_terminal_without_vanguard_lease");
                    else
                    {
                        ResetFinishedHandsCandidate(snapshot.BotProfileId);
                        TryRecoverTerminalStaleForeignMedicalHands(botOwner, snapshot, foreignTruth, now, out _);
                    }
                    ObserveForeignMedicalActivity(
                        snapshot,
                        foreignTruth,
                        now,
                        "proactive_native_medical_activity_without_vanguard_lease");
                }
                else
                {
                    if (foreignTruth.PositiveTerminalTruth)
                        ConfirmFinishedHandsRestoration(snapshot, foreignTruth, now);
                    ClearForeignObservation(snapshot.BotProfileId);
                }
                continue;
            }
            else
            {
                state = existingState;
            }

            if (activeLease)
            {
                ClearForeignObservation(snapshot.BotProfileId);
                // A terminal cancellation request may be issued before CompleteLease releases ownership.
                // Do not mutate again from the watchdog while the productive lease still exists.
                state.ResetObservationCandidates();
                continue;
            }

            MedicalHandsTruth truth = CaptureTruth(botOwner);
            if (truth.PositiveTerminalTruth)
            {
                // Once a complete native terminal boundary has been observed, any later native medical
                // activity is a new chain. The previous Vanguard lease can never regain mutation authority.
                state.PositiveTerminalObservedSinceLeaseTerminal = true;
            }

            if (state.ClassifiedRecoveryRequestCount == 0 && now - state.TerminalAtUtc < NaturalSettleGrace)
            {
                continue;
            }

            ObserveOwnedResourceOutsideLease(state, snapshot, truth, now);

            if (truth.PositiveTerminalTruth)
            {
                state.ResetOrphanCandidate();
                DateTimeOffset observationAt = snapshot.CapturedAtUtc == DateTimeOffset.MinValue ? now : snapshot.CapturedAtUtc;
                if (state.LastReadySnapshotAtUtc == observationAt)
                {
                    continue;
                }

                state.LastReadySnapshotAtUtc = observationAt;
                if (state.ReadySinceUtc == DateTimeOffset.MinValue)
                {
                    state.ReadySinceUtc = now;
                    state.ReadySnapshots = 1;
                    continue;
                }

                state.ReadySnapshots++;
                if (state.ReadySnapshots < 2 || now - state.ReadySinceUtc < StableWindow)
                {
                    continue;
                }

                bool refreshed = false;
                try
                {
                    botOwner.Medecine?.RefreshCurMeds();
                    refreshed = true;
                }
                catch { }

                EmitNativeControllerTransitionProbe(botOwner, state, "positive_terminal_confirmed");
                VanguardClientDiagnosticsLog.Info(MedicalHandsWatchdogStatusTag,
                    $"VANGUARD_MEDICAL_HANDS_TERMINAL_TRUTH_CONFIRMED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(state.LeaseId)}; readySnapshots={state.ReadySnapshots}; stableSeconds={(now - state.ReadySinceUtc).TotalSeconds:0.00}; physicalRecoveryRequests={state.PhysicalRecoveryRequestCount}; latchReconciliations={state.LatchReconciliationCount}; resourceDropsOutsideLease={state.ResourceDropsOutsideLease}; refreshAfterTerminal={Bool(refreshed)}; admissionReleased=true; successProof=medicine_idle_and_nonmedical_weapon_hands; {truth.Summary}; tag={MedicalHandsWatchdogStatusTag}; Tag={MedicalLeaseStatusTag}; legacyTag={StatusTag}");
                States.Remove(snapshot.BotProfileId);
                ConfirmFinishedHandsRestoration(snapshot, truth, now);
                ClearForeignObservation(snapshot.BotProfileId);
                continue;
            }

            state.ResetReadyCandidate();
            if (!truth.OrphanMedicalController)
            {
                // Non-medical transitional hands remain unsafe for a new medicine transaction, but
                // must not trigger medical recovery mutations.
                state.ResetOrphanCandidate();
                continue;
            }

            DateTimeOffset orphanObservationAt = snapshot.CapturedAtUtc == DateTimeOffset.MinValue ? now : snapshot.CapturedAtUtc;
            if (state.CandidateRecoveryClass != truth.RecoveryClass)
            {
                state.ResetOrphanCandidate();
                state.CandidateRecoveryClass = truth.RecoveryClass;
            }
            if (state.LastOrphanSnapshotAtUtc != orphanObservationAt)
            {
                state.LastOrphanSnapshotAtUtc = orphanObservationAt;
                if (state.OrphanCandidateSinceUtc == DateTimeOffset.MinValue)
                {
                    state.OrphanCandidateSinceUtc = now;
                    state.OrphanSnapshots = 1;
                }
                else
                {
                    state.OrphanSnapshots++;
                }
            }

            bool stableOrphan = state.OrphanSnapshots >= RequiredSnapshots
                && state.OrphanCandidateSinceUtc != DateTimeOffset.MinValue
                && now - state.OrphanCandidateSinceUtc >= StableWindow;
            if (!stableOrphan)
            {
                continue;
            }

            if (truth.RecoveryClass == MedicalOrphanRecoveryClass.PhysicalMedicalController)
            {
                // Finished is a positive native operation boundary even when a previously queued
                // weapon change has left IsWeaponReady false. Do not replay health/child cancellation
                // against a finished operation: restore weapon hands via the native bot selector instead.
                if (TryReadFinishedIdleMedsOperation(botOwner, truth, out _, out _))
                {
                    TryRestoreFinishedMedicalHands(botOwner, snapshot, truth, now, "vanguard_terminal_finished_native_operation");
                    continue;
                }
                ResetFinishedHandsCandidate(snapshot.BotProfileId);
                if (!HasOwnedPhysicalContinuity(state, truth, out string ownershipReason))
                {
                    // A previous Vanguard terminal lease does not give ownership
                    // of an unrelated native item. However, that unrelated AI-2
                    // can independently become a proven exhausted orphan. Apply
                    // only the same bounded native fast-forward used for foreign
                    // terminal-stale hands, never the previous lease's cleanup.
                    if (IsExhaustedIdleMedsController(botOwner, truth)
                        && now - state.TerminalAtUtc >= ForeignStalePhysicalSettleGrace)
                        TryRecoverTerminalStaleForeignMedicalHands(
                            botOwner, snapshot, truth, now, out _);
                    ObserveForeignMedicalActivity(
                        snapshot,
                        truth,
                        now,
                        "post_terminal_physical_activity_preserved:" + ownershipReason);
                    continue;
                }

                if (state.PhysicalRecoveryRequestCount == 0)
                {
                    PhysicalRecoveryRequest request = RequestPhysicalRecovery(botOwner, truth, state, now, "stable_vanguard_owned_post_terminal_physical_controller");
                    VanguardClientDiagnosticsLog.Warning(StatusTag,
                        $"VANGUARD_MEDICAL_HANDS_OWNED_PHYSICAL_RECOVERY_REQUESTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(state.LeaseId)}; outcome={Safe(state.Outcome)}; terminalReason={Safe(state.Reason)}; recoveryAuthority={state.RecoveryAuthority}; ownershipReason={Safe(ownershipReason)}; recoveryClass={truth.RecoveryClass}; request={request.Summary}; recoveryConfirmed=false; admissionBlocked=true; rawControllerFieldClear=false; {truth.Summary}; tag={StatusTag}; Tag={MedicalHandsWatchdogStatusTag}; Tag={MedicalLeaseStatusTag}");
                    continue;
                }

                if (state.PhysicalRecoveryRequestCount < MaximumPhysicalRecoveryRequests
                    && now - state.LastPhysicalRecoveryRequestAtUtc >= PhysicalRecoveryRetryDelay)
                {
                    PhysicalRecoveryRequest retry = RequestPhysicalRecovery(botOwner, truth, state, now, "bounded_vanguard_owned_physical_recovery_retry");
                    VanguardClientDiagnosticsLog.Warning(StatusTag,
                        $"VANGUARD_MEDICAL_HANDS_OWNED_PHYSICAL_RECOVERY_RETRY operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(state.LeaseId)}; recoveryAuthority={state.RecoveryAuthority}; ownershipReason={Safe(ownershipReason)}; recoveryClass={truth.RecoveryClass}; request={retry.Summary}; retryCount={state.PhysicalRecoveryRequestCount - 1}; retryBudget=1; recoveryConfirmed=false; admissionBlocked=true; rawControllerFieldClear=false; {truth.Summary}; tag={StatusTag}; Tag={MedicalHandsWatchdogStatusTag}");
                    continue;
                }
            }
            else if (truth.RecoveryClass == MedicalOrphanRecoveryClass.ParentUsingLatch)
            {
                ResetFinishedHandsCandidate(snapshot.BotProfileId);
                if (state.PositiveTerminalObservedSinceLeaseTerminal)
                {
                    ObserveForeignMedicalActivity(
                        snapshot,
                        truth,
                        now,
                        "parent_using_latch_after_positive_terminal_preserved");
                    continue;
                }

                if (state.LatchReconciliationCount < MaximumLatchReconciliations)
                {
                    ParentLatchReconciliation reconciliation = ReconcileParentUsingLatch(botOwner, state, now, "stable_parent_using_latch_with_firearm_ready");
                    VanguardClientDiagnosticsLog.Warning(MedicalHandsWatchdogStatusTag,
                        $"VANGUARD_MEDICAL_HANDS_PARENT_LATCH_RECONCILED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(state.LeaseId)}; recoveryClass={truth.RecoveryClass}; reconciliation={reconciliation.Summary}; recoveryConfirmed=false; admissionBlocked=true; physicalMedEffectMutation=false; childCancellation=false; rawControllerFieldClear=false; {truth.Summary}; tag={MedicalHandsWatchdogStatusTag}; Tag={MedicalLeaseStatusTag}");
                    continue;
                }
            }
            else
            {
                // Parent Using without firearm readiness is a transition, not a proven latch. Observe only.
                continue;
            }

            DateTimeOffset lastMutationAt = state.LastPhysicalRecoveryRequestAtUtc > state.LastLatchReconciliationAtUtc
                ? state.LastPhysicalRecoveryRequestAtUtc
                : state.LastLatchReconciliationAtUtc;
            if (!state.RecoveryExhaustedLogged
                && lastMutationAt != DateTimeOffset.MinValue
                && now - lastMutationAt >= RecoveryExhaustedLogDelay)
            {
                state.RecoveryExhaustedLogged = true;
                EmitNativeControllerTransitionProbe(botOwner, state, "recovery_exhausted");
                VanguardClientDiagnosticsLog.Warning(MedicalHandsWatchdogStatusTag,
                    $"VANGUARD_MEDICAL_HANDS_RECOVERY_UNCONFIRMED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(state.LeaseId)}; recoveryClass={truth.RecoveryClass}; physicalRecoveryRequests={state.PhysicalRecoveryRequestCount}; latchReconciliations={state.LatchReconciliationCount}; retryBudgetExhausted=true; repeatedMutation=false; rawControllerFieldClear=false; admissionBlocked=true; next=observe_until_positive_terminal_truth; {truth.Summary}; tag={MedicalHandsWatchdogStatusTag}; Tag={MedicalLeaseStatusTag}; legacyTag={StatusTag}");
            }
        }
    }

    private static State UpsertState(
        string botProfileId,
        string leaseId,
        string need,
        string outcome,
        string reason,
        DateTimeOffset terminalAtUtc,
        MedicalRecoveryAuthority recoveryAuthority)
    {
        if (!States.TryGetValue(botProfileId, out State? state)
            || !string.Equals(state.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase))
        {
            state = new State
            {
                LeaseId = Safe(leaseId),
                Need = Safe(need),
                Outcome = Safe(outcome),
                Reason = Safe(reason),
                TerminalAtUtc = terminalAtUtc,
                RecoveryAuthority = recoveryAuthority,
            };
            States[botProfileId] = state;
            return state;
        }

        state.Need = Safe(need);
        state.Outcome = Safe(outcome);
        state.Reason = Safe(reason);
        if (state.RecoveryAuthority == MedicalRecoveryAuthority.None)
        {
            state.RecoveryAuthority = recoveryAuthority;
        }
        if (state.TerminalAtUtc == DateTimeOffset.MinValue || terminalAtUtc < state.TerminalAtUtc)
        {
            state.TerminalAtUtc = terminalAtUtc;
        }
        return state;
    }

    private static MedicalHandsTruth CaptureTruth(BotOwner botOwner)
    {
        bool botMedicineUsing = botOwner.Medecine?.Using == true;
        bool firstAidUsing = botOwner.Medecine?.FirstAid?.Using == true;
        bool surgeryUsing = botOwner.Medecine?.SurgicalKit?.Using == true;
        bool stimulantUsing = ReadBool(botOwner.Medecine?.Stimulators, "Using");
        VanguardPostLootWeaponReadinessSnapshot readiness = VanguardPostLootWeaponReadinessReader.Capture(botOwner);
        // Native weapon-selector ability and physical weapon-in-hands truth are distinct.
        // A MedsController can be idle with a selectable weapon but is never weapon hands.
        bool nativeSelectorReady = false;
        try
        {
            nativeSelectorReady = botOwner.WeaponManager?.Selector?.IsWeaponReady == true
                && botOwner.GetPlayer?.InventoryController?.IsChangingWeapon != true;
        }
        catch
        {
            // Inaccessible native selector state cannot authorize terminal cleanup.
        }

        object? handsController = null;
        try { handsController = botOwner.GetPlayer?.HandsController; }
        catch { }
        string handsType = handsController?.GetType().Name ?? readiness.HandsControllerType ?? "none";
        bool typedMedsController = handsController is Player.MedsController;
        bool medicalHands = typedMedsController
            || handsType.IndexOf("Meds", StringComparison.OrdinalIgnoreCase) >= 0
            || handsType.IndexOf("Medicine", StringComparison.OrdinalIgnoreCase) >= 0;

        MedsItemClass? activeItem = null;
        string activeItemSource = "none";
        if (handsController is Player.MedsController medsController)
        {
            activeItem = medsController.Item as MedsItemClass;
            activeItemSource = activeItem == null ? "typed_hands_no_item" : "typed_hands";
        }

        // Child controllers keep their selected item even when the hands controller is briefly unreadable
        // or has already switched. Read those public native references only for identity/provenance proof;
        // The runtime never writes CurUsingMeds, Nullable_0 or any raw controller field.
        if (activeItem == null && firstAidUsing)
        {
            activeItem = botOwner.Medecine?.FirstAid?.CurUsingMeds;
            activeItemSource = activeItem == null ? "first_aid_child_no_item" : "first_aid_child";
        }
        if (activeItem == null && surgeryUsing)
        {
            activeItem = botOwner.Medecine?.SurgicalKit?.CurUsingMeds;
            activeItemSource = activeItem == null ? "surgery_child_no_item" : "surgery_child";
        }
        if (activeItem == null && stimulantUsing)
        {
            activeItem = botOwner.Medecine?.Stimulators?.StimulatorItemClass;
            activeItemSource = activeItem == null ? "stimulator_child_no_item" : "stimulator_child";
        }

        string itemInstanceId = VanguardMedicalInventoryReader.ResolveItemInstanceId(activeItem);
        string itemTemplateId = activeItem == null ? "none" : Safe(activeItem.StringTemplateId);
        string itemName = activeItem == null ? "none" : Safe(activeItem.Name ?? activeItem.ShortName ?? activeItem.StringTemplateId);
        float itemResource = VanguardMedicalInventoryReader.ReadItemResource(activeItem);
        float itemMaxResource = VanguardMedicalInventoryReader.ReadItemMaxResource(activeItem);

        return new MedicalHandsTruth(
            botMedicineUsing,
            firstAidUsing,
            surgeryUsing,
            stimulantUsing,
            medicalHands,
            typedMedsController,
            readiness.WeaponReady,
            nativeSelectorReady,
            handsType,
            activeItemSource,
            itemInstanceId,
            itemTemplateId,
            itemName,
            itemResource,
            itemMaxResource);
    }

    private static PhysicalRecoveryRequest RequestPhysicalRecovery(
        BotOwner botOwner,
        MedicalHandsTruth truth,
        State state,
        DateTimeOffset now,
        string reason)
    {
        bool medEffectRemoved = false;
        bool healthCancel = false;
        bool medsQueueCleared = false;
        bool fastForwardCurrentState = false;
        bool firstAidCancel = false;
        bool surgeryCancel = false;
        bool stimulantCancel = false;
        bool takePrevWeapon = false;
        string handsType = "none";

        EmitNativeControllerTransitionProbe(botOwner, state, "before_physical_recovery_" + (state.PhysicalRecoveryRequestCount + 1));
        try
        {
            Player? player = botOwner.GetPlayer;
            object? handsController = player?.HandsController;
            handsType = handsController?.GetType().Name ?? "none";

            try
            {
                if (player?.ActiveHealthController != null)
                {
                    player.ActiveHealthController.RemoveMedEffect();
                    medEffectRemoved = true;
                }
            }
            catch { }

            try
            {
                player?.HealthController?.CancelApplyingItem();
                healthCancel = player?.HealthController != null;
            }
            catch { }

            if (handsController is Player.MedsController medsController)
            {
                // Keep parity with the already-audited foreign stale-controller recovery. This
                // executes only after Vanguard-owned terminal continuity is proven and the watchdog has
                // observed a stable orphan PhysicalMedicalController. It asks the native controller to
                // finish its own state machine; no HP, resource or raw controller field is written.
                // "WeaponReady" now correctly rejects physical MedsController hands. For
                // owned terminal cleanup we only require the *selector* to be available;
                // ownership, typed medical hands and all native Using flags are checked above.
                if (!truth.AnyMedicineUsing && truth.NativeSelectorReady)
                {
                    try { medsController.FastForwardCurrentState(); fastForwardCurrentState = true; }
                    catch { }
                }
                try { medsController.ClearQueue(); medsQueueCleared = true; }
                catch { }
            }
        }
        catch { }

        // Cancel only controllers proven active by the same classified snapshot. This prevents the
        // parent-only latch path from replaying child cancellation against already-idle controllers.
        firstAidCancel = truth.FirstAidUsing && TryCancel(botOwner.Medecine?.FirstAid);
        surgeryCancel = truth.SurgeryUsing && TryCancel(botOwner.Medecine?.SurgicalKit);
        stimulantCancel = truth.StimulantUsing && TryCancel(botOwner.Medecine?.Stimulators);
        takePrevWeapon = InvokeNoArg(VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "WeaponManager", "Selector"), "TakePrevWeapon");

        state.PhysicalRecoveryRequestCount++;
        state.LastPhysicalRecoveryRequestAtUtc = now;
        if (state.FirstPhysicalRecoveryRequestAtUtc == DateTimeOffset.MinValue)
        {
            state.FirstPhysicalRecoveryRequestAtUtc = now;
        }

        // Read-only transition evidence: accepted native operations and asynchronous hand-swap
        // completion are distinct. Never interpret this synchronous readback as proof of success.
        EmitNativeControllerTransitionProbe(botOwner, state, "after_physical_recovery_" + state.PhysicalRecoveryRequestCount);
        return new PhysicalRecoveryRequest(
            medEffectRemoved,
            healthCancel,
            medsQueueCleared,
            fastForwardCurrentState,
            firstAidCancel,
            surgeryCancel,
            stimulantCancel,
            takePrevWeapon,
            handsType,
            state.PhysicalRecoveryRequestCount,
            reason);
    }

    /// <summary>
    /// Transition-only diagnostic. Read native hands, Meds operation and weapon selector
    /// without invoking an action or changing admission. This instrument does not decide recovery.
    /// </summary>
    private static void EmitNativeControllerTransitionProbe(BotOwner botOwner, State state, string stage)
    {
        try
        {
            Player? player = botOwner.GetPlayer;
            object? hands = player?.HandsController;
            object? operation = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(
                hands, "ObservedMedsControllerClass", "CurrentHandsOperation", "CurrentOperation");
            object? operationState = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(operation, "State");
            var selector = botOwner.WeaponManager?.Selector;
            var readiness = VanguardPostLootWeaponReadinessReader.Capture(botOwner);
            string handsId = hands == null ? "none" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(hands).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string operationId = operation == null ? "none" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(operation).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string selectorReady = selector == null ? "unknown" : Bool(selector.IsWeaponReady);
            string selectorErrors = selector == null ? "unknown" : selector.ErrorCounter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string nativeTime = UnityEngine.Time.time.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            string inventoryChanging = player?.InventoryController == null ? "unknown" : Bool(player.InventoryController.IsChangingWeapon);
            string selectorChanging = selector == null ? "unknown" : Bool(selector.IsChanging);
            string selectorStarted = selector == null ? "unknown" : selector.StartChangeTime.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            string selectorNext = selector == null ? "unknown" : selector.NextChangeTime.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            string selectorLastSlot = selector == null ? "none" : selector.LastEquipmentSlot.ToString();
            string selectorMainSlot = selector == null ? "none" : selector.MainWeapon.ToString();
            VanguardClientDiagnosticsLog.Info(MedicalHandsWatchdogStatusTag,
                $"VANGUARD_MEDICAL_NATIVE_CONTROLLER_TRANSITION_PROBE stage={Safe(stage)}; lease={Safe(state.LeaseId)}; recoveryRequests={state.PhysicalRecoveryRequestCount}; hands={Safe(hands?.GetType().Name)}; handsId={handsId}; handsDestroyed={Bool((hands as Player.AbstractHandsController)?.Destroyed == true)}; typedMeds={Bool(hands is Player.MedsController)}; operation={Safe(operation?.GetType().Name)}; operationId={operationId}; operationState={Safe(VanguardOperatorRuntimeAuditReflection.Text(operationState))}; selectorReady={selectorReady}; selectorErrors={selectorErrors}; selectorNativeTime={nativeTime}; inventoryChanging={inventoryChanging}; selectorChanging={selectorChanging}; selectorStartChange={selectorStarted}; selectorNextChange={selectorNext}; selectorLastSlot={Safe(selectorLastSlot)}; selectorMainSlot={Safe(selectorMainSlot)}; heuristicWeaponReady={Bool(readiness.WeaponReady)}; heuristicHandsSuspicious={Bool(readiness.HandsSuspicious)}; noNativeMutation=true; postRequestNotCompletionProof=true; tag={MedicalHandsWatchdogStatusTag}");
        }
        catch
        {
            // A diagnostic failure must never affect native medical recovery, admission or gameplay.
        }
    }

    private static ParentLatchReconciliation ReconcileParentUsingLatch(BotOwner botOwner, State state, DateTimeOffset now, string reason)
    {
        bool callbackInvoked = false;
        try
        {
            if (botOwner.Medecine != null)
            {
                // This is the aggregate callback used by native child completion. It repairs only the
                // stale parent latch and does not touch hands, health resources, child state or SAIN.
                botOwner.Medecine.method_0(false);
                callbackInvoked = true;
            }
        }
        catch { }

        state.LatchReconciliationCount++;
        state.LastLatchReconciliationAtUtc = now;
        return new ParentLatchReconciliation(callbackInvoked, state.LatchReconciliationCount, reason);
    }

    private static void ObserveOwnedResourceOutsideLease(State state, OperatorDecisionSnapshot snapshot, MedicalHandsTruth truth, DateTimeOffset now)
    {
        if (!truth.OrphanMedicalController
            || truth.ItemResource < 0f
            || string.Equals(truth.ItemInstanceId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!HasOwnedItemContinuity(state, truth))
        {
            ObserveForeignMedicalActivity(snapshot, truth, now, "resource_observation_without_owned_item_continuity");
            return;
        }

        if (!string.Equals(state.LastObservedItemInstanceId, truth.ItemInstanceId, StringComparison.OrdinalIgnoreCase))
        {
            state.LastObservedItemInstanceId = truth.ItemInstanceId;
            state.LastObservedItemResource = truth.ItemResource;
            state.LastObservedResourceAtUtc = now;
            return;
        }

        if (state.LastObservedItemResource >= 0f
            && truth.ItemResource + ResourceDeltaEpsilon < state.LastObservedItemResource)
        {
            float delta = state.LastObservedItemResource - truth.ItemResource;
            state.ResourceDropsOutsideLease++;
            VanguardClientDiagnosticsLog.Warning(StatusTag,
                $"{ResourceOutsideLeaseTag} operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(state.LeaseId)}; recoveryAuthority={state.RecoveryAuthority}; itemContinuity=true; positiveTerminalObserved={Bool(state.PositiveTerminalObservedSinceLeaseTerminal)}; itemInstance={Safe(truth.ItemInstanceId)}; itemTpl={Safe(truth.ItemTemplateId)}; item={Safe(truth.ItemName)}; resourceBefore={state.LastObservedItemResource:0.00}; resourceAfter={truth.ItemResource:0.00}; resourceDelta={delta:0.00}; activeVanguardLease=false; ownershipViolation=true; physicalRecoveryRequests={state.PhysicalRecoveryRequestCount}; latchReconciliations={state.LatchReconciliationCount}; tag={StatusTag}; Tag={MedicalHandsWatchdogStatusTag}; Tag={MedicalLeaseStatusTag}");
        }

        state.LastObservedItemResource = truth.ItemResource;
        state.LastObservedResourceAtUtc = now;
    }

    private static bool HasOwnedPhysicalContinuity(State state, MedicalHandsTruth truth, out string reason)
    {
        if (state.RecoveryAuthority != MedicalRecoveryAuthority.VanguardLeaseTerminal)
        {
            reason = "recovery_authority_not_vanguard_lease_terminal";
            return false;
        }

        if (state.PositiveTerminalObservedSinceLeaseTerminal)
        {
            reason = "positive_terminal_already_observed_breaks_chain";
            return false;
        }

        if (!truth.PhysicalMedicalControllerActive)
        {
            reason = "physical_medical_controller_not_active";
            return false;
        }

        if (!HasOwnedItemContinuity(state, truth))
        {
            reason = "leased_item_identity_not_continuous";
            return false;
        }

        reason = state.TerminalCancellationRequested
            ? "same_item_continuity_after_explicit_terminal_cancellation"
            : "same_item_continuity_from_vanguard_lease_terminal";
        return true;
    }

    private static bool HasOwnedItemContinuity(State state, MedicalHandsTruth truth)
    {
        return state.RecoveryAuthority == MedicalRecoveryAuthority.VanguardLeaseTerminal
            && !state.PositiveTerminalObservedSinceLeaseTerminal
            && !string.Equals(state.ItemInstanceId, "none", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(truth.ItemInstanceId, "none", StringComparison.OrdinalIgnoreCase)
            && string.Equals(state.ItemInstanceId, truth.ItemInstanceId, StringComparison.OrdinalIgnoreCase);
    }

    // A depleted native medical item need not disappear from inventory when its
    // healing resource reaches zero. EFT's MedsController checks HpResource
    // before advancing, but its completion can depend on a later native effect
    // callback. In that orphaned terminal state IsWeaponReady may remain false
    // *because MedsController still owns the hands*. Do not use selector readiness
    // as a circular requirement for this one exhausted/idle state. Never remove
    // the item from inventory or cancel an active medicine procedure.
    private static bool IsExhaustedIdleMedsController(BotOwner botOwner, MedicalHandsTruth truth)
    {
        if (!truth.TypedMedsController || !truth.MedicalHands || truth.AnyMedicineUsing
            || truth.ItemResource < 0f || truth.ItemResource > ResourceDeltaEpsilon
            || truth.ItemMaxResource <= 0f
            || string.Equals(truth.ItemInstanceId, "none", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            return botOwner.GetPlayer?.InventoryController?.IsChangingWeapon == false
                && botOwner.GetPlayer?.HandsController is Player.MedsController;
        }
        catch { return false; }
    }

    // Native EFT's MedsController.FastForwardCurrentState does nothing if the operation is
    // already Finished. BotWeaponSelector.TakePrevWeapon also refuses to work while IsWeaponReady
    // is false. For this precise terminal deadlock, TakeMainWeapon uses the native SetSlotItem
    // callback path without the selector's readiness gate. No field is forced to a ready value.
    // The item/resource and exact native operation must remain unchanged across distinct snapshots.
    private static bool TryReadFinishedIdleMedsOperation(
        BotOwner botOwner,
        MedicalHandsTruth truth,
        out Player.MedsController? meds,
        out Player.MedsController.ObservedMedsControllerClass? operation)
    {
        meds = null;
        operation = null;
        if (botOwner == null || !truth.TypedMedsController || !truth.MedicalHands
            || truth.AnyMedicineUsing || string.Equals(truth.ItemInstanceId, "none", StringComparison.OrdinalIgnoreCase)
            || truth.ItemResource < 0f)
            return false;

        try
        {
            meds = botOwner.GetPlayer?.HandsController as Player.MedsController;
            if (meds is null) return false;
            operation = ((Player.ItemHandsController)meds).CurrentHandsOperation as Player.MedsController.ObservedMedsControllerClass;
            return meds != null && operation != null
                && string.Equals(operation.State.ToString(), "Finished", StringComparison.Ordinal);
        }
        catch
        {
            meds = null;
            operation = null;
            return false;
        }
    }

    private static void TryRestoreFinishedMedicalHands(
        BotOwner botOwner,
        OperatorDecisionSnapshot snapshot,
        MedicalHandsTruth truth,
        DateTimeOffset now,
        string authorityReason)
    {
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId)
            || VanguardExecutionLeaseStore.TryGetActive(snapshot.BotProfileId, out _)
            || !TryReadFinishedIdleMedsOperation(botOwner, truth, out var meds, out var operation))
        {
            ResetFinishedHandsCandidate(snapshot?.BotProfileId);
            return;
        }

        if (!ForeignObservations.TryGetValue(snapshot.BotProfileId, out ForeignObservationState? state) || state == null)
        {
            state = new ForeignObservationState();
            ForeignObservations[snapshot.BotProfileId] = state;
        }

        bool sameTerminalOperation = ReferenceEquals(state.FinishedMedsController, meds)
            && ReferenceEquals(state.FinishedHandsOperation, operation)
            && string.Equals(state.FinishedHandsItemInstanceId, truth.ItemInstanceId, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(state.FinishedHandsItemResource - truth.ItemResource) <= ResourceDeltaEpsilon;
        if (!sameTerminalOperation)
        {
            state.FinishedMedsController = meds;
            state.FinishedHandsOperation = operation;
            state.FinishedHandsItemInstanceId = truth.ItemInstanceId;
            state.FinishedHandsItemResource = truth.ItemResource;
            state.FinishedHandsCandidateSinceUtc = now;
            state.FinishedHandsLastSnapshotAtUtc = snapshot.CapturedAtUtc == DateTimeOffset.MinValue ? now : snapshot.CapturedAtUtc;
            state.FinishedHandsCandidateSnapshots = 1;
            state.FinishedHandsRestoreRequests = 0;
            state.FinishedHandsLastRequestAtUtc = DateTimeOffset.MinValue;
            state.FinishedHandsExhaustionLogged = false;
            return;
        }

        DateTimeOffset observationAt = snapshot.CapturedAtUtc == DateTimeOffset.MinValue ? now : snapshot.CapturedAtUtc;
        if (state.FinishedHandsLastSnapshotAtUtc != observationAt)
        {
            state.FinishedHandsLastSnapshotAtUtc = observationAt;
            state.FinishedHandsCandidateSnapshots++;
        }
        if (state.FinishedHandsCandidateSnapshots < RequiredSnapshots
            || now - state.FinishedHandsCandidateSinceUtc < FinishedHandsSettleGrace)
            return;

        if (state.FinishedHandsRestoreRequests >= MaximumFinishedHandsRestoreRequests)
        {
            if (!state.FinishedHandsExhaustionLogged
                && now - state.FinishedHandsLastRequestAtUtc >= FinishedHandsRestoreRetryDelay)
            {
                state.FinishedHandsExhaustionLogged = true;
                VanguardClientDiagnosticsLog.Warning(MedicalHandsWatchdogStatusTag,
                    $"VANGUARD_MEDICAL_FINISHED_HANDS_RESTORE_UNCONFIRMED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; itemInstance={Safe(truth.ItemInstanceId)}; requests={state.FinishedHandsRestoreRequests}; activeLease=false; sameTerminalOperation=true; recoveryExhausted=true; next=observe_native_truth; {truth.Summary}; tag={MedicalHandsWatchdogStatusTag}");
            }
            return;
        }
        if (state.FinishedHandsLastRequestAtUtc != DateTimeOffset.MinValue
            && now - state.FinishedHandsLastRequestAtUtc < FinishedHandsRestoreRetryDelay)
            return;

        // A real native inventory/weapon transition is not a stuck terminal state. Never
        // interleave our fallback with one, and never restore while the bot is despawning.
        try
        {
            var player = botOwner.GetPlayer;
            var selector = botOwner.WeaponManager?.Selector;
            if (botOwner.BotState != EBotState.Active || player?.InventoryController == null
                || player.InventoryController.IsChangingWeapon
                || selector == null || selector.IsChanging
                || (selector.StartChangeTime > 0f && UnityEngine.Time.time - selector.StartChangeTime < 5f)
                || botOwner.WeaponManager?.Reload?.Reloading == true)
                return;

            if (!ReferenceEquals(player.HandsController, meds)
                || !ReferenceEquals(((Player.ItemHandsController)meds).CurrentHandsOperation as Player.MedsController.ObservedMedsControllerClass, operation)
                || !string.Equals(operation?.State.ToString(), "Finished", StringComparison.Ordinal))
                return;

            selector.FindItemsInSlots(anyway: true);
            if (selector.FirstPrimaryWeaponItem == null && selector.SecondPrimaryWeaponItem == null
                && selector.HolsterItem == null)
                return;

            state.FinishedHandsRestoreRequests++;
            state.FinishedHandsLastRequestAtUtc = now;
            selector.TakeMainWeapon();
            VanguardClientDiagnosticsLog.Warning(MedicalHandsWatchdogStatusTag,
                $"VANGUARD_MEDICAL_FINISHED_HANDS_NATIVE_RESTORE_REQUESTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; source={Safe(authorityReason)}; operationState=Finished; stableSeconds={(now - state.FinishedHandsCandidateSinceUtc).TotalSeconds:0.00}; stableSnapshots={state.FinishedHandsCandidateSnapshots}; itemInstance={Safe(truth.ItemInstanceId)}; itemResource={truth.ItemResource:0.00}; selectorReadyBefore={Bool(truth.NativeSelectorReady)}; requests={state.FinishedHandsRestoreRequests}; nativeAction=BotWeaponSelector.TakeMainWeapon; healthMutation=false; itemResourceMutation=false; childCancellation=false; rawFieldOverride=false; completionProof=pending_weapon_hands_observation; {truth.Summary}; tag={MedicalHandsWatchdogStatusTag}");
        }
        catch (Exception ex)
        {
            // Failure consumes a bounded attempt; a new action cannot be claimed successful.
            VanguardClientDiagnosticsLog.Warning(MedicalHandsWatchdogStatusTag,
                $"VANGUARD_MEDICAL_FINISHED_HANDS_NATIVE_RESTORE_FAILED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(ex.GetType().Name)}; requests={state.FinishedHandsRestoreRequests}; restored=false; tag={MedicalHandsWatchdogStatusTag}");
        }
    }

    private static void ConfirmFinishedHandsRestoration(OperatorDecisionSnapshot snapshot, MedicalHandsTruth truth, DateTimeOffset now)
    {
        if (snapshot == null || !truth.PositiveTerminalTruth || !truth.NativeSelectorReady
            || string.IsNullOrWhiteSpace(snapshot.BotProfileId)
            || !ForeignObservations.TryGetValue(snapshot.BotProfileId, out ForeignObservationState? state)
            || state == null || state.FinishedHandsRestoreRequests == 0)
            return;
        VanguardClientDiagnosticsLog.Info(MedicalHandsWatchdogStatusTag,
            $"VANGUARD_MEDICAL_FINISHED_HANDS_RESTORED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requestCount={state.FinishedHandsRestoreRequests}; nativeWeaponHandsConfirmed=true; stableSince={state.FinishedHandsCandidateSinceUtc:O}; confirmedAt={now:O}; tag={MedicalHandsWatchdogStatusTag}");
    }

    private static void ResetFinishedHandsCandidate(string? botProfileId)
    {
        if (string.IsNullOrWhiteSpace(botProfileId)
            || !ForeignObservations.TryGetValue(botProfileId, out ForeignObservationState? state)
            || state == null)
            return;
        state.FinishedMedsController = null;
        state.FinishedHandsOperation = null;
        state.FinishedHandsItemInstanceId = "none";
        state.FinishedHandsItemResource = -1f;
        state.FinishedHandsCandidateSinceUtc = DateTimeOffset.MinValue;
        state.FinishedHandsLastSnapshotAtUtc = DateTimeOffset.MinValue;
        state.FinishedHandsCandidateSnapshots = 0;
        state.FinishedHandsRestoreRequests = 0;
        state.FinishedHandsLastRequestAtUtc = DateTimeOffset.MinValue;
        state.FinishedHandsExhaustionLogged = false;
    }

    private static bool TryRecoverTerminalStaleForeignMedicalHands(
        BotOwner botOwner,
        OperatorDecisionSnapshot snapshot,
        MedicalHandsTruth truth,
        DateTimeOffset now,
        out string summary)
    {
        summary = "foreignStaleRecovery=not_qualified";
        if (botOwner == null
            || snapshot == null
            || string.IsNullOrWhiteSpace(snapshot.BotProfileId)
            || VanguardExecutionLeaseStore.TryGetActive(snapshot.BotProfileId, out _)
            || !truth.TypedMedsController
            || !truth.MedicalHands
            || truth.AnyMedicineUsing
            || (!truth.NativeSelectorReady && !IsExhaustedIdleMedsController(botOwner, truth))
            || string.Equals(truth.ItemInstanceId, "none", StringComparison.OrdinalIgnoreCase))
        {
            ResetForeignStaleCandidate(snapshot?.BotProfileId);
            return false;
        }

        if (!ForeignObservations.TryGetValue(snapshot.BotProfileId, out ForeignObservationState? state) || state == null)
        {
            state = new ForeignObservationState();
            ForeignObservations[snapshot.BotProfileId] = state;
        }

        bool sameItem = string.Equals(state.StaleCandidateItemInstanceId, truth.ItemInstanceId, StringComparison.OrdinalIgnoreCase);
        bool resourceStable = sameItem
            && state.StaleCandidateItemResource >= 0f
            && truth.ItemResource >= 0f
            && Math.Abs(state.StaleCandidateItemResource - truth.ItemResource) <= ResourceDeltaEpsilon;
        if (!sameItem || !resourceStable)
        {
            state.StaleCandidateSinceUtc = now;
            state.StaleCandidateSnapshots = 1;
            state.StaleCandidateItemInstanceId = truth.ItemInstanceId;
            state.StaleCandidateItemResource = truth.ItemResource;
            state.LastStaleCandidateSnapshotAtUtc = snapshot.CapturedAtUtc == DateTimeOffset.MinValue ? now : snapshot.CapturedAtUtc;
            summary = "foreignStaleRecovery=settling;reason=new_or_resource_changed";
            return false;
        }

        DateTimeOffset observationAt = snapshot.CapturedAtUtc == DateTimeOffset.MinValue ? now : snapshot.CapturedAtUtc;
        if (state.LastStaleCandidateSnapshotAtUtc != observationAt)
        {
            state.LastStaleCandidateSnapshotAtUtc = observationAt;
            state.StaleCandidateSnapshots++;
        }
        state.StaleCandidateItemResource = truth.ItemResource;

        bool stableTerminalStale = state.StaleCandidateSnapshots >= RequiredSnapshots
            && state.StaleCandidateSinceUtc != DateTimeOffset.MinValue
            && now - state.StaleCandidateSinceUtc >= ForeignStalePhysicalSettleGrace;
        if (!stableTerminalStale)
        {
            summary = "foreignStaleRecovery=settling;snapshots=" + state.StaleCandidateSnapshots
                + ";ageSeconds=" + Math.Max(0d, (now - state.StaleCandidateSinceUtc).TotalSeconds).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            return false;
        }

        if (state.ForeignStaleRecoveryRequestCount >= MaximumForeignStaleRecoveryRequests
            || (state.LastForeignStaleRecoveryRequestAtUtc != DateTimeOffset.MinValue
                && now - state.LastForeignStaleRecoveryRequestAtUtc < ForeignStaleRecoveryRetryDelay))
        {
            summary = "foreignStaleRecovery=bounded_wait;requests=" + state.ForeignStaleRecoveryRequestCount;
            return false;
        }

        bool fastForwardRequested = false;
        bool takePrevWeapon = false;
        try
        {
            if (botOwner.GetPlayer?.HandsController is Player.MedsController medsController)
            {
                // CaptureTruth read the item from this exact typed controller in this same tick. The
                // stable-candidate proof above already binds its instance/resource across snapshots.
                medsController.FastForwardCurrentState();
                fastForwardRequested = true;
            }
        }
        catch { }
        if (fastForwardRequested && state.ForeignStaleRecoveryRequestCount > 0)
        {
            // Give the native MedsController one full retry window to finish its own transition. Only
            // the bounded second request also asks the native selector to restore the previous weapon.
            takePrevWeapon = InvokeNoArg(VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "WeaponManager", "Selector"), "TakePrevWeapon");
        }

        state.ForeignStaleRecoveryRequestCount++;
        state.LastForeignStaleRecoveryRequestAtUtc = now;
        VanguardClientDiagnosticsLog.Warning(MedicalHandsWatchdogStatusTag,
            $"VANGUARD_MEDICAL_FOREIGN_STALE_CONTROLLER_RECOVERY_REQUESTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; recoveryAuthority=terminal_stale_native_controller; activeVanguardLease=false; proof=typed_meds_controller+all_using_false+(native_selector_ready_or_exhausted_idle)+same_item_resource_stable; exhaustedIdle={Bool(IsExhaustedIdleMedsController(botOwner, truth))}; stableSeconds={Math.Max(0d, (now - state.StaleCandidateSinceUtc).TotalSeconds):0.00}; stableSnapshots={state.StaleCandidateSnapshots}; requestCount={state.ForeignStaleRecoveryRequestCount}; requestBudget={MaximumForeignStaleRecoveryRequests}; fastForwardCurrentState={Bool(fastForwardRequested)}; takePrevWeapon={Bool(takePrevWeapon)}; healthMutation=false; itemResourceMutation=false; childCancellation=false; rawControllerFieldClear=false; {truth.Summary}; tag={MedicalHandsWatchdogStatusTag}; legacyTag={StatusTag}");
        summary = "foreignStaleRecovery=requested;fastForward=" + Bool(fastForwardRequested) + ";takePrevWeapon=" + Bool(takePrevWeapon);
        return fastForwardRequested || takePrevWeapon;
    }

    private static void ResetForeignStaleCandidate(string? botProfileId)
    {
        if (string.IsNullOrWhiteSpace(botProfileId)
            || !ForeignObservations.TryGetValue(botProfileId, out ForeignObservationState? state)
            || state == null)
        {
            return;
        }
        state.StaleCandidateSinceUtc = DateTimeOffset.MinValue;
        state.StaleCandidateSnapshots = 0;
        state.StaleCandidateItemInstanceId = "none";
        state.StaleCandidateItemResource = -1f;
        state.LastStaleCandidateSnapshotAtUtc = DateTimeOffset.MinValue;
        state.ForeignStaleRecoveryRequestCount = 0;
        state.LastForeignStaleRecoveryRequestAtUtc = DateTimeOffset.MinValue;
    }

    private static void ObserveForeignMedicalActivity(
        OperatorDecisionSnapshot snapshot,
        MedicalHandsTruth truth,
        DateTimeOffset now,
        string reason)
    {
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId) || !truth.OrphanMedicalController)
        {
            return;
        }

        if (!ForeignObservations.TryGetValue(snapshot.BotProfileId, out ForeignObservationState? state) || state == null)
        {
            state = new ForeignObservationState();
            ForeignObservations[snapshot.BotProfileId] = state;
        }

        bool signatureChanged = !string.Equals(state.LastRecoveryClass, truth.RecoveryClass.ToString(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(state.LastItemInstanceId, truth.ItemInstanceId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(state.LastHandsType, truth.HandsType, StringComparison.OrdinalIgnoreCase);
        bool resourceDropped = string.Equals(state.LastItemInstanceId, truth.ItemInstanceId, StringComparison.OrdinalIgnoreCase)
            && state.LastItemResource >= 0f
            && truth.ItemResource >= 0f
            && truth.ItemResource + ResourceDeltaEpsilon < state.LastItemResource;
        bool periodic = state.LastLogAtUtc == DateTimeOffset.MinValue || now - state.LastLogAtUtc >= TimeSpan.FromSeconds(3d);

        if (signatureChanged || resourceDropped || periodic)
        {
            float delta = resourceDropped ? state.LastItemResource - truth.ItemResource : 0f;
            // Capture read-only native-operation evidence only when the existing throttled observation emits.
            // This is diagnostic: it does not qualify a recovery request or affect native medicine.
            // Resolve the existing runtime only when this already-throttled diagnostic is emitted.
            // A missing or unreadable owner is unknown evidence, never terminality proof.
            string foreignOperationProbe = "foreignTerminalSnapshotGate=unknown"
                + ";foreignHandsType=unknown"
                + ";foreignHandsIdentity=unknown"
                + ";foreignOperationType=unknown"
                + ";foreignOperationIdentity=unknown"
                + ";foreignOperationState=unknown"
                + ";foreignSelectorChanging=unknown"
                + ";foreignInventoryChanging=unknown"
                + ";foreignSelectorErrors=unknown"
                + ";foreignProbeReadOnly=true";
            try
            {
                if (VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(
                        snapshot.BotProfileId, out VanguardRaidOperatorRuntimeRecord runtime)
                    && runtime.BotOwner != null)
                {
                    foreignOperationProbe = DescribeForeignMedicalOperation(runtime.BotOwner, truth);
                }
            }
            catch
            {
                // Read-only diagnostic failure must never alter native medicine or recovery gates.
            }
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"{ForeignActivityTag} operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; recoveryAuthority=None; activeVanguardLease=false; watchdogAdmissionStateCreated=false; nativeMutation=false; cancellationRequested=false; ownershipViolation=false; resourceActivityObserved={Bool(resourceDropped)}; resourceDelta={delta:0.00}; {truth.Summary}; {foreignOperationProbe}; tag={StatusTag}");
            state.LastLogAtUtc = now;
        }

        state.LastRecoveryClass = truth.RecoveryClass.ToString();
        state.LastItemInstanceId = truth.ItemInstanceId;
        state.LastItemResource = truth.ItemResource;
        state.LastHandsType = truth.HandsType;
        state.LastObservedAtUtc = now;
    }

    // Exposes why a foreign typed MedsController cannot enter the terminal-stale observation
    // candidate path. No recovery action, native state write or new log emission is performed.
    // Read failures are reported as unknown rather than treated as terminal evidence.
    private static string DescribeForeignMedicalOperation(BotOwner botOwner, MedicalHandsTruth truth)
    {
        string snapshotGate = !truth.TypedMedsController || !truth.MedicalHands
            ? "typed_controller_unavailable"
            : truth.AnyMedicineUsing
                ? "native_medicine_still_using"
                : string.Equals(truth.ItemInstanceId, "none", StringComparison.OrdinalIgnoreCase)
                    ? "item_identity_unavailable"
                    : !truth.NativeSelectorReady && !IsExhaustedIdleMedsController(botOwner, truth)
                        ? "native_selector_unready_nonexhausted"
                        : "snapshot_eligible_stability_unproven";
        string handsType = "unknown";
        string handsIdentity = "unknown";
        string operationType = "unknown";
        string operationIdentity = "unknown";
        string operationState = "unknown";
        string selectorChanging = "unknown";
        string inventoryChanging = "unknown";
        string selectorErrorCount = "unknown";
        try
        {
            Player? player = botOwner.GetPlayer;
            object? hands = player?.HandsController;
            if (hands != null)
            {
                handsType = Safe(hands.GetType().Name);
                handsIdentity = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(hands)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                object? operation = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(
                    hands, "ObservedMedsControllerClass", "CurrentHandsOperation", "CurrentOperation");
                if (operation != null)
                {
                    operationType = Safe(operation.GetType().Name);
                    operationIdentity = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(operation)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
                    object? nativeState = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(operation, "State");
                    operationState = nativeState == null
                        ? "unknown"
                        : Safe(VanguardOperatorRuntimeAuditReflection.Text(nativeState));
                }
            }
        }
        catch
        {
            // Diagnostic-only: unreadable EFT controller never supplies terminality proof.
        }
        try
        {
            var selector = botOwner.WeaponManager?.Selector;
            if (selector != null)
            {
                selectorChanging = Bool(selector.IsChanging);
                selectorErrorCount = selector.ErrorCounter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch { }
        try
        {
            var inventory = botOwner.GetPlayer?.InventoryController;
            if (inventory != null)
                inventoryChanging = Bool(inventory.IsChangingWeapon);
        }
        catch { }
        return "foreignTerminalSnapshotGate=" + snapshotGate
            + ";foreignHandsType=" + handsType
            + ";foreignHandsIdentity=" + handsIdentity
            + ";foreignOperationType=" + operationType
            + ";foreignOperationIdentity=" + operationIdentity
            + ";foreignOperationState=" + operationState
            + ";foreignSelectorChanging=" + selectorChanging
            + ";foreignInventoryChanging=" + inventoryChanging
            + ";foreignSelectorErrors=" + selectorErrorCount
            + ";foreignProbeReadOnly=true";
    }

    private static void ClearForeignObservation(string botProfileId)
    {
        if (!string.IsNullOrWhiteSpace(botProfileId))
        {
            ForeignObservations.Remove(botProfileId);
        }
    }

    private static bool TryCancel(object? controller)
        => InvokeNoArg(controller, "CancelCurrent")
            || InvokeNoArg(controller, "Cancel")
            || InvokeNoArg(controller, "StopUse");

    private static bool InvokeNoArg(object? instance, string name)
    {
        try
        {
            if (instance == null) return false;
            var method = instance.GetType().GetMethod(
                name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            if (method == null) return false;
            object? result = method.Invoke(instance, Array.Empty<object>());
            return result is not bool value || value;
        }
        catch { return false; }
    }

    private static bool ReadBool(object? instance, string name)
    {
        try
        {
            object? value = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(instance, name);
            return value != null && Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { return false; }
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_').Replace('\t', '_');

    private static string Bool(bool value) => value ? "true" : "false";

    private sealed class State
    {
        public string LeaseId = "none";
        public string Need = "none";
        public string Outcome = "none";
        public string Reason = "none";
        public string ItemInstanceId = "none";
        public string ItemTemplateId = "none";
        public DateTimeOffset TerminalAtUtc = DateTimeOffset.MinValue;
        public MedicalRecoveryAuthority RecoveryAuthority = MedicalRecoveryAuthority.None;
        public bool TerminalCancellationRequested;
        public bool PositiveTerminalObservedSinceLeaseTerminal;
        public DateTimeOffset OrphanCandidateSinceUtc = DateTimeOffset.MinValue;
        public int OrphanSnapshots;
        public DateTimeOffset ReadySinceUtc = DateTimeOffset.MinValue;
        public int ReadySnapshots;
        public DateTimeOffset LastReadySnapshotAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastOrphanSnapshotAtUtc = DateTimeOffset.MinValue;
        public MedicalOrphanRecoveryClass CandidateRecoveryClass = MedicalOrphanRecoveryClass.None;
        public DateTimeOffset FirstPhysicalRecoveryRequestAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastPhysicalRecoveryRequestAtUtc = DateTimeOffset.MinValue;
        public int PhysicalRecoveryRequestCount;
        public DateTimeOffset LastLatchReconciliationAtUtc = DateTimeOffset.MinValue;
        public int LatchReconciliationCount;
        public int ClassifiedRecoveryRequestCount => PhysicalRecoveryRequestCount + LatchReconciliationCount;
        public bool RecoveryExhaustedLogged;
        public string LastObservedItemInstanceId = "none";
        public float LastObservedItemResource = -1f;
        public DateTimeOffset LastObservedResourceAtUtc = DateTimeOffset.MinValue;
        public int ResourceDropsOutsideLease;

        public void ResetObservationCandidates()
        {
            ResetOrphanCandidate();
            ResetReadyCandidate();
        }

        public void ResetOrphanCandidate()
        {
            OrphanCandidateSinceUtc = DateTimeOffset.MinValue;
            OrphanSnapshots = 0;
            LastOrphanSnapshotAtUtc = DateTimeOffset.MinValue;
            CandidateRecoveryClass = MedicalOrphanRecoveryClass.None;
        }

        public void ResetReadyCandidate()
        {
            ReadySinceUtc = DateTimeOffset.MinValue;
            ReadySnapshots = 0;
            LastReadySnapshotAtUtc = DateTimeOffset.MinValue;
        }
    }

    private sealed class ForeignObservationState
    {
        public string LastRecoveryClass = "none";
        public string LastItemInstanceId = "none";
        public float LastItemResource = -1f;
        public string LastHandsType = "none";
        public DateTimeOffset LastObservedAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastLogAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset StaleCandidateSinceUtc = DateTimeOffset.MinValue;
        public int StaleCandidateSnapshots;
        public string StaleCandidateItemInstanceId = "none";
        public float StaleCandidateItemResource = -1f;
        public DateTimeOffset LastStaleCandidateSnapshotAtUtc = DateTimeOffset.MinValue;
        public int ForeignStaleRecoveryRequestCount;
        public DateTimeOffset LastForeignStaleRecoveryRequestAtUtc = DateTimeOffset.MinValue;
        public Player.MedsController? FinishedMedsController;
        public Player.MedsController.ObservedMedsControllerClass? FinishedHandsOperation;
        public string FinishedHandsItemInstanceId = "none";
        public float FinishedHandsItemResource = -1f;
        public DateTimeOffset FinishedHandsCandidateSinceUtc = DateTimeOffset.MinValue;
        public DateTimeOffset FinishedHandsLastSnapshotAtUtc = DateTimeOffset.MinValue;
        public int FinishedHandsCandidateSnapshots;
        public int FinishedHandsRestoreRequests;
        public DateTimeOffset FinishedHandsLastRequestAtUtc = DateTimeOffset.MinValue;
        public bool FinishedHandsExhaustionLogged;
    }

    private readonly struct MedicalHandsTruth
    {
        public MedicalHandsTruth(
            bool botMedicineUsing,
            bool firstAidUsing,
            bool surgeryUsing,
            bool stimulantUsing,
            bool medicalHands,
            bool typedMedsController,
            bool weaponReady,
            bool nativeSelectorReady,
            string handsType,
            string itemSource,
            string itemInstanceId,
            string itemTemplateId,
            string itemName,
            float itemResource,
            float itemMaxResource)
        {
            BotMedicineUsing = botMedicineUsing;
            FirstAidUsing = firstAidUsing;
            SurgeryUsing = surgeryUsing;
            StimulantUsing = stimulantUsing;
            MedicalHands = medicalHands;
            TypedMedsController = typedMedsController;
            WeaponReady = weaponReady;
            NativeSelectorReady = nativeSelectorReady;
            HandsType = handsType;
            ItemSource = itemSource;
            ItemInstanceId = itemInstanceId;
            ItemTemplateId = itemTemplateId;
            ItemName = itemName;
            ItemResource = itemResource;
            ItemMaxResource = itemMaxResource;
        }

        public bool BotMedicineUsing { get; }
        public bool FirstAidUsing { get; }
        public bool SurgeryUsing { get; }
        public bool StimulantUsing { get; }
        public bool MedicalHands { get; }
        public bool TypedMedsController { get; }
        public bool WeaponReady { get; }
        public bool NativeSelectorReady { get; }
        public string HandsType { get; }
        public string ItemSource { get; }
        public string ItemInstanceId { get; }
        public string ItemTemplateId { get; }
        public string ItemName { get; }
        public float ItemResource { get; }
        public float ItemMaxResource { get; }
        public bool AnyChildControllerUsing => FirstAidUsing || SurgeryUsing || StimulantUsing;
        public bool AnyMedicineUsing => BotMedicineUsing || AnyChildControllerUsing;
        public bool PhysicalMedicalControllerActive => AnyChildControllerUsing || MedicalHands;
        public bool ParentUsingLatch => BotMedicineUsing && !AnyChildControllerUsing && !MedicalHands && WeaponReady;
        public MedicalOrphanRecoveryClass RecoveryClass => PhysicalMedicalControllerActive
            ? MedicalOrphanRecoveryClass.PhysicalMedicalController
            : ParentUsingLatch
                ? MedicalOrphanRecoveryClass.ParentUsingLatch
                : BotMedicineUsing
                    ? MedicalOrphanRecoveryClass.ParentUsingTransition
                    : MedicalOrphanRecoveryClass.None;
        public bool OrphanMedicalController => AnyMedicineUsing || MedicalHands;
        public bool PositiveTerminalTruth => !AnyMedicineUsing && !MedicalHands && WeaponReady;
        public string Summary => "recoveryClass=" + RecoveryClass
            + ";botMedicineUsing=" + Bool(BotMedicineUsing)
            + ";firstAidUsing=" + Bool(FirstAidUsing)
            + ";surgeryUsing=" + Bool(SurgeryUsing)
            + ";stimulantUsing=" + Bool(StimulantUsing)
            + ";medicalHands=" + Bool(MedicalHands)
            + ";typedMedsController=" + Bool(TypedMedsController)
            + ";weaponReady=" + Bool(WeaponReady)
            + ";nativeSelectorReady=" + Bool(NativeSelectorReady)
            + ";hands=" + Safe(HandsType)
            + ";itemSource=" + Safe(ItemSource)
            + ";itemInstance=" + Safe(ItemInstanceId)
            + ";itemTpl=" + Safe(ItemTemplateId)
            + ";item=" + Safe(ItemName)
            + ";itemResource=" + ItemResource.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            + "/" + ItemMaxResource.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }

    private enum MedicalRecoveryAuthority
    {
        None = 0,
        VanguardLeaseTerminal = 1,
    }

    private enum MedicalOrphanRecoveryClass
    {
        None = 0,
        PhysicalMedicalController = 1,
        ParentUsingLatch = 2,
        ParentUsingTransition = 3,
    }

    private readonly struct PhysicalRecoveryRequest
    {
        public PhysicalRecoveryRequest(
            bool medEffectRemoved,
            bool healthCancel,
            bool medsQueueCleared,
            bool fastForwardCurrentState,
            bool firstAidCancel,
            bool surgeryCancel,
            bool stimulantCancel,
            bool takePrevWeapon,
            string handsType,
            int requestCount,
            string reason)
        {
            MedEffectRemoved = medEffectRemoved;
            HealthCancel = healthCancel;
            MedsQueueCleared = medsQueueCleared;
            FastForwardCurrentState = fastForwardCurrentState;
            FirstAidCancel = firstAidCancel;
            SurgeryCancel = surgeryCancel;
            StimulantCancel = stimulantCancel;
            TakePrevWeapon = takePrevWeapon;
            HandsType = handsType;
            RequestCount = requestCount;
            Reason = reason;
        }

        public bool MedEffectRemoved { get; }
        public bool HealthCancel { get; }
        public bool MedsQueueCleared { get; }
        public bool FastForwardCurrentState { get; }
        public bool FirstAidCancel { get; }
        public bool SurgeryCancel { get; }
        public bool StimulantCancel { get; }
        public bool TakePrevWeapon { get; }
        public string HandsType { get; }
        public int RequestCount { get; }
        public string Reason { get; }
        public string Summary => "requestCount=" + RequestCount
            + ";activeMedEffectRemoved=" + Bool(MedEffectRemoved)
            + ";healthControllerCancelApplyingItem=" + Bool(HealthCancel)
            + ";typedMedsQueueClear=" + Bool(MedsQueueCleared)
            + ";fastForwardCurrentState=" + Bool(FastForwardCurrentState)
            + ";typedMedsRemoveRequested=false"
            + ";firstAidCancel=" + Bool(FirstAidCancel)
            + ";surgeryCancel=" + Bool(SurgeryCancel)
            + ";stimulantCancel=" + Bool(StimulantCancel)
            + ";takePrevWeapon=" + Bool(TakePrevWeapon)
            + ";hands=" + Safe(HandsType)
            + ";rawControllerFieldClear=false"
            + ";recoveryConfirmed=false"
            + ";reason=" + Safe(Reason);
    }

    private readonly struct ParentLatchReconciliation
    {
        public ParentLatchReconciliation(bool callbackInvoked, int requestCount, string reason)
        {
            CallbackInvoked = callbackInvoked;
            RequestCount = requestCount;
            Reason = reason;
        }

        public bool CallbackInvoked { get; }
        public int RequestCount { get; }
        public string Reason { get; }
        public string Summary => "requestCount=" + RequestCount
            + ";nativeAggregateCallbackInvoked=" + Bool(CallbackInvoked)
            + ";parentUsingRequested=false"
            + ";childControllerMutation=false"
            + ";medicalHandsMutation=false"
            + ";rawControllerFieldClear=false"
            + ";recoveryConfirmed=false"
            + ";reason=" + Safe(Reason);
    }

}
#endif
