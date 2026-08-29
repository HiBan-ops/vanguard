#if SPT_CLIENT
using System;
using UnityEngine;

// Responsibility: Defines data/state contracts used by the runtime audit, centered on Operator Runtime Audit Snapshot.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime.Audit;

internal sealed class VanguardOperatorRuntimeAuditSnapshot
{
    public string OperatorId { get; init; } = string.Empty;
    public string OwnerProfileId { get; init; } = string.Empty;
    public string BotProfileId { get; init; } = string.Empty;
    public string Nickname { get; init; } = string.Empty;
    public bool Alive { get; init; }
    public Vector3 Position { get; init; }
    public float RealSpeed { get; init; }
    public string Movement { get; init; } = string.Empty;
    public string Brain { get; init; } = string.Empty;
    public string Sain { get; init; } = string.Empty;
    public string LootingBots { get; init; } = string.Empty;
    public string Orbit { get; init; } = string.Empty;
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Signature => $"alive={Alive}|move={Movement}|brain={Brain}|sain={Sain}|loot={LootingBots}|orbit={Orbit}";
}
#endif
