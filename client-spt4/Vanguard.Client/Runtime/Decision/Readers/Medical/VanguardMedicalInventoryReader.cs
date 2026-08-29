#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Inventory;
using Vanguard.Client.Runtime.Medical;

// Responsibility: Reads and normalizes live evidence for Medical Inventory Reader in the decision snapshot pipeline.
// Flow: Live EFT/Fika/Vanguard objects are inspected defensively, normalized into a bounded snapshot, then handed to policy/decision code.
// Authority boundary: Read-only observer; it does not create missing truth or mutate the game state it inspects.
// Invariant: Missing/stale evidence degrades explicitly and reader failures must not silently fabricate an actionable state.
namespace Vanguard.Client.Runtime.Decision;

internal sealed class VanguardMedicalInventoryReadResult
{
    public VanguardMedicalInventorySnapshot Snapshot { get; set; } = VanguardMedicalInventorySnapshot.Empty;
    public Dictionary<string, MedsItemClass> ItemByTemplateId { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<MedsItemClass>> ItemsByTemplateId { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal static class VanguardMedicalInventoryReader
{
    private static readonly EquipmentSlot[] MedicalSearchSlots =
    {
        EquipmentSlot.Pockets,
        EquipmentSlot.TacticalVest,
        EquipmentSlot.Backpack,
        EquipmentSlot.SecuredContainer,
    };

    public static VanguardMedicalInventoryReadResult Capture(BotOwner? botOwner)
    {
        var result = new VanguardMedicalInventoryReadResult();
        if (botOwner == null)
        {
            result.Snapshot = new VanguardMedicalInventorySnapshot { Observed = false, Source = "botOwnerNull" };
            return result;
        }

        try
        {
            var player = botOwner.GetPlayer;
            var items = new List<MedsItemClass>();
            player.InventoryController.GetAcceptableItemsNonAlloc(MedicalSearchSlots, items);
            VanguardSpecialEquipmentSlotReader.AppendDirectItems(player.Inventory?.Equipment, items);

            var names = new List<string>();
            foreach (var item in items)
            {
                string templateId = ResolveTemplateId(item);
                if (string.IsNullOrWhiteSpace(templateId) || !VanguardMedicalItemCapabilityResolver.IsKnownTemplate(templateId))
                {
                    continue;
                }

                if (!result.ItemsByTemplateId.TryGetValue(templateId, out var templateItems))
                {
                    templateItems = new List<MedsItemClass>();
                    result.ItemsByTemplateId[templateId] = templateItems;
                }
                templateItems.Add(item);

                if (!result.ItemByTemplateId.TryGetValue(templateId, out var currentBest)
                    || ReadItemResource(item) > ReadItemResource(currentBest))
                {
                    result.ItemByTemplateId[templateId] = item;
                }

                names.Add(Safe(item.Name ?? item.ShortName ?? templateId));
            }

            result.Snapshot = new VanguardMedicalInventorySnapshot
            {
                Observed = true,
                AcceptableItemCount = items.Count,
                MedicalTemplateCount = result.ItemByTemplateId.Count,
                CandidateTemplateIds = JoinOrNone(result.ItemByTemplateId.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
                CandidateNames = JoinOrNone(names.Distinct(StringComparer.OrdinalIgnoreCase).Take(12)),
                Source = "inventoryController.acceptableItemsNonAlloc;readonly=true"
            };
            return result;
        }
        catch (Exception ex)
        {
            result.Snapshot = new VanguardMedicalInventorySnapshot
            {
                Observed = false,
                Source = "inventoryReadFailed;reason=" + ex.GetType().Name
            };
            return result;
        }
    }


    public static string ResolveItemInstanceId(MedsItemClass? item)
    {
        if (item == null) return "none";
        if (!string.IsNullOrWhiteSpace(item.Id))
        {
            return item.Id.Trim();
        }

        // Preserve per-instance identity even if a future EFT build hides the canonical id. The
        // process-local fallback is stable for the lifetime of the item object and never broadens a
        // quarantine to every item sharing the same template.
        return "runtime-" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static float ReadItemResource(MedsItemClass? item)
    {
        if (item == null) return -1f;
        try
        {
            return item.MedKitComponent?.HpResource ?? -1f;
        }
        catch
        {
            object? medKit = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(item, "MedKitComponent", "MedKit");
            return TryFloat(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(medKit, "HpResource", "Resource", "Value"));
        }
    }

    public static float ReadItemMaxResource(MedsItemClass? item)
    {
        if (item == null) return -1f;
        try
        {
            return item.MedKitComponent?.MaxHpResource ?? -1f;
        }
        catch
        {
            object? medKit = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(item, "MedKitComponent", "MedKit");
            return TryFloat(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(medKit, "MaxHpResource", "MaxResource", "Maximum"));
        }
    }

    private static float TryFloat(object? value)
    {
        if (value == null) return -1f;
        try { return Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return -1f; }
    }

    private static string ResolveTemplateId(MedsItemClass item)
    {
        string direct = VanguardMedicalItemCapabilityResolver.NormalizeTemplateId(item.StringTemplateId);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        string fallback = VanguardMedicalItemCapabilityResolver.NormalizeTemplateId(
            VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(item, "Tpl", "tpl", "_tpl", "Id", "_id")?.ToString());
        return fallback;
    }

    private static string JoinOrNone(IEnumerable<string> values)
    {
        var array = values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(Safe).ToArray();
        return array.Length == 0 ? "none" : string.Join(",", array);
    }

    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "none";
        return value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_').Replace('\t', '_');
    }
}
#endif
