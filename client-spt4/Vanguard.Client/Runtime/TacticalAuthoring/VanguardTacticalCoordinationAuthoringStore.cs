#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Vanguard.Client.Diagnostics;

// Responsibility: Loads read-only Vanguard TC Base packs and transactionally persists the independent player TC User layer.
// Flow: The shared editor loads Base when present, loads/creates User from BepInEx config, validates each layer, and saves only User through temp/readback/replace semantics.
// Authority boundary: This store is map-authoring persistence only; Base can never be written here and no TC object is copied into the Tactical Authoring HOLD document.
// Invariant: Base/User schemas fail closed on invalid identity or geometry, User saves never overwrite Base, and future unknown schemas are rejected rather than guessed.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal static class VanguardTacticalCoordinationAuthoringStore
{
    public const int CurrentSchemaVersion = 1;
    public const string BaseLayer = "VanguardBase";
    public const string UserLayer = "User";
    private const string RelativeBaseRoot = "plugins/Vanguard.Client/TacticalCoordination/Base/Maps";
    private const string RelativeUserRoot = "config/Vanguard/TacticalCoordination/User/Maps";
    private const string StatusTag = "VANGUARD_TACTICAL_COORDINATION_AUTHORING_STATUS";
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Include
    };

    public static string GetBaseMapPath(string mapId)
    {
        return Path.Combine(GetBepInExRootDirectory(RelativeBaseRoot), GetSafeMapFileName(mapId) + ".tc.json");
    }

    public static string GetUserMapPath(string mapId)
    {
        return Path.Combine(GetBepInExRootDirectory(RelativeUserRoot), GetSafeMapFileName(mapId) + ".tc.user.json");
    }

    public static VanguardTacticalCoordinationAuthoringMapFile LoadBaseOrEmpty(string mapId, string mapRevision, string eftClientVersion, out bool loadedExisting)
    {
        var path = GetBaseMapPath(mapId);
        if (!File.Exists(path))
        {
            loadedExisting = false;
            return CreateEmpty(mapId, BaseLayer, mapRevision, eftClientVersion, sourcePackVersion: string.Empty);
        }

        loadedExisting = true;
        return LoadRequired(path, mapId, BaseLayer);
    }

    public static VanguardTacticalCoordinationAuthoringMapFile LoadUserOrCreate(string mapId, string mapRevision, string eftClientVersion, out bool loadedExisting)
    {
        var path = GetUserMapPath(mapId);
        if (!File.Exists(path))
        {
            loadedExisting = false;
            return CreateEmpty(mapId, UserLayer, mapRevision, eftClientVersion, sourcePackVersion: string.Empty);
        }

        loadedExisting = true;
        return LoadRequired(path, mapId, UserLayer);
    }

    public static VanguardTacticalCoordinationAuthoringMapFile ReloadUser(string mapId)
    {
        return LoadRequired(GetUserMapPath(mapId), mapId, UserLayer);
    }

    public static VanguardTacticalCoordinationAuthoringMapFile ReloadBaseOrEmpty(string mapId, string mapRevision, string eftClientVersion, out bool loadedExisting)
    {
        return LoadBaseOrEmpty(mapId, mapRevision, eftClientVersion, out loadedExisting);
    }

    public static void SaveUser(VanguardTacticalCoordinationAuthoringMapFile mapFile)
    {
        if (mapFile is null)
        {
            throw new ArgumentNullException(nameof(mapFile));
        }

        mapFile.SchemaVersion = CurrentSchemaVersion;
        mapFile.Layer = UserLayer;
        mapFile.LastSavedAt = UtcNowText();
        mapFile.LastSavedWithBuild = VanguardBuildVersion.BuildLabel;
        ValidateEnvelope(mapFile, mapFile.MapId, UserLayer);

        var path = GetUserMapPath(mapFile.MapId);
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("TC User map path has no parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
        var backupPath = path + ".bak";
        try
        {
            var json = JsonConvert.SerializeObject(mapFile, JsonSettings);
            WriteAllTextDurable(tempPath, json);

            var readBackJson = File.ReadAllText(tempPath, Utf8NoBom);
            var readBack = JsonConvert.DeserializeObject<VanguardTacticalCoordinationAuthoringMapFile>(readBackJson, JsonSettings)
                ?? throw new InvalidDataException("TC User save read-back deserialized to null.");
            ValidateEnvelope(readBack, mapFile.MapId, UserLayer);
            EnsureIdentityParity(mapFile, readBack);

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, backupPath, true);
            }
            else
            {
                File.Move(tempPath, path);
            }

            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"event=TC_USER_COMMIT; map={mapFile.MapId}; portals={mapFile.Portals.Count}; passagePaths={mapFile.PassagePaths.Count}; tacticalAnchors={mapFile.TacticalAnchors.Count}; legacyPromotions={mapFile.LegacyPortalPromotions.Count}; baseWrite=false; transactionalReadback=true; path={path}");
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public static IReadOnlyList<VanguardTacticalCoordinationResolvedPortal> ResolvePortals(
        VanguardTacticalCoordinationAuthoringMapFile baseMap,
        VanguardTacticalCoordinationAuthoringMapFile userMap)
    {
        var resolved = new List<VanguardTacticalCoordinationResolvedPortal>(baseMap.Portals.Count + userMap.Portals.Count);
        resolved.AddRange(baseMap.Portals.Select(portal => new VanguardTacticalCoordinationResolvedPortal
        {
            Portal = portal,
            Origin = BaseLayer,
            ReadOnly = true
        }));
        resolved.AddRange(userMap.Portals.Select(portal => new VanguardTacticalCoordinationResolvedPortal
        {
            Portal = portal,
            Origin = UserLayer,
            ReadOnly = false
        }));
        return resolved;
    }

    public static IReadOnlyList<VanguardTacticalCoordinationResolvedPassagePath> ResolvePassagePaths(
        VanguardTacticalCoordinationAuthoringMapFile baseMap,
        VanguardTacticalCoordinationAuthoringMapFile userMap)
    {
        var resolved = new List<VanguardTacticalCoordinationResolvedPassagePath>(baseMap.PassagePaths.Count + userMap.PassagePaths.Count);
        resolved.AddRange(baseMap.PassagePaths.Select(path => new VanguardTacticalCoordinationResolvedPassagePath
        {
            PassagePath = path,
            Origin = BaseLayer,
            ReadOnly = true
        }));
        resolved.AddRange(userMap.PassagePaths.Select(path => new VanguardTacticalCoordinationResolvedPassagePath
        {
            PassagePath = path,
            Origin = UserLayer,
            ReadOnly = false
        }));
        return resolved;
    }

    public static IReadOnlyList<VanguardTacticalCoordinationResolvedAnchor> ResolveTacticalAnchors(
        VanguardTacticalCoordinationAuthoringMapFile baseMap,
        VanguardTacticalCoordinationAuthoringMapFile userMap)
    {
        var resolved = new List<VanguardTacticalCoordinationResolvedAnchor>(baseMap.TacticalAnchors.Count + userMap.TacticalAnchors.Count);
        resolved.AddRange(baseMap.TacticalAnchors.Select(anchor => new VanguardTacticalCoordinationResolvedAnchor
        {
            TacticalAnchor = anchor,
            Origin = BaseLayer,
            ReadOnly = true
        }));
        resolved.AddRange(userMap.TacticalAnchors.Select(anchor => new VanguardTacticalCoordinationResolvedAnchor
        {
            TacticalAnchor = anchor,
            Origin = UserLayer,
            ReadOnly = false
        }));
        return resolved;
    }

    private static VanguardTacticalCoordinationAuthoringMapFile CreateEmpty(
        string mapId,
        string layer,
        string mapRevision,
        string eftClientVersion,
        string sourcePackVersion)
    {
        var now = UtcNowText();
        return new VanguardTacticalCoordinationAuthoringMapFile
        {
            SchemaVersion = CurrentSchemaVersion,
            MapId = mapId,
            Layer = layer,
            SourcePackVersion = sourcePackVersion,
            MapRevision = mapRevision,
            EftClientVersion = eftClientVersion,
            CreatedAt = now,
            LastSavedAt = string.Empty,
            CreatedWithBuild = VanguardBuildVersion.BuildLabel,
            LastSavedWithBuild = string.Empty,
            Portals = new List<VanguardTacticalAuthoringPortal>(),
            PassagePaths = new List<VanguardTacticalAuthoringPassagePath>(),
            TacticalAnchors = new List<VanguardTacticalAuthoringAnchor>(),
            LegacyPortalPromotions = new List<VanguardTacticalCoordinationLegacyPortalPromotion>()
        };
    }

    private static VanguardTacticalCoordinationAuthoringMapFile LoadRequired(string path, string expectedMapId, string expectedLayer)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("No Vanguard Tactical Coordination authoring file exists for this map.", path);
        }

        var json = File.ReadAllText(path, Utf8NoBom);
        var mapFile = JsonConvert.DeserializeObject<VanguardTacticalCoordinationAuthoringMapFile>(json, JsonSettings)
            ?? throw new InvalidDataException("TC authoring map file deserialized to null.");
        ValidateEnvelope(mapFile, expectedMapId, expectedLayer);
        return mapFile;
    }

    private static void ValidateEnvelope(VanguardTacticalCoordinationAuthoringMapFile mapFile, string expectedMapId, string expectedLayer)
    {
        if (mapFile.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported TC authoring schema {mapFile.SchemaVersion}; current={CurrentSchemaVersion}. Unknown future schemas fail closed.");
        }
        if (string.IsNullOrWhiteSpace(mapFile.MapId) || !string.Equals(mapFile.MapId, expectedMapId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"TC authoring map identity mismatch. expected={expectedMapId}; actual={mapFile.MapId}");
        }
        if (!string.Equals(mapFile.Layer, expectedLayer, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"TC authoring layer mismatch. expected={expectedLayer}; actual={mapFile.Layer}");
        }

        mapFile.Portals ??= new List<VanguardTacticalAuthoringPortal>();
        mapFile.PassagePaths ??= new List<VanguardTacticalAuthoringPassagePath>();
        mapFile.TacticalAnchors ??= new List<VanguardTacticalAuthoringAnchor>();
        mapFile.LegacyPortalPromotions ??= new List<VanguardTacticalCoordinationLegacyPortalPromotion>();

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var portal in mapFile.Portals)
        {
            if (portal == null || string.IsNullOrWhiteSpace(portal.PortalId) || !ids.Add(portal.PortalId))
            {
                throw new InvalidDataException("TC authoring file contains a missing or duplicate Portal id.");
            }
            ValidateFinite(portal.PortalId, portal.Position);
            if (portal.NavMeshProjectedPosition != null)
            {
                ValidateFinite(portal.PortalId, portal.NavMeshProjectedPosition);
            }
        }

        foreach (var passage in mapFile.PassagePaths)
        {
            if (passage == null || string.IsNullOrWhiteSpace(passage.PassagePathId) || !ids.Add(passage.PassagePathId))
            {
                throw new InvalidDataException("TC authoring file contains a missing or duplicate PassagePath id.");
            }
            passage.ControlPoints ??= new List<VanguardTacticalAuthoringPassageControlPoint>();
            passage.ResolvedPolyline ??= new List<VanguardVector3Dto>();
            if (passage.ControlPoints.Count < 2)
            {
                throw new InvalidDataException($"PassagePath {passage.PassagePathId} must preserve at least two authored control points.");
            }
            if (passage.ResolutionRevision < 0)
            {
                throw new InvalidDataException($"PassagePath {passage.PassagePathId} has an invalid negative resolution revision.");
            }

            var controlIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var point in passage.ControlPoints)
            {
                if (point == null || string.IsNullOrWhiteSpace(point.ControlPointId) || !controlIds.Add(point.ControlPointId))
                {
                    throw new InvalidDataException($"PassagePath {passage.PassagePathId} contains a missing or duplicate control point id.");
                }
                ValidateFinite(point.ControlPointId, point.Position);
                if (point.NavMeshProjectedPosition != null)
                {
                    ValidateFinite(point.ControlPointId, point.NavMeshProjectedPosition);
                }
            }

            for (var index = 0; index < passage.ResolvedPolyline.Count; index++)
            {
                ValidateFinite($"{passage.PassagePathId}.resolved[{index}]", passage.ResolvedPolyline[index]);
            }
        }

        foreach (var anchor in mapFile.TacticalAnchors)
        {
            if (anchor == null || string.IsNullOrWhiteSpace(anchor.AnchorId) || !ids.Add(anchor.AnchorId))
            {
                throw new InvalidDataException("TC authoring file contains a missing or duplicate TacticalAnchor id.");
            }
            ValidateFinite(anchor.AnchorId, anchor.Center);
            if (!IsFinite(anchor.Radius) || anchor.Radius < 1.0f || anchor.Radius > 80.0f)
            {
                throw new InvalidDataException($"TacticalAnchor {anchor.AnchorId} has invalid radius {anchor.Radius}.");
            }
            anchor.AuthoredPoseSlots ??= new List<VanguardTacticalAuthoringPoseSlot>();
            var poseIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var poseSlot in anchor.AuthoredPoseSlots)
            {
                if (poseSlot == null || string.IsNullOrWhiteSpace(poseSlot.PoseSlotId) || !poseIds.Add(poseSlot.PoseSlotId))
                {
                    throw new InvalidDataException($"TacticalAnchor {anchor.AnchorId} contains a missing or duplicate PoseSlot id.");
                }
                ValidateFinite(poseSlot.PoseSlotId, poseSlot.Position);
                if (poseSlot.NavMeshProjectedPosition != null)
                {
                    ValidateFinite(poseSlot.PoseSlotId, poseSlot.NavMeshProjectedPosition);
                }
                ValidateFinite(poseSlot.PoseSlotId + ".view", poseSlot.ViewDirection);
                if (poseSlot.PhysicalPoseSnapshot != null)
                {
                    if (poseSlot.PhysicalPoseSnapshot.CaptureContractVersion <= 0)
                    {
                        throw new InvalidDataException($"PoseSlot {poseSlot.PoseSlotId} has an invalid physical-pose contract version.");
                    }
                    if (poseSlot.PhysicalPoseSnapshot.CaptureContractVersion >= VanguardTacticalAuthoringPhysicalPoseCapture.MinimumPhysicalPoseContractVersion
                        && (!IsFinite(poseSlot.PhysicalPoseSnapshot.PoseLevel)
                            || poseSlot.PhysicalPoseSnapshot.PoseLevel < 0f
                            || poseSlot.PhysicalPoseSnapshot.PoseLevel > 1f))
                    {
                        throw new InvalidDataException($"PoseSlot {poseSlot.PoseSlotId} has invalid physical pose level {poseSlot.PhysicalPoseSnapshot.PoseLevel}.");
                    }
                    if (poseSlot.PhysicalPoseSnapshot.CaptureContractVersion >= VanguardTacticalAuthoringPhysicalPoseCapture.FullStationaryPresentationContractVersion
                        && (!IsFinite(poseSlot.PhysicalPoseSnapshot.Tilt)
                            || poseSlot.PhysicalPoseSnapshot.Tilt < -5f
                            || poseSlot.PhysicalPoseSnapshot.Tilt > 5f))
                    {
                        throw new InvalidDataException($"PoseSlot {poseSlot.PoseSlotId} has invalid authored tilt {poseSlot.PhysicalPoseSnapshot.Tilt}.");
                    }
                }
            }
        }

        var promotionCandidates = new HashSet<string>(StringComparer.Ordinal);
        var promotedPortals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var promotion in mapFile.LegacyPortalPromotions)
        {
            if (promotion == null || string.IsNullOrWhiteSpace(promotion.CandidateId) || !promotionCandidates.Add(promotion.CandidateId))
            {
                throw new InvalidDataException("TC User file contains a missing or duplicate legacy Portal candidate promotion.");
            }
            if (string.IsNullOrWhiteSpace(promotion.PortalId) || !promotedPortals.Add(promotion.PortalId)
                || mapFile.Portals.All(portal => !string.Equals(portal.PortalId, promotion.PortalId, StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"Legacy Portal promotion {promotion.CandidateId} references an unknown or duplicate User Portal.");
            }
        }
    }

    private static void EnsureIdentityParity(
        VanguardTacticalCoordinationAuthoringMapFile expected,
        VanguardTacticalCoordinationAuthoringMapFile readBack)
    {
        static string[] PortalIds(VanguardTacticalCoordinationAuthoringMapFile value) => value.Portals.Select(portal => portal.PortalId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        static string[] PassageIds(VanguardTacticalCoordinationAuthoringMapFile value) => value.PassagePaths.Select(path => path.PassagePathId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        static string[] PassageControlPointIds(VanguardTacticalCoordinationAuthoringMapFile value) => value.PassagePaths
            .SelectMany(path => path.ControlPoints.Select(point => path.PassagePathId + "\n" + point.ControlPointId))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        static string[] AnchorIds(VanguardTacticalCoordinationAuthoringMapFile value) => value.TacticalAnchors.Select(anchor => anchor.AnchorId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        static string[] PoseSlotIds(VanguardTacticalCoordinationAuthoringMapFile value) => value.TacticalAnchors
            .SelectMany(anchor => anchor.AuthoredPoseSlots.Select(slot => anchor.AnchorId + "\n" + slot.PoseSlotId))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        if (!PortalIds(expected).SequenceEqual(PortalIds(readBack), StringComparer.Ordinal)
            || !PassageIds(expected).SequenceEqual(PassageIds(readBack), StringComparer.Ordinal)
            || !PassageControlPointIds(expected).SequenceEqual(PassageControlPointIds(readBack), StringComparer.Ordinal)
            || !AnchorIds(expected).SequenceEqual(AnchorIds(readBack), StringComparer.Ordinal)
            || !PoseSlotIds(expected).SequenceEqual(PoseSlotIds(readBack), StringComparer.Ordinal))
        {
            throw new InvalidDataException("TC User save read-back changed durable entity identity.");
        }

        var actualAnchors = readBack.TacticalAnchors.ToDictionary(anchor => anchor.AnchorId, StringComparer.Ordinal);
        foreach (var expectedAnchor in expected.TacticalAnchors)
        {
            var actualAnchor = actualAnchors[expectedAnchor.AnchorId];
            var actualPoseSlots = actualAnchor.AuthoredPoseSlots.ToDictionary(slot => slot.PoseSlotId, StringComparer.Ordinal);
            foreach (var expectedSlot in expectedAnchor.AuthoredPoseSlots)
            {
                var actualSlot = actualPoseSlots[expectedSlot.PoseSlotId];
                if (!VectorEquals(expectedSlot.Position, actualSlot.Position)
                    || !NullableVectorEquals(expectedSlot.NavMeshProjectedPosition, actualSlot.NavMeshProjectedPosition)
                    || !VectorEquals(expectedSlot.ViewDirection, actualSlot.ViewDirection)
                    || !PhysicalPoseEquals(expectedSlot.PhysicalPoseSnapshot, actualSlot.PhysicalPoseSnapshot))
                {
                    throw new InvalidDataException($"TC User save read-back changed PoseSlot geometry/presentation for {expectedSlot.PoseSlotId} in anchor {expectedAnchor.AnchorId}.");
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

    private static string GetBepInExRootDirectory(string relativePath)
    {
        var bepInExRoot = BepInEx.Paths.BepInExRootPath;
        if (string.IsNullOrWhiteSpace(bepInExRoot))
        {
            throw new InvalidOperationException("BepInEx root path is unavailable; TC authoring storage cannot be resolved safely.");
        }

        return Path.Combine(bepInExRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string GetSafeMapFileName(string mapId)
    {
        var source = string.IsNullOrWhiteSpace(mapId) ? "unknown-map" : mapId.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(source.Length);
        foreach (var character in source)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }
        return builder.Length == 0 ? "unknown-map" : builder.ToString();
    }

    private static void WriteAllTextDurable(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, Utf8NoBom);
        writer.Write(content);
        writer.Flush();
        stream.Flush(true);
    }

    private static string UtcNowText()
    {
        return DateTimeOffset.UtcNow.ToString("O");
    }

    private static void ValidateFinite(string id, VanguardVector3Dto value)
    {
        if (value == null || !IsFinite(value.X) || !IsFinite(value.Y) || !IsFinite(value.Z))
        {
            throw new InvalidDataException($"TC authoring entity {id} contains missing or non-finite vector data.");
        }
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
#endif
