#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;

// Responsibility: Provides Medical Item Capability Resolver support for the medical runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Medical;

internal sealed record VanguardMedicalItemCapability(
    string TemplateId,
    string Name,
    VanguardMedicalNeed Need,
    VanguardMedicalCapabilityRole Role,
    int Priority,
    string ActionKind,
    string Notes)
{
    public string Summary => "tpl=" + Safe(TemplateId)
        + ";name=" + Safe(Name)
        + ";need=" + Need
        + ";role=" + Role
        + ";priority=" + Priority.ToString("0")
        + ";action=" + Safe(ActionKind)
        + ";notes=" + Safe(Notes);

    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        return value.Trim()
            .Replace(' ', '_')
            .Replace(';', '_')
            .Replace('|', '_')
            .Replace('\r', '_')
            .Replace('\n', '_');
    }
}

internal static class VanguardMedicalItemCapabilityResolver
{
    public const string MatrixTag = "VANGUARD_MEDICAL_ITEM_CAPABILITY_MATRIX";
    public const string HeavyBleedMatrixMarker = "heavy_bleed_cat_esmarch_calokb_hemostatic";
    public const string LightBleedMatrixMarker = "light_bleed_bandage_army_bandage";
    public const string FractureMatrixMarker = "fracture_splint_aluminum_splint_grizzly";
    public const string PainMobilityMatrixMarker = "pain_mobility_morphine_analgin_ibuprofen_golden_star_vaseline_propital";
    public const string SurgeryMatrixMarker = "surgery_cms_surv12_only";
    public const string Surv12NotFractureMarker = "surv12_not_fracture";
    public const string CmsNotFractureMarker = "cms_not_fracture";

