using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using Vanguard.Server.Diagnostics;
using Vanguard.Server.Operators.Inventory.Services;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Raid.Persistence.Services;
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
    VanguardOperatorInventoryModeService inventoryModeService,
    VanguardRaidOperatorPersistenceService raidPersistenceService,
    VanguardOperatorDeploymentStateService deploymentStateService,
    VanguardOperatorEnrollmentBaselineService enrollmentBaselineService,
    VanguardOperatorVisualIdentityService visualIdentityService,
    ISptLogger<VanguardOperatorRecruitmentService> logger)
{
    private const string ReleaseStatusTag = "VANGUARD_OPERATOR_RELEASE_STATUS";
    private static readonly SemaphoreSlim ReleaseGate = new(1, 1);
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
        bool wasPreviouslyReleased = existingProfile is not null
            && string.Equals(existingProfile.ContractStatus, VanguardOperatorContractStatuses.Released, StringComparison.OrdinalIgnoreCase);
        if (existingProfile is not null && state.ActiveService.Any(active => string.Equals(active.OperatorId, existingProfile.OperatorId, StringComparison.OrdinalIgnoreCase)))
        {
            return await BuildHireFailureAsync(requestedProfileId, storageProfileId, "operator_already_in_active_service", limits, state.Contracts.Count, state.ActiveService.Count);
        }

        bool firstEnrollment = existingProfile is null || existingProfile.Career is null;
        var profile = existingProfile is null
            ? CreateProfileFromOffer(offer, now)
            : existingProfile with
            {
                ContractStatus = VanguardOperatorContractStatuses.Contracted,
                ServiceStatus = VanguardOperatorServiceStatuses.ActiveService,
                SalaryPerRaid = offer.SalaryPerRaid,
                Career = existingProfile.Career ?? VanguardOperatorCareer.NewEnrollment(now, existingProfile.Progression.Level, existingProfile.Progression.Experience),
                UpdatedAtUtc = now,
            };

        if (existingProfile is null && profile.VisualIdentity is null)
        {
            try
            {
                profile = profile with { VisualIdentity = visualIdentityService.ResolveNewWorldIdentity(profile, now) };
            }
            catch (Exception exception)
            {
                return await BuildHireFailureAsync(
                    requestedProfileId,
                    storageProfileId,
                    "operator_visual_identity_failed_" + exception.GetType().Name,
                    limits,
                    state.Contracts.Count,
                    state.ActiveService.Count);
            }
        }

        if (firstEnrollment && profile.EnrollmentBaseline is null)
        {
            try
            {
                profile = profile with
                {
                    EnrollmentBaseline = enrollmentBaselineService.CreateNativeEnrollmentState(profile, now),
                    SchemaVersion = VanguardOperatorSchema.CurrentVersion,
                };
            }
            catch (Exception exception)
            {
                return await BuildHireFailureAsync(
                    requestedProfileId,
                    storageProfileId,
                    "operator_enrollment_baseline_failed_" + exception.GetType().Name,
                    limits,
                    state.Contracts.Count,
                    state.ActiveService.Count);
            }
        }
        VanguardOperatorMedicalRecord? existingMedical = state.Medical.FirstOrDefault(record =>
            string.Equals(record.OperatorId, profile.OperatorId, StringComparison.OrdinalIgnoreCase));
        var medical = existingMedical ?? new VanguardOperatorMedicalRecord(
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

        bool recovering = medical.RecoveryUntilUtc is not null && medical.RecoveryUntilUtc > now;
        profile = profile with
        {
            ServiceStatus = recovering ? VanguardOperatorServiceStatuses.Recovering : VanguardOperatorServiceStatuses.ActiveService,
            UpdatedAtUtc = now,
        };
        var isSelectedForRaid = !recovering && state.ActiveService.Count(record => record.IsSelectedForRaid) < limits.MaxDeployableOperators;
        var activeService = new VanguardActiveServiceRecord(
            profile.OperatorId,
            profile.Identity.DisplayName,
            profile.Identity.Side,
            profile.Role,
            profile.Specialty,
            recovering ? VanguardOperatorServiceStatuses.Recovering : VanguardOperatorServiceStatuses.ActiveService,
            isSelectedForRaid,
            false,
            now,
            profile.SalaryPerRaid,
            null,
            medical.RecoveryUntilUtc,
            VanguardOperatorSchema.CurrentVersion);

        var contact = BuildHiredContact(state.Contacts, profile, now, offer.RelationshipSummary, wasPreviouslyReleased);
        var remainingContracts = state.Contracts.Where(candidate => !string.Equals(candidate.OfferId, offer.OfferId, StringComparison.OrdinalIgnoreCase)).ToArray();

        try
        {
            await inventoryModeService.EnsurePersistentOperatorProfileAsync(
                new MongoId(requestedProfileId),
                storageProfileId,
                profile,
                firstEnrollment);
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

        if (wasPreviouslyReleased)
        {
            VanguardOperatorContactRecord? previousContact = state.Contacts.FirstOrDefault(candidate =>
                string.Equals(candidate.OperatorId, profile.OperatorId, StringComparison.OrdinalIgnoreCase));
            logger.Info(VanguardServerDiagnosticsLog.Present(
                $"[{ReleaseStatusTag}] phase=rehire; owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; state=committed; sameOperator=true; enrollmentPreserved={Bool(Equals(existingProfile?.EnrollmentBaseline, profile.EnrollmentBaseline))}; visualIdentityPreserved={Bool(Equals(existingProfile?.VisualIdentity, profile.VisualIdentity))}; medicalPreserved={Bool(existingMedical is not null && Equals(existingMedical, medical))}; contactHistoryPreserved={Bool(previousContact is not null && contact.HistoryEvents.Count >= previousContact.HistoryEvents.Count)}; recoveryPreserved={Bool(medical.RecoveryUntilUtc == existingMedical?.RecoveryUntilUtc)}; tag={ReleaseStatusTag}"));
        }

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
        var now = DateTimeOffset.UtcNow;
        var limits = await deploymentLimitService.GetLimitsAsync(storageProfileId);

        if (string.IsNullOrWhiteSpace(operatorId))
        {
            var state = await store.LoadStateAsync(storageProfileId);
            return BuildDismissFailure(requestedProfileId, storageProfileId, "operator_id_required", null, null, limits, state, now);
        }

        if (!await ReleaseGate.WaitAsync(0))
        {
            var state = await store.LoadStateAsync(storageProfileId);
            LogRelease(storageProfileId, operatorId, "rejected", "release_transaction_busy", state.Operators.Count, state.ActiveService.Count, false, false);
            return BuildDismissFailure(requestedProfileId, storageProfileId, "release_transaction_busy", null, null, limits, state, now);
        }

        try
        {
            using IDisposable? persistenceLease = await raidPersistenceService.TryAcquireExternalMutationLeaseAsync();
            if (persistenceLease is null)
            {
                var blockedState = await store.LoadStateAsync(storageProfileId);
                LogRelease(storageProfileId, operatorId, "rejected", "unsafe_raid_persistence_commit_active", blockedState.Operators.Count, blockedState.ActiveService.Count, false, false);
                return BuildDismissFailure(requestedProfileId, storageProfileId, "unsafe_raid_persistence_commit_active", null, null, limits, blockedState, now);
            }

            VanguardDeploymentProcessBaselineResult deploymentBaseline = await deploymentStateService.EnsureProcessBaselineAsync(storageProfileId);
            var state = await store.LoadStateAsync(storageProfileId);
            if (!deploymentBaseline.Success)
            {
                string reason = "deployment_process_baseline_failed_" + deploymentBaseline.Reason;
                LogRelease(storageProfileId, operatorId, "rejected", reason, state.Operators.Count, state.ActiveService.Count, false, false);
                return BuildDismissFailure(requestedProfileId, storageProfileId, reason, null, null, limits, state, now);
            }

            if (IsInventorySessionActive(storageProfileId))
            {
                LogRelease(storageProfileId, operatorId, "rejected", "unsafe_operator_inventory_session_active", state.Operators.Count, state.ActiveService.Count, false, false);
                return BuildDismissFailure(requestedProfileId, storageProfileId, "unsafe_operator_inventory_session_active", null, null, limits, state, now);
            }

            using IDisposable? inventoryLease = await inventoryModeService.TryAcquireExternalMutationLeaseAsync(storageProfileId, new[] { operatorId });
            if (inventoryLease is null)
            {
                LogRelease(storageProfileId, operatorId, "rejected", "unsafe_operator_inventory_mutation_active", state.Operators.Count, state.ActiveService.Count, false, false);
                return BuildDismissFailure(requestedProfileId, storageProfileId, "unsafe_operator_inventory_mutation_active", null, null, limits, state, now);
            }

            if (IsInventorySessionActive(storageProfileId))
            {
                state = await store.LoadStateAsync(storageProfileId);
                LogRelease(storageProfileId, operatorId, "rejected", "unsafe_operator_inventory_session_became_active", state.Operators.Count, state.ActiveService.Count, false, false);
                return BuildDismissFailure(requestedProfileId, storageProfileId, "unsafe_operator_inventory_session_became_active", null, null, limits, state, now);
            }

            state = await store.LoadStateAsync(storageProfileId);
            var active = state.ActiveService.FirstOrDefault(record => string.Equals(record.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
            var profile = state.Operators.FirstOrDefault(record => string.Equals(record.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
            if (active is null || profile is null)
            {
                LogRelease(storageProfileId, operatorId, "rejected", "operator_not_in_active_service", state.Operators.Count, state.ActiveService.Count, false, false);
                return BuildDismissFailure(requestedProfileId, storageProfileId, "operator_not_in_active_service", profile, active, limits, state, now);
            }

            if (active.IsDeployed)
            {
                LogRelease(storageProfileId, operatorId, "rejected", "unsafe_operator_deployed", state.Operators.Count, state.ActiveService.Count, false, false);
                return BuildDismissFailure(requestedProfileId, storageProfileId, "unsafe_operator_deployed", profile, active, limits, state, now);
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

            VanguardOperatorReleaseWriteResult write = await store.CommitReleaseFromServiceAsync(
                storageProfileId,
                state.Operators,
                operators,
                state.ActiveService,
                activeService,
                state.Contacts,
                contacts);
            if (!write.Success || !write.ReadBackVerified)
            {
                var readBackState = await store.LoadStateAsync(storageProfileId);
                string reason = "release_transaction_failed_" + write.Reason;
                LogRelease(storageProfileId, operatorId, "failed", reason, readBackState.Operators.Count, readBackState.ActiveService.Count, write.ReadBackVerified, write.RolledBack);
                return BuildDismissFailure(requestedProfileId, storageProfileId, reason, profile, active, limits, readBackState, now);
            }

            LogRelease(storageProfileId, operatorId, "committed", "released_to_contacts", operators.Length, activeService.Length, true, false);
            return new VanguardOperatorDismissResponse(true, requestedProfileId, storageProfileId, "released_to_contacts", updatedProfile, active, limits, operators.Length, activeService.Length, now, VanguardBuildVersion.BuildLabel);
        }
        finally
        {
            ReleaseGate.Release();
        }
    }

    private static VanguardOperatorDismissResponse BuildDismissFailure(
        string requestedProfileId,
        string storageProfileId,
        string reason,
        VanguardOperatorProfile? profile,
        VanguardActiveServiceRecord? active,
        VanguardOperatorDeploymentLimits limits,
        VanguardOperatorStorageState state,
        DateTimeOffset now)
    {
        return new VanguardOperatorDismissResponse(
            false,
            requestedProfileId,
            storageProfileId,
            reason,
            profile,
            active,
            limits,
            state.Operators.Count,
            state.ActiveService.Count,
            now,
            VanguardBuildVersion.BuildLabel);
    }

    private bool IsInventorySessionActive(string storageProfileId)
    {
        try
        {
            return inventoryModeService.IsActive(new MongoId(storageProfileId));
        }
        catch
        {
            return true;
        }
    }

    private void LogRelease(string storageProfileId, string operatorId, string state, string reason, int operatorCount, int activeServiceCount, bool readBack, bool rollback)
    {
        bool mutation = string.Equals(state, "committed", StringComparison.OrdinalIgnoreCase);
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[{ReleaseStatusTag}] owner={Safe(storageProfileId)}; operator={Safe(operatorId)}; state={Safe(state)}; reason={Safe(reason)}; operators={operatorCount}; activeService={activeServiceCount}; mutation={Bool(mutation)}; readback={Bool(readBack)}; rollback={Bool(rollback)}; tag={ReleaseStatusTag}"));
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

    private static VanguardOperatorContactRecord BuildHiredContact(
        IReadOnlyList<VanguardOperatorContactRecord> contacts,
        VanguardOperatorProfile profile,
        DateTimeOffset now,
        string relationshipSummary,
        bool rehire)
    {
        VanguardOperatorContactRecord? existing = contacts.FirstOrDefault(contact =>
            string.Equals(contact.OperatorId, profile.OperatorId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return BuildActiveContact(profile, now, relationshipSummary);
        }

        string eventType = rehire ? "contract_rehired" : "contract_hired";
        string eventSummary = rehire ? "Operator returned to active service." : "Operator entered active service.";
        return existing with
        {
            DisplayName = profile.Identity.DisplayName,
            ContactStatus = "active",
            FirstHiredAtUtc = existing.FirstHiredAtUtc ?? now,
            LastHiredAtUtc = now,
            ActiveServiceCount = rehire ? Math.Max(0, existing.ActiveServiceCount) + 1 : Math.Max(1, existing.ActiveServiceCount),
            Trust = profile.Progression.Trust,
            Loyalty = profile.Progression.Loyalty,
            Respect = profile.Progression.Respect,
            NarrativeSummary = rehire
                ? "Known Vanguard contact returned to active service."
                : string.IsNullOrWhiteSpace(relationshipSummary) ? existing.NarrativeSummary : relationshipSummary,
            HistoryEvents = existing.HistoryEvents.Concat(new[]
            {
                new VanguardOperatorContactHistoryEntry($"contact-event-{Guid.NewGuid():N}", eventType, eventSummary, now)
            }).ToArray(),
            UpdatedAtUtc = now,
        };
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

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim().Replace(';', '_').Replace('\r', ' ').Replace('\n', ' ');
    private static string Bool(bool value) => value ? "true" : "false";
}
