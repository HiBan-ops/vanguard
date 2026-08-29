#if SPT_CLIENT
using System;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.External;
using Vanguard.Client.Runtime.Medical.Execution;

// Responsibility: Encodes the deterministic rules for Medical Surgery Prepare Policy within the medical runtime.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Runtime.Medical;

internal static class VanguardMedicalSurgeryPreparePolicy
{
    public const string StatusTag = "VANGUARD_MEDICAL_AUTHORITY_LEASE_OK";
    private const string GoCoverOnlyStatusTag = "VANGUARD_SURGERY_GO_COVER_ONLY_OK";
    private const string OrbitLocalHoldLockStatusTag = "VANGUARD_MEDICAL_ORBIT_LOCAL_HOLD_LOCK_OK";

    private static readonly TimeSpan PolicyLogInterval = TimeSpan.FromSeconds(2.00d);
    private static readonly System.Collections.Generic.Dictionary<string, DateTimeOffset> LastPolicyLogAtByKey = new(StringComparer.OrdinalIgnoreCase);

    public static bool ShouldPrepareBeforeStationarySurgery(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (!IsSurgeryNeed(snapshot.Medical.Need.DominantNeed))
        {
            reason = "need_not_surgery_scope";
            return false;
        }

        if (!VanguardMedicalSurgeryTargetPolicy.TryResolveTarget(snapshot, out var surgeryTarget))
        {
            reason = "surgery_target_unknown";
            LogBlocked(snapshot, reason, now);
            return false;
        }

        if (VanguardMedicalSurgeryTargetPolicy.IsUntreatableVitalTarget(surgeryTarget))
        {
            reason = "surgery_target_untreatable_vital_part:" + surgeryTarget;
            LogBlocked(snapshot, reason, now);
            return false;
        }

        if (!VanguardMedicalSurgeryTargetPolicy.IsValidSurgeryTarget(surgeryTarget))
        {
            reason = "surgery_target_invalid:" + surgeryTarget;
            LogBlocked(snapshot, reason, now);
            return false;
        }

        if (snapshot.Medical.Need.HasHeavyBleed || snapshot.Medical.Need.HasLightBleed)
        {
            reason = "bleed_priority_blocks_surgery_cover_prepare";
            LogBlocked(snapshot, reason, now);
            return false;
        }

        if (!HasSurgeryPreparationCapability(snapshot, out var actionReason))
        {
            reason = "actionability_blocked:" + actionReason;
            LogBlocked(snapshot, reason, now);
            return false;
        }

        if (VanguardExecutionLeaseCoordinator.IsCooldownBlocked(snapshot.BotProfileId, snapshot.Medical.Need.DominantNeed, SafeTarget(snapshot.Medical.Actionability.TargetPart, snapshot.Medical.Need.TargetPart), snapshot.Medical.Actionability.SelectedItemTemplateId, now, out var retryAt))
        {
            if (!VanguardSurgeryDebtService.ShouldBypassOutcomeCooldown(snapshot, now, retryAt, out var debtCooldownReason))
            {
                reason = "outcome_cooldown_until_" + retryAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                LogBlocked(snapshot, reason, now);
                return false;
            }

            reason = debtCooldownReason;
        }

        if (HasHardThreatInterrupt(snapshot, out var threatReason))
        {
            if (!VanguardSurgeryDebtService.ShouldIgnoreSoftPrepareThreat(snapshot, now, threatReason, out var debtThreatReason))
            {
                reason = "hard_threat_interrupt:" + threatReason;
                LogBlocked(snapshot, reason, now);
                return false;
            }

            reason = debtThreatReason;
        }

        if (VanguardMedicalSurgeryTargetPolicy.IsCriticalFastSurgeryCandidate(snapshot, out var fastReason))
        {
            reason = "critical_fast_surgery_entry:" + fastReason;
            LogCriticalFastEntry(snapshot, reason, now);
            return true;
        }

        if (!IsPatientStableEnoughForOpportunisticPrepare(snapshot, out var stableReason))
        {
            reason = "opportunistic_prepare_blocked:" + stableReason;
            LogBlocked(snapshot, reason, now);
            return false;
        }

        var safety = snapshot.Medical.Safety;
        if (VanguardSurgeryCoverPrepareExecutor.HasRecentVanguardSurgeryCoverGrant(snapshot, out var vanguardGrantReason))
        {
            if (VanguardMedicalIsolationController.HasCompatibleStationaryIsolation(
                    snapshot.BotProfileId,
                    surgeryTarget,
                    snapshot.Medical.Actionability.SelectedItemTemplateId,
                    now,
                    out var isolationReason))
            {
                reason = "vanguard_go_cover_slot_with_live_isolation:" + vanguardGrantReason + ":" + isolationReason;
                return false;
            }

            reason = "stale_go_cover_grant_without_stationary_isolation_requires_reprepare:"
                + vanguardGrantReason + ":" + isolationReason;
            LogGoCoverRequired(snapshot, reason, now);
            return true;
        }

        if (safety.SurgeryAreaClear && safety.SafeForStationarySurgery && safety.CoveredOrHoldingAngle && !IsPreStartStationarySurgeryBlocked(snapshot, out _))
        {
            reason = "go_cover_required_even_if_current_position_safe";
            LogGoCoverRequired(snapshot, reason, now);
            return true;
        }

        if (safety.SurgeryAreaClear && safety.SafeForStationarySurgery && safety.CoveredOrHoldingAngle && IsPreStartStationarySurgeryBlocked(snapshot, out var stationaryBlockReason))
        {
            reason = "go_cover_required_not_local_stabilization:" + stationaryBlockReason;
            LogGoCoverRequired(snapshot, reason, now);
            return true;
        }

        if (!CanPrepareMissingCoverOrSafeWindow(snapshot, out var prepareReason))
        {
            reason = prepareReason;
            LogBlocked(snapshot, reason, now);
            return false;
        }

        reason = prepareReason;
        return true;
    }

