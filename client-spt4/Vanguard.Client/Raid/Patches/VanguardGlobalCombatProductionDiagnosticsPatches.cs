#if SPT_CLIENT
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using Vanguard.Client.Runtime.Combat;

// Responsibility: Bridges EFT/SPT/Fika callbacks into Global Combat Production Diagnostics Patches for the raid lifecycle patch bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.Raid.Patches;


/// <summary>
/// Passive ShootData boundary probe. The prefix has no return value and no ref/out arguments, so
/// Harmony can execute it even when another prefix suppresses the original method. The postfix
/// records the final result after the complete patch chain.
/// </summary>
internal sealed class VanguardShootDataProductionDiagnosticsPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ShootData), nameof(ShootData.Shoot))
        ?? throw new InvalidOperationException("ShootData.Shoot not found for passive global combat diagnostics.");

    [PatchPrefix]
    private static void PatchPrefix(ShootData __instance)
    {
        if (VanguardGlobalCombatProductionDiagnosticsService.FireBoundariesEnabled)
        {
            VanguardGlobalCombatProductionDiagnosticsService.ObserveShootRequest(__instance);
        }
    }

    [PatchPostfix]
    private static void PatchPostfix(ShootData __instance, bool __result)
    {
        if (VanguardGlobalCombatProductionDiagnosticsService.FireBoundariesEnabled)
        {
            VanguardGlobalCombatProductionDiagnosticsService.ObserveShootResult(__instance, __result);
        }
    }
}

/// <summary>
/// Passive sampled heartbeat while EFT keeps the firearm trigger active. No fire state is changed.
/// </summary>
internal sealed class VanguardShootDataTriggerDiagnosticsPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ShootData), nameof(ShootData.ManualUpdate))
        ?? throw new InvalidOperationException("ShootData.ManualUpdate not found for passive global combat diagnostics.");

    [PatchPrefix]
    private static void PatchPrefix(ShootData __instance)
    {
        if (VanguardGlobalCombatProductionDiagnosticsService.FireBoundariesEnabled)
        {
            VanguardGlobalCombatProductionDiagnosticsService.ObserveShootDataHeartbeat(__instance);
        }
    }
}

/// <summary>
/// Passive final fire-production probe. Entry is recorded independently of Vanguard's projectile
/// guard, and Harmony's __runOriginal value distinguishes a real execution of EFT's original
/// InitiateShot method from suppression by any prefix in the global patch chain.
/// </summary>
internal sealed class VanguardInitiateShotProductionDiagnosticsPatch : ModulePatch
{
    private static readonly FieldInfo? PlayerField = AccessTools.Field(typeof(Player.ItemHandsController), "_player");
    private static readonly ConditionalWeakTable<Player.FirearmController, ShooterReference> ShooterByController = new();

    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.InitiateShot))
        ?? throw new InvalidOperationException("Player.FirearmController.InitiateShot not found for passive global combat diagnostics.");

    [PatchPrefix]
    private static void PatchPrefix(Player.FirearmController __instance)
    {
        if (VanguardGlobalCombatProductionDiagnosticsService.FireBoundariesEnabled)
        {
            VanguardGlobalCombatProductionDiagnosticsService.ObserveInitiateShotEntry(ResolveShooter(__instance));
        }
    }

    [PatchPostfix]
    private static void PatchPostfix(Player.FirearmController __instance, bool __runOriginal)
    {
        if (VanguardGlobalCombatProductionDiagnosticsService.FireBoundariesEnabled)
        {
            VanguardGlobalCombatProductionDiagnosticsService.ObserveInitiateShotCompletion(ResolveShooter(__instance), __runOriginal);
        }
    }

    private static Player? ResolveShooter(Player.FirearmController controller)
    {
        ShooterReference reference = ShooterByController.GetValue(controller, value =>
            new ShooterReference(PlayerField?.GetValue(value) as Player));
        if (reference.Player == null)
        {
            reference.Player = PlayerField?.GetValue(controller) as Player;
        }
        return reference.Player;
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

/// <summary>
/// Passive timing probe around SAIN's batched command construction. It observes only the integer
/// work dimensions and elapsed CPU time; NativeArrays, enemy objects and job handles are untouched.
/// </summary>
internal sealed class VanguardSainVisionCreateCommandsDiagnosticsPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.Components.VisionRaycastJob")
            ?? throw new InvalidOperationException("SAIN VisionRaycastJob type not found for passive global combat diagnostics.");
        return AccessTools.Method(type, "CreateCommands")
            ?? throw new InvalidOperationException("SAIN VisionRaycastJob.CreateCommands not found for passive global combat diagnostics.");
    }

    [PatchPrefix]
    private static void PatchPrefix(out long __state)
    {
        __state = VanguardGlobalCombatProductionDiagnosticsService.GlobalVisionBoundariesEnabled
            ? VanguardGlobalCombatProductionDiagnosticsService.BeginTimedBoundary()
            : 0L;
    }

    [PatchPostfix]
    private static void PatchPostfix(int __1, int __2, long __state)
    {
        if (__state > 0L)
        {
            VanguardGlobalCombatProductionDiagnosticsService.ObserveSainVisionCreate(__1, __2, __state);
        }
    }
}

