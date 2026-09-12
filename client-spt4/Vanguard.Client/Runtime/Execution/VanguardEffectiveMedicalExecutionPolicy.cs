#if SPT_CLIENT
using System;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Medical;
using Vanguard.Client.Runtime.Intents;

// Responsibility: Encodes the deterministic rules for Effective Medical Execution Policy within the execution arbitration runtime.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Runtime.Execution;

/// <summary>
/// Vanguard centralizes active medical execution and its authority boundary.
/// A diagnosis or remaining treatment debt is read-model data. A mobile treatment remains a sidecar;
/// only a committed stationary procedure may own the patient and exclude cohesion. This contract is
/// deliberately shared by the orchestrator and movement code.
/// </summary>
internal static class VanguardEffectiveMedicalExecutionPolicy
{
    public const string StatusTag = "VANGUARD_EFFECTIVE_MEDICAL_AUTHORITY_STATUS";
    public const string AuthorityContractStatusTag = "VANGUARD_MEDICAL_AUTHORITY_CONTRACT_STATUS";

    public static bool TryDescribeActiveExecution(OperatorDecisionSnapshot? snapshot, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        if (VanguardExecutionLeaseCoordinator.TryGetActiveLease(snapshot.BotProfileId, out var lease))
        {
            string descriptor = ((lease.WindowKind ?? string.Empty) + "|" + (lease.IntentKey ?? string.Empty)).ToLowerInvariant();
            bool medicalLease = descriptor.Contains("medical")
                || descriptor.Contains("surgery")
                || descriptor.Contains("fracture")
                || lease.MedicalNeed != VanguardMedicalNeed.None;
            if (medicalLease)
            {
                reason = "active_medical_lease:" + Safe(lease.WindowKind) + ":" + Safe(lease.IntentKey) + ":" + Safe(lease.LeaseId);
                return true;
            }
        }

        if (VanguardMainIntentScheduler.TryGetActivePrimaryWindow(snapshot.BotProfileId, now, out var windowKind, out var intentKey, out var state, out _)
            && IsMedicalDescriptor(windowKind, intentKey))
        {
            reason = "scheduler_medical_window:" + Safe(windowKind) + ":" + Safe(state) + ":" + Safe(intentKey);
            return true;
        }

        // The runtime authority contract: raw EFT controller flags are observations only. They may prove
        // that an animation/controller is still alive and therefore trigger completion or stale
        // recovery in the medical executor, but they never create Vanguard movement sovereignty.
        // Only an active Vanguard lease or scheduler window can own the patient.
        if (HasObservedEftController(snapshot))
        {
            reason = "eft_medical_controller_observed_without_vanguard_authority";
            return false;
        }

        reason = snapshot.Medical.Need.HasAnyNeed
            ? "passive_medical_debt_without_execution"
            : "no_medical_need_or_execution";
        return false;
    }

    public static bool TryDescribeExclusiveAuthority(OperatorDecisionSnapshot? snapshot, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (!TryDescribeActiveExecution(snapshot, now, out var executionReason) || snapshot == null)
        {
            reason = executionReason;
            return false;
        }

        if (IsStationaryExecution(snapshot, now, out var stationaryReason))
        {
            reason = stationaryReason;
            return true;
        }

        // First-aid, tourniquet, bandage, HP heal and stimulators remain compatible with the
        // cohesion movement domain. They are effective executions, but never movement sovereignty.
        reason = "active_mobile_medical_sidecar_not_exclusive:" + executionReason;
        return false;
    }

    public static bool IsUrgentMobileSidecarCandidate(OperatorDecisionSnapshot? snapshot, VanguardIntentCandidate? candidate)
    {
        if (snapshot == null || candidate == null || !candidate.Valid || !string.Equals(candidate.Domain, "Medical", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(candidate.IntentKey, "MobileMedicalStabilize", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool urgentMobileNeed = snapshot.Medical.Need.HasHeavyBleed || snapshot.Medical.Need.HasLightBleed;
        bool actionable = snapshot.Medical.Actionability.RequiredItemAvailable
            && snapshot.Medical.Actionability.CanApplyItem != false;
        return urgentMobileNeed && actionable;
    }

    public static bool IsStationaryExecution(OperatorDecisionSnapshot? snapshot, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (!TryDescribeActiveExecution(snapshot, now, out var executionReason) || snapshot == null)
        {
            reason = executionReason;
            return false;
        }

        // Never infer stationary ownership from an EFT animation/controller alone. An orphaned
        // controller is recovered by the execution path; granting it authority here would freeze
        // cohesion without a lease, exactly the stale Warden failure observed during runtime qualification.

        if (VanguardExecutionLeaseCoordinator.TryGetActiveLease(snapshot.BotProfileId, out var lease))
        {
            string descriptor = ((lease.WindowKind ?? string.Empty) + "|" + (lease.IntentKey ?? string.Empty)).ToLowerInvariant();
            bool stationary = descriptor.Contains("stationary") || descriptor.Contains("surgery") || descriptor.Contains("fracture")
                || lease.MedicalNeed == VanguardMedicalNeed.SurgeryDestroyedPart
                || lease.MedicalNeed == VanguardMedicalNeed.BlackBroken
                || lease.MedicalNeed == VanguardMedicalNeed.Fracture;
            if (stationary)
            {
                reason = "stationary_medical_lease:" + Safe(lease.LeaseId);
                return true;
            }
        }

        if (VanguardMainIntentScheduler.TryGetActivePrimaryWindow(snapshot.BotProfileId, now, out var windowKind, out var intentKey, out var state, out _)
            && VanguardPrimaryExecutionContract.IsStationaryMedicalKind(windowKind))
        {
            reason = "stationary_scheduler_window:" + Safe(windowKind) + ":" + Safe(state) + ":" + Safe(intentKey);
            return true;
        }

        reason = "effective_medical_is_mobile_or_unclassified:" + executionReason;
        return false;
    }


    public static bool HasObservedEftController(OperatorDecisionSnapshot? snapshot)
    {
        return snapshot != null
            && (snapshot.Medical.Actionability.AnyMedicineUsing
                || snapshot.Medical.Actionability.FirstAidUsing
                || snapshot.Medical.Actionability.SurgicalKitUsing
                || snapshot.Medical.Actionability.StimulatorUsing);
    }

    private static bool IsMedicalDescriptor(string? windowKind, string? intentKey)
    {
        string descriptor = ((windowKind ?? string.Empty) + "|" + (intentKey ?? string.Empty)).ToLowerInvariant();
        return descriptor.Contains("medical") || descriptor.Contains("surgery") || descriptor.Contains("fracture");
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
