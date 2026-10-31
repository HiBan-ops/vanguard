#if SPT_CLIENT
using System;
using System.Collections.Generic;
using UnityEngine;

// Responsibility: Defines immutable Anti-Conga Advance Gate observations for the first bounded Tactical Coordinator influence.
// Flow: One coherent convergence/choke detection/route independence generation may admit at most one short translation gate per owner/threat/choke group; the snapshot exposes only bounded gate state and identity.
// Authority boundary: The models do not move Operators or replace SAIN combat. Physical influence exists only while the anti-conga service owns an exact generation-stamped gate admitted by the main scheduler.
// Invariant: Every gate is TTL-bounded, target/choke/generation-bound, fail-open on ambiguity, and cannot silently resume after release or replacement.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal sealed class AntiCongaSnapshot
{
    public static readonly AntiCongaSnapshot Empty = new(
        string.Empty,
        0,
        string.Empty,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        Array.Empty<AntiCongaLeaseObservation>());

    public AntiCongaSnapshot(
        string mapId,
        long topologyRevision,
        string topologyContentHash,
        DateTimeOffset operatorBatchAtUtc,
        DateTimeOffset publishedAtUtc,
        IReadOnlyList<AntiCongaLeaseObservation> activeLeases)
    {
        MapId = mapId;
        TopologyRevision = topologyRevision;
        TopologyContentHash = topologyContentHash;
        OperatorBatchAtUtc = operatorBatchAtUtc;
        PublishedAtUtc = publishedAtUtc;
        ActiveLeases = activeLeases;
    }

    public string MapId { get; }
    public long TopologyRevision { get; }
    public string TopologyContentHash { get; }
    public DateTimeOffset OperatorBatchAtUtc { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public IReadOnlyList<AntiCongaLeaseObservation> ActiveLeases { get; }
    public bool IsAvailable => TopologyRevision > 0 && !string.IsNullOrWhiteSpace(MapId);
    public bool InfluenceEnabled => true;
}

internal sealed class AntiCongaLeaseObservation
{
    public AntiCongaLeaseObservation(
        string leaseId,
        string ownerProfileId,
        string threatId,
        string chokeId,
        string leadBotProfileId,
        string heldBotProfileId,
        string schedulerWindowId,
        long gateGeneration,
        Vector3 gateAnchor,
        DateTimeOffset startedAtUtc,
        DateTimeOffset expiresAtUtc,
        string state)
    {
        LeaseId = leaseId;
        OwnerProfileId = ownerProfileId;
        ThreatId = threatId;
        ChokeId = chokeId;
        LeadBotProfileId = leadBotProfileId;
        HeldBotProfileId = heldBotProfileId;
        SchedulerWindowId = schedulerWindowId;
        GateGeneration = gateGeneration;
        GateAnchor = gateAnchor;
        StartedAtUtc = startedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        State = state;
    }

    public string LeaseId { get; }
    public string OwnerProfileId { get; }
    public string ThreatId { get; }
    public string ChokeId { get; }
    public string LeadBotProfileId { get; }
    public string HeldBotProfileId { get; }
    public string SchedulerWindowId { get; }
    public long GateGeneration { get; }
    public Vector3 GateAnchor { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public string State { get; }
    public bool IsActive(DateTimeOffset now) => ExpiresAtUtc > now;
}
#endif
