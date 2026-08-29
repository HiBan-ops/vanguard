#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.External;
using Vanguard.Client.Runtime.Movement.Brain;

// Responsibility: owns short-lived stationary cohesion claims around the player owner.
// Flow: Each tick compares owner and Operator positions, chooses an owner-relative lane/claim, validates NavMesh/path/occupancy, grants a short movement lease, then refreshes, hands off or releases that claim as the squad moves.
// Authority boundary: it may plan/lease movement positions but never supersedes combat, medical, hard-return or authored tactical authority.
// Invariant: a stable hold is admitted only from validated, physically occupiable claims; planning state is raid-scoped and reset atomically.

namespace Vanguard.Client.Runtime.Movement;

internal readonly struct VanguardObservationDeploymentApproachPlan
{
    public VanguardObservationDeploymentApproachPlan(
        bool valid,
        bool readyForHandoff,
        string claimId,
        string lane,
        Vector3 anchor,
        float anchorRadiusMeters,
        bool sprintAllowed,
        float anchorDistanceMeters,
        float pathDistanceMeters,
        string pathSummary,
        string summary)
    {
        Valid = valid;
        ReadyForHandoff = readyForHandoff;
        ClaimId = claimId ?? string.Empty;
        Lane = lane ?? string.Empty;
        Anchor = anchor;
        AnchorRadiusMeters = anchorRadiusMeters;
        SprintAllowed = sprintAllowed;
        AnchorDistanceMeters = anchorDistanceMeters;
        PathDistanceMeters = pathDistanceMeters;
        PathSummary = pathSummary ?? string.Empty;
        Summary = summary ?? string.Empty;
    }

    public bool Valid { get; }
    public bool ReadyForHandoff { get; }
    public string ClaimId { get; }
    public string Lane { get; }
    public Vector3 Anchor { get; }
    public float AnchorRadiusMeters { get; }
    public bool SprintAllowed { get; }
    public float AnchorDistanceMeters { get; }
    public float PathDistanceMeters { get; }
    public string PathSummary { get; }
    public string Summary { get; }

    public static VanguardObservationDeploymentApproachPlan Invalid(string reason)
        => new(false, false, string.Empty, string.Empty, Vector3.zero, 0f, false, 0f, 0f, string.Empty, reason);
}

