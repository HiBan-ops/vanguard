#if SPT_CLIENT
using System;
using EFT;
using UnityEngine;

// Responsibility: Compatibility wrapper preserving the Tactical Coordination pose-bridge surface for existing stationary consumers.
// Flow: TC PoseSlot consumers delegate pose/view application to the shared authored stationary-presentation bridge while suppressing ADS unless the owning consumer explicitly opts into it elsewhere.
// Authority boundary: This wrapper never acquires movement/combat authority, never selects a destination and never persists authoring data.
// Invariant: Medical behavior remains unchanged: TC placement may reproduce body pose/view, but authored lean, shoulder/patrol presentation and ADS are not forced during surgery/controller ownership.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal static class VanguardTacticalCoordinationSainPoseBridge
{
    public const string StatusTag = VanguardAuthoredStationaryPresentationBridge.StatusTag;

    public static void Reset(string reason)
        => VanguardAuthoredStationaryPresentationBridge.Reset("tc_pose_bridge:" + reason);

    public static bool TryApply(
        string botProfileId,
        BotOwner botOwner,
        VanguardTacticalCoordinationReadPhysicalPose? physicalPose,
        Vector3 viewDirection,
        string source,
        DateTimeOffset now,
        out bool poseApplied,
        out bool orientationApplied,
        out string result)
    {
        bool applied = VanguardAuthoredStationaryPresentationBridge.TryApplyReadSnapshot(
            botProfileId,
            botOwner,
            physicalPose,
            viewDirection,
            applyLeanState: false,
            applyWeaponPresentationState: false,
            applyAimState: false,
            source,
            now,
            out poseApplied,
            out orientationApplied,
            out _,
            out result);
        return applied;
    }
}
#endif
