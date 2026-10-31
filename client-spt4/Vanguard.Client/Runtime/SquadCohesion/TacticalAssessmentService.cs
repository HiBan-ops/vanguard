#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.TacticalAuthoring;

// Responsibility: Implements Tactical assessment as a deterministic read-only tactical-context layer over topology observations and matching Operator decision snapshots.
// Flow: On the raid authority, generation-matched evidence is scored into non-executable topology/context candidates, owner summaries are derived, and one immutable assessment snapshot is atomically published.
// Authority boundary: assessment is observation/scoring only; it does not emit Vanguard intents, select movement destinations, acquire leases, alter SAIN/BigBrain state, or write Base/User Tactical Coordination data.
// Invariant: Only decision evidence whose capture timestamp exactly matches the Operator topology view is scored, so correctness cannot depend on incidental callback ordering between snapshot producers.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class TacticalAssessmentService
{
    public const string StatusTag = "VANGUARD_TACTICAL_ASSESSMENT_STATUS";
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(0.20d);
    private static readonly TimeSpan SummaryLogInterval = TimeSpan.FromSeconds(15.0d);
    private static readonly object Sync = new();

    private static TacticalAssessmentSnapshot current = TacticalAssessmentSnapshot.Empty;
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

        if (!VanguardTacticalCoordinatorReadOnlyTopologyService.TryGetSnapshot(out var topology))
        {
            ClearIfPublished();
            return;
        }

        if (topology.TopologyRevision == lastTopologyRevision
            && string.Equals(topology.TopologyContentHash, lastTopologyHash, StringComparison.Ordinal)
            && topology.OperatorBatchAtUtc <= lastOperatorBatchAtUtc)
        {
            return;
        }

        var decisions = VanguardOperatorDecisionSnapshotService.GetLatestSnapshots();
        if (decisions == null || decisions.Count == 0)
        {
            return;
        }

        var decisionsByBot = decisions
            .Where(item => item != null && item.Alive && !string.IsNullOrWhiteSpace(item.BotProfileId))
            .GroupBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CapturedAtUtc).First(), StringComparer.OrdinalIgnoreCase);

        int generationMismatchCount = 0;
        var assessments = new List<TacticalOperatorAssessment>();
        foreach (var view in topology.Operators)
        {
            if (view == null
                || string.IsNullOrWhiteSpace(view.BotProfileId)
                || !decisionsByBot.TryGetValue(view.BotProfileId, out var decision))
            {
                continue;
            }

            if (decision.CapturedAtUtc != view.CapturedAtUtc)
            {
                generationMismatchCount++;
                continue;
            }

            assessments.Add(BuildOperatorAssessment(decision, view));
        }

        if (assessments.Count == 0 && topology.Operators.Count > 0)
        {
            return;
        }

        var ownerAssessments = BuildOwnerAssessments(assessments);
        var next = new TacticalAssessmentSnapshot(
            topology.MapId,
            topology.TopologyRevision,
            topology.TopologyContentHash,
            topology.OperatorBatchAtUtc,
            now,
            assessments.ToArray(),
            ownerAssessments);

        lock (Sync)
        {
            current = next;
        }

        lastTopologyRevision = topology.TopologyRevision;
        lastTopologyHash = topology.TopologyContentHash;
        lastOperatorBatchAtUtc = topology.OperatorBatchAtUtc;

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ASSESSMENT_BOOT enabled=true; mode=tactical_context_assessment; scoringOnly=true; observationOnly=true; executionAllowed=false; movementAuthority=false; intentAuthority=false; topologyRevision={topology.TopologyRevision}; operators={assessments.Count}; owners={ownerAssessments.Length}; build={VanguardBuildVersion.BuildLabel}");
        }

        if (now >= nextSummaryAtUtc)
        {
            nextSummaryAtUtc = now + SummaryLogInterval;
            int directThreat = assessments.Count(item => item.DirectThreat);
            int cohesionReview = assessments.Count(item => !item.InTacticalBubble || !item.UsefulCohesionPosition);
            int anchorFocus = assessments.Count(item => string.Equals(item.PreferredObservation.CandidateKind, "anchor_context", StringComparison.Ordinal));
            int poseFocus = assessments.Count(item => string.Equals(item.PreferredObservation.CandidateKind, "pose_context", StringComparison.Ordinal));
            int portalFocus = assessments.Count(item => string.Equals(item.PreferredObservation.CandidateKind, "portal_context", StringComparison.Ordinal));
            int passageFocus = assessments.Count(item => string.Equals(item.PreferredObservation.CandidateKind, "passage_context", StringComparison.Ordinal));
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ASSESSMENT_SUMMARY map={Safe(topology.MapId)}; topologyRevision={topology.TopologyRevision}; topologyHash={ShortHash(topology.TopologyContentHash)}; operators={assessments.Count}; owners={ownerAssessments.Length}; generationMismatch={generationMismatchCount}; directThreat={directThreat}; cohesionReview={cohesionReview}; focusAnchor={anchorFocus}; focusPose={poseFocus}; focusPortal={portalFocus}; focusPassage={passageFocus}; scoringOnly=true; executionAllowed=false");
        }
    }

    public static bool TryGetSnapshot(out TacticalAssessmentSnapshot snapshot)
    {
        lock (Sync)
        {
            snapshot = current;
            return snapshot.IsAvailable;
        }
    }

    public static bool TryGetOperatorAssessment(string botProfileId, out TacticalOperatorAssessment assessment)
    {
        assessment = null!;
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

        assessment = candidate;
        return true;
    }

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            current = TacticalAssessmentSnapshot.Empty;
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
                $"TC_ASSESSMENT_RESET reason={Safe(reason)}; scoringOnly=true; executionAllowed=false");
        }
    }

    private static TacticalOperatorAssessment BuildOperatorAssessment(
        OperatorDecisionSnapshot decision,
        VanguardTacticalCoordinatorOperatorTopologyView view)
    {
        var candidates = new List<TacticalCandidateAssessment>
        {
            BuildCurrentPositionCandidate(decision, view)
        };

        if (!string.IsNullOrWhiteSpace(view.ContainingAnchorId))
        {
            candidates.Add(new TacticalCandidateAssessment(
                "anchor_context",
                view.ContainingAnchorId,
                view.ContainingAnchorId,
                0f,
                92f,
                true,
                "operator_inside_authored_anchor"));
        }
        else if (!string.IsNullOrWhiteSpace(view.NearestAnchorId) && view.NearestAnchorDistanceMeters.HasValue)
        {
            candidates.Add(new TacticalCandidateAssessment(
                "anchor_context",
                view.NearestAnchorId,
                view.NearestAnchorId,
                view.NearestAnchorDistanceMeters,
                ProximityScore(62f, view.NearestAnchorDistanceMeters.Value, 0.55f, 20f),
                true,
                "nearest_enabled_anchor"));
        }

        if (!string.IsNullOrWhiteSpace(view.NearestPoseSlotId) && view.NearestPoseSlotDistanceMeters.HasValue)
        {
            float score = ProximityScore(72f, view.NearestPoseSlotDistanceMeters.Value, 0.80f, 18f);
            if (!string.IsNullOrWhiteSpace(view.ContainingAnchorId)
                && string.Equals(view.ContainingAnchorId, view.NearestPoseSlotAnchorId, StringComparison.OrdinalIgnoreCase))
            {
                score += 10f;
            }
            candidates.Add(new TacticalCandidateAssessment(
                "pose_context",
                view.NearestPoseSlotId,
                view.NearestPoseSlotAnchorId,
                view.NearestPoseSlotDistanceMeters,
                ClampScore(score),
                true,
                "nearest_enabled_pose_slot"));
        }

        if (!string.IsNullOrWhiteSpace(view.NearestPortalId) && view.NearestPortalDistanceMeters.HasValue)
        {
            candidates.Add(new TacticalCandidateAssessment(
                "portal_context",
                view.NearestPortalId,
                string.Empty,
                view.NearestPortalDistanceMeters,
                ProximityScore(54f, view.NearestPortalDistanceMeters.Value, 0.45f, 12f),
                true,
                "nearest_enabled_portal"));
        }

        if (!string.IsNullOrWhiteSpace(view.NearestPassagePathId) && view.NearestPassagePathDistanceMeters.HasValue)
        {
            float score = ProximityScore(58f, view.NearestPassagePathDistanceMeters.Value, 0.60f, 14f);
            if (decision.SquadCohesion.CorridorLike)
            {
                score += 12f;
            }
            candidates.Add(new TacticalCandidateAssessment(
                "passage_context",
                view.NearestPassagePathId,
                string.Empty,
                view.NearestPassagePathDistanceMeters,
                ClampScore(score),
                true,
                decision.SquadCohesion.CorridorLike ? "nearest_passage_corridor_context" : "nearest_enabled_passage"));
        }

        var preferred = candidates
            .Where(item => item.Available)
            .OrderByDescending(item => item.RelevanceScore)
            .ThenBy(item => item.CandidateKind, StringComparer.Ordinal)
            .ThenBy(item => item.TargetId, StringComparer.Ordinal)
            .FirstOrDefault() ?? TacticalCandidateAssessment.None;

        return new TacticalOperatorAssessment(
            decision.OperatorId,
            decision.OwnerProfileId,
            decision.BotProfileId,
            decision.CapturedAtUtc,
            decision.Position,
            decision.Threat.DirectThreat,
            decision.Threat.DirectThreat || decision.Threat.ResidualThreat,
            decision.Threat.EnemyId,
            decision.Threat.Classification,
            decision.Threat.ThreatAnchorPosition,
            decision.Threat.ThreatAnchorSource,
            decision.Threat.ThreatAnchorConfidence,
            ThreatAnchorAgeSeconds(decision.Threat),
            decision.SquadCohesion.InBubble,
            decision.SquadCohesion.UsefulPosition,
            view.IsInsideTacticalAnchor,
            ContextClassification(decision, view),
            candidates.ToArray(),
            preferred);
    }

    private static float? ThreatAnchorAgeSeconds(VanguardThreatDecisionSnapshot threat)
    {
        float? best = null;

        // Age must describe the position actually carried by ThreatAnchorPosition. A recent
        // unrelated hearing event must not make an older visual/known position appear fresh.
        if (string.Equals(threat.ThreatAnchorConfidence, "current_direct", StringComparison.OrdinalIgnoreCase))
        {
            ConsiderThreatAge(threat.TimeSinceSeen, ref best);
            ConsiderThreatAge(threat.TimeSinceKnownUpdated, ref best);
            return best;
        }

        if (string.Equals(threat.ThreatAnchorConfidence, "last_known", StringComparison.OrdinalIgnoreCase))
        {
            ConsiderThreatAge(threat.TimeSinceKnownUpdated, ref best);
            return best;
        }

        return null;
    }

    private static void ConsiderThreatAge(float? candidate, ref float? best)
    {
        if (!candidate.HasValue || candidate.Value < 0f || float.IsNaN(candidate.Value) || float.IsInfinity(candidate.Value))
        {
            return;
        }

        if (!best.HasValue || candidate.Value < best.Value)
        {
            best = candidate.Value;
        }
    }

    private static TacticalCandidateAssessment BuildCurrentPositionCandidate(
        OperatorDecisionSnapshot decision,
        VanguardTacticalCoordinatorOperatorTopologyView view)
    {
        float score = 28f;
        if (decision.SquadCohesion.InBubble)
        {
            score += 10f;
        }
        if (decision.SquadCohesion.UsefulPosition)
        {
            score += 12f;
        }
        if (decision.Threat.DirectThreat)
        {
            score += 18f;
        }
        if (view.IsInsideTacticalAnchor)
        {
            score += 8f;
        }

        return new TacticalCandidateAssessment(
            "current_position_context",
            decision.BotProfileId,
            view.ContainingAnchorId,
            0f,
            ClampScore(score),
            true,
            decision.Threat.DirectThreat ? "current_position_under_direct_threat" : "current_position_observed");
    }

    private static TacticalOwnerAssessment[] BuildOwnerAssessments(
        IReadOnlyList<TacticalOperatorAssessment> operators)
    {
        return operators
            .GroupBy(item => item.OwnerProfileId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.ToArray();
                int directThreat = items.Count(item => item.DirectThreat);
                int outsideBubble = items.Count(item => !item.InTacticalBubble);
                int cohesionReview = items.Count(item => !item.InTacticalBubble || !item.UsefulCohesionPosition);
                int anchor = items.Count(item => item.InsideTacticalAnchor);
                int pose = items.Count(item => item.Candidates.Any(candidate => candidate.CandidateKind == "pose_context"));
                int portal = items.Count(item => item.Candidates.Any(candidate => candidate.CandidateKind == "portal_context"));
                int passage = items.Count(item => item.Candidates.Any(candidate => candidate.CandidateKind == "passage_context"));
                string classification = directThreat > 0
                    ? "owner_context_direct_threat_present"
                    : cohesionReview > 0
                        ? "owner_context_cohesion_review"
                        : (anchor + pose + portal + passage) > 0
                            ? "owner_context_topology_observed"
                            : "owner_context_no_topology_observation";

                return new TacticalOwnerAssessment(
                    group.Key,
                    items.Length,
                    directThreat,
                    outsideBubble,
                    cohesionReview,
                    anchor,
                    pose,
                    portal,
                    passage,
                    classification);
            })
            .ToArray();
    }

    private static string ContextClassification(
        OperatorDecisionSnapshot decision,
        VanguardTacticalCoordinatorOperatorTopologyView view)
    {
        if (decision.Threat.DirectThreat)
        {
            return "operator_context_direct_threat";
        }
        if (!decision.SquadCohesion.InBubble)
        {
            return "operator_context_outside_tactical_bubble";
        }
        if (!decision.SquadCohesion.UsefulPosition)
        {
            return "operator_context_cohesion_review";
        }
        if (view.IsInsideTacticalAnchor)
        {
            return "operator_context_inside_authored_anchor";
        }
        if (view.HasPassageObservation || view.HasPortalObservation || view.HasPoseSlotObservation)
        {
            return "operator_context_topology_observed";
        }
        return "operator_context_stable_no_authored_feature_nearby";
    }

    private static float ProximityScore(float baseScore, float distanceMeters, float penaltyPerMeter, float minimum)
    {
        if (float.IsNaN(distanceMeters) || float.IsInfinity(distanceMeters))
        {
            return minimum;
        }
        return ClampScore(Math.Max(minimum, baseScore - Math.Max(0f, distanceMeters) * penaltyPerMeter));
    }

    private static float ClampScore(float score) => Math.Max(0f, Math.Min(100f, score));

    private static void ClearIfPublished()
    {
        bool hadPublished;
        lock (Sync)
        {
            hadPublished = current.IsAvailable;
            current = TacticalAssessmentSnapshot.Empty;
        }

        if (hadPublished)
        {
            lastTopologyRevision = 0;
            lastTopologyHash = string.Empty;
            lastOperatorBatchAtUtc = DateTimeOffset.MinValue;
        }
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');

    private static string ShortHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }
        var trimmed = value.Trim();
        return trimmed.Length <= 12 ? trimmed : trimmed.Substring(0, 12);
    }
}
#endif
