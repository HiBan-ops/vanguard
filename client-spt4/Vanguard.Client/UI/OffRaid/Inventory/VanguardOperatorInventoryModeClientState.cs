using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Vanguard.Client.Api;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using HarmonyLib;
using UnityEngine;
#endif

// Responsibility: owns the client-side lifecycle state for entering/leaving Operator inventory mode and restoring the ordinary Tarkov player profile/menu afterward.
// Flow: Enter/exit/status calls mirror the server session locally; profile descriptors are rebound for the temporary Operator view, then a successful exit triggers the main-menu profile reload/reassertion path before normal menu actions resume.
// Authority boundary: the server decides whether inventory mode is active and owns Operator persistence; this state holder only coordinates the client UI/profile bridge and cannot commit wallet or server inventory truth itself.
// Invariant: local active/rebind flags must converge with server exit state, and a successful server exit must restore the player profile even when an earlier best-effort direct commit failed.
namespace Vanguard.Client.UI.OffRaid.Inventory;

internal static class VanguardOperatorInventoryModeClientState
{
    private static readonly VanguardApiClient ApiClient = new();

    public static bool IsActive { get; private set; }

    public static string? OperatorId { get; private set; }

    public static string? OperatorDisplayName { get; private set; }

    public static string? OperatorCallsign { get; private set; }

    public static string? InventoryProfileId { get; private set; }

    public static bool OperatorProfileApplied { get; private set; }

    public static bool ReloadInProgress { get; private set; }

    public static bool PendingOpenCharacter { get; private set; }

