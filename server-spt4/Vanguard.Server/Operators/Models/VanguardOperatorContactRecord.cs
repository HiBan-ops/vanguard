// Responsibility: Defines data/state contracts used by the Operator persistence/domain models, centered on Operator Contact Record.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Server.Operators.Models;

public sealed record VanguardOperatorContactRecord(
    string OperatorId,
    string DisplayName,
    string ContactStatus,
    DateTimeOffset? FirstHiredAtUtc,
    DateTimeOffset? LastHiredAtUtc,
    DateTimeOffset? LastReleasedAtUtc,
    int ActiveServiceCount,
    int RaidTogetherCount,
    int Trust,
    int Loyalty,
    int Respect,
    int Grudge,
    string NarrativeSummary,
    IReadOnlyList<VanguardOperatorContactHistoryEntry> HistoryEvents,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int SchemaVersion = VanguardOperatorSchema.CurrentVersion);
