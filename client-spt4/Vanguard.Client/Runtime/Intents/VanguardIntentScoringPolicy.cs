#if SPT_CLIENT
using System;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Medical;
using Vanguard.Client.Runtime.Medical.Execution;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Encodes the deterministic rules for Intent Scoring Policy within the intent production pipeline.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Runtime.Intents;

internal static class VanguardIntentScoringPolicy
{
    public static VanguardIntentCandidate Score(OperatorDecisionSnapshot snapshot, VanguardIntentCandidate candidate)
    {
        if (!candidate.Valid)
        {
            candidate.FinalScore = 0f;
            return candidate;
        }

        float score = candidate.BaseScore;
        score *= ThreatMultiplier(snapshot, candidate);
        score *= MovementMultiplier(snapshot, candidate);
        score *= ExternalSystemMultiplier(snapshot, candidate);
        score *= ThreatScannerMultiplier(snapshot, candidate);
        score *= AwarenessMultiplier(snapshot, candidate);
        score *= MedicalPlanMultiplier(snapshot, candidate);
        score *= MovementAuthorityMultiplier(snapshot, candidate);
        score *= VanguardOrchestratorAuthorityPolicy.DomainScoreMultiplier(snapshot, candidate);
        candidate.FinalScore = Math.Max(0f, score);
        return candidate;
    }

    private static float ThreatMultiplier(OperatorDecisionSnapshot snapshot, VanguardIntentCandidate candidate)
    {
        if (VanguardSurgeryDebtService.HasDueDebt(snapshot, out _) && !VanguardSurgeryDebtService.HasTrueThreat(snapshot))
        {
            bool catastrophicSeparation = snapshot.MovementAuthority.HardOutsideBubble
                || snapshot.SquadCohesion.OperatorDistanceToOwner >= Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.CombatCohesionEmergencyReturnMeters;
            if (!catastrophicSeparation
                && (candidate.Domain == "Follow" || candidate.Domain == "SquadCohesion" || candidate.IntentKey == "ObserveResidualThreat" || candidate.IntentKey == "IgnoreStaleThreat"))
            {
                return 0.05f;
            }

            if (candidate.IntentKey == "YieldToSainCombat" && !snapshot.Medical.Safety.EnemyCanShoot && !snapshot.Medical.Safety.IncomingFireRecent)
            {
                return 0.20f;
            }
        }

        if (candidate.IntentKey == "YieldToSainCombat" && snapshot.Threat.DirectThreat)
        {
            bool productive = Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.IsCombatProductive(snapshot, out _);
            if (!productive && snapshot.SquadCohesion.OperatorDistanceToOwner >= Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.CombatCohesionForcedCatchupMeters)
            {
                return 0.75f;
            }

            if (snapshot.Medical.Safety.CoveredSuppressionOpportunity
                && (snapshot.Medical.Need.HasHeavyBleed || snapshot.Medical.Need.HasLightBleed))
            {
                return snapshot.Medical.Need.HasHeavyBleed ? 0.70f : 0.80f;
            }

            return productive ? 1.55f : 1.10f;
        }

        if (VanguardMedicalSurgeryTargetPolicy.IsCriticalFastSurgeryCandidate(snapshot, out _)
            && (candidate.Domain == "Follow" || candidate.Domain == "SquadCohesion" || candidate.IntentKey == "ObserveResidualThreat" || candidate.IntentKey == "IgnoreStaleThreat"))
        {
            return 0.05f;
        }

        if (candidate.Domain == "Follow" && snapshot.Threat.StaleThreat)
        {
            return 1.15f;
        }

        if (candidate.Domain == "Follow" && snapshot.Threat.ResidualThreat)
        {
            return 0.85f;
        }

        return 1.0f;
    }

    private static float MovementMultiplier(OperatorDecisionSnapshot snapshot, VanguardIntentCandidate candidate)
    {
        if (candidate.IntentKey == "RejoinFormationReadOnly" && snapshot.Movement.Classification == "movement_path_stalled")
        {
            return 1.25f;
        }

        if (candidate.IntentKey == "ObserveSainCoverMove" && snapshot.Movement.RealSpeed > 0.25f)
        {
            return 1.10f;
        }

        return 1.0f;
    }

