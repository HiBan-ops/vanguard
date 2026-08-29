#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using EFT.Interactive;
using EFT.InventoryLogic;

// Responsibility: Provides Corpse Loot Live Item Resolver support for the loot runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
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
