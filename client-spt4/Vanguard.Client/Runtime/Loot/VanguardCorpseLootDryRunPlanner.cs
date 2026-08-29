#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EftWeapon = global::EFT.InventoryLogic.Weapon;
using Vanguard.Client.Runtime.Medical;

// Responsibility: Evaluates whether an Operator may pursue a corpse-loot opportunity and builds the corresponding non-mutating plan.
// Flow: It combines corpse registry evidence, medical/combat safety, armament urgency, authored-position/cohesion rules and loot policy to rank/qualify a target before any physical movement or inventory transaction is attempted.
// Authority boundary: Planning never owns inventory or movement authority; downstream claim/executor paths must reacquire and revalidate those authorities.
// Invariant: Unsafe/stale corpses are rejected, medical emergencies keep their precedence except the qualified unrealizable-surgery fallback, and plans remain disposable when runtime truth changes.
namespace Vanguard.Client.Runtime.Loot;

internal static class VanguardCorpseLootDryRunPlanner
{
    private const int MaximumPlanEntries = 8;
    private const int MaximumMedicalEntries = 4;
    private const int MaximumPlacementPreviews = 16;
    private const int DesiredMagazineCount = 3;
    private const int DesiredGrenadeCount = 2;

    private sealed record SourceItem(Item Item, string Path);
    private sealed record WeaponChoice(EftWeapon Weapon, EquipmentSlot Slot, string Path, float Score);
    private sealed record PendingAutoPlace(SourceItem Source, string Category, string Reason, float Score, string StopCondition);

