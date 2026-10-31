#if SPT_CLIENT
using System;
using System.Collections.Generic;

// Responsibility: Defines immutable tactical replan observations over split-flank plan state and break conditions.
// Flow: Stable maneuver state and discontinuities are reduced to plan epochs, transition reasons and advisory break/replan classifications for later executors.
// Authority boundary: replan  is observation/advisory only; it cannot cancel SAIN, move an Operator, acquire a scheduler window, issue a hold, retarget a threat or execute DirectCommit.
// Invariant: Missing/stale upstream truth never becomes hidden execution authority; plan disappearance after meaningful establishment is surfaced as replan debt instead of silently resuming an old plan.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal sealed class TacticalReplanSnapshot
{
    public const string SemanticRevision = "direct_commit_lifecycle_closure";

    public static readonly TacticalReplanSnapshot Empty = new(
        DateTimeOffset.MinValue,
        Array.Empty<PlanLifecycleObservation>());

    public TacticalReplanSnapshot(
        DateTimeOffset publishedAtUtc,
        IReadOnlyList<PlanLifecycleObservation> plans)
    {
        PublishedAtUtc = publishedAtUtc;
        Plans = plans;
    }

    public DateTimeOffset PublishedAtUtc { get; }
    public IReadOnlyList<PlanLifecycleObservation> Plans { get; }
    public bool IsAvailable => PublishedAtUtc != DateTimeOffset.MinValue;
    public bool AdvisoryOnly => true;
    public bool ExecutionAllowed => false;
}

internal sealed class PlanLifecycleObservation
{
    public PlanLifecycleObservation(
        string ownerProfileId,
        string threatId,
        string alternateCandidateId,
        string alternateBotProfileId,
        long planEpoch,
        string currentState,
        string previousState,
        bool stateChanged,
        bool hardBreakRecommended,
        bool replanRecommended,
        bool commitReadyObserved,
        bool synchronizedHoldReleaseObserved,
        bool fullAltEstablishedObserved,
        DirectCommitExecutionReceipt? directCommitExecution,
        bool upstreamPresent,
        float upstreamMissingSeconds,
        string reason)
    {
        OwnerProfileId = ownerProfileId;
        ThreatId = threatId;
        AlternateCandidateId = alternateCandidateId;
        AlternateBotProfileId = alternateBotProfileId;
        PlanEpoch = planEpoch;
        CurrentState = currentState;
        PreviousState = previousState;
        StateChanged = stateChanged;
        HardBreakRecommended = hardBreakRecommended;
        ReplanRecommended = replanRecommended;
        CommitReadyObserved = commitReadyObserved;
        SynchronizedHoldReleaseObserved = synchronizedHoldReleaseObserved;
        FullAltEstablishedObserved = fullAltEstablishedObserved;
        DirectCommitExecution = directCommitExecution;
        UpstreamPresent = upstreamPresent;
        UpstreamMissingSeconds = upstreamMissingSeconds;
        Reason = reason;
    }

    public string OwnerProfileId { get; }
    public string ThreatId { get; }
    public string AlternateCandidateId { get; }
    public string AlternateBotProfileId { get; }
    public long PlanEpoch { get; }
    public string CurrentState { get; }
    public string PreviousState { get; }
    public bool StateChanged { get; }
    public bool HardBreakRecommended { get; }
    public bool ReplanRecommended { get; }
    public bool CommitReadyObserved { get; }
    public bool SynchronizedHoldReleaseObserved { get; }
    public bool FullAltEstablishedObserved { get; }
    public DirectCommitExecutionReceipt? DirectCommitExecution { get; }
    public bool DirectCommitExecutionObserved => DirectCommitExecution != null;
    public bool DirectCommitExecutionTerminalObserved => DirectCommitExecution?.IsTerminal == true;
    public bool DirectCommitExecutionCompletedObserved => DirectCommitExecution?.IsCompleted == true;
    public bool DirectCommitExecutionRequiresFreshPlanEpoch => DirectCommitExecution?.RequiresFreshPlanEpoch == true;
    public bool UpstreamPresent { get; }
    public float UpstreamMissingSeconds { get; }
    public string Reason { get; }
    public bool MutationApplied => false;
}
#endif
