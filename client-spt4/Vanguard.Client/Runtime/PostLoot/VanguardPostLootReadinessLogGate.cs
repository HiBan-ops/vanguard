#if SPT_CLIENT
using System;

// Responsibility: Throttle repeated post-loot readiness and suspect-state diagnostic messages.
// Flow: Log changed or forced snapshot signatures immediately and defer repeated signatures or suspect keys by cooldown.
// Authority boundary: This gate updates diagnostic timestamps only; it does not control Operator readiness or recovery decisions.
// Invariant: Identical snapshot or suspect diagnostics respect their respective cooldowns unless explicitly forced.
namespace Vanguard.Client.Runtime.PostLoot;

internal static class VanguardPostLootReadinessLogGate
{
    private static readonly TimeSpan SnapshotTransitionCooldown = TimeSpan.FromSeconds(8.0d);
    private static readonly TimeSpan SuspectCooldown = TimeSpan.FromSeconds(12.0d);

    public static bool ShouldLogSnapshot(VanguardPostLootEpisodeState state, DateTimeOffset now, string signature, bool forced)
    {
        if (forced || !string.Equals(state.LastSnapshotKey, signature, StringComparison.Ordinal))
        {
            state.LastSnapshotKey = signature;
            state.LastSnapshotLogAtUtc = now;
            return true;
        }

        if (now - state.LastSnapshotLogAtUtc < SnapshotTransitionCooldown)
        {
            return false;
        }

        state.LastSnapshotLogAtUtc = now;
        return true;
    }

    public static bool ShouldLogSuspect(VanguardPostLootEpisodeState state, DateTimeOffset now, string key)
    {
        if (state.LastSuspectLogAtByKey.TryGetValue(key, out var last) && now - last < SuspectCooldown)
        {
            return false;
        }

        state.LastSuspectLogAtByKey[key] = now;
        return true;
    }
}
#endif
