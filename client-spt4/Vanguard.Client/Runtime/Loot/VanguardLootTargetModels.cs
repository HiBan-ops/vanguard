#if SPT_CLIENT
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;

// Responsibility: Defines data/state contracts used by the loot runtime, centered on Loot Target Models.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime.Loot;

internal enum VanguardLootTargetKind
{
    Corpse = 0,
    WorldContainer = 1
}

/// <summary>
/// Persistent Operator target policy. The runtime settings path transports the off-raid profile value through the
/// raid manifest into runtime; missing/legacy profiles still fail closed to CorpsesOnly. Runtime/F12
/// tuning remains a second AND gate only and can never widen this policy.
/// </summary>
internal enum VanguardOperatorLootTargetPolicyMode
{
    CorpsesOnly = 0,
    ContainersOnly = 1,
    CorpsesAndContainers = 2,
    Disabled = 3
}

internal static class VanguardOperatorLootTargetPermissionPolicy
{
    public static bool AllowsTarget(
        VanguardOperatorLootPermissionSnapshot permissions,
        VanguardLootTargetKind targetKind,
        out string reason)
    {
        bool operatorPolicyAllows = permissions.OperatorTargetPolicy switch
        {
            VanguardOperatorLootTargetPolicyMode.CorpsesOnly => targetKind == VanguardLootTargetKind.Corpse,
            VanguardOperatorLootTargetPolicyMode.ContainersOnly => targetKind == VanguardLootTargetKind.WorldContainer,
            VanguardOperatorLootTargetPolicyMode.CorpsesAndContainers => true,
            VanguardOperatorLootTargetPolicyMode.Disabled => false,
            _ => false
        };

        if (!operatorPolicyAllows)
        {
            reason = "operator_policy_blocks_" + TargetToken(targetKind);
            return false;
        }

        bool runtimeAllows = targetKind switch
        {
            VanguardLootTargetKind.Corpse => permissions.LootAccessibleCorpses,
            VanguardLootTargetKind.WorldContainer => permissions.LootWorldContainers,
            _ => false
        };

        if (!runtimeAllows)
        {
            reason = "runtime_tuning_blocks_" + TargetToken(targetKind);
            return false;
        }

        reason = "allowed";
        return true;
    }

    private static string TargetToken(VanguardLootTargetKind targetKind)
        => targetKind == VanguardLootTargetKind.WorldContainer ? "world_container" : "corpse";
}

/// <summary>
/// Minimal target-agnostic read model. It intentionally contains no movement, claim, interaction,
/// transaction, or persistence authority. Those remain owned by the established loot execution pipeline.
/// </summary>
internal sealed class VanguardLootTargetSnapshot
{
    public VanguardLootTargetKind Kind { get; init; }
    public string TargetId { get; init; } = "none";
    public Vector3 Position { get; init; }
    public Item RootItem { get; init; } = null!;
    public string Source { get; init; } = "none";
    public bool RequiresOpenInteraction { get; init; }
    public bool IsOpen { get; init; }
    public bool IsLocked { get; init; }

    public string CanonicalKey => KindToken + ":" + Normalize(TargetId);
    public string KindToken => Kind == VanguardLootTargetKind.WorldContainer ? "world_container" : "corpse";

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
}

internal sealed class VanguardWorldLootContainerSnapshot
{
    public LootableContainer Container { get; init; } = null!;
    public VanguardLootTargetSnapshot Target { get; init; } = null!;
    public EDoorState DoorState { get; init; }

    public string ContainerId => Target.TargetId;
    public Item RootItem => Target.RootItem;
    public Vector3 Position => Target.Position;
    public string CanonicalKey => Target.CanonicalKey;
}
#endif
