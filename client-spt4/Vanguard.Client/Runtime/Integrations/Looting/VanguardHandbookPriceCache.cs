#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using Vanguard.Client.Diagnostics;

// Responsibility: Provides Handbook Price Cache support for the external AI integration.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Integrations.Looting;

/// <summary>
/// Vanguard-owned, headless-safe base-price cache. EFT's menu-created HandbookClass is not a reliable
/// authority on a Fika headless process, so Vanguard fetches the SPT handbook once and performs O(1) lookups.
/// ORBIT/LootingBots remain optional fallbacks in VanguardOrbitLootValueReader, never dependencies.
/// </summary>
internal static class VanguardHandbookPriceCache
{
    private static readonly object Sync = new();
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private static Dictionary<string, float>? prices;
    private static Task<LoadResult>? loadTask;
    private static DateTimeOffset retryAtUtc = DateTimeOffset.MinValue;
    private static bool loadedLogged;

    public static void Tick()
    {
        DrainCompleted();
        lock (Sync)
        {
            if (prices is { Count: > 0 } || loadTask != null || DateTimeOffset.UtcNow < retryAtUtc) return;
            loadTask = Task.Run(LoadFromServer);
        }
    }

    public static bool TryGetPrice(string? templateId, out float price)
    {
        price = 0f;
        if (string.IsNullOrWhiteSpace(templateId)) return false;
        lock (Sync)
        {
            return prices != null && prices.TryGetValue(templateId.Trim(), out price) && price > 0f;
        }
    }

    private static void DrainCompleted()
    {
        Task<LoadResult>? completed;
        lock (Sync)
        {
            completed = loadTask;
            if (completed == null || !completed.IsCompleted) return;
            loadTask = null;
        }

        LoadResult result;
        try { result = completed.GetAwaiter().GetResult(); }
        catch (Exception exception) { result = new LoadResult(null, "task_exception_" + exception.GetType().Name); }

        lock (Sync)
        {
            if (result.Prices is { Count: > 0 })
            {
                prices = result.Prices;
                retryAtUtc = DateTimeOffset.MaxValue;
                if (!loadedLogged)
                {
                    loadedLogged = true;
                    VanguardClientDiagnosticsLog.Info(
                        VanguardBuildVersion.UnifiedOpportunisticLootReadModelStatusTag,
                        $"VANGUARD_HANDBOOK_PRICE_CACHE_READY entries={prices.Count}; source=spt_server_handbook; lookup=O1; externalDependency=false");
                }
                return;
            }

            retryAtUtc = DateTimeOffset.UtcNow + RetryInterval;
        }

        VanguardClientDiagnosticsLog.Warning(
            VanguardBuildVersion.UnifiedOpportunisticLootReadModelStatusTag,
            $"VANGUARD_HANDBOOK_PRICE_CACHE_FAILED reason={Safe(result.Reason)}; retrySeconds={RetryInterval.TotalSeconds:0}; fallback=orbit_then_lootingbots_then_zero");
    }

    private static LoadResult LoadFromServer()
    {
        try
        {
            string json = RequestHandler.GetJson("/client/handbook/templates");
            if (string.IsNullOrWhiteSpace(json)) return new LoadResult(null, "empty_server_response");
            JToken root = JToken.Parse(json);
            JToken data = root["data"] ?? root["Data"] ?? root;
            if (data.Type == JTokenType.String)
            {
                string? nested = data.Value<string>();
                if (string.IsNullOrWhiteSpace(nested)) return new LoadResult(null, "empty_data_envelope");
                data = JToken.Parse(nested);
            }

            if ((data["Items"] ?? data["items"]) is not JArray items) return new LoadResult(null, "items_array_missing");
            var loaded = new Dictionary<string, float>(items.Count, StringComparer.OrdinalIgnoreCase);
            foreach (JToken token in items)
            {
                string? id = (token["Id"] ?? token["id"] ?? token["_id"])?.Value<string>();
                JToken? rawPrice = token["Price"] ?? token["price"];
                if (string.IsNullOrWhiteSpace(id) || rawPrice == null) continue;
                float value;
                try { value = rawPrice.Value<float>(); }
                catch { continue; }
                if (value > 0f) loaded[id.Trim()] = value;
            }
            return new LoadResult(loaded, loaded.Count > 0 ? "ok" : "no_positive_prices");
        }
        catch (Exception exception)
        {
            return new LoadResult(null, "server_fetch_exception_" + exception.GetType().Name);
        }
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
    private sealed record LoadResult(Dictionary<string, float>? Prices, string Reason);
}
#endif
