#if SPT_CLIENT
using System;
using System.Collections.Generic;
using DrakiaXYZ.BigBrain.Brains;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Provides Return Movement Big Brain Bootstrap support for the movement/cohesion runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Movement.Brain;

internal static class VanguardReturnMovementBigBrainBootstrap
{
    private static bool registered;

    public static void Register()
    {
        if (registered)
        {
            return;
        }

        var brains = new List<string>
        {
            "PMC",
            "PmcUsec",
            "PmcBear",
            "ExUsec",
            "ArenaFighter",
            "assault",
            "bossKilla",
            "bossKnight",
            "followerBigPipe",
            "followerBirdEye",
            "followerBully",
            "followerGluharAssault",
            "followerGluharScout",
            "followerGluharSecurity",
            "followerGluharSnipe"
        };

        BrainManager.AddCustomLayer(typeof(VanguardReturnMovementLayer), brains, VanguardReturnMovementLayer.LayerPriority);
        registered = true;
        VanguardClientDiagnosticsLog.Diagnostic(VanguardReturnMovementCommandStore.StatusTag,
            () => $"VANGUARD_MOVE_BRIDGE_LAYER_REGISTERED layer={nameof(VanguardReturnMovementLayer)}; logic={nameof(VanguardReturnMovementLogic)}; priority={VanguardReturnMovementLayer.LayerPriority}; brains={string.Join(",", brains)}; backend=terminal_GoToSomePoint_or_continuous_direct_GoToPoint_slowAtEnd_false; continuousTag={VanguardContinuousCohesionLocomotionPolicy.StatusTag}; tag={VanguardReturnMovementCommandStore.StatusTag}; goToSomePointTag={VanguardReturnMovementCommandStore.GoToSomePointStatusTag}");
    }

    public static void RegisterSafe()
    {
        try
        {
            Register();
        }
        catch (Exception ex)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardReturnMovementCommandStore.StatusTag,
                $"VANGUARD_MOVE_BRIDGE_LAYER_REGISTER_FAILED reason={ex.GetType().Name}; message={Safe(ex.Message)}; tag={VanguardReturnMovementCommandStore.StatusTag}; goToSomePointTag={VanguardReturnMovementCommandStore.GoToSomePointStatusTag}");
        }
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }
}
#endif
