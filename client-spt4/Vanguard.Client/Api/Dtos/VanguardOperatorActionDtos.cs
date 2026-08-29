using System;
using System.Collections.Generic;

// Responsibility: Defines client/server transport DTOs used by the client/server API contracts.
// Flow: API responses and requests are normalized into these data-only shapes before being consumed by higher-level client logic.
// Authority boundary: Transport data only; server persistence and in-raid runtime services remain authoritative for the represented state.
// Invariant: DTOs remain serialization-safe, side-effect free, and tolerant of compatible server data.
namespace Vanguard.Client.Api.Dtos;

internal sealed class VanguardHireContractRequestDto
{
    public string? OfferId { get; set; }
    public string? OperatorId { get; set; }
}

internal sealed class VanguardDismissOperatorRequestDto
{
    public string? OperatorId { get; set; }
}

internal sealed class VanguardSetOperatorRaidSelectionRequestDto
{
    public string? OperatorId { get; set; }
    public bool SelectedForRaid { get; set; }
}

internal sealed class VanguardOperatorMedicalTreatmentRequestDto
{
    public string? OperatorId { get; set; }
    public bool ConfirmTreatment { get; set; } = true;
}

internal sealed class VanguardSignBillingRequestDto
{
    public List<string>? InvoiceIds { get; set; }
}

internal sealed class VanguardOperatorHireResponseDto
{
    public bool Success { get; set; }
    public string? Reason { get; set; }
    public VanguardOperatorProfileDto? Operator { get; set; }
    public VanguardActiveServiceRecordDto? ActiveService { get; set; }
    public VanguardOperatorDeploymentLimitsDto? Limits { get; set; }
    public int RemainingContractCount { get; set; }
    public int ActiveServiceCount { get; set; }
    public bool BillingDebtCreated { get; set; }
    public VanguardOperatorBillingInvoiceDto? BillingInvoice { get; set; }
    public VanguardOperatorBillingSnapshotDto? Billing { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public string? BuildLabel { get; set; }
}

internal sealed class VanguardOperatorDismissResponseDto
{
    public bool Success { get; set; }
    public string? Reason { get; set; }
    public VanguardOperatorProfileDto? ReleasedOperator { get; set; }
    public VanguardActiveServiceRecordDto? ReleasedActiveService { get; set; }
    public VanguardOperatorDeploymentLimitsDto? Limits { get; set; }
    public int RemainingOperatorCount { get; set; }
    public int ActiveServiceCount { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public string? BuildLabel { get; set; }
}

internal sealed class VanguardOperatorRaidSelectionResponseDto
{
    public bool Success { get; set; }
    public string? Reason { get; set; }
    public string? OperatorId { get; set; }
    public bool RequestedSelection { get; set; }
    public bool IsSelectedForRaid { get; set; }
    public int SelectedForRaidCount { get; set; }
    public int ActiveServiceCount { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public string? BuildLabel { get; set; }
}

internal sealed class VanguardOperatorMedicalTreatmentResponseDto
{
    public bool Success { get; set; }
    public string? Reason { get; set; }
    public string? OperatorId { get; set; }
    public string? DisplayName { get; set; }
    public double HealthBefore { get; set; }
    public double HealthAfter { get; set; }
    public int Amount { get; set; }
    public VanguardOperatorBillingInvoiceDto? BillingInvoice { get; set; }
    public VanguardOperatorBillingSnapshotDto? Billing { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public string? BuildLabel { get; set; }
}

internal sealed class VanguardOperatorBillingActionResponseDto
{
    public bool Success { get; set; }
    public string? Reason { get; set; }
    public int InvoiceCount { get; set; }
    public int Amount { get; set; }
    public bool SettlementAttempted { get; set; }
    public bool SettlementSucceeded { get; set; }
    public List<VanguardOperatorBillingInvoiceDto>? ProcessedInvoices { get; set; }
    public VanguardOperatorBillingSnapshotDto? Billing { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public string? BuildLabel { get; set; }
}

internal sealed class VanguardSetOperatorLootTargetPolicyRequestDto
{
    public string? OperatorId { get; set; }
    public string? LootTargetPolicy { get; set; }
}

internal sealed class VanguardOperatorLootTargetPolicyResponseDto
{
    public bool Success { get; set; }
    public string? RequestedProfileId { get; set; }
    public string? StorageProfileId { get; set; }
    public string? Reason { get; set; }
    public string? OperatorId { get; set; }
    public string? LootTargetPolicy { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; }
    public string? BuildLabel { get; set; }
}
