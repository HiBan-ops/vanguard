using System;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;

#if SPT_CLIENT
using Comfort.Common;
using EFT;
#endif

// Responsibility: Applies the qualified optional Headless memory mitigation that keeps the managed garbage collector enabled during a Fika Headless raid when the user enables the F12 troubleshooting option.
// Flow: On the real Headless process, each lifecycle tick compares the synchronized raid-scoped setting with GameWorld/quiescence state; when enabled it restores GCEnabled=true without adding a forced-collection timer, then returns to Fika native policy on disable/end-of-raid.
// Authority boundary: The player raid authority owns the option; this service only applies that synchronized policy on the actual Headless process and does not change Fika RAM-clean scheduling.
// Invariant: The option remains OFF by default, never forces GC.Collect(), is active only during a live raid, and restores native Fika behavior when the mitigation is not requested.
namespace Vanguard.Client.Raid.Services;

/// <summary>
/// Qualified optional dedicated-Headless GC memory mitigation. Fika Headless 1.4.15 disables managed GC
/// after its pre-raid cleanup; this service can keep it enabled while a raid GameWorld exists
/// without introducing a second forced-collection timer. The setting is raid-scoped and comes
/// from the player raid authority through Vanguard F12 synchronization.
/// </summary>
internal static class VanguardHeadlessGcPolicyService
{
    public const string StatusTag = "VANGUARD_HEADLESS_GC_POLICY";

#if SPT_CLIENT
    private static bool vanguardEnabledGc;
    private static bool? lastDesiredState;
#endif

    public static void Tick()
    {
#if SPT_CLIENT
        if (!VanguardFikaCompat.IsActualHeadlessProcess)
        {
            return;
        }

        bool desired = VanguardOperatorRuntimeAuditOptions.GetHeadlessKeepGcEnabledInRaid();
        bool raidRuntimeActive = IsRaidRuntimeActive();
        bool postRaidQuiescent = VanguardHeadlessPostRaidQuiescenceService.IsActive;
        bool shouldKeepGcEnabled = desired && raidRuntimeActive && !postRaidQuiescent;

        if (!shouldKeepGcEnabled)
        {
            if (vanguardEnabledGc)
            {
                string reason = postRaidQuiescent
                    ? "post_raid_quiescence"
                    : !raidRuntimeActive
                        ? "raid_runtime_inactive"
                        : "f12_disabled";
                SetGcEnabled(false, reason);
            }

            lastDesiredState = desired;
            return;
        }

        if (!MemoryControllerClass.GCEnabled)
        {
            SetGcEnabled(true, "f12_raid_policy");
        }
        else if (lastDesiredState != true)
        {
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                "VANGUARD_HEADLESS_GC_POLICY desired=true; raidRuntimeActive=true; gcEnabled=true; action=already_enabled; forcedCollect=false; source=raid_scoped_f12");
        }

        lastDesiredState = true;
#endif
    }

    public static void ResetForRaidLifecycle(string reason)
    {
#if SPT_CLIENT
        if (VanguardFikaCompat.IsActualHeadlessProcess && vanguardEnabledGc)
        {
            SetGcEnabled(false, "raid_lifecycle_reset");
        }

        vanguardEnabledGc = false;
        lastDesiredState = null;
        VanguardClientDiagnosticsLog.Diagnostic(
            StatusTag,
            () => $"VANGUARD_HEADLESS_GC_POLICY_RESET reason={Safe(reason)}; mutation=false");
#endif
    }

#if SPT_CLIENT
    private static bool IsRaidRuntimeActive()
    {
        try
        {
            GameWorld? gameWorld = Singleton<GameWorld>.Instance;
            return gameWorld != null;
        }
        catch
        {
            return false;
        }
    }

    private static void SetGcEnabled(bool enabled, string reason)
    {
        try
        {
            MemoryControllerClass.GCEnabled = enabled;
            vanguardEnabledGc = enabled;
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"VANGUARD_HEADLESS_GC_POLICY desired={enabled}; raidRuntimeActive={IsRaidRuntimeActive()}; gcEnabled={MemoryControllerClass.GCEnabled}; action={(enabled ? "enable" : "restore_fika_disabled")}; forcedCollect=false; reason={Safe(reason)}; source=raid_scoped_f12");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"VANGUARD_HEADLESS_GC_POLICY_FAILED desired={enabled}; type={Safe(exception.GetType().Name)}; message={Safe(exception.Message)}; failOpenFikaPolicy=true");
        }
    }
#endif

    private static string Safe(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(';', '_').Replace('\n', ' ').Replace('\r', ' ');
}
