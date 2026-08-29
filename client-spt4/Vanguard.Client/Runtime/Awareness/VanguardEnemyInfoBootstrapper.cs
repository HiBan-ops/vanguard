#if SPT_CLIENT
using System;
using Comfort.Common;
using EFT;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Alliance;

// Responsibility: Converts one already-qualified Vanguard target assignment into the minimum SAIN/EFT enemy-memory state needed for SAIN to act on it.
// Flow: The caller supplies the selected target and evidence flags; the adapter resolves/creates EnemyInfo, applies only supported memory hints, then returns success without running combat logic itself.
// Authority boundary: Vanguard owns the qualification decision; this adapter may seed memory, but SAIN remains the combat tactician and visibility is never fabricated beyond caller evidence.
// Invariant: A missing/invalid target fails cleanly, and bootstrapping cannot turn an unqualified shared contact into a forced attack.
namespace Vanguard.Client.Runtime.Awareness;

/// <summary>
/// Single SAIN commit adapter for Vanguard-qualified assignments. The caller has already built the
/// squad contact picture and selected an individual target for this Operator. This adapter creates
/// or refreshes EnemyInfo and bot memory without manufacturing line of sight; SAIN remains the only
/// combat tactician after the commit.
/// </summary>
internal static class VanguardEnemyInfoBootstrapper
{
    public const string StatusTag = "VANGUARD_TARGET_BOOTSTRAP_STATUS";

    public static bool TryBootstrapTarget(
        BotOwner botOwner,
        string targetProfileId,
        bool markVisible,
        bool attackImmediately,
        bool markUnderFire,
        out EnemyInfo enemyInfo,
        out string reason)
    {
        enemyInfo = null!;
        reason = "none";

        if (botOwner == null || botOwner.IsDead)
        {
            reason = "botowner_missing_or_dead";
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetProfileId) || string.Equals(targetProfileId, "none", StringComparison.OrdinalIgnoreCase))
        {
            reason = "target_missing";
            return false;
        }

        if (VanguardFriendlyIdentityRegistry.ShouldProtectFromVanguardOperator(botOwner.ProfileId, targetProfileId)
            || VanguardFriendlyIdentityRegistry.IsProtectedFriendlyTargetProfileId(targetProfileId))
        {
            reason = "protected_friendly_target";
            return false;
        }

        var targetPlayer = Singleton<GameWorld>.Instance?.GetAlivePlayerByProfileID(targetProfileId);
        if (targetPlayer == null || targetPlayer.Transform == null)
        {
            reason = "target_player_not_alive_or_not_found";
            return false;
        }

        string groupBootstrap = TryPrimeVanillaGroupMemory(botOwner, targetPlayer, markVisible, markUnderFire);
        var info = EnsureEnemyInfo(botOwner, targetPlayer, EBotEnemyCause.addPlayerToBoss, out reason);
        if (info == null)
        {
            reason = "enemyinfo_bootstrap_failed:" + reason;
            return false;
        }

        info.IgnoreUntilAggression = false;
        TryRefreshEnemyLastPosition(info, targetPlayer.Transform.position);
        if (markVisible)
        {
            TrySetVisible(info, true);
            TryInvoke(botOwner.Memory, "SetLastTimeSeeEnemy");
        }

