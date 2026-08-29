#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EFT.InventoryLogic;

// Responsibility: Reads and normalizes live evidence for Special Equipment Slot Reader in the Operator runtime.
// Flow: Live EFT/Fika/Vanguard objects are inspected defensively, normalized into a bounded snapshot, then handed to policy/decision code.
// Authority boundary: Read-only observer; it does not create missing truth or mutate the game state it inspects.
// Invariant: Missing/stale evidence degrades explicitly and reader failures must not silently fabricate an actionable state.
namespace Vanguard.Client.Runtime.Inventory;

/// <summary>
/// Reads direct items held by EFT special equipment slots without assuming that those dynamic
/// slot identifiers are members of <see cref="EquipmentSlot"/>. In SPT 4 / EFT 0.16 the slots
/// exist as root <see cref="Slot"/> instances whose IDs start with "SpecialSlot", while the
/// EquipmentSlot enum intentionally exposes only canonical equipment slots.
/// </summary>
internal static class VanguardSpecialEquipmentSlotReader
{
    public static void AppendDirectItems<TItem>(InventoryEquipment? equipment, IList<TItem> destination)
        where TItem : Item
    {
        if (equipment == null || destination == null)
        {
            return;
        }

        Slot[] slots = equipment.Slots;
        if (slots == null || slots.Length == 0)
        {
            return;
        }

        foreach (Slot slot in slots)
        {
            if (slot == null || !slot.IsSpecial || slot.ContainedItem is not TItem item)
            {
                continue;
            }

            if (ContainsSameItem(destination, item))
            {
                continue;
            }

            destination.Add(item);
        }
    }

    private static bool ContainsSameItem<TItem>(IList<TItem> destination, TItem item)
        where TItem : Item
    {
        for (int index = 0; index < destination.Count; index++)
        {
            TItem existing = destination[index];
            if (ReferenceEquals(existing, item))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(existing.Id)
                && !string.IsNullOrWhiteSpace(item.Id)
                && string.Equals(existing.Id, item.Id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
#endif
