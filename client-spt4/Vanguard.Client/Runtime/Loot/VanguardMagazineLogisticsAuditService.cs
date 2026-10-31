#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using EftWeapon = global::EFT.InventoryLogic.Weapon;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;

// Responsibility: exposes a read-only magazine-logistics audit for carried and currently observed loot magazines.
// Flow: carried magazines + loose compatible ammunition + target manifest magazines -> capacity-aware structural facts -> bounded Diagnostic telemetry/read-model cache.
// Authority boundary: observation only. It never throws, moves, loads, unloads, merges, destroys or repacks an item and never changes loot utility or weapon preference.
// Invariant: source-visible EFT/Fika mutation paths remain evidence only until a separate runtime qualification explicitly promotes an inventory mutation policy.
namespace Vanguard.Client.Runtime.Loot;

internal static class VanguardMagazineLogisticsAuditService
{
    public const string StatusTag = "VANGUARD_MAGAZINE_LOGISTICS_AUDIT_STATUS";
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30d);
    private static readonly TimeSpan SnapshotFreshness = TimeSpan.FromSeconds(2d);
    private const int MaxDiagnosticFieldLength = 160;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, AuditState> ByWeaponKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, VanguardMagazineLogisticsAuditSnapshot> LatestByWeaponKey = new(StringComparer.OrdinalIgnoreCase);
    private static bool capabilityEvidencePublished;

    internal static void Observe(
        VanguardRaidOperatorRuntimeRecord record,
        VanguardLootTargetKind targetKind,
        string? targetId,
        VanguardCorpseLootManifest manifest,
        IReadOnlyList<EftWeapon> currentWeapons,
        IReadOnlyList<VanguardCorpseLootManifestItem> operatorInventorySupport,
        DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority
            || record.BotOwner == null
            || record.BotOwner.IsDead
            || currentWeapons.Count == 0)
        {
            return;
        }

        PublishMutationCapabilityEvidenceOnce();

        foreach (EftWeapon weapon in currentWeapons)
        {
            if (weapon == null || string.IsNullOrWhiteSpace(weapon.Id))
            {
                continue;
            }

            VanguardMagazineLogisticsAuditSnapshot snapshot = BuildSnapshot(
                record,
                targetKind,
                targetId,
                manifest,
                weapon,
                operatorInventorySupport,
                now);

            string key = WeaponKey(record.BotProfileId, weapon.Id);
            bool emit;
            string emission;
            lock (Sync)
            {
                LatestByWeaponKey[key] = snapshot;
                if (!ByWeaponKey.TryGetValue(key, out AuditState? state))
                {
                    ByWeaponKey[key] = new AuditState(snapshot.Fingerprint, now + HeartbeatInterval);
                    emit = true;
                    emission = "INITIAL";
                }
                else if (!string.Equals(state.Fingerprint, snapshot.Fingerprint, StringComparison.Ordinal))
                {
                    state.Fingerprint = snapshot.Fingerprint;
                    state.NextHeartbeatAtUtc = now + HeartbeatInterval;
                    emit = true;
                    emission = "MATERIAL_CHANGE";
                }
                else if (now >= state.NextHeartbeatAtUtc)
                {
                    state.NextHeartbeatAtUtc = now + HeartbeatInterval;
                    emit = true;
                    emission = "HEARTBEAT";
                }
                else
                {
                    emit = false;
                    emission = string.Empty;
                }
            }

            if (!emit)
            {
                continue;
            }

            VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
                $"MAGAZINE_LOGISTICS_AUDIT bot={Bounded(record.BotProfileId)}; owner={Bounded(record.OwnerProfileId)}; emission={emission}; targetKind={TargetKindToken(targetKind)}; target={Bounded(targetId)}; weapon={Bounded(weapon.Id)}; caliber={Bounded(weapon.AmmoCaliber)}; carried={snapshot.CarriedMagazineCount}; full={snapshot.FullMagazineCount}; partial={snapshot.PartialMagazineCount}; quarterOrLess={snapshot.QuarterOrLessMagazineCount}; empty={snapshot.EmptyMagazineCount}; capacityUnknown={snapshot.CapacityUnknownMagazineCount}; carriedRounds={snapshot.CarriedMagazineRounds}; looseCompatibleRounds={snapshot.LooseCompatibleRounds}; bestCarried={Bounded(snapshot.BestCarriedMagazine)}; worstCarried={Bounded(snapshot.WorstCarriedMagazine)}; lootCompatible={snapshot.CompatibleLootMagazineCount}; bestLoot={Bounded(snapshot.BestLootMagazine)}; betterLootCandidate={Bool(snapshot.BetterLootMagazineCandidate)}; discardCandidate={Bounded(snapshot.DiscardCandidate)}; topUpCandidate={Bool(snapshot.TopUpCandidate)}; topUpPotentialRounds={snapshot.TopUpPotentialRounds}; structuralLowDefinition=count_gt_0_and_count_x4_lte_capacity; discardRuntimeQualified=false; topUpRuntimeQualified=false; mutationAllowed=false; initialWeaponProtectionChanged=false");
        }
    }

    internal static bool TryGetLatest(
        string? botProfileId,
        string? weaponItemId,
        DateTimeOffset now,
        out VanguardMagazineLogisticsAuditSnapshot snapshot)
    {
        string key = WeaponKey(botProfileId, weaponItemId);
        lock (Sync)
        {
            if (LatestByWeaponKey.TryGetValue(key, out VanguardMagazineLogisticsAuditSnapshot? found)
                && now - found.ObservedAtUtc <= SnapshotFreshness)
            {
                snapshot = found;
                return true;
            }
        }

        snapshot = VanguardMagazineLogisticsAuditSnapshot.Empty;
        return false;
    }

    internal static void ResetForRaidLifecycle()
    {
        lock (Sync)
        {
            ByWeaponKey.Clear();
            LatestByWeaponKey.Clear();
            capabilityEvidencePublished = false;
        }
    }

    private static VanguardMagazineLogisticsAuditSnapshot BuildSnapshot(
        VanguardRaidOperatorRuntimeRecord record,
        VanguardLootTargetKind targetKind,
        string? targetId,
        VanguardCorpseLootManifest manifest,
        EftWeapon weapon,
        IReadOnlyList<VanguardCorpseLootManifestItem> operatorInventorySupport,
        DateTimeOffset now)
    {
        MagazineFact[] carried = operatorInventorySupport
            .Where(entry => entry.Item is MagazineItemClass magazine && Fits(magazine, weapon))
            .Select(entry => MagazineFact.From((MagazineItemClass)entry.Item, entry.Path))
            .OrderByDescending(entry => entry.FillFractionKnown)
            .ThenByDescending(entry => entry.FillFraction)
            .ThenByDescending(entry => entry.Count)
            .ToArray();

        MagazineFact[] loot = manifest.Items
            .Where(entry => entry.Item is MagazineItemClass magazine
                && Fits(magazine, weapon)
                && string.Equals(entry.ParentWeaponItemId, "none", StringComparison.OrdinalIgnoreCase))
            .Select(entry => MagazineFact.From((MagazineItemClass)entry.Item, entry.Path))
            .OrderByDescending(entry => entry.FillFractionKnown)
            .ThenByDescending(entry => entry.FillFraction)
            .ThenByDescending(entry => entry.Count)
            .ToArray();

        int looseCompatibleRounds = operatorInventorySupport
            .Where(entry => entry.Item is AmmoItemClass ammo && Fits(ammo, weapon))
            .Sum(entry => Math.Max(0, ((AmmoItemClass)entry.Item).StackObjectsCount));

        MagazineFact? bestCarried = carried.FirstOrDefault();
        MagazineFact? worstCarried = carried
            .OrderBy(entry => entry.FillFractionKnown ? 1 : 0)
            .ThenBy(entry => entry.FillFractionKnown ? entry.FillFraction : double.MaxValue)
            .ThenBy(entry => entry.Count)
            .FirstOrDefault();
        MagazineFact? bestLoot = loot.FirstOrDefault();

        bool betterLootCandidate = bestLoot != null && (worstCarried == null || IsMateriallyBetter(bestLoot, worstCarried));
        bool hasDiscardCandidate = worstCarried != null
            && (worstCarried.Empty || worstCarried.QuarterOrLess)
            && betterLootCandidate;
        string discardCandidate = hasDiscardCandidate ? worstCarried!.Token : "none";
        string discardCandidateItemId = hasDiscardCandidate ? worstCarried!.ItemId : "none";

        int partialCapacity = carried
            .Where(entry => entry.FillFractionKnown && !entry.Full && !entry.Empty)
            .Sum(entry => Math.Max(0, entry.Capacity - entry.Count));
        int topUpPotentialRounds = Math.Min(Math.Max(0, looseCompatibleRounds), Math.Max(0, partialCapacity));

        return new VanguardMagazineLogisticsAuditSnapshot
        {
            BotProfileId = Safe(record.BotProfileId),
            OwnerProfileId = Safe(record.OwnerProfileId),
            WeaponItemId = Safe(weapon.Id),
            TargetKind = targetKind,
            TargetId = Safe(targetId),
            ObservedAtUtc = now,
            CarriedMagazineCount = carried.Length,
            FullMagazineCount = carried.Count(entry => entry.Full),
            PartialMagazineCount = carried.Count(entry => entry.Partial),
            QuarterOrLessMagazineCount = carried.Count(entry => entry.QuarterOrLess),
            EmptyMagazineCount = carried.Count(entry => entry.Empty),
            CapacityUnknownMagazineCount = carried.Count(entry => !entry.FillFractionKnown),
            CarriedMagazineRounds = carried.Sum(entry => Math.Max(0, entry.Count)),
            LooseCompatibleRounds = looseCompatibleRounds,
            CompatibleLootMagazineCount = loot.Length,
            BestCarriedMagazine = bestCarried?.Token ?? "none",
            WorstCarriedMagazine = worstCarried?.Token ?? "none",
            BestLootMagazine = bestLoot?.Token ?? "none",
            BestLootMagazineItemId = bestLoot?.ItemId ?? "none",
            BetterLootMagazineCandidate = betterLootCandidate,
            DiscardCandidate = discardCandidate,
            DiscardCandidateItemId = discardCandidateItemId,
            TopUpCandidate = topUpPotentialRounds > 0,
            TopUpPotentialRounds = topUpPotentialRounds
        };
    }

    private static bool IsMateriallyBetter(MagazineFact candidate, MagazineFact current)
    {
        if (candidate.FillFractionKnown && current.FillFractionKnown)
        {
            return candidate.FillFraction > current.FillFraction + 0.0001d
                || (Math.Abs(candidate.FillFraction - current.FillFraction) <= 0.0001d && candidate.Count > current.Count);
        }

        if (candidate.FillFractionKnown != current.FillFractionKnown)
        {
            return candidate.FillFractionKnown;
        }

        return candidate.Count > current.Count;
    }

    private static void PublishMutationCapabilityEvidenceOnce()
    {
        lock (Sync)
        {
            if (capabilityEvidencePublished)
            {
                return;
            }
            capabilityEvidencePublished = true;
        }

        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
            "MAGAZINE_LOGISTICS_MUTATION_CAPABILITY_AUDIT eftTopUpSource=Magazine.Apply+InventoryController.TryRunNetworkTransaction_observed_in_native_bot_reload; eftLoadMagazineSource=InventoryController.LoadMagazine_observed; eftDiscardSource=InventoryController.ThrowItem_observed_in_native_bot_dropper; fikaReplicationSource=inventory_operation_strict_sync_path_observed; sourceApiEvidence=true; runtimeOwnerAuthorityQualified=false; topUpRuntimeQualified=false; discardRuntimeQualified=false; cartridgeToMagazineMutationPromoted=false; safeDiscardMutationPromoted=false; mutationAllowed=false");
    }

    private static bool Fits(MagazineItemClass magazine, EftWeapon weapon)
    {
        try
        {
            return weapon.GetMagazineSlot()?.CheckCompatibility(magazine) == true;
        }
        catch
        {
            return false;
        }
    }

    private static bool Fits(AmmoItemClass ammunition, EftWeapon weapon)
        => ammunition.StackObjectsCount > 0
            && !string.IsNullOrWhiteSpace(ammunition.Caliber)
            && string.Equals(ammunition.Caliber, weapon.AmmoCaliber, StringComparison.OrdinalIgnoreCase);

    private static string WeaponKey(string? botProfileId, string? weaponItemId)
        => Safe(botProfileId) + "|" + Safe(weaponItemId);

    private static string TargetKindToken(VanguardLootTargetKind targetKind)
        => targetKind == VanguardLootTargetKind.WorldContainer ? "world_container" : "corpse";

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private static string Bounded(string? value)
    {
        string safe = Safe(value);
        return safe.Length <= MaxDiagnosticFieldLength ? safe : safe[..MaxDiagnosticFieldLength];
    }

    private sealed class AuditState
    {
        public AuditState(string fingerprint, DateTimeOffset nextHeartbeatAtUtc)
        {
            Fingerprint = fingerprint;
            NextHeartbeatAtUtc = nextHeartbeatAtUtc;
        }

        public string Fingerprint { get; set; }
        public DateTimeOffset NextHeartbeatAtUtc { get; set; }
    }

    private sealed class MagazineFact
    {
        public string ItemId { get; init; } = "none";
        public int Count { get; init; }
        public int Capacity { get; init; }
        public bool FillFractionKnown => Capacity > 0;
        public double FillFraction => Capacity > 0 ? Math.Min(1d, Math.Max(0d, (double)Count / Capacity)) : 0d;
        public bool Full => Capacity > 0 && Count >= Capacity;
        public bool Empty => Count <= 0;
        public bool Partial => Count > 0 && (!FillFractionKnown || Count < Capacity);
        public bool QuarterOrLess => Count > 0 && Capacity > 0 && Count * 4 <= Capacity;
        public string Token => $"item={Safe(ItemId)},rounds={Count.ToString(CultureInfo.InvariantCulture)},capacity={(Capacity > 0 ? Capacity.ToString(CultureInfo.InvariantCulture) : "unknown")},fill={(FillFractionKnown ? FillFraction.ToString("0.00", CultureInfo.InvariantCulture) : "unknown")},empty={Bool(Empty)},quarterOrLess={Bool(QuarterOrLess)}";

        public static MagazineFact From(MagazineItemClass magazine, string? path)
            => new()
            {
                ItemId = Safe(magazine.Id),
                Count = Math.Max(0, magazine.Count),
                Capacity = Math.Max(0, magazine.MaxCount)
            };
    }
}