/// <summary>
/// Passive timing probe around SAIN's completed vision-raycast analysis. Create-to-analyze latency
/// is measured separately from CPU duration so frame/job starvation can be distinguished from a
/// slow result parser.
/// </summary>
internal sealed class VanguardSainVisionAnalyzeHitsDiagnosticsPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.Components.VisionRaycastJob")
            ?? throw new InvalidOperationException("SAIN VisionRaycastJob type not found for passive global combat diagnostics.");
        return AccessTools.Method(type, "AnalyzeHits")
            ?? throw new InvalidOperationException("SAIN VisionRaycastJob.AnalyzeHits not found for passive global combat diagnostics.");
    }

    [PatchPrefix]
    private static void PatchPrefix(out long __state)
    {
        __state = VanguardGlobalCombatProductionDiagnosticsService.GlobalVisionBoundariesEnabled
            ? VanguardGlobalCombatProductionDiagnosticsService.BeginTimedBoundary()
            : 0L;
    }

    [PatchPostfix]
    private static void PatchPostfix(int __2, int __3, long __state)
    {
        if (__state > 0L)
        {
            VanguardGlobalCombatProductionDiagnosticsService.ObserveSainVisionAnalyze(__2, __3, __state);
        }
    }
}

/// <summary>
/// Passive readback probe for the EFT look update performed by SAIN for every bot. Call and updated-
/// enemy counts remain exact. Stopwatch timing is sampled every fourth frame to cap probe overhead.
/// </summary>
internal sealed class VanguardSainBotLookDiagnosticsPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.SAINComponent.Classes.SAINBotLookClass")
            ?? throw new InvalidOperationException("SAIN SAINBotLookClass type not found for passive global combat diagnostics.");
        return AccessTools.Method(type, "UpdateLook", new[] { typeof(float) })
            ?? throw new InvalidOperationException("SAIN SAINBotLookClass.UpdateLook(float) not found for passive global combat diagnostics.");
    }

    [PatchPrefix]
    private static void PatchPrefix(out long __state)
    {
        __state = VanguardGlobalCombatProductionDiagnosticsService.ShouldTimeSainBotLook(Time.frameCount)
            ? VanguardGlobalCombatProductionDiagnosticsService.BeginTimedBoundary()
            : 0L;
    }

    [PatchPostfix]
    private static void PatchPostfix(int __result, long __state)
    {
        if (__state > 0L)
        {
            VanguardGlobalCombatProductionDiagnosticsService.ObserveSainBotLook(__result, __state);
        }
    }
}
#else
namespace Vanguard.Client.Raid.Patches;
internal sealed class VanguardShootDataProductionDiagnosticsPatch { public void Enable() { } }
internal sealed class VanguardShootDataTriggerDiagnosticsPatch { public void Enable() { } }
internal sealed class VanguardInitiateShotProductionDiagnosticsPatch { public void Enable() { } }
internal sealed class VanguardSainVisionCreateCommandsDiagnosticsPatch { public void Enable() { } }
internal sealed class VanguardSainVisionAnalyzeHitsDiagnosticsPatch { public void Enable() { } }
internal sealed class VanguardSainBotLookDiagnosticsPatch { public void Enable() { } }
#endif
