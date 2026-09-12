#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Interop;
using Vanguard.Client.Raid.Runtime;

// Responsibility: Builds the structural allied squad relationship between the player, coop players and Vanguard Operators when Fika/EFT group state alone is insufficient.
// Flow: Runtime participants are resolved by stable profile/operator identity, compatible group/enemy structures are reconciled, and the binder verifies the resulting friendly relationship for downstream target safety.
// Authority boundary: It repairs alliance structure only; Fika session membership and Vanguard Operator identity remain authoritative inputs.
// Invariant: Binding is idempotent, never merges unrelated actors by proximity/name alone, and failed reflection leaves the participant unmodified rather than forcing an unsafe group state.
namespace Vanguard.Client.Runtime.Alliance;

/// <summary>
/// Structural coop affiliation layer for the default Vanguard mode.
/// runtime qualification proved the fallback guard works, but the high number of blocked
/// hostility attempts showed that Operators were still entering EFT/Fika/SAIN
/// relation graphs too late as potential enemies. The runtime binds Operators to the
/// same raid-wide coop group/team used by Fika players before the vanilla group
/// constructor and after final spawn binding.
/// </summary>
internal static class VanguardCoopStructuralSquadBinder
{
    public const string StatusTag = "VANGUARD_COOP_STRUCTURAL_SQUAD_BIND_OK";
    public const string InfoClassCompatStatusTag = "VANGUARD_INFOCLASS_COMPAT_OK";
    public const string IdempotentBinderStatusTag = "VANGUARD_COOP_BINDER_IDEMPOTENT_OK";
    public const string DefaultLogicalAllianceId = "Vanguard1";
    public const string FikaCoopGroupId = "Fika";
    public const string FikaCoopTeamId = "Fika";

    private static readonly TimeSpan PeriodicScanInterval = TimeSpan.FromSeconds(10.0d);
    private static readonly HashSet<string> BoundFriendlyPairKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> EnemyGroupCleanupKeys = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset nextPeriodicScanUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset lastSummaryUtc = DateTimeOffset.MinValue;
    private static int profilesAffiliatedSinceSummary;
    private static int constructorPlayersFilteredSinceSummary;
    private static int constructorEnemiesFilteredSinceSummary;
    private static int alliesBoundSinceSummary;
    private static int allyBindSkippedSinceSummary;
    private static int enemyGroupCleanupSinceSummary;

    public static void Reset(string reason)
    {
        lastSummaryUtc = DateTimeOffset.MinValue;
        profilesAffiliatedSinceSummary = 0;
        constructorPlayersFilteredSinceSummary = 0;
        constructorEnemiesFilteredSinceSummary = 0;
        alliesBoundSinceSummary = 0;
        allyBindSkippedSinceSummary = 0;
        enemyGroupCleanupSinceSummary = 0;
        nextPeriodicScanUtc = DateTimeOffset.MinValue;
        BoundFriendlyPairKeys.Clear();
        EnemyGroupCleanupKeys.Clear();
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_COOP_STRUCTURAL_SQUAD_BIND_RESET reason={Safe(reason)}; mode={VanguardRaidAlliancePolicy.Mode}; logicalAlliance={DefaultLogicalAllianceId}; group={FikaCoopGroupId}; team={FikaCoopTeamId}; fallbackGuard=enabled; compatTag={InfoClassCompatStatusTag}; idempotentTag={IdempotentBinderStatusTag}");
    }

    public static void Tick()
    {
        if (!VanguardRaidAlliancePolicy.ProtectAllPlayerSquadsByDefault)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now >= nextPeriodicScanUtc)
        {
            nextPeriodicScanUtc = now + PeriodicScanInterval;
            foreach (var record in VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators())
            {
                if (record.BotOwner is null || record.BotOwner.IsDead)
                {
                    continue;
                }

                var ownerPlayer = VanguardFikaCompat.FindRaidPlayerByProfileId(record.OwnerProfileId);
                if (ownerPlayer is null)
                {
                    continue;
                }

                // The runtime hard brake: keep the low-cost profile affiliation alive, but do
                // not re-run heavy RemoveEnemy/AddAlly reflection every frame. Explicit
                // spawn/group bind calls remain active and idempotent; the runtime guard remains fallback.
                ApplyOperatorAffiliation(record.BotOwner, ownerPlayer, "periodic_affiliation_only", logDetail: false);
            }
        }

