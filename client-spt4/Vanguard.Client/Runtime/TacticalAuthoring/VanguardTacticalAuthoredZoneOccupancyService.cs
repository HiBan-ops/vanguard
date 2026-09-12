#if SPT_CLIENT
using System;
using System.IO;
using System.Linq;
using Comfort.Common;
using EFT;
using Newtonsoft.Json;
using UnityEngine;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;

// Responsibility: Determines which saved authored zone currently contains the player and publishes that owner-scoped occupancy to the Headless runtime.
// Flow: Local position is compared with exact/nested zone bounds, sticky/hysteresis rules prevent boundary churn, and changes are serialized through the Tactical Authoring transport for Operator assignment use.
// Authority boundary: The player client observes/chooses its authored occupancy; the Headless consumes the published assignment, while persisted authoring data remains owned by the Tactical Authoring store.
// Invariant: Only one best exact zone may be active per owner, more-specific nested zones may preempt deterministically, and stale occupancy must clear when position/map/raid authority disappears.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

/// <summary>
/// Owner-local publisher for automatic authored-zone occupancy when the editor overlay is closed.
/// Saved authoring data remains schema-1/non-runtime on disk; this service only creates a transient
/// owner-scoped headless preview session while the local player is physically inside an authored zone.
/// </summary>
internal static class VanguardTacticalAuthoredZoneOccupancyService
{
    public const string StatusTag = "VANGUARD_AUTOMATIC_AUTHORED_ZONE_OCCUPANCY_STATUS";

    private static readonly TimeSpan MapProbeInterval = TimeSpan.FromSeconds(1.50d);
    private const float HorizontalExitHysteresisMeters = 2.0f;
    private const float VerticalExitHysteresisMeters = 0.75f;
    private const float SpecificityRadiusEpsilonMeters = 0.25f;
    private const float SpecificityVerticalSpanEpsilonMeters = 0.25f;

    private static GameWorld? activeWorld;
    private static VanguardTacticalAuthoringMapFile? cachedMap;
    private static string cachedMapJson = string.Empty;
    private static string activeMapId = string.Empty;
    private static string ownerProfileId = string.Empty;
    private static string selectedZoneId = string.Empty;
    private static string liveSessionId = string.Empty;
    private static long revision;
    private static DateTime cachedWriteTimeUtc = DateTime.MinValue;
    private static long cachedFileLength = -1L;
    private static DateTimeOffset nextMapProbeAtUtc = DateTimeOffset.MinValue;
    private static string lastTransitionSignature = string.Empty;
    private static string lastMapLoadErrorSignature = string.Empty;

    public static bool HasActiveZone => !string.IsNullOrWhiteSpace(selectedZoneId)
        && !string.IsNullOrWhiteSpace(liveSessionId)
        && cachedMap != null;

    public static string SelectedZoneId => selectedZoneId;

    public static void Tick()
    {
        if (VanguardFikaCompat.IsActualHeadlessProcess
            || !VanguardTacticalAuthoringOptions.AutomaticAuthoredZoneOccupancyEnabled
            || VanguardTacticalAuthoringService.IsActive
            || VanguardTacticalAuthoringService.HasUnsavedChanges)
        {
            var reason = VanguardTacticalAuthoringService.IsActive
                ? "editor_active"
                : VanguardTacticalAuthoringService.HasUnsavedChanges
                    ? "unsaved_authoring_changes"
                    : "automatic_occupancy_disabled";
            ResetTransientSelection(reason);
            return;
        }

        if (!TryGetPlayerRaidContext(out var world, out var player, out var mapId))
        {
            ResetAll("raid_context_unavailable");
            return;
        }

        var owner = player.ProfileId?.Trim() ?? string.Empty;
        if (owner.Length == 0)
        {
            ResetAll("owner_profile_missing");
            return;
        }

        if (!ReferenceEquals(activeWorld, world)
            || !string.Equals(activeMapId, mapId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ownerProfileId, owner, StringComparison.Ordinal))
        {
            ResetAll("raid_owner_or_map_changed");
            activeWorld = world;
            activeMapId = mapId;
            ownerProfileId = owner;
        }

        var now = DateTimeOffset.UtcNow;
        ProbeSavedMap(now);
        if (cachedMap == null)
        {
            ResetTransientSelection("saved_map_unavailable");
            return;
        }

        var nextZone = SelectAutomaticZone(cachedMap, player.Position, selectedZoneId);
        if (nextZone == null)
        {
            if (selectedZoneId.Length > 0)
            {
                LogTransition("exit", selectedZoneId, player.Position, "outside_all_authored_zones_after_hysteresis");
            }
            ResetTransientSelection("outside_authored_zones");
            return;
        }

        var zoneChanged = !string.Equals(selectedZoneId, nextZone.ZoneId, StringComparison.Ordinal);
        if (liveSessionId.Length == 0)
        {
            liveSessionId = NewId("auto");
            revision = 1L;
            zoneChanged = true;
        }
        else if (zoneChanged)
        {
            revision = NextRevision(revision);
        }

        selectedZoneId = nextZone.ZoneId;
        if (zoneChanged)
        {
            LogTransition("enter_or_handoff", selectedZoneId, player.Position,
                $"radius={nextZone.ZoneRadius:0.0};floor={nextZone.FloorId};mode=sticky_exact_specificity_then_exit_hysteresis");
        }
    }

