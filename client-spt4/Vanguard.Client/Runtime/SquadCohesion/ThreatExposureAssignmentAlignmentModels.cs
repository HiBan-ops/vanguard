#if SPT_CLIENT
using System;
using System.Collections.Generic;

// Responsibility: immutable observation-only evidence that binds threat exposure to the exact RouteAssignment-selected Direct element and selected Alt Operator/path.
// Flow: qualified raw threat pose/Direct exposure is joined with one exact RouteAssignment publication and its candidate-specific selected Alt path, while preserving the exact convergence/choke/independence/assignment provenance required by downstream shadow classification.
// Authority boundary: Data only. This model cannot classify HeldAccess/KillLane, change a route assignment, start/hold movement, request replan, mutate SAIN or acquire execution authority.
// Invariant: "an alternate route is relieved" is never inferred from a different alternate axis or a different Operator than the pair actually selected by RouteAssignment; downstream consumers can reject stale/mismatched provenance fail-open.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal sealed class ThreatExposureAssignmentAlignmentSnapshot
{
    public static readonly ThreatExposureAssignmentAlignmentSnapshot Empty = new(
        string.Empty,
        0,
        string.Empty,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        Array.Empty<ThreatExposureAssignmentAlignmentGroup>());

    public ThreatExposureAssignmentAlignmentSnapshot(
        string mapId,
        long topologyRevision,
        string topologyContentHash,
        DateTimeOffset operatorBatchAtUtc,
        DateTimeOffset convergencePublishedAtUtc,
        DateTimeOffset chokePublishedAtUtc,
        DateTimeOffset independencePublishedAtUtc,
        DateTimeOffset exposurePublishedAtUtc,
        DateTimeOffset routeAssignmentPublishedAtUtc,
        DateTimeOffset publishedAtUtc,
        IReadOnlyList<ThreatExposureAssignmentAlignmentGroup> threatGroups)
    {
        MapId = mapId;
        TopologyRevision = topologyRevision;
        TopologyContentHash = topologyContentHash;
        OperatorBatchAtUtc = operatorBatchAtUtc;
        ConvergencePublishedAtUtc = convergencePublishedAtUtc;
        ChokePublishedAtUtc = chokePublishedAtUtc;
        IndependencePublishedAtUtc = independencePublishedAtUtc;
        ExposurePublishedAtUtc = exposurePublishedAtUtc;
        RouteAssignmentPublishedAtUtc = routeAssignmentPublishedAtUtc;
        PublishedAtUtc = publishedAtUtc;
        ThreatGroups = threatGroups;
    }

    public string MapId { get; }
    public long TopologyRevision { get; }
    public string TopologyContentHash { get; }
    public DateTimeOffset OperatorBatchAtUtc { get; }
    public DateTimeOffset ConvergencePublishedAtUtc { get; }
    public DateTimeOffset ChokePublishedAtUtc { get; }
    public DateTimeOffset IndependencePublishedAtUtc { get; }
    public DateTimeOffset ExposurePublishedAtUtc { get; }
    public DateTimeOffset RouteAssignmentPublishedAtUtc { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public IReadOnlyList<ThreatExposureAssignmentAlignmentGroup> ThreatGroups { get; }
    public bool IsAvailable => TopologyRevision > 0 && !string.IsNullOrWhiteSpace(MapId);
    public bool ExecutionAllowed => false;
}

internal sealed class ThreatExposureAssignmentAlignmentGroup
{
    public ThreatExposureAssignmentAlignmentGroup(
        string ownerProfileId,
        string threatId,
        string alternateCandidateId,
        string selectedAltBotProfileId,
        string selectedAltOperatorId,
        string role,
        string specialty,
        DateTimeOffset contactObservedAtUtc,
        DateTimeOffset contactExpiresAtUtc,
        DateTimeOffset selectedPairSourceEvidenceCapturedAtUtc,
        bool selectedPairPathCacheHit,
        float selectedPairPathLengthMeters,
        string rawObservedAltAxisId,
        bool rawObservedAltMatchesSelectedAxis,
        float rawObservedAltFacingWeightedLosMean,
        IReadOnlyList<string> directBotProfileIds,
        int matchedDirectRouteCount,
        float directFacingWeightedLosMean,
        float directFacingWeightedLosMax,
        float directForwardVisibleMean,
        RouteExposureObservation selectedAltRouteExposure,
        float directVsSelectedAltFacingWeightedDelta)
    {
        OwnerProfileId = ownerProfileId;
        ThreatId = threatId;
        AlternateCandidateId = alternateCandidateId;
        SelectedAltBotProfileId = selectedAltBotProfileId;
        SelectedAltOperatorId = selectedAltOperatorId;
        Role = role;
        Specialty = specialty;
        ContactObservedAtUtc = contactObservedAtUtc;
        ContactExpiresAtUtc = contactExpiresAtUtc;
        SelectedPairSourceEvidenceCapturedAtUtc = selectedPairSourceEvidenceCapturedAtUtc;
        SelectedPairPathCacheHit = selectedPairPathCacheHit;
        SelectedPairPathLengthMeters = selectedPairPathLengthMeters;
        RawObservedAltAxisId = rawObservedAltAxisId;
        RawObservedAltMatchesSelectedAxis = rawObservedAltMatchesSelectedAxis;
        RawObservedAltFacingWeightedLosMean = rawObservedAltFacingWeightedLosMean;
        DirectBotProfileIds = directBotProfileIds;
        MatchedDirectRouteCount = matchedDirectRouteCount;
        DirectFacingWeightedLosMean = directFacingWeightedLosMean;
        DirectFacingWeightedLosMax = directFacingWeightedLosMax;
        DirectForwardVisibleMean = directForwardVisibleMean;
        SelectedAltRouteExposure = selectedAltRouteExposure;
        DirectVsSelectedAltFacingWeightedDelta = directVsSelectedAltFacingWeightedDelta;
    }

    public string OwnerProfileId { get; }
    public string ThreatId { get; }
    public string AlternateCandidateId { get; }
    public string SelectedAltBotProfileId { get; }
    public string SelectedAltOperatorId { get; }
    public string Role { get; }
    public string Specialty { get; }
    public DateTimeOffset ContactObservedAtUtc { get; }
    public DateTimeOffset ContactExpiresAtUtc { get; }
    public DateTimeOffset SelectedPairSourceEvidenceCapturedAtUtc { get; }
    public bool SelectedPairPathCacheHit { get; }
    public float SelectedPairPathLengthMeters { get; }
    public string RawObservedAltAxisId { get; }
    public bool RawObservedAltMatchesSelectedAxis { get; }
    public float RawObservedAltFacingWeightedLosMean { get; }
    public IReadOnlyList<string> DirectBotProfileIds { get; }
    public int DirectMemberCount => DirectBotProfileIds.Count;
    public int MatchedDirectRouteCount { get; }
    public float DirectFacingWeightedLosMean { get; }
    public float DirectFacingWeightedLosMax { get; }
    public float DirectForwardVisibleMean { get; }
    public RouteExposureObservation SelectedAltRouteExposure { get; }
    public float DirectVsSelectedAltFacingWeightedDelta { get; }
    public bool ExecutionAllowed => false;
}
#endif
