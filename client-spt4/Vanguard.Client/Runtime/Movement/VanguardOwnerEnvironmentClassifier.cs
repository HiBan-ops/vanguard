#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using UnityEngine.AI;

// Responsibility: Classifies the player/owner local environment so squad movement can distinguish indoor, constrained, transition and open-space behavior.
// Flow: Navmesh/physics and nearby geometry evidence are sampled around the owner, reduced to stable environment features and cached briefly for cohesion/placement policies.
// Authority boundary: Classification supplies movement context only; it does not issue movement commands or override pathfinding authority.
// Invariant: Sampling is bounded, transient missing geometry falls back conservatively, and cached classification expires quickly enough to follow real environment transitions.
namespace Vanguard.Client.Runtime.Movement;

/// <summary>
/// Vanguard classifies the owner's local enclosure from the owner's physical position and applies
/// a bounded temporal consensus before the interior planner consumes the result. It never reads an
/// Operator-majority state and never mutates movement, SAIN, medical, loot, or follow authority.
/// </summary>
internal static class VanguardOwnerEnvironmentClassifier
{
    public const float IndoorCeilingProbeMeters = 12.0f;
    public const float MinimumValidOverheadHitMeters = 0.35f;
    public const float MaximumCeilingNormalY = -0.15f;
    public const float LateralProbeMeters = 6.5f;
    public const int MinimumIndoorEnclosureHits = 4;
    public const int MinimumSemiCoveredHits = 2;
    public const float TopologyOpenProbeMeters = 8.0f;
    public const float FloorBandMeters = 3.0f;
    public const float LargeOpenInteriorCeilingMeters = 6.0f;
    public const int LargeOpenInteriorMinimumOpenDirections = 4;
    public const int LateralIndoorEntryConfirmationSamples = 2;
    public const int IndoorExitConfirmationSamples = 3;
    public const float StabilityMovementResetMeters = 2.50f;
    public const float StrongIndoorEvidenceHoldSeconds = 2.00f;

    private static readonly object PhysicsProbeSync = new object();
    private static readonly object StabilitySync = new object();
    private static readonly RaycastHit[] OverheadHits = new RaycastHit[24];
    private static readonly Dictionary<string, EnvironmentStabilityState> StabilityByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Vector3[] ProbeDirections =
    {
        Vector3.forward,
        Vector3.back,
        Vector3.left,
        Vector3.right,
        (Vector3.forward + Vector3.left).normalized,
        (Vector3.forward + Vector3.right).normalized,
        (Vector3.back + Vector3.left).normalized,
        (Vector3.back + Vector3.right).normalized,
    };

    public static void Reset()
    {
        lock (StabilitySync)
        {
            StabilityByOwner.Clear();
        }
    }

