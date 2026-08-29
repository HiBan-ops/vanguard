#if SPT_CLIENT
using System;
using Vanguard.Client.Runtime.Medical;

// Responsibility: Coordinates sequencing and arbitration for Execution Lease Coordinator across the execution arbitration runtime.
// Flow: Subsystem snapshots/intents are gathered, precedence and lease rules select the active path, and work is dispatched to specialized executors/services.
// Authority boundary: Coordinator owns ordering, not the underlying EFT/SPT/Fika truth; specialized domains keep their explicit authority boundaries.
// Invariant: Only one compatible authority path may win a conflicting action window, with stale/failed work released rather than accumulated.
namespace Vanguard.Client.Runtime.Execution;

internal static class VanguardExecutionLeaseCoordinator
{
    public static bool HasActiveLease(string? botProfileId) => VanguardExecutionLeaseStore.TryGetActive(botProfileId, out _);

    public static bool TryGetActiveLease(string? botProfileId, out VanguardExecutionLeaseState lease)
        => VanguardExecutionLeaseStore.TryGetActive(botProfileId, out lease);

    public static bool IsCooldownBlocked(string? botProfileId, VanguardMedicalNeed need, string? targetPart, string? itemTemplateId, DateTimeOffset now, out DateTimeOffset untilUtc)
    {
        return VanguardExecutionLeaseStore.IsCooldownBlocked(botProfileId, need, targetPart, itemTemplateId, now, out untilUtc);
    }

    public static bool IsCooldownBlocked(string? botProfileId, VanguardMedicalNeed need, string? targetPart, string? itemTemplateId, string? itemInstanceId, DateTimeOffset now, out DateTimeOffset untilUtc)
    {
        return VanguardExecutionLeaseStore.IsCooldownBlocked(botProfileId, need, targetPart, itemTemplateId, itemInstanceId, now, out untilUtc);
    }

    public static bool TryGetOutcome(string? botProfileId, VanguardMedicalNeed need, string? targetPart, string? itemTemplateId, out VanguardExecutionOutcomeMemoryRecord outcome)
    {
        return VanguardExecutionLeaseStore.TryGetOutcome(botProfileId, need, targetPart, itemTemplateId, out outcome);
    }
}
#endif
