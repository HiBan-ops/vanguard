#if SPT_CLIENT
using Vanguard.Client.Runtime.Decision;

// Responsibility: Turns canonical injury, item-actionability and safety facts into one readable recommendation for the medical scheduler.
// Flow: Terminal/unreadable cases are handled first, then treatment urgency and feasibility are evaluated in a fixed order to produce a plan key, next step, gates and retry policy.
// Authority boundary: Planning is read-only; EFT owns health/items and only the medical scheduler/executors may acquire authority to perform treatment.
// Invariant: The same inputs produce the same plan, impossible treatment is never presented as executable, and building a plan cannot mutate raid state.
namespace Vanguard.Client.Runtime.Medical;

internal static class VanguardMedicalPlanReadOnlyBuilder
{
    public const string PlanMarker = "VANGUARD_MEDICAL_PLAN_READONLY_OK";

    public static VanguardMedicalPlanSnapshot ForDead()
    {
        return new VanguardMedicalPlanSnapshot
        {
            Readable = true,
            PlanKey = "MedicalTerminalDead",
            NextStep = "ObserveDeadOperator",
            ExecutionKind = "none",
            SafetyGate = "terminal",
            ActionabilityGate = "terminal",
            RetryPolicy = "none",
            Reason = "operator_dead_no_medical_plan",
            SuggestedPriority = 100f
        };
    }

