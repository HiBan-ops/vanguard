#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EFT.InventoryLogic;

// Responsibility: Provides World Loot Container Live Item Resolver support for the loot runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Loot;

/// <summary>Resolves an assigned live item beneath LootableContainer.ItemOwner.RootItem without owning loot policy.</summary>
internal static class VanguardWorldLootContainerLiveItemResolver
{
    private const int MaximumInspectedItems = 192;

    public static bool TryResolve(VanguardWorldLootContainerSnapshot container, string itemId, out Item item, out string sourcePath, out string sourceAddress)
    {
        item = null!;
        sourcePath = "none";
        sourceAddress = "none";
        if (container?.RootItem == null || string.IsNullOrWhiteSpace(itemId)) return false;

        try
        {
            int inspected = 0;
            if (TryVisitChildren(container.RootItem, "WorldContainer/" + Safe(container.RootItem.Id), itemId, ref inspected, out item, out sourcePath))
            {
                sourceAddress = VanguardCorpseLootLiveItemResolver.Fingerprint(item.CurrentAddress);
                return item.CurrentAddress != null;
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

    private static bool TryVisitChildren(Item root, string path, string itemId, ref int inspected, out Item item, out string sourcePath)
    {
        item = null!;
        sourcePath = "none";
        if (root is not CompoundItem compound) return false;
        if (compound.Slots != null)
        {
            foreach (Slot slot in compound.Slots)
            {
                if (slot?.ContainedItem == null) continue;
                if (TryVisit(slot.ContainedItem, path + "/slot:" + Safe(slot.ID), itemId, ref inspected, out item, out sourcePath)) return true;
            }
        }
        if (compound.Grids != null)
        {
            foreach (var grid in compound.Grids)
            {
                if (grid?.Items == null) continue;
                foreach (Item child in grid.Items)
                    if (child != null && TryVisit(child, path + "/grid", itemId, ref inspected, out item, out sourcePath)) return true;
            }
        }
        return false;
    }

    private static bool TryVisit(Item candidate, string path, string itemId, ref int inspected, out Item item, out string sourcePath)
    {
        item = null!;
        sourcePath = "none";
        if (++inspected > MaximumInspectedItems) return false;
        string currentPath = path + "/" + Safe(candidate.Id);
        if (string.Equals(candidate.Id, itemId, StringComparison.OrdinalIgnoreCase))
        {
            item = candidate;
            sourcePath = currentPath;
            return true;
        }
        return TryVisitChildren(candidate, currentPath, itemId, ref inspected, out item, out sourcePath);
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
}
#endif
