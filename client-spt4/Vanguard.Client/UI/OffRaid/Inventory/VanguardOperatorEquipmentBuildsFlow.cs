using System;
using Vanguard.Client.Diagnostics;

// Responsibility: Presents and coordinates Operator Equipment Builds Flow in the Off-Raid Operator inventory bridge.
// Flow: Canonical API/runtime state is projected into view models and Unity/TMP controls; explicit user actions are delegated back through API/service boundaries.
// Authority boundary: Presentation layer only; it does not become persistence, economy, medical, or raid-runtime authority.
// Invariant: UI refreshes are idempotent from canonical state and temporary view state must not outlive its owning screen/session.
namespace Vanguard.Client.UI.OffRaid.Inventory;

/// <summary>
/// Keeps Vanguard's direct Operator inventory session alive while EFT temporarily
/// navigates into the native Equipment Builds flow.  The direct InventoryScreen
/// controller remains the authority for deciding when the nested flow has returned.
/// </summary>
internal static class VanguardOperatorEquipmentBuildsFlow
{
    private static readonly object Gate = new();

    private static object? directInventoryScreenController;
    private static object? directInventoryBackendController;
    private static object? activeEquipmentBuildsController;
    private static string? capturedOperatorId;
    private static bool nativeBuildsFlowActive;
    private static int transitionGeneration;

    public static bool HasActiveNestedNavigationLease
    {
        get
        {
            lock (Gate)
            {
                return nativeBuildsFlowActive
                    && directInventoryScreenController != null
                    && VanguardOperatorInventoryModeClientState.IsActive;
            }
        }
    }

