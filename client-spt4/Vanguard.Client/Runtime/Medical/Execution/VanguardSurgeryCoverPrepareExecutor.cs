#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.External;
using Vanguard.Client.Runtime.Grenades;
using Vanguard.Client.Runtime.Intents;
using Vanguard.Client.Runtime.Medical;
using Vanguard.Client.Runtime.Movement;

// Responsibility: finds and reaches a safe cover anchor before a surgery procedure receives execution authority.
// Flow: A due-and-actionable surgery request selects a nearby cover candidate, validates NavMesh/path/occupancy, moves the Operator there under a temporary lease, then reports a prepared hold so the surgery executor can take over.
// Authority boundary: this executor owns only the preparation movement window; canonical medical truth decides whether surgery is due and the medical executor owns the procedure itself.
// Invariant: cover preparation must yield to direct threat/grenade safety, validate path/occupancy before commit and release its movement lease on every terminal path.

namespace Vanguard.Client.Runtime.Medical.Execution;

internal static class VanguardSurgeryCoverPrepareExecutor
{
    public const string IntentKey = "MedicalPrepareSurgeryCover";
    public const string WindowKind = "MedicalPrepareSurgeryCoverWindow";
    public const string StatusTag = "VANGUARD_MEDICAL_AUTHORITY_LEASE_OK";
    private const string StabilizationStatusTag = "VANGUARD_SURGERY_GO_COVER_ONLY_OK";
    private const string HardOrbitExitStatusTag = "VANGUARD_MEDICAL_HARD_ORBIT_EXIT_OK";
    private const string PathGateStatusTag = "VANGUARD_SURGERY_COVER_PATH_GATE_OK";
    private const string CoverPreflightStatusTag = "VANGUARD_SURGERY_COVER_PREFLIGHT_OK";
    private const string CoverMultiCandidateStatusTag = "VANGUARD_SURGERY_COVER_MULTI_CANDIDATE_OK";
    private const string CoverAnchorStatusTag = "VANGUARD_SURGERY_COVER_ANCHOR_OK";
    private const string CoverAnchorPreflightStatusTag = "VANGUARD_SURGERY_COVER_ANCHOR_PREFLIGHT_OK";
    private const string ExternalAuthorityAdapterStatusTag = VanguardExternalAuthorityAdapter.StatusTag;
    private const string ExternalMovementPreemptStatusTag = VanguardExternalAuthorityAdapter.MovementPreemptStatusTag;
    private const string CombatAwareGateStatusTag = VanguardExternalAuthorityAdapter.CombatAwareGateStatusTag;
    private const string TypedCoverFailureStatusTag = VanguardExternalAuthorityAdapter.TypedCoverFailureStatusTag;
    private const string OrbitLayerQuiesceStatusTag = VanguardExternalAuthorityAdapter.OrbitLayerQuiesceStatusTag;
    private const string CoverArrivalGrantStatusTag = VanguardExternalAuthorityAdapter.CoverArrivalGrantStatusTag;
    private const string MedicalAuthorityHoldStatusTag = VanguardExternalAuthorityAdapter.MedicalAuthorityHoldStatusTag;
    private const string MedicalCoverCommitStatusTag = VanguardExternalAuthorityAdapter.MedicalCoverCommitStatusTag;
    private const string MedicalCoverCommitUnificationStatusTag = VanguardExternalAuthorityAdapter.MedicalCoverCommitUnificationStatusTag;
    private const string MedicalCoverMovementStabilizationStatusTag = VanguardExternalAuthorityAdapter.MedicalCoverMovementStabilizationStatusTag;
    private const string MedicalHardProcedureAuthorityStatusTag = VanguardExternalAuthorityAdapter.MedicalHardProcedureAuthorityStatusTag;
    private const string MedicalProcedureCompletionGateStatusTag = VanguardExternalAuthorityAdapter.MedicalProcedureCompletionGateStatusTag;
    private const string MedicalSurgeryDirectChainStatusTag = VanguardExternalAuthorityAdapter.MedicalSurgeryDirectChainStatusTag;
    private const string MedicalSurgerySameProcedureStartStatusTag = VanguardExternalAuthorityAdapter.MedicalSurgerySameLeaseStartStatusTag;
    private const string ValidSurgeryTargetsStatusTag = VanguardMedicalSurgeryTargetPolicy.ValidSurgeryTargetsStatusTag;
    private const string CriticalFastSurgeryStatusTag = VanguardMedicalSurgeryTargetPolicy.CriticalFastSurgeryStatusTag;
    public const string OrbitLocalHoldLockStatusTag = "VANGUARD_MEDICAL_ORBIT_LOCAL_HOLD_LOCK_OK";
    public const string MedicalIsolationStatusTag = VanguardMedicalIsolationController.StatusTag;
    public const string MedicalRuntimeCleanupStatusTag = "VANGUARD_MEDICAL_RUNTIME_CLEANUP_OK";
    public const string SurgeryCoverMoveBridgeStatusTag = "VANGUARD_SURGERY_COVER_MOVE_BRIDGE_OK";
    public const string VanguardCoverSlotStatusTag = "VANGUARD_VANGUARD_SURGERY_COVER_SLOT_OK";
    public const string SainLikeCoverSeekStatusTag = "VANGUARD_SAIN_LIKE_SURGERY_COVER_SEEK_OK";
    public const string ResidualPrepareStatusTag = "VANGUARD_MEDICAL_PREPARE_RESIDUAL_SURGERY_WINDOW_OK";
    public const string ClientBuildStatusTag = "VANGUARD_CLIENT_BUILD_STATUS";
    public const string PerformanceChurnGuardStatusTag = "VANGUARD_MEDICAL_EPISODE_IDEMPOTENCE_STATUS";
    public const string IncrementalCoverSearchStatusTag = "VANGUARD_INCREMENTAL_MEDICAL_COVER_SEARCH_STATUS";
    public const string NavMeshNullSafetyStatusTag = "VANGUARD_NAVMESH_NULL_SAFETY_STATUS";
    public const string BoundedCoverCandidateScanStatusTag = "VANGUARD_BOUNDED_COVER_CANDIDATE_SCAN_STATUS";
    public const string StrictIncrementalCoverStatusTag = "VANGUARD_STRICT_INCREMENTAL_SURGERY_COVER_STATUS";
    public const string SurgeryPreparationConvergenceStatusTag = "VANGUARD_SURGERY_PREPARATION_CONVERGENCE_STATUS";
    public const string SurgeryCoverAdmissionConvergenceStatusTag = "VANGUARD_SURGERY_COVER_ADMISSION_CONVERGENCE_STATUS";

    private static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(1.00d);
    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(45.00d);
    private static readonly TimeSpan NoProgressTimeout = TimeSpan.FromSeconds(45.00d);
    private static readonly TimeSpan CoverProgressMaxDuration = TimeSpan.FromSeconds(58.00d);
    private static readonly TimeSpan CoverProgressWindowExtension = TimeSpan.FromSeconds(12.00d);
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(18.00d);
    private static readonly TimeSpan BlockedLogInterval = TimeSpan.FromSeconds(6.00d);
    private static readonly TimeSpan DeferredTransitionLogInterval = TimeSpan.FromSeconds(8.00d);
    private static readonly TimeSpan VanguardCoverSlotTtl = TimeSpan.FromSeconds(15.00d);
    private static readonly TimeSpan VanguardCoverGrantTtl = TimeSpan.FromSeconds(45.00d);
    private static readonly TimeSpan VanguardCoverRecommandInterval = TimeSpan.FromSeconds(2.25d);
    private static readonly TimeSpan VanguardRejectedCoverSlotTtl = TimeSpan.FromSeconds(35.00d);
    private static readonly TimeSpan ActionabilityGraceWindow = TimeSpan.FromSeconds(3.00d);
    private static readonly TimeSpan PreparedLaunchBlockedMaxDuration = TimeSpan.FromSeconds(8.00d);
    private static readonly TimeSpan PreparedSoftThreatHoldMaxDuration = TimeSpan.FromSeconds(6.00d);
    private static readonly TimeSpan PreparedReadySnapshotCadence = TimeSpan.FromSeconds(0.20d);
    private static readonly TimeSpan PreparedConvergenceLogInterval = TimeSpan.FromSeconds(2.00d);
    private static readonly TimeSpan PreparedLaunchRetryCooldown = TimeSpan.FromSeconds(1.50d);
    private const float VanguardCoverSlotPreferredMaxDistance = 6.50f;
    private const float VanguardCoverSlotHardRejectDistance = 10.00f;
    private const int VanguardCoverSlotMaxStagnantCommands = 5;
    private const int VanguardCoverSlotMaxReselects = 5;
    private const float VanguardRejectedCoverSlotCellMeters = 0.75f;
    private const int VanguardCoverPreflightMaxCandidates = 8;
    private const int VanguardCoverPreflightMaxMoveProbes = 8;
    private const int VanguardCoverAnchorMaxPerCandidate = 6;
    private const int IncrementalCoverCandidatesPerTick = 1;
    private const int IncrementalCoverMoveProbesPerTick = 1;
    private const int GlobalCoverCandidateBuildsPerFrame = 1;
    private const int GlobalCoverAnchorBuildsPerFrame = 1;
    private const int GlobalCoverMoveProbesPerFrame = 2;
    private const int IncrementalAiCoverPointScanLimit = 24;
    private const int IncrementalAiCoverAcceptedLimit = 6;
    private const int IncrementalWallSampleLimit = 6;
    private static readonly Vector3[] IncrementalWallDirections =
    {
        new(1f, 0f, 0f), new(-1f, 0f, 0f), new(0f, 0f, 1f), new(0f, 0f, -1f),
        new(0.707f, 0f, 0.707f), new(-0.707f, 0f, 0.707f), new(0.707f, 0f, -0.707f), new(-0.707f, 0f, -0.707f)
    };
    private static readonly float[] IncrementalWallDistances = { 3.0f, 4.5f, 6.0f };
    private static readonly Vector3[] IncrementalObstacleDirections =
    {
        Vector3.forward, Vector3.back, Vector3.left, Vector3.right,
        new(0.707f, 0f, 0.707f), new(-0.707f, 0f, 0.707f), new(0.707f, 0f, -0.707f), new(-0.707f, 0f, -0.707f)
    };
    private static readonly TimeSpan IncrementalCoverSearchTtl = TimeSpan.FromSeconds(20.0d);
    private static readonly TimeSpan VanguardCoverCommandObservationGrace = TimeSpan.FromSeconds(4.00d);
    // Runtime invariant: admission remains strict, while a committed cover slot gets a wider retention envelope.
    // This is real hysteresis: initial surgery cover admission still requires 3.75 m, but harmless
    // post-crouch/NavMesh drift does not revoke the commit until 4.50 m has been exceeded persistently.
    private const float VanguardCoverAdmissionDistance = 3.75f;
    private const float VanguardCoverCommitRetentionDistance = 4.50f;
    private const int VanguardCoverCommitExitMinSamples = 2;
    private static readonly TimeSpan VanguardCoverCommitExitObservationWindow = TimeSpan.FromSeconds(1.00d);
    private static readonly TimeSpan VanguardCoverCommitExitSampleCadence = TimeSpan.FromSeconds(0.35d);
    private static readonly TimeSpan PrepareMutationInterval = TimeSpan.FromSeconds(0.50d);
    private static readonly TimeSpan CoverProbeFailureMemoryTtl = TimeSpan.FromSeconds(8.00d);
    private static readonly Dictionary<string, DateTimeOffset> RetryAllowedAtByBotProfile = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastBlockedLogAtByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLocalHoldBlockLogAtByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastPrepareMutationAtByBotProfile = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> CoverSlotReselectCountByBotProfile = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, VanguardSurgeryCoverSlotState> CoverSlotsByBotProfile = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> RejectedCoverSlotUntilByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> ActionabilityGraceUntilByBotProfile = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> LastCoverFailureReasonByBotProfile = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CoverProbeFailureState> CoverProbeFailureByBotProfile = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastAnchorProbeLogAtByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastDeferredTransitionLogAtByBotProfile = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> LastDeferredTransitionReasonByBotProfile = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> SuppressedDeferredTransitionCountByBotProfile = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IncrementalCoverSearchState> IncrementalCoverSearchByBotProfile = new(StringComparer.OrdinalIgnoreCase);

    public static void Reset(string reason)
    {
        RetryAllowedAtByBotProfile.Clear();
        LastBlockedLogAtByKey.Clear();
        LastLocalHoldBlockLogAtByKey.Clear();
        LastPrepareMutationAtByBotProfile.Clear();
        CoverSlotReselectCountByBotProfile.Clear();
        CoverSlotsByBotProfile.Clear();
        RejectedCoverSlotUntilByKey.Clear();
        ActionabilityGraceUntilByBotProfile.Clear();
        LastCoverFailureReasonByBotProfile.Clear();
        CoverProbeFailureByBotProfile.Clear();
        LastAnchorProbeLogAtByKey.Clear();
        LastDeferredTransitionLogAtByBotProfile.Clear();
        LastDeferredTransitionReasonByBotProfile.Clear();
        SuppressedDeferredTransitionCountByBotProfile.Clear();
        foreach (var state in IncrementalCoverSearchByBotProfile.Values)
        {
            DisposeIncrementalSearchState(state);
        }
        IncrementalCoverSearchByBotProfile.Clear();
        VanguardSurgeryAdmissionSettleGate.Reset(reason);
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_MEDICAL_PREPARE_SURGERY_COVER_RESET reason={Safe(reason)}; patientOnly=true; bounded=true; cancelsLoot=patient_only; mutatesMovement=vanguard_cover_slot_patient_only_after_external_preempt; mutatesSain=cover_update_only; fallback=none_go_cover_required; previousTag={SurgeryCoverMoveBridgeStatusTag}; isolationTag={MedicalIsolationStatusTag}; externalAdapterTag={ExternalAuthorityAdapterStatusTag}; externalMovementTag={ExternalMovementPreemptStatusTag}; combatGateTag={CombatAwareGateStatusTag}; typedFailureTag={TypedCoverFailureStatusTag}; orbitLayerTag={OrbitLayerQuiesceStatusTag}; coverArrivalTag={CoverArrivalGrantStatusTag}; commitUnificationTag={MedicalCoverCommitUnificationStatusTag}; movementStabilizationTag={MedicalCoverMovementStabilizationStatusTag}; hardProcedureTag={MedicalHardProcedureAuthorityStatusTag}; completionGateTag={MedicalProcedureCompletionGateStatusTag}; directChainTag={MedicalSurgeryDirectChainStatusTag}; sameProcedureStartTag={MedicalSurgerySameProcedureStartStatusTag}; validTargetTag={ValidSurgeryTargetsStatusTag}; criticalFastTag={CriticalFastSurgeryStatusTag}; vanguardSlotTag={VanguardCoverSlotStatusTag}; sainLikeTag={SainLikeCoverSeekStatusTag}; residualTag={ResidualPrepareStatusTag}; tag={StatusTag}; hardOrbitExitTag={HardOrbitExitStatusTag}; pathGateTag={PathGateStatusTag}; preflightTag={CoverPreflightStatusTag}; multiCandidateTag={CoverMultiCandidateStatusTag}; anchorTag={CoverAnchorStatusTag}; anchorPreflightTag={CoverAnchorPreflightStatusTag}; previousCleanupTag={MedicalRuntimeCleanupStatusTag}; convergenceTag={SurgeryPreparationConvergenceStatusTag}");
    }

