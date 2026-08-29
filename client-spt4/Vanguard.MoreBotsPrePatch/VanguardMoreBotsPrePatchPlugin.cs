using BepInEx;

// Responsibility: Bootstraps More Bots Pre Patch Plugin for the MoreBots pre-patch bootstrap.
// Flow: The host loader registers dependencies/patches/services once, then hands ongoing behavior to dedicated runtime/domain components.
// Authority boundary: Bootstrap owns registration only; it must not duplicate the runtime or persistence authorities it wires together.
// Invariant: Initialization is repeat-safe for the supported lifecycle and failures remain visible without partially inventing runtime state.
namespace Vanguard.MoreBotsPrePatch;

[BepInPlugin(PluginGuid, PluginName, VanguardBuildVersion.Value)]
[BepInDependency("com.morebotsapiprepatch.tacticaltoaster", BepInDependency.DependencyFlags.HardDependency)]
public sealed class VanguardMoreBotsPrePatchPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.hiban.vanguard.morebots.prepatch";
    public const string PluginName = "Vanguard MoreBots PrePatch";

    private void Awake()
    {
        Logger.LogInfo($"[VANGUARD_STARTUP] MoreBots prepatch loaded; version={VanguardBuildVersion.Value}");
    }
}
