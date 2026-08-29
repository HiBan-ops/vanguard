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
        object? player = VanguardOperatorRuntimeAuditReflection.GetMember(botOwner, "GetPlayer", "Player");
        object? handsController = VanguardOperatorRuntimeAuditReflection.GetMember(player, "HandsController", "_handsController", "handsController");
        object? weaponManager = VanguardOperatorRuntimeAuditReflection.GetMember(botOwner, "WeaponManager", "WeaponManagerClass", "BotWeaponManager");
        object? currentWeapon = VanguardOperatorRuntimeAuditReflection.GetMember(weaponManager, "CurrentWeapon", "SelectedWeapon", "Weapon", "CurrentItem");
        object? primaryWeapon = VanguardOperatorRuntimeAuditReflection.GetMember(weaponManager, "PrimaryWeapon", "Primary", "Primary1", "PrimaryOne");
        object? secondaryWeapon = VanguardOperatorRuntimeAuditReflection.GetMember(weaponManager, "SecondaryWeapon", "Secondary", "Primary2", "PrimaryTwo");

        string handsType = VanguardOperatorRuntimeAuditReflection.TypeName(handsController);
        bool changingWeapon = BoolValue(VanguardOperatorRuntimeAuditReflection.GetMember(weaponManager, "IsChangingWeapon", "ChangingWeapon", "WeaponChanging", "IsWeaponChanging"))
            || BoolValue(VanguardOperatorRuntimeAuditReflection.GetMember(handsController, "IsChanging", "ChangingWeapon", "IsHandsChanging", "IsInInteraction"));
        bool handsSuspicious = IsHandsSuspicious(handsType, currentWeapon, changingWeapon);

        return new VanguardPostLootWeaponReadinessSnapshot
        {
            HandsControllerType = handsType,
            CurrentWeaponKnown = currentWeapon != null,
            CurrentWeaponType = VanguardOperatorRuntimeAuditReflection.TypeName(currentWeapon),
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
        return normalized.Contains("empty") || normalized.Contains("medicine") || normalized.Contains("inventory") || normalized.Contains("item");
    }

    private static string TemplateId(object? item)
    {
        object? template = VanguardOperatorRuntimeAuditReflection.GetMember(item, "Template", "TemplateId", "Tpl", "_template");
        object? id = VanguardOperatorRuntimeAuditReflection.GetMember(template, "Id", "_id", "TemplateId");
        return Safe(VanguardOperatorRuntimeAuditReflection.FirstNonEmpty(VanguardOperatorRuntimeAuditReflection.Text(id), VanguardOperatorRuntimeAuditReflection.Text(template), VanguardOperatorRuntimeAuditReflection.Text(item)));
    }

    private static bool BoolValue(object? value) => value is bool b && b;
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
}

internal sealed class VanguardPostLootWeaponReadinessSnapshot
{
    public string HandsControllerType { get; init; } = "none";
    public bool CurrentWeaponKnown { get; init; }
    public string CurrentWeaponType { get; init; } = "none";
    public string CurrentWeaponTpl { get; init; } = "none";
    public string PrimaryWeaponTpl { get; init; } = "none";
    public string SecondaryWeaponTpl { get; init; } = "none";
    public bool ChangingWeapon { get; init; }
    public bool HandsSuspicious { get; init; }
    public bool FirstAidHaveToDo { get; init; }
    public bool FirstAidUsing { get; init; }

    public bool WeaponReady => CurrentWeaponKnown && !ChangingWeapon && !HandsSuspicious;

    public string Signature => Safe(HandsControllerType) + "|" + Safe(CurrentWeaponTpl) + "|" + ChangingWeapon + "|" + HandsSuspicious + "|" + FirstAidHaveToDo + "|" + FirstAidUsing;

    public string Summary => "weaponReady=" + Bool(WeaponReady)
        + ";currentWeaponKnown=" + Bool(CurrentWeaponKnown)
        + ";currentWeaponType=" + Safe(CurrentWeaponType)
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
