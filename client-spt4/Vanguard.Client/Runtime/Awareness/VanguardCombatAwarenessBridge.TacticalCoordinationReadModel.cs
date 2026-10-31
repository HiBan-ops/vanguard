#if SPT_CLIENT
using Comfort.Common;
using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

// Responsibility: exposes fresh, already-qualified Vanguard squad contacts to Tactical Coordination as read-only exact runtime geometry.
// Flow: Existing Awareness contact authority is filtered first; only then is the already-authorized target ProfileId resolved to the current live Player and sampled for position/look direction.
// Authority boundary: Read-only projection. It never scans the world for targets, creates/extends contacts, mutates SAIN, changes GoalEnemy, or grants Tactical Coordination execution authority.
// Invariant: Runtime geometry can only exist for a target that is already present as a fresh non-suspicion Vanguard squad contact for the exact owner.
namespace Vanguard.Client.Runtime.Awareness;

internal static partial class VanguardCombatAwarenessBridge
{
    internal readonly struct TacticalCoordinationThreatRuntimeObservation
    {
        public TacticalCoordinationThreatRuntimeObservation(
            string ownerProfileId,
            string targetProfileId,
            string contactKind,
            string contactReason,
            string sourceOperatorId,
            string sourceBotProfileId,
            DateTimeOffset observedAtUtc,
            DateTimeOffset episodeStartedAtUtc,
            DateTimeOffset expiresAtUtc,
            Vector3 runtimePosition,
            Vector3 runtimeLookDirection,
            DateTimeOffset capturedAtUtc)
        {
            OwnerProfileId = ownerProfileId;
            TargetProfileId = targetProfileId;
            ContactKind = contactKind;
            ContactReason = contactReason;
            SourceOperatorId = sourceOperatorId;
            SourceBotProfileId = sourceBotProfileId;
            ObservedAtUtc = observedAtUtc;
            EpisodeStartedAtUtc = episodeStartedAtUtc;
            ExpiresAtUtc = expiresAtUtc;
            RuntimePosition = runtimePosition;
            RuntimeLookDirection = runtimeLookDirection;
            CapturedAtUtc = capturedAtUtc;
        }

        public string OwnerProfileId { get; }
        public string TargetProfileId { get; }
        public string ContactKind { get; }
        public string ContactReason { get; }
        public string SourceOperatorId { get; }
        public string SourceBotProfileId { get; }
        public DateTimeOffset ObservedAtUtc { get; }
        public DateTimeOffset EpisodeStartedAtUtc { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
        public Vector3 RuntimePosition { get; }
        public Vector3 RuntimeLookDirection { get; }
        public DateTimeOffset CapturedAtUtc { get; }
    }

    /// <summary>
    /// Returns exact runtime geometry only for contacts already qualified by Vanguard Awareness.
    /// GameWorld is used strictly as a resolver for an Awareness-authorized TargetProfileId; it is never enumerated here.
    /// </summary>
    public static IReadOnlyList<TacticalCoordinationThreatRuntimeObservation> GetFreshQualifiedTacticalCoordinationThreats(DateTimeOffset now)
    {
        var authorizedContacts = new List<SquadCombatContactState>();
        lock (Sync)
        {
            foreach (var ownerContacts in SquadContactsByOwnerProfileId.Values)
            {
                foreach (SquadCombatContactState contact in ownerContacts.Values)
                {
                    if (contact.ExpiresAtUtc <= now || IsSquadSuspicionKind(contact.Kind))
                    {
                        continue;
                    }

                    authorizedContacts.Add(contact);
                }
            }
        }

        if (authorizedContacts.Count == 0)
        {
            return Array.Empty<TacticalCoordinationThreatRuntimeObservation>();
        }

        GameWorld? world = Singleton<GameWorld>.Instance;
        if (world == null)
        {
            return Array.Empty<TacticalCoordinationThreatRuntimeObservation>();
        }

        var observations = new List<TacticalCoordinationThreatRuntimeObservation>(authorizedContacts.Count);
        foreach (SquadCombatContactState contact in authorizedContacts)
        {
            string owner = Normalize(contact.OwnerProfileId);
            string target = Normalize(contact.TargetId);
            if (string.Equals(owner, "none", StringComparison.OrdinalIgnoreCase)
                || string.Equals(target, "none", StringComparison.OrdinalIgnoreCase)
                || IsProtectedFriendlyTarget(target, out _))
            {
                continue;
            }

            Player? player;
            try
            {
                player = world.GetAlivePlayerByProfileID(target);
            }
            catch
            {
                player = null;
            }

            if (player == null || player.HealthController?.IsAlive != true)
            {
                continue;
            }

            Vector3 position;
            Vector3 lookDirection;
            try
            {
                position = player.Position;
                lookDirection = FlattenAndNormalizeTacticalDirection(player.LookDirection);
            }
            catch
            {
                continue;
            }

            if (!IsFinite(position) || lookDirection.sqrMagnitude <= 0.01f)
            {
                continue;
            }

            observations.Add(new TacticalCoordinationThreatRuntimeObservation(
                owner,
                target,
                Safe(contact.Kind),
                Safe(contact.Reason),
                Normalize(contact.SourceOperatorId),
                Normalize(contact.SourceBotProfileId),
                contact.ObservedAtUtc,
                contact.EpisodeStartedAtUtc,
                contact.ExpiresAtUtc,
                position,
                lookDirection,
                now));
        }

        return observations;
    }

    private static Vector3 FlattenAndNormalizeTacticalDirection(Vector3 direction)
    {
        direction.y = 0f;
        return direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.zero;
    }

    private static bool IsFinite(Vector3 value)
        => !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
            && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
}
#endif
