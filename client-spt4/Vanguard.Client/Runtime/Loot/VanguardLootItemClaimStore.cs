#if SPT_CLIENT
using System;
using System.Collections.Generic;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Audit;

// Responsibility: Maintains the bounded state used by Loot Item Claim Store in the loot runtime.
// Flow: Writers update normalized entries, readers query a stable view, and lifecycle/reset hooks clear or reconcile data at the appropriate boundary.
// Authority boundary: State cache/registry only; persistent or physical truth remains owned by the designated server/game subsystem unless explicitly documented otherwise.
// Invariant: Entries are scoped to their owner/raid/profile and stale state must be removable without forcing gameplay mutation.
namespace Vanguard.Client.Runtime.Loot;

internal sealed class VanguardLootItemClaim
{
    public string ClaimId { get; init; } = "none";
    public string OwnerProfileId { get; init; } = "none";
    public string BotProfileId { get; init; } = "none";
    public VanguardLootTargetKind TargetKind { get; init; } = VanguardLootTargetKind.Corpse;
    public string TargetId { get; init; } = "none";
    public string CorpseId => TargetKind == VanguardLootTargetKind.Corpse ? TargetId : "none";
    public string ItemId { get; init; } = "none";
    public long ManifestRevision { get; init; }
    public VanguardLootUtilityTier Tier { get; init; }
    public float UtilityScore { get; init; }
    public string AssignmentReason { get; init; } = "none";
    public DateTimeOffset AcquiredAtUtc { get; init; } = DateTimeOffset.MinValue;
    public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.MinValue;
}

