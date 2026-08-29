using System;
using System.Reflection;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.External;

#if SPT_CLIENT
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: Bridges EFT/SPT/Fika callbacks into Sain Autonomous Extract Veto Patch for the raid lifecycle patch bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.Raid.Patches;

#if SPT_CLIENT
/// <summary>
/// Narrow capability veto: only Vanguard-bound Operators are denied SAIN's generic PMC
/// extraction capability. Non-Vanguard PMCs and Scavs retain native SAIN behavior.
/// </summary>
internal sealed class VanguardSainAutonomousExtractVetoPatch : ModulePatch
{
    private const string TargetTypeName = "SAIN.Components.BotController.BotExtractManager";
    private const string TargetMethodName = "IsBotAllowedToExfil";

    protected override MethodBase GetTargetMethod()
    {
        Type targetType = AccessTools.TypeByName(TargetTypeName)
            ?? throw new InvalidOperationException(TargetTypeName + " not found for Vanguard autonomous extract veto.");
        MethodInfo method = AccessTools.Method(targetType, TargetMethodName)
            ?? throw new InvalidOperationException(TargetTypeName + "." + TargetMethodName + " not found for Vanguard autonomous extract veto.");
        VanguardClientDiagnosticsLog.Info(
            VanguardCombatTruthStatusTags.ExtractGuardOneShot,
            $"VANGUARD_SAIN_EXTRACT_VETO_BIND_OK type={targetType.FullName}; method={method.Name}; returnType={method.ReturnType.FullName}; parameterCount={method.GetParameters().Length}; operatorsOnly=true; tag={VanguardCombatTruthStatusTags.ExtractGuardOneShot}");
        return method;
    }

    [PatchPrefix]
    private static bool PatchPrefix([HarmonyArgument(0)] object bot, ref bool __result)
    {
        try
        {
            if (VanguardSainAutonomousExtractGuardService.ShouldDenyAutonomousExtract(bot, out var botProfileId))
            {
                __result = false;
                VanguardSainAutonomousExtractGuardService.RecordPermissionVeto(bot, botProfileId, "BotExtractManager.IsBotAllowedToExfil", DateTimeOffset.UtcNow);
                return false;
            }
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardSainAutonomousExtractGuardService.StatusTag,
                $"VANGUARD_SAIN_AUTONOMOUS_EXTRACT_VETO_EXCEPTION reason={exception.GetType().Name}:{exception.Message}; failOpenForNonOperators=true; tag={VanguardSainAutonomousExtractGuardService.StatusTag}");
        }

        return true;
    }
}

/// <summary>
/// Authoritative layer gate. Patching the layer itself prevents SAIN from rebuilding an
/// extraction authority after Vanguard has cleared its memory fields.
/// </summary>
internal sealed class VanguardSainExtractLayerIsActiveVetoPatch : ModulePatch
{
    private const string TargetTypeName = "SAIN.Layers.ExtractLayer";

    protected override MethodBase GetTargetMethod()
    {
        Type targetType = AccessTools.TypeByName(TargetTypeName)
            ?? throw new InvalidOperationException(TargetTypeName + " not found for Vanguard extract layer veto.");
        MethodInfo method = AccessTools.Method(targetType, "IsActive")
            ?? throw new InvalidOperationException(TargetTypeName + ".IsActive not found for Vanguard extract layer veto.");
        VanguardClientDiagnosticsLog.Info(VanguardRuntimeConvergenceStatusTags.SainLayerVeto,
            $"VANGUARD_SAIN_LAYER_VETO_BIND_OK type={targetType.FullName}; method={method.Name}; layer=extract; operatorsOnly=true; tag={VanguardRuntimeConvergenceStatusTags.SainLayerVeto}");
        return method;
    }

    [PatchPrefix]
    private static bool PatchPrefix(object __instance, ref bool __result)
    {
        return VanguardSainAutonomousExtractGuardService.TryVetoLayerIsActive(__instance, "ExtractLayer.IsActive", ref __result);
    }
}

/// <summary>
/// Some SAIN releases expose the same extraction implementation through PeacefulLayer.
/// This second exact gate closes that equivalent entry point without altering normal peace AI.
/// </summary>
internal sealed class VanguardSainPeacefulLayerIsActiveVetoPatch : ModulePatch
{
    private const string TargetTypeName = "SAIN.Layers.Peace.PeacefulLayer";

