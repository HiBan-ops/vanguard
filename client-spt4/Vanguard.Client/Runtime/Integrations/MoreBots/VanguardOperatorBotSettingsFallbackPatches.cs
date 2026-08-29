using System;
using System.Reflection;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: Bridges EFT/SPT/Fika callbacks into Operator Bot Settings Fallback Patches for the MoreBots integration.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.Runtime.Integrations.MoreBots;

/// <summary>
/// Vanguard boot/runtime compatibility bridge for Vanguard custom WildSpawnTypes.
///
/// MoreBotsAPI correctly creates the vanguardOperatorUSEC/BEAR enum values, but Fika and SPT
/// still enumerate bot settings by raw WildSpawnType during early boot and raid brain selection.
/// Until Vanguard ships full server-side custom bot type data, these patches keep the custom role
/// boundary while borrowing vanilla PMC settings where external caches require them.
/// </summary>
internal static class VanguardOperatorBotSettingsFallback
{
    public const string StatusTag = "VANGUARD_BOT_SETTINGS_FALLBACK_STATUS";

#if SPT_CLIENT
    public static bool IsVanguardUsec(WildSpawnType role)
    {
        return (int)role == VanguardOperatorBotTypes.UsecRoleValue
            || string.Equals(role.ToString(), VanguardOperatorBotTypes.UsecRoleName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVanguardBear(WildSpawnType role)
    {
        return (int)role == VanguardOperatorBotTypes.BearRoleValue
            || string.Equals(role.ToString(), VanguardOperatorBotTypes.BearRoleName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryResolveVanillaDifficultyRole(WildSpawnType role, out string vanillaRoleKey, out WildSpawnType vanillaRole)
    {
        if (IsVanguardUsec(role))
        {
            vanillaRoleKey = "pmcusec";
            vanillaRole = WildSpawnType.pmcUSEC;
            return true;
        }

        if (IsVanguardBear(role))
        {
            vanillaRoleKey = "pmcbear";
            vanillaRole = WildSpawnType.pmcBEAR;
            return true;
        }

        vanillaRoleKey = string.Empty;
        vanillaRole = role;
        return false;
    }
#endif
}

#if SPT_CLIENT
internal sealed class VanguardFikaBotDifficultiesFallbackPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var type = AccessTools.TypeByName("Fika.Core.Main.Custom.BotDifficulties")
            ?? throw new InvalidOperationException("Fika.Core.Main.Custom.BotDifficulties not found for the Vanguard Operator bot-settings fallback patch.");

        return AccessTools.Method(type, "GetComponent")
            ?? throw new InvalidOperationException("Fika BotDifficulties.GetComponent not found for the Vanguard Operator bot-settings fallback patch.");
    }

    [PatchPrefix]
    private static bool PatchPrefix(object __instance, BotDifficulty botDifficulty, WildSpawnType role, ref BotSettingsComponents __result)
    {
        try
        {
            if (!VanguardOperatorBotSettingsFallback.TryResolveVanillaDifficultyRole(role, out string vanillaRoleKey, out _))
            {
                return true;
            }

            if (TryGetFikaComponent(__instance, vanillaRoleKey, botDifficulty.ToString().ToLowerInvariant(), out var exact))
            {
                __result = exact;
                LogOnce($"fika_difficulty_fallback role={role}; source={vanillaRoleKey}; difficulty={botDifficulty}; result=exact; tag={VanguardOperatorBotSettingsFallback.StatusTag}");
                return false;
            }

            if (TryGetFikaComponent(__instance, vanillaRoleKey, "normal", out var normal))
            {
                __result = normal;
                LogOnce($"fika_difficulty_fallback role={role}; source={vanillaRoleKey}; difficulty={botDifficulty}; result=normal_fallback; tag={VanguardOperatorBotSettingsFallback.StatusTag}");
                return false;
            }
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardOperatorBotSettingsFallback.StatusTag,
                $"fika_difficulty_fallback_failed role={role}; difficulty={botDifficulty}; exception={exception.GetType().Name}:{exception.Message}; action=original; tag={VanguardOperatorBotSettingsFallback.StatusTag}");
        }

        return true;
    }

    private static bool TryGetFikaComponent(object instance, string roleKey, string difficultyKey, out BotSettingsComponents component)
    {
        component = null!;
        var instanceType = instance.GetType();
        var roleDataType = AccessTools.Inner(instanceType, "RoleData")
            ?? throw new InvalidOperationException("Fika BotDifficulties.RoleData not found.");

        var tryGetRole = FindTryGetValue(instanceType, roleDataType.MakeByRefType());
        if (tryGetRole == null)
        {
            return false;
        }

        object?[] roleArgs = { roleKey, null };
        if (!InvokeTryGetValue(tryGetRole, instance, roleArgs))
        {
            return false;
        }

        var roleData = roleArgs[1];
        if (roleData == null)
        {
            return false;
        }

        var tryGetDifficulty = FindTryGetValue(roleData.GetType(), typeof(BotSettingsComponents).MakeByRefType());
        if (tryGetDifficulty == null)
        {
            return false;
        }

        object?[] difficultyArgs = { difficultyKey, null };
        if (!InvokeTryGetValue(tryGetDifficulty, roleData, difficultyArgs))
        {
            return false;
        }

        if (difficultyArgs[1] is BotSettingsComponents resolved)
        {
            component = resolved;
            return true;
        }

        return false;
    }

    private static MethodInfo? FindTryGetValue(Type type, Type byRefValueType)
    {
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!string.Equals(method.Name, "TryGetValue", StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 2
                && parameters[0].ParameterType == typeof(string)
                && parameters[1].ParameterType == byRefValueType)
            {
                return method;
            }
        }

        return null;
    }

