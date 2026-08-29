#if SPT_CLIENT
using System;
using System.Collections.Generic;

// Responsibility: Defines data/state contracts used by the post-loot recovery runtime, centered on Post Loot Episode State.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime.PostLoot;

internal sealed class VanguardPostLootEpisodeState
{
    public bool WasLootActive { get; set; }
    public DateTimeOffset LastLootActiveAtUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastLootEndedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LootStateStaleSinceUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastSnapshotLogAtUtc { get; set; } = DateTimeOffset.MinValue;
    public string LastSnapshotKey { get; set; } = string.Empty;
    public bool StaleLootRecoveryAttempted { get; set; }
    public DateTimeOffset LastRecoveryAttemptAtUtc { get; set; } = DateTimeOffset.MinValue;
    public Dictionary<string, DateTimeOffset> LastSuspectLogAtByKey { get; } = new(StringComparer.OrdinalIgnoreCase);
}
#endif
