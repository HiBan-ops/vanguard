#if SPT_CLIENT
using System;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.Grenades;
using UnityEngine;
using UnityEngine.AI;

// Responsibility: Implements the BigBrain-facing movement logic used when Vanguard has explicitly authorized an Operator return/cohesion movement action.
// Flow: The brain logic consumes the active Vanguard return plan/target, drives compatible EFT movement primitives while the lease remains valid, reports progress/failure and yields when the plan or authority is revoked.
// Authority boundary: It is a physical movement adapter only; the movement doctrine/scheduler decides whether return authority exists and SAIN combat can preempt according to the shared rules.
// Invariant: No stale return target survives lease cancellation, movement stops/yields cleanly on higher-priority authority, and failures feed recovery rather than infinite retries.
namespace Vanguard.Client.Runtime.Movement.Brain;

/// <summary>
/// BigBrain action that owns the movement backend during a Vanguard movement lease. Terminal
/// movements keep the vanilla GoToSomePointData bridge. TravelCohesionFollowThrough instead uses
/// a continuous direct GoToPoint path with slowAtTheEnd=false, preserving one locomotion episode
/// while the monotonic corridor advances its anchor.
/// </summary>
internal sealed class VanguardReturnMovementLogic : CustomLogic
{
    private long activeGeneration;
    private Vector3 activeAnchor;
    private bool activeContinuousTravel;
    private DateTimeOffset lastSetPointAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastPathCheckAtUtc = DateTimeOffset.MinValue;
    private bool activeTacticalAuthoringHold;

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
                // requests keep the historical reset behavior.
                if (!activeTacticalAuthoringHold)
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
        lastSetPointAtUtc = DateTimeOffset.MinValue;
        lastPathCheckAtUtc = DateTimeOffset.MinValue;
        activeTacticalAuthoringHold = false;
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
                if (!activeTacticalAuthoringHold)
                {
                    ResetMovementBackend(activeContinuousTravel);
                }
                ClearLocalState();
            }
            return;
        }

        if (activeTacticalAuthoringHold && !IsTacticalAuthoringPreview(command))
        {
            // Command ownership changed domains. The stationary-hold exception must never leak
            // into grenade/medical/cohesion/other Vanguard movement semantics.
            activeTacticalAuthoringHold = false;
        }

        if (VanguardReturnMovementCommandStore.IsGrenadeEmergencyRequest(command.RequestKind))
        {
            DriveEmergencyGrenadeMovement(command, now);
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
        lastSetPointAtUtc = now;
        VanguardReturnMovementCommandStore.ReportLogicDrive(BotOwner, command, "grenade_emergency_physical_lease", result);
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

            NavMeshPathStatus status = BotOwner.GoToPoint(
                command.Anchor,
                false,
                reachDistance,
                false,
                false);

            activeGeneration = command.Generation;
            activeAnchor = command.Anchor;
            activeContinuousTravel = true;
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