/// <summary>
/// Vanguard gives every Operator a stable tactical claim around his player owner.
/// The claim is not a cosmetic formation slot. It is a short-lived, validated movement
/// authority contract with explicit owner/lane/purpose, reused until the owner meaningfully
/// moves, rotates, or the claim becomes stale. This prevents repeated self-drive drift while
/// keeping combat, medical and hard-return priorities protected. The runtime requires physical
/// occupation of distinct stationary claims before an outdoor stable hold may freeze planning.
/// </summary>
internal static class VanguardSquadCohesionClaimExecutor
{
    public const string StatusTag = "VANGUARD_COHESION_CLAIMS_STATUS";
    public const string CohesionAnchorsRunStatusTag = "VANGUARD_COHESION_ANCHORS_RUN_STATUS";
    public const string CombatHoldMedicalCatchupStatusTag = "VANGUARD_COMBAT_HOLD_MEDICAL_CATCHUP_STATUS";
    public const string HostileIndoorMovementPlanStatusTag = "VANGUARD_HOSTILE_INDOOR_MOVEMENT_PLAN_STATUS";
    public const string PathAlertRecoveryStatusTag = "VANGUARD_PATH_ALERT_RECOVERY_STATUS";
    public const string HardReturnAlertStatusTag = "VANGUARD_HARD_RETURN_ALERT_STATUS";
    public const string OrchestratorAuthorityStatusTag = "VANGUARD_ORCHESTRATOR_AUTHORITY_STATUS";
    public const string ExclusiveAuthorityStatusTag = "VANGUARD_EXCLUSIVE_AUTHORITY_STATUS";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, CohesionClaimState> ClaimsByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CohesionClaimLeaseState> ActiveByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CohesionClaimState> PendingPlanByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> CooldownByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, OwnerClaimState> OwnerStateByOwnerProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, AtomicHandoffTicket> AtomicHandoffByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> PhysicalStackSinceByPair = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> PhysicalStackCooldownByPair = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(0.55d);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(1.75d);
    private static DateTimeOffset nextTickAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            ClaimsByBotProfileId.Clear();
            ActiveByBotProfileId.Clear();
            PendingPlanByBotProfileId.Clear();
            CooldownByBotProfileId.Clear();
            OwnerStateByOwnerProfileId.Clear();
            AtomicHandoffByBotProfileId.Clear();
            LastLogByKey.Clear();
            PhysicalStackSinceByPair.Clear();
            PhysicalStackCooldownByPair.Clear();
        }

        VanguardDynamicFormationPlanner.Reset(reason);
        VanguardInteriorSecurityPlanner.Reset(reason);
        VanguardCohesionPlanningBudget.Reset(reason);
        VanguardInteriorSecurityOrientationExecutor.Reset(reason);
        bootLogged = false;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_COHESION_CLAIMS_RESET reason={Safe(reason)}; claims=cleared; active=0; doctrine=dynamic_roles_persistent_volume_security_and_verified_access_claims_before_generic_stable_hold; cohesionAnchors=full_bubble_anchors_run; contextualSpeed=combat_hold_medical_catchup; indoorMovement=movement_plan_anchor_queue_hostile_indoor_hold; pathRecovery=hard_path_sanity_alert_recovery; tag={StatusTag}; Tag={CohesionAnchorsRunStatusTag}; Tag={CombatHoldMedicalCatchupStatusTag}; Tag={HostileIndoorMovementPlanStatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
    }


    /// <summary>
    /// Returns true only after the owner has remained physically still long enough to switch from
    /// corridor travel to observation deployment. This is read-only and never opens a claim lease.
    /// </summary>
    public static bool RequiresObservationDeployment(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (snapshot == null
            || string.IsNullOrWhiteSpace(snapshot.BotProfileId)
            || string.IsNullOrWhiteSpace(snapshot.OwnerProfileId))
        {
            reason = "snapshot_identity_missing";
            return false;
        }

        if (!VanguardSquadTravelRouteMemory.TryGetOwnerStationaryState(
                snapshot.OwnerProfileId,
                now,
                out var canonicalMotion,
                out var canonicalReason))
        {
            reason = "canonical_owner_motion_unavailable:" + canonicalReason;
            return false;
        }

        if (canonicalMotion.OwnerMovingRecently)
        {
            reason = "canonical_owner_still_moving:" + canonicalReason;
            return false;
        }

        double stationarySeconds = canonicalMotion.StationarySeconds;
        if (stationarySeconds < VanguardContinuousCohesionLocomotionPolicy.ObservationDeploymentOwnerStillSeconds)
        {
            reason = "canonical_owner_stationary_hysteresis:" + stationarySeconds.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        reason = "canonical_owner_observation_pose_confirmed:" + stationarySeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + ":epoch=" + canonicalMotion.RouteEpoch.ToString(CultureInfo.InvariantCulture)
            + ":version=" + canonicalMotion.RouteVersion.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// The runtime resolves one current stationary deployment destination without transferring authority.
    /// Travel may use the validated claim as its final approach target; the claim executor receives
    /// authority only after the Operator is physically close enough for an invisible handoff.
    /// </summary>
    public static bool TryResolveObservationDeploymentApproach(
        OperatorDecisionSnapshot snapshot,
        Vector3 botPosition,
        DateTimeOffset now,
        out VanguardObservationDeploymentApproachPlan plan)
    {
        plan = VanguardObservationDeploymentApproachPlan.Invalid("not_ready");
        if (snapshot == null
            || string.IsNullOrWhiteSpace(snapshot.BotProfileId)
            || string.IsNullOrWhiteSpace(snapshot.OwnerProfileId))
        {
            plan = VanguardObservationDeploymentApproachPlan.Invalid("snapshot_identity_missing");
            return false;
        }

        CohesionClaimState claim;
        lock (Sync)
        {
            ClaimsByBotProfileId.TryGetValue(snapshot.BotProfileId, out claim);
        }

        if (!VanguardSquadTravelRouteMemory.TryGetOwnerStationaryState(
                snapshot.OwnerProfileId,
                now,
                out var canonicalMotion,
                out var canonicalReason)
            || canonicalMotion.OwnerMovingRecently)
        {
            plan = VanguardObservationDeploymentApproachPlan.Invalid(
                "canonical_owner_stop_not_confirmed:" + canonicalReason);
            return false;
        }

        double stationarySeconds = canonicalMotion.StationarySeconds;
        if (stationarySeconds < VanguardContinuousCohesionLocomotionPolicy.ObservationDeploymentCommitStillSeconds)
        {
            plan = VanguardObservationDeploymentApproachPlan.Invalid(
                "canonical_atomic_deployment_warmup:" + stationarySeconds.ToString("0.00", CultureInfo.InvariantCulture));
            return false;
        }

        if (string.IsNullOrWhiteSpace(claim.BotProfileId)
            || claim.ValidUntilUtc <= now
            || !claim.StationaryHold
            || !string.Equals(claim.OwnerProfileId, snapshot.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
        {
            plan = VanguardObservationDeploymentApproachPlan.Invalid("stationary_claim_missing_or_stale");
            return false;
        }

        lock (Sync)
        {
            foreach (var other in ClaimsByBotProfileId.Values)
            {
                if (string.Equals(other.BotProfileId, claim.BotProfileId, StringComparison.OrdinalIgnoreCase)
                    || other.ValidUntilUtc <= now
                    || !other.StationaryHold
                    || !string.Equals(other.OwnerProfileId, claim.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(other.Lane, claim.Lane, StringComparison.OrdinalIgnoreCase))
                {
                    plan = VanguardObservationDeploymentApproachPlan.Invalid("stationary_lane_duplicate:" + Safe(claim.Lane));
                    return false;
                }
            }
        }

        float anchorDistance = HorizontalDistance(botPosition, claim.Anchor);
        float acceptedDistance = claim.AnchorRadiusMeters
            + VanguardContinuousCohesionLocomotionPolicy.ObservationDeploymentClaimToleranceMeters;
        bool alreadyReached = anchorDistance <= acceptedDistance;
        if (alreadyReached)
        {
            string reachedSummary = "ready_claim_already_reached:claim=" + Safe(claim.ClaimId)
                + ":lane=" + Safe(claim.Lane)
                + ":distance=" + anchorDistance.ToString("0.00", CultureInfo.InvariantCulture)
                + ":stationary=" + stationarySeconds.ToString("0.00", CultureInfo.InvariantCulture);
            StoreAtomicHandoffTicket(snapshot, claim, botPosition, now, reachedSummary, 0f);
            plan = new VanguardObservationDeploymentApproachPlan(
                true,
                true,
                claim.ClaimId,
                claim.Lane,
                claim.Anchor,
                claim.AnchorRadiusMeters,
                claim.SprintAllowed,
                anchorDistance,
                0f,
                reachedSummary,
                reachedSummary);
            return true;
        }

        if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner(
                "CohesionNavMeshPath",
                snapshot.OwnerProfileId,
                1,
                VanguardContinuousCohesionLocomotionPolicy.CohesionNavMeshPathsPerFrame,
                out var budgetReason))
        {
            plan = VanguardObservationDeploymentApproachPlan.Invalid("handoff_path_budget_pending:" + budgetReason);
            return false;
        }

        if (!TryValidateClaimPath(snapshot, claim, botPosition, claim.Anchor, out var pathSummary, out var pathDistance, out var pathRejectReason))
        {
            plan = VanguardObservationDeploymentApproachPlan.Invalid(
                "handoff_claim_path_not_ready:" + Safe(pathRejectReason) + ":" + Safe(pathSummary));
            return false;
        }

        bool readyForHandoff = anchorDistance <= Math.Max(
            acceptedDistance,
            VanguardContinuousCohesionLocomotionPolicy.ObservationDeploymentTravelHandoffMeters);
        string summary = (readyForHandoff ? "ready_stationary_claim" : "stationary_claim_final_approach")
            + ":claim=" + Safe(claim.ClaimId)
            + ":lane=" + Safe(claim.Lane)
            + ":anchorDistance=" + anchorDistance.ToString("0.00", CultureInfo.InvariantCulture)
            + ":pathDistance=" + pathDistance.ToString("0.00", CultureInfo.InvariantCulture)
            + ":stationary=" + stationarySeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + ":path=" + Safe(pathSummary);
        if (readyForHandoff)
        {
            StoreAtomicHandoffTicket(snapshot, claim, botPosition, now, pathSummary, pathDistance);
        }

        plan = new VanguardObservationDeploymentApproachPlan(
            true,
            readyForHandoff,
            claim.ClaimId,
            claim.Lane,
            claim.Anchor,
            claim.AnchorRadiusMeters,
            claim.SprintAllowed,
            anchorDistance,
            pathDistance,
            pathSummary,
            summary);
        return true;
    }

    public static bool TryGetReadyObservationDeploymentClaim(
        OperatorDecisionSnapshot snapshot,
        Vector3 botPosition,
        DateTimeOffset now,
        out string summary)
    {
        if (!TryResolveObservationDeploymentApproach(snapshot, botPosition, now, out var plan))
        {
            summary = plan.Summary;
            return false;
        }

        summary = plan.Summary;
        return plan.ReadyForHandoff;
    }

    private static void StoreAtomicHandoffTicket(
        OperatorDecisionSnapshot snapshot,
        CohesionClaimState claim,
        Vector3 botPosition,
        DateTimeOffset now,
        string pathSummary,
        float pathDistance)
    {
        var ticket = new AtomicHandoffTicket(
            snapshot.BotProfileId,
            snapshot.OwnerProfileId,
            claim.ClaimId,
            claim.Anchor,
            botPosition,
            now,
            now + TimeSpan.FromSeconds(2.00d),
            pathSummary,
            pathDistance);
        lock (Sync)
        {
            AtomicHandoffByBotProfileId[snapshot.BotProfileId] = ticket;
        }
    }

    private static bool TryGetAtomicHandoffTicket(
        OperatorDecisionSnapshot snapshot,
        CohesionClaimState claim,
        Vector3 botPosition,
        DateTimeOffset now,
        out AtomicHandoffTicket ticket,
        out string reason)
    {
        reason = "ticket_missing";
        lock (Sync)
        {
            if (!AtomicHandoffByBotProfileId.TryGetValue(snapshot.BotProfileId, out ticket))
            {
                return false;
            }

            if (ticket.ValidUntilUtc <= now
                || !string.Equals(ticket.OwnerProfileId, snapshot.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ticket.ClaimId, claim.ClaimId, StringComparison.OrdinalIgnoreCase)
                || HorizontalDistance(ticket.ClaimAnchor, claim.Anchor) > 0.25f
                || HorizontalDistance(ticket.ValidatedBotPosition, botPosition) > 1.50f)
            {
                AtomicHandoffByBotProfileId.Remove(snapshot.BotProfileId);
                reason = "ticket_stale_or_identity_mismatch";
                ticket = default;
                return false;
            }
        }

        reason = "atomic_handoff_ticket_valid";
        return true;
    }

    /// <summary>
    /// Stable hold is valid outdoors only after this Operator physically occupies a current,
    /// stationary and non-duplicated claim. Proximity/usefulness alone cannot suppress deployment.
    /// </summary>
    public static bool HasSatisfiedObservationDeployment(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (!RequiresObservationDeployment(snapshot, now, out var deploymentReason))
        {
            reason = "deployment_not_required:" + deploymentReason;
            return false;
        }

        CohesionClaimState claim;
        lock (Sync)
        {
            ClaimsByBotProfileId.TryGetValue(snapshot.BotProfileId, out claim);
        }

        if (string.IsNullOrWhiteSpace(claim.BotProfileId)
            || claim.ValidUntilUtc <= now
            || !claim.StationaryHold
            || !string.Equals(claim.OwnerProfileId, snapshot.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "stationary_claim_missing_or_stale";
            return false;
        }

        float anchorDistance = HorizontalDistance(snapshot.Position, claim.Anchor);
        float acceptedDistance = claim.AnchorRadiusMeters
            + VanguardContinuousCohesionLocomotionPolicy.ObservationDeploymentClaimToleranceMeters;
        if (anchorDistance > acceptedDistance)
        {
            reason = "stationary_claim_not_reached:distance=" + anchorDistance.ToString("0.00", CultureInfo.InvariantCulture)
                + ":accepted=" + acceptedDistance.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        lock (Sync)
        {
            foreach (var other in ClaimsByBotProfileId.Values)
            {
                if (string.Equals(other.BotProfileId, claim.BotProfileId, StringComparison.OrdinalIgnoreCase)
                    || other.ValidUntilUtc <= now
                    || !other.StationaryHold
                    || !string.Equals(other.OwnerProfileId, claim.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(other.Lane, claim.Lane, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "stationary_lane_duplicate:" + Safe(claim.Lane);
                    return false;
                }
            }
        }

        reason = "stationary_claim_reached:lane=" + Safe(claim.Lane)
            + ":distance=" + anchorDistance.ToString("0.00", CultureInfo.InvariantCulture)
            + ":owner=" + Safe(claim.OwnerProfileId);
        return true;
    }

    public static void Tick()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now < nextTickAtUtc)
        {
            return;
        }

        nextTickAtUtc = now + TickInterval;
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_COHESION_CLAIMS_BOOT enabled={Bool(VanguardMovementAuthorityDoctrine.TacticalRepositionActiveEnabled)}; scope=stable_claim_distribution; lanes=full_bubble_front_flank_rear_entry; activeWindow=CloseCohesionMovement; maxActive=dynamic; claimRefresh=owner_significant_move_or_rotate_or_stale; runDistance={VanguardMovementAuthorityDoctrine.ClaimedCohesionRunDistanceMeters:0.0}; sprintDistance={VanguardMovementAuthorityDoctrine.ClaimedCohesionSprintDistanceMeters:0.0}; noLootForceScan=true; contextualSpeed=true; freshThreatHold=true; medicalResidualRelease=replaced_by_general_sain_stale_exit; movementPlanQueue=true; hardReturnFallbackOnReject=true; stationaryHysteresis=canonical_travel_route_memory_plus_direct_resume_preemption; stableHoldRequiresReachedDistinctClaim=true; CanonicalAdmissionTag={VanguardPrimaryExecutionContract.CanonicalStationaryAdmissionStatusTag}; indoorSectorHold=verified_navmesh_access_only; DynamicLeadViability=true; StationaryCompact=true; PersistentInteriorOrientationExecutor=true; MovementContract=true; FormationLanes=true; AnchorFailureThrottle=true; LanePreservingFallback=true; AntiStack=true; FinalAntiStack=true; IncrementalPlanning=true; OneOperatorPerTick=true; MaxPathCalculations={VanguardCohesionPlanningBudget.MaxPathCalculationsPerTick}; MaxCandidateEvaluations={VanguardCohesionPlanningBudget.MaxCandidateEvaluationsPerTick}; Tag={VanguardRuntimeConvergenceStatusTags.IncrementalCohesionPlanning}; BudgetTag={VanguardRuntimeConvergenceStatusTags.BoundedCohesionPathBudget}; tag={StatusTag}; Tag={CohesionAnchorsRunStatusTag}; Tag={CombatHoldMedicalCatchupStatusTag}; Tag={HostileIndoorMovementPlanStatusTag}; Tag={PathAlertRecoveryStatusTag}; Tag={HardReturnAlertStatusTag}; Tag={OrchestratorAuthorityStatusTag}; Tag={ExclusiveAuthorityStatusTag}; Tag={VanguardMovementAuthorityDoctrine.FormationLanesStatusTag}; build={VanguardBuildVersion.BuildLabel}; finalAntiStackTag={VanguardMovementAuthorityDoctrine.FinalAntiStackStatusTag}");
        }

        var snapshots = VanguardOperatorDecisionSnapshotService.GetLatestSnapshots();
        bool planningAllowed = VanguardRuntimeFrameBudgetGuard.ShouldRunOptional(
            "CohesionClaimPlanning",
            now,
            TimeSpan.FromSeconds(1.25d),
            out _);
        if (planningAllowed)
        {
            VanguardCohesionPlanningBudget.BeginTick(snapshots, now);
            VanguardCohesionPlanningBudget.EnterPlanningScope();
            try
            {
                RefreshClaims(snapshots, now);
            }
            finally
            {
                VanguardCohesionPlanningBudget.ExitPlanningScope();
            }
        }

        TickActiveLeases(snapshots, now);
        TickPersistentPhysicalDestack(snapshots, now);
        if (!VanguardMovementAuthorityDoctrine.TacticalRepositionActiveEnabled)
        {
            return;
        }

        // Atomic handoff tickets were path-validated by the Travel executor earlier in the same
        // frame and may start even when optional claim planning is deferred. Uncached claims still
        // obey the normal planning budget.
        TryStartClaimLeases(snapshots, now, planningAllowed);

        // Persistent interior watch orientation is lightweight and must never be deferred by
        // formation planning pressure: a verified guard keeps covering its access while the
        // player moves inside the same volume.
        VanguardInteriorSecurityOrientationExecutor.Tick();
    }

    private static void SupersedeClaimsForAtomicInteriorDeployment(
        string ownerProfileId,
        IReadOnlyList<string> assignedBotProfileIds,
        string volumeId,
        DateTimeOffset now,
        string reason)
    {
        var assignments = new Dictionary<string, InteriorSecurityAssignment>(StringComparer.OrdinalIgnoreCase);
        foreach (string botProfileId in assignedBotProfileIds ?? Array.Empty<string>())
        {
            if (VanguardInteriorSecurityPlanner.TryGetAssignment(botProfileId, now, out InteriorSecurityAssignment assignment))
            {
                assignments[botProfileId] = assignment;
            }
        }

        var retiredLeases = new List<CohesionClaimLeaseState>();
        int removedClaims = 0;
        int removedPending = 0;
        lock (Sync)
        {
            foreach (var pair in assignments)
            {
                string botProfileId = pair.Key;
                InteriorSecurityAssignment assignment = pair.Value;
                if (ClaimsByBotProfileId.TryGetValue(botProfileId, out CohesionClaimState existingClaim))
                {
                    bool sameMission = existingClaim.UsesInteriorPathContract
                        && string.Equals(existingClaim.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(existingClaim.Lane, assignment.Lane, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(existingClaim.Purpose, assignment.Purpose + ":" + assignment.PortalKey, StringComparison.OrdinalIgnoreCase)
                        && HorizontalDistance(existingClaim.Anchor, assignment.Anchor) <= 1.25f;
                    if (!sameMission && ClaimsByBotProfileId.Remove(botProfileId))
                    {
                        removedClaims++;
                    }
                }

                if (PendingPlanByBotProfileId.Remove(botProfileId))
                {
                    removedPending++;
                }
                AtomicHandoffByBotProfileId.Remove(botProfileId);
                CooldownByBotProfileId.Remove(botProfileId);

                if (ActiveByBotProfileId.TryGetValue(botProfileId, out CohesionClaimLeaseState activeLease))
                {
                    bool sameActiveMission = activeLease.UsesInteriorPathContract
                        && string.Equals(activeLease.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(activeLease.Lane, assignment.Lane, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(activeLease.Purpose, assignment.Purpose + ":" + assignment.PortalKey, StringComparison.OrdinalIgnoreCase)
                        && HorizontalDistance(activeLease.Anchor, assignment.Anchor) <= 1.25f;
                    if (!sameActiveMission)
                    {
                        ActiveByBotProfileId.Remove(botProfileId);
                        retiredLeases.Add(activeLease);
                    }
                }
            }
        }

        foreach (CohesionClaimLeaseState retired in retiredLeases)
        {
            VanguardReturnMovementCommandStore.ClearOwned(
                retired.BotProfileId,
                retired.LeaseId,
                retired.StartedAtUtc,
                "atomic_interior_deployment_supersedes_previous_claim:" + reason);
            VanguardMainIntentScheduler.FinishPrimaryWindow(
                retired.BotProfileId,
                now,
                "Interrupted",
                "atomic_interior_deployment_supersedes_previous_claim:" + reason,
                retired.Summary,
                retired.WindowId);
        }

        VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.StableEnvironmentAtomicInteriorDeploymentStatusTag,
            $"VANGUARD_GENERIC_CLAIMS_SUPERSEDED owner={Safe(ownerProfileId)}; volume={Safe(volumeId)}; assigned={assignments.Count}; removedClaims={removedClaims}; removedPending={removedPending}; retiredActiveLeases={retiredLeases.Count}; genericCooldownsCleared=true; assignmentsPreserved=true; doctrine=committed_indoor_batch_replaces_old_triangle_before_collective_claim_scoring; tag={VanguardPrimaryExecutionContract.StableEnvironmentAtomicInteriorDeploymentStatusTag}");
    }

    public static bool YieldActiveClaimToOwnerTravel(string botProfileId, DateTimeOffset now, string reason, out string summary)
    {
        summary = "claimYielded=false;reason=none";
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            summary = "claimYielded=false;reason=missing_bot_profile";
            return false;
        }

        CohesionClaimLeaseState active = default;
        bool hadActive;
        bool hadClaim;
        bool hadPending;
        lock (Sync)
        {
            hadActive = ActiveByBotProfileId.TryGetValue(botProfileId, out active);
            hadClaim = ClaimsByBotProfileId.Remove(botProfileId);
            hadPending = PendingPlanByBotProfileId.Remove(botProfileId);
            AtomicHandoffByBotProfileId.Remove(botProfileId);
            CooldownByBotProfileId.Remove(botProfileId);
            if (hadActive)
            {
                ActiveByBotProfileId.Remove(botProfileId);
            }
        }

        if (hadActive)
        {
            // TryOpenTravelCorridor has already atomically superseded the scheduler movement
            // window.  Only retire the claim executor and its lease-safe command here; finishing
            // the old scheduler window again would be a foreign-generation no-op and log noise.
            VanguardReturnMovementCommandStore.ClearOwned(active.BotProfileId, active.LeaseId, active.StartedAtUtc, "owner_travel_supersedes_static_claim:" + reason);
        }

        summary = "claimYielded=" + Bool(hadActive || hadClaim || hadPending)
            + ";active=" + Bool(hadActive)
            + ";claim=" + Bool(hadClaim)
            + ";pending=" + Bool(hadPending)
            + ";reason=" + Safe(reason);
        if (hadActive || hadClaim || hadPending)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_STATIC_CLAIM_YIELDED_TO_TRAVEL botProfile={Safe(botProfileId)}; {summary}; commandClearLeaseSafe=true; schedulerAlreadySuperseded=true; pendingPromotion=false; cooldownWritten=false; doctrine=owner_travel_atomically_supersedes_static_claim_before_new_command; tag=VANGUARD_TRAVEL_RESPONSIVENESS_STATUS; claimTag={StatusTag}; travelTag={VanguardSquadTravelCohesionExecutor.StatusTag}");
        }
        return hadActive || hadClaim || hadPending;
    }

    public static bool ShouldPublishClaimContract(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        if (!snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement || !snapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            reason = "owner_unreliable";
            return false;
        }

        OwnerClaimState publishOwnerState;
        CohesionClaimState publishClaim;
        lock (Sync)
        {
            OwnerStateByOwnerProfileId.TryGetValue(snapshot.OwnerProfileId, out publishOwnerState);
            ClaimsByBotProfileId.TryGetValue(snapshot.BotProfileId, out publishClaim);
        }
        bool freshOwnerMotion = !string.IsNullOrWhiteSpace(publishOwnerState.OwnerProfileId)
            && DateTimeOffset.UtcNow - publishOwnerState.ObservedAtUtc <= TimeSpan.FromSeconds(2.50d);
        if (freshOwnerMotion
            && !publishOwnerState.Stationary
            && snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.CloseCohesionStartMinMeters
            && !publishClaim.UsesInteriorPathContract)
        {
            reason = "owner_moving_travel_priority:speed="
                + publishOwnerState.Speed.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        if (VanguardOrchestratorAuthorityPolicy.ShouldBlockCohesionMutation(snapshot, out var authorityBlockReason))
        {
            reason = "primary_domain_blocks_cohesion:" + authorityBlockReason;
            return false;
        }

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot) || VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(snapshot))
        {
            reason = "direct_threat_or_immediate_awareness";
            return false;
        }

        if (VanguardCombatAwarenessBridge.HasFreshSquadCombatContact(snapshot, DateTimeOffset.UtcNow, out var squadContactReason)
            && snapshot.SquadCohesion.OperatorDistanceToOwner < VanguardMovementAuthorityDoctrine.ClaimedCohesionFreshContactHoldMeters)
        {
            reason = "fresh_squad_contact_hold_sector:" + squadContactReason;
            return false;
        }

        if (VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(snapshot))
        {
            reason = "stationary_medical_authority";
            return false;
        }

        if (snapshot.MovementAuthority.HardOutsideBubble || snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.HardCorrectionMeters)
        {
            reason = "hard_return_higher_priority";
            return false;
        }

        if (snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true)
        {
            reason = "critical_loot_activity";
            return false;
        }

        bool storedClaimPressure = HasStoredClaimDrivePressure(snapshot, DateTimeOffset.UtcNow, out var storedClaimReason);
        bool initialBubbleAssignment = snapshot.SquadCohesion.SameOwnerOperatorCount > 0
            && snapshot.SquadCohesion.OperatorDistanceToOwner >= Math.Max(3.5f, VanguardMovementAuthorityDoctrine.ClaimedCohesionRunDistanceMeters * 0.50f);
        bool catchUpPressure = snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.ClaimedCohesionStartMeters;
        bool stableSupportHold = VanguardOrchestratorAuthorityPolicy.ShouldHoldStableCohesion(snapshot, out var stableHoldReason);
        if (stableSupportHold && !storedClaimPressure)
        {
            reason = "stable_cohesion_hold_no_claim:" + stableHoldReason;
            return false;
        }

        bool externalResiduePressure = (snapshot.Movement.HasPath == true || snapshot.Orbit.Active)
            && snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.ClaimedCohesionExternalResidueStartMeters;
        bool shapePressure = !stableSupportHold
            && (!snapshot.SquadCohesion.UsefulPosition
                || snapshot.SquadCohesion.SectorDuplicate
                || snapshot.SquadCohesion.RearOverstacked
                || snapshot.MovementAuthority.MovementStallSuspect);
        bool pressure = catchUpPressure
            || externalResiduePressure
            || shapePressure
            || storedClaimPressure
            || initialBubbleAssignment;
        if (!pressure)
        {
            reason = "no_claim_pressure";
            return false;
        }

        reason = "claim_pressure:distance=" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", CultureInfo.InvariantCulture)
            + ":catchUp=" + Bool(catchUpPressure)
            + ":initialBubble=" + Bool(initialBubbleAssignment)
            + ":storedClaim=" + Bool(storedClaimPressure)
            + ":storedReason=" + Safe(storedClaimReason)
            + ":path=" + Bool(snapshot.Movement.HasPath == true)
            + ":orbit=" + Bool(snapshot.Orbit.Active)
            + ":useful=" + Bool(snapshot.SquadCohesion.UsefulPosition)
            + ":duplicate=" + Bool(snapshot.SquadCohesion.SectorDuplicate)
            + ":rearOverstacked=" + Bool(snapshot.SquadCohesion.RearOverstacked)
            + ":env=" + Safe(snapshot.SquadCohesion.TacticalEnvironmentKind);
        return true;
    }

    private static void RefreshClaims(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        if (snapshots == null || snapshots.Count == 0)
        {
            return;
        }

        var candidates = snapshots
            .Where(snapshot => snapshot != null
                && snapshot.Alive
                && !string.IsNullOrWhiteSpace(snapshot.BotProfileId)
                && snapshot.SquadCohesion.OwnerKnown
                && snapshot.SquadCohesion.OwnerReliableForActiveMovement
                && snapshot.SquadCohesion.OwnerPosition.HasValue)
            .GroupBy(snapshot => string.IsNullOrWhiteSpace(snapshot.OwnerProfileId) ? snapshot.SquadCohesion.OwnerProfileId : snapshot.OwnerProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var liveBotIds = new HashSet<string>(snapshots.Where(snapshot => snapshot != null && !string.IsNullOrWhiteSpace(snapshot.BotProfileId)).Select(snapshot => snapshot.BotProfileId), StringComparer.OrdinalIgnoreCase);
        lock (Sync)
        {
            foreach (string stale in ClaimsByBotProfileId.Keys.Where(key => !liveBotIds.Contains(key)).ToArray())
            {
                ClaimsByBotProfileId.Remove(stale);
                ActiveByBotProfileId.Remove(stale);
                PendingPlanByBotProfileId.Remove(stale);
                CooldownByBotProfileId.Remove(stale);
            }
        }

        foreach (var group in candidates)
        {
            var ordered = group
                .OrderBy(snapshot => Safe(snapshot.OperatorId), StringComparer.OrdinalIgnoreCase)
                .ThenBy(snapshot => Safe(snapshot.BotProfileId), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (ordered.Length == 0)
            {
                continue;
            }

            var ownerPositionValue = ordered[0].SquadCohesion.OwnerPosition;
            if (!ownerPositionValue.HasValue)
            {
                continue;
            }

            // The runtime owner fairness: only the group containing this tick's round-robin Operator
            // may advance owner-state, interior topology or candidate planning. Other owners keep
            // their existing claims untouched and cannot consume the shared NavMesh budget first.
            if (!ordered.Any(snapshot => VanguardCohesionPlanningBudget.ShouldPlanBot(snapshot.BotProfileId)))
            {
                continue;
            }

            Vector3 ownerPosition = ownerPositionValue.Value;
            var ownerForwardValue = ordered[0].SquadCohesion.OwnerForward;
            Vector3 rawOwnerForward = ownerForwardValue.HasValue ? Flatten(ownerForwardValue.Value) : Vector3.forward;
            if (rawOwnerForward.sqrMagnitude <= 0.001f)
            {
                rawOwnerForward = Vector3.forward;
            }
            rawOwnerForward.Normalize();
            Vector3 ownerForward = rawOwnerForward;

            OwnerClaimState ownerState;
            bool ownerMoved;
            bool ownerRotated;
            bool ownerStationary;
            bool directResumeObserved;
            float directSampleDistance;
            float ownerSpeed;
            DateTimeOffset stationarySinceUtc;
            lock (Sync)
            {
                OwnerStateByOwnerProfileId.TryGetValue(group.Key, out ownerState);
            }

            bool ownerIndoorContext = ordered.Count(IsIndoor) >= Math.Max(1, (ordered.Length + 1) / 2);
            float moveRefreshMeters = ownerIndoorContext
                ? VanguardMovementAuthorityDoctrine.IndoorSectorHoldOwnerMoveRefreshMeters
                : VanguardMovementAuthorityDoctrine.ClaimedCohesionOwnerMoveRefreshMeters;
            float rotateRefreshDegrees = ownerIndoorContext
                ? VanguardMovementAuthorityDoctrine.IndoorSectorHoldOwnerRotateRefreshDegrees
                : VanguardMovementAuthorityDoctrine.ClaimedCohesionOwnerRotateRefreshDegrees;

            if (string.IsNullOrWhiteSpace(ownerState.OwnerProfileId))
            {
                ownerMoved = true;
                ownerRotated = true;
                directResumeObserved = false;
                directSampleDistance = 0f;
                ownerSpeed = 0f;
            }
            else
            {
                double seconds = Math.Max(0.10d, (now - ownerState.ObservedAtUtc).TotalSeconds);
                Vector3 travelDelta = Flatten(ownerPosition - ownerState.Position);
                directSampleDistance = travelDelta.magnitude;
                ownerSpeed = directSampleDistance / (float)seconds;
                ownerMoved = directSampleDistance >= moveRefreshMeters;
                directResumeObserved = directSampleDistance >= VanguardContinuousCohesionLocomotionPolicy.StationaryClaimOwnerResumeSampleMeters
                    || (ownerSpeed >= VanguardContinuousCohesionLocomotionPolicy.StationaryClaimOwnerResumeSpeedMetersPerSecond
                        && directSampleDistance >= 0.35f);
                if ((directResumeObserved || ownerMoved) && travelDelta.sqrMagnitude >= 0.16f)
                {
                    ownerForward = travelDelta.normalized;
                }
                ownerRotated = Vector3.Angle(ownerState.Forward, ownerForward) >= rotateRefreshDegrees;
            }

            bool canonicalMotionAvailable = VanguardSquadTravelRouteMemory.TryGetOwnerStationaryState(
                group.Key,
                now,
                out var canonicalMotion,
                out var canonicalMotionReason);
            bool ownerStillCandidate = canonicalMotionAvailable
                && !canonicalMotion.OwnerMovingRecently
                && !directResumeObserved;
            ownerStationary = ownerStillCandidate
                && canonicalMotion.StationarySeconds >= VanguardContinuousCohesionLocomotionPolicy.ObservationDeploymentOwnerStillSeconds;
            stationarySinceUtc = ownerStillCandidate
                ? canonicalMotion.StationarySinceUtc
                : now;

            lock (Sync)
            {
                OwnerStateByOwnerProfileId[group.Key] = new OwnerClaimState(group.Key, ownerPosition, ownerForward, now, ownerSpeed, ownerStationary, stationarySinceUtc);
            }

            LogThrottled(
                "CanonicalStationaryAdmission|" + group.Key,
                now,
                TimeSpan.FromSeconds(4.0d),
                () => $"VANGUARD_CANONICAL_STATIONARY_ADMISSION owner={Safe(group.Key)}; canonicalAvailable={Bool(canonicalMotionAvailable)}; canonicalMoving={Bool(canonicalMotionAvailable && canonicalMotion.OwnerMovingRecently)}; canonicalStationaryFor={(canonicalMotionAvailable ? canonicalMotion.StationarySeconds : 0f):0.00}; directResume={Bool(directResumeObserved)}; directDistance={directSampleDistance:0.00}; directSpeed={ownerSpeed:0.00}; admitted={Bool(ownerStationary)}; route={Safe(canonicalMotionReason)}; doctrine=route_memory_owns_stationary_admission_direct_motion_only_preempts_and_never_starts_stationary; tag={VanguardPrimaryExecutionContract.CanonicalStationaryAdmissionStatusTag}; Tag={VanguardPrimaryExecutionContract.StationarySpatialTacticalPlacementStatusTag}");

            string ownerMode = ownerStationary ? "stationary_hold" : ownerSpeed >= VanguardMovementAuthorityDoctrine.ClaimedCohesionOwnerFastSpeed ? "fast_travel" : "moving_cohesion";
            VanguardInteriorPlanningDisposition interiorPlanning = VanguardInteriorSecurityPlanner.UpdateAssignments(
                group.Key,
                ordered,
                ownerPosition,
                rawOwnerForward,
                ownerStillCandidate,
                ownerStationary,
                stationarySinceUtc,
                now);
            if (interiorPlanning.AssignmentsCommittedThisTick)
            {
                SupersedeClaimsForAtomicInteriorDeployment(
                    group.Key,
                    interiorPlanning.AssignedBotProfileIds,
                    interiorPlanning.VolumeId,
                    now,
                    interiorPlanning.Reason);
            }

            // Vanguard predictive cohesion: while the player is progressing, formation claims lead the
            // sampled owner position slightly instead of continually chasing an already obsolete point.
            // Interior security assignments remain bound to the persistent volume and ignore this lead.
            float leadSeconds = ownerMode == "fast_travel" ? 2.25f : ownerMode == "moving_cohesion" ? 1.15f : 0f;
            float leadDistance = Math.Min(7.5f, ownerSpeed * leadSeconds);
            Vector3 formationOwnerPosition = ownerPosition + ownerForward * leadDistance;
            var dynamicLaneByBot = VanguardDynamicFormationPlanner.BuildLaneAssignments(group.Key, ordered, formationOwnerPosition, ownerForward, ownerMode, now);
            for (int index = 0; index < ordered.Length; index++)
            {
                var snapshot = ordered[index];
                InteriorSecurityAssignment interiorAssignment = default;
                bool hasInteriorAssignment = ownerStationary
                    && VanguardInteriorSecurityPlanner.TryGetAssignment(snapshot.BotProfileId, now, out interiorAssignment);
                string genericLane = dynamicLaneByBot.TryGetValue(snapshot.BotProfileId, out var dynamicLane)
                    ? dynamicLane
                    : LaneFor(index, ordered.Length, snapshot, ownerMode);
                string genericPurpose = PurposeFor(genericLane, ownerMode, snapshot);
                string lane = hasInteriorAssignment ? interiorAssignment.Lane : genericLane;
                string purpose = hasInteriorAssignment
                    ? interiorAssignment.Purpose + ":" + interiorAssignment.PortalKey
                    : genericPurpose;

                CohesionClaimState existing;
                CohesionClaimLeaseState activeLease;
                bool hasActiveLease;
                lock (Sync)
                {
                    ClaimsByBotProfileId.TryGetValue(snapshot.BotProfileId, out existing);
                    hasActiveLease = ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out activeLease);
                }

                if (VanguardHardReturnMovementExecutor.IsCrossLeasePhysicalRecoveryActive(snapshot.BotProfileId, now, out var physicalRecoveryReason))
                {
                    lock (Sync)
                    {
                        PendingPlanByBotProfileId.Remove(snapshot.BotProfileId);
                        if (!string.IsNullOrWhiteSpace(existing.BotProfileId) && existing.ValidUntilUtc <= now + TimeSpan.FromSeconds(4.0d))
                        {
                            existing.ValidUntilUtc = now + TimeSpan.FromSeconds(10.0d);
                            ClaimsByBotProfileId[snapshot.BotProfileId] = existing;
                        }
                    }

                    LogThrottled("PhysicalRecoveryClaimFreeze|" + snapshot.BotProfileId, now, TimeSpan.FromSeconds(8.0d),
                        $"VANGUARD_COHESION_CLAIM_FROZEN_FOR_PHYSICAL_RECOVERY operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(physicalRecoveryReason)}; existingClaim={Safe(existing.ClaimId)}; pendingPlanCleared=true; roleRedistribution=false; hardReturnOwnsRecovery=true; tag={VanguardHardReturnMovementExecutor.CrossLeasePhysicalRecoveryStatusTag}; claimTag={StatusTag}");
                    continue;
                }

                if (interiorPlanning.ShouldSuppressGenericStationaryFormation
                    && ownerStationary
                    && !hasInteriorAssignment
                    && !snapshot.MovementAuthority.HardOutsideBubble
                    && snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.TacticalBubbleMeters)
                {
                    lock (Sync)
                    {
                        PendingPlanByBotProfileId.Remove(snapshot.BotProfileId);
                    }
                    LogThrottled(
                        "ProvisionalInteriorHold|" + snapshot.BotProfileId + "|" + interiorPlanning.VolumeId,
                        now,
                        TimeSpan.FromSeconds(4.0d),
                        () => $"VANGUARD_PROVISIONAL_INTERIOR_HOLD owner={Safe(group.Key)}; operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; volume={Safe(interiorPlanning.VolumeId)}; reason={Safe(interiorPlanning.Reason)}; stationary=true; genericStationaryClaimPublished=false; existingClaimPreserved={Bool(!string.IsNullOrWhiteSpace(existing.BotProfileId))}; activeLeasePreserved={Bool(hasActiveLease)}; doctrine=wait_for_atomic_indoor_plan_without_creating_triangle_or_stealing_follow_authority; tag={VanguardPrimaryExecutionContract.StableEnvironmentAtomicInteriorDeploymentStatusTag}");
                    continue;
                }

                bool ignoreRotationForStableSupport = ownerRotated
                    && !ownerMoved
                    && snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.MovementPlanIgnoreOwnerRotationWhileCatchupMeters
                    && (snapshot.SquadCohesion.UsefulPosition || string.Equals(ownerMode, "stationary_hold", StringComparison.OrdinalIgnoreCase));
                bool sameInteriorClaim = hasInteriorAssignment
                    && !string.IsNullOrWhiteSpace(existing.BotProfileId)
                    && existing.ValidUntilUtc > now
                    && string.Equals(existing.OwnerProfileId, group.Key, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Lane, lane, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Purpose, purpose, StringComparison.OrdinalIgnoreCase)
                    && HorizontalDistance(existing.Anchor, interiorAssignment.Anchor) <= 1.25f;
                bool refresh = hasInteriorAssignment
                    ? !sameInteriorClaim
                    : ownerMoved || (ownerRotated && !ignoreRotationForStableSupport);

                if (VanguardOrchestratorAuthorityPolicy.ShouldFreezeCohesionClaimProduction(snapshot, out var authorityFreezeReason))
                {
                    if (!string.IsNullOrWhiteSpace(existing.BotProfileId) && existing.ValidUntilUtc <= now + TimeSpan.FromSeconds(4.0d))
                    {
                        existing.ValidUntilUtc = now + TimeSpan.FromSeconds(8.0d);
                        lock (Sync)
                        {
                            ClaimsByBotProfileId[snapshot.BotProfileId] = existing;
                        }
                    }

                    LogThrottled("ClaimFreeze|" + snapshot.BotProfileId + "|" + authorityFreezeReason, now, TimeSpan.FromSeconds(6.0d),
                        () => $"VANGUARD_COHESION_CLAIM_FREEZE operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(authorityFreezeReason)}; hasActiveLease={Bool(hasActiveLease)}; ownerMoved={Bool(ownerMoved)}; ownerRotated={Bool(ownerRotated)}; ownerMode={Safe(ownerMode)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; useful={Bool(snapshot.SquadCohesion.UsefulPosition)}; hasPath={Bool(snapshot.Movement.HasPath == true)}; tag={ExclusiveAuthorityStatusTag}; Tag={OrchestratorAuthorityStatusTag}; Tag={StatusTag}");
                    continue;
                }

                if (!hasActiveLease
                    && !hasInteriorAssignment
                    && VanguardPrimaryExecutionContract.IsIndoorPerimeterHoldCandidate(snapshot, ownerStationary, out var indoorHoldReason)
                    && !snapshot.MovementAuthority.HardOutsideBubble)
                {
                    if (!string.IsNullOrWhiteSpace(existing.BotProfileId) && existing.ValidUntilUtc <= now + TimeSpan.FromSeconds(12.0d))
                    {
                        existing.ValidUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.ClaimedCohesionStationaryReuseSeconds);
                        existing.StationaryHold = true;
                        existing.Purpose = "indoor_perimeter_hold";
                        lock (Sync)
                        {
                            ClaimsByBotProfileId[snapshot.BotProfileId] = existing;
                        }
                    }

                    LogThrottled("IndoorPerimeterHold|" + snapshot.BotProfileId + "|" + indoorHoldReason, now,
                        $"VANGUARD_INDOOR_PERIMETER_HOLD operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(indoorHoldReason)}; ownerMoved={Bool(ownerMoved)}; ownerRotated={Bool(ownerRotated)}; ownerMode={Safe(ownerMode)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; useful={Bool(snapshot.SquadCohesion.UsefulPosition)}; sector={Safe(snapshot.SquadCohesion.Sector)}; env={Safe(snapshot.SquadCohesion.TacticalEnvironmentKind)}; hasPath={Bool(snapshot.Movement.HasPath == true)}; doctrine=building_perimeter_hold_no_bubble_churn; tag={VanguardPrimaryExecutionContract.CohesionContractStatusTag}; Tag={StatusTag}");
                    continue;
                }

                if (!hasActiveLease
                    && sameInteriorClaim
                    && VanguardInteriorSecurityPlanner.IsVerifiedCoverageHold(snapshot, now, out var verifiedInteriorReason))
                {
                    existing.ValidUntilUtc = interiorAssignment.ExpiresAtUtc > existing.ValidUntilUtc
                        ? interiorAssignment.ExpiresAtUtc
                        : existing.ValidUntilUtc;
                    existing.StationaryHold = true;
                    lock (Sync)
                    {
                        ClaimsByBotProfileId[snapshot.BotProfileId] = existing;
                    }
                    LogThrottled("InteriorHold|" + snapshot.BotProfileId + "|" + interiorAssignment.PortalKey, now,
                        $"VANGUARD_INTERIOR_VOLUME_HOLD_PRESERVED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; volume={Safe(interiorAssignment.VolumeId)}; portal={Safe(interiorAssignment.PortalKey)}; reason={Safe(verifiedInteriorReason)}; playerMoving={Bool(!ownerStationary)}; ownerMoved={Bool(ownerMoved)}; ownerRotated={Bool(ownerRotated)}; anchor={FormatVector(interiorAssignment.Anchor)}; watch={FormatVector(interiorAssignment.WatchPoint)}; doctrine=guard_holds_while_player_moves_inside_same_volume; tag={VanguardPrimaryExecutionContract.InteriorVolumeSecurityStatusTag}; legacyInteriorTag={VanguardPrimaryExecutionContract.InteriorCoverageStatusTag}");
                    continue;
                }

                bool dynamicRoleCorrectionNeeded = RequiresDynamicRoleCorrection(existing, lane, purpose);
                if (!hasActiveLease
                    && VanguardOrchestratorAuthorityPolicy.ShouldHoldStableCohesion(snapshot, out var stableCohesionHoldReason)
                    && !dynamicRoleCorrectionNeeded
                    && !ownerMoved
                    && !ownerRotated)
                {
                    LogThrottled("StableHold|" + snapshot.BotProfileId + "|" + stableCohesionHoldReason, now,
                        $"VANGUARD_COHESION_STABLE_HOLD_PRESERVED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(stableCohesionHoldReason)}; ownerMoved={Bool(ownerMoved)}; ownerRotated={Bool(ownerRotated)}; ownerMode={Safe(ownerMode)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; useful={Bool(snapshot.SquadCohesion.UsefulPosition)}; hasPath={Bool(snapshot.Movement.HasPath == true)}; tag={OrchestratorAuthorityStatusTag}; Tag={StatusTag}");
                    continue;
                }

                if (!hasActiveLease
                    && ShouldPreserveActiveReturnCommand(snapshot, ownerMoved, ownerRotated, ownerMode, now, out var commandProtectReason))
                {
                    LogThrottled("CommandProtect|" + snapshot.BotProfileId + "|" + commandProtectReason, now,
                        () => $"VANGUARD_ACTIVE_RETURN_COMMAND_PRESERVED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(commandProtectReason)}; ownerMoved={Bool(ownerMoved)}; ownerRotated={Bool(ownerRotated)}; ownerMode={Safe(ownerMode)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; speed={snapshot.RealSpeed:0.00}; hasPath={Bool(snapshot.Movement.HasPath == true)}; tag={HardReturnAlertStatusTag}; Tag={HostileIndoorMovementPlanStatusTag}");
                    continue;
                }

                if (hasActiveLease
                    && hasInteriorAssignment
                    && activeLease.StationaryHold
                    && string.Equals(activeLease.Lane, lane, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(activeLease.Purpose, purpose, StringComparison.OrdinalIgnoreCase)
                    && HorizontalDistance(activeLease.Anchor, interiorAssignment.Anchor) <= 1.25f)
                {
                    LogThrottled("InteriorLease|" + snapshot.BotProfileId + "|" + interiorAssignment.PortalKey, now,
                        $"VANGUARD_INTERIOR_VOLUME_LEASE_PRESERVED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(activeLease.LeaseId)}; volume={Safe(interiorAssignment.VolumeId)}; portal={Safe(interiorAssignment.PortalKey)}; playerMoving={Bool(!ownerStationary)}; anchor={FormatVector(activeLease.Anchor)}; doctrine=no_replan_for_same_volume_same_access; tag={VanguardPrimaryExecutionContract.InteriorVolumeSecurityStatusTag}; legacyInteriorTag={VanguardPrimaryExecutionContract.InteriorCoverageStatusTag}");
                    continue;
                }

                bool ownerMovedForActivePlan = hasInteriorAssignment && sameInteriorClaim ? false : ownerMoved;
                bool ownerRotatedForActivePlan = hasInteriorAssignment && sameInteriorClaim ? false : ownerRotated;
                if (hasActiveLease
                    && ShouldProtectActiveMovementPlan(snapshot, activeLease, ownerMovedForActivePlan, ownerRotatedForActivePlan, ownerIndoorContext, now, out var planProtectReason))
                {
                    if (!VanguardCohesionPlanningBudget.ShouldPlanBot(snapshot.BotProfileId))
                    {
                        LogThrottled("PlanQueueDeferred|" + snapshot.BotProfileId, now, TimeSpan.FromSeconds(8.0d),
                            $"VANGUARD_COHESION_PLAN_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; phase=queued_plan; selected={Safe(VanguardCohesionPlanningBudget.SelectedBotProfileId)}; activeLeasePreserved=true; reason=owner_fair_round_robin; tag={VanguardRuntimeConvergenceStatusTags.IncrementalCohesionPlanning}; budgetTag={VanguardRuntimeConvergenceStatusTags.BoundedCohesionPathBudget}");
                        continue;
                    }

                    if (TryBuildQueuedPlanClaim(snapshot, ownerPosition, ownerForward, lane, purpose, ownerMode, ownerSpeed, now, out var pendingClaim, out var pendingReason))
                    {
                        QueuePendingPlan(snapshot.BotProfileId, pendingClaim, now, planProtectReason + ":" + pendingReason);
                    }
                    else
                    {
                        LogThrottled("planQueueSkip|" + snapshot.BotProfileId + "|" + planProtectReason, now,
                            $"VANGUARD_MOVEMENT_PLAN_QUEUE_SKIPPED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; active={Safe(activeLease.LeaseId)}; reason={Safe(planProtectReason)}; pending={Safe(pendingReason)}; ownerMoved={Bool(ownerMoved)}; ownerRotated={Bool(ownerRotated)}; ownerMode={Safe(ownerMode)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; tag={HostileIndoorMovementPlanStatusTag}; Tag={CohesionAnchorsRunStatusTag}");
                    }

                    continue;
                }

                string stableRefreshReason = "not_evaluated";
                DateTimeOffset claimCooldownUntil;
                lock (Sync)
                {
                    CooldownByBotProfileId.TryGetValue(snapshot.BotProfileId, out claimCooldownUntil);
                }

                if (claimCooldownUntil > now && !snapshot.MovementAuthority.HardOutsideBubble)
                {
                    LogThrottled("ClaimRefreshThrottled|" + snapshot.BotProfileId, now,
                        $"VANGUARD_CLAIM_REFRESH_THROTTLED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; remaining={(claimCooldownUntil - now).TotalSeconds:0.0}; ownerMoved={Bool(ownerMoved)}; ownerRotated={Bool(ownerRotated)}; ownerMode={Safe(ownerMode)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; doctrine=anchor_failure_or_path_reject_cooldown_prevents_claim_churn; tag={VanguardPrimaryExecutionContract.CohesionChurnThrottleStatusTag}; Tag={StatusTag}");
                    continue;
                }

                if (!refresh
                    && !string.IsNullOrWhiteSpace(existing.BotProfileId)
                    && existing.ValidUntilUtc > now
                    && string.Equals(existing.OwnerProfileId, group.Key, StringComparison.OrdinalIgnoreCase)
                    && !ShouldRefreshStableClaim(snapshot, existing, lane, purpose, ownerMode, now, out stableRefreshReason))
                {
                    DateTimeOffset extendedUntil = now + TimeSpan.FromSeconds(existing.StationaryHold
                        ? VanguardMovementAuthorityDoctrine.ClaimedCohesionStationaryReuseSeconds
                        : VanguardMovementAuthorityDoctrine.ClaimedCohesionMovingReuseSeconds);
                    if (extendedUntil > existing.ValidUntilUtc)
                    {
                        existing.ValidUntilUtc = extendedUntil;
                        lock (Sync)
                        {
                            ClaimsByBotProfileId[snapshot.BotProfileId] = existing;
                        }
                    }

                    LogThrottled("claimReuse|" + snapshot.BotProfileId + "|" + existing.ClaimId, now,
                        $"VANGUARD_CLAIM_REUSED {existing.Summary}; reason=owner_stable; requestedLane={Safe(lane)}; requestedPurpose={Safe(purpose)}; ownerMode={Safe(ownerMode)}; ownerSpeed={ownerSpeed:0.00}; moveRefresh={moveRefreshMeters:0.0}; rotateRefresh={rotateRefreshDegrees:0.0}; tag={StatusTag}; Tag={CohesionAnchorsRunStatusTag}");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(existing.BotProfileId) && !refresh)
                {
                    LogThrottled("claimRefresh|" + snapshot.BotProfileId + "|" + stableRefreshReason, now,
                        $"VANGUARD_CLAIM_REFRESH_NEEDED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; previous={Safe(existing.ClaimId)}; reason={Safe(stableRefreshReason)}; oldLane={Safe(existing.Lane)}; newLane={Safe(lane)}; ownerMode={Safe(ownerMode)}; tag={StatusTag}; Tag={CohesionAnchorsRunStatusTag}");
                }

                bool forceRallyFallback = snapshot.SquadCohesion.OperatorDistanceToOwner >= Math.Max(78.0f, VanguardMovementAuthorityDoctrine.CohesionLanePreservingFallbackDistanceMeters)
                    && (stableRefreshReason.StartsWith("anchor_very_far", StringComparison.OrdinalIgnoreCase)
                        || stableRefreshReason.StartsWith("operator_far", StringComparison.OrdinalIgnoreCase));

                if (!VanguardCohesionPlanningBudget.ShouldPlanBot(snapshot.BotProfileId))
                {
                    LogThrottled("ClaimDeferred|" + snapshot.BotProfileId, now, TimeSpan.FromSeconds(8.0d),
                        $"VANGUARD_COHESION_PLAN_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; phase=claim_refresh; selected={Safe(VanguardCohesionPlanningBudget.SelectedBotProfileId)}; existingClaimPreserved={Bool(!string.IsNullOrWhiteSpace(existing.BotProfileId))}; reason=owner_fair_round_robin; tag={VanguardRuntimeConvergenceStatusTags.IncrementalCohesionPlanning}; budgetTag={VanguardRuntimeConvergenceStatusTags.BoundedCohesionPathBudget}");
                    continue;
                }

                CohesionClaimState claim;
                int buildDeferralBefore = VanguardCohesionPlanningBudget.DeferralSerial;
                bool built = TryBuildClaim(
                    snapshot,
                    hasInteriorAssignment ? ownerPosition : formationOwnerPosition,
                    ownerForward,
                    lane,
                    purpose,
                    ownerMode,
                    ownerSpeed,
                    now,
                    out claim,
                    formationOwnerPosition,
                    genericLane,
                    genericPurpose);
                bool buildDeferred = VanguardCohesionPlanningBudget.DeferralSerial != buildDeferralBefore;
                if (!built && buildDeferred)
                {
                    LogThrottled("CandidateDeferred|" + snapshot.BotProfileId, now, TimeSpan.FromSeconds(4.0d),
                        $"VANGUARD_COHESION_CANDIDATE_SCAN_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lane={Safe(lane)}; usedPaths={VanguardCohesionPlanningBudget.UsedPathCalculations}; maxPaths={VanguardCohesionPlanningBudget.MaxPathCalculationsPerTick}; existingClaimPreserved={Bool(!string.IsNullOrWhiteSpace(existing.BotProfileId))}; fallbackSuppressed=true; hardReturnSuppressed=true; tag={VanguardRuntimeConvergenceStatusTags.BoundedCohesionPathBudget}; planningTag={VanguardRuntimeConvergenceStatusTags.IncrementalCohesionPlanning}");
                    continue;
                }

                if (hasInteriorAssignment
                    && !string.Equals(claim.Lane, interiorAssignment.Lane, StringComparison.OrdinalIgnoreCase))
                {
                    // TryBuildClaim invalidated a non-executable interior reservation and returned a
                    // generic Vanguard formation claim in the same cycle. Keep the caller's lane/purpose
                    // coherent with the actual claim so later logging/fallback decisions do not treat
                    // the generic destination as an interior mission.
                    hasInteriorAssignment = false;
                    lane = genericLane;
                    purpose = genericPurpose;
                }
                if (!built)
                {
                    int fallbackDeferralBefore = VanguardCohesionPlanningBudget.DeferralSerial;
                    bool laneFallbackBuilt = snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.ClaimedCohesionRallyFallbackDistanceMeters
                        && TryBuildLanePreservingFallbackClaim(snapshot, ownerPosition, ownerForward, lane, purpose, ownerMode, ownerSpeed, now, out claim);
                    bool fallbackBuilt = laneFallbackBuilt
                        || (forceRallyFallback
                            && TryBuildRallyFallbackClaim(snapshot, ownerPosition, ownerForward, ownerMode, ownerSpeed, now, out claim));
                    bool fallbackDeferred = VanguardCohesionPlanningBudget.DeferralSerial != fallbackDeferralBefore;
                    if (!fallbackBuilt && fallbackDeferred)
                    {
                        LogThrottled("FallbackDeferred|" + snapshot.BotProfileId, now, TimeSpan.FromSeconds(4.0d),
                            $"VANGUARD_COHESION_CANDIDATE_SCAN_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lane={Safe(lane)}; phase=fallback; usedPaths={VanguardCohesionPlanningBudget.UsedPathCalculations}; maxPaths={VanguardCohesionPlanningBudget.MaxPathCalculationsPerTick}; hardReturnSuppressed=true; failureCooldownSuppressed=true; existingClaimPreserved={Bool(!string.IsNullOrWhiteSpace(existing.BotProfileId))}; tag={VanguardRuntimeConvergenceStatusTags.BoundedCohesionPathBudget}; planningTag={VanguardRuntimeConvergenceStatusTags.IncrementalCohesionPlanning}");
                        continue;
                    }
                    if (!fallbackBuilt)
                    {
                        string fallbackOutcome = "not_attempted";
                        if (VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var buildFailRecord) && buildFailRecord.BotOwner != null && !buildFailRecord.BotOwner.IsDead)
                        {
                            Vector3 buildFailBotPosition = ResolveBotPosition(buildFailRecord.BotOwner);
                            if (TryIssuePathSafeHardReturnFallback(snapshot, buildFailRecord.BotOwner, buildFailBotPosition, now, "anchor_build_failed:" + lane + ":" + stableRefreshReason, out fallbackOutcome))
                            {
                                SetCooldown(snapshot.BotProfileId, now, Math.Max(8.0f, VanguardMovementAuthorityDoctrine.ClaimedCohesionFailureCooldownSeconds * 2.5f));
                                LogThrottled("BuildFailFallback|" + snapshot.BotProfileId + "|" + lane, now,
                                    $"VANGUARD_ANCHOR_BUILD_FAIL_TO_HARD_RETURN operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lane={Safe(lane)}; purpose={Safe(purpose)}; fallback={Safe(fallbackOutcome)}; ownerMode={Safe(ownerMode)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; tag={HardReturnAlertStatusTag}; Tag={StatusTag}");
                                continue;
                            }
                        }
                        LogThrottled("claimFail|" + snapshot.BotProfileId + "|" + lane, now,
                            $"VANGUARD_CLAIM_REFRESH_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; owner={Safe(group.Key)}; lane={Safe(lane)}; purpose={Safe(purpose)}; reason=anchor_failed; ownerMode={Safe(ownerMode)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; env={Safe(snapshot.SquadCohesion.TacticalEnvironmentKind)}; fallback=false; hardReturnFallback={Safe(fallbackOutcome)}; tag={StatusTag}; Tag={CombatHoldMedicalCatchupStatusTag}; Tag={HostileIndoorMovementPlanStatusTag}; Tag={HardReturnAlertStatusTag}");
                        continue;
                    }

                    SetCooldown(snapshot.BotProfileId, now, Math.Max(8.0f, VanguardMovementAuthorityDoctrine.ClaimedCohesionFailureCooldownSeconds * 2.5f));
                    LogThrottled("claimFallback|" + snapshot.BotProfileId, now,
                        $"VANGUARD_LANE_PRESERVING_FALLBACK_ASSIGNED {claim.Summary}; previousLane={Safe(lane)}; previousPurpose={Safe(purpose)}; reason={(laneFallbackBuilt ? "anchor_failed_lane_preserved" : "emergency_rally_bubble")}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; ownerMode={Safe(ownerMode)}; tag={VanguardMovementAuthorityDoctrine.LanePreservingFallbackStatusTag}; CompactTag={VanguardMovementAuthorityDoctrine.CompactLanesStatusTag}; Tag={CohesionAnchorsRunStatusTag}; Tag={HostileIndoorMovementPlanStatusTag}");
                }
                else if (forceRallyFallback)
                {
                    SetCooldown(snapshot.BotProfileId, now, Math.Max(6.0f, VanguardMovementAuthorityDoctrine.ClaimedCohesionFailureCooldownSeconds * 2.0f));
                    LogThrottled("claimRallyVeryFar|" + snapshot.BotProfileId, now,
                        $"VANGUARD_RALLY_FALLBACK_ASSIGNED {claim.Summary}; previousLane={Safe(lane)}; previousPurpose={Safe(purpose)}; reason={Safe(stableRefreshReason)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; ownerMode={Safe(ownerMode)}; tag={HostileIndoorMovementPlanStatusTag}; Tag={CohesionAnchorsRunStatusTag}");
                }

                string finalSpacingReason;
                bool finalSpacingRejected = hasInteriorAssignment
                    ? !VanguardInteriorSecurityPlanner.IsCollectiveAssignmentSpacingValid(
                        snapshot.OwnerProfileId,
                        snapshot.BotProfileId,
                        claim.Anchor,
                        VanguardMovementAuthorityDoctrine.InteriorMissionArrivalSpacingMeters,
                        now,
                        out finalSpacingReason)
                    : IsAnchorTooCloseToExistingClaim(
                        snapshot,
                        claim.Anchor,
                        VanguardMovementAuthorityDoctrine.CohesionMinOperatorSpacingMeters + 1.0f,
                        out finalSpacingReason);
                if (finalSpacingRejected)
                {
                    bool recoveredFromInteriorSpacingReject = false;
                    if (hasInteriorAssignment)
                    {
                        // Budget exhaustion is not a sector invalidation signal. Preserve the active
                        // assignment and existing claim until the generic anti-stack alternative can
                        // actually be evaluated on this Operator's next owner-fair planning turn.
                        if (!VanguardCohesionPlanningBudget.CanStartCandidate(2))
                        {
                            VanguardCohesionPlanningBudget.MarkDeferred();
                            LogThrottled("SpacingFallbackDeferred|" + snapshot.BotProfileId, now, TimeSpan.FromSeconds(4.0d),
                                $"VANGUARD_COHESION_CANDIDATE_SCAN_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; phase=interior_spacing_generic_fallback; usedPaths={VanguardCohesionPlanningBudget.UsedPathCalculations}; maxPaths={VanguardCohesionPlanningBudget.MaxPathCalculationsPerTick}; existingClaimPreserved=true; interiorAssignmentPreserved=true; tag={VanguardRuntimeConvergenceStatusTags.BoundedCohesionPathBudget}");
                            continue;
                        }

                        VanguardInteriorSecurityPlanner.InvalidateAssignment(
                            snapshot.BotProfileId,
                            now,
                            "interior_final_antistack_reject:" + finalSpacingReason);

                        if (TryBuildClaim(
                                snapshot,
                                formationOwnerPosition,
                                ownerForward,
                                genericLane,
                                genericPurpose,
                                ownerMode,
                                ownerSpeed,
                                now,
                                out var genericSpacingFallback)
                            && !IsAnchorTooCloseToExistingClaim(
                                snapshot,
                                genericSpacingFallback.Anchor,
                                VanguardMovementAuthorityDoctrine.CohesionMinOperatorSpacingMeters + 1.0f,
                                out _))
                        {
                            claim = genericSpacingFallback;
                            hasInteriorAssignment = false;
                            lane = genericLane;
                            purpose = genericPurpose;
                            recoveredFromInteriorSpacingReject = true;
                            LogThrottled("InteriorSpacingFallback|" + snapshot.BotProfileId, now,
                                $"VANGUARD_INTERIOR_ANTISTACK_GENERIC_FALLBACK operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; rejectedSector={Safe(interiorAssignment.PortalKey)}; rejectedReason={Safe(finalSpacingReason)}; fallbackLane={Safe(claim.Lane)}; fallbackAnchor={FormatVector(claim.Anchor)}; mutation=replace_non_executable_interior_claim_with_generic_claim; doctrine=final_physical_spacing_reject_cannot_leave_operator_without_cohesion_destination; Tag={VanguardPrimaryExecutionContract.InteriorExecutableMissionStatusTag}; antiStackTag={VanguardMovementAuthorityDoctrine.FinalAntiStackStatusTag}; Tag={StatusTag}");
                        }
                    }

                    if (!recoveredFromInteriorSpacingReject)
                    {
                        SetCooldown(snapshot.BotProfileId, now, Math.Max(3.0f, VanguardMovementAuthorityDoctrine.ClaimedCohesionFailureCooldownSeconds));
                        LogThrottled("FinalAntiStack|" + snapshot.BotProfileId + "|" + lane, now,
                            $"VANGUARD_FINAL_ANTISTACK_REJECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lane={Safe(claim.Lane)}; purpose={Safe(claim.Purpose)}; reason={Safe(finalSpacingReason)}; anchor={FormatVector(claim.Anchor)}; minSpacing={VanguardMovementAuthorityDoctrine.CohesionMinOperatorSpacingMeters + 1.0f:0.0}; action=skip_claim_until_next_stable_tick; tag={VanguardMovementAuthorityDoctrine.FinalAntiStackStatusTag}; Tag={StatusTag}");
                        continue;
                    }
                }

                lock (Sync)
                {
                    ClaimsByBotProfileId[snapshot.BotProfileId] = claim;
                }

                if (hasInteriorAssignment && claim.UsesInteriorPathContract)
                {
                    VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.StableEnvironmentAtomicInteriorDeploymentStatusTag,
                        $"VANGUARD_INTERIOR_ASSIGNMENT_APPLIED owner={Safe(group.Key)}; operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; volume={Safe(interiorAssignment.VolumeId)}; portal={Safe(interiorAssignment.PortalKey)}; lane={Safe(claim.Lane)}; purpose={Safe(claim.Purpose)}; anchor={FormatVector(claim.Anchor)}; watch={FormatVector(interiorAssignment.WatchPoint)}; interiorPathContract=true; interiorCoverage=true; score={claim.Score:0.0}; path={claim.PathDistanceMeters:0.0}; collectiveSpacing=true; oldGenericClaimsSuperseded=true; tag={VanguardPrimaryExecutionContract.StableEnvironmentAtomicInteriorDeploymentStatusTag}; pathTag={VanguardPrimaryExecutionContract.InteriorPathContractStatusTag}");
                }

                LogThrottled("claim|" + snapshot.BotProfileId + "|" + claim.ClaimId, now,
                    () => $"VANGUARD_CLAIM_ASSIGNED {claim.Summary}; reason=ownerMoved_{Bool(ownerMoved)}_ownerRotated_{Bool(ownerRotated)}; ownerMode={Safe(ownerMode)}; ownerSpeed={ownerSpeed:0.00}; predictiveLead={leadDistance:0.00}; squadCount={ordered.Length}; PredictiveTag={VanguardPrimaryExecutionContract.PredictiveCohesionStatusTag}; laneDoctrine=dynamic_lead_side_rear_or_persistent_interior_volume_access; roleCorrection={Bool(dynamicRoleCorrectionNeeded)}; moveRefresh={moveRefreshMeters:0.0}; rotateRefresh={rotateRefreshDegrees:0.0}; tag={StatusTag}; Tag={CohesionAnchorsRunStatusTag}; Tag={VanguardMovementAuthorityDoctrine.FormationLanesStatusTag}; CompactTag={VanguardMovementAuthorityDoctrine.CompactLanesStatusTag}");
            }
        }
    }

    private static void TryStartClaimLeases(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now, bool allowFreshPathValidation)
    {
        if (snapshots == null || snapshots.Count == 0)
        {
            return;
        }

        int activeCount;
        lock (Sync)
        {
            activeCount = ActiveByBotProfileId.Count;
        }

        int maxLeaseBudget = ResolveDynamicMaxActiveLeases(snapshots);
        int maxNewStarts = Math.Max(0, maxLeaseBudget - activeCount);
        if (maxNewStarts <= 0)
        {
            return;
        }

        foreach (var snapshot in snapshots.OrderByDescending(ScoreStartCandidate))
        {
            if (maxNewStarts <= 0)
            {
                return;
            }

            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            {
                continue;
            }

            if (VanguardOrchestratorAuthorityPolicy.ShouldFreezeCohesionClaimProduction(snapshot, out var authorityStartFreezeReason))
            {
                LogThrottled("StartFreeze|" + snapshot.BotProfileId + "|" + authorityStartFreezeReason, now,
                    $"VANGUARD_COHESION_LEASE_START_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(authorityStartFreezeReason)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; hasPath={Bool(snapshot.Movement.HasPath == true)}; tag={ExclusiveAuthorityStatusTag}; Tag={StatusTag}");
                continue;
            }

            if (!string.Equals(snapshot.MovementAuthority.BrokerPlan.Contract.RequestKind, VanguardMovementContractPolicy.ClaimedCohesionSlot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CohesionClaimState claim;
            DateTimeOffset cooldownUntil;
            lock (Sync)
            {
                if (ActiveByBotProfileId.ContainsKey(snapshot.BotProfileId))
                {
                    continue;
                }

                if (!ClaimsByBotProfileId.TryGetValue(snapshot.BotProfileId, out claim) || claim.ValidUntilUtc <= now)
                {
                    continue;
                }

                if (CooldownByBotProfileId.TryGetValue(snapshot.BotProfileId, out cooldownUntil) && cooldownUntil > now)
                {
                    LogThrottled("cooldown|" + snapshot.BotProfileId, now,
                        $"VANGUARD_CLAIM_LEASE_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=cooldown; remaining={(cooldownUntil - now).TotalSeconds:0.0}; claim={Safe(claim.ClaimId)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; tag={StatusTag}");
                    continue;
                }
            }

            string gate = CheckStartGate(snapshot, now);
            if (!string.Equals(gate, "none", StringComparison.OrdinalIgnoreCase))
            {
                LogThrottled("gate|" + snapshot.BotProfileId + "|" + gate, now,
                    $"VANGUARD_CLAIM_LEASE_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(gate)}; claim={Safe(claim.ClaimId)}; lane={Safe(claim.Lane)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; tag={StatusTag}");
                continue;
            }

            if (VanguardMainIntentScheduler.HasBlockingPrimaryWindow(snapshot.BotProfileId, now, out var blockingReason))
            {
                LogThrottled("primary|" + snapshot.BotProfileId + "|" + blockingReason, now,
                    $"VANGUARD_CLAIM_LEASE_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=primary_window_busy:{Safe(blockingReason)}; claim={Safe(claim.ClaimId)}; tag={StatusTag}");
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record) || record.BotOwner == null || record.BotOwner.IsDead)
            {
                LogThrottled("botowner|" + snapshot.BotProfileId, now,
                    $"VANGUARD_CLAIM_LEASE_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=bot_owner_missing_or_dead; claim={Safe(claim.ClaimId)}; tag={StatusTag}");
                continue;
            }

            Vector3 botPosition = ResolveBotPosition(record.BotOwner);
            float anchorDistance = HorizontalDistance(botPosition, claim.Anchor);
            if (!ShouldDriveClaim(snapshot, claim, anchorDistance, now, out var driveReason))
            {
                LogThrottled("driveGate|" + snapshot.BotProfileId + "|" + driveReason, now,
                    $"VANGUARD_CLAIM_LEASE_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(driveReason)}; claim={Safe(claim.ClaimId)}; anchorDistance={anchorDistance:0.0}; ownerDistance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; path={Bool(snapshot.Movement.HasPath == true)}; orbit={Bool(snapshot.Orbit.Active)}; tag={StatusTag}");
                continue;
            }

            if (VanguardSquadTravelCohesionExecutor.HasActiveTravelAuthority(snapshot.BotProfileId))
            {
                LogThrottled("travelOwnsMovement|" + snapshot.BotProfileId, now,
                    $"VANGUARD_CLAIM_OBSERVATION_ONLY operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; claim={Safe(claim.ClaimId)}; lane={Safe(claim.Lane)}; reason=monotonic_travel_corridor_owns_command; policy=claim_informs_role_and_lane_but_never_validates_or_writes_a_competing_path; tag={VanguardSquadTravelRouteMemory.StatusTag}");
                continue;
            }

            bool atomicHandoffReady = TryGetAtomicHandoffTicket(
                snapshot,
                claim,
                botPosition,
                now,
                out var handoffTicket,
                out var handoffTicketReason);
            string pathSummary;
            float pathDistance;
            string pathRejectReason;
            if (atomicHandoffReady)
            {
                pathSummary = handoffTicket.PathSummary + ";atomic_handoff_ticket=true";
                pathDistance = handoffTicket.PathDistanceMeters;
                pathRejectReason = "none";
            }
            else
            {
                if (!allowFreshPathValidation)
                {
                    LogThrottled("atomicHandoffAwaitPlanning|" + snapshot.BotProfileId + "|" + handoffTicketReason, now,
                        $"VANGUARD_CLAIM_START_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; claim={Safe(claim.ClaimId)}; reason=optional_planning_deferred_without_atomic_ticket:{Safe(handoffTicketReason)}; claimPreserved=true; cooldownWritten=false; tag={VanguardContinuousCohesionLocomotionPolicy.AtomicDeploymentStatusTag}");
                    continue;
                }

                if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner(
                        "CohesionNavMeshPath",
                        snapshot.OwnerProfileId,
                        1,
                        VanguardContinuousCohesionLocomotionPolicy.CohesionNavMeshPathsPerFrame,
                        out var pathBudgetReason))
                {
                    // Planning is deferred without poisoning the claim or starving other owners.
                    LogThrottled("claimPathBudget|" + snapshot.BotProfileId, now,
                        $"VANGUARD_CLAIM_PATH_BUDGET_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; claim={Safe(claim.ClaimId)}; reason={Safe(pathBudgetReason)}; claimPreserved=true; cooldownWritten=false; tag={VanguardContinuousCohesionLocomotionPolicy.AtomicDeploymentStatusTag}");
                    continue;
                }

                if (TryValidateClaimPath(snapshot, claim, botPosition, claim.Anchor, out pathSummary, out pathDistance, out pathRejectReason))
                {
                    pathRejectReason = "none";
                }
                else
                {
                    SetCooldown(snapshot.BotProfileId, now, Math.Max(1.0f, VanguardMovementAuthorityDoctrine.ClaimedCohesionFailureCooldownSeconds * 0.50f));
                    LogThrottled("path|" + snapshot.BotProfileId + "|" + claim.ClaimId + "|" + pathRejectReason, now,
                    $"VANGUARD_CLAIM_PATH_REJECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(pathRejectReason)}; claim={Safe(claim.ClaimId)}; lane={Safe(claim.Lane)}; ownerDistance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; claimPath={claim.PathDistanceMeters:0.0}; path={Safe(pathSummary)}; tag={PathAlertRecoveryStatusTag}; Tag={StatusTag}");
                    if (TryIssuePathSafeHardReturnFallback(snapshot, record.BotOwner, botPosition, now, "claim_path_rejected:" + pathRejectReason + ":" + claim.Lane, out var fallbackReason))
                    {
                    lock (Sync)
                    {
                        ClaimsByBotProfileId.Remove(snapshot.BotProfileId);
                        PendingPlanByBotProfileId.Remove(snapshot.BotProfileId);
                    }
                    LogThrottled("RejectFallback|" + snapshot.BotProfileId + "|" + fallbackReason, now,
                        $"VANGUARD_REJECT_TO_HARD_RETURN operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; rejectedClaim={Safe(claim.ClaimId)}; lane={Safe(claim.Lane)}; rejectReason={Safe(pathRejectReason)}; fallback={Safe(fallbackReason)}; policy=rejected_tactical_or_rally_never_means_idle; tag={HardReturnAlertStatusTag}; Tag={PathAlertRecoveryStatusTag}");
                }
                else
                {
                    LogThrottled("RejectFallbackFailed|" + snapshot.BotProfileId + "|" + fallbackReason, now,
                        $"VANGUARD_REJECT_TO_HARD_RETURN_FAILED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; rejectedClaim={Safe(claim.ClaimId)}; lane={Safe(claim.Lane)}; rejectReason={Safe(pathRejectReason)}; fallback={Safe(fallbackReason)}; action=keep_existing_path_until_next_tick; tag={HardReturnAlertStatusTag}; Tag={PathAlertRecoveryStatusTag}");
                }
                continue;
                }
            }

            if (!VanguardMainIntentScheduler.TryOpenCloseCohesion(snapshot, now, out var windowId, out var openReason))
            {
                LogThrottled("open|" + snapshot.BotProfileId + "|" + openReason, now,
                    $"VANGUARD_CLAIM_LEASE_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=scheduler_denied:{Safe(openReason)}; claim={Safe(claim.ClaimId)}; tag={StatusTag}");
                continue;
            }

            if (NeedsExternalPreempt(snapshot))
            {
                var preempt = VanguardExternalAuthorityAdapter.RequestOrbitAuthorityQuiesce(
                    record.BotOwner,
                    snapshot,
                    "claimed_cohesion:" + claim.Lane,
                    TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.ClaimedCohesionMaxDurationSeconds + VanguardMovementAuthorityDoctrine.OrbitQuiesceRefreshSeconds + 3.0f),
                    now);
                string softDriveReason = "not_evaluated";
                if (!preempt.CanDriveMovement && !CanSoftDriveAfterNonCriticalPreempt(snapshot, preempt, claim, out softDriveReason))
                {
                    SetCooldown(snapshot.BotProfileId, now, VanguardMovementAuthorityDoctrine.ClaimedCohesionFailureCooldownSeconds);
                    VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "external_preempt_not_granted:" + preempt.Outcome, preempt.Summary, windowId);
                    VanguardClientDiagnosticsLog.Info(StatusTag,
                        $"VANGUARD_CLAIM_LEASE_ABORTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=external_preempt_not_granted; outcome={preempt.Outcome}; claim={Safe(claim.ClaimId)}; preempt={Safe(preempt.Summary)}; tag={StatusTag}");
                    continue;
                }

                if (!preempt.CanDriveMovement)
                {
                    VanguardClientDiagnosticsLog.Info(StatusTag,
                        $"VANGUARD_CLAIM_SOFT_PREEMPT_GRANTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(softDriveReason)}; outcome={preempt.Outcome}; claim={Safe(claim.ClaimId)}; preempt={Safe(preempt.Summary)}; tag={StatusTag}");
                }
            }

            string leaseId = "claim_cohesion_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId);
            bool sprint = ResolveSprintForClaim(snapshot, claim, anchorDistance, pathDistance, out var paceReason);
            DateTimeOffset maxUntil = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.ClaimedCohesionMaxDurationSeconds);
            bool issued = VanguardReturnMovementCommandStore.Issue(
                leaseId,
                snapshot.OperatorId,
                snapshot.BotProfileId,
                claim.Anchor,
                claim.AnchorRadiusMeters,
                sprint,
                now,
                maxUntil,
                VanguardMovementContractPolicy.ClaimedCohesionSlot,
                pathSummary,
                pathDistance,
                out var commandResult);
            if (!issued)
            {
                SetCooldown(snapshot.BotProfileId, now, VanguardMovementAuthorityDoctrine.ClaimedCohesionFailureCooldownSeconds);
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "move_bridge_rejected:" + commandResult, claim.Summary, windowId);
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"VANGUARD_CLAIM_LEASE_ABORTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason=move_bridge_rejected:{Safe(commandResult)}; claim={Safe(claim.ClaimId)}; tag={StatusTag}");
                continue;
            }

            var lease = new CohesionClaimLeaseState
            {
                LeaseId = leaseId,
                WindowId = windowId,
                ClaimId = claim.ClaimId,
                OwnerProfileId = claim.OwnerProfileId,
                OperatorId = snapshot.OperatorId,
                BotProfileId = snapshot.BotProfileId,
                Lane = claim.Lane,
                Purpose = claim.Purpose,
                Anchor = claim.Anchor,
                AnchorRadiusMeters = claim.AnchorRadiusMeters,
                StartedAtUtc = now,
                MinUntilUtc = now + TimeSpan.FromSeconds(claim.StationaryHold ? VanguardMovementAuthorityDoctrine.ClaimedCohesionStationaryMinHoldSeconds : VanguardMovementAuthorityDoctrine.ClaimedCohesionMovingMinHoldSeconds),
                MaxUntilUtc = maxUntil,
                NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.ClaimedCohesionNoProgressSeconds),
                LastProgressAtUtc = now,
                InitialAnchorDistance = anchorDistance,
                LastAnchorDistance = anchorDistance,
                LastWorldPosition = botPosition,
                LastWorldSampleAtUtc = now,
                LastLivenessObservationAtUtc = now,
                ObservedBlockedSeconds = 0f,
                ObservedNoProgressSeconds = 0f,
                PhysicalBlockedSinceUtc = DateTimeOffset.MinValue,
                PhysicalRestartCount = 0,
                InitialOwnerPosition = snapshot.SquadCohesion.OwnerPosition ?? Vector3.zero,
                LastOwnerSamplePosition = snapshot.SquadCohesion.OwnerPosition ?? Vector3.zero,
                LastOwnerSampleAtUtc = now,
                ObservedOwnerResumeSeconds = 0f,
                InitialOwnerDistance = snapshot.SquadCohesion.OperatorDistanceToOwner,
                LastOwnerDistance = snapshot.SquadCohesion.OperatorDistanceToOwner,
                PathDistanceMeters = pathDistance,
                StationaryHold = claim.StationaryHold,
                NextExternalQuiesceAtUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.OrbitQuiesceRefreshSeconds),
                NextRetargetAllowedAtUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementRetargetCooldownSeconds),
                RetargetCount = 0,
                PhysicalStackSinceUtc = DateTimeOffset.MinValue,
                UsesInteriorPathContract = claim.UsesInteriorPathContract,
                ExecutionPathCeilingMeters = claim.ExecutionPathCeilingMeters,
                ExecutionPathRatioCeiling = claim.ExecutionPathRatioCeiling,
                PlanSummary = claim.Summary
            };

            lock (Sync)
            {
                ActiveByBotProfileId[snapshot.BotProfileId] = lease;
                AtomicHandoffByBotProfileId.Remove(snapshot.BotProfileId);
            }

            VanguardMainIntentScheduler.MarkCloseCohesionStarted(snapshot.BotProfileId, leaseId, now, lease.Summary, windowId);
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_CLAIM_LEASE_STARTED {lease.Summary}; driveReason={Safe(driveReason)}; pace={Safe(paceReason)}; path={Safe(pathSummary)}; sprint={Bool(sprint)}; anchorDistance={anchorDistance:0.0}; ownerDistance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; budget={maxLeaseBudget}; command={Safe(commandResult)}; tag={StatusTag}; Tag={CohesionAnchorsRunStatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
            maxNewStarts--;
        }
    }

    private static void TickActiveLeases(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        CohesionClaimLeaseState[] active;
        lock (Sync)
        {
            active = ActiveByBotProfileId.Values.ToArray();
        }

        if (active.Length == 0)
        {
            return;
        }

        var byProfile = snapshots == null
            ? new Dictionary<string, OperatorDecisionSnapshot>(StringComparer.OrdinalIgnoreCase)
            : snapshots.Where(item => item != null && !string.IsNullOrWhiteSpace(item.BotProfileId))
                .GroupBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var lease in active)
        {
            if (!byProfile.TryGetValue(lease.BotProfileId, out var snapshot))
            {
                FinishLease(lease, now, "Interrupted", "snapshot_missing", failureCooldown: true, snapshotSignature: "missing");
                continue;
            }

            string interrupt = CheckActiveLeaseInterruptGate(snapshot, lease, now);
            if (!string.Equals(interrupt, "none", StringComparison.OrdinalIgnoreCase))
            {
                FinishLease(lease, now, "Interrupted", interrupt, failureCooldown: true, snapshot.DecisionSignature);
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(lease.BotProfileId, out var record) || record.BotOwner == null || record.BotOwner.IsDead)
            {
                FinishLease(lease, now, "Interrupted", "bot_owner_missing_or_dead", failureCooldown: true, snapshot.DecisionSignature);
                continue;
            }

            var mutable = RefreshExternalAuthorityIfNeeded(lease, snapshot, record.BotOwner, now);
            if (mutable.NextExternalQuiesceAtUtc != lease.NextExternalQuiesceAtUtc)
            {
                lock (Sync)
                {
                    ActiveByBotProfileId[lease.BotProfileId] = mutable;
                }
            }

            Vector3 botPosition = ResolveBotPosition(record.BotOwner);
            float anchorDistance = HorizontalDistance(botPosition, mutable.Anchor);
            float ownerDistance = snapshot.SquadCohesion.OperatorDistanceToOwner;

            if (mutable.StationaryHold
                && !IsInteriorClaimLease(mutable)
                && snapshot.SquadCohesion.OwnerPosition.HasValue)
            {
                bool ownerResumeConfirmed = TryConfirmOwnerResume(
                    snapshot.SquadCohesion.OwnerPosition.Value,
                    now,
                    ref mutable,
                    out var ownerResumeReason);
                if (ownerResumeConfirmed)
                {
                    FinishStationaryClaimForOwnerResume(mutable, now, ownerResumeReason, snapshot.DecisionSignature);
                    continue;
                }

                lock (Sync)
                {
                    ActiveByBotProfileId[lease.BotProfileId] = mutable;
                }
            }

            if (IsActiveClaimPathDivergent(snapshot, mutable, botPosition, out var divergeReason))
            {
                FinishLease(mutable, now, "Interrupted", "path_divergent:" + divergeReason, failureCooldown: true, snapshot.DecisionSignature);
                if (TryIssuePathSafeHardReturnFallback(snapshot, record.BotOwner, botPosition, now, "active_claim_path_divergent:" + divergeReason, out var fallbackReason))
                {
                    LogThrottled("DivergentFallback|" + snapshot.BotProfileId + "|" + fallbackReason, now,
                        $"VANGUARD_DIVERGENT_TO_HARD_RETURN operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(mutable.LeaseId)}; lane={Safe(mutable.Lane)}; divergeReason={Safe(divergeReason)}; fallback={Safe(fallbackReason)}; tag={HardReturnAlertStatusTag}; Tag={PathAlertRecoveryStatusTag}");
                }
                continue;
            }

            // Runtime invariant: non-stationary formation claims may retarget the existing movement generation when
            // the owner has clearly outrun the old anchor. Persistent interior holds are excluded.
            bool claimOwnerGrowing = ownerDistance >= mutable.LastOwnerDistance + VanguardMovementAuthorityDoctrine.MovementRetargetOwnerDistanceGrowthMeters;
            bool claimNoProgress = now - mutable.LastProgressAtUtc >= TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementRetargetNoProgressSeconds);
            if (!mutable.StationaryHold
                && now >= mutable.MinUntilUtc
                && now >= mutable.NextRetargetAllowedAtUtc
                && mutable.RetargetCount < VanguardMovementAuthorityDoctrine.MovementRetargetMaxPerLease
                && ownerDistance >= VanguardMovementAuthorityDoctrine.MovementRetargetOwnerPressureMeters
                && (claimOwnerGrowing || claimNoProgress || (anchorDistance <= mutable.AnchorRadiusMeters + 2.0f && ownerDistance > VanguardMovementAuthorityDoctrine.ClaimedCohesionSoftCompleteMeters + 8.0f))
                && TryRetargetActiveClaimFromPendingPlan(snapshot, botPosition, now, ref mutable, out var claimRetargetReason))
            {
                lock (Sync)
                {
                    ActiveByBotProfileId[lease.BotProfileId] = mutable;
                }
                LogThrottled("ClaimRetarget|" + lease.BotProfileId, now,
                    $"VANGUARD_CLAIM_RETARGET {mutable.Summary}; ownerDistance={ownerDistance:0.0}; reason={Safe(claimRetargetReason)}; sameLease=true; sameGeneration=true; bounded=true; Tag={VanguardPrimaryExecutionContract.MovementRetargetStatusTag}; tag={StatusTag}");
                continue;
            }

            TimeSpan physicalSampleAge = now - mutable.LastWorldSampleAtUtc;
            bool movementExpected = anchorDistance > mutable.AnchorRadiusMeters + 0.35f;
            var physical = VanguardMovementProgressEvaluator.EvaluatePhysical(
                mutable.LastWorldPosition,
                botPosition,
                mutable.LastAnchorDistance,
                anchorDistance,
                snapshot.RealSpeed,
                movementExpected,
                physicalSampleAge);
            bool sampleReady = physicalSampleAge >= TimeSpan.FromSeconds(0.45d);
            float observedDeltaSeconds = 0f;
            bool contiguousObservation = false;
            if (sampleReady)
            {
                TimeSpan livenessGap = now - mutable.LastLivenessObservationAtUtc;
                contiguousObservation = mutable.LastLivenessObservationAtUtc != DateTimeOffset.MinValue
                    && livenessGap.TotalSeconds > 0d
                    && livenessGap.TotalSeconds <= VanguardContinuousCohesionLocomotionPolicy.LivenessMaximumContiguousSampleGapSeconds;
                observedDeltaSeconds = contiguousObservation
                    ? (float)Math.Min(livenessGap.TotalSeconds, VanguardContinuousCohesionLocomotionPolicy.LivenessMaximumContiguousSampleGapSeconds)
                    : 0f;
                mutable.LastLivenessObservationAtUtc = now;
                mutable.LastWorldPosition = botPosition;
                mutable.LastWorldSampleAtUtc = now;
                if (!contiguousObservation)
                {
                    mutable.ObservedBlockedSeconds = 0f;
                    mutable.ObservedNoProgressSeconds = 0f;
                }
            }

            bool anchorProgress = anchorDistance < mutable.LastAnchorDistance - VanguardMovementAuthorityDoctrine.ClaimedCohesionProgressGainMeters;
            bool ownerProgress = ownerDistance < mutable.LastOwnerDistance - 0.85f || ownerDistance < mutable.InitialOwnerDistance - 1.35f;
            bool physicalProgress = movementExpected && physical.HasProgress;
            bool stationaryHoldMaintained = mutable.StationaryHold && !movementExpected;
            if (physicalProgress || stationaryHoldMaintained)
            {
                if (anchorProgress || physical.GoalGainMeters > 0f)
                {
                    mutable.LastAnchorDistance = Math.Min(mutable.LastAnchorDistance, anchorDistance);
                }

                if (ownerProgress)
                {
                    mutable.LastOwnerDistance = Math.Min(mutable.LastOwnerDistance, ownerDistance);
                }

                mutable.LastProgressAtUtc = now;
                mutable.NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.ClaimedCohesionNoProgressSeconds);
                mutable.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
                mutable.ObservedBlockedSeconds = 0f;
                mutable.ObservedNoProgressSeconds = 0f;
                mutable.LastWorldPosition = botPosition;
                mutable.LastWorldSampleAtUtc = now;
                lock (Sync)
                {
                    ActiveByBotProfileId[lease.BotProfileId] = mutable;
                }

                string progressKind = stationaryHoldMaintained ? "stationary_hold_maintained" : physical.ProgressKind;
                VanguardMainIntentScheduler.ReportPrimaryProgress(lease.BotProfileId, now, "" + progressKind, mutable.Summary, lease.WindowId);
                LogThrottled("progress|" + lease.BotProfileId, now,
                    $"VANGUARD_CLAIM_LEASE_PROGRESS {mutable.Summary}; ownerDistance={ownerDistance:0.0}; anchorDistance={anchorDistance:0.0}; progress={Safe(progressKind)}; physical={Safe(physical.Summary)}; speed={snapshot.RealSpeed:0.00}; physicalTag={VanguardPrimaryExecutionContract.PhysicalMovementProgressStatusTag}; tag={StatusTag}");
            }
            else if (sampleReady && movementExpected)
            {
                if (contiguousObservation)
                {
                    mutable.ObservedNoProgressSeconds += observedDeltaSeconds;
                    if (physical.LocomotionBlocked)
                    {
                        mutable.ObservedBlockedSeconds += observedDeltaSeconds;
                    }
                    else
                    {
                        mutable.ObservedBlockedSeconds = 0f;
                    }
                }

                if (mutable.ObservedBlockedSeconds >= VanguardContinuousCohesionLocomotionPolicy.ClaimObservedRestartSeconds
                    && mutable.PhysicalRestartCount < 1)
                {
                    if (VanguardReturnMovementCommandStore.TryRestartOwned(mutable.LeaseId, mutable.BotProfileId, now, physical.Summary, out var restartResult))
                    {
                        mutable.PhysicalRestartCount++;
                        mutable.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
                        mutable.ObservedBlockedSeconds = 0f;
                        mutable.ObservedNoProgressSeconds = 0f;
                        mutable.LastWorldPosition = botPosition;
                        mutable.LastWorldSampleAtUtc = now;
                        mutable.LastLivenessObservationAtUtc = now;
                        lock (Sync)
                        {
                            ActiveByBotProfileId[lease.BotProfileId] = mutable;
                        }

                        LogThrottled("ClaimObservedRestart|" + lease.BotProfileId, now,
                            $"VANGUARD_CLAIM_OBSERVED_RESTART {mutable.Summary}; physical={Safe(physical.Summary)}; result={Safe(restartResult)}; observedOnly=true; sameLease=true; tag={VanguardContinuousCohesionLocomotionPolicy.SeamlessAuthorityContinuityStatusTag}");
                        continue;
                    }

                    if (mutable.StationaryHold && !IsInteriorClaimLease(mutable))
                    {
                        ReplanStationaryClaimAfterObservedStall(
                            mutable,
                            now,
                            "claim_observed_restart_rejected:" + restartResult,
                            snapshot.DecisionSignature);
                    }
                    else
                    {
                        FinishLease(mutable, now, "Failed", "physical_restart_rejected:" + restartResult, failureCooldown: true, snapshot.DecisionSignature);
                    }
                    continue;
                }

                bool blockedTerminal = mutable.PhysicalRestartCount >= 1
                    && mutable.ObservedBlockedSeconds >= VanguardContinuousCohesionLocomotionPolicy.ClaimObservedBlockedTerminalSeconds;
                bool noProgressTerminal = mutable.ObservedNoProgressSeconds >= VanguardContinuousCohesionLocomotionPolicy.ClaimObservedNoProgressTerminalSeconds;
                if (blockedTerminal || noProgressTerminal)
                {
                    string terminalReason = blockedTerminal
                        ? "claim_observed_blocked_after_single_restart"
                        : "claim_observed_no_progress";
                    if (mutable.StationaryHold && !IsInteriorClaimLease(mutable))
                    {
                        ReplanStationaryClaimAfterObservedStall(mutable, now, terminalReason + ":" + physical.Summary, snapshot.DecisionSignature);
                    }
                    else
                    {
                        FinishLease(mutable, now, "Timeout", terminalReason + ":" + physical.Summary, failureCooldown: true, snapshot.DecisionSignature);
                    }
                    continue;
                }

                lock (Sync)
                {
                    ActiveByBotProfileId[lease.BotProfileId] = mutable;
                }
            }
            else if (sampleReady)
            {
                mutable.ObservedBlockedSeconds = 0f;
                mutable.ObservedNoProgressSeconds = 0f;
                mutable.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
                lock (Sync)
                {
                    ActiveByBotProfileId[lease.BotProfileId] = mutable;
                }
            }

            if (anchorDistance <= mutable.AnchorRadiusMeters && now >= mutable.MinUntilUtc)
            {
                if (mutable.StationaryHold && IsInteriorClaimLease(mutable)
                    && IsPhysicallyStackedAtInteriorArrival(snapshot, byProfile.Values, botPosition, out var stackedWithProfileId, out var stackReason))
                {
                    bool currentMustYield = ShouldYieldInteriorArrivalStack(snapshot.BotProfileId, stackedWithProfileId, now, out var priorityReason);
                    if (!currentMustYield)
                    {
                        // Two simultaneous guards must not both abandon their sectors. The deterministic
                        // claim priority lets one hold while the other performs the bounded replan.
                        mutable.PhysicalStackSinceUtc = DateTimeOffset.MinValue;
                        lock (Sync)
                        {
                            ActiveByBotProfileId[lease.BotProfileId] = mutable;
                        }
                        LogThrottled("interiorArrivalStackPriorityHold|" + lease.BotProfileId, now,
                            $"VANGUARD_INTERIOR_ARRIVAL_STACK_PRIORITY_HOLD {mutable.Summary}; other={Safe(stackedWithProfileId)}; reason={Safe(stackReason)}; priority={Safe(priorityReason)}; completed=true; doctrine=exactly_one_guard_replans_on_confirmed_collision; Tag={VanguardPrimaryExecutionContract.InteriorCandidateRecoveryStatusTag}; tag={VanguardPrimaryExecutionContract.InteriorVolumeSecurityStatusTag}");
                    }
                    else
                    {
                        if (mutable.PhysicalStackSinceUtc == DateTimeOffset.MinValue)
                        {
                            mutable.PhysicalStackSinceUtc = now;
                            lock (Sync)
                            {
                                ActiveByBotProfileId[lease.BotProfileId] = mutable;
                            }
                            LogThrottled("interiorArrivalStackWatch|" + lease.BotProfileId, now,
                                $"VANGUARD_INTERIOR_ARRIVAL_STACK_WATCH {mutable.Summary}; other={Safe(stackedWithProfileId)}; reason={Safe(stackReason)}; priority={Safe(priorityReason)}; confirmSeconds={VanguardMovementAuthorityDoctrine.InteriorMissionArrivalStackConfirmSeconds:0.00}; completed=false; Tag={VanguardPrimaryExecutionContract.InteriorCandidateRecoveryStatusTag}; tag={VanguardPrimaryExecutionContract.InteriorVolumeSecurityStatusTag}");
                            continue;
                        }

                        if (now - mutable.PhysicalStackSinceUtc >= TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.InteriorMissionArrivalStackConfirmSeconds))
                        {
                            VanguardInteriorSecurityPlanner.InvalidateAssignment(snapshot.BotProfileId, now, "interior_arrival_stack:" + stackReason + ":" + priorityReason);
                            lock (Sync)
                            {
                                ClaimsByBotProfileId.Remove(snapshot.BotProfileId);
                            }
                            FinishLease(mutable, now, "Interrupted", "interior_arrival_stack:" + stackReason + ":" + priorityReason, failureCooldown: false, snapshot.DecisionSignature);
                            continue;
                        }

                        // The overlap is still inside the confirmation window. Do not complete the hold
                        // until it either clears or becomes a confirmed sector collision.
                        continue;
                    }
                }
                else if (mutable.PhysicalStackSinceUtc != DateTimeOffset.MinValue)
                {
                    mutable.PhysicalStackSinceUtc = DateTimeOffset.MinValue;
                    lock (Sync)
                    {
                        ActiveByBotProfileId[lease.BotProfileId] = mutable;
                    }
                }

                FinishLease(mutable, now, "Completed", mutable.StationaryHold ? "stationary_claim_held" : "claim_anchor_reached", failureCooldown: false, snapshot.DecisionSignature);
                continue;
            }

            if (!mutable.StationaryHold
                && ownerDistance <= VanguardMovementAuthorityDoctrine.ClaimedCohesionSoftCompleteMeters
                && anchorDistance <= mutable.AnchorRadiusMeters + 3.5f
                && now >= mutable.MinUntilUtc)
            {
                FinishLease(mutable, now, "Completed", "soft_claim_band_recovered", failureCooldown: false, snapshot.DecisionSignature);
                continue;
            }

            if (now >= mutable.MaxUntilUtc)
            {
                if (mutable.StationaryHold && !IsInteriorClaimLease(mutable))
                {
                    ReplanStationaryClaimAfterObservedStall(
                        mutable,
                        now,
                        "stationary_claim_max_window_expired",
                        snapshot.DecisionSignature);
                }
                else
                {
                    FinishLease(mutable, now, "Timeout", "max_window_expired", failureCooldown: true, snapshot.DecisionSignature);
                }
                continue;
            }

        }
    }

    private static bool TryBuildClaim(
        OperatorDecisionSnapshot snapshot,
        Vector3 owner,
        Vector3 forward,
        string lane,
        string purpose,
        string ownerMode,
        float ownerSpeed,
        DateTimeOffset now,
        out CohesionClaimState claim,
        Vector3? genericFallbackOwner = null,
        string? genericFallbackLane = null,
        string? genericFallbackPurpose = null)
    {
        claim = default;
        if (VanguardInteriorSecurityPlanner.TryGetAssignment(snapshot.BotProfileId, now, out var interiorAssignment))
        {
            int interiorDeferralBefore = VanguardCohesionPlanningBudget.DeferralSerial;
            if (TryBuildInteriorCoverageClaim(snapshot, owner, interiorAssignment, ownerMode, ownerSpeed, now, out claim, out var interiorRejectReason))
            {
                return true;
            }

            if (VanguardCohesionPlanningBudget.DeferralSerial != interiorDeferralBefore)
            {
                // An incomplete budgeted scan is not proof that the interior mission is invalid.
                // Preserve the reservation and resume its stable cursor on the Operator's next turn.
                return false;
            }

            // Regression guard: an interior reservation that cannot produce a navigable, separated
            // claim is not an execution authority. Release only that reservation and immediately
            // continue through the proven Vanguard player-relative formation path. This prevents the
            // The runtime illegal state "interior assignment + no claim + generic formation blocked".
            VanguardInteriorSecurityPlanner.InvalidateAssignment(
                snapshot.BotProfileId,
                now,
                "interior_claim_not_executable:" + interiorRejectReason);

            owner = genericFallbackOwner ?? owner;
            lane = !string.IsNullOrWhiteSpace(genericFallbackLane)
                ? genericFallbackLane
                : ResolveGenericFallbackLane(snapshot, lane);
            purpose = !string.IsNullOrWhiteSpace(genericFallbackPurpose)
                ? genericFallbackPurpose
                : PurposeFor(lane, ownerMode, snapshot);
            LogThrottled(
                "interior_fallback|" + Safe(snapshot.BotProfileId),
                now,
                $"VANGUARD_INTERIOR_GENERIC_FALLBACK owner={Safe(snapshot.OwnerProfileId)}; operatorId={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; volume={Safe(interiorAssignment.VolumeId)}; sector={Safe(interiorAssignment.PortalKey)}; reason={Safe(interiorRejectReason)}; fallbackLane={Safe(lane)}; fallbackPurpose={Safe(purpose)}; genericFormationAllowed=true; finalPhysicalSpacingGuardRetained=true; doctrine=mission_without_executable_claim_cannot_block_cohesion; Tag={VanguardPrimaryExecutionContract.InteriorExecutableMissionStatusTag}; tag={VanguardPrimaryExecutionContract.InteriorVolumeSecurityStatusTag}");
        }

        Vector3 right = new Vector3(forward.z, 0f, -forward.x);
        if (right.sqrMagnitude <= 0.001f)
        {
            right = Vector3.right;
        }
        right.Normalize();

        float distance = DistanceFor(lane, ownerMode, snapshot);
        Vector3 primaryDirection = DirectionFor(lane, forward, right);
        var rawCandidates = new List<Vector3>();
        AddCandidateSweep(rawCandidates, owner, primaryDirection, distance, new[] { 0f, 10f, -10f, 20f, -20f, 32f, -32f });
        AddCandidateSweep(rawCandidates, owner, primaryDirection, Math.Max(6.0f, distance - 3.0f), new[] { 0f, 14f, -14f, 28f, -28f });
        AddCandidateSweep(rawCandidates, owner, primaryDirection, distance + 4.0f, new[] { 0f, 16f, -16f, 30f, -30f });
        AddFullBubbleFallbackCandidates(rawCandidates, owner, forward, right, distance, lane, snapshot);

        ClaimAnchorScore best = ClaimAnchorScore.Invalid("no_candidate");
        Vector3 bot = snapshot.Position;
        string candidatePhase = "generic:" + Safe(lane) + ":" + Safe(ownerMode);
        string candidateGeneration = BuildCandidateGeneration(owner, primaryDirection, lane + "|" + ownerMode + "|" + distance.ToString("0.0", CultureInfo.InvariantCulture));
        int candidateCount = rawCandidates.Count;
        int candidateStart = VanguardCohesionPlanningBudget.GetCandidateStart(snapshot.BotProfileId, candidatePhase, candidateGeneration, candidateCount);
        int evaluatedCandidates = 0;
        for (int offset = 0; offset < candidateCount; offset++)
        {
            if (!VanguardCohesionPlanningBudget.CanStartCandidate(2))
            {
                break;
            }

            int candidateIndex = (candidateStart + offset) % candidateCount;
            Vector3 raw = rawCandidates[candidateIndex];
            evaluatedCandidates++;
            if (!TryScoreClaimAnchor(snapshot, owner, bot, raw, distance, primaryDirection, lane, out var scored))
            {
                if (scored.Score > best.Score)
                {
                    best = scored;
                }
                continue;
            }

            if (!best.Valid || scored.Score > best.Score)
            {
                best = scored;
            }
        }

        if (best.Valid)
        {
            VanguardCohesionPlanningBudget.CompleteCandidateSequence(snapshot.BotProfileId, candidatePhase);
        }
        else
        {
            bool completed = VanguardCohesionPlanningBudget.AdvanceCandidateCursor(snapshot.BotProfileId, candidatePhase, candidateGeneration, candidateCount, candidateStart, evaluatedCandidates);
            if (!completed)
            {
                return false;
            }
            return false;
        }

        bool stationaryHold = string.Equals(ownerMode, "stationary_hold", StringComparison.OrdinalIgnoreCase);
        bool sprintAllowed = snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.ClaimedCohesionSprintDistanceMeters
            || best.BotPathDistance >= VanguardMovementAuthorityDoctrine.ClaimedCohesionAnchorSprintDistanceMeters
            || (ownerSpeed >= VanguardMovementAuthorityDoctrine.ClaimedCohesionOwnerFastSpeed
                && snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.ClaimedCohesionRunDistanceMeters * 1.35f);
        claim = new CohesionClaimState
        {
            ClaimId = "claim_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId),
            OwnerProfileId = string.IsNullOrWhiteSpace(snapshot.OwnerProfileId) ? snapshot.SquadCohesion.OwnerProfileId : snapshot.OwnerProfileId,
            OperatorId = snapshot.OperatorId,
            BotProfileId = snapshot.BotProfileId,
            Lane = lane,
            Purpose = purpose,
            Anchor = best.Anchor,
            AnchorRadiusMeters = stationaryHold
                ? VanguardContinuousCohesionLocomotionPolicy.ObservationDeploymentAnchorRadiusMeters
                : VanguardMovementAuthorityDoctrine.ClaimedCohesionAnchorRadiusMeters,
            AssignedAtUtc = now,
            ValidUntilUtc = now + TimeSpan.FromSeconds(stationaryHold ? VanguardMovementAuthorityDoctrine.ClaimedCohesionStationaryValidSeconds : VanguardMovementAuthorityDoctrine.ClaimedCohesionValidSeconds),
            StationaryHold = stationaryHold,
            SprintAllowed = sprintAllowed,
            PathSummary = best.PathSummary,
            PathDistanceMeters = best.BotPathDistance,
            OwnerDistance = best.OwnerDistance,
            Score = best.Score
        };
        return true;
    }


    private static bool TryBuildInteriorCoverageClaim(
        OperatorDecisionSnapshot snapshot,
        Vector3 owner,
        InteriorSecurityAssignment assignment,
        string ownerMode,
        float ownerSpeed,
        DateTimeOffset now,
        out CohesionClaimState claim,
        out string rejectReason)
    {
        claim = default;
        rejectReason = "interior_not_scored";
        ClaimAnchorScore best = ClaimAnchorScore.Invalid(rejectReason);
        int attempts = 0;
        var rejects = new List<string>();
        var candidates = BuildInteriorExecutionCandidates(assignment).ToList();
        string candidatePhase = "interior:" + Safe(assignment.VolumeId) + ":" + Safe(assignment.PortalKey);
        string candidateGeneration = BuildCandidateGeneration(assignment.Anchor, Flatten(assignment.WatchPoint - assignment.Anchor), assignment.VolumeId + "|" + assignment.PortalKey);
        int candidateCount = candidates.Count;
        int candidateStart = VanguardCohesionPlanningBudget.GetCandidateStart(snapshot.BotProfileId, candidatePhase, candidateGeneration, candidateCount);
        for (int offset = 0; offset < candidateCount; offset++)
        {
            if (!VanguardCohesionPlanningBudget.CanStartCandidate(2))
            {
                break;
            }

            int candidateIndex = (candidateStart + offset) % candidateCount;
            InteriorExecutionCandidate candidate = candidates[candidateIndex];
            attempts++;
            if (!TryScoreInteriorCoverageAnchor(snapshot, owner, snapshot.Position, assignment, candidate.Anchor, candidate.Label, now, out var scored))
            {
                rejects.Add(candidate.Label + ":" + scored.PathSummary);
                continue;
            }

            if (!best.Valid || scored.Score > best.Score)
            {
                best = scored;
            }
        }

        if (best.Valid)
        {
            VanguardCohesionPlanningBudget.CompleteCandidateSequence(snapshot.BotProfileId, candidatePhase);
        }
        else
        {
            bool completed = VanguardCohesionPlanningBudget.AdvanceCandidateCursor(snapshot.BotProfileId, candidatePhase, candidateGeneration, candidateCount, candidateStart, attempts);
            if (!completed)
            {
                rejectReason = "planning_deferred:pathBudget=" + VanguardCohesionPlanningBudget.UsedPathCalculations.ToString(CultureInfo.InvariantCulture)
                    + "/" + VanguardCohesionPlanningBudget.MaxPathCalculationsPerTick.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            rejectReason = rejects.Count == 0
                ? "interior_no_candidate"
                : string.Join(",", rejects.Take(6));
            return false;
        }

        rejectReason = "none";
        claim = new CohesionClaimState
        {
            ClaimId = "claim_security_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId),
            OwnerProfileId = assignment.OwnerProfileId,
            OperatorId = snapshot.OperatorId,
            BotProfileId = snapshot.BotProfileId,
            Lane = assignment.Lane,
            Purpose = assignment.Purpose + ":" + assignment.PortalKey,
            Anchor = best.Anchor,
            AnchorRadiusMeters = 2.75f,
            AssignedAtUtc = now,
            ValidUntilUtc = assignment.ExpiresAtUtc,
            StationaryHold = true,
            SprintAllowed = false,
            PathSummary = best.PathSummary + ";interiorCoverage=true;portal=" + Safe(assignment.PortalKey) + ";watch=" + FormatVector(assignment.WatchPoint) + ";candidateAttempts=" + attempts.ToString(CultureInfo.InvariantCulture),
            PathDistanceMeters = best.BotPathDistance,
            OwnerDistance = best.OwnerDistance,
            Score = best.Score + 180.0f,
            UsesInteriorPathContract = true,
            ExecutionPathCeilingMeters = Math.Min(VanguardMovementAuthorityDoctrine.InteriorMissionMaxBotPathMeters, Math.Max(36.0f, best.BotPathDistance + 8.0f)),
            ExecutionPathRatioCeiling = 4.00f
        };
        return true;
    }

    private static IEnumerable<InteriorExecutionCandidate> BuildInteriorExecutionCandidates(InteriorSecurityAssignment assignment)
    {
        Vector3 watch = Flatten(assignment.WatchPoint - assignment.Anchor);
        if (watch.sqrMagnitude <= 0.01f)
        {
            watch = Vector3.forward;
        }
        watch.Normalize();
        Vector3 tangent = new Vector3(watch.z, 0f, -watch.x);
        tangent.Normalize();

        yield return new InteriorExecutionCandidate("base", assignment.Anchor);
        yield return new InteriorExecutionCandidate("tangent_l1", assignment.Anchor + tangent * 1.75f);
        yield return new InteriorExecutionCandidate("tangent_near", assignment.Anchor - tangent * 1.75f);
        yield return new InteriorExecutionCandidate("tangent_l2", assignment.Anchor + tangent * 3.25f);
        yield return new InteriorExecutionCandidate("tangent_mid", assignment.Anchor - tangent * 3.25f);
        yield return new InteriorExecutionCandidate("tangent_l3", assignment.Anchor + tangent * 5.00f);
        yield return new InteriorExecutionCandidate("tangent_far", assignment.Anchor - tangent * 5.00f);
        yield return new InteriorExecutionCandidate("depth_1", assignment.Anchor - watch * 1.50f);
        yield return new InteriorExecutionCandidate("depth_2", assignment.Anchor - watch * 3.00f);
        yield return new InteriorExecutionCandidate("depth_l", assignment.Anchor - watch * 2.25f + tangent * 2.00f);
        yield return new InteriorExecutionCandidate("depth_r", assignment.Anchor - watch * 2.25f - tangent * 2.00f);
    }

    private static bool TryScoreInteriorCoverageAnchor(
        OperatorDecisionSnapshot snapshot,
        Vector3 owner,
        Vector3 bot,
        InteriorSecurityAssignment assignment,
        Vector3 rawAnchor,
        string candidateLabel,
        DateTimeOffset now,
        out ClaimAnchorScore score)
    {
        score = ClaimAnchorScore.Invalid("interior_not_scored");
        if (!TrySample(rawAnchor, 2.25f, out var sampled))
        {
            score = ClaimAnchorScore.Invalid("interior_reject_navmesh_sample_failed");
            return false;
        }

        float sampleDrift = HorizontalDistance(sampled, rawAnchor);
        if (sampleDrift > 1.75f)
        {
            score = ClaimAnchorScore.Invalid("interior_reject_anchor_drift_" + sampleDrift.ToString("0.00", CultureInfo.InvariantCulture));
            return false;
        }

        float ownerDirect = HorizontalDistance(owner, sampled);
        if (ownerDirect < 1.25f || ownerDirect > VanguardMovementAuthorityDoctrine.InteriorMissionMaxOwnerDirectMeters)
        {
            score = ClaimAnchorScore.Invalid("interior_reject_owner_radius_" + ownerDirect.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (!VanguardInteriorSecurityPlanner.IsCollectiveAssignmentSpacingValid(
                snapshot.OwnerProfileId,
                snapshot.BotProfileId,
                sampled,
                VanguardMovementAuthorityDoctrine.InteriorMissionArrivalSpacingMeters,
                now,
                out var collectiveSpacingReason))
        {
            score = ClaimAnchorScore.Invalid("interior_reject_collective_assignment_stack_" + collectiveSpacingReason);
            return false;
        }

        if (!TryPath(owner, sampled, out var ownerPathDistance, out var ownerCorners, out var ownerPathStatus))
        {
            score = ClaimAnchorScore.Invalid("interior_reject_owner_path_" + ownerPathStatus);
            return false;
        }

        if (!TryPath(bot, sampled, out var botPathDistance, out var botCorners, out var botPathStatus))
        {
            score = ClaimAnchorScore.Invalid("interior_reject_bot_path_" + botPathStatus);
            return false;
        }

        float ownerRatio = ownerDirect <= 0.25f ? 1.0f : ownerPathDistance / ownerDirect;
        float botDirect = HorizontalDistance(bot, sampled);
        float botRatio = botDirect <= 0.25f ? 1.0f : botPathDistance / botDirect;
        float botPathCeiling = Math.Min(
            VanguardMovementAuthorityDoctrine.InteriorMissionMaxBotPathMeters,
            Math.Max(36.0f, ResolveClaimBotPathCeiling(snapshot, indoor: true) + 32.0f));
        if (ownerPathDistance > VanguardMovementAuthorityDoctrine.InteriorMissionMaxOwnerPathMeters
            || ownerRatio > VanguardMovementAuthorityDoctrine.InteriorMissionMaxOwnerPathRatio)
        {
            score = ClaimAnchorScore.Invalid("interior_reject_owner_detour_direct_" + ownerDirect.ToString("0.0", CultureInfo.InvariantCulture)
                + "_path_" + ownerPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + "_ratio_" + ownerRatio.ToString("0.00", CultureInfo.InvariantCulture));
            return false;
        }

        if (botPathDistance > botPathCeiling || (botPathDistance > 34.0f && botRatio > 4.00f))
        {
            score = ClaimAnchorScore.Invalid("interior_reject_bot_detour_direct_" + botDirect.ToString("0.0", CultureInfo.InvariantCulture)
                + "_path_" + botPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + "_ratio_" + botRatio.ToString("0.00", CultureInfo.InvariantCulture)
                + "_ceiling_" + botPathCeiling.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (ownerCorners > 20 || botCorners > 30)
        {
            score = ClaimAnchorScore.Invalid("interior_reject_corners_owner_" + ownerCorners.ToString(CultureInfo.InvariantCulture)
                + "_bot_" + botCorners.ToString(CultureInfo.InvariantCulture));
            return false;
        }

        Vector3 watchDirection = Flatten(assignment.WatchPoint - sampled);
        if (watchDirection.sqrMagnitude <= 0.01f)
        {
            score = ClaimAnchorScore.Invalid("interior_reject_watch_direction_missing");
            return false;
        }

        float value = 210.0f
            + assignment.Score
            - ownerPathDistance * 0.20f
            - botPathDistance * 0.16f
            - sampleDrift * 12.0f
            - ownerCorners * 0.30f
            - botCorners * 0.18f;
        string pathSummary = "candidate=" + Safe(candidateLabel)
            + ";ownerDirect=" + ownerDirect.ToString("0.0", CultureInfo.InvariantCulture)
            + ";ownerPath=" + ownerPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";ownerRatio=" + ownerRatio.ToString("0.00", CultureInfo.InvariantCulture)
            + ";ownerCorners=" + ownerCorners.ToString(CultureInfo.InvariantCulture)
            + ";botDirect=" + botDirect.ToString("0.0", CultureInfo.InvariantCulture)
            + ";botPath=" + botPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";botRatio=" + botRatio.ToString("0.00", CultureInfo.InvariantCulture)
            + ";botCeiling=" + botPathCeiling.ToString("0.0", CultureInfo.InvariantCulture)
            + ";botCorners=" + botCorners.ToString(CultureInfo.InvariantCulture)
            + ";anchorDrift=" + sampleDrift.ToString("0.00", CultureInfo.InvariantCulture)
            + ";portal=" + Safe(assignment.PortalKey)
            + ";lane=" + Safe(assignment.Lane)
            + ";interiorCoverage=true";
        score = new ClaimAnchorScore(true, sampled, ownerDirect, ownerPathDistance, botPathDistance, pathSummary, value);
        return true;
    }

    private static bool TryBuildLanePreservingFallbackClaim(OperatorDecisionSnapshot snapshot, Vector3 owner, Vector3 forward, string lane, string purpose, string ownerMode, float ownerSpeed, DateTimeOffset now, out CohesionClaimState claim)
    {
        claim = default;
        Vector3 right = new Vector3(forward.z, 0f, -forward.x);
        if (right.sqrMagnitude <= 0.001f)
        {
            right = Vector3.right;
        }
        right.Normalize();

        bool indoor = IsIndoor(snapshot);
        float baseDistance = DistanceFor(lane, ownerMode, snapshot);
        Vector3 primaryDirection = DirectionFor(lane, forward, right);
        if (primaryDirection.sqrMagnitude <= 0.001f)
        {
            primaryDirection = forward.sqrMagnitude > 0.001f ? forward.normalized : Vector3.forward;
        }

        var candidates = new List<Vector3>();
        float[] distances = indoor
            ? new[] { Math.Max(5.5f, baseDistance - 2.0f), baseDistance + 1.5f, baseDistance + 4.0f, baseDistance + 7.0f }
            : new[] { Math.Max(7.5f, baseDistance - 3.0f), baseDistance + 2.0f, baseDistance + 6.0f, baseDistance + 10.0f };
        foreach (float distance in distances)
        {
            AddCandidateSweep(candidates, owner, primaryDirection, distance, new[] { 0f, 12f, -12f, 24f, -24f, 38f, -38f });
        }

        ClaimAnchorScore best = ClaimAnchorScore.Invalid("lane_preserving_no_candidate");
        Vector3 bot = snapshot.Position;
        string candidatePhase = "lane_fallback:" + Safe(lane) + ":" + Safe(ownerMode);
        string candidateGeneration = BuildCandidateGeneration(owner, primaryDirection, lane + "|" + ownerMode + "|" + baseDistance.ToString("0.0", CultureInfo.InvariantCulture));
        int candidateCount = candidates.Count;
        int candidateStart = VanguardCohesionPlanningBudget.GetCandidateStart(snapshot.BotProfileId, candidatePhase, candidateGeneration, candidateCount);
        int evaluatedCandidates = 0;
        for (int offset = 0; offset < candidateCount; offset++)
        {
            if (!VanguardCohesionPlanningBudget.CanStartCandidate(2))
            {
                break;
            }

            int candidateIndex = (candidateStart + offset) % candidateCount;
            Vector3 raw = candidates[candidateIndex];
            evaluatedCandidates++;
            if (!TryScoreRallyFallbackAnchor(snapshot, owner, bot, raw, baseDistance, out var scored))
            {
                if (scored.Score > best.Score)
                {
                    best = scored;
                }
                continue;
            }

            Vector3 ownerToAnchor = Flatten(scored.Anchor - owner);
            float directionalDot = 0f;
            if (ownerToAnchor.sqrMagnitude > 0.01f)
            {
                ownerToAnchor.Normalize();
                Vector3 desired = Flatten(primaryDirection);
                if (desired.sqrMagnitude > 0.01f)
                {
                    desired.Normalize();
                    directionalDot = Vector3.Dot(desired, ownerToAnchor);
                }
            }

            var laneScore = new ClaimAnchorScore(
                scored.Valid,
                scored.Anchor,
                scored.OwnerDistance,
                scored.OwnerPathDistance,
                scored.BotPathDistance,
                scored.PathSummary + ";lanePreservingFallback=true;requestedLane=" + Safe(lane) + ";dirDot=" + directionalDot.ToString("0.00", CultureInfo.InvariantCulture),
                scored.Score + directionalDot * 42.0f);
            if (!best.Valid || laneScore.Score > best.Score)
            {
                best = laneScore;
            }
        }

        if (best.Valid)
        {
            VanguardCohesionPlanningBudget.CompleteCandidateSequence(snapshot.BotProfileId, candidatePhase);
        }
        else
        {
            bool completed = VanguardCohesionPlanningBudget.AdvanceCandidateCursor(snapshot.BotProfileId, candidatePhase, candidateGeneration, candidateCount, candidateStart, evaluatedCandidates);
            if (!completed)
            {
                return false;
            }
            return false;
        }

        bool stationaryHold = string.Equals(ownerMode, "stationary_hold", StringComparison.OrdinalIgnoreCase);
        claim = new CohesionClaimState
        {
            ClaimId = "claim_lanefb_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId),
            OwnerProfileId = string.IsNullOrWhiteSpace(snapshot.OwnerProfileId) ? snapshot.SquadCohesion.OwnerProfileId : snapshot.OwnerProfileId,
            OperatorId = snapshot.OperatorId,
            BotProfileId = snapshot.BotProfileId,
            Lane = lane,
            Purpose = "lane_preserving_fallback_after_anchor_failed:" + Safe(purpose),
            Anchor = best.Anchor,
            AnchorRadiusMeters = stationaryHold
                ? VanguardContinuousCohesionLocomotionPolicy.ObservationDeploymentFallbackAnchorRadiusMeters
                : Math.Max(5.75f, VanguardMovementAuthorityDoctrine.ClaimedCohesionAnchorRadiusMeters),
            AssignedAtUtc = now,
            ValidUntilUtc = now + TimeSpan.FromSeconds(Math.Max(18.0f, VanguardMovementAuthorityDoctrine.ClaimedCohesionValidSeconds * 0.70f)),
            StationaryHold = stationaryHold,
            SprintAllowed = snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.ClaimedCohesionSprintDistanceMeters,
            PathSummary = "lanePreservingFallback=true;" + best.PathSummary,
            PathDistanceMeters = best.BotPathDistance,
            OwnerDistance = best.OwnerDistance,
            Score = best.Score
        };
        return true;
    }

    private static bool TryBuildRallyFallbackClaim(OperatorDecisionSnapshot snapshot, Vector3 owner, Vector3 forward, string ownerMode, float ownerSpeed, DateTimeOffset now, out CohesionClaimState claim)
    {
        claim = default;
        Vector3 right = new Vector3(forward.z, 0f, -forward.x);
        if (right.sqrMagnitude <= 0.001f)
        {
            right = Vector3.right;
        }
        right.Normalize();

        bool indoor = IsIndoor(snapshot);
        Vector3[] directions =
        {
            Flatten(forward * 0.45f + right * 0.90f),
            Flatten(forward * 0.45f - right * 0.90f),
            Flatten(-forward * 0.55f + right * 0.75f),
            Flatten(-forward * 0.55f - right * 0.75f),
            Flatten(right),
            Flatten(-right),
            Flatten(forward)
        };
        float[] distances = indoor ? new[] { 7.0f, 9.0f, 11.0f, 13.0f } : new[] { 10.0f, 13.0f, 16.0f, 20.0f };

        var rallyCandidates = new List<RallyExecutionCandidate>();
        foreach (Vector3 rawDirection in directions)
        {
            Vector3 direction = rawDirection.sqrMagnitude <= 0.001f ? right : rawDirection.normalized;
            foreach (float distance in distances)
            {
                rallyCandidates.Add(new RallyExecutionCandidate(owner + direction * distance, distance));
            }
        }

        ClaimAnchorScore best = ClaimAnchorScore.Invalid("rally_no_candidate");
        Vector3 bot = snapshot.Position;
        const string candidatePhase = "rally_fallback";
        string candidateGeneration = BuildCandidateGeneration(owner, forward, ownerMode + "|" + (indoor ? "indoor" : "outdoor"));
        int candidateCount = rallyCandidates.Count;
        int candidateStart = VanguardCohesionPlanningBudget.GetCandidateStart(snapshot.BotProfileId, candidatePhase, candidateGeneration, candidateCount);
        int evaluatedCandidates = 0;
        for (int offset = 0; offset < candidateCount; offset++)
        {
            if (!VanguardCohesionPlanningBudget.CanStartCandidate(2))
            {
                break;
            }

            int candidateIndex = (candidateStart + offset) % candidateCount;
            RallyExecutionCandidate candidate = rallyCandidates[candidateIndex];
            evaluatedCandidates++;
            if (!TryScoreRallyFallbackAnchor(snapshot, owner, bot, candidate.Anchor, candidate.DesiredDistance, out var scored))
            {
                if (scored.Score > best.Score)
                {
                    best = scored;
                }
                continue;
            }

            if (!best.Valid || scored.Score > best.Score)
            {
                best = scored;
            }
        }

        if (best.Valid)
        {
            VanguardCohesionPlanningBudget.CompleteCandidateSequence(snapshot.BotProfileId, candidatePhase);
        }
        else
        {
            bool completed = VanguardCohesionPlanningBudget.AdvanceCandidateCursor(snapshot.BotProfileId, candidatePhase, candidateGeneration, candidateCount, candidateStart, evaluatedCandidates);
            if (!completed)
            {
                return false;
            }
            return false;
        }

        claim = new CohesionClaimState
        {
            ClaimId = "claim_rally_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId),
            OwnerProfileId = string.IsNullOrWhiteSpace(snapshot.OwnerProfileId) ? snapshot.SquadCohesion.OwnerProfileId : snapshot.OwnerProfileId,
            OperatorId = snapshot.OperatorId,
            BotProfileId = snapshot.BotProfileId,
            Lane = "rally_bubble",
            Purpose = "hard_rally_after_anchor_failed",
            Anchor = best.Anchor,
            AnchorRadiusMeters = Math.Max(6.5f, VanguardMovementAuthorityDoctrine.ClaimedCohesionAnchorRadiusMeters + 1.75f),
            AssignedAtUtc = now,
            ValidUntilUtc = now + TimeSpan.FromSeconds(Math.Max(14.0f, VanguardMovementAuthorityDoctrine.ClaimedCohesionValidSeconds * 0.75f)),
            StationaryHold = false,
            SprintAllowed = true,
            PathSummary = "rallyFallback=true;" + best.PathSummary,
            PathDistanceMeters = best.BotPathDistance,
            OwnerDistance = best.OwnerDistance,
            Score = best.Score
        };
        return true;
    }

    private static string BuildCandidateGeneration(Vector3 origin, Vector3 direction, string discriminator)
    {
        Vector3 flatDirection = Flatten(direction);
        if (flatDirection.sqrMagnitude > 0.001f)
        {
            flatDirection.Normalize();
        }
        return Safe(discriminator)
            + "|o=" + Quantize(origin.x, 0.25f) + "," + Quantize(origin.y, 0.5f) + "," + Quantize(origin.z, 0.25f)
            + "|d=" + Quantize(flatDirection.x, 4f) + "," + Quantize(flatDirection.z, 4f);
    }

    private static string Quantize(float value, float multiplier)
    {
        return Math.Round(value * multiplier, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryScoreRallyFallbackAnchor(OperatorDecisionSnapshot snapshot, Vector3 owner, Vector3 bot, Vector3 rawAnchor, float desiredDistance, out ClaimAnchorScore score)
    {
        score = ClaimAnchorScore.Invalid("rally_not_scored");
        bool indoor = IsIndoor(snapshot);
        if (!TrySample(rawAnchor, indoor ? 3.6f : 5.0f, out var sampled))
        {
            score = ClaimAnchorScore.Invalid("rally_reject_navmesh_sample_failed");
            return false;
        }

        float ownerDirect = HorizontalDistance(owner, sampled);
        float minOwnerRadius = indoor ? 4.0f : 6.0f;
        float maxOwnerRadius = indoor ? 22.0f : 28.0f;
        if (ownerDirect < minOwnerRadius || ownerDirect > maxOwnerRadius)
        {
            score = ClaimAnchorScore.Invalid("rally_reject_owner_radius_" + ownerDirect.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (IsAnchorTooCloseToOtherOperator(snapshot, sampled, VanguardMovementAuthorityDoctrine.CohesionMinOperatorSpacingMeters, out var spacingReason))
        {
            score = ClaimAnchorScore.Invalid("rally_reject_operator_stack_" + spacingReason);
            return false;
        }

        if (IsAnchorTooCloseToExistingClaim(snapshot, sampled, VanguardMovementAuthorityDoctrine.CohesionMinOperatorSpacingMeters + 1.0f, out var claimSpacingReason))
        {
            score = ClaimAnchorScore.Invalid("rally_reject_claim_stack_" + claimSpacingReason);
            return false;
        }

        if (!TryPath(owner, sampled, out var ownerPathDistance, out var ownerCorners, out var ownerPathStatus))
        {
            score = ClaimAnchorScore.Invalid("rally_reject_owner_path_" + ownerPathStatus);
            return false;
        }

        if (!TryPath(bot, sampled, out var botPathDistance, out var botCorners, out var botPathStatus))
        {
            score = ClaimAnchorScore.Invalid("rally_reject_bot_path_" + botPathStatus);
            return false;
        }

        float rallyBotCeiling = ResolveRallyBotPathCeiling(snapshot, indoor);
        float rallyBotDirectPre = HorizontalDistance(bot, sampled);
        float rallyBotRatioPre = rallyBotDirectPre <= 0.25f ? 1.0f : botPathDistance / rallyBotDirectPre;
        if (ownerPathDistance > (indoor ? 28f : 42f) || botPathDistance > rallyBotCeiling)
        {
            score = ClaimAnchorScore.Invalid("rally_reject_hard_path_sanity_owner_" + ownerPathDistance.ToString("0.0", CultureInfo.InvariantCulture) + "_bot_" + botPathDistance.ToString("0.0", CultureInfo.InvariantCulture) + "_ceiling_" + rallyBotCeiling.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.TacticalBubbleMeters
            && botPathDistance > snapshot.SquadCohesion.OperatorDistanceToOwner + VanguardMovementAuthorityDoctrine.ClaimPathHardCloseSupportExtraMeters)
        {
            score = ClaimAnchorScore.Invalid("rally_reject_false_support_path_owner_" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", CultureInfo.InvariantCulture) + "_botPath_" + botPathDistance.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.TacticalBubbleMeters
            && rallyBotRatioPre > VanguardMovementAuthorityDoctrine.ClaimPathHardCloseSupportRatio
            && botPathDistance > (indoor ? 28.0f : 38.0f))
        {
            score = ClaimAnchorScore.Invalid("rally_reject_detour_ratio_" + rallyBotRatioPre.ToString("0.00", CultureInfo.InvariantCulture) + "_botPath_" + botPathDistance.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (ownerCorners > (indoor ? 10 : 14) || botCorners > (indoor ? 20 : 28))
        {
            score = ClaimAnchorScore.Invalid("rally_reject_too_many_corners_owner_" + ownerCorners.ToString(CultureInfo.InvariantCulture) + "_bot_" + botCorners.ToString(CultureInfo.InvariantCulture));
            return false;
        }

        float value = 118f;
        value -= Math.Abs(ownerDirect - desiredDistance) * 2.2f;
        value -= ownerPathDistance * 0.22f;
        value -= botPathDistance * 0.10f;
        value -= ownerCorners * 0.35f;
        value -= botCorners * 0.18f;
        float rallyBotDirect = rallyBotDirectPre;
        float rallyBotRatio = rallyBotRatioPre;
        string pathSummary = "ownerDirect=" + ownerDirect.ToString("0.0", CultureInfo.InvariantCulture)
            + ";ownerPath=" + ownerPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";ownerCorners=" + ownerCorners.ToString(CultureInfo.InvariantCulture)
            + ";botPath=" + botPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";botRatio=" + rallyBotRatio.ToString("0.00", CultureInfo.InvariantCulture)
            + ";botCeiling=" + rallyBotCeiling.ToString("0.0", CultureInfo.InvariantCulture)
            + ";botCorners=" + botCorners.ToString(CultureInfo.InvariantCulture)
            + ";desired=" + desiredDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";lane=rally_bubble";
        score = new ClaimAnchorScore(true, sampled, ownerDirect, ownerPathDistance, botPathDistance, pathSummary, value);
        return true;
    }

    private static bool TryScoreClaimAnchor(OperatorDecisionSnapshot snapshot, Vector3 owner, Vector3 bot, Vector3 rawAnchor, float desiredDistance, Vector3 desiredDirection, string lane, out ClaimAnchorScore score)
    {
        score = ClaimAnchorScore.Invalid("not_scored");
        bool indoor = IsIndoor(snapshot);
        if (!TrySample(rawAnchor, indoor ? 3.2f : 4.2f, out var sampled))
        {
            score = ClaimAnchorScore.Invalid("reject_navmesh_sample_failed");
            return false;
        }

        float ownerDirect = HorizontalDistance(owner, sampled);
        float minOwnerRadius = indoor ? 4.5f : 6.0f;
        float maxOwnerRadius = indoor ? 24.0f : 32.0f;
        if (ownerDirect < minOwnerRadius || ownerDirect > maxOwnerRadius)
        {
            score = ClaimAnchorScore.Invalid("reject_owner_radius_" + ownerDirect.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (IsAnchorTooCloseToOtherOperator(snapshot, sampled, VanguardMovementAuthorityDoctrine.CohesionMinOperatorSpacingMeters, out var spacingReason))
        {
            score = ClaimAnchorScore.Invalid("reject_operator_stack_" + spacingReason);
            return false;
        }

        if (IsAnchorTooCloseToExistingClaim(snapshot, sampled, VanguardMovementAuthorityDoctrine.CohesionMinOperatorSpacingMeters + 1.0f, out var claimSpacingReason))
        {
            score = ClaimAnchorScore.Invalid("reject_claim_stack_" + claimSpacingReason);
            return false;
        }

        if (!TryPath(owner, sampled, out var ownerPathDistance, out var ownerCorners, out var ownerPathStatus))
        {
            score = ClaimAnchorScore.Invalid("reject_owner_path_" + ownerPathStatus);
            return false;
        }

        if (!TryPath(bot, sampled, out var botPathDistance, out var botCorners, out var botPathStatus))
        {
            score = ClaimAnchorScore.Invalid("reject_bot_path_" + botPathStatus);
            return false;
        }

        float ownerRatio = ownerDirect <= 0.25f ? 1.0f : ownerPathDistance / ownerDirect;
        float maxOwnerRatio = indoor ? VanguardMovementAuthorityDoctrine.ClaimedCohesionSupportPathRatioIndoor : VanguardMovementAuthorityDoctrine.ClaimedCohesionSupportPathRatioOutdoor;
        float maxOwnerPath = indoor ? VanguardMovementAuthorityDoctrine.ClaimedCohesionSupportPathMaxIndoorMeters : VanguardMovementAuthorityDoctrine.ClaimedCohesionSupportPathMaxOutdoorMeters;
        float botDirect = HorizontalDistance(bot, sampled);
        float botRatio = botDirect <= 0.25f ? 1.0f : botPathDistance / botDirect;
        float maxBotPath = ResolveClaimBotPathCeiling(snapshot, indoor);
        if (ownerRatio > maxOwnerRatio)
        {
            score = ClaimAnchorScore.Invalid("reject_support_detour_ratio_" + ownerRatio.ToString("0.00", CultureInfo.InvariantCulture));
            return false;
        }

        if (ownerDirect <= 16.0f && ownerPathDistance > ownerDirect + (indoor ? 14.0f : 22.0f))
        {
            score = ClaimAnchorScore.Invalid("reject_wall_volume_detour_owner_direct_" + ownerDirect.ToString("0.0", CultureInfo.InvariantCulture) + "_path_" + ownerPathDistance.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (ownerPathDistance > maxOwnerPath || botPathDistance > maxBotPath)
        {
            score = ClaimAnchorScore.Invalid("reject_path_sanity_owner_" + ownerPathDistance.ToString("0.0", CultureInfo.InvariantCulture) + "_bot_" + botPathDistance.ToString("0.0", CultureInfo.InvariantCulture) + "_ceiling_" + maxBotPath.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.TacticalBubbleMeters
            && botPathDistance > snapshot.SquadCohesion.OperatorDistanceToOwner + VanguardMovementAuthorityDoctrine.ClaimPathHardCloseSupportExtraMeters)
        {
            score = ClaimAnchorScore.Invalid("reject_direct_close_path_huge_owner_" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", CultureInfo.InvariantCulture) + "_botPath_" + botPathDistance.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.TacticalBubbleMeters
            && botRatio > VanguardMovementAuthorityDoctrine.ClaimPathHardCloseSupportRatio
            && botPathDistance > 44.0f)
        {
            score = ClaimAnchorScore.Invalid("reject_bot_detour_ratio_" + botRatio.ToString("0.00", CultureInfo.InvariantCulture) + "_botPath_" + botPathDistance.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (ownerCorners > (indoor ? 10 : 14) || botCorners > (indoor ? 24 : 34))
        {
            score = ClaimAnchorScore.Invalid("reject_too_many_corners_owner_" + ownerCorners.ToString(CultureInfo.InvariantCulture) + "_bot_" + botCorners.ToString(CultureInfo.InvariantCulture));
            return false;
        }

        Vector3 ownerToAnchor = Flatten(sampled - owner);
        float directionalDot = 0f;
        if (ownerToAnchor.sqrMagnitude > 0.01f)
        {
            ownerToAnchor.Normalize();
            Vector3 wanted = Flatten(desiredDirection);
            if (wanted.sqrMagnitude > 0.01f)
            {
                wanted.Normalize();
                directionalDot = Vector3.Dot(wanted, ownerToAnchor);
            }
        }

        float value = 145f;
        value += directionalDot * 18.0f;
        value -= Math.Abs(ownerDirect - desiredDistance) * 2.6f;
        value -= ownerPathDistance * 0.34f;
        value -= botPathDistance * (snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.ClaimedCohesionSprintDistanceMeters ? 0.10f : 0.18f);
        value -= Math.Max(0f, ownerRatio - 1f) * 7.0f;
        value -= ownerCorners * 0.42f;
        value -= botCorners * 0.24f;
        if (IsForwardLane(lane) && directionalDot > 0.20f)
        {
            value += 10.0f;
        }
        if (IsRearLane(lane) && directionalDot > 0.20f)
        {
            value += 4.0f;
        }

        if (snapshot.SquadCohesion.RearOverstacked && !IsRearLane(lane))
        {
            value += 14.0f;
        }

        if (snapshot.SquadCohesion.RearOverstacked && IsRearLane(lane))
        {
            value -= 10.0f;
        }

        string pathSummary = "ownerDirect=" + ownerDirect.ToString("0.0", CultureInfo.InvariantCulture)
            + ";ownerPath=" + ownerPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";ownerRatio=" + ownerRatio.ToString("0.00", CultureInfo.InvariantCulture)
            + ";ownerCorners=" + ownerCorners.ToString(CultureInfo.InvariantCulture)
            + ";botPath=" + botPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";botRatio=" + botRatio.ToString("0.00", CultureInfo.InvariantCulture)
            + ";botCeiling=" + maxBotPath.ToString("0.0", CultureInfo.InvariantCulture)
            + ";botCorners=" + botCorners.ToString(CultureInfo.InvariantCulture)
            + ";desired=" + desiredDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";lane=" + Safe(lane)
            + ";dirDot=" + directionalDot.ToString("0.00", CultureInfo.InvariantCulture);
        score = new ClaimAnchorScore(true, sampled, ownerDirect, ownerPathDistance, botPathDistance, pathSummary, value);
        return true;
    }

    private static bool ShouldDriveClaim(OperatorDecisionSnapshot snapshot, CohesionClaimState claim, float anchorDistance, DateTimeOffset now, out string reason)
    {
        if (claim.ValidUntilUtc <= now)
        {
            reason = "claim_expired";
            return false;
        }

        if (claim.StationaryHold
            && snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.ClaimedCohesionMicroHoldOwnerMeters
            && anchorDistance <= VanguardMovementAuthorityDoctrine.ClaimedCohesionMicroHoldAnchorMeters)
        {
            reason = "stationary_sector_hold_near_anchor";
            return false;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.ClaimedCohesionMicroHoldOwnerMeters
            && anchorDistance <= VanguardMovementAuthorityDoctrine.ClaimedCohesionRunAnchorDistanceMeters
            && snapshot.SquadCohesion.UsefulPosition)
        {
            reason = "close_sector_hold_no_micro_run";
            return false;
        }

        if (claim.StationaryHold
            && snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.ClaimedCohesionMicroHoldOwnerMeters + 5.0f
            && anchorDistance <= VanguardMovementAuthorityDoctrine.ClaimedCohesionMicroHoldAnchorMeters + 5.0f
            && !VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(snapshot))
        {
            reason = "stationary_hysteresis_hold_no_bounce";
            return false;
        }

        if (VanguardOrchestratorAuthorityPolicy.ShouldHoldStableCohesion(snapshot, out var stableHoldReason)
            && !IsLeadLane(claim.Lane)
            && claim.Purpose.IndexOf("cover_verified_navmesh_access", StringComparison.OrdinalIgnoreCase) < 0
            && anchorDistance <= VanguardMovementAuthorityDoctrine.ClaimedCohesionRunAnchorDistanceMeters + 4.0f)
        {
            reason = "stable_cohesion_hold:" + stableHoldReason;
            return false;
        }

        if (anchorDistance >= VanguardMovementAuthorityDoctrine.ClaimedCohesionRunAnchorDistanceMeters)
        {
            reason = "anchor_run_pressure:" + anchorDistance.ToString("0.0", CultureInfo.InvariantCulture);
            return true;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.ClaimedCohesionStartMeters)
        {
            reason = "owner_distance_pressure:" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", CultureInfo.InvariantCulture);
            return true;
        }

        if ((snapshot.Movement.HasPath == true || snapshot.Orbit.Active) && snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.ClaimedCohesionExternalResidueStartMeters)
        {
            reason = "external_residue_pressure:path=" + Bool(snapshot.Movement.HasPath == true) + ":orbit=" + Bool(snapshot.Orbit.Active);
            return true;
        }

        if (!snapshot.SquadCohesion.UsefulPosition && anchorDistance >= VanguardMovementAuthorityDoctrine.ClaimedCohesionUsefulCorrectionStartMeters)
        {
            reason = "useful_position_correction";
            return true;
        }

        reason = "already_on_claim";
        return false;
    }

    private static CohesionClaimLeaseState RefreshExternalAuthorityIfNeeded(CohesionClaimLeaseState lease, OperatorDecisionSnapshot snapshot, BotOwner botOwner, DateTimeOffset now)
    {
        if (now < lease.NextExternalQuiesceAtUtc)
        {
            return lease;
        }

        var mutable = lease;
        mutable.NextExternalQuiesceAtUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.OrbitQuiesceRefreshSeconds);
        if (!NeedsExternalPreempt(snapshot) || !VanguardMovementAuthorityDoctrine.ShouldQuiesceOrbitForSquadTravel(snapshot, out var quiesceReason))
        {
            return mutable;
        }

        var result = VanguardExternalAuthorityAdapter.RequestOrbitAuthorityQuiesce(
            botOwner,
            snapshot,
            "active_claim_refresh:" + lease.Lane + ":" + quiesceReason,
            TimeSpan.FromSeconds(Math.Max(2.0f, VanguardMovementAuthorityDoctrine.OrbitQuiesceRefreshSeconds + 1.50f)),
            now);
        LogThrottled("orbitRefresh|" + lease.BotProfileId + "|" + result.Outcome, now,
            $"VANGUARD_CLAIM_AUTHORITY_REFRESH {lease.Summary}; outcome={result.Outcome}; canDriveMovement={Bool(result.CanDriveMovement)}; reason={Safe(quiesceReason)}; tag={StatusTag}; orbitTag={VanguardMovementAuthorityDoctrine.OrbitAuthorityQuiesceStatusTag}");
        return mutable;
    }

    private static bool CanSoftDriveAfterNonCriticalPreempt(OperatorDecisionSnapshot snapshot, VanguardExternalPreemptResult preempt, CohesionClaimState claim, out string reason)
    {
        reason = "none";
        if (preempt.IsCombatDefer || VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot) || VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(snapshot))
        {
            reason = "combat_or_direct_threat";
            return false;
        }

        if (snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true || snapshot.Looting.HasActiveLootable == true || preempt.After.LootingBotsActive || preempt.After.LootingBotsTaskRunning || preempt.After.LootingBotsHasActiveLootable)
        {
            reason = "loot_activity_not_soft_drivable";
            return false;
        }

        bool supportedResidue = preempt.Outcome == VanguardExternalPreemptOutcome.Pending
            || preempt.Outcome == VanguardExternalPreemptOutcome.FailedOrbitStillActive
            || preempt.Outcome == VanguardExternalPreemptOutcome.FailedPathStillActive
            || preempt.Outcome == VanguardExternalPreemptOutcome.FailedMoverBusy;
        if (!supportedResidue)
        {
            reason = "outcome_not_soft_drivable:" + preempt.Outcome;
            return false;
        }

        if (preempt.After.MoverMoving && preempt.After.RealSpeed > 1.15f && snapshot.SquadCohesion.OperatorDistanceToOwner < VanguardMovementAuthorityDoctrine.ClaimedCohesionStartMeters)
        {
            reason = "external_mover_still_productive";
            return false;
        }

        reason = claim.StationaryHold ? "stationary_claim_overrides_noncritical_residue" : "claim_overrides_noncritical_residue";
        return true;
    }


    private static bool ResolveSprintForClaim(OperatorDecisionSnapshot snapshot, CohesionClaimState claim, float anchorDistance, float pathDistance, out string reason)
    {
        float ownerDistance = snapshot.SquadCohesion.OperatorDistanceToOwner;
        if (!claim.SprintAllowed)
        {
            reason = "run:sprint_not_allowed";
            return false;
        }

        if (ownerDistance >= VanguardMovementAuthorityDoctrine.HardCorrectionMeters || ownerDistance >= VanguardMovementAuthorityDoctrine.ClaimedCohesionRallyFallbackDistanceMeters + 20.0f)
        {
            reason = "sprint:hard_catchup_owner_distance=" + ownerDistance.ToString("0.0", CultureInfo.InvariantCulture);
            return true;
        }

        if (ownerDistance >= VanguardMovementAuthorityDoctrine.ClaimedCohesionSprintDistanceMeters)
        {
            reason = "sprint:owner_distance=" + ownerDistance.ToString("0.0", CultureInfo.InvariantCulture);
            return true;
        }

        if (anchorDistance >= VanguardMovementAuthorityDoctrine.ClaimedCohesionAnchorSprintDistanceMeters)
        {
            reason = "sprint:anchor_distance=" + anchorDistance.ToString("0.0", CultureInfo.InvariantCulture);
            return true;
        }

        if (pathDistance >= VanguardMovementAuthorityDoctrine.ClaimedCohesionAnchorSprintDistanceMeters + 14.0f)
        {
            reason = "sprint:path_distance=" + pathDistance.ToString("0.0", CultureInfo.InvariantCulture);
            return true;
        }

        reason = ownerDistance >= VanguardMovementAuthorityDoctrine.ClaimedCohesionRunDistanceMeters || anchorDistance >= VanguardMovementAuthorityDoctrine.ClaimedCohesionRunAnchorDistanceMeters
            ? "run:controlled_reposition"
            : "hold:near_anchor";
        return false;
    }

    private static bool TryRetargetActiveClaimFromPendingPlan(
        OperatorDecisionSnapshot snapshot,
        Vector3 botPosition,
        DateTimeOffset now,
        ref CohesionClaimLeaseState lease,
        out string reason)
    {
        reason = "none";
        CohesionClaimState pending;
        lock (Sync)
        {
            if (!PendingPlanByBotProfileId.TryGetValue(lease.BotProfileId, out pending))
            {
                reason = "pending_plan_missing";
                return false;
            }
        }

        if (pending.StationaryHold)
        {
            reason = "pending_plan_stationary_not_retargetable";
            return false;
        }

        if (pending.ValidUntilUtc <= now)
        {
            lock (Sync)
            {
                PendingPlanByBotProfileId.Remove(lease.BotProfileId);
            }
            reason = "pending_plan_expired";
            return false;
        }

        float anchorDelta = HorizontalDistance(lease.Anchor, pending.Anchor);
        if (anchorDelta < VanguardMovementAuthorityDoctrine.MovementRetargetAnchorDeltaMeters)
        {
            reason = "pending_anchor_delta_small:" + anchorDelta.ToString("0.0", CultureInfo.InvariantCulture);
            return false;
        }

        if (!TryPath(botPosition, pending.Anchor, out var pathDistance, out var corners, out var pathStatus))
        {
            reason = "pending_path_invalid:" + pathStatus;
            return false;
        }

        float pathCeiling = Math.Max(36.0f, ResolveClaimBotPathCeiling(snapshot, IsIndoor(snapshot)) + 24.0f);
        if (pathDistance > pathCeiling || corners > 28)
        {
            reason = "pending_path_exceeds_bound:path=" + pathDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + ";ceiling=" + pathCeiling.ToString("0.0", CultureInfo.InvariantCulture)
                + ";corners=" + corners.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        bool sprint = ResolveSprintForClaim(snapshot, pending, HorizontalDistance(botPosition, pending.Anchor), pathDistance, out var paceReason);
        string validatedPath = pending.PathSummary + ";retargetPath=" + Safe(pathStatus) + ";retargetCorners=" + corners.ToString(CultureInfo.InvariantCulture) + ";pace=" + Safe(paceReason);
        var retargetResult = VanguardReturnMovementCommandStore.TryRetargetActive(
            lease.LeaseId,
            lease.BotProfileId,
            pending.Anchor,
            pending.AnchorRadiusMeters,
            sprint,
            now,
            lease.MaxUntilUtc,
            validatedPath,
            pathDistance,
            "pending_plan_owner_outpaced_anchor");
        string commandResult = retargetResult.ToString();
        if (!retargetResult.Applied)
        {
            reason = retargetResult.Outcome == VanguardMovementRetargetOutcome.ExtendedOnlyNotMaterial
                ? "command_retarget_not_material:" + commandResult
                : "command_retarget_rejected:" + commandResult;
            return false;
        }

        lock (Sync)
        {
            PendingPlanByBotProfileId.Remove(lease.BotProfileId);
            ClaimsByBotProfileId[lease.BotProfileId] = pending;
        }

        lease.ClaimId = pending.ClaimId;
        lease.Lane = pending.Lane;
        lease.Purpose = pending.Purpose;
        lease.Anchor = pending.Anchor;
        lease.AnchorRadiusMeters = pending.AnchorRadiusMeters;
        lease.InitialAnchorDistance = HorizontalDistance(botPosition, pending.Anchor);
        lease.LastAnchorDistance = lease.InitialAnchorDistance;
        lease.LastWorldPosition = botPosition;
        lease.LastWorldSampleAtUtc = now;
        lease.PhysicalBlockedSinceUtc = DateTimeOffset.MinValue;
        lease.InitialOwnerDistance = snapshot.SquadCohesion.OperatorDistanceToOwner;
        lease.LastOwnerDistance = lease.InitialOwnerDistance;
        lease.PathDistanceMeters = pathDistance;
        lease.UsesInteriorPathContract = pending.UsesInteriorPathContract;
        lease.ExecutionPathCeilingMeters = pending.ExecutionPathCeilingMeters;
        lease.ExecutionPathRatioCeiling = pending.ExecutionPathRatioCeiling;
        lease.PlanSummary = pending.Summary + ";retarget=" + Safe(commandResult);
        lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.ClaimedCohesionNoProgressSeconds);
        lease.NextRetargetAllowedAtUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementRetargetCooldownSeconds);
        lease.RetargetCount++;
        reason = "retargeted:" + commandResult;
        return true;
    }

    private static bool IsInteriorClaimLease(CohesionClaimLeaseState lease)
    {
        return !string.IsNullOrWhiteSpace(lease.ClaimId)
            && lease.ClaimId.StartsWith("claim_security_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPhysicallyStackedAtInteriorArrival(
        OperatorDecisionSnapshot snapshot,
        IEnumerable<OperatorDecisionSnapshot> allSnapshots,
        Vector3 botPosition,
        out string otherProfileId,
        out string reason)
    {
        otherProfileId = "none";
        reason = "none";
        foreach (var other in allSnapshots)
        {
            if (other == null || !other.Alive || string.Equals(other.BotProfileId, snapshot.BotProfileId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            float distance = HorizontalDistance(botPosition, other.Position);
            if (distance < VanguardMovementAuthorityDoctrine.InteriorMissionArrivalSpacingMeters
                && other.RealSpeed <= 0.90f)
            {
                otherProfileId = other.BotProfileId;
                reason = "other=" + Safe(other.BotProfileId)
                    + ";distance=" + distance.ToString("0.00", CultureInfo.InvariantCulture)
                    + ";otherSpeed=" + other.RealSpeed.ToString("0.00", CultureInfo.InvariantCulture);
                return true;
            }
        }

        return false;
    }

    private static bool ShouldYieldInteriorArrivalStack(string currentBotProfileId, string otherBotProfileId, DateTimeOffset now, out string reason)
    {
        bool otherOwnsInteriorClaim;
        lock (Sync)
        {
            otherOwnsInteriorClaim = ClaimsByBotProfileId.TryGetValue(otherBotProfileId, out var otherClaim)
                && !string.IsNullOrWhiteSpace(otherClaim.ClaimId)
                && otherClaim.ClaimId.StartsWith("claim_security_", StringComparison.OrdinalIgnoreCase)
                && otherClaim.ValidUntilUtc > now;
        }

        if (!otherOwnsInteriorClaim)
        {
            reason = "other_has_no_interior_claim_current_replans";
            return true;
        }

        int order = string.Compare(currentBotProfileId, otherBotProfileId, StringComparison.OrdinalIgnoreCase);
        bool currentYields = order > 0;
        reason = currentYields
            ? "deterministic_higher_profile_replans"
            : "deterministic_lower_profile_holds";
        return currentYields;
    }

    private static void TickPersistentPhysicalDestack(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        if (snapshots == null || snapshots.Count < 2)
        {
            return;
        }

        var live = snapshots.Where(item => item != null && item.Alive && !string.IsNullOrWhiteSpace(item.OwnerProfileId))
            .OrderBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var observedPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < live.Length; i++)
        {
            for (int j = i + 1; j < live.Length; j++)
            {
                var first = live[i];
                var second = live[j];
                if (!string.Equals(first.OwnerProfileId, second.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string pairKey = string.Compare(first.BotProfileId, second.BotProfileId, StringComparison.OrdinalIgnoreCase) <= 0
                    ? first.BotProfileId + "|" + second.BotProfileId
                    : second.BotProfileId + "|" + first.BotProfileId;
                observedPairs.Add(pairKey);
                float distance = HorizontalDistance(first.Position, second.Position);
                bool protectedActivity = VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(first)
                    || VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(second)
                    || VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(first)
                    || VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(second)
                    || VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(first)
                    || VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(second);
                bool firstHasMovementPlan;
                bool secondHasMovementPlan;
                lock (Sync)
                {
                    firstHasMovementPlan = ClaimsByBotProfileId.ContainsKey(first.BotProfileId) || ActiveByBotProfileId.ContainsKey(first.BotProfileId);
                    secondHasMovementPlan = ClaimsByBotProfileId.ContainsKey(second.BotProfileId) || ActiveByBotProfileId.ContainsKey(second.BotProfileId);
                }
                bool stableOverlap = !protectedActivity
                    && firstHasMovementPlan
                    && secondHasMovementPlan
                    && distance < 3.0f
                    && first.RealSpeed <= 0.90f
                    && second.RealSpeed <= 0.90f;
                if (!stableOverlap)
                {
                    lock (Sync)
                    {
                        PhysicalStackSinceByPair.Remove(pairKey);
                    }
                    continue;
                }

                DateTimeOffset since;
                lock (Sync)
                {
                    if (PhysicalStackCooldownByPair.TryGetValue(pairKey, out var cooldown) && cooldown > now)
                    {
                        continue;
                    }
                    if (!PhysicalStackSinceByPair.TryGetValue(pairKey, out since))
                    {
                        PhysicalStackSinceByPair[pairKey] = now;
                        continue;
                    }
                }

                if (now - since < TimeSpan.FromSeconds(2.0d))
                {
                    continue;
                }

                var loser = string.Compare(first.BotProfileId, second.BotProfileId, StringComparison.OrdinalIgnoreCase) > 0 ? first : second;
                CohesionClaimLeaseState activeLease = default;
                CohesionClaimState removedClaim = default;
                bool hadActive;
                bool hadClaim;
                lock (Sync)
                {
                    hadActive = ActiveByBotProfileId.TryGetValue(loser.BotProfileId, out activeLease);
                    hadClaim = ClaimsByBotProfileId.TryGetValue(loser.BotProfileId, out removedClaim);
                    ClaimsByBotProfileId.Remove(loser.BotProfileId);
                    PendingPlanByBotProfileId.Remove(loser.BotProfileId);
                    CooldownByBotProfileId.Remove(loser.BotProfileId);
                    PhysicalStackSinceByPair.Remove(pairKey);
                    PhysicalStackCooldownByPair[pairKey] = now + TimeSpan.FromSeconds(8.0d);
                }

                if (hadActive)
                {
                    FinishLease(activeLease, now, "Interrupted", "persistent_physical_stack:" + pairKey, failureCooldown: false, loser.DecisionSignature);
                    lock (Sync)
                    {
                        CooldownByBotProfileId.Remove(loser.BotProfileId);
                    }
                }
                bool interiorClaim = IsInteriorClaimLease(activeLease)
                    || (hadClaim && !string.IsNullOrWhiteSpace(removedClaim.ClaimId) && removedClaim.ClaimId.StartsWith("claim_security_", StringComparison.OrdinalIgnoreCase));
                if (interiorClaim)
                {
                    VanguardInteriorSecurityPlanner.InvalidateAssignment(loser.BotProfileId, now, "persistent_physical_stack:" + pairKey);
                }

                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"VANGUARD_PERSISTENT_PHYSICAL_DESTACK owner={Safe(loser.OwnerProfileId)}; yieldingOperator={Safe(loser.OperatorId)}; yieldingBot={Safe(loser.BotProfileId)}; otherPair={Safe(pairKey)}; distance={distance:0.00}; overlapSeconds={(now - since).TotalSeconds:0.00}; activeLease={Bool(hadActive)}; deterministic=true; combatProtected=false; medicalProtected=false; action=invalidate_one_claim_and_replan; tag=VANGUARD_PHYSICAL_DESTACK_STATUS; Tag={StatusTag}");
            }
        }

        lock (Sync)
        {
            foreach (string stale in PhysicalStackSinceByPair.Keys.Where(key => !observedPairs.Contains(key)).ToArray())
            {
                PhysicalStackSinceByPair.Remove(stale);
            }
            foreach (string expired in PhysicalStackCooldownByPair.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
            {
                PhysicalStackCooldownByPair.Remove(expired);
            }
        }
    }

    private static bool TryConfirmOwnerResume(
        Vector3 ownerPosition,
        DateTimeOffset now,
        ref CohesionClaimLeaseState lease,
        out string reason)
    {
        reason = "owner_resume_not_confirmed";
        if (lease.LastOwnerSampleAtUtc == DateTimeOffset.MinValue)
        {
            lease.InitialOwnerPosition = ownerPosition;
            lease.LastOwnerSamplePosition = ownerPosition;
            lease.LastOwnerSampleAtUtc = now;
            lease.ObservedOwnerResumeSeconds = 0f;
            return false;
        }

        TimeSpan sampleAge = now - lease.LastOwnerSampleAtUtc;
        float sampleSeconds = (float)Math.Max(0d, sampleAge.TotalSeconds);
        float sampleDistance = HorizontalDistance(lease.LastOwnerSamplePosition, ownerPosition);
        float totalDistance = HorizontalDistance(lease.InitialOwnerPosition, ownerPosition);
        float sampleSpeed = sampleSeconds <= 0.05f ? 0f : sampleDistance / sampleSeconds;
        bool contiguous = sampleSeconds > 0f
            && sampleSeconds <= VanguardContinuousCohesionLocomotionPolicy.LivenessMaximumContiguousSampleGapSeconds;
        bool moving = sampleDistance >= VanguardContinuousCohesionLocomotionPolicy.StationaryClaimOwnerResumeSampleMeters
            || totalDistance >= VanguardContinuousCohesionLocomotionPolicy.StationaryClaimOwnerResumeSampleMeters + 0.20f
            || (sampleSpeed >= VanguardContinuousCohesionLocomotionPolicy.StationaryClaimOwnerResumeSpeedMetersPerSecond
                && sampleDistance >= 0.35f);

        lease.LastOwnerSamplePosition = ownerPosition;
        lease.LastOwnerSampleAtUtc = now;
        if (!contiguous)
        {
            lease.InitialOwnerPosition = ownerPosition;
            lease.ObservedOwnerResumeSeconds = 0f;
            reason = "owner_resume_observation_gap";
            return false;
        }

        if (!moving)
        {
            lease.ObservedOwnerResumeSeconds = 0f;
            reason = "owner_still_stationary";
            return false;
        }

        lease.ObservedOwnerResumeSeconds += Math.Min(
            sampleSeconds,
            VanguardContinuousCohesionLocomotionPolicy.LivenessMaximumContiguousSampleGapSeconds);
        reason = "owner_resume:sampleDistance=" + sampleDistance.ToString("0.00", CultureInfo.InvariantCulture)
            + ":totalDistance=" + totalDistance.ToString("0.00", CultureInfo.InvariantCulture)
            + ":speed=" + sampleSpeed.ToString("0.00", CultureInfo.InvariantCulture)
            + ":observed=" + lease.ObservedOwnerResumeSeconds.ToString("0.00", CultureInfo.InvariantCulture);
        return lease.ObservedOwnerResumeSeconds
            >= VanguardContinuousCohesionLocomotionPolicy.StationaryClaimOwnerResumeConfirmSeconds;
    }

    private static void FinishStationaryClaimForOwnerResume(
        CohesionClaimLeaseState lease,
        DateTimeOffset now,
        string reason,
        string snapshotSignature)
    {
        lock (Sync)
        {
            ActiveByBotProfileId.Remove(lease.BotProfileId);
            ClaimsByBotProfileId.Remove(lease.BotProfileId);
            PendingPlanByBotProfileId.Remove(lease.BotProfileId);
            AtomicHandoffByBotProfileId.Remove(lease.BotProfileId);
            CooldownByBotProfileId.Remove(lease.BotProfileId);
        }

        string clearResult = VanguardReturnMovementCommandStore.ClearOwned(
            lease.BotProfileId,
            lease.LeaseId,
            lease.StartedAtUtc,
            "owner_resume_preempts_stationary_claim:" + reason);
        VanguardMainIntentScheduler.FinishPrimaryWindow(
            lease.BotProfileId,
            now,
            "Interrupted",
            "owner_resume_travel_priority:" + reason,
            lease.Summary,
            lease.WindowId);
        VanguardClientDiagnosticsLog.Info(VanguardContinuousCohesionLocomotionPolicy.SeamlessAuthorityContinuityStatusTag,
            $"VANGUARD_STATIONARY_CLAIM_YIELDED_ON_OWNER_RESUME {lease.Summary}; reason={Safe(reason)}; clear={Safe(clearResult)}; snapshot={Safe(snapshotSignature)}; pendingPromotion=false; cooldownWritten=false; postReturnHold=false; doctrine=owner_motion_immediately_returns_authority_to_travel; tag={VanguardContinuousCohesionLocomotionPolicy.SeamlessAuthorityContinuityStatusTag}");
    }

    private static void ReplanStationaryClaimAfterObservedStall(
        CohesionClaimLeaseState lease,
        DateTimeOffset now,
        string reason,
        string snapshotSignature)
    {
        lock (Sync)
        {
            ActiveByBotProfileId.Remove(lease.BotProfileId);
            ClaimsByBotProfileId.Remove(lease.BotProfileId);
            PendingPlanByBotProfileId.Remove(lease.BotProfileId);
            AtomicHandoffByBotProfileId.Remove(lease.BotProfileId);
            CooldownByBotProfileId[lease.BotProfileId] = now + TimeSpan.FromSeconds(
                VanguardContinuousCohesionLocomotionPolicy.ClaimReplanCooldownSeconds);
        }

        string clearResult = VanguardReturnMovementCommandStore.ClearOwned(
            lease.BotProfileId,
            lease.LeaseId,
            lease.StartedAtUtc,
            "stationary_claim_observed_stall_replan:" + reason);
        VanguardMainIntentScheduler.FinishPrimaryWindow(
            lease.BotProfileId,
            now,
            "Interrupted",
            "stationary_claim_replan_after_observed_stall:" + reason,
            lease.Summary,
            lease.WindowId);
        VanguardClientDiagnosticsLog.Info(VanguardContinuousCohesionLocomotionPolicy.SeamlessAuthorityContinuityStatusTag,
            $"VANGUARD_STATIONARY_CLAIM_REPLAN {lease.Summary}; reason={Safe(reason)}; clear={Safe(clearResult)}; snapshot={Safe(snapshotSignature)}; cooldown={VanguardContinuousCohesionLocomotionPolicy.ClaimReplanCooldownSeconds:0.00}; pendingPromotion=false; hardReturnFallback=false; doctrine=one_claim_failure_replans_locally_without_poisoning_travel; tag={VanguardContinuousCohesionLocomotionPolicy.SeamlessAuthorityContinuityStatusTag}");
    }

    private static void FinishLease(CohesionClaimLeaseState lease, DateTimeOffset now, string outcome, string reason, bool failureCooldown, string snapshotSignature)
    {
        lock (Sync)
        {
            ActiveByBotProfileId.Remove(lease.BotProfileId);
        }

        VanguardReturnMovementCommandStore.ClearOwned(lease.BotProfileId, lease.LeaseId, lease.StartedAtUtc, "claim_finished:" + reason);
        PromotePendingPlanAfterLease(lease, now, outcome, reason);
        float cooldownSeconds = failureCooldown
            ? VanguardMovementAuthorityDoctrine.ClaimedCohesionFailureCooldownSeconds
            : lease.StationaryHold ? VanguardMovementAuthorityDoctrine.ClaimedCohesionStationarySuccessCooldownSeconds : VanguardMovementAuthorityDoctrine.ClaimedCohesionSuccessCooldownSeconds;
        SetCooldown(lease.BotProfileId, now, cooldownSeconds);
        if (!failureCooldown && lease.LastOwnerDistance > VanguardMovementAuthorityDoctrine.CloseCohesionStartMinMeters)
        {
            VanguardSquadTravelCohesionAuthority.RecordTravelAuthorityHold(lease.BotProfileId, lease.OperatorId, lease.LastOwnerDistance, now, "claim_completed_keep_quiesce:" + reason);
        }

        VanguardMainIntentScheduler.FinishPrimaryWindow(lease.BotProfileId, now, outcome, reason, lease.Summary, lease.WindowId);
        string log = string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase)
            ? "VANGUARD_CLAIM_LEASE_COMPLETED"
            : string.Equals(outcome, "Timeout", StringComparison.OrdinalIgnoreCase)
                ? "VANGUARD_CLAIM_LEASE_TIMEOUT"
                : "VANGUARD_CLAIM_LEASE_ABORTED";
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"{log} {lease.Summary}; outcome={Safe(outcome)}; reason={Safe(reason)}; snapshot={Safe(snapshotSignature)}; cooldown={cooldownSeconds:0.0}; tag={StatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
    }


    private static bool ShouldPreserveActiveReturnCommand(OperatorDecisionSnapshot snapshot, bool ownerMoved, bool ownerRotated, string ownerMode, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            reason = "snapshot_missing";
            return false;
        }

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot) || VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(snapshot))
        {
            reason = "combat_can_interrupt_return_command";
            return false;
        }

        if (!VanguardReturnMovementCommandStore.TryGetActive(snapshot.BotProfileId, now, out var command))
        {
            reason = "no_active_return_command";
            return false;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.ClaimedCohesionMicroHoldOwnerMeters
            && snapshot.SquadCohesion.UsefulPosition
            && string.Equals(ownerMode, "stationary_hold", StringComparison.OrdinalIgnoreCase))
        {
            reason = "stationary_useful_position_allows_command_to_expire";
            return false;
        }

        if (VanguardPrimaryExecutionContract.ShouldKeepMovementContractUntilTerminal(snapshot, command.RequestKind, out var contractReason))
        {
            reason = contractReason;
            return true;
        }

        bool catchup = snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.MovementPlanCatchupOwnerDistanceMeters;
        bool recentPhysicalProgress = false;
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var activeLease))
            {
                recentPhysicalProgress = now - activeLease.LastProgressAtUtc <= TimeSpan.FromSeconds(1.25d);
            }
        }
        if (catchup && recentPhysicalProgress)
        {
            reason = (ownerMoved || ownerRotated) ? "active_return_command_preserved_by_physical_progress" : "active_return_command_physically_productive";
            return true;
        }

        reason = "active_return_command_without_recent_physical_progress";
        return false;
    }

    private static bool ShouldProtectActiveMovementPlan(OperatorDecisionSnapshot snapshot, CohesionClaimLeaseState activeLease, bool ownerMoved, bool ownerRotated, bool indoor, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_not_alive";
            return false;
        }

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot) || VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(snapshot))
        {
            reason = "true_combat_can_interrupt_plan";
            return false;
        }

        float planElapsed = (float)Math.Max(0.0d, (now - activeLease.StartedAtUtc).TotalSeconds);
        if (ownerMoved
            && !activeLease.StationaryHold
            && planElapsed >= 2.50f
            && activeLease.LastAnchorDistance >= 9.0f
            && snapshot.SquadCohesion.OperatorDistanceToOwner >= 11.0f)
        {
            reason = "moving_anchor_obsolete_replan:elapsed=" + planElapsed.ToString("0.0", CultureInfo.InvariantCulture)
                + ":anchorDistance=" + activeLease.LastAnchorDistance.ToString("0.0", CultureInfo.InvariantCulture);
            return false;
        }

        if (VanguardPrimaryExecutionContract.ShouldKeepMovementContractUntilTerminal(snapshot, activeLease.LeaseId, out var contractReason))
        {
            reason = contractReason;
            return true;
        }

        float elapsed = (float)Math.Max(0.0d, (now - activeLease.StartedAtUtc).TotalSeconds);
        bool catchup = activeLease.InitialOwnerDistance >= VanguardMovementAuthorityDoctrine.MovementPlanCatchupOwnerDistanceMeters
            || snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.MovementPlanCatchupOwnerDistanceMeters;
        bool progress = activeLease.LastAnchorDistance < activeLease.InitialAnchorDistance - VanguardMovementAuthorityDoctrine.ClaimedCohesionProgressGainMeters
            || activeLease.LastOwnerDistance < activeLease.InitialOwnerDistance - 1.0f
            || snapshot.RealSpeed > 0.70f;
        if (catchup && (progress || elapsed <= VanguardMovementAuthorityDoctrine.MovementPlanCurrentAnchorProtectedSeconds))
        {
            if (ownerRotated && !ownerMoved)
            {
                reason = "active_catchup_ignore_owner_rotation";
                return true;
            }

            if (ownerMoved || ownerRotated)
            {
                reason = "active_catchup_queue_next_anchor";
                return true;
            }
        }

        if (indoor && activeLease.StationaryHold && elapsed <= VanguardMovementAuthorityDoctrine.ClaimedCohesionStationaryMinHoldSeconds + 4.0f)
        {
            reason = "indoor_sector_hold_preserve_active_anchor";
            return true;
        }

        return false;
    }

    private static bool TryBuildQueuedPlanClaim(OperatorDecisionSnapshot snapshot, Vector3 owner, Vector3 forward, string lane, string purpose, string ownerMode, float ownerSpeed, DateTimeOffset now, out CohesionClaimState claim, out string reason)
    {
        int deferralBefore = VanguardCohesionPlanningBudget.DeferralSerial;
        if (snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.CohesionLanePreservingFallbackDistanceMeters
            && TryBuildRallyFallbackClaim(snapshot, owner, forward, ownerMode, ownerSpeed, now, out claim))
        {
            reason = "queued_emergency_rally_fallback";
            return true;
        }

        if (TryBuildClaim(snapshot, owner, forward, lane, purpose, ownerMode, ownerSpeed, now, out claim))
        {
            reason = "queued_tactical_anchor";
            return true;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.ClaimedCohesionRallyFallbackDistanceMeters
            && TryBuildLanePreservingFallbackClaim(snapshot, owner, forward, lane, purpose, ownerMode, ownerSpeed, now, out claim))
        {
            reason = "queued_lane_preserving_fallback";
            return true;
        }

        reason = VanguardCohesionPlanningBudget.DeferralSerial != deferralBefore
            ? "queued_planning_deferred"
            : "queued_anchor_build_failed";
        claim = default;
        return false;
    }

    private static void QueuePendingPlan(string botProfileId, CohesionClaimState pendingClaim, DateTimeOffset now, string reason)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return;
        }

        bool replaced = false;
        string previous = "none";
        lock (Sync)
        {
            if (PendingPlanByBotProfileId.TryGetValue(botProfileId, out var old))
            {
                previous = old.ClaimId;
                replaced = true;
            }

            PendingPlanByBotProfileId[botProfileId] = pendingClaim;
        }

        LogThrottled("planQueued|" + botProfileId, now,
            $"VANGUARD_MOVEMENT_PLAN_QUEUED {pendingClaim.Summary}; previous={Safe(previous)}; replaced={Bool(replaced)}; reason={Safe(reason)}; queue=max_current_plus_next; tag={HostileIndoorMovementPlanStatusTag}; Tag={CohesionAnchorsRunStatusTag}");
    }

    private static void PromotePendingPlanAfterLease(CohesionClaimLeaseState lease, DateTimeOffset now, string outcome, string reason)
    {
        if (string.IsNullOrWhiteSpace(lease.BotProfileId))
        {
            return;
        }

        CohesionClaimState pending;
        bool hasPending;
        lock (Sync)
        {
            hasPending = PendingPlanByBotProfileId.TryGetValue(lease.BotProfileId, out pending);
            if (hasPending)
            {
                PendingPlanByBotProfileId.Remove(lease.BotProfileId);
            }
        }

        if (!hasPending)
        {
            return;
        }

        if (pending.ValidUntilUtc <= now)
        {
            LogThrottled("planPendingExpired|" + lease.BotProfileId, now,
                $"VANGUARD_MOVEMENT_PLAN_DROPPED operator={Safe(lease.OperatorId)}; botProfile={Safe(lease.BotProfileId)}; pending={Safe(pending.ClaimId)}; reason=pending_expired_after_{Safe(outcome)}; lease={Safe(lease.LeaseId)}; tag={HostileIndoorMovementPlanStatusTag}");
            return;
        }

        lock (Sync)
        {
            ClaimsByBotProfileId[lease.BotProfileId] = pending;
        }

        LogThrottled("planPromoted|" + lease.BotProfileId, now,
            $"VANGUARD_MOVEMENT_PLAN_PROMOTED {pending.Summary}; previousLease={Safe(lease.LeaseId)}; previousOutcome={Safe(outcome)}; previousReason={Safe(reason)}; policy=current_anchor_first_then_next; tag={HostileIndoorMovementPlanStatusTag}; Tag={CohesionAnchorsRunStatusTag}");
    }

    private static bool HasStoredClaimDrivePressure(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            reason = "snapshot_missing";
            return false;
        }

        CohesionClaimState claim;
        lock (Sync)
        {
            if (!ClaimsByBotProfileId.TryGetValue(snapshot.BotProfileId, out claim))
            {
                reason = "no_stored_claim";
                return false;
            }
        }

        if (claim.ValidUntilUtc <= now)
        {
            reason = "stored_claim_expired";
            return true;
        }

        float anchorDistance = HorizontalDistance(snapshot.Position, claim.Anchor);
        if (anchorDistance >= claim.AnchorRadiusMeters + 1.75f)
        {
            reason = "stored_claim_anchor_distance:" + anchorDistance.ToString("0.0", CultureInfo.InvariantCulture);
            return true;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.ClaimedCohesionRunDistanceMeters)
        {
            reason = "stored_claim_owner_distance:" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", CultureInfo.InvariantCulture);
            return true;
        }

        reason = "stored_claim_already_held";
        return false;
    }

    private static bool ShouldRefreshStableClaim(OperatorDecisionSnapshot snapshot, CohesionClaimState existing, string requestedLane, string requestedPurpose, string ownerMode, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (existing.ValidUntilUtc <= now)
        {
            reason = "claim_expired";
            return true;
        }

        float anchorDistance = HorizontalDistance(snapshot.Position, existing.Anchor);
        if (!VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot)
            && IsAnchorTooCloseToOtherOperator(snapshot, snapshot.Position, 2.75f, out var currentSpacingReason))
        {
            reason = "current_operator_stack_requires_replan:" + Safe(currentSpacingReason);
            return true;
        }

        bool laneChanged = !string.Equals(existing.Lane, requestedLane, StringComparison.OrdinalIgnoreCase);
        bool purposeChanged = !string.Equals(existing.Purpose, requestedPurpose, StringComparison.OrdinalIgnoreCase);
        bool ownerObservationMode = string.Equals(ownerMode, "stationary_hold", StringComparison.OrdinalIgnoreCase);
        bool modeChanged = existing.StationaryHold != ownerObservationMode;
        if (ownerObservationMode && (modeChanged || laneChanged || purposeChanged))
        {
            reason = "observation_deployment_refresh:modeChanged=" + Bool(modeChanged)
                + ":laneChanged=" + Bool(laneChanged)
                + ":purposeChanged=" + Bool(purposeChanged);
            return true;
        }

        if (RequiresDynamicRoleCorrection(existing, requestedLane, requestedPurpose))
        {
            reason = "dynamic_role_changed:" + Safe(existing.Lane) + "->" + Safe(requestedLane);
            return true;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.TravelCohesionForceMeters)
        {
            reason = "operator_far_for_replan:" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", CultureInfo.InvariantCulture);
            return true;
        }

        if (string.Equals(ownerMode, "stationary_hold", StringComparison.OrdinalIgnoreCase)
            && snapshot.SquadCohesion.OperatorDistanceToOwner > 12.5f
            && !VanguardInteriorSecurityPlanner.IsVerifiedCoverageHold(snapshot, now, out _))
        {
            reason = "stationary_compact_replan:" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", CultureInfo.InvariantCulture);
            return true;
        }

        if (anchorDistance >= VanguardMovementAuthorityDoctrine.ClaimedCohesionAnchorStableReplanMeters)
        {
            bool ownerStationary = string.Equals(ownerMode, "stationary_hold", StringComparison.OrdinalIgnoreCase);
            if (ownerStationary
                && snapshot.SquadCohesion.OperatorDistanceToOwner < VanguardMovementAuthorityDoctrine.ClaimedCohesionSprintDistanceMeters
                && anchorDistance < VanguardMovementAuthorityDoctrine.ClaimedCohesionAnchorStableReplanMeters + 12.0f
                && (laneChanged || purposeChanged || modeChanged))
            {
                reason = "stationary_hysteresis_keep_far_anchor:" + anchorDistance.ToString("0.0", CultureInfo.InvariantCulture);
                return false;
            }

            reason = "anchor_very_far_for_replan:" + anchorDistance.ToString("0.0", CultureInfo.InvariantCulture);
            return true;
        }

        if (anchorDistance >= Math.Max(existing.AnchorRadiusMeters + 7.0f, VanguardMovementAuthorityDoctrine.ClaimedCohesionSprintDistanceMeters))
        {
            reason = "anchor_far_requires_replan:" + anchorDistance.ToString("0.0", CultureInfo.InvariantCulture);
            return true;
        }

        if ((laneChanged || purposeChanged) && anchorDistance <= existing.AnchorRadiusMeters + 5.0f)
        {
            reason = "lane_or_purpose_changed_but_anchor_good";
            return false;
        }

        if (laneChanged && snapshot.SquadCohesion.OperatorDistanceToOwner < VanguardMovementAuthorityDoctrine.ClaimedCohesionSprintDistanceMeters)
        {
            reason = "lane_changed_but_owner_stable_keep_current";
            return false;
        }

        reason = "stable_claim_reusable";
        return false;
    }

    private static int ResolveDynamicMaxActiveLeases(IReadOnlyList<OperatorDecisionSnapshot> snapshots)
    {
        int live = snapshots == null ? 0 : snapshots.Count(snapshot => snapshot != null && snapshot.Alive && !VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(snapshot));
        if (live <= 0)
        {
            return 0;
        }

        return Math.Max(1, Math.Min(VanguardMovementAuthorityDoctrine.ClaimedCohesionMaxActiveLeases, live));
    }

    private static string CheckActiveLeaseInterruptGate(OperatorDecisionSnapshot snapshot, CohesionClaimLeaseState lease, DateTimeOffset now)
    {
        if (snapshot != null && VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out var combatWindowReason))
        {
            return "sain_combat_primary_protected:" + combatWindowReason;
        }

        if (snapshot == null || !snapshot.Alive)
        {
            return "operator_dead";
        }

        if (!snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement || !snapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            return "owner_unreliable";
        }

        if (VanguardOrchestratorAuthorityPolicy.ShouldBlockCohesionMutation(snapshot, out var authorityBlockReason))
        {
            return "exclusive_domain_blocks_cohesion:" + authorityBlockReason;
        }

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot) || VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(snapshot))
        {
            return "immediate_combat_awareness";
        }

        if (VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(snapshot))
        {
            return "stationary_medical_authority";
        }

        if (snapshot.MovementAuthority.HardOutsideBubble || snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.HardCorrectionMeters)
        {
            return "hard_return_higher_priority";
        }

        if (snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true)
        {
            return "critical_loot_active";
        }

        double elapsed = (now - lease.StartedAtUtc).TotalSeconds;
        if (elapsed <= VanguardMovementAuthorityDoctrine.ClaimedCohesionActiveLeaseProtectedSeconds)
        {
            return "none";
        }

        if (VanguardCombatAwarenessBridge.HasFreshSquadCombatContact(snapshot, now, out var contactReason)
            && snapshot.SquadCohesion.OperatorDistanceToOwner < VanguardMovementAuthorityDoctrine.ClaimedCohesionFreshContactHoldMeters)
        {
            return "fresh_squad_contact_hold_sector:" + contactReason;
        }

        return "none";
    }

    private static string CheckStartGate(OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot != null && VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out var combatWindowReason))
        {
            return "sain_combat_primary_protected:" + combatWindowReason;
        }

        if (snapshot == null || !snapshot.Alive)
        {
            return "operator_dead";
        }

        if (!snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement || !snapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            return "owner_unreliable";
        }

        if (VanguardOrchestratorAuthorityPolicy.ShouldBlockCohesionMutation(snapshot, out var authorityBlockReason))
        {
            return "exclusive_domain_blocks_cohesion:" + authorityBlockReason;
        }

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot) || VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(snapshot))
        {
            return "immediate_combat_awareness";
        }

        if (VanguardCombatAwarenessBridge.HasFreshSquadCombatContact(snapshot, now, out var contactReason)
            && snapshot.SquadCohesion.OperatorDistanceToOwner < VanguardMovementAuthorityDoctrine.ClaimedCohesionFreshContactHoldMeters)
        {
            return "fresh_squad_contact:" + contactReason;
        }

        if (VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(snapshot))
        {
            return "stationary_medical_authority";
        }

        if (snapshot.MovementAuthority.HardOutsideBubble || snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.HardCorrectionMeters)
        {
            return "hard_return_higher_priority";
        }

        if (snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true)
        {
            return "critical_loot_active";
        }

        return "none";
    }

    private static bool NeedsExternalPreempt(OperatorDecisionSnapshot snapshot)
    {
        return snapshot.Orbit.Active || snapshot.Movement.HasPath == true || snapshot.Looting.HasActiveLootable == true;
    }

    private static float ScoreStartCandidate(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot == null || !string.Equals(snapshot.MovementAuthority.BrokerPlan.Contract.RequestKind, VanguardMovementContractPolicy.ClaimedCohesionSlot, StringComparison.OrdinalIgnoreCase))
        {
            return -1f;
        }

        float score = snapshot.SquadCohesion.OperatorDistanceToOwner * 1.30f;
        if (snapshot.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.ClaimedCohesionSprintDistanceMeters) score += 18f;
        if (snapshot.Movement.HasPath == true) score += 12f;
        if (snapshot.Orbit.Active) score += 10f;
        if (!snapshot.SquadCohesion.UsefulPosition) score += 8f;
        if (snapshot.SquadCohesion.SectorDuplicate) score += 6f;
        if (snapshot.SquadCohesion.RearOverstacked) score += 5f;
        if (HasStoredClaimDrivePressure(snapshot, DateTimeOffset.UtcNow, out _)) score += 7f;
        return score;
    }

    private static string LaneFor(int index, int count, OperatorDecisionSnapshot snapshot, string ownerMode)
    {
        bool indoor = IsIndoor(snapshot);
        bool stationary = string.Equals(ownerMode, "stationary_hold", StringComparison.OrdinalIgnoreCase);
        bool fastTravel = string.Equals(ownerMode, "fast_travel", StringComparison.OrdinalIgnoreCase);

        if (count <= 1)
        {
            return indoor ? "entry_hold" : "front_offset";
        }

        if (indoor && stationary)
        {
            return index switch
            {
                0 => "left_entry_hold",
                1 => "right_entry_hold",
                2 => "stair_or_rear_guard",
                3 => "corridor_probe",
                _ => index % 2 == 0 ? "left_flank" : "right_flank"
            };
        }

        // Vanguard: for a three-Operator service, avoid the observed all-rear blob.
        // Preferred calm/travel shape is left/front support + right/front support + one rear guard.
        // The names intentionally keep the existing anchor machinery; only the lane contract changes.
        if (count == 2)
        {
            return index == 0 ? "left_front" : "right_front";
        }

        if (count == 3)
        {
            if (fastTravel)
            {
                return index switch
                {
                    0 => "left_front",
                    1 => "right_front",
                    2 => "rear_guard_close",
                    _ => index % 2 == 0 ? "left_flank" : "right_flank"
                };
            }

            if (stationary)
            {
                return index switch
                {
                    0 => "left_front",
                    1 => "rear_guard_close",
                    2 => "right_front",
                    _ => index % 2 == 0 ? "left_flank" : "right_flank"
                };
            }

            return index switch
            {
                0 => "left_front",
                1 => "right_front",
                2 => "rear_guard_close",
                _ => index % 2 == 0 ? "left_flank" : "right_flank"
            };
        }

        if (stationary)
        {
            return index switch
            {
                0 => "left_front",
                1 => "right_front",
                2 => "rear_guard_close",
                3 => "wide_flank",
                _ => index % 2 == 0 ? "left_flank" : "right_flank"
            };
        }

        if (fastTravel)
        {
            return index switch
            {
                0 => "left_front",
                1 => "right_front",
                2 => "rear_guard_close",
                3 => "forward_probe",
                _ => index % 2 == 0 ? "left_flank" : "right_flank"
            };
        }

        return index switch
        {
            0 => "left_front",
            1 => "right_front",
            2 => "rear_guard_close",
            3 => "wide_flank",
            _ => index % 2 == 0 ? "left_flank" : "right_flank"
        };
    }

    private static bool RequiresDynamicRoleCorrection(CohesionClaimState existing, string requestedLane, string requestedPurpose)
    {
        if (string.IsNullOrWhiteSpace(existing.BotProfileId))
        {
            return false;
        }

        string existingClass = FormationRoleClass(existing.Lane, existing.Purpose);
        string requestedClass = FormationRoleClass(requestedLane, requestedPurpose);
        return !string.Equals(existingClass, requestedClass, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormationRoleClass(string? lane, string? purpose)
    {
        string normalizedLane = lane ?? string.Empty;
        string normalizedPurpose = purpose ?? string.Empty;
        if (normalizedPurpose.IndexOf("cover_verified_navmesh_access", StringComparison.OrdinalIgnoreCase) >= 0
            || normalizedLane.IndexOf("interior_entry", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "interior_access";
        }
        if (IsLeadLane(normalizedLane))
        {
            return "lead";
        }
        if (IsRearLane(normalizedLane))
        {
            return "rear";
        }
        if (normalizedLane.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "close_support";
        }
        return "other";
    }

    private static bool IsLeadLane(string? lane)
    {
        return !string.IsNullOrWhiteSpace(lane)
            && lane.IndexOf("lead_forward", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string ResolveGenericFallbackLane(OperatorDecisionSnapshot snapshot, string requestedLane)
    {
        if (!string.IsNullOrWhiteSpace(requestedLane)
            && requestedLane.IndexOf("interior_sector_", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return requestedLane;
        }

        string sector = Safe(snapshot.SquadCohesion.Sector);
        if (sector.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "left_front";
        }
        if (sector.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "right_front";
        }
        if (sector.IndexOf("front", StringComparison.OrdinalIgnoreCase) >= 0
            || sector.IndexOf("forward", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "front_close";
        }
        return "rear_guard_close";
    }

    private static string PurposeFor(string lane, string ownerMode, OperatorDecisionSnapshot snapshot)
    {
        if (lane.IndexOf("lead_forward", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "bounded_lead_security";
        }

        if (lane.IndexOf("side_close", StringComparison.OrdinalIgnoreCase) >= 0
            || lane.IndexOf("front_close", StringComparison.OrdinalIgnoreCase) >= 0
            || lane.IndexOf("left_close", StringComparison.OrdinalIgnoreCase) >= 0
            || lane.IndexOf("right_close", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return string.Equals(ownerMode, "stationary_hold", StringComparison.OrdinalIgnoreCase) ? "compact_stationary_security" : "close_moving_support";
        }

        if (lane.IndexOf("entry", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "hold_entry_angle";
        }

        if (lane.IndexOf("stair", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "hold_stair_or_vertical_access";
        }

        if (lane.IndexOf("corridor", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "hold_corridor_opening";
        }

        if (string.Equals(ownerMode, "stationary_hold", StringComparison.OrdinalIgnoreCase))
        {
            if (IsForwardLane(lane))
            {
                return "hold_forward_angle";
            }

            return IsRearLane(lane) ? "rear_security" : "hold_flank_angle";
        }

        if (string.Equals(ownerMode, "fast_travel", StringComparison.OrdinalIgnoreCase))
        {
            return IsRearLane(lane) ? "catch_up_rear_security" : "catch_up_tactical_spacing";
        }

        return IsRearLane(lane) ? "rear_security" : IsForwardLane(lane) ? "forward_offset_spacing" : "flank_spacing";
    }

    private static float DistanceFor(string lane, string ownerMode, OperatorDecisionSnapshot snapshot)
    {
        bool indoor = IsIndoor(snapshot);
        if (lane.IndexOf("lead_forward", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return indoor ? 6.5f : string.Equals(ownerMode, "fast_travel", StringComparison.OrdinalIgnoreCase) ? 10.0f : 8.5f;
        }
        if (lane.IndexOf("side_close", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return indoor ? 5.5f : 7.5f;
        }
        if (lane.IndexOf("left_close", StringComparison.OrdinalIgnoreCase) >= 0
            || lane.IndexOf("right_close", StringComparison.OrdinalIgnoreCase) >= 0
            || lane.IndexOf("front_close", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return indoor ? 5.0f : 7.0f;
        }
        if (string.Equals(ownerMode, "stationary_hold", StringComparison.OrdinalIgnoreCase))
        {
            if (indoor)
            {
                return IsRearLane(lane) ? 7.0f : IsForwardLane(lane) ? 6.5f : 6.8f;
            }

            return IsRearLane(lane) ? 9.5f : IsForwardLane(lane) ? 10.5f : 9.0f;
        }

        if (string.Equals(ownerMode, "fast_travel", StringComparison.OrdinalIgnoreCase))
        {
            return IsRearLane(lane) ? 12.0f : IsForwardLane(lane) ? 11.5f : 10.5f;
        }

        return indoor
            ? (IsRearLane(lane) ? 7.2f : IsForwardLane(lane) ? 7.0f : 7.0f)
            : (IsRearLane(lane) ? 10.0f : IsForwardLane(lane) ? 11.0f : 9.5f);
    }

    private static Vector3 DirectionFor(string lane, Vector3 forward, Vector3 right)
    {
        if (lane.IndexOf("lead_forward_left", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return Flatten(forward * 0.94f - right * 0.34f).normalized;
        }
        if (lane.IndexOf("lead_forward_right", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return Flatten(forward * 0.94f + right * 0.34f).normalized;
        }
        if (lane.IndexOf("left_side_close", StringComparison.OrdinalIgnoreCase) >= 0 || lane.IndexOf("left_close", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return Flatten(-right * 0.90f + forward * 0.18f).normalized;
        }
        if (lane.IndexOf("right_side_close", StringComparison.OrdinalIgnoreCase) >= 0 || lane.IndexOf("right_close", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return Flatten(right * 0.90f + forward * 0.18f).normalized;
        }
        if (lane.IndexOf("front_close", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return forward;
        }

        if (lane.IndexOf("left_front", StringComparison.OrdinalIgnoreCase) >= 0 || lane.IndexOf("left_entry", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return Flatten(forward * 0.68f - right * 0.74f).normalized;
        }

        if (lane.IndexOf("right_front", StringComparison.OrdinalIgnoreCase) >= 0 || lane.IndexOf("right_entry", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return Flatten(forward * 0.68f + right * 0.74f).normalized;
        }

        if (lane.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return Flatten(-right * 0.92f - forward * 0.15f).normalized;
        }

        if (lane.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return Flatten(right * 0.92f - forward * 0.15f).normalized;
        }

        if (lane.IndexOf("forward", StringComparison.OrdinalIgnoreCase) >= 0 || lane.IndexOf("front", StringComparison.OrdinalIgnoreCase) >= 0 || lane.IndexOf("probe", StringComparison.OrdinalIgnoreCase) >= 0 || lane.IndexOf("corridor", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return forward;
        }

        return -forward;
    }


    private static void AddFullBubbleFallbackCandidates(List<Vector3> candidates, Vector3 owner, Vector3 forward, Vector3 right, float distance, string lane, OperatorDecisionSnapshot snapshot)
    {
        float inner = Math.Max(IsIndoor(snapshot) ? 5.5f : 7.0f, distance - 4.0f);
        float mid = Math.Max(inner + 1.5f, distance);
        float outer = Math.Min(IsIndoor(snapshot) ? 20.0f : 28.0f, distance + 5.5f);
        float[] frontAngles = IsIndoor(snapshot)
            ? new[] { -38f, 38f, -58f, 58f, 0f }
            : new[] { -32f, 32f, -52f, 52f, 0f };
        float[] flankAngles = new[] { -82f, 82f, -108f, 108f };
        float[] rearAngles = new[] { 150f, -150f, 162f, -162f };

        // Vanguard: claims may occupy the entire useful bubble.  Rear remains a possible lane,
        // not a hard doctrine.  Front/flank slots are allowed when the navmesh and path score are better.
        foreach (float radius in new[] { inner, mid, outer })
        {
            if (!IsRearLane(lane))
            {
                AddCandidateSweep(candidates, owner, forward, radius, frontAngles);
                AddCandidateSweep(candidates, owner, right, radius, flankAngles);
            }

            if (!IsForwardLane(lane))
            {
                AddCandidateSweep(candidates, owner, -forward, radius, rearAngles);
            }
        }
    }

    private static bool IsForwardLane(string lane)
    {
        return lane.IndexOf("front", StringComparison.OrdinalIgnoreCase) >= 0
            || lane.IndexOf("forward", StringComparison.OrdinalIgnoreCase) >= 0
            || lane.IndexOf("entry", StringComparison.OrdinalIgnoreCase) >= 0
            || lane.IndexOf("probe", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsRearLane(string lane)
    {
        return lane.IndexOf("rear", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void AddCandidateSweep(List<Vector3> candidates, Vector3 owner, Vector3 direction, float distance, float[] angles)
    {
        Vector3 dir = Flatten(direction);
        if (dir.sqrMagnitude <= 0.001f)
        {
            dir = Vector3.back;
        }
        dir.Normalize();
        foreach (float angle in angles)
        {
            candidates.Add(owner + Rotate(dir, angle) * distance);
        }
    }

    public static bool TryIssuePathSafeHardReturnFallback(OperatorDecisionSnapshot snapshot, BotOwner? botOwner, Vector3 botPosition, DateTimeOffset now, string reason, out string result)
    {
        result = "none";
        if (snapshot == null || botOwner == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId) || !snapshot.Alive)
        {
            result = "missing_snapshot_or_botowner";
            return false;
        }

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot))
        {
            result = "true_direct_threat_preserves_combat";
            return false;
        }

        if (!snapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            result = "owner_position_missing";
            return false;
        }

        if (VanguardSquadTravelRouteMemory.IsRouteUsable(snapshot, now, out var routeUsableReason)
            && VanguardSquadTravelRouteMemory.ShouldDriveTravel(snapshot, now, out var routeDriveReason)
            && VanguardSquadTravelRouteMemory.TryResolveTarget(snapshot, botPosition, now, out var freshRouteTarget)
            && freshRouteTarget.Valid)
        {
            result = "fresh_monotonic_route_preserves_travel:" + routeUsableReason + ":" + freshRouteTarget.Reason;
            VanguardClientDiagnosticsLog.Info(VanguardSquadTravelRouteMemory.StatusTag,
                $"VANGUARD_HARD_RETURN_FALLBACK_SUPPRESSED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; route={Safe(freshRouteTarget.Summary)}; routeDrive={Safe(routeDriveReason)}; fallbackReason={Safe(reason)}; hardReturnIssued=false; ownerForwardCandidateScan=false; doctrine=fresh_resolvable_corridor_precedes_owner_relative_fallback; tag={VanguardSquadTravelRouteMemory.StatusTag}");
            return false;
        }

        Vector3 owner = snapshot.SquadCohesion.OwnerPosition.Value;
        Vector3 forward = snapshot.SquadCohesion.OwnerForward ?? Vector3.forward;
        forward = Flatten(forward);
        if (forward.sqrMagnitude <= 0.001f)
        {
            forward = Vector3.forward;
        }
        forward.Normalize();
        Vector3 right = new Vector3(forward.z, 0f, -forward.x);
        if (right.sqrMagnitude <= 0.001f)
        {
            right = Vector3.right;
        }
        right.Normalize();

        var rawCandidates = new List<Vector3>(18)
        {
            owner - forward * 6.0f,
            owner - forward * 9.0f,
            owner - forward * 12.0f,
            owner - forward * 7.0f + right * 5.0f,
            owner - forward * 7.0f - right * 5.0f,
            owner + right * 7.0f,
            owner - right * 7.0f,
            owner + forward * 5.0f + right * 4.0f,
            owner + forward * 5.0f - right * 4.0f,
            owner
        };

        bool indoor = IsIndoor(snapshot);
        Vector3 best = Vector3.zero;
        float bestPath = float.MaxValue;
        int bestCorners = 0;
        string bestSummary = "none";
        bool bestStrict = false;
        foreach (var raw in rawCandidates)
        {
            if (!TrySample(raw, indoor ? 4.0f : 6.0f, out var sampled))
            {
                continue;
            }

            if (!TryPath(botPosition, sampled, out var pathDistance, out var corners, out var status))
            {
                continue;
            }

            float ownerDirect = HorizontalDistance(owner, sampled);
            float botDirect = HorizontalDistance(botPosition, sampled);
            float ratio = botDirect <= 0.25f ? 1.0f : pathDistance / botDirect;
            bool strict = ownerDirect <= (indoor ? 18.0f : 24.0f)
                && pathDistance <= (snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.TacticalBubbleMeters ? (indoor ? 34.0f : 48.0f) : (indoor ? 82.0f : 110.0f))
                && ratio <= Math.Max(3.0f, VanguardMovementAuthorityDoctrine.ClaimPathHardCloseSupportRatio + 1.0f);
            if ((strict && !bestStrict) || (strict == bestStrict && pathDistance < bestPath))
            {
                best = sampled;
                bestPath = pathDistance;
                bestCorners = corners;
                bestStrict = strict;
                bestSummary = "ownerDirect=" + ownerDirect.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";botDirect=" + botDirect.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";path=" + pathDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";ratio=" + ratio.ToString("0.00", CultureInfo.InvariantCulture)
                    + ";corners=" + corners.ToString(CultureInfo.InvariantCulture)
                    + ";strict=" + Bool(strict)
                    + ";status=" + Safe(status);
            }
        }

        if (bestPath >= float.MaxValue * 0.5f)
        {
            result = "no_navmesh_path_to_owner_trail_anchor";
            return false;
        }

        if (!VanguardMainIntentScheduler.TryOpenPathSafeHardReturnFallback(snapshot, now, reason, out var schedulerWindowId, out var schedulerReason))
        {
            result = "scheduler_denied:" + schedulerReason + ":" + bestSummary;
            VanguardClientDiagnosticsLog.Info(HardReturnAlertStatusTag,
                $"VANGUARD_HARD_RETURN_FALLBACK_SCHEDULER_DENIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; scheduler={Safe(schedulerReason)}; ownerDistance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; path={bestPath:0.0}; anchor={FormatVector(best)}; policy=scheduler_owned_no_direct_reissue; tag={VanguardMovementAuthorityDoctrine.MovementCommandQueueStatusTag}; Tag={HardReturnAlertStatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
            return false;
        }

        string leaseId = schedulerWindowId;
        DateTimeOffset expires = now + TimeSpan.FromSeconds(Math.Max(22.0f, VanguardMovementAuthorityDoctrine.ClaimedCohesionMaxDurationSeconds + 8.0f));
        bool issued = VanguardReturnMovementCommandStore.Issue(
            leaseId,
            snapshot.OperatorId,
            snapshot.BotProfileId,
            best,
            Math.Max(7.5f, VanguardMovementAuthorityDoctrine.HardReturnAnchorRadiusMeters),
            sprint: true,
            now,
            expires,
            VanguardMovementContractPolicy.ActionRallyHardReturn,
            "path_safe_hard_return;reason=" + Safe(reason) + ";" + bestSummary,
            bestPath,
            out var commandResult);
        result = (issued ? "issued" : "rejected") + ":" + commandResult + ":" + bestSummary;
        if (!issued)
        {
            VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "fallback_command_rejected:" + commandResult, bestSummary, schedulerWindowId);
            VanguardClientDiagnosticsLog.Warning(HardReturnAlertStatusTag,
                $"VANGUARD_HARD_RETURN_FALLBACK_COMMAND_REJECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(leaseId)}; command={Safe(commandResult)}; schedulerClosed=true; noOrphanWindow=true; tag={VanguardPrimaryExecutionContract.HardReturnPhysicalProgressStatusTag}");
            return false;
        }

        if (issued)
        {
            bool registered = VanguardHardReturnMovementExecutor.TryRegisterPathSafeFallback(
                snapshot,
                botOwner,
                leaseId,
                best,
                Math.Max(7.5f, VanguardMovementAuthorityDoctrine.HardReturnAnchorRadiusMeters),
                now,
                expires,
                "path_safe_hard_return;reason=" + Safe(reason) + ";" + bestSummary,
                bestPath,
                commandResult,
                out var registerResult);
            if (!registered)
            {
                string clearResult = VanguardReturnMovementCommandStore.ClearOwned(snapshot.BotProfileId, leaseId, now, "fallback_registration_failed:" + registerResult);
                VanguardMainIntentScheduler.FinishPrimaryWindow(snapshot.BotProfileId, now, "Failed", "fallback_registration_failed:" + registerResult, clearResult, schedulerWindowId);
                result = "registration_failed:" + registerResult + ":clear=" + clearResult + ":" + bestSummary;
                VanguardClientDiagnosticsLog.Warning(HardReturnAlertStatusTag,
                    $"VANGUARD_HARD_RETURN_FALLBACK_REGISTRATION_FAILED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; lease={Safe(leaseId)}; reason={Safe(registerResult)}; clear={Safe(clearResult)}; noUnmonitoredCommand=true; tag={VanguardPrimaryExecutionContract.HardReturnPhysicalProgressStatusTag}");
                return false;
            }

            result = "issued_and_monitored:" + commandResult + ":" + registerResult + ":" + bestSummary;
            VanguardClientDiagnosticsLog.Info(HardReturnAlertStatusTag,
                $"VANGUARD_HARD_RETURN_FALLBACK_ASSIGNED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; ownerDistance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; path={bestPath:0.0}; corners={bestCorners}; strict={Bool(bestStrict)}; anchor={FormatVector(best)}; schedulerWindow={Safe(schedulerWindowId)}; result={Safe(commandResult)}; registration={Safe(registerResult)}; policy=scheduler_owned_path_safe_catchup_canonical_executor_physical_monitor; tag={HardReturnAlertStatusTag}; physicalTag={VanguardPrimaryExecutionContract.HardReturnPhysicalProgressStatusTag}; Tag={VanguardMovementAuthorityDoctrine.MovementCommandQueueStatusTag}; moveBridgeTag={VanguardReturnMovementCommandStore.StatusTag}");
        }
        return issued;
    }

    private static bool TryValidateClaimPath(OperatorDecisionSnapshot snapshot, CohesionClaimState claim, Vector3 start, Vector3 end, out string summary, out float distance, out string rejectReason)
    {
        rejectReason = "none";
        if (!TryPath(start, end, out distance, out var corners, out summary))
        {
            summary = summary + ";distance=" + distance.ToString("0.0", CultureInfo.InvariantCulture) + ";corners=" + corners.ToString(CultureInfo.InvariantCulture);
            rejectReason = "navmesh_path_invalid";
            return false;
        }

        bool indoor = IsIndoor(snapshot);
        float ceiling = claim.UsesInteriorPathContract && claim.ExecutionPathCeilingMeters > 0f
            ? claim.ExecutionPathCeilingMeters
            : string.Equals(claim.Lane, "rally_bubble", StringComparison.OrdinalIgnoreCase)
                ? ResolveRallyBotPathCeiling(snapshot, indoor)
                : ResolveClaimBotPathCeiling(snapshot, indoor);
        float direct = HorizontalDistance(start, end);
        float ratio = direct <= 0.25f ? 1.0f : distance / direct;
        summary = summary + ";distance=" + distance.ToString("0.0", CultureInfo.InvariantCulture) + ";direct=" + direct.ToString("0.0", CultureInfo.InvariantCulture) + ";ratio=" + ratio.ToString("0.00", CultureInfo.InvariantCulture) + ";ceiling=" + ceiling.ToString("0.0", CultureInfo.InvariantCulture) + ";corners=" + corners.ToString(CultureInfo.InvariantCulture);
        if (distance > ceiling)
        {
            rejectReason = "path_ceiling_exceeded";
            return false;
        }

        if (claim.UsesInteriorPathContract
            && claim.ExecutionPathRatioCeiling > 0f
            && distance > 34.0f
            && ratio > claim.ExecutionPathRatioCeiling)
        {
            rejectReason = "interior_path_ratio_exceeded";
            return false;
        }

        if (!claim.UsesInteriorPathContract
            && snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.TacticalBubbleMeters
            && distance > snapshot.SquadCohesion.OperatorDistanceToOwner + VanguardMovementAuthorityDoctrine.ClaimPathHardCloseSupportExtraMeters)
        {
            rejectReason = "support_path_too_long_for_direct_bubble";
            return false;
        }

        if (!claim.UsesInteriorPathContract
            && snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.TacticalBubbleMeters
            && ratio > VanguardMovementAuthorityDoctrine.ClaimPathHardCloseSupportRatio
            && distance > 44.0f)
        {
            rejectReason = "support_path_detour_ratio";
            return false;
        }

        return true;
    }

    private static float ResolveClaimBotPathCeiling(OperatorDecisionSnapshot snapshot, bool indoor)
    {
        float ownerDistance = snapshot.SquadCohesion.OperatorDistanceToOwner;
        if (ownerDistance <= VanguardMovementAuthorityDoctrine.ClaimedCohesionMicroHoldOwnerMeters + 4.0f)
        {
            return indoor ? 22.0f : 30.0f;
        }

        if (ownerDistance <= VanguardMovementAuthorityDoctrine.TacticalBubbleMeters)
        {
            return indoor ? 28.0f : 42.0f;
        }

        if (ownerDistance <= VanguardMovementAuthorityDoctrine.HardCorrectionMeters)
        {
            return indoor ? 55.0f : 74.0f;
        }

        return indoor ? 90.0f : 120.0f;
    }

    private static float ResolveRallyBotPathCeiling(OperatorDecisionSnapshot snapshot, bool indoor)
    {
        float ownerDistance = snapshot.SquadCohesion.OperatorDistanceToOwner;
        if (ownerDistance <= VanguardMovementAuthorityDoctrine.ClaimedCohesionMicroHoldOwnerMeters + 4.0f)
        {
            return indoor ? 24.0f : 34.0f;
        }

        if (ownerDistance <= VanguardMovementAuthorityDoctrine.TacticalBubbleMeters)
        {
            return indoor ? 30.0f : 44.0f;
        }

        if (ownerDistance <= VanguardMovementAuthorityDoctrine.HardCorrectionMeters)
        {
            return indoor ? 58.0f : 78.0f;
        }

        return indoor ? 95.0f : 125.0f;
    }

    private static bool IsActiveClaimPathDivergent(OperatorDecisionSnapshot snapshot, CohesionClaimLeaseState lease, Vector3 botPosition, out string reason)
    {
        reason = "none";
        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot))
        {
            reason = "true_direct_threat";
            return false;
        }

        bool indoor = IsIndoor(snapshot);
        float ceiling = lease.UsesInteriorPathContract && lease.ExecutionPathCeilingMeters > 0f
            ? lease.ExecutionPathCeilingMeters
            : string.Equals(lease.Lane, "rally_bubble", StringComparison.OrdinalIgnoreCase)
                ? ResolveRallyBotPathCeiling(snapshot, indoor)
                : ResolveClaimBotPathCeiling(snapshot, indoor);
        if (!TryPath(botPosition, lease.Anchor, out var pathDistance, out var corners, out var status))
        {
            reason = "active_path_invalid:" + status;
            return true;
        }

        float direct = HorizontalDistance(botPosition, lease.Anchor);
        float ratio = direct <= 0.25f ? 1.0f : pathDistance / direct;
        if (pathDistance > ceiling)
        {
            reason = "active_path_ceiling_exceeded:path=" + pathDistance.ToString("0.0", CultureInfo.InvariantCulture) + ":ceiling=" + ceiling.ToString("0.0", CultureInfo.InvariantCulture) + ":corners=" + corners.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (lease.UsesInteriorPathContract
            && lease.ExecutionPathRatioCeiling > 0f
            && pathDistance > 34.0f
            && ratio > lease.ExecutionPathRatioCeiling)
        {
            reason = "active_interior_path_ratio_exceeded:" + ratio.ToString("0.00", CultureInfo.InvariantCulture);
            return true;
        }

        if (!lease.UsesInteriorPathContract
            && snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.TacticalBubbleMeters
            && pathDistance > snapshot.SquadCohesion.OperatorDistanceToOwner + VanguardMovementAuthorityDoctrine.ClaimPathHardCloseSupportExtraMeters)
        {
            reason = "active_support_path_too_long:path=" + pathDistance.ToString("0.0", CultureInfo.InvariantCulture) + ":owner=" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", CultureInfo.InvariantCulture);
            return true;
        }

        if (!lease.UsesInteriorPathContract
            && snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.TacticalBubbleMeters
            && ratio > VanguardMovementAuthorityDoctrine.ClaimPathHardCloseSupportRatio
            && pathDistance > 44.0f)
        {
            reason = "active_support_detour_ratio:" + ratio.ToString("0.00", CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private static bool IsAnchorTooCloseToOtherOperator(OperatorDecisionSnapshot snapshot, Vector3 anchor, float minSpacing, out string reason)
    {
        reason = "none";
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.OwnerProfileId))
        {
            reason = "owner_unknown";
            return false;
        }

        foreach (var candidate in VanguardRaidOperatorRuntimeRegistry.GetOperatorsForOwner(snapshot.OwnerProfileId))
        {
            if (candidate.BotOwner == null || candidate.BotOwner.IsDead)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(candidate.BotProfileId) && string.Equals(candidate.BotProfileId, snapshot.BotProfileId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Vector3 other = ResolveBotPosition(candidate.BotOwner);
            float distance = HorizontalDistance(other, anchor);
            if (distance < minSpacing)
            {
                reason = Safe(candidate.BotProfileId) + "_distance_" + distance.ToString("0.00", CultureInfo.InvariantCulture) + "_min_" + minSpacing.ToString("0.00", CultureInfo.InvariantCulture);
                return true;
            }
        }

        reason = "spacing_ok";
        return false;
    }


    private static bool IsAnchorTooCloseToExistingClaim(OperatorDecisionSnapshot snapshot, Vector3 anchor, float minSpacing, out string reason)
    {
        reason = "none";
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.OwnerProfileId))
        {
            reason = "owner_unknown";
            return false;
        }

        lock (Sync)
        {
            foreach (var claim in ClaimsByBotProfileId.Values)
            {
                if (!string.Equals(claim.OwnerProfileId, snapshot.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(claim.BotProfileId, snapshot.BotProfileId, StringComparison.OrdinalIgnoreCase)
                    || claim.ValidUntilUtc <= DateTimeOffset.UtcNow)
                {
                    continue;
                }

                float distance = HorizontalDistance(claim.Anchor, anchor);
                if (distance < minSpacing)
                {
                    reason = Safe(claim.BotProfileId) + "_claim_" + Safe(claim.Lane) + "_distance_" + distance.ToString("0.00", CultureInfo.InvariantCulture) + "_min_" + minSpacing.ToString("0.00", CultureInfo.InvariantCulture);
                    return true;
                }
            }
        }

        reason = "claim_spacing_ok";
        return false;
    }

    private static bool IsIndoor(OperatorDecisionSnapshot snapshot)
    {
        string env = snapshot.SquadCohesion.TacticalEnvironmentKind ?? string.Empty;
        return env.IndexOf("corridor", StringComparison.OrdinalIgnoreCase) >= 0
            || env.IndexOf("room", StringComparison.OrdinalIgnoreCase) >= 0
            || env.IndexOf("urban_wraparound", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void SetCooldown(string botProfileId, DateTimeOffset now, float seconds)
    {
        lock (Sync)
        {
            CooldownByBotProfileId[botProfileId] = now + TimeSpan.FromSeconds(Math.Max(0.5f, seconds));
        }
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

    private static bool TrySample(Vector3 raw, float radius, out Vector3 sampled)
    {
        if (NavMesh.SamplePosition(raw + Vector3.up * 0.25f, out var hit, radius, NavMesh.AllAreas))
        {
            sampled = hit.position;
            return true;
        }

        sampled = Vector3.zero;
        return false;
    }

    private static bool TryPath(Vector3 start, Vector3 end, out float distance, out int corners, out string status)
    {
        distance = 0f;
        corners = 0;
        status = "none";
        if (!TrySample(start, 4.0f, out var sampledStart))
        {
            status = "start_sample_failed";
            return false;
        }

        if (!TrySample(end, 4.0f, out var sampledEnd))
        {
            status = "end_sample_failed";
            return false;
        }

        if (!VanguardCohesionPlanningBudget.TryConsumePathCalculation(out var budgetReason))
        {
            status = budgetReason;
            return false;
        }

        var path = new NavMeshPath();
        bool calculated = NavMesh.CalculatePath(sampledStart, sampledEnd, NavMesh.AllAreas, path);
        corners = path.corners == null ? 0 : path.corners.Length;
        distance = PathDistance(path);
        status = "calculated=" + Bool(calculated) + ";status=" + path.status + ";corners=" + corners.ToString(CultureInfo.InvariantCulture);
        return calculated && path.status == NavMeshPathStatus.PathComplete && corners >= 2;
    }

    private static float PathDistance(NavMeshPath path)
    {
        if (path.corners == null || path.corners.Length < 2)
        {
            return 0f;
        }

        float distance = 0f;
        for (int index = 1; index < path.corners.Length; index++)
        {
            distance += HorizontalDistance(path.corners[index - 1], path.corners[index]);
        }

        return distance;
    }

    private static Vector3 Rotate(Vector3 vector, float degrees)
    {
        float radians = degrees * Mathf.Deg2Rad;
        float cos = Mathf.Cos(radians);
        float sin = Mathf.Sin(radians);
        return new Vector3(vector.x * cos - vector.z * sin, 0f, vector.x * sin + vector.z * cos);
    }

    private static Vector3 Flatten(Vector3 vector)
    {
        vector.y = 0f;
        return vector;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
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

    private static string Bool(bool value) => value ? "true" : "false";

    private static string FormatVector(Vector3 value)
    {
        return value.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + value.y.ToString("0.0", CultureInfo.InvariantCulture) + "," + value.z.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }

    private readonly struct AtomicHandoffTicket
    {
        public AtomicHandoffTicket(
            string botProfileId,
            string ownerProfileId,
            string claimId,
            Vector3 claimAnchor,
            Vector3 validatedBotPosition,
            DateTimeOffset validatedAtUtc,
            DateTimeOffset validUntilUtc,
            string pathSummary,
            float pathDistanceMeters)
        {
            BotProfileId = botProfileId;
            OwnerProfileId = ownerProfileId;
            ClaimId = claimId;
            ClaimAnchor = claimAnchor;
            ValidatedBotPosition = validatedBotPosition;
            ValidatedAtUtc = validatedAtUtc;
            ValidUntilUtc = validUntilUtc;
            PathSummary = pathSummary;
            PathDistanceMeters = pathDistanceMeters;
        }

        public string BotProfileId { get; }
        public string OwnerProfileId { get; }
        public string ClaimId { get; }
        public Vector3 ClaimAnchor { get; }
        public Vector3 ValidatedBotPosition { get; }
        public DateTimeOffset ValidatedAtUtc { get; }
        public DateTimeOffset ValidUntilUtc { get; }
        public string PathSummary { get; }
        public float PathDistanceMeters { get; }
    }

    private readonly struct OwnerClaimState
    {
        public OwnerClaimState(string ownerProfileId, Vector3 position, Vector3 forward, DateTimeOffset observedAtUtc, float speed, bool stationary, DateTimeOffset stationarySinceUtc)
        {
            OwnerProfileId = ownerProfileId;
            Position = position;
            Forward = forward;
            ObservedAtUtc = observedAtUtc;
            Speed = speed;
            Stationary = stationary;
            StationarySinceUtc = stationarySinceUtc;
        }

        public string OwnerProfileId { get; }
        public Vector3 Position { get; }
        public Vector3 Forward { get; }
        public DateTimeOffset ObservedAtUtc { get; }
        public float Speed { get; }
        public bool Stationary { get; }
        public DateTimeOffset StationarySinceUtc { get; }
    }

    private struct CohesionClaimState
    {
        public string ClaimId;
        public string OwnerProfileId;
        public string OperatorId;
        public string BotProfileId;
        public string Lane;
        public string Purpose;
        public Vector3 Anchor;
        public float AnchorRadiusMeters;
        public DateTimeOffset AssignedAtUtc;
        public DateTimeOffset ValidUntilUtc;
        public bool StationaryHold;
        public bool SprintAllowed;
        public string PathSummary;
        public float PathDistanceMeters;
        public float OwnerDistance;
        public float Score;
        public bool UsesInteriorPathContract;
        public float ExecutionPathCeilingMeters;
        public float ExecutionPathRatioCeiling;

        public string Summary => "claim=" + Safe(ClaimId)
            + ";owner=" + Safe(OwnerProfileId)
            + ";operator=" + Safe(OperatorId)
            + ";botProfile=" + Safe(BotProfileId)
            + ";lane=" + Safe(Lane)
            + ";purpose=" + Safe(Purpose)
            + ";anchor=" + Anchor.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + Anchor.y.ToString("0.0", CultureInfo.InvariantCulture) + "," + Anchor.z.ToString("0.0", CultureInfo.InvariantCulture)
            + ";radius=" + AnchorRadiusMeters.ToString("0.0", CultureInfo.InvariantCulture)
            + ";stationary=" + Bool(StationaryHold)
            + ";sprintAllowed=" + Bool(SprintAllowed)
            + ";ownerDistance=" + OwnerDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";path=" + PathDistanceMeters.ToString("0.0", CultureInfo.InvariantCulture)
            + ";score=" + Score.ToString("0.0", CultureInfo.InvariantCulture)
            + ";interiorPathContract=" + Bool(UsesInteriorPathContract)
            + ";executionCeiling=" + ExecutionPathCeilingMeters.ToString("0.0", CultureInfo.InvariantCulture)
            + ";executionRatio=" + ExecutionPathRatioCeiling.ToString("0.00", CultureInfo.InvariantCulture)
            + ";validUntil=" + ValidUntilUtc.ToString("O", CultureInfo.InvariantCulture)
            + ";pathSummary=" + Safe(PathSummary);
    }

    private struct CohesionClaimLeaseState
    {
        public string LeaseId;
        public string WindowId;
        public string ClaimId;
        public string OwnerProfileId;
        public string OperatorId;
        public string BotProfileId;
        public string Lane;
        public string Purpose;
        public Vector3 Anchor;
        public float AnchorRadiusMeters;
        public DateTimeOffset StartedAtUtc;
        public DateTimeOffset MinUntilUtc;
        public DateTimeOffset MaxUntilUtc;
        public DateTimeOffset NoProgressUntilUtc;
        public DateTimeOffset LastProgressAtUtc;
        public float InitialAnchorDistance;
        public float LastAnchorDistance;
        public Vector3 LastWorldPosition;
        public DateTimeOffset LastWorldSampleAtUtc;
        public DateTimeOffset LastLivenessObservationAtUtc;
        public float ObservedBlockedSeconds;
        public float ObservedNoProgressSeconds;
        public DateTimeOffset PhysicalBlockedSinceUtc;
        public int PhysicalRestartCount;
        public Vector3 InitialOwnerPosition;
        public Vector3 LastOwnerSamplePosition;
        public DateTimeOffset LastOwnerSampleAtUtc;
        public float ObservedOwnerResumeSeconds;
        public float InitialOwnerDistance;
        public float LastOwnerDistance;
        public float PathDistanceMeters;
        public bool StationaryHold;
        public DateTimeOffset NextExternalQuiesceAtUtc;
        public DateTimeOffset NextRetargetAllowedAtUtc;
        public int RetargetCount;
        public DateTimeOffset PhysicalStackSinceUtc;
        public bool UsesInteriorPathContract;
        public float ExecutionPathCeilingMeters;
        public float ExecutionPathRatioCeiling;
        public string PlanSummary;

        public string Summary => "lease=" + Safe(LeaseId)
            + ";window=" + Safe(WindowId)
            + ";claim=" + Safe(ClaimId)
            + ";owner=" + Safe(OwnerProfileId)
            + ";operator=" + Safe(OperatorId)
            + ";botProfile=" + Safe(BotProfileId)
            + ";lane=" + Safe(Lane)
            + ";purpose=" + Safe(Purpose)
            + ";anchor=" + Anchor.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + Anchor.y.ToString("0.0", CultureInfo.InvariantCulture) + "," + Anchor.z.ToString("0.0", CultureInfo.InvariantCulture)
            + ";radius=" + AnchorRadiusMeters.ToString("0.0", CultureInfo.InvariantCulture)
            + ";stationary=" + Bool(StationaryHold)
            + ";initialAnchorDist=" + InitialAnchorDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";lastAnchorDist=" + LastAnchorDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";physicalRestarts=" + PhysicalRestartCount.ToString(CultureInfo.InvariantCulture)
            + ";blockedSince=" + PhysicalBlockedSinceUtc.ToString("O", CultureInfo.InvariantCulture)
            + ";observedBlocked=" + ObservedBlockedSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + ";observedNoProgress=" + ObservedNoProgressSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + ";ownerResumeObserved=" + ObservedOwnerResumeSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + ";initialOwnerDist=" + InitialOwnerDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";lastOwnerDist=" + LastOwnerDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";path=" + PathDistanceMeters.ToString("0.0", CultureInfo.InvariantCulture)
            + ";retargets=" + RetargetCount.ToString(CultureInfo.InvariantCulture)
            + ";nextRetargetUtc=" + NextRetargetAllowedAtUtc.ToString("O", CultureInfo.InvariantCulture)
            + ";stackSinceUtc=" + PhysicalStackSinceUtc.ToString("O", CultureInfo.InvariantCulture)
            + ";interiorPathContract=" + Bool(UsesInteriorPathContract)
            + ";executionCeiling=" + ExecutionPathCeilingMeters.ToString("0.0", CultureInfo.InvariantCulture)
            + ";executionRatio=" + ExecutionPathRatioCeiling.ToString("0.00", CultureInfo.InvariantCulture)
            + ";nextOrbitQuiesceUtc=" + NextExternalQuiesceAtUtc.ToString("O", CultureInfo.InvariantCulture)
            + ";plan=" + Safe(PlanSummary);
    }

    private readonly struct InteriorExecutionCandidate
    {
        public InteriorExecutionCandidate(string label, Vector3 anchor)
        {
            Label = label;
            Anchor = anchor;
        }

        public string Label { get; }
        public Vector3 Anchor { get; }
    }

    private readonly struct RallyExecutionCandidate
    {
        public RallyExecutionCandidate(Vector3 anchor, float desiredDistance)
        {
            Anchor = anchor;
            DesiredDistance = desiredDistance;
        }

        public Vector3 Anchor { get; }
        public float DesiredDistance { get; }
    }

    private readonly struct ClaimAnchorScore
    {
        public ClaimAnchorScore(bool valid, Vector3 anchor, float ownerDistance, float ownerPathDistance, float botPathDistance, string pathSummary, float score)
        {
            Valid = valid;
            Anchor = anchor;
            OwnerDistance = ownerDistance;
            OwnerPathDistance = ownerPathDistance;
            BotPathDistance = botPathDistance;
            PathSummary = pathSummary;
            Score = score;
        }

        public static ClaimAnchorScore Invalid(string reason) => new(false, Vector3.zero, 0f, 0f, 0f, reason, -9999f);
        public bool Valid { get; }
        public Vector3 Anchor { get; }
        public float OwnerDistance { get; }
        public float OwnerPathDistance { get; }
        public float BotPathDistance { get; }
        public string PathSummary { get; }
        public float Score { get; }
    }
}
#endif
