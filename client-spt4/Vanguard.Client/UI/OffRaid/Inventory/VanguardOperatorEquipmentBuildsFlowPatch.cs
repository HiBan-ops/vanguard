using System;
using System.Linq;
using System.Reflection;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: Bridges EFT/SPT/Fika callbacks into Operator Equipment Builds Flow Patch for the Off-Raid Operator inventory bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.UI.OffRaid.Inventory;

#if SPT_CLIENT
/// <summary>
/// Rebinds EFT's native Configurations/EditBuild controller to the already-open
/// Vanguard Operator inventory controller while keeping the player's native session.
/// MainMenuController normally constructs this screen with its player InventoryController;
/// without this narrow substitution the native build UI would edit the PMC instead.
/// </summary>
internal sealed class VanguardOperatorEditBuildControllerPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type screenType = AccessTools.TypeByName("EFT.UI.EditBuildScreen")
            ?? AccessTools.TypeByName("EditBuildScreen")
            ?? throw new MissingMemberException("EditBuildScreen type not found.");

        Type controllerType = screenType.GetNestedType("GClass3881", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(screenType.FullName ?? screenType.Name, "GClass3881");

        ConstructorInfo? target = controllerType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(ctor => ctor.GetParameters().Length == 3);
        if (target == null)
        {
            throw new MissingMethodException(controllerType.FullName ?? controllerType.Name, ".ctor(3 args)");
        }

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
            $"edit_build_controller_patch_target_resolved target={controllerType.FullName}.ctor; signature={string.Join(",", target.GetParameters().Select(parameter => parameter.ParameterType.Name))}; authorityRouting=player_or_operator");
        return target;
    }

    [PatchPrefix]
    private static void Prefix([HarmonyArgument(1)] ref InventoryController requestedInventoryController)
    {
        if (!VanguardOffRaidViewAuthorityRouter.TryResolve(
                "EditBuild",
                "edit_build_controller_ctor",
                out VanguardOffRaidViewContext viewContext,
                out string routeReason))
        {
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
                $"edit_build_view_authority_unresolved reason={routeReason}; native_controller_preserved=true");
            return;
        }

        InventoryController originalController = requestedInventoryController;
        if (viewContext.IsOperator)
        {
            if (VanguardOperatorEquipmentBuildsFlow.TryPrepareNativeBuildInventoryController(
                    requestedInventoryController,
                    "edit_build_controller_ctor",
                    out object? operatorInventoryController)
                && operatorInventoryController is InventoryController preparedController)
            {
                requestedInventoryController = preparedController;
            }
            else
            {
                requestedInventoryController = viewContext.InventoryController;
            }

            VanguardOperatorProfileTransitionTrace.RecordNestedBoundary(
                "EditBuild",
                "edit_build_controller_ctor",
                "operator_presentation_player_session",
                "view_authority_router=operator; native_edit_build_controller_requested=true");
        }
        else
        {
            requestedInventoryController = viewContext.InventoryController;
        }

        bool itemUiContextReasserted = VanguardOffRaidViewAuthorityRouter.TryReassertItemUiContext(
            viewContext,
            "edit_build_controller_ctor",
            out string itemUiContextReason);

        VanguardOffRaidViewAuthorityRouter.RecordEffectiveBinding(
            "EditBuild",
            "edit_build_controller_ctor",
            viewContext,
            requestedInventoryController,
            null,
            null,
            itemUiContextReasserted,
            itemUiContextReason,
            $"controllerSubstituted={!ReferenceEquals(originalController, requestedInventoryController)}; sessionPreserved=true");
    }
}

/// <summary>
/// Reasserts the active Operator ItemUiContext immediately before EFT presents an
/// EditBuild screen. This covers returns from the native buy-missing-items commerce
/// subflow, where the global ItemUiContext may have been reconfigured by trading UI.
/// </summary>
internal sealed class VanguardOperatorEditBuildScreenPresentationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type screenType = AccessTools.TypeByName("EFT.UI.EditBuildScreen")
            ?? AccessTools.TypeByName("EditBuildScreen")
            ?? throw new MissingMemberException("EditBuildScreen type not found.");

        Type controllerType = screenType.GetNestedType("GClass3881", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(screenType.FullName ?? screenType.Name, "GClass3881");

        MethodInfo? target = AccessTools.Method(screenType, "Show", new[] { controllerType });
        target ??= screenType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
                string.Equals(method.Name, "Show", StringComparison.Ordinal)
                && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType == controllerType);

        if (target == null)
        {
            throw new MissingMethodException(screenType.FullName ?? screenType.Name, "Show(GClass3881)");
        }

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
            $"edit_build_screen_patch_target_resolved target={target.DeclaringType?.FullName ?? screenType.FullName}.{target.Name}; controller={controllerType.FullName ?? controllerType.Name}");
        return target;
    }

    [PatchPrefix]
    private static void Prefix()
    {
        if (!VanguardOffRaidViewAuthorityRouter.TryResolve(
                "EditBuild",
                "edit_build_screen_show_prefix",
                out VanguardOffRaidViewContext viewContext,
                out string routeReason))
        {
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
                $"edit_build_item_ui_context_reassert_skipped source=edit_build_screen_show_prefix; reason={routeReason}; native_show_preserved=true");
            return;
        }

        bool reasserted = VanguardOffRaidViewAuthorityRouter.TryReassertItemUiContext(
            viewContext,
            "edit_build_screen_show_prefix",
            out string reason);

        VanguardOffRaidViewAuthorityRouter.RecordEffectiveBinding(
            "EditBuild",
            "edit_build_screen_show_prefix",
            viewContext,
            viewContext.InventoryController,
            viewContext.HealthController,
            viewContext.Equipment,
            reasserted,
            reason,
            "presentation_boundary=true");
    }
}

