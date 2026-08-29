using System;
using System.Reflection;
using Vanguard.Client.Raid.Career;

#if SPT_CLIENT
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: Bridges EFT/SPT/Fika callbacks into Career Event Truth Patches for the raid lifecycle patch bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.Raid.Patches;

#if SPT_CLIENT
/// <summary>
/// Observes the exact public EFT boundary that dispatches BotEventHandler.OnKill.
/// Postfix-only and read-only: no combat, AI, damage, persistence or Career authority is introduced.
/// </summary>
internal sealed class VanguardCareerEventTruthKillPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(BotEventHandler), nameof(BotEventHandler.Kill), new[] { typeof(IPlayer), typeof(IPlayer) })
           ?? throw new InvalidOperationException("BotEventHandler.Kill(IPlayer,IPlayer) not found for Vanguard Career event truth probe.");

    [PatchPostfix]
    private static void PatchPostfix(IPlayer __0, IPlayer __1)
        => VanguardCareerEventTruthProbeService.ObserveKill(__0, __1);
}

/// <summary>
/// Read-only XP shadow capture at the exact EFT boundary that would feed IStatisticsManager.OnEnemyKill.
/// The Fika ObservedStatisticsManager is a stub, so this patch observes inputs only and never mutates Career XP.
/// </summary>
internal sealed class VanguardCareerXpShadowKillCreditPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(Player), nameof(Player.OnBeenKilledByAggressor), new[] { typeof(IPlayer), typeof(DamageInfoStruct), typeof(EBodyPart), typeof(EDamageType) })
           ?? throw new InvalidOperationException("Player.OnBeenKilledByAggressor(IPlayer,DamageInfoStruct,EBodyPart,EDamageType) not found for Vanguard XP shadow accounting.");

    [PatchPostfix]
    private static void PatchPostfix(Player __instance, IPlayer __0, DamageInfoStruct __1, EBodyPart __2, EDamageType __3)
        => VanguardCareerEventTruthProbeService.ObserveXpKillCredit(__instance, __0, __1, __2, __3);
}

/// <summary>
/// skill acquisition compatibility restores the exact EFT ExecuteShotSkill -> WeaponShotAction boundary for
/// runtime-bound Vanguard AI Operators. The prefix leaves players and ordinary AI untouched;
/// for a handled Operator it invokes the native root action through the parity service and
/// skips the current EFT !IsAI no-op to prevent double attribution.
/// </summary>
internal sealed class VanguardOperatorWeaponSkillAcquisitionPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(Player), nameof(Player.ExecuteShotSkill), new[] { typeof(Item) })
           ?? throw new InvalidOperationException("Player.ExecuteShotSkill(Item) not found for Vanguard weapon skill/mastery acquisition compatibility.");

    [PatchPrefix]
    private static bool PatchPrefix(Player __instance, Item __0)
        => VanguardOperatorSkillAcquisitionParityService.ShouldRunOriginalExecuteShotSkill(__instance, __0);
}

#else
internal sealed class VanguardCareerEventTruthKillPatch
{
    public void Enable() { }
}

internal sealed class VanguardCareerXpShadowKillCreditPatch
{
    public void Enable() { }
}

internal sealed class VanguardOperatorWeaponSkillAcquisitionPatch
{
    public void Enable() { }
}
#endif
