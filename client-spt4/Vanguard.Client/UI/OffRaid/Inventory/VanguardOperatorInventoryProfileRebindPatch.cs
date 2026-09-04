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
            VanguardOperatorInventoryNavigationDecision decision = VanguardOperatorInventoryNavigationPolicy.Evaluate(screenName, turnOn);

            if (!decision.RouteAllowed)
            {
                if (VanguardOperatorInventoryNavigationPolicy.TryReserveBlockedSignal(screenName))
                {
                    VanguardClientDiagnosticsLog.Info(
                        VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
                        $"owner=client; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; session_active=True; requested_route={screenName}; route_allowed=False; reason={decision.Reason}; nested_flow={decision.NestedFlowActive}; equipment_builds_lease={decision.EquipmentBuildsLease}; session_preserved=True; commit_triggered=False; exit_triggered=False; reload_triggered=False; screen={VanguardOperatorDirectInventoryLifecycle.DescribeCurrentScreen()}");
                    VanguardOperatorInventoryNavigationPolicy.ShowBlockedNavigationNotice();
                }

                // Prefix suppression occurs before MainMenuController can cross an
                // authority boundary that has not been qualified for the Operator session.
                return false;
            }

            if (!turnOn || decision.Disposition == VanguardOperatorInventoryNavigationDisposition.AllowNative)
            {
                return true;
            }

            if (decision.Disposition == VanguardOperatorInventoryNavigationDisposition.AllowNativePreserveSession)
            {
                VanguardOperatorInventorySessionNavigation.BeginPreservedNavigation(screenName, "main_menu_show_screen");
                VanguardClientDiagnosticsLog.Info(
                    VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
                    $"owner=client; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; session_active=True; requested_route={screenName}; route_allowed=True; reason={decision.Reason}; nested_flow={decision.NestedFlowActive}; equipment_builds_lease={decision.EquipmentBuildsLease}; session_preserved=True; commit_triggered=False; exit_triggered=False; reload_triggered=False");
                return true;
            }

            if (decision.Disposition == VanguardOperatorInventoryNavigationDisposition.ReturnToOperatorInventory)
            {
                if (VanguardOperatorDirectEquipmentScreenEntry.TryReturnToActiveOperatorInventory("character_route", out string returnReason))
                {
                    VanguardClientDiagnosticsLog.Info(
                        VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
                        $"owner=client; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; session_active=True; requested_route={screenName}; route_allowed=True; reason={decision.Reason}; action=return_to_captured_operator_inventory; result={returnReason}; session_preserved=True; commit_triggered=False; exit_triggered=False; reload_triggered=False");
                    return false;
                }

                VanguardClientDiagnosticsLog.Warning(
                    VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
                    $"owner=client; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; session_active=True; requested_route={screenName}; route_allowed=False; reason=return_to_operator_inventory_failed:{returnReason}; session_preserved=True; disposition=fail_closed");
                VanguardOperatorInventoryNavigationPolicy.ShowBlockedNavigationNotice();
                return false;
            }

            if (decision.Disposition == VanguardOperatorInventoryNavigationDisposition.ExitSessionToMainMenu)
            {
                if (VanguardOperatorDirectEquipmentScreenEntry.TryBeginExplicitSessionExit("main_menu_route", out string exitReason))
                {
                    VanguardClientDiagnosticsLog.Info(
                        VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
                        $"owner=client; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; session_active=True; requested_route={screenName}; route_allowed=True; reason={decision.Reason}; action=commit_exit_restore_then_main_menu_reload; result={exitReason}; session_preserved=False; commit_triggered=True; exit_triggered=True; reload_triggered=True");
                    // Vanguard owns this route transition. The proven profile/menu reload
                    // performed after server exit lands on Main Menu, so the original EFT
                    // ShowScreen call is deliberately suppressed to avoid racing the commit.
                    return false;
                }

                VanguardClientDiagnosticsLog.Warning(
                    VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
                    $"owner=client; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; session_active=True; requested_route={screenName}; route_allowed=False; reason=explicit_session_exit_not_started:{exitReason}; session_preserved=True; disposition=fail_closed");
                VanguardOperatorInventoryNavigationPolicy.ShowBlockedNavigationNotice();
                return false;
            }

            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
                $"navigation_guard_unhandled_disposition disposition={decision.Disposition}; requested_route={screenName}; session_active=True; disposition=fail_closed");
            return false;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
                $"navigation_guard_failed reason={exception.GetType().Name}:{exception.Message}; session_active={VanguardOperatorInventoryModeClientState.IsActive}; disposition=fail_closed_unverified_recovery");

            // An unexpected guard exception does not prove that the Operator technical session
            // and temporary UI authority were fully restored. Fail closed while the session is active.
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
