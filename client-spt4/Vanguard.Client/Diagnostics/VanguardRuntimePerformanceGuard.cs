using System;
using System.Collections.Generic;
using System.Diagnostics;
using Vanguard.Client.Options;

// Responsibility: Produces bounded diagnostics/telemetry for Runtime Performance Guard in the client diagnostics.
// Flow: Runtime facts are normalized, deduplicated/rate-gated where needed, then emitted according to Vanguard presentation levels.
// Authority boundary: Observation only; telemetry never changes the gameplay decision it reports.
// Invariant: Operational output stays actionable and repetitive detail remains restricted to diagnostic/trace levels.
namespace Vanguard.Client.Diagnostics;

/// <summary>
/// The runtime allocation-free runtime profiler for the authoritative Unity thread.
/// It never changes gameplay decisions. Timings below the slow threshold are ignored;
/// repeated slow samples are aggregated and emitted on a bounded cadence.
/// </summary>
internal static class VanguardRuntimePerformanceGuard
{
    public const string StatusTag = "VANGUARD_RUNTIME_PERFORMANCE_GUARD_STATUS";

    private const double SlowThresholdMilliseconds = 20.0d;
    private const double CriticalThresholdMilliseconds = 100.0d;
    private const double ImmediateNewMaximumMilliseconds = 500.0d;
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(60.0d);
    private static readonly TimeSpan CriticalSummaryInterval = TimeSpan.FromSeconds(15.0d);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, PerformanceState> StateBySubsystem = new(StringComparer.OrdinalIgnoreCase);

    public static long Begin() => VanguardOperatorRuntimeAuditOptions.GetPerformanceTelemetryMode() == VanguardPerformanceTelemetryMode.Off
        ? 0L
        : Stopwatch.GetTimestamp();

    public static void End(string subsystem, long startedTimestamp)
    {
        VanguardPerformanceTelemetryMode telemetryMode = VanguardOperatorRuntimeAuditOptions.GetPerformanceTelemetryMode();
        if (startedTimestamp <= 0 || telemetryMode == VanguardPerformanceTelemetryMode.Off)
        {
            return;
        }

        long elapsedTicks = Stopwatch.GetTimestamp() - startedTimestamp;
        if (elapsedTicks <= 0)
        {
            return;
        }

        double elapsedMilliseconds = elapsedTicks * 1000.0d / Stopwatch.Frequency;
        double effectiveSlowThreshold = telemetryMode == VanguardPerformanceTelemetryMode.Full
            ? 1.0d
            : SlowThresholdMilliseconds;
        if (elapsedMilliseconds < effectiveSlowThreshold)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string key = Normalize(subsystem);
        PerformanceState state;
        bool emit;
        lock (Sync)
        {
            if (!StateBySubsystem.TryGetValue(key, out var existing))
            {
                state = new PerformanceState { LastEmitUtc = DateTimeOffset.MinValue };
                StateBySubsystem[key] = state;
            }
            else
            {
                state = existing;
            }

            state.SlowSamples++;
            state.TotalSlowMilliseconds += elapsedMilliseconds;
            if (elapsedMilliseconds > state.MaxMilliseconds)
            {
                state.MaxMilliseconds = elapsedMilliseconds;
            }

            double previousAllTimeMaximum = state.AllTimeMaxMilliseconds;
            bool newAllTimeMaximum = elapsedMilliseconds > previousAllTimeMaximum;
            if (newAllTimeMaximum)
            {
                state.AllTimeMaxMilliseconds = elapsedMilliseconds;
            }

            bool critical = elapsedMilliseconds >= CriticalThresholdMilliseconds;
            bool significantNewMaximum = previousAllTimeMaximum < ImmediateNewMaximumMilliseconds
                || elapsedMilliseconds >= previousAllTimeMaximum + 100.0d
                || elapsedMilliseconds >= previousAllTimeMaximum * 1.25d;
            bool immediateNewMaximum = newAllTimeMaximum
                && elapsedMilliseconds >= ImmediateNewMaximumMilliseconds
                && significantNewMaximum;
            emit = immediateNewMaximum || (critical
                ? now - state.LastEmitUtc >= CriticalSummaryInterval
                : now - state.LastEmitUtc >= SummaryInterval);
            if (!emit)
            {
                return;
            }

            state.LastEmitUtc = now;
        }

        double average;
        long samples;
        double maximum;
        lock (Sync)
        {
            samples = state.SlowSamples;
            average = samples > 0 ? state.TotalSlowMilliseconds / samples : elapsedMilliseconds;
            maximum = state.MaxMilliseconds;
            state.SlowSamples = 0;
            state.TotalSlowMilliseconds = 0d;
            state.MaxMilliseconds = 0d;
        }

        bool criticalSample = elapsedMilliseconds >= CriticalThresholdMilliseconds;
        if (criticalSample)
        {
            VanguardClientDiagnosticsLog.Warning(StatusTag,
                () => $"VANGUARD_RUNTIME_HOTSPOT subsystem={Safe(key)}; currentMs={elapsedMilliseconds:0.00}; maxMs={maximum:0.00}; avgSlowMs={average:0.00}; slowSamples={samples}; thresholdMs={effectiveSlowThreshold:0.00}; telemetry={telemetryMode}; critical=true; mutation=false; gameplayImpactRisk=true; tag={StatusTag}");
        }
        else
        {
            VanguardClientDiagnosticsLog.Diagnostic(StatusTag,
                () => $"VANGUARD_RUNTIME_HOTSPOT subsystem={Safe(key)}; currentMs={elapsedMilliseconds:0.00}; maxMs={maximum:0.00}; avgSlowMs={average:0.00}; slowSamples={samples}; thresholdMs={effectiveSlowThreshold:0.00}; telemetry={telemetryMode}; critical=false; mutation=false; gameplayImpactRisk=false; tag={StatusTag}");
        }
    }

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            StateBySubsystem.Clear();
        }

        VanguardClientDiagnosticsLog.Diagnostic(StatusTag,
            () => $"VANGUARD_RUNTIME_PERFORMANCE_RESET reason={Safe(reason)}; telemetry={VanguardOperatorRuntimeAuditOptions.GetPerformanceTelemetryName()}; slowThresholdMs={SlowThresholdMilliseconds:0.00}; criticalThresholdMs={CriticalThresholdMilliseconds:0.00}; summarySeconds={SummaryInterval.TotalSeconds:0.00}; allocationFreeTiming=true; gameplayMutation=false; tag={StatusTag}");
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => Normalize(value).Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private sealed class PerformanceState
    {
        public long SlowSamples;
        public double TotalSlowMilliseconds;
        public double MaxMilliseconds;
        public double AllTimeMaxMilliseconds;
        public DateTimeOffset LastEmitUtc;
    }
}
