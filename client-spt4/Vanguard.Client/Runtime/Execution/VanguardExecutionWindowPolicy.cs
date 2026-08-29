#if SPT_CLIENT
using System;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Intents;

// Responsibility: Defines when an intent may enter, continue or be preempted within the shared primary execution window.
// Flow: Intent priority, current lease ownership, freshness, safety and completion evidence are reduced to deterministic acquire/continue/preempt decisions consumed by the scheduler.
// Authority boundary: This policy arbitrates ownership only; domain executors remain responsible for validating and applying the actual action.
// Invariant: At most one incompatible primary action owns the window, stale ownership is released, and higher-priority safety work can preempt lower-priority activity.
namespace Vanguard.Client.Runtime.Execution;

internal static class VanguardExecutionWindowPolicy
{
    public static VanguardExecutionWindowSnapshot Build(OperatorDecisionSnapshot snapshot, VanguardIntentCandidate selected)
    {
        if (selected is null)
        {
            return Observe("no_selected_intent", "none", "none", "none", "none", "no_selected_intent");
        }

        if (!snapshot.Alive)
        {
            return Observe("terminal_dead", selected.IntentKey, selected.Domain, selected.TargetKey, "none", "operator_dead_terminal_no_window");
        }

        return selected.Domain switch
        {
            "Combat" => BuildCombat(snapshot, selected),
            "ThreatScan" => BuildThreatScan(snapshot, selected),
            "Awareness" => BuildAwareness(snapshot, selected),
            "Medical" => BuildMedical(snapshot, selected),
            "Follow" => BuildFollow(snapshot, selected),
            "Loot" => ObserveExternal(selected, "external_loot_observe"),
            "CorpseLoot" => BuildCorpseLootApproach(snapshot, selected),
            "Orbit" => ObserveExternal(selected, "external_orbit_observe"),
            _ => Observe("generic_observe", selected.IntentKey, selected.Domain, selected.TargetKey, "ObserveOnlyNoValidIntent", "generic_readonly_observe")
        };
    }


    private static VanguardExecutionWindowSnapshot BuildCorpseLootApproach(OperatorDecisionSnapshot snapshot, VanguardIntentCandidate selected)
    {
        return Window(
            "corpse_loot_claim_and_approach_window",
            VanguardPrimaryExecutionWindowKinds.CorpseLoot,
            selected,
            min: Vanguard.Client.Runtime.Loot.VanguardCorpseLootApproachDoctrine.MinimumWindowSeconds,
            max: Vanguard.Client.Runtime.Loot.VanguardCorpseLootApproachDoctrine.SchedulerMaximumWindowSeconds,
            noProgress: Vanguard.Client.Runtime.Loot.VanguardCorpseLootApproachDoctrine.NoProgressSeconds + 1.0f,
            progress: "exclusive_claim_acquired|owned_movement_command_confirmed|path_distance_decreases|corpse_or_anchor_reached|typed_preflight_ready|item_mutation_confirmed|sequential_rescan",
            completion: "bounded_operational_corpse_session_terminal",
            failure: "claim_lost|owned_command_lost|path_invalid|corpse_destroyed|owner_leash_exceeded|no_progress|transaction_failed|runtime_readback_failed",
            interruptions: "direct_threat|fresh_squad_contact|incoming_fire|urgent_medical|surgery_debt|hard_return|owner_moves|operator_dead",
            fallback: "MaintainFormationReadOnly",
            outcome: "claim_and_approach_only_no_interaction_no_transaction",
            requiresStationary: false,
            allowsMovement: true,
            allowsCombat: false,
            allowsFollow: false,
            allowsMedical: false);
    }

