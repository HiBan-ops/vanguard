#if SPT_CLIENT
using System;
using System.Collections.Generic;

// Responsibility: Publishes immutable receipts for bounded DirectCommit physical execution, separate from split-flank readiness.
// Flow: A receipt is written when a DirectCommit lease starts and replaced by its exact terminal outcome for owner + threat + PlanEpoch + readiness phase, including the bounded movement mode actually used (ordinary advance or confirmed HardChoke entry).
// Authority boundary: execution receipts are observation truth only; they never grant movement, scheduler, SAIN, aim, fire or target authority.
// Invariant: Receipt identity is exact to plan epoch and readiness phase, both approach/full_alt phases may coexist for one epoch, and retained history is bounded per raid.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal sealed class DirectCommitExecutionSnapshot
{
    public static readonly DirectCommitExecutionSnapshot Empty = new(
        DateTimeOffset.MinValue,
        Array.Empty<DirectCommitExecutionReceipt>());

    public DirectCommitExecutionSnapshot(
        DateTimeOffset publishedAtUtc,
        IReadOnlyList<DirectCommitExecutionReceipt> executions)
    {
        PublishedAtUtc = publishedAtUtc;
        Executions = executions;
    }

    public DateTimeOffset PublishedAtUtc { get; }
    public IReadOnlyList<DirectCommitExecutionReceipt> Executions { get; }
    public bool IsAvailable => PublishedAtUtc != DateTimeOffset.MinValue;
}

internal sealed class DirectCommitExecutionReceipt
{
    public DirectCommitExecutionReceipt(
        string ownerProfileId,
        string threatId,
        long planEpoch,
        string readinessMode,
        string movementMode,
        string alternateCandidateId,
        string alternateBotProfileId,
        string operatorId,
        string botProfileId,
        string leaseId,
        long generation,
        string state,
        string reason,
        DateTimeOffset startedAtUtc,
        DateTimeOffset observedAtUtc,
        float physicalMeters,
        int nativeTicks)
    {
        OwnerProfileId = ownerProfileId;
        ThreatId = threatId;
        PlanEpoch = planEpoch;
        ReadinessMode = readinessMode;
        MovementMode = movementMode;
        AlternateCandidateId = alternateCandidateId;
        AlternateBotProfileId = alternateBotProfileId;
        OperatorId = operatorId;
        BotProfileId = botProfileId;
        LeaseId = leaseId;
        Generation = generation;
        State = state;
        Reason = reason;
        StartedAtUtc = startedAtUtc;
        ObservedAtUtc = observedAtUtc;
        PhysicalMeters = physicalMeters;
        NativeTicks = nativeTicks;
    }

    public string OwnerProfileId { get; }
    public string ThreatId { get; }
    public long PlanEpoch { get; }
    public string ReadinessMode { get; }
    public string MovementMode { get; }
    public string AlternateCandidateId { get; }
    public string AlternateBotProfileId { get; }
    public string OperatorId { get; }
    public string BotProfileId { get; }
    public string LeaseId { get; }
    public long Generation { get; }
    public string State { get; }
    public string Reason { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public float PhysicalMeters { get; }
    public int NativeTicks { get; }
    public bool IsActive => string.Equals(State, "active", StringComparison.OrdinalIgnoreCase);
    public bool IsCompleted => string.Equals(State, "completed", StringComparison.OrdinalIgnoreCase);
    public bool IsTerminal => !IsActive;
    public bool WasDrainedByTacticalReplan => Reason.StartsWith("tactical_replan:", StringComparison.OrdinalIgnoreCase);
    public bool RequiresFreshPlanEpoch => IsTerminal && !IsCompleted && !WasDrainedByTacticalReplan;
}
#endif
