#if SPT_CLIENT
using System;
using System.Collections.Generic;
using UnityEngine;

// Responsibility: Defines immutable Choke Commit + Support observations for the shared-plan contribution layer above Anti-Conga spacing.
// Flow: One coherent convergence/choke detection/route independence generation may yield a DirectCommit contribution for the leading Operator and a bounded StageHold contribution for one trailing Operator; the snapshot exposes plan identity and current contribution state only.
// Authority boundary: DirectCommit is permission/plan state, not a movement command. StageHold may suppress SearchAction translation through the shared TC bridge, while SAIN keeps local combat, target, aim, fire, suppression, cover and emergency authority.
// Invariant: Every commit support plan is choke/threat/generation-bound, requires a real support member, remains single-route in this tranche, and fails open on local danger, scheduler/path change, casualty, stale geometry or ownership conflict.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal sealed class ChokeCommitSupportSnapshot
{
    public static readonly ChokeCommitSupportSnapshot Empty = new(
        string.Empty,
        0,
        string.Empty,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        Array.Empty<ChokeCommitSupportPlanObservation>());

    public ChokeCommitSupportSnapshot(
        string mapId,
        long topologyRevision,
        string topologyContentHash,
        DateTimeOffset operatorBatchAtUtc,
        DateTimeOffset publishedAtUtc,
        IReadOnlyList<ChokeCommitSupportPlanObservation> activePlans)
    {
        MapId = mapId;
        TopologyRevision = topologyRevision;
        TopologyContentHash = topologyContentHash;
        OperatorBatchAtUtc = operatorBatchAtUtc;
        PublishedAtUtc = publishedAtUtc;
        ActivePlans = activePlans;
    }

    public string MapId { get; }
    public long TopologyRevision { get; }
    public string TopologyContentHash { get; }
    public DateTimeOffset OperatorBatchAtUtc { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public IReadOnlyList<ChokeCommitSupportPlanObservation> ActivePlans { get; }
    public bool IsAvailable => TopologyRevision > 0 && !string.IsNullOrWhiteSpace(MapId);
    public bool InfluenceEnabled => true;
}

internal sealed class ChokeCommitSupportPlanObservation
{
    public ChokeCommitSupportPlanObservation(
        string planId,
        string ownerProfileId,
        string threatId,
        string chokeId,
        string commitBotProfileId,
        string supportBotProfileId,
        IReadOnlyList<string> directHoldBotProfileIds,
        string supportSchedulerWindowId,
        long planGeneration,
        Vector3 stageAnchor,
        DateTimeOffset startedAtUtc,
        DateTimeOffset expiresAtUtc,
        string commitContribution,
        string supportContribution,
        string state)
    {
        PlanId = planId;
        OwnerProfileId = ownerProfileId;
        ThreatId = threatId;
        ChokeId = chokeId;
        CommitBotProfileId = commitBotProfileId;
        SupportBotProfileId = supportBotProfileId;
        DirectHoldBotProfileIds = directHoldBotProfileIds ?? Array.Empty<string>();
        SupportSchedulerWindowId = supportSchedulerWindowId;
        PlanGeneration = planGeneration;
        StageAnchor = stageAnchor;
        StartedAtUtc = startedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        CommitContribution = commitContribution;
        SupportContribution = supportContribution;
        State = state;
    }

    public string PlanId { get; }
    public string OwnerProfileId { get; }
    public string ThreatId { get; }
    public string ChokeId { get; }
    public string CommitBotProfileId { get; }
    public string SupportBotProfileId { get; }
    public IReadOnlyList<string> DirectHoldBotProfileIds { get; }
    public string SupportSchedulerWindowId { get; }
    public long PlanGeneration { get; }
    public Vector3 StageAnchor { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public string CommitContribution { get; }
    public string SupportContribution { get; }
    public string State { get; }
    public bool IsActive(DateTimeOffset now) => ExpiresAtUtc > now;
}
#endif
