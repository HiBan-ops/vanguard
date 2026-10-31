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

    // Stable raid-projected tactical identity copied directly from the server manifest at bind time.
    // These are upstream Vanguard identity facts, not derived SAIN tuning values.
    public string TacticalRole { get; init; } = string.Empty;
    public string TacticalSpecialty { get; init; } = string.Empty;
    public string BasePersona { get; init; } = string.Empty;
    public string Doctrine { get; init; } = string.Empty;
    public string Temperament { get; init; } = string.Empty;
    public string CombatStyle { get; init; } = string.Empty;
    public string EngagementRange { get; init; } = string.Empty;
    public string SquadRole { get; init; } = string.Empty;
    public int IdentitySchemaVersion { get; init; }

    public bool IsSpawnedByHeadless { get; init; }
    public bool IsLocalPlayerOwner { get; init; }
    public DateTimeOffset BoundAtUtc { get; init; } = DateTimeOffset.UtcNow;

    // Runtime incarnation is monotonic for one bot profile within the raid. Persistent Operator identity
    // remains stable, but every concrete BotOwner instance is a distinct runtime incarnation.
    public long RuntimeIncarnation { get; init; }
    public DateTimeOffset? RuntimeDetachedAtUtc { get; internal set; }
    public string RuntimeDetachReason { get; internal set; } = "none";
    // Captured from the exact outgoing BotOwner before its reference is cleared. False is required for
    // any explicit replacement; a terminally dead incarnation can never be resurrected by a later ProfileId match.
    public bool? RuntimeWasDeadAtDetach { get; internal set; }
    // null while attached; true only when every critical presence-loss cleanup step completed.
    // A failed seal quarantines this profile from exact-object replacement for the rest of the raid.
    public bool? RuntimeCleanupSucceeded { get; internal set; }

#if SPT_CLIENT
    public EFT.BotOwner? BotOwner { get; internal set; }
    public bool IsRuntimePresent => BotOwner != null && !RuntimeDetachedAtUtc.HasValue;
#endif
}
