using System;
using System.Linq;
using System.Reflection;
using Vanguard.Client;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: Bridges EFT/SPT/Fika callbacks into Off Raid Menu Patch for the Off-Raid Operator UI.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.UI.OffRaid;

#if SPT_CLIENT
internal sealed class VanguardOffRaidMenuPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type menuScreenType = AccessTools.TypeByName("EFT.UI.MenuScreen")
            ?? AccessTools.TypeByName("MenuScreen")
            ?? throw new InvalidOperationException("Vanguard off-raid UI patch failed: MenuScreen type not found.");

        MethodInfo? showMethod = menuScreenType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "Show" && method.GetParameters().Length == 3)
            ?? menuScreenType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "Show");

        return showMethod
            ?? throw new InvalidOperationException("Vanguard off-raid UI patch failed: MenuScreen.Show not found.");
    }

    [PatchPostfix]
    private static void PatchPostfix(object __instance)
    {
        try
        {
            if (__instance == null)
            {
                return;
            }

            VanguardOffRaidUiController.TryInitialize(__instance);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Error(VanguardBuildVersion.OffRaidUiStatusTag, exception);
        }
    }
}
#else
internal sealed class VanguardOffRaidMenuPatch
{
    public void Enable()
    {
    }
}
#endif