        if ((now - lastSummaryUtc).TotalSeconds >= 15.0
            && (profilesAffiliatedSinceSummary > 0 || constructorPlayersFilteredSinceSummary > 0 || constructorEnemiesFilteredSinceSummary > 0 || alliesBoundSinceSummary > 0 || allyBindSkippedSinceSummary > 0 || enemyGroupCleanupSinceSummary > 0))
        {
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"VANGUARD_COOP_STRUCTURAL_SQUAD_BIND_SUMMARY profiles={profilesAffiliatedSinceSummary}; constructorPlayersFiltered={constructorPlayersFilteredSinceSummary}; constructorEnemiesFiltered={constructorEnemiesFilteredSinceSummary}; alliesBound={alliesBoundSinceSummary}; allyBindSkipped={allyBindSkippedSinceSummary}; enemyGroupCleanup={enemyGroupCleanupSinceSummary}; cachePairs={BoundFriendlyPairKeys.Count}; mode={VanguardRaidAlliancePolicy.Mode}; logicalAlliance={DefaultLogicalAllianceId}; group={FikaCoopGroupId}; team={FikaCoopTeamId}; fallbackGuard=enabled; idempotent=true; tag={IdempotentBinderStatusTag}");
            profilesAffiliatedSinceSummary = 0;
            constructorPlayersFilteredSinceSummary = 0;
            constructorEnemiesFilteredSinceSummary = 0;
            alliesBoundSinceSummary = 0;
            allyBindSkippedSinceSummary = 0;
            enemyGroupCleanupSinceSummary = 0;
            lastSummaryUtc = now;
        }
    }

    public static void PrepareGeneratedProfile(Profile? profile, Player ownerPlayer, string reason)
    {
        if (!VanguardRaidAlliancePolicy.ProtectAllPlayerSquadsByDefault || profile?.Info is null || ownerPlayer is null)
        {
            return;
        }

        profile.Info.Side = ownerPlayer.Side;
        profile.Info.GroupId = ResolveCoopGroupId(ownerPlayer);
        profile.Info.TeamId = ResolveCoopTeamId(ownerPlayer);
        profilesAffiliatedSinceSummary++;
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_COOP_STRUCTURAL_PROFILE_AFFILIATED stage=generated_profile; profile={Safe(profile.ProfileId)}; owner={Safe(ownerPlayer.ProfileId)}; side={ownerPlayer.Side}; logicalAlliance={DefaultLogicalAllianceId}; group={Safe(profile.Info.GroupId)}; team={Safe(profile.Info.TeamId)}; reason={Safe(reason)}");
    }

    public static void ApplyOperatorAffiliation(BotOwner? botOwner, Player ownerPlayer, string reason, bool logDetail = true)
    {
        if (!VanguardRaidAlliancePolicy.ProtectAllPlayerSquadsByDefault || botOwner is null || ownerPlayer is null)
        {
            return;
        }

        string groupId = ResolveCoopGroupId(ownerPlayer);
        string teamId = ResolveCoopTeamId(ownerPlayer);
        bool changed = false;

        if (botOwner.Profile?.Info is { } ownerInfo)
        {
            changed |= ApplyInfo(ownerInfo, ownerPlayer.Side, groupId, teamId);
        }

        if (botOwner.GetPlayer?.Profile?.Info is { } playerInfo)
        {
            changed |= ApplyInfo(playerInfo, ownerPlayer.Side, groupId, teamId);
        }

        if (changed)
        {
            profilesAffiliatedSinceSummary++;
        }

        if (logDetail && changed)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_COOP_STRUCTURAL_OPERATOR_AFFILIATED botProfile={Safe(botOwner.ProfileId)}; owner={Safe(ownerPlayer.ProfileId)}; side={ownerPlayer.Side}; logicalAlliance={DefaultLogicalAllianceId}; group={Safe(groupId)}; team={Safe(teamId)}; reason={Safe(reason)}");
        }
    }

    public static List<Player> BuildConstructorPlayers(Player ownerPlayer, IEnumerable<Player>? allPlayers, string reason)
    {
        var result = new List<Player>();
        if (allPlayers is null)
        {
            return result;
        }

        VanguardFriendlyIdentityRegistry.RefreshNow("structural_constructor_players_" + reason);
        foreach (var player in allPlayers)
        {
            if (player is null)
            {
                continue;
            }

            if (IsDefaultCoopFriendly(ownerPlayer, player))
            {
                constructorPlayersFilteredSinceSummary++;
                continue;
            }

            result.Add(player);
        }

        return result;
    }

    public static List<BotOwner> BuildConstructorEnemies(Player ownerPlayer, IEnumerable<BotOwner>? activeEnemies, string reason)
    {
        var result = new List<BotOwner>();
        if (activeEnemies is null)
        {
            return result;
        }

        VanguardFriendlyIdentityRegistry.RefreshNow("structural_constructor_enemies_" + reason);
        foreach (var enemy in activeEnemies)
        {
            if (enemy is null || enemy.IsDead)
            {
                continue;
            }

            if (IsDefaultCoopFriendly(ownerPlayer, enemy.GetPlayer) || IsDefaultCoopFriendly(ownerPlayer, enemy))
            {
                constructorEnemiesFilteredSinceSummary++;
                continue;
            }

            result.Add(enemy);
        }

        return result;
    }

    public static int BindKnownFriendlies(BotOwner? botOwner, Player ownerPlayer, string reason, bool logDetail = true)
    {
        if (!VanguardRaidAlliancePolicy.ProtectAllPlayerSquadsByDefault || botOwner?.BotsGroup is null || ownerPlayer is null)
        {
            return 0;
        }

        ApplyOperatorAffiliation(botOwner, ownerPlayer, reason, logDetail: false);
        RemoveCoopGroupFromEnemyGroups(botOwner.BotsGroup);

        int bound = 0;
        int skipped = 0;
        foreach (var friendly in ResolveKnownFriendlyPlayers(botOwner, ownerPlayer))
        {
            string pairKey = BuildFriendlyPairKey(botOwner, friendly);
            if (BoundFriendlyPairKeys.Contains(pairKey))
            {
                skipped++;
                continue;
            }

            try
            {
                VanguardEftReflection.InvokeSingleArgumentMethod(botOwner.Memory, "DeleteInfoAboutEnemy", friendly);
                VanguardEftReflection.InvokeSingleArgumentMethod(botOwner.BotsGroup, "RemoveEnemy", friendly);
                VanguardEftReflection.InvokeSingleArgumentMethod(botOwner.BotsGroup, "AddNeutral", friendly);
                VanguardEftReflection.InvokeSingleArgumentMethod(botOwner.BotsGroup, "AddAlly", friendly);
                BoundFriendlyPairKeys.Add(pairKey);
                bound++;
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(StatusTag, $"VANGUARD_COOP_STRUCTURAL_ALLY_BIND_FAILED botProfile={Safe(botOwner.ProfileId)}; target={Safe(VanguardEftReflection.TryResolveProfileId(friendly))}; reason={Safe(reason)}; error={exception.GetType().Name}:{Safe(exception.Message)}");
            }
        }

        alliesBoundSinceSummary += bound;
        allyBindSkippedSinceSummary += skipped;
        if (logDetail && (bound > 0 || skipped > 0))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_COOP_STRUCTURAL_ALLIES_BOUND botProfile={Safe(botOwner.ProfileId)}; owner={Safe(ownerPlayer.ProfileId)}; bound={bound}; skipped={skipped}; cachePairs={BoundFriendlyPairKeys.Count}; logicalAlliance={DefaultLogicalAllianceId}; group={FikaCoopGroupId}; team={FikaCoopTeamId}; reason={Safe(reason)}; idempotent=true; tag={IdempotentBinderStatusTag}");
        }

        return bound;
    }

    public static void ApplyMindPolicy(BotOwner? botOwner)
    {
        var mind = botOwner?.Settings?.FileSettings?.Mind;
        if (mind is null)
        {
            return;
        }

        // Keep hostile AI PMC/scav behavior intact, but make the Operator receptive to
        // requests from every player PMC in the global coop alliance. Hostility is prevented
        // structurally by group construction + explicit allies; the runtime guards remain fallback.
        mind.CAN_EXECUTE_REQUESTS = true;
        mind.CAN_RECEIVE_PLAYER_REQUESTS_BEAR = true;
        mind.CAN_RECEIVE_PLAYER_REQUESTS_USEC = true;
        mind.DEFAULT_BEAR_BEHAVIOUR = EWarnBehaviour.AlwaysFriends;
        mind.DEFAULT_USEC_BEHAVIOUR = EWarnBehaviour.AlwaysFriends;
        mind.CHANCE_FUCK_YOU_ON_CONTACT_100 = 0;
        mind.REVENGE_TO_GROUP = true;
        mind.REVENGE_FOR_SAVAGE_PLAYERS = false;
    }

    private static IEnumerable<Player> ResolveKnownFriendlyPlayers(BotOwner botOwner, Player ownerPlayer)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in VanguardFriendlyIdentityRegistry.GetKnownFriendlyPlayersForOperator(botOwner.ProfileId).OfType<Player>())
        {
            if (TryAdd(player))
            {
                yield return player;
            }
        }

        if (TryAdd(ownerPlayer))
        {
            yield return ownerPlayer;
        }

        bool TryAdd(Player? player)
        {
            string? profileId = player?.ProfileId;
            return !string.IsNullOrWhiteSpace(profileId)
                && !string.Equals(profileId, botOwner.ProfileId, StringComparison.OrdinalIgnoreCase)
                && seen.Add(profileId);
        }
    }

    private static bool IsDefaultCoopFriendly(Player ownerPlayer, object? candidate)
    {
        string? profileId = VanguardEftReflection.TryResolveProfileId(candidate);
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return false;
        }

        if (string.Equals(profileId, ownerPlayer.ProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (VanguardFriendlyIdentityRegistry.IsProtectedFriendlyTargetProfileId(profileId))
        {
            return true;
        }

        string? groupId = ResolveGroupId(candidate);
        return VanguardFikaCompat.IsInstalled
            && string.Equals(groupId, FikaCoopGroupId, StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveCoopGroupFromEnemyGroups(BotsGroup group)
    {
        string groupKey = BuildGroupKey(group);
        if (EnemyGroupCleanupKeys.Contains(groupKey))
        {
            return;
        }

        try
        {
            int removed = group.EnemyPlayerGroups?.RemoveAll(value => string.Equals(value, FikaCoopGroupId, StringComparison.OrdinalIgnoreCase)) ?? 0;
            EnemyGroupCleanupKeys.Add(groupKey);
            if (removed > 0)
            {
                enemyGroupCleanupSinceSummary += removed;
            }
        }
        catch
        {
            // Best-effort structural cleanup; the runtime guard remains the safe fallback.
        }
    }

    private static string ResolveCoopGroupId(Player ownerPlayer)
    {
        if (VanguardFikaCompat.IsInstalled)
        {
            return FikaCoopGroupId;
        }

        return !string.IsNullOrWhiteSpace(ownerPlayer.Profile?.Info?.GroupId)
            ? ownerPlayer.Profile.Info.GroupId
            : ownerPlayer.ProfileId ?? FikaCoopGroupId;
    }

    private static string ResolveCoopTeamId(Player ownerPlayer)
    {
        if (VanguardFikaCompat.IsInstalled)
        {
            return FikaCoopTeamId;
        }

        return !string.IsNullOrWhiteSpace(ownerPlayer.Profile?.Info?.TeamId)
            ? ownerPlayer.Profile.Info.TeamId
            : ResolveCoopGroupId(ownerPlayer);
    }

    private static bool ApplyInfo(object? info, EPlayerSide side, string groupId, string teamId)
    {
        // SPT 4 exposes different concrete profile-info classes depending on the
        // access path (Profile.Info vs Player.Profile.Info). Both expose Side,
        // GroupId and TeamId, but they are not assignable to ProfileInfoClass at
        // compile time. Keep the structural affiliation layer type-agnostic here
        // and only mutate these three well-known members.
        if (info is null)
        {
            return false;
        }

        bool changed = false;
        changed |= TrySetMemberValue(info, "Side", side);
        changed |= TrySetMemberValue(info, "GroupId", groupId);
        changed |= TrySetMemberValue(info, "TeamId", teamId);
        return changed;
    }

    private static bool TrySetMemberValue(object target, string memberName, object? value)
    {
        var type = target.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var property = type.GetProperties(flags)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, memberName, StringComparison.Ordinal)
                && candidate.GetIndexParameters().Length == 0
                && candidate.CanRead
                && candidate.CanWrite);
        if (property is not null)
        {
            return TrySetReflectedValue(target, property.PropertyType, property.GetValue(target), value, property.SetValue);
        }

        var field = type.GetFields(flags)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, memberName, StringComparison.Ordinal));
        if (field is not null && !field.IsInitOnly)
        {
            return TrySetReflectedValue(target, field.FieldType, field.GetValue(target), value, field.SetValue);
        }

        return false;
    }

    private static bool TrySetReflectedValue(object target, Type memberType, object? currentValue, object? desiredValue, Action<object, object?> setter)
    {
        object? convertedValue;
        try
        {
            convertedValue = ConvertForMemberType(desiredValue, memberType);
        }
        catch
        {
            return false;
        }

        if (ValuesMatch(currentValue, convertedValue))
        {
            return false;
        }

        setter(target, convertedValue);
        return true;
    }

    private static object? ConvertForMemberType(object? value, Type memberType)
    {
        if (value is null)
        {
            return null;
        }

        var targetType = Nullable.GetUnderlyingType(memberType) ?? memberType;
        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }

        if (targetType == typeof(string))
        {
            return value.ToString();
        }

        if (targetType.IsEnum)
        {
            if (value is string text)
            {
                return Enum.Parse(targetType, text, ignoreCase: true);
            }

            return Enum.ToObject(targetType, Convert.ToInt32(value));
        }

        return Convert.ChangeType(value, targetType);
    }

    private static bool ValuesMatch(object? left, object? right)
    {
        return Equals(left, right)
            || string.Equals(left?.ToString(), right?.ToString(), StringComparison.Ordinal);
    }

    private static string BuildFriendlyPairKey(BotOwner botOwner, Player friendly)
    {
        return Safe(botOwner.ProfileId) + "|" + BuildGroupKey(botOwner.BotsGroup) + "|" + Safe(friendly.ProfileId);
    }

    private static string BuildGroupKey(BotsGroup? group)
    {
        return group is null ? "group_none" : "group_" + group.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? ResolveGroupId(object? instance)
    {
        if (instance is IPlayer player)
        {
            return player.GroupId;
        }

        return VanguardEftReflection.ReadFirstMember(instance, "GroupId", "Profile.Info.GroupId", "GetPlayer.Profile.Info.GroupId")?.ToString();
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }
}
#else
namespace Vanguard.Client.Runtime.Alliance;

internal static class VanguardCoopStructuralSquadBinder
{
    public static void Reset(string reason) { }
    public static void Tick() { }
}
#endif
