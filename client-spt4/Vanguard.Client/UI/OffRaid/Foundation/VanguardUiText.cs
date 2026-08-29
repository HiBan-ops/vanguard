using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vanguard.Client.UI.OffRaid.Localization;

// Responsibility: Centralizes defensive text formatting helpers shared by the Off-Raid Operator UI.
// Flow: Raw nullable API values pass through normalization, fallback, number/date/status and display helpers before panels render them to the player.
// Authority boundary: Formatting only; these helpers never alter the canonical Operator state or infer missing persistent facts.
// Invariant: Equivalent inputs produce stable player-facing text and unknown/missing values remain explicit instead of being fabricated.
namespace Vanguard.Client.UI.OffRaid.Foundation;

internal static class VanguardUiText
{
    public static string Safe(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    public static string Faction(string? value)
    {
        string normalized = Safe(value, "PMC").ToUpperInvariant();
        return normalized switch
        {
            "USEC" => "USEC",
            "BEAR" => "BEAR",
            "PMC" => "PMC",
            "VANGUARD" => "Vanguard",
            _ => FormatTokenForDisplay(normalized)
        };
    }

    public static string Role(string? role, string? specialty = null)
    {
        string safeRole = RoleToken(role);
        string safeSpecialty = RoleToken(specialty);
        return string.IsNullOrWhiteSpace(safeSpecialty) || string.Equals(safeRole, safeSpecialty, StringComparison.OrdinalIgnoreCase)
            ? safeRole
            : $"{safeRole} / {safeSpecialty}";
    }

    public static string RoleToken(string? value)
    {
        string safe = Safe(value, "operator").Replace('-', '_').Replace(' ', '_').ToLowerInvariant();
        return VanguardOperatorsLocalizationService.GetOrDefault("role." + safe, FormatTokenForDisplay(safe));
    }

    public static string Value(params string?[] values)
    {
        string raw = Safe(values).Trim();
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        string normalized = raw.Replace('-', '_').Replace(' ', '_').ToLowerInvariant();
        return VanguardOperatorsLocalizationService.GetOrDefault("value." + normalized, FormatTokenForDisplay(raw));
    }

    public static string Range(string? value)
    {
        string normalized = Safe(value, string.Empty).Replace('-', '_').Replace(' ', '_').ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return L("general.undefined_fem");
        }

        return VanguardOperatorsLocalizationService.GetOrDefault("range." + normalized, Value(value, L("general.undefined_fem")));
    }

    public static string SquadRole(string? value)
    {
        string normalized = Safe(value, string.Empty).Replace('-', '_').Replace(' ', '_').ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return L("general.undefined");
        }

        return VanguardOperatorsLocalizationService.GetOrDefault("role." + normalized, Value(value, L("general.undefined")));
    }

    public static string Traits(IEnumerable<string>? traits)
    {
        List<string> values = traits?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(5)
            .Select(value => Value(value))
            .ToList() ?? new List<string>();
        return values.Count == 0 ? L("general.none") : string.Join(", ", values);
    }

    public static string Money(int amount)
    {
        return amount <= 0 ? "0 ₽" : $"{amount.ToString("N0", CultureInfo.InvariantCulture)} ₽";
    }

    public static string HealthPercent(double ratio)
    {
        int value = Math.Max(0, Math.Min(100, (int)Math.Round(ratio * 100.0)));
        return $"{value}%";
    }

    public static string FormatTokenForDisplay(string value)
    {
        string text = value.Replace('_', ' ').Replace('-', ' ').Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
    }

    private static string L(string key) => VanguardOperatorsLocalizationService.Get(key);
}
