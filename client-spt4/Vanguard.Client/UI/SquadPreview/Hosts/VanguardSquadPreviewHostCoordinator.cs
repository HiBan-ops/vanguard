using System;
using System.Collections.Generic;
using System.Reflection;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.UI.OffRaid.Inventory;

#if SPT_CLIENT
using HarmonyLib;
#endif

// Responsibility: binds the current true Main Menu lifecycle to exactly one host adapter and the shared squad reconciler.
// Flow: MainMenuControllerClass is captured at EFT's native menu-entry boundary; MenuScreen show/unminimize acquires the vanilla render shell
// when no external visual host owns the scene, then hands its Operator root and the live Player InventoryController to the reconciler.
// Authority boundary: lifecycle orchestration only. External host ownership wins; Player InventoryController remains EFT-owned and read-only here.
// Invariant: no second Vanguard host is created for a recognized external owner; host failure disables preview without mutating persistent/UI authority.
namespace Vanguard.Client.UI.SquadPreview.Hosts;

internal static class VanguardSquadPreviewHostCoordinator
{
    private static readonly object Gate = new();
    private static readonly VanguardSquadPreviewController Controller = new();
    private static readonly VanguardVanillaSquadPreviewHost VanillaHost = new();
    private static readonly VanguardMenuOverhaulSquadPreviewHost MenuOverhaulHost = new();
    private static readonly VanguardRaidLoadingSquadPreviewHost RaidLoadingHost = new();
    private static object? mainMenuController;
    private static readonly HashSet<string> PresentationSuppressions = new(StringComparer.Ordinal);
    private static readonly TimeSpan ResidentProbeInterval = TimeSpan.FromMilliseconds(200);
    private static DateTimeOffset nextResidentProbeUtc = DateTimeOffset.MinValue;
    private static bool shutdown;

    public static void OnMainMenuControllerAvailable(object? controller, string source)
    {
#if SPT_CLIENT
        if (controller == null)
        {
            return;
        }

        lock (Gate)
        {
            if (shutdown)
            {
                return;
            }

            mainMenuController = controller;
        }

        VanguardMenuOverhaulHostProbe.Probe("controller_available_" + Safe(source));

        VanguardClientDiagnosticsLog.Diagnostic(
            VanguardVanillaSquadPreviewHost.HostResolvedTag,
            () => $"hostKind=vanilla; state=controller_captured; controller={Safe(controller.GetType().FullName)}; source={Safe(source)}; mutation=false");
#else
        _ = controller;
        _ = source;
#endif
    }

