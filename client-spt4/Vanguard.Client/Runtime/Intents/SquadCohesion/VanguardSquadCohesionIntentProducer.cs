#if SPT_CLIENT
using System.Collections.Generic;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Represent squad-cohesion state as read-only candidate intents.
// Flow: Evaluate missing owner, bubble distance, direct threat, sector distribution and useful holding position from the snapshot.
// Authority boundary: Cohesion arbitration and external combat control retain command authority; this producer emits observations only.
// Invariant: Dead or cohesion-disabled Operators emit no candidate, and an unknown owner never authorizes a blind regrouping move.
namespace Vanguard.Client.Runtime.Intents;

internal sealed class VanguardSquadCohesionIntentProducer : IVanguardIntentProducer
{
    public IEnumerable<VanguardIntentCandidate> Produce(OperatorDecisionSnapshot snapshot)
    {
        if (!snapshot.Alive || !snapshot.SquadCohesion.Enabled)
        {
            yield break;
        }

        var cohesion = snapshot.SquadCohesion;
        if (!cohesion.OwnerKnown)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "SquadCohesionOwnerUnknownReadOnly",
                Domain = "SquadCohesion",
                BaseScore = 6f,
                Reason = cohesion.Reason,
                Gate = "valid_readonly_owner_unknown"
            };
            yield break;
        }

        if (!cohesion.InBubble)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "SquadCohesionCatchUpToBubbleReadOnly",
                Domain = "SquadCohesion",
                BaseScore = 42f,
                Reason = cohesion.Reason,
                TargetKey = cohesion.Sector,
                PlanKey = "cohesion_bubble",
                NextStep = "catch_up_readonly",
                Gate = "valid_readonly_outside_bubble"
            };
            yield break;
        }

        if (cohesion.DirectThreat)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "SquadCohesionLocalCombatEnvelopeReadOnly",
                Domain = "SquadCohesion",
                BaseScore = 34f,
                Reason = cohesion.Reason,
                TargetKey = cohesion.Sector,
                PlanKey = "cohesion_sain_envelope",
                NextStep = cohesion.SainEnvelope,
                Gate = "valid_readonly_direct_threat_sector"
            };
            yield break;
        }

        if (cohesion.RearOverstacked || cohesion.SectorDuplicate)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "SquadCohesionReviewSectorDistributionReadOnly",
                Domain = "SquadCohesion",
                BaseScore = 31f,
                Reason = cohesion.Reason,
                TargetKey = cohesion.Sector,
                PlanKey = "cohesion_sector_distribution",
                NextStep = "review_distribution_readonly",
                Gate = "valid_readonly_sector_review"
            };
            yield break;
        }

        if (cohesion.UsefulPosition)
        {
            yield return new VanguardIntentCandidate
            {
                IntentKey = "SquadCohesionHoldUsefulSectorReadOnly",
                Domain = "SquadCohesion",
                BaseScore = 27f,
                Reason = cohesion.Reason,
                TargetKey = cohesion.Sector,
                PlanKey = "cohesion_useful_sector",
                NextStep = "do_not_apply_readonly",
                Gate = "valid_readonly_useful_sector"
            };
            yield break;
        }

        yield return new VanguardIntentCandidate
        {
            IntentKey = "SquadCohesionMaintainTacticalBubbleReadOnly",
            Domain = "SquadCohesion",
            BaseScore = 24f,
            Reason = cohesion.Reason,
            TargetKey = cohesion.Sector,
            PlanKey = "cohesion_maintain",
            NextStep = "observe_readonly",
            Gate = "valid_readonly_maintain"
        };
    }
}
#endif
