#if SPT_CLIENT
using System;
using System.Collections.Generic;
using UnityEngine;

// Responsibility: Defines immutable shadow route-assignment observations over alternate-establishment axes.
// Flow: Observed structural axes are combined with current Operator planning viability and candidate-specific paths to each establishment anchor; only fully feasible (axis, Operator) pairs enter preference ordering.
// Authority boundary: Route assignment is observation/planning only. These models cannot create intents, acquire leases, mutate SAIN/BigBrain, issue movement, reserve a member, or synchronize an assault.
// Invariant: Hard Conditions (membership/provenance/path proof) precede Preferences. Role/path/axis quality can rank only already-feasible pairs and can never compensate for a failed Condition.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal sealed class RouteAssignmentSnapshot
{
    public const string SemanticRevision = "alt_establishment_member_assignment_shadow";

    public static readonly RouteAssignmentSnapshot Empty = new(
        string.Empty,
        0,
        string.Empty,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        Array.Empty<ThreatGroupRouteAssignment>());

    public RouteAssignmentSnapshot(
        string mapId,
        long topologyRevision,
        string topologyContentHash,
        DateTimeOffset operatorBatchAtUtc,
        DateTimeOffset routePublishedAtUtc,
        DateTimeOffset publishedAtUtc,
        IReadOnlyList<ThreatGroupRouteAssignment> threatGroups)
    {
        MapId = mapId;
        TopologyRevision = topologyRevision;
        TopologyContentHash = topologyContentHash;
        OperatorBatchAtUtc = operatorBatchAtUtc;
        RoutePublishedAtUtc = routePublishedAtUtc;
        PublishedAtUtc = publishedAtUtc;
        ThreatGroups = threatGroups;
    }

    public string MapId { get; }
    public long TopologyRevision { get; }
    public string TopologyContentHash { get; }
    public DateTimeOffset OperatorBatchAtUtc { get; }
    public DateTimeOffset RoutePublishedAtUtc { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public IReadOnlyList<ThreatGroupRouteAssignment> ThreatGroups { get; }
    public bool IsAvailable => TopologyRevision > 0 && !string.IsNullOrWhiteSpace(MapId);
    public bool ExecutionAllowed => false;
    public bool RouteAssignmentApplied => false;
    public bool MovementAuthority => false;
}

internal sealed class ThreatGroupRouteAssignment
{
    public ThreatGroupRouteAssignment(
        string ownerProfileId,
        string threatId,
        string alternateCandidateId,
        string alternateSeedBotProfileId,
        string alternateCandidateSource,
        string alternateApproachSector,
        Vector3 alternateEstablishmentAnchor,
        Vector3 alternateEstablishmentRegionCenter,
        float alternateEstablishmentRegionRadiusMeters,
        float alternateAxisEvidenceScore,
        int observedAxisCount,
        int feasiblePairCount,
        string selectedAltBotProfileId,
        string selectedAltOperatorId,
        string selectedAltSupportBotProfileId,
        string selectedAltSupportOperatorId,
        bool selectedAltSupportPathProven,
        float selectedAltSupportPathLengthMeters,
        string role,
        string specialty,
        string basePersona,
        string doctrine,
        string temperament,
        string rolePreferenceBand,
        bool selectedAltPlanningViable,
        string planningViabilityReason,
        bool selectedPairPathProven,
        float selectedPathLengthMeters,
        float selectedDirectDistanceMeters,
        float selectedPathRatio,
        int potentialBetterRoleUnprovenCount,
        int potentialBetterRolePathProvenCount,
        bool candidateMatrixComplete,
        IReadOnlyList<AltRouteFeasibilityObservation> altCandidateFeasibility,
        IReadOnlyList<string> directBotProfileIds,
        string classification,
        string reason)
    {
        OwnerProfileId = ownerProfileId;
        ThreatId = threatId;
        AlternateCandidateId = alternateCandidateId;
        AlternateSeedBotProfileId = alternateSeedBotProfileId;
        AlternateCandidateSource = alternateCandidateSource;
        AlternateApproachSector = alternateApproachSector;
        AlternateEstablishmentAnchor = alternateEstablishmentAnchor;
        AlternateEstablishmentRegionCenter = alternateEstablishmentRegionCenter;
        AlternateEstablishmentRegionRadiusMeters = alternateEstablishmentRegionRadiusMeters;
        AlternateAxisEvidenceScore = alternateAxisEvidenceScore;
        ObservedAxisCount = observedAxisCount;
        FeasiblePairCount = feasiblePairCount;
        SelectedAltBotProfileId = selectedAltBotProfileId;
        SelectedAltOperatorId = selectedAltOperatorId;
        SelectedAltSupportBotProfileId = selectedAltSupportBotProfileId;
        SelectedAltSupportOperatorId = selectedAltSupportOperatorId;
        SelectedAltSupportPathProven = selectedAltSupportPathProven;
        SelectedAltSupportPathLengthMeters = selectedAltSupportPathLengthMeters;
        Role = role;
        Specialty = specialty;
        BasePersona = basePersona;
        Doctrine = doctrine;
        Temperament = temperament;
        RolePreferenceBand = rolePreferenceBand;
        SelectedAltPlanningViable = selectedAltPlanningViable;
        PlanningViabilityReason = planningViabilityReason;
        SelectedPairPathProven = selectedPairPathProven;
        SelectedPathLengthMeters = selectedPathLengthMeters;
        SelectedDirectDistanceMeters = selectedDirectDistanceMeters;
        SelectedPathRatio = selectedPathRatio;
        PotentialBetterRoleUnprovenCount = potentialBetterRoleUnprovenCount;
        PotentialBetterRolePathProvenCount = potentialBetterRolePathProvenCount;
        CandidateMatrixComplete = candidateMatrixComplete;
        AltCandidateFeasibility = altCandidateFeasibility;
        DirectBotProfileIds = directBotProfileIds;
        Classification = classification;
        Reason = reason;
    }

    public string OwnerProfileId { get; }
    public string ThreatId { get; }
    public string AlternateCandidateId { get; }
    public string AlternateSeedBotProfileId { get; }
    public string AlternateCandidateSource { get; }
    public string AlternateApproachSector { get; }
    public Vector3 AlternateEstablishmentAnchor { get; }
    public Vector3 AlternateEstablishmentRegionCenter { get; }
    public float AlternateEstablishmentRegionRadiusMeters { get; }
    public float AlternateAxisEvidenceScore { get; }
    public int ObservedAxisCount { get; }
    public int FeasiblePairCount { get; }
    public string SelectedAltBotProfileId { get; }
    public string SelectedAltOperatorId { get; }
    public string SelectedAltSupportBotProfileId { get; }
    public string SelectedAltSupportOperatorId { get; }
    public bool SelectedAltSupportPathProven { get; }
    public float SelectedAltSupportPathLengthMeters { get; }
    public string Role { get; }
    public string Specialty { get; }
    public string BasePersona { get; }
    public string Doctrine { get; }
    public string Temperament { get; }
    public string RolePreferenceBand { get; }
    public bool SelectedAltPlanningViable { get; }
    public string PlanningViabilityReason { get; }
    public bool SelectedPairPathProven { get; }
    public float SelectedPathLengthMeters { get; }
    public float SelectedDirectDistanceMeters { get; }
    public float SelectedPathRatio { get; }
    public int PotentialBetterRoleUnprovenCount { get; }
    public int PotentialBetterRolePathProvenCount { get; }
    public bool CandidateMatrixComplete { get; }
    public IReadOnlyList<AltRouteFeasibilityObservation> AltCandidateFeasibility { get; }
    public IReadOnlyList<string> DirectBotProfileIds { get; }
    public string Classification { get; }
    public string Reason { get; }
    public bool HasShadowAltSelection => !string.IsNullOrWhiteSpace(SelectedAltBotProfileId) && SelectedPairPathProven;
    public bool HasShadowAltSupportSelection => !string.IsNullOrWhiteSpace(SelectedAltSupportBotProfileId) && SelectedAltSupportPathProven;
    public bool ExecutionAllowed => false;
    public bool RouteAssignmentApplied => false;
}

internal sealed class AltRouteFeasibilityObservation
{
    public AltRouteFeasibilityObservation(
        string axisCandidateId,
        string axisCandidateSource,
        string axisSeedBotProfileId,
        Vector3 establishmentAnchor,
        Vector3 establishmentRegionCenter,
        float establishmentRegionRadiusMeters,
        float axisEvidenceScore,
        string botProfileId,
        string operatorId,
        DateTimeOffset sourceEvidenceCapturedAtUtc,
        bool isAxisSeed,
        string role,
        string specialty,
        string rolePreferenceBand,
        int rolePreferenceRank,
        bool planningViable,
        string planningViabilityReason,
        string routeEvidenceKind,
        string pathStatus,
        float pathLengthMeters,
        float directDistanceMeters,
        float pathRatio,
        IReadOnlyList<Vector3> pathCorners,
        bool cacheHit,
        string classification,
        string reason)
    {
        AxisCandidateId = axisCandidateId;
        AxisCandidateSource = axisCandidateSource;
        AxisSeedBotProfileId = axisSeedBotProfileId;
        EstablishmentAnchor = establishmentAnchor;
        EstablishmentRegionCenter = establishmentRegionCenter;
        EstablishmentRegionRadiusMeters = establishmentRegionRadiusMeters;
        AxisEvidenceScore = axisEvidenceScore;
        BotProfileId = botProfileId;
        OperatorId = operatorId;
        SourceEvidenceCapturedAtUtc = sourceEvidenceCapturedAtUtc;
        IsAxisSeed = isAxisSeed;
        Role = role;
        Specialty = specialty;
        RolePreferenceBand = rolePreferenceBand;
        RolePreferenceRank = rolePreferenceRank;
        PlanningViable = planningViable;
        PlanningViabilityReason = planningViabilityReason;
        RouteEvidenceKind = routeEvidenceKind;
        PathStatus = pathStatus;
        PathLengthMeters = pathLengthMeters;
        DirectDistanceMeters = directDistanceMeters;
        PathRatio = pathRatio;
        PathCorners = pathCorners ?? Array.Empty<Vector3>();
        CacheHit = cacheHit;
        Classification = classification;
        Reason = reason;
    }

    public string AxisCandidateId { get; }
    public string AxisCandidateSource { get; }
    public string AxisSeedBotProfileId { get; }
    public Vector3 EstablishmentAnchor { get; }
    public Vector3 EstablishmentRegionCenter { get; }
    public float EstablishmentRegionRadiusMeters { get; }
    public float AxisEvidenceScore { get; }
    public string BotProfileId { get; }
    public string OperatorId { get; }
    public DateTimeOffset SourceEvidenceCapturedAtUtc { get; }
    public bool IsAxisSeed { get; }
    public string Role { get; }
    public string Specialty { get; }
    public string RolePreferenceBand { get; }
    public int RolePreferenceRank { get; }
    public bool PlanningViable { get; }
    public string PlanningViabilityReason { get; }
    public string RouteEvidenceKind { get; }
    public string PathStatus { get; }
    public float PathLengthMeters { get; }
    public float DirectDistanceMeters { get; }
    public float PathRatio { get; }
    public IReadOnlyList<Vector3> PathCorners { get; }
    public bool CacheHit { get; }
    public string Classification { get; }
    public string Reason { get; }

    // Hard-Condition result. Preferences are evaluated only after this is true.
    public bool CandidateSpecificRouteProven => PlanningViable
        && string.Equals(PathStatus, "complete", StringComparison.OrdinalIgnoreCase);
    public bool IsFeasiblePair => CandidateSpecificRouteProven;
    public bool ExecutionAllowed => false;
}
#endif
