#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;
using EFT;
using UnityEngine;
using Vanguard.Client.Diagnostics;

// Responsibility: Reconciles complete authored stationary presentation for HOLD Slots and TC PoseSlots through bounded EFT/SAIN surfaces after another subsystem already owns the stationary placement.
// Flow: The bridge reasserts full 3D gaze plus the backward-compatible physical-pose contract; full presentation includes exact lean, shoulder side and patrol/weapon state while aim intent remains separately preserved.
// Authority boundary: It never selects destinations, opens movement/combat leases, fires, reloads or persists authoring data. While a caller already owns a valid stationary slot window, a heartbeat-bound presentation override temporarily suppresses only SAIN pose/lean/stance writers for that same bot; it expires fail-open as soon as the slot owner stops refreshing it.
// Invariant: Success requires the requested state to be observed in EFT. Missing or marker-only contracts fail open; pose, aiming and complete-presentation contracts retain their respective supported capabilities.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal static class VanguardAuthoredStationaryPresentationBridge
{
    public const string StatusTag = "VANGUARD_AUTHORED_STATIONARY_PRESENTATION_BRIDGE_STATUS";

    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(0.08d);
    private static readonly TimeSpan DriftDebounce = TimeSpan.FromSeconds(0.22d);
    private static readonly TimeSpan RepairCooldown = TimeSpan.FromSeconds(0.45d);
    private static readonly TimeSpan LatchStaleAfter = TimeSpan.FromSeconds(1.25d);
    private static readonly TimeSpan LeanRefreshInterval = TimeSpan.FromSeconds(0.22d);
    private const float PoseTolerance = 0.06f;
    private const float TiltTolerance = 0.30f;
    private const float ViewDotThreshold = 0.985f; // ~10 degrees, including authored pitch.
    private const float LeanHoldSeconds = 0.45f;
    private static readonly TimeSpan SainChannelOverrideHeartbeat = TimeSpan.FromSeconds(0.35d);

    private static readonly Dictionary<string, PresentationLatchState> LatchByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<object, SainChannelOverrideState> SainChannelOverrideByBotOwner = new();
    private static readonly Dictionary<string, string> LastLoggedStateByBot = new(StringComparer.OrdinalIgnoreCase);
    private static Type? sainBotComponentType;
    private static bool sainTypeResolved;

    public static void Reset(string reason)
    {
        LatchByBot.Clear();
        LastLoggedStateByBot.Clear();
        lock (SainChannelOverrideByBotOwner)
        {
            SainChannelOverrideByBotOwner.Clear();
        }
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_AUTHORED_STATIONARY_PRESENTATION_RESET reason={Safe(reason)}; movementAuthority=false; destinationAuthority=false; persistenceMutation=false; tag={StatusTag}");
    }

    public static string ForgetRuntimePresence(string? botProfileId, BotOwner? exactBotOwner, string reason)
    {
        string key = string.IsNullOrWhiteSpace(botProfileId) ? string.Empty : botProfileId.Trim();
        bool latchRemoved = key.Length > 0 && LatchByBot.Remove(key);
        bool logStateRemoved = key.Length > 0 && LastLoggedStateByBot.Remove(key);
        bool sainOverrideRemoved = false;
        if (exactBotOwner != null)
        {
            lock (SainChannelOverrideByBotOwner)
            {
                object? exactKey = SainChannelOverrideByBotOwner.Keys
                    .FirstOrDefault(candidate => ReferenceEquals(candidate, exactBotOwner));
                if (exactKey != null)
                {
                    sainOverrideRemoved = SainChannelOverrideByBotOwner.Remove(exactKey);
                }
            }
        }

        string summary = "latchRemoved=" + Bool(latchRemoved)
            + ";logStateRemoved=" + Bool(logStateRemoved)
            + ";sainOverrideRemoved=" + Bool(sainOverrideRemoved);
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_AUTHORED_PRESENTATION_RUNTIME_PRESENCE_FORGOTTEN botProfile={Safe(key)}; reason={Safe(reason)}; {summary}; exactBotOwner={Bool(exactBotOwner != null)}; failOpenAfterDetach=true; tag={StatusTag}");
        return summary;
    }

    public static bool TryApplyAuthoringSnapshot(
        string botProfileId,
        BotOwner botOwner,
        VanguardTacticalAuthoringPhysicalPoseSnapshot? snapshot,
        Vector3 viewDirection,
        bool applyLeanState,
        bool applyWeaponPresentationState,
        bool applyAimState,
        string source,
        DateTimeOffset now,
        out bool poseApplied,
        out bool orientationApplied,
        out bool aimApplied,
        out string result)
    {
        bool poseUsable = VanguardTacticalAuthoringPhysicalPoseCapture.IsRuntimeUsable(snapshot);
        bool fullPresentationUsable = VanguardTacticalAuthoringPhysicalPoseCapture.IsFullStationaryPresentationUsable(snapshot);
        bool aimUsable = VanguardTacticalAuthoringPhysicalPoseCapture.IsAimStateUsable(snapshot);
        return TryApplyCore(
            botProfileId,
            botOwner,
            poseUsable,
            snapshot?.PoseKind ?? string.Empty,
            snapshot?.IsProne == true,
            snapshot?.PoseLevel ?? 1f,
            fullPresentationUsable,
            snapshot?.Tilt ?? 0f,
            snapshot?.IsLeftStance == true,
            snapshot?.IsInPatrol == true,
            aimUsable,
            snapshot?.IsAimingDownSights == true,
            viewDirection,
            applyLeanState,
            applyWeaponPresentationState,
            applyAimState,
            source,
            now,
            out poseApplied,
            out orientationApplied,
            out aimApplied,
            out result);
    }

    public static bool TryApplyReadSnapshot(
        string botProfileId,
        BotOwner botOwner,
        VanguardTacticalCoordinationReadPhysicalPose? snapshot,
        Vector3 viewDirection,
        bool applyLeanState,
        bool applyWeaponPresentationState,
        bool applyAimState,
        string source,
        DateTimeOffset now,
        out bool poseApplied,
        out bool orientationApplied,
        out bool aimApplied,
        out string result)
    {
        return TryApplyCore(
            botProfileId,
            botOwner,
            snapshot?.RuntimeUsable == true,
            snapshot?.PoseKind ?? string.Empty,
            snapshot?.IsProne == true,
            snapshot?.PoseLevel ?? 1f,
            snapshot?.FullStationaryPresentationUsable == true,
            snapshot?.Tilt ?? 0f,
            snapshot?.IsLeftStance == true,
            snapshot?.IsInPatrol == true,
            snapshot?.AimStateUsable == true,
            snapshot?.IsAimingDownSights == true,
            viewDirection,
            applyLeanState,
            applyWeaponPresentationState,
            applyAimState,
            source,
            now,
            out poseApplied,
            out orientationApplied,
            out aimApplied,
            out result);
    }

    private static bool TryApplyCore(
        string botProfileId,
        BotOwner botOwner,
        bool poseUsable,
        string poseKind,
        bool isProne,
        float poseLevel,
        bool fullPresentationUsable,
        float tilt,
        bool isLeftStance,
        bool isInPatrol,
        bool aimStateUsable,
        bool isAimingDownSights,
        Vector3 viewDirection,
        bool applyLeanState,
        bool applyWeaponPresentationState,
        bool applyAimState,
        string source,
        DateTimeOffset now,
        out bool poseApplied,
        out bool orientationApplied,
        out bool aimApplied,
        out string result)
    {
        poseApplied = !poseUsable;
        orientationApplied = false;
        aimApplied = !applyAimState || !aimStateUsable;
        result = "not_applied";

        if (botOwner == null || botOwner.IsDead)
        {
            result = "bot_unavailable";
            return false;
        }

        Player? player = botOwner.GetPlayer;
        if (player == null)
        {
            result = "player_unavailable";
            return false;
        }

        Vector3 desiredView = NormalizeDirection(viewDirection);
        if (desiredView.sqrMagnitude <= 0.01f)
        {
            result = "view_direction_invalid";
            return false;
        }

        bool leanRequested = applyLeanState && fullPresentationUsable;
        bool weaponRequested = applyWeaponPresentationState && fullPresentationUsable;
        bool aimRequested = applyAimState && aimStateUsable;
        TouchSainChannelOverride(botOwner, now, poseUsable, leanRequested, weaponRequested);
        float desiredPose = Mathf.Clamp01(poseLevel);
        float desiredTilt = Mathf.Clamp(tilt, -5f, 5f);

        // Native EFT BotMover owns the continuous pose interpolation independently of SAIN.
        // Keep only its target coherent with the authored slot. This is a lightweight intent
        // heartbeat; it does not force the physical pose each frame.
        if (poseUsable && !isProne)
        {
            try
            {
                if (botOwner.Mover != null && Mathf.Abs(botOwner.Mover.TargetPose - desiredPose) > 0.001f)
                {
                    botOwner.Mover.SetPose(desiredPose);
                }
            }
            catch
            {
                // The normal postcondition/repair path remains fail-open.
            }
        }

        string key = string.IsNullOrWhiteSpace(botProfileId) ? "unknown" : botProfileId.Trim();
        string desiredKey = BuildDesiredKey(
            source,
            poseUsable,
            isProne,
            desiredPose,
            leanRequested,
            desiredTilt,
            weaponRequested,
            isLeftStance,
            isInPatrol,
            aimRequested,
            isAimingDownSights,
            desiredView);

        if (!LatchByBot.TryGetValue(key, out var latch)
            || !string.Equals(latch.DesiredKey, desiredKey, StringComparison.Ordinal)
            || (latch.LastObservedAtUtc != DateTimeOffset.MinValue && now - latch.LastObservedAtUtc > LatchStaleAfter))
        {
            latch = new PresentationLatchState { DesiredKey = desiredKey };
            LatchByBot[key] = latch;
        }

        if (latch.LastSampleAtUtc != DateTimeOffset.MinValue && now - latch.LastSampleAtUtc < SampleInterval)
        {
            poseApplied = latch.PoseSatisfied;
            orientationApplied = latch.OrientationSatisfied;
            aimApplied = latch.AimSatisfied;
            result = "mode=throttled:latched=" + Bool(latch.IsLatched) + ":source=" + Safe(source);
            return latch.AllSatisfied;
        }
        latch.LastSampleAtUtc = now;
        latch.LastObservedAtUtc = now;

        string sainResolution = "not_attempted";
        object? sain = TryGetSainComponent(botOwner, out sainResolution);
        object? mover = sain == null ? null : GetMember(sain, "Mover");

        bool leanSatisfied = !leanRequested || Mathf.Abs(player.MovementContext.Tilt - desiredTilt) <= TiltTolerance;
        bool leftStanceSatisfied = !weaponRequested || player.MovementContext.LeftStanceController?.LeftStance == isLeftStance;
        bool patrolSatisfied = !weaponRequested || player.MovementContext.IsInPatrol == isInPatrol;
        poseApplied = !poseUsable || IsPoseSatisfied(botOwner, player, isProne, desiredPose);
        orientationApplied = IsOrientationSatisfied(player, desiredView);
        aimApplied = !aimRequested || (ReadAimState(player, out bool initialAim) && initialAim == isAimingDownSights);

        bool initiallySatisfied = poseApplied
            && orientationApplied
            && leanSatisfied
            && leftStanceSatisfied
            && patrolSatisfied
            && aimApplied;

        bool wasLatched = latch.IsLatched;
        var repairs = new List<string>(6);
        bool proneCommandAccepted = false;
        bool poseCommandAccepted = false;
        bool leanCommandAccepted = false;
        bool orientationCommandAccepted = false;
        bool leftStanceCommandAccepted = false;
        bool patrolCommandAccepted = false;
        bool aimCommandAccepted = false;
        string aimResolution = aimRequested ? "not_attempted" : (applyAimState ? "authored_aim_unset" : "caller_suppressed");

        if (initiallySatisfied)
        {
            latch.IsLatched = true;
            ResetDrift(ref latch.PoseDriftSinceUtc);
            ResetDrift(ref latch.OrientationDriftSinceUtc);
            ResetDrift(ref latch.LeanDriftSinceUtc);
            ResetDrift(ref latch.LeftStanceDriftSinceUtc);
            ResetDrift(ref latch.PatrolDriftSinceUtc);
            ResetDrift(ref latch.AimDriftSinceUtc);

            // Lean is the only authored channel with a short SAIN hold timer. Refresh that semantic
            // hold without touching exact EFT tilt, pose, gaze, stance or ADS that are already satisfied.
            if (leanRequested && Mathf.Abs(desiredTilt) > TiltTolerance && now >= latch.NextLeanRefreshAtUtc)
            {
                object? lean = mover == null ? null : GetMember(mover, "Lean");
                TryInvoke(lean, "HoldLean", out _, LeanHoldSeconds);
                latch.NextLeanRefreshAtUtc = now + LeanRefreshInterval;
            }

            UpdateLatchObserved(latch, poseApplied, orientationApplied, leanSatisfied, leftStanceSatisfied, patrolSatisfied, aimApplied);
            result = BuildResult(
                "monitor",
                source,
                sainResolution,
                poseUsable,
                poseApplied,
                false,
                false,
                orientationApplied,
                false,
                leanRequested,
                desiredTilt,
                leanSatisfied,
                false,
                weaponRequested,
                isLeftStance,
                leftStanceSatisfied,
                false,
                isInPatrol,
                patrolSatisfied,
                false,
                aimRequested,
                aimStateUsable,
                isAimingDownSights,
                aimApplied,
                false,
                aimResolution,
                poseKind,
                desiredPose,
                desiredView,
                repairs);
            LogStateIfChanged(key, source, true, poseApplied, orientationApplied, leanSatisfied, leftStanceSatisfied, patrolSatisfied, aimApplied, result);
            return true;
        }

        bool repairPose = poseUsable && ShouldRepairChannel(!poseApplied, latch.IsLatched, now, ref latch.PoseDriftSinceUtc, ref latch.LastPoseRepairAtUtc);
        bool repairOrientation = ShouldRepairChannel(!orientationApplied, latch.IsLatched, now, ref latch.OrientationDriftSinceUtc, ref latch.LastOrientationRepairAtUtc);
        bool repairLean = leanRequested && ShouldRepairChannel(!leanSatisfied, latch.IsLatched, now, ref latch.LeanDriftSinceUtc, ref latch.LastLeanRepairAtUtc);
        bool repairLeftStance = weaponRequested && ShouldRepairChannel(!leftStanceSatisfied, latch.IsLatched, now, ref latch.LeftStanceDriftSinceUtc, ref latch.LastLeftStanceRepairAtUtc);
        bool repairPatrol = weaponRequested && ShouldRepairChannel(!patrolSatisfied, latch.IsLatched, now, ref latch.PatrolDriftSinceUtc, ref latch.LastPatrolRepairAtUtc);
        bool repairAim = aimRequested && ShouldRepairChannel(!aimApplied, latch.IsLatched, now, ref latch.AimDriftSinceUtc, ref latch.LastAimRepairAtUtc);

        if (repairPose)
        {
            repairs.Add("pose");
            if (mover != null)
            {
                object? prone = GetMember(mover, "Prone");

                // Do not repeatedly call SetProne(false) for a crouched/standing slot.
                // SAIN ProneClass forwards that request to EFT BotLay.IsLay; it is only a
                // transition command and is unnecessary unless the player is actually prone.
                if (isProne && !player.IsInPronePose)
                {
                    proneCommandAccepted = TryInvoke(prone, "SetProne", out _, true);
                }
                else if (!isProne && player.IsInPronePose)
                {
                    proneCommandAccepted = TryInvoke(prone, "SetProne", out _, false);
                }
            }

            try
            {
                if (!isProne)
                {
                    // EFT BotMover.method_18 continuously converges Player.PoseLevel toward
                    // BotMover.TargetPose. Own the native target while the authored stationary
                    // slot owns presentation; otherwise the native mover can undo an exact
                    // SetPoseLevel even when SAIN PoseClass itself is suppressed.
                    if (botOwner.Mover != null)
                    {
                        botOwner.Mover.SetPose(desiredPose);
                        poseCommandAccepted = true;
                    }

                    // Immediate reconciliation is only needed when the observed body pose has
                    // not reached the authored target. The native target above keeps it stable.
                    if (Mathf.Abs(player.PoseLevel - desiredPose) > PoseTolerance)
                    {
                        poseCommandAccepted = player.MovementContext.SetPoseLevel(desiredPose, force: true) || poseCommandAccepted;
                    }
                }
            }
            catch
            {
                // Postcondition remains authoritative.
            }
        }

        object? leanController = mover == null ? null : GetMember(mover, "Lean");
        if (repairLean)
        {
            repairs.Add("lean");
            string leanEnumName = desiredTilt < -TiltTolerance ? "Left" : desiredTilt > TiltTolerance ? "Right" : "None";
            leanCommandAccepted = TryInvokeEnum(leanController, "FastLean", leanEnumName, out _);
            if (Mathf.Abs(desiredTilt) > TiltTolerance)
            {
                TryInvoke(leanController, "HoldLean", out _, LeanHoldSeconds);
                latch.NextLeanRefreshAtUtc = now + LeanRefreshInterval;
            }
            else
            {
                TryInvoke(leanController, "ResetHoldLean", out _);
            }
            try
            {
                player.MovementContext.SetTilt(desiredTilt, force: true);
                leanCommandAccepted = true;
            }
            catch
            {
                // Postcondition remains authoritative.
            }
        }
        else if (leanRequested && leanSatisfied && Mathf.Abs(desiredTilt) > TiltTolerance && now >= latch.NextLeanRefreshAtUtc)
        {
            TryInvoke(leanController, "HoldLean", out _, LeanHoldSeconds);
            latch.NextLeanRefreshAtUtc = now + LeanRefreshInterval;
        }

        if (repairLeftStance)
        {
            repairs.Add("left_stance");
            try
            {
                LeftStanceController? leftStance = player.MovementContext.LeftStanceController;
                if (leftStance != null)
                {
                    leftStance.SetLeftStanceForce(isLeftStance);
                    leftStanceCommandAccepted = true;
                }
            }
            catch
            {
                leftStanceCommandAccepted = false;
            }
        }

        if (repairPatrol)
        {
            repairs.Add("patrol");
            try
            {
                player.MovementContext.SetPatrol(isInPatrol);
                patrolCommandAccepted = true;
            }
            catch
            {
                patrolCommandAccepted = false;
            }
        }

        if (repairOrientation)
        {
            repairs.Add("orientation");
            if (sain != null)
            {
                object? steering = GetMember(sain, "Steering");
                Vector3 lookOrigin = botOwner.WeaponRoot != null ? botOwner.WeaponRoot.position : player.Position + Vector3.up * 1.1f;
                Vector3 lookPoint = lookOrigin + desiredView * 30f;
                if (steering != null)
                {
                    orientationCommandAccepted = TryInvoke(steering, "LookToPoint", out _, lookPoint);
                    if (orientationCommandAccepted)
                    {
                        TryInvoke(steering, "TickPlayerSteering", out _);
                    }
                }

                object? playerComponent = GetMember(sain, "PlayerComponent");
                object? characterController = GetMember(playerComponent, "CharacterController");
                if (playerComponent != null && characterController != null)
                {
                    bool targetAccepted = TryInvoke(characterController, "SetTargetLookDirection", out _, desiredView, botOwner, sain);
                    bool updated = TryInvoke(characterController, "UpdateBotTurnData", out _, Mathf.Max(Time.fixedDeltaTime, 0.02f));
                    bool rotated = TryInvoke(characterController, "RotatePlayer", out _, playerComponent);
                    orientationCommandAccepted = orientationCommandAccepted || targetAccepted || updated || rotated;
                }
            }
            if (!IsOrientationSatisfied(player, desiredView))
            {
                try
                {
                    Vector3 lookOrigin = botOwner.WeaponRoot != null ? botOwner.WeaponRoot.position : player.Position + Vector3.up * 1.1f;
                    if (botOwner.Steering != null)
                    {
                        botOwner.Steering.LookToPoint(lookOrigin + desiredView * 30f);
                        orientationCommandAccepted = true;
                        TryInvoke(botOwner.Steering, "ManualFixedUpdate", out _);
                    }
                }
                catch
                {
                    // Postcondition remains authoritative.
                }
            }
        }

        if (repairAim)
        {
            repairs.Add("aim");
            if (sain != null)
            {
                object? adsController = GetMember(sain, "AimDownSightsController");
                aimCommandAccepted = TryInvoke(adsController, "SetADS", out aimResolution, isAimingDownSights, true);
            }
            if (!ReadAimState(player, out bool currentAim) || currentAim != isAimingDownSights)
            {
                try
                {
                    object? weaponManager = GetMember(botOwner, "WeaponManager");
                    object? shootController = GetMember(weaponManager, "ShootController");
                    aimCommandAccepted = TryInvoke(shootController, "SetAim", out aimResolution, isAimingDownSights) || aimCommandAccepted;
                }
                catch
                {
                    // Postcondition remains authoritative.
                }
            }
        }

        poseApplied = !poseUsable || IsPoseSatisfied(botOwner, player, isProne, desiredPose);
        orientationApplied = IsOrientationSatisfied(player, desiredView);
        leanSatisfied = !leanRequested || Mathf.Abs(player.MovementContext.Tilt - desiredTilt) <= TiltTolerance;
        leftStanceSatisfied = !weaponRequested || player.MovementContext.LeftStanceController?.LeftStance == isLeftStance;
        patrolSatisfied = !weaponRequested || player.MovementContext.IsInPatrol == isInPatrol;
        aimApplied = !aimRequested || (ReadAimState(player, out bool observedAim) && observedAim == isAimingDownSights);

        bool allSatisfied = poseApplied
            && orientationApplied
            && leanSatisfied
            && leftStanceSatisfied
            && patrolSatisfied
            && aimApplied;
        if (allSatisfied)
        {
            latch.IsLatched = true;
            ResetDrift(ref latch.PoseDriftSinceUtc);
            ResetDrift(ref latch.OrientationDriftSinceUtc);
            ResetDrift(ref latch.LeanDriftSinceUtc);
            ResetDrift(ref latch.LeftStanceDriftSinceUtc);
            ResetDrift(ref latch.PatrolDriftSinceUtc);
            ResetDrift(ref latch.AimDriftSinceUtc);
        }

        UpdateLatchObserved(latch, poseApplied, orientationApplied, leanSatisfied, leftStanceSatisfied, patrolSatisfied, aimApplied);
        string mode = repairs.Count > 0 ? (wasLatched ? "repair" : "apply") : "debounce";
        result = BuildResult(
            mode,
            source,
            sainResolution,
            poseUsable,
            poseApplied,
            poseCommandAccepted,
            proneCommandAccepted,
            orientationApplied,
            orientationCommandAccepted,
            leanRequested,
            desiredTilt,
            leanSatisfied,
            leanCommandAccepted,
            weaponRequested,
            isLeftStance,
            leftStanceSatisfied,
            leftStanceCommandAccepted,
            isInPatrol,
            patrolSatisfied,
            patrolCommandAccepted,
            aimRequested,
            aimStateUsable,
            isAimingDownSights,
            aimApplied,
            aimCommandAccepted,
            aimResolution,
            poseKind,
            desiredPose,
            desiredView,
            repairs);
        LogStateIfChanged(key, source, allSatisfied, poseApplied, orientationApplied, leanSatisfied, leftStanceSatisfied, patrolSatisfied, aimApplied, result);
        return allSatisfied;
    }

    private static bool ShouldRepairChannel(
        bool drifting,
        bool latched,
        DateTimeOffset now,
        ref DateTimeOffset driftSinceUtc,
        ref DateTimeOffset lastRepairAtUtc)
    {
        if (!drifting)
        {
            ResetDrift(ref driftSinceUtc);
            return false;
        }

        if (driftSinceUtc == DateTimeOffset.MinValue)
        {
            driftSinceUtc = now;
        }

        if (lastRepairAtUtc != DateTimeOffset.MinValue && now - lastRepairAtUtc < RepairCooldown)
        {
            return false;
        }

        if (latched && now - driftSinceUtc < DriftDebounce)
        {
            return false;
        }

        lastRepairAtUtc = now;
        return true;
    }

    private static void ResetDrift(ref DateTimeOffset value) => value = DateTimeOffset.MinValue;

    private static void UpdateLatchObserved(
        PresentationLatchState latch,
        bool poseSatisfied,
        bool orientationSatisfied,
        bool leanSatisfied,
        bool leftStanceSatisfied,
        bool patrolSatisfied,
        bool aimSatisfied)
    {
        latch.PoseSatisfied = poseSatisfied;
        latch.OrientationSatisfied = orientationSatisfied;
        latch.LeanSatisfied = leanSatisfied;
        latch.LeftStanceSatisfied = leftStanceSatisfied;
        latch.PatrolSatisfied = patrolSatisfied;
        latch.AimSatisfied = aimSatisfied;
        latch.AllSatisfied = poseSatisfied
            && orientationSatisfied
            && leanSatisfied
            && leftStanceSatisfied
            && patrolSatisfied
            && aimSatisfied;
    }

    private static string BuildDesiredKey(
        string source,
        bool poseUsable,
        bool isProne,
        float poseLevel,
        bool leanRequested,
        float tilt,
        bool weaponRequested,
        bool isLeftStance,
        bool isInPatrol,
        bool aimRequested,
        bool isAimingDownSights,
        Vector3 viewDirection)
    {
        return Safe(source)
            + "|p=" + Bool(poseUsable) + ":" + Bool(isProne) + ":" + poseLevel.ToString("0.000", CultureInfo.InvariantCulture)
            + "|l=" + Bool(leanRequested) + ":" + tilt.ToString("0.000", CultureInfo.InvariantCulture)
            + "|w=" + Bool(weaponRequested) + ":" + Bool(isLeftStance) + ":" + Bool(isInPatrol)
            + "|a=" + Bool(aimRequested) + ":" + Bool(isAimingDownSights)
            + "|v=" + viewDirection.x.ToString("0.000", CultureInfo.InvariantCulture)
            + "," + viewDirection.y.ToString("0.000", CultureInfo.InvariantCulture)
            + "," + viewDirection.z.ToString("0.000", CultureInfo.InvariantCulture);
    }

    private static string BuildResult(
        string mode,
        string source,
        string sainResolution,
        bool poseRequested,
        bool poseSatisfied,
        bool poseCommand,
        bool proneCommand,
        bool orientationSatisfied,
        bool orientationCommand,
        bool leanRequested,
        float leanDesired,
        bool leanSatisfied,
        bool leanCommand,
        bool weaponRequested,
        bool leftStanceDesired,
        bool leftStanceSatisfied,
        bool leftStanceCommand,
        bool patrolDesired,
        bool patrolSatisfied,
        bool patrolCommand,
        bool aimRequested,
        bool aimStateUsable,
        bool aimDesired,
        bool aimSatisfied,
        bool aimCommand,
        string aimResolution,
        string poseKind,
        float poseLevel,
        Vector3 desiredView,
        IReadOnlyList<string> repairs)
    {
        return "mode=" + Safe(mode)
            + ":repairs=" + (repairs.Count == 0 ? "none" : string.Join(",", repairs.Select(Safe)))
            + ":sain=" + Safe(sainResolution)
            + ":poseRequested=" + Bool(poseRequested)
            + ":poseSatisfied=" + Bool(poseSatisfied)
            + ":poseCommand=" + Bool(poseCommand)
            + ":proneCommand=" + Bool(proneCommand)
            + ":orientationSatisfied=" + Bool(orientationSatisfied)
            + ":orientationCommand=" + Bool(orientationCommand)
            + ":leanRequested=" + Bool(leanRequested)
            + ":leanDesired=" + leanDesired.ToString("0.00", CultureInfo.InvariantCulture)
            + ":leanSatisfied=" + Bool(leanSatisfied)
            + ":leanCommand=" + Bool(leanCommand)
            + ":leftStanceRequested=" + Bool(weaponRequested)
            + ":leftStanceDesired=" + Bool(leftStanceDesired)
            + ":leftStanceSatisfied=" + Bool(leftStanceSatisfied)
            + ":leftStanceCommand=" + Bool(leftStanceCommand)
            + ":patrolRequested=" + Bool(weaponRequested)
            + ":patrolDesired=" + Bool(patrolDesired)
            + ":patrolSatisfied=" + Bool(patrolSatisfied)
            + ":patrolCommand=" + Bool(patrolCommand)
            + ":aimRequested=" + Bool(aimRequested)
            + ":aimDesired=" + (aimStateUsable ? Bool(aimDesired) : "unset")
            + ":aimSatisfied=" + Bool(aimSatisfied)
            + ":aimCommand=" + Bool(aimCommand)
            + ":aimResolution=" + Safe(aimResolution)
            + ":poseKind=" + Safe(poseKind)
            + ":poseLevel=" + poseLevel.ToString("0.00", CultureInfo.InvariantCulture)
            + ":view=(" + desiredView.x.ToString("0.00", CultureInfo.InvariantCulture)
            + "," + desiredView.y.ToString("0.00", CultureInfo.InvariantCulture)
            + "," + desiredView.z.ToString("0.00", CultureInfo.InvariantCulture) + ")"
            + ":source=" + Safe(source);
    }

    private static void LogStateIfChanged(
        string key,
        string source,
        bool allSatisfied,
        bool poseSatisfied,
        bool orientationSatisfied,
        bool leanSatisfied,
        bool leftStanceSatisfied,
        bool patrolSatisfied,
        bool aimSatisfied,
        string result)
    {
        string logStateKey = Safe(source) + ":" + (allSatisfied ? "satisfied" : "pending")
            + ":p" + Bool(poseSatisfied)
            + ":o" + Bool(orientationSatisfied)
            + ":l" + Bool(leanSatisfied)
            + ":s" + Bool(leftStanceSatisfied)
            + ":w" + Bool(patrolSatisfied)
            + ":a" + Bool(aimSatisfied);
        if (!LastLoggedStateByBot.TryGetValue(key, out string? priorState) || !string.Equals(priorState, logStateKey, StringComparison.Ordinal))
        {
            LastLoggedStateByBot[key] = logStateKey;
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"VANGUARD_AUTHORED_STATIONARY_PRESENTATION_{(allSatisfied ? "SATISFIED" : "PENDING")} botProfile={Safe(key)}; {result}; movementAuthority=false; destinationAuthority=false; combatAuthority=false; persistenceMutation=false; tag={StatusTag}");
        }
    }

    internal static bool ShouldSuppressSainPoseChannel(object sainPoseInstance)
    {
        return TryGetActiveSainChannelOverride(sainPoseInstance, out var state) && state.SuppressPose;
    }

    internal static bool ShouldSuppressSainLeanChannel(object sainLeanInstance)
    {
        return TryGetActiveSainChannelOverride(sainLeanInstance, out var state) && state.SuppressLean;
    }

    internal static bool ShouldSuppressSainStanceChannel(object sainMoverInstance)
    {
        return TryGetActiveSainChannelOverride(sainMoverInstance, out var state) && state.SuppressStance;
    }

    private static void TouchSainChannelOverride(
        BotOwner botOwner,
        DateTimeOffset now,
        bool suppressPose,
        bool suppressLean,
        bool suppressStance)
    {
        lock (SainChannelOverrideByBotOwner)
        {
            SainChannelOverrideByBotOwner[botOwner] = new SainChannelOverrideState
            {
                ExpiresAtUtc = now + SainChannelOverrideHeartbeat,
                SuppressPose = suppressPose,
                SuppressLean = suppressLean,
                SuppressStance = suppressStance
            };
        }
    }

    private static bool TryGetActiveSainChannelOverride(object sainInstance, out SainChannelOverrideState state)
    {
        state = default;
        object? botOwner = GetMember(sainInstance, "BotOwner");
        if (botOwner == null)
        {
            object? bot = GetMember(sainInstance, "Bot");
            botOwner = GetMember(bot, "BotOwner");
        }
        if (botOwner == null)
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (SainChannelOverrideByBotOwner)
        {
            if (!SainChannelOverrideByBotOwner.TryGetValue(botOwner, out state))
            {
                return false;
            }
            if (state.ExpiresAtUtc <= now)
            {
                SainChannelOverrideByBotOwner.Remove(botOwner);
                state = default;
                return false;
            }
            return true;
        }
    }

    private struct SainChannelOverrideState
    {
        public DateTimeOffset ExpiresAtUtc;
        public bool SuppressPose;
        public bool SuppressLean;
        public bool SuppressStance;
    }

    private sealed class PresentationLatchState
    {
        public string DesiredKey = string.Empty;
        public bool IsLatched;
        public bool AllSatisfied;
        public bool PoseSatisfied;
        public bool OrientationSatisfied;
        public bool LeanSatisfied;
        public bool LeftStanceSatisfied;
        public bool PatrolSatisfied;
        public bool AimSatisfied;
        public DateTimeOffset LastSampleAtUtc;
        public DateTimeOffset LastObservedAtUtc;
        public DateTimeOffset NextLeanRefreshAtUtc;
        public DateTimeOffset PoseDriftSinceUtc;
        public DateTimeOffset OrientationDriftSinceUtc;
        public DateTimeOffset LeanDriftSinceUtc;
        public DateTimeOffset LeftStanceDriftSinceUtc;
        public DateTimeOffset PatrolDriftSinceUtc;
        public DateTimeOffset AimDriftSinceUtc;
        public DateTimeOffset LastPoseRepairAtUtc;
        public DateTimeOffset LastOrientationRepairAtUtc;
        public DateTimeOffset LastLeanRepairAtUtc;
        public DateTimeOffset LastLeftStanceRepairAtUtc;
        public DateTimeOffset LastPatrolRepairAtUtc;
        public DateTimeOffset LastAimRepairAtUtc;
    }

    private static bool IsPoseSatisfied(BotOwner botOwner, Player player, bool desiredProne, float desiredPoseLevel)
    {
        if (player.IsInPronePose != desiredProne)
        {
            return false;
        }
        if (desiredProne)
        {
            return true;
        }

        float desired = Mathf.Clamp01(desiredPoseLevel);
        try
        {
            if (botOwner.Mover == null || Mathf.Abs(botOwner.Mover.TargetPose - desired) > PoseTolerance)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        return Mathf.Abs(player.PoseLevel - desired) <= PoseTolerance;
    }

    private static bool IsOrientationSatisfied(Player player, Vector3 desiredView)
    {
        Vector3 observed;
        try
        {
            observed = NormalizeDirection(player.LookDirection);
        }
        catch
        {
            return false;
        }
        return observed.sqrMagnitude > 0.01f && Vector3.Dot(observed, desiredView) >= ViewDotThreshold;
    }

    private static bool ReadAimState(Player player, out bool isAiming)
    {
        isAiming = false;
        try
        {
            if (player.HandsController is Player.FirearmController firearmController)
            {
                isAiming = firearmController.IsAiming;
                return true;
            }
        }
        catch
        {
            return false;
        }
        // No firearm controller means the Operator is observably not ADS. This satisfies an authored ADS=OFF state.
        isAiming = false;
        return true;
    }

    private static Vector3 NormalizeDirection(Vector3 value)
    {
        if (float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z)
            || float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z)
            || value.sqrMagnitude <= 0.0001f)
        {
            return Vector3.zero;
        }
        return value.normalized;
    }

    private static object? TryGetSainComponent(BotOwner botOwner, out string result)
    {
        Type? type = ResolveSainBotComponentType();
        if (type == null)
        {
            result = "type_missing";
            return null;
        }

        try
        {
            object? direct = ((object)botOwner is Component ownerComponent) ? ownerComponent.gameObject.GetComponent(type) : null;
            if (direct != null)
            {
                result = "botowner_component";
                return direct;
            }

            Player? player = botOwner.GetPlayer;
            object? fromPlayer = player?.GetComponent(type);
            if (fromPlayer != null)
            {
                result = "player_component";
                return fromPlayer;
            }
        }
        catch (Exception ex)
        {
            result = "component_exception:" + ex.GetType().Name;
            return null;
        }

        result = "component_missing";
        return null;
    }

    private static Type? ResolveSainBotComponentType()
    {
        if (sainTypeResolved)
        {
            return sainBotComponentType;
        }
        sainTypeResolved = true;

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                sainBotComponentType = assembly.GetType("SAIN.Components.BotComponent", throwOnError: false, ignoreCase: false)
                    ?? assembly.GetType("SAIN.SAINComponent.BotComponent", throwOnError: false, ignoreCase: false);
                if (sainBotComponentType != null)
                {
                    break;
                }
            }
            catch
            {
                // Optional integration surface: assembly inspection must never be fatal.
            }
        }
        return sainBotComponentType;
    }

    private static object? GetMember(object? target, string name)
    {
        if (target == null)
        {
            return null;
        }
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = target.GetType();
            PropertyInfo? property = type.GetProperty(name, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(target);
            }
            FieldInfo? field = type.GetField(name, flags);
            return field?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryInvokeEnum(object? target, string name, string enumName, out string result)
    {
        result = "target_missing";
        if (target == null)
        {
            return false;
        }
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (MethodInfo method in target.GetType().GetMethods(flags).Where(candidate => candidate.Name == name))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1 || !parameters[0].ParameterType.IsEnum)
                {
                    continue;
                }
                object value = Enum.Parse(parameters[0].ParameterType, enumName, ignoreCase: true);
                object? returned = method.Invoke(target, new[] { value });
                if (returned is bool accepted && !accepted)
                {
                    result = "bool:false";
                    return false;
                }
                result = returned == null ? "void:true" : returned.GetType().Name + ":" + Safe(returned.ToString());
                return true;
            }
            result = "method_missing";
            return false;
        }
        catch (Exception ex)
        {
            result = "invoke_exception:" + ex.GetType().Name;
            return false;
        }
    }

    private static bool TryInvoke(object? target, string name, out string result, params object?[] args)
    {
        result = "target_missing";
        if (target == null)
        {
            return false;
        }
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (MethodInfo method in target.GetType().GetMethods(flags).Where(candidate => candidate.Name == name))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != args.Length)
                {
                    continue;
                }
                if (!ArgumentsCompatible(parameters, args))
                {
                    continue;
                }
                object? returned = method.Invoke(target, args);
                if (returned is bool accepted && !accepted)
                {
                    result = "bool:false";
                    return false;
                }
                result = returned == null ? "void:true" : returned.GetType().Name + ":" + Safe(returned.ToString());
                return true;
            }
            result = "method_missing";
            return false;
        }
        catch (Exception ex)
        {
            result = "invoke_exception:" + ex.GetType().Name;
            return false;
        }
    }

    private static bool ArgumentsCompatible(ParameterInfo[] parameters, object?[] args)
    {
        for (int i = 0; i < parameters.Length; i++)
        {
            object? arg = args[i];
            Type parameterType = parameters[i].ParameterType;
            if (arg == null)
            {
                if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) == null)
                {
                    return false;
                }
                continue;
            }
            if (!parameterType.IsInstanceOfType(arg))
            {
                return false;
            }
        }
        return true;
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }

    private static string Bool(bool value) => value ? "true" : "false";
}

