#if SPT_CLIENT
using System.Collections.Generic;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Provides Opportunistic Corpse Loot Intent Producer support for the intent production pipeline.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
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

        yield return new VanguardIntentCandidate
        {
            IntentKey = "ApproachNearbyCorpse",
            Domain = "CorpseLoot",
            Valid = loot.ExecutionEnabled && loot.EligibleIfActivated,
            BaseScore = ActiveScore(loot.UtilityScore),
            FinalScore = 0f,
            Reason = loot.Reason + ";plan=" + loot.Plan.CompactSummary,
            Gate = loot.Gate,
            TargetKey = loot.CandidateCorpseId,
            PlanKey = "corpse_loot_claim_and_approach_no_transaction",
            NextStep = "claim_corpse_and_approach_interaction_anchor_only"
        };
    }

    private static float ActiveScore(float utility)
        => System.Math.Min(145f, System.Math.Max(35f, 35f + System.Math.Max(0f, utility) * 0.18f));
}
#endif
