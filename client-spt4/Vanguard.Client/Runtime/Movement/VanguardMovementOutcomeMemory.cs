#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Vanguard.Client.Diagnostics;

// Responsibility: Provides Movement Outcome Memory support for the movement/cohesion runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Movement;

/// <summary>
/// Short-lived memory for HardReturn anchor outcomes.  It prevents the same bad action-rally
/// anchor from being selected again immediately after a no-progress timeout or forced reanchor.
/// </summary>
internal static class VanguardMovementOutcomeMemory
{
    public const string StatusTag = "VANGUARD_ANCHOR_SCORE_OK";
    private static readonly object Sync = new();
    private static readonly Dictionary<string, MovementOutcomeRecord> RecordsByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan FailureMemoryWindow = TimeSpan.FromSeconds(55d);

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            RecordsByKey.Clear();
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_OUTCOME_MEMORY_RESET reason={Safe(reason)}; scope=movement_hard_return_anchor; memoryWindow={FailureMemoryWindow.TotalSeconds:0}; tag={StatusTag}");
    }

    public static string RecordLeaseOutcome(string botProfileId, Vector3 anchor, string outcome, string reason, float pathDistanceMeters, DateTimeOffset now)
    {
        string key = BuildKey(botProfileId, anchor);
        bool failure = IsFailure(outcome, reason);
        MovementOutcomeRecord record;
        lock (Sync)
        {
            RecordsByKey.TryGetValue(key, out record);
            record.BotProfileId = botProfileId ?? string.Empty;
            record.AnchorKey = key;
            record.LastOutcome = outcome ?? "none";
            record.LastReason = reason ?? "none";
            record.LastPathDistanceMeters = pathDistanceMeters;
            record.LastUpdatedAtUtc = now;
            if (failure)
            {
                record.LastFailureAtUtc = now;
                record.RepeatedFailureCount = Math.Max(1, record.RepeatedFailureCount + 1);
            }
            else if (string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                record.LastSuccessAtUtc = now;
                record.RepeatedFailureCount = 0;
            }

            RecordsByKey[key] = record;
        }

        string summary = "key=" + key
            + ";outcome=" + Safe(outcome)
            + ";reason=" + Safe(reason)
            + ";failures=" + record.RepeatedFailureCount.ToString(CultureInfo.InvariantCulture)
            + ";pathDist=" + pathDistanceMeters.ToString("0.00", CultureInfo.InvariantCulture);
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_OUTCOME_MEMORY_UPDATED {summary}; tag={StatusTag}");
        return summary;
    }

    public static bool ShouldPenalizeAnchor(string botProfileId, Vector3 anchor, DateTimeOffset now, out string reason)
    {
        string key = BuildKey(botProfileId, anchor);
        lock (Sync)
        {
            if (RecordsByKey.TryGetValue(key, out var record)
                && record.RepeatedFailureCount > 0
                && record.LastFailureAtUtc != DateTimeOffset.MinValue
                && now - record.LastFailureAtUtc <= FailureMemoryWindow)
            {
                reason = "recent_failure:" + Safe(record.LastReason)
                    + ":count=" + record.RepeatedFailureCount.ToString(CultureInfo.InvariantCulture)
                    + ":age=" + (now - record.LastFailureAtUtc).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);
                return true;
            }
        }

        reason = "none";
        return false;
    }

    private static bool IsFailure(string outcome, string reason)
    {
        if (string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string text = (outcome + "|" + reason).ToLowerInvariant();
        return text.Contains("timeout")
            || text.Contains("no_progress")
            || text.Contains("noprogress")
            || text.Contains("anchor_reached_but_bubble_far")
            || text.Contains("anchor_path_failed")
            || text.Contains("reanchor")
            || text.Contains("failed");
    }

    private static string BuildKey(string botProfileId, Vector3 anchor)
    {
        float x = (float)(Math.Round(anchor.x / 5f) * 5f);
        float z = (float)(Math.Round(anchor.z / 5f) * 5f);
        return Safe(botProfileId) + "@" + x.ToString("0", CultureInfo.InvariantCulture) + ":" + z.ToString("0", CultureInfo.InvariantCulture);
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }

    private struct MovementOutcomeRecord
    {
        public string BotProfileId;
        public string AnchorKey;
        public string LastOutcome;
        public string LastReason;
        public float LastPathDistanceMeters;
        public DateTimeOffset LastUpdatedAtUtc;
        public DateTimeOffset LastFailureAtUtc;
        public DateTimeOffset LastSuccessAtUtc;
        public int RepeatedFailureCount;
    }
}
#endif
