#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Alliance;
using Vanguard.Client.Raid.Runtime;

// Responsibility: Bridges EFT/SPT/Fika callbacks into Coop Friendly Hostility Patches for the raid lifecycle patch bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.Raid.Patches;

internal sealed class VanguardBotMemoryFriendlyGuardPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(BotMemoryClass), nameof(BotMemoryClass.AddEnemy))
            ?? throw new InvalidOperationException("BotMemoryClass.AddEnemy not found for Vanguard coop alliance guard.");
    }

    [PatchPrefix]
    private static bool PatchPrefix(BotMemoryClass __instance, IPlayer enemy)
    {
        string? actorProfileId = __instance?.BotOwner_0?.ProfileId;
        string? targetProfileId = enemy?.ProfileId;
        if (!VanguardFriendlyIdentityRegistry.ShouldProtectFromVanguardOperator(actorProfileId, targetProfileId))
        {
            return true;
        }

        VanguardOperatorFriendlyTargetGuard.OnHostilityBlocked(actorProfileId, targetProfileId, repairRequired: true);
        VanguardFriendlyIdentityRegistry.TryLogBlockedHostility("memory_add_enemy", actorProfileId, targetProfileId, "BotMemoryClass.AddEnemy");
        return false;
    }
}

internal sealed class VanguardBotEnemiesControllerFriendlyGuardPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(BotEnemiesController), nameof(BotEnemiesController.IsEnemy))
            ?? throw new InvalidOperationException("BotEnemiesController.IsEnemy not found for Vanguard coop alliance guard.");
    }

    [PatchPrefix]
    private static bool PatchPrefix(BotEnemiesController __instance, IPlayer player, ref bool __result)
    {
        string? actorProfileId = __instance?.BotOwner_0?.ProfileId;
        string? targetProfileId = player?.ProfileId;
        if (!VanguardFriendlyIdentityRegistry.ShouldProtectFromVanguardOperator(actorProfileId, targetProfileId))
        {
            if (VanguardHostilityMatrixPolicy.ShouldForceHostile(actorProfileId, targetProfileId, out var hostileReason))
            {
                __result = true;
                VanguardHostilityMatrixPolicy.LogForced("enemies_controller_is_enemy", actorProfileId, targetProfileId, hostileReason);
                return false;
            }

            return true;
        }

        __result = false;
        VanguardOperatorFriendlyTargetGuard.OnHostilityBlocked(actorProfileId, targetProfileId, repairRequired: false);
        VanguardFriendlyIdentityRegistry.TryLogBlockedHostility("enemies_controller_is_enemy", actorProfileId, targetProfileId, "BotEnemiesController.IsEnemy");
        return false;
    }
}

internal sealed class VanguardBotsGroupFriendlyEnemyCheckPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.IsPlayerEnemy))
            ?? throw new InvalidOperationException("BotsGroup.IsPlayerEnemy not found for Vanguard coop alliance guard.");
    }

    [PatchPrefix]
    private static bool PatchPrefix(BotsGroup __instance, IPlayer player, ref bool __result)
    {
        string? actorProfileId = ResolveVanguardActorProfileId(__instance);
        string? targetProfileId = player?.ProfileId;
        bool protectedByGroup = __instance is VanguardOperatorBotsGroup
            && VanguardFriendlyIdentityRegistry.IsProtectedFriendlyTargetProfileId(targetProfileId);
        bool protectedByRuntimeActor = VanguardFriendlyIdentityRegistry.ShouldProtectFromVanguardOperator(actorProfileId, targetProfileId);
        if (!protectedByGroup && !protectedByRuntimeActor)
        {
            if (VanguardHostilityMatrixPolicy.ShouldForceHostile(actorProfileId, targetProfileId, out var hostileReason))
            {
                __result = true;
                VanguardHostilityMatrixPolicy.LogForced("group_is_player_enemy", actorProfileId, targetProfileId, hostileReason);
                return false;
            }

            return true;
        }

        __result = false;
        VanguardOperatorFriendlyTargetGuard.OnHostilityBlocked(actorProfileId, targetProfileId, repairRequired: false);
        VanguardFriendlyIdentityRegistry.TryLogBlockedHostility(
            "group_is_player_enemy",
            actorProfileId,
            targetProfileId,
            "BotsGroup.IsPlayerEnemy",
            forcedEarlyBindProtection: protectedByGroup && !protectedByRuntimeActor);
        return false;
    }

    private static string? ResolveVanguardActorProfileId(BotsGroup? group)
    {
        if (group is null)
        {
            return null;
        }

        if (group.InitialBot?.ProfileId is { } initialId
            && VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(initialId, out _))
        {
            return initialId;
        }

        foreach (var member in group.Members)
        {
            if (member?.ProfileId is { } memberId
                && VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(memberId, out _))
            {
                return memberId;
            }
        }

        return null;
    }
}
internal static class VanguardHostilityMatrixPolicy
{
    public const string StatusTag = "VANGUARD_HOSTILE_INDOOR_MOVEMENT_PLAN_STATUS";
    private const string CoopHostilityGuardStatusTag = "VANGUARD_COMBAT_BIND_COHESION_RECOVERY_STATUS";
    private static readonly Dictionary<string, DateTimeOffset> LastAuditByPair = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan AuditInterval = TimeSpan.FromSeconds(4.0);

