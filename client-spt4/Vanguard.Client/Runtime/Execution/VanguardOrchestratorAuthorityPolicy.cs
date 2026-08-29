#if SPT_CLIENT
using System;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Combat;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Intents;
using Vanguard.Client.Runtime.Medical;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Encodes the deterministic rules for Orchestrator Authority Policy within the execution arbitration runtime.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Runtime.Execution;

/// <summary>
/// Vanguard centralizes the primary runtime domain for one Operator.
/// Producers may still publish diagnostic intentions, but only the selected primary domain
/// is allowed to become an action through the scheduler/executors.
/// </summary>
internal static class VanguardOrchestratorAuthorityPolicy
{
    public const string StatusTag = "VANGUARD_ORCHESTRATOR_AUTHORITY_STATUS";
    public const string ExclusiveAuthorityStatusTag = "VANGUARD_EXCLUSIVE_AUTHORITY_STATUS";

    public const string DomainDead = "Dead";
    public const string DomainCombat = "Combat";
    public const string DomainMedical = "Medical";
    public const string DomainCohesion = "Cohesion";
    public const string DomainRecovery = "Recovery";

    private static bool bootLogged;

    public static void ResetForRaidLifecycle(string reason)
    {
        bootLogged = false;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_AUTHORITY_RESET reason={Safe(reason)}; mode=single_primary_domain; producers=passive_until_selected; tag={StatusTag}");
    }

    public static string SelectPrimaryDomain(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead";
            return DomainDead;
        }

        bool combatRecoveryBackoff = VanguardMainIntentScheduler.IsCombatRecoveryBackoffActive(snapshot, DateTimeOffset.UtcNow, out var recoveryBackoffReason);
        if (!combatRecoveryBackoff && IsCombatAuthority(snapshot, out reason))
        {
            return DomainCombat;
        }

        if (IsMedicalAuthority(snapshot, out reason))
        {
            return DomainMedical;
        }

        if (IsCohesionAuthority(snapshot, out reason))
        {
            if (combatRecoveryBackoff)
            {
                reason = "combat_no_progress_recovery_to_cohesion:" + recoveryBackoffReason + ":" + reason;
            }
            return DomainCohesion;
        }

