using System;
using System.Collections.Generic;
using System.Reflection;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using BepInEx.Bootstrap;
using UnityEngine;
#endif

// Responsibility: observes Menu Overhaul's live main-menu preview ownership without mutating it.
// Flow: existing Vanguard main-menu lifecycle callbacks ask this probe for bounded snapshots before
// Vanguard's current external-owner fail-closed branch skips rendering.
// Authority boundary: read-only reflection only. Menu Overhaul retains ownership of its clone, PMC,
// camera, CameraImage, PlayerModelView, lighting and refresh lifecycle.
// Invariant: this probe never creates, reparents, enables/disables, rotates, closes, shows or destroys
// a third-party or EFT object. It exists only to verify the external host ownership boundary and compatibility.
namespace Vanguard.Client.UI.SquadPreview.Hosts;

internal static class VanguardMenuOverhaulHostProbe
{
    public const string ProbeTag = "SQUAD_PREVIEW_MO_HOST_PROBE";

#if SPT_CLIENT
    private const int MaxProbeLogs = 48;
    private static readonly object Gate = new();
    private static int sequence;
    private static int emitted;
    private static bool capLogged;
    private static int lastCloneId;
    private static int lastCameraId;
    private static int lastPmcTargetId;
    private static bool hasReadySnapshot;

