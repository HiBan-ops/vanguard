#if SPT_CLIENT
using System.Collections.Generic;
using EFT;
using Vanguard.Client.Raid.Interop;

// Responsibility: Provides Operator Bots Group support for the raid-runtime state.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
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
