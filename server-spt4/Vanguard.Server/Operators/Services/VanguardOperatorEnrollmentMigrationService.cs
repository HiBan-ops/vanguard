using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using Vanguard.Server.Diagnostics;
using Vanguard.Server.Operators.Inventory.Services;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Raid.Persistence.Services;
using Vanguard.Server.Operators.Storage;

// Responsibility: performs legacy classification plus bounded VisualIdentity and Enrollment additive migration transactions.
// Flow: Off-Raid state convergence first closes world/enrolled VisualIdentity, then repairs only provenance-proven legacy enrollment baselines as baseline + existing progress under one dual-authority transaction.
// Authority boundary: VisualIdentity migration remains isolated. Enrollment repair may mutate only EnrollmentBaseline metadata plus supported persistent EFT skill Progress values; level/XP, physical state, customization and all other profile truth remain read-only.
// Invariant: Enrollment repair is provenance-gated, additive/non-regressive, expected-before/readback/rollback protected, and serialized against raid persistence through the same commit gate. Uncertain shape always fails closed.
namespace Vanguard.Server.Operators.Services;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorEnrollmentMigrationService(
    VanguardOperatorStore store,
    VanguardEftExperienceCurveService experienceCurve,
    VanguardOperatorInventoryModeService inventoryModeService,
    VanguardOperatorVisualIdentityService visualIdentityService,
    VanguardOperatorEnrollmentBaselineService enrollmentBaselineService,
    VanguardRaidOperatorPersistenceService raidPersistenceService,
    ISptLogger<VanguardOperatorEnrollmentMigrationService> logger)
{
    public const string StatusTag = "VANGUARD_OPERATOR_ENROLLMENT_MIGRATION_STATUS";
    public const string DryPolicyId = "legacy_zero_baseline_additive_v1";
    public const int DryPolicyVersion = 2;

    private readonly ConcurrentDictionary<string, string> lastDecisionFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> lastOwnerFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim enrollmentMutationGate = new(1, 1);

    public async Task<IReadOnlyList<VanguardOperatorProfile>> MigrateWorldVisualIdentitiesAsync(
        string storageProfileId,
        IReadOnlyList<VanguardOperatorProfile> operators)
    {
        if (operators.Count == 0) return operators;

        bool inventorySessionActive = IsInventorySessionActive(storageProfileId);
        if (inventorySessionActive)
        {
            logger.Info(VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] phase=visual_world_migration; owner={Safe(storageProfileId)}; state=skipped; reason=unsafe_operator_inventory_session_active; candidates=0; mutation=false; backupPresent={Bool(store.HasVisualIdentityMigrationBackup(storageProfileId))}; readback=false; rollback=false; tag={StatusTag}"));
            return operators;
        }

        var candidateIndexes = new List<int>();
        for (int index = 0; index < operators.Count; index++)
        {
            VanguardOperatorProfile profile = operators[index];
            VanguardOperatorCumulativeMigrationAssessment chain = VanguardOperatorCumulativeMigrationPolicy.Assess(profile);
            if (!chain.Supported || !chain.RequiresVisualIdentity)
            {
                continue;
            }

            bool worldOnly = profile.Career is null
                && string.Equals(profile.ContractStatus, VanguardOperatorContractStatuses.Uncontracted, StringComparison.OrdinalIgnoreCase);
            if (worldOnly && profile.VisualIdentity is null && IsSupportedVisualSide(profile.Identity.Side))
            {
                candidateIndexes.Add(index);
            }
        }

        if (candidateIndexes.Count == 0) return operators;

        var after = operators.ToArray();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            foreach (int index in candidateIndexes)
            {
                VanguardOperatorProfile before = operators[index];
                VanguardOperatorVisualIdentity resolved = visualIdentityService.ResolveNewWorldIdentity(before, now);
                after[index] = before with { VisualIdentity = resolved, UpdatedAtUtc = now };
            }
        }
        catch (Exception exception)
        {
            logger.Error(VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] phase=visual_world_migration; owner={Safe(storageProfileId)}; state=prepare_failed; reason={Safe(exception.GetType().Name)}; candidates={candidateIndexes.Count}; mutation=false; backupPresent={Bool(store.HasVisualIdentityMigrationBackup(storageProfileId))}; readback=false; rollback=false; tag={StatusTag}"));
            return operators;
        }

        VanguardOperatorVisualIdentityMigrationWriteResult write = await store.CommitWorldVisualIdentityMigrationAsync(
            storageProfileId, operators, after);
        if (!write.Success)
        {
            logger.Error(VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] phase=visual_world_migration; owner={Safe(storageProfileId)}; state=write_failed; reason={Safe(write.Reason)}; candidates={candidateIndexes.Count}; mutation=false; backupPresent={Bool(write.PermanentBackupPresent)}; readback={Bool(write.ReadBackVerified)}; rollback={Bool(write.RolledBack)}; tag={StatusTag}"));
            return operators;
        }

        foreach (int index in candidateIndexes)
        {
            VanguardOperatorProfile migrated = after[index];
            logger.Info(VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] phase=visual_world_migration; owner={Safe(storageProfileId)}; operator={Safe(migrated.OperatorId)}; state=committed_readback_verified; strategy=resolve_deterministic_world_identity; seed={Safe(migrated.VisualIdentity?.SeedFingerprint)}; backupPresent={Bool(write.PermanentBackupPresent)}; mutation=true; readback={Bool(write.ReadBackVerified)}; rollback={Bool(write.RolledBack)}; inventoryProfileMutation=false; tag={StatusTag}"));
        }

        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] phase=visual_world_migration_summary; owner={Safe(storageProfileId)}; state=committed_readback_verified; candidates={candidateIndexes.Count}; committed={candidateIndexes.Count}; backupPresent={Bool(write.PermanentBackupPresent)}; mutation=true; readback={Bool(write.ReadBackVerified)}; rollback={Bool(write.RolledBack)}; inventoryProfileMutation=false; tag={StatusTag}"));
        return after;
    }

    public async Task<IReadOnlyList<VanguardOperatorProfile>> MigrateEnrolledVisualIdentitiesAsync(
        string storageProfileId,
        IReadOnlyList<VanguardOperatorProfile> operators)
    {
        if (operators.Count == 0) return operators;

        bool inventorySessionActive = IsInventorySessionActive(storageProfileId);
        if (inventorySessionActive)
        {
            logger.Info(VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] phase=visual_enrolled_migration; owner={Safe(storageProfileId)}; state=skipped; reason=unsafe_operator_inventory_session_active; candidates=0; mutation=false; metadataMutation=false; inventoryProfileMutation=false; readback=false; rollback=false; tag={StatusTag}"));
            return operators;
        }

        var prepared = new List<PreparedEnrolledVisual>();
        foreach (VanguardOperatorProfile profile in operators)
        {
            VanguardOperatorCumulativeMigrationAssessment chain = VanguardOperatorCumulativeMigrationPolicy.Assess(profile);
            if (!chain.Supported || !chain.RequiresVisualIdentity)
            {
                continue;
            }

            if (profile.VisualIdentity is not null || profile.Career is null || !IsSupportedVisualSide(profile.Identity.Side))
            {
                continue;
            }

            string profilePath = store.GetOperatorInventoryProfileReadPath(storageProfileId, profile.OperatorId);
            if (!File.Exists(profilePath))
            {
                LogEnrolledVisualSkip(storageProfileId, profile.OperatorId, "persistent_inventory_profile_missing");
                continue;
            }

            try
            {
                string beforeJson = await File.ReadAllTextAsync(profilePath);
                JsonObject? root = JsonNode.Parse(beforeJson) as JsonObject;
                if (root is null || !TryGetObject(root, "characters", out JsonObject? characters) || characters is null
                    || !TryGetObject(characters, "pmc", out JsonObject? pmc) || pmc is null)
                {
                    LogEnrolledVisualSkip(storageProfileId, profile.OperatorId, "persistent_inventory_profile_shape_unsupported");
                    continue;
                }

                LegacyVisualSnapshot legacy = AssessLegacyVisual(pmc, profile);
                prepared.Add(new PreparedEnrolledVisual(profile, beforeJson, root, legacy));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                LogEnrolledVisualSkip(storageProfileId, profile.OperatorId, "persistent_inventory_profile_unreadable_" + exception.GetType().Name);
            }
        }

        if (prepared.Count == 0) return operators;

        var duplicateFingerprints = prepared
            .Where(item => !string.IsNullOrWhiteSpace(item.Legacy.Fingerprint))
            .GroupBy(item => item.Legacy.Fingerprint, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reservedPersistentFingerprints = operators
            .Where(profile => VanguardOperatorCumulativeMigrationPolicy.IsCurrentVisualIdentity(profile.VisualIdentity))
            .Select(profile => Fingerprint(string.Join('|',
                profile.VisualIdentity!.Head,
                profile.VisualIdentity.Body,
                profile.VisualIdentity.Feet,
                profile.VisualIdentity.Hands,
                profile.VisualIdentity.Voice)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var mutations = new List<VanguardOperatorInventoryProfileVisualMutation>();
        var migratedById = new Dictionary<string, VanguardOperatorProfile>(StringComparer.OrdinalIgnoreCase);
        var strategyById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (PreparedEnrolledVisual item in prepared)
        {
            bool duplicate = !string.IsNullOrWhiteSpace(item.Legacy.Fingerprint)
                && (duplicateFingerprints.Contains(item.Legacy.Fingerprint)
                    || reservedPersistentFingerprints.Contains(item.Legacy.Fingerprint));
            bool sideCompatible = string.Equals(item.Legacy.State, "legacy_customization_side_compatible", StringComparison.Ordinal);
            string strategy = sideCompatible && !duplicate
                ? "adopt_existing_unique_side_compatible"
                : sideCompatible
                    ? "resolve_deterministic_replace_duplicate"
                    : "resolve_deterministic_replace_legacy";
            try
            {
                VanguardOperatorVisualIdentity identity = sideCompatible && !duplicate
                    ? visualIdentityService.AdoptLegacyPersistentIdentity(
                        item.Profile,
                        item.Legacy.Head,
                        item.Legacy.Body,
                        item.Legacy.Feet,
                        item.Legacy.Hands,
                        item.Legacy.Voice,
                        now)
                    : visualIdentityService.ResolveNewWorldIdentity(item.Profile, now);
                visualIdentityService.ValidatePersistentIdentity(identity, item.Profile.Identity.Side);
                JsonObject afterRoot = item.Root.DeepClone().AsObject();
                ApplyVisualIdentityToPersistentProfile(afterRoot, identity);
                ValidateVisualIdentityInPersistentProfile(afterRoot, identity, item.Profile.Identity.Side);
                string afterJson = afterRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
                mutations.Add(new VanguardOperatorInventoryProfileVisualMutation(item.Profile.OperatorId, item.BeforeJson, afterJson));
                migratedById[item.Profile.OperatorId] = item.Profile with { VisualIdentity = identity, UpdatedAtUtc = now };
                strategyById[item.Profile.OperatorId] = strategy;
            }
            catch (Exception exception)
            {
                logger.Error(VanguardServerDiagnosticsLog.Present(
                    $"[{StatusTag}] phase=visual_enrolled_migration; owner={Safe(storageProfileId)}; operator={Safe(item.Profile.OperatorId)}; state=prepare_failed; reason={Safe(exception.GetType().Name + "_" + exception.Message)}; mutation=false; metadataMutation=false; inventoryProfileMutation=false; readback=false; rollback=false; tag={StatusTag}"));
                return operators;
            }
        }

        if (mutations.Count == 0) return operators;

        VanguardOperatorProfile[] afterOperators = operators
            .Select(profile => migratedById.TryGetValue(profile.OperatorId, out VanguardOperatorProfile? migrated) ? migrated : profile)
            .ToArray();
        VanguardOperatorEnrolledVisualIdentityMigrationWriteResult write = await store.CommitEnrolledVisualIdentityMigrationAsync(
            storageProfileId, operators, afterOperators, mutations);
        if (!write.Success)
        {
            logger.Error(VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] phase=visual_enrolled_migration; owner={Safe(storageProfileId)}; state=write_failed; reason={Safe(write.Reason)}; candidates={mutations.Count}; operatorsBackupPresent={Bool(write.OperatorsPermanentBackupPresent)}; inventoryBackups={write.InventoryPermanentBackupCount}; mutation=false; metadataMutation=false; inventoryProfileMutation=false; readback={Bool(write.ReadBackVerified)}; rollback={Bool(write.RolledBack)}; tag={StatusTag}"));
            return operators;
        }

        foreach (VanguardOperatorInventoryProfileVisualMutation mutation in mutations)
        {
            VanguardOperatorProfile migrated = migratedById[mutation.OperatorId];
            logger.Info(VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] phase=visual_enrolled_migration; owner={Safe(storageProfileId)}; operator={Safe(mutation.OperatorId)}; state=committed_readback_verified; strategy={Safe(strategyById[mutation.OperatorId])}; seed={Safe(migrated.VisualIdentity?.SeedFingerprint)}; operatorsBackupPresent={Bool(write.OperatorsPermanentBackupPresent)}; inventoryBackupPresent=true; mutation=true; metadataMutation=true; inventoryProfileMutation=true; readback={Bool(write.ReadBackVerified)}; rollback={Bool(write.RolledBack)}; enrollmentMutation=false; tag={StatusTag}"));
        }

        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] phase=visual_enrolled_migration_summary; owner={Safe(storageProfileId)}; state=committed_readback_verified; candidates={mutations.Count}; committed={mutations.Count}; operatorsBackupPresent={Bool(write.OperatorsPermanentBackupPresent)}; inventoryBackups={write.InventoryPermanentBackupCount}; mutation=true; metadataMutation=true; inventoryProfileMutation=true; readback={Bool(write.ReadBackVerified)}; rollback={Bool(write.RolledBack)}; enrollmentMutation=false; tag={StatusTag}"));
        return afterOperators;
    }

    public async Task<IReadOnlyList<VanguardOperatorProfile>> MigrateEnrollmentBaselinesAsync(
        string storageProfileId,
        IReadOnlyList<VanguardOperatorProfile> operators)
    {
        if (operators.Count == 0) return operators;

        await enrollmentMutationGate.WaitAsync();
        try
        {
            if (IsInventorySessionActive(storageProfileId))
            {
                logger.Info(VanguardServerDiagnosticsLog.Present(
                    $"[{StatusTag}] phase=enrollment_additive_migration; owner={Safe(storageProfileId)}; state=skipped; reason=unsafe_operator_inventory_session_active; candidates=0; mutation=false; metadataMutation=false; skillMutation=false; levelXpMutation=false; physicalMutation=false; visualIdentityMutation=false; backupPresent={Bool(store.HasEnrollmentBaselineMigrationBackup(storageProfileId))}; readback=false; rollback=false; tag={StatusTag}"));
                return operators;
            }

            using IDisposable? persistenceLease = await raidPersistenceService.TryAcquireExternalMutationLeaseAsync();
            if (persistenceLease is null)
            {
                logger.Info(VanguardServerDiagnosticsLog.Present(
                    $"[{StatusTag}] phase=enrollment_additive_migration; owner={Safe(storageProfileId)}; state=skipped; reason=unsafe_raid_persistence_commit_active; candidates=0; mutation=false; metadataMutation=false; skillMutation=false; levelXpMutation=false; physicalMutation=false; visualIdentityMutation=false; backupPresent={Bool(store.HasEnrollmentBaselineMigrationBackup(storageProfileId))}; readback=false; rollback=false; tag={StatusTag}"));
                return operators;
            }

            using IDisposable? inventoryLease = await inventoryModeService.TryAcquireExternalMutationLeaseAsync(
                storageProfileId,
                operators.Select(profile => profile.OperatorId));
            if (inventoryLease is null)
            {
                logger.Info(VanguardServerDiagnosticsLog.Present(
                    $"[{StatusTag}] phase=enrollment_additive_migration; owner={Safe(storageProfileId)}; state=skipped; reason=unsafe_operator_inventory_mutation_active; candidates=0; mutation=false; metadataMutation=false; skillMutation=false; levelXpMutation=false; physicalMutation=false; visualIdentityMutation=false; backupPresent={Bool(store.HasEnrollmentBaselineMigrationBackup(storageProfileId))}; readback=false; rollback=false; tag={StatusTag}"));
                return operators;
            }

            // Recheck after acquiring both shared leases so inventory and raid persistence
            // cannot cross the admission/read boundary unnoticed.
            if (IsInventorySessionActive(storageProfileId))
            {
                logger.Info(VanguardServerDiagnosticsLog.Present(
                    $"[{StatusTag}] phase=enrollment_additive_migration; owner={Safe(storageProfileId)}; state=skipped; reason=unsafe_operator_inventory_session_became_active; candidates=0; mutation=false; metadataMutation=false; skillMutation=false; levelXpMutation=false; physicalMutation=false; visualIdentityMutation=false; backupPresent={Bool(store.HasEnrollmentBaselineMigrationBackup(storageProfileId))}; readback=false; rollback=false; tag={StatusTag}"));
                return operators;
            }

            VanguardOperatorExperienceCurveAuthority curve = experienceCurve.ResolveAuthority();
            if (!curve.IsAuthoritative || curve.MaximumLevel < 1)
            {
                logger.Warning(VanguardServerDiagnosticsLog.Present(
                    $"[{StatusTag}] phase=enrollment_additive_migration; owner={Safe(storageProfileId)}; state=skipped; reason=eft_experience_curve_not_authoritative; candidates=0; mutation=false; metadataMutation=false; skillMutation=false; levelXpMutation=false; physicalMutation=false; visualIdentityMutation=false; backupPresent={Bool(store.HasEnrollmentBaselineMigrationBackup(storageProfileId))}; readback=false; rollback=false; tag={StatusTag}"));
                return operators;
            }

            var prepared = new List<PreparedEnrollmentMigration>();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (VanguardOperatorProfile profile in operators)
            {
                VanguardOperatorCumulativeMigrationAssessment chain = VanguardOperatorCumulativeMigrationPolicy.Assess(profile);
                if (!chain.Supported || !chain.RequiresEnrollmentBaseline)
                {
                    continue;
                }

                if (!VanguardOperatorCumulativeMigrationPolicy.IsCurrentVisualIdentity(profile.VisualIdentity))
                {
                    LogEnrollmentMigrationSkip(storageProfileId, profile.OperatorId, "visual_identity_predecessor_not_current");
                    continue;
                }

                string baselineMarker = ResolveBaselineMarker(profile);
                if (!string.Equals(baselineMarker, "missing", StringComparison.Ordinal))
                {
                    continue;
                }

                if (profile.Career is null)
                {
                    continue;
                }

                string lineage = ResolveLineage(profile.Career);
                if (string.Equals(lineage, "unrecognized", StringComparison.Ordinal))
                {
                    LogEnrollmentMigrationSkip(storageProfileId, profile.OperatorId, "legacy_lineage_unrecognized");
                    continue;
                }

                VanguardOperatorExperienceWindow currentWindow = experienceCurve.ResolveWindow(Math.Max(profile.Progression.Experience, 0));
                bool currentXpCoherent = currentWindow.IsAuthoritative
                    && currentWindow.Level == Math.Max(profile.Progression.Level, 1)
                    && profile.Progression.Level >= 1
                    && profile.Progression.Level <= curve.MaximumLevel;
                if (!currentXpCoherent)
                {
                    LogEnrollmentMigrationSkip(storageProfileId, profile.OperatorId, "career_level_xp_not_reconciled");
                    continue;
                }

                VanguardOperatorExperienceWindow enrollmentWindow = experienceCurve.ResolveWindow(Math.Max(profile.Career.EnrollmentExperience, 0));
                bool enrollmentXpCoherent = enrollmentWindow.IsAuthoritative
                    && enrollmentWindow.Level == Math.Max(profile.Career.EnrollmentLevel, 1)
                    && profile.Career.EnrollmentLevel >= 1
                    && profile.Career.EnrollmentLevel <= curve.MaximumLevel;
                if (!enrollmentXpCoherent)
                {
                    LogEnrollmentMigrationSkip(storageProfileId, profile.OperatorId, "enrollment_level_xp_not_authoritative");
                    continue;
                }

                string profilePath = store.GetOperatorInventoryProfileReadPath(storageProfileId, profile.OperatorId);
                if (!File.Exists(profilePath))
                {
                    LogEnrollmentMigrationSkip(storageProfileId, profile.OperatorId, "persistent_inventory_profile_missing");
                    continue;
                }

                try
                {
                    string beforeJson = await File.ReadAllTextAsync(profilePath);
                    JsonObject? root = JsonNode.Parse(beforeJson) as JsonObject;
                    if (root is null
                        || !TryGetObject(root, "characters", out JsonObject? characters) || characters is null
                        || !TryGetObject(characters, "pmc", out JsonObject? pmc) || pmc is null
                        || !TryGetObject(pmc, "Info", out JsonObject? info) || info is null
                        || !TryGetObject(pmc, "Skills", out JsonObject? skills) || skills is null
                        || !TryGetArray(skills, "Common", out JsonArray? common) || common is null
                        || !TryGetArray(skills, "Mastering", out JsonArray? mastering) || mastering is null)
                    {
                        LogEnrollmentMigrationSkip(storageProfileId, profile.OperatorId, "persistent_inventory_profile_shape_unsupported");
                        continue;
                    }

                    SkillSnapshot commonBefore = BuildSkillSnapshot(common);
                    SkillSnapshot masteringBefore = BuildSkillSnapshot(mastering);
                    if (!commonBefore.Readable || !masteringBefore.Readable)
                    {
                        LogEnrollmentMigrationSkip(storageProfileId, profile.OperatorId, "skills_entries_unreadable");
                        continue;
                    }

                    if (!TryGetInt32(info, "Level", out int persistentLevel)
                        || !TryGetInt32(info, "Experience", out int persistentExperience)
                        || persistentLevel != profile.Progression.Level
                        || persistentExperience != profile.Progression.Experience)
                    {
                        LogEnrollmentMigrationSkip(storageProfileId, profile.OperatorId, "persistent_profile_level_xp_mismatch");
                        continue;
                    }

                    string nonSkillBefore = FingerprintNonSkillPmcTruth(pmc);
                    string visualMetadataBefore = Fingerprint(JsonSerializer.Serialize(profile.VisualIdentity));
                    VanguardOperatorEnrollmentBaselineState baseline = enrollmentBaselineService.CreateLegacyAdditiveMigrationState(profile, now);
                    VanguardOperatorProfile migratedProfile = profile with
                    {
                        EnrollmentBaseline = baseline,
                        UpdatedAtUtc = now,
                    };

                    JsonObject afterRoot = root.DeepClone().AsObject();
                    if (!TryGetObject(afterRoot, "characters", out JsonObject? afterCharacters) || afterCharacters is null
                        || !TryGetObject(afterCharacters, "pmc", out JsonObject? afterPmc) || afterPmc is null
                        || !TryGetObject(afterPmc, "Skills", out JsonObject? afterSkills) || afterSkills is null)
                    {
                        throw new InvalidOperationException("persistent_inventory_profile_clone_shape_unsupported");
                    }

                    VanguardOperatorEnrollmentAdditiveMigrationApplication application = enrollmentBaselineService.ApplyAdditiveMigrationToSkills(afterSkills, migratedProfile);
                    string nonSkillAfter = FingerprintNonSkillPmcTruth(afterPmc);
                    string visualMetadataAfter = Fingerprint(JsonSerializer.Serialize(migratedProfile.VisualIdentity));
                    if (!string.Equals(nonSkillBefore, nonSkillAfter, StringComparison.Ordinal)
                        || !string.Equals(visualMetadataBefore, visualMetadataAfter, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("non_target_profile_truth_changed_during_prepare");
                    }

                    if (!TryGetObject(afterPmc, "Info", out JsonObject? afterInfo) || afterInfo is null
                        || !TryGetInt32(afterInfo, "Level", out int afterLevel)
                        || !TryGetInt32(afterInfo, "Experience", out int afterExperience)
                        || afterLevel != persistentLevel
                        || afterExperience != persistentExperience)
                    {
                        throw new InvalidOperationException("level_xp_changed_during_prepare");
                    }

                    string afterJson = afterRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
                    prepared.Add(new PreparedEnrollmentMigration(
                        profile,
                        migratedProfile,
                        lineage,
                        beforeJson,
                        afterJson,
                        application,
                        nonSkillBefore,
                        visualMetadataBefore));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                {
                    logger.Error(VanguardServerDiagnosticsLog.Present(
                        $"[{StatusTag}] phase=enrollment_additive_migration; owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; state=prepare_failed; reason={Safe(exception.GetType().Name + "_" + exception.Message)}; mutation=false; metadataMutation=false; skillMutation=false; levelXpMutation=false; physicalMutation=false; visualIdentityMutation=false; readback=false; rollback=false; tag={StatusTag}"));
                    return operators;
                }
            }

            if (prepared.Count == 0)
            {
                return operators;
            }

            var migratedById = prepared.ToDictionary(item => item.Before.OperatorId, item => item.After, StringComparer.OrdinalIgnoreCase);
            VanguardOperatorProfile[] afterOperators = operators
                .Select(profile => migratedById.TryGetValue(profile.OperatorId, out VanguardOperatorProfile? migrated) ? migrated : profile)
                .ToArray();
            VanguardOperatorInventoryProfileEnrollmentMutation[] mutations = prepared
                .Select(item => new VanguardOperatorInventoryProfileEnrollmentMutation(item.Before.OperatorId, item.BeforeJson, item.AfterJson))
                .ToArray();

            VanguardOperatorEnrollmentMigrationWriteResult write = await store.CommitEnrollmentBaselineMigrationAsync(
                storageProfileId,
                operators,
                afterOperators,
                mutations);
            if (!write.Success)
            {
                logger.Error(VanguardServerDiagnosticsLog.Present(
                    $"[{StatusTag}] phase=enrollment_additive_migration; owner={Safe(storageProfileId)}; state=write_failed; reason={Safe(write.Reason)}; candidates={prepared.Count}; operatorsBackupPresent={Bool(write.OperatorsPermanentBackupPresent)}; inventoryBackups={write.InventoryPermanentBackupCount}; mutation=false; metadataMutation=false; skillMutation=false; levelXpMutation=false; physicalMutation=false; visualIdentityMutation=false; readback={Bool(write.ReadBackVerified)}; rollback={Bool(write.RolledBack)}; tag={StatusTag}"));
                return operators;
            }

            foreach (PreparedEnrollmentMigration item in prepared)
            {
                VanguardOperatorEnrollmentAdditiveMigrationApplication application = item.Application;
                logger.Info(VanguardServerDiagnosticsLog.Present(
                    $"[{StatusTag}] phase=enrollment_additive_migration; owner={Safe(storageProfileId)}; operator={Safe(item.Before.OperatorId)}; state=committed_readback_verified; provenance={Safe(item.Lineage)}; policy={Safe(item.After.EnrollmentBaseline?.PolicyId)}; policyVersion={item.After.EnrollmentBaseline?.PolicyVersion ?? 0}; origin={Safe(item.After.EnrollmentBaseline?.Origin)}; enrollmentLevel={item.After.EnrollmentBaseline?.CareerLevel ?? 0}; currentLevel={item.After.Progression.Level}; seed={Safe(item.After.EnrollmentBaseline?.SeedFingerprint)}; commonSeen={application.CommonSkillCount}; commonBaselineEligible={application.CommonBaselineEligibleCount}; commonChanged={application.CommonChangedCount}; commonGeneratedTotal={application.CommonBaselineGeneratedTotal:0.####}; commonAddedTotal={application.CommonAddedTotal:0.####}; commonBefore={Safe(application.CommonBeforeFingerprint)}; commonAfter={Safe(application.CommonAfterFingerprint)}; masteringSeen={application.MasteringSkillCount}; masteringBaselineEligible={application.MasteringBaselineEligibleCount}; masteringChanged={application.MasteringChangedCount}; masteringGeneratedTotal={application.MasteringBaselineGeneratedTotal:0.####}; masteringAddedTotal={application.MasteringAddedTotal:0.####}; masteringBefore={Safe(application.MasteringBeforeFingerprint)}; masteringAfter={Safe(application.MasteringAfterFingerprint)}; operatorsBackupPresent={Bool(write.OperatorsPermanentBackupPresent)}; inventoryBackupPresent=true; mutation=true; metadataMutation=true; skillMutation=true; levelXpMutation=false; physicalMutation=false; visualIdentityMutation=false; nonSkillTruth={Safe(item.NonSkillFingerprint)}; visualIdentity={Safe(item.VisualMetadataFingerprint)}; readback={Bool(write.ReadBackVerified)}; rollback={Bool(write.RolledBack)}; tag={StatusTag}"));
            }

            logger.Info(VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] phase=enrollment_additive_migration_summary; owner={Safe(storageProfileId)}; state=committed_readback_verified; candidates={prepared.Count}; committed={prepared.Count}; operatorsBackupPresent={Bool(write.OperatorsPermanentBackupPresent)}; inventoryBackups={write.InventoryPermanentBackupCount}; mutation=true; metadataMutation=true; skillMutation=true; levelXpMutation=false; physicalMutation=false; visualIdentityMutation=false; readback={Bool(write.ReadBackVerified)}; rollback={Bool(write.RolledBack)}; tag={StatusTag}"));
            return afterOperators;
        }
        finally
        {
            enrollmentMutationGate.Release();
        }
    }

    public async Task ObserveDryRunAsync(string storageProfileId, IReadOnlyList<VanguardOperatorProfile> operators)
    {
        VanguardOperatorExperienceCurveAuthority curve = experienceCurve.ResolveAuthority();
        bool inventorySessionActive = IsInventorySessionActive(storageProfileId);
        var decisions = new List<DryDecision>(operators.Count);

        foreach (VanguardOperatorProfile profile in operators)
        {
            decisions.Add(await AnalyzeAsync(storageProfileId, profile, curve, inventorySessionActive));
        }

        var duplicateVisualFingerprints = decisions
            .Where(decision => !string.IsNullOrWhiteSpace(decision.LegacyVisualFingerprint))
            .GroupBy(decision => decision.LegacyVisualFingerprint, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int enrollmentStaticCandidates = 0;
        int enrollmentDryCandidates = 0;
        int enrollmentCurrent = 0;
        int visualStaticCandidates = 0;
        int visualDryCandidates = 0;
        int visualCurrent = 0;
        int visualWorldOnlyCandidates = 0;

        foreach (DryDecision decision in decisions)
        {
            bool duplicateVisual = !string.IsNullOrWhiteSpace(decision.LegacyVisualFingerprint)
                && duplicateVisualFingerprints.Contains(decision.LegacyVisualFingerprint);
            string visualStrategy = FinalizeVisualStrategy(decision, duplicateVisual);
            DryDecision finalized = decision with
            {
                DuplicateLegacyVisual = duplicateVisual,
                VisualStrategy = visualStrategy,
            };

            if (finalized.EnrollmentStaticCandidate) enrollmentStaticCandidates++;
            if (finalized.EnrollmentDryCandidate) enrollmentDryCandidates++;
            if (string.Equals(finalized.BaselineMarker, "current", StringComparison.Ordinal)) enrollmentCurrent++;
            if (finalized.VisualStaticCandidate) visualStaticCandidates++;
            if (finalized.VisualDryCandidate) visualDryCandidates++;
            if (string.Equals(finalized.VisualIdentityState, "present", StringComparison.Ordinal)) visualCurrent++;
            if (string.Equals(finalized.VisualReason, "dry_candidate_world_operator_visual_identity_missing", StringComparison.Ordinal)) visualWorldOnlyCandidates++;
            LogDecision(storageProfileId, finalized);
        }

        string ownerFingerprint = Fingerprint($"{operators.Count}|{enrollmentStaticCandidates}|{enrollmentDryCandidates}|{enrollmentCurrent}|{visualStaticCandidates}|{visualDryCandidates}|{visualCurrent}|{visualWorldOnlyCandidates}|{curve.IsAuthoritative}|{curve.MaximumLevel}|{inventorySessionActive}|{duplicateVisualFingerprints.Count}");
        if (lastOwnerFingerprints.TryGetValue(storageProfileId, out string? previous)
            && string.Equals(previous, ownerFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        lastOwnerFingerprints[storageProfileId] = ownerFingerprint;
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] phase=dry_run_summary; owner={Safe(storageProfileId)}; policy={DryPolicyId}; policyVersion={DryPolicyVersion}; operators={operators.Count}; enrollmentStaticCandidates={enrollmentStaticCandidates}; enrollmentDryCandidates={enrollmentDryCandidates}; enrollmentCurrent={enrollmentCurrent}; visualStaticCandidates={visualStaticCandidates}; visualDryCandidates={visualDryCandidates}; visualCurrent={visualCurrent}; visualWorldOnlyCandidates={visualWorldOnlyCandidates}; curveAuthoritative={Bool(curve.IsAuthoritative)}; maximumLevel={curve.MaximumLevel}; inventorySessionActive={Bool(inventorySessionActive)}; duplicateLegacyVisualFingerprints={duplicateVisualFingerprints.Count}; mutation=false; backups=false; tag={StatusTag}"));
    }

    private async Task<DryDecision> AnalyzeAsync(
        string storageProfileId,
        VanguardOperatorProfile profile,
        VanguardOperatorExperienceCurveAuthority curve,
        bool inventorySessionActive)
    {
        string baselineMarker = ResolveBaselineMarker(profile);
        string lineage = profile.Career is null ? "none" : ResolveLineage(profile.Career);
        VanguardOperatorCumulativeMigrationAssessment chain = VanguardOperatorCumulativeMigrationPolicy.Assess(profile);
        if (!chain.Supported)
        {
            string reason = "cumulative_chain_" + chain.Reason;
            return DryDecision.Create(
                profile,
                EnrollmentDecision.Skip(reason),
                VisualDecision.Skip(reason),
                lineage,
                baselineMarker);
        }
        bool worldNotYetEnrolled = profile.Career is null
            && string.Equals(profile.ContractStatus, VanguardOperatorContractStatuses.Uncontracted, StringComparison.OrdinalIgnoreCase);
        bool visualPresent = profile.VisualIdentity is not null;
        bool supportedVisualSide = IsSupportedVisualSide(profile.Identity.Side);

        EnrollmentDecision enrollment = ResolveEnrollmentPreProfile(profile, baselineMarker, lineage, curve, inventorySessionActive);
        VisualDecision visual = ResolveVisualPreProfile(profile, worldNotYetEnrolled, visualPresent, supportedVisualSide, inventorySessionActive);

        bool needsProfile = enrollment.NeedsProfile || visual.NeedsProfile;
        if (!needsProfile)
        {
            return DryDecision.Create(profile, enrollment, visual, lineage, baselineMarker);
        }

        string profilePath = store.GetOperatorInventoryProfileReadPath(storageProfileId, profile.OperatorId);
        if (!File.Exists(profilePath))
        {
            enrollment = enrollment.NeedsProfile
                ? enrollment with { StaticCandidate = false, DryCandidate = false, Reason = enrollment.ProfileMissingReason, NeedsProfile = false }
                : enrollment;
            visual = visual.NeedsProfile
                ? visual with { StaticCandidate = false, DryCandidate = false, Reason = "persistent_inventory_profile_missing", Strategy = "none", NeedsProfile = false }
                : visual;
            return DryDecision.Create(profile, enrollment, visual, lineage, baselineMarker) with
            {
                InventoryProfileState = "missing",
            };
        }

        JsonObject? root;
        try
        {
            string json = await File.ReadAllTextAsync(profilePath);
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            enrollment = FailProfileAxis(enrollment, "persistent_inventory_profile_unreadable");
            visual = FailProfileAxis(visual, "persistent_inventory_profile_unreadable");
            return DryDecision.Create(profile, enrollment, visual, lineage, baselineMarker) with
            {
                InventoryProfileState = "unreadable_" + exception.GetType().Name,
            };
        }

        if (root is null || !TryGetObject(root, "characters", out JsonObject? characters)
            || characters is null || !TryGetObject(characters, "pmc", out JsonObject? pmc) || pmc is null)
        {
            enrollment = FailProfileAxis(enrollment, "persistent_inventory_profile_shape_unsupported");
            visual = FailProfileAxis(visual, "persistent_inventory_profile_shape_unsupported");
            return DryDecision.Create(profile, enrollment, visual, lineage, baselineMarker) with
            {
                InventoryProfileState = "shape_unsupported",
            };
        }

        SkillSnapshot commonSnapshot = SkillSnapshot.Empty;
        SkillSnapshot masteringSnapshot = SkillSnapshot.Empty;
        bool xpCoherent = false;

        if (enrollment.NeedsProfile)
        {
            if (!TryGetObject(pmc, "Skills", out JsonObject? skills) || skills is null
                || !TryGetArray(skills, "Common", out JsonArray? common) || common is null
                || !TryGetArray(skills, "Mastering", out JsonArray? mastering) || mastering is null)
            {
                enrollment = enrollment with
                {
                    StaticCandidate = false,
                    DryCandidate = false,
                    Reason = "skills_shape_unsupported",
                    NeedsProfile = false,
                };
            }
            else
            {
                commonSnapshot = BuildSkillSnapshot(common);
                masteringSnapshot = BuildSkillSnapshot(mastering);
                if (!commonSnapshot.Readable || !masteringSnapshot.Readable)
                {
                    enrollment = enrollment with
                    {
                        StaticCandidate = false,
                        DryCandidate = false,
                        Reason = "skills_entries_unreadable",
                        NeedsProfile = false,
                    };
                }
                else
                {
                    VanguardOperatorExperienceWindow xpWindow = experienceCurve.ResolveWindow(Math.Max(profile.Progression.Experience, 0));
                    xpCoherent = curve.IsAuthoritative
                        && xpWindow.IsAuthoritative
                        && xpWindow.Level == Math.Max(profile.Progression.Level, 1)
                        && profile.Progression.Level >= 1
                        && profile.Progression.Level <= curve.MaximumLevel;
                    bool provenLineage = !string.Equals(lineage, "unrecognized", StringComparison.Ordinal)
                        && !string.Equals(lineage, "none", StringComparison.Ordinal);
                    bool staticCandidate = provenLineage && curve.IsAuthoritative && xpCoherent;
                    string reason = !provenLineage ? "legacy_lineage_unrecognized"
                        : !curve.IsAuthoritative ? "eft_experience_curve_not_authoritative"
                        : !xpCoherent ? "career_level_xp_not_reconciled"
                        : inventorySessionActive ? "unsafe_operator_inventory_session_active"
                        : "dry_candidate_proven_zero_baseline_lineage";
                    enrollment = enrollment with
                    {
                        StaticCandidate = staticCandidate,
                        DryCandidate = staticCandidate && !inventorySessionActive,
                        Reason = reason,
                        NeedsProfile = false,
                    };
                }
            }
        }

        LegacyVisualSnapshot legacyVisual = LegacyVisualSnapshot.NotProbed;
        if (visual.NeedsProfile)
        {
            legacyVisual = AssessLegacyVisual(pmc, profile);
            bool resolvableByPolicy = supportedVisualSide;
            bool staticCandidate = resolvableByPolicy;
            string reason = !resolvableByPolicy ? "visual_identity_side_unsupported"
                : inventorySessionActive ? "unsafe_operator_inventory_session_active"
                : "dry_candidate_legacy_visual_identity_missing";
            string strategy = legacyVisual.State switch
            {
                "legacy_customization_side_compatible" => "adopt_existing_if_unique_else_resolve_deterministic",
                _ => "resolve_deterministic_replace_legacy",
            };
            visual = visual with
            {
                StaticCandidate = staticCandidate,
                DryCandidate = staticCandidate && !inventorySessionActive,
                Reason = reason,
                Strategy = staticCandidate ? strategy : "none",
                NeedsProfile = false,
            };
        }

        return DryDecision.Create(profile, enrollment, visual, lineage, baselineMarker) with
        {
            InventoryProfileState = "readable",
            XpCoherent = xpCoherent,
            CommonCount = commonSnapshot.Count,
            MasteringCount = masteringSnapshot.Count,
            CommonFingerprint = commonSnapshot.Fingerprint,
            MasteringFingerprint = masteringSnapshot.Fingerprint,
            LegacyVisualState = legacyVisual.State,
            LegacyVisualFingerprint = legacyVisual.Fingerprint,
        };
    }

    private static EnrollmentDecision ResolveEnrollmentPreProfile(
        VanguardOperatorProfile profile,
        string baselineMarker,
        string lineage,
        VanguardOperatorExperienceCurveAuthority curve,
        bool inventorySessionActive)
    {
        if (string.Equals(baselineMarker, "current", StringComparison.Ordinal))
        {
            return EnrollmentDecision.Skip("baseline_policy_already_present");
        }

        if (string.Equals(baselineMarker, "conflicting", StringComparison.Ordinal))
        {
            return EnrollmentDecision.Skip("conflicting_baseline_marker");
        }

        if (profile.Career is null)
        {
            return EnrollmentDecision.Skip(
                string.Equals(profile.ContractStatus, VanguardOperatorContractStatuses.Uncontracted, StringComparison.OrdinalIgnoreCase)
                    ? "world_operator_not_yet_enrolled"
                    : "career_provenance_missing");
        }

        if (string.Equals(lineage, "unrecognized", StringComparison.Ordinal))
        {
            return EnrollmentDecision.Skip("legacy_lineage_unrecognized");
        }

        if (!curve.IsAuthoritative)
        {
            return EnrollmentDecision.Skip("eft_experience_curve_not_authoritative");
        }

        return new EnrollmentDecision(
            false,
            false,
            inventorySessionActive ? "unsafe_operator_inventory_session_active" : "profile_probe_required",
            true,
            "persistent_inventory_profile_missing");
    }

    private static VisualDecision ResolveVisualPreProfile(
        VanguardOperatorProfile profile,
        bool worldNotYetEnrolled,
        bool visualPresent,
        bool supportedVisualSide,
        bool inventorySessionActive)
    {
        if (visualPresent)
        {
            return VisualDecision.Skip("visual_identity_already_present");
        }

        if (!supportedVisualSide)
        {
            return VisualDecision.Skip("visual_identity_side_unsupported");
        }

        if (worldNotYetEnrolled)
        {
            return new VisualDecision(
                true,
                !inventorySessionActive,
                inventorySessionActive ? "unsafe_operator_inventory_session_active" : "dry_candidate_world_operator_visual_identity_missing",
                "resolve_deterministic_world_identity",
                false);
        }

        return new VisualDecision(
            false,
            false,
            inventorySessionActive ? "unsafe_operator_inventory_session_active" : "profile_probe_required",
            "none",
            true);
    }

    private LegacyVisualSnapshot AssessLegacyVisual(JsonObject pmc, VanguardOperatorProfile profile)
    {
        if (profile.VisualIdentity is not null)
        {
            return new LegacyVisualSnapshot(
                "persistent_visual_identity_present",
                Fingerprint(string.Join('|', profile.VisualIdentity.Head, profile.VisualIdentity.Body, profile.VisualIdentity.Feet, profile.VisualIdentity.Hands, profile.VisualIdentity.Voice)),
                profile.VisualIdentity.Head,
                profile.VisualIdentity.Body,
                profile.VisualIdentity.Feet,
                profile.VisualIdentity.Hands,
                profile.VisualIdentity.Voice);
        }

        if (!TryGetObject(pmc, "Customization", out JsonObject? customization) || customization is null)
        {
            return new LegacyVisualSnapshot("legacy_customization_missing", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        string head = GetString(customization, "Head");
        string body = GetString(customization, "Body");
        string feet = GetString(customization, "Feet");
        string hands = GetString(customization, "Hands");
        string voice = GetString(customization, "Voice");
        string fingerprint = Fingerprint(string.Join('|', head, body, feet, hands, voice));
        try
        {
            visualIdentityService.ValidateCustomizationForSide(head, body, feet, hands, voice, profile.Identity.Side);
            return new LegacyVisualSnapshot("legacy_customization_side_compatible", fingerprint, head, body, feet, hands, voice);
        }
        catch (Exception exception)
        {
            return new LegacyVisualSnapshot("legacy_customization_incompatible_" + Safe(exception.Message), fingerprint, head, body, feet, hands, voice);
        }
    }

    private void LogEnrollmentMigrationSkip(string storageProfileId, string operatorId, string reason)
    {
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] phase=enrollment_additive_migration; owner={Safe(storageProfileId)}; operator={Safe(operatorId)}; state=skipped; reason={Safe(reason)}; mutation=false; metadataMutation=false; skillMutation=false; levelXpMutation=false; physicalMutation=false; visualIdentityMutation=false; readback=false; rollback=false; tag={StatusTag}"));
    }

    private static bool TryGetInt32(JsonObject source, string name, out int value)
    {
        value = 0;
        if (!TryGetDouble(source, name, out double number)
            || !double.IsFinite(number)
            || Math.Abs(number - Math.Round(number)) > 0.0001d
            || number < int.MinValue
            || number > int.MaxValue)
        {
            return false;
        }

        value = (int)Math.Round(number);
        return true;
    }

    private static string FingerprintNonSkillPmcTruth(JsonObject pmc)
    {
        var parts = new List<string>();
        foreach ((string key, JsonNode? value) in pmc)
        {
            if (string.Equals(key, "Skills", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            parts.Add(key.Trim().ToLowerInvariant() + ":" + (value?.ToJsonString() ?? "null"));
        }

        parts.Sort(StringComparer.Ordinal);
        return Fingerprint(string.Join('|', parts));
    }

    private void LogEnrolledVisualSkip(string storageProfileId, string operatorId, string reason)
    {
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] phase=visual_enrolled_migration; owner={Safe(storageProfileId)}; operator={Safe(operatorId)}; state=skipped; reason={Safe(reason)}; mutation=false; metadataMutation=false; inventoryProfileMutation=false; readback=false; rollback=false; enrollmentMutation=false; tag={StatusTag}"));
    }

    private static void ApplyVisualIdentityToPersistentProfile(JsonObject root, VanguardOperatorVisualIdentity identity)
    {
        JsonObject characters = GetOrCreateObject(root, "characters");
        JsonObject pmc = GetOrCreateObject(characters, "pmc");
        JsonObject customization = GetOrCreateObject(pmc, "Customization");
        SetString(customization, "Head", identity.Head);
        SetString(customization, "Body", identity.Body);
        SetString(customization, "Feet", identity.Feet);
        SetString(customization, "Hands", identity.Hands);
        SetString(customization, "Voice", identity.Voice);
        foreach (string key in customization.Select(property => property.Key).ToArray())
        {
            if (string.Equals(key, "Dogtag", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "DogTag", StringComparison.OrdinalIgnoreCase))
            {
                customization.Remove(key);
            }
        }
    }

    private void ValidateVisualIdentityInPersistentProfile(JsonObject root, VanguardOperatorVisualIdentity identity, string side)
    {
        if (!TryGetObject(root, "characters", out JsonObject? characters) || characters is null
            || !TryGetObject(characters, "pmc", out JsonObject? pmc) || pmc is null
            || !TryGetObject(pmc, "Customization", out JsonObject? customization) || customization is null)
        {
            throw new InvalidOperationException("persistent_inventory_profile_customization_missing_after_prepare");
        }

        string head = GetString(customization, "Head");
        string body = GetString(customization, "Body");
        string feet = GetString(customization, "Feet");
        string hands = GetString(customization, "Hands");
        string voice = GetString(customization, "Voice");
        if (!string.Equals(head, identity.Head, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(body, identity.Body, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(feet, identity.Feet, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(hands, identity.Hands, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(voice, identity.Voice, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("persistent_inventory_profile_customization_prepare_mismatch");
        }

        visualIdentityService.ValidateCustomizationForSide(head, body, feet, hands, voice, side);
    }

    private static JsonObject GetOrCreateObject(JsonObject source, string name)
    {
        foreach ((string key, JsonNode? value) in source)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) && value is JsonObject existing)
            {
                return existing;
            }
        }

        var created = new JsonObject();
        source[name] = created;
        return created;
    }

    private static void SetString(JsonObject source, string name, string value)
    {
        string? existingKey = source.Select(property => property.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        source[existingKey ?? name] = value;
    }

    private static string FinalizeVisualStrategy(DryDecision decision, bool duplicateVisual)
    {
        if (!decision.VisualDryCandidate)
        {
            return decision.VisualStrategy;
        }

        if (string.Equals(decision.VisualStrategy, "adopt_existing_if_unique_else_resolve_deterministic", StringComparison.Ordinal))
        {
            return duplicateVisual ? "resolve_deterministic_replace_duplicate" : "adopt_existing_unique_side_compatible";
        }

        return decision.VisualStrategy;
    }

    private void LogDecision(string storageProfileId, DryDecision decision)
    {
        string key = storageProfileId + "|" + decision.OperatorId;
        string fingerprint = Fingerprint(string.Join('|',
            decision.EnrollmentStaticCandidate, decision.EnrollmentDryCandidate, decision.EnrollmentReason,
            decision.VisualStaticCandidate, decision.VisualDryCandidate, decision.VisualReason, decision.VisualStrategy,
            decision.Provenance, decision.BaselineMarker, decision.InventoryProfileState, decision.XpCoherent,
            decision.CommonCount, decision.MasteringCount, decision.CommonFingerprint, decision.MasteringFingerprint,
            decision.LegacyVisualState, decision.LegacyVisualFingerprint, decision.DuplicateLegacyVisual, decision.VisualIdentityState));
        if (lastDecisionFingerprints.TryGetValue(key, out string? previous)
            && string.Equals(previous, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        lastDecisionFingerprints[key] = fingerprint;
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] phase=dry_run; owner={Safe(storageProfileId)}; operator={Safe(decision.OperatorId)}; policy={DryPolicyId}; policyVersion={DryPolicyVersion}; enrollmentStaticCandidate={Bool(decision.EnrollmentStaticCandidate)}; enrollmentDryCandidate={Bool(decision.EnrollmentDryCandidate)}; enrollmentReason={Safe(decision.EnrollmentReason)}; visualStaticCandidate={Bool(decision.VisualStaticCandidate)}; visualDryCandidate={Bool(decision.VisualDryCandidate)}; visualReason={Safe(decision.VisualReason)}; visualStrategy={Safe(decision.VisualStrategy)}; mutationReady=false; provenance={Safe(decision.Provenance)}; baselineMarker={Safe(decision.BaselineMarker)}; inventoryProfile={Safe(decision.InventoryProfileState)}; xpCoherent={Bool(decision.XpCoherent)}; commonCount={decision.CommonCount}; commonFingerprint={Safe(decision.CommonFingerprint)}; masteringCount={decision.MasteringCount}; masteringFingerprint={Safe(decision.MasteringFingerprint)}; legacyVisual={Safe(decision.LegacyVisualState)}; legacyVisualFingerprint={Safe(decision.LegacyVisualFingerprint)}; duplicateLegacyVisual={Bool(decision.DuplicateLegacyVisual)}; visualIdentity={Safe(decision.VisualIdentityState)}; futureMutationGate=raid_and_persistence_commit_not_evaluated; mutation=false; backup=false; write=false; readback=false; rollback=false; tag={StatusTag}"));
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

    private static string ResolveBaselineMarker(VanguardOperatorProfile profile)
    {
        if (profile.EnrollmentBaseline is null)
        {
            return "missing";
        }

        bool current = string.Equals(profile.EnrollmentBaseline.PolicyId, VanguardOperatorEnrollmentBaselineService.PolicyId, StringComparison.Ordinal)
            && profile.EnrollmentBaseline.PolicyVersion == VanguardOperatorEnrollmentBaselineService.PolicyVersion
            && profile.EnrollmentBaseline.SchemaVersion == VanguardOperatorEnrollmentBaselineSchema.CurrentVersion;
        return current ? "current" : "conflicting";
    }

    private static string ResolveLineage(VanguardOperatorCareer career)
    {
        if (string.Equals(career.TrackingOrigin, "legacy_profile_migration", StringComparison.OrdinalIgnoreCase)
            && career.HistoryCompleteness.StartsWith("partial_from_", StringComparison.OrdinalIgnoreCase))
        {
            return "legacy_profile_migration_zero_baseline";
        }

        if (string.Equals(career.TrackingOrigin, "native_enrollment", StringComparison.OrdinalIgnoreCase)
            && string.Equals(career.HistoryCompleteness, "complete_since_enrollment", StringComparison.OrdinalIgnoreCase))
        {
            return "pre_m2_native_enrollment_zero_baseline";
        }

        return "unrecognized";
    }

    private static bool IsSupportedVisualSide(string? side)
        => string.Equals(side, "Bear", StringComparison.OrdinalIgnoreCase)
            || string.Equals(side, "Usec", StringComparison.OrdinalIgnoreCase);

    private static EnrollmentDecision FailProfileAxis(EnrollmentDecision decision, string reason)
        => decision.NeedsProfile
            ? decision with { StaticCandidate = false, DryCandidate = false, Reason = reason, NeedsProfile = false }
            : decision;

    private static VisualDecision FailProfileAxis(VisualDecision decision, string reason)
        => decision.NeedsProfile
            ? decision with { StaticCandidate = false, DryCandidate = false, Reason = reason, Strategy = "none", NeedsProfile = false }
            : decision;

    private static SkillSnapshot BuildSkillSnapshot(JsonArray values)
    {
        var parts = new List<string>();
        bool readable = true;
        foreach (JsonNode? node in values)
        {
            if (node is not JsonObject entry)
            {
                readable = false;
                continue;
            }

            string id = GetString(entry, "Id");
            if (string.IsNullOrWhiteSpace(id) || !TryGetDouble(entry, "Progress", out double progress))
            {
                readable = false;
                continue;
            }

            parts.Add(id.Trim().ToLowerInvariant() + ":" + progress.ToString("0.####", CultureInfo.InvariantCulture));
        }

        parts.Sort(StringComparer.Ordinal);
        return new SkillSnapshot(readable, parts.Count, Fingerprint(string.Join('|', parts)));
    }

    private static bool TryGetObject(JsonObject source, string name, out JsonObject? value)
    {
        value = Find(source, name) as JsonObject;
        return value is not null;
    }

    private static bool TryGetArray(JsonObject source, string name, out JsonArray? value)
    {
        value = Find(source, name) as JsonArray;
        return value is not null;
    }

    private static string GetString(JsonObject source, string name)
    {
        JsonNode? value = Find(source, name);
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out string? text))
        {
            return text?.Trim() ?? string.Empty;
        }
        return value?.ToString().Trim() ?? string.Empty;
    }

    private static bool TryGetDouble(JsonObject source, string name, out double value)
    {
        value = 0d;
        JsonNode? node = Find(source, name);
        if (node is not JsonValue scalar) return false;
        if (scalar.TryGetValue<double>(out value)) return true;
        if (scalar.TryGetValue<int>(out int integer))
        {
            value = integer;
            return true;
        }
        return double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static JsonNode? Find(JsonObject source, string name)
    {
        foreach ((string key, JsonNode? value) in source)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }

    private static string Fingerprint(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)))[..16];

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private sealed record PreparedEnrollmentMigration(
        VanguardOperatorProfile Before,
        VanguardOperatorProfile After,
        string Lineage,
        string BeforeJson,
        string AfterJson,
        VanguardOperatorEnrollmentAdditiveMigrationApplication Application,
        string NonSkillFingerprint,
        string VisualMetadataFingerprint);

    private sealed record PreparedEnrolledVisual(
        VanguardOperatorProfile Profile,
        string BeforeJson,
        JsonObject Root,
        LegacyVisualSnapshot Legacy);

    private sealed record SkillSnapshot(bool Readable, int Count, string Fingerprint)
    {
        public static SkillSnapshot Empty { get; } = new(false, 0, "none");
    }

    private sealed record LegacyVisualSnapshot(
        string State,
        string Fingerprint,
        string Head,
        string Body,
        string Feet,
        string Hands,
        string Voice)
    {
        public static LegacyVisualSnapshot NotProbed { get; } = new(
            "not_probed",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
    }

    private sealed record EnrollmentDecision(
        bool StaticCandidate,
        bool DryCandidate,
        string Reason,
        bool NeedsProfile,
        string ProfileMissingReason)
    {
        public static EnrollmentDecision Skip(string reason) => new(false, false, reason, false, "persistent_inventory_profile_missing");
    }

    private sealed record VisualDecision(
        bool StaticCandidate,
        bool DryCandidate,
        string Reason,
        string Strategy,
        bool NeedsProfile)
    {
        public static VisualDecision Skip(string reason) => new(false, false, reason, "none", false);
    }

    private sealed record DryDecision(
        string OperatorId,
        bool EnrollmentStaticCandidate,
        bool EnrollmentDryCandidate,
        string EnrollmentReason,
        bool VisualStaticCandidate,
        bool VisualDryCandidate,
        string VisualReason,
        string VisualStrategy,
        string Provenance,
        string BaselineMarker,
        string InventoryProfileState,
        bool XpCoherent,
        int CommonCount,
        int MasteringCount,
        string CommonFingerprint,
        string MasteringFingerprint,
        string LegacyVisualState,
        string LegacyVisualFingerprint,
        bool DuplicateLegacyVisual,
        string VisualIdentityState)
    {
        public static DryDecision Create(
            VanguardOperatorProfile profile,
            EnrollmentDecision enrollment,
            VisualDecision visual,
            string provenance,
            string baselineMarker) => new(
                profile.OperatorId,
                enrollment.StaticCandidate,
                enrollment.DryCandidate,
                enrollment.Reason,
                visual.StaticCandidate,
                visual.DryCandidate,
                visual.Reason,
                visual.Strategy,
                provenance,
                baselineMarker,
                "not_probed",
                false,
                0,
                0,
                "none",
                "none",
                "not_probed",
                string.Empty,
                false,
                profile.VisualIdentity is null ? "missing" : "present");
    }
}
