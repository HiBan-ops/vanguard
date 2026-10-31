#if SPT_CLIENT
using System;
using System.Collections.Generic;
using UnityEngine;

// Responsibility: Defines immutable tactical choke observations derived from one exact convergence generation plus bounded live EFT structural evidence.
// Flow: Each qualifying shared-threat convergence group may yield one inferred Choke or structurally confirmed HardChoke, carrying copied geometry/door scalars and explicit confidence.
// Authority boundary: choke detection is observation only. These models cannot gate Operators, emit movement/intent, mutate doors/SAIN/BigBrain, or persist Tactical Coordination data.
// Invariant: HardChoke CONFIRMED is impossible without a fresh, internally consistent NavMeshDoorLink relation to the convergent route segment; door presence alone never promotes tactical choke semantics.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal sealed class ChokeDetectionSnapshot
{
    public static readonly ChokeDetectionSnapshot Empty = new(
        string.Empty,
        0,
        string.Empty,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        Array.Empty<ChokeObservation>());

    public ChokeDetectionSnapshot(
        string mapId,
        long topologyRevision,
        string topologyContentHash,
        DateTimeOffset operatorBatchAtUtc,
        DateTimeOffset convergencePublishedAtUtc,
        DateTimeOffset publishedAtUtc,
        IReadOnlyList<ChokeObservation> chokes)
    {
        MapId = mapId;
        TopologyRevision = topologyRevision;
        TopologyContentHash = topologyContentHash;
        OperatorBatchAtUtc = operatorBatchAtUtc;
        ConvergencePublishedAtUtc = convergencePublishedAtUtc;
        PublishedAtUtc = publishedAtUtc;
        Chokes = chokes;
    }

    public string MapId { get; }
    public long TopologyRevision { get; }
    public string TopologyContentHash { get; }
    public DateTimeOffset OperatorBatchAtUtc { get; }
    public DateTimeOffset ConvergencePublishedAtUtc { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public IReadOnlyList<ChokeObservation> Chokes { get; }
    public bool IsAvailable => TopologyRevision > 0 && !string.IsNullOrWhiteSpace(MapId);
    public bool ExecutionAllowed => false;
}

internal sealed class ChokeObservation
{
    public ChokeObservation(
        string chokeId,
        string ownerProfileId,
        string threatId,
        string kind,
        string confidence,
        Vector3 segmentNear,
        Vector3 segmentFar,
        Vector3 center,
        float sharedSegmentNearDistanceFromThreatMeters,
        float sharedSegmentFarDistanceFromThreatMeters,
        float sharedSegmentMeters,
        float maxObservedSpreadMeters,
        int routeCount,
        int convergedPairCount,
        int ambiguousPairCount,
        int independentPairCount,
        float observedRouteConvergenceRatio,
        ChokeDoorEvidence? structuralDoor,
        string reason)
    {
        ChokeId = chokeId;
        OwnerProfileId = ownerProfileId;
        ThreatId = threatId;
        Kind = kind;
        Confidence = confidence;
        SegmentNear = segmentNear;
        SegmentFar = segmentFar;
        Center = center;
        SharedSegmentNearDistanceFromThreatMeters = sharedSegmentNearDistanceFromThreatMeters;
        SharedSegmentFarDistanceFromThreatMeters = sharedSegmentFarDistanceFromThreatMeters;
        SharedSegmentMeters = sharedSegmentMeters;
        MaxObservedSpreadMeters = maxObservedSpreadMeters;
        RouteCount = routeCount;
        ConvergedPairCount = convergedPairCount;
        AmbiguousPairCount = ambiguousPairCount;
        IndependentPairCount = independentPairCount;
        ObservedRouteConvergenceRatio = observedRouteConvergenceRatio;
        StructuralDoor = structuralDoor;
        Reason = reason;
    }

    public string ChokeId { get; }
    public string OwnerProfileId { get; }
    public string ThreatId { get; }
    public string Kind { get; }
    public string Confidence { get; }
    public Vector3 SegmentNear { get; }
    public Vector3 SegmentFar { get; }
    public Vector3 Center { get; }
    public float SharedSegmentNearDistanceFromThreatMeters { get; }
    public float SharedSegmentFarDistanceFromThreatMeters { get; }
    public float SharedSegmentMeters { get; }
    public float MaxObservedSpreadMeters { get; }
    public int RouteCount { get; }
    public int ConvergedPairCount { get; }
    public int AmbiguousPairCount { get; }
    public int IndependentPairCount { get; }
    public float ObservedRouteConvergenceRatio { get; }
    public ChokeDoorEvidence? StructuralDoor { get; }
    public string Reason { get; }
    public bool IsHardChoke => string.Equals(Kind, "hard_choke", StringComparison.OrdinalIgnoreCase);
    public bool IsConfirmed => string.Equals(Confidence, "CONFIRMED", StringComparison.OrdinalIgnoreCase);
    public bool ExecutionAllowed => false;
}

internal sealed class ChokeDoorEvidence
{
    public ChokeDoorEvidence(
        int linkId,
        string doorId,
        string doorState,
        Vector3 portalCenter,
        float routeRelationDistanceMeters,
        int relatedRouteCount,
        int requiredRouteCount,
        string consistency,
        string routeExecutability,
        bool nativeTraversalEligible)
    {
        LinkId = linkId;
        DoorId = doorId;
        DoorState = doorState;
        PortalCenter = portalCenter;
        RouteRelationDistanceMeters = routeRelationDistanceMeters;
        RelatedRouteCount = relatedRouteCount;
        RequiredRouteCount = requiredRouteCount;
        Consistency = consistency;
        RouteExecutability = routeExecutability;
        NativeTraversalEligible = nativeTraversalEligible;
    }

    public int LinkId { get; }
    public string DoorId { get; }
    public string DoorState { get; }
    public Vector3 PortalCenter { get; }
    public float RouteRelationDistanceMeters { get; }
    public int RelatedRouteCount { get; }
    public int RequiredRouteCount { get; }
    public string Consistency { get; }
    public string RouteExecutability { get; }
    public bool NativeTraversalEligible { get; }
    public bool IsConfirmed => string.Equals(Consistency, "consistent_live_link", StringComparison.OrdinalIgnoreCase)
        && RelatedRouteCount >= RequiredRouteCount;
}
#endif
