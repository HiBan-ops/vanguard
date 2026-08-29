#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EftWeapon = global::EFT.InventoryLogic.Weapon;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Integrations.Looting;
using Vanguard.Client.Runtime.Medical;

// Responsibility: consolidates bounded loot opportunities and Operator needs into the read model consumed by loot intent selection.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: it evaluates evidence but does not perform inventory transactions; claims, approach and transfer executors retain mutation authority and safety gates.
// Invariant: medical/combat safety and armament emergencies remain explicit inputs so an unrealizable treatment debt cannot silently starve an otherwise admissible rearm opportunity.

namespace Vanguard.Client.Runtime.Loot;

internal enum VanguardLootUtilityTier
{
    Low = 0,
    Opportunistic = 1,
    Reserve = 2,
    PlayerInterest = 3,
    Combat = 4,
    Critical = 5
}

internal enum VanguardLongWeaponArmamentNeedRank
{
    None = 0,
    RaidMutableUpgrade = 1,
    FillSecondLongWeaponSlot = 2,
    ZeroLongWeaponEmergency = 3
}

internal sealed class VanguardOperatorRaidLoadoutSnapshot
{
    public string BotProfileId { get; init; } = string.Empty;
    public string FirstPrimaryInitialItemId { get; init; } = "none";
    public string SecondPrimaryInitialItemId { get; init; } = "none";
    public EquipmentSlot? ProtectedPrimarySlot { get; init; }
    public EquipmentSlot? ProtectedSecondLongWeaponSlot { get; init; }
    public EquipmentSlot? RaidMutableLongWeaponSlot { get; init; }
    public bool Captured { get; init; }
    public string Source { get; init; } = "none";
}

internal static class VanguardOperatorRaidLoadoutRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, VanguardOperatorRaidLoadoutSnapshot> ByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly EquipmentSlot[] LongWeaponSlots = { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon };

    public static void ResetForRaidLifecycle(string source)
    {
        lock (Sync) ByBot.Clear();
        VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.UnifiedOpportunisticLootReadModelStatusTag,
            $"VANGUARD_LOADOUT_REGISTRY_RESET source={Safe(source)}");
    }

    public static bool TryGet(string? botProfileId, out VanguardOperatorRaidLoadoutSnapshot snapshot)
    {
        string bot = string.IsNullOrWhiteSpace(botProfileId) ? string.Empty : botProfileId.Trim();
        lock (Sync)
        {
            if (bot.Length > 0 && ByBot.TryGetValue(bot, out snapshot!)) return snapshot.Captured;
        }
        snapshot = new VanguardOperatorRaidLoadoutSnapshot { BotProfileId = bot, Captured = false, Source = "raid_loadout_not_captured" };
        return false;
    }

    public static VanguardOperatorRaidLoadoutSnapshot CaptureIfMissing(VanguardRaidOperatorRuntimeRecord record)
    {
        lock (Sync)
        {
            if (ByBot.TryGetValue(record.BotProfileId, out var existing)) return existing;
        }

        InventoryEquipment? equipment = record.BotOwner?.GetPlayer?.Inventory?.Equipment;
        if (equipment == null)
        {
            return new VanguardOperatorRaidLoadoutSnapshot
            {
                BotProfileId = record.BotProfileId,
                Captured = false,
                Source = "initial_equipment_not_ready_retry_later"
            };
        }
        var first = equipment.GetSlot(EquipmentSlot.FirstPrimaryWeapon)?.ContainedItem as EftWeapon;
        var second = equipment.GetSlot(EquipmentSlot.SecondPrimaryWeapon)?.ContainedItem as EftWeapon;
        int count = (first != null ? 1 : 0) + (second != null ? 1 : 0);
        if (count == 0 && (record.BotOwner == null || record.BotOwner.BotState != EBotState.Active))
        {
            return new VanguardOperatorRaidLoadoutSnapshot
            {
                BotProfileId = record.BotProfileId,
                Captured = false,
                Source = "initial_long_weapon_not_ready_retry_later"
            };
        }
        EquipmentSlot? protectedPrimary = count > 0
            ? first != null ? EquipmentSlot.FirstPrimaryWeapon : EquipmentSlot.SecondPrimaryWeapon
            : null;
        EquipmentSlot? protectedSecond = count >= 2 ? EquipmentSlot.SecondPrimaryWeapon : null;
        EquipmentSlot? mutable = count == 0
            ? EquipmentSlot.FirstPrimaryWeapon
            : count == 1
                ? first == null ? EquipmentSlot.FirstPrimaryWeapon : EquipmentSlot.SecondPrimaryWeapon
                : null;

        var snapshot = new VanguardOperatorRaidLoadoutSnapshot
        {
            BotProfileId = record.BotProfileId,
            FirstPrimaryInitialItemId = first?.Id ?? "none",
            SecondPrimaryInitialItemId = second?.Id ?? "none",
            ProtectedPrimarySlot = protectedPrimary,
            ProtectedSecondLongWeaponSlot = protectedSecond,
            RaidMutableLongWeaponSlot = mutable,
            Captured = true,
            Source = count == 0
                ? "initial_zero_long_weapons_two_acquisition_slots_after_bot_active"
                : count == 1
                    ? "initial_single_long_weapon_mutable_opposite_slot"
                    : "initial_two_long_weapons_both_protected"
        };
        lock (Sync) ByBot[record.BotProfileId] = snapshot;
        VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.UnifiedOpportunisticLootReadModelStatusTag,
            $"VANGUARD_INITIAL_LOADOUT_CAPTURED owner={Safe(record.OwnerProfileId)}; bot={Safe(record.BotProfileId)}; primary={snapshot.ProtectedPrimarySlot?.ToString() ?? "none"}; protectedSecond={snapshot.ProtectedSecondLongWeaponSlot?.ToString() ?? "none"}; raidMutable={snapshot.RaidMutableLongWeaponSlot?.ToString() ?? "none"}; first={Safe(snapshot.FirstPrimaryInitialItemId)}; second={Safe(snapshot.SecondPrimaryInitialItemId)}; source={snapshot.Source}");
        return snapshot;
    }

    public static bool IsInitialProtectedWeapon(VanguardOperatorRaidLoadoutSnapshot loadout, Item? item)
        => item != null
            && (string.Equals(loadout.FirstPrimaryInitialItemId, item.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(loadout.SecondPrimaryInitialItemId, item.Id, StringComparison.OrdinalIgnoreCase));

    public static bool IsRaidMutableLongWeaponSlot(
        VanguardOperatorRaidLoadoutSnapshot loadout,
        InventoryEquipment equipment,
        EquipmentSlot slotKind)
    {
        if (slotKind != EquipmentSlot.FirstPrimaryWeapon && slotKind != EquipmentSlot.SecondPrimaryWeapon) return false;
        Slot? slot = equipment.GetSlot(slotKind);
        return slot != null && (slot.ContainedItem == null || !IsInitialProtectedWeapon(loadout, slot.ContainedItem));
    }

    public static bool TryResolveRaidMutableLongWeaponSlot(
        VanguardOperatorRaidLoadoutSnapshot loadout,
        InventoryEquipment equipment,
        EftWeapon candidate,
        EquipmentSlot? preferredSlot,
        out EquipmentSlot slotKind,
        out Slot slot)
    {
        if (!loadout.Captured)
        {
            slotKind = default;
            slot = null!;
            return false;
        }

        // Free primary slots always win over replacement. Initial weapons are protected by item provenance,
        // not by permanently locking the physical slot that happened to contain them at raid start.
        foreach (EquipmentSlot candidateSlotKind in OrderedLongWeaponSlots(preferredSlot))
        {
            Slot? candidateSlot = equipment.GetSlot(candidateSlotKind);
            if (candidateSlot == null || candidateSlot.ContainedItem != null || !candidateSlot.CheckCompatibility(candidate)) continue;
            slotKind = candidateSlotKind;
            slot = candidateSlot;
            return true;
        }

        foreach (EquipmentSlot candidateSlotKind in OrderedLongWeaponSlots(preferredSlot))
        {
            Slot? candidateSlot = equipment.GetSlot(candidateSlotKind);
            if (candidateSlot == null || candidateSlot.ContainedItem == null || !candidateSlot.CheckCompatibility(candidate)) continue;
            if (IsInitialProtectedWeapon(loadout, candidateSlot.ContainedItem)) continue;
            slotKind = candidateSlotKind;
            slot = candidateSlot;
            return true;
        }

        slotKind = default;
        slot = null!;
        return false;
    }

    public static IReadOnlyList<EquipmentSlot> ResolveRaidMutableLongWeaponSlots(
        VanguardOperatorRaidLoadoutSnapshot loadout,
        InventoryEquipment? equipment)
    {
        if (!loadout.Captured || equipment == null) return Array.Empty<EquipmentSlot>();
        var result = new List<EquipmentSlot>(2);
        foreach (EquipmentSlot slotKind in LongWeaponSlots)
        {
            if (IsRaidMutableLongWeaponSlot(loadout, equipment, slotKind)) result.Add(slotKind);
        }
        return result;
    }

    private static IEnumerable<EquipmentSlot> OrderedLongWeaponSlots(EquipmentSlot? preferredSlot)
    {
        if (preferredSlot == EquipmentSlot.FirstPrimaryWeapon || preferredSlot == EquipmentSlot.SecondPrimaryWeapon)
        {
            yield return preferredSlot.Value;
            yield return preferredSlot.Value == EquipmentSlot.FirstPrimaryWeapon
                ? EquipmentSlot.SecondPrimaryWeapon
                : EquipmentSlot.FirstPrimaryWeapon;
            yield break;
        }
        yield return EquipmentSlot.FirstPrimaryWeapon;
        yield return EquipmentSlot.SecondPrimaryWeapon;
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
}

internal sealed class VanguardCorpseLootManifestItem
{
    public Item Item { get; init; } = null!;
    public string ItemId { get; init; } = "none";
    public string TemplateId { get; init; } = "none";
    public string Name { get; init; } = "none";
    public string Category { get; init; } = "generic";
    public string Path { get; init; } = "none";
    public int Quantity { get; init; } = 1;
    public int CellCount { get; init; } = 1;
    public float WeightKg { get; init; }
    public float Price { get; init; }
    public float ValuePerCell => CellCount > 0 ? Price / CellCount : Price;
    public string PriceSource { get; init; } = "none";
    public bool FoundInRaid { get; init; }
    public bool IsWeaponMod { get; init; }
    public bool RaidDetachable { get; init; }
    public string ParentWeaponItemId { get; init; } = "none";
}

internal sealed class VanguardCorpseLootManifest
{
    public static VanguardCorpseLootManifest Empty(string corpseId, string source)
        => Empty(corpseId, VanguardLootTargetKind.Corpse, source);

    public static VanguardCorpseLootManifest Empty(string targetId, VanguardLootTargetKind targetKind, string source)
        => new() { CorpseId = targetId, TargetKind = targetKind, Source = source };

    // CorpseId is retained as a compatibility alias for corpse-oriented call sites. TargetKind/TargetId
    // provide the target-agnostic identity required to represent world containers safely.
    public string CorpseId { get; init; } = "none";
    public string TargetId => CorpseId;
    public VanguardLootTargetKind TargetKind { get; init; } = VanguardLootTargetKind.Corpse;
    public long Revision { get; init; }
    public string Fingerprint { get; init; } = "none";
    public string Source { get; init; } = "none";
    public IReadOnlyList<VanguardCorpseLootManifestItem> Items { get; init; } = Array.Empty<VanguardCorpseLootManifestItem>();
    public int WeaponCount => Items.Count(item => item.Category == "long_weapon" || item.Category == "holster_weapon");
    public int MedicalCount => Items.Count(item => item.Category == "medical");
    public int GrenadeCount => Items.Count(item => item.Category == "grenade");
    public int GenericCount => Items.Count(item => item.Category == "generic" || item.Category == "weapon_mod");
}

/// <summary>
/// The corpse-oriented service name is retained for source compatibility while the underlying manifest authority
/// is target-agnostic: corpses and world containers share recursive item traversal, fingerprint/revision cache,
/// classification and value reads. Container manifests remain on-demand.
/// </summary>
internal static class VanguardCorpseLootManifestService
{
    private static readonly object Sync = new();
    private static readonly TimeSpan FingerprintRefreshInterval = TimeSpan.FromSeconds(1.5);
    private sealed record Cached(long Revision, string Fingerprint, VanguardCorpseLootManifest Manifest, DateTimeOffset CheckedAtUtc);
    private static readonly Dictionary<string, Cached> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static void ResetForRaidLifecycle(string source)
    {
        lock (Sync) Cache.Clear();
        VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.UnifiedOpportunisticLootReadModelStatusTag,
            $"VANGUARD_CORPSE_MANIFEST_RESET source={Safe(source)}");
    }

    public static bool Invalidate(VanguardLootTargetKind targetKind, string targetId)
    {
        string cacheKey = CacheKey(targetKind, targetId);
        lock (Sync)
        {
            if (!Cache.TryGetValue(cacheKey, out Cached? cached))
            {
                return false;
            }

            // Force the next fingerprint read while preserving the monotonic per-target revision lineage.
            Cache[cacheKey] = cached with { CheckedAtUtc = DateTimeOffset.MinValue };
            return true;
        }
    }

    public static VanguardCorpseLootManifest GetOrRefresh(VanguardCorpseRegistryEntry entry, DateTimeOffset now)
    {
        string cacheKey = CacheKey(VanguardLootTargetKind.Corpse, entry.CorpseId);
        if (TryGetRecent(cacheKey, now, out VanguardCorpseLootManifest recent)) return recent;
        if (entry.Corpse?.Item is not InventoryEquipment equipment)
        {
            return VanguardCorpseLootManifest.Empty(entry.CorpseId, VanguardLootTargetKind.Corpse, "corpse_equipment_missing");
        }

        List<(Item Item, string Path, string ParentWeapon)> raw = CollectCorpseEquipment(equipment);
        return BuildOrRefresh(cacheKey, entry.CorpseId, VanguardLootTargetKind.Corpse, raw,
            "corpse_equipment_recursive_readonly_no_secure_container", now);
    }

    public static VanguardCorpseLootManifest GetOrRefresh(VanguardWorldLootContainerSnapshot entry, DateTimeOffset now)
    {
        string cacheKey = CacheKey(VanguardLootTargetKind.WorldContainer, entry.ContainerId);
        if (TryGetRecent(cacheKey, now, out VanguardCorpseLootManifest recent)) return recent;
        if (entry.RootItem == null)
        {
            return VanguardCorpseLootManifest.Empty(entry.ContainerId, VanguardLootTargetKind.WorldContainer, "world_container_root_missing");
        }

        List<(Item Item, string Path, string ParentWeapon)> raw = CollectWorldContainerContents(entry.RootItem);
        return BuildOrRefresh(cacheKey, entry.ContainerId, VanguardLootTargetKind.WorldContainer, raw,
            "world_container_itemowner_root_recursive_readonly", now);
    }

    private static bool TryGetRecent(string cacheKey, DateTimeOffset now, out VanguardCorpseLootManifest manifest)
    {
        lock (Sync)
        {
            if (Cache.TryGetValue(cacheKey, out var recent) && now - recent.CheckedAtUtc <= FingerprintRefreshInterval)
            {
                manifest = recent.Manifest;
                return true;
            }
        }

        manifest = null!;
        return false;
    }

    private static VanguardCorpseLootManifest BuildOrRefresh(
        string cacheKey,
        string targetId,
        VanguardLootTargetKind targetKind,
        List<(Item Item, string Path, string ParentWeapon)> raw,
        string source,
        DateTimeOffset now)
    {
        string fingerprint = Fingerprint(raw);
        lock (Sync)
        {
            if (Cache.TryGetValue(cacheKey, out var cached) && string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                Cache[cacheKey] = cached with { CheckedAtUtc = now };
                return cached.Manifest;
            }
        }

        var items = new List<VanguardCorpseLootManifestItem>(raw.Count);
        foreach ((Item item, string path, string parentWeapon) in raw)
        {
            VanguardOrbitLootValueReader.TryGetPrice(item, out float price, out string priceSource);
            items.Add(new VanguardCorpseLootManifestItem
            {
                Item = item,
                ItemId = Safe(item.Id),
                TemplateId = Safe(item.StringTemplateId),
                Name = Safe(item.Name ?? item.ShortName ?? item.StringTemplateId),
                Category = Classify(item, path, parentWeapon),
                Path = path,
                Quantity = Quantity(item),
                CellCount = Math.Max(1, item.Width * item.Height),
                WeightKg = ReadWeight(item),
                Price = Math.Max(0f, price),
                PriceSource = priceSource,
                FoundInRaid = ReadBool(item, "SpawnedInSession", "MarkedAsSpawnedInSession"),
                IsWeaponMod = parentWeapon != "none" && item is not EftWeapon && item is not MagazineItemClass && item is not AmmoItemClass,
                RaidDetachable = IsRaidDetachableWeaponMod(item),
                ParentWeaponItemId = parentWeapon
            });
        }

        lock (Sync)
        {
            long revision = Cache.TryGetValue(cacheKey, out var current) ? current.Revision + 1 : 1;
            var manifest = new VanguardCorpseLootManifest
            {
                CorpseId = targetId,
                TargetKind = targetKind,
                Revision = revision,
                Fingerprint = fingerprint,
                Source = source,
                Items = items
            };
            Cache[cacheKey] = new Cached(revision, fingerprint, manifest, now);
            return manifest;
        }
    }

    private static List<(Item Item, string Path, string ParentWeapon)> CollectCorpseEquipment(InventoryEquipment equipment)
    {
        var result = new List<(Item, string, string)>();
        foreach (EquipmentSlot slotKind in new[] { EquipmentSlot.Pockets, EquipmentSlot.TacticalVest, EquipmentSlot.Backpack, EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster })
        {
            Item? root = equipment.GetSlot(slotKind)?.ContainedItem;
            if (root != null) CollectRecursive(root, slotKind.ToString(), "none", result);
        }
        return result;
    }

    private static List<(Item Item, string Path, string ParentWeapon)> CollectWorldContainerContents(Item rootItem)
    {
        var result = new List<(Item, string, string)>();
        string rootPath = "WorldContainer/" + Safe(rootItem.Id);
        CollectChildren(rootItem, rootPath, "none", result);
        return result;
    }

    private static void CollectRecursive(Item item, string path, string parentWeapon, ICollection<(Item, string, string)> destination)
    {
        string nextPath = path + "/" + Safe(item.Id);
        string owningWeapon = item is EftWeapon ? Safe(item.Id) : parentWeapon;
        destination.Add((item, nextPath, parentWeapon));
        if (item is MagazineItemClass magazine && magazine.Cartridges?.Items != null)
        {
            foreach (Item round in magazine.Cartridges.Items)
            {
                if (round != null) CollectRecursive(round, nextPath + "/cartridges", owningWeapon, destination);
            }
        }
        CollectChildren(item, nextPath, owningWeapon, destination);
    }

    private static void CollectChildren(Item item, string path, string parentWeapon, ICollection<(Item, string, string)> destination)
    {
        if (item is not CompoundItem compound) return;
        if (compound.Slots != null)
        {
            foreach (Slot slot in compound.Slots)
            {
                if (slot?.ContainedItem == null) continue;
                if ((slot.ID ?? string.Empty).IndexOf("Secured", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                CollectRecursive(slot.ContainedItem, path + "/slot:" + Safe(slot.ID), parentWeapon, destination);
            }
        }
        if (compound.Grids != null)
        {
            foreach (var grid in compound.Grids)
            {
                if (grid?.Items == null) continue;
                foreach (Item child in grid.Items)
                {
                    if (child != null) CollectRecursive(child, path + "/grid", parentWeapon, destination);
                }
            }
        }
    }

    private static string Fingerprint(IEnumerable<(Item Item, string Path, string ParentWeapon)> items)
    {
        string canonical = string.Join("\n", items.OrderBy(pair => pair.Item.Id, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Item.Id}|{pair.Item.StringTemplateId}|{Quantity(pair.Item)}|{ReadResource(pair.Item):0.###}|{pair.Path}"));
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static int Quantity(Item item) => item is AmmoItemClass ammo ? Math.Max(0, ammo.StackObjectsCount) : item is MagazineItemClass mag ? Math.Max(0, mag.Count) : 1;
    private static float ReadResource(Item item) => item is MedsItemClass meds ? Math.Max(0f, VanguardMedicalInventoryReader.ReadItemResource(meds)) : 0f;
    private static string Classify(Item item, string path, string parentWeapon)
    {
        if (item is PistolItemClass) return "holster_weapon";
        if (item is EftWeapon) return "long_weapon";
        if (item is MedsItemClass) return "medical";
        if (item is MagazineItemClass) return "magazine";
        if (item is AmmoItemClass) return "loose_ammunition";
        if (item is ThrowWeapItemClass) return "grenade";
        if (IsTopLevelCarryContainer(item, path, parentWeapon)) return "equipment_container";
        if (IsCarryEquipmentComponent(path, parentWeapon)) return "equipment_component";
        return parentWeapon != "none" ? "weapon_mod" : "generic";
    }

    private static bool IsTopLevelCarryContainer(Item item, string path, string parentWeapon)
    {
        if (parentWeapon != "none" || item is not CompoundItem || string.IsNullOrWhiteSpace(path)) return false;
        if (path.Count(character => character == '/') != 1) return false;
        return path.StartsWith(EquipmentSlot.Pockets + "/", StringComparison.Ordinal)
            || path.StartsWith(EquipmentSlot.TacticalVest + "/", StringComparison.Ordinal)
            || path.StartsWith(EquipmentSlot.Backpack + "/", StringComparison.Ordinal);
    }

    private static bool IsCarryEquipmentComponent(string path, string parentWeapon)
    {
        if (parentWeapon != "none" || string.IsNullOrWhiteSpace(path) || !path.Contains("/slot:", StringComparison.OrdinalIgnoreCase)) return false;
        return path.StartsWith(EquipmentSlot.Pockets + "/", StringComparison.Ordinal)
            || path.StartsWith(EquipmentSlot.TacticalVest + "/", StringComparison.Ordinal)
            || path.StartsWith(EquipmentSlot.Backpack + "/", StringComparison.Ordinal);
    }

    internal static bool IsRaidDetachableWeaponMod(Item? item)
    {
        if (item is not Mod mod || !mod.RaidModdable || item.CurrentAddress?.Container is not Slot sourceSlot)
        {
            return false;
        }

        return !sourceSlot.Required;
    }

    private static float ReadWeight(Item item)
    {
        object? value = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(item, "TotalWeight", "Weight");
        try { return value == null ? 0f : Math.Max(0f, Convert.ToSingle(value, CultureInfo.InvariantCulture)); } catch { return 0f; }
    }
    private static bool ReadBool(Item item, params string[] names)
    {
        object? value = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(item, names);
        try { return value != null && Convert.ToBoolean(value, CultureInfo.InvariantCulture); } catch { return false; }
    }
    private static string CacheKey(VanguardLootTargetKind kind, string targetId)
        => (kind == VanguardLootTargetKind.WorldContainer ? "world_container" : "corpse") + "|" + Safe(targetId);
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

internal sealed class VanguardWeaponQualitySnapshot
{
    public float TotalScore { get; init; }
    public float DurabilityFraction { get; init; }
    public int ReadyRounds { get; init; }
    public int ReserveRounds { get; init; }
    public int CompatibleMagazineCount { get; init; }
    public int BestPenetration { get; init; }
    public int BestDamage { get; init; }
    public float Ergonomics { get; init; }
    public float Recoil { get; init; }
    public float SightingRange { get; init; }
    public float PriceTieBreaker { get; init; }
    public bool Bricked { get; init; }
    public string Caliber { get; init; } = "none";
    public string Summary => $"score={TotalScore:0.0},dur={DurabilityFraction:0.00},ready={ReadyRounds},reserve={ReserveRounds},mags={CompatibleMagazineCount},pen={BestPenetration},dmg={BestDamage},ergo={Ergonomics:0.0},recoil={Recoil:0.0},sight={SightingRange:0.0},bricked={Bricked}";
}

internal static class VanguardWeaponQualityEvaluator
{
    public static VanguardWeaponQualitySnapshot Evaluate(EftWeapon? weapon, IEnumerable<VanguardCorpseLootManifestItem> source)
    {
        if (weapon == null) return new VanguardWeaponQualitySnapshot { Bricked = true };
        int ready = Math.Max(0, weapon.GetCurrentMagazineCount()) + Math.Max(0, weapon.ChamberAmmoCount);
        int reserve = 0, mags = 0, bestPen = 0, bestDamage = 0;
        foreach (VanguardCorpseLootManifestItem entry in source)
        {
            if (entry.Item is MagazineItemClass magazine && Fits(magazine, weapon) && !IsInsideWeapon(entry, weapon))
            {
                mags++;
                reserve += Math.Max(0, magazine.Count);
            }
            if (entry.Item is AmmoItemClass ammo && Fits(ammo, weapon))
            {
                bestPen = Math.Max(bestPen, ammo.PenetrationPower);
                bestDamage = Math.Max(bestDamage, ammo.Damage);
                if (!entry.Path.Contains("/cartridges", StringComparison.OrdinalIgnoreCase) && !IsInsideWeapon(entry, weapon))
                {
                    reserve += Math.Max(0, ammo.StackObjectsCount);
                }
            }
        }
        AmmoItemClass? loaded = weapon.GetCurrentMagazine()?.FirstRealAmmo() as AmmoItemClass;
        if (loaded != null)
        {
            bestPen = loaded.PenetrationPower;
            bestDamage = loaded.Damage;
        }
        float durability = weapon.Repairable?.MaxDurability > 0f ? weapon.Repairable.Durability / weapon.Repairable.MaxDurability : 0f;
        float ergo = weapon.ErgonomicsTotal;
        float recoil = weapon.RecoilTotal;
        float sight = weapon.GetSightingRange();
        VanguardOrbitLootValueReader.TryGetPrice(weapon, out float price, out _);
        int totalRounds = ready + reserve;
        bool bricked = totalRounds <= 0;
        float ammoCount = (float)Math.Log(1 + Math.Min(totalRounds, 200)) * 8f;
        float ammoQuality = bestPen * 3f + bestDamage * 0.5f - (bestPen > 0 && bestPen < 20 ? 30f : 0f);
        float stats = ergo * 0.75f - recoil * ResolveRecoilFactor(weapon) * 0.18f + sight * 0.025f;
        float condition = Math.Max(0f, durability) * 42f;
        float total = ammoCount + ammoQuality + stats + condition + price * 0.00002f;
        if (bricked) total *= 0.05f;
        if (durability < 0.20f) total *= 0.25f;
        return new VanguardWeaponQualitySnapshot
        {
            TotalScore = total,
            DurabilityFraction = durability,
            ReadyRounds = ready,
            ReserveRounds = reserve,
            CompatibleMagazineCount = mags,
            BestPenetration = bestPen,
            BestDamage = bestDamage,
            Ergonomics = ergo,
            Recoil = recoil,
            SightingRange = sight,
            PriceTieBreaker = price * 0.00002f,
            Bricked = bricked,
            Caliber = weapon.AmmoCaliber ?? "none"
        };
    }

    private static bool Fits(MagazineItemClass magazine, EftWeapon weapon) { try { return magazine.Count > 0 && weapon.GetMagazineSlot()?.CheckCompatibility(magazine) == true; } catch { return false; } }
    private static bool Fits(AmmoItemClass ammo, EftWeapon weapon) => ammo.StackObjectsCount > 0 && !string.IsNullOrWhiteSpace(ammo.Caliber) && string.Equals(ammo.Caliber, weapon.AmmoCaliber, StringComparison.OrdinalIgnoreCase);
    private static bool IsInsideWeapon(VanguardCorpseLootManifestItem entry, EftWeapon weapon) => entry.Path.Contains("/" + weapon.Id + "/", StringComparison.Ordinal);
    private static float ResolveRecoilFactor(EftWeapon weapon)
    {
        string caliber = weapon.AmmoCaliber ?? string.Empty;
        float factor = caliber.Contains("12g", StringComparison.OrdinalIgnoreCase) || caliber.Contains("20g", StringComparison.OrdinalIgnoreCase) || caliber.Contains("23x", StringComparison.OrdinalIgnoreCase) ? 0.5f : 1f;
        bool fullAuto = weapon.WeapFireType?.Any(mode => mode == EftWeapon.EFireMode.fullauto) == true;
        if (!fullAuto) factor *= 0.7f;
        return factor;
    }
}

internal sealed class VanguardLootItemUtility
{
    public string ItemId { get; init; } = "none";
    public string TemplateId { get; init; } = "none";
    public string Category { get; init; } = "none";
    public VanguardLootUtilityTier Tier { get; init; }
    public float Score { get; init; }
    public string Reason { get; init; } = "none";
    public string WishlistGroup { get; init; } = "none";
    public string WeaponQuality { get; init; } = "none";
    public EquipmentSlot? LongWeaponDestinationSlot { get; init; }
    public VanguardLongWeaponArmamentNeedRank ArmamentNeedRank { get; init; }
}

internal sealed class VanguardUnifiedLootReadModelObservation
{
    public string OwnerProfileId { get; init; } = "none";
    public string BotProfileId { get; init; } = "none";
    public VanguardLootTargetKind TargetKind { get; init; } = VanguardLootTargetKind.Corpse;
    public string TargetId { get; init; } = "none";
    // Compatibility alias retained for corpse-execution call sites. New target-agnostic
    // code must consume TargetKind + TargetId so container IDs never masquerade as corpse identity.
    public string CorpseId => TargetKind == VanguardLootTargetKind.Corpse ? TargetId : "none";
    public long ManifestRevision { get; init; }
    public long InterestRevision { get; init; }
    public string NeedSignature { get; init; } = "none";
    public EquipmentSlot? RaidMutableLongWeaponSlot { get; init; }
    public bool LegacyOwnerCorpseTerminal { get; init; }
    public bool FriendlyOperatorReadOnly { get; init; }
    public string RelationshipKind { get; init; } = "none";
    public IReadOnlyList<VanguardLootItemUtility> Utilities { get; init; } = Array.Empty<VanguardLootItemUtility>();
    public VanguardLootItemUtility? Best => Utilities.OrderByDescending(item => item.Tier).ThenByDescending(item => item.Score).FirstOrDefault();
}

internal static class VanguardUnifiedOpportunisticLootReadModelService
{
    private static readonly object LogSync = new();
    private static readonly Dictionary<string, string> LastLogSignature = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogAt = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RepeatLogInterval = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan OperatorWeaponContextLifetime = TimeSpan.FromSeconds(1.0);
    private sealed record CachedWeaponContext(
        DateTimeOffset CapturedAtUtc,
        VanguardOperatorRaidLoadoutSnapshot Loadout,
        IReadOnlyList<EftWeapon> CurrentWeapons,
        IReadOnlyList<VanguardCorpseLootManifestItem> OperatorInventorySupport,
        EquipmentSlot? CurrentMutableLongWeaponSlot,
        string CurrentMutableWeaponItemId,
        VanguardWeaponQualitySnapshot CurrentMutableQuality);
    private static readonly Dictionary<string, CachedWeaponContext> WeaponContextByBot = new(StringComparer.OrdinalIgnoreCase);

    public static void ResetForRaidLifecycle(string source)
    {
        lock (LogSync) { LastLogSignature.Clear(); LastLogAt.Clear(); WeaponContextByBot.Clear(); }
        VanguardOperatorRaidLoadoutRegistry.ResetForRaidLifecycle(source);
        VanguardCorpseLootManifestService.ResetForRaidLifecycle(source);
        VanguardSquadLootAssignmentService.ResetForRaidLifecycle(source);
    }

    public static void InvalidateWeaponContext(string? botProfileId, string source)
    {
        string bot = string.IsNullOrWhiteSpace(botProfileId) ? string.Empty : botProfileId.Trim();
        if (bot.Length == 0) return;
        bool removed;
        lock (LogSync) removed = WeaponContextByBot.Remove(bot);
        if (removed)
        {
            VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.ZeroLongWeaponAcquisitionCapacityStatusTag,
                $"VANGUARD_LONG_WEAPON_CONTEXT_INVALIDATED bot={Safe(bot)}; source={Safe(source)}; nextObservationFresh=true");
        }
    }

    public static VanguardUnifiedLootReadModelObservation Observe(
        VanguardRaidOperatorRuntimeRecord record,
        VanguardCorpseRegistryEntry corpse,
        VanguardOperatorLootNeedSnapshot need,
        VanguardMedicalDecisionSnapshot medical,
        VanguardMedicalInventoryReadResult medicalInventory,
        float directDistanceMeters,
        DateTimeOffset now,
        bool legacyOwnerCorpseTerminal,
        bool friendlyOperatorReadOnly,
        string relationshipKind)
    {
        VanguardCorpseLootManifest manifest = VanguardCorpseLootManifestService.GetOrRefresh(corpse, now);
        return ObserveTarget(
            record,
            VanguardLootTargetKind.Corpse,
            corpse.CorpseId,
            manifest,
            need,
            medical,
            medicalInventory,
            directDistanceMeters,
            now,
            legacyOwnerCorpseTerminal,
            friendlyOperatorReadOnly,
            relationshipKind);
    }

    /// <summary>
    /// The persistence path read-only world-container integration. It reuses exactly the same need/value evaluator and
    /// squad allocation authority as corpse loot, but it creates no physical target claim, movement,
    /// interaction, or inventory transaction. Persistent execution permission remains fail-closed.
    /// </summary>
    public static VanguardUnifiedLootReadModelObservation Observe(
        VanguardRaidOperatorRuntimeRecord record,
        VanguardWorldLootContainerSnapshot container,
        VanguardOperatorLootNeedSnapshot need,
        VanguardMedicalDecisionSnapshot medical,
        VanguardMedicalInventoryReadResult medicalInventory,
        float directDistanceMeters,
        DateTimeOffset now)
    {
        VanguardCorpseLootManifest manifest = VanguardCorpseLootManifestService.GetOrRefresh(container, now);
        return ObserveTarget(
            record,
            VanguardLootTargetKind.WorldContainer,
            container.ContainerId,
            manifest,
            need,
            medical,
            medicalInventory,
            directDistanceMeters,
            now,
            legacyOwnerCorpseTerminal: false,
            friendlyOperatorReadOnly: false,
            relationshipKind: "world_container");
    }

    private static VanguardUnifiedLootReadModelObservation ObserveTarget(
        VanguardRaidOperatorRuntimeRecord record,
        VanguardLootTargetKind targetKind,
        string targetId,
        VanguardCorpseLootManifest manifest,
        VanguardOperatorLootNeedSnapshot need,
        VanguardMedicalDecisionSnapshot medical,
        VanguardMedicalInventoryReadResult medicalInventory,
        float directDistanceMeters,
        DateTimeOffset now,
        bool legacyOwnerCorpseTerminal,
        bool friendlyOperatorReadOnly,
        string relationshipKind)
    {
        CachedWeaponContext weaponContext = ResolveWeaponContext(record, now);
        VanguardOperatorRaidLoadoutSnapshot loadout = weaponContext.Loadout;
        VanguardOwnerLootInterestSnapshot interest = VanguardOwnerLootInterestSyncService.Resolve(record.OwnerProfileId);
        IReadOnlyList<EftWeapon> currentWeapons = weaponContext.CurrentWeapons;
        VanguardWeaponQualitySnapshot currentMutableQuality = weaponContext.CurrentMutableQuality;

        var utilities = new List<VanguardLootItemUtility>();
        foreach (VanguardCorpseLootManifestItem item in manifest.Items)
        {
            VanguardLootItemUtility utility = EvaluateItem(item, manifest, interest, need, medical, medicalInventory, currentWeapons, weaponContext.OperatorInventorySupport,
                loadout, record.BotOwner?.GetPlayer?.Inventory?.Equipment, weaponContext.CurrentMutableLongWeaponSlot);
            if (utility.Tier > VanguardLootUtilityTier.Low || utility.Score > 0f) utilities.Add(utility);
        }

        var observation = new VanguardUnifiedLootReadModelObservation
        {
            OwnerProfileId = record.OwnerProfileId,
            BotProfileId = record.BotProfileId,
            TargetKind = targetKind,
            TargetId = NormalizeTargetId(targetId),
            ManifestRevision = manifest.Revision,
            InterestRevision = interest.Revision,
            NeedSignature = need.DecisionSignature + "||medical=" + medical.Need.Summary,
            RaidMutableLongWeaponSlot = weaponContext.CurrentMutableLongWeaponSlot,
            LegacyOwnerCorpseTerminal = legacyOwnerCorpseTerminal,
            FriendlyOperatorReadOnly = friendlyOperatorReadOnly,
            RelationshipKind = relationshipKind,
            Utilities = utilities
        };
        VanguardSquadLootAssignmentService.Observe(observation, directDistanceMeters, now);
        LogBounded(observation, manifest, interest, weaponContext.CurrentMutableWeaponItemId, currentMutableQuality, now);
        return observation;
    }

    private static VanguardLootItemUtility EvaluateItem(
        VanguardCorpseLootManifestItem item,
        VanguardCorpseLootManifest manifest,
        VanguardOwnerLootInterestSnapshot interest,
        VanguardOperatorLootNeedSnapshot need,
        VanguardMedicalDecisionSnapshot medical,
        VanguardMedicalInventoryReadResult medicalInventory,
        IReadOnlyList<EftWeapon> currentWeapons,
        IReadOnlyList<VanguardCorpseLootManifestItem> operatorInventorySupport,
        VanguardOperatorRaidLoadoutSnapshot loadout,
        InventoryEquipment? equipment,
        EquipmentSlot? preferredMutableLongWeaponSlot)
    {
        if (item.Category == "equipment_container" || item.Category == "equipment_component")
        {
            string reason = item.Category == "equipment_container"
                ? "protected_carry_equipment_container"
                : "protected_carry_equipment_component";
            return New(item, VanguardLootUtilityTier.Low, 0f, reason, "none", "none");
        }

        if (item.Item is EftWeapon candidateWeapon
            && item.Category == "long_weapon"
            && loadout.Captured
            && equipment != null
            && VanguardOperatorRaidLoadoutRegistry.TryResolveRaidMutableLongWeaponSlot(
                loadout, equipment, candidateWeapon, preferredMutableLongWeaponSlot, out EquipmentSlot destinationSlotKind, out Slot destinationSlot))
        {
            VanguardWeaponQualitySnapshot candidate = VanguardWeaponQualityEvaluator.Evaluate(candidateWeapon, manifest.Items.Concat(operatorInventorySupport));
            if (destinationSlot.ContainedItem is not EftWeapon currentMutableWeapon)
            {
                VanguardLootUtilityTier acquisitionTier = candidate.Bricked ? VanguardLootUtilityTier.Low : VanguardLootUtilityTier.Combat;
                float acquisitionScore = candidate.Bricked ? 0f : 100f + Math.Min(80f, Math.Max(0f, candidate.TotalScore) * 0.15f);
                string acquisitionReason = candidate.Bricked ? "empty_long_weapon_slot_bricked" : "empty_long_weapon_slot_acquisition";
                VanguardLongWeaponArmamentNeedRank armamentNeedRank = CountEquippedLongWeapons(equipment) == 0
                    ? VanguardLongWeaponArmamentNeedRank.ZeroLongWeaponEmergency
                    : VanguardLongWeaponArmamentNeedRank.FillSecondLongWeaponSlot;
                return ApplyWishlistFloor(item, interest, New(
                    item, acquisitionTier, acquisitionScore, acquisitionReason, "none", candidate.Summary, destinationSlotKind, armamentNeedRank));
            }

            VanguardWeaponQualitySnapshot currentMutableQuality = VanguardWeaponQualityEvaluator.Evaluate(currentMutableWeapon, operatorInventorySupport);
            float gain = candidate.TotalScore - currentMutableQuality.TotalScore;
            VanguardLootUtilityTier tier = gain >= 35f && !candidate.Bricked
                ? VanguardLootUtilityTier.Combat
                : gain > 10f ? VanguardLootUtilityTier.Reserve : VanguardLootUtilityTier.Low;
            return ApplyWishlistFloor(item, interest, New(
                item, tier, Math.Max(0f, gain), $"secondary_weapon_upgrade_gain={gain:0.0}", "none", candidate.Summary, destinationSlotKind,
                VanguardLongWeaponArmamentNeedRank.RaidMutableUpgrade));
        }

        if (item.Item is PistolItemClass pistol && need.HolsterSlotEmpty)
        {
            VanguardWeaponQualitySnapshot quality = VanguardWeaponQualityEvaluator.Evaluate(pistol, manifest.Items.Concat(operatorInventorySupport));
            VanguardLootUtilityTier tier = quality.Bricked ? VanguardLootUtilityTier.Low : VanguardLootUtilityTier.Reserve;
            return ApplyWishlistFloor(item, interest, New(item, tier, quality.Bricked ? 0f : 65f + Math.Min(60f, quality.TotalScore * 0.15f), quality.Bricked ? "empty_holster_weapon_bricked" : "empty_holster_weapon", "none", quality.Summary));
        }

        if (item.Item is MedsItemClass meds && VanguardOperatorLootNeedReader.IsUsableMedicalItem(meds))
        {
            (VanguardLootUtilityTier tier, float score, string reason) = EvaluateMedical(meds, medical, medicalInventory);
            return ApplyWishlistFloor(item, interest, New(item, tier, score, reason, "none", "none"));
        }

        if (item.Item is MagazineItemClass candidateMagazine && currentWeapons.Any(currentWeapon => Fits(candidateMagazine, currentWeapon)))
        {
            int rounds = Math.Max(0, candidateMagazine.Count);
            VanguardLootUtilityTier tier = need.NeedsCompatibleMagazine && rounds > 0 ? VanguardLootUtilityTier.Combat : rounds > 0 ? VanguardLootUtilityTier.Reserve : VanguardLootUtilityTier.Low;
            float score = (need.NeedsCompatibleMagazine ? 95f : 45f) + Math.Min(50, rounds);
            return ApplyWishlistFloor(item, interest, New(item, tier, score, need.NeedsCompatibleMagazine ? "compatible_magazine_deficit" : "compatible_magazine_reserve", "none", "none"));
        }

        if (item.Item is AmmoItemClass && item.Path.Contains("/cartridges", StringComparison.OrdinalIgnoreCase))
        {
            return New(item, VanguardLootUtilityTier.Low, 0f, "nested_cartridge_accounted_by_magazine_or_weapon_bundle", "none", "none");
        }

        if (item.Item is AmmoItemClass looseAmmo && currentWeapons.Any(currentWeapon => Fits(looseAmmo, currentWeapon)))
        {
            float combatQuality = looseAmmo.PenetrationPower * 2f + looseAmmo.Damage * 0.25f;
            VanguardLootUtilityTier tier = need.NeedsCompatibleAmmunition ? VanguardLootUtilityTier.Combat : VanguardLootUtilityTier.Reserve;
            return ApplyWishlistFloor(item, interest, New(item, tier, (need.NeedsCompatibleAmmunition ? 85f : 35f) + combatQuality + Math.Min(60, looseAmmo.StackObjectsCount) * 0.1f, need.NeedsCompatibleAmmunition ? "compatible_ammunition_deficit" : "compatible_ammunition_reserve", "none", "none"));
        }

        if (item.Item is ThrowWeapItemClass)
        {
            return ApplyWishlistFloor(item, interest, New(item, need.NeedsGrenade ? VanguardLootUtilityTier.Reserve : VanguardLootUtilityTier.Low, need.NeedsGrenade ? 70f : 5f, need.NeedsGrenade ? "grenade_reserve_deficit" : "grenade_reserve_satisfied", "none", "none"));
        }

        if (item.Category == "weapon_mod" && !item.RaidDetachable)
        {
            return New(item, VanguardLootUtilityTier.Low, 0f, "weapon_mod_not_raid_detachable", "none", "none");
        }

        if (interest.TryGetGroup(item.TemplateId, out string group))
        {
            float groupScore = group switch { "Quests" => 130f, "Hideout" => 115f, "Trading" => 100f, "Equipment" => 90f, _ => 80f };
            if (item.FoundInRaid && group == "Quests") groupScore += 20f;
            groupScore += Math.Min(35f, item.ValuePerCell / 5000f);
            return New(item, VanguardLootUtilityTier.PlayerInterest, groupScore, "eft_native_wishlist_" + group.ToLowerInvariant(), group, "none");
        }

        if (item.Price > 0f && item.Category is "generic" or "weapon_mod")
        {
            float score = Math.Min(100f, item.ValuePerCell / 1000f) + Math.Min(60f, item.Price / 10000f);
            VanguardLootUtilityTier tier = item.ValuePerCell >= 15000f || item.Price >= 75000f ? VanguardLootUtilityTier.Opportunistic : VanguardLootUtilityTier.Low;
            return New(item, tier, score, tier == VanguardLootUtilityTier.Opportunistic ? "high_economic_value" : "low_economic_value", "none", "none");
        }

        return New(item, VanguardLootUtilityTier.Low, 0f, "no_current_read_model_need", "none", "none");
    }

    private static (VanguardLootUtilityTier Tier, float Score, string Reason) EvaluateMedical(MedsItemClass meds, VanguardMedicalDecisionSnapshot medical, VanguardMedicalInventoryReadResult inventory)
    {
        string templateId = VanguardMedicalItemCapabilityResolver.NormalizeTemplateId(meds.StringTemplateId);
        float resource = Math.Max(0f, VanguardMedicalInventoryReader.ReadItemResource(meds));
        float maxResource = Math.Max(resource, VanguardMedicalInventoryReader.ReadItemMaxResource(meds));
        float resourceFraction = maxResource > 0f ? resource / maxResource : 1f;
        VanguardLootUtilityTier bestTier = VanguardLootUtilityTier.Reserve;
        float bestScore = 45f + resourceFraction * 25f;
        string reason = "medical_reserve";
        foreach (VanguardMedicalItemCapability capability in VanguardMedicalItemCapabilityResolver.Catalog)
        {
            if (!string.Equals(capability.TemplateId, templateId, StringComparison.OrdinalIgnoreCase)) continue;
            bool active = capability.Need switch
            {
                VanguardMedicalNeed.HeavyBleed => medical.Need.HasHeavyBleed,
                VanguardMedicalNeed.LightBleed => medical.Need.HasLightBleed,
                VanguardMedicalNeed.Fracture => medical.Need.HasFracture,
                VanguardMedicalNeed.HpHeal => medical.Need.HasHpDamage,
                VanguardMedicalNeed.PainMobility => medical.Need.HasPain,
                VanguardMedicalNeed.SurgeryDestroyedPart => medical.Need.HasOperableDestroyedPart,
                _ => false
            };
            bool stocked = HasUsableCapability(inventory, capability.Need);
            if (active && !stocked) return (VanguardLootUtilityTier.Critical, 220f + resourceFraction * 30f, "active_medical_need_uncovered_" + capability.Need);
            if (active && stocked && bestTier < VanguardLootUtilityTier.Combat) { bestTier = VanguardLootUtilityTier.Combat; bestScore = Math.Max(bestScore, 130f); reason = "active_medical_need_resupply_" + capability.Need; }
            if (!stocked) { bestTier = Max(bestTier, VanguardLootUtilityTier.Reserve); bestScore = Math.Max(bestScore, 90f); reason = "missing_medical_reserve_" + capability.Need; }
        }
        return (bestTier, bestScore, reason);
    }

    private static VanguardLootItemUtility ApplyWishlistFloor(VanguardCorpseLootManifestItem item, VanguardOwnerLootInterestSnapshot interest, VanguardLootItemUtility utility)
    {
        if (!interest.TryGetGroup(item.TemplateId, out string group) || utility.Tier >= VanguardLootUtilityTier.PlayerInterest) return utility;
        float groupScore = group switch { "Quests" => 130f, "Hideout" => 115f, "Trading" => 100f, "Equipment" => 90f, _ => 80f };
        if (item.FoundInRaid && group == "Quests") groupScore += 20f;
        return New(item, VanguardLootUtilityTier.PlayerInterest, Math.Max(groupScore, utility.Score), utility.Reason + "+eft_native_wishlist_" + group.ToLowerInvariant(), group, utility.WeaponQuality, utility.LongWeaponDestinationSlot, utility.ArmamentNeedRank);
    }

    private static bool HasUsableCapability(VanguardMedicalInventoryReadResult inventory, VanguardMedicalNeed need)
    {
        foreach (VanguardMedicalItemCapability candidate in VanguardMedicalItemCapabilityResolver.Catalog)
        {
            if (candidate.Need != need) continue;
            if (!inventory.ItemsByTemplateId.TryGetValue(candidate.TemplateId, out var owned)) continue;
            if (owned.Any(VanguardOperatorLootNeedReader.IsUsableMedicalItem)) return true;
        }
        return false;
    }

    private static VanguardLootItemUtility New(
        VanguardCorpseLootManifestItem item,
        VanguardLootUtilityTier tier,
        float score,
        string reason,
        string wishlistGroup,
        string weaponQuality,
        EquipmentSlot? longWeaponDestinationSlot = null,
        VanguardLongWeaponArmamentNeedRank armamentNeedRank = VanguardLongWeaponArmamentNeedRank.None) => new()
    {
        ItemId = item.ItemId,
        TemplateId = item.TemplateId,
        Category = item.Category,
        Tier = tier,
        Score = score,
        Reason = reason,
        WishlistGroup = wishlistGroup,
        WeaponQuality = weaponQuality,
        LongWeaponDestinationSlot = longWeaponDestinationSlot,
        ArmamentNeedRank = armamentNeedRank
    };

    private static int CountEquippedLongWeapons(InventoryEquipment equipment)
    {
        int count = 0;
        if (equipment.GetSlot(EquipmentSlot.FirstPrimaryWeapon)?.ContainedItem is EftWeapon) count++;
        if (equipment.GetSlot(EquipmentSlot.SecondPrimaryWeapon)?.ContainedItem is EftWeapon) count++;
        return count;
    }

    private static VanguardLootUtilityTier Max(VanguardLootUtilityTier a, VanguardLootUtilityTier b) => a > b ? a : b;

    private static CachedWeaponContext ResolveWeaponContext(VanguardRaidOperatorRuntimeRecord record, DateTimeOffset now)
    {
        lock (LogSync)
        {
            if (WeaponContextByBot.TryGetValue(record.BotProfileId, out var cached) && now - cached.CapturedAtUtc <= OperatorWeaponContextLifetime) return cached;
        }
        VanguardOperatorRaidLoadoutSnapshot loadout = VanguardOperatorRaidLoadoutRegistry.CaptureIfMissing(record);
        IReadOnlyList<EftWeapon> weapons = VanguardOperatorLootNeedReader.CaptureCurrentWeapons(record.BotOwner);
        IReadOnlyList<VanguardCorpseLootManifestItem> operatorInventorySupport = BuildOperatorInventoryManifest(record.BotOwner);
        InventoryEquipment? equipment = record.BotOwner?.GetPlayer?.Inventory?.Equipment;
        (EquipmentSlot? mutableSlot, EftWeapon? currentMutable, VanguardWeaponQualitySnapshot quality) =
            ResolvePreferredMutableWeapon(loadout, equipment, operatorInventorySupport);
        var next = new CachedWeaponContext(
            now, loadout, weapons, operatorInventorySupport, mutableSlot, currentMutable?.Id ?? "none", quality);
        lock (LogSync) WeaponContextByBot[record.BotProfileId] = next;
        return next;
    }

    private static (EquipmentSlot? Slot, EftWeapon? Weapon, VanguardWeaponQualitySnapshot Quality) ResolvePreferredMutableWeapon(
        VanguardOperatorRaidLoadoutSnapshot loadout,
        InventoryEquipment? equipment,
        IReadOnlyList<VanguardCorpseLootManifestItem> operatorInventorySupport)
    {
        IReadOnlyList<EquipmentSlot> mutableSlots = VanguardOperatorRaidLoadoutRegistry.ResolveRaidMutableLongWeaponSlots(loadout, equipment);
        if (equipment == null || mutableSlots.Count == 0)
        {
            return (null, null, VanguardWeaponQualityEvaluator.Evaluate(null, operatorInventorySupport));
        }

        foreach (EquipmentSlot slotKind in mutableSlots)
        {
            if (equipment.GetSlot(slotKind)?.ContainedItem == null)
            {
                return (slotKind, null, VanguardWeaponQualityEvaluator.Evaluate(null, operatorInventorySupport));
            }
        }

        EquipmentSlot? weakestSlot = null;
        EftWeapon? weakestWeapon = null;
        VanguardWeaponQualitySnapshot? weakestQuality = null;
        foreach (EquipmentSlot slotKind in mutableSlots)
        {
            if (equipment.GetSlot(slotKind)?.ContainedItem is not EftWeapon weapon) continue;
            VanguardWeaponQualitySnapshot quality = VanguardWeaponQualityEvaluator.Evaluate(weapon, operatorInventorySupport);
            if (weakestQuality == null || quality.TotalScore < weakestQuality.TotalScore)
            {
                weakestSlot = slotKind;
                weakestWeapon = weapon;
                weakestQuality = quality;
            }
        }
        return (weakestSlot, weakestWeapon, weakestQuality ?? VanguardWeaponQualityEvaluator.Evaluate(null, operatorInventorySupport));
    }
    private static IReadOnlyList<VanguardCorpseLootManifestItem> BuildOperatorInventoryManifest(BotOwner? botOwner)
    {
        var result = new List<VanguardCorpseLootManifestItem>();
        var items = new List<Item>();
        if (botOwner?.GetPlayer?.InventoryController == null) return result;
        botOwner.GetPlayer.InventoryController.GetAcceptableItemsNonAlloc(new[] { EquipmentSlot.Pockets, EquipmentSlot.TacticalVest, EquipmentSlot.Backpack, EquipmentSlot.SecuredContainer }, items);
        foreach (Item item in items) result.Add(new VanguardCorpseLootManifestItem { Item = item, ItemId = item.Id, TemplateId = item.StringTemplateId, Category = item is MagazineItemClass ? "magazine" : item is AmmoItemClass ? "ammunition" : "inventory", Path = "operator_inventory" });
        return result;
    }
    private static bool Fits(MagazineItemClass magazine, EftWeapon weapon) { try { return weapon.GetMagazineSlot()?.CheckCompatibility(magazine) == true; } catch { return false; } }
    private static bool Fits(AmmoItemClass ammo, EftWeapon weapon) => !string.IsNullOrWhiteSpace(ammo.Caliber) && string.Equals(ammo.Caliber, weapon.AmmoCaliber, StringComparison.OrdinalIgnoreCase);

    private static void LogBounded(VanguardUnifiedLootReadModelObservation observation, VanguardCorpseLootManifest manifest, VanguardOwnerLootInterestSnapshot interest, string currentMutableWeaponItemId, VanguardWeaponQualitySnapshot currentMutableQuality, DateTimeOffset now)
    {
        VanguardLootItemUtility? best = observation.Best;
        string key = observation.BotProfileId + "|" + observation.TargetKind + "|" + observation.TargetId;
        string signature = $"{observation.ManifestRevision}|{best?.ItemId}|{best?.Tier}|{best?.Score:0}|{best?.Reason}|{interest.Revision}|{observation.LegacyOwnerCorpseTerminal}";
        lock (LogSync)
        {
            if (LastLogSignature.TryGetValue(key, out var previous) && previous == signature && LastLogAt.TryGetValue(key, out DateTimeOffset last) && now - last < RepeatLogInterval) return;
            LastLogSignature[key] = signature;
            LastLogAt[key] = now;
        }
        string targetKindToken = observation.TargetKind == VanguardLootTargetKind.WorldContainer ? "world_container" : "corpse";
        string readModelEvent = observation.TargetKind == VanguardLootTargetKind.WorldContainer
            ? "VANGUARD_CONTAINER_LOOT_READ_MODEL"
            : "VANGUARD_LOOT_READ_MODEL";
        VanguardClientDiagnosticsLog.Info(
            observation.TargetKind == VanguardLootTargetKind.WorldContainer
                ? VanguardBuildVersion.ContainerScoringAndSquadAllocationIntegrationStatusTag
                : VanguardBuildVersion.UnifiedOpportunisticLootReadModelStatusTag,
            $"{readModelEvent} owner={Safe(observation.OwnerProfileId)}; bot={Safe(observation.BotProfileId)}; targetKind={targetKindToken}; target={Safe(observation.TargetId)}; corpse={Safe(observation.CorpseId)}; manifestRevision={observation.ManifestRevision}; items={manifest.Items.Count}; weapons={manifest.WeaponCount}; meds={manifest.MedicalCount}; grenades={manifest.GrenadeCount}; generic={manifest.GenericCount}; wishlistKnown={interest.Known}; wishlistRevision={interest.Revision}; raidMutable={observation.RaidMutableLongWeaponSlot?.ToString() ?? "none"}; currentMutableWeapon={currentMutableQuality.Summary}; bestItem={Safe(best?.ItemId)}; bestCategory={Safe(best?.Category)}; bestTier={best?.Tier.ToString() ?? "none"}; bestScore={(best?.Score ?? 0f):0.0}; bestReason={Safe(best?.Reason)}; wishlistGroup={Safe(best?.WishlistGroup)}; legacyOwnerTerminal={observation.LegacyOwnerCorpseTerminal}; friendlyOperatorReadOnly={observation.FriendlyOperatorReadOnly}; relationship={Safe(observation.RelationshipKind)}; scoring=true; squadAllocation=true; physicalClaim=false; movement=false; opening=false; transaction=false; readOnly=true");
        if (observation.TargetKind == VanguardLootTargetKind.Corpse
            && observation.LegacyOwnerCorpseTerminal && best != null && best.Tier > VanguardLootUtilityTier.Low)
        {
            VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.UnifiedOpportunisticLootReadModelStatusTag,
                $"VANGUARD_LEGACY_TERMINAL_USEFULNESS_PREVIEW owner={Safe(observation.OwnerProfileId)}; bot={Safe(observation.BotProfileId)}; corpse={Safe(observation.CorpseId)}; manifestRevision={observation.ManifestRevision}; usefulAfterLegacyTerminal=true; bestItem={Safe(best.ItemId)}; tier={best.Tier}; score={best.Score:0.0}; reason={Safe(best.Reason)}; friendlyOperatorReadOnly={observation.FriendlyOperatorReadOnly}; relationship={Safe(observation.RelationshipKind)}; terminalGateChanged=false; claimCreated=false; mutation=false");
        }
        if (observation.TargetKind == VanguardLootTargetKind.Corpse
            && observation.FriendlyOperatorReadOnly && best != null && best.Tier > VanguardLootUtilityTier.Low)
        {
            VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.UnifiedOpportunisticLootReadModelStatusTag,
                $"VANGUARD_FRIENDLY_OPERATOR_USEFULNESS_PREVIEW owner={Safe(observation.OwnerProfileId)}; bot={Safe(observation.BotProfileId)}; corpse={Safe(observation.CorpseId)}; manifestRevision={observation.ManifestRevision}; bestItem={Safe(best.ItemId)}; tier={best.Tier}; score={best.Score:0.0}; reason={Safe(best.Reason)}; relationship={Safe(observation.RelationshipKind)}; operatorCorpseExecutionSemanticsChanged=true; persistenceGate=operator_postraid_persistence_not_armed; claimCreated=false; mutation=false");
        }

        VanguardLootItemUtility? bestAcquisition = observation.Utilities
            .Where(value => value.Category == "long_weapon"
                && value.Tier >= VanguardLootUtilityTier.Combat
                && value.Reason.StartsWith("empty_long_weapon_slot_acquisition", StringComparison.Ordinal))
            .OrderByDescending(value => value.Tier)
            .ThenByDescending(value => value.Score)
            .FirstOrDefault();
        if (bestAcquisition != null)
        {
            VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.ZeroLongWeaponAcquisitionCapacityStatusTag,
                $"VANGUARD_EMPTY_LONG_WEAPON_SLOT_ACQUISITION_PREVIEW owner={Safe(observation.OwnerProfileId)}; bot={Safe(observation.BotProfileId)}; targetKind={targetKindToken}; target={Safe(observation.TargetId)}; candidateWeapon={Safe(bestAcquisition.ItemId)}; destination={bestAcquisition.LongWeaponDestinationSlot?.ToString() ?? "none"}; candidateQuality={Safe(bestAcquisition.WeaponQuality)}; tier={bestAcquisition.Tier}; score={bestAcquisition.Score:0.0}; freeSlotPreferred=true; initialWeaponProtectionPreserved=true; mutation=false");
        }

        VanguardLootItemUtility? bestReplacement = observation.Utilities
            .Where(value => value.Category == "long_weapon"
                && value.Tier >= VanguardLootUtilityTier.Reserve
                && value.Reason.StartsWith("secondary_weapon_upgrade_gain=", StringComparison.Ordinal))
            .OrderByDescending(value => value.Tier)
            .ThenByDescending(value => value.Score)
            .FirstOrDefault();
        if (observation.TargetKind == VanguardLootTargetKind.Corpse
            && !string.Equals(currentMutableWeaponItemId, "none", StringComparison.OrdinalIgnoreCase) && bestReplacement != null)
        {
            VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.SecondaryWeaponReplacementStatusTag,
                $"VANGUARD_SECONDARY_REPLACEMENT_PREVIEW owner={Safe(observation.OwnerProfileId)}; bot={Safe(observation.BotProfileId)}; corpse={Safe(observation.CorpseId)}; mutableSlot={observation.RaidMutableLongWeaponSlot?.ToString() ?? "none"}; currentWeapon={Safe(currentMutableWeaponItemId)}; currentQuality={currentMutableQuality.Summary}; candidateWeapon={Safe(bestReplacement.ItemId)}; candidateQuality={Safe(bestReplacement.WeaponQuality)}; gain={bestReplacement.Score:0.0}; tier={bestReplacement.Tier}; primaryLoadoutProtected=true; swapExecuted=false; claimCreated=false; mutation=false");
        }
    }
    private static string NormalizeTargetId(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

internal sealed record VanguardSquadLootItemAssignment(
    string OwnerProfileId,
    VanguardLootTargetKind TargetKind,
    string TargetId,
    string ItemId,
    string TemplateId,
    string Category,
    string AssignedBotProfileId,
    VanguardLootUtilityTier Tier,
    float UtilityScore,
    float DirectDistanceMeters,
    VanguardLongWeaponArmamentNeedRank ArmamentNeedRank,
    string Reason,
    string WishlistGroup,
    EquipmentSlot? LongWeaponDestinationSlot,
    long ManifestRevision,
    long InterestRevision,
    string NeedSignature,
    DateTimeOffset ObservedAtUtc)
{
    // Compatibility alias for the corpse-execution pipeline. Container assignments are read-only here
    // and must consume TargetKind/TargetId instead of this alias.
    public string CorpseId => TargetKind == VanguardLootTargetKind.Corpse ? TargetId : "none";
    public float ExecutionScore => ((int)Tier * 1000f) + Math.Max(0f, UtilityScore);
}

internal static class VanguardSquadLootAssignmentService
{
    private static readonly object Sync = new();
    private static readonly TimeSpan AssignmentLifetime = TimeSpan.FromSeconds(55);
    // A more severe long-weapon deficit may win squad allocation only within this direct-distance
    // budget. Seven metres matches the established opportunistic-loot detour budget and prevents
    // a zero-long-weapon Operator from reserving loot from an operationally remote teammate.
    private const float ArmamentNeedPriorityDistanceBudgetMeters = 7.0f;
    private static readonly Dictionary<string, VanguardSquadLootItemAssignment> BestByOwnerTargetItem = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> LastWinnerSignature = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> LatestNeedSignatureByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> LatestInterestRevisionByOwner = new(StringComparer.OrdinalIgnoreCase);

    public static void ResetForRaidLifecycle(string source)
    {
        lock (Sync)
        {
            BestByOwnerTargetItem.Clear();
            LastWinnerSignature.Clear();
            LatestNeedSignatureByBot.Clear();
            LatestInterestRevisionByOwner.Clear();
        }
        VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.UtilityClaimedLootActivationStatusTag, $"VANGUARD_SQUAD_ASSIGNMENT_AUTHORITY_RESET source={Safe(source)}");
    }

    public static void Observe(VanguardUnifiedLootReadModelObservation observation, float directDistanceMeters, DateTimeOffset now)
    {
        List<VanguardLootItemUtility> currentUtilities = observation.Utilities
            .Where(value => value.Tier > VanguardLootUtilityTier.Low)
            .OrderByDescending(value => value.Tier)
            .ThenByDescending(value => value.Score)
            .Take(8)
            .ToList();
        var currentItemIds = new HashSet<string>(currentUtilities.Select(value => Normalize(value.ItemId)), StringComparer.OrdinalIgnoreCase);
        lock (Sync)
        {
            PurgeLocked(now);
            string owner = Normalize(observation.OwnerProfileId);
            string target = Normalize(observation.TargetId);
            VanguardLootTargetKind targetKind = observation.TargetKind;
            string bot = Normalize(observation.BotProfileId);
            string needSignature = NormalizeSignature(observation.NeedSignature);

            if (LatestInterestRevisionByOwner.TryGetValue(owner, out long previousInterestRevision)
                && previousInterestRevision != observation.InterestRevision)
            {
                RemoveAssignmentsLocked(pair =>
                    string.Equals(Normalize(pair.Value.OwnerProfileId), owner, StringComparison.OrdinalIgnoreCase));
            }
            LatestInterestRevisionByOwner[owner] = observation.InterestRevision;

            if (LatestNeedSignatureByBot.TryGetValue(bot, out string previousNeedSignature)
                && !string.Equals(previousNeedSignature, needSignature, StringComparison.Ordinal))
            {
                RemoveAssignmentsLocked(pair =>
                    string.Equals(Normalize(pair.Value.AssignedBotProfileId), bot, StringComparison.OrdinalIgnoreCase));
            }
            LatestNeedSignatureByBot[bot] = needSignature;

            RemoveAssignmentsLocked(pair =>
                string.Equals(Normalize(pair.Value.OwnerProfileId), owner, StringComparison.OrdinalIgnoreCase)
                && pair.Value.TargetKind == targetKind
                && string.Equals(Normalize(pair.Value.TargetId), target, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Normalize(pair.Value.AssignedBotProfileId), bot, StringComparison.OrdinalIgnoreCase)
                && (pair.Value.ManifestRevision != observation.ManifestRevision
                    || pair.Value.InterestRevision != observation.InterestRevision
                    || !string.Equals(NormalizeSignature(pair.Value.NeedSignature), needSignature, StringComparison.Ordinal)
                    || !currentItemIds.Contains(Normalize(pair.Value.ItemId))));
        }

        foreach (VanguardLootItemUtility utility in currentUtilities)
        {
            string key = Key(observation.OwnerProfileId, observation.TargetKind, observation.TargetId, utility.ItemId);
            var next = new VanguardSquadLootItemAssignment(
                observation.OwnerProfileId, observation.TargetKind, observation.TargetId, utility.ItemId, utility.TemplateId, utility.Category, observation.BotProfileId,
                utility.Tier, utility.Score, directDistanceMeters, utility.ArmamentNeedRank, utility.Reason, utility.WishlistGroup, utility.LongWeaponDestinationSlot, observation.ManifestRevision,
                observation.InterestRevision, observation.NeedSignature, now);
            VanguardSquadLootItemAssignment winner;
            lock (Sync)
            {
                PurgeLocked(now);
                if (BestByOwnerTargetItem.TryGetValue(key, out var current)
                    && current.ManifestRevision == observation.ManifestRevision
                    && !string.Equals(current.AssignedBotProfileId, next.AssignedBotProfileId, StringComparison.OrdinalIgnoreCase)
                    && Better(current, next))
                {
                    winner = current;
                }
                else
                {
                    // A fresh observation from the current winner replaces its own older utility even when
                    // the score decreased (medical need resolved, inventory changed, etc.).
                    BestByOwnerTargetItem[key] = next;
                    winner = next;
                }
                string signature = winner.AssignedBotProfileId + "|" + winner.Tier + "|" + winner.UtilityScore.ToString("0", CultureInfo.InvariantCulture) + "|" + winner.ArmamentNeedRank + "|" + winner.ManifestRevision + "|" + winner.InterestRevision;
                if (LastWinnerSignature.TryGetValue(key, out var previous) && previous == signature) continue;
                LastWinnerSignature[key] = signature;
            }
            string assignmentTargetKind = winner.TargetKind == VanguardLootTargetKind.WorldContainer ? "world_container" : "corpse";
            string assignmentEvent = winner.TargetKind == VanguardLootTargetKind.WorldContainer
                ? "VANGUARD_SQUAD_CONTAINER_ITEM_ASSIGNMENT_AUTHORITY"
                : "VANGUARD_SQUAD_ITEM_ASSIGNMENT_AUTHORITY";
            VanguardClientDiagnosticsLog.Info(
                winner.TargetKind == VanguardLootTargetKind.WorldContainer
                    ? VanguardBuildVersion.ContainerScoringAndSquadAllocationIntegrationStatusTag
                    : VanguardBuildVersion.UtilityClaimedLootActivationStatusTag,
                $"{assignmentEvent} owner={Safe(winner.OwnerProfileId)}; targetKind={assignmentTargetKind}; target={Safe(winner.TargetId)}; corpse={Safe(winner.CorpseId)}; item={Safe(winner.ItemId)}; assignedBot={Safe(winner.AssignedBotProfileId)}; tier={winner.Tier}; score={winner.UtilityScore:0.0}; distance={winner.DirectDistanceMeters:0.0}; reason={Safe(winner.Reason)}; longWeaponDestination={winner.LongWeaponDestinationSlot?.ToString() ?? "none"}; armamentNeed={winner.ArmamentNeedRank}; manifestRevision={winner.ManifestRevision}; interestRevision={winner.InterestRevision}; assignmentAuthority=true; claimCreatedAtObservation=false; mutationAtObservation=false; physicalClaimCreatedAtObservation=false; movement=false; opening=false; transaction=false");
            if (winner.Category == "long_weapon" && winner.ArmamentNeedRank != VanguardLongWeaponArmamentNeedRank.None)
            {
                VanguardClientDiagnosticsLog.Info(
                    VanguardBuildVersion.ArmamentDeficitSquadPriorityStatusTag,
                    $"VANGUARD_ARMAMENT_DEFICIT_SQUAD_PRIORITY owner={Safe(winner.OwnerProfileId)}; targetKind={assignmentTargetKind}; target={Safe(winner.TargetId)}; item={Safe(winner.ItemId)}; assignedBot={Safe(winner.AssignedBotProfileId)}; armamentNeed={winner.ArmamentNeedRank}; distance={winner.DirectDistanceMeters:0.0}; priorityDistanceBudget={ArmamentNeedPriorityDistanceBudgetMeters:0.0}; tier={winner.Tier}; score={winner.UtilityScore:0.0}; destination={winner.LongWeaponDestinationSlot?.ToString() ?? "none"}; reason={Safe(winner.Reason)}; existingPathAndExecutionFeasibilityGatesPreserved=true; mutation=false");
            }
        }
    }

    public static IReadOnlyList<VanguardSquadLootItemAssignment> GetAssignmentsForBot(
        string ownerProfileId, string corpseId, string botProfileId, long manifestRevision, DateTimeOffset now)
        => GetAssignmentsForBot(ownerProfileId, VanguardLootTargetKind.Corpse, corpseId, botProfileId, manifestRevision, now);

    public static IReadOnlyList<VanguardSquadLootItemAssignment> GetAssignmentsForBot(
        string ownerProfileId, VanguardLootTargetKind targetKind, string targetId, string botProfileId, long manifestRevision, DateTimeOffset now)
    {
        string owner = Normalize(ownerProfileId);
        string target = Normalize(targetId);
        string bot = Normalize(botProfileId);
        lock (Sync)
        {
            PurgeLocked(now);
            return BestByOwnerTargetItem.Values
                .Where(value => string.Equals(Normalize(value.OwnerProfileId), owner, StringComparison.OrdinalIgnoreCase)
                    && value.TargetKind == targetKind
                    && string.Equals(Normalize(value.TargetId), target, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Normalize(value.AssignedBotProfileId), bot, StringComparison.OrdinalIgnoreCase)
                    && value.ManifestRevision == manifestRevision)
                .OrderByDescending(value => value.Tier)
                .ThenByDescending(value => value.UtilityScore)
                .ThenBy(value => value.ItemId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public static bool TryGetAssignment(
        string ownerProfileId, string corpseId, string itemId, long manifestRevision, DateTimeOffset now, out VanguardSquadLootItemAssignment assignment)
        => TryGetAssignment(ownerProfileId, VanguardLootTargetKind.Corpse, corpseId, itemId, manifestRevision, now, out assignment);

    public static bool TryGetAssignment(
        string ownerProfileId, VanguardLootTargetKind targetKind, string targetId, string itemId, long manifestRevision, DateTimeOffset now, out VanguardSquadLootItemAssignment assignment)
    {
        lock (Sync)
        {
            PurgeLocked(now);
            if (BestByOwnerTargetItem.TryGetValue(Key(ownerProfileId, targetKind, targetId, itemId), out var found)
                && found.TargetKind == targetKind
                && found.ManifestRevision == manifestRevision)
            {
                assignment = found;
                return true;
            }
        }
        assignment = null!;
        return false;
    }

    public static bool IsAssignedToBot(
        string ownerProfileId, string corpseId, string itemId, string botProfileId, long manifestRevision, DateTimeOffset now, out VanguardSquadLootItemAssignment assignment)
        => IsAssignedToBot(ownerProfileId, VanguardLootTargetKind.Corpse, corpseId, itemId, botProfileId, manifestRevision, now, out assignment);

    public static bool IsAssignedToBot(
        string ownerProfileId, VanguardLootTargetKind targetKind, string targetId, string itemId, string botProfileId, long manifestRevision, DateTimeOffset now, out VanguardSquadLootItemAssignment assignment)
    {
        if (TryGetAssignment(ownerProfileId, targetKind, targetId, itemId, manifestRevision, now, out assignment))
        {
            return string.Equals(Normalize(assignment.AssignedBotProfileId), Normalize(botProfileId), StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static bool Better(VanguardSquadLootItemAssignment current, VanguardSquadLootItemAssignment candidate)
    {
        if (current.Category == "long_weapon"
            && candidate.Category == "long_weapon"
            && current.ArmamentNeedRank != VanguardLongWeaponArmamentNeedRank.None
            && candidate.ArmamentNeedRank != VanguardLongWeaponArmamentNeedRank.None
            && current.ArmamentNeedRank != candidate.ArmamentNeedRank)
        {
            bool currentHasHigherArmamentNeed = current.ArmamentNeedRank > candidate.ArmamentNeedRank;
            float higherNeedDistance = currentHasHigherArmamentNeed ? current.DirectDistanceMeters : candidate.DirectDistanceMeters;
            float lowerNeedDistance = currentHasHigherArmamentNeed ? candidate.DirectDistanceMeters : current.DirectDistanceMeters;

            // Strong but bounded squad doctrine: zero-long-weapon emergency > second-slot fill > raid-mutable upgrade
            // while the higher-need Operator remains within one established loot-detour budget of the lower-need peer.
            // Beyond that budget, proximity wins this item; normal path/approach/execution feasibility still applies later.
            return higherNeedDistance <= lowerNeedDistance + ArmamentNeedPriorityDistanceBudgetMeters
                ? currentHasHigherArmamentNeed
                : !currentHasHigherArmamentNeed;
        }

        if (current.Tier != candidate.Tier) return current.Tier > candidate.Tier;
        if (Math.Abs(current.UtilityScore - candidate.UtilityScore) > 0.01f) return current.UtilityScore > candidate.UtilityScore;
        return current.DirectDistanceMeters <= candidate.DirectDistanceMeters;
    }


    private static void RemoveAssignmentsLocked(Func<KeyValuePair<string, VanguardSquadLootItemAssignment>, bool> predicate)
    {
        foreach (string staleKey in BestByOwnerTargetItem.Where(predicate).Select(pair => pair.Key).ToList())
        {
            BestByOwnerTargetItem.Remove(staleKey);
            LastWinnerSignature.Remove(staleKey);
        }
    }

    private static void PurgeLocked(DateTimeOffset now)
    {
        foreach (string key in BestByOwnerTargetItem.Where(pair => now - pair.Value.ObservedAtUtc > AssignmentLifetime).Select(pair => pair.Key).ToList())
        {
            BestByOwnerTargetItem.Remove(key);
            LastWinnerSignature.Remove(key);
        }
    }

    private static string Key(string owner, VanguardLootTargetKind targetKind, string target, string item)
        => Normalize(owner) + "|" + targetKind + "|" + Normalize(target) + "|" + Normalize(item);
    private static string NormalizeSignature(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
}
#endif