    protected override MethodBase GetTargetMethod()
    {
        Type targetType = AccessTools.TypeByName(TargetTypeName)
            ?? throw new InvalidOperationException(TargetTypeName + " not found for Vanguard peaceful extract layer veto.");
        MethodInfo method = AccessTools.Method(targetType, "IsActive")
            ?? throw new InvalidOperationException(TargetTypeName + ".IsActive not found for Vanguard peaceful extract layer veto.");
        VanguardClientDiagnosticsLog.Info(VanguardRuntimeConvergenceStatusTags.SainLayerVeto,
            $"VANGUARD_SAIN_LAYER_VETO_BIND_OK type={targetType.FullName}; method={method.Name}; layer=peaceful_extract; operatorsOnly=true; tag={VanguardRuntimeConvergenceStatusTags.SainLayerVeto}");
        return method;
    }

    [PatchPrefix]
    private static bool PatchPrefix(object __instance, ref bool __result)
    {
        return VanguardSainAutonomousExtractGuardService.TryVetoLayerIsActive(__instance, "PeacefulLayer.IsActive", ref __result);
    }
}


/// <summary>
/// Exact EFT PMC layer veto. MoreBots keeps the PmcUsec/PmcBear brain for Vanguard Operators,
/// so the native obfuscated Exfiltration layer can coexist with SAIN. This prefix prevents that
/// layer from ever becoming current while leaving every combat layer untouched.
/// </summary>
internal sealed class VanguardNativePmcExfiltrationLayerVetoPatch : ModulePatch
{
    private const string TargetTypeName = "GClass75";
    protected override MethodBase GetTargetMethod()
    {
        Type targetType = AccessTools.TypeByName(TargetTypeName)
            ?? throw new InvalidOperationException(TargetTypeName + " native Exfiltration layer not found.");
        MethodInfo method = AccessTools.Method(targetType, "ShallUseNow")
            ?? throw new InvalidOperationException(TargetTypeName + ".ShallUseNow not found.");
        VanguardClientDiagnosticsLog.Info(VanguardRuntimeConvergenceStatusTags.SainLayerVeto,
            $"VANGUARD_NATIVE_EXFIL_LAYER_BIND_OK type={targetType.FullName}; method={method.Name}; layer=Exfiltration; node=goToExfiltrationPointNode; operatorsOnly=true; tag={VanguardRuntimeConvergenceStatusTags.SainLayerVeto}");
        return method;
    }

    [PatchPrefix]
    private static bool PatchPrefix(object __instance, ref bool __result)
    {
        return VanguardSainAutonomousExtractGuardService.TryVetoNativeExfiltrationLayer(__instance, "GClass75.ShallUseNow", ref __result);
    }
}

/// <summary>
/// Prevents SAIN's per-bot/squad random timer refresh from re-enabling extraction after the
/// initial one-shot hardening. Combat settings and PMC identity are untouched.
/// </summary>
internal sealed class VanguardSainExtractTimeUpdateVetoPatch : ModulePatch
{
    private const string TargetTypeName = "SAIN.SAINComponent.Classes.Info.SAINBotInfoClass";
    private const string TargetMethodName = "UpdateExtractTime";

    protected override MethodBase GetTargetMethod()
    {
        Type targetType = AccessTools.TypeByName(TargetTypeName)
            ?? throw new InvalidOperationException(TargetTypeName + " not found for Vanguard extract-time veto.");
        MethodInfo method = AccessTools.Method(targetType, TargetMethodName)
            ?? throw new InvalidOperationException(TargetTypeName + "." + TargetMethodName + " not found for Vanguard extract-time veto.");
        VanguardClientDiagnosticsLog.Info(VanguardRuntimeConvergenceStatusTags.SainExtractTimeVeto,
            $"VANGUARD_SAIN_EXTRACT_TIME_VETO_BIND_OK type={targetType.FullName}; method={method.Name}; operatorsOnly=true; tag={VanguardRuntimeConvergenceStatusTags.SainExtractTimeVeto}");
        return method;
    }

    [PatchPrefix]
    private static bool PatchPrefix(object __instance)
    {
        return !VanguardSainAutonomousExtractGuardService.TryVetoExtractTimeRefresh(__instance, "SAINBotInfoClass.UpdateExtractTime", DateTimeOffset.UtcNow);
    }
}
#else
internal sealed class VanguardSainAutonomousExtractVetoPatch { public void Enable() { } }
internal sealed class VanguardSainExtractLayerIsActiveVetoPatch { public void Enable() { } }
internal sealed class VanguardSainPeacefulLayerIsActiveVetoPatch { public void Enable() { } }
internal sealed class VanguardSainExtractTimeUpdateVetoPatch { public void Enable() { } }
internal sealed class VanguardNativePmcExfiltrationLayerVetoPatch { public void Enable() { } }
#endif
