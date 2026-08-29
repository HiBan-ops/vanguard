using System;
using System.Reflection;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Raid.Services;
using Vanguard.Client.Runtime.Alliance;
using Vanguard.Client.UI.OffRaid.Inventory;

#if SPT_CLIENT
using EFT;
using EFT.UI.Matchmaker;
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: Bridges EFT/SPT/Fika callbacks into Raid Start Patch for the raid lifecycle patch bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.Raid.Patches;

#if SPT_CLIENT
internal sealed class VanguardRaidStartPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(MatchMakerAcceptScreen), "Show", new[] { typeof(ISession), typeof(RaidSettings), typeof(RaidSettings) })
            ?? throw new InvalidOperationException("MatchMakerAcceptScreen.Show(ISession,RaidSettings,RaidSettings) not found for Vanguard raid start patch.");
    }

    [PatchPostfix]
    private static void PatchPostfix(RaidSettings __1)
    {
        try
        {
            if (__1 is null || !__1.IsPmc)
            {
                return;
            }

            // A fresh PMC raid must always rebuild the authority manifest and spawn queue.
            // The runtime registry reset clears Operator records; the controller reset clears
            // static spawn latches that can otherwise survive a previous raid in the same game session.
            // The runtime keeps raid start passive for Operator-inventory cleanup.  The real repair
            // must happen immediately when the direct Operator inventory closes, after commit
            // and inventory-mode exit, by reloading the player menu/profile state off-raid.
            if (VanguardOperatorInventoryExitReloadState.IsDirty)
            {
                VanguardClientDiagnosticsLog.Warning(
                    VanguardBuildVersion.OperatorInventoryExitReloadStatusTag,
                    $"raid_start_with_dirty_operator_inventory_exit source=raid_start_pmc; {VanguardOperatorInventoryExitReloadState.Describe()}");
            }
            else if (VanguardOperatorInventoryExitReloadState.TryConsumePendingBattleInputReleaseForRaid("raid_start_pmc", out string releaseReason))
            {
                VanguardBattleInputNodeReleaseService.RequestForRaidStart("raid_start_pmc", releaseReason);
            }

            VanguardOperatorInventoryModeClientState.ForceClearForRaidStart("raid_start_pmc");
            VanguardRaidOperatorController.ResetForRaidStart("raid_start_pmc");
            VanguardRaidOperatorRuntimeRegistry.Reset("raid_start_pmc");
            VanguardFriendlyIdentityRegistry.Reset("raid_start_pmc");
            VanguardRaidOperatorController.PrimeFromRaidPlayers("raid_start");
            VanguardFriendlyIdentityRegistry.RefreshNow("raid_start_after_prime");
            VanguardRaidOperatorController.QueueSpawn("raid_start");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_RAID_SPAWN_STATUS", $"raid start patch failed: {exception.GetType().Name}: {exception.Message}");
        }
    }
}
#else
internal sealed class VanguardRaidStartPatch
{
    public void Enable() { }
}
#endif
