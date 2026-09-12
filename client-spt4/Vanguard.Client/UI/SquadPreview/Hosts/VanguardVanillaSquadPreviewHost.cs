using System;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.UI.SquadPreview;

#if SPT_CLIENT
using UnityEngine;
using UnityEngine.SceneManagement;
#endif

// Responsibility: owns the minimal Vanilla Main Menu presentation shell required by the shared native EFT Operator renderer.
// Flow: create one isolated Weapon Preview-layer 3D root, one depth-only direct overlay camera and bounded lights in the live Main Menu scene.
// Authority boundary: Vanguard owns this shell only; native EFT menu objects, persistent Operator truth and Player inventory/session authority remain untouched.
// Invariant: the camera never clears the existing menu color buffer, no full-screen UI surface is inserted, no native PMC is created, and Release destroys every Vanguard-owned shell resource.
namespace Vanguard.Client.UI.SquadPreview.Hosts;

internal sealed class VanguardVanillaSquadPreviewHost : IVanguardSquadPreviewHost
{
    public const string HostResolvedTag = "SQUAD_PREVIEW_HOST_RESOLVED";
    private const string RootName = "VanguardVanillaSquadHostRoot";
    private const string CameraName = "VanguardSquadPreviewCamera";
    // Keep the preview island far enough from ordinary menu objects for the dedicated camera
    // (farClip=30) while staying close enough to Unity world origin to preserve sub-millimetre
    // transform precision for skinned meshes/IK. High-coordinate placement caused visible micro-jitter at 12000m.
    private static readonly Vector3 IsolatedWorldOrigin = new(64f, 64f, 64f);
    private const float KeyBaseIntensity = 3.0f;
    private const float FillBaseIntensity = 1.45f;
    private const float LightRange = 9.0f;

#if SPT_CLIENT
    private GameObject? rootObject;
    private GameObject? cameraObject;
    private GameObject? lightRootObject;
    private Camera? camera;
    private Light? keyLight;
    private Light? fillLight;
#endif

    public VanguardVanillaSquadPreviewHost()
    {
#if SPT_CLIENT
        VanguardSquadPreviewOptions.PresentationChanged += ApplyLightingSettings;
#endif
    }

    public bool IsAvailable
    {
        get
        {
#if SPT_CLIENT
            return rootObject != null && camera != null;
#else
            return false;
#endif
        }
    }

    public string HostKind => "vanilla";

#if SPT_CLIENT
    public Transform? RootTransform => rootObject?.transform;
    public Camera? Camera => camera;

