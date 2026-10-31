#if SPT_CLIENT
using System;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.Grenades;
using Vanguard.Client.Runtime.Movement.Diagnostics;
using UnityEngine;
using UnityEngine.AI;

// Responsibility: Implements the BigBrain-facing movement logic used when Vanguard has explicitly authorized an Operator return/cohesion movement action.
// Flow: The brain logic consumes the active Vanguard return plan/target, drives compatible EFT movement primitives while the lease remains valid, reports progress/failure and yields when the plan or authority is revoked.
// Authority boundary: It is a physical movement adapter only; the movement doctrine/scheduler decides whether return authority exists and SAIN combat can preempt according to the shared rules.
// Invariant: No stale return target survives lease cancellation, movement stops/yields cleanly on higher-priority authority, and failures feed recovery rather than infinite retries.
namespace Vanguard.Client.Runtime.Movement.Brain;

/// <summary>
/// BigBrain action that owns the movement backend during a Vanguard movement lease. Terminal
/// movements keep the vanilla GoToSomePointData bridge. TravelCohesionFollowThrough uses a
/// continuous direct GoToPoint path with slowAtTheEnd=false. TacticalCoordinatorAltDevelop and TacticalCoordinatorDirectCommit are
/// different: BigBrain retains the exact Vanguard command lease, but physical path execution is
/// delegated to SAIN BotPathDataManual through the TC Search translation bridge.
/// </summary>
internal sealed class VanguardReturnMovementLogic : CustomLogic
{
    private long activeGeneration;
    private Vector3 activeAnchor;
    private bool activeContinuousTravel;
    private bool activeTacticalCoordinatorAltDevelop;
    private bool activeTacticalCoordinatorDirectCommit;
    private bool activeTacticalCoordinatorOpportunity;
    private bool activeMedicSupportCombatRendezvous;
    private DateTimeOffset lastSetPointAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastPathCheckAtUtc = DateTimeOffset.MinValue;
    private bool activeTacticalAuthoringHold;
    private bool activeTacticalCoordinatorAntiCongaHold;

    private const string TacticalAuthoringPreviewRequestKind = "TacticalAuthoringLivePreview";
    private const float TacticalAuthoringHoldEnterPaddingMeters = 0.25f;
    private const float TacticalAuthoringHoldExitPaddingMeters = 1.05f;

    public VanguardReturnMovementLogic(BotOwner botOwner)
        : base(botOwner)
    {
    }

    public override void Start()
    {
        ClearLocalState();
        VanguardTemporalSchedulingProbeService.ObserveBigBrainActionStart(BotOwner);
    }

    public override void Stop()
    {
        try
        {
            // Runtime invariant: the grenade service owns the physical lease independently of the current BigBrain
            // action. A layer handoff must never stop a still-live emergency path, otherwise the
            // service would reapply it on the next tick and reproduce the measured stop/restart loop.
            BotOwner? botOwner = BotOwner;
            VanguardReturnMovementCommand active = VanguardReturnMovementCommand.Empty;
            bool preserveEmergencyPhysicalLease = botOwner != null
                && !string.IsNullOrWhiteSpace(botOwner.ProfileId)
                && VanguardReturnMovementCommandStore.TryGetActive(botOwner.ProfileId, DateTimeOffset.UtcNow, out active)
                && VanguardReturnMovementCommandStore.IsGrenadeEmergencyRequest(active.RequestKind);
            if (!preserveEmergencyPhysicalLease)
            {
                // An authored stationary hold has already quiesced the mover. Replaying the terminal
                // GoToSomePoint backend during a BigBrain handoff would resurrect the old authored
                // target exactly when SAIN/medical/grenade is becoming sovereign. Other movement
                // requests use the standard reset behavior.
                if (!activeTacticalAuthoringHold && !activeTacticalCoordinatorAltDevelop && !activeTacticalCoordinatorDirectCommit && !activeTacticalCoordinatorOpportunity && !activeMedicSupportCombatRendezvous)
                {
                    ResetMovementBackend(activeContinuousTravel);
                }
            }
            else if (botOwner != null)
            {
                VanguardReturnMovementCommandStore.ReportLogicDrive(
                    botOwner,
                    active,
                    "grenade_emergency_bigbrain_stop_suppressed",
                    "physical_lease_owned_by_emergency_service");
            }
            VanguardTemporalSchedulingProbeService.ObserveBigBrainActionStop(botOwner, active);
            ClearLocalState();
        }
        catch
        {
            // Stop must never break the bot brain.
        }
    }

