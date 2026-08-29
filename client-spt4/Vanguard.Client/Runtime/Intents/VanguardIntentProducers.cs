#if SPT_CLIENT
using System;
using System.Collections.Generic;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Medical;

// Responsibility: Provides the registry/factory surface that turns normalized decision evidence into domain-specific candidate intents for the main scheduler.
// Flow: Each producer inspects the shared decision snapshot, emits zero or more typed intents with priority/ownership metadata, and leaves final arbitration and execution to the central scheduler.
// Authority boundary: Producers express intent only; they cannot acquire physical movement/hands/inventory authority or execute EFT actions.
// Invariant: Intent production is side-effect free, stale evidence cannot create durable ownership, and domain priorities remain comparable through the shared intent contract.
namespace Vanguard.Client.Runtime.Intents;


internal sealed class VanguardThreatScannerIntentProducer : IVanguardIntentProducer
{
    public IEnumerable<VanguardIntentCandidate> Produce(OperatorDecisionSnapshot snapshot)
    {
        if (!snapshot.Alive || !snapshot.ThreatScan.Enabled || !snapshot.ThreatScan.CombatContext || !snapshot.ThreatScan.Scanned)
        {
            yield break;
        }

        if (snapshot.ThreatScan.WouldPromote)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "PromoteImmediateThreatToSainReadOnly",
                Domain = "ThreatScan",
                BaseScore = 98f,
                Reason = snapshot.ThreatScan.PromotionReason,
                TargetKey = snapshot.ThreatScan.CandidateThreatId,
                Gate = "valid_sidecar_would_promote"
            };
            yield break;
        }

        if (!string.Equals(snapshot.ThreatScan.CandidateThreatId, "none", StringComparison.OrdinalIgnoreCase))
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "KeepCurrentSainTargetReadOnly",
                Domain = "ThreatScan",
                BaseScore = 22f,
                Reason = snapshot.ThreatScan.PromotionReason,
                TargetKey = snapshot.ThreatScan.CandidateThreatId,
                Gate = "valid_sidecar_keep_current"
            };
        }
    }
}

internal sealed class VanguardThreatIntentProducer : IVanguardIntentProducer
{
    public IEnumerable<VanguardIntentCandidate> Produce(OperatorDecisionSnapshot snapshot)
    {
        if (!snapshot.Alive)
        {
            yield break;
        }

        if (snapshot.Threat.DirectThreat)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "YieldToSainCombat",
                Domain = "Combat",
                BaseScore = 95f,
                Reason = "direct_threat",
                TargetKey = snapshot.Threat.EnemyId,
                Gate = "valid_direct_threat"
            };
            yield break;
        }

        if (snapshot.Threat.ResidualThreat)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "ObserveResidualThreat",
                Domain = "Threat",
                BaseScore = 35f,
                Reason = "residual_threat_observe_only",
                TargetKey = snapshot.Threat.EnemyId,
                Gate = "valid_residual_threat"
            };
            yield break;
        }

        if (snapshot.Threat.StaleThreat)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "IgnoreStaleThreat",
                Domain = "Threat",
                BaseScore = 15f,
                Reason = "stale_threat_follow_can_resume",
                TargetKey = snapshot.Threat.EnemyId,
                Gate = "valid_stale_threat"
            };
        }
    }
}

internal sealed class VanguardCombatIntentProducer : IVanguardIntentProducer
{
    public IEnumerable<VanguardIntentCandidate> Produce(OperatorDecisionSnapshot snapshot)
    {
        if (!snapshot.Alive)
        {
            yield break;
        }

        if (snapshot.Sain.Classification == "sain_cover_move")
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "ObserveSainCoverMove",
                Domain = "Combat",
                BaseScore = 70f,
                Reason = "sain_cover_move_progress_possible",
                Gate = "valid_sain_cover_move"
            };
        }

        if (snapshot.Sain.Classification == "sain_search" || snapshot.Sain.Classification == "sain_enemy_known")
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "HoldShortCombatSearchWindow",
                Domain = "Combat",
                BaseScore = 45f,
                Reason = "sain_enemy_known_or_searching",
                Gate = "valid_sain_search"
            };
        }
    }
}

internal sealed class VanguardFollowIntentProducer : IVanguardIntentProducer
{
    public IEnumerable<VanguardIntentCandidate> Produce(OperatorDecisionSnapshot snapshot)
    {
        if (!snapshot.Alive)
        {
            yield break;
        }

        if (snapshot.Threat.DirectThreat)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "FollowSuppressedByDirectThreat",
                Domain = "Follow",
                Valid = false,
                BaseScore = 0f,
                Reason = "direct_threat_blocks_follow",
                Gate = "invalid_direct_threat"
            };
            yield break;
        }

        if (snapshot.Movement.Classification == "movement_path_stalled")
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "RejoinFormationReadOnly",
                Domain = "Follow",
                BaseScore = 38f,
                Reason = "path_stalled_follow_candidate",
                Gate = "valid_no_direct_threat"
            };
            yield break;
        }

        yield return new VanguardIntentCandidate
        {
            IntentKey = "MaintainFormationReadOnly",
            Domain = "Follow",
            BaseScore = snapshot.Threat.StaleThreat ? 32f : 25f,
            Reason = snapshot.Threat.StaleThreat ? "stale_threat_follow_resume_candidate" : "cohesion_baseline",
            Gate = "valid_no_direct_threat"
        };
    }
}

