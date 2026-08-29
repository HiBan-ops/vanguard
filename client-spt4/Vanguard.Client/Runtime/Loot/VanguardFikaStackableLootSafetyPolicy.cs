#if SPT_CLIENT
using System;
using EFT.InventoryLogic;
using Vanguard.Client.Compatibility;

// Responsibility: Encodes the deterministic rules for Fika Stackable Loot Safety Policy within the loot runtime.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Runtime.Loot;

/// <summary>
/// The persistence path fail-closed network-safety boundary for multi-count inventory stacks.
/// Fika 2.3.9 broadcasts an inventory descriptor before the authoritative bot executes it locally,
/// while Vanguard's callback/readback only proves that authoritative local execution. Until a peer
/// replay acknowledgement exists, a multi-count stack must not be submitted through Vanguard under
/// Fika because a remote peer can reject CreateOperationFromDescriptor while the authority succeeds.
///
/// This policy is deliberately target-agnostic so the future world-container adapter can reuse the
/// same safety boundary. It does not split, clamp, merge, decrement, or otherwise rewrite quantities.
/// </summary>
internal static class VanguardFikaStackableLootSafetyPolicy
{
    public const string BlockReasonPrefix = "fika_stackable_peer_convergence_unproven";

    public static bool IsSafe(Item? item, out string reason)
    {
        if (item == null)
        {
            reason = "item_missing";
            return false;
        }

        int stackCount = Math.Max(0, item.StackObjectsCount);
        if (stackCount <= 1)
        {
            reason = "single_count_or_nonstack_item";
            return true;
        }

        if (!VanguardFikaCompat.IsInstalled)
        {
            reason = "non_fika_multi_count_stack_preserved";
            return true;
        }

        reason = $"{BlockReasonPrefix}:count={stackCount}";
        return false;
    }
}
#endif
