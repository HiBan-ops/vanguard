using System;
using System.Linq;
using System.Reflection;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
#endif

// Responsibility: binds the native Trader right-side stash presentation to the active Operator live spatial graph.
// Flow: EFT creates and owns its native trading controllers; Vanguard only substitutes the right-side TradingGridView StashGrid + InventoryController at the proven presentation boundary.
// Authority boundary: Trader/Flea controller construction, ISession.Profile, trader state, quest state, wallet/payment rules, backend routing, and server economy authority remain player-owned.
// Invariant: no upper-layer Trader/RagFair controller rebind, session-profile mutation, or parallel inventory authority is created by this patch family.
namespace Vanguard.Client.UI.OffRaid.Inventory;

#if SPT_CLIENT
internal static class VanguardOperatorTradingPresentationRebind
{
    internal sealed class TradingGridBindingSwapState
    {
        public object? EffectiveGrid { get; set; }
        public object? EffectiveController { get; set; }
        public string? OperatorId { get; set; }
        public int Generation { get; set; }
        public string Panel { get; set; } = "<none>";
        public string Path { get; set; } = "<none>";
        public bool GridSubstituted { get; set; }
        public bool ControllerSubstituted { get; set; }
    }

    public static TradingGridBindingSwapState? BeginTradingGridViewOperatorBinding(
        object gridView,
        object[] args,
        ref StashGridClass requestedGrid,
        ref InventoryController requestedController)
    {
        if (!VanguardOperatorInventoryModeClientState.IsActive
            || !VanguardOperatorInventorySessionNavigation.HasPreservedNavigationLease
            || gridView is not Component gridViewComponent
            || args.Length < 5)
        {
            return null;
        }

        string path = DescribeTransformPath(gridViewComponent.transform);
        string panel = path.IndexOf("/TraderDealScreen/Right Person/", StringComparison.OrdinalIgnoreCase) >= 0
            ? "right_person"
            : path.IndexOf("/TraderDealScreen/Left Person/", StringComparison.OrdinalIgnoreCase) >= 0
                ? "left_person"
                : "other";

        // The Trader stock (left side) must remain entirely native. Only the player's right-side
        // stash panel is projected onto the live Operator inventory while an Operator session is active.
        if (!string.Equals(panel, "right_person", StringComparison.Ordinal))
        {
            return null;
        }

        if (!VanguardOperatorEquipmentBuildsFlow.TryGetActiveOperatorInventoryController(
                out object? operatorInventoryControllerObject,
                out string? operatorId,
                out int generation)
            || operatorInventoryControllerObject is not InventoryController operatorInventoryController)
        {
            return null;
        }

        try
        {
            object? operatorProfile = ResolveMember(operatorInventoryController, "Profile_0")
                ?? ResolveMember(operatorInventoryController, "Profile");
            object? operatorInventory = ResolveMember(operatorInventoryController, "Inventory");
            object? operatorStash = operatorInventory == null
                ? null
                : ResolveMember(operatorInventory, "Stash")
                    ?? ResolveMember(operatorInventory, "StashItem")
                    ?? ResolveMember(operatorInventory, "StashItemClass");
            object? operatorStashGrids = operatorStash == null
                ? null
                : ResolveMember(operatorStash, "Grids")
                    ?? ResolveMember(operatorStash, "Grid");
            object? operatorPrimaryStashGridObject = FirstEnumerableValue(operatorStashGrids) ?? operatorStashGrids;
            if (operatorPrimaryStashGridObject is not StashGridClass operatorPrimaryStashGrid)
            {
                VanguardClientDiagnosticsLog.Warning(
                    "VANGUARD_OPERATOR_TRADER_GRID_BINDING_STATUS",
                    $"operator_trader_grid_binding_skipped panel={panel}; operator={operatorId ?? "<none>"}; generation={generation}; path={path}; reason=operator_primary_stash_grid_not_resolved_or_wrong_type; resolvedType={DescribeType(operatorPrimaryStashGridObject)}; noMutation=true");
                return null;
            }

            StashGridClass originalGrid = requestedGrid;
            InventoryController originalController = requestedController;
            object? originalProfile = ResolveMember(originalController, "Profile_0")
                ?? ResolveMember(originalController, "Profile");
            bool gridSubstituted = !ReferenceEquals(originalGrid, operatorPrimaryStashGrid);
            bool controllerSubstituted = !ReferenceEquals(originalController, operatorInventoryController);

            // Harmony must receive the concrete method arguments by reference here. Mutating the
            // aggregate __args array does not reliably replace the references retained by the native
            // TradingGridView, so the two authoritative presentation inputs are rebound explicitly.
            if (gridSubstituted)
            {
                requestedGrid = operatorPrimaryStashGrid;
            }

            if (controllerSubstituted)
            {
                requestedController = operatorInventoryController;
            }

            object? requestedItemContext = args[1];
            object? requestedAssortmentController = args[2];
            object? requestedItemUiContext = args[4];

            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_OPERATOR_TRADER_GRID_BINDING_STATUS",
                $"operator_trader_grid_binding_prepared panel={panel}; operator={operatorId ?? "<none>"}; generation={generation}; path={path}; mutationMechanism=harmony_direct_ref_arguments; originalGrid={DescribeType(originalGrid)}#{DescribeReferenceIdentity(originalGrid)}; operatorGrid={DescribeType(operatorPrimaryStashGrid)}#{DescribeReferenceIdentity(operatorPrimaryStashGrid)}; gridSubstituted={gridSubstituted}; effectiveGridArgument={DescribeType(requestedGrid)}#{DescribeReferenceIdentity(requestedGrid)}; effectiveGridArgumentIsOperator={ReferenceEquals(requestedGrid, operatorPrimaryStashGrid)}; originalController={DescribeType(originalController)}#{DescribeReferenceIdentity(originalController)}; operatorController={DescribeType(operatorInventoryController)}#{DescribeReferenceIdentity(operatorInventoryController)}; controllerSubstituted={controllerSubstituted}; effectiveControllerArgument={DescribeType(requestedController)}#{DescribeReferenceIdentity(requestedController)}; effectiveControllerArgumentIsOperator={ReferenceEquals(requestedController, operatorInventoryController)}; originalProfile={DescribeProfileId(originalProfile)}; operatorProfile={DescribeProfileId(operatorProfile)}; itemContextPreserved={DescribeType(requestedItemContext)}#{DescribeReferenceIdentity(requestedItemContext)}; assortmentControllerPreserved={DescribeType(requestedAssortmentController)}#{DescribeReferenceIdentity(requestedAssortmentController)}; itemUiContextPreserved={DescribeType(requestedItemUiContext)}#{DescribeReferenceIdentity(requestedItemUiContext)}; nativePlayerTraderEconomyAuthorityPreserved=true; rightSidePresentationUsesOperatorLiveGrid=true");

            return new TradingGridBindingSwapState
            {
                EffectiveGrid = operatorPrimaryStashGrid,
                EffectiveController = operatorInventoryController,
                OperatorId = operatorId,
                Generation = generation,
                Panel = panel,
                Path = path,
                GridSubstituted = gridSubstituted,
                ControllerSubstituted = controllerSubstituted
            };
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_TRADER_GRID_BINDING_STATUS",
                $"operator_trader_grid_binding_prepare_failed panel={panel}; path={path}; reason={exception.GetType().Name}:{exception.Message}; noMutationGuaranteed=false");
            return null;
        }
    }

    public static void CompleteTradingGridViewOperatorBinding(
        object gridView,
        TradingGridBindingSwapState? state,
        Exception? exception = null)
    {
        if (state == null)
        {
            return;
        }

        try
        {
            object? boundGrid = ResolveMember(gridView, "Grid");
            object? boundController = ResolveMember(gridView, "_itemController")
                ?? ResolveMember(gridView, "inventoryController_0")
                ?? ResolveMember(gridView, "InventoryController");
            object? boundItemUiContext = ResolveMember(gridView, "itemUiContext_0")
                ?? ResolveMember(gridView, "ItemUiContext");
            object? itemViews = ResolveMember(gridView, "ItemViews");

            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_OPERATOR_TRADER_GRID_BINDING_STATUS",
                $"operator_trader_grid_binding_completed panel={state.Panel}; operator={state.OperatorId ?? "<none>"}; generation={state.Generation}; path={state.Path}; gridSubstituted={state.GridSubstituted}; controllerSubstituted={state.ControllerSubstituted}; boundGrid={DescribeType(boundGrid)}#{DescribeReferenceIdentity(boundGrid)}; boundGridIsOperator={ReferenceEquals(boundGrid, state.EffectiveGrid)}; boundController={DescribeType(boundController)}#{DescribeReferenceIdentity(boundController)}; boundControllerIsOperator={ReferenceEquals(boundController, state.EffectiveController)}; boundItemUiContext={DescribeType(boundItemUiContext)}#{DescribeReferenceIdentity(boundItemUiContext)}; itemViews={DescribeCollectionCount(itemViews)}; exception={(exception == null ? "none" : exception.GetType().Name)}; nativePlayerTraderEconomyAuthorityPreserved=true");
        }
        catch (Exception logException)
        {
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_TRADER_GRID_BINDING_STATUS",
                $"operator_trader_grid_binding_completion_verification_failed panel={state.Panel}; operator={state.OperatorId ?? "<none>"}; reason={logException.GetType().Name}:{logException.Message}");
        }
    }

    private static object? FirstEnumerableValue(object? value)
    {
        if (value is string || value is not System.Collections.IEnumerable enumerable)
        {
            return null;
        }

        foreach (object? entry in enumerable)
        {
            if (entry != null)
            {
                return entry;
            }
        }

        return null;
    }

    private static int DescribeReferenceIdentity(object? value)
    {
        return value == null
            ? 0
            : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }

    private static string DescribeCollectionCount(object? value)
    {
        if (value is System.Collections.ICollection collection)
        {
            return $"{DescribeType(value)}(count={collection.Count})";
        }

        return DescribeType(value);
    }

    private static string DescribeTransformPath(Transform? transform)
    {
        if (transform == null)
        {
            return "<none>";
        }

        var segments = new System.Collections.Generic.List<string>();
        Transform? current = transform;
        int depth = 0;
        while (current != null && depth++ < 32)
        {
            segments.Add(current.gameObject.name);
            current = current.parent;
        }

        segments.Reverse();
        return string.Join("/", segments);
    }

    private static string DescribeProfileId(object? profile)
    {
        return profile == null
            ? "<none>"
            : Convert.ToString(ResolveMember(profile, "Id")) ?? "<none>";
    }

    private static object? ResolveMember(object source, string name)
    {
        for (Type? type = source.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                return field.GetValue(source);
            }

            PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(source);
            }
        }

        return null;
    }

    private static string DescribeType(object? value)
    {
        return value?.GetType().FullName ?? value?.GetType().Name ?? "<none>";
    }
}