        reason = combatRecoveryBackoff
            ? "combat_no_progress_recovery_observe:" + recoveryBackoffReason
            : "no_primary_pressure";
        return DomainRecovery;
    }

    public static bool IsCombatAuthority(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot))
        {
            reason = "true_direct_threat";
            return true;
        }

        if (snapshot.Threat.DirectThreat)
        {
            reason = "threat_direct";
            return true;
        }

        if (snapshot.Threat.EnemyVisible == true || snapshot.Threat.EnemyLineOfSight == true || snapshot.Threat.EnemyCanShoot == true)
        {
            reason = "direct_enemy_visible_or_canshoot";
            return true;
        }

        if (VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(snapshot))
        {
            reason = "immediate_combat_awareness";
            return true;
        }

        if (VanguardSainSquadCombatAuthority.IsSnapshotAuthority(snapshot.Sain, out var sainSquadReason)
            && !VanguardEffectiveMedicalExecutionPolicy.TryDescribeExclusiveAuthority(
                snapshot,
                DateTimeOffset.UtcNow,
                out _)
            && VanguardCombatAwarenessBridge.TryResolveLocallyAppliedSainTarget(
                snapshot,
                snapshot.Threat.EnemyId,
                out var sainSquadTarget,
                out var sainSquadGoalReason))
        {
            string grantedReason = sainSquadReason
                + ":target=" + sainSquadTarget
                + ":goal=" + sainSquadGoalReason;
            VanguardSainSquadCombatAuthority.GrantLayerYield(
                snapshot.BotProfileId,
                snapshot.Sain,
                DateTimeOffset.UtcNow,
                grantedReason);
            reason = grantedReason;
            return true;
        }

        string awarenessTarget = string.IsNullOrWhiteSpace(snapshot.Awareness.CandidateId) ? snapshot.Threat.EnemyId : snapshot.Awareness.CandidateId;
        if ((snapshot.Awareness.WouldPromoteSainTarget || snapshot.Awareness.WouldReleaseFormation)
            && VanguardCombatAwarenessBridge.HasIndividualQualifiedAssignmentOrLocalEvidence(snapshot, awarenessTarget))
        {
            reason = "awareness_individual_assignment_or_local_evidence";
            return true;
        }

        // Shared contacts are squad knowledge. Combat authority is granted after the unified coordinator
        // selects an individual assignment or after SAIN exposes direct local evidence.
        if (snapshot.Awareness.WouldPropagateConfirmedThreat
            && VanguardCombatAwarenessBridge.HasIndividualQualifiedAssignmentOrLocalEvidence(snapshot, awarenessTarget))
        {
            reason = "awareness_propagation_individually_assigned";
            return true;
        }

        if (snapshot.ThreatScan.WouldPromote
            && VanguardCombatAwarenessBridge.HasIndividualQualifiedAssignmentOrLocalEvidence(snapshot, snapshot.ThreatScan.CandidateThreatId))
        {
            reason = "threat_scan_individual_assignment_or_local_evidence";
            return true;
        }

        if (VanguardCombatAwarenessBridge.TryResolveVerifiedSainGoalHandoff(
                snapshot.BotProfileId,
                "none",
                DateTimeOffset.UtcNow,
                out var verifiedTarget,
                out var verifiedHandoffReason))
        {
            reason = "verified_sain_goal_handoff:" + verifiedTarget + ":" + verifiedHandoffReason;
            return true;
        }

        if (Vanguard.Client.Runtime.Awareness.VanguardCombatAwarenessBridge.HasMovementAuthoritativeSquadCombatContact(snapshot, DateTimeOffset.UtcNow, out var squadReason))
        {
            reason = "movement_authoritative_squad_contact:" + squadReason;
            return true;
        }

        reason = "no_combat_authority_signal";
        return false;
    }

    public static bool IsMedicalAuthority(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        bool recoveryBackoff = VanguardMainIntentScheduler.IsCombatRecoveryBackoffActive(snapshot, DateTimeOffset.UtcNow, out var recoveryReason);
        if (!recoveryBackoff && IsCombatAuthority(snapshot, out var combatReason))
        {
            reason = "medical_quiet_under_combat:" + combatReason;
            return false;
        }

        // Vanguard invariant: diagnosis is not authority. Remaining HP, fracture or surgery debt
        // stays in the snapshot and may publish intents, but it cannot freeze cohesion until a
        // scheduler medical window, Vanguard medical lease, or EFT controller is actually active.
        if (VanguardEffectiveMedicalExecutionPolicy.TryDescribeExclusiveAuthority(snapshot, DateTimeOffset.UtcNow, out var executionReason))
        {
            reason = recoveryBackoff
                ? "effective_medical_during_combat_recovery:" + recoveryReason + ":" + executionReason
                : "exclusive_medical_execution:" + executionReason;
            return true;
        }

        reason = snapshot.Medical.Need.HasAnyNeed
            ? "passive_medical_debt_no_authority:" + executionReason
            : "no_medical_need_or_execution";
        return false;
    }

    public static bool IsCohesionAuthority(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive || !snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement)
        {
            reason = "owner_unreliable_or_dead";
            return false;
        }

        if (!snapshot.SquadCohesion.InBubble || snapshot.MovementAuthority.HardOutsideBubble || snapshot.MovementAuthority.SoftOutsideBubble)
        {
            reason = "outside_or_soft_bubble";
            return true;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.ClaimedCohesionStartMeters)
        {
            reason = "cohesion_distance_pressure";
            return true;
        }

        if (!snapshot.SquadCohesion.UsefulPosition && snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.ClaimedCohesionUsefulCorrectionStartMeters + 4.0f)
        {
            reason = "useful_position_correction_pressure";
            return true;
        }

        reason = "cohesion_stable";
        return false;
    }

    public static float DomainScoreMultiplier(OperatorDecisionSnapshot snapshot, VanguardIntentCandidate candidate)
    {
        if (candidate == null || !candidate.Valid)
        {
            return 0.0f;
        }

        string domain = SelectPrimaryDomain(snapshot, out _);
        if (domain == DomainDead)
        {
            return string.Equals(candidate.IntentKey, "ObserveDeadOperator", StringComparison.OrdinalIgnoreCase) ? 10.0f : 0.01f;
        }

        if (domain == DomainCombat)
        {
            if (IsCombatCandidate(candidate))
            {
                return 8.0f;
            }

            if (candidate.Domain == "MovementAuthority" && IsHardEmergencyReturnCandidate(candidate) && snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.CombatCohesionEmergencyReturnMeters)
            {
                return 0.08f;
            }

            return 0.0f;
        }

        if (domain == DomainMedical)
        {
            if (candidate.Domain == "Medical")
            {
                return 3.2f;
            }

            if (candidate.Domain == "MovementAuthority" && candidate.IntentKey.IndexOf("Medical", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 1.0f;
            }

            return candidate.Domain == "Awareness" ? 0.10f : 0.0f;
        }

        if (domain == DomainCohesion)
        {
            if (string.Equals(candidate.IntentKey, "ExclusiveCohesionStableHold", StringComparison.OrdinalIgnoreCase))
            {
                return 5.0f;
            }

            if (IsCorpseLootApproachCandidate(candidate))
            {
                return 1.0f;
            }

            if (candidate.Domain == "MovementAuthority" || candidate.Domain == "SquadCohesion" || candidate.Domain == "Follow")
            {
                return 1.65f;
            }

            // Heavy/light bleed treatment may run as a mobile sidecar while cohesion owns the
            // primary movement. Stationary fracture/surgery is never admitted through this path.
            if (VanguardEffectiveMedicalExecutionPolicy.IsUrgentMobileSidecarCandidate(snapshot, candidate))
            {
                return 1.90f;
            }

            return 0.0f;
        }

        return 1.0f;
    }

    public static bool ShouldBlockCohesionMutation(OperatorDecisionSnapshot snapshot, out string reason)
    {
        string domain = SelectPrimaryDomain(snapshot, out reason);
        return domain == DomainCombat || domain == DomainMedical || domain == DomainDead;
    }

    public static bool ShouldHoldStableCohesion(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        if (ShouldBlockCohesionMutation(snapshot, out var blockReason))
        {
            reason = "blocked_by_primary_domain:" + blockReason;
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool verifiedInteriorCoverage = VanguardInteriorSecurityPlanner.IsVerifiedCoverageHold(snapshot, now, out var coverageReason);
        if (verifiedInteriorCoverage)
        {
            bool validInteriorSupport = snapshot.SquadCohesion.InBubble
                && snapshot.SquadCohesion.OperatorDistanceToOwner <= 16.0f
                && !snapshot.SquadCohesion.SectorDuplicate
                && !snapshot.SquadCohesion.RearOverstacked;
            reason = validInteriorSupport
                ? "verified_interior_coverage_hold:" + coverageReason
                : "verified_interior_coverage_invalidated_by_spacing";
            return validInteriorSupport;
        }

        if (VanguardSquadTravelCohesionExecutor.HasActiveTravelAuthority(snapshot.BotProfileId))
        {
            reason = "active_travel_corridor_cannot_be_frozen";
            return false;
        }

        if (VanguardSquadCohesionClaimExecutor.RequiresObservationDeployment(snapshot, now, out var deploymentReason))
        {
            if (!VanguardSquadCohesionClaimExecutor.HasSatisfiedObservationDeployment(snapshot, now, out var satisfiedReason))
            {
                reason = "observation_deployment_incomplete:" + deploymentReason + ":" + satisfiedReason;
                return false;
            }

            bool deploymentSupport = snapshot.SquadCohesion.InBubble
                && !snapshot.SquadCohesion.SectorDuplicate
                && !snapshot.SquadCohesion.RearOverstacked;
            reason = deploymentSupport
                ? "observation_deployment_satisfied:" + satisfiedReason
                : "observation_deployment_spacing_invalid";
            return deploymentSupport;
        }

        bool nearOwner = snapshot.SquadCohesion.OperatorDistanceToOwner <= 10.5f;
        bool stableSupport = snapshot.SquadCohesion.InBubble
            && nearOwner
            && !snapshot.SquadCohesion.SectorDuplicate
            && !snapshot.SquadCohesion.RearOverstacked
            && snapshot.SquadCohesion.UsefulPosition;
        if (stableSupport)
        {
            reason = "compact_moving_support_hold_no_replan";
            return true;
        }

        reason = "cohesion_not_stable_enough";
        return false;
    }


    public static bool IsCandidateAllowedByExclusiveDomain(OperatorDecisionSnapshot snapshot, VanguardIntentCandidate candidate, out string reason)
    {
        reason = "none";
        if (candidate == null || !candidate.Valid)
        {
            reason = "candidate_invalid";
            return false;
        }

        string domain = SelectPrimaryDomain(snapshot, out var domainReason);
        if (domain == DomainDead)
        {
            bool allowed = string.Equals(candidate.IntentKey, "ObserveDeadOperator", StringComparison.OrdinalIgnoreCase);
            reason = allowed ? "dead_observe_allowed" : "exclusive_dead_blocks:" + domainReason;
            return allowed;
        }

        if (domain == DomainCombat)
        {
            bool allowed = IsCombatCandidate(candidate);
            reason = allowed ? "exclusive_combat_allowed:" + domainReason : "exclusive_combat_blocks:" + domainReason;
            return allowed;
        }

        if (domain == DomainMedical)
        {
            bool allowed = candidate.Domain == "Medical" || (candidate.Domain == "MovementAuthority" && candidate.IntentKey.IndexOf("Medical", StringComparison.OrdinalIgnoreCase) >= 0);
            reason = allowed ? "exclusive_medical_allowed:" + domainReason : "exclusive_medical_blocks:" + domainReason;
            return allowed;
        }

        if (domain == DomainCohesion)
        {
            if (ShouldHoldStableCohesion(snapshot, out var stableReason))
            {
                bool allowedStable = string.Equals(candidate.IntentKey, "ExclusiveCohesionStableHold", StringComparison.OrdinalIgnoreCase);
                bool allowedCorpseLoot = IsCorpseLootApproachCandidate(candidate);
                reason = allowedStable
                    ? "exclusive_stable_hold_allowed:" + stableReason
                    : allowedCorpseLoot
                        ? "stable_hold_yields_to_bounded_corpse_loot:" + stableReason
                        : "exclusive_stable_hold_blocks:" + stableReason;
                return allowedStable || allowedCorpseLoot;
            }

            bool allowed = candidate.Domain == "MovementAuthority"
                || candidate.Domain == "SquadCohesion"
                || candidate.Domain == "Follow"
                || IsCorpseLootApproachCandidate(candidate)
                || VanguardEffectiveMedicalExecutionPolicy.IsUrgentMobileSidecarCandidate(snapshot, candidate);
            reason = allowed
                ? (VanguardEffectiveMedicalExecutionPolicy.IsUrgentMobileSidecarCandidate(snapshot, candidate)
                    ? "exclusive_cohesion_allows_urgent_mobile_medical_sidecar:" + domainReason
                    : "exclusive_cohesion_allowed:" + domainReason)
                : "exclusive_cohesion_blocks:" + domainReason;
            return allowed;
        }

        reason = "exclusive_recovery_allows_readonly";
        return candidate.Domain != "MovementAuthority" || string.Equals(candidate.IntentKey, "RecoveryObserveOnly", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCorpseLootApproachCandidate(VanguardIntentCandidate candidate)
        => candidate != null
            && candidate.Valid
            && string.Equals(candidate.Domain, "CorpseLoot", StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.IntentKey, "ApproachNearbyCorpse", StringComparison.OrdinalIgnoreCase);


    public static bool ShouldFreezeCohesionClaimProduction(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return true;
        }

        string domain = SelectPrimaryDomain(snapshot, out var domainReason);
        if (domain == DomainCombat || domain == DomainMedical || domain == DomainDead)
        {
            reason = "exclusive_domain_freeze:" + domain + ":" + domainReason;
            return true;
        }

        if (ShouldHoldStableCohesion(snapshot, out var stableReason))
        {
            reason = "exclusive_stable_hold_freeze:" + stableReason;
            return true;
        }

        reason = "cohesion_claims_allowed:" + domainReason;
        return false;
    }

    public static bool ShouldQuietMedicalProducer(OperatorDecisionSnapshot snapshot, out string reason)
    {
        if (VanguardMainIntentScheduler.IsCombatRecoveryBackoffActive(snapshot, DateTimeOffset.UtcNow, out var recoveryReason))
        {
            reason = "combat_no_progress_recovery_allows_medical:" + recoveryReason;
            return false;
        }

        if (IsCombatAuthority(snapshot, out var combatReason))
        {
            reason = "exclusive_combat_quiets_medical:" + combatReason;
            return true;
        }

        reason = "medical_allowed";
        return false;
    }

    public static void LogBootOnce()
    {
        if (bootLogged)
        {
            return;
        }

        bootLogged = true;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_AUTHORITY_BOOT mode=single_primary_domain; domains=Combat_Cohesion_Medical_Recovery_Dead; combatSuspendsCohesion=true; movementProducersPassiveUntilSelected=true; awarenessCombatSupportActive=true; stableCohesionHold=true; build={VanguardBuildVersion.BuildLabel}; tag={StatusTag}");
        VanguardClientDiagnosticsLog.Info(ExclusiveAuthorityStatusTag,
            $"VANGUARD_EXCLUSIVE_AUTHORITY_BOOT mode=hard_domain_execution_lock; combatSquadLock=true; cohesionStableFreeze=true; stationaryMedicalQuietUnderCombat=true; mobileMedicalSidecar=true; passiveMedicalDebtNeverOwnsMovement=true; rawEftControllerNeverOwnsMovement=true; effectiveMedicalAuthorityTag={VanguardEffectiveMedicalExecutionPolicy.StatusTag}; AuthorityTag={VanguardEffectiveMedicalExecutionPolicy.AuthorityContractStatusTag}; producers=gated_before_selection; build={VanguardBuildVersion.BuildLabel}; tag={ExclusiveAuthorityStatusTag}; Tag={StatusTag}");
    }

    private static bool IsCombatCandidate(VanguardIntentCandidate candidate)
    {
        if (candidate.Domain == "Combat" || candidate.Domain == "Awareness")
        {
            return true;
        }

        string key = candidate.IntentKey ?? string.Empty;
        return key.IndexOf("Sain", StringComparison.OrdinalIgnoreCase) >= 0
            || key.IndexOf("Threat", StringComparison.OrdinalIgnoreCase) >= 0
            || key.IndexOf("Combat", StringComparison.OrdinalIgnoreCase) >= 0
            || key.IndexOf("Promote", StringComparison.OrdinalIgnoreCase) >= 0
            || key.IndexOf("SectorAlert", StringComparison.OrdinalIgnoreCase) >= 0
            || key.IndexOf("YieldSainDirectThreat", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsHardEmergencyReturnCandidate(VanguardIntentCandidate candidate)
    {
        string key = candidate.IntentKey ?? string.Empty;
        return key.IndexOf("ReturnHard", StringComparison.OrdinalIgnoreCase) >= 0
            || key.IndexOf("ReturnBubble", StringComparison.OrdinalIgnoreCase) >= 0
            || key.IndexOf("HardReturn", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
