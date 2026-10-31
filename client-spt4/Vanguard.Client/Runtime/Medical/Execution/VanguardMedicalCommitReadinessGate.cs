#if SPT_CLIENT
using System;
using System.Collections.Generic;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;
using EFT;
using Vanguard.Client.Runtime.Medical;
using Vanguard.Client.Runtime.PostLoot;

// Responsibility: Check medical-controller readiness immediately before a native treatment commit.
// Flow: Compare fresh coherent decision snapshots and hands/item safety before allowing the applicable commit path.
// Authority boundary: The gate only evaluates readiness; it does not acquire a lease, manipulate hands or record treatment failure.
// Invariant: Ordinary commits require stable evidence; prepared direct surgery preserves its separate live-item safety check.
namespace Vanguard.Client.Runtime.Medical.Execution;

/// <summary>
/// Read-only pre-commit gate for medical controller starts. Standard HP, fracture and
/// unprepared surgery commits require two distinct coherent snapshots. A prepared direct
/// surgery can reuse its live EFT item check while retaining hands and stationary safety.
/// This gate never opens a lease, mutates hands, moves or records a failed outcome.
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
            $"VANGUARD_MEDICAL_COMMIT_READINESS_RESET reason={Safe(reason)}; twoDistinctSnapshotsForStandardCommit=true; directPreparedSurgeryUsesLiveCheck=true; urgentBleedUnchanged=true; surgeryCommitIncluded=true; hpFractureCommitIncluded=true; movementMutation=false; handsMutation=false; outcomeMutation=false; tag={StatusTag}");
    }

    public static int ForgetRuntimePresence(string? botProfileId, string reason)
    {
        string key = Safe(botProfileId);
        if (string.Equals(key, "none", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        int removed = StateByBotProfile.Remove(key) ? 1 : 0;
        foreach (string logKey in new List<string>(LastLogAtByKey.Keys))
        {
            if (logKey.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                LastLogAtByKey.Remove(logKey);
                removed++;
            }
        }

        return removed;
    }

    public static bool CanCommit(
        BotOwner? botOwner,
        OperatorDecisionSnapshot snapshot,
        VanguardMobileMedicalActionSelection selection,
        DateTimeOffset now,
        out string reason,
        bool preparedSurgeryLiveSelection = false)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        string bot = Safe(snapshot.BotProfileId);
        string signature = BuildSignature(selection);
        // Only the already-prepared direct surgery path supplies this signal. Its exact
        // kit/target was just verified by EFT's live CanApplyItem, and this call is
        // immediately followed by two native surgical-commit validations.
        // No second read-only snapshot confirmation is useful for this same start.
        bool livePreparedSurgery = preparedSurgeryLiveSelection
            && VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(selection.Need)
            && string.Equals(selection.ExecutionLane, "stationary_surgery_direct_chain", StringComparison.OrdinalIgnoreCase);

        if (!RequiresStableCommit(selection))
        {
            StateByBotProfile.Remove(bot);
            reason = "urgent_lane_no_extra_settle";
            return true;
        }

        if (!IsReady(botOwner, snapshot, selection, requireSnapshotCanApply: !livePreparedSurgery, out string readinessReason, out string handsSignature))
        {
            StateByBotProfile.Remove(bot);
            reason = "not_ready:" + readinessReason;
            LogThrottled(bot + "|not_ready|" + readinessReason, now,
                $"VANGUARD_MEDICAL_COMMIT_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={bot}; signature={Safe(signature)}; reason={Safe(readinessReason)}; leaseStarted=false; nativeApply=false; failedOutcome=false; tag={StatusTag}");
            return false;
        }

        if (livePreparedSurgery)
        {
            // The medical hands, threat, and stationary checks above still apply.
            // Avoid carrying an old two-snapshot confirmation into the next action.
            StateByBotProfile.Remove(bot);
            reason = "prepared_surgery_live_commit_ready";
            return true;
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

        bool retainForFinalFractureSettle = selection.Need == VanguardMedicalNeed.Fracture;
        if (retainForFinalFractureSettle)
        {
            // The final stationary settle happens after this two-snapshot gate. Keep the exact
            // ready proof alive while every live readiness/identity check above remains true,
            // then consume it only when the native fracture lease actually starts.
            state.FirstObservedUtc = now;
            StateByBotProfile[bot] = state;
        }
        else
        {
            StateByBotProfile.Remove(bot);
        }

        LogThrottled(bot + "|ready|" + signature, now,
            $"VANGUARD_MEDICAL_COMMIT_READY operator={Safe(snapshot.OperatorId)}; botProfile={bot}; signature={Safe(signature)}; hands={Safe(handsSignature)}; consecutiveReady={state.ConsecutiveReady}; retainedForFinalFractureSettle={retainForFinalFractureSettle.ToString().ToLowerInvariant()}; leaseMayStart=true; nativeApplyMayStart=true; tag={StatusTag}");
        reason = retainForFinalFractureSettle ? "stable_commit_ready_retained_for_final_fracture_settle" : "stable_commit_ready";
        return true;
    }

    public static void ConsumeStableCommit(string? botProfileId, VanguardMobileMedicalActionSelection selection, string reason)
    {
        if (selection.Need != VanguardMedicalNeed.Fracture)
        {
            return;
        }

        string bot = Safe(botProfileId);
        if (string.Equals(bot, "none", StringComparison.OrdinalIgnoreCase)
            || !StateByBotProfile.TryGetValue(bot, out ReadyState state)
            || !string.Equals(state.Signature, BuildSignature(selection), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        StateByBotProfile.Remove(bot);
        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
            $"VANGUARD_MEDICAL_COMMIT_READY_CONSUMED botProfile={bot}; signature={Safe(state.Signature)}; reason={Safe(reason)}; fracture=true; nativeLeaseStarted=true; tag={StatusTag}");
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
        bool requireSnapshotCanApply,
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

        if (requireSnapshotCanApply && actionability.CanApplyItem != true)
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
