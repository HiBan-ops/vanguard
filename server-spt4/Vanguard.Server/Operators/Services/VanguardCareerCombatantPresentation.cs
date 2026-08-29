// Responsibility: Provides Career Combatant Presentation support for the Operator domain services.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
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