    // Convert one canonical medical snapshot into a single next-step recommendation. The order matters:
    // unreadable/terminal cases are handled first, then surgery validity/actionability, urgent bleeding and
    // ordinary healing. Every branch returns a description of what should happen; none performs treatment.
    public static VanguardMedicalPlanSnapshot Build(
        VanguardMedicalNeedSnapshot need,
        VanguardMedicalActionabilitySnapshot actionability,
        VanguardMedicalSafetySnapshot safety)
    {
        if (!need.IsReadable)
        {
            return Plan(
                readable: false,
                planKey: "MedicalUnreadable",
                nextStep: "ObserveMedicalUnreadableReadOnly",
                executionKind: "none",
                need: need,
                actionability: actionability,
                safetyGate: "snapshot_unreadable",
                actionabilityGate: "snapshot_unreadable",
                retryPolicy: "rescan_next_cycle",
                reason: "medical_snapshot_unreadable",
                priority: 0f,
                wait: true,
                recheck: true);
        }

        if (need.DominantNeed == VanguardMedicalNeed.UntreatableVitalDestroyedPart)
        {
            return Plan(
                readable: true,
                planKey: "MedicalTerminalUntreatableVitalDamage",
                nextStep: "ObserveUntreatableVitalPartReadOnly",
                executionKind: "terminal_observation",
                need: need,
                actionability: actionability,
                safetyGate: SafetyGate(safety),
                actionabilityGate: "no_reconstructive_action",
                retryPolicy: "none_until_other_actionable_effect_appears",
                reason: "head_or_thorax_black_non_reconstructible_in_raid",
                priority: 8f,
                wait: false,
                recheck: false);
        }

        if (!need.HasAnyNeed)
        {
            return Plan(
                readable: true,
                planKey: "MedicalNoNeed",
                nextStep: "ObserveMedicalHealthy",
                executionKind: "none",
                need: need,
                actionability: actionability,
                safetyGate: "none_required",
                actionabilityGate: "none_required",
                retryPolicy: "none",
                reason: "medical_no_need",
                priority: 4f);
        }

        if (VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(need.DominantNeed)
            && VanguardMedicalSurgeryTargetPolicy.TryResolveTarget(actionability.TargetPart, need.TargetPart, out var surgeryTarget)
            && VanguardMedicalSurgeryTargetPolicy.IsUntreatableVitalTarget(surgeryTarget))
        {
            return Plan(
                readable: true,
                planKey: "MedicalUntreatableVitalPart",
                nextStep: "ObserveUntreatableVitalPartReadOnly",
                executionKind: "critical_triage_terminal",
                need: need,
                actionability: actionability,
                safetyGate: SafetyGate(safety),
                actionabilityGate: "surgery_target_non_reconstructible",
                retryPolicy: "no_cms_surv12_head_thorax_stop_bleeds_hp_heal_if_possible",
                reason: "surgery_target_non_reconstructible_head_or_thorax",
                priority: 8f,
                wait: false,
                recheck: false);
        }

        if (VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(need.DominantNeed)
            && VanguardMedicalSurgeryTargetPolicy.TryResolveTarget(actionability.TargetPart, need.TargetPart, out surgeryTarget)
            && !VanguardMedicalSurgeryTargetPolicy.IsValidSurgeryTarget(surgeryTarget))
        {
            return Plan(
                readable: true,
                planKey: "MedicalInvalidSurgeryTarget",
                nextStep: "MedicalRecheckTargetReadOnly",
                executionKind: "rescan",
                need: need,
                actionability: actionability,
                safetyGate: SafetyGate(safety),
                actionabilityGate: "invalid_surgery_target",
                retryPolicy: "rescan_body_part_before_retry",
                reason: "surgery_target_not_reconstructible_or_unknown_category",
                priority: 50f,
                wait: false,
                recheck: true);
        }

        if (!actionability.RequiredItemAvailable)
        {
            return Plan(
                readable: true,
                planKey: "MedicalAwaitItem",
                nextStep: "ObserveMedicalItemMissingReadOnly",
                executionKind: "none",
                need: need,
                actionability: actionability,
                safetyGate: SafetyGate(safety),
                actionabilityGate: "item_missing",
                retryPolicy: MissingItemRetryPolicy(need.DominantNeed),
                reason: "required_medical_item_missing",
                priority: MissingItemPriority(need.DominantNeed),
                wait: true);
        }

        if (!actionability.TargetKnown)
        {
            return Plan(
                readable: true,
                planKey: "MedicalRecheckTarget",
                nextStep: "MedicalRecheckTargetReadOnly",
                executionKind: "rescan",
                need: need,
                actionability: actionability,
                safetyGate: SafetyGate(safety),
                actionabilityGate: "target_unknown",
                retryPolicy: "rescan_body_part_before_retry",
                reason: "medical_target_unknown",
                priority: 42f,
                wait: false,
                recheck: true);
        }

        if (actionability.AnyMedicineUsing)
        {
            return Plan(
                readable: true,
                planKey: "MedicalAwaitController",
                nextStep: "AwaitMedicalControllerReadinessReadOnly",
                executionKind: "wait",
                need: need,
                actionability: actionability,
                safetyGate: SafetyGate(safety),
                actionabilityGate: "medicine_controller_busy",
                retryPolicy: "retry_after_current_medical_action",
                reason: "medical_controller_busy_using",
                priority: BusyPriority(need.DominantNeed),
                wait: true);
        }

        if (actionability.Reloading)
        {
            return Plan(
                readable: true,
                planKey: "MedicalAwaitController",
                nextStep: "RetryAfterReloadReadOnly",
                executionKind: "wait",
                need: need,
                actionability: actionability,
                safetyGate: SafetyGate(safety),
                actionabilityGate: "blocked_reloading",
                retryPolicy: "retry_after_reload",
                reason: "medical_blocked_by_reload",
                priority: BusyPriority(need.DominantNeed),
                wait: true);
        }

        if (actionability.GrenadeThrowing)
        {
            return Plan(
                readable: true,
                planKey: "MedicalAwaitController",
                nextStep: "RetryAfterGrenadeReadOnly",
                executionKind: "wait",
                need: need,
                actionability: actionability,
                safetyGate: SafetyGate(safety),
                actionabilityGate: "blocked_grenade",
                retryPolicy: "retry_after_grenade",
                reason: "medical_blocked_by_grenade",
                priority: BusyPriority(need.DominantNeed),
                wait: true);
        }

        if (actionability.CanApplyItem == false
            && VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(need.DominantNeed)
            && actionability.PersistentCapabilityAvailable)
        {
            return Plan(
                readable: true,
                planKey: "MedicalPrepareSurgeryController",
                nextStep: "PrepareStationarySurgeryControllerReadOnly",
                executionKind: "prepare_stationary_surgery",
                need: need,
                actionability: actionability,
                safetyGate: SafetyGate(safety),
                actionabilityGate: "persistent_capability_controller_transition_required",
                retryPolicy: "prepare_cover_and_controller_then_recheck",
                reason: "surgery_capability_present_controller_not_ready",
                priority: 64f,
                wait: false,
                recheck: true,
                stationary: true);
        }

        if (actionability.CanApplyItem == false)
        {
            return Plan(
                readable: true,
                planKey: "MedicalControllerRejected",
                nextStep: "MedicalControllerRejectedReadOnly",
                executionKind: "rescan",
                need: need,
                actionability: actionability,
                safetyGate: SafetyGate(safety),
                actionabilityGate: "controller_rejected",
                retryPolicy: "rescan_then_retry_with_cooldown",
                reason: "health_controller_can_apply_false",
                priority: 36f,
                wait: true,
                recheck: true);
        }

        if (RequiresStationarySurgery(need.DominantNeed) && !IsSurgerySafeWindow(safety))
        {
            return Plan(
                readable: true,
                planKey: "MedicalAwaitSafeWindow",
                nextStep: "AwaitStationarySurgerySafeWindowReadOnly",
                executionKind: "wait_for_safe_window",
                need: need,
                actionability: actionability,
                safetyGate: SafetyGate(safety),
                actionabilityGate: ActionabilityGate(actionability),
                retryPolicy: "retry_when_stationary_cover_or_hold_ready",
                reason: safety.CoveredOrHoldingAngle ? "stationary_surgery_requires_safe_window" : "stationary_surgery_await_cover_or_hold_angle",
                priority: actionability.CanApplyItem == true
                    && VanguardMedicalSurgeryTargetPolicy.TryResolveTarget(actionability.TargetPart, need.TargetPart, out var fastSurgeryTarget)
                    && VanguardMedicalSurgeryTargetPolicy.IsValidSurgeryTarget(fastSurgeryTarget)
                    && !safety.EnemyCanShoot
                    && !safety.IncomingFireRecent
                    && !(safety.EnemyVisible && !safety.CoveredOrHoldingAngle)
                        ? 92f
                        : 62f,
                wait: true,
                stationary: true);
        }

        if (need.DominantNeed == VanguardMedicalNeed.Fracture && !safety.SafeForStationaryAid)
        {
            return Plan(
                readable: true,
                planKey: "MedicalAwaitStationaryFractureSafeWindow",
                nextStep: "AwaitStationaryFractureSafeWindowReadOnly",
                executionKind: "wait_for_stationary_fracture_safe_window",
                need: need,
                actionability: actionability,
                safetyGate: SafetyGate(safety),
                actionabilityGate: ActionabilityGate(actionability),
                retryPolicy: "retry_when_stationary_aid_safe_window_true",
                reason: "stationary_fracture_requires_safe_window",
                priority: 48f,
                wait: true,
                stationary: true);
        }

        if (!safety.SafeForMobileAid && IsMobileAidCandidate(need.DominantNeed))
        {
            return Plan(
                readable: true,
                planKey: "MedicalAwaitSafeWindow",
                nextStep: "AwaitMobileAidSafeWindowReadOnly",
                executionKind: "wait_for_safe_window",
                need: need,
                actionability: actionability,
                safetyGate: SafetyGate(safety),
                actionabilityGate: ActionabilityGate(actionability),
                retryPolicy: "retry_when_mobile_safe_window_true",
                reason: "mobile_aid_not_safe_yet",
                priority: AwaitMobilePriority(need.DominantNeed),
                wait: true,
                mobile: true);
        }

        if (RequiresStationarySurgery(need.DominantNeed))
        {
            return Plan(
                readable: true,
                planKey: "MedicalReadyStationarySurgery",
                nextStep: "StationarySurgeryReadOnly",
                executionKind: "stationary_surgery",
                need: need,
                actionability: actionability,
                safetyGate: SafetyGate(safety),
                actionabilityGate: ActionabilityGate(actionability),
                retryPolicy: "execute_once_when_active_executor_exists",
                reason: "stationary_surgery_ready_active_candidate",
                priority: 74f,
                stationary: true,
                wouldExecute: true);
        }

        if (need.DominantNeed == VanguardMedicalNeed.Fracture)
        {
            return Plan(
                readable: true,
                planKey: "MedicalReadyStationaryFracture",
                nextStep: "StationaryFractureCareReadOnly",
                executionKind: "stationary_fracture",
                need: need,
                actionability: actionability,
                safetyGate: SafetyGate(safety),
                actionabilityGate: ActionabilityGate(actionability),
                retryPolicy: "execute_once_with_stationary_fracture_window",
                reason: "stationary_fracture_ready_active_candidate",
                priority: 64f,
                stationary: true,
                wouldExecute: true);
        }

        if (IsMobileAidCandidate(need.DominantNeed))
        {
            return Plan(
                readable: true,
                planKey: "MedicalReadyMobileStabilize",
                nextStep: "MobileMedicalStabilizeReadOnly",
                executionKind: "mobile_or_short_aid",
                need: need,
                actionability: actionability,
                safetyGate: SafetyGate(safety),
                actionabilityGate: ActionabilityGate(actionability),
                retryPolicy: "execute_with_short_window_when_active_executor_exists",
                reason: "mobile_medical_ready_active_candidate",
                priority: ReadyMobilePriority(need.DominantNeed, safety.DirectThreat, safety.CoveredSuppressionOpportunity),
                mobile: true,
                wouldExecute: true);
        }

        if (need.DominantNeed == VanguardMedicalNeed.PainMobility)
        {
            return Plan(
                readable: true,
                planKey: "MedicalShortActionFuture",
                nextStep: "AwaitShortMedicalActionFutureReadOnly",
                executionKind: "short_action_future",
                need: need,
                actionability: actionability,
                safetyGate: SafetyGate(safety),
                actionabilityGate: ActionabilityGate(actionability),
                retryPolicy: "future_short_action_executor_only",
                reason: "pain_mobility_short_action_future_scope",
                priority: 24f,
                wait: true);
        }

        return Plan(
            readable: true,
            planKey: "MedicalReadyShortAction",
            nextStep: "StationaryOrShortMedicalActionReadOnly",
            executionKind: "short_aid",
            need: need,
            actionability: actionability,
            safetyGate: SafetyGate(safety),
            actionabilityGate: ActionabilityGate(actionability),
            retryPolicy: "execute_with_short_window_when_active_executor_exists",
            reason: "medical_need_ready_but_readonly",
            priority: 40f,
            wouldExecute: true);
    }

