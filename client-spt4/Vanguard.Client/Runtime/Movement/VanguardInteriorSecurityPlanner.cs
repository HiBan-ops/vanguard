#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;

// Responsibility: Computes an executable plan for Interior Security Planner in the movement/cohesion runtime without performing the final action itself.
// Flow: Current snapshots and doctrine are reduced to a candidate plan; the owning scheduler/executor rechecks authority before any mutation.
// Authority boundary: Planning is non-authoritative for physical execution and cannot bypass final combat, medical, loot, or movement safety checks.
// Invariant: Plans stay raid-scoped, deterministic from their inputs, and safe to discard when newer evidence supersedes them.
namespace Vanguard.Client.Runtime.Movement;

/// <summary>
/// Vanguard maintains persistent indoor access topology from an owner-centered enclosure snapshot and
/// reachable NavMesh rays. It assigns one non-duplicated access axis per Operator, with a concrete
/// anchor and watch point. It never opens a movement window or overrides SAIN; the cohesion executor
/// consumes the assignment. Mobile Follow/Travel remains outside this planner.
/// </summary>
internal static class VanguardInteriorSecurityPlanner
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, InteriorSecurityAssignment> AssignmentByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, InteriorVolumeState> VolumeByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastPlanAtByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> IndoorExitPendingSinceByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> VolumeMismatchSinceByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> AssignmentRetryAfterByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, InteriorPlanningJob> PlanningJobByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PreparedInteriorPlan> PreparedPlanByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, OwnerEnvironmentCacheEntry> EnvironmentByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan AssignmentLifetime = TimeSpan.FromSeconds(90.0d);
    private static readonly TimeSpan ValidationRefresh = TimeSpan.FromSeconds(5.0d);
    private static readonly TimeSpan InvalidAssignmentReplanInterval = TimeSpan.FromSeconds(1.5d);
    private static readonly TimeSpan InvalidAssignmentRetry = TimeSpan.FromSeconds(4.0d);
    private static readonly TimeSpan IndoorExitGrace = TimeSpan.FromSeconds(2.5d);
    private static readonly TimeSpan VolumeChangeConfirmation = TimeSpan.FromSeconds(1.5d);
    private static readonly TimeSpan EnvironmentRefreshInterval = TimeSpan.FromSeconds(0.40d);
    private const float StableVolumeCellMeters = 18.0f;
    private const float VolumeHorizontalRadiusMeters = 55.0f;
    private const float VolumeVerticalToleranceMeters = 4.25f;
    private const float FloorBandChangeEnterMeters = 2.25f;
    private const float MinimumSectorSpacingMeters = 4.75f;
    private const float OperatorCapsuleRadiusMeters = 0.42f;
    private const float OperatorCapsuleHeightMeters = 1.72f;
    private const float PortalStandOffMeters = 1.85f;
    private const float PortalLateralOffsetMeters = 1.10f;
    private const float MinimumWatchVisibilityMeters = 3.50f;
    private const float TopologyProbeDistanceMeters = 14.0f;
    private const int MaxInteriorPathCalculationsPerTick = 4;
    private static readonly float[] AccessScanAngles = { 0f, -35f, 35f, -70f, 70f, -105f, 105f, 145f, -145f, 180f };
    private static readonly float[] DepthScanAngles = { -90f, 90f, 180f, -45f, 45f };
    private static readonly float[] DepthScanDistances = { 10.0f, 15.0f, 20.0f };

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            AssignmentByBot.Clear();
            VolumeByOwner.Clear();
            LastPlanAtByOwner.Clear();
            IndoorExitPendingSinceByOwner.Clear();
            VolumeMismatchSinceByOwner.Clear();
            AssignmentRetryAfterByKey.Clear();
            PlanningJobByOwner.Clear();
            PreparedPlanByOwner.Clear();
            EnvironmentByOwner.Clear();
            VanguardOwnerEnvironmentClassifier.Reset();
            LastLogAtByKey.Clear();
        }

        VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.InteriorVolumeSecurityStatusTag,
            $"VANGUARD_INTERIOR_VOLUME_RESET reason={Safe(reason)}; assignments=cleared; volumes=cleared; environmentCache=cleared; persistentByVolume=true; ownerCenteredEnvironment=true; ceilingProbe={VanguardOwnerEnvironmentClassifier.IndoorCeilingProbeMeters:0.0}; exitGrace={IndoorExitGrace.TotalSeconds:0.0}; changeConfirmation={VolumeChangeConfirmation.TotalSeconds:0.0}; Tag={VanguardPrimaryExecutionContract.OwnerCenteredEnvironmentAndStableVolumesStatusTag}; Tag={VanguardPrimaryExecutionContract.InteriorExecutableMissionStatusTag}; tag={VanguardPrimaryExecutionContract.InteriorVolumeSecurityStatusTag}; legacyCoverageTag={VanguardPrimaryExecutionContract.InteriorCoverageStatusTag}");
    }

    public static VanguardInteriorPlanningDisposition UpdateAssignments(
        string ownerProfileId,
        IReadOnlyList<OperatorDecisionSnapshot> operators,
        Vector3 ownerPosition,
        Vector3 ownerForward,
        bool ownerStillCandidate,
        bool ownerStationaryAdmitted,
        DateTimeOffset stationarySinceUtc,
        DateTimeOffset now)
    {
        string owner = Normalize(ownerProfileId);
        if (string.Equals(owner, "none", StringComparison.OrdinalIgnoreCase) || operators == null || operators.Count == 0)
        {
            return VanguardInteriorPlanningDisposition.None("missing_owner_or_operators");
        }

        var liveOperators = operators.Where(snapshot => snapshot != null && snapshot.Alive).ToArray();
        if (liveOperators.Length == 0)
        {
            ClearOwner(owner, clearVolume: true);
            return VanguardInteriorPlanningDisposition.None("no_live_operator");
        }

        double stationarySeconds = ownerStillCandidate ? Math.Max(0.0d, (now - stationarySinceUtc).TotalSeconds) : 0.0d;
        if (!ownerStillCandidate)
        {
            // The runtime remains the sole authority for admission and direct owner-resume preemption. A real
            // owner movement cancels all prepared or committed stationary missions immediately.
            ClearOwnerAssignments(owner, retainVolume: true);
            LogThrottled(owner + "|stationary_admission", now,
                $"VANGUARD_STATIONARY_TACTICAL_ADMISSION_DEFERRED owner={Safe(owner)}; ownerStillCandidate=false; ownerStationary=false; stationaryFor={stationarySeconds:0.00}; required={VanguardContinuousCohesionLocomotionPolicy.ObservationDeploymentCommitStillSeconds:0.00}; assignmentsCleared=true; preparedPlanCleared=true; volumeRetained=true; followUntouched=true; doctrine=stationary_tactical_placement_never_competes_with_mobile_follow; tag={VanguardPrimaryExecutionContract.StationarySpatialTacticalPlacementStatusTag}");
            return VanguardInteriorPlanningDisposition.None("owner_moving");
        }

        VanguardOwnerEnvironmentSnapshot environment = GetOwnerEnvironment(
            owner,
            ownerPosition,
            ownerForward,
            now,
            out VanguardOwnerEnvironmentSnapshot rawEnvironment,
            out string stabilityReason);
        LogThrottled(owner + "|owner_environment|" + environment.Signature + "|raw=" + rawEnvironment.Signature, now,
            $"VANGUARD_OWNER_ENVIRONMENT_STABILIZED owner={Safe(owner)}; rawEnclosure={rawEnvironment.Enclosure}; rawTopology={rawEnvironment.Topology}; stableEnclosure={environment.Enclosure}; stableTopology={environment.Topology}; ceilingDetected={Bool(environment.CeilingDetected)}; ceilingDistance={(environment.CeilingDetected ? environment.CeilingDistanceMeters : -1f):0.00}; lateralHits={environment.LateralHitCount}; openDirections={environment.OpenDirectionCount}; navMeshProjected={Bool(environment.NavMeshProjected)}; floorBand={environment.FloorBand}; confidence={environment.Confidence:0.00}; rawReason={Safe(rawEnvironment.Reason)}; stabilityReason={Safe(stabilityReason)}; ownerStillCandidate=true; ownerStationaryAdmitted={Bool(ownerStationaryAdmitted)}; operatorMajorityIgnored=true; followChanged=false; tag={VanguardPrimaryExecutionContract.StableEnvironmentAtomicInteriorDeploymentStatusTag}; Tag={VanguardPrimaryExecutionContract.OwnerCenteredEnvironmentAndStableVolumesStatusTag}");

        bool indoor = environment.Enclosure == VanguardOwnerEnclosure.Indoor;
        if (!indoor)
        {
            bool retainMission = false;
            DateTimeOffset pendingSince = now;
            lock (Sync)
            {
                if (VolumeByOwner.ContainsKey(owner))
                {
                    if (!IndoorExitPendingSinceByOwner.TryGetValue(owner, out pendingSince))
                    {
                        pendingSince = now;
                        IndoorExitPendingSinceByOwner[owner] = pendingSince;
                    }
                    retainMission = now - pendingSince < IndoorExitGrace;
                }
            }

            if (retainMission)
            {
                RefreshOwnerAssignments(owner, "none", now);
                LogThrottled(owner + "|indoor_exit_hysteresis", now,
                    $"VANGUARD_INTERIOR_EXIT_DEFERRED owner={Safe(owner)}; enclosure={environment.Enclosure}; rawEnclosure={rawEnvironment.Enclosure}; topology={environment.Topology}; pendingFor={(now - pendingSince).TotalSeconds:0.00}; grace={IndoorExitGrace.TotalSeconds:0.00}; assignmentsRetained=true; stabilityReason={Safe(stabilityReason)}; doctrine=temporal_consensus_and_exit_grace_prevent_probe_flicker_from_destroying_area_security; tag={VanguardPrimaryExecutionContract.StableEnvironmentAtomicInteriorDeploymentStatusTag}; Tag={VanguardPrimaryExecutionContract.OwnerCenteredEnvironmentAndStableVolumesStatusTag}");
                return VanguardInteriorPlanningDisposition.Retained("environment_exit_hysteresis");
            }

            ClearOwner(owner, clearVolume: true);
            return VanguardInteriorPlanningDisposition.None("stable_environment_not_indoor");
        }

        lock (Sync)
        {
            IndoorExitPendingSinceByOwner.Remove(owner);
        }

        Vector3 forward = environment.Forward;
        InteriorVolumeState volume;
        bool newVolume = false;
        bool deferVolumeChange = false;
        DateTimeOffset mismatchSince = now;
        string previousVolumeId = "none";
        string candidateVolumeId = "none";
        int candidateFloorBand = environment.FloorBand;
        lock (Sync)
        {
            bool hadPreviousVolume = VolumeByOwner.TryGetValue(owner, out volume);
            if (hadPreviousVolume)
            {
                previousVolumeId = volume.VolumeId;
            }

            candidateFloorBand = ResolveStableFloorBand(environment, hadPreviousVolume, volume);
            candidateVolumeId = BuildStableVolumeId(ownerPosition, candidateFloorBand);
            bool sameStableIdentity = hadPreviousVolume
                && string.Equals(candidateVolumeId, volume.VolumeId, StringComparison.OrdinalIgnoreCase);
            bool sameVolume = hadPreviousVolume
                && (sameStableIdentity || IsSameInteriorVolume(environment, candidateFloorBand, volume));
            if (hadPreviousVolume && !sameVolume)
            {
                if (!VolumeMismatchSinceByOwner.TryGetValue(owner, out mismatchSince))
                {
                    mismatchSince = now;
                    VolumeMismatchSinceByOwner[owner] = mismatchSince;
                }

                deferVolumeChange = now - mismatchSince < VolumeChangeConfirmation;
                if (deferVolumeChange)
                {
                    volume = volume.WithObservation(environment, now);
                    VolumeByOwner[owner] = volume;
                }
            }
            else
            {
                VolumeMismatchSinceByOwner.Remove(owner);
            }

            if (!deferVolumeChange && (!hadPreviousVolume || !sameVolume))
            {
                volume = new InteriorVolumeState(candidateVolumeId, ownerPosition, environment.NavMeshPosition, forward, candidateFloorBand, environment, now, now);
                VolumeByOwner[owner] = volume;
                VolumeMismatchSinceByOwner.Remove(owner);
                newVolume = true;
            }
            else if (!deferVolumeChange)
            {
                volume = volume.WithObservation(environment, now);
                VolumeByOwner[owner] = volume;
            }
        }

        if (deferVolumeChange)
        {
            RefreshOwnerAssignments(owner, previousVolumeId, now);
            LogThrottled(owner + "|volume_change_hysteresis|" + previousVolumeId, now,
                $"VANGUARD_INTERIOR_VOLUME_CHANGE_DEFERRED owner={Safe(owner)}; volume={Safe(previousVolumeId)}; candidateVolume={Safe(candidateVolumeId)}; candidateAnchor={Format(ownerPosition)}; candidateFloor={candidateFloorBand}; enclosure={environment.Enclosure}; topology={environment.Topology}; mismatchFor={(now - mismatchSince).TotalSeconds:0.00}; confirmAfter={VolumeChangeConfirmation.TotalSeconds:0.00}; assignmentsRetained=true; preparedPlanRetained=true; doctrine=stable_identity_or_navmesh_floor_signal_must_remain_changed_before_replan; Tag={VanguardPrimaryExecutionContract.OwnerCenteredEnvironmentAndStableVolumesStatusTag}; Tag={VanguardPrimaryExecutionContract.InteriorExecutableMissionStatusTag}; tag={VanguardPrimaryExecutionContract.InteriorVolumeSecurityStatusTag}");
            return VanguardInteriorPlanningDisposition.Retained("volume_change_hysteresis");
        }

        if (newVolume)
        {
            ClearOwner(owner, clearVolume: false);
            string volumeEvent = string.Equals(previousVolumeId, "none", StringComparison.OrdinalIgnoreCase)
                ? "VANGUARD_INTERIOR_VOLUME_ENTERED"
                : "VANGUARD_INTERIOR_VOLUME_CHANGED";
            VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.InteriorVolumeSecurityStatusTag,
                $"{volumeEvent} owner={Safe(owner)}; volume={Safe(volume.VolumeId)}; previousVolume={Safe(previousVolumeId)}; anchor={Format(volume.Anchor)}; navAnchor={Format(volume.AnchorNavMeshPosition)}; floorBand={volume.FloorBand}; enclosure={volume.Environment.Enclosure}; topology={volume.Environment.Topology}; ceilingDetected={Bool(volume.Environment.CeilingDetected)}; ceilingDistance={(volume.Environment.CeilingDetected ? volume.Environment.CeilingDistanceMeters : -1f):0.00}; forward={Format(volume.Forward)}; liveOperatorCount={liveOperators.Length}; preplanningAllowed=true; stationaryCommitStillRequired=true; topologyContinuity=stable_identity_plus_navmesh_path_and_floor; same_id_never_resets=true; Tag={VanguardPrimaryExecutionContract.OwnerCenteredEnvironmentAndStableVolumesStatusTag}; Tag={VanguardPrimaryExecutionContract.StableEnvironmentAtomicInteriorDeploymentStatusTag}; tag={VanguardPrimaryExecutionContract.InteriorVolumeSecurityStatusTag}");
        }

        bool combatBlocked = liveOperators.Any(snapshot => VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot)
            || VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(snapshot));
        if (combatBlocked)
        {
            RefreshOwnerAssignments(owner, volume.VolumeId, now);
            return VanguardInteriorPlanningDisposition.Blocked(volume.VolumeId, "combat_authority");
        }

        var availableOperators = liveOperators
            .Where(snapshot => !VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(snapshot))
            .ToArray();
        if (availableOperators.Length == 0)
        {
            RefreshOwnerAssignments(owner, volume.VolumeId, now);
            return VanguardInteriorPlanningDisposition.Blocked(volume.VolumeId, "no_available_operator");
        }

        string operatorSignature = BuildOperatorSignature(availableOperators);
        PreparedInteriorPlan prepared = default;
        bool hasPrepared = false;
        lock (Sync)
        {
            if (PreparedPlanByOwner.TryGetValue(owner, out PreparedInteriorPlan candidatePrepared))
            {
                if (string.Equals(candidatePrepared.VolumeId, volume.VolumeId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidatePrepared.OperatorSignature, operatorSignature, StringComparison.OrdinalIgnoreCase))
                {
                    prepared = candidatePrepared;
                    hasPrepared = true;
                }
                else
                {
                    PreparedPlanByOwner.Remove(owner);
                }
            }
        }

        if (hasPrepared)
        {
            if (!ownerStationaryAdmitted)
            {
                LogThrottled(owner + "|" + volume.VolumeId + "|prepared_waiting_commit", now,
                    $"VANGUARD_INTERIOR_PLAN_PREPARED owner={Safe(owner)}; volume={Safe(volume.VolumeId)}; assigned={prepared.Assignments.Count}; stationaryFor={stationarySeconds:0.00}; commitAfter={VanguardContinuousCohesionLocomotionPolicy.ObservationDeploymentCommitStillSeconds:0.00}; published=false; genericStationarySuppressed=false; doctrine=precompute_without_early_movement_authority; tag={VanguardPrimaryExecutionContract.StableEnvironmentAtomicInteriorDeploymentStatusTag}");
                return VanguardInteriorPlanningDisposition.PreparedPlan(volume.VolumeId, prepared.BotProfileIds, "waiting_for_stationary_admission");
            }

            CommitAssignments(owner, prepared.Assignments, now);
            lock (Sync)
            {
                PreparedPlanByOwner.Remove(owner);
            }
            LogCommittedPlan(owner, volume, prepared.Assignments, stationarySeconds, now, "prepared_plan_atomic_commit");
            return VanguardInteriorPlanningDisposition.Committed(volume.VolumeId, prepared.BotProfileIds, committedThisTick: true, "prepared_plan_committed");
        }

        bool assignmentsValid;
        InteriorSecurityAssignment[] currentAssignments;
        lock (Sync)
        {
            currentAssignments = AssignmentByBot.Values
                .Where(value => string.Equals(value.OwnerProfileId, owner, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(value.VolumeId, volume.VolumeId, StringComparison.OrdinalIgnoreCase)
                    && value.ExpiresAtUtc > now)
                .ToArray();
            assignmentsValid = ownerStationaryAdmitted
                && currentAssignments.Length == availableOperators.Length
                && currentAssignments.Select(value => value.PortalKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == currentAssignments.Length
                && HasUniquePhysicalSectorAnchors(currentAssignments)
                && currentAssignments.All(value => availableOperators.Any(bot => string.Equals(bot.BotProfileId, value.BotProfileId, StringComparison.OrdinalIgnoreCase)));

            bool planningInProgress = PlanningJobByOwner.TryGetValue(owner, out InteriorPlanningJob pendingJob)
                && string.Equals(pendingJob.VolumeId, volume.VolumeId, StringComparison.OrdinalIgnoreCase);
            if (LastPlanAtByOwner.TryGetValue(owner, out DateTimeOffset lastValidation) && !planningInProgress)
            {
                TimeSpan minimumInterval = assignmentsValid ? ValidationRefresh : InvalidAssignmentReplanInterval;
                if (now - lastValidation < minimumInterval)
                {
                    return assignmentsValid
                        ? VanguardInteriorPlanningDisposition.Committed(volume.VolumeId, currentAssignments.Select(value => value.BotProfileId), committedThisTick: false, "assignments_valid")
                        : VanguardInteriorPlanningDisposition.Planning(volume.VolumeId, ownerStationaryAdmitted, "replan_interval");
                }
            }
            if (!planningInProgress)
            {
                LastPlanAtByOwner[owner] = now;
            }
        }

        if (assignmentsValid && !newVolume)
        {
            lock (Sync)
            {
                PlanningJobByOwner.Remove(owner);
                foreach (string botId in currentAssignments.Select(value => value.BotProfileId))
                {
                    InteriorSecurityAssignment existing = AssignmentByBot[botId];
                    AssignmentByBot[botId] = existing.WithValidation(now, now + AssignmentLifetime);
                }
            }
            return VanguardInteriorPlanningDisposition.Committed(volume.VolumeId, currentAssignments.Select(value => value.BotProfileId), committedThisTick: false, "assignments_refreshed");
        }

        if (!TryAdvancePlanningJob(owner, volume, availableOperators, now, out IReadOnlyList<InteriorSecurityAssignment> assignments, out string planningSummary))
        {
            LogThrottled(owner + "|" + volume.VolumeId + "|deferred", now,
                $"VANGUARD_INTERIOR_TOPOLOGY_PLANNING_DEFERRED owner={Safe(owner)}; volume={Safe(volume.VolumeId)}; summary={Safe(planningSummary)}; pathBudgetPerTick={MaxInteriorPathCalculationsPerTick}; globalUsed={VanguardCohesionPlanningBudget.UsedPathCalculations}; globalMax={VanguardCohesionPlanningBudget.MaxPathCalculationsPerTick}; existingAssignmentsPreserved=true; preparedDuringStillness=true; genericStationarySuppressed={Bool(ownerStationaryAdmitted)}; atomicCommit=true; tag={VanguardRuntimeConvergenceStatusTags.IncrementalCohesionPlanning}; budgetTag={VanguardRuntimeConvergenceStatusTags.BoundedCohesionPathBudget}; interiorTag={VanguardPrimaryExecutionContract.StableEnvironmentAtomicInteriorDeploymentStatusTag}");
            return VanguardInteriorPlanningDisposition.Planning(volume.VolumeId, ownerStationaryAdmitted, planningSummary);
        }

        if (assignments.Count == 0)
        {
            lock (Sync)
            {
                PreparedPlanByOwner.Remove(owner);
            }
            return VanguardInteriorPlanningDisposition.None("no_executable_interior_assignment");
        }

        if (!ownerStationaryAdmitted)
        {
            var preparedPlan = new PreparedInteriorPlan(volume.VolumeId, operatorSignature, assignments, now);
            lock (Sync)
            {
                PreparedPlanByOwner[owner] = preparedPlan;
                LastPlanAtByOwner[owner] = now;
            }
            LogThrottled(owner + "|" + volume.VolumeId + "|prepared", now,
                $"VANGUARD_INTERIOR_PLAN_PREPARED owner={Safe(owner)}; volume={Safe(volume.VolumeId)}; assigned={assignments.Count}; stationaryFor={stationarySeconds:0.00}; commitAfter={VanguardContinuousCohesionLocomotionPolicy.ObservationDeploymentCommitStillSeconds:0.00}; assignments={Safe(string.Join(",", assignments.Select(value => value.BotProfileId + ":" + value.PortalKey + "@" + Format(value.Anchor))))}; planningSummary={Safe(planningSummary)}; published=false; genericStationarySuppressed=false; doctrine=precompute_without_early_movement_authority; tag={VanguardPrimaryExecutionContract.StableEnvironmentAtomicInteriorDeploymentStatusTag}");
            return VanguardInteriorPlanningDisposition.PreparedPlan(volume.VolumeId, preparedPlan.BotProfileIds, "plan_prepared_before_stationary_commit");
        }

        CommitAssignments(owner, assignments, now);
        LogCommittedPlan(owner, volume, assignments, stationarySeconds, now, planningSummary);
        return VanguardInteriorPlanningDisposition.Committed(volume.VolumeId, assignments.Select(value => value.BotProfileId), committedThisTick: true, "planned_and_committed");
    }

    private static string BuildOperatorSignature(IReadOnlyList<OperatorDecisionSnapshot> availableOperators)
    {
        return string.Join(",", availableOperators
            .Where(snapshot => snapshot != null && snapshot.Alive && !string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            .Select(snapshot => snapshot.BotProfileId)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
    }

    private static void CommitAssignments(string owner, IReadOnlyList<InteriorSecurityAssignment> assignments, DateTimeOffset now)
    {
        lock (Sync)
        {
            foreach (string stale in AssignmentByBot.Values
                .Where(value => string.Equals(value.OwnerProfileId, owner, StringComparison.OrdinalIgnoreCase))
                .Select(value => value.BotProfileId)
                .ToArray())
            {
                AssignmentByBot.Remove(stale);
            }
            foreach (InteriorSecurityAssignment assignment in assignments)
            {
                AssignmentByBot[assignment.BotProfileId] = assignment;
            }
            LastPlanAtByOwner[owner] = now;
        }
    }

    private static void LogCommittedPlan(
        string owner,
        InteriorVolumeState volume,
        IReadOnlyList<InteriorSecurityAssignment> assignments,
        double stationarySeconds,
        DateTimeOffset now,
        string planningSummary)
    {
        string signature = string.Join(",", assignments.OrderBy(value => value.BotProfileId, StringComparer.OrdinalIgnoreCase).Select(value => value.BotProfileId + ":" + value.PortalKey));
        LogThrottled(owner + "|" + volume.VolumeId + "|planned|" + signature, now,
            $"VANGUARD_INTERIOR_ASSIGNMENT_ATOMIC_COMMIT owner={Safe(owner)}; volume={Safe(volume.VolumeId)}; volumeAnchor={Format(volume.Anchor)}; floorBand={volume.FloorBand}; enclosure={volume.Environment.Enclosure}; topology={volume.Environment.Topology}; assigned={assignments.Count}; stationaryFor={stationarySeconds:0.0}; assignments={Safe(string.Join(",", assignments.Select(value => value.BotProfileId + ":" + value.PortalKey + "@" + Format(value.Anchor))))}; planningSummary={Safe(planningSummary)}; oldGenericClaimsMustYield=true; collectiveSpacingAuthority=committed_assignment_batch; doctrine=all_interior_reservations_publish_together_after_stationary_admission; tag={VanguardPrimaryExecutionContract.StableEnvironmentAtomicInteriorDeploymentStatusTag}; Tag={VanguardPrimaryExecutionContract.InteriorAreaSecurityStatusTag}; legacyCoverageTag={VanguardPrimaryExecutionContract.InteriorCoverageStatusTag}");
    }

    private static bool TryAdvancePlanningJob(
        string owner,
        InteriorVolumeState volume,
        IReadOnlyList<OperatorDecisionSnapshot> availableOperators,
        DateTimeOffset now,
        out IReadOnlyList<InteriorSecurityAssignment> assignments,
        out string summary)
    {
        assignments = Array.Empty<InteriorSecurityAssignment>();
        string operatorSignature = string.Join(",", availableOperators
            .Where(snapshot => snapshot != null && snapshot.Alive && !string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            .Select(snapshot => snapshot.BotProfileId)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(operatorSignature))
        {
            summary = "no_available_operator";
            return true;
        }

        InteriorPlanningJob job;
        lock (Sync)
        {
            bool reuse = PlanningJobByOwner.TryGetValue(owner, out var existingJob)
                && string.Equals(existingJob.VolumeId, volume.VolumeId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existingJob.OperatorSignature, operatorSignature, StringComparison.OrdinalIgnoreCase);
            if (reuse)
            {
                job = existingJob;
            }
            else
            {
                var previousBotByPortal = AssignmentByBot.Values
                    .Where(value => string.Equals(value.OwnerProfileId, owner, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(value => value.PortalKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().BotProfileId, StringComparer.OrdinalIgnoreCase);
                job = new InteriorPlanningJob(owner, volume, operatorSignature, previousBotByPortal, now);
                PlanningJobByOwner[owner] = job;
            }
        }

        var operatorById = availableOperators
            .Where(snapshot => snapshot != null && snapshot.Alive && !string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            .ToDictionary(snapshot => snapshot.BotProfileId, snapshot => snapshot, StringComparer.OrdinalIgnoreCase);
        int localPathStart = VanguardCohesionPlanningBudget.UsedPathCalculations;

        while (VanguardCohesionPlanningBudget.UsedPathCalculations - localPathStart < MaxInteriorPathCalculationsPerTick)
        {
            if (job.AccessAngleIndex < AccessScanAngles.Length)
            {
                if (VanguardCohesionPlanningBudget.UsedPathCalculations - localPathStart + 2 > MaxInteriorPathCalculationsPerTick
                    || !VanguardCohesionPlanningBudget.CanStartCandidate(2))
                {
                    break;
                }

                TryBuildAccessCandidateForAngle(job.Volume.Anchor, job.Volume.Forward, AccessScanAngles[job.AccessAngleIndex], job.AccessCandidates);
                job.AccessAngleIndex++;
                continue;
            }

            if (job.DepthAngleIndex < DepthScanAngles.Length)
            {
                if (VanguardCohesionPlanningBudget.UsedPathCalculations - localPathStart + 1 > MaxInteriorPathCalculationsPerTick
                    || !VanguardCohesionPlanningBudget.CanStartCandidate(1))
                {
                    break;
                }

                bool accepted = TryBuildDepthCandidate(
                    job.Volume.Anchor,
                    job.Volume.Forward,
                    DepthScanAngles[job.DepthAngleIndex],
                    DepthScanDistances[job.DepthDistanceIndex],
                    job.DepthCandidates,
                    job.DepthCandidates.Count);
                job.DepthDistanceIndex++;
                if (accepted || job.DepthDistanceIndex >= DepthScanDistances.Length)
                {
                    job.DepthAngleIndex++;
                    job.DepthDistanceIndex = 0;
                }
                continue;
            }

            if (job.OrderedCandidates == null)
            {
                job.OrderedCandidates = BuildBalancedSectorOrder(
                    job.AccessCandidates.OrderByDescending(candidate => candidate.Score).ToList(),
                    job.DepthCandidates.OrderByDescending(candidate => candidate.Score).ToList(),
                    operatorById.Count).ToList();
                job.UnassignedBotIds = operatorById.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
            }

            if (job.UnassignedBotIds.Count == 0 || job.AssignmentCandidateIndex >= job.OrderedCandidates.Count)
            {
                assignments = job.Assignments.ToArray();
                summary = job.BuildSummary("complete");
                lock (Sync)
                {
                    PlanningJobByOwner.Remove(owner);
                }
                return true;
            }

            AccessCandidate candidate = job.OrderedCandidates[job.AssignmentCandidateIndex];
            if (!string.Equals(job.EvaluatingPortalKey, candidate.PortalKey, StringComparison.OrdinalIgnoreCase))
            {
                job.BeginCandidate(candidate, operatorById);
            }

            if (job.AssignmentBotIndex < job.EvaluationBotIds.Count)
            {
                if (VanguardCohesionPlanningBudget.UsedPathCalculations - localPathStart + 1 > MaxInteriorPathCalculationsPerTick
                    || !VanguardCohesionPlanningBudget.CanStartCandidate(1))
                {
                    break;
                }

                string botId = job.EvaluationBotIds[job.AssignmentBotIndex++];
                if (!operatorById.TryGetValue(botId, out var bot)
                    || !job.UnassignedBotIds.Contains(botId, StringComparer.OrdinalIgnoreCase)
                    || IsAssignmentRetryBlocked(bot.BotProfileId, volume.VolumeId, candidate.PortalKey, now))
                {
                    continue;
                }

                if (TryPathDistance(bot.Position, candidate.Anchor, out float pathDistance))
                {
                    bool preserved = job.PreviousBotByPortal.TryGetValue(candidate.PortalKey, out string previousBotId)
                        && string.Equals(previousBotId, botId, StringComparison.OrdinalIgnoreCase);
                    if (preserved || pathDistance < job.CurrentBestPath)
                    {
                        job.CurrentBestBotId = botId;
                        job.CurrentBestPath = pathDistance;
                    }
                    if (preserved)
                    {
                        job.AssignmentBotIndex = job.EvaluationBotIds.Count;
                    }
                }
                continue;
            }

            job.CommitCurrentCandidate(operatorById, now);
        }

        VanguardCohesionPlanningBudget.MarkDeferred();
        summary = job.BuildSummary("deferred");
        return false;
    }

    private static void TryBuildAccessCandidateForAngle(Vector3 owner, Vector3 forward, float angle, List<AccessCandidate> result)
    {
        Vector3 direction = Flatten(Quaternion.Euler(0f, angle, 0f) * forward);
        if (direction.sqrMagnitude <= 0.001f)
        {
            return;
        }
        direction.Normalize();

        Vector3 farRaw = owner + direction * 14.0f;
        if (!TrySampleExact(farRaw, 3.0f, 1.25f, out var farPoint)
            || !TryPath(owner, farPoint, out var pathDistance, out var corners))
        {
            return;
        }

        float direct = HorizontalDistance(owner, farPoint);
        if (direct < 8.0f || pathDistance > 26.0f || pathDistance / Math.Max(0.5f, direct) > 1.90f
            || !TryFindAccessThroat(owner, direction, out var throatCenter, out var throatHalfWidth))
        {
            return;
        }

        float ownerHalfWidth = EstimateReachableHalfWidth(owner + direction * 1.5f, direction);
        float farHalfWidth = EstimateReachableHalfWidth(farPoint - direction * 1.5f, direction);
        bool narrowAccess = throatHalfWidth <= 1.65f;
        bool widthTransition = ownerHalfWidth >= throatHalfWidth + 0.65f || farHalfWidth >= throatHalfWidth + 0.65f;
        bool cornerDefinedAccess = corners >= 3 && throatHalfWidth <= 2.25f;
        if (!narrowAccess && !widthTransition && !cornerDefinedAccess)
        {
            return;
        }

        Vector3 lateral = new Vector3(direction.z, 0f, -direction.x).normalized;
        Vector3 baseAnchor = throatCenter - direction * PortalStandOffMeters;
        Vector3 watch = farPoint + Vector3.up * 1.25f;
        if (!TrySelectPortalGuardAnchor(owner, baseAnchor, lateral, watch, out var anchor, out float physicalScore, out string side))
        {
            return;
        }

        if (!TryPath(owner, anchor, out var anchorPath, out _)
            || anchorPath > 15.0f)
        {
            return;
        }

        Vector3 watchDirection = Flatten(watch - owner);
        if (result.Any(existing => Vector3.Angle(Flatten(existing.WatchPoint - owner), watchDirection) < 24f
            || HorizontalDistance(existing.Anchor, anchor) < MinimumSectorSpacingMeters))
        {
            return;
        }

        string portalKey = "nav_access_" + AngleKey(angle) + "_" + side;
        float transitionBonus = widthTransition ? 24.0f : 0.0f;
        float narrowBonus = Math.Max(0f, 2.50f - throatHalfWidth) * 22.0f;
        float accessImportance = Math.Min(18.0f, direct) + Math.Min(5, corners) * 4.0f;
        float score = transitionBonus + narrowBonus + accessImportance + physicalScore - Math.Abs(angle) * 0.0125f;
        result.Add(new AccessCandidate(portalKey, anchor, watch, score, throatHalfWidth, ownerHalfWidth, farHalfWidth, angle, corners));
    }

    private static bool TryBuildDepthCandidate(Vector3 volumeAnchor, Vector3 forward, float angle, float distance, List<AccessCandidate> result, int index)
    {
        Vector3 direction = Flatten(Quaternion.Euler(0f, angle, 0f) * forward);
        if (direction.sqrMagnitude <= 0.001f)
        {
            return false;
        }
        direction.Normalize();
        Vector3 raw = volumeAnchor + direction * distance;
        if (!TrySampleExact(raw, 2.25f, 1.40f, out var anchor)
            || !TryPath(volumeAnchor, anchor, out float pathDistance, out int corners)
            || pathDistance > 36.0f
            || result.Any(existing => HorizontalDistance(existing.Anchor, anchor) < MinimumSectorSpacingMeters))
        {
            return false;
        }

        Vector3 watch = anchor + direction * 9.0f + Vector3.up * 1.25f;
        if (!TryEvaluatePhysicalSlot(anchor, watch, out float physicalScore, out _))
        {
            return false;
        }
        float score = 38.0f + Math.Min(12.0f, pathDistance) + physicalScore - Math.Abs(angle) * 0.01f - index * 0.15f;
        result.Add(new AccessCandidate("depth_" + AngleKey(angle) + "_" + distance.ToString("0", CultureInfo.InvariantCulture), anchor, watch, score, 9.0f, 9.0f, 9.0f, angle, corners));
        return true;
    }

    public static bool TryGetAssignment(string? botProfileId, DateTimeOffset now, out InteriorSecurityAssignment assignment)
    {
        string bot = Normalize(botProfileId);
        lock (Sync)
        {
            if (AssignmentByBot.TryGetValue(bot, out assignment))
            {
                if (assignment.ExpiresAtUtc > now)
                {
                    return true;
                }
                AssignmentByBot.Remove(bot);
            }
        }
        assignment = default;
        return false;
    }

    public static bool InvalidateAssignment(string? botProfileId, DateTimeOffset now, string reason)
    {
        string bot = Normalize(botProfileId);
        InteriorSecurityAssignment removed = default;
        bool hadAssignment = false;
        lock (Sync)
        {
            if (AssignmentByBot.TryGetValue(bot, out removed))
            {
                hadAssignment = true;
                AssignmentByBot.Remove(bot);
                // Quarantine this exact bot/sector pair briefly, then let the planner search for a
                // different executable sector at a bounded cadence. The volume remains stable and
                // generic cohesion remains available during the retry interval.
                AssignmentRetryAfterByKey[BuildAssignmentRetryKey(bot, removed.VolumeId, removed.PortalKey)] = now + InvalidAssignmentRetry;
                LastPlanAtByOwner[removed.OwnerProfileId] = now;
            }
        }

        if (hadAssignment)
        {
            LogThrottled(bot + "|assignment_invalidated|" + Safe(reason), now,
                $"VANGUARD_INTERIOR_ASSIGNMENT_INVALIDATED owner={Safe(removed.OwnerProfileId)}; botProfile={Safe(bot)}; volume={Safe(removed.VolumeId)}; sector={Safe(removed.PortalKey)}; reason={Safe(reason)}; genericFallbackRequired=true; volumeRetained=true; doctrine=interior_mission_is_authoritative_only_while_its_claim_is_executable; Tag={VanguardPrimaryExecutionContract.InteriorExecutableMissionStatusTag}; tag={VanguardPrimaryExecutionContract.InteriorVolumeSecurityStatusTag}");
        }

        return hadAssignment;
    }

    public static bool IsCollectiveAssignmentSpacingValid(
        string? ownerProfileId,
        string? botProfileId,
        Vector3 candidateAnchor,
        float minimumSpacingMeters,
        DateTimeOffset now,
        out string reason)
    {
        string owner = Normalize(ownerProfileId);
        string bot = Normalize(botProfileId);
        lock (Sync)
        {
            if (!AssignmentByBot.TryGetValue(bot, out InteriorSecurityAssignment ownAssignment)
                || ownAssignment.ExpiresAtUtc <= now
                || !string.Equals(ownAssignment.OwnerProfileId, owner, StringComparison.OrdinalIgnoreCase))
            {
                reason = "missing_committed_assignment";
                return false;
            }

            foreach (InteriorSecurityAssignment other in AssignmentByBot.Values)
            {
                if (other.ExpiresAtUtc <= now
                    || !string.Equals(other.OwnerProfileId, owner, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(other.VolumeId, ownAssignment.VolumeId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(other.BotProfileId, bot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                float distance = HorizontalDistance(candidateAnchor, other.Anchor);
                if (distance < minimumSpacingMeters)
                {
                    reason = Safe(other.BotProfileId) + "_assigned_anchor_distance_"
                        + distance.ToString("0.00", CultureInfo.InvariantCulture)
                        + "_min_" + minimumSpacingMeters.ToString("0.00", CultureInfo.InvariantCulture);
                    return false;
                }
            }
        }

        reason = "committed_batch_spacing_ok";
        return true;
    }

    public static bool IsVerifiedCoverageHold(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (!TryGetAssignment(snapshot.BotProfileId, now, out InteriorSecurityAssignment assignment))
        {
            reason = "no_live_interior_assignment";
            return false;
        }
        float distance = HorizontalDistance(snapshot.Position, assignment.Anchor);
        if (distance > 3.5f)
        {
            reason = "not_at_coverage_anchor:" + distance.ToString("0.0", CultureInfo.InvariantCulture);
            return false;
        }
        reason = "verified_volume=" + Safe(assignment.VolumeId) + ":portal=" + Safe(assignment.PortalKey) + ":anchorDistance=" + distance.ToString("0.0", CultureInfo.InvariantCulture);
        return true;
    }


    private static VanguardOwnerEnvironmentSnapshot GetOwnerEnvironment(
        string owner,
        Vector3 ownerPosition,
        Vector3 ownerForward,
        DateTimeOffset now,
        out VanguardOwnerEnvironmentSnapshot rawSnapshot,
        out string stabilityReason)
    {
        lock (Sync)
        {
            if (EnvironmentByOwner.TryGetValue(owner, out OwnerEnvironmentCacheEntry cached)
                && now - cached.SampledAtUtc < EnvironmentRefreshInterval)
            {
                rawSnapshot = cached.RawSnapshot;
                stabilityReason = cached.StabilityReason;
                return cached.Snapshot;
            }
        }

        VanguardOwnerEnvironmentSnapshot snapshot = VanguardOwnerEnvironmentClassifier.ClassifyStable(
            owner,
            ownerPosition,
            ownerForward,
            now,
            out rawSnapshot,
            out stabilityReason);
        lock (Sync)
        {
            EnvironmentByOwner[owner] = new OwnerEnvironmentCacheEntry(snapshot, rawSnapshot, stabilityReason, now);
        }
        return snapshot;
    }

    private static int ResolveStableFloorBand(
        VanguardOwnerEnvironmentSnapshot environment,
        bool hadPreviousVolume,
        InteriorVolumeState previousVolume)
    {
        if (!hadPreviousVolume || environment.FloorBand == previousVolume.FloorBand)
        {
            return environment.FloorBand;
        }

        float verticalDelta = Math.Abs(environment.NavMeshPosition.y - previousVolume.AnchorNavMeshPosition.y);
        return verticalDelta >= FloorBandChangeEnterMeters
            ? environment.FloorBand
            : previousVolume.FloorBand;
    }

    private static string BuildStableVolumeId(Vector3 anchor, int floorBand)
    {
        int cellX = (int)Math.Floor(anchor.x / StableVolumeCellMeters);
        int cellZ = (int)Math.Floor(anchor.z / StableVolumeCellMeters);
        return "interior_cell_" + cellX.ToString(CultureInfo.InvariantCulture)
            + "_floor_" + floorBand.ToString(CultureInfo.InvariantCulture)
            + "_" + cellZ.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsSameInteriorVolume(
        VanguardOwnerEnvironmentSnapshot environment,
        int candidateFloorBand,
        InteriorVolumeState volume)
    {
        if (candidateFloorBand != volume.FloorBand
            || HorizontalDistance(environment.NavMeshPosition, volume.AnchorNavMeshPosition) > VolumeHorizontalRadiusMeters
            || Math.Abs(environment.NavMeshPosition.y - volume.AnchorNavMeshPosition.y) > VolumeVerticalToleranceMeters)
        {
            return false;
        }

        if (!VanguardCohesionPlanningBudget.CanStartCandidate(1))
        {
            VanguardCohesionPlanningBudget.MarkDeferred();
            // Budget pressure is not evidence of a volume transition. Preserve the current volume
            // and retry its topology check on the next owner-fair planning tick.
            return true;
        }

        if (!TryPath(volume.AnchorNavMeshPosition, environment.NavMeshPosition, out float pathDistance, out _))
        {
            return false;
        }

        float direct = Math.Max(0.5f, HorizontalDistance(volume.AnchorNavMeshPosition, environment.NavMeshPosition));
        return pathDistance <= VolumeHorizontalRadiusMeters * 1.75f
            && pathDistance / direct <= 4.50f;
    }


    private static bool HasUniquePhysicalSectorAnchors(IReadOnlyList<InteriorSecurityAssignment> assignments)
    {
        for (int i = 0; i < assignments.Count; i++)
        {
            for (int j = i + 1; j < assignments.Count; j++)
            {
                if (HorizontalDistance(assignments[i].Anchor, assignments[j].Anchor) < MinimumSectorSpacingMeters)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static List<AccessCandidate> BuildBalancedSectorOrder(
        IReadOnlyList<AccessCandidate> accessCandidates,
        IReadOnlyList<AccessCandidate> depthCandidates,
        int guardCount)
    {
        var ordered = new List<AccessCandidate>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(AccessCandidate candidate)
        {
            if (used.Contains(candidate.PortalKey)
                || ordered.Any(existing => HorizontalDistance(existing.Anchor, candidate.Anchor) < MinimumSectorSpacingMeters))
            {
                return;
            }

            used.Add(candidate.PortalKey);
            ordered.Add(candidate);
        }

        if (guardCount >= 3 && depthCandidates.Count > 0)
        {
            foreach (AccessCandidate access in accessCandidates.Take(2))
            {
                Add(access);
            }
            Add(depthCandidates[0]);
        }

        foreach (AccessCandidate candidate in accessCandidates.Concat(depthCandidates).OrderByDescending(value => value.Score))
        {
            Add(candidate);
        }

        return ordered;
    }

    private static bool TrySelectPortalGuardAnchor(
        Vector3 owner,
        Vector3 baseAnchor,
        Vector3 lateral,
        Vector3 watch,
        out Vector3 selected,
        out float selectedScore,
        out string side)
    {
        selected = default;
        selectedScore = float.MinValue;
        side = "none";
        var rawCandidates = new[]
        {
            new PortalAnchorProbe(baseAnchor + lateral * PortalLateralOffsetMeters, "left"),
            new PortalAnchorProbe(baseAnchor - lateral * PortalLateralOffsetMeters, "right"),
            new PortalAnchorProbe(baseAnchor, "center")
        };

        foreach (PortalAnchorProbe probe in rawCandidates)
        {
            if (!TrySampleExact(probe.Position, 1.25f, 0.80f, out Vector3 sampled)
                || !TryEvaluatePhysicalSlot(sampled, watch, out float physicalScore, out _))
            {
                continue;
            }

            float ownerDistance = HorizontalDistance(owner, sampled);
            float passagePenalty = string.Equals(probe.Side, "center", StringComparison.OrdinalIgnoreCase) ? 18.0f : 0.0f;
            float score = physicalScore - passagePenalty - Math.Max(0f, ownerDistance - 14.0f) * 1.5f;
            if (score > selectedScore)
            {
                selected = sampled;
                selectedScore = score;
                side = probe.Side;
            }
        }

        return selectedScore > float.MinValue;
    }

    private static bool TryEvaluatePhysicalSlot(Vector3 anchor, Vector3 watchPoint, out float score, out string summary)
    {
        score = 0f;
        summary = "none";
        Vector3 capsuleBottom = anchor + Vector3.up * OperatorCapsuleRadiusMeters;
        Vector3 capsuleTop = anchor + Vector3.up * Math.Max(OperatorCapsuleRadiusMeters, OperatorCapsuleHeightMeters - OperatorCapsuleRadiusMeters);
        if (Physics.CheckCapsule(capsuleBottom, capsuleTop, OperatorCapsuleRadiusMeters, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            summary = "capsule_blocked";
            return false;
        }

        Vector3 eye = anchor + Vector3.up * 1.45f;
        Vector3 toWatch = watchPoint - eye;
        float watchDistance = toWatch.magnitude;
        if (watchDistance < MinimumWatchVisibilityMeters)
        {
            summary = "watch_too_close";
            return false;
        }

        float visibleDistance = watchDistance;
        if (Physics.Raycast(eye, toWatch.normalized, out RaycastHit watchHit, watchDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            visibleDistance = watchHit.distance;
        }
        if (visibleDistance < MinimumWatchVisibilityMeters)
        {
            summary = "watch_occluded";
            return false;
        }

        Vector3 watchDirection = Flatten(toWatch).normalized;
        if (watchDirection.sqrMagnitude <= 0.001f)
        {
            watchDirection = Vector3.forward;
        }
        Vector3 rearDirection = -watchDirection;
        bool rearCover = Physics.Raycast(anchor + Vector3.up * 1.05f, rearDirection, out RaycastHit rearHit, 2.25f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float rearCoverScore = rearCover ? Math.Max(0f, 2.25f - rearHit.distance) * 5.0f + 8.0f : 0f;
        float visibilityScore = Math.Min(18.0f, visibleDistance) * 0.75f;
        score = rearCoverScore + visibilityScore;
        summary = "clear:visible=" + visibleDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ":rearCover=" + Bool(rearCover);
        return true;
    }

    private static bool TryFindAccessThroat(Vector3 owner, Vector3 direction, out Vector3 center, out float halfWidth)
    {
        center = Vector3.zero;
        halfWidth = float.MaxValue;
        float[] distances = { 4.5f, 6.5f, 8.5f, 10.5f };
        foreach (float distance in distances)
        {
            Vector3 raw = owner + direction * distance;
            if (!TrySampleExact(raw, 1.50f, 0.80f, out var sampled))
            {
                continue;
            }

            float width = EstimateReachableHalfWidth(sampled, direction);
            if (width < halfWidth)
            {
                halfWidth = width;
                center = sampled;
            }
        }

        return halfWidth < float.MaxValue && halfWidth >= 0.70f;
    }

    private static float EstimateReachableHalfWidth(Vector3 center, Vector3 direction)
    {
        Vector3 right = new(direction.z, 0f, -direction.x);
        if (right.sqrMagnitude <= 0.001f)
        {
            right = Vector3.right;
        }
        right.Normalize();

        float reachable = 0f;
        float[] offsets = { 0.75f, 1.25f, 1.75f, 2.25f, 2.75f, 3.25f };
        foreach (float offset in offsets)
        {
            if (!TrySampleExact(center - right * offset, 0.65f, 0.55f, out var left)
                || !TrySampleExact(center + right * offset, 0.65f, 0.55f, out var rightPoint)
                || NavMesh.Raycast(center, left, out _, NavMesh.AllAreas)
                || NavMesh.Raycast(center, rightPoint, out _, NavMesh.AllAreas))
            {
                break;
            }
            reachable = offset;
        }
        return reachable;
    }

    private static bool TrySampleExact(Vector3 raw, float radius, float tolerance, out Vector3 sampled)
    {
        sampled = default;
        if (!NavMesh.SamplePosition(raw + Vector3.up * 0.20f, out var hit, radius, NavMesh.AllAreas))
        {
            return false;
        }

        if (HorizontalDistance(raw, hit.position) > tolerance)
        {
            return false;
        }

        sampled = hit.position;
        return true;
    }

    private static string AngleKey(float angle)
    {
        int normalized = (int)Math.Round(angle, MidpointRounding.AwayFromZero);
        return normalized < 0
            ? "m" + Math.Abs(normalized).ToString(CultureInfo.InvariantCulture)
            : "p" + normalized.ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryPath(Vector3 start, Vector3 end, out float distance, out int corners)
    {
        distance = 0f;
        corners = 0;
        if (!VanguardCohesionPlanningBudget.TryConsumePathCalculation(out _))
        {
            return false;
        }

        var path = new NavMeshPath();
        if (!NavMesh.CalculatePath(start, end, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete || path.corners == null || path.corners.Length < 2)
        {
            return false;
        }

        corners = path.corners.Length;
        for (int i = 1; i < path.corners.Length; i++)
        {
            distance += Vector3.Distance(path.corners[i - 1], path.corners[i]);
        }
        return distance > 0.1f;
    }

    private static bool TryPathDistance(Vector3 start, Vector3 end, out float distance)
    {
        return TryPath(start, end, out distance, out _);
    }

    private static bool IsAssignmentRetryBlocked(string? botProfileId, string? volumeId, string? portalKey, DateTimeOffset now)
    {
        string key = BuildAssignmentRetryKey(botProfileId, volumeId, portalKey);
        lock (Sync)
        {
            if (AssignmentRetryAfterByKey.TryGetValue(key, out DateTimeOffset retryAfter))
            {
                if (retryAfter > now)
                {
                    return true;
                }
                AssignmentRetryAfterByKey.Remove(key);
            }
        }
        return false;
    }

    private static string BuildAssignmentRetryKey(string? botProfileId, string? volumeId, string? portalKey)
    {
        return Normalize(botProfileId) + "|" + Normalize(volumeId) + "|" + Normalize(portalKey);
    }

    private static void RefreshOwnerAssignments(string owner, string volumeId, DateTimeOffset now)
    {
        lock (Sync)
        {
            foreach (string botId in AssignmentByBot.Values
                .Where(value => string.Equals(value.OwnerProfileId, owner, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(volumeId, "none", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(value.VolumeId, volumeId, StringComparison.OrdinalIgnoreCase)))
                .Select(value => value.BotProfileId)
                .ToArray())
            {
                InteriorSecurityAssignment current = AssignmentByBot[botId];
                AssignmentByBot[botId] = current.WithValidation(now, now + AssignmentLifetime);
            }
        }
    }

    private static void ClearOwnerAssignments(string owner, bool retainVolume)
    {
        lock (Sync)
        {
            foreach (string bot in AssignmentByBot.Values
                .Where(value => string.Equals(value.OwnerProfileId, owner, StringComparison.OrdinalIgnoreCase))
                .Select(value => value.BotProfileId)
                .ToArray())
            {
                AssignmentByBot.Remove(bot);
            }
            PlanningJobByOwner.Remove(owner);
            PreparedPlanByOwner.Remove(owner);
            if (!retainVolume)
            {
                VolumeByOwner.Remove(owner);
            }
        }
    }

    private static void ClearOwner(string owner, bool clearVolume)
    {
        lock (Sync)
        {
            string[] ownerBots = AssignmentByBot.Values
                .Where(value => string.Equals(value.OwnerProfileId, owner, StringComparison.OrdinalIgnoreCase))
                .Select(value => value.BotProfileId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (string bot in ownerBots)
            {
                AssignmentByBot.Remove(bot);
                foreach (string retryKey in AssignmentRetryAfterByKey.Keys
                    .Where(key => key.StartsWith(Normalize(bot) + "|", StringComparison.OrdinalIgnoreCase))
                    .ToArray())
                {
                    AssignmentRetryAfterByKey.Remove(retryKey);
                }
            }
            PlanningJobByOwner.Remove(owner);
            PreparedPlanByOwner.Remove(owner);
            if (clearVolume)
            {
                VolumeByOwner.Remove(owner);
                LastPlanAtByOwner.Remove(owner);
                IndoorExitPendingSinceByOwner.Remove(owner);
                VolumeMismatchSinceByOwner.Remove(owner);
                EnvironmentByOwner.Remove(owner);
            }
        }
    }

    private static readonly Dictionary<string, DateTimeOffset> LastLogAtByKey = new(StringComparer.OrdinalIgnoreCase);
    private static void LogThrottled(string key, DateTimeOffset now, string message)
    {
        lock (Sync)
        {
            if (LastLogAtByKey.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(5))
            {
                return;
            }
            LastLogAtByKey[key] = now;
        }
        VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.InteriorVolumeSecurityStatusTag, message);
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static Vector3 Flatten(Vector3 value) => new(value.x, 0f, value.z);
    private static float HorizontalDistance(Vector3 a, Vector3 b) => Flatten(a - b).magnitude;
    private static string Format(Vector3 value) => value.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + value.y.ToString("0.0", CultureInfo.InvariantCulture) + "," + value.z.ToString("0.0", CultureInfo.InvariantCulture);
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private readonly struct OwnerEnvironmentCacheEntry
    {
        public OwnerEnvironmentCacheEntry(
            VanguardOwnerEnvironmentSnapshot snapshot,
            VanguardOwnerEnvironmentSnapshot rawSnapshot,
            string stabilityReason,
            DateTimeOffset sampledAtUtc)
        {
            Snapshot = snapshot;
            RawSnapshot = rawSnapshot;
            StabilityReason = stabilityReason;
            SampledAtUtc = sampledAtUtc;
        }

        public VanguardOwnerEnvironmentSnapshot Snapshot { get; }
        public VanguardOwnerEnvironmentSnapshot RawSnapshot { get; }
        public string StabilityReason { get; }
        public DateTimeOffset SampledAtUtc { get; }
    }

    private readonly struct PreparedInteriorPlan
    {
        public PreparedInteriorPlan(
            string volumeId,
            string operatorSignature,
            IReadOnlyList<InteriorSecurityAssignment> assignments,
            DateTimeOffset preparedAtUtc)
        {
            VolumeId = volumeId;
            OperatorSignature = operatorSignature;
            Assignments = assignments.ToArray();
            BotProfileIds = Assignments.Select(value => value.BotProfileId).ToArray();
            PreparedAtUtc = preparedAtUtc;
        }

        public string VolumeId { get; }
        public string OperatorSignature { get; }
        public IReadOnlyList<InteriorSecurityAssignment> Assignments { get; }
        public IReadOnlyList<string> BotProfileIds { get; }
        public DateTimeOffset PreparedAtUtc { get; }
    }

    private sealed class InteriorPlanningJob
    {
        public InteriorPlanningJob(
            string ownerProfileId,
            InteriorVolumeState volume,
            string operatorSignature,
            Dictionary<string, string> previousBotByPortal,
            DateTimeOffset startedAtUtc)
        {
            OwnerProfileId = ownerProfileId;
            Volume = volume;
            VolumeId = volume.VolumeId;
            OperatorSignature = operatorSignature;
            PreviousBotByPortal = previousBotByPortal;
            StartedAtUtc = startedAtUtc;
        }

        public string OwnerProfileId { get; }
        public InteriorVolumeState Volume { get; }
        public string VolumeId { get; }
        public string OperatorSignature { get; }
        public Dictionary<string, string> PreviousBotByPortal { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public int AccessAngleIndex { get; set; }
        public int DepthAngleIndex { get; set; }
        public int DepthDistanceIndex { get; set; }
        public List<AccessCandidate> AccessCandidates { get; } = new();
        public List<AccessCandidate> DepthCandidates { get; } = new();
        public List<AccessCandidate>? OrderedCandidates { get; set; }
        public List<string> UnassignedBotIds { get; set; } = new();
        public int AssignmentCandidateIndex { get; set; }
        public string EvaluatingPortalKey { get; private set; } = string.Empty;
        public List<string> EvaluationBotIds { get; private set; } = new();
        public int AssignmentBotIndex { get; set; }
        public string CurrentBestBotId { get; set; } = string.Empty;
        public float CurrentBestPath { get; set; } = float.MaxValue;
        public List<InteriorSecurityAssignment> Assignments { get; } = new();

        public void BeginCandidate(AccessCandidate candidate, IReadOnlyDictionary<string, OperatorDecisionSnapshot> operatorById)
        {
            EvaluatingPortalKey = candidate.PortalKey;
            AssignmentBotIndex = 0;
            CurrentBestBotId = string.Empty;
            CurrentBestPath = float.MaxValue;
            EvaluationBotIds = UnassignedBotIds
                .Where(operatorById.ContainsKey)
                .OrderBy(botId => PreviousBotByPortal.TryGetValue(candidate.PortalKey, out string previousBotId)
                    && string.Equals(previousBotId, botId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(botId => botId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public void CommitCurrentCandidate(IReadOnlyDictionary<string, OperatorDecisionSnapshot> operatorById, DateTimeOffset now)
        {
            if (OrderedCandidates == null || AssignmentCandidateIndex >= OrderedCandidates.Count)
            {
                return;
            }

            AccessCandidate candidate = OrderedCandidates[AssignmentCandidateIndex];
            if (!string.IsNullOrWhiteSpace(CurrentBestBotId) && operatorById.TryGetValue(CurrentBestBotId, out var bestBot))
            {
                int portalIndex = Assignments.Count + 1;
                Assignments.Add(new InteriorSecurityAssignment(
                    OwnerProfileId,
                    VolumeId,
                    bestBot.OperatorId,
                    bestBot.BotProfileId,
                    candidate.PortalKey,
                    "interior_sector_" + portalIndex.ToString(CultureInfo.InvariantCulture),
                    candidate.PortalKey.StartsWith("depth_", StringComparison.OrdinalIgnoreCase) ? "persistent_volume_depth_security" : "persistent_volume_access_guard",
                    candidate.Anchor,
                    candidate.WatchPoint,
                    candidate.Score,
                    CurrentBestPath,
                    now,
                    now,
                    now + AssignmentLifetime));
                UnassignedBotIds.RemoveAll(value => string.Equals(value, CurrentBestBotId, StringComparison.OrdinalIgnoreCase));
            }

            AssignmentCandidateIndex++;
            EvaluatingPortalKey = string.Empty;
            EvaluationBotIds.Clear();
            AssignmentBotIndex = 0;
            CurrentBestBotId = string.Empty;
            CurrentBestPath = float.MaxValue;
        }

        public string BuildSummary(string state)
        {
            return "state=" + Safe(state)
                + ";accessIndex=" + AccessAngleIndex.ToString(CultureInfo.InvariantCulture) + "/" + AccessScanAngles.Length.ToString(CultureInfo.InvariantCulture)
                + ";depthIndex=" + DepthAngleIndex.ToString(CultureInfo.InvariantCulture) + "/" + DepthScanAngles.Length.ToString(CultureInfo.InvariantCulture)
                + ";depthDistanceIndex=" + DepthDistanceIndex.ToString(CultureInfo.InvariantCulture)
                + ";accessCandidates=" + AccessCandidates.Count.ToString(CultureInfo.InvariantCulture)
                + ";depthCandidates=" + DepthCandidates.Count.ToString(CultureInfo.InvariantCulture)
                + ";assignmentCandidate=" + AssignmentCandidateIndex.ToString(CultureInfo.InvariantCulture)
                + ";assignmentBot=" + AssignmentBotIndex.ToString(CultureInfo.InvariantCulture)
                + ";assigned=" + Assignments.Count.ToString(CultureInfo.InvariantCulture)
                + ";unassigned=" + UnassignedBotIds.Count.ToString(CultureInfo.InvariantCulture)
                + ";ageSeconds=" + (DateTimeOffset.UtcNow - StartedAtUtc).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }

    private readonly struct PortalAnchorProbe
    {
        public PortalAnchorProbe(Vector3 position, string side)
        {
            Position = position;
            Side = side;
        }
        public Vector3 Position { get; }
        public string Side { get; }
    }

    private readonly struct AccessCandidate
    {
        public AccessCandidate(string portalKey, Vector3 anchor, Vector3 watchPoint, float score, float throatHalfWidth, float ownerHalfWidth, float farHalfWidth, float angleDegrees, int pathCorners)
        {
            PortalKey = portalKey;
            Anchor = anchor;
            WatchPoint = watchPoint;
            Score = score;
            ThroatHalfWidth = throatHalfWidth;
            OwnerHalfWidth = ownerHalfWidth;
            FarHalfWidth = farHalfWidth;
            AngleDegrees = angleDegrees;
            PathCorners = pathCorners;
        }
        public string PortalKey { get; }
        public Vector3 Anchor { get; }
        public Vector3 WatchPoint { get; }
        public float Score { get; }
        public float ThroatHalfWidth { get; }
        public float OwnerHalfWidth { get; }
        public float FarHalfWidth { get; }
        public float AngleDegrees { get; }
        public int PathCorners { get; }
    }
}

internal readonly struct VanguardInteriorPlanningDisposition
{
    private VanguardInteriorPlanningDisposition(
        bool indoorCandidate,
        bool planningInProgress,
        bool prepared,
        bool assignmentsCommitted,
        bool assignmentsCommittedThisTick,
        bool shouldSuppressGenericStationaryFormation,
        string volumeId,
        IReadOnlyList<string> assignedBotProfileIds,
        string reason)
    {
        IndoorCandidate = indoorCandidate;
        PlanningInProgress = planningInProgress;
        Prepared = prepared;
        AssignmentsCommitted = assignmentsCommitted;
        AssignmentsCommittedThisTick = assignmentsCommittedThisTick;
        ShouldSuppressGenericStationaryFormation = shouldSuppressGenericStationaryFormation;
        VolumeId = volumeId;
        AssignedBotProfileIds = assignedBotProfileIds;
        Reason = reason;
    }

    public bool IndoorCandidate { get; }
    public bool PlanningInProgress { get; }
    public bool Prepared { get; }
    public bool AssignmentsCommitted { get; }
    public bool AssignmentsCommittedThisTick { get; }
    public bool ShouldSuppressGenericStationaryFormation { get; }
    public string VolumeId { get; }
    public IReadOnlyList<string> AssignedBotProfileIds { get; }
    public string Reason { get; }

    public static VanguardInteriorPlanningDisposition None(string reason) => new(
        false, false, false, false, false, false, "none", Array.Empty<string>(), reason);

    public static VanguardInteriorPlanningDisposition Retained(string reason) => new(
        true, false, false, true, false, false, "retained", Array.Empty<string>(), reason);

    public static VanguardInteriorPlanningDisposition Blocked(string volumeId, string reason) => new(
        true, false, false, false, false, false, volumeId, Array.Empty<string>(), reason);

    public static VanguardInteriorPlanningDisposition Planning(string volumeId, bool stationaryAdmitted, string reason) => new(
        true, true, false, false, false, stationaryAdmitted, volumeId, Array.Empty<string>(), reason);

    public static VanguardInteriorPlanningDisposition PreparedPlan(string volumeId, IEnumerable<string> botProfileIds, string reason) => new(
        true, false, true, false, false, false, volumeId, botProfileIds.ToArray(), reason);

    public static VanguardInteriorPlanningDisposition Committed(
        string volumeId,
        IEnumerable<string> botProfileIds,
        bool committedThisTick,
        string reason) => new(
            true, false, false, true, committedThisTick, false, volumeId, botProfileIds.ToArray(), reason);
}

internal readonly struct InteriorSecurityAssignment
{
    public InteriorSecurityAssignment(string ownerProfileId, string volumeId, string operatorId, string botProfileId, string portalKey, string lane, string purpose, Vector3 anchor, Vector3 watchPoint, float score, float botPathDistance, DateTimeOffset assignedAtUtc, DateTimeOffset lastValidatedAtUtc, DateTimeOffset expiresAtUtc)
    {
        OwnerProfileId = ownerProfileId;
        VolumeId = volumeId;
        OperatorId = operatorId;
        BotProfileId = botProfileId;
        PortalKey = portalKey;
        Lane = lane;
        Purpose = purpose;
        Anchor = anchor;
        WatchPoint = watchPoint;
        Score = score;
        BotPathDistance = botPathDistance;
        AssignedAtUtc = assignedAtUtc;
        LastValidatedAtUtc = lastValidatedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public string OwnerProfileId { get; }
    public string VolumeId { get; }
    public string OperatorId { get; }
    public string BotProfileId { get; }
    public string PortalKey { get; }
    public string Lane { get; }
    public string Purpose { get; }
    public Vector3 Anchor { get; }
    public Vector3 WatchPoint { get; }
    public float Score { get; }
    public float BotPathDistance { get; }
    public DateTimeOffset AssignedAtUtc { get; }
    public DateTimeOffset LastValidatedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }

    public InteriorSecurityAssignment WithValidation(DateTimeOffset validatedAtUtc, DateTimeOffset expiresAtUtc) => new(
        OwnerProfileId, VolumeId, OperatorId, BotProfileId, PortalKey, Lane, Purpose, Anchor, WatchPoint,
        Score, BotPathDistance, AssignedAtUtc, validatedAtUtc, expiresAtUtc);
}

internal readonly struct InteriorVolumeState
{
    public InteriorVolumeState(
        string volumeId,
        Vector3 anchor,
        Vector3 anchorNavMeshPosition,
        Vector3 forward,
        int floorBand,
        VanguardOwnerEnvironmentSnapshot environment,
        DateTimeOffset enteredAtUtc,
        DateTimeOffset lastSeenAtUtc)
    {
        VolumeId = volumeId;
        Anchor = anchor;
        AnchorNavMeshPosition = anchorNavMeshPosition;
        Forward = forward;
        FloorBand = floorBand;
        Environment = environment;
        EnteredAtUtc = enteredAtUtc;
        LastSeenAtUtc = lastSeenAtUtc;
    }

    public string VolumeId { get; }
    public Vector3 Anchor { get; }
    public Vector3 AnchorNavMeshPosition { get; }
    public Vector3 Forward { get; }
    public int FloorBand { get; }
    public VanguardOwnerEnvironmentSnapshot Environment { get; }
    public DateTimeOffset EnteredAtUtc { get; }
    public DateTimeOffset LastSeenAtUtc { get; }

    public InteriorVolumeState WithObservation(VanguardOwnerEnvironmentSnapshot environment, DateTimeOffset now)
    {
        return new InteriorVolumeState(
            VolumeId,
            Anchor,
            AnchorNavMeshPosition,
            Forward,
            FloorBand,
            environment,
            EnteredAtUtc,
            now);
    }
}
#endif
