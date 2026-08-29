#if SPT_CLIENT
using EFT;
using EFT.Animations;
using TMPro;
using UnityEngine;

// Responsibility: Defines response/projection payloads produced by the raid Operator HUD.
// Flow: Owning services project canonical state into these shapes before serialization to the client or another subsystem.
// Authority boundary: Presentation/transport contract only; canonical authority remains with the source service or persistence store.
// Invariant: Projection code must not mutate the state it describes and serialized fields remain compatibility-conscious.
namespace Vanguard.Client.Raid.Hud;

internal enum VanguardRaidOperatorHudProjectionFailureReason
{
    None = 0,
    NoCamera = 1,
    BehindCamera = 2,
}

internal static class VanguardRaidOperatorHudProjection
{
    public static bool TryProjectToCanvas(
        Vector3 worldPosition,
        Player localPlayer,
        RectTransform canvasRect,
        out Vector2 canvasPosition,
        out VanguardRaidOperatorHudProjectionFailureReason failureReason)
    {
        canvasPosition = Vector2.zero;
        failureReason = VanguardRaidOperatorHudProjectionFailureReason.None;

        var cameraClass = CameraClass.Instance;
        if (localPlayer is null || cameraClass?.Camera is null)
        {
            failureReason = VanguardRaidOperatorHudProjectionFailureReason.NoCamera;
            return false;
        }

        var projectionCamera = cameraClass.Camera;
        var canvasSize = canvasRect.rect.size;
        var scaleFactor = 1f;

        // When the player uses a zoomed optic, project against the optic camera rather than the default world camera.
        if (IsZoomedOpticAiming(localPlayer.ProceduralWeaponAnimation))
        {
            var opticCamera = cameraClass.OpticCameraManager?.Camera;
            if (opticCamera is not null)
            {
                projectionCamera = opticCamera;
                canvasSize = opticCamera.pixelRect.max;
                scaleFactor = canvasRect.rect.width / Screen.width;
            }
        }

        var viewportPoint = projectionCamera.WorldToViewportPoint(worldPosition);
        if (viewportPoint.z <= 0f)
        {
            failureReason = VanguardRaidOperatorHudProjectionFailureReason.BehindCamera;
            return false;
        }

        canvasPosition = new Vector2(
            (viewportPoint.x - 0.5f) * canvasSize.x * scaleFactor,
            (viewportPoint.y - 0.5f) * canvasSize.y * scaleFactor);
        return true;
    }

    public static TMP_FontAsset? ResolveFont()
    {
        return TMP_Settings.defaultFontAsset
               ?? Resources.Load<TMP_FontAsset>("Fonts & Materials/BenderNormal SDF")
               ?? Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
    }

    private static bool IsZoomedOpticAiming(ProceduralWeaponAnimation? weaponAnimation)
    {
        if (weaponAnimation is null)
        {
            return false;
        }

        return weaponAnimation.IsAiming
               && weaponAnimation.CurrentScope is not null
               && weaponAnimation.CurrentScope.IsOptic
               && GetScopeZoomLevel(weaponAnimation) > 1f;
    }

    private static float GetScopeZoomLevel(ProceduralWeaponAnimation weaponAnimation)
    {
        var sight = weaponAnimation.CurrentAimingMod;
        if (sight is null)
        {
            return 1f;
        }

        return sight.ScopeZoomValue > 1f
            ? sight.ScopeZoomValue
            : sight.GetCurrentOpticZoom();
    }
}
#else
namespace Vanguard.Client.Raid.Hud;

internal static class VanguardRaidOperatorHudProjection
{
}
#endif
