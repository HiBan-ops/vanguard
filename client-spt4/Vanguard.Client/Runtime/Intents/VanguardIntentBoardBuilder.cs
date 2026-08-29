#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;

// Responsibility: Builds Intent Board Builder data for the intent production pipeline from already-available inputs.
// Flow: Normalized inputs are combined deterministically into a result consumed by the next policy, scheduler, UI, or transport stage.
// Authority boundary: Composition only; underlying gameplay/persistence truth remains owned by the source inputs.
// Invariant: Building a result must not perform hidden world mutation or acquire a competing authority.
namespace Vanguard.Client.Runtime.Intents;

internal static partial class VanguardOperatorIntentDryRunService
{
    internal static VanguardIntentDryRunBoard BuildBoard(OperatorDecisionSnapshot snapshot)
    {
        VanguardOrchestratorAuthorityPolicy.LogBootOnce();
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var candidates = new List<VanguardIntentCandidate>();
        foreach (var producer in Producers)
        {
            candidates.AddRange(producer.Produce(snapshot).Select(candidate => VanguardIntentScoringPolicy.Score(snapshot, candidate)));
        }

        if (VanguardOrchestratorAuthorityPolicy.IsCombatAuthority(snapshot, out var combatReason))
        {
            candidates.Add(VanguardIntentScoringPolicy.Score(snapshot, new VanguardIntentCandidate
            {
                IntentKey = "OrchestratorCombatAuthorityRelease",
                Domain = "Combat",
                BaseScore = 420f,
                Reason = "exclusive_combat_authority:" + combatReason,
                TargetKey = snapshot.Threat.EnemyId != "none" ? snapshot.Threat.EnemyId : snapshot.Awareness.CandidateId != "none" ? snapshot.Awareness.CandidateId : snapshot.ThreatScan.CandidateThreatId,
                PlanKey = "exclusive_combat_authority",
                NextStep = "release_sain_full_authority_and_suspend_other_domains",
                Gate = "valid_exclusive_combat_authority"
            }));
        }

        if (VanguardOrchestratorAuthorityPolicy.ShouldHoldStableCohesion(snapshot, out var stableHoldReason))
        {
            candidates.Add(VanguardIntentScoringPolicy.Score(snapshot, new VanguardIntentCandidate
            {
                IntentKey = "ExclusiveCohesionStableHold",
                Domain = "Cohesion",
                BaseScore = 260f,
                Reason = "exclusive_stable_hold:" + stableHoldReason,
                TargetKey = "current_anchor",
                PlanKey = "stable_hold_no_replan",
                NextStep = "hold_without_new_claim_or_goto",
                Gate = "valid_stable_cohesion_hold"
            }));
        }

        var gated = candidates.Select(candidate =>
        {
            if (VanguardOrchestratorAuthorityPolicy.IsCandidateAllowedByExclusiveDomain(snapshot, candidate, out var gateReason))
            {
                return VanguardIntentScoringPolicy.Score(snapshot, candidate);
            }

            return CloneAsBlocked(candidate, gateReason);
        }).ToArray();

        return new VanguardIntentDryRunBoard(snapshot, gated);
    }

    private static VanguardIntentCandidate CloneAsBlocked(VanguardIntentCandidate candidate, string gateReason)
    {
        return new VanguardIntentCandidate
        {
            IntentKey = candidate.IntentKey,
            Domain = candidate.Domain,
            Valid = false,
            Reason = candidate.Reason,
            BaseScore = candidate.BaseScore,
            FinalScore = 0f,
            Gate = "blocked_exclusive_authority:" + gateReason,
            TargetKey = candidate.TargetKey,
            PlanKey = candidate.PlanKey,
            NextStep = candidate.NextStep
        };
    }
}
#endif
