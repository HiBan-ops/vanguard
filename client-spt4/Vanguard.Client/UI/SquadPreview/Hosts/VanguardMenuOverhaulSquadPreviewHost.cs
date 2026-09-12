using System;
using System.Collections.Generic;
using System.Reflection;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using BepInEx.Bootstrap;
using UnityEngine;
#endif

// Responsibility: borrows Menu Overhaul's qualified main-menu preview scene while owning only a Vanguard Operator anchor.
// Flow: resolve Menu Overhaul's stable cloned PlayerModelView, attach a sibling anchor under PlayerMVObject at the native MenuPlayer origin,
// and let the shared Vanguard reconciler load 1..4 Operators into that anchor while MO keeps its PMC/camera/lights/viewport lifecycle.
// Authority boundary: Menu Overhaul owns the clone, PMC, PlayerModelView, Camera_inventory, CameraImage, lights and refresh behavior.
// Vanguard owns only VanguardMenuOverhaulSquadHostRoot and its Operator descendants.
// Invariant: Release destroys only Vanguard-owned objects; the MO PMC is never parented under, rotated by, closed by or destroyed by Vanguard.
namespace Vanguard.Client.UI.SquadPreview.Hosts;

internal sealed class VanguardMenuOverhaulSquadPreviewHost : IVanguardSquadPreviewHost
{
    private const string RootName = "VanguardMenuOverhaulSquadHostRoot";
    private const string PlayerMvObjectName = "PlayerMVObject";
    private const string MenuPlayerName = "MenuPlayer";
    private static readonly Vector3 MenuLocalOrigin = new(0f, -1.1f, 5.95f);
    private static readonly Quaternion MenuFacingRotation = Quaternion.Euler(0f, 180f, 0f);

#if SPT_CLIENT
    private GameObject? ownerClone;
    private GameObject? rootObject;
    private Transform? playerMvObject;
    private Camera? inventoryCamera;
    private bool borrowedPreviewActive;
    private bool presentationVisible = true;
    private readonly Dictionary<int, bool> rendererStates = new();
#endif

    public bool IsAvailable
    {
        get
        {
#if SPT_CLIENT
            return ownerClone != null && rootObject != null && playerMvObject != null && inventoryCamera != null;
#else
            return false;
#endif
        }
    }

    public string HostKind => "menu_overhaul";

#if SPT_CLIENT
    public Transform? RootTransform => rootObject?.transform;
    public Camera? Camera => inventoryCamera;
    public bool BorrowedPreviewActive => borrowedPreviewActive;

