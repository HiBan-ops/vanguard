#if SPT_CLIENT
using System;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Options;

// Responsibility: Provides the shared local NavMesh/path/clearance probes used by HOLD and Tactical Coordination authoring.
// Flow: Authoring points are projected to the local NavMesh, optionally path-checked from the author, then capsule-checked with the same bounded rules used by HOLD.
// Authority boundary: These are local authoring proofs only; the active runtime navigation authority independently revalidates points before runtime feedback is considered authoritative.
// Invariant: HOLD and Portal authoring use the same local spatial probes, and a local miss remains deferred rather than being treated as proof of runtime invalidity.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal static class VanguardTacticalAuthoringNavMeshProjection
{
    public static bool TryProjectLocal(Vector3 position, out Vector3 projected)
    {
        // EFT.Player.Position is a body-space anchor rather than a guaranteed NavMesh point.
        // Preserve the existing HOLD probe exactly so TC authoring cannot drift into a
        // different local geometry rule.
        var sampleRadius = Math.Max(VanguardTacticalAuthoringOptions.NavMeshProjectionMeters, 2.5f);
        if (NavMesh.SamplePosition(position, out var hit, sampleRadius, NavMesh.AllAreas))
        {
            var horizontal = new Vector2(hit.position.x - position.x, hit.position.z - position.z).magnitude;
            var vertical = Math.Abs(hit.position.y - position.y);
            if (horizontal <= 1.75f && vertical <= 2.25f)
            {
                projected = hit.position;
                return true;
            }
        }

        projected = position;
        return false;
    }

    public static bool HasCompleteLocalAuthorPath(Vector3 authorPosition, Vector3 target)
    {
        if (!TryProjectLocal(authorPosition, out var start))
        {
            return false;
        }

        var path = new NavMeshPath();
        return NavMesh.CalculatePath(start, target, NavMesh.AllAreas, path)
            && path.status == NavMeshPathStatus.PathComplete;
    }

    public static bool TryResolveCompleteLocalPath(Vector3 start, Vector3 target, out Vector3[] corners)
    {
        var path = new NavMeshPath();
        if (NavMesh.CalculatePath(start, target, NavMesh.AllAreas, path)
            && path.status == NavMeshPathStatus.PathComplete
            && path.corners != null
            && path.corners.Length >= 2)
        {
            corners = path.corners;
            return true;
        }

        corners = Array.Empty<Vector3>();
        return false;
    }

    public static bool HasStaticCapsuleClearance(Vector3 projected)
    {
        var radius = VanguardTacticalAuthoringOptions.ValidationCapsuleRadiusMeters;
        var height = Math.Max(VanguardTacticalAuthoringOptions.ValidationCapsuleHeightMeters, radius * 2.0f + 0.05f);
        var bottom = projected + Vector3.up * (radius + 0.06f);
        var top = projected + Vector3.up * (height - radius);
        var colliders = Physics.OverlapCapsule(
            bottom,
            top,
            radius,
            LayerMaskClass.HighPolyWithTerrainMask,
            QueryTriggerInteraction.Ignore);

        foreach (var collider in colliders)
        {
            if (collider == null || collider.isTrigger)
            {
                continue;
            }

            if (collider.GetComponentInParent<Player>() != null)
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
#endif
