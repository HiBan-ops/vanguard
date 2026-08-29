#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Runtime.Audit;

// Responsibility: Provides Owner Anchor Resolver support for the movement/cohesion runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Movement;

internal readonly struct VanguardOwnerAnchor
{
    public VanguardOwnerAnchor(bool known, bool reliableForActiveMovement, string source, Vector3 position, Vector3 forward, float ageSeconds, string reason)
    {
        Known = known;
        ReliableForActiveMovement = reliableForActiveMovement;
        Source = source;
        Position = position;
        Forward = forward;
        AgeSeconds = ageSeconds;
        Reason = reason;
    }

    public bool Known { get; }
    public bool ReliableForActiveMovement { get; }
    public string Source { get; }
    public Vector3 Position { get; }
    public Vector3 Forward { get; }
    public float AgeSeconds { get; }
    public string Reason { get; }

    public static VanguardOwnerAnchor Unknown(string reason) => new(false, false, "unknown", Vector3.zero, Vector3.forward, 0f, reason);
}

internal static class VanguardOwnerAnchorResolver
{
    private sealed class CachedAnchor
    {
        public Vector3 Position;
        public Vector3 Forward;
        public DateTimeOffset CapturedAtUtc;
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, CachedAnchor> CacheByOwnerProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ReadOnlyCacheTtl = TimeSpan.FromSeconds(20.0d);

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            CacheByOwnerProfileId.Clear();
        }
    }

    public static VanguardOwnerAnchor Resolve(string ownerProfileId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(ownerProfileId))
        {
            return VanguardOwnerAnchor.Unknown("owner_profile_id_missing");
        }

        Player? owner = VanguardFikaCompat.FindRaidPlayerByProfileId(ownerProfileId);
        if (owner is not null)
        {
            Vector3 position = ResolvePlayerPosition(owner);
            Vector3 forward = ResolvePlayerForward(owner);
            lock (Sync)
            {
                CacheByOwnerProfileId[ownerProfileId] = new CachedAnchor
                {
                    Position = position,
                    Forward = forward,
                    CapturedAtUtc = now
                };
            }

            return new VanguardOwnerAnchor(true, true, "direct_owner", position, forward, 0f, "owner_direct_found");
        }

        lock (Sync)
        {
            if (CacheByOwnerProfileId.TryGetValue(ownerProfileId, out var cached))
            {
                float age = (float)Math.Max(0.0d, (now - cached.CapturedAtUtc).TotalSeconds);
                if (age <= ReadOnlyCacheTtl.TotalSeconds)
                {
                    return new VanguardOwnerAnchor(true, false, "cached_owner_readonly", cached.Position, cached.Forward, age, "owner_direct_missing_cache_readonly");
                }

                return new VanguardOwnerAnchor(false, false, "stale_cache", cached.Position, cached.Forward, age, "owner_direct_missing_cache_stale");
            }
        }

        return VanguardOwnerAnchor.Unknown("owner_player_not_found");
    }

    private static Vector3 ResolvePlayerPosition(Player player)
    {
        try
        {
            return player.Transform.position;
        }
        catch
        {
            // Read-only resolver only.
        }

        object? position = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player, "Position");
        return position is Vector3 vector ? vector : Vector3.zero;
    }

    private static Vector3 ResolvePlayerForward(Player player)
    {
        try
        {
            Vector3 forward = player.Transform.forward;
            if (forward.sqrMagnitude > 0.001f)
            {
                return forward.normalized;
            }
        }
        catch
        {
            // Read-only resolver only.
        }

        object? rotation = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player, "Rotation");
        if (rotation is Vector3 vector && vector.sqrMagnitude > 0.001f)
        {
            return vector.normalized;
        }

        return Vector3.forward;
    }
}
#endif
