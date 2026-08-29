using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using Vanguard.Server.Diagnostics;
using Vanguard.Server.Operators.Inventory.Patches;
using Vanguard.Server.Operators.Inventory.Services;
using Vanguard.Server.Operators.Services;
using Vanguard.Server.Operators.Storage;

// Responsibility: Bootstraps Server Module for the server bootstrap.
// Flow: The host loader registers dependencies/patches/services once, then hands ongoing behavior to dedicated runtime/domain components.
// Authority boundary: Bootstrap owns registration only; it must not duplicate the runtime or persistence authorities it wires together.
// Invariant: Initialization is repeat-safe for the supported lifecycle and failures remain visible without partially inventing runtime state.
namespace Vanguard.Server.Startup;

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public sealed class VanguardServerModule(
    ISptLogger<VanguardServerModule> logger,
    VanguardOperatorStore operatorStore,
    VanguardOperatorStateService operatorStateService,
    VanguardOperatorInventoryModeService inventoryModeService,
    VanguardEftExperienceCurveService experienceCurveService)
    : IOnLoad
{
    public async Task OnLoad()
    {
        VanguardServerDiagnosticsLog.Startup(
            logger,
            $"Vanguard server loaded; version={VanguardBuildVersion.Value}");

        bool eftExperienceCurveResolved = experienceCurveService.EnsureResolved();

        try
        {
            operatorStore.EnsureStorageRootExists();
            VanguardOperatorInventoryServerPatches.Enable(logger, inventoryModeService);
            var diagnostics = operatorStateService.GetStorageDiagnostics();

            VanguardServerDiagnosticsLog.Info(
                logger,
                "VANGUARD_STARTUP",
                $"Server ready; persistence=OK; knownProfiles={diagnostics.KnownProfileCount}; inventory=OK; xpCurve={(eftExperienceCurveResolved ? "OK" : "FALLBACK")}; diagnostics=OK");
        }
        catch (Exception exception)
        {
            VanguardServerDiagnosticsLog.Error(
                logger,
                "VANGUARD_RUNTIME_ERROR",
                $"Operator persistence bootstrap failed: {exception.GetType().Name}: {exception.Message}");
        }

        await Task.CompletedTask;
    }
}
