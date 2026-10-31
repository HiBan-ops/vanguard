#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Comfort.Common;
using EFT;
using Newtonsoft.Json;
using UnityEngine;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;

// Responsibility: Publishes the resolved Tactical Coordination Base plus User topology as a revisioned raid-scoped read/query snapshot and synchronizes it to the runtime authority.
// Flow: The authorized raid-scope writer loads committed TC layers, serializes a deterministic resolved topology, relays revisioned snapshots through the existing authoring exchange, and runtime authorities publish immutable read models.
// Authority boundary: The service transports and reads committed topology only; it never writes Base/User files, consumes HOLD persistence, acquires movement leases or commands Operators.
// Invariant: One raid-scoped writer owns the topology revision, stale remote revisions cannot replace newer state, Base/User provenance is preserved, and query callers only observe immutable published snapshots.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal static class VanguardTacticalCoordinationRuntimeReadService
{
    public const string StatusTag = "VANGUARD_TACTICAL_COORDINATION_RUNTIME_READ_STATUS";
    private const int TransportSchemaVersion = 1;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2.0d);
    private static readonly object Sync = new();
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.None,
        NullValueHandling = NullValueHandling.Include
    };

    private static GameWorld? activeWorld;
    private static string activeMapId = string.Empty;
    private static string raidSessionId = string.Empty;
    private static string localOwnerProfileId = string.Empty;
    private static string localMapJson = string.Empty;
    private static string localContentHash = string.Empty;
    private static long localRevision;
    private static long lastOfferedRevision;
    private static DateTimeOffset nextHeartbeatAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextLocalLoadAttemptAtUtc = DateTimeOffset.MinValue;
    private static string remoteSessionId = string.Empty;
    private static string remoteOwnerProfileId = string.Empty;
    private static long remoteRevision;
    private static VanguardTacticalCoordinationReadSnapshot current = VanguardTacticalCoordinationReadSnapshot.Empty;

    public static void Tick()
    {
        if (!TryGetRaidContext(out var world, out var mapId, out var ownerProfileId))
        {
            if (activeWorld != null)
            {
                Reset("raid_world_inactive");
            }
            return;
        }

        if (!ReferenceEquals(activeWorld, world) || !string.Equals(activeMapId, mapId, StringComparison.OrdinalIgnoreCase))
        {
            ResetRaid(world, mapId, ownerProfileId);
        }
        else if (!string.IsNullOrWhiteSpace(ownerProfileId))
        {
            localOwnerProfileId = ownerProfileId;
        }

        if (!CanPublishLocalTopology() || localRevision > 0 || DateTimeOffset.UtcNow < nextLocalLoadAttemptAtUtc)
        {
            return;
        }

        try
        {
            var eftClientVersion = Application.version ?? string.Empty;
            var mapRevision = "eft-client-" + (string.IsNullOrWhiteSpace(eftClientVersion) ? "unknown" : eftClientVersion.Trim());
            var baseMap = VanguardTacticalCoordinationAuthoringStore.LoadBaseOrEmpty(mapId, mapRevision, eftClientVersion, out _);
            var userMap = VanguardTacticalCoordinationAuthoringStore.LoadUserOrCreate(mapId, mapRevision, eftClientVersion, out _);
            PublishCommittedLocalLayers(baseMap, userMap, "raid_bootstrap");
        }
        catch (Exception exception)
        {
            nextLocalLoadAttemptAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1.5d);
            VanguardClientDiagnosticsLog.Warning(StatusTag,
                $"TC_RUNTIME_TOPOLOGY_LOAD_FAILED map={mapId}; type={exception.GetType().Name}; message={Safe(exception.Message)}; failClosed=true");
        }
    }

    public static void PublishCommittedLocalLayers(
        VanguardTacticalCoordinationAuthoringMapFile baseMap,
        VanguardTacticalCoordinationAuthoringMapFile userMap,
        string reason)
    {
        if (baseMap is null || userMap is null || !CanPublishLocalTopology())
        {
            return;
        }

        var mapId = Normalize(userMap.MapId);
        if (mapId.Length == 0 || !string.Equals(mapId, Normalize(baseMap.MapId), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("TC runtime topology cannot resolve mismatched Base/User map identities.");
        }

        var document = BuildTransportDocument(baseMap, userMap);
        var mapJson = JsonConvert.SerializeObject(document, JsonSettings);
        var contentHash = ComputeSha256(mapJson);
        if (string.Equals(contentHash, localContentHash, StringComparison.Ordinal))
        {
            return;
        }

        localRevision = localRevision == long.MaxValue ? 1 : Math.Max(1L, localRevision + 1L);
        localMapJson = mapJson;
        localContentHash = contentHash;
        nextHeartbeatAtUtc = DateTimeOffset.MinValue;

        if (VanguardFikaCompat.IsRuntimeSettingsConsumerAuthority && !VanguardFikaCompat.IsActualHeadlessProcess)
        {
            PublishReadSnapshot(document, localRevision, contentHash, localOwnerProfileId, "local_raid_scope_writer");
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_RUNTIME_TOPOLOGY_LOCAL revision={localRevision}; map={mapId}; portals={document.Portals.Count}; passagePaths={document.PassagePaths.Count}; tacticalAnchors={document.TacticalAnchors.Count}; hash={ShortHash(contentHash)}; reason={Safe(reason)}; raidScopeWriter=true; movementAuthority=false");
    }

    public static bool TryBuildOutgoingTopologySnapshot(DateTimeOffset now, out VanguardTacticalCoordinationTopologySnapshotDto snapshot)
    {
        snapshot = new VanguardTacticalCoordinationTopologySnapshotDto();
        if (!CanPublishLocalTopology() || localRevision <= 0 || string.IsNullOrWhiteSpace(localOwnerProfileId)
            || string.IsNullOrWhiteSpace(activeMapId) || string.IsNullOrWhiteSpace(raidSessionId))
        {
            return false;
        }

        bool revisionChanged = lastOfferedRevision != localRevision;
        if (!revisionChanged && now < nextHeartbeatAtUtc)
        {
            return false;
        }

        snapshot = new VanguardTacticalCoordinationTopologySnapshotDto
        {
            OwnerProfileId = localOwnerProfileId,
            RaidSessionId = raidSessionId,
            MapId = activeMapId,
            Active = true,
            Revision = localRevision,
            ContentHash = localContentHash,
            MapJson = revisionChanged ? localMapJson : string.Empty,
            UpdatedAtUtc = now,
            ClientBuild = VanguardBuildVersion.BuildLabel
        };
        lastOfferedRevision = localRevision;
        nextHeartbeatAtUtc = now + HeartbeatInterval;
        return true;
    }

    public static void ApplyRemoteTopologySnapshots(
        IReadOnlyList<VanguardTacticalCoordinationTopologySnapshotDto>? snapshots,
        DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRuntimeSettingsConsumerAuthority || snapshots == null || snapshots.Count == 0)
        {
            return;
        }

        if (CanPublishLocalTopology() && !VanguardFikaCompat.IsActualHeadlessProcess)
        {
            // The direct local/Fika host already owns this raid-scoped truth locally.
            return;
        }

        var latest = snapshots
            .Where(item => item != null && item.Active
                && string.Equals(Normalize(item.MapId), activeMapId, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.RaidSessionId)
                && item.Revision > 0
                && !string.IsNullOrWhiteSpace(item.MapJson))
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ThenByDescending(item => item.Revision)
            .ThenBy(item => item.OwnerProfileId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (latest == null)
        {
            return;
        }

        var normalizedSession = Normalize(latest.RaidSessionId);
        var normalizedOwner = Normalize(latest.OwnerProfileId);
        if (string.Equals(remoteSessionId, normalizedSession, StringComparison.Ordinal)
            && string.Equals(remoteOwnerProfileId, normalizedOwner, StringComparison.OrdinalIgnoreCase)
            && latest.Revision <= remoteRevision)
        {
            return;
        }

        try
        {
            var document = JsonConvert.DeserializeObject<VanguardTacticalCoordinationRuntimeTopologyDocument>(latest.MapJson, JsonSettings)
                ?? throw new InvalidOperationException("TC topology document deserialized to null.");
            ValidateTransportDocument(document, latest.MapId);
            var computedHash = ComputeSha256(latest.MapJson);
            if (!string.IsNullOrWhiteSpace(latest.ContentHash)
                && !string.Equals(latest.ContentHash, computedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("TC topology content hash mismatch.");
            }

            remoteSessionId = normalizedSession;
            remoteOwnerProfileId = normalizedOwner;
            remoteRevision = latest.Revision;
            PublishReadSnapshot(document, latest.Revision, computedHash, normalizedOwner, "remote_raid_scope_writer");
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_RUNTIME_TOPOLOGY_REMOTE_APPLIED revision={latest.Revision}; map={document.MapId}; owner={Safe(normalizedOwner)}; portals={document.Portals.Count}; passagePaths={document.PassagePaths.Count}; tacticalAnchors={document.TacticalAnchors.Count}; hash={ShortHash(computedHash)}; runtimeAuthority=true; movementAuthority=false; mainThreadApply=true; at={now:O}");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(StatusTag,
                $"TC_RUNTIME_TOPOLOGY_REMOTE_REJECTED owner={Safe(normalizedOwner)}; revision={latest.Revision}; type={exception.GetType().Name}; message={Safe(exception.Message)}; failClosed=true");
        }
    }

    public static bool TryGetSnapshot(out VanguardTacticalCoordinationReadSnapshot snapshot)
    {
        lock (Sync)
        {
            snapshot = current;
            return snapshot.IsAvailable;
        }
    }

    public static bool TryGetPortal(string portalId, out VanguardTacticalCoordinationReadPortal portal)
    {
        portal = null!;
        if (!TryGetSnapshot(out var snapshot) || string.IsNullOrWhiteSpace(portalId))
        {
            return false;
        }

        var match = snapshot.Portals.FirstOrDefault(item => string.Equals(item.PortalId, portalId, StringComparison.Ordinal));
        if (match == null)
        {
            return false;
        }

        portal = match;
        return true;
    }

    public static bool TryGetPassagePath(string passagePathId, out VanguardTacticalCoordinationReadPassagePath passagePath)
    {
        passagePath = null!;
        if (!TryGetSnapshot(out var snapshot) || string.IsNullOrWhiteSpace(passagePathId))
        {
            return false;
        }

        var match = snapshot.PassagePaths.FirstOrDefault(item => string.Equals(item.PassagePathId, passagePathId, StringComparison.Ordinal));
        if (match == null)
        {
            return false;
        }

        passagePath = match;
        return true;
    }

    public static bool TryGetTacticalAnchor(string anchorId, out VanguardTacticalCoordinationReadAnchor anchor)
    {
        anchor = null!;
        if (!TryGetSnapshot(out var snapshot) || string.IsNullOrWhiteSpace(anchorId))
        {
            return false;
        }

        var match = snapshot.TacticalAnchors.FirstOrDefault(item => string.Equals(item.AnchorId, anchorId, StringComparison.Ordinal));
        if (match == null)
        {
            return false;
        }

        anchor = match;
        return true;
    }

    public static bool TryFindNearestEnabledPortal(Vector3 position, float maximumDistanceMeters, out VanguardTacticalCoordinationReadPortal portal)
    {
        portal = null!;
        if (!TryGetSnapshot(out var snapshot) || maximumDistanceMeters < 0.0f)
        {
            return false;
        }

        float maximumSquared = maximumDistanceMeters * maximumDistanceMeters;
        var candidate = snapshot.Portals
            .Where(item => item.Enabled)
            .Select(item => new { Item = item, Distance = (item.Position - position).sqrMagnitude })
            .Where(item => item.Distance <= maximumSquared)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Item.PortalId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (candidate == null)
        {
            return false;
        }

        portal = candidate.Item;
        return true;
    }

    public static IReadOnlyList<VanguardTacticalCoordinationReadPassagePath> GetEnabledPassagePaths()
    {
        return TryGetSnapshot(out var snapshot)
            ? snapshot.PassagePaths.Where(item => item.Enabled).ToArray()
            : Array.Empty<VanguardTacticalCoordinationReadPassagePath>();
    }

    public static bool TryFindNearestEnabledAnchor(Vector3 position, float maximumDistanceMeters, out VanguardTacticalCoordinationReadAnchor anchor)
    {
        anchor = null!;
        if (!TryGetSnapshot(out var snapshot) || maximumDistanceMeters < 0.0f)
        {
            return false;
        }

        float maximumSquared = maximumDistanceMeters * maximumDistanceMeters;
        var candidate = snapshot.TacticalAnchors
            .Where(item => item.Enabled)
            .Select(item => new { Item = item, Distance = (item.Center - position).sqrMagnitude })
            .Where(item => item.Distance <= maximumSquared)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Item.AnchorId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (candidate == null)
        {
            return false;
        }

        anchor = candidate.Item;
        return true;
    }

    public static bool TryFindNearestEnabledPoseSlot(
        Vector3 position,
        float maximumDistanceMeters,
        out VanguardTacticalCoordinationReadAnchor anchor,
        out VanguardTacticalCoordinationReadPoseSlot poseSlot)
    {
        anchor = null!;
        poseSlot = null!;
        if (!TryGetSnapshot(out var snapshot) || maximumDistanceMeters < 0.0f)
        {
            return false;
        }

        float maximumSquared = maximumDistanceMeters * maximumDistanceMeters;
        var candidate = snapshot.TacticalAnchors
            .Where(item => item.Enabled)
            .SelectMany(item => item.PoseSlots
                .Where(slot => slot.Enabled)
                .Select(slot => new { Anchor = item, Slot = slot, Distance = (slot.Position - position).sqrMagnitude }))
            .Where(item => item.Distance <= maximumSquared)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Anchor.AnchorId, StringComparer.Ordinal)
            .ThenBy(item => item.Slot.PoseSlotId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (candidate == null)
        {
            return false;
        }

        anchor = candidate.Anchor;
        poseSlot = candidate.Slot;
        return true;
    }

    public static bool TryFindContainingEnabledAnchor(Vector3 position, out VanguardTacticalCoordinationReadAnchor anchor)
    {
        anchor = null!;
        if (!TryGetSnapshot(out var snapshot))
        {
            return false;
        }

        var candidate = snapshot.TacticalAnchors
            .Where(item => item.Enabled && (item.Center - position).sqrMagnitude <= item.Radius * item.Radius)
            .OrderBy(item => (item.Center - position).sqrMagnitude)
            .ThenBy(item => item.AnchorId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (candidate == null)
        {
            return false;
        }

        anchor = candidate;
        return true;
    }

    public static IReadOnlyList<VanguardTacticalCoordinationReadPoseSlot> GetEnabledPoseSlots(string anchorId)
    {
        if (!TryGetSnapshot(out var snapshot) || string.IsNullOrWhiteSpace(anchorId))
        {
            return Array.Empty<VanguardTacticalCoordinationReadPoseSlot>();
        }

        var anchor = snapshot.TacticalAnchors.FirstOrDefault(item => string.Equals(item.AnchorId, anchorId, StringComparison.Ordinal));
        return anchor == null
            ? Array.Empty<VanguardTacticalCoordinationReadPoseSlot>()
            : anchor.PoseSlots.Where(item => item.Enabled).ToArray();
    }

    public static void Reset(string reason)
    {
        activeWorld = null;
        activeMapId = string.Empty;
        raidSessionId = string.Empty;
        localOwnerProfileId = string.Empty;
        localMapJson = string.Empty;
        localContentHash = string.Empty;
        localRevision = 0;
        lastOfferedRevision = 0;
        nextHeartbeatAtUtc = DateTimeOffset.MinValue;
        nextLocalLoadAttemptAtUtc = DateTimeOffset.MinValue;
        remoteSessionId = string.Empty;
        remoteOwnerProfileId = string.Empty;
        remoteRevision = 0;
        lock (Sync)
        {
            current = VanguardTacticalCoordinationReadSnapshot.Empty;
        }
        if (!string.IsNullOrWhiteSpace(reason))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag, $"TC_RUNTIME_TOPOLOGY_RESET reason={Safe(reason)}");
        }
    }

    private static void ResetRaid(GameWorld world, string mapId, string ownerProfileId)
    {
        Reset(string.Empty);
        activeWorld = world;
        activeMapId = mapId;
        localOwnerProfileId = ownerProfileId;
        raidSessionId = "tc-" + Guid.NewGuid().ToString("N");
    }

    private static VanguardTacticalCoordinationRuntimeTopologyDocument BuildTransportDocument(
        VanguardTacticalCoordinationAuthoringMapFile baseMap,
        VanguardTacticalCoordinationAuthoringMapFile userMap)
    {
        return new VanguardTacticalCoordinationRuntimeTopologyDocument
        {
            SchemaVersion = TransportSchemaVersion,
            MapId = userMap.MapId,
            MapRevision = string.IsNullOrWhiteSpace(userMap.MapRevision) ? baseMap.MapRevision : userMap.MapRevision,
            EftClientVersion = string.IsNullOrWhiteSpace(userMap.EftClientVersion) ? baseMap.EftClientVersion : userMap.EftClientVersion,
            BaseSourcePackVersion = baseMap.SourcePackVersion ?? string.Empty,
            Portals = VanguardTacticalCoordinationAuthoringStore.ResolvePortals(baseMap, userMap)
                .OrderBy(item => item.Origin, StringComparer.Ordinal)
                .ThenBy(item => item.Portal.PortalId, StringComparer.Ordinal)
                .ToList(),
            PassagePaths = VanguardTacticalCoordinationAuthoringStore.ResolvePassagePaths(baseMap, userMap)
                .OrderBy(item => item.Origin, StringComparer.Ordinal)
                .ThenBy(item => item.PassagePath.PassagePathId, StringComparer.Ordinal)
                .ToList(),
            TacticalAnchors = VanguardTacticalCoordinationAuthoringStore.ResolveTacticalAnchors(baseMap, userMap)
                .OrderBy(item => item.Origin, StringComparer.Ordinal)
                .ThenBy(item => item.TacticalAnchor.AnchorId, StringComparer.Ordinal)
                .ToList()
        };
    }

    private static void PublishReadSnapshot(
        VanguardTacticalCoordinationRuntimeTopologyDocument document,
        long revision,
        string contentHash,
        string sourceOwnerProfileId,
        string sourceKind)
    {
        ValidateTransportDocument(document, document.MapId);
        var portals = document.Portals.Select(item => new VanguardTacticalCoordinationReadPortal(
            item.Portal.PortalId,
            item.Portal.DisplayName,
            item.Portal.Position.ToVector3(),
            item.Portal.NavMeshProjectedPosition?.ToVector3(),
            item.Portal.Enabled,
            item.Portal.ValidationState,
            item.Portal.ValidationNotes,
            item.Origin,
            item.ReadOnly)).ToArray();

        var passagePaths = document.PassagePaths.Select(item => new VanguardTacticalCoordinationReadPassagePath(
            item.PassagePath.PassagePathId,
            item.PassagePath.DisplayName,
            item.PassagePath.ControlPoints.Select(point => point.Position.ToVector3()).ToArray(),
            item.PassagePath.ResolvedPolyline.Select(point => point.ToVector3()).ToArray(),
            item.PassagePath.ResolutionRevision,
            item.PassagePath.ResolutionSource,
            item.PassagePath.Enabled,
            item.PassagePath.ValidationState,
            item.PassagePath.ValidationNotes,
            item.Origin,
            item.ReadOnly)).ToArray();

        var anchors = document.TacticalAnchors.Select(item => new VanguardTacticalCoordinationReadAnchor(
            item.TacticalAnchor.AnchorId,
            item.TacticalAnchor.Name,
            item.TacticalAnchor.Center.ToVector3(),
            item.TacticalAnchor.Radius,
            item.TacticalAnchor.AuthoredPoseSlots.Select(slot => new VanguardTacticalCoordinationReadPoseSlot(
                slot.PoseSlotId,
                slot.Position.ToVector3(),
                slot.NavMeshProjectedPosition?.ToVector3(),
                slot.ViewDirection.ToVector3(),
                slot.PhysicalPoseSnapshot == null
                    ? null
                    : new VanguardTacticalCoordinationReadPhysicalPose(
                        slot.PhysicalPoseSnapshot.CaptureContractVersion,
                        slot.PhysicalPoseSnapshot.PoseKind,
                        slot.PhysicalPoseSnapshot.IsProne,
                        slot.PhysicalPoseSnapshot.PoseLevel,
                        slot.PhysicalPoseSnapshot.IsAimingDownSights,
                        slot.PhysicalPoseSnapshot.Tilt,
                        slot.PhysicalPoseSnapshot.IsLeftStance,
                        slot.PhysicalPoseSnapshot.IsInPatrol,
                        slot.PhysicalPoseSnapshot.CapturedAt,
                        slot.PhysicalPoseSnapshot.CapturedWithBuild),
                slot.Enabled,
                slot.ValidationState,
                slot.ValidationNotes)).ToArray(),
            item.TacticalAnchor.Enabled,
            item.TacticalAnchor.ValidationState,
            item.TacticalAnchor.ValidationNotes,
            item.Origin,
            item.ReadOnly)).ToArray();

        var snapshot = new VanguardTacticalCoordinationReadSnapshot(
            document.MapId,
            document.MapRevision,
            document.EftClientVersion,
            revision,
            contentHash,
            sourceOwnerProfileId,
            sourceKind,
            DateTimeOffset.UtcNow,
            portals,
            passagePaths,
            anchors);
        lock (Sync)
        {
            current = snapshot;
        }
    }

    private static void ValidateTransportDocument(VanguardTacticalCoordinationRuntimeTopologyDocument document, string expectedMapId)
    {
        if (document.SchemaVersion != TransportSchemaVersion)
        {
            throw new InvalidOperationException($"Unsupported TC runtime topology schema {document.SchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(document.MapId)
            || !string.Equals(Normalize(document.MapId), Normalize(expectedMapId), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("TC runtime topology map identity mismatch.");
        }

        document.Portals ??= new List<VanguardTacticalCoordinationResolvedPortal>();
        document.PassagePaths ??= new List<VanguardTacticalCoordinationResolvedPassagePath>();
        document.TacticalAnchors ??= new List<VanguardTacticalCoordinationResolvedAnchor>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.Portals)
        {
            if (item?.Portal == null || string.IsNullOrWhiteSpace(item.Portal.PortalId) || !identities.Add("P:" + item.Portal.PortalId))
            {
                throw new InvalidOperationException("TC runtime topology contains an invalid Portal identity.");
            }
        }
        foreach (var item in document.PassagePaths)
        {
            if (item?.PassagePath == null || string.IsNullOrWhiteSpace(item.PassagePath.PassagePathId) || !identities.Add("R:" + item.PassagePath.PassagePathId))
            {
                throw new InvalidOperationException("TC runtime topology contains an invalid PassagePath identity.");
            }
        }
        foreach (var item in document.TacticalAnchors)
        {
            if (item?.TacticalAnchor == null || string.IsNullOrWhiteSpace(item.TacticalAnchor.AnchorId) || !identities.Add("A:" + item.TacticalAnchor.AnchorId))
            {
                throw new InvalidOperationException("TC runtime topology contains an invalid TacticalAnchor identity.");
            }
        }
    }

    private static bool TryGetRaidContext(out GameWorld world, out string mapId, out string ownerProfileId)
    {
        world = null!;
        mapId = string.Empty;
        ownerProfileId = string.Empty;
        try
        {
            GameWorld? candidate = Singleton<GameWorld>.Instance;
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.LocationId)
                || (candidate.MainPlayer == null && (candidate.RegisteredPlayers?.Count ?? 0) == 0))
            {
                return false;
            }

            world = candidate;
            mapId = candidate.LocationId.Trim();
            ownerProfileId = candidate.MainPlayer?.ProfileId ?? string.Empty;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool CanPublishLocalTopology() =>
        !VanguardFikaCompat.IsActualHeadlessProcess && VanguardFikaCompat.CanWriteRaidScopedSettings;

    private static string ComputeSha256(string value)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return BitConverter.ToString(bytes).Replace("-", string.Empty);
    }

    private static string ShortHash(string value) => value.Length <= 12 ? value : value.Substring(0, 12);
    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    private static string Safe(string? value) => Normalize(value).Replace(";", ",", StringComparison.Ordinal);
}
#endif
