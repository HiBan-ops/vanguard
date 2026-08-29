#if SPT_CLIENT

// Responsibility: Encodes the deterministic rules for Raid Alliance Policy within the Operator allegiance runtime.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Runtime.Alliance;

/// <summary>
/// Central policy for raid allegiance. The runtime keeps the normal coop mode simple:
/// every player raid player and every Vanguard Operator are in the same protected camp.
/// Ownership and formation remain per-player; this policy only answers hostility.
/// A future optional mode can replace this policy with separated player squads.
/// </summary>
internal static class VanguardRaidAlliancePolicy
{
    public const string StatusTag = "VANGUARD_COOP_ALLIANCE_OWNERSHIP_GUARD_OK";
    public const string Mode = "CoopGlobalAllied";
    public const string DefaultAllianceId = "VanguardCoopDefault";
    public const string FutureIndependentSquadsMode = "IndependentPlayerSquads";

    public static bool ProtectAllPlayerSquadsByDefault => true;
}
#else
namespace Vanguard.Client.Runtime.Alliance;

internal static class VanguardRaidAlliancePolicy
{
}
#endif
