using System.Reflection;
using SPTarkov.Server.Core.Models.Utils;

// Responsibility: Normalizes Vanguard server diagnostics, mirrors them to the SPT logger, and appends the same presented events to Vanguard's support log.
// Flow: Callers select SPT severity, the presentation layer normalizes semantic tags/messages, then output is written to the host logger and best-effort Vanguard file sink.
// Authority boundary: Observation only; this helper does not decide gameplay, persistence, or client audit-level policy.
// Invariant: Logging/file-sink failures never block Vanguard server startup or runtime behavior, and event normalization must not change the underlying reported fact.
namespace Vanguard.Server.Diagnostics;

internal static class VanguardServerDiagnosticsLog
{
    private static readonly object FileLock = new();

    public static void Startup<TLogger>(ISptLogger<TLogger> logger, string message)
    {
        string line = $"[VANGUARD_STARTUP] {message}";
        string presented = VanguardRuntimeLogPresentation.PresentLine(line);
        logger.Success(presented);
        WriteFile(presented);
    }

    public static void Info<TLogger>(ISptLogger<TLogger> logger, string tag, string message)
    {
        string presentedTag = VanguardRuntimeLogPresentation.NormalizeTag(tag);
        string presentedMessage = VanguardRuntimeLogPresentation.NormalizeMessage(tag, message);
        string line = $"[{presentedTag}] {presentedMessage}";
        logger.Info(line);
        WriteFile(line);
    }

    public static void Error<TLogger>(ISptLogger<TLogger> logger, string tag, string message)
    {
        string presentedTag = VanguardRuntimeLogPresentation.NormalizeTag(tag);
        string presentedMessage = VanguardRuntimeLogPresentation.NormalizeMessage(tag, message);
        string line = $"[{presentedTag}] {presentedMessage}";
        logger.Error(line);
        WriteFile($"[{presentedTag}] ERROR {presentedMessage}");
    }

    public static string Present(string message) =>
        VanguardRuntimeLogPresentation.PresentLine(message);

    private static void WriteFile(string message)
    {
        try
        {
            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var modDirectory = Path.GetDirectoryName(assemblyPath) ?? AppContext.BaseDirectory;
            var userDirectory = ResolveUserDirectory(modDirectory);
            var dataDirectory = Path.Combine(userDirectory, "vanguard", "operators");
            Directory.CreateDirectory(dataDirectory);
            var line = $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}";
            lock (FileLock)
            {
                File.AppendAllText(Path.Combine(dataDirectory, "vanguard-server.log"), line);
            }
        }
        catch
        {
            // Diagnostics must never block server startup.
        }
    }

    private static string ResolveUserDirectory(string modDirectory)
    {
        try
        {
            var modInfo = new DirectoryInfo(modDirectory);
            DirectoryInfo? modsDirectory = modInfo.Parent;
            if (modsDirectory != null && modsDirectory.Name.Equals("mods", StringComparison.OrdinalIgnoreCase) && modsDirectory.Parent != null)
            {
                return modsDirectory.Parent.FullName;
            }
        }
        catch
        {
            // Fall back below.
        }

        return Path.GetFullPath(Path.Combine(modDirectory, "..", ".."));
    }
}
