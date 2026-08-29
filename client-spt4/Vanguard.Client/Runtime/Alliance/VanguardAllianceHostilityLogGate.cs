#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Diagnostics;

// Responsibility: Provides Alliance Hostility Log Gate support for the Operator allegiance runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Alliance;

/// <summary>
/// Compact diagnostics for coop-alliance hostility blocks. B proved that
/// the guard works but could emit thousands of per-check lines. The runtime keeps a
/// per-Operator/friendly summary while allowing rare detail lines for new
/// pairs. This is logging-only and never decides hostility.
/// </summary>
internal static class VanguardAllianceHostilityLogGate
{
    public const string StatusTag = "VANGUARD_COOP_ALLIANCE_NOISE_EARLY_BIND_OK";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, DateTimeOffset> LastDetailByPair = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> CountByBucket = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset lastSummaryUtc = DateTimeOffset.MinValue;
    private static int totalSinceSummary;
    private static int earlyBindSinceSummary;
    private static int playerTargetSinceSummary;
    private static int operatorTargetSinceSummary;

    public static bool RegisterBlockedHostility(
        string action,
        string source,
        VanguardOperatorAllegianceSnapshot snapshot,
        bool forcedEarlyBindProtection)
    {
        var now = DateTimeOffset.UtcNow;
        string relation = ResolveRelation(snapshot);
        string bucket = $"{action}|{source}|{relation}";
        string pairKey = $"{bucket}|{snapshot.ActorProfileId}|{snapshot.TargetProfileId}";
        bool emitDetail = false;

        lock (Sync)
        {
            totalSinceSummary++;
            if (forcedEarlyBindProtection)
            {
                earlyBindSinceSummary++;
            }

            if (snapshot.TargetIsPlayer)
            {
                playerTargetSinceSummary++;
            }

            if (snapshot.TargetIsVanguardOperator)
            {
                operatorTargetSinceSummary++;
            }

            CountByBucket.TryGetValue(bucket, out int count);
            CountByBucket[bucket] = count + 1;

            if (!LastDetailByPair.TryGetValue(pairKey, out var last) || (now - last).TotalSeconds >= 30.0)
            {
                LastDetailByPair[pairKey] = now;
                emitDetail = true;
            }
        }

        TryFlushSummary(now);
        return emitDetail;
    }

    public static void Tick()
    {
        TryFlushSummary(DateTimeOffset.UtcNow);
    }

    private static void TryFlushSummary(DateTimeOffset now)
    {
        string message;
        lock (Sync)
        {
            if ((now - lastSummaryUtc).TotalSeconds < 15.0 || totalSinceSummary <= 0)
            {
                return;
            }

            string topBuckets = string.Join(",", CountByBucket
                .OrderByDescending(pair => pair.Value)
                .Take(4)
                .Select(pair => $"{Sanitize(pair.Key)}:{pair.Value}"));

            message = $"VANGUARD_COOP_ALLIANCE_HOSTILITY_BLOCKED_SUMMARY total={totalSinceSummary}; playerTargets={playerTargetSinceSummary}; operatorTargets={operatorTargetSinceSummary}; earlyBind={earlyBindSinceSummary}; buckets={topBuckets}; mode={VanguardRaidAlliancePolicy.Mode}; canonicalAlliance={VanguardRaidAlliancePolicy.DefaultAllianceId}";
            totalSinceSummary = 0;
            earlyBindSinceSummary = 0;
            playerTargetSinceSummary = 0;
            operatorTargetSinceSummary = 0;
            CountByBucket.Clear();
            lastSummaryUtc = now;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag, message);
    }

    private static string ResolveRelation(VanguardOperatorAllegianceSnapshot snapshot)
    {
        if (snapshot.TargetIsPlayer)
        {
            return "player";
        }

        if (snapshot.TargetIsVanguardOperator)
        {
            return "operator";
        }

        return "unknown";
    }

    private static string Sanitize(string value)
    {
        return value.Replace(' ', '_').Replace(';', '_').Replace(',', '_');
    }
}
#else
namespace Vanguard.Client.Runtime.Alliance;

internal static class VanguardAllianceHostilityLogGate
{
    public const string StatusTag = "VANGUARD_COOP_ALLIANCE_NOISE_EARLY_BIND_OK";
    public static void Tick() { }
}
#endif
