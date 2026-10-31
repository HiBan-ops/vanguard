#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Medical.Execution;

// Responsibility: Resolves one exact Medic-owned item instance for an already-qualified support need.
// Flow: Reuses the canonical Vanguard capability resolver and bounded medical inventory reader, then applies resource/reserve policy before returning an exact EFT item.
// Authority boundary: Read/preflight only; it never consumes, drops, moves or transfers an item.
// Invariant: Direct care and supply drop share the same exact-instance/resource/reserve truth and never maintain a parallel medical item catalogue.
namespace Vanguard.Client.Runtime.Medical.Support;

internal static class VanguardMedicSupportItemSelector
{
    public static bool TrySelectExactItem(
        BotOwner caregiver,
        VanguardMedicSupportCandidateFact fact,
        out MedsItemClass item,
        out float resource,
        out float maxResource,
        out string reason)
    {
        item = null!;
        resource = -1f;
        maxResource = -1f;
        reason = "none";
        if (caregiver == null || fact == null)
        {
            reason = "caregiver_or_candidate_missing";
            return false;
        }

        VanguardMedicalInventoryReadResult inventory = VanguardMedicalInventoryReader.Capture(caregiver);
        string templateId = VanguardMedicalItemCapabilityResolver.NormalizeTemplateId(fact.CandidateItemTemplateId);
        if (!inventory.Snapshot.Observed
            || string.IsNullOrWhiteSpace(templateId)
            || !inventory.ItemsByTemplateId.TryGetValue(templateId, out List<MedsItemClass> matching)
            || matching.Count == 0)
        {
            reason = "exact_candidate_template_not_present_at_preflight";
            return false;
        }

        MedsItemClass? selected = matching
            .Select(candidate => new
            {
                Item = candidate,
                Resource = VanguardMedicalInventoryReader.ReadItemResource(candidate),
                Max = VanguardMedicalInventoryReader.ReadItemMaxResource(candidate),
                Id = VanguardMedicalInventoryReader.ResolveItemInstanceId(candidate)
            })
            .Where(candidate => IsResourceViable(candidate.Resource, candidate.Max))
            .OrderByDescending(candidate => candidate.Resource)
            .ThenBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Item)
            .FirstOrDefault();

        if (selected == null)
        {
            reason = "candidate_template_present_but_no_viable_exact_item_resource";
            return false;
        }

        if (fact.PatientNeed != VanguardMedicalNeed.HeavyBleed
            && IsHeavyBleedCapableTemplate(templateId)
            && CountViableHeavyBleedItems(inventory) <= 1)
        {
            reason = "critical_bleed_reserve_preserved";
            return false;
        }

        item = selected;
        resource = VanguardMedicalInventoryReader.ReadItemResource(selected);
        maxResource = VanguardMedicalInventoryReader.ReadItemMaxResource(selected);
        reason = "exact_item_resource_and_reserve_qualified";
        return true;
    }

    public static bool TryFindExactItem(
        BotOwner caregiver,
        string itemInstanceId,
        out MedsItemClass item,
        out float resource,
        out float maxResource)
    {
        item = null!;
        resource = -1f;
        maxResource = -1f;
        if (caregiver == null || string.IsNullOrWhiteSpace(itemInstanceId))
        {
            return false;
        }

        VanguardMedicalInventoryReadResult inventory = VanguardMedicalInventoryReader.Capture(caregiver);
        foreach (List<MedsItemClass> items in inventory.ItemsByTemplateId.Values)
        {
            foreach (MedsItemClass candidate in items)
            {
                if (!string.Equals(VanguardMedicalInventoryReader.ResolveItemInstanceId(candidate), itemInstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                item = candidate;
                resource = VanguardMedicalInventoryReader.ReadItemResource(candidate);
                maxResource = VanguardMedicalInventoryReader.ReadItemMaxResource(candidate);
                return true;
            }
        }
        return false;
    }

    public static bool ContainsExactItem(BotOwner owner, string itemInstanceId)
        => TryFindExactItem(owner, itemInstanceId, out _, out _, out _);

    public static bool IsResourceViable(float resource, float maxResource)
        => maxResource <= 0f || resource < 0f || resource > 0.01f;

    private static int CountViableHeavyBleedItems(VanguardMedicalInventoryReadResult inventory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VanguardMedicalItemCapability capability in VanguardMedicalItemCapabilityResolver.GetCandidates(VanguardMedicalNeed.HeavyBleed))
        {
            string templateId = VanguardMedicalItemCapabilityResolver.NormalizeTemplateId(capability.TemplateId);
            if (!inventory.ItemsByTemplateId.TryGetValue(templateId, out List<MedsItemClass> items))
            {
                continue;
            }

            foreach (MedsItemClass candidate in items)
            {
                float resource = VanguardMedicalInventoryReader.ReadItemResource(candidate);
                float max = VanguardMedicalInventoryReader.ReadItemMaxResource(candidate);
                if (IsResourceViable(resource, max))
                {
                    seen.Add(VanguardMedicalInventoryReader.ResolveItemInstanceId(candidate));
                }
            }
        }
        return seen.Count;
    }

    private static bool IsHeavyBleedCapableTemplate(string templateId)
        => VanguardMedicalItemCapabilityResolver.GetCandidates(VanguardMedicalNeed.HeavyBleed)
            .Any(capability => string.Equals(
                VanguardMedicalItemCapabilityResolver.NormalizeTemplateId(capability.TemplateId),
                templateId,
                StringComparison.OrdinalIgnoreCase));
}
#endif
