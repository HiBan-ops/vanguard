#if SPT_CLIENT

// Responsibility: Defines data/state contracts used by the medical runtime, centered on Medical Plan Snapshot.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime.Medical;

internal sealed class VanguardMedicalPlanSnapshot
{
    public static VanguardMedicalPlanSnapshot Empty { get; } = new();

    public bool Readable { get; init; }
    public string PlanKey { get; init; } = "none";
    public string NextStep { get; init; } = "none";
    public string ExecutionKind { get; init; } = "none";
    public string TargetPart { get; init; } = "none";
    public string ItemName { get; init; } = "none";
    public string ItemTemplateId { get; init; } = "none";
    public string SafetyGate { get; init; } = "none";
    public string ActionabilityGate { get; init; } = "none";
    public string RetryPolicy { get; init; } = "none";
    public string Reason { get; init; } = "none";
    public bool WouldRequireMovement { get; init; }
    public bool WouldRequireStationary { get; init; }
    public bool WouldAllowMobile { get; init; }
    public bool WouldWait { get; init; }
    public bool WouldRecheck { get; init; }
    public bool WouldExecuteIfActive { get; init; }
    public float SuggestedPriority { get; init; }

    public string Summary => "plan=" + Safe(PlanKey)
        + ";step=" + Safe(NextStep)
        + ";kind=" + Safe(ExecutionKind)
        + ";target=" + Safe(TargetPart)
        + ";item=" + Safe(ItemName)
        + ";tpl=" + Safe(ItemTemplateId)
        + ";safety=" + Safe(SafetyGate)
        + ";actionability=" + Safe(ActionabilityGate)
        + ";retry=" + Safe(RetryPolicy)
        + ";move=" + Bool(WouldRequireMovement)
        + ";stationary=" + Bool(WouldRequireStationary)
        + ";mobile=" + Bool(WouldAllowMobile)
        + ";wait=" + Bool(WouldWait)
        + ";recheck=" + Bool(WouldRecheck)
        + ";wouldExecuteIfActive=" + Bool(WouldExecuteIfActive)
        + ";priority=" + SuggestedPriority.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
        + ";reason=" + Safe(Reason);

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
