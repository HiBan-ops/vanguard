#if SPT_CLIENT
using System;
using System.Collections.Generic;
using UnityEngine;

// Responsibility: Defines immutable Tactical Coordination runtime read/query projections built from the resolved Base plus User topology.
// Flow: The runtime read service converts a serialized raid-scoped topology document into immutable Portal, PassagePath, TacticalAnchor and PoseSlot projections for consumers.
// Authority boundary: These projections are read-only knowledge; they never persist authoring data, acquire movement ownership or issue tactical commands.
// Invariant: Published snapshots are replaced atomically by revision and consumers cannot mutate the persisted authoring objects through this read surface.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal sealed class VanguardTacticalCoordinationReadSnapshot
{
    public static readonly VanguardTacticalCoordinationReadSnapshot Empty = new(
        string.Empty,
        string.Empty,
        string.Empty,
        0,
        string.Empty,
        string.Empty,
        string.Empty,
        DateTimeOffset.MinValue,
        Array.Empty<VanguardTacticalCoordinationReadPortal>(),
        Array.Empty<VanguardTacticalCoordinationReadPassagePath>(),
        Array.Empty<VanguardTacticalCoordinationReadAnchor>());

    public VanguardTacticalCoordinationReadSnapshot(
        string mapId,
        string mapRevision,
        string eftClientVersion,
        long revision,
        string contentHash,
        string sourceOwnerProfileId,
        string sourceKind,
        DateTimeOffset publishedAtUtc,
        IReadOnlyList<VanguardTacticalCoordinationReadPortal> portals,
        IReadOnlyList<VanguardTacticalCoordinationReadPassagePath> passagePaths,
        IReadOnlyList<VanguardTacticalCoordinationReadAnchor> tacticalAnchors)
    {
        MapId = mapId;
        MapRevision = mapRevision;
        EftClientVersion = eftClientVersion;
        Revision = revision;
        ContentHash = contentHash;
        SourceOwnerProfileId = sourceOwnerProfileId;
        SourceKind = sourceKind;
        PublishedAtUtc = publishedAtUtc;
        Portals = portals;
        PassagePaths = passagePaths;
        TacticalAnchors = tacticalAnchors;
    }

    public string MapId { get; }
    public string MapRevision { get; }
    public string EftClientVersion { get; }
    public long Revision { get; }
    public string ContentHash { get; }
    public string SourceOwnerProfileId { get; }
    public string SourceKind { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public IReadOnlyList<VanguardTacticalCoordinationReadPortal> Portals { get; }
    public IReadOnlyList<VanguardTacticalCoordinationReadPassagePath> PassagePaths { get; }
    public IReadOnlyList<VanguardTacticalCoordinationReadAnchor> TacticalAnchors { get; }
    public bool IsAvailable => Revision > 0 && !string.IsNullOrWhiteSpace(MapId);
}

internal sealed class VanguardTacticalCoordinationReadPortal
{
    public VanguardTacticalCoordinationReadPortal(
        string portalId,
        string displayName,
        Vector3 position,
        Vector3? navMeshProjectedPosition,
        bool enabled,
        VanguardTacticalAuthoringValidationState validationState,
        string validationNotes,
        string origin,
        bool readOnly)
    {
        PortalId = portalId;
        DisplayName = displayName;
        Position = position;
        NavMeshProjectedPosition = navMeshProjectedPosition;
        Enabled = enabled;
        ValidationState = validationState;
        ValidationNotes = validationNotes;
        Origin = origin;
        ReadOnly = readOnly;
    }

    public string PortalId { get; }
    public string DisplayName { get; }
    public Vector3 Position { get; }
    public Vector3? NavMeshProjectedPosition { get; }
    public bool Enabled { get; }
    public VanguardTacticalAuthoringValidationState ValidationState { get; }
    public string ValidationNotes { get; }
    public string Origin { get; }
    public bool ReadOnly { get; }
}

internal sealed class VanguardTacticalCoordinationReadPassagePath
{
    public VanguardTacticalCoordinationReadPassagePath(
        string passagePathId,
        string displayName,
        IReadOnlyList<Vector3> authoredControlPoints,
        IReadOnlyList<Vector3> resolvedPolyline,
        int resolutionRevision,
        string resolutionSource,
        bool enabled,
        VanguardTacticalAuthoringValidationState validationState,
        string validationNotes,
        string origin,
        bool readOnly)
    {
        PassagePathId = passagePathId;
        DisplayName = displayName;
        AuthoredControlPoints = authoredControlPoints;
        ResolvedPolyline = resolvedPolyline;
        ResolutionRevision = resolutionRevision;
        ResolutionSource = resolutionSource;
        Enabled = enabled;
        ValidationState = validationState;
        ValidationNotes = validationNotes;
        Origin = origin;
        ReadOnly = readOnly;
    }

    public string PassagePathId { get; }
    public string DisplayName { get; }
    public IReadOnlyList<Vector3> AuthoredControlPoints { get; }
    public IReadOnlyList<Vector3> ResolvedPolyline { get; }
    public int ResolutionRevision { get; }
    public string ResolutionSource { get; }
    public bool Enabled { get; }
    public VanguardTacticalAuthoringValidationState ValidationState { get; }
    public string ValidationNotes { get; }
    public string Origin { get; }
    public bool ReadOnly { get; }
}

internal sealed class VanguardTacticalCoordinationReadAnchor
{
    public VanguardTacticalCoordinationReadAnchor(
        string anchorId,
        string name,
        Vector3 center,
        float radius,
        IReadOnlyList<VanguardTacticalCoordinationReadPoseSlot> poseSlots,
        bool enabled,
        VanguardTacticalAuthoringValidationState validationState,
        string validationNotes,
        string origin,
        bool readOnly)
    {
        AnchorId = anchorId;
        Name = name;
        Center = center;
        Radius = radius;
        PoseSlots = poseSlots;
        Enabled = enabled;
        ValidationState = validationState;
        ValidationNotes = validationNotes;
        Origin = origin;
        ReadOnly = readOnly;
    }

    public string AnchorId { get; }
    public string Name { get; }
    public Vector3 Center { get; }
    public float Radius { get; }
    public IReadOnlyList<VanguardTacticalCoordinationReadPoseSlot> PoseSlots { get; }
    public bool Enabled { get; }
    public VanguardTacticalAuthoringValidationState ValidationState { get; }
    public string ValidationNotes { get; }
    public string Origin { get; }
    public bool ReadOnly { get; }
}

internal sealed class VanguardTacticalCoordinationReadPhysicalPose
{
    public VanguardTacticalCoordinationReadPhysicalPose(
        int captureContractVersion,
        string poseKind,
        bool isProne,
        float poseLevel,
        bool isAimingDownSights,
        float tilt,
        bool isLeftStance,
        bool isInPatrol,
        string capturedAt,
        string capturedWithBuild)
    {
        CaptureContractVersion = captureContractVersion;
        PoseKind = poseKind;
        IsProne = isProne;
        PoseLevel = poseLevel;
        IsAimingDownSights = isAimingDownSights;
        Tilt = tilt;
        IsLeftStance = isLeftStance;
        IsInPatrol = isInPatrol;
        CapturedAt = capturedAt;
        CapturedWithBuild = capturedWithBuild;
    }

    public int CaptureContractVersion { get; }
    public string PoseKind { get; }
    public bool IsProne { get; }
    public float PoseLevel { get; }
    public bool IsAimingDownSights { get; }
    public float Tilt { get; }
    public bool IsLeftStance { get; }
    public bool IsInPatrol { get; }
    public string CapturedAt { get; }
    public string CapturedWithBuild { get; }
    public bool RuntimeUsable => CaptureContractVersion >= VanguardTacticalAuthoringPhysicalPoseCapture.MinimumPhysicalPoseContractVersion
        && !float.IsNaN(PoseLevel) && !float.IsInfinity(PoseLevel)
        && PoseLevel >= 0f
        && PoseLevel <= 1f;
    public bool AimStateUsable => CaptureContractVersion >= VanguardTacticalAuthoringPhysicalPoseCapture.AimStateContractVersion;
    public bool FullStationaryPresentationUsable => CaptureContractVersion >= VanguardTacticalAuthoringPhysicalPoseCapture.FullStationaryPresentationContractVersion
        && !float.IsNaN(Tilt) && !float.IsInfinity(Tilt)
        && Tilt >= -5f && Tilt <= 5f;
}

internal sealed class VanguardTacticalCoordinationReadPoseSlot
{
    public VanguardTacticalCoordinationReadPoseSlot(
        string poseSlotId,
        Vector3 position,
        Vector3? navMeshProjectedPosition,
        Vector3 viewDirection,
        VanguardTacticalCoordinationReadPhysicalPose? physicalPose,
        bool enabled,
        VanguardTacticalAuthoringValidationState validationState,
        string validationNotes)
    {
        PoseSlotId = poseSlotId;
        Position = position;
        NavMeshProjectedPosition = navMeshProjectedPosition;
        ViewDirection = viewDirection;
        PhysicalPose = physicalPose;
        Enabled = enabled;
        ValidationState = validationState;
        ValidationNotes = validationNotes;
    }

    public string PoseSlotId { get; }
    public Vector3 Position { get; }
    public Vector3? NavMeshProjectedPosition { get; }
    public Vector3 ViewDirection { get; }
    public VanguardTacticalCoordinationReadPhysicalPose? PhysicalPose { get; }
    public bool Enabled { get; }
    public VanguardTacticalAuthoringValidationState ValidationState { get; }
    public string ValidationNotes { get; }
}
#endif
