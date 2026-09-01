using System;
using System.Threading.Tasks;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using HarmonyLib;
#endif

// Responsibility: Presents and coordinates Operator Direct Inventory Lifecycle in the Off-Raid Operator inventory bridge.
// Flow: Canonical API/runtime state is projected into view models and Unity/TMP controls; explicit user actions are delegated back through API/service boundaries.
// Authority boundary: Presentation layer only; it does not become persistence, economy, medical, or raid-runtime authority.
// Invariant: UI refreshes are idempotent from canonical state and temporary view state must not outlive its owning screen/session.
namespace Vanguard.Client.UI.OffRaid.Inventory;

/// <summary>
/// Owns the full lifecycle of the direct Operator inventory screen.
///
/// The vanilla InventoryScreen is not opened through MainMenuController.method_32()
/// because Vanguard must feed it a temporary Operator profile.  That is safe only if
/// the whole open -> close -> commit -> player profile reload sequence is treated as
/// one serialized transaction.  Re-entering the direct screen while the previous menu
/// rebuild is still settling leaves EFT in a half-restored UI/input state after
/// opening several Operator inventories in a row.
///
/// This class prevents that bad state from being created by serializing direct
/// inventory sessions and by keeping the UI disabled until the player menu has
/// been rebuilt after close.
/// </summary>
internal static class VanguardOperatorDirectInventoryLifecycle
{
    private static readonly object Gate = new();
    private static DirectInventoryLifecycleState state = DirectInventoryLifecycleState.Idle;
    private static DateTimeOffset readyAfterUtc = DateTimeOffset.MinValue;
    private static string? activeOperatorId;
    private static string lastReason = "idle";

    private static readonly TimeSpan PostCloseSettle = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan FaultCooldown = TimeSpan.FromMilliseconds(2500);

    public static bool IsBusy
    {
        get
        {
            lock (Gate)
            {
                return IsBusyNoLock(DateTimeOffset.UtcNow);
            }
        }
    }

    public static string BusyReason
    {
        get
        {
            lock (Gate)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (state != DirectInventoryLifecycleState.Idle)
                {
                    return $"state_{state.ToString().ToLowerInvariant()}";
                }

                if (now < readyAfterUtc)
                {
                    return $"menu_rebuild_settling_{Math.Max(0, (int)Math.Ceiling((readyAfterUtc - now).TotalMilliseconds))}ms";
                }

                return lastReason;
            }
        }
    }

    public static bool CanOpenNow(out string reason)
    {
        lock (Gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (IsBusyNoLock(now))
            {
                reason = BusyReasonNoLock(now);
                return false;
            }

            reason = "ready";
            return true;
        }
    }

    /// <summary>
    /// Reconciles the one safe stale-state case observed after a direct Operator inventory:
    /// EFT is already back on the main menu and the server technical inventory session is
    /// inactive, but the local lifecycle still says Open because the close callback was lost.
    /// Closing/RebuildingMenu states are deliberately never repaired here.
    /// </summary>
    public static bool TryRecoverOrphanedOpenOnMainMenu(string source, bool inventoryModeActive)
    {
#if SPT_CLIENT
        if (inventoryModeActive)
        {
            return false;
        }

        string screen = DescribeCurrentScreen();
        if (!IsMainMenuScreenDescription(screen))
        {
            return false;
        }

        string? operatorId;
        lock (Gate)
        {
            if (state != DirectInventoryLifecycleState.Open)
            {
                return false;
            }

            operatorId = activeOperatorId;
            state = DirectInventoryLifecycleState.Idle;
            activeOperatorId = null;
            readyAfterUtc = DateTimeOffset.MinValue;
            lastReason = "orphaned_open_recovered_on_main_menu";
        }

        VanguardClientDiagnosticsLog.Warning(
            VanguardBuildVersion.OperatorDirectEquipmentScreenStatusTag,
            $"direct_inventory_lifecycle_orphaned_open_recovered source={source}; operator={operatorId ?? "<none>"}; inventoryModeActive=False; screen={screen}");
        return true;
#else
        return false;
#endif
    }

    public static bool TryBeginOpen(string source, string? operatorId, out string reason)
    {
        lock (Gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (IsBusyNoLock(now))
            {
                reason = BusyReasonNoLock(now);
                VanguardClientDiagnosticsLog.Info(
                    VanguardBuildVersion.OperatorDirectEquipmentScreenStatusTag,
                    $"direct_inventory_lifecycle_open_refused source={source}; reason={reason}; activeOperator={activeOperatorId ?? "<none>"}; requestedOperator={operatorId ?? "<none>"}");
                return false;
            }

            state = DirectInventoryLifecycleState.Opening;
            activeOperatorId = operatorId;
            lastReason = "opening";
            reason = "open_started";
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.OperatorDirectEquipmentScreenStatusTag,
                $"direct_inventory_lifecycle_open_started source={source}; operator={operatorId ?? "<none>"}");
            return true;
        }
    }

    public static void MarkOpenShown(string source, string? operatorId)
    {
        lock (Gate)
        {
            state = DirectInventoryLifecycleState.Open;
            activeOperatorId = operatorId ?? activeOperatorId;
            lastReason = "screen_open";
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorDirectEquipmentScreenStatusTag,
            $"direct_inventory_lifecycle_open_completed source={source}; operator={operatorId ?? activeOperatorId ?? "<none>"}");
    }

    public static void MarkCloseStarted(string source)
    {
        lock (Gate)
        {
            state = DirectInventoryLifecycleState.Closing;
            lastReason = "closing";
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorDirectEquipmentScreenStatusTag,
            $"direct_inventory_lifecycle_close_started source={source}; operator={activeOperatorId ?? "<none>"}");
    }

    public static void MarkMenuRebuildStarted(string source)
    {
        lock (Gate)
        {
            state = DirectInventoryLifecycleState.RebuildingMenu;
            lastReason = "rebuilding_menu";
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorDirectEquipmentScreenStatusTag,
            $"direct_inventory_lifecycle_menu_rebuild_started source={source}; operator={activeOperatorId ?? "<none>"}");
    }

    public static async Task CompleteAfterMenuRebuildAsync(string source, bool success, string reason)
    {
        TimeSpan settle = success ? PostCloseSettle : FaultCooldown;
        DateTimeOffset readyAt = DateTimeOffset.UtcNow + settle;
        string? operatorId;
        lock (Gate)
        {
            operatorId = activeOperatorId;
            state = DirectInventoryLifecycleState.Idle;
            activeOperatorId = null;
            readyAfterUtc = readyAt;
            lastReason = reason;
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorDirectEquipmentScreenStatusTag,
            $"direct_inventory_lifecycle_menu_rebuild_completed source={source}; success={success}; reason={reason}; operator={operatorId ?? "<none>"}; settleMs={(int)settle.TotalMilliseconds}");

        // This delay is a lifecycle boundary that prevents a second direct
        // InventoryScreen from being opened while EFT is still
        // rebuilding the root menu after ProfileLoadingScreen/MainMenuScreen transition.
        await Task.Delay(settle);
    }

    public static void MarkFailedOpen(string source, string reason)
    {
        DateTimeOffset readyAt = DateTimeOffset.UtcNow + FaultCooldown;
        string? operatorId;
        lock (Gate)
        {
            operatorId = activeOperatorId;
            state = DirectInventoryLifecycleState.Idle;
            activeOperatorId = null;
            readyAfterUtc = readyAt;
            lastReason = reason;
        }

        VanguardClientDiagnosticsLog.Warning(
            VanguardBuildVersion.OperatorDirectEquipmentScreenStatusTag,
            $"direct_inventory_lifecycle_open_failed source={source}; reason={reason}; operator={operatorId ?? "<none>"}; settleMs={(int)FaultCooldown.TotalMilliseconds}");
    }

