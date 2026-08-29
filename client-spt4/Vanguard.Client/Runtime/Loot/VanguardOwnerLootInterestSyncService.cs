#if SPT_CLIENT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EFT;
using Vanguard.Client.Api;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Raid.Services;
using Vanguard.Client.Runtime.Audit;

// Responsibility: Synchronizes each player owner’s current loot interests so Operators can value useful items without reading another player’s inventory preferences.
// Flow: The owning client reads its local wishlist/interest state, sends a compact revision to the server, and Headless/runtime readers pull only the owner IDs already present in the raid registry.
// Authority boundary: The player client authors its own interests; Headless and other consumers may read them but never promote themselves to author.
// Invariant: Sync is content/revision based, owner-scoped and fail-open; missing interest data reduces loot preference detail but must not block the raid.
namespace Vanguard.Client.Runtime.Loot;

internal sealed class VanguardOwnerLootInterestSnapshot
{
    public static VanguardOwnerLootInterestSnapshot Empty(string ownerProfileId, string source) => new()
    {
        OwnerProfileId = ownerProfileId ?? string.Empty,
        Source = source,
        Known = false
    };

    public string OwnerProfileId { get; init; } = string.Empty;
    public long Revision { get; init; }
    public string ContentHash { get; init; } = "none";
    public string Source { get; init; } = "none";
    public bool Known { get; init; }
    public IReadOnlyDictionary<string, string> GroupByTemplateId { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool TryGetGroup(string? templateId, out string group)
    {
        if (!string.IsNullOrWhiteSpace(templateId) && GroupByTemplateId.TryGetValue(templateId.Trim(), out var found))
        {
            group = found;
            return true;
        }
        group = "none";
        return false;
    }
}

/// <summary>
/// The persistence path owner-scoped Wishlist bridge. Player clients read EFT's native Profile.WishlistManager and publish
/// only when its stable content hash changes. Runtime authorities pull by OwnerProfileId. This transport is
/// intentionally separate from F12: Wishlist is player state, not configuration. Unknown data yields no
/// wishlist bonus and never disables the existing loot pipeline.
/// </summary>
internal static class VanguardOwnerLootInterestSyncService
{
    private static readonly VanguardApiClient Api = new();
    private static readonly object Sync = new();
    private static readonly Dictionary<string, VanguardOwnerLootInterestSnapshot> ByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PlayerObserveInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan RuntimePullBootstrapInterval = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan RuntimePullSteadyInterval = TimeSpan.FromSeconds(20.0);
    private static readonly TimeSpan RuntimePullFailureInterval = TimeSpan.FromSeconds(5.0);
    private static readonly TimeSpan RuntimePullSweepInterval = TimeSpan.FromSeconds(0.75);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5.0);

    private static DateTimeOffset nextTickAtUtc = DateTimeOffset.MinValue;
    private static Task<SyncResult>? ioTask;
    private static SyncResult? pendingResult;
    private static string lastPlayerHash = "none";
    private static long playerRevision;
    private static int pullOwnerIndex;
    private static readonly Dictionary<string, DateTimeOffset> NextRuntimePullAtByOwner = new(StringComparer.OrdinalIgnoreCase);