    private static VanguardExecutionWindowSnapshot BuildCombat(OperatorDecisionSnapshot snapshot, VanguardIntentCandidate selected)
    {
        if (selected.IntentKey == "YieldToSainCombat")
        {
            bool productive = Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.IsCombatProductive(snapshot, out var productiveReason);
            bool farAndUnproductive = !productive
                && snapshot.SquadCohesion.OwnerKnown
                && snapshot.SquadCohesion.OperatorDistanceToOwner >= Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.CombatCohesionForcedCatchupMeters;
            return Window(
                "combat_release_window",
                "SainCombatReleaseWindow",
                selected,
                min: productive ? 1.50f : 0.80f,
                max: productive ? (snapshot.Threat.DirectThreat ? 8.00f : 5.00f) : 3.00f,
                noProgress: productive ? 2.50f : 1.25f,
                progress: "sain_in_combat|shot_observed|ammo_delta|cover_move|enemy_lost_or_dead|angle_acquired|" + productiveReason,
                completion: "threat_resolved|target_lost|combat_state_cleared|combat_no_longer_productive",
                failure: "no_progress|controller_unavailable|operator_dead|stale_non_actionable_target",
                interruptions: productive ? "operator_dead_only|do_not_interrupt_for_follow" : "operator_dead|critical_medical|combat_ready_regroup_allowed",
                fallback: farAndUnproductive ? "MovementBrokerBreakSainSearchReturnBubbleReadOnly" : "HoldShortCombatSearchWindow",
                outcome: productive ? "would_protect_productive_sain_combat" : "would_bound_non_productive_sain_combat",
                requiresStationary: false,
                allowsMovement: !productive,
                allowsCombat: true,
                allowsFollow: false,
                allowsMedical: false);
        }

        if (selected.IntentKey == "ObserveSainCoverMove")
        {
            return Window(
                "combat_cover_progress_window",
                "SainCoverMoveObservationWindow",
                selected,
                min: 1.00f,
                max: 4.00f,
                noProgress: 2.00f,
                progress: "position_delta|cover_move_continues|threat_angle_improves",
                completion: "cover_reached|threat_resolved",
                failure: "stalled_no_progress|operator_dead",
                interruptions: "critical_direct_threat_escalation|operator_dead",
                fallback: "YieldToSainCombat",
                outcome: "would_track_cover_progress",
                requiresStationary: false,
                allowsMovement: true,
                allowsCombat: true,
                allowsFollow: false,
                allowsMedical: false);
        }

        return Window(
            "combat_search_window",
            "CombatSearchObservationWindow",
            selected,
            min: 1.00f,
            max: 3.50f,
            noProgress: 2.00f,
            progress: "sain_search_state|new_visible_candidate|scan_promote_candidate",
            completion: "target_confirmed|search_cleared",
            failure: "search_stale_no_candidate",
            interruptions: "direct_threat_escalates|operator_dead",
            fallback: "MaintainFormationReadOnly",
            outcome: "would_hold_short_search_window",
            requiresStationary: false,
            allowsMovement: true,
            allowsCombat: true,
            allowsFollow: false,
            allowsMedical: false);
    }

    private static VanguardExecutionWindowSnapshot BuildThreatScan(OperatorDecisionSnapshot snapshot, VanguardIntentCandidate selected)
    {
        if (selected.IntentKey == "PromoteImmediateThreatToSainReadOnly")
        {
            return Window(
                "threat_promotion_window",
                "ThreatPromotionCandidateWindow",
                selected,
                min: 0.25f,
                max: 1.25f,
                noProgress: 0.75f,
                progress: "candidate_visible|candidate_can_shoot|incoming_fire_fresh|current_target_replaced_if_active",
                completion: "candidate_confirmed_for_scheduler",
                failure: "candidate_stale|cooldown_blocked|no_candidate",
                interruptions: "operator_dead_only",
                fallback: "YieldToSainCombat",
                outcome: "would_request_sain_target_promotion",
                requiresStationary: false,
                allowsMovement: true,
                allowsCombat: true,
                allowsFollow: false,
                allowsMedical: false);
        }

        return Observe("threat_scan_keep_current", selected.IntentKey, selected.Domain, selected.TargetKey, "YieldToSainCombat", "would_keep_current_sain_target");
    }

