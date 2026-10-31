#if SPT_CLIENT
using System;
using System.Collections.Generic;
using UnityEngine;

// Responsibility: Defines the immutable read-only Tactical Coordinator view derived from committed TC topology plus current Operator decision snapshots.
// Flow: The read-only coordinator service relates each live Operator position to nearby authored Portals, PassagePaths, TacticalAnchors and PoseSlots without selecting an intent or issuing execution commands.
// Authority boundary: These models are observations only; they cannot persist authoring data, acquire movement leases, mutate SAIN state, select combat targets or command Operators.
// Invariant: Every published coordinator view is bound to one TC topology revision/content hash and one observed Operator decision batch, and consumers cannot mutate the underlying topology through this surface.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal sealed class VanguardTacticalCoordinatorReadSnapshot
{
    public static readonly VanguardTacticalCoordinatorReadSnapshot Empty = new(
        string.Empty,
        0,
        string.Empty,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        Array.Empty<VanguardTacticalCoordinatorOperatorTopologyView>());

    public VanguardTacticalCoordinatorReadSnapshot(
        string mapId,
        long topologyRevision,
        string topologyContentHash,
        DateTimeOffset operatorBatchAtUtc,
        DateTimeOffset publishedAtUtc,
        IReadOnlyList<VanguardTacticalCoordinatorOperatorTopologyView> operators)
    {
        MapId = mapId;
        TopologyRevision = topologyRevision;
        TopologyContentHash = topologyContentHash;
        OperatorBatchAtUtc = operatorBatchAtUtc;
        PublishedAtUtc = publishedAtUtc;
        Operators = operators;
    }

    public string MapId { get; }
    public long TopologyRevision { get; }
    public string TopologyContentHash { get; }
    public DateTimeOffset OperatorBatchAtUtc { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public IReadOnlyList<VanguardTacticalCoordinatorOperatorTopologyView> Operators { get; }
    public bool IsAvailable => TopologyRevision > 0 && !string.IsNullOrWhiteSpace(MapId);
}

internal sealed class VanguardTacticalCoordinatorOperatorTopologyView
{
    public VanguardTacticalCoordinatorOperatorTopologyView(
        string operatorId,
        string ownerProfileId,
        string botProfileId,
        Vector3 position,
        DateTimeOffset capturedAtUtc,
        string containingAnchorId,
        string containingAnchorName,
        string containingAnchorOrigin,
        string nearestAnchorId,
        float? nearestAnchorDistanceMeters,
        string nearestPoseSlotAnchorId,
        string nearestPoseSlotId,
        float? nearestPoseSlotDistanceMeters,
        Vector3? nearestPoseSlotViewDirection,
        string nearestPortalId,
        string nearestPortalName,
        float? nearestPortalDistanceMeters,
        string nearestPassagePathId,
        string nearestPassagePathName,
        float? nearestPassagePathDistanceMeters,
        Vector3? nearestPassagePathPoint,
        string nearestPassageGeometrySource)
    {
        OperatorId = operatorId;
        OwnerProfileId = ownerProfileId;
        BotProfileId = botProfileId;
        Position = position;
        CapturedAtUtc = capturedAtUtc;
        ContainingAnchorId = containingAnchorId;
        ContainingAnchorName = containingAnchorName;
        ContainingAnchorOrigin = containingAnchorOrigin;
        NearestAnchorId = nearestAnchorId;
        NearestAnchorDistanceMeters = nearestAnchorDistanceMeters;
        NearestPoseSlotAnchorId = nearestPoseSlotAnchorId;
        NearestPoseSlotId = nearestPoseSlotId;
        NearestPoseSlotDistanceMeters = nearestPoseSlotDistanceMeters;
        NearestPoseSlotViewDirection = nearestPoseSlotViewDirection;
        NearestPortalId = nearestPortalId;
        NearestPortalName = nearestPortalName;
        NearestPortalDistanceMeters = nearestPortalDistanceMeters;
        NearestPassagePathId = nearestPassagePathId;
        NearestPassagePathName = nearestPassagePathName;
        NearestPassagePathDistanceMeters = nearestPassagePathDistanceMeters;
        NearestPassagePathPoint = nearestPassagePathPoint;
        NearestPassageGeometrySource = nearestPassageGeometrySource;
    }

    public string OperatorId { get; }
    public string OwnerProfileId { get; }
    public string BotProfileId { get; }
    public Vector3 Position { get; }
    public DateTimeOffset CapturedAtUtc { get; }
    public string ContainingAnchorId { get; }
    public string ContainingAnchorName { get; }
    public string ContainingAnchorOrigin { get; }
    public string NearestAnchorId { get; }
    public float? NearestAnchorDistanceMeters { get; }
    public string NearestPoseSlotAnchorId { get; }
    public string NearestPoseSlotId { get; }
    public float? NearestPoseSlotDistanceMeters { get; }
    public Vector3? NearestPoseSlotViewDirection { get; }
    public string NearestPortalId { get; }
    public string NearestPortalName { get; }
    public float? NearestPortalDistanceMeters { get; }
    public string NearestPassagePathId { get; }
    public string NearestPassagePathName { get; }
    public float? NearestPassagePathDistanceMeters { get; }
    public Vector3? NearestPassagePathPoint { get; }
    public string NearestPassageGeometrySource { get; }

    public bool IsInsideTacticalAnchor => !string.IsNullOrWhiteSpace(ContainingAnchorId);
    public bool HasPoseSlotObservation => !string.IsNullOrWhiteSpace(NearestPoseSlotId);
    public bool HasPortalObservation => !string.IsNullOrWhiteSpace(NearestPortalId);
    public bool HasPassageObservation => !string.IsNullOrWhiteSpace(NearestPassagePathId);
}
#endif
