#if SPT_CLIENT
using System;
using System.Collections.Generic;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Provides Movement Authority Intent Producer support for the intent production pipeline.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Intents;

internal sealed class VanguardMovementAuthorityIntentProducer : IVanguardIntentProducer
{
    public IEnumerable<VanguardIntentCandidate> Produce(OperatorDecisionSnapshot snapshot)
    {
        if (!snapshot.Alive || !snapshot.MovementAuthority.Enabled)
        {
            yield break;
        }

        var authority = snapshot.MovementAuthority;
        var broker = authority.BrokerPlan;
        string request = broker.Contract.RequestKind;
        float distance = snapshot.SquadCohesion.OperatorDistanceToOwner;
        bool forcedCatchup = distance >= VanguardMovementAuthorityDoctrine.CombatCohesionForcedCatchupMeters;
        bool hardCatchup = distance >= VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters;

        // The runtime cross-brick regression guard: a physically verified guard position inside the live
        // interior volume is intentionally allowed to exceed the ordinary follow envelope.  Without
        // this gate, accepting sectors beyond 44 m would immediately make HardReturn compete with the
        // Interior Area Security doctrine and pull guards back toward the player.  The exemption ends
        // automatically when the assignment expires, the Operator leaves its anchor, the volume
        // changes, or a direct threat requires SAIN authority.
        if (!VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot)
            && distance <= VanguardMovementAuthorityDoctrine.InteriorMissionMaxOwnerPathMeters
            && VanguardInteriorSecurityPlanner.IsVerifiedCoverageHold(snapshot, DateTimeOffset.UtcNow, out var interiorHoldReason))
        {
            yield return Candidate(
                "MovementBrokerBreakSainSearchHoldSectorReadOnly",
                76f,
                "verified_interior_security_hold:" + interiorHoldReason,
                "valid_verified_interior_hold_no_hard_return",
                broker);
            yield break;
        }

        if (request == VanguardMovementContractPolicy.BlockOwnerUnreliable)
        {
            string key = authority.OwnerKnown ? "MovementAuthorityOwnerCacheReadOnly" : "MovementAuthorityOwnerUnknownReadOnly";
            string gate = authority.OwnerKnown ? "valid_owner_cache_readonly_no_active_movement" : "invalid_owner_unknown_readonly";
            yield return Candidate(key, authority.OwnerKnown ? 10f : 7f, authority.Reason, gate, broker);
            yield break;
        }

        if (request == VanguardMovementContractPolicy.YieldSainDirectThreat)
        {
            yield return Candidate("MovementBrokerYieldSainDirectThreatReadOnly", 90f, authority.Reason, "valid_yield_sain_direct_threat", broker);
            yield break;
        }

        if (request == VanguardMovementContractPolicy.YieldVanguardMedical)
        {
            yield return Candidate("MovementBrokerYieldVanguardMedicalReadOnly", 86f, authority.Reason, "valid_yield_vanguard_medical", broker);
            yield break;
        }

        if (request == VanguardMovementContractPolicy.BreakSainSearchReturnBubble)
        {
            yield return Candidate("MovementBrokerBreakSainSearchReturnBubbleReadOnly", hardCatchup || authority.HardOutsideBubble ? 92f : (forcedCatchup ? 84f : 68f), authority.SainEnvelopeViolationReason, "valid_sain_search_outside_bubble_contract", broker);
            yield break;
        }

        if (request == VanguardMovementContractPolicy.BreakSainSearchHoldSector)
        {
            yield return Candidate("MovementBrokerBreakSainSearchHoldSectorReadOnly", distance <= VanguardMovementAuthorityDoctrine.CombatCohesionHoldSectorMaxMeters ? 50f : 12f, authority.SainEnvelopeViolationReason, "valid_sain_search_inside_bubble_no_movement_contract", broker);
            yield break;
        }

        if (request == VanguardMovementContractPolicy.SuppressExternalAndReturn)
        {
            yield return Candidate("MovementBrokerSuppressExternalReturnBubbleReadOnly", hardCatchup ? 88f : 74f, authority.Reason, "valid_external_hard_outside_bubble_contract", broker);
            yield break;
        }

        if (request == VanguardMovementContractPolicy.ReturnToBubbleHard)
        {
            yield return Candidate("MovementBrokerReturnHardBubbleReadOnly", hardCatchup ? 86f : 68f, authority.Reason, "valid_hard_outside_bubble_contract", broker);
            yield break;
        }

        if (request == VanguardMovementContractPolicy.MonitorSoftBubbleBreach)
        {
            yield return Candidate("MovementBrokerMonitorSoftBubbleBreachReadOnly", 34f, authority.Reason, "valid_soft_outside_hysteresis_readonly", broker);
            yield break;
        }

        if (request == VanguardMovementContractPolicy.ClaimedCohesionSlot)
        {
            yield return Candidate("MovementBrokerClaimedCohesionSlot", forcedCatchup ? 20f : 68f, authority.Reason, "valid_claimed_cohesion_slot_contract", broker);
            yield break;
        }

        if (request == VanguardMovementContractPolicy.CloseCohesionMicroAdjust)
        {
            yield return Candidate("MovementBrokerCloseCohesionMicroAdjust", forcedCatchup ? 16f : 54f, authority.Reason, "valid_close_cohesion_micro_adjust_contract", broker);
            yield break;
        }

        if (request == VanguardMovementContractPolicy.TravelCohesionFollowThrough)
        {
            yield return Candidate("MovementBrokerTravelCohesionFollowThrough", forcedCatchup ? 80f : (authority.SoftOutsideBubble ? 72f : 62f), authority.Reason, "valid_travel_cohesion_follow_through_contract", broker);
            yield break;
        }

        if (request == VanguardMovementContractPolicy.TacticalVolumeJoin)
        {
            yield return Candidate("MovementBrokerTacticalVolumeJoin", 70f, authority.Reason, "valid_tactical_volume_join_contract", broker);
            yield break;
        }

        if (request == VanguardMovementContractPolicy.SuppressExternalAuthorityOnly)
        {
            yield return Candidate("MovementBrokerSuppressExternalOnlyReadOnly", 46f, authority.Reason, "valid_external_out_of_doctrine_no_movement_contract", broker);
            yield break;
        }

        if (request == VanguardMovementContractPolicy.TacticalRepositionToUsefulSector)
        {
            yield return Candidate("MovementBrokerTacticalRepositionReadOnly", 43f, authority.Reason, "valid_tactical_reposition_contract", broker);
            yield break;
        }

        if (request == VanguardMovementContractPolicy.ObserveIdleStall)
        {
            yield return Candidate("MovementAuthorityIdleStallObserveReadOnly", 26f, authority.Reason, "valid_idle_stall_observe_readonly", broker);
            yield break;
        }

        yield return Candidate("MovementAuthorityMaintainCurrentReadOnly", 12f, authority.Reason, "valid_current_authority_within_doctrine", broker);
    }

    private static VanguardIntentCandidate Candidate(string key, float score, string reason, string gate, VanguardMovementBrokerPlanSnapshot broker)
    {
        return new VanguardIntentCandidate
        {
            IntentKey = key,
            Domain = "MovementAuthority",
            BaseScore = score,
            Reason = reason,
            Gate = gate,
            PlanKey = broker.PlanKey,
            NextStep = broker.RequestKind,
            TargetKey = broker.AnchorKind
        };
    }
}
#endif
