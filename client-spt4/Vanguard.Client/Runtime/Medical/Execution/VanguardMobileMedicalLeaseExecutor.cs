#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.External;
using Vanguard.Client.Runtime.Intents;
using Vanguard.Client.Runtime.PostLoot;
using Vanguard.Client.Runtime.Medical;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.Grenades;

// Responsibility: executes the already-authorized mobile medical plan while reconciling item capability, hands state and movement safety.
// Flow: The scheduler-granted medical lease is matched to the current canonical need and a usable item/body part, the EFT hands action is started, then progress, safety and post-use medical truth are watched until success, retry or safe cancellation.
// Authority boundary: this executor consumes medical authority; it does not create medical need or override combat/grenade safety.
// Invariant: terminal outcomes must be tied to canonical medical truth and every temporary lease/attempt is raid-scoped and recoverable.

namespace Vanguard.Client.Runtime.Medical.Execution;

internal static class VanguardMobileMedicalLeaseExecutor
{
    public const string CanonicalPriorityPreemptionStatusTag = "VANGUARD_MEDICAL_CANONICAL_PRIORITY_PREEMPTION_STATUS";
    public const string CanonicalMedicalConvergenceStatusTag = "VANGUARD_CANONICAL_MEDICAL_CONVERGENCE_STATUS";
    public const string StatusTag = "VANGUARD_MOBILE_MEDICAL_LEASE_STATUS";
    public const string CompletionRecheckStatusTag = "VANGUARD_MOBILE_MEDICAL_COMPLETION_RECHECK_OK";
    public const string ActiveMedicalHpFractureStatusTag = "VANGUARD_ACTIVE_MEDICAL_HP_FRACTURE_OK";
    public const string MedicalEffectGuardStatusTag = "VANGUARD_MEDICAL_EFFECT_GUARD_OK";
    public const string ActiveSurgeryStatusTag = "VANGUARD_ACTIVE_SURGERY_OK";
    public const string SainLikeSurgerySafetyStatusTag = "VANGUARD_SAIN_LIKE_SURGERY_SAFETY_OK";
    public const string SurgeryCoverCompletionGuardStatusTag = "VANGUARD_SURGERY_COVER_COMPLETION_GUARD_OK";
    public const string SurgeryCoverPrepareStatusTag = VanguardSurgeryCoverPrepareExecutor.StatusTag;
    public const string PostOrbitInventoryRecoveryStatusTag = VanguardPostOrbitInventoryRecoveryService.StatusTag;
    public const string InventoryRefreshStatusTag = VanguardPostOrbitInventoryRecoveryService.InventoryRefreshStatusTag;
    private const string MedicalAuthorityHoldStatusTag = Vanguard.Client.Runtime.External.VanguardExternalAuthorityAdapter.MedicalAuthorityHoldStatusTag;
    private const string MedicalCoverCommitStatusTag = Vanguard.Client.Runtime.External.VanguardExternalAuthorityAdapter.MedicalCoverCommitStatusTag;
    private const string MedicalHardProcedureAuthorityStatusTag = Vanguard.Client.Runtime.External.VanguardExternalAuthorityAdapter.MedicalHardProcedureAuthorityStatusTag;
    private const string MedicalProcedureCompletionGateStatusTag = Vanguard.Client.Runtime.External.VanguardExternalAuthorityAdapter.MedicalProcedureCompletionGateStatusTag;
    private const string MedicalSurgeryDirectChainStatusTag = Vanguard.Client.Runtime.External.VanguardExternalAuthorityAdapter.MedicalSurgeryDirectChainStatusTag;
    private const string MedicalSurgerySameProcedureStartStatusTag = Vanguard.Client.Runtime.External.VanguardExternalAuthorityAdapter.MedicalSurgerySameLeaseStartStatusTag;
    public const string MedicalSurgeryPersistenceStatusTag = "VANGUARD_MEDICAL_SURGERY_PERSISTENCE_OK";
    public const string MedicalHandsSettleStatusTag = "VANGUARD_MEDICAL_HANDS_SETTLE_STATUS";
    public const string MedicalPostureRetryStatusTag = "VANGUARD_MEDICAL_POSTURE_RETRY_OK";
    public const string MedicalSurgeryHardHoldStatusTag = "VANGUARD_MEDICAL_SURGERY_HARD_HOLD_OK";
    public const string MedicalOrbitLootFreezeDuringSurgeryStatusTag = "VANGUARD_MEDICAL_ORBIT_LOOT_FREEZE_DURING_SURGERY_OK";
    public const string MedicalHardLockAbortGateStatusTag = "VANGUARD_MEDICAL_HARD_LOCK_ABORT_GATE_OK";
    public const string MedicalSequentialSurgeryChainStatusTag = "VANGUARD_MEDICAL_SEQUENTIAL_SURGERY_CHAIN_OK";
    public const string MedicalSurgeryDebtRetryStatusTag = VanguardSurgeryDebtService.StatusTag;
    public const string MedicalEffectCircuitBreakerStatusTag = VanguardExecutionLeaseStore.MedicalEffectCircuitBreakerStatusTag;
    public const string MedicalTerminalTruthStatusTag = VanguardMedicalTerminalTruthReader.StatusTag;
    public const string MedicalThreatCancellationStatusTag = "VANGUARD_MEDICAL_THREAT_CANCELLATION_STATUS";
    public const string MedicalSurgeryDeterministicCompletionStatusTag = "VANGUARD_SURGERY_DETERMINISTIC_COMPLETION_STATUS";
    public const string NativeMedicalCommitAndSnapshotBudgetStatusTag = "VANGUARD_NATIVE_MEDICAL_COMMIT_AND_SNAPSHOT_BUDGET_STATUS";
    public const string MedicalNativeCommitStatusTag = "VANGUARD_NATIVE_MEDICAL_COMMIT_STATUS";
    public const string NativeMedicalCommitReconciliationStatusTag = "VANGUARD_NATIVE_MEDICAL_COMMIT_RECONCILIATION_STATUS";
    public const string SurgeryTerminalItemCommitStatusTag = "VANGUARD_SURGERY_TERMINAL_ITEM_COMMIT_STATUS";
    public const string MedicalHandsReturnTruthStatusTag = "VANGUARD_MEDICAL_HANDS_RETURN_TRUTH_STATUS";

    private static readonly TimeSpan MobileMinDuration = TimeSpan.FromSeconds(1.25d);
    private static readonly TimeSpan MobileMaxDuration = TimeSpan.FromSeconds(9.50d);
    private static readonly TimeSpan MobileNoProgressTimeout = TimeSpan.FromSeconds(2.75d);
    private static readonly TimeSpan StationaryFractureMinDuration = TimeSpan.FromSeconds(1.50d);
    private static readonly TimeSpan StationaryFractureMaxDuration = TimeSpan.FromSeconds(13.50d);
    private static readonly TimeSpan StationaryFractureGrizzlyMaxDuration = TimeSpan.FromSeconds(15.50d);
    private static readonly TimeSpan StationaryFractureNoProgressTimeout = TimeSpan.FromSeconds(4.50d);
    private static readonly TimeSpan StationarySurgeryMinDuration = TimeSpan.FromSeconds(3.00d);
    private static readonly TimeSpan StationarySurgeryCmsMaxDuration = TimeSpan.FromSeconds(45.00d);
    private static readonly TimeSpan StationarySurgerySurv12MaxDuration = TimeSpan.FromSeconds(60.00d);
    private static readonly TimeSpan StationarySurgeryNoProgressTimeout = TimeSpan.FromSeconds(45.00d);
    private static readonly TimeSpan MobilePostUseRecheckWindow = TimeSpan.FromSeconds(2.25d);
    private static readonly TimeSpan HpHealPostUseRecheckWindow = TimeSpan.FromSeconds(2.75d);
    private static readonly TimeSpan StationaryFracturePostUseRecheckWindow = TimeSpan.FromSeconds(5.50d);
    private static readonly TimeSpan StationaryFractureGrizzlyPostUseRecheckWindow = TimeSpan.FromSeconds(7.00d);
    private static readonly TimeSpan StationarySurgeryCmsPostUseRecheckWindow = TimeSpan.FromSeconds(18.00d);
    private static readonly TimeSpan StationarySurgerySurv12PostUseRecheckWindow = TimeSpan.FromSeconds(24.00d);
    private static readonly TimeSpan UsingHeartbeatInterval = TimeSpan.FromSeconds(3.00d);
    private static readonly TimeSpan EffectResolvedHandsStableWindow = TimeSpan.FromSeconds(0.85d);
    private static readonly TimeSpan EffectResolvedHandsRecoveryDelay = TimeSpan.FromSeconds(3.00d);
    private static readonly TimeSpan EffectResolvedHandsAbsoluteWindow = TimeSpan.FromSeconds(6.00d);
    private static readonly TimeSpan EffectResolvedHandsHandoffCooldown = TimeSpan.FromSeconds(3.50d);
    private static readonly TimeSpan EffectResolvedHandsBoundedCooldown = TimeSpan.FromSeconds(6.00d);
    private const int EffectResolvedHandsRequiredSnapshots = 2;
    private static readonly TimeSpan StationarySurgeryInterruptedRetryCooldown = TimeSpan.FromSeconds(1.50d);
    private static readonly TimeSpan SurgeryDebtRetryCooldown = TimeSpan.FromSeconds(2.50d);
    private static readonly TimeSpan SuccessCooldown = TimeSpan.FromSeconds(1.00d);
    private static readonly TimeSpan PartialSuccessCooldown = TimeSpan.FromSeconds(2.50d);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(3.00d);
    private static readonly TimeSpan NoEffectCooldown = TimeSpan.FromSeconds(6.00d);
    private static readonly TimeSpan RejectedCooldown = TimeSpan.FromSeconds(2.50d);
    private static readonly TimeSpan RecheckLogInterval = TimeSpan.FromSeconds(5.00d);
    private static readonly TimeSpan UrgentControllerBusyRecoveryDelay = TimeSpan.FromSeconds(1.25d);
    private static readonly TimeSpan MobileControllerBusyRecoveryDelay = TimeSpan.FromSeconds(2.50d);
    private static readonly TimeSpan StationaryControllerBusyRecoveryDelay = TimeSpan.FromSeconds(6.00d);
    private static readonly TimeSpan MobileControllerUsingGrace = TimeSpan.FromSeconds(3.50d);
    private static readonly TimeSpan SurgeryControllerUsingGrace = TimeSpan.FromSeconds(6.00d);
    private static readonly TimeSpan SurgeryNativeHandsCommitTimeout = TimeSpan.FromSeconds(6.00d);
    private static readonly TimeSpan SurgeryCmsNativeStartStallTimeout = TimeSpan.FromSeconds(24.00d);
    private static readonly TimeSpan SurgerySurv12NativeStartStallTimeout = TimeSpan.FromSeconds(32.00d);
    private static readonly TimeSpan FirstAidNativeStartStallTimeout = TimeSpan.FromSeconds(8.50d);
    private static readonly TimeSpan NativeStartRetryHandsDrainTimeout = TimeSpan.FromSeconds(8.00d);
    private static readonly TimeSpan SurgeryNativeHandsMismatchStableWindow = TimeSpan.FromSeconds(0.50d);
    private const int SurgeryNativeHandsMismatchRequiredSnapshots = 2;
    private const int MaxInternalSurgeryStartRetries = 2;
    private static readonly TimeSpan SurgeryNativeCancelDrainTimeout = TimeSpan.FromSeconds(8.00d);
    private static readonly TimeSpan SurgeryConfirmedTerminalSettleWindow = TimeSpan.FromSeconds(6.00d);
    private static readonly TimeSpan SurgeryResourceNoEffectCmsTimeout = TimeSpan.FromSeconds(3.00d);
    private static readonly TimeSpan SurgeryResourceNoEffectSurv12Timeout = TimeSpan.FromSeconds(4.00d);
    private static readonly TimeSpan SurgeryTerminalItemAbsenceStableWindow = TimeSpan.FromSeconds(0.35d);
    private const int SurgeryTerminalItemAbsenceRequiredSnapshots = 2;
    private const float SurgeryTerminalItemLastChargeMaximum = 1.01f;
    private static readonly TimeSpan FirstAidNativeCancelDrainTimeout = TimeSpan.FromSeconds(8.00d);
    private static readonly TimeSpan NativeCancelHandsStableWindow = TimeSpan.FromSeconds(0.85d);
    private static readonly TimeSpan NativeCancelHandsRecoveryDelay = TimeSpan.FromSeconds(3.00d);
    private const int NativeCancelHandsRequiredSnapshots = 2;
    private static readonly TimeSpan FirstAidThreatRetryCooldown = TimeSpan.FromSeconds(1.50d);
    private static readonly TimeSpan SurgeryMovementViolationGrace = TimeSpan.FromSeconds(2.00d);
    private static readonly TimeSpan SurgeryMovementSampleGapLimit = TimeSpan.FromSeconds(1.25d);
    private const float SurgeryStationaryDriftLimitMeters = 1.25f;
    private const float SurgeryReliableSampleMovementMeters = 0.20f;
    private static readonly TimeSpan NoEffectConfirmationDelay = TimeSpan.FromSeconds(1.50d);
    private static readonly TimeSpan MobilePostUseRecheckCadence = TimeSpan.FromSeconds(0.50d);
    private static readonly TimeSpan StationaryPostUseRecheckCadence = TimeSpan.FromSeconds(1.50d);
    private const int MaxPostUseRecheckSnapshots = 12;
    private const string ControllerRecoveryStatusTag = "VANGUARD_MEDICAL_CONTROLLER_RECOVERY_OK";
    private static readonly Dictionary<string, DateTimeOffset> LastRecheckLogAtByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> ControllerBusySinceByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, OperatorDecisionSnapshot> SnapshotsByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<VanguardExecutionLeaseState> ActiveLeaseSnapshot = new();
    private static bool bootLogged;

    public static void ResetForRaidLifecycle(string reason)
    {
        bootLogged = false;
        LastRecheckLogAtByKey.Clear();
        ControllerBusySinceByBot.Clear();
        SnapshotsByBotProfileId.Clear();
        ActiveLeaseSnapshot.Clear();
        VanguardSurgeryCoverPrepareExecutor.Reset(reason);
        VanguardMedicalIsolationController.Reset(reason);
        VanguardMedicalCommitReadinessGate.Reset(reason);
        VanguardPostOrbitInventoryRecoveryService.Reset(reason);
        VanguardSurgeryDebtService.Reset(reason);
        VanguardMedicalHandsWatchdogService.Reset(reason);
        VanguardMedicalNativeAttemptMemory.Reset(reason);
        VanguardMedicalExecutionResultBridge.Reset();
        VanguardExecutionLeaseStore.Reset(reason);
        VanguardClientDiagnosticsLog.Info(StatusTag, $"first active medical lease reset reason={reason}; scope=heavy_light_bleed_hp_heal_stationary_fracture_stationary_surgery_prepare_surgery_cover_medical_isolation_post_orbit_inventory_recovery; active=true; completionRecheck=true; effectGuard=true; activeSurgery=true; prepareSurgeryCover=true; medicalIsolation=true; postOrbitInventoryRecovery=true; controllerBusyRecovery=true; surgeryDebtRetry=true; singleSurgeryApply=true; noEffectCooldown={NoEffectCooldown.TotalSeconds:0.00}; surgeryDebtRetryCooldown={SurgeryDebtRetryCooldown.TotalSeconds:0.00}; tag={ActiveMedicalHpFractureStatusTag}; effectTag={MedicalEffectGuardStatusTag}; surgeryTag={ActiveSurgeryStatusTag}; surgerySafetyTag={SainLikeSurgerySafetyStatusTag}; Tag={SurgeryCoverCompletionGuardStatusTag}; Tag={SurgeryCoverPrepareStatusTag}; Tag={PostOrbitInventoryRecoveryStatusTag}; Tag={VanguardSurgeryCoverPrepareExecutor.StatusTag}; Tag={VanguardMedicalIsolationController.StatusTag}; controllerRecoveryTag={ControllerRecoveryStatusTag}; handsSettleTag={MedicalHandsSettleStatusTag}; inventoryRefreshTag={InventoryRefreshStatusTag}; authorityHoldTag={MedicalAuthorityHoldStatusTag}; coverCommitTag={MedicalCoverCommitStatusTag}; surgeryDebtTag={MedicalSurgeryDebtRetryStatusTag}");
    }

    public static void Tick()
    {
        if (!VanguardOperatorRuntimeAuditLoadGuard.IsOpen())
        {
            return;
        }

        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }

        if (!VanguardOperatorRuntimeAuditOptions.GetFirstActiveMobileMedicalLeaseEnabled())
        {
            return;
        }

