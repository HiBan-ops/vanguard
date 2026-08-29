#if SPT_CLIENT
using System;
using System.Globalization;
using EFT.InventoryLogic;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Integrations.MoreBots;

// Responsibility: Reads and normalizes live evidence for Orbit Loot Value Reader in the external AI integration.
// Flow: Live EFT/Fika/Vanguard objects are inspected defensively, normalized into a bounded snapshot, then handed to policy/decision code.
// Authority boundary: Read-only observer; it does not create missing truth or mutate the game state it inspects.
// Invariant: Missing/stale evidence degrades explicitly and reader failures must not silently fabricate an actionable state.
namespace Vanguard.Client.Runtime.Integrations.Looting;

/// <summary>
/// Lightweight valuation bridge for future Vanguard-owned loot decisions.
/// ORBIT's equipment/price heuristics remain useful, but this reader never grants ORBIT movement authority.
/// </summary>
internal static class VanguardOrbitLootValueReader
{
    public const string StatusTag = VanguardOperatorBotTypes.LootBoundaryStatusTag;

    public static bool TryGetPrice(Item? item, out float price, out string source)
    {
        price = 0f;
        source = "none";
        if (item == null)
        {
            source = "item_missing";
            return false;
        }

        if (VanguardHandbookPriceCache.TryGetPrice(item.StringTemplateId, out price))
        {
            source = "vanguard:spt_handbook_cache";
            return true;
        }

        if (TryInvokePrice("Orbit.Looting.ItemPriceLookup", "GetPrice", item, out price))
        {
            source = "orbit:GetPrice";
            return true;
        }

        if (TryInvokePrice("LootingBots.External", "GetItemPrice", item, out price))
        {
            source = "lootingbots:GetItemPrice";
            return true;
        }

        source = "valuation_bridge_missing";
        return false;
    }

    private static bool TryInvokePrice(string typeName, string methodName, Item item, out float price)
    {
        price = 0f;
        Type? type = VanguardOperatorRuntimeAuditReflection.FindType(typeName);
        if (type == null)
        {
            return false;
        }

        object? result = VanguardOperatorRuntimeAuditReflection.InvokeStatic(type, methodName, item);
        switch (result)
        {
            case int i when i > 0:
                price = i;
                return true;
            case float f when f > 0f:
                price = f;
                return true;
            case double d when d > 0d:
                price = (float)d;
                return true;
            case decimal m when m > 0m:
                price = float.Parse(m.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                return true;
            default:
                return false;
        }
    }
}
#endif
