#if SPT_CLIENT
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

// Responsibility: Defines current Tactical Authoring HOLD contracts and shared value types used by the editor.
// Flow: The authoring document persists only HOLD-domain user data plus migration candidates; future Tactical Coordination Base/User documents own Portal/PassagePath/TacticalAnchor/PoseSlot persistence.
// Authority boundary: A shared editor/schema vocabulary does not imply a shared persistence or runtime-consumption authority. TC data is physically separate and resolved by a dedicated coordination store.
// Invariant: legacy Access records migrate only to unconfirmed LegacyPortalCandidates in the authoring document; no migration path silently creates or persists active TC objects or physical pose truth.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal enum VanguardTacticalSlotType
{
    EntryGuard = 0,
    CorridorWatch = 1,
    StairGuard = 2,
    RearSecurity = 3,
    Overwatch = 4,
    CoveredSupport = 5,
    CornerWatch = 6,
    Fallback = 7,
    ExteriorCover = 8
}

internal enum VanguardTacticalAuthoringValidationState
{
    NotValidated = 0,
    Valid = 1,
    Warning = 2,
    Invalid = 3
}

[Serializable]
internal sealed class VanguardVector3Dto
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public static VanguardVector3Dto FromVector3(Vector3 value)
    {
        return new VanguardVector3Dto { X = value.x, Y = value.y, Z = value.z };
    }

    public Vector3 ToVector3()
    {
        return new Vector3(X, Y, Z);
    }
}

