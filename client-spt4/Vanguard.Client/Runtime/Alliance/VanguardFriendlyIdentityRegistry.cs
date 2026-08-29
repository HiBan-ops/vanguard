#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Interop;
using Vanguard.Client.Raid.Runtime;

// Responsibility: Maintains the bounded state used by Friendly Identity Registry in the Operator allegiance runtime.
// Flow: Writers update normalized entries, readers query a stable view, and lifecycle/reset hooks clear or reconcile data at the appropriate boundary.
// Authority boundary: State cache/registry only; persistent or physical truth remains owned by the designated server/game subsystem unless explicitly documented otherwise.
// Invariant: Entries are scoped to their owner/raid/profile and stale state must be removable without forcing gameplay mutation.
namespace Vanguard.Client.Runtime.Alliance;

/// <summary>
/// Raid-scoped friendly identity read model. It is intentionally generic:
/// names like Vector or Slate never drive behavior. The registry classifies
/// players, owners and Vanguard Operators by profile id for the current raid.
/// </summary>
internal static class VanguardFriendlyIdentityRegistry
{
    private static readonly object Sync = new();
    private static readonly HashSet<string> PlayerProfileIds = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset lastRefreshUtc = DateTimeOffset.MinValue;
    private static int lastPlayerCount = -1;
    private static int lastOperatorCount = -1;

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            PlayerProfileIds.Clear();
            lastRefreshUtc = DateTimeOffset.MinValue;
            lastPlayerCount = -1;
            lastOperatorCount = -1;
        }

        VanguardClientDiagnosticsLog.Info(VanguardRaidAlliancePolicy.StatusTag, $"VANGUARD_COOP_ALLIANCE_REGISTRY_RESET reason={reason}; mode={VanguardRaidAlliancePolicy.Mode}");
    }

    public static void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - lastRefreshUtc).TotalSeconds < 1.0)
        {
            return;
        }

        Refresh(now, reason: "periodic_tick");
    }

    public static void RefreshNow(string reason)
    {
        Refresh(DateTimeOffset.UtcNow, reason);
    }

    public static VanguardOperatorAllegianceSnapshot Evaluate(string? actorProfileId, string? targetProfileId)
    {
        string actor = Normalize(actorProfileId);
        string target = Normalize(targetProfileId);
        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(target))
        {
            return new VanguardOperatorAllegianceSnapshot
            {
                ActorProfileId = actor,
                TargetProfileId = target,
            };
        }

        bool actorIsOperator = IsKnownOperatorProfileId(actor);
        bool targetIsOperator = IsKnownOperatorProfileId(target);
        bool targetIsPlayer = IsPlayerProfileId(target);
        bool protectedByCoop = VanguardRaidAlliancePolicy.ProtectAllPlayerSquadsByDefault
            && actorIsOperator
            && !string.Equals(actor, target, StringComparison.OrdinalIgnoreCase)
            && (targetIsPlayer || targetIsOperator);

        return new VanguardOperatorAllegianceSnapshot
        {
            ActorProfileId = actor,
            TargetProfileId = target,
            ActorIsVanguardOperator = actorIsOperator,
            TargetIsPlayer = targetIsPlayer,
            TargetIsVanguardOperator = targetIsOperator,
            ProtectedByCoopAlliance = protectedByCoop,
            AllianceId = VanguardRaidAlliancePolicy.DefaultAllianceId,
        };
    }

    public static bool ShouldProtectFromVanguardOperator(string? actorProfileId, string? targetProfileId)
    {
        return Evaluate(actorProfileId, targetProfileId).ProtectedByCoopAlliance;
    }

    public static bool IsPlayerProfileId(string? profileId)
    {
        string id = Normalize(profileId);
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (Sync)
        {
            if (PlayerProfileIds.Contains(id))
            {
                return true;
            }
        }

        return VanguardRaidOperatorRuntimeRegistry.GetKnownOwnerProfileIds()
            .Any(owner => string.Equals(owner, id, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsProtectedFriendlyTargetProfileId(string? profileId)
    {
        string id = Normalize(profileId);
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        return IsPlayerProfileId(id) || IsKnownOperatorProfileId(id);
    }

    public static bool IsProtectedFriendlyTargetForOperator(string? actorProfileId, IPlayer? target)
    {
        return target is not null && ShouldProtectFromVanguardOperator(actorProfileId, target.ProfileId);
    }

    public static IReadOnlyList<object> GetKnownFriendlyPlayersForOperator(string? actorProfileId)
    {
        string actor = Normalize(actorProfileId);
        if (string.IsNullOrWhiteSpace(actor) || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(actor, out _))
        {
            return Array.Empty<object>();
        }

        var players = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string playerId in GetPlayerProfileIdsSnapshot())
        {
            var player = VanguardFikaCompat.FindRaidPlayerByProfileId(playerId);
            AddIfProtected(player);
        }

        foreach (string ownerId in VanguardRaidOperatorRuntimeRegistry.GetKnownOwnerProfileIds())
        {
            var player = VanguardFikaCompat.FindRaidPlayerByProfileId(ownerId);
            AddIfProtected(player);
        }

        foreach (var record in VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators())
        {
            AddIfProtected(record.BotOwner?.GetPlayer);
        }

        foreach (string expectedOperatorProfileId in VanguardRaidOperatorRuntimeRegistry.GetExpectedOperatorBotProfileIds())
        {
            AddIfProtected(VanguardFikaCompat.FindRaidPlayerByProfileId(expectedOperatorProfileId));
        }

        return players;

        void AddIfProtected(object? player)
        {
            string? profileId = VanguardEftReflection.TryResolveProfileId(player);
            if (string.IsNullOrWhiteSpace(profileId))
            {
                return;
            }

            if (string.Equals(profileId, actor, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!ShouldProtectFromVanguardOperator(actor, profileId))
            {
                return;
            }

            if (seen.Add(profileId))
            {
                players.Add(player!);
            }
        }
    }

    public static void TryLogBlockedHostility(string action, string? actorProfileId, string? targetProfileId, string source)
    {
        TryLogBlockedHostility(action, actorProfileId, targetProfileId, source, forcedEarlyBindProtection: false);
    }

    public static void TryLogBlockedHostility(
        string action,
        string? actorProfileId,
        string? targetProfileId,
        string source,
        bool forcedEarlyBindProtection)
    {
        string actor = Normalize(actorProfileId);
        string target = Normalize(targetProfileId);
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var snapshot = forcedEarlyBindProtection
            ? EvaluateEarlyBindProtected(actor, target)
            : Evaluate(actor, target);
        if (!snapshot.ProtectedByCoopAlliance)
        {
            return;
        }

        bool emitDetail = VanguardAllianceHostilityLogGate.RegisterBlockedHostility(action, source, snapshot, forcedEarlyBindProtection);
        if (emitDetail)
        {
            VanguardClientDiagnosticsLog.Info(VanguardRaidAlliancePolicy.StatusTag, $"VANGUARD_COOP_ALLIANCE_HOSTILITY_BLOCKED action={action}; source={source}; {snapshot.Summary}");
        }
    }

    public static VanguardOperatorAllegianceSnapshot EvaluateEarlyBindProtected(string? actorProfileId, string? targetProfileId)
    {
        string actor = Normalize(actorProfileId);
        string target = Normalize(targetProfileId);
        bool targetIsOperator = IsKnownOperatorProfileId(target);
        bool targetIsPlayer = IsPlayerProfileId(target);
        bool protectedByCoop = VanguardRaidAlliancePolicy.ProtectAllPlayerSquadsByDefault
            && !string.IsNullOrWhiteSpace(target)
            && (targetIsPlayer || targetIsOperator);

        return new VanguardOperatorAllegianceSnapshot
        {
            ActorProfileId = string.IsNullOrWhiteSpace(actor) ? "vanguard_group_early_bind" : actor,
            TargetProfileId = target,
            ActorIsVanguardOperator = true,
            TargetIsPlayer = targetIsPlayer,
            TargetIsVanguardOperator = targetIsOperator,
            ProtectedByCoopAlliance = protectedByCoop,
            EarlyBindProtection = protectedByCoop,
            AllianceId = VanguardRaidAlliancePolicy.DefaultAllianceId,
        };
    }

    private static bool IsKnownOperatorProfileId(string? profileId)
    {
        string id = Normalize(profileId);
        return !string.IsNullOrWhiteSpace(id)
            && (VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(id, out _)
                || VanguardRaidOperatorRuntimeRegistry.IsExpectedOperatorBotProfileId(id));
    }

    private static void Refresh(DateTimeOffset now, string reason)
    {
        var players = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string profileId in VanguardFikaCompat.GetFikaPlayerProfileIds(message =>
            VanguardClientDiagnosticsLog.Info(VanguardRaidAlliancePolicy.StatusTag, $"VANGUARD_COOP_ALLIANCE_FIKA_DISCOVERY note={message}")))
        {
            if (!string.IsNullOrWhiteSpace(profileId))
            {
                players.Add(profileId.Trim());
            }
        }

        foreach (string ownerId in VanguardRaidOperatorRuntimeRegistry.GetKnownOwnerProfileIds())
        {
            if (!string.IsNullOrWhiteSpace(ownerId))
            {
                players.Add(ownerId.Trim());
            }
        }

        int operatorCount = Math.Max(
            VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators().Count,
            VanguardRaidOperatorRuntimeRegistry.GetExpectedOperatorBotProfileIds().Count);
        bool changed;
        lock (Sync)
        {
            PlayerProfileIds.Clear();
            foreach (string player in players)
            {
                PlayerProfileIds.Add(player);
            }

            lastRefreshUtc = now;
            changed = lastPlayerCount != PlayerProfileIds.Count || lastOperatorCount != operatorCount;
            lastPlayerCount = PlayerProfileIds.Count;
            lastOperatorCount = operatorCount;
        }

        if (changed)
        {
            VanguardClientDiagnosticsLog.Info(VanguardRaidAlliancePolicy.StatusTag, $"VANGUARD_COOP_ALLIANCE_REGISTRY_REFRESH reason={reason}; players={players.Count}; operators={operatorCount}; mode={VanguardRaidAlliancePolicy.Mode}; defaultAllied={VanguardRaidAlliancePolicy.ProtectAllPlayerSquadsByDefault}; alliance={VanguardRaidAlliancePolicy.DefaultAllianceId}");
        }
    }

    private static IReadOnlyList<string> GetPlayerProfileIdsSnapshot()
    {
        lock (Sync)
        {
            return PlayerProfileIds.ToArray();
        }
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
#else
namespace Vanguard.Client.Runtime.Alliance;

internal static class VanguardFriendlyIdentityRegistry
{
    public static void Reset(string reason) { }
    public static void Tick() { }
}
#endif
