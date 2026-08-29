#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using EftWeapon = global::EFT.InventoryLogic.Weapon;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Inventory;
using Vanguard.Client.Runtime.Medical;

// Responsibility: Reads and normalizes live evidence for Operator Loot Need Reader in the loot runtime.
// Flow: Live EFT/Fika/Vanguard objects are inspected defensively, normalized into a bounded snapshot, then handed to policy/decision code.
// Authority boundary: Read-only observer; it does not create missing truth or mutate the game state it inspects.
// Invariant: Missing/stale evidence degrades explicitly and reader failures must not silently fabricate an actionable state.
namespace Vanguard.Client.Runtime.Loot;

internal static class VanguardOperatorLootNeedReader
{
    private static readonly EquipmentSlot[] SearchSlots =
    {
        EquipmentSlot.Pockets,
        EquipmentSlot.TacticalVest,
        EquipmentSlot.Backpack,
        EquipmentSlot.SecuredContainer,
    };

    public static VanguardOperatorLootNeedSnapshot Capture(BotOwner? botOwner)
    {
        if (botOwner?.GetPlayer?.InventoryController == null)
        {
            return new VanguardOperatorLootNeedSnapshot { Observed = false, Source = "bot_or_inventory_missing" };
        }

        try
        {
            var player = botOwner.GetPlayer;
            var equipment = player.Inventory?.Equipment;
            IReadOnlyList<EftWeapon> weapons = CaptureCurrentWeapons(botOwner);
            var firstPrimary = equipment?.GetSlot(EquipmentSlot.FirstPrimaryWeapon)?.ContainedItem as EftWeapon;
            var secondPrimary = equipment?.GetSlot(EquipmentSlot.SecondPrimaryWeapon)?.ContainedItem as EftWeapon;

            int longWeaponCount = (firstPrimary != null ? 1 : 0) + (secondPrimary != null ? 1 : 0);
            EquipmentSlot? protectedPrimarySlot = null;
            EquipmentSlot? emptyLongWeaponSlot = null;
            if (longWeaponCount == 0)
            {
                // The legacy need surface exposes one destination at a time. FirstPrimary is the
                // deterministic first acquisition target; after that mutation the next read exposes SecondPrimary.
                emptyLongWeaponSlot = EquipmentSlot.FirstPrimaryWeapon;
            }
            else if (longWeaponCount == 1)
            {
                protectedPrimarySlot = firstPrimary != null
                    ? EquipmentSlot.FirstPrimaryWeapon
                    : EquipmentSlot.SecondPrimaryWeapon;
                emptyLongWeaponSlot = firstPrimary == null
                    ? EquipmentSlot.FirstPrimaryWeapon
                    : EquipmentSlot.SecondPrimaryWeapon;
            }

            var allItems = new List<Item>();
            player.InventoryController.GetAcceptableItemsNonAlloc(SearchSlots, allItems);
            VanguardSpecialEquipmentSlotReader.AppendDirectItems(equipment, allItems);

            int compatibleMags = 0;
            int compatibleMagAmmo = 0;
            int compatibleLooseAmmo = 0;
            int grenades = 0;
            int medical = 0;
            bool heavy = false;
            bool light = false;
            bool fracture = false;
            bool hp = false;
            bool pain = false;
            bool surgery = false;

            foreach (Item item in allItems)
            {
                if (item is MagazineItemClass magazine
                    && magazine.Count > 0
                    && FitsAnyWeapon(magazine, weapons))
                {
                    compatibleMags++;
                    compatibleMagAmmo += magazine.Count;
                }

                if (item is AmmoItemClass ammunition
                    && ammunition.StackObjectsCount > 0
                    && FitsAnyWeapon(ammunition, weapons))
                {
                    compatibleLooseAmmo += ammunition.StackObjectsCount;
                }

                if (item is ThrowWeapItemClass)
                {
                    grenades++;
                }

                if (item is not MedsItemClass meds || !IsUsableMedicalItem(meds))
                {
                    continue;
                }

                medical++;
                ApplyMedicalCapabilities(meds, ref heavy, ref light, ref fracture, ref hp, ref pain, ref surgery);
            }

            return new VanguardOperatorLootNeedSnapshot
            {
                Observed = true,
                HasPrimaryWeapon = longWeaponCount > 0,
                LongWeaponCount = longWeaponCount,
                ProtectedPrimarySlot = protectedPrimarySlot,
                EmptyLongWeaponSlot = emptyLongWeaponSlot,
                HolsterSlotEmpty = equipment?.GetSlot(EquipmentSlot.Holster)?.ContainedItem == null,
                CompatibleMagazineCount = compatibleMags,
                CompatibleMagazineAmmoCount = compatibleMagAmmo,
                CompatibleLooseAmmunitionCount = compatibleLooseAmmo,
                GrenadeCount = grenades,
                MedicalItemCount = medical,
                HasHeavyBleedTreatment = heavy,
                HasLightBleedTreatment = light,
                HasFractureTreatment = fracture,
                HasHpTreatment = hp,
                HasPainMobilityTreatment = pain,
                HasSurgeryTreatment = surgery,
                Source = "inventoryController.acceptableItemsNonAlloc;operational_loot_readback=true"
            };
        }
        catch (Exception exception)
        {
            return new VanguardOperatorLootNeedSnapshot
            {
                Observed = false,
                Source = "loot_need_read_failed:" + exception.GetType().Name
            };
        }
    }

