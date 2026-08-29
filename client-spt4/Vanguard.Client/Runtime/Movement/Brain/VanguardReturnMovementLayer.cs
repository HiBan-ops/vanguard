#if SPT_CLIENT
using System;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Combat;
using Action = DrakiaXYZ.BigBrain.Brains.CustomLayer.Action;
using ActionData = DrakiaXYZ.BigBrain.Brains.CustomLayer.ActionData;

// Responsibility: Provides Return Movement Layer support for the movement/cohesion runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Movement.Brain;

internal sealed class VanguardReturnMovementLayer : CustomLayer
{
    public const int LayerPriority = 97;

    public VanguardReturnMovementLayer(BotOwner botOwner, int priority)
        : base(botOwner, priority)
    {
    }

    public override string GetName() => nameof(VanguardReturnMovementLayer);

    public override bool IsActive()
    {
        if (BotOwner == null || BotOwner.IsDead || string.IsNullOrWhiteSpace(BotOwner.ProfileId))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        bool active = VanguardReturnMovementCommandStore.TryGetActive(BotOwner.ProfileId, now, out var command);
        if (active && VanguardReturnMovementCommandStore.IsGrenadeEmergencyRequest(command.RequestKind))
        {
            VanguardReturnMovementCommandStore.ReportLayerActive(BotOwner, command, "grenade_emergency_survival_override");
            return true;
        }

        if (VanguardSainSquadCombatAuthority.TryGetCachedAuthority(BotOwner.ProfileId, now, out var squadDecision, out var authorityReason))
        {
            string clearResult = VanguardReturnMovementCommandStore.Clear(BotOwner.ProfileId, "sain_squad_combat_authority");
            VanguardSainSquadCombatAuthority.ReportMovementYield(BotOwner.ProfileId, squadDecision, clearResult, authorityReason);
            return false;
        }

        if (active)
        {
            VanguardReturnMovementCommandStore.ReportLayerActive(BotOwner, command, "is_active");
        }

        return active;
    }

    public override Action GetNextAction()
    {
        if (!IsActive())
        {
            return CurrentAction;
        }

        if (CurrentAction != null && CurrentAction.Type == typeof(VanguardReturnMovementLogic))
        {
            return CurrentAction;
        }

        CurrentAction = new Action(typeof(VanguardReturnMovementLogic), "vanguard_hard_return_move_bridge", new ActionData());
        return CurrentAction;
    }

    public override bool IsCurrentActionEnding() => !IsActive();
}
#endif
