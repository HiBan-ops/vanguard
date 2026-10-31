#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Observes whether a Tactical Coordination destination retains tactical value for a short bounded window after physical arrival.
// Flow: movement start is recorded as MOVING; canonical destination_reached advances ARRIVED -> ESTABLISHING; retained owner/threat/local-authority/support facts are sampled until the bounded window closes and only fully-proven persistence is reported as ESTABLISHED.
// Authority boundary: Shadow telemetry only; this service never moves an Operator, reserves a lease, selects/promotes a target, changes SAIN state, aims or fires.
// Invariant: arrival is not treated as proof of durable tactical value; unknown evidence never becomes ESTABLISHED and no shadow result can alter runtime behavior.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class TacticalEstablishmentShadowService
{
    public const string StatusTag = "VANGUARD_TC_TACTICAL_ESTABLISHMENT_SHADOW";

    private const int MaxDiagnosticFieldLength = 160;
    private static readonly TimeSpan EstablishmentObservationWindow = TimeSpan.FromSeconds(4.0d);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(0.50d);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, EstablishmentObservation> ByBotProfileId = new(StringComparer.OrdinalIgnoreCase);


    internal static void TrackMoving(
        string? ownerProfileId,
        string? threatId,
        string? botProfileId,
        string? kind,
        string? leaseId,
        string? supportBotProfileId,
        string? supportContextId,
        Vector3 destination,
        float arrivalEnvelopeMeters,
        DateTimeOffset startedAtUtc)
    {
        string bot = Normalize(botProfileId);
        if (bot.Length == 0 || string.IsNullOrWhiteSpace(leaseId))
        {
            return;
        }

        var observation = new EstablishmentObservation(
            Normalize(ownerProfileId),
            Normalize(threatId),
            bot,
            Normalize(kind),
            Normalize(leaseId),
            Normalize(supportBotProfileId),
            Normalize(supportContextId),
            destination,
            Math.Max(0f, arrivalEnvelopeMeters),
            startedAtUtc);

        lock (Sync)
        {
            ByBotProfileId[bot] = observation;
        }
        EmitPhase(observation, "MOVING", startedAtUtc, "physical_lease_started");
    }

    internal static void ObserveEnd(
        string? botProfileId,
        string? leaseId,
        string? outcome,
        string? reason,
        DateTimeOffset endedAtUtc)
    {
        string bot = Normalize(botProfileId);
        string lease = Normalize(leaseId);
        EstablishmentObservation? observation;
        lock (Sync)
        {
            if (!ByBotProfileId.TryGetValue(bot, out observation)
                || !string.Equals(observation.LeaseId, lease, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            bool arrived = string.Equals(Normalize(outcome), "Completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Normalize(reason), "destination_reached", StringComparison.OrdinalIgnoreCase);
            if (!arrived)
            {
                ByBotProfileId.Remove(bot);
            }
            else
            {
                observation.Phase = "ARRIVED";
                observation.ArrivedAtUtc = endedAtUtc;
                observation.ObservationUntilUtc = endedAtUtc + EstablishmentObservationWindow;
                observation.NextSampleAtUtc = endedAtUtc;
            }
        }

        bool isArrival = string.Equals(Normalize(outcome), "Completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Normalize(reason), "destination_reached", StringComparison.OrdinalIgnoreCase);
        if (!isArrival)
        {
            VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
                $"TC_TACTICAL_ESTABLISHMENT_SHADOW phase=MOVING_TERMINATED; bot={Bounded(bot)}; lease={Bounded(lease)}; sourceOutcome={Bounded(outcome)}; sourceReason={Bounded(reason)}; shadowOnly=true; behaviorMutation=false");
            return;
        }

        EmitPhase(observation!, "ARRIVED", endedAtUtc, "canonical_destination_reached");
    }

    internal static void Tick(DateTimeOffset now)
    {
        EstablishmentObservation[] observations;
        lock (Sync)
        {
            observations = ByBotProfileId.Values
                .Where(item => string.Equals(item.Phase, "ARRIVED", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Phase, "ESTABLISHING", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        foreach (EstablishmentObservation observation in observations)
        {
            if (string.Equals(observation.Phase, "ARRIVED", StringComparison.OrdinalIgnoreCase))
            {
                observation.Phase = "ESTABLISHING";
                EmitPhase(observation, "ESTABLISHING", now, "bounded_post_arrival_observation_started");
            }

            if (now < observation.NextSampleAtUtc)
            {
                continue;
            }

            ValiditySample sample = EvaluateRetainedTacticalValidity(observation, now);
            observation.SampleCount++;
            observation.LastDistanceToDestinationMeters = sample.DistanceToDestinationMeters;
            observation.NextSampleAtUtc = now + SampleInterval;
            if (!sample.Known)
            {
                observation.UnresolvedSampleCount++;
                observation.FirstNonValidReason ??= sample.Reason;
            }
            else if (sample.Valid)
            {
                observation.ValidSampleCount++;
            }
            else
            {
                observation.InvalidSampleCount++;
                observation.FirstNonValidReason ??= sample.Reason;
            }

            VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
                $"TC_TACTICAL_ESTABLISHMENT_SAMPLE phase=ESTABLISHING; kind={Bounded(observation.Kind)}; owner={Bounded(observation.OwnerProfileId)}; threat={Bounded(observation.ThreatId)}; bot={Bounded(observation.BotProfileId)}; lease={Bounded(observation.LeaseId)}; validity={(sample.Known ? (sample.Valid ? "VALID" : "INVALID") : "UNRESOLVED")}; basis={Bounded(sample.Reason)}; sample={observation.SampleCount}; validSamples={observation.ValidSampleCount}; invalidSamples={observation.InvalidSampleCount}; unresolvedSamples={observation.UnresolvedSampleCount}; distanceToDestinationMeters={sample.DistanceToDestinationMeters:0.00}; withinArrivalEnvelope={Bool(sample.WithinArrivalEnvelope)}; shadowOnly=true; behaviorMutation=false; targetMutation=false; aimMutation=false; fireCommand=false");

            if (now < observation.ObservationUntilUtc)
            {
                continue;
            }

            bool established = observation.SampleCount > 0
                && observation.ValidSampleCount == observation.SampleCount
                && observation.InvalidSampleCount == 0
                && observation.UnresolvedSampleCount == 0;
            lock (Sync)
            {
                if (ByBotProfileId.TryGetValue(observation.BotProfileId, out EstablishmentObservation? current)
                    && string.Equals(current.LeaseId, observation.LeaseId, StringComparison.OrdinalIgnoreCase))
                {
                    ByBotProfileId.Remove(observation.BotProfileId);
                }
            }

            if (established)
            {
                observation.Phase = "ESTABLISHED";
                EmitPhase(observation, "ESTABLISHED", now, "retained_tactical_facts_valid_for_full_window");
            }
            else
            {
                VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
                    $"TC_TACTICAL_ESTABLISHMENT_WINDOW_CLOSED result=NOT_ESTABLISHED; kind={Bounded(observation.Kind)}; owner={Bounded(observation.OwnerProfileId)}; threat={Bounded(observation.ThreatId)}; bot={Bounded(observation.BotProfileId)}; lease={Bounded(observation.LeaseId)}; samples={observation.SampleCount}; validSamples={observation.ValidSampleCount}; invalidSamples={observation.InvalidSampleCount}; unresolvedSamples={observation.UnresolvedSampleCount}; firstNonValidReason={Bounded(observation.FirstNonValidReason)}; windowSeconds={EstablishmentObservationWindow.TotalSeconds:0.00}; shadowOnly=true; behaviorMutation=false");
            }
        }
    }

    internal static void ResetForRaidLifecycle(string? reason)
    {
        lock (Sync)
        {
            ByBotProfileId.Clear();
        }

    }

    private static ValiditySample EvaluateRetainedTacticalValidity(EstablishmentObservation observation, DateTimeOffset now)
    {
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(observation.BotProfileId, out VanguardRaidOperatorRuntimeRecord runtime)
            || runtime.BotOwner == null
            || runtime.BotOwner.IsDead)
        {
            return ValiditySample.Invalid("runtime_missing_or_dead", float.PositiveInfinity, false);
        }

        float distance = HorizontalDistance(runtime.BotOwner.Position, observation.Destination);
        bool withinArrivalEnvelope = distance <= observation.ArrivalEnvelopeMeters;
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(observation.BotProfileId, out OperatorDecisionSnapshot snapshot)
            || snapshot == null
            || !snapshot.Alive)
        {
            return ValiditySample.Invalid("snapshot_missing_or_dead", distance, withinArrivalEnvelope);
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(snapshot, now, out string freshnessReason))
        {
            return ValiditySample.Unresolved("snapshot_" + freshnessReason, distance, withinArrivalEnvelope);
        }
        if (!string.Equals(snapshot.OwnerProfileId, observation.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return ValiditySample.Invalid("owner_identity_changed", distance, withinArrivalEnvelope);
        }
        if (!string.Equals(NormalizeThreat(snapshot.Threat.EnemyId), NormalizeThreat(observation.ThreatId), StringComparison.OrdinalIgnoreCase))
        {
            return ValiditySample.Invalid("exact_threat_identity_changed", distance, withinArrivalEnvelope);
        }

        string localReason;
        bool localCompatible = string.Equals(observation.Kind, "BoundAdvance", StringComparison.OrdinalIgnoreCase)
            ? AltDevelopAuthorityPolicy.IsBoundAdvanceTranslationCompatible(snapshot, observation.ThreatId, out localReason)
            : AltDevelopAuthorityPolicy.IsTacticalOpportunityTranslationCompatible(snapshot, observation.ThreatId, out localReason);
        if (!localCompatible)
        {
            return ValiditySample.Invalid("local_authority_not_compatible:" + localReason, distance, withinArrivalEnvelope);
        }

        if (!TryValidateRequiredSupportContext(observation, now, runtime.BotOwner.Position, out bool supportKnown, out string supportReason))
        {
            return supportKnown
                ? ValiditySample.Invalid("support_context:" + supportReason, distance, withinArrivalEnvelope)
                : ValiditySample.Unresolved("support_context:" + supportReason, distance, withinArrivalEnvelope);
        }

        return ValiditySample.ValidSample("retained_exact_owner_threat_local_and_support_facts", distance, withinArrivalEnvelope);
    }

    private static bool TryValidateRequiredSupportContext(
        EstablishmentObservation observation,
        DateTimeOffset now,
        Vector3 moverPosition,
        out bool evidenceKnown,
        out string reason)
    {
        evidenceKnown = true;
        reason = "not_required";
        if (IsBoundOpportunityKind(observation.Kind))
        {
            return IsBoundSupportCurrent(observation, moverPosition, now, out reason);
        }
        if (string.Equals(observation.Kind, "ThresholdSupport", StringComparison.OrdinalIgnoreCase))
        {
            return IsThresholdSupportContextCurrent(observation, now, out reason);
        }
        if (string.Equals(observation.Kind, "AssaultRelay", StringComparison.OrdinalIgnoreCase))
        {
            return IsAssaultRelayContextCurrent(observation, now, out reason);
        }
        if (string.Equals(observation.Kind, "FlankSupport", StringComparison.OrdinalIgnoreCase))
        {
            return IsFlankSupportContextCurrent(observation, now, out evidenceKnown, out reason);
        }
        return true;
    }

    private static bool IsBoundSupportCurrent(EstablishmentObservation observation, Vector3 moverPosition, DateTimeOffset now, out string reason)
    {
        if (string.IsNullOrWhiteSpace(observation.SupportBotProfileId))
        {
            reason = "support_identity_missing";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(observation.SupportBotProfileId, out OperatorDecisionSnapshot support)
            || support == null
            || !support.Alive)
        {
            reason = "support_snapshot_missing_or_dead";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(support, now, out string freshnessReason))
        {
            reason = "support_" + freshnessReason;
            return false;
        }
        if (!string.Equals(support.OwnerProfileId, observation.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "support_owner_changed";
            return false;
        }
        if (!string.Equals(NormalizeThreat(support.Threat.EnemyId), NormalizeThreat(observation.ThreatId), StringComparison.OrdinalIgnoreCase))
        {
            reason = "support_threat_changed";
            return false;
        }
        if (HorizontalDistance(moverPosition, support.Position) > TacticalOpportunityMovementService.BoundSupportMaxSeparationMeters)
        {
            reason = "support_separation_exceeded";
            return false;
        }
        reason = "support_current";
        return true;
    }

    private static bool IsThresholdSupportContextCurrent(EstablishmentObservation observation, DateTimeOffset now, out string reason)
    {
        if (string.IsNullOrWhiteSpace(observation.SupportBotProfileId) || string.IsNullOrWhiteSpace(observation.SupportContextId))
        {
            reason = "source_breach_identity_missing";
            return false;
        }
        if (!DirectCommitMovementService.TryGetActiveBreachSupportContext(
                observation.OwnerProfileId,
                observation.ThreatId,
                now,
                out string leadBotProfileId,
                out string sourceLeaseId,
                out _,
                out _,
                out _,
                out string breachReason))
        {
            reason = "source_breach_not_current:" + breachReason;
            return false;
        }
        if (!string.Equals(leadBotProfileId, observation.SupportBotProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(sourceLeaseId, observation.SupportContextId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "source_breach_identity_changed";
            return false;
        }
        return HasCurrentDirectMembership(observation, out reason);
    }

    private static bool IsAssaultRelayContextCurrent(EstablishmentObservation observation, DateTimeOffset now, out string reason)
    {
        if (string.IsNullOrWhiteSpace(observation.SupportBotProfileId) || string.IsNullOrWhiteSpace(observation.SupportContextId))
        {
            reason = "source_commit_identity_missing";
            return false;
        }
        if (!DirectCommitMovementService.TryGetActiveRelayContext(
                observation.OwnerProfileId,
                observation.ThreatId,
                now,
                out string leadBotProfileId,
                out string sourceLeaseId,
                out _,
                out _,
                out string commitReason))
        {
            reason = "source_commit_not_current:" + commitReason;
            return false;
        }
        if (!string.Equals(leadBotProfileId, observation.SupportBotProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(sourceLeaseId, observation.SupportContextId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "source_commit_identity_changed";
            return false;
        }
        return HasCurrentDirectMembership(observation, out reason);
    }

    private static bool HasCurrentDirectMembership(EstablishmentObservation observation, out string reason)
    {
        if (!RouteAssignmentService.TryGetSnapshot(out RouteAssignmentSnapshot assignmentSnapshot)
            || !assignmentSnapshot.IsAvailable)
        {
            reason = "route_assignment_not_current";
            return false;
        }
        ThreatGroupRouteAssignment? assignment = assignmentSnapshot.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, observation.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, observation.ThreatId, StringComparison.OrdinalIgnoreCase));
        if (assignment == null
            || !assignment.DirectBotProfileIds.Contains(observation.SupportBotProfileId, StringComparer.OrdinalIgnoreCase)
            || !assignment.DirectBotProfileIds.Contains(observation.BotProfileId, StringComparer.OrdinalIgnoreCase))
        {
            reason = "direct_membership_changed";
            return false;
        }
        reason = "current_exact_source_and_direct_membership";
        return true;
    }

    private static bool IsFlankSupportContextCurrent(EstablishmentObservation observation, DateTimeOffset now, out bool evidenceKnown, out string reason)
    {
        evidenceKnown = true;
        if (string.IsNullOrWhiteSpace(observation.SupportContextId))
        {
            reason = "flank_lead_maneuver_identity_missing";
            return false;
        }
        if (!RouteAssignmentService.TryGetSnapshot(out RouteAssignmentSnapshot assignments) || !assignments.IsAvailable)
        {
            evidenceKnown = false;
            reason = "route_assignment_not_current";
            return false;
        }

        ThreatGroupRouteAssignment? assignment = assignments.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, observation.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, observation.ThreatId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.SelectedAltSupportBotProfileId, observation.BotProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.SelectedAltBotProfileId, observation.SupportBotProfileId, StringComparison.OrdinalIgnoreCase));
        if (assignment == null || !assignment.HasShadowAltSupportSelection)
        {
            reason = "assignment_or_support_identity_changed";
            return false;
        }

        bool currentManeuver = AltDevelopMovementService.TryGetCurrentDevelopmentManeuverProof(
            assignment,
            now,
            out string currentManeuverEpochId,
            out bool physicalArrivalObserved,
            out _);
        if (currentManeuver
            && !string.Equals(currentManeuverEpochId, observation.SupportContextId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "flank_lead_maneuver_epoch_changed";
            return false;
        }
        bool stableCompletion = !currentManeuver
            && AltDevelopMovementService.TryGetStableDevelopmentCompletionProof(
                assignment,
                observation.SupportContextId,
                now,
                out _);
        if (!currentManeuver && !stableCompletion)
        {
            reason = "flank_lead_exact_maneuver_not_current_or_completed";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(observation.SupportBotProfileId, out OperatorDecisionSnapshot lead)
            || lead == null
            || !lead.Alive
            || !VanguardOperatorDecisionSnapshotService.IsFresh(lead, now, out _)
            || !string.Equals(lead.OwnerProfileId, observation.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "flank_lead_snapshot_not_current";
            return false;
        }
        reason = stableCompletion
            ? "flank_lead_stable_completion_same_maneuver"
            : physicalArrivalObserved
                ? "flank_lead_current_maneuver_arrived"
                : "flank_lead_current_maneuver";
        return true;
    }

    private static void EmitPhase(EstablishmentObservation observation, string phase, DateTimeOffset now, string basis)
    {
        double ageSeconds = Math.Max(0d, (now - observation.MovingStartedAtUtc).TotalSeconds);
        double sinceArrivalSeconds = observation.ArrivedAtUtc == DateTimeOffset.MinValue
            ? 0d
            : Math.Max(0d, (now - observation.ArrivedAtUtc).TotalSeconds);
        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
            $"TC_TACTICAL_ESTABLISHMENT_SHADOW phase={phase}; basis={Bounded(basis)}; kind={Bounded(observation.Kind)}; owner={Bounded(observation.OwnerProfileId)}; threat={Bounded(observation.ThreatId)}; bot={Bounded(observation.BotProfileId)}; lease={Bounded(observation.LeaseId)}; supportBot={Bounded(observation.SupportBotProfileId)}; supportContext={Bounded(observation.SupportContextId)}; ageSeconds={ageSeconds:0.00}; sinceArrivalSeconds={sinceArrivalSeconds:0.00}; observationWindowSeconds={EstablishmentObservationWindow.TotalSeconds:0.00}; samples={observation.SampleCount}; validSamples={observation.ValidSampleCount}; invalidSamples={observation.InvalidSampleCount}; unresolvedSamples={observation.UnresolvedSampleCount}; lastDistanceToDestinationMeters={observation.LastDistanceToDestinationMeters:0.00}; shadowOnly=true; behaviorMutation=false; continuityMutation=false; targetMutation=false; aimMutation=false; fireCommand=false");
    }

    private static bool IsBoundOpportunityKind(string kind)
        => string.Equals(kind, "BoundAdvance", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "BoundRetreat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "BreakContact", StringComparison.OrdinalIgnoreCase);

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static string NormalizeThreat(string? value) => Normalize(value);
    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    private static string Bool(bool value) => value ? "true" : "false";

    private static string Bounded(string? value)
    {
        string safe = string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
        return safe.Length <= MaxDiagnosticFieldLength
            ? safe
            : safe.Substring(0, MaxDiagnosticFieldLength) + "~";
    }

    private sealed class EstablishmentObservation
    {
        public EstablishmentObservation(
            string ownerProfileId,
            string threatId,
            string botProfileId,
            string kind,
            string leaseId,
            string supportBotProfileId,
            string supportContextId,
            Vector3 destination,
            float arrivalEnvelopeMeters,
            DateTimeOffset movingStartedAtUtc)
        {
            OwnerProfileId = ownerProfileId;
            ThreatId = threatId;
            BotProfileId = botProfileId;
            Kind = kind;
            LeaseId = leaseId;
            SupportBotProfileId = supportBotProfileId;
            SupportContextId = supportContextId;
            Destination = destination;
            ArrivalEnvelopeMeters = arrivalEnvelopeMeters;
            MovingStartedAtUtc = movingStartedAtUtc;
            Phase = "MOVING";
            NextSampleAtUtc = DateTimeOffset.MaxValue;
        }

        public string OwnerProfileId { get; }
        public string ThreatId { get; }
        public string BotProfileId { get; }
        public string Kind { get; }
        public string LeaseId { get; }
        public string SupportBotProfileId { get; }
        public string SupportContextId { get; }
        public Vector3 Destination { get; }
        public float ArrivalEnvelopeMeters { get; }
        public DateTimeOffset MovingStartedAtUtc { get; }
        public string Phase { get; set; }
        public DateTimeOffset ArrivedAtUtc { get; set; }
        public DateTimeOffset ObservationUntilUtc { get; set; }
        public DateTimeOffset NextSampleAtUtc { get; set; }
        public int SampleCount { get; set; }
        public int ValidSampleCount { get; set; }
        public int InvalidSampleCount { get; set; }
        public int UnresolvedSampleCount { get; set; }
        public string? FirstNonValidReason { get; set; }
        public float LastDistanceToDestinationMeters { get; set; }
    }

    private readonly struct ValiditySample
    {
        private ValiditySample(bool known, bool valid, string reason, float distanceToDestinationMeters, bool withinArrivalEnvelope)
        {
            Known = known;
            Valid = valid;
            Reason = reason;
            DistanceToDestinationMeters = distanceToDestinationMeters;
            WithinArrivalEnvelope = withinArrivalEnvelope;
        }

        public bool Known { get; }
        public bool Valid { get; }
        public string Reason { get; }
        public float DistanceToDestinationMeters { get; }
        public bool WithinArrivalEnvelope { get; }

        public static ValiditySample ValidSample(string reason, float distance, bool withinArrivalEnvelope)
            => new(true, true, reason, distance, withinArrivalEnvelope);
        public static ValiditySample Invalid(string reason, float distance, bool withinArrivalEnvelope)
            => new(true, false, reason, distance, withinArrivalEnvelope);
        public static ValiditySample Unresolved(string reason, float distance, bool withinArrivalEnvelope)
            => new(false, false, reason, distance, withinArrivalEnvelope);
    }
}
#endif
