#if SPT_CLIENT
using System;
using System.Linq;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Interop;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Alliance;

// Responsibility: Bridges EFT/SPT/Fika callbacks into Operator Group Enemy Sync Patch for the raid lifecycle patch bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.Raid.Patches;

/// <summary>
/// Keeps Vanguard Operators aligned with the player owner in the EFT hostility graph.
/// The Operator group refuses its owner as enemy, and hostile groups that target
/// the owner are also given the owner's Operators as enemies.
/// </summary>
internal sealed class VanguardOperatorGroupEnemySyncPatch : ModulePatch
{
    private static bool syncing;

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(BotsGroup), "AddEnemy");
    }

    [PatchPrefix]
    private static bool PatchPrefix(BotsGroup __instance, IPlayer person, EBotEnemyCause cause, ref bool __result)
    {
        if (person?.ProfileId is null)
        {
            return true;
        }

        string? actorProfileId = ResolveVanguardActorProfileId(__instance);
        if (__instance is VanguardOperatorBotsGroup operatorGroup)
        {
            actorProfileId ??= __instance.InitialBot?.ProfileId;
            if (VanguardFriendlyIdentityRegistry.IsProtectedFriendlyTargetProfileId(person.ProfileId)
                || (!string.IsNullOrWhiteSpace(operatorGroup.PlayerOwnerProfileId)
                    && string.Equals(person.ProfileId, operatorGroup.PlayerOwnerProfileId, StringComparison.OrdinalIgnoreCase)))
            {
                __result = false;
                VanguardOperatorFriendlyTargetGuard.OnHostilityBlocked(actorProfileId, person.ProfileId, repairRequired: true);
                VanguardFriendlyIdentityRegistry.TryLogBlockedHostility("group_add_enemy_coop_protected", actorProfileId, person.ProfileId, "BotsGroup.AddEnemy");
                return false;
            }
        }

        if (!VanguardFriendlyIdentityRegistry.ShouldProtectFromVanguardOperator(actorProfileId, person.ProfileId))
        {
            return true;
        }

        __result = false;
        VanguardOperatorFriendlyTargetGuard.OnHostilityBlocked(actorProfileId, person.ProfileId, repairRequired: true);
        VanguardFriendlyIdentityRegistry.TryLogBlockedHostility("group_add_enemy", actorProfileId, person.ProfileId, "BotsGroup.AddEnemy");
        return false;
    }

    [PatchPostfix]
    private static void PatchPostfix(BotsGroup __instance, IPlayer person, EBotEnemyCause cause, bool __result)
    {
        if (syncing || !__result || __instance is VanguardOperatorBotsGroup || person?.ProfileId is null)
        {
            return;
        }

        string? ownerProfileId = null;
        if (VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(person.ProfileId, out var operatorRecord))
        {
            ownerProfileId = operatorRecord.OwnerProfileId;
        }
        else
        {
            var ownedOperators = VanguardRaidOperatorRuntimeRegistry.GetOperatorsForOwner(person.ProfileId);
            if (ownedOperators.Count > 0)
            {
                ownerProfileId = person.ProfileId;
            }
        }

        if (string.IsNullOrWhiteSpace(ownerProfileId))
        {
            return;
        }

        var squad = VanguardRaidOperatorRuntimeRegistry.GetOperatorsForOwner(ownerProfileId)
            .Where(record => record.BotOwner?.GetPlayer is not null)
            .ToArray();
        if (squad.Length == 0)
        {
            return;
        }

        try
        {
            syncing = true;
            int added = 0;
            foreach (var record in squad)
            {
                object? operatorPlayer = record.BotOwner?.GetPlayer;
                string operatorProfileId = record.BotProfileId ?? string.Empty;
                if (operatorPlayer is null || string.Equals(operatorProfileId, person.ProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (VanguardEftReflection.TryAddEnemy(__instance, operatorPlayer, EBotEnemyCause.addPlayerToBoss))
                {
                    added++;
                }
            }

            if (added > 0)
            {
                VanguardClientDiagnosticsLog.Info(
                    "VANGUARD_OPERATOR_BRAIN_BIND_STATUS",
                    $"enemy_sync groupId={__instance.Id}; trigger={person.ProfileId}; owner={ownerProfileId}; addedOperators={added}; cause={cause}");
            }
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_BRAIN_BIND_STATUS",
                $"enemy_sync_failed groupId={__instance.Id}; trigger={person.ProfileId}; reason={exception.GetType().Name}:{exception.Message}");
        }
        finally
        {
            syncing = false;
        }
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
#else
namespace Vanguard.Client.Raid.Patches;

internal sealed class VanguardOperatorGroupEnemySyncPatch
{
    public void Enable() { }
}
#endif
