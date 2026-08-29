#if SPT_CLIENT
using EFT;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Integrations.MoreBots;

// Responsibility: Encodes the deterministic rules for Operator Loot Authority Policy within the external AI integration.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Runtime.Integrations.Looting;

/// <summary>
/// Vanguard boundary: LootingBots may remain installed and may still expose useful item valuation helpers,
/// but Vanguard Operators must not be handed to its autonomous scan driver by default.
/// Future loot will be executed as Vanguard-owned windows using explicit movement and completion checks.
/// </summary>
internal static class VanguardOperatorLootAuthorityPolicy
{
    public const string StatusTag = VanguardOperatorBotTypes.LootBoundaryStatusTag;

    public static bool ShouldAllowExternalForceScan(BotOwner botOwner, OperatorDecisionSnapshot snapshot, out string reason)
    {
        if (VanguardOperatorBotTypes.IsVanguardOperatorRole(botOwner))
        {
            reason = "external_force_scan_disabled:vanguard_operator_role";
            return false;
        }

        reason = "external_force_scan_disabled:operator_snapshot_scope";
        return false;
    }
}
#endif
