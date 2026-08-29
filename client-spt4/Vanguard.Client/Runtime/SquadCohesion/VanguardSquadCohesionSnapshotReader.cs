#if SPT_CLIENT
using System;
using System.Linq;
using UnityEngine;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Reads and normalizes live evidence for Squad Cohesion Snapshot Reader in the squad-cohesion analysis.
// Flow: Live EFT/Fika/Vanguard objects are inspected defensively, normalized into a bounded snapshot, then handed to policy/decision code.
// Authority boundary: Read-only observer; it does not create missing truth or mutate the game state it inspects.
// Invariant: Missing/stale evidence degrades explicitly and reader failures must not silently fabricate an actionable state.
namespace Vanguard.Client.Runtime.Decision;

internal sealed partial class VanguardOperatorDecisionSnapshotBuilder
{
    private VanguardSquadCohesionSnapshot CaptureSquadCohesion(
        VanguardRaidOperatorRuntimeRecord record,
        bool alive,
        Vector3 operatorPosition,
        float operatorRealSpeed,
        VanguardMovementDecisionSnapshot movement,
        VanguardThreatDecisionSnapshot threat,
        VanguardSainDecisionSnapshot sain,
        VanguardBrainDecisionSnapshot brain)
    {
        if (!alive)
        {
            return new VanguardSquadCohesionSnapshot
            {
                Enabled = true,
                ReadOnly = true,
                Classification = "cohesion_dead_operator",
                Reason = "operator_dead"
            };
        }

        var ownerAnchor = VanguardOwnerAnchorResolver.Resolve(record.OwnerProfileId, DateTimeOffset.UtcNow);
        if (!ownerAnchor.Known)
        {
            return new VanguardSquadCohesionSnapshot
            {
                Enabled = true,
                ReadOnly = true,
                OwnerKnown = false,
                OwnerProfileId = record.OwnerProfileId,
                OwnerReliableForActiveMovement = false,
                OwnerAnchorSource = ownerAnchor.Source,
                OwnerAnchorAgeSeconds = ownerAnchor.AgeSeconds,
                Classification = "cohesion_owner_unknown",
                Reason = ownerAnchor.Reason
            };
        }

        Vector3 ownerPosition = ownerAnchor.Position;
        Vector3 ownerForward = ownerAnchor.Forward;
        Vector3 toOperator = operatorPosition - ownerPosition;
        float distance = HorizontalDistance(ownerPosition, operatorPosition);
        float verticalDelta = operatorPosition.y - ownerPosition.y;
        float angle = SignedHorizontalAngle(ownerForward, toOperator);
        string sector = Vanguard.Client.Runtime.SquadCohesion.VanguardSquadCohesionDoctrine.SectorFromAngle(angle);
        string band = Vanguard.Client.Runtime.SquadCohesion.VanguardSquadCohesionDoctrine.BubbleBand(distance);
        var tacticalEnvironment = Vanguard.Client.Runtime.SquadCohesion.VanguardTacticalEnvironmentAnalyzer.Analyze(ownerPosition, ownerForward, operatorPosition, sector);
        bool inBubble = distance <= Vanguard.Client.Runtime.SquadCohesion.VanguardSquadCohesionDoctrine.TacticalBubbleRadiusMeters;
        var sameOwnerOperators = VanguardRaidOperatorRuntimeRegistry.GetOperatorsForOwner(record.OwnerProfileId)
            .Where(candidate => candidate.BotOwner is not null && !candidate.BotOwner.IsDead)
            .ToArray();
        int operatorCount = sameOwnerOperators.Length;
        int sameSectorCount = 0;
        int rearCount = 0;

        foreach (var candidate in sameOwnerOperators)
        {
            Vector3 candidatePosition = ResolvePosition(candidate.BotOwner);
            Vector3 candidateToOwner = candidatePosition - ownerPosition;
            string candidateSector = Vanguard.Client.Runtime.SquadCohesion.VanguardSquadCohesionDoctrine.SectorFromAngle(SignedHorizontalAngle(ownerForward, candidateToOwner));
            if (string.Equals(candidateSector, sector, StringComparison.OrdinalIgnoreCase))
            {
                sameSectorCount++;
            }

            if (IsRearLikeSector(candidateSector))
            {
                rearCount++;
            }
        }

        bool hasPath = movement.HasPath == true;
        bool sectorDuplicate = sameSectorCount > 1 && !string.Equals(sector, "front", StringComparison.OrdinalIgnoreCase);
        bool rearOverstacked = IsRearLikeSector(sector)
            && rearCount > Vanguard.Client.Runtime.SquadCohesion.VanguardSquadCohesionDoctrine.MaxRearGuards;
        bool useful = Vanguard.Client.Runtime.SquadCohesion.VanguardSquadCohesionDoctrine.IsUsefulSectorPosition(
            inBubble,
            threat.DirectThreat,
            sector,
            sameSectorCount,
            rearCount,
            operatorRealSpeed,
            hasPath);
        string order = ResolveSquadOrderReadOnly(record);
        string sainEnvelope = Vanguard.Client.Runtime.SquadCohesion.VanguardSquadCohesionDoctrine.LocalSainEnvelope(threat.DirectThreat, order);
        string recommendation = Recommendation(inBubble, useful, sectorDuplicate, rearOverstacked, threat.DirectThreat, order);
        string classification = Classification(inBubble, useful, sectorDuplicate, rearOverstacked, threat.DirectThreat);

        return new VanguardSquadCohesionSnapshot
        {
            Enabled = true,
            ReadOnly = true,
            OwnerKnown = true,
            OwnerProfileId = record.OwnerProfileId,
            OwnerReliableForActiveMovement = ownerAnchor.ReliableForActiveMovement,
            OwnerAnchorSource = ownerAnchor.Source,
            OwnerAnchorAgeSeconds = ownerAnchor.AgeSeconds,
            OwnerPosition = ownerPosition,
            OwnerForward = ownerForward,
            OperatorDistanceToOwner = distance,
            VerticalDelta = verticalDelta,
            BubbleRadius = Vanguard.Client.Runtime.SquadCohesion.VanguardSquadCohesionDoctrine.TacticalBubbleRadiusMeters,
            BubbleBand = band,
            InBubble = inBubble,
            Sector = sector,
            TacticalRole = Vanguard.Client.Runtime.SquadCohesion.VanguardSquadCohesionDoctrine.TacticalRoleForSector(sector),
            TacticalEnvironmentKind = tacticalEnvironment.EnvironmentKind,
            TacticalPlacementMode = tacticalEnvironment.PlacementMode,
            CorridorLike = tacticalEnvironment.CorridorLike,
            WideLateralAllowed = tacticalEnvironment.WideLateralAllowed,
            AdjacentRoomAllowed = tacticalEnvironment.AdjacentRoomAllowed,
            SectorTopologyValid = tacticalEnvironment.TopologyValid,
            SectorTopologyReason = tacticalEnvironment.TopologyReason,
            OwnerToOperatorDirectDistance = tacticalEnvironment.DirectDistance,
            OwnerToOperatorPathDistance = tacticalEnvironment.PathDistance,
            OwnerToOperatorPathRatio = tacticalEnvironment.PathRatio,
            OwnerToOperatorPathCorners = tacticalEnvironment.PathCorners,
            SignedAngleFromOwnerForward = angle,
            SameOwnerOperatorCount = operatorCount,
            SameSectorCount = sameSectorCount,
            RearSectorCount = rearCount,
            SectorDuplicate = sectorDuplicate,
            RearOverstacked = rearOverstacked,
            UsefulPosition = useful,
            DirectThreat = threat.DirectThreat,
            SainEnvelope = sainEnvelope,
            SquadOrder = order,
            RecommendedIntent = recommendation,
            Classification = classification,
            Reason = Reason(inBubble, useful, sectorDuplicate, rearOverstacked, threat, sain, brain) + ";ownerAnchor=" + ownerAnchor.Source + ";ownerReliable=" + ownerAnchor.ReliableForActiveMovement + ";env=" + tacticalEnvironment.EnvironmentKind + ";placement=" + tacticalEnvironment.PlacementMode + ";topology=" + tacticalEnvironment.TopologyReason + ";pathRatio=" + tacticalEnvironment.PathRatio.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private VanguardSquadCohesionSnapshot CaptureSquadCohesionFast(
        VanguardRaidOperatorRuntimeRecord record,
        bool alive,
        Vector3 operatorPosition,
        float operatorRealSpeed,
        VanguardMovementDecisionSnapshot movement,
        VanguardThreatDecisionSnapshot threat,
        VanguardSainDecisionSnapshot sain,
        VanguardBrainDecisionSnapshot brain,
        VanguardSquadCohesionSnapshot cached)
    {
        if (!alive)
        {
            return CaptureSquadCohesion(record, alive, operatorPosition, operatorRealSpeed, movement, threat, sain, brain);
        }

        var ownerAnchor = VanguardOwnerAnchorResolver.Resolve(record.OwnerProfileId, DateTimeOffset.UtcNow);
        if (!ownerAnchor.Known)
        {
            return new VanguardSquadCohesionSnapshot
            {
                Enabled = true, ReadOnly = true, OwnerKnown = false, OwnerProfileId = record.OwnerProfileId,
                OwnerReliableForActiveMovement = false, OwnerAnchorSource = ownerAnchor.Source,
                OwnerAnchorAgeSeconds = ownerAnchor.AgeSeconds, Classification = "cohesion_owner_unknown", Reason = ownerAnchor.Reason
            };
        }

        Vector3 ownerPosition = ownerAnchor.Position;
        Vector3 ownerForward = ownerAnchor.Forward;
        Vector3 toOperator = operatorPosition - ownerPosition;
        float distance = HorizontalDistance(ownerPosition, operatorPosition);
        float verticalDelta = operatorPosition.y - ownerPosition.y;
        float angle = SignedHorizontalAngle(ownerForward, toOperator);
        string sector = Vanguard.Client.Runtime.SquadCohesion.VanguardSquadCohesionDoctrine.SectorFromAngle(angle);
        string band = Vanguard.Client.Runtime.SquadCohesion.VanguardSquadCohesionDoctrine.BubbleBand(distance);
        bool inBubble = distance <= Vanguard.Client.Runtime.SquadCohesion.VanguardSquadCohesionDoctrine.TacticalBubbleRadiusMeters;
        bool hasPath = movement.HasPath == true;
        bool useful = Vanguard.Client.Runtime.SquadCohesion.VanguardSquadCohesionDoctrine.IsUsefulSectorPosition(
            inBubble, threat.DirectThreat, sector, cached.SameSectorCount, cached.RearSectorCount, operatorRealSpeed, hasPath);
        string order = ResolveSquadOrderReadOnly(record);
        string recommendation = Recommendation(inBubble, useful, cached.SectorDuplicate, cached.RearOverstacked, threat.DirectThreat, order);
        string classification = Classification(inBubble, useful, cached.SectorDuplicate, cached.RearOverstacked, threat.DirectThreat);

        return new VanguardSquadCohesionSnapshot
        {
            Enabled = true, ReadOnly = true, OwnerKnown = true, OwnerProfileId = record.OwnerProfileId,
            OwnerReliableForActiveMovement = ownerAnchor.ReliableForActiveMovement, OwnerAnchorSource = ownerAnchor.Source,
            OwnerAnchorAgeSeconds = ownerAnchor.AgeSeconds, OwnerPosition = ownerPosition, OwnerForward = ownerForward,
            OperatorDistanceToOwner = distance, VerticalDelta = verticalDelta, BubbleRadius = cached.BubbleRadius,
            BubbleBand = band, InBubble = inBubble, Sector = sector,
            TacticalRole = Vanguard.Client.Runtime.SquadCohesion.VanguardSquadCohesionDoctrine.TacticalRoleForSector(sector),
            TacticalEnvironmentKind = cached.TacticalEnvironmentKind, TacticalPlacementMode = cached.TacticalPlacementMode,
            CorridorLike = cached.CorridorLike, WideLateralAllowed = cached.WideLateralAllowed, AdjacentRoomAllowed = cached.AdjacentRoomAllowed,
            SectorTopologyValid = cached.SectorTopologyValid, SectorTopologyReason = cached.SectorTopologyReason,
            OwnerToOperatorDirectDistance = distance, OwnerToOperatorPathDistance = cached.OwnerToOperatorPathDistance,
            OwnerToOperatorPathRatio = cached.OwnerToOperatorPathRatio, OwnerToOperatorPathCorners = cached.OwnerToOperatorPathCorners,
            SignedAngleFromOwnerForward = angle, SameOwnerOperatorCount = cached.SameOwnerOperatorCount,
            SameSectorCount = cached.SameSectorCount, RearSectorCount = cached.RearSectorCount, SectorDuplicate = cached.SectorDuplicate,
            RearOverstacked = cached.RearOverstacked, UsefulPosition = useful, DirectThreat = threat.DirectThreat,
            SainEnvelope = Vanguard.Client.Runtime.SquadCohesion.VanguardSquadCohesionDoctrine.LocalSainEnvelope(threat.DirectThreat, order),
            SquadOrder = order, RecommendedIntent = recommendation, Classification = classification,
            Reason = Reason(inBubble, useful, cached.SectorDuplicate, cached.RearOverstacked, threat, sain, brain)
                + ";extendedCached=true;ownerAnchor=" + ownerAnchor.Source
        };
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static float SignedHorizontalAngle(Vector3 forward, Vector3 direction)
    {
        Vector3 flatForward = Vector3.ProjectOnPlane(forward, Vector3.up);
        Vector3 flatDirection = Vector3.ProjectOnPlane(direction, Vector3.up);
        if (flatForward.sqrMagnitude <= 0.001f || flatDirection.sqrMagnitude <= 0.001f)
        {
            return 0f;
        }

        return Vector3.SignedAngle(flatForward.normalized, flatDirection.normalized, Vector3.up);
    }

    private static bool IsRearLikeSector(string? sector)
    {
        return !string.IsNullOrWhiteSpace(sector)
            && sector.IndexOf("rear", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string ResolveSquadOrderReadOnly(VanguardRaidOperatorRuntimeRecord record)
    {
        object? command = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(record.BotOwner, "VanguardSquadOrder", "SquadOrder", "CurrentOrder");
        string text = command?.ToString() ?? "tactical";
        if (string.IsNullOrWhiteSpace(text))
        {
            return "tactical";
        }

        text = text.Trim().ToLowerInvariant();
        if (text.Contains("regroup", StringComparison.OrdinalIgnoreCase))
        {
            return "regroup";
        }

        if (text.Contains("follow", StringComparison.OrdinalIgnoreCase))
        {
            return "follow";
        }

        if (text.Contains("hold", StringComparison.OrdinalIgnoreCase))
        {
            return "hold";
        }

        if (text.Contains("go", StringComparison.OrdinalIgnoreCase) || text.Contains("advance", StringComparison.OrdinalIgnoreCase) || text.Contains("assault", StringComparison.OrdinalIgnoreCase))
        {
            return "go";
        }

        return "tactical";
    }

    private static string Recommendation(bool inBubble, bool useful, bool duplicate, bool rearOverstacked, bool directThreat, string order)
    {
        if (!inBubble)
        {
            return "CatchUpToTacticalBubbleReadOnly";
        }

        if (directThreat)
        {
            return "HoldLocalCombatSectorReadOnly";
        }

        if (duplicate || rearOverstacked)
        {
            return "ReviewSectorDistributionReadOnly";
        }

        if (useful)
        {
            return "HoldUsefulSectorReadOnly";
        }

        return string.Equals(order, "regroup", StringComparison.OrdinalIgnoreCase)
            ? "RegroupCohesionReadOnly"
            : "MaintainTacticalBubbleReadOnly";
    }

    private static string Classification(bool inBubble, bool useful, bool duplicate, bool rearOverstacked, bool directThreat)
    {
        if (!inBubble)
        {
            return "cohesion_outside_bubble";
        }

        if (directThreat)
        {
            return "cohesion_local_combat_sector";
        }

        if (rearOverstacked)
        {
            return "cohesion_rear_overstacked";
        }

        if (duplicate)
        {
            return "cohesion_sector_duplicate";
        }

        return useful ? "cohesion_useful_in_bubble" : "cohesion_review_needed";
    }

    private static string Reason(
        bool inBubble,
        bool useful,
        bool duplicate,
        bool rearOverstacked,
        VanguardThreatDecisionSnapshot threat,
        VanguardSainDecisionSnapshot sain,
        VanguardBrainDecisionSnapshot brain)
    {
        if (!inBubble)
        {
            return "outside_75m_tactical_bubble";
        }

        if (threat.DirectThreat)
        {
            return "direct_threat_local_sain_allowed";
        }

        if (rearOverstacked)
        {
            return "more_than_one_rear_guard";
        }

        if (duplicate)
        {
            return "same_sector_duplicate";
        }

        if (useful)
        {
            return "operator_position_useful_no_apply_needed";
        }

        return $"review_needed;sain={sain.Classification};brain={brain.Classification}";
    }
}
#endif
