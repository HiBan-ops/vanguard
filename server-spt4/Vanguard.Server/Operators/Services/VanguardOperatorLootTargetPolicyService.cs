using SPTarkov.DI.Annotations;
using Vanguard.Server.Operators.Responses;
using Vanguard.Server.Operators.Storage;

// Responsibility: Coordinates Operator Loot Target Policy Service for the Operator domain services, delegating specialized work to its collaborators.
// Flow: Caller/route input is validated and normalized, canonical Operator/profile state is read or updated through the owning store/integration, then a response and diagnostics are produced.
// Authority boundary: Server domain orchestration only; persistent truth remains explicit in the Operator/SPT stores and client in-raid execution remains separate.
// Invariant: Operations stay profile-scoped, deterministic/idempotent where required, and partial failures do not silently corrupt canonical state.
namespace Vanguard.Server.Operators.Services;

/// <summary>
/// Persistent Operator loot-target authority. Stored policy is deliberately independent from F12/runtime
/// tuning: runtime settings may narrow an allowed target kind, but can never widen a persistent deny.
/// Unknown or missing values normalize fail-closed to CorpsesOnly for backward compatibility.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorLootTargetPolicyService(VanguardOperatorStore store)
{
    public const string CorpsesOnly = "CorpsesOnly";
    public const string ContainersOnly = "ContainersOnly";
    public const string CorpsesAndContainers = "CorpsesAndContainers";
    public const string Disabled = "Disabled";

    public async Task<VanguardOperatorLootTargetPolicyResponse> SetAsync(string profileId, string? operatorId, string? requestedPolicy)
    {
        string requestedProfileId = profileId;
        string storageProfileId = await store.ResolveStorageProfileIdAsync(profileId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(operatorId))
        {
            return Build(false, requestedProfileId, storageProfileId, "operator_id_required", operatorId, CorpsesOnly, now);
        }
        if (!TryNormalize(requestedPolicy, out string normalizedPolicy))
        {
            return Build(false, requestedProfileId, storageProfileId, "loot_target_policy_invalid", operatorId, CorpsesOnly, now);
        }

        IReadOnlyList<Vanguard.Server.Operators.Models.VanguardOperatorProfile> operators = await store.LoadOperatorsAsync(storageProfileId);
        Vanguard.Server.Operators.Models.VanguardOperatorProfile? existing = operators.FirstOrDefault(item =>
            string.Equals(item.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return Build(false, requestedProfileId, storageProfileId, "operator_not_found", operatorId, normalizedPolicy, now);
        }

        Vanguard.Server.Operators.Models.VanguardOperatorProfile updated = existing with
        {
            LootTargetPolicy = normalizedPolicy,
            UpdatedAtUtc = now
        };
        Vanguard.Server.Operators.Models.VanguardOperatorProfile[] next = operators
            .Select(item => string.Equals(item.OperatorId, existing.OperatorId, StringComparison.OrdinalIgnoreCase) ? updated : item)
            .ToArray();
        await store.SaveOperatorsAsync(storageProfileId, next);
        return Build(true, requestedProfileId, storageProfileId, "loot_target_policy_updated", updated.OperatorId, normalizedPolicy, now);
    }

    public static string NormalizeOrDefault(string? value)
        => TryNormalize(value, out string normalized) ? normalized : CorpsesOnly;

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = CorpsesOnly;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }
        string raw = value.Trim();
        if (string.Equals(raw, CorpsesOnly, StringComparison.OrdinalIgnoreCase)) { normalized = CorpsesOnly; return true; }
        if (string.Equals(raw, ContainersOnly, StringComparison.OrdinalIgnoreCase)) { normalized = ContainersOnly; return true; }
        if (string.Equals(raw, CorpsesAndContainers, StringComparison.OrdinalIgnoreCase)) { normalized = CorpsesAndContainers; return true; }
        if (string.Equals(raw, Disabled, StringComparison.OrdinalIgnoreCase)) { normalized = Disabled; return true; }
        return false;
    }

    private static VanguardOperatorLootTargetPolicyResponse Build(bool success, string requested, string storage, string reason, string? operatorId, string policy, DateTimeOffset now)
        => new(success, requested, storage, reason, operatorId, policy, now, VanguardBuildVersion.BuildLabel);
}
