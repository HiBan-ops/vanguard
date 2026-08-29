using System;
using System.Linq;
using System.Reflection;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
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
            $"edit_build_controller_patch_target_resolved target={controllerType.FullName}.ctor; signature={string.Join(",", target.GetParameters().Select(parameter => parameter.ParameterType.Name))}");
        return target;
    }

    [PatchPrefix]
    private static void Prefix(object[] __args)
    {
        if (!VanguardOperatorInventoryModeClientState.IsActive || __args.Length < 2)
        {
            return;
        }

        object? requestedInventoryController = __args[1];
        if (!VanguardOperatorEquipmentBuildsFlow.TryPrepareNativeBuildInventoryController(
                requestedInventoryController,
                "edit_build_controller_ctor",
                out object? operatorInventoryController)
            || operatorInventoryController == null)
        {
            return;
        }

        __args[1] = operatorInventoryController;
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

        // EFT 0.16.9 / SPT 4.1.x constructs this native controller from
        // (session, backend inventory controller, health controller, equipment).
        // Constructor interception happens before ShowScreen queues navigation, so
        // Vanguard can preserve its direct inventory session before InventoryScreen
        // emits OnClose for the nested transition.
        ConstructorInfo? target = controllerType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(ctor => ctor.GetParameters().Length == 4);
        if (target == null)
        {
            throw new MissingMethodException(controllerType.FullName ?? controllerType.Name, ".ctor(4 args)");
        }

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
            $"equipment_builds_controller_patch_target_resolved target={controllerType.FullName}.ctor; signature={string.Join(",", target.GetParameters().Select(parameter => parameter.ParameterType.Name))}");
        return target;
    }

    [PatchPostfix]
    private static void Postfix(object __instance, object[] __args)
    {
        if (!VanguardOperatorInventoryModeClientState.IsActive)
        {
            return;
        }

        object? buildsBackendInventoryController = __args.Length > 1 ? __args[1] : null;
        if (!VanguardOperatorEquipmentBuildsFlow.TryBeginNativeBuildsTransition(__instance, buildsBackendInventoryController, "equipment_builds_controller_ctor"))
        {
            return;
        }

        // Miyako's compatible bot-inventory implementation documents that EFT can
        // immediately return from the build list when LastEquipmentBuildType is not
        // a usable equipment tab. Preserve an already-valid Standard/Custom choice;
        // only repair other/unknown values to Standard. Both tabs remain native.
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
            FieldInfo? field = AccessTools.Field(controller.GetType(), "LastEquipmentBuildType");
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

    [PatchPostfix]
    private static void Postfix(object[] __args)
    {
        object? controller = __args.Length > 0 ? __args[0] : null;
        VanguardOperatorEquipmentBuildsFlow.NotifyDirectInventoryShown(controller, "inventory_screen_show");
    }
}
#else
internal sealed class VanguardOperatorEditBuildControllerPatch
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