internal sealed class VanguardMagazineLogisticsAuditSnapshot
{
    public static VanguardMagazineLogisticsAuditSnapshot Empty { get; } = new();

    public string BotProfileId { get; init; } = "none";
    public string OwnerProfileId { get; init; } = "none";
    public string WeaponItemId { get; init; } = "none";
    public VanguardLootTargetKind TargetKind { get; init; } = VanguardLootTargetKind.Corpse;
    public string TargetId { get; init; } = "none";
    public DateTimeOffset ObservedAtUtc { get; init; }
    public int CarriedMagazineCount { get; init; }
    public int FullMagazineCount { get; init; }
    public int PartialMagazineCount { get; init; }
    public int QuarterOrLessMagazineCount { get; init; }
    public int EmptyMagazineCount { get; init; }
    public int CapacityUnknownMagazineCount { get; init; }
    public int CarriedMagazineRounds { get; init; }
    public int LooseCompatibleRounds { get; init; }
    public int CompatibleLootMagazineCount { get; init; }
    public string BestCarriedMagazine { get; init; } = "none";
    public string WorstCarriedMagazine { get; init; } = "none";
    public string BestLootMagazine { get; init; } = "none";
    public string BestLootMagazineItemId { get; init; } = "none";
    public bool BetterLootMagazineCandidate { get; init; }
    public string DiscardCandidate { get; init; } = "none";
    public string DiscardCandidateItemId { get; init; } = "none";
    public bool TopUpCandidate { get; init; }
    public int TopUpPotentialRounds { get; init; }

    public string Fingerprint => string.Join("|",
        WeaponItemId,
        CarriedMagazineCount,
        FullMagazineCount,
        PartialMagazineCount,
        QuarterOrLessMagazineCount,
        EmptyMagazineCount,
        CapacityUnknownMagazineCount,
        CarriedMagazineRounds,
        LooseCompatibleRounds,
        CompatibleLootMagazineCount,
        BestCarriedMagazine,
        WorstCarriedMagazine,
        BestLootMagazine,
        BestLootMagazineItemId,
        BetterLootMagazineCandidate,
        DiscardCandidate,
        DiscardCandidateItemId,
        TopUpCandidate,
        TopUpPotentialRounds);
}
#endif