    public static VanguardOwnerEnvironmentSnapshot ClassifyStable(
        string ownerProfileId,
        Vector3 ownerPosition,
        Vector3 ownerForward,
        DateTimeOffset now,
        out VanguardOwnerEnvironmentSnapshot rawSnapshot,
        out string stabilityReason)
    {
        rawSnapshot = Classify(ownerPosition, ownerForward);
        string owner = string.IsNullOrWhiteSpace(ownerProfileId) ? "none" : ownerProfileId.Trim();
        lock (StabilitySync)
        {
            if (!StabilityByOwner.TryGetValue(owner, out EnvironmentStabilityState state))
            {
                bool initialStrongIndoor = rawSnapshot.Enclosure == VanguardOwnerEnclosure.Indoor
                    && rawSnapshot.CeilingDetected;
                if (rawSnapshot.Enclosure == VanguardOwnerEnclosure.Indoor && !initialStrongIndoor)
                {
                    VanguardOwnerEnvironmentSnapshot transition = BuildHeldSnapshot(
                        rawSnapshot,
                        rawSnapshot,
                        VanguardOwnerEnclosure.Transition,
                        "initial_lateral_indoor_waiting_for_consensus_1");
                    state = new EnvironmentStabilityState(transition, ownerPosition, now);
                    state.ObservePending(VanguardOwnerEnclosure.Indoor);
                    StabilityByOwner[owner] = state;
                    stabilityReason = "initial_lateral_indoor_waiting_for_consensus_1";
                    return transition;
                }

                state = new EnvironmentStabilityState(rawSnapshot, ownerPosition, now);
                StabilityByOwner[owner] = state;
                stabilityReason = initialStrongIndoor
                    ? "initial_strong_ceiling_evidence"
                    : "initial_non_indoor_sample";
                return rawSnapshot;
            }

            float moved = HorizontalDistance(ownerPosition, state.LastOwnerPosition);
            bool rawIndoor = rawSnapshot.Enclosure == VanguardOwnerEnclosure.Indoor;
            bool rawStrongIndoor = rawIndoor && rawSnapshot.CeilingDetected;
            if (moved >= StabilityMovementResetMeters)
            {
                if (rawIndoor && !rawStrongIndoor)
                {
                    VanguardOwnerEnvironmentSnapshot transition = BuildHeldSnapshot(
                        rawSnapshot,
                        rawSnapshot,
                        VanguardOwnerEnclosure.Transition,
                        "location_changed_lateral_indoor_waiting_for_consensus_1");
                    state.ReplaceStable(transition, ownerPosition, now, strongIndoor: false);
                    state.ObservePending(VanguardOwnerEnclosure.Indoor);
                    stabilityReason = "location_changed_lateral_indoor_waiting_for_consensus_1";
                    return transition;
                }

                state.ReplaceStable(rawSnapshot, ownerPosition, now, rawStrongIndoor);
                stabilityReason = rawStrongIndoor
                    ? "location_changed_strong_ceiling_evidence"
                    : "location_changed_non_indoor_sample";
                return rawSnapshot;
            }

            bool stableIndoor = state.StableSnapshot.Enclosure == VanguardOwnerEnclosure.Indoor;
            if (rawStrongIndoor)
            {
                state.Accept(rawSnapshot, ownerPosition, now, strongIndoor: true);
                stabilityReason = "strong_ceiling_evidence_immediate";
                return rawSnapshot;
            }

            if (stableIndoor)
            {
                if (rawIndoor)
                {
                    state.Accept(rawSnapshot, ownerPosition, now, strongIndoor: false);
                    stabilityReason = "indoor_consensus_refreshed";
                    return rawSnapshot;
                }

                // Exit consensus is binary: any consecutive non-indoor raw sample contributes,
                // even when noise alternates between SemiCovered and Outdoor.
                state.ObservePending(VanguardOwnerEnclosure.Outdoor);
                bool strongEvidenceStillFresh = (now - state.LastStrongIndoorAtUtc).TotalSeconds <= StrongIndoorEvidenceHoldSeconds
                    && moved < StabilityMovementResetMeters;
                if (strongEvidenceStillFresh || state.PendingCount < IndoorExitConfirmationSamples)
                {
                    state.UpdatePose(ownerPosition, now);
                    stabilityReason = strongEvidenceStillFresh
                        ? "strong_indoor_evidence_sticky"
                        : "indoor_exit_waiting_for_consensus_" + state.PendingCount;
                    return BuildHeldSnapshot(
                        state.StableSnapshot,
                        rawSnapshot,
                        state.PendingCount >= 2 ? VanguardOwnerEnclosure.Transition : VanguardOwnerEnclosure.Indoor,
                        stabilityReason);
                }

                state.Accept(rawSnapshot, ownerPosition, now, strongIndoor: false);
                stabilityReason = "indoor_exit_consensus_reached";
                return rawSnapshot;
            }

            if (rawIndoor)
            {
                state.ObservePending(VanguardOwnerEnclosure.Indoor);
                if (state.PendingCount < LateralIndoorEntryConfirmationSamples)
                {
                    state.UpdatePose(ownerPosition, now);
                    stabilityReason = "lateral_indoor_entry_waiting_for_consensus_" + state.PendingCount;
                    return BuildHeldSnapshot(
                        state.StableSnapshot,
                        rawSnapshot,
                        VanguardOwnerEnclosure.Transition,
                        stabilityReason);
                }

                state.Accept(rawSnapshot, ownerPosition, now, strongIndoor: false);
                stabilityReason = "lateral_indoor_entry_consensus_reached";
                return rawSnapshot;
            }

            state.Accept(rawSnapshot, ownerPosition, now, strongIndoor: false);
            stabilityReason = "non_indoor_consensus_refreshed";
            return rawSnapshot;
        }
    }

