using System;
using System.Reflection;

#if SPT_CLIENT
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: captures EFT's authoritative MainMenuControllerClass instance at the native menu-entry boundary.
// Flow: ShowMenuScreenSync prefix forwards __instance to the preview host coordinator before MenuScreen.Show executes.
// Authority boundary: read-only reference capture only; Vanguard never mutates MainMenuController lifecycle or Player inventory authority here.
// Invariant: preview renderer dependencies come from the exact live controller that owns the current player off-raid session.
namespace Vanguard.Client.UI.SquadPreview.Hosts;

#if SPT_CLIENT
internal sealed class VanguardSquadPreviewMainMenuControllerCapturePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type mainMenuControllerType = AccessTools.TypeByName("MainMenuControllerClass")
            ?? throw new InvalidOperationException("Vanguard squad preview controller capture failed: MainMenuControllerClass type not found.");

        return AccessTools.Method(mainMenuControllerType, "ShowMenuScreenSync")
            ?? throw new MissingMethodException(mainMenuControllerType.FullName, "ShowMenuScreenSync");
    }

    [PatchPrefix]
    private static void PatchPrefix(object __instance)
    {
        VanguardSquadPreviewHostCoordinator.OnMainMenuControllerAvailable(__instance, "show_menu_screen_sync");
    }
}
#else
internal sealed class VanguardSquadPreviewMainMenuControllerCapturePatch
{
    public void Enable()
    {
    }
}
#endif
