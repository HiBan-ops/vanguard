using System;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.UI.SquadPreview;

#if SPT_CLIENT
using UnityEngine;
#endif

// Responsibility: owns the temporary Vanguard-only presentation shell used while a PMC raid is loading under Menu Overhaul.
// Flow: Menu Overhaul is allowed to hide its own Main Menu clone; Vanguard moves selected Operator presentation into a DontDestroyOnLoad
// direct-overlay shell, keeps it alive through loading, then the existing effective-BattleUI boundary destroys every Vanguard-owned resource.
// Authority boundary: this host owns only its root/camera/lights and never touches the Menu Overhaul PMC, clone, camera, lights or lifecycle.
// Invariant: presentation only, no persistent mutation, no forced third-party visibility, no custom character renderer.
namespace Vanguard.Client.UI.SquadPreview.Hosts;

internal sealed class VanguardRaidLoadingSquadPreviewHost
{
    private const string RootName = "VanguardRaidLoadingSquadHostRoot";
    private const string CameraName = "VanguardRaidLoadingSquadPreviewCamera";
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

    public VanguardRaidLoadingSquadPreviewHost()
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

    public string HostKind => "raid_loading";

#if SPT_CLIENT
    public Transform? RootTransform => rootObject?.transform;
    public Camera? Camera => camera;

    public bool TryAcquire(out string reason)
    {
        if (IsAvailable)
        {
            reason = "already_acquired";
            return true;
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
            GameObject root = new(RootName) { hideFlags = HideFlags.DontSave };
            UnityEngine.Object.DontDestroyOnLoad(root);
            root.transform.position = IsolatedWorldOrigin;
            root.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            root.transform.localScale = Vector3.one;

            GameObject cameraGo = new(CameraName) { hideFlags = HideFlags.DontSave };
            UnityEngine.Object.DontDestroyOnLoad(cameraGo);
            Camera createdCamera = cameraGo.AddComponent<Camera>();
            createdCamera.clearFlags = CameraClearFlags.Depth;
            createdCamera.backgroundColor = Color.clear;
            createdCamera.cullingMask = 1 << previewLayer;
            createdCamera.nearClipPlane = 0.05f;
            createdCamera.farClipPlane = 30f;
            createdCamera.fieldOfView = 31f;
            createdCamera.depth = 100f;
            createdCamera.allowHDR = false;
            createdCamera.allowMSAA = true;
            createdCamera.targetTexture = null;
            createdCamera.useOcclusionCulling = false;
            createdCamera.transform.position = IsolatedWorldOrigin + new Vector3(0f, 1.32f, -4.65f);
            createdCamera.transform.LookAt(IsolatedWorldOrigin + new Vector3(0f, 1.18f, 0f), Vector3.up);

            GameObject lights = new("VanguardRaidLoadingSquadPreviewLights") { hideFlags = HideFlags.DontSave };
            UnityEngine.Object.DontDestroyOnLoad(lights);
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

            reason = "acquired_vanguard_owned_dontdestroy_loading_shell";
            VanguardClientDiagnosticsLog.Diagnostic(
                VanguardVanillaSquadPreviewHost.HostResolvedTag,
                () => $"hostKind={HostKind}; state=acquired; shell=owned_dontdestroy_depth_overlay; layer=Weapon_Preview; facingYaw=180; precisionOrigin=64_64_64; lighting=bounded_spot_pair; lightingPercent={VanguardSquadPreviewOptions.LightingIntensityPercent}; nativePmcCreated=false; thirdPartyCameraTouched=false; thirdPartyLightsTouched=false; cleanupBoundary=effective_battle_ui; mutation=vanguard_owned_only");
            return true;
        }
        catch (Exception exception)
        {
            reason = "raid_loading_shell_failed_" + exception.GetType().Name;
            string safeReason = reason;
            VanguardClientDiagnosticsLog.Warning(
                VanguardVanillaSquadPreviewHost.HostResolvedTag,
                $"hostKind={HostKind}; state=failed_closed; reason={Safe(safeReason)}; detail={Safe(exception.Message)}; mutation=vanguard_owned_only");
            Release("acquire_exception");
            return false;
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
    }

    private static Light CreateBoundedSpotLight(
        Transform parent,
        string suffix,
        Vector3 localPosition,
        Vector3 localTarget,
        float baseIntensity,
        int previewLayer)
    {
        GameObject lightGo = new("VanguardRaidLoadingSquadPreviewLight" + suffix) { hideFlags = HideFlags.DontSave };
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
#else
    public object? RootTransform => null;
    public object? Camera => null;
    public bool TryAcquire(out string reason)
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

        if (root != null || lights != null || cameraGo != null)
        {
            VanguardClientDiagnosticsLog.Diagnostic(
                VanguardSquadPreviewController.HostReleasedTag,
                () => $"hostKind={HostKind}; hostRootDestroyed={root != null}; cameraDestroyed={cameraGo != null}; lightsDestroyed={lights != null}; nativePmcTouched=false; thirdPartyCameraTouched=false; thirdPartyLightsTouched=false; reason={Safe(reason)}; mutation=vanguard_owned_only");
        }
#else
        _ = reason;
#endif
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
