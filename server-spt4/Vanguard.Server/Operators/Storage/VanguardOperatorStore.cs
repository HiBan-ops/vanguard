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

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string rootDirectory;

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
        string safeOperatorId = string.IsNullOrWhiteSpace(operatorId) ? "unknown-operator" : operatorId.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            safeOperatorId = safeOperatorId.Replace(invalid, '_');
        }

        return Path.Combine(GetOperatorInventoryProfileDirectory(profileId), safeOperatorId + ".json");
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

    public Task<IReadOnlyList<VanguardActiveServiceRecord>> LoadActiveServiceAsync(string profileId) =>
        LoadListAsync<VanguardActiveServiceRecord>(GetActiveServicePath(profileId));

    public Task SaveActiveServiceAsync(string profileId, IReadOnlyList<VanguardActiveServiceRecord> activeService) =>
        SaveListAsync(GetActiveServicePath(profileId), activeService);

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
