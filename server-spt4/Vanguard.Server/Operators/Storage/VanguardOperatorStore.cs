using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Raid.Persistence.Models;
using Vanguard.Server.Operators.Services;

// Responsibility: canonical file-backed Operator storage and atomic persistence helpers.
// Flow: Writers update normalized entries, readers query a stable view, and lifecycle/reset hooks clear or reconcile data at the appropriate boundary.
// Authority boundary: services decide what state is valid; the store enforces compare-before-write, readback and rollback mechanics without inventing domain data.
// Invariant: migrations/reconciliation preserve a rollback copy when possible, while backup failure must not make normal server boot destructive.

namespace Vanguard.Server.Operators.Storage;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorStore
{
    private const string ProfilesDirectoryName = "profiles";
    private const string OperatorsFileName = "operators.json";
    private const string ActiveServiceFileName = "active-service.json";
    private const string ContractsFileName = "contracts.json";
    private const string MedicalFileName = "medical.json";
    private const string ContactsFileName = "contacts.json";
    private const string BillingLedgerFileName = "billing-ledger.json";
    private const string InventoryProfilesDirectoryName = "inventory-profiles";
    private const string IdentityRegistryFileName = "identity-registry.json";
    private const string CareerRaidLedgerFileName = "career-raid-ledger.json";
    private const string ProfileNormalizationBackupFileName = "operators.pre-profile-normalization.json";
    private const string ExperienceReconciliationBackupFileName = "operators.pre-xp-reconciliation.json";
    private const string VisualIdentityMigrationBackupFileName = "operators.pre-visual-identity-migration.json";
    private const string EnrollmentBaselineMigrationBackupFileName = "operators.pre-enrollment-baseline-v1-migration.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string rootDirectory;
    private readonly SemaphoreSlim activeServiceMutationGate = new(1, 1);

    public VanguardOperatorStore()
        : this(ResolveDefaultRootDirectory())
    {
    }

    public VanguardOperatorStore(string rootDirectory)
    {
        this.rootDirectory = rootDirectory;
    }

    public string RootDirectory => rootDirectory;

    public void EnsureStorageRootExists()
    {
        Directory.CreateDirectory(rootDirectory);
        Directory.CreateDirectory(GetProfilesRootDirectory());
    }


