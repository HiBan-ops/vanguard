#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Vanguard.Client.Options;
using Vanguard.Client.Raid.Runtime;

// Responsibility: Measures selected Vanguard runtime work against a small frame-time budget and reports sustained hotspots without changing gameplay.
// Flow: Instrumented scopes accumulate elapsed time/call counts, periodic summaries classify budget pressure, and repetitive detail is rate-limited into diagnostic output.
// Authority boundary: Observation only: the guard never skips, delays or reorders gameplay work merely because a budget warning was observed.
// Invariant: Measurement overhead stays bounded and a performance warning cannot become an implicit gameplay authority or execution gate.
namespace Vanguard.Client.Diagnostics;

/// <summary>
/// The runtime soft frame-budget guard for the authoritative Unity thread.
/// Mandatory scheduler, active medical, safety and movement work is never skipped.
/// Observational/opportunistic services may be staggered after the soft budget is consumed,
/// while heavyweight NavMesh/path probes share explicit per-frame token pools.
/// </summary>
internal static class VanguardRuntimeFrameBudgetGuard
{
    public const string StatusTag = "VANGUARD_FRAME_BUDGET_GUARD_STATUS";

    private const double SoftBudgetMilliseconds = 6.0d;
    private const double HardDiagnosticMilliseconds = 12.0d;
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(60.0d);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, BudgetState> StateBySubsystem = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> HeavyWorkConsumedByCategory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> HeavyWorkConsumedByOwnerCategory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> OwnerIndexByProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset lastGlobalSummaryUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextOwnerRefreshUtc = DateTimeOffset.MinValue;
    private static int lastRuntimeOperatorCount = -1;
    private static string lastOwnerSignature = string.Empty;

    private static long frameStartedTimestamp;
    private static DateTimeOffset frameStartedUtc = DateTimeOffset.MinValue;
    private static long frameSequence;

