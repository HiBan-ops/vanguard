#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using EFT.Interactive;
using EFT.InventoryLogic;

// Responsibility: Resolve a selected corpse item by identity against the current live EFT inventory tree.
// Flow: Traverse corpse equipment with a bounded item budget and return the item with its source path and address.
// Authority boundary: This resolver only reads live inventory structure; inventory transfer or commitment belongs to the executor.
// Invariant: A missing identity or exhausted inspection budget fails resolution rather than substituting another item.
namespace Vanguard.Client.Runtime.Loot;

internal static class VanguardCorpseLootLiveItemResolver
{
    private const int MaximumInspectedItems = 192;

    public static bool TryResolve(Corpse corpse, string itemId, out Item item, out string sourcePath, out string sourceAddress)
    {
        item = null!;
        sourcePath = "none";
        sourceAddress = "none";
        if (corpse?.Item is not InventoryEquipment equipment || string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        try
        {
            int inspected = 0;
            foreach (Item candidate in equipment.GetAllItems().Where(value => value != null))
            {
                if (++inspected > MaximumInspectedItems)
                {
                    break;
                }
                if (!string.Equals(candidate.Id, itemId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                item = candidate;
                sourcePath = DescribeSourcePath(equipment, candidate);
                sourceAddress = Fingerprint(candidate.CurrentAddress);
                return candidate.CurrentAddress != null;
            }
        }
        catch
        {
            item = null!;
            sourcePath = "none";
            sourceAddress = "none";
        }

        return false;
    }

    public static string Fingerprint(object? value)
        => value == null ? "none" : Safe(value.ToString()) + ":" + Safe(value.GetType().Name);

    private static string DescribeSourcePath(InventoryEquipment equipment, Item item)
    {
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
            if (root == null)
            {
                continue;
            }
            if (string.Equals(root.Id, item.Id, StringComparison.OrdinalIgnoreCase)
                || root.GetAllItems().Any(child => child != null && string.Equals(child.Id, item.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return "equipment:" + slotKind;
            }
        }

        return "corpse_equipment";
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
