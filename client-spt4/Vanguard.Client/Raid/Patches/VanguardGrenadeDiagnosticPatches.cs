#if SPT_CLIENT
using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using Vanguard.Client.Runtime.Grenades;

// Responsibility: Bridges EFT/SPT/Fika callbacks into Grenade Diagnostic Patches for the raid lifecycle patch bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.Raid.Patches;

/// <summary>
/// Passive hooks for grenade subsystem. Every patch only forwards already-available runtime evidence to the
/// grenade audit service. No prefix suppresses an original method and no argument is mutated.
/// </summary>
internal sealed class VanguardSainGrenadeThrownDiagnosticPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.Components.GrenadeController")
            ?? throw new InvalidOperationException("SAIN GrenadeController type not found for grenade subsystem diagnostics.");
        return AccessTools.Method(type, "GrenadeThrown", new[] { typeof(Grenade), typeof(Vector3), typeof(Vector3), typeof(float) })
            ?? throw new InvalidOperationException("SAIN GrenadeController.GrenadeThrown not found for grenade subsystem diagnostics.");
    }

    [PatchPrefix]
    private static void PatchPrefix(Grenade __0, Vector3 __1, Vector3 __2, float __3)
        => VanguardGrenadeHazardAuditService.ObserveThrow(__0, __1, __2, __3);
}

internal sealed class VanguardSainGrenadeExplosionDiagnosticPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.Components.GrenadeController")
            ?? throw new InvalidOperationException("SAIN GrenadeController type not found for grenade subsystem explosion diagnostics.");
        return AccessTools.Method(
                type,
                "GrenadeExplosion",
                new[] { typeof(Vector3), typeof(string), typeof(bool), typeof(float), typeof(float), typeof(int) })
            ?? throw new InvalidOperationException("SAIN GrenadeController.GrenadeExplosion not found for grenade subsystem diagnostics.");
    }

    [PatchPostfix]
    private static void PatchPostfix(Vector3 __0, string __1, bool __2, int __5)
    {
        if (!__2)
        {
            VanguardGrenadeHazardAuditService.ObserveExplosion(__0, __1, __5);
        }
    }
}

internal sealed class VanguardSainGrenadeCollisionDiagnosticPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.Components.GrenadeController")
            ?? throw new InvalidOperationException("SAIN GrenadeController type not found for grenade subsystem collision diagnostics.");
        return AccessTools.Method(type, "GrenadeCollided", new[] { typeof(Grenade), typeof(float) })
            ?? throw new InvalidOperationException("SAIN GrenadeController.GrenadeCollided not found for grenade subsystem diagnostics.");
    }

    [PatchPostfix]
    private static void PatchPostfix(Grenade __0, float __1)
        => VanguardGrenadeHazardAuditService.ObserveCollision(__0, __1);
}

internal sealed class VanguardSainGrenadeReactionDiagnosticPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.SAINComponent.Classes.WeaponFunction.GrenadeReactionClass")
            ?? throw new InvalidOperationException("SAIN GrenadeReactionClass type not found for grenade subsystem diagnostics.");
        return AccessTools.Method(type, "EnemyGrenadeThrown", new[] { typeof(Grenade), typeof(Vector3), typeof(string) })
            ?? throw new InvalidOperationException("SAIN GrenadeReactionClass.EnemyGrenadeThrown not found for grenade subsystem diagnostics.");
    }

    [PatchPrefix]
    private static void PatchPrefix(object __instance, Grenade __0, Vector3 __1, string __2)
        => VanguardGrenadeHazardAuditService.ObserveSainReaction(__instance, __0, __1, __2, afterCall: false);

    [PatchPostfix]
    private static void PatchPostfix(object __instance, Grenade __0, Vector3 __1, string __2)
        => VanguardGrenadeHazardAuditService.ObserveSainReaction(__instance, __0, __1, __2, afterCall: true);
}

internal sealed class VanguardSainGrenadeDangerUpdateDiagnosticPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.SAINComponent.SubComponents.GrenadeTrackerClass")
            ?? throw new InvalidOperationException("SAIN GrenadeTrackerClass type not found for grenade subsystem danger-update diagnostics.");
        return AccessTools.Method(type, "UpdateGrenadeDanger", new[] { typeof(Vector3) })
            ?? throw new InvalidOperationException("SAIN GrenadeTrackerClass.UpdateGrenadeDanger not found for grenade subsystem diagnostics.");
    }

    [PatchPostfix]
    private static void PatchPostfix(object __instance, Vector3 __0)
        => VanguardGrenadeHazardAuditService.ObserveDangerPointUpdate(
            VanguardGrenadeRuntimeResolver.ResolveGrenade(__instance),
            __0,
            "SAIN.GrenadeTracker.UpdateGrenadeDanger");
}

internal sealed class VanguardSainGrenadeTrackerSpottedDiagnosticPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.SAINComponent.SubComponents.GrenadeTrackerClass")
            ?? throw new InvalidOperationException("SAIN GrenadeTrackerClass type not found for grenade subsystem spotted diagnostics.");
        return AccessTools.Method(type, "SetSpotted", Type.EmptyTypes)
            ?? throw new InvalidOperationException("SAIN GrenadeTrackerClass.SetSpotted not found for grenade subsystem diagnostics.");
    }

    [PatchPostfix]
    private static void PatchPostfix(object __instance)
        => VanguardGrenadeHazardAuditService.ObserveTrackerSpotted(__instance);
}