        LogBootOnce();
        long snapshotStarted = VanguardRuntimePerformanceGuard.Begin();
        var snapshots = VanguardOperatorDecisionSnapshotService.GetLatestSnapshots();
        SnapshotsByBotProfileId.Clear();
        foreach (var snapshot in snapshots)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            {
                SnapshotsByBotProfileId[snapshot.BotProfileId] = snapshot;
            }
        }
        VanguardRuntimePerformanceGuard.End("MedicalSnapshotIndex", snapshotStarted);

        long recoveryStarted = VanguardRuntimePerformanceGuard.Begin();
        VanguardPostOrbitInventoryRecoveryService.Tick(snapshots);
        VanguardRuntimePerformanceGuard.End("MedicalPostOrbitRecovery", recoveryStarted);

        long debtStarted = VanguardRuntimePerformanceGuard.Begin();
        DateTimeOffset medicalObservationNow = DateTimeOffset.UtcNow;
        VanguardSurgeryDebtService.UpdateFromSnapshots(snapshots, medicalObservationNow);
        VanguardMedicalNativeAttemptMemory.ObserveSnapshots(snapshots, medicalObservationNow);
        VanguardRuntimePerformanceGuard.End("MedicalSurgeryDebtUpdate", debtStarted);

        long activeStarted = VanguardRuntimePerformanceGuard.Begin();
        UpdateActiveLeases(SnapshotsByBotProfileId);
        VanguardMedicalHandsWatchdogService.Tick(snapshots, DateTimeOffset.UtcNow);
        VanguardRuntimePerformanceGuard.End("MedicalActiveLeaseUpdate", activeStarted);

        long admissionStarted = VanguardRuntimePerformanceGuard.Begin();
        TryStartNewLeases(snapshots);
        VanguardRuntimePerformanceGuard.End("MedicalLeaseAdmission", admissionStarted);
    }

    private static void LogBootOnce()
    {
        if (bootLogged)
        {
            return;
        }

        bootLogged = true;
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_MOBILE_MEDICAL_LEASE_BOOT enabled=true; scope=heavy_light_bleed_hp_heal_stationary_fracture_stationary_surgery_prepare_surgery_cover; CoveredSuppressionWindow=true; CompletionRecheck=true; HpHeal=true; StationaryFracture=true; EffectGuard=true; ActiveSurgery=true; SainLikeSurgerySafety=true; SurgeryCoverCompletionGuard=true; MedicalPrepareSurgeryCover=true; PrepareResidualSurgeryWindow=true; PostOrbitInventoryRecovery=true; SainLikeSurgeryCoverSeek=true; MedicalIsolation=true; InventoryRefresh=true; OrbitLocalHoldLock=true; doctrine=full_life_when_safe_no_retry_without_effect_strict_surgery_area_clear_cover_or_hold_required_terminal_completion_before_max_patient_only_surgery_cover_seek_then_stationary_surgery_post_orbit_inventory_recheck_before_medical_bounded_safe_combat_mobile_opportunities_no_movement_driver_targeted_short_rechecks; items=bleed_first_aid_plus_ai2_car_salewa_ifak_afak_grizzly_plus_splint_alusplint_grizzly_plus_cms_surv12; mutatesMedical=true; mutatesMovement=patient_only_sain_like_cover_seek_or_stationary_stop; mutatesSain=cover_update_only_no_decision_force; mutatesLoot=patient_only_cancel_loot_before_critical_surgery; mobileMaxWindow={MobileMaxDuration.TotalSeconds:0.00}; stationaryFractureMaxWindow={StationaryFractureMaxDuration.TotalSeconds:0.00}; surgeryCmsMaxWindow={StationarySurgeryCmsMaxDuration.TotalSeconds:0.00}; surgerySurv12MaxWindow={StationarySurgerySurv12MaxDuration.TotalSeconds:0.00}; mobileNoProgress={MobileNoProgressTimeout.TotalSeconds:0.00}; stationaryNoProgress={StationaryFractureNoProgressTimeout.TotalSeconds:0.00}; postUseMobile={MobilePostUseRecheckWindow.TotalSeconds:0.00}; postUseHp={HpHealPostUseRecheckWindow.TotalSeconds:0.00}; postUseFracture={StationaryFracturePostUseRecheckWindow.TotalSeconds:0.00}; postUseCms={StationarySurgeryCmsPostUseRecheckWindow.TotalSeconds:0.00}; postUseSurv12={StationarySurgerySurv12PostUseRecheckWindow.TotalSeconds:0.00}; noEffectCooldown={NoEffectCooldown.TotalSeconds:0.00}; rejectedCooldown={RejectedCooldown.TotalSeconds:0.00}; controllerUsingGrace={MobileControllerUsingGrace.TotalSeconds:0.00}; build={VanguardBuildVersion.BuildLabel}; tag={CompletionRecheckStatusTag}; activeMedicalTag={ActiveMedicalHpFractureStatusTag}; effectTag={MedicalEffectGuardStatusTag}; surgeryTag={ActiveSurgeryStatusTag}; surgerySafetyTag={SainLikeSurgerySafetyStatusTag}; Tag={SurgeryCoverPrepareStatusTag}; Tag={PostOrbitInventoryRecoveryStatusTag}; Tag={VanguardSurgeryCoverPrepareExecutor.StatusTag}; Tag={VanguardMedicalIsolationController.StatusTag}; inventoryRefreshTag={InventoryRefreshStatusTag}; orbitLockTag={VanguardSurgeryCoverPrepareExecutor.OrbitLocalHoldLockStatusTag}; hardProcedureTag={MedicalHardProcedureAuthorityStatusTag}; completionGateTag={MedicalProcedureCompletionGateStatusTag}; directChainTag={MedicalSurgeryDirectChainStatusTag}; sameProcedureStartTag={MedicalSurgerySameProcedureStartStatusTag}; surgeryPersistenceTag={MedicalSurgeryPersistenceStatusTag}; postureRetryTag={MedicalPostureRetryStatusTag}; surgeryHardHoldTag={MedicalSurgeryHardHoldStatusTag}; orbitLootFreezeTag={MedicalOrbitLootFreezeDuringSurgeryStatusTag}; surgeryDebtTag={MedicalSurgeryDebtRetryStatusTag}");
    }

    private static VanguardIntentDryRunBoard BuildForcedSurgeryDebtBoard(OperatorDecisionSnapshot snapshot, VanguardIntentDryRunBoard currentBoard, string reason)
    {
        var candidates = new List<VanguardIntentCandidate>();
        if (currentBoard?.Candidates != null)
        {
            candidates.AddRange(currentBoard.Candidates);
        }

        var forced = new VanguardIntentCandidate
        {
            IntentKey = VanguardSurgeryCoverPrepareExecutor.IntentKey,
            Domain = "Medical",
            Valid = true,
            Reason = "persistent_surgery_debt_force_prepare:" + Safe(reason),
            BaseScore = 260f,
            FinalScore = 260f,
            Gate = "forced_by_surgery_debt",
            TargetKey = Safe(snapshot.Medical.Actionability.TargetPart),
            PlanKey = Safe(snapshot.Medical.Plan.PlanKey),
            NextStep = VanguardSurgeryCoverPrepareExecutor.IntentKey
        };
        candidates.Add(forced);
        return new VanguardIntentDryRunBoard(snapshot, candidates);
    }

    private static void UpdateActiveLeases(Dictionary<string, OperatorDecisionSnapshot> snapshotsByBotProfile)
    {
        var now = DateTimeOffset.UtcNow;
        VanguardExecutionLeaseStore.CopyActiveLeasesTo(ActiveLeaseSnapshot);
        foreach (var lease in ActiveLeaseSnapshot)
        {
            if (!snapshotsByBotProfile.TryGetValue(lease.BotProfileId, out var snapshot))
            {
                if (now >= lease.MaxUntilUtc)
                {
                    CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Timeout, "MaxWindowExpiredNoSnapshot", FailureCooldown);
                }
                continue;
            }

            VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(lease.BotProfileId, out var record);
            if (VanguardSurgeryCoverPrepareExecutor.IsPrepareLease(lease))
            {
                VanguardSurgeryCoverPrepareExecutor.Update(lease, record?.BotOwner, snapshot, now);
                continue;
            }

            bool waitingForPostUseCadence = !lease.EffectResolvedAwaitingHandsRelease
                && lease.ItemUseObserved
                && lease.FirstAidEndedObserved
                && lease.NextPostUseRecheckAtUtc != DateTimeOffset.MinValue
                && now < lease.NextPostUseRecheckAtUtc
                && now < lease.MaxUntilUtc;
            bool immediatePostUseInterruptSignal = VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot)
                || snapshot.Medical.Safety.ImmediateCombatBlock
                || !snapshot.Alive;
            if (waitingForPostUseCadence && !immediatePostUseInterruptSignal)
            {
                // Do not rebuild the expensive medical progress snapshot every frame while EFT is
                // settling the item result. Direct threat/death still bypasses this cadence gate.
                continue;
            }

            var progress = VanguardMedicalActionProgressReader.Capture(lease, record?.BotOwner, snapshot);
            if (lease.EffectResolvedAwaitingHandsRelease)
            {
                HandleEffectResolvedHandsDrain(lease, record?.BotOwner, snapshot, now, progress);
                continue;
            }

            bool combatProtected = VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out var combatProtectionReason);
            bool mobileLease = VanguardPrimaryExecutionContract.IsMobileMedicalKind(lease.WindowKind);
            string activeSidecarReason = "not_mobile_lease";
            bool mobileSidecarBaseAllowed = mobileLease
                && VanguardPrimaryExecutionContract.IsMobileMedicalSidecarCandidate(snapshot, out activeSidecarReason);
            bool combatMicroAidStillAllowed = !combatProtected
                || (mobileSidecarBaseAllowed && VanguardPrimaryExecutionContract.IsCombatMicroAidOpportunity(snapshot, out activeSidecarReason));
            bool mobileSidecarStillAllowed = mobileSidecarBaseAllowed && combatMicroAidStillAllowed;
            if (combatProtected && (!mobileLease || !mobileSidecarStillAllowed))
            {
                if (!progress.FirstAidUsing && !lease.ItemUseObserved)
                {
                    CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Interrupted,
                        "InterruptedBySainCombatPrimaryBeforeUse:" + combatProtectionReason, FailureCooldown, progress);
                    VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.SainWindowStatusTag,
                        $"VANGUARD_MEDICAL_LEASE_INTERRUPTED_BY_COMBAT operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(lease.LeaseId)}; window={Safe(lease.WindowKind)}; mobile={Bool(mobileLease)}; sidecarAllowed={Bool(mobileSidecarStillAllowed)}; sidecarReason={Safe(activeSidecarReason)}; combatReason={Safe(combatProtectionReason)}; itemUseObserved=false; doctrine=no_stationary_or_unsafe_mobile_medical_beside_sain_combat; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; medicalTag={StatusTag}");
                    continue;
                }

                LogCombatDrainOnly(lease, snapshot, now, mobileLease, mobileSidecarStillAllowed, activeSidecarReason, combatProtectionReason);
            }

            if (progress.OperatorDead)
            {
                CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Failed, progress.Reason, FailureCooldown, progress);
                continue;
            }

            if (TryPreemptLowerPriorityMedicalLease(lease, record?.BotOwner, snapshot, now, progress))
            {
                continue;
            }

            // Runtime invariant: TryApplyToCurrentPart may commit asynchronously after the old 2.75 s
            // no-progress boundary without ever exposing FirstAid.Using. Keep the exact lease alive
            // through a bounded reconciliation phase so a late item-resource/effect commit is
            // attributed to its originating transaction and a second medical lease cannot race it.
            if (!IsSurgeryNeed(lease.MedicalNeed)
                && HandleNativeStartPendingReconciliation(lease, snapshot, now, progress))
            {
                continue;
            }

            // Runtime invariant: surgery owns one controller use. Once native cancellation has been
            // requested, keep the medical lease until EFT has actually returned the hands. This
            // prevents combat/follow/loot from racing the controller's cancellation callback.
            if (IsSurgeryNeed(lease.MedicalNeed)
                && lease.SurgeryCancellationRequested
                && HandlePendingSurgeryCancellation(lease, record?.BotOwner, snapshot, now, progress))
            {
                continue;
            }

            // A native first-aid use already committed before a true direct threat must not
            // drain to completion while the Operator is being engaged. Keep the same lease until
            // EFT returns the hands, so combat cannot race the native cancellation callback.
            if (!IsSurgeryNeed(lease.MedicalNeed)
                && lease.FirstAidCancellationRequested
                && HandlePendingFirstAidCancellation(lease, record?.BotOwner, snapshot, now, progress))
            {
                continue;
            }

            if (IsSurgeryNeed(lease.MedicalNeed))
            {
                TryObserveTerminalSurgeryItemDepletionCommit(
                    lease,
                    snapshot,
                    now,
                    progress,
                    "active_lease_update",
                    out _);
            }

            if (!IsSurgeryNeed(lease.MedicalNeed)
                && mobileLease
                && progress.FirstAidUsing
                && HasTrueMedicalAbortThreat(snapshot, out var committedFirstAidThreat))
            {
                TryCancelCommittedFirstAid(lease, record?.BotOwner, now, progress, committedFirstAidThreat);
                continue;
            }

            // True enemy fire may always cancel an already committed procedure. A separate
            // movement guard below can also cancel, but only after reliable snapshots prove that
            // a real path command remained active; raw post-stall drift is never sufficient.
            if (IsSurgeryNeed(lease.MedicalNeed)
                && lease.SurgeryApplyAttemptCount > 0
                && IsSurgeryControllerActive(record?.BotOwner, progress)
                && HasTrueMedicalAbortThreat(snapshot, out var committedSurgeryThreat))
            {
                TryCancelCommittedSurgery(lease, record?.BotOwner, now, progress, committedSurgeryThreat, "true_threat", isThreat: true);
                continue;
            }

            if (IsSurgeryNeed(lease.MedicalNeed)
                && progress.FirstAidUsing
                && IsStationaryMedicalWindowBroken(lease, record?.BotOwner, snapshot, now, out var activeSurgeryWindowReason)
                && activeSurgeryWindowReason.StartsWith("persistent_commanded_movement", StringComparison.OrdinalIgnoreCase))
            {
                TryCancelCommittedSurgery(lease, record?.BotOwner, now, progress, activeSurgeryWindowReason, "commanded_movement_violation", isThreat: false);
                continue;
            }

            if (combatProtected && (!mobileLease || !mobileSidecarStillAllowed) && progress.FirstAidUsing)
            {
                // An item already committed before combat is allowed to drain naturally. Vanguard
                // must not refresh stationary hold, retry surgery or start another step. A surgical
                // controller that consumed its resource without any target effect remains bounded.
                ObserveFirstAidUsing(lease, now, progress);
                if (TryHandleNativeMedicalStartStall(lease, record?.BotOwner, snapshot, now, progress, allowInternalSurgeryRetry: false))
                {
                    continue;
                }

                if (IsSurgeryNeed(lease.MedicalNeed)
                    && TryHandleSurgeryControllerCommitTimeout(lease, record?.BotOwner, snapshot, now, progress, allowInternalRetry: false))
                {
                    continue;
                }

                if (IsSurgeryNeed(lease.MedicalNeed)
                    && TryHandleResourceConsumedNoTargetEffect(lease, record?.BotOwner, now, progress))
                {
                    continue;
                }

                lease.ItemUseObserved = true;
                lease.LastProgressAtUtc = now;
                lease.LastProgressKind = "combat_drain_existing_controller_use";
                var requestedDrainDeadline = now + TimeSpan.FromSeconds(1.00d);
                lease.NoProgressUntilUtc = requestedDrainDeadline < lease.MaxUntilUtc ? requestedDrainDeadline : lease.MaxUntilUtc;
                if (now >= lease.MaxUntilUtc)
                {
                    if (HoldCommittedSurgeryUntilControllerTerminal(lease, record?.BotOwner, now, progress, "combat_drain_max"))
                    {
                        continue;
                    }

                    if (TryExtendControllerUsingGrace(lease, now, progress, "combat_drain_max"))
                    {
                        continue;
                    }

                    string maxReason = IsSurgeryNeed(lease.MedicalNeed)
                        ? "HardProcedureTimeoutDuringCombatDrain"
                        : "MaxWindowExpiredDuringCombatDrainAfterGrace";
                    maxReason += ":" + RecoverControllerAtTerminalBoundary(record?.BotOwner, snapshot, lease, maxReason);
                    CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Timeout, maxReason, CooldownForMedicalOutcome(lease, progress, maxReason, FailureCooldown), progress);
                }
                continue;
            }

            if (progress.FirstAidUsing)
            {
                ObserveFirstAidUsing(lease, now, progress);
                if (TryHandleNativeMedicalStartStall(lease, record?.BotOwner, snapshot, now, progress, allowInternalSurgeryRetry: true))
                {
                    continue;
                }

                if (IsSurgeryNeed(lease.MedicalNeed))
                {
                    VanguardMedicalIsolationController.RefreshStationaryMedicalHold(lease, record?.BotOwner, snapshot, now, "surgery_using_heartbeat", out _);
                    if (TryHandleSurgeryControllerCommitTimeout(lease, record?.BotOwner, snapshot, now, progress, allowInternalRetry: true))
                    {
                        continue;
                    }

                    if (TryHandleResourceConsumedNoTargetEffect(lease, record?.BotOwner, now, progress))
                    {
                        continue;
                    }
                }

                if (now >= lease.MaxUntilUtc)
                {
                    if (HoldCommittedSurgeryUntilControllerTerminal(lease, record?.BotOwner, now, progress, "normal_using_max"))
                    {
                        continue;
                    }

                    if (TryExtendControllerUsingGrace(lease, now, progress, "normal_using_max"))
                    {
                        continue;
                    }

                    lease.LastProgressKind = IsSurgeryNeed(lease.MedicalNeed) ? "surgical_kit_using_at_max_window" : "first_aid_using_at_max_window_after_grace";
                    string maxReason = IsSurgeryNeed(lease.MedicalNeed) ? "HardProcedureTimeoutWhileControllerUsing" : "MaxWindowExpiredWhileControllerUsingAfterGrace";
                    maxReason += ":" + RecoverControllerAtTerminalBoundary(record?.BotOwner, snapshot, lease, maxReason);
                    CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Timeout, maxReason, CooldownForMedicalOutcome(lease, progress, maxReason, FailureCooldown), progress);
                    continue;
                }

                continue;
            }

            if (combatProtected && (!mobileLease || !mobileSidecarStillAllowed))
            {
                if (IsStrictCompletionResolved(lease, progress) && now >= lease.MinUntilUtc)
                {
                    lease.CompletionObserved = true;
                    string combatDrainCompletionReason = progress.NeedResolved ? "NeedResolvedDuringCombatDrain" : "TargetResolvedDuringCombatDrain";
                    CompleteResolvedLeaseOrDrainHands(lease, record?.BotOwner, snapshot, now, progress, combatDrainCompletionReason, SuccessCooldown);
                    continue;
                }

                if (CanCompletePartialMedicalEffect(lease, progress) && now >= lease.MinUntilUtc)
                {
                    lease.CompletionObserved = true;
                    CompleteResolvedLeaseOrDrainHands(lease, record?.BotOwner, snapshot, now, progress, "PartialMedicalEffectObservedDuringCombatDrain", PartialSuccessCooldown);
                    continue;
                }

                lease.LastProgressKind = "combat_drain_only_no_controller_mutation";
                lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(1.00d);
                continue;
            }

            if (VanguardPostOrbitInventoryRecoveryService.ShouldAbortMedicalGhostUse(lease, record?.BotOwner, snapshot, progress, now, out var ghostReason, out var ghostSummary))
            {
                VanguardPostOrbitInventoryRecoveryService.TryRecoverMedicalGhostUse(record?.BotOwner, lease, now, ghostReason, out var recoverySummary);
                CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Failed, ghostReason + ":" + recoverySummary + ":" + ghostSummary, NoEffectCooldown, progress);
                continue;
            }

            if (progress.ThreatInterrupt)
            {
                if (IsSurgeryNeed(lease.MedicalNeed))
                {
                    if (HasTrueMedicalAbortThreat(snapshot, out var surgeryThreatReason))
                    {
                        lease.ThreatObservedDuringLease = true;
                        if (!progress.FirstAidUsing && !lease.ItemUseObserved)
                        {
                            CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Interrupted, "InterruptedByTrueSurgeryThreatBeforeUse:" + surgeryThreatReason, FailureCooldown, progress);
                            continue;
                        }
                    }
                    else
                    {
                        VanguardMedicalIsolationController.RefreshStationaryMedicalHold(lease, record?.BotOwner, snapshot, now, "ignored_non_abort_threat_interrupt", out var ignoredThreatHoldSummary);
                        lease.LastProgressKind = "surgery_ignored_non_abort_threat_interrupt";
                        lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(3.00d);
                        VanguardClientDiagnosticsLog.Info(MedicalHardLockAbortGateStatusTag, $"VANGUARD_MEDICAL_SURGERY_ABORT_IGNORED_NON_REAL_THREAT {lease.Summary}; progressThreatInterrupt=true; enemyCanShoot={Bool(snapshot.Medical.Safety.EnemyCanShoot)}; incomingFire={Bool(snapshot.Medical.Safety.IncomingFireRecent)}; immediate={Bool(snapshot.Medical.Safety.ImmediateCombatBlock)}; enemyVisible={Bool(snapshot.Medical.Safety.EnemyVisible)}; directThreat={Bool(snapshot.Threat.DirectThreat)}; {ignoredThreatHoldSummary}; abortOnlyForTrueThreatOrDurableCommandedMovement=true; keepHardLock=true; tag={MedicalHardLockAbortGateStatusTag}; hardHoldTag={MedicalSurgeryHardHoldStatusTag}");
                    }
                }
                else
                {
                    lease.ThreatObservedDuringLease = true;
                    if (!progress.FirstAidUsing && !lease.ItemUseObserved)
                    {
                        CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Interrupted, "InterruptedByCriticalThreatBeforeUse", FailureCooldown);
                        continue;
                    }
                }
            }

            if (IsSurgeryNeed(lease.MedicalNeed))
            {
                if (IsSurgeryUnsafeDuringLease(lease, snapshot, out var unsafeReason))
                {
                    VanguardClientDiagnosticsLog.Info(SainLikeSurgerySafetyStatusTag, $"VANGUARD_SURGERY_ABORTED_UNSAFE {lease.Summary}; reason={Safe(unsafeReason)}; areaClear={Bool(snapshot.Medical.Safety.SurgeryAreaClear)}; surgeryReason={Safe(snapshot.Medical.Safety.SurgeryAreaClearReason)}; enemyVisible={Bool(snapshot.Medical.Safety.EnemyVisible)}; enemyCanShoot={Bool(snapshot.Medical.Safety.EnemyCanShoot)}; incomingFire={Bool(snapshot.Medical.Safety.IncomingFireRecent)}; coverOrHold={Bool(snapshot.Medical.Safety.CoveredOrHoldingAngle)}; tag={SainLikeSurgerySafetyStatusTag}; Tag={SurgeryCoverCompletionGuardStatusTag}");
                    CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Interrupted, "SurgeryAreaUnsafeDuringLease:" + unsafeReason, FailureCooldown, progress);
                    continue;
                }

                if (IsStationaryMedicalWindowBroken(lease, record?.BotOwner, snapshot, now, out var brokenReason))
                {
                    if (brokenReason.StartsWith("persistent_commanded_movement", StringComparison.OrdinalIgnoreCase)
                        && IsSurgeryControllerActive(record?.BotOwner, progress))
                    {
                        TryCancelCommittedSurgery(lease, record?.BotOwner, now, progress, brokenReason, "commanded_movement_violation", isThreat: false);
                        continue;
                    }

                    if (CanHoldBrokenSurgeryWindowUntilTimeout(lease, record?.BotOwner, snapshot, now, brokenReason, out var hardHoldSummary))
                    {
                        lease.LastProgressKind = "hard_procedure_window_recovered:" + Safe(brokenReason);
                        lease.LastProgressAtUtc = now;
                        lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(3.00d);
                        VanguardClientDiagnosticsLog.Info(MedicalHardProcedureAuthorityStatusTag, $"VANGUARD_MEDICAL_HARD_PROCEDURE_WINDOW_HELD {lease.Summary}; brokenReason={Safe(brokenReason)}; {hardHoldSummary}; releaseCondition=target_resolved_or_true_threat_or_controller_terminal_or_max_window; noImmediateRelease=true; tag={MedicalHardProcedureAuthorityStatusTag}; completionGateTag={MedicalProcedureCompletionGateStatusTag}; directChainTag={MedicalSurgeryDirectChainStatusTag}; sameProcedureStartTag={MedicalSurgerySameProcedureStartStatusTag}; surgeryPersistenceTag={MedicalSurgeryPersistenceStatusTag}; postureRetryTag={MedicalPostureRetryStatusTag}; surgeryHardHoldTag={MedicalSurgeryHardHoldStatusTag}; orbitLootFreezeTag={MedicalOrbitLootFreezeDuringSurgeryStatusTag}; surgeryDebtTag={MedicalSurgeryDebtRetryStatusTag}");
                        continue;
                    }

                    LogStationaryWindowBroken(lease, snapshot, brokenReason);
                    CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Failed, "StationaryMedicalWindowBroken:" + brokenReason, FailureCooldown, progress);
                    continue;
                }

                if (TryHandleSurgeryControllerCommitTimeout(lease, record?.BotOwner, snapshot, now, progress, allowInternalRetry: true))
                {
                    continue;
                }

                if (TryHandleResourceConsumedNoTargetEffect(lease, record?.BotOwner, now, progress))
                {
                    continue;
                }
            }

            // Runtime invariant: terminal completion is evaluated before hard max-window.
            // If the target/need is already resolved at the max boundary, Vanguard
            // must report completion, not a misleading MaxWindowExpired timeout.
            if (IsStrictCompletionResolved(lease, progress) && now >= lease.MinUntilUtc)
            {
                ObserveSequentialSurgeryBoundaryIfNeeded(lease, record?.BotOwner, snapshot, now, progress, "resolved_before_completion");

                lease.CompletionObserved = true;
                string reason = progress.NeedResolved ? "NeedResolved" : "TargetResolved";
                CompleteResolvedLeaseOrDrainHands(lease, record?.BotOwner, snapshot, now, progress, reason, SuccessCooldown);
                continue;
            }

            if (CanCompletePartialMedicalEffect(lease, progress) && now >= lease.MinUntilUtc)
            {
                lease.CompletionObserved = true;
                CompleteResolvedLeaseOrDrainHands(lease, record?.BotOwner, snapshot, now, progress, "PartialMedicalEffectObserved", PartialSuccessCooldown);
                continue;
            }

            // Hard max-window is evaluated before the normal use heartbeat. A native surgery
            // controller may cross that boundary only while the exact native controller is still
            // active and only inside the existing absolute deadline. Resource consumption remains
            // a result-pending commit, never a productive effect. Terminal completion wins first.
            if (now >= lease.MaxUntilUtc)
            {
                if (HoldCommittedSurgeryUntilControllerTerminal(lease, record?.BotOwner, now, progress, "sampled_using_missed_at_max"))
                {
                    continue;
                }

                CompleteMaxWindowLease(lease, record?.BotOwner, snapshot, now, progress);
                continue;
            }

            if (lease.ItemUseObserved && !progress.FirstAidUsing)
            {
                if (IsSurgeryNeed(lease.MedicalNeed))
                {
                    VanguardMedicalIsolationController.RefreshStationaryMedicalHold(lease, record?.BotOwner, snapshot, now, "surgery_post_use_recheck", out _);
                }

                if (HandlePostUseRecheck(lease, record?.BotOwner, snapshot, now, progress))
                {
                    continue;
                }
            }

            if (!lease.ItemUseObserved && now >= lease.NoProgressUntilUtc)
            {
                if (IsSurgeryNeed(lease.MedicalNeed) && now < lease.MaxUntilUtc)
                {
                    VanguardMedicalIsolationController.RefreshStationaryMedicalHold(lease, record?.BotOwner, snapshot, now, "hard_procedure_waiting_for_surgery_use", out var waitingSummary);
                    lease.LastProgressAtUtc = now;
                    lease.LastProgressKind = "hard_procedure_waiting_for_controller_use";
                    lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(3.00d);
                    VanguardClientDiagnosticsLog.Info(MedicalHardProcedureAuthorityStatusTag, $"VANGUARD_MEDICAL_HARD_PROCEDURE_WAITING_FOR_USE {lease.Summary}; {waitingSummary}; releaseCondition=target_resolved_or_true_threat_or_controller_terminal_or_max_window; noOrbitRelease=true; tag={MedicalHardProcedureAuthorityStatusTag}; completionGateTag={MedicalProcedureCompletionGateStatusTag}; directChainTag={MedicalSurgeryDirectChainStatusTag}; sameProcedureStartTag={MedicalSurgerySameProcedureStartStatusTag}");
                    continue;
                }

                BeginNativeStartPendingReconciliation(lease, now, "NoProgressBeforeUseObserved");
                continue;
            }
        }
    }

    private static void BeginNativeStartPendingReconciliation(
        VanguardExecutionLeaseState lease,
        DateTimeOffset now,
        string reason)
    {
        if (lease.NativeStartPendingReconciliation)
        {
            return;
        }

        lease.NativeStartPendingReconciliation = true;
        lease.NativeStartPendingSinceUtc = now;
        lease.NativeStartPendingUntilUtc = lease.MaxUntilUtc;
        lease.LastNativeStartPendingSnapshotAtUtc = DateTimeOffset.MinValue;
        lease.NativeStartPendingSnapshotCount = 0;
        lease.NativeStartLateCommitObserved = false;
        lease.NativeStartLateCommitObservedAtUtc = DateTimeOffset.MinValue;
        lease.NativeStartPendingReason = reason;
        lease.LastProgressAtUtc = now;
        lease.LastProgressKind = "native_start_pending_reconciliation";
        lease.NoProgressUntilUtc = lease.NativeStartPendingUntilUtc;

        VanguardClientDiagnosticsLog.Info(NativeMedicalCommitReconciliationStatusTag,
            $"VANGUARD_NATIVE_MEDICAL_START_PENDING {lease.Summary}; reason={Safe(reason)}; pendingWindow={(lease.NativeStartPendingUntilUtc - now).TotalSeconds:0.00}; leaseKept=true; newMedicalForbidden=true; nativeCancel=false; resourceRewrite=false; tag={NativeMedicalCommitReconciliationStatusTag}");
    }

    private static bool HandleNativeStartPendingReconciliation(
        VanguardExecutionLeaseState lease,
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        VanguardMedicalActionProgressSnapshot progress)
    {
        if (!lease.NativeStartPendingReconciliation)
        {
            return false;
        }

        if (progress.FirstAidUsing)
        {
            lease.NativeStartPendingReconciliation = false;
            lease.NativeStartPendingReason = "controller_using_observed_during_pending";
            lease.LastProgressAtUtc = now;
            lease.LastProgressKind = "native_start_pending_resolved_by_controller_using";
            VanguardClientDiagnosticsLog.Info(NativeMedicalCommitReconciliationStatusTag,
                $"VANGUARD_NATIVE_MEDICAL_START_PENDING_RESOLVED {lease.Summary}; resolution=controller_using; resourceConsumed={Bool(progress.ItemResourceConsumed)}; effectObserved={Bool(progress.AnyMedicalEffectObserved)}; keepSameLease=true; tag={NativeMedicalCommitReconciliationStatusTag}");
            return false;
        }

        bool lateCommit = progress.ItemResourceConsumed
            || progress.AnyMedicalEffectObserved
            || progress.NeedResolved
            || progress.TargetResolved;
        if (lateCommit)
        {
            lease.NativeStartPendingReconciliation = false;
            lease.NativeStartLateCommitObserved = true;
            lease.NativeStartLateCommitObservedAtUtc = now;
            lease.NativeStartPendingReason = "late_native_commit_reconciled";
            lease.ItemUseObserved = true;
            lease.FirstAidEndedObserved = false;
            lease.LastProgressAtUtc = now;
            lease.LastProgressKind = progress.AnyMedicalEffectObserved || progress.NeedResolved || progress.TargetResolved
                ? "native_start_late_effect_reconciled"
                : "native_start_late_resource_commit_reconciled";
            lease.PostUseRecheckUntilUtc = DateTimeOffset.MinValue;
            lease.NextPostUseRecheckAtUtc = DateTimeOffset.MinValue;
            lease.NoProgressUntilUtc = now + NoProgressTimeoutForLease(lease);

            VanguardClientDiagnosticsLog.Info(NativeMedicalCommitReconciliationStatusTag,
                $"VANGUARD_NATIVE_MEDICAL_LATE_COMMIT_RECONCILED {lease.Summary}; resourceReadable={Bool(progress.ItemResourceReadable)}; resourceConsumed={Bool(progress.ItemResourceConsumed)}; currentResource={(progress.ItemResourceReadable ? progress.CurrentItemResource.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "unknown")}; effectObserved={Bool(progress.AnyMedicalEffectObserved)}; needResolved={Bool(progress.NeedResolved)}; targetResolved={Bool(progress.TargetResolved)}; sameLease=true; outcomePending=true; newMedicalForbidden=true; tag={NativeMedicalCommitReconciliationStatusTag}");
            return false;
        }

        if (lease.LastNativeStartPendingSnapshotAtUtc != snapshot.CapturedAtUtc)
        {
            lease.LastNativeStartPendingSnapshotAtUtc = snapshot.CapturedAtUtc;
            lease.NativeStartPendingSnapshotCount++;
        }

        if (now < lease.NativeStartPendingUntilUtc)
        {
            lease.LastProgressAtUtc = now;
            lease.LastProgressKind = "native_start_pending_waiting_for_late_commit";
            lease.NoProgressUntilUtc = lease.NativeStartPendingUntilUtc;
            if (ShouldLogRecheck(lease.BotProfileId + "|native_start_pending|" + lease.LeaseId, now))
            {
                VanguardClientDiagnosticsLog.Diagnostic(NativeMedicalCommitReconciliationStatusTag, () =>
                    $"VANGUARD_NATIVE_MEDICAL_START_PENDING_WAIT {lease.Summary}; elapsed={(now - lease.NativeStartPendingSinceUtc).TotalSeconds:0.00}; remaining={(lease.NativeStartPendingUntilUtc - now).TotalSeconds:0.00}; snapshots={lease.NativeStartPendingSnapshotCount}; resourceReadable={Bool(progress.ItemResourceReadable)}; currentResource={(progress.ItemResourceReadable ? progress.CurrentItemResource.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "unknown")}; effectObserved={Bool(progress.AnyMedicalEffectObserved)}; nativeCancel=false; sameLease=true; tag={NativeMedicalCommitReconciliationStatusTag}");
            }
            return true;
        }

        lease.NativeStartPendingReconciliation = false;
        lease.NativeStartPendingReason = "pending_expired_no_commit";
        lease.LastProgressKind = "native_start_pending_expired_no_commit";
        CompleteLease(
            lease,
            now,
            VanguardMedicalActionOutcomeKind.Timeout,
            "NativeStartPendingExpiredNoCommit",
            FailureCooldown,
            progress);
        return true;
    }

    private static void ObserveFirstAidUsing(VanguardExecutionLeaseState lease, DateTimeOffset now, VanguardMedicalActionProgressSnapshot progress)
    {
        bool surgery = IsSurgeryNeed(lease.MedicalNeed);
        bool effectProgress = progress.TerminalAliveConfirmed && progress.AnyMedicalEffectObserved;
        bool confirmedSurgeryEffect = surgery && progress.SurgeryTargetRestored;
        lease.LastControllerActivityAtUtc = now;
        if (lease.FirstAidUsingObservedAtUtc == DateTimeOffset.MinValue)
        {
            lease.FirstAidUsingObservedAtUtc = now;
        }
        if (!lease.ItemUseObserved)
        {
            lease.ItemUseObserved = true;
            lease.LastUsingHeartbeatLogAtUtc = now;
            string activityKind = surgery ? "surgical_kit_controller_using_started" : "first_aid_controller_using_started";
            VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
                $"VANGUARD_EXECUTION_ACTIVITY_STARTED lease={Safe(lease.LeaseId)}; operator={Safe(lease.OperatorId)}; botProfile={Safe(lease.BotProfileId)}; need={lease.MedicalNeed}; target={Safe(lease.TargetPart)}; activity={activityKind}; effectProgress={Bool(effectProgress)}; resourceConsumed={Bool(progress.ItemResourceConsumed)}; reason={Safe(progress.Reason)}; surgeryTag={ActiveSurgeryStatusTag}; circuitTag={MedicalEffectCircuitBreakerStatusTag}");
        }
        else if (now - lease.LastUsingHeartbeatLogAtUtc >= UsingHeartbeatInterval)
        {
            lease.LastUsingHeartbeatLogAtUtc = now;
            string activityKind = surgery ? "surgical_kit_controller_using_heartbeat" : "first_aid_controller_using_heartbeat";
            VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                $"VANGUARD_EXECUTION_ACTIVITY_HEARTBEAT lease={Safe(lease.LeaseId)}; botProfile={Safe(lease.BotProfileId)}; activity={activityKind}; effectProgress={Bool(effectProgress)}; resourceConsumed={Bool(progress.ItemResourceConsumed)}; reason={Safe(progress.Reason)}; fullLeasePayload=false");
        }

        if (surgery && progress.ItemResourceConsumed && !lease.SurgeryResourceCommitObserved)
        {
            lease.SurgeryResourceCommitObserved = true;
            lease.SurgeryResourceCommitObservedAtUtc = now;
            lease.LastProgressKind = "surgery_resource_commit_observed_result_pending";
            VanguardClientDiagnosticsLog.Diagnostic(MedicalProcedureCompletionGateStatusTag, () =>
                $"VANGUARD_SURGERY_RESOURCE_COMMIT_OBSERVED lease={Safe(lease.LeaseId)}; operator={Safe(lease.OperatorId)}; botProfile={Safe(lease.BotProfileId)}; target={Safe(lease.TargetPart)}; resource={progress.CurrentItemResource:0.0}; callback={Bool(lease.SurgeryControllerCallbackObserved)}; targetRestored={Bool(progress.SurgeryTargetRestored)}; resultPending=true; productive=false; noReapply=true; tag={MedicalProcedureCompletionGateStatusTag}");
        }

        if (confirmedSurgeryEffect && !lease.SurgeryTargetEffectConfirmed)
        {
            lease.SurgeryTargetEffectConfirmed = true;
            lease.SurgeryTargetEffectConfirmedAtUtc = now;
            lease.LastProgressAtUtc = now;
            lease.LastProgressKind = "surgery_target_effect_confirmed_by_restoration";
            VanguardClientDiagnosticsLog.Diagnostic(MedicalProcedureCompletionGateStatusTag, () =>
                $"VANGUARD_SURGERY_TARGET_EFFECT_CONFIRMED lease={Safe(lease.LeaseId)}; operator={Safe(lease.OperatorId)}; botProfile={Safe(lease.BotProfileId)}; target={Safe(lease.TargetPart)}; resourceConsumed={Bool(progress.ItemResourceConsumed)}; callback={Bool(lease.SurgeryControllerCallbackObserved)}; targetRestored={Bool(progress.SurgeryTargetRestored)}; targetHealthImproved={Bool(progress.TargetHealthImproved)}; noReapply=true; tag={MedicalProcedureCompletionGateStatusTag}");
        }

        if (effectProgress)
        {
            lease.LastProgressAtUtc = now;
            lease.LastProgressKind = surgery ? "surgical_kit_effect_progress" : "first_aid_effect_progress";
            lease.NoProgressUntilUtc = now + NoProgressTimeoutForLease(lease);
        }
        else if (!confirmedSurgeryEffect)
        {
            lease.LastProgressKind = surgery
                ? (lease.SurgeryResourceCommitObserved ? "surgical_kit_resource_committed_result_pending" : "surgical_kit_controller_active_no_effect_yet")
                : "first_aid_controller_active_no_effect_yet";
        }

        lease.PostUseRecheckUntilUtc = DateTimeOffset.MinValue;
        lease.NextPostUseRecheckAtUtc = DateTimeOffset.MinValue;
        lease.FirstAidEndedObserved = false;
    }

    private static bool HandlePostUseRecheck(VanguardExecutionLeaseState lease, EFT.BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, VanguardMedicalActionProgressSnapshot progress)
    {
        if (!lease.FirstAidEndedObserved)
        {
            lease.FirstAidEndedObserved = true;
            lease.PostUseRecheckCount = 0;
            var window = PostUseRecheckWindowForLease(lease);
            lease.PostUseRecheckUntilUtc = now + window;
            lease.NextPostUseRecheckAtUtc = now + PostUseRecheckCadenceForLease(lease);
            lease.LastProgressAtUtc = now;
            lease.LastProgressKind = "post_use_recheck_started";
            lease.NoProgressUntilUtc = now + NoProgressTimeoutForLease(lease);
            string recheckLog = IsSurgeryNeed(lease.MedicalNeed) ? "VANGUARD_MEDICAL_SURGERY_SETTLE_PENDING" : "VANGUARD_MEDICAL_MOBILE_RECHECK";
            VanguardClientDiagnosticsLog.Info(StatusTag, $"{recheckLog} {lease.Summary}; phase=post_use_started; window={window.TotalSeconds:0.00}; {progress.EffectSummary}; needStillPresent={Bool(progress.NeedStillPresent)}; targetAware=true; effectGuard={MedicalEffectGuardStatusTag}; surgeryTag={ActiveSurgeryStatusTag}; surgerySafetyTag={SainLikeSurgerySafetyStatusTag}");
            return true;
        }

        if (lease.NextPostUseRecheckAtUtc != DateTimeOffset.MinValue && now < lease.NextPostUseRecheckAtUtc)
        {
            return true;
        }

        lease.PostUseRecheckCount = Math.Min(MaxPostUseRecheckSnapshots, lease.PostUseRecheckCount + 1);
        lease.NextPostUseRecheckAtUtc = now + PostUseRecheckCadenceForLease(lease);
        bool recheckBudgetExhausted = lease.PostUseRecheckCount >= MaxPostUseRecheckSnapshots;
        if (now < lease.PostUseRecheckUntilUtc && !recheckBudgetExhausted)
        {
            lease.LastProgressAtUtc = now;
            lease.NoProgressUntilUtc = now + NoProgressTimeoutForLease(lease);
            return true;
        }

        if (IsSurgeryNeed(lease.MedicalNeed)
            && progress.TargetStillPresent
            && (lease.SurgeryControllerCallbackObserved || lease.SurgeryResourceCommitObserved || progress.ItemResourceConsumed)
            && TryRepairCommittedSurgeryEffect(lease, botOwner, snapshot, now, progress, "post_use_terminal_truth", out var repairSummary))
        {
            lease.PostUseRecheckUntilUtc = Min(now + TimeSpan.FromSeconds(1.50d), lease.AbsoluteMaxUntilUtc);
            lease.NextPostUseRecheckAtUtc = now + TimeSpan.FromSeconds(0.20d);
            lease.NoProgressUntilUtc = lease.PostUseRecheckUntilUtc;
            VanguardClientDiagnosticsLog.Info(MedicalSurgeryDeterministicCompletionStatusTag,
                $"VANGUARD_SURGERY_EFFECT_REPAIRED {lease.Summary}; source=post_use; {repairSummary}; next=body_part_truth_recheck; gameplayFailure=false; tag={MedicalSurgeryDeterministicCompletionStatusTag}");
            return true;
        }

        if (IsStrictCompletionResolved(lease, progress))
        {
            ObserveSequentialSurgeryBoundaryIfNeeded(lease, botOwner, snapshot, now, progress, "post_use_target_resolved");

            lease.CompletionObserved = true;
            string reason = progress.NeedResolved ? "NeedResolvedAfterPostUseRecheck" : "TargetResolvedAfterPostUseRecheck";
            CompleteResolvedLeaseOrDrainHands(lease, botOwner, snapshot, now, progress, reason, SuccessCooldown);
            return true;
        }

        if (CanCompletePartialMedicalEffect(lease, progress))
        {
            lease.CompletionObserved = true;
            CompleteResolvedLeaseOrDrainHands(lease, botOwner, snapshot, now, progress, "PartialMedicalEffectObservedAfterPostUseRecheck", PartialSuccessCooldown);
            return true;
        }

        if (progress.NoMedicalEffectObserved)
        {
            if (lease.LastNoEffectConfirmationAtUtc == DateTimeOffset.MinValue
                || now - lease.LastNoEffectConfirmationAtUtc >= NoEffectConfirmationDelay)
            {
                lease.NoEffectConfirmationCount = Math.Min(2, lease.NoEffectConfirmationCount + 1);
                lease.LastNoEffectConfirmationAtUtc = now;
            }
            if (!recheckBudgetExhausted
                && lease.NoEffectConfirmationCount < 2
                && now < lease.AbsoluteMaxUntilUtc)
            {
                lease.PostUseRecheckUntilUtc = Min(now + NoEffectConfirmationDelay, lease.AbsoluteMaxUntilUtc);
                lease.NextPostUseRecheckAtUtc = now + PostUseRecheckCadenceForLease(lease);
                lease.MaxUntilUtc = Max(lease.MaxUntilUtc, lease.PostUseRecheckUntilUtc);
                lease.LastProgressAtUtc = now;
                lease.LastProgressKind = "no_effect_confirmation_pending";
                return true;
            }
        }

        if (!recheckBudgetExhausted
            && IsSurgeryNeed(lease.MedicalNeed)
            && now < lease.MaxUntilUtc
            && (progress.NoMedicalEffectObserved || progress.NeedStillPresent))
        {
            lease.PostUseRecheckUntilUtc = Min(now + TimeSpan.FromSeconds(2.50d), lease.MaxUntilUtc);
            lease.NextPostUseRecheckAtUtc = now + PostUseRecheckCadenceForLease(lease);
            lease.LastProgressAtUtc = now;
            lease.LastProgressKind = progress.NoMedicalEffectObserved ? "hard_procedure_post_use_no_effect_waiting" : "hard_procedure_post_use_need_still_present_waiting";
            lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(3.00d);
            VanguardClientDiagnosticsLog.Info(MedicalProcedureCompletionGateStatusTag, $"VANGUARD_MEDICAL_PROCEDURE_COMPLETION_GATE_HOLD {lease.Summary}; phase=post_use; noEffect={Bool(progress.NoMedicalEffectObserved)}; needStillPresent={Bool(progress.NeedStillPresent)}; maxUntil={lease.MaxUntilUtc:O}; releaseCondition=target_resolved_or_true_threat_or_controller_terminal_or_max_window; {progress.EffectSummary}; tag={MedicalProcedureCompletionGateStatusTag}; hardProcedureTag={MedicalHardProcedureAuthorityStatusTag}");
            return true;
        }

        if (progress.NoMedicalEffectObserved)
        {
            CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Failed,
                recheckBudgetExhausted ? "NoMedicalEffectObservedAtBoundedRecheckLimit" : "NoMedicalEffectObserved",
                NoEffectCooldown, progress);
            return true;
        }

        if (progress.NeedStillPresent)
        {
            CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Failed,
                recheckBudgetExhausted ? "PostUseRecheckNeedStillPresentAtBoundedLimit" : "PostUseRecheckNeedStillPresentNoEffect",
                NoEffectCooldown, progress);
            return true;
        }

        CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Timeout,
            recheckBudgetExhausted ? "PostUseRecheckBoundedLimitInconclusive" : "PostUseRecheckInconclusive",
            FailureCooldown, progress);
        return true;
    }

    private static bool TryResolveCanonicalEffectTruthBeforeSelection(
        BotOwner botOwner,
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        out OperatorDecisionSnapshot effectiveSnapshot)
    {
        effectiveSnapshot = snapshot;

        // Selection is also the last safe boundary for a canonical-only bleed that the immutable
        // decision snapshot did not expose at all. Capture uses the canonical service cache and
        // therefore does not force a reflected controller scan on every admission tick.
        object? player = botOwner.GetPlayer;
        VanguardCanonicalMedicalEffectSnapshot canonical = VanguardCanonicalMedicalStateService.Capture(
            snapshot.BotProfileId,
            player,
            botOwner.HealthController,
            VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player, "ActiveHealthController"),
            now,
            "medical_selection_cached_canonical_truth",
            forceRefresh: false);
        bool canonicalHeavy = canonical.Badges.Contains("HB", StringComparer.OrdinalIgnoreCase);
        bool canonicalLight = canonical.Badges.Contains("LB", StringComparer.OrdinalIgnoreCase);
        bool canonicalFracture = canonical.Badges.Contains("FR", StringComparer.OrdinalIgnoreCase);
        bool mismatch = canonicalHeavy != snapshot.Medical.Need.HasHeavyBleed
            || canonicalLight != snapshot.Medical.Need.HasLightBleed;
        if (!mismatch)
        {
            return true;
        }

        VanguardMedicalNeed canonicalBleedNeed = canonicalHeavy
            ? VanguardMedicalNeed.HeavyBleed
            : canonicalLight ? VanguardMedicalNeed.LightBleed : VanguardMedicalNeed.None;
        if (canonicalBleedNeed == VanguardMedicalNeed.None && !canonical.ScanComplete)
        {
            VanguardCanonicalMedicalStateService.RecordSelectionDeferral(
                snapshot.BotProfileId,
                canonical.Revision,
                "canonical_negative_incomplete_scan_wait_for_complete_truth");
            VanguardClientDiagnosticsLog.Warning(CanonicalPriorityPreemptionStatusTag,
                $"VANGUARD_MEDICAL_CANONICAL_SELECTION_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; snapshotHeavy={Bool(snapshot.Medical.Need.HasHeavyBleed)}; canonicalHeavy={Bool(canonicalHeavy)}; snapshotLight={Bool(snapshot.Medical.Need.HasLightBleed)}; canonicalLight={Bool(canonicalLight)}; snapshotFracture={Bool(snapshot.Medical.Need.HasFracture)}; canonicalFracture={Bool(canonicalFracture)}; canonicalRevision={canonical.Revision}; complete=false; actionStarted=false; forcedScan=false; materialRevisionStable=true; deferReason=incomplete_canonical_scan; convergenceTag={CanonicalMedicalConvergenceStatusTag}; tag={CanonicalPriorityPreemptionStatusTag}");
            return false;
        }

        string target = canonicalBleedNeed == VanguardMedicalNeed.None
            ? "none"
            : ResolveCanonicalBleedTarget(canonicalBleedNeed, canonical, snapshot.Medical.Need.TargetPart);
        VanguardMedicalNeedSnapshot canonicalNeed = BuildCanonicalBleedNeedOverlay(snapshot.Medical.Need, canonicalBleedNeed, target, canonical);
        VanguardMedicalInventoryReadResult inventory = VanguardMedicalInventoryReader.Capture(botOwner);
        VanguardMedicalActionabilitySnapshot actionability = VanguardMedicalActionabilityReader.Capture(botOwner, canonicalNeed, inventory);
        VanguardMedicalPlanSnapshot plan = VanguardMedicalPlanReadOnlyBuilder.Build(canonicalNeed, actionability, snapshot.Medical.Safety);
        VanguardMedicalDecisionSnapshot medical = new()
        {
            Alive = snapshot.Medical.Alive,
            ControllerObserved = snapshot.Medical.ControllerObserved,
            ControllerType = snapshot.Medical.ControllerType,
            Need = canonicalNeed,
            Inventory = inventory.Snapshot,
            Actionability = actionability,
            Safety = snapshot.Medical.Safety,
            Plan = plan,
            Classification = snapshot.Medical.Classification + ";canonicalBleedOverlay=true;canonicalRevision=" + canonical.Revision
        };
        effectiveSnapshot = CloneWithMedical(snapshot, medical);
        VanguardCanonicalMedicalStateService.RecordSelectionOverlay(
            snapshot.BotProfileId,
            canonical.Revision,
            "need=" + canonicalBleedNeed + ";target=" + Safe(target));
        VanguardClientDiagnosticsLog.Operational(CanonicalMedicalConvergenceStatusTag, () =>
            $"VANGUARD_MEDICAL_CANONICAL_SELECTION_OVERLAY operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; snapshotNeed={snapshot.Medical.Need.DominantNeed}; canonicalNeed={canonicalBleedNeed}; target={Safe(target)}; canonicalRevision={canonical.Revision}; canonicalSource={Safe(canonical.Source)}; canonicalScanComplete={Bool(canonical.ScanComplete)}; forcedScan=false; inventoryRecaptured=true; canApply={Tri(actionability.CanApplyItem)}; itemAvailable={Bool(actionability.RequiredItemAvailable)}; plan={Safe(plan.PlanKey)}; directSelection=true; negativeMismatchCleared={Bool(canonicalBleedNeed == VanguardMedicalNeed.None)}; actionStarted=false; fractureDirectTruthPreserved=true; tag={CanonicalMedicalConvergenceStatusTag}");
        return true;
    }

    private static VanguardMedicalNeedSnapshot BuildCanonicalBleedNeedOverlay(
        VanguardMedicalNeedSnapshot original,
        VanguardMedicalNeed canonicalNeed,
        string target,
        VanguardCanonicalMedicalEffectSnapshot canonical)
    {
        bool heavy = canonicalNeed == VanguardMedicalNeed.HeavyBleed;
        bool light = canonicalNeed == VanguardMedicalNeed.LightBleed;
        string badges = string.Join(",", canonical.Badges);
        return new VanguardMedicalNeedSnapshot
        {
            IsReadable = original.IsReadable || canonical.ControllerObserved,
            DominantNeed = canonicalNeed,
            HealthPercent = original.HealthPercent,
            HasHeavyBleed = heavy,
            HasLightBleed = light,
            HasFracture = original.HasFracture,
            HasPain = original.HasPain,
            HasTremor = original.HasTremor,
            HasDestroyedPart = original.HasDestroyedPart,
            HasHpDamage = original.HasHpDamage,
            HasBlackBroken = original.HasBlackBroken,
            HasOperableDestroyedPart = original.HasOperableDestroyedPart,
            HasUntreatableVitalDamage = original.HasUntreatableVitalDamage,
            UntreatableVitalPartCount = original.UntreatableVitalPartCount,
            UntreatableVitalParts = original.UntreatableVitalParts,
            DestroyedPartCount = original.DestroyedPartCount,
            DamagedPartCount = original.DamagedPartCount,
            BrokenPartCount = original.BrokenPartCount,
            TargetKnown = !string.IsNullOrWhiteSpace(target) && !string.Equals(target, "none", StringComparison.OrdinalIgnoreCase),
            TargetPart = target,
            Badges = string.IsNullOrWhiteSpace(badges) ? original.Badges : badges,
            DestroyedParts = original.DestroyedParts,
            DamagedParts = original.DamagedParts,
            BrokenParts = original.BrokenParts,
            RawEffectNames = string.Join(",", canonical.RawSignatures.Take(16)),
            Source = original.Source + ";canonicalBleedOverlay=true;canonicalRevision=" + canonical.Revision
        };
    }

    private static OperatorDecisionSnapshot CloneWithMedical(
        OperatorDecisionSnapshot snapshot,
        VanguardMedicalDecisionSnapshot medical)
        => new()
        {
            OperatorId = snapshot.OperatorId,
            OwnerProfileId = snapshot.OwnerProfileId,
            BotProfileId = snapshot.BotProfileId,
            Nickname = snapshot.Nickname,
            Alive = snapshot.Alive,
            Position = snapshot.Position,
            RealSpeed = snapshot.RealSpeed,
            Movement = snapshot.Movement,
            Brain = snapshot.Brain,
            Sain = snapshot.Sain,
            Threat = snapshot.Threat,
            GrenadeHazard = snapshot.GrenadeHazard,
            ThreatScan = snapshot.ThreatScan,
            Medical = medical,
            Awareness = snapshot.Awareness,
            SquadCohesion = snapshot.SquadCohesion,
            MovementAuthority = snapshot.MovementAuthority,
            Looting = snapshot.Looting,
            CorpseLoot = snapshot.CorpseLoot,
            Orbit = snapshot.Orbit,
            CapturedAtUtc = snapshot.CapturedAtUtc
        };

    private static bool TryPreemptLowerPriorityMedicalLease(
        VanguardExecutionLeaseState lease,
        BotOwner? botOwner,
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        VanguardMedicalActionProgressSnapshot progress)
    {
        // A cancellation already owns this lease until native hands return. Do not re-enter the
        // preemption path and starve the existing bounded cancellation drain handler.
        if (lease.FirstAidCancellationRequested || lease.SurgeryCancellationRequested)
        {
            return false;
        }

        if (botOwner == null)
        {
            return false;
        }

        object? player = botOwner.GetPlayer;
        VanguardCanonicalMedicalEffectSnapshot canonical = VanguardCanonicalMedicalStateService.Capture(
            lease.BotProfileId,
            player,
            botOwner.HealthController,
            VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player, "ActiveHealthController"),
            now,
            "active_medical_lease_priority_guard");
        VanguardMedicalNeed canonicalNeed = ResolveCanonicalBleedNeed(canonical);
        if (!IsHigherPriorityMedicalNeed(canonicalNeed, lease.MedicalNeed))
        {
            return false;
        }

        string canonicalTarget = ResolveCanonicalBleedTarget(canonicalNeed, canonical, snapshot.Medical.Need.TargetPart);
        string reason = "canonical_need=" + canonicalNeed
            + ":lease_need=" + lease.MedicalNeed
            + ":canonical_target=" + Safe(canonicalTarget)
            + ":lease_target=" + Safe(lease.TargetPart);
        VanguardCanonicalMedicalStateService.RequestForceRefresh(lease.BotProfileId, "medical_priority_preemption:" + canonicalNeed);
        VanguardCanonicalMedicalStateService.RecordPriorityPreemption(
            lease.BotProfileId,
            canonical.Revision,
            "oldNeed=" + lease.MedicalNeed + ";newNeed=" + canonicalNeed);

        bool nativeControllerActive = progress.FirstAidUsing || lease.ItemUseObserved;
        if (IsSurgeryNeed(lease.MedicalNeed))
        {
            nativeControllerActive |= IsSurgeryControllerActive(botOwner, progress);
            if (nativeControllerActive)
            {
                TryCancelCommittedSurgery(
                    lease,
                    botOwner,
                    now,
                    progress,
                    reason,
                    "canonical_priority_preemption",
                    isThreat: false);
            }
            else
            {
                CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Interrupted,
                    "PreemptedByCanonicalMedicalPriorityBeforeNativeUse:" + reason, TimeSpan.Zero, progress);
            }
        }
        else if (nativeControllerActive)
        {
            TryCancelCommittedFirstAid(
                lease,
                botOwner,
                now,
                progress,
                reason,
                "canonical_priority_preemption",
                isThreat: false);
        }
        else
        {
            CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Interrupted,
                "PreemptedByCanonicalMedicalPriorityBeforeNativeUse:" + reason, TimeSpan.Zero, progress);
        }

        string resourceAtPreemption = progress.ItemResourceReadable
            ? progress.CurrentItemResource.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";
        VanguardClientDiagnosticsLog.Warning(CanonicalPriorityPreemptionStatusTag,
            $"VANGUARD_MEDICAL_CANONICAL_PRIORITY_PREEMPTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(lease.LeaseId)}; oldNeed={lease.MedicalNeed}; newNeed={canonicalNeed}; oldTarget={Safe(lease.TargetPart)}; newTarget={Safe(canonicalTarget)}; item={Safe(lease.ItemName)}; itemInstance={Safe(lease.ItemInstanceId)}; initialResource={lease.InitialItemResource:0.0}/{lease.InitialItemMaxResource:0.0}; resourceAtPreemption={resourceAtPreemption}; canonicalRevision={canonical.Revision}; canonicalSource={Safe(canonical.Source)}; controllerActive={Bool(nativeControllerActive)}; cancellationRequested={Bool(nativeControllerActive)}; outcome={(nativeControllerActive ? "await_native_hands_return" : "interrupted_before_use")}; sameItemNoEffectCircuitPreserved=true; tag={CanonicalPriorityPreemptionStatusTag}; canonicalTag={VanguardCanonicalMedicalStateService.StatusTag}");
        return true;
    }

    private static VanguardMedicalNeed ResolveCanonicalBleedNeed(VanguardCanonicalMedicalEffectSnapshot canonical)
    {
        if (canonical.Badges.Contains("HB", StringComparer.OrdinalIgnoreCase)) return VanguardMedicalNeed.HeavyBleed;
        if (canonical.Badges.Contains("LB", StringComparer.OrdinalIgnoreCase)) return VanguardMedicalNeed.LightBleed;
        return VanguardMedicalNeed.None;
    }

    private static string ResolveCanonicalBleedTarget(
        VanguardMedicalNeed need,
        VanguardCanonicalMedicalEffectSnapshot canonical,
        string snapshotFallback)
    {
        string badge = need == VanguardMedicalNeed.HeavyBleed
            ? "HB"
            : need == VanguardMedicalNeed.LightBleed ? "LB" : string.Empty;
        if (!string.IsNullOrWhiteSpace(badge)
            && canonical.TargetByBadge.TryGetValue(badge, out string canonicalTarget)
            && !string.IsNullOrWhiteSpace(canonicalTarget))
        {
            return canonicalTarget;
        }
        return snapshotFallback;
    }

    private static bool IsHigherPriorityMedicalNeed(VanguardMedicalNeed candidate, VanguardMedicalNeed active)
    {
        int CandidatePriority(VanguardMedicalNeed value) => value switch
        {
            VanguardMedicalNeed.HeavyBleed => 0,
            VanguardMedicalNeed.LightBleed => 1,
            VanguardMedicalNeed.SurgeryDestroyedPart => 2,
            VanguardMedicalNeed.BlackBroken => 2,
            VanguardMedicalNeed.Fracture => 3,
            VanguardMedicalNeed.HpHeal => 4,
            VanguardMedicalNeed.PainMobility => 5,
            VanguardMedicalNeed.UntreatableVitalDestroyedPart => 6,
            _ => 99
        };
        return candidate != VanguardMedicalNeed.None && CandidatePriority(candidate) < CandidatePriority(active);
    }

    private static void TryStartNewLeases(IReadOnlyList<OperatorDecisionSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            try
            {
                TryStartNewLease(snapshot);
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(StatusTag, $"mobile medical lease tick failed operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; reason={exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private static void TryStartNewLease(OperatorDecisionSnapshot snapshot)
    {
        if (!snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            return;
        }

        long admissionDebtStarted = VanguardRuntimePerformanceGuard.Begin();
        VanguardSurgeryDebtService.UpdateFromSnapshot(snapshot, DateTimeOffset.UtcNow);
        VanguardRuntimePerformanceGuard.End("MedicalAdmissionSurgeryDebt", admissionDebtStarted);
        if (VanguardExecutionLeaseCoordinator.HasActiveLease(snapshot.BotProfileId))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record) || record.BotOwner == null)
        {
            return;
        }

        long admissionCanonicalStarted = VanguardRuntimePerformanceGuard.Begin();
        bool canonicalTruthResolved = TryResolveCanonicalEffectTruthBeforeSelection(record.BotOwner, snapshot, now, out OperatorDecisionSnapshot effectiveSnapshot);
        VanguardRuntimePerformanceGuard.End("MedicalAdmissionCanonicalTruth", admissionCanonicalStarted);
        if (!canonicalTruthResolved)
        {
            return;
        }
        snapshot = effectiveSnapshot;

        if (VanguardMedicalHandsWatchdogService.IsMedicalAdmissionBlocked(snapshot.BotProfileId, out var handsAdmissionSummary))
        {
            if (ShouldLogRecheck(snapshot.BotProfileId + "|post_terminal_hands_admission_block", now))
            {
                VanguardClientDiagnosticsLog.Info(VanguardMedicalHandsWatchdogService.StatusTag,
                    $"VANGUARD_MEDICAL_ADMISSION_BLOCKED_POST_TERMINAL_HANDS operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; {handsAdmissionSummary}; leaseStarted=false; nativeApply=false; repeatedRecovery=false; tag={VanguardMedicalHandsWatchdogService.StatusTag}");
            }
            return;
        }

        // The settle gate is advanced only by the executor admission path. Calling it while merely
        // building the board would count the same immutable snapshot twice and defeat the required
        // consecutive-snapshot confirmation after a transient EFT controller state.
        long admissionBoardStarted = VanguardRuntimePerformanceGuard.Begin();
        var board = VanguardOperatorIntentDryRunService.BuildBoard(snapshot);
        VanguardRuntimePerformanceGuard.End("MedicalAdmissionIntentBoard", admissionBoardStarted);
        bool selectedSurgeryPrepare = board.Selected.IntentKey == VanguardSurgeryCoverPrepareExecutor.IntentKey
            && board.ExecutionWindow.WindowKind == VanguardSurgeryCoverPrepareExecutor.WindowKind;
        bool selectedActiveMedical = (board.Selected.IntentKey == "MobileMedicalStabilize" && board.ExecutionWindow.WindowKind == "MobileMedicalStabilizeWindow")
            || (board.Selected.IntentKey == "StationaryMedicalStabilize" && board.ExecutionWindow.WindowKind == "StationaryMedicalFractureWindow")
            || (board.Selected.IntentKey == "StationaryMedicalSurgery" && board.ExecutionWindow.WindowKind == "StationaryMedicalSurgeryWindow");
        long admissionSchedulerStarted = VanguardRuntimePerformanceGuard.Begin();
        bool hasActivePrimary = VanguardMainIntentScheduler.TryGetActivePrimaryWindow(snapshot.BotProfileId, DateTimeOffset.UtcNow, out var activePrimaryKind, out _, out _, out var activePrimarySummary);
        bool combatPrimaryActive = VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, DateTimeOffset.UtcNow, out var combatProtectionReason);
        VanguardRuntimePerformanceGuard.End("MedicalAdmissionPrimaryScheduler", admissionSchedulerStarted);
        if (hasActivePrimary && VanguardPrimaryExecutionContract.IsGrenadeEmergencyKind(activePrimaryKind))
        {
            if (ShouldLogRecheck(snapshot.BotProfileId + "|grenade_emergency_medical_deferred", now))
            {
                VanguardClientDiagnosticsLog.Trace(VanguardGrenadeEmergencyPolicy.StatusTag,
                    () => $"VANGUARD_MEDICAL_ADMISSION_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; selectedIntent={Safe(board.Selected.IntentKey)}; primary={Safe(activePrimaryKind)}; primarySummary={Safe(activePrimarySummary)}; mutation=false; newMedicalStart=false; existingLeaseCancellationHandledByEmergencyService=true; doctrine=no_new_medical_action_inside_grenade_emergency; tag={VanguardGrenadeEmergencyPolicy.StatusTag}; medicalTag={StatusTag}");
            }
            return;
        }
        bool hasSidecarCompatiblePrimary = (!hasActivePrimary && !combatPrimaryActive)
            || ((hasActivePrimary || combatPrimaryActive)
                && !VanguardPrimaryExecutionContract.IsStationaryMedicalKind(activePrimaryKind)
                && !VanguardPrimaryExecutionContract.IsMobileMedicalKind(activePrimaryKind));
        bool mobileSidecarBaseAllowed = VanguardPrimaryExecutionContract.IsMobileMedicalSidecarCandidate(snapshot, out var mobileSidecarReason)
            && hasSidecarCompatiblePrimary;
        bool combatMicroAidAllowed = !combatPrimaryActive
            || (mobileSidecarBaseAllowed && VanguardPrimaryExecutionContract.IsCombatMicroAidOpportunity(snapshot, out mobileSidecarReason));
        bool mobileSidecarAllowed = mobileSidecarBaseAllowed && combatMicroAidAllowed;
        string mobileOpportunityKind = combatPrimaryActive
            ? "combat_micro_aid"
            : hasActivePrimary ? "movement_or_primary_sidecar" : "idle_opportunistic_medical";

        if (combatPrimaryActive && !mobileSidecarAllowed)
        {
            if (ShouldLogRecheck(snapshot.BotProfileId + "|combat_primary_medical_deferred|" + board.Selected.IntentKey, DateTimeOffset.UtcNow))
            {
                VanguardClientDiagnosticsLog.Trace(VanguardPrimaryExecutionContract.SainWindowStatusTag,
                    () => $"VANGUARD_MEDICAL_PRIMARY_DEFERRED operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; selectedIntent={board.Selected.IntentKey}; window={board.ExecutionWindow.WindowKind}; primary={Safe(activePrimaryKind)}; primarySummary={Safe(activePrimarySummary)}; sidecarReason={Safe(mobileSidecarReason)}; combatProtection={Safe(combatProtectionReason)}; mutation=false; surgeryDebtForce=false; doctrine=only_mobile_actionable_medical_may_run_beside_sain_combat; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; medicalTag={StatusTag}");
            }
            return;
        }

        if (!selectedActiveMedical && !selectedSurgeryPrepare && !mobileSidecarAllowed)
        {
            if (VanguardSurgeryDebtService.ShouldForcePrepare(snapshot, now, out var debtPrepareReason))
            {
                var debtBoard = BuildForcedSurgeryDebtBoard(snapshot, board, debtPrepareReason);
                if (VanguardSurgeryCoverPrepareExecutor.TryStart(record.BotOwner, snapshot, debtBoard, now))
                {
                    VanguardSurgeryDebtService.MarkRetrySelected(snapshot, now, debtPrepareReason);
                    return;
                }
            }

            TryRecoverBlockedMedicalController(record.BotOwner, snapshot, board.Selected.IntentKey, board.ExecutionWindow.WindowKind, now);
            return;
        }

        if (selectedSurgeryPrepare)
        {
            if (VanguardMovementAuthorityDoctrine.ShouldRejoinBeforeStationaryMedicalStart(snapshot, VanguardMovementAuthorityDoctrine.StationaryMedicalPrepareMaxOwnerDistanceMeters, out var prepareLeashReason))
            {
                if (ShouldLogRecheck(snapshot.BotProfileId + "|stationary_prepare_leash|" + prepareLeashReason, now))
                {
                    VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.StatusTag,
                        $"VANGUARD_STATIONARY_MEDICAL_PREPARE_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(prepareLeashReason)}; ownerDistance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; mutationMedical=false; mutationMovement=false; next=cohesion_rejoin_before_stationary_prepare; tag=VANGUARD_STATIONARY_MEDICAL_LEASH_STATUS; medicalTag={StatusTag}");
                }
                return;
            }

            if (VanguardSurgeryDebtService.HasDueDebt(snapshot, out var debtPrepareReason))
            {
                VanguardSurgeryDebtService.MarkRetrySelected(snapshot, now, debtPrepareReason);
            }

            VanguardSurgeryCoverPrepareExecutor.TryStart(record.BotOwner, snapshot, board, now);
            return;
        }

        long admissionPostOrbitStarted = VanguardRuntimePerformanceGuard.Begin();
        bool delayForPostOrbit = VanguardPostOrbitInventoryRecoveryService.ShouldDelayMedicalStart(record.BotOwner, snapshot, now, out var postOrbitDelayReason, out var postOrbitSummary);
        VanguardRuntimePerformanceGuard.End("MedicalAdmissionPostOrbitGate", admissionPostOrbitStarted);
        if (delayForPostOrbit)
        {
            if (ShouldLogRecheck(snapshot.BotProfileId + "|post_orbit_inventory_recheck|" + postOrbitDelayReason, now))
            {
                VanguardClientDiagnosticsLog.Info(PostOrbitInventoryRecoveryStatusTag, $"VANGUARD_MEDICAL_ACTIVE_RECHECK operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; selectedIntent={board.Selected.IntentKey}; reason={postOrbitDelayReason}; medNeed={snapshot.Medical.Need.DominantNeed}; target={snapshot.Medical.Actionability.TargetPart}; item={snapshot.Medical.Actionability.SelectedItemName}; canApply={Tri(snapshot.Medical.Actionability.CanApplyItem)}; {postOrbitSummary}; next=medical_recheck_before_action; tag={PostOrbitInventoryRecoveryStatusTag}");
            }
            return;
        }

        var excludedItemInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int surgeryDebtExclusions = VanguardSurgeryDebtService.AppendFailedItemInstanceExclusions(snapshot, excludedItemInstances, now, out var surgeryDebtExclusionSummary);
        long admissionSelectorStarted = VanguardRuntimePerformanceGuard.Begin();
        bool medicalSelectionSucceeded = VanguardMobileMedicalActionSelector.TrySelect(record.BotOwner, snapshot, excludedItemInstances, out var selection, out var reason);
        VanguardRuntimePerformanceGuard.End("MedicalAdmissionActionSelector", admissionSelectorStarted);
        if (!medicalSelectionSucceeded)
        {
            if (ShouldLogRecheck(snapshot.BotProfileId + "|" + reason, now))
            {
                VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_MEDICAL_ACTIVE_RECHECK operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; selectedIntent={board.Selected.IntentKey}; reason={reason}; medNeed={snapshot.Medical.Need.DominantNeed}; target={snapshot.Medical.Actionability.TargetPart}; item={snapshot.Medical.Actionability.SelectedItemName}; canApply={Tri(snapshot.Medical.Actionability.CanApplyItem)}; safety={snapshot.Medical.Safety.Reason}; stationarySafe={Bool(snapshot.Medical.Safety.SafeForStationaryAid)}; coverOrHold={Bool(snapshot.Medical.Safety.CoveredOrHoldingAngle)}; surgeryDebtExclusions={surgeryDebtExclusions}; surgeryDebt={Safe(surgeryDebtExclusionSummary)}");
            }
            return;
        }

        long admissionTargetHealthStarted = VanguardRuntimePerformanceGuard.Begin();
        var initialTargetHealth = TryReadInitialTargetHealth(record.BotOwner, selection.TargetPartName, out var initialTargetHealthValue)
            ? initialTargetHealthValue
            : (-1f, -1f);
        VanguardRuntimePerformanceGuard.End("MedicalAdmissionTargetHealthRead", admissionTargetHealthStarted);
        string effectSignature = BuildEffectSignature(snapshot, selection, initialTargetHealth);
        VanguardMedicalNativeAttemptMemory.AttemptRecord? nativeStartOutcome = null;
        while (!IsSurgeryNeed(selection.Need)
            && VanguardMedicalNativeAttemptMemory.IsBlocked(
                snapshot.BotProfileId, selection.Need, selection.TargetPartName, selection.ItemTemplateId,
                selection.ItemInstanceId, effectSignature, out var blockedNativeStart))
        {
            nativeStartOutcome = blockedNativeStart;
            excludedItemInstances.Add(selection.ItemInstanceId);
            if (!VanguardMobileMedicalActionSelector.TrySelect(record.BotOwner, snapshot, excludedItemInstances, out var alternative, out var alternativeReason))
            {
                if (ShouldLogRecheck(snapshot.BotProfileId + "|native_start_circuit|" + selection.Need + "|" + selection.TargetPartName + "|" + selection.ItemInstanceId, now))
                {
                    VanguardClientDiagnosticsLog.Warning(VanguardMedicalNativeAttemptMemory.StatusTag,
                        $"VANGUARD_MEDICAL_NATIVE_START_CIRCUIT_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; need={selection.Need}; target={Safe(selection.TargetPartName)}; item={Safe(selection.ItemName)}; itemInstance={Safe(selection.ItemInstanceId)}; {blockedNativeStart.Summary}; alternative={Safe(alternativeReason)}; actionStarted=false; movementMutation=false; tag={VanguardMedicalNativeAttemptMemory.StatusTag}");
                }
                return;
            }
            selection = alternative;
            initialTargetHealth = TryReadInitialTargetHealth(record.BotOwner, selection.TargetPartName, out initialTargetHealthValue)
                ? initialTargetHealthValue
                : (-1f, -1f);
            effectSignature = BuildEffectSignature(snapshot, selection, initialTargetHealth);
        }

        if (nativeStartOutcome != null)
        {
            VanguardClientDiagnosticsLog.Info(VanguardMedicalNativeAttemptMemory.StatusTag,
                $"VANGUARD_MEDICAL_NATIVE_START_ALTERNATIVE_SELECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; need={selection.Need}; target={Safe(selection.TargetPartName)}; item={Safe(selection.ItemName)}; itemInstance={Safe(selection.ItemInstanceId)}; prior={nativeStartOutcome.Summary}; excludedInstances={excludedItemInstances.Count}; tag={VanguardMedicalNativeAttemptMemory.StatusTag}");
        }
        VanguardExecutionOutcomeMemoryRecord? circuitOutcome = null;
        while (VanguardExecutionLeaseStore.IsEffectCircuitBlocked(
            snapshot.BotProfileId, selection.Need, selection.TargetPartName, selection.ItemTemplateId,
            selection.ItemInstanceId, selection.ItemResource, selection.ItemMaxResource,
            snapshot.Medical.Need.HealthPercent, initialTargetHealth.Item1, initialTargetHealth.Item2,
            now, out var blockedOutcome))
        {
            circuitOutcome = blockedOutcome;
            excludedItemInstances.Add(selection.ItemInstanceId);
            if (!VanguardMobileMedicalActionSelector.TrySelect(record.BotOwner, snapshot, excludedItemInstances, out var alternative, out var alternativeReason))
            {
                if (ShouldLogRecheck(snapshot.BotProfileId + "|effect_circuit|" + selection.Need + "|" + selection.TargetPartName + "|" + selection.ItemInstanceId, now))
                {
                    VanguardClientDiagnosticsLog.Warning(MedicalEffectCircuitBreakerStatusTag,
                        $"VANGUARD_MEDICAL_EFFECT_CIRCUIT_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; need={selection.Need}; target={Safe(selection.TargetPartName)}; item={Safe(selection.ItemName)}; itemInstance={Safe(selection.ItemInstanceId)}; stateUnchanged=true; noEffectCount={blockedOutcome.ConsecutiveNoEffectCount}; blockedUntil=state_change; alternative={Safe(alternativeReason)}; signature={Safe(effectSignature)}; actionStarted=false; movementMutation=false; doctrine=unchanged_impossible_action_must_not_loop; tag={MedicalEffectCircuitBreakerStatusTag}; stateTag={VanguardExecutionLeaseStore.StateBoundOutcomeStatusTag}");
                }
                return;
            }

            selection = alternative;
            initialTargetHealth = TryReadInitialTargetHealth(record.BotOwner, selection.TargetPartName, out initialTargetHealthValue)
                ? initialTargetHealthValue
                : (-1f, -1f);
            effectSignature = BuildEffectSignature(snapshot, selection, initialTargetHealth);
        }

        if (circuitOutcome != null)
        {
            VanguardClientDiagnosticsLog.Info(MedicalEffectCircuitBreakerStatusTag,
                $"VANGUARD_MEDICAL_EFFECT_ALTERNATIVE_SELECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; need={selection.Need}; target={Safe(selection.TargetPartName)}; item={Safe(selection.ItemName)}; itemInstance={Safe(selection.ItemInstanceId)}; priorNoEffectCount={circuitOutcome.ConsecutiveNoEffectCount}; excludedInstances={excludedItemInstances.Count}; doctrine=single_selector_pass_chooses_viable_instance; tag={VanguardExecutionLeaseStore.StateBoundOutcomeStatusTag}");
        }

        if (ShouldDeferTrivialHpHealDuringTravel(snapshot, selection, initialTargetHealth, out var trivialHealReason))
        {
            if (ShouldLogRecheck(snapshot.BotProfileId + "|trivial_hp_heal_travel|" + selection.TargetPartName, now))
            {
                VanguardClientDiagnosticsLog.Info(VanguardExecutionLeaseStore.StateBoundOutcomeStatusTag,
                    $"VANGUARD_MEDICAL_TRIVIAL_HP_HEAL_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(trivialHealReason)}; {selection.Summary}; actionStarted=false; movementMutation=false; doctrine=travel_cohesion_precedes_sub_one_hp_opportunistic_heal; tag={VanguardExecutionLeaseStore.StateBoundOutcomeStatusTag}");
            }
            return;
        }

        if (VanguardExecutionLeaseCoordinator.IsCooldownBlocked(snapshot.BotProfileId, selection.Need, selection.TargetPartName, selection.ItemTemplateId, selection.ItemInstanceId, now, out var untilUtc))
        {
            if (!VanguardSurgeryDebtService.ShouldBypassOutcomeCooldown(snapshot, selection, now, untilUtc, out _))
            {
                if (ShouldLogRecheck(snapshot.BotProfileId + "|outcome_cooldown|" + selection.Need + "|" + selection.TargetPartName + "|" + selection.ItemTemplateId, now))
                {
                    VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_MEDICAL_ACTIVE_RECHECK operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; reason=outcome_cooldown; retryAt={untilUtc:O}; {selection.Summary}");
                }
                return;
            }
        }

        if (mobileSidecarAllowed && selection.RequiresStationary)
        {
            if (ShouldLogRecheck(snapshot.BotProfileId + "|stationary_sidecar_blocked|" + selection.Need, now))
            {
                VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.StatusTag, $"VANGUARD_STATIONARY_MEDICAL_SIDECAR_BLOCKED operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; reason=stationary_selection_not_sidecar; sidecarReason={Safe(mobileSidecarReason)}; {selection.Summary}; primary={Safe(activePrimaryKind)}; doctrine=stationary_medical_is_primary_only; tag={VanguardPrimaryExecutionContract.StatusTag}; medicalTag={StatusTag}");
            }
            return;
        }

        if (selection.RequiresStationary
            && VanguardMovementAuthorityDoctrine.ShouldRejoinBeforeStationaryMedicalStart(snapshot, VanguardMovementAuthorityDoctrine.StationaryMedicalStartMaxOwnerDistanceMeters, out var stationaryLeashReason))
        {
            if (ShouldLogRecheck(snapshot.BotProfileId + "|stationary_action_leash|" + stationaryLeashReason, now))
            {
                VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.StatusTag,
                    $"VANGUARD_STATIONARY_MEDICAL_START_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; need={selection.Need}; target={Safe(selection.TargetPartName)}; reason={Safe(stationaryLeashReason)}; ownerDistance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; controllerUsing=false; mutationMedical=false; mutationMovement=false; next=cohesion_rejoin_before_stationary_action; tag=VANGUARD_STATIONARY_MEDICAL_LEASH_STATUS; medicalTag={StatusTag}");
            }
            return;
        }

        bool breakContactRequired = VanguardMovementAuthorityDoctrine.ShouldBreakContactBeforeMedical(snapshot, out var breakContactReason);
        bool mobileOpportunityCanUseExistingSafety = mobileSidecarAllowed
            && !selection.RequiresStationary
            && snapshot.Medical.Safety.SafeForMobileAid
            && !VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot);
        if (breakContactRequired && !mobileOpportunityCanUseExistingSafety)
        {
            if (combatPrimaryActive)
            {
                if (ShouldLogRecheck(snapshot.BotProfileId + "|mobile_sidecar_break_contact_blocked|" + breakContactReason, now))
                {
                    VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.SainWindowStatusTag,
                        $"VANGUARD_MOBILE_MEDICAL_DEFERRED_FOR_COMBAT operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; reason={Safe(breakContactReason)}; primary={Safe(activePrimaryKind)}; mutation=false; breakContact=false; doctrine=mobile_sidecar_never_drives_movement_or_interrupts_sain; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; medicalTag={StatusTag}");
                }
                return;
            }

            VanguardExternalAuthorityAdapter.RequestMedicalBreakContact(record.BotOwner, snapshot, breakContactReason, TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CombatNoFireRecoveryCooldownSeconds), now);
            if (ShouldLogRecheck(snapshot.BotProfileId + "|medical_break_contact|" + breakContactReason, now))
            {
                VanguardClientDiagnosticsLog.Warning(
                    VanguardMovementAuthorityDoctrine.CombatBindCohesionRecoveryStatusTag,
                    $"VANGUARD_MEDICAL_BREAK_CONTACT_REQUIRED operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; selectedIntent={board.Selected.IntentKey}; reason={breakContactReason}; medNeed={snapshot.Medical.Need.DominantNeed}; target={snapshot.Medical.Actionability.TargetPart}; item={snapshot.Medical.Actionability.SelectedItemName}; enemyCanShoot={Bool(snapshot.Medical.Safety.EnemyCanShoot)}; incomingFire={Bool(snapshot.Medical.Safety.IncomingFireRecent)}; immediateBlock={Bool(snapshot.Medical.Safety.ImmediateCombatBlock)}; threatDistance={Float(snapshot.Medical.Safety.ThreatDistance)}; action=break_contact_before_aid; tag={VanguardMovementAuthorityDoctrine.CombatBindCohesionRecoveryStatusTag}");
            }
            return;
        }

        if (breakContactRequired && mobileOpportunityCanUseExistingSafety)
        {
            VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.OpportunisticMedicalStatusTag,
                $"VANGUARD_MOBILE_AID_EXISTING_SAFETY_USED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; need={selection.Need}; target={Safe(selection.TargetPartName)}; item={Safe(selection.ItemName)}; reason={Safe(breakContactReason)}; opportunityKind={Safe(mobileOpportunityKind)}; opportunity={Safe(mobileSidecarReason)}; combat={Bool(combatPrimaryActive)}; mutationMovement=false; mutationSain=false; doctrine=mobile_aid_uses_existing_safe_pause_never_creates_break_contact; tag={VanguardPrimaryExecutionContract.OpportunisticMedicalStatusTag}; medicalTag={StatusTag}");
        }

        string selectedLog = selection.Need == Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.SurgeryDestroyedPart || selection.Need == Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.BlackBroken
            ? "VANGUARD_MEDICAL_SURGERY_ACTION_SELECTED"
            : selection.RequiresStationary ? "VANGUARD_MEDICAL_STATIONARY_FRACTURE_ACTION_SELECTED" : "VANGUARD_MEDICAL_MOBILE_ACTION_SELECTED";
        string activeSelectionLogKey = snapshot.BotProfileId + "|active_action_selected|" + selection.Need + "|" + selection.TargetPartName + "|" + selection.ItemInstanceId + "|" + board.Selected.IntentKey;
        if (ShouldLogRecheck(activeSelectionLogKey, now))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_MEDICAL_ACTIVE_ACTION_SELECTED operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; selectedIntent={(mobileSidecarAllowed ? (mobileOpportunityKind == "idle_opportunistic_medical" ? "IdleOpportunisticMedical" : "MobileMedicalSidecar") : board.Selected.IntentKey)}; opportunityKind={Safe(mobileOpportunityKind)}; score={board.Selected.FinalScore:0.00}; window={(mobileSidecarAllowed ? "MobileMedicalSidecarWindow" : board.ExecutionWindow.Summary)}; sidecar={Bool(mobileSidecarAllowed)}; sidecarReason={Safe(mobileSidecarReason)}; {selection.Summary}; heavyBefore={Bool(snapshot.Medical.Need.HasHeavyBleed)}; lightBefore={Bool(snapshot.Medical.Need.HasLightBleed)}; fractureBefore={Bool(snapshot.Medical.Need.HasFracture)}; hpBefore={snapshot.Medical.Need.HealthPercent}; hpDamageBefore={Bool(snapshot.Medical.Need.HasHpDamage)}; enemyCanShoot={Bool(snapshot.Medical.Safety.EnemyCanShoot)}; immediateBlock={Bool(snapshot.Medical.Safety.ImmediateCombatBlock)}; coveredSuppression={Bool(snapshot.Medical.Safety.CoveredSuppressionOpportunity)}; stationarySafe={Bool(snapshot.Medical.Safety.SafeForStationaryAid)}; coverOrHold={Bool(snapshot.Medical.Safety.CoveredOrHoldingAngle)}; incomingFire={Bool(snapshot.Medical.Safety.IncomingFireRecent)}; threatDistance={Float(snapshot.Medical.Safety.ThreatDistance)}; scanPromote={Bool(snapshot.Medical.Safety.ThreatScanWouldPromote)}; surgeryAreaClear={Bool(snapshot.Medical.Safety.SurgeryAreaClear)}; surgeryAreaReason={Safe(snapshot.Medical.Safety.SurgeryAreaClearReason)}; surgeryRequiresCover={Bool(snapshot.Medical.Safety.SurgeryRequiresCover)}; legacyLog={selectedLog}; logThrottled=true; surgerySafetyTag={SainLikeSurgerySafetyStatusTag}; Tag={VanguardPrimaryExecutionContract.StatusTag}");
        }

        long admissionCommitGateStarted = VanguardRuntimePerformanceGuard.Begin();
        bool medicalCommitReady = VanguardMedicalCommitReadinessGate.CanCommit(record.BotOwner, snapshot, selection, now, out var medicalCommitReadinessReason);
        VanguardRuntimePerformanceGuard.End("MedicalAdmissionCommitReadiness", admissionCommitGateStarted);
        if (!medicalCommitReady)
        {
            VanguardClientDiagnosticsLog.Diagnostic(VanguardMedicalCommitReadinessGate.StatusTag, () =>
                $"VANGUARD_MEDICAL_NATIVE_APPLY_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; need={selection.Need}; target={Safe(selection.TargetPartName)}; item={Safe(selection.ItemName)}; reason={Safe(medicalCommitReadinessReason)}; leaseStarted=false; nativeApply=false; gameplayAuthorityUnchanged=true; tag={VanguardMedicalCommitReadinessGate.StatusTag}");
            return;
        }

        var minDuration = MinDurationFor(selection);
        var maxDuration = MaxDurationFor(selection);
        var noProgressTimeout = NoProgressTimeoutFor(selection);
        var lease = new VanguardExecutionLeaseState
        {
            LeaseId = LeasePrefixFor(selection) + now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            OperatorId = snapshot.OperatorId,
            BotProfileId = snapshot.BotProfileId,
            IntentKey = mobileSidecarAllowed ? (mobileOpportunityKind == "idle_opportunistic_medical" ? "IdleOpportunisticMedical" : "MobileMedicalSidecar") : board.Selected.IntentKey,
            WindowKind = mobileSidecarAllowed ? "MobileMedicalSidecarWindow" : board.ExecutionWindow.WindowKind,
            MedicalNeed = selection.Need,
            TargetPart = selection.TargetPartName,
            ItemTemplateId = selection.ItemTemplateId,
            ItemInstanceId = selection.ItemInstanceId,
            InitialItemResource = selection.ItemResource,
            InitialItemMaxResource = selection.ItemMaxResource,
            ItemName = selection.ItemName,
            InitialHealthPercent = snapshot.Medical.Need.HealthPercent,
            InitialTargetHealth = initialTargetHealth.Item1,
            InitialTargetMaxHealth = initialTargetHealth.Item2,
            InitialNeedTargetPart = snapshot.Medical.Need.TargetPart,
            EffectSignature = effectSignature,
            SurgeryFallbackHealthPenalty = IsSurgeryNeed(selection.Need) ? ResolveSurgeryHealthPenalty(selection.Item, selection.ItemName) : -1f,
            StartedAtUtc = now,
            MinUntilUtc = now + minDuration,
            MaxUntilUtc = now + maxDuration,
            AbsoluteMaxUntilUtc = now + maxDuration + (IsSurgeryNeed(selection.Need) ? TimeSpan.FromSeconds(8.0d) : TimeSpan.FromSeconds(4.0d)),
            LastProgressAtUtc = now,
            NoProgressUntilUtc = now + noProgressTimeout,
            Attempted = true
        };

        try
        {
            bool surgery = IsSurgeryNeed(selection.Need);
            if (surgery)
            {
                var surgeryCandidateState = VanguardMedicalSurgeryTargetPolicy.EvaluateSurgeryCandidate(snapshot, out var validTargetReason);
                if (surgeryCandidateState == VanguardSurgeryCandidateState.Invalid)
                {
                    VanguardExecutionLeaseStore.RegisterOutcomeDetailed(snapshot.BotProfileId, selection.Need, selection.TargetPartName, selection.ItemTemplateId, "Failed", "invalid_surgery_candidate:" + Safe(validTargetReason), "stationary_surgery_not_started", now + RejectedCooldown);
                    VanguardClientDiagnosticsLog.Warning(VanguardMedicalSurgeryTargetPolicy.ValidSurgeryTargetsStatusTag, $"VANGUARD_STATIONARY_SURGERY_BLOCKED_INVALID_TARGET operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; reason={Safe(validTargetReason)}; need={snapshot.Medical.Need.DominantNeed}; target={Safe(selection.TargetPartName)}; item={Safe(selection.ItemName)}; noCmsSurv12OnHeadThorax=true; tag={VanguardMedicalSurgeryTargetPolicy.ValidSurgeryTargetsStatusTag}");
                    return;
                }

                if (!VanguardSurgeryAdmissionSettleGate.CanAdmit(snapshot, now, out var settleReason))
                {
                    VanguardClientDiagnosticsLog.Info(VanguardSurgeryAdmissionSettleGate.StatusTag, $"VANGUARD_STATIONARY_SURGERY_START_DEFERRED operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; reason={Safe(validTargetReason)}; settle={Safe(settleReason)}; failedOutcome=false; cooldownWritten=false; leaseStarted=false; tag={VanguardSurgeryAdmissionSettleGate.StatusTag}");
                    return;
                }

                var surgicalKit = record.BotOwner.Medecine?.SurgicalKit;
                if (surgicalKit == null)
                {
                    VanguardExecutionLeaseStore.RegisterOutcome(snapshot.BotProfileId, selection.Need, selection.TargetPartName, selection.ItemTemplateId, now + RejectedCooldown);
                    VanguardClientDiagnosticsLog.Warning(StatusTag, $"VANGUARD_EXECUTION_FAILED operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; reason=surgical_kit_controller_null; {selection.Summary}; surgeryTag={ActiveSurgeryStatusTag}; surgerySafetyTag={SainLikeSurgerySafetyStatusTag}");
                    return;
                }

                if (!TryValidateSingleSurgeryCommit(record.BotOwner, surgicalKit.Using, selection.Item, selection.TargetPart, out var surgeryCommitReason))
                {
                    VanguardClientDiagnosticsLog.Info(MedicalProcedureCompletionGateStatusTag,
                        $"VANGUARD_SURGERY_COMMIT_DEFERRED {lease.Summary}; reason={Safe(surgeryCommitReason)}; phase=before_isolation; applyCalls=0; resourceConsumed=false; leaseStarted=false; tag=VANGUARD_SURGERY_CONTROLLER_LIFECYCLE_STATUS; completionGateTag={MedicalProcedureCompletionGateStatusTag}");
                    return;
                }

                if (!VanguardMedicalIsolationController.TryBeginStationaryMedicalAction(lease, record.BotOwner, snapshot, now, out var isolationSummary))
                {
                    bool postureRetry = isolationSummary.IndexOf("posture_not_ready", StringComparison.OrdinalIgnoreCase) >= 0
                        || isolationSummary.IndexOf("stationary_settle_started", StringComparison.OrdinalIgnoreCase) >= 0
                        || isolationSummary.IndexOf("stationary_settle_waiting", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!postureRetry)
                    {
                        VanguardExecutionLeaseStore.RegisterOutcomeDetailed(snapshot.BotProfileId, selection.Need, selection.TargetPartName, selection.ItemTemplateId, "Failed", "medical_isolation_not_ready:" + isolationSummary, "stationary_action_not_started", now + RejectedCooldown);
                    }

                    VanguardClientDiagnosticsLog.Info(VanguardMedicalIsolationController.StatusTag, $"VANGUARD_EXECUTION_FAILED {lease.Summary}; reason=medical_isolation_not_ready; {isolationSummary}; cooldownWritten={Bool(!postureRetry)}; next=retry_after_isolation_or_posture; tag={VanguardMedicalIsolationController.StatusTag}; postureRetryTag={MedicalPostureRetryStatusTag}");
                    return;
                }

                if (!TryValidateSingleSurgeryCommit(record.BotOwner, surgicalKit.Using, selection.Item, selection.TargetPart, out surgeryCommitReason))
                {
                    VanguardMedicalIsolationController.ReleaseForLease(lease, record.BotOwner, now, "stationary_surgery_commit_changed_before_start:" + surgeryCommitReason);
                    VanguardClientDiagnosticsLog.Info(MedicalProcedureCompletionGateStatusTag,
                        $"VANGUARD_SURGERY_COMMIT_DEFERRED {lease.Summary}; reason={Safe(surgeryCommitReason)}; phase=before_lease_start; applyCalls=0; resourceConsumed=false; leaseStarted=false; tag=VANGUARD_SURGERY_CONTROLLER_LIFECYCLE_STATUS; completionGateTag={MedicalProcedureCompletionGateStatusTag}");
                    return;
                }

                if (!VanguardExecutionLeaseStore.TryStart(lease))
                {
                    VanguardMedicalIsolationController.ReleaseForLease(lease, record.BotOwner, now, "stationary_surgery_lease_start_rejected");
                    return;
                }

                surgicalKit.CurUsingMeds = selection.Item;
                surgicalKit.Nullable_0 = selection.TargetPart;
                VanguardMedicalIsolationController.RefreshStationaryMedicalHold(lease, record.BotOwner, snapshot, now, "surgery_before_apply", out var holdSummary);
                lease.LastSurgeryApplyAttemptAtUtc = now;
                lease.SurgeryApplyAttemptCount = 1;
                VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_EXECUTION_LEASE_STARTED {lease.Summary}; min={minDuration.TotalSeconds:0.00}; max={maxDuration.TotalSeconds:0.00}; noProgress={noProgressTimeout.TotalSeconds:0.00}; postUseRecheck={PostUseRecheckWindowForLease(lease).TotalSeconds:0.00}; movementAllowed=false; followAllowed=false; combatAllowed=false; medicalIsolation=true; releaseCondition=target_resolved_or_true_threat_or_controller_terminal_or_max_window; {isolationSummary}; {holdSummary}; activeMedicalTag={ActiveMedicalHpFractureStatusTag}; effectGuard={MedicalEffectGuardStatusTag}; surgeryTag={ActiveSurgeryStatusTag}; surgerySafetyTag={SainLikeSurgerySafetyStatusTag}; isolationTag={VanguardMedicalIsolationController.StatusTag}; inventoryRefreshTag={InventoryRefreshStatusTag}; authorityHoldTag={MedicalAuthorityHoldStatusTag}; coverCommitTag={MedicalCoverCommitStatusTag}; hardProcedureTag={MedicalHardProcedureAuthorityStatusTag}; completionGateTag={MedicalProcedureCompletionGateStatusTag}; directChainTag={MedicalSurgeryDirectChainStatusTag}; sameProcedureStartTag={MedicalSurgerySameProcedureStartStatusTag}; surgeryPersistenceTag={MedicalSurgeryPersistenceStatusTag}; postureRetryTag={MedicalPostureRetryStatusTag}; surgeryHardHoldTag={MedicalSurgeryHardHoldStatusTag}; orbitLootFreezeTag={MedicalOrbitLootFreezeDuringSurgeryStatusTag}; surgeryDebtTag={MedicalSurgeryDebtRetryStatusTag}");
                surgicalKit.ApplyToCurrentPart(() => ObserveSurgeryControllerCallback(lease.BotProfileId, lease.LeaseId));
                VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_STATIONARY_SURGERY_STARTED {lease.Summary}; method=BotSurgicalKit.ApplyToCurrentPart; exactItem=true; exactTarget=true; lane={selection.ExecutionLane}; cmsSurv12=true; medicalIsolation=true; authorityHold=true; coverCommit=true; hardProcedureAuthority=true; releaseCondition=target_resolved_or_true_threat_or_controller_terminal_or_max_window; surgeryTag={ActiveSurgeryStatusTag}; surgerySafetyTag={SainLikeSurgerySafetyStatusTag}; isolationTag={VanguardMedicalIsolationController.StatusTag}; inventoryRefreshTag={InventoryRefreshStatusTag}; authorityHoldTag={MedicalAuthorityHoldStatusTag}; coverCommitTag={MedicalCoverCommitStatusTag}; hardProcedureTag={MedicalHardProcedureAuthorityStatusTag}; completionGateTag={MedicalProcedureCompletionGateStatusTag}; directChainTag={MedicalSurgeryDirectChainStatusTag}; sameProcedureStartTag={MedicalSurgerySameProcedureStartStatusTag}; surgeryPersistenceTag={MedicalSurgeryPersistenceStatusTag}; postureRetryTag={MedicalPostureRetryStatusTag}; surgeryHardHoldTag={MedicalSurgeryHardHoldStatusTag}; orbitLootFreezeTag={MedicalOrbitLootFreezeDuringSurgeryStatusTag}; surgeryDebtTag={MedicalSurgeryDebtRetryStatusTag}");
                VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_MEDICAL_SURGERY_ACTION_ATTEMPTED {lease.Summary}; method=BotSurgicalKit.ApplyToCurrentPart; exactItem=true; exactTarget=true; lane={selection.ExecutionLane}; cmsSurv12=true; medicalIsolation=true; surgeryTag={ActiveSurgeryStatusTag}; surgerySafetyTag={SainLikeSurgerySafetyStatusTag}; isolationTag={VanguardMedicalIsolationController.StatusTag}; inventoryRefreshTag={InventoryRefreshStatusTag}; authorityHoldTag={MedicalAuthorityHoldStatusTag}; coverCommitTag={MedicalCoverCommitStatusTag}; surgeryDebtTag={MedicalSurgeryDebtRetryStatusTag}");
                return;
            }

            var firstAid = record.BotOwner.Medecine?.FirstAid;
            if (firstAid == null)
            {
                VanguardExecutionLeaseStore.RegisterOutcome(snapshot.BotProfileId, selection.Need, selection.TargetPartName, selection.ItemTemplateId, now + RejectedCooldown);
                VanguardClientDiagnosticsLog.Warning(StatusTag, $"VANGUARD_EXECUTION_FAILED operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; reason=first_aid_controller_null; {selection.Summary}");
                return;
            }

            if (!TryValidateSingleFirstAidCommit(record.BotOwner, firstAid.Using, firstAid.IsBleeding, selection, out var firstAidCommitReason))
            {
                VanguardClientDiagnosticsLog.Diagnostic(VanguardMedicalCommitReadinessGate.StatusTag, () =>
                    $"VANGUARD_FIRST_AID_COMMIT_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; need={selection.Need}; target={Safe(selection.TargetPartName)}; item={Safe(selection.ItemName)}; reason={Safe(firstAidCommitReason)}; leaseStarted=false; nativeApply=false; failedOutcome=false; tag={VanguardMedicalCommitReadinessGate.StatusTag}");
                return;
            }

            if (!VanguardExecutionLeaseStore.TryStart(lease))
            {
                return;
            }

            firstAid.CurUsingMeds = selection.Item;
            firstAid.Nullable_0 = selection.TargetPart;
            VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_EXECUTION_LEASE_STARTED {lease.Summary}; min={minDuration.TotalSeconds:0.00}; max={maxDuration.TotalSeconds:0.00}; noProgress={noProgressTimeout.TotalSeconds:0.00}; postUseRecheck={PostUseRecheckWindowForLease(lease).TotalSeconds:0.00}; movementAllowed={Bool(selection.MovementAllowed)}; followAllowed={Bool(selection.FollowAllowed)}; combatAllowed=false; activeMedicalTag={ActiveMedicalHpFractureStatusTag}; effectGuard={MedicalEffectGuardStatusTag}; surgeryTag={ActiveSurgeryStatusTag}; surgerySafetyTag={SainLikeSurgerySafetyStatusTag}");
            firstAid.TryApplyToCurrentPart();
            VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_MEDICAL_ACTIVE_ACTION_ATTEMPTED {lease.Summary}; method=BotFirstAidClass.TryApplyToCurrentPart; exactItem=true; exactTarget=true; lane={selection.ExecutionLane}");
        }
        catch (Exception exception)
        {
            VanguardMedicalExecutionResultBridge.Publish(
                lease,
                VanguardMedicalActionOutcomeKind.Failed,
                "AttemptException:" + exception.GetType().Name,
                "exception=" + exception.GetType().Name + ":" + Safe(exception.Message),
                now);
            VanguardExecutionLeaseStore.Release(snapshot.BotProfileId);
            if (IsSurgeryNeed(selection.Need))
            {
                VanguardMedicalIsolationController.ReleaseForLease(lease, record.BotOwner, now, "stationary_surgery_attempt_exception:" + exception.GetType().Name);
            }

            VanguardExecutionLeaseStore.RegisterOutcome(snapshot.BotProfileId, selection.Need, selection.TargetPartName, selection.ItemTemplateId, now + RejectedCooldown);
            VanguardClientDiagnosticsLog.Warning(StatusTag, $"VANGUARD_EXECUTION_FAILED {lease.Summary}; reason=AttemptException; exception={exception.GetType().Name}: {exception.Message}");
        }
    }


    public static bool TryStartStationarySurgeryFromPrepare(VanguardExecutionLeaseState prepareLease, BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason, out string summary)
    {
        summary = "directChain=false";
        if (prepareLease == null || botOwner == null || !snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            summary = "directChain=false;reason=missing_prepare_lease_or_bot_or_snapshot";
            return false;
        }

        if (!IsSurgeryNeed(snapshot.Medical.Need.DominantNeed))
        {
            summary = "directChain=false;reason=need_not_surgery";
            return false;
        }

        if (VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out var combatProtectionReason))
        {
            summary = "directChain=false;reason=sain_combat_primary_protected:" + Safe(combatProtectionReason);
            return false;
        }

        bool rejoinRequired = VanguardMovementAuthorityDoctrine.ShouldRejoinBeforeStationaryMedicalStart(
            snapshot,
            VanguardMovementAuthorityDoctrine.StationaryMedicalStartMaxOwnerDistanceMeters,
            out var directChainLeashReason);
        if (rejoinRequired)
        {
            if (!VanguardSurgeryCoverPrepareExecutor.CanFinishPreparedSurgeryBeforeRejoin(prepareLease, snapshot, out var preparedLeashBypassReason))
            {
                summary = "directChain=false;reason=stationary_medical_leash:" + Safe(directChainLeashReason);
                return false;
            }

            prepareLease.SurgeryPrepareOwnerLeashBypassed = true;
            if (ShouldLogRecheck(snapshot.BotProfileId + "|owner_leash_bypass|" + prepareLease.LeaseId, now))
            {
                VanguardClientDiagnosticsLog.Info(VanguardSurgeryCoverPrepareExecutor.SurgeryPreparationConvergenceStatusTag,
                    $"VANGUARD_SURGERY_DIRECT_CHAIN_OWNER_LEASH_BYPASSED {prepareLease.Summary}; reason={Safe(preparedLeashBypassReason)}; leash={Safe(directChainLeashReason)}; ownerDistance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; preparedProcedure=true; trueThreat=false; next=stationary_surgery_same_procedure; tag={VanguardSurgeryCoverPrepareExecutor.SurgeryPreparationConvergenceStatusTag}");
            }
        }

        // Runtime invariant: the prepare lease has already established target, item capability, cover and
        // patient authority. Do not let a stale read-only CanApply=false snapshot hold the
        // Operator crouched for the full isolation window. Preparation capability still
        // rejects invalid targets and busy hands; the exact refreshed item is then validated
        // by TryBuildDirectSurgerySelection through HealthController.CanApplyItem.
        var directCandidateState = VanguardMedicalSurgeryTargetPolicy.EvaluateSurgeryPreparationCandidate(snapshot, out var validTargetReason);
        if (directCandidateState == VanguardSurgeryCandidateState.Invalid)
        {
            summary = "directChain=false;reason=invalid_surgery_target_or_item:" + Safe(validTargetReason);
            if (ShouldLogRecheck(snapshot.BotProfileId + "|surgery_direct_chain_invalid|" + validTargetReason, now))
            {
                VanguardClientDiagnosticsLog.Warning(VanguardMedicalSurgeryTargetPolicy.ValidSurgeryTargetsStatusTag, $"VANGUARD_MEDICAL_SURGERY_DIRECT_CHAIN_BLOCKED_INVALID_TARGET operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; reason={Safe(validTargetReason)}; need={snapshot.Medical.Need.DominantNeed}; target={Safe(snapshot.Medical.Actionability.TargetPart)}; item={Safe(snapshot.Medical.Actionability.SelectedItemName)}; throttled=true; noCmsSurv12OnHeadThorax=true; tag={VanguardMedicalSurgeryTargetPolicy.ValidSurgeryTargetsStatusTag}; directChainTag={MedicalSurgeryDirectChainStatusTag}");
            }
            return false;
        }

        if (directCandidateState == VanguardSurgeryCandidateState.Transient)
        {
            summary = "directChain=false;reason=prepared_surgery_hands_transient:" + Safe(validTargetReason) + ";preparePreserved=true";
            return false;
        }

        bool coverGrant = VanguardSurgeryCoverPrepareExecutor.HasRecentVanguardSurgeryCoverGrant(snapshot, out var coverGrantReason);
        bool coverCommittedByIsolation = VanguardMedicalIsolationController.HasCompatibleStationaryIsolation(
            snapshot.BotProfileId,
            prepareLease.TargetPart,
            prepareLease.ItemTemplateId,
            now,
            out var directChainIsolationReason);
        bool physicalCoverCommitLost = !coverGrant
            && (coverGrantReason.StartsWith("grant_outside_retention:", StringComparison.OrdinalIgnoreCase)
                || coverGrantReason.StartsWith("grant_invalidated_", StringComparison.OrdinalIgnoreCase));
        if ((!coverGrant && !coverCommittedByIsolation) || physicalCoverCommitLost)
        {
            summary = "directChain=false;reason=cover_commit_or_live_isolation_missing;grant="
                + Safe(coverGrantReason)
                + ";physicalCoverCommitLost=" + Bool(physicalCoverCommitLost)
                + ";phase=" + Safe(prepareLease.MedicalIsolationPhase)
                + ";isolation=" + Safe(directChainIsolationReason);
            return false;
        }

        if (snapshot.Medical.Safety.EnemyCanShoot || snapshot.Medical.Safety.IncomingFireRecent || snapshot.Medical.Safety.ImmediateCombatBlock)
        {
            summary = "directChain=false;reason=hard_threat_before_direct_surgery;enemyCanShoot=" + Bool(snapshot.Medical.Safety.EnemyCanShoot)
                + ";incomingFire=" + Bool(snapshot.Medical.Safety.IncomingFireRecent)
                + ";immediate=" + Bool(snapshot.Medical.Safety.ImmediateCombatBlock);
            return false;
        }

        if (!TryBuildDirectSurgerySelection(botOwner, snapshot, prepareLease.TargetPart, out var selection, out var selectionReason))
        {
            summary = "directChain=false;reason=selection_failed:" + Safe(selectionReason) + ";grant=" + Safe(coverGrantReason) + ";phase=" + Safe(prepareLease.MedicalIsolationPhase);
            return false;
        }

        if (!VanguardMedicalCommitReadinessGate.CanCommit(botOwner, snapshot, selection, now, out var commitReadinessReason))
        {
            summary = "directChain=false;reason=commit_readiness_pending:" + Safe(commitReadinessReason) + ";preparePreserved=true";
            VanguardClientDiagnosticsLog.Diagnostic(VanguardMedicalCommitReadinessGate.StatusTag, () =>
                $"VANGUARD_SURGERY_DIRECT_COMMIT_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(selection.TargetPartName)}; item={Safe(selection.ItemName)}; reason={Safe(commitReadinessReason)}; prepareLease={Safe(prepareLease.LeaseId)}; leaseReplaced=false; nativeApply=false; preparePreserved=true; tag={VanguardMedicalCommitReadinessGate.StatusTag}");
            return false;
        }

        var minDuration = MinDurationFor(selection);
        var maxDuration = MaxDurationFor(selection);
        var noProgressTimeout = NoProgressTimeoutFor(selection);
        var initialTargetHealth = TryReadInitialTargetHealth(botOwner, selection.TargetPartName, out var initialTargetHealthValue)
            ? initialTargetHealthValue
            : (-1f, -1f);

        var surgeryLease = new VanguardExecutionLeaseState
        {
            LeaseId = "med-surgery-direct-" + now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            OperatorId = snapshot.OperatorId,
            BotProfileId = snapshot.BotProfileId,
            IntentKey = "StationaryMedicalSurgery",
            WindowKind = "StationaryMedicalSurgeryWindow",
            MedicalNeed = selection.Need,
            TargetPart = selection.TargetPartName,
            ItemTemplateId = selection.ItemTemplateId,
            ItemInstanceId = selection.ItemInstanceId,
            InitialItemResource = selection.ItemResource,
            InitialItemMaxResource = selection.ItemMaxResource,
            ItemName = selection.ItemName,
            InitialHealthPercent = snapshot.Medical.Need.HealthPercent,
            InitialTargetHealth = initialTargetHealth.Item1,
            InitialTargetMaxHealth = initialTargetHealth.Item2,
            InitialNeedTargetPart = snapshot.Medical.Need.TargetPart,
            SurgeryFallbackHealthPenalty = ResolveSurgeryHealthPenalty(selection.Item, selection.ItemName),
            StartedAtUtc = now,
            MinUntilUtc = now + minDuration,
            MaxUntilUtc = now + maxDuration,
            AbsoluteMaxUntilUtc = now + maxDuration + (IsSurgeryNeed(selection.Need) ? TimeSpan.FromSeconds(8.0d) : TimeSpan.FromSeconds(4.0d)),
            LastProgressAtUtc = now,
            NoProgressUntilUtc = now + noProgressTimeout,
            Attempted = true
        };

        var surgicalKit = botOwner.Medecine?.SurgicalKit;
        if (surgicalKit == null)
        {
            summary = "directChain=false;reason=surgical_kit_controller_null";
            return false;
        }

        if (!TryValidateSingleSurgeryCommit(botOwner, surgicalKit.Using, selection.Item, selection.TargetPart, out var surgeryCommitReason))
        {
            summary = "directChain=false;reason=surgery_commit_not_ready:" + Safe(surgeryCommitReason);
            VanguardClientDiagnosticsLog.Info(MedicalProcedureCompletionGateStatusTag,
                $"VANGUARD_SURGERY_COMMIT_DEFERRED {prepareLease.Summary}; reason={Safe(surgeryCommitReason)}; phase=direct_chain_before_isolation; applyCalls=0; resourceConsumed=false; preparePreserved=true; tag=VANGUARD_SURGERY_CONTROLLER_LIFECYCLE_STATUS; completionGateTag={MedicalProcedureCompletionGateStatusTag}");
            return false;
        }

        // Confirm that the exact prepare generation is still active before mutating isolation.
        // Runtime is main-threaded, but this protects against another terminal path in the same
        // Update cycle and prevents an orphan stationary hold from being opened for a stale lease.
        if (!VanguardExecutionLeaseStore.TryGetActive(prepareLease.BotProfileId, out var activePrepareLease)
            || !string.Equals(activePrepareLease.LeaseId, prepareLease.LeaseId, StringComparison.OrdinalIgnoreCase))
        {
            summary = "directChain=false;prepareReleased=false;terminalPrepare=false;reason=prepare_generation_not_active";
            return false;
        }

        if (!VanguardMedicalIsolationController.TryBeginStationaryMedicalAction(surgeryLease, botOwner, snapshot, now, out var isolationSummary))
        {
            summary = "directChain=false;reason=isolation_not_ready;" + isolationSummary;
            return false;
        }

        if (!VanguardMedicalIsolationController.RefreshStationaryMedicalHold(surgeryLease, botOwner, snapshot, now, "direct_chain_pre_apply:" + Safe(reason), out var holdSummary))
        {
            summary = "directChain=false;reason=stationary_hold_not_ready;" + holdSummary;
            VanguardMedicalIsolationController.BeginOrUpdatePrepareIsolation(prepareLease, botOwner, snapshot, now);
            return false;
        }

        if (!TryValidateSingleSurgeryCommit(botOwner, surgicalKit.Using, selection.Item, selection.TargetPart, out surgeryCommitReason))
        {
            summary = "directChain=false;reason=surgery_commit_changed_before_atomic_handoff:" + Safe(surgeryCommitReason);
            VanguardMedicalIsolationController.BeginOrUpdatePrepareIsolation(prepareLease, botOwner, snapshot, now);
            VanguardClientDiagnosticsLog.Info(MedicalProcedureCompletionGateStatusTag,
                $"VANGUARD_SURGERY_COMMIT_DEFERRED {prepareLease.Summary}; reason={Safe(surgeryCommitReason)}; phase=direct_chain_before_atomic_handoff; applyCalls=0; resourceConsumed=false; preparePreserved=true; tag=VANGUARD_SURGERY_CONTROLLER_LIFECYCLE_STATUS; completionGateTag={MedicalProcedureCompletionGateStatusTag}");
            return false;
        }

        // The runtime atomic handoff: replace the exact prepare lease with the exact surgery lease under
        // the lease-store lock. There is no observer-visible gap and no later lease can be overwritten.
        if (!VanguardExecutionLeaseStore.TryReplace(prepareLease.BotProfileId, prepareLease.LeaseId, surgeryLease))
        {
            summary = "directChain=false;prepareReleased=false;terminalPrepare=false;reason=atomic_lease_replace_rejected";
            // Do not release global medical authority here: another terminal path may already own it.
            // Restore the prepare generation only when it is still the active lease.
            if (VanguardExecutionLeaseStore.TryGetActive(prepareLease.BotProfileId, out var stillActive)
                && string.Equals(stillActive.LeaseId, prepareLease.LeaseId, StringComparison.OrdinalIgnoreCase))
            {
                VanguardMedicalIsolationController.BeginOrUpdatePrepareIsolation(prepareLease, botOwner, snapshot, now);
            }
            return false;
        }

        surgicalKit.CurUsingMeds = selection.Item;
        surgicalKit.Nullable_0 = selection.TargetPart;

        VanguardMedicalExecutionResultBridge.Publish(
            prepareLease,
            VanguardMedicalActionOutcomeKind.Completed,
            "direct_chain_prepare_completed:" + Safe(reason),
            "directChainHandoff=atomic;nextLease=" + Safe(surgeryLease.LeaseId),
            now);
        VanguardExecutionLeaseStore.RegisterOutcomeDetailed(prepareLease.BotProfileId, prepareLease.MedicalNeed, prepareLease.TargetPart, prepareLease.ItemTemplateId, "Completed", "direct_chain_prepare_completed:" + Safe(reason), "direct_chain_to_stationary_surgery_atomic", now);
        VanguardClientDiagnosticsLog.Info(MedicalSurgeryDirectChainStatusTag, $"VANGUARD_EXECUTION_COMPLETED {prepareLease.Summary}; outcome=Completed; reason=direct_chain_started_stationary_surgery:{Safe(reason)}; elapsed={(now - prepareLease.StartedAtUtc).TotalSeconds:0.00}; retryAfter=0.00; patientOnly=true; next=StationaryMedicalSurgery; noSchedulerGap=true; prepareLeaseReleased=true; sameProcedure=true; tag={MedicalSurgeryDirectChainStatusTag}; sameProcedureStartTag={MedicalSurgerySameProcedureStartStatusTag}; hardProcedureTag={MedicalHardProcedureAuthorityStatusTag}; completionGateTag={MedicalProcedureCompletionGateStatusTag}");
        VanguardClientDiagnosticsLog.Info(SurgeryTerminalItemCommitStatusTag,
            $"VANGUARD_SURGERY_PREPARE_TERMINAL_CONFIRMED {prepareLease.Summary}; outcome=Completed; reason=direct_chain_started_stationary_surgery:{Safe(reason)}; terminalMode=atomic_replace; outcomePublished=true; nextLease={Safe(surgeryLease.LeaseId)}; prepareLeaseStillActive=false; debtTransferredToSurgery=true; noSchedulerGap=true; tag={SurgeryTerminalItemCommitStatusTag}");
        VanguardClientDiagnosticsLog.Info(MedicalSurgerySameProcedureStartStatusTag, $"VANGUARD_MEDICAL_SURGERY_SAME_PROCEDURE_START {surgeryLease.Summary}; sourcePrepareLease={Safe(prepareLease.LeaseId)}; reason={Safe(reason)}; grant={Safe(coverGrantReason)}; phase={Safe(prepareLease.MedicalIsolationPhase)}; {selection.Summary}; {isolationSummary}; {holdSummary}; max={maxDuration.TotalSeconds:0.00}; noProgress={noProgressTimeout.TotalSeconds:0.00}; releaseCondition=target_resolved_or_true_threat_or_controller_terminal_or_max_window; patientOnly=true; directChain=true; noSchedulerGap=true; tag={MedicalSurgerySameProcedureStartStatusTag}; directChainTag={MedicalSurgeryDirectChainStatusTag}; authorityHoldTag={MedicalAuthorityHoldStatusTag}; coverCommitTag={MedicalCoverCommitStatusTag}; hardProcedureTag={MedicalHardProcedureAuthorityStatusTag}");
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_EXECUTION_LEASE_STARTED {surgeryLease.Summary}; min={minDuration.TotalSeconds:0.00}; max={maxDuration.TotalSeconds:0.00}; noProgress={noProgressTimeout.TotalSeconds:0.00}; postUseRecheck={PostUseRecheckWindowForLease(surgeryLease).TotalSeconds:0.00}; movementAllowed=false; followAllowed=false; combatAllowed=false; medicalIsolation=true; directChain=true; releaseCondition=target_resolved_or_true_threat_or_controller_terminal_or_max_window; {isolationSummary}; {holdSummary}; activeMedicalTag={ActiveMedicalHpFractureStatusTag}; effectGuard={MedicalEffectGuardStatusTag}; surgeryTag={ActiveSurgeryStatusTag}; surgerySafetyTag={SainLikeSurgerySafetyStatusTag}; isolationTag={VanguardMedicalIsolationController.StatusTag}; inventoryRefreshTag={InventoryRefreshStatusTag}; authorityHoldTag={MedicalAuthorityHoldStatusTag}; coverCommitTag={MedicalCoverCommitStatusTag}; hardProcedureTag={MedicalHardProcedureAuthorityStatusTag}; completionGateTag={MedicalProcedureCompletionGateStatusTag}; directChainTag={MedicalSurgeryDirectChainStatusTag}; surgeryPersistenceTag={MedicalSurgeryPersistenceStatusTag}; postureRetryTag={MedicalPostureRetryStatusTag}; surgeryHardHoldTag={MedicalSurgeryHardHoldStatusTag}; orbitLootFreezeTag={MedicalOrbitLootFreezeDuringSurgeryStatusTag}; surgeryDebtTag={MedicalSurgeryDebtRetryStatusTag}");
        surgeryLease.LastSurgeryApplyAttemptAtUtc = now;
        surgeryLease.SurgeryApplyAttemptCount = 1;
        surgicalKit.ApplyToCurrentPart(() => ObserveSurgeryControllerCallback(surgeryLease.BotProfileId, surgeryLease.LeaseId));
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_STATIONARY_SURGERY_STARTED {surgeryLease.Summary}; method=BotSurgicalKit.ApplyToCurrentPart; exactItem=true; exactTarget=true; lane={selection.ExecutionLane}; cmsSurv12=true; medicalIsolation=true; authorityHold=true; coverCommit=true; hardProcedureAuthority=true; directChain=true; releaseCondition=target_resolved_or_true_threat_or_controller_terminal_or_max_window; surgeryTag={ActiveSurgeryStatusTag}; surgerySafetyTag={SainLikeSurgerySafetyStatusTag}; isolationTag={VanguardMedicalIsolationController.StatusTag}; inventoryRefreshTag={InventoryRefreshStatusTag}; authorityHoldTag={MedicalAuthorityHoldStatusTag}; coverCommitTag={MedicalCoverCommitStatusTag}; hardProcedureTag={MedicalHardProcedureAuthorityStatusTag}; completionGateTag={MedicalProcedureCompletionGateStatusTag}; directChainTag={MedicalSurgeryDirectChainStatusTag}; sameProcedureStartTag={MedicalSurgerySameProcedureStartStatusTag}; surgeryPersistenceTag={MedicalSurgeryPersistenceStatusTag}; postureRetryTag={MedicalPostureRetryStatusTag}; surgeryHardHoldTag={MedicalSurgeryHardHoldStatusTag}; orbitLootFreezeTag={MedicalOrbitLootFreezeDuringSurgeryStatusTag}; surgeryDebtTag={MedicalSurgeryDebtRetryStatusTag}");
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_MEDICAL_SURGERY_ACTION_ATTEMPTED {surgeryLease.Summary}; method=BotSurgicalKit.ApplyToCurrentPart; exactItem=true; exactTarget=true; lane={selection.ExecutionLane}; cmsSurv12=true; medicalIsolation=true; directChain=true; surgeryTag={ActiveSurgeryStatusTag}; surgerySafetyTag={SainLikeSurgerySafetyStatusTag}; isolationTag={VanguardMedicalIsolationController.StatusTag}; inventoryRefreshTag={InventoryRefreshStatusTag}; authorityHoldTag={MedicalAuthorityHoldStatusTag}; coverCommitTag={MedicalCoverCommitStatusTag}; directChainTag={MedicalSurgeryDirectChainStatusTag}");
        summary = "directChain=true;started=StationaryMedicalSurgery;lease=" + Safe(surgeryLease.LeaseId) + ";" + selection.Summary + ";" + isolationSummary + ";" + holdSummary;
        return true;
    }

    private static void ObserveSequentialSurgeryBoundaryIfNeeded(VanguardExecutionLeaseState completedLease, EFT.BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, VanguardMedicalActionProgressSnapshot progress, string sourceReason)
    {
        if (!IsSurgeryNeed(completedLease.MedicalNeed)
            || botOwner == null
            || !snapshot.Alive
            || !progress.TargetResolved
            || progress.NeedResolved
            || !progress.NeedStillPresent
            || string.IsNullOrWhiteSpace(progress.CurrentNeedTargetPart)
            || string.Equals(progress.CurrentNeedTargetPart, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(progress.CurrentNeedTargetPart, completedLease.TargetPart, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Runtime invariant: each destroyed body part is a distinct procedure generation. The completed lease
        // must first settle and release EFT hands/controller state. A fresh decision snapshot may
        // then admit the next target through the normal prepare-cover -> surgery path. Keeping a
        // single controller/lease across two body parts caused stale SurgicalKit.Using states and
        // long false-progress windows. This method intentionally never starts the next surgery.
        string logKey = "sequential_boundary|" + Safe(completedLease.LeaseId) + "|" + Safe(progress.CurrentNeedTargetPart);
        if (ShouldLogRecheck(logKey, now))
        {
            VanguardClientDiagnosticsLog.Info(
                VanguardMedicalCohesionStatusTags.SequentialSurgeryBoundary,
                $"VANGUARD_SEQUENTIAL_SURGERY_BOUNDARY {completedLease.Summary}; source={Safe(sourceReason)}; completedTarget={Safe(completedLease.TargetPart)}; nextTarget={Safe(progress.CurrentNeedTargetPart)}; controllerMustSettle=true; leaseMustComplete=true; isolationMustRelease=true; next=fresh_snapshot_then_prepare_cover; immediateRetarget=false; atomicLeaseReplace=false; tag={VanguardMedicalCohesionStatusTags.SequentialSurgeryBoundary}; previousTag={MedicalSequentialSurgeryChainStatusTag}");
        }

        return;
    }


    private static bool TryBuildDirectSurgerySelection(BotOwner botOwner, OperatorDecisionSnapshot snapshot, string expectedTargetPartName, out VanguardMobileMedicalActionSelection selection, out string reason)
    {
        if (!VanguardMobileMedicalActionSelector.TrySelectPreparedSurgery(botOwner, snapshot, expectedTargetPartName, out selection, out reason))
        {
            return false;
        }

        reason = "selected_prepared_surgery_via_central_selector";
        return true;
    }


    private static bool TryRecoverBlockedMedicalController(EFT.BotOwner botOwner, OperatorDecisionSnapshot snapshot, string selectedIntent, string windowKind, DateTimeOffset now)
    {
        if (!snapshot.Medical.Actionability.AnyMedicineUsing)
        {
            ControllerBusySinceByBot.Remove(snapshot.BotProfileId);
            return false;
        }

        var need = snapshot.Medical.Need.DominantNeed;
        if (!IsControllerRecoveryCandidate(need))
        {
            return false;
        }

        if (!string.Equals(selectedIntent, "AwaitMedicalControllerReadinessReadOnly", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(snapshot.Medical.Plan.NextStep, "AwaitMedicalControllerReadinessReadOnly", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(snapshot.Medical.Plan.ActionabilityGate, "medicine_controller_busy", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ControllerBusySinceByBot.TryGetValue(snapshot.BotProfileId, out var busySince))
        {
            ControllerBusySinceByBot[snapshot.BotProfileId] = now;
            if (ShouldLogRecheck(snapshot.BotProfileId + "|controller_busy_recovery_wait|" + need, now))
            {
                VanguardClientDiagnosticsLog.Info(ControllerRecoveryStatusTag, $"VANGUARD_MEDICAL_CONTROLLER_RECOVERY_WAIT operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; need={need}; target={Safe(snapshot.Medical.Actionability.TargetPart)}; item={Safe(snapshot.Medical.Actionability.SelectedItemName)}; selected={Safe(selectedIntent)}; window={Safe(windowKind)}; reason=controller_busy_initial_observation; hp={snapshot.Medical.Need.HealthPercent}; canApply={Tri(snapshot.Medical.Actionability.CanApplyItem)}; firstAidUsing={Bool(snapshot.Medical.Actionability.FirstAidUsing)}; surgicalKitUsing={Bool(snapshot.Medical.Actionability.SurgicalKitUsing)}; anyMedicineUsing={Bool(snapshot.Medical.Actionability.AnyMedicineUsing)}; tag={ControllerRecoveryStatusTag}");
            }

            return true;
        }

        var delay = ControllerBusyRecoveryDelayFor(need);
        if (now - busySince < delay)
        {
            return true;
        }

        bool observed = VanguardMedicalHandsWatchdogService.ObserveForeignMedicalActivityWithoutLease(
            botOwner,
            snapshot,
            "controller_busy_without_active_vanguard_lease:" + Safe(selectedIntent) + ":" + Safe(windowKind),
            now,
            out string watchdogSummary);
        VanguardClientDiagnosticsLog.Info(
            VanguardMedicalHandsWatchdogService.StatusTag,
            $"VANGUARD_MEDICAL_CONTROLLER_FOREIGN_ACTIVITY_PRESERVED operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; need={need}; target={Safe(snapshot.Medical.Actionability.TargetPart)}; item={Safe(snapshot.Medical.Actionability.SelectedItemName)}; selected={Safe(selectedIntent)}; window={Safe(windowKind)}; delay={delay.TotalSeconds:0.00}; busyFor={(now - busySince).TotalSeconds:0.00}; hp={snapshot.Medical.Need.HealthPercent}; canApply={Tri(snapshot.Medical.Actionability.CanApplyItem)}; firstAidUsing={Bool(snapshot.Medical.Actionability.FirstAidUsing)}; surgicalKitUsing={Bool(snapshot.Medical.Actionability.SurgicalKitUsing)}; anyMedicineUsing={Bool(snapshot.Medical.Actionability.AnyMedicineUsing)}; watchdogObserved={Bool(observed)}; recoveryAuthority=None; recovered=false; nativeMutation=false; rawControllerFieldClear=false; watchdogAdmissionStateCreated=false; {watchdogSummary}; next=wait_for_native_eft_or_sain_owner_to_release_controller; tag={VanguardMedicalHandsWatchdogService.StatusTag}; legacyTag={ControllerRecoveryStatusTag}");
        ControllerBusySinceByBot.Remove(snapshot.BotProfileId);
        return true;
    }

    private static bool IsControllerRecoveryCandidate(Vanguard.Client.Runtime.Medical.VanguardMedicalNeed need)
    {
        return need == Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.HeavyBleed
            || need == Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.LightBleed
            || need == Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.Fracture
            || need == Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.HpHeal;
    }

    private static TimeSpan ControllerBusyRecoveryDelayFor(Vanguard.Client.Runtime.Medical.VanguardMedicalNeed need)
    {
        if (need == Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.HeavyBleed)
        {
            return UrgentControllerBusyRecoveryDelay;
        }

        if (need == Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.Fracture)
        {
            return StationaryControllerBusyRecoveryDelay;
        }

        return MobileControllerBusyRecoveryDelay;
    }

    private static bool InvokeNoArgBool(object? instance, params string[] names)
    {
        foreach (string name in names)
        {
            object? value = VanguardOperatorRuntimeAuditReflection.InvokeNoArg(instance, name);
            if (value is bool boolean)
            {
                if (boolean)
                {
                    return true;
                }

                continue;
            }

            if (value != null)
            {
                return true;
            }
        }

        return false;
    }

    private static TimeSpan MinDurationFor(VanguardMobileMedicalActionSelection selection)
    {
        if (IsSurgeryNeed(selection.Need))
        {
            return StationarySurgeryMinDuration;
        }

        return selection.RequiresStationary ? StationaryFractureMinDuration : MobileMinDuration;
    }

    private static TimeSpan MaxDurationFor(VanguardMobileMedicalActionSelection selection)
    {
        if (IsSurgeryNeed(selection.Need))
        {
            return IsSurv12(selection.ItemTemplateId, selection.ItemName) ? StationarySurgerySurv12MaxDuration : StationarySurgeryCmsMaxDuration;
        }

        if (selection.RequiresStationary && selection.ItemName.Contains("Grizzly", StringComparison.OrdinalIgnoreCase))
        {
            return StationaryFractureGrizzlyMaxDuration;
        }

        return selection.RequiresStationary ? StationaryFractureMaxDuration : MobileMaxDuration;
    }

    private static TimeSpan NoProgressTimeoutFor(VanguardMobileMedicalActionSelection selection)
    {
        if (IsSurgeryNeed(selection.Need))
        {
            return StationarySurgeryNoProgressTimeout;
        }

        return selection.RequiresStationary ? StationaryFractureNoProgressTimeout : MobileNoProgressTimeout;
    }

    private static void CompleteMaxWindowLease(VanguardExecutionLeaseState lease, EFT.BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, VanguardMedicalActionProgressSnapshot progress)
    {
        if (IsStrictCompletionResolved(lease, progress))
        {
            ObserveSequentialSurgeryBoundaryIfNeeded(lease, botOwner, snapshot, now, progress, "max_window_target_resolved");

            lease.CompletionObserved = true;
            string reason = progress.NeedResolved ? "NeedResolvedAtMaxWindow" : "TargetResolvedAtMaxWindow";
            CompleteResolvedLeaseOrDrainHands(lease, botOwner, snapshot, now, progress, reason, SuccessCooldown);
            return;
        }

        if (CanCompletePartialMedicalEffect(lease, progress))
        {
            lease.CompletionObserved = true;
            CompleteResolvedLeaseOrDrainHands(lease, botOwner, snapshot, now, progress, "PartialMedicalEffectObservedAtMaxWindow", PartialSuccessCooldown);
            return;
        }

        if (TryExtendControllerUsingGrace(lease, now, progress, "max_window"))
        {
            return;
        }

        if (progress.NoMedicalEffectObserved)
        {
            if (lease.LastNoEffectConfirmationAtUtc == DateTimeOffset.MinValue
                || now - lease.LastNoEffectConfirmationAtUtc >= NoEffectConfirmationDelay)
            {
                lease.NoEffectConfirmationCount = Math.Min(2, lease.NoEffectConfirmationCount + 1);
                lease.LastNoEffectConfirmationAtUtc = now;
            }
            if (lease.NoEffectConfirmationCount < 2 && now < lease.AbsoluteMaxUntilUtc)
            {
                lease.MaxUntilUtc = Min(lease.AbsoluteMaxUntilUtc, now + NoEffectConfirmationDelay);
                lease.NoProgressUntilUtc = lease.MaxUntilUtc;
                lease.LastProgressKind = "max_window_no_effect_confirmation_pending";
                return;
            }
            CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Failed, "NoMedicalEffectObservedAtMaxWindowConfirmed", CooldownForMedicalOutcome(lease, progress, "NoMedicalEffectObservedAtMaxWindowConfirmed", NoEffectCooldown), progress);
            return;
        }

        CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Timeout, "MaxWindowExpired", CooldownForMedicalOutcome(lease, progress, "MaxWindowExpired", FailureCooldown), progress);
    }

    private static TimeSpan CooldownForMedicalOutcome(VanguardExecutionLeaseState lease, VanguardMedicalActionProgressSnapshot? progress, string reason, TimeSpan fallback)
    {
        if (!IsSurgeryNeed(lease.MedicalNeed))
        {
            if (progress != null && progress.TerminalAliveConfirmed && (progress.NeedResolved || progress.TargetResolved))
            {
                return SuccessCooldown;
            }

            if (reason.IndexOf("CanApply", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("Rejected", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("controller_busy", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return TimeSpan.FromSeconds(1.75d);
            }

            if (reason.IndexOf("WhileControllerUsing", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("DuringCombatDrain", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return TimeSpan.FromSeconds(2.00d);
            }

            if (reason.IndexOf("NoMedicalEffect", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("NeedStillPresent", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return lease.MedicalNeed == Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.HeavyBleed
                    || lease.MedicalNeed == Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.LightBleed
                    ? TimeSpan.FromSeconds(2.50d)
                    : NoEffectCooldown;
            }

            return fallback;
        }

        if (progress != null && (progress.TargetStillPresent || progress.NeedStillPresent))
        {
            return SurgeryDebtRetryCooldown;
        }

        if (reason.IndexOf("WhileControllerUsing", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("NoMedicalEffect", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("NeedStillPresent", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("MaxWindow", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return SurgeryDebtRetryCooldown;
        }

        return fallback;
    }

    private static bool TryValidateSingleFirstAidCommit(BotOwner botOwner, bool controllerUsing, bool nativeBleeding, VanguardMobileMedicalActionSelection selection, out string reason)
    {
        reason = "none";
        if (botOwner == null || botOwner.IsDead || botOwner.GetPlayer?.HealthController?.IsAlive != true)
        {
            reason = "operator_not_alive";
            return false;
        }

        if (controllerUsing)
        {
            reason = "first_aid_controller_already_using";
            return false;
        }

        if (selection.Item == null)
        {
            reason = "first_aid_item_missing";
            return false;
        }

        if (botOwner.WeaponManager?.Reload?.Reloading == true)
        {
            reason = "weapon_reloading";
            return false;
        }

        bool bleedingNeed = selection.Need == VanguardMedicalNeed.HeavyBleed
            || selection.Need == VanguardMedicalNeed.LightBleed;
        if (bleedingNeed && !nativeBleeding)
        {
            reason = "native_bleeding_state_not_ready";
            return false;
        }

        if (!bleedingNeed)
        {
            try
            {
                if (botOwner.GetPlayer?.HealthController?.CanApplyItem(selection.Item, selection.TargetPart) != true)
                {
                    reason = "health_controller_can_apply_false:" + selection.TargetPart;
                    return false;
                }
            }
            catch (Exception exception)
            {
                reason = "commit_preflight_exception:" + exception.GetType().Name;
                return false;
            }
        }

        reason = "single_first_aid_commit_ready";
        return true;
    }

    private static bool TryValidateSingleSurgeryCommit(BotOwner botOwner, bool controllerUsing, MedsItemClass item, EBodyPart targetPart, out string reason)
    {
        reason = "none";
        if (botOwner == null || botOwner.IsDead || botOwner.GetPlayer?.HealthController?.IsAlive != true)
        {
            reason = "operator_not_alive";
            return false;
        }

        if (controllerUsing)
        {
            reason = "surgical_controller_already_using";
            return false;
        }

        if (item == null)
        {
            reason = "surgery_item_missing";
            return false;
        }

        if (botOwner.WeaponManager?.Reload?.Reloading == true)
        {
            reason = "weapon_reloading";
            return false;
        }

        try
        {
            if (botOwner.GetPlayer?.ActiveHealthController?.IsBodyPartDestroyed(targetPart) != true)
            {
                reason = "target_not_destroyed:" + targetPart;
                return false;
            }

            if (botOwner.GetPlayer?.HealthController?.CanApplyItem(item, targetPart) != true)
            {
                reason = "health_controller_can_apply_false:" + targetPart;
                return false;
            }
        }
        catch (Exception exception)
        {
            reason = "commit_preflight_exception:" + exception.GetType().Name;
            return false;
        }

        reason = "single_controller_commit_ready";
        return true;
    }

    private static void ObserveSurgeryControllerCallback(string botProfileId, string leaseId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!VanguardExecutionLeaseStore.TryGetActive(botProfileId, out var lease)
            || !string.Equals(lease.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase)
            || !IsSurgeryNeed(lease.MedicalNeed))
        {
            return;
        }

        lease.SurgeryControllerCallbackObserved = true;
        lease.SurgeryControllerCallbackAtUtc = now;
        lease.ItemUseObserved = true;
        lease.FirstAidEndedObserved = true;
        lease.LastControllerActivityAtUtc = now;
        lease.LastProgressAtUtc = now;
        lease.LastProgressKind = "surgery_controller_callback_completed";
        lease.NextPostUseRecheckAtUtc = now;
        lease.PostUseRecheckUntilUtc = now + PostUseRecheckWindowForLease(lease);
        lease.NoProgressUntilUtc = lease.PostUseRecheckUntilUtc;
        VanguardClientDiagnosticsLog.Info(MedicalProcedureCompletionGateStatusTag,
            $"VANGUARD_SURGERY_CONTROLLER_CALLBACK {lease.Summary}; callback=end_use; applyCalls={lease.SurgeryApplyAttemptCount}; next=effect_recheck; callbackIsTerminalOnly=true; effectRequiresBodyPartTruth=true; noReapply=true; tag=VANGUARD_SURGERY_CONTROLLER_LIFECYCLE_STATUS; completionGateTag={MedicalProcedureCompletionGateStatusTag}");
    }

    private static bool TryHandleSurgeryControllerCommitTimeout(VanguardExecutionLeaseState lease, BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, VanguardMedicalActionProgressSnapshot progress, bool allowInternalRetry)
    {
        if (!IsSurgeryNeed(lease.MedicalNeed)
            || lease.SurgeryControllerCallbackObserved)
        {
            return false;
        }

        ObserveSurgeryCommitTruth(lease, now, progress);
        if (lease.SurgeryResourceCommitObserved || lease.SurgeryTargetEffectConfirmed)
        {
            return false;
        }

        bool nativeHandsCommitted = TryObserveSurgeryNativeHandsCommit(lease, botOwner, now, out bool handsReadable, out bool weaponHandsStillActive);
        if (nativeHandsCommitted || now - lease.StartedAtUtc < SurgeryNativeHandsCommitTimeout)
        {
            return false;
        }

        if (progress.FirstAidUsing && handsReadable && weaponHandsStillActive
            && lease.SurgeryNativeHandsMismatchSnapshotCount >= SurgeryNativeHandsMismatchRequiredSnapshots
            && lease.SurgeryNativeHandsMismatchSinceUtc != DateTimeOffset.MinValue
            && now - lease.SurgeryNativeHandsMismatchSinceUtc >= SurgeryNativeHandsMismatchStableWindow)
        {
            bool retryAllowed = allowInternalRetry && lease.SurgeryStartRetryCount < MaxInternalSurgeryStartRetries;
            lease.SurgeryStartRetryPending = retryAllowed;
            lease.SurgeryStartRetryRequestedAtUtc = now;
            lease.SurgeryStartRetryReason = retryAllowed
                ? "native_set_in_hands_not_committed"
                : "native_set_in_hands_not_committed_retry_unavailable";
            if (retryAllowed) lease.SurgeryStartRetryCount++;
            TryCancelCommittedSurgery(
                lease,
                botOwner,
                now,
                progress,
                "native_set_in_hands_not_committed:" + Safe(lease.LastSurgeryNativeHandsSummary),
                retryAllowed ? "native_hands_commit_missing_retry" : "native_hands_commit_missing",
                isThreat: false);
            VanguardClientDiagnosticsLog.Warning(MedicalProcedureCompletionGateStatusTag, () =>
                $"VANGUARD_SURGERY_NATIVE_HANDS_NOT_COMMITTED {lease.Summary}; applyCalls={lease.SurgeryApplyAttemptCount}; elapsed={(now - lease.StartedAtUtc).TotalSeconds:0.00}; hands={Safe(lease.LastSurgeryNativeHandsSummary)}; resourceConsumed=false; targetEffect=false; controllerUsing={Bool(progress.FirstAidUsing)}; internalRetry={Bool(retryAllowed)}; action={(retryAllowed ? "native_cancel_then_same_lease_retry" : "native_cancel_then_terminal_generation")}; tag={MedicalProcedureCompletionGateStatusTag}");
            return true;
        }

        if (progress.FirstAidUsing)
        {
            // Unknown or transitioning hands are not sufficient evidence for cancellation. Keep the
            // existing bounded controller window; only positive, stable weapon-hands truth proves
            // that SetInHands failed while the native Using flag remained stuck.
            return false;
        }

        string resourceState = progress.ItemResourceReadable
            ? "resource_unchanged:" + progress.CurrentItemResource.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : "resource_unreadable";
        var surgicalKit = botOwner?.Medecine?.SurgicalKit;
        if (surgicalKit?.Using == true)
        {
            return false;
        }

        bool noControllerRetryAllowed = allowInternalRetry && lease.SurgeryStartRetryCount < MaxInternalSurgeryStartRetries;
        if (noControllerRetryAllowed)
        {
            lease.SurgeryStartRetryPending = true;
            lease.SurgeryStartRetryRequestedAtUtc = now;
            lease.SurgeryStartRetryReason = "native_controller_did_not_enter_using";
            lease.SurgeryStartRetryCount++;
            TryCancelCommittedSurgery(
                lease,
                botOwner,
                now,
                progress,
                lease.SurgeryStartRetryReason,
                "native_controller_start_missing_retry",
                isThreat: false);
            VanguardClientDiagnosticsLog.Warning(MedicalNativeCommitStatusTag,
                $"VANGUARD_NATIVE_SURGERY_CONTROLLER_START_MISSING {lease.Summary}; resource={Safe(resourceState)}; hands={Safe(lease.LastSurgeryNativeHandsSummary)}; applyCalls={lease.SurgeryApplyAttemptCount}; internalRetry=true; itemExpectedUnconsumed={Bool(!progress.ItemResourceConsumed)}; action=cancel_drain_then_same_lease_retry; tag={MedicalNativeCommitStatusTag}");
            return true;
        }

        lease.LastProgressKind = "surgery_controller_did_not_commit";
        CompleteLease(
            lease,
            now,
            VanguardMedicalActionOutcomeKind.Interrupted,
            "SurgeryControllerDidNotCommitAfterBoundedRetry:" + resourceState,
            StationarySurgeryInterruptedRetryCooldown,
            progress);
        VanguardClientDiagnosticsLog.Warning(MedicalProcedureCompletionGateStatusTag,
            $"VANGUARD_SURGERY_CONTROLLER_NOT_COMMITTED {lease.Summary}; resource={Safe(resourceState)}; hands={Safe(lease.LastSurgeryNativeHandsSummary)}; applyCalls={lease.SurgeryApplyAttemptCount}; internalRetry=false; itemExpectedUnconsumed={Bool(!progress.ItemResourceConsumed)}; next=terminal_explicit_native_start_unavailable; tag=VANGUARD_SURGERY_CONTROLLER_LIFECYCLE_STATUS; completionGateTag={MedicalProcedureCompletionGateStatusTag}");
        return true;
    }

    private static bool TryObserveSurgeryNativeHandsCommit(
        VanguardExecutionLeaseState lease,
        BotOwner? botOwner,
        DateTimeOffset now,
        out bool handsReadable,
        out bool weaponHandsStillActive)
    {
        handsReadable = false;
        weaponHandsStillActive = false;
        if (lease.SurgeryNativeHandsCommitObserved)
        {
            return true;
        }

        if (botOwner == null)
        {
            lease.LastSurgeryNativeHandsSummary = "bot_owner_missing";
            return false;
        }

        try
        {
            VanguardPostLootWeaponReadinessSnapshot hands = VanguardPostLootWeaponReadinessReader.Capture(botOwner);
            lease.LastSurgeryNativeHandsSummary = hands.Summary;
            string handsType = hands.HandsControllerType ?? string.Empty;
            handsReadable = !string.IsNullOrWhiteSpace(handsType)
                && !string.Equals(handsType, "none", StringComparison.OrdinalIgnoreCase);
            bool medicalHands = handsReadable
                && (handsType.IndexOf("MedsController", StringComparison.OrdinalIgnoreCase) >= 0
                    || handsType.IndexOf("MedsAnimationHandsController", StringComparison.OrdinalIgnoreCase) >= 0
                    || handsType.IndexOf("Medicine", StringComparison.OrdinalIgnoreCase) >= 0);
            if (medicalHands)
            {
                lease.SurgeryNativeHandsCommitObserved = true;
                lease.SurgeryNativeHandsCommitObservedAtUtc = now;
                lease.SurgeryNativeHandsMismatchSinceUtc = DateTimeOffset.MinValue;
                lease.SurgeryNativeHandsMismatchSnapshotCount = 0;
                lease.LastProgressAtUtc = now;
                lease.LastProgressKind = "surgery_native_medical_hands_committed";
                return true;
            }

            weaponHandsStillActive = handsReadable && hands.WeaponReady;
            if (!weaponHandsStillActive)
            {
                lease.SurgeryNativeHandsMismatchSinceUtc = DateTimeOffset.MinValue;
                lease.SurgeryNativeHandsMismatchSnapshotCount = 0;
                return false;
            }

            if (lease.LastSurgeryNativeHandsSnapshotAtUtc == now)
            {
                return false;
            }

            lease.LastSurgeryNativeHandsSnapshotAtUtc = now;
            if (lease.SurgeryNativeHandsMismatchSinceUtc == DateTimeOffset.MinValue)
            {
                lease.SurgeryNativeHandsMismatchSinceUtc = now;
                lease.SurgeryNativeHandsMismatchSnapshotCount = 1;
            }
            else
            {
                lease.SurgeryNativeHandsMismatchSnapshotCount = Math.Min(
                    SurgeryNativeHandsMismatchRequiredSnapshots,
                    lease.SurgeryNativeHandsMismatchSnapshotCount + 1);
            }
        }
        catch (Exception exception)
        {
            lease.LastSurgeryNativeHandsSummary = "hands_read_exception:" + Safe(exception.GetType().Name);
            lease.SurgeryNativeHandsMismatchSinceUtc = DateTimeOffset.MinValue;
            lease.SurgeryNativeHandsMismatchSnapshotCount = 0;
        }

        return false;
    }

    private static bool TryHandleNativeMedicalStartStall(
        VanguardExecutionLeaseState lease,
        BotOwner? botOwner,
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        VanguardMedicalActionProgressSnapshot progress,
        bool allowInternalSurgeryRetry)
    {
        if (!progress.FirstAidUsing
            || progress.ItemResourceConsumed
            || progress.AnyMedicalEffectObserved
            || progress.TargetResolved
            || progress.NeedResolved)
        {
            return false;
        }

        if (IsSurgeryNeed(lease.MedicalNeed))
        {
            if (lease.SurgeryControllerCallbackObserved || lease.SurgeryResourceCommitObserved || lease.SurgeryTargetEffectConfirmed)
            {
                return false;
            }

            DateTimeOffset started = lease.LastSurgeryApplyAttemptAtUtc == DateTimeOffset.MinValue
                ? lease.StartedAtUtc
                : lease.LastSurgeryApplyAttemptAtUtc;
            TimeSpan deadline = IsSurv12(lease.ItemTemplateId, lease.ItemName)
                ? SurgerySurv12NativeStartStallTimeout
                : SurgeryCmsNativeStartStallTimeout;
            if (now - started < deadline)
            {
                return false;
            }

            if (HasTrueMedicalAbortThreat(snapshot, out _))
            {
                return false;
            }

            bool retryAllowed = allowInternalSurgeryRetry && lease.SurgeryStartRetryCount < MaxInternalSurgeryStartRetries;
            lease.SurgeryStartRetryPending = retryAllowed;
            lease.SurgeryStartRetryRequestedAtUtc = now;
            lease.SurgeryStartRetryReason = retryAllowed
                ? "native_controller_using_without_resource_commit_or_effect"
                : "native_controller_stalled_after_bounded_retry";
            if (retryAllowed)
            {
                lease.SurgeryStartRetryCount++;
            }

            TryCancelCommittedSurgery(
                lease,
                botOwner,
                now,
                progress,
                lease.SurgeryStartRetryReason,
                retryAllowed ? "native_start_stall_retry" : "native_start_stall_terminal",
                isThreat: false);
            VanguardClientDiagnosticsLog.Warning(MedicalNativeCommitStatusTag,
                $"VANGUARD_NATIVE_SURGERY_START_STALL {lease.Summary}; elapsed={(now - started).TotalSeconds:0.00}; deadline={deadline.TotalSeconds:0.00}; retryAllowed={Bool(retryAllowed)}; retryCount={lease.SurgeryStartRetryCount}; resourceCommitted=false; targetEffect=false; callback=false; action={(retryAllowed ? "cancel_drain_then_internal_retry" : "cancel_drain_then_terminal_failure")}; gameplayFailure=false; tag={MedicalNativeCommitStatusTag}");
            return true;
        }

        if (lease.FirstAidCancellationRequested || lease.FirstAidStartStallObserved)
        {
            return false;
        }

        DateTimeOffset firstAidStarted = lease.FirstAidUsingObservedAtUtc == DateTimeOffset.MinValue
            ? now
            : lease.FirstAidUsingObservedAtUtc;
        if (now - firstAidStarted < FirstAidNativeStartStallTimeout)
        {
            return false;
        }

        lease.FirstAidStartStallObserved = true;
        TryCancelStalledFirstAid(lease, botOwner, now, progress, "native_first_aid_using_without_resource_or_effect");
        return true;
    }

    private static void TryCancelStalledFirstAid(
        VanguardExecutionLeaseState lease,
        BotOwner? botOwner,
        DateTimeOffset now,
        VanguardMedicalActionProgressSnapshot progress,
        string reason)
    {
        if (lease.FirstAidCancellationRequested)
        {
            return;
        }

        lease.FirstAidCancellationRequested = true;
        lease.FirstAidCancellationRequestedAtUtc = now;
        lease.FirstAidCancellationReason = reason;
        lease.FirstAidCancellationKind = "native_start_stall";
        lease.FirstAidCancellationIsThreat = false;
        ResetNativeCancelHandsTruth(lease);
        lease.LastProgressAtUtc = now;
        lease.LastProgressKind = "first_aid_native_start_stall_cancel_requested";
        lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(1.00d);

        bool controllerFound = false;
        bool controllerWasUsing = false;
        bool cancelRequested = false;
        try
        {
            var firstAid = botOwner?.Medecine?.FirstAid;
            controllerFound = firstAid != null;
            controllerWasUsing = firstAid?.Using == true;
            if (firstAid != null)
            {
                firstAid.CancelCurrent();
                cancelRequested = true;
            }
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(MedicalNativeCommitStatusTag,
                $"VANGUARD_NATIVE_FIRST_AID_START_STALL_CANCEL_EXCEPTION {lease.Summary}; exception={Safe(exception.GetType().Name)}:{Safe(exception.Message)}; tag={MedicalNativeCommitStatusTag}");
        }

        VanguardClientDiagnosticsLog.Warning(MedicalNativeCommitStatusTag,
            $"VANGUARD_NATIVE_FIRST_AID_START_STALL {lease.Summary}; elapsed={(now - lease.StartedAtUtc).TotalSeconds:0.00}; deadline={FirstAidNativeStartStallTimeout.TotalSeconds:0.00}; controllerFound={Bool(controllerFound)}; controllerWasUsing={Bool(controllerWasUsing)}; cancelRequested={Bool(cancelRequested)}; resource={(progress.ItemResourceReadable ? progress.CurrentItemResource.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "unknown")}; action=cancel_then_fresh_lease_retry; tag={MedicalNativeCommitStatusTag}");
    }

    private static void TryCancelCommittedFirstAid(
        VanguardExecutionLeaseState lease,
        BotOwner? botOwner,
        DateTimeOffset now,
        VanguardMedicalActionProgressSnapshot progress,
        string cancellationReason,
        string cancellationKind = "true_threat",
        bool isThreat = true)
    {
        if (lease.FirstAidCancellationRequested)
        {
            return;
        }

        lease.ThreatObservedDuringLease |= isThreat;
        lease.FirstAidCancellationRequested = true;
        lease.FirstAidCancellationRequestedAtUtc = now;
        lease.FirstAidCancellationReason = cancellationReason;
        lease.FirstAidCancellationKind = cancellationKind;
        lease.FirstAidCancellationIsThreat = isThreat;
        ResetNativeCancelHandsTruth(lease);
        lease.LastProgressAtUtc = now;
        lease.LastProgressKind = "first_aid_native_cancel_requested:" + Safe(cancellationKind);
        lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(1.00d);

        bool controllerFound = false;
        bool controllerWasUsing = false;
        bool cancelRequested = false;
        try
        {
            var firstAid = botOwner?.Medecine?.FirstAid;
            controllerFound = firstAid != null;
            controllerWasUsing = firstAid?.Using == true;
            if (firstAid != null)
            {
                // Native cancellation preserves EFT/Fika ownership of hand state and item commit.
                // Vanguard never rewrites the consumable resource.
                firstAid.CancelCurrent();
                cancelRequested = true;
            }
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(MedicalThreatCancellationStatusTag,
                $"VANGUARD_FIRST_AID_NATIVE_CANCEL_EXCEPTION {lease.Summary}; reason={Safe(cancellationReason)}; exception={Safe(exception.GetType().Name)}:{Safe(exception.Message)}; tag={MedicalThreatCancellationStatusTag}");
        }

        string resourceState = progress.ItemResourceReadable
            ? progress.CurrentItemResource.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";
        VanguardClientDiagnosticsLog.Warning(MedicalThreatCancellationStatusTag,
            $"VANGUARD_FIRST_AID_NATIVE_CANCEL_REQUESTED {lease.Summary}; reason={Safe(cancellationReason)}; kind={Safe(cancellationKind)}; isThreat={Bool(isThreat)}; controllerFound={Bool(controllerFound)}; controllerWasUsing={Bool(controllerWasUsing)}; cancelRequested={Bool(cancelRequested)}; resourceAtCancel={resourceState}; initialResource={lease.InitialItemResource:0.0}; nativePath=BotFirstAidClass.CancelCurrent; awaitHandsReturn=true; noResourceRewrite=true; tag={(isThreat ? MedicalThreatCancellationStatusTag : CanonicalPriorityPreemptionStatusTag)}");
    }

    private static bool HandlePendingFirstAidCancellation(
        VanguardExecutionLeaseState lease,
        BotOwner? botOwner,
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        VanguardMedicalActionProgressSnapshot progress)
    {
        bool controllerUsing;
        try
        {
            controllerUsing = botOwner?.Medecine?.FirstAid?.Using == true;
        }
        catch
        {
            controllerUsing = progress.FirstAidUsing;
        }

        bool canonicalPriorityPreemption = string.Equals(lease.FirstAidCancellationKind, "canonical_priority_preemption", StringComparison.OrdinalIgnoreCase);
        VanguardMedicalActionOutcomeKind outcome = lease.FirstAidCancellationIsThreat || canonicalPriorityPreemption
            ? VanguardMedicalActionOutcomeKind.Interrupted
            : VanguardMedicalActionOutcomeKind.Failed;
        string confirmedPrefix = canonicalPriorityPreemption
            ? "InterruptedByCanonicalMedicalPriorityNativeCancelHandsReturnedConfirmed"
            : lease.FirstAidCancellationIsThreat
                ? "InterruptedByTrueMedicalThreatNativeCancelHandsReturnedConfirmed"
                : "NativeFirstAidStartStallCancelledHandsReturnedConfirmed";
        string timeoutPrefix = canonicalPriorityPreemption
            ? "CanonicalMedicalPriorityNativeCancelHandsReturnTimeout"
            : lease.FirstAidCancellationIsThreat
                ? "FirstAidNativeCancelHandsReturnTimeout"
                : "NativeFirstAidStartStallHandsReturnTimeout";
        return HandleNativeCancelHandsReturn(
            lease,
            botOwner,
            snapshot,
            now,
            progress,
            controllerUsing,
            lease.FirstAidCancellationRequestedAtUtc,
            FirstAidNativeCancelDrainTimeout,
            outcome,
            confirmedPrefix + ":" + Safe(lease.FirstAidCancellationKind) + ":" + Safe(lease.FirstAidCancellationReason),
            timeoutPrefix + ":" + Safe(lease.FirstAidCancellationKind) + ":" + Safe(lease.FirstAidCancellationReason),
            canonicalPriorityPreemption ? TimeSpan.Zero : lease.FirstAidCancellationIsThreat ? FirstAidThreatRetryCooldown : StationarySurgeryInterruptedRetryCooldown,
            canonicalPriorityPreemption ? CanonicalPriorityPreemptionStatusTag : lease.FirstAidCancellationIsThreat ? MedicalThreatCancellationStatusTag : MedicalNativeCommitStatusTag,
            canonicalPriorityPreemption ? "first_aid_canonical_priority_preemption" : "first_aid");
    }

    private static void ResetNativeCancelHandsTruth(VanguardExecutionLeaseState lease)
    {
        lease.NativeCancelHandsReadySinceUtc = DateTimeOffset.MinValue;
        lease.LastNativeCancelHandsSnapshotAtUtc = DateTimeOffset.MinValue;
        lease.NativeCancelHandsReadySnapshotCount = 0;
        lease.NativeCancelHandsRecoveryAttempted = false;
        lease.LastNativeCancelHandsReadiness = "not_sampled";
    }

    private static bool HandleNativeCancelHandsReturn(
        VanguardExecutionLeaseState lease,
        BotOwner? botOwner,
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        VanguardMedicalActionProgressSnapshot progress,
        bool nativeControllerUsing,
        DateTimeOffset cancellationRequestedAtUtc,
        TimeSpan absoluteDrainTimeout,
        VanguardMedicalActionOutcomeKind confirmedOutcome,
        string confirmedReasonPrefix,
        string timeoutReasonPrefix,
        TimeSpan confirmedCooldown,
        string legacyStatusTag,
        string cancellationKind)
    {
        bool medicineUsing = nativeControllerUsing
            || progress.FirstAidUsing
            || snapshot.Medical.Actionability.AnyMedicineUsing
            || snapshot.Medical.Actionability.FirstAidUsing
            || snapshot.Medical.Actionability.SurgicalKitUsing
            || snapshot.Medical.Actionability.StimulatorUsing;
        VanguardPostLootWeaponReadinessSnapshot? readiness = null;
        bool weaponReady = false;
        if (!medicineUsing && botOwner != null)
        {
            try
            {
                readiness = VanguardPostLootWeaponReadinessReader.Capture(botOwner);
                weaponReady = readiness.WeaponReady && !readiness.FirstAidUsing;
                lease.LastNativeCancelHandsReadiness = readiness.Summary;
            }
            catch (Exception exception)
            {
                lease.LastNativeCancelHandsReadiness = "readiness_exception:" + Safe(exception.GetType().Name) + ":" + Safe(exception.Message);
            }
        }
        else
        {
            lease.LastNativeCancelHandsReadiness = medicineUsing ? "medicine_controller_still_active" : "bot_owner_missing";
        }

        TimeSpan cancelElapsed = cancellationRequestedAtUtc == DateTimeOffset.MinValue
            ? TimeSpan.Zero
            : now - cancellationRequestedAtUtc;
        if (medicineUsing || !weaponReady)
        {
            lease.NativeCancelHandsReadySinceUtc = DateTimeOffset.MinValue;
            lease.NativeCancelHandsReadySnapshotCount = 0;
            lease.LastProgressAtUtc = now;
            lease.LastProgressKind = cancellationKind + "_native_cancel_waiting_for_positive_hands_truth";
            lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(1.00d);

            if (!lease.NativeCancelHandsRecoveryAttempted
                && cancelElapsed >= NativeCancelHandsRecoveryDelay)
            {
                lease.NativeCancelHandsRecoveryAttempted = true;
                string recovery = RecoverControllerAtTerminalBoundary(botOwner, snapshot, lease, cancellationKind + "_native_cancel_positive_hands_truth_delay");
                VanguardClientDiagnosticsLog.Warning(MedicalHandsReturnTruthStatusTag,
                    $"VANGUARD_NATIVE_CANCEL_HANDS_RECOVERY {lease.Summary}; kind={Safe(cancellationKind)}; elapsed={cancelElapsed.TotalSeconds:0.00}; medicineUsing={Bool(medicineUsing)}; weaponReady={Bool(weaponReady)}; readiness={Safe(readiness?.Summary ?? lease.LastNativeCancelHandsReadiness)}; recovery={Safe(recovery)}; keepLease=true; outcomeNotCommitted=true; tag={MedicalHandsReturnTruthStatusTag}; legacyTag={legacyStatusTag}");
            }

            if (cancelElapsed >= absoluteDrainTimeout)
            {
                string terminalRecovery = lease.NativeCancelHandsRecoveryAttempted
                    ? "controllerRecovery=already_attempted"
                    : RecoverControllerAtTerminalBoundary(botOwner, snapshot, lease, cancellationKind + "_native_cancel_hands_return_timeout");
                lease.NativeCancelHandsRecoveryAttempted = true;
                CompleteLease(
                    lease,
                    now,
                    confirmedOutcome,
                    timeoutReasonPrefix + ":" + terminalRecovery + ":readiness=" + Safe(readiness?.Summary ?? lease.LastNativeCancelHandsReadiness),
                    NoEffectCooldown,
                    progress);
                return true;
            }

            if (ShouldLogRecheck(lease.BotProfileId + "|native_cancel_hands_waiting|" + lease.LeaseId + "|" + cancellationKind, now))
            {
                VanguardClientDiagnosticsLog.Info(MedicalHandsReturnTruthStatusTag,
                    $"VANGUARD_NATIVE_CANCEL_HANDS_DRAIN {lease.Summary}; kind={Safe(cancellationKind)}; elapsed={cancelElapsed.TotalSeconds:0.00}; medicineUsing={Bool(medicineUsing)}; nativeControllerUsing={Bool(nativeControllerUsing)}; sampledUsing={Bool(progress.FirstAidUsing)}; weaponReady={Bool(weaponReady)}; readiness={Safe(readiness?.Summary ?? lease.LastNativeCancelHandsReadiness)}; keepLease=true; newPrimaryForbidden=true; falseHandsReturnedForbidden=true; tag={MedicalHandsReturnTruthStatusTag}; legacyTag={legacyStatusTag}");
            }
            return true;
        }

        if (lease.LastNativeCancelHandsSnapshotAtUtc == snapshot.CapturedAtUtc)
        {
            return true;
        }

        lease.LastNativeCancelHandsSnapshotAtUtc = snapshot.CapturedAtUtc;
        if (lease.NativeCancelHandsReadySinceUtc == DateTimeOffset.MinValue)
        {
            lease.NativeCancelHandsReadySinceUtc = now;
            lease.NativeCancelHandsReadySnapshotCount = 1;
            lease.LastProgressAtUtc = now;
            lease.LastProgressKind = cancellationKind + "_hands_ready_candidate_1";
            lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(1.00d);
            return true;
        }

        lease.NativeCancelHandsReadySnapshotCount = Math.Min(
            NativeCancelHandsRequiredSnapshots,
            lease.NativeCancelHandsReadySnapshotCount + 1);
        bool stable = lease.NativeCancelHandsReadySnapshotCount >= NativeCancelHandsRequiredSnapshots
            && now - lease.NativeCancelHandsReadySinceUtc >= NativeCancelHandsStableWindow;
        if (!stable)
        {
            lease.LastProgressAtUtc = now;
            lease.LastProgressKind = cancellationKind + "_hands_ready_candidate_" + lease.NativeCancelHandsReadySnapshotCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(1.00d);
            return true;
        }

        lease.FirstAidEndedObserved = true;
        string resourceState = progress.ItemResourceReadable
            ? progress.CurrentItemResource.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";
        VanguardClientDiagnosticsLog.Info(MedicalHandsReturnTruthStatusTag,
            $"VANGUARD_NATIVE_CANCEL_HANDS_RETURN_CONFIRMED {lease.Summary}; kind={Safe(cancellationKind)}; stableSeconds={(now - lease.NativeCancelHandsReadySinceUtc).TotalSeconds:0.00}; snapshots={lease.NativeCancelHandsReadySnapshotCount}; readiness={Safe(readiness?.Summary ?? lease.LastNativeCancelHandsReadiness)}; resource={resourceState}; newPrimaryAllowed=true; tag={MedicalHandsReturnTruthStatusTag}; legacyTag={legacyStatusTag}");
        CompleteLease(
            lease,
            now,
            confirmedOutcome,
            confirmedReasonPrefix + ":resource=" + resourceState + ":weaponReadyConfirmed=true",
            confirmedCooldown,
            progress);
        return true;
    }

    private static bool IsSurgeryControllerActive(BotOwner? botOwner, VanguardMedicalActionProgressSnapshot progress)
    {
        if (progress.FirstAidUsing)
        {
            return true;
        }

        try
        {
            return botOwner?.Medecine?.SurgicalKit?.Using == true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryCancelCommittedSurgery(VanguardExecutionLeaseState lease, BotOwner? botOwner, DateTimeOffset now, VanguardMedicalActionProgressSnapshot progress, string cancellationReason, string cancellationKind, bool isThreat)
    {
        if (lease.SurgeryCancellationRequested)
        {
            return;
        }

        lease.ThreatObservedDuringLease |= isThreat;
        lease.SurgeryCancellationRequested = true;
        lease.SurgeryCancellationRequestedAtUtc = now;
        lease.SurgeryCancellationReason = cancellationReason;
        lease.SurgeryCancellationKind = cancellationKind;
        lease.SurgeryCancellationIsThreat = isThreat;
        ResetNativeCancelHandsTruth(lease);
        lease.LastProgressAtUtc = now;
        lease.LastProgressKind = "surgery_native_cancel_requested:" + Safe(cancellationKind);
        lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(2.00d);

        bool controllerFound = false;
        bool controllerWasUsing = false;
        bool cancelRequested = false;
        try
        {
            var surgicalKit = botOwner?.Medecine?.SurgicalKit;
            controllerFound = surgicalKit != null;
            controllerWasUsing = surgicalKit?.Using == true;
            if (surgicalKit != null)
            {
                // The native controller owns cancellation semantics and resource commit timing.
                // Calling it once is safer than force-clearing hands or mutating the item resource.
                surgicalKit.CancelCurrent();
                cancelRequested = true;
            }
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(MedicalHardLockAbortGateStatusTag,
                $"VANGUARD_SURGERY_NATIVE_CANCEL_EXCEPTION {lease.Summary}; reason={Safe(cancellationReason)}; kind={Safe(cancellationKind)}; exception={Safe(exception.GetType().Name)}:{Safe(exception.Message)}; tag=VANGUARD_SURGERY_CONTROLLER_LIFECYCLE_STATUS; abortGateTag={MedicalHardLockAbortGateStatusTag}");
        }

        string resourceState = progress.ItemResourceReadable
            ? progress.CurrentItemResource.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";
        VanguardClientDiagnosticsLog.Warning(MedicalHardLockAbortGateStatusTag,
            $"VANGUARD_SURGERY_NATIVE_CANCEL_REQUESTED {lease.Summary}; reason={Safe(cancellationReason)}; kind={Safe(cancellationKind)}; controllerFound={Bool(controllerFound)}; controllerWasUsing={Bool(controllerWasUsing)}; cancelRequested={Bool(cancelRequested)}; resourceAtCancel={resourceState}; initialResource={lease.InitialItemResource:0.0}; nativePath=BotSurgicalKit.CancelCurrent; itemConsumptionExpected=false_if_EFT_cancels_before_on_used_commit; awaitHandsReturn=true; noForcedControllerClear=true; tag=VANGUARD_SURGERY_CONTROLLER_LIFECYCLE_STATUS; abortGateTag={MedicalHardLockAbortGateStatusTag}");
    }

    private static bool HandlePendingSurgeryCancellation(VanguardExecutionLeaseState lease, BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, VanguardMedicalActionProgressSnapshot progress)
    {
        if (lease.SurgeryStartRetryPending)
        {
            return HandlePendingSurgeryStartRetry(lease, botOwner, snapshot, now, progress);
        }

        bool controllerUsing = false;
        try
        {
            controllerUsing = botOwner?.Medecine?.SurgicalKit?.Using == true;
        }
        catch
        {
            controllerUsing = progress.FirstAidUsing;
        }

        bool repairedTargetTruth = lease.SurgeryTargetEffectConfirmed || progress.SurgeryTargetRestored;
        if (!repairedTargetTruth && botOwner != null
            && VanguardMedicalActionProgressReader.TryReadTargetHealth(botOwner, lease.TargetPart, out float repairedCurrent, out _)
            && repairedCurrent > 0.01f)
        {
            repairedTargetTruth = true;
            lease.SurgeryTargetEffectConfirmed = true;
            lease.SurgeryTargetEffectConfirmedAtUtc = now;
        }
        bool repairedTerminalCleanup = string.Equals(lease.SurgeryCancellationKind, "effect_repaired_terminal_cleanup", StringComparison.OrdinalIgnoreCase)
            && repairedTargetTruth;
        bool canonicalPriorityPreemption = string.Equals(lease.SurgeryCancellationKind, "canonical_priority_preemption", StringComparison.OrdinalIgnoreCase);
        VanguardMedicalActionOutcomeKind cancellationOutcome = repairedTerminalCleanup
            ? VanguardMedicalActionOutcomeKind.Completed
            : lease.SurgeryCancellationIsThreat || canonicalPriorityPreemption
                ? VanguardMedicalActionOutcomeKind.Interrupted
                : VanguardMedicalActionOutcomeKind.Failed;
        string confirmedPrefix = repairedTerminalCleanup
            ? "SurgeryEffectRepairedNativeCancelHandsReturnedConfirmed"
            : canonicalPriorityPreemption
                ? "InterruptedByCanonicalMedicalPrioritySurgeryCancelHandsReturnedConfirmed"
                : lease.SurgeryCancellationIsThreat
                    ? "InterruptedByTrueSurgeryThreatNativeCancelHandsReturnedConfirmed"
                    : "SurgeryControllerInvalidatedNativeCancelHandsReturnedConfirmed";
        string timeoutPrefix = repairedTerminalCleanup
            ? "SurgeryEffectRepairedHandsReturnTimeout"
            : canonicalPriorityPreemption
                ? "CanonicalMedicalPrioritySurgeryCancelHandsReturnTimeout"
                : lease.SurgeryCancellationIsThreat
                    ? "SurgeryThreatNativeCancelHandsReturnTimeout"
                    : "SurgeryControllerInvalidatedHandsReturnTimeout";
        return HandleNativeCancelHandsReturn(
            lease,
            botOwner,
            snapshot,
            now,
            progress,
            controllerUsing,
            lease.SurgeryCancellationRequestedAtUtc,
            SurgeryNativeCancelDrainTimeout,
            cancellationOutcome,
            confirmedPrefix + ":" + Safe(lease.SurgeryCancellationKind) + ":" + Safe(lease.SurgeryCancellationReason),
            timeoutPrefix + ":" + Safe(lease.SurgeryCancellationKind) + ":" + Safe(lease.SurgeryCancellationReason),
            repairedTerminalCleanup ? SuccessCooldown : canonicalPriorityPreemption ? TimeSpan.Zero : lease.SurgeryCancellationIsThreat ? StationarySurgeryInterruptedRetryCooldown : NoEffectCooldown,
            canonicalPriorityPreemption ? CanonicalPriorityPreemptionStatusTag : MedicalHardLockAbortGateStatusTag,
            canonicalPriorityPreemption ? "surgery_canonical_priority_preemption" : "surgery");
    }

    private static bool HandlePendingSurgeryStartRetry(
        VanguardExecutionLeaseState lease,
        BotOwner? botOwner,
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        VanguardMedicalActionProgressSnapshot progress)
    {
        bool controllerUsing;
        try { controllerUsing = botOwner?.Medecine?.SurgicalKit?.Using == true; }
        catch { controllerUsing = progress.FirstAidUsing; }

        bool medicineUsing = controllerUsing
            || progress.FirstAidUsing
            || snapshot.Medical.Actionability.AnyMedicineUsing
            || snapshot.Medical.Actionability.FirstAidUsing
            || snapshot.Medical.Actionability.SurgicalKitUsing
            || snapshot.Medical.Actionability.StimulatorUsing;
        VanguardPostLootWeaponReadinessSnapshot? readiness = null;
        bool weaponReady = false;
        if (!medicineUsing && botOwner != null)
        {
            try
            {
                readiness = VanguardPostLootWeaponReadinessReader.Capture(botOwner);
                weaponReady = readiness.WeaponReady && !readiness.FirstAidUsing;
                lease.LastNativeCancelHandsReadiness = readiness.Summary;
            }
            catch (Exception exception)
            {
                lease.LastNativeCancelHandsReadiness = "readiness_exception:" + Safe(exception.GetType().Name);
            }
        }
        else
        {
            lease.LastNativeCancelHandsReadiness = medicineUsing ? "medicine_controller_still_active" : "bot_owner_missing";
        }

        TimeSpan elapsed = lease.SurgeryStartRetryRequestedAtUtc == DateTimeOffset.MinValue
            ? TimeSpan.Zero
            : now - lease.SurgeryStartRetryRequestedAtUtc;
        if (medicineUsing || !weaponReady)
        {
            lease.NativeCancelHandsReadySinceUtc = DateTimeOffset.MinValue;
            lease.NativeCancelHandsReadySnapshotCount = 0;
            lease.LastProgressAtUtc = now;
            lease.LastProgressKind = "surgery_start_retry_waiting_for_positive_hands_truth";
            lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(1.00d);

            if (!lease.NativeCancelHandsRecoveryAttempted && elapsed >= NativeCancelHandsRecoveryDelay)
            {
                lease.NativeCancelHandsRecoveryAttempted = true;
                string recovery = RecoverControllerAtTerminalBoundary(botOwner, snapshot, lease, "surgery_start_retry_hands_delay");
                VanguardClientDiagnosticsLog.Warning(MedicalNativeCommitStatusTag,
                    $"VANGUARD_NATIVE_SURGERY_START_RETRY_HANDS_RECOVERY {lease.Summary}; elapsed={elapsed.TotalSeconds:0.00}; medicineUsing={Bool(medicineUsing)}; weaponReady={Bool(weaponReady)}; recovery={Safe(recovery)}; keepLease=true; tag={MedicalNativeCommitStatusTag}");
            }

            if (elapsed >= NativeStartRetryHandsDrainTimeout)
            {
                lease.SurgeryStartRetryPending = false;
                CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Failed,
                    "NativeSurgeryStartRetryHandsReturnTimeout:" + Safe(lease.LastNativeCancelHandsReadiness),
                    NoEffectCooldown, progress);
            }
            return true;
        }

        if (lease.LastNativeCancelHandsSnapshotAtUtc == snapshot.CapturedAtUtc)
        {
            return true;
        }

        lease.LastNativeCancelHandsSnapshotAtUtc = snapshot.CapturedAtUtc;
        if (lease.NativeCancelHandsReadySinceUtc == DateTimeOffset.MinValue)
        {
            lease.NativeCancelHandsReadySinceUtc = now;
            lease.NativeCancelHandsReadySnapshotCount = 1;
            lease.LastProgressKind = "surgery_start_retry_hands_ready_candidate_1";
            return true;
        }

        lease.NativeCancelHandsReadySnapshotCount = Math.Min(NativeCancelHandsRequiredSnapshots, lease.NativeCancelHandsReadySnapshotCount + 1);
        if (lease.NativeCancelHandsReadySnapshotCount < NativeCancelHandsRequiredSnapshots
            || now - lease.NativeCancelHandsReadySinceUtc < NativeCancelHandsStableWindow)
        {
            return true;
        }

        return TryRestartStalledSurgery(lease, botOwner, snapshot, now, progress);
    }

    private static bool TryRestartStalledSurgery(
        VanguardExecutionLeaseState lease,
        BotOwner? botOwner,
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        VanguardMedicalActionProgressSnapshot progress)
    {
        lease.SurgeryStartRetryPending = false;
        if (botOwner == null || !snapshot.Alive || botOwner.IsDead)
        {
            CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Failed, "NativeSurgeryStartRetryOperatorUnavailable", FailureCooldown, progress);
            return true;
        }

        VanguardMedicalActionProgressSnapshot freshProgress = VanguardMedicalActionProgressReader.Capture(lease, botOwner, snapshot);
        if (freshProgress.SurgeryTargetRestored)
        {
            CompleteResolvedLeaseOrDrainHands(lease, botOwner, snapshot, now, freshProgress,
                "SurgeryTargetRestoredDuringStartStallCancellation", SuccessCooldown);
            return true;
        }

        if (freshProgress.ItemResourceConsumed)
        {
            ObserveSurgeryCommitTruth(lease, now, freshProgress);
            if (freshProgress.SurgeryTargetRestored)
            {
                CompleteResolvedLeaseOrDrainHands(lease, botOwner, snapshot, now, freshProgress, "SurgeryCommittedDuringStartStallCancellation", SuccessCooldown);
                return true;
            }

            if (TryRepairCommittedSurgeryEffect(lease, botOwner, snapshot, now, freshProgress, "start_stall_cancel_resource_commit", out string repairSummary))
            {
                lease.PostUseRecheckUntilUtc = Min(now + TimeSpan.FromSeconds(1.50d), lease.AbsoluteMaxUntilUtc);
                lease.NextPostUseRecheckAtUtc = now + TimeSpan.FromSeconds(0.20d);
                lease.NoProgressUntilUtc = lease.PostUseRecheckUntilUtc;
                VanguardClientDiagnosticsLog.Info(MedicalNativeCommitStatusTag,
                    $"VANGUARD_NATIVE_SURGERY_START_STALL_COMMITTED_DURING_CANCEL {lease.Summary}; {repairSummary}; action=repair_then_body_part_truth_recheck; noSecondApply=true; tag={MedicalNativeCommitStatusTag}");
                return true;
            }

            lease.ItemUseObserved = true;
            lease.FirstAidEndedObserved = true;
            lease.PostUseRecheckUntilUtc = Min(now + TimeSpan.FromSeconds(2.00d), lease.AbsoluteMaxUntilUtc);
            lease.NextPostUseRecheckAtUtc = now + TimeSpan.FromSeconds(0.20d);
            lease.NoProgressUntilUtc = lease.PostUseRecheckUntilUtc;
            lease.LastProgressKind = "start_stall_cancel_commit_effect_recheck_pending";
            VanguardClientDiagnosticsLog.Warning(MedicalNativeCommitStatusTag,
                $"VANGUARD_NATIVE_SURGERY_START_STALL_COMMIT_REPAIR_PENDING {lease.Summary}; action=bounded_body_part_truth_recheck; noSecondApply=true; gameplayFailure=false; tag={MedicalNativeCommitStatusTag}");
            return true;
        }

        progress = freshProgress;
        if (HasTrueMedicalAbortThreat(snapshot, out string threatReason))
        {
            CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Interrupted,
                "NativeSurgeryStartRetryCancelledByTrueThreat:" + Safe(threatReason),
                StationarySurgeryInterruptedRetryCooldown, progress);
            return true;
        }

        if (!progress.TargetStillPresent || !Enum.TryParse(lease.TargetPart, true, out EBodyPart targetPart)
            || !VanguardMedicalSurgeryTargetPolicy.IsValidSurgeryTarget(targetPart.ToString()))
        {
            CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Failed,
                "NativeSurgeryStartRetryTargetUnavailable:" + Safe(lease.TargetPart),
                NoEffectCooldown, progress);
            return true;
        }

        if (!TryFindExactMedicalItem(botOwner, lease.ItemInstanceId, out MedsItemClass? exactItem)
            || exactItem == null)
        {
            CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Failed,
                "NativeSurgeryStartRetryConsumableUnavailable:" + Safe(lease.ItemInstanceId),
                NoEffectCooldown, progress);
            return true;
        }

        object? medecine = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Medecine");
        var surgicalKit = botOwner.Medecine?.SurgicalKit;
        InvokeNoArgBool(medecine, "RefreshCurMeds", "RefreshMeds");
        InvokeNoArgBool(surgicalKit, "Refresh", "RefreshMeds");
        string commitReason = "surgical_kit_missing";
        if (surgicalKit == null || !TryValidateSingleSurgeryCommit(botOwner, surgicalKit.Using, exactItem, targetPart, out commitReason))
        {
            CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Failed,
                "NativeSurgeryStartRetryCommitUnavailable:" + Safe(commitReason),
                NoEffectCooldown, progress);
            return true;
        }

        surgicalKit.CurUsingMeds = exactItem;
        surgicalKit.Nullable_0 = targetPart;
        lease.SurgeryCancellationRequested = false;
        lease.SurgeryCancellationRequestedAtUtc = DateTimeOffset.MinValue;
        lease.SurgeryCancellationReason = string.Empty;
        lease.SurgeryCancellationKind = string.Empty;
        lease.SurgeryCancellationIsThreat = false;
        lease.SurgeryControllerCallbackObserved = false;
        lease.SurgeryControllerCallbackAtUtc = DateTimeOffset.MinValue;
        lease.SurgeryNativeHandsCommitObserved = false;
        lease.SurgeryNativeHandsCommitObservedAtUtc = DateTimeOffset.MinValue;
        lease.SurgeryNativeHandsMismatchSinceUtc = DateTimeOffset.MinValue;
        lease.SurgeryNativeHandsMismatchSnapshotCount = 0;
        lease.SurgeryResourceCommitObserved = false;
        lease.SurgeryResourceCommitObservedAtUtc = DateTimeOffset.MinValue;
        lease.SurgeryTerminalItemDepletionCommitObserved = false;
        lease.SurgeryTerminalItemDepletionCommitObservedAtUtc = DateTimeOffset.MinValue;
        lease.SurgeryTerminalItemAbsenceSinceUtc = DateTimeOffset.MinValue;
        lease.LastSurgeryTerminalItemAbsenceSnapshotAtUtc = DateTimeOffset.MinValue;
        lease.SurgeryTerminalItemAbsenceSnapshotCount = 0;
        lease.SurgeryTargetEffectConfirmed = false;
        lease.SurgeryTargetEffectConfirmedAtUtc = DateTimeOffset.MinValue;
        lease.ItemUseObserved = false;
        lease.FirstAidEndedObserved = false;
        lease.PostUseRecheckUntilUtc = DateTimeOffset.MinValue;
        lease.NextPostUseRecheckAtUtc = DateTimeOffset.MinValue;
        ResetNativeCancelHandsTruth(lease);

        TimeSpan retryWindow = IsSurv12(lease.ItemTemplateId, lease.ItemName)
            ? TimeSpan.FromSeconds(40.00d)
            : TimeSpan.FromSeconds(30.00d);
        lease.LastSurgeryApplyAttemptAtUtc = now;
        lease.SurgeryApplyAttemptCount++;
        lease.LastProgressAtUtc = now;
        lease.LastProgressKind = "native_surgery_start_internal_retry_applied";
        lease.MaxUntilUtc = now + retryWindow;
        lease.AbsoluteMaxUntilUtc = lease.MaxUntilUtc + TimeSpan.FromSeconds(8.00d);
        lease.NoProgressUntilUtc = now + retryWindow;

        try
        {
            surgicalKit.ApplyToCurrentPart(() => ObserveSurgeryControllerCallback(lease.BotProfileId, lease.LeaseId));
        }
        catch (Exception exception)
        {
            CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Failed,
                "NativeSurgeryStartRetryApplyException:" + Safe(exception.GetType().Name),
                NoEffectCooldown, progress);
            VanguardClientDiagnosticsLog.Warning(MedicalNativeCommitStatusTag,
                $"VANGUARD_NATIVE_SURGERY_START_RETRY_APPLY_EXCEPTION {lease.Summary}; exception={Safe(exception.GetType().Name)}:{Safe(exception.Message)}; resourceStillUnconsumed=true; targetStillDestroyed=true; maxInternalRetries={MaxInternalSurgeryStartRetries}; furtherRetryAllowed={Bool(lease.SurgeryStartRetryCount < MaxInternalSurgeryStartRetries)}; tag={MedicalNativeCommitStatusTag}");
            return true;
        }

        VanguardClientDiagnosticsLog.Info(MedicalNativeCommitStatusTag,
            $"VANGUARD_NATIVE_SURGERY_START_RETRIED {lease.Summary}; retryCount={lease.SurgeryStartRetryCount}; applyCalls={lease.SurgeryApplyAttemptCount}; exactItem=true; exactTarget=true; resourceStillUnconsumed=true; coverAndAuthorityPreserved=true; retryWindow={retryWindow.TotalSeconds:0.00}; maxInternalRetries={MaxInternalSurgeryStartRetries}; furtherRetryAllowed={Bool(lease.SurgeryStartRetryCount < MaxInternalSurgeryStartRetries)}; tag={MedicalNativeCommitStatusTag}");
        return true;
    }

    private static bool TryFindExactMedicalItem(BotOwner botOwner, string itemInstanceId, out MedsItemClass? item)
    {
        item = null;
        var inventory = VanguardMedicalInventoryReader.Capture(botOwner);
        foreach (var items in inventory.ItemsByTemplateId.Values)
        {
            foreach (var candidate in items)
            {
                if (string.Equals(VanguardMedicalInventoryReader.ResolveItemInstanceId(candidate), itemInstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    item = candidate;
                    return true;
                }
            }
        }
        return false;
    }

    private static bool HoldCommittedSurgeryUntilControllerTerminal(VanguardExecutionLeaseState lease, BotOwner? botOwner, DateTimeOffset now, VanguardMedicalActionProgressSnapshot progress, string source)
    {
        if (!IsSurgeryNeed(lease.MedicalNeed) || !IsSurgeryControllerActive(botOwner, progress))
        {
            return false;
        }

        ObserveSurgeryCommitTruth(lease, now, progress);

        if (now >= lease.AbsoluteMaxUntilUtc)
        {
            string timeoutReason = lease.SurgeryTargetEffectConfirmed
                ? "confirmed_effect_native_terminal_timeout:" + Safe(source)
                : lease.SurgeryResourceCommitObserved
                    ? "resource_consumed_no_target_effect:" + Safe(source)
                    : "native_terminal_deadline_exceeded_without_commit:" + Safe(source);
            TryCancelCommittedSurgery(lease, botOwner, now, progress, timeoutReason, "controller_terminal_timeout", isThreat: false);
            return true;
        }

        if (lease.SurgeryTargetEffectConfirmed)
        {
            DateTimeOffset settleAnchor = lease.SurgeryTargetEffectConfirmedAtUtc == DateTimeOffset.MinValue
                ? now
                : lease.SurgeryTargetEffectConfirmedAtUtc;
            DateTimeOffset settleUntil = Min(lease.AbsoluteMaxUntilUtc, settleAnchor + SurgeryConfirmedTerminalSettleWindow);
            if (now >= settleUntil)
            {
                TryCancelCommittedSurgery(lease, botOwner, now, progress,
                    "confirmed_effect_hands_not_returned:" + Safe(source),
                    "controller_terminal_timeout",
                    isThreat: false);
                return true;
            }
        }

        lease.NoProgressUntilUtc = Min(lease.AbsoluteMaxUntilUtc, now + TimeSpan.FromSeconds(1.00d));
        lease.LastProgressAtUtc = now;
        lease.LastProgressKind = lease.SurgeryTargetEffectConfirmed
            ? "confirmed_surgery_effect_waiting_for_native_terminal:" + Safe(source)
            : lease.SurgeryResourceCommitObserved
                ? "surgery_resource_commit_waiting_for_target_effect:" + Safe(source)
                : "committed_surgery_bounded_native_terminal_wait:" + Safe(source);
        if (ShouldLogRecheck(lease.BotProfileId + "|surgery_native_terminal_wait|" + lease.LeaseId, now))
        {
            VanguardClientDiagnosticsLog.Trace(MedicalProcedureCompletionGateStatusTag, () =>
                $"VANGUARD_SURGERY_NATIVE_TERMINAL_WAIT lease={Safe(lease.LeaseId)}; botProfile={Safe(lease.BotProfileId)}; target={Safe(lease.TargetPart)}; source={Safe(source)}; controllerUsing=true; resourceCommit={Bool(lease.SurgeryResourceCommitObserved)}; effectConfirmed={Bool(lease.SurgeryTargetEffectConfirmed)}; applyCalls={lease.SurgeryApplyAttemptCount}; absoluteRemaining={Math.Max(0d, (lease.AbsoluteMaxUntilUtc - now).TotalSeconds):0.00}; fullLeasePayload=false; tag={MedicalProcedureCompletionGateStatusTag}");
        }

        return true;
    }

    private static bool TryObserveTerminalSurgeryItemDepletionCommit(
        VanguardExecutionLeaseState lease,
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        VanguardMedicalActionProgressSnapshot progress,
        string source,
        out string summary)
    {
        summary = "terminalItemCommit=false";
        if (!IsSurgeryNeed(lease.MedicalNeed)
            || lease.SurgeryTerminalItemDepletionCommitObserved
            || lease.SurgeryResourceCommitObserved
            || lease.SurgeryCancellationRequested
            || lease.SurgeryCancellationIsThreat
            || lease.InitialItemResource <= 0.01f
            || lease.InitialItemResource > SurgeryTerminalItemLastChargeMaximum
            || lease.SurgeryApplyAttemptCount <= 0
            || !lease.ItemUseObserved
            || !lease.SurgeryControllerCallbackObserved
            || !lease.SurgeryNativeHandsCommitObserved
            || !progress.TerminalAliveConfirmed
            || !progress.TargetDestroyedReadable
            || !progress.CurrentTargetDestroyed
            || !progress.TargetStillPresent)
        {
            ResetTerminalItemAbsenceCandidateIfVisible(lease, progress);
            return false;
        }

        snapshot ??= OperatorDecisionSnapshot.Empty;
        if (snapshot.Alive && HasTrueMedicalAbortThreat(snapshot, out var threatReason))
        {
            ResetTerminalItemAbsenceCandidate(lease);
            summary = "terminalItemCommit=false;reason=true_threat:" + Safe(threatReason);
            return false;
        }

        if (!progress.ItemInventoryObserved)
        {
            summary = "terminalItemCommit=false;reason=inventory_not_observed";
            return false;
        }

        if (!progress.ExactItemAbsentFromObservedInventory)
        {
            ResetTerminalItemAbsenceCandidateIfVisible(lease, progress);
            summary = progress.ItemInstanceFound
                ? "terminalItemCommit=false;reason=exact_item_still_present"
                : "terminalItemCommit=false;reason=exact_item_absence_not_proven";
            return false;
        }

        DateTimeOffset snapshotAt = snapshot.CapturedAtUtc == DateTimeOffset.MinValue ? now : snapshot.CapturedAtUtc;
        if (lease.SurgeryTerminalItemAbsenceSinceUtc == DateTimeOffset.MinValue)
        {
            lease.SurgeryTerminalItemAbsenceSinceUtc = now;
            lease.LastSurgeryTerminalItemAbsenceSnapshotAtUtc = snapshotAt;
            lease.SurgeryTerminalItemAbsenceSnapshotCount = 1;
            lease.LastProgressKind = "terminal_surgery_item_absence_candidate";
            summary = "terminalItemCommit=false;reason=first_absence_snapshot";
            return false;
        }

        if (lease.LastSurgeryTerminalItemAbsenceSnapshotAtUtc != snapshotAt)
        {
            lease.LastSurgeryTerminalItemAbsenceSnapshotAtUtc = snapshotAt;
            lease.SurgeryTerminalItemAbsenceSnapshotCount = Math.Min(
                SurgeryTerminalItemAbsenceRequiredSnapshots,
                lease.SurgeryTerminalItemAbsenceSnapshotCount + 1);
        }

        TimeSpan absenceFor = now - lease.SurgeryTerminalItemAbsenceSinceUtc;
        if (lease.SurgeryTerminalItemAbsenceSnapshotCount < SurgeryTerminalItemAbsenceRequiredSnapshots
            || absenceFor < SurgeryTerminalItemAbsenceStableWindow)
        {
            summary = "terminalItemCommit=false;reason=absence_confirmation_pending;snapshots="
                + lease.SurgeryTerminalItemAbsenceSnapshotCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ";absenceFor=" + absenceFor.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            return false;
        }

        lease.SurgeryTerminalItemDepletionCommitObserved = true;
        lease.SurgeryTerminalItemDepletionCommitObservedAtUtc = now;
        lease.SurgeryResourceCommitObserved = true;
        DateTimeOffset inferredCommitAt = lease.SurgeryControllerCallbackAtUtc != DateTimeOffset.MinValue
            ? lease.SurgeryControllerCallbackAtUtc
            : lease.SurgeryNativeHandsCommitObservedAtUtc != DateTimeOffset.MinValue
                ? lease.SurgeryNativeHandsCommitObservedAtUtc
                : now;
        lease.SurgeryResourceCommitObservedAtUtc = inferredCommitAt;
        lease.LastProgressAtUtc = now;
        lease.LastProgressKind = "surgery_terminal_item_depletion_commit_observed";
        summary = "terminalItemCommit=true;initialResource="
            + lease.InitialItemResource.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            + ";inventoryObserved=true;exactItemAbsent=true;absenceSnapshots="
            + lease.SurgeryTerminalItemAbsenceSnapshotCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ";absenceFor=" + absenceFor.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            + ";callback=true;nativeHands=true;targetStillDestroyed=true;source=" + Safe(source);

        VanguardClientDiagnosticsLog.Info(SurgeryTerminalItemCommitStatusTag,
            $"VANGUARD_SURGERY_TERMINAL_ITEM_COMMIT_OBSERVED {lease.Summary}; {summary}; inferredCommitAt={inferredCommitAt:O}; resourceRewrite=false; inventoryMutation=false; exactInstanceOnly=true; lastChargeOnly=true; repairStillRequiresThreatAndBodyPartTruth=true; tag={SurgeryTerminalItemCommitStatusTag}");
        return true;
    }

    private static void ResetTerminalItemAbsenceCandidateIfVisible(
        VanguardExecutionLeaseState lease,
        VanguardMedicalActionProgressSnapshot progress)
    {
        if (!progress.ItemInventoryObserved || !progress.ItemInstanceFound)
        {
            return;
        }

        ResetTerminalItemAbsenceCandidate(lease);
    }

    private static void ResetTerminalItemAbsenceCandidate(VanguardExecutionLeaseState lease)
    {
        lease.SurgeryTerminalItemAbsenceSinceUtc = DateTimeOffset.MinValue;
        lease.LastSurgeryTerminalItemAbsenceSnapshotAtUtc = DateTimeOffset.MinValue;
        lease.SurgeryTerminalItemAbsenceSnapshotCount = 0;
    }

    private static void ObserveSurgeryCommitTruth(VanguardExecutionLeaseState lease, DateTimeOffset now, VanguardMedicalActionProgressSnapshot progress)
    {
        if (progress.ItemResourceConsumed && !lease.SurgeryResourceCommitObserved)
        {
            lease.SurgeryResourceCommitObserved = true;
            lease.SurgeryResourceCommitObservedAtUtc = now;
        }

        bool effectConfirmed = progress.SurgeryTargetRestored;
        if (effectConfirmed && !lease.SurgeryTargetEffectConfirmed)
        {
            lease.SurgeryTargetEffectConfirmed = true;
            lease.SurgeryTargetEffectConfirmedAtUtc = now;
        }
    }

    private static bool TryHandleResourceConsumedNoTargetEffect(
        VanguardExecutionLeaseState lease,
        BotOwner? botOwner,
        DateTimeOffset now,
        VanguardMedicalActionProgressSnapshot progress)
    {
        if (!IsSurgeryNeed(lease.MedicalNeed))
        {
            return false;
        }

        ObserveSurgeryCommitTruth(lease, now, progress);
        if (!lease.SurgeryResourceCommitObserved
            || lease.SurgeryTargetEffectConfirmed
            || lease.SurgeryResourceCommitObservedAtUtc == DateTimeOffset.MinValue)
        {
            return false;
        }

        TimeSpan timeout = IsSurv12(lease.ItemTemplateId, lease.ItemName)
            ? SurgeryResourceNoEffectSurv12Timeout
            : SurgeryResourceNoEffectCmsTimeout;
        if (now - lease.SurgeryResourceCommitObservedAtUtc < timeout)
        {
            return false;
        }

        string terminalReason = "ResourceConsumedNoTargetEffectAfter"
            + timeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            + "s";
        OperatorDecisionSnapshot latestSnapshot = OperatorDecisionSnapshot.Empty;
        VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(lease.BotProfileId, out latestSnapshot);
        if (TryRepairCommittedSurgeryEffect(lease, botOwner, latestSnapshot, now, progress, "resource_commit_without_native_effect", out var repairSummary))
        {
            lease.NoProgressUntilUtc = Min(lease.AbsoluteMaxUntilUtc, now + TimeSpan.FromSeconds(1.50d));
            bool controllerActive = IsSurgeryControllerActive(botOwner, progress);
            if (controllerActive)
            {
                TryCancelCommittedSurgery(lease, botOwner, now, progress,
                    "effect_repaired_after_resource_commit",
                    "effect_repaired_terminal_cleanup",
                    isThreat: false);
            }
            VanguardClientDiagnosticsLog.Info(MedicalSurgeryDeterministicCompletionStatusTag,
                $"VANGUARD_SURGERY_EFFECT_REPAIRED {lease.Summary}; source=resource_commit_timeout; {repairSummary}; next={(controllerActive ? "native_cancel_then_hands_truth" : "body_part_truth_recheck")}; controllerCancellation={Bool(controllerActive)}; gameplayFailure=false; tag={MedicalSurgeryDeterministicCompletionStatusTag}");
            return true;
        }
        if (!IsSurgeryControllerActive(botOwner, progress))
        {
            lease.LastProgressKind = "resource_consumed_no_target_effect_controller_terminal";
            CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Failed, terminalReason, NoEffectCooldown, progress);
            VanguardClientDiagnosticsLog.Warning(MedicalProcedureCompletionGateStatusTag, () =>
                $"VANGUARD_SURGERY_RESOURCE_NO_EFFECT_TERMINAL lease={Safe(lease.LeaseId)}; operator={Safe(lease.OperatorId)}; botProfile={Safe(lease.BotProfileId)}; target={Safe(lease.TargetPart)}; controllerUsing=false; timeout={timeout.TotalSeconds:0}; resourceConsumed=true; targetEffect=false; secondApply=false; outcome=Failed; tag={MedicalProcedureCompletionGateStatusTag}");
            return true;
        }

        TryCancelCommittedSurgery(
            lease,
            botOwner,
            now,
            progress,
            "resource_consumed_no_target_effect_after_" + timeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "s",
            "resource_consumed_no_target_effect",
            isThreat: false);
        return true;
    }

    private static bool IsNoEffectReason(string reason, VanguardMedicalActionProgressSnapshot? progress)
    {
        return reason.IndexOf("WhileControllerUsing", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("NoMedicalEffect", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("NeedStillPresentNoEffect", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("ConditionUnresolved", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("PostOrbitMedicalGhostUseNoEffect", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool HasConfirmedNativeMedicalAttempt(
        VanguardExecutionLeaseState lease,
        VanguardMedicalActionProgressSnapshot? progress,
        string reason)
    {
        if (!lease.Attempted
            || reason.IndexOf("BeforeUse", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("CommitDeferred", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        return lease.ItemUseObserved
            || lease.FirstAidEndedObserved
            || lease.PostUseRecheckCount > 0
            || progress?.ItemResourceConsumed == true
            || reason.IndexOf("WhileControllerUsing", StringComparison.OrdinalIgnoreCase) >= 0;
    }


    private static string BuildEffectDeltaSummary(VanguardExecutionLeaseState lease, VanguardMedicalActionProgressSnapshot? progress)
    {
        if (progress == null)
        {
            return "effectDelta=unknown";
        }

        string targetDelta = lease.InitialTargetHealth >= 0f && progress.CurrentTargetHealth >= 0f
            ? (progress.CurrentTargetHealth - lease.InitialTargetHealth).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";
        string hpDelta = lease.InitialHealthPercent >= 0 && progress.CurrentHealthPercent >= 0
            ? (progress.CurrentHealthPercent - lease.InitialHealthPercent).ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";
        string initialTarget = lease.InitialTargetHealth >= 0f
            ? lease.InitialTargetHealth.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "/" + lease.InitialTargetMaxHealth.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";
        return "effectDelta=hpDelta=" + hpDelta
            + ";targetDelta=" + targetDelta
            + ";initialHp=" + lease.InitialHealthPercent.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            + ";initialTargetHp=" + initialTarget
            + ";currentTargetHp=" + progress.CurrentTargetHealth.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "/" + progress.CurrentTargetMaxHealth.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            + ";uiBarMayLagSnapshot=true";
    }

    private static TimeSpan PostUseRecheckCadenceForLease(VanguardExecutionLeaseState lease)
    {
        return IsSurgeryNeed(lease.MedicalNeed) || lease.MedicalNeed == Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.Fracture
            ? StationaryPostUseRecheckCadence
            : MobilePostUseRecheckCadence;
    }

    private static string RecoverControllerAtTerminalBoundary(EFT.BotOwner? botOwner, OperatorDecisionSnapshot snapshot, VanguardExecutionLeaseState lease, string boundaryReason)
    {
        if (botOwner == null)
        {
            return "controllerRecovery=bot_owner_missing";
        }

        try
        {
            string requestSummary = VanguardMedicalHandsWatchdogService.RequestTerminalCancellation(
                botOwner,
                snapshot,
                lease,
                boundaryReason,
                DateTimeOffset.UtcNow);
            VanguardClientDiagnosticsLog.Warning(
                VanguardMedicalHandsWatchdogService.MedicalLeaseStatusTag,
                $"VANGUARD_MEDICAL_TERMINAL_CONTROLLER_RECOVERY_REQUESTED {lease.Summary}; recovered=false; request={Safe(requestSummary)}; reason={Safe(boundaryReason)}; next=release_lease_then_watchdog_positive_terminal_truth; rawControllerFieldClear=false; outcomeNotProvenByMutation=true; tag={VanguardMedicalHandsWatchdogService.MedicalLeaseStatusTag}; legacyTag={ControllerRecoveryStatusTag}; outcomeBridgeTag={VanguardMedicalExecutionResultBridge.StatusTag}");
            return "controllerRecovery=requested_unconfirmed:" + Safe(requestSummary);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardMedicalHandsWatchdogService.MedicalLeaseStatusTag,
                $"VANGUARD_MEDICAL_TERMINAL_CONTROLLER_RECOVERY_REQUEST_FAILED {lease.Summary}; boundary={Safe(boundaryReason)}; reason={Safe(exception.GetType().Name)}:{Safe(exception.Message)}; next=release_lease_then_watchdog_observation; rawControllerFieldClear=false; tag={VanguardMedicalHandsWatchdogService.MedicalLeaseStatusTag}; legacyTag={ControllerRecoveryStatusTag}; outcomeBridgeTag={VanguardMedicalExecutionResultBridge.StatusTag}");
            return "controllerRecovery=request_exception:" + Safe(exception.GetType().Name);
        }
    }

    private static bool TryExtendControllerUsingGrace(VanguardExecutionLeaseState lease, DateTimeOffset now, VanguardMedicalActionProgressSnapshot progress, string source)
    {
        if (lease.ControllerUsingGraceApplied || !progress.FirstAidUsing || now >= lease.AbsoluteMaxUntilUtc)
        {
            return false;
        }

        TimeSpan grace = IsSurgeryNeed(lease.MedicalNeed) ? SurgeryControllerUsingGrace : MobileControllerUsingGrace;
        lease.ControllerUsingGraceApplied = true;
        lease.MaxUntilUtc = Min(lease.AbsoluteMaxUntilUtc, now + grace);
        lease.NoProgressUntilUtc = lease.MaxUntilUtc;
        lease.LastProgressAtUtc = now;
        lease.LastProgressKind = "controller_using_grace:" + Safe(source);
        VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.OpportunisticMedicalStatusTag,
            $"VANGUARD_MEDICAL_CONTROLLER_USING_GRACE {lease.Summary}; source={Safe(source)}; grace={grace.TotalSeconds:0.00}; surgery={Bool(IsSurgeryNeed(lease.MedicalNeed))}; doctrine=single_bounded_controller_grace_absolute_cap_prevents_false_timeout_and_long_ambiguous_lease; tag={VanguardPrimaryExecutionContract.OpportunisticMedicalStatusTag}; medicalTag={StatusTag}");
        return true;
    }

    private static void LogCombatDrainOnly(
        VanguardExecutionLeaseState lease,
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        bool mobileLease,
        bool mobileSidecarStillAllowed,
        string sidecarReason,
        string combatProtectionReason)
    {
        if (!ShouldLogRecheck(snapshot.BotProfileId + "|medical_drain_only|" + lease.LeaseId, now))
        {
            return;
        }

        VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.SainWindowStatusTag,
            $"VANGUARD_MEDICAL_ACTION_DRAIN_ONLY operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(lease.LeaseId)}; window={Safe(lease.WindowKind)}; mobile={Bool(mobileLease)}; sidecarAllowed={Bool(mobileSidecarStillAllowed)}; sidecarReason={Safe(sidecarReason)}; combatReason={Safe(combatProtectionReason)}; itemUseObserved={Bool(lease.ItemUseObserved)}; doctrine=already_started_controller_may_finish_without_new_chain_movement_or_retry; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; medicalTag={StatusTag}");
    }

    private static void CompleteResolvedLeaseOrDrainHands(
        VanguardExecutionLeaseState lease,
        EFT.BotOwner? botOwner,
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        VanguardMedicalActionProgressSnapshot progress,
        string reason,
        TimeSpan cooldown)
    {
        if (ShouldDrainHandsBeforeTerminal(lease, snapshot, progress))
        {
            BeginEffectResolvedHandsDrain(lease, now, progress, reason);
            return;
        }

        lease.FirstAidEndedObserved = true;
        CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Completed, reason, cooldown, progress);
    }

    private static bool ShouldDrainHandsBeforeTerminal(VanguardExecutionLeaseState lease, OperatorDecisionSnapshot snapshot, VanguardMedicalActionProgressSnapshot progress)
    {
        var actionability = snapshot.Medical.Actionability;
        return progress.FirstAidUsing
            || actionability.AnyMedicineUsing
            || actionability.FirstAidUsing
            || actionability.SurgicalKitUsing
            || actionability.StimulatorUsing;
    }

    private static void BeginEffectResolvedHandsDrain(VanguardExecutionLeaseState lease, DateTimeOffset now, VanguardMedicalActionProgressSnapshot progress, string reason)
    {
        if (!lease.EffectResolvedAwaitingHandsRelease)
        {
            lease.EffectResolvedAwaitingHandsRelease = true;
            lease.HandsDrainStartedAtUtc = now;
            lease.HandsReleasedSinceUtc = DateTimeOffset.MinValue;
            lease.LastHandsDrainSnapshotAtUtc = DateTimeOffset.MinValue;
            lease.HandsReleasedSnapshotCount = 0;
            lease.HandsDrainRecoveryAttempted = false;
            lease.AbsoluteMaxUntilUtc = Max(lease.AbsoluteMaxUntilUtc, now + EffectResolvedHandsAbsoluteWindow);
            lease.MaxUntilUtc = Max(lease.MaxUntilUtc, lease.AbsoluteMaxUntilUtc);
            lease.NoProgressUntilUtc = lease.AbsoluteMaxUntilUtc;
            lease.LastProgressAtUtc = now;
            lease.LastProgressKind = "effect_resolved_draining_hands:" + Safe(reason);
            VanguardClientDiagnosticsLog.Info(MedicalHandsSettleStatusTag,
                $"VANGUARD_MEDICAL_EFFECT_RESOLVED_HANDS_DRAIN_STARTED {lease.Summary}; reason={Safe(reason)}; firstAidUsing={Bool(progress.FirstAidUsing)}; stableWindow={EffectResolvedHandsStableWindow.TotalSeconds:0.00}; requiredSnapshots={EffectResolvedHandsRequiredSnapshots}; absoluteWindow={EffectResolvedHandsAbsoluteWindow.TotalSeconds:0.00}; noNewMedicalItem=true; terminalDeferred=true; tag={MedicalHandsSettleStatusTag}");
        }
    }

    private static void HandleEffectResolvedHandsDrain(
        VanguardExecutionLeaseState lease,
        EFT.BotOwner? botOwner,
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        VanguardMedicalActionProgressSnapshot progress)
    {
        bool usingMedicine = progress.FirstAidUsing
            || snapshot.Medical.Actionability.AnyMedicineUsing
            || snapshot.Medical.Actionability.FirstAidUsing
            || snapshot.Medical.Actionability.SurgicalKitUsing
            || snapshot.Medical.Actionability.StimulatorUsing;

        if (usingMedicine)
        {
            lease.HandsReleasedSinceUtc = DateTimeOffset.MinValue;
            lease.HandsReleasedSnapshotCount = 0;
            lease.LastProgressAtUtc = now;
            lease.LastProgressKind = "effect_resolved_waiting_hands_release";
            lease.NoProgressUntilUtc = lease.AbsoluteMaxUntilUtc;

            if (!lease.HandsDrainRecoveryAttempted
                && lease.HandsDrainStartedAtUtc != DateTimeOffset.MinValue
                && now - lease.HandsDrainStartedAtUtc >= EffectResolvedHandsRecoveryDelay)
            {
                lease.HandsDrainRecoveryAttempted = true;
                string recovery = RecoverControllerAtTerminalBoundary(botOwner, snapshot, lease, "effect_resolved_hands_recovery_delay");
                VanguardClientDiagnosticsLog.Warning(MedicalHandsSettleStatusTag,
                    $"VANGUARD_MEDICAL_EFFECT_RESOLVED_HANDS_RECOVERY {lease.Summary}; recovery={Safe(recovery)}; stillUsing=true; retryNewMedical=false; tag={MedicalHandsSettleStatusTag}");
            }

            if (now < lease.AbsoluteMaxUntilUtc)
            {
                return;
            }

            lease.FirstAidEndedObserved = false;
            CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Completed,
                "MedicalEffectResolvedHandsDrainBoundedRelease", EffectResolvedHandsBoundedCooldown, progress);
            return;
        }

        if (lease.LastHandsDrainSnapshotAtUtc == snapshot.CapturedAtUtc)
        {
            return;
        }

        lease.LastHandsDrainSnapshotAtUtc = snapshot.CapturedAtUtc;
        if (lease.HandsReleasedSinceUtc == DateTimeOffset.MinValue)
        {
            lease.HandsReleasedSinceUtc = now;
            lease.HandsReleasedSnapshotCount = 1;
            lease.LastProgressKind = "hands_release_candidate_1";
            return;
        }

        lease.HandsReleasedSnapshotCount = Math.Min(EffectResolvedHandsRequiredSnapshots, lease.HandsReleasedSnapshotCount + 1);
        bool stable = lease.HandsReleasedSnapshotCount >= EffectResolvedHandsRequiredSnapshots
            && now - lease.HandsReleasedSinceUtc >= EffectResolvedHandsStableWindow;
        if (!stable)
        {
            lease.LastProgressKind = "hands_release_candidate_" + lease.HandsReleasedSnapshotCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return;
        }

        lease.FirstAidEndedObserved = true;
        VanguardClientDiagnosticsLog.Info(MedicalHandsSettleStatusTag,
            $"VANGUARD_MEDICAL_EFFECT_RESOLVED_HANDS_RELEASE_CONFIRMED {lease.Summary}; stableSeconds={(now - lease.HandsReleasedSinceUtc).TotalSeconds:0.00}; snapshots={lease.HandsReleasedSnapshotCount}; newMedicalAllowed=true; tag={MedicalHandsSettleStatusTag}");
        CompleteLease(lease, now, VanguardMedicalActionOutcomeKind.Completed,
            "MedicalEffectResolvedHandsReleased", EffectResolvedHandsHandoffCooldown, progress);
    }

    private static void CompleteLease(VanguardExecutionLeaseState lease, DateTimeOffset now, VanguardMedicalActionOutcomeKind outcome, string reason, TimeSpan cooldown, VanguardMedicalActionProgressSnapshot? progress = null)
    {
        bool successRequested = outcome == VanguardMedicalActionOutcomeKind.Completed
            || lease.EffectResolvedAwaitingHandsRelease
            || reason.StartsWith("MedicalEffectResolvedHands", StringComparison.OrdinalIgnoreCase)
            || (progress != null && (progress.NeedResolved || progress.TargetResolved || progress.AnyMedicalEffectObserved));
        var terminalTruth = ReadTerminalTruthBeforeOutcomeCommit(lease, progress);
        bool terminalAllowsSuccess = terminalTruth.AliveConfirmed;
        bool terminalFailure = terminalTruth.DeadConfirmed || (successRequested && terminalTruth.TerminalUnknown);

        if (terminalFailure)
        {
            outcome = VanguardMedicalActionOutcomeKind.Failed;
            reason = terminalTruth.DeadConfirmed
                ? "TerminalDeathBeforeOutcomeCommit:" + terminalTruth.Reason
                : "TerminalTruthUnknownBeforeOutcomeCommit:" + terminalTruth.Reason;
            cooldown = FailureCooldown;
            lease.CompletionObserved = false;
        }
        else
        {
            bool handsDrainTerminal = lease.EffectResolvedAwaitingHandsRelease
                || reason.StartsWith("MedicalEffectResolvedHands", StringComparison.OrdinalIgnoreCase);
            if (handsDrainTerminal)
            {
                // Runtime invariant: a previously observed effect may complete only after direct terminal truth
                // confirms the Operator is still alive at the actual outcome commit boundary.
                outcome = VanguardMedicalActionOutcomeKind.Completed;
                lease.CompletionObserved = true;
            }
            else if (progress != null && terminalAllowsSuccess && (progress.NeedResolved || progress.TargetResolved))
            {
                outcome = VanguardMedicalActionOutcomeKind.Completed;
                reason = progress.NeedResolved ? "MedicalNeedResolvedBeforeOutcomeCommit" : "MedicalTargetResolvedBeforeOutcomeCommit";
                cooldown = SuccessCooldown;
                lease.CompletionObserved = true;
            }
            else
            {
                cooldown = CooldownForMedicalOutcome(lease, progress, reason, cooldown);
            }
        }

        string backendSummary = (progress is null ? "effect=unknown" : progress.EffectSummary + ";" + BuildEffectDeltaSummary(lease, progress))
            + ";" + terminalTruth.Summary;
        VanguardMedicalExecutionResultBridge.Publish(lease, outcome, reason, backendSummary, now);
        VanguardMedicalHandsWatchdogService.NotifyLeaseTerminal(lease, outcome, reason, now);
        VanguardExecutionLeaseStore.Release(lease.BotProfileId);
        bool medicalEffectSucceeded = outcome == VanguardMedicalActionOutcomeKind.Completed
            && terminalAllowsSuccess
            && progress != null
            && (progress.AnyMedicalEffectObserved || progress.NeedResolved || progress.TargetResolved);
        bool confirmedNativeAttempt = HasConfirmedNativeMedicalAttempt(lease, progress, reason);
        bool preCommitNativeStartStall = !IsSurgeryNeed(lease.MedicalNeed)
            && !lease.ThreatObservedDuringLease
            && !lease.FirstAidCancellationIsThreat
            && progress?.ItemResourceConsumed != true
            && (reason.IndexOf("NativeFirstAidStartStall", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("NativeStartPendingExpiredNoCommit", StringComparison.OrdinalIgnoreCase) >= 0);
        if (preCommitNativeStartStall)
        {
            VanguardMedicalNativeAttemptMemory.RegisterPreCommitStartStall(lease, now, reason);
        }
        else if (outcome == VanguardMedicalActionOutcomeKind.Completed
            && progress != null
            && (progress.AnyMedicalEffectObserved || progress.NeedResolved || progress.TargetResolved))
        {
            VanguardMedicalNativeAttemptMemory.ClearOnMedicalSuccess(lease, reason);
        }

        bool countAsNoEffect = !IsSurgeryNeed(lease.MedicalNeed)
            && !terminalTruth.DeadConfirmed
            && !medicalEffectSucceeded
            && confirmedNativeAttempt
            && (IsNoEffectReason(reason, progress)
                || reason.IndexOf("MaxWindowExpiredWhileControllerUsing", StringComparison.OrdinalIgnoreCase) >= 0);
        bool surgeryStateBoundFailure = IsSurgeryNeed(lease.MedicalNeed)
            && !string.Equals(lease.SurgeryCancellationKind, "canonical_priority_preemption", StringComparison.OrdinalIgnoreCase)
            && !terminalTruth.DeadConfirmed
            && !medicalEffectSucceeded
            && progress != null
            && (progress.NeedStillPresent || progress.TargetStillPresent)
            && (progress.ResourceConsumedWithoutTargetEffect
                || string.Equals(lease.SurgeryCancellationKind, "controller_terminal_timeout", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lease.SurgeryCancellationKind, "commanded_movement_violation", StringComparison.OrdinalIgnoreCase)
                || reason.IndexOf("NoMedicalEffect", StringComparison.OrdinalIgnoreCase) >= 0);
        if (surgeryStateBoundFailure
            && VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(lease.BotProfileId, out var surgeryFailureSnapshot))
        {
            bool committedNoEffect = progress!.ResourceConsumedWithoutTargetEffect
                || lease.SurgeryResourceCommitObserved
                || progress.ItemResourceConsumed;
            if (committedNoEffect)
            {
                VanguardSurgeryDebtService.RecordFailedItemInstance(lease, now, reason);
                bool alternativeInstanceAvailable = TryFindAlternativeSurgeryInstance(lease, surgeryFailureSnapshot, out var alternativeSummary);
                if (!alternativeInstanceAvailable)
                {
                    VanguardSurgeryDebtService.BlockUntilStateChange(lease, surgeryFailureSnapshot, progress!, now,
                        "terminal_invalid_surgery:" + Safe(lease.SurgeryCancellationKind) + ":" + Safe(reason));
                }
                else
                {
                    VanguardClientDiagnosticsLog.Info(MedicalSurgeryDeterministicCompletionStatusTag,
                        $"VANGUARD_SURGERY_ALTERNATIVE_INSTANCE_AVAILABLE {lease.Summary}; {alternativeSummary}; failedInstancePersistentlyExcluded=true; targetDebtBlocked=false; next=retry_with_alternative_after_short_cooldown; tag={MedicalSurgeryDeterministicCompletionStatusTag}");
                }
            }
            else
            {
                VanguardClientDiagnosticsLog.Info(MedicalSurgeryDeterministicCompletionStatusTag,
                    $"VANGUARD_SURGERY_GAMEPLAY_CANCELLATION_RETRYABLE {lease.Summary}; cancellation={Safe(lease.SurgeryCancellationKind)}; reason={Safe(reason)}; resourceCommitted=false; failedInstanceRecorded=false; targetDebtBlocked=false; sameKitRetryAllowedAfterCooldown=true; tag={MedicalSurgeryDeterministicCompletionStatusTag}");
            }
        }

        var outcomeMemory = VanguardExecutionLeaseStore.RegisterLeaseOutcomeDetailed(
            lease, outcome.ToString(), reason, lease.LastProgressKind, now, now + cooldown, countAsNoEffect, medicalEffectSucceeded);
        if (outcomeMemory.CircuitBreakerArmed)
        {
            VanguardClientDiagnosticsLog.Warning(MedicalEffectCircuitBreakerStatusTag,
                $"VANGUARD_MEDICAL_EFFECT_CIRCUIT_ARMED {lease.Summary}; outcome={outcome}; reason={Safe(reason)}; noEffectCount={outcomeMemory.ConsecutiveNoEffectCount}; blockedUntil=state_change; stateBound=true; exactItemInstance=true; itemResourceBound=true; clearsOnMedicalStateChange=true; bleedIncluded=true; bleedThreshold=one_confirmed_terminal_attempt_then_state_bound_block_per_exact_item_instance; surgeryDebtSeparate=true; tag={MedicalEffectCircuitBreakerStatusTag}; Tag={VanguardExecutionLeaseStore.StateBoundOutcomeStatusTag}");
        }
        if (!terminalTruth.DeadConfirmed)
        {
            VanguardSurgeryDebtService.RegisterOutcome(lease, progress, now, outcome, reason);
        }
        if (IsSurgeryNeed(lease.MedicalNeed))
        {
            VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(lease.BotProfileId, out var releaseRecord);
            VanguardMedicalIsolationController.ReleaseForLease(lease, releaseRecord?.BotOwner, now, "stationary_surgery_" + outcome + ":" + reason);
        }

        if (!terminalTruth.DeadConfirmed && confirmedNativeAttempt && IsNoEffectReason(reason, progress))
        {
            VanguardCanonicalMedicalStateService.RequestForceRefresh(lease.BotProfileId, "medical_no_effect_terminal:" + reason);
            VanguardPostOrbitInventoryRecoveryService.MarkNoMedicalEffect(lease, progress, now, reason, requestBoundedRetryRefresh: !outcomeMemory.CircuitBreakerArmed);
        }

        string logKind = outcome switch
        {
            VanguardMedicalActionOutcomeKind.Completed => "VANGUARD_EXECUTION_COMPLETED",
            VanguardMedicalActionOutcomeKind.Timeout => "VANGUARD_EXECUTION_TIMEOUT",
            VanguardMedicalActionOutcomeKind.Interrupted => "VANGUARD_EXECUTION_FAILED",
            _ => "VANGUARD_EXECUTION_FAILED"
        };

        double elapsed = Math.Max(0d, (now - lease.StartedAtUtc).TotalSeconds);
        string effect = backendSummary;
        string surgery = IsSurgeryNeed(lease.MedicalNeed) ? $"; surgery=true; surgeryTag={ActiveSurgeryStatusTag}; surgerySafetyTag={SainLikeSurgerySafetyStatusTag}; surgeryDebtTag={MedicalSurgeryDebtRetryStatusTag}" : string.Empty;
        VanguardClientDiagnosticsLog.Info(StatusTag, $"{logKind} {lease.Summary}; outcome={outcome}; reason={reason}; elapsed={elapsed:0.00}; retryAfter={cooldown.TotalSeconds:0.00}; completionRecheck=true; targetAware=true; terminalTruth=true; {effect}; effectGuard={MedicalEffectGuardStatusTag}; terminalTag={MedicalTerminalTruthStatusTag}{surgery}; isolationTag={VanguardMedicalIsolationController.StatusTag}; inventoryRefreshTag={InventoryRefreshStatusTag}");
    }

    private static VanguardMedicalTerminalTruthSnapshot ReadTerminalTruthBeforeOutcomeCommit(VanguardExecutionLeaseState lease, VanguardMedicalActionProgressSnapshot? progress)
    {
        VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(lease.BotProfileId, out var runtime);
        OperatorDecisionSnapshot? latestSnapshot = null;
        if (VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(lease.BotProfileId, out var latest))
        {
            latestSnapshot = latest;
        }

        var terminal = VanguardMedicalTerminalTruthReader.Capture(lease.BotProfileId, runtime?.BotOwner, latestSnapshot);
        if ((terminal.DeadConfirmed || terminal.TerminalUnknown)
            && (progress == null || !progress.TerminalDeadConfirmed || !string.Equals(progress.TerminalReason, terminal.Reason, StringComparison.OrdinalIgnoreCase)))
        {
            VanguardClientDiagnosticsLog.Warning(MedicalTerminalTruthStatusTag,
                $"VANGUARD_MEDICAL_TERMINAL_TRUTH_RECHECK {lease.Summary}; {terminal.Summary}; progressTerminal={Safe(progress?.TerminalReason)}; completionAllowed={Bool(terminal.AliveConfirmed)}; doctrine=terminal_truth_precedes_need_disappearance; tag={MedicalTerminalTruthStatusTag}");
        }

        return terminal;
    }

    private static TimeSpan NoProgressTimeoutForLease(VanguardExecutionLeaseState lease)
    {
        if (IsSurgeryNeed(lease.MedicalNeed))
        {
            return StationarySurgeryNoProgressTimeout;
        }

        return lease.MedicalNeed == Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.Fracture ? StationaryFractureNoProgressTimeout : MobileNoProgressTimeout;
    }

    private static TimeSpan PostUseRecheckWindowForLease(VanguardExecutionLeaseState lease)
    {
        if (IsSurgeryNeed(lease.MedicalNeed))
        {
            return IsSurv12(lease.ItemTemplateId, lease.ItemName) ? StationarySurgerySurv12PostUseRecheckWindow : StationarySurgeryCmsPostUseRecheckWindow;
        }

        if (lease.MedicalNeed == Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.Fracture)
        {
            return lease.ItemName.Contains("Grizzly", StringComparison.OrdinalIgnoreCase) ? StationaryFractureGrizzlyPostUseRecheckWindow : StationaryFracturePostUseRecheckWindow;
        }

        return lease.MedicalNeed == Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.HpHeal ? HpHealPostUseRecheckWindow : MobilePostUseRecheckWindow;
    }


    private static string LeasePrefixFor(VanguardMobileMedicalActionSelection selection)
    {
        if (IsSurgeryNeed(selection.Need))
        {
            return "med-surgery-";
        }

        return selection.RequiresStationary ? "med-fracture-" : "med-mobile-";
    }

    private static bool IsSurgeryNeed(Vanguard.Client.Runtime.Medical.VanguardMedicalNeed need)
    {
        return VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(need);
    }

    private static bool IsSurv12(string? templateId, string? itemName)
    {
        return string.Equals(templateId, "5d02797c86f774203f38e30a", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(itemName) && itemName.IndexOf("Surv12", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static bool IsStrictCompletionResolved(VanguardExecutionLeaseState lease, VanguardMedicalActionProgressSnapshot progress)
    {
        return progress.TerminalAliveConfirmed && (progress.NeedResolved || progress.TargetResolved);
    }


    private static bool CanHoldBrokenSurgeryWindowUntilTimeout(VanguardExecutionLeaseState lease, EFT.BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, string brokenReason, out string summary)
    {
        summary = "hardProcedureHold=false";
        if (!IsSurgeryNeed(lease.MedicalNeed) || now >= lease.MaxUntilUtc
            || brokenReason.StartsWith("persistent_commanded_movement", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var safety = snapshot.Medical.Safety;
        bool criticalThreat = safety.EnemyCanShoot
            || safety.IncomingFireRecent
            || safety.ImmediateCombatBlock
            || (snapshot.Threat.DirectThreat && safety.EnemyVisible && !safety.CoveredOrHoldingAngle);
        if (criticalThreat)
        {
            summary = "hardProcedureHold=false;reason=critical_threat";
            return false;
        }

        bool refreshed = VanguardMedicalIsolationController.RefreshStationaryMedicalHold(lease, botOwner, snapshot, now, "hard_procedure_window_broken:" + Safe(brokenReason), out var holdSummary);
        if (!refreshed)
        {
            summary = "hardProcedureHold=false;" + holdSummary;
            return false;
        }

        summary = "hardProcedureHold=true;reason=" + Safe(brokenReason)
            + ";remaining=" + Math.Max(0d, (lease.MaxUntilUtc - now).TotalSeconds).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            + ";" + holdSummary;
        return true;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b)
    {
        return a <= b ? a : b;
    }


    private static bool CanCompletePartialMedicalEffect(VanguardExecutionLeaseState lease, VanguardMedicalActionProgressSnapshot progress)
    {
        // HP heal is the only active medical lane where a partial HP increase is a
        // valid completion signal. Bleeds, fractures and surgery must resolve their
        // condition/target; HP improvement alone is only progress and must never
        // complete those lanes.
        return lease.MedicalNeed == Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.HpHeal
            && progress.AnyMedicalEffectObserved
            && !progress.NeedResolved
            && !progress.TargetResolved;
    }

    private static bool IsSurgeryUnsafeDuringLease(VanguardExecutionLeaseState lease, OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (!IsSurgeryNeed(lease.MedicalNeed))
        {
            return false;
        }

        if (HasTrueMedicalAbortThreat(snapshot, out reason))
        {
            return true;
        }

        var safety = snapshot.Medical.Safety;
        bool nativeProcedureCommitted = lease.SurgeryApplyAttemptCount > 0
            || lease.ItemUseObserved
            || lease.SurgeryControllerCallbackObserved
            || lease.MedicalIsolationAcquired;
        string commitReason;
        bool durableCommit;
        if (nativeProcedureCommitted)
        {
            durableCommit = true;
            commitReason = "native_apply_or_isolation_committed";
        }
        else
        {
            durableCommit = HasDurableSurgeryCommit(lease, snapshot, out commitReason);
        }

        if ((!safety.SurgeryAreaClear || !safety.CoveredOrHoldingAngle) && durableCommit)
        {
            reason = "none";
            if (ShouldLogRecheck(lease.BotProfileId + "|surgery_persist_cover_signal|" + lease.TargetPart, DateTimeOffset.UtcNow))
            {
                VanguardClientDiagnosticsLog.Info(MedicalSurgeryPersistenceStatusTag, $"VANGUARD_MEDICAL_SURGERY_PERSISTED_DESPITE_COVER_SIGNAL {lease.Summary}; areaClear={Bool(safety.SurgeryAreaClear)}; areaReason={Safe(safety.SurgeryAreaClearReason)}; coverOrHold={Bool(safety.CoveredOrHoldingAngle)}; commit={Safe(commitReason)}; enemyVisible={Bool(safety.EnemyVisible)}; enemyCanShoot={Bool(safety.EnemyCanShoot)}; incomingFire={Bool(safety.IncomingFireRecent)}; immediate={Bool(safety.ImmediateCombatBlock)}; noAbortWithoutTrueThreat=true; keepAuthority=true; tag={MedicalSurgeryPersistenceStatusTag}; postureRetryTag={MedicalPostureRetryStatusTag}; hardProcedureTag={MedicalHardProcedureAuthorityStatusTag}");
            }

            return false;
        }

        if (!safety.SurgeryAreaClear)
        {
            reason = string.IsNullOrWhiteSpace(safety.SurgeryAreaClearReason) ? "surgery_area_not_clear_without_durable_commit" : safety.SurgeryAreaClearReason;
            return true;
        }

        if (!safety.CoveredOrHoldingAngle)
        {
            reason = "surgery_cover_or_hold_lost_without_durable_commit";
            return true;
        }

        return false;
    }

    private static bool HasTrueMedicalAbortThreat(OperatorDecisionSnapshot snapshot, out string reason)
    {
        var safety = snapshot.Medical.Safety;
        if (safety.EnemyCanShoot || snapshot.Threat.EnemyCanShoot == true || snapshot.ThreatScan.CandidateCanShoot)
        {
            reason = "enemy_can_shoot";
            return true;
        }

        if (safety.IncomingFireRecent)
        {
            reason = "incoming_fire_recent";
            return true;
        }

        reason = "none";
        return false;
    }

    private static bool HasDurableSurgeryCommit(VanguardExecutionLeaseState lease, OperatorDecisionSnapshot snapshot, out string reason)
    {
        if (string.Equals(lease.MedicalIsolationPhase, "ExecutingMedicalAction", StringComparison.OrdinalIgnoreCase)
            || string.Equals(lease.MedicalIsolationPhase, "ReadyForMedicalAction", StringComparison.OrdinalIgnoreCase)
            || string.Equals(lease.MedicalIsolationPhase, "ArrivedAtCover", StringComparison.OrdinalIgnoreCase)
            || string.Equals(lease.MedicalIsolationPhase, "StabilizingPosture", StringComparison.OrdinalIgnoreCase))
        {
            reason = "medical_isolation_phase_" + Safe(lease.MedicalIsolationPhase);
            return true;
        }

        if (VanguardSurgeryCoverPrepareExecutor.HasRecentVanguardSurgeryCoverGrant(snapshot, out var grantReason))
        {
            reason = "recent_cover_grant:" + Safe(grantReason);
            return true;
        }

        reason = "missing_durable_commit";
        return false;
    }

    private static bool IsStationaryMedicalWindowBroken(VanguardExecutionLeaseState lease, EFT.BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (!IsSurgeryNeed(lease.MedicalNeed))
        {
            return false;
        }

        // Give the EFT/SurgicalKit controller a short chance to settle after the
        // ApplyToCurrentPart call. After that grace period, surgery is expected to
        // remain stationary, but the runtime keeps suppressing ORBIT/LootingBots instead
        // of failing immediately on a reacquired corpse objective.
        if (now - lease.StartedAtUtc < TimeSpan.FromSeconds(1.25d))
        {
            VanguardMedicalIsolationController.RefreshStationaryMedicalHold(lease, botOwner, snapshot, now, "initial_surgery_settle", out _);
            return false;
        }

        float speed = Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed);
        DateTimeOffset observationUtc = now;
        float sampleMovement = 0f;
        bool unreliableSampleGap = false;
        bool hadPriorMovementSample = lease.LastSurgeryMovementPositionCaptured;
        if (botOwner != null)
        {
            if (hadPriorMovementSample)
            {
                float sampleDx = botOwner.Position.x - lease.LastSurgeryMovementX;
                float sampleDz = botOwner.Position.z - lease.LastSurgeryMovementZ;
                sampleMovement = (float)Math.Sqrt((sampleDx * sampleDx) + (sampleDz * sampleDz));
                unreliableSampleGap = lease.LastSurgeryMovementSampleAtUtc != DateTimeOffset.MinValue
                    && observationUtc - lease.LastSurgeryMovementSampleAtUtc > SurgeryMovementSampleGapLimit;
            }

            lease.LastSurgeryMovementX = botOwner.Position.x;
            lease.LastSurgeryMovementZ = botOwner.Position.z;
            lease.LastSurgeryMovementPositionCaptured = true;
            lease.LastSurgeryMovementSampleAtUtc = observationUtc;
        }

        float stationaryDrift = 0f;
        if (lease.SurgeryStationaryAnchorCaptured && botOwner != null)
        {
            float dx = botOwner.Position.x - lease.SurgeryStationaryAnchorX;
            float dz = botOwner.Position.z - lease.SurgeryStationaryAnchorZ;
            stationaryDrift = (float)Math.Sqrt((dx * dx) + (dz * dz));
        }

        bool commandedMovement = snapshot.Movement.HasPath == true
            && snapshot.Movement.TargetSpeed.GetValueOrDefault() > 0.10f;
        bool physicalMovementObserved = hadPriorMovementSample
            && sampleMovement > SurgeryReliableSampleMovementMeters;

        if (unreliableSampleGap)
        {
            lease.SurgeryMovementViolationSinceUtc = DateTimeOffset.MinValue;
            if (botOwner != null)
            {
                lease.SurgeryStationaryAnchorCaptured = true;
                lease.SurgeryStationaryAnchorX = botOwner.Position.x;
                lease.SurgeryStationaryAnchorZ = botOwner.Position.z;
            }
            VanguardMedicalIsolationController.RefreshStationaryMedicalHold(lease, botOwner, snapshot, now, "surgery_movement_sample_gap_reanchor", out _);
            lease.LastProgressKind = "stationary_surgery_unreliable_movement_sample_ignored";
            return false;
        }

        if (physicalMovementObserved && !commandedMovement)
        {
            lease.SurgeryMovementViolationSinceUtc = DateTimeOffset.MinValue;
            if (botOwner != null && stationaryDrift > SurgeryStationaryDriftLimitMeters)
            {
                lease.SurgeryStationaryAnchorCaptured = true;
                lease.SurgeryStationaryAnchorX = botOwner.Position.x;
                lease.SurgeryStationaryAnchorZ = botOwner.Position.z;
            }
            VanguardMedicalIsolationController.RefreshStationaryMedicalHold(lease, botOwner, snapshot, now, "surgery_physical_drift_without_command_reanchor", out _);
            lease.LastProgressKind = "stationary_surgery_uncommanded_drift_ignored";
            return false;
        }

        if (physicalMovementObserved && commandedMovement)
        {
            VanguardMedicalIsolationController.RefreshStationaryMedicalHold(lease, botOwner, snapshot, now, "hard_lock_commanded_movement_restabilize", out var movementHoldSummary);
            if (lease.SurgeryMovementViolationSinceUtc == DateTimeOffset.MinValue)
            {
                lease.SurgeryMovementViolationSinceUtc = now;
                lease.LastProgressKind = "stationary_surgery_commanded_movement_observed";
                lease.NoProgressUntilUtc = now + SurgeryMovementViolationGrace;
                VanguardClientDiagnosticsLog.Warning(MedicalHardLockAbortGateStatusTag, $"VANGUARD_MEDICAL_SURGERY_COMMANDED_MOVEMENT_OBSERVED {lease.Summary}; speed={speed:0.00}; sampleMovement={sampleMovement:0.00}; drift={stationaryDrift:0.00}; hasPath={Bool(snapshot.Movement.HasPath == true)}; targetSpeed={snapshot.Movement.TargetSpeed.GetValueOrDefault():0.00}; distanceToDestination={snapshot.Movement.DistanceToDestination.GetValueOrDefault():0.00}; grace={SurgeryMovementViolationGrace.TotalSeconds:0.00}; {movementHoldSummary}; action=restabilize_then_cancel_only_if_command_persists; tag={MedicalHardLockAbortGateStatusTag}; hardHoldTag={MedicalSurgeryHardHoldStatusTag}");
                return false;
            }

            TimeSpan violationDuration = now - lease.SurgeryMovementViolationSinceUtc;
            if (violationDuration >= SurgeryMovementViolationGrace)
            {
                reason = "persistent_commanded_movement:speed=" + speed.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                    + ";sample=" + sampleMovement.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                    + ";drift=" + stationaryDrift.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                    + ";duration=" + violationDuration.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            lease.NoProgressUntilUtc = lease.SurgeryMovementViolationSinceUtc + SurgeryMovementViolationGrace;
            return false;
        }

        lease.SurgeryMovementViolationSinceUtc = DateTimeOffset.MinValue;

        if (snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true)
        {
            if (VanguardMedicalIsolationController.RefreshStationaryMedicalHold(lease, botOwner, snapshot, now, "loot_reacquired_during_stationary_surgery", out var lootHoldSummary))
            {
                lease.LastProgressKind = "stationary_surgery_loot_suppressed";
                lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(1.50d);
                VanguardClientDiagnosticsLog.Info(MedicalAuthorityHoldStatusTag, $"VANGUARD_STATIONARY_SURGERY_EXTERNAL_REACQUIRE_SUPPRESSED {lease.Summary}; external=loot; {lootHoldSummary}; keepWindow=true; noImmediateFail=true; tag={MedicalAuthorityHoldStatusTag}; surgerySafetyTag={SainLikeSurgerySafetyStatusTag}");
                return false;
            }

            VanguardClientDiagnosticsLog.Info(MedicalHardLockAbortGateStatusTag, $"VANGUARD_MEDICAL_SURGERY_HARD_LOCK_IGNORED_LOOT_REACQUIRE {lease.Summary}; external=loot; abortOnlyForTrueThreatOrDurableCommandedMovement=true; keepWindow=true; tag={MedicalHardLockAbortGateStatusTag}; orbitLootFreezeTag={MedicalOrbitLootFreezeDuringSurgeryStatusTag}; surgeryDebtTag={MedicalSurgeryDebtRetryStatusTag}");
            return false;
        }

        string orbit = (snapshot.Orbit.Status + "|" + snapshot.Orbit.Category + "|" + snapshot.Orbit.ExtractReason).ToLowerInvariant();
        if (snapshot.Orbit.Active && (orbit.Contains("loot") || orbit.Contains("corpse") || orbit.Contains("container") || orbit.Contains("moving") || orbit.Contains("orbit_moving")))
        {
            if (VanguardMedicalIsolationController.RefreshStationaryMedicalHold(lease, botOwner, snapshot, now, "orbit_reacquired_during_stationary_surgery", out var orbitHoldSummary))
            {
                lease.LastProgressKind = "stationary_surgery_orbit_suppressed";
                lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(1.50d);
                VanguardClientDiagnosticsLog.Info(MedicalAuthorityHoldStatusTag, $"VANGUARD_STATIONARY_SURGERY_EXTERNAL_REACQUIRE_SUPPRESSED {lease.Summary}; external=orbit; orbit={Safe(orbit)}; {orbitHoldSummary}; keepWindow=true; noImmediateFail=true; tag={MedicalAuthorityHoldStatusTag}; surgerySafetyTag={SainLikeSurgerySafetyStatusTag}");
                return false;
            }

            VanguardClientDiagnosticsLog.Info(MedicalHardLockAbortGateStatusTag, $"VANGUARD_MEDICAL_SURGERY_HARD_LOCK_IGNORED_ORBIT_REACQUIRE {lease.Summary}; orbit={Safe(orbit)}; abortOnlyForTrueThreatOrDurableCommandedMovement=true; keepWindow=true; tag={MedicalHardLockAbortGateStatusTag}; orbitLootFreezeTag={MedicalOrbitLootFreezeDuringSurgeryStatusTag}; surgeryDebtTag={MedicalSurgeryDebtRetryStatusTag}");
            return false;
        }

        string state = snapshot.Movement.PlayerState ?? string.Empty;
        if (state.IndexOf("DoorInteraction", StringComparison.OrdinalIgnoreCase) >= 0
            || state.IndexOf("Loot", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (now - lease.StartedAtUtc < TimeSpan.FromSeconds(4.25d)
                && VanguardMedicalIsolationController.RefreshStationaryMedicalHold(lease, botOwner, snapshot, now, "interaction_state_settle_during_stationary_surgery", out var interactionHoldSummary))
            {
                lease.LastProgressKind = "stationary_surgery_interaction_state_suppressed";
                lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(1.25d);
                VanguardClientDiagnosticsLog.Info(MedicalAuthorityHoldStatusTag, $"VANGUARD_STATIONARY_SURGERY_INTERACTION_STATE_SUPPRESSED {lease.Summary}; playerState={Safe(state)}; {interactionHoldSummary}; keepWindow=true; tag={MedicalAuthorityHoldStatusTag}; surgerySafetyTag={SainLikeSurgerySafetyStatusTag}");
                return false;
            }

            VanguardMedicalIsolationController.RefreshStationaryMedicalHold(lease, botOwner, snapshot, now, "hard_lock_interaction_state_restabilize", out var interactionHardHoldSummary);
            VanguardClientDiagnosticsLog.Info(MedicalHardLockAbortGateStatusTag, $"VANGUARD_MEDICAL_SURGERY_HARD_LOCK_IGNORED_INTERACTION_STATE {lease.Summary}; playerState={Safe(state)}; {interactionHardHoldSummary}; abortOnlyForTrueThreatOrDurableCommandedMovement=true; keepWindow=true; tag={MedicalHardLockAbortGateStatusTag}; hardHoldTag={MedicalSurgeryHardHoldStatusTag}");
            return false;
        }

        VanguardMedicalIsolationController.RefreshStationaryMedicalHold(lease, botOwner, snapshot, now, "stationary_surgery_idle_heartbeat", out _);
        return false;
    }

    private static void LogStationaryWindowBroken(VanguardExecutionLeaseState lease, OperatorDecisionSnapshot snapshot, string reason)
    {
        float speed = Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed);
        VanguardClientDiagnosticsLog.Info(
            SainLikeSurgerySafetyStatusTag,
            $"VANGUARD_STATIONARY_MEDICAL_WINDOW_BROKEN {lease.Summary}; reason={Safe(reason)}; speed={speed:0.00}; playerState={Safe(snapshot.Movement.PlayerState)}; lootBot={Tri(snapshot.Looting.BotLooting)}; lootTask={Tri(snapshot.Looting.LootTaskRunning)}; orbitActive={Bool(snapshot.Orbit.Active)}; orbitStatus={Safe(snapshot.Orbit.Status)}; orbitCategory={Safe(snapshot.Orbit.Category)}; tag={SainLikeSurgerySafetyStatusTag}; Tag={SurgeryCoverCompletionGuardStatusTag}");
    }

    private static string BuildEffectSignature(OperatorDecisionSnapshot snapshot, VanguardMobileMedicalActionSelection selection, (float current, float maximum) initialTargetHealth)
    {
        return VanguardExecutionLeaseStore.BuildEffectSignature(
            snapshot.BotProfileId, selection.Need, selection.TargetPartName, selection.ItemTemplateId,
            selection.ItemInstanceId, selection.ItemResource, selection.ItemMaxResource,
            snapshot.Medical.Need.HealthPercent, initialTargetHealth.current, initialTargetHealth.maximum);
    }

    private static bool ShouldDeferTrivialHpHealDuringTravel(OperatorDecisionSnapshot snapshot, VanguardMobileMedicalActionSelection selection, (float current, float maximum) targetHealth, out string reason)
    {
        reason = "none";
        if (selection.Need != VanguardMedicalNeed.HpHeal || targetHealth.current < 0f || targetHealth.maximum <= 0f)
        {
            return false;
        }

        float missingHp = targetHealth.maximum - targetHealth.current;
        bool trivialDeficit = missingHp <= 1.0f;
        bool travelPressure = snapshot.SquadCohesion.OperatorDistanceToOwner > 8.0f
            || Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed) > 0.55f;
        if (!trivialDeficit || !travelPressure)
        {
            return false;
        }

        reason = "missingHp=" + missingHp.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            + ";ownerDistance=" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            + ";speed=" + Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryFindAlternativeSurgeryInstance(VanguardExecutionLeaseState lease, OperatorDecisionSnapshot snapshot, out string summary)
    {
        summary = "alternative=false";
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(lease.BotProfileId, out var runtime)
            || runtime.BotOwner == null)
        {
            summary = "alternative=false;reason=runtime_missing";
            return false;
        }

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { lease.ItemInstanceId };
        if (!VanguardMobileMedicalActionSelector.TrySelect(runtime.BotOwner, snapshot, excluded, out var alternative, out var reason)
            || !IsSurgeryNeed(alternative.Need)
            || !string.Equals(alternative.TargetPartName, lease.TargetPart, StringComparison.OrdinalIgnoreCase)
            || string.Equals(alternative.ItemInstanceId, lease.ItemInstanceId, StringComparison.OrdinalIgnoreCase))
        {
            summary = "alternative=false;reason=" + Safe(reason);
            return false;
        }

        summary = "alternative=true;item=" + Safe(alternative.ItemName)
            + ";tpl=" + Safe(alternative.ItemTemplateId)
            + ";instance=" + Safe(alternative.ItemInstanceId)
            + ";target=" + Safe(alternative.TargetPartName);
        return true;
    }

    private static float ResolveSurgeryHealthPenalty(MedsItemClass? item, string? itemName)
    {
        try
        {
            object? effects = item?.HealthEffectsComponent;
            object? damageEffects = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(effects, "DamageEffects");
            if (damageEffects is System.Collections.IDictionary dictionary)
            {
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                {
                    if ((entry.Key?.ToString() ?? string.Empty).IndexOf("DestroyedPart", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    float min = ReadFloatMember(entry.Value, "HealthPenaltyMin", -1f);
                    float max = ReadFloatMember(entry.Value, "HealthPenaltyMax", -1f);
                    if (min >= 0f && max >= 0f)
                    {
                        return Math.Clamp(((min + max) * 0.5f) / 100f, 0.05f, 1f);
                    }
                }
            }
        }
        catch
        {
        }

        string name = itemName ?? string.Empty;
        return name.IndexOf("Surv", StringComparison.OrdinalIgnoreCase) >= 0 ? 0.85f : 0.55f;
    }

    private static bool TryRepairCommittedSurgeryEffect(
        VanguardExecutionLeaseState lease,
        BotOwner? botOwner,
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        VanguardMedicalActionProgressSnapshot progress,
        string source,
        out string summary)
    {
        summary = "repair=false";
        if (!IsSurgeryNeed(lease.MedicalNeed)
            || lease.SurgeryFallbackRepairApplied
            || lease.SurgeryCancellationRequested
            || lease.SurgeryCancellationIsThreat
            || botOwner?.GetPlayer?.ActiveHealthController == null
            || !progress.TargetStillPresent
            || (!lease.SurgeryResourceCommitObserved
                && !lease.SurgeryTerminalItemDepletionCommitObserved
                && !progress.ItemResourceConsumed))
        {
            return false;
        }

        snapshot ??= OperatorDecisionSnapshot.Empty;
        if (snapshot.Alive && HasTrueMedicalAbortThreat(snapshot, out var threatReason))
        {
            summary = "repair=false;reason=true_threat:" + Safe(threatReason);
            return false;
        }

        if (!Enum.TryParse(lease.TargetPart, true, out EBodyPart targetPart)
            || targetPart == EBodyPart.Head
            || targetPart == EBodyPart.Chest)
        {
            summary = "repair=false;reason=invalid_or_vital_target:" + Safe(lease.TargetPart);
            return false;
        }

        float penalty = lease.SurgeryFallbackHealthPenalty > 0f
            ? lease.SurgeryFallbackHealthPenalty
            : (lease.ItemName.IndexOf("Surv", StringComparison.OrdinalIgnoreCase) >= 0 ? 0.85f : 0.55f);
        bool restored;
        try
        {
            restored = botOwner.GetPlayer.ActiveHealthController.RestoreBodyPart(targetPart, Math.Clamp(penalty, 0.05f, 1f));
        }
        catch (Exception exception)
        {
            summary = "repair=false;reason=restore_exception:" + Safe(exception.GetType().Name) + ":" + Safe(exception.Message);
            return false;
        }

        if (!restored)
        {
            if (VanguardMedicalActionProgressReader.TryReadTargetHealth(botOwner, lease.TargetPart, out float current, out _)
                && current > 0.01f)
            {
                restored = true;
            }
        }
        if (!restored)
        {
            summary = "repair=false;reason=target_not_destroyed_or_restore_rejected";
            return false;
        }

        lease.SurgeryFallbackRepairApplied = true;
        lease.SurgeryFallbackRepairAppliedAtUtc = now;
        lease.SurgeryFallbackRepairReason = source;
        // The native health mutation is never treated as success by itself. The next immutable
        // medical snapshot must prove that the exact leased body part is no longer destroyed.
        lease.SurgeryTargetEffectConfirmed = false;
        lease.SurgeryTargetEffectConfirmedAtUtc = DateTimeOffset.MinValue;
        lease.LastProgressAtUtc = now;
        lease.LastProgressKind = "surgery_effect_repaired_after_native_commit";
        summary = "repair=true;target=" + Safe(lease.TargetPart)
            + ";penalty=" + penalty.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
            + ";callback=" + Bool(lease.SurgeryControllerCallbackObserved)
            + ";resourceCommit=" + Bool(lease.SurgeryResourceCommitObserved || progress.ItemResourceConsumed)
            + ";terminalItemCommit=" + Bool(lease.SurgeryTerminalItemDepletionCommitObserved)
            + ";source=" + Safe(source);
        return true;
    }

    private static float ReadFloatMember(object? instance, string name, float fallback)
    {
        object? value = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(instance, name);
        if (value == null)
        {
            return fallback;
        }
        try
        {
            return Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static bool TryReadInitialTargetHealth(EFT.BotOwner? botOwner, string targetPartName, out (float current, float maximum) value)
    {
        if (VanguardMedicalActionProgressReader.TryReadTargetHealth(botOwner, targetPartName, out float current, out float maximum))
        {
            value = (current, maximum);
            return true;
        }

        value = (-1f, -1f);
        return false;
    }

    private static bool ShouldLogRecheck(string key, DateTimeOffset now)
    {
        if (LastRecheckLogAtByKey.TryGetValue(key, out var last) && now - last < RecheckLogInterval)
        {
            return false;
        }

        LastRecheckLogAtByKey[key] = now;
        return true;
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
    }

    private static string Tri(bool? value)
    {
        return value.HasValue ? Bool(value.Value) : "unknown";
    }

    private static string Float(float? value)
    {
        return value.HasValue ? value.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "unknown";
    }
}
#endif
