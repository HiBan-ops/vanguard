using System.Reflection;
using SPTarkov.Server.Core.Models.Utils;

// Responsibility: Produces bounded diagnostics/telemetry for Server Diagnostics Log in the server diagnostics.
// Flow: Runtime facts are normalized, deduplicated/rate-gated where needed, then emitted according to Vanguard presentation levels.
// Authority boundary: Observation only; telemetry never changes the gameplay decision it reports.
// Invariant: Operational output stays actionable and repetitive detail remains restricted to diagnostic/trace levels.
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
