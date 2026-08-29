using SPTarkov.DI.Annotations;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Responses;
using Vanguard.Server.Operators.Storage;

// Responsibility: Coordinates Operator Active Service Service for the Operator domain services, delegating specialized work to its collaborators.
// Flow: Caller/route input is validated and normalized, canonical Operator/profile state is read or updated through the owning store/integration, then a response and diagnostics are produced.
// Authority boundary: Server domain orchestration only; persistent truth remains explicit in the Operator/SPT stores and client in-raid execution remains separate.
// Invariant: Operations stay profile-scoped, deterministic/idempotent where required, and partial failures do not silently corrupt canonical state.
namespace Vanguard.Server.Operators.Services;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorActiveServiceService(
    VanguardOperatorStore store,
    VanguardDeploymentLimitService deploymentLimitService)
{
    public async Task<VanguardOperatorRaidSelectionResponse> SetRaidSelectionAsync(string profileId, string? operatorId, bool selectedForRaid)
    {
        var requestedProfileId = profileId;
        var storageProfileId = await store.ResolveStorageProfileIdAsync(profileId);
        var state = await store.LoadStateAsync(storageProfileId);
        var now = DateTimeOffset.UtcNow;
        var limits = await deploymentLimitService.GetLimitsAsync(storageProfileId);

        if (string.IsNullOrWhiteSpace(operatorId))
        {
            return new VanguardOperatorRaidSelectionResponse(false, requestedProfileId, storageProfileId, "operator_id_required", operatorId, selectedForRaid, false, state.ActiveService.Count(record => record.IsSelectedForRaid), state.ActiveService.Count, now, VanguardBuildVersion.BuildLabel);
        }

        var record = state.ActiveService.FirstOrDefault(item => string.Equals(item.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            return new VanguardOperatorRaidSelectionResponse(false, requestedProfileId, storageProfileId, "operator_not_in_active_service", operatorId, selectedForRaid, false, state.ActiveService.Count(item => item.IsSelectedForRaid), state.ActiveService.Count, now, VanguardBuildVersion.BuildLabel);
        }

        var medical = state.Medical.FirstOrDefault(item => string.Equals(item.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
        var isRecovering = medical?.RecoveryUntilUtc is DateTimeOffset recoveryUntil && recoveryUntil > now;
        if (selectedForRaid && isRecovering)
        {
            return new VanguardOperatorRaidSelectionResponse(false, requestedProfileId, storageProfileId, "operator_recovering", operatorId, selectedForRaid, record.IsSelectedForRaid, state.ActiveService.Count(item => item.IsSelectedForRaid), state.ActiveService.Count, now, VanguardBuildVersion.BuildLabel);
        }

        var selectedCountWithoutOperator = state.ActiveService.Count(item => item.IsSelectedForRaid && !string.Equals(item.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
        if (selectedForRaid && selectedCountWithoutOperator >= limits.MaxDeployableOperators)
        {
            return new VanguardOperatorRaidSelectionResponse(false, requestedProfileId, storageProfileId, "deployment_limit_reached", operatorId, selectedForRaid, record.IsSelectedForRaid, selectedCountWithoutOperator, state.ActiveService.Count, now, VanguardBuildVersion.BuildLabel);
        }

        var updated = record with { IsSelectedForRaid = selectedForRaid };
        var activeService = state.ActiveService.Select(item => string.Equals(item.OperatorId, record.OperatorId, StringComparison.OrdinalIgnoreCase) ? updated : item).ToArray();
        await store.SaveActiveServiceAsync(storageProfileId, activeService);

        return new VanguardOperatorRaidSelectionResponse(true, requestedProfileId, storageProfileId, "raid_selection_updated", operatorId, selectedForRaid, updated.IsSelectedForRaid, activeService.Count(item => item.IsSelectedForRaid), activeService.Length, now, VanguardBuildVersion.BuildLabel);
    }
}
