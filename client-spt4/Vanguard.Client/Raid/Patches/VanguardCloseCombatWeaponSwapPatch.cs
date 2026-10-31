#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.SquadCohesion;

// Responsibility: stabilizes Vanguard combat weapon handling by preserving the existing critical-ammo close-combat native weapon swap and rejecting non-improving pure external-magazine reload swaps.
// Flow: after EFT/SAIN resolves FightShallReload -> exact Vanguard runtime -> critical close-threat native swap opportunity -> fresh SAIN-combat proof -> current external magazine versus compatible reachable spare -> allow native reload only when the spare strictly improves ready magazine rounds.
// Authority boundary: EFT/SAIN owns reload execution and the native slot transition; Vanguard only vetoes a proven non-emergency down/equal external-magazine swap or asks the existing native selector for the already-supported critical close-combat alternative.
// Invariant: critical-low ammo, malfunction handling, hybrid/internal reload modes, unresolved evidence and non-Vanguard bots always fail open to EFT/SAIN; the stability guard never mutates inventory, magazines, target, aim, fire or movement.
namespace Vanguard.Client.Raid.Patches;

internal sealed class VanguardCloseCombatWeaponSwapPatch : ModulePatch
{
    internal const string StatusTag = "VANGUARD_CLOSE_COMBAT_WEAPON_SWAP";
    private const float CloseCombatSwapDistanceMeters = 15f;
    private static readonly object ReloadStabilitySync = new();
    private static readonly List<MagazineItemClass> ReachableMagazineBuffer = new();

    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(BotReload), nameof(BotReload.FightShallReload))
        ?? throw new InvalidOperationException("BotReload.FightShallReload not found for Vanguard combat weapon handling.");

    [PatchPostfix]
    private static void PatchPostfix(BotReload __instance, ref bool __result)
    {
        try
        {
            if (!__result || __instance == null || __instance.Reloading)
            {
                return;
            }

            BotOwner? owner = __instance.BotOwner_0;
            if (owner == null
                || owner.IsDead
                || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(owner.ProfileId, out _))
            {
                return;
            }

            int currentReadyRounds = Math.Max(0, __instance.BulletCount);
            if (currentReadyRounds <= VanguardDynamicWeaponPreferenceService.CriticalLowReadyRounds)
            {
                if (TryHandleCloseCombatNativeSwap(__instance, owner, currentReadyRounds))
                {
                    __result = false;
                }
                return;
            }

            if (!ShouldSuppressNonImprovingExternalMagazineReload(
                    __instance,
                    owner,
                    currentReadyRounds,
                    out int currentMagazineRounds,
                    out int bestSpareRounds,
                    out string stabilityReason))
            {
                return;
            }

            __result = false;
            VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                $"VANGUARD_COMBAT_RELOAD_STABILITY_SUPPRESSED operator={Safe(owner.ProfileId)}; currentReadyRounds={currentReadyRounds}; currentMagazineRounds={currentMagazineRounds}; bestCompatibleSpareRounds={bestSpareRounds}; reason={Safe(stabilityReason)}; reloadSuppressed=true; inventoryMutation=false; targetAimFireMutation=false; movementMutation=false; authority=post_fight_shall_reload_non_improving_external_mag_only");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(StatusTag, () =>
                $"VANGUARD_CLOSE_COMBAT_WEAPON_SWAP_FAILED type={exception.GetType().Name}; reason={Safe(exception.Message)}; failOpenToEftSainReload=true");
        }
    }

    private static bool TryHandleCloseCombatNativeSwap(BotReload reload, BotOwner owner, int currentReadyRounds)
    {
        EnemyInfo? enemy = owner.Memory?.GoalEnemy;
        if (enemy == null || !enemy.IsVisible || enemy.Distance > CloseCombatSwapDistanceMeters)
        {
            return false;
        }

        BotWeaponManager? weaponManager = owner.WeaponManager;
        BotWeaponSelector? selector = weaponManager?.Selector;
        if (weaponManager == null || selector == null || !selector.IsWeaponReady)
        {
            return false;
        }

        selector.UpdateWeaponsList();
        EquipmentSlot currentSlot = selector.EquipmentSlot;
        if (!TryChooseImmediatelyLoadedAlternative(owner, currentSlot, currentReadyRounds, out EquipmentSlot targetSlot, out int readyRounds))
        {
            return false;
        }

        bool switched = selector.TryChangeToSlot(targetSlot, changeToMain: targetSlot == selector.MainWeapon);
        if (!switched)
        {
            return false;
        }

        bool preferenceAligned = VanguardDynamicWeaponPreferenceService.NotifyCloseCombatNativeSwap(
            owner,
            enemy.ProfileId,
            currentSlot,
            targetSlot,
            DateTimeOffset.UtcNow);
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"VANGUARD_CLOSE_COMBAT_WEAPON_SWAP_TRIGGERED operator={Safe(owner.ProfileId)}; enemy={Safe(enemy.ProfileId)}; distance={enemy.Distance:0.0}; from={currentSlot}; to={targetSlot}; currentReadyRounds={currentReadyRounds}; alternativeReadyRounds={readyRounds}; reloadSuppressed=true; nativeSelector=true; dynamicPreferenceAligned={preferenceAligned}; targetAimFireMutation=false; movementMutation=false; doctrine=opportunity_first_critical_low_ammo_swap");
        return true;
    }

    private static bool ShouldSuppressNonImprovingExternalMagazineReload(
        BotReload reload,
        BotOwner owner,
        int currentReadyRounds,
        out int currentMagazineRounds,
        out int bestSpareRounds,
        out string reason)
    {
        currentMagazineRounds = 0;
        bestSpareRounds = 0;
        reason = "reload_stability_not_applicable";

        if (currentReadyRounds <= VanguardDynamicWeaponPreferenceService.CriticalLowReadyRounds)
        {
            reason = "critical_low_ammo_fail_open";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(owner.ProfileId, out var decision)
            || decision == null
            || !VanguardOperatorDecisionSnapshotService.IsFresh(decision, DateTimeOffset.UtcNow, out _)
            || decision.Sain.Active != true
            || decision.Sain.IsInCombat != true)
        {
            reason = "fresh_sain_combat_not_proven";
            return false;
        }

        BotWeaponManager? weaponManager = owner.WeaponManager;
        if (weaponManager == null || weaponManager.Malfunctions?.HaveMalfunction() == true)
        {
            reason = "weapon_manager_missing_or_malfunction_fail_open";
            return false;
        }

        Weapon? weapon = reload.ShootController?.Item;
        if (weapon == null || weapon.ReloadMode != Weapon.EReloadMode.ExternalMagazine)
        {
            reason = "not_pure_external_magazine_reload";
            return false;
        }

        MagazineItemClass? currentMagazine = weapon.GetCurrentMagazine();
        Player? player = reload.Player ?? owner.GetPlayer;
        if (currentMagazine == null || player?.InventoryController == null)
        {
            reason = "current_magazine_or_inventory_unresolved_fail_open";
            return false;
        }

        currentMagazineRounds = Math.Max(0, currentMagazine.Count);
        if (currentMagazineRounds >= currentMagazine.MaxCount)
        {
            reason = "current_magazine_full_native_gate_owns";
            return false;
        }

        Slot magazineSlot = weapon.GetMagazineSlot();
        if (magazineSlot == null)
        {
            reason = "magazine_slot_unresolved_fail_open";
            return false;
        }

        bool foundCompatibleSpare = false;
        lock (ReloadStabilitySync)
        {
            ReachableMagazineBuffer.Clear();
            try
            {
                player.InventoryController.GetReachableItemsOfTypeNonAlloc(ReachableMagazineBuffer);
                foreach (MagazineItemClass spare in ReachableMagazineBuffer)
                {
                    if (spare == null
                        || ReferenceEquals(spare, currentMagazine)
                        || spare.Count <= 0
                        || !InteractionsHandlerClass.CheckMoveIgnoringTargetItem(spare, magazineSlot, player.InventoryController).Succeeded)
                    {
                        continue;
                    }

                    foundCompatibleSpare = true;
                    bestSpareRounds = Math.Max(bestSpareRounds, Math.Max(0, spare.Count));
                }
            }
            finally
            {
                ReachableMagazineBuffer.Clear();
            }
        }

        if (!foundCompatibleSpare)
        {
            reason = "no_compatible_spare_native_gate_owns";
            return false;
        }
        if (bestSpareRounds > currentMagazineRounds)
        {
            reason = "strictly_better_spare_available";
            return false;
        }

        reason = "compatible_spare_not_strictly_better";
        return true;
    }

    private static bool TryChooseImmediatelyLoadedAlternative(
        BotOwner owner,
        EquipmentSlot currentSlot,
        int currentReadyRounds,
        out EquipmentSlot targetSlot,
        out int readyRounds)
    {
        targetSlot = currentSlot;
        readyRounds = 0;

        if (currentSlot == EquipmentSlot.FirstPrimaryWeapon)
        {
            return TryLoadedSlot(owner, EquipmentSlot.SecondPrimaryWeapon, currentReadyRounds, out targetSlot, out readyRounds)
                || TryLoadedSlot(owner, EquipmentSlot.Holster, currentReadyRounds, out targetSlot, out readyRounds);
        }

        if (currentSlot == EquipmentSlot.SecondPrimaryWeapon)
        {
            return TryLoadedSlot(owner, EquipmentSlot.FirstPrimaryWeapon, currentReadyRounds, out targetSlot, out readyRounds)
                || TryLoadedSlot(owner, EquipmentSlot.Holster, currentReadyRounds, out targetSlot, out readyRounds);
        }

        if (currentSlot == EquipmentSlot.Holster)
        {
            return TryLoadedSlot(owner, EquipmentSlot.FirstPrimaryWeapon, currentReadyRounds, out targetSlot, out readyRounds)
                || TryLoadedSlot(owner, EquipmentSlot.SecondPrimaryWeapon, currentReadyRounds, out targetSlot, out readyRounds);
        }

        return false;
    }

    private static bool TryLoadedSlot(
        BotOwner owner,
        EquipmentSlot slot,
        int currentReadyRounds,
        out EquipmentSlot targetSlot,
        out int readyRounds)
    {
        targetSlot = slot;
        readyRounds = 0;

        InventoryEquipment? equipment = owner.GetPlayer?.Inventory?.Equipment;
        if (equipment?.GetSlot(slot)?.ContainedItem is not Weapon weapon)
        {
            return false;
        }

        readyRounds = Math.Max(0, weapon.GetCurrentMagazineCount()) + Math.Max(0, weapon.ChamberAmmoCount);
        return readyRounds > currentReadyRounds;
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
}