    private static float ThreatScannerMultiplier(OperatorDecisionSnapshot snapshot, VanguardIntentCandidate candidate)
    {
        if (candidate.IntentKey == "PromoteImmediateThreatToSainReadOnly" && snapshot.ThreatScan.WouldPromote)
        {
            if (VanguardSurgeryDebtService.HasDueDebt(snapshot, out _)
                && !snapshot.ThreatScan.CandidateCanShoot
                && !snapshot.ThreatScan.CandidateIncomingFireFresh
                && !snapshot.ThreatScan.CandidateShotMeRecently
                && !snapshot.ThreatScan.CandidateShotAtMeRecently)
            {
                return 0.15f;
            }

            if (VanguardMedicalSurgeryTargetPolicy.IsCriticalFastSurgeryCandidate(snapshot, out _)
                && !snapshot.ThreatScan.CandidateCanShoot
                && !snapshot.ThreatScan.CandidateIncomingFireFresh
                && !snapshot.ThreatScan.CandidateShotMeRecently
                && !snapshot.ThreatScan.CandidateShotAtMeRecently)
            {
                return 0.25f;
            }

            return snapshot.ThreatScan.CandidateCanShoot || snapshot.ThreatScan.CandidateShotMeRecently || snapshot.ThreatScan.CandidateShotAtMeRecently
                ? 1.35f
                : 1.15f;
        }

        if (candidate.IntentKey == "KeepCurrentSainTargetReadOnly")
        {
            return 0.75f;
        }

        return 1.0f;
    }

    private static float AwarenessMultiplier(OperatorDecisionSnapshot snapshot, VanguardIntentCandidate candidate)
    {
        if (candidate.Domain != "Awareness")
        {
            return 1.0f;
        }

        if (candidate.IntentKey == "AwarenessPromoteConfirmedThreatReadOnly" && snapshot.Awareness.WouldPromoteSainTarget)
        {
            if (VanguardSurgeryDebtService.HasDueDebt(snapshot, out _)
                && !snapshot.Awareness.IncomingFireFresh
                && !snapshot.Awareness.CandidateCanShoot)
            {
                return 0.15f;
            }

            if (VanguardMedicalSurgeryTargetPolicy.IsCriticalFastSurgeryCandidate(snapshot, out _)
                && !snapshot.Awareness.IncomingFireFresh
                && !snapshot.Awareness.CandidateCanShoot)
            {
                return 0.25f;
            }

            return snapshot.Awareness.IncomingFireFresh || snapshot.Awareness.CandidateCanShoot
                ? 1.30f
                : 1.12f;
        }

        if (candidate.IntentKey == "AwarenessReleaseFormationForThreatReadOnly" && snapshot.Awareness.WouldReleaseFormation)
        {
            return snapshot.Awareness.Confidence >= 0.70f ? 1.18f : 1.0f;
        }

        if (candidate.IntentKey == "AwarenessOrientAttentionReadOnly" && snapshot.Awareness.ShouldOrientAttention)
        {
            return snapshot.Awareness.ShouldMaintainFormation ? 0.92f : 1.05f;
        }

        return 1.0f;
    }

    private static float MedicalPlanMultiplier(OperatorDecisionSnapshot snapshot, VanguardIntentCandidate candidate)
    {
        if (candidate.Domain != "Medical")
        {
            return 1.0f;
        }

        if (snapshot.Threat.DirectThreat && snapshot.Medical.Need.DominantNeed != Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.HeavyBleed)
        {
            return 0.70f;
        }

        if (candidate.IntentKey == "MobileMedicalStabilize" && snapshot.Medical.Plan.WouldAllowMobile)
        {
            if (snapshot.Medical.Safety.CoveredSuppressionOpportunity)
            {
                return snapshot.Medical.Need.DominantNeed == Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.HeavyBleed
                    ? 1.70f
                    : 1.35f;
            }

            return snapshot.Medical.Need.DominantNeed == Vanguard.Client.Runtime.Medical.VanguardMedicalNeed.HpHeal ? 1.02f : 1.10f;
        }

        if (candidate.IntentKey == "StationaryMedicalStabilize")
        {
            if (!snapshot.Medical.Safety.SafeForStationaryAid)
            {
                return 0.15f;
            }

            return snapshot.Medical.Safety.CoveredOrHoldingAngle ? 1.16f : 1.05f;
        }

        if (candidate.IntentKey == "MedicalPrepareSurgeryCover")
        {
            if (VanguardSurgeryDebtService.HasDueDebt(snapshot, out _) && !VanguardSurgeryDebtService.HasTrueThreat(snapshot))
            {
                return 2.25f;
            }

            if (VanguardMedicalSurgeryTargetPolicy.HasImmediateThreatBlock(snapshot, out _))
            {
                return 0.20f;
            }

            if (VanguardMedicalSurgeryTargetPolicy.IsCriticalFastSurgeryCandidate(snapshot, out _))
            {
                return 1.85f;
            }

            if (snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true || snapshot.Orbit.Active || Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed) > 0.35f)
            {
                return 1.18f;
            }

            return snapshot.Medical.Safety.CoveredOrHoldingAngle ? 1.08f : 1.12f;
        }