/// <summary>
/// Corrects the concrete right-side Trader TradingGridView binding during an active Operator
/// inventory session. EFT otherwise binds this panel to the main player's stale StashGrid and
/// inventory controller even though the live Operator graph is current. Vanguard substitutes
/// only the right-side StashGrid + inventory controller; trader assortment, ItemContext,
/// ItemUiContext, wallet/payment logic, backend routing, and market authority remain native.
/// </summary>
internal sealed class VanguardOperatorTraderGridBindingPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type gridViewType = AccessTools.TypeByName("EFT.UI.DragAndDrop.TradingGridView")
            ?? AccessTools.TypeByName("TradingGridView")
            ?? throw new MissingMemberException("TradingGridView type not found.");

        MethodInfo? target = gridViewType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => string.Equals(method.Name, "Show", StringComparison.Ordinal))
            .Where(method => method.GetParameters().Length == 5)
            .FirstOrDefault(method =>
            {
                ParameterInfo[] parameters = method.GetParameters();
                return parameters[0].ParameterType.Name.IndexOf("StashGrid", StringComparison.OrdinalIgnoreCase) >= 0
                    && (parameters[3].ParameterType.Name.IndexOf("GClass", StringComparison.OrdinalIgnoreCase) >= 0
                        || parameters[3].ParameterType.Name.IndexOf("InventoryController", StringComparison.OrdinalIgnoreCase) >= 0);
            });

        if (target == null)
        {
            string candidates = string.Join(
                " | ",
                gridViewType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method => string.Equals(method.Name, "Show", StringComparison.Ordinal))
                    .Select(method => $"Show({string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.Name))})"));
            throw new MissingMethodException(gridViewType.FullName ?? gridViewType.Name, $"Show(5 args); candidates={candidates}");
        }

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_TRADER_GRID_BINDING_STATUS",
            $"trading_grid_view_binding_patch_target_resolved target={target.DeclaringType?.FullName ?? gridViewType.FullName}.{target.Name}; signature={string.Join(",", target.GetParameters().Select(parameter => parameter.ParameterType.Name))}; scope=right_person_only; operatorLiveGridAndController=true; nativeTraderEconomyAuthorityPreserved=true");
        return target;
    }

    [PatchPrefix]
    private static void Prefix(
        object __instance,
        object[] __args,
        [HarmonyArgument(0)] ref StashGridClass requestedGrid,
        [HarmonyArgument(3)] ref InventoryController requestedController,
        out VanguardOperatorTradingPresentationRebind.TradingGridBindingSwapState? __state)
    {
        __state = VanguardOperatorTradingPresentationRebind.BeginTradingGridViewOperatorBinding(
            __instance,
            __args,
            ref requestedGrid,
            ref requestedController);
    }

    [PatchPostfix]
    private static void Postfix(
        object __instance,
        VanguardOperatorTradingPresentationRebind.TradingGridBindingSwapState? __state)
    {
        VanguardOperatorTradingPresentationRebind.CompleteTradingGridViewOperatorBinding(
            __instance,
            __state);
    }

    [PatchFinalizer]
    private static Exception? Finalizer(
        Exception? __exception,
        object __instance,
        VanguardOperatorTradingPresentationRebind.TradingGridBindingSwapState? __state)
    {
        if (__exception != null)
        {
            VanguardOperatorTradingPresentationRebind.CompleteTradingGridViewOperatorBinding(
                __instance,
                __state,
                __exception);
        }

        return __exception;
    }
}
#else
internal sealed class VanguardOperatorTraderGridBindingPatch
{
    public void Enable()
    {
    }
}

#endif
