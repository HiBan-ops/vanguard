#if SPT_CLIENT
using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Diagnostics;

// Responsibility: Analyzes local tactical geometry and squad spacing around the owner to provide context for cohesion, formation and return decisions.
// Flow: Bounded physics/navmesh samples classify obstruction, exposure, indoor/outdoor constraints and available movement space, then project those facts into a compact tactical-environment snapshot.
// Authority boundary: Analysis is read-only and cannot issue movement or combat actions.
// Invariant: Expensive world queries stay bounded/cached, unavailable evidence degrades conservatively, and the snapshot represents current local conditions rather than persistent map truth.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal readonly struct VanguardTacticalEnvironmentSnapshot
{
    public VanguardTacticalEnvironmentSnapshot(
        string environmentKind,
        string placementMode,
        bool corridorLike,
        bool wideLateralAllowed,
        bool adjacentRoomAllowed,
        bool topologyValid,
        string topologyReason,
        float directDistance,
        float pathDistance,
        float pathRatio,
        int pathCorners)
    {
        EnvironmentKind = environmentKind;
        PlacementMode = placementMode;
        CorridorLike = corridorLike;
        WideLateralAllowed = wideLateralAllowed;
        AdjacentRoomAllowed = adjacentRoomAllowed;
        TopologyValid = topologyValid;
        TopologyReason = topologyReason;
        DirectDistance = directDistance;
        PathDistance = pathDistance;
        PathRatio = pathRatio;
        PathCorners = pathCorners;
    }

    public static VanguardTacticalEnvironmentSnapshot Unknown(string reason) => new(
        "environment_unknown",
        "placement_observe_readonly",
        false,
        false,
        false,
        false,
        reason,
        0f,
        0f,
        0f,
        0);

    public string EnvironmentKind { get; }
    public string PlacementMode { get; }
    public bool CorridorLike { get; }
    public bool WideLateralAllowed { get; }
    public bool AdjacentRoomAllowed { get; }
    public bool TopologyValid { get; }
    public string TopologyReason { get; }
    public float DirectDistance { get; }
    public float PathDistance { get; }
    public float PathRatio { get; }
    public int PathCorners { get; }

    public string Signature => string.Join("|",
        EnvironmentKind,
        PlacementMode,
        CorridorLike ? "corridor" : "not_corridor",
        WideLateralAllowed ? "wide_lateral" : "no_wide_lateral",
        AdjacentRoomAllowed ? "adjacent_room_ok" : "adjacent_room_limited",
        TopologyValid ? "topology_valid" : "topology_invalid",
        TopologyReason,
        DirectDistance.ToString("0.0", CultureInfo.InvariantCulture),
        PathDistance.ToString("0.0", CultureInfo.InvariantCulture),
        PathRatio.ToString("0.00", CultureInfo.InvariantCulture),
        PathCorners.ToString(CultureInfo.InvariantCulture));
}

internal static class VanguardTacticalEnvironmentAnalyzer
{
    public const string StatusTag = "VANGUARD_TACTICAL_ENVIRONMENT_READONLY_OK";

    private static readonly float[] ProbeDistances = { 6f, 12f };
    private static bool bootLogged;

