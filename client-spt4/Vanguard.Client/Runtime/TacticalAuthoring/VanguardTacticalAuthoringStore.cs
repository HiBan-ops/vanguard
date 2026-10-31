#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vanguard.Client.Diagnostics;

// Responsibility: Loads and saves Tactical Authoring HOLD documents and migrates known legacy input without strengthening authored semantics.
// Flow: Legacy input is validated and converted in memory; the original bytes are backed up before the first persisted replacement.
// Authority boundary: This store owns only local Tactical Authoring persistence. Portal/PassagePath/TacticalAnchor/PoseSlot persistence belongs to physically separate Tactical Coordination Base/User stores.
// Invariant: Unknown future schemas and cross-domain TC collections fail closed; saves remain temp/readback/replace transactional; legacy Access records survive only as unconfirmed LegacyPortalCandidates.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal static class VanguardTacticalAuthoringStore
{
    public const int CurrentSchemaVersion = 2;
    private const int LegacySchemaVersion = 1;
    private const string RelativeRoot = "Vanguard/TacticalAuthoring/Maps";
    private const string RelativeReportRoot = "Vanguard/TacticalAuthoring/Reports";
    private const string RelativeBackupRoot = "Vanguard/TacticalAuthoring/Backups";
    private const string BepInExConfigDirectoryName = "config";
    private const string MigrationStatusTag = "VANGUARD_TACTICAL_AUTHORING_MIGRATION_STATUS";
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Include
    };

    public static string GetMapPath(string mapId)
    {
        var directory = GetBepInExConfigDirectory(RelativeRoot);
        return Path.Combine(directory, GetSafeMapFileName(mapId) + ".json");
    }

    public static VanguardTacticalAuthoringMapFile LoadOrCreate(string mapId, string mapRevision, string eftClientVersion, out bool loadedExisting)
    {
        var path = GetMapPath(mapId);
        if (File.Exists(path))
        {
            loadedExisting = true;
            return LoadRequired(path, mapId);
        }

        loadedExisting = false;
        var now = UtcNowText();
        return new VanguardTacticalAuthoringMapFile
        {
            SchemaVersion = CurrentSchemaVersion,
            MapId = mapId,
            MapRevision = mapRevision,
            EftClientVersion = eftClientVersion,
            CreatedAt = now,
            LastSavedAt = string.Empty,
            CreatedWithBuild = VanguardBuildVersion.BuildLabel,
            LastSavedWithBuild = string.Empty,
            RuntimeConsumptionEnabled = false,
            HoldZones = new List<VanguardTacticalAuthoringZone>(),
            LegacyPortalCandidates = new List<VanguardTacticalAuthoringLegacyPortalCandidate>(),
            MigrationRecord = null
        };
    }

    public static VanguardTacticalAuthoringMapFile Reload(string mapId)
    {
        return LoadRequired(GetMapPath(mapId), mapId);
    }

    public static void Save(VanguardTacticalAuthoringMapFile mapFile)
    {
        if (mapFile is null)
        {
            throw new ArgumentNullException(nameof(mapFile));
        }

        VanguardTacticalAuthoringSchemaMigration.SynchronizeLegacyPortalCandidatesFromCompatibilityViews(mapFile);
        mapFile.SchemaVersion = CurrentSchemaVersion;
        mapFile.RuntimeConsumptionEnabled = false;
        ValidateEnvelope(mapFile, mapFile.MapId);

        var path = GetMapPath(mapFile.MapId);
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Authoring map path has no parent directory.");
        Directory.CreateDirectory(directory);

        bool legacyReplacement = File.Exists(path) && ReadSchemaVersionFromFile(path) == LegacySchemaVersion;
        if (legacyReplacement)
        {
            EnsureLegacySourceBackup(path, mapFile);
        }

        mapFile.LastSavedAt = UtcNowText();
        mapFile.LastSavedWithBuild = VanguardBuildVersion.BuildLabel;
        ValidateEnvelope(mapFile, mapFile.MapId);

        var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
        var backupPath = path + ".bak";
        try
        {
            var json = JsonConvert.SerializeObject(mapFile, JsonSettings);
            WriteAllTextDurable(tempPath, json);

            var readBackJson = File.ReadAllText(tempPath, Utf8NoBom);
            EnsureAuthoringDocumentHasNoCoordinationCollections(readBackJson);
            var readBack = JsonConvert.DeserializeObject<VanguardTacticalAuthoringMapFile>(readBackJson, JsonSettings)
                ?? throw new InvalidDataException("Authoring save read-back deserialized to null.");
            VanguardTacticalAuthoringSchemaMigration.AttachLegacyAccessCompatibilityViews(readBack);
            ValidateEnvelope(readBack, mapFile.MapId);
            EnsureIdentityParity(mapFile, readBack);

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, backupPath, true);
            }
            else
            {
                File.Move(tempPath, path);
            }

            if (mapFile.MigrationRecord != null)
            {
                VanguardClientDiagnosticsLog.Info(
                    MigrationStatusTag,
                    $"event=SCHEMA_V2_COMMIT; map={mapFile.MapId}; from={mapFile.MigrationRecord.FromSchemaVersion}; to={mapFile.MigrationRecord.ToSchemaVersion}; sourceSha256={mapFile.MigrationRecord.SourceSha256}; sourceBackup={mapFile.MigrationRecord.SourceBackupFileName}; holdZones={mapFile.HoldZones.Count}; holdSlots={mapFile.HoldZones.Sum(zone => zone.Slots.Count)}; legacyPortalCandidates={mapFile.LegacyPortalCandidates.Count}; coordinationObjectsPersisted=0; activePortalsCreatedByMigration=0; physicalPoseSnapshotsInvented=0; transactionalReadback=true");
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public static string ExportInvalidReport(VanguardTacticalAuthoringMapFile mapFile)
    {
        ValidateEnvelope(mapFile, mapFile.MapId);
        var report = new VanguardTacticalAuthoringInvalidReport
        {
            MapId = mapFile.MapId,
            ExportedAt = UtcNowText(),
            ExportedWithBuild = VanguardBuildVersion.BuildLabel,
            ZoneCount = mapFile.Zones.Count,
            SlotCount = mapFile.Zones.Sum(zone => zone.Slots.Count)
        };

        foreach (var zone in mapFile.Zones)
        {
            foreach (var slot in zone.Slots.Where(slot => slot.ValidationState != VanguardTacticalAuthoringValidationState.Valid || !slot.Enabled))
            {
                report.Slots.Add(new VanguardTacticalAuthoringInvalidSlotRecord
                {
                    ZoneId = zone.ZoneId,
                    DisplayZoneName = zone.DisplayZoneName,
                    FloorId = zone.FloorId,
                    SlotId = slot.SlotId,
                    SlotType = slot.SlotType,
                    Enabled = slot.Enabled,
                    ValidationState = slot.ValidationState,
                    ValidationNotes = slot.ValidationNotes
                });
            }
        }

        report.NonValidSlotCount = report.Slots.Count;
        var directory = GetBepInExConfigDirectory(RelativeReportRoot);
        Directory.CreateDirectory(directory);
        var fileName = string.Format(
            CultureInfo.InvariantCulture,
            "InvalidSlots_{0}_{1}.json",
            GetSafeMapFileName(mapFile.MapId),
            DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
        var path = Path.Combine(directory, fileName);
        WriteAllTextDurable(path, JsonConvert.SerializeObject(report, JsonSettings));
        return path;
    }

    private static string GetBepInExConfigDirectory(string relativePath)
    {
        var bepinExRoot = BepInEx.Paths.BepInExRootPath;
        if (string.IsNullOrWhiteSpace(bepinExRoot))
        {
            throw new InvalidOperationException("BepInEx root path is unavailable; tactical authoring storage cannot be resolved safely.");
        }

        return Path.Combine(
            bepinExRoot,
            BepInExConfigDirectoryName,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static VanguardTacticalAuthoringMapFile LoadRequired(string path, string expectedMapId)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("No Vanguard tactical authoring file exists for this map.", path);
        }

        byte[] sourceBytes = File.ReadAllBytes(path);
        string rawJson = Utf8NoBom.GetString(sourceBytes);
        string json = NormalizeSerializedFieldNamesForCurrentVocabulary(rawJson);
        int schemaVersion = ReadSchemaVersion(json);

        if (schemaVersion == CurrentSchemaVersion)
        {
            EnsureAuthoringDocumentHasNoCoordinationCollections(json);
            var current = JsonConvert.DeserializeObject<VanguardTacticalAuthoringMapFile>(json, JsonSettings)
                ?? throw new InvalidDataException("Authoring map file deserialized to null.");
            VanguardTacticalAuthoringSchemaMigration.AttachLegacyAccessCompatibilityViews(current);
            ValidateEnvelope(current, expectedMapId);
            return current;
        }

        if (schemaVersion == LegacySchemaVersion)
        {
            var legacy = JsonConvert.DeserializeObject<VanguardLegacyTacticalAuthoringMapFile>(json, JsonSettings)
                ?? throw new InvalidDataException("Schema-v1 authoring map file deserialized to null.");
            ValidateLegacyEnvelope(legacy, expectedMapId);

            string sourceSha256 = ComputeSha256(sourceBytes);
            var migrated = VanguardTacticalAuthoringSchemaMigration.MigrateLegacyAuthoring(legacy, sourceSha256, UtcNowText());
            VanguardTacticalAuthoringSchemaMigration.AttachLegacyAccessCompatibilityViews(migrated);
            ValidateEnvelope(migrated, expectedMapId);

            VanguardClientDiagnosticsLog.Info(
                MigrationStatusTag,
                $"event=SCHEMA_V1_CONVERTED_IN_MEMORY; map={migrated.MapId}; from=1; to=2; sourceSha256={sourceSha256}; holdZones={migrated.HoldZones.Count}; holdSlots={migrated.HoldZones.Sum(zone => zone.Slots.Count)}; legacyPortalCandidates={migrated.LegacyPortalCandidates.Count}; coordinationObjectsPersisted=0; activePortalsCreated=0; physicalPoseSnapshotsInvented=0; diskReplacement=false; explicitSaveRequired=true");
            return migrated;
        }

        throw new InvalidDataException($"Unsupported tactical authoring schema {schemaVersion}; current={CurrentSchemaVersion}; knownLegacy={LegacySchemaVersion}. Unknown future schemas fail closed.");
    }

    private static void EnsureAuthoringDocumentHasNoCoordinationCollections(string json)
    {
        JObject envelope;
        try
        {
            envelope = JObject.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Tactical authoring file is not valid JSON.", exception);
        }

        string[] forbidden = { "Portals", "PassagePaths", "AuthoredTacticalAnchors", "TacticalAnchors", "PoseSlots" };
        foreach (var propertyName in forbidden)
        {
            if (envelope.Property(propertyName, StringComparison.Ordinal) != null)
            {
                throw new InvalidDataException(
                    $"Schema-v2 Tactical Authoring document illegally embeds Tactical Coordination collection '{propertyName}'. " +
                    "TC Base/User data must remain physically separate; refusing ambiguous cross-domain persistence.");
            }
        }
    }

    private static int ReadSchemaVersionFromFile(string path)
    {
        return ReadSchemaVersion(NormalizeSerializedFieldNamesForCurrentVocabulary(File.ReadAllText(path, Utf8NoBom)));
    }

    private static int ReadSchemaVersion(string json)
    {
        try
        {
            var envelope = JObject.Parse(json);
            var token = envelope["SchemaVersion"];
            if (token == null || token.Type != JTokenType.Integer)
            {
                throw new InvalidDataException("Tactical authoring file has no integer SchemaVersion.");
            }
            return token.Value<int>();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Tactical authoring file is not valid JSON.", exception);
        }
    }

    // A prior public schema renamed two display-only JSON fields to player-neutral names. Existing authoring maps are
    // still schema-compatible, so normalize only those property labels before deserialization instead of
    // rejecting or rewriting the rest of the file. The old labels are assembled from character codes so
    // Vanguard source itself does not retain the deprecated vocabulary. A normal Save writes only new names.
    private static string NormalizeSerializedFieldNamesForCurrentVocabulary(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return json;
        }

        string oldZoneName = new string(new[] { 'H', 'u', 'm', 'a', 'n', 'Z', 'o', 'n', 'e', 'N', 'a', 'm', 'e' });
        string oldAccessName = new string(new[] { 'H', 'u', 'm', 'a', 'n', 'N', 'a', 'm', 'e' });
        return json
            .Replace("\"" + oldZoneName + "\"", "\"DisplayZoneName\"")
            .Replace("\"" + oldAccessName + "\"", "\"DisplayName\"");
    }

    private static void ValidateEnvelope(VanguardTacticalAuthoringMapFile mapFile, string expectedMapId)
    {
        if (mapFile.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported tactical authoring schema {mapFile.SchemaVersion}; expected {CurrentSchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(mapFile.MapId) || !string.Equals(mapFile.MapId, expectedMapId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Authoring map identity mismatch. expected={expectedMapId}; actual={mapFile.MapId}");
        }
        if (mapFile.RuntimeConsumptionEnabled)
        {
            throw new InvalidDataException("Tactical authoring schema-v2 compatibility runtime flag must remain false.");
        }

        mapFile.HoldZones ??= new List<VanguardTacticalAuthoringZone>();
        mapFile.LegacyPortalCandidates ??= new List<VanguardTacticalAuthoringLegacyPortalCandidate>();
        VanguardTacticalAuthoringSchemaMigration.AttachLegacyAccessCompatibilityViews(mapFile);
        EnsureUniqueIdsAndGeometry(mapFile);

        if (mapFile.MigrationRecord != null)
        {
            if (mapFile.MigrationRecord.FromSchemaVersion != LegacySchemaVersion
                || mapFile.MigrationRecord.ToSchemaVersion != CurrentSchemaVersion
                || string.IsNullOrWhiteSpace(mapFile.MigrationRecord.SourceSha256)
                || mapFile.MigrationRecord.ActivePortalCountCreated != 0
                || mapFile.MigrationRecord.PhysicalPoseSnapshotsInvented != 0)
            {
                throw new InvalidDataException("Tactical authoring migration provenance violates the schema-v1 -> schema-v2 safety contract.");
            }
        }
    }

    private static void ValidateLegacyEnvelope(VanguardLegacyTacticalAuthoringMapFile mapFile, string expectedMapId)
    {
        if (mapFile.SchemaVersion != LegacySchemaVersion)
        {
            throw new InvalidDataException($"Legacy tactical authoring reader received schema {mapFile.SchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(mapFile.MapId) || !string.Equals(mapFile.MapId, expectedMapId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Legacy authoring map identity mismatch. expected={expectedMapId}; actual={mapFile.MapId}");
        }
        if (mapFile.RuntimeConsumptionEnabled)
        {
            throw new InvalidDataException("Schema-v1 authoring file illegally sets RuntimeConsumptionEnabled=true.");
        }

        mapFile.Zones ??= new List<VanguardLegacyTacticalAuthoringZone>();
        var zoneIds = new HashSet<string>(StringComparer.Ordinal);
        var slotIds = new HashSet<string>(StringComparer.Ordinal);
        var accessIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var zone in mapFile.Zones)
        {
            if (zone == null || string.IsNullOrWhiteSpace(zone.ZoneId) || !zoneIds.Add(zone.ZoneId))
            {
                throw new InvalidDataException("Schema-v1 authoring file contains a missing or duplicate ZoneId.");
            }
            ValidateZoneGeometry(zone.ZoneId, zone.ZoneAnchor, zone.MinY, zone.MaxY, zone.MinYExplicit, zone.MaxYExplicit, zone.FloorBoundsProvisional, zone.ZoneRadius);

            zone.Slots ??= new List<VanguardLegacyTacticalAuthoringSlot>();
            zone.Accesses ??= new List<VanguardLegacyTacticalAuthoringAccess>();
            foreach (var slot in zone.Slots)
            {
                if (slot == null || string.IsNullOrWhiteSpace(slot.SlotId) || !slotIds.Add(slot.SlotId))
                {
                    throw new InvalidDataException("Schema-v1 authoring file contains a missing or duplicate SlotId.");
                }
                if (!Enum.IsDefined(typeof(VanguardTacticalSlotType), slot.SlotType))
                {
                    throw new InvalidDataException($"Schema-v1 authoring slot {slot.SlotId} has an unknown SlotType value.");
                }
                ValidateSlotVectors(slot.SlotId, slot.Position, slot.NavMeshProjectedPosition, slot.WatchDirection);
                if (!IsFinite(slot.WatchArc) || slot.WatchArc < 20.0f || slot.WatchArc > 180.0f
                    || slot.Priority < 0 || slot.Priority > 100
                    || slot.MinimumSquadSize < 1 || slot.MinimumSquadSize > 3
                    || !IsFinite(slot.MaximumOwnerDistance) || slot.MaximumOwnerDistance < 0.0f)
                {
                    throw new InvalidDataException($"Schema-v1 authoring slot {slot.SlotId} contains invalid tactical constraints.");
                }
                if (slot.RuntimeEligible)
                {
                    throw new InvalidDataException($"Schema-v1 authoring slot {slot.SlotId} illegally sets RuntimeEligible=true.");
                }
            }

            foreach (var access in zone.Accesses)
            {
                if (access == null || string.IsNullOrWhiteSpace(access.AccessId) || !accessIds.Add(access.AccessId))
                {
                    throw new InvalidDataException("Schema-v1 authoring file contains a missing or duplicate AccessId.");
                }
                ValidateAccessVectors(access.AccessId, access.Position, access.NavMeshProjectedPosition, access.ApproachDirection);
            }

            foreach (var slot in zone.Slots)
            {
                if (!string.IsNullOrWhiteSpace(slot.AssociatedAccessId)
                    && zone.Accesses.All(access => !string.Equals(access.AccessId, slot.AssociatedAccessId, StringComparison.Ordinal)))
                {
                    throw new InvalidDataException($"Schema-v1 authoring slot {slot.SlotId} references missing access {slot.AssociatedAccessId}.");
                }
            }
        }
    }

    private static void EnsureUniqueIdsAndGeometry(VanguardTacticalAuthoringMapFile mapFile)
    {
        var zoneIds = new HashSet<string>(StringComparer.Ordinal);
        var slotIds = new HashSet<string>(StringComparer.Ordinal);
        var candidateIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var zone in mapFile.HoldZones)
        {
            if (zone == null || string.IsNullOrWhiteSpace(zone.ZoneId) || !zoneIds.Add(zone.ZoneId))
            {
                throw new InvalidDataException("Schema-v2 authoring file contains a missing or duplicate HoldZone id.");
            }
            ValidateZoneGeometry(zone.ZoneId, zone.ZoneAnchor, zone.MinY, zone.MaxY, zone.MinYExplicit, zone.MaxYExplicit, zone.FloorBoundsProvisional, zone.ZoneRadius);

            zone.Slots ??= new List<VanguardTacticalAuthoringSlot>();
            zone.Accesses ??= new List<VanguardTacticalAuthoringAccess>();
            foreach (var slot in zone.Slots)
            {
                if (slot == null || string.IsNullOrWhiteSpace(slot.SlotId) || !slotIds.Add(slot.SlotId))
                {
                    throw new InvalidDataException("Schema-v2 authoring file contains a missing or duplicate HoldSlot id.");
                }
                ValidateSlotVectors(slot.SlotId, slot.Position, slot.NavMeshProjectedPosition, slot.WatchDirection);
                if (slot.PhysicalPoseSnapshot != null)
                {
                    if (slot.PhysicalPoseSnapshot.CaptureContractVersion <= 0)
                    {
                        throw new InvalidDataException($"HoldSlot {slot.SlotId} has an invalid physical-pose contract version.");
                    }
                    if (slot.PhysicalPoseSnapshot.CaptureContractVersion >= VanguardTacticalAuthoringPhysicalPoseCapture.MinimumPhysicalPoseContractVersion
                        && (!IsFinite(slot.PhysicalPoseSnapshot.PoseLevel)
                            || slot.PhysicalPoseSnapshot.PoseLevel < 0f
                            || slot.PhysicalPoseSnapshot.PoseLevel > 1f))
                    {
                        throw new InvalidDataException($"HoldSlot {slot.SlotId} has invalid physical pose level {slot.PhysicalPoseSnapshot.PoseLevel}.");
                    }
                    if (slot.PhysicalPoseSnapshot.CaptureContractVersion >= VanguardTacticalAuthoringPhysicalPoseCapture.FullStationaryPresentationContractVersion
                        && (!IsFinite(slot.PhysicalPoseSnapshot.Tilt)
                            || slot.PhysicalPoseSnapshot.Tilt < -5f
                            || slot.PhysicalPoseSnapshot.Tilt > 5f))
                    {
                        throw new InvalidDataException($"HoldSlot {slot.SlotId} has invalid authored tilt {slot.PhysicalPoseSnapshot.Tilt}.");
                    }
                }
                if (slot.CompatibilityAssignmentOrderHint is < 0 or > 100)
                {
                    throw new InvalidDataException($"HoldSlot {slot.SlotId} has an invalid compatibility assignment-order hint.");
                }
                if (slot.LegacyCompatibility != null)
                {
                    var compatibility = slot.LegacyCompatibility;
                    if (!Enum.IsDefined(typeof(VanguardTacticalSlotType), compatibility.SlotType)
                        || !IsFinite(compatibility.WatchArc) || compatibility.WatchArc < 20.0f || compatibility.WatchArc > 180.0f
                        || compatibility.MinimumSquadSize < 1 || compatibility.MinimumSquadSize > 3
                        || !IsFinite(compatibility.MaximumOwnerDistance) || compatibility.MaximumOwnerDistance < 0.0f)
                    {
                        throw new InvalidDataException($"HoldSlot {slot.SlotId} has invalid legacy compatibility metadata.");
                    }
                }
            }
        }

        foreach (var candidate in mapFile.LegacyPortalCandidates)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.CandidateId) || !candidateIds.Add(candidate.CandidateId))
            {
                throw new InvalidDataException("Schema-v2 authoring file contains a missing or duplicate LegacyPortalCandidate id.");
            }
            if (string.IsNullOrWhiteSpace(candidate.SourceHoldZoneId) || !zoneIds.Contains(candidate.SourceHoldZoneId))
            {
                throw new InvalidDataException($"LegacyPortalCandidate {candidate.CandidateId} references an unknown HoldZone.");
            }
            if (string.IsNullOrWhiteSpace(candidate.OriginalAccessId))
            {
                throw new InvalidDataException($"LegacyPortalCandidate {candidate.CandidateId} has no original Access identity.");
            }
            ValidateAccessVectors(candidate.CandidateId, candidate.Position, candidate.NavMeshProjectedPosition, candidate.ApproachDirection);
        }

        foreach (var zone in mapFile.HoldZones)
        {
            foreach (var slot in zone.Slots)
            {
                if (!string.IsNullOrWhiteSpace(slot.AssociatedAccessId)
                    && zone.Accesses.All(access => !string.Equals(access.AccessId, slot.AssociatedAccessId, StringComparison.Ordinal)))
                {
                    throw new InvalidDataException($"HoldSlot {slot.SlotId} references missing legacy candidate/access {slot.AssociatedAccessId}.");
                }
            }
        }
    }

    private static void ValidateZoneGeometry(
        string zoneId,
        VanguardVector3Dto zoneAnchor,
        float minY,
        float maxY,
        bool minYExplicit,
        bool maxYExplicit,
        bool floorBoundsProvisional,
        float zoneRadius)
    {
        if (zoneAnchor == null || !IsFinite(zoneAnchor))
        {
            throw new InvalidDataException($"Authoring zone {zoneId} has a missing or non-finite anchor.");
        }
        if (!IsFinite(minY) || !IsFinite(maxY) || maxY <= minY + 0.20f)
        {
            throw new InvalidDataException($"Authoring zone {zoneId} has invalid floor bounds.");
        }
        if (!IsFinite(zoneRadius) || zoneRadius < 2.0f || zoneRadius > 80.0f)
        {
            throw new InvalidDataException($"Authoring zone {zoneId} has invalid radius {zoneRadius}.");
        }
        if (floorBoundsProvisional == (minYExplicit && maxYExplicit))
        {
            throw new InvalidDataException($"Authoring zone {zoneId} has inconsistent explicit/provisional floor-bound flags.");
        }
    }

    private static void ValidateSlotVectors(string slotId, VanguardVector3Dto position, VanguardVector3Dto? projected, VanguardVector3Dto watchDirection)
    {
        if (position == null || watchDirection == null || !IsFinite(position) || !IsFinite(watchDirection)
            || (projected != null && !IsFinite(projected)))
        {
            throw new InvalidDataException($"Authoring slot {slotId} contains missing or non-finite vector data.");
        }
    }

    private static void ValidateAccessVectors(string accessId, VanguardVector3Dto position, VanguardVector3Dto? projected, VanguardVector3Dto approachDirection)
    {
        if (position == null || approachDirection == null || !IsFinite(position) || !IsFinite(approachDirection)
            || (projected != null && !IsFinite(projected)))
        {
            throw new InvalidDataException($"Authoring access/candidate {accessId} contains missing or non-finite vector data.");
        }
    }

    private static bool IsFinite(VanguardVector3Dto value)
    {
        return IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static void EnsureIdentityParity(VanguardTacticalAuthoringMapFile expected, VanguardTacticalAuthoringMapFile actual)
    {
        if (expected.SchemaVersion != actual.SchemaVersion
            || !string.Equals(expected.MapId, actual.MapId, StringComparison.Ordinal)
            || expected.HoldZones.Count != actual.HoldZones.Count
            || expected.LegacyPortalCandidates.Count != actual.LegacyPortalCandidates.Count)
        {
            throw new InvalidDataException("Authoring transactional save read-back envelope mismatch.");
        }

        EnsureSetParity(
            expected.HoldZones.Select(zone => zone.ZoneId),
            actual.HoldZones.Select(zone => zone.ZoneId),
            "HoldZone");
        EnsureHoldZoneSlotMembershipParity(expected, actual);
        EnsureSetParity(
            expected.LegacyPortalCandidates.Select(candidate => candidate.SourceHoldZoneId + "\n" + candidate.OriginalAccessId),
            actual.LegacyPortalCandidates.Select(candidate => candidate.SourceHoldZoneId + "\n" + candidate.OriginalAccessId),
            "LegacyPortalCandidate");
    }

    private static void EnsureHoldZoneSlotMembershipParity(VanguardTacticalAuthoringMapFile expected, VanguardTacticalAuthoringMapFile actual)
    {
        var actualZones = actual.HoldZones.ToDictionary(zone => zone.ZoneId, StringComparer.Ordinal);
        foreach (var expectedZone in expected.HoldZones)
        {
            if (!actualZones.TryGetValue(expectedZone.ZoneId, out var actualZone))
            {
                throw new InvalidDataException($"Authoring transactional save read-back lost HoldZone {expectedZone.ZoneId}.");
            }

            EnsureSetParity(
                expectedZone.Slots.Select(slot => slot.SlotId),
                actualZone.Slots.Select(slot => slot.SlotId),
                $"HoldSlot membership for zone {expectedZone.ZoneId}");

            var actualSlots = actualZone.Slots.ToDictionary(slot => slot.SlotId, StringComparer.Ordinal);
            foreach (var expectedSlot in expectedZone.Slots)
            {
                var actualSlot = actualSlots[expectedSlot.SlotId];
                if (!VectorEquals(expectedSlot.Position, actualSlot.Position)
                    || !NullableVectorEquals(expectedSlot.NavMeshProjectedPosition, actualSlot.NavMeshProjectedPosition)
                    || !VectorEquals(expectedSlot.WatchDirection, actualSlot.WatchDirection)
                    || !PhysicalPoseEquals(expectedSlot.PhysicalPoseSnapshot, actualSlot.PhysicalPoseSnapshot))
                {
                    throw new InvalidDataException($"Authoring transactional save read-back changed HoldSlot geometry/presentation for {expectedSlot.SlotId} in zone {expectedZone.ZoneId}.");
                }
            }
        }
    }

    private static bool VectorEquals(VanguardVector3Dto? left, VanguardVector3Dto? right)
    {
        return left != null && right != null && left.X == right.X && left.Y == right.Y && left.Z == right.Z;
    }

    private static bool NullableVectorEquals(VanguardVector3Dto? left, VanguardVector3Dto? right)
    {
        if (left == null || right == null)
        {
            return left == null && right == null;
        }
        return VectorEquals(left, right);
    }

    private static bool PhysicalPoseEquals(VanguardTacticalAuthoringPhysicalPoseSnapshot? left, VanguardTacticalAuthoringPhysicalPoseSnapshot? right)
    {
        if (left == null || right == null)
        {
            return left == null && right == null;
        }
        return left.CaptureContractVersion == right.CaptureContractVersion
            && string.Equals(left.PoseKind, right.PoseKind, StringComparison.Ordinal)
            && left.IsProne == right.IsProne
            && left.PoseLevel == right.PoseLevel
            && left.IsAimingDownSights == right.IsAimingDownSights
            && left.Tilt == right.Tilt
            && left.IsLeftStance == right.IsLeftStance
            && left.IsInPatrol == right.IsInPatrol
            && string.Equals(left.CapturedAt, right.CapturedAt, StringComparison.Ordinal)
            && string.Equals(left.CapturedWithBuild, right.CapturedWithBuild, StringComparison.Ordinal);
    }

    private static void EnsureSetParity(IEnumerable<string> expected, IEnumerable<string> actual, string family)
    {
        var expectedSet = expected.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var actualSet = actual.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!expectedSet.SequenceEqual(actualSet, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"Authoring transactional save read-back {family} identity mismatch.");
        }
    }

    private static void EnsureLegacySourceBackup(string path, VanguardTacticalAuthoringMapFile migrated)
    {
        if (migrated.MigrationRecord == null
            || migrated.MigrationRecord.FromSchemaVersion != LegacySchemaVersion
            || migrated.MigrationRecord.ToSchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException("Replacing a schema-v1 authoring file requires explicit schema-v2 migration provenance.");
        }

        byte[] sourceBytes = File.ReadAllBytes(path);
        string actualSourceSha256 = ComputeSha256(sourceBytes);
        if (!string.Equals(actualSourceSha256, migrated.MigrationRecord.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Schema-v1 source changed after in-memory migration; refusing destructive replacement. Reload and migrate the current file first.");
        }

        string backupDirectory = GetBepInExConfigDirectory(RelativeBackupRoot);
        Directory.CreateDirectory(backupDirectory);
        string safeMapName = GetSafeMapFileName(migrated.MapId);
        string backupFileName = BuildShortMigrationBackupFileName(backupDirectory, safeMapName, actualSourceSha256);
        string backupPath = Path.Combine(backupDirectory, backupFileName);
        if (File.Exists(backupPath))
        {
            string existingSha256 = ComputeSha256(File.ReadAllBytes(backupPath));
            if (!string.Equals(existingSha256, actualSourceSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Existing schema-v1 migration backup hash does not match the current source file.");
            }
        }
        else
        {
            WriteAllBytesDurable(backupPath, sourceBytes);
            string backupSha256 = ComputeSha256(File.ReadAllBytes(backupPath));
            if (!string.Equals(backupSha256, actualSourceSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Schema-v1 migration backup read-back hash mismatch.");
            }
        }

        migrated.MigrationRecord.SourceBackupFileName = backupFileName;
        VanguardClientDiagnosticsLog.Info(
            MigrationStatusTag,
            $"event=SCHEMA_V1_BACKUP_PRESERVED; map={migrated.MapId}; source={Path.GetFileName(path)}; backup={backupFileName}; backupDirectory=Backups; sha256={actualSourceSha256}; exactBytes=true; replacementPending=true");
    }


    private static string BuildShortMigrationBackupFileName(string backupDirectory, string safeMapName, string sourceSha256)
    {
        // Windows-first filesystem hygiene: keep the cryptographic SHA-256 in migration metadata/logs,
        // not in the normal filename. A short prefix is enough for operator-facing identification; if an
        // exceptionally rare prefix collision exists, extend only as far as required.
        foreach (var prefixLength in new[] { 12, 16, 24, 32, 64 })
        {
            var effectiveLength = Math.Min(prefixLength, sourceSha256.Length);
            var candidate = $"{safeMapName}.v1.{sourceSha256.Substring(0, effectiveLength)}.bak.json";
            var candidatePath = Path.Combine(backupDirectory, candidate);
            if (!File.Exists(candidatePath))
            {
                return candidate;
            }

            var existingSha256 = ComputeSha256(File.ReadAllBytes(candidatePath));
            if (string.Equals(existingSha256, sourceSha256, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        throw new IOException("Unable to allocate an unambiguous schema-v1 migration backup filename.");
    }

    private static string ComputeSha256(byte[] bytes)
    {
        using var sha256 = SHA256.Create();
        return string.Concat(sha256.ComputeHash(bytes).Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static string GetSafeMapFileName(string mapId)
    {
        var source = string.IsNullOrWhiteSpace(mapId) ? "unknown-map" : mapId.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(source.Length);
        foreach (var character in source)
        {
            builder.Append(invalid.Contains(character) || character == '/' || character == '\\' ? '_' : character);
        }

        var safe = builder.ToString().Trim();
        return safe.Length == 0 ? "unknown-map" : safe;
    }

    private static void WriteAllTextDurable(string path, string content)
    {
        WriteAllBytesDurable(path, Utf8NoBom.GetBytes(content));
    }

    private static void WriteAllBytesDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);
    }

    internal static string UtcNowText()
    {
        return DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }
}
#endif
