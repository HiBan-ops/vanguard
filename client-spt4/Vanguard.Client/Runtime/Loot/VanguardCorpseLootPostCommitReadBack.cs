#if SPT_CLIENT
using System;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using EftWeapon = global::EFT.InventoryLogic.Weapon;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Medical;

// Responsibility: Provides Corpse Loot Post Commit Read Back support for the loot runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Loot;

/// <summary>
/// The persistence path proves that a committed item is present in the Operator inventory and, for typed combat/medical
/// categories, visible to the EFT subsystem expected to use it. Generic Wishlist/value items and
/// The persistence path raid-detachable weapon mods require inventory presence only; no artificial runtime subsystem
/// is invented for them. The persistence path preserves
/// strict Vanguard read-back for catalogued medical items and accepts unknown native medical items only
/// after exact EFT-controller recognition. Read-back never creates an inventory operation.
/// </summary>
internal static class VanguardCorpseLootPostCommitReadBack
{
    public static VanguardCorpseLootPostCommitReadBackResult RefreshAndVerify(
        VanguardCorpseLootPreparedTransaction prepared,
        BotOwner? botOwner)
    {
        if (prepared == null || botOwner?.GetPlayer?.InventoryController == null)
        {
            return Failed("prepared_or_bot_owner_missing");
        }

        try
        {
            bool itemInInventory = prepared.Inventory.TryFindItem(prepared.Item.Id, out Item foundItem)
                && ReferenceEquals(foundItem, prepared.Item);
            if (!itemInInventory)
            {
                return Failed("item_not_found_in_operator_inventory");
            }

            bool weaponRecognized = false;
            bool eftMedicalRecognized = false;
            bool vanguardMedicalRecognized = false;
            bool medicalTemplateKnownToVanguard = false;
            bool eftNativeMedicalFallbackUsed = false;
            string medicalReadBackMode = "none";
            bool magazineCompatible = false;
            bool looseAmmunitionCompatible = false;

            if (prepared.Item is EftWeapon)
            {
                botOwner.WeaponManager.UpdateWeaponsList();
                weaponRecognized = IsReferencedByWeaponSelector(botOwner.WeaponManager, prepared.Item);
            }
            else if (prepared.Item is MedsItemClass meds)
            {
                botOwner.Medecine?.RefreshCurMeds();
                eftMedicalRecognized = IsSelectedByMedicalController(botOwner.Medecine, prepared.Item);
                medicalTemplateKnownToVanguard = VanguardMedicalItemCapabilityResolver.IsKnownTemplate(meds.StringTemplateId);

                if (medicalTemplateKnownToVanguard)
                {
                    VanguardMedicalInventoryReadResult medicalRead = VanguardMedicalInventoryReader.Capture(botOwner);
                    VanguardOperatorLootNeedSnapshot need = VanguardOperatorLootNeedReader.Capture(botOwner);
                    vanguardMedicalRecognized = IsExactMedicalItemInVanguardSelection(medicalRead, meds)
                        && need.Observed
                        && VanguardOperatorLootNeedReader.IsMedicalCapabilityRecognized(meds, need);
                    medicalReadBackMode = vanguardMedicalRecognized
                        ? "vanguard_typed"
                        : "vanguard_typed_missing";
                }
                else
                {
                    eftNativeMedicalFallbackUsed = eftMedicalRecognized;
                    medicalReadBackMode = eftNativeMedicalFallbackUsed
                        ? "eft_native_fallback"
                        : "eft_native_missing";
                }
            }
            else if (prepared.Item is MagazineItemClass magazine)
            {
                prepared.Inventory.StrictCheckMagazine(magazine, false, 0, false, false);
                botOwner.WeaponManager.UpdateWeaponsList();
                magazineCompatible = VanguardOperatorLootNeedReader.FitsAnyWeapon(
                    magazine,
                    VanguardOperatorLootNeedReader.CaptureCurrentWeapons(botOwner));
            }
            else if (prepared.Item is AmmoItemClass ammunition)
            {
                botOwner.WeaponManager.UpdateWeaponsList();
                looseAmmunitionCompatible = VanguardOperatorLootNeedReader.FitsAnyWeapon(
                    ammunition,
                    VanguardOperatorLootNeedReader.CaptureCurrentWeapons(botOwner));
            }

            botOwner.GetPlayer.UpdateInteractionCast();

            bool secondarySwapDisplacedObserved = !prepared.Preflight.SecondarySwap;
            bool secondarySwapSourceRestored = !prepared.Preflight.SecondarySwap;
            if (prepared.Preflight.SecondarySwap)
            {
                Item? displaced = prepared.DisplacedItem;
                if (displaced?.CurrentAddress != null)
                {
                    secondarySwapDisplacedObserved = true;
                    string displacedAddress = VanguardCorpseLootLiveItemResolver.Fingerprint(displaced.CurrentAddress);
                    secondarySwapSourceRestored = string.Equals(
                        displacedAddress,
                        prepared.Preflight.SourceAddressFingerprint,
                        StringComparison.OrdinalIgnoreCase);
                }
            }

            bool medicalCategoryRecognized = medicalTemplateKnownToVanguard
                ? vanguardMedicalRecognized
                : eftMedicalRecognized;
            bool categoryRecognized = prepared.Preflight.Category switch
            {
                "long_weapon" or "holster_weapon" => weaponRecognized,
                "medical" => medicalCategoryRecognized,
                "magazine" => magazineCompatible,
                "loose_ammunition" => looseAmmunitionCompatible,
                "grenade" => true,
                "generic" or "weapon_mod" => itemInInventory,
                _ => false
            };
            bool swapReadBackSatisfied = !prepared.Preflight.SecondarySwap
                || (secondarySwapDisplacedObserved && secondarySwapSourceRestored);
            string reason = !swapReadBackSatisfied
                ? "secondary_swap_displaced_item_not_at_candidate_source"
                : categoryRecognized
                    ? eftNativeMedicalFallbackUsed
                        ? "eft_native_medical_fallback_recognized"
                        : "runtime_subsystem_recognized_item"
                    : "runtime_subsystem_did_not_recognize_item";

            return new VanguardCorpseLootPostCommitReadBackResult
            {
                Success = itemInInventory && categoryRecognized && swapReadBackSatisfied,
                Reason = reason,
                ItemInOperatorInventory = itemInInventory,
                WeaponManagerRecognized = weaponRecognized,
                EftMedicalRecognized = eftMedicalRecognized,
                VanguardMedicalRecognized = vanguardMedicalRecognized,
                MedicalTemplateKnownToVanguard = medicalTemplateKnownToVanguard,
                EftNativeMedicalFallbackUsed = eftNativeMedicalFallbackUsed,
                MedicalReadBackMode = medicalReadBackMode,
                MagazineCompatible = magazineCompatible,
                LooseAmmunitionCompatible = looseAmmunitionCompatible,
                SecondarySwapDisplacedItemObserved = secondarySwapDisplacedObserved,
                SecondarySwapSourceRestored = secondarySwapSourceRestored
            };
        }
        catch (Exception exception)
        {
            return Failed("readback_exception:" + exception.GetType().Name);
        }
    }