    private static VanguardExecutionWindowSnapshot BuildAwareness(OperatorDecisionSnapshot snapshot, VanguardIntentCandidate selected)
    {
        if (selected.IntentKey == "AwarenessPromoteConfirmedThreatReadOnly")
        {
            return Window(
                "awareness_confirmed_threat_window",
                "AwarenessConfirmedThreatWindow",
                selected,
                min: 0.50f,
                max: 2.00f,
                noProgress: 1.00f,
                progress: "confidence_stable|candidate_visible_or_can_shoot|incoming_fire_fresh",
                completion: "confirmed_threat_ready_for_scheduler",
                failure: "candidate_stale|confidence_lost",
                interruptions: "operator_dead_only",
                fallback: "YieldToSainCombat",
                outcome: "would_promote_confirmed_threat",
                requiresStationary: false,
                allowsMovement: true,
                allowsCombat: true,
                allowsFollow: false,
                allowsMedical: false);
        }

        if (selected.IntentKey == "AwarenessReleaseFormationForThreatReadOnly")
        {
            return Window(
                "awareness_release_candidate_window",
                "AwarenessFormationReleaseCandidateWindow",
                selected,
                min: 0.75f,
                max: 2.50f,
                noProgress: 1.25f,
                progress: "release_confidence_stable|los_or_can_shoot|fresh_fire",
                completion: "release_candidate_ready_for_scheduler",
                failure: "suspicion_decayed|target_not_confirmed",
                interruptions: "operator_dead_only",
                fallback: "AwarenessOrientAttentionReadOnly",
                outcome: "would_request_formation_release",
                requiresStationary: false,
                allowsMovement: true,
                allowsCombat: true,
                allowsFollow: false,
                allowsMedical: false);
        }

        if (selected.IntentKey == "AwarenessOrientAttentionReadOnly" || selected.IntentKey == "AwarenessPropagateConfirmedThreatReadOnly")
        {
            return Window(
                "awareness_attention_window",
                "AwarenessAttentionObservationWindow",
                selected,
                min: 0.75f,
                max: 3.00f,
                noProgress: 1.50f,
                progress: "stimulus_recent|candidate_arc_known|confidence_not_decaying",
                completion: "attention_candidate_observed|threat_confirmed_or_dismissed",
                failure: "stimulus_stale|no_candidate",
                interruptions: "direct_threat_escalates|operator_dead",
                fallback: "MaintainFormationReadOnly",
                outcome: selected.IntentKey == "AwarenessPropagateConfirmedThreatReadOnly" ? "would_propagate_confirmed_threat" : "would_orient_attention_only",
                requiresStationary: false,
                allowsMovement: true,
                allowsCombat: false,
                allowsFollow: true,
                allowsMedical: true);
        }

        return Observe("awareness_maintain_formation", selected.IntentKey, selected.Domain, selected.TargetKey, "MaintainFormationReadOnly", "would_keep_formation_under_suspicion");
    }