#if SPT_CLIENT
    public static string DescribeCurrentScreen()
    {
        try
        {
            object? singleton = ResolveCurrentScreenSingleton();
            if (singleton == null)
            {
                return "screenSingleton=<none>";
            }

            object? root = ResolveMember(singleton, "RootScreenType");
            object? current = ResolveMember(singleton, "CurrentScreenController");
            object? currentType = current == null ? null : ResolveMember(current, "ScreenType");
            return $"root={root ?? "<null>"}; current={currentType ?? "<null>"}; controller={FormatTypeName(current?.GetType())}";
        }
        catch (Exception exception)
        {
            return $"screenDiagnosticsFailed={exception.GetType().Name}:{exception.Message}";
        }
    }

    private static bool IsMainMenuScreenDescription(string screen)
    {
        bool hasRootMenu = screen.IndexOf("root=Menu", StringComparison.OrdinalIgnoreCase) >= 0
            || screen.IndexOf("root=MainMenu", StringComparison.OrdinalIgnoreCase) >= 0;
        bool hasCurrentMenu = screen.IndexOf("current=Menu", StringComparison.OrdinalIgnoreCase) >= 0
            || screen.IndexOf("current=MainMenu", StringComparison.OrdinalIgnoreCase) >= 0
            || screen.IndexOf("MenuScreen", StringComparison.OrdinalIgnoreCase) >= 0;
        return hasRootMenu && hasCurrentMenu;
    }

    private static object? ResolveCurrentScreenSingleton()
    {
        Type? type = AccessTools.TypeByName("CurrentScreenSingletonClass");
        if (type == null)
        {
            return null;
        }

        return AccessTools.Property(type, "Instance")?.GetValue(null)
            ?? AccessTools.Field(type, "Instance")?.GetValue(null)
            ?? AccessTools.Property(type, "instance")?.GetValue(null)
            ?? AccessTools.Field(type, "instance")?.GetValue(null);
    }

    private static object? ResolveMember(object target, string name)
    {
        try
        {
            var property = AccessTools.Property(target.GetType(), name);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(target);
            }

            return AccessTools.Field(target.GetType(), name)?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }
#endif

    private static bool IsBusyNoLock(DateTimeOffset now)
    {
        return state != DirectInventoryLifecycleState.Idle || now < readyAfterUtc;
    }

    private static string BusyReasonNoLock(DateTimeOffset now)
    {
        if (state != DirectInventoryLifecycleState.Idle)
        {
            return $"state_{state.ToString().ToLowerInvariant()}";
        }

        if (now < readyAfterUtc)
        {
            return $"menu_rebuild_settling_{Math.Max(0, (int)Math.Ceiling((readyAfterUtc - now).TotalMilliseconds))}ms";
        }

        return lastReason;
    }

    private static string FormatTypeName(Type? type)
    {
        return type == null ? "<null>" : (type.FullName ?? type.Name).Replace(';', ',');
    }

    private enum DirectInventoryLifecycleState
    {
        Idle,
        Opening,
        Open,
        Closing,
        RebuildingMenu
    }
}
