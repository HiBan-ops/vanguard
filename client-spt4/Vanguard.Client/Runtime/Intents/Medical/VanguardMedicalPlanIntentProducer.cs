#if SPT_CLIENT
using System;
using System.Collections.Generic;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Medical;
using Vanguard.Client.Runtime.Execution;

// Responsibility: Provides Medical Plan Intent Producer support for the intent production pipeline.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Intents;

internal sealed class VanguardMedicalPlanIntentProducer : IVanguardIntentProducer
{
    public IEnumerable<VanguardIntentCandidate> Produce(OperatorDecisionSnapshot snapshot)
    {
        if (VanguardOrchestratorAuthorityPolicy.ShouldQuietMedicalProducer(snapshot, out var quietReason)
            && !VanguardPrimaryExecutionContract.IsMobileMedicalSidecarCandidate(snapshot, out var mobileSidecarReason))
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "MedicalQuietUnderCombatReadOnly",
                Domain = "Medical",
                Valid = false,
                BaseScore = 0f,
                Reason = quietReason,
                TargetKey = "none",
                Gate = "blocked_medical_quiet_under_combat",
                PlanKey = "medical_quiet",
                NextStep = "observe_only"
            };
            yield break;
        }

        var medical = snapshot.Medical;
        var need = medical.Need;
        var plan = medical.Plan;
        string target = string.IsNullOrWhiteSpace(plan.TargetPart) ? "none" : plan.TargetPart;

        if (!snapshot.Alive)
        {
            yield return Candidate("ObserveDeadOperator", 100f, "operator_dead_no_action", target, "valid_terminal_readonly", plan);
            yield break;
        }

        if (!need.IsReadable || !plan.Readable)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "ObserveMedicalUnreadableReadOnly",
                Domain = "Medical",
                Valid = false,
                BaseScore = 0f,
                Reason = "medical_snapshot_unreadable;" + plan.Summary,
                TargetKey = "none",
                Gate = "invalid_medical_snapshot_unreadable"
            };
            yield break;
        }

        switch (plan.NextStep)
        {
            case "ObserveMedicalHealthy":
                yield return Candidate("ObserveMedicalHealthy", plan.SuggestedPriority, "medical_no_need;" + plan.Summary, target, "valid_medical_plan_no_need", plan);
                yield break;

            case "ObserveMedicalItemMissingReadOnly":
                yield return Candidate("Observe" + need.DominantNeed + "NeedItemMissingReadOnly", plan.SuggestedPriority, "medical_plan_item_missing;" + plan.Summary, target, "valid_medical_plan_item_missing", plan);
                yield break;

            case "MedicalRecheckTargetReadOnly":
                yield return Candidate("MedicalRecheckTargetReadOnly", plan.SuggestedPriority, "medical_plan_recheck_target;" + plan.Summary, "none", "valid_medical_plan_recheck", plan);
                yield break;

            case "AwaitMedicalControllerReadinessReadOnly":
            case "RetryAfterReloadReadOnly":
            case "RetryAfterGrenadeReadOnly":
                yield return Candidate(plan.NextStep, plan.SuggestedPriority, "medical_plan_await_controller;" + plan.Summary, target, "valid_medical_plan_await_controller", plan);
                yield break;

            case "MedicalControllerRejectedReadOnly":
                yield return Candidate("Observe" + need.DominantNeed + "ControllerRejectedReadOnly", plan.SuggestedPriority, "medical_plan_controller_rejected;" + plan.Summary, target, "valid_medical_plan_controller_rejected", plan);
                yield break;

            case "ObserveUntreatableVitalPartReadOnly":
                yield return Candidate("ObserveUntreatableVitalPartReadOnly", plan.SuggestedPriority, "medical_plan_untreatable_vital_part_no_surgery;" + plan.Summary, target, "valid_medical_plan_untreatable_vital_part", plan);
                yield break;

            case "PrepareStationarySurgeryControllerReadOnly":
                if (VanguardMedicalSurgeryPreparePolicy.ShouldPrepareBeforeStationarySurgery(snapshot, DateTimeOffset.UtcNow, out var controllerPrepareReason))
                {
                    yield return Candidate("MedicalPrepareSurgeryCover", Math.Max(plan.SuggestedPriority + 12f, 78f), "medical_plan_prepare_surgery_controller;reason=" + controllerPrepareReason + ";" + plan.Summary, target, "valid_medical_plan_prepare_surgery_controller", plan);
                }
                else
                {
                    yield return Candidate("ObserveSurgeryActionabilitySettleReadOnly", 18f, "medical_plan_surgery_controller_settle;reason=" + controllerPrepareReason + ";" + plan.Summary, target, "valid_medical_plan_surgery_controller_settle", plan);
                }
                yield break;

            case "AwaitStationarySurgerySafeWindowReadOnly":
                bool criticalFastSurgery = VanguardMedicalSurgeryTargetPolicy.IsCriticalFastSurgeryCandidate(snapshot, out var criticalFastReason);
                if (VanguardMedicalSurgeryPreparePolicy.ShouldPrepareBeforeStationarySurgery(snapshot, DateTimeOffset.UtcNow, out var prepareAwaitReason))
                {
                    float prepareScore = criticalFastSurgery ? Math.Max(plan.SuggestedPriority + 42f, 126f) : Math.Max(plan.SuggestedPriority + 8f, 74f);
                    yield return Candidate("MedicalPrepareSurgeryCover", prepareScore, "medical_plan_prepare_surgery_cover;fast=" + (criticalFastSurgery ? "true" : "false") + ";fastReason=" + criticalFastReason + ";reason=" + prepareAwaitReason + ";" + plan.Summary, target, "valid_medical_plan_prepare_surgery_cover", plan);
                }

                yield return Candidate("ObserveSurgeryNeedAwaitSafeWindowReadOnly", criticalFastSurgery ? 18f : plan.SuggestedPriority, "medical_plan_await_surgery_safe_window;fast=" + (criticalFastSurgery ? "true" : "false") + ";" + plan.Summary, target, "valid_medical_plan_await_safe_window", plan);
                yield break;

            case "AwaitMobileAidSafeWindowReadOnly":
                yield return Candidate("Observe" + need.DominantNeed + "NeedAwaitMobileSafeWindowReadOnly", plan.SuggestedPriority, "medical_plan_await_mobile_safe_window;" + plan.Summary, target, "valid_medical_plan_await_safe_window", plan);
                yield break;

            case "AwaitStationaryFractureSafeWindowReadOnly":
                yield return Candidate("ObserveFractureNeedAwaitStationarySafeWindowReadOnly", plan.SuggestedPriority, "medical_plan_await_stationary_fracture_safe_window;" + plan.Summary, target, "valid_medical_plan_await_stationary_safe_window", plan);
                yield break;

            case "StationarySurgeryReadOnly":
                var surgeryCandidateState = VanguardMedicalSurgeryTargetPolicy.EvaluateSurgeryCandidate(snapshot, out var surgeryTargetReason);
                if (surgeryCandidateState == VanguardSurgeryCandidateState.Invalid)
                {
                    yield return Candidate("ObserveInvalidSurgeryTargetReadOnly", 35f, "medical_plan_surgery_invalid_target;reason=" + surgeryTargetReason + ";" + plan.Summary, target, "valid_medical_plan_invalid_surgery_target", plan);
                    yield break;
                }

                if (surgeryCandidateState == VanguardSurgeryCandidateState.Transient)
                {
                    yield return Candidate("ObserveSurgeryActionabilitySettleReadOnly", 18f, "medical_plan_surgery_actionability_settle;reason=" + surgeryTargetReason + ";preLeaseSettle=true;movementMutation=false;authorityMutation=false;doesNotOutscoreFollowOrCombat=true;" + plan.Summary, target, "valid_medical_plan_surgery_actionability_settle_readonly", plan);
                    yield break;
                }

                if (!Vanguard.Client.Runtime.Medical.Execution.VanguardSurgeryCoverPrepareExecutor.HasRecentVanguardSurgeryCoverGrant(snapshot, out var activeGrantReason))
                {
                    if (VanguardMedicalSurgeryPreparePolicy.ShouldPrepareBeforeStationarySurgery(snapshot, DateTimeOffset.UtcNow, out var prepareReadyReason))
                    {
                        yield return Candidate("MedicalPrepareSurgeryCover", Math.Max(plan.SuggestedPriority + 12f, 80f), "medical_plan_prepare_before_stationary_surgery;reason=" + prepareReadyReason + ";grant=" + activeGrantReason + ";" + plan.Summary, target, "valid_medical_plan_prepare_surgery_cover", plan);
                        yield break;
                    }

                    yield return Candidate("MedicalPrepareSurgeryCover", Math.Max(plan.SuggestedPriority + 8f, 76f), "medical_plan_stationary_surgery_requires_vanguard_cover_commit;grant=" + activeGrantReason + ";" + plan.Summary, target, "valid_medical_plan_prepare_surgery_cover", plan);
                    yield break;
                }

                yield return Candidate("StationaryMedicalSurgery", plan.SuggestedPriority, "medical_plan_stationary_surgery_ready;grant=" + activeGrantReason + ";" + plan.Summary, target, "valid_medical_plan_ready_active_candidate", plan);
                yield break;

            case "MobileMedicalStabilizeReadOnly":
                yield return Candidate("MobileMedicalStabilize", plan.SuggestedPriority, "medical_plan_mobile_stabilize_ready;" + plan.Summary, target, "valid_medical_plan_ready_active_candidate", plan);
                yield break;

            case "StationaryFractureCareReadOnly":
                yield return Candidate("StationaryMedicalStabilize", plan.SuggestedPriority, "medical_plan_stationary_fracture_ready;" + plan.Summary, target, "valid_medical_plan_ready_active_candidate", plan);
                yield break;

            case "StationaryOrShortMedicalActionReadOnly":
                yield return Candidate("ProposeStationaryOrShortMedicalActionReadOnly", plan.SuggestedPriority, "medical_plan_short_action_ready;" + plan.Summary, target, "valid_medical_plan_ready_readonly", plan);
                yield break;
        }

        yield return Candidate("ObserveMedicalPlanReadOnly", plan.SuggestedPriority, "medical_plan_fallback;" + plan.Summary, target, "valid_medical_plan_fallback", plan);
    }

    private static VanguardIntentCandidate Candidate(string intentKey, float baseScore, string reason, string target, string gate, VanguardMedicalPlanSnapshot plan)
    {
        return new VanguardIntentCandidate
        {
            IntentKey = intentKey,
            Domain = "Medical",
            BaseScore = baseScore,
            Reason = reason,
            TargetKey = target,
            Gate = gate,
            PlanKey = plan.PlanKey,
            NextStep = plan.NextStep
        };
    }
}
#endif
