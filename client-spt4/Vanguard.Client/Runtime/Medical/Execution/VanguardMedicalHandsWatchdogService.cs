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
// Authority boundary: EFT owns native hands and medical controllers; Vanguard may recover only state proven to originate from its own completed lease and merely observes foreign EFT/SAIN medical activity.
// Invariant: Unproven controller state is never cancelled or claimed, and a new Vanguard medical lease cannot start until terminal truth is positively stable.
namespace Vanguard.Client.Runtime.Medical.Execution;

/// <summary>
/// Owns the post-terminal medical-hands boundary.
///
/// The runtime enforces provenance before post-terminal medical recovery can mutate native EFT state.
///
/// Only a state created from a real Vanguard lease terminal owns recovery authority. Native EFT/SAIN medical
/// activity discovered without that provenance is foreign activity: Vanguard may observe and defer its own
/// admission, but it never cancels, clears or claims that controller. A real Vanguard post-terminal physical
/// controller may be recovered only while item continuity is proven and no positive terminal truth has been
/// observed in between. A parent-only BotMedecine.Using latch remains a separate, narrowly reconciled native state.
/// Neither mutation proves recovery. Admission remains blocked for Vanguard-owned post-terminal state until
/// positive terminal truth is stable: parent and children idle, non-medical hands, weapon ready.
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
    private const int RequiredSnapshots = 3;
    private const int MaximumPhysicalRecoveryRequests = 2;
    private const int MaximumLatchReconciliations = 1;
    private const float ResourceDeltaEpsilon = 0.01f;
    private static readonly Dictionary<string, State> States = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ForeignObservationState> ForeignObservations = new(StringComparer.OrdinalIgnoreCase);

    public static void Reset(string reason)
    {
        States.Clear();
        ForeignObservations.Clear();
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_MEDICAL_HANDS_WATCHDOG_RESET reason={Safe(reason)}; postTerminalOnly=true; recoveryAuthority=real_vanguard_lease_terminal_only; foreignNativeActivity=observe_only; sameItemContinuityRequired=true; positiveTerminalBreaksRecoveryAuthority=true; parentLatchReconciliation=true; nativeTerminalTruthRequired=true; rawControllerFieldClearForbidden=true; productiveLeaseNeverCancelled=true; tag={StatusTag}; Tag={PostTerminalStatusTag}; Tag={MedicalHandsWatchdogStatusTag}; Tag={MedicalLeaseStatusTag}");
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
            if (boundaryTruth.PositiveTerminalTruth)
            {
                state.PositiveTerminalObservedSinceLeaseTerminal = true;
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"VANGUARD_MEDICAL_POSITIVE_TERMINAL_OBSERVED_AT_LEASE_BOUNDARY botProfile={Safe(lease.BotProfileId)}; lease={Safe(lease.LeaseId)}; recoveryAuthority={state.RecoveryAuthority}; futureNativeMutationByThisLease=false; {boundaryTruth.Summary}; tag={StatusTag}");
            }
        }
    }

    /// <summary>
    /// Observes a native medical controller that is busy while no Vanguard medical lease owns it.
    /// The runtime deliberately does not create a recovery-authorized watchdog state here: the activity may
    /// legitimately belong to EFT or SAIN. The caller may defer a Vanguard admission while the native
    /// owner finishes, but this observation can never authorize CancelCurrent/ClearQueue/med-effect mutation.
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
                    ObserveForeignMedicalActivity(
                        snapshot,
                        foreignTruth,
                        now,
                        "proactive_native_medical_activity_without_vanguard_lease");
                }
                else
                {
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

                VanguardClientDiagnosticsLog.Info(MedicalHandsWatchdogStatusTag,
                    $"VANGUARD_MEDICAL_HANDS_TERMINAL_TRUTH_CONFIRMED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(state.LeaseId)}; readySnapshots={state.ReadySnapshots}; stableSeconds={(now - state.ReadySinceUtc).TotalSeconds:0.00}; physicalRecoveryRequests={state.PhysicalRecoveryRequestCount}; latchReconciliations={state.LatchReconciliationCount}; resourceDropsOutsideLease={state.ResourceDropsOutsideLease}; refreshAfterTerminal={Bool(refreshed)}; admissionReleased=true; successProof=medicine_idle_and_nonmedical_weapon_hands; {truth.Summary}; tag={MedicalHandsWatchdogStatusTag}; Tag={MedicalLeaseStatusTag}; legacyTag={StatusTag}");
                States.Remove(snapshot.BotProfileId);
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
                if (!HasOwnedPhysicalContinuity(state, truth, out string ownershipReason))
                {
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
        bool firstAidCancel = false;
        bool surgeryCancel = false;
        bool stimulantCancel = false;
        bool takePrevWeapon = false;
        string handsType = "none";

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

        return new PhysicalRecoveryRequest(
            medEffectRemoved,
            healthCancel,
            medsQueueCleared,
            firstAidCancel,
            surgeryCancel,
            stimulantCancel,
            takePrevWeapon,
            handsType,
            state.PhysicalRecoveryRequestCount,
            reason);
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
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"{ForeignActivityTag} operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; recoveryAuthority=None; activeVanguardLease=false; watchdogAdmissionStateCreated=false; nativeMutation=false; cancellationRequested=false; ownershipViolation=false; resourceActivityObserved={Bool(resourceDropped)}; resourceDelta={delta:0.00}; {truth.Summary}; tag={StatusTag}");
            state.LastLogAtUtc = now;
        }

        state.LastRecoveryClass = truth.RecoveryClass.ToString();
        state.LastItemInstanceId = truth.ItemInstanceId;
        state.LastItemResource = truth.ItemResource;
        state.LastHandsType = truth.HandsType;
        state.LastObservedAtUtc = now;
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
