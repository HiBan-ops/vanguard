#if SPT_CLIENT
using System.Globalization;

// Responsibility: Defines data/state contracts used by the execution arbitration runtime, centered on Execution Window Snapshot.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime.Execution;

internal sealed class VanguardExecutionWindowSnapshot
{
    public static VanguardExecutionWindowSnapshot Empty { get; } = new();

    public bool Readable { get; init; }
    public string ContractKey { get; init; } = "none";
    public string WindowKind { get; init; } = "none";
    public string IntentKey { get; init; } = "none";
    public string Domain { get; init; } = "none";
    public string TargetKey { get; init; } = "none";
    public string MinDurationSeconds { get; init; } = "0.00";
    public string MaxDurationSeconds { get; init; } = "0.00";
    public string NoProgressTimeoutSeconds { get; init; } = "0.00";
    public string ProgressSignals { get; init; } = "none";
    public string CompletionSignals { get; init; } = "none";
    public string FailureSignals { get; init; } = "none";
    public string InterruptionRules { get; init; } = "none";
    public string FallbackIntentKey { get; init; } = "none";
    public string OutcomePreview { get; init; } = "none";
    public bool WouldOpenIfActive { get; init; }
    public bool BlocksOtherPrimaryActions { get; init; }
    public bool RequiresStationary { get; init; }
    public bool AllowsMovement { get; init; }
    public bool AllowsCombat { get; init; }
    public bool AllowsFollow { get; init; }
    public bool AllowsMedical { get; init; }
    public bool ReadOnly { get; init; } = true;

    public string Signature => string.Join("|",
        Readable ? "readable" : "unreadable",
        ContractKey,
        WindowKind,
        IntentKey,
        Domain,
        TargetKey,
        WouldOpenIfActive ? "would_open" : "observe_only",
        FallbackIntentKey,
        OutcomePreview);

    public string Summary => "contract=" + Safe(ContractKey)
        + ";kind=" + Safe(WindowKind)
        + ";intent=" + Safe(IntentKey)
        + ";domain=" + Safe(Domain)
        + ";target=" + Safe(TargetKey)
        + ";min=" + Safe(MinDurationSeconds)
        + ";max=" + Safe(MaxDurationSeconds)
        + ";noProgress=" + Safe(NoProgressTimeoutSeconds)
        + ";progress=" + Safe(ProgressSignals)
        + ";completion=" + Safe(CompletionSignals)
        + ";failure=" + Safe(FailureSignals)
        + ";interruptions=" + Safe(InterruptionRules)
        + ";fallback=" + Safe(FallbackIntentKey)
        + ";outcome=" + Safe(OutcomePreview)
        + ";wouldOpenIfActive=" + Bool(WouldOpenIfActive)
        + ";blocksPrimary=" + Bool(BlocksOtherPrimaryActions)
        + ";stationary=" + Bool(RequiresStationary)
        + ";movement=" + Bool(AllowsMovement)
        + ";combat=" + Bool(AllowsCombat)
        + ";follow=" + Bool(AllowsFollow)
        + ";medical=" + Bool(AllowsMedical)
        + ";readOnly=" + Bool(ReadOnly);

    internal static string Seconds(float seconds) => seconds.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        return value.Trim()
            .Replace(' ', '_')
            .Replace(';', '_')
            .Replace('|', '_')
            .Replace('\r', '_')
            .Replace('\n', '_');
    }
}
#endif
