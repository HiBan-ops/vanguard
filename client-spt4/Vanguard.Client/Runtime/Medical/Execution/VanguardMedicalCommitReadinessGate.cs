#if SPT_CLIENT
using System;
using System.Collections.Generic;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;
using EFT;
using Vanguard.Client.Runtime.Medical;
using Vanguard.Client.Runtime.PostLoot;

// Responsibility: Provides Medical Commit Readiness Gate support for the medical runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Medical.Execution;

/// <summary>
/// Read-only pre-commit gate for non-urgent medical controller starts. It requires two
/// distinct, coherent snapshots before the first native apply call. It never opens a lease,
/// mutates hands, moves the Operator or writes a failed medical outcome.
/// </summary>
internal static class VanguardMedicalCommitReadinessGate
{
    public const string StatusTag = "VANGUARD_MEDICAL_COMMIT_READINESS_STATUS";

    private static readonly TimeSpan StateExpiry = TimeSpan.FromSeconds(3.0d);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(4.0d);
    private static readonly Dictionary<string, ReadyState> StateByBotProfile = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogAtByKey = new(StringComparer.OrdinalIgnoreCase);

    public static void Reset(string reason)
    {
        StateByBotProfile.Clear();
        LastLogAtByKey.Clear();
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_MEDICAL_COMMIT_READINESS_RESET reason={Safe(reason)}; twoDistinctSnapshots=true; urgentBleedUnchanged=true; surgeryCommitIncluded=true; hpFractureCommitIncluded=true; movementMutation=false; handsMutation=false; outcomeMutation=false; tag={StatusTag}");
    }

    public static bool CanCommit(
        BotOwner? botOwner,
        OperatorDecisionSnapshot snapshot,
        VanguardMobileMedicalActionSelection selection,
        DateTimeOffset now,
        out string reason)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        string bot = Safe(snapshot.BotProfileId);
        string signature = BuildSignature(selection);

        if (!RequiresStableCommit(selection))
        {
            StateByBotProfile.Remove(bot);
            reason = "urgent_lane_no_extra_settle";
            return true;
        }

        if (!IsReady(botOwner, snapshot, selection, out string readinessReason, out string handsSignature))
        {
            StateByBotProfile.Remove(bot);
            reason = "not_ready:" + readinessReason;
            LogThrottled(bot + "|not_ready|" + readinessReason, now,
                $"VANGUARD_MEDICAL_COMMIT_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={bot}; signature={Safe(signature)}; reason={Safe(readinessReason)}; leaseStarted=false; nativeApply=false; failedOutcome=false; tag={StatusTag}");
            return false;
        }

        DateTimeOffset observationUtc = snapshot.CapturedAtUtc == DateTimeOffset.MinValue ? now : snapshot.CapturedAtUtc;
        if (!StateByBotProfile.TryGetValue(bot, out ReadyState state)
            || !string.Equals(state.Signature, signature, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(state.HandsSignature, handsSignature, StringComparison.OrdinalIgnoreCase)
            || now - state.FirstObservedUtc > StateExpiry)
        {
            state = new ReadyState
            {
                Signature = signature,
                HandsSignature = handsSignature,
                FirstObservedUtc = now,
                LastObservationUtc = observationUtc,
                ConsecutiveReady = 1
            };
            StateByBotProfile[bot] = state;
            reason = "ready_confirmation_pending:1";
            return false;
        }

        if (observationUtc <= state.LastObservationUtc)
        {
            reason = "ready_confirmation_non_new_snapshot_pending:" + state.ConsecutiveReady;
            return false;
        }

        state.LastObservationUtc = observationUtc;
        state.ConsecutiveReady++;
        if (state.ConsecutiveReady < 2)
        {
            reason = "ready_confirmation_pending:" + state.ConsecutiveReady;
            return false;
        }

        StateByBotProfile.Remove(bot);
        LogThrottled(bot + "|ready|" + signature, now,
            $"VANGUARD_MEDICAL_COMMIT_READY operator={Safe(snapshot.OperatorId)}; botProfile={bot}; signature={Safe(signature)}; hands={Safe(handsSignature)}; consecutiveReady={state.ConsecutiveReady}; leaseMayStart=true; nativeApplyMayStart=true; tag={StatusTag}");
        reason = "stable_commit_ready";
        return true;
    }

    private static bool RequiresStableCommit(VanguardMobileMedicalActionSelection selection)
    {
        return selection.Need == VanguardMedicalNeed.HpHeal
            || selection.Need == VanguardMedicalNeed.Fracture
            || VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(selection.Need);
    }

    private static bool IsReady(
        BotOwner? botOwner,
        OperatorDecisionSnapshot snapshot,
        VanguardMobileMedicalActionSelection selection,
        out string reason,
        out string handsSignature)
    {
        handsSignature = "none";
        var actionability = snapshot.Medical.Actionability;
        if (!actionability.HandsReadyForMedicalAction
            || actionability.Reloading
            || actionability.GrenadeThrowing
            || actionability.AnyMedicineUsing
            || actionability.FirstAidUsing
            || actionability.SurgicalKitUsing
            || actionability.StimulatorUsing)
        {
            reason = "hands_or_controller_not_ready";
            return false;
        }

        if (actionability.CanApplyItem != true)
        {
            reason = "can_apply_not_true";
            return false;
        }

        if (botOwner == null)
        {
            reason = "bot_owner_missing";
            return false;
        }

        try
        {
            VanguardPostLootWeaponReadinessSnapshot hands = VanguardPostLootWeaponReadinessReader.Capture(botOwner);
            handsSignature = hands.Signature;
            if (!hands.WeaponReady || hands.FirstAidUsing)
            {
                reason = "native_hands_not_weapon_ready:" + Safe(hands.Summary);
                return false;
            }
        }
        catch (Exception exception)
        {
            reason = "native_hands_read_exception:" + Safe(exception.GetType().Name);
            return false;
        }

        if (selection.RequiresStationary)
        {
            float speed = Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed);
            bool activeMovementCommand = snapshot.Movement.HasPath == true
                && speed > 0.15f
                && snapshot.Movement.TargetSpeed.GetValueOrDefault() > 0.10f;
            if (speed > 0.35f || activeMovementCommand)
            {
                reason = activeMovementCommand
                    ? "stationary_active_movement_command"
                    : "stationary_posture_not_stable";
                return false;
            }
        }

        reason = "ready";
        return true;
    }

    private static string BuildSignature(VanguardMobileMedicalActionSelection selection)
    {
        return selection.Need + "|" + Safe(selection.TargetPartName) + "|" + Safe(selection.ItemInstanceId) + "|" + Safe(selection.ItemTemplateId);
    }

    private static void LogThrottled(string key, DateTimeOffset now, string message)
    {
        if (LastLogAtByKey.TryGetValue(key, out DateTimeOffset last) && now - last < LogInterval)
        {
            return;
        }

        LastLogAtByKey[key] = now;
        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () => message);
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private sealed class ReadyState
    {
        public string Signature = "none";
        public string HandsSignature = "none";
        public DateTimeOffset FirstObservedUtc;
        public DateTimeOffset LastObservationUtc;
        public int ConsecutiveReady;
    }
}
#endif