    public static void ResetForRaidLifecycle(string source)
    {
        lock (Sync)
        {
            ByOwner.Clear();
        }
        nextTickAtUtc = DateTimeOffset.MinValue;
        ioTask = null;
        pendingResult = null;
        lastPlayerHash = "none";
        playerRevision = 0;
        pullOwnerIndex = 0;
        lock (Sync) NextRuntimePullAtByOwner.Clear();
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.UnifiedOpportunisticLootReadModelStatusTag,
            $"VANGUARD_LOOT_INTEREST_RESET source={Safe(source)}; separateFromF12=true; failClosedBonusOnly=true");
    }

    public static void Tick()
    {
        DrainCompleted();
        if (VanguardHeadlessPostRaidQuiescenceService.IsActive)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (ioTask != null || now < nextTickAtUtc) return;

        if (!VanguardFikaCompat.IsActualHeadlessProcess)
        {
            if (!TryCapturePlayerWishlist(out VanguardOwnerLootInterestSetRequestDto? request, out string hash))
            {
                nextTickAtUtc = now + PlayerObserveInterval;
                return;
            }

            if (string.Equals(hash, lastPlayerHash, StringComparison.Ordinal))
            {
                nextTickAtUtc = now + PlayerObserveInterval;
                return;
            }

            lastPlayerHash = hash;
            playerRevision++;
            VanguardOwnerLootInterestSetRequestDto captured = request!;
            captured.Revision = playerRevision;
            captured.ContentHash = hash;
            ioTask = Task.Run(() => DoSet(captured));
            nextTickAtUtc = now + PlayerObserveInterval;
            return;
        }

        IReadOnlyList<string> owners = VanguardRaidOperatorRuntimeRegistry.GetKnownOwnerProfileIds();
        if (owners.Count == 0)
        {
            nextTickAtUtc = now + RuntimePullSweepInterval;
            return;
        }

        string? owner = ResolveNextDueRuntimeOwner(owners, now);
        if (string.IsNullOrWhiteSpace(owner))
        {
            nextTickAtUtc = now + RuntimePullSweepInterval;
            return;
        }

        ioTask = Task.Run(() => DoGet(owner));
        nextTickAtUtc = now + RuntimePullSweepInterval;
    }

    public static VanguardOwnerLootInterestSnapshot Resolve(string? ownerProfileId)
    {
        string owner = Normalize(ownerProfileId);
        lock (Sync)
        {
            return owner.Length > 0 && ByOwner.TryGetValue(owner, out var snapshot)
                ? snapshot
                : VanguardOwnerLootInterestSnapshot.Empty(owner, "wishlist_unknown_no_bonus");
        }
    }

    private static bool TryCapturePlayerWishlist(out VanguardOwnerLootInterestSetRequestDto? request, out string hash)
    {
        request = null;
        hash = "none";
        Player? player = GamePlayerOwner.MyPlayer;
        string owner = Normalize(player?.ProfileId);
        object? manager = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player?.Profile, "WishlistManager");
        if (owner.Length == 0 || manager == null) return false;

        try
        {
            object? raw = VanguardOperatorRuntimeAuditReflection.InvokeNoArg(manager, "GetWishlist");
            if (raw is not IEnumerable enumerable) return false;

            var entries = new List<VanguardOwnerLootInterestEntryDto>();
            foreach (object? pair in enumerable)
            {
                object? key = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(pair, "Key");
                object? value = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(pair, "Value");
                string templateId = Normalize(key?.ToString());
                if (templateId.Length == 0) continue;
                entries.Add(new VanguardOwnerLootInterestEntryDto
                {
                    TemplateId = templateId,
                    Group = NormalizeGroup(value?.ToString())
                });
            }

            entries = entries.OrderBy(entry => entry.TemplateId, StringComparer.OrdinalIgnoreCase).ToList();
            hash = ComputeHash(entries);
            request = new VanguardOwnerLootInterestSetRequestDto
            {
                OwnerProfileId = owner,
                Source = "eft_native_wishlist",
                ClientBuild = VanguardBuildVersion.Value,
                Entries = entries
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static SyncResult DoSet(VanguardOwnerLootInterestSetRequestDto request)
    {
        try { return new SyncResult(Api.SetOwnerLootInterest(request), true); }
        catch (Exception exception) { return SyncResult.Failure(request.OwnerProfileId, "set_exception_" + exception.GetType().Name); }
    }

    private static SyncResult DoGet(string owner)
    {
        try { return new SyncResult(Api.GetOwnerLootInterest(owner), false); }
        catch (Exception exception) { return SyncResult.Failure(owner, "get_exception_" + exception.GetType().Name); }
    }

    private static void DrainCompleted()
    {
        Task<SyncResult>? task = ioTask;
        if (task == null || !task.IsCompleted) return;
        ioTask = null;
        try { pendingResult = task.GetAwaiter().GetResult(); }
        catch (Exception exception) { pendingResult = SyncResult.Failure("none", "task_exception_" + exception.GetType().Name); }

        SyncResult result = pendingResult!;
        pendingResult = null;
        VanguardOwnerLootInterestResponseDto response = result.Response;
        if (!response.Success)
        {
            // A failed player SET must remain retryable. lastPlayerHash is optimistic while the request is
            // in flight, so clear it on failure; otherwise the next observation would incorrectly look
            // already published and the updated Wishlist could remain local forever.
            if (result.WasSet)
            {
                lastPlayerHash = "none";
                nextTickAtUtc = DateTimeOffset.UtcNow + RetryInterval;
            }
            else
            {
                ScheduleRuntimePull(response.OwnerProfileId, DateTimeOffset.UtcNow + RuntimePullFailureInterval);
                nextTickAtUtc = DateTimeOffset.UtcNow + RuntimePullSweepInterval;
            }
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.UnifiedOpportunisticLootReadModelStatusTag,
                $"VANGUARD_LOOT_INTEREST_SYNC_FAILED owner={Safe(response.OwnerProfileId)}; direction={(result.WasSet ? "player_push" : "runtime_pull")}; reason={Safe(response.Reason)}");
            return;
        }

        string owner = Normalize(response.OwnerProfileId);
        if (owner.Length == 0) return;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (VanguardOwnerLootInterestEntryDto entry in response.Entries ?? new List<VanguardOwnerLootInterestEntryDto>())
        {
            if (!string.IsNullOrWhiteSpace(entry.TemplateId)) map[entry.TemplateId.Trim()] = NormalizeGroup(entry.Group);
        }
        var snapshot = new VanguardOwnerLootInterestSnapshot
        {
            OwnerProfileId = owner,
            Revision = response.Revision,
            ContentHash = response.ContentHash ?? "none",
            Source = response.Source ?? "none",
            Known = !string.Equals(response.Source, "server_no_interest_snapshot", StringComparison.OrdinalIgnoreCase),
            GroupByTemplateId = map
        };
        lock (Sync) ByOwner[owner] = snapshot;
        TimeSpan nextPull = snapshot.Known ? RuntimePullSteadyInterval : RuntimePullBootstrapInterval;
        if (!result.WasSet)
        {
            ScheduleRuntimePull(owner, DateTimeOffset.UtcNow + nextPull);
        }
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.UnifiedOpportunisticLootReadModelStatusTag,
            $"VANGUARD_LOOT_INTEREST_APPLIED owner={Safe(owner)}; direction={(result.WasSet ? "player_push" : "runtime_pull")}; revision={snapshot.Revision}; entries={map.Count}; hash={Safe(snapshot.ContentHash)}; source={Safe(snapshot.Source)}; known={snapshot.Known}; nextRuntimePullSeconds={(result.WasSet ? 0d : nextPull.TotalSeconds):0.0}; pullMode={(result.WasSet ? "content_hash_push" : snapshot.Known ? "steady" : "bootstrap")}");
    }

    private static string? ResolveNextDueRuntimeOwner(IReadOnlyList<string> owners, DateTimeOffset now)
    {
        if (owners.Count == 0) return null;
        var active = new HashSet<string>(owners.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()), StringComparer.OrdinalIgnoreCase);
        lock (Sync)
        {
            foreach (string stale in NextRuntimePullAtByOwner.Keys.Where(key => !active.Contains(key)).ToArray()) NextRuntimePullAtByOwner.Remove(stale);
        }

        if (pullOwnerIndex >= owners.Count) pullOwnerIndex = 0;
        for (int offset = 0; offset < owners.Count; offset++)
        {
            int index = (pullOwnerIndex + offset) % owners.Count;
            string candidate = Normalize(owners[index]);
            if (candidate.Length == 0) continue;
            DateTimeOffset due;
            lock (Sync)
            {
                if (!NextRuntimePullAtByOwner.TryGetValue(candidate, out due)) due = DateTimeOffset.MinValue;
            }
            if (now < due) continue;
            pullOwnerIndex = (index + 1) % owners.Count;
            // Reserve the slot before the asynchronous GET starts so a fast Tick cannot enqueue the same Owner twice.
            ScheduleRuntimePull(candidate, now + RuntimePullBootstrapInterval);
            return candidate;
        }
        return null;
    }

    private static void ScheduleRuntimePull(string? ownerProfileId, DateTimeOffset atUtc)
    {
        string owner = Normalize(ownerProfileId);
        if (owner.Length == 0) return;
        lock (Sync) NextRuntimePullAtByOwner[owner] = atUtc;
    }

    private static string ComputeHash(IReadOnlyList<VanguardOwnerLootInterestEntryDto> entries)
    {
        using SHA256 sha = SHA256.Create();
        string canonical = string.Join("\n", entries.Select(entry => entry.TemplateId + "|" + NormalizeGroup(entry.Group)));
        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string NormalizeGroup(string? group)
    {
        string value = Normalize(group);
        return value is "Quests" or "Hideout" or "Trading" or "Equipment" or "Other" ? value : "Other";
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private sealed record SyncResult(VanguardOwnerLootInterestResponseDto Response, bool WasSet)
    {
        public static SyncResult Failure(string? owner, string reason) => new(new VanguardOwnerLootInterestResponseDto
        {
            Success = false,
            OwnerProfileId = owner ?? string.Empty,
            Reason = reason
        }, false);
    }
}
#endif