    public static void Probe(string source)
    {
        try
        {
            if (!Chainloader.PluginInfos.TryGetValue(VanguardMainMenuInterop.PreferredLoadOrderPluginGuid, out var pluginInfo)
                || pluginInfo?.Instance == null)
            {
                WriteBounded($"state=plugin_missing; source={Safe(source)}; pluginGuid={Safe(VanguardMainMenuInterop.PreferredLoadOrderPluginGuid)}; mutation=false");
                return;
            }

            Assembly assembly = pluginInfo.Instance.GetType().Assembly;
            Type? viewServiceType = ResolveType(
                assembly,
                "MoxoPixel.MenuOverhaul.Helpers.Services.PlayerProfileViewService",
                "PlayerProfileViewService");
            if (viewServiceType == null)
            {
                WriteBounded(
                    $"state=service_type_missing; source={Safe(source)}; assembly={Safe(assembly.GetName().Name)}; version={Safe(assembly.GetName().Version?.ToString())}; mutation=false");
                return;
            }

            object? cloneValue = ReadStaticMember(viewServiceType, "ClonedPlayerModelView");
            GameObject? clone = cloneValue as GameObject ?? (cloneValue as Component)?.gameObject;
            if (clone == null)
            {
                WriteBounded(
                    $"state=clone_missing; source={Safe(source)}; assembly={Safe(assembly.GetName().Name)}; version={Safe(assembly.GetName().Version?.ToString())}; hadReadySnapshot={hasReadySnapshot}; mutation=false");
                return;
            }

            Camera[] cameras = clone.GetComponentsInChildren<Camera>(true);
            Camera? inventoryCamera = FindInventoryCamera(cameras);
            Component[] components = clone.GetComponentsInChildren<Component>(true);
            Component? playerModelView = FindComponentByTypeName(components, "PlayerModelView");
            Component? cameraImage = FindComponentByTypeName(components, "CameraImage");
            Light[] lights = clone.GetComponentsInChildren<Light>(true);
            Transform? pmcTarget = ResolveDragTarget(assembly);

            int cloneId = clone.GetInstanceID();
            int cameraId = inventoryCamera != null ? inventoryCamera.GetInstanceID() : 0;
            int pmcTargetId = pmcTarget != null ? pmcTarget.GetInstanceID() : 0;

            string stability;
            lock (Gate)
            {
                stability = !hasReadySnapshot
                    ? "initial"
                    : cloneId != lastCloneId
                        ? "clone_changed"
                        : cameraId != lastCameraId
                            ? "camera_changed"
                            : pmcTargetId != lastPmcTargetId
                                ? "pmc_target_changed"
                                : "stable";

                lastCloneId = cloneId;
                lastCameraId = cameraId;
                lastPmcTargetId = pmcTargetId;
                hasReadySnapshot = true;
            }

            string targetTexture = inventoryCamera?.targetTexture == null
                ? "none"
                : $"{inventoryCamera.targetTexture.width}x{inventoryCamera.targetTexture.height}";
            string cameraMask = inventoryCamera == null
                ? "none"
                : $"0x{unchecked((uint)inventoryCamera.cullingMask):X8}";

            WriteBounded(
                $"state=ready; stability={stability}; source={Safe(source)}; assembly={Safe(assembly.GetName().Name)}; version={Safe(assembly.GetName().Version?.ToString())}; "
                + $"cloneId={cloneId}; cloneName={Safe(clone.name)}; cloneActive={clone.activeInHierarchy}; cloneParent={Safe(clone.transform.parent?.name)}; scene={Safe(clone.scene.name)}; "
                + $"cameraCount={cameras.Length}; inventoryCameraId={cameraId}; inventoryCameraName={Safe(inventoryCamera?.name)}; cameraEnabled={inventoryCamera?.enabled}; cameraActive={inventoryCamera?.gameObject.activeInHierarchy}; cameraMask={cameraMask}; clearFlags={Safe(inventoryCamera?.clearFlags.ToString())}; targetTexture={targetTexture}; "
                + $"playerModelViewId={(playerModelView != null ? playerModelView.GetInstanceID() : 0)}; cameraImageId={(cameraImage != null ? cameraImage.GetInstanceID() : 0)}; "
                + $"pmcTargetId={pmcTargetId}; pmcTargetName={Safe(pmcTarget?.name)}; pmcTargetParent={Safe(pmcTarget?.parent?.name)}; "
                + $"lights={lights.Length}; lightNames={Safe(JoinNames(lights, 8))}; directChildren={clone.transform.childCount}; childNames={Safe(JoinDirectChildren(clone.transform, 12))}; mutation=false");
        }
        catch (Exception exception)
        {
            WriteBounded(
                $"state=probe_failed; source={Safe(source)}; error={Safe(exception.GetType().Name)}; message={Safe(exception.Message)}; mutation=false");
        }
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

        for (int index = 0; index < cameras.Count; index++)
        {
            Camera camera = cameras[index];
            if (camera != null && camera.name.IndexOf("inventory", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return camera;
            }
        }

        return fallback;
    }

    private static Component? FindComponentByTypeName(IReadOnlyList<Component> components, string typeName)
    {
        for (int index = 0; index < components.Count; index++)
        {
            Component component = components[index];
            if (component != null && string.Equals(component.GetType().Name, typeName, StringComparison.Ordinal))
            {
                return component;
            }
        }

        return null;
    }

    private static Transform? ResolveDragTarget(Assembly assembly)
    {
        Type? dragType = ResolveType(
            assembly,
            "MoxoPixel.MenuOverhaul.Helpers.PlayerModelDragRotator",
            "PlayerModelDragRotator");
        if (dragType == null)
        {
            return null;
        }

        object? target = ReadStaticMember(dragType, "Target");
        return target as Transform ?? (target as GameObject)?.transform ?? (target as Component)?.transform;
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

    private static string JoinNames(IReadOnlyList<Light> lights, int max)
    {
        var names = new List<string>();
        int count = Math.Min(lights.Count, max);
        for (int index = 0; index < count; index++)
        {
            Light light = lights[index];
            if (light != null)
            {
                names.Add(light.name);
            }
        }

        return names.Count == 0 ? "none" : string.Join(",", names);
    }

    private static string JoinDirectChildren(Transform root, int max)
    {
        var names = new List<string>();
        int count = Math.Min(root.childCount, max);
        for (int index = 0; index < count; index++)
        {
            Transform child = root.GetChild(index);
            if (child != null)
            {
                names.Add(child.name);
            }
        }

        return names.Count == 0 ? "none" : string.Join(",", names);
    }

    private static void WriteBounded(string message)
    {
        int currentSequence;
        lock (Gate)
        {
            sequence++;
            currentSequence = sequence;
            if (emitted >= MaxProbeLogs)
            {
                if (!capLogged)
                {
                    capLogged = true;
                    VanguardClientDiagnosticsLog.Diagnostic(
                        ProbeTag,
                        () => $"state=log_cap_reached; max={MaxProbeLogs}; mutation=false");
                }
                return;
            }

            emitted++;
        }

        VanguardClientDiagnosticsLog.Diagnostic(
            ProbeTag,
            () => $"sequence={currentSequence}; {message}");
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
#else
    public static void Probe(string source) => _ = source;
#endif
}