    private static VanguardMedicalPlanSnapshot Plan(
        bool readable,
        string planKey,
        string nextStep,
        string executionKind,
        VanguardMedicalNeedSnapshot need,
        VanguardMedicalActionabilitySnapshot actionability,
        string safetyGate,
        string actionabilityGate,
        string retryPolicy,
        string reason,
        float priority,
        bool move = false,
        bool stationary = false,
        bool mobile = false,
        bool wait = false,
        bool recheck = false,
        bool wouldExecute = false)
    {
        return new VanguardMedicalPlanSnapshot
        {
            Readable = readable,
            PlanKey = planKey,
            NextStep = nextStep,
            ExecutionKind = executionKind,
            TargetPart = actionability.TargetKnown ? actionability.TargetPart : (need.TargetKnown ? need.TargetPart : "none"),
            ItemName = actionability.SelectedItemName,
            ItemTemplateId = actionability.SelectedItemTemplateId,
            SafetyGate = safetyGate,
            ActionabilityGate = actionabilityGate,
            RetryPolicy = retryPolicy,
            Reason = reason,
            WouldRequireMovement = move,
            WouldRequireStationary = stationary,
            WouldAllowMobile = mobile,
            WouldWait = wait,
            WouldRecheck = recheck,
            WouldExecuteIfActive = wouldExecute,
            SuggestedPriority = priority
        };
    }