    public static void CaptureDirectInventoryScreen(object? screenController, object? inventoryController, string? operatorId, string source)
    {
        lock (Gate)
        {
            directInventoryScreenController = screenController;
            directInventoryBackendController = inventoryController;
            activeEquipmentBuildsController = null;
            capturedOperatorId = operatorId;
            nativeBuildsFlowActive = false;
            transitionGeneration = 0;
        }

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
            $"direct_inventory_captured source={source}; operator={operatorId ?? "<none>"}; screenController={DescribeType(screenController)}; inventoryController={DescribeType(inventoryController)}; nativeBuildsFlowActive=false");
    }

    public static void ReplaceDirectInventoryScreenController(object? screenController, object? inventoryController, string? operatorId, string source)
    {
        string? effectiveOperatorId;
        bool backendControllerChanged;
        bool preservedBuildsFlow;
        int generation;
        lock (Gate)
        {
            if (!VanguardOperatorInventoryModeClientState.IsActive || screenController == null)
            {
                return;
            }

            backendControllerChanged = directInventoryBackendController != null
                && inventoryController != null
                && !ReferenceEquals(directInventoryBackendController, inventoryController);
            directInventoryScreenController = screenController;
            if (inventoryController != null)
            {
                directInventoryBackendController = inventoryController;
            }

            capturedOperatorId = operatorId ?? capturedOperatorId ?? VanguardOperatorInventoryModeClientState.OperatorId;
            effectiveOperatorId = capturedOperatorId;
            preservedBuildsFlow = nativeBuildsFlowActive;
            generation = transitionGeneration;
        }

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
            $"direct_inventory_screen_controller_replaced source={source}; operator={effectiveOperatorId ?? "<none>"}; screenController={DescribeType(screenController)}; inventoryController={DescribeType(inventoryController)}; backendControllerChanged={backendControllerChanged}; nativeBuildsFlowPreserved={preservedBuildsFlow}; generation={generation}");
    }

    public static bool TryPrepareNativeBuildInventoryController(object? requestedInventoryController, string source, out object? effectiveInventoryController)
    {
        string? operatorId;
        int generation;
        bool alreadyMatched;

        lock (Gate)
        {
            effectiveInventoryController = null;
            if (!VanguardOperatorInventoryModeClientState.IsActive
                || directInventoryScreenController == null
                || directInventoryBackendController == null)
            {
                return false;
            }

            effectiveInventoryController = directInventoryBackendController;
            alreadyMatched = ReferenceEquals(requestedInventoryController, directInventoryBackendController);
            nativeBuildsFlowActive = true;
            transitionGeneration++;
            generation = transitionGeneration;
            operatorId = capturedOperatorId ?? VanguardOperatorInventoryModeClientState.OperatorId;
        }

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
            $"native_build_inventory_controller_prepared source={source}; operator={operatorId ?? "<none>"}; generation={generation}; requestedController={DescribeType(requestedInventoryController)}; effectiveController={DescribeType(effectiveInventoryController)}; alreadyMatchedOperator={alreadyMatched}; substituted={!alreadyMatched}; sessionRemainsNativePlayerSession=true");
        return true;
    }

    public static bool TryBeginNativeBuildsTransition(object? equipmentBuildsController, object? buildsBackendInventoryController, string source)
    {
        string? operatorId;
        int generation;
        string? directControllerType;
        string? directInventoryControllerType;
        bool backendControllerMatches;

        lock (Gate)
        {
            if (!VanguardOperatorInventoryModeClientState.IsActive || directInventoryScreenController == null)
            {
                return false;
            }

            nativeBuildsFlowActive = true;
            activeEquipmentBuildsController = equipmentBuildsController;
            transitionGeneration++;
            generation = transitionGeneration;
            operatorId = capturedOperatorId ?? VanguardOperatorInventoryModeClientState.OperatorId;
            directControllerType = DescribeType(directInventoryScreenController);
            directInventoryControllerType = DescribeType(directInventoryBackendController);
            backendControllerMatches = directInventoryBackendController != null
                && ReferenceEquals(buildsBackendInventoryController, directInventoryBackendController);
        }

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
            $"native_builds_transition_started source={source}; operator={operatorId ?? "<none>"}; generation={generation}; buildsController={DescribeType(equipmentBuildsController)}; buildsBackendController={DescribeType(buildsBackendInventoryController)}; directInventoryController={directInventoryControllerType}; backendControllerMatchesOperator={backendControllerMatches}; directScreenController={directControllerType}; commitDeferredUntilDirectInventoryReturn=true");
        return true;
    }

    public static bool ShouldDeferDirectInventoryClose(string source)
    {
        string? operatorId;
        int generation;
        bool shouldDefer;

        lock (Gate)
        {
            shouldDefer = nativeBuildsFlowActive
                && directInventoryScreenController != null
                && VanguardOperatorInventoryModeClientState.IsActive;
            operatorId = capturedOperatorId ?? VanguardOperatorInventoryModeClientState.OperatorId;
            generation = transitionGeneration;
        }

        if (shouldDefer)
        {
            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
                $"direct_inventory_close_deferred source={source}; operator={operatorId ?? "<none>"}; generation={generation}; reason=native_equipment_builds_subflow_active");
        }

        return shouldDefer;
    }

    public static bool NotifyDirectInventoryShown(object? screenController, string source)
    {
        string? operatorId;
        int generation;
        bool returned;

        lock (Gate)
        {
            returned = nativeBuildsFlowActive
                && directInventoryScreenController != null
                && ReferenceEquals(screenController, directInventoryScreenController);
            if (!returned)
            {
                return false;
            }

            nativeBuildsFlowActive = false;
            activeEquipmentBuildsController = null;
            operatorId = capturedOperatorId ?? VanguardOperatorInventoryModeClientState.OperatorId;
            generation = transitionGeneration;
        }

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
            $"native_builds_transition_returned source={source}; operator={operatorId ?? "<none>"}; generation={generation}; directController={DescribeType(screenController)}; nextDirectCloseWillCommit=true");
        return true;
    }

    public static void Clear(string source)
    {
        bool hadState;
        string? operatorId;
        int generation;
        string? buildsControllerType;

        lock (Gate)
        {
            hadState = directInventoryScreenController != null || directInventoryBackendController != null || nativeBuildsFlowActive || activeEquipmentBuildsController != null;
            operatorId = capturedOperatorId ?? VanguardOperatorInventoryModeClientState.OperatorId;
            generation = transitionGeneration;
            buildsControllerType = DescribeType(activeEquipmentBuildsController);

            directInventoryScreenController = null;
            directInventoryBackendController = null;
            activeEquipmentBuildsController = null;
            capturedOperatorId = null;
            nativeBuildsFlowActive = false;
            transitionGeneration = 0;
        }

        if (hadState)
        {
            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
                $"native_builds_flow_cleared source={source}; operator={operatorId ?? "<none>"}; generation={generation}; buildsController={buildsControllerType}");
        }
    }

    private static string DescribeType(object? value)
    {
        return value?.GetType().FullName ?? value?.GetType().Name ?? "<none>";
    }
}
