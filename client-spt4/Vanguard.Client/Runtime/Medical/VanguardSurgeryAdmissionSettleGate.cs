#if SPT_CLIENT
using System;
using System.Collections.Generic;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Provides Surgery Admission Settle Gate support for the medical runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Medical;

/// <summary>
/// Pre-lease settle gate. Transient hands states (reload, grenade or another medical action)
/// are observed without opening movement/medical authority, writing a failed outcome or starting
/// cover search. Permanent target/item invalidity still rejects immediately. This gate protects
/// preparation admission; the separate medical commit-readiness gate requires two distinct
/// coherent snapshots at the actual native ApplyToCurrentPart boundary.
/// </summary>
internal static class VanguardSurgeryAdmissionSettleGate
{
    public const string StatusTag = "VANGUARD_SURGERY_ADMISSION_SETTLE_STATUS";

    private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(2.0d);
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(0.75d);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(4.0d);
    private static readonly Dictionary<string, SettleState> StateByBotProfile = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogAtByKey = new(StringComparer.OrdinalIgnoreCase);

    public static void Reset(string reason)
    {
        StateByBotProfile.Clear();
        LastLogAtByKey.Clear();
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_SURGERY_ADMISSION_SETTLE_RESET reason={Safe(reason)}; preLease=true; movementMutation=false; medicalAuthorityMutation=false; failureOutcome=false; settleSeconds={SettleWindow.TotalSeconds:0.00}; tag={StatusTag}");
    }

    public static bool CanAdmit(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        string bot = Safe(snapshot.BotProfileId);
        var stateKind = VanguardMedicalSurgeryTargetPolicy.EvaluateSurgeryPreparationCandidate(snapshot, out var candidateReason);
        string signature = Signature(snapshot);

        if (stateKind == VanguardSurgeryCandidateState.Invalid)
        {
            StateByBotProfile.Remove(bot);
            reason = "hard_invalid:" + candidateReason;
            return false;
        }

        if (stateKind == VanguardSurgeryCandidateState.Transient)
        {
            if (!StateByBotProfile.TryGetValue(bot, out var state) || !string.Equals(state.Signature, signature, StringComparison.OrdinalIgnoreCase))
            {
                state = new SettleState
                {
                    Signature = signature,
                    DeadlineUtc = now + SettleWindow,
                    RetryAfterUtc = DateTimeOffset.MinValue,
                    ConsecutiveReady = 0
                };
                StateByBotProfile[bot] = state;
            }
            else
            {
                state.ConsecutiveReady = 0;
            }

            if (state.RetryAfterUtc > now)
            {
                reason = "transient_retry_pause:" + candidateReason;
                return false;
            }

            if (now >= state.DeadlineUtc)
            {
                state.DeadlineUtc = now + SettleWindow;
                state.RetryAfterUtc = now + RetryPause;
                LogThrottled(bot + "|timeout|" + candidateReason, now,
                    $"VANGUARD_SURGERY_ADMISSION_SETTLE_TIMEOUT operator={Safe(snapshot.OperatorId)}; botProfile={bot}; signature={Safe(signature)}; reason={Safe(candidateReason)}; action=short_non_mutating_retry; failedOutcome=false; cooldownSeconds={RetryPause.TotalSeconds:0.00}; tag={StatusTag}");
                reason = "transient_timeout_retry:" + candidateReason;
                return false;
            }

            LogThrottled(bot + "|waiting|" + candidateReason, now,
                $"VANGUARD_SURGERY_ADMISSION_SETTLING operator={Safe(snapshot.OperatorId)}; botProfile={bot}; signature={Safe(signature)}; reason={Safe(candidateReason)}; remaining={(state.DeadlineUtc - now).TotalSeconds:0.00}; leaseStarted=false; movementMutation=false; authorityMutation=false; failedOutcome=false; tag={StatusTag}");
            reason = "transient_settling:" + candidateReason;
            return false;
        }

        if (!StateByBotProfile.TryGetValue(bot, out var readyState)
            || !string.Equals(readyState.Signature, signature, StringComparison.OrdinalIgnoreCase))
        {
            reason = "ready_without_prior_transient:" + candidateReason;
            return true;
        }

        DateTimeOffset observationUtc = snapshot.CapturedAtUtc == DateTimeOffset.MinValue ? now : snapshot.CapturedAtUtc;
        if (readyState.LastReadyObservationUtc == observationUtc)
        {
            reason = "ready_confirmation_same_snapshot_pending:" + readyState.ConsecutiveReady;
            return false;
        }

        readyState.LastReadyObservationUtc = observationUtc;
        readyState.ConsecutiveReady++;
        if (readyState.ConsecutiveReady < 2)
        {
            reason = "ready_confirmation_pending:" + readyState.ConsecutiveReady;
            return false;
        }

        StateByBotProfile.Remove(bot);
        LogThrottled(bot + "|ready|" + signature, now,
            $"VANGUARD_SURGERY_ADMISSION_SETTLED operator={Safe(snapshot.OperatorId)}; botProfile={bot}; signature={Safe(signature)}; consecutiveReady={readyState.ConsecutiveReady}; leaseMayStart=true; tag={StatusTag}");
        reason = "settled_ready:" + candidateReason;
        return true;
    }

    private static string Signature(OperatorDecisionSnapshot snapshot)
    {
        VanguardMedicalSurgeryTargetPolicy.TryResolveTarget(snapshot, out var target);
        return Safe(target) + "|" + Safe(snapshot.Medical.Actionability.SelectedItemTemplateId);
    }

    private static void LogThrottled(string key, DateTimeOffset now, string message)
    {
        if (LastLogAtByKey.TryGetValue(key, out var last) && now - last < LogInterval)
        {
            return;
        }

        LastLogAtByKey[key] = now;
        VanguardClientDiagnosticsLog.Info(StatusTag, message);
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private sealed class SettleState
    {
        public string Signature = "none";
        public DateTimeOffset DeadlineUtc;
        public DateTimeOffset RetryAfterUtc;
        public DateTimeOffset LastReadyObservationUtc;
        public int ConsecutiveReady;
    }
}
#endif
