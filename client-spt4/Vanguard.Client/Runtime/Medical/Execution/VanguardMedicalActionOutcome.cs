#if SPT_CLIENT

// Responsibility: Define medical native-action outcome kinds and read-only progress evidence fields.
// Flow: Carry start, progress, completion or failure classifications alongside observed health and item effects.
// Authority boundary: These outcome models describe executor observations; they do not initiate or authorize a medical action.
// Invariant: A reported outcome retains its distinct classification rather than conflating progress with completion.
namespace Vanguard.Client.Runtime.Medical.Execution;

internal enum VanguardMedicalActionOutcomeKind
{
    None = 0,
    Started = 1,
    Progress = 2,
    Completed = 3,
    Failed = 4,
    Timeout = 5,
    Interrupted = 6,
}

internal sealed class VanguardMedicalActionProgressSnapshot
{
    public bool FirstAidUsing { get; init; }
    public bool NeedResolved { get; init; }
    public bool NeedStillPresent { get; init; }
    public bool TargetResolved { get; init; }
    public bool TargetStillPresent { get; init; }
    public bool HealthImproved { get; init; }
    public bool TargetHealthImproved { get; init; }
    public bool SurgeryTargetRestored { get; init; }
    public bool TargetDestroyedReadable { get; init; }
    public bool CurrentTargetDestroyed { get; init; }
    public bool ItemInventoryObserved { get; init; }
    public bool ItemInstanceFound { get; init; }
    public bool ExactItemAbsentFromObservedInventory { get; init; }
    public bool ItemResourceReadable { get; init; }
    public bool ItemResourceConsumed { get; init; }
    public bool ResourceConsumedWithoutTargetEffect { get; init; }
    public float CurrentItemResource { get; init; } = -1f;
    public bool AnyMedicalEffectObserved { get; init; }
    public bool NoMedicalEffectObserved { get; init; }
    public int CurrentHealthPercent { get; init; } = -1;
    public float CurrentTargetHealth { get; init; } = -1f;
    public float CurrentTargetMaxHealth { get; init; } = -1f;
    public string CurrentNeedTargetPart { get; init; } = "none";
    public bool ThreatInterrupt { get; init; }
    public bool OperatorDead { get; init; }
    public bool TerminalAliveConfirmed { get; init; }
    public bool TerminalDeadConfirmed { get; init; }
    public bool TerminalUnknown { get; init; }
    public string TerminalReason { get; init; } = "none";
    public string Reason { get; init; } = "none";

    public string EffectSummary => "needResolved=" + Bool(NeedResolved)
        + ";needStillPresent=" + Bool(NeedStillPresent)
        + ";targetResolved=" + Bool(TargetResolved)
        + ";targetStillPresent=" + Bool(TargetStillPresent)
        + ";healthImproved=" + Bool(HealthImproved)
        + ";targetHealthImproved=" + Bool(TargetHealthImproved)
        + ";surgeryTargetRestored=" + Bool(SurgeryTargetRestored)
        + ";targetDestroyedReadable=" + Bool(TargetDestroyedReadable)
        + ";targetDestroyed=" + Bool(CurrentTargetDestroyed)
        + ";itemInventoryObserved=" + Bool(ItemInventoryObserved)
        + ";itemInstanceFound=" + Bool(ItemInstanceFound)
        + ";exactItemAbsent=" + Bool(ExactItemAbsentFromObservedInventory)
        + ";itemResourceReadable=" + Bool(ItemResourceReadable)
        + ";itemResourceConsumed=" + Bool(ItemResourceConsumed)
        + ";resourceConsumedWithoutTargetEffect=" + Bool(ResourceConsumedWithoutTargetEffect)
        + ";itemResource=" + CurrentItemResource.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
        + ";effectObserved=" + Bool(AnyMedicalEffectObserved)
        + ";noEffect=" + Bool(NoMedicalEffectObserved)
        + ";hp=" + CurrentHealthPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ";targetHp=" + CurrentTargetHealth.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
        + "/" + CurrentTargetMaxHealth.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
        + ";currentNeedTarget=" + Safe(CurrentNeedTargetPart)
        + ";terminalAlive=" + Bool(TerminalAliveConfirmed)
        + ";terminalDead=" + Bool(TerminalDeadConfirmed)
        + ";terminalUnknown=" + Bool(TerminalUnknown)
        + ";terminalReason=" + Safe(TerminalReason)
        + ";reason=" + Safe(Reason);

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
}
#endif
