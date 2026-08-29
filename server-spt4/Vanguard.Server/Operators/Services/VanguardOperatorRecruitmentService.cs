using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using Vanguard.Server.Operators.Inventory.Services;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Responses;
using Vanguard.Server.Operators.Storage;

// Responsibility: Transitions a selected contract offer into a persistent hired Operator and active-service state.
// Flow: It validates offer ownership/availability and billing prerequisites, materializes the Operator profile/persona/progression records, updates active service and contract/contact state, and returns a canonical recruitment result.
// Authority boundary: Server Operator storage and billing services are authoritative; recruitment does not directly control the spawned in-raid bot.
// Invariant: A hire must be profile-scoped and transactionally coherent so an Operator cannot exist half-recruited across contracts, billing and active service.
namespace Vanguard.Server.Operators.Services;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorRecruitmentService(
    VanguardOperatorStore store,
    VanguardDeploymentLimitService deploymentLimitService,
    VanguardContractPoolService contractPoolService,
    VanguardOperatorBillingService billingService,
    VanguardOperatorInventoryModeService inventoryModeService)
{
    public async Task<VanguardOperatorHireResponse> HireAsync(string profileId, string? offerId, string? operatorId)
    {
        var requestedProfileId = profileId;
        var storageProfileId = await store.ResolveStorageProfileIdAsync(profileId);
        var limits = await deploymentLimitService.GetLimitsAsync(storageProfileId);
        await contractPoolService.EnsureContractPoolAsync(storageProfileId, limits.PlayerLevel);
        var state = await store.LoadStateAsync(storageProfileId);
        var now = DateTimeOffset.UtcNow;

        if (state.ActiveService.Count >= limits.MaxHiredOperators)
        {
            return await BuildHireFailureAsync(requestedProfileId, storageProfileId, "active_service_limit_reached", limits, state.Contracts.Count, state.ActiveService.Count);
        }

        var offer = state.Contracts.FirstOrDefault(candidate =>
            (!string.IsNullOrWhiteSpace(offerId) && string.Equals(candidate.OfferId, offerId, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(operatorId) && string.Equals(candidate.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase)))
            ?? state.Contracts.FirstOrDefault(candidate => candidate.AvailableUntilUtc > now)
            ?? state.Contracts.FirstOrDefault();

        if (offer is null)
        {
            return await BuildHireFailureAsync(requestedProfileId, storageProfileId, "no_contract_offer_available", limits, state.Contracts.Count, state.ActiveService.Count);
        }

        if (offer.AvailableUntilUtc <= now)
        {
            return await BuildHireFailureAsync(requestedProfileId, storageProfileId, "contract_offer_expired", limits, state.Contracts.Count, state.ActiveService.Count);
        }

        if (!offer.CanHire)
        {
            return await BuildHireFailureAsync(requestedProfileId, storageProfileId, "contract_offer_not_hireable", limits, state.Contracts.Count, state.ActiveService.Count);
        }

        var existingProfile = state.Operators.FirstOrDefault(existing => string.Equals(existing.OperatorId, offer.OperatorId, StringComparison.OrdinalIgnoreCase));
        if (existingProfile is not null && state.ActiveService.Any(active => string.Equals(active.OperatorId, existingProfile.OperatorId, StringComparison.OrdinalIgnoreCase)))
        {
            return await BuildHireFailureAsync(requestedProfileId, storageProfileId, "operator_already_in_active_service", limits, state.Contracts.Count, state.ActiveService.Count);
        }

        var profile = existingProfile is null
            ? CreateProfileFromOffer(offer, now)
            : existingProfile with
            {
                ContractStatus = VanguardOperatorContractStatuses.Contracted,
                ServiceStatus = VanguardOperatorServiceStatuses.ActiveService,
                SalaryPerRaid = offer.SalaryPerRaid,
                UpdatedAtUtc = now,
            };
        var isSelectedForRaid = state.ActiveService.Count(record => record.IsSelectedForRaid) < limits.MaxDeployableOperators;
        var activeService = new VanguardActiveServiceRecord(
            profile.OperatorId,
            profile.Identity.DisplayName,
            profile.Identity.Side,
            profile.Role,
            profile.Specialty,
            VanguardOperatorServiceStatuses.ActiveService,
            isSelectedForRaid,
            false,
            now,
            profile.SalaryPerRaid,
            null,
            null,
            VanguardOperatorSchema.CurrentVersion);

        var medical = new VanguardOperatorMedicalRecord(
            profile.OperatorId,
            profile.Identity.DisplayName,
            VanguardOperatorServiceStatuses.Available,
            1.0,
            null,
            0,
            0,
            false,
            "No persistent injury recorded.",
            now,
            VanguardOperatorSchema.CurrentVersion);

        var contact = BuildActiveContact(profile, now, offer.RelationshipSummary);
        var remainingContracts = state.Contracts.Where(candidate => !string.Equals(candidate.OfferId, offer.OfferId, StringComparison.OrdinalIgnoreCase)).ToArray();

        try
        {
            await inventoryModeService.EnsurePersistentOperatorProfileAsync(new MongoId(requestedProfileId), storageProfileId, profile);
        }
        catch (Exception exception)
        {
            return await BuildHireFailureAsync(requestedProfileId, storageProfileId, "operator_profile_foundation_failed_" + exception.GetType().Name, limits, state.Contracts.Count, state.ActiveService.Count);
        }

        await store.SaveOperatorsAsync(storageProfileId, UpsertOperator(state.Operators, profile));
        await store.SaveActiveServiceAsync(storageProfileId, state.ActiveService.Concat(new[] { activeService }).ToArray());
        await store.SaveMedicalAsync(storageProfileId, UpsertMedical(state.Medical, medical));
        await store.SaveContactsAsync(storageProfileId, UpsertContact(state.Contacts, contact));
        await store.SaveContractsAsync(storageProfileId, remainingContracts);

        var invoice = await billingService.CreateInvoiceAsync(
            storageProfileId,
            VanguardOperatorBillingTypes.ContractSignature,
            profile.OperatorId,
            profile.Identity.DisplayName,
            offer.OfferId,
            profile.HirePrice,
            profile.CurrencyTpl,
            $"Contract signature fee for {profile.Identity.DisplayName} recorded as Vanguard deferred billing.");
        var billing = await billingService.GetBillingSnapshotAsync(storageProfileId);

        return new VanguardOperatorHireResponse(
            true,
            requestedProfileId,
            storageProfileId,
            "hired",
            profile,
            activeService,
            limits,
            remainingContracts.Length,
            state.ActiveService.Count + 1,
            invoice.Amount > 0,
            invoice,
            billing,
            now,
            VanguardBuildVersion.BuildLabel);
    }

    public async Task<VanguardOperatorDismissResponse> DismissAsync(string profileId, string? operatorId)
    {
        var requestedProfileId = profileId;
        var storageProfileId = await store.ResolveStorageProfileIdAsync(profileId);
        var state = await store.LoadStateAsync(storageProfileId);
        var now = DateTimeOffset.UtcNow;
        var limits = await deploymentLimitService.GetLimitsAsync(storageProfileId);

        if (string.IsNullOrWhiteSpace(operatorId))
        {
            return new VanguardOperatorDismissResponse(false, requestedProfileId, storageProfileId, "operator_id_required", null, null, limits, state.Operators.Count, state.ActiveService.Count, now, VanguardBuildVersion.BuildLabel);
        }

        var active = state.ActiveService.FirstOrDefault(record => string.Equals(record.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
        var profile = state.Operators.FirstOrDefault(record => string.Equals(record.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
        if (active is null || profile is null)
        {
            return new VanguardOperatorDismissResponse(false, requestedProfileId, storageProfileId, "operator_not_in_active_service", profile, active, limits, state.Operators.Count, state.ActiveService.Count, now, VanguardBuildVersion.BuildLabel);
        }

        var updatedProfile = profile with
        {
            ServiceStatus = VanguardOperatorServiceStatuses.Available,
            ContractStatus = VanguardOperatorContractStatuses.Released,
            UpdatedAtUtc = now,
        };
        var operators = state.Operators.Select(item => string.Equals(item.OperatorId, profile.OperatorId, StringComparison.OrdinalIgnoreCase) ? updatedProfile : item).ToArray();
        var activeService = state.ActiveService.Where(record => !string.Equals(record.OperatorId, active.OperatorId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var contacts = UpsertReleasedContact(state.Contacts, updatedProfile, now);

        await store.SaveOperatorsAsync(storageProfileId, operators);
        await store.SaveActiveServiceAsync(storageProfileId, activeService);
        await store.SaveContactsAsync(storageProfileId, contacts);

        return new VanguardOperatorDismissResponse(true, requestedProfileId, storageProfileId, "released_to_contacts", updatedProfile, active, limits, operators.Length, activeService.Length, now, VanguardBuildVersion.BuildLabel);
    }

    private async Task<VanguardOperatorHireResponse> BuildHireFailureAsync(string requestedProfileId, string storageProfileId, string reason, VanguardOperatorDeploymentLimits limits, int remainingContracts, int activeServiceCount)
    {
        var billing = await billingService.GetBillingSnapshotAsync(storageProfileId);
        return new VanguardOperatorHireResponse(false, requestedProfileId, storageProfileId, reason, null, null, limits, remainingContracts, activeServiceCount, false, null, billing, DateTimeOffset.UtcNow, VanguardBuildVersion.BuildLabel);
    }

    private static VanguardOperatorProfile CreateProfileFromOffer(VanguardOperatorContractOffer offer, DateTimeOffset now)
    {
        var identity = new VanguardOperatorIdentity(
            offer.OperatorId,
            offer.FirstName,
            offer.LastName,
            offer.Callsign,
            offer.DisplayName,
            offer.Side,
            offer.Side.Equals("Bear", StringComparison.OrdinalIgnoreCase) ? "ru" : "en",
            offer.VisualFamily,
            VanguardOperatorSchema.CurrentVersion);
        var persona = new VanguardOperatorPersona(
            offer.BasePersona,
            offer.Doctrine,
            offer.Temperament,
            offer.SainProfileFamily,
            offer.SainTuningPlan,
            offer.Traits,
            offer.BehaviorSummary,
            VanguardOperatorSchema.CurrentVersion,
            offer.CombatStyle,
            offer.EngagementRange,
            offer.SquadRole);
        var progression = new VanguardOperatorProgression(
            offer.Level,
            offer.Experience,
            0,
            0,
            0,
            0,
            0,
            50,
            50,
            50,
            VanguardOperatorSchema.CurrentVersion);

        return new VanguardOperatorProfile(
            offer.OperatorId,
            identity,
            offer.Role,
            offer.Specialty,
            VanguardOperatorContractStatuses.Contracted,
            VanguardOperatorServiceStatuses.ActiveService,
            offer.SalaryPerRaid,
            offer.HirePrice,
            offer.CurrencyTpl,
            persona,
            progression,
            now,
            now,
            VanguardOperatorSchema.CurrentVersion,
            "CorpsesOnly",
            VanguardOperatorCareer.NewEnrollment(now, offer.Level, offer.Experience));
    }

    private static VanguardOperatorContactRecord BuildActiveContact(VanguardOperatorProfile profile, DateTimeOffset now, string relationshipSummary)
    {
        return new VanguardOperatorContactRecord(
            profile.OperatorId,
            profile.Identity.DisplayName,
            "active",
            now,
            now,
            null,
            1,
            0,
            profile.Progression.Trust,
            profile.Progression.Loyalty,
            profile.Progression.Respect,
            0,
            string.IsNullOrWhiteSpace(relationshipSummary) ? "Initial Vanguard contract signed." : relationshipSummary,
            [new VanguardOperatorContactHistoryEntry($"contact-event-{Guid.NewGuid():N}", "contract_hired", "Operator entered active service.", now)],
            now,
            now,
            VanguardOperatorSchema.CurrentVersion);
    }

    private static IReadOnlyList<VanguardOperatorContactRecord> UpsertReleasedContact(IReadOnlyList<VanguardOperatorContactRecord> contacts, VanguardOperatorProfile profile, DateTimeOffset now)
    {
        var updatedContact = contacts.FirstOrDefault(contact => string.Equals(contact.OperatorId, profile.OperatorId, StringComparison.OrdinalIgnoreCase));
        if (updatedContact is null)
        {
            updatedContact = new VanguardOperatorContactRecord(
                profile.OperatorId,
                profile.Identity.DisplayName,
                "historical_contact",
                profile.CreatedAtUtc,
                null,
                now,
                1,
                profile.Progression.RaidCount,
                profile.Progression.Trust,
                profile.Progression.Loyalty,
                profile.Progression.Respect,
                0,
                "Operator released from active service and retained as known contact.",
                Array.Empty<VanguardOperatorContactHistoryEntry>(),
                profile.CreatedAtUtc,
                now,
                VanguardOperatorSchema.CurrentVersion);
        }

        updatedContact = updatedContact with
        {
            ContactStatus = "historical_contact",
            LastReleasedAtUtc = now,
            UpdatedAtUtc = now,
            NarrativeSummary = "Operator released from active service and retained as known contact.",
            HistoryEvents = updatedContact.HistoryEvents.Concat(new[] { new VanguardOperatorContactHistoryEntry($"contact-event-{Guid.NewGuid():N}", "released", "Operator released from active service.", now) }).ToArray(),
        };

        return UpsertContact(contacts, updatedContact);
    }

    private static IReadOnlyList<VanguardOperatorProfile> UpsertOperator(IReadOnlyList<VanguardOperatorProfile> operators, VanguardOperatorProfile profile)
    {
        var replaced = false;
        var result = operators.Select(existing =>
        {
            if (!string.Equals(existing.OperatorId, profile.OperatorId, StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }
            replaced = true;
            return profile;
        }).ToList();
        if (!replaced) result.Add(profile);
        return result;
    }

    private static IReadOnlyList<VanguardOperatorMedicalRecord> UpsertMedical(IReadOnlyList<VanguardOperatorMedicalRecord> records, VanguardOperatorMedicalRecord medical)
    {
        var replaced = false;
        var result = records.Select(existing =>
        {
            if (!string.Equals(existing.OperatorId, medical.OperatorId, StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }
            replaced = true;
            return medical;
        }).ToList();
        if (!replaced) result.Add(medical);
        return result;
    }

    private static IReadOnlyList<VanguardOperatorContactRecord> UpsertContact(IReadOnlyList<VanguardOperatorContactRecord> contacts, VanguardOperatorContactRecord contact)
    {
        var replaced = false;
        var result = contacts.Select(existing =>
        {
            if (!string.Equals(existing.OperatorId, contact.OperatorId, StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }
            replaced = true;
            return contact;
        }).ToList();

        if (!replaced)
        {
            result.Add(contact);
        }

        return result;
    }
}