/// <summary>
/// While an authored stationary slot owns presentation, suppress SAIN's pose smoother only for that bot.
/// The override is heartbeat-bound and fails open automatically when the slot owner stops refreshing it.
/// </summary>
internal sealed class VanguardSainAuthoredPoseChannelOverridePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type targetType = AccessTools.TypeByName("SAIN.SAINComponent.Classes.Mover.PoseClass")
            ?? throw new InvalidOperationException("SAIN PoseClass not found for authored stationary presentation override.");
        return AccessTools.Method(targetType, "ManualUpdate")
            ?? throw new InvalidOperationException("SAIN PoseClass.ManualUpdate not found for authored stationary presentation override.");
    }

    [PatchPrefix]
    private static bool PatchPrefix(object __instance)
    {
        return !VanguardAuthoredStationaryPresentationBridge.ShouldSuppressSainPoseChannel(__instance);
    }
}

/// <summary>
/// Prevents SAIN LeanClass from continuously steering authored tilt away from the captured HOLD/PoseSlot value.
/// </summary>
internal sealed class VanguardSainAuthoredLeanChannelOverridePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type targetType = AccessTools.TypeByName("SAIN.SAINComponent.Classes.Mover.LeanClass")
            ?? throw new InvalidOperationException("SAIN LeanClass not found for authored stationary presentation override.");
        return AccessTools.Method(targetType, "ManualUpdate")
            ?? throw new InvalidOperationException("SAIN LeanClass.ManualUpdate not found for authored stationary presentation override.");
    }

    [PatchPrefix]
    private static bool PatchPrefix(object __instance)
    {
        return !VanguardAuthoredStationaryPresentationBridge.ShouldSuppressSainLeanChannel(__instance);
    }
}

/// <summary>
/// Freezes SAIN's patrol/left-stance reconciler while the authored slot owns those stable presentation channels.
/// </summary>
internal sealed class VanguardSainAuthoredStanceChannelOverridePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type targetType = AccessTools.TypeByName("SAIN.SAINComponent.Classes.Mover.SAINMoverClass")
            ?? throw new InvalidOperationException("SAIN SAINMoverClass not found for authored stationary presentation override.");
        return AccessTools.Method(targetType, "UpdateStance")
            ?? throw new InvalidOperationException("SAIN SAINMoverClass.UpdateStance not found for authored stationary presentation override.");
    }

    [PatchPrefix]
    private static bool PatchPrefix(object __instance)
    {
        return !VanguardAuthoredStationaryPresentationBridge.ShouldSuppressSainStanceChannel(__instance);
    }
}

#endif
