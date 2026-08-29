#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// Responsibility: Provides Action Rally Anchor Scorer support for the movement/cohesion runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Movement;

/// <summary>
/// Vanguard replaces the old "first PathComplete wins" rule with a bounded action-rally score.
/// PathComplete is still mandatory, but it is no longer sufficient: excessive path length, bad
/// owner-ring placement and recent no-progress outcomes are penalized before a command is issued.
/// </summary>
internal static class VanguardActionRallyAnchorScorer
{
    public static VanguardActionRallyAnchorCandidate Score(
        string botProfileId,
        Vector3 botPosition,
        Vector3 ownerPosition,
        Vector3 anchor,
        float requestedRadius,
        int radiusIndex,
        int directionIndex,
        float bubbleDistanceMeters,
        float ownerDistanceMeters,
        string pathSummary,
        float pathDistanceMeters,
        DateTimeOffset now,
        bool ownerFallback = false)
    {
        float straightDistance = HorizontalDistance(botPosition, anchor);
        float expectedPath = Math.Max(45f, bubbleDistanceMeters * 1.05f);
        float softLimit = Math.Max(110f, bubbleDistanceMeters * 1.35f);
        float hardLimit = Math.Max(VanguardMovementAuthorityDoctrine.ActionRallyAnchorHardRejectMeters, bubbleDistanceMeters * 1.85f);
        bool excessivePath = pathDistanceMeters > hardLimit;
        string memoryReason;
        bool memoryPenalty = VanguardMovementOutcomeMemory.ShouldPenalizeAnchor(botProfileId, anchor, now, out memoryReason);

        float pathPenalty = Math.Max(0f, pathDistanceMeters - expectedPath) * 0.45f;
        if (pathDistanceMeters > softLimit)
        {
            pathPenalty += (pathDistanceMeters - softLimit) * 0.90f;
        }

        float ownerPenalty = Math.Abs(ownerDistanceMeters - VanguardMovementAuthorityDoctrine.ActionRallyPreferredMeters) * 1.35f;
        float radiusPenalty = radiusIndex * 1.75f;
        float directionPenalty = directionIndex > 2 ? 2.5f : 0f;
        float fallbackPenalty = ownerFallback ? 8f : 0f;
        float memoryScorePenalty = memoryPenalty ? 45f : 0f;
        float reductionBonus = Math.Max(0f, bubbleDistanceMeters - ownerDistanceMeters) * 0.18f;
        float directnessBonus = pathDistanceMeters > 0.1f ? Math.Max(0f, straightDistance / pathDistanceMeters) * 8f : 0f;
        float score = 100f + reductionBonus + directnessBonus - pathPenalty - ownerPenalty - radiusPenalty - directionPenalty - fallbackPenalty - memoryScorePenalty;

        bool accepted = !excessivePath && score >= VanguardMovementAuthorityDoctrine.ActionRallyAnchorScoreMinimum;
        string reason = excessivePath
            ? "rejected_path_excessive"
            : !accepted
                ? "rejected_score_below_minimum"
                : "accepted";

        string anchorReason = (ownerFallback ? "action_rally_owner_fallback_scored" : "action_rally_scored")
            + "_radius_" + requestedRadius.ToString("0", CultureInfo.InvariantCulture)
            + "_ridx_" + radiusIndex.ToString(CultureInfo.InvariantCulture)
            + "_dir_" + directionIndex.ToString(CultureInfo.InvariantCulture)
            + "_ownerDist_" + ownerDistanceMeters.ToString("0", CultureInfo.InvariantCulture)
            + "_score_" + score.ToString("0", CultureInfo.InvariantCulture);

        string scoreSummary = "score=" + score.ToString("0.00", CultureInfo.InvariantCulture)
            + ";accepted=" + Bool(accepted)
            + ";reason=" + reason
            + ";pathDist=" + pathDistanceMeters.ToString("0.00", CultureInfo.InvariantCulture)
            + ";expectedPath=" + expectedPath.ToString("0.00", CultureInfo.InvariantCulture)
            + ";softLimit=" + softLimit.ToString("0.00", CultureInfo.InvariantCulture)
            + ";hardLimit=" + hardLimit.ToString("0.00", CultureInfo.InvariantCulture)
            + ";ownerDist=" + ownerDistanceMeters.ToString("0.00", CultureInfo.InvariantCulture)
            + ";straight=" + straightDistance.ToString("0.00", CultureInfo.InvariantCulture)
            + ";pathPenalty=" + pathPenalty.ToString("0.00", CultureInfo.InvariantCulture)
            + ";ownerPenalty=" + ownerPenalty.ToString("0.00", CultureInfo.InvariantCulture)
            + ";memoryPenalty=" + Bool(memoryPenalty)
            + ";memoryReason=" + memoryReason;

        return new VanguardActionRallyAnchorCandidate(
            accepted,
            anchor,
            anchorReason,
            pathSummary + ";" + scoreSummary,
            scoreSummary,
            pathDistanceMeters,
            ownerDistanceMeters,
            score);
    }

    public static bool TrySelectBest(IReadOnlyList<VanguardActionRallyAnchorCandidate> candidates, out VanguardActionRallyAnchorCandidate best)
    {
        best = VanguardActionRallyAnchorCandidate.Empty;
        if (candidates == null || candidates.Count == 0)
        {
            return false;
        }

        bool found = false;
        float bestScore = float.MinValue;
        for (int index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (!candidate.Accepted)
            {
                continue;
            }

            if (!found || candidate.Score > bestScore)
            {
                found = true;
                best = candidate;
                bestScore = candidate.Score;
            }
        }

        return found;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static string Bool(bool value) => value ? "true" : "false";
}

internal readonly struct VanguardActionRallyAnchorCandidate
{
    public static readonly VanguardActionRallyAnchorCandidate Empty = new(false, Vector3.zero, "none", "none", "none", 0f, 0f, 0f);

    public VanguardActionRallyAnchorCandidate(bool accepted, Vector3 anchor, string anchorReason, string pathSummary, string scoreSummary, float pathDistanceMeters, float ownerDistanceMeters, float score)
    {
        Accepted = accepted;
        Anchor = anchor;
        AnchorReason = anchorReason;
        PathSummary = pathSummary;
        ScoreSummary = scoreSummary;
        PathDistanceMeters = pathDistanceMeters;
        OwnerDistanceMeters = ownerDistanceMeters;
        Score = score;
    }

    public bool Accepted { get; }
    public Vector3 Anchor { get; }
    public string AnchorReason { get; }
    public string PathSummary { get; }
    public string ScoreSummary { get; }
    public float PathDistanceMeters { get; }
    public float OwnerDistanceMeters { get; }
    public float Score { get; }
}
#endif
