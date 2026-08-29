#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EFT;

// Responsibility: Encodes the deterministic rules for Raid Operator Hud Visibility Policy within the raid Operator HUD.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Raid.Hud;

internal enum VanguardRaidOperatorHudVisibilityMode
{
    AllFriendlyPlayers = 0,
    OwnerOnly = 1,
    AlliedOnly = 2,
}

/// <summary>
/// Keeps the current coop assumption explicit. The HUD currently treats every player as friendly,
/// while leaving the future owner/alliance restriction in one replaceable policy point.
/// </summary>
internal static class VanguardRaidOperatorHudVisibilityPolicy
{
    public static VanguardRaidOperatorHudVisibilityMode CurrentMode => VanguardRaidOperatorHudVisibilityMode.AllFriendlyPlayers;

    public static bool ShouldRejectPlayerOrLocal(IPlayer player, string localProfileId, ISet<string> playerProfileIds)
    {
        string profileId = player.ProfileId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return true;
        }

        if (string.Equals(profileId, localProfileId, StringComparison.Ordinal))
        {
            return true;
        }

        return playerProfileIds.Contains(profileId);
    }

    public static bool AllowsOperator(string ownerProfileId, string localProfileId, ISet<string> playerProfileIds)
    {
        if (CurrentMode == VanguardRaidOperatorHudVisibilityMode.AllFriendlyPlayers)
        {
            return true;
        }

        if (CurrentMode == VanguardRaidOperatorHudVisibilityMode.OwnerOnly)
        {
            return string.Equals(ownerProfileId, localProfileId, StringComparison.OrdinalIgnoreCase);
        }

        return playerProfileIds.Contains(ownerProfileId);
    }
}
#else
namespace Vanguard.Client.Raid.Hud;

internal enum VanguardRaidOperatorHudVisibilityMode
{
    AllFriendlyPlayers = 0,
    OwnerOnly = 1,
    AlliedOnly = 2,
}

internal static class VanguardRaidOperatorHudVisibilityPolicy
{
}
#endif
