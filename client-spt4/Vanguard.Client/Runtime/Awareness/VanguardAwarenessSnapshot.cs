#if SPT_CLIENT
using System;

// Responsibility: Defines data/state contracts used by the combat-awareness runtime, centered on Awareness Snapshot.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime.Awareness;

internal sealed class VanguardAwarenessSnapshot
{
    public static VanguardAwarenessSnapshot Empty { get; } = new();

    public bool Enabled { get; init; }
    public bool Alive { get; init; }
    public VanguardAwarenessStimulusKind StimulusKind { get; init; } = VanguardAwarenessStimulusKind.None;
    public string CandidateId { get; init; } = "none";
    public string CandidateName { get; init; } = "none";
    public string Source { get; init; } = "none";
    public string Reason { get; init; } = "none";
    public string CandidateArc { get; init; } = "none";
    public float? CandidateDistance { get; init; }
    public float? CandidateSeenAgo { get; init; }
    public float Score { get; init; }
    public float Confidence { get; init; }
    public bool HasCandidate => !string.Equals(CandidateId, "none", StringComparison.OrdinalIgnoreCase);
    public bool CandidateVisible { get; init; }
    public bool CandidateLineOfSight { get; init; }
    public bool CandidateCanShoot { get; init; }
    public bool IncomingFireFresh { get; init; }
    public bool IncomingFireStale { get; init; }
    public bool CurrentTargetHealthy { get; init; }
    public bool ShouldOrientAttention { get; init; }
    public bool WouldPropagateConfirmedThreat { get; init; }
    public bool WouldPromoteSainTarget { get; init; }
    public bool WouldReleaseFormation { get; init; }
    public bool WouldBreakMedical { get; init; }
    public bool ShouldMaintainFormation { get; init; }
    public bool ReadOnly { get; init; } = true;
    public string Classification { get; init; } = "awareness_none";

    public string DecisionSignature => string.Join("|",
        Enabled ? "enabled" : "disabled",
        Alive ? "alive" : "dead",
        StimulusKind.ToString(),
        CandidateId,
        CandidateArc,
        CandidateVisible ? "visible" : "not_visible",
        CandidateLineOfSight ? "los" : "no_los",
        CandidateCanShoot ? "can_shoot" : "cannot_shoot",
        IncomingFireFresh ? "incoming_fresh" : IncomingFireStale ? "incoming_stale" : "no_incoming",
        WouldPromoteSainTarget ? "would_promote" : "no_promote",
        WouldReleaseFormation ? "would_release" : ShouldMaintainFormation ? "maintain" : "neutral",
        Classification);

    public string Summary => "kind=" + StimulusKind
        + ";candidate=" + CandidateId
        + ";source=" + Source
        + ";score=" + Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
        + ";confidence=" + Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
        + ";orient=" + Bool(ShouldOrientAttention)
        + ";propagate=" + Bool(WouldPropagateConfirmedThreat)
        + ";promote=" + Bool(WouldPromoteSainTarget)
        + ";release=" + Bool(WouldReleaseFormation)
        + ";maintain=" + Bool(ShouldMaintainFormation)
        + ";reason=" + Reason;

    private static string Bool(bool value) => value ? "true" : "false";
}
#endif