    private static VanguardExecutionWindowSnapshot BuildMedical(OperatorDecisionSnapshot snapshot, VanguardIntentCandidate selected)
    {
        var plan = snapshot.Medical.Plan;
        if (selected.IntentKey == "MobileMedicalStabilize")
        {
            return Window(
                "mobile_medical_window",
                "MobileMedicalStabilizeWindow",
                selected,
                min: 1.25f,
                max: 5.00f,
                noProgress: 2.00f,
                progress: "item_use_started|controller_accepts|bleed_removed|hp_delta|movement_continues",
                completion: "need_resolved|mobile_stabilize_done",
                failure: "controller_rejected|item_missing|no_medical_progress",
                interruptions: "immediate_threat_can_shoot_or_close|operator_dead",
                fallback: plan.WouldWait ? plan.NextStep : "AwaitMobileAidSafeWindowReadOnly",
                outcome: "would_open_mobile_medical_stabilize",
                requiresStationary: false,
                allowsMovement: true,
                allowsCombat: false,
                allowsFollow: true,
                allowsMedical: true);
        }

        if (selected.IntentKey == "StationaryMedicalStabilize")
        {
            return Window(
                "stationary_fracture_window",
                "StationaryMedicalFractureWindow",
                selected,
                min: 1.50f,
                max: snapshot.Medical.Actionability.SelectedItemName.Contains("Grizzly", StringComparison.OrdinalIgnoreCase) ? 12.50f : 10.50f,
                noProgress: 3.00f,
                progress: "item_use_started|splint_started|fracture_removed|controller_busy_expected",
                completion: "fracture_removed|stationary_fracture_done",
                failure: "controller_rejected|item_missing|no_medical_progress",
                interruptions: "enemy_can_shoot|close_direct_threat|operator_dead",
                fallback: "AwaitStationaryFractureSafeWindowReadOnly",
                outcome: "would_open_stationary_fracture_care",
                requiresStationary: true,
                allowsMovement: false,
                allowsCombat: false,
                allowsFollow: false,
                allowsMedical: true);
        }

        if (selected.IntentKey == "MedicalPrepareSurgeryCover")
        {
            return Window(
                "medical_prepare_surgery_cover_window",
                "MedicalPrepareSurgeryCoverWindow",
                selected,
                min: 1.00f,
                max: 24.00f,
                noProgress: 4.50f,
                progress: "loot_cancelled|orbit_loot_cancelled|sain_cover_seek_started|vanguard_cover_slot_assigned|vanguard_cover_slot_moving|moving_to_cover|hold_in_cover|vanguard_cover_slot_granted|stationary_idle|cover_or_hold_ready|surgery_area_clear",
                completion: "stationary_surgery_ready_next_tick",
                failure: "direct_threat|actionability_lost|cover_seek_stalled|no_ai_cover_or_wall_recess_slot_found",
                interruptions: "enemy_can_shoot|fresh_incoming_fire|operator_dead",
                fallback: "AwaitStationarySurgerySafeWindowReadOnly",
                outcome: "would_seek_sain_or_vanguard_patient_only_surgery_cover_slot_then_stationary_surgery",
                requiresStationary: false,
                allowsMovement: true,
                allowsCombat: false,
                allowsFollow: false,
                allowsMedical: false);
        }

        if (selected.IntentKey == "StationaryMedicalSurgery" || selected.IntentKey == "ProposeStationarySurgeryReadOnly")
        {
            bool surv12 = snapshot.Medical.Actionability.SelectedItemName.Contains("Surv12", StringComparison.OrdinalIgnoreCase);
            return Window(
                "stationary_surgery_window",
                "StationaryMedicalSurgeryWindow",
                selected,
                min: 3.00f,
                max: surv12 ? 24.00f : 18.00f,
                noProgress: 5.00f,
                progress: "surgery_started|surgical_kit_using|body_part_restored|controller_busy_expected",
                completion: "target_part_restored|surgery_finished",
                failure: "target_unknown|controller_rejected|item_missing|no_surgery_progress|false_finish",
                interruptions: "direct_threat|fresh_incoming_fire|enemy_can_shoot|operator_dead",
                fallback: "AwaitStationarySurgerySafeWindowReadOnly",
                outcome: selected.IntentKey == "StationaryMedicalSurgery" ? "would_open_active_stationary_surgery" : "would_open_stationary_surgery_readonly",
                requiresStationary: true,
                allowsMovement: false,
                allowsCombat: false,
                allowsFollow: false,
                allowsMedical: true);
        }

        if (selected.IntentKey == "ProposeStationaryOrShortMedicalActionReadOnly")
        {
            return Window(
                "short_medical_window",
                "ShortMedicalActionWindow",
                selected,
                min: 1.00f,
                max: 6.00f,
                noProgress: 2.50f,
                progress: "item_use_started|controller_accepts|effect_removed|hp_delta",
                completion: "need_resolved|short_action_done",
                failure: "controller_rejected|item_missing|no_medical_progress",
                interruptions: "direct_threat_can_shoot|operator_dead",
                fallback: "AwaitMobileAidSafeWindowReadOnly",
                outcome: "would_open_short_medical_action",
                requiresStationary: false,
                allowsMovement: plan.WouldAllowMobile,
                allowsCombat: false,
                allowsFollow: plan.WouldAllowMobile,
                allowsMedical: true);
        }

        if (selected.IntentKey.Contains("Await", StringComparison.OrdinalIgnoreCase)
            || selected.IntentKey.Contains("Rejected", StringComparison.OrdinalIgnoreCase)
            || selected.IntentKey.Contains("Recheck", StringComparison.OrdinalIgnoreCase))
        {
            return Observe("medical_wait_or_recheck", selected.IntentKey, selected.Domain, selected.TargetKey, plan.NextStep, "would_wait_recheck_or_retry_medical_plan");
        }

        return Observe("medical_observation", selected.IntentKey, selected.Domain, selected.TargetKey, plan.NextStep, "would_observe_medical_plan");
    }

