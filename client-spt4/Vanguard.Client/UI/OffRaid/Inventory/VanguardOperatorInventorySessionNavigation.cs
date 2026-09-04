using System;
using Vanguard.Client.Diagnostics;

// Responsibility: preserves one active Operator inventory transaction across compatible off-raid navigation until an explicit authority boundary is requested.
// Flow: MainMenu ShowScreen policy arms a preservation lease before native navigation; InventoryScreen.OnClose consults that lease; returning to the captured Operator inventory clears it; MainMenu explicitly converts it into session completion.
// Authority boundary: client navigation/lifecycle coordination only; it never commits Operator equipment or changes server inventory-mode truth.
// Invariant: compatible off-raid screen changes cannot implicitly commit/exit an Operator session, while explicit session exit always cancels the preservation lease.
namespace Vanguard.Client.UI.OffRaid.Inventory;

internal static class VanguardOperatorInventorySessionNavigation
{
    private static readonly object Gate = new();

    private static bool preserveSessionLeaseActive;
    private static bool explicitExitRequested;
    private static string activeRoute = "<none>";
    private static string activeSource = "<none>";
    private static int generation;

    public static bool HasPreservedNavigationLease
    {
        get
        {
            lock (Gate)
            {
                return preserveSessionLeaseActive
                    && !explicitExitRequested
                    && VanguardOperatorInventoryModeClientState.IsActive;
            }
        }
    }

    public static void BeginPreservedNavigation(string? route, string source)
    {
        if (!VanguardOperatorInventoryModeClientState.IsActive)
        {
            return;
        }

        int currentGeneration;
        string normalizedRoute = Normalize(route);
        lock (Gate)
        {
            preserveSessionLeaseActive = true;
            explicitExitRequested = false;
            activeRoute = normalizedRoute;
            activeSource = source;
            generation++;
            currentGeneration = generation;
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
            $"session_navigation_lease_started operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; route={normalizedRoute}; source={source}; generation={currentGeneration}; session_preserved=True");
    }

    public static bool ShouldDeferDirectInventoryClose(string source, out string reason)
    {
        string route;
        string leaseSource;
        int currentGeneration;
        bool defer;
        lock (Gate)
        {
            defer = preserveSessionLeaseActive
                && !explicitExitRequested
                && VanguardOperatorInventoryModeClientState.IsActive;
            route = activeRoute;
            leaseSource = activeSource;
            currentGeneration = generation;
        }

        if (!defer)
        {
            reason = "no_preserved_navigation_lease";
            return false;
        }

        reason = $"preserved_offraid_navigation:{route}";
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
            $"session_navigation_close_deferred operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; route={route}; leaseSource={leaseSource}; closeSource={source}; generation={currentGeneration}; commit_triggered=False; exit_triggered=False; reload_triggered=False");
        return true;
    }

    public static void NotifyOperatorInventoryShown(string source)
    {
        bool hadLease;
        string route;
        int currentGeneration;
        lock (Gate)
        {
            hadLease = preserveSessionLeaseActive || explicitExitRequested;
            route = activeRoute;
            currentGeneration = generation;
            preserveSessionLeaseActive = false;
            explicitExitRequested = false;
            activeRoute = "<none>";
            activeSource = "<none>";
        }

        if (hadLease)
        {
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
                $"session_navigation_returned_to_operator_inventory operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; priorRoute={route}; source={source}; generation={currentGeneration}; next_direct_close_will_commit=True");
        }
    }

    public static void BeginExplicitExit(string? route, string source)
    {
        int currentGeneration;
        string normalizedRoute = Normalize(route);
        lock (Gate)
        {
            preserveSessionLeaseActive = false;
            explicitExitRequested = true;
            activeRoute = normalizedRoute;
            activeSource = source;
            generation++;
            currentGeneration = generation;
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
            $"session_navigation_explicit_exit_requested operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; route={normalizedRoute}; source={source}; generation={currentGeneration}");
    }

    public static void CancelExplicitExitAfterFailure(string source, string reason)
    {
        bool changed;
        string route;
        int currentGeneration;
        lock (Gate)
        {
            changed = explicitExitRequested;
            route = activeRoute;
            currentGeneration = generation;
            explicitExitRequested = false;
            activeRoute = "<none>";
            activeSource = "<none>";
        }

        if (changed)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
                $"session_navigation_explicit_exit_cancelled source={source}; priorRoute={route}; generation={currentGeneration}; reason={reason}; session_preserved={VanguardOperatorInventoryModeClientState.IsActive}");
        }
    }

    public static void Clear(string source)
    {
        bool hadState;
        string route;
        int currentGeneration;
        lock (Gate)
        {
            hadState = preserveSessionLeaseActive || explicitExitRequested || generation != 0;
            route = activeRoute;
            currentGeneration = generation;
            preserveSessionLeaseActive = false;
            explicitExitRequested = false;
            activeRoute = "<none>";
            activeSource = "<none>";
            generation = 0;
        }

        if (hadState)
        {
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
                $"session_navigation_state_cleared source={source}; priorRoute={route}; generation={currentGeneration}");
        }
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<unknown>" : value.Trim();
    }
}
