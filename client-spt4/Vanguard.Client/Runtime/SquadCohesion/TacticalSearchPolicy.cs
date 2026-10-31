#if SPT_CLIENT
using System;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Defines the single semantic contract for SAIN Search and GroupSearch states in which Tactical Coordination may borrow bounded translation authority.
// Flow: Normalized SAIN snapshot/action/decision evidence is classified once and reused by route development, spacing holds, choke support and the shared low-level translation gate.
// Authority boundary: This policy never moves a bot, mutates SAIN, changes a target or opens a scheduler window; it only distinguishes search-compatible movement context from an individual higher-priority action.
// Invariant: Search and GroupSearch are tactical movement contexts, not combat vetoes. Vanguard yields only when SAIN leaves that context, enters a self/survival action, becomes inactive, or exposes a concrete local movement conflict.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class TacticalSearchPolicy
{
    public static string EvaluateSnapshot(VanguardSainDecisionSnapshot sain)
    {
        if (sain == null || !sain.ComponentPresent)
        {
            return "sain_component_missing";
        }
        if (sain.Active != true)
        {
            return "sain_bot_not_active";
        }
        if (sain.LayersActive != true)
        {
            return "sain_layers_not_active";
        }
        if (!IsNoneToken(sain.SelfDecision))
        {
            return "sain_self_decision_active:" + Safe(sain.SelfDecision);
        }
        if (sain.RunningToCover == true)
        {
            return "sain_running_to_cover";
        }
        if (!IsSearchAction(sain.CurrentAction))
        {
            return "sain_action_not_search:" + Safe(sain.CurrentAction);
        }
        if (!IsSearchCombatDecision(sain.CombatDecision))
        {
            return "sain_combat_decision_not_search_compatible:" + Safe(sain.CombatDecision);
        }
        if (!IsSearchSquadDecision(sain.SquadDecision))
        {
            return "sain_squad_decision_not_search_compatible:" + Safe(sain.SquadDecision);
        }
        if (sain.Searching != true)
        {
            return "sain_search_state_not_active";
        }
        return string.Empty;
    }

    public static bool IsSearchAction(string? value)
    {
        string token = NormalizeToken(value);
        return string.Equals(token, "SearchAction", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "FollowSearchParty", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLeaderSearchAction(string? value)
        => string.Equals(NormalizeToken(value), "SearchAction", StringComparison.OrdinalIgnoreCase);

    public static bool IsSearchCombatDecision(string? value)
    {
        string token = NormalizeToken(value);
        return IsNoneToken(token)
            || string.Equals(token, "Search", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSearchSquadDecision(string? value)
    {
        string token = NormalizeToken(value);
        return IsNoneToken(token)
            || string.Equals(token, "Search", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "GroupSearch", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNoneToken(string? value)
    {
        string token = NormalizeToken(value);
        return token.Length == 0
            || string.Equals(token, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "null", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "nodecision", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        string trimmed = value.Trim();
        int separator = Math.Max(trimmed.LastIndexOf('.'), trimmed.LastIndexOf('+'));
        return separator >= 0 && separator + 1 < trimmed.Length
            ? trimmed[(separator + 1)..]
            : trimmed;
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
