#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Decision;

// Responsibility: observes whether an already-authorized Tactical Opportunity physically develops into unsupported forward penetration while support and exposure evidence degrade.
// Flow: the canonical started lease establishes an immutable baseline, bounded active samples reuse fresh Awareness threat geometry plus the existing exposure LOS budget and current same-owner/same-threat squad snapshots, and lease end publishes a raw trajectory summary.
// Authority boundary: shadow diagnostics only. This service cannot reject a lease, alter a path, request a replan, choose Hold/Cover/Regroup, mutate target/aim/fire, or change SAIN state.
// Invariant: all trend evidence is directional/raw with no promoted distance, exposure or support threshold; missing evidence remains unresolved and can never constrain gameplay.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class UnsupportedPenetrationShadowService
{
    public const string StatusTag = "VANGUARD_TC_UNSUPPORTED_PENETRATION_SHADOW_STATUS";
    private const int MaxDiagnosticFieldLength = 160;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(0.50d);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, ObservationState> ByLeaseId = new(StringComparer.OrdinalIgnoreCase);

    private sealed class ObservationState
    {
        public ObservationState(
            string ownerProfileId,
            string threatId,
            string botProfileId,
            string kind,
            string leaseId,
            string supportBotProfileId,
            string supportContextId,
            Vector3 destination,
            DateTimeOffset startedAtUtc)
        {
            OwnerProfileId = ownerProfileId;
            ThreatId = threatId;
            BotProfileId = botProfileId;
            Kind = kind;
            LeaseId = leaseId;
            SupportBotProfileId = supportBotProfileId;
            SupportContextId = supportContextId;
            Destination = destination;
            StartedAtUtc = startedAtUtc;
            NextSampleAtUtc = startedAtUtc + SampleInterval;
        }

        public string OwnerProfileId { get; }
        public string ThreatId { get; }
        public string BotProfileId { get; }
        public string Kind { get; }
        public string LeaseId { get; }
        public string SupportBotProfileId { get; }
        public string SupportContextId { get; }
        public Vector3 Destination { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public DateTimeOffset NextSampleAtUtc { get; set; }
        public bool BaselineAvailable { get; set; }
        public Vector3 InitialMoverPosition { get; set; }
        public Vector3 InitialThreatPosition { get; set; }
        public Vector3 InitialForwardDirection { get; set; }
        public float InitialMoverThreatDistanceMeters { get; set; } = float.NaN;
        public float InitialDestinationThreatDistanceMeters { get; set; } = float.NaN;
        public bool IntendedForwardDestination { get; set; }
        public int PreviousCredibleSupportCount { get; set; }
        public float PreviousNearestSupportMeters { get; set; } = float.NaN;
        public bool PreviousExplicitSupportAvailable { get; set; }
        public float PreviousMoverThreatDistanceMeters { get; set; } = float.NaN;
        public bool PreviousExposureAvailable { get; set; }
        public bool PreviousLosClear { get; set; }
        public float PreviousFacingDot { get; set; } = float.NaN;
        public int Samples { get; set; }
        public int UnresolvedSamples { get; set; }
        public int CandidateSamples { get; set; }
        public bool ForwardPenetrationObserved { get; set; }
        public bool ClosingThreatObserved { get; set; }
        public bool SupportDegradationObserved { get; set; }
        public bool ExposureIncreaseObserved { get; set; }
        public bool NoCredibleSupportObserved { get; set; }
        public bool CandidateObserved { get; set; }
        public float MaxForwardPenetrationMeters { get; set; }
        public float MinimumMoverThreatDistanceMeters { get; set; } = float.NaN;
        public int MinimumCredibleSupportCount { get; set; } = int.MaxValue;
        public float MaximumNearestSupportMeters { get; set; } = float.NaN;
    }

    private readonly struct SampleEvidence
    {
        public SampleEvidence(
            bool threatAvailable,
            Vector3 threatPosition,
            float moverThreatDistanceMeters,
            int credibleSupportCount,
            float nearestSupportMeters,
            bool explicitSupportAvailable,
            bool exposureObservationCompleted,
            bool exposureSampleAvailable,
            bool lineOfSightClear,
            float facingDot,
            float facingAngleDegrees,
            string reason)
        {
            ThreatAvailable = threatAvailable;
            ThreatPosition = threatPosition;
            MoverThreatDistanceMeters = moverThreatDistanceMeters;
            CredibleSupportCount = credibleSupportCount;
            NearestSupportMeters = nearestSupportMeters;
            ExplicitSupportAvailable = explicitSupportAvailable;
            ExposureObservationCompleted = exposureObservationCompleted;
            ExposureSampleAvailable = exposureSampleAvailable;
            LineOfSightClear = lineOfSightClear;
            FacingDot = facingDot;
            FacingAngleDegrees = facingAngleDegrees;
            Reason = reason;
        }

        public bool ThreatAvailable { get; }
        public Vector3 ThreatPosition { get; }
        public float MoverThreatDistanceMeters { get; }
        public int CredibleSupportCount { get; }
        public float NearestSupportMeters { get; }
        public bool ExplicitSupportAvailable { get; }
        public bool ExposureObservationCompleted { get; }
        public bool ExposureSampleAvailable { get; }
        public bool LineOfSightClear { get; }
        public float FacingDot { get; }
        public float FacingAngleDegrees { get; }
        public string Reason { get; }
    }

    internal static void TrackStartedLease(
        OperatorDecisionSnapshot? snapshot,
        string? kind,
        string? leaseId,
        Vector3 destination,
        string? supportBotProfileId,
        string? supportContextId,
        DateTimeOffset now)
    {
        if (snapshot == null || !snapshot.Alive)
        {
            return;
        }

        string owner = Normalize(snapshot.OwnerProfileId);
        string threat = Normalize(snapshot.Threat?.EnemyId);
        string bot = Normalize(snapshot.BotProfileId);
        string lease = Normalize(leaseId);
        if (owner.Length == 0 || threat.Length == 0 || bot.Length == 0 || lease.Length == 0)
        {
            return;
        }

        var state = new ObservationState(
            owner,
            threat,
            bot,
            Normalize(kind),
            lease,
            Normalize(supportBotProfileId),
            Normalize(supportContextId),
            destination,
            now);

        SampleEvidence baseline = CaptureSample(snapshot, state.SupportBotProfileId, now);
        if (baseline.ThreatAvailable)
        {
            Vector3 forward = Flatten(baseline.ThreatPosition - snapshot.Position);
            state.BaselineAvailable = forward.sqrMagnitude > 0.0001f;
            state.InitialMoverPosition = snapshot.Position;
            state.InitialThreatPosition = baseline.ThreatPosition;
            state.InitialForwardDirection = state.BaselineAvailable ? forward.normalized : Vector3.zero;
            state.InitialMoverThreatDistanceMeters = baseline.MoverThreatDistanceMeters;
            state.InitialDestinationThreatDistanceMeters = HorizontalDistance(destination, baseline.ThreatPosition);
            state.IntendedForwardDestination = state.BaselineAvailable
                && state.InitialDestinationThreatDistanceMeters < state.InitialMoverThreatDistanceMeters;
            state.PreviousMoverThreatDistanceMeters = baseline.MoverThreatDistanceMeters;
            state.MinimumMoverThreatDistanceMeters = baseline.MoverThreatDistanceMeters;
        }

        state.PreviousCredibleSupportCount = baseline.CredibleSupportCount;
        state.PreviousNearestSupportMeters = baseline.NearestSupportMeters;
        state.PreviousExplicitSupportAvailable = baseline.ExplicitSupportAvailable;
        state.MinimumCredibleSupportCount = baseline.CredibleSupportCount;
        state.MaximumNearestSupportMeters = baseline.NearestSupportMeters;
        state.PreviousExposureAvailable = baseline.ExposureObservationCompleted && baseline.ExposureSampleAvailable;
        state.PreviousLosClear = baseline.LineOfSightClear;
        state.PreviousFacingDot = baseline.FacingDot;
        state.Samples = 1;
        if (!baseline.ThreatAvailable || !baseline.ExposureObservationCompleted || !baseline.ExposureSampleAvailable)
        {
            state.UnresolvedSamples++;
        }

        lock (Sync)
        {
            ByLeaseId[lease] = state;
        }

        EmitStart(state, baseline, now);
    }

    internal static void ObserveActiveLease(
        OperatorDecisionSnapshot? snapshot,
        string? kind,
        string? leaseId,
        Vector3 destination,
        string? supportBotProfileId,
        string? supportContextId,
        DateTimeOffset now)
    {
        string lease = Normalize(leaseId);
        if (snapshot == null || !snapshot.Alive || lease.Length == 0)
        {
            return;
        }

        ObservationState? state;
        lock (Sync)
        {
            ByLeaseId.TryGetValue(lease, out state);
            if (state == null || now < state.NextSampleAtUtc)
            {
                return;
            }
            state.NextSampleAtUtc = now + SampleInterval;
        }

        if (!string.Equals(state.BotProfileId, Normalize(snapshot.BotProfileId), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(state.OwnerProfileId, Normalize(snapshot.OwnerProfileId), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(state.ThreatId, Normalize(snapshot.Threat?.EnemyId), StringComparison.OrdinalIgnoreCase))
        {
            lock (Sync)
            {
                state.Samples++;
                state.UnresolvedSamples++;
            }
            EmitSample(state, snapshot, default, 0f, false, false, false, false, false, false, "UNRESOLVED_EXACT_OWNER_THREAT_IDENTITY_CHANGED", now);
            return;
        }

        SampleEvidence current = CaptureSample(snapshot, state.SupportBotProfileId, now);
        if (!current.ThreatAvailable)
        {
            lock (Sync)
            {
                state.Samples++;
                state.UnresolvedSamples++;
            }
            EmitSample(state, snapshot, current, 0f, false, false, false, current.CredibleSupportCount == 0, false, false, current.Reason, now);
            return;
        }

        float forwardPenetrationMeters = state.BaselineAvailable
            ? Vector3.Dot(Flatten(snapshot.Position - state.InitialMoverPosition), state.InitialForwardDirection)
            : 0f;
        bool forwardPenetration = state.BaselineAvailable
            && state.IntendedForwardDestination
            && forwardPenetrationMeters > 0f;
        bool closingThreat = IsFinite(state.PreviousMoverThreatDistanceMeters)
            && current.MoverThreatDistanceMeters < state.PreviousMoverThreatDistanceMeters;

        bool supportCountDropped = current.CredibleSupportCount < state.PreviousCredibleSupportCount;
        bool supportSeparationIncreased = current.CredibleSupportCount > 0
            && state.PreviousCredibleSupportCount > 0
            && IsFinite(current.NearestSupportMeters)
            && IsFinite(state.PreviousNearestSupportMeters)
            && current.NearestSupportMeters > state.PreviousNearestSupportMeters;
        bool explicitSupportLost = state.SupportBotProfileId.Length > 0
            && state.PreviousExplicitSupportAvailable
            && !current.ExplicitSupportAvailable;
        bool supportDegrading = supportCountDropped || supportSeparationIncreased || explicitSupportLost;
        bool noCredibleSupport = current.CredibleSupportCount == 0;

        bool currentExposureAvailable = current.ExposureObservationCompleted && current.ExposureSampleAvailable;
        bool exposureIncreasing = state.PreviousExposureAvailable
            && currentExposureAvailable
            && ((!state.PreviousLosClear && current.LineOfSightClear)
                || (state.PreviousLosClear
                    && current.LineOfSightClear
                    && IsFinite(state.PreviousFacingDot)
                    && IsFinite(current.FacingDot)
                    && current.FacingDot > state.PreviousFacingDot));

        lock (Sync)
        {
            state.Samples++;
            if (!current.ExposureObservationCompleted || !current.ExposureSampleAvailable)
            {
                state.UnresolvedSamples++;
            }
            state.ForwardPenetrationObserved |= forwardPenetration;
            state.ClosingThreatObserved |= closingThreat;
            state.SupportDegradationObserved |= supportDegrading;
            state.ExposureIncreaseObserved |= exposureIncreasing;
            state.NoCredibleSupportObserved |= noCredibleSupport;
            state.MaxForwardPenetrationMeters = Math.Max(state.MaxForwardPenetrationMeters, forwardPenetrationMeters);
            state.MinimumMoverThreatDistanceMeters = MinFinite(state.MinimumMoverThreatDistanceMeters, current.MoverThreatDistanceMeters);
            state.MinimumCredibleSupportCount = Math.Min(state.MinimumCredibleSupportCount, current.CredibleSupportCount);
            state.MaximumNearestSupportMeters = MaxFinite(state.MaximumNearestSupportMeters, current.NearestSupportMeters);

            bool unsupportedCandidate = state.IntendedForwardDestination
                && state.ForwardPenetrationObserved
                && state.ClosingThreatObserved
                && state.SupportDegradationObserved
                && state.ExposureIncreaseObserved
                && noCredibleSupport;
            if (unsupportedCandidate)
            {
                state.CandidateObserved = true;
                state.CandidateSamples++;
            }

            state.PreviousMoverThreatDistanceMeters = current.MoverThreatDistanceMeters;
            state.PreviousCredibleSupportCount = current.CredibleSupportCount;
            state.PreviousNearestSupportMeters = current.NearestSupportMeters;
            state.PreviousExplicitSupportAvailable = current.ExplicitSupportAvailable;
            state.PreviousExposureAvailable = currentExposureAvailable;
            state.PreviousLosClear = current.LineOfSightClear;
            state.PreviousFacingDot = current.FacingDot;

            EmitSample(
                state,
                snapshot,
                current,
                forwardPenetrationMeters,
                forwardPenetration,
                closingThreat,
                supportDegrading,
                noCredibleSupport,
                exposureIncreasing,
                unsupportedCandidate,
                current.ExposureObservationCompleted && current.ExposureSampleAvailable ? "RAW_EVIDENCE_AVAILABLE" : current.Reason,
                now);
        }
    }

    internal static void ObserveEnd(
        string? botProfileId,
        string? leaseId,
        string? outcome,
        string? reason,
        DateTimeOffset now)
    {
        string lease = Normalize(leaseId);
        if (lease.Length == 0)
        {
            return;
        }

        ObservationState? state;
        lock (Sync)
        {
            if (!ByLeaseId.TryGetValue(lease, out state))
            {
                return;
            }
            ByLeaseId.Remove(lease);
        }

        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
            $"TC_UNSUPPORTED_PENETRATION_SUMMARY kind={Bounded(state.Kind)}; owner={Bounded(state.OwnerProfileId)}; threat={Bounded(state.ThreatId)}; bot={Bounded(botProfileId)}; lease={Bounded(state.LeaseId)}; outcome={Bounded(outcome)}; reason={Bounded(reason)}; ageSeconds={Number(Math.Max(0d, (now - state.StartedAtUtc).TotalSeconds))}; samples={state.Samples}; unresolvedSamples={state.UnresolvedSamples}; intendedForwardDestination={Bool(state.IntendedForwardDestination)}; initialMoverThreatDistanceMeters={Number(state.InitialMoverThreatDistanceMeters)}; initialDestinationThreatDistanceMeters={Number(state.InitialDestinationThreatDistanceMeters)}; minimumMoverThreatDistanceMeters={Number(state.MinimumMoverThreatDistanceMeters)}; maxForwardPenetrationMeters={Number(state.MaxForwardPenetrationMeters)}; minimumCredibleSupportCount={(state.MinimumCredibleSupportCount == int.MaxValue ? "unknown" : state.MinimumCredibleSupportCount.ToString(CultureInfo.InvariantCulture))}; maximumNearestSupportMeters={Number(state.MaximumNearestSupportMeters)}; forwardPenetrationObserved={Bool(state.ForwardPenetrationObserved)}; closingThreatObserved={Bool(state.ClosingThreatObserved)}; supportDegradationObserved={Bool(state.SupportDegradationObserved)}; exposureIncreaseObserved={Bool(state.ExposureIncreaseObserved)}; noCredibleSupportObserved={Bool(state.NoCredibleSupportObserved)}; unsupportedForwardPenetrationCandidate={Bool(state.CandidateObserved)}; candidateSamples={state.CandidateSamples}; classification={(state.CandidateObserved ? "UNSUPPORTED_FORWARD_PENETRATION_CANDIDATE" : "NOT_OBSERVED_OR_UNRESOLVED")}; promotedThreshold=false; shadowOnly=true; executionAllowed=false; movementMutation=false; routeMutation=false; leaseMutation=false; replanMutation=false; targetMutation=false; aimMutation=false; fireCommand=false; sainMutation=false");
    }

    internal static void ResetForRaidLifecycle(string reason)
    {
        int cleared;
        lock (Sync)
        {
            cleared = ByLeaseId.Count;
            ByLeaseId.Clear();
        }

        // Non-authority Tick() may invoke lifecycle reset continuously. When there is no
        // shadow state to clear, keep that fail-closed behavior silent so Diagnostic logs
        // do not grow by one identical reset line per tick. A real stale-state cleanup
        // (cleared > 0) remains observable, as do explicit raid lifecycle resets.
        if (cleared == 0 && string.Equals(reason, "not_raid_authority", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
            $"TC_UNSUPPORTED_PENETRATION_RESET reason={Bounded(reason)}; cleared={cleared}; shadowOnly=true; executionAllowed=false; movementMutation=false; targetMutation=false; aimMutation=false; fireCommand=false; sainMutation=false");
    }

    private static SampleEvidence CaptureSample(OperatorDecisionSnapshot snapshot, string explicitSupportBotProfileId, DateTimeOffset now)
    {
        string owner = Normalize(snapshot.OwnerProfileId);
        string threatId = Normalize(snapshot.Threat?.EnemyId);
        IReadOnlyList<VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation> qualified =
            VanguardCombatAwarenessBridge.GetFreshQualifiedTacticalCoordinationThreats(now);
        if (!TryFindFreshQualified(qualified, owner, threatId, out VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation threat))
        {
            ResolveCredibleSupport(snapshot, threatId, explicitSupportBotProfileId, now, out int supportCount, out float nearestSupport, out bool explicitSupportAvailable);
            return new SampleEvidence(false, default, float.NaN, supportCount, nearestSupport, explicitSupportAvailable, false, false, false, float.NaN, float.NaN, "UNRESOLVED_EXACT_AWARENESS_THREAT_MISSING");
        }

        ResolveCredibleSupport(snapshot, threatId, explicitSupportBotProfileId, now, out int credibleSupportCount, out float nearestSupportMeters, out bool explicitSupportIsAvailable);

        int linecasts = 0;
        bool observationCompleted = ThreatExposureObservationService.TryObserveExistingPoint(
            owner,
            snapshot.Position,
            threat.RuntimePosition,
            threat.RuntimeLookDirection,
            ref linecasts,
            out bool sampleAvailable,
            out bool lineOfSightClear,
            out float facingDot,
            out float facingAngleDegrees,
            out _);

        string reason = !observationCompleted
            ? "UNRESOLVED_EXPOSURE_OBSERVATION_BUDGET_DEFERRED"
            : !sampleAvailable
                ? "UNRESOLVED_EXPOSURE_GEOMETRY"
                : "RAW_EVIDENCE_AVAILABLE";

        return new SampleEvidence(
            true,
            threat.RuntimePosition,
            HorizontalDistance(snapshot.Position, threat.RuntimePosition),
            credibleSupportCount,
            nearestSupportMeters,
            explicitSupportIsAvailable,
            observationCompleted,
            sampleAvailable,
            lineOfSightClear,
            facingDot,
            facingAngleDegrees,
            reason);
    }

    private static void ResolveCredibleSupport(
        OperatorDecisionSnapshot mover,
        string threatId,
        string explicitSupportBotProfileId,
        DateTimeOffset now,
        out int count,
        out float nearestSupportMeters,
        out bool explicitSupportAvailable)
    {
        count = 0;
        nearestSupportMeters = float.NaN;
        explicitSupportAvailable = false;
        IReadOnlyList<OperatorDecisionSnapshot> snapshots = VanguardOperatorDecisionSnapshotService.GetLatestSnapshots();
        for (int index = 0; index < snapshots.Count; index++)
        {
            OperatorDecisionSnapshot candidate = snapshots[index];
            if (candidate == null
                || !candidate.Alive
                || string.Equals(candidate.BotProfileId, mover.BotProfileId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Normalize(candidate.OwnerProfileId), Normalize(mover.OwnerProfileId), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Normalize(candidate.Threat?.EnemyId), threatId, StringComparison.OrdinalIgnoreCase)
                || !VanguardOperatorDecisionSnapshotService.IsFresh(candidate, now, out _)
                || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(candidate.BotProfileId, out VanguardRaidOperatorRuntimeRecord runtime)
                || runtime.BotOwner == null
                || runtime.BotOwner.IsDead)
            {
                continue;
            }

            count++;
            float separation = HorizontalDistance(mover.Position, runtime.BotOwner.Position);
            nearestSupportMeters = MinFinite(nearestSupportMeters, separation);
            if (explicitSupportBotProfileId.Length > 0
                && string.Equals(candidate.BotProfileId, explicitSupportBotProfileId, StringComparison.OrdinalIgnoreCase))
            {
                explicitSupportAvailable = true;
            }
        }
    }

    private static void EmitStart(ObservationState state, SampleEvidence baseline, DateTimeOffset now)
    {
        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
            $"TC_UNSUPPORTED_PENETRATION_STARTED kind={Bounded(state.Kind)}; owner={Bounded(state.OwnerProfileId)}; threat={Bounded(state.ThreatId)}; bot={Bounded(state.BotProfileId)}; lease={Bounded(state.LeaseId)}; destination={Vector(state.Destination)}; baselineAvailable={Bool(state.BaselineAvailable)}; intendedForwardDestination={Bool(state.IntendedForwardDestination)}; moverThreatDistanceMeters={Number(state.InitialMoverThreatDistanceMeters)}; destinationThreatDistanceMeters={Number(state.InitialDestinationThreatDistanceMeters)}; credibleSupportCount={baseline.CredibleSupportCount}; nearestSupportMeters={Number(baseline.NearestSupportMeters)}; explicitSupportBot={Bounded(state.SupportBotProfileId)}; explicitSupportAvailable={Bool(baseline.ExplicitSupportAvailable)}; supportContext={Bounded(state.SupportContextId)}; exposureSampleAvailable={Bool(baseline.ExposureObservationCompleted && baseline.ExposureSampleAvailable)}; los={(baseline.ExposureSampleAvailable ? Bool(baseline.LineOfSightClear) : "unknown")}; facingDot={Number(baseline.FacingDot)}; facingAngleDegrees={Number(baseline.FacingAngleDegrees)}; evidence={Bounded(baseline.Reason)}; sampleIntervalSeconds={SampleInterval.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)}; trendThreshold=directional_only_zero_not_promoted; shadowOnly=true; executionAllowed=false; movementMutation=false; targetMutation=false; aimMutation=false; fireCommand=false; sainMutation=false");
    }

    private static void EmitSample(
        ObservationState state,
        OperatorDecisionSnapshot snapshot,
        SampleEvidence current,
        float forwardPenetrationMeters,
        bool forwardPenetration,
        bool closingThreat,
        bool supportDegrading,
        bool noCredibleSupport,
        bool exposureIncreasing,
        bool unsupportedCandidate,
        string evidence,
        DateTimeOffset now)
    {
        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
            $"TC_UNSUPPORTED_PENETRATION_SAMPLE kind={Bounded(state.Kind)}; owner={Bounded(state.OwnerProfileId)}; threat={Bounded(state.ThreatId)}; bot={Bounded(snapshot.BotProfileId)}; lease={Bounded(state.LeaseId)}; ageSeconds={Number(Math.Max(0d, (now - state.StartedAtUtc).TotalSeconds))}; moverThreatDistanceMeters={Number(current.MoverThreatDistanceMeters)}; forwardPenetrationMeters={Number(forwardPenetrationMeters)}; intendedForwardDestination={Bool(state.IntendedForwardDestination)}; forwardPenetration={Bool(forwardPenetration)}; closingThreat={Bool(closingThreat)}; credibleSupportCount={current.CredibleSupportCount}; nearestSupportMeters={Number(current.NearestSupportMeters)}; explicitSupportAvailable={Bool(current.ExplicitSupportAvailable)}; supportDegrading={Bool(supportDegrading)}; noCredibleSupport={Bool(noCredibleSupport)}; exposureSampleAvailable={Bool(current.ExposureObservationCompleted && current.ExposureSampleAvailable)}; los={(current.ExposureSampleAvailable ? Bool(current.LineOfSightClear) : "unknown")}; facingDot={Number(current.FacingDot)}; facingAngleDegrees={Number(current.FacingAngleDegrees)}; exposureIncreasing={Bool(exposureIncreasing)}; supportDegradationObserved={Bool(state.SupportDegradationObserved)}; exposureIncreaseObserved={Bool(state.ExposureIncreaseObserved)}; unsupportedForwardPenetrationCandidate={Bool(unsupportedCandidate)}; classification={(unsupportedCandidate ? "UNSUPPORTED_FORWARD_PENETRATION_CANDIDATE" : "RAW_TRAJECTORY_ONLY")}; evidence={Bounded(evidence)}; promotedThreshold=false; shadowOnly=true; executionAllowed=false; holdMutation=false; coverMutation=false; regroupMutation=false; movementMutation=false; routeMutation=false; leaseMutation=false; replanMutation=false; targetMutation=false; aimMutation=false; fireCommand=false; sainMutation=false");
    }

    private static bool TryFindFreshQualified(
        IReadOnlyList<VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation> observations,
        string ownerProfileId,
        string targetProfileId,
        out VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation observation)
    {
        for (int index = 0; index < observations.Count; index++)
        {
            VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation current = observations[index];
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

    private static Vector3 Flatten(Vector3 value)
    {
        value.y = 0f;
        return value;
    }

    private static float HorizontalDistance(Vector3 first, Vector3 second)
    {
        float dx = first.x - second.x;
        float dz = first.z - second.z;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    private static float MinFinite(float current, float candidate)
        => !IsFinite(candidate) ? current : !IsFinite(current) ? candidate : Math.Min(current, candidate);

    private static float MaxFinite(float current, float candidate)
        => !IsFinite(candidate) ? current : !IsFinite(current) ? candidate : Math.Max(current, candidate);

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static string Normalize(string? value)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        return string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "null", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "unknown", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : normalized;
    }

    private static string Bounded(string? value)
    {
        string safe = Normalize(value).Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
        if (safe.Length == 0)
        {
            return "none";
        }
        return safe.Length <= MaxDiagnosticFieldLength ? safe : safe.Substring(0, MaxDiagnosticFieldLength);
    }

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Number(float value) => IsFinite(value) ? value.ToString("0.00", CultureInfo.InvariantCulture) : "unknown";
    private static string Number(double value) => !double.IsNaN(value) && !double.IsInfinity(value) ? value.ToString("0.00", CultureInfo.InvariantCulture) : "unknown";
    private static string Vector(Vector3 value) => Number(value.x) + "," + Number(value.y) + "," + Number(value.z);
}
#endif
