#if SPT_CLIENT
using System;
using System.Collections.Generic;
using UnityEngine;

// Responsibility: Defines data/state contracts used by the tactical-authoring runtime, centered on Tactical Authoring Models.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
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
    public bool RuntimeConsumptionEnabled { get; set; }
    public List<VanguardTacticalAuthoringZone> Zones { get; set; } = new();
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
    public List<VanguardTacticalAuthoringAccess> Accesses { get; set; } = new();
    public List<VanguardTacticalAuthoringSlot> Slots { get; set; } = new();
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
    public VanguardTacticalSlotType SlotType { get; set; }
    public VanguardVector3Dto Position { get; set; } = new();
    public VanguardVector3Dto? NavMeshProjectedPosition { get; set; }
    public VanguardVector3Dto WatchDirection { get; set; } = new();
    public float WatchArc { get; set; }
    public string AssociatedAccessId { get; set; } = string.Empty;
    public string ConnectedZoneId { get; set; } = string.Empty;
    public int Priority { get; set; }
    public int MinimumSquadSize { get; set; }
    public float MaximumOwnerDistance { get; set; }
    public string RoleAffinity { get; set; } = string.Empty;
    public string MutualExclusionGroup { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string CreatedWithBuild { get; set; } = string.Empty;
    public string LastValidatedAt { get; set; } = string.Empty;
    public string LastValidatedBuild { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool AuthoringValid { get; set; }
    public bool RuntimeEligible { get; set; }
    public VanguardTacticalAuthoringValidationState ValidationState { get; set; }
    public string ValidationNotes { get; set; } = string.Empty;
}

[Serializable]
internal sealed class VanguardTacticalAuthoringInvalidReport
{
    public int SchemaVersion { get; set; } = 1;
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
