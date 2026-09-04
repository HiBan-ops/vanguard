using System;
using System.Collections.Generic;

// Responsibility: Defines client/server transport DTOs used by the client/server API contracts.
// Flow: API responses and requests are normalized into these data-only shapes before being consumed by higher-level client logic.
// Authority boundary: Transport data only; server persistence and in-raid runtime services remain authoritative for the represented state.
// Invariant: DTOs remain serialization-safe, side-effect free, and tolerant of compatible server data.
namespace Vanguard.Client.Api.Dtos;

internal sealed class VanguardOperatorInventoryModeRequestDto
{
    public string? OperatorId { get; set; }

    public bool Confirm { get; set; }
}


internal sealed class VanguardOperatorInventoryDirectCommitRequestDto
{
    public string? OperatorId { get; set; }

    public bool Confirm { get; set; }

    public string? ProfileDescriptorJson { get; set; }

    public string? SnapshotSource { get; set; }

    public int ClientItemCount { get; set; }
}

internal sealed class VanguardOperatorInventoryModeResponseDto
{
    public bool Success { get; set; }

    public string? Reason { get; set; }

    public bool Active { get; set; }

    public string? RequestedProfileId { get; set; }

    public string? StorageProfileId { get; set; }

    public string? OperatorId { get; set; }

    public string? OperatorDisplayName { get; set; }

    public string? OperatorCallsign { get; set; }

    public string? OperatorInventoryProfileId { get; set; }

    public DateTimeOffset? GeneratedAtUtc { get; set; }

    public VanguardOperatorInventorySummaryDto? Summary { get; set; }
}

internal sealed class VanguardOperatorInventorySummaryResponseDto
{
    public string? RequestedProfileId { get; set; }

    public string? StorageProfileId { get; set; }

    public List<VanguardOperatorInventorySummaryDto>? Summaries { get; set; }

    public DateTimeOffset? GeneratedAtUtc { get; set; }
}

internal sealed class VanguardOperatorInventorySummaryDto
{
    public string? OperatorId { get; set; }

    public string? DisplayName { get; set; }

    public string? InventoryProfileId { get; set; }

    public bool ProfileExists { get; set; }

    public int ItemCount { get; set; }

    public bool HasPrimaryWeapon { get; set; }

    public bool HasBackpack { get; set; }

    public bool HasTacticalVest { get; set; }

    public bool HasArmorVest { get; set; }

    public string? ReadinessState { get; set; }

    public DateTimeOffset? LastSavedUtc { get; set; }
}
