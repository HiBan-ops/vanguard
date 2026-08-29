#if SPT_CLIENT
using System;
using System.Diagnostics;
using System.Globalization;

// Responsibility: Produces bounded diagnostics/telemetry for Headless Runtime Stall Guard in the client diagnostics.
// Flow: Runtime facts are normalized, deduplicated/rate-gated where needed, then emitted according to Vanguard presentation levels.
// Authority boundary: Observation only; telemetry never changes the gameplay decision it reports.
// Invariant: Operational output stays actionable and repetitive detail remains restricted to diagnostic/trace levels.
namespace Vanguard.Client.Diagnostics;

/// <summary>
/// Vanguard main-thread stall observer and short recovery gate.
/// It never changes mandatory gameplay work. After a slow authoritative frame it only
/// suppresses optional diagnostics/HUD work for a bounded recovery window and records
/// GC collection deltas so a future runtime can distinguish managed pauses from a slow
/// Vanguard subsystem.
/// </summary>
internal static class VanguardHeadlessRuntimeStallGuard
{
    public const string StatusTag = "VANGUARD_HEADLESS_RUNTIME_STALL_GUARD_STATUS";

    private const double RecoveryThresholdMilliseconds = 250.0d;
    private const double CriticalThresholdMilliseconds = 500.0d;
    private static readonly TimeSpan RecoveryDuration = TimeSpan.FromSeconds(2.0d);
    private static readonly TimeSpan RepeatLogInterval = TimeSpan.FromSeconds(5.0d);

    private static long frameStartedTimestamp;
    private static long frameSequence;
    private static int gen0AtFrameStart;
    private static int gen1AtFrameStart;
    private static int gen2AtFrameStart;
    private static DateTimeOffset recoveryUntilUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset lastCriticalLogUtc = DateTimeOffset.MinValue;
    private static double allTimeMaximumMilliseconds;

    public static void BeginFrame(DateTimeOffset now)
    {
        frameStartedTimestamp = Stopwatch.GetTimestamp();
        frameSequence++;
        gen0AtFrameStart = GC.CollectionCount(0);
        gen1AtFrameStart = GC.CollectionCount(1);
        gen2AtFrameStart = GC.CollectionCount(2);

        if (recoveryUntilUtc != DateTimeOffset.MinValue && now >= recoveryUntilUtc)
        {
            recoveryUntilUtc = DateTimeOffset.MinValue;
        }
    }

    public static void EndFrame(DateTimeOffset now)
    {
        long started = frameStartedTimestamp;
        if (started <= 0L)
        {
            return;
        }

        double elapsedMilliseconds = ElapsedMilliseconds(started);
        if (elapsedMilliseconds < RecoveryThresholdMilliseconds)
        {
            return;
        }

        DateTimeOffset proposedRecoveryUntil = now + RecoveryDuration;
        if (proposedRecoveryUntil > recoveryUntilUtc)
        {
            recoveryUntilUtc = proposedRecoveryUntil;
        }

        if (elapsedMilliseconds < CriticalThresholdMilliseconds)
        {
            return;
        }

        double previousMaximum = allTimeMaximumMilliseconds;
        bool newMaximum = elapsedMilliseconds > previousMaximum;
        if (newMaximum)
        {
            allTimeMaximumMilliseconds = elapsedMilliseconds;
        }

        bool significantNewMaximum = newMaximum
            && (previousMaximum < CriticalThresholdMilliseconds
                || elapsedMilliseconds >= previousMaximum + 100.0d
                || elapsedMilliseconds >= previousMaximum * 1.25d);
        if (!significantNewMaximum && now - lastCriticalLogUtc < RepeatLogInterval)
        {
            return;
        }

        lastCriticalLogUtc = now;
        int gen0Delta = Math.Max(0, GC.CollectionCount(0) - gen0AtFrameStart);
        int gen1Delta = Math.Max(0, GC.CollectionCount(1) - gen1AtFrameStart);
        int gen2Delta = Math.Max(0, GC.CollectionCount(2) - gen2AtFrameStart);
        long managedBytes = 0L;
        try
        {
            managedBytes = GC.GetTotalMemory(forceFullCollection: false);
        }
        catch
        {
            managedBytes = -1L;
        }

        VanguardClientDiagnosticsLog.Warning(
            StatusTag,
            () => "VANGUARD_HEADLESS_RUNTIME_STALL"
                + ";frame=" + frameSequence.ToString(CultureInfo.InvariantCulture)
                + ";elapsedMs=" + elapsedMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)
                + ";maxMs=" + allTimeMaximumMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)
                + ";gc0Delta=" + gen0Delta.ToString(CultureInfo.InvariantCulture)
                + ";gc1Delta=" + gen1Delta.ToString(CultureInfo.InvariantCulture)
                + ";gc2Delta=" + gen2Delta.ToString(CultureInfo.InvariantCulture)
                + ";managedBytes=" + managedBytes.ToString(CultureInfo.InvariantCulture)
                + ";optionalRecoveryUntil=" + recoveryUntilUtc.ToString("O")
                + ";mandatoryGameplayDeferred=false"
                + ";exfilMutation=false"
                + ";tag=" + StatusTag);
    }

    public static bool IsOptionalRecoveryActive(DateTimeOffset now, out string reason)
    {
        if (recoveryUntilUtc == DateTimeOffset.MinValue || now >= recoveryUntilUtc)
        {
            reason = "none";
            return false;
        }

        reason = "post_stall_optional_recovery"
            + ";remainingMs=" + Math.Max(0d, (recoveryUntilUtc - now).TotalMilliseconds).ToString("0", CultureInfo.InvariantCulture)
            + ";mandatoryGameplayDeferred=false";
        return true;
    }

    public static void Reset(string reason)
    {
        frameStartedTimestamp = 0L;
        frameSequence = 0L;
        gen0AtFrameStart = 0;
        gen1AtFrameStart = 0;
        gen2AtFrameStart = 0;
        recoveryUntilUtc = DateTimeOffset.MinValue;
        lastCriticalLogUtc = DateTimeOffset.MinValue;
        allTimeMaximumMilliseconds = 0d;
        VanguardClientDiagnosticsLog.Diagnostic(
            StatusTag,
            () => "VANGUARD_HEADLESS_RUNTIME_STALL_GUARD_RESET"
                + ";reason=" + Safe(reason)
                + ";recoveryThresholdMs=" + RecoveryThresholdMilliseconds.ToString("0", CultureInfo.InvariantCulture)
                + ";criticalThresholdMs=" + CriticalThresholdMilliseconds.ToString("0", CultureInfo.InvariantCulture)
                + ";recoverySeconds=" + RecoveryDuration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)
                + ";mandatoryGameplayDeferred=false"
                + ";tag=" + StatusTag);
    }

    private static double ElapsedMilliseconds(long startedTimestamp)
    {
        long ticks = Stopwatch.GetTimestamp() - startedTimestamp;
        return ticks <= 0L ? 0d : ticks * 1000.0d / Stopwatch.Frequency;
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }
}
#else
namespace Vanguard.Client.Diagnostics;

internal static class VanguardHeadlessRuntimeStallGuard
{
    public const string StatusTag = "VANGUARD_HEADLESS_RUNTIME_STALL_GUARD_STATUS";
    public static void BeginFrame(System.DateTimeOffset now) { }
    public static void EndFrame(System.DateTimeOffset now) { }
    public static bool IsOptionalRecoveryActive(System.DateTimeOffset now, out string reason) { reason = "none"; return false; }
    public static void Reset(string reason) { }
}
#endif