    public static void BeginFrame(DateTimeOffset now)
    {
        frameStartedTimestamp = Stopwatch.GetTimestamp();
        frameStartedUtc = now;
        frameSequence++;
        lock (Sync)
        {
            HeavyWorkConsumedByCategory.Clear();
            HeavyWorkConsumedByOwnerCategory.Clear();
            if (now >= nextOwnerRefreshUtc)
            {
                var records = VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators();
                string[] owners = records
                    .Where(record => !string.IsNullOrWhiteSpace(record.OwnerProfileId))
                    .Select(record => record.OwnerProfileId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                string ownerSignature = string.Join("|", owners);
                if (records.Count != lastRuntimeOperatorCount
                    || !string.Equals(ownerSignature, lastOwnerSignature, StringComparison.OrdinalIgnoreCase)
                    || OwnerIndexByProfileId.Count == 0)
                {
                    OwnerIndexByProfileId.Clear();
                    for (int index = 0; index < owners.Length; index++)
                    {
                        OwnerIndexByProfileId[owners[index]] = index;
                    }
                    lastRuntimeOperatorCount = records.Count;
                    lastOwnerSignature = ownerSignature;
                }
                nextOwnerRefreshUtc = now + TimeSpan.FromSeconds(1.0d);
            }
        }
    }

    public static bool ShouldRunOptional(string subsystem, DateTimeOffset now, TimeSpan maxDeferral, out string reason)
    {
        string key = Normalize(subsystem);
        double elapsed = ElapsedMilliseconds();
        BudgetSummary? summary = null;
        bool shouldRun;
        bool postStallRecovery = VanguardHeadlessRuntimeStallGuard.IsOptionalRecoveryActive(now, out string recoveryReason);
        lock (Sync)
        {
            if (!StateBySubsystem.TryGetValue(key, out var state))
            {
                state = new BudgetState();
                StateBySubsystem[key] = state;
            }

            if (state.FirstDeferredUtc == DateTimeOffset.MinValue)
            {
                state.FirstDeferredUtc = now;
            }

            bool hasRun = state.LastRunUtc != DateTimeOffset.MinValue;
            DateTimeOffset deferralOrigin = hasRun ? state.LastRunUtc : state.FirstDeferredUtc;
            bool forceByMaxDeferral = maxDeferral <= TimeSpan.Zero || now - deferralOrigin >= maxDeferral;
            if (postStallRecovery && !forceByMaxDeferral)
            {
                state.DeferredRuns++;
                state.LastDeferredUtc = now;
                reason = recoveryReason + ";subsystem=" + Safe(key) + ";maxDeferralHonored=true";
                shouldRun = false;
            }
            else if (elapsed <= SoftBudgetMilliseconds || forceByMaxDeferral)
            {
                state.LastRunUtc = now;
                state.FirstDeferredUtc = DateTimeOffset.MinValue;
                if (forceByMaxDeferral && postStallRecovery)
                {
                    state.ForcedRuns++;
                    reason = "post_stall_max_deferral_elapsed;elapsedMs=" + elapsed.ToString("0.00", CultureInfo.InvariantCulture)
                        + ";recovery=" + Safe(recoveryReason);
                }
                else if (forceByMaxDeferral && elapsed > SoftBudgetMilliseconds)
                {
                    state.ForcedRuns++;
                    reason = "max_deferral_elapsed;elapsedMs=" + elapsed.ToString("0.00", CultureInfo.InvariantCulture);
                }
                else
                {
                    reason = "within_soft_budget;elapsedMs=" + elapsed.ToString("0.00", CultureInfo.InvariantCulture);
                }
                shouldRun = true;
            }
            else
            {
                state.DeferredRuns++;
                state.LastDeferredUtc = now;
                reason = "soft_budget_exhausted;elapsedMs=" + elapsed.ToString("0.00", CultureInfo.InvariantCulture);
                shouldRun = false;
            }

            summary = CaptureSummaryUnsafe(key, state, now, elapsed);
        }

        EmitSummary(summary);
        return shouldRun;
    }

    /// <summary>
    /// Consumes a token from a heavyweight per-frame pool. Callers must leave their current
    /// operation pending when the pool is exhausted; they must never convert budget exhaustion
    /// into a gameplay failure or cooldown.
    /// </summary>
    public static bool TryConsumeHeavyWork(string category, int cost, int maxPerFrame, out string reason)
    {
        string key = Normalize(category);
        int normalizedCost = Math.Max(1, cost);
        int normalizedMaximum = Math.Max(normalizedCost, maxPerFrame);
        lock (Sync)
        {
            HeavyWorkConsumedByCategory.TryGetValue(key, out int consumed);
            if (consumed + normalizedCost > normalizedMaximum)
            {
                reason = "frame_token_exhausted;category=" + Safe(key)
                    + ";consumed=" + consumed.ToString(CultureInfo.InvariantCulture)
                    + ";cost=" + normalizedCost.ToString(CultureInfo.InvariantCulture)
                    + ";max=" + normalizedMaximum.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            consumed += normalizedCost;
            HeavyWorkConsumedByCategory[key] = consumed;
            reason = "frame_token_granted;category=" + Safe(key)
                + ";consumed=" + consumed.ToString(CultureInfo.InvariantCulture)
                + ";max=" + normalizedMaximum.ToString(CultureInfo.InvariantCulture);
            return true;
        }
    }

    public static bool TryConsumeHeavyWorkForOwner(
        string category,
        string? ownerProfileId,
        int cost,
        int maxPerFrame,
        out string reason)
    {
        string owner = Normalize(ownerProfileId);
        string key = Normalize(category);
        int normalizedCost = Math.Max(1, cost);
        int normalizedMaximum = Math.Max(normalizedCost, maxPerFrame);
        lock (Sync)
        {
            int knownOwnerCount = OwnerIndexByProfileId.Count;
            int ownerCount = Math.Max(1, knownOwnerCount);
            if (knownOwnerCount > 0 && !OwnerIndexByProfileId.ContainsKey(owner))
            {
                reason = "owner_identity_pending;category=" + Safe(key)
                    + ";owner=" + Safe(owner)
                    + ";knownOwners=" + knownOwnerCount.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            if (ownerCount > 1
                && OwnerIndexByProfileId.TryGetValue(owner, out int ownerIndex)
                && ownerCount > normalizedMaximum)
            {
                int firstSelectedOwnerIndex = (int)(Math.Abs(frameSequence) % ownerCount);
                int relativeIndex = (ownerIndex - firstSelectedOwnerIndex + ownerCount) % ownerCount;
                if (relativeIndex >= normalizedMaximum)
                {
                    reason = "owner_fair_turn_pending;category=" + Safe(key)
                        + ";owner=" + Safe(owner)
                        + ";ownerIndex=" + ownerIndex.ToString(CultureInfo.InvariantCulture)
                        + ";firstSelectedOwnerIndex=" + firstSelectedOwnerIndex.ToString(CultureInfo.InvariantCulture)
                        + ";selectedOwnerCount=" + normalizedMaximum.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
            }

            string ownerKey = key + "|" + owner;
            int ownerMaximum = ownerCount <= 1
                ? normalizedMaximum
                : Math.Max(normalizedCost, (int)Math.Ceiling(normalizedMaximum / (double)ownerCount));
            HeavyWorkConsumedByOwnerCategory.TryGetValue(ownerKey, out int ownerConsumed);
            HeavyWorkConsumedByCategory.TryGetValue(key, out int consumed);

            // Preserve at least one grant for every known owner before an already-served owner
            // consumes a second token. This closes the max=4/owners=3 call-order starvation case.
            if (ownerCount > 1 && ownerConsumed > 0)
            {
                int unservedOwners = 0;
                foreach (string knownOwner in OwnerIndexByProfileId.Keys)
                {
                    string knownOwnerKey = key + "|" + knownOwner;
                    if (!HeavyWorkConsumedByOwnerCategory.TryGetValue(knownOwnerKey, out int knownConsumed)
                        || knownConsumed == 0)
                    {
                        unservedOwners++;
                    }
                }

                int remainingAfterGrant = normalizedMaximum - (consumed + normalizedCost);
                int reservedForUnservedOwners = unservedOwners * normalizedCost;
                if (remainingAfterGrant < reservedForUnservedOwners)
                {
                    reason = "owner_fair_reserve_pending;category=" + Safe(key)
                        + ";owner=" + Safe(owner)
                        + ";remainingAfterGrant=" + remainingAfterGrant.ToString(CultureInfo.InvariantCulture)
                        + ";reservedForUnservedOwners=" + reservedForUnservedOwners.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
            }

            if (ownerConsumed + normalizedCost > ownerMaximum)
            {
                reason = "owner_token_exhausted;category=" + Safe(key)
                    + ";owner=" + Safe(owner)
                    + ";ownerConsumed=" + ownerConsumed.ToString(CultureInfo.InvariantCulture)
                    + ";ownerMax=" + ownerMaximum.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            if (consumed + normalizedCost > normalizedMaximum)
            {
                reason = "frame_token_exhausted;category=" + Safe(key)
                    + ";consumed=" + consumed.ToString(CultureInfo.InvariantCulture)
                    + ";max=" + normalizedMaximum.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            HeavyWorkConsumedByCategory[key] = consumed + normalizedCost;
            HeavyWorkConsumedByOwnerCategory[ownerKey] = ownerConsumed + normalizedCost;
            reason = "owner_fair_token_granted;category=" + Safe(key)
                + ";owner=" + Safe(owner)
                + ";frameConsumed=" + (consumed + normalizedCost).ToString(CultureInfo.InvariantCulture)
                + ";frameMax=" + normalizedMaximum.ToString(CultureInfo.InvariantCulture)
                + ";ownerConsumed=" + (ownerConsumed + normalizedCost).ToString(CultureInfo.InvariantCulture)
                + ";ownerMax=" + ownerMaximum.ToString(CultureInfo.InvariantCulture);
            return true;
        }
    }

    public static void MarkMandatory(string subsystem, long startedTimestamp)
    {
        if (startedTimestamp <= 0)
        {
            return;
        }

        double elapsed = (Stopwatch.GetTimestamp() - startedTimestamp) * 1000.0d / Stopwatch.Frequency;
        if (elapsed < HardDiagnosticMilliseconds)
        {
            return;
        }

        string key = Normalize(subsystem);
        BudgetSummary? summary;
        lock (Sync)
        {
            if (!StateBySubsystem.TryGetValue(key, out var state))
            {
                state = new BudgetState();
                StateBySubsystem[key] = state;
            }

            state.MandatorySlowRuns++;
            if (elapsed > state.MaxMandatoryMilliseconds)
            {
                state.MaxMandatoryMilliseconds = elapsed;
            }

            summary = CaptureSummaryUnsafe(key, state, DateTimeOffset.UtcNow, ElapsedMilliseconds());
        }

        EmitSummary(summary);
    }

    public static double ElapsedMilliseconds()
    {
        long started = frameStartedTimestamp;
        if (started <= 0)
        {
            return 0d;
        }

        long ticks = Stopwatch.GetTimestamp() - started;
        return ticks <= 0 ? 0d : ticks * 1000.0d / Stopwatch.Frequency;
    }

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            StateBySubsystem.Clear();
            HeavyWorkConsumedByCategory.Clear();
            HeavyWorkConsumedByOwnerCategory.Clear();
            OwnerIndexByProfileId.Clear();
            lastGlobalSummaryUtc = DateTimeOffset.UtcNow;
            nextOwnerRefreshUtc = DateTimeOffset.MinValue;
            lastRuntimeOperatorCount = -1;
            lastOwnerSignature = string.Empty;
        }

        frameStartedTimestamp = 0;
        frameStartedUtc = DateTimeOffset.MinValue;
        frameSequence = 0;
        VanguardClientDiagnosticsLog.Diagnostic(StatusTag,
            () => $"VANGUARD_FRAME_BUDGET_RESET reason={Safe(reason)}; softBudgetMs={SoftBudgetMilliseconds:0.00}; mandatoryWorkNeverDeferred=true; optionalMaxDeferralBounded=true; heavyweightTokenPools=true; mutation=stagger_optional_only; tag={StatusTag}");
    }

    private static BudgetSummary? CaptureSummaryUnsafe(string key, BudgetState state, DateTimeOffset now, double frameElapsed)
    {
        if (now - lastGlobalSummaryUtc < SummaryInterval)
        {
            return null;
        }

        long deferred = 0;
        long forced = 0;
        long mandatory = 0;
        double maximum = 0d;
        string maximumSubsystem = "none";
        foreach (var pair in StateBySubsystem)
        {
            deferred += pair.Value.DeferredRuns;
            forced += pair.Value.ForcedRuns;
            mandatory += pair.Value.MandatorySlowRuns;
            if (pair.Value.MaxMandatoryMilliseconds > maximum)
            {
                maximum = pair.Value.MaxMandatoryMilliseconds;
                maximumSubsystem = pair.Key;
            }
        }

        if (deferred == 0 && forced == 0 && mandatory == 0)
        {
            return null;
        }

        lastGlobalSummaryUtc = now;
        foreach (var pair in StateBySubsystem)
        {
            pair.Value.DeferredRuns = 0;
            pair.Value.ForcedRuns = 0;
            pair.Value.MandatorySlowRuns = 0;
            pair.Value.MaxMandatoryMilliseconds = 0d;
        }

        return new BudgetSummary(
            maximumSubsystem,
            frameSequence,
            frameElapsed,
            deferred,
            forced,
            mandatory,
            maximum,
            frameStartedUtc);
    }

    private static void EmitSummary(BudgetSummary? summary)
    {
        if (!summary.HasValue)
        {
            return;
        }

        if (VanguardOperatorRuntimeAuditOptions.GetPerformanceTelemetryMode() == VanguardPerformanceTelemetryMode.Off)
        {
            return;
        }

        BudgetSummary value = summary.Value;
        bool critical = value.FrameElapsedMilliseconds >= 100.0d || value.MaxMandatoryMilliseconds >= 100.0d;
        if (critical)
        {
            VanguardClientDiagnosticsLog.Warning(StatusTag,
                () => $"VANGUARD_FRAME_BUDGET_SUMMARY frame={value.FrameSequence}; frameMs={value.FrameElapsedMilliseconds:0.00}; deferred={value.DeferredRuns}; forced={value.ForcedRuns}; mandatorySlow={value.MandatorySlowRuns}; maxSubsystem={Safe(value.Subsystem)}; maxMs={value.MaxMandatoryMilliseconds:0.00}; critical=true; mutation=false; tag={StatusTag}");
        }
        else
        {
            VanguardClientDiagnosticsLog.Diagnostic(StatusTag,
                () => $"VANGUARD_FRAME_BUDGET_SUMMARY frame={value.FrameSequence}; frameMs={value.FrameElapsedMilliseconds:0.00}; deferred={value.DeferredRuns}; forced={value.ForcedRuns}; mandatorySlow={value.MandatorySlowRuns}; maxSubsystem={Safe(value.Subsystem)}; maxMs={value.MaxMandatoryMilliseconds:0.00}; critical=false; mutation=false; tag={StatusTag}");
        }
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
    private static string Safe(string? value) => Normalize(value).Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    private static string Bool(bool value) => value ? "true" : "false";

    private sealed class BudgetState
    {
        public DateTimeOffset LastRunUtc;
        public DateTimeOffset FirstDeferredUtc;
        public DateTimeOffset LastDeferredUtc;
        public long DeferredRuns;
        public long ForcedRuns;
        public long MandatorySlowRuns;
        public double MaxMandatoryMilliseconds;
    }

    private readonly struct BudgetSummary
    {
        public BudgetSummary(string subsystem, long frameSequence, double frameElapsedMilliseconds, long deferredRuns, long forcedRuns, long mandatorySlowRuns, double maxMandatoryMilliseconds, DateTimeOffset frameStartedUtc)
        {
            Subsystem = subsystem;
            FrameSequence = frameSequence;
            FrameElapsedMilliseconds = frameElapsedMilliseconds;
            DeferredRuns = deferredRuns;
            ForcedRuns = forcedRuns;
            MandatorySlowRuns = mandatorySlowRuns;
            MaxMandatoryMilliseconds = maxMandatoryMilliseconds;
            FrameStartedUtc = frameStartedUtc;
        }

        public string Subsystem { get; }
        public long FrameSequence { get; }
        public double FrameElapsedMilliseconds { get; }
        public long DeferredRuns { get; }
        public long ForcedRuns { get; }
        public long MandatorySlowRuns { get; }
        public double MaxMandatoryMilliseconds { get; }
        public DateTimeOffset FrameStartedUtc { get; }
    }
}
#endif