        // Target authority is deliberately not written here. The runtime atomic commit adapter is the
        // only boundary allowed to synchronize EFT BotMemory with SAINEnemyController.GoalEnemy.
        // This bootstrap only guarantees that the vanilla/group EnemyInfo exists and that local
        // visibility is preserved when it was independently observed.
        enemyInfo = info;
        reason = "bootstrapped_enemyinfo:visible=" + Bool(markVisible)
            + ":attackRequested=" + Bool(attackImmediately)
            + ":underFire=" + Bool(markUnderFire)
            + ":group=" + Safe(groupBootstrap)
            + ":targetAuthorityMutation=false";
        return true;
    }

    private static string TryPrimeVanillaGroupMemory(BotOwner botOwner, Player targetPlayer, bool markVisible, bool markUnderFire)
    {
        try
        {
            var targetIPlayer = (IPlayer)(object)targetPlayer;
            bool checkedAdd = false;
            bool reported = false;
            bool positionSet = false;
            bool underFire = false;

            try
            {
                // ignoreAI=true is required for hostile AI contacts such as Goons/Scavs; otherwise
                // CheckAndAddEnemy can reject AI targets before the group memory receives a useful contact.
                checkedAdd = botOwner.BotsGroup?.CheckAndAddEnemy(targetIPlayer, ignoreAI: true) == true;
            }
            catch
            {
                checkedAdd = false;
            }

            try
            {
                if (botOwner.BotsGroup != null)
                {
                    var visibility = markVisible ? EEnemyPartVisibleType.Visible : EEnemyPartVisibleType.NotVisible;
                    botOwner.BotsGroup.ReportAboutEnemy(targetIPlayer, visibility, botOwner);
                    reported = true;
                    botOwner.BotsGroup.SetEnemyPos(targetIPlayer, targetPlayer.Transform.position, targetPlayer.Transform.position, visibility);
                    positionSet = true;
                }
            }
            catch
            {
                reported = false;
                positionSet = false;
            }

            if (markUnderFire)
            {
                try
                {
                    botOwner.Memory?.SetUnderFire(targetIPlayer);
                    underFire = true;
                }
                catch
                {
                    underFire = false;
                }
            }

            return "checkAdd=" + Bool(checkedAdd) + ":report=" + Bool(reported) + ":setPos=" + Bool(positionSet) + ":underFire=" + Bool(underFire);
        }
        catch (Exception exception)
        {
            return "group_bootstrap_exception=" + exception.GetType().Name;
        }
    }

    private static EnemyInfo? EnsureEnemyInfo(BotOwner botOwner, Player targetPlayer, EBotEnemyCause cause, out string reason)
    {
        reason = "none";
        if (botOwner.BotsGroup == null || botOwner.EnemiesController == null || targetPlayer == null)
        {
            reason = "missing_group_or_enemy_controller";
            return null;
        }

        try
        {
            var targetIPlayer = (IPlayer)(object)targetPlayer;

            if (botOwner.EnemiesController.EnemyInfos?.TryGetValue(targetIPlayer, out var existingInfo) == true && existingInfo != null)
            {
                existingInfo.IgnoreUntilAggression = false;
                TryRefreshEnemyLastPosition(existingInfo, targetPlayer.Transform.position);
                reason = "existing_enemyinfo";
                return existingInfo;
            }

            botOwner.BotsGroup.Enemies.TryGetValue(targetIPlayer, out var groupInfo);
            if (groupInfo == null)
            {
                botOwner.BotsGroup.AddEnemy(targetIPlayer, cause);
                botOwner.BotsGroup.Enemies.TryGetValue(targetIPlayer, out groupInfo);
            }

            if (groupInfo == null)
            {
                groupInfo = new BotSettingsClass(targetPlayer, botOwner.BotsGroup, cause);
                botOwner.Memory?.AddEnemy(targetIPlayer, groupInfo, false);
            }

            if (groupInfo == null)
            {
                reason = "group_info_missing_after_add";
                return null;
            }

            groupInfo.EnemyLastPosition = targetPlayer.Transform.position;
            var enemyInfo = botOwner.EnemiesController.AddNew(botOwner.BotsGroup, targetIPlayer, groupInfo);
            if (enemyInfo == null)
            {
                reason = "enemy_controller_addnew_returned_null";
                return null;
            }

            botOwner.EnemiesController.SetInfo(targetIPlayer, enemyInfo);
            enemyInfo.IgnoreUntilAggression = false;
            reason = "created_enemyinfo";
            return enemyInfo;
        }
        catch (Exception exception)
        {
            reason = exception.GetType().Name + ":" + Safe(exception.Message);
            return null;
        }
    }


    private static void TryRefreshEnemyLastPosition(EnemyInfo info, Vector3 position)
    {
        try
        {
            if (info?.GroupInfo != null)
            {
                info.GroupInfo.EnemyLastPosition = position;
            }
        }
        catch
        {
            // Best-effort bootstrap refresh only. Combat authority must never fail because EFT
            // exposes the EnemyInfo projection as read-only on this build.
        }
    }

    private static bool TrySetVisible(EnemyInfo info, bool value)
    {
        try
        {
            info.SetVisible(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryInvoke(object? target, string methodName)
    {
        if (target == null || string.IsNullOrWhiteSpace(methodName))
        {
            return false;
        }

        try
        {
            var method = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (method == null)
            {
                return false;
            }

            method.Invoke(target, Array.Empty<object>());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Replace(';', '_').Replace('\n', ' ').Replace('\r', ' ');
}
#endif
