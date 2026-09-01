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

            if (VanguardOperatorDirectEquipmentScreenEntry.TryOpenFromMainMenu(
                    __instance,
                    "vanilla_player_button",
                    out string reason,
                    out bool vanillaFallbackSafe))
            {
                VanguardClientDiagnosticsLog.Info(
                    "VANGUARD_OPERATOR_PROFILE_REBIND_STATUS",
                    $"vanilla_player_redirected_to_direct_operator_inventory reason={reason}; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; inventoryProfile={VanguardOperatorInventoryModeClientState.InventoryProfileId ?? "<none>"}");
                return false;
            }

            if (vanillaFallbackSafe)
            {
                VanguardClientDiagnosticsLog.Warning(
                    "VANGUARD_OPERATOR_PROFILE_REBIND_STATUS",
                    $"vanilla_player_fallback_after_recovered_operator_open_failure reason={reason}; inventoryModeActive={VanguardOperatorInventoryModeClientState.IsActive}");

                // The Operator technical session has been closed and Vanguard's temporary UI state
                // restored.  Resume EFT's original Player ShowScreen call instead of turning a
                // recoverable interop failure into a dead Equipment button.
                return true;
            }

            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_PROFILE_REBIND_STATUS",
                $"vanilla_player_blocked_operator_inventory_not_opened reason={reason}; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; inventoryProfile={VanguardOperatorInventoryModeClientState.InventoryProfileId ?? "<none>"}");

            // Keep the vanilla Player screen blocked while the Operator technical session is still
            // active or another direct-inventory lifecycle is in flight. Opening it against the
            // cached player controller in that state would mix player and Operator authorities.
            return false;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_PROFILE_REBIND_STATUS",
                $"vanilla Player screen guard failed; reason={exception.GetType().Name}: {exception.Message}; vanillaFallbackSafe=false; disposition=fail_closed_unverified_recovery");

            // An unexpected guard exception does not prove that the Operator technical session
            // and temporary UI authority were fully restored. Only the explicit recovered-failure
            // path above may resume EFT's original Player ShowScreen call.
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
