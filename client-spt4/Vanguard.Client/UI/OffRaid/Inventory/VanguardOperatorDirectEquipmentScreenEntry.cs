using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vanguard.Client.Api;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using HarmonyLib;
using UnityEngine;
#endif

// Responsibility: bridges the Vanguard Operator inventory workflow into EFT equipment screens and coordinates the return path to the player profile/menu.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: it drives UI/profile loading only; the server inventory-mode service remains authoritative for session/commit/exit state.
// Invariant: any server-confirmed exit triggers player-profile restoration regardless of the separate best-effort direct-commit result.

namespace Vanguard.Client.UI.OffRaid.Inventory;

internal static class VanguardOperatorDirectEquipmentScreenEntry
{
    private static bool openInProgress;
    private static bool closeInProgress;
    private static object? activeOperatorItemUiContextOwner;
    private static bool operatorItemUiContextActive;

    public static bool TryOpenFromCurrentMainMenu(string source, out string reason)
    {
#if SPT_CLIENT
        reason = "unknown";
        try
        {
            object? mainMenuController = ResolveCurrentMainMenuController();
            if (mainMenuController == null)
            {
                reason = "main_menu_controller_not_found";
                VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"direct_entry_failed source={source}; reason={reason}");
                return false;
            }

            return TryOpenFromMainMenu(mainMenuController, source, out reason);
        }
        catch (Exception exception)
        {
            reason = exception.GetType().Name + ":" + exception.Message;
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"direct_entry_failed source={source}; reason={reason}");
            return false;
        }
#else
        reason = "spt_client_not_defined";
        return false;
#endif
    }

