#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Computes an executable plan for Corpse Loot Approach Planner in the loot runtime without performing the final action itself.
// Flow: Current snapshots and doctrine are reduced to a candidate plan; the owning scheduler/executor rechecks authority before any mutation.
// Authority boundary: Planning is non-authoritative for physical execution and cannot bypass final combat, medical, loot, or movement safety checks.
// Invariant: Plans stay raid-scoped, deterministic from their inputs, and safe to discard when newer evidence supersedes them.
namespace Vanguard.Client.Runtime.Loot;

internal readonly struct VanguardCorpseLootApproachPlan
{
    public VanguardCorpseLootApproachPlan(bool valid, Vector3 anchor, float directDistance, float pathDistance, float addedDetour, float pathRatio, float ownerAnchorDistance, int corners, string reason, string pathSummary)
    {
        Valid = valid;
        Anchor = anchor;
        DirectDistance = directDistance;
        PathDistance = pathDistance;
        AddedDetour = addedDetour;
        PathRatio = pathRatio;
        OwnerAnchorDistance = ownerAnchorDistance;
        Corners = corners;
        Reason = reason;
        PathSummary = pathSummary;
    }

    public static VanguardCorpseLootApproachPlan Invalid(string reason) => new(false, Vector3.zero, 0f, 0f, 0f, 0f, 0f, 0, reason, "none");

    public bool Valid { get; }
    public Vector3 Anchor { get; }
    public float DirectDistance { get; }
    public float PathDistance { get; }
    public float AddedDetour { get; }
    public float PathRatio { get; }
    public float OwnerAnchorDistance { get; }
    public int Corners { get; }
    public string Reason { get; }
    public string PathSummary { get; }

    public string Summary => $"valid={Valid}; anchor={Anchor.x:0.00},{Anchor.y:0.00},{Anchor.z:0.00}; direct={DirectDistance:0.00}; path={PathDistance:0.00}; detour={AddedDetour:0.00}; ratio={PathRatio:0.00}; ownerAnchor={OwnerAnchorDistance:0.00}; corners={Corners}; reason={Safe(Reason)}; pathSummary={Safe(PathSummary)}";

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

/// <summary>
/// Shared physical reachability authority for CorpseLoot and the world-container approach executor.
/// The runtime deliberately exposes a non-snapshot overload so the runtime read-only qualifier can fall back to
/// the exact same bounded anchor proof used by the runtime instead of rejecting a corpse solely because its
/// Transform position is not itself a complete NavMesh destination.
/// </summary>
internal static class VanguardCorpseLootApproachPlanner
{
    private static readonly float[] CandidateAngles = { 0f, 38f, -38f, 78f, -78f, 180f };

    public static bool TryBuild(OperatorDecisionSnapshot snapshot, Vector3 botPosition, Vector3 corpsePosition, out VanguardCorpseLootApproachPlan plan)
    {
        if (snapshot == null)
        {
            plan = VanguardCorpseLootApproachPlan.Invalid("snapshot_missing");
            return false;
        }

        return TryBuild(snapshot.OwnerProfileId, snapshot.SquadCohesion.OwnerPosition, botPosition, corpsePosition, out plan);
    }

    internal static bool TryBuild(string ownerProfileId, Vector3? ownerPosition, Vector3 botPosition, Vector3 corpsePosition, out VanguardCorpseLootApproachPlan plan)
    {
        plan = VanguardCorpseLootApproachPlan.Invalid("not_evaluated");
        if (!ownerPosition.HasValue)
        {
            plan = VanguardCorpseLootApproachPlan.Invalid("owner_position_missing");
            return false;
        }

        float directCorpseDistance = HorizontalDistance(botPosition, corpsePosition);
        if (directCorpseDistance > VanguardCorpseLootApproachDoctrine.MaximumDirectCorpseDistanceMetersForOwner(ownerProfileId))
        {
            plan = VanguardCorpseLootApproachPlan.Invalid("direct_distance_exceeds_budget:" + directCorpseDistance.ToString("0.00", CultureInfo.InvariantCulture));
            return false;
        }

        if (!NavMesh.SamplePosition(botPosition, out NavMeshHit startHit, 2.5f, NavMesh.AllAreas))
        {
            plan = VanguardCorpseLootApproachPlan.Invalid("start_navmesh_sample_failed");
            return false;
        }

        // A corpse can physically sit off the NavMesh while the Operator is already close enough to
        // interact. Requiring two path corners in that state creates a false negative for otherwise reachable corpses.
        if (directCorpseDistance <= VanguardCorpseLootApproachDoctrine.CorpseInteractionDistanceMeters)
        {
            float ownerAnchorDistance = HorizontalDistance(ownerPosition.Value, startHit.position);
            if (ownerAnchorDistance <= VanguardCorpseLootApproachDoctrine.MaximumOwnerAnchorDistanceMeters)
            {
                plan = new VanguardCorpseLootApproachPlan(
                    true,
                    startHit.position,
                    directCorpseDistance,
                    0f,
                    0f,
                    1f,
                    ownerAnchorDistance,
                    1,
                    "already_within_interaction_range",
                    "status=already_in_range;corners=1;distance=0.00");
                return true;
            }
        }

        Vector3 radial = Flatten(botPosition - corpsePosition);
        if (radial.sqrMagnitude < 0.01f)
        {
            radial = Flatten(ownerPosition.Value - corpsePosition);
        }
        if (radial.sqrMagnitude < 0.01f)
        {
            radial = Vector3.forward;
        }
        radial.Normalize();

        var reusablePath = new NavMeshPath();
        VanguardCorpseLootApproachPlan? best = null;
        foreach (float angle in CandidateAngles)
        {
            Vector3 raw = corpsePosition + Rotate(radial, angle) * VanguardCorpseLootApproachDoctrine.ApproachAnchorOffsetMeters;
            if (!NavMesh.SamplePosition(raw + Vector3.up * 0.25f, out NavMeshHit anchorHit, 1.40f, NavMesh.AllAreas))
            {
                continue;
            }

            if (!TryCandidate(ownerPosition.Value, botPosition, directCorpseDistance, startHit.position, anchorHit.position, reusablePath,
                    "bounded_complete_path", out VanguardCorpseLootApproachPlan candidate))
            {
                continue;
            }

            if (!best.HasValue
                || candidate.PathDistance + candidate.OwnerAnchorDistance * 0.15f
                    < best.Value.PathDistance + best.Value.OwnerAnchorDistance * 0.15f)
            {
                best = candidate;
            }
        }

        if (best.HasValue)
        {
            plan = best.Value;
            return true;
        }

        // Convergence fallback: if no radial anchor survives, accept the projected target NavMesh point
        // only when it is itself reachable and remains within the normal interaction radius of the corpse.
        // This makes a successful the runtime exact-target proof consumable by the runtime instead of creating two
        // contradictory notions of reachability.
        if (NavMesh.SamplePosition(corpsePosition, out NavMeshHit targetHit, 1.50f, NavMesh.AllAreas)
            && HorizontalDistance(targetHit.position, corpsePosition) <= VanguardCorpseLootApproachDoctrine.CorpseInteractionDistanceMeters
            && TryCandidate(ownerPosition.Value, botPosition, directCorpseDistance, startHit.position, targetHit.position, reusablePath,
                "projected_target_navmesh_fallback", out VanguardCorpseLootApproachPlan targetCandidate))
        {
            plan = targetCandidate;
            return true;
        }

        plan = VanguardCorpseLootApproachPlan.Invalid("no_bounded_complete_anchor");
        return false;
    }

    private static bool TryCandidate(
        Vector3 ownerPosition,
        Vector3 botPosition,
        float directCorpseDistance,
        Vector3 sampledStart,
        Vector3 anchor,
        NavMeshPath reusablePath,
        string reason,
        out VanguardCorpseLootApproachPlan candidate)
    {
        candidate = VanguardCorpseLootApproachPlan.Invalid(reason);
        float ownerAnchorDistance = HorizontalDistance(ownerPosition, anchor);
        if (ownerAnchorDistance > VanguardCorpseLootApproachDoctrine.MaximumOwnerAnchorDistanceMeters)
        {
            return false;
        }

        if (!TryPath(sampledStart, anchor, reusablePath, out float pathDistance, out int corners, out string pathSummary))
        {
            return false;
        }

        float directAnchorDistance = Math.Max(0.10f, HorizontalDistance(botPosition, anchor));
        float addedDetour = Math.Max(0f, pathDistance - directAnchorDistance);
        float ratio = pathDistance / directAnchorDistance;
        if (pathDistance > VanguardCorpseLootApproachDoctrine.MaximumPathDistanceMeters
            || addedDetour > VanguardCorpseLootApproachDoctrine.MaximumAddedDetourMeters
            || ratio > VanguardCorpseLootApproachDoctrine.MaximumPathRatio)
        {
            return false;
        }

        candidate = new VanguardCorpseLootApproachPlan(
            true,
            anchor,
            directCorpseDistance,
            pathDistance,
            addedDetour,
            ratio,
            ownerAnchorDistance,
            corners,
            reason,
            pathSummary);
        return true;
    }

    private static bool TryPath(Vector3 sampledStart, Vector3 end, NavMeshPath reusablePath, out float distance, out int corners, out string summary)
    {
        distance = 0f;
        corners = 0;
        summary = "none";
        if (!NavMesh.SamplePosition(end, out NavMeshHit endHit, 1.5f, NavMesh.AllAreas))
        {
            summary = "sample_failed";
            return false;
        }

        float alreadyThere = HorizontalDistance(sampledStart, endHit.position);
        if (alreadyThere <= 0.15f)
        {
            corners = 1;
            summary = "status=already_at_anchor;corners=1;distance=0.00";
            return true;
        }

        bool calculated = NavMesh.CalculatePath(sampledStart, endHit.position, NavMesh.AllAreas, reusablePath);
        Vector3[]? pathCorners = reusablePath.corners;
        corners = pathCorners?.Length ?? 0;
        if (!calculated
            || reusablePath.status != NavMeshPathStatus.PathComplete
            || pathCorners is null
            || corners < 2)
        {
            summary = "calculated=" + calculated + ";status=" + reusablePath.status + ";corners=" + corners;
            return false;
        }

        for (int index = 1; index < pathCorners.Length; index++)
        {
            distance += HorizontalDistance(pathCorners[index - 1], pathCorners[index]);
        }
        summary = "status=" + reusablePath.status + ";corners=" + corners + ";distance=" + distance.ToString("0.00", CultureInfo.InvariantCulture);
        return true;
    }

    private static Vector3 Rotate(Vector3 vector, float degrees)
    {
        float radians = degrees * Mathf.Deg2Rad;
        float cos = Mathf.Cos(radians);
        float sin = Mathf.Sin(radians);
        return new Vector3(vector.x * cos - vector.z * sin, 0f, vector.x * sin + vector.z * cos);
    }

    private static Vector3 Flatten(Vector3 value)
    {
        value.y = 0f;
        return value;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }
}
#endif