/// <summary>
/// Observes EFT's native EquipmentBuildsScreen and marks it as a nested navigation
/// flow only while Vanguard's direct Operator inventory is active.
/// </summary>
internal sealed class VanguardOperatorEquipmentBuildsControllerPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type screenType = AccessTools.TypeByName("EFT.UI.EquipmentBuildsScreen")
            ?? AccessTools.TypeByName("EFT.UI.Builds.EquipmentBuildsScreen")
            ?? AccessTools.TypeByName("EquipmentBuildsScreen")
            ?? throw new MissingMemberException("EquipmentBuildsScreen type not found.");

        Type controllerType = screenType.GetNestedType("GClass3870", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(screenType.FullName ?? screenType.Name, "GClass3870");

        ConstructorInfo? target = controllerType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(ctor => ctor.GetParameters().Length == 4);
        if (target == null)
        {
            throw new MissingMethodException(controllerType.FullName ?? controllerType.Name, ".ctor(4 args)");
        }

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
            $"equipment_builds_controller_patch_target_resolved target={controllerType.FullName}.ctor; signature={string.Join(",", target.GetParameters().Select(parameter => parameter.ParameterType.Name))}; authorityRouting=player_or_operator; mutationMechanism=harmony_direct_ref_arguments");
        return target;
    }

    [PatchPrefix]
    private static void Prefix(
        [HarmonyArgument(0)] ISession requestedSession,
        [HarmonyArgument(1)] ref GClass3387 requestedInventoryController,
        [HarmonyArgument(2)] ref IHealthController requestedHealthController,
        [HarmonyArgument(3)] ref InventoryEquipment requestedEquipment)
    {
        if (!VanguardOffRaidViewAuthorityRouter.TryResolve(
                "EquipmentBuilds",
                "equipment_builds_controller_ctor_prefix",
                out VanguardOffRaidViewContext viewContext,
                out string routeReason))
        {
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
                $"equipment_builds_view_authority_unresolved reason={routeReason}; native_constructor_preserved=true");
            return;
        }

        GClass3387 originalController = requestedInventoryController;
        IHealthController originalHealth = requestedHealthController;
        InventoryEquipment originalEquipment = requestedEquipment;

        requestedInventoryController = viewContext.InventoryController;
        requestedHealthController = viewContext.HealthController;
        requestedEquipment = viewContext.Equipment;

        bool itemUiContextReasserted = VanguardOffRaidViewAuthorityRouter.TryReassertItemUiContext(
            viewContext,
            "equipment_builds_controller_ctor",
            out string itemUiContextReason);

        if (viewContext.IsOperator)
        {
            VanguardOperatorProfileTransitionTrace.RecordNestedBoundary(
                "EquipmentBuilds",
                "equipment_builds_controller_ctor_prefix",
                "operator_presentation_player_session",
                $"operator={viewContext.OperatorId ?? "<none>"}; profile={viewContext.ProfileId ?? "<none>"}; generation={viewContext.Generation}; directRefRouting=true; sessionArgumentPreserved=True");

            VanguardOperatorProfileTransitionTrace.Record(
                phase: "equipment_builds_ctor_effective_context",
                route: "EquipmentBuilds",
                source: "equipment_builds_controller_ctor_prefix",
                expectedAuthority: "operator_presentation_player_session",
                suppliedInventoryController: requestedInventoryController,
                suppliedHealthController: requestedHealthController,
                suppliedEquipment: requestedEquipment,
                detail: $"requestedControllerWasOperator={ReferenceEquals(originalController, viewContext.InventoryController)}; requestedHealthWasOperator={ReferenceEquals(originalHealth, viewContext.HealthController)}; requestedEquipmentWasOperator={ReferenceEquals(originalEquipment, viewContext.Equipment)}; directRefRouting=true");
        }

        VanguardOffRaidViewAuthorityRouter.RecordEffectiveBinding(
            "EquipmentBuilds",
            "equipment_builds_controller_ctor_prefix",
            viewContext,
            requestedInventoryController,
            requestedHealthController,
            requestedEquipment,
            itemUiContextReasserted,
            itemUiContextReason,
            $"sessionArgumentMatchesResolvedSession={ReferenceEquals(requestedSession, viewContext.Session)}; controllerSubstituted={!ReferenceEquals(originalController, requestedInventoryController)}; healthSubstituted={!ReferenceEquals(originalHealth, requestedHealthController)}; equipmentSubstituted={!ReferenceEquals(originalEquipment, requestedEquipment)}; nativePlayerSessionPreserved=true");

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
            $"equipment_builds_view_context_rebound authority={viewContext.AuthorityName}; operator={viewContext.OperatorId ?? "<none>"}; profile={viewContext.ProfileId ?? "<none>"}; generation={viewContext.Generation}; controllerSubstituted={!ReferenceEquals(originalController, requestedInventoryController)}; healthSubstituted={!ReferenceEquals(originalHealth, requestedHealthController)}; equipmentSubstituted={!ReferenceEquals(originalEquipment, requestedEquipment)}; controllerMatchesExpected={ReferenceEquals(requestedInventoryController, viewContext.InventoryController)}; healthMatchesExpected={ReferenceEquals(requestedHealthController, viewContext.HealthController)}; equipmentMatchesExpected={ReferenceEquals(requestedEquipment, viewContext.Equipment)}; itemUiContextReasserted={itemUiContextReasserted}; playerSessionPreserved=True");
    }

    [PatchPostfix]
    private static void Postfix(
        object __instance,
        [HarmonyArgument(0)] ISession effectiveSession,
        [HarmonyArgument(1)] GClass3387 effectiveInventoryController,
        [HarmonyArgument(2)] IHealthController effectiveHealthController,
        [HarmonyArgument(3)] InventoryEquipment effectiveEquipment)
    {
        if (!VanguardOffRaidViewAuthorityRouter.TryResolve(
                "EquipmentBuilds",
                "equipment_builds_controller_ctor_postfix",
                out VanguardOffRaidViewContext viewContext,
                out _))
        {
            return;
        }

        VanguardOffRaidViewAuthorityRouter.RecordEffectiveBinding(
            "EquipmentBuilds",
            "equipment_builds_controller_ctor_postfix",
            viewContext,
            effectiveInventoryController,
            effectiveHealthController,
            effectiveEquipment,
            itemUiContextReasserted: true,
            itemUiContextReason: "prefix_already_reasserted",
            detail: "native_controller_constructed=true");

        if (!viewContext.IsOperator)
        {
            return;
        }

        if (!VanguardOperatorEquipmentBuildsFlow.TryBeginNativeBuildsTransition(
                __instance,
                effectiveInventoryController,
                "equipment_builds_controller_ctor"))
        {
            return;
        }

        VanguardOperatorProfileTransitionTrace.Record(
            phase: "equipment_builds_ctor_completed",
            route: "EquipmentBuilds",
            source: "equipment_builds_controller_ctor_postfix",
            expectedAuthority: "operator_presentation_player_session",
            suppliedInventoryController: effectiveInventoryController,
            suppliedHealthController: effectiveHealthController,
            suppliedEquipment: effectiveEquipment,
            detail: "native_controller_constructed=true; directRefRouting=true");

        bool initialBuildTypeValid = TryEnsureUsableInitialBuildType(__instance, out string buildTypeReason);
        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
            $"equipment_builds_native_controller operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; usableInitialBuildType={initialBuildTypeValid}; reason={buildTypeReason}; standardAndCustomRemainNative=true; missingItemsAndBuyFlowRemainNative=true");
    }

    private static bool TryEnsureUsableInitialBuildType(object? controller, out string reason)
    {
        reason = "unknown";
        if (controller == null)
        {
            reason = "controller_missing";
            return false;
        }

        try
        {
            PropertyInfo? property = AccessTools.Property(controller.GetType(), "LastEquipmentBuildType");
            // The supported SPT 4.0.13 controller exposes this as a property. Query the field only
            // as a compatibility fallback so Harmony does not emit a false missing-field warning.
            FieldInfo? field = property == null
                ? AccessTools.Field(controller.GetType(), "LastEquipmentBuildType")
                : null;
            Type? enumType = property?.PropertyType ?? field?.FieldType;
            if (enumType == null || !enumType.IsEnum)
            {
                reason = "last_equipment_build_type_member_not_found";
                return false;
            }

            object? current = property?.CanRead == true ? property.GetValue(controller) : field?.GetValue(controller);
            string currentName = current?.ToString() ?? string.Empty;
            if (string.Equals(currentName, "Standard", StringComparison.Ordinal)
                || string.Equals(currentName, "Custom", StringComparison.Ordinal))
            {
                reason = "existing_native_value:" + currentName;
                return true;
            }

            object standard = Enum.Parse(enumType, "Standard", ignoreCase: false);
            if (property?.CanWrite == true)
            {
                property.SetValue(controller, standard);
                reason = "repaired_to_standard_via_property; previous=" + (string.IsNullOrWhiteSpace(currentName) ? "<none>" : currentName);
                return true;
            }

            if (field != null)
            {
                field.SetValue(controller, standard);
                reason = "repaired_to_standard_via_field; previous=" + (string.IsNullOrWhiteSpace(currentName) ? "<none>" : currentName);
                return true;
            }

            reason = "member_not_writable; previous=" + (string.IsNullOrWhiteSpace(currentName) ? "<none>" : currentName);
            return false;
        }
        catch (Exception exception)
        {
            reason = exception.GetType().Name + ":" + exception.Message;
            return false;
        }
    }
}

