using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Raid.Models;
using Vanguard.Server.Operators.Raid.Responses;
using Vanguard.Server.Operators.Storage;
using Vanguard.Server.Operators.Services;
using Vanguard.Server.Diagnostics;

// Responsibility: Builds the owner-scoped Operator manifest used to tell a raid exactly which persistent Operators belong to that player and what immutable/profile facts travel with them.
// Flow: The player profile and Operator store are reconciled, eligible persistent Operators are projected into a compact raid manifest, and deterministic identifiers/policies are returned to the client/headless path.
// Authority boundary: The server store owns persistent Operator truth; the manifest is a transport snapshot and does not itself spawn bots or mutate raid state.
// Invariant: A manifest must never mix owners, duplicate Operators or invent missing persistent data, and the same stored state should project consistently across retries.
namespace Vanguard.Server.Operators.Raid.Services;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardRaidOperatorManifestService(
    VanguardOperatorStore store,
    VanguardOperatorExperienceReconciliationService experienceReconciliationService,
    VanguardOperatorCareerXpCommitService careerXpCommitService,
    ISptLogger<VanguardRaidOperatorManifestService> logger)
{
    public async Task<VanguardRaidOperatorManifestResponse> LoadManifestForOwnerAsync(
        string requestedProfileId,
        string? raidSessionId = null,
        bool createStorageIfMissing = true)
    {
        string requested = Normalize(requestedProfileId, "unknown-profile");
        string raidId = Normalize(raidSessionId, BuildRaidSessionId());
        string storageProfileId = createStorageIfMissing
            ? await store.ResolveStorageProfileIdAsync(requested)
            : requested;

        if (!createStorageIfMissing && !store.GetKnownProfileIds().Contains(storageProfileId, StringComparer.OrdinalIgnoreCase))
        {
            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_RAID_MANIFEST_OWNER] owner={storageProfileId} raid={raidId} returned=0 reason=no_vanguard_storage createStorage=false"));
            return new VanguardRaidOperatorManifestResponse(
                requested,
                storageProfileId,
                raidId,
                Array.Empty<VanguardRaidOperatorSnapshot>(),
                0,
                0,
                0,
                0,
                true,
                "no_vanguard_storage_for_owner",
                DateTimeOffset.UtcNow,
                VanguardBuildVersion.BuildLabel);
        }

        if (!createStorageIfMissing)
        {
            await store.EnsureProfileStorageInitializedAsync(storageProfileId);
        }

        var state = await store.LoadStateAsync(storageProfileId);

        // progression convergence makes raid manifest construction a progression-consistency boundary.
        // A player must not need to open the Off-Raid dossier first for an old incoherent
        // Level/cumulative-XP baseline or a pending verified XP credit to converge.
        _ = await experienceReconciliationService.ReconcileLegacyBaselinesAsync(
            storageProfileId,
            state.Operators);

        // Always reload after reconciliation. Multiple Operator bot requests may arrive close together;
        // this prevents a request that entered with a stale pre-reconciliation snapshot from carrying it forward.
        state = state with { Operators = await store.LoadOperatorsAsync(storageProfileId) };

        // Let the XP service load its own Operator snapshot inside its singleton gate so concurrent manifest
        // requests cannot hand it a stale knownOperators array captured before another request committed.
        VanguardOperatorCareerXpSyncResult xpSync = await careerXpCommitService.SynchronizeAsync(storageProfileId);
        if (xpSync.Success)
        {
            state = state with { Operators = xpSync.Operators };
        }
        else
        {
            state = state with { Operators = await store.LoadOperatorsAsync(storageProfileId) };
            logger.Warning(VanguardServerDiagnosticsLog.Present(
                $"[{VanguardBuildVersion.CareerXpProfileParityStatusTag}] phase=raid_manifest_progression_sync; owner={storageProfileId}; raid={raidId}; success=false; reason={Normalize(xpSync.Reason, "unknown")}; action=reload_persisted_operator_state_and_continue; tag={VanguardBuildVersion.CareerXpProfileParityStatusTag}"));
        }

        var operatorsById = state.Operators
            .Where(profile => !string.IsNullOrWhiteSpace(profile.OperatorId))
            .GroupBy(profile => Normalize(profile.OperatorId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var medicalById = state.Medical
            .Where(record => !string.IsNullOrWhiteSpace(record.OperatorId))
            .GroupBy(record => Normalize(record.OperatorId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var snapshots = new List<VanguardRaidOperatorSnapshot>();
        int selectedCount = 0;
        int skippedCount = 0;
        foreach (var activeRecord in state.ActiveService)
        {
            if (!activeRecord.IsSelectedForRaid)
            {
                continue;
            }

            selectedCount++;
            string operatorId = Normalize(activeRecord.OperatorId);
            if (string.IsNullOrWhiteSpace(operatorId))
            {
                skippedCount++;
                continue;
            }

            if (!operatorsById.TryGetValue(operatorId, out var operatorProfile))
            {
                skippedCount++;
                logger.Warning(VanguardServerDiagnosticsLog.Present($"[VANGUARD_RAID_MANIFEST_OWNER] owner={storageProfileId} raid={raidId} operator={operatorId} skipped=profile_missing"));
                continue;
            }

            medicalById.TryGetValue(operatorId, out var medical);
            var snapshot = BuildSnapshot(storageProfileId, raidId, operatorProfile, activeRecord, medical);
            snapshots.Add(snapshot);
        }

        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_RAID_MANIFEST_OWNER] owner={storageProfileId} raid={raidId} active={state.ActiveService.Count} selected={selectedCount} returned={snapshots.Count} skipped={skippedCount} mode=headless_authoritative_owner_logical"));
        return new VanguardRaidOperatorManifestResponse(
            requested,
            storageProfileId,
            raidId,
            snapshots,
            state.ActiveService.Count,
            selectedCount,
            snapshots.Count,
            skippedCount,
            true,
            "vanguard_raid_manifest_loaded",
            DateTimeOffset.UtcNow,
            VanguardBuildVersion.BuildLabel);
    }

    public async Task<IReadOnlyDictionary<string, VanguardRaidOperatorManifestResponse>> LoadManifestForOwnersAsync(
        IReadOnlyCollection<string>? requestedProfileIds,
        string? raidSessionId = null)
    {
        var result = new Dictionary<string, VanguardRaidOperatorManifestResponse>(StringComparer.OrdinalIgnoreCase);
        string raidId = Normalize(raidSessionId, BuildRaidSessionId());
        var knownOwners = store.GetKnownProfileIds().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedOwners = (requestedProfileIds ?? Array.Empty<string>())
            .Where(profileId => !string.IsNullOrWhiteSpace(profileId))
            .Select(profileId => profileId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (string ownerProfileId in requestedOwners)
        {
            bool known = knownOwners.Contains(ownerProfileId);
            result[ownerProfileId] = await LoadManifestForOwnerAsync(ownerProfileId, raidId, createStorageIfMissing: known);
        }

        return result;
    }

    public async Task<VanguardRaidOperatorSpawnProfile?> TryLoadSpawnProfileAsync(
        string? ownerProfileId,
        string? operatorId,
        string? raidSessionId = null)
    {
        string owner = Normalize(ownerProfileId);
        string opId = Normalize(operatorId);
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(opId))
        {
            return null;
        }

        var manifest = await LoadManifestForOwnerAsync(owner, raidSessionId, createStorageIfMissing: false);
        var snapshot = manifest.Operators.FirstOrDefault(candidate => string.Equals(candidate.OperatorId, opId, StringComparison.OrdinalIgnoreCase));
        if (snapshot is null)
        {
            logger.Warning(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PROFILE_GENERATED] owner={owner} operator={opId} skipped=not_in_selected_manifest"));
            return null;
        }

        string profilePath = store.GetOperatorInventoryProfilePath(owner, opId);
        if (!File.Exists(profilePath))
        {
            logger.Warning(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PROFILE_GENERATED] owner={owner} operator={opId} inventoryProfile=missing path={profilePath}"));
            return new VanguardRaidOperatorSpawnProfile(snapshot, null, null, null, "inventory_profile_missing");
        }

        try
        {
            string json = await File.ReadAllTextAsync(profilePath);
            JsonObject profile = JsonNode.Parse(json)?.AsObject()
                ?? throw new InvalidOperationException("Operator inventory profile JSON root is not an object.");
            JsonObject pmc = GetPmcObject(profile);
            JsonObject inventory = GetObject(pmc, "Inventory") ?? GetObject(pmc, "inventory") ?? new JsonObject();
            return new VanguardRaidOperatorSpawnProfile(snapshot, profile, pmc, inventory, "operator_spawn_profile_loaded");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.Warning(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PROFILE_GENERATED] owner={owner} operator={opId} inventoryProfile=invalid type={exception.GetType().Name} message={Compact(exception.Message)}"));
            return new VanguardRaidOperatorSpawnProfile(snapshot, null, null, null, "operator_inventory_profile_invalid_" + exception.GetType().Name);
        }
    }

    private VanguardRaidOperatorSnapshot BuildSnapshot(
        string ownerProfileId,
        string raidSessionId,
        VanguardOperatorProfile operatorProfile,
        VanguardActiveServiceRecord activeRecord,
        VanguardOperatorMedicalRecord? medical)
    {
        string profilePath = store.GetOperatorInventoryProfilePath(ownerProfileId, operatorProfile.OperatorId);
        bool profileExists = File.Exists(profilePath);
        int itemCount = 0;
        bool hasEquipmentRoot = false;
        string inventoryProfileId = BuildInventoryProfileId(ownerProfileId, operatorProfile.OperatorId);
        if (profileExists)
        {
            TryAuditInventoryProfile(profilePath, out itemCount, out hasEquipmentRoot, out string? profileIdFromFile);
            inventoryProfileId = Normalize(profileIdFromFile, inventoryProfileId);
        }

        DateTimeOffset eligibilityNow = DateTimeOffset.UtcNow;
        var eligibility = VanguardOperatorRaidEligibilityPolicy.Evaluate(activeRecord, medical, eligibilityNow);
        double healthRatio = eligibility.HealthRatio;

        var persona = operatorProfile.Persona;
        return new VanguardRaidOperatorSnapshot(
            operatorProfile.OperatorId,
            ownerProfileId,
            string.Empty,
            raidSessionId,
            inventoryProfileId,
            ResolveDisplayName(operatorProfile, activeRecord),
            Normalize(operatorProfile.Identity.Callsign, ResolveDisplayName(operatorProfile, activeRecord)),
            Normalize(operatorProfile.Identity.Side, activeRecord.Side, "Usec"),
            Math.Max(operatorProfile.Progression.Level, 1),
            Math.Max(operatorProfile.Progression.Experience, 0),
            Normalize(operatorProfile.Role, activeRecord.Role, "Operator"),
            Normalize(operatorProfile.Specialty, activeRecord.Specialty, "Rifleman"),
            Normalize(activeRecord.Status, operatorProfile.ServiceStatus, VanguardOperatorServiceStatuses.Available),
            activeRecord.IsSelectedForRaid,
            eligibility.IsEligible,
            eligibility.Reason,
            Normalize(medical?.Status, VanguardOperatorServiceStatuses.Available),
            healthRatio,
            profileExists,
            itemCount,
            hasEquipmentRoot,
            new VanguardRaidOperatorSainPayload(
                Normalize(persona.BasePersona, "Disciplined"),
                Normalize(persona.Doctrine, "fire_discipline_and_squad_cohesion"),
                Normalize(persona.Temperament, "methodical"),
                Normalize(persona.SainProfileFamily, "vanguard.sain.disciplined"),
                Normalize(persona.SainTuningPlan, "vanguard.tuning.disciplined.standard"),
                Normalize(persona.CombatStyle, "disciplined_fire_support"),
                Normalize(persona.EngagementRange, "medium"),
                Normalize(persona.SquadRole, "rifleman"),
                persona.Traits?.Where(trait => !string.IsNullOrWhiteSpace(trait)).Select(trait => trait.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>()),
            DateTimeOffset.UtcNow,
            VanguardBuildVersion.BuildLabel,
            VanguardOperatorSchema.CurrentVersion,
            VanguardOperatorLootTargetPolicyService.NormalizeOrDefault(operatorProfile.LootTargetPolicy));
    }

    private static bool IsRecovering(VanguardOperatorMedicalRecord? medical, DateTimeOffset now)
    {
        return medical?.RecoveryUntilUtc is DateTimeOffset until && until > now;
    }

    private static string ResolveDisplayName(VanguardOperatorProfile operatorProfile, VanguardActiveServiceRecord activeRecord)
    {
        return Normalize(
            operatorProfile.Identity.DisplayName,
            activeRecord.DisplayName,
            operatorProfile.Identity.Callsign,
            operatorProfile.OperatorId,
            "Operator");
    }

    private static void TryAuditInventoryProfile(string profilePath, out int itemCount, out bool hasEquipmentRoot, out string? inventoryProfileId)
    {
        itemCount = 0;
        hasEquipmentRoot = false;
        inventoryProfileId = null;
        try
        {
            JsonObject profile = JsonNode.Parse(File.ReadAllText(profilePath))?.AsObject() ?? new JsonObject();
            inventoryProfileId = GetString(GetObject(profile, "info"), "id")
                ?? GetString(GetObject(profile, "Info"), "Id");
            JsonObject pmc = GetPmcObject(profile);
            JsonObject? inventory = GetObject(pmc, "Inventory") ?? GetObject(pmc, "inventory");
            JsonArray? items = GetArray(inventory, "items") ?? GetArray(inventory, "Items");
            itemCount = items?.Count ?? 0;
            string? equipmentId = GetString(inventory, "equipment") ?? GetString(inventory, "Equipment");
            hasEquipmentRoot = !string.IsNullOrWhiteSpace(equipmentId)
                && items?.OfType<JsonObject>().Any(item => string.Equals(GetItemId(item), equipmentId, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch
        {
            itemCount = 0;
            hasEquipmentRoot = false;
            inventoryProfileId = null;
        }
    }

    private static JsonObject GetPmcObject(JsonObject profile)
    {
        JsonObject? characters = GetObject(profile, "characters") ?? GetObject(profile, "Characters");
        JsonObject? pmc = GetObject(characters, "pmc") ?? GetObject(characters, "Pmc") ?? GetObject(profile, "Pmc") ?? GetObject(profile, "pmc");
        if (pmc == null)
        {
            throw new InvalidOperationException("Operator profile has no PMC descriptor.");
        }

        return pmc;
    }

    internal static JsonObject? GetObject(JsonObject? obj, string name)
    {
        if (obj == null)
        {
            return null;
        }

        string? actual = obj.Select(property => property.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        return actual != null && obj[actual] is JsonObject child ? child : null;
    }

    internal static JsonArray? GetArray(JsonObject? obj, string name)
    {
        if (obj == null)
        {
            return null;
        }

        string? actual = obj.Select(property => property.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        return actual != null && obj[actual] is JsonArray child ? child : null;
    }

    internal static string? GetString(JsonObject? obj, string name)
    {
        if (obj == null)
        {
            return null;
        }

        string? actual = obj.Select(property => property.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        return actual == null ? null : NodeToString(obj[actual]);
    }

    internal static string? GetItemId(JsonObject item) => GetString(item, "_id") ?? GetString(item, "Id") ?? GetString(item, "id");

    internal static string? GetTemplateId(JsonObject item) => GetString(item, "_tpl") ?? GetString(item, "Template") ?? GetString(item, "template");

    internal static string? Raw(JsonNode? node) => node?.ToJsonString();

    private static string? NodeToString(JsonNode? node)
    {
        if (node == null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out string? text))
        {
            return text;
        }

        return node.ToJsonString();
    }

    private static string BuildInventoryProfileId(string ownerProfileId, string operatorId) =>
        "vg-inv-" + Normalize(ownerProfileId, "owner").Replace(' ', '-') + "-" + Normalize(operatorId, "operator").Replace(' ', '-');

    private static string BuildRaidSessionId() => "raid-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static double ClampRatio(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 1d;
        }

        return Math.Min(1d, Math.Max(0d, value));
    }

    private static string Normalize(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string Compact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        string compact = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return compact.Length <= 180 ? compact : compact[..180] + "...";
    }
}
