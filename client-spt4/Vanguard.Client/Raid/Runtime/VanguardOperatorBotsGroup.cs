#if SPT_CLIENT
using System.Collections.Generic;
using EFT;
using Vanguard.Client.Raid.Interop;

// Responsibility: Associate an EFT BotsGroup for Vanguard Operators with its player owner.
// Flow: Construct the group, retain the owner profile ID, update ally/enemy relations through EFT reflection, and align the side.
// Authority boundary: EFT BotsGroup owns group relations; this subclass initializes the Operator-to-player association.
// Invariant: The owner identity is retained and the player is requested as an ally without assuming optional method signatures.
namespace Vanguard.Client.Raid.Runtime;

internal sealed class VanguardOperatorBotsGroup : BotsGroup
{
    public string PlayerOwnerProfileId { get; }

    public VanguardOperatorBotsGroup(
        BotZone zone,
        IBotGame botGame,
        BotOwner initialBot,
        List<BotOwner> enemies,
        DeadBodiesController deadBodiesController,
        List<Player> allPlayers,
        Player ownerPlayer)
        : base(zone, botGame, initialBot, enemies, deadBodiesController, allPlayers, false)
    {
        PlayerOwnerProfileId = ownerPlayer.ProfileId ?? string.Empty;
        // Keep the player owner inside the Operator group relation graph without
        // adding a compile-time dependency on optional EFT/Dissonance signatures.
        VanguardEftReflection.InvokeSingleArgumentMethod(this, "RemoveEnemy", ownerPlayer);
        VanguardEftReflection.InvokeSingleArgumentMethod(this, "AddAlly", ownerPlayer);
        Side = ownerPlayer.Side;
    }

}
#endif
