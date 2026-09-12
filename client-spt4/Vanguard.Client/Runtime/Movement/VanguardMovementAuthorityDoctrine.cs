#if SPT_CLIENT
using System;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Medical;
using Vanguard.Client.Options;

// Responsibility: centralizes the precedence rules deciding which movement authority may act when combat, medical, tactical, cohesion and return demands overlap.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: doctrine grants or suppresses movement domains; it does not fabricate the underlying threat, medical or authored-position evidence.
// Invariant: safety-critical authorities preempt lower-priority locomotion and every temporary grant remains bounded to the current raid evidence window.

namespace Vanguard.Client.Runtime.Movement;

internal static class VanguardMovementAuthorityDoctrine
{
    public const string StatusTag = "VANGUARD_MOVE_AUTHORITY_READONLY_OK";
    public const string BrokerDryRunStatusTag = "VANGUARD_MOVEMENT_BROKER_DRYRUN_OK";
    public const string MovementContractsStatusTag = "VANGUARD_MOVEMENT_CONTRACTS_OK";
    public const string MovementLeasePlanStatusTag = "VANGUARD_MOVEMENT_LEASE_PLAN_OK";
    public const string HardReturnActiveStatusTag = "VANGUARD_HARD_RETURN_ACTIVE_OK";
    public const string ExternalSuppressHardReturnStatusTag = "VANGUARD_EXTERNAL_SUPPRESS_HARD_RETURN_OK";
    public const string SainBoundaryReturnActiveStatusTag = "VANGUARD_SAIN_BOUNDARY_RETURN_ACTIVE_OK";
    public const string SainSearchSuppressStatusTag = "VANGUARD_SAIN_SEARCH_SUPPRESS_OK";
    public const string ReturnPathValidationStatusTag = "VANGUARD_RETURN_PATH_VALIDATION_OK";
    public const string PreemptPendingStatusTag = "VANGUARD_PREEMPT_PENDING_OK";
    public const string ReturnAuthorityLockStatusTag = "VANGUARD_RETURN_AUTHORITY_LOCK_OK";
    public const string ActionRallyReturnStatusTag = "VANGUARD_ACTION_RALLY_RETURN_OK";
    public const string MoveBridgeLayerStatusTag = "VANGUARD_MOVE_BRIDGE_LAYER_OK";
    public const string GoToSomePointBridgeStatusTag = "VANGUARD_GOTOSOMEPOINT_BRIDGE_OK";
    public const string ReturnContinuationStatusTag = "VANGUARD_RETURN_CONTINUATION_OK";
    public const string AnchorScoreStatusTag = "VANGUARD_ANCHOR_SCORE_OK";
    public const string MovementDoctrineF12SyncStatusTag = "VANGUARD_MOVEMENT_F12_SYNC_OK";
    public const string TacticalEnvironmentReadOnlyStatusTag = "VANGUARD_TACTICAL_ENVIRONMENT_READONLY_OK";
    public const string TacticalRepositionActiveStatusTag = "VANGUARD_TACTICAL_REPOSITION_ACTIVE_OK";
    public const string TacticalPlacementSolverStatusTag = "VANGUARD_TACTICAL_PLACEMENT_SOLVER_OK";
    public const string RuntimeCleanStatusTag = "VANGUARD_RUNTIME_CLEAN_OK";
    public const string ExternalPreemptPendingStatusTag = "VANGUARD_EXTERNAL_PREEMPT_PENDING_OK";
    public const string BuildParityGuardStatusTag = "VANGUARD_BUILD_PARITY_GUARD_OK";
    public const string TacticalTuningStatusTag = "VANGUARD_TACTICAL_TUNING_OK";
    public const string HardReturnCombatBackoffStatusTag = "VANGUARD_HARD_RETURN_COMBAT_BACKOFF_OK";
    public const string OpportunisticLootBrokerStatusTag = "VANGUARD_OPPORTUNISTIC_LOOT_BROKER_OK";
    public const string CloseCohesionStatusTag = "VANGUARD_CLOSE_COHESION_STATUS";
    public const string CloseCohesionRuntimeTuningStatusTag = "VANGUARD_CLOSE_COHESION_RUNTIME_TUNING_STATUS";
    public const string SquadTravelCombatAuthorityStatusTag = "VANGUARD_SQUAD_TRAVEL_COMBAT_AUTHORITY_STATUS";
    public const string SquadTravelBuildGuardStatusTag = "VANGUARD_SQUAD_TRAVEL_BUILD_GUARD_STATUS";
    public const string OrbitAuthorityQuiesceStatusTag = "VANGUARD_ORBIT_AUTHORITY_QUIESCE_STATUS";
    public const string CohesionClaimsStatusTag = "VANGUARD_COHESION_CLAIMS_STATUS";
    public const string OrbitAbsentBackoffStatusTag = "VANGUARD_ORBIT_ABSENT_BACKOFF_STATUS";
    public const string CohesionClaimsBuildStatusTag = "VANGUARD_COHESION_CLAIMS_BUILD_STATUS";
    public const string CohesionAnchorsRunStatusTag = "VANGUARD_COHESION_ANCHORS_RUN_STATUS";
    public const string CombatHoldMedicalCatchupStatusTag = "VANGUARD_COMBAT_HOLD_MEDICAL_CATCHUP_STATUS";
    public const string HostileIndoorMovementPlanStatusTag = "VANGUARD_HOSTILE_INDOOR_MOVEMENT_PLAN_STATUS";
    public const string CombatBindCohesionRecoveryStatusTag = "VANGUARD_COMBAT_BIND_COHESION_RECOVERY_STATUS";
    public const string PathAlertRecoveryStatusTag = "VANGUARD_PATH_ALERT_RECOVERY_STATUS";
    public const string HardReturnAlertStatusTag = "VANGUARD_HARD_RETURN_ALERT_STATUS";
    public const string CombatCohesionAuthorityStatusTag = "VANGUARD_COMBAT_COHESION_AUTHORITY_STATUS";
    public const string BootLogFixStatusTag = "VANGUARD_BOOT_LOG_FIX_STATUS";
    public const string MovementCommandQueueStatusTag = "VANGUARD_MOVEMENT_COMMAND_QUEUE_STATUS";
    public const string OrchestratorAuthorityStatusTag = "VANGUARD_ORCHESTRATOR_AUTHORITY_STATUS";
    public const string ExclusiveAuthorityStatusTag = "VANGUARD_EXCLUSIVE_AUTHORITY_STATUS";
    public const string CombatWindowClosureStatusTag = "VANGUARD_COMBAT_WINDOW_CLOSURE_STATUS";
    public const string FormationLanesStatusTag = "VANGUARD_FORMATION_LANES_STATUS";
    public const string AwarenessCombatSupportStatusTag = "VANGUARD_AWARENESS_COMBAT_SUPPORT_STATUS";
    public const string LanePreservingFallbackStatusTag = "VANGUARD_LANE_PRESERVING_FALLBACK_STATUS";
    public const string VanguardMovementDriverStatusTag = "VANGUARD_VANGUARD_MOVEMENT_DRIVER_STATUS";
    public const string TargetBootstrapStatusTag = "VANGUARD_TARGET_BOOTSTRAP_STATUS";
    public const string DriverDominanceStatusTag = "VANGUARD_DRIVER_DOMINANCE_STATUS";
    public const string CompactLanesStatusTag = "VANGUARD_COMPACT_LANES_STATUS";
    public const string TargetApplyVerifyStatusTag = "VANGUARD_TARGET_APPLY_VERIFY_STATUS";
    public const string PostKillCloseThreatStatusTag = "VANGUARD_POST_KILL_CLOSE_THREAT_STATUS";
    public const string FinalAntiStackStatusTag = "VANGUARD_FINAL_ANTISTACK_STATUS";

    public const float DefaultTacticalBubbleMeters = 75f;
    public const float DefaultSoftCorrectionMeters = 80f;
    public const float DefaultHardCorrectionMeters = 88f;
    public const float DefaultActionRallyClearMeters = 38f;
    public const float DefaultActionRallyAcceptMeters = 45f;
    public const float DefaultActionRallyPreferredMeters = 24f;

