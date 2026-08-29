using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Diagnostics;

// Responsibility: Provides the typed client facade over Vanguard server HTTP endpoints used by Off-Raid UI, raid setup, persistence support and runtime settings.
// Flow: Public methods build request DTOs, dispatch them through IRequestDispatcher, normalize/deserialize responses and expose explicit success/failure results to higher-level client services.
// Authority boundary: Transport only: the server remains authoritative for persistent Operator/economy state and client callers remain responsible for UI/runtime decisions.
// Invariant: Endpoint failures remain explicit, request/response contracts stay compatibility-conscious, and this facade performs no hidden gameplay mutation.
namespace Vanguard.Client.Api;

internal sealed class VanguardApiClient
{
    private readonly IRequestDispatcher requestDispatcher;

    public VanguardApiClient()
        : this(CreateDefaultDispatcher())
    {
    }

    public VanguardApiClient(IRequestDispatcher requestDispatcher)
    {
        this.requestDispatcher = requestDispatcher;
    }

    public VanguardOperatorStateView LoadState()
    {
        try
        {
            string json = requestDispatcher.GetJson(VanguardApiRoutes.State);
            var response = Deserialize<VanguardOperatorStateResponseDto>(json);
            if (response == null)
            {
                VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.OffRaidUiStatusTag, "State response deserialization returned null.");
                return VanguardOperatorStateView.Empty("empty_state_response");
            }

            var view = VanguardOperatorStateView.FromResponse(response);
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.OffRaidUiStatusTag,
                $"State received: requested={view.RequestedProfileId}; storage={view.StorageProfileId}; operators={view.Operators.Count}; activeService={view.ActiveService.Count}; contracts={view.Contracts.Count}; medical={view.MedicalRecords.Count}; contacts={view.Contacts.Count}; careerVerifiedEntries={view.CareerProjection.VerifiedEntryCount}; careerCoverage={view.CareerProjection.CoverageState ?? "<none>"}; raidHistoryVerifiedEntries={view.CanonicalRaidHistory.VerifiedEntryCount}; raidHistoryParity={view.CanonicalRaidHistory.CareerParity?.IsMatch ?? false}; maxHired={view.Limits.MaxHiredOperators}; maxDeployable={view.Limits.MaxDeployableOperators}; debt={view.Billing.OutstandingDebt}; build={view.Metadata.BuildLabel ?? "<none>"}");
            return view;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Error("VANGUARD_OFFRAID_UI_STATUS", exception);
            return VanguardOperatorStateView.Empty(exception.Message);
        }
    }

    public VanguardOperatorHireResponseDto HireContract(string? offerId, string? operatorId)
    {
        return Post<VanguardHireContractRequestDto, VanguardOperatorHireResponseDto>(
            VanguardApiRoutes.HireContract,
            new VanguardHireContractRequestDto { OfferId = offerId, OperatorId = operatorId })
            ?? new VanguardOperatorHireResponseDto { Success = false, Reason = "empty_hire_response" };
    }

    public VanguardOperatorRaidSelectionResponseDto SetRaidSelection(string? operatorId, bool selectedForRaid)
    {
        return Post<VanguardSetOperatorRaidSelectionRequestDto, VanguardOperatorRaidSelectionResponseDto>(
            VanguardApiRoutes.SetRaidSelection,
            new VanguardSetOperatorRaidSelectionRequestDto { OperatorId = operatorId, SelectedForRaid = selectedForRaid })
            ?? new VanguardOperatorRaidSelectionResponseDto { Success = false, Reason = "empty_raid_selection_response", OperatorId = operatorId };
    }

    public VanguardOperatorLootTargetPolicyResponseDto SetLootTargetPolicy(string? operatorId, string? lootTargetPolicy)
    {
        return Post<VanguardSetOperatorLootTargetPolicyRequestDto, VanguardOperatorLootTargetPolicyResponseDto>(
            VanguardApiRoutes.SetLootTargetPolicy,
            new VanguardSetOperatorLootTargetPolicyRequestDto { OperatorId = operatorId, LootTargetPolicy = lootTargetPolicy })
            ?? new VanguardOperatorLootTargetPolicyResponseDto { Success = false, Reason = "empty_loot_target_policy_response", OperatorId = operatorId, LootTargetPolicy = "CorpsesOnly" };
    }

    public VanguardOperatorMedicalTreatmentResponseDto TreatMedical(string? operatorId)
    {
        return Post<VanguardOperatorMedicalTreatmentRequestDto, VanguardOperatorMedicalTreatmentResponseDto>(
            VanguardApiRoutes.TreatMedical,
            new VanguardOperatorMedicalTreatmentRequestDto { OperatorId = operatorId, ConfirmTreatment = true })
            ?? new VanguardOperatorMedicalTreatmentResponseDto { Success = false, Reason = "empty_medical_treatment_response", OperatorId = operatorId };
    }

    public VanguardOperatorBillingActionResponseDto SignBilling(IEnumerable<string>? invoiceIds)
    {
        var ids = invoiceIds?.Where(id => !string.IsNullOrWhiteSpace(id)).ToList() ?? new List<string>();
        return Post<VanguardSignBillingRequestDto, VanguardOperatorBillingActionResponseDto>(
            VanguardApiRoutes.SignBilling,
            new VanguardSignBillingRequestDto { InvoiceIds = ids })
            ?? new VanguardOperatorBillingActionResponseDto { Success = false, Reason = "empty_billing_sign_response" };
    }

    public VanguardOperatorBillingActionResponseDto ReconcileBilling()
    {
        return Post<object, VanguardOperatorBillingActionResponseDto>(
            VanguardApiRoutes.ReconcileBilling,
            new { })
            ?? new VanguardOperatorBillingActionResponseDto { Success = false, Reason = "empty_billing_reconcile_response" };
    }



    public VanguardOperatorInventoryModeResponseDto EnterInventoryMode(string? operatorId)
    {
        var response = Post<VanguardOperatorInventoryModeRequestDto, VanguardOperatorInventoryModeResponseDto>(
            VanguardApiRoutes.EnterInventoryMode,
            new VanguardOperatorInventoryModeRequestDto { OperatorId = operatorId, Confirm = true });
        if (response != null)
        {
            return response;
        }

        VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_INVENTORY_PROFILE_MODE_STATUS", $"Inventory enter route returned an empty body; probing status fallback for operator={operatorId ?? "<none>"}.");
        var status = SafeInventoryModeStatus();
        if (status.Success && status.Active && (string.IsNullOrWhiteSpace(operatorId) || string.Equals(status.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase)))
        {
            status.Reason = string.IsNullOrWhiteSpace(status.Reason) ? "entered_status_fallback" : status.Reason;
            return status;
        }

        return new VanguardOperatorInventoryModeResponseDto { Success = false, Reason = "inventory_mode_enter_no_server_body", OperatorId = operatorId };
    }

    public VanguardOperatorInventoryModeResponseDto ExitInventoryMode(string? operatorId)
    {
        var response = Post<VanguardOperatorInventoryModeRequestDto, VanguardOperatorInventoryModeResponseDto>(
            VanguardApiRoutes.ExitInventoryMode,
            new VanguardOperatorInventoryModeRequestDto { OperatorId = operatorId, Confirm = true });
        if (response != null)
        {
            return response;
        }

        VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_INVENTORY_PROFILE_MODE_STATUS", $"Inventory exit route returned an empty body; probing status fallback for operator={operatorId ?? "<none>"}.");
        var status = SafeInventoryModeStatus();
        if (status.Success && !status.Active)
        {
            status.Reason = "exited_status_fallback";
            return status;
        }

        return new VanguardOperatorInventoryModeResponseDto { Success = false, Reason = "inventory_mode_exit_no_server_body", OperatorId = operatorId };
    }


    public VanguardOperatorInventoryModeResponseDto DirectCommitInventoryMode(string? operatorId, string? profileDescriptorJson, int clientItemCount, string? snapshotSource)
    {
        return Post<VanguardOperatorInventoryDirectCommitRequestDto, VanguardOperatorInventoryModeResponseDto>(
            VanguardApiRoutes.DirectCommitInventoryMode,
            new VanguardOperatorInventoryDirectCommitRequestDto
            {
                OperatorId = operatorId,
                Confirm = true,
                ProfileDescriptorJson = profileDescriptorJson,
                SnapshotSource = snapshotSource,
                ClientItemCount = clientItemCount
            })
            ?? new VanguardOperatorInventoryModeResponseDto { Success = false, Reason = "empty_inventory_mode_direct_commit_response", OperatorId = operatorId };
    }

    public VanguardOperatorInventoryModeResponseDto GetInventoryModeStatus()
    {
        return SafeInventoryModeStatus();
    }

    private VanguardOperatorInventoryModeResponseDto SafeInventoryModeStatus()
    {
        try
        {
            return Get<VanguardOperatorInventoryModeResponseDto>(VanguardApiRoutes.InventoryModeStatus)
                ?? new VanguardOperatorInventoryModeResponseDto { Success = false, Reason = "empty_inventory_mode_status_response" };
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_INVENTORY_PROFILE_MODE_STATUS", $"Inventory mode status fallback failed: {exception.GetType().Name}: {exception.Message}");
            return new VanguardOperatorInventoryModeResponseDto { Success = false, Reason = "inventory_mode_status_exception_" + exception.GetType().Name };
        }
    }

    public VanguardOperatorInventorySummaryResponseDto GetInventorySummary()
    {
        return Get<VanguardOperatorInventorySummaryResponseDto>(VanguardApiRoutes.InventorySummary)
            ?? new VanguardOperatorInventorySummaryResponseDto { Summaries = new List<VanguardOperatorInventorySummaryDto>() };
    }

    public string GetInventoryModeProfilesJson()
    {
        return requestDispatcher.GetJson(VanguardApiRoutes.InventoryModeProfiles);
    }

    public VanguardRaidOperatorManifestForProfilesResponseDto LoadRaidManifestForProfiles(IEnumerable<string> profileIds, string? raidSessionId)
    {
        var ids = profileIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToList();
        return Post<VanguardRaidManifestForProfilesRequestDto, VanguardRaidOperatorManifestForProfilesResponseDto>(
            VanguardApiRoutes.RaidManifestForProfiles,
            new VanguardRaidManifestForProfilesRequestDto { ProfileIds = ids, RaidSessionId = raidSessionId })
            ?? new VanguardRaidOperatorManifestForProfilesResponseDto { Success = false, Reason = "empty_raid_manifest_for_profiles_response" };
    }

    public VanguardRaidOperatorPersistenceBatchResponseDto CommitRaidOperatorPersistence(VanguardRaidOperatorPersistenceBatchRequestDto request)
    {
        return Post<VanguardRaidOperatorPersistenceBatchRequestDto, VanguardRaidOperatorPersistenceBatchResponseDto>(
            VanguardApiRoutes.RaidPersistenceCommit,
            request)
            ?? new VanguardRaidOperatorPersistenceBatchResponseDto
            {
                Success = false,
                Reason = "empty_raid_persistence_commit_response",
                RaidSessionId = request.RaidSessionId,
                RequestedOperatorCount = request.Operators?.Count ?? 0
            };
    }

    public VanguardOperatorDismissResponseDto DismissOperator(string? operatorId)
    {
        return Post<VanguardDismissOperatorRequestDto, VanguardOperatorDismissResponseDto>(
            VanguardApiRoutes.DismissOperator,
            new VanguardDismissOperatorRequestDto { OperatorId = operatorId })
            ?? new VanguardOperatorDismissResponseDto { Success = false, Reason = "empty_dismiss_response" };
    }




    public VanguardOperatorRuntimeAuditSettingsResponseDto GetRuntimeAuditSettings(
        string? ownerProfileId = null,
        bool requesterIsFikaInstalled = false,
        bool requesterIsActualHeadlessProcess = false,
        bool requesterIsHeadlessRequester = false,
        bool requesterIsHost = false,
        bool requesterRaidHostedByHeadless = false)
    {
        return Post<VanguardOperatorRuntimeAuditSettingsGetRequestDto, VanguardOperatorRuntimeAuditSettingsResponseDto>(
            VanguardApiRoutes.RuntimeAuditConfigGet,
            new VanguardOperatorRuntimeAuditSettingsGetRequestDto
            {
                OwnerProfileId = ownerProfileId,
                Source = "client_runtime_audit_poll",
                ClientBuild = VanguardBuildVersion.Value,
                ClientLabel = VanguardBuildVersion.BuildLabel,
                RequesterIsFikaInstalled = requesterIsFikaInstalled,
                RequesterIsActualHeadlessProcess = requesterIsActualHeadlessProcess,
                RequesterIsHeadlessRequester = requesterIsHeadlessRequester,
                RequesterIsHost = requesterIsHost,
                RequesterRaidHostedByHeadless = requesterRaidHostedByHeadless
            })
            ?? new VanguardOperatorRuntimeAuditSettingsResponseDto { Success = false, Reason = "empty_runtime_audit_config_get_response" };
    }

    public VanguardOperatorRuntimeAuditSettingsResponseDto SetRuntimeAuditSettings(VanguardOperatorRuntimeAuditSettingsRequestDto request)
    {
        request.ClientBuild ??= VanguardBuildVersion.Value;
        request.ClientLabel ??= VanguardBuildVersion.BuildLabel;
        request.Source ??= request.UpdatedBySource;

        return Post<VanguardOperatorRuntimeAuditSettingsRequestDto, VanguardOperatorRuntimeAuditSettingsResponseDto>(
            VanguardApiRoutes.RuntimeAuditConfigSet,
            request)
            ?? new VanguardOperatorRuntimeAuditSettingsResponseDto { Success = false, Reason = "empty_runtime_audit_config_set_response" };
    }

    public VanguardOwnerLootInterestResponseDto SetOwnerLootInterest(VanguardOwnerLootInterestSetRequestDto request)
    {
        request.ClientBuild ??= VanguardBuildVersion.Value;
        return Post<VanguardOwnerLootInterestSetRequestDto, VanguardOwnerLootInterestResponseDto>(
            VanguardApiRoutes.OwnerLootInterestSet, request)
            ?? new VanguardOwnerLootInterestResponseDto { Success = false, Reason = "empty_owner_loot_interest_set_response" };
    }

    public VanguardOwnerLootInterestResponseDto GetOwnerLootInterest(string ownerProfileId)
    {
        return Post<VanguardOwnerLootInterestGetRequestDto, VanguardOwnerLootInterestResponseDto>(
            VanguardApiRoutes.OwnerLootInterestGet,
            new VanguardOwnerLootInterestGetRequestDto
            {
                OwnerProfileId = ownerProfileId,
                Source = "runtime_owner_loot_interest_pull",
                ClientBuild = VanguardBuildVersion.Value
            })
            ?? new VanguardOwnerLootInterestResponseDto { Success = false, Reason = "empty_owner_loot_interest_get_response", OwnerProfileId = ownerProfileId };
    }

    public VanguardTacticalAuthoringLiveExchangeResponseDto ExchangeTacticalAuthoringLive(VanguardTacticalAuthoringLiveExchangeRequestDto request)
    {
        request.ClientBuild = string.IsNullOrWhiteSpace(request.ClientBuild) ? VanguardBuildVersion.Value : request.ClientBuild;
        request.ClientLabel = string.IsNullOrWhiteSpace(request.ClientLabel) ? VanguardBuildVersion.BuildLabel : request.ClientLabel;
        return Post<VanguardTacticalAuthoringLiveExchangeRequestDto, VanguardTacticalAuthoringLiveExchangeResponseDto>(
            VanguardApiRoutes.TacticalAuthoringLiveExchange,
            request)
            ?? new VanguardTacticalAuthoringLiveExchangeResponseDto { Success = false, Reason = "empty_tactical_authoring_live_exchange_response" };
    }

    private TResponse? Get<TResponse>(string route)
        where TResponse : class
    {
        string json = requestDispatcher.GetJson(route);
        return Deserialize<TResponse>(json);
    }

    private TResponse? Post<TRequest, TResponse>(string route, TRequest request)
        where TResponse : class
    {
        string body = JsonConvert.SerializeObject(request);
        string json = requestDispatcher.PostJson(route, body);
        return Deserialize<TResponse>(json);
    }

    private static T? Deserialize<T>(string json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            JToken rootToken = UnwrapStringToken(JToken.Parse(json));
            JToken payloadToken = SelectPayloadToken(rootToken);
            return payloadToken.ToObject<T>();
        }
        catch
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch
            {
                return default;
            }
        }
    }

    private static JToken SelectPayloadToken(JToken token)
    {
        if (token is not JObject obj)
        {
            return token;
        }

        foreach (string propertyName in new[] { "data", "Data", "response", "Response", "result", "Result" })
        {
            if (!obj.TryGetValue(propertyName, StringComparison.OrdinalIgnoreCase, out JToken? payload) || payload == null || payload.Type == JTokenType.Null)
            {
                continue;
            }

            return UnwrapStringToken(payload);
        }

        return token;
    }

    private static JToken UnwrapStringToken(JToken token)
    {
        if (token.Type != JTokenType.String)
        {
            return token;
        }

        string? value = token.Value<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return token;
        }

        string trimmed = value.Trim();
        if ((!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
            && (!trimmed.StartsWith("[", StringComparison.Ordinal) || !trimmed.EndsWith("]", StringComparison.Ordinal)))
        {
            return token;
        }

        try
        {
            return JToken.Parse(trimmed);
        }
        catch
        {
            return token;
        }
    }

    private static IRequestDispatcher CreateDefaultDispatcher()
    {
#if SPT_CLIENT
        return new RequestHandlerDispatcher();
#else
        return new NoopRequestDispatcher();
#endif
    }
}

