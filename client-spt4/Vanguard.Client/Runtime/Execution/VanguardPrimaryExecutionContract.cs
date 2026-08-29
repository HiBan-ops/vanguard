#if SPT_CLIENT
using System;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Defines the shared contract and classification rules used by all domains competing for the Operator primary execution window.
// Flow: Intent kind, priority, ownership class, preemption semantics and completion/release metadata are normalized here so scheduler and lease state can compare otherwise unrelated medical, loot, movement and tactical work.
// Authority boundary: The contract describes arbitration semantics only; domain policy creates intents and domain executors perform physical actions.
// Invariant: Incompatible primary actions remain mutually exclusive, priority ordering is stable, and every acquired contract has an explicit release/terminal path.
namespace Vanguard.Client.Runtime.Execution;

/// <summary>
/// Central execution ownership contract: one primary task owns the Operator, while only
/// explicitly compatible sidecars may run beside it.  This is intentionally small and central so
/// movement, combat and medical executors do not each invent their own preemption rules.
/// </summary>
internal static class VanguardPrimaryExecutionContract
{
    public const string StatusTag = "VANGUARD_EXECUTION_CONTRACT_STATUS";
    public const string CohesionContractStatusTag = "VANGUARD_COHESION_MOVEMENT_CONTRACT_STATUS";
    public const string CombatClosureStatusTag = "VANGUARD_COMBAT_WINDOW_CLOSURE_STATUS";
    public const string FormationLaneStatusTag = "VANGUARD_FORMATION_LANES_STATUS";
    public const string CombatLifecycleStatusTag = "VANGUARD_COMBAT_LIFECYCLE_STATUS";
    public const string PipelineLogFilterStatusTag = "VANGUARD_PIPELINE_LOG_FILTER_STATUS";
    public const string CohesionChurnThrottleStatusTag = "VANGUARD_COHESION_CHURN_THROTTLE_STATUS";
    public const string AwarenessCombatSupportStatusTag = "VANGUARD_AWARENESS_COMBAT_SUPPORT_STATUS";
    public const string LanePreservingFallbackStatusTag = "VANGUARD_LANE_PRESERVING_FALLBACK_STATUS";
    public const string VanguardMovementDriverStatusTag = "VANGUARD_VANGUARD_MOVEMENT_DRIVER_STATUS";
    public const string SainWindowStatusTag = "VANGUARD_SAIN_WINDOW_STATUS";
    public const string CombatTargetChainStatusTag = "VANGUARD_COMBAT_CHAIN_STATUS";
    public const string OpportunisticMedicalStatusTag = "VANGUARD_OPPORTUNISTIC_MEDICAL_STATUS";
    public const string DynamicFormationStatusTag = "VANGUARD_DYNAMIC_FORMATION_STATUS";
    public const string InteriorCoverageStatusTag = "VANGUARD_INTERIOR_COVERAGE_STATUS";
    public const string TargetChainIdempotenceStatusTag = "VANGUARD_TARGET_CHAIN_IDEMPOTENCE_STATUS";
    public const string FriendlyFireSafetyStatusTag = "VANGUARD_FRIENDLY_FIRE_SAFETY_STATUS";
    public const string InteriorVolumeSecurityStatusTag = "VANGUARD_INTERIOR_VOLUME_SECURITY_STATUS";
    public const string StationarySpatialTacticalPlacementStatusTag = "VANGUARD_STATIONARY_SPATIAL_TACTICAL_PLACEMENT_STATUS";
    public const string CanonicalStationaryAdmissionStatusTag = "VANGUARD_CANONICAL_STATIONARY_ADMISSION_STATUS";
    public const string OwnerCenteredEnvironmentAndStableVolumesStatusTag = "VANGUARD_OWNER_CENTERED_ENVIRONMENT_AND_STABLE_VOLUMES_STATUS";
    public const string StableEnvironmentAtomicInteriorDeploymentStatusTag = "VANGUARD_STABLE_ENVIRONMENT_ATOMIC_INTERIOR_DEPLOYMENT_STATUS";
    public const string ClientBuildCorrectionStatusTag = "VANGUARD_CLIENT_BUILD_CORRECTION_STATUS";
    public const string AuditProfileStatusTag = "VANGUARD_AUDIT_PROFILE_STATUS";
    public const string TacticalStabilityStatusTag = "VANGUARD_TACTICAL_STABILITY_STATUS";
    public const string AuthorityIntegrityStatusTag = "VANGUARD_AUTHORITY_INTEGRITY_STATUS";
    public const string InteriorAreaSecurityStatusTag = "VANGUARD_INTERIOR_AREA_SECURITY_STATUS";
    public const string PredictiveCohesionStatusTag = "VANGUARD_PREDICTIVE_COHESION_STATUS";
    public const string AwarenessCommitStatusTag = "VANGUARD_AWARENESS_COMMIT_STATUS";
    public const string RegressionGuardStatusTag = "VANGUARD_REGRESSION_GUARD_STATUS";
    public const string EffectiveMedicalAuthorityStatusTag = "VANGUARD_EFFECTIVE_MEDICAL_AUTHORITY_STATUS";
    public const string MedicalOutcomeTruthStatusTag = "VANGUARD_MEDICAL_OUTCOME_TRUTH_STATUS";
    public const string InteriorExecutableMissionStatusTag = "VANGUARD_INTERIOR_EXECUTABLE_MISSION_STATUS";
    public const string SainTargetVerificationStatusTag = "VANGUARD_SAIN_TARGET_VERIFICATION_STATUS";
    public const string PredictiveRegressionStatusTag = "VANGUARD_PREDICTIVE_REGRESSION_STATUS";
    public const string MovementRetargetStatusTag = "VANGUARD_MOVEMENT_RETARGET_STATUS";
    public const string HardReturnCompletionStatusTag = "VANGUARD_HARD_RETURN_COMPLETION_STATUS";
    public const string InteriorCandidateRecoveryStatusTag = "VANGUARD_INTERIOR_CANDIDATE_STATUS";
    public const string SpawnDiagnosticsStatusTag = "VANGUARD_SPAWN_DIAGNOSTICS_STATUS";
    public const string SafetyLocalityGuardStatusTag = "VANGUARD_SAFETY_LOCALITY_GUARD_STATUS";
    public const string PerShotFriendlyFireStatusTag = "VANGUARD_PER_SHOT_FRIENDLY_FIRE_STATUS";
    public const string LocalCombatAuthorityStatusTag = "VANGUARD_LOCAL_COMBAT_AUTHORITY_STATUS";
    public const string StationaryMedicalLeashStatusTag = "VANGUARD_STATIONARY_MEDICAL_LEASH_STATUS";
    public const string InteriorPathContractStatusTag = "VANGUARD_INTERIOR_PATH_CONTRACT_STATUS";
    public const string PhysicalDestackStatusTag = "VANGUARD_PHYSICAL_DESTACK_STATUS";
    public const string SafeRuntimeBindScanStatusTag = "VANGUARD_SAFE_RUNTIME_BIND_SCAN_STATUS";
    public const string SpawnSyncGuardStatusTag = "VANGUARD_SPAWN_SYNC_GUARD_STATUS";
    public const string ExecutionStabilityGuardStatusTag = "VANGUARD_EXECUTION_STABILITY_GUARD_STATUS";
    public const string CombatWindowEdgeGuardStatusTag = "VANGUARD_COMBAT_WINDOW_EDGE_GUARD_STATUS";
    public const string PhysicalMovementProgressStatusTag = "VANGUARD_PHYSICAL_MOVEMENT_PROGRESS_STATUS";
    public const string MedicalAuthorityFastPathStatusTag = "VANGUARD_MEDICAL_AUTHORITY_FAST_PATH_STATUS";
    public const string TypedAwarenessChurnStatusTag = "VANGUARD_TYPED_AWARENESS_CHURN_STATUS";
    public const string RuntimeConvergenceGuardStatusTag = "VANGUARD_RUNTIME_CONVERGENCE_GUARD_STATUS";
    public const string HardReturnPhysicalProgressStatusTag = "VANGUARD_HARD_RETURN_PHYSICAL_PROGRESS_STATUS";
    public const string IsolatedCombatBackoffStatusTag = "VANGUARD_ISOLATED_COMBAT_BACKOFF_STATUS";
    public const string MedicalPhaseProfilingStatusTag = "VANGUARD_MEDICAL_PHASE_PROFILING_STATUS";
    public const string AwarenessTypedEpisodeStatusTag = "VANGUARD_AWARENESS_TYPED_EPISODE_STATUS";
    public const string DiagnosticNoiseGuardStatusTag = "VANGUARD_DIAGNOSTIC_NOISE_GUARD_STATUS";

