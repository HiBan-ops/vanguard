#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EFT;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Alliance;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Captures short-lived evidence that the player owner is under immediate attack so nearby Operators can react to a real threat instead of stale shared knowledge.
// Flow: Fresh hit/shot-at evidence is tied to the owner and aggressor, distance/position facts are stored, and the snapshot expires automatically when the immediate-threat window closes.
// Authority boundary: This service supplies awareness evidence only; target selection and combat execution remain with the awareness policy and SAIN.
// Invariant: Only recent evidence may qualify as immediate threat, and raid reset removes every cached owner/aggressor relationship.
namespace Vanguard.Client.Runtime.Awareness;

internal readonly struct VanguardOwnerImmediateThreatSnapshot
{
    public VanguardOwnerImmediateThreatSnapshot(
        string ownerProfileId,
        string targetProfileId,
        IPlayer target,
        Vector3 ownerPositionAtHit,
        Vector3 targetPosition,
        float operatorTargetDistance,
        float ownerTargetDistance,
        DateTimeOffset observedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        OwnerProfileId = ownerProfileId;
        TargetProfileId = targetProfileId;
        Target = target;
        OwnerPositionAtHit = ownerPositionAtHit;
        TargetPosition = targetPosition;
        OperatorTargetDistance = operatorTargetDistance;
        OwnerTargetDistance = ownerTargetDistance;
        ObservedAtUtc = observedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public string OwnerProfileId { get; }
    public string TargetProfileId { get; }
    public IPlayer Target { get; }
    public Vector3 OwnerPositionAtHit { get; }
    public Vector3 TargetPosition { get; }
    public float OperatorTargetDistance { get; }
    public float OwnerTargetDistance { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
}

/// <summary>
/// Multi-target memory of confirmed hostile damage against a player squad owner. Contacts are
/// evidence only: they enter the shared Vanguard contact picture, then every Operator requalifies
/// them from its own geometry before the unified coordinator commits one assignment into SAIN.
/// </summary>
internal static class VanguardOwnerImmediateThreatService
{
    public const string StatusTag = "VANGUARD_OWNER_IMMEDIATE_THREAT_STATUS";

    private const float MaxOwnerTargetDistanceMeters = 32.0f;
    private const int MaxContactsPerOwner = 8;
    private static readonly TimeSpan ContactTtl = TimeSpan.FromSeconds(3.50d);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Dictionary<string, OwnerImmediateThreatContact>> ContactByOwnerAndTarget = new(StringComparer.OrdinalIgnoreCase);

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            ContactByOwnerAndTarget.Clear();
        }

        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
            $"VANGUARD_OWNER_IMMEDIATE_THREAT_RESET reason={Safe(reason)}; contacts=cleared; multiTarget=true; gameplayAuthority=false; tag={StatusTag}");
    }

    public static void ObserveConfirmedOwnerHit(Player? victim, DamageInfoStruct damageInfo, EBodyPart bodyPart, DateTimeOffset now)
    {
        if (victim == null || victim.HealthController?.IsAlive != true)
        {
            return;
        }

        string ownerProfileId = Normalize(victim.ProfileId);
        if (string.Equals(ownerProfileId, "none", StringComparison.OrdinalIgnoreCase)
            || !VanguardRaidOperatorRuntimeRegistry.IsKnownOwnerProfileId(ownerProfileId))
        {
            return;
        }

        IPlayer? attacker;
        try
        {
            attacker = damageInfo.Player?.iPlayer;
        }
        catch
        {
            attacker = null;
        }

        string attackerProfileId = Normalize(attacker?.ProfileId);
        if (attacker == null
            || string.Equals(attackerProfileId, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attackerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
            || VanguardFriendlyIdentityRegistry.IsProtectedFriendlyTargetProfileId(attackerProfileId)
            || !TryGetLivePosition(attacker, out Vector3 attackerPosition))
        {
            return;
        }

        float ownerTargetDistance = HorizontalDistance(victim.Position, attackerPosition);
        if (ownerTargetDistance > MaxOwnerTargetDistanceMeters)
        {
            // Long-range projectile identity is handled by the target-specific near-miss/shot lane.
            return;
        }

        var contact = new OwnerImmediateThreatContact(
            ownerProfileId,
            attackerProfileId,
            attacker,
            victim.Position,
            now,
            now + ContactTtl);
        lock (Sync)
        {
            if (!ContactByOwnerAndTarget.TryGetValue(ownerProfileId, out Dictionary<string, OwnerImmediateThreatContact> byTarget))
            {
                byTarget = new Dictionary<string, OwnerImmediateThreatContact>(StringComparer.OrdinalIgnoreCase);
                ContactByOwnerAndTarget[ownerProfileId] = byTarget;
            }

            PruneExpiredLocked(byTarget, now);
            byTarget[attackerProfileId] = contact;
            if (byTarget.Count > MaxContactsPerOwner)
            {
                foreach (string staleTarget in byTarget.Values
                    .OrderByDescending(entry => entry.ObservedAtUtc)
                    .Skip(MaxContactsPerOwner)
                    .Select(entry => entry.TargetProfileId)
                    .ToArray())
                {
                    byTarget.Remove(staleTarget);
                }
            }
        }

        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
            $"VANGUARD_OWNER_IMMEDIATE_THREAT_RECORDED owner={Safe(ownerProfileId)}; target={Safe(attackerProfileId)}; ownerTargetDistance={ownerTargetDistance.ToString("0.0", CultureInfo.InvariantCulture)}; bodyPart={Safe(bodyPart.ToString())}; reportedDamage={Math.Max(0f, damageInfo.Damage).ToString("0.0", CultureInfo.InvariantCulture)}; ttl={ContactTtl.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)}; source=Player.ApplyDamageInfo; multiTarget=true; losFabricated=false; movementAuthority=false; tag={StatusTag}");
    }

    public static VanguardOwnerImmediateThreatSnapshot[] GetRecentForRecipient(
        OperatorDecisionSnapshot snapshot,
        BotOwner? botOwner,
        DateTimeOffset now)
    {
        if (snapshot == null
            || !snapshot.Alive
            || botOwner == null
            || botOwner.IsDead
            || string.IsNullOrWhiteSpace(snapshot.OwnerProfileId)
            || snapshot.SquadCohesion.OperatorDistanceToOwner > VanguardMovementAuthorityDoctrine.SquadContactAssistRadiusMeters)
        {
            return Array.Empty<VanguardOwnerImmediateThreatSnapshot>();
        }

        OwnerImmediateThreatContact[] contacts;
        lock (Sync)
        {
            if (!ContactByOwnerAndTarget.TryGetValue(snapshot.OwnerProfileId, out Dictionary<string, OwnerImmediateThreatContact> byTarget))
            {
                return Array.Empty<VanguardOwnerImmediateThreatSnapshot>();
            }

            PruneExpiredLocked(byTarget, now);
            if (byTarget.Count == 0)
            {
                ContactByOwnerAndTarget.Remove(snapshot.OwnerProfileId);
                return Array.Empty<VanguardOwnerImmediateThreatSnapshot>();
            }

            contacts = byTarget.Values
                .OrderByDescending(contact => contact.ObservedAtUtc)
                .ToArray();
        }

        var result = new List<VanguardOwnerImmediateThreatSnapshot>(contacts.Length);
        foreach (OwnerImmediateThreatContact contact in contacts)
        {
            if (VanguardFriendlyIdentityRegistry.IsProtectedFriendlyTargetProfileId(contact.TargetProfileId)
                || !TryGetLivePosition(contact.Target, out Vector3 targetPosition))
            {
                Invalidate(snapshot.OwnerProfileId, contact.TargetProfileId, "target_not_live_or_became_friendly");
                continue;
            }

            bool groupEnemy;
            try
            {
                groupEnemy = botOwner.BotsGroup != null
                    && (botOwner.BotsGroup.IsEnemy(contact.Target) || botOwner.BotsGroup.IsPlayerEnemy(contact.Target));
            }
            catch
            {
                groupEnemy = false;
            }

            if (!groupEnemy)
            {
                continue;
            }

            Vector3 ownerPosition = snapshot.SquadCohesion.OwnerPosition ?? contact.OwnerPositionAtHit;
            float ownerTargetDistance = HorizontalDistance(ownerPosition, targetPosition);
            if (ownerTargetDistance > MaxOwnerTargetDistanceMeters)
            {
                Invalidate(snapshot.OwnerProfileId, contact.TargetProfileId, "target_left_owner_immediate_envelope");
                continue;
            }

            float operatorTargetDistance = HorizontalDistance(botOwner.Position, targetPosition);
            result.Add(new VanguardOwnerImmediateThreatSnapshot(
                contact.OwnerProfileId,
                contact.TargetProfileId,
                contact.Target,
                contact.OwnerPositionAtHit,
                targetPosition,
                operatorTargetDistance,
                ownerTargetDistance,
                contact.ObservedAtUtc,
                contact.ExpiresAtUtc));
        }

        return result.ToArray();
    }

    private static void PruneExpiredLocked(
        Dictionary<string, OwnerImmediateThreatContact> byTarget,
        DateTimeOffset now)
    {
        foreach (string targetId in byTarget
            .Where(entry => entry.Value.ExpiresAtUtc <= now)
            .Select(entry => entry.Key)
            .ToArray())
        {
            byTarget.Remove(targetId);
        }
    }

    private static void Invalidate(string ownerProfileId, string targetProfileId, string reason)
    {
        lock (Sync)
        {
            if (ContactByOwnerAndTarget.TryGetValue(ownerProfileId, out Dictionary<string, OwnerImmediateThreatContact> byTarget))
            {
                byTarget.Remove(targetProfileId);
                if (byTarget.Count == 0)
                {
                    ContactByOwnerAndTarget.Remove(ownerProfileId);
                }
            }
        }

        VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
            $"VANGUARD_OWNER_IMMEDIATE_THREAT_INVALIDATED owner={Safe(ownerProfileId)}; target={Safe(targetProfileId)}; reason={Safe(reason)}; tag={StatusTag}");
    }

    private static bool TryGetLivePosition(IPlayer? player, out Vector3 position)
    {
        position = default;
        if (player == null)
        {
            return false;
        }

        try
        {
            BifacialTransform? transform = player.Transform;
            if (player.HealthController?.IsAlive != true || transform == null)
            {
                return false;
            }

            position = transform.position;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        float x = left.x - right.x;
        float z = left.z - right.z;
        return Mathf.Sqrt((x * x) + (z * z));
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private readonly struct OwnerImmediateThreatContact
    {
        public OwnerImmediateThreatContact(
            string ownerProfileId,
            string targetProfileId,
            IPlayer target,
            Vector3 ownerPositionAtHit,
            DateTimeOffset observedAtUtc,
            DateTimeOffset expiresAtUtc)
        {
            OwnerProfileId = ownerProfileId;
            TargetProfileId = targetProfileId;
            Target = target;
            OwnerPositionAtHit = ownerPositionAtHit;
            ObservedAtUtc = observedAtUtc;
            ExpiresAtUtc = expiresAtUtc;
        }

        public string OwnerProfileId { get; }
        public string TargetProfileId { get; }
        public IPlayer Target { get; }
        public Vector3 OwnerPositionAtHit { get; }
        public DateTimeOffset ObservedAtUtc { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
    }
}
#endif