    private static bool IsExactMedicalItemInVanguardSelection(VanguardMedicalInventoryReadResult read, MedsItemClass item)
    {
        string templateId = VanguardMedicalItemCapabilityResolver.NormalizeTemplateId(item.StringTemplateId);
        if (string.IsNullOrWhiteSpace(templateId)
            || !read.ItemsByTemplateId.TryGetValue(templateId, out System.Collections.Generic.List<MedsItemClass>? candidates)
            || candidates == null)
        {
            return false;
        }

        foreach (MedsItemClass candidate in candidates)
        {
            if (ReferenceEquals(candidate, item)
                || (!string.IsNullOrWhiteSpace(candidate.Id)
                    && string.Equals(candidate.Id, item.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsReferencedByWeaponSelector(object weaponManager, Item item)
    {
        object? selector = ReadMember(weaponManager, "Selector");
        if (selector == null) return false;
        foreach (string member in new[] { "FirstPrimaryWeaponItem", "SecondPrimaryWeaponItem", "HolsterItem" })
        {
            if (ReferenceEquals(ReadMember(selector, member), item)) return true;
        }
        return false;
    }

    private static bool IsSelectedByMedicalController(object? medecine, Item item)
    {
        if (medecine == null) return false;

        object? firstAid = ReadMember(medecine, "FirstAid");
        if (IsSelectedMedicalItem(firstAid, "CurUsingMeds", item)) return true;

        object? surgicalKit = ReadMember(medecine, "SurgicalKit");
        if (IsSelectedMedicalItem(surgicalKit, "CurUsingMeds", item)) return true;

        object? stimulators = ReadMember(medecine, "Stimulators");
        return IsSelectedMedicalItem(stimulators, "StimulatorItemClass", item);
    }

    private static bool IsSelectedMedicalItem(object? controller, string memberName, Item expected)
    {
        return controller != null
            && ReadMember(controller, memberName) is Item selected
            && IsSameItem(selected, expected);
    }

    private static bool IsSameItem(Item candidate, Item expected)
    {
        if (ReferenceEquals(candidate, expected)) return true;
        return !string.IsNullOrWhiteSpace(candidate.Id)
            && !string.IsNullOrWhiteSpace(expected.Id)
            && string.Equals(candidate.Id, expected.Id, StringComparison.OrdinalIgnoreCase);
    }

    private static object? ReadMember(object target, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            Type type = target.GetType();
            PropertyInfo? property = type.GetProperty(name, flags);
            if (property != null) return property.GetValue(target);
            FieldInfo? field = type.GetField(name, flags);
            return field?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static VanguardCorpseLootPostCommitReadBackResult Failed(string reason)
        => new() { Success = false, Reason = reason };
}
#endif
