#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Solves candidate squad placement points around the owner while respecting formation lanes, local geometry, pathability and anti-stacking constraints.
// Flow: Given owner/operator positions and tactical environment evidence, the solver generates candidate anchors, scores/rejects blocked or overlapping positions and returns the best bounded placement for a movement plan.
// Authority boundary: Placement computation is advisory; path execution and movement authority remain with the movement executors and current lease owner.
// Invariant: Returned points must be reachable/safe enough for the current context, operators should not collapse onto one point, and failure yields no placement rather than an unsafe fabricated one.
namespace Vanguard.Client.Runtime.Movement;

internal readonly struct VanguardTacticalPlacementPlan
{
    public VanguardTacticalPlacementPlan(
        bool valid,
        Vector3 anchor,
        string desiredSector,
        string environmentKind,
        string placementMode,
        string reason,
        string pathSummary,
        float ownerPathDistance,
        float botPathDistance,
        float score)
    {
        Valid = valid;
        Anchor = anchor;
        DesiredSector = desiredSector;
        EnvironmentKind = environmentKind;
        PlacementMode = placementMode;
        Reason = reason;
        PathSummary = pathSummary;
        OwnerPathDistance = ownerPathDistance;
        BotPathDistance = botPathDistance;
        Score = score;
    }

    public static VanguardTacticalPlacementPlan Invalid(string reason) => new(false, Vector3.zero, "none", "unknown", "none", reason, "none", 0f, 0f, 0f);

    public bool Valid { get; }
    public Vector3 Anchor { get; }
    public string DesiredSector { get; }
    public string EnvironmentKind { get; }
    public string PlacementMode { get; }
    public string Reason { get; }
    public string PathSummary { get; }
    public float OwnerPathDistance { get; }
    public float BotPathDistance { get; }
    public float Score { get; }

    public string Summary => "sector=" + Safe(DesiredSector)
        + ";env=" + Safe(EnvironmentKind)
        + ";mode=" + Safe(PlacementMode)
        + ";score=" + Score.ToString("0.00", CultureInfo.InvariantCulture)
        + ";ownerPath=" + OwnerPathDistance.ToString("0.00", CultureInfo.InvariantCulture)
        + ";botPath=" + BotPathDistance.ToString("0.00", CultureInfo.InvariantCulture)
        + ";reason=" + Safe(Reason)
        + ";path=" + Safe(PathSummary);

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

internal static class VanguardTacticalPlacementSolver
{
    public const string StatusTag = "VANGUARD_TACTICAL_PLACEMENT_SOLVER_OK";
    public const string TacticalTuningStatusTag = "VANGUARD_TACTICAL_TUNING_OK";

    public static bool TryResolve(OperatorDecisionSnapshot snapshot, Vector3 botPosition, DateTimeOffset now, out VanguardTacticalPlacementPlan plan)
    {
        plan = VanguardTacticalPlacementPlan.Invalid("not_evaluated");
        if (snapshot == null || !snapshot.Alive)
        {
            plan = VanguardTacticalPlacementPlan.Invalid("snapshot_dead_or_missing");
            return false;
        }

        if (!snapshot.SquadCohesion.OwnerPosition.HasValue || !snapshot.SquadCohesion.OwnerForward.HasValue)
        {
            plan = VanguardTacticalPlacementPlan.Invalid("owner_anchor_missing");
            return false;
        }

        Vector3 owner = snapshot.SquadCohesion.OwnerPosition.Value;
        Vector3 forward = Flatten(snapshot.SquadCohesion.OwnerForward.Value);
        if (forward.sqrMagnitude <= 0.001f)
        {
            forward = Vector3.forward;
        }

        forward.Normalize();
        Vector3 right = new(forward.z, 0f, -forward.x);
        var candidates = BuildCandidates(snapshot, owner, forward, right);
        CandidateScore best = CandidateScore.Invalid("no_candidate");
        foreach (var candidate in candidates)
        {
            if (!TryScoreCandidate(snapshot, owner, botPosition, candidate, out var scored))
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

        if (!best.Valid)
        {
            plan = VanguardTacticalPlacementPlan.Invalid(best.Reason);
            return false;
        }

        plan = new VanguardTacticalPlacementPlan(
            true,
            best.Anchor,
            best.Sector,
            snapshot.SquadCohesion.TacticalEnvironmentKind,
            snapshot.SquadCohesion.TacticalPlacementMode,
            best.Reason,
            best.PathSummary,
            best.OwnerPathDistance,
            best.BotPathDistance,
            best.Score);
        return true;
    }

    private static IEnumerable<CandidateAnchor> BuildCandidates(OperatorDecisionSnapshot snapshot, Vector3 owner, Vector3 forward, Vector3 right)
    {
        string env = snapshot.SquadCohesion.TacticalEnvironmentKind;
        string current = snapshot.SquadCohesion.Sector;
        if (string.Equals(env, "corridor", StringComparison.OrdinalIgnoreCase))
        {
            yield return Candidate(owner - forward * 7.0f + right * 1.2f, "rear_stagger", "corridor_rear_stagger");
            yield return Candidate(owner - forward * 9.5f - right * 1.2f, "rear_guard", "corridor_rear_guard");
            yield return Candidate(owner + forward * 6.5f + right * 1.0f, "front_stagger", "corridor_front_stagger");
            yield return Candidate(owner + forward * 9.0f - right * 1.0f, "front_stagger", "corridor_front_stagger_far");
            yield break;
        }

        if (string.Equals(env, "urban_wraparound_risk", StringComparison.OrdinalIgnoreCase))
        {
            yield return Candidate(owner - forward * 6.5f, "close_support", "same_volume_rear_close");
            yield return Candidate(owner + forward * 5.5f, "close_support", "same_volume_front_close");
            yield return Candidate(owner - forward * 8.0f + right * 1.5f, "rear_stagger", "same_volume_rear_stagger");
            yield break;
        }

        if (string.Equals(env, "room_or_adjacent_room", StringComparison.OrdinalIgnoreCase))
        {
            yield return Candidate(owner - forward * 6.0f + right * 3.0f, "rear_right", "room_corner_rear_right");
            yield return Candidate(owner - forward * 6.0f - right * 3.0f, "rear_left", "room_corner_rear_left");
            yield return Candidate(owner + forward * 5.5f + right * 3.0f, "front_right", "room_front_right");
            yield return Candidate(owner + forward * 5.5f - right * 3.0f, "front_left", "room_front_left");
            yield return Candidate(owner - forward * 4.5f, "close_support", "room_close_support");
            yield break;
        }

        if (string.Equals(env, "outdoor_open", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in OutdoorCandidates(snapshot, owner, forward, right, current))
            {
                yield return candidate;
            }

            yield break;
        }

        yield return Candidate(owner - forward * 7.0f + right * 2.0f, "rear_right", "compressed_rear_right");
        yield return Candidate(owner - forward * 7.0f - right * 2.0f, "rear_left", "compressed_rear_left");
        yield return Candidate(owner + forward * 6.0f + right * 2.0f, "front_right", "compressed_front_right");
        yield return Candidate(owner + forward * 6.0f - right * 2.0f, "front_left", "compressed_front_left");
        yield return Candidate(owner - forward * 5.0f, "close_support", "compressed_close_support");
    }

    private static IEnumerable<CandidateAnchor> OutdoorCandidates(OperatorDecisionSnapshot snapshot, Vector3 owner, Vector3 forward, Vector3 right, string current)
    {
        bool currentLeft = current.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0;
        bool currentRight = current.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0;
        if (snapshot.SquadCohesion.SectorDuplicate || !snapshot.SquadCohesion.UsefulPosition)
        {
            if (currentLeft)
            {
                yield return Candidate(owner + right * 14.0f + forward * 2.0f, "right_flank", "outdoor_balance_right_flank");
            }
            else if (currentRight)
            {
                yield return Candidate(owner - right * 14.0f + forward * 2.0f, "left_flank", "outdoor_balance_left_flank");
            }
        }

        yield return Candidate(owner - right * 12.0f + forward * 3.0f, "left_flank", "outdoor_left_flank");
        yield return Candidate(owner + right * 12.0f + forward * 3.0f, "right_flank", "outdoor_right_flank");
        yield return Candidate(owner - forward * 12.0f + right * 4.0f, "rear_right", "outdoor_rear_right");
        yield return Candidate(owner - forward * 12.0f - right * 4.0f, "rear_left", "outdoor_rear_left");
        yield return Candidate(owner + forward * 10.0f - right * 4.0f, "front_left", "outdoor_front_left");
        yield return Candidate(owner + forward * 10.0f + right * 4.0f, "front_right", "outdoor_front_right");
    }

    private static bool TryScoreCandidate(OperatorDecisionSnapshot snapshot, Vector3 owner, Vector3 botPosition, CandidateAnchor candidate, out CandidateScore score)
    {
        score = CandidateScore.Invalid(candidate.Reason + ":not_scored");
        if (IsWideLateral(candidate.Sector) && !snapshot.SquadCohesion.WideLateralAllowed && !snapshot.SquadCohesion.AdjacentRoomAllowed)
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_lateral_not_open");
            return false;
        }

        if (snapshot.SquadCohesion.CorridorLike && IsWideLateral(candidate.Sector))
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_wide_lateral_corridor");
            return false;
        }

        if (!TrySample(candidate.RawAnchor, 2.5f, out var sampled))
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_navmesh_sample_failed");
            return false;
        }

        float ownerDirect = HorizontalDistance(owner, sampled);
        if (ownerDirect > Math.Max(8f, VanguardMovementAuthorityDoctrine.TacticalBubbleMeters - 5f))
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_anchor_outside_bubble");
            return false;
        }

        float botDirect = HorizontalDistance(botPosition, sampled);
        if (botDirect < VanguardMovementAuthorityDoctrine.TacticalRepositionMinDeltaMeters)
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_delta_too_small_" + botDirect.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (!TryPath(owner, sampled, out var ownerPathDistance, out var ownerCorners, out var ownerPathStatus))
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_owner_path_" + ownerPathStatus);
            return false;
        }

        if (!TryPath(botPosition, sampled, out var botPathDistance, out var botCorners, out var botPathStatus))
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_bot_path_" + botPathStatus);
            return false;
        }

        float ownerRatio = SafeRatio(ownerPathDistance, ownerDirect);
        string env = snapshot.SquadCohesion.TacticalEnvironmentKind;
        float maxRatio = MaxRatio(env);
        float maxOwnerPath = MaxOwnerPath(env);
        float maxBotPath = MaxBotPath(env);
        int maxCorners = MaxCorners(env);
        if (ownerRatio > maxRatio)
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_detour_ratio_" + ownerRatio.ToString("0.00", CultureInfo.InvariantCulture));
            return false;
        }

        if (ownerPathDistance > maxOwnerPath)
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_owner_path_too_long_" + ownerPathDistance.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (botPathDistance > maxBotPath)
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_bot_path_too_long_" + botPathDistance.ToString("0.0", CultureInfo.InvariantCulture) + "_limit_" + maxBotPath.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (botCorners > maxCorners + 2)
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_bot_corners_" + botCorners.ToString(CultureInfo.InvariantCulture));
            return false;
        }

        if (ownerCorners > maxCorners)
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_owner_corners_" + ownerCorners.ToString(CultureInfo.InvariantCulture));
            return false;
        }

        float value = 100f;
        value -= ownerPathDistance * 1.2f;
        value -= Math.Max(0f, ownerRatio - 1f) * 35f;
        value -= ownerCorners * 1.5f;
        value -= botPathDistance * 0.60f;
        if (snapshot.SquadCohesion.SectorDuplicate)
        {
            value += 8f;
        }

        if (!snapshot.SquadCohesion.UsefulPosition)
        {
            value += 6f;
        }

        string pathSummary = "ownerDirect=" + ownerDirect.ToString("0.0", CultureInfo.InvariantCulture)
            + ";ownerPath=" + ownerPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";ownerRatio=" + ownerRatio.ToString("0.00", CultureInfo.InvariantCulture)
            + ";ownerCorners=" + ownerCorners.ToString(CultureInfo.InvariantCulture)
            + ";botDirect=" + botDirect.ToString("0.0", CultureInfo.InvariantCulture)
            + ";botPath=" + botPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";botCorners=" + botCorners.ToString(CultureInfo.InvariantCulture)
            + ";maxBotPath=" + maxBotPath.ToString("0.0", CultureInfo.InvariantCulture);
        score = new CandidateScore(true, sampled, candidate.Sector, candidate.Reason + ":accepted", pathSummary, ownerPathDistance, botPathDistance, value);
        return true;
    }

    private static float MaxRatio(string env)
    {
        if (string.Equals(env, "corridor", StringComparison.OrdinalIgnoreCase)) return 1.35f;
        if (string.Equals(env, "urban_wraparound_risk", StringComparison.OrdinalIgnoreCase)) return 1.25f;
        if (string.Equals(env, "room_or_adjacent_room", StringComparison.OrdinalIgnoreCase)) return 1.55f;
        if (string.Equals(env, "outdoor_open", StringComparison.OrdinalIgnoreCase)) return 1.85f;
        return 1.60f;
    }

    private static float MaxOwnerPath(string env)
    {
        if (string.Equals(env, "corridor", StringComparison.OrdinalIgnoreCase)) return 14f;
        if (string.Equals(env, "urban_wraparound_risk", StringComparison.OrdinalIgnoreCase)) return 12f;
        if (string.Equals(env, "room_or_adjacent_room", StringComparison.OrdinalIgnoreCase)) return 16f;
        if (string.Equals(env, "outdoor_open", StringComparison.OrdinalIgnoreCase)) return 32f;
        return 20f;
    }

    private static float MaxBotPath(string env)
    {
        if (string.Equals(env, "corridor", StringComparison.OrdinalIgnoreCase)) return 14f;
        if (string.Equals(env, "urban_wraparound_risk", StringComparison.OrdinalIgnoreCase)) return 14f;
        if (string.Equals(env, "room_or_adjacent_room", StringComparison.OrdinalIgnoreCase)) return 16f;
        if (string.Equals(env, "outdoor_open", StringComparison.OrdinalIgnoreCase)) return 28f;
        return 22f;
    }

    private static int MaxCorners(string env)
    {
        if (string.Equals(env, "corridor", StringComparison.OrdinalIgnoreCase)) return 4;
        if (string.Equals(env, "urban_wraparound_risk", StringComparison.OrdinalIgnoreCase)) return 3;
        if (string.Equals(env, "room_or_adjacent_room", StringComparison.OrdinalIgnoreCase)) return 5;
        if (string.Equals(env, "outdoor_open", StringComparison.OrdinalIgnoreCase)) return 8;
        return 5;
    }

    private static bool IsWideLateral(string sector)
    {
        return string.Equals(sector, "left_flank", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sector, "right_flank", StringComparison.OrdinalIgnoreCase);
    }

    private static CandidateAnchor Candidate(Vector3 raw, string sector, string reason) => new(raw, sector, reason);

    /// <summary>Canonical runtime NavMesh projection shared with transient Tactical Authoring headless validation.</summary>
    public static bool TryProjectRuntimeAnchor(Vector3 raw, float radius, out Vector3 sampled)
    {
        return TrySample(raw, radius, out sampled);
    }

    /// <summary>Canonical complete-path probe shared with transient Tactical Authoring headless validation.</summary>
    public static bool TryCalculateRuntimePath(Vector3 start, Vector3 end, out float distance, out int corners, out string status)
    {
        return TryPath(start, end, out distance, out corners, out status);
    }

    private static bool TrySample(Vector3 raw, float radius, out Vector3 sampled)
    {
        if (NavMesh.SamplePosition(raw + Vector3.up * 0.30f, out var hit, radius, NavMesh.AllAreas))
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
        if (!TrySample(start, 3.0f, out var sampledStart))
        {
            status = "start_sample_failed";
            return false;
        }

        if (!TrySample(end, 3.0f, out var sampledEnd))
        {
            status = "end_sample_failed";
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

    private static float SafeRatio(float pathDistance, float directDistance) => directDistance <= 0.25f ? 1f : pathDistance / directDistance;
    private static string Bool(bool value) => value ? "true" : "false";

    private readonly struct CandidateAnchor
    {
        public CandidateAnchor(Vector3 rawAnchor, string sector, string reason)
        {
            RawAnchor = rawAnchor;
            Sector = sector;
            Reason = reason;
        }

        public Vector3 RawAnchor { get; }
        public string Sector { get; }
        public string Reason { get; }
    }

    private readonly struct CandidateScore
    {
        public CandidateScore(bool valid, Vector3 anchor, string sector, string reason, string pathSummary, float ownerPathDistance, float botPathDistance, float score)
        {
            Valid = valid;
            Anchor = anchor;
            Sector = sector;
            Reason = reason;
            PathSummary = pathSummary;
            OwnerPathDistance = ownerPathDistance;
            BotPathDistance = botPathDistance;
            Score = score;
        }

        public static CandidateScore Invalid(string reason) => new(false, Vector3.zero, "none", reason, "none", 0f, 0f, -9999f);

        public bool Valid { get; }
        public Vector3 Anchor { get; }
        public string Sector { get; }
        public string Reason { get; }
        public string PathSummary { get; }
        public float OwnerPathDistance { get; }
        public float BotPathDistance { get; }
        public float Score { get; }
    }
}
#endif