    public static void OnMenuShown(object? menuScreen, string source)
    {
#if SPT_CLIENT
        if (menuScreen == null)
        {
            return;
        }

        lock (Gate)
        {
            if (shutdown)
            {
                return;
            }
        }

        // A real MenuScreen return before BattleUI means raid loading was cancelled/aborted
        // or the player returned from raid. Never leave the Vanguard-owned transition shell
        // alive once ordinary menu presentation resumes.
        if (RaidLoadingHost.IsAvailable)
        {
            Controller.Release("raid_loading_ended_menu_return_" + Safe(source));
            RaidLoadingHost.Release("raid_loading_ended_menu_return_" + Safe(source));
        }

        ClearFallbackPresentationSuppressions("menu_shown_" + Safe(source));
        VanguardMenuOverhaulHostProbe.Probe("menu_shown_" + Safe(source));

        if (VanguardMainMenuInterop.Has(VanguardMainMenuInteropCapability.ExternalVisualStyleAuthority))
        {
            object? externalInventoryController = ResolvePlayerInventoryController();
            if (externalInventoryController == null)
            {
                Release("menu_overhaul_inventory_controller_missing_" + Safe(source));
                VanguardClientDiagnosticsLog.Warning(
                    VanguardVanillaSquadPreviewHost.HostResolvedTag,
                    $"hostKind=menu_overhaul; state=failed_closed; reason=player_inventory_controller_missing; source={Safe(source)}; mutation=false");
                return;
            }

            if (MenuOverhaulHost.TryAcquire(menuScreen, out string menuOverhaulReason)
                && MenuOverhaulHost.IsAvailable
                && MenuOverhaulHost.RootTransform != null)
            {
                if (VanillaHost.IsAvailable)
                {
                    Controller.Release("transition_vanilla_to_menu_overhaul");
                    VanillaHost.Release("transition_vanilla_to_menu_overhaul");
                }

                if (!MenuOverhaulHost.BorrowedPreviewActive)
                {
                    Controller.SetReconciliationPaused(true, "mo_preview_hidden_" + Safe(source));
                    MenuOverhaulHost.SetPresentationVisible(false, "mo_preview_hidden_" + Safe(source));
                    VanguardClientDiagnosticsLog.Diagnostic(
                        VanguardVanillaSquadPreviewHost.HostResolvedTag,
                        () => $"hostKind=menu_overhaul; state=resident_hidden; reason={Safe(menuOverhaulReason)}; source={Safe(source)}; loaded={Controller.LoadedInstanceCount}; bodiesPreserved=true; nativePmcTouched=false; thirdPartyCameraTouched=false; mutation=vanguard_owned_only");
                    return;
                }

                if (OperatorInventoryLifecycleBusy(out string operatorLifecycleReason))
                {
                    Controller.SetReconciliationPaused(true, "operator_lifecycle_settling_" + Safe(source));
                    MenuOverhaulHost.SetPresentationVisible(false, "operator_lifecycle_settling_" + Safe(source));
                    VanguardClientDiagnosticsLog.Diagnostic(
                        VanguardVanillaSquadPreviewHost.HostResolvedTag,
                        () => $"hostKind=menu_overhaul; state=resident_waiting_for_operator_lifecycle; reason={Safe(operatorLifecycleReason)}; source={Safe(source)}; loaded={Controller.LoadedInstanceCount}; bodiesPreserved=true; mutation=false");
                    return;
                }

                bool menuOverhaulAcquired = Controller.IsBoundTo(
                    MenuOverhaulHost.RootTransform,
                    externalInventoryController,
                    MenuOverhaulHost.HostKind)
                    || Controller.Acquire(
                        MenuOverhaulHost.RootTransform,
                        externalInventoryController,
                        MenuOverhaulHost.HostKind);
                Controller.SetReconciliationPaused(false, "mo_preview_active_" + Safe(source));
                MenuOverhaulHost.SetPresentationVisible(!HasPresentationSuppression(), "menu_host_bound_" + Safe(source));
                VanguardClientDiagnosticsLog.Diagnostic(
                    VanguardVanillaSquadPreviewHost.HostResolvedTag,
                    () => $"hostKind=menu_overhaul; state={(menuOverhaulAcquired ? "bound_resident" : "controller_rejected")}; reason={Safe(menuOverhaulReason)}; source={Safe(source)}; loaded={Controller.LoadedInstanceCount}; bodiesPreserved=true; nativePmcTouched=false; thirdPartyCameraTouched=false; mutation=vanguard_owned_only");
                return;
            }

            Release("external_visual_host_unavailable_" + Safe(source));
            VanguardClientDiagnosticsLog.Warning(
                VanguardVanillaSquadPreviewHost.HostResolvedTag,
                $"hostKind=menu_overhaul; state=failed_closed; reason={Safe(menuOverhaulReason)}; owners={Safe(VanguardMainMenuInterop.DescribeActiveOwners(VanguardMainMenuInteropCapability.ExternalVisualStyleAuthority))}; source={Safe(source)}; mutation=false");
            return;
        }

        if (MenuOverhaulHost.IsAvailable)
        {
            Controller.Release("transition_menu_overhaul_to_vanilla");
            MenuOverhaulHost.Release("transition_menu_overhaul_to_vanilla");
        }

        object? inventoryController = ResolvePlayerInventoryController();
        if (inventoryController == null)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardVanillaSquadPreviewHost.HostResolvedTag,
                $"hostKind=vanilla; state=failed_closed; reason=player_inventory_controller_missing; controllerCaptured={HasCapturedController()}; source={Safe(source)}; mutation=false");
            return;
        }

        if (!VanillaHost.TryAcquire(menuScreen, out string hostReason)
            || !VanillaHost.IsAvailable
            || VanillaHost.RootTransform == null)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardVanillaSquadPreviewHost.HostResolvedTag,
                $"hostKind=vanilla; state=failed_closed; reason={Safe(hostReason)}; source={Safe(source)}; mutation=false");
            return;
        }

        bool acquired = Controller.Acquire(VanillaHost.RootTransform, inventoryController, VanillaHost.HostKind);
        VanillaHost.SetPresentationVisible(!HasPresentationSuppression(), "menu_host_bound_" + Safe(source));
        VanguardClientDiagnosticsLog.Diagnostic(
            VanguardVanillaSquadPreviewHost.HostResolvedTag,
            () => $"hostKind=vanilla; state={(acquired ? "bound" : "controller_rejected")}; reason={Safe(hostReason)}; source={Safe(source)}; loaded={Controller.LoadedInstanceCount}; mutation=false");