    public string GetOperatorInventoryProfileDirectory(string profileId)
    {
        string normalized = NormalizeProfileId(profileId);
        string directory = Path.Combine(GetProfileDirectory(normalized), InventoryProfilesDirectoryName);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public string GetOperatorInventoryProfilePath(string profileId, string operatorId)
    {
        string path = GetOperatorInventoryProfileReadPath(profileId, operatorId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    // Read-only migration diagnostics must never create the inventory-profiles directory merely by probing legacy state.
    public string GetOperatorInventoryProfileReadPath(string profileId, string operatorId)
    {
        string safeOperatorId = SanitizeOperatorIdForFileName(operatorId);
        return Path.Combine(GetProfileDirectory(NormalizeProfileId(profileId)), InventoryProfilesDirectoryName, safeOperatorId + ".json");
    }

    public IReadOnlyList<string> GetKnownProfileIds()
    {
        EnsureStorageRootExists();
        return Directory.GetDirectories(GetProfilesRootDirectory())
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<string> ResolveStorageProfileIdAsync(string requestedProfileId)
    {
        var normalized = NormalizeProfileId(requestedProfileId);
        await EnsureProfileStorageInitializedAsync(normalized);
        return normalized;
    }

    public async Task EnsureProfileStorageInitializedAsync(string profileId)
    {
        var profileDirectory = GetProfileDirectory(profileId);
        Directory.CreateDirectory(profileDirectory);

        await EnsureJsonArrayFileExistsAsync(GetOperatorsPath(profileId));
        await EnsureJsonArrayFileExistsAsync(GetActiveServicePath(profileId));
        await EnsureJsonArrayFileExistsAsync(GetContractsPath(profileId));
        await EnsureJsonArrayFileExistsAsync(GetMedicalPath(profileId));
        await EnsureJsonArrayFileExistsAsync(GetContactsPath(profileId));
        await EnsureJsonArrayFileExistsAsync(GetCareerRaidLedgerPath(profileId));
        await EnsureBillingLedgerExistsAsync(GetBillingLedgerPath(profileId));
    }

    public async Task<VanguardOperatorStorageState> LoadStateAsync(string profileId)
    {
        await EnsureProfileStorageInitializedAsync(profileId);
        return new VanguardOperatorStorageState(
            await LoadOperatorsAsync(profileId),
            await LoadActiveServiceAsync(profileId),
            await LoadContractsAsync(profileId),
            await LoadMedicalAsync(profileId),
            await LoadContactsAsync(profileId),
            await LoadBillingLedgerAsync(profileId));
    }

    public async Task<IReadOnlyList<VanguardOperatorProfile>> LoadOperatorsAsync(string profileId)
    {
        var loaded = await LoadListAsync<VanguardOperatorProfile>(GetOperatorsPath(profileId));
        if (loaded.Count == 0)
        {
            return loaded;
        }

        bool changed = false;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var normalized = loaded.Select(profile =>
        {
            var migrated = VanguardOperatorProfileMigrator.Normalize(profile, now, out bool profileChanged);
            changed |= profileChanged;
            return migrated;
        }).ToArray();

        if (changed)
        {
            TryCreateProfileNormalizationBackup(profileId);
            await SaveOperatorsAsync(profileId, normalized);
        }

        return normalized;
    }

    public Task SaveOperatorsAsync(string profileId, IReadOnlyList<VanguardOperatorProfile> operators) =>
        SaveListAsync(GetOperatorsPath(profileId), operators);

    public async Task<VanguardOperatorProfilesAtomicWriteResult> CommitOperatorProfilesAtomicAsync(
        string profileId,
        IReadOnlyList<VanguardOperatorProfile> expectedBefore,
        IReadOnlyList<VanguardOperatorProfile> after)
    {
        string path = GetOperatorsPath(profileId);
        try
        {
            if (!File.Exists(path))
            {
                return new VanguardOperatorProfilesAtomicWriteResult(false, "operators_file_missing", false);
            }

            IReadOnlyList<VanguardOperatorProfile> current = await LoadListAsync<VanguardOperatorProfile>(path);
            if (!JsonEquivalent(current, expectedBefore))
            {
                return new VanguardOperatorProfilesAtomicWriteResult(false, "operators_changed_since_read", false);
            }

            await SaveListAtomicAsync(path, after);
            IReadOnlyList<VanguardOperatorProfile> readBack = await LoadListAsync<VanguardOperatorProfile>(path);
            if (!JsonEquivalent(readBack, after))
            {
                return new VanguardOperatorProfilesAtomicWriteResult(false, "operators_readback_mismatch", false);
            }

            return new VanguardOperatorProfilesAtomicWriteResult(true, "operators_committed_readback_verified", true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new VanguardOperatorProfilesAtomicWriteResult(false, "exception_" + exception.GetType().Name, false);
        }
    }

    public Task SaveOperatorsAtomicAsync(string profileId, IReadOnlyList<VanguardOperatorProfile> operators) =>
        SaveListAtomicAsync(GetOperatorsPath(profileId), operators);

    public async Task<VanguardOperatorVisualIdentityMigrationWriteResult> CommitWorldVisualIdentityMigrationAsync(
        string profileId,
        IReadOnlyList<VanguardOperatorProfile> expectedBefore,
        IReadOnlyList<VanguardOperatorProfile> after)
    {
        string path = GetOperatorsPath(profileId);
        string backupPath = Path.Combine(GetProfileDirectory(profileId), VisualIdentityMigrationBackupFileName);
        string transientPath = path + ".visual-identity-migration-" + Guid.NewGuid().ToString("N") + ".rollback";
        bool permanentBackupCreated = false;
        bool transientCaptured = false;
        try
        {
            if (!File.Exists(path))
            {
                return new VanguardOperatorVisualIdentityMigrationWriteResult(false, "operators_file_missing", false, false, false);
            }

            IReadOnlyList<VanguardOperatorProfile> current = await LoadListAsync<VanguardOperatorProfile>(path);
            if (!JsonEquivalent(current, expectedBefore))
            {
                return new VanguardOperatorVisualIdentityMigrationWriteResult(false, "operators_changed_since_visual_migration_read", File.Exists(backupPath), false, false);
            }

            if (!File.Exists(backupPath))
            {
                File.Copy(path, backupPath, overwrite: false);
                permanentBackupCreated = true;
            }

            if (!File.Exists(backupPath) || (permanentBackupCreated && !FilesEqual(path, backupPath)))
            {
                return new VanguardOperatorVisualIdentityMigrationWriteResult(false, "permanent_backup_verification_failed", File.Exists(backupPath), false, false);
            }

            File.Copy(path, transientPath, overwrite: false);
            transientCaptured = true;
            await SaveListAtomicAsync(path, after);

            IReadOnlyList<VanguardOperatorProfile> readBack = await LoadListAsync<VanguardOperatorProfile>(path);
            if (!JsonEquivalent(readBack, after))
            {
                File.Copy(transientPath, path, overwrite: true);
                return new VanguardOperatorVisualIdentityMigrationWriteResult(false, "readback_mismatch_rollback_restored", true, false, true);
            }

            return new VanguardOperatorVisualIdentityMigrationWriteResult(true, "committed_readback_verified", true, true, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            bool rolledBack = false;
            try
            {
                if (transientCaptured && File.Exists(transientPath))
                {
                    File.Copy(transientPath, path, overwrite: true);
                    rolledBack = true;
                }
            }
            catch
            {
                rolledBack = false;
            }

            return new VanguardOperatorVisualIdentityMigrationWriteResult(
                false,
                "exception_" + exception.GetType().Name,
                File.Exists(backupPath),
                false,
                rolledBack);
        }
        finally
        {
            try
            {
                if (File.Exists(transientPath)) File.Delete(transientPath);
            }
            catch
            {
                // A stale transient rollback file is safer than hiding a failed cleanup.
            }
        }
    }

    public bool HasVisualIdentityMigrationBackup(string profileId) =>
        File.Exists(Path.Combine(GetProfileDirectory(profileId), VisualIdentityMigrationBackupFileName));


    public async Task<VanguardOperatorEnrolledVisualIdentityMigrationWriteResult> CommitEnrolledVisualIdentityMigrationAsync(
        string profileId,
        IReadOnlyList<VanguardOperatorProfile> expectedBefore,
        IReadOnlyList<VanguardOperatorProfile> after,
        IReadOnlyList<VanguardOperatorInventoryProfileVisualMutation> profileMutations)
    {
        string operatorsPath = GetOperatorsPath(profileId);
        string operatorsBackupPath = Path.Combine(GetProfileDirectory(profileId), VisualIdentityMigrationBackupFileName);
        string transactionId = Guid.NewGuid().ToString("N");
        string operatorsTransientPath = operatorsPath + ".visual-identity-enrolled-" + transactionId + ".rollback";
        var profileTransientPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool operatorsTransientCaptured = false;
        int permanentInventoryBackups = 0;
        try
        {
            if (profileMutations.Count == 0)
            {
                return new VanguardOperatorEnrolledVisualIdentityMigrationWriteResult(false, "inventory_profile_mutations_empty", File.Exists(operatorsBackupPath), 0, false, false);
            }

            if (!File.Exists(operatorsPath))
            {
                return new VanguardOperatorEnrolledVisualIdentityMigrationWriteResult(false, "operators_file_missing", false, 0, false, false);
            }

            IReadOnlyList<VanguardOperatorProfile> currentOperators = await LoadListAsync<VanguardOperatorProfile>(operatorsPath);
            if (!JsonEquivalent(currentOperators, expectedBefore))
            {
                return new VanguardOperatorEnrolledVisualIdentityMigrationWriteResult(false, "operators_changed_since_visual_migration_read", File.Exists(operatorsBackupPath), 0, false, false);
            }

            foreach (VanguardOperatorInventoryProfileVisualMutation mutation in profileMutations)
            {
                string profilePath = GetOperatorInventoryProfileReadPath(profileId, mutation.OperatorId);
                if (!File.Exists(profilePath))
                {
                    return new VanguardOperatorEnrolledVisualIdentityMigrationWriteResult(false, "inventory_profile_missing_" + mutation.OperatorId, File.Exists(operatorsBackupPath), permanentInventoryBackups, false, false);
                }

                string currentJson = await File.ReadAllTextAsync(profilePath);
                if (!string.Equals(currentJson, mutation.ExpectedBeforeJson, StringComparison.Ordinal))
                {
                    return new VanguardOperatorEnrolledVisualIdentityMigrationWriteResult(false, "inventory_profile_changed_since_visual_migration_read_" + mutation.OperatorId, File.Exists(operatorsBackupPath), permanentInventoryBackups, false, false);
                }
            }

            if (!File.Exists(operatorsBackupPath))
            {
                File.Copy(operatorsPath, operatorsBackupPath, overwrite: false);
                if (!FilesEqual(operatorsPath, operatorsBackupPath))
                {
                    return new VanguardOperatorEnrolledVisualIdentityMigrationWriteResult(false, "operators_permanent_backup_verification_failed", true, permanentInventoryBackups, false, false);
                }
            }

            foreach (VanguardOperatorInventoryProfileVisualMutation mutation in profileMutations)
            {
                string profilePath = GetOperatorInventoryProfileReadPath(profileId, mutation.OperatorId);
                string backupPath = profilePath + ".pre-visual-identity-migration.json";
                if (!File.Exists(backupPath))
                {
                    File.Copy(profilePath, backupPath, overwrite: false);
                    if (!FilesEqual(profilePath, backupPath))
                    {
                        return new VanguardOperatorEnrolledVisualIdentityMigrationWriteResult(false, "inventory_permanent_backup_verification_failed_" + mutation.OperatorId, true, permanentInventoryBackups, false, false);
                    }
                }

                permanentInventoryBackups++;
            }

            File.Copy(operatorsPath, operatorsTransientPath, overwrite: false);
            operatorsTransientCaptured = true;
            foreach (VanguardOperatorInventoryProfileVisualMutation mutation in profileMutations)
            {
                string profilePath = GetOperatorInventoryProfileReadPath(profileId, mutation.OperatorId);
                string transientPath = profilePath + ".visual-identity-enrolled-" + transactionId + ".rollback";
                File.Copy(profilePath, transientPath, overwrite: false);
                profileTransientPaths[profilePath] = transientPath;
            }

            foreach (VanguardOperatorInventoryProfileVisualMutation mutation in profileMutations)
            {
                string profilePath = GetOperatorInventoryProfileReadPath(profileId, mutation.OperatorId);
                string currentJson = await File.ReadAllTextAsync(profilePath);
                if (!string.Equals(currentJson, mutation.ExpectedBeforeJson, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("inventory_profile_changed_before_write_" + mutation.OperatorId);
                }

                await SaveTextAtomicAsync(profilePath, mutation.AfterJson);
            }

            IReadOnlyList<VanguardOperatorProfile> currentBeforeOperatorsWrite = await LoadListAsync<VanguardOperatorProfile>(operatorsPath);
            if (!JsonEquivalent(currentBeforeOperatorsWrite, expectedBefore))
            {
                throw new InvalidOperationException("operators_changed_before_write");
            }

            await SaveListAtomicAsync(operatorsPath, after);

            IReadOnlyList<VanguardOperatorProfile> operatorsReadBack = await LoadListAsync<VanguardOperatorProfile>(operatorsPath);
            if (!JsonEquivalent(operatorsReadBack, after))
            {
                throw new InvalidOperationException("operators_readback_mismatch");
            }

            foreach (VanguardOperatorInventoryProfileVisualMutation mutation in profileMutations)
            {
                string profilePath = GetOperatorInventoryProfileReadPath(profileId, mutation.OperatorId);
                string readBackJson = await File.ReadAllTextAsync(profilePath);
                if (!string.Equals(readBackJson, mutation.AfterJson, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("inventory_profile_readback_mismatch_" + mutation.OperatorId);
                }
            }

            return new VanguardOperatorEnrolledVisualIdentityMigrationWriteResult(true, "committed_readback_verified", true, permanentInventoryBackups, true, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            bool rollback = true;
            try
            {
                foreach ((string profilePath, string transientPath) in profileTransientPaths)
                {
                    if (!File.Exists(transientPath))
                    {
                        rollback = false;
                        continue;
                    }
                    File.Copy(transientPath, profilePath, overwrite: true);
                    if (!FilesEqual(transientPath, profilePath)) rollback = false;
                }

                if (operatorsTransientCaptured && File.Exists(operatorsTransientPath))
                {
                    File.Copy(operatorsTransientPath, operatorsPath, overwrite: true);
                    if (!FilesEqual(operatorsTransientPath, operatorsPath)) rollback = false;
                }
                else
                {
                    rollback = false;
                }
            }
            catch
            {
                rollback = false;
            }

            return new VanguardOperatorEnrolledVisualIdentityMigrationWriteResult(
                false,
                "exception_" + exception.GetType().Name + "_" + SanitizeReason(exception.Message),
                File.Exists(operatorsBackupPath),
                permanentInventoryBackups,
                false,
                rollback);
        }
        finally
        {
            try
            {
                if (File.Exists(operatorsTransientPath)) File.Delete(operatorsTransientPath);
                foreach (string transientPath in profileTransientPaths.Values)
                {
                    if (File.Exists(transientPath)) File.Delete(transientPath);
                }
            }
            catch
            {
                // Stale transient rollback material is safer than hiding failed cleanup.
            }
        }
    }

    public bool HasEnrollmentBaselineMigrationBackup(string profileId) =>
        File.Exists(Path.Combine(GetProfileDirectory(profileId), EnrollmentBaselineMigrationBackupFileName));

    public async Task<VanguardOperatorEnrollmentMigrationWriteResult> CommitEnrollmentBaselineMigrationAsync(
        string profileId,
        IReadOnlyList<VanguardOperatorProfile> expectedBefore,
        IReadOnlyList<VanguardOperatorProfile> after,
        IReadOnlyList<VanguardOperatorInventoryProfileEnrollmentMutation> profileMutations)
    {
        string operatorsPath = GetOperatorsPath(profileId);
        string operatorsBackupPath = Path.Combine(GetProfileDirectory(profileId), EnrollmentBaselineMigrationBackupFileName);
        string transactionId = Guid.NewGuid().ToString("N");
        string operatorsTransientPath = operatorsPath + ".enrollment-baseline-v1-" + transactionId + ".rollback";
        var profileTransientPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool operatorsTransientCaptured = false;
        int permanentInventoryBackups = 0;
        try
        {
            if (profileMutations.Count == 0)
            {
                return new VanguardOperatorEnrollmentMigrationWriteResult(false, "inventory_profile_mutations_empty", File.Exists(operatorsBackupPath), 0, false, false);
            }

            if (!File.Exists(operatorsPath))
            {
                return new VanguardOperatorEnrollmentMigrationWriteResult(false, "operators_file_missing", false, 0, false, false);
            }

            IReadOnlyList<VanguardOperatorProfile> currentOperators = await LoadListAsync<VanguardOperatorProfile>(operatorsPath);
            if (!JsonEquivalent(currentOperators, expectedBefore))
            {
                return new VanguardOperatorEnrollmentMigrationWriteResult(false, "operators_changed_since_enrollment_migration_read", File.Exists(operatorsBackupPath), 0, false, false);
            }

            foreach (VanguardOperatorInventoryProfileEnrollmentMutation mutation in profileMutations)
            {
                string profilePath = GetOperatorInventoryProfileReadPath(profileId, mutation.OperatorId);
                if (!File.Exists(profilePath))
                {
                    return new VanguardOperatorEnrollmentMigrationWriteResult(false, "inventory_profile_missing_" + mutation.OperatorId, File.Exists(operatorsBackupPath), permanentInventoryBackups, false, false);
                }

                string currentJson = await File.ReadAllTextAsync(profilePath);
                if (!string.Equals(currentJson, mutation.ExpectedBeforeJson, StringComparison.Ordinal))
                {
                    return new VanguardOperatorEnrollmentMigrationWriteResult(false, "inventory_profile_changed_since_enrollment_migration_read_" + mutation.OperatorId, File.Exists(operatorsBackupPath), permanentInventoryBackups, false, false);
                }
            }

            if (!File.Exists(operatorsBackupPath))
            {
                File.Copy(operatorsPath, operatorsBackupPath, overwrite: false);
                if (!FilesEqual(operatorsPath, operatorsBackupPath))
                {
                    return new VanguardOperatorEnrollmentMigrationWriteResult(false, "operators_permanent_backup_verification_failed", true, permanentInventoryBackups, false, false);
                }
            }
            else
            {
                try
                {
                    _ = await LoadListAsync<VanguardOperatorProfile>(operatorsBackupPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    return new VanguardOperatorEnrollmentMigrationWriteResult(false, "operators_permanent_backup_unreadable", true, permanentInventoryBackups, false, false);
                }
            }

            foreach (VanguardOperatorInventoryProfileEnrollmentMutation mutation in profileMutations)
            {
                string profilePath = GetOperatorInventoryProfileReadPath(profileId, mutation.OperatorId);
                string backupPath = profilePath + ".pre-enrollment-baseline-v1-migration.json";
                if (!File.Exists(backupPath))
                {
                    File.Copy(profilePath, backupPath, overwrite: false);
                    if (!FilesEqual(profilePath, backupPath))
                    {
                        return new VanguardOperatorEnrollmentMigrationWriteResult(false, "inventory_permanent_backup_verification_failed_" + mutation.OperatorId, true, permanentInventoryBackups, false, false);
                    }
                }
                else if (!FilesEqual(profilePath, backupPath))
                {
                    return new VanguardOperatorEnrollmentMigrationWriteResult(false, "inventory_permanent_backup_conflict_" + mutation.OperatorId, true, permanentInventoryBackups, false, false);
                }

                permanentInventoryBackups++;
            }

            File.Copy(operatorsPath, operatorsTransientPath, overwrite: false);
            operatorsTransientCaptured = true;
            foreach (VanguardOperatorInventoryProfileEnrollmentMutation mutation in profileMutations)
            {
                string profilePath = GetOperatorInventoryProfileReadPath(profileId, mutation.OperatorId);
                string transientPath = profilePath + ".enrollment-baseline-v1-" + transactionId + ".rollback";
                File.Copy(profilePath, transientPath, overwrite: false);
                profileTransientPaths[profilePath] = transientPath;
            }

            foreach (VanguardOperatorInventoryProfileEnrollmentMutation mutation in profileMutations)
            {
                string profilePath = GetOperatorInventoryProfileReadPath(profileId, mutation.OperatorId);
                string currentJson = await File.ReadAllTextAsync(profilePath);
                if (!string.Equals(currentJson, mutation.ExpectedBeforeJson, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("inventory_profile_changed_before_enrollment_write_" + mutation.OperatorId);
                }

                await SaveTextAtomicAsync(profilePath, mutation.AfterJson);
            }

            IReadOnlyList<VanguardOperatorProfile> currentBeforeOperatorsWrite = await LoadListAsync<VanguardOperatorProfile>(operatorsPath);
            if (!JsonEquivalent(currentBeforeOperatorsWrite, expectedBefore))
            {
                throw new InvalidOperationException("operators_changed_before_enrollment_write");
            }

            await SaveListAtomicAsync(operatorsPath, after);

            IReadOnlyList<VanguardOperatorProfile> operatorsReadBack = await LoadListAsync<VanguardOperatorProfile>(operatorsPath);
            if (!JsonEquivalent(operatorsReadBack, after))
            {
                throw new InvalidOperationException("operators_enrollment_readback_mismatch");
            }

            foreach (VanguardOperatorInventoryProfileEnrollmentMutation mutation in profileMutations)
            {
                string profilePath = GetOperatorInventoryProfileReadPath(profileId, mutation.OperatorId);
                string readBackJson = await File.ReadAllTextAsync(profilePath);
                if (!string.Equals(readBackJson, mutation.AfterJson, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("inventory_profile_enrollment_readback_mismatch_" + mutation.OperatorId);
                }
            }

            return new VanguardOperatorEnrollmentMigrationWriteResult(true, "committed_readback_verified", true, permanentInventoryBackups, true, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            bool rollback = true;
            try
            {
                foreach ((string profilePath, string transientPath) in profileTransientPaths)
                {
                    if (!File.Exists(transientPath))
                    {
                        rollback = false;
                        continue;
                    }

                    File.Copy(transientPath, profilePath, overwrite: true);
                    if (!FilesEqual(transientPath, profilePath)) rollback = false;
                }

                if (operatorsTransientCaptured && File.Exists(operatorsTransientPath))
                {
                    File.Copy(operatorsTransientPath, operatorsPath, overwrite: true);
                    if (!FilesEqual(operatorsTransientPath, operatorsPath)) rollback = false;
                }
                else
                {
                    rollback = false;
                }
            }
            catch
            {
                rollback = false;
            }

            return new VanguardOperatorEnrollmentMigrationWriteResult(
                false,
                "exception_" + exception.GetType().Name + "_" + SanitizeReason(exception.Message),
                File.Exists(operatorsBackupPath),
                permanentInventoryBackups,
                false,
                rollback);
        }
        finally
        {
            try
            {
                if (File.Exists(operatorsTransientPath)) File.Delete(operatorsTransientPath);
                foreach (string transientPath in profileTransientPaths.Values)
                {
                    if (File.Exists(transientPath)) File.Delete(transientPath);
                }
            }
            catch
            {
                // Stale transient rollback material is safer than hiding failed cleanup.
            }
        }
    }

    public async Task<VanguardOperatorExperienceReconciliationWriteResult> CommitExperienceReconciliationAsync(
        string profileId,
        IReadOnlyList<VanguardOperatorProfile> expectedBefore,
        IReadOnlyList<VanguardOperatorProfile> after)
    {
        string path = GetOperatorsPath(profileId);
        string backupPath = ResolveExperienceReconciliationBackupPath(profileId);
        string transientPath = path + ".xp-reconciliation-" + Guid.NewGuid().ToString("N") + ".rollback";
        bool permanentBackupCreated = false;
        bool transientCaptured = false;
        try
        {
            if (!File.Exists(path))
            {
                return new VanguardOperatorExperienceReconciliationWriteResult(false, "operators_file_missing", false, false, false);
            }

            IReadOnlyList<VanguardOperatorProfile> current = await LoadListAsync<VanguardOperatorProfile>(path);
            if (!JsonEquivalent(current, expectedBefore))
            {
                return new VanguardOperatorExperienceReconciliationWriteResult(false, "operators_changed_since_reconciliation_read", File.Exists(backupPath), false, false);
            }

            if (!File.Exists(backupPath))
            {
                File.Copy(path, backupPath, overwrite: false);
                permanentBackupCreated = true;
            }

            if (!File.Exists(backupPath) || (permanentBackupCreated && !FilesEqual(path, backupPath)))
            {
                return new VanguardOperatorExperienceReconciliationWriteResult(false, "permanent_backup_verification_failed", File.Exists(backupPath), false, false);
            }

            File.Copy(path, transientPath, overwrite: false);
            transientCaptured = true;
            await SaveListAtomicAsync(path, after);

            IReadOnlyList<VanguardOperatorProfile> readBack = await LoadListAsync<VanguardOperatorProfile>(path);
            if (!JsonEquivalent(readBack, after))
            {
                File.Copy(transientPath, path, overwrite: true);
                return new VanguardOperatorExperienceReconciliationWriteResult(false, "readback_mismatch_rollback_restored", true, false, true);
            }

            return new VanguardOperatorExperienceReconciliationWriteResult(true, "committed_readback_verified", true, true, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            bool rolledBack = false;
            try
            {
                if (transientCaptured && File.Exists(transientPath))
                {
                    File.Copy(transientPath, path, overwrite: true);
                    rolledBack = true;
                }
            }
            catch
            {
                rolledBack = false;
            }

            return new VanguardOperatorExperienceReconciliationWriteResult(
                false,
                "exception_" + exception.GetType().Name,
                File.Exists(backupPath),
                false,
                rolledBack);
        }
        finally
        {
            try
            {
                if (File.Exists(transientPath))
                {
                    File.Delete(transientPath);
                }
            }
            catch
            {
                // A stale transient rollback file is safer than hiding a failed cleanup.
            }
        }
    }

    public Task<IReadOnlyList<VanguardOperatorIdentityReservation>> LoadIdentityRegistryAsync() =>
        LoadListAsync<VanguardOperatorIdentityReservation>(GetIdentityRegistryPath());

    public Task SaveIdentityRegistryAsync(IReadOnlyList<VanguardOperatorIdentityReservation> reservations) =>
        SaveListAsync(GetIdentityRegistryPath(), reservations);

    public bool HasProfileNormalizationBackup(string profileId) =>
        File.Exists(Path.Combine(GetProfileDirectory(profileId), ProfileNormalizationBackupFileName))
        || HasLegacyBackupMatching(profileId, "operators.pre-*.json", "xp-reconciliation");

    public bool HasExperienceReconciliationBackup(string profileId) =>
        File.Exists(Path.Combine(GetProfileDirectory(profileId), ExperienceReconciliationBackupFileName))
        || HasLegacyBackupMatching(profileId, "operators.pre-*-xp-reconciliation-*.json", null);

    private string ResolveExperienceReconciliationBackupPath(string profileId)
    {
        string directory = GetProfileDirectory(profileId);
        string canonicalPath = Path.Combine(directory, ExperienceReconciliationBackupFileName);
        if (File.Exists(canonicalPath)) return canonicalPath;

        try
        {
            string? compatiblePath = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "operators.pre-*-xp-reconciliation-*.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()
                : null;
            return compatiblePath ?? canonicalPath;
        }
        catch
        {
            // Falling back to the canonical target preserves the original fail-safe write behavior.
            return canonicalPath;
        }
    }

    private bool HasLegacyBackupMatching(string profileId, string searchPattern, string? excludedToken)
    {
        try
        {
            string directory = GetProfileDirectory(profileId);
            if (!Directory.Exists(directory)) return false;
            return Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly)
                .Any(path => excludedToken is null || path.IndexOf(excludedToken, StringComparison.OrdinalIgnoreCase) < 0);
        }
        catch
        {
            // Backup discovery is diagnostic only; storage availability remains authoritative.
            return false;
        }
    }

    public bool HasOperatorStore(string profileId) => File.Exists(GetOperatorsPath(profileId));

    // Strict read-only preview transport must not reuse LoadListAsync because that general reader
    // quarantines malformed files. These methods deliberately perform no initialization, repair,
    // normalization, quarantine, directory creation or persistence side effect.
    public Task<string?> ReadOperatorsJsonReadOnlyAsync(string profileId) =>
        ReadExistingTextReadOnlyAsync(GetOperatorsPath(profileId));

    public Task<string?> ReadActiveServiceJsonReadOnlyAsync(string profileId) =>
        ReadExistingTextReadOnlyAsync(GetActiveServicePath(profileId));

    public Task<string?> ReadOperatorInventoryProfileJsonReadOnlyAsync(string profileId, string operatorId) =>
        ReadExistingTextReadOnlyAsync(GetOperatorInventoryProfileReadPath(profileId, operatorId));

    public Task<IReadOnlyList<VanguardActiveServiceRecord>> LoadActiveServiceAsync(string profileId) =>
        LoadListAsync<VanguardActiveServiceRecord>(GetActiveServicePath(profileId));

    public async Task SaveActiveServiceAsync(string profileId, IReadOnlyList<VanguardActiveServiceRecord> activeService)
    {
        await activeServiceMutationGate.WaitAsync();
        try
        {
            await SaveListAsync(GetActiveServicePath(profileId), activeService);
        }
        finally
        {
            activeServiceMutationGate.Release();
        }
    }

    public async Task<VanguardActiveServiceAtomicWriteResult> CommitActiveServiceAtomicAsync(
        string profileId,
        IReadOnlyList<VanguardActiveServiceRecord> expectedBefore,
        IReadOnlyList<VanguardActiveServiceRecord> after)
    {
        await activeServiceMutationGate.WaitAsync();
        string path = GetActiveServicePath(profileId);
        string rollbackPath = path + ".active-service-" + Guid.NewGuid().ToString("N") + ".rollback";
        bool rollbackMaterialCaptured = false;
        bool mutationAttempted = false;
        try
        {
            if (!File.Exists(path))
            {
                return new VanguardActiveServiceAtomicWriteResult(false, "active_service_file_missing", false);
            }

            IReadOnlyList<VanguardActiveServiceRecord> current = await LoadListAsync<VanguardActiveServiceRecord>(path);
            if (!JsonEquivalent(current, expectedBefore))
            {
                return new VanguardActiveServiceAtomicWriteResult(false, "active_service_changed_since_read", false);
            }

            File.Copy(path, rollbackPath, overwrite: false);
            rollbackMaterialCaptured = true;
            mutationAttempted = true;
            await SaveListAtomicAsync(path, after);
            IReadOnlyList<VanguardActiveServiceRecord> readBack = await LoadListAsync<VanguardActiveServiceRecord>(path);
            if (!JsonEquivalent(readBack, after))
            {
                bool rolledBack = RestoreSingleRollbackFile(rollbackPath, path);
                return new VanguardActiveServiceAtomicWriteResult(
                    false,
                    rolledBack ? "active_service_readback_mismatch_rolled_back" : "active_service_readback_mismatch_rollback_failed",
                    false);
            }

            return new VanguardActiveServiceAtomicWriteResult(true, "active_service_committed_readback_verified", true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            bool rolledBack = rollbackMaterialCaptured && mutationAttempted && RestoreSingleRollbackFile(rollbackPath, path);
            string suffix = rollbackMaterialCaptured && mutationAttempted
                ? rolledBack ? "_rolled_back" : "_rollback_failed"
                : string.Empty;
            return new VanguardActiveServiceAtomicWriteResult(false, "exception_" + exception.GetType().Name + suffix, false);
        }
        finally
        {
            try
            {
                if (File.Exists(rollbackPath)) File.Delete(rollbackPath);
            }
            catch
            {
                // A stale rollback copy is preferable to hiding a failed cleanup.
            }

            activeServiceMutationGate.Release();
        }
    }

    private static bool RestoreSingleRollbackFile(string rollbackPath, string targetPath)
    {
        try
        {
            if (!File.Exists(rollbackPath))
            {
                return false;
            }

            File.Copy(rollbackPath, targetPath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<VanguardOperatorReleaseWriteResult> CommitReleaseFromServiceAsync(
        string profileId,
        IReadOnlyList<VanguardOperatorProfile> expectedOperators,
        IReadOnlyList<VanguardOperatorProfile> afterOperators,
        IReadOnlyList<VanguardActiveServiceRecord> expectedActiveService,
        IReadOnlyList<VanguardActiveServiceRecord> afterActiveService,
        IReadOnlyList<VanguardOperatorContactRecord> expectedContacts,
        IReadOnlyList<VanguardOperatorContactRecord> afterContacts)
    {
        await activeServiceMutationGate.WaitAsync();
        string transactionId = Guid.NewGuid().ToString("N");
        string operatorsPath = GetOperatorsPath(profileId);
        string activeServicePath = GetActiveServicePath(profileId);
        string contactsPath = GetContactsPath(profileId);
        string operatorsRollbackPath = operatorsPath + ".release-" + transactionId + ".rollback";
        string activeServiceRollbackPath = activeServicePath + ".release-" + transactionId + ".rollback";
        string contactsRollbackPath = contactsPath + ".release-" + transactionId + ".rollback";
        bool rollbackMaterialCaptured = false;
        try
        {
            if (!File.Exists(operatorsPath) || !File.Exists(activeServicePath) || !File.Exists(contactsPath))
            {
                return new VanguardOperatorReleaseWriteResult(false, "release_store_file_missing", false, false);
            }

            IReadOnlyList<VanguardOperatorProfile> currentOperators = await LoadListAsync<VanguardOperatorProfile>(operatorsPath);
            IReadOnlyList<VanguardActiveServiceRecord> currentActiveService = await LoadListAsync<VanguardActiveServiceRecord>(activeServicePath);
            IReadOnlyList<VanguardOperatorContactRecord> currentContacts = await LoadListAsync<VanguardOperatorContactRecord>(contactsPath);
            if (!JsonEquivalent(currentOperators, expectedOperators))
            {
                return new VanguardOperatorReleaseWriteResult(false, "operators_changed_since_release_read", false, false);
            }
            if (!JsonEquivalent(currentActiveService, expectedActiveService))
            {
                return new VanguardOperatorReleaseWriteResult(false, "active_service_changed_since_release_read", false, false);
            }
            if (!JsonEquivalent(currentContacts, expectedContacts))
            {
                return new VanguardOperatorReleaseWriteResult(false, "contacts_changed_since_release_read", false, false);
            }

            File.Copy(operatorsPath, operatorsRollbackPath, overwrite: false);
            File.Copy(activeServicePath, activeServiceRollbackPath, overwrite: false);
            File.Copy(contactsPath, contactsRollbackPath, overwrite: false);
            rollbackMaterialCaptured = true;

            await SaveListAtomicAsync(operatorsPath, afterOperators);
            await SaveListAtomicAsync(activeServicePath, afterActiveService);
            await SaveListAtomicAsync(contactsPath, afterContacts);

            IReadOnlyList<VanguardOperatorProfile> operatorsReadBack = await LoadListAsync<VanguardOperatorProfile>(operatorsPath);
            IReadOnlyList<VanguardActiveServiceRecord> activeServiceReadBack = await LoadListAsync<VanguardActiveServiceRecord>(activeServicePath);
            IReadOnlyList<VanguardOperatorContactRecord> contactsReadBack = await LoadListAsync<VanguardOperatorContactRecord>(contactsPath);
            if (!JsonEquivalent(operatorsReadBack, afterOperators)
                || !JsonEquivalent(activeServiceReadBack, afterActiveService)
                || !JsonEquivalent(contactsReadBack, afterContacts))
            {
                bool rolledBack = RestoreReleaseRollbackFiles(
                    operatorsRollbackPath, operatorsPath,
                    activeServiceRollbackPath, activeServicePath,
                    contactsRollbackPath, contactsPath);
                return new VanguardOperatorReleaseWriteResult(false, "release_readback_mismatch", false, rolledBack);
            }

            return new VanguardOperatorReleaseWriteResult(true, "release_committed_readback_verified", true, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            bool rolledBack = rollbackMaterialCaptured && RestoreReleaseRollbackFiles(
                operatorsRollbackPath, operatorsPath,
                activeServiceRollbackPath, activeServicePath,
                contactsRollbackPath, contactsPath);
            return new VanguardOperatorReleaseWriteResult(false, "exception_" + exception.GetType().Name, false, rolledBack);
        }
        finally
        {
            try
            {
                if (File.Exists(operatorsRollbackPath)) File.Delete(operatorsRollbackPath);
                if (File.Exists(activeServiceRollbackPath)) File.Delete(activeServiceRollbackPath);
                if (File.Exists(contactsRollbackPath)) File.Delete(contactsRollbackPath);
            }
            catch
            {
                // Stale rollback material is safer than hiding a failed cleanup.
            }

            activeServiceMutationGate.Release();
        }
    }

    private static bool RestoreReleaseRollbackFiles(
        string operatorsRollbackPath,
        string operatorsPath,
        string activeServiceRollbackPath,
        string activeServicePath,
        string contactsRollbackPath,
        string contactsPath)
    {
        try
        {
            if (!File.Exists(operatorsRollbackPath)
                || !File.Exists(activeServiceRollbackPath)
                || !File.Exists(contactsRollbackPath))
            {
                return false;
            }

            File.Copy(operatorsRollbackPath, operatorsPath, overwrite: true);
            File.Copy(activeServiceRollbackPath, activeServicePath, overwrite: true);
            File.Copy(contactsRollbackPath, contactsPath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task<IReadOnlyList<VanguardOperatorContractOffer>> LoadContractsAsync(string profileId) =>
        LoadListAsync<VanguardOperatorContractOffer>(GetContractsPath(profileId));

    public Task SaveContractsAsync(string profileId, IReadOnlyList<VanguardOperatorContractOffer> contracts) =>
        SaveListAsync(GetContractsPath(profileId), contracts);

    public Task<IReadOnlyList<VanguardOperatorMedicalRecord>> LoadMedicalAsync(string profileId) =>
        LoadListAsync<VanguardOperatorMedicalRecord>(GetMedicalPath(profileId));

    public Task SaveMedicalAsync(string profileId, IReadOnlyList<VanguardOperatorMedicalRecord> medical) =>
        SaveListAsync(GetMedicalPath(profileId), medical);

    public Task<IReadOnlyList<VanguardOperatorContactRecord>> LoadContactsAsync(string profileId) =>
        LoadListAsync<VanguardOperatorContactRecord>(GetContactsPath(profileId));

    public Task SaveContactsAsync(string profileId, IReadOnlyList<VanguardOperatorContactRecord> contacts) =>
        SaveListAsync(GetContactsPath(profileId), contacts);

    public async Task<IReadOnlyList<VanguardCareerRaidLedgerEntry>> LoadCareerRaidLedgerAsync(string profileId) =>
        (await LoadCareerRaidLedgerSnapshotAsync(profileId)).Entries;

    public async Task<VanguardCareerRaidLedgerReadSnapshot> LoadCareerRaidLedgerSnapshotAsync(string profileId)
    {
        string path = GetCareerRaidLedgerPath(profileId);
        bool quarantineEvidence = HasQuarantineEvidence(path);
        if (!File.Exists(path))
        {
            return new VanguardCareerRaidLedgerReadSnapshot(
                quarantineEvidence ? "missing_with_quarantine_evidence" : "missing",
                Array.Empty<VanguardCareerRaidLedgerEntry>(),
                false,
                quarantineEvidence);
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var entries = await JsonSerializer.DeserializeAsync<List<VanguardCareerRaidLedgerEntry>>(stream, SerializerOptions)
                ?? new List<VanguardCareerRaidLedgerEntry>();
            return new VanguardCareerRaidLedgerReadSnapshot(
                quarantineEvidence ? "readable_with_quarantine_evidence" : "readable",
                entries,
                true,
                quarantineEvidence);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            bool quarantined = QuarantineUnreadableStoreFile(path);
            return new VanguardCareerRaidLedgerReadSnapshot(
                quarantined ? "unreadable_quarantined" : "unreadable_quarantine_failed",
                Array.Empty<VanguardCareerRaidLedgerEntry>(),
                !quarantined && File.Exists(path),
                quarantined || quarantineEvidence || HasQuarantineEvidence(path));
        }
    }

    public Task SaveCareerRaidLedgerAtomicAsync(string profileId, IReadOnlyList<VanguardCareerRaidLedgerEntry> entries) =>
        SaveListAtomicAsync(GetCareerRaidLedgerPath(profileId), entries);

    public async Task<VanguardOperatorBillingLedger> LoadBillingLedgerAsync(string profileId)
    {
        var path = GetBillingLedgerPath(profileId);
        if (!File.Exists(path))
        {
            var ledger = VanguardOperatorBillingLedger.Empty(DateTimeOffset.UtcNow);
            await SaveBillingLedgerAsync(profileId, ledger);
            return ledger;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<VanguardOperatorBillingLedger>(stream, SerializerOptions)
                ?? VanguardOperatorBillingLedger.Empty(DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            QuarantineUnreadableStoreFile(path);
            var ledger = VanguardOperatorBillingLedger.Empty(DateTimeOffset.UtcNow);
            await SaveBillingLedgerAsync(profileId, ledger);
            return ledger;
        }
    }

    public async Task SaveBillingLedgerAsync(string profileId, VanguardOperatorBillingLedger ledger)
    {
        var path = GetBillingLedgerPath(profileId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".vanguard-write-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(ledger, SerializerOptions);
            await File.WriteAllTextAsync(temporary, json + Environment.NewLine);
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

    private static async Task<string?> ReadExistingTextReadOnlyAsync(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<T>> LoadListAsync<T>(string path)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<T>();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<T>>(stream, SerializerOptions) ?? new List<T>();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            QuarantineUnreadableStoreFile(path);
            return Array.Empty<T>();
        }
    }

    private async Task SaveListAsync<T>(string path, IReadOnlyList<T> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, values, SerializerOptions);
    }

    private static async Task SaveListAtomicAsync<T>(string path, IReadOnlyList<T> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".vanguard-write-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(values, SerializerOptions);
            await File.WriteAllTextAsync(temporary, json + Environment.NewLine);
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

    private static async Task SaveTextAtomicAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".vanguard-write-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string SanitizeReason(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private static async Task EnsureJsonArrayFileExistsAsync(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "[]" + Environment.NewLine);
    }

    private static async Task EnsureBillingLedgerExistsAsync(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ledger = VanguardOperatorBillingLedger.Empty(DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(ledger, SerializerOptions) + Environment.NewLine);
    }

    private static bool QuarantineUnreadableStoreFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var backupPath = $"{path}.invalid-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Move(path, backupPath, overwrite: false);
            return true;
        }
        catch
        {
            // Storage must never prevent the SPT server from booting.
            return false;
        }
    }

    private static bool HasQuarantineEvidence(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            return Directory.EnumerateFiles(
                    directory,
                    Path.GetFileName(path) + ".invalid-*",
                    SearchOption.TopDirectoryOnly)
                .Any();
        }
        catch
        {
            return false;
        }
    }

    private void TryCreateProfileNormalizationBackup(string profileId)
    {
        try
        {
            string sourcePath = GetOperatorsPath(profileId);
            string backupPath = Path.Combine(GetProfileDirectory(profileId), ProfileNormalizationBackupFileName);
            if (File.Exists(sourcePath) && !HasProfileNormalizationBackup(profileId))
            {
                File.Copy(sourcePath, backupPath, overwrite: false);
            }
        }
        catch
        {
            // Migration is additive; inability to create the single rollback copy must not block server boot.
        }
    }

    private static bool JsonEquivalent<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
    {
        string leftJson = JsonSerializer.Serialize(left, SerializerOptions);
        string rightJson = JsonSerializer.Serialize(right, SerializerOptions);
        return string.Equals(leftJson, rightJson, StringComparison.Ordinal);
    }

    private static bool FilesEqual(string leftPath, string rightPath)
    {
        var left = new FileInfo(leftPath);
        var right = new FileInfo(rightPath);
        if (!left.Exists || !right.Exists || left.Length != right.Length)
        {
            return false;
        }

        using FileStream leftStream = File.OpenRead(leftPath);
        using FileStream rightStream = File.OpenRead(rightPath);
        int leftByte;
        while ((leftByte = leftStream.ReadByte()) >= 0)
        {
            if (leftByte != rightStream.ReadByte())
            {
                return false;
            }
        }

        return rightStream.ReadByte() < 0;
    }


    private static string SanitizeOperatorIdForFileName(string operatorId)
    {
        string safeOperatorId = string.IsNullOrWhiteSpace(operatorId) ? "unknown-operator" : operatorId.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            safeOperatorId = safeOperatorId.Replace(invalid, '_');
        }

        return safeOperatorId;
    }

    private static string NormalizeProfileId(string profileId) =>
        string.IsNullOrWhiteSpace(profileId) ? "unknown-profile" : profileId.Trim();

    private static string ResolveDefaultRootDirectory()
    {
        string modDirectory = GetModDirectory();
        string userDirectory = ResolveUserDirectory(modDirectory);
        string stableRoot = Path.Combine(userDirectory, "vanguard", "operators");
        string legacyRoot = Path.Combine(modDirectory, "data");
        TryMigrateLegacyStorage(legacyRoot, stableRoot);
        return stableRoot;
    }

    private static string ResolveUserDirectory(string modDirectory)
    {
        try
        {
            var modInfo = new DirectoryInfo(modDirectory);
            DirectoryInfo? modsDirectory = modInfo.Parent;
            if (modsDirectory != null && modsDirectory.Name.Equals("mods", StringComparison.OrdinalIgnoreCase) && modsDirectory.Parent != null)
            {
                return modsDirectory.Parent.FullName;
            }
        }
        catch
        {
            // Fall back below.
        }

        return Path.GetFullPath(Path.Combine(modDirectory, "..", ".."));
    }

    private static void TryMigrateLegacyStorage(string legacyRoot, string stableRoot)
    {
        try
        {
            string legacyProfiles = Path.Combine(legacyRoot, ProfilesDirectoryName);
            string stableProfiles = Path.Combine(stableRoot, ProfilesDirectoryName);
            if (!Directory.Exists(legacyProfiles))
            {
                return;
            }

            bool stableHasProfiles = Directory.Exists(stableProfiles)
                && Directory.EnumerateDirectories(stableProfiles).Any();
            if (stableHasProfiles)
            {
                return;
            }

            CopyDirectory(legacyRoot, stableRoot);
        }
        catch
        {
            // Persistence migration must never block server startup.
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (string directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
        }

        foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDirectory, file);
            string destination = Path.Combine(destinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination))
            {
                File.Copy(file, destination, overwrite: false);
            }
        }
    }

    private static string GetModDirectory()
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        return Path.GetDirectoryName(assemblyPath) ?? AppContext.BaseDirectory;
    }

    private string GetProfilesRootDirectory() => Path.Combine(rootDirectory, ProfilesDirectoryName);

    private string GetIdentityRegistryPath() => Path.Combine(rootDirectory, IdentityRegistryFileName);

    private string GetProfileDirectory(string profileId) => Path.Combine(GetProfilesRootDirectory(), NormalizeProfileId(profileId));

    private string GetOperatorsPath(string profileId) => Path.Combine(GetProfileDirectory(profileId), OperatorsFileName);

    private string GetActiveServicePath(string profileId) => Path.Combine(GetProfileDirectory(profileId), ActiveServiceFileName);

    private string GetContractsPath(string profileId) => Path.Combine(GetProfileDirectory(profileId), ContractsFileName);

    private string GetMedicalPath(string profileId) => Path.Combine(GetProfileDirectory(profileId), MedicalFileName);

    private string GetContactsPath(string profileId) => Path.Combine(GetProfileDirectory(profileId), ContactsFileName);

    private string GetCareerRaidLedgerPath(string profileId) => Path.Combine(GetProfileDirectory(profileId), CareerRaidLedgerFileName);

    private string GetBillingLedgerPath(string profileId) => Path.Combine(GetProfileDirectory(profileId), BillingLedgerFileName);
}


public sealed record VanguardOperatorExperienceReconciliationWriteResult(
    bool Success,
    string Reason,
    bool PermanentBackupPresent,
    bool ReadBackVerified,
    bool RolledBack);


public sealed record VanguardOperatorProfilesAtomicWriteResult(
    bool Success,
    string Reason,
    bool ReadBackVerified);

public sealed record VanguardActiveServiceAtomicWriteResult(
    bool Success,
    string Reason,
    bool ReadBackVerified);

public sealed record VanguardOperatorReleaseWriteResult(
    bool Success,
    string Reason,
    bool ReadBackVerified,
    bool RolledBack);

public sealed record VanguardOperatorInventoryProfileVisualMutation(
    string OperatorId,
    string ExpectedBeforeJson,
    string AfterJson);

public sealed record VanguardOperatorInventoryProfileEnrollmentMutation(
    string OperatorId,
    string ExpectedBeforeJson,
    string AfterJson);

public sealed record VanguardOperatorEnrollmentMigrationWriteResult(
    bool Success,
    string Reason,
    bool OperatorsPermanentBackupPresent,
    int InventoryPermanentBackupCount,
    bool ReadBackVerified,
    bool RolledBack);

public sealed record VanguardOperatorEnrolledVisualIdentityMigrationWriteResult(
    bool Success,
    string Reason,
    bool OperatorsPermanentBackupPresent,
    int InventoryPermanentBackupCount,
    bool ReadBackVerified,
    bool RolledBack);

public sealed record VanguardOperatorVisualIdentityMigrationWriteResult(bool Success, string Reason, bool PermanentBackupPresent, bool ReadBackVerified, bool RolledBack);
