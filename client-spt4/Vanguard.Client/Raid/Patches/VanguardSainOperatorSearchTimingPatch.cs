using System;
using System.Reflection;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Integrations.Sain;

#if SPT_CLIENT
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: Bridges EFT/SPT/Fika callbacks into Sain Operator Search Timing Patch for the raid lifecycle patch bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.Raid.Patches;

#if SPT_CLIENT
/// <summary>
/// The runtime exact runtime invariant. SAIN recalculates TimeBeforeSearch whenever a new search phase
/// starts, so the spawn-time independent profile alone cannot guarantee the player-facing cadence.
/// This postfix clamps only registered Vanguard Operators after every native calculation and keeps
/// SAIN's correlated enemy-forget horizon aligned with the clamped search delay.
/// </summary>
internal sealed class VanguardSainOperatorSearchTimingPatch : ModulePatch
{
    private const string TargetTypeName = "SAIN.SAINComponent.Classes.Info.SAINBotInfoClass";
    private const string TargetMethodName = "CalcTimeBeforeSearch";

    protected override MethodBase GetTargetMethod()
    {
        Type targetType = AccessTools.TypeByName(TargetTypeName)
            ?? throw new InvalidOperationException(TargetTypeName + " not found for Vanguard Operator search timing clamp.");
        MethodInfo method = AccessTools.Method(targetType, TargetMethodName)
            ?? throw new InvalidOperationException(TargetTypeName + "." + TargetMethodName + " not found for Vanguard Operator search timing clamp.");
        VanguardClientDiagnosticsLog.Info(
            VanguardSainStaticProfilePolicy.RuntimeSearchTimingBindTag,
            $"type={targetType.FullName}; method={method.Name}; postfix=true; searchBaseTime={VanguardSainStaticProfilePolicy.OperatorSearchBaseTimeSeconds:0.00}; clampMin={VanguardSainStaticProfilePolicy.OperatorMinimumTimeBeforeSearchSeconds:0.00}; clampMax={VanguardSainStaticProfilePolicy.OperatorMaximumTimeBeforeSearchSeconds:0.00}; operatorsOnly=true; nonOperatorsChanged=false; tag={VanguardSainStaticProfilePolicy.OperatorSearchCadenceStatusTag}");
        return method;
    }

    [PatchPostfix]
    private static void PatchPostfix(object __instance)
    {
        try
        {
            if (VanguardSainStaticProfileService.TryEnforceRuntimeSearchTiming(
                    __instance,
                    "SAINBotInfoClass.CalcTimeBeforeSearch",
                    out string summary))
            {
                VanguardClientDiagnosticsLog.Info(
                    VanguardSainStaticProfilePolicy.RuntimeSearchTimingAppliedTag,
                    summary);
            }
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardSainStaticProfilePolicy.RuntimeSearchTimingFailedTag,
                $"reason={exception.GetType().Name}:{exception.Message}; failOpen=true; operatorsOnly=true; nonOperatorsChanged=false; tag={VanguardSainStaticProfilePolicy.OperatorSearchCadenceStatusTag}");
        }
    }
}
#else
internal sealed class VanguardSainOperatorSearchTimingPatch
{
    public void Enable() { }
}
#endif

#if SPT_CLIENT
// One write-authority is preserved: both hooks observe existing SAIN decisions without suppressing original execution.
// SAINMoverClass.TriggerNewMove is the common physical path-admission point, including rush/search and cover moves.
internal sealed class VanguardSainRouteSubmissionObservationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.SAINComponent.Classes.Mover.SAINMoverClass")
            ?? throw new InvalidOperationException("SAINMoverClass unavailable for route observation.");
        MethodInfo method = AccessTools.Method(type, "TriggerNewMove")
            ?? throw new InvalidOperationException("SAINMoverClass.TriggerNewMove unavailable for route observation.");
        if (method.GetParameters().Length != 5 || method.ReturnType != typeof(void))
        {
            throw new InvalidOperationException("Unexpected SAIN TriggerNewMove signature; observation disabled.");
        }
        VanguardClientDiagnosticsLog.Info(
            Vanguard.Client.Runtime.SquadCohesion.SainCrossAuthorityRouteObservationService.StatusTag,
            "SAIN_ROUTE_OBSERVATION_BIND hook=SAINMoverClass.TriggerNewMove; shadow=true; mutation=false");
        return method;
    }

    [PatchPrefix]
    private static void PatchPrefix(object __instance, UnityEngine.Vector3[] pathCorners,
        UnityEngine.Vector3 point, bool shallSprint)
    {
        Vanguard.Client.Runtime.SquadCohesion.SainCrossAuthorityRouteObservationService.ObserveNewPath(
            __instance, pathCorners, point, shallSprint);
    }
}