    private static VanguardExecutionWindowSnapshot BuildFollow(OperatorDecisionSnapshot snapshot, VanguardIntentCandidate selected)
    {
        if (selected.IntentKey == "RejoinFormationReadOnly")
        {
            return Window(
                "follow_rejoin_window",
                "FollowRejoinWindow",
                selected,
                min: 1.00f,
                max: 6.00f,
                noProgress: 2.50f,
                progress: "distance_to_slot_decreases|path_progress|speed_towards_slot",
                completion: "slot_reached|cohesion_restored",
                failure: "path_stalled|direct_threat_interrupts|no_follow_progress",
                interruptions: "direct_threat|urgent_medical|operator_dead",
                fallback: "MaintainFormationReadOnly",
                outcome: "would_open_follow_rejoin",
                requiresStationary: false,
                allowsMovement: true,
                allowsCombat: false,
                allowsFollow: true,
                allowsMedical: false);
        }

        return Window(
            "follow_maintain_window",
            "FollowMaintainWindow",
            selected,
            min: 1.00f,
            max: 4.00f,
            noProgress: 2.00f,
            progress: "cohesion_stable|slot_valid|no_direct_threat",
            completion: "formation_maintained",
            failure: "direct_threat_interrupts|slot_invalid",
            interruptions: "direct_threat|urgent_medical|operator_dead",
            fallback: "RejoinFormationReadOnly",
            outcome: "would_maintain_follow_cohesion",
            requiresStationary: false,
            allowsMovement: true,
            allowsCombat: false,
            allowsFollow: true,
            allowsMedical: false);
    }

    private static VanguardExecutionWindowSnapshot ObserveExternal(VanguardIntentCandidate selected, string outcome)
    {
        return Observe("external_observation", selected.IntentKey, selected.Domain, selected.TargetKey, "MaintainFormationReadOnly", outcome);
    }

    private static VanguardExecutionWindowSnapshot Observe(string contractKey, string intentKey, string domain, string target, string fallback, string outcome)
    {
        return new VanguardExecutionWindowSnapshot
        {
            Readable = true,
            ContractKey = contractKey,
            WindowKind = "ObservationOnlyWindow",
            IntentKey = intentKey,
            Domain = domain,
            TargetKey = SafeTarget(target),
            MinDurationSeconds = VanguardExecutionWindowSnapshot.Seconds(0.00f),
            MaxDurationSeconds = VanguardExecutionWindowSnapshot.Seconds(1.00f),
            NoProgressTimeoutSeconds = VanguardExecutionWindowSnapshot.Seconds(1.00f),
            ProgressSignals = "snapshot_change|intent_change",
            CompletionSignals = "observation_logged",
            FailureSignals = "none",
            InterruptionRules = "none_readonly_observe",
            FallbackIntentKey = string.IsNullOrWhiteSpace(fallback) ? "none" : fallback,
            OutcomePreview = outcome,
            WouldOpenIfActive = false,
            BlocksOtherPrimaryActions = false,
            RequiresStationary = false,
            AllowsMovement = true,
            AllowsCombat = true,
            AllowsFollow = true,
            AllowsMedical = true,
            ReadOnly = true
        };
    }

    private static VanguardExecutionWindowSnapshot Window(
        string contract,
        string kind,
        VanguardIntentCandidate selected,
        float min,
        float max,
        float noProgress,
        string progress,
        string completion,
        string failure,
        string interruptions,
        string fallback,
        string outcome,
        bool requiresStationary,
        bool allowsMovement,
        bool allowsCombat,
        bool allowsFollow,
        bool allowsMedical)
    {
        return new VanguardExecutionWindowSnapshot
        {
            Readable = true,
            ContractKey = contract,
            WindowKind = kind,
            IntentKey = selected.IntentKey,
            Domain = selected.Domain,
            TargetKey = SafeTarget(selected.TargetKey),
            MinDurationSeconds = VanguardExecutionWindowSnapshot.Seconds(min),
            MaxDurationSeconds = VanguardExecutionWindowSnapshot.Seconds(max),
            NoProgressTimeoutSeconds = VanguardExecutionWindowSnapshot.Seconds(noProgress),
            ProgressSignals = progress,
            CompletionSignals = completion,
            FailureSignals = failure,
            InterruptionRules = interruptions,
            FallbackIntentKey = fallback,
            OutcomePreview = outcome,
            WouldOpenIfActive = true,
            BlocksOtherPrimaryActions = true,
            RequiresStationary = requiresStationary,
            AllowsMovement = allowsMovement,
            AllowsCombat = allowsCombat,
            AllowsFollow = allowsFollow,
            AllowsMedical = allowsMedical,
            ReadOnly = true
        };
    }

    private static string SafeTarget(string? target)
    {
        return string.IsNullOrWhiteSpace(target) ? "none" : target;
    }
}
#endif
