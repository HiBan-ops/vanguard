#if SPT_CLIENT
using BepInEx.Bootstrap;
using Vanguard.Client.Diagnostics;

// Responsibility: detects the optional Menu Overhaul plugin and exposes a narrow ownership boundary to Vanguard's main-menu integration.
// Flow: the UI controller asks this facade whether Menu Overhaul owns the vanilla main-menu geometry; no Menu Overhaul implementation type is referenced or invoked.
// Authority boundary: when Menu Overhaul is present it owns vanilla main-menu button layout, while Vanguard owns only its injected VANGUARD button.
// Invariant: compatibility is soft/fail-open, requires no Menu Overhaul assembly reference, and Menu Overhaul absence preserves Vanguard's native two-column layout.
namespace Vanguard.Client.Compatibility;

internal static class VanguardMenuOverhaulCompat
{
    public const string PluginGuid = "com.moxopixel.menuoverhaul";

    private static bool diagnosticWritten;

    public static bool IsInstalled
    {
        get
        {
            try
            {
                bool installed = Chainloader.PluginInfos.ContainsKey(PluginGuid);
                WriteDiagnosticOnce(installed, installed ? "bepinex_plugin_registered" : "plugin_not_registered");
                return installed;
            }
            catch (Exception exception)
            {
                WriteDiagnosticOnce(false, $"detection_failed_{exception.GetType().Name}");
                return false;
            }
        }
    }

    private static void WriteDiagnosticOnce(bool installed, string reason)
    {
        if (diagnosticWritten)
        {
            return;
        }

        diagnosticWritten = true;
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OffRaidUiStatusTag,
            $"Menu Overhaul compatibility detection; installed={installed}; pluginGuid={PluginGuid}; reason={reason}; vanillaMenuLayoutAuthority={(installed ? "menu_overhaul" : "vanguard")}");
    }
}
#else
// Responsibility: keeps the non-SPT compile target independent from BepInEx while preserving the same compatibility surface.
// Authority boundary: the analysis/build target has no live main-menu owner, therefore the optional integration is reported absent.
namespace Vanguard.Client.Compatibility;

internal static class VanguardMenuOverhaulCompat
{
    public const string PluginGuid = "com.moxopixel.menuoverhaul";
    public static bool IsInstalled => false;
}
#endif
