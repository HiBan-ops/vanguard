using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;
using Vanguard.Server.Operators.Requests;
using Vanguard.Server.Operators.Inventory.Requests;
using Vanguard.Server.Operators.Inventory.Services;
using Vanguard.Server.Operators.Inventory.Responses;
using Vanguard.Server.Operators.Services;
using Vanguard.Server.Operators.Raid.Requests;
using Vanguard.Server.Operators.Raid.Responses;
using Vanguard.Server.Operators.Raid.Services;
using Vanguard.Server.Operators.Raid.Persistence.Models;
using Vanguard.Server.Operators.Raid.Persistence.Services;
using Vanguard.Server.Operators.Audit.Requests;
using Vanguard.Server.Operators.Audit.Services;
using Vanguard.Server.Operators.TacticalAuthoring.Requests;
using Vanguard.Server.Operators.TacticalAuthoring.Services;
using Vanguard.Server.Operators.LootInterests.Requests;
using Vanguard.Server.Operators.LootInterests.Services;
using Vanguard.Server.Diagnostics;

// Responsibility: Implements the concrete SPT route callbacks behind the Vanguard Operator HTTP API.
// Flow: Each callback resolves request/profile identity, deserializes its payload, delegates business work to the appropriate Operator service and serializes a consistent SPT response/error shape.
// Authority boundary: Callbacks own HTTP adaptation only; validation/business rules and persistent state remain in domain services/store.
// Invariant: No route silently bypasses confirmation/profile ownership, callback failures remain explicit, and transport code does not duplicate persistence logic.
namespace Vanguard.Server.Operators.Routes;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorRouteCallbacks(
    HttpResponseUtil httpResponseUtil,
    VanguardOperatorStateService stateService,
    VanguardOperatorRecruitmentService recruitmentService,
    VanguardOperatorActiveServiceService activeServiceService,
    VanguardOperatorMedicalRecoveryService medicalRecoveryService,
    VanguardOperatorBillingService billingService,
    VanguardOperatorInventoryModeService inventoryModeService,
    VanguardRaidOperatorManifestService raidManifestService,
    VanguardRaidBotGenerationService raidBotGenerationService,
    VanguardRaidOperatorPersistenceService raidOperatorPersistenceService,
    VanguardOperatorRuntimeAuditSettingsService runtimeAuditSettingsService,
    VanguardTacticalAuthoringLiveRelayService tacticalAuthoringLiveRelayService,
    VanguardOwnerLootInterestService ownerLootInterestService,
    VanguardOperatorLootTargetPolicyService lootTargetPolicyService,
    ISptLogger<VanguardOperatorRouteCallbacks> logger)
{
    public async ValueTask<string> GetStateAsync(string url, EmptyRequestData request, MongoId sessionId)
    {
        var response = await stateService.GetStateAsync(sessionId.ToString());
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PERSISTENCE_STATUS] state requested={response.RequestedProfileId}, storage={response.StorageProfileId}, operators={response.Metadata.OperatorCount}, activeService={response.Metadata.ActiveServiceCount}, contracts={response.Metadata.ContractOfferCount}, medical={response.Metadata.MedicalRecordCount}, debt={response.Billing.OutstandingDebt}"));
        return httpResponseUtil.GetBody(response);
    }

    public async ValueTask<string> GetLimitsAsync(string url, EmptyRequestData request, MongoId sessionId)
    {
        var response = await stateService.GetLimitsAsync(sessionId.ToString());
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PERSISTENCE_STATUS] limits requested={response.RequestedProfileId}, storage={response.StorageProfileId}, tier={response.Limits.Tier}, maxHired={response.Limits.MaxHiredOperators}, maxDeployable={response.Limits.MaxDeployableOperators}"));
        return httpResponseUtil.GetBody(response);
    }

    public async ValueTask<string> GetRaidManifestAsync(string url, EmptyRequestData request, MongoId sessionId)
    {
        var response = await raidManifestService.LoadManifestForOwnerAsync(sessionId.ToString());
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_RAID_SPAWN_STATUS] route=manifest requested={response.RequestedProfileId}, storage={response.StorageProfileId}, operators={response.OperatorCount}, selected={response.SelectedForRaidCount}, skipped={response.SkippedCount}"));
        return httpResponseUtil.GetBody(response);
    }

    public async ValueTask<string> GetRaidManifestForProfilesAsync(string url, VanguardRaidManifestForProfilesRequest request, MongoId sessionId)
    {
        var manifests = await raidManifestService.LoadManifestForOwnersAsync(request.ProfileIds, request.RaidSessionId);
        var firstRaidSessionId = manifests.Values.FirstOrDefault()?.RaidSessionId ?? request.RaidSessionId ?? string.Empty;
        var response = new VanguardRaidOperatorManifestForProfilesResponse(
            sessionId.ToString(),
            firstRaidSessionId,
            manifests,
            manifests.Count,
            manifests.Values.Sum(manifest => manifest.OperatorCount),
            true,
            "vanguard_raid_manifest_for_profiles_loaded",
            DateTimeOffset.UtcNow,
            VanguardBuildVersion.BuildLabel);
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_RAID_SPAWN_STATUS] route=manifest-for-profiles owners={response.OwnerCount}, operators={response.OperatorCount}, requestedBySession={sessionId}"));
        return httpResponseUtil.GetBody(response);
    }

    public async ValueTask<string> GenerateVanguardOperatorBotAsync(string url, VanguardGenerateOperatorBotRequest request, MongoId sessionId)
    {
        var response = await raidBotGenerationService.GenerateOperatorBotsAsync(sessionId, request);
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PROFILE_GENERATED] route=vanguardoperatorgenerate requestedBySession={sessionId}, owner={request.OwnerProfileId ?? "<none>"}, operator={request.OperatorId ?? "<none>"}, bots={response.Count}"));
        return httpResponseUtil.GetBody(response);
    }

    public ValueTask<string> GetRuntimeAuditConfigAsync(string url, VanguardOperatorRuntimeAuditSettingsGetRequest? request, MongoId sessionId)
    {
        var safeRequest = request ?? new VanguardOperatorRuntimeAuditSettingsGetRequest();
        var response = runtimeAuditSettingsService.Get(sessionId.ToString(), safeRequest);
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_F12_AUTHORITY_CONVERGENCE_STATUS] route=config/get requestedBySession={sessionId}; owner={safeRequest.OwnerProfileId ?? "<fallback>"}; raidAuthority={response.Settings?.RaidAuthorityProfileId ?? "<none>"}; playerSource={response.Settings?.PlayerScopedSource ?? "<none>"}; raidSource={response.Settings?.RaidScopedSource ?? "<none>"}; governance={response.Settings?.GovernanceVersion ?? "<none>"}; lootRadius={response.Settings?.MovementOpportunisticLootMaxDistanceMeters}; medLease={response.Settings?.FirstActiveMobileMedicalLeaseEnabled}; postRaidPersistence={response.Settings?.OperatorPostRaidPersistenceEnabled}; requesterActualHeadless={safeRequest.RequesterIsActualHeadlessProcess}; requesterHeadlessRequester={safeRequest.RequesterIsHeadlessRequester}; requesterHost={safeRequest.RequesterIsHost}; headlessRaid={safeRequest.RequesterRaidHostedByHeadless}; reason={response.Reason ?? "<none>"}; latestClientWins=false"));
        return ValueTask.FromResult(httpResponseUtil.GetBody(response));
    }

    public ValueTask<string> ExchangeTacticalAuthoringLiveAsync(string url, VanguardTacticalAuthoringLiveExchangeRequest? request, MongoId sessionId)
    {
        var response = tacticalAuthoringLiveRelayService.Exchange(request);
        // This route is a sub-second local heartbeat while preview is active (and a lightweight
        // headless poll while idle). Never emit one info line per exchange: that would turn an
        // editor aid into persistent server-log noise. Only malformed/unsupported calls surface.
        if (!response.Success)
        {
            logger.Warning(VanguardServerDiagnosticsLog.Present($"[VANGUARD_TACTICAL_AUTHORING_STATUS] route=live/exchange rejected requestedBySession={sessionId}; role={request?.Role ?? "<none>"}; reason={response.Reason}"));
        }
        return ValueTask.FromResult(httpResponseUtil.GetBody(response));
    }

    public ValueTask<string> SetOwnerLootInterestAsync(string url, VanguardOwnerLootInterestSetRequest? request, MongoId sessionId)
    {
        var response = ownerLootInterestService.Set(sessionId.ToString(), request ?? new VanguardOwnerLootInterestSetRequest());
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_UNIFIED_OPPORTUNISTIC_LOOT_READ_MODEL_STATUS] route=loot-interest/set owner={response.OwnerProfileId}; revision={response.Revision}; entries={response.Entries.Count}; source={response.Source}; reason={response.Reason}"));
        return ValueTask.FromResult(httpResponseUtil.GetBody(response));
    }

    public ValueTask<string> GetOwnerLootInterestAsync(string url, VanguardOwnerLootInterestGetRequest? request, MongoId sessionId)
    {
        var response = ownerLootInterestService.Get(sessionId.ToString(), request ?? new VanguardOwnerLootInterestGetRequest());
        return ValueTask.FromResult(httpResponseUtil.GetBody(response));
    }

    public ValueTask<string> SetRuntimeAuditConfigAsync(string url, VanguardOperatorRuntimeAuditSettingsRequest? request, MongoId sessionId)
    {
        var safeRequest = request ?? new VanguardOperatorRuntimeAuditSettingsRequest();
        var response = runtimeAuditSettingsService.Set(safeRequest, sessionId.ToString());
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_F12_AUTHORITY_CONVERGENCE_STATUS] route=config/set requestedBySession={sessionId}; owner={safeRequest.OwnerProfileId ?? safeRequest.UpdatedByProfileId ?? "<fallback>"}; raidAuthority={response.Settings?.RaidAuthorityProfileId ?? "<none>"}; playerSource={response.Settings?.PlayerScopedSource ?? "<none>"}; raidSource={response.Settings?.RaidScopedSource ?? "<none>"}; governance={response.Settings?.GovernanceVersion ?? "<none>"}; lootRadius={response.Settings?.MovementOpportunisticLootMaxDistanceMeters}; medLease={response.Settings?.FirstActiveMobileMedicalLeaseEnabled}; postRaidPersistence={response.Settings?.OperatorPostRaidPersistenceEnabled}; fika={safeRequest.RequesterIsFikaInstalled}; actualHeadless={safeRequest.RequesterIsActualHeadlessProcess}; headlessRequester={safeRequest.RequesterIsHeadlessRequester}; host={safeRequest.RequesterIsHost}; headlessRaid={safeRequest.RequesterRaidHostedByHeadless}; reason={response.Reason ?? "<none>"}; latestClientWins=false"));
        return ValueTask.FromResult(httpResponseUtil.GetBody(response));
    }


    public async ValueTask<string> HireContractAsync(string url, VanguardHireContractRequest request, MongoId sessionId)
    {
        var response = await recruitmentService.HireAsync(sessionId.ToString(), request.OfferId, request.OperatorId);
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PERSISTENCE_STATUS] hire requested={response.RequestedProfileId}, storage={response.StorageProfileId}, success={response.Success}, reason={response.Reason}, operator={response.Operator?.OperatorId ?? request.OperatorId ?? "<none>"}, activeService={response.ActiveServiceCount}, contracts={response.RemainingContractCount}, invoice={response.BillingInvoice?.InvoiceId ?? "<none>"}"));
        return httpResponseUtil.GetBody(response);
    }

    public async ValueTask<string> DismissOperatorAsync(string url, VanguardDismissOperatorRequest request, MongoId sessionId)
    {
        var response = await recruitmentService.DismissAsync(sessionId.ToString(), request.OperatorId);
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PERSISTENCE_STATUS] active service dismiss requested={response.RequestedProfileId}, storage={response.StorageProfileId}, success={response.Success}, reason={response.Reason}, operator={request.OperatorId ?? "<none>"}, activeService={response.ActiveServiceCount}"));
        return httpResponseUtil.GetBody(response);
    }

    public async ValueTask<string> SetOperatorRaidSelectionAsync(string url, VanguardSetOperatorRaidSelectionRequest request, MongoId sessionId)
    {
        var response = await activeServiceService.SetRaidSelectionAsync(sessionId.ToString(), request.OperatorId, request.SelectedForRaid);
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PERSISTENCE_STATUS] raid selection requested={response.RequestedProfileId}, storage={response.StorageProfileId}, success={response.Success}, reason={response.Reason}, operator={response.OperatorId ?? "<none>"}, selected={response.IsSelectedForRaid}, selectedCount={response.SelectedForRaidCount}"));
        return httpResponseUtil.GetBody(response);
    }

    public async ValueTask<string> SetOperatorLootTargetPolicyAsync(string url, VanguardSetOperatorLootTargetPolicyRequest request, MongoId sessionId)
    {
        var response = await lootTargetPolicyService.SetAsync(sessionId.ToString(), request.OperatorId, request.LootTargetPolicy);
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_CONTAINER_CLAIM_APPROACH_OPEN_PROOF_STATUS] route=loot-policy/set requested={response.RequestedProfileId}; storage={response.StorageProfileId}; success={response.Success}; reason={response.Reason}; operator={response.OperatorId ?? "<none>"}; policy={response.LootTargetPolicy}; persistentAuthority=true; f12MayWiden=false"));
        return httpResponseUtil.GetBody(response);
    }

    public async ValueTask<string> TreatOperatorMedicalAsync(string url, VanguardOperatorMedicalTreatmentRequest request, MongoId sessionId)
    {
        var response = await medicalRecoveryService.TreatOperatorAsync(sessionId.ToString(), request.OperatorId, request.ConfirmTreatment);
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PERSISTENCE_STATUS] medical treatment requested={response.RequestedProfileId}, storage={response.StorageProfileId}, success={response.Success}, reason={response.Reason}, operator={response.OperatorId ?? "<none>"}, amount={response.Amount}, invoice={response.BillingInvoice?.InvoiceId ?? "<none>"}"));
        return httpResponseUtil.GetBody(response);
    }

    public async ValueTask<string> SignBillingAsync(string url, VanguardSignBillingRequest request, MongoId sessionId)
    {
        var response = await billingService.SignOutstandingInvoicesAsync(sessionId.ToString(), request.InvoiceIds);
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PERSISTENCE_STATUS] billing sign requested={response.RequestedProfileId}, storage={response.StorageProfileId}, success={response.Success}, reason={response.Reason}, invoices={response.InvoiceCount}, amount={response.Amount}, debt={response.Billing.OutstandingDebt}"));
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OFFRAID_BILLING_FLOW_STATUS] sign requested={response.RequestedProfileId}, storage={response.StorageProfileId}, invoices={response.InvoiceCount}, signedPending={response.Billing.SignedPendingSettlementDebt}, flow=sign_then_explicit_eft_settlement"));
        return httpResponseUtil.GetBody(response);
    }

    public async ValueTask<string> ReconcileBillingAsync(string url, EmptyRequestData request, MongoId sessionId)
    {
        var response = await billingService.ReconcileSignedInvoicesAsync(sessionId.ToString());
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PERSISTENCE_STATUS] billing reconcile requested={response.RequestedProfileId}, storage={response.StorageProfileId}, success={response.Success}, reason={response.Reason}, settlementAttempted={response.SettlementAttempted}, invoices={response.InvoiceCount}, amount={response.Amount}, debt={response.Billing.OutstandingDebt}"));
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OFFRAID_BILLING_FLOW_STATUS] eft settlement requested={response.RequestedProfileId}, storage={response.StorageProfileId}, invoices={response.InvoiceCount}, amount={response.Amount}, settlementSucceeded={response.SettlementSucceeded}"));
        return httpResponseUtil.GetBody(response);
    }



    public async ValueTask<string> CommitRaidOperatorPersistenceAsync(string url, VanguardRaidOperatorPersistenceBatchRequest request, MongoId sessionId)
    {
        var response = await raidOperatorPersistenceService.CommitAsync(sessionId, request);
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_RAID_PERSISTENCE_STATUS] route=commit requested={sessionId}; raid={response.RaidSessionId}; success={response.Success}; reason={response.Reason}; requestedOperators={response.RequestedOperatorCount}; committedOperators={response.CommittedOperatorCount}; replay={response.IdempotentReplay}; rollback={response.RolledBack}"));
        return httpResponseUtil.GetBody(response);
    }

    public async ValueTask<string> EnterInventoryModeAsync(string url, VanguardOperatorInventoryModeRequest request, MongoId sessionId)
    {
        try
        {
            var response = await inventoryModeService.EnterAsync(sessionId, request.OperatorId, request.Confirm);
            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_INVENTORY_PROFILE_MODE_STATUS] route=enter requested={response.RequestedProfileId}, storage={response.StorageProfileId}, success={response.Success}, reason={response.Reason}, operator={response.OperatorId ?? "<none>"}, active={response.Active}, inventoryProfile={response.OperatorInventoryProfileId ?? "<none>"}"));
            return InventoryBody(response);
        }
        catch (Exception exception)
        {
            logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_INVENTORY_PROFILE_MODE_STATUS] route=enter exception requested={sessionId}, operator={request.OperatorId ?? "<none>"}, type={exception.GetType().Name}, message={exception.Message}"));
            return InventoryBody(BuildInventoryRouteFailure(sessionId, request.OperatorId, "enter_exception_" + exception.GetType().Name));
        }
    }

    public async ValueTask<string> ExitInventoryModeAsync(string url, VanguardOperatorInventoryModeRequest request, MongoId sessionId)
    {
        try
        {
            var response = await inventoryModeService.ExitAsync(sessionId, request.OperatorId);
            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_INVENTORY_PROFILE_MODE_STATUS] route=exit requested={response.RequestedProfileId}, storage={response.StorageProfileId}, success={response.Success}, reason={response.Reason}, operator={response.OperatorId ?? "<none>"}, active={response.Active}, inventoryProfile={response.OperatorInventoryProfileId ?? "<none>"}"));
            return InventoryBody(response);
        }
        catch (Exception exception)
        {
            logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_INVENTORY_PROFILE_MODE_STATUS] route=exit exception requested={sessionId}, operator={request.OperatorId ?? "<none>"}, type={exception.GetType().Name}, message={exception.Message}"));
            return InventoryBody(BuildInventoryRouteFailure(sessionId, request.OperatorId, "exit_exception_" + exception.GetType().Name));
        }
    }


    public async ValueTask<string> DirectCommitInventoryModeAsync(string url, VanguardOperatorInventoryDirectCommitRequest request, MongoId sessionId)
    {
        try
        {
            var response = await inventoryModeService.DirectCommitAsync(sessionId, request.OperatorId, request.Confirm, request.ProfileDescriptorJson, request.SnapshotSource, request.ClientItemCount);
            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_DIRECT_COMMIT_STATUS] route=direct-commit requested={response.RequestedProfileId}, storage={response.StorageProfileId}, success={response.Success}, reason={response.Reason}, operator={response.OperatorId ?? "<none>"}, active={response.Active}, inventoryProfile={response.OperatorInventoryProfileId ?? "<none>"}, clientItems={request.ClientItemCount}, source={request.SnapshotSource ?? "<none>"}"));
            return InventoryBody(response);
        }
        catch (Exception exception)
        {
            logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_DIRECT_COMMIT_STATUS] route=direct-commit exception requested={sessionId}, operator={request.OperatorId ?? "<none>"}, type={exception.GetType().Name}, message={exception.Message}"));
            return InventoryBody(BuildInventoryRouteFailure(sessionId, request.OperatorId, "direct_commit_exception_" + exception.GetType().Name));
        }
    }

    public async ValueTask<string> GetInventoryModeStatusAsync(string url, EmptyRequestData request, MongoId sessionId)
    {
        try
        {
            var response = await inventoryModeService.GetStatusAsync(sessionId);
            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_INVENTORY_PROFILE_MODE_STATUS] route=status requested={response.RequestedProfileId}, storage={response.StorageProfileId}, active={response.Active}, operator={response.OperatorId ?? "<none>"}"));
            return InventoryBody(response);
        }
        catch (Exception exception)
        {
            logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_INVENTORY_PROFILE_MODE_STATUS] route=status exception requested={sessionId}, type={exception.GetType().Name}, message={exception.Message}"));
            return InventoryBody(BuildInventoryRouteFailure(sessionId, null, "status_exception_" + exception.GetType().Name));
        }
    }

    public ValueTask<string> GetInventoryModeProfilesAsync(string url, EmptyRequestData request, MongoId sessionId)
    {
        try
        {
            string response = inventoryModeService.GetProfileDescriptorsJsonForClient(sessionId);
            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_INVENTORY_PROFILE_REDIRECT_STATUS] route=profiles requested={sessionId}, rawJson=true, bytes={response.Length}"));
            return ValueTask.FromResult(string.IsNullOrWhiteSpace(response) ? "[]" : response);
        }
        catch (Exception exception)
        {
            logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_INVENTORY_PROFILE_REDIRECT_STATUS] route=profiles exception requested={sessionId}, type={exception.GetType().Name}, message={exception.Message}"));
            return ValueTask.FromResult("[]");
        }
    }

    public async ValueTask<string> GetInventorySummaryAsync(string url, EmptyRequestData request, MongoId sessionId)
    {
        try
        {
            var response = await inventoryModeService.GetSummaryAsync(sessionId);
            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_INVENTORY_SUMMARY_STATUS] route=summary requested={response.RequestedProfileId}, storage={response.StorageProfileId}, operators={response.Summaries.Count}"));
            return InventoryBody(response);
        }
        catch (Exception exception)
        {
            logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_INVENTORY_SUMMARY_STATUS] route=summary exception requested={sessionId}, type={exception.GetType().Name}, message={exception.Message}"));
            return InventoryBody(new VanguardOperatorInventorySummaryResponse { RequestedProfileId = sessionId.ToString(), StorageProfileId = sessionId.ToString(), Summaries = [] });
        }
    }

    private string InventoryBody(object response)
    {
        string body = httpResponseUtil.GetBody(response);
        if (string.IsNullOrWhiteSpace(body))
        {
            body = httpResponseUtil.NoBody(response);
        }

        return body;
    }

    private static VanguardOperatorInventoryModeResponse BuildInventoryRouteFailure(MongoId sessionId, string? operatorId, string reason)
    {
        return new VanguardOperatorInventoryModeResponse
        {
            Success = false,
            Reason = reason,
            Active = false,
            RequestedProfileId = sessionId.ToString(),
            StorageProfileId = sessionId.ToString(),
            OperatorId = operatorId,
            GeneratedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public ValueTask<string> GetStorageDiagnosticsAsync(string url, EmptyRequestData request, MongoId sessionId)
    {
        var response = stateService.GetStorageDiagnostics();
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PERSISTENCE_STATUS] storage diagnostics root={response.RootDirectory}, profiles={response.KnownProfileCount}"));
        return ValueTask.FromResult(httpResponseUtil.GetBody(response));
    }
}
