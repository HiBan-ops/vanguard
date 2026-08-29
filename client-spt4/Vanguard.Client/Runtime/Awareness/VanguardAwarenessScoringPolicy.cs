#if SPT_CLIENT
using System;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Encodes the deterministic rules for Awareness Scoring Policy within the combat-awareness runtime.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Runtime.Awareness;

internal static class VanguardAwarenessScoringPolicy
{
    public static float ScoreCurrentThreat(VanguardThreatDecisionSnapshot threat)
    {
        if (!threat.HasThreat)
        {
            return 0f;
        }

        float score = 8f;
        if (threat.ShotMeRecently == true) score += 120f;
        if (threat.ShotAtMeRecently == true) score += 95f;
        if (threat.EnemyCanShoot == true) score += 82f;
        if (threat.EnemyLineOfSight == true) score += 70f;
        if (threat.EnemyVisible == true) score += 60f;
        if (threat.DirectThreat) score += 40f;
        if (threat.ResidualThreat) score += 20f;
        if (threat.StaleThreat) score -= 28f;
        score += DistanceBonus(threat.Distance);
        score += SeenAgeBonus(threat.TimeSinceSeen);
        return Clamp(score);
    }

    public static float ScoreThreatScan(VanguardThreatScanDecisionSnapshot scan)
    {
        if (!scan.Scanned || string.Equals(scan.CandidateThreatId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return 0f;
        }

        float score = scan.CandidateScore;
        if (scan.CandidateIncomingFireFresh) score += 42f;
        if (scan.CandidateIncomingFireStale) score += 12f;
        if (scan.WouldPromote) score += 35f;
        if (scan.CandidateCanShoot) score += 18f;
        if (scan.CandidateLineOfSight) score += 14f;
        if (scan.CandidateVisible) score += 10f;
        return Clamp(score);
    }

    public static float ConfidenceFromScore(float score)
    {
        if (score <= 0f)
        {
            return 0f;
        }

        return Math.Min(1f, score / 185f);
    }

    public static bool HasHealthyCurrentTarget(VanguardThreatDecisionSnapshot threat)
    {
        if (!threat.HasThreat || string.Equals(threat.EnemyId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (threat.EnemyCanShoot == true || threat.EnemyLineOfSight == true || threat.EnemyVisible == true)
        {
            return true;
        }

        return threat.TimeSinceSeen.HasValue && threat.TimeSinceSeen.Value >= 0f && threat.TimeSinceSeen.Value <= 2.0f;
    }

    public static bool IsClosePotentialContact(float? distance, float thresholdMeters)
    {
        return distance.HasValue && distance.Value >= 0f && distance.Value <= thresholdMeters;
    }

    private static float DistanceBonus(float? distance)
    {
        if (!distance.HasValue)
        {
            return 0f;
        }

        if (distance.Value <= 12f) return 52f;
        if (distance.Value <= 20f) return 38f;
        if (distance.Value <= 35f) return 26f;
        if (distance.Value <= 68f) return 12f;
        if (distance.Value <= 145f) return 4f;
        return 0f;
    }

    private static float SeenAgeBonus(float? seenAgo)
    {
        if (!seenAgo.HasValue || seenAgo.Value < 0f)
        {
            return 0f;
        }

        if (seenAgo.Value <= 1.5f) return 18f;
        if (seenAgo.Value <= 4.0f) return 8f;
        return 0f;
    }

    private static float Clamp(float score)
    {
        if (score < 0f)
        {
            return 0f;
        }

        return score > 240f ? 240f : score;
    }
}
#endif
