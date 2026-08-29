#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EFT;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.External;
using Vanguard.Client.Runtime.Loot;
using Vanguard.Client.Runtime.Movement.Brain;

// Responsibility: Owns the short emergency movement window that gets an Operator away from a live grenade while preserving the rest of combat state.
// Flow: A validated grenade hazard opens one high-priority movement lease, a safe detour is selected and monitored, and the Operator holds safety until that exact grenade is gone before normal arbitration resumes.
// Authority boundary: Vanguard owns only the emergency movement lease; EFT owns grenade physics and SAIN keeps target/combat state unless a separate authority explicitly changes it.
// Invariant: One grenade produces one bounded emergency window: no duplicate leases, no premature return to routine behavior, and cleanup occurs when the grenade or lease is no longer valid.
namespace Vanguard.Client.Runtime.Grenades;

/// <summary>
/// grenade subsystem active survival authority. The runtime keeps one exact grenade window until
/// the physical grenade explodes or is destroyed; the runtime adds a deterministic physical movement
/// lease and the runtime closes cleanup and path-valid detour progress. Reaching distance/cover enters
/// a protected safety hold; it never resumes follow/combat/medical while the same grenade remains live.
/// grenade subsystem keeps the exact fallback alive through a bounded mover ignition grace so the priority-97
/// layer can displace an active SAIN chase before any destructive path failure is accepted. grenade subsystem
/// closes the pre-fallback authority gap: active SAIN locomotion bypasses the native probe and receives
/// the exact Vanguard emergency lease immediately while SAIN target and decision state remain intact.
/// </summary>
internal static class VanguardGrenadeEmergencyEvasionService
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, VanguardGrenadeEmergencyOperatorState> ActiveByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset nextTickAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;
    private const int MaximumDestructiveWindowRecoveries = 1;
    private const int MaximumStickySameAnchorBackendResets = 1;
    private static readonly TimeSpan StickyHoldLogInterval = TimeSpan.FromSeconds(1.0d);
    private const string GrenadeWindowConvergenceTag = VanguardBuildVersion.GrenadeWindowConvergenceStatusTag;

    public static void ResetForRaidLifecycle(string reason)
    {
        VanguardGrenadeEmergencyOperatorState[] active;
        lock (Sync)
        {
            active = ActiveByBotProfileId.Values.ToArray();
            ActiveByBotProfileId.Clear();
            nextTickAtUtc = DateTimeOffset.MinValue;
            bootLogged = false;
        }

        foreach (VanguardGrenadeEmergencyOperatorState state in active)
        {
            VanguardReturnMovementCommandStore.ClearOwned(state.BotProfileId, state.WindowId, state.StartedAtUtc, "raid_reset:" + reason);
            VanguardGrenadeEmergencyPhysicalDriver.Release(state.BotProfileId);
        }
        VanguardGrenadeEmergencyPhysicalDriver.ResetForRaidLifecycle();
        VanguardClientDiagnosticsLog.Operational(VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag, () =>
            $"VANGUARD_GRENADE_EMERGENCY_RESET reason={Safe(reason)}; statesCleared={active.Length}; movementCommandsOwnedOnly=true; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}; foundationTag={VanguardGrenadeEmergencyPolicy.StatusTag}");
    }

    public static void Tick(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Diagnostic(VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag, () =>
                $"VANGUARD_GRENADE_EMERGENCY_BOOT enabled=true; eventDriven=true; operatorsOnly=true; sourceIndependentSurvival=true; exactGrenadeContinuousWindow=true; safetyEnvelope=min_actual_and_predicted; coverRequiresBothLines=true; fuseAware=true; contactAndShortFuseFallbackImmediate=true; holdingSafetyUntilPhysicalTerminal=true; windowScopedRecoveryBudget=1; stickySameAnchorBackendResetBudget=1; commandLeaseRefreshedToExactWindow=true; targetPropagation=terminal_hostile_source_only; distantGunshotChase=false; build={VanguardBuildVersion.BuildLabel}; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}; foundationTag={VanguardGrenadeEmergencyPolicy.StatusTag}");
        }

        bool hasStates;
        lock (Sync)
        {
            hasStates = ActiveByBotProfileId.Count > 0;
            if (now < nextTickAtUtc)
            {
                return;
            }
            nextTickAtUtc = now + TimeSpan.FromSeconds(VanguardGrenadeEmergencyPolicy.ServiceTickSeconds);
        }

        if (!hasStates && !VanguardGrenadeHazardRegistry.HasActiveHazards)
        {
            return;
        }

        var byProfile = (snapshots ?? Array.Empty<OperatorDecisionSnapshot>())
            .Where(snapshot => snapshot != null && !string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            .GroupBy(snapshot => snapshot.BotProfileId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(snapshot => snapshot.CapturedAtUtc).First(), StringComparer.OrdinalIgnoreCase);

        foreach (VanguardRaidOperatorRuntimeRecord runtime in VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators())
        {
            try
            {
                if (runtime.BotOwner == null || string.IsNullOrWhiteSpace(runtime.BotProfileId))
                {
                    continue;
                }

                byProfile.TryGetValue(runtime.BotProfileId, out OperatorDecisionSnapshot? snapshot);
                TickOperator(runtime, snapshot, now);
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag,
                    $"VANGUARD_GRENADE_EMERGENCY_TICK_FAILED operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; reason={exception.GetType().Name}:{Safe(exception.Message)}; failOpenToNativeBrain=true; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}");
            }
        }
    }

    private static void TickOperator(VanguardRaidOperatorRuntimeRecord runtime, OperatorDecisionSnapshot? snapshot, DateTimeOffset now)
    {
        BotOwner owner = runtime.BotOwner!;
        VanguardGrenadeEmergencyOperatorState? state = GetState(runtime.BotProfileId);
        if (owner.IsDead || snapshot == null || !snapshot.Alive)
        {
            if (state != null)
            {
                Finish(runtime, snapshot, state, now, VanguardGrenadeEmergencyTerminalKind.OperatorDead, "operator_dead", "Interrupted");
            }
            return;
        }

        Vector3 position = owner.Position;
        VanguardGrenadeHazardDecisionSnapshot bestHazard = VanguardGrenadeHazardRegistry.CaptureDecisionSnapshot(runtime, position, now);
        if (state == null)
        {
            if (bestHazard.HasRelevantHazard && (bestHazard.Critical || bestHazard.Imminent || bestHazard.NativeDangerPresent))
            {
                Admit(runtime, snapshot, bestHazard, now);
            }
            return;
        }

        if (!VanguardMainIntentScheduler.TryGetActiveEmergencyWindow(runtime.BotProfileId, now, out string activeWindow, out _, out _)
            || !string.Equals(activeWindow, state.WindowId, StringComparison.OrdinalIgnoreCase))
        {
            Finish(runtime, snapshot, state, now, VanguardGrenadeEmergencyTerminalKind.WindowLost, "scheduler_window_lost_or_replaced", "Interrupted", finishScheduler: false);
            return;
        }

        if (VanguardGrenadeHazardRegistry.TryGetTerminalReason(state.GrenadeKey, out string terminalReason))
        {
            VanguardGrenadeEmergencyTerminalKind terminal = terminalReason.IndexOf("Exploded", StringComparison.OrdinalIgnoreCase) >= 0
                ? VanguardGrenadeEmergencyTerminalKind.GrenadeExplodedAndHazardCleared
                : VanguardGrenadeEmergencyTerminalKind.GrenadeDestroyed;
            Finish(runtime, snapshot, state, now, terminal, terminalReason, "Completed");
            return;
        }

        if (bestHazard.HasRelevantHazard
            && !string.Equals(bestHazard.GrenadeKey, state.GrenadeKey, StringComparison.OrdinalIgnoreCase)
            && (bestHazard.RiskScore > state.LastHazard.RiskScore + 5f || !VanguardGrenadeHazardRegistry.TryGetGrenade(state.GrenadeKey, out _)))
        {
            Finish(runtime, snapshot, state, now, VanguardGrenadeEmergencyTerminalKind.SupersededByHigherRiskGrenade,
                "higher_risk_grenade:" + bestHazard.GrenadeKey, "Interrupted");
            Admit(runtime, snapshot, bestHazard, now);
            return;
        }

        if (!VanguardGrenadeHazardRegistry.TryCaptureExactHazardSnapshot(state.GrenadeKey, runtime, position, now, out VanguardGrenadeHazardDecisionSnapshot hazard))
        {
            if (state.RuntimeLostSinceUtc == DateTimeOffset.MinValue)
            {
                state.RuntimeLostSinceUtc = now;
                return;
            }
            if (now - state.RuntimeLostSinceUtc < TimeSpan.FromSeconds(VanguardGrenadeEmergencyPolicy.RuntimeObjectLostGraceSeconds))
            {
                return;
            }
            Finish(runtime, snapshot, state, now, VanguardGrenadeEmergencyTerminalKind.RuntimeObjectLost, "exact_grenade_runtime_object_lost_after_grace", "Interrupted");
            return;
        }
        state.RuntimeLostSinceUtc = DateTimeOffset.MinValue;
        UpdateStateHazard(state, hazard);

        if (now >= state.AbsoluteUntilUtc)
        {
            Finish(runtime, snapshot, state, now, VanguardGrenadeEmergencyTerminalKind.AbsoluteSafetyGuardExpired,
                "absolute_safety_guard_elapsed_while_grenade_still_live", "Timeout");
            return;
        }

        if (VanguardGrenadeEmergencyPolicy.IsSafetyEnvelopeSatisfied(hazard))
        {
            EnterOrMaintainSafetyHold(runtime, state, hazard, position, now);
            return;
        }

        if (state.Phase == VanguardGrenadeEmergencyPhase.HoldingSafety)
        {
            BreakSafetyHold(runtime, state, hazard, now);
        }

        ObserveProgress(runtime, state, position, hazard.EffectiveDistance, now);
        switch (state.Phase)
        {
            case VanguardGrenadeEmergencyPhase.NativeRequested:
            case VanguardGrenadeEmergencyPhase.NativeProgress:
                TickNative(runtime, state, position, hazard.EffectiveDistance, now);
                break;
            case VanguardGrenadeEmergencyPhase.FallbackPlanning:
                PlanFallback(runtime, snapshot, state, now);
                break;
            case VanguardGrenadeEmergencyPhase.FallbackMoving:
                TickFallback(runtime, snapshot, state, now);
                break;
        }
    }

    private static void Admit(VanguardRaidOperatorRuntimeRecord runtime, OperatorDecisionSnapshot snapshot, VanguardGrenadeHazardDecisionSnapshot hazard, DateTimeOffset now)
    {
        if (!VanguardGrenadeHazardRegistry.TryGetGrenade(hazard.GrenadeKey, out Grenade grenade))
        {
            return;
        }

        if (!VanguardMainIntentScheduler.TryOpenEmergencyGrenadeEvasion(snapshot, hazard, now, out string windowId, out string preemptedSummary, out string openReason))
        {
            VanguardClientDiagnosticsLog.Warning(VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag,
                $"GRENADE_EMERGENCY_ADMISSION_FAILED operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; {hazard.Summary}; reason={Safe(openReason)}; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}");
            return;
        }

        float windowSeconds = Math.Max(VanguardGrenadeEmergencyPolicy.MinimumAbsoluteEmergencySeconds, hazard.RecommendedAbsoluteWindowSeconds);
        TimeSpan suppressionTtl = TimeSpan.FromSeconds(windowSeconds + 1.0f);
        string movementClear = VanguardReturnMovementCommandStore.Clear(runtime.BotProfileId, "grenade_emergency_preempt_existing_movement");
        string lootPreempt = VanguardOpportunisticLootBroker.PreventForVanguardOwnedWindow(runtime.BotOwner!, windowSeconds + 1.0f, "grenade_emergency");
        string medicalPreempt = RequestMedicalCancellation(runtime.BotProfileId, now, hazard);
        VanguardExternalPreemptResult externalPreempt = VanguardExternalAuthorityAdapter.RequestGrenadeEmergencyPreempt(
            runtime.BotOwner, snapshot, "grenade_emergency", suppressionTtl, now);

        Vector3 start = runtime.BotOwner!.Position;
        var state = new VanguardGrenadeEmergencyOperatorState
        {
            OperatorId = runtime.OperatorId,
            BotProfileId = runtime.BotProfileId,
            WindowId = windowId,
            GrenadeKey = hazard.GrenadeKey,
            Phase = VanguardGrenadeEmergencyPhase.NativeRequested,
            StartedAtUtc = now,
            AbsoluteUntilUtc = now + TimeSpan.FromSeconds(windowSeconds),
            PhaseStartedAtUtc = now,
            LastProgressAtUtc = now,
            StartPosition = start,
            LastPosition = start,
            GrenadePosition = hazard.GrenadePosition,
            DangerPoint = hazard.DangerPoint,
            DangerPointKnown = hazard.DangerPointKnown,
            StartDistance = hazard.EffectiveDistance,
            LastDistance = hazard.EffectiveDistance,
            BestDistance = hazard.EffectiveDistance,
            SafeDistance = hazard.SafeDistance,
            NativeProbeSeconds = hazard.NativeProbeSeconds,
            LastHazard = hazard,
        };
        SetState(state);

        bool skipNativeForFuse = VanguardGrenadeEmergencyPolicy.ShouldSkipNative(hazard.FuseProfile);
        bool bypassNativeForSain = ShouldBypassNativeForActiveSainLocomotion(
            snapshot,
            externalPreempt,
            out string sainBypassReason);
        bool skipNative = skipNativeForFuse || bypassNativeForSain;
        string nativeSummary;
        bool nativeRequested;
        if (bypassNativeForSain)
        {
            nativeRequested = false;
            nativeSummary = "native_bypassed_for_active_sain_locomotion:" + sainBypassReason;
        }
        else if (skipNativeForFuse)
        {
            nativeRequested = false;
            nativeSummary = "native_skipped_for_contact_or_imminent_fuse";
        }
        else
        {
            nativeRequested = RequestNativeEvasion(runtime.BotOwner!, grenade, hazard, out nativeSummary);
        }

        VanguardClientDiagnosticsLog.Operational(VanguardGrenadeEmergencyPolicy.FuseProfileTag, () =>
            $"operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; grenade={Safe(hazard.GrenadeKey)}; {hazard.FuseProfile.Summary}; safeDistance={hazard.SafeDistance:0.00}; nativeProbe={hazard.NativeProbeSeconds:0.00}; absoluteWindow={windowSeconds:0.00}; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}");
        VanguardClientDiagnosticsLog.Operational(VanguardGrenadeEmergencyPolicy.ActivityPreemptedTag, () =>
            $"operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; grenade={Safe(hazard.GrenadeKey)}; schedulerPreempt={Safe(preemptedSummary)}; movement={Safe(movementClear)}; medical={Safe(medicalPreempt)}; loot={Safe(lootPreempt)}; external={Safe(externalPreempt.ToString())}; exactPathAuthorityRequested=true; grenadeThrowingPreempt=new_throw_decisions_vetoed_existing_hands_left_to_safe_brain_handoff; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}");
        VanguardClientDiagnosticsLog.Operational(VanguardGrenadeEmergencyPolicy.NativeRequestedTag, () =>
            $"operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; window={Safe(windowId)}; grenade={Safe(hazard.GrenadeKey)}; nativeRequested={Bool(nativeRequested)}; nativeSkipped={Bool(skipNative)}; nativeSkippedForFuse={Bool(skipNativeForFuse)}; nativeBypassedForSain={Bool(bypassNativeForSain)}; native={Safe(nativeSummary)}; startEffectiveDistance={hazard.EffectiveDistance:0.00}; actualDistance={hazard.DistanceToGrenade:0.00}; predictedDistance={hazard.DistanceToDangerPoint:0.00}; fallbackIfNoProgress=true; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}");

        if (bypassNativeForSain)
        {
            VanguardClientDiagnosticsLog.Operational(VanguardGrenadeEmergencyPolicy.NativeBypassedForSainTag, () =>
                $"operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; window={Safe(windowId)}; grenade={Safe(hazard.GrenadeKey)}; reason={Safe(sainBypassReason)}; previousMovementOwner={externalPreempt.Before.MovementOwner}; previousMoverMoving={Bool(externalPreempt.Before.MoverMoving)}; previousPathActive={Bool(externalPreempt.Before.EftPathActive)}; previousSpeed={externalPreempt.Before.RealSpeed:0.00}; immediateFallbackLease=true; nativeProbeWait=false; layer97EligibleAfterCommandIssue=true; sainTargetPreserved=true; sainDecisionPreserved=true; tag={VanguardGrenadeEmergencyPolicy.ImmediateSainCombatLeaseStatusTag}");
            VanguardMainIntentScheduler.ReportPrimaryProgress(
                runtime.BotProfileId,
                now,
                "grenade_sain_locomotion_native_bypass",
                sainBypassReason,
                state.WindowId);
        }

        if (!nativeRequested)
        {
            state.Phase = VanguardGrenadeEmergencyPhase.FallbackPlanning;
            state.PhaseStartedAtUtc = now;
            if (!bypassNativeForSain)
            {
                VanguardClientDiagnosticsLog.Warning(VanguardGrenadeEmergencyPolicy.NativeFailedTag,
                    $"operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; grenade={Safe(hazard.GrenadeKey)}; reason={Safe(nativeSummary)}; fallbackImmediate=true; nativeBypassedForSain=false; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}");
            }
            PlanFallback(runtime, snapshot, state, now);
        }
    }

    private static bool RequestNativeEvasion(BotOwner owner, Grenade grenade, VanguardGrenadeHazardDecisionSnapshot hazard, out string summary)
    {
        try
        {
            if (owner.BewareGrenade == null)
            {
                summary = "beware_grenade_missing";
                return false;
            }

            // Stop the pre-existing locomotion backend immediately before the native grenade node
            // writes its own destination. This makes the native request an atomic path replacement.
            owner.GoToSomePointData?.UpdateToGo(false);
            owner.Mover?.Stop();
            owner.Sprint(false, true);
            owner.BewareGrenade.AddGrenadeDanger(hazard.DangerPoint, grenade);
            bool dangerWritten = VanguardGrenadeRuntimeResolver.TryReadNativeDangerState(
                owner.BewareGrenade,
                out bool dangerPresent,
                out Grenade? nativeGrenade,
                out Vector3 nativeDangerPoint)
                && dangerPresent
                && ReferenceEquals(nativeGrenade, grenade);
            if (!dangerWritten)
            {
                summary = "native_danger_not_immediately_written_update_by_node_skipped";
                return false;
            }

            owner.BewareGrenade.UpdateByNode();
            summary = "old_path_stopped_then_native_danger_confirmed_then_update_by_node:dangerPoint=" + VectorText(nativeDangerPoint);
            return true;
        }
        catch (Exception exception)
        {
            summary = exception.GetType().Name + ":" + exception.Message;
            return false;
        }
    }

    private static void TickNative(VanguardRaidOperatorRuntimeRecord runtime, VanguardGrenadeEmergencyOperatorState state, Vector3 position, float distance, DateTimeOffset now)
    {
        float moved = HorizontalDistance(position, state.StartPosition);
        float gain = distance - state.StartDistance;
        bool causalProgress = moved >= VanguardGrenadeEmergencyPolicy.ProgressPositionMeters
            && gain >= VanguardGrenadeEmergencyPolicy.ProgressAwayMeters;
        if (causalProgress)
        {
            state.Phase = VanguardGrenadeEmergencyPhase.NativeProgress;
            if (!state.NativeProgressLogged)
            {
                state.NativeProgressLogged = true;
                VanguardClientDiagnosticsLog.Operational(VanguardGrenadeEmergencyPolicy.NativeProgressTag, () =>
                    $"operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; grenade={Safe(state.GrenadeKey)}; moved={moved:0.00}; effectiveDistanceGain={gain:0.00}; currentEffectiveDistance={distance:0.00}; actualDistance={state.LastHazard.DistanceToGrenade:0.00}; predictedDistance={state.LastHazard.DistanceToDangerPoint:0.00}; causal=true; competingMovementStoppedBeforeRequest=true; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}");
            }
            VanguardMainIntentScheduler.ReportPrimaryProgress(runtime.BotProfileId, now, "grenade_native_away_progress", "moved=" + moved.ToString("0.00", CultureInfo.InvariantCulture) + ";gain=" + gain.ToString("0.00", CultureInfo.InvariantCulture), state.WindowId);
        }

        TimeSpan sincePhase = now - state.PhaseStartedAtUtc;
        TimeSpan sinceProgress = now - state.LastProgressAtUtc;
        if ((state.Phase == VanguardGrenadeEmergencyPhase.NativeRequested && sincePhase.TotalSeconds >= state.NativeProbeSeconds)
            || (state.Phase == VanguardGrenadeEmergencyPhase.NativeProgress && sinceProgress.TotalSeconds >= VanguardGrenadeEmergencyPolicy.NativeStallSeconds))
        {
            state.Phase = VanguardGrenadeEmergencyPhase.FallbackPlanning;
            state.PhaseStartedAtUtc = now;
            VanguardClientDiagnosticsLog.Warning(VanguardGrenadeEmergencyPolicy.NativeFailedTag,
                $"operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; grenade={Safe(state.GrenadeKey)}; nativeProgress={Bool(state.NativeProgressLogged)}; nativeProbe={state.NativeProbeSeconds:0.00}; sincePhase={sincePhase.TotalSeconds:0.00}; sinceProgress={sinceProgress.TotalSeconds:0.00}; effectiveDistance={distance:0.00}; fallback=true; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}");
        }
    }

    private static void PlanFallback(VanguardRaidOperatorRuntimeRecord runtime, OperatorDecisionSnapshot snapshot, VanguardGrenadeEmergencyOperatorState state, DateTimeOffset now)
    {
        if (state.NextFallbackCycleAtUtc != DateTimeOffset.MinValue && now < state.NextFallbackCycleAtUtc)
        {
            return;
        }
        if (state.FallbackPlans >= VanguardGrenadeEmergencyPolicy.MaximumFallbackPlansPerCycle)
        {
            state.FallbackCycles++;
            state.FallbackPlans = 0;
            state.NextFallbackCycleAtUtc = now + TimeSpan.FromSeconds(VanguardGrenadeEmergencyPolicy.FallbackCycleCooldownSeconds);
            VanguardClientDiagnosticsLog.Warning(VanguardGrenadeEmergencyPolicy.FallbackPlannedTag,
                $"operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; grenade={Safe(state.GrenadeKey)}; planCycleExhausted=true; cycle={state.FallbackCycles}; retryAt={state.NextFallbackCycleAtUtc:O}; emergencyRetained=true; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}");
            return;
        }
        if (state.LastPlanAtUtc != DateTimeOffset.MinValue
            && now - state.LastPlanAtUtc < TimeSpan.FromSeconds(VanguardGrenadeEmergencyPolicy.FallbackReplanCooldownSeconds))
        {
            return;
        }

        state.FallbackPlans++;
        state.TotalFallbackPlanAttempts++;
        VanguardGrenadeFallbackPlan plan = VanguardGrenadeFallbackPlanner.Plan(
            runtime.BotOwner!,
            state.LastHazard,
            now,
            state.FailedFallbackDestinations);
        state.LastPlanAtUtc = now;
        if (!plan.Valid)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardGrenadeEmergencyPolicy.FallbackPlannedTag,
                $"operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; grenade={Safe(state.GrenadeKey)}; valid=false; attempt={state.FallbackPlans}; cycle={state.FallbackCycles}; reason={Safe(plan.Summary)}; emergencyRetained=true; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}");
            return;
        }

        state.TotalValidFallbackPlans++;

        // The grenade registry and scheduler own the exact terminal. A short generic movement TTL
        // would create a false command-loss recovery inside a longer live grenade window.
        DateTimeOffset expires = state.AbsoluteUntilUtc;
        bool issued = VanguardReturnMovementCommandStore.Issue(
            state.WindowId,
            runtime.OperatorId,
            runtime.BotProfileId,
            plan.Destination,
            VanguardGrenadeEmergencyPolicy.EmergencyReachDistanceMeters,
            true,
            now,
            expires,
            VanguardGrenadeEmergencyPolicy.RequestKind,
            plan.Summary,
            plan.PathLength,
            out string issueResult);
        VanguardClientDiagnosticsLog.Operational(VanguardGrenadeEmergencyPolicy.FallbackPlannedTag, () =>
            $"operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; grenade={Safe(state.GrenadeKey)}; valid=true; attempt={state.FallbackPlans}; cycle={state.FallbackCycles}; {plan.Summary}; issue={Safe(issueResult)}; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}");
        if (!issued || !VanguardReturnMovementCommandStore.TryGetActive(runtime.BotProfileId, now, out VanguardReturnMovementCommand command))
        {
            return;
        }

        state.TotalFallbackCommandsIssued++;
        state.FallbackDestination = plan.Destination;
        state.FallbackDestinationKnown = true;
        state.MovementGeneration = command.Generation;
        state.Phase = VanguardGrenadeEmergencyPhase.FallbackMoving;
        state.PhaseStartedAtUtc = now;
        state.LastProgressAtUtc = now;
        state.LastPosition = runtime.BotOwner!.Position;
        state.LastDistance = state.LastHazard.EffectiveDistance;
        VanguardMainIntentScheduler.ReportPrimaryProgress(runtime.BotProfileId, now, "grenade_fallback_started", plan.Summary, state.WindowId);
        VanguardClientDiagnosticsLog.Operational(VanguardGrenadeEmergencyPolicy.FallbackStartedTag, () =>
            $"operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; window={Safe(state.WindowId)}; grenade={Safe(state.GrenadeKey)}; generation={command.Generation}; destination={VectorText(plan.Destination)}; sprint=true; slowAtEnd=false; atomicPathReplaceRequested=true; physicalProofSeparate=true; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}");
        if (!VanguardGrenadeEmergencyPhysicalDriver.Drive(runtime.BotOwner!, command, now, "fallback_start", out string physicalStart))
        {
            if (VanguardReturnMovementCommandStore.TryConsumePhysicalBackendFailure(
                runtime.BotProfileId,
                state.WindowId,
                state.MovementGeneration,
                now,
                out string backendFailure))
            {
                HandlePhysicalBackendFailure(runtime, snapshot, state, now, backendFailure, "fallback_start");
                return;
            }

            bool immediatePathInvalid = VanguardReturnMovementCommandStore.TryConsumePathInvalid(
                runtime.BotProfileId,
                state.WindowId,
                state.MovementGeneration,
                now,
                out string pathInvalid);
            if (immediatePathInvalid)
            {
                HandleWindowRecoveryFailure(
                    runtime,
                    snapshot,
                    state,
                    now,
                    "fallback_start_path_invalid",
                    pathInvalid,
                    quarantineAnchor: true);
                return;
            }

            VanguardClientDiagnosticsLog.Warning(VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag,
                $"GRENADE_PHYSICAL_DRIVE_FAILED operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; grenade={Safe(state.GrenadeKey)}; generation={command.Generation}; reason={Safe(physicalStart)}; phase=fallback_start; emergencyRetained=true; tag={VanguardGrenadeEmergencyPhysicalDriver.StatusTag}");
        }
    }

    private static void TickFallback(VanguardRaidOperatorRuntimeRecord runtime, OperatorDecisionSnapshot snapshot, VanguardGrenadeEmergencyOperatorState state, DateTimeOffset now)
    {
        if (VanguardReturnMovementCommandStore.TryConsumePhysicalBackendFailure(runtime.BotProfileId, state.WindowId, state.MovementGeneration, now, out string backendFailure))
        {
            HandlePhysicalBackendFailure(runtime, snapshot, state, now, backendFailure, "fallback_tick");
            return;
        }

        if (VanguardReturnMovementCommandStore.TryConsumePathInvalid(runtime.BotProfileId, state.WindowId, state.MovementGeneration, now, out string pathInvalid))
        {
            HandleWindowRecoveryFailure(
                runtime,
                snapshot,
                state,
                now,
                "fallback_path_invalid",
                pathInvalid,
                quarantineAnchor: true);
            return;
        }

        if (!VanguardReturnMovementCommandStore.TryGetExactOwned(runtime.BotProfileId, state.WindowId, VanguardGrenadeEmergencyPolicy.RequestKind, state.MovementGeneration, now, out VanguardReturnMovementCommand activeCommand, out string commandReason))
        {
            HandleWindowRecoveryFailure(
                runtime,
                snapshot,
                state,
                now,
                "fallback_command_lost",
                commandReason,
                quarantineAnchor: false);
            return;
        }

        VanguardReturnMovementCommandStore.RefreshLeaseWindow(
            runtime.BotProfileId,
            state.AbsoluteUntilUtc,
            "exact_grenade_window_refresh");

        if (!VanguardGrenadeEmergencyPhysicalDriver.Drive(runtime.BotOwner!, activeCommand, now, "emergency_service", out string physicalDrive))
        {
            VanguardClientDiagnosticsLog.Warning(VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag,
                $"GRENADE_PHYSICAL_DRIVE_FAILED operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; grenade={Safe(state.GrenadeKey)}; generation={state.MovementGeneration}; reason={Safe(physicalDrive)}; emergencyRetained=true; replanSignalDeferredToNextTick=true; tag={VanguardGrenadeEmergencyPhysicalDriver.IgnitionGraceStatusTag}");
            return;
        }

        if (VanguardGrenadeEmergencyPhysicalDriver.IsIgnitionPending(
            runtime.BotProfileId,
            state.MovementGeneration,
            now,
            out string ignitionSummary))
        {
            VanguardMainIntentScheduler.ReportPrimaryProgress(runtime.BotProfileId, now,
                "grenade_physical_ignition_pending",
                ignitionSummary,
                state.WindowId);
            return;
        }

        if (now - state.LastProgressAtUtc >= TimeSpan.FromSeconds(VanguardGrenadeEmergencyPolicy.FallbackStallSeconds))
        {
            bool physicalProgress = VanguardGrenadeEmergencyPhysicalDriver.IsPhysicalPathProgressRecent(
                runtime.BotProfileId,
                state.MovementGeneration,
                now,
                TimeSpan.FromSeconds(VanguardGrenadeEmergencyPolicy.FallbackStallSeconds),
                out string physicalProgressSummary);
            if (physicalProgress)
            {
                // The path is physically advancing even if grenade-distance gain is temporarily flat
                // around an obstacle. Preserve the exact command and avoid a destructive stop/replan.
                state.LastProgressAtUtc = now;
                VanguardMainIntentScheduler.ReportPrimaryProgress(runtime.BotProfileId, now,
                    "grenade_fallback_path_progress",
                    physicalProgressSummary,
                    state.WindowId);
                return;
            }

            HandleWindowRecoveryFailure(
                runtime,
                snapshot,
                state,
                now,
                "fallback_no_physical_or_safety_progress",
                physicalProgressSummary,
                quarantineAnchor: true);
        }
    }

    private static void HandlePhysicalBackendFailure(
        VanguardRaidOperatorRuntimeRecord runtime,
        OperatorDecisionSnapshot snapshot,
        VanguardGrenadeEmergencyOperatorState state,
        DateTimeOffset now,
        string backendFailure,
        string phase)
    {
        state.PhysicalBackendRepairAttempts++;
        HandleWindowRecoveryFailure(
            runtime,
            snapshot,
            state,
            now,
            "physical_backend_failure_" + phase,
            backendFailure,
            quarantineAnchor: false);
    }

    private static void HandleWindowRecoveryFailure(
        VanguardRaidOperatorRuntimeRecord runtime,
        OperatorDecisionSnapshot snapshot,
        VanguardGrenadeEmergencyOperatorState state,
        DateTimeOffset now,
        string failureKind,
        string failureDetail,
        bool quarantineAnchor)
    {
        bool destructiveRecoveryAvailable = state.WindowRecoveryAttempts < MaximumDestructiveWindowRecoveries;
        bool failedAnchorRecorded = destructiveRecoveryAvailable && quarantineAnchor && RecordFailedFallbackDestination(state);
        if (destructiveRecoveryAvailable)
        {
            state.WindowRecoveryAttempts++;
            string movementCleanup = VanguardReturnMovementCommandStore.ClearOwned(
                runtime.BotProfileId,
                state.WindowId,
                state.StartedAtUtc,
                "window_recovery:" + failureKind);
            string physicalCleanup = VanguardGrenadeEmergencyPhysicalDriver.StopAndRelease(
                runtime.BotOwner,
                runtime.BotProfileId,
                "window_recovery:" + failureKind + ":" + failureDetail);
            state.Phase = VanguardGrenadeEmergencyPhase.FallbackPlanning;
            state.PhaseStartedAtUtc = now;
            state.LastPlanAtUtc = DateTimeOffset.MinValue;
            state.NextFallbackCycleAtUtc = DateTimeOffset.MinValue;
            state.FallbackDestinationKnown = false;
            VanguardClientDiagnosticsLog.Warning(GrenadeWindowConvergenceTag,
                $"GRENADE_WINDOW_RECOVERY_CONSUMED operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; window={Safe(state.WindowId)}; grenade={Safe(state.GrenadeKey)}; failureKind={Safe(failureKind)}; failureDetail={Safe(failureDetail)}; windowRecoveryAttempt={state.WindowRecoveryAttempts}; windowRecoveryBudget={MaximumDestructiveWindowRecoveries}; failedAnchorRecorded={Bool(failedAnchorRecorded)}; failedAnchorCount={state.FailedFallbackDestinations.Count}; movementCleanup={Safe(movementCleanup)}; physicalCleanup={Safe(physicalCleanup)}; next=FallbackPlanning; generationResetDoesNotResetWindowBudget=true; anchorChangeDoesNotResetWindowBudget=true; tag={GrenadeWindowConvergenceTag}");
            return;
        }

        state.WindowRecoveryBudgetExhausted = true;
        if (state.WindowRecoveryBudgetExhaustedAtUtc == DateTimeOffset.MinValue)
        {
            state.WindowRecoveryBudgetExhaustedAtUtc = now;
        }

        bool explicitBackendFailure = failureKind.StartsWith("physical_backend_failure_", StringComparison.OrdinalIgnoreCase);
        if (state.StickySameAnchorBackendResets < MaximumStickySameAnchorBackendResets
            && state.FallbackDestinationKnown)
        {
            state.StickySameAnchorBackendResets++;
            string physicalCleanup = VanguardGrenadeEmergencyPhysicalDriver.StopAndRelease(
                runtime.BotOwner,
                runtime.BotProfileId,
                "sticky_same_anchor_backend_reset:" + failureKind + ":" + failureDetail);
            if (!TryRetainStickySameAnchorCommand(runtime, state, now, out string commandRecovery))
            {
                FinishWindowRecoveryAsBackendUnavailable(
                    runtime,
                    snapshot,
                    state,
                    now,
                    failureKind,
                    failureDetail,
                    "sticky_same_anchor_command_unavailable:" + commandRecovery,
                    failedAnchorRecorded);
                return;
            }

            state.Phase = VanguardGrenadeEmergencyPhase.FallbackMoving;
            state.PhaseStartedAtUtc = now;
            state.LastProgressAtUtc = now;
            state.LastPosition = runtime.BotOwner!.Position;
            state.LastDistance = state.LastHazard.EffectiveDistance;
            VanguardClientDiagnosticsLog.Warning(GrenadeWindowConvergenceTag,
                $"GRENADE_WINDOW_RECOVERY_BUDGET_EXHAUSTED operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; window={Safe(state.WindowId)}; grenade={Safe(state.GrenadeKey)}; failureKind={Safe(failureKind)}; failureDetail={Safe(failureDetail)}; windowRecoveryAttempts={state.WindowRecoveryAttempts}; windowRecoveryBudget={MaximumDestructiveWindowRecoveries}; stickyBackendReset={state.StickySameAnchorBackendResets}; stickyBackendResetBudget={MaximumStickySameAnchorBackendResets}; sameAnchorRetained=true; sameGenerationRetained=true; commandRecovery={Safe(commandRecovery)}; physicalCleanup={Safe(physicalCleanup)}; destructiveReplan=false; emergencyLayerRetained=true; survivalAuthorityUntilGrenadeTerminal=true; generationResetDoesNotResetWindowBudget=true; anchorChangeDoesNotResetWindowBudget=true; tag={GrenadeWindowConvergenceTag}");
            return;
        }

        if (explicitBackendFailure)
        {
            FinishWindowRecoveryAsBackendUnavailable(
                runtime,
                snapshot,
                state,
                now,
                failureKind,
                failureDetail,
                "physical_backend_failed_after_single_sticky_reset",
                failedAnchorRecorded);
            return;
        }

        if (!VanguardReturnMovementCommandStore.TryGetExactOwned(
            runtime.BotProfileId,
            state.WindowId,
            VanguardGrenadeEmergencyPolicy.RequestKind,
            state.MovementGeneration,
            now,
            out _,
            out string commandReason))
        {
            FinishWindowRecoveryAsBackendUnavailable(
                runtime,
                snapshot,
                state,
                now,
                failureKind,
                failureDetail,
                "exact_sticky_command_lost_after_window_budget:" + commandReason,
                failedAnchorRecorded);
            return;
        }

        // Survival-first terminal policy: a path stall or path-invalid signal must not silently
        // surrender the locomotion layer to SAIN while the exact grenade is still live. Once the
        // single destructive replan and single backend reset are consumed, retain the final exact
        // anchor/generation, keep driving it idempotently, and let only a physical grenade terminal,
        // the absolute guard, operator death, or a proven backend loss close the emergency window.
        VanguardReturnMovementCommandStore.RefreshLeaseWindow(
            runtime.BotProfileId,
            state.AbsoluteUntilUtc,
            "sticky_survival_authority");
        state.Phase = VanguardGrenadeEmergencyPhase.FallbackMoving;
        state.LastProgressAtUtc = now;
        state.LastPosition = runtime.BotOwner!.Position;
        state.LastDistance = state.LastHazard.EffectiveDistance;
        state.StickyHoldEvents++;
        if (state.LastStickyHoldLogAtUtc == DateTimeOffset.MinValue
            || now - state.LastStickyHoldLogAtUtc >= StickyHoldLogInterval)
        {
            state.LastStickyHoldLogAtUtc = now;
            VanguardClientDiagnosticsLog.Warning(GrenadeWindowConvergenceTag,
                $"GRENADE_WINDOW_STICKY_SURVIVAL_HOLD operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; window={Safe(state.WindowId)}; grenade={Safe(state.GrenadeKey)}; failureKind={Safe(failureKind)}; failureDetail={Safe(failureDetail)}; windowRecoveryAttempts={state.WindowRecoveryAttempts}; stickyBackendResets={state.StickySameAnchorBackendResets}; stickyHoldEvents={state.StickyHoldEvents}; sameAnchorRetained=true; sameGenerationRetained=true; destructiveReplan=false; commandReissue=false; emergencyLayerRetained=true; survivalAuthorityUntilGrenadeTerminal=true; silentSainReturn=false; tag={GrenadeWindowConvergenceTag}");
        }
    }

    private static void FinishWindowRecoveryAsBackendUnavailable(
        VanguardRaidOperatorRuntimeRecord runtime,
        OperatorDecisionSnapshot snapshot,
        VanguardGrenadeEmergencyOperatorState state,
        DateTimeOffset now,
        string failureKind,
        string failureDetail,
        string terminalReason,
        bool failedAnchorRecorded)
    {
        VanguardClientDiagnosticsLog.Warning(GrenadeWindowConvergenceTag,
            $"GRENADE_WINDOW_RECOVERY_TERMINAL operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; window={Safe(state.WindowId)}; grenade={Safe(state.GrenadeKey)}; failureKind={Safe(failureKind)}; failureDetail={Safe(failureDetail)}; windowRecoveryAttempts={state.WindowRecoveryAttempts}; windowRecoveryBudget={MaximumDestructiveWindowRecoveries}; stickyBackendResets={state.StickySameAnchorBackendResets}; stickyBackendResetBudget={MaximumStickySameAnchorBackendResets}; stickyHoldEvents={state.StickyHoldEvents}; failedAnchorRecorded={Bool(failedAnchorRecorded)}; failedAnchorCount={state.FailedFallbackDestinations.Count}; terminal=PhysicalBackendUnavailable; reason={Safe(terminalReason)}; silentSainReturn=false; causalTerminal=true; terminalRequiresProvenBackendLoss=true; tag={GrenadeWindowConvergenceTag}");
        Finish(
            runtime,
            snapshot,
            state,
            now,
            VanguardGrenadeEmergencyTerminalKind.PhysicalBackendUnavailable,
            "window_scoped_recovery_backend_unavailable:" + terminalReason + ":" + failureKind + ":" + failureDetail,
            "Interrupted");
    }

    private static bool TryRetainStickySameAnchorCommand(
        VanguardRaidOperatorRuntimeRecord runtime,
        VanguardGrenadeEmergencyOperatorState state,
        DateTimeOffset now,
        out string result)
    {
        if (!VanguardReturnMovementCommandStore.TryGetExactOwned(
            runtime.BotProfileId,
            state.WindowId,
            VanguardGrenadeEmergencyPolicy.RequestKind,
            state.MovementGeneration,
            now,
            out _,
            out string commandReason))
        {
            result = "exact_command_unavailable:" + commandReason;
            return false;
        }

        VanguardReturnMovementCommandStore.RefreshLeaseWindow(
            runtime.BotProfileId,
            state.AbsoluteUntilUtc,
            "sticky_same_anchor_refresh");
        result = "retained_exact_command_same_generation";
        return true;
    }

    private static bool RecordFailedFallbackDestination(VanguardGrenadeEmergencyOperatorState state)
    {
        if (!state.FallbackDestinationKnown) return false;
        foreach (Vector3 existing in state.FailedFallbackDestinations)
        {
            if (HorizontalDistance(existing, state.FallbackDestination) <= 0.75f) return false;
        }
        state.FailedFallbackDestinations.Add(state.FallbackDestination);
        return true;
    }

    private static void EnterOrMaintainSafetyHold(
        VanguardRaidOperatorRuntimeRecord runtime,
        VanguardGrenadeEmergencyOperatorState state,
        VanguardGrenadeHazardDecisionSnapshot hazard,
        Vector3 position,
        DateTimeOffset now)
    {
        bool entering = state.Phase != VanguardGrenadeEmergencyPhase.HoldingSafety;
        string pathSummary = VanguardGrenadeEmergencyPolicy.SafetyHoldPathMarker
            + ";actualDistance=" + hazard.DistanceToGrenade.ToString("0.00", CultureInfo.InvariantCulture)
            + ";predictedDistance=" + hazard.DistanceToDangerPoint.ToString("0.00", CultureInfo.InvariantCulture)
            + ";effectiveDistance=" + hazard.EffectiveDistance.ToString("0.00", CultureInfo.InvariantCulture)
            + ";dualCover=" + Bool(hazard.DualSolidCover);

        bool activeExactEmergency = VanguardReturnMovementCommandStore.TryGetActive(runtime.BotProfileId, now, out VanguardReturnMovementCommand active)
            && string.Equals(active.LeaseId, state.WindowId, StringComparison.OrdinalIgnoreCase)
            && VanguardReturnMovementCommandStore.IsGrenadeEmergencyRequest(active.RequestKind);
        bool activeAlreadyHolding = activeExactEmergency
            && !string.IsNullOrWhiteSpace(active.PathSummary)
            && active.PathSummary.IndexOf(VanguardGrenadeEmergencyPolicy.SafetyHoldPathMarker, StringComparison.OrdinalIgnoreCase) >= 0;

        if (activeExactEmergency && !activeAlreadyHolding)
        {
            // RetargetActive historically treats only anchor/sprint/radius as a material retarget. A
            // fallback command may already be close to the current position, so merely changing the
            // path summary would leave the old GoToPoint destination alive. Replace the exact owned
            // command before entering HoldingSafety to guarantee the no-destination contract.
            VanguardReturnMovementCommandStore.ClearOwned(
                runtime.BotProfileId,
                state.WindowId,
                state.StartedAtUtc,
                "replace_escape_destination_with_safety_hold");
            activeExactEmergency = false;
        }

        if (activeExactEmergency)
        {
            VanguardMovementRetargetResult retarget = VanguardReturnMovementCommandStore.TryRetargetActive(
                state.WindowId,
                runtime.BotProfileId,
                position,
                0.50f,
                false,
                now,
                state.AbsoluteUntilUtc,
                pathSummary,
                0f,
                "refresh_safety_hold",
                0.50f,
                TimeSpan.Zero);
            if (retarget.Generation > 0L)
            {
                state.MovementGeneration = retarget.Generation;
            }
        }
        else
        {
            VanguardReturnMovementCommandStore.Issue(
                state.WindowId,
                runtime.OperatorId,
                runtime.BotProfileId,
                position,
                0.50f,
                false,
                now,
                state.AbsoluteUntilUtc,
                VanguardGrenadeEmergencyPolicy.RequestKind,
                pathSummary,
                0f,
                out _);
            if (VanguardReturnMovementCommandStore.TryGetActive(runtime.BotProfileId, now, out VanguardReturnMovementCommand issuedHoldCommand))
            {
                state.MovementGeneration = issuedHoldCommand.Generation;
            }
        }
        VanguardReturnMovementCommandStore.RefreshLeaseWindow(runtime.BotProfileId, state.AbsoluteUntilUtc, "safety_hold");
        if (VanguardReturnMovementCommandStore.TryGetExactOwned(runtime.BotProfileId, state.WindowId, VanguardGrenadeEmergencyPolicy.RequestKind, state.MovementGeneration, now, out VanguardReturnMovementCommand holdCommand, out _))
        {
            VanguardGrenadeEmergencyPhysicalDriver.Drive(runtime.BotOwner!, holdCommand, now, "emergency_service_holding", out _);
        }

        state.Phase = VanguardGrenadeEmergencyPhase.HoldingSafety;
        state.PhaseStartedAtUtc = entering ? now : state.PhaseStartedAtUtc;
        state.HoldingStartedAtUtc = entering ? now : state.HoldingStartedAtUtc;
        state.LastProgressAtUtc = now;
        state.LastPosition = position;
        state.LastDistance = hazard.EffectiveDistance;
        state.BestDistance = Math.Max(state.BestDistance, hazard.EffectiveDistance);
        state.FallbackDestinationKnown = false;

        if (entering)
        {
            string safetyKind = hazard.DistanceToGrenade >= hazard.SafeDistance + VanguardGrenadeEmergencyPolicy.SafeDistanceHysteresisMeters
                && (!hazard.DangerPointKnown || hazard.DistanceToDangerPoint >= hazard.SafeDistance + VanguardGrenadeEmergencyPolicy.SafeDistanceHysteresisMeters)
                ? "dual_distance"
                : "dual_solid_cover";
            VanguardClientDiagnosticsLog.Operational(VanguardGrenadeEmergencyPolicy.SafeDistanceTag, () =>
                $"operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; grenade={Safe(state.GrenadeKey)}; actualDistance={hazard.DistanceToGrenade:0.00}; predictedDistance={hazard.DistanceToDangerPoint:0.00}; effectiveDistance={hazard.EffectiveDistance:0.00}; safeDistance={hazard.SafeDistance:0.00}; safetyKind={safetyKind}; terminal=false; next=HoldingSafety; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}");
            if (hazard.DualSolidCover)
            {
                VanguardClientDiagnosticsLog.Operational(VanguardGrenadeEmergencyPolicy.SolidCoverTag, () =>
                    $"operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; grenade={Safe(state.GrenadeKey)}; actualBlocked={Bool(hazard.ActualLineOfEffectBlocked)}; predictedBlocked={Bool(hazard.PredictedLineOfEffectBlocked)}; actualDistance={hazard.DistanceToGrenade:0.00}; predictedDistance={hazard.DistanceToDangerPoint:0.00}; terminal=false; next=HoldingSafety; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}");
            }
            VanguardClientDiagnosticsLog.Operational(VanguardGrenadeEmergencyPolicy.SafetyHoldEnteredTag, () =>
                $"operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; window={Safe(state.WindowId)}; grenade={Safe(state.GrenadeKey)}; safetyKind={safetyKind}; actualDistance={hazard.DistanceToGrenade:0.00}; predictedDistance={hazard.DistanceToDangerPoint:0.00}; effectiveDistance={hazard.EffectiveDistance:0.00}; dualCover={Bool(hazard.DualSolidCover)}; grenadeStillLive=true; followResume=false; combatResume=false; medicalResume=false; holdUntilPhysicalTerminal=true; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}");
            VanguardMainIntentScheduler.ReportPrimaryProgress(runtime.BotProfileId, now, "grenade_safety_hold_entered", pathSummary, state.WindowId);
        }
        else if (state.LastHoldingLogAtUtc == DateTimeOffset.MinValue
            || now - state.LastHoldingLogAtUtc >= TimeSpan.FromSeconds(VanguardGrenadeEmergencyPolicy.SafetyHoldLogIntervalSeconds))
        {
            state.LastHoldingLogAtUtc = now;
            VanguardClientDiagnosticsLog.Operational(VanguardGrenadeEmergencyPolicy.SafetyHoldMaintainedTag, () =>
                $"operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; window={Safe(state.WindowId)}; grenade={Safe(state.GrenadeKey)}; actualDistance={hazard.DistanceToGrenade:0.00}; predictedDistance={hazard.DistanceToDangerPoint:0.00}; effectiveDistance={hazard.EffectiveDistance:0.00}; dualCover={Bool(hazard.DualSolidCover)}; remaining={(hazard.EstimatedTimeToExplosionSeconds.HasValue ? hazard.EstimatedTimeToExplosionSeconds.Value.ToString("0.00", CultureInfo.InvariantCulture) : "unknown")}; emergencyRetained=true; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}");
        }
    }

    private static void BreakSafetyHold(VanguardRaidOperatorRuntimeRecord runtime, VanguardGrenadeEmergencyOperatorState state, VanguardGrenadeHazardDecisionSnapshot hazard, DateTimeOffset now)
    {
        VanguardReturnMovementCommandStore.ClearOwned(runtime.BotProfileId, state.WindowId, state.StartedAtUtc, "safety_hold_broken");
        VanguardGrenadeEmergencyPhysicalDriver.Release(runtime.BotProfileId);
        state.Phase = VanguardGrenadeEmergencyPhase.FallbackPlanning;
        state.PhaseStartedAtUtc = now;
        state.LastProgressAtUtc = now;
        state.FallbackPlans = 0;
        state.NextFallbackCycleAtUtc = DateTimeOffset.MinValue;
        state.FallbackDestinationKnown = false;
        VanguardClientDiagnosticsLog.Warning(VanguardGrenadeEmergencyPolicy.SafetyHoldBrokenTag,
            $"operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; window={Safe(state.WindowId)}; grenade={Safe(state.GrenadeKey)}; actualDistance={hazard.DistanceToGrenade:0.00}; predictedDistance={hazard.DistanceToDangerPoint:0.00}; effectiveDistance={hazard.EffectiveDistance:0.00}; dualCover={Bool(hazard.DualSolidCover)}; next=FallbackPlanning; emergencyRetained=true; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}");
    }

    private static void ObserveProgress(VanguardRaidOperatorRuntimeRecord runtime, VanguardGrenadeEmergencyOperatorState state, Vector3 position, float distance, DateTimeOffset now)
    {
        float moved = HorizontalDistance(position, state.LastPosition);
        float gain = distance - state.LastDistance;
        if (moved >= VanguardGrenadeEmergencyPolicy.ProgressPositionMeters && gain >= VanguardGrenadeEmergencyPolicy.ProgressAwayMeters)
        {
            state.LastProgressAtUtc = now;
            state.BestDistance = Math.Max(state.BestDistance, distance);
            VanguardMainIntentScheduler.ReportPrimaryProgress(runtime.BotProfileId, now,
                state.Phase == VanguardGrenadeEmergencyPhase.FallbackMoving ? "grenade_fallback_away_progress" : "grenade_native_away_progress",
                "step=" + moved.ToString("0.00", CultureInfo.InvariantCulture) + ";effectiveGain=" + gain.ToString("0.00", CultureInfo.InvariantCulture) + ";effectiveDistance=" + distance.ToString("0.00", CultureInfo.InvariantCulture),
                state.WindowId);
        }
        state.LastPosition = position;
        state.LastDistance = distance;
    }

    private static void UpdateStateHazard(VanguardGrenadeEmergencyOperatorState state, VanguardGrenadeHazardDecisionSnapshot hazard)
    {
        state.LastHazard = hazard;
        state.GrenadePosition = hazard.GrenadePosition;
        state.DangerPoint = hazard.DangerPoint;
        state.DangerPointKnown = hazard.DangerPointKnown;
        state.SafeDistance = hazard.SafeDistance;
        state.NativeProbeSeconds = hazard.NativeProbeSeconds;
    }

    private static string RequestMedicalCancellation(string botProfileId, DateTimeOffset now, VanguardGrenadeHazardDecisionSnapshot hazard)
    {
        if (!VanguardExecutionLeaseStore.TryGetActive(botProfileId, out VanguardExecutionLeaseState lease))
        {
            return "no_active_medical_lease";
        }

        lease.ThreatObservedDuringLease = true;
        lease.SurgeryCancellationRequested = true;
        lease.SurgeryCancellationRequestedAtUtc = now;
        lease.SurgeryCancellationReason = "grenade_emergency:" + hazard.GrenadeKey;
        lease.SurgeryCancellationKind = VanguardGrenadeEmergencyPolicy.RequestKind;
        lease.SurgeryCancellationIsThreat = true;
        lease.FirstAidCancellationRequested = true;
        lease.FirstAidCancellationRequestedAtUtc = now;
        lease.FirstAidCancellationReason = "grenade_emergency:" + hazard.GrenadeKey;
        lease.FirstAidCancellationKind = "grenade_emergency";
        lease.FirstAidCancellationIsThreat = true;
        return "cancellation_requested:lease=" + lease.LeaseId + ":window=" + lease.WindowKind;
    }

    private static void Finish(
        VanguardRaidOperatorRuntimeRecord runtime,
        OperatorDecisionSnapshot? snapshot,
        VanguardGrenadeEmergencyOperatorState state,
        DateTimeOffset now,
        VanguardGrenadeEmergencyTerminalKind terminal,
        string reason,
        string outcome,
        bool finishScheduler = true)
    {
        state.Phase = VanguardGrenadeEmergencyPhase.Terminal;
        string movement = VanguardReturnMovementCommandStore.ClearOwned(runtime.BotProfileId, state.WindowId, state.StartedAtUtc, "terminal:" + terminal + ":" + reason);
        string physicalCleanup = VanguardGrenadeEmergencyPhysicalDriver.StopAndRelease(
            runtime.BotOwner,
            runtime.BotProfileId,
            "terminal:" + terminal + ":" + reason);
        string externalRelease = VanguardExternalAuthorityAdapter.ReleaseMovementHardReturnPreempt(runtime.BotOwner, runtime.BotProfileId, now, "terminal:" + terminal);
        string scheduler = finishScheduler
            ? Bool(VanguardMainIntentScheduler.FinishPrimaryWindow(runtime.BotProfileId, now, outcome, reason, "movement=" + movement + ";physical=" + physicalCleanup + ";external=" + externalRelease, state.WindowId))
            : "not_requested";

        if (!state.HostileSourcePropagated
            && snapshot != null
            && state.LastHazard.SourceRelation == VanguardGrenadeLocalRelation.Hostile
            && !string.Equals(state.LastHazard.SourceProfileId, "none", StringComparison.OrdinalIgnoreCase))
        {
            state.HostileSourcePropagated = VanguardCombatAwarenessBridge.PublishHostileGrenadeSourceContact(
                snapshot,
                state.LastHazard.SourceProfileId,
                state.LastHazard.EffectiveDistance,
                now,
                terminal + ":" + reason);
        }

        RemoveState(runtime.BotProfileId, state.WindowId);
        VanguardClientDiagnosticsLog.Operational(VanguardGrenadeEmergencyPolicy.TerminalTag, () =>
            $"operator={Safe(runtime.OperatorId)}; botProfile={Safe(runtime.BotProfileId)}; window={Safe(state.WindowId)}; grenade={Safe(state.GrenadeKey)}; terminal={terminal}; outcome={Safe(outcome)}; reason={Safe(reason)}; phase={state.Phase}; startEffectiveDistance={state.StartDistance:0.00}; bestEffectiveDistance={state.BestDistance:0.00}; finalActualDistance={state.LastHazard.DistanceToGrenade:0.00}; finalPredictedDistance={state.LastHazard.DistanceToDangerPoint:0.00}; fallbackPlansCurrentCycle={state.FallbackPlans}; fallbackPlanAttemptsTotal={state.TotalFallbackPlanAttempts}; validFallbackPlansTotal={state.TotalValidFallbackPlans}; fallbackCommandsIssuedTotal={state.TotalFallbackCommandsIssued}; fallbackCycles={state.FallbackCycles}; windowRecoveryAttempts={state.WindowRecoveryAttempts}; windowRecoveryBudget={MaximumDestructiveWindowRecoveries}; windowRecoveryBudgetExhausted={Bool(state.WindowRecoveryBudgetExhausted)}; stickySameAnchorBackendResets={state.StickySameAnchorBackendResets}; stickyHoldEvents={state.StickyHoldEvents}; nativeProgress={Bool(state.NativeProgressLogged)}; holdingSafetyDuration={(state.HoldingStartedAtUtc == DateTimeOffset.MinValue ? 0d : (now - state.HoldingStartedAtUtc).TotalSeconds):0.00}; movementCleanup={Safe(movement)}; physicalCleanup={Safe(physicalCleanup)}; externalRelease={Safe(externalRelease)}; schedulerFinish={scheduler}; hostileSourcePropagated={Bool(state.HostileSourcePropagated)}; resumeTactic=next_scheduler_cycle_after_physical_grenade_terminal; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}; foundationTag={VanguardGrenadeEmergencyPolicy.StatusTag}");
    }

    private static VanguardGrenadeEmergencyOperatorState? GetState(string botProfileId)
    {
        lock (Sync)
        {
            return ActiveByBotProfileId.TryGetValue(botProfileId, out VanguardGrenadeEmergencyOperatorState? state) ? state : null;
        }
    }

    private static void SetState(VanguardGrenadeEmergencyOperatorState state)
    {
        lock (Sync)
        {
            ActiveByBotProfileId[state.BotProfileId] = state;
        }
    }

    private static void RemoveState(string botProfileId, string windowId)
    {
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(botProfileId, out VanguardGrenadeEmergencyOperatorState? state)
                && string.Equals(state.WindowId, windowId, StringComparison.OrdinalIgnoreCase))
            {
                ActiveByBotProfileId.Remove(botProfileId);
            }
        }
    }


    private static bool ShouldBypassNativeForActiveSainLocomotion(
        OperatorDecisionSnapshot snapshot,
        VanguardExternalPreemptResult externalPreempt,
        out string reason)
    {
        VanguardExternalActivitySnapshot before = externalPreempt.Before;

        bool movementSemantic = snapshot.Sain.Searching == true
            || snapshot.Sain.RunningToCover == true
            || ContainsAny(snapshot.Sain.CurrentAction, "search", "chase", "rush", "cover", "move", "run", "sprint", "flank", "push")
            || ContainsAny(snapshot.Sain.CombatDecision, "search", "chase", "rush", "cover", "move", "run", "sprint", "flank", "push")
            || ContainsAny(snapshot.Sain.SquadDecision, "search", "chase", "rush", "cover", "move", "run", "sprint", "flank", "push")
            || ContainsAny(snapshot.Sain.SelfDecision, "search", "chase", "rush", "cover", "move", "run", "sprint", "flank", "push");
        bool explicitSainLayer = ContainsAny(snapshot.Sain.ActiveLayer, "combat", "search")
            || (ContainsAny(snapshot.Brain.ActiveLayer, "sain")
                && ContainsAny(snapshot.Brain.ActiveLayer, "combat", "search"));
        bool sainAuthority = before.MovementOwner == VanguardExternalMovementOwner.SainCombat
            || (before.SainCombatLikely && !before.SainCombatStaleNonActionable)
            || explicitSainLayer
            || snapshot.Sain.Searching == true
            || snapshot.Sain.RunningToCover == true
            || (snapshot.Sain.IsInCombat == true && movementSemantic);

        float observedSpeed = Math.Max(before.RealSpeed, Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed));
        bool physicalLocomotion = before.MoverMoving
            || before.EftPathActive
            || snapshot.Movement.HasPath == true
            || observedSpeed >= VanguardGrenadeEmergencyPolicy.ActiveSainLocomotionSpeedMetersPerSecond;

        if (sainAuthority && (movementSemantic || physicalLocomotion))
        {
            reason = "active_sain_locomotion"
                + ":owner=" + before.MovementOwner
                + ":moving=" + Bool(before.MoverMoving)
                + ":path=" + Bool(before.EftPathActive || snapshot.Movement.HasPath == true)
                + ":speed=" + observedSpeed.ToString("0.00", CultureInfo.InvariantCulture)
                + ":sainLayer=" + Safe(snapshot.Sain.ActiveLayer)
                + ":sainAction=" + Safe(snapshot.Sain.CurrentAction)
                + ":combatDecision=" + Safe(snapshot.Sain.CombatDecision);
            return true;
        }

        reason = sainAuthority
            ? "sain_authority_without_active_locomotion"
            : "no_active_sain_authority";
        return false;
    }

    private static bool ContainsAny(string? value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (string needle in needles)
        {
            if (!string.IsNullOrWhiteSpace(needle)
                && value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private static string VectorText(Vector3 value) => value.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + value.y.ToString("0.0", CultureInfo.InvariantCulture) + "," + value.z.ToString("0.0", CultureInfo.InvariantCulture);
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
