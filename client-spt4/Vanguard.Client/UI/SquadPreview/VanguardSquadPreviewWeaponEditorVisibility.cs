using System;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.UI.SquadPreview.Hosts;

#if SPT_CLIENT
using UnityEngine;
#endif

// Responsibility: temporarily suppresses the persistent Squad Preview while EFT owns a dedicated weapon-editing presentation.
// Flow: EditBuild/WeaponModding screen presentation acquires a lease; disabling/destroying that screen releases it without unloading Operator bodies.
// Authority boundary: visibility only. The reconciler, equipment truth, native weapon editors and EFT screen lifecycle remain untouched.
// Invariant: weapon editing gets an unobstructed native view while ordinary Off-Raid navigation keeps the persistent selected-Operator presence.
namespace Vanguard.Client.UI.SquadPreview;

internal static class VanguardSquadPreviewWeaponEditorVisibility
{
#if SPT_CLIENT
    public static void AttachToScreen(object? screen, string source)
    {
        if (screen is not Component component || component.gameObject == null)
        {
            VanguardSquadPreviewHostCoordinator.AcquirePresentationSuppression(
                "weapon_editor_fallback_" + Safe(source),
                source + "_screen_component_missing");
            return;
        }

        VanguardSquadPreviewVisibilityLease? lease = component.GetComponent<VanguardSquadPreviewVisibilityLease>();
        lease ??= component.gameObject.AddComponent<VanguardSquadPreviewVisibilityLease>();
        lease.Arm(source);
    }

    public static void AttachToActiveScreenType(Type? screenType, string source)
    {
        if (screenType != null)
        {
            try
            {
                UnityEngine.Object[] candidates = UnityEngine.Object.FindObjectsOfType(screenType);
                foreach (UnityEngine.Object candidate in candidates)
                {
                    if (candidate is Component component && component.gameObject.activeInHierarchy)
                    {
                        AttachToScreen(component, source);
                        return;
                    }
                }
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(
                    "SQUAD_PREVIEW_PRESENTATION",
                    $"weaponEditorVisibility=lookup_failed; source={Safe(source)}; screenType={Safe(screenType?.FullName)}; detail={Safe(exception.GetType().Name + ":" + exception.Message)}; mutation=false");
            }
        }

        VanguardSquadPreviewHostCoordinator.AcquirePresentationSuppression(
            "weapon_editor_fallback_" + Safe(source),
            source + "_active_screen_missing");
    }
#endif

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

#if SPT_CLIENT
internal sealed class VanguardSquadPreviewVisibilityLease : MonoBehaviour
{
    private string token = string.Empty;
    private string source = string.Empty;
    private bool acquired;

    public void Arm(string leaseSource)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            source = string.IsNullOrWhiteSpace(leaseSource) ? "weapon_editor" : leaseSource;
            token = source + "_" + GetInstanceID();
        }

        if (gameObject.activeInHierarchy)
        {
            Acquire();
        }
    }

    private void OnEnable()
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            Acquire();
        }
    }

    private void OnDisable() => Release();

    private void OnDestroy() => Release();

    private void Acquire()
    {
        if (acquired || string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        acquired = true;
        VanguardSquadPreviewHostCoordinator.AcquirePresentationSuppression(token, source);
    }

    private void Release()
    {
        if (!acquired || string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        acquired = false;
        VanguardSquadPreviewHostCoordinator.ReleasePresentationSuppression(token, source + "_screen_closed");
    }
}
#endif
