using System;
using System.Linq;
using System.Reflection;

#if SPT_CLIENT
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: observes the native MenuScreen minimized/restored lifecycle used when leaving/returning to the true Main Menu.
// Flow: postfix forwards the native minimized flag to the host coordinator; original EFT behavior is never intercepted.
// Authority boundary: observation only; Vanguard owns only its preview cleanup/acquisition.
// Invariant: leaving the true Main Menu releases all Vanguard preview bodies before a later return can acquire a fresh host.
namespace Vanguard.Client.UI.SquadPreview.Hosts;

#if SPT_CLIENT
internal sealed class VanguardSquadPreviewMenuMinimizePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type menuScreenType = AccessTools.TypeByName("EFT.UI.MenuScreen")
            ?? AccessTools.TypeByName("MenuScreen")
            ?? throw new InvalidOperationException("Vanguard squad preview lifecycle patch failed: MenuScreen type not found.");

        MethodInfo? target = menuScreenType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "method_9"
                && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType == typeof(bool));

        return target
            ?? throw new MissingMethodException(menuScreenType.FullName, "method_9(bool)");
    }

    [PatchPostfix]
    private static void PatchPostfix(object __instance, bool minimized)
    {
        VanguardSquadPreviewHostCoordinator.OnMenuMinimized(__instance, minimized, "menu_screen_method_9");
    }
}
#else
internal sealed class VanguardSquadPreviewMenuMinimizePatch
{
    public void Enable()
    {
    }
}
#endif
