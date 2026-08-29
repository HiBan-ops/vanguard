using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using HarmonyLib;
#endif

// Responsibility: Presents and coordinates Operator Direct Inventory Exit Guard in the Off-Raid Operator inventory bridge.
// Flow: Canonical API/runtime state is projected into view models and Unity/TMP controls; explicit user actions are delegated back through API/service boundaries.
// Authority boundary: Presentation layer only; it does not become persistence, economy, medical, or raid-runtime authority.
// Invariant: UI refreshes are idempotent from canonical state and temporary view state must not outlive its owning screen/session.
namespace Vanguard.Client.UI.OffRaid.Inventory;

internal static class VanguardOperatorDirectInventoryExitGuard
{
#if SPT_CLIENT
    public static void CaptureBeforeOpen(string source)
    {
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorInventoryExitReloadStatusTag,
            $"direct_inventory_exit_guard_capture source={source}; mode=screen_return_and_menu_reload");
    }

    public static async Task RestoreAfterCloseAsync(string source)
    {
        try
        {
            await TryReturnInventoryScreenToKeyScreenAsync(source);
            TryCloseTransientItemUiWindows(source);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                $"direct_inventory_exit_guard_restore_failed source={source}; reason={exception.GetType().Name}: {exception.Message}");
        }
    }

    public static void RestoreAfterFailedOpen(string source)
    {
        try
        {
            TryCloseTransientItemUiWindows(source);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                $"direct_inventory_exit_guard_recovery_failed source={source}; reason={exception.GetType().Name}: {exception.Message}");
        }
    }

    private static async Task TryReturnInventoryScreenToKeyScreenAsync(string source)
    {
        try
        {
            object? currentScreen = ResolveCurrentScreenSingleton();
            object? inventoryScreenType = ResolveEnumValue("EEftScreenType", "Inventory");
            if (currentScreen == null || inventoryScreenType == null)
            {
                VanguardClientDiagnosticsLog.Info(
                    "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                    $"direct_inventory_exit_guard_screen_return_skipped source={source}; reason=screen_singleton_or_inventory_enum_missing");
                return;
            }

            MethodInfo? checkCurrent = currentScreen.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => string.Equals(method.Name, "CheckCurrentScreen", StringComparison.Ordinal)
                    && method.GetParameters().Length == 1);
            if (checkCurrent == null)
            {
                VanguardClientDiagnosticsLog.Info(
                    "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                    $"direct_inventory_exit_guard_screen_return_skipped source={source}; reason=check_current_screen_missing");
                return;
            }

            bool isInventoryCurrent = checkCurrent.Invoke(currentScreen, new[] { inventoryScreenType }) is bool value && value;
            if (!isInventoryCurrent)
            {
                VanguardClientDiagnosticsLog.Info(
                    "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                    $"direct_inventory_exit_guard_screen_return_noop source={source}; currentInventory=False");
                return;
            }

            MethodInfo? returnToKey = currentScreen.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => string.Equals(method.Name, "TryReturnToKeyScreen", StringComparison.Ordinal)
                    && method.GetParameters().Length == 0);
            if (returnToKey == null)
            {
                VanguardClientDiagnosticsLog.Warning(
                    "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                    $"direct_inventory_exit_guard_screen_return_skipped source={source}; reason=try_return_to_key_screen_missing");
                return;
            }

            object? result = returnToKey.Invoke(currentScreen, Array.Empty<object>());
            if (result is Task task)
            {
                await task;
            }

            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                $"direct_inventory_exit_guard_screen_return_completed source={source}");
        }
        catch (Exception exception)
        {
            Exception root = Unwrap(exception);
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                $"direct_inventory_exit_guard_screen_return_failed source={source}; reason={root.GetType().Name}: {root.Message}");
        }
    }

    private static void TryCloseTransientItemUiWindows(string source)
    {
        object? itemUiContext = ResolveItemUiContextInstance();
        if (itemUiContext == null)
        {
            return;
        }

        int closed = 0;
        closed += TryCloseMember(itemUiContext, "Tooltip") ? 1 : 0;
        closed += TryCloseMember(itemUiContext, "MultiLineTooltip") ? 1 : 0;
        closed += TryCloseMember(itemUiContext, "TaskConditionsTooltip") ? 1 : 0;

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
            $"direct_inventory_exit_guard_transient_windows_closed source={source}; count={closed}");
    }

    private static bool TryCloseMember(object owner, string memberName)
    {
        try
        {
            object? target = ResolveMember(owner, memberName);
            if (target == null)
            {
                return false;
            }

            MethodInfo? close = target.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => string.Equals(method.Name, "Close", StringComparison.Ordinal)
                    && method.GetParameters().Length == 0);
            close?.Invoke(target, Array.Empty<object>());
            return close != null;
        }
        catch
        {
            return false;
        }
    }

    private static object? ResolveCurrentScreenSingleton()
    {
        Type? type = ResolveTypeByName("CurrentScreenSingletonClass");
        if (type == null)
        {
            return null;
        }

        return AccessTools.Property(type, "Instance")?.GetValue(null)
            ?? AccessTools.Field(type, "Instance")?.GetValue(null)
            ?? AccessTools.Property(type, "instance")?.GetValue(null)
            ?? AccessTools.Field(type, "instance")?.GetValue(null);
    }

    private static object? ResolveItemUiContextInstance()
    {
        Type? itemUiContextType = ResolveTypeByName("EFT.UI.ItemUiContext") ?? ResolveTypeByName("ItemUiContext");
        if (itemUiContextType == null)
        {
            return null;
        }

        return AccessTools.Property(itemUiContextType, "Instance")?.GetValue(null)
            ?? AccessTools.Field(itemUiContextType, "Instance")?.GetValue(null)
            ?? AccessTools.Property(itemUiContextType, "instance")?.GetValue(null)
            ?? AccessTools.Field(itemUiContextType, "instance")?.GetValue(null);
    }

    private static object? ResolveEnumValue(string enumTypeName, string valueName)
    {
        Type? enumType = ResolveTypeByName(enumTypeName);
        if (enumType == null || !enumType.IsEnum)
        {
            return null;
        }

        try
        {
            return Enum.Parse(enumType, valueName);
        }
        catch
        {
            return null;
        }
    }



    private static object? ResolveMember(object target, string name)
    {
        PropertyInfo? property = AccessTools.Property(target.GetType(), name);
        if (property != null && property.GetIndexParameters().Length == 0)
        {
            return property.GetValue(target);
        }

        FieldInfo? field = AccessTools.Field(target.GetType(), name);
        return field?.GetValue(target);
    }


    private static Type? ResolveTypeByName(string typeName)
    {
        return AccessTools.TypeByName(typeName)
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeGetTypes)
                .FirstOrDefault(type => string.Equals(type.FullName, typeName, StringComparison.Ordinal)
                    || string.Equals(type.Name, typeName, StringComparison.Ordinal));
    }

    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type != null).Cast<Type>().ToArray();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    private static Exception Unwrap(Exception exception)
    {
        Exception current = exception;
        while (current is TargetInvocationException && current.InnerException != null)
        {
            current = current.InnerException;
        }

        return current;
    }


#else
    public static void CaptureBeforeOpen(string source)
    {
    }

    public static Task RestoreAfterCloseAsync(string source)
    {
        return Task.CompletedTask;
    }

    public static void RestoreAfterFailedOpen(string source)
    {
    }
#endif
}