        if (candidate.IntentKey == "StationaryMedicalSurgery")
        {
            if (VanguardSurgeryDebtService.HasDueDebt(snapshot, out _) && !VanguardSurgeryDebtService.HasTrueThreat(snapshot))
            {
                return 1.45f;
            }

            if (!snapshot.Medical.Safety.SafeForStationarySurgery && !snapshot.Medical.Safety.SafeForStationaryAid)
            {
                return 0.10f;
            }

            if (snapshot.Medical.Safety.IncomingFireRecent && !snapshot.Medical.Safety.CoveredOrHoldingAngle)
            {
                return 0.20f;
            }

            return snapshot.Medical.Safety.CoveredOrHoldingAngle ? 1.12f : 1.03f;
        }

        if (candidate.IntentKey == "ObserveFractureNeedAwaitStationarySafeWindowReadOnly")
        {
            return 1.02f;
        }

        if (candidate.IntentKey == "ObserveSurgeryNeedAwaitSafeWindowReadOnly")
        {
            return VanguardMedicalSurgeryTargetPolicy.IsCriticalFastSurgeryCandidate(snapshot, out _) ? 0.15f : 1.05f;
        }

        return 1.0f;
    }


    private static float MovementAuthorityMultiplier(OperatorDecisionSnapshot snapshot, VanguardIntentCandidate candidate)
    {
        if (candidate.Domain != "MovementAuthority")
        {
            return 1.0f;
        }

        if (VanguardSurgeryDebtService.HasDueDebt(snapshot, out _) && !VanguardSurgeryDebtService.HasTrueThreat(snapshot))
        {
            bool catastrophicSeparation = snapshot.MovementAuthority.HardOutsideBubble
                || snapshot.SquadCohesion.OperatorDistanceToOwner >= Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.CombatCohesionEmergencyReturnMeters;
            if (catastrophicSeparation && IsHardReturnRecoveryCandidate(candidate))
            {
                // A due surgery remains medically important, but it is not allowed to strand the
                // patient beyond the emergency cohesion envelope. Only a command that actually
                // closes distance receives this exception: a hold-sector/search posture must never
                // beat the return path. Once the patient is back near the squad, the same debt
                // regains priority and can proceed through cover preparation.
                return 1.45f;
            }

            return 0.04f;
        }

        if (snapshot.Threat.DirectThreat && !IsSainSearchBreakOrHardReturn(candidate) && candidate.IntentKey != "MovementBrokerYieldSainDirectThreatReadOnly")
        {
            return 0.40f;
        }

        if (candidate.IntentKey == "MovementBrokerBreakSainSearchReturnBubbleReadOnly"
            || candidate.IntentKey == "MovementBrokerReturnHardBubbleReadOnly"
            || candidate.IntentKey == "MovementBrokerSuppressExternalReturnBubbleReadOnly")
        {
            if (snapshot.SquadCohesion.OperatorDistanceToOwner >= Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.CombatCohesionEmergencyReturnMeters
                && !Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.IsCombatProductive(snapshot, out _))
            {
                return 1.65f;
            }

            if (snapshot.SquadCohesion.OperatorDistanceToOwner >= Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters
                || snapshot.MovementAuthority.HardOutsideBubble)
            {
                return 1.40f;
            }

            if (snapshot.SquadCohesion.OperatorDistanceToOwner >= Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.CombatCohesionForcedCatchupMeters)
            {
                return 1.22f;
            }
        }

        if (candidate.IntentKey == "MovementBrokerBreakSainSearchHoldSectorReadOnly")
        {
            if (snapshot.SquadCohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.InteriorMissionMaxOwnerDirectMeters
                && VanguardInteriorSecurityPlanner.IsVerifiedCoverageHold(snapshot, DateTimeOffset.UtcNow, out _))
            {
                // A verified interior guard is not a stale search hold. It is the selected persistent
                // area-security action and must remain stronger than ordinary Follow/Rejoin while the
                // player moves inside the same volume.
                return 1.25f;
            }

            if (snapshot.SquadCohesion.OperatorDistanceToOwner > VanguardMovementAuthorityDoctrine.CombatCohesionHoldSectorMaxMeters)
            {
                return 0.05f;
            }

            return snapshot.Threat.StaleThreat ? 0.75f : 0.90f;
        }

        if (candidate.IntentKey == "MovementBrokerCloseCohesionMicroAdjust")
        {
            if (snapshot.MovementAuthority.HardOutsideBubble || snapshot.Threat.DirectThreat)
            {
                return 0.05f;
            }

            if (snapshot.SquadCohesion.OperatorDistanceToOwner >= Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.CombatCohesionForcedCatchupMeters)
            {
                return 0.20f;
            }

            if (snapshot.SquadCohesion.OperatorDistanceToOwner >= Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.CloseCohesionForceStartMeters)
            {
                return 1.08f;
            }

            if (snapshot.SquadCohesion.UsefulPosition && !snapshot.SquadCohesion.SectorDuplicate && !snapshot.SquadCohesion.RearOverstacked)
            {
                return 0.82f;
            }

            return 1.05f;
        }

        if (candidate.IntentKey == "MovementBrokerTravelCohesionFollowThrough")
        {
            if (snapshot.MovementAuthority.HardOutsideBubble || snapshot.Threat.DirectThreat)
            {
                return 0.20f;
            }

            if (snapshot.SquadCohesion.OperatorDistanceToOwner >= Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.TravelCohesionForceMeters)
            {
                return 1.42f;
            }

            if (snapshot.SquadCohesion.OperatorDistanceToOwner >= Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.CombatCohesionForcedCatchupMeters)
            {
                return 1.30f;
            }

            if (Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.ShouldQuiesceOrbitForSquadTravel(snapshot, out _))
            {
                return snapshot.SquadCohesion.OperatorDistanceToOwner >= Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.OrbitQuiesceForceDistanceMeters ? 1.30f : 1.22f;
            }

            return snapshot.Orbit.Active || snapshot.Movement.HasPath == true ? 1.14f : 1.0f;
        }

        if (candidate.IntentKey == "MovementBrokerTacticalVolumeJoin")
        {
            if (snapshot.Threat.DirectThreat)
            {
                return 0.15f;
            }

            if (snapshot.SquadCohesion.OwnerToOperatorPathRatio >= Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.TacticalVolumeJoinPathRatio
                || Math.Abs(snapshot.SquadCohesion.VerticalDelta) >= Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.TacticalVolumeJoinVerticalDeltaMeters)
            {
                return 1.30f;
            }

            return 1.12f;
        }


        return 1.0f;
    }

    private static bool IsSainSearchBreakOrHardReturn(VanguardIntentCandidate candidate)
    {
        if (candidate == null)
        {
            return false;
        }

        return IsHardReturnRecoveryCandidate(candidate)
            || candidate.IntentKey == "MovementBrokerBreakSainSearchHoldSectorReadOnly";
    }

    private static bool IsHardReturnRecoveryCandidate(VanguardIntentCandidate candidate)
    {
        if (candidate == null)
        {
            return false;
        }

        return candidate.IntentKey == "MovementBrokerBreakSainSearchReturnBubbleReadOnly"
            || candidate.IntentKey == "MovementBrokerReturnHardBubbleReadOnly"
            || candidate.IntentKey == "MovementBrokerSuppressExternalReturnBubbleReadOnly";
    }

    private static float ExternalSystemMultiplier(OperatorDecisionSnapshot snapshot, VanguardIntentCandidate candidate)
    {
        if (VanguardSurgeryDebtService.HasDueDebt(snapshot, out _)
            && !VanguardSurgeryDebtService.HasTrueThreat(snapshot)
            && (candidate.IntentKey == "ObserveLootingBotsTask" || candidate.IntentKey == "ObserveOrbitObjective"))
        {
            return 0.02f;
        }

        if (VanguardMedicalSurgeryTargetPolicy.IsCriticalFastSurgeryCandidate(snapshot, out _)
            && (candidate.IntentKey == "ObserveLootingBotsTask" || candidate.IntentKey == "ObserveOrbitObjective"))
        {
            return 0.02f;
        }

        if (candidate.IntentKey == "ObserveLootingBotsTask" && snapshot.Threat.DirectThreat)
        {
            return 0.25f;
        }

        if (candidate.IntentKey == "ObserveOrbitObjective" && snapshot.Threat.DirectThreat)
        {
            return 0.25f;
        }

        if (candidate.IntentKey == "ObserveOrbitObjective"
            && snapshot.SquadCohesion.OwnerKnown
            && !snapshot.Threat.DirectThreat
            && !Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(snapshot))
        {
            if (Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.ShouldQuiesceOrbitForSquadTravel(snapshot, out _))
            {
                return 0.22f;
            }

            if (snapshot.SquadCohesion.OperatorDistanceToOwner >= Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.CloseCohesionOrbitPreemptMinMeters)
            {
                return 0.55f;
            }
        }

        return 1.0f;
    }
}
#endif
