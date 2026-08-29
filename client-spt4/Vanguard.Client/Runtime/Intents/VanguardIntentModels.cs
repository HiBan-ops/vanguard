#if SPT_CLIENT
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;

// Responsibility: Defines data/state contracts used by the intent production pipeline, centered on Intent Models.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime.Intents;

internal sealed class VanguardIntentCandidate
{
    public string IntentKey { get; init; } = "none";
    public string Domain { get; init; } = "none";
    public bool Valid { get; init; } = true;
    public string Reason { get; init; } = "none";
    public float BaseScore { get; init; }
    public float FinalScore { get; set; }
    public string Gate { get; init; } = "valid";
    public string TargetKey { get; init; } = "none";
    public string PlanKey { get; init; } = "none";
    public string NextStep { get; init; } = "none";
}

internal sealed class VanguardIntentDryRunBoard
{
    public OperatorDecisionSnapshot Snapshot { get; }
    public IReadOnlyList<VanguardIntentCandidate> Candidates { get; }
    public VanguardIntentCandidate Selected { get; }
    public VanguardExecutionWindowSnapshot ExecutionWindow { get; }

    public VanguardIntentDryRunBoard(OperatorDecisionSnapshot snapshot, IEnumerable<VanguardIntentCandidate> candidates)
    {
        Snapshot = snapshot;
        var materialized = candidates.ToArray();
        Candidates = materialized;
        Selected = materialized
            .Where(candidate => candidate.Valid)
            .OrderByDescending(candidate => candidate.FinalScore)
            .ThenBy(candidate => candidate.IntentKey)
            .FirstOrDefault()
            ?? new VanguardIntentCandidate
            {
                IntentKey = "ObserveOnlyNoValidIntent",
                Domain = "Recovery",
                Valid = true,
                Reason = "no_valid_candidate",
                BaseScore = 0f,
                FinalScore = 0f,
                Gate = "fallback_observe_only"
            };
        ExecutionWindow = VanguardExecutionWindowReadOnlyBuilder.Build(snapshot, Selected);
    }

    public string Signature => string.Join("|",
        Selected.IntentKey,
        Selected.Domain,
        Selected.Gate,
        Snapshot.Alive ? "alive" : "dead",
        Snapshot.Threat.Classification,
        Snapshot.ThreatScan.Classification,
        Snapshot.ThreatScan.WouldPromote ? Snapshot.ThreatScan.CandidateThreatId : "scan_keep",
        Snapshot.Awareness.Classification,
        Snapshot.Awareness.DecisionSignature,
        Snapshot.SquadCohesion.DecisionSignature,
        Snapshot.Sain.Classification,
        Snapshot.Medical.Classification,
        Snapshot.Medical.Plan.PlanKey,
        Snapshot.Medical.Plan.NextStep,
        ExecutionWindow.Signature);
}

internal interface IVanguardIntentProducer
{
    IEnumerable<VanguardIntentCandidate> Produce(OperatorDecisionSnapshot snapshot);
}
#endif
