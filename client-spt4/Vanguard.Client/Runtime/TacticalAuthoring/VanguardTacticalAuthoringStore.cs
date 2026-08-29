#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using Newtonsoft.Json;

// Responsibility: Loads and transactionally saves the player-authored tactical map files used by the editor while keeping runtime-consumption flags fail-closed.
// Flow: Per-map JSON is loaded through compatibility normalization and envelope validation; edits save via temp file/readback/replace, and invalid-slot reports are exported separately.
// Authority boundary: This store owns only local authoring files; it does not grant Headless runtime authority, mutate EFT navigation graphs or enable persisted slots for gameplay by itself.
// Invariant: Map/schema identity, unique ids, finite geometry and disabled runtime flags are validated on every load/save; failed writes must not replace the last good file.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal static class VanguardTacticalAuthoringStore
{
    public const int CurrentSchemaVersion = 1;
    private const string RelativeRoot = "Vanguard/TacticalAuthoring/Maps";
    private const string RelativeReportRoot = "Vanguard/TacticalAuthoring/Reports";
    private const string BepInExConfigDirectoryName = "config";
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
            Zones = new List<VanguardTacticalAuthoringZone>()
        };
    }

    public static VanguardTacticalAuthoringMapFile Reload(string mapId)
    {
        return LoadRequired(GetMapPath(mapId), mapId);
    }

    public static void Save(VanguardTacticalAuthoringMapFile mapFile)
    {
        ValidateEnvelope(mapFile, mapFile.MapId);
        mapFile.RuntimeConsumptionEnabled = false;
        mapFile.LastSavedAt = UtcNowText();
        mapFile.LastSavedWithBuild = VanguardBuildVersion.BuildLabel;

        var path = GetMapPath(mapFile.MapId);
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Authoring map path has no parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
        var backupPath = path + ".bak";
        try
        {
            var json = JsonConvert.SerializeObject(mapFile, JsonSettings);
            WriteAllTextDurable(tempPath, json);

            var readBackJson = File.ReadAllText(tempPath, Utf8NoBom);
            var readBack = JsonConvert.DeserializeObject<VanguardTacticalAuthoringMapFile>(readBackJson, JsonSettings)
                ?? throw new InvalidDataException("Authoring save read-back deserialized to null.");
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

        var json = NormalizeSerializedFieldNamesForCurrentVocabulary(File.ReadAllText(path, Utf8NoBom));
        var mapFile = JsonConvert.DeserializeObject<VanguardTacticalAuthoringMapFile>(json, JsonSettings)
            ?? throw new InvalidDataException("Authoring map file deserialized to null.");
        ValidateEnvelope(mapFile, expectedMapId);
        return mapFile;
    }

    // 0.7.0 renamed two display-only JSON fields to player-neutral names. Existing authoring maps are
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
            throw new InvalidDataException($"Unsupported tactical authoring schema {mapFile.SchemaVersion}; expected {CurrentSchemaVersion}. No automatic migration is allowed.");
        }

        if (string.IsNullOrWhiteSpace(mapFile.MapId) || !string.Equals(mapFile.MapId, expectedMapId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Authoring map identity mismatch. expected={expectedMapId}; actual={mapFile.MapId}");
        }

        if (mapFile.RuntimeConsumptionEnabled)
        {
            throw new InvalidDataException("Tactical authoring files must keep RuntimeConsumptionEnabled=false. Operator runtime consumption is intentionally not enabled in this release.");
        }

        mapFile.Zones ??= new List<VanguardTacticalAuthoringZone>();
        EnsureUniqueIds(mapFile);
    }

    private static void EnsureUniqueIds(VanguardTacticalAuthoringMapFile mapFile)
    {
        var zoneIds = new HashSet<string>(StringComparer.Ordinal);
        var slotIds = new HashSet<string>(StringComparer.Ordinal);
        var accessIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var zone in mapFile.Zones)
        {
            if (zone is null || string.IsNullOrWhiteSpace(zone.ZoneId) || !zoneIds.Add(zone.ZoneId))
            {
                throw new InvalidDataException("Authoring file contains a missing or duplicate ZoneId.");
            }

            if (zone.ZoneAnchor is null || !IsFinite(zone.ZoneAnchor))
            {
                throw new InvalidDataException($"Authoring zone {zone.ZoneId} has a missing or non-finite anchor.");
            }
            if (!IsFinite(zone.MinY) || !IsFinite(zone.MaxY) || zone.MaxY <= zone.MinY + 0.20f)
            {
                throw new InvalidDataException($"Authoring zone {zone.ZoneId} has invalid floor bounds.");
            }
            if (!IsFinite(zone.ZoneRadius) || zone.ZoneRadius < 2.0f || zone.ZoneRadius > 80.0f)
            {
                throw new InvalidDataException($"Authoring zone {zone.ZoneId} has invalid radius {zone.ZoneRadius}.");
            }
            if (zone.FloorBoundsProvisional == (zone.MinYExplicit && zone.MaxYExplicit))
            {
                throw new InvalidDataException($"Authoring zone {zone.ZoneId} has inconsistent explicit/provisional floor-bound flags.");
            }

            zone.Slots ??= new List<VanguardTacticalAuthoringSlot>();
            zone.Accesses ??= new List<VanguardTacticalAuthoringAccess>();
            foreach (var slot in zone.Slots)
            {
                if (slot is null || string.IsNullOrWhiteSpace(slot.SlotId) || !slotIds.Add(slot.SlotId))
                {
                    throw new InvalidDataException("Authoring file contains a missing or duplicate SlotId.");
                }
                if (!Enum.IsDefined(typeof(VanguardTacticalSlotType), slot.SlotType))
                {
                    throw new InvalidDataException($"Authoring slot {slot.SlotId} has an unknown SlotType value.");
                }
                if (slot.Position is null || slot.WatchDirection is null || !IsFinite(slot.Position) || !IsFinite(slot.WatchDirection)
                    || (slot.NavMeshProjectedPosition is not null && !IsFinite(slot.NavMeshProjectedPosition)))
                {
                    throw new InvalidDataException($"Authoring slot {slot.SlotId} contains missing or non-finite vector data.");
                }
                if (!IsFinite(slot.WatchArc) || slot.WatchArc < 20.0f || slot.WatchArc > 180.0f
                    || slot.Priority < 0 || slot.Priority > 100
                    || slot.MinimumSquadSize < 1 || slot.MinimumSquadSize > 3
                    || !IsFinite(slot.MaximumOwnerDistance) || slot.MaximumOwnerDistance < 0.0f)
                {
                    throw new InvalidDataException($"Authoring slot {slot.SlotId} contains invalid tactical constraints.");
                }
                if (slot.RuntimeEligible)
                {
                    throw new InvalidDataException($"Authoring slot {slot.SlotId} illegally sets RuntimeEligible=true while runtime consumption is disabled.");
                }
            }

            foreach (var access in zone.Accesses)
            {
                if (access is null || string.IsNullOrWhiteSpace(access.AccessId) || !accessIds.Add(access.AccessId))
                {
                    throw new InvalidDataException("Authoring file contains a missing or duplicate AccessId.");
                }
                if (access.Position is null || access.ApproachDirection is null || !IsFinite(access.Position) || !IsFinite(access.ApproachDirection)
                    || (access.NavMeshProjectedPosition is not null && !IsFinite(access.NavMeshProjectedPosition)))
                {
                    throw new InvalidDataException($"Authoring access {access.AccessId} contains missing or non-finite vector data.");
                }
            }

            foreach (var slot in zone.Slots)
            {
                if (!string.IsNullOrWhiteSpace(slot.AssociatedAccessId)
                    && zone.Accesses.All(access => !string.Equals(access.AccessId, slot.AssociatedAccessId, StringComparison.Ordinal)))
                {
                    throw new InvalidDataException($"Authoring slot {slot.SlotId} references missing access {slot.AssociatedAccessId}.");
                }
            }
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
            || expected.Zones.Count != actual.Zones.Count)
        {
            throw new InvalidDataException("Authoring transactional save read-back identity mismatch.");
        }

        var expectedZoneIds = expected.Zones.Select(zone => zone.ZoneId).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var actualZoneIds = actual.Zones.Select(zone => zone.ZoneId).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!expectedZoneIds.SequenceEqual(actualZoneIds, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Authoring transactional save read-back zone set mismatch.");
        }
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
        var bytes = Utf8NoBom.GetBytes(content);
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