    private void ClearLocalState()
    {
        activeGeneration = 0L;
        activeAnchor = Vector3.zero;
        activeContinuousTravel = false;
        activeTacticalCoordinatorAltDevelop = false;
        activeTacticalCoordinatorDirectCommit = false;
        activeTacticalCoordinatorOpportunity = false;
        activeMedicSupportCombatRendezvous = false;
        lastSetPointAtUtc = DateTimeOffset.MinValue;
        lastPathCheckAtUtc = DateTimeOffset.MinValue;
        activeTacticalAuthoringHold = false;
        activeTacticalCoordinatorAntiCongaHold = false;
    }

    private void EnsureVanillaMoverOwnershipReady(VanguardReturnMovementCommand command, string phase)
    {
        // SAIN manual movement deliberately uses BotMover.Pause as a mutex while it
        // drives Player.Move directly. A Vanguard command that takes ownership of the vanilla
        // BotMover must normalize that mutex exactly once at command-generation entry. Do not
        // Stop/restart the backend here: this is ownership normalization, not recovery churn.
        if (BotOwner?.Mover == null)
        {
            return;
        }

        bool wasPaused = BotOwner.Mover.Pause;
        BotOwner.Mover.MovementResume();
        VanguardReturnMovementCommandStore.ReportLogicDrive(
            BotOwner,
            command,
            "vanilla_mover_ownership_normalized",
            $"phase={phase}:wasPaused={wasPaused}:pauseNow={BotOwner.Mover.Pause}:backendRestart=false");
    }

    private void ResetMovementBackend(bool stopContinuousPath = false)
    {
        if (stopContinuousPath)
        {
            // Direct Travel commands bypass GoToSomePointData, so their authority handoff or
            // terminal cleanup must stop the mover explicitly. A later activation of the same owned
            // command reissues its path without changing lease/generation identity.
            BotOwner?.Mover?.Stop();
        }
        else
        {
            BotOwner?.GoToSomePointData?.UpdateToGo(false);
        }

        BotOwner?.Sprint(false, true);
        BotOwner?.Mover?.Sprint(false, false);
    }

    public override void Update(CustomLayer.ActionData data)
    {
        if (BotOwner == null || BotOwner.IsDead || string.IsNullOrWhiteSpace(BotOwner.ProfileId))
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!VanguardReturnMovementCommandStore.TryGetActive(BotOwner.ProfileId, now, out var command))
        {
            if (activeGeneration != 0L)
            {
                if (!activeTacticalAuthoringHold && !activeTacticalCoordinatorAltDevelop && !activeTacticalCoordinatorDirectCommit && !activeTacticalCoordinatorOpportunity && !activeMedicSupportCombatRendezvous)
                {
                    ResetMovementBackend(activeContinuousTravel);
                }
                // AltDevelop path lifetime is owned by SearchTranslationGateBridge. The TC lease
                // terminal releases/disposes that exact SAIN path; this BigBrain adapter must not
                // stop the vanilla mover and accidentally interfere with SAIN's fallback path.
                ClearLocalState();
            }
            return;
        }

        VanguardTemporalSchedulingProbeService.ObserveMovementCommandConsume(BotOwner, command);

        if (activeTacticalAuthoringHold && !IsTacticalAuthoringPreview(command))
        {
            // Command ownership changed domains. The stationary-hold exception must never leak
            // into grenade/medical/cohesion/other Vanguard movement semantics.
            activeTacticalAuthoringHold = false;
        }
        if (activeTacticalCoordinatorAntiCongaHold && !IsTacticalCoordinatorAntiCongaHold(command))
        {
            activeTacticalCoordinatorAntiCongaHold = false;
        }
        if (activeTacticalCoordinatorAltDevelop
            && !VanguardContinuousCohesionLocomotionPolicy.IsTacticalCoordinatorAltDevelopRequest(command.RequestKind))
        {
            activeTacticalCoordinatorAltDevelop = false;
        }
        if (activeTacticalCoordinatorDirectCommit
            && !VanguardContinuousCohesionLocomotionPolicy.IsTacticalCoordinatorDirectCommitRequest(command.RequestKind))
        {
            activeTacticalCoordinatorDirectCommit = false;
        }
        if (activeTacticalCoordinatorOpportunity
            && !VanguardContinuousCohesionLocomotionPolicy.IsTacticalCoordinatorOpportunityRequest(command.RequestKind))
        {
            activeTacticalCoordinatorOpportunity = false;
        }
        if (activeMedicSupportCombatRendezvous
            && !VanguardContinuousCohesionLocomotionPolicy.IsMedicSupportSainPathRequest(command.RequestKind))
        {
            activeMedicSupportCombatRendezvous = false;
        }

