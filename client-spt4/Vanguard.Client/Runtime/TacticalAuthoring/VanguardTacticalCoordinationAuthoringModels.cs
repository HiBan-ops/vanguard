#if SPT_CLIENT
using System;
using System.Collections.Generic;

// Responsibility: Defines the physically separate Tactical Coordination authoring documents consumed by the shared Tactical Authoring editor.
// Flow: Vanguard Base data and player User data use the same sparse Portal/PassagePath/TacticalAnchor vocabulary while remaining distinct files and provenance layers.
// Authority boundary: These records are authored map knowledge only; they do not grant Tactical Coordination runtime movement/combat authority and never alter HOLD persistence.
// Invariant: Base is read-only, User is the only writable layer, and legacy TA Access candidates become TC Portals only through an explicit promotion record.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

[Serializable]
internal sealed class VanguardTacticalCoordinationAuthoringMapFile
{
    public int SchemaVersion { get; set; } = VanguardTacticalCoordinationAuthoringStore.CurrentSchemaVersion;
    public string MapId { get; set; } = string.Empty;
    public string Layer { get; set; } = string.Empty;
    public string SourcePackVersion { get; set; } = string.Empty;
    public string MapRevision { get; set; } = string.Empty;
    public string EftClientVersion { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string LastSavedAt { get; set; } = string.Empty;
    public string CreatedWithBuild { get; set; } = string.Empty;
    public string LastSavedWithBuild { get; set; } = string.Empty;
    public List<VanguardTacticalAuthoringPortal> Portals { get; set; } = new();
    public List<VanguardTacticalAuthoringPassagePath> PassagePaths { get; set; } = new();
    public List<VanguardTacticalAuthoringAnchor> TacticalAnchors { get; set; } = new();
    public List<VanguardTacticalCoordinationLegacyPortalPromotion> LegacyPortalPromotions { get; set; } = new();
}

[Serializable]
internal sealed class VanguardTacticalCoordinationLegacyPortalPromotion
{
    public string CandidateId { get; set; } = string.Empty;
    public string PortalId { get; set; } = string.Empty;
    public string PromotedAt { get; set; } = string.Empty;
    public string PromotedWithBuild { get; set; } = string.Empty;
}

[Serializable]
internal sealed class VanguardTacticalCoordinationResolvedPortal
{
    public VanguardTacticalAuthoringPortal Portal { get; set; } = new();
    public string Origin { get; set; } = string.Empty;
    public bool ReadOnly { get; set; }
}

[Serializable]
internal sealed class VanguardTacticalCoordinationResolvedPassagePath
{
    public VanguardTacticalAuthoringPassagePath PassagePath { get; set; } = new();
    public string Origin { get; set; } = string.Empty;
    public bool ReadOnly { get; set; }
}

[Serializable]
internal sealed class VanguardTacticalCoordinationResolvedAnchor
{
    public VanguardTacticalAuthoringAnchor TacticalAnchor { get; set; } = new();
    public string Origin { get; set; } = string.Empty;
    public bool ReadOnly { get; set; }
}

[Serializable]
internal sealed class VanguardTacticalCoordinationRuntimeTopologyDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string MapId { get; set; } = string.Empty;
    public string MapRevision { get; set; } = string.Empty;
    public string EftClientVersion { get; set; } = string.Empty;
    public string BaseSourcePackVersion { get; set; } = string.Empty;
    public List<VanguardTacticalCoordinationResolvedPortal> Portals { get; set; } = new();
    public List<VanguardTacticalCoordinationResolvedPassagePath> PassagePaths { get; set; } = new();
    public List<VanguardTacticalCoordinationResolvedAnchor> TacticalAnchors { get; set; } = new();
}
#endif
