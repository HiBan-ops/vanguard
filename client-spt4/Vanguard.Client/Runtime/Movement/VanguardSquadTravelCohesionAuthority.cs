#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vanguard.Client.Diagnostics;

// Responsibility: Provides Squad Travel Cohesion Authority support for the movement/cohesion runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Movement;

/// <summary>
/// Vanguard lightweight memory used to prevent return -> release -> ORBIT/path residue -> return ping-pong.
/// It does not own movement by itself.  It only remembers that a hard return just succeeded and lets
/// TravelCohesion keep a short, bounded follow-through window if the Operator starts drifting again.
/// </summary>
internal static class VanguardSquadTravelCohesionAuthority
{
    public const string StatusTag = "VANGUARD_SQUAD_TRAVEL_COMBAT_AUTHORITY_STATUS";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, PostReturnHoldState> HoldByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(2.0d);

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            HoldByBotProfileId.Clear();
            LastLogByKey.Clear();
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_TRAVEL_AUTHORITY_RESET reason={Safe(reason)}; postReturnHolds=cleared; doctrine=post_hard_return_follow_through_without_slot_churn; tag={StatusTag}");
    }

    public static void RecordHardReturnCompleted(string botProfileId, string operatorId, float bubbleDistance, DateTimeOffset now, string reason)
    {
        RecordHold(botProfileId, operatorId, bubbleDistance, now, VanguardMovementAuthorityDoctrine.TravelCohesionPostReturnHoldSeconds, reason, "VANGUARD_POST_RETURN_HOLD_STARTED", "allow_travel_follow_through_if_external_residue_reacquires");
    }

    public static void RecordTravelAuthorityHold(string botProfileId, string operatorId, float bubbleDistance, DateTimeOffset now, string reason)
    {
        RecordHold(botProfileId, operatorId, bubbleDistance, now, VanguardMovementAuthorityDoctrine.OrbitQuiesceHoldSeconds, reason, "VANGUARD_ORBIT_AUTHORITY_HOLD_STARTED", "keep_orbit_quiesced_after_travel_until_settled_or_expired");
    }

    private static void RecordHold(string botProfileId, string operatorId, float bubbleDistance, DateTimeOffset now, float seconds, string reason, string logTag, string action)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return;
        }

        DateTimeOffset until = now + TimeSpan.FromSeconds(Math.Max(1.0f, seconds));
        lock (Sync)
        {
            HoldByBotProfileId[botProfileId] = new PostReturnHoldState(operatorId, botProfileId, bubbleDistance, until, reason);
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"{logTag} operator={Safe(operatorId)}; botProfile={Safe(botProfileId)}; bubble={bubbleDistance:0.00}; untilUtc={until:O}; seconds={Math.Max(1.0f, seconds):0.0}; reason={Safe(reason)}; action={Safe(action)}; tag={StatusTag}; orbitQuiesceTag={VanguardMovementAuthorityDoctrine.OrbitAuthorityQuiesceStatusTag}");
    }

    public static bool IsPostReturnHoldActive(string botProfileId, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return false;
        }

        lock (Sync)
        {
            if (!HoldByBotProfileId.TryGetValue(botProfileId, out var hold))
            {
                return false;
            }

            if (hold.UntilUtc <= now)
            {
                HoldByBotProfileId.Remove(botProfileId);
                reason = "expired";
                return false;
            }

            reason = "active_post_return_hold:initialBubble=" + hold.InitialBubbleDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + ";remaining=" + (hold.UntilUtc - now).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)
                + ";source=" + Safe(hold.Reason);
            return true;
        }
    }

    public static void ClearHold(string botProfileId, DateTimeOffset now, string reason)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return;
        }

        PostReturnHoldState hold;
        bool removed;
        lock (Sync)
        {
            removed = HoldByBotProfileId.TryGetValue(botProfileId, out hold);
            if (removed)
            {
                HoldByBotProfileId.Remove(botProfileId);
            }
        }

        if (removed)
        {
            LogThrottled("clear|" + botProfileId + "|" + reason, now,
                $"VANGUARD_POST_RETURN_HOLD_CLEARED operator={Safe(hold.OperatorId)}; botProfile={Safe(botProfileId)}; reason={Safe(reason)}; tag={StatusTag}");
        }
    }

    public static void Tick(DateTimeOffset now)
    {
        string[] expired;
        lock (Sync)
        {
            expired = HoldByBotProfileId.Where(pair => pair.Value.UntilUtc <= now).Select(pair => pair.Key).ToArray();
            foreach (string key in expired)
            {
                HoldByBotProfileId.Remove(key);
            }
        }

        if (expired.Length > 0)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_POST_RETURN_HOLD_EXPIRED count={expired.Length}; tag={StatusTag}");
        }
    }

    private static void LogThrottled(string key, DateTimeOffset now, string message)
    {
        lock (Sync)
        {
            if (LastLogByKey.TryGetValue(key, out var last) && now - last < LogInterval)
            {
                return;
            }

            LastLogByKey[key] = now;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag, message);
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }

    private readonly struct PostReturnHoldState
    {
        public PostReturnHoldState(string operatorId, string botProfileId, float initialBubbleDistance, DateTimeOffset untilUtc, string reason)
        {
            OperatorId = operatorId;
            BotProfileId = botProfileId;
            InitialBubbleDistance = initialBubbleDistance;
            UntilUtc = untilUtc;
            Reason = reason;
        }

        public string OperatorId { get; }
        public string BotProfileId { get; }
        public float InitialBubbleDistance { get; }
        public DateTimeOffset UntilUtc { get; }
        public string Reason { get; }
    }
}
#endif
