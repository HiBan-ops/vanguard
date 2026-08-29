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