    public static VanguardOwnerEnvironmentSnapshot Classify(Vector3 ownerPosition, Vector3 ownerForward)
    {
        Vector3 navMeshPosition = ownerPosition;
        bool navMeshProjected = NavMesh.SamplePosition(ownerPosition + (Vector3.up * 0.20f), out NavMeshHit navHit, 2.0f, NavMesh.AllAreas);
        if (navMeshProjected)
        {
            navMeshPosition = navHit.position;
        }

        bool ceilingDetected = TryFindOverheadCeiling(ownerPosition, out float ceilingDistance);
        int lateralHitCount = CountLateralEnvironmentHits(ownerPosition);
        int openDirectionCount = CountOpenNavMeshDirections(navMeshPosition);

        VanguardOwnerEnclosure enclosure;
        float confidence;
        string reason;
        if (ceilingDetected)
        {
            enclosure = VanguardOwnerEnclosure.Indoor;
            confidence = 1.0f;
            reason = "validated_overhead_ceiling";
        }
        else if (lateralHitCount >= MinimumIndoorEnclosureHits)
        {
            enclosure = VanguardOwnerEnclosure.Indoor;
            confidence = 0.76f;
            reason = "lateral_enclosure";
        }
        else if (lateralHitCount >= MinimumSemiCoveredHits)
        {
            enclosure = VanguardOwnerEnclosure.SemiCovered;
            confidence = 0.58f;
            reason = "partial_lateral_enclosure";
        }
        else
        {
            enclosure = VanguardOwnerEnclosure.Outdoor;
            confidence = 0.72f;
            reason = "open_environment";
        }

        VanguardOwnerTopologyHint topology = ResolveTopology(enclosure, ceilingDetected, ceilingDistance, lateralHitCount, openDirectionCount);
        int floorBand = (int)Math.Round(navMeshPosition.y / FloorBandMeters, MidpointRounding.AwayFromZero);
        Vector3 flattenedForward = new Vector3(ownerForward.x, 0f, ownerForward.z);
        if (flattenedForward.sqrMagnitude <= 0.001f)
        {
            flattenedForward = Vector3.forward;
        }
        flattenedForward.Normalize();

        return new VanguardOwnerEnvironmentSnapshot(
            enclosure,
            topology,
            ownerPosition,
            navMeshPosition,
            flattenedForward,
            navMeshProjected,
            ceilingDetected,
            ceilingDistance,
            lateralHitCount,
            openDirectionCount,
            floorBand,
            confidence,
            reason);
    }

    private static VanguardOwnerTopologyHint ResolveTopology(
        VanguardOwnerEnclosure enclosure,
        bool ceilingDetected,
        float ceilingDistanceMeters,
        int lateralHitCount,
        int openDirectionCount)
    {
        if (enclosure == VanguardOwnerEnclosure.Indoor)
        {
            bool highOpenRoof = ceilingDetected
                && ceilingDistanceMeters >= LargeOpenInteriorCeilingMeters
                && openDirectionCount >= LargeOpenInteriorMinimumOpenDirections;
            if (highOpenRoof || (ceilingDetected && openDirectionCount >= 5))
            {
                return VanguardOwnerTopologyHint.LargeOpenInterior;
            }

            if (openDirectionCount <= 3 && lateralHitCount >= 4)
            {
                return VanguardOwnerTopologyHint.Corridor;
            }

            return VanguardOwnerTopologyHint.Room;
        }

        if (enclosure == VanguardOwnerEnclosure.SemiCovered || enclosure == VanguardOwnerEnclosure.Transition)
        {
            return VanguardOwnerTopologyHint.Transition;
        }

        return openDirectionCount >= 5
            ? VanguardOwnerTopologyHint.ExteriorOpen
            : VanguardOwnerTopologyHint.ExteriorConstrained;
    }

