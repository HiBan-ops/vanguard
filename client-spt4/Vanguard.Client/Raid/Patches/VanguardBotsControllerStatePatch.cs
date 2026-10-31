using System;
using System.Reflection;
using Vanguard.Client;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Services;

#if SPT_CLIENT
using Vanguard.Client.Runtime.Movement.Diagnostics;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: Bridges EFT/SPT/Fika callbacks into Bots Controller State Patch for the raid lifecycle patch bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.Raid.Patches;

#if SPT_CLIENT
internal sealed class VanguardBotsControllerStatePatch : ModulePatch
{
    public static BotsController? ActiveController { get; private set; }

    internal static int ControllerGeneration => controllerGeneration;

    private static int controllerGeneration;

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(BotsController), "AddActivePLayer")
            ?? throw new InvalidOperationException("BotsController.AddActivePLayer not found for Vanguard raid spawn state patch.");
    }

    [PatchPostfix]
    private static void PatchPostfix(BotsController __instance)
    {
        if (__instance is null)
        {
            return;
        }

        bool isFreshController = !ReferenceEquals(ActiveController, __instance);
        if (isFreshController)
        {
            BotsController? previousController = ActiveController;
            if (previousController != null)
            {
                try
                {
                    previousController.Bots.OnBotRemove -= OnBotRemoved;
                }
                catch
                {
                    // The previous raid controller may already be partially disposed.
                }
            }

            ActiveController = __instance;
            controllerGeneration++;

            // On Fika headless the matchmaker screen patch is not a reliable raid-cycle
            // boundary after the first raid.  A fresh authoritative BotsController is the
            // earliest stable runtime boundary before SpawnAction queues Vanguard Operators.
            // Resetting here prevents stale manifest/spawn latches from suppressing the
            // next raid spawn while keeping the already validated placement/binding code intact.
            string source = $"bots_controller_new:{controllerGeneration}";
            VanguardTemporalSchedulingProbeService.ObserveAuthorityEpoch(controllerGeneration, source);
            VanguardRaidOperatorController.ResetForNewAuthorityRaidCycle(source);
            __instance.Bots.OnBotRemove -= OnBotRemoved;
            __instance.Bots.OnBotRemove += OnBotRemoved;
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.RaidSpawnStatusTag,
                $"bots controller captured for raid spawn; fresh=True; generation={controllerGeneration}; botRemoveLifecycleSubscription=true");
            return;
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.RaidSpawnStatusTag,
            $"bots controller captured for raid spawn; fresh=False; generation={controllerGeneration}");
    }

    private static void OnBotRemoved(BotOwner botOwner)
    {
        try
        {
            VanguardRaidOperatorPresenceLifecycleService.ObserveExactRuntimePresenceLost(
                botOwner,
                "bots_controller_on_remove:generation=" + controllerGeneration);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardRaidOperatorPresenceLifecycleService.StatusTag,
                $"VANGUARD_OPERATOR_RUNTIME_PRESENCE_REMOVE_CALLBACK_FAILED generation={controllerGeneration}; reason={exception.GetType().Name}:{exception.Message}; failOpenToEftRemoval=true");
        }
    }
}

// Vanilla BotLeaveData.RemoveFromMap deactivates then disposes the BotOwner without going through
// Bots.OnBotRemove. Observe it before Deactivate so Vanguard can drain exact physical ownership
// while the runtime object is still fully active. The later Dispose prefix is an idempotent fallback.
internal sealed class VanguardBotLeaveDataRemoveFromMapPresencePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(global::BotLeaveData), "RemoveFromMap")
            ?? throw new InvalidOperationException("BotLeaveData.RemoveFromMap not found for Vanguard runtime presence lifecycle patch.");
    }

    [PatchPrefix]
    private static void PatchPrefix(global::BotLeaveData __instance)
    {
        BotOwner? botOwner = __instance?.BotOwner_0;
        if (botOwner == null)
        {
            return;
        }

        try
        {
            VanguardRaidOperatorPresenceLifecycleService.ObserveExactRuntimePresenceLost(
                botOwner,
                "bot_leave_remove_from_map_prefix:generation=" + VanguardBotsControllerStatePatch.ControllerGeneration);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardRaidOperatorPresenceLifecycleService.StatusTag,
                $"VANGUARD_OPERATOR_RUNTIME_PRESENCE_REMOVE_FROM_MAP_CALLBACK_FAILED generation={VanguardBotsControllerStatePatch.ControllerGeneration}; reason={exception.GetType().Name}:{exception.Message}; failOpenToEftRemoval=true");
        }
    }
}


// BotOwner.Dispose is the final exact-object fallback for removal paths that bypass Bots.OnBotRemove
// and BotLeaveData.RemoveFromMap (for example third-party extraction code). If an earlier boundary
// already sealed the incarnation, exact-object lookup makes this prefix a no-op.
internal sealed class VanguardBotOwnerDisposePresencePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(BotOwner).GetMethod(nameof(BotOwner.Dispose), BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("BotOwner.Dispose not found for Vanguard runtime presence lifecycle fallback patch.");
    }

    [PatchPrefix]
    private static void PatchPrefix(BotOwner __instance)
    {
        if (__instance == null)
        {
            return;
        }

        try
        {
            VanguardRaidOperatorPresenceLifecycleService.ObserveExactRuntimePresenceLost(
                __instance,
                "bot_owner_dispose_prefix:generation=" + VanguardBotsControllerStatePatch.ControllerGeneration);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardRaidOperatorPresenceLifecycleService.StatusTag,
                $"VANGUARD_OPERATOR_RUNTIME_PRESENCE_DISPOSE_CALLBACK_FAILED generation={VanguardBotsControllerStatePatch.ControllerGeneration}; reason={exception.GetType().Name}:{exception.Message}; failOpenToEftDispose=true");
        }
    }
}

#else
internal sealed class VanguardBotsControllerStatePatch
{
    public void Enable() { }
}
#endif