    public static VanguardOperatorInventoryModeResponseDto Enter(string? operatorId)
    {
        VanguardOperatorInventoryModeResponseDto response = ApiClient.EnterInventoryMode(operatorId);
        if (response.Success && response.Active)
        {
            IsActive = true;
            OperatorId = response.OperatorId ?? operatorId;
            OperatorDisplayName = response.OperatorDisplayName ?? response.Summary?.DisplayName ?? operatorId;
            OperatorCallsign = response.OperatorCallsign ?? response.OperatorDisplayName ?? response.Summary?.DisplayName ?? operatorId;
            InventoryProfileId = response.OperatorInventoryProfileId ?? response.Summary?.InventoryProfileId;
            OperatorProfileApplied = false;
            ReloadInProgress = false;
            PendingOpenCharacter = false;
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_INVENTORY_OFFRAID_STATUS", $"client enter operator={OperatorId ?? "<none>"}; inventoryProfile={InventoryProfileId ?? "<none>"}; reloadRequested=false; directEquipmentEntry=true");
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_PROFILE_REBIND_STATUS", $"enter armed operator={OperatorId ?? "<none>"}; inventoryProfile={InventoryProfileId ?? "<none>"}; profileApplied=false");
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"enter armed operator={OperatorId ?? "<none>"}; inventoryProfile={InventoryProfileId ?? "<none>"}; directEquipmentEntry=true");
        }
        else
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_INVENTORY_OFFRAID_STATUS", $"client enter failed operator={operatorId ?? "<none>"}; reason={response.Reason ?? "no_response"}");
        }

        return response;
    }

    public static VanguardOperatorInventoryModeResponseDto Exit(bool skipProfileReload = false)
    {
        VanguardOperatorInventoryModeResponseDto response = ApiClient.ExitInventoryMode(OperatorId);
        if (response.Success)
        {
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_INVENTORY_OFFRAID_STATUS", $"client exit operator={OperatorId ?? "<none>"}; reason={response.Reason ?? "ok"}; reloadRequested={!skipProfileReload}");
            ClearInventoryModeClientState();
            if (!skipProfileReload)
            {
                TryReloadMainMenuProfile();
            }
            else
            {
                VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", "player_profile_reload_skipped_after_direct_entry_exit");
            }
        }
        else
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_INVENTORY_OFFRAID_STATUS", $"client exit failed operator={OperatorId ?? "<none>"}; reason={response.Reason ?? "no_response"}");
        }

        return response;
    }

    public static VanguardOperatorInventoryModeResponseDto ExitForDirectCommitRefresh()
    {
        string? operatorId = OperatorId;
        VanguardOperatorInventoryModeResponseDto response = ApiClient.ExitInventoryMode(operatorId);
        if (response.Success)
        {
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_INVENTORY_OFFRAID_STATUS", $"client exit operator={operatorId ?? "<none>"}; reason={response.Reason ?? "ok"}; reloadRequested=true; source=direct_commit_refresh");
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_PLAYER_STASH_REFRESH_STATUS", $"player_stash_refresh_requested_after_direct_commit operator={operatorId ?? "<none>"}; reason={response.Reason ?? "ok"}");
            ClearInventoryModeClientState();
        }
        else
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_INVENTORY_OFFRAID_STATUS", $"client exit failed operator={operatorId ?? "<none>"}; reason={response.Reason ?? "no_response"}; source=direct_commit_refresh");
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_PLAYER_STASH_REFRESH_STATUS", $"player_stash_refresh_exit_failed operator={operatorId ?? "<none>"}; reason={response.Reason ?? "no_response"}");
        }

        return response;
    }

    public static void ForceClearForRaidStart(string source)
    {
        bool hadState = IsActive || ReloadInProgress || PendingOpenCharacter || OperatorProfileApplied;
        string? operatorId = OperatorId;
        ClearInventoryModeClientState();
        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
            $"direct_entry_raid_start_clear source={source}; hadState={hadState}; operator={operatorId ?? "<none>"}");
    }

    public static void RefreshFromServerStatus()
    {
        try
        {
            VanguardOperatorInventoryModeResponseDto status = ApiClient.GetInventoryModeStatus();
            bool wasActive = IsActive;
            string? previousOperatorId = OperatorId;
            string? previousInventoryProfileId = InventoryProfileId;
            IsActive = status.Success && status.Active;
            OperatorId = IsActive ? status.OperatorId : null;
            OperatorDisplayName = IsActive ? status.OperatorDisplayName ?? status.Summary?.DisplayName : null;
            OperatorCallsign = IsActive ? status.OperatorCallsign ?? status.OperatorDisplayName ?? status.Summary?.DisplayName : null;
            InventoryProfileId = IsActive ? status.OperatorInventoryProfileId ?? status.Summary?.InventoryProfileId : null;
            if (!IsActive)
            {
                OperatorProfileApplied = false;
                ReloadInProgress = false;
                PendingOpenCharacter = false;
            }
            else if (!wasActive
                || !string.Equals(previousOperatorId, OperatorId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(previousInventoryProfileId, InventoryProfileId, StringComparison.OrdinalIgnoreCase))
            {
                OperatorProfileApplied = false;
            }
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_INVENTORY_OFFRAID_STATUS", $"status refresh failed: {exception.Message}");
        }
    }

    public static string GetProfileDescriptorsJson()
    {
        VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_PROFILE_REBIND_STATUS", $"profiles_route_called operator={OperatorId ?? "<none>"}; inventoryProfile={InventoryProfileId ?? "<none>"}");
        return ApiClient.GetInventoryModeProfilesJson();
    }

    public static bool TryBeginProfileRebind(bool pendingOpenCharacter)
    {
        if (!IsActive)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_PROFILE_REBIND_STATUS", "rebind refused: inventory mode inactive");
            return false;
        }

        if (ReloadInProgress)
        {
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_PROFILE_REBIND_STATUS", $"rebind already running operator={OperatorId ?? "<none>"}; pendingOpenCharacter={PendingOpenCharacter}");
            return false;
        }

        ReloadInProgress = true;
        PendingOpenCharacter = pendingOpenCharacter;
        VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_PROFILE_REBIND_STATUS", $"reload_requested operator={OperatorId ?? "<none>"}; inventoryProfile={InventoryProfileId ?? "<none>"}; pendingOpenCharacter={PendingOpenCharacter}");
        return true;
    }


    public static void BeginDirectEquipmentOpen()
    {
        if (!IsActive)
        {
            return;
        }

        ReloadInProgress = true;
        PendingOpenCharacter = true;
        VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"direct_open_begin operator={OperatorId ?? "<none>"}; inventoryProfile={InventoryProfileId ?? "<none>"}");
    }

    public static void FinishDirectEquipmentOpen(string reason)
    {
        ReloadInProgress = false;
        PendingOpenCharacter = false;
        VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"direct_open_finished reason={reason}; operator={OperatorId ?? "<none>"}; profileApplied={OperatorProfileApplied}");
    }

    public static void MarkOperatorProfileApplied(string? profileId)
    {
        OperatorProfileApplied = true;
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            InventoryProfileId = profileId;
        }

        VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_PROFILE_REBIND_STATUS", $"operator_profile_applied operator={OperatorId ?? "<none>"}; inventoryProfile={InventoryProfileId ?? profileId ?? "<none>"}");
    }

    public static void FinishProfileRebind(bool success, string reason)
    {
        ReloadInProgress = false;
        PendingOpenCharacter = false;
        VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_PROFILE_REBIND_STATUS", $"rebind_finished success={success}; reason={reason}; operator={OperatorId ?? "<none>"}; profileApplied={OperatorProfileApplied}");
    }

    public static async Task<bool> TryReloadMainMenuProfileAfterDirectCommitAsync(object? knownMainMenuController = null)
    {
#if SPT_CLIENT
        try
        {
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_PLAYER_STASH_REFRESH_STATUS", $"player_profile_reload_started source=direct_commit_refresh; screen={VanguardOperatorDirectInventoryLifecycle.DescribeCurrentScreen()}");
            VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OperatorInventoryExitReloadStatusTag, $"offraid_exit_reload_sequence_started source=direct_commit_refresh; screen={VanguardOperatorDirectInventoryLifecycle.DescribeCurrentScreen()}");
            SetPreloaderVisible(true);

            object? mainMenuController = knownMainMenuController ?? ResolveCurrentMainMenuController();
            if (mainMenuController == null)
            {
                VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_PLAYER_STASH_REFRESH_STATUS", "player_profile_reload_failed reason=main_menu_controller_not_found");
                return false;
            }

            // Use the exact vanilla profile reload method.  The previous fallback that
            // selected any zero-argument Task on MainMenuController could bind to an
            // unrelated async menu method if names change, leaving the menu visually
            // half-refreshed after repeated direct Operator inventory sessions.
            MethodInfo? reloadMethod = AccessTools.Method(mainMenuController.GetType(), "method_21");
            if (reloadMethod == null || !typeof(Task).IsAssignableFrom(reloadMethod.ReturnType) || reloadMethod.GetParameters().Length != 0)
            {
                VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_PLAYER_STASH_REFRESH_STATUS", "player_profile_reload_failed reason=method_21_not_found_or_unexpected_signature");
                return false;
            }

            object? result = reloadMethod.Invoke(mainMenuController, Array.Empty<object>());
            if (result is Task task)
            {
                await task;
            }

            await EnsureMainMenuRootReadyAsync(mainMenuController, "direct_commit_refresh");
            if (ShouldRunSecondMainMenuRootReassertion(out string screenAfterFirstPass))
            {
                VanguardClientDiagnosticsLog.Info(
                    "VANGUARD_OPERATOR_PLAYER_STASH_REFRESH_STATUS",
                    $"player_menu_root_reassert_second_pass_needed source=direct_commit_refresh; reason=menu_root_not_ready; screen={screenAfterFirstPass}");
                await EnsureMainMenuRootReadyAsync(mainMenuController, "direct_commit_refresh_conditional_second_pass");
            }
            else
            {
                VanguardClientDiagnosticsLog.Info(
                    "VANGUARD_OPERATOR_PLAYER_STASH_REFRESH_STATUS",
                    $"player_menu_root_reassert_second_pass_skipped source=direct_commit_refresh; reason=menu_root_ready; screen={screenAfterFirstPass}");
            }

            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_PLAYER_STASH_REFRESH_STATUS", $"player_profile_reload_completed source=direct_commit_refresh; screen={VanguardOperatorDirectInventoryLifecycle.DescribeCurrentScreen()}");
            VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OperatorInventoryExitReloadStatusTag, $"offraid_exit_reload_sequence_completed source=direct_commit_refresh; screen={VanguardOperatorDirectInventoryLifecycle.DescribeCurrentScreen()}");
            return true;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_PLAYER_STASH_REFRESH_STATUS", $"player_profile_reload_failed reason={exception.GetType().Name}: {exception.Message}; screen={VanguardOperatorDirectInventoryLifecycle.DescribeCurrentScreen()}");
            VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.OperatorInventoryExitReloadStatusTag, $"offraid_exit_reload_sequence_failed reason={exception.GetType().Name}: {exception.Message}; screen={VanguardOperatorDirectInventoryLifecycle.DescribeCurrentScreen()}");
            return false;
        }
        finally
        {
            SetPreloaderVisible(false);
        }
