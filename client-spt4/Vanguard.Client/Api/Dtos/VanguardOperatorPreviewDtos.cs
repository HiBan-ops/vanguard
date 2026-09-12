using System.Collections.Generic;

// Responsibility: carries the smallest renderer-facing Operator projection used by Squad Preview.
// Flow: the read-only server route returns one or more records whose Visual payload matches EFT's
// GClass2214/LastPlayerState transport shape; the preview adapter converts that payload into native runtime state.
// Authority boundary: transport only. The DTO never creates, repairs or persists Operator gameplay state.
// Invariant: renderer identity/revision metadata stays separate from the opaque native visual payload.
namespace Vanguard.Client.Api.Dtos;

internal sealed class VanguardOperatorPreviewVisualRequestDto
{
    public List<string> OperatorIds { get; set; } = new();
}

internal sealed class VanguardOperatorPreviewVisualResponseDto
{
    public bool Success { get; set; }
    public string? RequestedProfileId { get; set; }
    public string? StorageProfileId { get; set; }
    public int RequestedOperatorCount { get; set; }
    public int ReturnedOperatorCount { get; set; }
    public List<string>? UnavailableOperatorIds { get; set; }
    public List<VanguardOperatorPreviewVisualDto>? Operators { get; set; }
    public string? Reason { get; set; }
}

internal sealed class VanguardOperatorPreviewVisualDto
{
    public string? OperatorId { get; set; }
    public string? DisplayName { get; set; }
    public string? Side { get; set; }
    public string? VisualRevision { get; set; }
    public string? VisualFingerprint { get; set; }

    // Deliberately opaque at the shared DTO layer so the non-SPT target does not take a hard
    // dependency on EFT/Newtonsoft runtime types. Under SPT_CLIENT Json.NET materializes this
    // as JObject and VanguardSquadPreviewVisualAdapter performs the native conversion.
    public object? Visual { get; set; }
}