    public static bool TryBuildLiveAuthorSnapshot(out VanguardTacticalAuthoringLiveAuthorSnapshotDto snapshot)
    {
        snapshot = new VanguardTacticalAuthoringLiveAuthorSnapshotDto();
        if (!HasActiveZone || activeWorld?.MainPlayer is not Player player
            || string.IsNullOrWhiteSpace(ownerProfileId) || string.IsNullOrWhiteSpace(activeMapId)
            || cachedMap == null || string.IsNullOrWhiteSpace(cachedMapJson))
        {
            return false;
        }

        cachedMap.RuntimeConsumptionEnabled = false;
        snapshot = new VanguardTacticalAuthoringLiveAuthorSnapshotDto
        {
            OwnerProfileId = ownerProfileId,
            LiveSessionId = liveSessionId,
            MapId = activeMapId,
            Active = true,
            Revision = Math.Max(1L, revision),
            SelectedZoneId = selectedZoneId,
            MapJson = cachedMapJson,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ClientBuild = VanguardBuildVersion.BuildLabel
        };
        return string.Equals(player.ProfileId, ownerProfileId, StringComparison.Ordinal);
    }

    public static void Reset(string reason)
    {
        ResetAll(reason);
    }

    private static void ProbeSavedMap(DateTimeOffset now)
    {
        if (now < nextMapProbeAtUtc && cachedMap != null)
        {
            return;
        }
        nextMapProbeAtUtc = now + MapProbeInterval;

        var path = VanguardTacticalAuthoringStore.GetMapPath(activeMapId);
        if (!File.Exists(path))
        {
            cachedMap = null;
            cachedMapJson = string.Empty;
            cachedWriteTimeUtc = DateTime.MinValue;
            cachedFileLength = -1L;
            return;
        }

        var info = new FileInfo(path);
        if (cachedMap != null
            && info.LastWriteTimeUtc == cachedWriteTimeUtc
            && info.Length == cachedFileLength)
        {
            return;
        }

        try
        {
            var loaded = VanguardTacticalAuthoringStore.Reload(activeMapId);
            loaded.RuntimeConsumptionEnabled = false;
            cachedMap = loaded;
            cachedMapJson = JsonConvert.SerializeObject(loaded);
            cachedWriteTimeUtc = info.LastWriteTimeUtc;
            cachedFileLength = info.Length;
            lastMapLoadErrorSignature = string.Empty;
            if (liveSessionId.Length > 0)
            {
                revision = NextRevision(revision);
            }
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"AUTOMATIC_AUTHORED_ZONE_MAP_LOADED owner={ownerProfileId}; map={activeMapId}; zones={loaded.Zones.Count}; persistedRuntimeConsumption=false; revision={Math.Max(1L, revision)}; build={VanguardBuildVersion.BuildLabel}");
        }
        catch (Exception exception)
        {
            cachedMap = null;
            cachedMapJson = string.Empty;
            var signature = exception.GetType().Name + ":" + exception.Message;
            if (!string.Equals(lastMapLoadErrorSignature, signature, StringComparison.Ordinal))
            {
                lastMapLoadErrorSignature = signature;
                VanguardClientDiagnosticsLog.Warning(StatusTag,
                    $"AUTOMATIC_AUTHORED_ZONE_MAP_REJECTED owner={ownerProfileId}; map={activeMapId}; error={exception.GetType().Name}; failClosed=true; normalVanguardBehaviorPreserved=true");
            }
        }
    }

    private static VanguardTacticalAuthoringZone? SelectAutomaticZone(
        VanguardTacticalAuthoringMapFile map,
        Vector3 playerPosition,
        string currentZoneId)
    {
        var exactZones = map.Zones
            .Where(zone => IsInside(zone, playerPosition, horizontalExtra: 0f, verticalExtra: 0f))
            .ToArray();

        var current = !string.IsNullOrWhiteSpace(currentZoneId)
            ? map.Zones.FirstOrDefault(zone => string.Equals(zone.ZoneId, currentZoneId, StringComparison.Ordinal))
            : null;

        // Sticky exact arbitration is the anti-churn rule. Once an owner has entered a zone,
        // crossing the geometric center line of an equal-level overlapping zone must not cause
        // handoff/reassignment churn. A different exact zone may preempt only when it is
        // structurally more specific (smaller radius; materially equal radii then smaller Y span).
        if (current != null && exactZones.Any(zone => ReferenceEquals(zone, current)))
        {
            var preempting = exactZones
                .Where(zone => !ReferenceEquals(zone, current) && IsStrictlyMoreSpecific(zone, current))
                .OrderBy(zone => zone.ZoneRadius)
                .ThenBy(zone => VerticalSpan(zone))
                .ThenBy(zone => zone.ZoneId, StringComparer.Ordinal)
                .FirstOrDefault();
            return preempting ?? current;
        }

        // Initial acquisition remains deterministic. Center distance is only a late tie-break here;
        // it is deliberately never used to churn between zones while the current one still exactly
        // contains the owner.
        var exact = exactZones
            .OrderBy(zone => zone.ZoneRadius)
            .ThenBy(zone => VerticalSpan(zone))
            .ThenBy(zone => HorizontalDistance(zone.ZoneAnchor.ToVector3(), playerPosition))
            .ThenBy(zone => zone.ZoneId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (exact != null)
        {
            return exact;
        }

        // Exit hysteresis applies only after exact containment has been lost. It never participates
        // in arbitration between overlapping exact zones.
        if (current != null && IsInside(current, playerPosition, HorizontalExitHysteresisMeters, VerticalExitHysteresisMeters))
        {
            return current;
        }

        return null;
    }

    private static bool IsStrictlyMoreSpecific(VanguardTacticalAuthoringZone candidate, VanguardTacticalAuthoringZone current)
    {
        if (current.ZoneRadius - candidate.ZoneRadius > SpecificityRadiusEpsilonMeters)
        {
            return true;
        }

        return Mathf.Abs(candidate.ZoneRadius - current.ZoneRadius) <= SpecificityRadiusEpsilonMeters
            && VerticalSpan(current) - VerticalSpan(candidate) > SpecificityVerticalSpanEpsilonMeters;
    }

    private static float VerticalSpan(VanguardTacticalAuthoringZone zone) => Mathf.Max(0f, zone.MaxY - zone.MinY);

    private static bool IsInside(VanguardTacticalAuthoringZone zone, Vector3 playerPosition, float horizontalExtra, float verticalExtra)
    {
        var anchor = zone.ZoneAnchor.ToVector3();
        var radius = Mathf.Max(0.5f, zone.ZoneRadius + horizontalExtra);
        var horizontal = HorizontalDistance(anchor, playerPosition);
        return horizontal <= radius
            && playerPosition.y >= zone.MinY - verticalExtra
            && playerPosition.y <= zone.MaxY + verticalExtra;
    }

    private static void ResetTransientSelection(string reason)
    {
        if (selectedZoneId.Length == 0 && liveSessionId.Length == 0)
        {
            return;
        }

        selectedZoneId = string.Empty;
        liveSessionId = string.Empty;
        revision = 0L;
        lastTransitionSignature = string.Empty;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"AUTOMATIC_AUTHORED_ZONE_RELEASE owner={ownerProfileId}; map={activeMapId}; reason={reason}; normalVanguardBehaviorResumes=true");
    }

    private static void ResetAll(string reason)
    {
        ResetTransientSelection(reason);
        activeWorld = null;
        cachedMap = null;
        cachedMapJson = string.Empty;
        activeMapId = string.Empty;
        ownerProfileId = string.Empty;
        cachedWriteTimeUtc = DateTime.MinValue;
        cachedFileLength = -1L;
        nextMapProbeAtUtc = DateTimeOffset.MinValue;
        lastMapLoadErrorSignature = string.Empty;
    }

    private static bool TryGetPlayerRaidContext(out GameWorld world, out Player player, out string mapId)
    {
        world = null!;
        player = null!;
        mapId = string.Empty;
        try
        {
            world = Singleton<GameWorld>.Instance;
        }
        catch
        {
            return false;
        }

        if (world == null || world.MainPlayer is not Player mainPlayer || mainPlayer.HealthController?.IsAlive != true)
        {
            return false;
        }

        var locationId = world.LocationId?.Trim() ?? string.Empty;
        if (locationId.Length == 0)
        {
            return false;
        }

        player = mainPlayer;
        mapId = locationId;
        return true;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static long NextRevision(long value) => value == long.MaxValue ? 1L : Math.Max(1L, value + 1L);

    private static string NewId(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 12);

    private static void LogTransition(string kind, string zoneId, Vector3 playerPosition, string reason)
    {
        var signature = $"{kind}|{zoneId}|{reason}";
        if (string.Equals(lastTransitionSignature, signature, StringComparison.Ordinal))
        {
            return;
        }
        lastTransitionSignature = signature;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"AUTOMATIC_AUTHORED_ZONE_TRANSITION kind={kind}; owner={ownerProfileId}; map={activeMapId}; zone={zoneId}; player=({playerPosition.x:0.0},{playerPosition.y:0.0},{playerPosition.z:0.0}); reason={reason}; ownerScoped=true; persistedRuntimeConsumption=false");
    }
}
#endif
