#if SPT_CLIENT
using System;
using System.Collections.Generic;
using UnityEngine;

// Responsibility: Defines immutable Tactical assessment snapshots derived from read-only topology observations plus generation-matched Operator decision evidence.
// Flow: The assessment service scores read-only context candidates for each Operator, aggregates owner-level observations, and publishes one revision-bound diagnostic snapshot for later Coordinator milestones.
// Authority boundary: assessment scores observation relevance only; every candidate is non-executable and cannot create intents, acquire movement leases, mutate SAIN/BigBrain state, or persist Tactical Authoring data.
// Invariant: Published assessments are bound to one topology revision/hash and one matched Operator evidence batch; stale or generation-mismatched evidence is omitted rather than fabricated.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal sealed class TacticalAssessmentSnapshot
{
    public static readonly TacticalAssessmentSnapshot Empty = new(
        string.Empty,
        0,
        string.Empty,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        Array.Empty<TacticalOperatorAssessment>(),
        Array.Empty<TacticalOwnerAssessment>());

    public TacticalAssessmentSnapshot(
        string mapId,
        long topologyRevision,
        string topologyContentHash,
        DateTimeOffset operatorBatchAtUtc,
        DateTimeOffset publishedAtUtc,
        IReadOnlyList<TacticalOperatorAssessment> operators,
        IReadOnlyList<TacticalOwnerAssessment> owners)
    {
        MapId = mapId;
        TopologyRevision = topologyRevision;
        TopologyContentHash = topologyContentHash;
        OperatorBatchAtUtc = operatorBatchAtUtc;
        PublishedAtUtc = publishedAtUtc;
        Operators = operators;
        Owners = owners;
    }

    public string MapId { get; }
    public long TopologyRevision { get; }
    public string TopologyContentHash { get; }
    public DateTimeOffset OperatorBatchAtUtc { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public IReadOnlyList<TacticalOperatorAssessment> Operators { get; }
    public IReadOnlyList<TacticalOwnerAssessment> Owners { get; }
    public bool IsAvailable => TopologyRevision > 0 && !string.IsNullOrWhiteSpace(MapId);
}

internal sealed class TacticalOperatorAssessment
{
    public TacticalOperatorAssessment(
        string operatorId,
        string ownerProfileId,
        string botProfileId,
        DateTimeOffset evidenceCapturedAtUtc,
        Vector3 operatorPosition,
        bool directThreat,
        bool relevantThreat,
        string threatId,
        string threatClassification,
        Vector3? threatAnchorPosition,
        string threatAnchorSource,
        string threatAnchorConfidence,
        float? threatAnchorAgeSeconds,
        bool inTacticalBubble,
        bool usefulCohesionPosition,
        bool insideTacticalAnchor,
        string contextClassification,
        IReadOnlyList<TacticalCandidateAssessment> candidates,
        TacticalCandidateAssessment preferredObservation)
    {
        OperatorId = operatorId;
        OwnerProfileId = ownerProfileId;
        BotProfileId = botProfileId;
        EvidenceCapturedAtUtc = evidenceCapturedAtUtc;
        OperatorPosition = operatorPosition;
        DirectThreat = directThreat;
        RelevantThreat = relevantThreat;
        ThreatId = threatId;
        ThreatClassification = threatClassification;
        ThreatAnchorPosition = threatAnchorPosition;
        ThreatAnchorSource = threatAnchorSource;
        ThreatAnchorConfidence = threatAnchorConfidence;
        ThreatAnchorAgeSeconds = threatAnchorAgeSeconds;
        InTacticalBubble = inTacticalBubble;
        UsefulCohesionPosition = usefulCohesionPosition;
        InsideTacticalAnchor = insideTacticalAnchor;
        ContextClassification = contextClassification;
        Candidates = candidates;
        PreferredObservation = preferredObservation;
    }

    public string OperatorId { get; }
    public string OwnerProfileId { get; }
    public string BotProfileId { get; }
    public DateTimeOffset EvidenceCapturedAtUtc { get; }
    public Vector3 OperatorPosition { get; }
    public bool DirectThreat { get; }
    public bool RelevantThreat { get; }
    public string ThreatId { get; }
    public string ThreatClassification { get; }
    public Vector3? ThreatAnchorPosition { get; }
    public string ThreatAnchorSource { get; }
    public string ThreatAnchorConfidence { get; }
    public float? ThreatAnchorAgeSeconds { get; }
    public bool InTacticalBubble { get; }
    public bool UsefulCohesionPosition { get; }
    public bool InsideTacticalAnchor { get; }
    public string ContextClassification { get; }
    public IReadOnlyList<TacticalCandidateAssessment> Candidates { get; }
    public TacticalCandidateAssessment PreferredObservation { get; }
}

internal sealed class TacticalCandidateAssessment
{
    public static readonly TacticalCandidateAssessment None = new(
        "none",
        "none",
        string.Empty,
        null,
        0f,
        false,
        "no_candidate");

    public TacticalCandidateAssessment(
        string candidateKind,
        string targetId,
        string containerId,
        float? distanceMeters,
        float relevanceScore,
        bool available,
        string evidence)
    {
        CandidateKind = candidateKind;
        TargetId = targetId;
        ContainerId = containerId;
        DistanceMeters = distanceMeters;
        RelevanceScore = relevanceScore;
        Available = available;
        Evidence = evidence;
    }

    public string CandidateKind { get; }
    public string TargetId { get; }
    public string ContainerId { get; }
    public float? DistanceMeters { get; }
    public float RelevanceScore { get; }
    public bool Available { get; }
    public string Evidence { get; }
    public bool ExecutionAllowed => false;
}

internal sealed class TacticalOwnerAssessment
{
    public TacticalOwnerAssessment(
        string ownerProfileId,
        int operatorCount,
        int directThreatCount,
        int outsideBubbleCount,
        int cohesionReviewCount,
        int anchorOccupancyCount,
        int poseObservationCount,
        int portalObservationCount,
        int passageObservationCount,
        string classification)
    {
        OwnerProfileId = ownerProfileId;
        OperatorCount = operatorCount;
        DirectThreatCount = directThreatCount;
        OutsideBubbleCount = outsideBubbleCount;
        CohesionReviewCount = cohesionReviewCount;
        AnchorOccupancyCount = anchorOccupancyCount;
        PoseObservationCount = poseObservationCount;
        PortalObservationCount = portalObservationCount;
        PassageObservationCount = passageObservationCount;
        Classification = classification;
    }

    public string OwnerProfileId { get; }
    public int OperatorCount { get; }
    public int DirectThreatCount { get; }
    public int OutsideBubbleCount { get; }
    public int CohesionReviewCount { get; }
    public int AnchorOccupancyCount { get; }
    public int PoseObservationCount { get; }
    public int PortalObservationCount { get; }
    public int PassageObservationCount { get; }
    public string Classification { get; }
}
#endif