    public bool TryAcquire(object menuScreen, out string reason)
    {
        _ = menuScreen;
        try
        {
            if (!Chainloader.PluginInfos.TryGetValue(VanguardMainMenuInterop.PreferredLoadOrderPluginGuid, out var pluginInfo)
                || pluginInfo?.Instance == null)
            {
                reason = "menu_overhaul_plugin_missing";
                return false;
            }

            Assembly assembly = pluginInfo.Instance.GetType().Assembly;
            Type? serviceType = ResolveType(
                assembly,
                "MoxoPixel.MenuOverhaul.Helpers.Services.PlayerProfileViewService",
                "PlayerProfileViewService");
            if (serviceType == null)
            {
                reason = "player_profile_view_service_missing";
                return false;
            }

            object? cloneValue = ReadStaticMember(serviceType, "ClonedPlayerModelView");
            GameObject? clone = cloneValue as GameObject ?? (cloneValue as Component)?.gameObject;
            if (clone == null)
            {
                if (rootObject != null)
                {
                    SuspendResidentPresentation("cloned_player_model_view_temporarily_missing");
                    reason = "resident_hidden_waiting_for_mo_clone";
                    return true;
                }

                reason = "cloned_player_model_view_missing";
                return false;
            }

            Transform? playerRoot = clone.transform.Find(PlayerMvObjectName) ?? FindDescendantByName(clone.transform, PlayerMvObjectName);
            if (playerRoot == null)
            {
                if (rootObject != null)
                {
                    SuspendResidentPresentation("player_mv_object_temporarily_missing");
                    ownerClone = clone;
                    reason = "resident_hidden_waiting_for_player_mv_object";
                    return true;
                }

                reason = "player_mv_object_missing";
                return false;
            }

            Camera? camera = FindInventoryCamera(clone.GetComponentsInChildren<Camera>(true));
            if (camera == null)
            {
                if (rootObject != null)
                {
                    SuspendResidentPresentation("inventory_camera_temporarily_missing");
                    ownerClone = clone;
                    playerMvObject = playerRoot;
                    reason = "resident_hidden_waiting_for_inventory_camera";
                    return true;
                }

                reason = "inventory_camera_missing";
                return false;
            }

            bool cloneActive = clone.activeInHierarchy;
            bool cameraActive = camera.gameObject.activeInHierarchy;
            bool cameraEnabled = camera.enabled;
            bool nativePreviewActive = cloneActive && cameraActive && cameraEnabled;
            bool wasBorrowedPreviewActive = borrowedPreviewActive;

            if (rootObject == null)
            {
                if (!nativePreviewActive)
                {
                    reason = $"mo_preview_inactive_clone_{cloneActive}_cameraActive_{cameraActive}_cameraEnabled_{cameraEnabled}";
                    string inactivePreviewReason = reason;
                    VanguardClientDiagnosticsLog.Diagnostic(
                        VanguardVanillaSquadPreviewHost.HostResolvedTag,
                        () => $"hostKind=menu_overhaul; state=waiting_for_initial_active_preview; cloneActive={cloneActive}; cameraActive={cameraActive}; cameraEnabled={cameraEnabled}; reason={Safe(inactivePreviewReason)}; nativePmcTouched=false; thirdPartyCameraTouched=false; mutation=false");
                    return false;
                }

                int previewLayer = LayerMask.NameToLayer("Weapon Preview");
                if (previewLayer < 0 || previewLayer > 31)
                {
                    reason = "weapon_preview_layer_missing";
                    return false;
                }

                GameObject anchor = new(RootName) { hideFlags = HideFlags.DontSave };
                anchor.layer = previewLayer;
                UnityEngine.Object.DontDestroyOnLoad(anchor);
                rootObject = anchor;
                presentationVisible = true;
                rendererStates.Clear();
            }

            ownerClone = clone;
            playerMvObject = playerRoot;
            inventoryCamera = camera;

            if (!nativePreviewActive)
            {
                SuspendResidentPresentation($"mo_preview_inactive_clone_{cloneActive}_cameraActive_{cameraActive}_cameraEnabled_{cameraEnabled}");
                reason = "resident_hidden_waiting_for_active_mo_preview";
                return true;
            }

            SyncDetachedAnchorToMenuPreview(playerRoot);
            borrowedPreviewActive = true;

            Transform? menuPlayer = playerRoot.Find(MenuPlayerName);
            Light[] lights = clone.GetComponentsInChildren<Light>(true);
            reason = "acquired_resident_detached_mo_scene_vanguard_anchor_only";
            if (!wasBorrowedPreviewActive)
            {
                VanguardClientDiagnosticsLog.Info(
                    VanguardVanillaSquadPreviewHost.HostResolvedTag,
                    $"hostKind=menu_overhaul; state=active_resident; ownership=borrow_clone_camera_lights_detached_vanguard_anchor; cloneId={clone.GetInstanceID()}; playerMvObjectId={playerRoot.GetInstanceID()}; menuPlayerId={(menuPlayer != null ? menuPlayer.GetInstanceID() : 0)}; anchorId={rootObject.GetInstanceID()}; anchorParent={Safe(rootObject.transform.parent?.name)}; anchorWorld={FormatVector(rootObject.transform.position)}; facingYaw=180; stableDepth=5.95; cameraId={camera.GetInstanceID()}; cameraMask=0x{unchecked((uint)camera.cullingMask):X8}; lights={lights.Length}; nativePmcTouched=false; thirdPartyCameraTouched=false; thirdPartyLightsTouched=false; bodiesPreserved=true; mutation=vanguard_owned_only");
            }
            return true;
        }
        catch (Exception exception)
        {
            reason = "menu_overhaul_host_acquire_failed_" + exception.GetType().Name;
            VanguardClientDiagnosticsLog.Warning(
                VanguardVanillaSquadPreviewHost.HostResolvedTag,
                $"hostKind=menu_overhaul; state=failed_closed; reason={Safe(reason)}; detail={Safe(exception.Message)}; nativePmcTouched=false; mutation=vanguard_owned_only");
            Release("acquire_exception");
            return false;
        }
    }
#else
    public object? RootTransform => null;
    public object? Camera => null;
    public bool TryAcquire(object menuScreen, out string reason)
    {
        reason = "spt_client_not_defined";
        return false;
    }
#endif

    public void Release(string reason)
    {
#if SPT_CLIENT
        GameObject? anchor = rootObject;
        int cloneId = ownerClone != null ? ownerClone.GetInstanceID() : 0;
        rootObject = null;
        ownerClone = null;
        playerMvObject = null;
        inventoryCamera = null;
        borrowedPreviewActive = false;
        presentationVisible = true;
        rendererStates.Clear();

        if (anchor != null)
        {
            UnityEngine.Object.Destroy(anchor);
            VanguardClientDiagnosticsLog.Diagnostic(
                VanguardSquadPreviewController.HostReleasedTag,
                () => $"hostKind=menu_overhaul; hostRootDestroyed=true; borrowedCloneId={cloneId}; nativePmcTouched=false; cameraDestroyed=false; lightsDestroyed=false; playerModelViewClosed=false; reason={Safe(reason)}; mutation=vanguard_owned_only");
        }
#else
        _ = reason;
#endif
    }

#if SPT_CLIENT
    public void SuspendResidentPresentation(string reason)
    {
        borrowedPreviewActive = false;
        SetPresentationVisible(false, "resident_hidden_" + Safe(reason));
        VanguardClientDiagnosticsLog.Diagnostic(
            VanguardVanillaSquadPreviewHost.HostResolvedTag,
            () => $"hostKind=menu_overhaul; state=resident_hidden; reason={Safe(reason)}; anchorActive={rootObject != null && rootObject.activeInHierarchy}; bodiesPreserved=true; nativePmcTouched=false; thirdPartyCameraTouched=false; mutation=vanguard_owned_only");
    }

