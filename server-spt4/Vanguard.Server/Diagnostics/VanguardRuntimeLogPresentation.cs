using System;
using System.Text.RegularExpressions;

// Responsibility: Produces bounded diagnostics/telemetry for Runtime Log Presentation in the server diagnostics.
// Flow: Runtime facts are normalized, deduplicated/rate-gated where needed, then emitted according to Vanguard presentation levels.
// Authority boundary: Observation only; telemetry never changes the gameplay decision it reports.
// Invariant: Operational output stays actionable and repetitive detail remains restricted to diagnostic/trace levels.
namespace Vanguard.Server.Diagnostics;

/// <summary>
/// Reader-friendly runtime presentation boundary. Active source emits functional event identifiers;
/// this layer only groups those identifiers into stable user-facing diagnostic families and cleans spacing.
/// It does not alter gameplay state or decide diagnostic severity.
/// </summary>
internal static class VanguardRuntimeLogPresentation
{
    private static readonly Regex EventRegex = new(@"VANGUARD_[A-Z0-9_]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmptyValueRegex = new(@"=\s*(?=;|$)", RegexOptions.Compiled);

    public static string NormalizeTag(string? tag)
    {
        string semanticTag = NormalizeEventIdentifier(tag);
        return string.IsNullOrWhiteSpace(semanticTag) ? "VANGUARD_DIAGNOSTIC" : SelectFamily(semanticTag);
    }

    public static string NormalizeMessage(string? tag, string? message)
    {
        string normalized = EventRegex.Replace(message ?? string.Empty, match => NormalizeEventIdentifier(match.Value));
        normalized = Cleanup(normalized);
        string semanticTag = NormalizeEventIdentifier(tag);
        string family = SelectFamily(semanticTag);
        if (!string.IsNullOrWhiteSpace(semanticTag)
            && !string.Equals(semanticTag, family, StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("event=", StringComparison.OrdinalIgnoreCase)
            && normalized.IndexOf(semanticTag, StringComparison.OrdinalIgnoreCase) < 0)
        {
            string eventName = semanticTag.StartsWith("VANGUARD_", StringComparison.OrdinalIgnoreCase)
                ? semanticTag.Substring("VANGUARD_".Length)
                : semanticTag;
            normalized = $"event={eventName}; {normalized}";
        }
        return normalized.Trim();
    }

    public static string PresentLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return string.Empty;
        string normalized = Cleanup(EventRegex.Replace(line, match => NormalizeEventIdentifier(match.Value)));
        Match bracket = Regex.Match(normalized, @"^\[(VANGUARD_[A-Z0-9_]+)\]\s*(.*)$", RegexOptions.IgnoreCase);
        if (!bracket.Success) return normalized.Trim();

        string semanticTag = NormalizeEventIdentifier(bracket.Groups[1].Value);
        string family = SelectFamily(semanticTag);
        string payload = bracket.Groups[2].Value.Trim();
        if (!string.Equals(semanticTag, family, StringComparison.OrdinalIgnoreCase)
            && payload.IndexOf(semanticTag, StringComparison.OrdinalIgnoreCase) < 0
            && !payload.StartsWith("event=", StringComparison.OrdinalIgnoreCase))
        {
            string eventName = semanticTag.StartsWith("VANGUARD_", StringComparison.OrdinalIgnoreCase)
                ? semanticTag.Substring("VANGUARD_".Length)
                : semanticTag;
            payload = $"event={eventName}; {payload}";
        }
        return $"[{family}] {payload}".TrimEnd();
    }

    private static string NormalizeEventIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string result = value.Trim().ToUpperInvariant();
        return result.StartsWith("VANGUARD_", StringComparison.Ordinal) ? Cleanup(result) : result;
    }

    private static string SelectFamily(string semanticTag)
    {
        string value = semanticTag.ToUpperInvariant();
        if (value.Contains("AUDIT_PROFILE")) return "VANGUARD_AUDIT_PROFILE";
        if (ContainsAny(value, "STARTUP", "BOOT_STATUS")) return "VANGUARD_STARTUP";
        if (value.Contains("TACTICAL_AUTHORING")) return "VANGUARD_TACTICAL_EDITOR";
        if (value.Contains("HUD")) return "VANGUARD_HUD";
        if (value.Contains("BILLING")) return "VANGUARD_BILLING";
        if (ContainsAny(value, "CAREER", "_XP_", "EXPERIENCE", "SKILL", "MASTERY", "MASTERING")) return "VANGUARD_CAREER";
        if (ContainsAny(value, "PERSIST", "POSTRAID", "POST_RAID", "RAID_HISTORY")) return "VANGUARD_PERSISTENCE";
        if (value.Contains("INVENTORY")) return "VANGUARD_INVENTORY";
        if (ContainsAny(value, "MEDICAL", "SURGERY", "HEAL", "BLEED", "FRACTURE")) return "VANGUARD_MEDICAL";
        if (ContainsAny(value, "LOOT", "CORPSE", "CONTAINER", "WISHLIST")) return "VANGUARD_LOOT";
        if (ContainsAny(value, "EXFIL", "EXTRACT")) return "VANGUARD_EXFIL";
        if (ContainsAny(value, "COOP", "FIKA", "HEADLESS")) return "VANGUARD_COOP";
        if (ContainsAny(value, "PERFORMANCE", "RUNTIME_COST", "STALL", "FRAME_BUDGET", "PROFILER")) return "VANGUARD_PERFORMANCE";
        if (ContainsAny(value, "F12", "CONFIG", "AUDIT_")) return "VANGUARD_CONFIG";
        if (ContainsAny(value, "SAIN", "MOREBOTS", "BOT_SETTINGS", "CUSTOMAI", "ORBIT", "INTEGRATION")) return "VANGUARD_INTEGRATION";
        if (ContainsAny(value, "COMBAT", "TARGET", "THREAT", "FRIENDLY_FIRE", "GRENADE", "AWARENESS", "SHOT", "FIRE_")) return "VANGUARD_COMBAT";
        if (ContainsAny(value, "TACTICAL", "MOVEMENT", "COHESION", "FORMATION", "TRAVEL", "INTERIOR", "STATIONARY", "RETURN", "EXECUTION")) return "VANGUARD_TACTICAL";
        if (ContainsAny(value, "OPERATOR", "SPAWN", "OWNER_BOUND", "RUNTIME_REGISTERED", "BOT_TYPES")) return "VANGUARD_OPERATORS";
        return value.StartsWith("VANGUARD_", StringComparison.Ordinal) ? value : "VANGUARD_DIAGNOSTIC";
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (string needle in needles)
            if (value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static string Cleanup(string value)
    {
        string result = value;
        for (int pass = 0; pass < 6; pass++)
        {
            string next = result
                .Replace("__", "_")
                .Replace("--", "-")
                .Replace("=_", "=")
                .Replace("=-", "=")
                .Replace("_;", ";")
                .Replace("-;", ";")
                .Replace(";;", ";")
                .Replace("; ;", ";");
            next = EmptyValueRegex.Replace(next, "=none");
            if (string.Equals(next, result, StringComparison.Ordinal)) break;
            result = next;
        }
        return result.Trim();
    }
}
