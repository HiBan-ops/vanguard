using System;
using System.Linq;
using System.Reflection;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: Bridges EFT/SPT/Fika callbacks into Operator Inventory Profile Rebind Patch for the Off-Raid Operator inventory bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.UI.OffRaid.Inventory;

#if SPT_CLIENT
internal sealed class VanguardOperatorInventoryProfileRebindPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type mainMenuControllerType = AccessTools.TypeByName("MainMenuControllerClass")
            ?? throw new InvalidOperationException("Vanguard operator direct equipment entry failed: MainMenuControllerClass type not found.");

        MethodInfo? method = mainMenuControllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, "ShowScreen", StringComparison.Ordinal))
                {
                    return false;
                }

                ParameterInfo[] parameters = candidate.GetParameters();
                return parameters.Length == 2
                    && parameters[0].ParameterType.Name.IndexOf("EMenuType", StringComparison.OrdinalIgnoreCase) >= 0
                    && parameters[1].ParameterType == typeof(bool);
            });

        if (method == null)
        {
            throw new InvalidOperationException("Vanguard operator direct equipment entry failed: MainMenuControllerClass.ShowScreen(EMenuType,bool) not found.");
        }

        VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_PROFILE_REBIND_STATUS", $"direct equipment Player screen guard target resolved: {method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? "<unknown>"}.{method.Name}");
        return method;
    }

    [PatchPrefix]
    private static bool Prefix(object __instance, object[] __args)
    {
        try
        {
            if (!VanguardOperatorInventoryModeClientState.IsActive || __args.Length < 2)
            {
                return true;
            }

            object? screen = __args[0];
            bool turnOn = __args[1] is bool value && value;
            string screenName = screen?.ToString() ?? string.Empty;
            if (!turnOn || !string.Equals(screenName, "Player", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (VanguardOperatorDirectEquipmentScreenEntry.TryOpenFromMainMenu(__instance, "vanilla_player_button", out string reason))
            {
                VanguardClientDiagnosticsLog.Info(
                    "VANGUARD_OPERATOR_PROFILE_REBIND_STATUS",
                    $"vanilla_player_redirected_to_direct_operator_inventory reason={reason}; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; inventoryProfile={VanguardOperatorInventoryModeClientState.InventoryProfileId ?? "<none>"}");
            }
            else
            {
                VanguardClientDiagnosticsLog.Warning(
                    "VANGUARD_OPERATOR_PROFILE_REBIND_STATUS",
                    $"vanilla_player_blocked_operator_inventory_not_opened reason={reason}; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; inventoryProfile={VanguardOperatorInventoryModeClientState.InventoryProfileId ?? "<none>"}");
            }

            // Never let the vanilla Player screen open against the cached player controller while
            // Vanguard operator equipment mode is active. The direct screen path either opened the
            // Operator inventory or leaves the user in a stable menu state for recovery/exit.
            return false;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_PROFILE_REBIND_STATUS", $"vanilla Player screen guard failed; reason={exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }
}
#else
internal sealed class VanguardOperatorInventoryProfileRebindPatch
{
    public void Enable()
    {
    }
}
#endif
