#if SPT_CLIENT
using System;
using System.Linq;
using UnityEngine;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.TacticalAuthoring;

// Responsibility: Selects a nearby authored Tactical Coordination PoseSlot as a preferred stationary-medical placement candidate.
// Flow: Reads the immutable runtime topology snapshot, filters enabled/non-invalid TacticalAnchors and PoseSlots, and returns the nearest local authored position without mutating authoring or medical state.
// Authority boundary: This resolver is read-only topology knowledge; the medical executor remains the sole owner of path preflight, movement lease, safety admission and procedure execution.
// Invariant: Authored TC placement is advisory and local. Missing, stale, invalid or unreachable topology must fall back to the existing medical cover search; physical pose/view remain immutable metadata until the owning medical window invokes the dedicated pose bridge.
namespace Vanguard.Client.Runtime.Medical;

internal static class VanguardStationaryMedicalTacticalPlacementResolver
{
    public const string StatusTag = "VANGUARD_STATIONARY_MEDICAL_TC_PLACEMENT_STATUS";

    public static bool TryResolvePreferredPoseSlot(
        OperatorDecisionSnapshot snapshot,
        float maximumDistanceMeters,
        out VanguardStationaryMedicalTacticalPlacementCandidate candidate,
        out string reason)
    {
        candidate = null!;
        reason = "none";

        if (maximumDistanceMeters <= 0f)
        {
            reason = "maximum_distance_not_positive";
            return false;
        }

        if (!VanguardTacticalCoordinationRuntimeReadService.TryGetSnapshot(out var topology))
        {
            reason = "tc_runtime_snapshot_unavailable";
            return false;
        }

        float maximumSquared = maximumDistanceMeters * maximumDistanceMeters;
        Vector3 origin = snapshot.Position;
        var selected = topology.TacticalAnchors
            .Where(anchor => anchor.Enabled && anchor.ValidationState != VanguardTacticalAuthoringValidationState.Invalid)
            .SelectMany(anchor => anchor.PoseSlots
                .Where(slot => slot.Enabled && slot.ValidationState != VanguardTacticalAuthoringValidationState.Invalid)
                .Select(slot => new
                {
                    Anchor = anchor,
                    Slot = slot,
                    Target = slot.NavMeshProjectedPosition ?? slot.Position,
                    ContainsOrigin = FlatSquaredDistance(anchor.Center, origin) <= anchor.Radius * anchor.Radius,
                    DistanceSquared = FlatSquaredDistance(slot.NavMeshProjectedPosition ?? slot.Position, origin)
                }))
            .Where(item => item.DistanceSquared <= maximumSquared)
            .OrderBy(item => item.ContainsOrigin ? 0 : 1)
            .ThenBy(item => item.DistanceSquared)
            .ThenBy(item => item.Anchor.AnchorId, StringComparer.Ordinal)
            .ThenBy(item => item.Slot.PoseSlotId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (selected == null)
        {
            reason = "no_enabled_non_invalid_pose_slot_within_range"
                + ":topologyRevision=" + topology.Revision
                + ":anchors=" + topology.TacticalAnchors.Count;
            return false;
        }

        candidate = new VanguardStationaryMedicalTacticalPlacementCandidate(
            selected.Anchor.AnchorId,
            selected.Anchor.Name,
            selected.Slot.PoseSlotId,
            selected.Target,
            selected.Slot.ViewDirection,
            selected.Slot.PhysicalPose,
            selected.Anchor.Origin,
            selected.Anchor.ReadOnly,
            selected.Anchor.ValidationState,
            selected.Slot.ValidationState,
            selected.ContainsOrigin,
            Mathf.Sqrt(selected.DistanceSquared),
            topology.Revision,
            topology.ContentHash);

        reason = "tc_pose_slot_selected"
            + ":anchor=" + Safe(candidate.AnchorId)
            + ":slot=" + Safe(candidate.PoseSlotId)
            + ":origin=" + Safe(candidate.Origin)
            + ":containingAnchor=" + Bool(candidate.ContainingAnchor)
            + ":distance=" + candidate.DistanceMeters.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            + ":topologyRevision=" + candidate.TopologyRevision;
        return true;
    }

    private static float FlatSquaredDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }

    private static string Bool(bool value) => value ? "true" : "false";
}

internal sealed class VanguardStationaryMedicalTacticalPlacementCandidate
{
    public VanguardStationaryMedicalTacticalPlacementCandidate(
        string anchorId,
        string anchorName,
        string poseSlotId,
        Vector3 target,
        Vector3 viewDirection,
        VanguardTacticalCoordinationReadPhysicalPose? physicalPose,
        string origin,
        bool readOnly,
        VanguardTacticalAuthoringValidationState anchorValidationState,
        VanguardTacticalAuthoringValidationState poseSlotValidationState,
        bool containingAnchor,
        float distanceMeters,
        long topologyRevision,
        string topologyContentHash)
    {
        AnchorId = anchorId;
        AnchorName = anchorName;
        PoseSlotId = poseSlotId;
        Target = target;
        ViewDirection = viewDirection;
        PhysicalPose = physicalPose;
        Origin = origin;
        ReadOnly = readOnly;
        AnchorValidationState = anchorValidationState;
        PoseSlotValidationState = poseSlotValidationState;
        ContainingAnchor = containingAnchor;
        DistanceMeters = distanceMeters;
        TopologyRevision = topologyRevision;
        TopologyContentHash = topologyContentHash;
    }

    public string AnchorId { get; }
    public string AnchorName { get; }
    public string PoseSlotId { get; }
    public Vector3 Target { get; }
    public Vector3 ViewDirection { get; }
    public VanguardTacticalCoordinationReadPhysicalPose? PhysicalPose { get; }
    public string Origin { get; }
    public bool ReadOnly { get; }
    public VanguardTacticalAuthoringValidationState AnchorValidationState { get; }
    public VanguardTacticalAuthoringValidationState PoseSlotValidationState { get; }
    public bool ContainingAnchor { get; }
    public float DistanceMeters { get; }
    public long TopologyRevision { get; }
    public string TopologyContentHash { get; }
    public string Source => "tc_pose_slot:" + Origin + ":" + AnchorId + ":" + PoseSlotId;
}
#endif
