#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Movement;

// Responsibility: provides a conservative same-volume interior reposition when no usable authored PassagePath can produce an InteriorDevelop step.
// Flow: fresh exact-threat squad truth -> credible support -> local tactical-environment classification -> a few short line-clear NavMesh candidates -> one disposable InteriorDevelop opportunity.
// Authority boundary: fallback geometry only. It cannot infer/operate doors, claim room transitions, create targets, aim, fire, mutate SAIN or chain into another movement.
// Invariant: generic fallback is restricted to corridor/room/constrained local volumes, uses direct line-clear short paths with low detour/vertical change, preserves threat standoff and multi-threat safety, and is discarded when environment/support/assignment context changes.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class InteriorDevelopGenericFallbackService
{
    public const string StatusTag = "VANGUARD_TC_INTERIOR_GENERIC_FALLBACK_STATUS";

    private const float DesiredStepMeters = 5.5f;
    private const float MinimumStepMeters = 3.0f;
    private const float MaximumStepMeters = 7.5f;
    private const float MaximumPreparedPathMeters = 9.5f;
    private const float MaximumPathRatio = 1.45f;
    private const int MaximumPathCorners = 4;
    private const float MaximumVerticalDeltaMeters = 0.75f;
    private const float MinimumThreatStandoffMeters = 8.0f;
    private const float MaximumThreatApproachMeters = 3.5f;
    private const float MaximumSupportSeparationMeters = 32.0f;

    internal static bool IsGenericContextId(string? contextId)
        => !string.IsNullOrWhiteSpace(contextId) && contextId.StartsWith("generic~", StringComparison.OrdinalIgnoreCase);

    internal static bool TryResolve(
        IReadOnlyList<OperatorDecisionSnapshot> members,
        DateTimeOffset now,
        out InteriorDevelopOpportunity opportunity,
        out string reason)
    {
        opportunity = InteriorDevelopOpportunity.Empty;
        reason = "generic_not_evaluated";
        if (members == null || members.Count < 2)
        {
            reason = "generic_squad_support_missing";
            return false;
        }

        GenericInteriorCandidate? best = null;
        foreach (OperatorDecisionSnapshot mover in members
            .Where(item => item != null && item.Alive && item.Threat.HasThreat && item.Threat.ThreatAnchorPosition.HasValue)
            .OrderBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsMoverLocallyReady(mover, now)
                || !IsGenericEnvironmentEligible(mover))
            {
                continue;
            }
            if (!MultiThreatOffensiveSafetyPolicy.IsSupportMateriallyReady(mover, now, out string moverMaterial))
            {
                continue;
            }

            InteriorMultiAxisCompositionContext multiAxis = InteriorMultiAxisCompositionPolicy.Resolve(mover, now);
            if (!InteriorDevelopOpportunityService.TrySelectSupportForInterior(
                    members,
                    mover,
                    now,
                    multiAxis,
                    out OperatorDecisionSnapshot support,
                    out string supportSummary))
            {
                continue;
            }
            if (!MultiThreatOffensiveSafetyPolicy.IsOffensiveTranslationSafeWithProspectiveSupport(
                    mover,
                    support,
                    mover.Threat.EnemyId,
                    InteriorDevelopOpportunityService.MovementKind,
                    now,
                    out string multiThreatReason))
            {
                continue;
            }

            foreach (GenericInteriorSeed seed in BuildSeeds(mover))
            {
                GenericInteriorCandidate candidate = BuildCandidate(
                    mover,
                    support,
                    seed,
                    multiAxis,
                    moverMaterial,
                    supportSummary,
                    multiThreatReason);
                if (candidate.Valid && (!best.HasValue || candidate.Score > best.Value.Score))
                {
                    best = candidate;
                }
            }
        }

        if (!best.HasValue)
        {
            reason = "no_safe_generic_same_volume_interior_step";
            return false;
        }

        GenericInteriorCandidate selected = best.Value;
        string contextId = BuildContextId(selected.Mover, selected.MultiAxisToken);
        opportunity = new InteriorDevelopOpportunity(
            selected.Mover,
            selected.Support.BotProfileId,
            contextId,
            "generic_local_geometry",
            string.Empty,
            selected.GeometryKind,
            selected.Destination,
            selected.PathLengthMeters,
            selected.VerticalDeltaMeters,
            selected.ThreatDistanceBeforeMeters,
            selected.ThreatDistanceAfterMeters,
            0,
            selected.Score,
            selected.Summary);
        reason = "generic_interior_selected:" + selected.GeometryKind;
        return true;
    }

    internal static bool IsContextCurrent(
        string ownerProfileId,
        string threatId,
        string moverBotProfileId,
        string supportBotProfileId,
        string contextId,
        Vector3 destination,
        DateTimeOffset now,
        out string reason)
    {
        reason = "generic_context_not_evaluated";
        if (!TryParseContextId(contextId, out string environmentKind, out string placementMode, out string flags, out string multiAxisToken))
        {
            reason = "generic_context_id_invalid";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(moverBotProfileId, out OperatorDecisionSnapshot mover)
            || mover == null
            || !mover.Alive
            || !VanguardOperatorDecisionSnapshotService.IsFresh(mover, now, out _)
            || !string.Equals(mover.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(NormalizeThreat(mover.Threat.EnemyId), NormalizeThreat(threatId), StringComparison.OrdinalIgnoreCase)
            || !IsMoverLocallyReady(mover, now))
        {
            reason = "generic_mover_identity_or_authority_changed";
            return false;
        }
        if (!string.Equals(mover.SquadCohesion.TacticalEnvironmentKind, environmentKind, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(mover.SquadCohesion.TacticalPlacementMode, placementMode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(EnvironmentFlags(mover), flags, StringComparison.OrdinalIgnoreCase)
            || !IsGenericEnvironmentEligible(mover))
        {
            reason = "generic_environment_changed";
            return false;
        }
        string supportReason = "support_snapshot_missing";
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(supportBotProfileId, out OperatorDecisionSnapshot support)
            || !InteriorDevelopOpportunityService.IsCredibleSupportForInterior(mover, support, now, out supportReason))
        {
            reason = "generic_support_not_current:" + Safe(supportReason);
            return false;
        }
        Vector3? threatAnchor = mover.Threat.ThreatAnchorPosition;
        if (!threatAnchor.HasValue)
        {
            reason = "generic_threat_anchor_lost";
            return false;
        }
        Vector3 threatPosition = threatAnchor.Value;
        float before = HorizontalDistance(mover.Position, threatPosition);
        float after = HorizontalDistance(destination, threatPosition);
        if (after < MinimumThreatStandoffMeters || before - after > MaximumThreatApproachMeters)
        {
            reason = "generic_threat_geometry_changed:before=" + Number(before) + ":after=" + Number(after);
            return false;
        }
        if (!MultiThreatOffensiveSafetyPolicy.IsOffensiveTranslationSafeWithProspectiveSupport(
                mover,
                support,
                threatId,
                InteriorDevelopOpportunityService.MovementKind,
                now,
                out string multiThreatReason))
        {
            reason = "generic_multi_threat_safety_changed:" + Safe(multiThreatReason);
            return false;
        }
        if (!InteriorMultiAxisCompositionPolicy.IsContextCurrent(mover, multiAxisToken, now, null, out string multiAxisReason))
        {
            reason = "generic_multi_axis_context_changed:" + Safe(multiAxisReason);
            return false;
        }
        if (!ValidatePreparedSameVolumePath(
                mover.Position,
                destination,
                allowShortRemainingPath: true,
                out _,
                out string pathReason))
        {
            reason = "generic_path_changed:" + Safe(pathReason);
            return false;
        }

        reason = "generic_context_current;multiThreat=" + Safe(multiThreatReason) + ";multiAxis=" + Safe(multiAxisReason);
        return true;
    }

    private static bool IsMoverLocallyReady(OperatorDecisionSnapshot mover, DateTimeOffset now)
    {
        return mover != null
            && mover.Alive
            && VanguardOperatorDecisionSnapshotService.IsFresh(mover, now, out _)
            && mover.Sain != null
            && mover.Sain.Active == true
            && mover.Sain.HasEnemy == true
            && mover.Sain.IsInCombat == true
            && mover.Sain.RunningToCover != true
            && !mover.GrenadeHazard.HasRelevantHazard
            && !mover.Medical.Actionability.GrenadeThrowing
            && !mover.Medical.Actionability.AnyMedicineUsing
            && !mover.Medical.Actionability.Reloading;
    }

    private static bool IsGenericEnvironmentEligible(OperatorDecisionSnapshot mover)
    {
        string env = mover.SquadCohesion.TacticalEnvironmentKind;
        if (string.Equals(env, "outdoor_open", StringComparison.OrdinalIgnoreCase)
            || string.Equals(env, "urban_wraparound_risk", StringComparison.OrdinalIgnoreCase)
            || string.Equals(env, "environment_unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return mover.SquadCohesion.CorridorLike
            || mover.SquadCohesion.AdjacentRoomAllowed
            || string.Equals(env, "intermediate_constrained", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<GenericInteriorSeed> BuildSeeds(OperatorDecisionSnapshot mover)
    {
        Vector3 threat = mover.Threat.ThreatAnchorPosition ?? mover.Position + Vector3.forward * 10f;
        Vector3 threatAxis = Flatten(threat - mover.Position);
        Vector3 away = threatAxis.sqrMagnitude > 0.01f ? -threatAxis.normalized : Vector3.back;
        Vector3? ownerForward = mover.SquadCohesion.OwnerForward;
        Vector3 forward = ownerForward.HasValue ? Flatten(ownerForward.Value) : -away;
        if (forward.sqrMagnitude <= 0.01f) forward = Vector3.forward;
        forward.Normalize();
        Vector3 right = new(forward.z, 0f, -forward.x);

        if (mover.SquadCohesion.CorridorLike)
        {
            yield return new GenericInteriorSeed(mover.Position + forward * DesiredStepMeters, "generic_corridor", "corridor_forward");
            yield return new GenericInteriorSeed(mover.Position - forward * DesiredStepMeters, "generic_corridor", "corridor_reverse");
            yield break;
        }

        if (mover.SquadCohesion.AdjacentRoomAllowed)
        {
            yield return new GenericInteriorSeed(mover.Position + right * 4.5f + away * 1.5f, "generic_room_reposition", "room_lateral_right");
            yield return new GenericInteriorSeed(mover.Position - right * 4.5f + away * 1.5f, "generic_room_reposition", "room_lateral_left");
            yield return new GenericInteriorSeed(mover.Position + away * DesiredStepMeters, "generic_room_reposition", "room_depth_away");
            yield break;
        }

        yield return new GenericInteriorSeed(mover.Position + away * DesiredStepMeters, "generic_constrained_reposition", "constrained_depth_away");
        yield return new GenericInteriorSeed(mover.Position + right * 4.0f + away * 1.5f, "generic_constrained_reposition", "constrained_right");
        yield return new GenericInteriorSeed(mover.Position - right * 4.0f + away * 1.5f, "generic_constrained_reposition", "constrained_left");
    }

    private static GenericInteriorCandidate BuildCandidate(
        OperatorDecisionSnapshot mover,
        OperatorDecisionSnapshot support,
        GenericInteriorSeed seed,
        InteriorMultiAxisCompositionContext multiAxis,
        string moverMaterial,
        string supportSummary,
        string multiThreatReason)
    {
        if (!VanguardTacticalPlacementSolver.TryProjectRuntimeAnchor(seed.RawDestination, 2.0f, out Vector3 destination))
        {
            return GenericInteriorCandidate.Invalid;
        }
        float direct = HorizontalDistance(mover.Position, destination);
        if (direct < MinimumStepMeters || direct > MaximumStepMeters)
        {
            return GenericInteriorCandidate.Invalid;
        }
        if (Math.Abs(destination.y - mover.Position.y) > MaximumVerticalDeltaMeters)
        {
            return GenericInteriorCandidate.Invalid;
        }
        if (Physics.Linecast(mover.Position + Vector3.up * 1.15f, destination + Vector3.up * 1.15f))
        {
            return GenericInteriorCandidate.Invalid;
        }
        if (!ValidatePreparedSameVolumePath(
                mover.Position,
                destination,
                allowShortRemainingPath: false,
                out NavMeshPath path,
                out string pathReason))
        {
            return GenericInteriorCandidate.Invalid;
        }
        float pathLength = PreparedPathDistance(path);
        Vector3? threatAnchor = mover.Threat.ThreatAnchorPosition;
        if (!threatAnchor.HasValue)
        {
            return GenericInteriorCandidate.Invalid;
        }
        Vector3 threatPosition = threatAnchor.Value;
        float threatBefore = HorizontalDistance(mover.Position, threatPosition);
        float threatAfter = HorizontalDistance(destination, threatPosition);
        float threatApproach = threatBefore - threatAfter;
        if (threatAfter < MinimumThreatStandoffMeters || threatApproach > MaximumThreatApproachMeters)
        {
            return GenericInteriorCandidate.Invalid;
        }
        float supportDistance = HorizontalDistance(destination, support.Position);
        if (supportDistance > MaximumSupportSeparationMeters)
        {
            return GenericInteriorCandidate.Invalid;
        }
        if (!InteriorMultiAxisCompositionPolicy.TryEvaluateDestination(
                multiAxis,
                mover.Position,
                destination,
                crossesAuthoredPortal: false,
                out float multiAxisAdjustment,
                out string multiAxisSummary))
        {
            return GenericInteriorCandidate.Invalid;
        }

        Vector3 threatAxis = Flatten(threatPosition - mover.Position);
        Vector3 moveAxis = Flatten(destination - mover.Position);
        float lateralAngle = threatAxis.sqrMagnitude > 0.01f && moveAxis.sqrMagnitude > 0.01f
            ? Vector3.Angle(threatAxis.normalized, moveAxis.normalized)
            : 0f;
        float lateralValue = Math.Min(90f, lateralAngle) * 0.12f;
        float supportPenalty = Math.Max(0f, supportDistance - 18f) * 0.35f;
        float score = 62f + lateralValue + Math.Max(0f, threatAfter - threatBefore) * 1.25f + multiAxisAdjustment - pathLength * 1.7f - Math.Max(0f, threatApproach) * 3.0f - supportPenalty;
        string summary = "source=generic_same_volume"
            + ";seed=" + Safe(seed.Reason)
            + ";geometry=" + Safe(seed.GeometryKind)
            + ";env=" + Safe(mover.SquadCohesion.TacticalEnvironmentKind)
            + ";direct=" + Number(direct)
            + ";path=" + Number(pathLength)
            + ";corners=" + ((path.corners?.Length ?? 0).ToString(CultureInfo.InvariantCulture))
            + ";verticalDelta=" + Number(destination.y - mover.Position.y)
            + ";threatBefore=" + Number(threatBefore)
            + ";threatAfter=" + Number(threatAfter)
            + ";support=" + Safe(support.BotProfileId)
            + ";supportDistanceAfter=" + Number(supportDistance)
            + ";moverMaterial=" + Safe(moverMaterial)
            + ";supportSummary=" + Safe(supportSummary)
            + ";multiThreat=" + Safe(multiThreatReason)
            + ";" + multiAxisSummary
            + ";pathReason=" + Safe(pathReason)
            + ";unknownPortalTraversalClaim=false";

        return new GenericInteriorCandidate(
            true,
            mover,
            support,
            seed.GeometryKind,
            destination,
            pathLength,
            destination.y - mover.Position.y,
            threatBefore,
            threatAfter,
            score,
            multiAxis.Valid ? multiAxis.ContextToken : "none",
            summary);
    }

    private static bool ValidatePreparedSameVolumePath(
        Vector3 start,
        Vector3 destination,
        bool allowShortRemainingPath,
        out NavMeshPath path,
        out string reason)
    {
        path = new NavMeshPath();
        reason = "not_evaluated";
        if (!VanguardTacticalPlacementSolver.TryCalculatePreparedRuntimePath(start, destination, out path, out float pathLength, out string pathStatus))
        {
            reason = "prepared_path_failed:" + Safe(pathStatus);
            return false;
        }
        float direct = HorizontalDistance(start, destination);
        float ratio = direct <= 0.25f ? 99f : pathLength / direct;
        int corners = path.corners?.Length ?? 0;
        if ((!allowShortRemainingPath && pathLength < MinimumStepMeters) || pathLength > MaximumPreparedPathMeters)
        {
            reason = "prepared_path_length_out_of_bounds:" + Number(pathLength);
            return false;
        }
        if (ratio > MaximumPathRatio)
        {
            reason = "prepared_path_ratio_exceeded:" + ratio.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }
        if (corners < 2 || corners > MaximumPathCorners)
        {
            reason = "prepared_path_corner_count_out_of_bounds:" + corners.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        reason = "prepared_same_volume_path_complete:length=" + Number(pathLength) + ":ratio=" + ratio.ToString("0.00", CultureInfo.InvariantCulture) + ":corners=" + corners.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static float PreparedPathDistance(NavMeshPath path)
    {
        Vector3[] corners = path?.corners ?? Array.Empty<Vector3>();
        float total = 0f;
        for (int i = 1; i < corners.Length; i++)
        {
            total += HorizontalDistance(corners[i - 1], corners[i]);
        }
        return total;
    }

    private static string BuildContextId(OperatorDecisionSnapshot mover, string multiAxisToken)
        => "generic~" + Escape(mover.SquadCohesion.TacticalEnvironmentKind)
            + "~" + Escape(mover.SquadCohesion.TacticalPlacementMode)
            + "~" + Escape(EnvironmentFlags(mover))
            + "~" + Escape(multiAxisToken);

    private static bool TryParseContextId(string contextId, out string environmentKind, out string placementMode, out string flags, out string multiAxisToken)
    {
        environmentKind = string.Empty;
        placementMode = string.Empty;
        flags = string.Empty;
        multiAxisToken = "none";
        string[] parts = (contextId ?? string.Empty).Split('~');
        if (parts.Length != 5 || !string.Equals(parts[0], "generic", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        environmentKind = Unescape(parts[1]);
        placementMode = Unescape(parts[2]);
        flags = Unescape(parts[3]);
        multiAxisToken = Unescape(parts[4]);
        return !string.IsNullOrWhiteSpace(environmentKind) && !string.IsNullOrWhiteSpace(placementMode);
    }

    private static string EnvironmentFlags(OperatorDecisionSnapshot mover)
        => (mover.SquadCohesion.CorridorLike ? "corridor" : "not_corridor")
            + ":" + (mover.SquadCohesion.AdjacentRoomAllowed ? "adjacent" : "no_adjacent")
            + ":" + (mover.SquadCohesion.WideLateralAllowed ? "wide" : "no_wide")
            + ":" + (mover.SquadCohesion.SectorTopologyValid ? "topology_valid" : "topology_invalid");

    private static string Escape(string? value) => Safe(value).Replace("~", "_");
    private static string Unescape(string? value) => value ?? string.Empty;
    private static Vector3 Flatten(Vector3 value) { value.y = 0f; return value; }
    private static float HorizontalDistance(Vector3 a, Vector3 b) => Vector3.Distance(Flatten(a), Flatten(b));
    private static string NormalizeThreat(string? value) => string.IsNullOrWhiteSpace(value) || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) ? string.Empty : value.Trim();
    private static string Number(float value) => value.ToString("0.0", CultureInfo.InvariantCulture);
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private readonly struct GenericInteriorSeed
    {
        public GenericInteriorSeed(Vector3 rawDestination, string geometryKind, string reason)
        {
            RawDestination = rawDestination;
            GeometryKind = geometryKind;
            Reason = reason;
        }
        public Vector3 RawDestination { get; }
        public string GeometryKind { get; }
        public string Reason { get; }
    }

    private readonly struct GenericInteriorCandidate
    {
        public static readonly GenericInteriorCandidate Invalid = new(false, OperatorDecisionSnapshot.Empty, OperatorDecisionSnapshot.Empty, string.Empty, Vector3.zero, 0f, 0f, 0f, 0f, -9999f, "none", string.Empty);
        public GenericInteriorCandidate(bool valid, OperatorDecisionSnapshot mover, OperatorDecisionSnapshot support, string geometryKind, Vector3 destination, float pathLengthMeters, float verticalDeltaMeters, float threatDistanceBeforeMeters, float threatDistanceAfterMeters, float score, string multiAxisToken, string summary)
        {
            Valid = valid;
            Mover = mover;
            Support = support;
            GeometryKind = geometryKind;
            Destination = destination;
            PathLengthMeters = pathLengthMeters;
            VerticalDeltaMeters = verticalDeltaMeters;
            ThreatDistanceBeforeMeters = threatDistanceBeforeMeters;
            ThreatDistanceAfterMeters = threatDistanceAfterMeters;
            Score = score;
            MultiAxisToken = multiAxisToken;
            Summary = summary;
        }
        public bool Valid { get; }
        public OperatorDecisionSnapshot Mover { get; }
        public OperatorDecisionSnapshot Support { get; }
        public string GeometryKind { get; }
        public Vector3 Destination { get; }
        public float PathLengthMeters { get; }
        public float VerticalDeltaMeters { get; }
        public float ThreatDistanceBeforeMeters { get; }
        public float ThreatDistanceAfterMeters { get; }
        public float Score { get; }
        public string MultiAxisToken { get; }
        public string Summary { get; }
    }
}
#endif