    public bool TryAcquire(object menuScreen, out string reason)
    {
        if (IsAvailable)
        {
            reason = "already_acquired";
            return true;
        }

        if (menuScreen is not Component menuComponent || menuComponent.gameObject == null)
        {
            reason = "menu_screen_not_component";
            return false;
        }

        int previewLayer = LayerMask.NameToLayer("Weapon Preview");
        if (previewLayer < 0 || previewLayer > 31)
        {
            reason = "weapon_preview_layer_missing";
            return false;
        }

        Release("partial_acquire_cleanup");

        try
        {
            Scene targetScene = menuComponent.gameObject.scene;
            if (!targetScene.IsValid() || !targetScene.isLoaded)
            {
                reason = "menu_scene_invalid";
                return false;
            }

            GameObject root = new(RootName) { hideFlags = HideFlags.DontSave };
            SceneManager.MoveGameObjectToScene(root, targetScene);
            root.transform.position = IsolatedWorldOrigin;
            // EFT's menu player prefab faces +Z. The Vanilla overlay camera sits on -Z looking
            // toward the root, so rotate the host once rather than mutating each loaded body.
            root.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            root.transform.localScale = Vector3.one;

            GameObject cameraGo = new(CameraName) { hideFlags = HideFlags.DontSave };
            SceneManager.MoveGameObjectToScene(cameraGo, targetScene);
            Camera createdCamera = cameraGo.AddComponent<Camera>();
            // Direct-to-screen overlay: preserve the already-rendered EFT menu color buffer.
            // A full-screen RawImage/RenderTexture composition can black out the native menu, so this host renders directly to screen.
            createdCamera.clearFlags = CameraClearFlags.Depth;
            createdCamera.backgroundColor = Color.clear;
            createdCamera.cullingMask = 1 << previewLayer;
            createdCamera.nearClipPlane = 0.05f;
            createdCamera.farClipPlane = 30f;
            createdCamera.fieldOfView = 31f;
            createdCamera.depth = 100f;
            createdCamera.allowHDR = false;
            createdCamera.allowMSAA = true;
            createdCamera.transform.position = IsolatedWorldOrigin + new Vector3(0f, 1.32f, -4.65f);
            createdCamera.transform.LookAt(IsolatedWorldOrigin + new Vector3(0f, 1.18f, 0f), Vector3.up);

            createdCamera.targetTexture = null;
            createdCamera.useOcclusionCulling = false;

            GameObject lights = new("VanguardSquadPreviewLights") { hideFlags = HideFlags.DontSave };
            SceneManager.MoveGameObjectToScene(lights, targetScene);
            lights.transform.position = IsolatedWorldOrigin;
            Light createdKey = CreateBoundedSpotLight(
                lights.transform,
                "Key",
                new Vector3(-2.35f, 2.85f, -3.10f),
                new Vector3(0f, 1.20f, 0f),
                KeyBaseIntensity,
                previewLayer);
            Light createdFill = CreateBoundedSpotLight(
                lights.transform,
                "Fill",
                new Vector3(2.65f, 2.10f, -1.65f),
                new Vector3(0f, 1.12f, 0f),
                FillBaseIntensity,
                previewLayer);

            rootObject = root;
            cameraObject = cameraGo;
            lightRootObject = lights;
            camera = createdCamera;
            keyLight = createdKey;
            fillLight = createdFill;
            ApplyLightingSettings();

            reason = "acquired_owned_render_shell_native_operator_renderer";
            VanguardClientDiagnosticsLog.Info(
                HostResolvedTag,
                $"hostKind=vanilla; state=acquired; shell=owned_direct_depth_overlay_camera; camera={CameraName}; layer=Weapon_Preview; target=screen; clear=depth_only; uiOverlay=false; facingYaw=180; precisionOrigin=64_64_64; isolation=weapon_preview_layer_plus_farclip; lighting=bounded_spot_pair; lightingPercent={VanguardSquadPreviewOptions.LightingIntensityPercent}; lightRange={LightRange:0.0}; menu={Safe(menuComponent.name)}; nativePmcCreated=false; mutation=vanguard_owned_only");
            return true;
        }
        catch (Exception exception)
        {
            reason = "owned_render_shell_failed_" + exception.GetType().Name;
            VanguardClientDiagnosticsLog.Warning(
                HostResolvedTag,
                $"hostKind=vanilla; state=failed_closed; reason={Safe(reason)}; detail={Safe(exception.Message)}; mutation=vanguard_owned_only");
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
        GameObject? root = rootObject;
        GameObject? cameraGo = cameraObject;
        GameObject? lights = lightRootObject;
        rootObject = null;
        cameraObject = null;
        lightRootObject = null;
        camera = null;
        keyLight = null;
        fillLight = null;

        if (root != null)
        {
            UnityEngine.Object.Destroy(root);
        }
        if (lights != null)
        {
            UnityEngine.Object.Destroy(lights);
        }
        if (cameraGo != null)
        {
            UnityEngine.Object.Destroy(cameraGo);
        }
        if (root != null || cameraGo != null || lights != null)
        {
            VanguardClientDiagnosticsLog.Diagnostic(
                VanguardSquadPreviewController.HostReleasedTag,
                () => $"hostKind=vanilla; hostRootDestroyed={root != null}; cameraDestroyed={cameraGo != null}; lightsDestroyed={lights != null}; uiOverlayDestroyed=false; renderTextureReleased=false; nativePmcTouched=false; reason={Safe(reason)}; mutation=vanguard_owned_only");
        }
#else
        _ = reason;
#endif
    }

#if SPT_CLIENT
    public void SetPresentationVisible(bool visible, string reason)
    {
        // Temporary presentation suppression must not deactivate the Operator hierarchy.
        // PlayerModelLoader bodies carry native idle/weapon animator state beneath rootObject;
        // toggling the whole root inactive can resume them in a stale intermediate pose.
        // Keep bodies active and resident, and suppress only Vanguard-owned render outputs.
        bool rootReactivated = false;
        bool changed = false;
        if (rootObject != null && !rootObject.activeSelf)
        {
            rootObject.SetActive(true);
            rootReactivated = true;
            changed = true;
        }
        if (cameraObject != null && cameraObject.activeSelf != visible)
        {
            cameraObject.SetActive(visible);
            changed = true;
        }
        if (lightRootObject != null && lightRootObject.activeSelf != visible)
        {
            lightRootObject.SetActive(visible);
            changed = true;
        }

        if (changed)
        {
            VanguardClientDiagnosticsLog.Diagnostic(
                "SQUAD_PREVIEW_PRESENTATION",
                () => $"hostKind=vanilla; visible={visible}; reason={Safe(reason)}; bodiesActive={rootObject?.activeSelf == true}; rootReactivated={rootReactivated}; cameraActive={cameraObject?.activeSelf == true}; lightsActive={lightRootObject?.activeSelf == true}; mutation=vanguard_owned_only");
        }
    }

    private void ApplyLightingSettings()
    {
        float multiplier = VanguardSquadPreviewOptions.LightingMultiplier;
        if (keyLight != null)
        {
            keyLight.intensity = KeyBaseIntensity * multiplier;
        }
        if (fillLight != null)
        {
            fillLight.intensity = FillBaseIntensity * multiplier;
        }

        if (keyLight != null || fillLight != null)
        {
            VanguardClientDiagnosticsLog.Diagnostic(
                "SQUAD_PREVIEW_PRESENTATION",
                () => $"hostKind=vanilla; lighting=bounded_spot_pair; lightingPercent={VanguardSquadPreviewOptions.LightingIntensityPercent}; key={(keyLight?.intensity ?? 0f):0.00}; fill={(fillLight?.intensity ?? 0f):0.00}; range={LightRange:0.0}; mutation=vanguard_owned_only");
        }
    }

    private static Light CreateBoundedSpotLight(
        Transform parent,
        string suffix,
        Vector3 localPosition,
        Vector3 localTarget,
        float baseIntensity,
        int previewLayer)
    {
        GameObject lightGo = new("VanguardSquadPreviewLight" + suffix) { hideFlags = HideFlags.DontSave };
        lightGo.transform.SetParent(parent, false);
        lightGo.transform.localPosition = localPosition;
        Vector3 direction = localTarget - localPosition;
        lightGo.transform.localRotation = direction.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(direction.normalized, Vector3.up)
            : Quaternion.identity;

        Light light = lightGo.AddComponent<Light>();
        light.type = LightType.Spot;
        light.color = Color.white;
        light.intensity = baseIntensity * VanguardSquadPreviewOptions.LightingMultiplier;
        light.range = LightRange;
        light.spotAngle = 92f;
        light.shadows = LightShadows.None;
        light.cullingMask = 1 << previewLayer;
        return light;
    }
#endif

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
