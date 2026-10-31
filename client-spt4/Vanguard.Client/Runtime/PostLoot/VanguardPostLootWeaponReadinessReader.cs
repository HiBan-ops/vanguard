#if SPT_CLIENT
using EFT;
using Vanguard.Client.Runtime.Audit;

// Responsibility: Reads and normalizes live evidence for Post Loot Weapon Readiness Reader in the post-loot recovery runtime.
// Flow: Live EFT/Fika/Vanguard objects are inspected defensively, normalized into a bounded snapshot, then handed to policy/decision code.
// Authority boundary: Read-only observer; it does not create missing truth or mutate the game state it inspects.
// Invariant: Missing/stale evidence degrades explicitly and reader failures must not silently fabricate an actionable state.
namespace Vanguard.Client.Runtime.PostLoot;

internal static class VanguardPostLootWeaponReadinessReader
{
    public static VanguardPostLootWeaponReadinessSnapshot Capture(BotOwner botOwner)
    {
        // Tactical-fitness provenance hardening: successful execution alone is not evidence that the readiness
        // surface was actually observed. Existing defensive reflection still owns access; the returned player
        // and weapon-manager surfaces establish the minimum proof for this snapshot.
        object? player = VanguardOperatorRuntimeAuditReflection.GetMember(botOwner, "GetPlayer", "Player");
        object? weaponManager = VanguardOperatorRuntimeAuditReflection.GetMember(botOwner, "WeaponManager", "WeaponManagerClass", "BotWeaponManager");
        bool observed = player != null && weaponManager != null;
        string observationReason = !observed
            ? (player == null ? "player_missing" : "weapon_manager_missing")
            : "runtime_weapon_surface_observed";

        object? handsController = VanguardOperatorRuntimeAuditReflection.GetMember(player, "HandsController", "_handsController", "handsController");
        object? currentWeapon = VanguardOperatorRuntimeAuditReflection.GetMember(weaponManager, "CurrentWeapon", "SelectedWeapon", "Weapon", "CurrentItem");
        object? primaryWeapon = VanguardOperatorRuntimeAuditReflection.GetMember(weaponManager, "PrimaryWeapon", "Primary", "Primary1", "PrimaryOne");
        object? secondaryWeapon = VanguardOperatorRuntimeAuditReflection.GetMember(weaponManager, "SecondaryWeapon", "Secondary", "Primary2", "PrimaryTwo");

        string handsType = VanguardOperatorRuntimeAuditReflection.TypeName(handsController);
        bool changingWeapon = BoolValue(VanguardOperatorRuntimeAuditReflection.GetMember(weaponManager, "IsChangingWeapon", "ChangingWeapon", "WeaponChanging", "IsWeaponChanging"))
            || BoolValue(VanguardOperatorRuntimeAuditReflection.GetMember(handsController, "IsChanging", "ChangingWeapon", "IsHandsChanging", "IsInInteraction"));
        bool handsSuspicious = IsHandsSuspicious(handsType, currentWeapon, changingWeapon);

        return new VanguardPostLootWeaponReadinessSnapshot
        {
            Observed = observed,
            ObservationReason = observationReason,
            HandsControllerType = handsType,
            CurrentWeaponKnown = currentWeapon != null,
            CurrentWeaponType = VanguardOperatorRuntimeAuditReflection.TypeName(currentWeapon),
            CurrentWeaponItemId = ItemId(currentWeapon),
            CurrentWeaponTpl = TemplateId(currentWeapon),
            PrimaryWeaponTpl = TemplateId(primaryWeapon),
            SecondaryWeaponTpl = TemplateId(secondaryWeapon),
            ChangingWeapon = changingWeapon,
            HandsSuspicious = handsSuspicious,
            FirstAidHaveToDo = BoolValue(VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "Medecine", "FirstAid", "Have2Do")),
            FirstAidUsing = BoolValue(VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "Medecine", "FirstAid", "Using"))
        };
    }

    private static bool IsHandsSuspicious(string handsType, object? currentWeapon, bool changingWeapon)
    {
        if (changingWeapon || currentWeapon == null || string.IsNullOrWhiteSpace(handsType) || handsType == "none")
        {
            return true;
        }

        string normalized = handsType.ToLowerInvariant();
        // EFT reports a typed Player.MedsController as "MedsController", not
        // "MedicineController". A cached/current weapon in WeaponManager is not proof
        // that the native hands controller has actually returned to that weapon.
        return normalized.Contains("empty") || normalized.Contains("medicine") || normalized.Contains("meds") || normalized.Contains("inventory") || normalized.Contains("item");
    }

    private static string ItemId(object? item)
    {
        if (item is EFT.InventoryLogic.Item typedItem)
        {
            return Safe(typedItem.Id);
        }

        object? id = VanguardOperatorRuntimeAuditReflection.GetMember(item, "Id", "ItemId", "_id");
        if (id == null)
        {
            object? nestedItem = VanguardOperatorRuntimeAuditReflection.GetMember(item, "Item", "Weapon", "CurrentItem");
            id = VanguardOperatorRuntimeAuditReflection.GetMember(nestedItem, "Id", "ItemId", "_id");
        }

        return Safe(VanguardOperatorRuntimeAuditReflection.Text(id));
    }

    private static string TemplateId(object? item)
    {
        if (item is EFT.InventoryLogic.Item typedItem)
        {
            return Safe(typedItem.StringTemplateId);
        }

        object? template = VanguardOperatorRuntimeAuditReflection.GetMember(item, "Template", "TemplateId", "Tpl", "_template");
        object? id = VanguardOperatorRuntimeAuditReflection.GetMember(template, "Id", "_id", "TemplateId");
        return Safe(VanguardOperatorRuntimeAuditReflection.FirstNonEmpty(VanguardOperatorRuntimeAuditReflection.Text(id), VanguardOperatorRuntimeAuditReflection.Text(template), VanguardOperatorRuntimeAuditReflection.Text(item)));
    }

    private static bool BoolValue(object? value) => value is bool b && b;
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
}