internal sealed class VanguardSainGrenadeTrackerUpdateDiagnosticPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.SAINComponent.SubComponents.GrenadeTrackerClass")
            ?? throw new InvalidOperationException("SAIN GrenadeTrackerClass type not found for grenade subsystem tracker diagnostics.");
        return AccessTools.Method(type, "Update", Type.EmptyTypes)
            ?? throw new InvalidOperationException("SAIN GrenadeTrackerClass.Update not found for grenade subsystem diagnostics.");
    }

    [PatchPrefix]
    private static void PatchPrefix(object __instance)
        => VanguardGrenadeHazardAuditService.ObserveTrackerUpdate(__instance);

    [PatchPostfix]
    private static void PatchPostfix(object __instance)
        => VanguardGrenadeHazardAuditService.ObserveTrackerUpdate(__instance);
}

internal sealed class VanguardNativeGrenadeDangerDiagnosticPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(
            typeof(BotBewareGrenade),
            nameof(BotBewareGrenade.AddGrenadeDanger),
            new[] { typeof(Vector3), typeof(Grenade) })
        ?? throw new InvalidOperationException("BotBewareGrenade.AddGrenadeDanger not found for grenade subsystem diagnostics.");

    [PatchPrefix]
    private static void PatchPrefix(BotBewareGrenade __instance, Vector3 __0, Grenade __1)
        => VanguardGrenadeHazardAuditService.ObserveNativeDanger(__instance, __0, __1, afterCall: false);

    [PatchPostfix]
    private static void PatchPostfix(BotBewareGrenade __instance, Vector3 __0, Grenade __1)
        => VanguardGrenadeHazardAuditService.ObserveNativeDanger(__instance, __0, __1, afterCall: true);
}

internal sealed class VanguardNativeGrenadeShallRunAwayDiagnosticPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(
            typeof(BotBewareGrenade),
            nameof(BotBewareGrenade.ShallRunAway),
            Type.EmptyTypes)
        ?? throw new InvalidOperationException("BotBewareGrenade.ShallRunAway not found for grenade subsystem diagnostics.");

    [PatchPrefix]
    private static void PatchPrefix(BotBewareGrenade __instance, out VanguardNativeGrenadePatchState __state)
        => __state = VanguardNativeGrenadePatchState.Capture(__instance);

    [PatchPostfix]
    private static void PatchPostfix(BotBewareGrenade __instance, VanguardNativeGrenadePatchState __state, bool __result)
        => VanguardGrenadeHazardAuditService.ObserveNativeShallRunAway(
            __instance,
            __state.Grenade,
            __state.DangerPoint,
            __state.DangerPresent,
            __result);
}

internal sealed class VanguardNativeGrenadeExecutionDiagnosticPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(
            typeof(BotBewareGrenade),
            nameof(BotBewareGrenade.UpdateByNode),
            Type.EmptyTypes)
        ?? throw new InvalidOperationException("BotBewareGrenade.UpdateByNode not found for grenade subsystem diagnostics.");

    [PatchPrefix]
    private static void PatchPrefix(BotBewareGrenade __instance, out VanguardNativeGrenadePatchState __state)
        => __state = VanguardNativeGrenadePatchState.Capture(__instance);

    [PatchPostfix]
    private static void PatchPostfix(BotBewareGrenade __instance, VanguardNativeGrenadePatchState __state)
        => VanguardGrenadeHazardAuditService.ObserveNativeExecution(
            __instance,
            __state.Grenade,
            __state.DangerPoint,
            __state.DangerPresent);
}

internal sealed class VanguardNativeGrenadePatchState
{
    public Grenade? Grenade { get; private init; }
    public Vector3 DangerPoint { get; private init; }
    public bool DangerPresent { get; private init; }

    public static VanguardNativeGrenadePatchState Capture(BotBewareGrenade instance)
    {
        bool present = VanguardGrenadeRuntimeResolver.TryReadNativeDangerState(
            instance,
            out bool dangerPresent,
            out Grenade? grenade,
            out Vector3 dangerPoint) && dangerPresent;
        return new VanguardNativeGrenadePatchState
        {
            Grenade = grenade,
            DangerPoint = dangerPoint,
            DangerPresent = present,
        };
    }
}

internal sealed class VanguardSainGrenadeDecisionDiagnosticPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.SAINComponent.Classes.Decision.BotDecisionManager")
            ?? throw new InvalidOperationException("SAIN BotDecisionManager type not found for grenade subsystem diagnostics.");
        return AccessTools.Method(type, "SetDecisions")
            ?? throw new InvalidOperationException("SAIN BotDecisionManager.SetDecisions not found for grenade subsystem diagnostics.");
    }

    [PatchPostfix]
    private static void PatchPostfix(object __instance, object[] __args)
        => VanguardGrenadeHazardAuditService.ObserveSainDecision(__instance, __args);
}
#else
namespace Vanguard.Client.Raid.Patches;
internal sealed class VanguardSainGrenadeThrownDiagnosticPatch { public void Enable() { } }
internal sealed class VanguardSainGrenadeExplosionDiagnosticPatch { public void Enable() { } }
internal sealed class VanguardSainGrenadeCollisionDiagnosticPatch { public void Enable() { } }
internal sealed class VanguardSainGrenadeReactionDiagnosticPatch { public void Enable() { } }
internal sealed class VanguardSainGrenadeDangerUpdateDiagnosticPatch { public void Enable() { } }
internal sealed class VanguardSainGrenadeTrackerSpottedDiagnosticPatch { public void Enable() { } }
internal sealed class VanguardSainGrenadeTrackerUpdateDiagnosticPatch { public void Enable() { } }
internal sealed class VanguardNativeGrenadeDangerDiagnosticPatch { public void Enable() { } }
internal sealed class VanguardNativeGrenadeShallRunAwayDiagnosticPatch { public void Enable() { } }
internal sealed class VanguardNativeGrenadeExecutionDiagnosticPatch { public void Enable() { } }
internal sealed class VanguardSainGrenadeDecisionDiagnosticPatch { public void Enable() { } }
#endif