    private static bool RequiresStationarySurgery(VanguardMedicalNeed need)
    {
        return need == VanguardMedicalNeed.SurgeryDestroyedPart || need == VanguardMedicalNeed.BlackBroken;
    }


    private static bool IsSurgerySafeWindow(VanguardMedicalSafetySnapshot safety)
    {
        // Runtime invariant: CMS/Surv12 surgery follows a SAIN-like strict area-clear
        // doctrine and must already be in a cover/hold posture. The active
        // selector also checks movement/loot/ORBIT idle state before executing.
        return safety.SurgeryAreaClear && safety.SafeForStationarySurgery && safety.CoveredOrHoldingAngle;
    }

    private static bool IsMobileAidCandidate(VanguardMedicalNeed need)
    {
        // The runtime active scope: bleeding remains first priority, HP heal may enter
        // the mobile short-aid lane, fracture has its own stationary safe-window lane,
        // and destroyed/black parts use the stationary CMS/Surv12 surgery lane.
        return need == VanguardMedicalNeed.HeavyBleed
            || need == VanguardMedicalNeed.LightBleed
            || need == VanguardMedicalNeed.HpHeal;
    }

    private static string SafetyGate(VanguardMedicalSafetySnapshot safety)
    {
        if (!safety.SurgeryAreaClear && !string.IsNullOrWhiteSpace(safety.SurgeryAreaClearReason) && safety.SurgeryAreaClearReason != "none") return "surgery_area_" + safety.SurgeryAreaClearReason;
        if (safety.DirectThreat) return "direct_threat";
        if (safety.ThreatScanWouldPromote) return "scan_would_promote";
        if (safety.EnemyVisible || safety.EnemyCanShoot) return "enemy_visible_or_can_shoot";
        if (safety.ResidualThreat) return "residual_threat";
        if (safety.StaleThreat) return "stale_threat";
        return "safe";
    }