    private static VanguardOwnerEnvironmentSnapshot BuildHeldSnapshot(
        VanguardOwnerEnvironmentSnapshot previous,
        VanguardOwnerEnvironmentSnapshot raw,
        VanguardOwnerEnclosure enclosure,
        string reason)
    {
        VanguardOwnerTopologyHint topology = enclosure == VanguardOwnerEnclosure.Transition
            ? VanguardOwnerTopologyHint.Transition
            : previous.Topology;
        return new VanguardOwnerEnvironmentSnapshot(
            enclosure,
            topology,
            raw.OwnerPosition,
            raw.NavMeshPosition,
            raw.Forward,
            raw.NavMeshProjected,
            previous.CeilingDetected,
            previous.CeilingDistanceMeters,
            raw.LateralHitCount,
            raw.OpenDirectionCount,
            raw.FloorBand,
            Math.Max(0.50f, previous.Confidence * 0.90f),
            reason);
    }

    private static bool TryFindOverheadCeiling(Vector3 ownerPosition, out float overheadHitDistanceMeters)
    {
        overheadHitDistanceMeters = float.MaxValue;
        Vector3 origin = ownerPosition + (Vector3.up * 1.35f);
        int hitCount;
        lock (PhysicsProbeSync)
        {
            hitCount = Physics.RaycastNonAlloc(
                origin,
                Vector3.up,
                OverheadHits,
                IndoorCeilingProbeMeters,
                ~0,
                QueryTriggerInteraction.Ignore);

            for (int index = 0; index < hitCount; index++)
            {
                RaycastHit hit = OverheadHits[index];
                if (!IsValidEnvironmentHit(hit)
                    || hit.distance < MinimumValidOverheadHitMeters
                    || hit.normal.y > MaximumCeilingNormalY)
                {
                    continue;
                }

                if (hit.distance < overheadHitDistanceMeters)
                {
                    overheadHitDistanceMeters = hit.distance;
                }
            }
        }

        return overheadHitDistanceMeters < float.MaxValue;
    }

    private static int CountLateralEnvironmentHits(Vector3 ownerPosition)
    {
        Vector3 origin = ownerPosition + (Vector3.up * 1.25f);
        int hitCount = 0;
        for (int index = 0; index < ProbeDirections.Length; index++)
        {
            Vector3 direction = ProbeDirections[index];
            if (Physics.Raycast(origin, direction, out RaycastHit hit, LateralProbeMeters, ~0, QueryTriggerInteraction.Ignore)
                && IsValidEnvironmentHit(hit))
            {
                hitCount++;
            }
        }
        return hitCount;
    }

    private static int CountOpenNavMeshDirections(Vector3 navMeshPosition)
    {
        int openCount = 0;
        for (int index = 0; index < ProbeDirections.Length; index++)
        {
            Vector3 raw = navMeshPosition + (ProbeDirections[index] * TopologyOpenProbeMeters);
            if (!NavMesh.SamplePosition(raw + (Vector3.up * 0.20f), out NavMeshHit hit, 1.75f, NavMesh.AllAreas))
            {
                continue;
            }

            Vector3 delta = hit.position - raw;
            delta.y = 0f;
            if (delta.magnitude > 1.75f)
            {
                continue;
            }

            if (!NavMesh.Raycast(navMeshPosition, hit.position, out _, NavMesh.AllAreas))
            {
                openCount++;
            }
        }
        return openCount;
    }

    private static bool IsValidEnvironmentHit(RaycastHit hit)
    {
        if (hit.collider == null || hit.collider.GetComponentInParent<Player>() != null)
        {
            return false;
        }

        Rigidbody? body = hit.collider.attachedRigidbody;
        return body == null || body.isKinematic;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        Vector3 delta = a - b;
        delta.y = 0f;
        return delta.magnitude;
    }

    private sealed class EnvironmentStabilityState
    {
        public EnvironmentStabilityState(VanguardOwnerEnvironmentSnapshot snapshot, Vector3 ownerPosition, DateTimeOffset now)
        {
            StableSnapshot = snapshot;
            LastOwnerPosition = ownerPosition;
            LastSampleAtUtc = now;
            LastStrongIndoorAtUtc = snapshot.CeilingDetected && snapshot.Enclosure == VanguardOwnerEnclosure.Indoor
                ? now
                : DateTimeOffset.MinValue;
            PendingEnclosure = snapshot.Enclosure;
        }

