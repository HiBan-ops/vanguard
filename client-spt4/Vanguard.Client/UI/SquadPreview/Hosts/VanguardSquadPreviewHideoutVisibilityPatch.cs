using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.UI.SquadPreview;

#if SPT_CLIENT
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: suppresses Vanguard's persistent Squad Preview while EFT owns the Hideout presentation.
// Flow: HideoutScreenRear.ShowAsync arms the same screen-lifetime presentation lease already proven by the weapon-editing flow.
// Authority boundary: visibility only; Hideout lifecycle, camera, player, inventory and persistent Operator state remain EFT/Vanguard-domain owned.
// Invariant: entering Hideout cannot leave Operator preview bodies over the Hideout view, and leaving Hideout restores ordinary menu presentation without unloading bodies.
namespace Vanguard.Client.UI.SquadPreview.Hosts;

#if SPT_CLIENT
internal sealed class VanguardSquadPreviewHideoutVisibilityPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type screenType = AccessTools.TypeByName("EFT.Hideout.HideoutScreenRear")
            ?? throw new MissingMemberException("EFT.Hideout.HideoutScreenRear type not found.");

        MethodInfo? target = screenType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
                string.Equals(method.Name, "ShowAsync", StringComparison.Ordinal)
                && method.GetParameters().Length == 1
                && typeof(Task).IsAssignableFrom(method.ReturnType));

        if (target == null)
        {
            throw new MissingMethodException(screenType.FullName ?? screenType.Name, "ShowAsync(controller)");
        }

        VanguardClientDiagnosticsLog.Info(
            "SQUAD_PREVIEW_PRESENTATION",
            $"hideout_visibility_patch_target_resolved target={target.DeclaringType?.FullName ?? screenType.FullName}.{target.Name}; suppression=screen_lifetime_lease; mutation=vanguard_presentation_only");
        return target;
    }

    [PatchPrefix]
    private static void Prefix(object __instance)
    {
        // Reuse the established lease primitive rather than inventing a second visibility authority.
        // Its historical class name is weapon-editor-specific, but the lease itself is screen-lifecycle generic.
        VanguardSquadPreviewWeaponEditorVisibility.AttachToScreen(__instance, "hideout_screen");
    }
}
#endif
