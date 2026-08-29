#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using EFT;
using UnityEngine;
using UnityEngine.AI;

// Responsibility: Computes an executable plan for Grenade Fallback Planner in the grenade emergency runtime without performing the final action itself.
// Flow: Current snapshots and doctrine are reduced to a candidate plan; the owning scheduler/executor rechecks authority before any mutation.
// Authority boundary: Planning is non-authoritative for physical execution and cannot bypass final combat, medical, loot, or movement safety checks.
// Invariant: Plans stay raid-scoped, deterministic from their inputs, and safe to discard when newer evidence supersedes them.
namespace Vanguard.Client.Runtime.Grenades;

internal static class VanguardGrenadeFallbackPlanner
{
    private static readonly float[] Radii = { 8f, 11f, 14f, 17f, 20f, 23f };
    private static readonly float[] Angles = { 0f, -22.5f, 22.5f, -45f, 45f, -67.5f, 67.5f, 90f, -90f };

    // Search outward from the Operator in small deterministic rings around the direction away from danger.
    // Each candidate must be on NavMesh, improve effective grenade distance, stay inside squad doctrine and
    // avoid already-failed anchors/other grenade hazards. The planner only returns the best complete path;
    // the emergency executor still owns the final movement lease and revalidation.
    public static VanguardGrenadeFallbackPlan Plan(
        BotOwner owner,
        VanguardGrenadeHazardDecisionSnapshot hazard,
        DateTimeOffset now,
        IReadOnlyList<Vector3>? excludedDestinations = null)
    {
        if (owner == null || owner.IsDead || !hazard.HasRelevantHazard)
        {
            return VanguardGrenadeFallbackPlan.None("owner_or_hazard_invalid");
        }

        Vector3 start = owner.Position;
        if (!NavMesh.SamplePosition(start + Vector3.up * 0.25f, out NavMeshHit startHit, 4f, NavMesh.AllAreas))
        {
            return VanguardGrenadeFallbackPlan.None("operator_navmesh_sample_failed");
        }

        Vector3 primaryDanger = hazard.DistanceToGrenade <= hazard.DistanceToDangerPoint
            ? hazard.GrenadePosition
            : hazard.DangerPoint;
        Vector3 away = start - primaryDanger;
        away.y = 0f;
        if (away.sqrMagnitude < 0.25f)
        {
            away = -hazard.Velocity;
            away.y = 0f;
        }
        if (away.sqrMagnitude < 0.25f)
        {
            away = Vector3.forward;
        }
        away.Normalize();

        float currentActualDistance = Vector3.Distance(start, hazard.GrenadePosition);
        float currentPredictedDistance = hazard.DangerPointKnown
            ? Vector3.Distance(start, hazard.DangerPoint)
            : currentActualDistance;
        float currentEffectiveDistance = Math.Min(currentActualDistance, currentPredictedDistance);

        VanguardGrenadeFallbackPlan best = VanguardGrenadeFallbackPlan.None("no_complete_safe_candidate");
        int candidates = 0;
        int complete = 0;
        int otherHazardRejected = 0;
        int envelopeRejected = 0;
        int failedAnchorRejected = 0;
        foreach (float radius in Radii)
        {
            foreach (float angle in Angles)
            {
                candidates++;
                Vector3 direction = Quaternion.Euler(0f, angle, 0f) * away;
                Vector3 requested = startHit.position + direction * radius;
                if (!NavMesh.SamplePosition(requested + Vector3.up * 0.25f, out NavMeshHit candidateHit, 2.75f, NavMesh.AllAreas))
                {
                    continue;
                }

                Vector3 destination = candidateHit.position;
                if (IsExcludedFailedAnchor(destination, excludedDestinations))
                {
                    failedAnchorRejected++;
                    continue;
                }

                float finalActualDistance = Vector3.Distance(destination, hazard.GrenadePosition);
                float finalPredictedDistance = hazard.DangerPointKnown
                    ? Vector3.Distance(destination, hazard.DangerPoint)
                    : finalActualDistance;
                float finalEffectiveDistance = Math.Min(finalActualDistance, finalPredictedDistance);
                float gain = finalEffectiveDistance - currentEffectiveDistance;
                float minimumCandidateDistance = Math.Min(hazard.SafeDistance - 1.0f, 9.0f);
                if (gain < 2.0f || finalEffectiveDistance < minimumCandidateDistance)
                {
                    envelopeRejected++;
                    continue;
                }

                if (!VanguardGrenadeHazardRegistry.IsCandidateSafeFromOtherHazards(destination, hazard.GrenadeKey, 7.5f, now, out _))
                {
                    otherHazardRejected++;
                    continue;
                }

                var path = new NavMeshPath();
                bool calculated = NavMesh.CalculatePath(startHit.position, destination, NavMesh.AllAreas, path);
                if (!calculated || path.status != NavMeshPathStatus.PathComplete || path.corners == null || path.corners.Length < 2)
                {
                    continue;
                }
                complete++;

                float pathLength = PathLength(path);
                if (pathLength > 38f)
                {
                    continue;
                }

                bool actualBlocked = VanguardGrenadeRuntimeResolver.ProbeLineOfEffect(hazard.GrenadePosition, destination);
                bool predictedBlocked = !hazard.DangerPointKnown || VanguardGrenadeRuntimeResolver.ProbeLineOfEffect(hazard.DangerPoint, destination);
                bool dualSolidCover = actualBlocked && predictedBlocked;
                float directionAlignment = Vector3.Dot(direction.normalized, away);
                float score = gain * 16f
                    + finalEffectiveDistance * 2.2f
                    + Math.Min(finalActualDistance, finalPredictedDistance) * 0.8f
                    + directionAlignment * 12f
                    + (dualSolidCover ? 55f : 0f)
                    - pathLength * 0.75f
                    - Math.Abs(destination.y - start.y) * 3f;

                string summary = "destination=" + VectorText(destination)
                    + ";gain=" + gain.ToString("0.00", CultureInfo.InvariantCulture)
                    + ";finalEffective=" + finalEffectiveDistance.ToString("0.00", CultureInfo.InvariantCulture)
                    + ";finalActual=" + finalActualDistance.ToString("0.00", CultureInfo.InvariantCulture)
                    + ";finalPredicted=" + finalPredictedDistance.ToString("0.00", CultureInfo.InvariantCulture)
                    + ";pathLength=" + pathLength.ToString("0.00", CultureInfo.InvariantCulture)
                    + ";corners=" + path.corners.Length.ToString(CultureInfo.InvariantCulture)
                    + ";actualBlocked=" + Bool(actualBlocked)
                    + ";predictedBlocked=" + Bool(predictedBlocked)
                    + ";dualSolidCover=" + Bool(dualSolidCover)
                    + ";angle=" + angle.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";radius=" + radius.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";score=" + score.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";failedAnchorExclusions=" + (excludedDestinations?.Count ?? 0).ToString(CultureInfo.InvariantCulture);
                if (!best.Valid || score > best.Score)
                {
                    best = new VanguardGrenadeFallbackPlan(true, destination, pathLength, dualSolidCover, score, summary);
                }
            }
        }

        if (!best.Valid)
        {
            return VanguardGrenadeFallbackPlan.None("no_complete_safe_candidate:candidates=" + candidates.ToString(CultureInfo.InvariantCulture)
                + ":complete=" + complete.ToString(CultureInfo.InvariantCulture)
                + ":envelopeRejected=" + envelopeRejected.ToString(CultureInfo.InvariantCulture)
                + ":otherHazardRejected=" + otherHazardRejected.ToString(CultureInfo.InvariantCulture)
                + ":failedAnchorRejected=" + failedAnchorRejected.ToString(CultureInfo.InvariantCulture)
                + ":failedAnchorExclusions=" + (excludedDestinations?.Count ?? 0).ToString(CultureInfo.InvariantCulture));
        }
        return best;
    }

    private static bool IsExcludedFailedAnchor(Vector3 destination, IReadOnlyList<Vector3>? excludedDestinations)
    {
        if (excludedDestinations == null || excludedDestinations.Count == 0) return false;
        foreach (Vector3 failed in excludedDestinations)
        {
            Vector3 delta = destination - failed;
            delta.y = 0f;
            if (delta.sqrMagnitude <= 4.0f) return true;
        }
        return false;
    }

    private static float PathLength(NavMeshPath path)
    {
        float total = 0f;
        Vector3[] corners = path.corners ?? Array.Empty<Vector3>();
        for (int i = 1; i < corners.Length; i++)
        {
            total += Vector3.Distance(corners[i - 1], corners[i]);
        }
        return total;
    }

    private static string VectorText(Vector3 value) => value.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + value.y.ToString("0.0", CultureInfo.InvariantCulture) + "," + value.z.ToString("0.0", CultureInfo.InvariantCulture);
    private static string Bool(bool value) => value ? "true" : "false";
}
#endif
