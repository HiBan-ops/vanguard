#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Intents;
using Vanguard.Client.Runtime.Medical;

// Responsibility: Remembers a repairable black-limb surgery need across short interruptions so combat, movement or loot do not make the need disappear.
// Flow: Canonical medical state creates/refreshes a debt, inventory/actionability checks decide whether surgery is currently possible, and the debt is consumed only when recovery is proven or expires when the underlying injury is gone.
// Authority boundary: This service records intent/debt only; EFT health/inventory are truth and the medical scheduler/executors decide when an actual surgery action may run.
// Invariant: A debt must never force impossible surgery, block higher-priority survival indefinitely, or survive after the injury/actionability facts that created it are no longer true.
namespace Vanguard.Client.Runtime.Medical.Execution;

/// <summary>
/// Keeps a short-lived but persistent surgery debt for operable black parts.
/// runtime qualification proved that the ideal surgery window works; the runtime prevents the scheduler from
/// treating a failed/interrupted/no-effect surgery as a consumed opportunity while the
/// same operable black part is still present.
/// </summary>
internal static class VanguardSurgeryDebtService
{
    public const string StatusTag = "VANGUARD_MEDICAL_SURGERY_DEBT_RETRY_OK";

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2.50d);
    private static readonly TimeSpan ThreatRetryDelay = TimeSpan.FromSeconds(1.50d);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(2.00d);
    private static readonly Dictionary<string, SurgeryDebt> DebtByBotProfile = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogAtByKey = new(StringComparer.OrdinalIgnoreCase);

    public static void Reset(string reason)
    {
        DebtByBotProfile.Clear();
        LastLogAtByKey.Clear();
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_MEDICAL_SURGERY_DEBT_RESET reason={Safe(reason)}; active=false; tag={StatusTag}");
    }

    public static void UpdateFromSnapshots(IEnumerable<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        foreach (var snapshot in snapshots ?? Enumerable.Empty<OperatorDecisionSnapshot>())
        {
            UpdateFromSnapshot(snapshot, now);
        }
    }

    public static void UpdateFromSnapshot(OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        if (string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            return;
        }

        if (!snapshot.Alive)
        {
            Clear(snapshot.BotProfileId, now, "operator_dead", snapshot.OperatorId);
            return;
        }

        if (!TryReadCurrentDebt(snapshot, out var debt, out var clearReason))
        {
            if ((string.Equals(clearReason, "target_unknown", StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(clearReason, "need_not_surgery", StringComparison.OrdinalIgnoreCase)
                        && (snapshot.Medical.Need.HasOperableDestroyedPart || snapshot.Medical.Need.HasBlackBroken)))
                && DebtByBotProfile.TryGetValue(snapshot.BotProfileId, out var unresolvedDebt))
            {
                unresolvedDebt.LastSeenAtUtc = now;
                unresolvedDebt.Actionable = false;
                unresolvedDebt.LastActionabilityReason = string.Equals(clearReason, "target_unknown", StringComparison.OrdinalIgnoreCase)
                    ? "target_temporarily_unknown_keep_debt"
                    : "dominant_need_temporarily_not_surgery_keep_debt";
                Log(StatusTag, "target_unknown_keep|" + snapshot.BotProfileId + "|" + unresolvedDebt.TargetPart + "|" + clearReason, now,
                    $"VANGUARD_MEDICAL_SURGERY_DEBT_TARGET_UNKNOWN_KEEP operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; previousTarget={Safe(unresolvedDebt.TargetPart)}; reason={Safe(unresolvedDebt.LastActionabilityReason)}; dominantNeed={snapshot.Medical.Need.DominantNeed}; black={Bool(snapshot.Medical.Need.HasDestroyedPart)}; blackBroken={Bool(snapshot.Medical.Need.HasBlackBroken)}; tag={StatusTag}");
                return;
            }

            Clear(snapshot.BotProfileId, now, clearReason, snapshot.OperatorId);
            return;
        }

        if (!DebtByBotProfile.TryGetValue(snapshot.BotProfileId, out var current)
            || !string.Equals(current.TargetPart, debt.TargetPart, StringComparison.OrdinalIgnoreCase))
        {
            debt.FirstDetectedAtUtc = now;
            debt.RetryAllowedAtUtc = now;
            debt.RetryState = debt.Actionable ? "Ready" : "RetryPendingNotActionable";
            DebtByBotProfile[snapshot.BotProfileId] = debt;
            Log(StatusTag, "created|" + snapshot.BotProfileId + "|" + debt.TargetPart, now,
                $"VANGUARD_MEDICAL_SURGERY_DEBT_CREATED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(debt.TargetPart)}; need={debt.Need}; item={Safe(debt.ItemName)}; tpl={Safe(debt.ItemTemplateId)}; reason=operable_black_part_detected; retryAt={debt.RetryAllowedAtUtc:O}; tag={StatusTag}");
            return;
        }

        current.Need = debt.Need;
        current.TargetPart = debt.TargetPart;
        current.ItemTemplateId = debt.ItemTemplateId;
        current.ItemName = debt.ItemName;
        current.LastSeenAtUtc = now;
        current.Actionable = debt.Actionable;
        string previousReadinessSignature = current.LastReadinessSignature;
        string currentReadinessSignature = BuildReadinessSignature(snapshot);
        current.LastReadinessSignature = currentReadinessSignature;
        current.LastActionabilityReason = debt.LastActionabilityReason;
        if (!string.Equals(previousReadinessSignature, currentReadinessSignature, StringComparison.OrdinalIgnoreCase)
            && VanguardMedicalSurgeryTargetPolicy.EvaluateSurgeryPreparationCandidate(snapshot, out var readinessReason) == VanguardSurgeryCandidateState.Ready)
        {
            current.RetryAllowedAtUtc = now;
            current.Actionable = true;
            current.RetryState = "HandsStateChangedRetryReady";
            current.LastActionabilityReason = readinessReason;
            Log(StatusTag, "hands_state_wake|" + snapshot.BotProfileId + "|" + current.TargetPart, now,
                $"VANGUARD_MEDICAL_SURGERY_DEBT_HANDS_STATE_WAKE operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(current.TargetPart)}; previous={Safe(previousReadinessSignature)}; current={Safe(currentReadinessSignature)}; retryAt={current.RetryAllowedAtUtc:O}; authorityMutation=false; tag={StatusTag}; Tag={VanguardRuntimeLivenessStatus.StatusTag}");
        }
        if (current.RetryAllowedAtUtc == DateTimeOffset.MinValue)
        {
            current.RetryAllowedAtUtc = now;
        }

        if (current.BlockedUntilStateChange)
        {
            bool alternativeInstanceAvailable = TryFindAlternativeSurgeryInstance(snapshot, current, out var alternativeSummary);
            string currentSignature = BuildStateBoundBlockSignature(snapshot, current.TargetPart, current.FailedItemInstanceIds);
            if (!alternativeInstanceAvailable
                && string.Equals(current.StateBoundBlockSignature, currentSignature, StringComparison.OrdinalIgnoreCase))
            {
                current.Actionable = false;
                current.RetryAllowedAtUtc = DateTimeOffset.MaxValue;
                current.RetryState = "BlockedUntilStateChange";
                current.LastActionabilityReason = "retry_cap_no_effect_state_unchanged";
                LogLazy(StatusTag, "state_bound_hold|" + snapshot.BotProfileId + "|" + current.TargetPart, now, VanguardAuditLevel.Trace, () =>
                    $"VANGUARD_MEDICAL_SURGERY_DEBT_STATE_BOUND_HOLD operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(current.TargetPart)}; failedInstance={Safe(current.ItemInstanceId)}; alternativeAvailable=false; blockedUntil=target_restored_or_alternative_item_available; wallClockRelease=false; signaturePayload=false; tag={StatusTag}; Tag={VanguardMedicalCohesionStatusTags.SequentialSurgeryBoundary}");
                return;
            }

            bool previousSignatureChanged = !string.Equals(current.StateBoundBlockSignature, currentSignature, StringComparison.OrdinalIgnoreCase);
            current.BlockedUntilStateChange = false;
            current.StateBoundBlockSignature = "none";
            current.RetryAllowedAtUtc = now;
            current.RetryState = "StateChangedRetryReady";
            current.Actionable = debt.Actionable;
            current.LastActionabilityReason = alternativeInstanceAvailable ? "alternative_surgery_instance_available" : debt.LastActionabilityReason;
            Log(StatusTag, "state_bound_release|" + snapshot.BotProfileId + "|" + current.TargetPart, now,
                $"VANGUARD_MEDICAL_SURGERY_DEBT_STATE_BOUND_RELEASED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(current.TargetPart)}; previousSignatureChanged={Bool(previousSignatureChanged)}; alternativeInstanceAvailable={Bool(alternativeInstanceAvailable)}; alternative={Safe(alternativeSummary)}; currentSignature={Safe(currentSignature)}; retryAt={current.RetryAllowedAtUtc:O}; tag={StatusTag}; Tag={VanguardMedicalCohesionStatusTags.SequentialSurgeryBoundary}");
        }

        if (!current.Actionable)
        {
            string state = current.LastActionabilityReason.IndexOf("item_missing", StringComparison.OrdinalIgnoreCase) >= 0
                || current.LastActionabilityReason.IndexOf("fracture_fallback", StringComparison.OrdinalIgnoreCase) >= 0
                ? "BlockedItemMissing"
                : current.LastActionabilityReason.IndexOf("controller", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "WaitingForControllerSettle"
                    : "RetryPendingNotActionable";
            current.RetryState = state;
            Log(StatusTag, "blocked_state|" + snapshot.BotProfileId + "|" + current.TargetPart + "|" + state, now,
                $"VANGUARD_MEDICAL_SURGERY_DEBT_BLOCKED_STATE operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(current.TargetPart)}; state={state}; reason={Safe(current.LastActionabilityReason)}; retryAt={current.RetryAllowedAtUtc:O}; debtPreserved=true; otherActionableMedicalAllowed=true; tag={StatusTag}");
        }
    }

    public static int AppendFailedItemInstanceExclusions(OperatorDecisionSnapshot snapshot, ISet<string> excludedItemInstances, DateTimeOffset now, out string summary)
    {
        summary = "excluded=0";
        snapshot ??= OperatorDecisionSnapshot.Empty;
        if (excludedItemInstances == null
            || string.IsNullOrWhiteSpace(snapshot.BotProfileId)
            || !DebtByBotProfile.TryGetValue(snapshot.BotProfileId, out var debt)
            || debt.FailedItemInstanceIds.Count == 0)
        {
            return 0;
        }

        if (!TryResolveDebtTarget(snapshot, out var currentTarget) || !SameTarget(currentTarget, debt.TargetPart))
        {
            return 0;
        }

        int added = 0;
        foreach (string failedInstance in debt.FailedItemInstanceIds)
        {
            if (!string.IsNullOrWhiteSpace(failedInstance)
                && !string.Equals(failedInstance, "none", StringComparison.OrdinalIgnoreCase)
                && excludedItemInstances.Add(failedInstance))
            {
                added++;
            }
        }

        summary = "excluded=" + added + ";target=" + Safe(debt.TargetPart)
            + ";failedInstances=" + Safe(string.Join(",", debt.FailedItemInstanceIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)));
        if (added > 0)
        {
            Log(StatusTag, "failed_instance_exclusion|" + snapshot.BotProfileId + "|" + debt.TargetPart, now,
                $"VANGUARD_SURGERY_FAILED_INSTANCE_EXCLUDED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(debt.TargetPart)}; {summary}; scope=current_unresolved_surgery_episode; otherKitsAllowed=true; tag={StatusTag}");
        }
        return added;
    }

    public static void RecordFailedItemInstance(VanguardExecutionLeaseState lease, DateTimeOffset now, string reason)
    {
        if (lease == null
            || !VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(lease.MedicalNeed)
            || string.IsNullOrWhiteSpace(lease.BotProfileId)
            || string.IsNullOrWhiteSpace(lease.ItemInstanceId)
            || string.Equals(lease.ItemInstanceId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var debt = GetOrCreateFromLease(lease, now);
        debt.ItemTemplateId = lease.ItemTemplateId;
        debt.ItemName = lease.ItemName;
        debt.ItemInstanceId = Safe(lease.ItemInstanceId);
        debt.FailedItemInstanceIds.Add(lease.ItemInstanceId);
        debt.LastFailureReason = Safe(reason);
        debt.LastSeenAtUtc = now;
        DebtByBotProfile[lease.BotProfileId] = debt;
        Log(StatusTag, "failed_instance_recorded|" + lease.BotProfileId + "|" + lease.TargetPart + "|" + lease.ItemInstanceId, now,
            $"VANGUARD_SURGERY_FAILED_INSTANCE_RECORDED {lease.Summary}; reason={Safe(reason)}; failedInstance={Safe(lease.ItemInstanceId)}; failedCount={debt.FailedItemInstanceIds.Count}; scope=current_unresolved_surgery_episode; sameInstanceRetry=false; tag={StatusTag}");
    }

    public static void BlockUntilStateChange(VanguardExecutionLeaseState lease, OperatorDecisionSnapshot snapshot, VanguardMedicalActionProgressSnapshot progress, DateTimeOffset now, string reason)
    {
        if (lease == null
            || snapshot == null
            || !VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(lease.MedicalNeed)
            || string.IsNullOrWhiteSpace(lease.BotProfileId))
        {
            return;
        }

        var debt = GetOrCreateFromLease(lease, now);
        debt.LastSeenAtUtc = now;
        debt.LastAttemptAtUtc = now;
        debt.LastFailureReason = Safe(reason);
        debt.ItemInstanceId = Safe(lease.ItemInstanceId);
        debt.FailedItemInstanceIds.Add(lease.ItemInstanceId);
        debt.LastOutcome = VanguardMedicalActionOutcomeKind.Failed.ToString();
        debt.BlockedUntilStateChange = true;
        debt.StateBoundBlockSignature = BuildStateBoundBlockSignature(snapshot, lease.TargetPart, debt.FailedItemInstanceIds);
        debt.RetryAllowedAtUtc = DateTimeOffset.MaxValue;
        debt.RetryState = "BlockedUntilStateChange";
        debt.Actionable = false;
        debt.LastActionabilityReason = "retry_cap_no_effect_state_unchanged";
        DebtByBotProfile[lease.BotProfileId] = debt;

        Log(StatusTag, "state_bound_block|" + lease.BotProfileId + "|" + lease.TargetPart, now,
            $"VANGUARD_MEDICAL_SURGERY_DEBT_STATE_BOUND_BLOCK {lease.Summary}; reason={Safe(reason)}; signature={Safe(debt.StateBoundBlockSignature)}; attempts={lease.SurgeryApplyAttemptCount}; targetStillPresent={Bool(progress.TargetStillPresent)}; needStillPresent={Bool(progress.NeedStillPresent)}; blockedUntil=medical_or_target_or_item_state_change; wallClockRelease=false; tag={StatusTag}; Tag={VanguardMedicalCohesionStatusTags.SequentialSurgeryBoundary}");
    }

    public static void RegisterOutcome(VanguardExecutionLeaseState lease, VanguardMedicalActionProgressSnapshot? progress, DateTimeOffset now, VanguardMedicalActionOutcomeKind outcome, string reason)
    {
        if (lease == null || !VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(lease.MedicalNeed) || string.IsNullOrWhiteSpace(lease.BotProfileId))
        {
            return;
        }

        if (outcome == VanguardMedicalActionOutcomeKind.Completed && progress != null && !progress.NeedStillPresent && !progress.TargetStillPresent)
        {
            Clear(lease.BotProfileId, now, "resolved:" + Safe(reason), lease.OperatorId);
            return;
        }

        if (!ShouldKeepDebt(lease, progress, outcome, reason))
        {
            return;
        }

        var debt = GetOrCreateFromLease(lease, now);
        debt.LastSeenAtUtc = now;
        debt.LastAttemptAtUtc = now;
        debt.LastFailureReason = Safe(reason);
        debt.LastOutcome = outcome.ToString();
        if (debt.BlockedUntilStateChange)
        {
            debt.RetryState = "BlockedUntilStateChange";
            debt.RetryAllowedAtUtc = DateTimeOffset.MaxValue;
            debt.Actionable = false;
            debt.LastActionabilityReason = "retry_cap_no_effect_state_unchanged";
        }
        else
        {
            debt.RetryState = outcome == VanguardMedicalActionOutcomeKind.Interrupted
                ? "InterruptedRetryPending"
                : IsTrueThreatReason(reason) ? "BlockedByCombat" : "RetryPending";
            debt.RetryAllowedAtUtc = now + (IsTrueThreatReason(reason) ? ThreatRetryDelay : RetryDelay);
            debt.Actionable = true;
            debt.LastActionabilityReason = progress == null ? "progress_unknown" : progress.EffectSummary;
        }
        debt.FailureCount++;
        DebtByBotProfile[lease.BotProfileId] = debt;

        Log(StatusTag, "retry_pending|" + lease.BotProfileId + "|" + lease.TargetPart + "|" + reason, now,
            $"VANGUARD_MEDICAL_SURGERY_DEBT_RETRY_PENDING {lease.Summary}; outcome={outcome}; reason={Safe(reason)}; retryState={Safe(debt.RetryState)}; targetStillPresent={Bool(progress?.TargetStillPresent == true)}; needStillPresent={Bool(progress?.NeedStillPresent == true)}; retryAt={debt.RetryAllowedAtUtc:O}; failureCount={debt.FailureCount}; cooldownScope=controller_retry_not_medical_need; tag={StatusTag}");
    }

    public static bool ShouldBypassOutcomeCooldown(OperatorDecisionSnapshot snapshot, VanguardMobileMedicalActionSelection selection, DateTimeOffset now, DateTimeOffset blockedUntilUtc, out string reason)
    {
        reason = "none";
        snapshot ??= OperatorDecisionSnapshot.Empty;
        if (selection == null || !VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(selection.Need))
        {
            return false;
        }

        if (!TryGetDueDebt(snapshot, now, out var debt, out reason))
        {
            return false;
        }

        if (!SameTarget(debt.TargetPart, selection.TargetPartName))
        {
            reason = "debt_target_mismatch:" + Safe(debt.TargetPart) + "_vs_" + Safe(selection.TargetPartName);
            return false;
        }

        if (IsIsolationAdmissionBoundaryCooldown(
                snapshot.BotProfileId,
                selection.Need,
                selection.TargetPartName,
                selection.ItemTemplateId,
                now,
                out var admissionReason))
        {
            reason = admissionReason;
            Log(StatusTag, "cooldown_honored|" + snapshot.BotProfileId + "|" + debt.TargetPart, now,
                $"VANGUARD_SURGERY_ADMISSION_COOLDOWN_HONORED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(debt.TargetPart)}; blockedUntil={blockedUntilUtc:O}; reason={Safe(reason)}; bypass=false; doctrine=surgery_debt_cannot_retry_a_missing_isolation_each_frame; tag=VANGUARD_RUNTIME_BOUNDARY_CONVERGENCE_STATUS; debtTag={StatusTag}");
            return false;
        }

        reason = "persistent_surgery_debt_bypasses_outcome_cooldown_until_" + blockedUntilUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        Log(StatusTag, "cooldown_bypass|" + snapshot.BotProfileId + "|" + debt.TargetPart, now,
            $"VANGUARD_MEDICAL_SURGERY_DEBT_COOLDOWN_BYPASS operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(debt.TargetPart)}; blockedUntil={blockedUntilUtc:O}; retryReason={Safe(reason)}; tag={StatusTag}");
        return true;
    }

    public static bool ShouldBypassOutcomeCooldown(OperatorDecisionSnapshot snapshot, DateTimeOffset now, DateTimeOffset blockedUntilUtc, out string reason)
    {
        reason = "none";
        if (!TryGetDueDebt(snapshot, now, out var debt, out reason))
        {
            return false;
        }

        string targetPart = !string.IsNullOrWhiteSpace(snapshot.Medical.Actionability.TargetPart)
            && !string.Equals(snapshot.Medical.Actionability.TargetPart, "none", StringComparison.OrdinalIgnoreCase)
                ? snapshot.Medical.Actionability.TargetPart
                : snapshot.Medical.Need.TargetPart;
        if (IsIsolationAdmissionBoundaryCooldown(
                snapshot.BotProfileId,
                snapshot.Medical.Need.DominantNeed,
                targetPart,
                snapshot.Medical.Actionability.SelectedItemTemplateId,
                now,
                out var admissionReason))
        {
            reason = admissionReason;
            Log(StatusTag, "prepare_cooldown_honored|" + snapshot.BotProfileId + "|" + debt.TargetPart, now,
                $"VANGUARD_SURGERY_PREPARE_COOLDOWN_HONORED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(debt.TargetPart)}; blockedUntil={blockedUntilUtc:O}; reason={Safe(reason)}; bypass=false; doctrine=isolation_rejection_cooldown_precedes_debt_retry; tag=VANGUARD_RUNTIME_BOUNDARY_CONVERGENCE_STATUS; debtTag={StatusTag}");
            return false;
        }

        reason = "persistent_surgery_debt_prepare_bypasses_outcome_cooldown_until_" + blockedUntilUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        Log(StatusTag, "prepare_cooldown_bypass|" + snapshot.BotProfileId + "|" + debt.TargetPart, now,
            $"VANGUARD_MEDICAL_SURGERY_DEBT_PREPARE_COOLDOWN_BYPASS operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(debt.TargetPart)}; blockedUntil={blockedUntilUtc:O}; tag={StatusTag}");
        return true;
    }

    private static bool IsIsolationAdmissionBoundaryCooldown(
        string? botProfileId,
        VanguardMedicalNeed need,
        string? targetPart,
        string? itemTemplateId,
        DateTimeOffset now,
        out string reason)
    {
        reason = "none";
        if (!VanguardExecutionLeaseCoordinator.TryGetOutcome(
                botProfileId,
                need,
                targetPart,
                itemTemplateId,
                out var outcome)
            || outcome.RetryAllowedAtUtc <= now)
        {
            return false;
        }

        bool isolationAdmissionRejected = outcome.Reason.IndexOf(
                "medical_isolation_not_ready",
                StringComparison.OrdinalIgnoreCase) >= 0
            || outcome.Reason.IndexOf(
                "go_cover_required_no_direct_stationary_isolation",
                StringComparison.OrdinalIgnoreCase) >= 0;
        if (!isolationAdmissionRejected)
        {
            return false;
        }

        reason = "isolation_admission_cooldown_until_"
            + outcome.RetryAllowedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            + ":" + Safe(outcome.Reason);
        return true;
    }

    public static bool ShouldForcePrepare(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (!TryGetDueDebt(snapshot, now, out var debt, out reason))
        {
            return false;
        }

        if (!IsPrepareActionable(snapshot, out var actionReason))
        {
            reason = "debt_prepare_not_actionable:" + actionReason;
            return false;
        }

        reason = "persistent_surgery_debt_due:" + Safe(debt.TargetPart);
        return true;
    }

    public static bool ShouldIgnoreSoftPrepareThreat(OperatorDecisionSnapshot snapshot, DateTimeOffset now, string threatReason, out string reason)
    {
        reason = "none";
        if (!TryGetDueDebt(snapshot, now, out var debt, out reason))
        {
            return false;
        }

        if (IsTrueThreatReason(threatReason) || HasTrueThreat(snapshot))
        {
            reason = "true_threat_keeps_prepare_block:" + Safe(threatReason);
            return false;
        }

        if (!IsPrepareActionable(snapshot, out var actionReason))
        {
            reason = "debt_prepare_not_actionable:" + actionReason;
            return false;
        }

        reason = "persistent_surgery_debt_ignores_soft_prepare_threat:" + Safe(debt.TargetPart) + ":" + Safe(threatReason);
        Log(StatusTag, "soft_threat_ignore|" + snapshot.BotProfileId + "|" + debt.TargetPart + "|" + threatReason, now,
            $"VANGUARD_MEDICAL_SURGERY_DEBT_SOFT_THREAT_IGNORED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(debt.TargetPart)}; originalThreat={Safe(threatReason)}; trueThreat=false; action=retry_cover_prepare; tag={StatusTag}");
        return true;
    }

    public static bool HasDueDebt(OperatorDecisionSnapshot snapshot, out string reason)
    {
        if (!TryGetDueDebt(snapshot, DateTimeOffset.UtcNow, out _, out reason))
        {
            return false;
        }

        // A debt remains persistent across transient hands states, but it must not
        // suppress follow, loot or other squad work until the existing preparation
        // path can actually admit it. Persistence and scheduler due-ness are distinct.
        if (!IsPrepareActionable(snapshot, out var actionReason))
        {
            reason = "debt_persistent_but_admission_not_ready:" + actionReason;
            return false;
        }

        return true;
    }

    public static bool HasTrueThreat(OperatorDecisionSnapshot snapshot)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        return snapshot.Medical.Safety.EnemyCanShoot
            || snapshot.Medical.Safety.IncomingFireRecent
            || snapshot.Threat.EnemyCanShoot == true
            || snapshot.ThreatScan.CandidateCanShoot
            || snapshot.ThreatScan.CandidateIncomingFireFresh
            || snapshot.ThreatScan.CandidateShotMeRecently
            || snapshot.ThreatScan.CandidateShotAtMeRecently
            || snapshot.Awareness.CandidateCanShoot
            || snapshot.Awareness.IncomingFireFresh;
    }

    public static void MarkRetrySelected(OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason)
    {
        if (!DebtByBotProfile.TryGetValue(snapshot.BotProfileId, out var debt))
        {
            return;
        }

        Log(StatusTag, "retry_selected|" + snapshot.BotProfileId + "|" + debt.TargetPart, now,
            $"VANGUARD_MEDICAL_SURGERY_DEBT_RETRY_SELECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(debt.TargetPart)}; reason={Safe(reason)}; retryAt={debt.RetryAllowedAtUtc:O}; failureCount={debt.FailureCount}; tag={StatusTag}");
    }

    private static bool TryGetDueDebt(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out SurgeryDebt debt, out string reason)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        UpdateFromSnapshot(snapshot, now);
        if (!DebtByBotProfile.TryGetValue(snapshot.BotProfileId, out var existingDebt))
        {
            debt = new SurgeryDebt();
            reason = "no_surgery_debt";
            return false;
        }

        debt = existingDebt;
        if (!debt.Actionable)
        {
            reason = "debt_not_actionable:" + Safe(debt.LastActionabilityReason);
            return false;
        }

        if (HasTrueThreat(snapshot))
        {
            debt.RetryAllowedAtUtc = now + ThreatRetryDelay;
            debt.RetryState = "BlockedByCombat";
            reason = "true_threat_delays_debt_retry";
            Log(StatusTag, "blocked_true_threat|" + snapshot.BotProfileId + "|" + debt.TargetPart, now,
                $"VANGUARD_MEDICAL_STATIONARY_DEBT_BLOCKED_BY_TRUE_THREAT operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(debt.TargetPart)}; enemyCanShoot={Bool(snapshot.Medical.Safety.EnemyCanShoot)}; incomingFire={Bool(snapshot.Medical.Safety.IncomingFireRecent)}; retryAt={debt.RetryAllowedAtUtc:O}; tag={StatusTag}");
            return false;
        }

        if (now < debt.RetryAllowedAtUtc)
        {
            debt.RetryState = "RetryPendingDelay";
            reason = "debt_retry_wait_until_" + debt.RetryAllowedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            return false;
        }

        debt.RetryState = "Ready";
        reason = "debt_due:" + Safe(debt.TargetPart);
        return true;
    }

    private static bool TryReadCurrentDebt(OperatorDecisionSnapshot snapshot, out SurgeryDebt debt, out string clearReason)
    {
        debt = new SurgeryDebt();
        clearReason = "none";
        bool dominantSurgery = VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(snapshot.Medical.Need.DominantNeed);
        string target;
        if (dominantSurgery)
        {
            if (!VanguardMedicalSurgeryTargetPolicy.TryResolveTarget(snapshot, out target))
            {
                clearReason = "target_unknown";
                return false;
            }
        }
        else if (snapshot.Medical.Need.HasOperableDestroyedPart && TryResolveDestroyedPart(snapshot.Medical.Need.DestroyedParts, out target))
        {
            // The runtime actionable triage may temporarily expose fracture as the next executable action
            // while a non-actionable surgery debt remains persistent in the background.
        }
        else
        {
            clearReason = "need_not_surgery";
            return false;
        }

        if (VanguardMedicalSurgeryTargetPolicy.IsUntreatableVitalTarget(target) || !VanguardMedicalSurgeryTargetPolicy.IsValidSurgeryTarget(target))
        {
            clearReason = "target_not_operable:" + Safe(target);
            return false;
        }

        var actionability = snapshot.Medical.Actionability;
        bool actionable = false;
        string actionReason = "surgery_item_missing_actionable_fracture_fallback";
        if (dominantSurgery)
        {
            actionable = VanguardMedicalSurgeryTargetPolicy.HasPersistentSurgeryCapability(snapshot, out actionReason);
        }

        debt = new SurgeryDebt
        {
            BotProfileId = snapshot.BotProfileId,
            OperatorId = snapshot.OperatorId,
            Need = VanguardMedicalNeed.SurgeryDestroyedPart,
            TargetPart = target,
            ItemTemplateId = dominantSurgery ? actionability.SelectedItemTemplateId : "none",
            ItemName = dominantSurgery ? actionability.SelectedItemName : "none",
            LastSeenAtUtc = DateTimeOffset.UtcNow,
            LastReadinessSignature = BuildReadinessSignature(snapshot),
            Actionable = actionable,
            LastActionabilityReason = actionReason
        };
        return true;
    }

    private static bool TryResolveDestroyedPart(string? list, out string target)
    {
        target = "none";
        if (string.IsNullOrWhiteSpace(list) || string.Equals(list, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (string token in list.Split(','))
        {
            string candidate = token.Trim();
            if (VanguardMedicalSurgeryTargetPolicy.IsValidSurgeryTarget(candidate))
            {
                target = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsPrepareActionable(OperatorDecisionSnapshot snapshot, out string reason)
    {
        if (snapshot.Medical.Need.HasHeavyBleed || snapshot.Medical.Need.HasLightBleed)
        {
            reason = "bleed_priority_before_surgery";
            return false;
        }

        var state = VanguardMedicalSurgeryTargetPolicy.EvaluateSurgeryPreparationCandidate(snapshot, out reason);
        return state == VanguardSurgeryCandidateState.Ready;
    }

    private static string BuildReadinessSignature(OperatorDecisionSnapshot snapshot)
    {
        var actionability = snapshot.Medical.Actionability;
        return "reload=" + Bool(actionability.Reloading)
            + "|grenade=" + Bool(actionability.GrenadeThrowing)
            + "|medicine=" + Bool(actionability.AnyMedicineUsing)
            + "|canApply=" + (actionability.CanApplyItem.HasValue ? Bool(actionability.CanApplyItem.Value) : "unknown")
            + "|item=" + Safe(actionability.SelectedItemTemplateId)
            + "|target=" + Safe(actionability.TargetPart);
    }

    private static SurgeryDebt GetOrCreateFromLease(VanguardExecutionLeaseState lease, DateTimeOffset now)
    {
        if (!DebtByBotProfile.TryGetValue(lease.BotProfileId, out var debt))
        {
            debt = new SurgeryDebt
            {
                BotProfileId = lease.BotProfileId,
                OperatorId = lease.OperatorId,
                Need = lease.MedicalNeed,
                TargetPart = SafeTarget(lease.TargetPart),
                ItemTemplateId = lease.ItemTemplateId,
                ItemName = lease.ItemName,
                ItemInstanceId = Safe(lease.ItemInstanceId),
                FirstDetectedAtUtc = now,
                LastSeenAtUtc = now,
                RetryAllowedAtUtc = now
            };
        }

        debt.ItemTemplateId = lease.ItemTemplateId;
        debt.ItemName = lease.ItemName;
        debt.ItemInstanceId = Safe(lease.ItemInstanceId);
        return debt;
    }

    private static bool ShouldKeepDebt(VanguardExecutionLeaseState lease, VanguardMedicalActionProgressSnapshot? progress, VanguardMedicalActionOutcomeKind outcome, string reason)
    {
        if (outcome == VanguardMedicalActionOutcomeKind.Completed && progress != null && !progress.TargetStillPresent && !progress.NeedStillPresent)
        {
            return false;
        }

        if (progress != null && (progress.TargetStillPresent || progress.NeedStillPresent))
        {
            return true;
        }

        string text = reason ?? string.Empty;
        return text.IndexOf("WhileControllerUsing", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("NoMedicalEffect", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("NeedStillPresent", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("MaxWindow", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("Timeout", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("Interrupted", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("WindowBroken", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsTrueThreatReason(string? reason)
    {
        string text = reason ?? string.Empty;
        return text.IndexOf("enemy_can_shoot", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("incoming_fire", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("shot_me", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("critical_threat", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("true_surgery_threat", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool TryFindAlternativeSurgeryInstance(OperatorDecisionSnapshot snapshot, SurgeryDebt debt, out string summary)
    {
        summary = "alternative=false";
        if (debt.FailedItemInstanceIds.Count == 0
            || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var runtime)
            || runtime.BotOwner == null)
        {
            summary = "alternative=false;reason=failed_instance_or_runtime_missing";
            return false;
        }

        var excluded = new HashSet<string>(debt.FailedItemInstanceIds, StringComparer.OrdinalIgnoreCase);
        if (excluded.Count == 0 && !string.IsNullOrWhiteSpace(debt.ItemInstanceId))
        {
            excluded.Add(debt.ItemInstanceId);
        }
        if (!VanguardMobileMedicalActionSelector.TrySelect(runtime.BotOwner, snapshot, excluded, out var alternative, out var reason)
            || (alternative.Need != VanguardMedicalNeed.SurgeryDestroyedPart && alternative.Need != VanguardMedicalNeed.BlackBroken)
            || !SameTarget(alternative.TargetPartName, debt.TargetPart)
            || string.Equals(alternative.ItemInstanceId, debt.ItemInstanceId, StringComparison.OrdinalIgnoreCase))
        {
            summary = "alternative=false;reason=" + Safe(reason);
            return false;
        }

        summary = "alternative=true;item=" + Safe(alternative.ItemName)
            + ";tpl=" + Safe(alternative.ItemTemplateId)
            + ";instance=" + Safe(alternative.ItemInstanceId);
        return true;
    }

    private static string BuildStateBoundBlockSignature(OperatorDecisionSnapshot snapshot, string? targetPart, IEnumerable<string> failedItemInstanceIds)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        string normalizedTarget = SafeTarget(targetPart);
        bool targetStillDestroyed = snapshot.Medical.Need.HasOperableDestroyedPart
            && (string.Equals(SafeTarget(snapshot.Medical.Need.TargetPart), normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || (snapshot.Medical.Need.DestroyedParts ?? string.Empty).IndexOf(normalizedTarget, StringComparison.OrdinalIgnoreCase) >= 0);
        string failedInstances = string.Join(",", (failedItemInstanceIds ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        return "target=" + normalizedTarget
            + "|failedInstances=" + Safe(failedInstances)
            + "|targetDestroyed=" + Bool(targetStillDestroyed);
    }

    private static bool TryResolveDebtTarget(OperatorDecisionSnapshot snapshot, out string target)
    {
        if (VanguardMedicalSurgeryTargetPolicy.TryResolveTarget(snapshot, out target))
        {
            return true;
        }
        return snapshot.Medical.Need.HasOperableDestroyedPart
            && TryResolveDestroyedPart(snapshot.Medical.Need.DestroyedParts, out target);
    }

    private static bool SameTarget(string? left, string? right)
    {
        return string.Equals(SafeTarget(left), SafeTarget(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeTarget(string? target)
    {
        string normalized = VanguardMedicalSurgeryTargetPolicy.NormalizeTarget(target);
        return normalized == "none" ? Safe(target) : normalized;
    }

    private static void Clear(string botProfileId, DateTimeOffset now, string reason, string operatorId)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return;
        }

        if (DebtByBotProfile.TryGetValue(botProfileId, out var debt))
        {
            DebtByBotProfile.Remove(botProfileId);
            Log(StatusTag, "cleared|" + botProfileId + "|" + reason, now,
                $"VANGUARD_MEDICAL_SURGERY_DEBT_CLEARED operator={Safe(operatorId)}; botProfile={Safe(botProfileId)}; target={Safe(debt.TargetPart)}; reason={Safe(reason)}; failureCount={debt.FailureCount}; tag={StatusTag}");
        }
    }

    private static void Log(string tag, string key, DateTimeOffset now, string message)
    {
        if (LastLogAtByKey.TryGetValue(key, out var last) && now - last < LogInterval)
        {
            return;
        }

        LastLogAtByKey[key] = now;
        VanguardClientDiagnosticsLog.Info(tag, message);
    }

    private static void LogLazy(
        string tag,
        string key,
        DateTimeOffset now,
        VanguardAuditLevel minimumLevel,
        Func<string> messageFactory)
    {
        if (!VanguardClientDiagnosticsLog.IsEnabled(minimumLevel))
        {
            return;
        }

        if (LastLogAtByKey.TryGetValue(key, out var last) && now - last < LogInterval)
        {
            return;
        }

        LastLogAtByKey[key] = now;
        VanguardClientDiagnosticsLog.Info(tag, minimumLevel, messageFactory);
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }

    private sealed class SurgeryDebt
    {
        public string BotProfileId { get; init; } = string.Empty;
        public string OperatorId { get; init; } = string.Empty;
        public VanguardMedicalNeed Need { get; set; } = VanguardMedicalNeed.None;
        public string TargetPart { get; set; } = "none";
        public string ItemTemplateId { get; set; } = "none";
        public string ItemName { get; set; } = "none";
        public string ItemInstanceId { get; set; } = "none";
        public HashSet<string> FailedItemInstanceIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset FirstDetectedAtUtc { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastSeenAtUtc { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastAttemptAtUtc { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset RetryAllowedAtUtc { get; set; } = DateTimeOffset.MinValue;
        public string LastOutcome { get; set; } = "none";
        public string LastFailureReason { get; set; } = "none";
        public int FailureCount { get; set; }
        public bool Actionable { get; set; }
        public string LastReadinessSignature { get; set; } = "none";
        public string LastActionabilityReason { get; set; } = "none";
        public string RetryState { get; set; } = "Detected";
        public bool BlockedUntilStateChange { get; set; }
        public string StateBoundBlockSignature { get; set; } = "none";
    }
}
#endif