// Low-frequency, post-TickPath witness of live path state and real operator position.
// Never modifies the native result and cannot cancel a path or arbitrate an owner.
internal sealed class VanguardSainPhysicalPathObservationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.SAINComponent.Classes.Mover.SAINMoverClass")
            ?? throw new InvalidOperationException("SAINMoverClass unavailable for physical trace observation.");
        MethodInfo method = AccessTools.Method(type, "CheckTickPath")
            ?? throw new InvalidOperationException("SAINMoverClass.CheckTickPath unavailable for physical trace observation.");
        if (method.GetParameters().Length != 0 || method.ReturnType != typeof(bool))
        {
            throw new InvalidOperationException("Unexpected SAIN CheckTickPath signature; physical trace observation disabled.");
        }
        VanguardClientDiagnosticsLog.Info(
            Vanguard.Client.Runtime.SquadCohesion.SainCrossAuthorityRouteObservationService.StatusTag,
            "SAIN_PHYSICAL_PATH_OBSERVATION_BIND hook=SAINMoverClass.CheckTickPath; postfix=true; shadow=true; mutation=false");
        return method;
    }

    [PatchPostfix]
    private static void PatchPostfix(object __instance, bool __result)
    {
        Vanguard.Client.Runtime.SquadCohesion.SainCrossAuthorityRouteObservationService.ObservePhysicalPathTick(
            __instance, __result);
    }
}

// Observe SAIN's fully updated Search path before SwitchSearchModes can submit or reuse it.
// This is deliberately a read-only advisory boundary, not an unsafe physical-path veto.
internal sealed class VanguardSainSearchIntentCoordinationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.SAINComponent.Classes.Search.SAINSearchClass")
            ?? throw new InvalidOperationException("SAINSearchClass unavailable for search-intent observation.");
        MethodInfo? method = AccessTools.Method(type, "SwitchSearchModes");
        ParameterInfo[] parameters = method?.GetParameters() ?? Array.Empty<ParameterInfo>();
        if (method == null || method.IsStatic || method.ReturnType != typeof(void)
            || parameters.Length != 2 || parameters[0].ParameterType != typeof(bool)
            || parameters[1].ParameterType.FullName != "SAIN.SAINComponent.Classes.EnemyClasses.Enemy")
        {
            throw new InvalidOperationException("Unexpected SAIN SwitchSearchModes signature; search-intent hook disabled.");
        }
        VanguardClientDiagnosticsLog.Info(
            Vanguard.Client.Runtime.SquadCohesion.SainSearchIntentCoordinationService.StatusTag,
            "SAIN_SEARCH_INTENT_BIND hook=SAINSearchClass.SwitchSearchModes; beforeMovement=true; adviceOnly=true; nativeSainPreserved=true");
        return method;
    }

    [PatchPrefix]
    private static void PatchPrefix(object __instance, bool shallSprint, object enemy)
    {
        try
        {
            Vanguard.Client.Runtime.SquadCohesion.SainSearchIntentCoordinationService.ObserveBeforeNativeSearchMovement(
                __instance, enemy, shallSprint);
            Vanguard.Client.Runtime.SquadCohesion.SainSearchNativePathArbitrationService.BeginSearchWindow(
                __instance, enemy);
        }
        catch
        {
            // A failed advisory bridge must never interrupt the native SAIN search action.
        }
    }
    [PatchPostfix]
    private static void PatchPostfix()
    {
        Vanguard.Client.Runtime.SquadCohesion.SainSearchNativePathArbitrationService.EndSearchWindow();
    }

    // Always releases the thread-local context even when SAIN's native Search throws.
    [PatchFinalizer]
    private static Exception? PatchFinalizer(Exception? __exception)
    {
        Vanguard.Client.Runtime.SquadCohesion.SainSearchNativePathArbitrationService.EndSearchWindow();
        return __exception; // never suppress SAIN exceptions
    }

}