internal sealed class VanguardOperatorStateView
{
    public string RequestedProfileId { get; private init; } = string.Empty;
    public string StorageProfileId { get; private init; } = string.Empty;
    public VanguardOperatorDeploymentLimitsDto Limits { get; private init; } = VanguardOperatorDeploymentLimitsDto.Empty;
    public List<VanguardOperatorProfileDto> Operators { get; private init; } = new();
    public List<VanguardActiveServiceRecordDto> ActiveService { get; private init; } = new();
    public List<VanguardOperatorContractOfferDto> Contracts { get; private init; } = new();
    public List<VanguardOperatorContactRecordDto> Contacts { get; private init; } = new();
    public List<VanguardOperatorMedicalRecordDto> MedicalRecords { get; private init; } = new();
    public List<VanguardOperatorServiceProjectionDto> ServiceProjections { get; private init; } = new();
    public List<VanguardOperatorMedicalProjectionDto> MedicalProjections { get; private init; } = new();
    public List<VanguardOperatorRaidProjectionDto> RaidProjections { get; private init; } = new();
    public VanguardCareerProjectionReadModelDto CareerProjection { get; private init; } = VanguardCareerProjectionReadModelDto.Empty;
    public VanguardCanonicalRaidHistoryReadModelDto CanonicalRaidHistory { get; private init; } = VanguardCanonicalRaidHistoryReadModelDto.Empty;
    public VanguardOperatorBillingSnapshotDto Billing { get; private init; } = VanguardOperatorBillingSnapshotDto.Empty;
    public VanguardOperatorStateMetadataDto Metadata { get; private init; } = VanguardOperatorStateMetadataDto.Empty;
    public string? Error { get; private init; }

