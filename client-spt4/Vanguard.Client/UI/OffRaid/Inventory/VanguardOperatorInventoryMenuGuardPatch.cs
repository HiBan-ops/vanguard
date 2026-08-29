using System;
using System.Linq;
using System.Reflection;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.UI.OffRaid.Localization;

#if SPT_CLIENT
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: Bridges EFT/SPT/Fika callbacks into Operator Inventory Menu Guard Patch for the Off-Raid Operator inventory bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.UI.OffRaid.Inventory;

#if SPT_CLIENT
internal sealed class VanguardOperatorInventoryMenuGuardPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type menuScreenType = AccessTools.TypeByName("EFT.UI.MenuScreen")
            ?? AccessTools.TypeByName("MenuScreen")
            ?? throw new InvalidOperationException("Vanguard inventory mode guard failed: MenuScreen type not found.");
        return AccessTools.Method(menuScreenType, "method_6")
            ?? menuScreenType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).First(method => method.Name == "Show");
    }

    [PatchPostfix]
    private static void Postfix(object __instance)
    {
        if (!VanguardOperatorInventoryModeClientState.IsActive || __instance == null)
        {
            return;
        }

        try
        {
            object? playButton = AccessTools.Field(__instance.GetType(), "_playButton")?.GetValue(__instance);
            if (playButton == null)
            {
                return;
            }

            AccessTools.Property(playButton.GetType(), "Interactable")?.SetValue(playButton, false);
            MethodInfo? tooltipMethod = playButton.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "SetDisabledTooltip" && method.GetParameters().Length >= 1);
            string operatorName = VanguardOperatorInventoryModeClientState.OperatorDisplayName
                ?? VanguardOperatorsLocalizationService.Get("general.operator");
            tooltipMethod?.Invoke(playButton, BuildArguments(tooltipMethod, VanguardOperatorsLocalizationService.Format("inventory.guard", operatorName)));
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_INVENTORY_OFFRAID_STATUS", $"play guard failed: {exception.Message}");
        }
    }

    private static object?[] BuildArguments(MethodInfo method, string text)
    {
        ParameterInfo[] parameters = method.GetParameters();
        object?[] args = new object?[parameters.Length];
        if (args.Length > 0)
        {
            args[0] = text;
        }

        for (int i = 1; i < args.Length; i++)
        {
            args[i] = parameters[i].ParameterType == typeof(bool) ? false : null;
        }

        return args;
    }
}
#else
internal sealed class VanguardOperatorInventoryMenuGuardPatch
{
    public void Enable()
    {
    }
}
#endif
