// Responsibility: Defines data/state contracts used by the raid Operator HUD, centered on Raid Fixed Operator Hud Semantic State.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Raid.Hud;

internal enum VanguardRaidFixedOperatorHudAlertSeverity
{
    None = 0,
    Attention = 1,
    Critical = 2,
    Stale = 3,
}

internal sealed record VanguardRaidFixedOperatorHudSemanticState(
    string ActivityLabel,
    string AlertLabel,
    VanguardRaidFixedOperatorHudAlertSeverity AlertSeverity,
    string Detail,
    bool Authoritative,
    bool Fresh,
    bool Urgent)
{
    public string DisplaySignature => string.Join("|",
        ActivityLabel,
        AlertLabel,
        AlertSeverity,
        Detail,
        Authoritative ? "authoritative" : "unavailable",
        Fresh ? "fresh" : "stale");
}