// Reused path skips TriggerNewMove; observe the successful no-replan return independently.
internal sealed class VanguardSainPathReuseObservationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.SAINComponent.Classes.Mover.SAINMoverClass")
            ?? throw new InvalidOperationException("SAINMoverClass unavailable for path reuse observation.");
        MethodInfo method = AccessTools.Method(type, "TryUpdatePath")
            ?? throw new InvalidOperationException("SAINMoverClass.TryUpdatePath unavailable for path reuse observation.");
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length != 1 || parameters[0].ParameterType != typeof(UnityEngine.Vector3)
            || method.ReturnType != typeof(bool))
        {
            throw new InvalidOperationException("Unexpected SAIN TryUpdatePath signature; observation disabled.");
        }
        VanguardClientDiagnosticsLog.Info(
            Vanguard.Client.Runtime.SquadCohesion.SainCrossAuthorityRouteObservationService.StatusTag,
            "SAIN_PATH_REUSE_OBSERVATION_BIND hook=SAINMoverClass.TryUpdatePath; shadow=true; mutation=false");
        return method;
    }

    [PatchPostfix]
    private static void PatchPostfix(object __instance, UnityEngine.Vector3 point, bool __result)
    {
        Vanguard.Client.Runtime.SquadCohesion.SainCrossAuthorityRouteObservationService.ObservePathReuse(
            __instance, point, __result);
    }
}

// DogFight may stop, back up, or approach a target without any selected cover.
internal sealed class VanguardSainDogFightIntentObservationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.SAINComponent.Classes.Mover.DogFight")
            ?? throw new InvalidOperationException("SAIN DogFight unavailable for intent observation.");
        MethodInfo method = AccessTools.Method(type, "DogFightMove")
            ?? throw new InvalidOperationException("DogFight.DogFightMove unavailable for intent observation.");
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length != 2 || parameters[0].ParameterType != typeof(bool)
            || method.ReturnType != typeof(void))
        {
            throw new InvalidOperationException("Unexpected SAIN DogFightMove signature; observation disabled.");
        }
        VanguardClientDiagnosticsLog.Info(
            Vanguard.Client.Runtime.SquadCohesion.SainCrossAuthorityRouteObservationService.StatusTag,
            "SAIN_DOGFIGHT_OBSERVATION_BIND hook=DogFight.DogFightMove; shadow=true; mutation=false");
        return method;
    }

    [PatchPrefix]
    private static void PatchPrefix(object __instance, bool aggressive, object Enemy)
    {
        Vanguard.Client.Runtime.SquadCohesion.SainCrossAuthorityRouteObservationService.ObserveDogFightIntent(
            __instance, aggressive, Enemy);
    }
}

internal sealed class VanguardSainCoverSelectionObservationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.SAINComponent.Classes.SAINCoverClass")
            ?? throw new InvalidOperationException("SAINCoverClass unavailable for cover observation.");
        MethodInfo method = AccessTools.Method(type, "FindCoverPoint")
            ?? throw new InvalidOperationException("SAINCoverClass.FindCoverPoint unavailable for cover observation.");
        if (method.GetParameters().Length != 0 || method.ReturnType == typeof(void))
        {
            throw new InvalidOperationException("Unexpected SAIN FindCoverPoint signature; observation disabled.");
        }
        VanguardClientDiagnosticsLog.Info(
            Vanguard.Client.Runtime.SquadCohesion.SainCrossAuthorityRouteObservationService.StatusTag,
            "SAIN_COVER_OBSERVATION_BIND hook=SAINCoverClass.FindCoverPoint; shadow=true; mutation=false");
        return method;
    }

    [PatchPostfix]
    private static void PatchPostfix(object __instance, object __result)
    {
        Vanguard.Client.Runtime.SquadCohesion.SainCrossAuthorityRouteObservationService.ObserveCoverSelection(
            __instance, __result);
    }
}
// Exact native Search path argument admission, not a global movement veto.
// These patches do not skip originals, rewrite results, or touch DogFight / cover calls.
internal sealed class VanguardSainNativeWalkByWayArbitrationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.SAINComponent.Classes.Mover.SAINMoverClass")
            ?? throw new InvalidOperationException("SAINMoverClass unavailable.");
        MethodInfo method = AccessTools.Method(type, "WalkToPointByWay")
            ?? throw new InvalidOperationException("WalkToPointByWay unavailable.");
        ParameterInfo[] ps = method.GetParameters();
        if (method.ReturnType != typeof(bool) || ps.Length != 4
            || ps[0].ParameterType != typeof(UnityEngine.AI.NavMeshPath)
            || ps[1].ParameterType != typeof(bool) || ps[2].ParameterType != typeof(float)
            || ps[3].ParameterType != typeof(bool))
        {
            throw new InvalidOperationException("SAIN WalkToPointByWay signature drift.");
        }
        return method;
    }

    [PatchPrefix]
    private static void PatchPrefix(object __instance, ref UnityEngine.AI.NavMeshPath path, ref bool checkSameWay)
    {
        try
        {
            bool forceNew = false;
            Vanguard.Client.Runtime.SquadCohesion.SainSearchNativePathArbitrationService.ReviewNativeSearchPath(
                __instance, ref path, ref forceNew, "WalkToPointByWay");
            if (forceNew) checkSameWay = false;
        }
        catch { /* no interference with SAIN native movement */ }
    }
}

