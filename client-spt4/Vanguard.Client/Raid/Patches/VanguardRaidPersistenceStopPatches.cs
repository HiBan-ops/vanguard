using System;
using System.Reflection;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Career;
using Vanguard.Client.Raid.Persistence;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Raid.Services;

#if SPT_CLIENT
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: Bridges EFT/SPT/Fika callbacks into Raid Persistence Stop Patches for the raid lifecycle patch bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.Raid.Patches;

#if SPT_CLIENT
internal static class VanguardRaidPersistenceStopBoundaryDiagnostics
{
    public static void Record(string source, string location, ExitStatus exitStatus, string exitName, float delay)
    {
        try
        {
            string runtimeRaid = VanguardRaidOperatorRuntimeRegistry.ActiveRaidSessionId ?? string.Empty;
            int runtimeOperators = VanguardRaidOperatorRuntimeRegistry.GetRuntimeCountForAuthority();
            VanguardClientDiagnosticsLog.Info(
                VanguardRaidOperatorPersistenceService.StatusTag,
                $"VANGUARD_RAID_PERSISTENCE_STOP_BOUNDARY source={source}; runtimeRaid={runtimeRaid}; runtimeOperators={runtimeOperators}; location={location ?? "<none>"}; exitStatus={exitStatus}; exitName={exitName ?? "<none>"}; delay={delay:0.###}; armedForCorpseTransactions={VanguardRaidOperatorPersistenceService.IsArmedForOperatorCorpseTransactions}; boundaryReached=true");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardRaidOperatorPersistenceService.StatusTag,
                $"VANGUARD_RAID_PERSISTENCE_STOP_BOUNDARY_DIAGNOSTIC_FAILED source={source}; type={exception.GetType().Name}; message={exception.Message}; persistenceFlowContinues=true");
        }
    }
}

internal sealed class VanguardRaidPersistenceLocalStopPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(LocalGame), nameof(LocalGame.Stop), new[] { typeof(string), typeof(ExitStatus), typeof(string), typeof(float) })
           ?? throw new InvalidOperationException("EFT.LocalGame.Stop(string,ExitStatus,string,float) not found for Vanguard persistence.");

    [PatchPrefix]
    private static void PatchPrefix(string __0, ExitStatus __1, string __2, float __3)
    {
        if (VanguardRaidOperatorController.IsPostPersistenceRuntimeTeardownApplied)
        {
            return;
        }

        VanguardRaidPersistenceStopBoundaryDiagnostics.Record("eft_localgame_stop_prefix", __0, __1, __2, __3);
        try
        {
            VanguardCareerEventTruthProbeService.ObserveRaidStop("eft_localgame_stop_prefix", __0, __1, __2, __3);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardCareerEventTruthProbeService.StatusTag,
                $"VANGUARD_LOCAL_STOP_PROBE_FAILED type={exception.GetType().Name}; message={exception.Message}; persistenceFailOpen=true");
        }

        try
        {
            VanguardRaidOperatorPersistenceService.CommitAtRaidEnd("eft_localgame_stop_prefix");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardRaidOperatorPersistenceService.StatusTag,
                $"VANGUARD_LOCAL_STOP_PATCH_FAILED type={exception.GetType().Name}; message={exception.Message}; failOpenGameShutdown=true");
        }
        finally
        {
            VanguardRaidOperatorController.ReleaseRuntimeAfterPersistenceAtRaidStop("eft_localgame_stop_prefix");
        }
    }
}

/// <summary>
/// Fika player-host/client game boundary. Fika.Core CoopGame overrides the EFT local-game Stop method,
/// so a dedicated patch is required when Fika.Core owns the active game implementation.
/// </summary>
internal sealed class VanguardRaidPersistenceFikaStopPatch : ModulePatch
{
    private const string RuntimeTypeName = "Fika.Core.Main.GameMode.CoopGame";

    protected override MethodBase GetTargetMethod()
    {
        Type? coopGame = AccessTools.TypeByName(RuntimeTypeName);
        if (coopGame == null)
        {
            throw new InvalidOperationException("Fika CoopGame type not found; optional Fika persistence stop patch disabled.");
        }

        return AccessTools.Method(coopGame, "Stop", new[] { typeof(string), typeof(ExitStatus), typeof(string), typeof(float) })
            ?? throw new InvalidOperationException("Fika CoopGame.Stop(string,ExitStatus,string,float) not found for Vanguard persistence.");
    }

