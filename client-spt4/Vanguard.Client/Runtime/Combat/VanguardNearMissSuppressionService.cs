#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Alliance;

// Responsibility: Tracks bullets that pass dangerously close to an Operator so short-lived suppression can influence safety decisions without inventing a confirmed attacker hit.
// Flow: Shot geometry is sampled, qualifying near misses are accumulated into a short expiry window, and readers receive one compact snapshot until that evidence becomes stale.
// Authority boundary: This service records threat evidence only; it does not pick targets, move the Operator, fire weapons, or write persistent state.
// Invariant: Near-miss evidence expires quickly and can raise caution, but it can never become permanent combat ownership by itself.
namespace Vanguard.Client.Runtime.Combat;

internal readonly struct VanguardNearMissSuppressionSnapshot
{
    public VanguardNearMissSuppressionSnapshot(
        bool active,
        DateTimeOffset observedAtUtc,
        DateTimeOffset untilUtc,
        Vector3 direction,
        float closestMeters,
        int burstShots,
        string shooterProfileId,
        Vector3 shooterPosition,
        float shooterDistanceMeters,
        bool threatenedOperator,
        bool threatenedOwner)
    {
        Active = active;
        ObservedAtUtc = observedAtUtc;
        UntilUtc = untilUtc;
        Direction = direction;
        ClosestMeters = closestMeters;
        BurstShots = burstShots;
        ShooterProfileId = shooterProfileId;
        ShooterPosition = shooterPosition;
        ShooterDistanceMeters = shooterDistanceMeters;
        ThreatenedOperator = threatenedOperator;
        ThreatenedOwner = threatenedOwner;
    }

    public bool Active { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public DateTimeOffset UntilUtc { get; }
    public Vector3 Direction { get; }
    public float ClosestMeters { get; }
    public int BurstShots { get; }
    public string ShooterProfileId { get; }
    public Vector3 ShooterPosition { get; }
    public float ShooterDistanceMeters { get; }
    public bool ThreatenedOperator { get; }
    public bool ThreatenedOwner { get; }
}

/// <summary>
/// Target-specific incoming-fire evidence. Receipts are retained independently per Operator and
/// hostile shooter, so simultaneous attacks do not overwrite each other. The service never mutates
/// SAIN: the unified Awareness coordinator qualifies and distributes these contacts.
/// </summary>
internal static class VanguardNearMissSuppressionService
{
    public const string StatusTag = "VANGUARD_NEAR_MISS_SUPPRESSION_STATUS";

    private const float MaxShotDistanceMeters = 120f;
    private const float NearMissRadiusMeters = 5f;
    private static readonly TimeSpan SuppressionWindow = TimeSpan.FromSeconds(3.0d);
    private static readonly TimeSpan BurstAggregationWindow = TimeSpan.FromSeconds(1.5d);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Dictionary<string, State>> StateByBotAndShooter = new(StringComparer.OrdinalIgnoreCase);

    public static void ObserveHostileShot(Player? shooter, Vector3 origin, Vector3 direction)
    {
        if (!VanguardFikaCompat.IsRaidAuthority || shooter == null || direction.sqrMagnitude <= 0.001f)
        {
            return;
        }

        string shooterProfileId = Normalize(shooter.ProfileId);
        if (string.Equals(shooterProfileId, "none", StringComparison.OrdinalIgnoreCase)
            || VanguardRaidOperatorRuntimeRegistry.IsKnownOwnerProfileId(shooterProfileId)
            || VanguardFriendlyIdentityRegistry.IsProtectedFriendlyTargetProfileId(shooterProfileId))
        {
            return;
        }

        Vector3 rayDirection = direction.normalized;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (VanguardRaidOperatorRuntimeRecord record in VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators())
        {
            BotOwner? botOwner = record.BotOwner;
            if (botOwner == null || botOwner.IsDead || string.IsNullOrWhiteSpace(record.BotProfileId))
            {
                continue;
            }

            Vector3 operatorCenter;
            try
            {
                operatorCenter = botOwner.GetPlayer.Transform.position + Vector3.up * 1.05f;
            }
            catch
            {
                continue;
            }

            bool operatorThreat = TryResolveTrajectoryThreat(origin, rayDirection, operatorCenter, out float operatorAlong, out float operatorLateral);
            bool ownerThreat = false;
            float ownerAlong = float.MaxValue;
            float ownerLateral = float.MaxValue;
            Player? ownerPlayer = VanguardFikaCompat.FindRaidPlayerByProfileId(record.OwnerProfileId);
            if (ownerPlayer?.Transform != null)
            {
                Vector3 ownerCenter = ownerPlayer.Transform.position + Vector3.up * 1.05f;
                ownerThreat = TryResolveTrajectoryThreat(origin, rayDirection, ownerCenter, out ownerAlong, out ownerLateral);
            }

            if (!operatorThreat && !ownerThreat)
            {
                continue;
            }

            float along = Math.Min(operatorThreat ? operatorAlong : float.MaxValue, ownerThreat ? ownerAlong : float.MaxValue);
            float lateral = Math.Min(operatorThreat ? operatorLateral : float.MaxValue, ownerThreat ? ownerLateral : float.MaxValue);
            Vector3 obstructionOrigin = origin + rayDirection * 1.0f;
            float obstructionDistance = Math.Max(0.1f, along - 1.75f);
            if (Physics.Raycast(obstructionOrigin, rayDirection, out _, obstructionDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                continue;
            }

            float shooterDistance = Vector3.Distance(origin, operatorCenter);
            Register(record, shooterProfileId, shooter.Position, shooterDistance, operatorThreat, ownerThreat, rayDirection, lateral, now);
        }
    }

    /// <summary>
    /// Compatibility/readiness query: true when at least one hostile projectile receipt is active.
    /// The returned snapshot is the most recent receipt, preferring a shot threatening the owner.
    /// </summary>
    public static bool IsRecent(string? botProfileId, DateTimeOffset now, out VanguardNearMissSuppressionSnapshot snapshot)
    {
        string bot = Normalize(botProfileId);
        lock (Sync)
        {
            if (!StateByBotAndShooter.TryGetValue(bot, out Dictionary<string, State> byShooter))
            {
                snapshot = default;
                return false;
            }

            PruneExpiredLocked(bot, byShooter, now);
            State? selected = null;
            foreach (State state in byShooter.Values)
            {
                if (selected == null
                    || (state.ThreatenedOwner && !selected.ThreatenedOwner)
                    || (state.ThreatenedOwner == selected.ThreatenedOwner
                        && state.LastObservedAtUtc > selected.LastObservedAtUtc))
                {
                    selected = state;
                }
            }

            if (selected == null)
            {
                snapshot = default;
                return false;
            }

            snapshot = ToSnapshot(selected);
            return true;
        }
    }

    /// <summary>
    /// Returns all currently valid hostile-shooter receipts for one Operator. The array is an
    /// immutable point-in-time view; callers cannot mutate the service state.
    /// </summary>
    public static VanguardNearMissSuppressionSnapshot[] GetRecentContacts(string? botProfileId, DateTimeOffset now)
    {
        string bot = Normalize(botProfileId);
        lock (Sync)
        {
            if (!StateByBotAndShooter.TryGetValue(bot, out Dictionary<string, State> byShooter))
            {
                return Array.Empty<VanguardNearMissSuppressionSnapshot>();
            }

            PruneExpiredLocked(bot, byShooter, now);
            if (byShooter.Count == 0)
            {
                return Array.Empty<VanguardNearMissSuppressionSnapshot>();
            }

            var states = new List<State>(byShooter.Values);
            states.Sort(static (left, right) =>
            {
                int ownerThreat = right.ThreatenedOwner.CompareTo(left.ThreatenedOwner);
                return ownerThreat != 0
                    ? ownerThreat
                    : right.LastObservedAtUtc.CompareTo(left.LastObservedAtUtc);
            });

            var snapshots = new VanguardNearMissSuppressionSnapshot[states.Count];
            for (int index = 0; index < states.Count; index++)
            {
                snapshots[index] = ToSnapshot(states[index]);
            }

            return snapshots;
        }
    }

    private static void PruneExpiredLocked(string botProfileId, Dictionary<string, State> byShooter, DateTimeOffset now)
    {
        foreach (string shooterId in byShooter.Keys.ToArray())
        {
            if (byShooter[shooterId].UntilUtc <= now)
            {
                byShooter.Remove(shooterId);
            }
        }

        if (byShooter.Count == 0)
        {
            StateByBotAndShooter.Remove(botProfileId);
        }
    }

    private static VanguardNearMissSuppressionSnapshot ToSnapshot(State state)
        => new(
            true,
            state.LastObservedAtUtc,
            state.UntilUtc,
            state.Direction,
            state.ClosestMeters,
            state.BurstShots,
            state.ShooterProfileId,
            state.ShooterPosition,
            state.ShooterDistanceMeters,
            state.ThreatenedOperator,
            state.ThreatenedOwner);

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            StateByBotAndShooter.Clear();
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_NEAR_MISS_RESET reason={Safe(reason)}; radiusMeters={NearMissRadiusMeters:0.0}; windowSeconds={SuppressionWindow.TotalSeconds:0.0}; targetSpecific=true; multipleShootersPreserved=true; squadPropagation=true; directTargetMutation=false; sainRemainsCombatOwner=true; tag={StatusTag}");
    }

    private static bool TryResolveTrajectoryThreat(Vector3 origin, Vector3 rayDirection, Vector3 targetCenter, out float along, out float lateral)
    {
        Vector3 fromOrigin = targetCenter - origin;
        along = Vector3.Dot(fromOrigin, rayDirection);
        if (along <= 0.75f || along > MaxShotDistanceMeters)
        {
            lateral = float.MaxValue;
            return false;
        }

        Vector3 closest = origin + rayDirection * along;
        lateral = Vector3.Distance(targetCenter, closest);
        return lateral <= NearMissRadiusMeters;
    }

    private static void Register(
        VanguardRaidOperatorRuntimeRecord record,
        string shooterProfileId,
        Vector3 shooterPosition,
        float shooterDistanceMeters,
        bool threatenedOperator,
        bool threatenedOwner,
        Vector3 direction,
        float lateral,
        DateTimeOffset now)
    {
        bool emit;
        int burstShots;
        lock (Sync)
        {
            if (!StateByBotAndShooter.TryGetValue(record.BotProfileId, out Dictionary<string, State> byShooter))
            {
                byShooter = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);
                StateByBotAndShooter[record.BotProfileId] = byShooter;
            }

            if (!byShooter.TryGetValue(shooterProfileId, out State state))
            {
                state = new State { ShooterProfileId = shooterProfileId };
                byShooter[shooterProfileId] = state;
            }

            bool sameBurst = now - state.LastObservedAtUtc <= BurstAggregationWindow;
            state.BurstShots = sameBurst ? state.BurstShots + 1 : 1;
            state.Direction = direction;
            state.ClosestMeters = sameBurst ? Math.Min(state.ClosestMeters, lateral) : lateral;
            state.ShooterPosition = shooterPosition;
            state.ShooterDistanceMeters = shooterDistanceMeters;
            state.ThreatenedOperator |= threatenedOperator;
            state.ThreatenedOwner |= threatenedOwner;
            state.UntilUtc = now + SuppressionWindow;
            state.LastObservedAtUtc = now;
            emit = now - state.LastLogAtUtc >= BurstAggregationWindow;
            if (emit)
            {
                state.LastLogAtUtc = now;
            }
            burstShots = state.BurstShots;
        }

        if (emit)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_NEAR_MISS_PRESSURE operator={Safe(record.OperatorId)}; botProfile={Safe(record.BotProfileId)}; owner={Safe(record.OwnerProfileId)}; shooter={Safe(shooterProfileId)}; shooterDistance={shooterDistanceMeters:0.0}; threatenedOperator={threatenedOperator}; threatenedOwner={threatenedOwner}; closestMeters={lateral:0.00}; burstShots={burstShots}; suppressionSeconds={SuppressionWindow.TotalSeconds:0.0}; targetEvidence=qualified_by_unified_scanner; multipleShootersPreserved=true; reaction=return_fire_candidate; tag={StatusTag}");
        }
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Safe(string? value) => Normalize(value).Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private sealed class State
    {
        public DateTimeOffset UntilUtc;
        public DateTimeOffset LastObservedAtUtc;
        public DateTimeOffset LastLogAtUtc;
        public Vector3 Direction;
        public float ClosestMeters = float.MaxValue;
        public int BurstShots;
        public string ShooterProfileId = "none";
        public Vector3 ShooterPosition;
        public float ShooterDistanceMeters = float.MaxValue;
        public bool ThreatenedOperator;
        public bool ThreatenedOwner;
    }
}
#endif
