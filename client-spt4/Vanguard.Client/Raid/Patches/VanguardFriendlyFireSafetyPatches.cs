#if SPT_CLIENT
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Alliance;
using Vanguard.Client.Runtime.Combat;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Grenades;

// Responsibility: Bridges EFT/SPT/Fika callbacks into Friendly Fire Safety Patches for the raid lifecycle patch bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.Raid.Patches;

internal sealed class VanguardShootDataFriendlyCorridorPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ShootData), nameof(ShootData.Shoot))
        ?? throw new InvalidOperationException("ShootData.Shoot not found for Vanguard friendly corridor guard.");

    [PatchPrefix]
    private static bool PatchPrefix(ShootData __instance, ref bool __result)
    {
        if (!VanguardFriendlyFireSafetyService.IsFireCorridorBlocked(__instance, out var friendlyProfileId, out _, out _))
        {
            return true;
        }

        VanguardGlobalCombatProductionDiagnosticsService.ObserveVanguardTriggerVeto(__instance, friendlyProfileId);
        __result = false;
        return false;
    }
}

internal sealed class VanguardShootDataBurstFriendlyCorridorPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ShootData), nameof(ShootData.ManualUpdate))
        ?? throw new InvalidOperationException("ShootData.ManualUpdate not found for Vanguard per-frame friendly corridor guard.");

    [PatchPrefix]
    private static void PatchPrefix(ShootData __instance)
    {
        if (__instance == null || !__instance.Shooting
            || !VanguardFriendlyFireSafetyService.IsFireCorridorBlocked(__instance, out var friendlyProfileId, out var alongMeters, out var lateralMeters))
        {
            return;
        }

        // The burst may have been authorized before a squadmate crossed the muzzle. Releasing the
        // trigger here prevents the rest of the burst without changing SAIN target or movement state.
        __instance.EndShoot();
        __instance.NextFingerDownCan = Math.Max(__instance.NextFingerDownCan, Time.time + 0.16f);
        VanguardFriendlyFireSafetyService.LogBurstTriggerReleased(
            __instance.Owner?.ProfileId,
            friendlyProfileId,
            alongMeters,
            lateralMeters);
    }
}

internal sealed class VanguardActualProjectileFriendlyCorridorPatch : ModulePatch
{
    private static readonly FieldInfo? PlayerField = AccessTools.Field(typeof(Player.ItemHandsController), "_player");
    private static readonly ConditionalWeakTable<Player.FirearmController, ShooterReference> ShooterByController = new();

    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.InitiateShot))
        ?? throw new InvalidOperationException("Player.FirearmController.InitiateShot not found for Vanguard per-projectile friendly corridor guard.");

    [PatchPrefix]
    private static bool PatchPrefix(Player.FirearmController __instance, Vector3 __2, Vector3 __3)
    {
        ShooterReference reference = ShooterByController.GetValue(__instance, ResolveShooter);
        Player? shooter = reference.Player;
        if (shooter == null)
        {
            shooter = PlayerField?.GetValue(__instance) as Player;
            reference.Player = shooter;
        }

        VanguardOwnerShotMemoryService.ObserveShot(shooter, __2, __3);
        VanguardNearMissSuppressionService.ObserveHostileShot(shooter, __2, __3);
        bool blocked = VanguardFriendlyFireSafetyService.IsActualShotBlocked(shooter, __2, __3, out _, out _, out _);
        if (blocked)
        {
            VanguardGlobalCombatProductionDiagnosticsService.ObserveVanguardProjectileVeto(shooter);
        }
        return !blocked;
    }

    private static ShooterReference ResolveShooter(Player.FirearmController controller)
    {
        return new ShooterReference(PlayerField?.GetValue(controller) as Player);
    }

    private sealed class ShooterReference
    {
        public ShooterReference(Player? player)
        {
            Player = player;
        }

        public Player? Player { get; set; }
    }
}

internal sealed class VanguardSainGrenadeFriendlyRadiusPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.SAINComponent.Classes.WeaponFunction.GrenadeThrowDecider")
            ?? throw new InvalidOperationException("SAIN GrenadeThrowDecider type not found for Vanguard grenade safety guard.");
        return AccessTools.Method(type, "CheckFriendlyDistances", new[] { typeof(Vector3) })
            ?? throw new InvalidOperationException("SAIN GrenadeThrowDecider.CheckFriendlyDistances not found.");
    }

    [PatchPrefix]
    private static bool PatchPrefix(object __instance, Vector3 __0, ref bool __result)
    {
        BotOwner? owner = TryResolveBotOwner(__instance);
        if (owner != null
            && VanguardMainIntentScheduler.TryGetActiveEmergencyWindow(owner.ProfileId, DateTimeOffset.UtcNow, out string windowId, out string grenadeKey, out _))
        {
            __result = false;
            VanguardClientDiagnosticsLog.Trace(VanguardGrenadeEmergencyPolicy.StatusTag, () =>
                $"VANGUARD_NEW_GRENADE_THROW_VETO botProfile={owner.ProfileId}; emergencyWindow={windowId}; dangerGrenade={grenadeKey}; candidateThrowPoint={__0.x:0.0},{__0.y:0.0},{__0.z:0.0}; existingHandsForceCancel=false; reason=survival_window_preempts_new_throw_decision; tag={VanguardGrenadeEmergencyPolicy.StatusTag}");
            return false;
        }
        if (!VanguardFriendlyFireSafetyService.IsGrenadeBlastUnsafe(owner, __0, out _, out _))
        {
            return true;
        }

        __result = false;
        return false;
    }

    private static BotOwner? TryResolveBotOwner(object instance)
    {
        if (instance == null)
        {
            return null;
        }

        try
        {
            BotOwner? propertyValue = Traverse.Create(instance).Property("BotOwner").GetValue<BotOwner>();
            if (propertyValue != null)
            {
                return propertyValue;
            }
        }
        catch
        {
            // Fall through to field/base-type reflection. SAIN has changed this member shape
            // between releases, while the semantic owner contract remains stable.
        }

        Type? current = instance.GetType();
        while (current != null)
        {
            FieldInfo? field = AccessTools.Field(current, "BotOwner")
                ?? AccessTools.Field(current, "_botOwner")
                ?? AccessTools.Field(current, "botOwner");
            if (field?.GetValue(instance) is BotOwner fieldValue)
            {
                return fieldValue;
            }

            PropertyInfo? property = AccessTools.Property(current, "BotOwner");
            if (property?.GetValue(instance, null) is BotOwner propertyValue)
            {
                return propertyValue;
            }
            current = current.BaseType;
        }

        return null;
    }
}
#else
namespace Vanguard.Client.Raid.Patches;
internal sealed class VanguardShootDataFriendlyCorridorPatch { public void Enable() { } }
internal sealed class VanguardShootDataBurstFriendlyCorridorPatch { public void Enable() { } }
internal sealed class VanguardActualProjectileFriendlyCorridorPatch { public void Enable() { } }
internal sealed class VanguardSainGrenadeFriendlyRadiusPatch { public void Enable() { } }
#endif