    private static bool InvokeTryGetValue(MethodInfo method, object instance, object?[] args)
    {
        var result = method.Invoke(instance, args);
        return result is bool value && value;
    }

    private static DateTimeOffset _lastLogAt = DateTimeOffset.MinValue;
    private static void LogOnce(string message)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastLogAt).TotalSeconds < 10)
        {
            return;
        }

        _lastLogAt = now;
        VanguardClientDiagnosticsLog.Info(VanguardOperatorBotSettingsFallback.StatusTag, message);
    }
}

internal sealed class VanguardSptCustomAiVanguardRoleBypassPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var type = AccessTools.TypeByName("SPT.Custom.CustomAI.AIBrainSpawnWeightAdjustment")
            ?? throw new InvalidOperationException("SPT.Custom.CustomAI.AIBrainSpawnWeightAdjustment not found for the Vanguard Operator bot-settings fallback patch.");

        return AccessTools.Method(type, "GetPmcWildSpawnType")
            ?? throw new InvalidOperationException("AIBrainSpawnWeightAdjustment.GetPmcWildSpawnType not found for the Vanguard Operator bot-settings fallback patch.");
    }

    [PatchPrefix]
    private static bool PatchPrefix(BotOwner botOwner_0, WildSpawnType pmcType, ref WildSpawnType __result)
    {
        if (!VanguardOperatorBotSettingsFallback.TryResolveVanillaDifficultyRole(pmcType, out string vanillaRoleKey, out _))
        {
            return true;
        }

        __result = pmcType;
        VanguardClientDiagnosticsLog.Info(
            VanguardOperatorBotSettingsFallback.StatusTag,
            $"spt_custom_ai_role_bypass operator={botOwner_0?.Profile?.Info?.Nickname ?? "unknown"}; role={pmcType}; source={vanillaRoleKey}; action=preserve_vanguard_custom_role; tag={VanguardOperatorBotSettingsFallback.StatusTag}");
        return false;
    }
}
#else
internal sealed class VanguardFikaBotDifficultiesFallbackPatch { public void Enable() { } }
internal sealed class VanguardSptCustomAiVanguardRoleBypassPatch { public void Enable() { } }
#endif