    public static bool IsPrepareLease(VanguardExecutionLeaseState lease)
    {
        return string.Equals(lease.IntentKey, IntentKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(lease.WindowKind, WindowKind, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryStart(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, VanguardIntentDryRunBoard board, DateTimeOffset now)
    {
        if (botOwner == null || !snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            return false;
        }

        if (!IsSurgeryNeed(snapshot.Medical.Need.DominantNeed))
        {
            LogSkip(snapshot, board, "need_not_surgery_scope", now);
            return false;
        }

        // The runtime invariant at the executor boundary: every prepare entry path, including forced
        // surgery-debt retries, must rejoin before opening a stationary medical movement lease.
        if (VanguardMovementAuthorityDoctrine.ShouldRejoinBeforeStationaryMedicalStart(snapshot, VanguardMovementAuthorityDoctrine.StationaryMedicalStartMaxOwnerDistanceMeters, out var prepareLeashReason))
        {
            LogSkip(snapshot, board, "stationary_medical_leash:" + prepareLeashReason, now);
            LogStationaryMedicalDeferred(snapshot, prepareLeashReason, "try_start", now);
            return false;
        }

        // Performance guard: do not create a lease that will be interrupted by the scheduler or
        // a direct-fire safety gate in the same frame. The medical debt remains in the snapshot.
        if (VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out var protectedCombatReason))
        {
            LogSkip(snapshot, board, "combat_window_protected:" + protectedCombatReason, now);
            LogStationaryMedicalDeferred(snapshot, "combat_window_protected:" + protectedCombatReason, "try_start", now);
            return false;
        }

        if (HasHardThreatInterrupt(snapshot, out var startThreatReason))
        {
            LogSkip(snapshot, board, "hard_threat_interrupt:" + startThreatReason, now);
            LogStationaryMedicalDeferred(snapshot, "hard_threat_interrupt:" + startThreatReason, "try_start", now);
            return false;
        }

        var surgeryCandidateState = VanguardMedicalSurgeryTargetPolicy.EvaluateSurgeryPreparationCandidate(snapshot, out var validTargetReason);
        if (surgeryCandidateState == VanguardSurgeryCandidateState.Invalid)
        {
            LogSkip(snapshot, board, "invalid_surgery_candidate:" + validTargetReason, now);
            return false;
        }

        // Runtime invariant: preserve the surgery debt, but do not open movement/isolation authority while
        // hands are already in a transient state. The scheduler will reconsider immediately
        // after the hands-controller transition without recording a medical failure.
        if (surgeryCandidateState == VanguardSurgeryCandidateState.Transient)
        {
            LogStationaryMedicalDeferred(snapshot, "deferred_transient:" + validTargetReason, "try_start", now);
            return false;
        }

        if (!VanguardSurgeryAdmissionSettleGate.CanAdmit(snapshot, now, out var settleReason))
        {
            LogStationaryMedicalDeferred(snapshot, "surgery_admission_settle:" + settleReason, "try_start", now);
            return false;
        }

        if (!VanguardMedicalSurgeryPreparePolicy.ShouldPrepareBeforeStationarySurgery(snapshot, now, out var policyReason))
        {
            LogSkip(snapshot, board, "prepare_policy_blocked:" + policyReason, now);
            return false;
        }

        if (RetryAllowedAtByBotProfile.TryGetValue(snapshot.BotProfileId, out var retryAt) && retryAt > now)
        {
            LogSkip(snapshot, board, "legacy_prepare_retry_cooldown_until_" + retryAt.ToString("O", CultureInfo.InvariantCulture), now);
            return false;
        }

        var lease = new VanguardExecutionLeaseState
        {
            LeaseId = "med-surgery-prep-" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            OperatorId = snapshot.OperatorId,
            BotProfileId = snapshot.BotProfileId,
            IntentKey = IntentKey,
            WindowKind = WindowKind,
            MedicalNeed = snapshot.Medical.Need.DominantNeed,
            TargetPart = SafeTarget(snapshot.Medical.Actionability.TargetPart, snapshot.Medical.Need.TargetPart),
            ItemTemplateId = snapshot.Medical.Actionability.SelectedItemTemplateId,
            ItemName = snapshot.Medical.Actionability.SelectedItemName,
            InitialHealthPercent = snapshot.Medical.Need.HealthPercent,
            InitialTargetHealth = -1f,
            InitialTargetMaxHealth = -1f,
            InitialNeedTargetPart = snapshot.Medical.Need.TargetPart,
            StartedAtUtc = now,
            MinUntilUtc = now + MinDuration,
            MaxUntilUtc = now + MaxDuration,
            LastProgressAtUtc = now,
            NoProgressUntilUtc = now + NoProgressTimeout,
            Attempted = true
        };

        if (!VanguardExecutionLeaseStore.TryStart(lease))
        {
            return false;
        }

        CoverSlotReselectCountByBotProfile.Remove(lease.BotProfileId);
        LastCoverFailureReasonByBotProfile.Remove(lease.BotProfileId);

        var isolation = VanguardMedicalIsolationController.BeginOrUpdatePrepareIsolation(lease, botOwner, snapshot, now);
        if (isolation.ShouldFail)
        {
            VanguardMedicalExecutionResultBridge.Publish(
                lease,
                VanguardMedicalActionOutcomeKind.Failed,
                "medical_isolation_failed:" + isolation.FailureReason,
                isolation.Summary,
                now);
            VanguardExecutionLeaseStore.Release(lease.BotProfileId);
            var isolationRetryAt = now + CooldownForPrepareOutcome("Failed", isolation.FailureReason);
            RetryAllowedAtByBotProfile[lease.BotProfileId] = isolationRetryAt;
            VanguardExecutionLeaseStore.RegisterOutcomeDetailed(lease.BotProfileId, lease.MedicalNeed, lease.TargetPart, lease.ItemTemplateId, "Failed", "medical_isolation_failed:" + isolation.FailureReason, lease.LastProgressKind, isolationRetryAt);
            VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_EXECUTION_FAILED {lease.Summary}; outcome=Failed; reason=medical_isolation_failed:{Safe(isolation.FailureReason)}; retryAt={isolationRetryAt:O}; {isolation.Summary}; patientOnly=true; tag={StatusTag}; movementStabilizationTag={MedicalCoverMovementStabilizationStatusTag}");
            return false;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_MEDICAL_PREPARE_SURGERY_COVER_SELECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; selectedIntent={board.Selected.IntentKey}; score={board.Selected.FinalScore:0.00}; window={board.ExecutionWindow.Summary}; need={snapshot.Medical.Need.DominantNeed}; target={Safe(lease.TargetPart)}; item={Safe(lease.ItemName)}; tpl={Safe(lease.ItemTemplateId)}; blocker={CurrentPrepareBlocker(snapshot)}; patientOnly=true; isolation=true; tag={StatusTag}");
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_EXECUTION_LEASE_STARTED {lease.Summary}; min={MinDuration.TotalSeconds:0.00}; max={MaxDuration.TotalSeconds:0.00}; noProgress={NoProgressTimeout.TotalSeconds:0.00}; movementAllowed={Bool(isolation.CanDriveMovement)}; followAllowed=false; combatAllowed=false; medicalAction=false; patientOnly=true; mode=medical_isolation_then_vanguard_cover_slot; {isolation.Summary}; tag={StatusTag}");

        if (isolation.CanDriveMovement)
        {
            var effect = ApplyPreparation(botOwner, snapshot, forceMutation: true);
            VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_MEDICAL_PREPARE_SURGERY_COVER_ATTEMPTED {lease.Summary}; {effect}; targetNext=StationaryMedicalSurgery; exactItem=true; exactTarget=true; coverSeek=true; vanguardCoverSlot=true; isolation=true; tag={StatusTag}");
        }
        else
        {
            VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_MEDICAL_PREPARE_SURGERY_COVER_WAITING {lease.Summary}; reason=await_medical_isolation_quiesce; {isolation.Summary}; targetNext=MoveToSurgeryCover; tag={StatusTag}");
        }

        return true;
    }

    public static void Update(VanguardExecutionLeaseState lease, BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        if (!snapshot.Alive)
        {
            Complete(lease, now, "Failed", "operator_dead", true);
            return;
        }

        if (VanguardMainIntentScheduler.TryGetActiveEmergencyWindow(snapshot.BotProfileId, now, out string grenadeWindowId, out string grenadeKey, out _))
        {
            Complete(lease, now, "Interrupted", "grenade_emergency_primary:" + grenadeKey, false);
            VanguardClientDiagnosticsLog.Info(VanguardGrenadeEmergencyPolicy.StatusTag,
                $"VANGUARD_SURGERY_PREPARE_INTERRUPTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(lease.LeaseId)}; emergencyWindow={Safe(grenadeWindowId)}; grenade={Safe(grenadeKey)}; mutation=release_prepare_patient_only; debtRetained=true; retryPenalty=false; tag={VanguardGrenadeEmergencyPolicy.StatusTag}; medicalTag={StatusTag}");
            return;
        }

        if (VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out var combatProtectionReason))
        {
            Complete(lease, now, "Interrupted", "sain_combat_primary_protected_before_surgery:" + combatProtectionReason, true);
            VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.SainWindowStatusTag,
                $"VANGUARD_SURGERY_PREPARE_INTERRUPTED_BY_COMBAT operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(lease.LeaseId)}; reason={Safe(combatProtectionReason)}; mutation=release_medical_prepare_and_return_sain_authority; doctrine=stationary_medical_never_drives_beside_sain_combat; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; medicalTag={StatusTag}");
            return;
        }

        if (!IsSurgeryNeed(snapshot.Medical.Need.DominantNeed))
        {
            Complete(lease, now, "Completed", "surgery_need_resolved_or_reprioritized", false);
            return;
        }

        if (VanguardMovementAuthorityDoctrine.ShouldRejoinBeforeStationaryMedicalStart(snapshot, VanguardMovementAuthorityDoctrine.StationaryMedicalStartMaxOwnerDistanceMeters, out var prepareLeashReason))
        {
            if (!CanFinishPreparedSurgeryBeforeRejoin(lease, snapshot, out var preparedLeashReason))
            {
                Complete(lease, now, "Interrupted", "stationary_medical_leash:" + prepareLeashReason, false);
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"VANGUARD_STATIONARY_MEDICAL_PREPARE_RELEASED {lease.Summary}; reason={Safe(prepareLeashReason)}; ownerDistance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; controllerUsing=false; mutation=release_prepare_for_cohesion_rejoin; tag=VANGUARD_STATIONARY_MEDICAL_LEASH_STATUS; medicalTag={StatusTag}");
                return;
            }

            lease.SurgeryPrepareOwnerLeashBypassed = true;
            LogPreparedConvergence(lease, snapshot, now, "owner_leash_bypassed_for_committed_surgery", preparedLeashReason);
        }

        var surgeryCandidateState = VanguardMedicalSurgeryTargetPolicy.EvaluateSurgeryPreparationCandidate(snapshot, out var actionReason);
        if (surgeryCandidateState == VanguardSurgeryCandidateState.Invalid)
        {
            Complete(lease, now, "Failed", "invalid_surgery_candidate:" + actionReason, true);
            return;
        }

        if (surgeryCandidateState == VanguardSurgeryCandidateState.Transient)
        {
            if (CanGraceActionabilityDuringPrepare(lease, snapshot, now, actionReason, out var graceReason))
            {
                ObservePrepareProgress(lease, snapshot, now, "actions=transient_hands_grace;" + graceReason);
                return;
            }

            // A reload, grenade throw or another medicine controller is not a failed medical
            // attempt. Release patient-only movement/isolation authority without retry penalty;
            // the persistent debt will wake the scheduler when hands become available again.
            Complete(lease, now, "Interrupted", "deferred_transient:" + actionReason, false);
            return;
        }
        ActionabilityGraceUntilByBotProfile.Remove(lease.BotProfileId);

        if (HasCurrentPreparedSurgeryThreat(lease, snapshot, out var threatReason, out var softRecentFireHold))
        {
            lease.ThreatObservedDuringLease = true;
            Complete(lease, now, "Interrupted", "hard_threat_interrupt:" + threatReason, true);
            return;
        }

        if (softRecentFireHold)
        {
            lease.ThreatObservedDuringLease = true;
            if (lease.SurgeryPrepareSoftThreatSinceUtc == DateTimeOffset.MinValue)
            {
                lease.SurgeryPrepareSoftThreatSinceUtc = now;
            }

            var heldFor = now - lease.SurgeryPrepareSoftThreatSinceUtc;
            if (heldFor >= PreparedSoftThreatHoldMaxDuration)
            {
                Complete(lease, now, "Interrupted", "prepared_soft_threat_hold_expired:" + threatReason, false);
                VanguardClientDiagnosticsLog.Info(SurgeryPreparationConvergenceStatusTag,
                    $"VANGUARD_SURGERY_PREPARE_POSTURE_RELEASED {lease.Summary}; reason={Safe(threatReason)}; heldFor={heldFor.TotalSeconds:0.00}; max={PreparedSoftThreatHoldMaxDuration.TotalSeconds:0.00}; currentThreat=false; recentFireOnly=true; coverCommitPhysicalPositionRetained=true; debtRetained=true; retryCooldown=false; next=medical_recheck_after_threat; tag={SurgeryPreparationConvergenceStatusTag}");
                return;
            }

            VanguardExternalAuthorityAdapter.RefreshHardMedicalProcedureAuthority(botOwner, snapshot, "prepared_soft_recent_fire_hold", now);
            ObservePrepareProgress(lease, snapshot, now, "actions=prepared_soft_recent_fire_hold;reason=" + Safe(threatReason) + ";heldFor=" + heldFor.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture));
            LogPreparedConvergence(lease, snapshot, now, "prepared_soft_threat_hold", threatReason);
            return;
        }

        lease.SurgeryPrepareSoftThreatSinceUtc = DateTimeOffset.MinValue;

        var isolation = VanguardMedicalIsolationController.BeginOrUpdatePrepareIsolation(lease, botOwner, snapshot, now);
        if (isolation.ShouldFail)
        {
            bool combatDefer = isolation.FailureReason.IndexOf("deferred_by_combat_owner", StringComparison.OrdinalIgnoreCase) >= 0;
            Complete(lease, now, combatDefer ? "Interrupted" : "Failed", combatDefer ? "medical_isolation_deferred_by_combat_owner" : "medical_isolation_failed:" + isolation.FailureReason, true);
            return;
        }

        if (!isolation.CanDriveMovement)
        {
            ObservePrepareProgress(lease, snapshot, now, "actions=medical_isolation_wait;" + isolation.Summary);
            return;
        }

        var effect = ApplyPreparation(botOwner, snapshot, forceMutation: false);
        ObservePrepareProgress(lease, snapshot, now, effect);

        if (TryGetReadyReasonFromPreparationEffect(effect, snapshot, out var effectReadyReason) && now >= lease.MinUntilUtc)
        {
            if (TryDirectChainToStationarySurgery(lease, botOwner, snapshot, now, "effect_ready:" + effectReadyReason))
            {
                return;
            }

            ObservePrepareProgress(lease, snapshot, now, effect + ";directChainWaiting=true;directChainReason=" + Safe(effectReadyReason));
            return;
        }

        if (effect.Contains("movementCommandUnavailable=true", StringComparison.OrdinalIgnoreCase))
        {
            if (IsHardProcedureStillViable(snapshot) && now < lease.MaxUntilUtc)
            {
                VanguardExternalAuthorityAdapter.RefreshHardMedicalProcedureAuthority(botOwner, snapshot, "movement_command_unavailable_retry", now);
                ObservePrepareProgress(lease, snapshot, now, effect + ";hardProcedureRetry=true;noReleaseBefore45s=true");
                return;
            }

            Complete(lease, now, "Failed", "movement_command_unavailable:" + ExtractCoverFailureReason(effect, snapshot), true);
            return;
        }

        if (effect.Contains("coverSeek=vanguard_slot_unavailable", StringComparison.OrdinalIgnoreCase))
        {
            if (IsHardProcedureStillViable(snapshot) && now < lease.MaxUntilUtc)
            {
                VanguardExternalAuthorityAdapter.RefreshHardMedicalProcedureAuthority(botOwner, snapshot, "cover_slot_unavailable_retry", now);
                ObservePrepareProgress(lease, snapshot, now, effect + ";hardProcedureRetry=true;noReleaseBefore45s=true");
                return;
            }

            Complete(lease, now, "Failed", "cover_slot_unavailable:" + ExtractCoverFailureReason(effect, snapshot), true);
            return;
        }

        if (TryPromoteCurrentCoverSlotToCommit(lease, botOwner, snapshot, now, "post_effect_ready_unification", out var promotedReadyReason) && now >= lease.MinUntilUtc)
        {
            if (TryDirectChainToStationarySurgery(lease, botOwner, snapshot, now, "promoted_cover_commit:" + promotedReadyReason))
            {
                return;
            }

            ObservePrepareProgress(lease, snapshot, now, "actions=direct_chain_wait_after_promoted_commit;reason=" + Safe(promotedReadyReason));
            return;
        }

        if (IsReadyForStationarySurgery(snapshot, out var readyReason) && now >= lease.MinUntilUtc)
        {
            if (TryDirectChainToStationarySurgery(lease, botOwner, snapshot, now, "snapshot_ready:" + readyReason))
            {
                return;
            }

            ObservePrepareProgress(lease, snapshot, now, "actions=direct_chain_wait_after_snapshot_ready;reason=" + Safe(readyReason));
            return;
        }

        if (TryForceHardProcedureCurrentPositionCommit(lease, botOwner, snapshot, now, "pre_timeout_current_position_capture", out var forcedCommitReason) && now >= lease.MinUntilUtc)
        {
            if (TryDirectChainToStationarySurgery(lease, botOwner, snapshot, now, "hard_current_position_commit:" + forcedCommitReason))
            {
                return;
            }

            ObservePrepareProgress(lease, snapshot, now, "actions=direct_chain_wait_after_current_position_commit;reason=" + Safe(forcedCommitReason));
            return;
        }

        if (now >= lease.MaxUntilUtc)
        {
            if ((CurrentPrepareBlocker(snapshot).Equals("ready", StringComparison.OrdinalIgnoreCase)
                    || IsHardProcedureStillViable(snapshot))
                && (TryPromoteCurrentCoverSlotToCommit(lease, botOwner, snapshot, now, "max_window_ready_unification", out var timeoutReadyReason)
                    || TryForceHardProcedureCurrentPositionCommit(lease, botOwner, snapshot, now, "max_window_hard_procedure_current_position", out timeoutReadyReason)))
            {
                if (TryDirectChainToStationarySurgery(lease, botOwner, snapshot, now, "max_window_ready:" + timeoutReadyReason))
                {
                    return;
                }

                ObservePrepareProgress(lease, snapshot, now, "actions=direct_chain_wait_at_max_window;reason=" + Safe(timeoutReadyReason));
                return;
            }

            Complete(lease, now, "Timeout", "HardProcedureTimeout45s:" + CurrentPrepareBlocker(snapshot), true);
            return;
        }

