#if SPT_CLIENT
using System;
using System.Collections.Generic;

// Responsibility: Defines data/state contracts used by the tactical-authoring runtime, centered on Tactical Authoring Live Preview Models.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

[Serializable]
internal sealed class VanguardTacticalAuthoringHeadlessPreviewResult
{
    public string OwnerProfileId { get; set; } = string.Empty;
    public string LiveSessionId { get; set; } = string.Empty;
    public string MapId { get; set; } = string.Empty;
    public long AuthorRevision { get; set; }
    public string SelectedZoneId { get; set; } = string.Empty;
    public string State { get; set; } = "none";
    public string Reason { get; set; } = "none";
    public int OperatorCount { get; set; }
    public int CandidateSlotCount { get; set; }
    public int HeadlessValidSlotCount { get; set; }
    public int AssignedOperatorCount { get; set; }
    public List<VanguardTacticalAuthoringHeadlessSlotResult> Slots { get; set; } = new();
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public string HeadlessBuild { get; set; } = string.Empty;
}

[Serializable]
internal sealed class VanguardTacticalAuthoringHeadlessSlotResult
{
    public string SlotId { get; set; } = string.Empty;
    public string State { get; set; } = "PENDING";
    public string Reason { get; set; } = "none";
    public VanguardVector3Dto? ProjectedPosition { get; set; }
    public float BestPathDistanceMeters { get; set; }
    public string AssignedOperatorId { get; set; } = string.Empty;
    public string AssignedBotProfileId { get; set; } = string.Empty;
    public string AssignedCallsign { get; set; } = string.Empty;
    public string MovementState { get; set; } = "unassigned";
}
#endif