    private static string ActionabilityGate(VanguardMedicalActionabilitySnapshot actionability)
    {
        if (actionability.AnyMedicineUsing) return "medicine_controller_busy";
        if (actionability.Reloading) return "blocked_reloading";
        if (actionability.GrenadeThrowing) return "blocked_grenade";
        if (!actionability.TargetKnown) return "target_unknown";
        if (!actionability.RequiredItemAvailable) return "item_missing";
        if (actionability.CanApplyItem == false) return "controller_rejected";
        if (actionability.CanApplyItem == true) return "controller_ready";
        return "item_available_unverified";
    }

    private static string MissingItemRetryPolicy(VanguardMedicalNeed need)
    {
        return need == VanguardMedicalNeed.HeavyBleed
            ? "urgent_loot_or_player_support_future"
            : "wait_for_inventory_change_or_lower_priority";
    }

    private static float MissingItemPriority(VanguardMedicalNeed need)
    {
        return need == VanguardMedicalNeed.HeavyBleed ? 46f : 12f;
    }

    private static float BusyPriority(VanguardMedicalNeed need)
    {
        return need == VanguardMedicalNeed.HeavyBleed ? 60f : 30f;
    }

    private static float AwaitMobilePriority(VanguardMedicalNeed need)
    {
        return need switch
        {
            VanguardMedicalNeed.HeavyBleed => 70f,
            VanguardMedicalNeed.LightBleed => 36f,
            VanguardMedicalNeed.HpHeal => 28f,
            _ => 20f
        };
    }

    private static float ReadyMobilePriority(VanguardMedicalNeed need, bool directThreat, bool coveredSuppressionOpportunity)
    {
        float score = need switch
        {
            VanguardMedicalNeed.HeavyBleed => 90f,
            VanguardMedicalNeed.LightBleed => 58f,
            VanguardMedicalNeed.HpHeal => 42f,
            _ => 20f
        };

        if (directThreat && !coveredSuppressionOpportunity && need != VanguardMedicalNeed.HeavyBleed)
        {
            score -= 35f;
        }

        return score;
    }
}
#endif
