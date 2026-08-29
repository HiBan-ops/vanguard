#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EftWeapon = global::EFT.InventoryLogic.Weapon;

// Responsibility: Computes an executable plan for Utility Loot Activation Planner in the loot runtime without performing the final action itself.
// Flow: Current snapshots and doctrine are reduced to a candidate plan; the owning scheduler/executor rechecks authority before any mutation.
// Authority boundary: Planning is non-authoritative for physical execution and cannot bypass final combat, medical, loot, or movement safety checks.
// Invariant: Plans stay raid-scoped, deterministic from their inputs, and safe to discard when newer evidence supersedes them.
namespace Vanguard.Client.Runtime.Loot;

/// <summary>
/// Converts the validated loot read model into a bounded execution preview/plan without changing
/// tactical admission. The persistence path converges player-interest execution. The persistence path adds a target-agnostic Fika
/// network-safety preview gate so a known-unsafe multi-count stack cannot trigger a loot approach
/// while peer descriptor replay remains unacknowledged. Existing utility scoring is unchanged.
/// </summary>
internal static class VanguardUtilityLootActivationPolicy
{
    public static bool IsExecutable(
        VanguardLootItemUtility? utility,
        VanguardOperatorLootPermissionSnapshot permissions,
        VanguardLootTargetKind targetKind = VanguardLootTargetKind.Corpse)
    {
        if (utility == null || utility.Tier <= VanguardLootUtilityTier.Low
            || !VanguardOperatorLootTargetPermissionPolicy.AllowsTarget(permissions, targetKind, out _))
        {
            return false;
        }

        return utility.Category switch
        {
            "medical" => permissions.LootMedicalItems
                && utility.Tier >= VanguardLootUtilityTier.Reserve
                && IsValidMedicalReason(utility.Reason),
            "magazine" => permissions.LootCompatibleMagazines
                && utility.Tier >= VanguardLootUtilityTier.Reserve
                && utility.Reason.StartsWith("compatible_magazine_", StringComparison.Ordinal),
            "loose_ammunition" => permissions.LootCompatibleLooseAmmunition
                && utility.Tier >= VanguardLootUtilityTier.Reserve
                && utility.Reason.StartsWith("compatible_ammunition_", StringComparison.Ordinal),
            "grenade" => permissions.LootGrenades && utility.Tier >= VanguardLootUtilityTier.Reserve,
            "long_weapon" => permissions.FillEmptyLongWeaponSlot
                && utility.Tier >= VanguardLootUtilityTier.Combat
                && IsValidLongWeaponReason(utility.Reason),
            "holster_weapon" => permissions.FillEmptyHolsterSlot
                && utility.Tier >= VanguardLootUtilityTier.Reserve
                && IsValidHolsterReason(utility.Reason),
            "generic" => utility.Tier == VanguardLootUtilityTier.PlayerInterest
                || utility.Tier == VanguardLootUtilityTier.Opportunistic,
            "weapon_mod" => utility.Tier == VanguardLootUtilityTier.PlayerInterest
                || utility.Tier == VanguardLootUtilityTier.Opportunistic,
            _ => false
        };
    }

    public static float ExecutionScore(VanguardLootItemUtility utility)
        => ((int)utility.Tier * 1000f) + Math.Max(0f, utility.Score);

    private static bool IsValidLongWeaponReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        return reason.StartsWith("empty_long_weapon_slot_acquisition", StringComparison.Ordinal)
            || reason.StartsWith("secondary_weapon_upgrade_gain=", StringComparison.Ordinal);
    }

    private static bool IsValidHolsterReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        return string.Equals(reason, "empty_holster_weapon", StringComparison.Ordinal)
            || reason.StartsWith("empty_holster_weapon+eft_native_wishlist_", StringComparison.Ordinal);
    }

    private static bool IsValidMedicalReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        return reason.StartsWith("active_medical_need_", StringComparison.Ordinal)
            || reason.StartsWith("medical_reserve", StringComparison.Ordinal)
            || reason.StartsWith("missing_medical_reserve_", StringComparison.Ordinal);
    }
}

internal static class VanguardUtilityLootActivationPlanner
{
    private const int MaximumPlanEntries = 8;

