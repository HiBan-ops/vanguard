#if SPT_CLIENT
using System;

// Responsibility: Maintains the bounded state used by Corpse Loot Claim Store in the loot runtime.
// Flow: Writers update normalized entries, readers query a stable view, and lifecycle/reset hooks clear or reconcile data at the appropriate boundary.
// Authority boundary: State cache/registry only; persistent or physical truth remains owned by the designated server/game subsystem unless explicitly documented otherwise.
// Invariant: Entries are scoped to their owner/raid/profile and stale state must be removable without forcing gameplay mutation.
namespace Vanguard.Client.Runtime.Loot;

internal sealed class VanguardCorpseLootClaim
{
    public string ClaimId { get; init; } = "none";
    public string OwnerProfileId { get; init; } = "none";
    public string OperatorId { get; init; } = "none";
    public string BotProfileId { get; init; } = "none";
    public string CorpseId { get; init; } = "none";
    public float Score { get; init; }
    public DateTimeOffset AcquiredAtUtc { get; init; } = DateTimeOffset.MinValue;
    public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.MinValue;

    public string Summary => $"claim={Safe(ClaimId)}; owner={Safe(OwnerProfileId)}; operator={Safe(OperatorId)}; botProfile={Safe(BotProfileId)}; corpse={Safe(CorpseId)}; score={Score:0.0}; acquired={AcquiredAtUtc:O}; expires={ExpiresAtUtc:O}";

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

/// <summary>
/// Physical corpse-interaction lease. One Owner squad may approach one corpse at a time and one corpse
/// may be physically handled by one Operator. Item authority is deliberately separate and lives in
/// VanguardLootItemClaimStore; holding this lease never authorizes taking an item assigned elsewhere.
/// </summary>
internal static class VanguardCorpseLootClaimStore
{
    public static void ResetForRaidLifecycle(string reason) => VanguardLootTargetClaimStore.ResetForRaidLifecycle(reason);

    public static bool TryAcquire(string ownerProfileId, string operatorId, string botProfileId, string corpseId,
        float score, DateTimeOffset now, out VanguardCorpseLootClaim claim, out string reason)
    {
        bool ok = VanguardLootTargetClaimStore.TryAcquire(ownerProfileId, operatorId, botProfileId,
            VanguardLootTargetKind.Corpse, corpseId, score, now, out VanguardLootTargetClaim generic, out reason);
        claim = ToCorpse(generic);
        return ok;
    }

    public static bool TryGetByBot(string botProfileId, DateTimeOffset now, out VanguardCorpseLootClaim claim)
    {
        if (VanguardLootTargetClaimStore.TryGetByBot(botProfileId, now, out VanguardLootTargetClaim generic)
            && generic.TargetKind == VanguardLootTargetKind.Corpse)
        {
            claim = ToCorpse(generic);
            return true;
        }
        claim = new VanguardCorpseLootClaim();
        return false;
    }

    public static bool TryGetActiveClaimBot(string ownerProfileId, DateTimeOffset now, out string botProfileId)
        => VanguardLootTargetClaimStore.TryGetActiveClaimBot(ownerProfileId, now, out botProfileId);

    public static bool Refresh(string claimId, DateTimeOffset now) => VanguardLootTargetClaimStore.Refresh(claimId, VanguardLootTargetKind.Corpse, now);

    public static bool Release(string claimId, string reason, out VanguardCorpseLootClaim released)
    {
        bool ok = VanguardLootTargetClaimStore.Release(claimId, VanguardLootTargetKind.Corpse, reason, out VanguardLootTargetClaim generic);
        released = ToCorpse(generic);
        return ok && generic.TargetKind == VanguardLootTargetKind.Corpse;
    }

    private static VanguardCorpseLootClaim ToCorpse(VanguardLootTargetClaim generic) => new()
    {
        ClaimId = generic.ClaimId,
        OwnerProfileId = generic.OwnerProfileId,
        OperatorId = generic.OperatorId,
        BotProfileId = generic.BotProfileId,
        CorpseId = generic.TargetKind == VanguardLootTargetKind.Corpse ? generic.TargetId : "none",
        Score = generic.Score,
        AcquiredAtUtc = generic.AcquiredAtUtc,
        ExpiresAtUtc = generic.ExpiresAtUtc
    };
}
#endif
