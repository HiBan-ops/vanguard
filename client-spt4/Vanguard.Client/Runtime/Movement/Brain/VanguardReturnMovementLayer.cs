#if SPT_CLIENT
using System;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Combat;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.SquadCohesion;
using Vanguard.Client.Runtime.Movement.Diagnostics;
using Action = DrakiaXYZ.BigBrain.Brains.CustomLayer.Action;
using ActionData = DrakiaXYZ.BigBrain.Brains.CustomLayer.ActionData;

// Responsibility: Expose active Vanguard return-movement commands as an Operator-only BigBrain layer.
// Flow: Check live command and context vetoes in IsActive, then select VanguardReturnMovementLogic through GetNextAction.
// Authority boundary: SAIN squad combat and tactical context checks retain their veto authority over ordinary return commands.
// Invariant: Missing or vetoed commands do not activate the layer; emergency survival overrides retain their distinct path.
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
            VanguardTemporalSchedulingProbeService.ObserveBigBrainLayerIsActive(BotOwner, true, command, "grenade_emergency_survival_override");
            VanguardReturnMovementCommandStore.ReportLayerActive(BotOwner, command, "grenade_emergency_survival_override");
            return true;
        }

        bool compatibleTcSainPath = false;
        string compatibleTcContext = "none";
        if (active && AltDevelopAuthorityPolicy.IsAltDevelopRequest(command.RequestKind))
        {
            if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(BotOwner.ProfileId, out var snapshot))
            {
                VanguardTemporalSchedulingProbeService.ObserveBigBrainLayerIsActive(BotOwner, false, command, "tc_alt_develop_snapshot_missing");
                VanguardReturnMovementCommandStore.Clear(BotOwner.ProfileId, "tc_alt_develop_snapshot_missing");
                return false;
            }

            if (!VanguardMainIntentScheduler.TryGetActiveTacticalCoordinatorAltDevelopThreatId(
                    BotOwner.ProfileId,
                    now,
                    out var expectedThreatId,
                    out var threatIdentityReason))
            {
                string vetoReason = $"tc_alt_develop_primary_context_veto:{threatIdentityReason}";
                VanguardTemporalSchedulingProbeService.ObserveBigBrainLayerIsActive(BotOwner, false, command, vetoReason);
                VanguardReturnMovementCommandStore.Clear(BotOwner.ProfileId, vetoReason);
                return false;
            }

            if (!AltDevelopAuthorityPolicy.IsAltDevelopTranslationCompatible(snapshot, expectedThreatId, out var compatibilityReason))
            {
                string vetoReason = $"tc_alt_develop_local_authority_veto:{compatibilityReason}";
                VanguardTemporalSchedulingProbeService.ObserveBigBrainLayerIsActive(BotOwner, false, command, vetoReason);
                VanguardReturnMovementCommandStore.Clear(BotOwner.ProfileId, vetoReason);
                return false;
            }

            compatibleTcSainPath = true;
            compatibleTcContext = "tc_alt_develop_compatible_search_or_opportunity_combat_context";
        }

        if (active && DirectCommitAuthorityPolicy.IsDirectCommitRequest(command.RequestKind))
        {
            if (!VanguardMainIntentScheduler.TryGetActiveTacticalCoordinatorDirectCommitContext(
                    BotOwner.ProfileId,
                    now,
                    out _,
                    out _,
                    out var contextReason))
            {
                string vetoReason = $"tc_direct_commit_primary_context_veto:{contextReason}";
                VanguardTemporalSchedulingProbeService.ObserveBigBrainLayerIsActive(BotOwner, false, command, vetoReason);
                VanguardReturnMovementCommandStore.Clear(BotOwner.ProfileId, vetoReason);
                return false;
            }
            if (!DirectCommitMovementService.IsLayerContextCurrent(
                    BotOwner.ProfileId,
                    command,
                    now,
                    out var compatibilityReason))
            {
                string vetoReason = $"tc_direct_commit_local_authority_veto:{compatibilityReason}";
                VanguardTemporalSchedulingProbeService.ObserveBigBrainLayerIsActive(BotOwner, false, command, vetoReason);
                VanguardReturnMovementCommandStore.Clear(BotOwner.ProfileId, vetoReason);
                return false;
            }
            compatibleTcSainPath = true;
            compatibleTcContext = "tc_direct_commit_compatible_search_or_bounded_combat_advance_context";
        }

        if (active && TacticalOpportunityMovementService.IsOpportunityRequest(command.RequestKind))
        {
            if (!TacticalOpportunityMovementService.IsLayerContextCurrent(BotOwner.ProfileId, command, now, out string opportunityReason))
            {
                string vetoReason = "tc_tactical_opportunity_context_veto:" + opportunityReason;
                VanguardTemporalSchedulingProbeService.ObserveBigBrainLayerIsActive(BotOwner, false, command, vetoReason);
                VanguardReturnMovementCommandStore.Clear(BotOwner.ProfileId, vetoReason);
                return false;
            }
            compatibleTcSainPath = true;
            compatibleTcContext = "tc_tactical_opportunity_compatible_combat_sidecar";
        }

        if (VanguardSainSquadCombatAuthority.TryGetCachedAuthority(BotOwner.ProfileId, now, out var squadDecision, out var authorityReason))
        {
            if (!compatibleTcSainPath)
            {
                VanguardTemporalSchedulingProbeService.ObserveBigBrainLayerIsActive(BotOwner, false, active ? command : VanguardReturnMovementCommand.Empty, "sain_squad_combat_authority");
                string clearResult = VanguardReturnMovementCommandStore.Clear(BotOwner.ProfileId, "sain_squad_combat_authority");
                VanguardSainSquadCombatAuthority.ReportMovementYield(BotOwner.ProfileId, squadDecision, clearResult, authorityReason);
                return false;
            }

            VanguardTemporalSchedulingProbeService.ObserveBigBrainLayerIsActive(BotOwner, true, command, compatibleTcContext);
            VanguardReturnMovementCommandStore.ReportLayerActive(BotOwner, command, compatibleTcContext);
            return true;
        }

        VanguardTemporalSchedulingProbeService.ObserveBigBrainLayerIsActive(BotOwner, active, active ? command : VanguardReturnMovementCommand.Empty, active ? "is_active" : "no_active_vanguard_command");
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

        VanguardTemporalSchedulingProbeService.ObserveBigBrainActionSelected(BotOwner, "vanguard_hard_return_move_bridge");
        CurrentAction = new Action(typeof(VanguardReturnMovementLogic), "vanguard_hard_return_move_bridge", new ActionData());
        return CurrentAction;
    }

    public override bool IsCurrentActionEnding() => !IsActive();
}
#endif
