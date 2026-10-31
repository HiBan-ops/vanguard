// Responsibility: Format persisted Career combatant identities into readable display names.
// Flow: Prefer the persisted name and fall back to side or raw-role labels when no name is provided.
// Authority boundary: Career persistence remains authoritative; presentation fallback does not classify combat evidence.
// Invariant: Fallback labels never convert an unverified raw role into a verified combatant classification.
namespace Vanguard.Server.Operators.Services;

/// <summary>
/// Shared reader-friendly presentation rules for combatants already persisted in the verified Career ledger.
/// This class never classifies new truth and never upgrades raw roles into authoritative combat semantics.
/// </summary>
public static class VanguardCareerCombatantPresentation
{
    public static string ResolveDisplayName(string? persistedName, string? side, string? rawRole)
        => Normalize(persistedName, DisplayFallback(side, rawRole));

    public static string DisplayFallback(string? side, string? rawRole)
    {
        string normalizedSide = Normalize(side);
        if (normalizedSide.Equals("Usec", StringComparison.OrdinalIgnoreCase)) return "PMC USEC";
        if (normalizedSide.Equals("Bear", StringComparison.OrdinalIgnoreCase)) return "PMC BEAR";

        string role = Normalize(rawRole);
        if (role.Equals("assault", StringComparison.OrdinalIgnoreCase)) return "Scav";
        if (role.Equals("marksman", StringComparison.OrdinalIgnoreCase)) return "Scav sniper";
        if (role.StartsWith("boss", StringComparison.OrdinalIgnoreCase)) return "Boss";
        if (role.StartsWith("follower", StringComparison.OrdinalIgnoreCase)) return "Garde de boss";
        return "Source non identifiée";
    }

    private static string Normalize(string? value, string fallback = "")
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
