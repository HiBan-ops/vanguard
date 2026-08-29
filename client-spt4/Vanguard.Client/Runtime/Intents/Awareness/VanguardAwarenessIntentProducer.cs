#if SPT_CLIENT
using System;
using System.Collections.Generic;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Provides Awareness Intent Producer support for the intent production pipeline.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Intents;

internal sealed class VanguardAwarenessIntentProducer : IVanguardIntentProducer
{
    public IEnumerable<VanguardIntentCandidate> Produce(OperatorDecisionSnapshot snapshot)
    {
        var awareness = snapshot.Awareness;
        if (!awareness.Enabled)
        {
            yield break;
        }

        if (!snapshot.Alive)
        {
            yield break;
        }

        if (awareness.WouldPromoteSainTarget)
        {
            yield return Candidate(
                "AwarenessPromoteConfirmedThreatReadOnly",
                90f,
                "awareness_would_promote_sain_target;" + awareness.Summary,
                awareness.CandidateId,
                "valid_awareness_confirmed_threat_readonly");
            yield break;
        }

        if (awareness.WouldReleaseFormation)
        {
            yield return Candidate(
                "AwarenessReleaseFormationForThreatReadOnly",
                78f,
                "awareness_would_release_formation;" + awareness.Summary,
                awareness.CandidateId,
                "valid_awareness_release_readonly");
            yield break;
        }

        if (awareness.WouldPropagateConfirmedThreat)
        {
            yield return Candidate(
                "AwarenessPropagateConfirmedThreatReadOnly",
                62f,
                "awareness_would_propagate_confirmed_threat;" + awareness.Summary,
                awareness.CandidateId,
                "valid_awareness_propagate_readonly");
        }

        if (awareness.ShouldOrientAttention)
        {
            yield return Candidate(
                "AwarenessOrientAttentionReadOnly",
                44f,
                "awareness_orient_attention;" + awareness.Summary,
                awareness.CandidateId,
                "valid_awareness_orient_readonly");
            yield break;
        }

        if (awareness.ShouldMaintainFormation)
        {
            yield return Candidate(
                "AwarenessMaintainFormationReadOnly",
                12f,
                "awareness_keep_formation;" + awareness.Summary,
                awareness.CandidateId,
                "valid_awareness_keep_formation_readonly");
        }
    }

    private static VanguardIntentCandidate Candidate(string key, float score, string reason, string target, string gate)
    {
        return new VanguardIntentCandidate
        {
            IntentKey = key,
            Domain = "Awareness",
            BaseScore = score,
            Reason = reason,
            TargetKey = string.IsNullOrWhiteSpace(target) ? "none" : target,
            Gate = gate
        };
    }
}
#endif
