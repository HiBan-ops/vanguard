#if SPT_CLIENT
using System;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Medical;

// Responsibility: Builds Awareness Read Only Builder data for the combat-awareness runtime from already-available inputs.
// Flow: Normalized inputs are combined deterministically into a result consumed by the next policy, scheduler, UI, or transport stage.
// Authority boundary: Composition only; underlying gameplay/persistence truth remains owned by the source inputs.
// Invariant: Building a result must not perform hidden world mutation or acquire a competing authority.
namespace Vanguard.Client.Runtime.Awareness;

internal static class VanguardAwarenessReadOnlyBuilder
{
    private const float ImmediateAudioOnlyGunshotDistanceMeters = 34f;
    private const float PlayerCombatSupportGunshotDistanceMeters = 68f;
    private const float HeardShotReactionDistanceMeters = 145f;
    private const float CloseSuspicionDistanceMeters = 20f;

    public static VanguardAwarenessSnapshot Build(
        bool alive,
        VanguardThreatDecisionSnapshot threat,
        VanguardThreatScanDecisionSnapshot threatScan,
        VanguardSainDecisionSnapshot sain,
        VanguardBrainDecisionSnapshot brain,
        VanguardMedicalDecisionSnapshot medical)
    {
        if (!alive)
        {
            return new VanguardAwarenessSnapshot
            {
                Enabled = true,
                Alive = false,
                StimulusKind = VanguardAwarenessStimulusKind.TerminalDead,
                Source = "terminal",
                Reason = "operator_dead_awareness_disabled",
                Classification = "awareness_terminal_dead"
            };
        }

        var current = BuildFromCurrentThreat(threat, sain, brain, medical);
        var scan = BuildFromThreatScan(threat, threatScan, sain, brain, medical);
        return scan.Score > current.Score ? scan : current;
    }

    private static VanguardAwarenessSnapshot BuildFromCurrentThreat(
        VanguardThreatDecisionSnapshot threat,
        VanguardSainDecisionSnapshot sain,
        VanguardBrainDecisionSnapshot brain,
        VanguardMedicalDecisionSnapshot medical)
    {
        if (!threat.HasThreat)
        {
            return new VanguardAwarenessSnapshot
            {
                Enabled = true,
                Alive = true,
                StimulusKind = VanguardAwarenessStimulusKind.None,
                Source = "current_threat",
                Reason = "no_current_threat",
                ShouldMaintainFormation = true,
                Classification = "awareness_clear"
            };
        }

        float score = VanguardAwarenessScoringPolicy.ScoreCurrentThreat(threat);
        bool currentHealthy = VanguardAwarenessScoringPolicy.HasHealthyCurrentTarget(threat);
        bool incomingFresh = threat.ShotMeRecently == true || threat.ShotAtMeRecently == true;
        bool confirmed = threat.DirectThreat || threat.EnemyVisible == true || threat.EnemyLineOfSight == true || threat.EnemyCanShoot == true || incomingFresh;
        bool residualOnly = threat.ResidualThreat && !confirmed;
        bool staleOnly = threat.StaleThreat && !confirmed;
        var kind = ResolveCurrentStimulusKind(threat, incomingFresh, confirmed, residualOnly, staleOnly);
        bool closeSuspicion = VanguardAwarenessScoringPolicy.IsClosePotentialContact(threat.Distance, CloseSuspicionDistanceMeters);
        bool shouldOrient = confirmed || residualOnly || closeSuspicion;
        bool wouldPropagate = confirmed && !string.Equals(threat.EnemyId, "none", StringComparison.OrdinalIgnoreCase);
        bool wouldPromote = confirmed && !currentHealthy;
        bool wouldRelease = confirmed && !staleOnly;
        bool wouldBreakMedical = ShouldBreakMedicalForAwareness(medical, incomingFresh, threat.EnemyVisible == true, threat.EnemyLineOfSight == true, threat.EnemyCanShoot == true, threat.Distance);
        bool maintain = !wouldRelease;

        return new VanguardAwarenessSnapshot
        {
            Enabled = true,
            Alive = true,
            StimulusKind = kind,
            CandidateId = threat.EnemyId,
            CandidateName = threat.EnemyName,
            CandidateDistance = threat.Distance,
            CandidateSeenAgo = threat.TimeSinceSeen,
            CandidateVisible = threat.EnemyVisible == true,
            CandidateLineOfSight = threat.EnemyLineOfSight == true,
            CandidateCanShoot = threat.EnemyCanShoot == true,
            IncomingFireFresh = incomingFresh,
            IncomingFireStale = false,
            CurrentTargetHealthy = currentHealthy,
            Source = "current_threat_snapshot",
            Reason = ResolveReason(confirmed, residualOnly, staleOnly, incomingFresh, currentHealthy),
            Score = score,
            Confidence = VanguardAwarenessScoringPolicy.ConfidenceFromScore(score),
            ShouldOrientAttention = shouldOrient,
            WouldPropagateConfirmedThreat = wouldPropagate,
            WouldPromoteSainTarget = wouldPromote,
            WouldReleaseFormation = wouldRelease,
            WouldBreakMedical = wouldBreakMedical,
            ShouldMaintainFormation = maintain,
            Classification = Classify(confirmed, residualOnly, staleOnly, wouldRelease, shouldOrient)
        };
    }

