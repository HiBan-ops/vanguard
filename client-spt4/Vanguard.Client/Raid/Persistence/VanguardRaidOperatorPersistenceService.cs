using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vanguard.Client.Api;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;
using Vanguard.Client.Raid.Career;
using Vanguard.Client.Raid.Hud;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Loot;

#if SPT_CLIENT
using EFT;
using EFT.InventoryLogic;
#endif

// Responsibility: Builds the client end-of-raid persistence request from final Operator truth, including exact-incarnation snapshots captured before a living runtime presence is definitively removed.
// Flow: Living definitive despawn seals capture an immutable local snapshot only; the raid-stop boundary later resolves live, corpse, or exact detached-living truth and submits one cross-Operator server transaction.
// Authority boundary: Mid-raid capture is read-only and never durable; the server remains the only durable Operator/Career persistence authority and is contacted only by the raid-end commit path.
// Invariant: Detached-living truth must match raid + persistent identities + RuntimeIncarnation exactly, death keeps corpse semantics, and no presence-loss callback may create a second durable transaction.
namespace Vanguard.Client.Raid.Persistence;

internal static class VanguardRaidOperatorPersistenceService
{
    public const string StatusTag = "VANGUARD_OPERATOR_POSTRAID_PERSISTENCE_STATUS";
    private static readonly object Sync = new();
    private static string armedRaidSessionId = string.Empty;
    private static bool armed;
    private static bool finalCommitAttempted;
    private static readonly Dictionary<string, DetachedLivingPersistenceSnapshot> DetachedLivingSnapshotsByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> DetachedLivingSnapshotFailuresByBotProfileId = new(StringComparer.OrdinalIgnoreCase);

    private sealed record DetachedLivingPersistenceSnapshot(
        string RaidSessionId,
        string OperatorId,
        string OwnerProfileId,
        string BotProfileId,
        long RuntimeIncarnation,
        DateTimeOffset CapturedAtUtc,
        string ProfileDescriptorJson,
        int ClientItemCount,
        int EquipmentItemCount,
        double HealthRatio,
        string StatisticsManagerType,
        string PresenceSource);

