using System;
using System.Linq;
using System.Reflection;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.UI.OffRaid.Localization;

#if SPT_CLIENT
using HarmonyLib;
#endif

// Responsibility: centralizes the main-menu route policy while an Operator inventory transaction is active.
// Flow: MainMenuController.ShowScreen requests are classified before EFT changes screens; compatible off-raid routes preserve the same session, Player returns to the captured Operator inventory, and MainMenu explicitly completes the session.
// Authority boundary: navigation policy only; commit/exit remains owned by the direct inventory lifecycle and server inventory-mode service.
// Invariant: UI screen changes are never interpreted as session completion unless the route is an explicit authority boundary.
namespace Vanguard.Client.UI.OffRaid.Inventory;

internal enum VanguardOperatorInventoryNavigationDisposition
{
    AllowNative,
    AllowNativePreserveSession,
    ReturnToOperatorInventory,
    ExitSessionToMainMenu,
    Block
}

internal readonly record struct VanguardOperatorInventoryNavigationDecision(
    VanguardOperatorInventoryNavigationDisposition Disposition,
    string Reason,
    bool NestedFlowActive,
    bool EquipmentBuildsLease)
{
    public bool RouteAllowed => Disposition != VanguardOperatorInventoryNavigationDisposition.Block;
}

internal static class VanguardOperatorInventoryNavigationPolicy
{
    private static readonly object SignalGate = new();
    private static readonly TimeSpan BlockedSignalThrottle = TimeSpan.FromMilliseconds(1500);
    private static DateTimeOffset lastBlockedSignalUtc = DateTimeOffset.MinValue;
    private static string lastBlockedRoute = string.Empty;

    public static VanguardOperatorInventoryNavigationDecision Evaluate(string? requestedRoute, bool turnOn)
    {
        string route = NormalizeRoute(requestedRoute);
        bool sessionActive = VanguardOperatorInventoryModeClientState.IsActive;
        bool equipmentBuildsLease = VanguardOperatorEquipmentBuildsFlow.HasActiveNestedNavigationLease;

        if (!sessionActive)
        {
            return Decide(VanguardOperatorInventoryNavigationDisposition.AllowNative, "inventory_session_inactive", equipmentBuildsLease);
        }

        // EFT emits route deactivation callbacks while unwinding screens. They do not
        // establish a new authority boundary and are allowed to complete natively.
        if (!turnOn)
        {
            return Decide(VanguardOperatorInventoryNavigationDisposition.AllowNative, "route_deactivation", equipmentBuildsLease);
        }

        // Character remains the active Operator's inventory for the whole transaction.
        // Do not call the direct-entry constructor a second time: reuse the exact screen
        // controller captured when the session began.
        if (RouteEquals(route, "Player"))
        {
            return Decide(VanguardOperatorInventoryNavigationDisposition.ReturnToOperatorInventory, "return_to_active_operator_inventory", equipmentBuildsLease);
        }

        // Main Menu is the explicit Operator-session authority boundary. Vanguard
        // commits once, exits server inventory mode, restores player authority and then
        // performs the already-proven full menu/profile reconciliation.
        if (RouteEquals(route, "MainMenu"))
        {
            return Decide(VanguardOperatorInventoryNavigationDisposition.ExitSessionToMainMenu, "explicit_main_menu_session_exit", equipmentBuildsLease);
        }

        // These routes are compatible with keeping the Operator transaction alive.
        // Trade/RagFair are supported by the server's player-purchase projection while
        // inventory mode is active. EditBuild retains its dedicated Operator controller
        // substitution. WeaponModding is also qualified because ItemUiContext is rebound
        // to the live Operator controller for the active transaction. Handbook/Chat/Settings/NewsHub
        // stay within the off-raid menu.
        if (RouteEquals(route, "EditBuild")
            || RouteEquals(route, "WeaponModding")
            || RouteEquals(route, "Trade")
            || RouteEquals(route, "RagFair")
            || RouteEquals(route, "Handbook")
            || RouteEquals(route, "Chat")
            || RouteEquals(route, "Settings")
            || RouteEquals(route, "NewsHub"))
        {
            string reason = equipmentBuildsLease && (RouteEquals(route, "Trade") || RouteEquals(route, "RagFair"))
                ? "equipment_builds_nested_economy_session_preserved"
                : "compatible_offraid_navigation_session_preserved";
            return Decide(VanguardOperatorInventoryNavigationDisposition.AllowNativePreserveSession, reason, equipmentBuildsLease);
        }

        // Hideout is intentionally still excluded: it owns a broader profile/
        // controller graph than the validated menu/economy routes. Raid/game-mode/session
        // boundaries likewise require player authority and must not inherit the temporary
        // Operator transaction. Unknown routes fail closed until qualified.
        return Decide(VanguardOperatorInventoryNavigationDisposition.Block, BlockReason(route), equipmentBuildsLease);
    }