/// <summary>
/// Detects the exact direct Operator InventoryScreen controller returning to the
/// foreground.  Only that return closes the nested Equipment Builds navigation
/// lease; market/trader screens opened by EFT's native "buy missing items" flow do
/// not end the lease prematurely.
/// </summary>
internal sealed class VanguardOperatorInventoryScreenReturnPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type screenType = AccessTools.TypeByName("EFT.UI.InventoryScreen")
            ?? AccessTools.TypeByName("InventoryScreen")
            ?? throw new MissingMemberException("InventoryScreen type not found.");

        Type? controllerType = screenType.GetNestedType("GClass3871", BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo? target = controllerType == null
            ? null
            : AccessTools.Method(screenType, "Show", new[] { controllerType });

        target ??= screenType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
                string.Equals(method.Name, "Show", StringComparison.Ordinal)
                && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType.Name.StartsWith("GClass387", StringComparison.Ordinal));

        if (target == null)
        {
            throw new MissingMethodException(screenType.FullName ?? screenType.Name, "Show(GClass3871)");
        }

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
            $"inventory_screen_return_patch_target_resolved target={target.DeclaringType?.FullName ?? screenType.FullName}.{target.Name}; controller={target.GetParameters()[0].ParameterType.FullName ?? target.GetParameters()[0].ParameterType.Name}");
        return target;
    }

    [PatchPrefix]
    private static void Prefix(object[] __args)
    {
        object? controller = __args.Length > 0 ? __args[0] : null;
        if (!VanguardOperatorDirectEquipmentScreenEntry.IsActiveOperatorInventoryScreenController(controller))
        {
            return;
        }

        if (!VanguardOperatorDirectEquipmentScreenEntry.TryReassertActiveOperatorItemUiContext("inventory_screen_show_prefix", out string reassertReason))
        {
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                $"operator_item_ui_context_reassert_before_show_failed source=inventory_screen_show_prefix; reason={reassertReason}; session_preserved={VanguardOperatorInventoryModeClientState.IsActive}");
        }
    }

    [PatchPostfix]
    private static void Postfix(object[] __args)
    {
        object? controller = __args.Length > 0 ? __args[0] : null;
        bool equipmentBuildsReturned = VanguardOperatorEquipmentBuildsFlow.NotifyDirectInventoryShown(controller, "inventory_screen_show");
        if (VanguardOperatorDirectEquipmentScreenEntry.IsActiveOperatorInventoryScreenController(controller))
        {
            if (!VanguardOperatorDirectEquipmentScreenEntry.TryReassertActiveOperatorItemUiContext("inventory_screen_show_postfix", out string reassertReason))
            {
                VanguardClientDiagnosticsLog.Warning(
                    "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                    $"operator_item_ui_context_reassert_after_show_failed source=inventory_screen_show_postfix; reason={reassertReason}; session_preserved={VanguardOperatorInventoryModeClientState.IsActive}");
            }

            string returnSource = equipmentBuildsReturned ? "inventory_screen_show_after_equipment_builds" : "inventory_screen_show";
            VanguardOperatorInventorySessionNavigation.NotifyOperatorInventoryShown(returnSource);
        }
    }
}
#else
internal sealed class VanguardOperatorEditBuildControllerPatch
{
    public void Enable()
    {
    }
}

internal sealed class VanguardOperatorEditBuildScreenPresentationPatch
{
    public void Enable()
    {
    }
}

internal sealed class VanguardOperatorEquipmentBuildsControllerPatch
{
    public void Enable()
    {
    }
}

internal sealed class VanguardOperatorInventoryScreenReturnPatch
{
    public void Enable()
    {
    }
}
#endif