internal sealed class VanguardMedicalIntentProducer : IVanguardIntentProducer
{
    public IEnumerable<VanguardIntentCandidate> Produce(OperatorDecisionSnapshot snapshot)
    {
        if (!snapshot.Alive)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "ObserveDeadOperator",
                Domain = "Medical",
                BaseScore = 100f,
                Reason = "operator_dead_no_action",
                Gate = "valid_terminal_readonly"
            };
            yield break;
        }

        var medical = snapshot.Medical;
        var need = medical.Need;
        var actionability = medical.Actionability;

        if (!need.IsReadable)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "ObserveMedicalUnreadableReadOnly",
                Domain = "Medical",
                Valid = false,
                BaseScore = 0f,
                Reason = "medical_snapshot_unreadable",
                Gate = "invalid_medical_snapshot_unreadable"
            };
            yield break;
        }

        if (!need.HasAnyNeed)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "ObserveMedicalHealthy",
                Domain = "Medical",
                BaseScore = 4f,
                Reason = "medical_no_need",
                Gate = "valid_medical_readonly"
            };
            yield break;
        }

        string target = need.TargetKnown ? need.TargetPart : "none";
        if (!actionability.RequiredItemAvailable)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "Observe" + need.DominantNeed + "NeedItemMissingReadOnly",
                Domain = "Medical",
                BaseScore = MissingItemScore(need.DominantNeed),
                Reason = "medical_need_item_missing;" + need.Summary,
                TargetKey = target,
                Gate = "valid_medical_need_readonly_item_missing"
            };
            yield break;
        }

        if (!actionability.TargetKnown)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "MedicalRecheckTargetReadOnly",
                Domain = "Medical",
                BaseScore = 42f,
                Reason = "medical_target_unknown;" + need.Summary,
                TargetKey = "none",
                Gate = "valid_medical_recheck_readonly"
            };
            yield break;
        }

        if (actionability.CanApplyItem == false)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "Observe" + need.DominantNeed + "ControllerRejectedReadOnly",
                Domain = "Medical",
                BaseScore = 36f,
                Reason = "medical_controller_rejected;" + actionability.Summary,
                TargetKey = target,
                Gate = "valid_medical_controller_rejected_readonly"
            };
            yield break;
        }

        if (IsSurgeryNeed(need.DominantNeed) && !medical.Safety.SafeForStationarySurgery)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "ObserveSurgeryNeedAwaitSafeWindowReadOnly",
                Domain = "Medical",
                BaseScore = 62f,
                Reason = "surgery_need_safe_window_required;" + medical.Safety.Reason + ";" + actionability.Summary,
                TargetKey = target,
                Gate = "valid_surgery_need_readonly_unsafe"
            };
            yield break;
        }

        yield return new VanguardIntentCandidate
        {
            IntentKey = IntentKeyForNeed(need.DominantNeed),
            Domain = "Medical",
            BaseScore = ReadyScore(need.DominantNeed, medical.Safety.DirectThreat),
            Reason = "medical_need_ready_readonly;" + need.Summary + ";" + actionability.Summary,
            TargetKey = target,
            Gate = "valid_medical_need_readonly"
        };
    }

    private static bool IsSurgeryNeed(VanguardMedicalNeed need)
    {
        return need == VanguardMedicalNeed.SurgeryDestroyedPart || need == VanguardMedicalNeed.BlackBroken;
    }

    private static string IntentKeyForNeed(VanguardMedicalNeed need)
    {
        return need switch
        {
            VanguardMedicalNeed.HeavyBleed => "ObserveHeavyBleedNeedReadOnly",
            VanguardMedicalNeed.LightBleed => "ObserveLightBleedNeedReadOnly",
            VanguardMedicalNeed.Fracture => "ObserveFractureNeedReadOnly",
            VanguardMedicalNeed.HpHeal => "ObserveHpHealNeedReadOnly",
            VanguardMedicalNeed.PainMobility => "ObservePainMobilityNeedReadOnly",
            VanguardMedicalNeed.SurgeryDestroyedPart => "ObserveSurgeryNeedReadOnly",
            VanguardMedicalNeed.BlackBroken => "ObserveSurgeryNeedReadOnly",
            _ => "ObserveMedicalNeedReadOnly"
        };
    }

    private static float ReadyScore(VanguardMedicalNeed need, bool directThreat)
    {
        float score = need switch
        {
            VanguardMedicalNeed.HeavyBleed => 88f,
            VanguardMedicalNeed.LightBleed => 58f,
            VanguardMedicalNeed.Fracture => 52f,
            VanguardMedicalNeed.HpHeal => 44f,
            VanguardMedicalNeed.PainMobility => 40f,
            VanguardMedicalNeed.SurgeryDestroyedPart => 68f,
            VanguardMedicalNeed.BlackBroken => 68f,
            _ => 20f
        };

        if (directThreat && need != VanguardMedicalNeed.HeavyBleed)
        {
            score -= 35f;
        }

        return score;
    }

    private static float MissingItemScore(VanguardMedicalNeed need)
    {
        return need == VanguardMedicalNeed.HeavyBleed ? 46f : 12f;
    }
}

internal sealed class VanguardExternalSystemIntentProducer : IVanguardIntentProducer
{
    public IEnumerable<VanguardIntentCandidate> Produce(OperatorDecisionSnapshot snapshot)
    {
        if (!snapshot.Alive)
        {
            yield break;
        }

        if (snapshot.Looting.Classification == "loot_active")
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "ObserveLootingBotsTask",
                Domain = "Loot",
                BaseScore = 30f,
                Reason = "lootingbots_active_observe_only",
                Gate = "valid_external_observe"
            };
        }

        if (snapshot.Orbit.Active)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "ObserveOrbitObjective",
                Domain = "Orbit",
                BaseScore = 28f,
                Reason = "orbit_objective_active_observe_only",
                Gate = "valid_external_observe"
            };
        }
    }
}
#endif
