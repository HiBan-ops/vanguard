#if SPT_CLIENT
using System;
using System.Collections.Generic;
using UnityEngine;

// Responsibility: immutable observation-only evidence describing how existing TC routes relate to the exact runtime pose of an already-qualified Vanguard threat.
// Flow: ThreatExposureObservationService combines coherent RouteConvergence/RouteIndependence publications with the Awareness read-only runtime projection and publishes raw angle/LOS/distance measurements.
// Authority boundary: Data only. No exposure band, HeldAccess, KillLane, route preference, assignment, replan request, movement lease or SAIN mutation is encoded here.
// Invariant: Raw measurements remain bound to one owner/threat and one exact convergence/independence publication pair; absence of evidence never becomes tactical safety.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal sealed class ThreatExposureObservationSnapshot
{
    public static readonly ThreatExposureObservationSnapshot Empty = new(
        string.Empty,
        0,
        string.Empty,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        Array.Empty<ThreatGroupExposureObservation>());

    public ThreatExposureObservationSnapshot(
        string mapId,
        long topologyRevision,
        string topologyContentHash,
        DateTimeOffset operatorBatchAtUtc,
        DateTimeOffset convergencePublishedAtUtc,
        DateTimeOffset independencePublishedAtUtc,
        DateTimeOffset publishedAtUtc,
        IReadOnlyList<ThreatGroupExposureObservation> threatGroups)
    {
        MapId = mapId;
        TopologyRevision = topologyRevision;
        TopologyContentHash = topologyContentHash;
        OperatorBatchAtUtc = operatorBatchAtUtc;
        ConvergencePublishedAtUtc = convergencePublishedAtUtc;
        IndependencePublishedAtUtc = independencePublishedAtUtc;
        PublishedAtUtc = publishedAtUtc;
        ThreatGroups = threatGroups;
    }

    public string MapId { get; }
    public long TopologyRevision { get; }
    public string TopologyContentHash { get; }
    public DateTimeOffset OperatorBatchAtUtc { get; }
    public DateTimeOffset ConvergencePublishedAtUtc { get; }
    public DateTimeOffset IndependencePublishedAtUtc { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public IReadOnlyList<ThreatGroupExposureObservation> ThreatGroups { get; }
    public bool IsAvailable => TopologyRevision > 0 && !string.IsNullOrWhiteSpace(MapId);
    public bool ExecutionAllowed => false;
}

internal sealed class ThreatGroupExposureObservation
{
    public ThreatGroupExposureObservation(
        string ownerProfileId,
        string threatId,
        string contactKind,
        DateTimeOffset contactObservedAtUtc,
        DateTimeOffset contactExpiresAtUtc,
        Vector3 runtimeThreatPosition,
        Vector3 runtimeThreatLookDirection,
        Vector3 historicalThreatAnchorPosition,
        float anchorRuntimeDeltaMeters,
        IReadOnlyList<RouteExposureObservation> directRoutes,
        IReadOnlyList<RouteExposureObservation> alternateAxes)
    {
        OwnerProfileId = ownerProfileId;
        ThreatId = threatId;
        ContactKind = contactKind;
        ContactObservedAtUtc = contactObservedAtUtc;
        ContactExpiresAtUtc = contactExpiresAtUtc;
        RuntimeThreatPosition = runtimeThreatPosition;
        RuntimeThreatLookDirection = runtimeThreatLookDirection;
        HistoricalThreatAnchorPosition = historicalThreatAnchorPosition;
        AnchorRuntimeDeltaMeters = anchorRuntimeDeltaMeters;
        DirectRoutes = directRoutes;
        AlternateAxes = alternateAxes;
    }

    public string OwnerProfileId { get; }
    public string ThreatId { get; }
    public string ContactKind { get; }
    public DateTimeOffset ContactObservedAtUtc { get; }
    public DateTimeOffset ContactExpiresAtUtc { get; }
    public Vector3 RuntimeThreatPosition { get; }
    public Vector3 RuntimeThreatLookDirection { get; }
    public Vector3 HistoricalThreatAnchorPosition { get; }
    public float AnchorRuntimeDeltaMeters { get; }
    public IReadOnlyList<RouteExposureObservation> DirectRoutes { get; }
    public IReadOnlyList<RouteExposureObservation> AlternateAxes { get; }
    public bool ExecutionAllowed => false;
}

internal sealed class RouteExposureObservation
{
    public RouteExposureObservation(
        string routeKind,
        string routeId,
        string botProfileId,
        int sampleCount,
        int linecastCount,
        int unobstructedSampleCount,
        float unobstructedSampleRatio,
        int forwardVisibleSampleCount,
        float forwardVisibleSampleRatio,
        float facingWeightedLosEvidenceMean,
        float facingWeightedLosEvidenceMax,
        float visibleFacingDotMean,
        float visibleFacingAngleDegreesMean,
        float facingDotMin,
        float facingDotMean,
        float facingDotMax,
        float facingAngleDegreesMin,
        float facingAngleDegreesMean,
        float facingAngleDegreesMax,
        float threatDistanceMetersMin,
        float threatDistanceMetersMean,
        float threatDistanceMetersMax)
    {
        RouteKind = routeKind;
        RouteId = routeId;
        BotProfileId = botProfileId;
        SampleCount = sampleCount;
        LinecastCount = linecastCount;
        UnobstructedSampleCount = unobstructedSampleCount;
        UnobstructedSampleRatio = unobstructedSampleRatio;
        ForwardVisibleSampleCount = forwardVisibleSampleCount;
        ForwardVisibleSampleRatio = forwardVisibleSampleRatio;
        FacingWeightedLosEvidenceMean = facingWeightedLosEvidenceMean;
        FacingWeightedLosEvidenceMax = facingWeightedLosEvidenceMax;
        VisibleFacingDotMean = visibleFacingDotMean;
        VisibleFacingAngleDegreesMean = visibleFacingAngleDegreesMean;
        FacingDotMin = facingDotMin;
        FacingDotMean = facingDotMean;
        FacingDotMax = facingDotMax;
        FacingAngleDegreesMin = facingAngleDegreesMin;
        FacingAngleDegreesMean = facingAngleDegreesMean;
        FacingAngleDegreesMax = facingAngleDegreesMax;
        ThreatDistanceMetersMin = threatDistanceMetersMin;
        ThreatDistanceMetersMean = threatDistanceMetersMean;
        ThreatDistanceMetersMax = threatDistanceMetersMax;
    }

    public string RouteKind { get; }
    public string RouteId { get; }
    public string BotProfileId { get; }
    public int SampleCount { get; }
    public int LinecastCount { get; }
    public int UnobstructedSampleCount { get; }
    public float UnobstructedSampleRatio { get; }
    public int ForwardVisibleSampleCount { get; }
    public float ForwardVisibleSampleRatio { get; }
    public float FacingWeightedLosEvidenceMean { get; }
    public float FacingWeightedLosEvidenceMax { get; }
    public float VisibleFacingDotMean { get; }
    public float VisibleFacingAngleDegreesMean { get; }
    public float FacingDotMin { get; }
    public float FacingDotMean { get; }
    public float FacingDotMax { get; }
    public float FacingAngleDegreesMin { get; }
    public float FacingAngleDegreesMean { get; }
    public float FacingAngleDegreesMax { get; }
    public float ThreatDistanceMetersMin { get; }
    public float ThreatDistanceMetersMean { get; }
    public float ThreatDistanceMetersMax { get; }
    public bool ExecutionAllowed => false;
}
#endif
