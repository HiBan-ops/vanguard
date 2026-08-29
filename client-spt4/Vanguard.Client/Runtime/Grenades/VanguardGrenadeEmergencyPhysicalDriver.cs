#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.Movement.Brain;

// Responsibility: performs the physical escape movement for a grenade emergency after grenade policy has already selected an escape command.
// Flow: The approved escape anchor/path is revalidated, conflicting movement is suspended, EFT movement is driven away from the danger area, and path/anchor progress plus ignition timing are monitored until the Operator reaches safety or the emergency is cancelled.
// Authority boundary: grenade policy owns the emergency decision; this driver only executes that decision and must still yield to invalid paths, dead/unbound actors and lifecycle shutdown.
// Invariant: one generation owns one physical escape attempt, and every success/failure/timeout path releases movement state so an old grenade command cannot keep steering the Operator.
namespace Vanguard.Client.Runtime.Grenades;

/// <summary>
/// grenade subsystem physical locomotion authority. The emergency service and BigBrain layer share this
/// single driver, so a valid emergency command is applied even while BigBrain is between actions.
/// The runtime closes the physical cleanup boundary and recognises path-valid detour progress without
/// weakening exact command ownership. grenade subsystem adds a bounded physical ignition grace: an immediate
/// same-frame PathInvalid or missing TargetPoint is treated as provisional while the exact emergency
/// command remains active, allowing the priority-97 layer to displace a live SAIN chase before any
/// destructive stop/replan is permitted.
/// </summary>
internal static class VanguardGrenadeEmergencyPhysicalDriver
{
    public const string StatusTag = "VANGUARD_PHYSICAL_EVASION_LEASE_STATUS";
    public const string CleanupStatusTag = "VANGUARD_PHYSICAL_LEASE_CLEANUP_AND_SOURCE_VALIDATOR_CONVERGENCE_STATUS";
    public const string IgnitionGraceStatusTag = "VANGUARD_PHYSICAL_IGNITION_GRACE_AND_SAIN_CHASE_EXCLUSION_STATUS";
    public const string AppliedTag = "GRENADE_PHYSICAL_DRIVE_APPLIED";
    public const string ReacquiredTag = "GRENADE_PHYSICAL_DRIVE_REACQUIRED";
    public const string ProgressTag = "GRENADE_PHYSICAL_PROGRESS_CONFIRMED";
    public const string ReleasedTag = "GRENADE_PHYSICAL_LEASE_RELEASED";
    public const string IgnitionPendingTag = "GRENADE_PHYSICAL_IGNITION_PENDING";
    public const string IgnitionConfirmedTag = "GRENADE_PHYSICAL_IGNITION_CONFIRMED";
    public const string IgnitionFailedTag = "GRENADE_PHYSICAL_IGNITION_FAILED";
    public const string PathInvalidDeferredTag = "GRENADE_PHYSICAL_PATH_INVALID_DEFERRED";
    public const string SainChaseExcludedTag = "GRENADE_SAIN_CHASE_MOVEMENT_EXCLUDED";
    public const string DiagnosticConvergenceStatusTag = "VANGUARD_DIAGNOSTIC_OWNERSHIP_AND_COUNTER_CONVERGENCE_STATUS";
    public const string PhysicalIgnitionProofStatusTag = "VANGUARD_PHYSICAL_GRENADE_IGNITION_PROOF_STATUS";
    public const string EmergencyLayerAcquisitionStatusTag = "VANGUARD_EMERGENCY_LAYER_ACQUISITION_STATUS";
    public const string EmergencyAuthorityPendingTag = "GRENADE_EMERGENCY_LAYER_AUTHORITY_PENDING";
    public const string EmergencyAuthorityAcquiredTag = "GRENADE_EMERGENCY_LAYER_AUTHORITY_ACQUIRED";
    public const string EmergencyRecoveryReissueTag = "GRENADE_EMERGENCY_LAYER_RECOVERY_REISSUE";
    public const string EmergencySafetyHoldExcludedTag = "GRENADE_EMERGENCY_LAYER_SAFETY_HOLD_EXCLUDED";
    public const string EmergencyBackendFailureTag = "GRENADE_EMERGENCY_PHYSICAL_BACKEND_FAILURE";
    public const string IdempotentDriveStatusTag = "VANGUARD_GRENADE_DRIVE_IDEMPOTENCE_STATUS";
    public const string TransientReleaseStatusTag = "VANGUARD_TRANSIENT_GRENADE_RELEASE_STATUS";
    public const string LocomotionAuthorityStatusTag = "VANGUARD_CRITICAL_GRENADE_LOCOMOTION_AUTHORITY_STATUS";
    public const string TransientActionBlockedTag = "GRENADE_PHYSICAL_TRANSIENT_ACTION_BLOCKED";
    public const string TransientActionReleasedTag = "GRENADE_PHYSICAL_TRANSIENT_ACTION_RELEASED";

    private sealed class DriveState
    {
        public string LeaseId = string.Empty;
        public long Generation;
        public Vector3 Anchor;
        public Vector3 MovementReferencePosition;
        public DateTimeOffset LastIssueAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset MovementReferenceAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastProgressAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastMovementAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastTowardAnchorProgressAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastPathValidAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastTargetMatchAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastKnownTargetMismatchAtUtc = DateTimeOffset.MinValue;
        public float MovementReferenceDistanceToAnchor = float.PositiveInfinity;
        public float CurrentDistanceToAnchor = float.PositiveInfinity;
        public DateTimeOffset LastProgressLogAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastPathCheckAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset IgnitionStartedAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastIgnitionPendingLogAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastIgnitionConfirmationLogAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastBigBrainDriveAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset AuthorityPendingStartedAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset AuthorityAcquiredAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastAuthorityPendingLogAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastRecoveryReissueAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastSainExclusionLogAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset TransientActionBlockedSinceUtc = DateTimeOffset.MinValue;
        public DateTimeOffset TransientActionReleasedAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastTransientActionLogAtUtc = DateTimeOffset.MinValue;
        public bool TransientActionObserved;
        public int IgnitionAttemptCount;
        public int AuthorityReissueCount;
        public int RecoveryReissueCount;
        public int ReacquireCount;
        public Vector3 IgnitionOriginPosition;
        public Vector3 LastPhysicalSamplePosition;
        public DateTimeOffset LastPhysicalSampleAtUtc = DateTimeOffset.MinValue;
        public float ObservedSpeedMetersPerSecond;
        public float MaxDisplacementFromIgnitionOrigin;
        public float IgnitionOriginDistanceToAnchor = float.PositiveInfinity;
        public float MaxTowardAnchorDisplacement;
        public bool IgnitionConfirmed;
        public bool MovementAuthorityAcquired;
        public bool Holding;
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, DriveState> States = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan MinimumIssueInterval = TimeSpan.FromSeconds(0.18d);
    private static readonly TimeSpan StallReacquireInterval = TimeSpan.FromSeconds(0.48d);
    private static readonly TimeSpan PathValidityMemory = TimeSpan.FromSeconds(0.90d);
    private static readonly TimeSpan BigBrainOwnershipMemory = TimeSpan.FromSeconds(0.75d);
    private static readonly TimeSpan IgnitionPendingLogInterval = TimeSpan.FromSeconds(0.35d);
    private const float ProgressMeters = 0.18f;
    private const float PathTargetToleranceMeters = 1.25f;
    private const float PhysicalSpeedEvidenceMetersPerSecond = 0.20f;
    private const float PhysicalDisplacementEvidenceMeters = 0.05f;
    private const float PhysicalTowardAnchorEvidenceMeters = 0.03f;
    private static readonly TimeSpan HardNoIgnitionFailureAge = TimeSpan.FromSeconds(0.72d);
    private static readonly TimeSpan ConditionalRecoveryReissueDelay = TimeSpan.FromSeconds(0.35d);
    private static readonly TimeSpan PostAuthorityAcquisitionGrace = TimeSpan.FromSeconds(0.30d);
    private static readonly TimeSpan MaximumTransientActionReleaseGrace = TimeSpan.FromSeconds(1.50d);
    private static readonly TimeSpan PostTransientActionReleaseFailureAge = TimeSpan.FromSeconds(0.72d);
    private static readonly TimeSpan TransientActionLogInterval = TimeSpan.FromSeconds(0.35d);

    public static void ResetForRaidLifecycle()
    {
        lock (Sync)
        {
            States.Clear();
        }
    }

    public static void Release(string botProfileId)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return;
        }

