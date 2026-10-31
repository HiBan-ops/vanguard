#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.TacticalAuthoring;

// Responsibility: resolves one bounded authored interior-development step from current TC PassagePath/Portal topology without creating a room-clearing state machine.
// Flow: fresh exact-threat squad snapshots -> materially credible support partner -> current authored PassagePath geometry -> one reachable bounded segment -> room-to-room/corridor/level classification -> disposable TacticalOpportunity lease.
// Authority boundary: planning/readback only. This service never selects or mutates targets, aim, fire, SAIN decisions, doors, inventory or persistent authoring data; physical movement remains owned by TacticalOpportunityMovementService through the existing SAIN BotPathDataManual bridge.
// Invariant: one lease may traverse at most one authored portal, never chains itself into another room, never enters danger-close geometry merely to satisfy a plan, and becomes stale immediately when topology revision/hash, exact threat, support credibility or passage attachment changes.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class InteriorDevelopOpportunityService
{
    public const string StatusTag = "VANGUARD_TC_INTERIOR_DEVELOP_OPPORTUNITY_STATUS";
    public const string MovementKind = "InteriorDevelop";

    private const float MaximumPassageAttachmentMeters = 5.50f;
    private const float RevalidationPassageAttachmentMeters = 7.50f;
    private const float DesiredStepMeters = 7.00f;
    private const float MinimumStepMeters = 3.00f;
    private const float MaximumStepMeters = 11.50f;
    private const float MaximumPreparedPathMeters = 14.00f;
    private const float PortalRelationMeters = 2.40f;
    private const float MinimumThreatStandoffMeters = 8.00f;
    private const float MaximumThreatApproachMeters = 4.50f;
    private const float LevelTransitionMeters = 1.15f;
    private const float MaximumSupportSeparationMeters = 32.00f;

    internal static bool TryResolve(
        IReadOnlyList<OperatorDecisionSnapshot> members,
        DateTimeOffset now,
        out InteriorDevelopOpportunity opportunity,
        out string reason)
    {
        opportunity = InteriorDevelopOpportunity.Empty;
        reason = "not_evaluated";
        if (members == null || members.Count < 2)
        {
            reason = "squad_support_missing";
            return false;
        }
        if (!VanguardTacticalCoordinationRuntimeReadService.TryGetSnapshot(out VanguardTacticalCoordinationReadSnapshot topology)
            || !topology.IsAvailable)
        {
            reason = "authored_topology_unavailable";
            return false;
        }

        InteriorDevelopCandidate? best = null;
        foreach (OperatorDecisionSnapshot mover in members
            .Where(item => item != null && item.Alive && item.Threat.HasThreat && item.Threat.ThreatAnchorPosition.HasValue)
            .OrderBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase))
        {
            if (!VanguardOperatorDecisionSnapshotService.IsFresh(mover, now, out _)
                || mover.Sain == null
                || mover.Sain.Active != true
                || mover.Sain.HasEnemy != true
                || mover.Sain.IsInCombat != true
                || mover.Sain.RunningToCover == true
                || mover.GrenadeHazard.HasRelevantHazard
                || mover.Medical.Actionability.GrenadeThrowing
                || mover.Medical.Actionability.AnyMedicineUsing
                || mover.Medical.Actionability.Reloading)
            {
                continue;
            }
            if (!MultiThreatOffensiveSafetyPolicy.IsSupportMateriallyReady(mover, now, out string moverMaterial))
            {
                continue;
            }
            InteriorMultiAxisCompositionContext multiAxis = InteriorMultiAxisCompositionPolicy.Resolve(mover, now, topology);
            if (!TrySelectSupportForInterior(members, mover, now, multiAxis, out OperatorDecisionSnapshot support, out string supportSummary))
            {
                continue;
            }
            if (!MultiThreatOffensiveSafetyPolicy.IsOffensiveTranslationSafeWithProspectiveSupport(
                    mover,
                    support,
                    mover.Threat.EnemyId,
                    MovementKind,
                    now,
                    out string multiThreatReason))
            {
                continue;
            }

            Vector3? threatAnchor = mover.Threat.ThreatAnchorPosition;
            if (!threatAnchor.HasValue)
            {
                continue;
            }
            Vector3 threatAnchorPosition = threatAnchor.Value;

            int viableEntryCount = CountDistinctViableEntries(mover, topology);
            foreach (VanguardTacticalCoordinationReadPassagePath passage in topology.PassagePaths
                .Where(IsUsablePassage)
                .OrderBy(item => item.PassagePathId, StringComparer.Ordinal))
            {
                if (!TryBuildCandidates(
                        mover,
                        support,
                        topology,
                        passage,
                        threatAnchorPosition,
                        moverMaterial,
                        supportSummary,
                        multiThreatReason,
                        viableEntryCount,
                        multiAxis,
                        out InteriorDevelopCandidate forward,
                        out InteriorDevelopCandidate backward))
                {
                    continue;
                }

                if (forward.Valid && (!best.HasValue || forward.Score > best.Value.Score))
                {
                    best = forward;
                }
                if (backward.Valid && (!best.HasValue || backward.Score > best.Value.Score))
                {
                    best = backward;
                }
            }
        }

        if (!best.HasValue)
        {
            reason = "no_bounded_authored_interior_step";
            return false;
        }

        InteriorDevelopCandidate selected = best.Value;
        string contextId = BuildContextId(
            topology.Revision,
            topology.ContentHash,
            selected.PassagePathId,
            selected.PortalId,
            selected.GeometryKind,
            selected.MultiAxisToken);
        opportunity = new InteriorDevelopOpportunity(
            selected.Mover,
            selected.Support.BotProfileId,
            contextId,
            selected.PassagePathId,
            selected.PortalId,
            selected.GeometryKind,
            selected.Destination,
            selected.PathLengthMeters,
            selected.VerticalDeltaMeters,
            selected.ThreatDistanceBeforeMeters,
            selected.ThreatDistanceAfterMeters,
            selected.ViableEntryCount,
            selected.Score,
            selected.Summary);
        reason = "interior_develop_selected:" + selected.GeometryKind;
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
        reason = "interior_context_not_evaluated";
        if (!TryParseContextId(contextId, out long revision, out string contentHash, out string passagePathId, out _, out _, out string multiAxisToken))
        {
            reason = "context_id_invalid";
            return false;
        }
        if (!VanguardTacticalCoordinationRuntimeReadService.TryGetSnapshot(out VanguardTacticalCoordinationReadSnapshot topology)
            || topology.Revision != revision
            || !string.Equals(topology.ContentHash, contentHash, StringComparison.Ordinal))
        {
            reason = "topology_generation_changed";
            return false;
        }
        VanguardTacticalCoordinationReadPassagePath? passage = topology.PassagePaths.FirstOrDefault(item =>
            string.Equals(item.PassagePathId, passagePathId, StringComparison.Ordinal));
        if (passage == null || !IsUsablePassage(passage))
        {
            reason = "passage_missing_or_invalid";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(moverBotProfileId, out OperatorDecisionSnapshot mover)
            || mover == null
            || !mover.Alive
            || !VanguardOperatorDecisionSnapshotService.IsFresh(mover, now, out _)
            || !string.Equals(mover.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(NormalizeThreat(mover.Threat.EnemyId), NormalizeThreat(threatId), StringComparison.OrdinalIgnoreCase))
        {
            reason = "mover_identity_or_threat_changed";
            return false;
        }
        if (!TryDistanceToPolyline(mover.Position, passage.ResolvedPolyline, out float passageDistance)
            || passageDistance > RevalidationPassageAttachmentMeters)
        {
            reason = "mover_detached_from_passage:" + passageDistance.ToString("0.0", CultureInfo.InvariantCulture);
            return false;
        }
        if (!mover.Threat.ThreatAnchorPosition.HasValue)
        {
            reason = "threat_anchor_lost";
            return false;
        }
        float currentThreatDistance = HorizontalDistance(mover.Position, mover.Threat.ThreatAnchorPosition.Value);
        float destinationThreatDistance = HorizontalDistance(destination, mover.Threat.ThreatAnchorPosition.Value);
        if (destinationThreatDistance < MinimumThreatStandoffMeters
            || currentThreatDistance - destinationThreatDistance > MaximumThreatApproachMeters)
        {
            reason = "destination_threat_geometry_changed:before="
                + currentThreatDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + ":after=" + destinationThreatDistance.ToString("0.0", CultureInfo.InvariantCulture);
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(supportBotProfileId, out OperatorDecisionSnapshot support))
        {
            reason = "support_snapshot_missing";
            return false;
        }
        if (!IsCredibleSupportForInterior(mover, support, now, out string supportReason))
        {
            reason = "support_not_current:" + supportReason;
            return false;
        }
        if (!MultiThreatOffensiveSafetyPolicy.IsOffensiveTranslationSafeWithProspectiveSupport(
                mover,
                support,
                threatId,
                MovementKind,
                now,
                out string multiThreatReason))
        {
            reason = "multi_threat_safety_changed:" + multiThreatReason;
            return false;
        }
        if (!InteriorMultiAxisCompositionPolicy.IsContextCurrent(mover, multiAxisToken, now, topology, out string multiAxisReason))
        {
            reason = "multi_axis_context_changed:" + multiAxisReason;
            return false;
        }

        reason = "current_authored_passage_support_and_threat;multiAxis=" + Safe(multiAxisReason);
        return true;
    }

    private static bool TryBuildCandidates(
        OperatorDecisionSnapshot mover,
        OperatorDecisionSnapshot support,
        VanguardTacticalCoordinationReadSnapshot topology,
        VanguardTacticalCoordinationReadPassagePath passage,
        Vector3 threatPosition,
        string moverMaterial,
        string supportSummary,
        string multiThreatReason,
        int viableEntryCount,
        InteriorMultiAxisCompositionContext multiAxis,
        out InteriorDevelopCandidate forward,
        out InteriorDevelopCandidate backward)
    {
        forward = InteriorDevelopCandidate.Invalid;
        backward = InteriorDevelopCandidate.Invalid;
        IReadOnlyList<Vector3> points = passage.ResolvedPolyline;
        if (points == null || points.Count < 2
            || !TryProjectOnPolyline(points, mover.Position, out float currentProgress, out float attachment, out _, out float totalLength)
            || attachment > MaximumPassageAttachmentMeters
            || totalLength < MinimumStepMeters + 0.5f)
        {
            return false;
        }

        forward = BuildCandidate(mover, support, topology, passage, threatPosition, moverMaterial, supportSummary, multiThreatReason, viableEntryCount, multiAxis, points, currentProgress, totalLength, +1f);
        backward = BuildCandidate(mover, support, topology, passage, threatPosition, moverMaterial, supportSummary, multiThreatReason, viableEntryCount, multiAxis, points, currentProgress, totalLength, -1f);
        return forward.Valid || backward.Valid;
    }

    private static InteriorDevelopCandidate BuildCandidate(
        OperatorDecisionSnapshot mover,
        OperatorDecisionSnapshot support,
        VanguardTacticalCoordinationReadSnapshot topology,
        VanguardTacticalCoordinationReadPassagePath passage,
        Vector3 threatPosition,
        string moverMaterial,
        string supportSummary,
        string multiThreatReason,
        int viableEntryCount,
        InteriorMultiAxisCompositionContext multiAxis,
        IReadOnlyList<Vector3> points,
        float currentProgress,
        float totalLength,
        float direction)
    {
        float desiredProgress = Math.Max(0f, Math.Min(totalLength, currentProgress + direction * DesiredStepMeters));
        float authoredStep = Math.Abs(desiredProgress - currentProgress);
        if (authoredStep < MinimumStepMeters || authoredStep > MaximumStepMeters)
        {
            return InteriorDevelopCandidate.Invalid;
        }

        Vector3 destination = PointAlongPath(points, desiredProgress);
        float directStep = Vector3.Distance(mover.Position, destination);
        if (directStep < MinimumStepMeters || directStep > MaximumStepMeters + 2.0f)
        {
            return InteriorDevelopCandidate.Invalid;
        }
        if (!VanguardTacticalPlacementSolver.TryCalculatePreparedRuntimePath(
                mover.Position,
                destination,
                out NavMeshPath preparedPath,
                out float pathLength,
                out string pathStatus)
            || pathLength < MinimumStepMeters
            || pathLength > MaximumPreparedPathMeters)
        {
            return InteriorDevelopCandidate.Invalid;
        }

        float threatBefore = HorizontalDistance(mover.Position, threatPosition);
        float threatAfter = HorizontalDistance(destination, threatPosition);
        float threatApproach = threatBefore - threatAfter;
        if (threatAfter < MinimumThreatStandoffMeters || threatApproach > MaximumThreatApproachMeters)
        {
            return InteriorDevelopCandidate.Invalid;
        }

        List<VanguardTacticalCoordinationReadPortal> crossedPortals = topology.Portals
            .Where(IsUsablePortal)
            .Where(portal => DistanceToPreparedPath(portal.NavMeshProjectedPosition ?? portal.Position, preparedPath) <= PortalRelationMeters)
            .OrderBy(portal => DistanceToPreparedPath(portal.NavMeshProjectedPosition ?? portal.Position, preparedPath))
            .ThenBy(portal => portal.PortalId, StringComparer.Ordinal)
            .ToList();
        if (crossedPortals.Count > 1)
        {
            return InteriorDevelopCandidate.Invalid;
        }

        if (!InteriorMultiAxisCompositionPolicy.TryEvaluateDestination(
                multiAxis,
                mover.Position,
                destination,
                crossedPortals.Count == 1,
                out float multiAxisAdjustment,
                out string multiAxisSummary))
        {
            return InteriorDevelopCandidate.Invalid;
        }

        float verticalDelta = destination.y - mover.Position.y;
        string portalId = crossedPortals.Count == 1 ? crossedPortals[0].PortalId : string.Empty;
        string geometryKind = crossedPortals.Count == 1
            ? "room_to_room"
            : Math.Abs(verticalDelta) >= LevelTransitionMeters
                ? "level_transition"
                : string.Equals(mover.SquadCohesion.TacticalEnvironmentKind, "corridor", StringComparison.OrdinalIgnoreCase)
                    ? "corridor_develop"
                    : "authored_passage_develop";

        Vector3 threatAxis = Flatten(threatPosition - mover.Position);
        Vector3 moveAxis = Flatten(destination - mover.Position);
        float lateralAngle = threatAxis.sqrMagnitude > 0.01f && moveAxis.sqrMagnitude > 0.01f
            ? Vector3.Angle(threatAxis.normalized, moveAxis.normalized)
            : 0f;
        float lateralValue = Math.Min(90f, lateralAngle) * 0.18f;
        float supportDistance = HorizontalDistance(destination, support.Position);
        float supportPenalty = Math.Max(0f, supportDistance - 18f) * 0.35f;
        float portalBonus = crossedPortals.Count == 1 ? 22f : 0f;
        float levelBonus = Math.Abs(verticalDelta) >= LevelTransitionMeters ? 16f : 0f;
        float corridorBonus = string.Equals(geometryKind, "corridor_develop", StringComparison.OrdinalIgnoreCase) ? 8f : 0f;
        float multiEntryBonus = crossedPortals.Count == 1 && viableEntryCount >= 2
            ? Math.Min(3, viableEntryCount - 1) * 4f
            : 0f;
        float score = 90f + portalBonus + levelBonus + corridorBonus + multiEntryBonus + lateralValue + multiAxisAdjustment - pathLength * 1.6f - Math.Max(0f, threatApproach) * 3.0f - supportPenalty;
        string summary = "passage=" + Safe(passage.PassagePathId)
            + ";portal=" + Safe(portalId)
            + ";geometry=" + Safe(geometryKind)
            + ";authoredStep=" + authoredStep.ToString("0.0", CultureInfo.InvariantCulture)
            + ";path=" + pathLength.ToString("0.0", CultureInfo.InvariantCulture)
            + ";verticalDelta=" + verticalDelta.ToString("0.0", CultureInfo.InvariantCulture)
            + ";threatBefore=" + threatBefore.ToString("0.0", CultureInfo.InvariantCulture)
            + ";threatAfter=" + threatAfter.ToString("0.0", CultureInfo.InvariantCulture)
            + ";threatApproach=" + threatApproach.ToString("0.0", CultureInfo.InvariantCulture)
            + ";lateralAngle=" + lateralAngle.ToString("0.0", CultureInfo.InvariantCulture)
            + ";support=" + Safe(support.BotProfileId)
            + ";supportDistanceAfter=" + supportDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";viableEntries=" + viableEntryCount.ToString(CultureInfo.InvariantCulture)
            + ";multiEntryBonus=" + multiEntryBonus.ToString("0.0", CultureInfo.InvariantCulture)
            + ";multiAxis=" + Safe(multiAxisSummary)
            + ";moverMaterial=" + Safe(moverMaterial)
            + ";supportSummary=" + Safe(supportSummary)
            + ";multiThreat=" + Safe(multiThreatReason)
            + ";pathStatus=" + Safe(pathStatus);

        return new InteriorDevelopCandidate(
            true,
            mover,
            support,
            passage.PassagePathId,
            portalId,
            geometryKind,
            destination,
            pathLength,
            verticalDelta,
            threatBefore,
            threatAfter,
            viableEntryCount,
            score,
            multiAxis.Valid ? multiAxis.ContextToken : "none",
            summary);
    }

    internal static bool TrySelectSupportForInterior(
        IReadOnlyList<OperatorDecisionSnapshot> members,
        OperatorDecisionSnapshot mover,
        DateTimeOffset now,
        InteriorMultiAxisCompositionContext multiAxis,
        out OperatorDecisionSnapshot support,
        out string summary)
    {
        support = OperatorDecisionSnapshot.Empty;
        summary = "support_not_found";
        foreach (OperatorDecisionSnapshot candidate in members
            .Where(item => item != null && !string.Equals(item.BotProfileId, mover.BotProfileId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => InteriorMultiAxisCompositionPolicy.SupportPriority(multiAxis, item.BotProfileId, mover))
            .ThenBy(item => HorizontalDistance(item.Position, mover.Position))
            .ThenBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsCredibleSupportForInterior(mover, candidate, now, out string supportReason))
            {
                continue;
            }
            support = candidate;
            summary = supportReason;
            return true;
        }
        return false;
    }

    internal static bool IsCredibleSupportForInterior(
        OperatorDecisionSnapshot mover,
        OperatorDecisionSnapshot support,
        DateTimeOffset now,
        out string reason)
    {
        reason = "support_not_evaluated";
        if (support == null
            || !support.Alive
            || string.IsNullOrWhiteSpace(support.BotProfileId)
            || string.Equals(support.BotProfileId, mover.BotProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(support.OwnerProfileId, mover.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(NormalizeThreat(support.Threat.EnemyId), NormalizeThreat(mover.Threat.EnemyId), StringComparison.OrdinalIgnoreCase)
            || !VanguardOperatorDecisionSnapshotService.IsFresh(support, now, out _))
        {
            reason = "support_identity_or_snapshot_not_current";
            return false;
        }
        if (support.Sain == null
            || support.Sain.Active != true
            || support.Sain.HasEnemy != true
            || support.Sain.IsInCombat != true
            || support.Sain.RunningToCover == true
            || support.GrenadeHazard.HasRelevantHazard
            || support.Medical.Actionability.GrenadeThrowing
            || support.Medical.Actionability.AnyMedicineUsing
            || support.Medical.Actionability.Reloading)
        {
            reason = "support_local_combat_or_survival_state_not_ready";
            return false;
        }
        float separation = HorizontalDistance(mover.Position, support.Position);
        if (separation > MaximumSupportSeparationMeters)
        {
            reason = "support_separation_exceeded:" + separation.ToString("0.0", CultureInfo.InvariantCulture);
            return false;
        }
        if (!MultiThreatOffensiveSafetyPolicy.IsSupportMateriallyReady(support, now, out string materialReason))
        {
            reason = "support_material_not_ready:" + Safe(materialReason);
            return false;
        }
        if (TacticalOpportunityMovementService.HasActiveOpportunityForBot(support.BotProfileId)
            && !TacticalOpportunityMovementService.TryGetActiveSupportContributionForBot(support.BotProfileId, mover.Threat.EnemyId, out _))
        {
            reason = "support_committed_to_non_support_tactical_opportunity";
            return false;
        }

        reason = "credible_exact_threat_support:separation=" + separation.ToString("0.0", CultureInfo.InvariantCulture)
            + ":material=" + Safe(materialReason);
        return true;
    }

    private static int CountDistinctViableEntries(
        OperatorDecisionSnapshot mover,
        VanguardTacticalCoordinationReadSnapshot topology)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VanguardTacticalCoordinationReadPassagePath passage in topology.PassagePaths.Where(IsUsablePassage))
        {
            if (!TryDistanceToPolyline(mover.Position, passage.ResolvedPolyline, out float distance)
                || distance > MaximumPassageAttachmentMeters)
            {
                continue;
            }
            foreach (VanguardTacticalCoordinationReadPortal portal in topology.Portals.Where(IsUsablePortal))
            {
                Vector3 point = portal.NavMeshProjectedPosition ?? portal.Position;
                if (DistanceToPolyline(point, passage.ResolvedPolyline) <= PortalRelationMeters + 0.5f)
                {
                    ids.Add(portal.PortalId);
                }
            }
        }
        return ids.Count;
    }

    private static bool IsUsablePassage(VanguardTacticalCoordinationReadPassagePath passage)
        => passage != null
            && passage.Enabled
            && passage.ValidationState != VanguardTacticalAuthoringValidationState.Invalid
            && passage.ResolvedPolyline != null
            && passage.ResolvedPolyline.Count >= 2;

    private static bool IsUsablePortal(VanguardTacticalCoordinationReadPortal portal)
        => portal != null
            && portal.Enabled
            && portal.ValidationState != VanguardTacticalAuthoringValidationState.Invalid;

    private static string BuildContextId(long revision, string contentHash, string passagePathId, string portalId, string geometryKind, string multiAxisToken)
        => revision.ToString(CultureInfo.InvariantCulture)
            + "~" + (contentHash ?? string.Empty)
            + "~" + EscapeContext(passagePathId)
            + "~" + EscapeContext(portalId)
            + "~" + EscapeContext(geometryKind)
            + "~" + EscapeContext(multiAxisToken);

    private static bool TryParseContextId(string contextId, out long revision, out string contentHash, out string passagePathId, out string portalId, out string geometryKind, out string multiAxisToken)
    {
        revision = 0;
        contentHash = string.Empty;
        passagePathId = string.Empty;
        portalId = string.Empty;
        geometryKind = string.Empty;
        multiAxisToken = "none";
        string[] parts = (contextId ?? string.Empty).Split('~');
        if ((parts.Length != 5 && parts.Length != 6) || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out revision))
        {
            return false;
        }
        contentHash = parts[1];
        passagePathId = UnescapeContext(parts[2]);
        portalId = UnescapeContext(parts[3]);
        geometryKind = UnescapeContext(parts[4]);
        if (parts.Length == 6)
        {
            multiAxisToken = UnescapeContext(parts[5]);
        }
        return revision > 0 && !string.IsNullOrWhiteSpace(contentHash) && !string.IsNullOrWhiteSpace(passagePathId);
    }

    private static string EscapeContext(string? value) => (value ?? string.Empty).Replace("~", "_");
    private static string UnescapeContext(string? value) => value ?? string.Empty;

    private static bool TryProjectOnPolyline(
        IReadOnlyList<Vector3> points,
        Vector3 point,
        out float progress,
        out float separation,
        out Vector3 projected,
        out float totalLength)
    {
        progress = 0f;
        separation = float.MaxValue;
        projected = Vector3.zero;
        totalLength = 0f;
        if (points == null || points.Count < 2)
        {
            return false;
        }

        float traversed = 0f;
        for (int i = 1; i < points.Count; i++)
        {
            Vector3 a = points[i - 1];
            Vector3 b = points[i];
            Vector3 segment = b - a;
            float length = segment.magnitude;
            if (length <= 0.001f)
            {
                continue;
            }
            float t = Mathf.Clamp01(Vector3.Dot(point - a, segment) / (length * length));
            Vector3 candidate = a + segment * t;
            float distance = Vector3.Distance(point, candidate);
            if (distance < separation)
            {
                separation = distance;
                projected = candidate;
                progress = traversed + length * t;
            }
            traversed += length;
        }
        totalLength = traversed;
        return totalLength > 0.1f && separation < float.MaxValue;
    }

    private static Vector3 PointAlongPath(IReadOnlyList<Vector3> points, float targetProgress)
    {
        if (points == null || points.Count == 0)
        {
            return Vector3.zero;
        }
        float traversed = 0f;
        for (int i = 1; i < points.Count; i++)
        {
            Vector3 a = points[i - 1];
            Vector3 b = points[i];
            float length = Vector3.Distance(a, b);
            if (length <= 0.001f)
            {
                continue;
            }
            if (traversed + length >= targetProgress)
            {
                float t = Mathf.Clamp01((targetProgress - traversed) / length);
                return Vector3.Lerp(a, b, t);
            }
            traversed += length;
        }
        return points[points.Count - 1];
    }

    private static bool TryDistanceToPolyline(Vector3 point, IReadOnlyList<Vector3> points, out float distance)
    {
        distance = DistanceToPolyline(point, points);
        return distance < float.MaxValue;
    }

    private static float DistanceToPolyline(Vector3 point, IReadOnlyList<Vector3> points)
    {
        if (points == null || points.Count < 2)
        {
            return float.MaxValue;
        }
        float best = float.MaxValue;
        for (int i = 1; i < points.Count; i++)
        {
            best = Math.Min(best, DistancePointToSegment(point, points[i - 1], points[i]));
        }
        return best;
    }

    private static float DistanceToPreparedPath(Vector3 point, NavMeshPath path)
    {
        Vector3[] corners = path?.corners ?? Array.Empty<Vector3>();
        if (corners.Length < 2)
        {
            return float.MaxValue;
        }
        float best = float.MaxValue;
        for (int i = 1; i < corners.Length; i++)
        {
            best = Math.Min(best, DistancePointToSegment(point, corners[i - 1], corners[i]));
        }
        return best;
    }

    private static float DistancePointToSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        Vector3 segment = b - a;
        float lengthSq = segment.sqrMagnitude;
        if (lengthSq <= 0.001f)
        {
            return Vector3.Distance(point, a);
        }
        float t = Mathf.Clamp01(Vector3.Dot(point - a, segment) / lengthSq);
        return Vector3.Distance(point, a + segment * t);
    }

    private static Vector3 Flatten(Vector3 value)
    {
        value.y = 0f;
        return value;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b) => Vector3.Distance(Flatten(a), Flatten(b));
    private static string NormalizeThreat(string? value) => string.IsNullOrWhiteSpace(value) || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) ? string.Empty : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private readonly struct InteriorDevelopCandidate
    {
        public static readonly InteriorDevelopCandidate Invalid = new(false, OperatorDecisionSnapshot.Empty, OperatorDecisionSnapshot.Empty, string.Empty, string.Empty, string.Empty, Vector3.zero, 0f, 0f, 0f, 0f, 0, -9999f, "none", string.Empty);

        public InteriorDevelopCandidate(bool valid, OperatorDecisionSnapshot mover, OperatorDecisionSnapshot support, string passagePathId, string portalId, string geometryKind, Vector3 destination, float pathLengthMeters, float verticalDeltaMeters, float threatDistanceBeforeMeters, float threatDistanceAfterMeters, int viableEntryCount, float score, string multiAxisToken, string summary)
        {
            Valid = valid;
            Mover = mover;
            Support = support;
            PassagePathId = passagePathId;
            PortalId = portalId;
            GeometryKind = geometryKind;
            Destination = destination;
            PathLengthMeters = pathLengthMeters;
            VerticalDeltaMeters = verticalDeltaMeters;
            ThreatDistanceBeforeMeters = threatDistanceBeforeMeters;
            ThreatDistanceAfterMeters = threatDistanceAfterMeters;
            ViableEntryCount = viableEntryCount;
            Score = score;
            MultiAxisToken = multiAxisToken;
            Summary = summary;
        }

        public bool Valid { get; }
        public OperatorDecisionSnapshot Mover { get; }
        public OperatorDecisionSnapshot Support { get; }
        public string PassagePathId { get; }
        public string PortalId { get; }
        public string GeometryKind { get; }
        public Vector3 Destination { get; }
        public float PathLengthMeters { get; }
        public float VerticalDeltaMeters { get; }
        public float ThreatDistanceBeforeMeters { get; }
        public float ThreatDistanceAfterMeters { get; }
        public int ViableEntryCount { get; }
        public float Score { get; }
        public string MultiAxisToken { get; }
        public string Summary { get; }
    }
}