    public static VanguardCorpseLootDryRunPlan Build(
        Corpse corpse,
        BotOwner botOwner,
        IReadOnlyList<EftWeapon> currentWeapons,
        VanguardOperatorLootNeedSnapshot need,
        VanguardOperatorLootPermissionSnapshot permissions)
    {
        if (!VanguardOperatorLootTargetPermissionPolicy.AllowsTarget(
                permissions,
                VanguardLootTargetKind.Corpse,
                out _))
        {
            return VanguardCorpseLootDryRunPlan.Empty;
        }

        if (corpse.Item is not InventoryEquipment corpseEquipment
            || botOwner.GetPlayer?.InventoryController == null
            || botOwner.GetPlayer.Inventory?.Equipment == null)
        {
            return VanguardCorpseLootDryRunPlan.Empty;
        }

        List<SourceItem> items = CollectCorpseItems(corpseEquipment);
        WeaponChoice? longChoice = permissions.FillEmptyLongWeaponSlot && need.EmptyLongWeaponSlot.HasValue
            ? SelectWeaponChoice(botOwner, need.EmptyLongWeaponSlot, items, source => IsLongWeaponSource(source.Path), excludedWeaponId: null)
            : null;
        WeaponChoice? holsterChoice = permissions.FillEmptyHolsterSlot && need.HolsterSlotEmpty
            ? SelectWeaponChoice(botOwner, EquipmentSlot.Holster, items, source => IsHolsterWeaponSource(source.Path), longChoice?.Weapon.Id)
            : null;

        var compatibleWeapons = new List<EftWeapon>(currentWeapons);
        if (longChoice != null) compatibleWeapons.Add(longChoice.Weapon);
        if (holsterChoice != null) compatibleWeapons.Add(holsterChoice.Weapon);

        var candidates = new List<VanguardCorpseLootItemPlanEntry>();
        var pendingAutoPlace = new List<PendingAutoPlace>();
        if (permissions.FillEmptyLongWeaponSlot && longChoice != null)
        {
            candidates.Add(BuildWeaponEntry(longChoice, "empty_long_weapon_slot", "slot_filled_or_window_completed"));
        }
        if (permissions.FillEmptyHolsterSlot && holsterChoice != null)
        {
            candidates.Add(BuildWeaponEntry(holsterChoice, "empty_holster_weapon", "holster_filled_or_window_completed"));
        }

        if (permissions.LootMedicalItems)
        {
            foreach (SourceItem source in items.Where(value => value.Item is MedsItemClass))
            {
                var meds = (MedsItemClass)source.Item;
                if (!VanguardOperatorLootNeedReader.IsUsableMedicalItem(meds))
                {
                    continue;
                }

                string? missingCapability = ResolveMissingMedicalCapability(meds.StringTemplateId, need);
                float score = missingCapability != null ? 120f : 60f;
                pendingAutoPlace.Add(new PendingAutoPlace(
                    source,
                    "medical",
                    missingCapability != null ? "missing_medical_capability_" + missingCapability : "medical_resupply",
                    score,
                    "medical_capability_satisfied_or_medical_cap_reached"));
            }
        }

        if (permissions.LootCompatibleMagazines)
        {
            foreach (SourceItem source in items.Where(value => value.Item is MagazineItemClass))
            {
                var magazine = (MagazineItemClass)source.Item;
                if (IsContainedByPlannedWeapon(source.Path, longChoice, holsterChoice)
                    || magazine.Count <= 0
                    || !VanguardOperatorLootNeedReader.FitsAnyWeapon(magazine, compatibleWeapons))
                {
                    continue;
                }

                bool supportsPlannedWeapon = (longChoice != null && FitsWeapon(magazine, longChoice.Weapon))
                    || (holsterChoice != null && FitsWeapon(magazine, holsterChoice.Weapon));
                string reason = need.NeedsCompatibleMagazine
                    ? "compatible_magazine_critical"
                    : supportsPlannedWeapon
                        ? "planned_weapon_magazine"
                        : "compatible_magazine_reserve";
                float score = need.NeedsCompatibleMagazine ? 110f : supportsPlannedWeapon ? 88f : 50f;
                score += Math.Min(50, magazine.Count) * 0.15f;
                pendingAutoPlace.Add(new PendingAutoPlace(
                    source,
                    "magazine",
                    reason,
                    score,
                    "desired_compatible_magazine_reserve_reached"));
            }
        }

        if (permissions.LootCompatibleLooseAmmunition)
        {
            foreach (SourceItem source in items.Where(value => value.Item is AmmoItemClass))
            {
                var ammunition = (AmmoItemClass)source.Item;
                if (!IsLooseAmmunitionSource(source.Path)
                    || IsContainedByPlannedWeapon(source.Path, longChoice, holsterChoice)
                    || ammunition.StackObjectsCount <= 0
                    || !VanguardOperatorLootNeedReader.FitsAnyWeapon(ammunition, compatibleWeapons))
                {
                    continue;
                }

                bool supportsPlannedWeapon = (longChoice != null && FitsWeapon(ammunition, longChoice.Weapon))
                    || (holsterChoice != null && FitsWeapon(ammunition, holsterChoice.Weapon));
                string reason = need.NeedsCompatibleAmmunition
                    ? "compatible_loose_ammunition_critical"
                    : supportsPlannedWeapon
                        ? "planned_weapon_loose_ammunition"
                        : "compatible_loose_ammunition_reserve";
                float score = need.NeedsCompatibleAmmunition ? 104f : supportsPlannedWeapon ? 92f : 64f;
                score += Math.Min(60, ammunition.StackObjectsCount) * 0.08f;
                pendingAutoPlace.Add(new PendingAutoPlace(
                    source,
                    "loose_ammunition",
                    reason,
                    score,
                    "loose_ammunition_session_cap_reached"));
            }
        }

        if (permissions.LootGrenades && need.NeedsGrenade)
        {
            foreach (SourceItem source in items.Where(value => value.Item is ThrowWeapItemClass))
            {
                pendingAutoPlace.Add(new PendingAutoPlace(
                    source,
                    "grenade",
                    "grenade_resupply",
                    68f,
                    "desired_grenade_reserve_reached"));
            }
        }

        int rawUsefulItemCount = candidates.Count + pendingAutoPlace.Count;
        IReadOnlyList<PendingAutoPlace> placementPreviews = SelectPlacementPreviewCandidates(pendingAutoPlace);
        foreach (PendingAutoPlace pending in placementPreviews)
        {
            candidates.Add(BuildAutoPlaceEntry(
                botOwner,
                pending.Source,
                pending.Category,
                pending.Reason,
                pending.Score,
                pending.StopCondition));
        }

        int previewBudgetTruncated = Math.Max(0, pendingAutoPlace.Count - placementPreviews.Count);
        List<VanguardCorpseLootItemPlanEntry> selected = SelectBoundedPlan(candidates, need);
        int feasible = selected.Count(entry => entry.PlacementPossible);
        int blocked = selected.Count - feasible;
        string highest = selected
            .Where(entry => entry.PlacementPossible)
            .OrderByDescending(entry => entry.Score)
            .Select(entry => entry.Reason)
            .FirstOrDefault() ?? "none";
        string stop = selected.Count >= MaximumPlanEntries
            ? "maximum_plan_entries_reached"
            : feasible == 0 && selected.Count > 0
                ? "no_feasible_destination"
                : "needs_satisfied_or_corpse_plan_exhausted";

        return new VanguardCorpseLootDryRunPlan
        {
            Entries = selected,
            UsefulItemCount = rawUsefulItemCount,
            FeasibleItemCount = feasible,
            NoDestinationCount = blocked,
            PlannedMedicalCount = selected.Count(entry => entry.PlacementPossible && entry.Category == "medical"),
            PlannedMagazineCount = selected.Count(entry => entry.PlacementPossible && entry.Category == "magazine"),
            PlannedLooseAmmunitionStackCount = selected.Count(entry => entry.PlacementPossible && entry.Category == "loose_ammunition"),
            PlannedLooseAmmunitionRoundCount = selected.Where(entry => entry.PlacementPossible && entry.Category == "loose_ammunition").Sum(entry => Math.Max(0, entry.Quantity)),
            PlannedGrenadeCount = selected.Count(entry => entry.PlacementPossible && entry.Category == "grenade"),
            PlannedLongWeaponCount = selected.Count(entry => entry.PlacementPossible && entry.Category == "long_weapon"),
            PlannedHolsterWeaponCount = selected.Count(entry => entry.PlacementPossible && entry.Category == "holster_weapon"),
            EstimatedCellCount = selected.Where(entry => entry.PlacementPossible).Sum(entry => entry.CellCount),
            PlacementPreviewCount = placementPreviews.Count,
            PlacementPreviewBudgetTruncatedCount = previewBudgetTruncated,
            EstimatedWeightKg = selected.Where(entry => entry.PlacementPossible).Sum(entry => entry.EstimatedWeightKg),
            TotalScore = selected.Where(entry => entry.PlacementPossible).Sum(entry => entry.Score),
            HighestPriorityReason = highest,
            StopCondition = stop
        };
    }

