#if SPT_CLIENT
using System;
using System.Reflection;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using SPT.Reflection.Patching;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Loot;

// Responsibility: Bridges EFT/SPT/Fika callbacks into Corpse Registration Patch for the raid lifecycle patch bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.Raid.Patches;

/// <summary>
/// Observes EFT's canonical corpse creation boundary. The runtime registers the corpse and captures registration-time hostility evidence for bounded
/// read-only qualification and dry-run planning; it never reroutes bots, opens inventory, moves items or claims authority.
/// </summary>
internal sealed class VanguardCorpseRegistrationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(Player), nameof(Player.CreateCorpse), Type.EmptyTypes)
            ?? throw new InvalidOperationException("Player.CreateCorpse() not found for the Vanguard corpse registry.");
    }

    [PatchPostfix]
    private static void PatchPostfix(Player __instance, Corpse __result)
    {
        try
        {
            VanguardCorpseRegistry.Register(__instance, __result);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardCorpseRegistry.StatusTag, () =>
                $"VANGUARD_CORPSE_PATCH_FAILED type={exception.GetType().Name}; reason={exception.Message}; failOpen=true");
        }
    }
}
#else
namespace Vanguard.Client.Raid.Patches;
internal sealed class VanguardCorpseRegistrationPatch { public void Enable() { } }
#endif
