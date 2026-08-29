#if SPT_CLIENT
using System;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Encodes the deterministic rules for Medical Surgery Target Policy within the medical runtime.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Runtime.Medical;

internal static class VanguardMedicalSurgeryTargetPolicy
{
    public const string ValidSurgeryTargetsStatusTag = "VANGUARD_MEDICAL_VALID_SURGERY_TARGETS_OK";
    public const string CriticalFastSurgeryStatusTag = "VANGUARD_MEDICAL_CRITICAL_TRIAGE_FAST_SURGERY_OK";

    public static bool IsSurgeryNeed(VanguardMedicalNeed need)
    {
        return need == VanguardMedicalNeed.SurgeryDestroyedPart || need == VanguardMedicalNeed.BlackBroken;
    }

    public static bool TryResolveTarget(OperatorDecisionSnapshot snapshot, out string target)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        return TryResolveTarget(snapshot.Medical.Actionability.TargetPart, snapshot.Medical.Need.TargetPart, out target);
    }

    public static bool TryResolveTarget(string? actionTarget, string? needTarget, out string target)
    {
        target = NormalizeTarget(actionTarget);
        if (target != "none")
        {
            return true;
        }

        target = NormalizeTarget(needTarget);
        return target != "none";
    }

    public static bool IsValidSurgeryTarget(string? targetPart)
    {
        string target = NormalizeTarget(targetPart);
        return target == "leftarm"
            || target == "rightarm"
            || target == "leftleg"
            || target == "rightleg"
            || target == "stomach";
    }

    public static bool IsUntreatableVitalTarget(string? targetPart)
    {
        string target = NormalizeTarget(targetPart);
        return target == "head"
            || target == "thorax"
            || target == "chest";
    }

    public static bool IsKnownButInvalidSurgeryTarget(string? targetPart)
    {
        string target = NormalizeTarget(targetPart);
        return target != "none" && !IsValidSurgeryTarget(target);
    }

    public static bool HasPersistentSurgeryCapability(OperatorDecisionSnapshot snapshot, out string reason)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        if (!IsSurgeryNeed(snapshot.Medical.Need.DominantNeed))
        {
            reason = "need_not_surgery";
            return false;
        }

        if (!TryResolveTarget(snapshot, out var target))
        {
            reason = "target_unknown";
            return false;
        }

        if (IsUntreatableVitalTarget(target) || !IsValidSurgeryTarget(target))
        {
            reason = "target_not_operable:" + target;
            return false;
        }

        var actionability = snapshot.Medical.Actionability;
        if (!actionability.RequiredItemAvailable
            || string.IsNullOrWhiteSpace(actionability.SelectedItemTemplateId)
            || string.Equals(actionability.SelectedItemTemplateId, "none", StringComparison.OrdinalIgnoreCase))
        {
            reason = "selected_item_missing";
            return false;
        }

        reason = "persistent_surgery_capability:" + target;
        return true;
    }

    public static VanguardSurgeryCandidateState EvaluateSurgeryPreparationCandidate(OperatorDecisionSnapshot snapshot, out string reason)
    {
        if (!HasPersistentSurgeryCapability(snapshot, out reason))
        {
            return VanguardSurgeryCandidateState.Invalid;
        }

        var actionability = snapshot.Medical.Actionability;
        if (actionability.Reloading)
        {
            reason = "hands_reloading_transient";
            return VanguardSurgeryCandidateState.Transient;
        }

        if (actionability.GrenadeThrowing)
        {
            reason = "hands_grenade_transient";
            return VanguardSurgeryCandidateState.Transient;
        }

        if (actionability.AnyMedicineUsing || actionability.FirstAidUsing || actionability.SurgicalKitUsing || actionability.StimulatorUsing)
        {
            reason = "medicine_controller_busy";
            return VanguardSurgeryCandidateState.Transient;
        }

        reason = "preparation_capability_ready";
        return VanguardSurgeryCandidateState.Ready;
    }

    public static VanguardSurgeryCandidateState EvaluateSurgeryCandidate(OperatorDecisionSnapshot snapshot, out string reason)
    {
        var prepareState = EvaluateSurgeryPreparationCandidate(snapshot, out reason);
        if (prepareState != VanguardSurgeryCandidateState.Ready)
        {
            return prepareState;
        }

        if (snapshot.Medical.Actionability.CanApplyItem != true)
        {
            reason = "can_apply_not_true";
            return VanguardSurgeryCandidateState.Transient;
        }

        TryResolveTarget(snapshot, out var target);
        reason = "valid_actionable_surgery_target:" + target;
        return VanguardSurgeryCandidateState.Ready;
    }

    public static bool IsValidActionableSurgery(OperatorDecisionSnapshot snapshot, out string reason)
    {
        return EvaluateSurgeryCandidate(snapshot, out reason) == VanguardSurgeryCandidateState.Ready;
    }

    public static bool HasImmediateThreatBlock(OperatorDecisionSnapshot snapshot, out string reason)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
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

        if (safety.ImmediateCombatBlock && (safety.EnemyVisible || safety.EnemyCanShoot || safety.ThreatScanWouldPromote))
        {
            reason = "immediate_visible_or_promoted_combat_block";
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

    public static bool IsCriticalFastSurgeryCandidate(OperatorDecisionSnapshot snapshot, out string reason)
    {
        if (!IsValidActionableSurgery(snapshot, out var actionReason))
        {
            reason = actionReason;
            return false;
        }

        if (snapshot.Medical.Need.HasHeavyBleed || snapshot.Medical.Need.HasLightBleed)
        {
            reason = "bleed_priority_before_surgery";
            return false;
        }

        if (HasImmediateThreatBlock(snapshot, out var threatReason))
        {
            reason = "immediate_threat_blocks_fast_surgery:" + threatReason;
            return false;
        }

        TryResolveTarget(snapshot, out var target);
        reason = "critical_fast_surgery:" + target + ":hp=" + snapshot.Medical.Need.HealthPercent.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    public static string NormalizeTarget(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        string text = value.Trim()
            .Replace("_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();
        return text.Length == 0 ? "none" : text;
    }
}

internal enum VanguardSurgeryCandidateState
{
    Invalid = 0,
    Transient = 1,
    Ready = 2
}
#endif
