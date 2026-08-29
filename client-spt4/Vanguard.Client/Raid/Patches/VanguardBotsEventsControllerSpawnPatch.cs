using System;
using System.Linq;
using System.Reflection;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Services;

#if SPT_CLIENT
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: Bridges EFT/SPT/Fika callbacks into Bots Events Controller Spawn Patch for the raid lifecycle patch bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.Raid.Patches;

#if SPT_CLIENT
internal sealed class VanguardBotsEventsControllerSpawnPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("BotsEventsController")
            ?? throw new InvalidOperationException("BotsEventsController type not found for Vanguard raid spawn patch.");
        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "SpawnAction")
            ?? throw new InvalidOperationException("BotsEventsController.SpawnAction not found for Vanguard raid spawn patch.");
    }

    [PatchPostfix]
    private static void PatchPostfix()
    {
        try
        {
            VanguardRaidOperatorController.QueueSpawn("bots_events_spawn_action");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_RAID_SPAWN_STATUS", $"spawn action patch failed: {exception}");
        }
    }
}
#else
internal sealed class VanguardBotsEventsControllerSpawnPatch
{
    public void Enable() { }
}
#endif
