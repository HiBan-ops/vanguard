using System;

// Responsibility: Defines data/state contracts used by the raid-runtime state, centered on Raid Operator Runtime Record.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Raid.Runtime;

/// <summary>
/// Runtime mapping between a persistent Vanguard Operator and the bot profile
/// created for the current raid. OwnerProfileId is always the player owner;
/// IsSpawnedByHeadless only describes the technical Fika spawn authority.
/// </summary>
internal sealed class VanguardRaidOperatorRuntimeRecord
{
    public string OperatorId { get; init; } = string.Empty;
    public string OwnerProfileId { get; init; } = string.Empty;
    public string BotProfileId { get; init; } = string.Empty;
    public string BotNickname { get; init; } = string.Empty;
    public string RaidSessionId { get; init; } = string.Empty;
    public string LootTargetPolicy { get; init; } = "CorpsesOnly";
    public bool IsSpawnedByHeadless { get; init; }
    public bool IsLocalPlayerOwner { get; init; }
    public DateTimeOffset BoundAtUtc { get; init; } = DateTimeOffset.UtcNow;

#if SPT_CLIENT
    public EFT.BotOwner? BotOwner { get; init; }
#endif
}