    public static VanguardTacticalEnvironmentSnapshot Analyze(Vector3 ownerPosition, Vector3 ownerForward, Vector3 operatorPosition, string currentSector)
    {
        LogBootOnce();
        Vector3 forward = Flatten(ownerForward);
        if (forward.sqrMagnitude <= 0.001f)
        {
            forward = Vector3.forward;
        }

        forward.Normalize();
        Vector3 right = new(forward.z, 0f, -forward.x);
        float directDistance = HorizontalDistance(ownerPosition, operatorPosition);
        if (!TryPath(ownerPosition, operatorPosition, out var ownerToOperatorPath, out var ownerToOperatorDistance, out var ownerToOperatorCorners))
        {
            return VanguardTacticalEnvironmentSnapshot.Unknown("owner_operator_path_invalid");
        }

        float pathRatio = SafeRatio(ownerToOperatorDistance, directDistance);
        var front = Probe(ownerPosition, forward);
        var rear = Probe(ownerPosition, -forward);
        var left = Probe(ownerPosition, -right);
        var rightProbe = Probe(ownerPosition, right);
        var frontLeft = Probe(ownerPosition, (forward - right).normalized);
        var frontRight = Probe(ownerPosition, (forward + right).normalized);
        int longOpen = Count(front.LongValid, rear.LongValid, left.LongValid, rightProbe.LongValid, frontLeft.LongValid, frontRight.LongValid);
        int lateralOpen = Count(left.ShortValid, rightProbe.ShortValid);
        bool longitudinalOpen = front.ShortValid || rear.ShortValid;
        bool corridorLike = longitudinalOpen && lateralOpen == 0;
        bool wideLateralAllowed = (left.LongValid || rightProbe.LongValid) && !corridorLike;
        bool adjacentRoomAllowed = (left.ShortValid || rightProbe.ShortValid || frontLeft.ShortValid || frontRight.ShortValid) && !wideLateralAllowed;
        string environmentKind = ResolveEnvironmentKind(corridorLike, wideLateralAllowed, adjacentRoomAllowed, longOpen, pathRatio, ownerToOperatorDistance, directDistance);
        string placementMode = ResolvePlacementMode(environmentKind);
        bool topologyValid = IsTopologyValid(environmentKind, currentSector, pathRatio, ownerToOperatorDistance, ownerToOperatorCorners, directDistance, wideLateralAllowed, adjacentRoomAllowed, out var topologyReason);

        return new VanguardTacticalEnvironmentSnapshot(
            environmentKind,
            placementMode,
            corridorLike,
            wideLateralAllowed,
            adjacentRoomAllowed,
            topologyValid,
            topologyReason,
            directDistance,
            ownerToOperatorDistance,
            pathRatio,
            ownerToOperatorCorners);
    }

    private static void LogBootOnce()
    {
        if (bootLogged)
        {
            return;
        }

        bootLogged = true;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_TACTICAL_ENVIRONMENT_BOOT active=true; mode=readonly_snapshot_feed_for_solver; probes=forward_back_left_right_diagonal; gates=corridor_room_adjacent_outdoor_wraparound; tag={StatusTag}; runtimeCleanTag={Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.RuntimeCleanStatusTag}; build={VanguardBuildVersion.BuildLabel}");
    }

    private static ProbeResult Probe(Vector3 origin, Vector3 direction)
    {
        bool shortValid = TryProbe(origin, direction, ProbeDistances[0], out _, out var shortRatio) && shortRatio <= 1.45f;
        bool longValid = TryProbe(origin, direction, ProbeDistances[1], out _, out var longRatio) && longRatio <= 1.65f;
        return new ProbeResult(shortValid, longValid);
    }

    private static bool TryProbe(Vector3 origin, Vector3 direction, float distance, out float pathDistance, out float ratio)
    {
        Vector3 raw = origin + Flatten(direction).normalized * distance;
        pathDistance = 0f;
        ratio = 0f;
        if (!TrySample(raw, 2.5f, out var sampled))
        {
            return false;
        }

        if (!TryPath(origin, sampled, out _, out pathDistance, out _))
        {
            return false;
        }

        float direct = HorizontalDistance(origin, sampled);
        ratio = SafeRatio(pathDistance, direct);
        return true;
    }

    private static string ResolveEnvironmentKind(bool corridorLike, bool wideLateralAllowed, bool adjacentRoomAllowed, int longOpen, float pathRatio, float pathDistance, float directDistance)
    {
        if (directDistance <= 18f && pathRatio >= 2.1f && pathDistance >= 22f)
        {
            return "urban_wraparound_risk";
        }

        if (corridorLike)
        {
            return "corridor";
        }

        if (longOpen >= 5 && wideLateralAllowed)
        {
            return "outdoor_open";
        }

        if (adjacentRoomAllowed)
        {
            return "room_or_adjacent_room";
        }

        return "intermediate_constrained";
    }

