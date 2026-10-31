#if SPT_CLIENT
using System;
using System.Collections.Generic;
using UnityEngine;

// Responsibility: Defines immutable Route independence  Alternate Tactical Axis observations over one coherent convergence/choke detection/topology generation.
// Flow: Bounded objective-centric/authored establishment candidates are probed to an Alt establishment space, compared with the DirectRoute family, related to pre-establishment choke/topology evidence, and published without assigning any route to an Operator.
// Authority boundary: route independence is observation only. These models cannot assign a route, create an intent, gate an Operator, acquire a lease, mutate SAIN/BigBrain/doors, or persist Tactical Coordination data.
// Invariant:  may publish an observed alternate axis, but legacy route independence genuine-alternate compatibility remains fail-closed until route assignment is explicitly rebased and runtime-qualified against AltEstablishment semantics.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal sealed class RouteIndependenceSnapshot
{
    public const string SemanticRevision = "alternate_tactical_axis_observation";

    public static readonly RouteIndependenceSnapshot Empty = new(
        string.Empty,
        0,
        string.Empty,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        Array.Empty<ThreatGroupRouteObservation>());

    public RouteIndependenceSnapshot(
        string mapId,
        long topologyRevision,
        string topologyContentHash,
        DateTimeOffset operatorBatchAtUtc,
        DateTimeOffset convergencePublishedAtUtc,
        DateTimeOffset chokePublishedAtUtc,
        DateTimeOffset publishedAtUtc,
        IReadOnlyList<ThreatGroupRouteObservation> threatGroups)
    {
        MapId = mapId;
        TopologyRevision = topologyRevision;
        TopologyContentHash = topologyContentHash;
        OperatorBatchAtUtc = operatorBatchAtUtc;
        ConvergencePublishedAtUtc = convergencePublishedAtUtc;
        ChokePublishedAtUtc = chokePublishedAtUtc;
        PublishedAtUtc = publishedAtUtc;
        ThreatGroups = threatGroups;
    }

    public string MapId { get; }
    public long TopologyRevision { get; }
    public string TopologyContentHash { get; }
    public DateTimeOffset OperatorBatchAtUtc { get; }
    public DateTimeOffset ConvergencePublishedAtUtc { get; }
    public DateTimeOffset ChokePublishedAtUtc { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public IReadOnlyList<ThreatGroupRouteObservation> ThreatGroups { get; }
    public bool IsAvailable => TopologyRevision > 0 && !string.IsNullOrWhiteSpace(MapId);
    public bool ExecutionAllowed => false;
}

internal sealed class ThreatGroupRouteObservation
{
    public ThreatGroupRouteObservation(
        string ownerProfileId,
        string threatId,
        Vector3 threatAnchorPosition,
        int directRouteCount,
        string representativeBotProfileId,
        string? observedChokeId,
        string observedChokeConfidence,
        IReadOnlyList<AlternateRouteObservation> candidates,
        IReadOnlyList<AxisCandidateObservation> axisCandidates,
        string bestCandidateId,
        string classification)
    {
        OwnerProfileId = ownerProfileId;
        ThreatId = threatId;
        ThreatAnchorPosition = threatAnchorPosition;
        DirectRouteCount = directRouteCount;
        RepresentativeBotProfileId = representativeBotProfileId;
        ObservedChokeId = observedChokeId;
        ObservedChokeConfidence = observedChokeConfidence;
        Candidates = candidates;
        AxisCandidates = axisCandidates;
        BestCandidateId = bestCandidateId;
        Classification = classification;
    }

    public string OwnerProfileId { get; }
    public string ThreatId { get; }
    public Vector3 ThreatAnchorPosition { get; }
    public int DirectRouteCount { get; }
    public string RepresentativeBotProfileId { get; }
    public string? ObservedChokeId { get; }
    public string ObservedChokeConfidence { get; }

    // Legacy compatibility surface retained for anti-conga/commit support/route assignment compile stability during the  observer campaign.
    // It intentionally carries no executable/genuine alternate authority.
    public IReadOnlyList<AlternateRouteObservation> Candidates { get; }

    public IReadOnlyList<AxisCandidateObservation> AxisCandidates { get; }
    public string BestCandidateId { get; }
    public string BestAxisCandidateId => BestCandidateId;
    public string Classification { get; }
    public bool HasObservedAlternateAxis => string.Equals(Classification, "alternate_axis_observed", StringComparison.OrdinalIgnoreCase);

    // Explicit downstream gate: current route assignment only consumes HasGenuineAlternate. Keep it closed until route assignment is rebased.
    public bool HasGenuineAlternate => false;
    public bool ExecutionAllowed => false;
}

// Legacy compatibility record retained so downstream route assignment compiles unchanged while  qualification proceeds.
// New  authority is AxisCandidateObservation below.
internal sealed class AlternateRouteObservation
{
    public AlternateRouteObservation(
        string candidateId,
        string seedBotProfileId,
        string anchorSide,
        float requestedLateralOffsetMeters,
        Vector3 intermediateAnchor,
        string pathStatus,
        float directPathLengthMeters,
        float alternatePathLengthMeters,
        float pathCostRatio,
        float routeOverlapRatio,
        float meanLateralSeparationMeters,
        float maxLateralSeparationMeters,
        float arrivalAngleDegrees,
        bool? sharesObservedFinalChoke,
        string observedFinalChokeId,
        int stableResampleCount,
        string stability,
        float independenceScore,
        string classification,
        string reason,
        IReadOnlyList<Vector3> corners,
        bool cacheHit)
    {
        CandidateId = candidateId;
        SeedBotProfileId = seedBotProfileId;
        AnchorSide = anchorSide;
        RequestedLateralOffsetMeters = requestedLateralOffsetMeters;
        IntermediateAnchor = intermediateAnchor;
        PathStatus = pathStatus;
        DirectPathLengthMeters = directPathLengthMeters;
        AlternatePathLengthMeters = alternatePathLengthMeters;
        PathCostRatio = pathCostRatio;
        RouteOverlapRatio = routeOverlapRatio;
        MeanLateralSeparationMeters = meanLateralSeparationMeters;
        MaxLateralSeparationMeters = maxLateralSeparationMeters;
        ArrivalAngleDegrees = arrivalAngleDegrees;
        SharesObservedFinalChoke = sharesObservedFinalChoke;
        ObservedFinalChokeId = observedFinalChokeId;
        StableResampleCount = stableResampleCount;
        Stability = stability;
        IndependenceScore = independenceScore;
        Classification = classification;
        Reason = reason;
        Corners = corners;
        CacheHit = cacheHit;
    }

    public string CandidateId { get; }
    public string SeedBotProfileId { get; }
    public string AnchorSide { get; }
    public float RequestedLateralOffsetMeters { get; }
    public Vector3 IntermediateAnchor { get; }
    public string PathStatus { get; }
    public float DirectPathLengthMeters { get; }
    public float AlternatePathLengthMeters { get; }
    public float PathCostRatio { get; }
    public float RouteOverlapRatio { get; }
    public float MeanLateralSeparationMeters { get; }
    public float MaxLateralSeparationMeters { get; }
    public float ArrivalAngleDegrees { get; }
    public bool? SharesObservedFinalChoke { get; }
    public string ObservedFinalChokeId { get; }
    public int StableResampleCount { get; }
    public string Stability { get; }
    public float IndependenceScore { get; }
    public string Classification { get; }
    public string Reason { get; }
    public IReadOnlyList<Vector3> Corners { get; }
    public bool CacheHit { get; }
    public bool HasUsableGeometry => Corners.Count >= 2 && string.Equals(PathStatus, "complete", StringComparison.OrdinalIgnoreCase);

    // Deliberate  fail-closed compatibility contract. route assignment must be explicitly rebased before this can ever reopen.
    public bool IsGenuineAlternate => false;
    public bool ExecutionAllowed => false;
}

internal sealed class AxisCandidateObservation
{
    public AxisCandidateObservation(
        string candidateId,
        string candidateSource,
        string candidateSourceId,
        string candidateSourceOrigin,
        string seedBotProfileId,
        string approachSector,
        float approachAngleFromDirectDegrees,
        Vector3 establishmentAnchor,
        Vector3 establishmentRegionCenter,
        float establishmentRegionRadiusMeters,
        float objectiveDistanceMeters,
        string pathStatus,
        float pathLengthMeters,
        float directReferencePathLengthMeters,
        float pathCostRatioTelemetry,
        float routeOverlapRatio,
        float meanDirectFamilySeparationMeters,
        float maxDirectFamilySeparationMeters,
        float arrivalAngleFromDirectDegrees,
        float independentTailMeters,
        string topologyEvidence,
        string directTopologySignature,
        string alternateTopologySignature,
        string preEstablishmentChokeRelation,
        string observedChokeId,
        string observedChokeConfidence,
        float? minimumObservedChokeDistanceMeters,
        string verticalEvidence,
        float verticalDeltaMeters,
        int stableResampleCount,
        string stability,
        float axisEvidenceScore,
        string classification,
        string reasonCode,
        IReadOnlyList<Vector3> corners,
        bool cacheHit)
    {
        CandidateId = candidateId;
        CandidateSource = candidateSource;
        CandidateSourceId = candidateSourceId;
        CandidateSourceOrigin = candidateSourceOrigin;
        SeedBotProfileId = seedBotProfileId;
        ApproachSector = approachSector;
        ApproachAngleFromDirectDegrees = approachAngleFromDirectDegrees;
        EstablishmentAnchor = establishmentAnchor;
        EstablishmentRegionCenter = establishmentRegionCenter;
        EstablishmentRegionRadiusMeters = establishmentRegionRadiusMeters;
        ObjectiveDistanceMeters = objectiveDistanceMeters;
        PathStatus = pathStatus;
        PathLengthMeters = pathLengthMeters;
        DirectReferencePathLengthMeters = directReferencePathLengthMeters;
        PathCostRatioTelemetry = pathCostRatioTelemetry;
        RouteOverlapRatio = routeOverlapRatio;
        MeanDirectFamilySeparationMeters = meanDirectFamilySeparationMeters;
        MaxDirectFamilySeparationMeters = maxDirectFamilySeparationMeters;
        ArrivalAngleFromDirectDegrees = arrivalAngleFromDirectDegrees;
        IndependentTailMeters = independentTailMeters;
        TopologyEvidence = topologyEvidence;
        DirectTopologySignature = directTopologySignature;
        AlternateTopologySignature = alternateTopologySignature;
        PreEstablishmentChokeRelation = preEstablishmentChokeRelation;
        ObservedChokeId = observedChokeId;
        ObservedChokeConfidence = observedChokeConfidence;
        MinimumObservedChokeDistanceMeters = minimumObservedChokeDistanceMeters;
        VerticalEvidence = verticalEvidence;
        VerticalDeltaMeters = verticalDeltaMeters;
        StableResampleCount = stableResampleCount;
        Stability = stability;
        AxisEvidenceScore = axisEvidenceScore;
        Classification = classification;
        ReasonCode = reasonCode;
        Corners = corners;
        CacheHit = cacheHit;
    }

    public string CandidateId { get; }
    public string CandidateSource { get; }
    public string CandidateSourceId { get; }
    public string CandidateSourceOrigin { get; }
    public string SeedBotProfileId { get; }
    public string ApproachSector { get; }
    public float ApproachAngleFromDirectDegrees { get; }
    public Vector3 EstablishmentAnchor { get; }
    public Vector3 EstablishmentRegionCenter { get; }
    public float EstablishmentRegionRadiusMeters { get; }
    public float ObjectiveDistanceMeters { get; }
    public string PathStatus { get; }
    public float PathLengthMeters { get; }
    public float DirectReferencePathLengthMeters { get; }
    public float PathCostRatioTelemetry { get; }
    public float RouteOverlapRatio { get; }
    public float MeanDirectFamilySeparationMeters { get; }
    public float MaxDirectFamilySeparationMeters { get; }
    public float ArrivalAngleFromDirectDegrees { get; }
    public float IndependentTailMeters { get; }
    public string TopologyEvidence { get; }
    public string DirectTopologySignature { get; }
    public string AlternateTopologySignature { get; }
    public string PreEstablishmentChokeRelation { get; }
    public string ObservedChokeId { get; }
    public string ObservedChokeConfidence { get; }
    public float? MinimumObservedChokeDistanceMeters { get; }
    public string VerticalEvidence { get; }
    public float VerticalDeltaMeters { get; }
    public int StableResampleCount { get; }
    public string Stability { get; }
    public float AxisEvidenceScore { get; }
    public string Classification { get; }
    public string ReasonCode { get; }
    public IReadOnlyList<Vector3> Corners { get; }
    public bool CacheHit { get; }
    public bool HasUsableGeometry => Corners.Count >= 2 && string.Equals(PathStatus, "complete", StringComparison.OrdinalIgnoreCase);
    public bool IsObservedAlternateAxis => string.Equals(Classification, "alternate_axis_observed", StringComparison.OrdinalIgnoreCase);
    public bool ExecutionAllowed => false;
}
#endif
