#if SPT_CLIENT
using System;

// Responsibility: Provides Post Loot Readiness Log Gate support for the post-loot recovery runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
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
