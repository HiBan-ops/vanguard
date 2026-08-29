using System;
using System.Reflection;
using Vanguard.Client;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Services;

#if SPT_CLIENT
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: Bridges EFT/SPT/Fika callbacks into Bots Controller State Patch for the raid lifecycle patch bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.Raid.Patches;

#if SPT_CLIENT
internal sealed class VanguardBotsControllerStatePatch : ModulePatch
{
    public static BotsController? ActiveController { get; private set; }

    private static int controllerGeneration;

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(BotsController), "AddActivePLayer")
            ?? throw new InvalidOperationException("BotsController.AddActivePLayer not found for Vanguard raid spawn state patch.");
    }

    [PatchPostfix]
    private static void PatchPostfix(BotsController __instance)
    {
        if (__instance is null)
        {
            return;
        }

        bool isFreshController = !ReferenceEquals(ActiveController, __instance);
        if (isFreshController)
        {
            ActiveController = __instance;
            controllerGeneration++;

            // On Fika headless the matchmaker screen patch is not a reliable raid-cycle
            // boundary after the first raid.  A fresh authoritative BotsController is the
            // earliest stable runtime boundary before SpawnAction queues Vanguard Operators.
            // Resetting here prevents stale manifest/spawn latches from suppressing the
            // next raid spawn while keeping the already validated placement/binding code intact.
            string source = $"bots_controller_new:{controllerGeneration}";
            VanguardRaidOperatorController.ResetForNewAuthorityRaidCycle(source);
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.RaidSpawnStatusTag,
                $"bots controller captured for raid spawn; fresh=True; generation={controllerGeneration}");
            return;
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.RaidSpawnStatusTag,
            $"bots controller captured for raid spawn; fresh=False; generation={controllerGeneration}");
    }
}
#else
internal sealed class VanguardBotsControllerStatePatch
{
    public void Enable() { }
}
#endif