    private static VanguardAwarenessSnapshot BuildFromThreatScan(
        VanguardThreatDecisionSnapshot currentThreat,
        VanguardThreatScanDecisionSnapshot scan,
        VanguardSainDecisionSnapshot sain,
        VanguardBrainDecisionSnapshot brain,
        VanguardMedicalDecisionSnapshot medical)
    {
        if (!scan.Enabled)
        {
            return new VanguardAwarenessSnapshot
            {
                Enabled = false,
                Alive = true,
                StimulusKind = VanguardAwarenessStimulusKind.ScannerUnavailable,
                Source = "threat_scan",
                Reason = "threat_scan_disabled",
                ShouldMaintainFormation = true,
                Classification = "awareness_scanner_disabled"
            };
        }

        if (!scan.Scanned || string.Equals(scan.CandidateThreatId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new VanguardAwarenessSnapshot
            {
                Enabled = true,
                Alive = true,
                StimulusKind = VanguardAwarenessStimulusKind.None,
                Source = "threat_scan",
                Reason = scan.Scanned ? "scan_no_candidate" : "scan_not_scanned",
                ShouldMaintainFormation = true,
                Classification = scan.Scanned ? "awareness_scan_clear" : "awareness_scan_unavailable"
            };
        }

        float score = VanguardAwarenessScoringPolicy.ScoreThreatScan(scan);
        bool currentHealthy = VanguardAwarenessScoringPolicy.HasHealthyCurrentTarget(currentThreat);
        bool directProof = scan.CandidateVisible || scan.CandidateLineOfSight || scan.CandidateCanShoot || scan.CandidateIncomingFireFresh;
        bool closeAudioOnly = scan.CandidateIncomingFireFresh && VanguardAwarenessScoringPolicy.IsClosePotentialContact(scan.CandidateDistance, ImmediateAudioOnlyGunshotDistanceMeters);
        bool supportDistance = scan.CandidateIncomingFireFresh && VanguardAwarenessScoringPolicy.IsClosePotentialContact(scan.CandidateDistance, PlayerCombatSupportGunshotDistanceMeters);
        bool heardLongRange = (scan.CandidateIncomingFireFresh || scan.CandidateIncomingFireStale) && VanguardAwarenessScoringPolicy.IsClosePotentialContact(scan.CandidateDistance, HeardShotReactionDistanceMeters);
        bool wouldPromote = scan.WouldPromote && directProof && !currentHealthy;
        bool wouldRelease = wouldPromote || closeAudioOnly || (scan.CandidateCanShoot && directProof);
        bool shouldOrient = directProof || heardLongRange || scan.WouldPromote;
        bool wouldPropagate = directProof && (scan.WouldPromote || supportDistance);
        bool wouldBreakMedical = ShouldBreakMedicalForAwareness(medical, scan.CandidateIncomingFireFresh, scan.CandidateVisible, scan.CandidateLineOfSight, scan.CandidateCanShoot, scan.CandidateDistance);
        bool maintain = !wouldRelease;
        var kind = ResolveScanStimulusKind(scan, directProof);

        return new VanguardAwarenessSnapshot
        {
            Enabled = true,
            Alive = true,
            StimulusKind = kind,
            CandidateId = scan.CandidateThreatId,
            CandidateName = scan.CandidateThreatName,
            CandidateArc = scan.CandidateArc,
            CandidateDistance = scan.CandidateDistance,
            CandidateSeenAgo = scan.CandidateTimeSinceSeen,
            CandidateVisible = scan.CandidateVisible,
            CandidateLineOfSight = scan.CandidateLineOfSight,
            CandidateCanShoot = scan.CandidateCanShoot,
            IncomingFireFresh = scan.CandidateIncomingFireFresh,
            IncomingFireStale = scan.CandidateIncomingFireStale,
            CurrentTargetHealthy = currentHealthy,
            Source = "threat_scan_sidecar",
            Reason = ResolveScanReason(scan, currentHealthy, wouldPromote, wouldRelease, shouldOrient),
            Score = score,
            Confidence = VanguardAwarenessScoringPolicy.ConfidenceFromScore(score),
            ShouldOrientAttention = shouldOrient,
            WouldPropagateConfirmedThreat = wouldPropagate,
            WouldPromoteSainTarget = wouldPromote,
            WouldReleaseFormation = wouldRelease,
            WouldBreakMedical = wouldBreakMedical,
            ShouldMaintainFormation = maintain,
            Classification = Classify(directProof || wouldPromote, heardLongRange && !directProof, false, wouldRelease, shouldOrient)
        };
    }

    private static VanguardAwarenessStimulusKind ResolveCurrentStimulusKind(VanguardThreatDecisionSnapshot threat, bool incomingFresh, bool confirmed, bool residualOnly, bool staleOnly)
    {
        if (incomingFresh)
        {
            return VanguardAwarenessStimulusKind.IncomingFireFresh;
        }

        if (threat.EnemyCanShoot == true)
        {
            return VanguardAwarenessStimulusKind.CanShootContact;
        }

        if (threat.EnemyLineOfSight == true)
        {
            return VanguardAwarenessStimulusKind.LineOfSightContact;
        }

        if (threat.EnemyVisible == true)
        {
            return VanguardAwarenessStimulusKind.VisibleContact;
        }

        if (confirmed)
        {
            return VanguardAwarenessStimulusKind.ConfirmedCurrentThreat;
        }

        if (residualOnly)
        {
            return VanguardAwarenessStimulusKind.ResidualKnownThreat;
        }

        return staleOnly ? VanguardAwarenessStimulusKind.StaleThreat : VanguardAwarenessStimulusKind.SuspiciousKnownContact;
    }

    private static VanguardAwarenessStimulusKind ResolveScanStimulusKind(VanguardThreatScanDecisionSnapshot scan, bool directProof)
    {
        if (scan.CandidateIncomingFireFresh)
        {
            return VanguardAwarenessStimulusKind.IncomingFireFresh;
        }

        if (scan.CandidateIncomingFireStale)
        {
            return VanguardAwarenessStimulusKind.IncomingFireStale;
        }

        if (scan.CandidateCanShoot)
        {
            return VanguardAwarenessStimulusKind.CanShootContact;
        }

        if (scan.CandidateLineOfSight)
        {
            return VanguardAwarenessStimulusKind.LineOfSightContact;
        }

        if (scan.CandidateVisible)
        {
            return VanguardAwarenessStimulusKind.VisibleContact;
        }

        return directProof ? VanguardAwarenessStimulusKind.ConfirmedSecondaryThreat : VanguardAwarenessStimulusKind.SuspiciousKnownContact;
    }

    private static bool ShouldBreakMedicalForAwareness(VanguardMedicalDecisionSnapshot medical, bool incomingFresh, bool visible, bool lineOfSight, bool canShoot, float? distance)
    {
        bool criticalThreat = incomingFresh || canShoot || lineOfSight || (visible && VanguardAwarenessScoringPolicy.IsClosePotentialContact(distance, 24f));
        if (!criticalThreat)
        {
            return false;
        }

        return medical.Need.DominantNeed != VanguardMedicalNeed.HeavyBleed || incomingFresh || canShoot;
    }

    private static string ResolveReason(bool confirmed, bool residualOnly, bool staleOnly, bool incomingFresh, bool currentHealthy)
    {
        if (incomingFresh) return "incoming_fire_current_threat";
        if (currentHealthy) return "current_target_healthy_keep_authority";
        if (confirmed) return "confirmed_current_threat";
        if (residualOnly) return "residual_known_threat_orient_only";
        if (staleOnly) return "stale_threat_follow_can_resume";
        return "suspicious_current_threat_observe_only";
    }

    private static string ResolveScanReason(VanguardThreatScanDecisionSnapshot scan, bool currentHealthy, bool wouldPromote, bool wouldRelease, bool shouldOrient)
    {
        if (currentHealthy && !wouldPromote)
        {
            return "current_target_healthy_scan_candidate_observe_only";
        }

        if (wouldPromote)
        {
            return "secondary_candidate_would_promote_readonly;scanReason=" + scan.PromotionReason;
        }

        if (wouldRelease)
        {
            return "secondary_candidate_would_release_formation_readonly;scanReason=" + scan.PromotionReason;
        }

        if (shouldOrient)
        {
            return "secondary_candidate_orient_attention_only;scanReason=" + scan.PromotionReason;
        }

        return "secondary_candidate_keep_formation;scanReason=" + scan.PromotionReason;
    }

    private static string Classify(bool confirmed, bool suspicious, bool stale, bool wouldRelease, bool shouldOrient)
    {
        if (wouldRelease)
        {
            return "awareness_would_release_formation";
        }

        if (confirmed)
        {
            return "awareness_confirmed_threat";
        }

        if (stale)
        {
            return "awareness_stale_threat";
        }

        if (suspicious || shouldOrient)
        {
            return "awareness_orient_only";
        }

        return "awareness_keep_formation";
    }
}
#endif
