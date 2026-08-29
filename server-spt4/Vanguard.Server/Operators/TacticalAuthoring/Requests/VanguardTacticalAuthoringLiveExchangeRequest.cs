using SPTarkov.Server.Core.Models.Utils;

// Responsibility: Defines request payloads accepted by the server tactical-authoring relay.
// Flow: Caller input is deserialized into these data-only shapes, then validated and executed by the owning route/service.
// Authority boundary: Transport contract only; it does not authorize, persist, or execute the requested operation.
// Invariant: Payload defaults remain backward-compatible and contain no hidden side effects.
namespace Vanguard.Server.Operators.TacticalAuthoring.Requests;

public sealed record VanguardTacticalAuthoringLiveAuthorSnapshot(
    string OwnerProfileId = "",
    string LiveSessionId = "",
    string MapId = "",
    bool Active = false,
    long Revision = 0,
    string SelectedZoneId = "",
    string MapJson = "",
    DateTimeOffset UpdatedAtUtc = default,
    string ClientBuild = "");

public sealed record VanguardTacticalAuthoringLiveHeadlessResult(
    string OwnerProfileId = "",
    string LiveSessionId = "",
    string MapId = "",
    long AuthorRevision = 0,
    string ResultJson = "",
    DateTimeOffset UpdatedAtUtc = default,
    string HeadlessBuild = "");

public sealed record VanguardTacticalAuthoringLiveExchangeRequest(
    string Role = "",
    string ClientBuild = "",
    string ClientLabel = "",
    List<string>? KnownOwnerProfileIds = null,
    VanguardTacticalAuthoringLiveAuthorSnapshot? Author = null,
    List<VanguardTacticalAuthoringLiveHeadlessResult>? HeadlessResults = null) : IRequestData;