    public static VanguardCorpseLootDryRunPlan Build(
        Corpse corpse,
        BotOwner botOwner,
        VanguardUnifiedLootReadModelObservation observation,
        VanguardOperatorLootPermissionSnapshot permissions)
    {
        if (corpse == null || botOwner == null || observation == null
            || !VanguardOperatorLootTargetPermissionPolicy.AllowsTarget(permissions, VanguardLootTargetKind.Corpse, out _))
        {
            return VanguardCorpseLootDryRunPlan.Empty;
        }

        var entries = new List<VanguardCorpseLootItemPlanEntry>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (VanguardLootItemUtility utility in observation.Utilities
                     .Where(value => VanguardUtilityLootActivationPolicy.IsExecutable(value, permissions))
                     .Where(value => VanguardSquadLootAssignmentService.IsAssignedToBot(
                         observation.OwnerProfileId, observation.CorpseId, value.ItemId, observation.BotProfileId, observation.ManifestRevision, now, out _))
                     .OrderByDescending(value => value.Tier)
                     .ThenByDescending(value => value.Score)
                     .ThenBy(value => value.ItemId, StringComparer.OrdinalIgnoreCase))
        {
            if (entries.Count >= MaximumPlanEntries)
            {
                break;
            }

            if (!VanguardCorpseLootLiveItemResolver.TryResolve(corpse, utility.ItemId, out Item item, out string sourcePath, out _))
            {
                continue;
            }

            bool possible = TryPreviewPlacement(botOwner, observation, utility, item, out string destination, out string operation, out string placementReason);
            int quantity = item is MagazineItemClass magazine
                ? Math.Max(0, magazine.Count)
                : item is AmmoItemClass ammunition
                    ? Math.Max(0, ammunition.StackObjectsCount)
                    : 1;
            entries.Add(new VanguardCorpseLootItemPlanEntry
            {
                ItemId = Safe(item.Id),
                TemplateId = Safe(item.StringTemplateId),
                Name = Safe(item.LocalizedName()),
                Category = utility.Category,
                Reason = utility.Reason,
                SourcePath = sourcePath,
                Destination = destination,
                PlacementOperation = operation,
                PlacementPossible = possible,
                Quantity = Math.Max(1, quantity),
                CellCount = Math.Max(1, item.Width * item.Height),
                EstimatedWeightKg = ReadWeight(item),
                Score = possible ? VanguardUtilityLootActivationPolicy.ExecutionScore(utility) : Math.Max(0f, utility.Score) * 0.1f,
                StopCondition = possible ? "utility_claim_or_context_change" : "placement_blocked:" + placementReason
            });
        }

        List<VanguardCorpseLootItemPlanEntry> selected = entries
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumPlanEntries)
            .ToList();
        int feasible = selected.Count(entry => entry.PlacementPossible);
        int blocked = selected.Count - feasible;
        string highest = selected.Where(entry => entry.PlacementPossible)
            .OrderByDescending(entry => entry.Score)
            .Select(entry => entry.Reason)
            .FirstOrDefault() ?? "none";

