#if SPT_CLIENT
using System;
using System.Collections.Generic;

// Responsibility: Defines immutable synchronized split-flank readiness observations above the spatial-dependency shadow.
// Flow: Spatial establishment + route assignment physical-development provenance + current threat/SAIN evidence + Direct-element readiness are fused into strict FullAlt/SynchronizedCommit state plus a separate bounded near-commit approach permission and exact current-PlanEpoch terminal cycle truth.
// Authority boundary:  publishes plan/readiness truth and may release an already-existing commit support Vanguard StageHold/DirectHold for the same owner/threat once synchronized readiness is stable. It does not issue movement, create a new hold, mutate SAIN, grant DirectCommit execution, issue fire/suppression commands or synchronize local combat actions.
// Invariant: FullAltEstablished keeps its strict stable spatial-establishment + pressure semantics. SynchronizedApproachCommitReady never asserts D5/D6; it only exposes one bounded Direct permission when the physically developed alternate is within one DirectCommit slice of establishment and current hard-safety/direct readiness still hold. A completed DirectCommit cycle is exact to the current PlanEpoch and consumes further synchronized permission for that epoch.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal sealed class SplitFlankStateSnapshot
{
    public const string SemanticRevision = "split_flank_state_terminal_cycle_closure";

    public static readonly SplitFlankStateSnapshot Empty = new(
        string.Empty,
        0,
        string.Empty,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        Array.Empty<ThreatGroupSplitFlankStateObservation>());

    public SplitFlankStateSnapshot(
        string mapId,
        long topologyRevision,
        string topologyContentHash,
        DateTimeOffset sourcePublishedAtUtc,
        DateTimeOffset publishedAtUtc,
        IReadOnlyList<ThreatGroupSplitFlankStateObservation> threatGroups)
    {
        MapId = mapId;
        TopologyRevision = topologyRevision;
        TopologyContentHash = topologyContentHash;
        SourcePublishedAtUtc = sourcePublishedAtUtc;
        PublishedAtUtc = publishedAtUtc;
        ThreatGroups = threatGroups;
    }

    public string MapId { get; }
    public long TopologyRevision { get; }
    public string TopologyContentHash { get; }
    public DateTimeOffset SourcePublishedAtUtc { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public IReadOnlyList<ThreatGroupSplitFlankStateObservation> ThreatGroups { get; }
    public bool IsAvailable => TopologyRevision > 0 && !string.IsNullOrWhiteSpace(MapId);
    public bool PlanStateOnly => true;
    public bool ExecutionAllowed => false;
    public bool MovementMutationAllowed => false;
    public bool DirectCommitExecutionAllowed => false;
    public bool ExistingVanguardHoldReleaseAllowed => true;
}

internal sealed class ThreatGroupSplitFlankStateObservation
{
    public ThreatGroupSplitFlankStateObservation(
        string ownerProfileId,
        string threatId,
        string alternateCandidateId,
        string alternateBotProfileId,
        bool assignmentChanged,
        float assignmentAgeSeconds,
        bool alternateDevelopmentObserved,
        bool alternateDevelopmentCompletedObserved,
        bool alternatePhysicalProgressObserved,
        string alternatePhysicalProgressKind,
        float alternateClosestRegionDistanceMeters,
        string alternateDevelopmentState,
        string alternateDevelopmentReason,
        bool positionEstablishedObserved,
        bool alternateNearCommitObserved,
        float alternateNearCommitThresholdMeters,
        bool pressureProofObserved,
        string pressureProofReason,
        bool alternateHardSafetyReady,
        string alternateHardSafetyReason,
        int fullAltStableSamples,
        float fullAltStableDwellSeconds,
        bool fullAltRetainedByGrace,
        bool fullAltEstablished,
        string fullAltEstablishmentSource,
        int directMemberCount,
        int directCommitReadyCount,
        int directCommitBlockedCount,
        IReadOnlyList<string> directCommitBlockingReasons,
        bool synchronizedApproachCommitRawReady,
        int synchronizedApproachCommitStableSamples,
        float synchronizedApproachCommitStableDwellSeconds,
        bool synchronizedApproachCommitReady,
        bool synchronizedCommitRawReady,
        int synchronizedCommitStableSamples,
        float synchronizedCommitStableDwellSeconds,
        bool synchronizedCommitReady,
        bool synchronizedHoldReleaseApplied,
        int synchronizedHoldReleasePlanCount,
        int synchronizedHoldReleaseDirectHoldCount,
        bool directCommitCycleCompletedObserved,
        long directCommitCyclePlanEpoch,
        string directCommitCycleCompletionReason,
        string classification,
        string reason)
    {
        OwnerProfileId = ownerProfileId;
        ThreatId = threatId;
        AlternateCandidateId = alternateCandidateId;
        AlternateBotProfileId = alternateBotProfileId;
        AssignmentChanged = assignmentChanged;
        AssignmentAgeSeconds = assignmentAgeSeconds;
        AlternateDevelopmentObserved = alternateDevelopmentObserved;
        AlternateDevelopmentCompletedObserved = alternateDevelopmentCompletedObserved;
        AlternatePhysicalProgressObserved = alternatePhysicalProgressObserved;
        AlternatePhysicalProgressKind = alternatePhysicalProgressKind;
        AlternateClosestRegionDistanceMeters = alternateClosestRegionDistanceMeters;
        AlternateDevelopmentState = alternateDevelopmentState;
        AlternateDevelopmentReason = alternateDevelopmentReason;
        PositionEstablishedObserved = positionEstablishedObserved;
        AlternateNearCommitObserved = alternateNearCommitObserved;
        AlternateNearCommitThresholdMeters = alternateNearCommitThresholdMeters;
        PressureProofObserved = pressureProofObserved;
        PressureProofReason = pressureProofReason;
        AlternateHardSafetyReady = alternateHardSafetyReady;
        AlternateHardSafetyReason = alternateHardSafetyReason;
        FullAltStableSamples = fullAltStableSamples;
        FullAltStableDwellSeconds = fullAltStableDwellSeconds;
        FullAltRetainedByGrace = fullAltRetainedByGrace;
        FullAltEstablished = fullAltEstablished;
        FullAltEstablishmentSource = fullAltEstablishmentSource;
        DirectMemberCount = directMemberCount;
        DirectCommitReadyCount = directCommitReadyCount;
        DirectCommitBlockedCount = directCommitBlockedCount;
        DirectCommitBlockingReasons = directCommitBlockingReasons;
        SynchronizedApproachCommitRawReady = synchronizedApproachCommitRawReady;
        SynchronizedApproachCommitStableSamples = synchronizedApproachCommitStableSamples;
        SynchronizedApproachCommitStableDwellSeconds = synchronizedApproachCommitStableDwellSeconds;
        SynchronizedApproachCommitReady = synchronizedApproachCommitReady;
        SynchronizedCommitRawReady = synchronizedCommitRawReady;
        SynchronizedCommitStableSamples = synchronizedCommitStableSamples;
        SynchronizedCommitStableDwellSeconds = synchronizedCommitStableDwellSeconds;
        SynchronizedCommitReady = synchronizedCommitReady;
        SynchronizedHoldReleaseApplied = synchronizedHoldReleaseApplied;
        SynchronizedHoldReleasePlanCount = synchronizedHoldReleasePlanCount;
        SynchronizedHoldReleaseDirectHoldCount = synchronizedHoldReleaseDirectHoldCount;
        DirectCommitCycleCompletedObserved = directCommitCycleCompletedObserved;
        DirectCommitCyclePlanEpoch = directCommitCyclePlanEpoch;
        DirectCommitCycleCompletionReason = directCommitCycleCompletionReason;
        Classification = classification;
        Reason = reason;
    }

    public string OwnerProfileId { get; }
    public string ThreatId { get; }
    public string AlternateCandidateId { get; }
    public string AlternateBotProfileId { get; }
    public bool AssignmentChanged { get; }
    public float AssignmentAgeSeconds { get; }
    public bool AlternateDevelopmentObserved { get; }
    public bool AlternateDevelopmentCompletedObserved { get; }
    public bool AlternatePhysicalProgressObserved { get; }
    public string AlternatePhysicalProgressKind { get; }
    public float AlternateClosestRegionDistanceMeters { get; }
    public string AlternateDevelopmentState { get; }
    public string AlternateDevelopmentReason { get; }
    public bool PositionEstablishedObserved { get; }
    public bool AlternateNearCommitObserved { get; }
    public float AlternateNearCommitThresholdMeters { get; }
    public bool PressureProofObserved { get; }
    public string PressureProofReason { get; }
    public bool AlternateHardSafetyReady { get; }
    public string AlternateHardSafetyReason { get; }
    public int FullAltStableSamples { get; }
    public float FullAltStableDwellSeconds { get; }
    public bool FullAltRetainedByGrace { get; }
    public bool FullAltEstablished { get; }
    public string FullAltEstablishmentSource { get; }
    public int DirectMemberCount { get; }
    public int DirectCommitReadyCount { get; }
    public int DirectCommitBlockedCount { get; }
    public IReadOnlyList<string> DirectCommitBlockingReasons { get; }
    public bool SynchronizedApproachCommitRawReady { get; }
    public int SynchronizedApproachCommitStableSamples { get; }
    public float SynchronizedApproachCommitStableDwellSeconds { get; }
    public bool SynchronizedApproachCommitReady { get; }
    public bool SynchronizedCommitRawReady { get; }
    public int SynchronizedCommitStableSamples { get; }
    public float SynchronizedCommitStableDwellSeconds { get; }
    public bool SynchronizedCommitReady { get; }
    public bool SynchronizedHoldReleaseApplied { get; }
    public int SynchronizedHoldReleasePlanCount { get; }
    public int SynchronizedHoldReleaseDirectHoldCount { get; }
    public bool DirectCommitCycleCompletedObserved { get; }
    public long DirectCommitCyclePlanEpoch { get; }
    public string DirectCommitCycleCompletionReason { get; }
    public string Classification { get; }
    public string Reason { get; }
    public bool DirectCommitPermissionObserved => SynchronizedCommitReady || SynchronizedApproachCommitReady;
    public bool ExistingVanguardHoldReleaseObserved => SynchronizedHoldReleaseApplied;
    public bool ExecutionAllowed => false;
}
#endif
