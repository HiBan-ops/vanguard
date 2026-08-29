#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using Vanguard.Client.Api;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Raid.Services;

// Responsibility: Relays the player’s live Tactical Authoring preview to the authoritative raid process and brings the current preview state back to readers.
// Flow: The local editor publishes a compact revision, background HTTP exchanges it with the SPT server, and the Headless/host consumes the latest accepted raid-scoped preview at a bounded cadence.
// Authority boundary: Only the player author writes authoring revisions; server/Headless transport them, and persisted authored maps remain separate from the live preview channel.
// Invariant: Network I/O never blocks the simulation thread, off-raid chatter stays disabled, and stale revisions cannot overwrite a newer preview.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

/// <summary>
/// Async relay between the local authoring editor, the SPT server, and the Fika headless authority.
/// Network I/O never runs on the Unity/headless simulation thread.
/// </summary>
internal static class VanguardTacticalAuthoringLiveSyncService
{
    public const string StatusTag = "VANGUARD_TACTICAL_AUTHORING_LIVE_SYNC_STATUS";
    private static readonly VanguardApiClient ApiClient = new();
    private static readonly object Sync = new();
    private static readonly TimeSpan InRaidExchangeInterval = TimeSpan.FromSeconds(0.55d);
    private static readonly TimeSpan LocalStateProbeInterval = TimeSpan.FromSeconds(0.55d);
    private static readonly TimeSpan FailureInterval = TimeSpan.FromSeconds(1.5d);

    private static Task? activeIoTask;
    private static PendingExchange? pending;
    private static DateTimeOffset nextExchangeAtUtc = DateTimeOffset.MinValue;
    private static VanguardTacticalAuthoringLiveAuthorSnapshotDto? lastAuthorSnapshot;
    private static bool closePending;
    private static bool bootLogged;
    private static bool firstHeadlessAuthorReceiptLogged;
    private static bool firstAuthorResultReceiptLogged;
    private static bool raidTransportActive;
    private static long transportEpoch;

    public static void Tick()
    {
        DrainOnMainThread();
        bool raidActive = IsRaidWorldActive();
        if (!raidActive)
        {
            if (raidTransportActive)
            {
                raidTransportActive = false;
                Reset("raid_world_inactive");
            }
            return;
        }

        if (!raidTransportActive)
        {
            raidTransportActive = true;
            nextExchangeAtUtc = DateTimeOffset.MinValue;
            bootLogged = false;
        }

        if (VanguardHeadlessPostRaidQuiescenceService.IsActive)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < nextExchangeAtUtc || !TryReserve())
        {
            return;
        }

        VanguardTacticalAuthoringLiveExchangeRequestDto? request = null;
        VanguardTacticalAuthoringLiveAuthorSnapshotDto author;
        if (VanguardFikaCompat.IsActualHeadlessProcess)
        {
            request = new VanguardTacticalAuthoringLiveExchangeRequestDto
            {
                Role = "headless",
                ClientBuild = VanguardBuildVersion.Value,
                ClientLabel = VanguardBuildVersion.BuildLabel,
                KnownOwnerProfileIds = VanguardRaidOperatorRuntimeRegistry.GetKnownOwnerProfileIds().ToList(),
                HeadlessResults = VanguardTacticalAuthoringHeadlessPreviewService.BuildRelayResults()
            };
        }
        else if (VanguardTacticalAuthoringService.TryBuildLiveAuthorSnapshot(out author))
        {
            lastAuthorSnapshot = author;
            closePending = false;
            VanguardTacticalAuthoringLivePreviewClientState.Expect(author.LiveSessionId, author.MapId, author.Revision);
            request = new VanguardTacticalAuthoringLiveExchangeRequestDto
            {
                Role = "author",
                ClientBuild = VanguardBuildVersion.Value,
                ClientLabel = VanguardBuildVersion.BuildLabel,
                Author = author
            };
        }
        else if (VanguardTacticalAuthoredZoneOccupancyService.TryBuildLiveAuthorSnapshot(out author))
        {
            lastAuthorSnapshot = author;
            closePending = false;
            VanguardTacticalAuthoringLivePreviewClientState.Expect(author.LiveSessionId, author.MapId, author.Revision);
            request = new VanguardTacticalAuthoringLiveExchangeRequestDto
            {
                Role = "author",
                ClientBuild = VanguardBuildVersion.Value,
                ClientLabel = VanguardBuildVersion.BuildLabel,
                Author = author
            };
        }
        else if (lastAuthorSnapshot != null && !closePending)
        {
            closePending = true;
        }

        if (request == null && closePending && lastAuthorSnapshot != null)
        {
            request = new VanguardTacticalAuthoringLiveExchangeRequestDto
            {
                Role = "author",
                ClientBuild = VanguardBuildVersion.Value,
                ClientLabel = VanguardBuildVersion.BuildLabel,
                Author = new VanguardTacticalAuthoringLiveAuthorSnapshotDto
                {
                    OwnerProfileId = lastAuthorSnapshot.OwnerProfileId,
                    LiveSessionId = lastAuthorSnapshot.LiveSessionId,
                    MapId = lastAuthorSnapshot.MapId,
                    Active = false,
                    Revision = lastAuthorSnapshot.Revision + 1,
                    SelectedZoneId = lastAuthorSnapshot.SelectedZoneId,
                    MapJson = string.Empty,
                    UpdatedAtUtc = now,
                    ClientBuild = VanguardBuildVersion.BuildLabel
                }
            };
        }

        if (request == null)
        {
            ReleaseReservation();
            nextExchangeAtUtc = now + LocalStateProbeInterval;
            return;
        }