    private static readonly VanguardMedicalItemCapability[] Entries =
    {
        new("60098af40accd37ef2175f27", "CAT", VanguardMedicalNeed.HeavyBleed, VanguardMedicalCapabilityRole.Primary, 300, "firstAid.heavyBleed", HeavyBleedMatrixMarker),
        new("5e831507ea0a7c419c2f9bd9", "Esmarch", VanguardMedicalNeed.HeavyBleed, VanguardMedicalCapabilityRole.Primary, 290, "firstAid.heavyBleed", HeavyBleedMatrixMarker),
        new("5e8488fa988a8701445df1e4", "Calok-B / Hemostatic", VanguardMedicalNeed.HeavyBleed, VanguardMedicalCapabilityRole.Primary, 280, "firstAid.heavyBleed", HeavyBleedMatrixMarker),
        new("544fb45d4bdc2dee738b4568", "Salewa", VanguardMedicalNeed.HeavyBleed, VanguardMedicalCapabilityRole.Fallback, 150, "firstAid.heavyBleed", "medkit_compatible_fallback"),
        new("590c678286f77426c9660122", "IFAK", VanguardMedicalNeed.HeavyBleed, VanguardMedicalCapabilityRole.Fallback, 145, "firstAid.heavyBleed", "medkit_compatible_fallback"),
        new("60098ad7c2240c0fe85c570a", "AFAK", VanguardMedicalNeed.HeavyBleed, VanguardMedicalCapabilityRole.Fallback, 140, "firstAid.heavyBleed", "medkit_compatible_fallback"),
        new("590c657e86f77412b013051d", "Grizzly", VanguardMedicalNeed.HeavyBleed, VanguardMedicalCapabilityRole.Fallback, 135, "firstAid.heavyBleed", "medkit_compatible_fallback"),

        new("544fb25a4bdc2dfb738b4567", "Bandage", VanguardMedicalNeed.LightBleed, VanguardMedicalCapabilityRole.Primary, 300, "firstAid.lightBleed", LightBleedMatrixMarker),
        new("5751a25924597722c463c472", "Army Bandage", VanguardMedicalNeed.LightBleed, VanguardMedicalCapabilityRole.Primary, 290, "firstAid.lightBleed", LightBleedMatrixMarker),
        new("544fb45d4bdc2dee738b4568", "Salewa", VanguardMedicalNeed.LightBleed, VanguardMedicalCapabilityRole.Fallback, 150, "firstAid.lightBleed", "medkit_compatible_fallback"),
        new("590c678286f77426c9660122", "IFAK", VanguardMedicalNeed.LightBleed, VanguardMedicalCapabilityRole.Fallback, 145, "firstAid.lightBleed", "medkit_compatible_fallback"),
        new("60098ad7c2240c0fe85c570a", "AFAK", VanguardMedicalNeed.LightBleed, VanguardMedicalCapabilityRole.Fallback, 140, "firstAid.lightBleed", "medkit_compatible_fallback"),
        new("590c657e86f77412b013051d", "Grizzly", VanguardMedicalNeed.LightBleed, VanguardMedicalCapabilityRole.Fallback, 135, "firstAid.lightBleed", "medkit_compatible_fallback"),

        new("544fb3364bdc2d34748b456a", "Splint", VanguardMedicalNeed.Fracture, VanguardMedicalCapabilityRole.Primary, 300, "splint.fracture", FractureMatrixMarker),
        new("5af0454c86f7746bf20992e8", "Aluminum Splint", VanguardMedicalNeed.Fracture, VanguardMedicalCapabilityRole.Primary, 290, "splint.fracture", FractureMatrixMarker),
        new("590c657e86f77412b013051d", "Grizzly", VanguardMedicalNeed.Fracture, VanguardMedicalCapabilityRole.Fallback, 120, "firstAid.fractureFallback", FractureMatrixMarker),

        new("5755356824597772cb798962", "AI-2", VanguardMedicalNeed.HpHeal, VanguardMedicalCapabilityRole.Primary, 300, "firstAid.hp", "hp_heal_ai2_car_salewa_ifak_afak_grizzly"),
        new("590c661e86f7741e566b646a", "Car First Aid Kit", VanguardMedicalNeed.HpHeal, VanguardMedicalCapabilityRole.Primary, 295, "firstAid.hp", "hp_heal_ai2_car_salewa_ifak_afak_grizzly"),
        new("544fb45d4bdc2dee738b4568", "Salewa", VanguardMedicalNeed.HpHeal, VanguardMedicalCapabilityRole.Primary, 290, "firstAid.hp", "hp_heal_ai2_car_salewa_ifak_afak_grizzly"),
        new("590c678286f77426c9660122", "IFAK", VanguardMedicalNeed.HpHeal, VanguardMedicalCapabilityRole.Primary, 280, "firstAid.hp", "hp_heal_ai2_car_salewa_ifak_afak_grizzly"),
        new("60098ad7c2240c0fe85c570a", "AFAK", VanguardMedicalNeed.HpHeal, VanguardMedicalCapabilityRole.Primary, 270, "firstAid.hp", "hp_heal_ai2_car_salewa_ifak_afak_grizzly"),
        new("590c657e86f77412b013051d", "Grizzly", VanguardMedicalNeed.HpHeal, VanguardMedicalCapabilityRole.Primary, 260, "firstAid.hp", "hp_heal_ai2_car_salewa_ifak_afak_grizzly"),

        new("544fb3f34bdc2d03748b456a", "Morphine", VanguardMedicalNeed.PainMobility, VanguardMedicalCapabilityRole.Primary, 300, "stim.painMobility", PainMobilityMatrixMarker),
        new("544fb37f4bdc2dee738b4567", "Analgin", VanguardMedicalNeed.PainMobility, VanguardMedicalCapabilityRole.Primary, 290, "stim.painMobility", PainMobilityMatrixMarker),
        new("5af0548586f7743a532b7e99", "Ibuprofen", VanguardMedicalNeed.PainMobility, VanguardMedicalCapabilityRole.Primary, 280, "stim.painMobility", PainMobilityMatrixMarker),
        new("5751a89d24597722aa0e8db0", "Golden Star", VanguardMedicalNeed.PainMobility, VanguardMedicalCapabilityRole.Primary, 270, "stim.painMobility", PainMobilityMatrixMarker),
        new("5755383e24597772cb798966", "Vaseline", VanguardMedicalNeed.PainMobility, VanguardMedicalCapabilityRole.Primary, 260, "stim.painMobility", PainMobilityMatrixMarker),
        new("5c0e530286f7747fa1419862", "Propital", VanguardMedicalNeed.PainMobility, VanguardMedicalCapabilityRole.Utility, 250, "stim.regenMobility", "propital_regen_mobility"),

        new("5d02778e86f774203e7dedbe", "CMS", VanguardMedicalNeed.SurgeryDestroyedPart, VanguardMedicalCapabilityRole.Primary, 300, "surgery.destroyedPart", SurgeryMatrixMarker + ";" + CmsNotFractureMarker),
        new("5d02797c86f774203f38e30a", "Surv12", VanguardMedicalNeed.SurgeryDestroyedPart, VanguardMedicalCapabilityRole.Primary, 290, "surgery.destroyedPart", SurgeryMatrixMarker + ";" + Surv12NotFractureMarker),
    };

    public static IReadOnlyList<VanguardMedicalItemCapability> Catalog => Entries;

    public static IReadOnlyList<VanguardMedicalItemCapability> GetCandidates(VanguardMedicalNeed need)
    {
        if (need == VanguardMedicalNeed.None)
        {
            return Array.Empty<VanguardMedicalItemCapability>();
        }

        var effectiveNeed = need == VanguardMedicalNeed.BlackBroken ? VanguardMedicalNeed.SurgeryDestroyedPart : need;
        return Entries
            .Where(entry => entry.Need == effectiveNeed)
            .OrderByDescending(entry => entry.Priority)
            .ThenBy(entry => entry.Role)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool TryGetBestCandidate(VanguardMedicalNeed need, IEnumerable<string?> availableTemplateIds, out VanguardMedicalItemCapability capability)
    {
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var templateId in availableTemplateIds ?? Array.Empty<string>())
        {
            var normalized = NormalizeTemplateId(templateId);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                available.Add(normalized);
            }
        }

        foreach (var candidate in GetCandidates(need))
        {
            if (available.Contains(candidate.TemplateId))
            {
                capability = candidate;
                return true;
            }
        }

        capability = default!;
        return false;
    }

    public static bool IsKnownTemplate(string? templateId)
    {
        var normalized = NormalizeTemplateId(templateId);
        return !string.IsNullOrWhiteSpace(normalized)
            && Entries.Any(entry => string.Equals(entry.TemplateId, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeTemplateId(string? templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return string.Empty;
        }

        return templateId.Trim().ToLowerInvariant();
    }
}
#endif
