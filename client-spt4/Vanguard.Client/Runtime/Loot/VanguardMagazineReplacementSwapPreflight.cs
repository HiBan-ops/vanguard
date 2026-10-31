#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using EftWeapon = global::EFT.InventoryLogic.Weapon;
using Vanguard.Client.Diagnostics;

// Responsibility: Promotes the already-qualified Magazine Discard shadow pair into one exact native atomic Swap prepared for the existing loot executor.
// Flow: exact assignment + fresh LOOT-Q1 audit -> live carried low magazine + assigned live replacement -> simulated native Swap -> executor submission/callback/readback.
// Authority boundary: This class prepares/revalidates only; it never submits a network transaction and never calls ThrowItem.
// Invariant: The low magazine is in ordinary carried storage, not installed/secured, both magazines fit the same current weapon, and no destructive operation is accepted.
namespace Vanguard.Client.Runtime.Loot;

internal static class VanguardMagazineReplacementSwapPreflight
{
    public const string StatusTag = "VANGUARD_MAGAZINE_DISCARD_MUTATION_STATUS";
    private static readonly EquipmentSlot[] AllowedStorageSlots =
    {
        EquipmentSlot.Pockets,
        EquipmentSlot.TacticalVest,
        EquipmentSlot.Backpack
    };

    internal static bool TryPrepare(
        VanguardLootTargetKind targetKind,
        string targetId,
        BotOwner botOwner,
        InventoryController inventory,
        VanguardSquadLootItemAssignment assignment,
        MagazineItemClass replacementMagazine,
        out VanguardMagazineReplacementSwapPlan plan,
        out bool applicable,
        out string reason)
    {
        plan = VanguardMagazineReplacementSwapPlan.Empty;
        applicable = false;
        reason = "no_exact_discard_pair";

        try
        {
            IReadOnlyList<EftWeapon> currentWeapons = VanguardOperatorLootNeedReader.CaptureCurrentWeapons(botOwner);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (EftWeapon weapon in currentWeapons)
            {
                if (weapon == null || string.IsNullOrWhiteSpace(weapon.Id)) continue;
                if (!VanguardMagazineLogisticsAuditService.TryGetLatest(assignment.AssignedBotProfileId, weapon.Id, now, out VanguardMagazineLogisticsAuditSnapshot audit)
                    || !audit.BetterLootMagazineCandidate
                    || audit.TargetKind != targetKind
                    || !string.Equals(audit.TargetId, targetId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(audit.BestLootMagazineItemId, assignment.ItemId, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(audit.DiscardCandidateItemId)
                    || string.Equals(audit.DiscardCandidateItemId, "none", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                applicable = true;
                if (!string.Equals(assignment.Category, "magazine", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "replacement_assignment_not_magazine";
                    return false;
                }

                Item? discardItem = inventory.FindItem<Item>(audit.DiscardCandidateItemId);
                if (discardItem is not MagazineItemClass discardMagazine)
                {
                    reason = "discard_magazine_live_item_missing";
                    return false;
                }

                if (discardMagazine.CurrentAddress == null || replacementMagazine.CurrentAddress == null)
                {
                    reason = "atomic_swap_address_missing";
                    return false;
                }

                string discardStorageSlot = ResolveAllowedStorageSlot(inventory, discardMagazine.Id);
                if (string.Equals(discardStorageSlot, "none", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "discard_not_in_allowed_carried_storage";
                    return false;
                }
                if (IsInstalledMagazine(currentWeapons, discardMagazine.Id))
                {
                    reason = "discard_is_current_weapon_magazine";
                    return false;
                }
                if (!IsStructurallyLow(discardMagazine))
                {
                    reason = "discard_no_longer_structurally_low";
                    return false;
                }
                if (replacementMagazine.Count <= discardMagazine.Count)
                {
                    reason = "replacement_no_longer_better";
                    return false;
                }
                if (!Fits(discardMagazine, weapon) || !Fits(replacementMagazine, weapon))
                {
                    reason = "magazine_weapon_compatibility_failed";
                    return false;
                }

                ItemAddress discardSourceAddress = discardMagazine.CurrentAddress;
                ItemAddress replacementSourceAddress = replacementMagazine.CurrentAddress;
                var swap = InteractionsHandlerClass.Swap(
                    replacementMagazine,
                    discardSourceAddress,
                    discardMagazine,
                    replacementSourceAddress,
                    inventory,
                    true);
                if (!swap.Succeeded || swap.Value == null)
                {
                    reason = "atomic_replacement_swap_build_failed";
                    return false;
                }
                if (ReadBoolean(swap.Value, "ItemsDestroyRequired"))
                {
                    reason = "atomic_replacement_swap_destroy_required";
                    return false;
                }
                if (!inventory.CanExecute(swap.Value))
                {
                    reason = "atomic_replacement_swap_can_execute_false";
                    return false;
                }

                int discardRounds = Math.Max(0, discardMagazine.Count);
                int replacementRounds = Math.Max(0, replacementMagazine.Count);
                plan = new VanguardMagazineReplacementSwapPlan
                {
                    Ready = true,
                    WeaponItemId = weapon.Id,
                    DiscardMagazine = discardMagazine,
                    DiscardSourceAddress = discardSourceAddress,
                    ReplacementSourceAddress = replacementSourceAddress,
                    Operation = swap.Value,
                    DiscardStorageSlot = discardStorageSlot,
                    DiscardRounds = discardRounds,
                    ReplacementRounds = replacementRounds
                };
                reason = "atomic_replacement_swap_ready";
                VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
                    $"VANGUARD_MAGAZINE_DISCARD_MUTATION_PREPARE state=ready; targetKind={TargetKindToken(targetKind)}; target={Safe(targetId)}; bot={Safe(assignment.AssignedBotProfileId)}; weapon={Safe(weapon.Id)}; discard={Safe(discardMagazine.Id)}; discardRounds={discardRounds}; discardStorage={Safe(discardStorageSlot)}; replacement={Safe(replacementMagazine.Id)}; replacementRounds={replacementRounds}; preparedNativeSwap=true; canExecute=true; destroyRequired=false; networkTransactionSubmitted=false; noThrowItemFirst=true; parallelWriter=false");
                return true;
            }
        }
        catch (Exception exception)
        {
            reason = "magazine_atomic_replacement_preflight_exception:" + exception.GetType().Name;
            return false;
        }

        return false;
    }

    internal static bool Revalidate(VanguardCorpseLootPreparedTransaction prepared, BotOwner botOwner, out string reason)
    {
        reason = "none";
        if (!prepared.Preflight.MagazineReplacementSwap) return true;

        try
        {
            if (prepared.Item is not MagazineItemClass replacementMagazine
                || prepared.DisplacedItem is not MagazineItemClass discardMagazine
                || prepared.DisplacedSourceAddress == null
                || replacementMagazine.CurrentAddress == null
                || discardMagazine.CurrentAddress == null)
            {
                reason = "magazine_atomic_replacement_items_or_addresses_missing";
                return false;
            }

            string replacementCurrent = VanguardCorpseLootLiveItemResolver.Fingerprint(replacementMagazine.CurrentAddress);
            string replacementExpected = VanguardCorpseLootLiveItemResolver.Fingerprint(prepared.SourceAddress);
            string discardCurrent = VanguardCorpseLootLiveItemResolver.Fingerprint(discardMagazine.CurrentAddress);
            string discardExpected = VanguardCorpseLootLiveItemResolver.Fingerprint(prepared.DisplacedSourceAddress);
            if (!string.Equals(replacementCurrent, replacementExpected, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(discardCurrent, discardExpected, StringComparison.OrdinalIgnoreCase))
            {
                reason = "magazine_atomic_replacement_source_address_changed";
                return false;
            }

            IReadOnlyList<EftWeapon> currentWeapons = VanguardOperatorLootNeedReader.CaptureCurrentWeapons(botOwner);
            EftWeapon? weapon = currentWeapons.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, prepared.Preflight.MagazineReplacementWeaponId, StringComparison.OrdinalIgnoreCase));
            if (weapon == null)
            {
                reason = "magazine_atomic_replacement_weapon_context_changed";
                return false;
            }

            string storageSlot = ResolveAllowedStorageSlot(prepared.Inventory, discardMagazine.Id);
            if (string.Equals(storageSlot, "none", StringComparison.OrdinalIgnoreCase)
                || IsInstalledMagazine(currentWeapons, discardMagazine.Id)
                || !IsStructurallyLow(discardMagazine)
                || replacementMagazine.Count <= discardMagazine.Count
                || !Fits(discardMagazine, weapon)
                || !Fits(replacementMagazine, weapon))
            {
                reason = "magazine_atomic_replacement_live_invariants_changed";
                return false;
            }

            if (ReadBoolean(prepared.Operation, "ItemsDestroyRequired") || !prepared.Inventory.CanExecute(prepared.Operation))
            {
                reason = "magazine_atomic_replacement_operation_not_executable";
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            reason = "magazine_atomic_replacement_revalidation_exception:" + exception.GetType().Name;
            return false;
        }
    }

    private static string ResolveAllowedStorageSlot(InventoryController inventory, string itemId)
    {
        var scratch = new List<Item>();
        foreach (EquipmentSlot slot in AllowedStorageSlots)
        {
            scratch.Clear();
            try { inventory.GetAcceptableItemsNonAlloc(new[] { slot }, scratch); }
            catch { continue; }
            if (scratch.Any(item => item != null && string.Equals(item.Id, itemId, StringComparison.OrdinalIgnoreCase))) return slot.ToString();
        }
        return "none";
    }

    private static bool IsInstalledMagazine(IReadOnlyList<EftWeapon> currentWeapons, string itemId)
    {
        foreach (EftWeapon weapon in currentWeapons)
        {
            try
            {
                MagazineItemClass? magazine = weapon.GetCurrentMagazine();
                if (magazine != null && string.Equals(magazine.Id, itemId, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
        }
        return false;
    }

    private static bool IsStructurallyLow(MagazineItemClass magazine) =>
        magazine.Count <= 0 || (magazine.MaxCount > 0 && magazine.Count * 4 <= magazine.MaxCount);

    private static bool Fits(MagazineItemClass magazine, EftWeapon weapon)
    {
        try { return weapon.GetMagazineSlot()?.CheckCompatibility(magazine) == true; }
        catch { return false; }
    }

    private static bool ReadBoolean(object target, string propertyName)
    {
        try
        {
            Type type = target.GetType();
            PropertyInfo? property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.PropertyType == typeof(bool) && property.GetValue(target) is bool propertyValue) return propertyValue;
            FieldInfo? field = type.GetField(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.FieldType == typeof(bool) && field.GetValue(target) is bool fieldValue && fieldValue;
        }
        catch { return false; }
    }

    private static string TargetKindToken(VanguardLootTargetKind targetKind) =>
        targetKind == VanguardLootTargetKind.WorldContainer ? "world_container" : "corpse";

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

internal sealed class VanguardMagazineReplacementSwapPlan
{
    public static VanguardMagazineReplacementSwapPlan Empty { get; } = new();
    public bool Ready { get; init; }
    public string WeaponItemId { get; init; } = "none";
    public MagazineItemClass DiscardMagazine { get; init; } = null!;
    public ItemAddress DiscardSourceAddress { get; init; } = null!;
    public ItemAddress ReplacementSourceAddress { get; init; } = null!;
    public IRaiseEvents Operation { get; init; } = null!;
    public string DiscardStorageSlot { get; init; } = "none";
    public int DiscardRounds { get; init; }
    public int ReplacementRounds { get; init; }
}
#endif