#else
        await Task.CompletedTask;
        return false;
#endif
    }

    private static void ClearInventoryModeClientState()
    {
        VanguardOperatorEquipmentBuildsFlow.Clear("inventory_mode_client_state_clear");
        VanguardOperatorInventorySessionNavigation.Clear("inventory_mode_client_state_clear");
        VanguardOperatorDirectEquipmentScreenEntry.ClearActiveSessionContext("inventory_mode_client_state_clear");
        IsActive = false;
        OperatorId = null;
        OperatorDisplayName = null;
        OperatorCallsign = null;
        InventoryProfileId = null;
        OperatorProfileApplied = false;
        ReloadInProgress = false;
        PendingOpenCharacter = false;
    }


    private static void TryReloadMainMenuProfile()
    {
#if SPT_CLIENT
        try
        {
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_PROFILE_REBIND_STATUS", "player_profile_reload_requested_after_exit");
            SetPreloaderVisible(true);
            Type? tarkovApplicationType = AccessTools.TypeByName("EFT.TarkovApplication") ?? AccessTools.TypeByName("TarkovApplication");
            if (tarkovApplicationType == null)
            {
                VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_INVENTORY_OFFRAID_STATUS", "TarkovApplication type not found; profile reload not forced.");
                return;
            }

            object? application = ResolveTarkovApplication(tarkovApplicationType);
            if (application == null)
            {
                VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_INVENTORY_OFFRAID_STATUS", "TarkovApplication instance not found; profile reload not forced.");
                return;
            }

            object? mainMenuController = AccessTools.Field(application.GetType(), "mainMenuControllerClass")?.GetValue(application)
                ?? AccessTools.Property(application.GetType(), "MainMenuController")?.GetValue(application);
            if (mainMenuController == null)
            {
                VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_INVENTORY_OFFRAID_STATUS", "Main menu controller not found; profile reload not forced.");
                return;
            }

            MethodInfo? reloadMethod = AccessTools.Method(mainMenuController.GetType(), "method_21")
                ?? mainMenuController.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(method => method.GetParameters().Length == 0 && typeof(Task).IsAssignableFrom(method.ReturnType));
            object? result = reloadMethod?.Invoke(mainMenuController, Array.Empty<object>());
            if (result is Task task)
            {
                _ = task.ContinueWith(_ => SetPreloaderVisible(false));
            }
            else
            {
                SetPreloaderVisible(false);
            }
        }
        catch (Exception exception)
        {
            SetPreloaderVisible(false);
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_INVENTORY_OFFRAID_STATUS", $"profile reload failed: {exception.GetType().Name}: {exception.Message}");
        }
#endif
    }