    private static string ResolvePlacementMode(string environmentKind)
    {
        return environmentKind switch
        {
            "corridor" => "longitudinal_stagger_readonly",
            "urban_wraparound_risk" => "same_volume_only_readonly",
            "room_or_adjacent_room" => "room_corner_or_doorway_readonly",
            "outdoor_open" => "sector_flank_allowed_readonly",
            _ => "compressed_sector_readonly"
        };
    }

    private static bool IsTopologyValid(string environmentKind, string currentSector, float pathRatio, float pathDistance, int corners, float directDistance, bool wideLateralAllowed, bool adjacentRoomAllowed, out string reason)
    {
        if (pathDistance <= 0f || directDistance <= 0.25f)
        {
            reason = "topology_unmeasurable";
            return false;
        }

        if (string.Equals(environmentKind, "urban_wraparound_risk", StringComparison.OrdinalIgnoreCase))
        {
            reason = "reject_structure_wraparound_risk";
            return false;
        }

        float maxRatio = environmentKind switch
        {
            "corridor" => 1.35f,
            "room_or_adjacent_room" => 1.55f,
            "intermediate_constrained" => 1.65f,
            _ => 1.85f
        };

        if (pathRatio > maxRatio)
        {
            reason = "reject_excessive_detour_ratio_" + pathRatio.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        if (pathDistance > 28f && !string.Equals(environmentKind, "outdoor_open", StringComparison.OrdinalIgnoreCase))
        {
            reason = "reject_non_outdoor_path_too_long_" + pathDistance.ToString("0.0", CultureInfo.InvariantCulture);
            return false;
        }

        if (corners > 6 && !string.Equals(environmentKind, "outdoor_open", StringComparison.OrdinalIgnoreCase))
        {
            reason = "reject_too_many_path_corners_" + corners.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        if (string.Equals(environmentKind, "corridor", StringComparison.OrdinalIgnoreCase)
            && IsWideFlankSector(currentSector))
        {
            reason = "reject_wide_flank_in_corridor";
            return false;
        }

        if (IsWideFlankSector(currentSector) && !wideLateralAllowed && !adjacentRoomAllowed)
        {
            reason = "reject_lateral_sector_not_topologically_open";
            return false;
        }

        reason = "topology_valid_same_tactical_volume";
        return true;
    }

    private static bool IsWideFlankSector(string sector)
    {
        return string.Equals(sector, "left_flank", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sector, "right_flank", StringComparison.OrdinalIgnoreCase);
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

    private static bool TryPath(Vector3 start, Vector3 end, out NavMeshPath path, out float distance, out int corners)
    {
        path = new NavMeshPath();
        distance = 0f;
        corners = 0;
        if (!TrySample(start, 3.0f, out var sampledStart) || !TrySample(end, 3.0f, out var sampledEnd))
        {
            return false;
        }

        bool calculated = NavMesh.CalculatePath(sampledStart, sampledEnd, NavMesh.AllAreas, path);
        corners = path.corners == null ? 0 : path.corners.Length;
        distance = PathDistance(path);
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

    private static float SafeRatio(float pathDistance, float directDistance)
    {
        return directDistance <= 0.25f ? 1f : pathDistance / directDistance;
    }

    private static int Count(params bool[] values)
    {
        int count = 0;
        foreach (bool value in values)
        {
            if (value)
            {
                count++;
            }
        }

        return count;
    }

    private readonly struct ProbeResult
    {
        public ProbeResult(bool shortValid, bool longValid)
        {
            ShortValid = shortValid;
            LongValid = longValid;
        }

        public bool ShortValid { get; }
        public bool LongValid { get; }
    }
}
#endif
