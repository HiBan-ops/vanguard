#if SPT_CLIENT
using System;

// Responsibility: Defines data/state contracts used by the combat-awareness runtime, centered on Combat Awareness Bridge.Corpse Loot Read Model.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime.Awareness;

internal static partial class VanguardCombatAwarenessBridge
{
    /// <summary>
    /// Read-only runtime boundary used by corpse qualification. It exposes only fresh, already-qualified
    /// squad contacts and never scans the world, mutates SAIN or extends contact lifetime.
    /// Suspicion-only contacts are intentionally rejected as insufficient proof for corpse ownership.
    /// </summary>
    public static bool TryGetFreshQualifiedCorpseContact(
        string? ownerProfileId,
        string? targetProfileId,
        DateTimeOffset now,
        out string source,
        out float ageSeconds)
    {
        source = "none";
        ageSeconds = -1f;
        string owner = Normalize(ownerProfileId);
        string target = Normalize(targetProfileId);
        if (string.Equals(owner, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(target, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (Sync)
        {
            if (!SquadContactsByOwnerProfileId.TryGetValue(owner, out var byTarget)
                || !byTarget.TryGetValue(target, out SquadCombatContactState contact)
                || contact.ExpiresAtUtc <= now
                || IsSquadSuspicionKind(contact.Kind))
            {
                return false;
            }

            source = "vanguard_contact:" + Safe(contact.Kind);
            ageSeconds = (float)Math.Max(0d, (now - contact.ObservedAtUtc).TotalSeconds);
            return true;
        }
    }
}
#endif