    public static VanguardCorpseLootInventorySummary Summarize(VanguardCorpseLootDryRunPlan plan)
    {
        int missingMedical = plan.Entries.Count(entry => entry.Category == "medical" && entry.Reason.StartsWith("missing_medical_capability_", StringComparison.Ordinal));
        int magazineAmmo = plan.Entries
            .Where(entry => entry.Category == "magazine")
            .Sum(entry => Math.Max(0, entry.Quantity));
        return new VanguardCorpseLootInventorySummary
        {
            MedicalItemCount = plan.Entries.Count(entry => entry.Category == "medical"),
            MissingCapabilityMedicalCount = missingMedical,
            CompatibleMagazineCount = plan.Entries.Count(entry => entry.Category == "magazine"),
            CompatibleMagazineAmmoCount = magazineAmmo,
            CompatibleLooseAmmunitionStackCount = plan.Entries.Count(entry => entry.Category == "loose_ammunition"),
            CompatibleLooseAmmunitionRoundCount = plan.Entries.Where(entry => entry.Category == "loose_ammunition").Sum(entry => Math.Max(0, entry.Quantity)),
            GrenadeCount = plan.Entries.Count(entry => entry.Category == "grenade"),
            UsableEmptyLongWeaponSlotCount = plan.Entries.Count(entry => entry.Category == "long_weapon"),
            UsableHolsterWeaponCount = plan.Entries.Count(entry => entry.Category == "holster_weapon"),
            TotalUsefulItemCount = plan.UsefulItemCount,
            FeasibleItemCount = plan.FeasibleItemCount,
            NoDestinationCount = plan.NoDestinationCount,
            HighestPriorityReason = plan.HighestPriorityReason
        };
    }

