using SPTarkov.Server.Core.Models.Spt.Mod;

// Responsibility: Declare SPT server mod identity, release compatibility, dependency and incompatibility metadata.
// Flow: Provide the mod GUID, semantic version, supported SPT range, incompatible mod IDs, MoreBotsAPI requirement and license.
// Authority boundary: SPT mod loading consumes these declarations; this record does not modify server gameplay or persisted profiles.
// Invariant: Semantic version, SPT compatibility and dependency constraints remain explicit loader-facing contracts.
namespace Vanguard.Server;

public sealed record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.hiban.vanguard";
    public override string Name { get; init; } = "Vanguard";
    public override string Author { get; init; } = "Hi-Ban";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new(VanguardBuildVersion.Value);
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; } = new()
    {
        // PiT Fireteam and Vanguard both own friendly/persistent PMC squad functionality.
        "xyz.pit.fireteam",
    };
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        ["com.morebotsapi.tacticaltoaster"] = new SemanticVersioning.Range(">=2.0.1"),
    };
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; }
    public override string License { get; init; } = "MPL-2.0";
}