internal readonly struct InteriorDevelopOpportunity
{
    public static readonly InteriorDevelopOpportunity Empty = new(OperatorDecisionSnapshot.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, Vector3.zero, 0f, 0f, 0f, 0f, 0, 0f, string.Empty);

    public InteriorDevelopOpportunity(OperatorDecisionSnapshot mover, string supportBotProfileId, string contextId, string passagePathId, string portalId, string geometryKind, Vector3 destination, float pathLengthMeters, float verticalDeltaMeters, float threatDistanceBeforeMeters, float threatDistanceAfterMeters, int viableEntryCount, float score, string summary)
    {
        Mover = mover;
        SupportBotProfileId = supportBotProfileId;
        ContextId = contextId;
        PassagePathId = passagePathId;
        PortalId = portalId;
        GeometryKind = geometryKind;
        Destination = destination;
        PathLengthMeters = pathLengthMeters;
        VerticalDeltaMeters = verticalDeltaMeters;
        ThreatDistanceBeforeMeters = threatDistanceBeforeMeters;
        ThreatDistanceAfterMeters = threatDistanceAfterMeters;
        ViableEntryCount = viableEntryCount;
        Score = score;
        Summary = summary;
    }

    public OperatorDecisionSnapshot Mover { get; }
    public string SupportBotProfileId { get; }
    public string ContextId { get; }
    public string PassagePathId { get; }
    public string PortalId { get; }
    public string GeometryKind { get; }
    public Vector3 Destination { get; }
    public float PathLengthMeters { get; }
    public float VerticalDeltaMeters { get; }
    public float ThreatDistanceBeforeMeters { get; }
    public float ThreatDistanceAfterMeters { get; }
    public int ViableEntryCount { get; }
    public float Score { get; }
    public string Summary { get; }
}
#endif