        public VanguardOwnerEnvironmentSnapshot StableSnapshot { get; private set; }
        public Vector3 LastOwnerPosition { get; private set; }
        public DateTimeOffset LastSampleAtUtc { get; private set; }
        public DateTimeOffset LastStrongIndoorAtUtc { get; private set; }
        public VanguardOwnerEnclosure PendingEnclosure { get; private set; }
        public int PendingCount { get; private set; }

        public void ObservePending(VanguardOwnerEnclosure enclosure)
        {
            if (PendingEnclosure == enclosure)
            {
                PendingCount++;
            }
            else
            {
                PendingEnclosure = enclosure;
                PendingCount = 1;
            }
        }

        public void Accept(VanguardOwnerEnvironmentSnapshot snapshot, Vector3 ownerPosition, DateTimeOffset now, bool strongIndoor)
        {
            StableSnapshot = snapshot;
            LastOwnerPosition = ownerPosition;
            LastSampleAtUtc = now;
            if (strongIndoor)
            {
                LastStrongIndoorAtUtc = now;
            }
            else if (snapshot.Enclosure != VanguardOwnerEnclosure.Indoor)
            {
                LastStrongIndoorAtUtc = DateTimeOffset.MinValue;
            }
            ResetPending();
        }

        public void ReplaceStable(VanguardOwnerEnvironmentSnapshot snapshot, Vector3 ownerPosition, DateTimeOffset now, bool strongIndoor)
        {
            LastStrongIndoorAtUtc = strongIndoor ? now : DateTimeOffset.MinValue;
            StableSnapshot = snapshot;
            LastOwnerPosition = ownerPosition;
            LastSampleAtUtc = now;
            ResetPending();
        }

        public void UpdatePose(Vector3 ownerPosition, DateTimeOffset now)
        {
            LastOwnerPosition = ownerPosition;
            LastSampleAtUtc = now;
        }

        public void ResetPending()
        {
            PendingEnclosure = StableSnapshot.Enclosure;
            PendingCount = 0;
        }
    }
}

internal enum VanguardOwnerEnclosure
{
    Outdoor = 0,
    SemiCovered = 1,
    Transition = 2,
    Indoor = 3,
}

internal enum VanguardOwnerTopologyHint
{
    Unknown = 0,
    Room = 1,
    Corridor = 2,
    LargeOpenInterior = 3,
    Transition = 4,
    ExteriorOpen = 5,
    ExteriorConstrained = 6,
}

internal readonly struct VanguardOwnerEnvironmentSnapshot
{
    public VanguardOwnerEnvironmentSnapshot(
        VanguardOwnerEnclosure enclosure,
        VanguardOwnerTopologyHint topology,
        Vector3 ownerPosition,
        Vector3 navMeshPosition,
        Vector3 forward,
        bool navMeshProjected,
        bool ceilingDetected,
        float ceilingDistanceMeters,
        int lateralHitCount,
        int openDirectionCount,
        int floorBand,
        float confidence,
        string reason)
    {
        Enclosure = enclosure;
        Topology = topology;
        OwnerPosition = ownerPosition;
        NavMeshPosition = navMeshPosition;
        Forward = forward;
        NavMeshProjected = navMeshProjected;
        CeilingDetected = ceilingDetected;
        CeilingDistanceMeters = ceilingDistanceMeters;
        LateralHitCount = lateralHitCount;
        OpenDirectionCount = openDirectionCount;
        FloorBand = floorBand;
        Confidence = confidence;
        Reason = reason;
    }

    public VanguardOwnerEnclosure Enclosure { get; }
    public VanguardOwnerTopologyHint Topology { get; }
    public Vector3 OwnerPosition { get; }
    public Vector3 NavMeshPosition { get; }
    public Vector3 Forward { get; }
    public bool NavMeshProjected { get; }
    public bool CeilingDetected { get; }
    public float CeilingDistanceMeters { get; }
    public int LateralHitCount { get; }
    public int OpenDirectionCount { get; }
    public int FloorBand { get; }
    public float Confidence { get; }
    public string Reason { get; }

    public string Signature => Enclosure + ":" + Topology + ":floor=" + FloorBand;
}
#endif