#if SPT_CLIENT
    private static object? ResolveCurrentMainMenuController()
    {
        Type? tarkovApplicationType = AccessTools.TypeByName("EFT.TarkovApplication") ?? AccessTools.TypeByName("TarkovApplication");
        if (tarkovApplicationType == null)
        {
            return null;
        }

        object? application = ResolveTarkovApplication(tarkovApplicationType);
        if (application == null)
        {
            return null;
        }

        return AccessTools.Field(application.GetType(), "mainMenuControllerClass")?.GetValue(application)
            ?? AccessTools.Property(application.GetType(), "MainMenuController")?.GetValue(application);
    }

    private static object? ResolveTarkovApplication(Type tarkovApplicationType)
    {
        MethodInfo? existMethod = tarkovApplicationType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "Exist" && method.GetParameters().Length == 1);
        if (existMethod != null)
        {
            object?[] parameters = { null };
            object? result = existMethod.Invoke(null, parameters);
            if (result is bool success && success)
            {
                return parameters[0];
            }
        }

        UnityEngine.Object? instance = UnityEngine.Object.FindObjectOfType(tarkovApplicationType);
        return instance;
    }

    private static bool ShouldRunSecondMainMenuRootReassertion(out string screen)
    {
        screen = VanguardOperatorDirectInventoryLifecycle.DescribeCurrentScreen();

        // The second menu-root reassertion used to run unconditionally as part of the
        // old cursor workaround chain.  runtime qualification proved cursor recovery belongs to the
        // deferred raid InputTree cleanup, so the off-raid exit path now keeps only a
        // guarded fallback: run a second pass only if the first pass did not actually
        // bring EFT back to a menu root/current screen.
        bool hasRootMenu = screen.IndexOf("root=Menu", StringComparison.OrdinalIgnoreCase) >= 0
            || screen.IndexOf("root=MainMenu", StringComparison.OrdinalIgnoreCase) >= 0;
        bool hasCurrentMenu = screen.IndexOf("current=Menu", StringComparison.OrdinalIgnoreCase) >= 0
            || screen.IndexOf("current=MainMenu", StringComparison.OrdinalIgnoreCase) >= 0
            || screen.IndexOf("MenuScreen", StringComparison.OrdinalIgnoreCase) >= 0;

        return !(hasRootMenu && hasCurrentMenu);
    }

    private static async Task EnsureMainMenuRootReadyAsync(object mainMenuController, string source)
    {
        try
        {
            MethodInfo? showMenuScreen = AccessTools.Method(mainMenuController.GetType(), "method_60");
            if (showMenuScreen != null && typeof(Task).IsAssignableFrom(showMenuScreen.ReturnType) && showMenuScreen.GetParameters().Length == 0)
            {
                object? result = showMenuScreen.Invoke(mainMenuController, Array.Empty<object>());
                if (result is Task task)
                {
                    await task;
                }

                VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_PLAYER_STASH_REFRESH_STATUS", $"player_menu_root_reasserted source={source}; screen={VanguardOperatorDirectInventoryLifecycle.DescribeCurrentScreen()}");
                VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OperatorInventoryExitReloadStatusTag, $"main_menu_root_reasserted source={source}; screen={VanguardOperatorDirectInventoryLifecycle.DescribeCurrentScreen()}");
            }
            else
            {
                VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_PLAYER_STASH_REFRESH_STATUS", $"player_menu_root_reassert_skipped source={source}; reason=method_60_missing; screen={VanguardOperatorDirectInventoryLifecycle.DescribeCurrentScreen()}");
            }

            // Let the Unity screen queue finish activating the root MenuScreen before
            // Vanguard allows another direct Operator InventoryScreen to be opened.
            await Task.Delay(250);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_PLAYER_STASH_REFRESH_STATUS", $"player_menu_root_reassert_failed source={source}; reason={exception.GetType().Name}: {exception.Message}; screen={VanguardOperatorDirectInventoryLifecycle.DescribeCurrentScreen()}");
        }
    }

    private static void SetPreloaderVisible(bool visible)
    {
        try
        {
            Type? singletonType = AccessTools.TypeByName("Comfort.Common.Singleton`1");
            Type? preloaderType = AccessTools.TypeByName("EFT.UI.PreloaderUI") ?? AccessTools.TypeByName("PreloaderUI");
            if (singletonType == null || preloaderType == null)
            {
                return;
            }

            Type closedSingleton = singletonType.MakeGenericType(preloaderType);
            object? instance = AccessTools.Property(closedSingleton, "Instance")?.GetValue(null);
            MethodInfo? method = instance == null ? null : AccessTools.Method(instance.GetType(), "SetLoaderStatus", new[] { typeof(bool) });
            method?.Invoke(instance, new object[] { visible });
        }
        catch
        {
            // Visual loading indicator is best-effort only.
        }
    }
#endif
}
