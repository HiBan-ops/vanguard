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

// Responsibility: Builds the client end-of-raid persistence request from live Operator truth and sends it once to the server persistence transaction.
// Flow: Raid stop boundaries are deduplicated, runtime Operators are snapshotted with health/inventory/Career evidence, the request is posted, and success/readback diagnostics close the client lifecycle.
// Authority boundary: The client may observe and package raid truth; the server remains the only durable Operator/Career persistence authority.
// Invariant: One raid outcome must not be committed twice, missing Operators/facts are reported rather than synthesized, and client cleanup cannot run as proof of server commit.
namespace Vanguard.Client.Raid.Persistence;

internal static class VanguardRaidOperatorPersistenceService
{
    public const string StatusTag = "VANGUARD_OPERATOR_POSTRAID_PERSISTENCE_STATUS";
    private static readonly object Sync = new();
    private static string armedRaidSessionId = string.Empty;
    private static bool armed;
    private static bool finalCommitAttempted;

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
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_RAID_PERSISTENCE_RESET reason={Safe(reason)}; armed=false; finalCommit=false; operatorCorpseTransactions=false");
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
    public static void CommitAtRaidEnd(string source)
    {
        string raidSessionId;
        lock (Sync)
        {
            if (!armed || finalCommitAttempted)
            {
                return;
            }

            if (!IsPersistenceAuthority)
            {
                return;
            }

            raidSessionId = armedRaidSessionId;
            finalCommitAttempted = true;
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
