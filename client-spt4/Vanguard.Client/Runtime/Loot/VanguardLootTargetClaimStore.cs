#if SPT_CLIENT
using System;
using System.Collections.Generic;

// Responsibility: Maintains the bounded state used by Loot Target Claim Store in the loot runtime.
// Flow: Writers update normalized entries, readers query a stable view, and lifecycle/reset hooks clear or reconcile data at the appropriate boundary.
// Authority boundary: State cache/registry only; persistent or physical truth remains owned by the designated server/game subsystem unless explicitly documented otherwise.
// Invariant: Entries are scoped to their owner/raid/profile and stale state must be removable without forcing gameplay mutation.
namespace Vanguard.Client.Runtime.Loot;

internal sealed class VanguardLootTargetClaim
{
    public string ClaimId { get; init; } = "none";
    public string OwnerProfileId { get; init; } = "none";
    public string OperatorId { get; init; } = "none";
    public string BotProfileId { get; init; } = "none";
    public VanguardLootTargetKind TargetKind { get; init; }
    public string TargetId { get; init; } = "none";
    public float Score { get; init; }
    public DateTimeOffset AcquiredAtUtc { get; init; } = DateTimeOffset.MinValue;
    public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.MinValue;
    public string TargetKey => TargetKind + ":" + TargetId;
}

