#if SPT_CLIENT
using System;
using System.Collections.Generic;

// Responsibility: immutable shadow-only calibration results derived from the exact assignment-aligned threat-exposure publication.
// Flow: ThreatExposureShadowClassificationService evaluates provisional sensitivity probes over one exact selected Direct element + selected Alt Operator/path and records temporal stability for that exact assignment identity.
// Authority boundary: Data only. Candidate labels are calibration probes, not tactical policy; no route preference, assignment, movement, replan, target or SAIN mutation is authorized.
// Invariant: A stable candidate never outlives or crosses an assignment identity/provenance change. Missing, expired or mismatched upstream evidence remains unclassified rather than being interpreted as clear/safe.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal sealed class ThreatExposureShadowSnapshot
{
    public static readonly ThreatExposureShadowSnapshot Empty = new(
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
        false,
        Array.Empty<ThreatExposureShadowGroupObservation>());

    public ThreatExposureShadowSnapshot(
        string mapId,
        long topologyRevision,
        string topologyContentHash,
        DateTimeOffset operatorBatchAtUtc,
        DateTimeOffset convergencePublishedAtUtc,
        DateTimeOffset chokePublishedAtUtc,
        DateTimeOffset exposurePublishedAtUtc,
        DateTimeOffset routeAssignmentPublishedAtUtc,
        DateTimeOffset assignmentAlignmentPublishedAtUtc,
        DateTimeOffset publishedAtUtc,
        bool chokeEvidenceCoherent,
        IReadOnlyList<ThreatExposureShadowGroupObservation> groups)
    {
        MapId = mapId;
        TopologyRevision = topologyRevision;
        TopologyContentHash = topologyContentHash;
        OperatorBatchAtUtc = operatorBatchAtUtc;
        ConvergencePublishedAtUtc = convergencePublishedAtUtc;
        ChokePublishedAtUtc = chokePublishedAtUtc;
        ExposurePublishedAtUtc = exposurePublishedAtUtc;
        RouteAssignmentPublishedAtUtc = routeAssignmentPublishedAtUtc;
        AssignmentAlignmentPublishedAtUtc = assignmentAlignmentPublishedAtUtc;
        PublishedAtUtc = publishedAtUtc;
        ChokeEvidenceCoherent = chokeEvidenceCoherent;
        Groups = groups;
    }

    public string MapId { get; }
    public long TopologyRevision { get; }
    public string TopologyContentHash { get; }
    public DateTimeOffset OperatorBatchAtUtc { get; }
    public DateTimeOffset ConvergencePublishedAtUtc { get; }
    public DateTimeOffset ChokePublishedAtUtc { get; }
    public DateTimeOffset ExposurePublishedAtUtc { get; }
    public DateTimeOffset RouteAssignmentPublishedAtUtc { get; }
    public DateTimeOffset AssignmentAlignmentPublishedAtUtc { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public bool ChokeEvidenceCoherent { get; }
    public IReadOnlyList<ThreatExposureShadowGroupObservation> Groups { get; }
    public bool IsAvailable => TopologyRevision > 0 && !string.IsNullOrWhiteSpace(MapId);
    public bool ExecutionAllowed => false;
}

internal sealed class ThreatExposureShadowGroupObservation
{
    public ThreatExposureShadowGroupObservation(
        string ownerProfileId,
        string threatId,
        string alternateCandidateId,
        string selectedAltBotProfileId,
        string selectedAltOperatorId,
        IReadOnlyList<string> directBotProfileIds,
        DateTimeOffset contactExpiresAtUtc,
        DateTimeOffset selectedPairSourceEvidenceCapturedAtUtc,
        float directEvidenceMean,
        float directEvidenceMax,
        float directForwardVisibleRatioMean,
        float alternateEvidenceMean,
        float directVsAlternateDelta,
        bool chokeEvidenceAvailable,
        bool chokePresent,
        bool confirmedHardChokePresent,
        string assignmentIdentity,
        IReadOnlyList<ThreatExposureShadowProfileObservation> profiles)
    {
        OwnerProfileId = ownerProfileId;
        ThreatId = threatId;
        AlternateCandidateId = alternateCandidateId;
        SelectedAltBotProfileId = selectedAltBotProfileId;
        SelectedAltOperatorId = selectedAltOperatorId;
        DirectBotProfileIds = directBotProfileIds;
        ContactExpiresAtUtc = contactExpiresAtUtc;
        SelectedPairSourceEvidenceCapturedAtUtc = selectedPairSourceEvidenceCapturedAtUtc;
        DirectEvidenceMean = directEvidenceMean;
        DirectEvidenceMax = directEvidenceMax;
        DirectForwardVisibleRatioMean = directForwardVisibleRatioMean;
        AlternateEvidenceMean = alternateEvidenceMean;
        DirectVsAlternateDelta = directVsAlternateDelta;
        ChokeEvidenceAvailable = chokeEvidenceAvailable;
        ChokePresent = chokePresent;
        ConfirmedHardChokePresent = confirmedHardChokePresent;
        AssignmentIdentity = assignmentIdentity;
        Profiles = profiles;
    }

    public string OwnerProfileId { get; }
    public string ThreatId { get; }
    public string AlternateCandidateId { get; }
    public string SelectedAltBotProfileId { get; }
    public string SelectedAltOperatorId { get; }
    public IReadOnlyList<string> DirectBotProfileIds { get; }
    public DateTimeOffset ContactExpiresAtUtc { get; }
    public DateTimeOffset SelectedPairSourceEvidenceCapturedAtUtc { get; }
    public float DirectEvidenceMean { get; }
    public float DirectEvidenceMax { get; }
    public float DirectForwardVisibleRatioMean { get; }
    public bool AlternateEvidenceAvailable => true;
    public float AlternateEvidenceMean { get; }
    public float DirectVsAlternateDelta { get; }
    public bool ChokeEvidenceAvailable { get; }
    public bool ChokePresent { get; }
    public bool ConfirmedHardChokePresent { get; }
    public string AssignmentIdentity { get; }
    public IReadOnlyList<ThreatExposureShadowProfileObservation> Profiles { get; }
    public bool ExecutionAllowed => false;
}

internal sealed class ThreatExposureShadowProfileObservation
{
    public ThreatExposureShadowProfileObservation(
        string profileId,
        bool directExposureCandidate,
        bool altReliefCandidate,
        bool heldAccessCandidate,
        bool splitOpportunityCandidate,
        int directStableSamples,
        double directStableSeconds,
        bool directStableQualified,
        int heldAccessStableSamples,
        double heldAccessStableSeconds,
        bool heldAccessStableQualified,
        int splitStableSamples,
        double splitStableSeconds,
        bool splitStableQualified)
    {
        ProfileId = profileId;
        DirectExposureCandidate = directExposureCandidate;
        AltReliefCandidate = altReliefCandidate;
        HeldAccessCandidate = heldAccessCandidate;
        SplitOpportunityCandidate = splitOpportunityCandidate;
        DirectStableSamples = directStableSamples;
        DirectStableSeconds = directStableSeconds;
        DirectStableQualified = directStableQualified;
        HeldAccessStableSamples = heldAccessStableSamples;
        HeldAccessStableSeconds = heldAccessStableSeconds;
        HeldAccessStableQualified = heldAccessStableQualified;
        SplitStableSamples = splitStableSamples;
        SplitStableSeconds = splitStableSeconds;
        SplitStableQualified = splitStableQualified;
    }

    public string ProfileId { get; }
    public bool DirectExposureCandidate { get; }
    public bool AltReliefCandidate { get; }
    public bool HeldAccessCandidate { get; }
    public bool SplitOpportunityCandidate { get; }
    public int DirectStableSamples { get; }
    public double DirectStableSeconds { get; }
    public bool DirectStableQualified { get; }
    public int HeldAccessStableSamples { get; }
    public double HeldAccessStableSeconds { get; }
    public bool HeldAccessStableQualified { get; }
    public int SplitStableSamples { get; }
    public double SplitStableSeconds { get; }
    public bool SplitStableQualified { get; }
    public bool ExecutionAllowed => false;
}
#endif
