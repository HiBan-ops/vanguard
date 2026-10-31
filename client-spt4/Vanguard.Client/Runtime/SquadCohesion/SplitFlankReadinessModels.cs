#if SPT_CLIENT
using System;
using System.Collections.Generic;
using UnityEngine;

// Responsibility: Defines immutable split-flank shadow observations over one alternate-establishment assignment and its Direct dependency set.
// Flow: The selected Alt member is revalidated against planning membership/provenance evidence and observed spatially against the route independence establishment region while Direct members are independently revalidated.
// Authority boundary: Split-flank readiness is observation only. Position establishment evidence is not movement authority, pressure proof, synchronized commit permission, or flank execution authority.
// Invariant: Reaching an establishment region can satisfy only a shadow spatial dependency. Full AltEstablished semantics remain closed until tactical pressure/engagement evidence is separately defined and qualified.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal sealed class SplitFlankReadinessSnapshot
{
    public const string SemanticRevision = "alt_establishment_dependency_shadow";

    public static readonly SplitFlankReadinessSnapshot Empty = new(
        string.Empty,
        0,
        string.Empty,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        Array.Empty<ThreatGroupReadinessObservation>());

    public SplitFlankReadinessSnapshot(
        string mapId,
        long topologyRevision,
        string topologyContentHash,
        DateTimeOffset operatorBatchAtUtc,
        DateTimeOffset assignmentPublishedAtUtc,
        DateTimeOffset publishedAtUtc,
        IReadOnlyList<ThreatGroupReadinessObservation> threatGroups)
    {
        MapId = mapId;
        TopologyRevision = topologyRevision;
        TopologyContentHash = topologyContentHash;
        OperatorBatchAtUtc = operatorBatchAtUtc;
        AssignmentPublishedAtUtc = assignmentPublishedAtUtc;
        PublishedAtUtc = publishedAtUtc;
        ThreatGroups = threatGroups;
    }

    public string MapId { get; }
    public long TopologyRevision { get; }
    public string TopologyContentHash { get; }
    public DateTimeOffset OperatorBatchAtUtc { get; }
    public DateTimeOffset AssignmentPublishedAtUtc { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public IReadOnlyList<ThreatGroupReadinessObservation> ThreatGroups { get; }
    public bool IsAvailable => TopologyRevision > 0 && !string.IsNullOrWhiteSpace(MapId);
    public bool PositionEstablishmentObservationOnly => true;
    public bool ExecutionAllowed => false;
    public bool SynchronizationApplied => false;
}

internal sealed class ThreatGroupReadinessObservation
{
    public ThreatGroupReadinessObservation(
        string ownerProfileId,
        string threatId,
        string alternateCandidateId,
        string alternateBotProfileId,
        Vector3 alternateEstablishmentRegionCenter,
        float alternateEstablishmentRegionRadiusMeters,
        bool alternatePositionKnown,
        Vector3 alternatePosition,
        float alternateDistanceToRegionCenterMeters,
        float alternateHorizontalDistanceMeters,
        float alternateVerticalDeltaMeters,
        bool alternatePlanningViable,
        string alternatePlanningReason,
        bool selectedPairPathProven,
        bool alternateInsideEstablishmentRegion,
        int establishmentStableSamples,
        float establishmentDwellSeconds,
        bool alternatePositionEstablishedObserved,
        int directMemberCount,
        int directReadyCount,
        int directVetoedCount,
        int directUnknownCount,
        IReadOnlyList<string> directBlockingReasons,
        bool dependencyEnvelopeReady,
        bool positionDependencyEnvelopeObserved,
        bool directElementDegraded,
        bool replacementSelectionPressureObserved,
        bool assignmentChanged,
        float assignmentAgeSeconds,
        string classification,
        string reason)
    {
        OwnerProfileId = ownerProfileId;
        ThreatId = threatId;
        AlternateCandidateId = alternateCandidateId;
        AlternateBotProfileId = alternateBotProfileId;
        AlternateEstablishmentRegionCenter = alternateEstablishmentRegionCenter;
        AlternateEstablishmentRegionRadiusMeters = alternateEstablishmentRegionRadiusMeters;
        AlternatePositionKnown = alternatePositionKnown;
        AlternatePosition = alternatePosition;
        AlternateDistanceToRegionCenterMeters = alternateDistanceToRegionCenterMeters;
        AlternateHorizontalDistanceMeters = alternateHorizontalDistanceMeters;
        AlternateVerticalDeltaMeters = alternateVerticalDeltaMeters;
        AlternatePlanningViable = alternatePlanningViable;
        AlternatePlanningReason = alternatePlanningReason;
        SelectedPairPathProven = selectedPairPathProven;
        AlternateInsideEstablishmentRegion = alternateInsideEstablishmentRegion;
        EstablishmentStableSamples = establishmentStableSamples;
        EstablishmentDwellSeconds = establishmentDwellSeconds;
        AlternatePositionEstablishedObserved = alternatePositionEstablishedObserved;
        DirectMemberCount = directMemberCount;
        DirectReadyCount = directReadyCount;
        DirectVetoedCount = directVetoedCount;
        DirectUnknownCount = directUnknownCount;
        DirectBlockingReasons = directBlockingReasons;
        DependencyEnvelopeReady = dependencyEnvelopeReady;
        PositionDependencyEnvelopeObserved = positionDependencyEnvelopeObserved;
        DirectElementDegraded = directElementDegraded;
        ReplacementSelectionPressureObserved = replacementSelectionPressureObserved;
        AssignmentChanged = assignmentChanged;
        AssignmentAgeSeconds = assignmentAgeSeconds;
        Classification = classification;
        Reason = reason;
    }

    public string OwnerProfileId { get; }
    public string ThreatId { get; }
    public string AlternateCandidateId { get; }
    public string AlternateBotProfileId { get; }
    public Vector3 AlternateEstablishmentRegionCenter { get; }
    public float AlternateEstablishmentRegionRadiusMeters { get; }
    public bool AlternatePositionKnown { get; }
    public Vector3 AlternatePosition { get; }
    public float AlternateDistanceToRegionCenterMeters { get; }
    public float AlternateHorizontalDistanceMeters { get; }
    public float AlternateVerticalDeltaMeters { get; }
    public bool AlternatePlanningViable { get; }
    public string AlternatePlanningReason { get; }
    public bool SelectedPairPathProven { get; }
    public bool AlternateInsideEstablishmentRegion { get; }
    public int EstablishmentStableSamples { get; }
    public float EstablishmentDwellSeconds { get; }
    public bool AlternatePositionEstablishedObserved { get; }
    public int DirectMemberCount { get; }
    public int DirectReadyCount { get; }
    public int DirectVetoedCount { get; }
    public int DirectUnknownCount { get; }
    public IReadOnlyList<string> DirectBlockingReasons { get; }
    public bool DependencyEnvelopeReady { get; }
    public bool PositionDependencyEnvelopeObserved { get; }
    public bool DirectElementDegraded { get; }
    public bool ReplacementSelectionPressureObserved { get; }
    public bool AssignmentChanged { get; }
    public float AssignmentAgeSeconds { get; }
    public string Classification { get; }
    public string Reason { get; }

    public bool AltDevelopEligible => SelectedPairPathProven && AlternatePlanningViable;

    // Deliberately false: incidental/current movement toward the region is not proof that Vanguard commanded or developed the axis.
    public bool AlternateDevelopmentObserved => false;

    // Deliberately false: position establishment does not yet prove tactical pressure/engagement capability.
    public bool FullAltEstablished => false;
    public bool SynchronizedCommitReady => false;
    public bool ExecutionAllowed => false;
}
#endif