        lock (Sync)
        {
            States.Remove(botProfileId);
        }
    }

    public static string StopAndRelease(BotOwner? owner, string botProfileId, string reason)
    {
        bool hadState;
        lock (Sync)
        {
            hadState = !string.IsNullOrWhiteSpace(botProfileId) && States.Remove(botProfileId);
        }

        bool goToStopped = false;
        bool moverStopped = false;
        bool sprintStopped = false;
        if (owner != null)
        {
            try
            {
                if (owner.GoToSomePointData != null)
                {
                    owner.GoToSomePointData.UpdateToGo(false);
                    goToStopped = true;
                }
            }
            catch
            {
                // Direct mover stop remains the decisive cleanup boundary.
            }

            try
            {
                if (owner.Mover != null)
                {
                    owner.Mover.Stop();
                    moverStopped = true;
                }
            }
            catch
            {
                // Reported in the release diagnostic.
            }

            try
            {
                owner.Sprint(false, true);
                owner.Mover?.Sprint(false, false);
                sprintStopped = true;
            }
            catch
            {
                // Sprint cleanup is secondary to path cancellation.
            }
        }

        bool physicalBackendStopped = moverStopped;
        string summary = "hadState=" + Bool(hadState)
            + ";goToStopped=" + Bool(goToStopped)
            + ";moverStopped=" + Bool(moverStopped)
            + ";sprintStopped=" + Bool(sprintStopped)
            + ";physicalBackendStopped=" + Bool(physicalBackendStopped)
            + ";reason=" + Safe(reason);
        VanguardClientDiagnosticsLog.Operational(ReleasedTag, () =>
            $"botProfile={Safe(botProfileId)}; {summary}; directGoToPointStopProof=moverStopped; tag={CleanupStatusTag}; foundationTag={StatusTag}");
        return summary;
    }

    public static bool Drive(BotOwner owner, VanguardReturnMovementCommand command, DateTimeOffset now, string source, out string result)
    {
        result = "not_applied";
        if (owner == null || owner.IsDead || string.IsNullOrWhiteSpace(owner.ProfileId))
        {
            result = "owner_unavailable";
            return false;
        }
        if (!string.Equals(owner.ProfileId, command.BotProfileId, StringComparison.OrdinalIgnoreCase))
        {
            result = "owner_command_mismatch";
            return false;
        }
        if (command.ExpiresAtUtc <= now)
        {
            result = "command_expired";
            return false;
        }
        if (!VanguardReturnMovementCommandStore.IsGrenadeEmergencyRequest(command.RequestKind))
        {
            result = "not_grenade_emergency";
            return false;
        }

        bool holding = !string.IsNullOrWhiteSpace(command.PathSummary)
            && command.PathSummary.IndexOf(VanguardGrenadeEmergencyPolicy.SafetyHoldPathMarker, StringComparison.OrdinalIgnoreCase) >= 0;

        DriveState state;
        lock (Sync)
        {
            if (!States.TryGetValue(owner.ProfileId, out state))
            {
                state = new DriveState();
                States[owner.ProfileId] = state;
            }
        }

        bool exactLayerCall = string.Equals(source, "bigbrain_layer", StringComparison.OrdinalIgnoreCase);
        bool leaseChanged = !string.Equals(state.LeaseId, command.LeaseId, StringComparison.OrdinalIgnoreCase);
        bool newGeneration = state.Generation != command.Generation;
        bool anchorChanged = HorizontalDistance(state.Anchor, command.Anchor) > 0.30f;
        bool identityChanged = leaseChanged || newGeneration || anchorChanged;
        Vector3 position = owner.Position;
        float distanceToAnchor = HorizontalDistance(position, command.Anchor);

        if (holding)
        {
            bool enteringHold = identityChanged || !state.Holding;
            owner.GoToSomePointData?.UpdateToGo(false);
            if (enteringHold)
            {
                owner.Mover?.Stop();
            }
            owner.Sprint(false, true);
            owner.Mover?.Sprint(false, false);
            if (enteringHold)
            {
                state.LeaseId = command.LeaseId;
                state.Generation = command.Generation;
                state.Anchor = command.Anchor;
                state.MovementReferencePosition = position;
                state.MovementReferenceDistanceToAnchor = 0f;
                state.CurrentDistanceToAnchor = 0f;
                state.MovementReferenceAtUtc = now;
                state.LastProgressAtUtc = now;
                state.LastMovementAtUtc = DateTimeOffset.MinValue;
                state.LastTowardAnchorProgressAtUtc = DateTimeOffset.MinValue;
                state.LastPathValidAtUtc = now;
                state.LastTargetMatchAtUtc = DateTimeOffset.MinValue;
                state.LastKnownTargetMismatchAtUtc = DateTimeOffset.MinValue;
                state.IgnitionStartedAtUtc = DateTimeOffset.MinValue;
                state.LastIgnitionPendingLogAtUtc = DateTimeOffset.MinValue;
                state.LastBigBrainDriveAtUtc = DateTimeOffset.MinValue;
                state.AuthorityPendingStartedAtUtc = DateTimeOffset.MinValue;
                state.AuthorityAcquiredAtUtc = DateTimeOffset.MinValue;
                state.LastAuthorityPendingLogAtUtc = DateTimeOffset.MinValue;
                state.LastRecoveryReissueAtUtc = DateTimeOffset.MinValue;
                state.IgnitionAttemptCount = 0;
                state.AuthorityReissueCount = 0;
                state.RecoveryReissueCount = 0;
                state.IgnitionOriginPosition = position;
                state.LastPhysicalSamplePosition = position;
                state.LastPhysicalSampleAtUtc = now;
                state.ObservedSpeedMetersPerSecond = 0f;
                state.MaxDisplacementFromIgnitionOrigin = 0f;
                state.IgnitionOriginDistanceToAnchor = 0f;
                state.MaxTowardAnchorDisplacement = 0f;
                state.IgnitionConfirmed = false;
                state.MovementAuthorityAcquired = false;
                state.TransientActionBlockedSinceUtc = DateTimeOffset.MinValue;
                state.TransientActionReleasedAtUtc = DateTimeOffset.MinValue;
                state.LastTransientActionLogAtUtc = DateTimeOffset.MinValue;
                state.TransientActionObserved = false;
                VanguardClientDiagnosticsLog.Operational(EmergencySafetyHoldExcludedTag, () =>
                    $"operator={Safe(command.OperatorId)}; botProfile={Safe(command.BotProfileId)}; lease={Safe(command.LeaseId)}; generation={command.Generation}; safetyHold=true; movementAuthorityAcquisitionSuppressed=true; commandReissuedUnderLayer=false; moverStopOnEntry={Bool(enteringHold)}; repeatedMoverStop=false; tag={IdempotentDriveStatusTag}");
            }
            state.Holding = true;
            result = enteringHold ? "holding_safety_backend_stopped_once" : "holding_safety_maintained_without_authority_reacquisition";
            return true;
        }

        bool transitionFromHolding = state.Holding;
        TransientActionBlock transientAction = CaptureTransientActionBlock(owner);
        ObserveTransientAction(state, command, transientAction, now, identityChanged || transitionFromHolding);
        bool authorityAcquiredThisCall = exactLayerCall
            && (identityChanged || transitionFromHolding || !state.MovementAuthorityAcquired);
        if (exactLayerCall)
        {
            state.LastBigBrainDriveAtUtc = now;
            if (authorityAcquiredThisCall)
            {
                state.MovementAuthorityAcquired = true;
                state.AuthorityAcquiredAtUtc = now;
                state.AuthorityReissueCount++;
                VanguardClientDiagnosticsLog.Operational(EmergencyAuthorityAcquiredTag, () =>
                    $"operator={Safe(command.OperatorId)}; botProfile={Safe(command.BotProfileId)}; lease={Safe(command.LeaseId)}; generation={command.Generation}; destination={VectorText(command.Anchor)}; authorityPendingAge={(state.AuthorityPendingStartedAtUtc == DateTimeOffset.MinValue ? 0d : Math.Max(0d, (now - state.AuthorityPendingStartedAtUtc).TotalSeconds)):0.00}; sameGenerationRetained={Bool(!newGeneration)}; sameAnchorRetained={Bool(!anchorChanged)}; commandReissuedUnderLayer={Bool(!transientAction.Active)}; transientActionBlocked={Bool(transientAction.Active)}; commandRetainedWithoutPhysicalIssue={Bool(transientAction.Active)}; acquisitionIdentity=lease_generation_anchor; acquisitionCountForIdentity=1; safetyHold=false; emergencyLayerPriority={VanguardReturnMovementLayer.LayerPriority}; sainMovementAuthority=false; tag={IdempotentDriveStatusTag}");
            }
        }

        if (transientAction.Active && (identityChanged || transitionFromHolding))
        {
            AdoptTransientBlockedIdentity(state, command, position, distanceToAnchor, now, exactLayerCall);
        }

        if (transientAction.Active)
        {
            bool transientActionExpired = state.TransientActionBlockedSinceUtc != DateTimeOffset.MinValue
                && now - state.TransientActionBlockedSinceUtc >= MaximumTransientActionReleaseGrace;
            if (!transientActionExpired)
            {
                result = "transient_action_blocked_command_retained_without_physical_issue";
                return true;
            }

            if (TryReportBoundedIgnitionFailure(owner, command, state, now, source, out string transientFailureResult))
            {
                result = transientFailureResult;
                return false;
            }

            result = "transient_action_expired_terminal_evaluation_pending";
            return true;
        }

        // Accumulate sub-threshold movement against a stable reference. Drive can be called by the
        // emergency service and the BigBrain layer in the same frame; overwriting the reference on
        // every call would hide real movement and manufacture false stalls.
        float moved = state.MovementReferenceAtUtc == DateTimeOffset.MinValue
            ? 0f
            : HorizontalDistance(position, state.MovementReferencePosition);
        float towardAnchorGain = float.IsPositiveInfinity(state.MovementReferenceDistanceToAnchor)
            ? 0f
            : state.MovementReferenceDistanceToAnchor - distanceToAnchor;
        if (moved >= ProgressMeters)
        {
            state.LastProgressAtUtc = now;
            state.LastMovementAtUtc = now;
            state.MovementReferencePosition = position;
            state.MovementReferenceDistanceToAnchor = distanceToAnchor;
            state.MovementReferenceAtUtc = now;
            if (towardAnchorGain >= 0.10f)
            {
                state.LastTowardAnchorProgressAtUtc = now;
            }
            if (!state.IgnitionConfirmed && towardAnchorGain >= 0.05f)
            {
                ConfirmIgnition(state, command, now, source, "operator_moved_toward_emergency_anchor");
            }
            if (state.LastProgressLogAtUtc == DateTimeOffset.MinValue || now - state.LastProgressLogAtUtc >= TimeSpan.FromSeconds(0.50d))
            {
                state.LastProgressLogAtUtc = now;
                bool pathValidatedRecent = state.LastPathValidAtUtc != DateTimeOffset.MinValue
                    && now - state.LastPathValidAtUtc <= PathValidityMemory;
                bool targetMatchedRecent = state.LastTargetMatchAtUtc != DateTimeOffset.MinValue
                    && now - state.LastTargetMatchAtUtc <= PathValidityMemory;
                VanguardClientDiagnosticsLog.Operational(ProgressTag, () =>
                    $"operator={Safe(command.OperatorId)}; botProfile={Safe(command.BotProfileId)}; lease={Safe(command.LeaseId)}; generation={command.Generation}; moved={moved:0.00}; towardAnchorGain={towardAnchorGain:0.00}; remainingToAnchor={distanceToAnchor:0.00}; pathValidatedRecent={Bool(pathValidatedRecent)}; moverTargetMatchedRecent={Bool(targetMatchedRecent)}; source={Safe(source)}; destination={VectorText(command.Anchor)}; continuous=true; tag={CleanupStatusTag}; foundationTag={StatusTag}");
            }
        }

        bool moverPathPresent = owner.Mover?.HasPathAndNoComplete == true;
        bool noPath = !moverPathPresent;
        bool targetMatches = TryReadTargetMatch(owner, command.Anchor, out bool targetKnown, out _);
        if (targetMatches)
        {
            state.LastTargetMatchAtUtc = now;
            state.LastKnownTargetMismatchAtUtc = DateTimeOffset.MinValue;
        }
        else if (targetKnown)
        {
            state.LastKnownTargetMismatchAtUtc = now;
        }
        if (!identityChanged
            && !transitionFromHolding
            && !state.IgnitionConfirmed
            && targetMatches)
        {
            ConfirmIgnition(state, command, now, source, "mover_target_matches_emergency_anchor");
        }

        bool targetMismatch = targetKnown && !targetMatches;
        bool stalled = state.IgnitionConfirmed
            && state.LastProgressAtUtc != DateTimeOffset.MinValue
            && now - state.LastProgressAtUtc >= StallReacquireInterval;
        bool issueReady = state.LastIssueAtUtc == DateTimeOffset.MinValue || now - state.LastIssueAtUtc >= MinimumIssueInterval;
        bool noPhysicalProof = state.ObservedSpeedMetersPerSecond < PhysicalSpeedEvidenceMetersPerSecond
            && state.MaxDisplacementFromIgnitionOrigin < PhysicalDisplacementEvidenceMeters
            && state.MaxTowardAnchorDisplacement < PhysicalTowardAnchorEvidenceMeters;
        bool transientReleaseReissue = !transientAction.Active
            && state.TransientActionObserved
            && state.TransientActionReleasedAtUtc != DateTimeOffset.MinValue
            && state.RecoveryReissueCount == 0;
        bool preIgnitionRecoveryReissue = !state.IgnitionConfirmed
            && !transientAction.Active
            && state.MovementAuthorityAcquired
            && state.AuthorityAcquiredAtUtc != DateTimeOffset.MinValue
            && state.RecoveryReissueCount == 0
            && (transientReleaseReissue || now - state.AuthorityAcquiredAtUtc >= ConditionalRecoveryReissueDelay)
            && issueReady
            && noPhysicalProof
            && (transientReleaseReissue || noPath || !targetMatches);
        bool confirmedStallRecoveryReissue = state.IgnitionConfirmed
            && state.RecoveryReissueCount == 0
            && issueReady
            && (targetMismatch || stalled);
        bool recoveryReissue = preIgnitionRecoveryReissue || confirmedStallRecoveryReissue;
        bool mustIssue = !transientAction.Active
            && (identityChanged
                || transitionFromHolding
                || authorityAcquiredThisCall
                || recoveryReissue);

        if (mustIssue)
        {
            // A new generation or material retarget is the only destructive authority boundary.
            // Same-anchor ignition retries and stall recovery force the emergency destination back into
            // the shared EFT mover without Stop(), so a live SAIN chase cannot regain locomotion between
            // frames and the Operator does not reproduce the measured run/stop cadence.
            owner.GoToSomePointData?.UpdateToGo(false);
            bool atomicReplacement = identityChanged || transitionFromHolding;
            if (atomicReplacement)
            {
                owner.Mover?.Stop();
                state.IgnitionStartedAtUtc = now;
                state.IgnitionAttemptCount = 0;
                state.IgnitionConfirmed = false;
                state.LastIgnitionPendingLogAtUtc = DateTimeOffset.MinValue;
                state.LastIgnitionConfirmationLogAtUtc = DateTimeOffset.MinValue;
                state.LastPathCheckAtUtc = now;
                state.LastPathValidAtUtc = DateTimeOffset.MinValue;
                state.LastTargetMatchAtUtc = DateTimeOffset.MinValue;
                state.LastKnownTargetMismatchAtUtc = DateTimeOffset.MinValue;
                state.LastBigBrainDriveAtUtc = exactLayerCall ? now : DateTimeOffset.MinValue;
                state.AuthorityPendingStartedAtUtc = now;
                state.AuthorityAcquiredAtUtc = exactLayerCall ? now : DateTimeOffset.MinValue;
                state.LastAuthorityPendingLogAtUtc = DateTimeOffset.MinValue;
                state.LastRecoveryReissueAtUtc = DateTimeOffset.MinValue;
                state.AuthorityReissueCount = exactLayerCall ? 1 : 0;
                state.RecoveryReissueCount = 0;
                state.MovementAuthorityAcquired = exactLayerCall;
                state.LastSainExclusionLogAtUtc = DateTimeOffset.MinValue;
                state.IgnitionOriginPosition = position;
                state.LastPhysicalSamplePosition = position;
                state.LastPhysicalSampleAtUtc = now;
                state.ObservedSpeedMetersPerSecond = 0f;
                state.MaxDisplacementFromIgnitionOrigin = 0f;
                state.IgnitionOriginDistanceToAnchor = distanceToAnchor;
                state.MaxTowardAnchorDisplacement = 0f;
            }

            EmergencyPathIssue issue = ApplyForcedEmergencyPath(owner, command.Anchor, exactLayerAuthority: exactLayerCall);
            NavMeshPathStatus status = issue.Status;
            state.IgnitionAttemptCount++;

            bool reacquired = !identityChanged && !transitionFromHolding;
            if (reacquired)
            {
                state.ReacquireCount++;
            }
            state.LeaseId = command.LeaseId;
            state.Generation = command.Generation;
            state.Anchor = command.Anchor;
            state.LastIssueAtUtc = now;
            if (recoveryReissue)
            {
                state.RecoveryReissueCount++;
                state.LastRecoveryReissueAtUtc = now;
                VanguardClientDiagnosticsLog.Operational(EmergencyRecoveryReissueTag, () =>
                    $"operator={Safe(command.OperatorId)}; botProfile={Safe(command.BotProfileId)}; lease={Safe(command.LeaseId)}; generation={command.Generation}; destination={VectorText(command.Anchor)}; recoveryReissueCount={state.RecoveryReissueCount}; recoveryBudget=1; reason={(transientReleaseReissue ? "transient_action_released_immediate_reissue" : preIgnitionRecoveryReissue ? "pre_ignition_no_backend_proof" : "confirmed_path_stall_or_target_mismatch")}; sameGenerationRetained=true; sameAnchorRetained=true; destructiveStop=false; tag={IdempotentDriveStatusTag}");
            }
            if (identityChanged)
            {
                state.LastProgressAtUtc = now;
                state.LastMovementAtUtc = DateTimeOffset.MinValue;
                state.LastTowardAnchorProgressAtUtc = DateTimeOffset.MinValue;
                state.MovementReferencePosition = position;
                state.MovementReferenceDistanceToAnchor = distanceToAnchor;
                state.MovementReferenceAtUtc = now;
                state.CurrentDistanceToAnchor = distanceToAnchor;
                state.ReacquireCount = 0;
            }
            state.Holding = false;

            bool appliedTargetMatches = TryReadTargetMatch(owner, command.Anchor, out bool appliedTargetKnown, out float appliedTargetDistance);
            bool appliedMoverPathPresent = owner.Mover?.HasPathAndNoComplete == true;
            if (appliedTargetMatches)
            {
                state.LastTargetMatchAtUtc = now;
                state.LastKnownTargetMismatchAtUtc = DateTimeOffset.MinValue;
            }
            else if (appliedTargetKnown)
            {
                state.LastKnownTargetMismatchAtUtc = now;
            }
            if (status == NavMeshPathStatus.PathComplete)
            {
                state.LastPathValidAtUtc = now;
            }

            bool immediateMoverPathProof = status == NavMeshPathStatus.PathComplete && appliedMoverPathPresent;
            bool immediateConfirmation = appliedTargetMatches || immediateMoverPathProof;
            if (immediateConfirmation)
            {
                ConfirmIgnition(state, command, now, source, appliedTargetMatches
                    ? "mover_target_matches_after_issue"
                    : "go_to_point_complete_and_mover_path_active");
            }

            bool pendingAfterIssue = !state.IgnitionConfirmed && IsIgnitionPending(state, now);
            double ignitionAgeSeconds = state.IgnitionStartedAtUtc == DateTimeOffset.MinValue
                ? 0d
                : Math.Max(0d, (now - state.IgnitionStartedAtUtc).TotalSeconds);
            bool hardNoMoverProof = !state.IgnitionConfirmed
                && ignitionAgeSeconds >= HardNoIgnitionFailureAge.TotalSeconds
                && state.IgnitionAttemptCount >= 2
                && !appliedMoverPathPresent
                && !appliedTargetMatches
                && state.ObservedSpeedMetersPerSecond < PhysicalSpeedEvidenceMetersPerSecond
                && state.MaxDisplacementFromIgnitionOrigin < PhysicalDisplacementEvidenceMeters
                && state.MaxTowardAnchorDisplacement < PhysicalTowardAnchorEvidenceMeters;
            bool immediatePathInvalidNoMoverProof = !state.IgnitionConfirmed
                && status == NavMeshPathStatus.PathInvalid
                && !appliedMoverPathPresent
                && !appliedTargetMatches
                && state.ObservedSpeedMetersPerSecond < PhysicalSpeedEvidenceMetersPerSecond
                && state.MaxDisplacementFromIgnitionOrigin < PhysicalDisplacementEvidenceMeters
                && state.MaxTowardAnchorDisplacement < PhysicalTowardAnchorEvidenceMeters;
            result = (reacquired ? "reacquired" : "applied") + ":status=" + status
                + ":targetKnown=" + Bool(appliedTargetKnown)
                + ":targetMatch=" + Bool(appliedTargetMatches)
                + ":targetDistance=" + appliedTargetDistance.ToString("0.00", CultureInfo.InvariantCulture)
                + ":moverPath=" + Bool(appliedMoverPathPresent)
                + ":observedSpeed=" + state.ObservedSpeedMetersPerSecond.ToString("0.00", CultureInfo.InvariantCulture)
                + ":displacement=" + state.MaxDisplacementFromIgnitionOrigin.ToString("0.00", CultureInfo.InvariantCulture)
                + ":towardAnchor=" + state.MaxTowardAnchorDisplacement.ToString("0.00", CultureInfo.InvariantCulture)
                + ":ignition=" + (state.IgnitionConfirmed ? "confirmed" : pendingAfterIssue ? "pending" : "unconfirmed")
                + ":backend=" + issue.Backend
                + ":nativeFallback=" + Bool(issue.NativeFallbackAttempted)
                + ":moverState=" + issue.MoverState;
            string tag = reacquired ? ReacquiredTag : AppliedTag;
            VanguardClientDiagnosticsLog.Operational(tag, () =>
                $"operator={Safe(command.OperatorId)}; botProfile={Safe(command.BotProfileId)}; lease={Safe(command.LeaseId)}; generation={command.Generation}; destination={VectorText(command.Anchor)}; source={Safe(source)}; oldPathStopped={Bool(atomicReplacement)}; pathIssueApplied=true; issueBackend={Safe(issue.Backend)}; coreGraphStatus={issue.CoreGraphStatus}; nativeNavMeshFallbackAttempted={Bool(issue.NativeFallbackAttempted)}; nativeFallbackStatus={issue.NativeFallbackStatus}; moverState={Safe(issue.MoverState)}; exactLayerAuthority={Bool(exactLayerCall)}; force=true; mustHaveWay=false; moverTargetKnown={Bool(appliedTargetKnown)}; moverTargetMatch={Bool(appliedTargetMatches)}; moverTargetDistance={appliedTargetDistance:0.00}; moverPathPresent={Bool(appliedMoverPathPresent)}; observedSpeed={state.ObservedSpeedMetersPerSecond:0.00}; displacementFromIgnition={state.MaxDisplacementFromIgnitionOrigin:0.00}; towardAnchorDisplacement={state.MaxTowardAnchorDisplacement:0.00}; slowAtEnd=false; sprint=true; status={status}; ignitionConfirmed={Bool(state.IgnitionConfirmed)}; ignitionAttempt={state.IgnitionAttemptCount}; reacquireCount={state.ReacquireCount}; singleEmission=true; sainTargetPreserved=true; sainDecisionPreserved=true; tag={LocomotionAuthorityStatusTag}; Tag={TransientReleaseStatusTag}; ignitionTag={IgnitionGraceStatusTag}; cleanupTag={CleanupStatusTag}; foundationTag={StatusTag}");

            if (!state.IgnitionConfirmed && immediatePathInvalidNoMoverProof)
            {
                bool independentPathComplete = VanguardReturnMovementCommandStore.ValidatePathStillComplete(owner, command.Anchor, out string pathSummary);
                if (!independentPathComplete)
                {
                    string failure = "anchor_navmesh_became_invalid_after_issue:" + pathSummary;
                    VanguardClientDiagnosticsLog.Warning(IgnitionFailedTag,
                        $"operator={Safe(command.OperatorId)}; botProfile={Safe(command.BotProfileId)}; lease={Safe(command.LeaseId)}; generation={command.Generation}; ignitionAge={ignitionAgeSeconds:0.00}; attempts={state.IgnitionAttemptCount}; recoveryReissues={state.RecoveryReissueCount}; status={status}; independentNavMeshPathComplete=false; failureKind=anchor_navmesh_became_invalid_after_issue; backendFailure=false; anchorFailure=true; moverStopHere=false; tag={IdempotentDriveStatusTag}");
                    VanguardReturnMovementCommandStore.ReportPathInvalid(command, now, "" + failure);
                    Release(command.BotProfileId);
                    result += ":anchor_failure_reported";
                    return false;
                }

                bool exactLayerActive = state.LastBigBrainDriveAtUtc != DateTimeOffset.MinValue
                    && now - state.LastBigBrainDriveAtUtc <= BigBrainOwnershipMemory;
                if (!state.MovementAuthorityAcquired || !exactLayerActive)
                {
                    if (state.LastAuthorityPendingLogAtUtc == DateTimeOffset.MinValue
                        || now - state.LastAuthorityPendingLogAtUtc >= IgnitionPendingLogInterval)
                    {
                        state.LastAuthorityPendingLogAtUtc = now;
                        VanguardClientDiagnosticsLog.Operational(EmergencyAuthorityPendingTag, () =>
                            $"operator={Safe(command.OperatorId)}; botProfile={Safe(command.BotProfileId)}; lease={Safe(command.LeaseId)}; generation={command.Generation}; destination={VectorText(command.Anchor)}; ignitionAge={ignitionAgeSeconds:0.00}; status={status}; independentNavMeshPathComplete=true; exactLayerActive={Bool(exactLayerActive)}; movementAuthorityAcquired={Bool(state.MovementAuthorityAcquired)}; commandStored=true; commandRetained=true; generationRetained=true; anchorRetained=true; anchorQuarantined=false; replan=false; waitingForBigBrainLayer=true; emergencyLayerPriority={VanguardReturnMovementLayer.LayerPriority}; tag={IdempotentDriveStatusTag}");
                    }
                    result += ":authority_pending_command_retained";
                    LogIgnitionPending(state, command, now, source, status, appliedTargetKnown, appliedTargetMatches, appliedMoverPathPresent, appliedTargetDistance, "authority_pending_waiting_for_exact_bigbrain_layer");
                    return true;
                }

                DateTimeOffset settleStartedAt = state.LastRecoveryReissueAtUtc != DateTimeOffset.MinValue
                    ? state.LastRecoveryReissueAtUtc
                    : state.AuthorityAcquiredAtUtc;
                if (settleStartedAt != DateTimeOffset.MinValue && now - settleStartedAt <= PostAuthorityAcquisitionGrace)
                {
                    LogIgnitionPending(state, command, now, source, status, appliedTargetKnown, appliedTargetMatches, appliedMoverPathPresent, appliedTargetDistance,
                        state.LastRecoveryReissueAtUtc != DateTimeOffset.MinValue
                            ? "single_recovery_reissue_bounded_backend_settle_window"
                            : "exact_layer_acquired_bounded_backend_settle_window");
                    result += ":authority_acquired_backend_settle_pending";
                    return true;
                }
            }

            if (!state.IgnitionConfirmed)
            {
                LogIgnitionPending(state, command, now, source, status, appliedTargetKnown, appliedTargetMatches, appliedMoverPathPresent, appliedTargetDistance,
                    hardNoMoverProof ? "hard_no_mover_proof" : "independent_navmesh_is_plan_only_waiting_for_physical_or_mover_proof");
            }
        }

        if (TryReportBoundedIgnitionFailure(owner, command, state, now, source, out string boundedFailureResult))
        {
            result = boundedFailureResult;
            return false;
        }

        bool pathCheckDelayElapsed = state.IgnitionStartedAtUtc == DateTimeOffset.MinValue
            || now - state.IgnitionStartedAtUtc >= TimeSpan.FromSeconds(VanguardGrenadeEmergencyPolicy.PhysicalIgnitionPathCheckDelaySeconds);
        if (pathCheckDelayElapsed
            && (state.LastPathCheckAtUtc == DateTimeOffset.MinValue || now - state.LastPathCheckAtUtc >= TimeSpan.FromSeconds(0.40d)))
        {
            state.LastPathCheckAtUtc = now;
            bool pathCheckTargetMatches = TryReadTargetMatch(owner, command.Anchor, out bool pathCheckTargetKnown, out float pathCheckTargetDistance);
            if (pathCheckTargetMatches)
            {
                state.LastTargetMatchAtUtc = now;
                state.LastKnownTargetMismatchAtUtc = DateTimeOffset.MinValue;
            }
            else if (pathCheckTargetKnown)
            {
                state.LastKnownTargetMismatchAtUtc = now;
            }
            bool pathCheckMoverPathPresent = owner.Mover?.HasPathAndNoComplete == true;
            if (!VanguardReturnMovementCommandStore.ValidatePathStillComplete(owner, command.Anchor, out string pathSummary))
            {
                if (IsIgnitionPending(state, now))
                {
                    LogIgnitionPending(state, command, now, source, NavMeshPathStatus.PathInvalid, pathCheckTargetKnown, pathCheckTargetMatches, pathCheckMoverPathPresent, pathCheckTargetDistance, "navmesh_check_pending:" + pathSummary);
                }
                else
                {
                    bool exactLayerActive = state.LastBigBrainDriveAtUtc != DateTimeOffset.MinValue
                        && now - state.LastBigBrainDriveAtUtc <= BigBrainOwnershipMemory;
                    bool knownTargetConflict = pathCheckTargetKnown && !pathCheckTargetMatches;
                    if (state.IgnitionConfirmed && exactLayerActive && !knownTargetConflict)
                    {
                        // Once exact-layer ignition has been proven, one independent NavMesh readback
                        // must not cut the mover mid-stride. Keep the exact command alive and let the
                        // existing bounded physical no-progress guard decide whether a destructive
                        // replan is actually required.
                        VanguardClientDiagnosticsLog.Operational(PathInvalidDeferredTag, () =>
                            $"operator={Safe(command.OperatorId)}; botProfile={Safe(command.BotProfileId)}; lease={Safe(command.LeaseId)}; generation={command.Generation}; path={Safe(pathSummary)}; exactLayerActive=true; ignitionConfirmed=true; knownMoverTargetConflict=false; commandRetained=true; moverStop=false; decision=defer_to_physical_no_progress_guard; tag={IgnitionGraceStatusTag}; foundationTag={StatusTag}");
                        result = "path_invalid_deferred_to_physical_no_progress_guard:" + pathSummary;
                    }
                    else
                    {
                        VanguardClientDiagnosticsLog.Warning(IgnitionFailedTag,
                            $"operator={Safe(command.OperatorId)}; botProfile={Safe(command.BotProfileId)}; lease={Safe(command.LeaseId)}; generation={command.Generation}; attempts={state.IgnitionAttemptCount}; path={Safe(pathSummary)}; exactLayerActive={Bool(exactLayerActive)}; moverTargetKnown={Bool(pathCheckTargetKnown)}; moverTargetMatch={Bool(pathCheckTargetMatches)}; action=release_and_replan; tag={IgnitionGraceStatusTag}; foundationTag={StatusTag}");
                        VanguardReturnMovementCommandStore.ReportPathInvalid(command, now, "physical_lease:" + pathSummary);
                        Release(command.BotProfileId);
                        result = "path_invalid_reported_single_cleanup_deferred_to_emergency_service:" + pathSummary;
                        return false;
                    }
                }
            }
            else
            {
                state.LastPathValidAtUtc = now;
                bool pathCheckBigBrainRecent = state.LastBigBrainDriveAtUtc != DateTimeOffset.MinValue
                    && now - state.LastBigBrainDriveAtUtc <= BigBrainOwnershipMemory;
                bool pathCheckKnownTargetConflict = pathCheckTargetKnown && !pathCheckTargetMatches;
                if (!state.IgnitionConfirmed
                    && pathCheckBigBrainRecent
                    && !pathCheckKnownTargetConflict
                    && pathCheckMoverPathPresent)
                {
                    ConfirmIgnition(state, command, now, source, pathCheckTargetMatches
                        ? "exact_layer_mover_target_match_and_path_active"
                        : "exact_layer_mover_path_active_and_navmesh_complete");
                }
                else if (!state.IgnitionConfirmed)
                {
                    LogIgnitionPending(state, command, now, source, NavMeshPathStatus.PathComplete, pathCheckTargetKnown, pathCheckTargetMatches, pathCheckMoverPathPresent, pathCheckTargetDistance, "navmesh_complete_without_mover_or_physical_proof_not_accepted");
                }
            }
        }

        bool emergencyLayerOwnsMovement = state.Generation == command.Generation
            && state.LastBigBrainDriveAtUtc != DateTimeOffset.MinValue
            && now - state.LastBigBrainDriveAtUtc <= BigBrainOwnershipMemory;
        if (emergencyLayerOwnsMovement
            && (state.LastSainExclusionLogAtUtc == DateTimeOffset.MinValue
                || now - state.LastSainExclusionLogAtUtc >= TimeSpan.FromSeconds(0.50d)))
        {
            state.LastSainExclusionLogAtUtc = now;
            VanguardClientDiagnosticsLog.Operational(SainChaseExcludedTag, () =>
                $"operator={Safe(command.OperatorId)}; botProfile={Safe(command.BotProfileId)}; lease={Safe(command.LeaseId)}; generation={command.Generation}; source={Safe(source)}; emergencyLayerPriority={VanguardReturnMovementLayer.LayerPriority}; exactEmergencyCommandRetained=true; sainTargetPreserved=true; sainDecisionPreserved=true; sainMovementAuthority=false; destination={VectorText(command.Anchor)}; tag={IgnitionGraceStatusTag}; foundationTag={StatusTag}");
        }

        float speed = VanguardContinuousCohesionLocomotionPolicy.ResolveTargetMoveSpeed(true);
        owner.SetPose(1f);
        owner.SetTargetMoveSpeed(speed);
        owner.Sprint(true, true);
        owner.Mover?.Sprint(true, false);
        owner.Mover?.SetTargetMoveSpeed(speed);
        owner.Steering?.LookToMovingDirection(60f);

        state.CurrentDistanceToAnchor = distanceToAnchor;
        if (state.MovementReferenceAtUtc == DateTimeOffset.MinValue)
        {
            state.MovementReferencePosition = position;
            state.MovementReferenceDistanceToAnchor = distanceToAnchor;
            state.MovementReferenceAtUtc = now;
        }
        if (state.LastProgressAtUtc == DateTimeOffset.MinValue)
        {
            state.LastProgressAtUtc = now;
        }
        return true;
    }

    public static bool IsIgnitionPending(string botProfileId, long generation, DateTimeOffset now, out string summary)
    {
        summary = "physical_state_missing";
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return false;
        }

        lock (Sync)
        {
            if (!States.TryGetValue(botProfileId, out DriveState state) || state.Generation != generation)
            {
                return false;
            }

            if (state.IgnitionConfirmed || state.IgnitionStartedAtUtc == DateTimeOffset.MinValue)
            {
                summary = state.IgnitionConfirmed ? "ignition_confirmed" : "ignition_not_started";
                return false;
            }

            TimeSpan age = now - state.IgnitionStartedAtUtc;
            bool pending = age <= TimeSpan.FromSeconds(VanguardGrenadeEmergencyPolicy.PhysicalIgnitionGraceSeconds);
            summary = "ignition_age=" + age.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
                + ";attempts=" + state.IgnitionAttemptCount.ToString(CultureInfo.InvariantCulture)
                + ";lastIssueAge=" + (state.LastIssueAtUtc == DateTimeOffset.MinValue ? "unknown" : (now - state.LastIssueAtUtc).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture))
                + ";pending=" + Bool(pending);
            return pending;
        }
    }

    public static bool IsPhysicalPathProgressRecent(string botProfileId, long generation, DateTimeOffset now, TimeSpan maximumAge, out string summary)
    {
        summary = "physical_state_missing";
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return false;
        }

        lock (Sync)
        {
            if (!States.TryGetValue(botProfileId, out DriveState state) || state.Generation != generation)
            {
                return false;
            }

            if (state.LastMovementAtUtc == DateTimeOffset.MinValue)
            {
                summary = "physical_movement_not_observed";
                return false;
            }

            TimeSpan movementAge = now - state.LastMovementAtUtc;
            TimeSpan pathAge = state.LastPathValidAtUtc == DateTimeOffset.MinValue
                ? TimeSpan.MaxValue
                : now - state.LastPathValidAtUtc;
            TimeSpan targetAge = state.LastTargetMatchAtUtc == DateTimeOffset.MinValue
                ? TimeSpan.MaxValue
                : now - state.LastTargetMatchAtUtc;
            bool towardRecent = state.LastTowardAnchorProgressAtUtc != DateTimeOffset.MinValue
                && now - state.LastTowardAnchorProgressAtUtc <= maximumAge;
            bool bigBrainRecent = state.LastBigBrainDriveAtUtc != DateTimeOffset.MinValue
                && now - state.LastBigBrainDriveAtUtc <= BigBrainOwnershipMemory;
            bool exactIssueRecent = state.LastIssueAtUtc != DateTimeOffset.MinValue
                && now - state.LastIssueAtUtc <= PathValidityMemory;
            bool knownTargetConflictRecent = state.LastKnownTargetMismatchAtUtc != DateTimeOffset.MinValue
                && now - state.LastKnownTargetMismatchAtUtc <= PathValidityMemory;
            bool exactLayerUnknownTargetDetour = bigBrainRecent && exactIssueRecent && !knownTargetConflictRecent;
            bool accepted = movementAge <= maximumAge
                && pathAge <= PathValidityMemory
                && (towardRecent || targetAge <= PathValidityMemory || exactLayerUnknownTargetDetour);
            summary = "physical_movement_age=" + movementAge.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
                + ";path_valid_age=" + (pathAge == TimeSpan.MaxValue ? "unknown" : pathAge.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture))
                + ";mover_target_match_age=" + (targetAge == TimeSpan.MaxValue ? "unknown" : targetAge.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture))
                + ";bigbrain_layer_age=" + (state.LastBigBrainDriveAtUtc == DateTimeOffset.MinValue ? "unknown" : (now - state.LastBigBrainDriveAtUtc).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture))
                + ";exact_issue_age=" + (state.LastIssueAtUtc == DateTimeOffset.MinValue ? "unknown" : (now - state.LastIssueAtUtc).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture))
                + ";known_target_conflict_recent=" + Bool(knownTargetConflictRecent)
                + ";progressKind=" + (towardRecent ? "toward_anchor" : targetAge <= PathValidityMemory ? "target_matched_detour" : "exact_layer_unknown_target_detour")
                + ";remaining=" + state.CurrentDistanceToAnchor.ToString("0.00", CultureInfo.InvariantCulture)
                + ";reacquireCount=" + state.ReacquireCount.ToString(CultureInfo.InvariantCulture);
            return accepted;
        }
    }

    public static bool IsTowardAnchorProgressRecent(string botProfileId, long generation, DateTimeOffset now, TimeSpan maximumAge, out string summary)
    {
        summary = "physical_state_missing";
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return false;
        }

        lock (Sync)
        {
            if (!States.TryGetValue(botProfileId, out DriveState state) || state.Generation != generation)
            {
                return false;
            }

            if (state.LastTowardAnchorProgressAtUtc == DateTimeOffset.MinValue)
            {
                summary = "toward_anchor_progress_not_observed";
                return false;
            }

            TimeSpan age = now - state.LastTowardAnchorProgressAtUtc;
            summary = "toward_anchor_progress_age=" + age.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
                + ";remaining=" + state.CurrentDistanceToAnchor.ToString("0.00", CultureInfo.InvariantCulture)
                + ";reacquireCount=" + state.ReacquireCount.ToString(CultureInfo.InvariantCulture);
            return age <= maximumAge;
        }
    }

    private static void AdoptTransientBlockedIdentity(
        DriveState state,
        VanguardReturnMovementCommand command,
        Vector3 position,
        float distanceToAnchor,
        DateTimeOffset now,
        bool exactLayerCall)
    {
        // A native grenade throw already in hands is a bounded non-preemptable action. Adopt the
        // emergency identity without Stop()/GoToPoint so the throw can reach its native release
        // boundary. The exact runtime identity and recovery budget are then reused immediately after
        // release; no second emergency transaction is created.
        state.LeaseId = command.LeaseId;
        state.Generation = command.Generation;
        state.Anchor = command.Anchor;
        state.Holding = false;
        state.LastIssueAtUtc = DateTimeOffset.MinValue;
        state.MovementReferencePosition = position;
        state.MovementReferenceDistanceToAnchor = distanceToAnchor;
        state.CurrentDistanceToAnchor = distanceToAnchor;
        state.MovementReferenceAtUtc = now;
        state.LastProgressAtUtc = now;
        state.LastMovementAtUtc = DateTimeOffset.MinValue;
        state.LastTowardAnchorProgressAtUtc = DateTimeOffset.MinValue;
        state.LastPathValidAtUtc = DateTimeOffset.MinValue;
        state.LastTargetMatchAtUtc = DateTimeOffset.MinValue;
        state.LastKnownTargetMismatchAtUtc = DateTimeOffset.MinValue;
        state.LastPathCheckAtUtc = now;
        state.IgnitionStartedAtUtc = now;
        state.LastIgnitionPendingLogAtUtc = DateTimeOffset.MinValue;
        state.LastIgnitionConfirmationLogAtUtc = DateTimeOffset.MinValue;
        state.LastBigBrainDriveAtUtc = exactLayerCall ? now : DateTimeOffset.MinValue;
        state.AuthorityPendingStartedAtUtc = now;
        state.AuthorityAcquiredAtUtc = exactLayerCall ? now : DateTimeOffset.MinValue;
        state.LastAuthorityPendingLogAtUtc = DateTimeOffset.MinValue;
        state.LastRecoveryReissueAtUtc = DateTimeOffset.MinValue;
        state.LastSainExclusionLogAtUtc = DateTimeOffset.MinValue;
        state.IgnitionAttemptCount = 0;
        state.AuthorityReissueCount = exactLayerCall ? Math.Max(1, state.AuthorityReissueCount) : 0;
        state.RecoveryReissueCount = 0;
        state.ReacquireCount = 0;
        state.IgnitionOriginPosition = position;
        state.LastPhysicalSamplePosition = position;
        state.LastPhysicalSampleAtUtc = now;
        state.ObservedSpeedMetersPerSecond = 0f;
        state.MaxDisplacementFromIgnitionOrigin = 0f;
        state.IgnitionOriginDistanceToAnchor = distanceToAnchor;
        state.MaxTowardAnchorDisplacement = 0f;
        state.IgnitionConfirmed = false;
        state.MovementAuthorityAcquired = exactLayerCall;
    }

    private static void ObserveTransientAction(
        DriveState state,
        VanguardReturnMovementCommand command,
        TransientActionBlock transientAction,
        DateTimeOffset now,
        bool resetIdentity)
    {
        if (resetIdentity)
        {
            state.TransientActionBlockedSinceUtc = transientAction.Active ? now : DateTimeOffset.MinValue;
            state.TransientActionReleasedAtUtc = DateTimeOffset.MinValue;
            state.LastTransientActionLogAtUtc = DateTimeOffset.MinValue;
            state.TransientActionObserved = transientAction.Active;
        }
        else if (transientAction.Active)
        {
            if (state.TransientActionBlockedSinceUtc == DateTimeOffset.MinValue)
            {
                state.TransientActionBlockedSinceUtc = now;
            }
            state.TransientActionObserved = true;
            state.TransientActionReleasedAtUtc = DateTimeOffset.MinValue;
        }
        else if (state.TransientActionObserved
            && state.TransientActionBlockedSinceUtc != DateTimeOffset.MinValue
            && state.TransientActionReleasedAtUtc == DateTimeOffset.MinValue)
        {
            state.TransientActionReleasedAtUtc = now;
            VanguardClientDiagnosticsLog.Operational(TransientActionReleasedTag, () =>
                $"operator={Safe(command.OperatorId)}; botProfile={Safe(command.BotProfileId)}; lease={Safe(command.LeaseId)}; generation={command.Generation}; destination={VectorText(command.Anchor)}; blockedSeconds={(now - state.TransientActionBlockedSinceUtc).TotalSeconds:0.00}; sameIdentityRetained=true; immediateRecoveryReissueEligible={Bool(state.RecoveryReissueCount == 0)}; recoveryBudget=1; tag={TransientReleaseStatusTag}; idempotenceTag={IdempotentDriveStatusTag}");
        }

        if (transientAction.Active
            && (state.LastTransientActionLogAtUtc == DateTimeOffset.MinValue
                || now - state.LastTransientActionLogAtUtc >= TransientActionLogInterval))
        {
            state.LastTransientActionLogAtUtc = now;
            VanguardClientDiagnosticsLog.Operational(TransientActionBlockedTag, () =>
                $"operator={Safe(command.OperatorId)}; botProfile={Safe(command.BotProfileId)}; lease={Safe(command.LeaseId)}; generation={command.Generation}; destination={VectorText(command.Anchor)}; reason={Safe(transientAction.Reason)}; hands={Safe(transientAction.HandsType)}; grenadeThrowing={Bool(transientAction.GrenadeThrowing)}; blockedSeconds={(state.TransientActionBlockedSinceUtc == DateTimeOffset.MinValue ? 0d : (now - state.TransientActionBlockedSinceUtc).TotalSeconds):0.00}; maxGrace={MaximumTransientActionReleaseGrace.TotalSeconds:0.00}; commandRetained=true; reissueSuppressedWhileBlocked=true; failureClockPaused=true; tag={TransientReleaseStatusTag}; idempotenceTag={IdempotentDriveStatusTag}");
        }
    }

    private static TransientActionBlock CaptureTransientActionBlock(BotOwner owner)
    {
        bool grenadeThrowing = false;
        try { grenadeThrowing = owner.WeaponManager?.Grenades?.ThrowindNow == true; }
        catch { }

        string handsType = "none";
        try { handsType = owner.GetPlayer?.HandsController?.GetType().Name ?? "none"; }
        catch { }
        bool grenadeHands = handsType.IndexOf("Grenade", StringComparison.OrdinalIgnoreCase) >= 0;
        bool active = grenadeThrowing || grenadeHands;
        string reason = grenadeThrowing && grenadeHands
            ? "grenade_throw_flag_and_grenade_hands"
            : grenadeThrowing
                ? "grenade_throw_flag"
                : grenadeHands
                    ? "grenade_hands_controller"
                    : "none";
        return new TransientActionBlock(active, grenadeThrowing, grenadeHands, handsType, reason);
    }

    private readonly struct TransientActionBlock
    {
        public TransientActionBlock(bool active, bool grenadeThrowing, bool grenadeHands, string handsType, string reason)
        {
            Active = active;
            GrenadeThrowing = grenadeThrowing;
            GrenadeHands = grenadeHands;
            HandsType = handsType;
            Reason = reason;
        }

        public bool Active { get; }
        public bool GrenadeThrowing { get; }
        public bool GrenadeHands { get; }
        public string HandsType { get; }
        public string Reason { get; }
    }

    private static EmergencyPathIssue ApplyForcedEmergencyPath(BotOwner owner, Vector3 anchor, bool exactLayerAuthority)
    {
        BotMover? mover = owner.Mover;
        if (mover == null)
        {
            NavMeshPathStatus ownerStatus = owner.GoToPoint(
                anchor,
                false,
                VanguardGrenadeEmergencyPolicy.EmergencyReachDistanceMeters,
                false,
                false);
            return new EmergencyPathIssue(
                ownerStatus,
                "bot_owner_default_state_last_resort",
                ownerStatus,
                false,
                NavMeshPathStatus.PathInvalid,
                "mover_unavailable");
        }

        string moverState = mover.CurrentState.ToString();

        // The runtime writes into the mover state that owns TargetPoint/HasPathAndNoComplete and therefore the
        // physical proof surface. BotOwner.GoToPoint and BotMover.GoToPoint target DefaultMoverState;
        // that is not authoritative while EFT is in another mover state.
        NavMeshPathStatus coreGraphStatus = mover.CurrentStateGoToPoint(
            anchor,
            false,
            VanguardGrenadeEmergencyPolicy.EmergencyReachDistanceMeters,
            false,
            false,
            false,
            true);
        if (coreGraphStatus != NavMeshPathStatus.PathInvalid)
        {
            return new EmergencyPathIssue(
                coreGraphStatus,
                "current_state_core_graph",
                coreGraphStatus,
                false,
                NavMeshPathStatus.PathInvalid,
                moverState);
        }

        // The anchor has already passed Vanguard's independent NavMesh proof. EFT's core/cover graph can
        // still reject it. In the same logical emission, use EFT's own current-state native NavMesh path
        // fallback. This preserves the runtime one-acquisition/one-reissue budget and never teleports because
        // mustHaveWay remains false.
        NavMeshPathStatus nativeFallbackStatus = mover.ActualPathFinder.GoToPosition(
            anchor,
            false,
            VanguardGrenadeEmergencyPolicy.EmergencyReachDistanceMeters,
            false,
            false,
            false,
            true,
            true);
        return new EmergencyPathIssue(
            nativeFallbackStatus,
            nativeFallbackStatus == NavMeshPathStatus.PathInvalid
                ? "current_state_native_navmesh_fallback_failed"
                : "current_state_native_navmesh_fallback",
            coreGraphStatus,
            true,
            nativeFallbackStatus,
            moverState + (exactLayerAuthority ? "_exact_layer" : "_pre_layer_probe"));
    }

    private readonly struct EmergencyPathIssue
    {
        public EmergencyPathIssue(
            NavMeshPathStatus status,
            string backend,
            NavMeshPathStatus coreGraphStatus,
            bool nativeFallbackAttempted,
            NavMeshPathStatus nativeFallbackStatus,
            string moverState)
        {
            Status = status;
            Backend = backend;
            CoreGraphStatus = coreGraphStatus;
            NativeFallbackAttempted = nativeFallbackAttempted;
            NativeFallbackStatus = nativeFallbackStatus;
            MoverState = moverState;
        }

        public NavMeshPathStatus Status { get; }
        public string Backend { get; }
        public NavMeshPathStatus CoreGraphStatus { get; }
        public bool NativeFallbackAttempted { get; }
        public NavMeshPathStatus NativeFallbackStatus { get; }
        public string MoverState { get; }
    }

    private static bool IsIgnitionPending(DriveState state, DateTimeOffset now)
    {
        if (state.IgnitionConfirmed || state.IgnitionStartedAtUtc == DateTimeOffset.MinValue)
        {
            return false;
        }

        bool initialGrace = now - state.IgnitionStartedAtUtc <= TimeSpan.FromSeconds(VanguardGrenadeEmergencyPolicy.PhysicalIgnitionGraceSeconds);
        bool recoveryGrace = state.LastRecoveryReissueAtUtc != DateTimeOffset.MinValue
            && now - state.LastRecoveryReissueAtUtc <= PostAuthorityAcquisitionGrace;
        bool transientBlockGrace = state.TransientActionBlockedSinceUtc != DateTimeOffset.MinValue
            && state.TransientActionReleasedAtUtc == DateTimeOffset.MinValue
            && now - state.TransientActionBlockedSinceUtc <= MaximumTransientActionReleaseGrace;
        bool transientReleaseGrace = state.TransientActionReleasedAtUtc != DateTimeOffset.MinValue
            && now - state.TransientActionReleasedAtUtc <= PostTransientActionReleaseFailureAge;
        return initialGrace || recoveryGrace || transientBlockGrace || transientReleaseGrace;
    }

    private static bool TryReportBoundedIgnitionFailure(
        BotOwner owner,
        VanguardReturnMovementCommand command,
        DriveState state,
        DateTimeOffset now,
        string source,
        out string result)
    {
        result = "none";
        if (state.IgnitionConfirmed || state.IgnitionStartedAtUtc == DateTimeOffset.MinValue)
        {
            return false;
        }

        TimeSpan ignitionAge = now - state.IgnitionStartedAtUtc;
        TransientActionBlock transientAction = CaptureTransientActionBlock(owner);
        bool transientActionExpired = transientAction.Active
            && state.TransientActionBlockedSinceUtc != DateTimeOffset.MinValue
            && now - state.TransientActionBlockedSinceUtc >= MaximumTransientActionReleaseGrace;
        if (transientAction.Active && !transientActionExpired)
        {
            return false;
        }

        DateTimeOffset physicalFailureClockStartedAt = state.TransientActionReleasedAtUtc != DateTimeOffset.MinValue
            ? state.TransientActionReleasedAtUtc
            : state.IgnitionStartedAtUtc;
        TimeSpan physicalFailureAge = now - physicalFailureClockStartedAt;
        if (!transientActionExpired && physicalFailureAge < HardNoIgnitionFailureAge)
        {
            return false;
        }

        DateTimeOffset settleStartedAt = state.LastRecoveryReissueAtUtc != DateTimeOffset.MinValue
            ? state.LastRecoveryReissueAtUtc
            : state.AuthorityAcquiredAtUtc;
        if (!transientActionExpired
            && settleStartedAt != DateTimeOffset.MinValue
            && now - settleStartedAt <= PostAuthorityAcquisitionGrace)
        {
            return false;
        }

        bool targetMatches = TryReadTargetMatch(owner, command.Anchor, out bool targetKnown, out float targetDistance);
        bool moverPathPresent = owner.Mover?.HasPathAndNoComplete == true;
        bool noPhysicalProof = state.ObservedSpeedMetersPerSecond < PhysicalSpeedEvidenceMetersPerSecond
            && state.MaxDisplacementFromIgnitionOrigin < PhysicalDisplacementEvidenceMeters
            && state.MaxTowardAnchorDisplacement < PhysicalTowardAnchorEvidenceMeters;
        if (!transientActionExpired && (targetMatches || moverPathPresent || !noPhysicalProof))
        {
            return false;
        }

        bool independentPathComplete = VanguardReturnMovementCommandStore.ValidatePathStillComplete(owner, command.Anchor, out string pathSummary);
        bool exactLayerActive = state.LastBigBrainDriveAtUtc != DateTimeOffset.MinValue
            && now - state.LastBigBrainDriveAtUtc <= BigBrainOwnershipMemory;
        string failureKind;
        bool backendFailure;
        if (transientActionExpired)
        {
            failureKind = "transient_player_action_not_released_within_bounded_window:" + transientAction.Reason;
            backendFailure = true;
        }
        else if (independentPathComplete && (!state.MovementAuthorityAcquired || !exactLayerActive))
        {
            failureKind = "physical_backend_authority_not_acquired_within_bounded_window";
            backendFailure = true;
        }
        else if (independentPathComplete && state.RecoveryReissueCount >= 1)
        {
            failureKind = "physical_backend_rejected_after_single_idempotent_recovery_reissue";
            backendFailure = true;
        }
        else if (independentPathComplete)
        {
            return false;
        }
        else
        {
            failureKind = "anchor_navmesh_no_longer_complete";
            backendFailure = false;
        }

        string failure = failureKind + ":" + pathSummary;
        VanguardClientDiagnosticsLog.Warning(IgnitionFailedTag,
            $"operator={Safe(command.OperatorId)}; botProfile={Safe(command.BotProfileId)}; lease={Safe(command.LeaseId)}; generation={command.Generation}; ignitionAge={ignitionAge.TotalSeconds:0.00}; physicalFailureAge={physicalFailureAge.TotalSeconds:0.00}; transientAction={Bool(transientAction.Active)}; transientReason={Safe(transientAction.Reason)}; attempts={state.IgnitionAttemptCount}; authorityReissues={state.AuthorityReissueCount}; recoveryReissues={state.RecoveryReissueCount}; recoveryBudget=1; moverTargetKnown={Bool(targetKnown)}; moverTargetMatch={Bool(targetMatches)}; moverTargetDistance={targetDistance:0.00}; moverPathPresent={Bool(moverPathPresent)}; observedSpeed={state.ObservedSpeedMetersPerSecond:0.00}; displacementFromIgnition={state.MaxDisplacementFromIgnitionOrigin:0.00}; towardAnchorDisplacement={state.MaxTowardAnchorDisplacement:0.00}; exactLayerActive={Bool(exactLayerActive)}; movementAuthorityAcquired={Bool(state.MovementAuthorityAcquired)}; independentNavMeshPathComplete={Bool(independentPathComplete)}; failureKind={failureKind}; backendFailure={Bool(backendFailure)}; anchorFailure={Bool(!backendFailure)}; conditionalRecoveryExhausted={Bool(state.RecoveryReissueCount >= 1)}; moverStopHere=false; singleDestructiveCleanupOwnedByEmergencyService=true; source={Safe(source)}; tag={IdempotentDriveStatusTag}");
        if (backendFailure)
        {
            VanguardReturnMovementCommandStore.ReportPhysicalBackendFailure(command, now, "" + failure);
        }
        else
        {
            VanguardReturnMovementCommandStore.ReportPathInvalid(command, now, "" + failure);
        }
        Release(command.BotProfileId);
        result = backendFailure ? "backend_failure_reported" : "anchor_failure_reported";
        return true;
    }

    private static void ConfirmIgnition(DriveState state, VanguardReturnMovementCommand command, DateTimeOffset now, string source, string evidence)
    {
        if (state.IgnitionConfirmed)
        {
            return;
        }

        state.IgnitionConfirmed = true;
        state.LastProgressAtUtc = now;
        if (state.LastIgnitionConfirmationLogAtUtc == DateTimeOffset.MinValue)
        {
            state.LastIgnitionConfirmationLogAtUtc = now;
            VanguardClientDiagnosticsLog.Operational(IgnitionConfirmedTag, () =>
                $"operator={Safe(command.OperatorId)}; botProfile={Safe(command.BotProfileId)}; lease={Safe(command.LeaseId)}; generation={command.Generation}; evidence={Safe(evidence)}; attempts={state.IgnitionAttemptCount}; observedSpeed={state.ObservedSpeedMetersPerSecond:0.00}; displacementFromIgnition={state.MaxDisplacementFromIgnitionOrigin:0.00}; towardAnchorDisplacement={state.MaxTowardAnchorDisplacement:0.00}; source={Safe(source)}; theoreticalNavMeshOnly=false; exactEmergencyCommandRetained=true; destructiveReplan=false; sainTargetPreserved=true; sainDecisionPreserved=true; tag={IgnitionGraceStatusTag}; foundationTag={StatusTag}");
        }
    }

    private static void LogIgnitionPending(
        DriveState state,
        VanguardReturnMovementCommand command,
        DateTimeOffset now,
        string source,
        NavMeshPathStatus status,
        bool targetKnown,
        bool targetMatches,
        bool moverPathPresent,
        float targetDistance,
        string detail = "same_frame_readback_not_authoritative")
    {
        if (state.LastIgnitionPendingLogAtUtc != DateTimeOffset.MinValue
            && now - state.LastIgnitionPendingLogAtUtc < IgnitionPendingLogInterval)
        {
            return;
        }

        state.LastIgnitionPendingLogAtUtc = now;
        double age = state.IgnitionStartedAtUtc == DateTimeOffset.MinValue
            ? 0d
            : Math.Max(0d, (now - state.IgnitionStartedAtUtc).TotalSeconds);
        VanguardClientDiagnosticsLog.Operational(IgnitionPendingTag, () =>
            $"operator={Safe(command.OperatorId)}; botProfile={Safe(command.BotProfileId)}; lease={Safe(command.LeaseId)}; generation={command.Generation}; ignitionAge={age:0.00}; grace={VanguardGrenadeEmergencyPolicy.PhysicalIgnitionGraceSeconds:0.00}; attempts={state.IgnitionAttemptCount}; status={status}; moverTargetKnown={Bool(targetKnown)}; moverTargetMatch={Bool(targetMatches)}; moverTargetDistance={targetDistance:0.00}; moverPathPresent={Bool(moverPathPresent)}; observedSpeed={state.ObservedSpeedMetersPerSecond:0.00}; displacementFromIgnition={state.MaxDisplacementFromIgnitionOrigin:0.00}; towardAnchorDisplacement={state.MaxTowardAnchorDisplacement:0.00}; source={Safe(source)}; detail={Safe(detail)}; commandRetained=true; layer97Eligible=true; stopAndReplan=false; sainChaseExclusionPending=true; tag={IgnitionGraceStatusTag}; foundationTag={StatusTag}");
    }

    private static bool TryReadTargetMatch(BotOwner owner, Vector3 anchor, out bool targetKnown, out float distance)
    {
        targetKnown = false;
        distance = float.PositiveInfinity;
        try
        {
            Vector3? target = owner.Mover?.TargetPoint;
            if (!target.HasValue)
            {
                return false;
            }

            targetKnown = true;
            distance = HorizontalDistance(target.Value, anchor);
            return distance <= PathTargetToleranceMeters;
        }
        catch
        {
            targetKnown = false;
            return false;
        }
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float x = a.x - b.x;
        float z = a.z - b.z;
        return (float)Math.Sqrt((x * x) + (z * z));
    }

    private static string VectorText(Vector3 value)
        => value.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + value.y.ToString("0.0", CultureInfo.InvariantCulture) + "," + value.z.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Replace(';', '_').Replace('\r', ' ').Replace('\n', ' ');
}
#endif
