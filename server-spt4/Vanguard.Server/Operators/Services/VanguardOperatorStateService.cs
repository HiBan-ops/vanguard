using SPTarkov.DI.Annotations;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Raid.Persistence.Services;
using Vanguard.Server.Operators.Responses;
using Vanguard.Server.Operators.Storage;

// Responsibility: Coordinates Operator State Service for the Operator domain services, delegating specialized work to its collaborators.
// Flow: Caller/route input is validated and normalized, canonical Operator/profile state is read or updated through the owning store/integration, then a response and diagnostics are produced.
// Authority boundary: Server domain orchestration only; persistent truth remains explicit in the Operator/SPT stores and client in-raid execution remains separate.
// Invariant: Operations stay profile-scoped, deterministic/idempotent where required, and partial failures do not silently corrupt canonical state.
namespace Vanguard.Server.Operators.Services;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorStateService(
    VanguardOperatorStore store,
    VanguardDeploymentLimitService deploymentLimitService,
    VanguardContractPoolService contractPoolService,
    VanguardOperatorBillingService billingService,
    VanguardOperatorProjectionService projectionService,
    VanguardCareerRaidLedgerVerificationService careerLedgerVerificationService,
    VanguardOperatorExperienceReconciliationService experienceReconciliationService,
    VanguardOperatorCareerProjectionService careerProjectionService,
    VanguardCanonicalRaidHistoryService canonicalRaidHistoryService,
    VanguardOperatorXpShadowProjectionService xpShadowProjectionService,
    VanguardOperatorCareerXpCommitService careerXpCommitService)
{
    public async Task<VanguardOperatorStateResponse> GetStateAsync(string profileId)
    {
        var requestedProfileId = profileId;
        var storageProfileId = await store.ResolveStorageProfileIdAsync(profileId);
        var limits = await deploymentLimitService.GetLimitsAsync(storageProfileId);
        await contractPoolService.EnsureContractPoolAsync(storageProfileId, limits.PlayerLevel);
        var state = await store.LoadStateAsync(storageProfileId);
        IReadOnlyList<VanguardOperatorProfile> reconciledOperators = await experienceReconciliationService.ReconcileLegacyBaselinesAsync(
            storageProfileId,
            state.Operators);
        if (!ReferenceEquals(reconciledOperators, state.Operators))
        {
            state = state with { Operators = reconciledOperators };
        }
        var billing = await billingService.GetBillingSnapshotAsync(storageProfileId);
        var verifiedCareerLedger = await careerLedgerVerificationService.ReadAsync(storageProfileId);
        VanguardOperatorCareerXpSyncResult xpSync = await careerXpCommitService.SynchronizeAsync(
            storageProfileId,
            state.Operators,
            verifiedCareerLedger);
        if (xpSync.Success && xpSync.Changed)
        {
            state = state with { Operators = xpSync.Operators };
        }
        var careerProjection = careerProjectionService.BuildFromVerifiedLedger(storageProfileId, state.Operators, verifiedCareerLedger);
        var canonicalRaidHistory = canonicalRaidHistoryService.Build(storageProfileId, state.Operators, verifiedCareerLedger, careerProjection);
        xpShadowProjectionService.Observe(storageProfileId, state.Operators, verifiedCareerLedger);
        var now = DateTimeOffset.UtcNow;

        return new VanguardOperatorStateResponse(
            requestedProfileId,
            storageProfileId,
            limits,
            state.Operators,
            state.ActiveService,
            state.Contracts,
            state.Contacts,
            state.Medical,
            projectionService.BuildServiceProjections(state.Operators, state.ActiveService, state.Medical),
            projectionService.BuildMedicalProjections(state.Operators, state.ActiveService, state.Medical),
            projectionService.BuildRaidProjections(state.Operators, state.ActiveService, state.Medical),
            careerProjection,
            canonicalRaidHistory,
            billing,
            new VanguardOperatorStateMetadata(
                state.Operators.Count,
                state.ActiveService.Count,
                state.Contracts.Count,
                state.Medical.Count,
                state.Contacts.Count,
                now,
                $"schema-{VanguardOperatorSchema.CurrentVersion}-reference-offraid-port",
                VanguardBuildVersion.BuildLabel));
    }

    public async Task<VanguardOperatorLimitsResponse> GetLimitsAsync(string profileId)
    {
        var storageProfileId = await store.ResolveStorageProfileIdAsync(profileId);
        return new VanguardOperatorLimitsResponse(profileId, storageProfileId, await deploymentLimitService.GetLimitsAsync(storageProfileId));
    }

    public VanguardOperatorStorageDiagnosticsResponse GetStorageDiagnostics()
    {
        var known = store.GetKnownProfileIds();
        return new VanguardOperatorStorageDiagnosticsResponse(
            store.RootDirectory,
            known,
            known.Count,
            VanguardBuildVersion.BuildLabel,
            DateTimeOffset.UtcNow);
    }
}
