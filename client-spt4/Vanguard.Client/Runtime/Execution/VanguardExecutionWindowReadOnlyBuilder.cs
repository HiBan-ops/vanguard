#if SPT_CLIENT
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Intents;

// Responsibility: Builds Execution Window Read Only Builder data for the execution arbitration runtime from already-available inputs.
// Flow: Normalized inputs are combined deterministically into a result consumed by the next policy, scheduler, UI, or transport stage.
// Authority boundary: Composition only; underlying gameplay/persistence truth remains owned by the source inputs.
// Invariant: Building a result must not perform hidden world mutation or acquire a competing authority.
namespace Vanguard.Client.Runtime.Execution;

internal static class VanguardExecutionWindowReadOnlyBuilder
{
    public static VanguardExecutionWindowSnapshot Build(OperatorDecisionSnapshot snapshot, VanguardIntentCandidate selected)
    {
        return VanguardExecutionWindowPolicy.Build(snapshot, selected);
    }
}
#endif
