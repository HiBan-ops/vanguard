#if SPT_CLIENT
using System;
using System.Collections.Generic;
using UnityEngine;

// Responsibility: Resolves player-authored PassagePath control points into a derived local NavMesh polyline without mutating the authored intent.
// Flow: Ordered authored control points are projected, clearance-checked and connected segment-by-segment; complete local evidence produces a derived polyline while any local miss remains deferred.
// Authority boundary: This is local authoring evidence only. It never moves Operators and it never upgrades local evidence into universal runtime navigation truth.
// Invariant: Authored control points survive every resolution failure unchanged, derived data can always be regenerated, and incomplete local evidence is represented explicitly rather than guessed.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal static class VanguardTacticalCoordinationPassagePathResolver
{
    private const float DuplicateCornerDistanceMeters = 0.05f;

    public static VanguardTacticalCoordinationPassageResolution Resolve(IReadOnlyList<Vector3> authoredControlPoints)
    {
        var result = new VanguardTacticalCoordinationPassageResolution();
        if (authoredControlPoints is null || authoredControlPoints.Count < 2)
        {
            result.State = VanguardTacticalAuthoringValidationState.NotValidated;
            result.Source = "insufficient_authored_control_points";
            result.Notes = "passage_path_requires_at_least_two_control_points";
            return result;
        }

        var reasons = new List<string>();
        var projected = new List<Vector3?>(authoredControlPoints.Count);
        bool projectionDeferred = false;
        bool clearanceWarning = false;

        for (var index = 0; index < authoredControlPoints.Count; index++)
        {
            var authored = authoredControlPoints[index];
            if (!VanguardTacticalAuthoringNavMeshProjection.TryProjectLocal(authored, out var navPoint))
            {
                projected.Add(null);
                projectionDeferred = true;
                reasons.Add($"control_point_{index + 1}_projection_deferred_to_authoritative_runtime");
                continue;
            }

            projected.Add(navPoint);
            reasons.Add($"control_point_{index + 1}_local_projection_ok");
            if (!VanguardTacticalAuthoringNavMeshProjection.HasStaticCapsuleClearance(navPoint))
            {
                clearanceWarning = true;
                reasons.Add($"control_point_{index + 1}_local_clearance_warning");
            }
        }

        result.ProjectedControlPoints.AddRange(projected);
        if (projectionDeferred)
        {
            reasons.Add("local_polyline_resolution_deferred");
            reasons.Add("authoritative_runtime_revalidation_required");
            result.State = VanguardTacticalAuthoringValidationState.Warning;
            result.Source = "local_navmesh_resolution_deferred";
            result.Notes = string.Join(";", reasons);
            return result;
        }

        var polyline = new List<Vector3>();
        bool segmentDeferred = false;
        for (var index = 0; index < projected.Count - 1; index++)
        {
            var start = projected[index].GetValueOrDefault();
            var end = projected[index + 1].GetValueOrDefault();
            if (!VanguardTacticalAuthoringNavMeshProjection.TryResolveCompleteLocalPath(start, end, out var corners))
            {
                segmentDeferred = true;
                reasons.Add($"segment_{index + 1}_local_path_incomplete_deferred_to_authoritative_runtime");
                break;
            }

            reasons.Add($"segment_{index + 1}_local_path_complete");
            AppendCorners(polyline, corners);
        }

        if (segmentDeferred || polyline.Count < 2)
        {
            reasons.Add("local_polyline_resolution_deferred");
            reasons.Add("authoritative_runtime_revalidation_required");
            result.State = VanguardTacticalAuthoringValidationState.Warning;
            result.Source = "local_navmesh_resolution_deferred";
            result.Notes = string.Join(";", reasons);
            return result;
        }

        result.ResolvedPolyline.AddRange(polyline);
        result.HasCompletePolyline = true;
        result.State = clearanceWarning
            ? VanguardTacticalAuthoringValidationState.Warning
            : VanguardTacticalAuthoringValidationState.Valid;
        result.Source = clearanceWarning
            ? "local_navmesh_path_complete_with_clearance_warning"
            : "local_navmesh_path_complete";
        reasons.Add("local_polyline_resolution_complete");
        reasons.Add("authoritative_runtime_revalidation_required");
        result.Notes = string.Join(";", reasons);
        return result;
    }

    private static void AppendCorners(List<Vector3> destination, IReadOnlyList<Vector3> corners)
    {
        for (var index = 0; index < corners.Count; index++)
        {
            var corner = corners[index];
            if (destination.Count > 0
                && Vector3.Distance(destination[destination.Count - 1], corner) <= DuplicateCornerDistanceMeters)
            {
                continue;
            }
            destination.Add(corner);
        }
    }
}

internal sealed class VanguardTacticalCoordinationPassageResolution
{
    public List<Vector3?> ProjectedControlPoints { get; } = new();
    public List<Vector3> ResolvedPolyline { get; } = new();
    public bool HasCompletePolyline { get; set; }
    public VanguardTacticalAuthoringValidationState State { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}
#endif