#else
        _ = menuScreen;
        _ = source;
#endif
    }

    public static void OnMenuMinimized(object? menuScreen, bool minimized, string source)
    {
#if SPT_CLIENT
        VanguardMenuOverhaulHostProbe.Probe((minimized ? "menu_minimized_" : "menu_unminimized_") + Safe(source));
        if (minimized)
        {
            if (VanguardMainMenuInterop.Has(VanguardMainMenuInteropCapability.ExternalVisualStyleAuthority)
                && MenuOverhaulHost.IsAvailable)
            {
                Controller.SetReconciliationPaused(true, "menu_overhaul_minimized_" + Safe(source));
                MenuOverhaulHost.SuspendResidentPresentation("menu_minimized_" + Safe(source));
                return;
            }

            Release("menu_minimized_" + Safe(source));
            return;
        }

        OnMenuShown(menuScreen, source);
#else
        _ = menuScreen;
        _ = minimized;
        _ = source;
#endif
    }

    public static void BeginRaidLoadingPresentation(string source)
    {
#if SPT_CLIENT
        lock (Gate)
        {
            if (shutdown)
            {
                return;
            }
        }

        // Vanilla already owns a qualified loading presentation path. The transition shell exists only
        // to bridge the gap created by Menu Overhaul intentionally disabling its borrowed
        // Main Menu preview when raid loading begins.
        if (!VanguardMainMenuInterop.Has(VanguardMainMenuInteropCapability.ExternalVisualStyleAuthority))
        {
            VanguardClientDiagnosticsLog.Diagnostic(
                "SQUAD_PREVIEW_RAID_LOADING",
                () => $"state=ignored; reason=no_external_visual_style_authority; source={Safe(source)}; mutation=false");
            return;
        }

        object? inventoryController = ResolvePlayerInventoryController();
        if (inventoryController == null)
        {
            VanguardClientDiagnosticsLog.Warning(
                "SQUAD_PREVIEW_RAID_LOADING",
                $"state=failed_closed; reason=player_inventory_controller_missing; source={Safe(source)}; mutation=false");
            return;
        }

        lock (Gate)
        {
            PresentationSuppressions.Clear();
            nextResidentProbeUtc = DateTimeOffset.MinValue;
        }

        // End only Vanguard's borrowed-MO projection. Menu Overhaul's clone/PMC/camera/lights
        // remain untouched and are free to follow their normal loading lifecycle.
        Controller.Release("transition_menu_overhaul_to_raid_loading");
        MenuOverhaulHost.Release("transition_menu_overhaul_to_raid_loading");
        VanillaHost.Release("transition_menu_overhaul_to_raid_loading");

        if (!RaidLoadingHost.TryAcquire(out string hostReason)
            || !RaidLoadingHost.IsAvailable
            || RaidLoadingHost.RootTransform == null)
        {
            VanguardClientDiagnosticsLog.Warning(
                "SQUAD_PREVIEW_RAID_LOADING",
                $"state=failed_closed; reason={Safe(hostReason)}; source={Safe(source)}; nativePmcTouched=false; thirdPartyCameraTouched=false; mutation=vanguard_owned_only");
            return;
        }

        bool acquired = Controller.Acquire(
            RaidLoadingHost.RootTransform,
            inventoryController,
            RaidLoadingHost.HostKind);

        VanguardClientDiagnosticsLog.Diagnostic(
            "SQUAD_PREVIEW_RAID_LOADING",
            () => $"state={(acquired ? "active" : "controller_rejected")}; hostKind={RaidLoadingHost.HostKind}; source={Safe(source)}; loaded={Controller.LoadedInstanceCount}; nativePmcTouched=false; thirdPartyCameraTouched=false; thirdPartyLightsTouched=false; cleanupBoundary=effective_battle_ui; mutation=vanguard_owned_only");
#else
        _ = source;
#endif
    }

    public static void TickOffRaidPresentation(string source)
    {
#if SPT_CLIENT
        if (!VanguardMainMenuInterop.Has(VanguardMainMenuInteropCapability.ExternalVisualStyleAuthority)
            || RaidLoadingHost.IsAvailable
            || !MenuOverhaulHost.IsAvailable)
        {
            return;
        }

        object? capturedController;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (Gate)
        {
            if (shutdown || now < nextResidentProbeUtc)
            {
                return;
            }

            nextResidentProbeUtc = now + ResidentProbeInterval;
            capturedController = mainMenuController;
        }

        if (capturedController == null)
        {
            return;
        }

        if (!MenuOverhaulHost.TryAcquire(capturedController, out string hostReason)
            || !MenuOverhaulHost.IsAvailable
            || MenuOverhaulHost.RootTransform == null)
        {
            return;
        }

        if (!MenuOverhaulHost.BorrowedPreviewActive)
        {
            Controller.SetReconciliationPaused(true, "resident_tick_mo_preview_hidden_" + Safe(source));
            MenuOverhaulHost.SetPresentationVisible(false, "resident_tick_mo_preview_hidden_" + Safe(source));
            return;
        }

        if (OperatorInventoryLifecycleBusy(out _))
        {
            Controller.SetReconciliationPaused(true, "resident_tick_operator_lifecycle_" + Safe(source));
            MenuOverhaulHost.SetPresentationVisible(false, "resident_tick_operator_lifecycle_" + Safe(source));
            return;
        }

        object? inventoryController = ResolvePlayerInventoryController();
        if (inventoryController == null)
        {
            return;
        }

        bool alreadyBound = Controller.IsBoundTo(
            MenuOverhaulHost.RootTransform,
            inventoryController,
            MenuOverhaulHost.HostKind);

        if (!alreadyBound)
        {
            Controller.Acquire(
                MenuOverhaulHost.RootTransform,
                inventoryController,
                MenuOverhaulHost.HostKind);
        }

        Controller.SetReconciliationPaused(false, "resident_tick_mo_preview_active_" + Safe(source));
        MenuOverhaulHost.SetPresentationVisible(!HasPresentationSuppression(), "resident_tick_mo_preview_active_" + Safe(source));
#else
        _ = source;
#endif
    }

    public static void AcquirePresentationSuppression(string token, string source)
    {
#if SPT_CLIENT
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        int count;
        lock (Gate)
        {
            if (shutdown)
            {
                return;
            }

            PresentationSuppressions.Add(token);
            count = PresentationSuppressions.Count;
        }

        VanillaHost.SetPresentationVisible(false, "suppressed_" + Safe(source));
        MenuOverhaulHost.SetPresentationVisible(false, "suppressed_" + Safe(source));
        VanguardClientDiagnosticsLog.Diagnostic(
            "SQUAD_PREVIEW_PRESENTATION",
            () => $"hostKind={CurrentPresentationHostKind()}; state=suppressed; source={Safe(source)}; leaseCount={count}; bodiesResident={Controller.LoadedInstanceCount}; mutation=false");
#else
        _ = token;
        _ = source;
#endif
    }

    public static void ReleasePresentationSuppression(string token, string source)
    {
#if SPT_CLIENT
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        int count;
        lock (Gate)
        {
            PresentationSuppressions.Remove(token);
            count = PresentationSuppressions.Count;
        }

        if (count == 0)
        {
            VanillaHost.SetPresentationVisible(true, "restored_" + Safe(source));
            MenuOverhaulHost.SetPresentationVisible(true, "restored_" + Safe(source));
        }

        VanguardClientDiagnosticsLog.Diagnostic(
            "SQUAD_PREVIEW_PRESENTATION",
            () => $"hostKind={CurrentPresentationHostKind()}; state={(count == 0 ? "restored" : "suppressed")}; source={Safe(source)}; leaseCount={count}; bodiesResident={Controller.LoadedInstanceCount}; mutation=false");
#else
        _ = token;
        _ = source;
#endif
    }

    private static void ClearFallbackPresentationSuppressions(string source)
    {
#if SPT_CLIENT
        int removed;
        int remaining;
        lock (Gate)
        {
            removed = PresentationSuppressions.RemoveWhere(token =>
                token.StartsWith("weapon_editor_fallback_", StringComparison.Ordinal)
                || string.Equals(token, "weapon_modding_controller", StringComparison.Ordinal));
            remaining = PresentationSuppressions.Count;
        }

        if (removed > 0 && remaining == 0)
        {
            VanillaHost.SetPresentationVisible(true, "fallback_restore_" + Safe(source));
            MenuOverhaulHost.SetPresentationVisible(true, "fallback_restore_" + Safe(source));
            VanguardClientDiagnosticsLog.Diagnostic(
                "SQUAD_PREVIEW_PRESENTATION",
                () => $"hostKind={CurrentPresentationHostKind()}; state=restored_fallback; source={Safe(source)}; clearedLeases={removed}; bodiesResident={Controller.LoadedInstanceCount}; mutation=false");
        }
#else
        _ = source;
#endif
    }

    public static void ClearPresentationSuppressions(string source)
    {
#if SPT_CLIENT
        int cleared;
        lock (Gate)
        {
            cleared = PresentationSuppressions.Count;
            PresentationSuppressions.Clear();
        }

        VanillaHost.SetPresentationVisible(true, "fallback_restore_" + Safe(source));
        MenuOverhaulHost.SetPresentationVisible(true, "fallback_restore_" + Safe(source));
        if (cleared > 0)
        {
            VanguardClientDiagnosticsLog.Diagnostic(
                "SQUAD_PREVIEW_PRESENTATION",
                () => $"hostKind={CurrentPresentationHostKind()}; state=restored_fallback; source={Safe(source)}; clearedLeases={cleared}; bodiesResident={Controller.LoadedInstanceCount}; mutation=false");
        }
#else
        _ = source;
#endif
    }

    public static void Release(string reason)
    {
#if SPT_CLIENT
        lock (Gate)
        {
            PresentationSuppressions.Clear();
        }
        Controller.Release(reason);
        VanillaHost.Release(reason);
        MenuOverhaulHost.Release(reason);
        RaidLoadingHost.Release(reason);
#else
        _ = reason;
#endif
    }

    public static void Shutdown(string reason)
    {
        lock (Gate)
        {
            if (shutdown)
            {
                return;
            }
            shutdown = true;
            mainMenuController = null;
            PresentationSuppressions.Clear();
        }

        Controller.Dispose();
        VanillaHost.Release(reason);
        MenuOverhaulHost.Release(reason);
        RaidLoadingHost.Release(reason);
    }