    [PatchPrefix]
    private static void PatchPrefix(string __0, ExitStatus __1, string __2, float __3)
    {
        if (VanguardRaidOperatorController.IsPostPersistenceRuntimeTeardownApplied)
        {
            return;
        }

        VanguardRaidPersistenceStopBoundaryDiagnostics.Record("fika_coopgame_stop_prefix", __0, __1, __2, __3);
        try
        {
            VanguardCareerEventTruthProbeService.ObserveRaidStop("fika_coopgame_stop_prefix", __0, __1, __2, __3);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardCareerEventTruthProbeService.StatusTag,
                $"VANGUARD_FIKA_STOP_PROBE_FAILED type={exception.GetType().Name}; message={exception.Message}; persistenceFailOpen=true");
        }

        try
        {
            VanguardRaidOperatorPersistenceService.CommitAtRaidEnd("fika_coopgame_stop_prefix");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardRaidOperatorPersistenceService.StatusTag,
                $"VANGUARD_FIKA_STOP_PATCH_FAILED type={exception.GetType().Name}; message={exception.Message}; failOpenGameShutdown=true");
        }
        finally
        {
            VanguardRaidOperatorController.ReleaseRuntimeAfterPersistenceAtRaidStop("fika_coopgame_stop_prefix");
        }
    }
}

/// <summary>
/// Fika Headless owns a distinct IFikaGame implementation and does not execute CoopGame.Stop.
/// Patching CoopGame alone covers ordinary Fika hosts but misses the dedicated headless raid-close boundary.
/// The Headless assembly is loaded before Vanguard on the
/// supported stack, so type availability is a safe optional capability probe with no compile-time
/// dependency on Fika.Headless.
/// </summary>
internal sealed class VanguardRaidPersistenceFikaHeadlessStopPatch : ModulePatch
{
    public const string RuntimeTypeName = "Fika.Headless.Classes.GameMode.HeadlessGame";

    public static bool IsRuntimeTypeAvailable => AccessTools.TypeByName(RuntimeTypeName) != null;

    protected override MethodBase GetTargetMethod()
    {
        Type? headlessGame = AccessTools.TypeByName(RuntimeTypeName);
        if (headlessGame == null)
        {
            throw new InvalidOperationException("Fika HeadlessGame type not found; optional Headless persistence stop patch disabled.");
        }

        return AccessTools.Method(headlessGame, "Stop", new[] { typeof(string), typeof(ExitStatus), typeof(string), typeof(float) })
            ?? throw new InvalidOperationException("Fika HeadlessGame.Stop(string,ExitStatus,string,float) not found for Vanguard raid persistence.");
    }

    [PatchPrefix]
    private static void PatchPrefix(string __0, ExitStatus __1, string __2, float __3)
    {
        if (VanguardRaidOperatorController.IsPostPersistenceRuntimeTeardownApplied)
        {
            return;
        }

        VanguardRaidPersistenceStopBoundaryDiagnostics.Record("fika_headless_headlessgame_stop_prefix", __0, __1, __2, __3);
        try
        {
            VanguardCareerEventTruthProbeService.ObserveRaidStop("fika_headless_headlessgame_stop_prefix", __0, __1, __2, __3);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardCareerEventTruthProbeService.StatusTag,
                $"VANGUARD_FIKA_HEADLESS_STOP_PROBE_FAILED type={exception.GetType().Name}; message={exception.Message}; persistenceFailOpen=true");
        }

        try
        {
            // Prefix is intentionally before HeadlessGame.Stop disposes CoopHandler players and clears
            // Fika runtime variables, preserving the final runtime inventory truth consumed by persistence.
            VanguardRaidOperatorPersistenceService.CommitAtRaidEnd("fika_headless_headlessgame_stop_prefix");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardRaidOperatorPersistenceService.StatusTag,
                $"VANGUARD_FIKA_HEADLESS_STOP_PATCH_FAILED type={exception.GetType().Name}; message={exception.Message}; failOpenGameShutdown=true");
        }
        finally
        {
            // Cleanup must run while the old Headless raid objects are still live. Only after exact raid-scoped
            // Vanguard runtime has been sealed do we suppress recurring plugin work through the inter-raid gap.
            VanguardRaidOperatorController.ReleaseRuntimeAfterPersistenceAtRaidStop("fika_headless_headlessgame_stop_prefix");
            VanguardHeadlessPostRaidQuiescenceService.Begin("fika_headless_headlessgame_stop_prefix_after_persistence_attempt");
        }
    }
}
#else
internal sealed class VanguardRaidPersistenceLocalStopPatch { public void Enable() { } }
internal sealed class VanguardRaidPersistenceFikaStopPatch { public void Enable() { } }
internal sealed class VanguardRaidPersistenceFikaHeadlessStopPatch
{
    public static bool IsRuntimeTypeAvailable => false;
    public void Enable() { }
}
#endif
