using System;
using System.Collections.Generic;

// Responsibility: Defines client/server transport DTOs used by the client/server API contracts.
// Flow: API responses and requests are normalized into these data-only shapes before being consumed by higher-level client logic.
// Authority boundary: Transport data only; server persistence and in-raid runtime services remain authoritative for the represented state.
// Invariant: DTOs remain serialization-safe, side-effect free, and tolerant of compatible server data.
namespace Vanguard.Client.Api.Dtos;

internal sealed class VanguardTacticalAuthoringLiveExchangeRequestDto
{
    public string Role { get; set; } = string.Empty;
    public string ClientBuild { get; set; } = string.Empty;
    public string ClientLabel { get; set; } = string.Empty;
    public List<string> KnownOwnerProfileIds { get; set; } = new();
    public VanguardTacticalAuthoringLiveAuthorSnapshotDto? Author { get; set; }
    public List<VanguardTacticalAuthoringLiveHeadlessResultDto> HeadlessResults { get; set; } = new();
}

internal sealed class VanguardTacticalAuthoringLiveExchangeResponseDto
{
    public bool Success { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<VanguardTacticalAuthoringLiveAuthorSnapshotDto> Authors { get; set; } = new();
    public List<VanguardTacticalAuthoringLiveHeadlessResultDto> HeadlessResults { get; set; } = new();
    public DateTimeOffset ServerTimeUtc { get; set; }
}

internal sealed class VanguardTacticalAuthoringLiveAuthorSnapshotDto
{
    public string OwnerProfileId { get; set; } = string.Empty;
    public string LiveSessionId { get; set; } = string.Empty;
    public string MapId { get; set; } = string.Empty;
    public bool Active { get; set; }
    public long Revision { get; set; }
    public string SelectedZoneId { get; set; } = string.Empty;
    public string MapJson { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string ClientBuild { get; set; } = string.Empty;
}

internal sealed class VanguardTacticalAuthoringLiveHeadlessResultDto
{
    public string OwnerProfileId { get; set; } = string.Empty;
    public string LiveSessionId { get; set; } = string.Empty;
    public string MapId { get; set; } = string.Empty;
    public long AuthorRevision { get; set; }
    public string ResultJson { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string HeadlessBuild { get; set; } = string.Empty;
}