    public static bool HasSurgeryPreparationCapability(OperatorDecisionSnapshot snapshot, out string reason)
    {
        var state = VanguardMedicalSurgeryTargetPolicy.EvaluateSurgeryPreparationCandidate(snapshot, out reason);
        return state == VanguardSurgeryCandidateState.Ready;
    }

    public static bool HasHardThreatInterrupt(OperatorDecisionSnapshot snapshot, out string reason)
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

    public static bool IsPreStartStationarySurgeryBlocked(OperatorDecisionSnapshot snapshot, out string reason)
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

        if (IsLootOrOrbitActive(snapshot))
        {
            reason = "orbit_active_before_surgery";
            return true;
        }

        if (HasExternalPathOrOrbitResidue(snapshot, out var externalResidueReason))
        {
            reason = externalResidueReason;
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

    private static bool IsPatientStableEnoughForOpportunisticPrepare(OperatorDecisionSnapshot snapshot, out string reason)
    {
        float speed = Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed);
        if (speed > 0.85f)
        {
            reason = "patient_moving_speed_gt_0_85";
            return false;
        }

        string state = snapshot.Movement.PlayerState ?? string.Empty;
        if (state.IndexOf("DoorInteraction", StringComparison.OrdinalIgnoreCase) >= 0
            || state.IndexOf("Run", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            reason = "player_state_busy_for_prepare";
            return false;
        }

        reason = IsLootOrOrbitActive(snapshot) || HasExternalPathOrOrbitResidue(snapshot, out _) || snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true ? "stable_enough_external_quiesce_required" : "stable_enough";
        return true;
    }

    private static bool CanPrepareMissingCoverOrSafeWindow(OperatorDecisionSnapshot snapshot, out string reason)
    {
        var safety = snapshot.Medical.Safety;
        if (safety.EnemyVisible && !safety.CoveredOrHoldingAngle)
        {
            reason = "enemy_visible_without_cover_prepare_denied";
            return false;
        }

        if (VanguardMedicalSurgeryTargetPolicy.IsCriticalFastSurgeryCandidate(snapshot, out var fastReason))
        {
            reason = "critical_fast_surgery_prepare_allowed:" + fastReason;
            return true;
        }

        if (safety.SurgeryThreatPathTooClose || safety.SurgeryThreatDistanceTooClose)
        {
            if (safety.ResidualThreat || safety.StaleThreat || !snapshot.Threat.DirectThreat || safety.CoveredOrHoldingAngle)
            {
                reason = safety.SurgeryThreatPathTooClose
                    ? "opportunistic_residual_path_close_prepare_allowed:" + Safe(safety.SurgeryAreaClearReason)
                    : "opportunistic_residual_distance_close_prepare_allowed:" + Safe(safety.SurgeryAreaClearReason);
                return true;
            }

            reason = safety.SurgeryThreatPathTooClose
                ? "path_close_active_threat_prepare_denied:" + Safe(safety.SurgeryAreaClearReason)
                : "distance_close_active_threat_prepare_denied:" + Safe(safety.SurgeryAreaClearReason);
            return false;
        }

        if (!safety.CoveredOrHoldingAngle)
        {
            reason = "opportunistic_cover_or_hold_prepare_allowed:" + Safe(safety.SurgeryAreaClearReason);
            return true;
        }

        if (safety.ResidualThreat || safety.StaleThreat)
        {
            reason = "opportunistic_residual_or_stale_safe_window_prepare_allowed:" + Safe(safety.SurgeryAreaClearReason);
            return true;
        }

        reason = "safe_window_not_preparable:" + Safe(safety.SurgeryAreaClearReason);
        return false;
    }


    private static bool HasExternalPathOrOrbitResidue(OperatorDecisionSnapshot snapshot, out string reason)
    {
        float? dist = snapshot.Movement.DistanceToDestination ?? snapshot.Movement.GoToDistance;
        if (snapshot.Movement.HasPath == true && dist.HasValue && dist.Value > 1.00f)
        {
            reason = "external_path_residue_before_surgery";
            return true;
        }

        var activity = VanguardExternalAuthorityAdapter.ReadActivity(null, snapshot, DateTimeOffset.UtcNow);
        if ((activity.OrbitSemanticActive || activity.IsOrbitObjectiveResidue) && !activity.OrbitLayerIdleQuiesced)
        {
            reason = "orbit_objective_residue_before_surgery:" + Safe(activity.BlockingReason);
            return true;
        }

        reason = "none";
        return false;
    }

    private static bool IsLootOrOrbitActive(OperatorDecisionSnapshot snapshot)
    {
        var activity = VanguardExternalAuthorityAdapter.ReadActivity(null, snapshot, DateTimeOffset.UtcNow);
        return activity.LootingBotsActive
            || activity.LootingBotsTaskRunning
            || activity.LootingBotsHasActiveLootable
            || ((activity.OrbitSemanticActive || activity.IsOrbitObjectiveResidue) && !activity.OrbitLayerIdleQuiesced);
    }

    private static bool IsSurgeryNeed(VanguardMedicalNeed need)
    {
        return VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(need);
    }

    private static void LogCriticalFastEntry(OperatorDecisionSnapshot snapshot, string reason, DateTimeOffset now)
    {
        string key = Safe(snapshot.BotProfileId) + ":critical_fast_surgery:" + SafeTarget(snapshot.Medical.Actionability.TargetPart, snapshot.Medical.Need.TargetPart);
        if (LastPolicyLogAtByKey.TryGetValue(key, out var last) && now - last < PolicyLogInterval)
        {
            return;
        }

        LastPolicyLogAtByKey[key] = now;
        VanguardClientDiagnosticsLog.Diagnostic(VanguardMedicalSurgeryTargetPolicy.CriticalFastSurgeryStatusTag, () => $"VANGUARD_MEDICAL_FAST_PROCEDURE_ENTRY operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; need={snapshot.Medical.Need.DominantNeed}; target={SafeTarget(snapshot.Medical.Actionability.TargetPart, snapshot.Medical.Need.TargetPart)}; hp={snapshot.Medical.Need.HealthPercent}; item={Safe(snapshot.Medical.Actionability.SelectedItemName)}; enemyVisible={Bool(snapshot.Medical.Safety.EnemyVisible)}; enemyCanShoot={Bool(snapshot.Medical.Safety.EnemyCanShoot)}; incomingFire={Bool(snapshot.Medical.Safety.IncomingFireRecent)}; residual={Bool(snapshot.Medical.Safety.ResidualThreat)}; stale={Bool(snapshot.Medical.Safety.StaleThreat)}; threatDistance={(snapshot.Medical.Safety.ThreatDistance.HasValue ? snapshot.Medical.Safety.ThreatDistance.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "unknown")}; action=enter_hard_medical_procedure; cutOrbitLootFollowQuestGuard=patient_only; noMapWalk=true; noDistantTargetDelay=true; tag={VanguardMedicalSurgeryTargetPolicy.CriticalFastSurgeryStatusTag}; validTargetTag={VanguardMedicalSurgeryTargetPolicy.ValidSurgeryTargetsStatusTag}; authorityTag={StatusTag}");
    }

    private static string SafeTarget(string? actionTarget, string? needTarget)
    {
        if (!string.IsNullOrWhiteSpace(actionTarget) && actionTarget != "none")
        {
            return actionTarget;
        }

        return string.IsNullOrWhiteSpace(needTarget) ? "none" : needTarget;
    }

    private static void LogGoCoverRequired(OperatorDecisionSnapshot snapshot, string reason, DateTimeOffset now)
    {
        string key = Safe(snapshot.BotProfileId) + ":go_cover_required:" + Safe(reason);
        if (LastPolicyLogAtByKey.TryGetValue(key, out var last) && now - last < PolicyLogInterval)
        {
            return;
        }

        LastPolicyLogAtByKey[key] = now;
        VanguardClientDiagnosticsLog.Info(GoCoverOnlyStatusTag, $"VANGUARD_SURGERY_GO_COVER_REQUIRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; need={snapshot.Medical.Need.DominantNeed}; target={SafeTarget(snapshot.Medical.Actionability.TargetPart, snapshot.Medical.Need.TargetPart)}; speed={Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed):0.00}; coverOrHold={Bool(snapshot.Medical.Safety.CoveredOrHoldingAngle)}; surgeryAreaClear={Bool(snapshot.Medical.Safety.SurgeryAreaClear)}; safeForStationary={Bool(snapshot.Medical.Safety.SafeForStationarySurgery)}; patientOnly=true; localHoldRemoved=true; orbitLockTag={OrbitLocalHoldLockStatusTag}; tag={GoCoverOnlyStatusTag}");
    }

    private static void LogBlocked(OperatorDecisionSnapshot snapshot, string reason, DateTimeOffset now)
    {
        string key = Safe(snapshot.BotProfileId) + ":" + Safe(reason);
        if (LastPolicyLogAtByKey.TryGetValue(key, out var last) && now - last < PolicyLogInterval)
        {
            return;
        }

        LastPolicyLogAtByKey[key] = now;
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_MEDICAL_SURGERY_PREPARE_POLICY_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; need={snapshot.Medical.Need.DominantNeed}; target={SafeTarget(snapshot.Medical.Actionability.TargetPart, snapshot.Medical.Need.TargetPart)}; plan={Safe(snapshot.Medical.Plan.NextStep)}; speed={Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed):0.00}; lootBot={Tri(snapshot.Looting.BotLooting)}; lootTask={Tri(snapshot.Looting.LootTaskRunning)}; orbitActive={Bool(snapshot.Orbit.Active)}; orbitStatus={Safe(snapshot.Orbit.Status)}; enemyVisible={Bool(snapshot.Medical.Safety.EnemyVisible)}; enemyCanShoot={Bool(snapshot.Medical.Safety.EnemyCanShoot)}; incomingFire={Bool(snapshot.Medical.Safety.IncomingFireRecent)}; coverOrHold={Bool(snapshot.Medical.Safety.CoveredOrHoldingAngle)}; surgeryAreaClear={Bool(snapshot.Medical.Safety.SurgeryAreaClear)}; surgeryReason={Safe(snapshot.Medical.Safety.SurgeryAreaClearReason)}; patientOnly=true; gameplaySafe=true; orbitLockTag={OrbitLocalHoldLockStatusTag}; tag={StatusTag}");
    }

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Tri(bool? value) => value.HasValue ? Bool(value.Value) : "unknown";
    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }
}
#endif
