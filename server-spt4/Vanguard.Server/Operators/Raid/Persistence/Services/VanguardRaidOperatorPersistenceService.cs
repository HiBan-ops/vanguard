using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using Vanguard.Server.Operators.Inventory.Services;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Raid.Persistence.Models;
using Vanguard.Server.Operators.Services;
using Vanguard.Server.Operators.Storage;
using Vanguard.Server.Diagnostics;

// Responsibility: Commits the end-of-raid Operator state as one server-side transaction while keeping Career-ledger and Operator-profile consistency explicit.
// Flow: Incoming raid truth is validated and normalized, per-Operator updates plus Career ledger facts are prepared, store writes are applied, then readback/audit decides whether the batch is accepted or rolled back.
// Authority boundary: The server Operator store is durable authority; the client supplies observed raid facts but cannot directly write canonical persisted Operator state.
// Invariant: A batch must be replay-safe and all-or-recoverable: partial writes, identity mismatches or failed readback cannot silently leave a mixed durable state.
namespace Vanguard.Server.Operators.Raid.Persistence.Services;

/// <summary>
/// Authoritative raid-close reconciliation for persistent Vanguard Operators.
/// The request is validated as one cross-owner batch before any write.  This is
/// deliberately separate from the off-raid equipment session: raid truth comes
/// from EFT's final runtime profile descriptor, while persistence still reuses
/// the canonical Operator equipment-subtree storage primitive.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class VanguardRaidOperatorPersistenceService(
    VanguardOperatorStore store,
    VanguardOperatorInventoryModeService inventoryModeService,
    VanguardOperatorCareerTruthProbeService careerTruthProbeService,
    VanguardCareerRaidLedgerCommitService careerLedgerService,
    VanguardOperatorCareerXpCommitService careerXpCommitService,
    VanguardOperatorBillingService billingService,
    ISptLogger<VanguardRaidOperatorPersistenceService> logger)
{
    private static readonly SemaphoreSlim CommitGate = new(1, 1);
    private readonly ConcurrentDictionary<string, VanguardRaidOperatorPersistenceBatchResponse> committedBatches = new(StringComparer.OrdinalIgnoreCase);

    public async Task<VanguardRaidOperatorPersistenceBatchResponse> CommitAsync(
        MongoId requesterSessionId,
        VanguardRaidOperatorPersistenceBatchRequest request)
    {
        string raidSessionId = Normalize(request.RaidSessionId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(raidSessionId))
        {
            return Failure("raid_session_id_required", raidSessionId, request.Operators?.Count ?? 0, now);
        }

        VanguardRaidOperatorPersistenceEntryRequest[] entries = (request.Operators ?? Array.Empty<VanguardRaidOperatorPersistenceEntryRequest>())
            .Where(entry => entry is not null)
            .ToArray();
        if (entries.Length == 0)
        {
            return Failure("operator_batch_empty", raidSessionId, 0, now);
        }

        if (!string.Equals(Normalize(request.ClientLabel), VanguardBuildVersion.BuildLabel, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Normalize(request.ClientBuild), VanguardBuildVersion.Value, StringComparison.OrdinalIgnoreCase))
        {
            return Failure("client_server_build_mismatch", raidSessionId, entries.Length, now);
        }

        string authorityKind = Normalize(request.AuthorityKind);
        if (!string.Equals(authorityKind, "local_spt", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(authorityKind, "fika_headless", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(authorityKind, "fika_player_host", StringComparison.OrdinalIgnoreCase))
        {
            return Failure("persistence_authority_kind_rejected", raidSessionId, entries.Length, now);
        }

        string batchKey = BuildBatchKey(raidSessionId, entries);
        VanguardRaidOperatorPersistenceBatchResponse? durableReplay = await TryLoadCommittedBatchAsync(batchKey);
        if (durableReplay is not null)
        {
            committedBatches[batchKey] = durableReplay;
            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_RAID_PERSISTENCE_STATUS] idempotent_replay_durable raid={raidSessionId}; requester={requesterSessionId}; operators={entries.Length}; key={batchKey}; committed={durableReplay.CommittedOperatorCount}"));
            return durableReplay with { IdempotentReplay = true };
        }

        if (committedBatches.TryGetValue(batchKey, out VanguardRaidOperatorPersistenceBatchResponse? replay))
        {
            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_RAID_PERSISTENCE_STATUS] idempotent_replay_memory raid={raidSessionId}; requester={requesterSessionId}; operators={entries.Length}; key={batchKey}; committed={replay.CommittedOperatorCount}"));
            return replay with { IdempotentReplay = true };
        }

        await CommitGate.WaitAsync();
        try
        {
            durableReplay = await TryLoadCommittedBatchAsync(batchKey);
            if (durableReplay is not null)
            {
                committedBatches[batchKey] = durableReplay;
                return durableReplay with { IdempotentReplay = true };
            }

            if (committedBatches.TryGetValue(batchKey, out replay))
            {
                return replay with { IdempotentReplay = true };
            }

            var prepared = new List<PreparedEntry>(entries.Length);
            var runtimeItemOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var globalItemOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var seenOperators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (VanguardRaidOperatorPersistenceEntryRequest entry in entries)
            {
                string operatorId = Normalize(entry.OperatorId);
                string ownerProfileId = Normalize(entry.OwnerProfileId);
                if (string.IsNullOrWhiteSpace(operatorId) || string.IsNullOrWhiteSpace(ownerProfileId))
                {
                    return Failure("operator_or_owner_id_required", raidSessionId, entries.Length, now);
                }

                string operatorKey = ownerProfileId + ":" + operatorId;
                if (!seenOperators.Add(operatorKey))
                {
                    return Failure("duplicate_operator_entry_" + operatorId, raidSessionId, entries.Length, now);
                }

                string storageProfileId = await store.ResolveStorageProfileIdAsync(ownerProfileId);
                IReadOnlyList<VanguardOperatorProfile> operators = await store.LoadOperatorsAsync(storageProfileId);
                VanguardOperatorProfile? persistentOperator = operators.FirstOrDefault(candidate =>
                    string.Equals(candidate.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
                if (persistentOperator is null)
                {
                    return Failure("persistent_operator_not_found_" + operatorId, raidSessionId, entries.Length, now);
                }

                IReadOnlyList<VanguardActiveServiceRecord> active = await store.LoadActiveServiceAsync(storageProfileId);
                if (!active.Any(candidate => string.Equals(candidate.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase)))
                {
                    return Failure("operator_not_in_active_service_" + operatorId, raidSessionId, entries.Length, now);
                }

                if (!inventoryModeService.TryPrepareRaidInventorySnapshot(
                        entry.ProfileDescriptorJson,
                        out VanguardRaidInventoryPreparedSnapshot? runtimeSnapshot,
                        out string snapshotReason)
                    || runtimeSnapshot is null)
                {
                    return Failure("snapshot_invalid_" + operatorId + "_" + snapshotReason, raidSessionId, entries.Length, now);
                }

                if (entry.ClientItemCount > 0 && runtimeSnapshot.SnapshotItemCount != entry.ClientItemCount)
                {
                    return Failure("snapshot_item_count_mismatch_" + operatorId, raidSessionId, entries.Length, now);
                }

                if (!inventoryModeService.TryPrepareRaidSkillSnapshot(
                        entry.ProfileDescriptorJson,
                        out VanguardRaidSkillPreparedSnapshot? skillSnapshot,
                        out string skillSnapshotReason)
                    || skillSnapshot is null)
                {
                    return Failure("skill_snapshot_invalid_" + operatorId + "_" + skillSnapshotReason, raidSessionId, entries.Length, now);
                }

                // Career truth probing is deliberately observational: probe failure never rejects the authoritative
                // persistence transaction and the probe never mutates Career state.
                VanguardOperatorCareerTruthProbe careerTruthProbe = careerTruthProbeService.Probe(
                    entry.ProfileDescriptorJson,
                    persistentOperator,
                    entry.Died,
                    raidSessionId,
                    operatorId,
                    entry.StatisticsManagerType);

                // For dead Operators the client proves its descriptor came from the residual
                // corpse Equipment tree.  A mismatch means allowing the transfer could later
                // resurrect gear, so the entire cross-Operator batch is rejected.
                if (entry.Died)
                {
                    string[] corpseIds = (entry.CorpseEquipmentItemIds ?? Array.Empty<string>())
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Select(id => id.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (entry.CorpseEquipmentItemCount < 0
                        || corpseIds.Length != entry.CorpseEquipmentItemCount
                        || runtimeSnapshot.EquipmentItemCount != corpseIds.Length
                        || !runtimeSnapshot.EquipmentItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(corpseIds))
                    {
                        return Failure("dead_operator_corpse_snapshot_mismatch_" + operatorId, raidSessionId, entries.Length, now);
                    }
                }

                // First validate the unmodified runtime trees.  A legal transfer removes an item
                // from the donor corpse before it appears on the receiver.  Seeing the same ItemId
                // in two final runtime Operator trees is therefore a transaction/replication fault,
                // even if the donor is about to lose that item under the KIA policy.
                foreach (string itemId in runtimeSnapshot.EquipmentItemIds)
                {
                    if (runtimeItemOwners.TryGetValue(itemId, out string? existingRuntimeOwner))
                    {
                        return Failure("cross_operator_runtime_duplicate_item_" + itemId + "_" + existingRuntimeOwner + "_" + operatorKey, raidSessionId, entries.Length, now);
                    }

                    runtimeItemOwners[itemId] = operatorKey;
                }

                VanguardRaidInventoryPreparedSnapshot persistenceSnapshot = runtimeSnapshot;
                string effectiveSnapshotSource = Normalize(entry.SnapshotSource, "runtime_profile_descriptor");
                if (entry.Died)
                {
                    // SPT does not persist a dead PMC's residual corpse as survivor equipment.
                    // After the corpse has been reconciled exactly, apply SPT LostOnDeathConfig to
                    // derive the distinct persistence tree.  Recovered items have already left the
                    // corpse and live in the receiver's runtime tree; unrecovered lost-on-death gear
                    // is deliberately absent from the dead Operator's next-raid inventory.
                    if (!inventoryModeService.TryPrepareKiaRaidInventorySnapshot(
                            runtimeSnapshot,
                            out VanguardRaidInventoryPreparedSnapshot? kiaSnapshot,
                            out string kiaReason)
                        || kiaSnapshot is null)
                    {
                        return Failure("kia_snapshot_invalid_" + operatorId + "_" + kiaReason, raidSessionId, entries.Length, now);
                    }

                    persistenceSnapshot = kiaSnapshot;
                    effectiveSnapshotSource = "runtime_corpse_reconciled_spt_lost_on_death";
                }

                foreach (string itemId in persistenceSnapshot.EquipmentItemIds)
                {
                    if (globalItemOwners.TryGetValue(itemId, out string? existingOwner))
                    {
                        return Failure("cross_operator_duplicate_item_" + itemId + "_" + existingOwner + "_" + operatorKey, raidSessionId, entries.Length, now);
                    }

                    globalItemOwners[itemId] = operatorKey;
                }

                prepared.Add(new PreparedEntry(entry, storageProfileId, persistentOperator, persistenceSnapshot, skillSnapshot, effectiveSnapshotSource, careerTruthProbe));
            }

            foreach (string owner in prepared.Select(entry => entry.StorageProfileId).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                VanguardOperatorCareerXpSyncResult xpPreflight = await careerXpCommitService.SynchronizeAsync(owner);
                if (!xpPreflight.Success)
                {
                    logger.Warning(VanguardServerDiagnosticsLog.Present($"[{VanguardOperatorCareerXpCommitService.StatusTag}] phase=raid_preflight; owner={owner}; success=false; reason={xpPreflight.Reason}; persistenceWriteStarted=false; currentRaidXpMutation=false; failClosedBeforeMutableRaidCommit=true; tag={VanguardOperatorCareerXpCommitService.StatusTag}"));
                    return Failure("career_xp_preflight_failed_" + Normalize(xpPreflight.Reason, "unknown"), raidSessionId, entries.Length, now);
                }
            }

            VanguardCareerRaidLedgerPreparedBatch preparedCareerLedger;
            try
            {
                VanguardCareerRaidLedgerOperatorTruth[] careerParticipants = prepared
                    .Select(entry => new VanguardCareerRaidLedgerOperatorTruth(
                        entry.StorageProfileId,
                        Normalize(entry.Request.OperatorId),
                        Normalize(entry.Request.BotProfileId),
                        entry.Request.Died,
                        entry.CareerTruthProbe))
                    .ToArray();
                preparedCareerLedger = await careerLedgerService.PrepareAsync(
                    raidSessionId,
                    request.CareerLedger,
                    careerParticipants,
                    now);
            }
            catch (Exception careerPreflightException)
            {
                preparedCareerLedger = new VanguardCareerRaidLedgerPreparedBatch(
                    raidSessionId,
                    false,
                    "career_ledger_preflight_exception_" + careerPreflightException.GetType().Name,
                    Array.Empty<VanguardCareerRaidLedgerPreparedOwner>(),
                    0,
                    0,
                    prepared.Count,
                    request.CareerLedger?.KillEvents?.Count ?? 0);
                logger.Warning(VanguardServerDiagnosticsLog.Present($"[{VanguardCareerRaidLedgerCommitService.StatusTag}] phase=preflight_exception; raid={raidSessionId}; type={careerPreflightException.GetType().Name}; message={careerPreflightException.Message}; persistenceFailOpen=true; durableCareerMutation=false; aggregateMutation=false; xpMutation=false; tag={VanguardCareerRaidLedgerCommitService.StatusTag}"));
            }

            bool hasExactXpCreditToPersist = request.CareerLedger?.XpKillCreditEvents?.Any(credit =>
                credit.CalculationAvailable
                && credit.Awarded
                && !credit.SameGroup
                && credit.KillXpSubtotal > 0) == true;
            if (hasExactXpCreditToPersist && !preparedCareerLedger.Admitted)
            {
                logger.Warning(VanguardServerDiagnosticsLog.Present($"[{VanguardOperatorCareerXpCommitService.StatusTag}] phase=ledger_preflight; raid={raidSessionId}; exactXpCreditRequested=true; admitted=false; reason={Normalize(preparedCareerLedger.Reason, "unknown")}; persistenceWriteStarted=false; failClosedBeforeMutableRaidCommit=true; tag={VanguardOperatorCareerXpCommitService.StatusTag}"));
                return Failure("career_xp_ledger_preflight_not_admitted_" + Normalize(preparedCareerLedger.Reason, "unknown"), raidSessionId, entries.Length, now);
            }

            // Capture all mutable persistent state before the first write.  Rollback is batch-wide:
            // cross-Operator loot is not durable unless both the donor residual tree and receiver
            // final tree can be committed and read back coherently.
            var ownerBackups = new Dictionary<string, OwnerStateBackup>(StringComparer.OrdinalIgnoreCase);
            var inventoryBackups = new Dictionary<string, FileBackup>(StringComparer.OrdinalIgnoreCase);
            foreach (PreparedEntry entry in prepared)
            {
                if (!ownerBackups.ContainsKey(entry.StorageProfileId))
                {
                    ownerBackups[entry.StorageProfileId] = new OwnerStateBackup(
                        await store.LoadActiveServiceAsync(entry.StorageProfileId),
                        await store.LoadMedicalAsync(entry.StorageProfileId),
                        await store.LoadOperatorsAsync(entry.StorageProfileId));
                }

                string path = store.GetOperatorInventoryProfilePath(entry.StorageProfileId, Normalize(entry.Request.OperatorId));
                if (!inventoryBackups.ContainsKey(path))
                {
                    inventoryBackups[path] = FileBackup.Capture(path);
                }
            }

            var responses = new List<VanguardRaidOperatorPersistenceEntryResponse>(prepared.Count);
            var operatorStateMutatedWithinBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            VanguardCareerRaidLedgerCommitResult careerLedgerResult = new(
                "skipped",
                preparedCareerLedger.Admitted,
                false,
                false,
                0,
                preparedCareerLedger.ExistingEntryCount,
                preparedCareerLedger.Owners.Count,
                preparedCareerLedger.Reason);
            bool careerLedgerCommitStarted = false;
            var raidSalaryInvoices = new List<VanguardRaidSalaryInvoiceEnsureResult>(prepared.Count);
            try
            {
                foreach (PreparedEntry entry in prepared)
                {
                    string operatorId = Normalize(entry.Request.OperatorId);
                    VanguardRaidInventoryCommitResult commit = await inventoryModeService.CommitRaidInventorySnapshotAsync(
                        entry.StorageProfileId,
                        operatorId,
                        entry.Snapshot);
                    if (!commit.Success)
                    {
                        throw new InventoryCommitFailureException(
                            operatorId,
                            entry.Request.Died,
                            entry.EffectiveSnapshotSource,
                            entry.CareerTruthProbe,
                            commit);
                    }

                    VanguardRaidSkillCommitResult skillCommit = await inventoryModeService.CommitRaidSkillSnapshotAsync(
                        entry.StorageProfileId,
                        operatorId,
                        entry.SkillSnapshot);
                    if (!skillCommit.Success)
                    {
                        throw new SkillCommitFailureException(
                            operatorId,
                            entry.Request.Died,
                            entry.EffectiveSnapshotSource,
                            entry.CareerTruthProbe,
                            commit,
                            skillCommit);
                    }

                    responses.Add(new VanguardRaidOperatorPersistenceEntryResponse(
                        operatorId,
                        entry.StorageProfileId,
                        entry.Request.Died,
                        true,
                        entry.Request.Died ? "kia_inventory_committed_readback_verified" : "inventory_committed_readback_verified",
                        commit.EquipmentItemCount,
                        commit.EquipmentFingerprint,
                        entry.EffectiveSnapshotSource,
                        entry.CareerTruthProbe,
                        skillCommit));
                }

                foreach (IGrouping<string, PreparedEntry> ownerGroup in prepared.GroupBy(entry => entry.StorageProfileId, StringComparer.OrdinalIgnoreCase))
                {
                    string owner = ownerGroup.Key;
                    OwnerStateBackup backup = ownerBackups[owner];
                    var active = backup.ActiveService.ToArray();
                    var medical = backup.Medical.ToList();

                    foreach (PreparedEntry entry in ownerGroup)
                    {
                        string operatorId = Normalize(entry.Request.OperatorId);
                        double healthRatio = entry.Request.Died ? 0.0 : Math.Clamp(entry.Request.HealthRatio, 0.0, 1.0);
                        VanguardOperatorMedicalRecord nextMedical = VanguardOperatorMedicalRecoveryService.CreateRecoveryRecordFromRaidDamage(
                            entry.PersistentOperator,
                            healthRatio,
                            entry.Request.Died,
                            now);

                        int medicalIndex = medical.FindIndex(item => string.Equals(item.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
                        if (medicalIndex >= 0)
                        {
                            medical[medicalIndex] = nextMedical;
                        }
                        else
                        {
                            medical.Add(nextMedical);
                        }

                        for (int index = 0; index < active.Length; index++)
                        {
                            if (!string.Equals(active[index].OperatorId, operatorId, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            active[index] = active[index] with
                            {
                                Status = nextMedical.RecoveryUntilUtc is null
                                    ? VanguardOperatorServiceStatuses.ActiveService
                                    : VanguardOperatorServiceStatuses.Recovering,
                                IsDeployed = false,
                                LastRaidAtUtc = now,
                                RecoveryUntilUtc = nextMedical.RecoveryUntilUtc
                            };
                        }
                    }

                    await store.SaveMedicalAsync(owner, medical);
                    await store.SaveActiveServiceAsync(owner, active);
                }

                int careerXpAppliedCredits = 0;
                int careerXpDeferredCredits = 0;
                long careerXpCommitted = 0;
                int careerLevelUps = 0;
                if (preparedCareerLedger.Admitted)
                {
                    careerLedgerCommitStarted = true;
                    careerLedgerResult = await careerLedgerService.CommitAsync(preparedCareerLedger);
                    if (!careerLedgerResult.Committed)
                    {
                        throw new InvalidOperationException("career_ledger_commit_not_committed_" + Normalize(careerLedgerResult.Reason, "unknown"));
                    }

                    foreach (string owner in preparedCareerLedger.Owners.Select(value => value.OwnerProfileId).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        VanguardOperatorCareerXpSyncResult xpCommit = await careerXpCommitService.SynchronizeAsync(owner);
                        if (!xpCommit.Success)
                        {
                            throw new InvalidOperationException("career_xp_commit_failed_" + Normalize(xpCommit.Reason, "unknown"));
                        }

                        if (xpCommit.Changed)
                        {
                            operatorStateMutatedWithinBatch.Add(owner);
                        }

                        careerXpAppliedCredits += xpCommit.AppliedCreditCount;
                        careerXpDeferredCredits += xpCommit.DeferredCreditCount;
                        careerXpCommitted += xpCommit.CommittedExperience;
                        careerLevelUps += xpCommit.LevelUps;
                    }
                }

                foreach (PreparedEntry entry in prepared)
                {
                    if (entry.PersistentOperator.SalaryPerRaid <= 0)
                    {
                        continue;
                    }

                    VanguardRaidSalaryInvoiceEnsureResult salaryInvoice = await billingService.EnsureRaidSalaryInvoiceAsync(
                        entry.StorageProfileId,
                        raidSessionId,
                        Normalize(entry.Request.OperatorId),
                        Normalize(entry.PersistentOperator.Identity.DisplayName, Normalize(entry.Request.OperatorId)),
                        entry.PersistentOperator.SalaryPerRaid,
                        entry.PersistentOperator.CurrencyTpl);
                    raidSalaryInvoices.Add(salaryInvoice);
                }

                var success = new VanguardRaidOperatorPersistenceBatchResponse(
                    true,
                    "raid_operator_batch_committed",
                    raidSessionId,
                    entries.Length,
                    responses.Count,
                    false,
                    false,
                    responses,
                    now,
                    VanguardBuildVersion.BuildLabel,
                    careerLedgerResult);
                await SaveCommittedBatchAsync(batchKey, success);
                committedBatches[batchKey] = success;
                try
                {
                    logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_RAID_PERSISTENCE_STATUS] commit_success raid={raidSessionId}; requester={requesterSessionId}; authority={Normalize(request.AuthorityKind, "unknown")}; operators={entries.Length}; owners={prepared.Select(x => x.StorageProfileId).Distinct(StringComparer.OrdinalIgnoreCase).Count()}; runtimeUniqueItems={runtimeItemOwners.Count}; persistentUniqueItems={globalItemOwners.Count}; skillCommon={responses.Sum(x => x.SkillProgression?.CommonSkillCount ?? 0)}; skillCommonProgressed={responses.Sum(x => x.SkillProgression?.CommonProgressedCount ?? 0)}; skillMastering={responses.Sum(x => x.SkillProgression?.MasteringSkillCount ?? 0)}; skillMasteringProgressed={responses.Sum(x => x.SkillProgression?.MasteringProgressedCount ?? 0)}; skillReadback=true; kia={prepared.Count(x => x.Request.Died)}; idempotency={batchKey}; durableLedger=true; careerLedgerAdmitted={Bool(careerLedgerResult.Admitted)}; careerLedgerCommitted={Bool(careerLedgerResult.Committed)}; careerLedgerReplay={Bool(careerLedgerResult.IdempotentReplay)}; careerLedgerAdded={careerLedgerResult.AddedEntryCount}; careerLedgerExisting={careerLedgerResult.ExistingEntryCount}; careerAggregateMutation=false; careerXpCommitActive=true; careerXpAppliedCredits={careerXpAppliedCredits}; careerXpDeferredCredits={careerXpDeferredCredits}; careerXpCommitted={careerXpCommitted}; careerLevelUps={careerLevelUps}; careerXpCoverage={VanguardOperatorCareerXpCommitPolicy.CoverageBoundary}; raidSalaryInvoices={raidSalaryInvoices.Count}; raidSalaryCreated={raidSalaryInvoices.Count(value => value.InvoiceCreated)}; raidSalaryReused={raidSalaryInvoices.Count(value => !value.InvoiceCreated)}; raidSalaryAmount={raidSalaryInvoices.Sum(value => value.Invoice.Amount)}; totalSessionExperienceClaimed=false; rollback=false; readback=true"));
                }
                catch
                {
                    // The durable idempotency marker is the transaction boundary. Logging must never
                    // turn a successfully committed/read-back batch into a rollback with a stale success ledger.
                }
                return success;
            }
            catch (Exception exception)
            {
                bool rollbackSucceeded = true;
                var rollbackFailures = new List<string>();

                foreach (VanguardRaidSalaryInvoiceEnsureResult salaryInvoice in raidSalaryInvoices.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (!await billingService.RollbackRaidSalaryInvoiceAsync(salaryInvoice))
                        {
                            rollbackSucceeded = false;
                            rollbackFailures.Add("raid-salary:" + salaryInvoice.InvoiceId + ":state_changed");
                        }
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackSucceeded = false;
                        rollbackFailures.Add("raid-salary:" + salaryInvoice.InvoiceId + ":" + rollbackException.GetType().Name);
                    }
                }

                if (careerLedgerCommitStarted)
                {
                    try
                    {
                        await careerLedgerService.RollbackAsync(preparedCareerLedger);
                        careerLedgerResult = careerLedgerResult with
                        {
                            Status = "rolled_back",
                            Committed = false,
                            IdempotentReplay = false,
                            Reason = "career_ledger_rolled_back_with_batch"
                        };
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackSucceeded = false;
                        rollbackFailures.Add("career-ledger:" + rollbackException.GetType().Name);
                        careerLedgerResult = careerLedgerResult with
                        {
                            Status = "rollback_failed",
                            Committed = false,
                            IdempotentReplay = false,
                            Reason = "career_ledger_rollback_failed_" + rollbackException.GetType().Name
                        };
                    }
                }
                foreach ((string path, FileBackup backup) in inventoryBackups)
                {
                    try
                    {
                        backup.Restore(path);
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackSucceeded = false;
                        rollbackFailures.Add("inventory:" + Path.GetFileName(path) + ":" + rollbackException.GetType().Name);
                    }
                }

                foreach ((string owner, OwnerStateBackup backup) in ownerBackups)
                {
                    try
                    {
                        await store.SaveMedicalAsync(owner, backup.Medical);
                        await store.SaveActiveServiceAsync(owner, backup.ActiveService);
                        if (operatorStateMutatedWithinBatch.Contains(owner))
                        {
                            await store.SaveOperatorsAtomicAsync(owner, backup.Operators);
                        }
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackSucceeded = false;
                        rollbackFailures.Add("owner:" + owner + ":" + rollbackException.GetType().Name);
                    }
                }

                string rollbackSummary = rollbackFailures.Count == 0 ? "none" : string.Join(",", rollbackFailures);
                string failureDetail = exception is InventoryCommitFailureException inventoryFailure
                    ? "inventory_commit_failed_" + Normalize(inventoryFailure.OperatorId, "unknown") + "_" + Normalize(inventoryFailure.Commit.Reason, "unknown")
                    : exception is SkillCommitFailureException skillFailure
                        ? "skill_commit_failed_" + Normalize(skillFailure.OperatorId, "unknown") + "_" + Normalize(skillFailure.SkillCommit.Reason, "unknown")
                        : exception.GetType().Name;

                var rollbackResponses = responses
                    .Select(response =>
                    {
                        string rollbackReason = "rolled_back_after_" + failureDetail;
                        return response with
                        {
                            Success = false,
                            Reason = rollbackReason,
                            SkillProgression = response.SkillProgression is null
                                ? null
                                : response.SkillProgression with
                                {
                                    Success = false,
                                    Reason = rollbackReason
                                }
                        };
                    })
                    .ToList();

                if (exception is InventoryCommitFailureException failedInventory)
                {
                    rollbackResponses.Add(new VanguardRaidOperatorPersistenceEntryResponse(
                        failedInventory.OperatorId,
                        failedInventory.Commit.StorageProfileId,
                        failedInventory.Died,
                        false,
                        failedInventory.Commit.Reason,
                        failedInventory.Commit.EquipmentItemCount,
                        failedInventory.Commit.EquipmentFingerprint,
                        failedInventory.SnapshotSource,
                        failedInventory.CareerTruthProbe));
                }
                else if (exception is SkillCommitFailureException failedSkill)
                {
                    rollbackResponses.Add(new VanguardRaidOperatorPersistenceEntryResponse(
                        failedSkill.OperatorId,
                        failedSkill.InventoryCommit.StorageProfileId,
                        failedSkill.Died,
                        false,
                        failedSkill.SkillCommit.Reason,
                        failedSkill.InventoryCommit.EquipmentItemCount,
                        failedSkill.InventoryCommit.EquipmentFingerprint,
                        failedSkill.SnapshotSource,
                        failedSkill.CareerTruthProbe,
                        failedSkill.SkillCommit));
                }

                logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_RAID_PERSISTENCE_STATUS] commit_failed_rollback raid={raidSessionId}; requester={requesterSessionId}; type={exception.GetType().Name}; failure={failureDetail}; message={exception.Message}; operators={entries.Length}; completedBeforeFailure={responses.Count}; rollback={rollbackSucceeded}; rollbackFailures={rollbackSummary}"));
                return new VanguardRaidOperatorPersistenceBatchResponse(
                    false,
                    (rollbackSucceeded ? "batch_commit_failed_rollback_" : "batch_commit_failed_rollback_incomplete_") + failureDetail,
                    raidSessionId,
                    entries.Length,
                    0,
                    false,
                    rollbackSucceeded,
                    rollbackResponses,
                    now,
                    VanguardBuildVersion.BuildLabel,
                    careerLedgerResult);
            }
        }
        finally
        {
            CommitGate.Release();
        }
    }

    private async Task<VanguardRaidOperatorPersistenceBatchResponse?> TryLoadCommittedBatchAsync(string batchKey)
    {
        string path = GetCommittedBatchPath(batchKey);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = await File.ReadAllTextAsync(path);
            VanguardRaidOperatorPersistenceBatchResponse? response = JsonSerializer.Deserialize<VanguardRaidOperatorPersistenceBatchResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return response?.Success == true ? response : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Move(path, path + ".invalid-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: false);
            }
            catch
            {
            }
            logger.Warning(VanguardServerDiagnosticsLog.Present($"[VANGUARD_RAID_PERSISTENCE_STATUS] durable_ledger_invalid path={path}; type={exception.GetType().Name}; ignored=true"));
            return null;
        }
    }

    private async Task SaveCommittedBatchAsync(string batchKey, VanguardRaidOperatorPersistenceBatchResponse response)
    {
        string path = GetCommittedBatchPath(batchKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        string temporary = path + ".vanguard-write-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private string GetCommittedBatchPath(string batchKey)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(batchKey));
        string keyHash = Convert.ToHexString(hash);
        return Path.Combine(store.RootDirectory, "raid-persistence-commits", keyHash + ".json");
    }

    private static VanguardRaidOperatorPersistenceBatchResponse Failure(string reason, string raidSessionId, int requestedCount, DateTimeOffset now)
        => new(false, reason, raidSessionId, requestedCount, 0, false, false, Array.Empty<VanguardRaidOperatorPersistenceEntryResponse>(), now, VanguardBuildVersion.BuildLabel);

    private static string BuildBatchKey(string raidSessionId, IEnumerable<VanguardRaidOperatorPersistenceEntryRequest> entries)
    {
        string lineage = string.Join("|", entries
            .Select(entry => Normalize(entry.OwnerProfileId) + ":" + Normalize(entry.OperatorId))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        return raidSessionId + "|" + lineage;
    }

    private static string Normalize(string? value, string fallback = "")
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Bool(bool value) => value ? "true" : "false";

    private sealed class InventoryCommitFailureException(
        string operatorId,
        bool died,
        string snapshotSource,
        VanguardOperatorCareerTruthProbe careerTruthProbe,
        VanguardRaidInventoryCommitResult commit)
        : InvalidOperationException("inventory_commit_failed_" + operatorId + "_" + commit.Reason)
    {
        public string OperatorId { get; } = operatorId;
        public bool Died { get; } = died;
        public string SnapshotSource { get; } = snapshotSource;
        public VanguardOperatorCareerTruthProbe CareerTruthProbe { get; } = careerTruthProbe;
        public VanguardRaidInventoryCommitResult Commit { get; } = commit;
    }

    private sealed class SkillCommitFailureException(
        string operatorId,
        bool died,
        string snapshotSource,
        VanguardOperatorCareerTruthProbe careerTruthProbe,
        VanguardRaidInventoryCommitResult inventoryCommit,
        VanguardRaidSkillCommitResult skillCommit)
        : InvalidOperationException("skill_commit_failed_" + operatorId + "_" + skillCommit.Reason)
    {
        public string OperatorId { get; } = operatorId;
        public bool Died { get; } = died;
        public string SnapshotSource { get; } = snapshotSource;
        public VanguardOperatorCareerTruthProbe CareerTruthProbe { get; } = careerTruthProbe;
        public VanguardRaidInventoryCommitResult InventoryCommit { get; } = inventoryCommit;
        public VanguardRaidSkillCommitResult SkillCommit { get; } = skillCommit;
    }

    private sealed record PreparedEntry(
        VanguardRaidOperatorPersistenceEntryRequest Request,
        string StorageProfileId,
        VanguardOperatorProfile PersistentOperator,
        VanguardRaidInventoryPreparedSnapshot Snapshot,
        VanguardRaidSkillPreparedSnapshot SkillSnapshot,
        string EffectiveSnapshotSource,
        VanguardOperatorCareerTruthProbe CareerTruthProbe);

    private sealed record OwnerStateBackup(
        IReadOnlyList<VanguardActiveServiceRecord> ActiveService,
        IReadOnlyList<VanguardOperatorMedicalRecord> Medical,
        IReadOnlyList<VanguardOperatorProfile> Operators);

    private sealed record FileBackup(bool Existed, byte[] Bytes)
    {
        public static FileBackup Capture(string path)
            => File.Exists(path) ? new FileBackup(true, File.ReadAllBytes(path)) : new FileBackup(false, Array.Empty<byte>());

        public void Restore(string path)
        {
            if (!Existed)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".vanguard-rollback-" + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, Bytes);
            File.Move(temporary, path, overwrite: true);
        }
    }
}