    public static bool IsGrenadeEmergency(VanguardPrimaryExecutionWindowState? window)
    {
        return window != null && IsGrenadeEmergencyKind(window.WindowKind);
    }

    public static bool IsGrenadeEmergencyKind(string? windowKind)
    {
        return string.Equals(windowKind, VanguardPrimaryExecutionWindowKinds.EmergencyGrenadeEvasion, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCombatPrimary(VanguardPrimaryExecutionWindowState? window)
    {
        return window != null && string.Equals(window.WindowKind, VanguardPrimaryExecutionWindowKinds.SainCombatRelease, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCombatPrimaryKind(string? windowKind)
    {
        return string.Equals(windowKind, VanguardPrimaryExecutionWindowKinds.SainCombatRelease, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMobileMedicalKind(string? windowKind)
    {
        return !string.IsNullOrWhiteSpace(windowKind)
            && windowKind.IndexOf("MobileMedical", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static bool IsStationaryMedicalKind(string? windowKind)
    {
        return !string.IsNullOrWhiteSpace(windowKind)
            && windowKind.IndexOf("Medical", StringComparison.OrdinalIgnoreCase) >= 0
            && !IsMobileMedicalKind(windowKind);
    }

    public static bool IsMovementPrimaryKind(string? windowKind)
    {
        return string.Equals(windowKind, VanguardPrimaryExecutionWindowKinds.HardReturnMovement, StringComparison.OrdinalIgnoreCase)
            || string.Equals(windowKind, VanguardPrimaryExecutionWindowKinds.TacticalMovement, StringComparison.OrdinalIgnoreCase)
            || string.Equals(windowKind, VanguardPrimaryExecutionWindowKinds.AuthoringPreviewMovement, StringComparison.OrdinalIgnoreCase)
            || string.Equals(windowKind, VanguardPrimaryExecutionWindowKinds.CloseCohesionMovement, StringComparison.OrdinalIgnoreCase)
            || string.Equals(windowKind, VanguardPrimaryExecutionWindowKinds.CorpseLoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(windowKind, VanguardPrimaryExecutionWindowKinds.WorldContainerLoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(windowKind, VanguardPrimaryExecutionWindowKinds.Rejoin, StringComparison.OrdinalIgnoreCase);
    }


    public static bool ShouldTerminateWindowForMissingOrDeadSnapshot(OperatorDecisionSnapshot? snapshot, VanguardPrimaryExecutionWindowState? active, out string reason)
    {
        reason = "none";
        if (active == null)
        {
            reason = "active_window_missing";
            return false;
        }

        if (snapshot == null)
        {
            reason = "snapshot_missing";
            return true;
        }

        if (!snapshot.Alive)
        {
            reason = "operator_dead";
            return true;
        }

        reason = "operator_alive";
        return false;
    }

    public static bool ShouldPreserveCombatWindowForFreshThreat(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        if (VanguardMovementAuthorityDoctrine.IsCombatProductive(snapshot, out var productiveReason))
        {
            reason = "productive:" + productiveReason;
            return true;
        }

        if (VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(snapshot))
        {
            reason = "relevant_but_not_productive:immediate_awareness_or_incoming_fire";
            return true;
        }

        reason = "no_fresh_threat_or_productive_signal";
        return false;
    }

    public static bool ShouldCleanupNonProductiveCombatWindow(OperatorDecisionSnapshot snapshot, VanguardPrimaryExecutionWindowState active, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (snapshot == null || active == null || !snapshot.Alive)
        {
            reason = "snapshot_or_window_missing";
            return false;
        }

        if (!IsCombatPrimaryKind(active.WindowKind))
        {
            reason = "not_combat_primary";
            return false;
        }

        if (VanguardMovementAuthorityDoctrine.IsCombatProductive(snapshot, out var productiveReason))
        {
            reason = "productive:" + productiveReason;
            return false;
        }

        double quietSeconds = Math.Max(0.0d, (now - active.LastProgressAtUtc).TotalSeconds);
        if (quietSeconds < VanguardMovementAuthorityDoctrine.CombatNoProductionCleanupSeconds)
        {
            reason = "quiet_window_not_elapsed:" + quietSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            return false;
        }

        reason = "combat_no_production_cleanup:quietSeconds=" + quietSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    public static bool IsMobileMedicalSidecarCandidate(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        var plan = snapshot.Medical.Plan;
        var need = snapshot.Medical.Need;
        if (!need.HasAnyNeed)
        {
            reason = "no_medical_need";
            return false;
        }

        if (!plan.Readable || !plan.WouldAllowMobile)
        {
            reason = "plan_not_mobile_compatible:" + Safe(plan.NextStep) + ":mobile=" + Bool(plan.WouldAllowMobile);
            return false;
        }

        if (plan.WouldRequireStationary || plan.WouldRequireMovement)
        {
            reason = "plan_requires_primary_movement_or_stationary:" + Safe(plan.NextStep);
            return false;
        }

        string action = snapshot.Medical.Actionability.SelectedItemActionKind ?? string.Empty;
        string step = plan.NextStep ?? string.Empty;
        bool compatibleNeed = need.HasHeavyBleed
            || need.HasLightBleed
            || need.HasHpDamage
            || need.HasPain
            || need.HasTremor
            || action.IndexOf("stim", StringComparison.OrdinalIgnoreCase) >= 0
            || action.IndexOf("first", StringComparison.OrdinalIgnoreCase) >= 0
            || step.IndexOf("MobileMedical", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!compatibleNeed)
        {
            reason = "need_not_mobile_sidecar_compatible:" + Safe(need.DominantNeed.ToString());
            return false;
        }

        if (!snapshot.Medical.Actionability.RequiredItemAvailable || snapshot.Medical.Actionability.CanApplyItem == false)
        {
            reason = "item_not_actionable:item=" + Safe(snapshot.Medical.Actionability.SelectedItemName) + ":canApply=" + NullableBool(snapshot.Medical.Actionability.CanApplyItem);
            return false;
        }

        if (snapshot.Medical.Actionability.Reloading || snapshot.Medical.Actionability.GrenadeThrowing)
        {
            reason = "hands_busy_reload_or_grenade";
            return false;
        }

        if (!snapshot.Medical.Safety.SafeForMobileAid)
        {
            reason = "medical_safety_rejects_mobile_aid";
            return false;
        }

        bool incomingFire = snapshot.Medical.Safety.IncomingFireRecent
            || snapshot.Threat.ShotMeRecently == true
            || snapshot.Threat.ShotAtMeRecently == true;
        bool enemyCanShoot = snapshot.Threat.EnemyCanShoot == true
            || snapshot.Brain.VanillaGoalEnemyCanShoot == true
            || snapshot.Medical.Safety.EnemyCanShoot;
        if (enemyCanShoot || snapshot.Medical.Safety.ImmediateCombatBlock)
        {
            reason = "mobile_aid_would_preempt_direct_enemy_fire";
            return false;
        }

        if (incomingFire && !snapshot.Medical.Safety.CoveredSuppressionOpportunity)
        {
            reason = "incoming_fire_without_covered_suppression_opportunity";
            return false;
        }

        reason = "mobile_medical_sidecar_allowed:" + Safe(plan.NextStep);
        return true;
    }

    public static bool IsCombatMicroAidOpportunity(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (!IsMobileMedicalSidecarCandidate(snapshot, out var sidecarReason))
        {
            reason = "base_sidecar_rejected:" + sidecarReason;
            return false;
        }

        bool incomingFire = snapshot.Medical.Safety.IncomingFireRecent
            || snapshot.Threat.ShotMeRecently == true
            || snapshot.Threat.ShotAtMeRecently == true
            || snapshot.Awareness.IncomingFireFresh
            || snapshot.ThreatScan.CandidateIncomingFireFresh;
        bool localVisualDanger = snapshot.Threat.EnemyVisible == true
            || snapshot.Threat.EnemyLineOfSight == true
            || snapshot.Threat.EnemyCanShoot == true
            || snapshot.Brain.VanillaGoalEnemyVisible == true
            || snapshot.Brain.VanillaGoalEnemyCanShoot == true;
        if (incomingFire || localVisualDanger || snapshot.Medical.Safety.ImmediateCombatBlock)
        {
            reason = "direct_danger_present:incoming=" + Bool(incomingFire) + ":visual=" + Bool(localVisualDanger);
            return false;
        }

        string combatDecision = snapshot.Sain.CombatDecision ?? string.Empty;
        string action = snapshot.Sain.CurrentAction ?? string.Empty;
        string selfDecision = snapshot.Sain.SelfDecision ?? string.Empty;
        if (ContainsAny(combatDecision, "StandAndShoot", "DogFight", "Rush", "MoveToEngage", "ThrowGrenade", "Shoot")
            || ContainsAny(action, "StandAndShoot", "DogFight", "Rush", "MoveToEngage", "ThrowGrenade", "Shoot")
            || ContainsAny(selfDecision, "Reload", "Grenade"))
        {
            reason = "offensive_or_weapon_action_in_progress:" + Safe(combatDecision) + ":" + Safe(action) + ":" + Safe(selfDecision);
            return false;
        }

        if (snapshot.RealSpeed > 2.25f)
        {
            reason = "movement_too_fast_for_micro_aid:" + snapshot.RealSpeed.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            return false;
        }

        var need = snapshot.Medical.Need;
        if (need.HasHeavyBleed || need.HasLightBleed)
        {
            reason = "bleed_micro_aid_opportunity:" + sidecarReason;
            return true;
        }

        bool coveredPause = snapshot.Medical.Safety.CoveredOrHoldingAngle
            || snapshot.Medical.Safety.CoveredSuppressionOpportunity
            || snapshot.Sain.Searching == true
            || snapshot.RealSpeed <= 0.35f;
        if (need.DominantNeed == Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.PainMobility && coveredPause)
        {
            reason = "pain_mobility_micro_aid_opportunity:" + sidecarReason;
            return true;
        }

        if (need.HasHpDamage && need.HealthPercent <= 86 && coveredPause && snapshot.RealSpeed <= 0.75f)
        {
            reason = "bounded_hp_micro_aid_opportunity:hp=" + need.HealthPercent.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + sidecarReason;
            return true;
        }

        reason = "minor_need_without_stable_safe_pause";
        return false;
    }

    public static bool ShouldKeepMovementContractUntilTerminal(OperatorDecisionSnapshot snapshot, string requestKind, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot)
            || VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(snapshot)
            || snapshot.Threat.DirectThreat
            || snapshot.Sain.IsInCombat == true)
        {
            reason = "combat_interrupt_allowed";
            return false;
        }

        if (snapshot.MovementAuthority.HardOutsideBubble
            && snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.HardCorrectionMeters + 18.0f)
        {
            reason = "extreme_distance_interrupt_allowed:" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            return false;
        }

        if (snapshot.MovementAuthority.MovementStallSuspect && snapshot.RealSpeed <= 0.12f)
        {
            reason = "stuck_interrupt_allowed";
            return false;
        }

        reason = "movement_contract_must_finish_before_replan:" + Safe(requestKind);
        return true;
    }

    public static bool IsIndoorPerimeterHoldCandidate(OperatorDecisionSnapshot snapshot, bool ownerStationary, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        if (!ownerStationary)
        {
            reason = "owner_not_stationary";
            return false;
        }

        if (!IsIndoor(snapshot))
        {
            reason = "not_indoor_context:" + Safe(snapshot.SquadCohesion.TacticalEnvironmentKind);
            return false;
        }

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot) || VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(snapshot))
        {
            reason = "combat_interrupts_indoor_hold";
            return false;
        }

        if (!VanguardInteriorSecurityPlanner.IsVerifiedCoverageHold(snapshot, DateTimeOffset.UtcNow, out var coverageReason))
        {
            reason = "no_verified_access_coverage:" + coverageReason;
            return false;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner >= 16.0f)
        {
            reason = "operator_too_far_for_verified_perimeter:" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            return false;
        }

        reason = "verified_indoor_access_coverage:" + coverageReason;
        return true;
    }

    public static bool IsIndoor(OperatorDecisionSnapshot snapshot)
    {
        string env = snapshot?.SquadCohesion?.TacticalEnvironmentKind ?? string.Empty;
        string mode = snapshot?.SquadCohesion?.TacticalPlacementMode ?? string.Empty;
        string topology = snapshot?.SquadCohesion?.SectorTopologyReason ?? string.Empty;
        return env.IndexOf("indoor", StringComparison.OrdinalIgnoreCase) >= 0
            || env.IndexOf("room", StringComparison.OrdinalIgnoreCase) >= 0
            || env.IndexOf("corridor", StringComparison.OrdinalIgnoreCase) >= 0
            || env.IndexOf("building", StringComparison.OrdinalIgnoreCase) >= 0
            || mode.IndexOf("indoor", StringComparison.OrdinalIgnoreCase) >= 0
            || mode.IndexOf("entry", StringComparison.OrdinalIgnoreCase) >= 0
            || topology.IndexOf("room", StringComparison.OrdinalIgnoreCase) >= 0
            || topology.IndexOf("corridor", StringComparison.OrdinalIgnoreCase) >= 0
            || topology.IndexOf("different_volume", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool ContainsAny(string value, params string[] tokens)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (string token in tokens)
        {
            if (value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string Bool(bool value) => value ? "true" : "false";
    private static string NullableBool(bool? value) => value.HasValue ? Bool(value.Value) : "unknown";

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }
}
#endif
