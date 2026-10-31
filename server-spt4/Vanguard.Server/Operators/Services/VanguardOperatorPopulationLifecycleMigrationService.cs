using System.Security.Cryptography;
using System.Text;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using Vanguard.Server.Diagnostics;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Raid.Persistence.Models;
using Vanguard.Server.Operators.Storage;

// Responsibility: Performs the one-time bounded-population migration for one Operator owner profile.
// Flow: It accumulates durable ever-hired evidence, preserves ambiguous history, purges only proven never-hired people,
// migrates legacy synthetic callsigns, reconciles offers/contacts/active-service/identity reservations, then commits by expected-before/readback.
// Authority boundary: This service decides migration membership; VanguardOperatorStore alone owns the multi-file write/backup/rollback transaction.
// Invariant: Any ambiguous or unreadable hire-history evidence fails safe toward preservation, and historical raid/billing ledgers are never rewritten.
namespace Vanguard.Server.Operators.Services;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorPopulationLifecycleMigrationService(
    VanguardOperatorStore store,
    ISptLogger<VanguardOperatorPopulationLifecycleMigrationService> logger)
{
    public const int PolicyVersion = 1;
    private const string StatusTag = "VANGUARD_OPERATOR_POPULATION_MIGRATION_STATUS";

    public async Task<VanguardOperatorPopulationLifecycleMigrationResult> ApplyIfRequiredAsync(
        string ownerProfileId,
        IReadOnlyList<VanguardOperatorProfile> operators,
        IReadOnlyList<VanguardOperatorContractOffer> contracts,
        IReadOnlyList<VanguardActiveServiceRecord> activeService,
        IReadOnlyList<VanguardOperatorContactRecord> contacts,
        IReadOnlyList<VanguardOperatorIdentityReservation> identityRegistry,
        DateTimeOffset now)
    {
        bool journalFilePresent = store.HasPopulationLifecycleMigrationJournal(ownerProfileId);
        VanguardOperatorPopulationLifecycleMigrationJournal? journal =
            await store.LoadPopulationLifecycleMigrationJournalAsync(ownerProfileId);
        if (journalFilePresent && journal is null)
        {
            return Blocked(ownerProfileId, "migration_journal_unreadable", operators, contracts, activeService, contacts, identityRegistry);
        }
        if (journal is { PolicyVersion: >= PolicyVersion })
        {
            return VanguardOperatorPopulationLifecycleMigrationResult.NoChange(
                "journal_current", operators, contracts, activeService, contacts, identityRegistry, journalCurrent: true);
        }

        if (store.HasPopulationMigrationEvidenceQuarantine(ownerProfileId))
        {
            return Blocked(ownerProfileId, "historical_evidence_quarantine_present", operators, contracts, activeService, contacts, identityRegistry);
        }

        VanguardCareerRaidLedgerReadSnapshot raidLedger = await store.LoadCareerRaidLedgerSnapshotAsync(ownerProfileId);
        if (raidLedger.QuarantineEvidencePresent
            || (raidLedger.ActiveFilePresent && !string.Equals(raidLedger.ReadState, "readable", StringComparison.OrdinalIgnoreCase)))
        {
            return Blocked(ownerProfileId, "raid_ledger_ambiguous_or_quarantined", operators, contracts, activeService, contacts, identityRegistry);
        }

        if (store.HasBillingLedgerQuarantineEvidence(ownerProfileId))
        {
            return Blocked(ownerProfileId, "billing_ledger_quarantine_evidence", operators, contracts, activeService, contacts, identityRegistry);
        }

        VanguardOperatorBillingLedger billing = await store.LoadBillingLedgerAsync(ownerProfileId);
        IReadOnlyList<VanguardOperatorMedicalRecord> medical = await store.LoadMedicalAsync(ownerProfileId);
        if (store.HasPopulationMigrationEvidenceQuarantine(ownerProfileId))
        {
            return Blocked(ownerProfileId, "historical_evidence_became_quarantined_during_read", operators, contracts, activeService, contacts, identityRegistry);
        }
        var everHired = BuildEverHiredEvidence(ownerProfileId, operators, activeService, contacts, medical, raidLedger.Entries, billing);
        var purgeNeverHired = operators
            .Where(profile => !everHired.Contains(profile.OperatorId)
                && string.Equals(profile.ContractStatus, VanguardOperatorContractStatuses.Uncontracted, StringComparison.OrdinalIgnoreCase))
            .Select(profile => profile.OperatorId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int preservedAmbiguous = operators.Count(profile =>
            !everHired.Contains(profile.OperatorId)
            && !purgeNeverHired.Contains(profile.OperatorId));

        // A durable Operator inventory profile is positive service evidence. Never destroy it merely because an older
        // scalar status failed to carry the historical engagement state.
        foreach (VanguardOperatorProfile profile in operators.Where(profile => purgeNeverHired.Contains(profile.OperatorId)).ToArray())
        {
            string inventoryPath = store.GetOperatorInventoryProfileReadPath(ownerProfileId, profile.OperatorId);
            if (File.Exists(inventoryPath))
            {
                everHired.Add(profile.OperatorId);
                purgeNeverHired.Remove(profile.OperatorId);
            }
        }

        var survivors = operators.Where(profile => !purgeNeverHired.Contains(profile.OperatorId)).ToList();
        var usedCallsigns = identityRegistry
            .Where(entry => !(string.Equals(entry.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                && purgeNeverHired.Contains(entry.OperatorId)))
            .Where(entry => !VanguardCallsignCatalog.IsSyntheticLegacyCallsign(entry.Callsign))
            .Select(entry => entry.Callsign)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Concat(survivors
                .Where(profile => !VanguardCallsignCatalog.IsSyntheticLegacyCallsign(profile.Identity.Callsign))
                .Select(profile => profile.Identity.Callsign))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var callsignMigrations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < survivors.Count; index++)
        {
            VanguardOperatorProfile profile = survivors[index];
            if (!VanguardCallsignCatalog.IsSyntheticLegacyCallsign(profile.Identity.Callsign)) continue;

            if (!VanguardCallsignCatalog.TryAllocateDeterministic(
                    $"population-v1|{ownerProfileId}|{profile.OperatorId}",
                    usedCallsigns,
                    out string callsign))
            {
                return Blocked(ownerProfileId, "callsign_catalog_exhausted_fail_closed", operators, contracts, activeService, contacts, identityRegistry);
            }

            callsignMigrations[profile.OperatorId] = callsign;
            survivors[index] = ApplyCallsignMigration(profile, callsign, now);
        }

        var survivorById = survivors
            .Where(profile => !string.IsNullOrWhiteSpace(profile.OperatorId))
            .ToDictionary(profile => profile.OperatorId, StringComparer.OrdinalIgnoreCase);

        var afterContracts = contracts
            .Where(offer => !purgeNeverHired.Contains(offer.OperatorId))
            .Select(offer => callsignMigrations.TryGetValue(offer.OperatorId, out string? callsign)
                ? offer with { Callsign = callsign, DisplayName = BuildDisplayName(callsign, offer.LastName) }
                : offer)
            .ToArray();

        var afterActiveService = activeService
            .Where(record => !purgeNeverHired.Contains(record.OperatorId))
            .Select(record => survivorById.TryGetValue(record.OperatorId, out VanguardOperatorProfile? profile)
                ? record with { DisplayName = profile.Identity.DisplayName }
                : record)
            .ToArray();

        var afterContacts = contacts
            .Where(record => !purgeNeverHired.Contains(record.OperatorId))
            .Select(record => survivorById.TryGetValue(record.OperatorId, out VanguardOperatorProfile? profile)
                ? record with { DisplayName = profile.Identity.DisplayName, UpdatedAtUtc = now }
                : record)
            .ToArray();

        var afterRegistry = identityRegistry
            .Where(entry => !(string.Equals(entry.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                && purgeNeverHired.Contains(entry.OperatorId)))
            .Select(entry => callsignMigrations.TryGetValue(entry.OperatorId, out string? callsign)
                && string.Equals(entry.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                    ? entry with
                    {
                        Callsign = callsign,
                        DisplayName = BuildDisplayName(callsign, entry.LastName),
                        LastSeenAtUtc = now,
                        Source = "population_lifecycle_v1",
                        IsPermanent = true,
                        ExpiresAtUtc = null
                    }
                    : entry)
            .ToList();

        foreach (VanguardOperatorProfile profile in survivors)
        {
            int reservationIndex = afterRegistry.FindIndex(entry =>
                string.Equals(entry.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.OperatorId, profile.OperatorId, StringComparison.OrdinalIgnoreCase));
            if (reservationIndex >= 0)
            {
                VanguardOperatorIdentityReservation current = afterRegistry[reservationIndex];
                afterRegistry[reservationIndex] = current with
                {
                    FirstName = profile.Identity.FirstName,
                    LastName = profile.Identity.LastName,
                    Callsign = profile.Identity.Callsign,
                    DisplayName = profile.Identity.DisplayName,
                    Side = profile.Identity.Side,
                    Source = "population_lifecycle_v1",
                    LastSeenAtUtc = now,
                    IsPermanent = true,
                    ExpiresAtUtc = null
                };
            }
            else
            {
                afterRegistry.Add(new VanguardOperatorIdentityReservation(
                    profile.OperatorId,
                    ownerProfileId,
                    profile.Identity.FirstName,
                    profile.Identity.LastName,
                    profile.Identity.Callsign,
                    profile.Identity.DisplayName,
                    profile.Identity.Side,
                    "population_lifecycle_v1",
                    now,
                    now,
                    true,
                    null));
            }
        }

        string beforeFingerprint = Fingerprint(operators, contracts, activeService, contacts, identityRegistry);
        string afterFingerprint = Fingerprint(survivors, afterContracts, afterActiveService, afterContacts, afterRegistry);
        bool mutationRequired = !string.Equals(beforeFingerprint, afterFingerprint, StringComparison.Ordinal);

        if (mutationRequired)
        {
            VanguardOperatorPopulationLifecycleMigrationWriteResult write = await store.CommitPopulationLifecycleMigrationAsync(
                ownerProfileId,
                operators, survivors,
                contracts, afterContracts,
                activeService, afterActiveService,
                contacts, afterContacts,
                identityRegistry, afterRegistry);
            if (!write.Success)
            {
                return Blocked(ownerProfileId, "transaction_failed_" + write.Reason, operators, contracts, activeService, contacts, identityRegistry);
            }
        }

        int preservedEverHired = survivors.Count(profile => everHired.Contains(profile.OperatorId));
        var completedJournal = new VanguardOperatorPopulationLifecycleMigrationJournal(
            PolicyVersion,
            now,
            operators.Count,
            survivors.Count,
            purgeNeverHired.Count,
            preservedEverHired,
            preservedAmbiguous,
            callsignMigrations.Count,
            afterFingerprint);
        await store.SavePopulationLifecycleMigrationJournalAsync(ownerProfileId, completedJournal);

        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] owner={ownerProfileId}; policyVersion={PolicyVersion}; state=committed; mutationApplied={mutationRequired.ToString().ToLowerInvariant()}; beforeWorld={operators.Count}; afterWorld={survivors.Count}; purgedNeverHired={purgeNeverHired.Count}; preservedEverHired={preservedEverHired}; preservedAmbiguous={preservedAmbiguous}; migratedSyntheticCallsigns={callsignMigrations.Count}; backups=true; expectedBefore=true; readback=true; rollback=false; idempotenceJournal=true; historicalLedgersRewritten=false; tag={StatusTag}"));

        return new VanguardOperatorPopulationLifecycleMigrationResult(
            false, mutationRequired, false, "committed", survivors, afterContracts, afterActiveService,
            afterContacts, afterRegistry, purgeNeverHired.Count, preservedEverHired, preservedAmbiguous, callsignMigrations.Count);
    }

    private VanguardOperatorPopulationLifecycleMigrationResult Blocked(
        string ownerProfileId,
        string reason,
        IReadOnlyList<VanguardOperatorProfile> operators,
        IReadOnlyList<VanguardOperatorContractOffer> contracts,
        IReadOnlyList<VanguardActiveServiceRecord> activeService,
        IReadOnlyList<VanguardOperatorContactRecord> contacts,
        IReadOnlyList<VanguardOperatorIdentityReservation> identityRegistry)
    {
        logger.Warning(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] owner={ownerProfileId}; policyVersion={PolicyVersion}; state=blocked_fail_closed; reason={Safe(reason)}; marketMutation=false; neverHiredPurge=false; callsignMigration=false; ambiguousPreserved=true; tag={StatusTag}"));
        return VanguardOperatorPopulationLifecycleMigrationResult.NoChange(
            reason, operators, contracts, activeService, contacts, identityRegistry, migrationBlocked: true);
    }

    private static HashSet<string> BuildEverHiredEvidence(
        string ownerProfileId,
        IReadOnlyList<VanguardOperatorProfile> operators,
        IReadOnlyList<VanguardActiveServiceRecord> activeService,
        IReadOnlyList<VanguardOperatorContactRecord> contacts,
        IReadOnlyList<VanguardOperatorMedicalRecord> medical,
        IReadOnlyList<VanguardCareerRaidLedgerEntry> raidEntries,
        VanguardOperatorBillingLedger billing)
    {
        var result = activeService
            .Select(record => record.OperatorId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (VanguardOperatorProfile profile in operators)
        {
            if (string.Equals(profile.ContractStatus, VanguardOperatorContractStatuses.Contracted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(profile.ContractStatus, VanguardOperatorContractStatuses.Released, StringComparison.OrdinalIgnoreCase)
                || profile.Career is not null
                || profile.EnrollmentBaseline is not null)
            {
                result.Add(profile.OperatorId);
            }
        }

        foreach (VanguardOperatorContactRecord contact in contacts)
        {
            if (contact.FirstHiredAtUtc.HasValue
                || contact.LastHiredAtUtc.HasValue
                || contact.LastReleasedAtUtc.HasValue
                || contact.ActiveServiceCount > 0
                || contact.RaidTogetherCount > 0
                || (contact.HistoryEvents?.Count ?? 0) > 0)
            {
                result.Add(contact.OperatorId);
            }
        }

        foreach (VanguardOperatorMedicalRecord record in medical)
        {
            if (!string.IsNullOrWhiteSpace(record.OperatorId)) result.Add(record.OperatorId);
        }

        foreach (VanguardCareerRaidLedgerEntry entry in raidEntries)
        {
            if ((string.IsNullOrWhiteSpace(entry.OwnerProfileId)
                    || string.Equals(entry.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(entry.OperatorId))
            {
                result.Add(entry.OperatorId);
            }
        }

        foreach (VanguardOperatorBillingInvoice invoice in billing.Invoices ?? Array.Empty<VanguardOperatorBillingInvoice>())
        {
            if (!string.IsNullOrWhiteSpace(invoice.OperatorId)) result.Add(invoice.OperatorId);
        }

        return result;
    }

    private static VanguardOperatorProfile ApplyCallsignMigration(VanguardOperatorProfile profile, string callsign, DateTimeOffset now)
    {
        string displayName = BuildDisplayName(callsign, profile.Identity.LastName);
        return profile with
        {
            Identity = profile.Identity with { Callsign = callsign, DisplayName = displayName },
            UpdatedAtUtc = now
        };
    }

    private static string BuildDisplayName(string callsign, string? lastName) =>
        string.IsNullOrWhiteSpace(lastName) ? callsign : $"{callsign} {lastName.Trim()}";

    private static string Fingerprint(
        IReadOnlyList<VanguardOperatorProfile> operators,
        IReadOnlyList<VanguardOperatorContractOffer> contracts,
        IReadOnlyList<VanguardActiveServiceRecord> activeService,
        IReadOnlyList<VanguardOperatorContactRecord> contacts,
        IReadOnlyList<VanguardOperatorIdentityReservation> identityRegistry)
    {
        string canonical = string.Join("\n", operators.OrderBy(x => x.OperatorId, StringComparer.OrdinalIgnoreCase)
                .Select(x => $"O|{x.OperatorId}|{x.ContractStatus}|{x.Identity.Callsign}|{x.Identity.DisplayName}"))
            + "\n" + string.Join("\n", contracts.OrderBy(x => x.OperatorId, StringComparer.OrdinalIgnoreCase)
                .Select(x => $"C|{x.OperatorId}|{x.Callsign}|{x.DisplayName}|{x.AvailableUntilUtc:O}"))
            + "\n" + string.Join("\n", activeService.OrderBy(x => x.OperatorId, StringComparer.OrdinalIgnoreCase)
                .Select(x => $"A|{x.OperatorId}|{x.DisplayName}|{x.Status}"))
            + "\n" + string.Join("\n", contacts.OrderBy(x => x.OperatorId, StringComparer.OrdinalIgnoreCase)
                .Select(x => $"K|{x.OperatorId}|{x.DisplayName}|{x.ActiveServiceCount}|{x.RaidTogetherCount}"))
            + "\n" + string.Join("\n", identityRegistry.OrderBy(x => x.OwnerProfileId, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.OperatorId, StringComparer.OrdinalIgnoreCase)
                .Select(x => $"I|{x.OwnerProfileId}|{x.OperatorId}|{x.Callsign}|{x.DisplayName}|{x.IsPermanent}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

public sealed record VanguardOperatorPopulationLifecycleMigrationResult(
    bool JournalCurrent,
    bool MutationApplied,
    bool MigrationBlocked,
    string Reason,
    IReadOnlyList<VanguardOperatorProfile> Operators,
    IReadOnlyList<VanguardOperatorContractOffer> Contracts,
    IReadOnlyList<VanguardActiveServiceRecord> ActiveService,
    IReadOnlyList<VanguardOperatorContactRecord> Contacts,
    IReadOnlyList<VanguardOperatorIdentityReservation> IdentityRegistry,
    int PurgedNeverHired,
    int PreservedEverHired,
    int PreservedAmbiguous,
    int MigratedSyntheticCallsigns)
{
    public static VanguardOperatorPopulationLifecycleMigrationResult NoChange(
        string reason,
        IReadOnlyList<VanguardOperatorProfile> operators,
        IReadOnlyList<VanguardOperatorContractOffer> contracts,
        IReadOnlyList<VanguardActiveServiceRecord> activeService,
        IReadOnlyList<VanguardOperatorContactRecord> contacts,
        IReadOnlyList<VanguardOperatorIdentityReservation> identityRegistry,
        bool journalCurrent = false,
        bool migrationBlocked = false) =>
        new(journalCurrent, false, migrationBlocked, reason, operators, contracts, activeService, contacts, identityRegistry, 0, 0, 0, 0);
}
