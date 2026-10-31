#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Builds the read-only Tactical Coordinator spatial view from committed topology snapshots and current Operator decision snapshots.
// Flow: At a bounded cadence on the raid authority, the service relates each alive Operator to containing/nearest Anchors, PoseSlots, Portals and PassagePath geometry, then atomically publishes an immutable observation snapshot.
// Authority boundary: This topology projection is descriptive only; it never scores execution intents, acquires movement ownership, starts leases, changes SAIN/BigBrain state, selects combat targets or writes Base/User authoring data.
// Invariant: Coordinator observations are revision-bound derived knowledge; topology or Operator evidence changes replace the whole snapshot atomically and no correctness depends on callback ordering between subsystems.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal static class VanguardTacticalCoordinatorReadOnlyTopologyService
{
    public const string StatusTag = "VANGUARD_TACTICAL_COORDINATOR_READ_ONLY_STATUS";
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(0.20d);
    private static readonly TimeSpan SummaryLogInterval = TimeSpan.FromSeconds(15.0d);
    private static readonly object Sync = new();

    private static VanguardTacticalCoordinatorReadSnapshot current = VanguardTacticalCoordinatorReadSnapshot.Empty;
    private static DateTimeOffset nextTickAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static long lastTopologyRevision;
    private static string lastTopologyHash = string.Empty;
    private static DateTimeOffset lastOperatorBatchAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    public static void Tick()
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            ClearIfPublished();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < nextTickAtUtc)
        {
            return;
        }
        nextTickAtUtc = now + TickInterval;

        if (!VanguardTacticalCoordinationRuntimeReadService.TryGetSnapshot(out var topology))
        {
            ClearIfPublished();
            return;
        }

        var decisionSnapshots = VanguardOperatorDecisionSnapshotService.GetLatestSnapshots();
        if (decisionSnapshots == null || decisionSnapshots.Count == 0)
        {
            return;
        }

        var operatorBatchAtUtc = decisionSnapshots
            .Where(item => item != null)
            .Select(item => item.CapturedAtUtc)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

        if (topology.Revision == lastTopologyRevision
            && string.Equals(topology.ContentHash, lastTopologyHash, StringComparison.Ordinal)
            && operatorBatchAtUtc <= lastOperatorBatchAtUtc)
        {
            return;
        }

        var views = decisionSnapshots
            .Where(item => item != null && item.Alive && !string.IsNullOrWhiteSpace(item.BotProfileId))
            .OrderBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase)
            .Select(item => BuildOperatorView(item, topology))
            .ToArray();

        var next = new VanguardTacticalCoordinatorReadSnapshot(
            topology.MapId,
            topology.Revision,
            topology.ContentHash,
            operatorBatchAtUtc,
            now,
            views);

        lock (Sync)
        {
            current = next;
        }

        lastTopologyRevision = topology.Revision;
        lastTopologyHash = topology.ContentHash;
        lastOperatorBatchAtUtc = operatorBatchAtUtc;

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_COORDINATOR_READ_ONLY_BOOT enabled=true; authority=raid_authority; topologyRevision={topology.Revision}; operators={views.Length}; movementAuthority=false; intentAuthority=false; sainMutation=false; persistentWrite=false; build={VanguardBuildVersion.BuildLabel}");
        }

        if (now >= nextSummaryAtUtc)
        {
            nextSummaryAtUtc = now + SummaryLogInterval;
            int insideAnchors = views.Count(item => item.IsInsideTacticalAnchor);
            int withPose = views.Count(item => item.HasPoseSlotObservation);
            int withPortal = views.Count(item => item.HasPortalObservation);
            int withPassage = views.Count(item => item.HasPassageObservation);
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_COORDINATOR_READ_ONLY_SUMMARY map={Safe(topology.MapId)}; topologyRevision={topology.Revision}; topologyHash={ShortHash(topology.ContentHash)}; operators={views.Length}; insideAnchors={insideAnchors}; poseObserved={withPose}; portalObserved={withPortal}; passageObserved={withPassage}; observationOnly=true; movementAuthority=false");
        }
    }

    public static bool TryGetSnapshot(out VanguardTacticalCoordinatorReadSnapshot snapshot)
    {
        lock (Sync)
        {
            snapshot = current;
            return snapshot.IsAvailable;
        }
    }

    public static bool TryGetOperatorView(string botProfileId, out VanguardTacticalCoordinatorOperatorTopologyView view)
    {
        view = null!;
        if (string.IsNullOrWhiteSpace(botProfileId) || !TryGetSnapshot(out var snapshot))
        {
            return false;
        }

        var candidate = snapshot.Operators.FirstOrDefault(item =>
            string.Equals(item.BotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase));
        if (candidate == null)
        {
            return false;
        }

        view = candidate;
        return true;
    }

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            current = VanguardTacticalCoordinatorReadSnapshot.Empty;
        }

        nextTickAtUtc = DateTimeOffset.MinValue;
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        lastTopologyRevision = 0;
        lastTopologyHash = string.Empty;
        lastOperatorBatchAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;

        if (!string.IsNullOrWhiteSpace(reason))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_COORDINATOR_READ_ONLY_RESET reason={Safe(reason)}; observationOnly=true");
        }
    }

    private static VanguardTacticalCoordinatorOperatorTopologyView BuildOperatorView(
        OperatorDecisionSnapshot decision,
        VanguardTacticalCoordinationReadSnapshot topology)
    {
        var position = decision.Position;

        VanguardTacticalCoordinationReadAnchor? containingAnchor = null;
        float containingAnchorDistance = float.MaxValue;
        foreach (var anchor in topology.TacticalAnchors)
        {
            if (!IsUsable(anchor.Enabled, anchor.ValidationState))
            {
                continue;
            }

            float distance = Vector3.Distance(position, anchor.Center);
            if (distance <= Math.Max(0.0f, anchor.Radius)
                && (containingAnchor == null
                    || anchor.Radius < containingAnchor.Radius
                    || (Math.Abs(anchor.Radius - containingAnchor.Radius) < 0.01f && distance < containingAnchorDistance)))
            {
                containingAnchor = anchor;
                containingAnchorDistance = distance;
            }
        }

        VanguardTacticalCoordinationReadAnchor? nearestAnchor = null;
        float nearestAnchorDistance = float.MaxValue;
        foreach (var anchor in topology.TacticalAnchors)
        {
            if (!IsUsable(anchor.Enabled, anchor.ValidationState))
            {
                continue;
            }

            float distance = Vector3.Distance(position, anchor.Center);
            if (distance < nearestAnchorDistance)
            {
                nearestAnchor = anchor;
                nearestAnchorDistance = distance;
            }
        }

        VanguardTacticalCoordinationReadAnchor? nearestPoseAnchor = null;
        VanguardTacticalCoordinationReadPoseSlot? nearestPose = null;
        float nearestPoseDistance = float.MaxValue;
        foreach (var anchor in topology.TacticalAnchors)
        {
            if (!IsUsable(anchor.Enabled, anchor.ValidationState))
            {
                continue;
            }

            foreach (var pose in anchor.PoseSlots)
            {
                if (!IsUsable(pose.Enabled, pose.ValidationState))
                {
                    continue;
                }

                float distance = Vector3.Distance(position, pose.NavMeshProjectedPosition ?? pose.Position);
                if (distance < nearestPoseDistance)
                {
                    nearestPoseAnchor = anchor;
                    nearestPose = pose;
                    nearestPoseDistance = distance;
                }
            }
        }

        VanguardTacticalCoordinationReadPortal? nearestPortal = null;
        float nearestPortalDistance = float.MaxValue;
        foreach (var portal in topology.Portals)
        {
            if (!IsUsable(portal.Enabled, portal.ValidationState))
            {
                continue;
            }

            float distance = Vector3.Distance(position, portal.NavMeshProjectedPosition ?? portal.Position);
            if (distance < nearestPortalDistance)
            {
                nearestPortal = portal;
                nearestPortalDistance = distance;
            }
        }

        VanguardTacticalCoordinationReadPassagePath? nearestPassage = null;
        float nearestPassageDistance = float.MaxValue;
        Vector3? nearestPassagePoint = null;
        string nearestPassageGeometrySource = string.Empty;
        foreach (var passage in topology.PassagePaths)
        {
            if (!IsUsable(passage.Enabled, passage.ValidationState))
            {
                continue;
            }

            IReadOnlyList<Vector3> geometry = passage.ResolvedPolyline.Count >= 2
                ? passage.ResolvedPolyline
                : passage.AuthoredControlPoints;
            string geometrySource = passage.ResolvedPolyline.Count >= 2 ? "resolved_polyline" : "authored_control_points";
            if (!TryFindNearestPointOnPolyline(position, geometry, out var point, out var distance))
            {
                continue;
            }

            if (distance < nearestPassageDistance)
            {
                nearestPassage = passage;
                nearestPassageDistance = distance;
                nearestPassagePoint = point;
                nearestPassageGeometrySource = geometrySource;
            }
        }

        return new VanguardTacticalCoordinatorOperatorTopologyView(
            decision.OperatorId,
            decision.OwnerProfileId,
            decision.BotProfileId,
            position,
            decision.CapturedAtUtc,
            containingAnchor?.AnchorId ?? string.Empty,
            containingAnchor?.Name ?? string.Empty,
            containingAnchor?.Origin ?? string.Empty,
            nearestAnchor?.AnchorId ?? string.Empty,
            nearestAnchor == null ? null : nearestAnchorDistance,
            nearestPoseAnchor?.AnchorId ?? string.Empty,
            nearestPose?.PoseSlotId ?? string.Empty,
            nearestPose == null ? null : nearestPoseDistance,
            nearestPose?.ViewDirection,
            nearestPortal?.PortalId ?? string.Empty,
            nearestPortal?.DisplayName ?? string.Empty,
            nearestPortal == null ? null : nearestPortalDistance,
            nearestPassage?.PassagePathId ?? string.Empty,
            nearestPassage?.DisplayName ?? string.Empty,
            nearestPassage == null ? null : nearestPassageDistance,
            nearestPassagePoint,
            nearestPassageGeometrySource);
    }

    private static bool TryFindNearestPointOnPolyline(
        Vector3 position,
        IReadOnlyList<Vector3> geometry,
        out Vector3 nearestPoint,
        out float distanceMeters)
    {
        nearestPoint = default;
        distanceMeters = float.MaxValue;
        if (geometry == null || geometry.Count == 0)
        {
            return false;
        }

        if (geometry.Count == 1)
        {
            nearestPoint = geometry[0];
            distanceMeters = Vector3.Distance(position, nearestPoint);
            return true;
        }

        float bestSquared = float.MaxValue;
        for (int index = 0; index < geometry.Count - 1; index++)
        {
            var point = ClosestPointOnSegment(position, geometry[index], geometry[index + 1]);
            float squared = (point - position).sqrMagnitude;
            if (squared < bestSquared)
            {
                bestSquared = squared;
                nearestPoint = point;
            }
        }

        if (bestSquared == float.MaxValue)
        {
            return false;
        }

        distanceMeters = (float)Math.Sqrt(bestSquared);
        return true;
    }

    private static Vector3 ClosestPointOnSegment(Vector3 point, Vector3 start, Vector3 end)
    {
        var segment = end - start;
        float lengthSquared = segment.sqrMagnitude;
        if (lengthSquared <= 0.0001f)
        {
            return start;
        }

        float t = Vector3.Dot(point - start, segment) / lengthSquared;
        return start + segment * Mathf.Clamp01(t);
    }

    private static bool IsUsable(bool enabled, VanguardTacticalAuthoringValidationState validationState) =>
        enabled && validationState != VanguardTacticalAuthoringValidationState.Invalid;

    private static void ClearIfPublished()
    {
        lock (Sync)
        {
            if (!current.IsAvailable)
            {
                return;
            }
            current = VanguardTacticalCoordinatorReadSnapshot.Empty;
        }

        lastTopologyRevision = 0;
        lastTopologyHash = string.Empty;
        lastOperatorBatchAtUtc = DateTimeOffset.MinValue;
        nextSummaryAtUtc = DateTimeOffset.MinValue;
    }

    private static string ShortHash(string value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Length <= 12 ? value : value.Substring(0, 12);

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(";", ",", StringComparison.Ordinal);
}
#endif