    internal static IReadOnlyList<EftWeapon> CaptureCurrentWeapons(BotOwner? botOwner)
    {
        var weapons = new List<EftWeapon>(3);
        InventoryEquipment? equipment = botOwner?.GetPlayer?.Inventory?.Equipment;
        if (equipment?.GetSlot(EquipmentSlot.FirstPrimaryWeapon)?.ContainedItem is EftWeapon first) weapons.Add(first);
        if (equipment?.GetSlot(EquipmentSlot.SecondPrimaryWeapon)?.ContainedItem is EftWeapon second) weapons.Add(second);
        if (equipment?.GetSlot(EquipmentSlot.Holster)?.ContainedItem is EftWeapon holster) weapons.Add(holster);
        return weapons;
    }

    internal static bool IsUsableMedicalItem(MedsItemClass meds)
    {
        try
        {
            float maximum = VanguardMedicalInventoryReader.ReadItemMaxResource(meds);
            float current = VanguardMedicalInventoryReader.ReadItemResource(meds);
            return maximum <= 0f || current > 0.01f;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsMedicalCapabilityRecognized(MedsItemClass meds, VanguardOperatorLootNeedSnapshot need)
    {
        bool anyCatalogCapability = false;
        string templateId = VanguardMedicalItemCapabilityResolver.NormalizeTemplateId(meds.StringTemplateId);
        foreach (VanguardMedicalItemCapability capability in VanguardMedicalItemCapabilityResolver.Catalog)
        {
            if (!string.Equals(capability.TemplateId, templateId, StringComparison.OrdinalIgnoreCase)) continue;
            anyCatalogCapability = true;
            if (capability.Need == VanguardMedicalNeed.HeavyBleed && need.HasHeavyBleedTreatment) return true;
            if (capability.Need == VanguardMedicalNeed.LightBleed && need.HasLightBleedTreatment) return true;
            if (capability.Need == VanguardMedicalNeed.Fracture && need.HasFractureTreatment) return true;
            if (capability.Need == VanguardMedicalNeed.HpHeal && need.HasHpTreatment) return true;
            if (capability.Need == VanguardMedicalNeed.PainMobility && need.HasPainMobilityTreatment) return true;
            if (capability.Need == VanguardMedicalNeed.SurgeryDestroyedPart && need.HasSurgeryTreatment) return true;
        }

        return !anyCatalogCapability && need.MedicalItemCount > 0 && IsUsableMedicalItem(meds);
    }

    internal static bool FitsAnyWeapon(MagazineItemClass magazine, IReadOnlyList<EftWeapon> weapons)
    {
        foreach (EftWeapon weapon in weapons)
        {
            try
            {
                if (weapon.GetMagazineSlot()?.CheckCompatibility(magazine) == true)
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    internal static bool FitsAnyWeapon(AmmoItemClass ammunition, IReadOnlyList<EftWeapon> weapons)
    {
        if (string.IsNullOrWhiteSpace(ammunition.Caliber)) return false;
        foreach (EftWeapon weapon in weapons)
        {
            try
            {
                if (string.Equals(ammunition.Caliber, weapon.AmmoCaliber, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private static void ApplyMedicalCapabilities(
        MedsItemClass meds,
        ref bool heavy,
        ref bool light,
        ref bool fracture,
        ref bool hp,
        ref bool pain,
        ref bool surgery)
    {
        string templateId = VanguardMedicalItemCapabilityResolver.NormalizeTemplateId(meds.StringTemplateId);
        foreach (VanguardMedicalItemCapability capability in VanguardMedicalItemCapabilityResolver.Catalog)
        {
            if (!string.Equals(capability.TemplateId, templateId, StringComparison.OrdinalIgnoreCase)) continue;
            switch (capability.Need)
            {
                case VanguardMedicalNeed.HeavyBleed: heavy = true; break;
                case VanguardMedicalNeed.LightBleed: light = true; break;
                case VanguardMedicalNeed.Fracture: fracture = true; break;
                case VanguardMedicalNeed.HpHeal: hp = true; break;
                case VanguardMedicalNeed.PainMobility: pain = true; break;
                case VanguardMedicalNeed.SurgeryDestroyedPart: surgery = true; break;
            }
        }
    }
}
#endif
