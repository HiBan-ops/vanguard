#if SPT_CLIENT
using System;
using EFT;
using UnityEngine;

// Responsibility: Captures durable authored stationary-presentation truth from the player for HOLD Slots and Tactical Coordination PoseSlots.
// Flow: New slot creation and explicit recapture sample EFT prone state, continuous pose level, lean/tilt, shoulder stance, patrol/weapon presentation and exact ADS state into an additive backward-compatible contract.
// Authority boundary: Capture only records author intent; it never moves a player or Operator, never mutates SAIN, and never changes runtime movement/combat authority.
// Invariant: Missing, marker-only, pose-only and aiming-capable snapshots remain readable; full presentation adds stationary posture without recreating SlotId or PoseSlotId.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal static class VanguardTacticalAuthoringPhysicalPoseCapture
{
    public const int MinimumPhysicalPoseContractVersion = 2;
    public const int AimStateContractVersion = 3;
    public const int FullStationaryPresentationContractVersion = 4;
    public const int CurrentCaptureContractVersion = FullStationaryPresentationContractVersion;

    public static VanguardTacticalAuthoringPhysicalPoseSnapshot Capture(Player player)
    {
        bool isProne = player.IsInPronePose;
        float poseLevel = Mathf.Clamp01(player.PoseLevel);
        string poseKind;
        try
        {
            poseKind = player.Pose.ToString().ToLowerInvariant();
        }
        catch
        {
            poseKind = isProne ? "prone" : poseLevel <= 0.11f ? "duck" : "stand";
        }

        bool isAimingDownSights = false;
        try
        {
            isAimingDownSights = player.ProceduralWeaponAnimation?.IsAiming == true;
        }
        catch
        {
            try
            {
                isAimingDownSights = player.HandsController is Player.FirearmController firearmController
                    && firearmController.IsAiming;
            }
            catch
            {
                isAimingDownSights = false;
            }
        }

        float tilt = 0f;
        bool isLeftStance = false;
        bool isInPatrol = false;
        try
        {
            var movement = player.MovementContext;
            if (movement != null)
            {
                tilt = Mathf.Clamp(movement.Tilt, -5f, 5f);
                isLeftStance = movement.LeftStanceController?.LeftStance == true;
                isInPatrol = movement.IsInPatrol;
            }
        }
        catch
        {
            tilt = 0f;
            isLeftStance = false;
            isInPatrol = false;
        }

        return new VanguardTacticalAuthoringPhysicalPoseSnapshot
        {
            CaptureContractVersion = CurrentCaptureContractVersion,
            PoseKind = NormalizePoseKind(poseKind, isProne, poseLevel),
            IsProne = isProne,
            PoseLevel = poseLevel,
            IsAimingDownSights = isAimingDownSights,
            Tilt = tilt,
            IsLeftStance = isLeftStance,
            IsInPatrol = isInPatrol,
            CapturedAt = DateTimeOffset.UtcNow.ToString("O"),
            CapturedWithBuild = VanguardBuildVersion.BuildLabel
        };
    }

    public static bool IsRuntimeUsable(VanguardTacticalAuthoringPhysicalPoseSnapshot? snapshot)
    {
        return snapshot != null
            && snapshot.CaptureContractVersion >= MinimumPhysicalPoseContractVersion
            && !float.IsNaN(snapshot.PoseLevel) && !float.IsInfinity(snapshot.PoseLevel)
            && snapshot.PoseLevel >= 0f
            && snapshot.PoseLevel <= 1f;
    }

    public static bool IsAimStateUsable(VanguardTacticalAuthoringPhysicalPoseSnapshot? snapshot)
        => snapshot != null && snapshot.CaptureContractVersion >= AimStateContractVersion;

    public static bool IsFullStationaryPresentationUsable(VanguardTacticalAuthoringPhysicalPoseSnapshot? snapshot)
    {
        return snapshot != null
            && snapshot.CaptureContractVersion >= FullStationaryPresentationContractVersion
            && !float.IsNaN(snapshot.Tilt) && !float.IsInfinity(snapshot.Tilt)
            && snapshot.Tilt >= -5f && snapshot.Tilt <= 5f;
    }

    public static string Describe(VanguardTacticalAuthoringPhysicalPoseSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            return "UNSET";
        }
        if (!IsRuntimeUsable(snapshot))
        {
            return "LEGACY-v" + snapshot.CaptureContractVersion;
        }

        string pose = snapshot.IsProne
            ? "PRONE"
            : (string.IsNullOrWhiteSpace(snapshot.PoseKind) ? "POSE" : snapshot.PoseKind.ToUpperInvariant())
                + " " + snapshot.PoseLevel.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        string aim = IsAimStateUsable(snapshot)
            ? (snapshot.IsAimingDownSights ? "ADS=ON" : "ADS=OFF")
            : "ADS=UNSET";
        if (!IsFullStationaryPresentationUsable(snapshot))
        {
            return pose + " " + aim + " LEAN/STANCE=UNSET";
        }

        string tilt = snapshot.Tilt.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        string shoulder = snapshot.IsLeftStance ? "LEFT" : "RIGHT";
        string patrol = snapshot.IsInPatrol ? "PATROL=ON" : "PATROL=OFF";
        return pose + " LEAN=" + tilt + " SHOULDER=" + shoulder + " " + patrol + " " + aim;
    }

    private static string NormalizePoseKind(string? value, bool isProne, float poseLevel)
    {
        if (isProne)
        {
            return "prone";
        }
        if (!string.IsNullOrWhiteSpace(value))
        {
            string normalized = value.Trim().ToLowerInvariant();
            if (normalized is "stand" or "duck" or "prone")
            {
                return normalized;
            }
        }
        return poseLevel <= 0.11f ? "duck" : "stand";
    }
}
#endif
