using System;

#if SPT_CLIENT
using BepInEx.Logging;
#endif

// Responsibility: Produces bounded diagnostics/telemetry for Client Diagnostics Log in the client diagnostics.
// Flow: Runtime facts are normalized, deduplicated/rate-gated where needed, then emitted according to Vanguard presentation levels.
// Authority boundary: Observation only; telemetry never changes the gameplay decision it reports.
// Invariant: Operational output stays actionable and repetitive detail remains restricted to diagnostic/trace levels.
namespace Vanguard.Client.Diagnostics;

/// <summary>
/// Single diagnostics boundary for Vanguard. The runtime adds lazy, typed emission so hot runtime
/// paths can reject disabled diagnostics before allocating payload strings. Gameplay code must
/// never read this logger to decide an action.
/// </summary>
internal static class VanguardClientDiagnosticsLog
{
    private const string AuditProfileTag = "VANGUARD_AUDIT_PROFILE";
    private static readonly object AuditProfileSync = new();
    private static string lastAuditProfileSignature = "none";
    private static DateTimeOffset lastAuditProfileLogAtUtc = DateTimeOffset.MinValue;

#if SPT_CLIENT
    private static readonly ManualLogSource LogSource = Logger.CreateLogSource("Vanguard.Client");
#endif

    public static bool IsEnabled(VanguardAuditLevel minimumLevel) =>
        VanguardDiagnosticsPolicy.IsEnabled(minimumLevel);

    public static void Startup(string message)
    {
        Write("VANGUARD_STARTUP", message, VanguardAuditLevel.Operational, warningOrError: false, force: true);
    }

    public static void SetAuditLevel(string? level, string source)
    {
        VanguardDiagnosticsPolicy.SetLevel(level);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string signature = VanguardDiagnosticsPolicy.LevelName + "|" + Safe(source);
        bool emit;
        lock (AuditProfileSync)
        {
            emit = !string.Equals(lastAuditProfileSignature, signature, StringComparison.Ordinal)
                || now - lastAuditProfileLogAtUtc >= TimeSpan.FromSeconds(60.0d);
            if (emit)
            {
                lastAuditProfileSignature = signature;
                lastAuditProfileLogAtUtc = now;
            }
        }

        if (emit)
        {
            Write(AuditProfileTag,
                $"level={VanguardDiagnosticsPolicy.LevelName}; source={Safe(source)}; transitionOrHeartbeat=true; heartbeatSeconds=60; gameplayUnaffected=true; profiles=Off_Operational_Diagnostic_Trace",
                VanguardAuditLevel.Operational,
                warningOrError: false,
                force: true);
        }
    }

    public static void ResetAuditSession(string source)
    {
        if (VanguardDiagnosticsPolicy.DrainSuppressionSummary(source, out string summary))
        {
            WriteRaw(AuditProfileTag, summary);
        }

        Write(AuditProfileTag,
            $"sessionReset=true; level={VanguardDiagnosticsPolicy.LevelName}; source={Safe(source)}; previousSuppressionDrained=true; gameplayUnaffected=true",
            VanguardAuditLevel.Operational,
            warningOrError: false,
            force: true);
    }

    // Compatibility boundary for existing call sites. New or hot paths should use the typed lazy
    // overloads below so disabled diagnostics do not build expensive interpolated strings.
    public static void Info(string tag, string message)
    {
        Write(tag, message, VanguardDiagnosticsPolicy.MinimumLevelForLegacy(tag, message), warningOrError: false, force: false);
    }

    public static void Info(string tag, VanguardAuditLevel minimumLevel, Func<string> messageFactory)
    {
        if (minimumLevel == VanguardAuditLevel.Operational)
        {
            WritePolicyAwareOperational(tag, messageFactory);
            return;
        }

        WriteLazy(tag, minimumLevel, messageFactory, warningOrError: false, force: false);
    }

    public static void Operational(string tag, Func<string> messageFactory) =>
        WritePolicyAwareOperational(tag, messageFactory);

    public static void Diagnostic(string tag, Func<string> messageFactory) =>
        WriteLazy(tag, VanguardAuditLevel.Diagnostic, messageFactory, warningOrError: false, force: false);

    public static void Trace(string tag, Func<string> messageFactory) =>
        WriteLazy(tag, VanguardAuditLevel.Trace, messageFactory, warningOrError: false, force: false);

