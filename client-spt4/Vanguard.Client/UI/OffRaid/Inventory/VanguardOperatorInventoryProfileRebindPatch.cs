using System;
using System.Linq;
using System.Reflection;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: Enforces the semantic MainMenuController.ShowScreen navigation policy while a direct Operator inventory session owns the current Off-Raid view authority.
// Flow: Native route requests are classified before EFT changes screens; compatible routes preserve the session, Character returns to the captured Operator inventory, and Main Menu starts the single commit/exit/reconciliation boundary.
// Authority boundary: Navigation interception only; inventory persistence remains server-owned and Player restoration is delegated to the parity-gated direct-exit reconciliation flow.
// Invariant: Unqualified authority boundaries fail closed, compatible routes cannot commit implicitly, and Main Menu completion cannot race a second native ShowScreen transition.
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
            VanguardOperatorProfileTransitionTrace.RecordNavigationDecision(
                screenName,
                turnOn,
                decision.Disposition.ToString(),
                decision.Reason,
                "main_menu_show_screen");

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
                        $"owner=client; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; session_active=True; requested_route={screenName}; route_allowed=True; reason={decision.Reason}; action=commit_exit_restore_then_reconcile_player_menu; result={exitReason}; session_preserved=False; commit_triggered=True; exit_triggered=True; reconciliation_triggered=True; reload_triggered=False; native_reload_decision=parity_gated");
                    // Vanguard owns this route transition. The direct-exit flow lands on Main Menu
                    // through admitted fast reconciliation or, if parity is unsafe/unknown, the native
                    // full-reload fallback. Suppress the original ShowScreen call so it cannot race commit/exit.
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