    private static IReadOnlyList<PendingAutoPlace> SelectPlacementPreviewCandidates(IEnumerable<PendingAutoPlace> pending)
    {
        int medical = 0;
        int magazines = 0;
        int looseAmmunition = 0;
        int grenades = 0;
        var missingMedicalCapabilities = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<PendingAutoPlace>(MaximumPlacementPreviews);

        foreach (PendingAutoPlace candidate in pending
                     .OrderByDescending(value => value.Score)
                     .ThenBy(value => value.Source.Item.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (selected.Count >= MaximumPlacementPreviews) break;
            if (candidate.Category == "medical")
            {
                if (medical >= MaximumMedicalEntries + 1) continue;
                if (candidate.Reason.StartsWith("missing_medical_capability_", StringComparison.Ordinal)
                    && !missingMedicalCapabilities.Add(candidate.Reason)) continue;
                medical++;
            }
            else if (candidate.Category == "magazine")
            {
                if (magazines >= DesiredMagazineCount + 2) continue;
                magazines++;
            }
            else if (candidate.Category == "loose_ammunition")
            {
                if (looseAmmunition >= 4) continue;
                looseAmmunition++;
            }
            else if (candidate.Category == "grenade")
            {
                if (grenades >= DesiredGrenadeCount) continue;
                grenades++;
            }

            selected.Add(candidate);
        }

        return selected;
    }

    private static List<VanguardCorpseLootItemPlanEntry> SelectBoundedPlan(
        IEnumerable<VanguardCorpseLootItemPlanEntry> candidates,
        VanguardOperatorLootNeedSnapshot need)
    {
        int medical = 0;
        int magazines = 0;
        int looseAmmunition = 0;
        int grenades = 0;
        int magazineCap = Math.Max(0, DesiredMagazineCount - need.CompatibleMagazineCount) + 2;
        int grenadeCap = Math.Max(1, DesiredGrenadeCount - need.GrenadeCount);
        var selected = new List<VanguardCorpseLootItemPlanEntry>();
        var selectedMissingMedicalCapabilities = new HashSet<string>(StringComparer.Ordinal);

        foreach (VanguardCorpseLootItemPlanEntry entry in candidates
                     .OrderByDescending(value => value.PlacementPossible)
                     .ThenByDescending(value => value.Score)
                     .ThenBy(value => value.ItemId, StringComparer.OrdinalIgnoreCase))
        {
            if (selected.Count >= MaximumPlanEntries)
            {
                break;
            }

            if (entry.Category == "medical" && medical >= MaximumMedicalEntries) continue;
            if (entry.Category == "medical"
                && entry.Reason.StartsWith("missing_medical_capability_", StringComparison.Ordinal)
                && !selectedMissingMedicalCapabilities.Add(entry.Reason)) continue;
            if (entry.Category == "magazine" && magazines >= Math.Max(1, magazineCap)) continue;
            if (entry.Category == "loose_ammunition" && looseAmmunition >= 4) continue;
            if (entry.Category == "grenade" && grenades >= grenadeCap) continue;
            if (entry.Category == "long_weapon" && selected.Any(value => value.Category == "long_weapon")) continue;
            if (entry.Category == "holster_weapon" && selected.Any(value => value.Category == "holster_weapon")) continue;

            selected.Add(entry);
            if (entry.Category == "medical") medical++;
            if (entry.Category == "magazine") magazines++;
            if (entry.Category == "loose_ammunition") looseAmmunition++;
            if (entry.Category == "grenade") grenades++;
        }

        return selected;
    }

    private static WeaponChoice? SelectWeaponChoice(
        BotOwner botOwner,
        EquipmentSlot? targetSlot,
        IReadOnlyList<SourceItem> items,
        Func<SourceItem, bool> sourcePolicy,
        string? excludedWeaponId)
    {
        if (!targetSlot.HasValue)
        {
            return null;
        }

        WeaponChoice? best = null;
        foreach (SourceItem source in items.Where(value => value.Item is EftWeapon && sourcePolicy(value)))
        {
            var weapon = (EftWeapon)source.Item;
            if ((targetSlot.Value == EquipmentSlot.Holster && weapon is not PistolItemClass)
                || (targetSlot.Value != EquipmentSlot.Holster && weapon is PistolItemClass))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(excludedWeaponId)
                && string.Equals(weapon.Id, excludedWeaponId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!CanUseInEmptySlot(botOwner, targetSlot.Value, weapon, source.Path, items))
            {
                continue;
            }

            float durability = weapon.Repairable?.MaxDurability > 0f
                ? weapon.Repairable.Durability / weapon.Repairable.MaxDurability
                : 0f;
            int loaded = Math.Max(0, weapon.GetCurrentMagazineCount() + weapon.ChamberAmmoCount);
            int reserve = items
                .Where(value => value.Item is MagazineItemClass
                    && !value.Path.StartsWith(source.Path + "/", StringComparison.Ordinal))
                .Select(value => (MagazineItemClass)value.Item)
                .Where(magazine => magazine.Count > 0 && FitsWeapon(magazine, weapon))
                .Sum(magazine => Math.Min(50, magazine.Count));
            float score = 72f + durability * 12f + Math.Min(60, loaded + reserve) * 0.15f;
            var choice = new WeaponChoice(weapon, targetSlot.Value, source.Path, score);
            if (best == null || choice.Score > best.Score)
            {
                best = choice;
            }
        }

        return best;
    }

    private static VanguardCorpseLootItemPlanEntry BuildWeaponEntry(WeaponChoice choice, string reason, string stop)
        => new()
        {
            ItemId = Safe(choice.Weapon.Id),
            TemplateId = Safe(choice.Weapon.StringTemplateId),
            Name = ItemName(choice.Weapon),
            Category = choice.Slot == EquipmentSlot.Holster ? "holster_weapon" : "long_weapon",
            Reason = reason,
            SourcePath = choice.Path,
            Destination = "equipment_slot:" + choice.Slot,
            PlacementOperation = "direct_slot_compatibility_preview",
            PlacementPossible = true,
            Quantity = 1,
            CellCount = Math.Max(1, choice.Weapon.Width * choice.Weapon.Height),
            EstimatedWeightKg = ReadWeight(choice.Weapon),
            Score = choice.Score,
            StopCondition = stop
        };

    private static VanguardCorpseLootItemPlanEntry BuildAutoPlaceEntry(
        BotOwner botOwner,
        SourceItem source,
        string category,
        string reason,
        float score,
        string stop)
    {
        bool possible = TryPreviewAutoPlace(botOwner, source.Item, out string destination, out string operation);
        int quantity = source.Item is MagazineItemClass magazine
            ? Math.Max(0, magazine.Count)
            : source.Item is AmmoItemClass ammunition
                ? Math.Max(0, ammunition.StackObjectsCount)
                : 1;
        return new VanguardCorpseLootItemPlanEntry
        {
            ItemId = Safe(source.Item.Id),
            TemplateId = Safe(source.Item.StringTemplateId),
            Name = ItemName(source.Item),
            Category = category,
            Reason = reason,
            SourcePath = source.Path,
            Destination = destination,
            PlacementOperation = operation,
            PlacementPossible = possible,
            Quantity = quantity,
            CellCount = Math.Max(1, source.Item.Width * source.Item.Height),
            EstimatedWeightKg = ReadWeight(source.Item),
            Score = possible ? score : score * 0.1f,
            StopCondition = stop
        };
    }

    private static bool TryPreviewAutoPlace(BotOwner botOwner, Item item, out string destination, out string operation)
    {
        destination = "none";
        operation = "quick_find_failed";
        try
        {
            var inventoryController = botOwner.GetPlayer?.InventoryController;
            InventoryEquipment? equipment = inventoryController?.Inventory?.Equipment;
            if (inventoryController == null || equipment == null)
            {
                operation = "inventory_controller_unavailable";
                return false;
            }

            var targets = new List<CompoundItem>(3);
            foreach (EquipmentSlot slotKind in new[] { EquipmentSlot.Pockets, EquipmentSlot.TacticalVest, EquipmentSlot.Backpack })
            {
                if (equipment.GetSlot(slotKind)?.ContainedItem is CompoundItem container)
                {
                    targets.Add(container);
                }
            }
            if (targets.Count == 0)
            {
                operation = "no_mvp_target_container";
                return false;
            }

            var preview = InteractionsHandlerClass.QuickFindAppropriatePlace(
                item,
                inventoryController,
                targets,
                InteractionsHandlerClass.EMoveItemOrder.PickUp,
                simulate: true);
            if (!preview.Succeeded)
            {
                operation = "quick_find_no_destination";
                return false;
            }

            object value = preview.Value;
            operation = value?.GetType().Name ?? "quick_find_operation";
            destination = DescribeOperationDestination(value);
            return true;
        }
        catch (Exception exception)
        {
            operation = "quick_find_exception:" + exception.GetType().Name;
            destination = "none";
            return false;
        }
    }

    private static string DescribeOperationDestination(object? operation)
    {
        if (operation == null)
        {
            return "inventory_auto_place";
        }

        try
        {
            foreach (string propertyName in new[] { "To", "Destination", "Address", "ItemAddress", "TargetAddress" })
            {
                PropertyInfo? property = operation.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object? value = property?.GetValue(operation);
                if (value != null)
                {
                    return "inventory_auto_place:" + Safe(value.ToString());
                }
            }
        }
        catch
        {
        }

        return "inventory_auto_place:" + Safe(operation.GetType().Name);
    }

    private static bool CanUseInEmptySlot(
        BotOwner botOwner,
        EquipmentSlot targetSlot,
        EftWeapon weapon,
        string weaponPath,
        IReadOnlyList<SourceItem> corpseItems)
    {
        try
        {
            var slot = botOwner.GetPlayer?.Inventory?.Equipment?.GetSlot(targetSlot);
            if (slot == null || slot.ContainedItem != null || !slot.CheckCompatibility(weapon))
            {
                return false;
            }

            if (weapon.Repairable == null
                || weapon.Repairable.MaxDurability <= 0f
                || weapon.Repairable.Durability / weapon.Repairable.MaxDurability < 0.20f)
            {
                return false;
            }

            if (weapon.GetCurrentMagazineCount() + weapon.ChamberAmmoCount > 0)
            {
                return true;
            }

            return corpseItems.Any(source => source.Item is MagazineItemClass magazine
                    && !source.Path.StartsWith(weaponPath + "/", StringComparison.Ordinal)
                    && magazine.Count > 0
                    && FitsWeapon(magazine, weapon))
                || corpseItems.Any(source => source.Item is AmmoItemClass ammunition
                    && IsLooseAmmunitionSource(source.Path)
                    && ammunition.StackObjectsCount > 0
                    && FitsWeapon(ammunition, weapon));
        }
        catch
        {
            return false;
        }
    }

    private static bool FitsWeapon(MagazineItemClass magazine, EftWeapon weapon)
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

    private static bool FitsWeapon(AmmoItemClass ammunition, EftWeapon weapon)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(ammunition.Caliber)
                && string.Equals(ammunition.Caliber, weapon.AmmoCaliber, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLooseAmmunitionSource(string path)
        => path.Contains("/grid:", StringComparison.Ordinal)
            && !path.StartsWith(EquipmentSlot.FirstPrimaryWeapon + "/", StringComparison.Ordinal)
            && !path.StartsWith(EquipmentSlot.SecondPrimaryWeapon + "/", StringComparison.Ordinal)
            && !path.StartsWith(EquipmentSlot.Holster + "/", StringComparison.Ordinal);

    private static string? ResolveMissingMedicalCapability(string? templateId, VanguardOperatorLootNeedSnapshot need)
    {
        string normalized = VanguardMedicalItemCapabilityResolver.NormalizeTemplateId(templateId);
        foreach (VanguardMedicalItemCapability capability in VanguardMedicalItemCapabilityResolver.Catalog)
        {
            if (!string.Equals(capability.TemplateId, normalized, StringComparison.OrdinalIgnoreCase)) continue;
            if (capability.Need == VanguardMedicalNeed.HeavyBleed && !need.HasHeavyBleedTreatment) return "heavy_bleed";
            if (capability.Need == VanguardMedicalNeed.LightBleed && !need.HasLightBleedTreatment) return "light_bleed";
            if (capability.Need == VanguardMedicalNeed.Fracture && !need.HasFractureTreatment) return "fracture";
            if (capability.Need == VanguardMedicalNeed.HpHeal && !need.HasHpTreatment) return "hp_heal";
            if (capability.Need == VanguardMedicalNeed.PainMobility && !need.HasPainMobilityTreatment) return "pain_mobility";
            if (capability.Need == VanguardMedicalNeed.SurgeryDestroyedPart && !need.HasSurgeryTreatment) return "surgery";
        }
        return null;
    }

    private static bool IsLongWeaponSource(string path)
        => path.StartsWith(EquipmentSlot.FirstPrimaryWeapon + "/", StringComparison.Ordinal)
            || path.StartsWith(EquipmentSlot.SecondPrimaryWeapon + "/", StringComparison.Ordinal);

    private static bool IsHolsterWeaponSource(string path)
        => path.StartsWith(EquipmentSlot.Holster + "/", StringComparison.Ordinal);

    private static bool IsContainedByPlannedWeapon(string path, WeaponChoice? longChoice, WeaponChoice? holsterChoice)
        => (longChoice != null && path.StartsWith(longChoice.Path + "/", StringComparison.Ordinal))
            || (holsterChoice != null && path.StartsWith(holsterChoice.Path + "/", StringComparison.Ordinal));

    private static List<SourceItem> CollectCorpseItems(InventoryEquipment equipment)
    {
        var result = new List<SourceItem>();
        foreach (EquipmentSlot slotKind in new[]
                 {
                     EquipmentSlot.Pockets,
                     EquipmentSlot.TacticalVest,
                     EquipmentSlot.Backpack,
                     EquipmentSlot.FirstPrimaryWeapon,
                     EquipmentSlot.SecondPrimaryWeapon,
                     EquipmentSlot.Holster
                 })
        {
            Item? root = equipment.GetSlot(slotKind)?.ContainedItem;
            if (root != null)
            {
                CollectRecursive(root, slotKind.ToString(), result);
            }
        }
        return result;
    }

    private static void CollectRecursive(Item item, string path, ICollection<SourceItem> destination)
    {
        destination.Add(new SourceItem(item, path + "/" + Safe(item.Id)));
        if (item is not CompoundItem compound) return;
        if (compound.Slots != null)
        {
            foreach (var slot in compound.Slots)
            {
                if (slot?.ContainedItem != null) CollectRecursive(slot.ContainedItem, path + "/slot:" + Safe(slot.ID), destination);
            }
        }
        if (compound.Grids != null)
        {
            foreach (var grid in compound.Grids)
            {
                if (grid?.Items == null) continue;
                foreach (Item child in grid.Items)
                {
                    if (child != null) CollectRecursive(child, path + "/grid:" + Safe(grid.GetType().Name), destination);
                }
            }
        }
    }

    private static string ItemName(Item item)
        => Safe(item.Name ?? item.ShortName ?? item.StringTemplateId);

    private static float ReadWeight(Item item)
    {
        foreach (string name in new[] { "TotalWeight", "Weight" })
        {
            try
            {
                PropertyInfo? property = item.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object? value = property?.GetValue(item);
                if (value != null)
                {
                    return Math.Max(0f, Convert.ToSingle(value, CultureInfo.InvariantCulture));
                }
            }
            catch
            {
            }
        }
        return 0f;
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
