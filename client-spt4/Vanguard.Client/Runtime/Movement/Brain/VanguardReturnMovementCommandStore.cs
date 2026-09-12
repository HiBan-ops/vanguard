#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.Grenades;

// Responsibility: Holds the active Vanguard movement command that the BigBrain movement layer is allowed to execute for each Operator.
// Flow: Movement arbitration publishes a generation-stamped destination; the BigBrain layer reads it and reports path/backend progress or failure, while retarget guards prevent needless command churn.
// Authority boundary: The scheduler/lease executor owns movement eligibility and outcome; this store is only the handoff, and BigBrain/EFT remain responsible for physical locomotion.
// Invariant: At most the newest valid command may drive an Operator, stale generations must be rejected, and failure/cleanup signals must not leak into later movement leases.
namespace Vanguard.Client.Runtime.Movement.Brain;

/// <summary>
/// Vanguard command store is the narrow handoff between the MovementBroker/lease executor and the BigBrain
/// movement layer.  The executor owns eligibility, preemption, path validation and lease outcome.  The
/// layer only drives the active destination while a command is present.  This prevents ORBIT/LootingBots
/// from overwriting Vanguard after a few seconds and avoids one-shot reflection GoToPoint calls.
/// </summary>
internal static class VanguardReturnMovementCommandStore
{
    public const string StatusTag = "VANGUARD_MOVE_BRIDGE_LAYER_OK";
    public const string GoToSomePointStatusTag = "VANGUARD_GOTOSOMEPOINT_BRIDGE_OK";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, VanguardReturnMovementCommand> CommandsByProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> LastGenerationByProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, VanguardMovementPathInvalidSignal> PathInvalidByProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, VanguardMovementPhysicalBackendFailureSignal> PhysicalBackendFailureByProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(1.5d);
    private const float SameAnchorToleranceMeters = 1.75f;
    private const float MaterialRetargetMeters = 4.50f;
    private static readonly TimeSpan RetargetCooldown = TimeSpan.FromSeconds(1.25d);


    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            CommandsByProfileId.Clear();
            LastGenerationByProfileId.Clear();
            PathInvalidByProfileId.Clear();
            PhysicalBackendFailureByProfileId.Clear();
            LastLogByKey.Clear();
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_MOVE_BRIDGE_RESET reason={Safe(reason)}; bridge=bigbrain_layer; commandStore=cleared; backend=terminal_GoToSomePoint_or_continuous_direct_GoToPoint; singleAuthority=true; continuousTag={VanguardContinuousCohesionLocomotionPolicy.StatusTag}; tag={StatusTag}; goToSomePointTag={GoToSomePointStatusTag}");
    }

    public static bool Issue(
        string leaseId,
        string operatorId,
        string botProfileId,
        Vector3 anchor,
        float anchorRadiusMeters,
        bool sprint,
        DateTimeOffset now,
        DateTimeOffset expiresAtUtc,
        string requestKind,
        string pathSummary,
        float pathDistanceMeters,
        out string result)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            result = "missing_bot_profile";
            return false;
        }

        if (expiresAtUtc <= now)
        {
            result = "expired_command_window";
            return false;
        }

        if (!IsGrenadeEmergencyRequest(requestKind)
            && VanguardMainIntentScheduler.TryGetActiveEmergencyWindow(botProfileId, now, out string emergencyWindowId, out string emergencyGrenadeKey, out _))
        {
            result = "grenade_emergency_primary_protected:window=" + Safe(emergencyWindowId)
                + ";grenade=" + Safe(emergencyGrenadeKey);
            LogThrottled("grenadeEmergencyIssueRejected|" + botProfileId + "|" + NormalizeRequest(requestKind), now,
                $"VANGUARD_MOVE_COMMAND_REJECTED botProfile={Safe(botProfileId)}; incomingLease={Safe(leaseId)}; incomingRequest={Safe(requestKind)}; emergencyWindow={Safe(emergencyWindowId)}; grenade={Safe(emergencyGrenadeKey)}; mutation=false; doctrine=grenade_emergency_is_exclusive_movement_authority; tag={VanguardGrenadeEmergencyPolicy.StatusTag}; moveBridgeTag={StatusTag}");
            return false;
        }

        VanguardReturnMovementCommand command;
        lock (Sync)
        {
            if (CommandsByProfileId.TryGetValue(botProfileId, out var active) && active.ExpiresAtUtc > now)
            {
                string existingRequest = NormalizeRequest(active.RequestKind);
                string incomingRequest = NormalizeRequest(requestKind);
                bool sameRequest = string.Equals(existingRequest, incomingRequest, StringComparison.OrdinalIgnoreCase);
                bool sameLease = string.Equals(active.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase);
                float anchorDelta = HorizontalDistance(active.Anchor, anchor);
                bool sameAnchor = anchorDelta <= SameAnchorToleranceMeters;
                bool incomingEmergencyHardReturn = IsEmergencyRequest(incomingRequest);

                // The runtime regression guard: only a materially identical command may be adopted by a
                // successor scheduler lease.  Preserving a different anchor/request while creating
                // executor state for the incoming plan would split the lease truth from the BigBrain
                // driver and reproduce the stale-anchor behavior measured during runtime qualification.
                if (sameRequest && sameAnchor)
                {
                    string previousLeaseId = active.LeaseId;
                    bool ownershipTransferred = !sameLease;
                    if (ownershipTransferred)
                    {
                        // Transfer ownership without changing the BigBrain generation or anchor.  The
                        // retiring executor is then unable to clear the adopted command through its
                        // lease-safe cleanup path.
                        active.LeaseId = leaseId;
                        active.OperatorId = operatorId;
                        active.BotProfileId = botProfileId;
                        active.IssuedAtUtc = now;
                        active.ExpiresAtUtc = expiresAtUtc;
                        active.RequestKind = requestKind;
                        active.Sprint = sprint;
                        active.PathSummary = pathSummary;
                        active.PathDistanceMeters = pathDistanceMeters;
                        active.LastRetargetAtUtc = DateTimeOffset.MinValue;
                        active.RetargetCount = 0;
                    }
                    else if (expiresAtUtc > active.ExpiresAtUtc)
                    {
                        active.ExpiresAtUtc = expiresAtUtc;
                    }

                    CommandsByProfileId[botProfileId] = active;
                    result = "preserved_identical_command"
                        + ";existingLease=" + Safe(previousLeaseId)
                        + ";activeLease=" + Safe(active.LeaseId)
                        + ";ownershipTransferred=" + Bool(ownershipTransferred)
                        + ";anchorDelta=" + anchorDelta.ToString("0.00", CultureInfo.InvariantCulture)
                        + ";expires=" + (active.ExpiresAtUtc - now).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);
                    LogThrottled("preserveIdentical|" + botProfileId + "|" + incomingRequest, now,
                        $"VANGUARD_MOVE_COMMAND_ADOPTED {active.Summary}; previousLease={Safe(previousLeaseId)}; incomingLease={Safe(leaseId)}; ownershipTransferred={Bool(ownershipTransferred)}; generationPreserved=true; requestEquivalent=true; anchorEquivalent=true; anchorDelta={anchorDelta:0.00}; doctrine=only_materially_identical_command_may_cross_lease_boundary; Tag={VanguardMovementAuthorityDoctrine.MovementCommandQueueStatusTag}; Tag={VanguardPrimaryExecutionContract.MovementRetargetStatusTag}; tag={StatusTag}; goToSomePointTag={GoToSomePointStatusTag}");
                    return true;
                }

                if (sameLease)
                {
                    // The active executor must use TryRetargetActive so path validation, generation
                    // stability and retarget budgets remain centralized.  Issuing a second command for
                    // the same lease would create silent command churn.
                    result = "same_lease_requires_validated_retarget"
                        + ";anchorDelta=" + anchorDelta.ToString("0.00", CultureInfo.InvariantCulture)
                        + ";activeRequest=" + Safe(existingRequest)
                        + ";incomingRequest=" + Safe(incomingRequest);
                    LogThrottled("sameLeaseIssueRejected|" + botProfileId + "|" + incomingRequest, now,
                        $"VANGUARD_MOVE_COMMAND_REISSUE_REJECTED {active.Summary}; incomingLease={Safe(leaseId)}; incomingRequest={Safe(incomingRequest)}; anchorDelta={anchorDelta:0.00}; reason=same_lease_requires_validated_retarget; doctrine=same_lease_anchor_changes_only_through_path_validated_retarget; Tag={VanguardPrimaryExecutionContract.MovementRetargetStatusTag}; tag={StatusTag}");
                    return false;
                }

                // A different scheduler lease with a different request or destination represents a
                // real handoff. Replace the residual command instead of protecting its obsolete anchor.
                // ClearOwned on the retiring executor protects this newer command from late cleanup.
                LogThrottled("replaceLease|" + botProfileId + "|" + incomingRequest, now,
                    $"VANGUARD_MOVE_COMMAND_HANDOFF botProfile={Safe(botProfileId)}; previousLease={Safe(active.LeaseId)}; incomingLease={Safe(leaseId)}; previousRequest={Safe(existingRequest)}; incomingRequest={Safe(incomingRequest)}; anchorDelta={anchorDelta:0.00}; emergency={Bool(incomingEmergencyHardReturn)}; previousGeneration={active.Generation}; action=replace_with_new_generation; doctrine=new_scheduler_lease_must_drive_its_own_plan_not_inherit_stale_anchor; Tag={VanguardPrimaryExecutionContract.MovementRetargetStatusTag}; tag={StatusTag}");
            }

            command = new VanguardReturnMovementCommand
            {
                LeaseId = leaseId,
                OperatorId = operatorId,
                BotProfileId = botProfileId,
                Anchor = anchor,
                AnchorRadiusMeters = anchorRadiusMeters,
                Sprint = sprint,
                IssuedAtUtc = now,
                ExpiresAtUtc = expiresAtUtc,
                RequestKind = requestKind,
                PathSummary = pathSummary,
                PathDistanceMeters = pathDistanceMeters,
                Generation = NextGenerationLocked(botProfileId),
                ResetBackendBeforeSetPoint = false
            };

            CommandsByProfileId[botProfileId] = command;
        }

        result = $"bigbrain_command_stored;generation={command.Generation.ToString(CultureInfo.InvariantCulture)};expires={(expiresAtUtc - now).TotalSeconds:0.00}";
        string backend = IsGrenadeEmergencyRequest(command.RequestKind)
            ? "shared_emergency_physical_driver_direct_GoToPoint"
            : VanguardContinuousCohesionLocomotionPolicy.IsContinuousTravelRequest(command.RequestKind)
                ? "direct_GoToPoint_slowAtEnd_false"
                : "GoToSomePointData_terminal";
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"VANGUARD_MOVE_BRIDGE_COMMAND_ACCEPTED lease={Safe(command.LeaseId)}; operator={Safe(command.OperatorId)}; botProfile={Safe(command.BotProfileId)}; request={Safe(command.RequestKind)}; generation={command.Generation}; anchorRadius={command.AnchorRadiusMeters:0.00}; pathDist={pathDistanceMeters:0.00}; sprint={Bool(sprint)}; expiresIn={(expiresAtUtc - now).TotalSeconds:0.00}; backend={backend}; fullCommandPayload=false; pathPayload=false; continuousTag={VanguardContinuousCohesionLocomotionPolicy.StatusTag}; Tag={VanguardMovementAuthorityDoctrine.MovementCommandQueueStatusTag}; tag={StatusTag}");
        VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
            $"VANGUARD_MOVE_BRIDGE_COMMAND_ACCEPTED_TRACE command={Safe(command.Summary)}; path={Safe(pathSummary)}; tag={StatusTag}");
        return true;
    }

    public static bool TryUpdateActiveParameters(
        string leaseId,
        string botProfileId,
        float anchorRadiusMeters,
        bool sprint,
        DateTimeOffset now,
        DateTimeOffset expiresAtUtc,
        string reason,
        out string result)
    {
        result = "none";
        if (string.IsNullOrWhiteSpace(botProfileId) || string.IsNullOrWhiteSpace(leaseId))
        {
            result = "missing_identity";
            return false;
        }

        VanguardReturnMovementCommand command;
        bool sprintChanged;
        bool radiusChanged;
        lock (Sync)
        {
            if (!CommandsByProfileId.TryGetValue(botProfileId, out command) || command.ExpiresAtUtc <= now)
            {
                result = "active_command_missing_or_expired";
                return false;
            }

            if (!string.Equals(command.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase))
            {
                result = "lease_mismatch:active=" + Safe(command.LeaseId);
                return false;
            }

            sprintChanged = command.Sprint != sprint;
            radiusChanged = Math.Abs(command.AnchorRadiusMeters - anchorRadiusMeters) >= 0.05f;
            command.Sprint = sprint;
            command.AnchorRadiusMeters = anchorRadiusMeters;
            if (expiresAtUtc > command.ExpiresAtUtc)
            {
                command.ExpiresAtUtc = expiresAtUtc;
            }
            CommandsByProfileId[botProfileId] = command;
        }

        result = "parameters_updated;sprintChanged=" + Bool(sprintChanged)
            + ";radiusChanged=" + Bool(radiusChanged)
            + ";generation=" + command.Generation.ToString(CultureInfo.InvariantCulture);
        if (sprintChanged || radiusChanged)
        {
            VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.MovementRetargetStatusTag,
                $"VANGUARD_TRAVEL_PARAMETERS_UPDATED {command.Summary}; sprintChanged={Bool(sprintChanged)}; radiusChanged={Bool(radiusChanged)}; reason={Safe(reason)}; anchorPreserved=true; generationPreserved=true; noSetPointRetarget=true; doctrine=travel_mode_changes_update_locomotion_parameters_without_anchor_churn; tag=VANGUARD_TRAVEL_RESPONSIVENESS_STATUS; Tag={VanguardPrimaryExecutionContract.MovementRetargetStatusTag}; moveBridgeTag={StatusTag}");
        }
        return true;
    }

    public static VanguardMovementRetargetResult TryRetargetActive(
        string leaseId,
        string botProfileId,
        Vector3 anchor,
        float anchorRadiusMeters,
        bool sprint,
        DateTimeOffset now,
        DateTimeOffset expiresAtUtc,
        string pathSummary,
        float pathDistanceMeters,
        string reason)
        => TryRetargetActive(
            leaseId,
            botProfileId,
            anchor,
            anchorRadiusMeters,
            sprint,
            now,
            expiresAtUtc,
            pathSummary,
            pathDistanceMeters,
            reason,
            MaterialRetargetMeters,
            RetargetCooldown);

    public static VanguardMovementRetargetResult TryRetargetActive(
        string leaseId,
        string botProfileId,
        Vector3 anchor,
        float anchorRadiusMeters,
        bool sprint,
        DateTimeOffset now,
        DateTimeOffset expiresAtUtc,
        string pathSummary,
        float pathDistanceMeters,
        string reason,
        float materialRetargetMeters,
        TimeSpan retargetCooldown)
    {
        materialRetargetMeters = Math.Max(0.50f, materialRetargetMeters);
        if (retargetCooldown < TimeSpan.Zero)
        {
            retargetCooldown = TimeSpan.Zero;
        }
        if (string.IsNullOrWhiteSpace(botProfileId) || string.IsNullOrWhiteSpace(leaseId))
        {
            return VanguardMovementRetargetResult.Rejected(VanguardMovementRetargetOutcome.RejectedIdentity, "missing_identity");
        }

        VanguardReturnMovementCommand command;
        float anchorDelta;
        bool sprintChanged;
        bool radiusChanged;
        lock (Sync)
        {
            if (!CommandsByProfileId.TryGetValue(botProfileId, out command) || command.ExpiresAtUtc <= now)
            {
                return VanguardMovementRetargetResult.Rejected(VanguardMovementRetargetOutcome.RejectedMissingCommand, "active_command_missing_or_expired");
            }

            if (!string.Equals(command.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase))
            {
                return VanguardMovementRetargetResult.Rejected(VanguardMovementRetargetOutcome.RejectedIdentity, "lease_mismatch:active=" + Safe(command.LeaseId));
            }

            anchorDelta = HorizontalDistance(command.Anchor, anchor);
            sprintChanged = command.Sprint != sprint;
            radiusChanged = Math.Abs(command.AnchorRadiusMeters - anchorRadiusMeters) >= 0.50f;
            if (anchorDelta < materialRetargetMeters && !sprintChanged && !radiusChanged)
            {
                if (expiresAtUtc > command.ExpiresAtUtc)
                {
                    command.ExpiresAtUtc = expiresAtUtc;
                }
                command.LastRetargetAtUtc = now;
                CommandsByProfileId[botProfileId] = command;
                return new VanguardMovementRetargetResult(
                    VanguardMovementRetargetOutcome.ExtendedOnlyNotMaterial,
                    "retarget_not_material:delta=" + anchorDelta.ToString("0.00", CultureInfo.InvariantCulture),
                    command.Anchor,
                    command.Generation,
                    command.RetargetCount);
            }

            if (command.LastRetargetAtUtc != DateTimeOffset.MinValue && now - command.LastRetargetAtUtc < retargetCooldown)
            {
                return VanguardMovementRetargetResult.Rejected(
                    VanguardMovementRetargetOutcome.RejectedCooldown,
                    "retarget_cooldown:" + (retargetCooldown - (now - command.LastRetargetAtUtc)).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture));
            }

            command.Anchor = anchor;
            command.AnchorRadiusMeters = anchorRadiusMeters;
            command.Sprint = sprint;
            command.PathSummary = pathSummary;
            command.PathDistanceMeters = pathDistanceMeters;
            command.LastRetargetAtUtc = now;
            command.RetargetCount++;
            if (expiresAtUtc > command.ExpiresAtUtc)
            {
                command.ExpiresAtUtc = expiresAtUtc;
            }
            CommandsByProfileId[botProfileId] = command;
        }

        string summary = "retargeted_same_generation;delta=" + anchorDelta.ToString("0.00", CultureInfo.InvariantCulture)
            + ";sprintChanged=" + Bool(sprintChanged)
            + ";radiusChanged=" + Bool(radiusChanged)
            + ";count=" + command.RetargetCount.ToString(CultureInfo.InvariantCulture);
        VanguardClientDiagnosticsLog.Trace(VanguardPrimaryExecutionContract.MovementRetargetStatusTag,
            () => $"VANGUARD_MOVE_COMMAND_RETARGETED {command.Summary}; delta={anchorDelta:0.00}; sprintChanged={Bool(sprintChanged)}; radiusChanged={Bool(radiusChanged)}; reason={Safe(reason)}; path={Safe(pathSummary)}; pathDist={pathDistanceMeters:0.00}; sameLease=true; sameGeneration=true; boundedCooldown={retargetCooldown.TotalSeconds:0.00}; doctrine=validated_active_executor_retargets_without_command_churn; Tag={VanguardPrimaryExecutionContract.MovementRetargetStatusTag}; Tag={VanguardMovementAuthorityDoctrine.MovementCommandQueueStatusTag}; tag={StatusTag}");
        return new VanguardMovementRetargetResult(
            VanguardMovementRetargetOutcome.Applied,
            summary,
            command.Anchor,
            command.Generation,
            command.RetargetCount);
    }

    public static bool TryRestartOwned(string leaseId, string botProfileId, DateTimeOffset now, string reason, out string result)
        => TryRestartOwned(leaseId, botProfileId, now, reason, false, out result);

    public static bool TryRestartOwned(
        string leaseId,
        string botProfileId,
        DateTimeOffset now,
        string reason,
        bool resetBackendBeforeSetPoint,
        out string result)
    {
        if (string.IsNullOrWhiteSpace(leaseId) || string.IsNullOrWhiteSpace(botProfileId))
        {
            result = "missing_identity";
            return false;
        }

        VanguardReturnMovementCommand command;
        lock (Sync)
        {
            if (!CommandsByProfileId.TryGetValue(botProfileId, out command) || command.ExpiresAtUtc <= now)
            {
                result = "active_command_missing_or_expired";
                return false;
            }

            if (!string.Equals(command.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase))
            {
                result = "lease_mismatch:active=" + Safe(command.LeaseId);
                return false;
            }

            if (VanguardContinuousCohesionLocomotionPolicy.IsContinuousTravelRequest(command.RequestKind))
            {
                result = "continuous_travel_restart_forbidden";
                return false;
            }

            command.Generation = NextGenerationLocked(botProfileId);
            command.IssuedAtUtc = now;
            command.ResetBackendBeforeSetPoint = resetBackendBeforeSetPoint;
            command.LastRetargetAtUtc = now;
            command.RetargetCount++;
            command.PathSummary = Safe(command.PathSummary) + ":physical_restart";
            CommandsByProfileId[botProfileId] = command;
        }

        result = "restarted_same_lease_new_generation;generation=" + command.Generation.ToString(CultureInfo.InvariantCulture)
            + ";count=" + command.RetargetCount.ToString(CultureInfo.InvariantCulture)
            + ";resetBackend=" + Bool(resetBackendBeforeSetPoint);
        VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.PhysicalMovementProgressStatusTag,
            $"VANGUARD_MOVE_COMMAND_PHYSICAL_RESTART {command.Summary}; reason={Safe(reason)}; sameLease=true; newGeneration=true; boundedOnceByExecutor=true; doctrine=world_delta_not_animation_speed_proves_progress; tag={VanguardPrimaryExecutionContract.PhysicalMovementProgressStatusTag}; moveBridgeTag={StatusTag}");
        return true;
    }

    public static bool TryGetActive(string botProfileId, DateTimeOffset now, out VanguardReturnMovementCommand command)
    {
        lock (Sync)
        {
            if (CommandsByProfileId.TryGetValue(botProfileId, out command) && command.ExpiresAtUtc > now)
            {
                return true;
            }

            if (CommandsByProfileId.ContainsKey(botProfileId))
            {
                CommandsByProfileId.Remove(botProfileId);
            }
        }

        command = VanguardReturnMovementCommand.Empty;
        return false;
    }

    /// <summary>
    /// Returns the active command only when it is the exact command generation owned by the
    /// caller's lease.  Movement executors use this during interrupt checks so their own command
    /// is not mistaken for a competing movement authority.  Request kind and generation are part
    /// of the identity: a same-profile command replaced or restarted elsewhere is not silently
    /// adopted by a stale executor.
    /// </summary>
    public static bool TryGetExactOwned(
        string botProfileId,
        string leaseId,
        string requestKind,
        long generation,
        DateTimeOffset now,
        out VanguardReturnMovementCommand command,
        out string reason)
    {
        if (!TryGetActive(botProfileId, now, out command))
        {
            reason = "active_command_missing_or_expired";
            return false;
        }

        if (!string.Equals(command.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "lease_mismatch:active=" + Safe(command.LeaseId);
            return false;
        }

        if (!string.Equals(NormalizeRequest(command.RequestKind), NormalizeRequest(requestKind), StringComparison.OrdinalIgnoreCase))
        {
            reason = "request_mismatch:active=" + Safe(command.RequestKind);
            return false;
        }

        if (command.Generation != generation)
        {
            reason = "generation_mismatch:active=" + command.Generation.ToString(CultureInfo.InvariantCulture)
                + ":expected=" + generation.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        reason = "exact_owned_command";
        return true;
    }

    public static bool HasActive(string botProfileId)
    {
        return TryGetActive(botProfileId, DateTimeOffset.UtcNow, out _);
    }

    public static void RefreshLeaseWindow(string botProfileId, DateTimeOffset expiresAtUtc, string reason)
    {
        lock (Sync)
        {
            if (CommandsByProfileId.TryGetValue(botProfileId, out var command) && expiresAtUtc > command.ExpiresAtUtc)
            {
                command.ExpiresAtUtc = expiresAtUtc;
                CommandsByProfileId[botProfileId] = command;
            }
        }

        LogThrottled("refresh|" + botProfileId, DateTimeOffset.UtcNow,
            () => $"VANGUARD_MOVE_BRIDGE_COMMAND_REFRESHED botProfile={Safe(botProfileId)}; reason={Safe(reason)}; expires={expiresAtUtc:O}; tag={StatusTag}; goToSomePointTag={GoToSomePointStatusTag}");
    }

    public static string ClearOwned(string botProfileId, string leaseId, DateTimeOffset leaseStartedAtUtc, string reason)
    {
        VanguardReturnMovementCommand command;
        bool removed = false;
        string guardReason;
        lock (Sync)
        {
            if (!CommandsByProfileId.TryGetValue(botProfileId, out command))
            {
                return "none";
            }

            bool sameLease = string.Equals(command.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase);
            if (sameLease)
            {
                CommandsByProfileId.Remove(botProfileId);
                removed = true;
                guardReason = "same_lease";
            }
            else
            {
                // Cross-lease cleanup is never inferred from timestamps. A scheduler handoff can occur
                // in the same frame, and any tolerance would allow the retiring executor to delete the
                // replacement command. Identical command adoption explicitly transfers LeaseId first.
                guardReason = "different_lease_protected";
            }
        }

        if (removed)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_MOVE_COMMAND_OWNED_CLEAR {command.Summary}; lease={Safe(leaseId)}; guard={Safe(guardReason)}; reason={Safe(reason)}; Tag={VanguardPrimaryExecutionContract.MovementRetargetStatusTag}; tag={StatusTag}; goToSomePointTag={GoToSomePointStatusTag}");
            return "cleared:" + command.LeaseId + ":" + guardReason;
        }

        LogThrottled("ownedClearProtected|" + botProfileId + "|" + leaseId, DateTimeOffset.UtcNow,
            $"VANGUARD_MOVE_COMMAND_CLEAR_PROTECTED botProfile={Safe(botProfileId)}; finishingLease={Safe(leaseId)}; activeLease={Safe(command.LeaseId)}; activeIssued={command.IssuedAtUtc:O}; finishingStarted={leaseStartedAtUtc:O}; guard={Safe(guardReason)}; reason={Safe(reason)}; doctrine=executor_can_clear_only_its_exact_lease; Tag={VanguardPrimaryExecutionContract.MovementRetargetStatusTag}; tag={StatusTag}");
        return "protected_newer_command:" + command.LeaseId;
    }

    public static string Clear(string botProfileId, string reason)
    {
        VanguardReturnMovementCommand command;
        bool removed;
        bool protectedEmergency = false;
        lock (Sync)
        {
            removed = CommandsByProfileId.TryGetValue(botProfileId, out command);
            if (removed && IsGrenadeEmergencyRequest(command.RequestKind))
            {
                // Generic legacy cleanup paths do not own grenade subsystem. Only the exact emergency lease may
                // clear its command through ClearOwned, preventing medical/combat/follow cleanup
                // from deleting the survival destination in the same frame.
                protectedEmergency = true;
                removed = false;
            }
            else if (removed)
            {
                CommandsByProfileId.Remove(botProfileId);
            }
        }

        if (protectedEmergency)
        {
            LogThrottled("grenadeEmergencyGenericClearProtected|" + botProfileId + "|" + Safe(reason), DateTimeOffset.UtcNow,
                $"VANGUARD_MOVE_COMMAND_CLEAR_PROTECTED botProfile={Safe(botProfileId)}; emergencyLease={Safe(command.LeaseId)}; emergencyGeneration={command.Generation}; reason={Safe(reason)}; mutation=false; doctrine=only_exact_emergency_lease_may_clear; tag={VanguardGrenadeEmergencyPolicy.StatusTag}; moveBridgeTag={StatusTag}");
            return "protected_grenade_emergency:" + command.LeaseId;
        }

        if (removed)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_MOVE_BRIDGE_COMMAND_CLEARED {command.Summary}; reason={Safe(reason)}; tag={StatusTag}; goToSomePointTag={GoToSomePointStatusTag}");
            return "cleared:" + command.LeaseId;
        }

        return "none";
    }

    public static void ReportLayerActive(BotOwner botOwner, VanguardReturnMovementCommand command, string phase)
    {
        string botProfileId = botOwner?.ProfileId ?? command.BotProfileId;
        LogThrottled("layer|" + botProfileId + "|" + phase, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(6.0d),
            () => $"VANGUARD_MOVE_BRIDGE_LAYER_ACTIVE {command.Summary}; phase={Safe(phase)}; botAlive={Bool(botOwner != null && !botOwner.IsDead)}; backend=BigBrain_CustomLayer; tag={StatusTag}; goToSomePointTag={GoToSomePointStatusTag}");
    }

    public static void ReportLogicDrive(BotOwner botOwner, VanguardReturnMovementCommand command, string phase, string result)
    {
        string botProfileId = botOwner?.ProfileId ?? command.BotProfileId;
        bool grenadeEmergency = IsGrenadeEmergencyRequest(command.RequestKind);
        bool grenadeSafetyHold = grenadeEmergency
            && !string.IsNullOrWhiteSpace(command.PathSummary)
            && command.PathSummary.IndexOf(VanguardGrenadeEmergencyPolicy.SafetyHoldPathMarker, StringComparison.OrdinalIgnoreCase) >= 0;
        string backend = grenadeSafetyHold
            ? "emergency_safety_hold_no_destination"
            : grenadeEmergency
                ? "atomic_stop_then_direct_emergency_GoToPoint_slowAtEnd_false"
                : VanguardContinuousCohesionLocomotionPolicy.IsContinuousTravelRequest(command.RequestKind)
                ? "direct_GoToPoint_slowAtEnd_false"
                : "GoToSomePointData_SetPoint_UpdateToGo";
        LogThrottled("drive|" + botProfileId + "|" + phase + "|" + result, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(6.0d),
            $"VANGUARD_GOTOSOMEPOINT_DRIVE {command.Summary}; phase={Safe(phase)}; result={Safe(result)}; backend={backend}; continuousTag={VanguardContinuousCohesionLocomotionPolicy.StatusTag}; tag={GoToSomePointStatusTag}; moveBridgeTag={StatusTag}");
    }

    public static bool ReportPathInvalid(VanguardReturnMovementCommand command, DateTimeOffset now, string summary)
    {
        bool released = false;
        lock (Sync)
        {
            if (CommandsByProfileId.TryGetValue(command.BotProfileId, out var active)
                && string.Equals(active.LeaseId, command.LeaseId, StringComparison.OrdinalIgnoreCase)
                && active.Generation == command.Generation)
            {
                CommandsByProfileId.Remove(command.BotProfileId);
                PathInvalidByProfileId[command.BotProfileId] = new VanguardMovementPathInvalidSignal(
                    command.LeaseId,
                    command.Generation,
                    now,
                    summary);
                released = true;
            }
        }

        VanguardClientDiagnosticsLog.Warning(VanguardPrimaryExecutionContract.MovementRetargetStatusTag,
            $"VANGUARD_PATH_INVALID_FEEDBACK {command.Summary}; path={Safe(summary)}; commandReleased={Bool(released)}; layer97Released={Bool(released)}; staleObservationIgnored={Bool(!released)}; doctrine=invalid_navmesh_path_must_not_remain_as_bigbrain_authority; tag={VanguardPrimaryExecutionContract.MovementRetargetStatusTag}");
        return released;
    }

    public static bool ReportPhysicalBackendFailure(VanguardReturnMovementCommand command, DateTimeOffset now, string summary)
    {
        bool released = false;
        lock (Sync)
        {
            if (CommandsByProfileId.TryGetValue(command.BotProfileId, out var active)
                && string.Equals(active.LeaseId, command.LeaseId, StringComparison.OrdinalIgnoreCase)
                && active.Generation == command.Generation)
            {
                CommandsByProfileId.Remove(command.BotProfileId);
                PhysicalBackendFailureByProfileId[command.BotProfileId] = new VanguardMovementPhysicalBackendFailureSignal(
                    command.LeaseId,
                    command.Generation,
                    now,
                    summary);
                released = true;
            }
        }

        VanguardClientDiagnosticsLog.Warning(VanguardGrenadeEmergencyPhysicalDriver.EmergencyBackendFailureTag,
            $"{command.Summary}; backendFailure={Safe(summary)}; commandReleased={Bool(released)}; exactEmergencyLayerReleased={Bool(released)}; anchorInvalid=false; anchorQuarantineRequired=false; doctrine=physical_backend_failure_is_not_navmesh_anchor_failure; tag={VanguardGrenadeEmergencyPhysicalDriver.EmergencyLayerAcquisitionStatusTag}");
        return released;
    }

    public static bool TryConsumePhysicalBackendFailure(
        string botProfileId,
        string leaseId,
        long generation,
        DateTimeOffset now,
        out string summary)
    {
        lock (Sync)
        {
            if (PhysicalBackendFailureByProfileId.TryGetValue(botProfileId, out var signal))
            {
                if (now - signal.ObservedAtUtc > TimeSpan.FromSeconds(5.0d))
                {
                    PhysicalBackendFailureByProfileId.Remove(botProfileId);
                }
                else if (string.Equals(signal.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase)
                    && signal.Generation == generation)
                {
                    PhysicalBackendFailureByProfileId.Remove(botProfileId);
                    summary = signal.Summary;
                    return true;
                }
            }
        }

        summary = "none";
        return false;
    }

    public static bool TryConsumePathInvalid(
        string botProfileId,
        string leaseId,
        long generation,
        DateTimeOffset now,
        out string summary)
    {
        lock (Sync)
        {
            if (PathInvalidByProfileId.TryGetValue(botProfileId, out var signal))
            {
                if (now - signal.ObservedAtUtc > TimeSpan.FromSeconds(5.0d))
                {
                    PathInvalidByProfileId.Remove(botProfileId);
                }
                else if (string.Equals(signal.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase)
                    && signal.Generation == generation)
                {
                    PathInvalidByProfileId.Remove(botProfileId);
                    summary = signal.Summary;
                    return true;
                }
            }
        }

        summary = "none";
        return false;
    }

    public static bool ValidatePathStillComplete(BotOwner botOwner, Vector3 anchor, out string summary)
    {
        Vector3 position = ResolveBotPosition(botOwner);
        if (!NavMesh.SamplePosition(position + Vector3.up * 0.25f, out var botHit, 4.0f, NavMesh.AllAreas))
        {
            summary = "bot_navmesh_sample_failed";
            return false;
        }

        if (!NavMesh.SamplePosition(anchor + Vector3.up * 0.25f, out var targetHit, 2.0f, NavMesh.AllAreas))
        {
            summary = "target_navmesh_sample_failed";
            return false;
        }

        var path = new NavMeshPath();
        bool calculated = NavMesh.CalculatePath(botHit.position, targetHit.position, NavMesh.AllAreas, path);
        int corners = path.corners == null ? 0 : path.corners.Length;
        summary = "calculated=" + Bool(calculated) + ";status=" + path.status + ";corners=" + corners.ToString(CultureInfo.InvariantCulture);
        return calculated && path.status == NavMeshPathStatus.PathComplete && corners >= 2;
    }

    private static Vector3 ResolveBotPosition(BotOwner botOwner)
    {
        object? player = VanguardOperatorRuntimeAuditReflection.GetMember(botOwner, "GetPlayer", "Player");
        object? transform = VanguardOperatorRuntimeAuditReflection.GetDeep(player, "PlayerBones", "BodyTransform");
        object? position = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(transform, "position");
        if (position is Vector3 vector)
        {
            return vector;
        }

        object? playerTransform = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player, "Transform", "transform");
        position = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(playerTransform, "position");
        return position is Vector3 transformVector ? transformVector : Vector3.zero;
    }

    private static void LogThrottled(string key, DateTimeOffset now, Func<string> messageFactory)
    {
        LogThrottled(key, now, LogInterval, messageFactory);
    }

    private static void LogThrottled(string key, DateTimeOffset now, TimeSpan interval, Func<string> messageFactory)
    {
        if (!VanguardClientDiagnosticsLog.IsEnabled(VanguardAuditLevel.Trace))
        {
            return;
        }

        lock (Sync)
        {
            if (LastLogByKey.TryGetValue(key, out var last) && now - last < interval)
            {
                return;
            }

            LastLogByKey[key] = now;
        }

        VanguardClientDiagnosticsLog.Trace(StatusTag, messageFactory);
    }

    private static void LogThrottled(string key, DateTimeOffset now, string message)
    {
        LogThrottled(key, now, LogInterval, message);
    }

    private static void LogThrottled(string key, DateTimeOffset now, TimeSpan interval, string message)
    {
        lock (Sync)
        {
            if (LastLogByKey.TryGetValue(key, out var last) && now - last < interval)
            {
                return;
            }

            LastLogByKey[key] = now;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag, message);
    }

    private static long NextGenerationLocked(string botProfileId)
    {
        long next = LastGenerationByProfileId.TryGetValue(botProfileId, out var last)
            ? checked(last + 1L)
            : 1L;
        LastGenerationByProfileId[botProfileId] = next;
        return next;
    }

    public static bool IsGrenadeEmergencyRequest(string? requestKind)
    {
        return string.Equals(NormalizeRequest(requestKind), VanguardGrenadeEmergencyPolicy.RequestKind, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEmergencyRequest(string? requestKind)
    {
        string request = NormalizeRequest(requestKind);
        return request.IndexOf("HardReturn", StringComparison.OrdinalIgnoreCase) >= 0
            || request.IndexOf("PathSafe", StringComparison.OrdinalIgnoreCase) >= 0
            || request.IndexOf("Emergency", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string NormalizeRequest(string? requestKind)
    {
        return string.IsNullOrWhiteSpace(requestKind) ? "none" : requestKind.Trim();
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }
}

internal enum VanguardMovementRetargetOutcome
{
    Applied = 0,
    ExtendedOnlyNotMaterial = 1,
    RejectedCooldown = 2,
    RejectedIdentity = 3,
    RejectedMissingCommand = 4,
}

internal readonly struct VanguardMovementRetargetResult
{
    public VanguardMovementRetargetResult(VanguardMovementRetargetOutcome outcome, string summary, Vector3 commandedAnchor, long generation, int retargetCount)
    {
        Outcome = outcome;
        Summary = summary;
        CommandedAnchor = commandedAnchor;
        Generation = generation;
        RetargetCount = retargetCount;
    }

    public VanguardMovementRetargetOutcome Outcome { get; }
    public string Summary { get; }
    public Vector3 CommandedAnchor { get; }
    public long Generation { get; }
    public int RetargetCount { get; }
    public bool Applied => Outcome == VanguardMovementRetargetOutcome.Applied;
    public bool Accepted => Applied || Outcome == VanguardMovementRetargetOutcome.ExtendedOnlyNotMaterial;

    public static VanguardMovementRetargetResult Rejected(VanguardMovementRetargetOutcome outcome, string summary)
        => new(outcome, summary, Vector3.zero, 0L, 0);

    public override string ToString() => Outcome + ":" + Summary;
}

internal readonly struct VanguardMovementPhysicalBackendFailureSignal
{
    public VanguardMovementPhysicalBackendFailureSignal(string leaseId, long generation, DateTimeOffset observedAtUtc, string summary)
    {
        LeaseId = leaseId;
        Generation = generation;
        ObservedAtUtc = observedAtUtc;
        Summary = summary;
    }

    public string LeaseId { get; }
    public long Generation { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public string Summary { get; }
}

internal readonly struct VanguardMovementPathInvalidSignal
{
    public VanguardMovementPathInvalidSignal(string leaseId, long generation, DateTimeOffset observedAtUtc, string summary)
    {
        LeaseId = leaseId;
        Generation = generation;
        ObservedAtUtc = observedAtUtc;
        Summary = summary;
    }

    public string LeaseId { get; }
    public long Generation { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public string Summary { get; }
}

internal struct VanguardReturnMovementCommand
{
    public static readonly VanguardReturnMovementCommand Empty = new();

    public string LeaseId;
    public string OperatorId;
    public string BotProfileId;
    public Vector3 Anchor;
    public float AnchorRadiusMeters;
    public bool Sprint;
    public DateTimeOffset IssuedAtUtc;
    public DateTimeOffset ExpiresAtUtc;
    public string RequestKind;
    public string PathSummary;
    public float PathDistanceMeters;
    public long Generation;
    public DateTimeOffset LastRetargetAtUtc;
    public int RetargetCount;
    public bool ResetBackendBeforeSetPoint;

    public bool ContinuousTravel => VanguardContinuousCohesionLocomotionPolicy.IsContinuousTravelRequest(RequestKind);

    public string Summary => "lease=" + Safe(LeaseId)
        + ";operator=" + Safe(OperatorId)
        + ";botProfile=" + Safe(BotProfileId)
        + ";request=" + Safe(RequestKind)
        + ";anchor=" + Anchor.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + Anchor.y.ToString("0.0", CultureInfo.InvariantCulture) + "," + Anchor.z.ToString("0.0", CultureInfo.InvariantCulture)
        + ";radius=" + AnchorRadiusMeters.ToString("0.00", CultureInfo.InvariantCulture)
        + ";sprint=" + (Sprint ? "true" : "false")
        + ";generation=" + Generation.ToString(CultureInfo.InvariantCulture)
        + ";retargets=" + RetargetCount.ToString(CultureInfo.InvariantCulture)
        + ";resetBackend=" + (ResetBackendBeforeSetPoint ? "true" : "false")
        + ";continuousTravel=" + (ContinuousTravel ? "true" : "false");

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }
}
#endif
