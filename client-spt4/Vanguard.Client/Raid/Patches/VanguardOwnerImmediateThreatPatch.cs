#if SPT_CLIENT
using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Alliance;
using Vanguard.Client.Runtime.Awareness;

// Responsibility: Bridges EFT/SPT/Fika callbacks into Owner Immediate Threat Patch for the raid lifecycle patch bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.Raid.Patches;

/// <summary>
/// Guards the terminal EFT damage boundary for canonical Vanguard allies, then observes completed
/// hostile damage dispatches against known player Operator owners. The friendly veto never changes
/// target, movement or SAIN state; the hostile postfix only publishes a short awareness receipt.
/// </summary>
internal sealed class VanguardOwnerImmediateThreatPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(
                typeof(Player),
                nameof(Player.ApplyDamageInfo),
                new[] { typeof(DamageInfoStruct), typeof(EBodyPart), typeof(EBodyPartColliderType), typeof(float) })
            ?? throw new InvalidOperationException("Player.ApplyDamageInfo(DamageInfoStruct,EBodyPart,EBodyPartColliderType,float) not found for Vanguard owner threat receipt.");
    }


    [PatchPrefix]
    private static bool PatchPrefix(Player __instance, DamageInfoStruct __0, EBodyPart __1, out bool __state)
    {
        __state = false;
        try
        {
            if (!VanguardFriendlyDamageVetoService.ShouldBlock(__instance, __0, __1, out _))
            {
                return true;
            }

            __state = true;
            return false;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardFriendlyDamageVetoService.StatusTag, () =>
                $"VANGUARD_FRIENDLY_DAMAGE_VETO_FAILED type={exception.GetType().Name}; reason={exception.Message}; failOpen=true");
            return true;
        }
    }

    [PatchPostfix]
    private static void PatchPostfix(Player __instance, DamageInfoStruct __0, EBodyPart __1, bool __state)
    {
        if (__state)
        {
            return;
        }

        try
        {
            VanguardOwnerImmediateThreatService.ObserveConfirmedOwnerHit(__instance, __0, __1, DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardOwnerImmediateThreatService.StatusTag, () =>
                $"VANGUARD_OWNER_IMMEDIATE_THREAT_PATCH_FAILED type={exception.GetType().Name}; reason={exception.Message}");
        }
    }
}
#else
namespace Vanguard.Client.Raid.Patches;
internal sealed class VanguardOwnerImmediateThreatPatch { public void Enable() { } }
#endif