        nextExchangeAtUtc = now + InRaidExchangeInterval;
        StartIo(request);
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TACTICAL_AUTHORING_LIVE_SYNC active=true; role={request.Role}; raidOnlyTransport=true; offRaidHttp=false; actualHeadlessProcess={VanguardFikaCompat.IsActualHeadlessProcess}; raidHostedByHeadless={VanguardFikaCompat.IsRaidHostedByHeadless}; legacyHeadlessCompat={VanguardFikaCompat.IsHeadless}; inRaidInterval={InRaidExchangeInterval.TotalSeconds:0.00}s; asyncIo=true; persistedRuntimeConsumption=false; build={VanguardBuildVersion.BuildLabel}");
        }
    }

    public static void Reset(string reason)
    {
        transportEpoch++;
        raidTransportActive = false;
        bootLogged = false;
        lastAuthorSnapshot = null;
        closePending = false;
        nextExchangeAtUtc = DateTimeOffset.MinValue;
        VanguardTacticalAuthoringLivePreviewClientState.Clear();
        VanguardTacticalAuthoredZoneOccupancyService.Reset(reason);
        firstHeadlessAuthorReceiptLogged = false;
        firstAuthorResultReceiptLogged = false;
        VanguardTacticalAuthoringHeadlessPreviewService.Reset(reason);
    }

    private static bool TryReserve()
    {
        lock (Sync)
        {
            if ((activeIoTask != null && !activeIoTask.IsCompleted) || pending != null)
            {
                return false;
            }
            activeIoTask = Task.CompletedTask;
            return true;
        }
    }

    private static void ReleaseReservation()
    {
        lock (Sync)
        {
            activeIoTask = null;
        }
    }

    private static void StartIo(VanguardTacticalAuthoringLiveExchangeRequestDto request)
    {
        var role = request.Role;
        var epoch = transportEpoch;
        var task = Task.Run(() =>
        {
            PendingExchange result;
            try
            {
                result = new PendingExchange(epoch, role, ApiClient.ExchangeTacticalAuthoringLive(request), null);
            }
            catch (Exception exception)
            {
                result = new PendingExchange(epoch, role, null, exception);
            }

            lock (Sync)
            {
                pending = result;
            }
        });
        lock (Sync)
        {
            activeIoTask = task;
        }
    }

    private static void DrainOnMainThread()
    {
        PendingExchange? result;
        lock (Sync)
        {
            result = pending;
            if (result == null)
            {
                return;
            }
            pending = null;
            activeIoTask = null;
        }

        if (result.Epoch != transportEpoch)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (result.Exception != null || result.Response == null || !result.Response.Success)
        {
            nextExchangeAtUtc = now + FailureInterval;
            if (string.Equals(result.Role, "author", StringComparison.OrdinalIgnoreCase) && closePending)
            {
                // Keep retrying the explicit close until it is acknowledged; relay TTL is still the fallback.
            }
            VanguardClientDiagnosticsLog.Warning(StatusTag,
                $"TACTICAL_AUTHORING_LIVE_SYNC_FAILED role={result.Role}; reason={result.Response?.Reason ?? result.Exception?.GetType().Name ?? "empty"}; retry={FailureInterval.TotalSeconds:0.0}s; mainThreadIo=false");
            return;
        }

        if (string.Equals(result.Role, "headless", StringComparison.OrdinalIgnoreCase))
        {
            if (!firstHeadlessAuthorReceiptLogged && result.Response.Authors.Count > 0)
            {
                firstHeadlessAuthorReceiptLogged = true;
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"TACTICAL_AUTHORING_LIVE_SYNC_HEADLESS_AUTHOR_RECEIVED authors={result.Response.Authors.Count}; owners={string.Join(",", result.Response.Authors.Select(item => item.OwnerProfileId))}; mainThreadApply=true");
            }
            VanguardTacticalAuthoringHeadlessPreviewService.ApplyAuthorSnapshots(result.Response.Authors, now);
            nextExchangeAtUtc = now + InRaidExchangeInterval;
            return;
        }

        if (closePending)
        {
            closePending = false;
            lastAuthorSnapshot = null;
            VanguardTacticalAuthoringLivePreviewClientState.Clear();
            return;
        }

        var expected = lastAuthorSnapshot;
        if (expected == null)
        {
            return;
        }

        var latest = result.Response.HeadlessResults
            .Where(item => string.Equals(item.LiveSessionId, expected.LiveSessionId, StringComparison.Ordinal)
                && string.Equals(item.MapId, expected.MapId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.UpdatedAtUtc)
            .FirstOrDefault();
        if (!firstAuthorResultReceiptLogged && latest != null)
        {
            firstAuthorResultReceiptLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TACTICAL_AUTHORING_LIVE_SYNC_AUTHOR_RESULT_RECEIVED revision={latest.AuthorRevision}; owner={latest.OwnerProfileId}; headlessBuild={latest.HeadlessBuild}; mainThreadApply=true");
        }
        VanguardTacticalAuthoringLivePreviewClientState.Apply(latest, expected.LiveSessionId, expected.MapId);
    }

    private static bool IsRaidWorldActive()
    {
        try
        {
            GameWorld? gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null || string.IsNullOrWhiteSpace(gameWorld.LocationId))
            {
                return false;
            }

            return gameWorld.MainPlayer != null || (gameWorld.RegisteredPlayers?.Count ?? 0) > 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed class PendingExchange
    {
        public PendingExchange(long epoch, string role, VanguardTacticalAuthoringLiveExchangeResponseDto? response, Exception? exception)
        {
            Epoch = epoch;
            Role = role;
            Response = response;
            Exception = exception;
        }
        public long Epoch { get; }
        public string Role { get; }
        public VanguardTacticalAuthoringLiveExchangeResponseDto? Response { get; }
        public Exception? Exception { get; }
    }
}
#endif
