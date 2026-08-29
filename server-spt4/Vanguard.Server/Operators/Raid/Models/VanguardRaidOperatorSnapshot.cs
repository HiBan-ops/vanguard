using System.Text.Json.Nodes;
using Vanguard.Server.Operators.Models;

// Responsibility: Defines data/state contracts used by the raid Operator server workflow, centered on Raid Operator Snapshot.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Server.Operators.Raid.Models;

/// <summary>
/// Raid-facing immutable projection of an off-raid Operator.
/// OwnerProfileId is the player owner. It must never be replaced by the
/// profile/session id of the process that technically performs the spawn.
/// </summary>
public sealed record VanguardRaidOperatorSnapshot(
    string OperatorId,
    string OwnerProfileId,
    string OwnerNickname,
    string RaidSessionId,
    string OperatorInventoryProfileId,
    string DisplayName,
    string Callsign,
    string Side,
    int Level,
    int Experience,
    string Role,
    string Specialty,
    string ServiceStatus,
    bool IsSelectedForRaid,
    bool IsEligibleForRaid,
    string EligibilityReason,
    string MedicalStatus,
    double HealthRatio,
    bool InventoryProfileExists,
    int InventoryItemCount,
    bool HasEquipmentRoot,
    VanguardRaidOperatorSainPayload SainRuntime,
    DateTimeOffset GeneratedAtUtc,
    string BuildLabel,
    int SchemaVersion = VanguardOperatorSchema.CurrentVersion,
    string LootTargetPolicy = "CorpsesOnly")
{
    public static VanguardRaidOperatorSnapshot Empty(string ownerProfileId, string raidSessionId, string reason) => new(
        string.Empty,
        Normalize(ownerProfileId),
        string.Empty,
        Normalize(raidSessionId),
        string.Empty,
        reason,
        reason,
        string.Empty,
        0,
        0,
        string.Empty,
        string.Empty,
        string.Empty,
        false,
        false,
        reason,
        reason,
        0,
        false,
        0,
        false,
        VanguardRaidOperatorSainPayload.Empty,
        DateTimeOffset.UtcNow,
        VanguardBuildVersion.BuildLabel);

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}

public sealed record VanguardRaidOperatorSainPayload(
    string BasePersona,
    string Doctrine,
    string Temperament,
    string SainProfileFamily,
    string SainTuningPlan,
    string CombatStyle,
    string EngagementRange,
    string SquadRole,
    IReadOnlyList<string> Traits)
{
    public static readonly VanguardRaidOperatorSainPayload Empty = new(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        Array.Empty<string>());
}

public sealed record VanguardRaidOperatorSpawnProfile(
    VanguardRaidOperatorSnapshot Snapshot,
    JsonObject? OperatorProfileJson,
    JsonObject? OperatorPmcJson,
    JsonObject? OperatorInventoryJson,
    string Reason);

internal sealed record VanguardRaidInventoryItemSnapshot(
    string Id,
    string TemplateId,
    string? ParentId,
    string? SlotId,
    string? LocationJson,
    string? UpdJson);