        if (now >= lease.NoProgressUntilUtc)
        {
            if (IsHardProcedureStillViable(snapshot))
            {
                VanguardExternalAuthorityAdapter.RefreshHardMedicalProcedureAuthority(botOwner, snapshot, "prepare_no_progress_continue_until_45s", now);
                lease.LastProgressAtUtc = now;
                lease.LastProgressKind = "hard_procedure_authority_held_no_release";
                lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(3.00d);
                VanguardClientDiagnosticsLog.Info(MedicalHardProcedureAuthorityStatusTag, $"VANGUARD_MEDICAL_HARD_PROCEDURE_NO_PROGRESS_HELD {lease.Summary}; blocker={CurrentPrepareBlocker(snapshot)}; elapsed={(now - lease.StartedAtUtc).TotalSeconds:0.00}; max={(lease.MaxUntilUtc - lease.StartedAtUtc).TotalSeconds:0.00}; releaseCondition=cover_committed_or_true_threat_or_prepare_max_window; next=continue_cover_or_commit; tag={MedicalHardProcedureAuthorityStatusTag}; completionGateTag={MedicalProcedureCompletionGateStatusTag}");
                return;
            }

            Complete(lease, now, "Timeout", "NoProgress:" + CurrentPrepareBlocker(snapshot), true);
        }
    }

    private static string ApplyPreparation(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, bool forceMutation)
    {
        var actions = new List<string>(12);
        if (botOwner == null)
        {
            return "actions=none; reason=botowner_null";
        }

        bool mutationAllowed = forceMutation || ShouldRunPrepareMutation(snapshot);
        actions.Add("prepareMutationAllowed=" + Bool(mutationAllowed));

        if (mutationAllowed)
        {
            var externalPreempt = VanguardExternalAuthorityAdapter.RefreshHardMedicalProcedureAuthority(botOwner, snapshot, "surgery_cover_prepare", DateTimeOffset.UtcNow);
            actions.Add(externalPreempt.Summary);
            if (externalPreempt.IsCombatDefer)
            {
                actions.Add("combatOwnerCannotDriveMovement=true");
                actions.Add("movementCommandUnavailable=true");
                actions.Add("typedCoverFailure=" + VanguardCoverMovementFailureKind.CombatOwnerCannotDriveMovement);
                return "actions=" + string.Join(",", actions);
            }
        }
        else
        {
            var externalActivity = VanguardExternalAuthorityAdapter.ReadActivity(botOwner, snapshot, DateTimeOffset.UtcNow, log: false, reason: "surgery_cover_prepare_no_mutation");
            actions.Add(externalActivity.Summary);
        }

        object? medicalMover = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Mover");
        var coverSeek = ApplyVanguardSurgeryCoverSlot(botOwner, snapshot, medicalMover, "medical_isolation_vanguard_owned", "off", false, false);
        actions.Add(coverSeek.Summary);
        actions.Add("movementAuthority=medical_isolation_vanguard_only");
        actions.Add("blockerBefore=" + CurrentPrepareBlocker(snapshot));
        return "actions=" + string.Join(",", actions);
    }

    private static SainLikeCoverSeekResult ApplyVanguardSurgeryCoverSlot(BotOwner botOwner, OperatorDecisionSnapshot snapshot, object? mover, string sainCoverState, string sainFinderState, bool sainUpdateCover, bool sainPoseSet)
    {
        var now = DateTimeOffset.UtcNow;
        string key = Safe(snapshot.BotProfileId);
        if (string.Equals(key, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new SainLikeCoverSeekResult("vanguard_slot_unavailable", false, false, true, sainUpdateCover, sainPoseSet, sainCoverState, sainFinderState, "none", false, "none", "bot_profile_missing", false, "vanguardSlot=unavailable");
        }

        // The runtime doctrine: CMS/Surv12 surgery is go-cover only.
        // Do not grant surgery from the current position, even when the snapshot looks locally safe.
        // A real Vanguard-owned cover slot must be reached before StationaryMedicalSurgery can start.

        if (VanguardExternalAuthorityAdapter.ShouldDeferMedicalMovementForCombat(botOwner, snapshot, now, "surgery_cover_slot", out _, out var combatGateSummary))
        {
            SetLastCoverFailure(key, "combat_owner_cannot_drive_movement");
            VanguardClientDiagnosticsLog.Info(CombatAwareGateStatusTag, $"VANGUARD_MEDICAL_COVER_MOVE_DEFERRED_BY_COMBAT operator={Safe(snapshot.OperatorId)}; botProfile={key}; {combatGateSummary}; noCoverProbe=true; noGoCover=true; patientOnly=true; tag={CombatAwareGateStatusTag}; typedFailureTag={TypedCoverFailureStatusTag}");
            return new SainLikeCoverSeekResult("vanguard_slot_unavailable", false, false, true, sainUpdateCover, sainPoseSet, sainCoverState, sainFinderState, "none", false, "none", "combat_owner_cannot_drive_movement", false, "vanguardSlot=unavailable;movementCommandUnavailable=true;typedCoverFailure=" + VanguardCoverMovementFailureKind.CombatOwnerCannotDriveMovement + ";combatOwnerCannotDriveMovement=true;noOpenField=true");
        }

        if (!CoverSlotsByBotProfile.TryGetValue(key, out var slot) || slot.ExpiresAtUtc <= now || Vector3.Distance(Flat(slot.Origin), Flat(snapshot.Position)) > 42f)
        {
            long coverSearchStarted = VanguardRuntimePerformanceGuard.Begin();
            bool coverFound;
            Vector3 target;
            string source;
            string diagnostic;
            string preflightResult;
            try
            {
                coverFound = TryFindVanguardSurgeryCoverSlot(botOwner, snapshot, mover, key, now, out target, out source, out diagnostic, out preflightResult);
            }
            finally
            {
                VanguardRuntimePerformanceGuard.End("MedicalCoverSearchTick", coverSearchStarted);
            }

            if (!coverFound)
            {
                if (diagnostic.StartsWith("cover_search_pending", StringComparison.OrdinalIgnoreCase))
                {
                    return new SainLikeCoverSeekResult("vanguard_slot_search_pending", false, false, false, sainUpdateCover, sainPoseSet, sainCoverState, sainFinderState, "none", false, "incremental_search", diagnostic, false, "vanguardSlot=search_pending;movementCommandUnavailable=false;incrementalCoverSearch=true;budgetCandidates=" + IncrementalCoverCandidatesPerTick + ";budgetProbes=" + IncrementalCoverMoveProbesPerTick + ";noOpenField=true");
                }

                CoverSlotsByBotProfile.Remove(key);
                SetLastCoverFailure(key, diagnostic);
                VanguardClientDiagnosticsLog.Info(CoverPreflightStatusTag, $"VANGUARD_SURGERY_COVER_PREFLIGHT_UNAVAILABLE operator={Safe(snapshot.OperatorId)}; botProfile={key}; reason={Safe(diagnostic)}; sainCoverState={Safe(sainCoverState)}; sainCoverFinder={Safe(sainFinderState)}; noOpenField=true; patientOnly=true; tag={CoverPreflightStatusTag}");
                return new SainLikeCoverSeekResult("vanguard_slot_unavailable", false, false, true, sainUpdateCover, sainPoseSet, sainCoverState, sainFinderState, "none", false, "none", diagnostic, false, "vanguardSlot=unavailable;movementCommandUnavailable=true;preflightNoMoverValidCover=true;typedCoverFailure=" + ClassifyCoverMovementFailure(diagnostic, preflightResult) + ";noOpenField=true");
            }

            LastCoverFailureReasonByBotProfile.Remove(key);

            slot = new VanguardSurgeryCoverSlotState
            {
                OperatorId = snapshot.OperatorId,
                BotProfileId = snapshot.BotProfileId,
                Origin = snapshot.Position,
                Target = target,
                Source = source,
                Diagnostic = diagnostic + ":preflight=" + Safe(preflightResult),
                CreatedAtUtc = now,
                ExpiresAtUtc = now + VanguardCoverSlotTtl,
                GrantUntilUtc = DateTimeOffset.MinValue,
                LastCommandAtUtc = DateTimeOffset.MinValue,
                InitialDistance = Distance2D(snapshot.Position, target),
                LastDistance = Distance2D(snapshot.Position, target),
                BestDistance = Distance2D(snapshot.Position, target),
                BestPosition = snapshot.Position,
                LastMeaningfulProgressAtUtc = now,
                LastWorldPosition = snapshot.Position,
                LastWorldSampleAtUtc = now,
                CommandCount = 0,
                StagnantCommandCount = 0,
                EftCommandPreferred = true
            };
            CoverSlotsByBotProfile[key] = slot;
            VanguardClientDiagnosticsLog.Info(CoverPreflightStatusTag, $"VANGUARD_SURGERY_COVER_PREFLIGHT_ACCEPTED operator={Safe(snapshot.OperatorId)}; botProfile={key}; source={Safe(source)}; diagnostic={Safe(diagnostic)}; preflight={Safe(preflightResult)}; from={FormatVector(snapshot.Position)}; target={FormatVector(target)}; distance={slot.LastDistance:0.00}; moverValid=true; noOpenField=true; patientOnly=true; tag={CoverPreflightStatusTag}");
            VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_SURGERY_COVER_SLOT_ASSIGNED operator={Safe(snapshot.OperatorId)}; botProfile={key}; source={Safe(source)}; diagnostic={Safe(slot.Diagnostic)}; from={FormatVector(snapshot.Position)}; target={FormatVector(target)}; distance={slot.LastDistance:0.00}; ttl={VanguardCoverSlotTtl.TotalSeconds:0.00}; noOpenField=true; patientOnly=true; tag={StatusTag}");
        }

        float distance = Distance2D(snapshot.Position, slot.Target);
        bool meaningfulImprovement = slot.BestDistance - distance > 0.35f;
        bool stepImproved = slot.LastDistance - distance > 0.18f;
        bool worsened = distance - slot.LastDistance > 0.75f;
        if (meaningfulImprovement)
        {
            slot.BestDistance = distance;
            slot.BestPosition = snapshot.Position;
            slot.LastMeaningfulProgressAtUtc = now;
            slot.StagnantCommandCount = 0;
        }

        TimeSpan worldSampleAge = now - slot.LastWorldSampleAtUtc;
        bool worldSampleMature = worldSampleAge >= TimeSpan.FromSeconds(0.75d);
        float worldDelta = worldSampleMature ? Distance2D(slot.LastWorldPosition, snapshot.Position) : 0f;
        bool physicalMoved = worldSampleMature && worldDelta >= 0.25f;
        if (worldSampleMature)
        {
            if (physicalMoved || meaningfulImprovement || stepImproved)
            {
                slot.PhysicalStallSamples = 0;
            }
            else if (slot.CommandCount > 0)
            {
                slot.PhysicalStallSamples++;
            }

            slot.LastWorldPosition = snapshot.Position;
            slot.LastWorldSampleAtUtc = now;
        }

        slot.LastDistance = distance;
        bool improved = meaningfulImprovement || stepImproved;
        float currentSpeed = Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed);
        bool withinAdmissionEnvelope = distance <= VanguardCoverAdmissionDistance;
        bool safeForAdmission = snapshot.Medical.Safety.CoveredOrHoldingAngle || snapshot.Medical.Safety.SurgeryAreaClear;
        bool externalIdle = !IsLootOrOrbitActive(snapshot);
        bool noHardThreatForCommit = !snapshot.Medical.Safety.EnemyCanShoot
            && !snapshot.Medical.Safety.IncomingFireRecent
            && !snapshot.Medical.Safety.ImmediateCombatBlock;

        bool activeCommitInvalidated = slot.CoverCommitUntilUtc > now
            && (!safeForAdmission || !externalIdle || !noHardThreatForCommit);
        if (activeCommitInvalidated)
        {
            string invalidationReason = !noHardThreatForCommit
                ? "hard_threat"
                : !externalIdle
                    ? "loot_or_orbit_active"
                    : "cover_safety_lost";
            InvalidateCommittedSlot(slot);
            VanguardClientDiagnosticsLog.Info(
                SurgeryCoverAdmissionConvergenceStatusTag,
                $"VANGUARD_SURGERY_COVER_COMMIT_INVALIDATED operator={Safe(snapshot.OperatorId)}; botProfile={key}; reason={invalidationReason}; source={Safe(slot.Source)}; target={FormatVector(slot.Target)}; distance={distance:0.00}; safetyCovered={Bool(snapshot.Medical.Safety.CoveredOrHoldingAngle)}; surgeryAreaClear={Bool(snapshot.Medical.Safety.SurgeryAreaClear)}; externalIdle={Bool(externalIdle)}; noHardThreat={Bool(noHardThreatForCommit)}; immediate=true; patientOnly=true; noOpenField=true; tag={SurgeryCoverAdmissionConvergenceStatusTag}");
        }

        // Runtime invariant: a committed slot is retained through harmless post-crouch drift. Entry remains strict
        // at 3.75 m, but retention is allowed to 4.50 m. Beyond 4.50 m, two distinct observations
        // spanning at least one second are required before revocation. Hard threat, unsafe placement
        // or external loot/ORBIT activity still invalidate immediately.
        bool retentionPending = false;
        string retentionReason = "not_committed";
        bool retainedCommit = slot.CoverCommitUntilUtc > now
            && safeForAdmission
            && externalIdle
            && noHardThreatForCommit
            && IsCommittedSlotRetentionValid(slot, distance, now, out retentionPending, out retentionReason);
        if (retainedCommit)
        {
            LastCoverFailureReasonByBotProfile.Remove(key);
            return new SainLikeCoverSeekResult(
                "vanguard_slot_arrived",
                false,
                false,
                false,
                sainUpdateCover,
                sainPoseSet,
                sainCoverState,
                sainFinderState,
                slot.Source,
                true,
                slot.Source,
                slot.Diagnostic,
                true,
                $"vanguardSlot=arrived;vanguardCoverGrant=true;coverCommit=true;commitIdempotent=true;vanguardSlotDist={distance:0.00};admissionDistance={VanguardCoverAdmissionDistance:0.00};retentionDistance={VanguardCoverCommitRetentionDistance:0.00};retentionPending={Bool(retentionPending)};retentionReason={Safe(retentionReason)};speed={currentSpeed:0.00};externalIdle={Bool(externalIdle)};noHardThreat={Bool(noHardThreatForCommit)};movementMutation=false;poseMutation=false;noOpenField=true");
        }

        bool persistentCommitExit = slot.CoverCommitUntilUtc > now
            && safeForAdmission
            && externalIdle
            && noHardThreatForCommit
            && distance > VanguardCoverCommitRetentionDistance
            && !retentionPending;
        if (persistentCommitExit && !slot.CommitCorrectionIssued)
        {
            slot.CommitCorrectionIssued = true;
            slot.GrantUntilUtc = DateTimeOffset.MinValue;
            slot.CoverCommitUntilUtc = DateTimeOffset.MinValue;
            slot.ArrivedLogged = false;
            bool correctionCommanded = TryCommandMoveToSurgerySlot(botOwner, mover, slot.Target, out var correctionResult);
            slot.LastCommandAtUtc = now;
            slot.CommandCount++;
            slot.LastCommandResult = correctionResult;
            if (!correctionCommanded || IsNegativeMoveCommandResult(correctionResult))
            {
                string correctionFailure = "commit_retention_correction_failed:" + Safe(correctionResult);
                bool retryAllowed = TryScheduleCoverReselection(key, slot, now, correctionFailure, out int reselectCount);
                SetLastCoverFailure(key, correctionFailure);
                VanguardClientDiagnosticsLog.Info(SurgeryCoverAdmissionConvergenceStatusTag,
                    $"VANGUARD_SURGERY_COVER_COMMIT_CORRECTION_REJECTED operator={Safe(snapshot.OperatorId)}; botProfile={key}; source={Safe(slot.Source)}; target={FormatVector(slot.Target)}; distance={distance:0.00}; admissionDistance={VanguardCoverAdmissionDistance:0.00}; retentionDistance={VanguardCoverCommitRetentionDistance:0.00}; retentionReason={Safe(retentionReason)}; correctionResult={Safe(correctionResult)}; reselectCount={reselectCount}; retryAllowed={Bool(retryAllowed)}; patientOnly=true; noOpenField=true; tag={SurgeryCoverAdmissionConvergenceStatusTag}");
                return retryAllowed
                    ? new SainLikeCoverSeekResult("vanguard_slot_search_pending", false, false, false, sainUpdateCover, sainPoseSet, sainCoverState, sainFinderState, "none", false, "incremental_reselect", "cover_search_pending:commit_correction_failed", false, "vanguardSlot=search_pending;movementCommandUnavailable=false;commitCorrectionFailed=true;noOpenField=true")
                    : new SainLikeCoverSeekResult("vanguard_slot_unavailable", false, false, true, sainUpdateCover, sainPoseSet, sainCoverState, sainFinderState, "none", false, "none", "commit_correction_reselect_budget_exhausted", false, "vanguardSlot=unavailable;movementCommandUnavailable=true;commitCorrectionFailed=true;reselectBudgetExhausted=true;noOpenField=true");
            }

            VanguardClientDiagnosticsLog.Info(SurgeryCoverAdmissionConvergenceStatusTag,
                $"VANGUARD_SURGERY_COVER_COMMIT_CORRECTION operator={Safe(snapshot.OperatorId)}; botProfile={key}; source={Safe(slot.Source)}; target={FormatVector(slot.Target)}; distance={distance:0.00}; admissionDistance={VanguardCoverAdmissionDistance:0.00}; retentionDistance={VanguardCoverCommitRetentionDistance:0.00}; outsideSamples={slot.CommitOutsideEnvelopeSamples}; outsideFor={(now - slot.CommitOutsideEnvelopeSinceUtc).TotalSeconds:0.00}; correctionCommanded={Bool(correctionCommanded)}; correctionResult={Safe(correctionResult)}; correctionBudget=one_same_slot_before_reselect; grantRevoked=true; postureMutation=false; patientOnly=true; noOpenField=true; tag={SurgeryCoverAdmissionConvergenceStatusTag}");
            return new SainLikeCoverSeekResult("vanguard_slot_assigned", false, false, false, sainUpdateCover, sainPoseSet, sainCoverState, sainFinderState, slot.Source, false, slot.Source, slot.Diagnostic, false, $"vanguardSlot=assigned;vanguardCoverGrant=false;coverCommit=false;commitCorrection=true;vanguardSlotDist={distance:0.00};admissionDistance={VanguardCoverAdmissionDistance:0.00};retentionDistance={VanguardCoverCommitRetentionDistance:0.00};vanguardCommand=true;vanguardCommandResult={Safe(correctionResult)};noOpenField=true");
        }

        bool admissionReady = withinAdmissionEnvelope
            && safeForAdmission
            && currentSpeed <= 1.10f
            && externalIdle
            && noHardThreatForCommit;
        if (admissionReady)
        {
            if (!slot.ArrivedLogged)
            {
                VanguardClientDiagnosticsLog.Info(
                    CoverArrivalGrantStatusTag,
                    $"VANGUARD_MEDICAL_COVER_READY operator={Safe(snapshot.OperatorId)}; botProfile={key}; source={Safe(slot.Source)}; target={FormatVector(slot.Target)}; distance={distance:0.00}; admissionDistance={VanguardCoverAdmissionDistance:0.00}; safeForAdmission={Bool(safeForAdmission)}; speed={currentSpeed:0.00}; externalIdle={Bool(externalIdle)}; noHardThreat={Bool(noHardThreatForCommit)}; stallEvaluatedAfterAdmission=true; grantSeconds={VanguardCoverGrantTtl.TotalSeconds:0.00}; patientOnly=true; noOpenField=true; next=StationaryMedicalSurgery; tag={CoverArrivalGrantStatusTag}; convergenceTag={SurgeryCoverAdmissionConvergenceStatusTag}; authorityHoldTag={MedicalAuthorityHoldStatusTag}; coverCommitTag={MedicalCoverCommitStatusTag}; commitUnificationTag={MedicalCoverCommitUnificationStatusTag}; movementStabilizationTag={MedicalCoverMovementStabilizationStatusTag}; previousTag={StatusTag}");
                slot.ArrivedLogged = true;
            }

            object? activePath = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "ActivePath");
            bool cancelPath = TryInvoke(activePath, "Cancel", 0.1f);
            bool speedZero = TryInvoke(mover, "SetTargetMoveSpeed", 0f) || TryInvoke(botOwner, "SetTargetMoveSpeed", 0f);
            bool pause = TryInvoke(mover, "PauseMovement", 1.5f) || TryInvoke(mover, "MovementPause", 1.5f, true);
            bool pose = TryInvoke(mover, "SetPose", 0.55f) || TrySetPropertyOrField(mover, "TargetPose", 0.55f);
            slot.GrantUntilUtc = now + VanguardCoverGrantTtl;
            slot.CoverCommitUntilUtc = now + VanguardCoverGrantTtl;
            ResetCommitRetentionTracking(slot);
            LastCoverFailureReasonByBotProfile.Remove(key);
            return new SainLikeCoverSeekResult(
                "vanguard_slot_arrived",
                false,
                false,
                false,
                sainUpdateCover,
                sainPoseSet || pose,
                sainCoverState,
                sainFinderState,
                slot.Source,
                true,
                slot.Source,
                slot.Diagnostic,
                true,
                $"vanguardSlot=arrived;vanguardCoverGrant=true;coverCommit=true;commitIdempotent=false;vanguardSlotDist={distance:0.00};admissionDistance={VanguardCoverAdmissionDistance:0.00};safeForAdmission={Bool(safeForAdmission)};externalIdle={Bool(externalIdle)};noHardThreat={Bool(noHardThreatForCommit)};speedZero={Bool(speedZero)};pause={Bool(pause)};cancelPath={Bool(cancelPath)};noOpenField=true");
        }

        // Stall/reselection is deliberately evaluated only after admission. A physically stopped bot
        // already inside a safe cover envelope must stabilize for surgery, not discard the slot.
        bool shouldReselect = false;
        string reselectReason = "none";
        if (slot.PhysicalStallSamples >= 2 && slot.CommandCount >= 1 && distance > VanguardCoverAdmissionDistance)
        {
            shouldReselect = true;
            reselectReason = "physical_world_stall:samples=" + slot.PhysicalStallSamples.ToString(CultureInfo.InvariantCulture)
                + ":worldDelta=" + worldDelta.ToString("0.00", CultureInfo.InvariantCulture)
                + ":distance=" + distance.ToString("0.00", CultureInfo.InvariantCulture)
                + ":admission=" + VanguardCoverAdmissionDistance.ToString("0.00", CultureInfo.InvariantCulture);
        }
        else if (distance > VanguardCoverSlotHardRejectDistance && slot.CommandCount >= 1)
        {
            shouldReselect = true;
            reselectReason = "distance_gt_hard_reject_after_command:" + distance.ToString("0.00", CultureInfo.InvariantCulture);
        }
        else if (slot.InitialDistance > VanguardCoverSlotPreferredMaxDistance && slot.CommandCount >= 2 && slot.BestDistance > VanguardCoverSlotPreferredMaxDistance)
        {
            shouldReselect = true;
            reselectReason = "too_far_no_convergence:initial=" + slot.InitialDistance.ToString("0.00", CultureInfo.InvariantCulture) + ":best=" + slot.BestDistance.ToString("0.00", CultureInfo.InvariantCulture);
        }
        else if (slot.StagnantCommandCount >= VanguardCoverSlotMaxStagnantCommands && distance > VanguardCoverAdmissionDistance)
        {
            shouldReselect = true;
            reselectReason = "stagnant_commands:" + slot.StagnantCommandCount.ToString(CultureInfo.InvariantCulture) + ":best=" + slot.BestDistance.ToString("0.00", CultureInfo.InvariantCulture);
        }

        if (shouldReselect)
        {
            bool retryAllowed = TryScheduleCoverReselection(key, slot, now, reselectReason, out int reselectCount);
            SetLastCoverFailure(key, reselectReason);
            VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_SURGERY_COVER_SLOT_RESELECT operator={Safe(snapshot.OperatorId)}; botProfile={key}; reason={Safe(reselectReason)}; oldSource={Safe(slot.Source)}; oldTarget={FormatVector(slot.Target)}; distance={distance:0.00}; best={slot.BestDistance:0.00}; admissionDistance={VanguardCoverAdmissionDistance:0.00}; commandCount={slot.CommandCount}; reselectCount={reselectCount}; maxReselects={VanguardCoverSlotMaxReselects}; retryAllowed={Bool(retryAllowed)}; admissionEvaluatedFirst=true; rejectedBeforeSearch=true; patientOnly=true; noOpenField=true; tag={StatusTag}; convergenceTag={SurgeryCoverAdmissionConvergenceStatusTag}; Tag={VanguardMedicalCohesionStatusTags.SurgeryCoverReselection}");
            if (!retryAllowed)
            {
                return new SainLikeCoverSeekResult("vanguard_slot_unavailable", false, false, true, sainUpdateCover, sainPoseSet, sainCoverState, sainFinderState, "none", false, "none", "reselect_budget_exhausted:" + Safe(reselectReason), false, "vanguardSlot=unavailable;movementCommandUnavailable=true;reselectBudgetExhausted=true;typedCoverFailure=" + VanguardCoverMovementFailureKind.ProbeBudgetExhausted + ";noOpenField=true");
            }

            return new SainLikeCoverSeekResult("vanguard_slot_search_pending", false, false, false, sainUpdateCover, sainPoseSet, sainCoverState, sainFinderState, "none", false, "incremental_reselect", "cover_search_pending:reselect=" + Safe(reselectReason), false, "vanguardSlot=search_pending;movementCommandUnavailable=false;incrementalCoverSearch=true;noRecursiveReselect=true;noOpenField=true");
        }

        bool shouldCommand = slot.LastCommandAtUtc == DateTimeOffset.MinValue || now - slot.LastCommandAtUtc >= VanguardCoverRecommandInterval;
        bool commanded = false;
        string commandResult = "not_due";
        if (shouldCommand)
        {
            commanded = TryCommandMoveToSurgerySlot(botOwner, mover, slot.Target, out commandResult);
            slot.LastCommandAtUtc = now;
            slot.CommandCount++;
            bool hasMotionSignal = improved || physicalMoved;
            if (!hasMotionSignal)
            {
                slot.StagnantCommandCount++;
            }
            else
            {
                slot.StagnantCommandCount = 0;
            }

            slot.LastCommandResult = commandResult;
            VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_SURGERY_COVER_SLOT_MOVE operator={Safe(snapshot.OperatorId)}; botProfile={key}; source={Safe(slot.Source)}; target={FormatVector(slot.Target)}; distance={distance:0.00}; best={slot.BestDistance:0.00}; initial={slot.InitialDistance:0.00}; command={Bool(commanded)}; commandResult={Safe(commandResult)}; commandCount={slot.CommandCount}; stagnantCommands={slot.StagnantCommandCount}; improved={Bool(improved)}; worsened={Bool(worsened)}; speed={Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed):0.00}; preferredMover={TypeName(mover)}; eftMover={TypeName(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Mover"))}; eftPreferred={Bool(slot.EftCommandPreferred)}; patientOnly=true; tag={StatusTag}");

            if (IsCommandNoMotionRejection(commanded, commandResult, slot, distance, Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed), improved))
            {
                string rejectionReason = IsNegativeMoveCommandResult(commandResult) ? "cover_path_invalid_or_noway" : "command_no_motion";
                bool retryAllowed = TryScheduleCoverReselection(key, slot, now, rejectionReason + ":" + Safe(commandResult), out int reselectCount);
                SetLastCoverFailure(key, rejectionReason);
                VanguardClientDiagnosticsLog.Info(PathGateStatusTag, $"VANGUARD_SURGERY_COVER_MOVE_REJECTED operator={Safe(snapshot.OperatorId)}; botProfile={key}; source={Safe(slot.Source)}; target={FormatVector(slot.Target)}; distance={distance:0.00}; commandResult={Safe(commandResult)}; stagnantCommands={slot.StagnantCommandCount}; speed={Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed):0.00}; reason={Safe(rejectionReason)}; reselectCount={reselectCount}; maxReselects={VanguardCoverSlotMaxReselects}; retryAllowed={Bool(retryAllowed)}; retryTtl={VanguardRejectedCoverSlotTtl.TotalSeconds:0.00}; rejectedBeforeSearch=true; patientOnly=true; tag={PathGateStatusTag}; Tag={VanguardMedicalCohesionStatusTags.SurgeryCoverReselection}");
                if (retryAllowed)
                {
                    VanguardClientDiagnosticsLog.Info(CoverMultiCandidateStatusTag, $"VANGUARD_SURGERY_COVER_MULTI_CANDIDATE_RETRY operator={Safe(snapshot.OperatorId)}; botProfile={key}; reason={Safe(rejectionReason)}; rejectedTarget={FormatVector(slot.Target)}; reselectCount={reselectCount}; maxReselects={VanguardCoverSlotMaxReselects}; next=try_alternate_cover_same_lease; patientOnly=true; tag={CoverMultiCandidateStatusTag}; Tag={VanguardMedicalCohesionStatusTags.SurgeryCoverReselection}");
                    return new SainLikeCoverSeekResult("vanguard_slot_search_pending", false, false, false, sainUpdateCover, sainPoseSet, sainCoverState, sainFinderState, "none", false, "incremental_reselect", "cover_search_pending:command_reject=" + Safe(rejectionReason), false, "vanguardSlot=search_pending;movementCommandUnavailable=false;incrementalCoverSearch=true;noRecursiveReselect=true;noOpenField=true");
                }

                return new SainLikeCoverSeekResult("vanguard_slot_unavailable", false, false, true, sainUpdateCover, sainPoseSet, sainCoverState, sainFinderState, "none", false, "none", "all_cover_candidates_rejected:" + Safe(rejectionReason), false, "vanguardSlot=unavailable;movementCommandUnavailable=true;allCoverCandidatesRejected=true;typedCoverFailure=" + ClassifyCoverMovementFailure(rejectionReason, commandResult) + ";" + Safe(rejectionReason) + "=true;noOpenField=true");
            }
        }

        bool movementCommandUnavailable = shouldCommand && (!commanded || IsNegativeMoveCommandResult(commandResult)) && (commandResult.Contains("movement_command_unavailable", StringComparison.OrdinalIgnoreCase) || IsNegativeMoveCommandResult(commandResult));
        bool hasRealMotionProgress = improved || physicalMoved;
        bool moving = hasRealMotionProgress && !movementCommandUnavailable;

        string state = moving ? "vanguard_slot_moving" : "vanguard_slot_assigned";
        return new SainLikeCoverSeekResult(state, false, moving, false, sainUpdateCover, sainPoseSet, sainCoverState, sainFinderState, slot.Source, false, slot.Source, slot.Diagnostic, moving, $"vanguardSlot={(moving ? "moving" : "assigned")};vanguardCoverGrant=false;vanguardSlotDist={distance:0.00};vanguardSlotBest={slot.BestDistance:0.00};vanguardSlotInitial={slot.InitialDistance:0.00};vanguardCommand={Bool(commanded)};vanguardCommandResult={Safe(commandResult)};movementCommandUnavailable={Bool(movementCommandUnavailable)};typedCoverFailure={ClassifyCoverMovementFailure(commandResult, commandResult)};vanguardSlotProgress={Bool(moving)};stagnantCommands={slot.StagnantCommandCount};noOpenField=true");
    }

    private static void ObservePrepareProgress(VanguardExecutionLeaseState lease, OperatorDecisionSnapshot snapshot, DateTimeOffset now, string effect)
    {
        string? progress = null;
        bool vanguardGrant = HasRecentVanguardSurgeryCoverGrant(snapshot, out _);
        bool coverHold = effect.Contains("coverSeek=hold_in_cover", StringComparison.OrdinalIgnoreCase)
            || effect.Contains("coverSeek=vanguard_slot_arrived", StringComparison.OrdinalIgnoreCase)
            || effect.Contains("vanguardCoverGrant=true", StringComparison.OrdinalIgnoreCase)
            || effect.Contains("coverCommit=true", StringComparison.OrdinalIgnoreCase)
            || vanguardGrant
            || (snapshot.Medical.Safety.SurgeryAreaClear && snapshot.Medical.Safety.CoveredOrHoldingAngle);
        bool coverMoving = effect.Contains("coverSeek=move_to_cover", StringComparison.OrdinalIgnoreCase)
            || effect.Contains("coverSeek=vanguard_slot_moving", StringComparison.OrdinalIgnoreCase)
            || effect.Contains("vanguardSlot=moving", StringComparison.OrdinalIgnoreCase)
            || effect.Contains("sainCoverMovingTo=true", StringComparison.OrdinalIgnoreCase);
        bool coverRequested = effect.Contains("coverSeek=request_cover", StringComparison.OrdinalIgnoreCase)
            || effect.Contains("coverSeek=vanguard_slot_assigned", StringComparison.OrdinalIgnoreCase);

        if (!lease.PrepareProgressObserved && !IsLootOrOrbitActive(snapshot))
        {
            lease.PrepareProgressObserved = true;
            progress = "prepare_loot_or_orbit_inactive";
        }
        else if (coverHold && !lease.CompletionObserved)
        {
            lease.CompletionObserved = true;
            progress = vanguardGrant || effect.Contains("vanguardCoverGrant=true", StringComparison.OrdinalIgnoreCase) || effect.Contains("coverCommit=true", StringComparison.OrdinalIgnoreCase) ? "vanguard_surgery_cover_slot_committed" : "sain_like_cover_or_hold_observed";
        }
        else if (coverMoving)
        {
            progress = effect.Contains("vanguardSlot=moving", StringComparison.OrdinalIgnoreCase) ? "vanguard_surgery_cover_slot_moving" : "sain_like_cover_seek_moving";
            VanguardMedicalIsolationController.MarkCoverMovementProgress(lease, now, progress);
            ExtendCoverProgressWindow(lease, now, effect, progress);
        }
        else if (coverRequested && !lease.ThreatObservedDuringLease && now - lease.LastProgressAtUtc >= TimeSpan.FromSeconds(0.75d))
        {
            lease.ThreatObservedDuringLease = true;
            progress = effect.Contains("coverSeek=vanguard_slot_assigned", StringComparison.OrdinalIgnoreCase) ? "vanguard_surgery_cover_slot_assigned" : "sain_like_cover_seek_requested";
        }
        else if (!lease.ThreatObservedDuringLease && !IsPreStartStationarySurgeryBlocked(snapshot, out _))
        {
            lease.ThreatObservedDuringLease = true;
            progress = "stationary_idle_observed";
        }

        if (progress == null)
        {
            return;
        }

        lease.LastProgressAtUtc = now;
        lease.LastProgressKind = progress;
        lease.NoProgressUntilUtc = now + NoProgressTimeout;
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_EXECUTION_PROGRESS {lease.Summary}; progress={progress}; {effect}; blockerNow={CurrentPrepareBlocker(snapshot)}; tag={StatusTag}");
    }


    private static bool TryGetReadyReasonFromPreparationEffect(string effect, OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (string.IsNullOrWhiteSpace(effect))
        {
            return false;
        }

        bool effectGrant = effect.Contains("vanguardCoverGrant=true", StringComparison.OrdinalIgnoreCase)
            || effect.Contains("coverCommit=true", StringComparison.OrdinalIgnoreCase)
            || effect.Contains("coverSeek=vanguard_slot_arrived", StringComparison.OrdinalIgnoreCase);
        if (!effectGrant)
        {
            return false;
        }

        if (HasHardThreatInterrupt(snapshot, out var hardThreat))
        {
            reason = "effect_ready_blocked_by_hard_threat:" + Safe(hardThreat);
            return false;
        }

        if (IsPreStartStationarySurgeryBlocked(snapshot, out var preStartBlock)
            && !effect.Contains("speedZero=true", StringComparison.OrdinalIgnoreCase)
            && !effect.Contains("pause=true", StringComparison.OrdinalIgnoreCase))
        {
            reason = "effect_ready_waiting_stationary:" + Safe(preStartBlock);
            return false;
        }

        reason = "effect_cover_committed:" + ExtractEffectField(effect, "vanguardSlotDist", "unknown")
            + ":" + ExtractEffectField(effect, "vanguardCoverSource", "vanguard_slot");
        VanguardClientDiagnosticsLog.Info(MedicalCoverCommitUnificationStatusTag, $"VANGUARD_MEDICAL_COVER_COMMIT_FROM_EFFECT operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; effect={Safe(effect)}; next=StationaryMedicalSurgery; patientOnly=true; noLocalHold=true; noStationaryFallback=true; tag={MedicalCoverCommitUnificationStatusTag}; coverArrivalTag={CoverArrivalGrantStatusTag}; coverCommitTag={MedicalCoverCommitStatusTag}");
        return true;
    }

    private static bool TryPromoteCurrentCoverSlotToCommit(VanguardExecutionLeaseState lease, BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason, out string commitReason)
    {
        commitReason = "none";
        if (botOwner == null || lease == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            commitReason = "missing_bot_or_lease";
            return false;
        }

        string key = Safe(snapshot.BotProfileId);
        if (!CoverSlotsByBotProfile.TryGetValue(key, out var slot))
        {
            commitReason = "no_vanguard_cover_slot";
            return false;
        }

        if (slot.Source.IndexOf("local", StringComparison.OrdinalIgnoreCase) >= 0
            || slot.Source.IndexOf("fallback", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            commitReason = "go_cover_only_rejects_local_or_fallback_slot:" + Safe(slot.Source);
            return false;
        }

        if (HasHardThreatInterrupt(snapshot, out var hardThreat))
        {
            commitReason = "hard_threat:" + Safe(hardThreat);
            return false;
        }

        if (IsLootOrOrbitActive(snapshot))
        {
            commitReason = "external_active";
            return false;
        }

        float distance = Distance2D(snapshot.Position, slot.Target);
        float speed = Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed);
        bool safeEnough = snapshot.Medical.Safety.SurgeryAreaClear || snapshot.Medical.Safety.CoveredOrHoldingAngle;
        bool closeEnough = distance <= VanguardCoverAdmissionDistance;
        string retentionReason = "retention_not_evaluated";
        bool retainedCommit = slot.CoverCommitUntilUtc > now
            && safeEnough
            && IsCommittedSlotRetentionValid(slot, distance, now, out _, out retentionReason);

        // Idempotence is evaluated only after current safety and physical truth. A committed slot
        // survives harmless drift inside the wider retention envelope, but never survives a hard threat.
        if (retainedCommit
            && (string.Equals(lease.MedicalIsolationPhase, "ReadyForMedicalAction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lease.MedicalIsolationPhase, "StabilizingPosture", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lease.MedicalIsolationPhase, "ExecutingMedicalAction", StringComparison.OrdinalIgnoreCase)))
        {
            commitReason = "existing_vanguard_cover_commit:" + Safe(slot.Source)
                + ":distance=" + distance.ToString("0.00", CultureInfo.InvariantCulture)
                + ":admission=" + VanguardCoverAdmissionDistance.ToString("0.00", CultureInfo.InvariantCulture)
                + ":retention=" + VanguardCoverCommitRetentionDistance.ToString("0.00", CultureInfo.InvariantCulture)
                + ":retentionReason=" + Safe(retentionReason);
            return true;
        }

        bool controlledEnough = speed <= 1.10f;
        if (!safeEnough || !closeEnough || !controlledEnough)
        {
            commitReason = "commit_not_ready:safe=" + Bool(safeEnough)
                + ":close=" + Bool(closeEnough)
                + ":controlled=" + Bool(controlledEnough)
                + ":distance=" + distance.ToString("0.00", CultureInfo.InvariantCulture)
                + ":best=" + slot.BestDistance.ToString("0.00", CultureInfo.InvariantCulture)
                + ":speed=" + speed.ToString("0.00", CultureInfo.InvariantCulture)
                + ":admission=" + VanguardCoverAdmissionDistance.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        object? mover = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Mover");
        object? activePath = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "ActivePath");
        bool cancelPath = TryInvoke(activePath, "Cancel", 0.1f);
        bool speedZero = TryInvoke(mover, "SetTargetMoveSpeed", 0f) || TryInvoke(botOwner, "SetTargetMoveSpeed", 0f);
        bool pause = TryInvoke(mover, "PauseMovement", 1.75f) || TryInvoke(mover, "MovementPause", 1.75f, true);
        bool pose = TryInvoke(mover, "SetPose", 0.55f) || TrySetPropertyOrField(mover, "TargetPose", 0.55f);

        slot.GrantUntilUtc = now + VanguardCoverGrantTtl;
        slot.CoverCommitUntilUtc = now + VanguardCoverGrantTtl;
        ResetCommitRetentionTracking(slot);
        slot.ArrivedLogged = true;
        LastCoverFailureReasonByBotProfile.Remove(key);
        commitReason = "unified_vanguard_cover_commit:" + Safe(reason)
            + ":source=" + Safe(slot.Source)
            + ":distance=" + distance.ToString("0.00", CultureInfo.InvariantCulture)
            + ":best=" + slot.BestDistance.ToString("0.00", CultureInfo.InvariantCulture)
            + ":speed=" + speed.ToString("0.00", CultureInfo.InvariantCulture);
        VanguardClientDiagnosticsLog.Info(MedicalCoverCommitUnificationStatusTag, $"VANGUARD_MEDICAL_COVER_COMMITTED_FROM_READY operator={Safe(snapshot.OperatorId)}; botProfile={key}; lease={Safe(lease.LeaseId)}; reason={Safe(reason)}; source={Safe(slot.Source)}; target={FormatVector(slot.Target)}; distance={distance:0.00}; admissionDistance={VanguardCoverAdmissionDistance:0.00}; best={slot.BestDistance:0.00}; speed={speed:0.00}; safeEnough={Bool(safeEnough)}; closeEnough={Bool(closeEnough)}; controlledEnough={Bool(controlledEnough)}; cancelPath={Bool(cancelPath)}; speedZero={Bool(speedZero)}; pause={Bool(pause)}; pose={Bool(pose)}; grantSeconds={VanguardCoverGrantTtl.TotalSeconds:0.00}; patientOnly=true; noOpenField=true; noLocalHold=true; noStationaryFallback=true; next=StationaryMedicalSurgery; tag={MedicalCoverCommitUnificationStatusTag}; movementStabilizationTag={MedicalCoverMovementStabilizationStatusTag}; coverCommitTag={MedicalCoverCommitStatusTag}");
        return true;
    }



    private static bool TryForceHardProcedureCurrentPositionCommit(VanguardExecutionLeaseState lease, BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason, out string commitReason)
    {
        commitReason = "none";
        if (botOwner == null || lease == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            commitReason = "missing_bot_or_lease";
            return false;
        }

        if (string.Equals(lease.MedicalIsolationPhase, "ReadyForMedicalAction", StringComparison.OrdinalIgnoreCase)
            || string.Equals(lease.MedicalIsolationPhase, "StabilizingPosture", StringComparison.OrdinalIgnoreCase)
            || string.Equals(lease.MedicalIsolationPhase, "ExecutingMedicalAction", StringComparison.OrdinalIgnoreCase))
        {
            commitReason = "existing_hard_procedure_current_position_commit:" + Safe(lease.MedicalIsolationPhase);
            return true;
        }

        if (!IsHardProcedureStillViable(snapshot))
        {
            commitReason = "hard_procedure_not_viable:" + CurrentPrepareBlocker(snapshot);
            return false;
        }

        float speed = Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed);
        bool stationaryEnough = speed <= 1.10f;
        bool safeCoveredPosition = snapshot.Medical.Safety.SurgeryAreaClear
            && (snapshot.Medical.Safety.SafeForStationarySurgery
                || snapshot.Medical.Safety.CoveredOrHoldingAngle
                || snapshot.Medical.Safety.CoveredSuppressionOpportunity);
        if (!stationaryEnough || !safeCoveredPosition)
        {
            commitReason = "current_position_not_committable:speed=" + speed.ToString("0.00", CultureInfo.InvariantCulture)
                + ":safeCovered=" + Bool(safeCoveredPosition)
                + ":areaClear=" + Bool(snapshot.Medical.Safety.SurgeryAreaClear)
                + ":coverOrHold=" + Bool(snapshot.Medical.Safety.CoveredOrHoldingAngle)
                + ":stationarySafe=" + Bool(snapshot.Medical.Safety.SafeForStationarySurgery);
            return false;
        }

        var hardLock = VanguardExternalAuthorityAdapter.RefreshHardMedicalProcedureAuthority(botOwner, snapshot, "current_position_commit:" + Safe(reason), now);
        if (hardLock.IsCombatDefer)
        {
            commitReason = "combat_owner_defer:" + hardLock.Summary;
            return false;
        }

        object? mover = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Mover");
        object? activePath = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "ActivePath");
        bool cancelPath = TryInvoke(activePath, "Cancel", 0.1f);
        bool speedZero = TryInvoke(mover, "SetTargetMoveSpeed", 0f) || TryInvoke(botOwner, "SetTargetMoveSpeed", 0f);
        bool pause = TryInvoke(mover, "PauseMovement", 2.25f) || TryInvoke(mover, "MovementPause", 2.25f, true);
        bool pose = TryInvoke(mover, "SetPose", 0.55f) || TrySetPropertyOrField(mover, "TargetPose", 0.55f);
        string key = Safe(snapshot.BotProfileId);
        LastCoverFailureReasonByBotProfile.Remove(key);
        commitReason = "hard_procedure_current_covered_position:" + Safe(reason)
            + ":speed=" + speed.ToString("0.00", CultureInfo.InvariantCulture)
            + ":area=" + Safe(snapshot.Medical.Safety.SurgeryAreaClearReason);
        VanguardClientDiagnosticsLog.Info(MedicalHardProcedureAuthorityStatusTag, $"VANGUARD_MEDICAL_HARD_PROCEDURE_CURRENT_POSITION_COMMIT operator={Safe(snapshot.OperatorId)}; botProfile={key}; lease={Safe(lease.LeaseId)}; reason={Safe(reason)}; speed={speed:0.00}; areaClear={Bool(snapshot.Medical.Safety.SurgeryAreaClear)}; stationarySafe={Bool(snapshot.Medical.Safety.SafeForStationarySurgery)}; coverOrHold={Bool(snapshot.Medical.Safety.CoveredOrHoldingAngle)}; suppression={hardLock.Summary}; cancelPath={Bool(cancelPath)}; speedZero={Bool(speedZero)}; pause={Bool(pause)}; pose={Bool(pose)}; releaseCondition=target_resolved_or_true_threat_or_retry_cap_no_effect_or_max_window; noOpenField=true; noLocalHoldStrict=true; patientOnly=true; next=StationaryMedicalSurgery; tag={MedicalHardProcedureAuthorityStatusTag}; completionGateTag={MedicalProcedureCompletionGateStatusTag}; directChainTag={MedicalSurgeryDirectChainStatusTag}; sameProcedureStartTag={MedicalSurgerySameProcedureStartStatusTag}; coverCommitTag={MedicalCoverCommitStatusTag}");
        return true;
    }

    private static bool IsHardProcedureStillViable(OperatorDecisionSnapshot snapshot)
    {
        if (!VanguardMedicalSurgeryTargetPolicy.IsValidActionableSurgery(snapshot, out _))
        {
            return false;
        }

        if (HasHardThreatInterrupt(snapshot, out _))
        {
            return false;
        }

        return true;
    }


    private static string ExtractEffectField(string effect, string field, string fallback)
    {
        if (string.IsNullOrWhiteSpace(effect) || string.IsNullOrWhiteSpace(field))
        {
            return fallback;
        }

        string prefix = field + "=";
        foreach (string part in effect.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = part.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return Safe(trimmed.Substring(prefix.Length));
            }
        }

        return fallback;
    }


    private static void ExtendCoverProgressWindow(VanguardExecutionLeaseState lease, DateTimeOffset now, string effect, string progress)
    {
        DateTimeOffset hardCap = lease.StartedAtUtc + CoverProgressMaxDuration;
        if (now >= hardCap)
        {
            return;
        }

        DateTimeOffset proposed = now + CoverProgressWindowExtension;
        if (proposed > hardCap)
        {
            proposed = hardCap;
        }

        if (proposed <= lease.MaxUntilUtc)
        {
            return;
        }

        lease.MaxUntilUtc = proposed;
        lease.NoProgressUntilUtc = now + NoProgressTimeout;
        VanguardClientDiagnosticsLog.Info(CoverArrivalGrantStatusTag, $"VANGUARD_MEDICAL_COVER_PROGRESS_WINDOW_EXTENDED {lease.Summary}; progress={Safe(progress)}; newMaxUntil={lease.MaxUntilUtc:O}; hardCap={hardCap:O}; extension={CoverProgressWindowExtension.TotalSeconds:0.00}; {effect}; patientOnly=true; noLocalHold=true; noStationaryFallback=true; tag={CoverArrivalGrantStatusTag}; previousTag={StatusTag}");
    }

    private static bool TryDirectChainToStationarySurgery(VanguardExecutionLeaseState lease, BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason)
    {
        VanguardMedicalIsolationController.MarkCoverReady(lease, botOwner, now, reason);
        ObservePreparedReadySnapshot(lease, now);
        VanguardExternalAuthorityAdapter.RefreshHardMedicalProcedureAuthority(botOwner, snapshot, "direct_chain_before_stationary_surgery:" + Safe(reason), now);
        if (VanguardMobileMedicalLeaseExecutor.TryStartStationarySurgeryFromPrepare(lease, botOwner, snapshot, now, reason, out var chainSummary))
        {
            VanguardClientDiagnosticsLog.Info(MedicalSurgeryDirectChainStatusTag, $"VANGUARD_MEDICAL_SURGERY_DIRECT_CHAIN_STARTED {lease.Summary}; reason={Safe(reason)}; {chainSummary}; prepareLeaseReleased=true; sameProcedure=true; noSchedulerGap=true; releaseCondition=target_resolved_or_true_threat_or_retry_cap_no_effect_or_max_window; tag={MedicalSurgeryDirectChainStatusTag}; sameProcedureStartTag={MedicalSurgerySameProcedureStartStatusTag}; hardProcedureTag={MedicalHardProcedureAuthorityStatusTag}; completionGateTag={MedicalProcedureCompletionGateStatusTag}");
            return true;
        }

        if (chainSummary.IndexOf("stationary_medical_leash", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Complete(lease, now, "Interrupted", chainSummary, true);
            VanguardClientDiagnosticsLog.Info(MedicalSurgeryDirectChainStatusTag,
                $"VANGUARD_SURGERY_DIRECT_CHAIN_RELEASED_FOR_REJOIN {lease.Summary}; reason={Safe(reason)}; {chainSummary}; prepareLeaseReleased=true; terminalHandled=true; next=cohesion_rejoin; tag=VANGUARD_STATIONARY_MEDICAL_LEASH_STATUS; directChainTag={MedicalSurgeryDirectChainStatusTag}");
            return true;
        }

        if (chainSummary.IndexOf("prepareReleased=true", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            VanguardClientDiagnosticsLog.Warning(MedicalSurgeryDirectChainStatusTag, $"VANGUARD_MEDICAL_SURGERY_DIRECT_CHAIN_HANDOFF_FAILED {lease.Summary}; reason={Safe(reason)}; {chainSummary}; prepareLeaseReleased=true; terminalHandled=true; next=scheduler_medical_recheck; tag={MedicalSurgeryDirectChainStatusTag}; outcomeBridgeTag={VanguardMedicalExecutionResultBridge.StatusTag}; hardProcedureTag={MedicalHardProcedureAuthorityStatusTag}");
            // The prepare lease has already reached a truthful terminal state. Stop this tick rather
            // than writing progress to a released lease or attempting a second completion path.
            return true;
        }

        if (HandlePreparedLaunchDeferred(lease, snapshot, now, chainSummary))
        {
            return true;
        }

        LogStationaryMedicalDeferred(snapshot, chainSummary, "direct_chain_wait", now);
        return false;
    }

    private static void ObservePreparedReadySnapshot(VanguardExecutionLeaseState lease, DateTimeOffset now)
    {
        if (lease.SurgeryPrepareReadySinceUtc == DateTimeOffset.MinValue)
        {
            lease.SurgeryPrepareReadySinceUtc = now;
        }

        if (lease.LastSurgeryPrepareReadySnapshotAtUtc == DateTimeOffset.MinValue
            || now - lease.LastSurgeryPrepareReadySnapshotAtUtc >= PreparedReadySnapshotCadence)
        {
            lease.LastSurgeryPrepareReadySnapshotAtUtc = now;
            lease.SurgeryPrepareReadySnapshotCount = Math.Min(32, lease.SurgeryPrepareReadySnapshotCount + 1);
        }
    }

    private static bool HandlePreparedLaunchDeferred(VanguardExecutionLeaseState lease, OperatorDecisionSnapshot snapshot, DateTimeOffset now, string chainSummary)
    {
        if (!IsPreparedProcedurePhase(lease))
        {
            return false;
        }

        string family = PreparedLaunchBlockFamily(chainSummary);
        if (string.Equals(family, "current_combat_threat", StringComparison.OrdinalIgnoreCase))
        {
            Complete(lease, now, "Interrupted", "prepared_launch_current_combat_threat:" + Safe(chainSummary), true);
            return true;
        }

        if (lease.SurgeryPrepareLaunchBlockedSinceUtc == DateTimeOffset.MinValue)
        {
            // One total convergence budget after the procedure is physically ready. A changing
            // native blocker reason must not restart the timer and recreate a 45-65 second crouch.
            lease.SurgeryPrepareLaunchBlockedSinceUtc = lease.SurgeryPrepareReadySinceUtc == DateTimeOffset.MinValue
                ? now
                : lease.SurgeryPrepareReadySinceUtc;
        }

        lease.SurgeryPrepareLaunchBlockReason = family;
        var blockedFor = now - lease.SurgeryPrepareLaunchBlockedSinceUtc;
        if (blockedFor < PreparedLaunchBlockedMaxDuration)
        {
            LogPreparedConvergence(lease, snapshot, now, "prepared_launch_wait", chainSummary);
            return false;
        }

        Complete(lease, now, "Interrupted", "prepared_launch_blocked_bounded:" + family, true);
        VanguardClientDiagnosticsLog.Info(SurgeryPreparationConvergenceStatusTag,
            $"VANGUARD_SURGERY_PREPARE_POSTURE_RELEASED {lease.Summary}; reason={Safe(family)}; raw={Safe(chainSummary)}; blockedFor={blockedFor.TotalSeconds:0.00}; max={PreparedLaunchBlockedMaxDuration.TotalSeconds:0.00}; currentThreat=false; nativeApply=false; coverCommitPhysicalPositionRetained=true; debtRetained=true; retryCooldown=true; retryAfter={PreparedLaunchRetryCooldown.TotalSeconds:0.00}; next=medical_recheck; tag={SurgeryPreparationConvergenceStatusTag}");
        return true;
    }

    private static string PreparedLaunchBlockFamily(string summary)
    {
        string value = summary ?? string.Empty;
        if (value.IndexOf("commit_readiness_pending", StringComparison.OrdinalIgnoreCase) >= 0) return "commit_readiness_pending";
        if (value.IndexOf("can_apply_not_true", StringComparison.OrdinalIgnoreCase) >= 0) return "live_can_apply_pending";
        if (value.IndexOf("selected_item_not_found_or_not_applicable", StringComparison.OrdinalIgnoreCase) >= 0) return "live_item_not_applicable";
        if (value.IndexOf("prepared_surgery_hands_transient", StringComparison.OrdinalIgnoreCase) >= 0) return "hands_transient";
        if (value.IndexOf("surgery_commit_not_ready", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("surgery_commit_changed", StringComparison.OrdinalIgnoreCase) >= 0) return "surgery_controller_commit_pending";
        if (value.IndexOf("isolation_not_ready", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("stationary_hold_not_ready", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("cover_commit_or_live_isolation_missing", StringComparison.OrdinalIgnoreCase) >= 0) return "stationary_isolation_pending";
        if (value.IndexOf("hard_threat_before_direct_surgery", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("sain_combat_primary_protected", StringComparison.OrdinalIgnoreCase) >= 0) return "current_combat_threat";
        if (value.IndexOf("stationary_medical_leash", StringComparison.OrdinalIgnoreCase) >= 0) return "owner_leash_pending";
        if (value.IndexOf("prepare_generation_not_active", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("atomic_lease_replace_rejected", StringComparison.OrdinalIgnoreCase) >= 0) return "atomic_handoff_pending";
        if (value.IndexOf("selection_failed", StringComparison.OrdinalIgnoreCase) >= 0) return "selection_pending";
        return "other_direct_chain_pending";
    }

    private static bool IsPreparedProcedurePhase(VanguardExecutionLeaseState lease)
    {
        return lease.MedicalIsolationAcquired
            && (string.Equals(lease.MedicalIsolationPhase, "ReadyForMedicalAction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lease.MedicalIsolationPhase, "StabilizingPosture", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lease.MedicalIsolationPhase, "ArrivedAtCover", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool CanFinishPreparedSurgeryBeforeRejoin(VanguardExecutionLeaseState lease, OperatorDecisionSnapshot snapshot, out string reason)
    {
        if (!IsPreparedProcedurePhase(lease))
        {
            reason = "prepare_not_committed";
            return false;
        }

        if (!VanguardMedicalSurgeryTargetPolicy.HasPersistentSurgeryCapability(snapshot, out var capabilityReason))
        {
            reason = "persistent_capability_missing:" + Safe(capabilityReason);
            return false;
        }

        if (HasCurrentPreparedSurgeryThreat(lease, snapshot, out var threatReason, out _))
        {
            reason = "current_threat:" + Safe(threatReason);
            return false;
        }

        reason = "prepared_cover_commit_target_and_item_valid_owner_distance_deferred_until_surgery_terminal";
        return true;
    }

    private static bool HasCurrentPreparedSurgeryThreat(VanguardExecutionLeaseState lease, OperatorDecisionSnapshot snapshot, out string reason, out bool softRecentFireHold)
    {
        softRecentFireHold = false;
        var safety = snapshot.Medical.Safety;
        if (safety.EnemyCanShoot || snapshot.Threat.EnemyCanShoot == true || snapshot.ThreatScan.CandidateCanShoot)
        {
            reason = "enemy_can_shoot";
            return true;
        }

        if (safety.ImmediateCombatBlock && (safety.EnemyVisible || safety.ThreatScanWouldPromote || snapshot.Threat.DirectThreat))
        {
            reason = "immediate_combat_block";
            return true;
        }

        if (snapshot.Threat.DirectThreat && safety.EnemyVisible && !safety.CoveredOrHoldingAngle)
        {
            reason = "direct_visible_threat_without_cover";
            return true;
        }

        if (safety.IncomingFireRecent)
        {
            if (IsPreparedProcedurePhase(lease)
                && safety.CoveredOrHoldingAngle
                && !safety.EnemyVisible
                && !safety.EnemyCanShoot
                && !safety.ImmediateCombatBlock)
            {
                softRecentFireHold = true;
                reason = "incoming_fire_recent_without_current_firing_solution";
                return false;
            }

            reason = "incoming_fire_recent";
            return true;
        }

        reason = "none";
        return false;
    }

    private static void LogPreparedConvergence(VanguardExecutionLeaseState lease, OperatorDecisionSnapshot snapshot, DateTimeOffset now, string phase, string reason)
    {
        string key = Safe(snapshot.BotProfileId) + "|surgery-cover-prepare|" + Safe(phase) + "|" + PreparedLaunchBlockFamily(reason);
        if (LastBlockedLogAtByKey.TryGetValue(key, out var last) && now - last < PreparedConvergenceLogInterval)
        {
            return;
        }

        LastBlockedLogAtByKey[key] = now;
        double readyFor = lease.SurgeryPrepareReadySinceUtc == DateTimeOffset.MinValue ? 0d : Math.Max(0d, (now - lease.SurgeryPrepareReadySinceUtc).TotalSeconds);
        double blockedFor = lease.SurgeryPrepareLaunchBlockedSinceUtc == DateTimeOffset.MinValue ? 0d : Math.Max(0d, (now - lease.SurgeryPrepareLaunchBlockedSinceUtc).TotalSeconds);
        VanguardClientDiagnosticsLog.Info(SurgeryPreparationConvergenceStatusTag,
            $"VANGUARD_SURGERY_PREPARE_CONVERGENCE {lease.Summary}; phase={Safe(phase)}; reason={Safe(reason)}; readyFor={readyFor:0.00}; readySnapshots={lease.SurgeryPrepareReadySnapshotCount}; blockedFor={blockedFor:0.00}; ownerDistance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; canApply={Tri(snapshot.Medical.Actionability.CanApplyItem)}; handsReady={Bool(snapshot.Medical.Actionability.HandsReadyForMedicalAction)}; enemyVisible={Bool(snapshot.Medical.Safety.EnemyVisible)}; enemyCanShoot={Bool(snapshot.Medical.Safety.EnemyCanShoot)}; incomingFire={Bool(snapshot.Medical.Safety.IncomingFireRecent)}; immediateBlock={Bool(snapshot.Medical.Safety.ImmediateCombatBlock)}; coverOrHold={Bool(snapshot.Medical.Safety.CoveredOrHoldingAngle)}; currentPostureBounded=true; noGameplayRetuneOutsideSurgeryPrepare=true; tag={SurgeryPreparationConvergenceStatusTag}");
    }

    private static void Complete(VanguardExecutionLeaseState lease, DateTimeOffset now, string outcome, string reason, bool cooldown)
    {
        VanguardMedicalActionOutcomeKind terminalOutcome = string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase)
            ? VanguardMedicalActionOutcomeKind.Completed
            : string.Equals(outcome, "Timeout", StringComparison.OrdinalIgnoreCase)
                ? VanguardMedicalActionOutcomeKind.Timeout
                : string.Equals(outcome, "Interrupted", StringComparison.OrdinalIgnoreCase)
                    ? VanguardMedicalActionOutcomeKind.Interrupted
                    : VanguardMedicalActionOutcomeKind.Failed;
        VanguardMedicalExecutionResultBridge.Publish(
            lease,
            terminalOutcome,
            reason,
            "prepareOutcome=" + Safe(outcome) + ";lastProgress=" + Safe(lease.LastProgressKind),
            now);
        VanguardExecutionLeaseStore.Release(lease.BotProfileId);
        bool keepIsolationForStationaryAction = string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase)
            && reason.StartsWith("stationary_surgery_ready_next_tick", StringComparison.OrdinalIgnoreCase);
        if (!keepIsolationForStationaryAction)
        {
            VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(lease.BotProfileId, out var releaseRecord);
            VanguardMedicalIsolationController.ReleaseForLease(lease, releaseRecord?.BotOwner, now, "prepare_" + outcome + ":" + reason);
        }

        TimeSpan retryDelay = cooldown ? CooldownForPrepareOutcome(outcome, reason) : TimeSpan.Zero;
        if (cooldown)
        {
            var retryAt = now + retryDelay;
            RetryAllowedAtByBotProfile[lease.BotProfileId] = retryAt;
            VanguardExecutionLeaseStore.RegisterOutcomeDetailed(lease.BotProfileId, lease.MedicalNeed, lease.TargetPart, lease.ItemTemplateId, outcome, reason, lease.LastProgressKind, retryAt);
        }
        else
        {
            RetryAllowedAtByBotProfile.Remove(lease.BotProfileId);
            VanguardExecutionLeaseStore.RegisterOutcomeDetailed(lease.BotProfileId, lease.MedicalNeed, lease.TargetPart, lease.ItemTemplateId, outcome, reason, lease.LastProgressKind, now);
        }

        string logKind = outcome switch
        {
            "Completed" => "VANGUARD_EXECUTION_COMPLETED",
            "Timeout" => "VANGUARD_EXECUTION_TIMEOUT",
            "Interrupted" => "VANGUARD_EXECUTION_INTERRUPTED",
            _ => "VANGUARD_EXECUTION_FAILED"
        };

        double elapsed = Math.Max(0d, (now - lease.StartedAtUtc).TotalSeconds);
        string next = keepIsolationForStationaryAction
            ? "StationaryMedicalSurgery"
            : reason.IndexOf("combat", StringComparison.OrdinalIgnoreCase) >= 0
                ? "YieldToSainCombat"
                : "MedicalRecheckOrRetry";
        VanguardClientDiagnosticsLog.Info(StatusTag, $"{logKind} {lease.Summary}; outcome={Safe(outcome)}; reason={Safe(reason)}; elapsed={elapsed:0.00}; retryAfter={retryDelay.TotalSeconds:0.00}; patientOnly=true; next={next}; mode=medical_isolation_vanguard_cover_slot_then_surgery; tag={StatusTag}; combatGateTag={CombatAwareGateStatusTag}; typedFailureTag={TypedCoverFailureStatusTag}");
        if (reason.StartsWith("prepared_launch_blocked_bounded:", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("prepared_soft_threat_hold_expired:", StringComparison.OrdinalIgnoreCase))
        {
            bool stillActive = VanguardExecutionLeaseStore.TryGetActive(lease.BotProfileId, out var activeAfterRelease)
                && string.Equals(activeAfterRelease.LeaseId, lease.LeaseId, StringComparison.OrdinalIgnoreCase);
            VanguardClientDiagnosticsLog.Info(VanguardMobileMedicalLeaseExecutor.SurgeryTerminalItemCommitStatusTag,
                $"VANGUARD_SURGERY_PREPARE_TERMINAL_CONFIRMED {lease.Summary}; outcome={Safe(outcome)}; reason={Safe(reason)}; terminalMode=bounded_release; outcomePublished=true; leaseStillActive={Bool(stillActive)}; isolationReleased={Bool(!keepIsolationForStationaryAction)}; debtRetained=true; retryAfter={retryDelay.TotalSeconds:0.00}; next={next}; tag={VanguardMobileMedicalLeaseExecutor.SurgeryTerminalItemCommitStatusTag}");
        }
    }

    internal static bool ShouldPrepareBeforeStationarySurgery(OperatorDecisionSnapshot snapshot, out string reason)
    {
        return VanguardMedicalSurgeryPreparePolicy.ShouldPrepareBeforeStationarySurgery(snapshot, DateTimeOffset.UtcNow, out reason);
    }

    private static bool ShouldRunPrepareMutation(OperatorDecisionSnapshot snapshot)
    {
        string key = Safe(snapshot.BotProfileId);
        var now = DateTimeOffset.UtcNow;
        if (LastPrepareMutationAtByBotProfile.TryGetValue(key, out var last) && now - last < PrepareMutationInterval)
        {
            return false;
        }

        LastPrepareMutationAtByBotProfile[key] = now;
        return true;
    }

    private static TimeSpan CooldownForPrepareOutcome(string outcome, string reason)
    {
        if (reason.StartsWith("prepared_launch_blocked_bounded:", StringComparison.OrdinalIgnoreCase))
        {
            return PreparedLaunchRetryCooldown;
        }

        if (reason.IndexOf("deferred_by_combat_owner", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("CombatOwnerCannotDriveMovement", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return TimeSpan.FromSeconds(3.00d);
        }

        if (reason.IndexOf("stationary_medical_leash", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return TimeSpan.FromSeconds(3.00d);
        }

        if (reason.IndexOf("movement_command_unavailable", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("typed_cover_failure", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("NoProgress", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return TimeSpan.FromSeconds(8.00d);
        }

        if (reason.IndexOf("grant_expired", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("cover_slot_unavailable", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("reselect_budget", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return TimeSpan.FromSeconds(7.50d);
        }

        if (reason.IndexOf("hard_threat", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("enemy_can_shoot", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("incoming_fire", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return TimeSpan.FromSeconds(10.00d);
        }

        if (reason.IndexOf("actionability", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return TimeSpan.FromSeconds(12.00d);
        }

        return RetryCooldown;
    }

    internal static bool IsReadyForStationarySurgery(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (!IsSurgeryNeed(snapshot.Medical.Need.DominantNeed))
        {
            reason = "need_not_surgery_scope";
            return false;
        }

        if (!HasSurgeryActionability(snapshot, out var actionReason))
        {
            reason = "actionability_blocked:" + actionReason;
            return false;
        }

        var safety = snapshot.Medical.Safety;
        if (!safety.SurgeryAreaClear || !safety.SafeForStationarySurgery)
        {
            reason = "surgery_area_not_clear:" + Safe(safety.SurgeryAreaClearReason);
            return false;
        }

        bool vanguardCoverGranted = HasRecentVanguardSurgeryCoverGrant(snapshot, out var vanguardGrantReason);
        if (!vanguardCoverGranted)
        {
            reason = "await_vanguard_go_cover_slot:" + Safe(vanguardGrantReason);
            return false;
        }

        if (IsPreStartStationarySurgeryBlocked(snapshot, out var preStartReason))
        {
            reason = "await_stationary_idle:" + preStartReason;
            return false;
        }

        reason = "area_clear_vanguard_go_cover_slot_stationary_idle:" + Safe(vanguardGrantReason);
        return true;
    }

    internal static bool IsPreStartStationarySurgeryBlocked(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        float speed = Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed);
        if (speed > 0.35f)
        {
            reason = "movement_speed_gt_0_35";
            return true;
        }

        if (snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true)
        {
            reason = "loot_active_before_surgery";
            return true;
        }

        string orbit = (snapshot.Orbit.Status + "|" + snapshot.Orbit.Category + "|" + snapshot.Orbit.ExtractReason).ToLowerInvariant();
        if (snapshot.Orbit.Active && (orbit.Contains("loot") || orbit.Contains("moving") || orbit.Contains("orbit_moving")))
        {
            reason = "orbit_active_before_surgery";
            return true;
        }

        string state = snapshot.Movement.PlayerState ?? string.Empty;
        if (state.IndexOf("DoorInteraction", StringComparison.OrdinalIgnoreCase) >= 0
            || state.IndexOf("Loot", StringComparison.OrdinalIgnoreCase) >= 0
            || state.IndexOf("Run", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            reason = "player_state_not_stationary_before_surgery";
            return true;
        }

        return false;
    }

    private static bool CanPrepareMissingCoverOrSafeWindow(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        var safety = snapshot.Medical.Safety;
        if (safety.EnemyCanShoot)
        {
            reason = "hard_threat_prepare_denied:enemy_can_shoot";
            return false;
        }

        if (safety.IncomingFireRecent)
        {
            reason = "hard_threat_prepare_denied:incoming_fire_recent";
            return false;
        }

        if (safety.ImmediateCombatBlock)
        {
            reason = "hard_threat_prepare_denied:immediate_combat_block";
            return false;
        }

        if (safety.EnemyVisible && !safety.CoveredOrHoldingAngle)
        {
            reason = "hard_threat_prepare_denied:enemy_visible_without_cover_or_hold";
            return false;
        }

        if (safety.SurgeryThreatPathTooClose || safety.SurgeryThreatDistanceTooClose)
        {
            if (safety.ResidualThreat || safety.StaleThreat || !snapshot.Threat.DirectThreat || safety.CoveredOrHoldingAngle)
            {
                reason = safety.SurgeryThreatPathTooClose
                    ? "residual_path_close_prepare_allowed:" + Safe(safety.SurgeryAreaClearReason)
                    : "residual_distance_close_prepare_allowed:" + Safe(safety.SurgeryAreaClearReason);
                return true;
            }

            reason = safety.SurgeryThreatPathTooClose
                ? "path_close_active_threat_prepare_denied:" + Safe(safety.SurgeryAreaClearReason)
                : "distance_close_active_threat_prepare_denied:" + Safe(safety.SurgeryAreaClearReason);
            return false;
        }

        if (safety.ResidualThreat || safety.StaleThreat)
        {
            reason = "residual_or_stale_safe_window_prepare_allowed:" + Safe(safety.SurgeryAreaClearReason);
            return true;
        }

        if (!safety.CoveredOrHoldingAngle)
        {
            reason = "cover_or_hold_prepare_allowed:" + Safe(safety.SurgeryAreaClearReason);
            return true;
        }

        if (IsPreStartStationarySurgeryBlocked(snapshot, out var preStartReason))
        {
            reason = "stationary_idle_prepare_allowed:" + Safe(preStartReason);
            return true;
        }

        reason = "safe_window_not_preparable:" + Safe(safety.SurgeryAreaClearReason);
        return false;
    }


    private static void LogPrepareBlocked(OperatorDecisionSnapshot snapshot, string reason)
    {
        string key = Safe(snapshot.BotProfileId) + ":" + Safe(reason);
        var now = DateTimeOffset.UtcNow;
        if (LastBlockedLogAtByKey.TryGetValue(key, out var last) && now - last < BlockedLogInterval)
        {
            return;
        }

        LastBlockedLogAtByKey[key] = now;
        var safety = snapshot.Medical.Safety;
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_MEDICAL_PREPARE_SURGERY_COVER_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; need={snapshot.Medical.Need.DominantNeed}; target={SafeTarget(snapshot.Medical.Actionability.TargetPart, snapshot.Medical.Need.TargetPart)}; plan={Safe(snapshot.Medical.Plan.NextStep)}; medSafety={Safe(safety.SurgeryAreaClearReason)}; residual={safety.ResidualThreat}; stale={safety.StaleThreat}; directThreat={snapshot.Threat.DirectThreat}; enemyVisible={safety.EnemyVisible}; enemyCanShoot={safety.EnemyCanShoot}; incomingFire={safety.IncomingFireRecent}; immediateCombatBlock={safety.ImmediateCombatBlock}; pathClose={safety.SurgeryThreatPathTooClose}; distanceClose={safety.SurgeryThreatDistanceTooClose}; coveredOrHolding={safety.CoveredOrHoldingAngle}; patientOnly=true; surgeryLaunchStillStrict=true; tag={StatusTag}");
    }

    private static bool CanGraceActionabilityDuringPrepare(VanguardExecutionLeaseState lease, OperatorDecisionSnapshot snapshot, DateTimeOffset now, string actionReason, out string graceReason)
    {
        graceReason = "none";
        if (!actionReason.Equals("can_apply_not_true", StringComparison.OrdinalIgnoreCase)
            && !actionReason.Equals("medicine_controller_busy", StringComparison.OrdinalIgnoreCase)
            && !actionReason.Equals("hands_reloading_transient", StringComparison.OrdinalIgnoreCase)
            && !actionReason.Equals("hands_grenade_transient", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsSurgeryNeed(snapshot.Medical.Need.DominantNeed)
            || !snapshot.Medical.Actionability.RequiredItemAvailable
            || !snapshot.Medical.Actionability.TargetKnown)
        {
            return false;
        }

        string key = Safe(lease.BotProfileId);
        if (!ActionabilityGraceUntilByBotProfile.TryGetValue(key, out var until) || until <= now)
        {
            until = now + ActionabilityGraceWindow;
            ActionabilityGraceUntilByBotProfile[key] = until;
            VanguardClientDiagnosticsLog.Info(PathGateStatusTag, $"VANGUARD_MEDICAL_ACTIONABILITY_GRACE operator={Safe(snapshot.OperatorId)}; botProfile={key}; reason={Safe(actionReason)}; until={until:O}; target={Safe(lease.TargetPart)}; item={Safe(lease.ItemName)}; requiredItem=true; targetKnown=true; next=recheck_before_fail; tag={PathGateStatusTag}");
        }

        if (now <= until)
        {
            graceReason = "actionability_grace=" + Safe(actionReason) + ";until=" + until.ToString("O", CultureInfo.InvariantCulture);
            return true;
        }

        ActionabilityGraceUntilByBotProfile.Remove(key);
        return false;
    }

    private static bool HasHardThreatInterrupt(OperatorDecisionSnapshot snapshot, out string reason)
    {
        var safety = snapshot.Medical.Safety;
        if (safety.EnemyCanShoot)
        {
            reason = "enemy_can_shoot";
            return true;
        }

        if (safety.IncomingFireRecent)
        {
            reason = "incoming_fire_recent";
            return true;
        }

        if (safety.ImmediateCombatBlock)
        {
            reason = "immediate_combat_block";
            return true;
        }

        if (snapshot.Threat.DirectThreat && safety.EnemyVisible && !safety.CoveredOrHoldingAngle)
        {
            reason = "direct_visible_threat_without_cover";
            return true;
        }

        reason = "none";
        return false;
    }

    private static bool HasSurgeryActionability(OperatorDecisionSnapshot snapshot, out string reason)
    {
        return VanguardMedicalSurgeryTargetPolicy.EvaluateSurgeryCandidate(snapshot, out reason)
            == VanguardSurgeryCandidateState.Ready;
    }


    private static bool HasExternalPathOrOrbitResidue(OperatorDecisionSnapshot snapshot, out string reason)
    {
        float? dist = snapshot.Movement.DistanceToDestination ?? snapshot.Movement.GoToDistance;
        float distValue = dist.GetValueOrDefault(0.00f);
        bool pathFar = snapshot.Movement.HasPath == true && dist.HasValue && distValue > 1.00f;
        if (pathFar)
        {
            reason = "external_path_residue:dist=" + distValue.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        var activity = VanguardExternalAuthorityAdapter.ReadActivity(null, snapshot, DateTimeOffset.UtcNow);
        if ((activity.OrbitSemanticActive || activity.IsOrbitObjectiveResidue) && !activity.OrbitLayerIdleQuiesced)
        {
            reason = "orbit_objective_residue:" + Safe(activity.BlockingReason);
            return true;
        }

        if (activity.LootingBotsHasActiveLootable || activity.LootingBotsActive || activity.LootingBotsTaskRunning)
        {
            reason = "looting_residue:" + Safe(activity.LootingBotsClassification);
            return true;
        }

        reason = "none";
        return false;
    }

    private static void LogLocalHoldBlocked(OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason, string phase)
    {
        string key = Safe(snapshot.BotProfileId) + "|" + Safe(reason) + "|" + Safe(phase);
        if (LastLocalHoldBlockLogAtByKey.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(2.00d))
        {
            return;
        }

        LastLocalHoldBlockLogAtByKey[key] = now;
        VanguardClientDiagnosticsLog.Info(OrbitLocalHoldLockStatusTag, $"VANGUARD_SURGERY_COVER_LOCAL_HOLD_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; phase={Safe(phase)}; reason={Safe(reason)}; brain={Safe(snapshot.Brain.ActiveLayer)}; orbit={Safe(snapshot.Orbit.Classification)}; orbitStatus={Safe(snapshot.Orbit.Status)}; orbitCategory={Safe(snapshot.Orbit.Category)}; path={Tri(snapshot.Movement.HasPath)}; dist={Float(snapshot.Movement.DistanceToDestination)}; goToDist={Float(snapshot.Movement.GoToDistance)}; loot={Safe(snapshot.Looting.Classification)}; surgeryAreaClear={Bool(snapshot.Medical.Safety.SurgeryAreaClear)}; coverOrHold={Bool(snapshot.Medical.Safety.CoveredOrHoldingAngle)}; patientOnly=true; next=wait_for_external_path_clear_or_release; tag={OrbitLocalHoldLockStatusTag}");
    }

    private static bool IsLootOrOrbitActive(OperatorDecisionSnapshot snapshot)
    {
        var activity = VanguardExternalAuthorityAdapter.ReadActivity(null, snapshot, DateTimeOffset.UtcNow);
        return activity.LootingBotsActive
            || activity.LootingBotsTaskRunning
            || activity.LootingBotsHasActiveLootable
            || ((activity.OrbitSemanticActive || activity.IsOrbitObjectiveResidue) && !activity.OrbitLayerIdleQuiesced);
    }

    private static string ExtractCoverFailureReason(string effect, OperatorDecisionSnapshot snapshot)
    {
        string key = Safe(snapshot.BotProfileId);
        var typed = ClassifyCoverMovementFailure(effect, effect);
        if (typed != VanguardCoverMovementFailureKind.Unknown && typed != VanguardCoverMovementFailureKind.None)
        {
            return "typed_cover_failure:" + typed;
        }

        if (LastCoverFailureReasonByBotProfile.TryGetValue(key, out var remembered) && !string.IsNullOrWhiteSpace(remembered))
        {
            return remembered;
        }

        return CurrentPrepareBlocker(snapshot);
    }

    private static void SetLastCoverFailure(string botProfileKey, string reason)
    {
        if (!string.IsNullOrWhiteSpace(botProfileKey))
        {
            LastCoverFailureReasonByBotProfile[Safe(botProfileKey)] = Safe(reason);
        }
    }

    private static string CurrentPrepareBlocker(OperatorDecisionSnapshot snapshot)
    {
        if (HasHardThreatInterrupt(snapshot, out var hardThreat))
        {
            return "hard_threat:" + hardThreat;
        }

        if (!HasSurgeryActionability(snapshot, out var actionReason))
        {
            return "actionability:" + actionReason;
        }

        if (LastCoverFailureReasonByBotProfile.TryGetValue(Safe(snapshot.BotProfileId), out var coverFailure)
            && !string.IsNullOrWhiteSpace(coverFailure))
        {
            if (coverFailure.IndexOf("no_mover_valid_cover", StringComparison.OrdinalIgnoreCase) >= 0
                || coverFailure.IndexOf("all_cover_candidates_rejected", StringComparison.OrdinalIgnoreCase) >= 0
                || coverFailure.IndexOf("cover_path_invalid", StringComparison.OrdinalIgnoreCase) >= 0
                || coverFailure.IndexOf("command_no_motion", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "cover_path_gate:" + Safe(coverFailure);
            }
        }

        if (!snapshot.Medical.Safety.SurgeryAreaClear || !snapshot.Medical.Safety.SafeForStationarySurgery)
        {
            return "safe_window:" + Safe(snapshot.Medical.Safety.SurgeryAreaClearReason);
        }

        if (!snapshot.Medical.Safety.CoveredOrHoldingAngle)
        {
            return HasRecentVanguardSurgeryCoverGrant(snapshot, out var grantReason)
                ? "vanguard_cover_slot_granted:" + Safe(grantReason)
                : "sain_like_or_vanguard_cover_missing:" + Safe(grantReason);
        }

        if (IsPreStartStationarySurgeryBlocked(snapshot, out var preStart))
        {
            return preStart;
        }

        var externalActivity = VanguardExternalAuthorityAdapter.ReadActivity(null, snapshot, DateTimeOffset.UtcNow);
        if (externalActivity.BlocksMedicalPrepare)
        {
            return "external_authority:" + Safe(externalActivity.BlockingReason);
        }

        return "ready";
    }

    private static bool IsSurgeryNeed(VanguardMedicalNeed need)
    {
        return VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(need);
    }

    private static void LogStationaryMedicalDeferred(OperatorDecisionSnapshot snapshot, string reason, string phase, DateTimeOffset now)
    {
        string botKey = Safe(snapshot.BotProfileId);
        string key = botKey + ":" + Safe(phase);
        string normalizedReason = reason.IndexOf("stationary_medical", StringComparison.OrdinalIgnoreCase) >= 0
            ? "stationary_medical_rejoin_required"
            : reason.IndexOf("combat", StringComparison.OrdinalIgnoreCase) >= 0
                ? "combat_authority_active"
                : reason.IndexOf("threat", StringComparison.OrdinalIgnoreCase) >= 0
                    || reason.IndexOf("incoming", StringComparison.OrdinalIgnoreCase) >= 0
                    || reason.IndexOf("enemy", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "direct_threat_active"
                        : Safe(reason);
        bool changed = !LastDeferredTransitionReasonByBotProfile.TryGetValue(key, out var previous)
            || !string.Equals(previous, normalizedReason, StringComparison.OrdinalIgnoreCase);
        bool intervalElapsed = !LastDeferredTransitionLogAtByBotProfile.TryGetValue(key, out var last)
            || now - last >= DeferredTransitionLogInterval;
        if (!changed && !intervalElapsed)
        {
            SuppressedDeferredTransitionCountByBotProfile.TryGetValue(key, out int suppressed);
            SuppressedDeferredTransitionCountByBotProfile[key] = suppressed + 1;
            return;
        }

        SuppressedDeferredTransitionCountByBotProfile.TryGetValue(key, out int suppressedCount);
        SuppressedDeferredTransitionCountByBotProfile[key] = 0;
        LastDeferredTransitionReasonByBotProfile[key] = normalizedReason;
        LastDeferredTransitionLogAtByBotProfile[key] = now;
        VanguardClientDiagnosticsLog.Info(PerformanceChurnGuardStatusTag,
            $"VANGUARD_STATIONARY_MEDICAL_DEFERRED_TRANSITION operator={Safe(snapshot.OperatorId)}; botProfile={botKey}; phase={Safe(phase)}; reason={Safe(normalizedReason)}; rawReason={Safe(reason)}; ownerDistance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; suppressedRepeats={suppressedCount}; mutationMedical=false; mutationMovement=false; debtRetained=true; next=cohesion_or_combat_then_recheck; tag={PerformanceChurnGuardStatusTag}; leashTag=VANGUARD_STATIONARY_MEDICAL_LEASH_STATUS");
    }

    private static void LogSkip(OperatorDecisionSnapshot snapshot, VanguardIntentDryRunBoard board, string reason, DateTimeOffset now)
    {
        string key = Safe(snapshot.BotProfileId) + ":skip:" + SkipReasonFamily(reason);
        if (LastBlockedLogAtByKey.TryGetValue(key, out var last) && now - last < BlockedLogInterval)
        {
            return;
        }

        LastBlockedLogAtByKey[key] = now;
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_MEDICAL_PREPARE_SURGERY_COVER_SKIP operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; selectedIntent={board.Selected.IntentKey}; reason={Safe(reason)}; at={now:O}; need={snapshot.Medical.Need.DominantNeed}; plan={Safe(snapshot.Medical.Plan.NextStep)}; blocker={CurrentPrepareBlocker(snapshot)}; patientOnly=true; throttled=true; tag={StatusTag}");
    }

    private static string SkipReasonFamily(string reason)
    {
        if (reason.IndexOf("stationary_medical_leash", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "stationary_medical_leash";
        }
        if (reason.IndexOf("combat", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "combat_authority";
        }
        if (reason.IndexOf("threat", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("enemy", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("incoming", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "direct_threat";
        }
        if (reason.IndexOf("cooldown", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "retry_cooldown";
        }
        if (reason.IndexOf("actionability", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("target", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "actionability_or_target";
        }
        return Safe(reason);
    }

    internal static bool HasRecentVanguardSurgeryCoverGrant(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "no_vanguard_cover_slot";
        if (string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            reason = "bot_profile_missing";
            return false;
        }

        if (!CoverSlotsByBotProfile.TryGetValue(snapshot.BotProfileId, out var slot))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (slot.GrantUntilUtc < now && slot.CoverCommitUntilUtc < now)
        {
            reason = "grant_expired";
            return false;
        }

        if (slot.Source.IndexOf("local", StringComparison.OrdinalIgnoreCase) >= 0
            || slot.Source.IndexOf("fallback", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            reason = "go_cover_only_rejects_local_or_fallback_slot:" + Safe(slot.Source);
            return false;
        }

        if (IsLootOrOrbitActive(snapshot))
        {
            InvalidateCommittedSlot(slot);
            reason = "grant_invalidated_loot_or_orbit_active";
            return false;
        }

        if (HasHardThreatInterrupt(snapshot, out var hardThreatReason))
        {
            InvalidateCommittedSlot(slot);
            reason = "grant_invalidated_hard_threat:" + Safe(hardThreatReason);
            return false;
        }

        bool safeForRetention = snapshot.Medical.Safety.CoveredOrHoldingAngle || snapshot.Medical.Safety.SurgeryAreaClear;
        if (!safeForRetention)
        {
            InvalidateCommittedSlot(slot);
            reason = "grant_invalidated_cover_safety_lost";
            return false;
        }

        float distance = Distance2D(snapshot.Position, slot.Target);
        if (!IsCommittedSlotRetentionValid(slot, distance, now, out var retentionPending, out var retentionReason))
        {
            reason = "grant_outside_retention:" + distance.ToString("0.00", CultureInfo.InvariantCulture)
                + ":admission=" + VanguardCoverAdmissionDistance.ToString("0.00", CultureInfo.InvariantCulture)
                + ":retention=" + VanguardCoverCommitRetentionDistance.ToString("0.00", CultureInfo.InvariantCulture)
                + ":best=" + slot.BestDistance.ToString("0.00", CultureInfo.InvariantCulture)
                + ":state=" + Safe(retentionReason);
            return false;
        }

        if (Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed) > 0.95f)
        {
            reason = "speed_too_high";
            return false;
        }

        if (!VanguardMedicalSurgeryTargetPolicy.TryResolveTarget(snapshot, out var surgeryTarget))
        {
            reason = "cover_grant_target_unknown";
            return false;
        }

        if (!VanguardMedicalIsolationController.HasCompatibleStationaryIsolation(
                snapshot.BotProfileId,
                surgeryTarget,
                snapshot.Medical.Actionability.SelectedItemTemplateId,
                now,
                out var isolationReason))
        {
            reason = "stale_cover_grant_without_stationary_isolation:" + Safe(isolationReason);
            return false;
        }

        reason = "vanguard_cover_commit:" + Safe(slot.Source) + ":" + Safe(slot.Diagnostic)
            + ":best=" + slot.BestDistance.ToString("0.00", CultureInfo.InvariantCulture)
            + ":retentionPending=" + Bool(retentionPending)
            + ":retention=" + Safe(retentionReason)
            + ":isolation=" + Safe(isolationReason);
        return true;
    }

    private static bool IsCommittedSlotRetentionValid(VanguardSurgeryCoverSlotState slot, float distance, DateTimeOffset now, out bool pending, out string reason)
    {
        pending = false;
        if (slot.CoverCommitUntilUtc <= now && slot.GrantUntilUtc <= now)
        {
            reason = "commit_expired";
            return false;
        }

        if (distance <= VanguardCoverCommitRetentionDistance)
        {
            ResetCommitRetentionTracking(slot);
            reason = distance <= VanguardCoverAdmissionDistance ? "inside_admission" : "inside_retention_hysteresis";
            return true;
        }

        if (slot.CommitCorrectionIssued)
        {
            reason = "outside_retention_after_same_slot_correction";
            return false;
        }

        if (slot.CommitOutsideEnvelopeSinceUtc == DateTimeOffset.MinValue)
        {
            slot.CommitOutsideEnvelopeSinceUtc = now;
            slot.CommitOutsideEnvelopeSamples = 1;
            slot.LastCommitOutsideObservationAtUtc = now;
        }
        else if (slot.LastCommitOutsideObservationAtUtc == DateTimeOffset.MinValue
            || now - slot.LastCommitOutsideObservationAtUtc >= VanguardCoverCommitExitSampleCadence)
        {
            slot.LastCommitOutsideObservationAtUtc = now;
            slot.CommitOutsideEnvelopeSamples = Math.Min(16, slot.CommitOutsideEnvelopeSamples + 1);
        }

        TimeSpan outsideFor = now - slot.CommitOutsideEnvelopeSinceUtc;
        pending = slot.CommitOutsideEnvelopeSamples < VanguardCoverCommitExitMinSamples
            || outsideFor < VanguardCoverCommitExitObservationWindow;
        reason = pending
            ? "outside_retention_observation_pending:samples=" + slot.CommitOutsideEnvelopeSamples.ToString(CultureInfo.InvariantCulture)
                + ":seconds=" + outsideFor.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            : "outside_retention_persistent:samples=" + slot.CommitOutsideEnvelopeSamples.ToString(CultureInfo.InvariantCulture)
                + ":seconds=" + outsideFor.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);
        return pending;
    }

    private static void ResetCommitRetentionTracking(VanguardSurgeryCoverSlotState slot)
    {
        slot.CommitOutsideEnvelopeSinceUtc = DateTimeOffset.MinValue;
        slot.LastCommitOutsideObservationAtUtc = DateTimeOffset.MinValue;
        slot.CommitOutsideEnvelopeSamples = 0;
        slot.CommitCorrectionIssued = false;
    }

    private static void InvalidateCommittedSlot(VanguardSurgeryCoverSlotState slot)
    {
        slot.GrantUntilUtc = DateTimeOffset.MinValue;
        slot.CoverCommitUntilUtc = DateTimeOffset.MinValue;
        slot.ArrivedLogged = false;
        ResetCommitRetentionTracking(slot);
    }

    private static bool TryFindVanguardSurgeryCoverSlot(BotOwner botOwner, OperatorDecisionSnapshot snapshot, object? mover, string botProfileKey, DateTimeOffset now, out Vector3 target, out string source, out string diagnostic, out string preflightResult)
    {
        if (CoverProbeFailureByBotProfile.TryGetValue(botProfileKey, out var rememberedFailure))
        {
            if (rememberedFailure.RetryAfterUtc > now)
            {
                target = default;
                source = "none";
                preflightResult = rememberedFailure.LastFailure;
                diagnostic = "cover_probe_memory_cooldown:failure=" + Safe(rememberedFailure.LastFailure)
                    + ":retryIn=" + Math.Max(0.0d, (rememberedFailure.RetryAfterUtc - now).TotalSeconds).ToString("0.00", CultureInfo.InvariantCulture)
                    + ":count=" + rememberedFailure.Count.ToString(CultureInfo.InvariantCulture);
                LogCoverProbeMemory(botProfileKey, snapshot, now, diagnostic);
                return false;
            }

            CoverProbeFailureByBotProfile.Remove(botProfileKey);
        }

        if (!IncrementalCoverSearchByBotProfile.TryGetValue(botProfileKey, out var state)
            || state.ExpiresAtUtc <= now
            || Distance2D(state.Origin, snapshot.Position) > 8.0f)
        {
            DisposeIncrementalSearchState(state);
            state = new IncrementalCoverSearchState
            {
                Origin = snapshot.Position,
                CreatedAtUtc = now,
                ExpiresAtUtc = now + IncrementalCoverSearchTtl,
                Phase = IncrementalCoverBuildPhase.WallSample
            };
            IncrementalCoverSearchByBotProfile[botProfileKey] = state;
        }

        if (!state.CandidatesReady)
        {
            if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner("MedicalCoverCandidateBuild", snapshot.OwnerProfileId, 1, GlobalCoverCandidateBuildsPerFrame, out var buildBudgetReason))
            {
                target = default;
                source = "none";
                diagnostic = "cover_search_pending:" + buildBudgetReason;
                preflightResult = "incremental_pending_frame_budget";
                return false;
            }

            long candidateBuildStarted = VanguardRuntimePerformanceGuard.Begin();
            bool ready;
            string buildStep;
            try
            {
                ready = AdvanceIncrementalCandidateBuild(snapshot, state, out buildStep);
            }
            finally
            {
                VanguardRuntimePerformanceGuard.End("MedicalCoverCandidateBuild", candidateBuildStarted);
            }

            if (!ready)
            {
                target = default;
                source = "none";
                diagnostic = "cover_search_pending:phase=" + state.Phase + ":step=" + Safe(buildStep)
                    + ":source=local_navmesh_raycast_only"
                    + ":wallAttempts=" + state.WallAttemptIndex.ToString(CultureInfo.InvariantCulture)
                    + ":wallAccepted=" + state.WallAccepted.ToString(CultureInfo.InvariantCulture)
                    + ":strictIncremental=true";
                preflightResult = "incremental_candidate_build_pending";
                return false;
            }
        }

        int candidatesVisitedThisTick = 0;
        int probesThisTick = 0;
        bool frameProbeBudgetExhausted = false;
        string lastRejected = state.LastRejected;
        while (!frameProbeBudgetExhausted && state.CandidateIndex < state.Candidates.Count
            && candidatesVisitedThisTick < IncrementalCoverCandidatesPerTick
            && probesThisTick < IncrementalCoverMoveProbesPerTick)
        {
            var candidate = state.Candidates[state.CandidateIndex];
            if (!state.AnchorBuildComplete)
            {
                if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner("MedicalCoverAnchorBuild", snapshot.OwnerProfileId, 1, GlobalCoverAnchorBuildsPerFrame, out var anchorBudgetReason))
                {
                    state.LastRejected = anchorBudgetReason;
                    break;
                }

                if (state.AnchorWants.Count == 0)
                {
                    state.AnchorWants = BuildCoverAnchorWants(snapshot.Position, candidate.Target);
                    state.AnchorWantIndex = 0;
                    state.AnchorEmittedKeys.Clear();
                }

                bool anchorBuildFinished;
                string anchorBuildStep;
                long anchorBuildStarted = VanguardRuntimePerformanceGuard.Begin();
                try
                {
                    anchorBuildFinished = AdvanceCoverAnchorBuild(snapshot.Position, candidate.Source, state, out anchorBuildStep);
                }
                finally
                {
                    VanguardRuntimePerformanceGuard.End("MedicalCoverAnchorBuild", anchorBuildStarted);
                }

                if (anchorBuildFinished)
                {
                    state.AnchorBuildComplete = true;
                    state.AnchorIndex = 0;
                    state.RawCandidatesVisited++;
                    candidatesVisitedThisTick++;
                    state.LastRejected = "anchor_build_complete_pending_probe:" + Safe(anchorBuildStep);
                }
                else
                {
                    state.LastRejected = "anchor_build_incremental_pending:" + Safe(anchorBuildStep);
                }

                // Exactly one anchor NavMesh sample is allowed per Update. Even a rejected sample
                // returns control here; the next tick advances the explicit wanted-position index.
                break;
            }

            if (state.Anchors.Count == 0)
            {
                state.LastRejected = "no_anchor_projection";
                state.RawCandidatesRejected++;
                state.CandidateIndex++;
                state.AnchorIndex = 0;
                ResetAnchorBuildState(state);
                continue;
            }

            while (state.AnchorIndex < state.Anchors.Count && probesThisTick < IncrementalCoverMoveProbesPerTick)
            {
                var probe = state.Anchors[state.AnchorIndex];
                if (IsRejectedCoverTarget(botProfileKey, probe.Target, now, out var rejectedReason))
                {
                    state.AnchorIndex++;
                    state.AnchorRejectedRecent++;
                    state.LastRejected = "recent_anchor:" + Safe(rejectedReason);
                    continue;
                }

                if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner("MedicalCoverMoveProbe", snapshot.OwnerProfileId, 1, GlobalCoverMoveProbesPerFrame, out var probeBudgetReason))
                {
                    state.LastRejected = probeBudgetReason;
                    frameProbeBudgetExhausted = true;
                    break;
                }

                state.AnchorIndex++;
                state.AnchorProbes++;
                probesThisTick++;
                bool moverValid;
                long pathProbeStarted = VanguardRuntimePerformanceGuard.Begin();
                try
                {
                    moverValid = TryPreflightVanguardCoverCandidate(snapshot.Position, probe.Target, out preflightResult);
                }
                finally
                {
                    VanguardRuntimePerformanceGuard.End("MedicalCoverPathProbe", pathProbeStarted);
                }
                LogAnchorProbe(snapshot, botProfileKey, now, candidate, probe, state.RawCandidatesVisited, state.AnchorIndex, state.AnchorProbes, moverValid, preflightResult);
                if (moverValid)
                {
                    target = probe.Target;
                    source = candidate.Source + ":anchor";
                    diagnostic = candidate.Diagnostic
                        + ":anchorKind=" + Safe(probe.Kind)
                        + ":anchorProbes=" + state.AnchorProbes.ToString(CultureInfo.InvariantCulture)
                        + ":rawVisited=" + state.RawCandidatesVisited.ToString(CultureInfo.InvariantCulture)
                        + ":totalCandidates=" + state.Candidates.Count.ToString(CultureInfo.InvariantCulture)
                        + ":source=local_navmesh_raycast_only"
                        + ":wall=" + Safe(state.WallDiagnostic)
                        + ":incremental=true";
                    DisposeIncrementalSearchState(state);
                    IncrementalCoverSearchByBotProfile.Remove(botProfileKey);
                    CoverProbeFailureByBotProfile.Remove(botProfileKey);
                    return true;
                }

                state.AnchorPreflightRejected++;
                state.LastRejected = preflightResult;
                lastRejected = preflightResult;
                RejectCoverTarget(botProfileKey, probe.Target, now, "anchor_preflight_rejected:" + Safe(preflightResult));
            }

            if (state.AnchorIndex >= state.Anchors.Count)
            {
                state.RawCandidatesRejected++;
                state.CandidateIndex++;
                state.AnchorIndex = 0;
                ResetAnchorBuildState(state);
            }
        }

        target = default;
        source = "none";
        preflightResult = string.IsNullOrWhiteSpace(state.LastRejected) ? lastRejected : state.LastRejected;
        if (state.CandidateIndex < state.Candidates.Count)
        {
            diagnostic = "cover_search_pending:candidate=" + state.CandidateIndex.ToString(CultureInfo.InvariantCulture)
                + "/" + state.Candidates.Count.ToString(CultureInfo.InvariantCulture)
                + ":anchorProbes=" + state.AnchorProbes.ToString(CultureInfo.InvariantCulture)
                + ":tickProbes=" + probesThisTick.ToString(CultureInfo.InvariantCulture)
                + ":tickCandidates=" + candidatesVisitedThisTick.ToString(CultureInfo.InvariantCulture)
                + ":globalCandidateBuilds=" + GlobalCoverCandidateBuildsPerFrame.ToString(CultureInfo.InvariantCulture)
                + ":globalAnchorBuilds=" + GlobalCoverAnchorBuildsPerFrame.ToString(CultureInfo.InvariantCulture)
                + ":globalMoveProbes=" + GlobalCoverMoveProbesPerFrame.ToString(CultureInfo.InvariantCulture)
                + ":last=" + Safe(preflightResult);
            preflightResult = "incremental_pending";
            return false;
        }

        DisposeIncrementalSearchState(state);
        IncrementalCoverSearchByBotProfile.Remove(botProfileKey);
        diagnostic = state.Candidates.Count == 0
            ? "no_cover_candidates:source=local_navmesh_raycast_only:wall=" + Safe(state.WallDiagnostic)
            : "incremental_cover_search_exhausted:candidates=" + state.Candidates.Count.ToString(CultureInfo.InvariantCulture)
                + ":rawVisited=" + state.RawCandidatesVisited.ToString(CultureInfo.InvariantCulture)
                + ":rawRejected=" + state.RawCandidatesRejected.ToString(CultureInfo.InvariantCulture)
                + ":anchorProbes=" + state.AnchorProbes.ToString(CultureInfo.InvariantCulture)
                + ":anchorPreflightRejected=" + state.AnchorPreflightRejected.ToString(CultureInfo.InvariantCulture)
                + ":recentRejected=" + state.AnchorRejectedRecent.ToString(CultureInfo.InvariantCulture)
                + ":last=" + Safe(preflightResult);
        SetLastCoverFailure(botProfileKey, diagnostic);
        RememberCoverProbeFailure(botProfileKey, now, diagnostic);
        return false;
    }

    private static bool AdvanceIncrementalCandidateBuild(OperatorDecisionSnapshot snapshot, IncrementalCoverSearchState state, out string step)
    {
        step = "none";
        try
        {
            switch (state.Phase)
            {
                case IncrementalCoverBuildPhase.WallSample:
                {
                    int maxAttempts = IncrementalWallDirections.Length * IncrementalWallDistances.Length;
                    if (state.WallAttemptIndex >= maxAttempts || state.WallAccepted >= IncrementalWallSampleLimit)
                    {
                        state.WallDiagnostic = "wall_recess_candidates:attempts=" + state.WallAttemptIndex.ToString(CultureInfo.InvariantCulture)
                            + ":sampled=" + state.WallSampled.ToString(CultureInfo.InvariantCulture)
                            + ":accepted=" + state.WallAccepted.ToString(CultureInfo.InvariantCulture)
                            + ":strictIncremental=true";
                        state.Phase = IncrementalCoverBuildPhase.Finalize;
                        step = "wall_scan_complete";
                        return false;
                    }

                    int attempt = state.WallAttemptIndex++;
                    int distanceIndex = attempt / IncrementalWallDirections.Length;
                    int directionIndex = attempt % IncrementalWallDirections.Length;
                    Vector3 wanted = state.Origin + IncrementalWallDirections[directionIndex].normalized * IncrementalWallDistances[distanceIndex];
                    if (!TrySampleNavMesh(wanted, 2.25f, out var sampled))
                    {
                        step = "wall_navmesh_rejected";
                        return false;
                    }

                    state.WallSampled++;
                    state.PendingWallSample = sampled;
                    state.PendingWallObstacleCount = 0;
                    state.PendingWallRayIndex = 0;
                    state.Phase = IncrementalCoverBuildPhase.WallRay;
                    step = "wall_sample_ready";
                    return false;
                }
                case IncrementalCoverBuildPhase.WallRay:
                {
                    if (state.PendingWallRayIndex < IncrementalObstacleDirections.Length)
                    {
                        Vector3 origin = state.PendingWallSample + Vector3.up * 0.9f;
                        Vector3 direction = IncrementalObstacleDirections[state.PendingWallRayIndex++].normalized;
                        try
                        {
                            if (Physics.Raycast(origin, direction, 1.35f, ~0, QueryTriggerInteraction.Ignore))
                            {
                                state.PendingWallObstacleCount++;
                            }
                        }
                        catch
                        {
                            state.PendingWallRayIndex = IncrementalObstacleDirections.Length;
                            state.PendingWallObstacleCount = 0;
                        }
                        step = "wall_obstacle_ray_" + state.PendingWallRayIndex.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }

                    if (state.PendingWallObstacleCount > 0)
                    {
                        float realDist = Distance2D(state.Origin, state.PendingWallSample);
                        float score = 22f - Math.Abs(realDist - 4.75f) * 2.5f + state.PendingWallObstacleCount * 8.0f;
                        state.Candidates.Add(new VanguardCoverCandidate(state.PendingWallSample, "wall_recess_navmesh",
                            "wall_rays=" + state.PendingWallObstacleCount.ToString(CultureInfo.InvariantCulture)
                                + ":distance=" + realDist.ToString("0.0", CultureInfo.InvariantCulture)
                                + ":attempt=" + state.WallAttemptIndex.ToString(CultureInfo.InvariantCulture), score));
                        state.WallAccepted++;
                    }
                    state.Phase = IncrementalCoverBuildPhase.WallSample;
                    step = "wall_candidate_evaluated";
                    return false;
                }
                case IncrementalCoverBuildPhase.Finalize:
                {
                    state.Candidates = state.Candidates
                        .GroupBy(c => c.Target.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + c.Target.z.ToString("0.0", CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.OrderByDescending(c => c.Score).First())
                        .OrderByDescending(c => c.Score)
                        .Take(VanguardCoverPreflightMaxCandidates)
                        .ToList();
                    state.CandidatesReady = true;
                    state.Phase = IncrementalCoverBuildPhase.Ready;
                    step = "candidate_build_complete";
                    return true;
                }
                case IncrementalCoverBuildPhase.Ready:
                    step = "ready";
                    return true;
                default:
                    state.Phase = IncrementalCoverBuildPhase.Finalize;
                    step = "phase_recovered";
                    return false;
            }
        }
        catch (Exception exception)
        {
            state.LastRejected = "incremental_candidate_exception:" + exception.GetType().Name;
            state.Phase = IncrementalCoverBuildPhase.Finalize;
            step = state.LastRejected;
            return false;
        }
    }

    private static void DisposeIncrementalSearchState(IncrementalCoverSearchState? state)
    {
        if (state == null) return;
        ResetAnchorBuildState(state);
    }

    private static void RememberCoverProbeFailure(string botProfileKey, DateTimeOffset now, string diagnostic)
    {
        if (string.IsNullOrWhiteSpace(botProfileKey) || string.Equals(botProfileKey, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CoverProbeFailureByBotProfile.TryGetValue(botProfileKey, out var state);
        int count = state == null ? 1 : state.Count + 1;
        var failure = ClassifyCoverMovementFailure(diagnostic, diagnostic);
        TimeSpan ttl = failure == VanguardCoverMovementFailureKind.ProbeBudgetExhausted || failure == VanguardCoverMovementFailureKind.NoMoverValidCover
            ? CoverProbeFailureMemoryTtl
            : TimeSpan.FromSeconds(Math.Max(3.00d, CoverProbeFailureMemoryTtl.TotalSeconds * 0.50d));
        CoverProbeFailureByBotProfile[botProfileKey] = new CoverProbeFailureState
        {
            LastFailure = failure + ":" + Safe(diagnostic),
            LastDiagnostic = diagnostic,
            RetryAfterUtc = now + ttl,
            Count = count
        };
    }

    private static void LogCoverProbeMemory(string botProfileKey, OperatorDecisionSnapshot snapshot, DateTimeOffset now, string diagnostic)
    {
        string key = "probeMemory|" + Safe(botProfileKey);
        if (LastAnchorProbeLogAtByKey.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(1.50d))
        {
            return;
        }

        LastAnchorProbeLogAtByKey[key] = now;
        VanguardClientDiagnosticsLog.Info(TypedCoverFailureStatusTag, $"VANGUARD_SURGERY_COVER_PROBE_MEMORY_COOLDOWN operator={Safe(snapshot.OperatorId)}; botProfile={Safe(botProfileKey)}; diagnostic={Safe(diagnostic)}; patientOnly=true; noOpenField=true; tag={TypedCoverFailureStatusTag}; coverPreflightTag={CoverPreflightStatusTag}");
    }

    private static void LogAnchorProbe(OperatorDecisionSnapshot snapshot, string botProfileKey, DateTimeOffset now, VanguardCoverCandidate candidate, VanguardCoverAnchorCandidate probe, int rawRank, int localIndex, int anchorProbes, bool moverValid, string preflightResult)
    {
        bool important = moverValid || anchorProbes == 1 || anchorProbes >= VanguardCoverPreflightMaxMoveProbes || IsNegativeMoveCommandResult(preflightResult);
        string key = "anchorProbe|" + Safe(botProfileKey) + "|" + Safe(candidate.Source) + "|" + anchorProbes.ToString(CultureInfo.InvariantCulture);
        if (!important && LastAnchorProbeLogAtByKey.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(2.00d))
        {
            return;
        }

        LastAnchorProbeLogAtByKey[key] = now;
        var typed = ClassifyCoverMovementFailure(preflightResult, preflightResult);
        VanguardClientDiagnosticsLog.Info(CoverAnchorPreflightStatusTag, $"VANGUARD_SURGERY_COVER_ANCHOR_PROBE operator={Safe(snapshot.OperatorId)}; botProfile={Safe(botProfileKey)}; rawSource={Safe(candidate.Source)}; rawTarget={FormatVector(candidate.Target)}; anchor={FormatVector(probe.Target)}; anchorKind={Safe(probe.Kind)}; rawDistance={Distance2D(snapshot.Position, candidate.Target):0.00}; anchorDistance={Distance2D(snapshot.Position, probe.Target):0.00}; obstacleRays={probe.ObstacleRays}; rawRank={rawRank}; anchorIndex={localIndex}; globalProbe={anchorProbes}; maxProbes={VanguardCoverPreflightMaxMoveProbes}; moverValid={Bool(moverValid)}; preflight={Safe(preflightResult)}; typedCoverFailure={typed}; patientOnly=true; noOpenField=true; tag={CoverAnchorPreflightStatusTag}; typedFailureTag={TypedCoverFailureStatusTag}");
    }

    private static VanguardCoverMovementFailureKind ClassifyCoverMovementFailure(string text, string commandResult)
    {
        string merged = ((text ?? string.Empty) + "|" + (commandResult ?? string.Empty)).ToLowerInvariant();
        if (merged.Contains("combatownercannotdrivemovement") || merged.Contains("combat_owner_cannot_drive_movement") || merged.Contains("rejectedcombatowner"))
        {
            return VanguardCoverMovementFailureKind.CombatOwnerCannotDriveMovement;
        }

        if (merged.Contains("probe_budget_exhausted") || merged.Contains("reselect_budget_exhausted"))
        {
            return VanguardCoverMovementFailureKind.ProbeBudgetExhausted;
        }

        if (merged.Contains("no_mover_valid_anchor") || merged.Contains("preflightnomovervalidcover") || merged.Contains("no_mover_valid_cover"))
        {
            return VanguardCoverMovementFailureKind.NoMoverValidCover;
        }

        if (merged.Contains("pathinvalid"))
        {
            return VanguardCoverMovementFailureKind.MoverRejectedPathInvalid;
        }

        if (merged.Contains("gotopointnoway") || merged.Contains("noway"))
        {
            return VanguardCoverMovementFailureKind.GoToPointNoWay;
        }

        if (merged.Contains("command_no_motion") || merged.Contains("commandnomotion"))
        {
            return VanguardCoverMovementFailureKind.CommandNoMotion;
        }

        if (merged.Contains("all_cover_candidates_rejected") || merged.Contains("allcovercandidatesrejected"))
        {
            return VanguardCoverMovementFailureKind.AllCoverCandidatesRejected;
        }

        if (merged.Contains("bot_profile_missing"))
        {
            return VanguardCoverMovementFailureKind.BotProfileMissing;
        }

        if (merged.Contains("none") || string.IsNullOrWhiteSpace(merged.Trim('|')))
        {
            return VanguardCoverMovementFailureKind.None;
        }

        return VanguardCoverMovementFailureKind.Unknown;
    }

    private static List<VanguardCoverAnchorWanted> BuildCoverAnchorWants(Vector3 origin, Vector3 rawCoverTarget)
    {
        Vector3 flatToOrigin = Flat(origin - rawCoverTarget);
        if (flatToOrigin.sqrMagnitude < 0.05f)
        {
            flatToOrigin = Vector3.forward;
        }

        Vector3 towardOrigin = flatToOrigin.normalized;
        Vector3 right = new Vector3(-towardOrigin.z, 0f, towardOrigin.x).normalized;
        return new List<VanguardCoverAnchorWanted>
        {
            new(rawCoverTarget, "raw_sampled"),
            new(rawCoverTarget + towardOrigin * 0.75f, "toward_origin_0_75"),
            new(rawCoverTarget + towardOrigin * 1.25f, "toward_origin_1_25"),
            new(rawCoverTarget + towardOrigin * 1.75f, "toward_origin_1_75"),
            new(rawCoverTarget + towardOrigin * 2.25f, "toward_origin_2_25"),
            new(rawCoverTarget + towardOrigin * 1.00f + right * 0.85f, "right_flank_0_85"),
            new(rawCoverTarget + towardOrigin * 1.00f - right * 0.85f, "left_flank_0_85"),
            new(rawCoverTarget + towardOrigin * 1.55f + right * 1.25f, "right_flank_1_25"),
            new(rawCoverTarget + towardOrigin * 1.55f - right * 1.25f, "left_flank_1_25"),
            new(rawCoverTarget - towardOrigin * 0.65f, "behind_raw_0_65"),
            new(rawCoverTarget + right * 1.10f, "raw_right_1_10"),
            new(rawCoverTarget - right * 1.10f, "raw_left_1_10")
        };
    }

    private static bool AdvanceCoverAnchorBuild(Vector3 origin, string source, IncrementalCoverSearchState state, out string step)
    {
        step = "none";
        if (state.AnchorWantIndex >= state.AnchorWants.Count || state.Anchors.Count >= VanguardCoverAnchorMaxPerCandidate)
        {
            step = "anchor_wants_exhausted";
            return true;
        }

        VanguardCoverAnchorWanted wanted = state.AnchorWants[state.AnchorWantIndex++];
        if (!TrySampleNavMesh(wanted.Target, 1.50f, out var sampled))
        {
            step = "anchor_navmesh_rejected:" + wanted.Kind;
            return state.AnchorWantIndex >= state.AnchorWants.Count;
        }

        float distance = Distance2D(origin, sampled);
        if (distance < 2.00f || distance > 10.75f || Math.Abs(sampled.y - origin.y) > 5.75f)
        {
            step = "anchor_outside_bounds:" + wanted.Kind;
            return state.AnchorWantIndex >= state.AnchorWants.Count;
        }

        string key = sampled.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + sampled.z.ToString("0.0", CultureInfo.InvariantCulture);
        if (!state.AnchorEmittedKeys.Add(key))
        {
            step = "anchor_duplicate:" + wanted.Kind;
            return state.AnchorWantIndex >= state.AnchorWants.Count;
        }

        // The runtime candidates originate only from an EFT AI cover point or a wall-recess ray result.
        // Their cover evidence has already been established in the candidate stage; do not run an
        // additional multi-ray obstacle scan while projecting anchor variants.
        bool coverBacked = source.IndexOf("ai_cover", StringComparison.OrdinalIgnoreCase) >= 0
            || source.IndexOf("wall", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!coverBacked)
        {
            step = "anchor_source_not_cover_backed:" + Safe(source);
            return state.AnchorWantIndex >= state.AnchorWants.Count;
        }

        state.Anchors.Add(new VanguardCoverAnchorCandidate(sampled, wanted.Kind, obstacleRays: 1));
        step = "anchor_added:" + wanted.Kind;
        return state.AnchorWantIndex >= state.AnchorWants.Count || state.Anchors.Count >= VanguardCoverAnchorMaxPerCandidate;
    }

    private static void ResetAnchorBuildState(IncrementalCoverSearchState state)
    {
        state.AnchorBuildComplete = false;
        state.AnchorWants = new List<VanguardCoverAnchorWanted>();
        state.AnchorWantIndex = 0;
        state.AnchorEmittedKeys.Clear();
        state.Anchors = new List<VanguardCoverAnchorCandidate>();
        state.AnchorIndex = 0;
    }

    private static bool TryPreflightVanguardCoverCandidate(Vector3 origin, Vector3 target, out string result)
    {
        try
        {
            var path = new NavMeshPath();
            bool calculated = NavMesh.CalculatePath(origin, target, NavMesh.AllAreas, path);
            Vector3[]? corners = path.corners;
            int cornerCount = corners?.Length ?? 0;
            bool complete = calculated && path.status == NavMeshPathStatus.PathComplete && cornerCount >= 2;
            result = complete
                ? "preflight_navmesh_complete:corners=" + cornerCount.ToString(CultureInfo.InvariantCulture)
                : "preflight_navmesh_rejected:calculated=" + Bool(calculated) + ":status=" + path.status + ":corners=" + cornerCount.ToString(CultureInfo.InvariantCulture);
            return complete;
        }
        catch (Exception exception)
        {
            result = "preflight_navmesh_exception:" + exception.GetType().Name;
            return false;
        }
    }

    private static bool TryCommandMoveToSurgerySlot(BotOwner botOwner, object? preferredMover, Vector3 target, out string result)
    {
        // Runtime invariant: the runtime could command SAIN WalkToPoint, but the bot often
        // did not converge to the surgery cover slot. For Vanguard-owned slots,
        // prefer the EFT BotOwner.Mover GoToPoint bridge first, then BotOwner, and
        // keep SAIN WalkToPoint only as a fallback. The lease now treats accepted
        // commands as diagnostic only; progress requires real movement or distance gain.
        var diagnostics = new List<string>(10);
        object? eftMover = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Mover");
        object? sainBot = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "SAIN.Components.BotComponent");
        object? sainMover = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBot, "Mover");

        TryInvoke(preferredMover, "Sprint", false);
        TryInvoke(sainMover, "Sprint", false);
        TryInvoke(eftMover, "Sprint", false);
        TryInvoke(preferredMover, "SetTargetMoveSpeed", 1.0f);
        TryInvoke(sainMover, "SetTargetMoveSpeed", 1.0f);
        TryInvoke(eftMover, "SetTargetMoveSpeed", 1.0f);

        bool sawCommandApi = false;

        if (TryCommandEftMover(eftMover, target, "eftMover", diagnostics, ref sawCommandApi)
            || TryCommandEftBotOwner(botOwner, target, "botOwner", diagnostics, ref sawCommandApi)
            || TryCommandSainMover(preferredMover, target, "preferred", diagnostics, ref sawCommandApi)
            || TryCommandSainMover(sainMover, target, "sain", diagnostics, ref sawCommandApi))
        {
            result = "movement_commanded:" + string.Join("|", diagnostics);
            return true;
        }

        string prefix = sawCommandApi ? "movement_command_failed" : "movement_command_unavailable";
        result = prefix
            + ":preferredMover=" + TypeName(preferredMover)
            + ":sainMover=" + TypeName(sainMover)
            + ":eftMover=" + TypeName(eftMover)
            + ":diagnostics=" + string.Join("|", diagnostics);
        return false;
    }

    private static bool TryCommandSainMover(object? mover, Vector3 target, string label, List<string> diagnostics, ref bool sawCommandApi)
    {
        if (mover == null)
        {
            diagnostics.Add(label + ":none");
            return false;
        }

        string type = TypeName(mover);
        if (TryInvokeMoveCommand(mover, "WalkToPoint", out var walkLoose, target, false, 0.75f, true))
        {
            sawCommandApi = true;
            diagnostics.Add(label + ":" + type + ":WalkToPoint(false,0.75,true)=" + walkLoose);
            if (IsPositiveMoveResult(walkLoose))
            {
                return true;
            }
        }

        if (TryInvokeMoveCommand(mover, "WalkToPoint", out var walkNoSameWay, target, false, 0.75f, false))
        {
            sawCommandApi = true;
            diagnostics.Add(label + ":" + type + ":WalkToPoint(false,0.75,false)=" + walkNoSameWay);
            if (IsPositiveMoveResult(walkNoSameWay))
            {
                return true;
            }
        }

        diagnostics.Add(label + ":" + type + ":no_sain_walk_command");
        return false;
    }

    private static bool TryCommandEftMover(object? mover, Vector3 target, string label, List<string> diagnostics, ref bool sawCommandApi)
    {
        if (mover == null)
        {
            diagnostics.Add(label + ":none");
            return false;
        }

        string type = TypeName(mover);
        if (TryInvokeMoveCommand(mover, "CurrentStateGoToPoint", out var currentForced, target, false, 0.75f, false, false, false, true))
        {
            sawCommandApi = true;
            diagnostics.Add(label + ":" + type + ":CurrentStateGoToPoint=" + currentForced);
            if (IsPositiveMoveResult(currentForced))
            {
                return true;
            }
        }

        if (TryInvokeMoveCommand(mover, "GoToPoint", out var goForced, target, false, 0.75f, false, false, false, true))
        {
            sawCommandApi = true;
            diagnostics.Add(label + ":" + type + ":GoToPoint7=" + goForced);
            if (IsPositiveMoveResult(goForced))
            {
                return true;
            }
        }

        if (TryInvokeMoveCommand(mover, "GoToPoint", out var goBasic, target, false, 0.75f, false, false))
        {
            sawCommandApi = true;
            diagnostics.Add(label + ":" + type + ":GoToPoint5=" + goBasic);
            if (IsPositiveMoveResult(goBasic))
            {
                return true;
            }
        }

        Vector3? nullableTarget = target;
        if (TryInvokeMoveCommand(mover, "GoToPointNoWay", out var noWay, nullableTarget))
        {
            sawCommandApi = true;
            diagnostics.Add(label + ":" + type + ":GoToPointNoWay=" + noWay);
            if (IsPositiveMoveResult(noWay))
            {
                return true;
            }
        }

        diagnostics.Add(label + ":" + type + ":no_eft_go_command");
        return false;
    }

    private static bool TryCommandEftBotOwner(BotOwner botOwner, Vector3 target, string label, List<string> diagnostics, ref bool sawCommandApi)
    {
        string type = TypeName(botOwner);
        if (TryInvokeMoveCommand(botOwner, "GoToPoint", out var goBasic, target, false, 0.75f, false, false))
        {
            sawCommandApi = true;
            diagnostics.Add(label + ":" + type + ":GoToPoint5=" + goBasic);
            if (IsPositiveMoveResult(goBasic))
            {
                return true;
            }
        }

        if (TryInvokeMoveCommand(botOwner, "GoToPoint", out var goForced, target, false, 0.75f, false, false, false, true))
        {
            sawCommandApi = true;
            diagnostics.Add(label + ":" + type + ":GoToPoint7=" + goForced);
            if (IsPositiveMoveResult(goForced))
            {
                return true;
            }
        }

        diagnostics.Add(label + ":" + type + ":no_botowner_go_command");
        return false;
    }

    private static bool TryInvokeMoveCommand(object? target, string methodName, out string result, params object?[] args)
    {
        result = "method_not_found";
        if (target == null)
        {
            return false;
        }

        try
        {
            var type = target.GetType();
            bool foundAny = false;
            string lastError = "none";
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!string.Equals(method.Name, methodName, StringComparison.Ordinal) || method.GetParameters().Length != args.Length)
                {
                    continue;
                }

                foundAny = true;
                try
                {
                    object? returnValue = method.Invoke(target, args);
                    result = FormatMoveReturn(returnValue);
                    return true;
                }
                catch (Exception ex)
                {
                    lastError = ex.GetType().Name;
                }
            }

            result = foundAny ? "invoke_failed:" + lastError : "method_not_found";
            return foundAny;
        }
        catch (Exception ex)
        {
            result = "reflection_exception:" + ex.GetType().Name;
            return false;
        }
    }

    private static string FormatMoveReturn(object? returnValue)
    {
        if (returnValue == null)
        {
            return "void:true";
        }

        if (returnValue is bool b)
        {
            return "bool:" + Bool(b);
        }

        string text = Safe(returnValue.ToString());
        return returnValue.GetType().Name + ":" + text;
    }

    private static bool IsPositiveMoveResult(string result)
    {
        if (result.Contains("bool:false", StringComparison.OrdinalIgnoreCase)
            || result.Contains("PathInvalid", StringComparison.OrdinalIgnoreCase)
            || result.Contains("NoWay", StringComparison.OrdinalIgnoreCase)
            || result.Contains("GoToPointNoWay", StringComparison.OrdinalIgnoreCase)
            || result.Contains("invoke_failed", StringComparison.OrdinalIgnoreCase)
            || result.Contains("method_not_found", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool TryScheduleCoverReselection(string botProfileKey, VanguardSurgeryCoverSlotState slot, DateTimeOffset now, string reason, out int reselectCount)
    {
        RejectCoverTarget(botProfileKey, slot.Target, now, reason);
        CoverSlotsByBotProfile.Remove(botProfileKey);
        if (IncrementalCoverSearchByBotProfile.TryGetValue(botProfileKey, out var existingSearch))
        {
            DisposeIncrementalSearchState(existingSearch);
            IncrementalCoverSearchByBotProfile.Remove(botProfileKey);
        }

        CoverSlotReselectCountByBotProfile.TryGetValue(botProfileKey, out reselectCount);
        reselectCount++;
        CoverSlotReselectCountByBotProfile[botProfileKey] = reselectCount;
        return reselectCount < VanguardCoverSlotMaxReselects;
    }

    private static bool IsRejectedCoverTarget(string botProfileKey, Vector3 target, DateTimeOffset now, out string reason)
    {
        string key = RejectedCoverSlotKey(botProfileKey, target);
        if (RejectedCoverSlotUntilByKey.TryGetValue(key, out var until) && until > now)
        {
            reason = "rejected_until=" + until.ToString("O", CultureInfo.InvariantCulture);
            return true;
        }

        if (RejectedCoverSlotUntilByKey.ContainsKey(key))
        {
            RejectedCoverSlotUntilByKey.Remove(key);
        }

        reason = "none";
        return false;
    }

    private static void RejectCoverTarget(string botProfileKey, Vector3 target, DateTimeOffset now, string reason)
    {
        string key = RejectedCoverSlotKey(botProfileKey, target);
        RejectedCoverSlotUntilByKey[key] = now + VanguardRejectedCoverSlotTtl;
    }

    private static bool IsCommandNoMotionRejection(bool commanded, string commandResult, VanguardSurgeryCoverSlotState slot, float distance, float speed, bool improved)
    {
        if (!commanded)
        {
            return false;
        }

        if (IsNegativeMoveCommandResult(commandResult))
        {
            return true;
        }

        // Runtime invariant: a successful EFT PathComplete on the command tick is not proof of failure.
        // The runtime shows motion frequently arrives on following ticks, so only reject after
        // a real observation window with no distance gain from the original slot.
        bool observationElapsed = DateTimeOffset.UtcNow - slot.CreatedAtUtc >= VanguardCoverCommandObservationGrace;
        bool noMeaningfulSlotGain = slot.InitialDistance - slot.BestDistance < 0.45f;
        return commandResult.Contains("PathComplete", StringComparison.OrdinalIgnoreCase)
            && observationElapsed
            && noMeaningfulSlotGain
            && slot.CommandCount >= VanguardCoverSlotMaxStagnantCommands
            && distance > VanguardCoverAdmissionDistance
            && speed <= 0.15f
            && !improved
            && slot.StagnantCommandCount >= VanguardCoverSlotMaxStagnantCommands;
    }

    private static bool IsNegativeMoveCommandResult(string commandResult)
    {
        return commandResult.Contains("PathInvalid", StringComparison.OrdinalIgnoreCase)
            || commandResult.Contains("NoWay", StringComparison.OrdinalIgnoreCase)
            || commandResult.Contains("GoToPointNoWay", StringComparison.OrdinalIgnoreCase)
            || commandResult.Contains("bool:false", StringComparison.OrdinalIgnoreCase);
    }

    private static string RejectedCoverSlotKey(string botProfileKey, Vector3 target)
    {
        const float cellMeters = 0.75f;
        float cellX = (float)Math.Round(target.x / cellMeters) * cellMeters;
        float cellZ = (float)Math.Round(target.z / cellMeters) * cellMeters;
        return Safe(botProfileKey) + "|" + cellX.ToString("0.00", CultureInfo.InvariantCulture) + "," + cellZ.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static bool TrySampleNavMesh(Vector3 position, float radius, out Vector3 sampled)
    {
        sampled = position;
        try
        {
            if (NavMesh.SamplePosition(position + Vector3.up * 0.35f, out var hit, radius, NavMesh.AllAreas))
            {
                sampled = hit.position;
                return true;
            }
        }
        catch
        {
            // ignored: some menu/headless bootstrap phases can have no navmesh context yet.
        }

        return false;
    }

    private static int CountNearbyObstacles(Vector3 position)
    {
        int count = 0;
        Vector3 origin = position + Vector3.up * 0.9f;
        Vector3[] dirs =
        {
            Vector3.forward, Vector3.back, Vector3.left, Vector3.right,
            new Vector3(0.707f, 0f, 0.707f), new Vector3(-0.707f, 0f, 0.707f), new Vector3(0.707f, 0f, -0.707f), new Vector3(-0.707f, 0f, -0.707f)
        };

        foreach (Vector3 dir in dirs)
        {
            try
            {
                if (Physics.Raycast(origin, dir.normalized, 1.35f, ~0, QueryTriggerInteraction.Ignore))
                {
                    count++;
                }
            }
            catch
            {
                return 0;
            }
        }

        return count;
    }

    private static Vector3 Flat(Vector3 value) => new(value.x, 0f, value.z);

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    private static string FormatVector(Vector3 value)
    {
        return value.x.ToString("0.00", CultureInfo.InvariantCulture) + "," + value.y.ToString("0.00", CultureInfo.InvariantCulture) + "," + value.z.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static Vector3? Vector(object? value)
    {
        return value is Vector3 vector ? vector : null;
    }

    private static bool? BoolValue(object? value)
    {
        return value is bool b ? b : null;
    }

    private static float? FloatValue(object? value)
    {
        return value switch
        {
            float f => f,
            double d => (float)d,
            int i => i,
            _ => null
        };
    }

    private static bool TryInvoke(object? target, string methodName, params object?[] args)
    {
        if (target == null)
        {
            return false;
        }

        try
        {
            var type = target.GetType();
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!string.Equals(method.Name, methodName, StringComparison.Ordinal) || method.GetParameters().Length != args.Length)
                {
                    continue;
                }

                method.Invoke(target, args);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TrySetPropertyOrField(object? target, string name, object? value)
    {
        if (target == null)
        {
            return false;
        }

        try
        {
            var type = target.GetType();
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, value);
                return true;
            }

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(target, value);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private enum VanguardCoverMovementFailureKind
    {
        None,
        CombatOwnerCannotDriveMovement,
        NoNavmeshAnchor,
        NoMoverValidCover,
        MoverRejectedPathInvalid,
        GoToPointNoWay,
        ProbeBudgetExhausted,
        AllCoverCandidatesRejected,
        CommandNoMotion,
        BotProfileMissing,
        Unknown
    }

    private sealed class CoverProbeFailureState
    {
        public string LastFailure { get; init; } = "none";
        public string LastDiagnostic { get; init; } = "none";
        public DateTimeOffset RetryAfterUtc { get; init; }
        public int Count { get; init; }
    }

    private sealed class SainLikeCoverSeekResult
    {
        public SainLikeCoverSeekResult(string state, bool inCover, bool movingToCover, bool noCover, bool updateCover, bool poseSet, string coverState, string finderState, string coverObject, bool vanguardGrant, string vanguardSource, string vanguardDiagnostic, bool vanguardProgress, string details)
        {
            State = state;
            InCover = inCover;
            MovingToCover = movingToCover;
            NoCover = noCover;
            UpdateCover = updateCover;
            PoseSet = poseSet;
            CoverState = coverState;
            FinderState = finderState;
            CoverObject = coverObject;
            VanguardGrant = vanguardGrant;
            VanguardSource = vanguardSource;
            VanguardDiagnostic = vanguardDiagnostic;
            VanguardProgress = vanguardProgress;
            Details = details;
        }

        public string State { get; }
        public bool InCover { get; }
        public bool MovingToCover { get; }
        public bool NoCover { get; }
        public bool UpdateCover { get; }
        public bool PoseSet { get; }
        public string CoverState { get; }
        public string FinderState { get; }
        public string CoverObject { get; }
        public bool VanguardGrant { get; }
        public string VanguardSource { get; }
        public string VanguardDiagnostic { get; }
        public bool VanguardProgress { get; }
        public string Details { get; }

        public string Summary => "coverSeek=" + Safe(State)
            + ";sainCoverInUse=" + Bool(InCover)
            + ";sainCoverMovingTo=" + Bool(MovingToCover)
            + ";sainCoverNoCover=" + Bool(NoCover)
            + ";sainCoverUpdate=" + Bool(UpdateCover)
            + ";sainPoseSet=" + Bool(PoseSet)
            + ";sainCoverState=" + Safe(CoverState)
            + ";sainCoverFinder=" + Safe(FinderState)
            + ";sainCoverObject=" + Safe(CoverObject)
            + ";vanguardCoverGrant=" + Bool(VanguardGrant)
            + ";vanguardCoverSource=" + Safe(VanguardSource)
            + ";vanguardCoverDiagnostic=" + Safe(VanguardDiagnostic)
            + ";vanguardCoverProgress=" + Bool(VanguardProgress)
            + ";" + Details;
    }


    private sealed class VanguardCoverAnchorWanted
    {
        public VanguardCoverAnchorWanted(Vector3 target, string kind)
        {
            Target = target;
            Kind = kind;
        }

        public Vector3 Target { get; }
        public string Kind { get; }
    }

    private sealed class VanguardCoverAnchorCandidate
    {
        public VanguardCoverAnchorCandidate(Vector3 target, string kind, int obstacleRays)
        {
            Target = target;
            Kind = kind;
            ObstacleRays = obstacleRays;
        }

        public Vector3 Target { get; }
        public string Kind { get; }
        public int ObstacleRays { get; }
    }

    private sealed class VanguardCoverCandidate
    {
        public VanguardCoverCandidate(Vector3 target, string source, string diagnostic, float score)
        {
            Target = target;
            Source = source;
            Diagnostic = diagnostic;
            Score = score;
        }

        public Vector3 Target { get; }
        public string Source { get; }
        public string Diagnostic { get; }
        public float Score { get; }
    }

    private sealed class IncrementalCoverSearchState
    {
        public Vector3 Origin;
        public DateTimeOffset CreatedAtUtc;
        public DateTimeOffset ExpiresAtUtc;
        public IncrementalCoverBuildPhase Phase;
        public int WallAttemptIndex;
        public int WallSampled;
        public int WallAccepted;
        public Vector3 PendingWallSample;
        public int PendingWallRayIndex;
        public int PendingWallObstacleCount;
        public bool CandidatesReady;
        public List<VanguardCoverCandidate> Candidates = new();
        public int CandidateIndex;
        public bool AnchorBuildComplete;
        public List<VanguardCoverAnchorWanted> AnchorWants = new();
        public int AnchorWantIndex;
        public HashSet<string> AnchorEmittedKeys = new(StringComparer.OrdinalIgnoreCase);
        public List<VanguardCoverAnchorCandidate> Anchors = new();
        public int AnchorIndex;
        public int RawCandidatesVisited;
        public int RawCandidatesRejected;
        public int AnchorProbes;
        public int AnchorRejectedRecent;
        public int AnchorPreflightRejected;
        public string WallDiagnostic = "none";
        public string LastRejected = "none";
    }

    private enum IncrementalCoverBuildPhase
    {
        WallSample,
        WallRay,
        Finalize,
        Ready
    }

    private sealed class VanguardSurgeryCoverSlotState
    {
        public string OperatorId { get; init; } = string.Empty;
        public string BotProfileId { get; init; } = string.Empty;
        public Vector3 Origin { get; init; }
        public Vector3 Target { get; init; }
        public string Source { get; init; } = "none";
        public string Diagnostic { get; init; } = "none";
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset ExpiresAtUtc { get; init; }
        public DateTimeOffset LastCommandAtUtc { get; set; }
        public DateTimeOffset GrantUntilUtc { get; set; }
        public float InitialDistance { get; set; }
        public float LastDistance { get; set; }
        public float BestDistance { get; set; }
        public Vector3 BestPosition { get; set; }
        public DateTimeOffset CoverCommitUntilUtc { get; set; }
        public DateTimeOffset CommitOutsideEnvelopeSinceUtc { get; set; }
        public DateTimeOffset LastCommitOutsideObservationAtUtc { get; set; }
        public int CommitOutsideEnvelopeSamples { get; set; }
        public bool CommitCorrectionIssued { get; set; }
        public DateTimeOffset LastMeaningfulProgressAtUtc { get; set; }
        public Vector3 LastWorldPosition { get; set; }
        public DateTimeOffset LastWorldSampleAtUtc { get; set; }
        public int PhysicalStallSamples { get; set; }
        public int CommandCount { get; set; }
        public int StagnantCommandCount { get; set; }
        public string LastCommandResult { get; set; } = "none";
        public bool EftCommandPreferred { get; set; }
        public bool ArrivedLogged { get; set; }
    }

    private static string TypeName(object? value)
    {
        return value?.GetType().Name ?? "none";
    }

    private static string SafeTarget(string? actionTarget, string? needTarget)
    {
        if (!string.IsNullOrWhiteSpace(actionTarget) && actionTarget != "none")
        {
            return actionTarget;
        }

        return string.IsNullOrWhiteSpace(needTarget) ? "none" : needTarget;
    }

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Tri(bool? value) => value.HasValue ? Bool(value.Value) : "unknown";
    private static string Float(float? value) => value.HasValue ? value.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "none";

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }
}
#endif
