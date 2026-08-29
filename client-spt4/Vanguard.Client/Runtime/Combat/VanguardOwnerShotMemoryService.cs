#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;

// Responsibility: Coordinates Owner Shot Memory Service for the combat safety runtime, delegating specialized work to its collaborators.
// Flow: Current raid/runtime evidence is normalized, applicable guards and ownership rules are evaluated, then the service updates only its bounded runtime/UI responsibility.
// Authority boundary: Service coordinates its domain but does not fabricate server persistence truth or bypass higher-priority runtime authorities.
// Invariant: State is lifecycle-scoped, stale work is releasable, and failures degrade without leaving hidden long-lived ownership.
namespace Vanguard.Client.Runtime.Combat;

internal readonly struct VanguardOwnerShotSnapshot
{
    public VanguardOwnerShotSnapshot(
        string ownerProfileId,
        Vector3 origin,
        Vector3 direction,
        DateTimeOffset observedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        OwnerProfileId = ownerProfileId;
        Origin = origin;
        Direction = direction;
        ObservedAtUtc = observedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public string OwnerProfileId { get; }
    public Vector3 Origin { get; }
    public Vector3 Direction { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
}

/// <summary>
/// Short-lived memory of an actual player-owner projectile. It is evidence only: the Awareness
/// coordinator decides whether a hostile lies in the fired sector and SAIN remains the combat executor.
/// </summary>
internal static class VanguardOwnerShotMemoryService
{
    public const string StatusTag = "VANGUARD_OWNER_SHOT_MEMORY_STATUS";
    public const string OwnerShotIntentStatusTag = "VANGUARD_BASELINE_CONSOLIDATION_AND_OWNER_SHOT_INTENT_STATUS";

    private static readonly TimeSpan EvidenceWindow = TimeSpan.FromSeconds(5.75d);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, VanguardOwnerShotSnapshot> ShotByOwnerProfileId = new(StringComparer.OrdinalIgnoreCase);

    public static void ObserveShot(Player? shooter, Vector3 origin, Vector3 direction)
    {
        if (!VanguardFikaCompat.IsRaidAuthority || shooter == null || direction.sqrMagnitude <= 0.001f)
        {
            return;
        }

        string ownerProfileId = Normalize(shooter.ProfileId);
        if (string.Equals(ownerProfileId, "none", StringComparison.OrdinalIgnoreCase)
            || !VanguardRaidOperatorRuntimeRegistry.IsKnownOwnerProfileId(ownerProfileId))
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var snapshot = new VanguardOwnerShotSnapshot(
            ownerProfileId,
            origin,
            direction.normalized,
            now,
            now + EvidenceWindow);
        lock (Sync)
        {
            ShotByOwnerProfileId[ownerProfileId] = snapshot;
        }
    }

    public static bool TryGetRecentShot(string? ownerProfileId, DateTimeOffset now, out VanguardOwnerShotSnapshot snapshot)
    {
        string owner = Normalize(ownerProfileId);
        lock (Sync)
        {
            if (ShotByOwnerProfileId.TryGetValue(owner, out snapshot))
            {
                if (snapshot.ExpiresAtUtc > now)
                {
                    return true;
                }

                ShotByOwnerProfileId.Remove(owner);
            }
        }

        snapshot = default;
        return false;
    }

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            ShotByOwnerProfileId.Clear();
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_OWNER_SHOT_MEMORY_RESET reason={Safe(reason)}; evidenceSeconds={EvidenceWindow.TotalSeconds:0.00}; mutation=none; consumer=unified_awareness_assignment; sainRemainsCombatOwner=true; EvidencePolicy=owner_visual_or_corroborated_can_commit_geometric_only_is_suspicion; ownerShotIntentTag={OwnerShotIntentStatusTag}; tag={StatusTag}");
        VanguardClientDiagnosticsLog.Info(OwnerShotIntentStatusTag,
            $"VANGUARD_OWNER_SHOT_INTENT_BOOT enabled=true; strong=shot_sector_plus_fresh_owner_los; corroborated=shot_sector_plus_existing_direct_or_shared_hostile_truth; suspicion=shot_sector_only; suspicionMutation=squad_contact_pool_only; suspicionCanCommitToSain=false; suspicionCanFeedLegacyCombat=false; grenadeAndSearchRequireActionableTarget=true; sainRemainsCombatOwner=true; tag={OwnerShotIntentStatusTag}");
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Safe(string? value) => Normalize(value).Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
