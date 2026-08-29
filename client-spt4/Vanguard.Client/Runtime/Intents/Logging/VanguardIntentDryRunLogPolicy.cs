#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Encodes the deterministic rules for Intent Dry Run Log Policy within the intent production pipeline.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Runtime.Intents;

internal static partial class VanguardOperatorIntentDryRunService
{
private static bool ShouldLogSelected(VanguardIntentDryRunBoard board, DateTimeOffset now)
    {
        var state = GetOrCreateState(board.Snapshot.BotProfileId);
        if (string.Equals(state.Signature, board.Signature, StringComparison.Ordinal))
        {
            return false;
        }

        if (!VanguardOperatorRuntimeAuditOptions.GetVerboseTransitionLogEnabled() && state.LastTransitionAtUtc != DateTimeOffset.MinValue)
        {
            var minInterval = TimeSpan.FromSeconds(VanguardOperatorRuntimeAuditOptions.GetTransitionLogMinIntervalSeconds());
            if (now - state.LastTransitionAtUtc < minInterval)
            {
                state.Signature = board.Signature;
                return false;
            }
        }

        state.Signature = board.Signature;
        state.LastTransitionAtUtc = now;
        return true;
    }

private static bool ShouldLogSummary(VanguardIntentDryRunBoard board, DateTimeOffset now)
    {
        var state = GetOrCreateState(board.Snapshot.BotProfileId);
        if (now - state.LastSummaryAtUtc < SummaryIntervalFor(board))
        {
            return false;
        }

        state.LastSummaryAtUtc = now;
        return true;
    }

private static TimeSpan SummaryIntervalFor(VanguardIntentDryRunBoard board)
    {
        double seconds = VanguardOperatorRuntimeAuditOptions.GetSummaryIntervalSeconds();
        if (!board.Snapshot.Alive)
        {
            seconds = Math.Max(60d, seconds * 3d);
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
#endif