#if SPT_CLIENT
    public static bool TryOpenFromMainMenu(object mainMenuController, string source, out string reason)
    {
        return TryOpenFromMainMenu(mainMenuController, source, out reason, out _);
    }

    public static bool TryOpenFromMainMenu(object mainMenuController, string source, out string reason, out bool vanillaFallbackSafe)
    {
        vanillaFallbackSafe = false;
        reason = "unknown";
        if (!VanguardOperatorInventoryModeClientState.IsActive)
        {
            reason = "inventory_mode_inactive";
            return false;
        }

        if (openInProgress)
        {
            reason = "direct_entry_already_running";
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"direct_entry_ignored source={source}; reason={reason}; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}");
            return false;
        }

        if (!VanguardOperatorDirectInventoryLifecycle.TryBeginOpen(source, VanguardOperatorInventoryModeClientState.OperatorId, out reason))
        {
            // The server-side technical session was entered by the UI before this
            // direct screen was requested.  Close that session, but do not mutate the
            // lifecycle gate: it is protecting a previous close/rebuild transaction.
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"direct_entry_lifecycle_rejected_session_exit reason={reason}; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}");
            VanguardOperatorInventoryModeClientState.Exit(skipProfileReload: true);
            return false;
        }

        openInProgress = true;
        closeInProgress = false;
        VanguardOperatorDirectInventoryExitGuard.CaptureBeforeOpen("direct_entry_open");
        VanguardOperatorInventoryModeClientState.BeginDirectEquipmentOpen();
        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
            $"direct_entry_requested source={source}; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; inventoryProfile={VanguardOperatorInventoryModeClientState.InventoryProfileId ?? "<none>"}");

        try
        {
            if (!TryBuildOperatorInventoryScreen(mainMenuController, out object? screenController, out object? session, out object? operatorProfile, out object? inventoryController, out string? operatorProfileId, out reason))
            {
                VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"direct_entry_build_failed source={source}; reason={reason}");
                vanillaFallbackSafe = RecoverFailedDirectOpen("build_failed", reason);
                return false;
            }

            VanguardOperatorEquipmentBuildsFlow.CaptureDirectInventoryScreen(
                screenController,
                inventoryController,
                VanguardOperatorInventoryModeClientState.OperatorId,
                "direct_entry_controller_built");
            AttachCloseHandler(screenController!, mainMenuController, session, operatorProfile, inventoryController, operatorProfileId);
            if (!TryShowScreen(screenController!, out reason))
            {
                VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"direct_entry_show_failed source={source}; reason={reason}");
                vanillaFallbackSafe = RecoverFailedDirectOpen("show_failed", reason);
                return false;
            }

            VanguardOperatorInventoryModeClientState.MarkOperatorProfileApplied(operatorProfileId);
            VanguardOperatorDirectInventoryLifecycle.MarkOpenShown(source, VanguardOperatorInventoryModeClientState.OperatorId);
            VanguardOperatorInventoryExitReloadState.MarkOperatorInventoryOpened("direct_entry_opened", VanguardOperatorInventoryModeClientState.OperatorId);
            reason = "operator_inventory_screen_opened";
            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                $"direct_entry_opened source={source}; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; inventoryProfile={operatorProfileId ?? "<none>"}; screen={VanguardOperatorDirectInventoryLifecycle.DescribeCurrentScreen()}");
            return true;
        }
        catch (Exception exception)
        {
            Exception root = Unwrap(exception);
            reason = root.GetType().Name + ":" + root.Message;
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"direct_entry_exception source={source}; reason={reason}; wrapper={exception.GetType().Name}; stack={CompactStack(root)}");
            vanillaFallbackSafe = RecoverFailedDirectOpen("exception", reason);
            return false;
        }
        finally
        {
            openInProgress = false;
            VanguardOperatorInventoryModeClientState.FinishDirectEquipmentOpen(reason);
        }
    }

    private static bool TryBuildOperatorInventoryScreen(object mainMenuController, out object? screenController, out object? session, out object? operatorProfile, out object? inventoryControllerForCommit, out string? operatorProfileId, out string reason)
    {
        screenController = null;
        session = null;
        operatorProfile = null;
        inventoryControllerForCommit = null;
        operatorProfileId = null;
        reason = "unknown";

        session = ResolveMember(mainMenuController, "ISession");
        if (session == null)
        {
            reason = "session_not_found";
            return false;
        }

        if (!VanguardOperatorInventoryProfileLoader.TryLoadFirstOperatorProfile(out operatorProfile, out operatorProfileId, out reason) || operatorProfile == null)
        {
            return false;
        }

        object? profileId = ResolveMember(operatorProfile, "Id");
        Type? inventoryControllerType = ResolveTypeByName("GClass3388");
        if (inventoryControllerType == null)
        {
            reason = "inventory_controller_type_not_found";
            return false;
        }

        ConstructorInfo? inventoryConstructor = inventoryControllerType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(ctor => ctor.GetParameters().Length == 3);
        if (inventoryConstructor == null)
        {
            reason = "inventory_controller_constructor_not_found";
            return false;
        }

        VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"inventory_controller_ctor_begin profile={operatorProfileId ?? "<none>"}; type={inventoryControllerType.FullName ?? inventoryControllerType.Name}");
        object inventoryController = inventoryConstructor.Invoke(new[] { session, operatorProfile, profileId });
        inventoryControllerForCommit = inventoryController;
        VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"inventory_controller_ctor_success profile={operatorProfileId ?? "<none>"}");
        object? inventory = ResolveMember(inventoryController, "Inventory");
        object? stash = inventory == null ? null : ResolveMember(inventory, "Stash");
        if (stash == null)
        {
            reason = "operator_stash_not_found";
            return false;
        }

        object? health = ResolveMember(operatorProfile, "Health");
        object? skills = ResolveMember(operatorProfile, "Skills");
        if (!TryBuildHealthController(mainMenuController, health, inventoryController, skills, operatorProfileId, out object? healthController, out reason) || healthController == null)
        {
            return false;
        }
        object? questController = ResolveMember(mainMenuController, "LocalQuestControllerClass");
        bool profileUpdaterBound = TryRegisterOperatorProfileUpdater(
            session,
            operatorProfile,
            inventoryController,
            questController,
            operatorProfileId,
            out string profileUpdaterReason);
        if (profileUpdaterBound)
        {
            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
                $"operator_profile_updater_bound profile={operatorProfileId ?? "<none>"}; inventoryController={FormatTypeName(inventoryController.GetType())}; backendSession={FormatTypeName(session.GetType())}; reason={profileUpdaterReason}; backendProfileChangesCanApplyToComposite=true");
        }
        else
        {
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
                $"operator_profile_updater_bind_failed profile={operatorProfileId ?? "<none>"}; reason={profileUpdaterReason}; nativeBackendPurchasesMayNotRefreshComposite=true");
        }

        object? achievementController = ResolveMember(mainMenuController, "AbstractAchievementControllerClass");
        object? prestigeController = ResolveMember(mainMenuController, "AbstractPrestigeControllerClass");
        object? inventoryTabGear = ResolveEnumValue("EInventoryTab", "Gear");
        if (inventoryTabGear == null)
        {
            reason = "inventory_tab_gear_not_found";
            return false;
        }

        ConfigureItemUiContext(mainMenuController, session, operatorProfile, inventoryController, healthController, questController, "operator");

        Type? screenType = ResolveInventoryScreenControllerType();
        if (screenType == null)
        {
            reason = "inventory_screen_controller_type_not_found";
            return false;
        }

        ConstructorInfo? screenConstructor = screenType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(ctor => ctor.GetParameters().Length == 8);
        if (screenConstructor == null)
        {
            reason = "inventory_screen_constructor_not_found";
            return false;
        }

        VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"inventory_screen_ctor_begin profile={operatorProfileId ?? "<none>"}; screenType={screenType.FullName ?? screenType.Name}");
        screenController = screenConstructor.Invoke(new[]
        {
            session,
            healthController,
            inventoryController,
            questController,
            achievementController,
            prestigeController,
            stash,
            inventoryTabGear
        });

        object? stashId = ResolveMember(stash, "Id") ?? "<none>";
        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
            $"direct_entry_controller_built operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; profile={operatorProfileId ?? "<none>"}; stash={stashId}; screenType={screenType.FullName ?? screenType.Name}");
        reason = "ok";
        return true;
    }

    private static void AttachCloseHandler(object screenController, object mainMenuController, object? session, object? operatorProfile, object? inventoryController, string? operatorProfileId)
    {
        try
        {
            EventInfo? closeEvent = screenController.GetType().GetEvent("OnClose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (closeEvent == null || closeEvent.EventHandlerType == null)
            {
                VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", "direct_entry_close_handler_not_attached reason=OnClose_event_not_found");
                return;
            }

            Action closeAction = async () => await FinishOperatorEquipmentSessionAsync(mainMenuController, session, operatorProfile, inventoryController, operatorProfileId);
            Delegate handler = closeEvent.EventHandlerType == typeof(Action)
                ? closeAction
                : Delegate.CreateDelegate(closeEvent.EventHandlerType, closeAction.Target, closeAction.Method);
            closeEvent.AddEventHandler(screenController, handler);
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", "direct_entry_close_handler_attached");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"direct_entry_close_handler_failed reason={exception.GetType().Name}: {exception.Message}");
        }
    }

    private static async Task FinishOperatorEquipmentSessionAsync(object mainMenuController, object? session, object? operatorProfile, object? inventoryController, string? operatorProfileId)
    {
        // EquipmentBuildsScreen is a native child flow of the direct Operator inventory.
        // Its opening closes/hides InventoryScreen, but that transition is not a real
        // Vanguard inventory exit and must not commit/reload the player profile.
        if (VanguardOperatorEquipmentBuildsFlow.ShouldDeferDirectInventoryClose("direct_entry_close"))
        {
            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                $"direct_entry_close_deferred operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; reason=native_equipment_builds_subflow; commitRequested=false; playerReloadRequested=false");
            return;
        }

        if (closeInProgress)
        {
            return;
        }

        closeInProgress = true;
        try
        {
            VanguardOperatorDirectInventoryLifecycle.MarkCloseStarted("direct_entry_close");
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"direct_entry_closed commit_requested operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; screen={VanguardOperatorDirectInventoryLifecycle.DescribeCurrentScreen()}");
            await TryFlushOperationQueueAsync(session);
            bool directCommitSucceeded = TryDirectCommitOperatorProfile(operatorProfile, inventoryController, operatorProfileId, session);
            var exitResponse = VanguardOperatorInventoryModeClientState.ExitForDirectCommitRefresh();
            bool playerRefreshSucceeded = false;
            if (!exitResponse.Success)
            {
                VanguardClientDiagnosticsLog.Warning(
                    "VANGUARD_OPERATOR_PLAYER_STASH_REFRESH_STATUS",
                    $"player_stash_refresh_skipped directCommit={directCommitSucceeded}; exitSuccess=False; reason={exitResponse.Reason ?? "no_response"}");
            }
            else if (!directCommitSucceeded)
            {
                // Direct-commit is a best-effort client snapshot.  A successful inventory-mode exit
                // has already committed the authoritative server session and removed the player from
                // Operator profile redirection, so it must still converge through the normal player
                // profile/menu reload.  Skipping that reload leaves the already-shown MenuScreen in
                // its temporary Operator guard state and can poison the next off-raid transaction.
                VanguardClientDiagnosticsLog.Warning(
                    "VANGUARD_OPERATOR_PLAYER_STASH_REFRESH_STATUS",
                    $"player_stash_refresh_continuing_after_direct_commit_failure exitSuccess=True; reason={exitResponse.Reason ?? "no_response"}; recovery=authoritative_exit_then_player_menu_reload");
            }

            // The Operator inventory is opened through a direct vanilla InventoryScreen controller,
            // not through the normal player menu route.  The close path must first persist the
            // Operator equipment and restore the player stash, then force a real off-raid menu/profile
            // reload while still in the menu.  Raid-start input workarounds are intentionally absent.
            RestorePlayerItemUiContext(mainMenuController, "direct_entry_close_pre_reload");
            await VanguardOperatorDirectInventoryExitGuard.RestoreAfterCloseAsync("direct_entry_close_pre_reload");

            string reloadResultReason;
            if (exitResponse.Success)
            {
                VanguardOperatorInventoryExitReloadState.MarkExitReloadStarted("direct_entry_close");
                VanguardOperatorDirectInventoryLifecycle.MarkMenuRebuildStarted("direct_entry_close");
                playerRefreshSucceeded = await VanguardOperatorInventoryModeClientState.TryReloadMainMenuProfileAfterDirectCommitAsync(mainMenuController);
                RestorePlayerItemUiContext(mainMenuController, "direct_entry_close_post_reload");
                await VanguardOperatorDirectInventoryExitGuard.RestoreAfterCloseAsync("direct_entry_close_post_reload");
                reloadResultReason = playerRefreshSucceeded
                    ? (directCommitSucceeded ? "player_menu_reloaded" : "player_menu_reloaded_after_exit_fallback")
                    : (directCommitSucceeded ? "player_menu_reload_failed" : "player_menu_reload_failed_after_exit_fallback");
                VanguardOperatorInventoryExitReloadState.MarkExitReloadCompleted("direct_entry_close", playerRefreshSucceeded, reloadResultReason);
            }
            else
            {
                reloadResultReason = "inventory_mode_exit_failed";
                VanguardOperatorInventoryExitReloadState.MarkExitReloadCompleted("direct_entry_close", false, reloadResultReason);
            }

            await VanguardOperatorDirectInventoryLifecycle.CompleteAfterMenuRebuildAsync("direct_entry_close", playerRefreshSucceeded, reloadResultReason);
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"direct_entry_closed commit_completed playerStashRefresh={playerRefreshSucceeded}; itemUiContextRestored=True; exitGuardRestored=True; exitReloadState={VanguardOperatorInventoryExitReloadState.Describe()}; lifecycleReady={!VanguardOperatorDirectInventoryLifecycle.IsBusy}");
        }
        catch (Exception exception)
        {
            VanguardOperatorDirectInventoryLifecycle.MarkFailedOpen("direct_entry_close_exception", exception.GetType().Name + ":" + exception.Message);
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"direct_entry_close_commit_failed reason={exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            closeInProgress = false;
        }
    }


    private static bool TryDirectCommitOperatorProfile(object? operatorProfile, object? inventoryController, string? operatorProfileId, object? session)
    {
        if (operatorProfile == null)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_COMMIT_STATUS", $"direct_commit_snapshot_skipped reason=operator_profile_missing; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}");
            return false;
        }

        if (!TryBuildDirectCommitProfileDescriptorJson(operatorProfile, session, out string? descriptorJson, out int itemCount, out string reason) || string.IsNullOrWhiteSpace(descriptorJson))
        {
            int fallbackCount = CountInventoryItemsFromRuntimeController(inventoryController);
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_COMMIT_STATUS", $"direct_commit_snapshot_failed reason={reason}; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; profile={operatorProfileId ?? "<none>"}; runtimeControllerItems={fallbackCount}");
            return false;
        }

        VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_COMMIT_STATUS", $"direct_commit_snapshot_built operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; profile={operatorProfileId ?? "<none>"}; items={itemCount}; bytes={descriptorJson.Length}");

        try
        {
            var response = new VanguardApiClient().DirectCommitInventoryMode(
                VanguardOperatorInventoryModeClientState.OperatorId,
                descriptorJson,
                itemCount,
                "direct_equipment_screen");
            if (response.Success)
            {
                VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_COMMIT_STATUS", $"direct_commit_route_success operator={response.OperatorId ?? VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; reason={response.Reason ?? "ok"}; active={response.Active}; inventoryProfile={response.OperatorInventoryProfileId ?? operatorProfileId ?? "<none>"}; items={itemCount}");
                return true;
            }

            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_COMMIT_STATUS", $"direct_commit_route_failed operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; reason={response.Reason ?? "no_response"}; active={response.Active}; items={itemCount}");
            return false;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_COMMIT_STATUS", $"direct_commit_route_exception operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; reason={exception.GetType().Name}: {exception.Message}; items={itemCount}");
            return false;
        }
    }

    private static bool TryBuildDirectCommitProfileDescriptorJson(object operatorProfile, object? session, out string? descriptorJson, out int itemCount, out string reason)
    {
        descriptorJson = null;
        itemCount = 0;
        reason = "unknown";

        Type? descriptorType = ResolveTypeByName("CompleteProfileDescriptorClass");
        if (descriptorType == null)
        {
            reason = "complete_profile_descriptor_type_not_found";
            return false;
        }

        object? searchController = ResolveSearchController(session);
        if (searchController == null)
        {
            reason = "search_controller_not_found";
            return false;
        }

        ConstructorInfo? constructor = descriptorType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(ctor =>
            {
                ParameterInfo[] parameters = ctor.GetParameters();
                return parameters.Length == 2
                    && parameters[0].ParameterType.IsAssignableFrom(operatorProfile.GetType())
                    && parameters[1].ParameterType.IsAssignableFrom(searchController.GetType());
            });
        if (constructor == null)
        {
            reason = "complete_profile_descriptor_constructor_not_found";
            return false;
        }

        object descriptor;
        try
        {
            descriptor = constructor.Invoke(new[] { operatorProfile, searchController });
        }
        catch (Exception exception)
        {
            Exception root = Unwrap(exception);
            reason = "complete_profile_descriptor_ctor_" + root.GetType().Name;
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_COMMIT_STATUS", $"direct_commit_descriptor_ctor_failed reason={root.GetType().Name}: {root.Message}; wrapper={exception.GetType().Name}; stack={CompactStack(root)}");
            return false;
        }

        try
        {
            descriptorJson = JsonConvert.SerializeObject(descriptor);
            itemCount = CountInventoryItemsFromDescriptorJson(descriptorJson);
            reason = "ok";
            return true;
        }
        catch (Exception exception)
        {
            reason = "descriptor_json_serialize_" + exception.GetType().Name;
            return false;
        }
    }

    private static object? ResolveSearchController(object? session)
    {
        if (session != null)
        {
            object? fromSession = ResolveMember(session, "SearchController") ?? ResolveMember(session, "SearchControllerClass");
            if (fromSession != null)
            {
                return fromSession;
            }
        }

        Type? knownControllerType = ResolveTypeByName("GClass2240");
        if (knownControllerType != null)
        {
            object? instance = AccessTools.Field(knownControllerType, "Instance")?.GetValue(null)
                ?? AccessTools.Property(knownControllerType, "Instance")?.GetValue(null)
                ?? AccessTools.Field(knownControllerType, "instance")?.GetValue(null)
                ?? AccessTools.Property(knownControllerType, "instance")?.GetValue(null);
            if (instance != null)
            {
                return instance;
            }

            ConstructorInfo? constructor = knownControllerType.GetConstructor(Type.EmptyTypes);
            if (constructor != null)
            {
                return constructor.Invoke(Array.Empty<object>());
            }
        }

        return null;
    }

    private static int CountInventoryItemsFromDescriptorJson(string descriptorJson)
    {
        try
        {
            JToken root = JToken.Parse(descriptorJson);
            JToken? inventory = root["Inventory"] ?? root["inventory"] ?? root.SelectToken("characters.pmc.Inventory") ?? root.SelectToken("characters.pmc.inventory");
            JToken? items = inventory?["items"] ?? inventory?["Items"];
            return items is JArray array ? array.Count : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int CountInventoryItemsFromRuntimeController(object? inventoryController)
    {
        try
        {
            object? inventory = inventoryController == null ? null : ResolveMember(inventoryController, "Inventory");
            object? allItems = inventory == null ? null : ResolveMember(inventory, "AllItems") ?? ResolveMember(inventory, "Items");
            if (allItems is System.Collections.ICollection collection)
            {
                return collection.Count;
            }
        }
        catch
        {
        }

        return 0;
    }

    private static async Task TryFlushOperationQueueAsync(object? session)
    {
        if (session == null)
        {
            return;
        }

        try
        {
            MethodInfo? method = AccessTools.Method(session.GetType(), "FlushOperationQueue");
            object? result = method?.Invoke(session, Array.Empty<object>());
            if (result is Task task)
            {
                await task;
            }
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"direct_entry_flush_failed reason={exception.GetType().Name}: {exception.Message}");
        }
    }

    private static bool RecoverFailedDirectOpen(string stage, string reason)
    {
        try
        {
            if (!VanguardOperatorInventoryModeClientState.IsActive)
            {
                return false;
            }

            string? operatorId = VanguardOperatorInventoryModeClientState.OperatorId;
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"direct_entry_recovery_exit_requested stage={stage}; reason={reason}; operator={operatorId ?? "<none>"}");
            VanguardOperatorDirectInventoryLifecycle.MarkFailedOpen($"direct_entry_recovery:{stage}", reason);
            var exitResponse = VanguardOperatorInventoryModeClientState.Exit(skipProfileReload: true);
            bool inventoryModeCleared = exitResponse.Success && !VanguardOperatorInventoryModeClientState.IsActive;
            if (operatorItemUiContextActive && activeOperatorItemUiContextOwner is not null)
            {
                RestorePlayerItemUiContext(activeOperatorItemUiContextOwner, $"direct_entry_recovery:{stage}");
            }

            VanguardOperatorDirectInventoryExitGuard.RestoreAfterFailedOpen($"direct_entry_recovery:{stage}");
            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                $"direct_entry_recovery_exit_completed stage={stage}; operator={operatorId ?? "<none>"}; vanillaFallbackSafe={inventoryModeCleared}");
            return inventoryModeCleared;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"direct_entry_recovery_exit_failed stage={stage}; reason={exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static bool TryShowScreen(object screenController, out string reason)
    {
        reason = "unknown";
        MethodInfo? showScreenMethod = screenController.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => string.Equals(method.Name, "ShowScreen", StringComparison.Ordinal) && method.GetParameters().Length == 1);
        if (showScreenMethod == null)
        {
            reason = "show_screen_method_not_found";
            return false;
        }

        object? queued = ResolveEnumValue("EScreenState", "Queued");
        if (queued == null)
        {
            reason = "screen_state_queued_not_found";
            return false;
        }

        showScreenMethod.Invoke(screenController, new[] { queued });
        reason = "ok";
        return true;
    }

    private static bool TryRegisterOperatorProfileUpdater(
        object session,
        object operatorProfile,
        object inventoryController,
        object? questController,
        string? operatorProfileId,
        out string reason)
    {
        reason = "unknown";
        try
        {
            // EFT's normal MainMenuController binds GClass2331 after constructing the
            // player's backend InventoryController.  That updater is what consumes
            // backend ProfileChanges (new/deleted/changed stash items, trader data,
            // etc.) and applies them to the live inventory controller.
            //
            // Vanguard constructs a second GClass3388 around the composite Operator
            // profile, so it must register the same native updater for that profile id.
            // The player's native updater remains registered; Vanguard does not clear or
            // replace it.  The server already rekeys ItemEventRouter.ProfileChanges to
            // OperatorInventoryProfileId while inventory mode is active.
            Type? updaterType = ResolveTypeByName("GClass2331");
            if (updaterType == null)
            {
                reason = "profile_updater_type_not_found";
                return false;
            }

            MethodInfo? bindMethod = updaterType
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                {
                    if (!string.Equals(method.Name, "Bind", StringComparison.Ordinal) || method.GetParameters().Length != 4)
                    {
                        return false;
                    }

                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters[0].ParameterType.IsInstanceOfType(operatorProfile)
                        && parameters[1].ParameterType.IsInstanceOfType(session)
                        && parameters[2].ParameterType.IsInstanceOfType(inventoryController)
                        && (questController == null
                            ? !parameters[3].ParameterType.IsValueType
                            : parameters[3].ParameterType.IsInstanceOfType(questController));
                });

            if (bindMethod == null)
            {
                reason = "profile_updater_bind_method_not_found";
                return false;
            }

            bindMethod.Invoke(null, new[] { operatorProfile, session, inventoryController, questController });
            reason = $"native_GClass2331_Bind; profile={operatorProfileId ?? "<none>"}; updater={updaterType.FullName ?? updaterType.Name}";
            return true;
        }
        catch (TargetInvocationException exception)
        {
            Exception root = exception.InnerException ?? exception;
            reason = root.GetType().Name + ":" + root.Message;
            return false;
        }
        catch (Exception exception)
        {
            reason = exception.GetType().Name + ":" + exception.Message;
            return false;
        }
    }

    private static bool TryBuildHealthController(object mainMenuController, object? health, object inventoryController, object? skills, string? operatorProfileId, out object? healthController, out string reason)
    {
        healthController = null;
        reason = "unknown";

        object? playerHealthController = ResolveMainMenuHealthController(mainMenuController);
        Type? healthControllerType = playerHealthController?.GetType()
            ?? ResolveTypeByName("HealthControllerClass")
            ?? ResolveTypeByName("EFT.HealthControllerClass");

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
            $"health_controller_runtime_type profile={operatorProfileId ?? "<none>"}; runtimeType={FormatTypeName(healthControllerType)}; playerType={FormatTypeName(playerHealthController?.GetType())}; healthType={FormatTypeName(health?.GetType())}; inventoryType={FormatTypeName(inventoryController.GetType())}; skillsType={FormatTypeName(skills?.GetType())}");

        if (health == null || skills == null)
        {
            if (playerHealthController != null)
            {
                healthController = playerHealthController;
                reason = "health_controller_player_fallback_missing_inputs";
                VanguardClientDiagnosticsLog.Warning(
                    "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                    $"health_controller_reused_main_menu_fallback profile={operatorProfileId ?? "<none>"}; reason=missing_operator_health_or_skills");
                return true;
            }

            reason = "health_controller_inputs_not_found";
            return false;
        }

        if (healthControllerType == null)
        {
            if (playerHealthController != null)
            {
                healthController = playerHealthController;
                reason = "health_controller_player_fallback_missing_type";
                VanguardClientDiagnosticsLog.Warning(
                    "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                    $"health_controller_reused_main_menu_fallback profile={operatorProfileId ?? "<none>"}; reason=runtime_type_not_found");
                return true;
            }

            reason = "health_controller_type_not_found";
            return false;
        }

        ConstructorInfo[] constructors = healthControllerType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
            $"health_controller_ctor_candidates profile={operatorProfileId ?? "<none>"}; type={FormatTypeName(healthControllerType)}; candidates={FormatConstructorCandidates(constructors)}");

        foreach (ConstructorInfo constructor in constructors.OrderBy(ctor => ctor.GetParameters().Length == 3 ? 0 : ctor.GetParameters().Length == 4 ? 1 : 2))
        {
            if (TryBuildHealthControllerArguments(constructor, health, inventoryController, skills, out object?[]? args))
            {
                VanguardClientDiagnosticsLog.Info(
                    "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                    $"health_controller_ctor_begin profile={operatorProfileId ?? "<none>"}; signature={FormatConstructorSignature(constructor)}");

                try
                {
                    healthController = constructor.Invoke(args);
                    reason = "ok";
                    VanguardClientDiagnosticsLog.Info(
                        "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                        $"health_controller_ctor_success profile={operatorProfileId ?? "<none>"}; type={FormatTypeName(healthController.GetType())}; signature={FormatConstructorSignature(constructor)}");
                    return true;
                }
                catch (Exception exception)
                {
                    Exception root = Unwrap(exception);
                    VanguardClientDiagnosticsLog.Warning(
                        "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                        $"health_controller_ctor_failed profile={operatorProfileId ?? "<none>"}; signature={FormatConstructorSignature(constructor)}; reason={root.GetType().Name}:{root.Message}; wrapper={exception.GetType().Name}");
                }
            }
        }

        if (playerHealthController != null)
        {
            healthController = playerHealthController;
            reason = "health_controller_player_fallback_ctor_failed";
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
                $"health_controller_reused_main_menu_fallback profile={operatorProfileId ?? "<none>"}; reason=operator_ctor_unavailable_or_failed; playerType={FormatTypeName(playerHealthController.GetType())}");
            return true;
        }

        reason = "health_controller_constructor_not_found";
        return false;
    }

    private static bool TryBuildHealthControllerArguments(ConstructorInfo constructor, object health, object inventoryController, object skills, out object?[]? args)
    {
        args = null;
        ParameterInfo[] parameters = constructor.GetParameters();
        if (parameters.Length == 3)
        {
            object?[] candidate = { health, inventoryController, skills };
            if (ArgumentsMatch(parameters, candidate))
            {
                args = candidate;
                return true;
            }
        }

        if (parameters.Length == 4)
        {
            foreach (bool regeneration in new[] { true, false })
            {
                object?[] candidate = { health, inventoryController, skills, regeneration };
                if (ArgumentsMatch(parameters, candidate))
                {
                    args = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ArgumentsMatch(ParameterInfo[] parameters, object?[] args)
    {
        if (parameters.Length != args.Length)
        {
            return false;
        }

        for (int i = 0; i < parameters.Length; i++)
        {
            if (!IsValueCompatible(parameters[i].ParameterType, args[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValueCompatible(Type parameterType, object? value)
    {
        if (value == null)
        {
            return !parameterType.IsValueType || Nullable.GetUnderlyingType(parameterType) != null;
        }

        Type targetType = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
        Type valueType = value.GetType();
        if (targetType.IsAssignableFrom(valueType))
        {
            return true;
        }

        if (targetType == typeof(bool) && value is bool)
        {
            return true;
        }

        return false;
    }

    private static object? ResolveMainMenuHealthController(object mainMenuController)
    {
        object? byName = ResolveMember(mainMenuController, "HealthControllerClass") ?? ResolveMember(mainMenuController, "HealthController");
        if (byName != null)
        {
            return byName;
        }

        Type? expectedType = ResolveTypeByName("HealthControllerClass") ?? ResolveTypeByName("EFT.HealthControllerClass");
        if (expectedType == null)
        {
            return null;
        }

        Type? type = mainMenuController.GetType();
        while (type != null)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (property.GetIndexParameters().Length == 0 && expectedType.IsAssignableFrom(property.PropertyType))
                {
                    try
                    {
                        object? value = property.GetValue(mainMenuController);
                        if (value != null)
                        {
                            return value;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (expectedType.IsAssignableFrom(field.FieldType))
                {
                    try
                    {
                        object? value = field.GetValue(mainMenuController);
                        if (value != null)
                        {
                            return value;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            type = type.BaseType;
        }

        return null;
    }

    private static string FormatConstructorCandidates(ConstructorInfo[] constructors)
    {
        if (constructors.Length == 0)
        {
            return "<none>";
        }

        return string.Join("|", constructors.Select(FormatConstructorSignature).Take(12));
    }

    private static string FormatConstructorSignature(ConstructorInfo constructor)
    {
        return "(" + string.Join(",", constructor.GetParameters().Select(parameter => FormatTypeName(parameter.ParameterType))) + ")";
    }

    private static string FormatTypeName(Type? type)
    {
        return type == null ? "<null>" : (type.FullName ?? type.Name).Replace(';', ',');
    }

    private static void RestorePlayerItemUiContext(object mainMenuController, string source = "unspecified")
    {
        try
        {
            object? session = ResolveMember(mainMenuController, "ISession");
            object? profile = session == null ? null : ResolveMember(session, "Profile");
            object? inventoryController = ResolveMember(mainMenuController, "InventoryController");
            object? healthController = ResolveMember(mainMenuController, "HealthControllerClass") ?? ResolveMember(mainMenuController, "HealthController");
            object? questController = ResolveMember(mainMenuController, "LocalQuestControllerClass");
            if (session == null || profile == null || inventoryController == null || healthController == null)
            {
                VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", "player_item_ui_context_restore_skipped reason=missing_player_inputs");
                return;
            }

            ConfigureItemUiContext(mainMenuController, session, profile, inventoryController, healthController, questController, "player_restore");
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"player_item_ui_context_restore_completed source={source}");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"player_item_ui_context_restore_failed reason={exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void ConfigureItemUiContext(object mainMenuController, object? session, object profile, object inventoryController, object healthController, object? questController, string stage)
    {
        try
        {
            object? itemUiContext = ResolveItemUiContextInstance();
            if (itemUiContext == null || session == null)
            {
                VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"item_ui_context_configure_skipped stage={stage}; reason=context_or_session_not_found");
                return;
            }

            object? insurance = ResolveMember(session, "InsuranceCompany");
            object? contextType = ResolveEnumValue("EItemUiContextType", "InventoryScreen");
            object? cursorResult = ResolveEnumValue("ECursorResult", "ShowCursor");
            MethodInfo? configure = itemUiContext.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => string.Equals(method.Name, "Configure", StringComparison.Ordinal))
                .OrderByDescending(method => method.GetParameters().Length)
                .FirstOrDefault(method => method.GetParameters().Length >= 8);
            if (configure == null)
            {
                VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"item_ui_context_configure_skipped stage={stage}; reason=configure_not_found");
                return;
            }

            ParameterInfo[] parameters = configure.GetParameters();
            object?[] args = new object?[parameters.Length];
            if (args.Length > 0) args[0] = inventoryController;
            if (args.Length > 1) args[1] = profile;
            if (args.Length > 2) args[2] = session;
            if (args.Length > 3) args[3] = insurance;
            if (args.Length > 4) args[4] = null;
            if (args.Length > 5) args[5] = healthController;
            if (args.Length > 6) args[6] = null;
            if (args.Length > 7) args[7] = contextType;
            if (args.Length > 8) args[8] = cursorResult;
            if (args.Length > 9) args[9] = null;
            if (args.Length > 10) args[10] = null;
            if (args.Length > 11) args[11] = questController;

            configure.Invoke(itemUiContext, args);
            if (string.Equals(stage, "operator", StringComparison.OrdinalIgnoreCase))
            {
                activeOperatorItemUiContextOwner = mainMenuController;
                operatorItemUiContextActive = true;
            }
            else if (string.Equals(stage, "player_restore", StringComparison.OrdinalIgnoreCase))
            {
                activeOperatorItemUiContextOwner = null;
                operatorItemUiContextActive = false;
            }

            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"item_ui_context_configured stage={stage}; args={args.Length}; operatorContextActive={operatorItemUiContextActive}");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"item_ui_context_configure_failed stage={stage}; reason={exception.GetType().Name}: {exception.Message}");
        }
    }

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

    private static Type? ResolveInventoryScreenControllerType()
    {
        return ResolveTypeByName("EFT.UI.Screens.InventoryScreen+GClass3872")
            ?? ResolveTypeByName("InventoryScreen+GClass3872")
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeGetTypes)
                .FirstOrDefault(type => string.Equals(type.Name, "GClass3872", StringComparison.Ordinal)
                    && string.Equals(type.DeclaringType?.Name, "InventoryScreen", StringComparison.Ordinal));
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

    private static string CompactStack(Exception exception)
    {
        string? stack = exception.StackTrace;
        if (string.IsNullOrWhiteSpace(stack))
        {
            return "<none>";
        }

        string firstLine = stack.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? stack;
        return firstLine.Replace(';', ',');
    }

    private static object? ResolveMember(object target, string name)
    {
        return VanguardOperatorInventoryProfileLoader.ResolveMember(target, name);
    }
#endif
}