    public static void Warning(string tag, string message)
    {
        string warningMessage = "WARNING: " + message;
        Write(tag, warningMessage, VanguardDiagnosticsPolicy.MinimumLevelForWarning(tag, warningMessage), warningOrError: true, force: false);
    }

    public static void Warning(string tag, Func<string> messageFactory)
    {
        WritePolicyAwareWarning(tag, messageFactory);
    }

    public static void Error(string tag, Exception exception)
    {
        Write(tag, exception.ToString(), VanguardAuditLevel.Operational, warningOrError: true, force: true);
    }

    private static void WritePolicyAwareWarning(string tag, Func<string>? messageFactory)
    {
        if (messageFactory == null || !VanguardDiagnosticsPolicy.IsEnabled(VanguardAuditLevel.Operational))
        {
            return;
        }

        string message;
        try
        {
            message = "WARNING: " + (messageFactory() ?? string.Empty);
        }
        catch (Exception exception)
        {
            WriteRaw(tag, $"ERROR: diagnostic_payload_factory_failed type={exception.GetType().Name}; reason={Safe(exception.Message)}");
            TryWriteSuppressionSummary(tag);
            return;
        }

        VanguardAuditLevel minimumLevel = VanguardDiagnosticsPolicy.MinimumLevelForWarning(tag, message);
        if (!VanguardDiagnosticsPolicy.ShouldEmit(tag, minimumLevel))
        {
            return;
        }

        WriteRaw(tag, message);
        TryWriteSuppressionSummary(tag);
    }

    private static void WritePolicyAwareOperational(string tag, Func<string>? messageFactory)
    {
        if (messageFactory == null || !VanguardDiagnosticsPolicy.IsEnabled(VanguardAuditLevel.Operational))
        {
            return;
        }

        string message;
        try
        {
            message = messageFactory() ?? string.Empty;
        }
        catch (Exception exception)
        {
            message = $"diagnostic_payload_factory_failed type={exception.GetType().Name}; reason={Safe(exception.Message)}";
        }

        VanguardAuditLevel minimumLevel = VanguardDiagnosticsPolicy.MinimumLevelForLegacy(tag, message);
        if (!VanguardDiagnosticsPolicy.ShouldEmit(tag, minimumLevel))
        {
            return;
        }

        WriteRaw(tag, message);
        TryWriteSuppressionSummary(tag);
    }

    private static void WriteLazy(
        string tag,
        VanguardAuditLevel minimumLevel,
        Func<string>? messageFactory,
        bool warningOrError,
        bool force)
    {
        if (messageFactory == null)
        {
            return;
        }

        if (!force && !VanguardDiagnosticsPolicy.ShouldEmit(tag, minimumLevel))
        {
            return;
        }

        string message;
        try
        {
            message = messageFactory() ?? string.Empty;
        }
        catch (Exception exception)
        {
            message = $"diagnostic_payload_factory_failed type={exception.GetType().Name}; reason={Safe(exception.Message)}";
            warningOrError = true;
        }

        WriteRaw(tag, message);
        TryWriteSuppressionSummary(tag);
    }

    private static void Write(
        string tag,
        string message,
        VanguardAuditLevel minimumLevel,
        bool warningOrError,
        bool force)
    {
        if (!force && !VanguardDiagnosticsPolicy.ShouldEmit(tag, minimumLevel))
        {
            return;
        }

        WriteRaw(tag, message);
        TryWriteSuppressionSummary(tag);
    }

    private static void TryWriteSuppressionSummary(string tag)
    {
        if (!string.Equals(tag, AuditProfileTag, StringComparison.OrdinalIgnoreCase)
            && VanguardDiagnosticsPolicy.TryBuildSuppressionSummary(DateTimeOffset.UtcNow, out string summary))
        {
            WriteRaw(AuditProfileTag, summary);
        }
    }

    private static void WriteRaw(string tag, string message)
    {
        string presentedTag = VanguardRuntimeLogPresentation.NormalizeTag(tag);
        string presentedMessage = VanguardRuntimeLogPresentation.NormalizeMessage(tag, message);
#if SPT_CLIENT
        LogSource.LogInfo($"[{presentedTag}] {presentedMessage}");
#else
        Console.WriteLine($"[{presentedTag}] {presentedMessage}");
#endif
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