    public static float TacticalBubbleMeters => VanguardOperatorRuntimeAuditOptions.GetMovementTacticalBubbleMeters();
    public static float SoftCorrectionMeters => Math.Max(TacticalBubbleMeters + 1f, VanguardOperatorRuntimeAuditOptions.GetMovementSoftCorrectionMeters());
    public static float HardCorrectionMeters => Math.Max(SoftCorrectionMeters + 1f, VanguardOperatorRuntimeAuditOptions.GetMovementHardCorrectionMeters());
    public static float ClearCorrectionMeters => Math.Max(TacticalBubbleMeters - 5f, ActionRallyAcceptMeters + 5f);
    public static float ActionRallyClearMeters => Math.Min(VanguardOperatorRuntimeAuditOptions.GetMovementActionRallyClearMeters(), ActionRallyAcceptMeters);
    public static float ActionRallyAcceptMeters => VanguardOperatorRuntimeAuditOptions.GetMovementActionRallyAcceptMeters();
    public static float ActionRallyPreferredMeters => Math.Min(VanguardOperatorRuntimeAuditOptions.GetMovementActionRallyPreferredMeters(), Math.Max(8f, ActionRallyAcceptMeters - 4f));
    public static float ActionRallyNearMeters => Math.Max(8f, ActionRallyPreferredMeters - 6f);
    public static float ActionRallyWideMeters => Math.Min(ActionRallyAcceptMeters - 2f, ActionRallyPreferredMeters + 8f);
    public static float ActionRallyTightMeters => 12f;
    public static float ActionRallyOuterMeters => Math.Min(ActionRallyAcceptMeters - 1f, ActionRallyPreferredMeters + 16f);
    // Runtime invariant: emergency recall is not complete merely because the Operator re-enters the broad
    // 45 m action-rally acceptance ring. It must hand back to normal travel/close cohesion from
    // a genuinely useful support distance.
    public static float HardReturnCompletionMeters => Math.Min(ActionRallyAcceptMeters, 28.0f);
    public static float HardReturnRetargetOwnerMoveMeters => 8.0f;
    public static float HardReturnRetargetAnchorOwnerDistanceMeters => 36.0f;
    public static float HardReturnRetargetNoProgressSeconds => 2.25f;
    public static float StaleSearchDistanceMeters => TacticalBubbleMeters;
    public static float TacticalAnchorRadiusMeters => 5f;
    public static float TacticalRepositionCooldownSeconds => VanguardOperatorRuntimeAuditOptions.GetMovementTacticalRepositionCooldownSeconds();
    public static float TacticalRepositionMinDeltaMeters => VanguardOperatorRuntimeAuditOptions.GetMovementTacticalRepositionMinDeltaMeters();
    public static float TacticalRepositionMaxDurationSeconds => 16.0f;
    public static float TacticalRepositionNoProgressSeconds => Math.Min(5.5f, MovementLeaseNoProgressSeconds);
    public static float TacticalRepositionSuccessCooldownSeconds => Math.Max(14.0f, TacticalRepositionCooldownSeconds * 2.0f);
    public static float TacticalSquadPressureBlockMeters => Math.Min(TacticalBubbleMeters, 55.0f);
    public static float CloseCohesionStartMinMeters => 18.0f;
    public static float CloseCohesionStartMaxMeters => 34.0f;
    public static float CloseCohesionForceStartMeters => 26.0f;
    public static float CloseCohesionOrbitPreemptMinMeters => 24.0f;
    public static float CloseCohesionPathPreemptMinMeters => 26.0f;
    public static float CloseCohesionOutdoorTargetMeters => 18.0f;
    public static float CloseCohesionIndoorTargetMeters => 12.0f;
    public static float CloseCohesionAnchorRadiusMeters => 5.5f;
    public static float CloseCohesionMaxDurationSeconds => 8.0f;
    public static float CloseCohesionNoProgressSeconds => 3.50f;
    public static float CloseCohesionSuccessCooldownSeconds => 8.0f;
    public static float CloseCohesionFailureCooldownSeconds => 7.0f;
    public static float CloseCohesionSoftProgressGainMeters => 0.45f;
    public static float CloseCohesionOwnerProgressGainMeters => 0.85f;
    public static float CloseCohesionSoftCompleteExtraMeters => 3.5f;
    public static float CloseCohesionOutdoorRelaxedTargetMeters => 18.0f;
    public static float CloseCohesionIndoorRelaxedTargetMeters => 15.0f;
    public static float CloseCohesionTickSeconds => 0.40f;
    public static float TravelCohesionStartMeters => 24.0f;
    // Runtime invariant: owner travel is a responsiveness-critical authority transition.  It keeps a
    // dedicated bounded path budget so static claim planning cannot serialize the squad start.
    public static int TravelAdmissionPathPlansPerFrame => 4;
    public static int TravelRetargetPathPlansPerFrame => 3;
    public static float TravelRetargetMaterialMeters => 1.50f;
    public static float TravelRetargetCooldownSeconds => 0.75f;
    // Runtime invariant: keep a useful command lead without chasing every breadcrumb. Retarget path
    // work is event-driven near the active anchor, while the physical watchdog is independent
    // from target movement.
    public static float TravelRetargetLeadDistanceMeters => 12.0f;
    public static float TravelRetargetMaxAdvanceFormationMeters => 10.0f;
    public static float TravelRetargetMaxAdvanceCatchUpMeters => 16.0f;
    public static float TravelRetargetMaxAdvanceEmergencyMeters => 22.0f;
    public static float TravelRetargetMaxAnchorDeltaFormationMeters => 16.0f;
    public static float TravelRetargetMaxAnchorDeltaCatchUpMeters => 24.0f;
    public static float TravelRetargetMaxAnchorDeltaEmergencyMeters => 30.0f;
    public static float TravelPhysicalSampleSeconds => 0.45f;
    public static float TravelPhysicalJitterMeters => 0.18f;
    public static float TravelPhysicalMeaningfulDisplacementMeters => 0.70f;
    public static float TravelPhysicalCurvedPathTravelMeters => 1.10f;
    public static float TravelPhysicalCurvedPathNetMeters => 0.55f;
    public static float TravelPhysicalMeaningfulRouteGainMeters => 0.35f;
    public static float TravelPhysicalMeaningfulGoalGainMeters => 0.45f;
    public static float TravelPhysicalBlockedNetDisplacementMeters => 0.60f;
    public static float TravelPhysicalBlockedDetectSeconds => 1.35f;
    public static float TravelPhysicalRecoveryGraceSeconds => 2.50f;
    public static float TravelExtremeOwnerLagDistanceMeters => 60.0f;
    public static float TravelExtremeOwnerLagObservationSeconds => 8.0f;
    public static float TravelExtremeOwnerLagNoClosingSeconds => 6.0f;
    // Runtime invariant: CatchUp does not use the short-anchor the runtime recovery loop. A soft stall
    // releases the existing corridor retarget; only a prolonged deadlock may restart the same
    // command once for the lifetime of the lease.
    public static float TravelCatchUpSoftStallRetargetReleaseSeconds => 1.75f;
    public static float TravelCatchUpHardRestartDetectSeconds => 6.25f;
    public static float TravelCatchUpHardRestartGraceSeconds => 4.00f;
    public static float TravelPhysicalRecoverySameAnchorMeters => 2.50f;
    public static float TravelPhysicalRecoveryFormationLookAheadMeters => 9.0f;
    public static float TravelPhysicalRecoveryCatchUpLookAheadMeters => 14.0f;
    public static float TravelPhysicalRecoveryEmergencyLookAheadMeters => 20.0f;
    public static float TravelPhysicalRecoveryMaxAnchorDistanceMeters => 28.0f;
    public static float TravelPhysicalFailureMemorySeconds => 16.0f;
    public static float TravelRecentReacquireOwnerDistanceMeters => 64.0f;
    public static float TravelRecentReacquireMinimumDebtMeters => 96.0f;
    public static float TravelRecentReacquireMinimumPauseSeconds => 6.0f;
    public static float TravelRecentReacquireMaxSetbackMeters => 56.0f;
    public static float TravelRecentReacquireCandidateStepMeters => 56.0f;
    public static int TravelRecentReacquireCandidateCount => 2;
    public static int TravelRecentReacquirePathPlansPerFrame => 2;
    // Runtime invariant: admission-only reconciliation for the inverse stale-cursor case: the
    // Operator is already physically near the owner after combat/medical, but the logical cursor
    // still points far behind on the same append-only route. The normal active Travel path never
    // enters this policy.
    public static float TravelPostInterruptionReconcileMinimumPauseSeconds => 6.0f;
    public static float TravelPostInterruptionReconcileMaximumOwnerDistanceMeters => 36.0f;
    public static float TravelPostInterruptionReconcileMinimumDebtMeters => 96.0f;
    public static float TravelPostInterruptionReconcileRecentWindowMeters => 150.0f;
    public static float TravelPostInterruptionReconcileProjectionCaptureMeters => 36.0f;
    public static float TravelPostInterruptionReconcileMaximumBehindSlotMeters => 32.0f;
    public static float TravelPostInterruptionReconcileMinimumStaleAnchorDistanceMeters => 36.0f;
    public static float TravelPostInterruptionReconcileMinimumAnchorImprovementMeters => 16.0f;
    public static float TravelPostInterruptionReconcileMinimumOwnerRelativeDivergenceMeters => 16.0f;
    public static float TravelPostInterruptionReconcileMaximumCandidateDistanceMeters => 38.0f;
    public static float TravelConsumedAnchorPathFailureSeconds => 1.50f;
    public static int TravelConsumedAnchorPathFailureCount => 2;
    // Runtime invariant: an active Travel generation cannot remain authoritative after its physical
    // anchor is consumed while the owner is still outside the Travel admission envelope.
    // The contradiction is observed briefly, then the exact generation is released without
    // cooldown so the scheduler can resolve a fresh route target.
    public static float TravelConsumedAnchorStaleGenerationReleaseSeconds => 1.75f;
    // The scheduler heartbeat only proves that the single Travel executor is still alive.
    // Physical liveness remains exclusively evaluated by VanguardSquadTravelCohesionExecutor.
    public static float TravelSchedulerHeartbeatSeconds => 2.50f;
    public static float TravelSchedulerHeartbeatTimeoutSeconds => 12.0f;
    public static float TravelOwnerStationaryReleaseSeconds => 1.50f;
    public static float TravelCohesionForceMeters => 32.0f;
    public static float TravelCohesionTargetMeters => 16.0f;
    public static float TravelCohesionAnchorRadiusMeters => 7.0f;
    public static float TravelCohesionMaxDurationSeconds => 16.0f;
    public static float TravelCohesionNoProgressSeconds => 4.50f;
    public static float TravelCohesionSuccessCooldownSeconds => 10.0f;
    public static float TravelCohesionFailureCooldownSeconds => 6.0f;
    public static float TravelCohesionPostReturnHoldSeconds => 42.0f;
    public static float TravelCohesionPostReturnReacquireMeters => 26.0f;
    public static float TravelCohesionSoftPathPreemptMeters => 28.0f;
    public static float OrbitQuiesceMinDistanceMeters => 24.0f;
    public static float OrbitQuiesceForceDistanceMeters => 46.0f;
    public static float OrbitQuiesceCooldownBypassMeters => 66.0f;
    public static float OrbitQuiesceRefreshSeconds => 1.15f;
    public static float OrbitQuiesceHoldSeconds => 45.0f;
    public static float OrbitObjectiveOppositionDot => -0.15f;
    public static float TacticalVolumeJoinStartMinMeters => 4.0f;
    public static float TacticalVolumeJoinStartMaxMeters => 52.0f;
    public static float TacticalVolumeJoinTargetMeters => 16.0f;
    public static float TacticalVolumeJoinAnchorRadiusMeters => 6.0f;
    public static float TacticalVolumeJoinMaxDurationSeconds => 18.0f;
    public static float TacticalVolumeJoinNoProgressSeconds => 4.75f;
    public static float TacticalVolumeJoinSuccessCooldownSeconds => 18.0f;
    public static float TacticalVolumeJoinFailureCooldownSeconds => 12.0f;
    public static float ClaimedCohesionStartMeters => 9.0f;
    public static float ClaimedCohesionExternalResidueStartMeters => 10.0f;
    public static float ClaimedCohesionStartAnchorDistanceMeters => 6.25f;
    public static float ClaimedCohesionUsefulCorrectionStartMeters => 6.0f;
    public static float ClaimedCohesionAnchorRadiusMeters => 4.75f;
    public static float ClaimedCohesionStationaryAnchorRadiusMeters => 5.25f;
    public static float ClaimedCohesionSoftCompleteMeters => 11.0f;
    public static float ClaimedCohesionMaxDurationSeconds => 14.0f;
    public static float ClaimedCohesionNoProgressSeconds => 5.50f;
    public static float ClaimedCohesionMovingMinHoldSeconds => 3.25f;
    public static float ClaimedCohesionStationaryMinHoldSeconds => 12.0f;
    public static float ClaimedCohesionSuccessCooldownSeconds => 4.0f;
    public static float ClaimedCohesionStationarySuccessCooldownSeconds => 9.0f;
    public static float ClaimedCohesionFailureCooldownSeconds => 3.0f;
    public static float ClaimedCohesionValidSeconds => 46.0f;
    public static float ClaimedCohesionStationaryValidSeconds => 90.0f;
    public static float ClaimedCohesionOwnerMoveRefreshMeters => 4.0f;
    public static float ClaimedCohesionOwnerRotateRefreshDegrees => 50.0f;
    public static float ClaimedCohesionOwnerStationarySpeed => 0.35f;
    public static float ClaimedCohesionOwnerFastSpeed => 1.85f;
    public static float ClaimedCohesionSprintDistanceMeters => 20.0f;
    public static float ClaimedCohesionRunDistanceMeters => 8.0f;
    public static float ClaimedCohesionAnchorSprintDistanceMeters => 15.0f;
    public static float ClaimedCohesionMicroHoldAnchorMeters => 11.0f;
    public static float ClaimedCohesionMicroHoldOwnerMeters => 25.0f;
    public static float ClaimedCohesionRunAnchorDistanceMeters => 12.0f;
    public static float ClaimedCohesionFreshContactHoldMeters => 22.0f;
    public static float ClaimedCohesionAnchorStableReplanMeters => 48.0f;
    public static float ClaimedCohesionRallyFallbackDistanceMeters => 24.0f;
    public static float ClaimedCohesionIndoorOwnerMoveRefreshMeters => 5.5f;
    public static float ClaimedCohesionIndoorOwnerRotateRefreshDegrees => 58.0f;
    public static float ClaimedCohesionStationaryReuseSeconds => 120.0f;
    public static float ClaimedCohesionMovingReuseSeconds => 24.0f;
    public static float ClaimedCohesionActiveLeaseProtectedSeconds => 16.0f;
    public static float ClaimedCohesionProgressGainMeters => 0.45f;
    public static float MovementPlanCurrentAnchorProtectedSeconds => 44.0f;
    public static float MovementRetargetAnchorDeltaMeters => 6.0f;
    public static float MovementRetargetOwnerPressureMeters => 24.0f;
    public static float MovementRetargetOwnerDistanceGrowthMeters => 3.0f;
    public static float MovementRetargetNoProgressSeconds => 2.50f;
    public static float MovementRetargetCooldownSeconds => 2.25f;
    public static int MovementRetargetMaxPerLease => 3;
    public static float MovementPlanQueueCoalesceMeters => 14.0f;
    public static float MovementPlanCatchupOwnerDistanceMeters => 16.0f;
    public static float MovementPlanIgnoreOwnerRotationWhileCatchupMeters => 42.0f;
    public static float MovementPlanRallyFallbackVeryFarMeters => 34.0f;
    public static float IndoorSectorHoldOwnerMoveRefreshMeters => 9.0f;
    public static float IndoorSectorHoldOwnerRotateRefreshDegrees => 120.0f;
    public static float StaleSainExitNoActionSeconds => 3.75f;
    public static float CombatNoFireSuspectSeconds => 1.25f;
    public static float CombatNoFireRecoveryCooldownSeconds => 2.25f;
    public static float CombatNoFireActionableDistanceMeters => 58.0f;
    public static float SquadContactSectorAlertSeconds => 14.0f;
    public static float MedicalBreakContactThreatMeters => 42.0f;
    public static float ClaimPathHardCloseSupportOutdoorMeters => 50.0f;
    public static float ClaimPathHardCloseSupportIndoorMeters => 34.0f;
    public static float ClaimPathHardCloseSupportExtraMeters => 28.0f;
    public static float ClaimPathHardCloseSupportRatio => 2.20f;
    public static float CombatNoFireEscalateSeconds => 3.25f;
    public static float CombatNoProductionCleanupSeconds => 20.0f;
    public static float CombatNoProductionWindowMaxSeconds => 26.0f;
    public static float CombatProtectedSegmentSeconds => 26.0f;
    public static float CombatProtectedAbsoluteMaxSeconds => 90.0f;
    public static float CombatTargetRefreshExtensionSeconds => 14.0f;
    public static float CombatNoProductionReopenBackoffSeconds => 5.0f;
    public static float CohesionLanePreservingFallbackDistanceMeters => 82.0f;
    public static float CohesionMinOperatorSpacingMeters => 6.00f;
    public static float CohesionPreferredOperatorSpacingMeters => 9.50f;
    public static float SquadContactSectorAlertLevel2DistanceMeters => 58.0f;
    public static float ClaimedCohesionSupportPathMaxIndoorMeters => 26.0f;
    public static float ClaimedCohesionSupportPathMaxOutdoorMeters => 32.0f;
    public static float ClaimedCohesionSupportPathRatioIndoor => 1.85f;
    public static float ClaimedCohesionSupportPathRatioOutdoor => 1.85f;
    public static float InteriorMissionMaxOwnerDirectMeters => 58.0f;
    public static float InteriorMissionMaxOwnerPathMeters => 82.0f;
    public static float InteriorMissionMaxOwnerPathRatio => 4.50f;
    public static float InteriorMissionMaxBotPathMeters => 68.0f;
    public static float InteriorMissionArrivalSpacingMeters => 4.25f;
    public static float InteriorMissionArrivalStackConfirmSeconds => 1.25f;
    public static int ClaimedCohesionMaxActiveLeases => 4;
    public static float TacticalVolumeJoinVerticalDeltaMeters => 2.25f;
    public static float TacticalVolumeJoinPathRatio => 2.75f;
    public static float TacticalVolumeJoinPathExtraMeters => 32.0f;
    public static float SquadContactBroadcastSeconds => 14.0f;
    public static float SquadCombatTargetMemorySeconds => 35.0f;
    public static float SquadContactAssistRadiusMeters => 82.0f;
    // Runtime invariant: propagated squad knowledge remains in memory at long range, but it no longer owns
    // movement unless the isolated Operator has an individual Vanguard assignment or productive combat of its own.
    public static float PropagatedContactMovementAuthorityMaxDistanceMeters => 50.0f;
    public static float StationaryMedicalStartMaxOwnerDistanceMeters => 50.0f;
    public static float StationaryMedicalPrepareMaxOwnerDistanceMeters => 62.0f;
    public static float StationaryMedicalMaintainMaxOwnerDistanceMeters => 60.0f;
    public static float SquadContactCloseVisibleMeters => 52.0f;
    public static float CombatCohesionSupportRadiusMeters => 24.0f;
    public static float CombatCohesionForcedCatchupMeters => VanguardOperatorRuntimeAuditOptions.GetMovementCombatCohesionForcedCatchupMeters();
    public static float CombatCohesionHardReturnMeters => 44.0f;
    public static float CombatCohesionEmergencyReturnMeters => 52.0f;
    public static float CombatCohesionProductiveHoldMaxMeters => 42.0f;
    public static float CombatCohesionHoldSectorMaxMeters => 24.0f;
    public static float TravelCatchUpEnterMeters => VanguardOperatorRuntimeAuditOptions.GetMovementTravelCatchUpEnterMeters();
    public static float TravelCatchUpExitMeters => VanguardOperatorRuntimeAuditOptions.GetMovementTravelCatchUpExitMeters();
    public static float TravelModeDwellSeconds => VanguardOperatorRuntimeAuditOptions.GetMovementTravelModeDwellSeconds();
    // Compatibility alias for existing logs and non-mode consumers. The runtime mode transitions use
    // the explicit enter/exit+dwell contract above.
    public static float TravelCohesionSprintDistanceMeters => TravelCatchUpEnterMeters;
    public static float TravelCohesionRequiredSprintDistanceMeters => 36.0f;
    public static float HardReturnCombatBackoffSeconds => 8.0f;
    public static bool OpportunisticLootBrokerEnabled => VanguardOperatorRuntimeAuditOptions.GetMovementOpportunisticLootBrokerEnabled();
    public static float OpportunisticLootMaxDistanceMeters => VanguardOperatorRuntimeAuditOptions.GetMovementOpportunisticLootMaxDistanceMeters();
    public static float OpportunisticLootScanCooldownSeconds => VanguardOperatorRuntimeAuditOptions.GetMovementOpportunisticLootScanCooldownSeconds();
    public static float OpportunisticLootGrantSeconds => VanguardOperatorRuntimeAuditOptions.GetMovementOpportunisticLootGrantSeconds();
    public static bool TacticalRepositionActiveEnabled => VanguardOperatorRuntimeAuditOptions.GetMovementTacticalRepositionEnabled();
    public static float RegroupAnchorRadiusMeters => 6f;
    public static float HardReturnAnchorRadiusMeters => 7f;
    public static float MovementLeaseMinDurationSeconds => 4.0f;
    public static float MovementLeaseMaxDurationSeconds => VanguardOperatorRuntimeAuditOptions.GetMovementLeaseMaxDurationSeconds();
    public static float MovementLeaseNoProgressSeconds => VanguardOperatorRuntimeAuditOptions.GetMovementLeaseNoProgressSeconds();
    public static float MovementLeaseStartCooldownSeconds => VanguardOperatorRuntimeAuditOptions.GetMovementLeaseStartCooldownSeconds();
    public static float MovementLeaseFailureCooldownSeconds => VanguardOperatorRuntimeAuditOptions.GetMovementLeaseFailureCooldownSeconds();
    public static float MovementLeaseAbortCooldownSeconds => 4f;
    public static float PreemptPendingDelaySeconds => 0.55f;
    public static float PreemptPendingMaxSeconds => 3.25f;
    public static float MovementAuthorityRefreshSeconds => 1.25f;
    public static float MovementLeaseProgressExtendSeconds => 8.0f;
    public static float MovementLeaseHardMaxSeconds => Math.Max(MovementLeaseMaxDurationSeconds + 15f, 75.0f);
    public static int ActionRallyMaxReanchorsPerLease => VanguardOperatorRuntimeAuditOptions.GetMovementActionRallyMaxReanchors();
    public static float ActionRallyReanchorCooldownSeconds => 3.0f;
    public static float ActionRallyReanchorLimitCooldownSeconds => 22.0f;
    public static float ActionRallyAnchorHardRejectMeters => Math.Max(178.0f, HardCorrectionMeters + ActionRallyAcceptMeters);
    public static float ActionRallyAnchorScoreMinimum => 8.0f;
    public static bool ActiveBackendApplyEnabled => VanguardOperatorRuntimeAuditOptions.GetMovementOutsideBubbleRecallEnabled();
    public static bool ActiveSainBoundaryReturnEnabled => VanguardOperatorRuntimeAuditOptions.GetMovementSainBoundaryReturnEnabled();
    public static bool SuppressExternalDuringRecallEnabled => VanguardOperatorRuntimeAuditOptions.GetMovementSuppressExternalDuringRecallEnabled();
    public static bool VerboseDoctrineLogEnabled => VanguardOperatorRuntimeAuditOptions.GetMovementVerboseDoctrineLogEnabled();