[Serializable]
internal sealed class VanguardTacticalAuthoringMapFile
{
    public int SchemaVersion { get; set; } = VanguardTacticalAuthoringStore.CurrentSchemaVersion;
    public string MapId { get; set; } = string.Empty;
    public string MapRevision { get; set; } = string.Empty;
    public string EftClientVersion { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string LastSavedAt { get; set; } = string.Empty;
    public string CreatedWithBuild { get; set; } = string.Empty;
    public string LastSavedWithBuild { get; set; } = string.Empty;

    // Runtime consumption is not persisted as authoring truth. The compatibility property remains
    // as a compatibility guard for the existing editor/live-preview code until the dedicated authoring surface replaces it.
    [JsonIgnore]
    public bool RuntimeConsumptionEnabled { get; set; }

    [JsonProperty("HoldZones")]
    public List<VanguardTacticalAuthoringZone> HoldZones { get; set; } = new();

    // Migration bridge only. These candidates remain authoring-owned until a later explicit user action
    // promotes/copies one into the physically separate Tactical Coordination USER layer.
    public List<VanguardTacticalAuthoringLegacyPortalCandidate> LegacyPortalCandidates { get; set; } = new();
    public VanguardTacticalAuthoringMigrationRecord? MigrationRecord { get; set; }

    // Transitional in-memory alias. It deliberately serializes only as HoldZones.
    [JsonIgnore]
    public List<VanguardTacticalAuthoringZone> Zones
    {
        get => HoldZones;
        set => HoldZones = value ?? new List<VanguardTacticalAuthoringZone>();
    }
}

[Serializable]
internal sealed class VanguardTacticalAuthoringZone
{
    public string ZoneId { get; set; } = string.Empty;
    public string DisplayZoneName { get; set; } = string.Empty;
    public string BuildingId { get; set; } = string.Empty;
    public string FloorId { get; set; } = string.Empty;
    public float MinY { get; set; }
    public float MaxY { get; set; }
    public bool MinYExplicit { get; set; }
    public bool MaxYExplicit { get; set; }
    public bool FloorBoundsProvisional { get; set; } = true;
    public VanguardVector3Dto ZoneAnchor { get; set; } = new();
    public float ZoneRadius { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string CreatedWithBuild { get; set; } = string.Empty;
    public string LastValidatedAt { get; set; } = string.Empty;
    public string LastValidatedBuild { get; set; } = string.Empty;

    [JsonProperty("HoldSlots")]
    public List<VanguardTacticalAuthoringSlot> Slots { get; set; } = new();

    // Legacy editor compatibility view only. Store load/save projects this list to/from the top-level
    // LegacyPortalCandidates collection. It is never serialized under a HoldZone in the current document.
    [JsonIgnore]
    public List<VanguardTacticalAuthoringAccess> Accesses { get; set; } = new();
}

[Serializable]
internal sealed class VanguardTacticalAuthoringAccess
{
    public string AccessId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public VanguardVector3Dto Position { get; set; } = new();
    public VanguardVector3Dto? NavMeshProjectedPosition { get; set; }
    public VanguardVector3Dto ApproachDirection { get; set; } = new();
    public bool NavMeshProjectionSucceeded { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string CreatedWithBuild { get; set; } = string.Empty;
}

[Serializable]
internal sealed class VanguardTacticalAuthoringSlot
{
    public string SlotId { get; set; } = string.Empty;
    public VanguardVector3Dto Position { get; set; } = new();
    public VanguardVector3Dto? NavMeshProjectedPosition { get; set; }
    public VanguardVector3Dto WatchDirection { get; set; } = new();
    public VanguardTacticalAuthoringPhysicalPoseSnapshot? PhysicalPoseSnapshot { get; set; }
    public bool Enabled { get; set; } = true;
    public bool AuthoringValid { get; set; }
    public VanguardTacticalAuthoringValidationState ValidationState { get; set; }
    public string ValidationNotes { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string CreatedWithBuild { get; set; } = string.Empty;
    public string LastValidatedAt { get; set; } = string.Empty;
    public string LastValidatedBuild { get; set; } = string.Empty;

    // HOLD selection still orders by the former Priority field. The current schema preserves it solely as an
    // internal compatibility ordering hint retained until the dedicated topology ordering contract proves it unnecessary.
    public int? CompatibilityAssignmentOrderHint { get; set; }
    public VanguardTacticalAuthoringLegacySlotCompatibility? LegacyCompatibility { get; set; }

    [JsonIgnore]
    public VanguardTacticalSlotType SlotType
    {
        get => LegacyCompatibility?.SlotType ?? VanguardTacticalSlotType.EntryGuard;
        set => EnsureLegacyCompatibility().SlotType = value;
    }

    [JsonIgnore]
    public float WatchArc
    {
        get => LegacyCompatibility?.WatchArc ?? 90.0f;
        set => EnsureLegacyCompatibility().WatchArc = value;
    }

    [JsonIgnore]
    public string AssociatedAccessId
    {
        get => LegacyCompatibility?.AssociatedAccessId ?? string.Empty;
        set => EnsureLegacyCompatibility().AssociatedAccessId = value ?? string.Empty;
    }

    [JsonIgnore]
    public string ConnectedZoneId
    {
        get => LegacyCompatibility?.ConnectedZoneId ?? string.Empty;
        set => EnsureLegacyCompatibility().ConnectedZoneId = value ?? string.Empty;
    }

    [JsonIgnore]
    public int Priority
    {
        get => CompatibilityAssignmentOrderHint ?? 0;
        set => CompatibilityAssignmentOrderHint = value;
    }

    [JsonIgnore]
    public int MinimumSquadSize
    {
        get => LegacyCompatibility?.MinimumSquadSize ?? 1;
        set => EnsureLegacyCompatibility().MinimumSquadSize = value;
    }

    [JsonIgnore]
    public float MaximumOwnerDistance
    {
        get => LegacyCompatibility?.MaximumOwnerDistance ?? 0.0f;
        set => EnsureLegacyCompatibility().MaximumOwnerDistance = value;
    }

    [JsonIgnore]
    public string RoleAffinity
    {
        get => LegacyCompatibility?.RoleAffinity ?? string.Empty;
        set => EnsureLegacyCompatibility().RoleAffinity = value ?? string.Empty;
    }

    [JsonIgnore]
    public string MutualExclusionGroup
    {
        get => LegacyCompatibility?.MutualExclusionGroup ?? string.Empty;
        set => EnsureLegacyCompatibility().MutualExclusionGroup = value ?? string.Empty;
    }

    // RuntimeEligible was persisted in legacy data. Current documents exclude it, and the compatibility surface
    // can only be false, preserving the existing fail-closed runtime contract.
    [JsonIgnore]
    public bool RuntimeEligible
    {
        get => false;
        set { }
    }

    private VanguardTacticalAuthoringLegacySlotCompatibility EnsureLegacyCompatibility()
    {
        return LegacyCompatibility ??= new VanguardTacticalAuthoringLegacySlotCompatibility();
    }
}

[Serializable]
internal sealed class VanguardTacticalAuthoringLegacySlotCompatibility
{
    public VanguardTacticalSlotType SlotType { get; set; }
    public float WatchArc { get; set; } = 90.0f;
    public string AssociatedAccessId { get; set; } = string.Empty;
    public string ConnectedZoneId { get; set; } = string.Empty;
    public int MinimumSquadSize { get; set; } = 1;
    public float MaximumOwnerDistance { get; set; }
    public string RoleAffinity { get; set; } = string.Empty;
    public string MutualExclusionGroup { get; set; } = string.Empty;
}

[Serializable]
internal sealed class VanguardTacticalAuthoringPhysicalPoseSnapshot
{
    // The earliest contract carried only a marker. The pose-capable contract is additive: earlier marker/null
    // snapshots remain readable, while newly captured/recaptured PoseSlots persist real player pose truth.
    public int CaptureContractVersion { get; set; } = 1;
    public string PoseKind { get; set; } = string.Empty;
    public bool IsProne { get; set; }
    public float PoseLevel { get; set; } = 1.0f;
    // The ADS-capable contract adds exact author aim intent. Earlier marker/pose snapshots remain valid and
    // must not be interpreted as either aiming or not aiming.
    public bool IsAimingDownSights { get; set; }
    // The full-presentation contract completes stationary capture. Tilt is EFT MovementContext.Tilt
    // in its native [-5,+5] range; LeftStance is the shoulder-side presentation and
    // IsInPatrol is EFT's non-ADS patrol / lowered-weapon presentation state.
    public float Tilt { get; set; }
    public bool IsLeftStance { get; set; }
    public bool IsInPatrol { get; set; }
    public string CapturedAt { get; set; } = string.Empty;
    public string CapturedWithBuild { get; set; } = string.Empty;
}

// Shared editor/value type for the future Tactical Coordination domain. It is intentionally NOT a member of VanguardTacticalAuthoringMapFile.
[Serializable]
internal sealed class VanguardTacticalAuthoringPortal
{
    public string PortalId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public VanguardVector3Dto Position { get; set; } = new();
    public VanguardVector3Dto? NavMeshProjectedPosition { get; set; }
    public bool Enabled { get; set; } = true;
    public VanguardTacticalAuthoringValidationState ValidationState { get; set; }
    public string ValidationNotes { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string CreatedWithBuild { get; set; } = string.Empty;
}

// Shared editor/value type for the future Tactical Coordination domain. Persistence belongs to TC Base/User documents.
[Serializable]
internal sealed class VanguardTacticalAuthoringPassagePath
{
    public string PassagePathId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<VanguardTacticalAuthoringPassageControlPoint> ControlPoints { get; set; } = new();
    public List<VanguardVector3Dto> ResolvedPolyline { get; set; } = new();
    public int ResolutionRevision { get; set; }
    public string ResolutionSource { get; set; } = string.Empty;
    public string ResolvedAt { get; set; } = string.Empty;
    public string ResolvedWithBuild { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public VanguardTacticalAuthoringValidationState ValidationState { get; set; }
    public string ValidationNotes { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string CreatedWithBuild { get; set; } = string.Empty;
}

[Serializable]
internal sealed class VanguardTacticalAuthoringPassageControlPoint
{
    public string ControlPointId { get; set; } = string.Empty;
    public VanguardVector3Dto Position { get; set; } = new();
    public VanguardVector3Dto? NavMeshProjectedPosition { get; set; }
}

// Shared editor/value type for the future Tactical Coordination domain. Persistence belongs to TC Base/User documents.
[Serializable]
internal sealed class VanguardTacticalAuthoringAnchor
{
    public string AnchorId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public VanguardVector3Dto Center { get; set; } = new();
    public float Radius { get; set; }
    public List<VanguardTacticalAuthoringPoseSlot> AuthoredPoseSlots { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public VanguardTacticalAuthoringValidationState ValidationState { get; set; }
    public string ValidationNotes { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string CreatedWithBuild { get; set; } = string.Empty;
}

[Serializable]
internal sealed class VanguardTacticalAuthoringPoseSlot
{
    public string PoseSlotId { get; set; } = string.Empty;
    public VanguardVector3Dto Position { get; set; } = new();
    public VanguardVector3Dto? NavMeshProjectedPosition { get; set; }
    public VanguardVector3Dto ViewDirection { get; set; } = new();
    public VanguardTacticalAuthoringPhysicalPoseSnapshot? PhysicalPoseSnapshot { get; set; }
    public bool Enabled { get; set; } = true;
    public VanguardTacticalAuthoringValidationState ValidationState { get; set; }
    public string ValidationNotes { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string CreatedWithBuild { get; set; } = string.Empty;
    public string LastValidatedAt { get; set; } = string.Empty;
    public string LastValidatedBuild { get; set; } = string.Empty;
}

[Serializable]
internal sealed class VanguardTacticalAuthoringLegacyPortalCandidate
{
    public string CandidateId { get; set; } = string.Empty;
    public string SourceHoldZoneId { get; set; } = string.Empty;
    public string OriginalAccessId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public VanguardVector3Dto Position { get; set; } = new();
    public VanguardVector3Dto? NavMeshProjectedPosition { get; set; }
    public VanguardVector3Dto ApproachDirection { get; set; } = new();
    public bool NavMeshProjectionSucceeded { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string CreatedWithBuild { get; set; } = string.Empty;
    public string ReviewState { get; set; } = "unconfirmed";
}

[Serializable]
internal sealed class VanguardTacticalAuthoringMigrationRecord
{
    public int FromSchemaVersion { get; set; }
    public int ToSchemaVersion { get; set; }
    public string SourceSha256 { get; set; } = string.Empty;
    public string MigratedAt { get; set; } = string.Empty;
    public string MigratedWithBuild { get; set; } = string.Empty;
    public int HoldZoneCount { get; set; }
    public int HoldSlotCount { get; set; }
    public int LegacyPortalCandidateCount { get; set; }
    public int ActivePortalCountCreated { get; set; }
    public int PhysicalPoseSnapshotsInvented { get; set; }
    public string SourceBackupFileName { get; set; } = string.Empty;
    public string Policy { get; set; } = "schema1_zone_slot_to_hold_access_to_unconfirmed_candidate_tc_domain_physically_separate";
}

[Serializable]
internal sealed class VanguardTacticalAuthoringInvalidReport
{
    public int SchemaVersion { get; set; } = VanguardTacticalAuthoringStore.CurrentSchemaVersion;
    public string MapId { get; set; } = string.Empty;
    public string ExportedAt { get; set; } = string.Empty;
    public string ExportedWithBuild { get; set; } = string.Empty;
    public int ZoneCount { get; set; }
    public int SlotCount { get; set; }
    public int NonValidSlotCount { get; set; }
    public List<VanguardTacticalAuthoringInvalidSlotRecord> Slots { get; set; } = new();
}

[Serializable]
internal sealed class VanguardTacticalAuthoringInvalidSlotRecord
{
    public string ZoneId { get; set; } = string.Empty;
    public string DisplayZoneName { get; set; } = string.Empty;
    public string FloorId { get; set; } = string.Empty;
    public string SlotId { get; set; } = string.Empty;
    public VanguardTacticalSlotType SlotType { get; set; }
    public bool Enabled { get; set; }
    public VanguardTacticalAuthoringValidationState ValidationState { get; set; }
    public string ValidationNotes { get; set; } = string.Empty;
}
#endif