    public static VanguardOperatorStateView Empty(string? error = null)
    {
        return new VanguardOperatorStateView { Error = error };
    }

    public static VanguardOperatorStateView FromResponse(VanguardOperatorStateResponseDto response)
    {
        return new VanguardOperatorStateView
        {
            RequestedProfileId = response.RequestedProfileId ?? string.Empty,
            StorageProfileId = response.StorageProfileId ?? string.Empty,
            Limits = response.Limits ?? VanguardOperatorDeploymentLimitsDto.Empty,
            Operators = response.Operators ?? new List<VanguardOperatorProfileDto>(),
            ActiveService = response.ActiveService ?? new List<VanguardActiveServiceRecordDto>(),
            Contracts = response.Contracts ?? new List<VanguardOperatorContractOfferDto>(),
            Contacts = response.Contacts ?? new List<VanguardOperatorContactRecordDto>(),
            MedicalRecords = response.MedicalRecords ?? new List<VanguardOperatorMedicalRecordDto>(),
            ServiceProjections = response.ServiceProjections ?? new List<VanguardOperatorServiceProjectionDto>(),
            MedicalProjections = response.MedicalProjections ?? new List<VanguardOperatorMedicalProjectionDto>(),
            RaidProjections = response.RaidProjections ?? new List<VanguardOperatorRaidProjectionDto>(),
            CareerProjection = response.CareerProjection ?? VanguardCareerProjectionReadModelDto.Empty,
            CanonicalRaidHistory = response.CanonicalRaidHistory ?? VanguardCanonicalRaidHistoryReadModelDto.Empty,
            Billing = response.Billing ?? VanguardOperatorBillingSnapshotDto.Empty,
            Metadata = response.Metadata ?? VanguardOperatorStateMetadataDto.Empty,
            Error = null
        };
    }
}
