#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EFT;
using Vanguard.Client.Diagnostics;

// Responsibility: Maintains the bounded state used by Operator Native Squad Registry in the raid-runtime state.
// Flow: Writers update normalized entries, readers query a stable view, and lifecycle/reset hooks clear or reconcile data at the appropriate boundary.
// Authority boundary: State cache/registry only; persistent or physical truth remains owned by the designated server/game subsystem unless explicitly documented otherwise.
// Invariant: Entries are scoped to their owner/raid/profile and stale state must be removable without forcing gameplay mutation.
namespace Vanguard.Client.Raid.Runtime;

/// <summary>
/// Owns one native EFT BotsGroup per raid session and player owner.
/// SAIN resolves its SquadInfo from BotOwner.BotsGroup during BotOwner.PreActivate,
/// so the shared native group must exist before that callback and must remain opaque to Vanguard.
/// </summary>
internal static class VanguardOperatorNativeSquadRegistry
{
    public const string StatusTag = "VANGUARD_NATIVE_SAIN_SQUAD_FOUNDATION_STATUS";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, NativeSquadEntry> EntriesByRaidAndOwner = new(StringComparer.Ordinal);

    public static void ResetForRaidLifecycle(string reason)
    {
        int cleared;
        lock (Sync)
        {
            cleared = EntriesByRaidAndOwner.Count;
            EntriesByRaidAndOwner.Clear();
        }

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_NATIVE_SQUAD_REGISTRY_RESET reason={Safe(reason)}; cleared={cleared}; scope=raid_plus_player_owner; sainInternalsMutated=false; tag={StatusTag}");
    }

    public static VanguardOperatorBotsGroup GetOrCreate(
        string raidSessionId,
        string playerOwnerProfileId,
        int expectedOperatorCount,
        Func<VanguardOperatorBotsGroup> groupFactory,
        out bool created)
    {
        if (groupFactory == null)
        {
            throw new ArgumentNullException(nameof(groupFactory));
        }

        string normalizedOwner = Normalize(playerOwnerProfileId);
        if (string.IsNullOrWhiteSpace(normalizedOwner))
        {
            throw new InvalidOperationException("Cannot create a shared Vanguard native squad without a player owner profile id.");
        }

        string key = BuildKey(raidSessionId, normalizedOwner);
        VanguardOperatorBotsGroup group;
        int effectiveExpectedCount;
        lock (Sync)
        {
            if (EntriesByRaidAndOwner.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing.Group.PlayerOwnerProfileId, normalizedOwner, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Shared Vanguard native squad owner mismatch.");
                }

                existing.ExpectedOperatorCount = Math.Max(existing.ExpectedOperatorCount, Math.Max(1, expectedOperatorCount));
                group = existing.Group;
                effectiveExpectedCount = existing.ExpectedOperatorCount;
                created = false;
            }
            else
            {
                group = groupFactory() ?? throw new InvalidOperationException("The Vanguard native squad factory returned no group.");
                if (!string.Equals(group.PlayerOwnerProfileId, normalizedOwner, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The created Vanguard native squad does not belong to the requested player owner.");
                }

                effectiveExpectedCount = Math.Max(1, expectedOperatorCount);
                EntriesByRaidAndOwner.Add(key, new NativeSquadEntry(group, effectiveExpectedCount));
                created = true;
            }
        }

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_NATIVE_SQUAD_{(created ? "CREATED" : "REUSED")} raid={Safe(raidSessionId)}; owner={normalizedOwner}; nativeGroup={group.Id}; nativeMembers={group.MembersCount}; expectedOperators={effectiveExpectedCount}; scope=raid_plus_player_owner; preActivateContract=true; tag={StatusTag}");
        return group;
    }

    public static void RecordMemberBound(
        string raidSessionId,
        string playerOwnerProfileId,
        BotOwner botOwner,
        int expectedOperatorCount,
        string source)
    {
        if (botOwner == null || string.IsNullOrWhiteSpace(botOwner.ProfileId))
        {
            return;
        }

        string key = BuildKey(raidSessionId, playerOwnerProfileId);
        bool firstObservation = false;
        int observedMembers = botOwner.BotsGroup?.MembersCount ?? 0;
        int expectedMembers = Math.Max(1, expectedOperatorCount);
        lock (Sync)
        {
            if (EntriesByRaidAndOwner.TryGetValue(key, out var entry))
            {
                entry.ExpectedOperatorCount = Math.Max(entry.ExpectedOperatorCount, expectedMembers);
                expectedMembers = entry.ExpectedOperatorCount;
                firstObservation = entry.BoundBotProfileIds.Add(botOwner.ProfileId);
            }
        }

        if (!firstObservation)
        {
            return;
        }

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_NATIVE_SQUAD_MEMBER_BOUND raid={Safe(raidSessionId)}; owner={Safe(playerOwnerProfileId)}; botProfile={botOwner.ProfileId}; nativeGroup={botOwner.BotsGroup?.Id.ToString() ?? "none"}; nativeMembers={observedMembers}; expectedOperators={expectedMembers}; source={Safe(source)}; tag={StatusTag}");
    }

    private static string BuildKey(string raidSessionId, string playerOwnerProfileId)
    {
        return Normalize(raidSessionId, "raid_unknown") + "|" + Normalize(playerOwnerProfileId, "owner_unknown");
    }

    private static string Normalize(string value, string fallback = "")
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string Safe(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    }

    private sealed class NativeSquadEntry
    {
        public NativeSquadEntry(VanguardOperatorBotsGroup group, int expectedOperatorCount)
        {
            Group = group;
            ExpectedOperatorCount = expectedOperatorCount;
        }

        public VanguardOperatorBotsGroup Group { get; }
        public int ExpectedOperatorCount { get; set; }
        public HashSet<string> BoundBotProfileIds { get; } = new(StringComparer.Ordinal);
    }
}
#endif
