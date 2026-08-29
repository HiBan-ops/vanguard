using SPTarkov.Server.Core.Models.Spt.Mod;

// Responsibility: Provides Mod Metadata support for the Vanguard server.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Server;

public sealed record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.hiban.vanguard";
    public override string Name { get; init; } = "Vanguard";
    public override string Author { get; init; } = "Hi-Ban";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new(VanguardBuildVersion.Value);
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        ["com.morebotsapi.tacticaltoaster"] = new SemanticVersioning.Range(">=2.0.1"),
    };
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; }
    public override string License { get; init; } = "MIT";
}