        if (VanguardReturnMovementCommandStore.IsGrenadeEmergencyRequest(command.RequestKind))
        {
            DriveEmergencyGrenadeMovement(command, now);
            return;
        }

        if (IsTacticalCoordinatorAntiCongaHold(command))
        {
            DriveTacticalCoordinatorAntiCongaStationaryHold(command, now);
            return;
        }

        if (VanguardContinuousCohesionLocomotionPolicy.IsTacticalCoordinatorAltDevelopRequest(command.RequestKind))
        {
            DriveTacticalCoordinatorAltDevelopSainPathLease(command, now);
            return;
        }
        if (VanguardContinuousCohesionLocomotionPolicy.IsTacticalCoordinatorDirectCommitRequest(command.RequestKind))
        {
            DriveTacticalCoordinatorAltDevelopSainPathLease(command, now);
            return;
        }
        if (VanguardContinuousCohesionLocomotionPolicy.IsTacticalCoordinatorOpportunityRequest(command.RequestKind))
        {
            DriveTacticalCoordinatorAltDevelopSainPathLease(command, now);
            return;
        }
        if (VanguardContinuousCohesionLocomotionPolicy.IsMedicSupportSainPathRequest(command.RequestKind))
        {
            DriveTacticalCoordinatorAltDevelopSainPathLease(command, now);
            return;
        }

        bool continuousTravel = VanguardContinuousCohesionLocomotionPolicy.IsContinuousTravelRequest(command.RequestKind);
        if (continuousTravel)
        {
            DriveContinuousTravel(command, now);
            return;
        }