/// <summary>
/// Item-level ownership shared by typed loot targets without changing established corpse semantics.
/// The physical target claim remains the interaction lease; this store prevents an Operator holding that
/// target lease from taking an item assigned to another Operator. Claims are revision-bound and short-lived.
/// </summary>
internal static class VanguardLootItemClaimStore
{
    private static readonly object Sync = new();
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);
    private static readonly Dictionary<string, VanguardLootItemClaim> ByItem = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> KeysByBot = new(StringComparer.OrdinalIgnoreCase);

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            ByItem.Clear();
            KeysByBot.Clear();
        }
    }

    public static bool TryAcquire(
        VanguardSquadLootItemAssignment assignment,
        string botProfileId,
        DateTimeOffset now,
        out VanguardLootItemClaim claim,
        out string reason)
    {
        claim = new VanguardLootItemClaim();
        if (assignment == null)
        {
            reason = "assignment_missing";
            return false;
        }

        string bot = Normalize(botProfileId);
        if (!string.Equals(Normalize(assignment.AssignedBotProfileId), bot, StringComparison.OrdinalIgnoreCase))
        {
            reason = "assignment_owned_by_other_operator:" + Safe(assignment.AssignedBotProfileId);
            return false;
        }

        string owner = Normalize(assignment.OwnerProfileId);
        VanguardLootTargetKind targetKind = assignment.TargetKind;
        string target = Normalize(assignment.TargetId);
        string item = Normalize(assignment.ItemId);
        if (owner == "none" || target == "none" || item == "none")
        {
            reason = "claim_identity_missing";
            return false;
        }
        string key = Key(owner, targetKind, target, item);

        lock (Sync)
        {
            PurgeLocked(now);
            if (ByItem.TryGetValue(key, out VanguardLootItemClaim? existing))
            {
                if (string.Equals(existing.BotProfileId, bot, StringComparison.OrdinalIgnoreCase)
                    && existing.ManifestRevision == assignment.ManifestRevision)
                {
                    existing.ExpiresAtUtc = now + Lifetime;
                    claim = existing;
                    reason = "existing_item_claim_refreshed";
                    return true;
                }
                reason = "item_claimed_by_other_or_revision:" + Safe(existing.BotProfileId) + ":rev=" + existing.ManifestRevision;
                claim = existing;
                return false;
            }

            claim = new VanguardLootItemClaim
            {
                ClaimId = "loot_item_claim_" + now.ToUnixTimeMilliseconds() + "_" + Safe(bot) + "_" + Safe(assignment.ItemId),
                OwnerProfileId = owner,
                BotProfileId = bot,
                TargetKind = targetKind,
                TargetId = target,
                ItemId = item,
                ManifestRevision = assignment.ManifestRevision,
                Tier = assignment.Tier,
                UtilityScore = assignment.UtilityScore,
                AssignmentReason = assignment.Reason,
                AcquiredAtUtc = now,
                ExpiresAtUtc = now + Lifetime
            };
            ByItem[key] = claim;
            if (!KeysByBot.TryGetValue(bot, out HashSet<string>? keys))
            {
                keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                KeysByBot[bot] = keys;
            }
            keys.Add(key);
            reason = "item_claim_acquired";
        }

        if (claim.TargetKind == VanguardLootTargetKind.WorldContainer)
        {
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.ContainerItemTransactionActivationStatusTag,
                $"VANGUARD_ITEM_CLAIM_ACQUIRED owner={Safe(claim.OwnerProfileId)}; bot={Safe(claim.BotProfileId)}; targetKind={claim.TargetKind}; target={Safe(claim.TargetId)}; item={Safe(claim.ItemId)}; manifestRevision={claim.ManifestRevision}; tier={claim.Tier}; score={claim.UtilityScore:0.0}; reason={Safe(claim.AssignmentReason)}; expiresSeconds={Lifetime.TotalSeconds:0.0}");
        }
        else
        {
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.UtilityClaimedLootActivationStatusTag,
                $"VANGUARD_ITEM_CLAIM_ACQUIRED owner={Safe(claim.OwnerProfileId)}; bot={Safe(claim.BotProfileId)}; corpse={Safe(claim.TargetId)}; item={Safe(claim.ItemId)}; manifestRevision={claim.ManifestRevision}; tier={claim.Tier}; score={claim.UtilityScore:0.0}; reason={Safe(claim.AssignmentReason)}; expiresSeconds={Lifetime.TotalSeconds:0.0}");
        }
        return true;
    }

    public static bool TryGet(string ownerProfileId, string corpseId, string itemId, DateTimeOffset now, out VanguardLootItemClaim claim)
        => TryGet(ownerProfileId, VanguardLootTargetKind.Corpse, corpseId, itemId, now, out claim);

    public static bool TryGet(string ownerProfileId, VanguardLootTargetKind targetKind, string targetId, string itemId, DateTimeOffset now, out VanguardLootItemClaim claim)
    {
        lock (Sync)
        {
            PurgeLocked(now);
            return ByItem.TryGetValue(Key(ownerProfileId, targetKind, targetId, itemId), out claim!);
        }
    }

    public static bool Refresh(string claimId, DateTimeOffset now)
    {
        string id = Normalize(claimId);
        lock (Sync)
        {
            PurgeLocked(now);
            foreach (VanguardLootItemClaim claim in ByItem.Values)
            {
                if (string.Equals(claim.ClaimId, id, StringComparison.OrdinalIgnoreCase))
                {
                    claim.ExpiresAtUtc = now + Lifetime;
                    return true;
                }
            }
        }
        return false;
    }

    public static bool Release(string claimId, string reason, out VanguardLootItemClaim released)
    {
        string id = Normalize(claimId);
        lock (Sync)
        {
            foreach (KeyValuePair<string, VanguardLootItemClaim> pair in new List<KeyValuePair<string, VanguardLootItemClaim>>(ByItem))
            {
                if (!string.Equals(pair.Value.ClaimId, id, StringComparison.OrdinalIgnoreCase)) continue;
                released = pair.Value;
                RemoveLocked(pair.Key, pair.Value);
                return true;
            }
        }
        released = new VanguardLootItemClaim();
        return false;
    }

    public static void ReleaseByBot(string botProfileId, string reason)
    {
        string bot = Normalize(botProfileId);
        lock (Sync)
        {
            if (!KeysByBot.TryGetValue(bot, out HashSet<string>? keys)) return;
            foreach (string key in new List<string>(keys))
            {
                if (ByItem.TryGetValue(key, out VanguardLootItemClaim? claim)) RemoveLocked(key, claim);
            }
            KeysByBot.Remove(bot);
        }
    }

    private static void PurgeLocked(DateTimeOffset now)
    {
        foreach (KeyValuePair<string, VanguardLootItemClaim> pair in new List<KeyValuePair<string, VanguardLootItemClaim>>(ByItem))
        {
            if (pair.Value.ExpiresAtUtc <= now) RemoveLocked(pair.Key, pair.Value);
        }
    }

    private static void RemoveLocked(string key, VanguardLootItemClaim claim)
    {
        ByItem.Remove(key);
        if (KeysByBot.TryGetValue(claim.BotProfileId, out HashSet<string>? keys))
        {
            keys.Remove(key);
            if (keys.Count == 0) KeysByBot.Remove(claim.BotProfileId);
        }
    }

    private static string Key(string owner, VanguardLootTargetKind targetKind, string target, string item)
        => Normalize(owner) + "|" + targetKind + "|" + Normalize(target) + "|" + Normalize(item);
    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
