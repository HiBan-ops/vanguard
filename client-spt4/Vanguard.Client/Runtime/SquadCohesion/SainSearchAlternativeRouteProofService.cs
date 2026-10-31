#if SPT_CLIENT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.TacticalAuthoring;

// Responsibility: collects bounded positive route/obstacle evidence for a Marksman standoff alternative to SAIN Search's unsafe offensive destination.
// Flow: exact owner/threat/snapshot -> native high-threat qualified pose -> per-owner heavy-work budget -> at most three local NavMesh candidates -> two-point static-obstacle occlusion + segment standoff checks -> shadow evidence.
// Authority boundary: a complete route and static obstruction are diagnostic evidence, NOT proof of a safe Combat handback or a lease; no native Search/path/weapon/target mutation.
// Invariant: uncertain multi-threat, mixed-floor, stale owner, budget exhaustion and unknown survival yield immediately. NO veto and no path writer are created here.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class SainSearchAlternativeRouteProofService
{
    private const float MinimumObservedRange = 16f;
    // Enlarge shadow search coverage only: two natural raids recorded high-threat role
    // violations at 110-147m. The three candidates, owner/segment/standoff/occlusion
    // guards and per-owner heavy-work budget remain independently mandatory.
    private const float MaximumObservedRange = 150f;
    private const float MaxPathLength = 26f;
    private const float MaxVerticalDelta = 2.25f;
    private const float MaxCandidateProjectionDelta = 1.75f;
    private const int MaxPathCorners = 10;

    // Same immutable admission witness feeds forensic and the optional native-path bridge.
    // A candidate remains a shadow witness until independent runtime handback qualification.
    internal static string AssessShadow(OperatorDecisionSnapshot snapshot, BotOwner botOwner,
        string exactEnemyId, DateTimeOffset now)
    {
        TryFindStandoffRoute(snapshot, botOwner, exactEnemyId, now, out _, out string result);
        return result;
    }

    internal static bool TryFindStandoffRoute(OperatorDecisionSnapshot snapshot, BotOwner botOwner,
        string exactEnemyId, DateTimeOffset now, out NavMeshPath? admittedPath, out string result)
    {
        admittedPath = null;
        if (snapshot == null || !snapshot.Alive || botOwner == null || botOwner.IsDead
            || !string.Equals(snapshot.Sain.CurrentAction, "SearchAction", StringComparison.OrdinalIgnoreCase)
            || snapshot.Sain.Active != true || snapshot.Sain.LayersActive != true
            || snapshot.Sain.RunningToCover != false || snapshot.Threat.EnemyVisible != false
            || snapshot.Threat.ShotMeRecently != false || snapshot.Threat.ShotAtMeRecently != false
            || snapshot.GrenadeHazard.HasRelevantHazard
            || SainSearchIntentCoordinationService.BlocksSearchForNoncombatPrimary(snapshot)
            || snapshot.MovementAuthority.HardOutsideBubble
            || !snapshot.SquadCohesion.OwnerKnown
            || !snapshot.SquadCohesion.OwnerReliableForActiveMovement
            || !snapshot.SquadCohesion.OwnerPosition.HasValue
            || !Finite(snapshot.SquadCohesion.OwnerPosition.Value))
        {
            result = "NO_PROOF:search_or_survival_not_exclusively_qualified";
            return false;
        }
        // Another separately identified plausible threat makes an isolated standoff route unsafe to infer.
        if (snapshot.ThreatScan.CandidatePosition.HasValue
            && !string.IsNullOrWhiteSpace(snapshot.ThreatScan.CandidateThreatId)
            && !string.Equals(snapshot.ThreatScan.CandidateThreatId, "none", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(snapshot.ThreatScan.CandidateThreatId, exactEnemyId, StringComparison.OrdinalIgnoreCase))
        {
            result = "NO_PROOF:secondary_threat_unresolved";
            return false;
        }
        if (!MarksmanForwardRoleDisciplinePolicy.TryGetExactHighThreatPoseForSearchShadow(
            snapshot.OwnerProfileId, exactEnemyId, now, out Vector3 threat, out string poseReason)
            || !Finite(threat) || !Finite(botOwner.Position))
        {
            result = "NO_PROOF:exact_qualified_threat_pose_missing:" + Safe(poseReason);
            return false;
        }
        Vector3 start = botOwner.Position;
        if (Mathf.Abs(start.y - threat.y) > MaxVerticalDelta)
        {
            result = "NO_PROOF:threat_different_level";
            return false;
        }
        float currentRange = FlatDistance(start, threat);
        if (currentRange < MinimumObservedRange || currentRange > MaximumObservedRange)
        {
            result = "NO_PROOF:too_close_or_out_of_range";
            return false;
        }
        Vector3 retreat = start - threat;
        retreat.y = 0f;
        if (retreat.sqrMagnitude <= 0.01f)
        {
            result = "NO_PROOF:direction_missing";
            return false;
        }
        retreat.Normalize();
        Vector3 lateral = new Vector3(-retreat.z, 0f, retreat.x);
        float minimumStandoff = VanguardTacticalCoordinationTuningPolicy.ForOwner(snapshot.OwnerProfileId).OverwatchStandoffMeters;
        float rearStep = Mathf.Clamp(minimumStandoff - currentRange + 3.0f, 5f, 13f);
        Vector3[] candidates =
        {
            start + retreat * rearStep,
            start + retreat * rearStep + lateral * 5f,
            start + retreat * rearStep - lateral * 5f
        };
        if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner(
                "TcSainSearchAlternativeShadow", snapshot.OwnerProfileId, 1, 1, out _))
        {
            result = "NO_PROOF:owner_frame_budget";
            return false;
        }
        // Read at most two already-existing SAIN cover points. This does not invoke
        // SAIN's cover finder, request movement, or increase the three-path budget.
        // If the optional cache is absent/mismatched, all three procedural points remain unchanged.
        int borrowedNativeCoverPoints = TryBorrowSainCoverPoints(botOwner, exactEnemyId,
            threat, start, candidates, out string nativeCoverStatus, out string nativeCoverDiagnostics);
        // Strict route admission remains unchanged. Optional comparative path/occlusion
        // work runs only AFTER all strict candidates have failed, on a bounded budget.
        string[] candidateDiagnostics = new string[candidates.Length];
        Vector3 ownerAnchor = snapshot.SquadCohesion.OwnerPosition.Value;
        float ownerLimit = VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters - 2f;
        // The owner-keyed settings are resolved on the execution authority, never from
        // another player's F12 and never as a replacement for the strict admission path.
        var searchComparisonSettings = VanguardRuntimeSettingsAuthorityResolver.ResolvePlayerScoped(
            snapshot.OwnerProfileId, "tactical_search_shadow_comparison");
        bool compareSearchLimits = searchComparisonSettings.TacticalSearchShadowComparisonEnabled;
        float relaxedOwnerLimit = Mathf.Clamp(searchComparisonSettings.TacticalSearchShadowOwnerLimitMeters,
            42.0f, 90.0f);
        bool requireCohesionImprovement = searchComparisonSettings.TacticalSearchShadowRequireOwnerDistanceImprovement;
        string[] comparisonDiagnostics = compareSearchLimits ? new string[candidates.Length] : Array.Empty<string>();
        bool[] comparisonPending = compareSearchLimits ? new bool[candidates.Length] : Array.Empty<bool>();
        Vector3[] comparisonAnchors = compareSearchLimits ? new Vector3[candidates.Length] : Array.Empty<Vector3>();
        NavMeshPath?[] comparisonExistingPaths = compareSearchLimits ? new NavMeshPath?[candidates.Length] : Array.Empty<NavMeshPath?>();
        int comparisonAdmissible = 0;
        int sampled = 0;
        int geometryQualified = 0;
        int pathQualified = 0;
        int standoffQualified = 0;
        int firstOcclusionQualified = 0;
        for (int candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
        {
            string source = candidateIndex > 0 && candidateIndex <= borrowedNativeCoverPoints
                ? "sain" : "procedural";
            if (!NavMesh.SamplePosition(candidates[candidateIndex] + Vector3.up * 0.25f,
                    out NavMeshHit navHit, 1.50f, NavMesh.AllAreas))
            {
                candidateDiagnostics[candidateIndex] = "c" + candidateIndex.ToString(CultureInfo.InvariantCulture)
                    + "~" + source + "~requested=" + Pos(candidates[candidateIndex]) + "~reject=sample";
                continue;
            }
            sampled++;
            Vector3 anchor = navHit.position;
            // Keep the existing short-circuit predicate verbatim: all additional metrics
            // are collected only AFTER its existing decision has failed.
            if (!Finite(anchor) || Mathf.Abs(anchor.y - start.y) > MaxVerticalDelta
                || FlatDistance(anchor, candidates[candidateIndex]) > MaxCandidateProjectionDelta
                || FlatDistance(anchor, threat) < minimumStandoff
                || FlatDistance(anchor, threat) < currentRange + 2f
                || FlatDistance(anchor, snapshot.SquadCohesion.OwnerPosition.Value)
                    > VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters - 2f)
            {
                candidateDiagnostics[candidateIndex] = CandidateDiagnostic(candidateIndex, source,
                    candidates[candidateIndex], anchor, start, threat, ownerAnchor)
                    + "~reject=geometry:" + GeometryRejectReasons(anchor, candidates[candidateIndex],
                        start, threat, ownerAnchor, minimumStandoff, currentRange, ownerLimit);
                if (compareSearchLimits && IsOwnerOnlyGeometryRejection(anchor, candidates[candidateIndex],
                    start, threat, ownerAnchor, minimumStandoff, currentRange, ownerLimit)
                    && ExperimentalOwnerDistanceAllowed(start, anchor, ownerAnchor, ownerLimit,
                        relaxedOwnerLimit, requireCohesionImprovement))
                {
                    // Defer extra path work until ALL strict candidates have failed.
                    // This cannot preempt or consume a budget ahead of native admission.
                    comparisonPending[candidateIndex] = true;
                    comparisonAnchors[candidateIndex] = anchor;
                }
                continue;
            }
            geometryQualified++;
            if (!VanguardTacticalPlacementSolver.TryCalculatePreparedRuntimePath(
                    start, anchor, out NavMeshPath path, out float pathLength, out _)
                || pathLength > MaxPathLength || path.corners.Length > MaxPathCorners)
            {
                candidateDiagnostics[candidateIndex] = CandidateDiagnostic(candidateIndex, source,
                    candidates[candidateIndex], anchor, start, threat, ownerAnchor) + "~reject=path";
                continue;
            }
            pathQualified++;
            if (!PreservesStandoffAlongCorners(path.corners, threat, currentRange, start.y,
                    snapshot.SquadCohesion.OwnerPosition.Value,
                    VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters - 2f))
            {
                candidateDiagnostics[candidateIndex] = CandidateDiagnostic(candidateIndex, source,
                    candidates[candidateIndex], anchor, start, threat, ownerAnchor) + "~reject=segment_standoff";
                if (compareSearchLimits
                    && ExperimentalOwnerDistanceAllowed(start, anchor, ownerAnchor, ownerLimit,
                        relaxedOwnerLimit, requireCohesionImprovement)
                    && PreservesStandoffAlongCorners(path.corners, threat, currentRange, start.y,
                        ownerAnchor, relaxedOwnerLimit))
                {
                    // Reuse the existing prepared path after strict evaluation ends.
                    // No comparative raycast or extra path work occurs before then.
                    comparisonPending[candidateIndex] = true;
                    comparisonAnchors[candidateIndex] = anchor;
                    comparisonExistingPaths[candidateIndex] = path;
                }
                continue;
            }
            standoffQualified++;
            // Two native Physics witnesses, in the same order and at the same endpoints.
            // Static obstruction does not prove hard cover or safe Combat handback.
            if (!StaticOcclusion(threat + Vector3.up * 1.45f, anchor + Vector3.up * 1.05f))
            {
                candidateDiagnostics[candidateIndex] = CandidateDiagnostic(candidateIndex, source,
                    candidates[candidateIndex], anchor, start, threat, ownerAnchor) + "~reject=occlusion_low";
                continue;
            }
            firstOcclusionQualified++;
            if (!StaticOcclusion(threat + Vector3.up * 1.45f, anchor + Vector3.up * 1.65f))
            {
                candidateDiagnostics[candidateIndex] = CandidateDiagnostic(candidateIndex, source,
                    candidates[candidateIndex], anchor, start, threat, ownerAnchor) + "~reject=occlusion_high";
                continue;
            }
            admittedPath = path;
            result = "SHADOW_ROUTE_STATIC_OCCLUSION_CANDIDATE:index=" + candidateIndex.ToString(CultureInfo.InvariantCulture)
                + ",pathMeters=" + pathLength.ToString("0.0", CultureInfo.InvariantCulture)
                + ",corners=" + path.corners.Length.ToString(CultureInfo.InvariantCulture)
                + ",standoff=" + FlatDistance(anchor, threat).ToString("0.0", CultureInfo.InvariantCulture)
                + ",candidateOwnerDistance=" + FlatDistance(anchor, snapshot.SquadCohesion.OwnerPosition.Value).ToString("0.0", CultureInfo.InvariantCulture)
                + ",ownerLimit=" + (VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters - 2f).ToString("0.0", CultureInfo.InvariantCulture)
                + ",candidateSource=" + (candidateIndex > 0 && candidateIndex <= borrowedNativeCoverPoints
                    ? "sain_existing_cover_cache" : "vanguard_procedural")
                + ",nativeCoverStatus=" + nativeCoverStatus;
            return true;
        }
        // Evaluate counterfactuals only once the original strict decision has failed.
        // These results are diagnostics; they cannot flow into admittedPath.
        if (compareSearchLimits)
        {
            for (int i = 0; i < comparisonPending.Length; i++)
            {
                if (!comparisonPending[i]) continue;
                Vector3 anchor = comparisonAnchors[i];
                string comparison;
                if (comparisonExistingPaths[i] != null)
                {
                    // Native-path geometry was already computed by the strict branch.
                    comparison = StaticOcclusion(threat + Vector3.up * 1.45f, anchor + Vector3.up * 1.05f)
                        && StaticOcclusion(threat + Vector3.up * 1.45f, anchor + Vector3.up * 1.65f)
                        ? "admissible_shadow_only" : "no_static_occlusion";
                }
                else
                {
                    comparison = EvaluateExperimentalCandidate(snapshot.OwnerProfileId, start,
                        anchor, threat, currentRange, ownerAnchor, relaxedOwnerLimit);
                }
                comparisonDiagnostics[i] = "c" + i.ToString(CultureInfo.InvariantCulture) + ":" + comparison;
                if (string.Equals(comparison, "admissible_shadow_only", StringComparison.Ordinal))
                {
                    comparisonAdmissible++;
                }
            }
        }
        // Additional procedural geometry is compared only after the strict route has
        // failed. It is not passed to admittedPath, SAIN, or movement authority.
        // NavMesh work for each extra hypothesis requires its own owner budget grant.
        string experimentalGeometry = compareSearchLimits
            ? CompareExperimentalRetreatGeometry(snapshot.OwnerProfileId, start, retreat,
                lateral, threat, currentRange, minimumStandoff, ownerAnchor,
                relaxedOwnerLimit, requireCohesionImprovement,
                searchComparisonSettings.TacticalSearchShadowRetreatStepMeters,
                searchComparisonSettings.TacticalSearchShadowLateralOffsetMeters,
                searchComparisonSettings.TacticalSearchShadowMaxPathMeters)
            : string.Empty;
        // Authored positions are compared separately from the strict admission. A
        // raid-scoped topology belongs to a single author; another player's
        // snapshot must never become evidence for this Operator's decisions.
        string authoredCandidates = compareSearchLimits
            ? CompareAuthoredPoseSlots(snapshot.OwnerProfileId, start, threat,
                currentRange, minimumStandoff, ownerAnchor, relaxedOwnerLimit,
                requireCohesionImprovement,
                searchComparisonSettings.TacticalSearchShadowMaxPathMeters)
            : string.Empty;
        // A compact stage tally avoids another raid with an opaque all-causes rejection.
        // s=NavMesh sample, g=geometry, p=prepared path, r=segment standoff,
        // o=first static occlusion (second must also pass to admit the candidate).
        result = "NO_PROOF:no_bounded_standoff_path_with_static_occlusion"
            + "_s" + sampled.ToString(CultureInfo.InvariantCulture)
            + "_g" + geometryQualified.ToString(CultureInfo.InvariantCulture)
            + "_p" + pathQualified.ToString(CultureInfo.InvariantCulture)
            + "_r" + standoffQualified.ToString(CultureInfo.InvariantCulture)
            + "_o" + firstOcclusionQualified.ToString(CultureInfo.InvariantCulture)
            + "_nativeCoverBorrowed" + borrowedNativeCoverPoints.ToString(CultureInfo.InvariantCulture)
            + "_nativeCoverStatus_" + nativeCoverStatus
            + "_geometryContext=start:" + Pos(start) + "~threat:" + Pos(threat)
            + "~owner:" + Pos(ownerAnchor)
            + "~range:" + Metric(currentRange) + "~minStandoff:" + Metric(minimumStandoff)
            + "~requiredRetreat:" + Metric(currentRange + 2f)
            + "~ownerLimit:" + Metric(ownerLimit)
            + "~verticalLimit:" + Metric(MaxVerticalDelta)
            + "~projectionLimit:" + Metric(MaxCandidateProjectionDelta)
            + "_geometryCandidates=" + string.Join("/", candidateDiagnostics)
            + "_nativeCoverDiagnostics=" + nativeCoverDiagnostics
            + (compareSearchLimits ? "_experimentalOwnerLimit=" + Metric(relaxedOwnerLimit)
                + "_experimentalRequiresImprovement=" + requireCohesionImprovement
                + "_experimentalAdmissible=" + comparisonAdmissible.ToString(CultureInfo.InvariantCulture)
                + "_experimentalCandidates=" + string.Join("/", comparisonDiagnostics)
                + "_experimentalGeometry=" + experimentalGeometry
                + "_authoredPoseComparison=" + authoredCandidates : string.Empty);
        return false;
    }

    // Compare a small set of authored poses after strict admission fails. These
    // positions describe author intent, not guaranteed cover or movement authority.
    // User-origin positions are never borrowed across player ownership boundaries.
    private static string CompareAuthoredPoseSlots(string ownerProfileId,
        Vector3 start, Vector3 threat, float currentRange, float minimumStandoff,
        Vector3 ownerAnchor, float ownerLimit, bool requireCohesionImprovement,
        float maxPathMeters)
    {
        if (string.IsNullOrWhiteSpace(ownerProfileId)
            || !VanguardTacticalCoordinationRuntimeReadService.TryGetSnapshot(out var topology))
        {
            return "topology_unavailable";
        }
        if (!string.Equals(topology.SourceOwnerProfileId, ownerProfileId,
                StringComparison.OrdinalIgnoreCase))
        {
            return "owner_topology_mismatch";
        }
        // The read service validates published map identity. Cross-check the
        // active raid again to avoid consuming a snapshot from a world transition.
        var world = Singleton<GameWorld>.Instance;
        if (world == null || string.IsNullOrWhiteSpace(world.LocationId)
            || !string.Equals(topology.MapId, world.LocationId.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return "map_mismatch";
        }
        maxPathMeters = Mathf.Clamp(maxPathMeters, 26f, 90f);
        var locations = new List<(Vector3 Position, float Range, string Origin)>(32);
        // Bound the scan, but reserve room for both User and Base origins so
        // many Base anchors cannot hide a player's own authored positions.
        int anchorCount = Math.Min(topology.TacticalAnchors.Count, 128);
        int baseAnchors = 0;
        int userAnchors = 0;
        for (int a = 0; a < anchorCount; a++)
        {
            var anchor = topology.TacticalAnchors[a];
            if (anchor == null || !anchor.Enabled
                || anchor.ValidationState != VanguardTacticalAuthoringValidationState.Valid
                || (!string.Equals(anchor.Origin, VanguardTacticalCoordinationAuthoringStore.BaseLayer,
                        StringComparison.Ordinal)
                    && !string.Equals(anchor.Origin, VanguardTacticalCoordinationAuthoringStore.UserLayer,
                        StringComparison.Ordinal))
                || !Finite(anchor.Center))
            {
                continue;
            }
            if (string.Equals(anchor.Origin, VanguardTacticalCoordinationAuthoringStore.UserLayer,
                    StringComparison.Ordinal))
            {
                if (++userAnchors > 16) continue;
            }
            else if (++baseAnchors > 16) continue;
            int poseCount = Math.Min(anchor.PoseSlots.Count, 8);
            for (int p = 0; p < poseCount; p++)
            {
                var pose = anchor.PoseSlots[p];
                if (pose == null || !pose.Enabled
                    || pose.ValidationState != VanguardTacticalAuthoringValidationState.Valid
                    || !Finite(pose.Position)
                    || (pose.NavMeshProjectedPosition.HasValue
                        && (!Finite(pose.NavMeshProjectedPosition.Value)
                            || FlatDistance(pose.NavMeshProjectedPosition.Value, pose.Position)
                                > MaxCandidateProjectionDelta)))
                {
                    continue;
                }
                Vector3 position = pose.NavMeshProjectedPosition ?? pose.Position;
                float range = FlatDistance(position, start);
                if (range < 1.5f || range > maxPathMeters
                    || Mathf.Abs(position.y - start.y) > MaxVerticalDelta)
                {
                    continue;
                }
                locations.Add((position, range, anchor.Origin));
            }
        }
        if (locations.Count == 0)
        {
            return "no_valid_nearby_pose";
        }
        // Choose inexpensive, geometry-compatible authored hints before spending
        // owner-scoped NavMesh and occlusion budgets. Final sampled-position and
        // full-path guards remain authoritative for every selected candidate.
        var eligibleLocations = new List<(Vector3 Position, float Range, string Origin)>(locations.Count);
        int standoffFiltered = 0;
        int cohesionFiltered = 0;
        for (int i = 0; i < locations.Count; i++)
        {
            var location = locations[i];
            float threatRange = FlatDistance(location.Position, threat);
            if (threatRange < minimumStandoff || threatRange < currentRange + 2f)
            {
                standoffFiltered++;
                continue;
            }
            if (!ExperimentalOwnerDistanceAllowed(start, location.Position, ownerAnchor,
                    VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters - 2f,
                    ownerLimit, requireCohesionImprovement))
            {
                cohesionFiltered++;
                continue;
            }
            eligibleLocations.Add(location);
        }
        // Stable ordering makes the bounded comparison reproducible when several
        // equally distant PoseSlots exist. Favor nearest paths without discarding
        // distinct User and Base candidates when both are available.
        eligibleLocations.Sort((left, right) =>
        {
            int order = left.Range.CompareTo(right.Range);
            if (order != 0) return order;
            order = string.Compare(left.Origin, right.Origin, StringComparison.Ordinal);
            if (order != 0) return order;
            order = left.Position.x.CompareTo(right.Position.x);
            if (order != 0) return order;
            order = left.Position.y.CompareTo(right.Position.y);
            return order != 0 ? order : left.Position.z.CompareTo(right.Position.z);
        });
        var selected = new List<(Vector3 Position, float Range, string Origin)>(3);
        foreach (string preferred in new[] {
            VanguardTacticalCoordinationAuthoringStore.UserLayer,
            VanguardTacticalCoordinationAuthoringStore.BaseLayer })
        {
            for (int i = 0; i < eligibleLocations.Count; i++)
            {
                if (!string.Equals(eligibleLocations[i].Origin, preferred, StringComparison.Ordinal))
                    continue;
                bool duplicate = false;
                for (int j = 0; j < selected.Count; j++)
                {
                    if (FlatDistance(eligibleLocations[i].Position, selected[j].Position) < 0.25f)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate) continue;
                selected.Add(eligibleLocations[i]);
                break;
            }
        }
        for (int i = 0; i < eligibleLocations.Count && selected.Count < 3; i++)
        {
            bool duplicate = false;
            for (int j = 0; j < selected.Count; j++)
            {
                if (FlatDistance(eligibleLocations[i].Position, selected[j].Position) < 0.25f)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate) selected.Add(eligibleLocations[i]);
        }
        int candidates = selected.Count;
        var outcomes = new string[candidates];
        int admissible = 0;
        for (int i = 0; i < candidates; i++)
        {
            Vector3 requested = selected[i].Position;
            // One budget grant covers this candidate's NavMesh and physics work.
            // Failure cannot change the result of normal Search admission.
            if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner(
                    "TcSainSearchAuthoredShadow", ownerProfileId, 1, 1, out _))
            {
                outcomes[i] = "budget_deferred";
                continue;
            }
            if (!NavMesh.SamplePosition(requested + Vector3.up * 0.25f,
                    out NavMeshHit hit, 1.50f, NavMesh.AllAreas))
            {
                outcomes[i] = "navmesh_sample";
                continue;
            }
            Vector3 position = hit.position;
            if (!Finite(position)
                || Mathf.Abs(position.y - start.y) > MaxVerticalDelta
                || FlatDistance(position, requested) > MaxCandidateProjectionDelta)
            {
                outcomes[i] = "geometry";
                continue;
            }
            if (FlatDistance(position, threat) < minimumStandoff
                || FlatDistance(position, threat) < currentRange + 2f)
            {
                outcomes[i] = "standoff";
                continue;
            }
            if (!ExperimentalOwnerDistanceAllowed(start, position, ownerAnchor,
                    VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters - 2f,
                    ownerLimit, requireCohesionImprovement))
            {
                outcomes[i] = "owner_cohesion";
                continue;
            }
            if (!VanguardTacticalPlacementSolver.TryCalculatePreparedRuntimePath(
                    start, position, out NavMeshPath path, out float pathLength, out _)
                || pathLength > maxPathMeters || path.corners.Length > MaxPathCorners)
            {
                outcomes[i] = "path";
                continue;
            }
            if (!PreservesStandoffAlongCorners(path.corners, threat, currentRange,
                    start.y, ownerAnchor, ownerLimit))
            {
                outcomes[i] = "segment_standoff_or_cohesion";
                continue;
            }
            if (!StaticOcclusion(threat + Vector3.up * 1.45f,
                    position + Vector3.up * 1.05f))
            {
                outcomes[i] = "occlusion_low";
                continue;
            }
            bool occluded = StaticOcclusion(threat + Vector3.up * 1.45f,
                position + Vector3.up * 1.65f);
            outcomes[i] = occluded ? "admissible_shadow_only" : "occlusion_high";
            if (occluded) admissible++;
        }
        for (int i = 0; i < candidates; i++)
        {
            string origin = string.Equals(selected[i].Origin,
                VanguardTacticalCoordinationAuthoringStore.BaseLayer,
                StringComparison.Ordinal) ? "base" : "user";
            outcomes[i] = origin + "/" + outcomes[i];
        }
        return "available:" + locations.Count.ToString(CultureInfo.InvariantCulture)
            + "~geometryEligible:" + eligibleLocations.Count.ToString(CultureInfo.InvariantCulture)
            + "~standoffPrefilterRejected:" + standoffFiltered.ToString(CultureInfo.InvariantCulture)
            + "~cohesionPrefilterRejected:" + cohesionFiltered.ToString(CultureInfo.InvariantCulture)
            + "~checked:" + candidates.ToString(CultureInfo.InvariantCulture)
            + "~admissible:" + admissible.ToString(CultureInfo.InvariantCulture)
            + "~results:" + string.Join(",", outcomes);
    }

    // Additional search hypotheses are explicitly bounded and are evaluated only
    // after failure of all three strict candidates. They never authorize movement.
    private static string CompareExperimentalRetreatGeometry(string ownerProfileId,
        Vector3 start, Vector3 retreat, Vector3 lateral, Vector3 threat,
        float currentRange, float minimumStandoff, Vector3 ownerAnchor,
        float ownerLimit, bool requireCohesionImprovement, float retreatStep,
        float lateralOffset, float maxPathMeters)
    {
        retreatStep = Mathf.Clamp(retreatStep, 5.0f, 45.0f);
        lateralOffset = Mathf.Clamp(lateralOffset, 0.0f, 25.0f);
        maxPathMeters = Mathf.Clamp(maxPathMeters, 26.0f, 90.0f);
        Vector3 rear = start + retreat * retreatStep;
        Vector3[] proposed =
        {
            rear,
            rear + lateral * lateralOffset,
            rear - lateral * lateralOffset
        };
        var outcomes = new string[proposed.Length];
        for (int i = 0; i < proposed.Length; i++)
        {
            // Zero lateral offset produces duplicate proposals; do not spend
            // NavMesh or raycast work on the same position more than once.
            if (i > 0 && FlatDistance(proposed[i], proposed[0]) < 0.25f)
            {
                outcomes[i] = "duplicate";
                continue;
            }
            if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner(
                    "TcSainSearchRetreatGeometryShadow", ownerProfileId, 1, 1, out _))
            {
                outcomes[i] = "budget_deferred";
                continue;
            }
            if (!NavMesh.SamplePosition(proposed[i] + Vector3.up * 0.25f,
                    out NavMeshHit navHit, 1.50f, NavMesh.AllAreas))
            {
                outcomes[i] = "navmesh_sample";
                continue;
            }
            Vector3 anchor = navHit.position;
            if (!Finite(anchor)
                || Mathf.Abs(anchor.y - start.y) > MaxVerticalDelta
                || FlatDistance(anchor, proposed[i]) > MaxCandidateProjectionDelta)
            {
                outcomes[i] = "geometry";
                continue;
            }
            if (FlatDistance(anchor, threat) < minimumStandoff
                || FlatDistance(anchor, threat) < currentRange + 2f)
            {
                outcomes[i] = "standoff";
                continue;
            }
            if (!ExperimentalOwnerDistanceAllowed(start, anchor, ownerAnchor,
                    VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters - 2f,
                    ownerLimit, requireCohesionImprovement))
            {
                outcomes[i] = "owner_cohesion";
                continue;
            }
            if (!VanguardTacticalPlacementSolver.TryCalculatePreparedRuntimePath(
                    start, anchor, out NavMeshPath path, out float length, out _)
                || length > maxPathMeters || path.corners.Length > MaxPathCorners)
            {
                outcomes[i] = "path";
                continue;
            }
            if (!PreservesStandoffAlongCorners(path.corners, threat, currentRange,
                    start.y, ownerAnchor, ownerLimit))
            {
                outcomes[i] = "segment_standoff_or_cohesion";
                continue;
            }
            if (!StaticOcclusion(threat + Vector3.up * 1.45f,
                    anchor + Vector3.up * 1.05f))
            {
                outcomes[i] = "occlusion_low";
                continue;
            }
            outcomes[i] = StaticOcclusion(threat + Vector3.up * 1.45f,
                anchor + Vector3.up * 1.65f)
                ? "admissible_shadow_only" : "occlusion_high";
        }
        return "retreat:" + Metric(retreatStep)
            + "~lateral:" + Metric(lateralOffset)
            + "~pathMax:" + Metric(maxPathMeters)
            + "~results:" + string.Join(",", outcomes);
    }

    // This branch is observation-only and is never called by the strict route
    // admission. It compares the owner-distance guard after all other geometry
    // checks have passed, using the same bounded candidate set.
    private static bool IsOwnerOnlyGeometryRejection(Vector3 anchor, Vector3 requested,
        Vector3 start, Vector3 threat, Vector3 owner, float minimumStandoff,
        float currentRange, float strictOwnerLimit) =>
        Finite(anchor)
        && Mathf.Abs(anchor.y - start.y) <= MaxVerticalDelta
        && FlatDistance(anchor, requested) <= MaxCandidateProjectionDelta
        && FlatDistance(anchor, threat) >= minimumStandoff
        && FlatDistance(anchor, threat) >= currentRange + 2f
        && FlatDistance(anchor, owner) > strictOwnerLimit;

    private static bool ExperimentalOwnerDistanceAllowed(Vector3 start, Vector3 anchor,
        Vector3 owner, float strictOwnerLimit, float relaxedOwnerLimit,
        bool requireImprovement)
    {
        float initialDistance = FlatDistance(start, owner);
        float destinationDistance = FlatDistance(anchor, owner);
        return destinationDistance <= relaxedOwnerLimit
            && (initialDistance <= strictOwnerLimit
                || !requireImprovement
                || destinationDistance < initialDistance - 0.25f);
    }

    // An experimental route consumes a separate bounded heavy-work budget.
    // It never changes the strict route result, SAIN movement, or admittedPath.
    private static string EvaluateExperimentalCandidate(string ownerProfileId, Vector3 start,
        Vector3 anchor, Vector3 threat, float currentRange, Vector3 owner, float relaxedLimit)
    {
        if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner(
            "TcSainSearchComparisonShadow", ownerProfileId, 1, 1, out _))
        {
            return "comparison_budget_exhausted";
        }
        if (!VanguardTacticalPlacementSolver.TryCalculatePreparedRuntimePath(
                start, anchor, out NavMeshPath path, out float length, out _)
            || length > MaxPathLength || path.corners.Length > MaxPathCorners)
        {
            return "path_rejected";
        }
        if (!PreservesStandoffAlongCorners(path.corners, threat, currentRange, start.y,
                owner, relaxedLimit))
        {
            return "segment_standoff_or_owner_rejected";
        }
        return StaticOcclusion(threat + Vector3.up * 1.45f, anchor + Vector3.up * 1.05f)
            && StaticOcclusion(threat + Vector3.up * 1.45f, anchor + Vector3.up * 1.65f)
            ? "admissible_shadow_only" : "no_static_occlusion";
    }

    // Opportunistic source selection, NOT a replacement for route admission. SAIN has
    // already sampled these points from nearby colliders for its own current GoalEnemy.
    // Exact target identity, close threat-pose correlation, fresh cover data, static
    // collider provenance and local distance are mandatory before borrowing a hint.
    // No native method that creates/updates cover points is invoked. Never read
    // CoverPoint.Spotted here: SAIN's getter can mutate its hit-history state.
    private static int TryBorrowSainCoverPoints(BotOwner botOwner, string exactEnemyId,
        Vector3 qualifiedThreat, Vector3 start, Vector3[] candidateSlots,
        out string nativeCoverStatus, out string nativeCoverDiagnostics)
    {
        nativeCoverStatus = "unavailable";
        nativeCoverDiagnostics = "unavailable";
        if (candidateSlots == null || candidateSlots.Length != 3
            || string.IsNullOrWhiteSpace(exactEnemyId))
        {
            nativeCoverStatus = "invalid_inputs";
            nativeCoverDiagnostics = "invalid_inputs";
            return 0;
        }
        try
        {
            object? sain = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(
                botOwner, "SAIN.Components.BotComponent");
            if (sain == null)
            {
                nativeCoverStatus = "sain_component_absent";
                nativeCoverDiagnostics = "sain_component_absent";
                return 0;
            }
            object? goalEnemy = VanguardOperatorRuntimeAuditReflection.GetDeep(
                sain, "EnemyController", "GoalEnemy");
            if (goalEnemy == null || !string.Equals(
                    VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goalEnemy,
                        "EnemyProfileId") as string, exactEnemyId, StringComparison.OrdinalIgnoreCase))
            {
                nativeCoverStatus = "goal_enemy_mismatch_or_absent";
                nativeCoverDiagnostics = "goal_enemy_mismatch_or_absent";
                return 0;
            }
            // Native cover may have been assembled for a previous contact. Reject
            // an unrelated or stale enemy pose rather than treating the cache as proof.
            if (VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goalEnemy,
                    "LastKnownPosition") is not Vector3 nativeEnemyPosition
                || !Finite(nativeEnemyPosition)
                || Vector3.Distance(nativeEnemyPosition, qualifiedThreat) > 8f)
            {
                nativeCoverStatus = "enemy_pose_mismatch_or_unknown";
                nativeCoverDiagnostics = "enemy_pose_mismatch_or_unknown";
                return 0;
            }
            if (VanguardOperatorRuntimeAuditReflection.GetDeep(sain,
                    "Cover", "CoverPoints") is not IList cachedPoints)
            {
                nativeCoverStatus = "cover_cache_unavailable";
                nativeCoverDiagnostics = "cover_cache_unavailable";
                return 0;
            }
            if (cachedPoints.Count == 0)
            {
                nativeCoverStatus = "cover_cache_empty";
                nativeCoverDiagnostics = "cover_cache_empty";
                return 0;
            }
            // Stage locally: a reflection failure must never leave the procedural
            // candidates partially replaced while reporting zero native hints.
            Vector3 firstHint = default;
            Vector3 secondHint = default;
            int borrowed = 0;
            // The existing scan remains capped at five cached points, and two
            // successful hints stop the scan. Reasons are first-failed guard in
            // the SAME order as the previous admission logic; no extra SAIN getters are invoked.
            var inspected = new List<string>(5);
            for (int i = 0; i < cachedPoints.Count && i < 5 && borrowed < 2; i++)
            {
                if (!TryReadSainCoverHint(cachedPoints[i], start, candidateSlots[0],
                    borrowed > 0, firstHint, out Vector3 position, out string reason))
                {
                    inspected.Add("h" + i.ToString(CultureInfo.InvariantCulture) + ":" + reason);
                    continue;
                }
                inspected.Add("h" + i.ToString(CultureInfo.InvariantCulture) + ":accepted");
                if (borrowed == 0) firstHint = position;
                else secondHint = position;
                borrowed++;
            }
            nativeCoverDiagnostics = "count:" + cachedPoints.Count.ToString(CultureInfo.InvariantCulture)
                + "~" + string.Join(",", inspected);
            nativeCoverStatus = borrowed == 0 ? "no_admissible_cover_hint"
                : "borrowed_" + borrowed.ToString(CultureInfo.InvariantCulture);
            if (borrowed > 0) candidateSlots[1] = firstHint;
            if (borrowed > 1) candidateSlots[2] = secondHint;
            return borrowed;
        }
        catch
        {
            // Optional SAIN coverage can disappear during bot lifecycle changes.
            // Never promote a failed reflection read to route authority.
            nativeCoverStatus = "reflection_error";
            nativeCoverDiagnostics = "reflection_error";
            return 0;
        }
    }

    // Preserves the exact hint guards/order, including the accepted data
    // types and bounds. All diagnostics are derived from fields already read.
    private static bool TryReadSainCoverHint(object? cover, Vector3 start,
        Vector3 rearCandidate, bool hasFirstHint, Vector3 firstHint,
        out Vector3 position, out string reason)
    {
        position = default;
        reason = "none";
        if (cover == null)
        {
            reason = "null_cover";
            return false;
        }
        if (VanguardOperatorRuntimeAuditReflection.GetDeep(cover, "CoverData", "IsBad") is not bool isBad)
        {
            reason = "bad_flag_unavailable";
            return false;
        }
        if (isBad)
        {
            reason = "bad_cover";
            return false;
        }
        if (VanguardOperatorRuntimeAuditReflection.GetDeep(cover, "CoverData", "TimeSinceUpdated") is not float ageSeconds)
        {
            reason = "age_unavailable";
            return false;
        }
        if (ageSeconds < 0f || ageSeconds > 2.5f)
        {
            reason = "stale_or_negative_age:" + Metric(ageSeconds);
            return false;
        }
        if (VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(cover, "Collider") is not Collider collider
            || collider == null)
        {
            reason = "collider_unavailable";
            return false;
        }
        if (collider.isTrigger)
        {
            reason = "trigger_collider";
            return false;
        }
        if (collider.gameObject == null)
        {
            reason = "collider_object_unavailable";
            return false;
        }
        if (!collider.gameObject.isStatic)
        {
            reason = "collider_not_static";
            return false;
        }
        if (VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(cover, "Position") is not Vector3 cachePosition)
        {
            reason = "position_unavailable";
            return false;
        }
        position = cachePosition;
        if (!Finite(position))
        {
            reason = "position_nonfinite";
            return false;
        }
        if (Mathf.Abs(position.y - start.y) > MaxVerticalDelta)
        {
            reason = "vertical:" + Metric(Mathf.Abs(position.y - start.y));
            return false;
        }
        float startDistance = FlatDistance(position, start);
        if (startDistance > MaxPathLength)
        {
            reason = "start_too_far:" + Metric(startDistance);
            return false;
        }
        if (startDistance < 1.5f)
        {
            reason = "start_too_near:" + Metric(startDistance);
            return false;
        }
        float rearDistance = FlatDistance(position, rearCandidate);
        if (rearDistance < 1.5f)
        {
            reason = "rear_duplicate:" + Metric(rearDistance);
            return false;
        }
        if (hasFirstHint)
        {
            float firstDistance = FlatDistance(position, firstHint);
            if (firstDistance < 1.5f)
            {
                reason = "native_hint_duplicate:" + Metric(firstDistance);
                return false;
            }
        }
        reason = "accepted";
        return true;
    }

    // Independent forensic classification ONLY after the original geometry admission
    // predicate has returned false. Multiple causes can coexist; none are vetoes.
    private static string GeometryRejectReasons(Vector3 anchor, Vector3 requested, Vector3 start,
        Vector3 threat, Vector3 owner, float minStandoff, float currentRange, float ownerLimit)
    {
        var reasons = new List<string>(6);
        if (!Finite(anchor)) reasons.Add("nonfinite");
        if (Mathf.Abs(anchor.y - start.y) > MaxVerticalDelta) reasons.Add("vertical");
        if (FlatDistance(anchor, requested) > MaxCandidateProjectionDelta) reasons.Add("projection");
        if (FlatDistance(anchor, threat) < minStandoff) reasons.Add("minStandoff");
        if (FlatDistance(anchor, threat) < currentRange + 2f) reasons.Add("relativeRetreat");
        if (FlatDistance(anchor, owner) > ownerLimit) reasons.Add("ownerCohesion");
        return reasons.Count == 0 ? "unclassified" : string.Join("+", reasons);
    }

    private static string CandidateDiagnostic(int index, string source, Vector3 requested,
        Vector3 anchor, Vector3 start, Vector3 threat, Vector3 owner) =>
        "c" + index.ToString(CultureInfo.InvariantCulture) + "~" + source
        + "~requested:" + Pos(requested) + "~anchor:" + Pos(anchor)
        + "~dy:" + Metric(Mathf.Abs(anchor.y - start.y))
        + "~projection:" + Metric(FlatDistance(anchor, requested))
        + "~enemy:" + Metric(FlatDistance(anchor, threat))
        + "~owner:" + Metric(FlatDistance(anchor, owner));

    private static string Pos(Vector3 p) =>
        Metric(p.x) + "," + Metric(p.y) + "," + Metric(p.z);
    private static string Metric(float value) =>
        float.IsNaN(value) ? "nan" : float.IsInfinity(value) ? "infinite"
        : value.ToString("0.0", CultureInfo.InvariantCulture);

    private static bool PreservesStandoffAlongCorners(Vector3[] corners, Vector3 threat, float startRange,
        float startY, Vector3 ownerAnchor, float ownerLimit)
    {
        if (corners == null || corners.Length < 2 || corners.Length > MaxPathCorners)
        {
            return false;
        }
        float smallestPermitted = Math.Max(MinimumObservedRange, startRange - 1.0f);
        for (int i = 0; i < corners.Length; i++)
        {
            if (!Finite(corners[i]) || Mathf.Abs(corners[i].y - startY) > MaxVerticalDelta
                || FlatDistance(corners[i], threat) < smallestPermitted
                || FlatDistance(corners[i], ownerAnchor) > ownerLimit)
            {
                return false;
            }
            if (i > 0 && MinimumSegmentThreatDistanceXZ(corners[i - 1], corners[i], threat)
                < smallestPermitted)
            {
                return false;
            }
        }
        return true;
    }

    // Endpoint+midpoint sampling misses an interior closest approach at arbitrary t.
    // The segment projection is exact in XZ (NavMesh corner path), so a candidate
    // cannot cross the threat standoff annulus unnoticed between sampled points.
    private static float MinimumSegmentThreatDistanceXZ(Vector3 start, Vector3 end, Vector3 threat)
    {
        start.y = 0f;
        end.y = 0f;
        threat.y = 0f;
        Vector3 segment = end - start;
        float lengthSquared = segment.sqrMagnitude;
        if (lengthSquared <= 0.0001f) return Vector3.Distance(start, threat);
        float fraction = Mathf.Clamp01(Vector3.Dot(threat - start, segment) / lengthSquared);
        return Vector3.Distance(start + segment * fraction, threat);
    }

    private static bool StaticOcclusion(Vector3 from, Vector3 to)
    {
        return Physics.Linecast(from, to, out RaycastHit hit, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore)
            && hit.collider != null && !hit.collider.isTrigger
            && hit.collider.gameObject != null && hit.collider.gameObject.isStatic;
    }

    private static bool Finite(Vector3 p) =>
        !float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsNaN(p.z)
        && !float.IsInfinity(p.x) && !float.IsInfinity(p.y) && !float.IsInfinity(p.z);
    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }
    private static string Safe(string s) => string.IsNullOrWhiteSpace(s) ? "none"
        : s.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
}
#endif
