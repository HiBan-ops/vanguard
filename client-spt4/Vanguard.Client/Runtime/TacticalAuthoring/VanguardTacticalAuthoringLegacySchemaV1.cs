#if SPT_CLIENT
using System;
using System.Collections.Generic;

// Responsibility: Private read-only DTOs for recognized legacy Tactical Authoring documents.
// Flow: The store deserializes known legacy documents into these shapes and converts them deterministically to the current in-memory layout.
// Authority boundary: Compatibility input only. These DTOs are never emitted as current authoring truth and never create active Portal/TC semantics.
// Invariant: Unknown future schemas are not routed through this compatibility reader.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

[Serializable]
internal sealed class VanguardLegacyTacticalAuthoringMapFile
{
    public int SchemaVersion { get; set; } = 1;
    public string MapId { get; set; } = string.Empty;
    public string MapRevision { get; set; } = string.Empty;
    public string EftClientVersion { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string LastSavedAt { get; set; } = string.Empty;
    public string CreatedWithBuild { get; set; } = string.Empty;
    public string LastSavedWithBuild { get; set; } = string.Empty;
    public bool RuntimeConsumptionEnabled { get; set; }
    public List<VanguardLegacyTacticalAuthoringZone> Zones { get; set; } = new();
}

[Serializable]
internal sealed class VanguardLegacyTacticalAuthoringZone
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
    public List<VanguardLegacyTacticalAuthoringAccess> Accesses { get; set; } = new();
    public List<VanguardLegacyTacticalAuthoringSlot> Slots { get; set; } = new();
}

[Serializable]
internal sealed class VanguardLegacyTacticalAuthoringAccess
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
internal sealed class VanguardLegacyTacticalAuthoringSlot
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
#endif
