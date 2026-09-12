using System.Text.Json.Nodes;

// Responsibility: carries the smallest EFT-compatible visual read model needed by the Squad Preview renderer.
// Flow: the server projects persistent Operator profile truth into GClass2214-compatible Info/Customization/Equipment JSON.
// Authority boundary: presentation transport only; no gameplay state or mutable session object is exposed as authority.
// Invariant: every returned record was read from an existing persistent profile and passed owner/membership/identity consistency checks.
namespace Vanguard.Server.Operators.Responses;

public sealed class VanguardOperatorPreviewVisualResponse
{
    public bool Success { get; set; }
    public string Reason { get; set; } = "unknown";
    public string RequestedProfileId { get; set; } = string.Empty;
    public string StorageProfileId { get; set; } = string.Empty;
    public int RequestedOperatorCount { get; set; }
    public int ReturnedOperatorCount { get; set; }
    public List<string> UnavailableOperatorIds { get; set; } = [];
    public List<VanguardOperatorPreviewVisualProjection> Operators { get; set; } = [];
    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string BuildLabel { get; set; } = VanguardBuildVersion.BuildLabel;
}

public sealed class VanguardOperatorPreviewVisualProjection
{
    public string OperatorId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string VisualRevision { get; set; } = string.Empty;
    public string VisualFingerprint { get; set; } = string.Empty;
    public JsonObject Visual { get; set; } = new();
}