// Legacy class name is retained to keep the existing bootstrap surface stable; the patch now routes only native ChangeToMain semantics.
internal sealed class VanguardWeaponSuitabilityContinuityPatch : ModulePatch
{
    internal const string StatusTag = "VANGUARD_DYNAMIC_WEAPON_PREFERENCE_ROUTING";

    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(BotWeaponSelector), nameof(BotWeaponSelector.ChangeToMain))
        ?? throw new InvalidOperationException("BotWeaponSelector.ChangeToMain not found for Vanguard dynamic weapon preference routing.");

    [PatchPrefix]
    private static bool PatchPrefix(BotWeaponSelector __instance, ref bool __result)
    {
        try
        {
            if (!VanguardDynamicWeaponPreferenceService.TryHandleNativeMainRequest(
                    __instance,
                    DateTimeOffset.UtcNow,
                    out bool routedResult,
                    out _))
            {
                return true;
            }

            __result = routedResult;
            return false;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(StatusTag, () =>
                $"VANGUARD_DYNAMIC_WEAPON_PREFERENCE_ROUTING_FAILED type={exception.GetType().Name}; reason={Safe(exception.Message)}; failOpenToNativeSelector=true");
            return true;
        }
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
}
#else
namespace Vanguard.Client.Raid.Patches;
internal sealed class VanguardCloseCombatWeaponSwapPatch { public void Enable() { } }
internal sealed class VanguardWeaponSuitabilityContinuityPatch { public void Enable() { } }
#endif