/// <summary>
/// Single physical opportunistic-loot target authority shared by corpses and world containers.
/// One Owner squad may hold one physical loot target at a time; one target and one bot may belong
/// to only one claim. Item-level claims remain separate and do not authorize container opening.
/// </summary>
internal static class VanguardLootTargetClaimStore
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, VanguardLootTargetClaim> ByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> OwnerByTarget = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> OwnerByBot = new(StringComparer.OrdinalIgnoreCase);

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            ByOwner.Clear();
            OwnerByTarget.Clear();
            OwnerByBot.Clear();
        }
    }

    public static bool TryAcquire(string ownerProfileId, string operatorId, string botProfileId,
        VanguardLootTargetKind targetKind, string targetId, float score, DateTimeOffset now,
        out VanguardLootTargetClaim claim, out string reason)
    {
        claim = new VanguardLootTargetClaim();
        ownerProfileId = Normalize(ownerProfileId);
        botProfileId = Normalize(botProfileId);
        targetId = Normalize(targetId);
        if (ownerProfileId == "none" || botProfileId == "none" || targetId == "none")
        {
            reason = "missing_claim_identity";
            return false;
        }

        string targetKey = BuildTargetKey(targetKind, targetId);
        lock (Sync)
        {
            PurgeExpiredLocked(now);
            if (ByOwner.TryGetValue(ownerProfileId, out VanguardLootTargetClaim existing))
            {
                if (string.Equals(existing.BotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase)
                    && existing.TargetKind == targetKind
                    && string.Equals(existing.TargetId, targetId, StringComparison.OrdinalIgnoreCase))
                {
                    existing.ExpiresAtUtc = now + TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.ClaimLifetimeSeconds);
                    claim = existing;
                    reason = "existing_claim_refreshed";
                    return true;
                }

                claim = existing;
                reason = "owner_claim_busy:" + existing.BotProfileId + ":" + existing.TargetKind + ":" + existing.TargetId;
                return false;
            }

            if (OwnerByTarget.TryGetValue(targetKey, out string targetOwner))
            {
                reason = "target_claimed_by_owner:" + targetOwner;
                return false;
            }
            if (OwnerByBot.TryGetValue(botProfileId, out string botOwner))
            {
                reason = "bot_claimed_by_owner:" + botOwner;
                return false;
            }

            claim = new VanguardLootTargetClaim
            {
                ClaimId = "loot_target_claim_" + now.ToUnixTimeMilliseconds() + "_" + botProfileId,
                OwnerProfileId = ownerProfileId,
                OperatorId = Normalize(operatorId),
                BotProfileId = botProfileId,
                TargetKind = targetKind,
                TargetId = targetId,
                Score = score,
                AcquiredAtUtc = now,
                ExpiresAtUtc = now + TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.ClaimLifetimeSeconds)
            };
            ByOwner[ownerProfileId] = claim;
            OwnerByTarget[targetKey] = ownerProfileId;
            OwnerByBot[botProfileId] = ownerProfileId;
            reason = "claim_acquired";
            return true;
        }
    }

    public static bool TryGetByBot(string botProfileId, DateTimeOffset now, out VanguardLootTargetClaim claim)
    {
        botProfileId = Normalize(botProfileId);
        lock (Sync)
        {
            PurgeExpiredLocked(now);
            if (OwnerByBot.TryGetValue(botProfileId, out string owner) && ByOwner.TryGetValue(owner, out claim)) return true;
        }
        claim = new VanguardLootTargetClaim();
        return false;
    }

    public static bool TryGetActiveClaimBot(string ownerProfileId, DateTimeOffset now, out string botProfileId)
    {
        ownerProfileId = Normalize(ownerProfileId);
        lock (Sync)
        {
            PurgeExpiredLocked(now);
            if (ByOwner.TryGetValue(ownerProfileId, out VanguardLootTargetClaim claim))
            {
                botProfileId = claim.BotProfileId;
                return true;
            }
        }
        botProfileId = "none";
        return false;
    }

    public static bool Refresh(string claimId, DateTimeOffset now)
        => Refresh(claimId, (VanguardLootTargetKind?)null, now);

    public static bool Refresh(string claimId, VanguardLootTargetKind expectedTargetKind, DateTimeOffset now)
        => Refresh(claimId, (VanguardLootTargetKind?)expectedTargetKind, now);

    private static bool Refresh(string claimId, VanguardLootTargetKind? expectedTargetKind, DateTimeOffset now)
    {
        claimId = Normalize(claimId);
        lock (Sync)
        {
            PurgeExpiredLocked(now);
            foreach (VanguardLootTargetClaim claim in ByOwner.Values)
            {
                if (!string.Equals(claim.ClaimId, claimId, StringComparison.OrdinalIgnoreCase)) continue;
                if (expectedTargetKind.HasValue && claim.TargetKind != expectedTargetKind.Value) return false;
                claim.ExpiresAtUtc = now + TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.ClaimLifetimeSeconds);
                return true;
            }
        }
        return false;
    }

    public static bool Release(string claimId, string reason, out VanguardLootTargetClaim released)
        => Release(claimId, (VanguardLootTargetKind?)null, reason, out released);

    public static bool Release(string claimId, VanguardLootTargetKind expectedTargetKind, string reason, out VanguardLootTargetClaim released)
        => Release(claimId, (VanguardLootTargetKind?)expectedTargetKind, reason, out released);

    private static bool Release(string claimId, VanguardLootTargetKind? expectedTargetKind, string reason, out VanguardLootTargetClaim released)
    {
        claimId = Normalize(claimId);
        lock (Sync)
        {
            foreach (KeyValuePair<string, VanguardLootTargetClaim> pair in new List<KeyValuePair<string, VanguardLootTargetClaim>>(ByOwner))
            {
                if (!string.Equals(pair.Value.ClaimId, claimId, StringComparison.OrdinalIgnoreCase)) continue;
                if (expectedTargetKind.HasValue && pair.Value.TargetKind != expectedTargetKind.Value) break;
                released = pair.Value;
                RemoveLocked(pair.Key, pair.Value);
                return true;
            }
        }
        released = new VanguardLootTargetClaim();
        return false;
    }

    private static void PurgeExpiredLocked(DateTimeOffset now)
    {
        foreach (KeyValuePair<string, VanguardLootTargetClaim> pair in new List<KeyValuePair<string, VanguardLootTargetClaim>>(ByOwner))
            if (pair.Value.ExpiresAtUtc <= now) RemoveLocked(pair.Key, pair.Value);
    }

    private static void RemoveLocked(string owner, VanguardLootTargetClaim claim)
    {
        ByOwner.Remove(owner);
        OwnerByTarget.Remove(BuildTargetKey(claim.TargetKind, claim.TargetId));
        OwnerByBot.Remove(claim.BotProfileId);
    }

    private static string BuildTargetKey(VanguardLootTargetKind kind, string targetId) => kind + ":" + Normalize(targetId);
    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
}
#endif
