using SPTarkov.Server.Core.Models.Eft.Bot;
using SPTarkov.Server.Core.Models.Utils;

// Responsibility: Defines request payloads accepted by the raid Operator server workflow.
// Flow: Caller input is deserialized into these data-only shapes, then validated and executed by the owning route/service.
// Authority boundary: Transport contract only; it does not authorize, persist, or execute the requested operation.
// Invariant: Payload defaults remain backward-compatible and contain no hidden side effects.
namespace Vanguard.Server.Operators.Raid.Requests;

public sealed record VanguardRaidManifestForProfilesRequest(
    IReadOnlyList<string>? ProfileIds,
    string? RaidSessionId = null) : IRequestData;

public sealed record VanguardGenerateOperatorBotRequest(
    GenerateBotsRequestData? Info,
    string? OperatorId,
    string? OwnerProfileId,
    string? RaidSessionId = null) : IRequestData;
