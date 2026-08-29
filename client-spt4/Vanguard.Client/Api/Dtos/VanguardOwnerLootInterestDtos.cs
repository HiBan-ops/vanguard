using System;
using System.Collections.Generic;

// Responsibility: Defines client/server transport DTOs used by the client/server API contracts.
// Flow: API responses and requests are normalized into these data-only shapes before being consumed by higher-level client logic.
// Authority boundary: Transport data only; server persistence and in-raid runtime services remain authoritative for the represented state.
// Invariant: DTOs remain serialization-safe, side-effect free, and tolerant of compatible server data.
namespace Vanguard.Client.Api.Dtos;

internal sealed class VanguardOwnerLootInterestEntryDto
{
    public string TemplateId { get; set; } = string.Empty;
    public string Group { get; set; } = "Other";
}

internal sealed class VanguardOwnerLootInterestSetRequestDto
{
    public string? OwnerProfileId { get; set; }
    public long Revision { get; set; }
    public string? ContentHash { get; set; }
    public string? Source { get; set; }
    public string? ClientBuild { get; set; }
    public List<VanguardOwnerLootInterestEntryDto> Entries { get; set; } = new();
}

internal sealed class VanguardOwnerLootInterestGetRequestDto
{
    public string? OwnerProfileId { get; set; }
    public string? Source { get; set; }
    public string? ClientBuild { get; set; }
}

internal sealed class VanguardOwnerLootInterestResponseDto
{
    public bool Success { get; set; }
    public string Reason { get; set; } = "none";
    public string OwnerProfileId { get; set; } = string.Empty;
    public long Revision { get; set; }
    public string ContentHash { get; set; } = "none";
    public string Source { get; set; } = "none";
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string BuildLabel { get; set; } = string.Empty;
    public List<VanguardOwnerLootInterestEntryDto> Entries { get; set; } = new();
}