    private void SyncDetachedAnchorToMenuPreview(Transform playerRoot)
    {
        if (rootObject == null)
        {
            return;
        }

        Transform anchor = rootObject.transform;
        if (anchor.parent != null)
        {
            anchor.SetParent(null, true);
            UnityEngine.Object.DontDestroyOnLoad(rootObject);
        }

        anchor.position = playerRoot.TransformPoint(MenuLocalOrigin);
        anchor.rotation = playerRoot.rotation * MenuFacingRotation;
        anchor.localScale = playerRoot.lossyScale;
    }

    public void SetPresentationVisible(bool visible, string reason)
    {
        if (rootObject == null)
        {
            return;
        }

        if (presentationVisible == visible)
        {
            return;
        }

        Renderer[] renderers = rootObject.GetComponentsInChildren<Renderer>(true);
        if (!visible)
        {
            rendererStates.Clear();
            for (int index = 0; index < renderers.Length; index++)
            {
                Renderer renderer = renderers[index];
                if (renderer == null)
                {
                    continue;
                }

                rendererStates[renderer.GetInstanceID()] = renderer.enabled;
                renderer.enabled = false;
            }
        }
        else
        {
            for (int index = 0; index < renderers.Length; index++)
            {
                Renderer renderer = renderers[index];
                if (renderer == null)
                {
                    continue;
                }

                if (rendererStates.TryGetValue(renderer.GetInstanceID(), out bool wasEnabled))
                {
                    renderer.enabled = wasEnabled;
                }
            }
            rendererStates.Clear();
        }

        presentationVisible = visible;
        VanguardClientDiagnosticsLog.Diagnostic(
            "SQUAD_PREVIEW_PRESENTATION",
            () => $"hostKind=menu_overhaul; visible={visible}; reason={Safe(reason)}; renderers={renderers.Length}; bodiesActive={rootObject.activeInHierarchy}; nativePmcTouched=false; thirdPartyCameraTouched=false; mutation=vanguard_owned_only");
    }

    private static Camera? FindInventoryCamera(IReadOnlyList<Camera> cameras)
    {
        Camera? fallback = null;
        for (int index = 0; index < cameras.Count; index++)
        {
            Camera camera = cameras[index];
            if (camera == null)
            {
                continue;
            }

            fallback ??= camera;
            if (string.Equals(camera.name, "Camera_inventory", StringComparison.OrdinalIgnoreCase))
            {
                return camera;
            }
        }
        return fallback;
    }

    private static Transform? FindDescendantByName(Transform root, string name)
    {
        for (int index = 0; index < root.childCount; index++)
        {
            Transform child = root.GetChild(index);
            if (string.Equals(child.name, name, StringComparison.Ordinal))
            {
                return child;
            }
            Transform? nested = FindDescendantByName(child, name);
            if (nested != null)
            {
                return nested;
            }
        }
        return null;
    }

    private static object? ReadStaticMember(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        PropertyInfo? property = type.GetProperty(name, flags);
        if (property != null && property.GetIndexParameters().Length == 0)
        {
            return property.GetValue(null);
        }
        return type.GetField(name, flags)?.GetValue(null);
    }

    private static Type? ResolveType(Assembly assembly, string fullName, string simpleName)
    {
        Type? exact = assembly.GetType(fullName, false, false);
        if (exact != null)
        {
            return exact;
        }

        try
        {
            Type[] types = assembly.GetTypes();
            for (int index = 0; index < types.Length; index++)
            {
                if (string.Equals(types[index].Name, simpleName, StringComparison.Ordinal))
                {
                    return types[index];
                }
            }
        }
        catch (ReflectionTypeLoadException exception)
        {
            Type?[] types = exception.Types;
            for (int index = 0; index < types.Length; index++)
            {
                Type? type = types[index];
                if (type != null && string.Equals(type.Name, simpleName, StringComparison.Ordinal))
                {
                    return type;
                }
            }
        }
        return null;
    }

    private static string FormatVector(Vector3 value) => $"{value.x:0.###}_{value.y:0.###}_{value.z:0.###}";
#endif

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