    public static bool TryReserveBlockedSignal(string? requestedRoute)
    {
        string route = NormalizeRoute(requestedRoute);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (SignalGate)
        {
            if (string.Equals(lastBlockedRoute, route, StringComparison.OrdinalIgnoreCase)
                && now - lastBlockedSignalUtc < BlockedSignalThrottle)
            {
                return false;
            }

            lastBlockedRoute = route;
            lastBlockedSignalUtc = now;
            return true;
        }
    }

    public static void ShowBlockedNavigationNotice()
    {
#if SPT_CLIENT
        string message = VanguardOperatorsLocalizationService.Get("inventory.navigation.blocked");
        try
        {
            Type? notificationManagerType = AccessTools.TypeByName("NotificationManagerClass");
            MethodInfo? displayMethod = notificationManagerType?
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                {
                    if (!string.Equals(method.Name, "DisplayWarningNotification", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length >= 1 && parameters[0].ParameterType == typeof(string);
                });

            if (displayMethod == null)
            {
                VanguardClientDiagnosticsLog.Warning(
                    VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
                    "blocked_navigation_notice_unavailable reason=DisplayWarningNotification_not_found");
                return;
            }

            ParameterInfo[] signature = displayMethod.GetParameters();
            object?[] args = new object?[signature.Length];
            args[0] = message;
            for (int index = 1; index < args.Length; index++)
            {
                args[index] = signature[index].HasDefaultValue
                    ? signature[index].DefaultValue
                    : signature[index].ParameterType.IsValueType
                        ? Activator.CreateInstance(signature[index].ParameterType)
                        : null;
            }

            displayMethod.Invoke(null, args);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
                $"blocked_navigation_notice_failed reason={exception.GetType().Name}:{exception.Message}");
        }
#endif
    }

    private static VanguardOperatorInventoryNavigationDecision Decide(
        VanguardOperatorInventoryNavigationDisposition disposition,
        string reason,
        bool equipmentBuildsLease)
    {
        return new VanguardOperatorInventoryNavigationDecision(
            disposition,
            reason,
            equipmentBuildsLease,
            equipmentBuildsLease);
    }

    private static string BlockReason(string route)
    {
        if (RouteEquals(route, "Play") || RouteEquals(route, "GoInRaid") || RouteEquals(route, "ToggleGameMode"))
        {
            return "raid_or_game_mode_requires_player_authority";
        }

        if (RouteEquals(route, "Hideout"))
        {
            return "hideout_profile_graph_not_qualified_for_operator_session";
        }

        if (RouteEquals(route, "Exit") || RouteEquals(route, "Logout") || RouteEquals(route, "Reconnect"))
        {
            return "account_or_connection_boundary_requires_operator_exit";
        }

        if (RouteEquals(route, "HideScreen"))
        {
            return "screen_hide_not_qualified_for_operator_session";
        }

        return "operator_session_route_not_qualified";
    }

    private static bool RouteEquals(string route, string expected)
    {
        return string.Equals(route, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoute(string? route)
    {
        return string.IsNullOrWhiteSpace(route) ? "<unknown>" : route.Trim();
    }
}