internal sealed class VanguardPostLootWeaponReadinessSnapshot
{
    public bool Observed { get; init; }
    public string ObservationReason { get; init; } = "unobserved";
    public string HandsControllerType { get; init; } = "none";
    public bool CurrentWeaponKnown { get; init; }
    public string CurrentWeaponType { get; init; } = "none";
    public string CurrentWeaponItemId { get; init; } = "none";
    public string CurrentWeaponTpl { get; init; } = "none";
    public string PrimaryWeaponTpl { get; init; } = "none";
    public string SecondaryWeaponTpl { get; init; } = "none";
    public bool ChangingWeapon { get; init; }
    public bool HandsSuspicious { get; init; }
    public bool FirstAidHaveToDo { get; init; }
    public bool FirstAidUsing { get; init; }

    public bool WeaponReady => CurrentWeaponKnown && !ChangingWeapon && !HandsSuspicious;

    public string Signature => Bool(Observed) + "|" + Safe(ObservationReason) + "|" + Safe(HandsControllerType) + "|" + Safe(CurrentWeaponItemId) + "|" + Safe(CurrentWeaponTpl) + "|" + ChangingWeapon + "|" + HandsSuspicious + "|" + FirstAidHaveToDo + "|" + FirstAidUsing;

    public string Summary => "observed=" + Bool(Observed)
        + ";observationReason=" + Safe(ObservationReason)
        + ";weaponReady=" + Bool(WeaponReady)
        + ";currentWeaponKnown=" + Bool(CurrentWeaponKnown)
        + ";currentWeaponType=" + Safe(CurrentWeaponType)
        + ";currentWeaponItemId=" + Safe(CurrentWeaponItemId)
        + ";currentWeaponTpl=" + Safe(CurrentWeaponTpl)
        + ";primaryWeaponTpl=" + Safe(PrimaryWeaponTpl)
        + ";secondaryWeaponTpl=" + Safe(SecondaryWeaponTpl)
        + ";handsController=" + Safe(HandsControllerType)
        + ";changingWeapon=" + Bool(ChangingWeapon)
        + ";handsSuspicious=" + Bool(HandsSuspicious)
        + ";firstAidHave2Do=" + Bool(FirstAidHaveToDo)
        + ";firstAidUsing=" + Bool(FirstAidUsing);

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
}
#endif