    public static bool IsArmedForOperatorCorpseTransactions
    {
        get
        {
            lock (Sync)
            {
                string currentRaid = VanguardRaidOperatorRuntimeRegistry.ActiveRaidSessionId ?? string.Empty;
                return armed
                    && !string.IsNullOrWhiteSpace(currentRaid)
                    && string.Equals(armedRaidSessionId, currentRaid, StringComparison.OrdinalIgnoreCase)
                    && IsPersistenceAuthority;
            }
        }
    }

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            armedRaidSessionId = string.Empty;
            armed = false;
            finalCommitAttempted = false;
            DetachedLivingSnapshotsByBotProfileId.Clear();
            DetachedLivingSnapshotFailuresByBotProfileId.Clear();
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_RAID_PERSISTENCE_RESET reason={Safe(reason)}; armed=false; finalCommit=false; operatorCorpseTransactions=false; detachedLivingSnapshots=cleared");
    }

    public static void ArmFromManifest(VanguardRaidOperatorManifestForProfilesResponseDto response, string source)
    {
        string raidSessionId = response.RaidSessionId ?? string.Empty;
        bool authority = IsPersistenceAuthority;
        bool serverBuildMatches = string.Equals(response.BuildLabel, VanguardBuildVersion.BuildLabel, StringComparison.OrdinalIgnoreCase);
        bool canArm = response.Success
            && authority
            && !string.IsNullOrWhiteSpace(raidSessionId)
            && response.OperatorCount > 0
            && serverBuildMatches;

        lock (Sync)
        {
            armedRaidSessionId = canArm ? raidSessionId : string.Empty;
            armed = canArm;
            finalCommitAttempted = false;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_RAID_PERSISTENCE_ARM source={Safe(source)}; raid={Safe(raidSessionId)}; operators={response.OperatorCount}; authority={Bool(authority)}; serverBuild={Safe(response.BuildLabel)}; clientBuild={Safe(VanguardBuildVersion.BuildLabel)}; buildMatch={Bool(serverBuildMatches)}; armed={Bool(canArm)}; operatorCorpseTransactions={Bool(canArm)}");
    }

#if SPT_CLIENT
    public static string CaptureLivingDefinitivePresenceLossSnapshot(
        VanguardRaidOperatorRuntimeRecord record,
        BotOwner exactBotOwner,
        Player? exactPlayer,
        string source)
    {
        if (exactBotOwner.IsDead)
        {
            return "captured=false;reason=bot_owner_dead;death_semantics_deferred_to_corpse=true";
        }

        string key = record.BotProfileId?.Trim() ?? string.Empty;
        string raidSessionId = VanguardRaidOperatorRuntimeRegistry.ActiveRaidSessionId ?? string.Empty;
        long incarnation = record.RuntimeIncarnation;
        if (string.IsNullOrWhiteSpace(key)
            || string.IsNullOrWhiteSpace(raidSessionId)
            || !record.IsRuntimePresent
            || !ReferenceEquals(record.BotOwner, exactBotOwner))
        {
            return RecordDetachedLivingCaptureFailure(
                key,
                incarnation,
                "exact_runtime_identity_not_present",
                source,
                raidSessionId);
        }

        if (exactPlayer == null || !ReferenceEquals(exactBotOwner.GetPlayer, exactPlayer))
        {
            return RecordDetachedLivingCaptureFailure(
                key,
                incarnation,
                "exact_player_missing_or_changed",
                source,
                raidSessionId);
        }

        if (!TryBuildProfileDescriptorSnapshot(
                exactPlayer,
                out string descriptorJson,
                out int itemCount,
                out HashSet<string> equipmentIds,
                out string descriptorReason))
        {
            return RecordDetachedLivingCaptureFailure(
                key,
                incarnation,
                "descriptor_" + descriptorReason,
                source,
                raidSessionId);
        }

        double healthRatio = ReadHealthRatio(exactPlayer);
        string statisticsManagerType = exactPlayer.StatisticsManager?.GetType().FullName
            ?? exactPlayer.StatisticsManager?.GetType().Name
            ?? "none";
        var snapshot = new DetachedLivingPersistenceSnapshot(
            raidSessionId,
            record.OperatorId,
            record.OwnerProfileId,
            key,
            incarnation,
            DateTimeOffset.UtcNow,
            descriptorJson,
            itemCount,
            equipmentIds.Count,
            healthRatio,
            statisticsManagerType,
            source ?? string.Empty);

        bool idempotent = false;
        long? supersededPriorIncarnation = null;
        lock (Sync)
        {
            if (DetachedLivingSnapshotsByBotProfileId.TryGetValue(key, out DetachedLivingPersistenceSnapshot? existing))
            {
                bool samePersistentIdentity = string.Equals(existing.RaidSessionId, raidSessionId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.OperatorId, record.OperatorId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.OwnerProfileId, record.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.BotProfileId, key, StringComparison.OrdinalIgnoreCase);
                if (!samePersistentIdentity)
                {
                    DetachedLivingSnapshotFailuresByBotProfileId[key] =
                        $"snapshot_persistent_identity_conflict_existing_incarnation_{existing.RuntimeIncarnation}_new_{incarnation}";
                    VanguardClientDiagnosticsLog.Warning(
                        StatusTag,
                        $"VANGUARD_DETACHED_LIVING_SNAPSHOT_REJECTED operator={Safe(record.OperatorId)}; owner={Safe(record.OwnerProfileId)}; bot={Safe(key)}; incarnation={incarnation}; existingIncarnation={existing.RuntimeIncarnation}; raid={Safe(raidSessionId)}; source={Safe(source)}; reason=persistent_identity_or_raid_conflict; persistentMutation=false; serverCommit=false; explicitReplacementOnly=true");
                    return "captured=false;reason=snapshot_persistent_identity_or_raid_conflict;explicitReplacementOnly=true";
                }

                if (existing.RuntimeIncarnation > incarnation)
                {
                    DetachedLivingSnapshotFailuresByBotProfileId[key] =
                        $"snapshot_stale_incarnation_existing_{existing.RuntimeIncarnation}_new_{incarnation}";
                    VanguardClientDiagnosticsLog.Warning(
                        StatusTag,
                        $"VANGUARD_DETACHED_LIVING_SNAPSHOT_REJECTED operator={Safe(record.OperatorId)}; owner={Safe(record.OwnerProfileId)}; bot={Safe(key)}; incarnation={incarnation}; existingIncarnation={existing.RuntimeIncarnation}; raid={Safe(raidSessionId)}; source={Safe(source)}; reason=stale_lower_incarnation; persistentMutation=false; serverCommit=false; explicitReplacementOnly=true");
                    return "captured=false;reason=snapshot_stale_lower_incarnation;explicitReplacementOnly=true";
                }

                if (existing.RuntimeIncarnation == incarnation)
                {
                    idempotent = true;
                }
                else
                {
                    supersededPriorIncarnation = existing.RuntimeIncarnation;
                    DetachedLivingSnapshotsByBotProfileId[key] = snapshot;
                }
            }
            else
            {
                DetachedLivingSnapshotsByBotProfileId[key] = snapshot;
            }

            DetachedLivingSnapshotFailuresByBotProfileId.Remove(key);
        }

        bool healthControllerAlive = exactPlayer.HealthController?.IsAlive == true;
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_DETACHED_LIVING_SNAPSHOT_CAPTURED operator={Safe(record.OperatorId)}; owner={Safe(record.OwnerProfileId)}; bot={Safe(key)}; incarnation={incarnation}; raid={Safe(raidSessionId)}; hp={healthRatio:0.000}; descriptorItems={itemCount}; equipmentItems={equipmentIds.Count}; statisticsManager={Safe(statisticsManagerType)}; healthControllerAlive={Bool(healthControllerAlive)}; botOwnerDead=false; source={Safe(source)}; idempotent={Bool(idempotent)}; supersededPriorIncarnation={(supersededPriorIncarnation?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none")}; persistentMutation=false; serverCommit=false; exactIncarnation=true; explicitReplacementOnly=true; healthAliveFlagIsNotDeathAuthority=true");
        return $"captured=true;incarnation={incarnation};items={itemCount};hp={healthRatio:0.000};idempotent={Bool(idempotent)};supersededPriorIncarnation={(supersededPriorIncarnation?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none")}";
    }

    private static string RecordDetachedLivingCaptureFailure(
        string botProfileId,
        long incarnation,
        string reason,
        string source,
        string raidSessionId)
    {
        string key = botProfileId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(key))
        {
            lock (Sync)
            {
                DetachedLivingSnapshotFailuresByBotProfileId[key] = reason;
            }
        }

        VanguardClientDiagnosticsLog.Warning(
            StatusTag,
            $"VANGUARD_DETACHED_LIVING_SNAPSHOT_REJECTED bot={Safe(key)}; incarnation={incarnation}; raid={Safe(raidSessionId)}; source={Safe(source)}; reason={Safe(reason)}; persistentMutation=false; serverCommit=false; removalBlocked=false");
        return "captured=false;reason=" + Safe(reason) + ";removalBlocked=false";
    }

    public static void CommitAtRaidEnd(string source)
    {
        string raidSessionId;
        string gateDecision;
        bool gateArmed;
        bool gateFinalCommitAttempted;
        bool gateAuthority;
        string gateArmedRaidSessionId;

        lock (Sync)
        {
            gateArmed = armed;
            gateFinalCommitAttempted = finalCommitAttempted;
            gateAuthority = IsPersistenceAuthority;
            gateArmedRaidSessionId = armedRaidSessionId;

            if (!gateArmed)
            {
                raidSessionId = gateArmedRaidSessionId;
                gateDecision = "reject_not_armed";
            }
            else if (gateFinalCommitAttempted)
            {
                raidSessionId = gateArmedRaidSessionId;
                gateDecision = "reject_already_attempted";
            }
            else if (!gateAuthority)
            {
                raidSessionId = gateArmedRaidSessionId;
                gateDecision = "reject_not_persistence_authority";
            }
            else
            {
                raidSessionId = gateArmedRaidSessionId;
                finalCommitAttempted = true;
                gateDecision = "admitted";
            }
        }

        string runtimeRaidSessionId = VanguardRaidOperatorRuntimeRegistry.ActiveRaidSessionId ?? string.Empty;
        int runtimeOperatorCount = VanguardRaidOperatorRuntimeRegistry.GetRuntimeCountForAuthority();
        bool raidIdentityMatches = !string.IsNullOrWhiteSpace(gateArmedRaidSessionId)
            && !string.IsNullOrWhiteSpace(runtimeRaidSessionId)
            && string.Equals(gateArmedRaidSessionId, runtimeRaidSessionId, StringComparison.OrdinalIgnoreCase);

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_RAID_PERSISTENCE_COMMIT_GATE source={Safe(source)}; decision={gateDecision}; armed={Bool(gateArmed)}; finalCommitAttemptedBeforeGate={Bool(gateFinalCommitAttempted)}; authority={Bool(gateAuthority)}; armedRaid={Safe(gateArmedRaidSessionId)}; runtimeRaid={Safe(runtimeRaidSessionId)}; raidIdentityMatches={Bool(raidIdentityMatches)}; runtimeOperators={runtimeOperatorCount}; operatorPostRaidPersistenceEnabled={Bool(VanguardOperatorRuntimeAuditOptions.GetOperatorPostRaidPersistenceEnabled())}");

        if (!string.Equals(gateDecision, "admitted", StringComparison.Ordinal))
        {
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"VANGUARD_RAID_PERSISTENCE_COMMIT_GATE_REJECTED source={Safe(source)}; reason={gateDecision}; armed={Bool(gateArmed)}; finalCommitAttempted={Bool(gateFinalCommitAttempted)}; authority={Bool(gateAuthority)}; armedRaid={Safe(gateArmedRaidSessionId)}; runtimeRaid={Safe(runtimeRaidSessionId)}; runtimeOperators={runtimeOperatorCount}; durableCommitAttempted=false");
            return;
        }

        try
        {
            if (!VanguardOperatorRuntimeAuditOptions.GetOperatorPostRaidPersistenceEnabled())
            {
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"VANGUARD_PERSISTENCE_SKIPPED source={Safe(source)}; raid={Safe(raidSessionId)}; reason=f12_persistence_disabled; durableCommit=false; snapshotsBuilt=false; operatorCorpseTransactionsRemainEnabled=true; inRaidLootSemanticsChanged=false");
                return;
            }

            if (VanguardCorpseLootSessionExecutor.HasInFlightNativeTransaction(out string corpseTransactionSummary))
            {
                FailAttempt("corpse_native_transaction_inflight_" + corpseTransactionSummary, source, raidSessionId);
                return;
            }

            if (VanguardWorldLootContainerSessionExecutor.HasInFlightNativeTransaction(out string containerTransactionSummary))
            {
                FailAttempt("container_native_transaction_inflight_" + containerTransactionSummary, source, raidSessionId);
                return;
            }

            IReadOnlyList<VanguardRaidOperatorRuntimeRecord> records = VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators();
            if (records.Count == 0)
            {
                FailAttempt("runtime_operator_batch_empty", source, raidSessionId);
                return;
            }

            var corpseByVictim = VanguardCorpseRegistry.GetSnapshot(DateTimeOffset.UtcNow)
                .Where(entry => entry.VictimWasOperator)
                .GroupBy(entry => entry.VictimProfileId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.RegisteredAtUtc).First(), StringComparer.OrdinalIgnoreCase);

            var entries = new List<VanguardRaidOperatorPersistenceEntryRequestDto>(records.Count);
            foreach (VanguardRaidOperatorRuntimeRecord record in records.OrderBy(value => value.OwnerProfileId, StringComparer.OrdinalIgnoreCase).ThenBy(value => value.OperatorId, StringComparer.OrdinalIgnoreCase))
            {
                Player? player = record.BotOwner?.GetPlayer;
                corpseByVictim.TryGetValue(record.BotProfileId, out VanguardCorpseRegistryEntry? corpseEntry);
                if (player == null && corpseEntry == null && record.RuntimeDetachedAtUtc.HasValue)
                {
                    if (TryResolveDetachedLivingSnapshot(record, raidSessionId, out DetachedLivingPersistenceSnapshot? detachedSnapshot, out string detachedReason))
                    {
                        entries.Add(new VanguardRaidOperatorPersistenceEntryRequestDto
                        {
                            OperatorId = detachedSnapshot!.OperatorId,
                            OwnerProfileId = detachedSnapshot.OwnerProfileId,
                            BotProfileId = detachedSnapshot.BotProfileId,
                            Died = false,
                            HealthRatio = detachedSnapshot.HealthRatio,
                            ProfileDescriptorJson = detachedSnapshot.ProfileDescriptorJson,
                            SnapshotSource = "detached_living_presence_seal",
                            ClientItemCount = detachedSnapshot.ClientItemCount,
                            CorpseId = null,
                            CorpseEquipmentItemCount = -1,
                            CorpseEquipmentItemIds = null,
                            StatisticsManagerType = detachedSnapshot.StatisticsManagerType
                        });

                        VanguardClientDiagnosticsLog.Info(
                            StatusTag,
                            $"VANGUARD_RAID_OPERATOR_SNAPSHOT operator={Safe(record.OperatorId)}; owner={Safe(record.OwnerProfileId)}; bot={Safe(record.BotProfileId)}; died=false; hp={detachedSnapshot.HealthRatio:0.000}; descriptorItems={detachedSnapshot.ClientItemCount}; equipmentItems={detachedSnapshot.EquipmentItemCount}; corpseEquipmentItems=-1; source=detached_living_presence_seal; presenceSource={Safe(detachedSnapshot.PresenceSource)}; capturedAt={detachedSnapshot.CapturedAtUtc:O}; incarnation={detachedSnapshot.RuntimeIncarnation}; raid={Safe(raidSessionId)}; exactIncarnation=true; serverCommitDeferredUntilRaidEnd=true");
                        continue;
                    }

                    FailAttempt(
                        "operator_player_missing_" + record.OperatorId + "_detached_snapshot_" + detachedReason,
                        source,
                        raidSessionId);
                    return;
                }

                player ??= corpseEntry?.Victim;
                if (player == null)
                {
                    FailAttempt("operator_player_missing_" + record.OperatorId, source, raidSessionId);
                    return;
                }

                bool died = record.BotOwner?.IsDead == true
                    || corpseEntry != null
                    || player.HealthController?.IsAlive == false;
                if (died && corpseEntry == null)
                {
                    FailAttempt("dead_operator_corpse_missing_" + record.OperatorId, source, raidSessionId);
                    return;
                }

                if (!TryBuildProfileDescriptorSnapshot(player, out string descriptorJson, out int itemCount, out HashSet<string> descriptorEquipmentIds, out string descriptorReason))
                {
                    FailAttempt("descriptor_failed_" + record.OperatorId + "_" + descriptorReason, source, raidSessionId);
                    return;
                }

                int corpseEquipmentItemCount = -1;
                List<string>? corpseEquipmentItemIds = null;
                string snapshotSource = "live_operator_profile";
                if (died)
                {
                    if (!TryCaptureCorpseEquipmentIds(corpseEntry!.Corpse, out HashSet<string> corpseEquipmentIds, out string corpseReason))
                    {
                        FailAttempt("corpse_snapshot_failed_" + record.OperatorId + "_" + corpseReason, source, raidSessionId);
                        return;
                    }

                    if (!descriptorEquipmentIds.SetEquals(corpseEquipmentIds))
                    {
                        FailAttempt(
                            $"dead_operator_descriptor_corpse_diverged_{record.OperatorId}_descriptor_{descriptorEquipmentIds.Count}_corpse_{corpseEquipmentIds.Count}",
                            source,
                            raidSessionId);
                        return;
                    }

                    corpseEquipmentItemCount = corpseEquipmentIds.Count;
                    corpseEquipmentItemIds = corpseEquipmentIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
                    snapshotSource = "dead_operator_corpse_reconciled_profile";
                }

                double healthRatio = died ? 0.0 : ReadHealthRatio(player);
                entries.Add(new VanguardRaidOperatorPersistenceEntryRequestDto
                {
                    OperatorId = record.OperatorId,
                    OwnerProfileId = record.OwnerProfileId,
                    BotProfileId = record.BotProfileId,
                    Died = died,
                    HealthRatio = healthRatio,
                    ProfileDescriptorJson = descriptorJson,
                    SnapshotSource = snapshotSource,
                    ClientItemCount = itemCount,
                    CorpseId = corpseEntry?.CorpseId,
                    CorpseEquipmentItemCount = corpseEquipmentItemCount,
                    CorpseEquipmentItemIds = corpseEquipmentItemIds,
                    StatisticsManagerType = player.StatisticsManager?.GetType().FullName
                        ?? player.StatisticsManager?.GetType().Name
                        ?? "none"
                });

                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"VANGUARD_RAID_OPERATOR_SNAPSHOT operator={Safe(record.OperatorId)}; owner={Safe(record.OwnerProfileId)}; bot={Safe(record.BotProfileId)}; died={Bool(died)}; hp={healthRatio:0.000}; descriptorItems={itemCount}; equipmentItems={descriptorEquipmentIds.Count}; corpseEquipmentItems={corpseEquipmentItemCount}; source={snapshotSource}; statisticsManager={Safe(player.StatisticsManager?.GetType().FullName ?? player.StatisticsManager?.GetType().Name)}; raid={Safe(raidSessionId)}");
            }

            VanguardCareerRaidLedgerCommitRequestDto? careerLedger = null;
            if (!VanguardCareerEventTruthProbeService.TryBuildLedgerCommitRequest(raidSessionId, out careerLedger, out string careerLedgerReason))
            {
                VanguardClientDiagnosticsLog.Warning(
                    VanguardCareerEventTruthProbeService.LedgerStatusTag,
                    $"VANGUARD_LEDGER_CLIENT_PREFLIGHT_SKIPPED raid={Safe(raidSessionId)}; reason={Safe(careerLedgerReason)}; persistenceFailOpen=true; serverLedgerAdmission=false; aggregateMutation=false; xpMutation=false");
            }

            var request = new VanguardRaidOperatorPersistenceBatchRequestDto
            {
                RaidSessionId = raidSessionId,
                Operators = entries,
                AuthorityKind = ResolveAuthorityKind(),
                ClientBuild = VanguardBuildVersion.Value,
                ClientLabel = VanguardBuildVersion.BuildLabel,
                CareerLedger = careerLedger
            };
            VanguardRaidOperatorPersistenceBatchResponseDto response = new VanguardApiClient().CommitRaidOperatorPersistence(request);
            EmitCareerTruthProbes(response.Operators, raidSessionId);
            EmitSkillProgressionCommits(response.Operators, raidSessionId);
            EmitCareerLedgerCommit(response.CareerLedger, raidSessionId);
            if (!response.Success)
            {
                VanguardClientDiagnosticsLog.Warning(StatusTag,
                    $"VANGUARD_RAID_PERSISTENCE_COMMIT_FAILED source={Safe(source)}; raid={Safe(raidSessionId)}; operators={entries.Count}; reason={Safe(response.Reason)}; rollback={Bool(response.RolledBack)}; replay={Bool(response.IdempotentReplay)}; entryResults={BuildCommitFailureSummary(response.Operators)}; operatorCorpseTransactionsWereArmed=true");
                return;
            }

            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_RAID_PERSISTENCE_COMMIT_OK source={Safe(source)}; raid={Safe(raidSessionId)}; requested={response.RequestedOperatorCount}; committed={response.CommittedOperatorCount}; replay={Bool(response.IdempotentReplay)}; rollback={Bool(response.RolledBack)}; readback=true; crossOperatorBatch=true; operatorCorpseRecoveryPersistent=true");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(StatusTag,
                $"VANGUARD_RAID_PERSISTENCE_EXCEPTION source={Safe(source)}; raid={Safe(raidSessionId)}; type={Safe(exception.GetType().Name)}; message={Safe(exception.Message)}; failClosedPersistence=true");
        }
    }


    private static void EmitSkillProgressionCommits(
        IReadOnlyList<VanguardRaidOperatorPersistenceEntryResponseDto>? operators,
        string raidSessionId)
    {
        if (operators == null || operators.Count == 0)
        {
            return;
        }

        foreach (VanguardRaidOperatorPersistenceEntryResponseDto entry in operators)
        {
            VanguardRaidSkillCommitResultDto? result = entry.SkillProgression;
            if (result == null)
            {
                continue;
            }

            VanguardClientDiagnosticsLog.Operational(
                VanguardBuildVersion.OperatorSkillAndMasteryPersistenceStatusTag,
                () => $"VANGUARD_SKILL_PERSISTENCE operator={Safe(entry.OperatorId)}; raid={Safe(raidSessionId)}; success={Bool(result.Success)}; reason={Safe(result.Reason)}; common={result.CommonSkillCount}; commonProgressed={result.CommonProgressedCount}; commonDelta={InvariantDouble(result.CommonProgressDelta, "0.####")}; mastering={result.MasteringSkillCount}; masteringProgressed={result.MasteringProgressedCount}; masteringDelta={InvariantDouble(result.MasteringProgressDelta, "0.####")}; sessionPointsReset=true; forwardOnly=true; runtimeFingerprint={Safe(result.RuntimeFingerprint)}; persistentFingerprint={Safe(result.PersistentFingerprint)}; readback={Bool(result.Success)}; generalCareerXpSynthesis=false; personaMutation=false; sainProjectionChanged=false");
        }
    }

    private static void EmitCareerLedgerCommit(
        VanguardCareerRaidLedgerCommitResponseDto? result,
        string raidSessionId)
    {
        if (result == null)
        {
            VanguardClientDiagnosticsLog.Operational(
                VanguardCareerEventTruthProbeService.LedgerStatusTag,
                () => $"VANGUARD_LEDGER_SERVER_RESULT_MISSING raid={Safe(raidSessionId)}; admitted=false; committed=false; persistenceSemanticsChanged=false; aggregateMutation=false; xpMutation=false");
            return;
        }

        VanguardClientDiagnosticsLog.Operational(
            VanguardCareerEventTruthProbeService.LedgerStatusTag,
            () => $"VANGUARD_LEDGER_SERVER_RESULT raid={Safe(raidSessionId)}; status={Safe(result.Status)}; admitted={Bool(result.Admitted)}; committed={Bool(result.Committed)}; replay={Bool(result.IdempotentReplay)}; added={result.AddedEntryCount}; existing={result.ExistingEntryCount}; owners={result.OwnerCount}; reason={Safe(result.Reason)}; schema={result.SchemaVersion}; aggregateMutation=false; xpMutation=false");
    }


    private static void EmitCareerTruthProbes(
        IReadOnlyList<VanguardRaidOperatorPersistenceEntryResponseDto>? operators,
        string raidSessionId)
    {
        if (operators == null || operators.Count == 0)
        {
            VanguardClientDiagnosticsLog.Operational(
                VanguardBuildVersion.CareerTruthProbeStatusTag,
                () => $"VANGUARD_CAREER_TRUTH_PROBE_NONE raid={Safe(raidSessionId)}; reason=response_has_no_operator_probe; gate=A_read_only; careerMutation=false; persistenceSemanticsChanged=false");
            return;
        }

        foreach (VanguardRaidOperatorPersistenceEntryResponseDto entry in operators)
        {
            VanguardOperatorCareerTruthProbeDto? probe = entry.CareerTruthProbe;
            if (probe == null)
            {
                VanguardClientDiagnosticsLog.Operational(
                    VanguardBuildVersion.CareerTruthProbeStatusTag,
                    () => $"VANGUARD_CAREER_TRUTH_PROBE_MISSING operator={Safe(entry.OperatorId)}; raid={Safe(raidSessionId)}; gate=A_read_only; careerMutation=false; persistenceSemanticsChanged=false");
                continue;
            }

            VanguardClientDiagnosticsLog.Operational(
                VanguardBuildVersion.CareerTruthProbeStatusTag,
                () => $"VANGUARD_CAREER_TRUTH_XP operator={Safe(entry.OperatorId)}; raid={Safe(raidSessionId)}; parsed={Bool(probe.DescriptorParsed)}; reason={Safe(probe.DescriptorReason)}; persistentLevel={probe.PersistentLevelBefore}; persistentXp={probe.PersistentExperienceBefore}; infoPresent={Bool(probe.InfoPresent)}; descriptorLevel={probe.DescriptorReportedLevel}; descriptorXp={probe.DescriptorExperience}; descriptorXpDelta={probe.DescriptorExperienceDeltaFromPersistent}; curveResolvedLevel={probe.ExperienceCurveResolvedLevel}; curveAuthoritative={Bool(probe.ExperienceCurveAuthoritative)}; curveSource={Safe(probe.ExperienceCurveSource)}; levelCoherent={Bool(probe.ExperienceLevelCoherent)}; descriptorXpSemantics={Safe(probe.DescriptorExperienceSemantics)}; descriptorXpCareerAuthority={Bool(probe.DescriptorExperienceIsCareerAuthority)}; statisticsManager={Safe(probe.StatisticsManagerType)}; nativeSessionXpAuthority={Safe(probe.NativeSessionExperienceAuthorityState)}; nativeSessionXpAvailable={Bool(probe.NativeSessionExperienceAuthorityAvailable)}; gate=A_read_only; careerMutation=false");

            VanguardClientDiagnosticsLog.Operational(
                VanguardBuildVersion.CareerTruthProbeStatusTag,
                () => $"VANGUARD_CAREER_TRUTH_STATS operator={Safe(entry.OperatorId)}; statsEftPresent={Bool(probe.StatsEftPresent)}; sessionState={Safe(probe.SessionCountersState)}; sessionItems={probe.SessionCounterItemCount}; sessionNonZero={probe.SessionCounterNonZeroCount}; sessionKills={NullableLong(probe.SessionKills)}; sessionDeaths={NullableLong(probe.SessionDeaths)}; sessionExpKill={NullableLong(probe.SessionExpKill)}; sessionExpExitStatus={NullableLong(probe.SessionExpExitStatus)}; totalSessionExperience={probe.TotalSessionExperience}; overallState={Safe(probe.OverallCountersState)}; overallItems={probe.OverallCounterItemCount}; overallNonZero={probe.OverallCounterNonZeroCount}; victimsState={Safe(probe.VictimsState)}; victims={probe.VictimCount}; died={Bool(probe.DiedRuntimeTruth)}; diedSource={Safe(probe.DiedTruthSource)}; exitStatusState={Safe(probe.ExitStatusState)}; exitStatusValue={Safe(probe.ExitStatusValue)}; raidOutcome={Safe(probe.RaidOutcomeState)}; deathCause={Safe(probe.DeathCauseState)}:{Safe(probe.DeathCauseDamageType)}:{Safe(probe.DeathCauseSide)}:{Safe(probe.DeathCauseRole)}:{Safe(probe.DeathCauseWeaponId)}; aggressor={Safe(probe.AggressorState)}:{Safe(probe.AggressorProfileId)}:{Safe(probe.AggressorAccountId)}:{Safe(probe.AggressorName)}:{Safe(probe.AggressorSide)}:{Safe(probe.AggressorRole)}");

            List<VanguardOperatorCareerTruthVictimDto> victims = probe.Victims ?? new List<VanguardOperatorCareerTruthVictimDto>();
            for (int index = 0; index < victims.Count; index++)
            {
                VanguardOperatorCareerTruthVictimDto victim = victims[index];
                int victimIndex = index;
                VanguardClientDiagnosticsLog.Operational(
                    VanguardBuildVersion.CareerTruthProbeStatusTag,
                    () => $"VANGUARD_CAREER_TRUTH_VICTIM operator={Safe(entry.OperatorId)}; index={victimIndex}; profile={Safe(victim.ProfileId)}; account={Safe(victim.AccountId)}; name={Safe(victim.Name)}; side={Safe(victim.Side)}; level={victim.Level}; role={Safe(victim.Role)}; weapon={Safe(victim.Weapon)}; bodyPart={Safe(victim.BodyPart)}; distance={InvariantDouble(victim.Distance, "0.0")}; location={Safe(victim.Location)}; time={Safe(victim.Time)}; authoritativeClass=descriptor_victim_record_observed_not_yet_committed");
            }

            List<VanguardOperatorCareerTruthSkillDto> skills = probe.SkillsWithSessionPointEntries ?? new List<VanguardOperatorCareerTruthSkillDto>();
            string skillSummary = skills.Count == 0
                ? "none"
                : string.Join(",", skills.Select(skill => Safe(skill.Id) + ":" + skill.PointsEarnedDuringSession.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
            VanguardClientDiagnosticsLog.Operational(
                VanguardBuildVersion.CareerTruthProbeStatusTag,
                () => $"VANGUARD_CAREER_TRUTH_SKILLS operator={Safe(entry.OperatorId)}; commonState={Safe(probe.SkillsCommonState)}; commonCount={probe.SkillCommonCount}; sessionPointEntries={probe.SkillsWithSessionPoints}; sessionPointsTotal={InvariantDouble(probe.SkillSessionPointsTotal, "0.###")}; entries={skillSummary}; semantics=Skills.Common.PointsEarnedDuringSession_not_general_career_xp");

            string reliability = probe.MissingOrUnreliable == null || probe.MissingOrUnreliable.Count == 0
                ? "none"
                : string.Join(",", probe.MissingOrUnreliable.Select(Safe));
            VanguardClientDiagnosticsLog.Operational(
                VanguardBuildVersion.CareerTruthProbeStatusTag,
                () => $"VANGUARD_CAREER_TRUTH_RELIABILITY operator={Safe(entry.OperatorId)}; missingOrUnreliable={reliability}; noSynthesis=true; careerMutation=false; persistenceSemanticsChanged=false");
        }
    }

    private static string NullableLong(long? value) => value.HasValue ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none";
    private static string InvariantDouble(double value, string format) => value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);

    private static bool TryResolveDetachedLivingSnapshot(
        VanguardRaidOperatorRuntimeRecord record,
        string raidSessionId,
        out DetachedLivingPersistenceSnapshot? snapshot,
        out string reason)
    {
        snapshot = null;
        reason = "not_found";
        string key = record.BotProfileId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key) || record.BotOwner != null || !record.RuntimeDetachedAtUtc.HasValue)
        {
            reason = "record_not_detached";
            return false;
        }

        lock (Sync)
        {
            if (!DetachedLivingSnapshotsByBotProfileId.TryGetValue(key, out DetachedLivingPersistenceSnapshot? candidate))
            {
                reason = DetachedLivingSnapshotFailuresByBotProfileId.TryGetValue(key, out string? failure)
                    ? "capture_failed_" + Safe(failure)
                    : "not_captured";
                return false;
            }

            if (candidate.RuntimeIncarnation != record.RuntimeIncarnation)
            {
                reason = $"incarnation_mismatch_expected_{record.RuntimeIncarnation}_captured_{candidate.RuntimeIncarnation}";
                return false;
            }

            if (!string.Equals(candidate.RaidSessionId, raidSessionId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(candidate.OperatorId, record.OperatorId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(candidate.OwnerProfileId, record.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(candidate.BotProfileId, record.BotProfileId, StringComparison.OrdinalIgnoreCase))
            {
                reason = "persistent_identity_or_raid_mismatch";
                return false;
            }

            snapshot = candidate;
            reason = "ok";
            return true;
        }
    }

    private static bool TryBuildProfileDescriptorSnapshot(
        Player player,
        out string descriptorJson,
        out int itemCount,
        out HashSet<string> equipmentIds,
        out string reason)
    {
        descriptorJson = string.Empty;
        itemCount = 0;
        equipmentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        reason = "unknown";
        try
        {
            // EFT.Player also implements Dissonance.IDissonancePlayer. Erase the concrete type
            // before binding only EFT.IPlayer so Vanguard keeps its canonical client dependency
            // graph free of a direct DissonanceVoip compile-time reference.
            object rawPlayer = player;
            if (rawPlayer is not IPlayer iPlayer)
            {
                reason = "iplayer_contract_missing";
                return false;
            }

            var descriptor = new CompleteProfileDescriptorClass(iPlayer.Profile, iPlayer.SearchController);
            descriptorJson = JsonConvert.SerializeObject(descriptor);
            JObject root = JObject.Parse(descriptorJson);
            JToken? inventory = GetTokenCaseInsensitive(root, "Inventory");
            JArray? items = inventory == null ? null : GetTokenCaseInsensitive(inventory, "items") as JArray;
            string equipmentId = GetTokenCaseInsensitive(inventory, "equipment")?.Value<string>() ?? string.Empty;
            if (items == null || string.IsNullOrWhiteSpace(equipmentId))
            {
                reason = "descriptor_inventory_or_equipment_missing";
                return false;
            }

            itemCount = items.Count;
            equipmentIds = CollectEquipmentTreeIds(items, equipmentId);
            if (equipmentIds.Count == 0 || !equipmentIds.Contains(equipmentId))
            {
                reason = "descriptor_equipment_tree_missing";
                return false;
            }

            reason = "ok";
            return true;
        }
        catch (Exception exception)
        {
            reason = "descriptor_exception_" + exception.GetType().Name;
            return false;
        }
    }

    private static HashSet<string> CollectEquipmentTreeIds(JArray items, string equipmentId)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { equipmentId };
        bool changed;
        do
        {
            changed = false;
            foreach (JObject item in items.OfType<JObject>())
            {
                string id = GetTokenCaseInsensitive(item, "_id")?.Value<string>()
                    ?? GetTokenCaseInsensitive(item, "id")?.Value<string>()
                    ?? string.Empty;
                string parentId = GetTokenCaseInsensitive(item, "parentId")?.Value<string>() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(id) && result.Contains(parentId) && result.Add(id))
                {
                    changed = true;
                }
            }
        }
        while (changed);
        return result;
    }

    private static bool TryCaptureCorpseEquipmentIds(EFT.Interactive.Corpse corpse, out HashSet<string> ids, out string reason)
    {
        ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        reason = "unknown";
        try
        {
            if (corpse?.Item is not InventoryEquipment equipment)
            {
                reason = "corpse_equipment_missing";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(equipment.Id))
            {
                ids.Add(equipment.Id);
            }

            foreach (Item item in equipment.GetAllItems().Where(item => item != null))
            {
                if (!string.IsNullOrWhiteSpace(item.Id))
                {
                    ids.Add(item.Id);
                }
            }

            reason = ids.Count > 0 ? "ok" : "corpse_equipment_empty";
            return ids.Count > 0;
        }
        catch (Exception exception)
        {
            reason = "corpse_equipment_exception_" + exception.GetType().Name;
            return false;
        }
    }

    private static double ReadHealthRatio(Player player)
    {
        // Same concrete-type erasure as descriptor capture: converting Player directly to IPlayer
        // makes Roslyn resolve Player's IDissonancePlayer interface and would force DissonanceVoip.
        object rawPlayer = player;
        if (rawPlayer is not IPlayer iPlayer)
        {
            return 0.05;
        }

        if (VanguardRaidOperatorVitalitySnapshot.TryReadCommonHealth(iPlayer, out float current, out float maximum, out bool isAlive)
            && isAlive
            && maximum > 0f)
        {
            return Math.Max(0.05, Math.Min(1.0, current / maximum));
        }

        VanguardRaidOperatorVitalitySnapshot snapshot = VanguardRaidOperatorVitalitySnapshot.Create(iPlayer);
        return Math.Max(0.05, Math.Min(1.0, snapshot.HealthPercent / 100.0));
    }

    private static JToken? GetTokenCaseInsensitive(JToken? parent, string name)
    {
        if (parent is not JObject obj)
        {
            return null;
        }

        return obj.Properties().FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
    }
#endif

    private static void FailAttempt(string reason, string source, string raidSessionId)
    {
        VanguardClientDiagnosticsLog.Warning(StatusTag,
            $"VANGUARD_RAID_PERSISTENCE_SNAPSHOT_REJECTED source={Safe(source)}; raid={Safe(raidSessionId)}; reason={Safe(reason)}; batchSubmitted=false; persistentMutation=false; operatorCorpseReconciliationUnsafe=true");
    }

    private static bool IsPersistenceAuthority
        => !VanguardFikaCompat.IsInstalled
            || VanguardFikaCompat.IsActualHeadlessProcess
            || VanguardFikaCompat.IsDirectPlayerRaidHost;

    private static string ResolveAuthorityKind()
        => VanguardFikaCompat.IsActualHeadlessProcess ? "fika_headless"
            : VanguardFikaCompat.IsDirectPlayerRaidHost ? "fika_player_host"
            : VanguardFikaCompat.IsInstalled ? "fika_raid_authority"
            : "local_spt";

    private static string BuildCommitFailureSummary(IReadOnlyList<VanguardRaidOperatorPersistenceEntryResponseDto>? operators)
    {
        if (operators == null || operators.Count == 0)
        {
            return "none";
        }

        return string.Join(",", operators
            .Take(8)
            .Select(entry => Safe(entry.OperatorId) + ":" + Bool(entry.Success) + ":" + Safe(entry.Reason)));
    }

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