    public static bool HasCriticalLootActivity(OperatorDecisionSnapshot snapshot)
    {
        return snapshot != null
            && (snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true || snapshot.Looting.HasActiveLootable == true);
    }

    public static bool HasNonCriticalOrbitOrPathResidue(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return false;
        }

        return (snapshot.Orbit.Active || snapshot.Movement.HasPath == true)
            && !HasCriticalLootActivity(snapshot)
            && !IsTrueDirectThreat(snapshot)
            && !HasImmediateCombatAwareness(snapshot)
            && !IsStationaryMedicalAuthority(snapshot);
    }

    public static bool IsOrbitObjectiveOpposingOwner(OperatorDecisionSnapshot snapshot, out float alignment)
    {
        alignment = 0f;
        if (snapshot == null || !snapshot.Orbit.Objective.HasValue || !snapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            return false;
        }

        Vector3 toOwner = FlattenForDoctrine(snapshot.SquadCohesion.OwnerPosition.Value - snapshot.Position);
        Vector3 toOrbit = FlattenForDoctrine(snapshot.Orbit.Objective.Value - snapshot.Position);
        if (toOwner.sqrMagnitude <= 1.0f || toOrbit.sqrMagnitude <= 1.0f)
        {
            return false;
        }

        toOwner.Normalize();
        toOrbit.Normalize();
        alignment = Vector3.Dot(toOwner, toOrbit);
        return alignment <= OrbitObjectiveOppositionDot;
    }

    public static bool ShouldQuiesceOrbitForSquadTravel(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        if (!snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement)
        {
            reason = "owner_unreliable";
            return false;
        }

        if (!HasNonCriticalOrbitOrPathResidue(snapshot))
        {
            reason = "no_noncritical_orbit_path_residue";
            return false;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner >= OrbitQuiesceMinDistanceMeters)
        {
            reason = "distance_pressure:" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        if (IsOrbitObjectiveOpposingOwner(snapshot, out var alignment))
        {
            reason = "orbit_objective_opposes_owner:dot=" + alignment.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        reason = "distance_low_and_objective_not_opposed";
        return false;
    }

    private static Vector3 FlattenForDoctrine(Vector3 vector)
    {
        vector.y = 0f;
        return vector;
    }

    public static bool IsGoOrder(string squadOrder)
    {
        return string.Equals(squadOrder, "go", StringComparison.OrdinalIgnoreCase)
            || string.Equals(squadOrder, "advance", StringComparison.OrdinalIgnoreCase)
            || string.Equals(squadOrder, "assault", StringComparison.OrdinalIgnoreCase)
            || string.Equals(squadOrder, "push", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRegroupOrder(string squadOrder)
    {
        return string.Equals(squadOrder, "regroup", StringComparison.OrdinalIgnoreCase)
            || string.Equals(squadOrder, "follow", StringComparison.OrdinalIgnoreCase)
            || string.Equals(squadOrder, "tight", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsStationaryMedicalAuthority(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.Alive)
        {
            return false;
        }

        // Vanguard consumes the same effective-execution contract as the central authority policy.
        // Authority follows the live execution, not the latest diagnosis snapshot: a surgery/splint
        // controller can remain active for a short terminal phase after the body-part read model has
        // already changed. Requiring the need to remain classified as stationary would release
        // movement early and could cancel the final animation/controller completion. Conversely, a
        // black limb/fracture/surgery debt without execution remains passive and non-sovereign.
        return VanguardEffectiveMedicalExecutionPolicy.IsStationaryExecution(snapshot, DateTimeOffset.UtcNow, out _);
    }

    public static bool HasPassiveStationaryMedicalDebt(OperatorDecisionSnapshot snapshot)
    {
        return snapshot != null
            && snapshot.Alive
            && IsStationaryMedicalNeed(snapshot)
            && !IsStationaryMedicalAuthority(snapshot);
    }

    private static bool IsStationaryMedicalNeed(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot.Medical.Need.DominantNeed == VanguardMedicalNeed.SurgeryDestroyedPart
            || snapshot.Medical.Need.DominantNeed == VanguardMedicalNeed.BlackBroken
            || snapshot.Medical.Need.DominantNeed == VanguardMedicalNeed.Fracture)
        {
            return true;
        }

        string step = (snapshot.Medical.Plan.NextStep + "|" + snapshot.Medical.Plan.ExecutionKind + "|" + snapshot.Medical.Plan.PlanKey).ToLowerInvariant();
        return step.Contains("surgery") || step.Contains("stationary") || step.Contains("fracture");
    }

    public static bool IsTrueDirectThreat(OperatorDecisionSnapshot snapshot)
    {
        return snapshot.Medical.Safety.EnemyCanShoot
            || snapshot.Medical.Safety.IncomingFireRecent
            || snapshot.Threat.EnemyCanShoot == true
            || snapshot.Threat.ShotMeRecently == true
            || snapshot.Threat.ShotAtMeRecently == true;
    }


    public static bool HasImmediateCombatAwareness(OperatorDecisionSnapshot snapshot)
    {
        if (IsTrueDirectThreat(snapshot))
        {
            return true;
        }

        bool awarenessCanHurt = snapshot.Awareness.IncomingFireFresh || snapshot.Awareness.CandidateCanShoot;
        bool scanCanHurt = snapshot.ThreatScan.CandidateIncomingFireFresh || snapshot.ThreatScan.CandidateCanShoot || snapshot.ThreatScan.CandidateShotMeRecently || snapshot.ThreatScan.CandidateShotAtMeRecently;
        bool closeAwarenessContact = snapshot.Awareness.CandidateDistance.HasValue
            && snapshot.Awareness.CandidateDistance.Value <= SquadContactCloseVisibleMeters
            && (snapshot.Awareness.CandidateVisible || snapshot.Awareness.CandidateLineOfSight);
        bool closeScanContact = snapshot.ThreatScan.CandidateDistance.HasValue
            && snapshot.ThreatScan.CandidateDistance.Value <= SquadContactCloseVisibleMeters
            && (snapshot.ThreatScan.CandidateVisible || snapshot.ThreatScan.CandidateLineOfSight);
        bool level2AwarenessContact = snapshot.Awareness.CandidateDistance.HasValue
            && snapshot.Awareness.CandidateDistance.Value <= SquadContactSectorAlertLevel2DistanceMeters
            && (snapshot.Awareness.CandidateCanShoot || snapshot.Awareness.IncomingFireFresh);
        bool level2ScanContact = snapshot.ThreatScan.CandidateDistance.HasValue
            && snapshot.ThreatScan.CandidateDistance.Value <= SquadContactSectorAlertLevel2DistanceMeters
            && (snapshot.ThreatScan.CandidateCanShoot || snapshot.ThreatScan.CandidateIncomingFireFresh || snapshot.ThreatScan.CandidateShotAtMeRecently || snapshot.ThreatScan.CandidateShotMeRecently);
        return awarenessCanHurt || scanCanHurt || closeAwarenessContact || closeScanContact || level2AwarenessContact || level2ScanContact;
    }

    public static bool ShouldRejoinBeforeStationaryMedicalStart(OperatorDecisionSnapshot snapshot, float maxOwnerDistanceMeters, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        if (!snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement)
        {
            reason = "owner_not_reliable_for_medical_leash";
            return false;
        }

        float requestedLimit = Math.Max(0f, maxOwnerDistanceMeters);
        float effectiveLimit = requestedLimit;
        string hysteresisReason = "none";
        if (VanguardExecutionLeaseStore.TryGetActive(snapshot.BotProfileId, out var activeLease)
            && IsStationaryMedicalDistanceHysteresisEligible(activeLease))
        {
            effectiveLimit = Math.Max(requestedLimit, StationaryMedicalMaintainMaxOwnerDistanceMeters);
            hysteresisReason = "active_" + SafeLeaseToken(activeLease.IntentKey) + "_" + SafeLeaseToken(activeLease.WindowKind);
        }

        float distance = snapshot.SquadCohesion.OperatorDistanceToOwner;
        if (distance <= effectiveLimit)
        {
            reason = effectiveLimit > requestedLimit
                ? "inside_stationary_medical_hysteresis:ownerDistance=" + distance.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":admissionLimit=" + requestedLimit.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":maintainLimit=" + effectiveLimit.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":lease=" + hysteresisReason
                    + ":tag=" + VanguardRuntimeConvergenceStatusTags.StationaryMedicalHysteresis
                : "inside_stationary_medical_leash";
            return false;
        }

        reason = "rejoin_before_stationary_medical:ownerDistance=" + distance.ToString("0.0", CultureInfo.InvariantCulture)
            + ":admissionLimit=" + requestedLimit.ToString("0.0", CultureInfo.InvariantCulture)
            + ":effectiveLimit=" + effectiveLimit.ToString("0.0", CultureInfo.InvariantCulture)
            + ":hysteresis=" + hysteresisReason;
        return true;
    }

    private static bool IsStationaryMedicalDistanceHysteresisEligible(VanguardExecutionLeaseState lease)
    {
        if (lease == null)
        {
            return false;
        }

        string intent = lease.IntentKey ?? string.Empty;
        string window = lease.WindowKind ?? string.Empty;
        return intent.IndexOf("MedicalPrepareSurgeryCover", StringComparison.OrdinalIgnoreCase) >= 0
            || intent.IndexOf("StationaryMedicalSurgery", StringComparison.OrdinalIgnoreCase) >= 0
            || intent.IndexOf("ProposeStationarySurgery", StringComparison.OrdinalIgnoreCase) >= 0
            || window.IndexOf("MedicalPrepareSurgeryCover", StringComparison.OrdinalIgnoreCase) >= 0
            || window.IndexOf("StationaryMedicalSurgery", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string SafeLeaseToken(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }

    public static bool IsCombatProductive(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        // Vanguard: target relevance and a concrete SAIN execution are deliberately separate.
        // Shared visibility/can-shoot data may open or preserve a bounded combat window, but it
        // must never reset the no-progress clock by itself. Only an observable SAIN action does.
        if (IsSainConcreteCombatExecution(snapshot, out var executionReason))
        {
            reason = executionReason;
            return true;
        }

        reason = "no_concrete_combat_execution_signal";
        return false;
    }

    /// <summary>
    /// The runtime strong local execution evidence for long-range shared-contact authority. Generic Search,
    /// SeekCover or movement text is intentionally excluded: runtime qualification showed that those states can keep
    /// an isolated Operator busy around stale squad knowledge without producing local combat.
    /// </summary>
    public static bool IsStrongLocalCombatExecution(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        string text = SainText(snapshot);
        bool offensiveAction = text.Contains("shoot")
            || text.Contains("dogfight")
            || text.Contains("attack")
            || text.Contains("engage")
            || text.Contains("suppress")
            || text.Contains("fire")
            || text.Contains("grenade")
            || text.Contains("melee");
        if (offensiveAction)
        {
            reason = "sain_strong_local_offensive_execution:" + CompactCombatText(snapshot);
            return true;
        }

        reason = "no_strong_local_offensive_execution:" + CompactCombatText(snapshot);
        return false;
    }

    public static bool IsSainConcreteCombatExecution(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        string text = SainText(snapshot);
        bool offensiveAction = text.Contains("shoot")
            || text.Contains("dogfight")
            || text.Contains("attack")
            || text.Contains("engage")
            || text.Contains("suppress")
            || text.Contains("fire")
            || text.Contains("grenade")
            || text.Contains("melee");
        if (offensiveAction)
        {
            reason = "sain_offensive_execution:" + CompactCombatText(snapshot);
            return true;
        }

        float speed = Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed);
        bool pursuitLikeMovement = text.Contains("rush")
            || text.Contains("push")
            || text.Contains("flank")
            || text.Contains("chase")
            || text.Contains("pursuit")
            || text.Contains("move")
            || text.Contains("search")
            || text.Contains("seek");
        bool formationDetached = snapshot.SquadCohesion.OwnerKnown
            && snapshot.SquadCohesion.OwnerReliableForActiveMovement
            && snapshot.SquadCohesion.OwnerPosition.HasValue
            && snapshot.SquadCohesion.OperatorDistanceToOwner >= CombatCohesionForcedCatchupMeters;
        if (formationDetached
            && pursuitLikeMovement
            && speed >= 0.25f
            && !HasRecentDetachedPursuitProof(snapshot))
        {
            reason = "sain_detached_pursuit_without_recent_direct_proof:" + CompactCombatText(snapshot)
                + ":ownerDistance=" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", CultureInfo.InvariantCulture)
                + ":speed=" + speed.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        bool movementAction = snapshot.Sain.RunningToCover == true
            || text.Contains("cover")
            || pursuitLikeMovement
            || text.Contains("retreat");
        if (movementAction && speed >= 0.25f)
        {
            reason = "sain_combat_movement_execution:" + CompactCombatText(snapshot) + ":speed=" + speed.ToString("0.00", CultureInfo.InvariantCulture);
            return true;
        }

        bool freshReloadTransition = text.Contains("reload")
            && snapshot.Sain.TimeSinceDecisionChange.HasValue
            && snapshot.Sain.TimeSinceDecisionChange.Value <= 4.0f;
        if (freshReloadTransition)
        {
            reason = "sain_fresh_reload_transition";
            return true;
        }

        reason = "sain_no_concrete_execution:" + CompactCombatText(snapshot);
        return false;
    }

    private static bool HasRecentDetachedPursuitProof(OperatorDecisionSnapshot snapshot)
    {
        if (IsTrueDirectThreat(snapshot) || HasImmediateCombatAwareness(snapshot))
        {
            return true;
        }

        if (snapshot.Threat.EnemyVisible == true
            || snapshot.Threat.EnemyLineOfSight == true
            || snapshot.Brain.VanillaGoalEnemyVisible == true
            || snapshot.Brain.VanillaGoalEnemyCanShoot == true)
        {
            return true;
        }

        return snapshot.Threat.TimeSinceSeen.HasValue
            && snapshot.Threat.TimeSinceSeen.Value >= 0f
            && snapshot.Threat.TimeSinceSeen.Value <= 4.0f;
    }

    public static bool IsCombatRelevant(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        if (IsCombatProductive(snapshot, out var productiveReason))
        {
            reason = "productive:" + productiveReason;
            return true;
        }

        if (IsTrueDirectThreat(snapshot))
        {
            reason = "true_direct_threat_relevant";
            return true;
        }

        if (HasImmediateCombatAwareness(snapshot))
        {
            reason = "immediate_awareness_relevant";
            return true;
        }

        if (snapshot.Awareness.WouldPromoteSainTarget || snapshot.Awareness.WouldPropagateConfirmedThreat || snapshot.ThreatScan.WouldPromote)
        {
            reason = "awareness_or_scan_promotes_relevant";
            return true;
        }

        reason = "not_combat_relevant";
        return false;
    }

    public static bool ShouldHoldSectorForStaleSain(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        if (!snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement)
        {
            reason = "owner_unreliable";
            return false;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner > CombatCohesionHoldSectorMaxMeters)
        {
            reason = "outside_hold_sector_radius:" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", CultureInfo.InvariantCulture);
            return false;
        }

        if (IsCombatProductive(snapshot, out var productiveReason))
        {
            reason = "combat_productive:" + productiveReason;
            return true;
        }

        reason = "inside_support_radius_stale_hold_allowed";
        return true;
    }

    public static bool ShouldForceCatchupForStaleSain(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        if (!snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement || !snapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            reason = "owner_unreliable";
            return false;
        }

        float distance = snapshot.SquadCohesion.OperatorDistanceToOwner;
        if (distance < CombatCohesionForcedCatchupMeters)
        {
            reason = "inside_forced_catchup_band:" + distance.ToString("0.0", CultureInfo.InvariantCulture);
            return false;
        }

        if (IsCombatProductive(snapshot, out var productiveReason) && distance < CombatCohesionEmergencyReturnMeters)
        {
            reason = "combat_productive_not_forced:" + productiveReason;
            return false;
        }

        if (IsTrueDirectThreat(snapshot) && distance < CombatCohesionEmergencyReturnMeters)
        {
            reason = "true_direct_threat_not_forced";
            return false;
        }

        if (IsStationaryMedicalAuthority(snapshot))
        {
            reason = "stationary_medical_authority";
            return false;
        }

        reason = "distance_requires_combat_ready_catchup:" + distance.ToString("0.0", CultureInfo.InvariantCulture);
        return true;
    }

    public static bool ShouldPreemptWeakCohesionForHardReturn(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        if (!snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement || !snapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            reason = "owner_unreliable";
            return false;
        }

        float distance = snapshot.SquadCohesion.OperatorDistanceToOwner;
        if (distance < CombatCohesionHardReturnMeters)
        {
            reason = "below_hard_return_band:" + distance.ToString("0.0", CultureInfo.InvariantCulture);
            return false;
        }

        if (IsCombatProductive(snapshot, out var productiveReason) && distance < CombatCohesionEmergencyReturnMeters)
        {
            reason = "combat_productive_protected:" + productiveReason;
            return false;
        }

        if (IsTrueDirectThreat(snapshot) && distance < CombatCohesionEmergencyReturnMeters)
        {
            reason = "true_direct_threat_protected";
            return false;
        }

        reason = "weak_cohesion_preempted_for_combat_ready_return:" + distance.ToString("0.0", CultureInfo.InvariantCulture);
        return true;
    }

    public static bool NeedsTacticalVolumeJoin(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.Alive || !snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement)
        {
            return false;
        }

        float direct = snapshot.SquadCohesion.OwnerToOperatorDirectDistance > 0.1f
            ? snapshot.SquadCohesion.OwnerToOperatorDirectDistance
            : snapshot.SquadCohesion.OperatorDistanceToOwner;
        float path = snapshot.SquadCohesion.OwnerToOperatorPathDistance;
        bool directBand = snapshot.SquadCohesion.OperatorDistanceToOwner >= TacticalVolumeJoinStartMinMeters
            && snapshot.SquadCohesion.OperatorDistanceToOwner <= TacticalVolumeJoinStartMaxMeters;
        bool verticalSplit = Math.Abs(snapshot.SquadCohesion.VerticalDelta) >= TacticalVolumeJoinVerticalDeltaMeters;
        bool pathSplit = path > 0.1f
            && (snapshot.SquadCohesion.OwnerToOperatorPathRatio >= TacticalVolumeJoinPathRatio
                || path >= direct + TacticalVolumeJoinPathExtraMeters
                || snapshot.SquadCohesion.OwnerToOperatorPathCorners >= 12);
        string topology = snapshot.SquadCohesion.SectorTopologyReason ?? string.Empty;
        string env = snapshot.SquadCohesion.TacticalEnvironmentKind ?? string.Empty;
        bool volumeHint = topology.IndexOf("detour", StringComparison.OrdinalIgnoreCase) >= 0
            || topology.IndexOf("different", StringComparison.OrdinalIgnoreCase) >= 0
            || topology.IndexOf("not_same", StringComparison.OrdinalIgnoreCase) >= 0
            || topology.IndexOf("wraparound", StringComparison.OrdinalIgnoreCase) >= 0
            || env.IndexOf("corridor", StringComparison.OrdinalIgnoreCase) >= 0
            || env.IndexOf("room", StringComparison.OrdinalIgnoreCase) >= 0
            || env.IndexOf("urban_wraparound", StringComparison.OrdinalIgnoreCase) >= 0;
        return directBand && (verticalSplit || pathSplit || (volumeHint && pathSplit));
    }

    public static bool IsSainExtractLike(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return false;
        }

        string text = SainText(snapshot);
        return text.Contains("exfiltration")
            || text.Contains("gotoexfiltrationpoint")
            || text.Contains("go_to_exfil")
            || text.Contains("moving_to_extract")
            || text.Contains("extractaction")
            || text.Contains("sain_extract");
    }

    public static bool IsSainSearchLike(OperatorDecisionSnapshot snapshot)
    {
        if (IsSainExtractLike(snapshot))
        {
            return false;
        }

        string text = SainText(snapshot);
        return text.Contains("search")
            || text.Contains("seek")
            || text.Contains("rush")
            || text.Contains("push")
            || text.Contains("chase")
            || text.Contains("pursuit")
            || snapshot.Sain.Searching == true
            || string.Equals(snapshot.Sain.Classification, "sain_search", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSainLocalDefensiveLike(OperatorDecisionSnapshot snapshot)
    {
        string text = SainText(snapshot);
        return text.Contains("cover")
            || text.Contains("hold")
            || text.Contains("angle")
            || text.Contains("dogfight")
            || text.Contains("shoot")
            || string.Equals(snapshot.Sain.Classification, "sain_cover_move", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasStaleOrNonActionableTarget(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return false;
        }

        bool notVisible = snapshot.Threat.EnemyVisible == false || snapshot.Threat.EnemyLineOfSight == false;
        bool cannotShoot = snapshot.Threat.EnemyCanShoot != true;
        bool far = snapshot.Threat.Distance.HasValue && snapshot.Threat.Distance.Value >= StaleSearchDistanceMeters;
        bool noFreshExchange = snapshot.Threat.ShotMeRecently != true
            && snapshot.Threat.ShotAtMeRecently != true
            && !snapshot.Medical.Safety.IncomingFireRecent
            && !snapshot.Awareness.IncomingFireFresh
            && !snapshot.ThreatScan.CandidateIncomingFireFresh
            && !snapshot.ThreatScan.CandidateShotMeRecently
            && !snapshot.ThreatScan.CandidateShotAtMeRecently;
        return snapshot.Threat.StaleThreat || ((notVisible || far) && cannotShoot && noFreshExchange);
    }

    public static bool IsSainCombatStaleNonActionable(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        if (IsSainExtractLike(snapshot))
        {
            reason = "sain_extract_is_not_combat_authority";
            return false;
        }

        if (IsTrueDirectThreat(snapshot) || HasImmediateCombatAwareness(snapshot))
        {
            reason = "fresh_or_direct_threat_keeps_sain";
            return false;
        }

        if (!IsSainSearchLike(snapshot) && !string.Equals(snapshot.Sain.Classification, "sain_direct_combat", StringComparison.OrdinalIgnoreCase))
        {
            reason = "sain_not_search_or_combat_like";
            return false;
        }

        bool targetInvisible = snapshot.Threat.EnemyVisible != true
            && snapshot.Threat.EnemyLineOfSight != true
            && !snapshot.Awareness.CandidateVisible
            && !snapshot.Awareness.CandidateLineOfSight
            && !snapshot.ThreatScan.CandidateVisible
            && !snapshot.ThreatScan.CandidateLineOfSight;
        bool targetCannotShoot = snapshot.Threat.EnemyCanShoot != true
            && !snapshot.Awareness.CandidateCanShoot
            && !snapshot.ThreatScan.CandidateCanShoot
            && !snapshot.Medical.Safety.EnemyCanShoot;
        bool noRecentFireExchange = snapshot.Threat.ShotMeRecently != true
            && snapshot.Threat.ShotAtMeRecently != true
            && !snapshot.Medical.Safety.IncomingFireRecent
            && !snapshot.Awareness.IncomingFireFresh
            && !snapshot.ThreatScan.CandidateIncomingFireFresh
            && !snapshot.ThreatScan.CandidateShotMeRecently
            && !snapshot.ThreatScan.CandidateShotAtMeRecently;
        bool staleByThreat = snapshot.Threat.StaleThreat || HasStaleOrNonActionableTarget(snapshot);
        if (targetInvisible && targetCannotShoot && noRecentFireExchange && staleByThreat)
        {
            reason = "sain_stale_non_actionable:no_visible_target:no_can_shoot:no_recent_fire";
            return true;
        }

        reason = "sain_target_still_potentially_actionable";
        return false;
    }

    public static bool IsCombatNoFireRecoverable(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        bool actionableVisible = snapshot.Threat.EnemyVisible == true
            || snapshot.Threat.EnemyLineOfSight == true
            || snapshot.ThreatScan.CandidateVisible
            || snapshot.ThreatScan.CandidateLineOfSight
            || snapshot.Awareness.CandidateVisible
            || snapshot.Awareness.CandidateLineOfSight;
        bool actionableCanShoot = snapshot.Threat.EnemyCanShoot == true
            || snapshot.ThreatScan.CandidateCanShoot
            || snapshot.Awareness.CandidateCanShoot;
        float distance = snapshot.Threat.Distance
            ?? snapshot.ThreatScan.CandidateDistance
            ?? float.MaxValue;
        bool closeEnough = distance <= CombatNoFireActionableDistanceMeters;
        bool sainCombatLike = string.Equals(snapshot.Sain.Classification, "sain_direct_combat", StringComparison.OrdinalIgnoreCase)
            || IsSainLocalDefensiveLike(snapshot)
            || snapshot.Sain.IsInCombat == true
            || ContainsCombatText(snapshot.Sain.ActiveLayer)
            || ContainsCombatText(snapshot.Sain.CurrentAction)
            || ContainsCombatText(snapshot.Brain.ActiveLayer)
            || ContainsCombatText(snapshot.Brain.Node);

        if (!sainCombatLike)
        {
            reason = "sain_not_combat_like";
            return false;
        }

        if (!actionableVisible && !actionableCanShoot)
        {
            reason = "target_not_actionable_visible";
            return false;
        }

        if (!closeEnough && !actionableCanShoot)
        {
            reason = "target_too_far_without_can_shoot";
            return false;
        }

        if (snapshot.Medical.Actionability.GrenadeThrowing || snapshot.Medical.Actionability.Reloading)
        {
            reason = "hands_temporarily_busy_reload_or_grenade";
            return false;
        }

        reason = "combat_posture_actionable_no_fire_watch";
        return true;
    }

    public static bool ShouldBreakContactBeforeMedical(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive || !snapshot.Medical.Need.HasAnyNeed)
        {
            reason = "no_active_medical_need";
            return false;
        }

        bool serious = snapshot.Medical.Need.HasHeavyBleed
            || snapshot.Medical.Need.HealthPercent <= 55
            || snapshot.Medical.Need.HasDestroyedPart
            || snapshot.Medical.Need.HasBlackBroken;
        if (!serious)
        {
            reason = "medical_need_not_serious";
            return false;
        }

        float distance = snapshot.Medical.Safety.ThreatDistance
            ?? snapshot.Threat.Distance
            ?? snapshot.ThreatScan.CandidateDistance
            ?? float.MaxValue;
        bool closeThreat = distance <= MedicalBreakContactThreatMeters;
        bool enemyCanShoot = snapshot.Medical.Safety.EnemyCanShoot
            || snapshot.Threat.EnemyCanShoot == true
            || snapshot.ThreatScan.CandidateCanShoot
            || snapshot.Awareness.CandidateCanShoot;
        bool freshFire = snapshot.Medical.Safety.IncomingFireRecent
            || snapshot.Awareness.IncomingFireFresh
            || snapshot.ThreatScan.CandidateIncomingFireFresh
            || snapshot.Threat.ShotMeRecently == true
            || snapshot.Threat.ShotAtMeRecently == true;

        bool closeVisibleThreat = closeThreat && (snapshot.Threat.EnemyVisible == true || snapshot.Threat.EnemyLineOfSight == true || snapshot.Awareness.CandidateVisible || snapshot.Awareness.CandidateLineOfSight || snapshot.ThreatScan.CandidateVisible || snapshot.ThreatScan.CandidateLineOfSight);
        if ((enemyCanShoot || freshFire || snapshot.Medical.Safety.ImmediateCombatBlock || closeVisibleThreat) && closeThreat)
        {
            reason = closeVisibleThreat && !enemyCanShoot && !freshFire
                ? "serious_medical_need_requires_cover_before_visible_close_contact"
                : "serious_medical_need_requires_break_contact_before_aid";
            return true;
        }

        reason = "medical_contact_not_close_or_not_actionable";
        return false;
    }


    private static bool ContainsCombatText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string lower = value.ToLowerInvariant();
        return lower.Contains("combat") || lower.Contains("shoot") || lower.Contains("cover") || lower.Contains("attack") || lower.Contains("enemy");
    }

    public static bool IsSainEnvelopeViolation(OperatorDecisionSnapshot snapshot, out string reason)
    {
        string order = snapshot.SquadCohesion.SquadOrder;
        if (IsGoOrder(order))
        {
            reason = "go_order_allows_offensive_search";
            return false;
        }

        if (IsTrueDirectThreat(snapshot))
        {
            reason = "true_direct_threat_allows_sain";
            return false;
        }

        if (!IsSainSearchLike(snapshot))
        {
            reason = IsSainLocalDefensiveLike(snapshot) ? "local_defensive_sain_allowed" : "sain_not_search_like";
            return false;
        }

        if (!snapshot.SquadCohesion.InBubble && snapshot.SquadCohesion.OperatorDistanceToOwner >= TacticalBubbleMeters)
        {
            reason = "sain_search_outside_tactical_bubble";
            return true;
        }

        if (HasStaleOrNonActionableTarget(snapshot))
        {
            reason = "sain_search_stale_or_non_actionable_target";
            return true;
        }

        reason = "sain_search_monitored_inside_bubble";
        return false;
    }


    public static bool IsSainBoundaryReturnEligible(OperatorDecisionSnapshot snapshot, out string reason)
    {
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_not_alive";
            return false;
        }

        if (!ActiveBackendApplyEnabled || !ActiveSainBoundaryReturnEnabled)
        {
            reason = "movement_f12_sain_boundary_return_disabled";
            return false;
        }

        if (IsGoOrder(snapshot.SquadCohesion.SquadOrder))
        {
            reason = "go_order_allows_sain_offensive_search";
            return false;
        }

        if (IsTrueDirectThreat(snapshot))
        {
            reason = "true_direct_threat_keeps_sain_authority";
            return false;
        }

        if (!snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement)
        {
            reason = "owner_anchor_unreliable";
            return false;
        }

        if (!snapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            reason = "owner_position_missing";
            return false;
        }

        bool staleInsideOrOutside = IsSainCombatStaleNonActionable(snapshot, out var staleReason);
        if (snapshot.SquadCohesion.OperatorDistanceToOwner < HardCorrectionMeters && !staleInsideOrOutside)
        {
            reason = "not_hard_outside_bubble_and_not_stale:" + staleReason;
            return false;
        }

        if (!IsSainSearchLike(snapshot) && !staleInsideOrOutside)
        {
            reason = "sain_not_search_like";
            return false;
        }

        if (!IsSainEnvelopeViolation(snapshot, out var violationReason))
        {
            reason = "sain_envelope_not_violated:" + violationReason;
            return false;
        }

        if (!HasStaleOrNonActionableTarget(snapshot) && !staleInsideOrOutside)
        {
            reason = "sain_target_still_actionable";
            return false;
        }

        reason = staleInsideOrOutside ? staleReason : violationReason + ":stale_or_non_actionable";
        return true;
    }

    public static string MovementOwner(OperatorDecisionSnapshot snapshot, bool sainViolation)
    {
        if (IsTrueDirectThreat(snapshot))
        {
            return "SAIN_DIRECT_COMBAT";
        }

        if (IsSainExtractLike(snapshot))
        {
            return "SAIN_AUTONOMOUS_EXTRACT_VETO";
        }

        if (IsStationaryMedicalAuthority(snapshot))
        {
            return "VANGUARD_MEDICAL";
        }

        if (sainViolation)
        {
            return "SAIN_OUT_OF_ENVELOPE_READONLY";
        }

        if (snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true)
        {
            return "LOOTINGBOTS";
        }

        if (snapshot.Orbit.Active)
        {
            return "ORBIT";
        }

        if (snapshot.Movement.HasPath == true)
        {
            return "EFT_PATH";
        }

        return "IDLE_OR_VANGUARD_OBSERVE";
    }

    private static string CompactCombatText(OperatorDecisionSnapshot snapshot)
    {
        string value = string.Join("_", new[]
        {
            snapshot?.Sain.CurrentAction,
            snapshot?.Sain.CombatDecision,
            snapshot?.Sain.SquadDecision,
            snapshot?.Sain.SelfDecision
        }.Where(part => !string.IsNullOrWhiteSpace(part) && !string.Equals(part, "none", StringComparison.OrdinalIgnoreCase)));
        return string.IsNullOrWhiteSpace(value) ? "none" : value.Replace(' ', '_').Replace(';', '_').Replace('|', '_');
    }

    private static string SainText(OperatorDecisionSnapshot snapshot)
    {
        return string.Join("|",
            snapshot.Sain.Classification,
            snapshot.Sain.ActiveLayer,
            snapshot.Sain.CurrentAction,
            snapshot.Sain.CombatDecision,
            snapshot.Sain.SquadDecision,
            snapshot.Sain.SelfDecision,
            snapshot.Brain.ActiveLayer,
            snapshot.Brain.Node,
            snapshot.Brain.CustomAction,
            snapshot.Brain.Classification).ToLowerInvariant();
    }
}
#endif