#if SPT_CLIENT
    private static bool HasPresentationSuppression()
    {
        lock (Gate)
        {
            return PresentationSuppressions.Count > 0;
        }
    }

    private static string CurrentPresentationHostKind()
    {
        if (RaidLoadingHost.IsAvailable)
        {
            return RaidLoadingHost.HostKind;
        }
        if (MenuOverhaulHost.IsAvailable)
        {
            return MenuOverhaulHost.HostKind;
        }
        if (VanillaHost.IsAvailable)
        {
            return VanillaHost.HostKind;
        }
        return "none";
    }

    private static bool HasCapturedController()
    {
        lock (Gate)
        {
            return mainMenuController != null;
        }
    }

    private static bool OperatorInventoryLifecycleBusy(out string reason)
    {
        if (VanguardOperatorDirectInventoryLifecycle.IsBusy)
        {
            reason = VanguardOperatorDirectInventoryLifecycle.BusyReason;
            return true;
        }

        if (VanguardOperatorInventorySessionIndicator.IsReconciliationInProgress)
        {
            reason = "operator_session_indicator_reconciliation";
            return true;
        }

        reason = "ready";
        return false;
    }

    private static object? ResolvePlayerInventoryController()
    {
        object? controller;
        lock (Gate)
        {
            controller = mainMenuController;
        }

        if (controller == null)
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        PropertyInfo? property = controller.GetType().GetProperty("InventoryController", flags);
        if (property != null)
        {
            return property.GetValue(controller);
        }

        FieldInfo? field = controller.GetType().GetField("Gclass3388_0", flags)
            ?? controller.GetType().GetField("InventoryController", flags);
        return field?.GetValue(controller);
    }
#endif

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