        DriveTerminalMovement(command, now);
    }

    private void DriveEmergencyGrenadeMovement(VanguardReturnMovementCommand command, DateTimeOffset now)
    {
        if (!VanguardGrenadeEmergencyPhysicalDriver.Drive(BotOwner, command, now, "bigbrain_layer", out string result))
        {
            VanguardReturnMovementCommandStore.ReportLogicDrive(BotOwner, command, "grenade_emergency_physical_drive_rejected", result);
            return;
        }

        activeGeneration = command.Generation;
        activeAnchor = command.Anchor;
        activeContinuousTravel = true;
        activeTacticalCoordinatorAltDevelop = false;
        activeTacticalCoordinatorDirectCommit = false;
        activeTacticalCoordinatorOpportunity = false;
        activeMedicSupportCombatRendezvous = false;
        lastSetPointAtUtc = now;
        VanguardReturnMovementCommandStore.ReportLogicDrive(BotOwner, command, "grenade_emergency_physical_lease", result);
    }

    private void DriveTacticalCoordinatorAltDevelopSainPathLease(VanguardReturnMovementCommand command, DateTimeOffset now)
    {
        bool directCommit = VanguardContinuousCohesionLocomotionPolicy.IsTacticalCoordinatorDirectCommitRequest(command.RequestKind);
        bool tacticalOpportunity = VanguardContinuousCohesionLocomotionPolicy.IsTacticalCoordinatorOpportunityRequest(command.RequestKind);
        bool medicSupport = VanguardContinuousCohesionLocomotionPolicy.IsMedicSupportSainPathRequest(command.RequestKind);
        bool requestAlreadyActive = medicSupport
            ? activeMedicSupportCombatRendezvous
            : tacticalOpportunity
                ? activeTacticalCoordinatorOpportunity
                : directCommit ? activeTacticalCoordinatorDirectCommit : activeTacticalCoordinatorAltDevelop;
        bool newCommand = command.Generation != activeGeneration || !requestAlreadyActive;
        bool anchorChanged = (command.Anchor - activeAnchor).sqrMagnitude > 0.25f;

        // Deliberately do not call BotOwner.GoToPoint, GoToSomePointData.SetPoint, Mover.Stop,
        // Sprint or SetTargetMoveSpeed here. SAIN disables the vanilla combat-mover fixed
        // update and owns physical combat translation through BotPathDataManual. TC owns the exact
        // destination/path lease; SearchTranslationGateBridge binds that destination into SAIN's
        // active manual path and lets SAIN TickPath execute it.
        activeGeneration = command.Generation;
        activeAnchor = command.Anchor;
        activeContinuousTravel = false;
        activeTacticalCoordinatorAltDevelop = !directCommit && !tacticalOpportunity && !medicSupport;
        activeTacticalCoordinatorDirectCommit = directCommit;
        activeTacticalCoordinatorOpportunity = tacticalOpportunity;
        activeMedicSupportCombatRendezvous = medicSupport;
        lastSetPointAtUtc = now;

        if (newCommand || anchorChanged)
        {
            VanguardReturnMovementCommandStore.ReportLogicDrive(
                BotOwner,
                command,
                medicSupport ? "medic_support_combat_rendezvous_sain_path_authority"
                    : tacticalOpportunity ? "tc_tactical_opportunity_sain_path_authority"
                    : directCommit ? "tc_direct_commit_sain_path_authority" : "tc_alt_develop_sain_path_authority",
                (newCommand ? "new_command" : "anchor_observed")
                    + (medicSupport ? ":destination_owned_by_medic_support" : ":destination_owned_by_tc")
                    + ":physical_tick_owned_by_sain:vanilla_go_to_point=false");
        }
    }

    private void DriveContinuousTravel(VanguardReturnMovementCommand command, DateTimeOffset now)
    {
        bool newCommand = command.Generation != activeGeneration || !activeContinuousTravel;
        bool anchorChanged = (command.Anchor - activeAnchor).sqrMagnitude > 0.25f;
        float reachDistance = VanguardContinuousCohesionLocomotionPolicy.ResolveTravelReachDistance(command.AnchorRadiusMeters);
        Vector3 planarAnchorDelta = command.Anchor - BotOwner.Position;
        planarAnchorDelta.y = 0f;
        bool anchorConsumed = planarAnchorDelta.sqrMagnitude <= (reachDistance + 0.50f) * (reachDistance + 0.50f);
        bool missingPath = BotOwner.Mover?.HasPathAndNoComplete != true;
        bool missingPathReissueReady = missingPath
            && !anchorConsumed
            && now - lastSetPointAtUtc >= VanguardContinuousCohesionLocomotionPolicy.MissingPathReissueCooldown;
        bool refreshPath = newCommand || anchorChanged || missingPathReissueReady;

        if (refreshPath)
        {
            // A valid corridor command never consumes ResetBackendBeforeSetPoint. Backend resets
            // were the measured stop/restart source; a same-lease route update is a non-terminal
            // continuation and is applied directly with slowAtTheEnd=false.
            if (command.ResetBackendBeforeSetPoint)
            {
                VanguardReturnMovementCommandStore.ReportLogicDrive(
                    BotOwner,
                    command,
                    "continuous_backend_reset_suppressed",
                    "same_travel_lease_never_resets_backend");
            }

            if (newCommand)
            {
                EnsureVanillaMoverOwnershipReady(command, "continuous_travel_new_command");
            }

            NavMeshPathStatus status = BotOwner.GoToPoint(
                command.Anchor,
                false,
                reachDistance,
                false,
                false);

            activeGeneration = command.Generation;
            activeAnchor = command.Anchor;
            activeContinuousTravel = true;
            activeTacticalCoordinatorAltDevelop = false;
            activeTacticalCoordinatorDirectCommit = false;
            activeTacticalCoordinatorOpportunity = false;
            lastSetPointAtUtc = now;
            VanguardReturnMovementCommandStore.ReportLogicDrive(
                BotOwner,
                command,
                "continuous_go_to_point",
                (newCommand ? "new_command" : anchorChanged ? "route_advanced" : "missing_path_reissue")
                    + ":slowAtEnd=false:status=" + status);
        }

        ApplyMotionState(command);
        VanguardReturnMovementCommandStore.ReportLogicDrive(
            BotOwner,
            command,
            "continuous_motion",
            command.Sprint ? "sprint_no_terminal_brake" : "run_no_terminal_brake");
    }

    private void DriveTerminalMovement(VanguardReturnMovementCommand command, DateTimeOffset now)
    {
        activeTacticalCoordinatorAltDevelop = false;
        activeTacticalCoordinatorDirectCommit = false;
        activeTacticalCoordinatorOpportunity = false;
        activeMedicSupportCombatRendezvous = false;
        if (IsTacticalAuthoringPreview(command))
        {
            float anchorDistance = HorizontalDistance(BotOwner.Position, command.Anchor);
            float enterRadius = Math.Max(0.25f, command.AnchorRadiusMeters + TacticalAuthoringHoldEnterPaddingMeters);
            float exitRadius = Math.Max(enterRadius, command.AnchorRadiusMeters + TacticalAuthoringHoldExitPaddingMeters);
            bool commandChangedWhileHolding = activeTacticalAuthoringHold
                && (command.Generation != activeGeneration || (command.Anchor - activeAnchor).sqrMagnitude > 0.25f);
            if (activeTacticalAuthoringHold)
            {
                if (!commandChangedWhileHolding && anchorDistance <= exitRadius)
                {
                    DriveTacticalAuthoringStationaryHold(command, now, anchorDistance, entering: false);
                    return;
                }
                activeTacticalAuthoringHold = false;
                VanguardReturnMovementCommandStore.ReportLogicDrive(
                    BotOwner,
                    command,
                    "tactical_authoring_hold_exit",
                    $"distance={anchorDistance:0.00}:exitRadius={exitRadius:0.00}:commandChanged={commandChangedWhileHolding}:action=reapproach");
            }
            if (anchorDistance <= enterRadius)
            {
                activeTacticalAuthoringHold = true;
                DriveTacticalAuthoringStationaryHold(command, now, anchorDistance, entering: true);
                return;
            }
        }

        bool newCommand = command.Generation != activeGeneration || activeContinuousTravel;
        bool anchorChanged = (command.Anchor - activeAnchor).sqrMagnitude > 0.25f;
        bool missingTarget = BotOwner.GoToSomePointData?.HaveTarget() != true;
        bool refreshSetPoint = newCommand || anchorChanged || missingTarget;

        if (refreshSetPoint)
        {
            if (newCommand)
            {
                EnsureVanillaMoverOwnershipReady(command, "terminal_movement_new_command");
            }

            if (newCommand && command.ResetBackendBeforeSetPoint)
            {
                ResetMovementBackend();
                VanguardReturnMovementCommandStore.ReportLogicDrive(BotOwner, command, "backend_reset", "terminal_physical_recovery_generation");
            }

            BotOwner.GoToSomePointData?.SetPoint(command.Anchor);
            activeGeneration = command.Generation;
            activeAnchor = command.Anchor;
            activeContinuousTravel = false;
            lastSetPointAtUtc = now;
            VanguardReturnMovementCommandStore.ReportLogicDrive(BotOwner, command, "set_point", newCommand ? "issued_new_terminal_command" : anchorChanged ? "issued_terminal_reanchor" : "issued_missing_target");
        }

        if (now - lastPathCheckAtUtc > TimeSpan.FromSeconds(1.50d))
        {
            lastPathCheckAtUtc = now;
            if (!VanguardReturnMovementCommandStore.ValidatePathStillComplete(BotOwner, command.Anchor, out var pathSummary))
            {
                VanguardReturnMovementCommandStore.ReportLogicDrive(BotOwner, command, "path_monitor", "not_complete:" + pathSummary);
                bool commandReleased = VanguardReturnMovementCommandStore.ReportPathInvalid(command, now, pathSummary);
                VanguardReturnMovementCommandStore.ReportLogicDrive(BotOwner, command, "path_monitor_release", commandReleased ? "exact_command_released" : "stale_observation_ignored");
                ResetMovementBackend();
                ClearLocalState();
                return;
            }
        }

        BotOwner.GoToSomePointData?.UpdateToGo(command.Sprint);
        ApplyMotionState(command);
        VanguardReturnMovementCommandStore.ReportLogicDrive(BotOwner, command, "update_to_go", command.Sprint ? "terminal_sprint" : "terminal_run");
    }


    private void DriveTacticalAuthoringStationaryHold(
        VanguardReturnMovementCommand command,
        DateTimeOffset now,
        float anchorDistance,
        bool entering)
    {
        bool pathWasActive = BotOwner.Mover?.HasPathAndNoComplete == true;
        if (entering || pathWasActive)
        {
            // The BigBrain layer itself remains active, so normal follow/patrol cannot reclaim the
            // Operator. Only the physical locomotion backend is stopped. This is deliberately not
            // repeated every frame: it is asserted on hold entry and only if a path reappears.
            BotOwner.Mover?.Stop();
            BotOwner.Sprint(false, true);
            BotOwner.Mover?.Sprint(false, false);
            BotOwner.SetTargetMoveSpeed(0f);
            BotOwner.Mover?.SetTargetMoveSpeed(0f);
        }

        activeGeneration = command.Generation;
        activeAnchor = command.Anchor;
        activeContinuousTravel = false;
        lastSetPointAtUtc = now;

        if (entering || pathWasActive)
        {
            VanguardReturnMovementCommandStore.ReportLogicDrive(
                BotOwner,
                command,
                entering ? "tactical_authoring_stationary_hold_enter" : "tactical_authoring_stationary_hold_reassert",
                $"distance={anchorDistance:0.00}:pathWasActive={pathWasActive}:orientationOwnedByAuthoringWatch=true");
        }
    }

    private void DriveTacticalCoordinatorAntiCongaStationaryHold(VanguardReturnMovementCommand command, DateTimeOffset now)
    {
        bool entering = !activeTacticalCoordinatorAntiCongaHold || command.Generation != activeGeneration;
        bool pathWasActive = BotOwner.Mover?.HasPathAndNoComplete == true;
        if (entering || pathWasActive)
        {
            // anti-conga owns only the locomotion slot for a short scheduler-admitted hold. It emits no
            // path and does not touch target/aim/fire semantics. Stop is asserted on entry and only
            // reasserted if a path unexpectedly appears while the exact command remains owned.
            BotOwner.Mover?.Stop();
            BotOwner.Sprint(false, true);
            BotOwner.Mover?.Sprint(false, false);
            BotOwner.SetTargetMoveSpeed(0f);
            BotOwner.Mover?.SetTargetMoveSpeed(0f);
        }

        activeTacticalCoordinatorAntiCongaHold = true;
        activeTacticalCoordinatorAltDevelop = false;
        activeTacticalCoordinatorDirectCommit = false;
        activeTacticalCoordinatorOpportunity = false;
        activeMedicSupportCombatRendezvous = false;
        activeGeneration = command.Generation;
        activeAnchor = command.Anchor;
        activeContinuousTravel = false;
        lastSetPointAtUtc = now;

        if (entering || pathWasActive)
        {
            VanguardReturnMovementCommandStore.ReportLogicDrive(
                BotOwner,
                command,
                entering ? "tc_anti_conga_anti_conga_hold_enter" : "tc_anti_conga_anti_conga_hold_reassert",
                $"pathWasActive={pathWasActive}:movementOnly=true:targetAimFireMutation=false:pathEmitted=false");
        }
    }

    private static bool IsTacticalCoordinatorAntiCongaHold(VanguardReturnMovementCommand command)
    {
        return VanguardReturnMovementCommandStore.IsTacticalCoordinatorAntiCongaHoldRequest(command.RequestKind);
    }

    private static bool IsTacticalAuthoringPreview(VanguardReturnMovementCommand command)
    {
        return string.Equals(command.RequestKind, TacticalAuthoringPreviewRequestKind, StringComparison.OrdinalIgnoreCase);
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private void ApplyMotionState(VanguardReturnMovementCommand command)
    {
        float speed = VanguardContinuousCohesionLocomotionPolicy.ResolveTargetMoveSpeed(command.Sprint);
        BotOwner.SetPose(1f);
        BotOwner.SetTargetMoveSpeed(speed);
        BotOwner.Sprint(command.Sprint, true);
        BotOwner.Mover?.Sprint(command.Sprint, false);
        BotOwner.Mover?.SetTargetMoveSpeed(speed);
        BotOwner.Steering?.LookToMovingDirection(60f);
    }
}
#endif