internal sealed class VanguardSainNativeRunByWayArbitrationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type type = AccessTools.TypeByName("SAIN.SAINComponent.Classes.Mover.SAINMoverClass")
            ?? throw new InvalidOperationException("SAINMoverClass unavailable.");
        MethodInfo method = AccessTools.Method(type, "RunToPointByWay")
            ?? throw new InvalidOperationException("RunToPointByWay unavailable.");
        ParameterInfo[] ps = method.GetParameters();
        if (method.ReturnType != typeof(bool) || ps.Length != 5
            || ps[0].ParameterType != typeof(UnityEngine.AI.NavMeshPath)
            || ps[1].ParameterType != typeof(bool) || ps[2].ParameterType != typeof(float)
            || ps[3].ParameterType.FullName != "SAIN.SAINComponent.Classes.Mover.ESprintUrgency"
            || ps[4].ParameterType != typeof(bool))
        {
            throw new InvalidOperationException("SAIN RunToPointByWay signature drift.");
        }
        return method;
    }

    [PatchPrefix]
    private static void PatchPrefix(object __instance, ref UnityEngine.AI.NavMeshPath path, ref bool checkSameWay)
    {
        try
        {
            bool forceNew = false;
            Vanguard.Client.Runtime.SquadCohesion.SainSearchNativePathArbitrationService.ReviewNativeSearchPath(
                __instance, ref path, ref forceNew, "RunToPointByWay");
            if (forceNew) checkSameWay = false;
        }
        catch { /* no interference with SAIN native movement */ }
    }
}

#endif

#if SPT_CLIENT
// Observe whether native EFT BotMover code actually executed after SAIN/other prefixes.
// __runOriginal is a read-only Harmony injection. This patch cannot grant or revoke movement.
internal sealed class VanguardNativeEftTickObservationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        MethodInfo method = AccessTools.Method(typeof(global::BotMover), nameof(global::BotMover.ManualFixedUpdate))
            ?? throw new InvalidOperationException("EFT BotMover.ManualFixedUpdate not found; probe disabled.");
        if (method.IsStatic || method.ReturnType != typeof(void) || method.GetParameters().Length != 0)
        {
            throw new InvalidOperationException("EFT BotMover signature drift; probe disabled.");
        }
        VanguardClientDiagnosticsLog.Info(
            Vanguard.Client.Runtime.Movement.Diagnostics.VanguardNativeEftTickObservationService.StatusTag,
            "EFT_NATIVE_TICK_OBSERVATION_BIND target=BotMover.ManualFixedUpdate; postfix=true; harmonyRunOriginal=true; observerOnly=true; traceOnly=true; movementMutation=false");
        return method;
    }

    [PatchPostfix]
    private static void PatchPostfix(global::BotMover __instance, bool __runOriginal)
    {
        try
        {
            Vanguard.Client.Runtime.Movement.Diagnostics.VanguardNativeEftTickObservationService.Observe(__instance, __runOriginal);
        }
        catch
        {
            // The diagnostic must never interrupt the original EFT or SAIN locomotion call.
        }
    }
}
#endif
