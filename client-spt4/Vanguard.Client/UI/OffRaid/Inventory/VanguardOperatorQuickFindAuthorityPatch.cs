using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: keeps EFT's native Ctrl+Click / QuickFind placement search on the active Operator view authority and shared player stash while the direct Operator InventoryScreen is foreground.
// Flow: the view-authority router resolves the currently consulted Operator, this prefix corrects only QuickFind inputs, then EFT still chooses the placement and executes the native transaction.
// Authority boundary: Vanguard does not choose a destination cell or execute the transaction; native QuickFindAppropriatePlace, CanExecute and network transaction remain authoritative.
// Invariant: inactive for Player views and preserved nested navigation; stash-origin items are never redirected back into the same stash.
namespace Vanguard.Client.UI.OffRaid.Inventory;

#if SPT_CLIENT
internal sealed class VanguardOperatorQuickFindAuthorityPatch : ModulePatch
{
    private const string StatusTag = "VANGUARD_OPERATOR_QUICK_FIND_AUTHORITY_STATUS";

    protected override MethodBase GetTargetMethod()
    {
        const BindingFlags flags = BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.FlattenHierarchy;

        MethodInfo[] allMethods = typeof(InteractionsHandlerClass).GetMethods(flags);
        MethodInfo? named = allMethods.FirstOrDefault(method =>
            string.Equals(method.Name, "QuickFindAppropriatePlace", StringComparison.Ordinal)
            && IsQuickFindSignature(method));

        MethodInfo[] signatureMatches = allMethods
            .Where(IsQuickFindSignature)
            .ToArray();
        MethodInfo? target = named ?? (signatureMatches.Length == 1 ? signatureMatches[0] : null);

        if (target == null)
        {
            string candidates = string.Join(
                " | ",
                allMethods
                    .Where(method => method.GetParameters().Length == 5)
                    .Select(method => $"{method.DeclaringType?.FullName ?? "<unknown>"}.{method.Name}({string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name))})->{method.ReturnType.FullName ?? method.ReturnType.Name}")
                    .Take(24));
            throw new MissingMethodException(
                typeof(InteractionsHandlerClass).FullName,
                $"QuickFindAppropriatePlace signature; matches={signatureMatches.Length}; fiveArgCandidates={candidates}");
        }

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"quick_find_authority_patch_target_resolved target={target.DeclaringType?.FullName ?? target.DeclaringType?.Name ?? "<unknown>"}.{target.Name}; signature={string.Join(",", target.GetParameters().Select(parameter => parameter.ParameterType.Name))}; resolvedBy={(named != null ? "name_plus_signature" : "signature_only_runtime_obfuscation_safe")}; scope=direct_operator_inventory_only");
        return target;
    }

    private static bool IsQuickFindSignature(MethodInfo method)
    {
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length != 5
            || parameters[3].ParameterType != typeof(InteractionsHandlerClass.EMoveItemOrder)
            || parameters[4].ParameterType != typeof(bool))
        {
            return false;
        }

        Type itemType = parameters[0].ParameterType;
        Type controllerType = parameters[1].ParameterType;
        Type targetsType = parameters[2].ParameterType;
        bool itemCompatible = typeof(Item).IsAssignableFrom(itemType) || itemType.IsAssignableFrom(typeof(Item));
        bool controllerCompatible = typeof(InventoryController).IsAssignableFrom(controllerType)
            || controllerType.IsAssignableFrom(typeof(InventoryController));
        bool targetsCompatible = typeof(IEnumerable<CompoundItem>).IsAssignableFrom(targetsType)
            || targetsType.IsAssignableFrom(typeof(IEnumerable<CompoundItem>));

        // QuickFind returns EFT's operation-result struct.  Keep the return check deliberately
        // shape-based so the resolver survives SPT's obfuscated generic type names while still
        // excluding unrelated five-argument helpers.
        Type returnType = method.ReturnType;
        bool resultCompatible = returnType.IsValueType
            && returnType.IsGenericType
            && returnType.GetGenericArguments().Length == 1;

        return itemCompatible && controllerCompatible && targetsCompatible && resultCompatible;
    }

    [PatchPrefix]
    private static void Prefix(
        [HarmonyArgument(0)] Item item,
        [HarmonyArgument(1)] ref InventoryController requestedController,
        [HarmonyArgument(2)] ref IEnumerable<CompoundItem> targets,
        [HarmonyArgument(3)] InteractionsHandlerClass.EMoveItemOrder order)
    {
        if (!VanguardOperatorInventoryModeClientState.IsActive
            || VanguardOperatorEquipmentBuildsFlow.HasActiveNestedNavigationLease
            || !order.HasFlag(InteractionsHandlerClass.EMoveItemOrder.MoveToAnotherSide))
        {
            return;
        }

        if (!VanguardOffRaidViewAuthorityRouter.TryResolve(
                "InventoryQuickFind",
                "quick_find_move_to_other_side",
                out VanguardOffRaidViewContext viewContext,
                out string routeReason)
            || !viewContext.IsOperator)
        {
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"quick_find_authority_skipped reason={routeReason}; operatorSessionActive={VanguardOperatorInventoryModeClientState.IsActive}; nativeQuickFindContinues=true");
            return;
        }

        try
        {
            InventoryController operatorController = viewContext.InventoryController;
            bool requestedControllerWasOperator = ReferenceEquals(requestedController, operatorController);
            if (!requestedControllerWasOperator)
            {
                requestedController = operatorController;
            }

            bool itemUiContextReasserted = VanguardOffRaidViewAuthorityRouter.TryReassertItemUiContext(
                viewContext,
                "quick_find_move_to_other_side",
                out string itemUiContextReason);

            StashItemClass? activeStash = operatorController.Inventory?.Stash;
            bool sourceInsideActiveStash = activeStash != null && IsInside(item, activeStash);
            List<CompoundItem> currentTargets = targets?
                .Where(target => target != null)
                .ToList()
                ?? new List<CompoundItem>();
            bool activeStashIncluded = activeStash != null
                && currentTargets.Any(target => ReferenceEquals(target, activeStash));
            bool activeStashCanFit = activeStash != null
                && !sourceInsideActiveStash
                && activeStash.Grid.FindFreeSpace(item) != null;
            bool stashTargetInjected = false;

            if (activeStash != null && !sourceInsideActiveStash && !activeStashIncluded)
            {
                currentTargets.RemoveAll(target => ReferenceEquals(target, activeStash));
                currentTargets.Insert(0, activeStash);
                targets = currentTargets;
                activeStashIncluded = true;
                stashTargetInjected = true;
            }

            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"quick_find_authority_prepared operator={viewContext.OperatorId ?? "<none>"}; generation={viewContext.Generation}; order={order}; requestedControllerWasOperator={requestedControllerWasOperator}; controllerSubstituted={!requestedControllerWasOperator}; effectiveControllerIsOperator={ReferenceEquals(requestedController, operatorController)}; itemUiContextReasserted={itemUiContextReasserted}; itemUiContextReason={itemUiContextReason}; activeStash={activeStash?.Id ?? "<none>"}; sourceInsideActiveStash={sourceInsideActiveStash}; activeStashIncluded={activeStashIncluded}; activeStashCanFit={activeStashCanFit}; stashTargetInjected={stashTargetInjected}; nativePlacementAuthorityPreserved=true");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"quick_find_authority_prepare_failed operator={viewContext.OperatorId ?? "<none>"}; generation={viewContext.Generation}; reason={exception.GetType().Name}:{exception.Message}; nativeQuickFindContinues=true");
        }
    }

    private static bool IsInside(Item item, CompoundItem ancestor)
    {
        Item? current = item;
        int depth = 0;
        while (current != null && depth++ < 64)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            if (current.CurrentAddress == null)
            {
                return false;
            }

            Item? parent = current.CurrentAddress.Container?.ParentItem;
            if (parent == null || ReferenceEquals(parent, current))
            {
                return false;
            }

            current = parent;
        }

        return false;
    }
}
#else
internal sealed class VanguardOperatorQuickFindAuthorityPatch
{
    public void Enable()
    {
    }
}
#endif