    public static bool ShouldForceHostile(string? actorProfileId, string? targetProfileId, out string reason)
    {
        reason = "none";
        string actor = Normalize(actorProfileId);
        string target = Normalize(targetProfileId);
        bool actorOperator = false;
        bool targetOperator = false;
        bool actorFriendly = false;
        bool targetFriendly = false;
        bool force = false;

        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(target) || string.Equals(actor, target, StringComparison.OrdinalIgnoreCase))
        {
            reason = "missing_or_self";
            LogAudit("relation_check", actor, target, actorOperator, targetOperator, actorFriendly, targetFriendly, force, reason);
            return false;
        }

        actorOperator = VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(actor, out _);
        targetOperator = VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(target, out _);
        actorFriendly = VanguardFriendlyIdentityRegistry.IsProtectedFriendlyTargetProfileId(actor);
        targetFriendly = VanguardFriendlyIdentityRegistry.IsProtectedFriendlyTargetProfileId(target);

        if (!actorOperator && !targetOperator)
        {
            reason = "no_operator_pair";
            LogAudit("relation_check", actor, target, actorOperator, targetOperator, actorFriendly, targetFriendly, force, reason);
            return false;
        }

        if (actorFriendly && targetFriendly)
        {
            reason = "both_vanguard_friendly";
            LogAudit("relation_check", actor, target, actorOperator, targetOperator, actorFriendly, targetFriendly, force, reason);
            return false;
        }

        if ((actorOperator && !targetFriendly) || (targetOperator && !actorFriendly))
        {
            force = true;
            reason = "operator_non_friendly_forced_hostile:actorOperator=" + Bool(actorOperator) + ":targetOperator=" + Bool(targetOperator) + ":actorFriendly=" + Bool(actorFriendly) + ":targetFriendly=" + Bool(targetFriendly);
            LogAudit("relation_check", actor, target, actorOperator, targetOperator, actorFriendly, targetFriendly, force, reason);
            return true;
        }

        reason = "friend_guard_or_unclassified";
        LogAudit("relation_check", actor, target, actorOperator, targetOperator, actorFriendly, targetFriendly, force, reason);
        return false;
    }

    public static void LogForced(string action, string? actorProfileId, string? targetProfileId, string reason)
    {
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_HOSTILITY_MATRIX_FORCE action={Safe(action)}; actor={Safe(actorProfileId)}; target={Safe(targetProfileId)}; reason={Safe(reason)}; result=hostile; preservesCoopFriendlies=true; tag={StatusTag}");
    }

    private static void LogAudit(string action, string actorProfileId, string targetProfileId, bool actorOperator, bool targetOperator, bool actorFriendly, bool targetFriendly, bool forceHostile, string reason)
    {
        if (!actorOperator && !targetOperator)
        {
            return;
        }

        string key = action + "|" + actorProfileId + "|" + targetProfileId + "|" + forceHostile + "|" + reason;
        var now = DateTimeOffset.UtcNow;
        if (LastAuditByPair.TryGetValue(key, out var last) && now - last < AuditInterval)
        {
            return;
        }

        LastAuditByPair[key] = now;
        VanguardClientDiagnosticsLog.Info(CoopHostilityGuardStatusTag,
            $"VANGUARD_HOSTILITY_AUDIT action={Safe(action)}; actor={Safe(actorProfileId)}; target={Safe(targetProfileId)}; actorOperator={Bool(actorOperator)}; targetOperator={Bool(targetOperator)}; actorFriendly={Bool(actorFriendly)}; targetFriendly={Bool(targetFriendly)}; vanguardFriendlyProtected={Bool(actorFriendly || targetFriendly)}; finalHostile={Bool(forceHostile)}; reason={Safe(reason)}; tag={CoopHostilityGuardStatusTag}; matrixTag={StatusTag}");
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    private static string Bool(bool value) => value ? "true" : "false";
}

#else
namespace Vanguard.Client.Raid.Patches;

internal sealed class VanguardBotMemoryFriendlyGuardPatch
{
    public void Enable() { }
}

internal sealed class VanguardBotEnemiesControllerFriendlyGuardPatch
{
    public void Enable() { }
}

internal sealed class VanguardBotsGroupFriendlyEnemyCheckPatch
{
    public void Enable() { }
}
#endif