        return new VanguardCorpseLootDryRunPlan
        {
            Entries = selected,
            UsefulItemCount = selected.Count,
            FeasibleItemCount = feasible,
            NoDestinationCount = blocked,
            PlannedMedicalCount = selected.Count(entry => entry.Category == "medical"),
            PlannedMagazineCount = selected.Count(entry => entry.Category == "magazine"),
            PlannedLooseAmmunitionStackCount = selected.Count(entry => entry.Category == "loose_ammunition"),
            PlannedLooseAmmunitionRoundCount = selected.Where(entry => entry.Category == "loose_ammunition").Sum(entry => Math.Max(1, entry.Quantity)),
            PlannedGrenadeCount = selected.Count(entry => entry.Category == "grenade"),
            PlannedLongWeaponCount = selected.Count(entry => entry.Category == "long_weapon"),
            PlannedHolsterWeaponCount = selected.Count(entry => entry.Category == "holster_weapon"),
            EstimatedCellCount = selected.Sum(entry => Math.Max(1, entry.CellCount)),
            PlacementPreviewCount = selected.Count,
            PlacementPreviewBudgetTruncatedCount = Math.Max(0, entries.Count - MaximumPlanEntries),
            EstimatedWeightKg = selected.Sum(entry => Math.Max(0f, entry.EstimatedWeightKg)),
            TotalScore = selected.Where(entry => entry.PlacementPossible).Sum(entry => Math.Max(0f, entry.Score)),
            HighestPriorityReason = highest,
            StopCondition = selected.Count == 0
                ? "no_executable_unified_utility"
                : feasible == 0
                    ? "no_feasible_unified_destination"
                    : "utility_claim_context"
        };
    }

    private static bool TryPreviewPlacement(
        BotOwner botOwner,
        VanguardUnifiedLootReadModelObservation observation,
        VanguardLootItemUtility utility,
        Item item,
        out string destination,
        out string operation,
        out string reason)
    {
        destination = "none";
        operation = "none";
        reason = "none";
        InventoryController? inventory = botOwner.GetPlayer?.InventoryController;
        InventoryEquipment? equipment = botOwner.GetPlayer?.Inventory?.Equipment;
        if (inventory == null || equipment == null)
        {
            reason = "inventory_missing";
            return false;
        }

        try
        {
            if (!VanguardFikaStackableLootSafetyPolicy.IsSafe(item, out string stackableSafetyReason))
            {
                destination = "network_safety_blocked";
                reason = stackableSafetyReason;
                return false;
            }

            if (utility.Category == "holster_weapon")
            {
                if (item is not PistolItemClass pistol)
                {
                    reason = "holster_candidate_not_pistol";
                    return false;
                }
                Slot? holster = equipment.GetSlot(EquipmentSlot.Holster);
                if (holster == null || holster.ContainedItem != null || !holster.CheckCompatibility(pistol))
                {
                    reason = "holster_not_empty_or_rejects_candidate";
                    return false;
                }
                ItemAddress address = holster.CreateItemAddress();
                var move = InteractionsHandlerClass.Move(pistol, address, inventory, true);
                if (move.Failed || move.Value == null || move.Value.ItemsDestroyRequired)
                {
                    reason = "holster_move_preview_failed";
                    return false;
                }
                destination = "equipment_slot:" + EquipmentSlot.Holster;
                operation = move.Value.GetType().Name;
                return true;
            }

            if (utility.Category == "long_weapon")
            {
                if (item is not EftWeapon candidate
                    || !VanguardOperatorRaidLoadoutRegistry.TryGet(observation.BotProfileId, out VanguardOperatorRaidLoadoutSnapshot loadout))
                {
                    reason = "mutable_loadout_or_weapon_missing";
                    return false;
                }

                EquipmentSlot? preferredSlot = utility.LongWeaponDestinationSlot ?? observation.RaidMutableLongWeaponSlot;
                if (!VanguardOperatorRaidLoadoutRegistry.TryResolveRaidMutableLongWeaponSlot(
                    loadout, equipment, candidate, preferredSlot, out EquipmentSlot targetSlotKind, out Slot targetSlot))
                {
                    reason = "no_compatible_free_or_raid_mutable_long_weapon_slot";
                    return false;
                }

                if (targetSlot.ContainedItem == null)
                {
                    ItemAddress address = targetSlot.CreateItemAddress();
                    var move = InteractionsHandlerClass.Move(candidate, address, inventory, true);
                    if (move.Failed || move.Value == null || move.Value.ItemsDestroyRequired)
                    {
                        reason = "mutable_slot_move_preview_failed";
                        return false;
                    }
                    destination = "equipment_slot:" + targetSlotKind;
                    operation = move.Value.GetType().Name;
                    return true;
                }

                if (targetSlot.ContainedItem is not EftWeapon current
                    || VanguardOperatorRaidLoadoutRegistry.IsInitialProtectedWeapon(loadout, current)
                    || current.CurrentAddress == null
                    || candidate.CurrentAddress == null)
                {
                    reason = "mutable_slot_current_weapon_unavailable_or_initially_protected";
                    return false;
                }

                var swap = InteractionsHandlerClass.Swap(candidate, current.CurrentAddress, current, candidate.CurrentAddress, inventory, true);
                if (swap.Failed || swap.Value == null || ReadItemsDestroyRequired(swap.Value))
                {
                    reason = "mutable_slot_swap_preview_failed";
                    return false;
                }
                destination = "equipment_slot_swap:" + targetSlotKind;
                operation = swap.Value.GetType().Name;
                return true;
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
                reason = "no_target_container";
                return false;
            }

            var place = InteractionsHandlerClass.QuickFindAppropriatePlace(
                item,
                inventory,
                targets,
                InteractionsHandlerClass.EMoveItemOrder.PickUp,
                simulate: true);
            if (!place.Succeeded || place.Value == null)
            {
                reason = "quick_find_no_destination";
                return false;
            }
            destination = "inventory_auto_place";
            operation = place.Value.GetType().Name;
            return true;
        }
        catch (Exception exception)
        {
            reason = "placement_preview_exception:" + exception.GetType().Name;
            return false;
        }
    }

    private static bool ReadItemsDestroyRequired(object operation)
    {
        try
        {
            var property = operation.GetType().GetProperty("ItemsDestroyRequired");
            return property?.GetValue(operation) is bool value && value;
        }
        catch
        {
            return false;
        }
    }

    private static float ReadWeight(Item item)
    {
        try { return Math.Max(0f, item.TotalWeight); }
        catch { return 0f; }
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
