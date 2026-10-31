#if SPT_CLIENT
using System;
using System.Collections.Generic;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;

// Responsibility: Propose an opportunistic corpse-loot claim-and-approach intent from a snapshot.
// Flow: Inspect corpse candidates and actionable self-care, then emit a bounded-score approach candidate with an explicit gate.
// Authority boundary: The loot execution subsystem owns claims and transactions; this producer does not transfer inventory.
// Invariant: No candidate is emitted without a corpse target, and actionable self-care invalidates the proposed approach.
namespace Vanguard.Client.Runtime.Intents;

/// <summary>
/// The runtime activates only the claim-and-approach seam. The inventory plan remains read-only and the
/// candidate score is capped below direct combat and medical authority so loot cannot degrade target
/// acquisition or surgery sequencing.
/// </summary>
internal sealed class VanguardOpportunisticCorpseLootIntentProducer : IVanguardIntentProducer
{
    public IEnumerable<VanguardIntentCandidate> Produce(OperatorDecisionSnapshot snapshot)
    {
        var loot = snapshot.CorpseLoot;
        if (!loot.CandidateFound)
        {
            yield break;
        }

        bool yieldToSelfCare = VanguardEffectiveMedicalExecutionPolicy.HasActionableSelfCareOpportunity(
            snapshot, DateTimeOffset.UtcNow, out string selfCareReason);

        yield return new VanguardIntentCandidate
        {
            IntentKey = "ApproachNearbyCorpse",
            Domain = "CorpseLoot",
            Valid = loot.ExecutionEnabled && loot.EligibleIfActivated && !yieldToSelfCare,
            BaseScore = ActiveScore(loot.UtilityScore),
            FinalScore = 0f,
            Reason = loot.Reason + ";plan=" + loot.Plan.CompactSummary + (yieldToSelfCare ? ";yield=actionable_self_care:" + selfCareReason : string.Empty),
            Gate = yieldToSelfCare ? "blocked_actionable_self_care:" + selfCareReason : loot.Gate,
            TargetKey = loot.CandidateCorpseId,
            PlanKey = "corpse_loot_claim_and_approach_no_transaction",
            NextStep = "claim_corpse_and_approach_interaction_anchor_only"
        };
    }

    private static float ActiveScore(float utility)
        => System.Math.Min(145f, System.Math.Max(35f, 35f + System.Math.Max(0f, utility) * 0.18f));
}
#endif
