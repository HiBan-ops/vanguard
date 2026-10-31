#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Decision;

// Responsibility: observes exact-target retarget events from both the decision snapshot and the canonical live SAIN GoalEnemy gate around active Tactical Opportunity leases so later calibration can decide whether an abstract tactical intent could survive while target continuity never does.
// Flow: the canonical lease supplies its exact owner/target identity, the current decision snapshot or a concrete live SAIN GoalEnemy mismatch reveals a retarget, and only fresh already-qualified Awareness contacts are compared as a spatial pair before the canonical lease gate independently ends/revalidates the exact target lease.
// Authority boundary: shadow-only diagnostics. This service does not scan the world, select/promote a target, alter TacticalIntentContinuity, issue movement, or mutate SAIN/EFT aim/fire/perception.
// Invariant: target continuity is always false across retarget; tactical-intent continuity is at most MAYBE when both old and new threats are simultaneously fresh qualified Awareness contacts, and no same-engagement distance/angle threshold is promoted before grouped runtime calibration.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class TacticalEngagementContextShadowService
{
    public const string StatusTag = "VANGUARD_TC_ENGAGEMENT_CONTEXT_SHADOW_STATUS";
    private const int MaxDiagnosticFieldLength = 160;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, RetargetObservation> ByLeaseId = new(StringComparer.OrdinalIgnoreCase);

    internal static void ObserveLeaseDecision(
        string? ownerProfileId,
        string? exactLeaseThreatId,
        string? botProfileId,
        string? kind,
        string? leaseId,
        OperatorDecisionSnapshot? decision,
        DateTimeOffset now)
    {
        string owner = Normalize(ownerProfileId);
        string originalThreat = NormalizeThreat(exactLeaseThreatId);
        string bot = Normalize(botProfileId);
        string lease = Normalize(leaseId);
        if (owner.Length == 0 || originalThreat.Length == 0 || bot.Length == 0 || lease.Length == 0 || decision == null)
        {
            return;
        }

        string currentThreat = NormalizeThreat(decision.Threat?.EnemyId);
        ObserveRetarget(owner, originalThreat, currentThreat, bot, Normalize(kind), lease, "decision_snapshot", decision, now);
    }

    internal static void ObserveLiveGoalEnemyRetarget(
        string? ownerProfileId,
        string? exactLeaseThreatId,
        string? liveGoalEnemyProfileId,
        string? botProfileId,
        string? kind,
        string? leaseId,
        OperatorDecisionSnapshot? decision,
        DateTimeOffset now)
    {
        string owner = Normalize(ownerProfileId);
        string originalThreat = NormalizeThreat(exactLeaseThreatId);
        string currentThreat = NormalizeThreat(liveGoalEnemyProfileId);
        string bot = Normalize(botProfileId);
        string lease = Normalize(leaseId);
        if (owner.Length == 0 || originalThreat.Length == 0 || currentThreat.Length == 0 || bot.Length == 0 || lease.Length == 0 || decision == null)
        {
            return;
        }

        ObserveRetarget(owner, originalThreat, currentThreat, bot, Normalize(kind), lease, "live_sain_goal_enemy", decision, now);
    }

    private static void ObserveRetarget(
        string owner,
        string originalThreat,
        string currentThreat,
        string bot,
        string kind,
        string lease,
        string observationSource,
        OperatorDecisionSnapshot decision,
        DateTimeOffset now)
    {
        if (string.Equals(originalThreat, currentThreat, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (Sync)
        {
            if (ByLeaseId.TryGetValue(lease, out RetargetObservation? existing)
                && string.Equals(existing.NewThreatId, currentThreat, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        IReadOnlyList<VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation> qualified =
            VanguardCombatAwarenessBridge.GetFreshQualifiedTacticalCoordinationThreats(now);
        VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation oldThreat = default;
        VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation newThreat = default;
        bool oldFresh = TryFindFreshQualified(qualified, owner, originalThreat, out oldThreat);
        bool newFresh = currentThreat.Length > 0 && TryFindFreshQualified(qualified, owner, currentThreat, out newThreat);

        string qualification;
        string intentCandidate;
        string spatialAssessment;
        float? threatPairDistance = null;
        float? oldRange = null;
        float? newRange = null;
        float? bearingDelta = null;
        float? verticalDelta = null;
        string oldContactKind = oldFresh ? oldThreat.ContactKind : "none";
        string newContactKind = newFresh ? newThreat.ContactKind : "none";
        double? oldAgeSeconds = oldFresh ? Math.Max(0d, (now - oldThreat.ObservedAtUtc).TotalSeconds) : null;
        double? newAgeSeconds = newFresh ? Math.Max(0d, (now - newThreat.ObservedAtUtc).TotalSeconds) : null;

        if (currentThreat.Length == 0)
        {
            qualification = "NO_CURRENT_TARGET";
            intentCandidate = "UNRESOLVED";
            spatialAssessment = "UNRESOLVED_NO_NEW_TARGET";
        }
        else if (!oldFresh || !newFresh)
        {
            qualification = !oldFresh && !newFresh ? "BOTH_AWARENESS_CONTACTS_MISSING" : !oldFresh ? "OLD_AWARENESS_CONTACT_MISSING" : "NEW_AWARENESS_CONTACT_MISSING";
            intentCandidate = "UNRESOLVED";
            spatialAssessment = "UNRESOLVED_REQUIRES_FRESH_QUALIFIED_PAIR";
        }
        else
        {
            Vector3 botPosition = decision.Position;
            threatPairDistance = HorizontalDistance(oldThreat.RuntimePosition, newThreat.RuntimePosition);
            oldRange = HorizontalDistance(botPosition, oldThreat.RuntimePosition);
            newRange = HorizontalDistance(botPosition, newThreat.RuntimePosition);
            bearingDelta = HorizontalAngleDegrees(oldThreat.RuntimePosition - botPosition, newThreat.RuntimePosition - botPosition);
            verticalDelta = Math.Abs(oldThreat.RuntimePosition.y - newThreat.RuntimePosition.y);
            qualification = "FRESH_QUALIFIED_AWARENESS_PAIR";
            intentCandidate = "MAYBE";
            spatialAssessment = "CALIBRATION_REQUIRED_NO_PROMOTED_THRESHOLD";
        }

        var observation = new RetargetObservation(
            owner,
            originalThreat,
            currentThreat,
            bot,
            kind,
            lease,
            observationSource,
            qualification,
            intentCandidate,
            spatialAssessment,
            threatPairDistance,
            oldRange,
            newRange,
            bearingDelta,
            verticalDelta,
            oldContactKind,
            newContactKind,
            oldAgeSeconds,
            newAgeSeconds,
            now);
        lock (Sync)
        {
            ByLeaseId[lease] = observation;
        }

        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
            $"TC_ENGAGEMENT_CONTEXT_RETARGET kind={Bounded(observation.Kind)}; owner={Bounded(observation.OwnerProfileId)}; bot={Bounded(observation.BotProfileId)}; lease={Bounded(observation.LeaseId)}; source={Bounded(observation.ObservationSource)}; oldThreat={Bounded(observation.OldThreatId)}; newThreat={Bounded(observation.NewThreatId)}; qualification={Bounded(observation.Qualification)}; targetContinuity=false; tacticalIntentContinuityCandidate={Bounded(observation.IntentCandidate)}; spatialAssessment={Bounded(observation.SpatialAssessment)}; threatPairDistanceMeters={Number(observation.ThreatPairDistanceMeters)}; oldRangeMeters={Number(observation.OldRangeMeters)}; newRangeMeters={Number(observation.NewRangeMeters)}; bearingDeltaDegrees={Number(observation.BearingDeltaDegrees)}; verticalDeltaMeters={Number(observation.VerticalDeltaMeters)}; oldContactKind={Bounded(observation.OldContactKind)}; newContactKind={Bounded(observation.NewContactKind)}; oldContactAgeSeconds={Number(observation.OldContactAgeSeconds)}; newContactAgeSeconds={Number(observation.NewContactAgeSeconds)}; awarenessOnly=true; scanner=false; promotedThreshold=false; behaviorMutation=false; targetMutation=false; aimMutation=false; fireCommand=false");
    }

    internal static void ObserveEnd(
        string? botProfileId,
        string? leaseId,
        string? outcome,
        string? reason,
        DateTimeOffset now)
    {
        string lease = Normalize(leaseId);
        RetargetObservation? observation = null;
        lock (Sync)
        {
            if (lease.Length > 0 && ByLeaseId.TryGetValue(lease, out RetargetObservation? current))
            {
                observation = current;
                ByLeaseId.Remove(lease);
            }
        }
        if (observation == null)
        {
            return;
        }

        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
            $"TC_ENGAGEMENT_CONTEXT_LEASE_END kind={Bounded(observation.Kind)}; owner={Bounded(observation.OwnerProfileId)}; bot={Bounded(botProfileId)}; lease={Bounded(observation.LeaseId)}; source={Bounded(observation.ObservationSource)}; oldThreat={Bounded(observation.OldThreatId)}; newThreat={Bounded(observation.NewThreatId)}; qualification={Bounded(observation.Qualification)}; targetContinuity=false; tacticalIntentContinuityCandidate={Bounded(observation.IntentCandidate)}; canonicalOutcome={Bounded(outcome)}; canonicalReason={Bounded(reason)}; observedForSeconds={Math.Max(0d, (now - observation.ObservedAtUtc).TotalSeconds):0.00}; shadowOnly=true; continuityMutation=false; behaviorMutation=false");
    }

    internal static void ResetForRaidLifecycle(string? reason)
    {
        lock (Sync)
        {
            ByLeaseId.Clear();
        }
    }

    private static bool TryFindFreshQualified(
        IReadOnlyList<VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation> observations,
        string ownerProfileId,
        string targetProfileId,
        out VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation observation)
    {
        for (int index = 0; index < observations.Count; index++)
        {
            var current = observations[index];
            if (string.Equals(current.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.TargetProfileId, targetProfileId, StringComparison.OrdinalIgnoreCase))
            {
                observation = current;
                return true;
            }
        }

        observation = default;
        return false;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    private static float HorizontalAngleDegrees(Vector3 first, Vector3 second)
    {
        first.y = 0f;
        second.y = 0f;
        if (first.sqrMagnitude <= 0.0001f || second.sqrMagnitude <= 0.0001f)
        {
            return 0f;
        }
        return Vector3.Angle(first, second);
    }

    private static string Normalize(string? value)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        return string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "null", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "unknown", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : normalized;
    }

    private static string NormalizeThreat(string? value) => Normalize(value);

    private static string Bounded(string? value)
    {
        string safe = Normalize(value).Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
        if (safe.Length == 0)
        {
            return "none";
        }
        return safe.Length <= MaxDiagnosticFieldLength ? safe : safe.Substring(0, MaxDiagnosticFieldLength);
    }

    private static string Number(float? value) => value.HasValue && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value) ? value.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "unknown";
    private static string Number(double? value) => value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) ? value.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "unknown";

    private sealed class RetargetObservation
    {
        public RetargetObservation(
            string ownerProfileId,
            string oldThreatId,
            string newThreatId,
            string botProfileId,
            string kind,
            string leaseId,
            string observationSource,
            string qualification,
            string intentCandidate,
            string spatialAssessment,
            float? threatPairDistanceMeters,
            float? oldRangeMeters,
            float? newRangeMeters,
            float? bearingDeltaDegrees,
            float? verticalDeltaMeters,
            string oldContactKind,
            string newContactKind,
            double? oldContactAgeSeconds,
            double? newContactAgeSeconds,
            DateTimeOffset observedAtUtc)
        {
            OwnerProfileId = ownerProfileId;
            OldThreatId = oldThreatId;
            NewThreatId = newThreatId;
            BotProfileId = botProfileId;
            Kind = kind;
            LeaseId = leaseId;
            ObservationSource = observationSource;
            Qualification = qualification;
            IntentCandidate = intentCandidate;
            SpatialAssessment = spatialAssessment;
            ThreatPairDistanceMeters = threatPairDistanceMeters;
            OldRangeMeters = oldRangeMeters;
            NewRangeMeters = newRangeMeters;
            BearingDeltaDegrees = bearingDeltaDegrees;
            VerticalDeltaMeters = verticalDeltaMeters;
            OldContactKind = oldContactKind;
            NewContactKind = newContactKind;
            OldContactAgeSeconds = oldContactAgeSeconds;
            NewContactAgeSeconds = newContactAgeSeconds;
            ObservedAtUtc = observedAtUtc;
        }

        public string OwnerProfileId { get; }
        public string OldThreatId { get; }
        public string NewThreatId { get; }
        public string BotProfileId { get; }
        public string Kind { get; }
        public string LeaseId { get; }
        public string ObservationSource { get; }
        public string Qualification { get; }
        public string IntentCandidate { get; }
        public string SpatialAssessment { get; }
        public float? ThreatPairDistanceMeters { get; }
        public float? OldRangeMeters { get; }
        public float? NewRangeMeters { get; }
        public float? BearingDeltaDegrees { get; }
        public float? VerticalDeltaMeters { get; }
        public string OldContactKind { get; }
        public string NewContactKind { get; }
        public double? OldContactAgeSeconds { get; }
        public double? NewContactAgeSeconds { get; }
        public DateTimeOffset ObservedAtUtc { get; }
    }
}
#endif
