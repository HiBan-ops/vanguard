#if SPT_CLIENT
using System;
using System.Collections.Generic;
using UnityEngine;

// Responsibility: Defines immutable Route convergence shared-threat/direct-path convergence observations derived from the already-qualified assessment snapshot.
// Flow: One generation-bound squad analysis groups Operators by owner/shared threat, records bounded direct-route geometry, compares final approach overlap, and publishes a non-executable convergence snapshot.
// Authority boundary: convergence is observation only. These models cannot create intents, choose destinations, acquire leases, mutate SAIN/BigBrain, or persist Tactical Coordination data.
// Invariant: Every published convergence snapshot is bound to one exact assessment input generation; incomplete/deferred path work is never exposed as current tactical truth.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal sealed class RouteConvergenceSnapshot
{
    public static readonly RouteConvergenceSnapshot Empty = new(
        string.Empty,
        0,
        string.Empty,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        Array.Empty<ThreatGroupConvergenceAssessment>());

    public RouteConvergenceSnapshot(
        string mapId,
        long topologyRevision,
        string topologyContentHash,
        DateTimeOffset operatorBatchAtUtc,
        DateTimeOffset assessmentPublishedAtUtc,
        DateTimeOffset publishedAtUtc,
        IReadOnlyList<ThreatGroupConvergenceAssessment> threatGroups)
    {
        MapId = mapId;
        TopologyRevision = topologyRevision;
        TopologyContentHash = topologyContentHash;
        OperatorBatchAtUtc = operatorBatchAtUtc;
        AssessmentPublishedAtUtc = assessmentPublishedAtUtc;
        PublishedAtUtc = publishedAtUtc;
        ThreatGroups = threatGroups;
    }

    public string MapId { get; }
    public long TopologyRevision { get; }
    public string TopologyContentHash { get; }
    public DateTimeOffset OperatorBatchAtUtc { get; }
    public DateTimeOffset AssessmentPublishedAtUtc { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public IReadOnlyList<ThreatGroupConvergenceAssessment> ThreatGroups { get; }
    public bool IsAvailable => TopologyRevision > 0 && !string.IsNullOrWhiteSpace(MapId);
    public bool ExecutionAllowed => false;
}

internal sealed class ThreatGroupConvergenceAssessment
{
    public ThreatGroupConvergenceAssessment(
        string ownerProfileId,
        string threatId,
        Vector3 threatAnchorPosition,
        string threatAnchorSource,
        string threatAnchorConfidence,
        int memberCount,
        IReadOnlyList<DirectRouteObservation> routes,
        IReadOnlyList<PairConvergenceObservation> pairs,
        string classification)
    {
        OwnerProfileId = ownerProfileId;
        ThreatId = threatId;
        ThreatAnchorPosition = threatAnchorPosition;
        ThreatAnchorSource = threatAnchorSource;
        ThreatAnchorConfidence = threatAnchorConfidence;
        MemberCount = memberCount;
        Routes = routes;
        Pairs = pairs;
        Classification = classification;
    }

    public string OwnerProfileId { get; }
    public string ThreatId { get; }
    public Vector3 ThreatAnchorPosition { get; }
    public string ThreatAnchorSource { get; }
    public string ThreatAnchorConfidence { get; }
    public int MemberCount { get; }
    public IReadOnlyList<DirectRouteObservation> Routes { get; }
    public IReadOnlyList<PairConvergenceObservation> Pairs { get; }
    public string Classification { get; }
    public bool ExecutionAllowed => false;
}

internal sealed class DirectRouteObservation
{
    public DirectRouteObservation(
        string operatorId,
        string botProfileId,
        DateTimeOffset evidenceCapturedAtUtc,
        Vector3 startPosition,
        Vector3 targetPosition,
        string pathStatus,
        float pathLengthMeters,
        IReadOnlyList<Vector3> corners,
        bool cacheHit)
    {
        OperatorId = operatorId;
        BotProfileId = botProfileId;
        EvidenceCapturedAtUtc = evidenceCapturedAtUtc;
        StartPosition = startPosition;
        TargetPosition = targetPosition;
        PathStatus = pathStatus;
        PathLengthMeters = pathLengthMeters;
        Corners = corners;
        CacheHit = cacheHit;
    }

    public string OperatorId { get; }
    public string BotProfileId { get; }
    public DateTimeOffset EvidenceCapturedAtUtc { get; }
    public Vector3 StartPosition { get; }
    public Vector3 TargetPosition { get; }
    public string PathStatus { get; }
    public float PathLengthMeters { get; }
    public IReadOnlyList<Vector3> Corners { get; }
    public bool CacheHit { get; }
    public bool HasUsableGeometry => Corners.Count >= 2 && !string.Equals(PathStatus, "invalid", StringComparison.OrdinalIgnoreCase);
    public bool ExecutionAllowed => false;
}

internal sealed class PairConvergenceObservation
{
    public PairConvergenceObservation(
        string firstBotProfileId,
        string secondBotProfileId,
        int sampleCount,
        float? meanFinalApproachSeparationMeters,
        float? maxFinalApproachSeparationMeters,
        float? finalApproachAngleDegrees,
        string classification)
    {
        FirstBotProfileId = firstBotProfileId;
        SecondBotProfileId = secondBotProfileId;
        SampleCount = sampleCount;
        MeanFinalApproachSeparationMeters = meanFinalApproachSeparationMeters;
        MaxFinalApproachSeparationMeters = maxFinalApproachSeparationMeters;
        FinalApproachAngleDegrees = finalApproachAngleDegrees;
        Classification = classification;
    }

    public string FirstBotProfileId { get; }
    public string SecondBotProfileId { get; }
    public int SampleCount { get; }
    public float? MeanFinalApproachSeparationMeters { get; }
    public float? MaxFinalApproachSeparationMeters { get; }
    public float? FinalApproachAngleDegrees { get; }
    public string Classification { get; }
    public bool ExecutionAllowed => false;
}
#endif
