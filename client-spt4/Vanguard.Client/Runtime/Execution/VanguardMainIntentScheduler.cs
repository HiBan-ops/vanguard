#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Intents;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.Movement.Brain;
using Vanguard.Client.Runtime.External;
using Vanguard.Client.Runtime.Medical.Execution;
using Vanguard.Client.Runtime.PostLoot;
using Vanguard.Client.Runtime.Loot;
using Vanguard.Client.Runtime.Grenades;
using Vanguard.Client.Raid.Runtime;

// Responsibility: arbitrates competing Operator intents and grants the single primary execution domain for each decision window.
// Flow: Subsystem snapshots/intents are gathered, precedence and lease rules select the active path, and work is dispatched to specialized executors/services.
// Authority boundary: producers publish evidence/intents; the scheduler orders them but does not fabricate threat, medical or loot truth.
// Invariant: higher-safety domains can preempt lower-priority work, and every grant/renewal must remain bounded by current evidence and lease generation.

namespace Vanguard.Client.Runtime.Execution;

/// <summary>
/// Vanguard promotes the previously read-only intent board into a narrow active scheduler.
/// It does not replace medical, combat, SAIN or movement executors in one pass; it acts as
/// the central authority that selects one primary execution window per Operator and authorizes
/// HardReturn starts only when the scored board says the movement intent is the winner.
/// </summary>
internal static class VanguardMainIntentScheduler
{
    public const string StatusTag = "VANGUARD_CORE_DECISION_OK";
    public const string CleanAuthStatusTag = "VANGUARD_CLEAN_AUTH_OK";
    public const string SainWindowAuthorityStatusTag = VanguardPrimaryExecutionContract.SainWindowStatusTag;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, VanguardPrimaryExecutionWindowState> ActiveByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, VanguardSchedulerOutcomeRecord> OutcomeByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> LastDecisionSignatureByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogAtByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> CombatReopenBlockedUntilByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> CombatReopenBlockedTargetByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CombatNoProgressSeriesState> CombatNoProgressSeriesByBotAndTarget = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> CombatAssignmentPendingUntilByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> CombatAssignmentPendingTargetByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DecisionLogInterval = TimeSpan.FromSeconds(2.00d);
    private static readonly TimeSpan ProgressLogInterval = TimeSpan.FromSeconds(2.00d);
    private static readonly TimeSpan TerminalDecisionLogInterval = TimeSpan.FromSeconds(30.00d);
    private static readonly TimeSpan SchedulerTickInterval = TimeSpan.FromSeconds(0.20d);
    private static DateTimeOffset nextSchedulerTickAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset lastSquadReadModelBatchAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            ActiveByBotProfileId.Clear();
            OutcomeByBotProfileId.Clear();
            LastDecisionSignatureByBotProfileId.Clear();
            LastLogAtByKey.Clear();
            CombatReopenBlockedUntilByBotProfileId.Clear();
            CombatReopenBlockedTargetByBotProfileId.Clear();
            CombatNoProgressSeriesByBotAndTarget.Clear();
            CombatAssignmentPendingUntilByBotProfileId.Clear();
            CombatAssignmentPendingTargetByBotProfileId.Clear();
            nextSchedulerTickAtUtc = DateTimeOffset.MinValue;
            lastSquadReadModelBatchAtUtc = DateTimeOffset.MinValue;
        }

        VanguardOrchestratorAuthorityPolicy.ResetForRaidLifecycle(reason);
        VanguardSquadTargetNoProgressQuarantine.ResetForRaidLifecycle(reason);
        bootLogged = false;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_SCHEDULER_RESET reason={Safe(reason)}; activeWindows=0; outcomeMemory=cleared; cleanAuth=true; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
    }

    public static void Tick(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_MAIN_SCHEDULER_BOOT enabled=true; mode=active_minimal; pipeline=snapshot_intents_scoring_scheduler_execution_window_outcome; primaryWindowPerOperator=true; controls=HardReturnStartAuthorization; observes=MedicalLease_CombatYield_FollowRejoin; hardReturnBackend=BigBrain_GoToSomePointData; combatProductivity=true; weakCohesionPreempt=true; noProgressExpiry=true; build={VanguardBuildVersion.BuildLabel}; buildParityRequired=Headless_LocalClient_same_build_SonClient_optional_filtered_if_mismatch; Lifecycle=true; cleanAuth=true; medicalObservedOnce=true; terminalThrottle=true; sainCombatEntryEdgeTriggered=true; combatHeartbeatMutation=false; boundedCombatSegments=true; MultiTargetChain=idempotent_local_then_awareness_scanner_then_group; combatExitOnlyAfterNoLiveContinuation=true; Tag={VanguardMovementAuthorityDoctrine.CombatCohesionAuthorityStatusTag}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
        }

        if (snapshots == null || snapshots.Count == 0)
        {
            return;
        }

        bool refreshSquadReadModel = false;
        DateTimeOffset batchAtUtc = snapshots
            .Where(snapshot => snapshot != null)
            .Select(snapshot => snapshot.CapturedAtUtc)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        lock (Sync)
        {
            if (now < nextSchedulerTickAtUtc)
            {
                return;
            }

            nextSchedulerTickAtUtc = now + SchedulerTickInterval;
            if (batchAtUtc > lastSquadReadModelBatchAtUtc)
            {
                lastSquadReadModelBatchAtUtc = batchAtUtc;
                refreshSquadReadModel = true;
            }
        }

        // The group contact read model is immutable for one snapshot batch. Rebuilding it every
        // Unity frame multiplied coop work without adding target truth. Primary arbitration remains
        // responsive at 5 Hz and target acquisition remains owned by the awareness bridge.
        if (refreshSquadReadModel)
        {
            VanguardCombatAwarenessBridge.RefreshSquadCombatContactReadModel(snapshots, now);
        }

        foreach (var snapshot in snapshots)
        {
            try
            {
                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
                {
                    continue;
                }

                ExpireStaleWindow(snapshot, now);
                bool medicalObserved = ObserveMedicalLease(snapshot, now);
                if (!medicalObserved)
                {
                    ReleaseObservedMedicalIfEnded(snapshot.BotProfileId, now);
                }
                var board = VanguardOperatorIntentDryRunService.BuildBoard(snapshot);
                ObserveBoard(board, now);
                ApplySelectedPrimaryAuthority(board, now);
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(StatusTag,
                    $"VANGUARD_SCHEDULER_TICK_FAILED operator={Safe(snapshot?.OperatorId)}; botProfile={Safe(snapshot?.BotProfileId)}; reason={exception.GetType().Name}:{Safe(exception.Message)}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
            }
        }
    }

    public static bool TryOpenHardReturnPending(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string windowId, out string reason)
    {
        windowId = "none";
        reason = "none";
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            reason = "snapshot_missing";
            return false;
        }

        if (VanguardSquadTravelCohesionExecutor.ShouldOwnTravelRecovery(snapshot, now, out var travelReason))
        {
            reason = "monotonic_travel_corridor_owns_recovery:" + travelReason;
            return false;
        }

        ExpireStaleWindow(snapshot, now);
        var board = VanguardOperatorIntentDryRunService.BuildBoard(snapshot);
        var selected = board.Selected;
        string selectedKind = ClassifyWindowKind(selected);
        ObserveBoard(board, now);

        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var active) && active.IsActive(now))
            {
                if (active.IsHardReturn)
                {
                    reason = "hard_return_window_already_active:" + Safe(active.State);
                    windowId = active.WindowId;
                    return false;
                }

                if (!IsHardReturnMovementIntent(selected))
                {
                    reason = "primary_window_busy:" + Safe(active.WindowKind) + ":" + Safe(active.IntentKey);
                    windowId = active.WindowId;
                    LogThrottled("denyBusy|" + snapshot.BotProfileId + "|" + active.WindowKind, now, DecisionLogInterval,
                        $"VANGUARD_EXECUTION_DENIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requested=HardReturnMovementWindow; reason={Safe(reason)}; selected={Safe(selected.IntentKey)}; active={active.Summary}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
                    return false;
                }

                if (active.IsCloseCohesionMovement && VanguardMovementAuthorityDoctrine.ShouldPreemptWeakCohesionForHardReturn(snapshot, out var preemptReason))
                {
                    ActiveByBotProfileId.Remove(snapshot.BotProfileId);
                    OutcomeByBotProfileId[snapshot.BotProfileId] = new VanguardSchedulerOutcomeRecord
                    {
                        BotProfileId = snapshot.BotProfileId,
                        WindowId = active.WindowId,
                        WindowKind = active.WindowKind,
                        IntentKey = active.IntentKey,
                        Outcome = "Interrupted",
                        Reason = "hard_return_preempt:" + preemptReason,
                        BackendSummary = Safe(active.Summary),
                        RecordedAtUtc = now
                    };
                    VanguardClientDiagnosticsLog.Warning(StatusTag,
                        $"VANGUARD_WEAK_COHESION_WINDOW_PREEMPTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; previous={active.Summary}; selected={Safe(selected.IntentKey)}; reason={Safe(preemptReason)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; tag={VanguardMovementAuthorityDoctrine.CombatCohesionAuthorityStatusTag}; schedulerTag={StatusTag}");
                }
                else
                {
                    reason = "primary_window_busy:" + Safe(active.WindowKind) + ":" + Safe(active.IntentKey);
                    windowId = active.WindowId;
                    LogThrottled("denyBusy|" + snapshot.BotProfileId + "|" + active.WindowKind, now, DecisionLogInterval,
                        $"VANGUARD_EXECUTION_DENIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requested=HardReturnMovementWindow; reason={Safe(reason)}; selected={Safe(selected.IntentKey)}; active={active.Summary}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
                    return false;
                }
            }

            if (!IsHardReturnMovementIntent(selected))
            {
                reason = "selected_intent_not_hard_return:" + Safe(selected.IntentKey) + ":" + Safe(selectedKind);
                LogThrottled("denySelected|" + snapshot.BotProfileId + "|" + selected.IntentKey, now, DecisionLogInterval,
                    $"VANGUARD_EXECUTION_DENIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requested=HardReturnMovementWindow; reason={Safe(reason)}; selected={Safe(selected.IntentKey)}; selectedKind={Safe(selectedKind)}; score={selected.FinalScore:0.00}; candidates={Safe(TopCandidates(board))}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
                return false;
            }

            windowId = "primary_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId);
            var window = new VanguardPrimaryExecutionWindowState
            {
                WindowId = windowId,
                OperatorId = snapshot.OperatorId,
                BotProfileId = snapshot.BotProfileId,
                WindowKind = VanguardPrimaryExecutionWindowKinds.HardReturnMovement,
                State = "PendingExternalPreempt",
                IntentKey = selected.IntentKey,
                Domain = selected.Domain,
                Reason = selected.Reason,
                TargetKey = selected.TargetKey,
                PlanKey = selected.PlanKey,
                NextStep = selected.NextStep,
                Score = selected.FinalScore,
                StartedAtUtc = now,
                MinUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementLeaseMinDurationSeconds),
                MaxUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementLeaseHardMaxSeconds),
                NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementLeaseNoProgressSeconds),
                LastProgressAtUtc = now,
                LastObservedAtUtc = now,
                LastProgressKind = "pending_external_preempt"
            };
            ActiveByBotProfileId[snapshot.BotProfileId] = window;
            reason = "opened:" + selected.IntentKey;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_EXECUTION_STARTED {window.Summary}; phase=pending_external_preempt; candidates={Safe(TopCandidates(board))}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
            return true;
        }
    }


    public static bool TryOpenPathSafeHardReturnFallback(OperatorDecisionSnapshot snapshot, DateTimeOffset now, string fallbackReason, out string windowId, out string reason)
    {
        windowId = "none";
        reason = "none";
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            reason = "snapshot_missing";
            return false;
        }

        if (VanguardSquadTravelCohesionExecutor.ShouldOwnTravelRecovery(snapshot, now, out var travelReason))
        {
            reason = "monotonic_travel_corridor_owns_path_recovery:" + travelReason;
            return false;
        }

        ExpireStaleWindow(snapshot, now);
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var active) && active.IsActive(now))
            {
                if (active.IsHardReturn)
                {
                    reason = "hard_return_window_already_active:" + Safe(active.State);
                    windowId = active.WindowId;
                    LogThrottled("pathFallbackAlreadyActive|" + snapshot.BotProfileId, now, DecisionLogInterval,
                        $"VANGUARD_PATH_FALLBACK_WINDOW_PRESERVED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; active={active.Summary}; fallbackReason={Safe(fallbackReason)}; commandMutation=false; doctrine=canonical_hard_return_executor_owns_restart_and_retarget; physicalTag={VanguardPrimaryExecutionContract.HardReturnPhysicalProgressStatusTag}; tag={VanguardMovementAuthorityDoctrine.MovementCommandQueueStatusTag}; schedulerTag={StatusTag}");
                    return false;
                }

                if (active.IsCloseCohesionMovement && VanguardMovementAuthorityDoctrine.ShouldPreemptWeakCohesionForHardReturn(snapshot, out var preemptReason))
                {
                    ActiveByBotProfileId.Remove(snapshot.BotProfileId);
                    OutcomeByBotProfileId[snapshot.BotProfileId] = new VanguardSchedulerOutcomeRecord
                    {
                        BotProfileId = snapshot.BotProfileId,
                        WindowId = active.WindowId,
                        WindowKind = active.WindowKind,
                        IntentKey = active.IntentKey,
                        Outcome = "Interrupted",
                        Reason = "path_fallback_preempt:" + preemptReason,
                        BackendSummary = Safe(active.Summary),
                        RecordedAtUtc = now
                    };
                    VanguardClientDiagnosticsLog.Warning(StatusTag,
                        $"VANGUARD_PATH_FALLBACK_PREEMPTED_WEAK_WINDOW operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; previous={active.Summary}; fallbackReason={Safe(fallbackReason)}; reason={Safe(preemptReason)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; tag={VanguardMovementAuthorityDoctrine.MovementCommandQueueStatusTag}; schedulerTag={StatusTag}");
                }
                else
                {
                    reason = "primary_window_busy:" + Safe(active.WindowKind) + ":" + Safe(active.IntentKey);
                    windowId = active.WindowId;
                    LogThrottled("pathFallbackBusy|" + snapshot.BotProfileId + "|" + active.WindowKind, now, DecisionLogInterval,
                        $"VANGUARD_PATH_FALLBACK_WINDOW_DENIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; active={active.Summary}; fallbackReason={Safe(fallbackReason)}; tag={VanguardMovementAuthorityDoctrine.MovementCommandQueueStatusTag}; schedulerTag={StatusTag}");
                    return false;
                }
            }

            windowId = "pathfallback_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId);
            var window = new VanguardPrimaryExecutionWindowState
            {
                WindowId = windowId,
                OperatorId = snapshot.OperatorId,
                BotProfileId = snapshot.BotProfileId,
                WindowKind = VanguardPrimaryExecutionWindowKinds.HardReturnMovement,
                State = "PendingPathSafeFallbackCommand",
                IntentKey = "PathSafeHardReturnFallback",
                Domain = "MovementAuthority",
                Reason = fallbackReason,
                TargetKey = "owner_rally_path_safe",
                PlanKey = "path_safe_hard_return_fallback",
                NextStep = "ActionRallyHardReturn",
                Score = 999.0f,
                StartedAtUtc = now,
                MinUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementLeaseMinDurationSeconds),
                MaxUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementLeaseHardMaxSeconds),
                NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementLeaseNoProgressSeconds),
                LastProgressAtUtc = now,
                LastObservedAtUtc = now,
                LastProgressKind = "path_safe_fallback_window_opened"
            };
            ActiveByBotProfileId[snapshot.BotProfileId] = window;
            reason = "opened:path_safe_hard_return_fallback";
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_PATH_FALLBACK_WINDOW_OPENED {window.Summary}; fallbackReason={Safe(fallbackReason)}; doctrine=scheduler_owned_path_fallback_no_direct_tick_reissue; tag={VanguardMovementAuthorityDoctrine.MovementCommandQueueStatusTag}; schedulerTag={StatusTag}");
            return true;
        }
    }



    public static bool TryOpenOrRefreshAuthoringPreview(
        string? operatorId,
        string? botProfileId,
        string liveSessionId,
        string slotId,
        DateTimeOffset now,
        out string windowId,
        out string reason)
    {
        windowId = "none";
        reason = "none";
        string key = Normalize(botProfileId);
        if (string.Equals(key, "none", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(liveSessionId)
            || string.IsNullOrWhiteSpace(slotId))
        {
            reason = "authoring_preview_identity_missing";
            return false;
        }

        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(key, out var active) && active.IsActive(now))
            {
                if (active.IsAuthoringPreview)
                {
                    active.State = "RunningLivePreview";
                    active.TargetKey = slotId;
                    active.PlanKey = liveSessionId;
                    active.LastObservedAtUtc = now;
                    active.LastProgressAtUtc = now;
                    active.LastProgressKind = "authoring_preview_heartbeat";
                    active.MaxUntilUtc = now + TimeSpan.FromSeconds(2.5d);
                    active.NoProgressUntilUtc = now + TimeSpan.FromSeconds(2.5d);
                    ActiveByBotProfileId[key] = active;
                    windowId = active.WindowId;
                    reason = "authoring_preview_refreshed";
                    return true;
                }

                if (active.IsOpportunisticLoot)
                {
                    // Once a bounded CorpseLoot excursion has been admitted from an authored HOLD slot, Tactical Authoring must not
                    // steal the primary window back merely because the authored position still resolves to stationary hold.
                    // Headless assignment remains sticky until loot reaches a real terminal; the normal preview path then reacquires the slot.
                    windowId = active.WindowId;
                    reason = "authoring_preview_yield:opportunistic_loot:" + Safe(active.State);
                    return false;
                }

                bool supersedable = VanguardPrimaryExecutionContract.IsMovementPrimaryKind(active.WindowKind)
                    && !active.IsGrenadeEmergency
                    && !active.IsMedical
                    && !VanguardPrimaryExecutionContract.IsCombatPrimaryKind(active.WindowKind);
                if (!supersedable)
                {
                    windowId = active.WindowId;
                    reason = "authoring_preview_yield:" + Safe(active.WindowKind) + ":" + Safe(active.State);
                    return false;
                }

                ActiveByBotProfileId.Remove(key);
                OutcomeByBotProfileId[key] = new VanguardSchedulerOutcomeRecord
                {
                    BotProfileId = key,
                    WindowId = active.WindowId,
                    WindowKind = active.WindowKind,
                    IntentKey = active.IntentKey,
                    Outcome = "Interrupted",
                    Reason = "explicit_tactical_authoring_preview_supersedes_noncritical_movement",
                    BackendSummary = Safe(active.Summary),
                    RecordedAtUtc = now
                };
            }

            windowId = "authoring_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(key);
            var window = new VanguardPrimaryExecutionWindowState
            {
                WindowId = windowId,
                OperatorId = Normalize(operatorId),
                BotProfileId = key,
                WindowKind = VanguardPrimaryExecutionWindowKinds.AuthoringPreviewMovement,
                State = "RunningLivePreview",
                IntentKey = "TacticalAuthoringLivePreview",
                Domain = "MovementAuthority",
                Reason = "explicit_player_authoring_preview",
                TargetKey = slotId,
                PlanKey = liveSessionId,
                NextStep = "MoveToAuthoredSlot",
                Score = 99f,
                StartedAtUtc = now,
                MinUntilUtc = now,
                MaxUntilUtc = now + TimeSpan.FromSeconds(2.5d),
                NoProgressUntilUtc = now + TimeSpan.FromSeconds(2.5d),
                LastProgressAtUtc = now,
                LastObservedAtUtc = now,
                LastProgressKind = "authoring_preview_open"
            };
            ActiveByBotProfileId[key] = window;
            reason = "authoring_preview_opened";
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TACTICAL_AUTHORING_PREVIEW_WINDOW_OPENED botProfile={Safe(key)}; operator={Safe(operatorId)}; window={Safe(windowId)}; slot={Safe(slotId)}; session={Safe(liveSessionId)}; authority=transient; combatMedicalGrenadePreempt=true");
        return true;
    }

    public static bool TryOpenTacticalReposition(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string windowId, out string reason)
    {
        windowId = "none";
        reason = "none";
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            reason = "snapshot_missing";
            return false;
        }

        ExpireStaleWindow(snapshot, now);
        var board = VanguardOperatorIntentDryRunService.BuildBoard(snapshot);
        var selected = board.Selected;
        string selectedKind = ClassifyWindowKind(selected);
        ObserveBoard(board, now);

        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var active) && active.IsActive(now))
            {
                reason = "primary_window_busy:" + Safe(active.WindowKind) + ":" + Safe(active.IntentKey);
                windowId = active.WindowId;
                LogThrottled("denyBusyTactical|" + snapshot.BotProfileId + "|" + active.WindowKind, now, DecisionLogInterval,
                    $"VANGUARD_TACTICAL_EXECUTION_DENIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requested=TacticalMovementWindow; reason={Safe(reason)}; selected={Safe(selected.IntentKey)}; active={active.Summary}; tacticalTag={VanguardMovementAuthorityDoctrine.TacticalRepositionActiveStatusTag}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
                return false;
            }

            if (!IsTacticalMovementIntent(selected))
            {
                reason = "selected_intent_not_tactical_reposition:" + Safe(selected.IntentKey) + ":" + Safe(selectedKind);
                LogThrottled("denySelectedTactical|" + snapshot.BotProfileId + "|" + selected.IntentKey, now, DecisionLogInterval,
                    $"VANGUARD_TACTICAL_EXECUTION_DENIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requested=TacticalMovementWindow; reason={Safe(reason)}; selected={Safe(selected.IntentKey)}; selectedKind={Safe(selectedKind)}; score={selected.FinalScore:0.00}; candidates={Safe(TopCandidates(board))}; tacticalTag={VanguardMovementAuthorityDoctrine.TacticalRepositionActiveStatusTag}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
                return false;
            }

            windowId = "tactical_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId);
            var window = new VanguardPrimaryExecutionWindowState
            {
                WindowId = windowId,
                OperatorId = snapshot.OperatorId,
                BotProfileId = snapshot.BotProfileId,
                WindowKind = VanguardPrimaryExecutionWindowKinds.TacticalMovement,
                State = "PlanningAnchor",
                IntentKey = selected.IntentKey,
                Domain = selected.Domain,
                Reason = selected.Reason,
                TargetKey = selected.TargetKey,
                PlanKey = selected.PlanKey,
                NextStep = selected.NextStep,
                Score = selected.FinalScore,
                StartedAtUtc = now,
                MinUntilUtc = now + TimeSpan.FromSeconds(2.25d),
                MaxUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TacticalRepositionMaxDurationSeconds),
                NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TacticalRepositionNoProgressSeconds),
                LastProgressAtUtc = now,
                LastObservedAtUtc = now,
                LastProgressKind = "planning_environment_anchor"
            };
            ActiveByBotProfileId[snapshot.BotProfileId] = window;
            reason = "opened:" + selected.IntentKey;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_TACTICAL_EXECUTION_STARTED {window.Summary}; phase=planning_environment_anchor; candidates={Safe(TopCandidates(board))}; tacticalTag={VanguardMovementAuthorityDoctrine.TacticalRepositionActiveStatusTag}; solverTag={VanguardMovementAuthorityDoctrine.TacticalPlacementSolverStatusTag}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
            return true;
        }
    }

    public static bool TryOpenTravelCorridor(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string windowId, out string reason)
    {
        windowId = "none";
        reason = "none";
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            reason = "snapshot_missing";
            return false;
        }

        ExpireStaleWindow(snapshot, now);
        var board = VanguardOperatorIntentDryRunService.BuildBoard(snapshot);
        var selected = board.Selected;
        string selectedKind = ClassifyWindowKind(selected);
        ObserveBoard(board, now);

        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var active) && active.IsActive(now))
            {
                bool supersedableMovementWindow = active.IsCloseCohesionMovement
                    || active.IsTacticalMovement;
                if (!supersedableMovementWindow)
                {
                    reason = "primary_window_busy:" + Safe(active.WindowKind) + ":" + Safe(active.IntentKey);
                    windowId = active.WindowId;
                    return false;
                }

                ActiveByBotProfileId.Remove(snapshot.BotProfileId);
                OutcomeByBotProfileId[snapshot.BotProfileId] = new VanguardSchedulerOutcomeRecord
                {
                    BotProfileId = snapshot.BotProfileId,
                    WindowId = active.WindowId,
                    WindowKind = active.WindowKind,
                    IntentKey = active.IntentKey,
                    Outcome = "Interrupted",
                    Reason = "owner_travel_supersedes_static_movement",
                    BackendSummary = Safe(active.Summary),
                    RecordedAtUtc = now
                };
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"VANGUARD_STATIC_MOVEMENT_WINDOW_SUPERSEDED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; previous={active.Summary}; reason=owner_travel_started; previousKind={Safe(active.WindowKind)}; doctrine=travel_corridor_supersedes_static_movement_and_is_single_movement_authority_while_owner_moves; tag={VanguardSquadTravelRouteMemory.StatusTag}; schedulerTag={StatusTag}");
            }

            string brokerRequest = snapshot.MovementAuthority.BrokerPlan.Contract.RequestKind;
            bool brokerTravel = string.Equals(brokerRequest, VanguardMovementContractPolicy.TravelCohesionFollowThrough, StringComparison.OrdinalIgnoreCase)
                || string.Equals(brokerRequest, VanguardMovementContractPolicy.TacticalVolumeJoin, StringComparison.OrdinalIgnoreCase);
            if (!brokerTravel && !IsCloseCohesionMovementIntent(selected) && !IsHardReturnMovementIntent(selected))
            {
                reason = "selected_intent_not_travel_corridor:" + Safe(selected.IntentKey) + ":" + Safe(selectedKind);
                LogThrottled("denySelectedTravelCorridor|" + snapshot.BotProfileId + "|" + selected.IntentKey, now, DecisionLogInterval,
                    $"VANGUARD_TRAVEL_CORRIDOR_EXECUTION_DENIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; selected={Safe(selected.IntentKey)}; selectedKind={Safe(selectedKind)}; score={selected.FinalScore:0.00}; candidates={Safe(TopCandidates(board))}; doctrine=travel_corridor_accepts_formation_catchup_and_hard_return_intents_under_one_window; tag={VanguardSquadTravelRouteMemory.StatusTag}; schedulerTag={StatusTag}");
                return false;
            }

            windowId = "travelcorridor_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId);
            var window = new VanguardPrimaryExecutionWindowState
            {
                WindowId = windowId,
                OperatorId = snapshot.OperatorId,
                BotProfileId = snapshot.BotProfileId,
                WindowKind = VanguardPrimaryExecutionWindowKinds.CloseCohesionMovement,
                State = "PlanningMonotonicTravelCorridor",
                IntentKey = selected.IntentKey,
                Domain = selected.Domain,
                Reason = selected.Reason,
                TargetKey = selected.TargetKey,
                PlanKey = selected.PlanKey,
                NextStep = selected.NextStep,
                Score = selected.FinalScore,
                StartedAtUtc = now,
                MinUntilUtc = now + TimeSpan.FromSeconds(1.5d),
                MaxUntilUtc = now + TimeSpan.FromSeconds(30.0d),
                NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TravelSchedulerHeartbeatTimeoutSeconds),
                LastProgressAtUtc = now,
                LastObservedAtUtc = now,
                LastProgressKind = "planning_monotonic_travel_corridor"
            };
            ActiveByBotProfileId[snapshot.BotProfileId] = window;
            reason = "opened:" + selected.IntentKey;
            VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
                $"VANGUARD_TRAVEL_CORRIDOR_EXECUTION_STARTED window={Safe(window.WindowId)}; operator={Safe(window.OperatorId)}; botProfile={Safe(window.BotProfileId)}; intent={Safe(window.IntentKey)}; target={Safe(window.TargetKey)}; score={window.Score:0.00}; maxSeconds={(window.MaxUntilUtc - now).TotalSeconds:0.00}; fullWindowPayload=false; tag={VanguardSquadTravelRouteMemory.StatusTag}; schedulerTag={StatusTag}");
            VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                $"VANGUARD_TRAVEL_CORRIDOR_EXECUTION_STARTED_TRACE {window.Summary}; candidates={Safe(TopCandidates(board))}; tag={VanguardSquadTravelRouteMemory.StatusTag}; schedulerTag={StatusTag}");
            return true;
        }
    }

    public static bool RefreshTravelCorridorWindow(string? botProfileId, DateTimeOffset now, string progressKind, string backendSummary, string? expectedWindowId)
    {
        string key = Normalize(botProfileId);
        string expected = Normalize(expectedWindowId);
        lock (Sync)
        {
            if (!ActiveByBotProfileId.TryGetValue(key, out var active))
            {
                return false;
            }

            if (!string.Equals(expected, "none", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(active.WindowId, expected, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!active.IsCloseCohesionMovement
                || active.State.IndexOf("TravelCorridor", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            active.State = "RunningMonotonicTravelCorridor";
            active.LastProgressKind = progressKind;
            active.BackendSummary = backendSummary;
            active.LastProgressAtUtc = now;
            active.LastObservedAtUtc = now;
            active.MaxUntilUtc = now + TimeSpan.FromSeconds(30.0d);
            active.NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TravelSchedulerHeartbeatTimeoutSeconds);
            ActiveByBotProfileId[key] = active;
            return true;
        }
    }

    public static bool TryOpenCloseCohesion(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string windowId, out string reason)
    {
        windowId = "none";
        reason = "none";
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            reason = "snapshot_missing";
            return false;
        }

        if (VanguardSquadTravelCohesionExecutor.HasActiveTravelAuthority(snapshot.BotProfileId))
        {
            reason = "monotonic_travel_corridor_active";
            return false;
        }

        ExpireStaleWindow(snapshot, now);
        var board = VanguardOperatorIntentDryRunService.BuildBoard(snapshot);
        var selected = board.Selected;
        string selectedKind = ClassifyWindowKind(selected);
        ObserveBoard(board, now);

        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var active) && active.IsActive(now))
            {
                reason = "primary_window_busy:" + Safe(active.WindowKind) + ":" + Safe(active.IntentKey);
                windowId = active.WindowId;
                LogThrottled("denyBusyCloseCohesion|" + snapshot.BotProfileId + "|" + active.WindowKind, now, DecisionLogInterval,
                    $"VANGUARD_CLOSE_COHESION_EXECUTION_DENIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requested=CloseCohesionMovementWindow; reason={Safe(reason)}; selected={Safe(selected.IntentKey)}; active={active.Summary}; closeCohesionTag={VanguardMovementAuthorityDoctrine.CloseCohesionStatusTag}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
                return false;
            }

            if (!IsCloseCohesionMovementIntent(selected))
            {
                reason = "selected_intent_not_close_cohesion:" + Safe(selected.IntentKey) + ":" + Safe(selectedKind);
                LogThrottled("denySelectedCloseCohesion|" + snapshot.BotProfileId + "|" + selected.IntentKey, now, DecisionLogInterval,
                    $"VANGUARD_CLOSE_COHESION_EXECUTION_DENIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requested=CloseCohesionMovementWindow; reason={Safe(reason)}; selected={Safe(selected.IntentKey)}; selectedKind={Safe(selectedKind)}; score={selected.FinalScore:0.00}; candidates={Safe(TopCandidates(board))}; closeCohesionTag={VanguardMovementAuthorityDoctrine.CloseCohesionStatusTag}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
                return false;
            }

            double maxDurationSeconds = CloseCohesionWindowMaxSecondsFor(selected.IntentKey);
            double noProgressSeconds = CloseCohesionWindowNoProgressSecondsFor(selected.IntentKey);
            string planningState = CloseCohesionPlanningStateFor(selected.IntentKey);
            string progressKind = CloseCohesionProgressKindFor(selected.IntentKey);

            windowId = "closecohesion_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId);
            var window = new VanguardPrimaryExecutionWindowState
            {
                WindowId = windowId,
                OperatorId = snapshot.OperatorId,
                BotProfileId = snapshot.BotProfileId,
                WindowKind = VanguardPrimaryExecutionWindowKinds.CloseCohesionMovement,
                State = planningState,
                IntentKey = selected.IntentKey,
                Domain = selected.Domain,
                Reason = selected.Reason,
                TargetKey = selected.TargetKey,
                PlanKey = selected.PlanKey,
                NextStep = selected.NextStep,
                Score = selected.FinalScore,
                StartedAtUtc = now,
                MinUntilUtc = now + TimeSpan.FromSeconds(1.75d),
                MaxUntilUtc = now + TimeSpan.FromSeconds(maxDurationSeconds),
                NoProgressUntilUtc = now + TimeSpan.FromSeconds(noProgressSeconds),
                LastProgressAtUtc = now,
                LastObservedAtUtc = now,
                LastProgressKind = progressKind
            };
            ActiveByBotProfileId[snapshot.BotProfileId] = window;
            reason = "opened:" + selected.IntentKey;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_CLOSE_COHESION_EXECUTION_STARTED {window.Summary}; phase=planning_close_anchor; candidates={Safe(TopCandidates(board))}; closeCohesionTag={VanguardMovementAuthorityDoctrine.CloseCohesionStatusTag}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
            return true;
        }
    }


    public static bool TryOpenCorpseLootApproach(OperatorDecisionSnapshot snapshot, DateTimeOffset now, string corpseId, bool allowAuthoringPreviewYield, bool allowTravelCohesionYield, out string windowId, out string reason)
    {
        windowId = "none";
        reason = "none";
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            reason = "snapshot_missing";
            return false;
        }

        ExpireStaleWindow(snapshot, now);
        var board = VanguardOperatorIntentDryRunService.BuildBoard(snapshot);
        VanguardIntentCandidate boardSelected = board.Selected;
        VanguardIntentCandidate? corpseIntent = board.Candidates.FirstOrDefault(candidate =>
            IsCorpseLootApproachIntent(candidate)
            && string.Equals(Normalize(candidate.TargetKey), Normalize(corpseId), StringComparison.OrdinalIgnoreCase));
        ObserveBoard(board, now);
        lock (Sync)
        {
            if (corpseIntent == null)
            {
                reason = "corpse_loot_intent_missing_or_blocked:boardSelected=" + Safe(boardSelected.IntentKey)
                    + ":selectedKind=" + Safe(ClassifyWindowKind(boardSelected))
                    + ":target=" + Safe(boardSelected.TargetKey);
                return false;
            }

            bool boardAlreadySelectedCorpse = ReferenceEquals(boardSelected, corpseIntent)
                || (IsCorpseLootApproachIntent(boardSelected)
                    && string.Equals(Normalize(boardSelected.TargetKey), Normalize(corpseId), StringComparison.OrdinalIgnoreCase));
            if (!boardAlreadySelectedCorpse && !CanYieldToBoundedCorpseLoot(boardSelected))
            {
                reason = "higher_priority_primary_intent_preserved:" + Safe(boardSelected.IntentKey)
                    + ":" + Safe(ClassifyWindowKind(boardSelected))
                    + ":domain=" + Safe(boardSelected.Domain)
                    + ":target=" + Safe(boardSelected.TargetKey);
                return false;
            }

            if (ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var active) && active.IsActive(now))
            {
                if (!CanSupersedeActiveWindowForCorpseLoot(active, allowAuthoringPreviewYield, allowTravelCohesionYield))
                {
                    reason = "primary_window_busy:" + Safe(active.WindowKind) + ":" + Safe(active.IntentKey);
                    windowId = active.WindowId;
                    return false;
                }

                bool authoredHoldYield = active.IsAuthoringPreview;
                bool travelCohesionYield = allowTravelCohesionYield
                    && string.Equals(active.IntentKey, "MovementBrokerTravelCohesionFollowThrough", StringComparison.OrdinalIgnoreCase);
                string supersedeReason = authoredHoldYield
                    ? "bounded_corpse_loot_supersedes_authored_stationary_hold"
                    : travelCohesionYield
                        ? "bounded_corpse_loot_supersedes_stationary_travel_cohesion"
                        : "bounded_corpse_loot_supersedes_noncritical_hold";
                ActiveByBotProfileId.Remove(snapshot.BotProfileId);
                OutcomeByBotProfileId[snapshot.BotProfileId] = new VanguardSchedulerOutcomeRecord
                {
                    BotProfileId = snapshot.BotProfileId,
                    WindowId = active.WindowId,
                    WindowKind = active.WindowKind,
                    IntentKey = active.IntentKey,
                    Outcome = "Interrupted",
                    Reason = supersedeReason,
                    BackendSummary = Safe(active.Summary),
                    RecordedAtUtc = now
                };
                VanguardClientDiagnosticsLog.Operational(VanguardCorpseLootApproachDoctrine.StatusTag, () =>
                    $"VANGUARD_CORPSE_LOOT_NONCRITICAL_WINDOW_SUPERSEDED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; previous={active.Summary}; reason={Safe(supersedeReason)}; authoringHoldYield={Bool(authoredHoldYield)}; travelCohesionYield={Bool(travelCohesionYield)}; combatMedicalPreserved=true");
            }

            VanguardIntentCandidate selected = corpseIntent;
            string arbitration = boardAlreadySelectedCorpse
                ? "board_selected_corpse_loot"
                : "noncritical_domain_yielded:" + Safe(boardSelected.Domain) + ":" + Safe(boardSelected.IntentKey);
            windowId = "corpseloot_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId);
            var window = new VanguardPrimaryExecutionWindowState
            {
                WindowId = windowId,
                OperatorId = snapshot.OperatorId,
                BotProfileId = snapshot.BotProfileId,
                WindowKind = VanguardPrimaryExecutionWindowKinds.CorpseLoot,
                State = "PlanningCorpseApproach",
                IntentKey = selected.IntentKey,
                Domain = selected.Domain,
                Reason = selected.Reason + ";arbitration=" + arbitration,
                TargetKey = selected.TargetKey,
                PlanKey = selected.PlanKey,
                NextStep = selected.NextStep,
                Score = selected.FinalScore,
                StartedAtUtc = now,
                MinUntilUtc = now + TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.MinimumWindowSeconds),
                MaxUntilUtc = now + TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.SchedulerMaximumWindowSeconds),
                NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.NoProgressSeconds + 1.0f),
                LastProgressAtUtc = now,
                LastObservedAtUtc = now,
                LastProgressKind = "planning_corpse_approach"
            };
            ActiveByBotProfileId[snapshot.BotProfileId] = window;
            reason = "opened:" + selected.IntentKey + ":" + arbitration;
            VanguardClientDiagnosticsLog.Operational(VanguardCorpseLootApproachDoctrine.StatusTag, () =>
                $"VANGUARD_CORPSE_LOOT_WINDOW_OPENED {window.Summary}; selected={Safe(selected.IntentKey)}; boardSelected={Safe(boardSelected.IntentKey)}; arbitration={Safe(arbitration)}; target={Safe(corpseId)}; interaction=false; transactions=false");
            return true;
        }
    }


    public static bool TryOpenWorldContainerLootApproach(OperatorDecisionSnapshot snapshot, DateTimeOffset now, string containerId, float score, bool allowAuthoringPreviewYield, bool allowTravelCohesionYield, out string windowId, out string reason)
    {
        windowId = "none";
        reason = "none";
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId)) { reason = "snapshot_missing"; return false; }
        ExpireStaleWindow(snapshot, now);
        var board = VanguardOperatorIntentDryRunService.BuildBoard(snapshot);
        VanguardIntentCandidate selected = board.Selected;
        ObserveBoard(board, now);
        lock (Sync)
        {
            if (!CanYieldToBoundedCorpseLoot(selected))
            {
                reason = "higher_priority_primary_intent_preserved:" + Safe(selected.IntentKey) + ":" + Safe(ClassifyWindowKind(selected)) + ":domain=" + Safe(selected.Domain);
                return false;
            }
            if (ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var active) && active.IsActive(now))
            {
                if (active.IsOpportunisticLoot || !CanSupersedeActiveWindowForCorpseLoot(active, allowAuthoringPreviewYield, allowTravelCohesionYield))
                {
                    reason = "primary_window_busy:" + Safe(active.WindowKind) + ":" + Safe(active.IntentKey);
                    windowId = active.WindowId;
                    return false;
                }
                bool authoredHoldYield = active.IsAuthoringPreview;
                bool travelCohesionYield = allowTravelCohesionYield
                    && string.Equals(active.IntentKey, "MovementBrokerTravelCohesionFollowThrough", StringComparison.OrdinalIgnoreCase);
                string supersedeReason = authoredHoldYield
                    ? "bounded_world_container_loot_supersedes_authored_stationary_hold"
                    : travelCohesionYield
                        ? "bounded_world_container_loot_supersedes_stationary_travel_cohesion"
                        : "bounded_world_container_loot_supersedes_noncritical_hold";
                ActiveByBotProfileId.Remove(snapshot.BotProfileId);
                OutcomeByBotProfileId[snapshot.BotProfileId] = new VanguardSchedulerOutcomeRecord
                {
                    BotProfileId = snapshot.BotProfileId, WindowId = active.WindowId, WindowKind = active.WindowKind,
                    IntentKey = active.IntentKey, Outcome = "Interrupted", Reason = supersedeReason,
                    BackendSummary = Safe(active.Summary), RecordedAtUtc = now
                };
                if (authoredHoldYield)
                {
                    VanguardClientDiagnosticsLog.Operational(
                        VanguardWorldLootContainerApproachDoctrine.StatusTag,
                        () => $"VANGUARD_CONTAINER_LOOT_AUTHORED_HOLD_SUPERSEDED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; container={Safe(containerId)}; previous={active.Summary}; reason={Safe(supersedeReason)}; authoringHoldYield=true; ordinaryLootBoundsPreserved=true; slotReturnReserved=true; combatMedicalPreserved=true");
                }
                if (travelCohesionYield)
                {
                    VanguardClientDiagnosticsLog.Operational(
                        VanguardOpportunisticLootTravelYieldPolicy.StatusTag,
                        () => $"VANGUARD_CONTAINER_LOOT_PRIMARY_WINDOW_SUPERSEDED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; container={Safe(containerId)}; previous={active.Summary}; reason={Safe(supersedeReason)}; travelCohesionYield=true; combatMedicalPreserved=true");
                }
            }
            windowId = "containerloot_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId);
            var window = new VanguardPrimaryExecutionWindowState
            {
                WindowId = windowId, OperatorId = snapshot.OperatorId, BotProfileId = snapshot.BotProfileId,
                WindowKind = VanguardPrimaryExecutionWindowKinds.WorldContainerLoot, State = "PlanningContainerApproach",
                IntentKey = "ApproachAssignedWorldContainer", Domain = "WorldContainerLoot",
                Reason = "assignment_plus_loot004_permission", TargetKey = containerId,
                PlanKey = "bounded_navmesh_container_approach", NextStep = "ApproachOpenThenNativeItemTransaction", Score = score,
                StartedAtUtc = now, MinUntilUtc = now + TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.MinimumWindowSeconds),
                MaxUntilUtc = now + TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.SchedulerMaximumWindowSeconds),
                NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardCorpseLootApproachDoctrine.NoProgressSeconds + 1.0f),
                LastProgressAtUtc = now, LastObservedAtUtc = now, LastProgressKind = "planning_container_approach"
            };
            ActiveByBotProfileId[snapshot.BotProfileId] = window;
            reason = "opened:assigned_world_container";
            return true;
        }
    }

    public static bool MarkWorldContainerLootApproachStarted(string? botProfileId, string leaseId, DateTimeOffset now, string backendSummary, string? expectedWindowId = null)
    {
        string key = Normalize(botProfileId);
        string expected = Normalize(expectedWindowId);
        lock (Sync)
        {
            if (!ActiveByBotProfileId.TryGetValue(key, out var window)
                || (!string.Equals(expected, "none", StringComparison.OrdinalIgnoreCase) && !string.Equals(window.WindowId, expected, StringComparison.OrdinalIgnoreCase))
                || !window.IsWorldContainerLoot) return false;
            window.State = "RunningContainerApproach";
            window.BackendLeaseId = leaseId;
            window.BackendSummary = backendSummary;
            window.LastProgressKind = "world_container_backend_lease_started";
            window.LastProgressAtUtc = now;
            window.LastObservedAtUtc = now;
            ActiveByBotProfileId[key] = window;
            return true;
        }
    }

    public static bool AbortCorpseLootApproachActivation(string? botProfileId, DateTimeOffset now, string reason, out string summary)
    {
        string key = Normalize(botProfileId);
        VanguardPrimaryExecutionWindowState? aborted = null;
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(key, out VanguardPrimaryExecutionWindowState active)
                && active.IsCorpseLoot)
            {
                aborted = active;
                ActiveByBotProfileId.Remove(key);
                OutcomeByBotProfileId[key] = new VanguardSchedulerOutcomeRecord
                {
                    BotProfileId = key,
                    WindowId = active.WindowId,
                    WindowKind = active.WindowKind,
                    IntentKey = active.IntentKey,
                    Outcome = "Failed",
                    Reason = "corpse_loot_activation_aborted:" + Safe(reason),
                    BackendSummary = Safe(active.Summary),
                    RecordedAtUtc = now
                };
            }
        }

        if (aborted == null)
        {
            summary = "corpse_loot_window_not_found";
            return false;
        }

        summary = "aborted:" + Safe(aborted.WindowId) + ":" + Safe(reason);
        VanguardClientDiagnosticsLog.Warning(VanguardCorpseLootApproachDoctrine.StatusTag, () =>
            $"VANGUARD_CORPSE_LOOT_WINDOW_ACTIVATION_ABORTED {aborted.Summary}; reason={Safe(reason)}; claimAndCommandCleanupOwnedByExecutor=true; interactions=false; transactions=false");
        return true;
    }


    public static bool MarkTacticalRepositionStarted(string? botProfileId, string leaseId, DateTimeOffset now, string backendSummary, string? expectedWindowId = null)
    {
        string key = Normalize(botProfileId);
        string expected = Normalize(expectedWindowId);
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(key, out var activeWindow)
                && !string.Equals(expected, "none", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(activeWindow.WindowId, expected, StringComparison.OrdinalIgnoreCase))
            {
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"VANGUARD_FOREIGN_WINDOW_START_IGNORED botProfile={Safe(key)}; expectedWindow={Safe(expected)}; activeWindow={Safe(activeWindow.WindowId)}; activeKind={Safe(activeWindow.WindowKind)}; requested=TacticalMovement; doctrine=only_window_owner_may_mark_started; Tag={SainWindowAuthorityStatusTag}; tag={StatusTag}");
                return false;
            }

            if (!ActiveByBotProfileId.TryGetValue(key, out var window))
            {
                if (!string.Equals(expected, "none", StringComparison.OrdinalIgnoreCase))
                {
                    VanguardClientDiagnosticsLog.Info(StatusTag,
                        $"VANGUARD_EXPECTED_WINDOW_START_IGNORED botProfile={Safe(key)}; expectedWindow={Safe(expected)}; requestedLease={Safe(leaseId)}; reason=expected_window_not_active; doctrine=stale_executor_cannot_recover_or_replace_scheduler_window; Tag={SainWindowAuthorityStatusTag}; tag={StatusTag}");
                    return false;
                }

                window = new VanguardPrimaryExecutionWindowState
                {
                    WindowId = "tactical_recovered_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(key),
                    BotProfileId = key,
                    WindowKind = VanguardPrimaryExecutionWindowKinds.TacticalMovement,
                    IntentKey = "MovementBrokerRecoveredTacticalReposition",
                    Domain = "MovementAuthority",
                    State = "Running",
                    StartedAtUtc = now,
                    MinUntilUtc = now + TimeSpan.FromSeconds(2.25d),
                    MaxUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.TacticalRepositionMaxDurationSeconds)
                };
                ActiveByBotProfileId[key] = window;
            }

            window.State = "Running";
            window.BackendLeaseId = leaseId;
            window.BackendSummary = backendSummary;
            window.LastProgressKind = "tactical_backend_lease_started";
            window.LastProgressAtUtc = now;
            window.LastObservedAtUtc = now;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_TACTICAL_EXECUTION_PROGRESS {window.Summary}; phase=backend_lease_started; tacticalTag={VanguardMovementAuthorityDoctrine.TacticalRepositionActiveStatusTag}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
        }

        return true;
    }


    public static bool MarkCloseCohesionStarted(string? botProfileId, string leaseId, DateTimeOffset now, string backendSummary, string? expectedWindowId = null)
    {
        string key = Normalize(botProfileId);
        string expected = Normalize(expectedWindowId);
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(key, out var activeWindow)
                && !string.Equals(expected, "none", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(activeWindow.WindowId, expected, StringComparison.OrdinalIgnoreCase))
            {
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"VANGUARD_FOREIGN_WINDOW_START_IGNORED botProfile={Safe(key)}; expectedWindow={Safe(expected)}; activeWindow={Safe(activeWindow.WindowId)}; activeKind={Safe(activeWindow.WindowKind)}; requested=CloseCohesionMovement; doctrine=only_window_owner_may_mark_started; Tag={SainWindowAuthorityStatusTag}; tag={StatusTag}");
                return false;
            }

            if (!ActiveByBotProfileId.TryGetValue(key, out var window))
            {
                if (!string.Equals(expected, "none", StringComparison.OrdinalIgnoreCase))
                {
                    VanguardClientDiagnosticsLog.Info(StatusTag,
                        $"VANGUARD_EXPECTED_WINDOW_START_IGNORED botProfile={Safe(key)}; expectedWindow={Safe(expected)}; requestedLease={Safe(leaseId)}; reason=expected_window_not_active; doctrine=stale_executor_cannot_recover_or_replace_scheduler_window; Tag={SainWindowAuthorityStatusTag}; tag={StatusTag}");
                    return false;
                }

                window = new VanguardPrimaryExecutionWindowState
                {
                    WindowId = "closecohesion_recovered_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(key),
                    BotProfileId = key,
                    WindowKind = VanguardPrimaryExecutionWindowKinds.CloseCohesionMovement,
                    IntentKey = "MovementBrokerRecoveredCloseCohesion",
                    Domain = "MovementAuthority",
                    State = "Running",
                    StartedAtUtc = now,
                    MinUntilUtc = now + TimeSpan.FromSeconds(1.75d),
                    MaxUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CloseCohesionMaxDurationSeconds)
                };
                ActiveByBotProfileId[key] = window;
            }

            bool travelCorridorWindow = window.State.IndexOf("TravelCorridor", StringComparison.OrdinalIgnoreCase) >= 0
                || window.WindowId.StartsWith("travelcorridor_", StringComparison.OrdinalIgnoreCase);
            window.State = travelCorridorWindow ? "RunningMonotonicTravelCorridor" : "Running";
            window.BackendLeaseId = leaseId;
            window.BackendSummary = backendSummary;
            window.LastProgressKind = travelCorridorWindow
                ? "monotonic_travel_corridor_backend_lease_started"
                : "close_cohesion_backend_lease_started";
            window.LastProgressAtUtc = now;
            window.LastObservedAtUtc = now;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_CLOSE_COHESION_EXECUTION_PROGRESS {window.Summary}; phase=backend_lease_started; closeCohesionTag={VanguardMovementAuthorityDoctrine.CloseCohesionStatusTag}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
        }

        return true;
    }


    public static bool MarkCorpseLootApproachStarted(string? botProfileId, string leaseId, DateTimeOffset now, string backendSummary, string? expectedWindowId = null)
    {
        string key = Normalize(botProfileId);
        string expected = Normalize(expectedWindowId);
        lock (Sync)
        {
            if (!ActiveByBotProfileId.TryGetValue(key, out var window)
                || (!string.Equals(expected, "none", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(window.WindowId, expected, StringComparison.OrdinalIgnoreCase))
                || !window.IsCorpseLoot)
            {
                return false;
            }

            window.State = "RunningCorpseApproach";
            window.BackendLeaseId = leaseId;
            window.BackendSummary = backendSummary;
            window.LastProgressKind = "corpse_loot_backend_lease_started";
            window.LastProgressAtUtc = now;
            window.LastObservedAtUtc = now;
            ActiveByBotProfileId[key] = window;
            return true;
        }
    }


    public static bool MarkCorpseLootPreflightStarted(string? botProfileId, string leaseId, DateTimeOffset now, string backendSummary, string? expectedWindowId = null)
    {
        string key = Normalize(botProfileId);
        string expected = Normalize(expectedWindowId);
        lock (Sync)
        {
            if (!ActiveByBotProfileId.TryGetValue(key, out var window)
                || (!string.Equals(expected, "none", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(window.WindowId, expected, StringComparison.OrdinalIgnoreCase))
                || !window.IsCorpseLoot
                || !string.Equals(window.BackendLeaseId, leaseId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            window.State = "RunningCorpsePreflight";
            window.BackendSummary = backendSummary;
            window.LastProgressKind = "typed_corpse_transaction_preflight_started";
            window.LastProgressAtUtc = now;
            window.LastObservedAtUtc = now;
            ActiveByBotProfileId[key] = window;
            return true;
        }
    }

    public static bool MarkCorpseLootTransactionStarted(string? botProfileId, string leaseId, DateTimeOffset now, string backendSummary, string? expectedWindowId = null)
    {
        string key = Normalize(botProfileId);
        string expected = Normalize(expectedWindowId);
        lock (Sync)
        {
            if (!ActiveByBotProfileId.TryGetValue(key, out var window)
                || (!string.Equals(expected, "none", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(window.WindowId, expected, StringComparison.OrdinalIgnoreCase))
                || !window.IsCorpseLoot
                || !string.Equals(window.BackendLeaseId, leaseId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(window.State, "RunningCorpsePreflight", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            window.State = "RunningCorpseTransaction";
            window.BackendSummary = backendSummary;
            window.LastProgressKind = "atomic_single_item_corpse_transaction_submitted";
            window.LastProgressAtUtc = now;
            window.LastObservedAtUtc = now;
            ActiveByBotProfileId[key] = window;
            return true;
        }
    }


    public static bool MarkHardReturnStarted(string? botProfileId, string leaseId, DateTimeOffset now, string backendSummary, string? expectedWindowId = null)
    {
        string key = Normalize(botProfileId);
        string expected = Normalize(expectedWindowId);
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(key, out var activeWindow)
                && !string.Equals(expected, "none", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(activeWindow.WindowId, expected, StringComparison.OrdinalIgnoreCase))
            {
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"VANGUARD_FOREIGN_WINDOW_START_IGNORED botProfile={Safe(key)}; expectedWindow={Safe(expected)}; activeWindow={Safe(activeWindow.WindowId)}; activeKind={Safe(activeWindow.WindowKind)}; requested=HardReturnMovement; doctrine=only_window_owner_may_mark_started; Tag={SainWindowAuthorityStatusTag}; tag={StatusTag}");
                return false;
            }

            if (!ActiveByBotProfileId.TryGetValue(key, out var window))
            {
                if (!string.Equals(expected, "none", StringComparison.OrdinalIgnoreCase))
                {
                    VanguardClientDiagnosticsLog.Info(StatusTag,
                        $"VANGUARD_EXPECTED_WINDOW_START_IGNORED botProfile={Safe(key)}; expectedWindow={Safe(expected)}; requestedLease={Safe(leaseId)}; reason=expected_window_not_active; doctrine=stale_executor_cannot_recover_or_replace_scheduler_window; Tag={SainWindowAuthorityStatusTag}; tag={StatusTag}");
                    return false;
                }

                window = new VanguardPrimaryExecutionWindowState
                {
                    WindowId = "primary_recovered_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(key),
                    BotProfileId = key,
                    WindowKind = VanguardPrimaryExecutionWindowKinds.HardReturnMovement,
                    IntentKey = "MovementBrokerRecoveredHardReturn",
                    Domain = "MovementAuthority",
                    State = "Running",
                    StartedAtUtc = now,
                    MinUntilUtc = now,
                    MaxUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.MovementLeaseHardMaxSeconds)
                };
                ActiveByBotProfileId[key] = window;
            }

            window.State = "Running";
            window.BackendLeaseId = leaseId;
            window.BackendSummary = backendSummary;
            window.LastProgressKind = "backend_lease_started";
            window.LastProgressAtUtc = now;
            window.LastObservedAtUtc = now;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_EXECUTION_PROGRESS {window.Summary}; phase=backend_lease_started; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
        }

        return true;
    }

    public static bool TryOpenEmergencyGrenadeEvasion(
        OperatorDecisionSnapshot snapshot,
        VanguardGrenadeHazardDecisionSnapshot hazard,
        DateTimeOffset now,
        out string windowId,
        out string preemptedSummary,
        out string reason)
    {
        windowId = "none";
        preemptedSummary = "none";
        reason = "none";
        if (snapshot == null || !snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.BotProfileId) || hazard == null || !hazard.HasRelevantHazard)
        {
            reason = "snapshot_or_hazard_invalid";
            return false;
        }

        VanguardPrimaryExecutionWindowState? opened = null;
        VanguardPrimaryExecutionWindowState? preempted = null;
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var active) && active.IsActive(now))
            {
                if (active.IsGrenadeEmergency && string.Equals(active.TargetKey, hazard.GrenadeKey, StringComparison.OrdinalIgnoreCase))
                {
                    active.LastObservedAtUtc = now;
                    active.NoProgressUntilUtc = now + TimeSpan.FromSeconds(Math.Max(VanguardGrenadeEmergencyPolicy.NativeStallSeconds, hazard.NativeProbeSeconds));
                    DateTimeOffset requestedAbsolute = now + TimeSpan.FromSeconds(hazard.RecommendedAbsoluteWindowSeconds + VanguardGrenadeEmergencyPolicy.SchedulerTerminalGraceSeconds);
                    if (requestedAbsolute > active.AbsoluteUntilUtc)
                    {
                        active.MaxUntilUtc = requestedAbsolute;
                        active.HardUntilUtc = requestedAbsolute;
                        active.AbsoluteUntilUtc = requestedAbsolute;
                    }
                    active.BackendSummary = hazard.Summary;
                    ActiveByBotProfileId[snapshot.BotProfileId] = active;
                    windowId = active.WindowId;
                    reason = "existing_emergency_refreshed";
                    return true;
                }

                preempted = active;
                preemptedSummary = active.Summary;
                ActiveByBotProfileId.Remove(snapshot.BotProfileId);
                OutcomeByBotProfileId[snapshot.BotProfileId] = new VanguardSchedulerOutcomeRecord
                {
                    BotProfileId = snapshot.BotProfileId,
                    WindowId = active.WindowId,
                    WindowKind = active.WindowKind,
                    IntentKey = active.IntentKey,
                    Outcome = "Interrupted",
                    Reason = "grenade_emergency_preempted:" + hazard.GrenadeKey,
                    BackendSummary = Safe(active.Summary),
                    RecordedAtUtc = now
                };
            }

            string id = "grenade_emergency_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId);
            DateTimeOffset emergencyAbsolute = now + TimeSpan.FromSeconds(hazard.RecommendedAbsoluteWindowSeconds + VanguardGrenadeEmergencyPolicy.SchedulerTerminalGraceSeconds);
            opened = new VanguardPrimaryExecutionWindowState
            {
                WindowId = id,
                OperatorId = snapshot.OperatorId,
                BotProfileId = snapshot.BotProfileId,
                WindowKind = VanguardPrimaryExecutionWindowKinds.EmergencyGrenadeEvasion,
                State = "Admitted",
                IntentKey = VanguardGrenadeEmergencyPolicy.RequestKind,
                Domain = "Survival",
                Reason = hazard.AdmissionReason,
                TargetKey = hazard.GrenadeKey,
                PlanKey = "native_then_navmesh_fallback",
                NextStep = "NativeUpdateByNode",
                Score = 1000f + hazard.RiskScore,
                StartedAtUtc = now,
                MinUntilUtc = now + TimeSpan.FromSeconds(0.10d),
                // The runtime keeps the exact emergency alive through the grenade-specific fuse window.
                // The runtime adds a scheduler-only terminal grace: the executor checks and records its
                // explicit absolute terminal before the scheduler can make the window inactive.
                // Safe distance/cover remains HoldingSafety while the physical grenade is live.
                MaxUntilUtc = emergencyAbsolute,
                HardUntilUtc = emergencyAbsolute,
                AbsoluteUntilUtc = emergencyAbsolute,
                NoProgressUntilUtc = now + TimeSpan.FromSeconds(Math.Max(VanguardGrenadeEmergencyPolicy.NativeStallSeconds, hazard.NativeProbeSeconds)),
                LastProgressAtUtc = now,
                LastObservedAtUtc = now,
                LastProgressKind = "grenade_emergency_admitted",
                BackendSummary = hazard.Summary,
            };
            ActiveByBotProfileId[snapshot.BotProfileId] = opened;
            windowId = id;
            reason = preempted == null ? "emergency_opened" : "emergency_opened_after_preemption";
        }

        if (preempted != null)
        {
            VanguardClientDiagnosticsLog.Operational(VanguardGrenadeEmergencyPolicy.ActivityPreemptedTag, () =>
                $"operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; grenade={Safe(hazard.GrenadeKey)}; preemptedWindow={Safe(preempted.WindowKind)}; preemptedIntent={Safe(preempted.IntentKey)}; preemptedId={Safe(preempted.WindowId)}; outcome=Interrupted; survivalFirst=true; tag={VanguardGrenadeEmergencyPolicy.StatusTag}");
        }
        // C# forbids capturing ref/out/in parameters in lambdas (CS1628). The scheduler has already
        // committed the window id under Sync; copy it to a normal local for deferred diagnostics.
        string admittedWindowId = opened?.WindowId ?? "none";
        VanguardClientDiagnosticsLog.Operational(VanguardGrenadeEmergencyPolicy.AdmittedTag, () =>
            $"operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; window={Safe(admittedWindowId)}; {hazard.Summary}; preempted={Safe(preempted?.WindowKind)}; doctrine=survival_independent_of_source_relation; tag={VanguardGrenadeEmergencyPolicy.StatusTag}");
        return opened != null;
    }

    public static bool TryGetActiveEmergencyWindow(string? botProfileId, DateTimeOffset now, out string windowId, out string grenadeKey, out string summary)
    {
        string key = Normalize(botProfileId);
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(key, out var active) && active.IsActive(now) && active.IsGrenadeEmergency)
            {
                windowId = active.WindowId;
                grenadeKey = active.TargetKey;
                summary = active.Summary;
                return true;
            }
        }
        windowId = "none";
        grenadeKey = "none";
        summary = "none";
        return false;
    }

    public static bool ReportPrimaryProgress(
        string? botProfileId,
        DateTimeOffset now,
        string progressKind,
        string backendSummary = "none",
        string? expectedWindowId = null)
    {
        string key = Normalize(botProfileId);
        string expected = Normalize(expectedWindowId);
        VanguardPrimaryExecutionWindowState? window = null;
        string ignoredReason = "none";
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(key, out var active))
            {
                if (!string.Equals(expected, "none", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(active.WindowId, expected, StringComparison.OrdinalIgnoreCase))
                {
                    ignoredReason = "window_owner_mismatch:expected=" + Safe(expected) + ":active=" + Safe(active.WindowId) + ":kind=" + Safe(active.WindowKind);
                }
                else
                {
                    active.LastProgressKind = progressKind;
                    active.BackendSummary = backendSummary;
                    active.LastProgressAtUtc = now;
                    active.LastObservedAtUtc = now;
                    active.NoProgressUntilUtc = now + TimeSpan.FromSeconds(NoProgressSecondsFor(active));
                    window = active;
                }
            }
            else
            {
                ignoredReason = "no_active_window";
            }
        }

        if (window != null)
        {
            LogThrottled("progress|" + key + "|" + Safe(progressKind), now, ProgressLogInterval,
                $"VANGUARD_EXECUTION_PROGRESS {window.Summary}; phase={Safe(progressKind)}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
            return true;
        }

        LogThrottled("foreignProgress|" + key + "|" + Safe(expected) + "|" + Safe(progressKind), now, ProgressLogInterval,
            $"VANGUARD_FOREIGN_WINDOW_PROGRESS_IGNORED botProfile={Safe(key)}; expectedWindow={Safe(expected)}; progress={Safe(progressKind)}; reason={Safe(ignoredReason)}; backend={Safe(backendSummary)}; doctrine=only_window_owner_may_refresh_progress_or_deadlines; Tag={SainWindowAuthorityStatusTag}; tag={StatusTag}");
        return false;
    }

    public static bool FinishPrimaryWindow(
        string? botProfileId,
        DateTimeOffset now,
        string outcome,
        string reason,
        string backendSummary = "none",
        string? expectedWindowId = null)
    {
        string key = Normalize(botProfileId);
        VanguardPrimaryExecutionWindowState? window = null;
        string expected = Normalize(expectedWindowId);
        string ignoredReason = "none";
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(key, out var active))
            {
                bool hasExpectedOwner = !string.Equals(expected, "none", StringComparison.OrdinalIgnoreCase);
                if (hasExpectedOwner && !string.Equals(active.WindowId, expected, StringComparison.OrdinalIgnoreCase))
                {
                    ignoredReason = "window_owner_mismatch:expected=" + Safe(expected) + ":active=" + Safe(active.WindowId) + ":kind=" + Safe(active.WindowKind);
                }
                else if (!hasExpectedOwner && VanguardPrimaryExecutionContract.IsCombatPrimaryKind(active.WindowKind))
                {
                    // Public subsystem completion must never close the scheduler-owned SAIN window.
                    // Combat termination is handled exclusively by ExpireStaleWindow.
                    ignoredReason = "unowned_finish_cannot_close_combat_window:" + Safe(active.WindowId);
                }
                else
                {
                    window = active;
                    ActiveByBotProfileId.Remove(key);
                }
            }
            else if (!string.Equals(expected, "none", StringComparison.OrdinalIgnoreCase))
            {
                ignoredReason = "expected_window_not_active:" + Safe(expected);
            }

            if (string.Equals(ignoredReason, "none", StringComparison.OrdinalIgnoreCase))
            {
                OutcomeByBotProfileId[key] = new VanguardSchedulerOutcomeRecord
                {
                    BotProfileId = key,
                    WindowId = window?.WindowId ?? Safe(expected),
                    WindowKind = window?.WindowKind ?? "none",
                    IntentKey = window?.IntentKey ?? "none",
                    Outcome = Safe(outcome),
                    Reason = Safe(reason),
                    BackendSummary = Safe(backendSummary),
                    RecordedAtUtc = now
                };
            }
        }

        if (!string.Equals(ignoredReason, "none", StringComparison.OrdinalIgnoreCase))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_FOREIGN_WINDOW_FINISH_IGNORED botProfile={Safe(key)}; expectedWindow={Safe(expected)}; outcome={Safe(outcome)}; reason={Safe(reason)}; ignoredReason={Safe(ignoredReason)}; backend={Safe(backendSummary)}; doctrine=only_window_owner_may_finish; Tag={SainWindowAuthorityStatusTag}; tag={StatusTag}");
            return false;
        }

        string logName = string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase)
            ? "VANGUARD_EXECUTION_COMPLETED"
            : string.Equals(outcome, "Timeout", StringComparison.OrdinalIgnoreCase)
                ? "VANGUARD_EXECUTION_TIMEOUT"
                : "VANGUARD_EXECUTION_FAILED";
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"{logName} window={Safe(window?.WindowId ?? expected)}; botProfile={Safe(key)}; windowKind={Safe(window?.WindowKind)}; intent={Safe(window?.IntentKey)}; outcome={Safe(outcome)}; reason={Safe(reason)}; fullWindowPayload=false; backendPayload=false; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
        VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
            $"VANGUARD_EXECUTION_TERMINAL_TRACE windowSummary={Safe(window?.Summary ?? ("window=" + expected))}; outcome={Safe(outcome)}; reason={Safe(reason)}; backend={Safe(backendSummary)}; tag={StatusTag}");
        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
            $"VANGUARD_OUTCOME_MEMORY_UPDATED botProfile={Safe(key)}; outcome={Safe(outcome)}; reason={Safe(reason)}; window={Safe(window?.WindowKind)}; intent={Safe(window?.IntentKey)}; backendPayload=false; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
        return true;
    }

    public static bool HasBlockingPrimaryWindow(string? botProfileId, DateTimeOffset now, out string reason)
    {
        reason = "none";
        string key = Normalize(botProfileId);
        lock (Sync)
        {
            if (CombatAssignmentPendingUntilByBotProfileId.TryGetValue(key, out var pendingUntil) && pendingUntil <= now)
            {
                CombatAssignmentPendingUntilByBotProfileId.Remove(key);
                CombatAssignmentPendingTargetByBotProfileId.Remove(key);
            }
            // Target qualification/assignment is a sidecar. Until the selected Vanguard assignment is
            // committed and read back from SAIN, it must not
            // block Travel, tactical movement, medical or loot with an empty primary authority.
            if (!ActiveByBotProfileId.TryGetValue(key, out var active) || !active.IsActive(now))
            {
                return false;
            }

            if (VanguardPrimaryExecutionContract.IsMobileMedicalKind(active.WindowKind))
            {
                reason = "mobile_medical_sidecar_not_blocking:" + active.WindowKind + ":" + active.IntentKey;
                return false;
            }

            reason = active.WindowKind + ":" + active.State + ":" + active.IntentKey;
            return true;
        }
    }

    public static bool HasBlockingPrimaryWindowForTravel(string? botProfileId, DateTimeOffset now, out string reason)
    {
        reason = "none";
        string key = Normalize(botProfileId);
        lock (Sync)
        {
            if (CombatAssignmentPendingUntilByBotProfileId.TryGetValue(key, out var pendingUntil) && pendingUntil <= now)
            {
                CombatAssignmentPendingUntilByBotProfileId.Remove(key);
                CombatAssignmentPendingTargetByBotProfileId.Remove(key);
            }
            // Target qualification/assignment is a sidecar. Until the selected Vanguard assignment is
            // committed and read back from SAIN, it must not
            // block Travel, tactical movement, medical or loot with an empty primary authority.
            if (!ActiveByBotProfileId.TryGetValue(key, out var active) || !active.IsActive(now))
            {
                return false;
            }

            if (VanguardPrimaryExecutionContract.IsMobileMedicalKind(active.WindowKind))
            {
                reason = "mobile_medical_sidecar_not_blocking:" + active.WindowKind + ":" + active.IntentKey;
                return false;
            }

            if (active.IsCloseCohesionMovement || active.IsTacticalMovement)
            {
                reason = "supersedable_movement_window:" + active.WindowKind + ":" + active.State + ":" + active.IntentKey;
                return false;
            }

            reason = active.WindowKind + ":" + active.State + ":" + active.IntentKey;
            return true;
        }
    }

    public static bool TryGetActivePrimaryWindowIdentity(string? botProfileId, DateTimeOffset now, out string windowKind, out string intentKey, out string state)
    {
        string key = Normalize(botProfileId);
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(key, out var active) && active.IsActive(now))
            {
                windowKind = active.WindowKind;
                intentKey = active.IntentKey;
                state = active.State;
                return true;
            }
        }

        windowKind = "none";
        intentKey = "none";
        state = "none";
        return false;
    }

    public static bool TryGetActivePrimaryWindow(string? botProfileId, DateTimeOffset now, out string windowKind, out string intentKey, out string state, out string summary)
    {
        string key = Normalize(botProfileId);
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(key, out var active) && active.IsActive(now))
            {
                windowKind = active.WindowKind;
                intentKey = active.IntentKey;
                state = active.State;
                summary = active.Summary;
                return true;
            }
        }

        windowKind = "none";
        intentKey = "none";
        state = "none";
        summary = "none";
        return false;
    }

    /// <summary>
    /// Returns the single target generation committed by the scheduler. Awareness may publish
    /// candidates, but non-local SAIN memory mutations must match this target before they apply.
    /// </summary>
    public static bool TryGetCommittedCombatTarget(string? botProfileId, DateTimeOffset now, out string targetId, out int generation, out string reason)
    {
        string key = Normalize(botProfileId);
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(key, out var active)
                && active.IsActive(now)
                && VanguardPrimaryExecutionContract.IsCombatPrimaryKind(active.WindowKind))
            {
                targetId = Normalize(active.CommittedTargetKey);
                if (string.Equals(targetId, "none", StringComparison.OrdinalIgnoreCase))
                {
                    targetId = Normalize(active.TargetKey);
                }

                if (!string.Equals(targetId, "none", StringComparison.OrdinalIgnoreCase))
                {
                    generation = Math.Max(1, active.TargetGeneration);
                    reason = "scheduler_commit:" + Safe(active.WindowKind) + ":" + Safe(active.CommittedTargetSource);
                    return true;
                }
            }
        }

        targetId = "none";
        generation = 0;
        reason = "no_committed_combat_target";
        return false;
    }

    public static void NotifyCombatTargetApplied(string? botProfileId, string? targetId, string source, DateTimeOffset now, bool verified)
    {
        string key = Normalize(botProfileId);
        string target = Normalize(targetId);
        if (string.Equals(key, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(target, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        VanguardPrimaryExecutionWindowState? updated = null;
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(key, out var active)
                && active.IsActive(now)
                && VanguardPrimaryExecutionContract.IsCombatPrimaryKind(active.WindowKind)
                && string.Equals(Normalize(active.CommittedTargetKey), target, StringComparison.OrdinalIgnoreCase))
            {
                active.LastTargetAppliedAtUtc = now;
                active.TargetApplicationState = verified ? "Verified" : "Applied";
                if (verified)
                {
                    active.LastTargetVerifiedAtUtc = now;
                }
                active.LastProgressKind = verified
                    ? "target_verified:" + Safe(source)
                    : "target_applied:" + Safe(source);
                ActiveByBotProfileId[key] = active;
                updated = active;
            }
        }

        if (updated != null)
        {
            LogThrottled("TargetApply|" + key + "|" + target + "|" + verified, now, ProgressLogInterval,
                $"VANGUARD_TARGET_APPLICATION_STATE {updated.Summary}; source={Safe(source)}; verified={Bool(verified)}; doctrine=awareness_mutation_remains_immediate_scheduler_observes_and_repairs_without_gating_sain; regressionGuardTag={VanguardPrimaryExecutionContract.RegressionGuardStatusTag}; verificationTag={VanguardPrimaryExecutionContract.SainTargetVerificationStatusTag}; schedulerTag={StatusTag}");
        }
    }

    public static bool HasCombatPrimaryWindow(string? botProfileId, DateTimeOffset now, out string reason)
    {
        if (TryGetActivePrimaryWindow(botProfileId, now, out var kind, out var intent, out var state, out var summary)
            && VanguardPrimaryExecutionContract.IsCombatPrimaryKind(kind))
        {
            reason = "combat_primary_active:" + Safe(kind) + ":" + Safe(state) + ":" + Safe(intent) + ":" + Safe(summary);
            return true;
        }

        reason = "no_combat_primary";
        return false;
    }

    public static bool IsSainCombatExecutionProtected(string? botProfileId, DateTimeOffset now, out string reason)
    {
        if (HasCombatPrimaryWindow(botProfileId, now, out reason))
        {
            return true;
        }

        if (VanguardCombatAwarenessBridge.TryResolveVerifiedSainGoalHandoff(
                botProfileId,
                "none",
                now,
                out var verifiedTarget,
                out var verifiedReason))
        {
            reason = "verified_sain_goal_handoff:" + Safe(verifiedTarget) + ":" + Safe(verifiedReason);
            return true;
        }

        string key = Normalize(botProfileId);
        lock (Sync)
        {
            if (CombatAssignmentPendingUntilByBotProfileId.TryGetValue(key, out var pendingUntil) && pendingUntil <= now)
            {
                CombatAssignmentPendingUntilByBotProfileId.Remove(key);
                CombatAssignmentPendingTargetByBotProfileId.Remove(key);
            }
            // Target qualification/assignment is a sidecar. Until the selected Vanguard assignment is
            // committed and read back from SAIN, it must not
            // block Travel, tactical movement, medical or loot with an empty primary authority.
        }

        reason = "no_combat_primary_or_pending_assignment";
        return false;
    }

    public static bool IsCombatRecoveryBackoffActive(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        string target = !string.Equals(Normalize(snapshot.Threat.EnemyId), "none", StringComparison.OrdinalIgnoreCase)
            ? snapshot.Threat.EnemyId
            : !string.Equals(Normalize(snapshot.Awareness.CandidateId), "none", StringComparison.OrdinalIgnoreCase)
                ? snapshot.Awareness.CandidateId
                : snapshot.ThreatScan.CandidateThreatId;
        return IsCombatReopenBlocked(snapshot, target, now, out reason);
    }

    public static void NotifyCombatTargetAssignment(string? botProfileId, string? targetId, string source, DateTimeOffset now)
    {
        string key = Normalize(botProfileId);
        string normalizedTarget = Normalize(targetId);
        if (string.Equals(key, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedTarget, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(key, out OperatorDecisionSnapshot quarantineSnapshot)
            && VanguardSquadTargetNoProgressQuarantine.IsCombatAuthorityBlocked(
                quarantineSnapshot,
                normalizedTarget,
                now,
                out string quarantineReason))
        {
            LogThrottled(
                "AssignmentBlocked|" + key + "|" + normalizedTarget,
                now,
                ProgressLogInterval,
                $"VANGUARD_COMBAT_ASSIGNMENT_BLOCKED operator={Safe(quarantineSnapshot.OperatorId)}; botProfile={Safe(key)}; owner={Safe(quarantineSnapshot.OwnerProfileId)}; target={Safe(normalizedTarget)}; source={Safe(source)}; reason={Safe(quarantineReason)}; mutation=false; doctrine=squad_no_progress_contact_remains_knowledge_only_until_new_local_proof; tag={VanguardSquadTargetNoProgressQuarantine.StatusTag}; schedulerTag={StatusTag}");
            return;
        }

        lock (Sync)
        {
            if (CombatReopenBlockedUntilByBotProfileId.TryGetValue(key, out var blockedUntil))
            {
                CombatReopenBlockedTargetByBotProfileId.TryGetValue(key, out var blockedTarget);
                if (blockedUntil > now && string.Equals(Normalize(blockedTarget), normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    // Do not let repeated awareness of the same failed target recreate a pending
                    // combat lock during the deliberate medical/cohesion recovery backoff.
                    return;
                }

                if (blockedUntil <= now || !string.Equals(Normalize(blockedTarget), normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    CombatReopenBlockedUntilByBotProfileId.Remove(key);
                    CombatReopenBlockedTargetByBotProfileId.Remove(key);
                }
            }

            if (ActiveByBotProfileId.TryGetValue(key, out var active)
                && active.IsActive(now)
                && VanguardPrimaryExecutionContract.IsCombatPrimaryKind(active.WindowKind))
            {
                if (string.Equals(Normalize(active.TargetKey), normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    // Same-target reports are evidence updates, not a new execution generation.
                    // The scheduler observes progress from snapshots and keeps the window bounded.
                    return;
                }

                CombatAssignmentPendingUntilByBotProfileId[key] = now + TimeSpan.FromSeconds(1.75d);
                CombatAssignmentPendingTargetByBotProfileId[key] = normalizedTarget;
                // Vanguard: awareness publishes a candidate, but only the scheduler commits a target
                // generation. This prevents bridge callbacks and a stale board from alternating
                // GoalEnemy every tick. The candidate is consumed on the next scheduler pass.
                active.LastObservedAtUtc = now;
                active.LastProgressKind = "awareness_candidate_pending:" + Safe(source);
                ActiveByBotProfileId[key] = active;
            }
            else
            {
                CombatAssignmentPendingUntilByBotProfileId[key] = now + TimeSpan.FromSeconds(1.75d);
                CombatAssignmentPendingTargetByBotProfileId[key] = normalizedTarget;
            }
        }

    }

    private static void ObserveCommittedTargetVerification(OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot == null || !snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            return;
        }

        VanguardPrimaryExecutionWindowState? activeSnapshot = null;
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var active)
                && active.IsActive(now)
                && VanguardPrimaryExecutionContract.IsCombatPrimaryKind(active.WindowKind))
            {
                activeSnapshot = active;
            }
        }

        if (activeSnapshot == null)
        {
            return;
        }

        string committedTarget = Normalize(activeSnapshot.CommittedTargetKey);
        if (string.Equals(committedTarget, "none", StringComparison.OrdinalIgnoreCase))
        {
            committedTarget = Normalize(activeSnapshot.TargetKey);
        }
        if (string.Equals(committedTarget, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string localTarget = Normalize(snapshot.Threat.EnemyId);
        bool differentLocalDirectTarget = !string.Equals(localTarget, "none", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(localTarget, committedTarget, StringComparison.OrdinalIgnoreCase)
            && VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot);
        if (differentLocalDirectTarget)
        {
            NotifyCombatTargetAssignment(snapshot.BotProfileId, localTarget, "local_direct_target_adoption", now);
            LogThrottled("LocalTargetAdopt|" + snapshot.BotProfileId + "|" + localTarget, now, ProgressLogInterval,
                $"VANGUARD_LOCAL_SAIN_TARGET_PRESERVED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; committed={Safe(committedTarget)}; local={Safe(localTarget)}; mutation=false; doctrine=never_overwrite_a_different_live_local_direct_target_scheduler_adopts_it_next; verificationTag={VanguardPrimaryExecutionContract.SainTargetVerificationStatusTag}; schedulerTag={StatusTag}");
            return;
        }

        // A verified generation does not need reflection readback on every scheduler tick. A local
        // direct target mismatch is handled above; otherwise keep the proven Vanguard combat path free
        // of periodic target-heartbeat work until the scheduler commits a new generation.
        if (string.Equals(activeSnapshot.TargetApplicationState, "Verified", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (activeSnapshot.LastTargetVerificationAttemptAtUtc != DateTimeOffset.MinValue
            && now - activeSnapshot.LastTargetVerificationAttemptAtUtc < TimeSpan.FromSeconds(0.75d))
        {
            return;
        }

        bool freezeLike = IsSainFreezeLike(snapshot);
        bool directPressure = VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot)
            || snapshot.Threat.DirectThreat
            || snapshot.Threat.EnemyVisible == true
            || snapshot.Threat.EnemyCanShoot == true;
        double ageSeconds = Math.Max(0d, (now - activeSnapshot.StartedAtUtc).TotalSeconds);
        bool weaponReadyForRepair = false;
        string weaponRepairReadiness = "not_evaluated";
        if (directPressure
            && freezeLike
            && ageSeconds >= 2.0d
            && VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var repairRecord)
            && repairRecord.BotOwner != null
            && !repairRecord.BotOwner.IsDead)
        {
            var readiness = VanguardPostLootWeaponReadinessReader.Capture(repairRecord.BotOwner);
            weaponReadyForRepair = readiness.WeaponReady
                && !readiness.FirstAidUsing
                && !snapshot.Medical.Actionability.AnyMedicineUsing;
            weaponRepairReadiness = readiness.Summary;
        }

        bool individualAssignmentOrLocalEvidence = VanguardCombatAwarenessBridge.HasIndividualQualifiedAssignmentOrLocalEvidence(snapshot, committedTarget);
        bool allowRepair = activeSnapshot.TargetRepairAttempts < 1
            && !string.Equals(activeSnapshot.TargetApplicationState, "Verified", StringComparison.OrdinalIgnoreCase)
            && directPressure
            && individualAssignmentOrLocalEvidence
            && freezeLike
            && weaponReadyForRepair
            && ageSeconds >= 2.0d;

        bool verified = VanguardCombatAwarenessBridge.TryVerifyOrRepairCommittedTarget(
            snapshot,
            committedTarget,
            Math.Max(1, activeSnapshot.TargetGeneration),
            allowRepair,
            now,
            out var verificationReason);

        bool repairDeferred = allowRepair && VanguardCombatAwarenessBridge.IsTargetApplyDeferredReason(verificationReason);
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var current)
                && current.IsActive(now)
                && VanguardPrimaryExecutionContract.IsCombatPrimaryKind(current.WindowKind)
                && current.TargetGeneration == activeSnapshot.TargetGeneration
                && string.Equals(Normalize(current.CommittedTargetKey), committedTarget, StringComparison.OrdinalIgnoreCase))
            {
                current.LastTargetVerificationAttemptAtUtc = now;
                if (verified)
                {
                    current.TargetApplicationState = "Verified";
                    current.LastTargetAppliedAtUtc = now;
                    current.LastTargetVerifiedAtUtc = now;
                    current.LastProgressKind = "target_verified:" + Safe(verificationReason);
                }
                else if (allowRepair && !repairDeferred)
                {
                    current.TargetRepairAttempts = Math.Min(1, current.TargetRepairAttempts + 1);
                    current.TargetApplicationState = "RepairAttempted";
                    current.LastProgressKind = "target_repair_attempted:" + Safe(verificationReason);
                }
                else if (repairDeferred)
                {
                    current.LastProgressKind = "target_repair_deferred:" + Safe(verificationReason);
                }
                ActiveByBotProfileId[snapshot.BotProfileId] = current;
            }
        }

        if (!verified && allowRepair && !repairDeferred)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardPrimaryExecutionContract.SainTargetVerificationStatusTag,
                $"VANGUARD_TARGET_REPAIR_RESULT operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(committedTarget)}; generation={activeSnapshot.TargetGeneration}; repaired=false; freezeLike={Bool(freezeLike)}; directPressure={Bool(directPressure)}; age={ageSeconds:0.00}; reason={Safe(verificationReason)}; weaponReadiness={Safe(weaponRepairReadiness)}; retryBudgetExhausted=true; doctrine=one_idempotent_repair_only_under_direct_pressure_weapon_ready_no_awareness_gate_no_sain_heartbeat_override; regressionGuardTag={VanguardPrimaryExecutionContract.RegressionGuardStatusTag}; tag={VanguardPrimaryExecutionContract.SainTargetVerificationStatusTag}; schedulerTag={StatusTag}");
        }
        else if (repairDeferred)
        {
            LogThrottled("TargetRepairDeferred|" + snapshot.BotProfileId + "|" + committedTarget, now, ProgressLogInterval,
                $"VANGUARD_TARGET_REPAIR_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(committedTarget)}; generation={activeSnapshot.TargetGeneration}; reason={Safe(verificationReason)}; repairBudgetConsumed=false; mutation=false; tag={VanguardCombatTruthStatusTags.TargetApplyCircuitBreaker}; schedulerTag={StatusTag}");
        }
    }

    private static bool IsSainFreezeLike(OperatorDecisionSnapshot snapshot)
    {
        return Contains(snapshot.Sain.Classification, "freeze")
            || Contains(snapshot.Sain.CurrentAction, "freeze")
            || Contains(snapshot.Sain.CombatDecision, "freeze")
            || Contains(snapshot.Brain.ActiveLayer, "freeze")
            || Contains(snapshot.Brain.Node, "freeze")
            || Contains(snapshot.Brain.Classification, "freeze");
    }

    private static bool Contains(string? value, string needle)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void ApplySelectedPrimaryAuthority(VanguardIntentDryRunBoard board, DateTimeOffset now)
    {
        if (board == null || board.Snapshot == null || board.Selected == null)
        {
            return;
        }

        var snapshot = board.Snapshot;
        if (!snapshot.Alive)
        {
            return;
        }

        if (TryGetActiveEmergencyWindow(snapshot.BotProfileId, now, out _, out _, out _))
        {
            return;
        }

        var selected = board.Selected;
        string selectedKind = ClassifyWindowKind(selected);
        if (!string.Equals(selectedKind, VanguardPrimaryExecutionWindowKinds.SainCombatRelease, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!VanguardCombatAwarenessBridge.TryResolveLocallyAppliedSainTarget(
                snapshot, selected.TargetKey, out var locallyAppliedTarget, out var localTargetReason))
        {
            LogThrottled("CombatDeferred|" + snapshot.BotProfileId + "|" + Safe(selected.TargetKey) + "|" + Safe(localTargetReason), now, DecisionLogInterval,
                $"VANGUARD_COMBAT_WINDOW_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; selectedTarget={Safe(selected.TargetKey)}; localTarget=none; reason={Safe(localTargetReason)}; primaryMutation=false; travelBlocked=false; doctrine=shared_contact_or_scanner_candidate_requests_local_acquisition_but_never_opens_primary_combat_without_live_local_sain_goal; tag={VanguardBuildVersion.CoopAuthorityConvergenceStatusTag}; schedulerTag={StatusTag}");
            return;
        }

        if (IsCombatReopenBlocked(snapshot, locallyAppliedTarget, now, out var reopenBlockReason))
        {
            LogThrottled("CombatReopenBlocked|" + snapshot.BotProfileId + "|" + Safe(locallyAppliedTarget), now, DecisionLogInterval,
                $"VANGUARD_COMBAT_REOPEN_BLOCKED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(locallyAppliedTarget)}; reason={Safe(reopenBlockReason)}; doctrine=same_nonproductive_target_cannot_immediately_reopen_combat; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; schedulerTag={StatusTag}");
            return;
        }

        if (!VanguardOrchestratorAuthorityPolicy.IsCombatAuthority(snapshot, out var authorityReason))
        {
            return;
        }

        VanguardPrimaryExecutionWindowState? window = null;
        bool opened = false;
        bool interrupted = false;
        bool targetChanged = false;
        bool medicalWindowPreserved = false;
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var active) && active.IsActive(now))
            {
                if (!string.Equals(active.WindowKind, VanguardPrimaryExecutionWindowKinds.SainCombatRelease, StringComparison.OrdinalIgnoreCase))
                {
                    if (active.IsMedical && !VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot))
                    {
                        // Once a no-progress combat window has yielded to medical, a repeated
                        // shared/stale contact cannot steal the primary slot back. Only locally
                        // proven direct danger may interrupt an already-running medical action.
                        active.LastObservedAtUtc = now;
                        active.LastProgressKind = "medical_window_preserved_against_non_direct_combat";
                        ActiveByBotProfileId[snapshot.BotProfileId] = active;
                        window = active;
                        medicalWindowPreserved = true;
                    }
                    else
                    {
                        ActiveByBotProfileId.Remove(snapshot.BotProfileId);
                        OutcomeByBotProfileId[snapshot.BotProfileId] = new VanguardSchedulerOutcomeRecord
                        {
                            BotProfileId = snapshot.BotProfileId,
                            WindowId = active.WindowId,
                            WindowKind = active.WindowKind,
                            IntentKey = active.IntentKey,
                            Outcome = "Interrupted",
                            Reason = "combat_authority_preempt:" + authorityReason,
                            BackendSummary = Safe(active.Summary),
                            RecordedAtUtc = now
                        };
                        interrupted = true;
                    }
                }
                else
                {
                    active.State = "RunningSainAuthority";
                    active.LastObservedAtUtc = now;
                    string selectedTarget = Normalize(locallyAppliedTarget);
                    string activeTarget = Normalize(active.TargetKey);
                    targetChanged = !string.Equals(selectedTarget, "none", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(selectedTarget, activeTarget, StringComparison.OrdinalIgnoreCase);
                    // Vanguard: the selected board may still contain the previous snapshot target.
                    // It must never roll back a committed combat target. Target transitions are
                    // resolved atomically in ExpireStaleWindow after liveness and priority checks.
                    if (IsCombatProgressSignal(snapshot, out var progressReason))
                    {
                        active.LastProgressAtUtc = now;
                        active.NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CombatNoProductionCleanupSeconds);
                        active.LastProgressKind = "combat_progress:" + progressReason;
                    }
                    else
                    {
                        active.LastProgressKind = "sain_observed_no_mutation";
                    }

                    active.IntentKey = selected.IntentKey;
                    active.Reason = selected.Reason;
                    active.Score = selected.FinalScore;
                    window = active;
                    ActiveByBotProfileId[snapshot.BotProfileId] = active;
                }
            }

            if (window == null)
            {
                DateTimeOffset absoluteUntil = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CombatProtectedAbsoluteMaxSeconds);
                window = new VanguardPrimaryExecutionWindowState
                {
                    WindowId = "combat_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId),
                    OperatorId = snapshot.OperatorId,
                    BotProfileId = snapshot.BotProfileId,
                    WindowKind = VanguardPrimaryExecutionWindowKinds.SainCombatRelease,
                    State = "RunningSainAuthority",
                    IntentKey = selected.IntentKey,
                    Domain = selected.Domain,
                    Reason = selected.Reason,
                    TargetKey = locallyAppliedTarget,
                    PlanKey = selected.PlanKey,
                    NextStep = selected.NextStep,
                    Score = selected.FinalScore,
                    StartedAtUtc = now,
                    MinUntilUtc = now + TimeSpan.FromSeconds(3.5d),
                    MaxUntilUtc = absoluteUntil,
                    HardUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CombatProtectedSegmentSeconds),
                    AbsoluteUntilUtc = absoluteUntil,
                    NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CombatNoProductionCleanupSeconds),
                    LastProgressAtUtc = now,
                    LastObservedAtUtc = now,
                    LastProgressKind = IsCombatProgressSignal(snapshot, out var startProgressReason)
                        ? "combat_progress_started:" + startProgressReason
                        : "combat_window_started_support_pending",
                    SegmentIndex = 1,
                    TargetGeneration = 1,
                    CommittedTargetKey = Normalize(locallyAppliedTarget),
                    CommittedTargetSource = "local_sain_goal_open:" + Safe(localTargetReason),
                    LastTargetTransitionSignature = "open->" + Normalize(locallyAppliedTarget),
                    LastTargetTransitionAtUtc = now,
                    TargetApplicationState = "Verified",
                    LastTargetAppliedAtUtc = now,
                    LastTargetVerifiedAtUtc = now,
                    LastTargetVerificationAttemptAtUtc = now,
                    TargetRepairAttempts = 0,
                    TargetMissingSinceUtc = DateTimeOffset.MinValue,
                    TargetMissingSnapshotCount = 0,
                    LastTargetLivenessReason = "locally_applied_open:" + Safe(localTargetReason)
                };
                ActiveByBotProfileId[snapshot.BotProfileId] = window;
                CombatReopenBlockedUntilByBotProfileId.Remove(snapshot.BotProfileId);
                CombatReopenBlockedTargetByBotProfileId.Remove(snapshot.BotProfileId);
                CombatAssignmentPendingUntilByBotProfileId.Remove(snapshot.BotProfileId);
                CombatAssignmentPendingTargetByBotProfileId.Remove(snapshot.BotProfileId);
                opened = true;
            }
        }

        if (medicalWindowPreserved)
        {
            LogThrottled("MedicalPreserved|" + snapshot.BotProfileId + "|" + Safe(window?.WindowId), now, ProgressLogInterval,
                $"VANGUARD_MEDICAL_WINDOW_PRESERVED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requestedCombatTarget={Safe(locallyAppliedTarget)}; authorityReason={Safe(authorityReason)}; active={Safe(window?.Summary)}; trueDirectThreat=false; mutation=false; doctrine=shared_or_stale_contact_cannot_reclaim_primary_slot_from_running_medical_after_combat_yield; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; schedulerTag={StatusTag}");
            return;
        }

        if (interrupted)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardOrchestratorAuthorityPolicy.StatusTag,
                $"VANGUARD_PRIMARY_DOMAIN_PREEMPTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; newDomain=Combat; reason={Safe(authorityReason)}; selected={Safe(selected.IntentKey)}; tag={VanguardOrchestratorAuthorityPolicy.StatusTag}; schedulerTag={StatusTag}");
        }

        if (opened)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardOrchestratorAuthorityPolicy.StatusTag,
                $"VANGUARD_SAIN_WINDOW_STARTED {window.Summary}; authorityReason={Safe(authorityReason)}; candidates={Safe(TopCandidates(board))}; doctrine=edge_triggered_entry_then_sain_exclusive_no_heartbeat_mutation; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; schedulerTag={StatusTag}");
        }
        else
        {
            LogThrottled("CombatObserve|" + snapshot.BotProfileId, now, ProgressLogInterval,
                () => $"VANGUARD_SAIN_WINDOW_OBSERVED {window.Summary}; authorityReason={Safe(authorityReason)}; targetChanged={Bool(targetChanged)}; doctrine=maintain_by_observation_only; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; schedulerTag={StatusTag}");
        }

        ObserveCommittedTargetVerification(snapshot, now);

        if (!opened)
        {
            return;
        }

        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record) || record.BotOwner == null || record.BotOwner.IsDead)
        {
            return;
        }

        // Vanguard: entry release is deliberately edge-triggered. The adapter only suspends
        // competing external/Vanguard drivers; it does not cancel hands, reset SAIN shooting,
        // force reload, synthesize UnderFire or recalculate the combat goal every heartbeat.
        VanguardExternalAuthorityAdapter.RequestOrchestratorCombatAuthorityRelease(
            record.BotOwner,
            snapshot,
            selected.IntentKey + ":" + authorityReason,
            TimeSpan.FromSeconds(Math.Max(4.5d, VanguardMovementAuthorityDoctrine.SquadContactSectorAlertSeconds)),
            now);
    }

    private static bool ObserveMedicalLease(OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        if (!VanguardExecutionLeaseStore.TryGetActive(snapshot.BotProfileId, out var medicalLease))
        {
            return false;
        }

        VanguardPrimaryExecutionWindowState? windowForProgress = null;
        bool started = false;
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var grenadeEmergency)
                && grenadeEmergency.IsActive(now)
                && grenadeEmergency.IsGrenadeEmergency)
            {
                grenadeEmergency.LastObservedAtUtc = now;
                ActiveByBotProfileId[snapshot.BotProfileId] = grenadeEmergency;
                return true;
            }

            bool medicalLeaseIsMobile = VanguardPrimaryExecutionContract.IsMobileMedicalKind(medicalLease.WindowKind) || medicalLease.WindowKind.IndexOf("Mobile", StringComparison.OrdinalIgnoreCase) >= 0;
            if (ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var protectedCombat)
                && protectedCombat.IsActive(now)
                && VanguardPrimaryExecutionContract.IsCombatPrimaryKind(protectedCombat.WindowKind))
            {
                protectedCombat.LastObservedAtUtc = now;
                ActiveByBotProfileId[snapshot.BotProfileId] = protectedCombat;
                LogThrottled("medicalLeaseBesideCombat|" + snapshot.BotProfileId + "|" + medicalLease.LeaseId, now, ProgressLogInterval,
                    $"VANGUARD_MEDICAL_LEASE_OBSERVED_BESIDE_COMBAT operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; primary={protectedCombat.Summary}; medicalLease={Safe(medicalLease.LeaseId)}; medicalWindow={Safe(medicalLease.WindowKind)}; mobile={Bool(medicalLeaseIsMobile)}; doctrine=combat_primary_is_never_overwritten_by_observed_medical_lease_executor_handles_sidecar_or_drain_only; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; schedulerTag={StatusTag}");
                return true;
            }

            if (ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var active) && active.IsHardReturn && active.IsActive(now) && !medicalLeaseIsMobile)
            {
                return true;
            }

            if (medicalLeaseIsMobile
                && ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var activePrimary)
                && activePrimary.IsActive(now)
                && !activePrimary.IsMedical)
            {
                activePrimary.LastObservedAtUtc = now;
                ActiveByBotProfileId[snapshot.BotProfileId] = activePrimary;
                windowForProgress = activePrimary;
                LogThrottled("mobileSidecar|" + snapshot.BotProfileId + "|" + medicalLease.LeaseId, now, ProgressLogInterval,
                    $"VANGUARD_MOBILE_MEDICAL_SIDECAR_OBSERVED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; primary={activePrimary.Summary}; sidecarLease={Safe(medicalLease.LeaseId)}; sidecarWindow={Safe(medicalLease.WindowKind)}; sidecarNeed={Safe(medicalLease.MedicalNeed.ToString())}; doctrine=mobile_medical_is_sidecar_not_primary; tag={VanguardPrimaryExecutionContract.StatusTag}; schedulerTag={StatusTag}");
                return true;
            }

            if (ActiveByBotProfileId.TryGetValue(snapshot.BotProfileId, out var existingMedical)
                && existingMedical.IsMedical
                && string.Equals(existingMedical.BackendLeaseId, medicalLease.LeaseId, StringComparison.OrdinalIgnoreCase))
            {
                existingMedical.State = "ObservedRunning";
                existingMedical.LastObservedAtUtc = now;
                existingMedical.LastProgressAtUtc = medicalLease.LastProgressAtUtc;
                existingMedical.LastProgressKind = string.IsNullOrWhiteSpace(medicalLease.LastProgressKind) ? "observed_medical_lease" : medicalLease.LastProgressKind;
                existingMedical.BackendSummary = medicalLease.Summary;
                existingMedical.MaxUntilUtc = medicalLease.MaxUntilUtc;
                existingMedical.NoProgressUntilUtc = medicalLease.NoProgressUntilUtc;
                windowForProgress = existingMedical;
            }
            else
            {
                var window = new VanguardPrimaryExecutionWindowState
                {
                    WindowId = "medical_observed_" + Safe(snapshot.BotProfileId) + "_" + Safe(medicalLease.LeaseId),
                    OperatorId = snapshot.OperatorId,
                    BotProfileId = snapshot.BotProfileId,
                    WindowKind = medicalLease.WindowKind.IndexOf("Mobile", StringComparison.OrdinalIgnoreCase) >= 0
                        ? VanguardPrimaryExecutionWindowKinds.MobileMedical
                        : VanguardPrimaryExecutionWindowKinds.StationaryMedical,
                    State = "ObservedRunning",
                    IntentKey = medicalLease.IntentKey,
                    Domain = "Medical",
                    Reason = "active_vanguard_medical_lease",
                    TargetKey = medicalLease.TargetPart,
                    PlanKey = medicalLease.CooldownKey,
                    NextStep = medicalLease.MedicalNeed.ToString(),
                    Score = 100f,
                    StartedAtUtc = medicalLease.StartedAtUtc,
                    MinUntilUtc = medicalLease.MinUntilUtc,
                    MaxUntilUtc = medicalLease.MaxUntilUtc,
                    NoProgressUntilUtc = medicalLease.NoProgressUntilUtc,
                    LastProgressAtUtc = medicalLease.LastProgressAtUtc,
                    LastObservedAtUtc = now,
                    LastProgressKind = medicalLease.LastProgressKind,
                    BackendLeaseId = medicalLease.LeaseId,
                    BackendSummary = medicalLease.Summary
                };
                ActiveByBotProfileId[snapshot.BotProfileId] = window;
                windowForProgress = window;
                started = true;
            }
        }

        if (windowForProgress != null)
        {
            if (started)
            {
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"VANGUARD_EXECUTION_STARTED {windowForProgress.Summary}; phase=observed_existing_medical_lease; schedulerDoesNotStartMedicalYet=true; cleanAuth=true; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
            }
            else
            {
                LogThrottled("medicalProgress|" + snapshot.BotProfileId + "|" + medicalLease.LeaseId, now, ProgressLogInterval,
                    $"VANGUARD_EXECUTION_PROGRESS {windowForProgress.Summary}; phase=observed_existing_medical_lease_progress; duplicateStartedSuppressed=true; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
            }
        }

        return true;
    }

    private static void ReleaseObservedMedicalIfEnded(string? botProfileId, DateTimeOffset now)
    {
        string key = Normalize(botProfileId);
        VanguardPrimaryExecutionWindowState? released = null;
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(key, out var active) && active.IsMedical)
            {
                released = active;
                ActiveByBotProfileId.Remove(key);
            }
        }

        if (released == null)
        {
            return;
        }

        string outcome = "Interrupted";
        string reason = "observed_medical_lease_ended_without_terminal_record";
        string backendSummary = released.BackendSummary;
        string bridgeState = "safe_interrupted_fallback";
        if (VanguardMedicalExecutionResultBridge.TryConsume(key, released.BackendLeaseId, now, out var terminal))
        {
            bridgeState = "terminal_result_consumed";
            outcome = terminal.Outcome switch
            {
                nameof(VanguardMedicalActionOutcomeKind.Completed) => "Completed",
                nameof(VanguardMedicalActionOutcomeKind.Timeout) => "Timeout",
                nameof(VanguardMedicalActionOutcomeKind.Interrupted) => "Interrupted",
                nameof(VanguardMedicalActionOutcomeKind.Failed) => "Failed",
                _ => terminal.Outcome
            };
            reason = terminal.Reason;
            backendSummary = terminal.BackendSummary;
        }

        lock (Sync)
        {
            OutcomeByBotProfileId[key] = new VanguardSchedulerOutcomeRecord
            {
                BotProfileId = key,
                WindowId = released.WindowId,
                WindowKind = released.WindowKind,
                IntentKey = released.IntentKey,
                Outcome = outcome,
                Reason = reason,
                BackendSummary = backendSummary,
                RecordedAtUtc = now
            };
        }

        string logKind = string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase)
            ? "VANGUARD_EXECUTION_COMPLETED"
            : string.Equals(outcome, "Timeout", StringComparison.OrdinalIgnoreCase)
                ? "VANGUARD_EXECUTION_TIMEOUT"
                : "VANGUARD_EXECUTION_FAILED";
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"{logKind} window={Safe(released.WindowId)}; botProfile={Safe(key)}; windowKind={Safe(released.WindowKind)}; intent={Safe(released.IntentKey)}; outcome={Safe(outcome)}; reason={Safe(reason)}; bridge={Safe(bridgeState)}; fullWindowPayload=false; backendPayload=false; outcomeBridgeTag={VanguardMedicalExecutionResultBridge.StatusTag}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
        VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
            $"VANGUARD_MEDICAL_EXECUTION_TERMINAL_TRACE windowSummary={Safe(released.Summary)}; outcome={Safe(outcome)}; reason={Safe(reason)}; backend={Safe(backendSummary)}; bridge={Safe(bridgeState)}; tag={StatusTag}");
        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
            $"VANGUARD_OUTCOME_MEMORY_UPDATED botProfile={Safe(key)}; outcome={Safe(outcome)}; reason={Safe(reason)}; window={Safe(released.WindowKind)}; intent={Safe(released.IntentKey)}; bridge={Safe(bridgeState)}; backendPayload=false; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
    }

    private static void ObserveBoard(VanguardIntentDryRunBoard board, DateTimeOffset now)
    {
        if (board == null || board.Snapshot == null)
        {
            return;
        }

        var snapshot = board.Snapshot;
        var selected = board.Selected;
        string kind = ClassifyWindowKind(selected);
        string signature = selected.IntentKey + "|" + selected.FinalScore.ToString("0.00", CultureInfo.InvariantCulture) + "|" + selected.Gate + "|" + kind + "|" + board.ExecutionWindow.Signature;
        bool changed;
        lock (Sync)
        {
            changed = !LastDecisionSignatureByBotProfileId.TryGetValue(snapshot.BotProfileId, out var last) || !string.Equals(last, signature, StringComparison.Ordinal);
            if (changed)
            {
                LastDecisionSignatureByBotProfileId[snapshot.BotProfileId] = signature;
            }
        }

        bool terminalDead = !snapshot.Alive && string.Equals(selected.IntentKey, "ObserveDeadOperator", StringComparison.OrdinalIgnoreCase);
        TimeSpan selectedInterval = terminalDead ? TerminalDecisionLogInterval : DecisionLogInterval;
        bool selectedDue = ShouldLog((terminalDead ? "terminalSelected|" : "selected|") + snapshot.BotProfileId, now, selectedInterval);
        if ((!terminalDead && changed) || selectedDue)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_INTENT_SELECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; selected={Safe(selected.IntentKey)}; domain={Safe(selected.Domain)}; score={selected.FinalScore:0.00}; gate={Safe(selected.Gate)}; reason={Safe(selected.Reason)}; windowKind={Safe(kind)}; candidates={Safe(TopCandidates(board))}; active={Safe(ActiveSummary(snapshot.BotProfileId, now))}; terminalThrottle={Bool(terminalDead)}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
        }

        TimeSpan snapshotInterval = terminalDead ? TerminalDecisionLogInterval : TimeSpan.FromSeconds(5.0d);
        if (ShouldLog((terminalDead ? "terminalSnapshot|" : "snapshot|") + snapshot.BotProfileId, now, snapshotInterval))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_SNAPSHOT_SUMMARY operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; alive={Bool(snapshot.Alive)}; selected={Safe(selected.IntentKey)}; windowKind={Safe(kind)}; bubble={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; threat={Safe(snapshot.Threat.Classification)}; grenade={Safe(snapshot.GrenadeHazard.DecisionSignature)}; medical={Safe(snapshot.Medical.Classification)}; moveOwner={Safe(snapshot.MovementAuthority.CurrentAuthority)}; hardOutside={Bool(snapshot.MovementAuthority.HardOutsideBubble)}; active={Safe(ActiveSummary(snapshot.BotProfileId, now))}; terminalThrottle={Bool(terminalDead)}; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
        }
    }

    private static void ExpireStaleWindow(OperatorDecisionSnapshot? snapshot, DateTimeOffset now)
    {
        string key = Normalize(snapshot?.BotProfileId);
        if (string.Equals(key, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        VanguardPrimaryExecutionWindowState? expired = null;
        VanguardPrimaryExecutionWindowState? renewed = null;
        string timeoutReason = "none";
        bool combatNoProductionCleanup = false;
        string combatReason = "none";
        float combatReopenBackoffSeconds = VanguardMovementAuthorityDoctrine.CombatNoProductionReopenBackoffSeconds;

        lock (Sync)
        {
            if (!ActiveByBotProfileId.TryGetValue(key, out var active))
            {
                return;
            }

            if (VanguardPrimaryExecutionContract.ShouldTerminateWindowForMissingOrDeadSnapshot(snapshot, active, out var snapshotTerminalReason))
            {
                expired = active;
                timeoutReason = "TerminalSnapshot:" + snapshotTerminalReason;
                ActiveByBotProfileId.Remove(key);
            }
            else if (active.IsGrenadeEmergency)
            {
                // grenade subsystem owns native/fallback no-progress and terminal classification. The generic
                // scheduler must not expire the emergency between native probe and fallback replan.
                if (active.AbsoluteUntilUtc != DateTimeOffset.MinValue && active.AbsoluteUntilUtc <= now)
                {
                    expired = active;
                    timeoutReason = "GrenadeEmergencyAbsoluteWindowExpired";
                    ActiveByBotProfileId.Remove(key);
                }
                else
                {
                    active.LastObservedAtUtc = now;
                    ActiveByBotProfileId[key] = active;
                    return;
                }
            }
            else if (active.IsMedical)
            {
                active.LastObservedAtUtc = now;
                ActiveByBotProfileId[key] = active;
                return;
            }
            else if (VanguardPrimaryExecutionContract.IsCombatPrimaryKind(active.WindowKind) && snapshot != null)
            {
                bool productive = IsCombatProgressSignal(snapshot, out var progressReason);
                if (productive)
                {
                    active.LastProgressAtUtc = now;
                    active.NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CombatNoProductionCleanupSeconds);
                    active.LastProgressKind = "combat_progress:" + progressReason;
                }

                bool absoluteExpired = active.AbsoluteUntilUtc != DateTimeOffset.MinValue && active.AbsoluteUntilUtc <= now;
                bool noProgressExpired = active.NoProgressUntilUtc != DateTimeOffset.MinValue
                    && active.NoProgressUntilUtc <= now
                    && (active.MinUntilUtc == DateTimeOffset.MinValue || active.MinUntilUtc <= now);
                bool hardSegmentExpired = active.HardUntilUtc != DateTimeOffset.MinValue && active.HardUntilUtc <= now;
                string activeTarget = Normalize(active.TargetKey);
                bool trueDirectThreat = HasFreshDirectCombatPressure(snapshot);
                if ((productive || trueDirectThreat) && !string.Equals(activeTarget, "none", StringComparison.OrdinalIgnoreCase))
                {
                    // A genuine productive/direct episode breaks the consecutive isolated
                    // no-progress series. A future stale episode starts again at level one.
                    CombatNoProgressSeriesByBotAndTarget.Remove(key + "|" + activeTarget);
                }
                if (active.PreviousTargetRetryAfterUtc != DateTimeOffset.MinValue && active.PreviousTargetRetryAfterUtc <= now)
                {
                    active.PreviousTargetKey = "none";
                    active.PreviousTargetRetryAfterUtc = DateTimeOffset.MinValue;
                }
                bool worldTargetLive = VanguardCombatAwarenessBridge.IsLiveCombatTarget(activeTarget, out var worldTargetLiveReason);
                bool snapshotTargetEvidence = HasFreshSnapshotEvidenceForTarget(snapshot, activeTarget, out var snapshotTargetEvidenceReason);
                bool localGoalResolved = VanguardCombatAwarenessBridge.TryResolveLocallyAppliedSainTarget(
                    snapshot, activeTarget, out var localAppliedTarget, out var localGoalReason);
                bool activeTargetLocallyApplied = localGoalResolved
                    && string.Equals(Normalize(localAppliedTarget), activeTarget, StringComparison.OrdinalIgnoreCase);
                bool differentLocallyAppliedTarget = localGoalResolved
                    && !string.Equals(Normalize(localAppliedTarget), "none", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Normalize(localAppliedTarget), activeTarget, StringComparison.OrdinalIgnoreCase);

                // A world-live or shared/snapshot contact remains useful awareness evidence, but it
                // is not sufficient to retain primary combat authority. The active generation must
                // continue to be the live hostile target actually installed in this Operator's own
                // SAIN GoalEnemy. A short debounce absorbs readback transitions without creating a
                // long combat-without-combat window.
                if (activeTargetLocallyApplied)
                {
                    active.TargetMissingSinceUtc = DateTimeOffset.MinValue;
                    active.TargetMissingSnapshotCount = 0;
                    active.LastTargetLivenessReason = "local_sain_goal_verified:" + Safe(localGoalReason);
                }
                else
                {
                    if (active.TargetMissingSinceUtc == DateTimeOffset.MinValue)
                    {
                        active.TargetMissingSinceUtc = now;
                        active.TargetMissingSnapshotCount = 1;
                    }
                    else
                    {
                        active.TargetMissingSnapshotCount = Math.Max(1, active.TargetMissingSnapshotCount + 1);
                    }
                    active.LastTargetLivenessReason = "local_sain_goal_unconfirmed:" + Safe(localGoalReason)
                        + ":world=" + Safe(worldTargetLiveReason)
                        + ":snapshot=" + Safe(snapshotTargetEvidenceReason);
                }

                double missingSeconds = active.TargetMissingSinceUtc == DateTimeOffset.MinValue
                    ? 0d
                    : Math.Max(0d, (now - active.TargetMissingSinceUtc).TotalSeconds);
                double windowAgeSeconds = Math.Max(0d, (now - active.StartedAtUtc).TotalSeconds);
                bool livenessDebounceActive = !activeTargetLocallyApplied
                    && (windowAgeSeconds < 0.75d || active.TargetMissingSnapshotCount < 2 || missingSeconds < 0.65d);
                bool activeTargetLive = activeTargetLocallyApplied || livenessDebounceActive;
                string activeTargetLiveReason = activeTargetLocallyApplied
                    ? "local_sain_goal_verified:" + Safe(localGoalReason)
                    : livenessDebounceActive
                        ? "local_goal_debounce:count=" + active.TargetMissingSnapshotCount.ToString(CultureInfo.InvariantCulture)
                            + ":seconds=" + missingSeconds.ToString("0.00", CultureInfo.InvariantCulture)
                            + ":source=" + Safe(localGoalReason)
                            + ":world=" + Bool(worldTargetLive)
                            + ":snapshotEvidence=" + Bool(snapshotTargetEvidence)
                        : "local_goal_confirmed_missing:count=" + active.TargetMissingSnapshotCount.ToString(CultureInfo.InvariantCulture)
                            + ":seconds=" + missingSeconds.ToString("0.00", CultureInfo.InvariantCulture)
                            + ":source=" + Safe(localGoalReason)
                            + ":world=" + Bool(worldTargetLive)
                            + ":snapshotEvidence=" + Bool(snapshotTargetEvidence);
                if (!activeTargetLive)
                {
                    VanguardCombatAwarenessBridge.InvalidateSquadCombatTarget(snapshot.OwnerProfileId, activeTarget, now, "scheduler_local_target_authority_lost:" + activeTargetLiveReason);
                }

                bool localAppliedSwitch = differentLocallyAppliedTarget
                    && now - active.LastTargetTransitionAtUtc >= TimeSpan.FromSeconds(0.65d);
                bool shouldSeekContinuation = !activeTargetLive || localAppliedSwitch || noProgressExpired || absoluteExpired;
                string continuationTarget = "none";
                string continuationSource = "none";
                string continuationReason = shouldSeekContinuation ? "no_continuation_found" : "continuation_not_required";
                bool continuationFound = false;
                if (shouldSeekContinuation && localAppliedSwitch)
                {
                    continuationTarget = Normalize(localAppliedTarget);
                    continuationSource = "local_sain_goal_readback";
                    continuationReason = "different_live_locally_applied_target:" + Safe(localGoalReason);
                    continuationFound = true;
                }
                else if (shouldSeekContinuation)
                {
                    continuationFound = VanguardCombatAwarenessBridge.TryResolveCombatContinuationTarget(
                        snapshot,
                        activeTarget,
                        active.PreviousTargetRetryAfterUtc > now ? active.PreviousTargetKey : "none",
                        now,
                        out continuationTarget,
                        out continuationSource,
                        out continuationReason);
                }
                string locallyAppliedContinuation = "none";
                string continuationApplyReason = "continuation_not_checked";
                if (continuationFound)
                {
                    if (!VanguardCombatAwarenessBridge.TryResolveLocallyAppliedSainTarget(
                            snapshot, continuationTarget, out locallyAppliedContinuation, out continuationApplyReason))
                    {
                        LogThrottled("ContinuationDeferred|" + snapshot.BotProfileId + "|" + Safe(continuationTarget), now, DecisionLogInterval,
                            $"VANGUARD_COMBAT_CONTINUATION_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; activeTarget={Safe(activeTarget)}; candidate={Safe(continuationTarget)}; source={Safe(continuationSource)}; reason={Safe(continuationApplyReason)}; targetMutation=false; primaryExtension=false; travelPendingBlock=false; doctrine=next_target_must_be_live_and_locally_applied_before_primary_generation_changes; tag={VanguardBuildVersion.CoopAuthorityConvergenceStatusTag}; schedulerTag={StatusTag}");
                        continuationFound = false;
                        continuationReason = "candidate_not_locally_applied:" + Safe(continuationApplyReason);
                    }
                    else
                    {
                        continuationTarget = locallyAppliedContinuation;
                    }
                }

                if (continuationFound)
                {
                    string nextTarget = Normalize(continuationTarget);
                    bool targetChanged = !string.Equals(nextTarget, activeTarget, StringComparison.OrdinalIgnoreCase);
                    string transitionSignature = activeTarget + "->" + nextTarget + ":" + Normalize(continuationSource);
                    bool duplicateTransition = !targetChanged
                        || (string.Equals(active.LastTargetTransitionSignature, transitionSignature, StringComparison.OrdinalIgnoreCase)
                            && now - active.LastTargetTransitionAtUtc < TimeSpan.FromSeconds(4.0d));

                    if (duplicateTransition)
                    {
                        if (!activeTargetLive)
                        {
                            expired = active;
                            timeoutReason = "CombatTargetsResolved";
                            combatReason = "duplicate_or_same_continuation_did_not_replace_dead_target";
                            ActiveByBotProfileId.Remove(key);
                        }
                        else if ((noProgressExpired || absoluteExpired) && !productive && !trueDirectThreat)
                        {
                            expired = active;
                            timeoutReason = "CombatNoProgress";
                            combatNoProductionCleanup = true;
                            combatReason = "idempotent_same_target_no_progress";
                            ActiveByBotProfileId.Remove(key);
                            combatReopenBackoffSeconds = SetCombatReopenBackoff(snapshot, activeTarget, now, trueDirectThreat);
                            CombatReopenBlockedUntilByBotProfileId[key] = now + TimeSpan.FromSeconds(combatReopenBackoffSeconds);
                            CombatReopenBlockedTargetByBotProfileId[key] = activeTarget;
                        }
                        else
                        {
                            active.LastObservedAtUtc = now;
                            active.LastProgressKind = "combat_chain_idempotent_hold:" + Safe(continuationSource);
                            ActiveByBotProfileId[key] = active;
                        }
                    }
                    else
                    {
                        active.PreviousTargetKey = activeTarget;
                        active.PreviousTargetRetryAfterUtc = now + TimeSpan.FromSeconds(noProgressExpired ? 12.0d : 3.0d);
                        if (absoluteExpired)
                        {
                            RollCombatWindow(active, now, nextTarget, targetChanged,
                                "bounded_combat_chain_roll:" + Safe(continuationSource) + ":" + Safe(continuationReason));
                        }
                        else
                        {
                            active.TargetKey = nextTarget;
                            active.TargetGeneration = Math.Max(1, active.TargetGeneration + 1);
                            active.LastProgressAtUtc = now;
                            active.NoProgressUntilUtc = Min(active.AbsoluteUntilUtc,
                                now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CombatNoProductionCleanupSeconds));
                            active.HardUntilUtc = Min(active.AbsoluteUntilUtc,
                                now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CombatTargetRefreshExtensionSeconds));
                            active.LastObservedAtUtc = now;
                            active.LastProgressKind = "combat_target_chained:" + Safe(continuationSource);
                            // Every committed target generation owns its own verification state.
                            // Carrying a previous generation's Verified flag would suppress readback
                            // and repair for the new enemy, recreating the runtime committed-but-frozen gap.
                            active.TargetApplicationState = "Verified";
                            active.LastTargetAppliedAtUtc = now;
                            active.LastTargetVerifiedAtUtc = now;
                            active.LastTargetVerificationAttemptAtUtc = now;
                            active.TargetRepairAttempts = 0;
                            active.TargetMissingSinceUtc = DateTimeOffset.MinValue;
                            active.TargetMissingSnapshotCount = 0;
                            active.LastTargetLivenessReason = "target_generation_changed_after_local_sain_readback";
                        }

                        active.CommittedTargetKey = nextTarget;
                        active.CommittedTargetSource = "locally_verified_continuation:" + Normalize(continuationSource);
                        active.LastTargetTransitionSignature = transitionSignature;
                        active.LastTargetTransitionAtUtc = now;
                        CombatAssignmentPendingUntilByBotProfileId.Remove(key);
                        CombatAssignmentPendingTargetByBotProfileId.Remove(key);
                        CombatReopenBlockedUntilByBotProfileId.Remove(key);
                        CombatReopenBlockedTargetByBotProfileId.Remove(key);
                        ActiveByBotProfileId[key] = active;
                        renewed = active;
                        combatReason = "target_chain:" + Safe(activeTarget) + "->" + Safe(nextTarget) + ":" + Safe(continuationSource) + ":" + Safe(continuationReason);
                    }
                }
                else if (!activeTargetLive)
                {
                    expired = active;
                    timeoutReason = "CombatTargetsResolved";
                    combatReason = "active_target_dead_or_missing_and_no_local_scan_or_group_continuation:" + Safe(activeTargetLiveReason);
                    ActiveByBotProfileId.Remove(key);
                    CombatReopenBlockedUntilByBotProfileId.Remove(key);
                    CombatReopenBlockedTargetByBotProfileId.Remove(key);
                }
                else if (absoluteExpired)
                {
                    if (productive)
                    {
                        RollCombatWindow(active, now, activeTarget, targetChanged: false,
                            "same_target_productive_absolute_roll:" + Safe(progressReason));
                        ActiveByBotProfileId[key] = active;
                        renewed = active;
                        combatReason = active.LastProgressKind;
                    }
                    else
                    {
                        expired = active;
                        timeoutReason = trueDirectThreat ? "CombatAbsoluteNoProgress" : "CombatAbsoluteWindowExpired";
                        combatNoProductionCleanup = true;
                        combatReason = "absolute_cap_reached_without_productive_signal_or_alternate_target";
                        ActiveByBotProfileId.Remove(key);
                        combatReopenBackoffSeconds = SetCombatReopenBackoff(snapshot, activeTarget, now, trueDirectThreat);
                        CombatReopenBlockedUntilByBotProfileId[key] = now + TimeSpan.FromSeconds(combatReopenBackoffSeconds);
                        CombatReopenBlockedTargetByBotProfileId[key] = activeTarget;
                    }
                }
                else if (noProgressExpired && !productive && !trueDirectThreat)
                {
                    expired = active;
                    timeoutReason = "CombatNoProgress";
                    combatNoProductionCleanup = true;
                    combatReason = "no_productive_signal_for_" + VanguardMovementAuthorityDoctrine.CombatNoProductionCleanupSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s_and_no_alternate_target";
                    ActiveByBotProfileId.Remove(key);
                    combatReopenBackoffSeconds = SetCombatReopenBackoff(snapshot, Normalize(active.TargetKey), now, trueDirectThreat);
                    CombatReopenBlockedUntilByBotProfileId[key] = now + TimeSpan.FromSeconds(combatReopenBackoffSeconds);
                    CombatReopenBlockedTargetByBotProfileId[key] = Normalize(active.TargetKey);
                }
                else if (noProgressExpired && !productive && trueDirectThreat)
                {
                    active.NoProgressUntilUtc = Min(
                        active.AbsoluteUntilUtc,
                        now + TimeSpan.FromSeconds(Math.Min(8.0f, VanguardMovementAuthorityDoctrine.CombatNoProductionCleanupSeconds)));
                    active.HardUntilUtc = Min(
                        active.AbsoluteUntilUtc,
                        now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CombatTargetRefreshExtensionSeconds));
                    active.LastObservedAtUtc = now;
                    active.LastProgressKind = "no_progress_deferred_true_direct_threat";
                    ActiveByBotProfileId[key] = active;
                    renewed = active;
                    combatReason = active.LastProgressKind;
                }
                else if (hardSegmentExpired)
                {
                    if (productive || trueDirectThreat)
                    {
                        active.SegmentIndex = Math.Max(1, active.SegmentIndex + 1);
                        active.HardUntilUtc = Min(active.AbsoluteUntilUtc, now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CombatProtectedSegmentSeconds));
                        active.LastObservedAtUtc = now;
                        active.LastProgressKind = productive
                            ? "segment_renewed_productive:" + progressReason
                            : "segment_renewed_true_direct_threat";
                        ActiveByBotProfileId[key] = active;
                        renewed = active;
                        combatReason = active.LastProgressKind;
                    }
                    else
                    {
                        active.HardUntilUtc = Min(active.AbsoluteUntilUtc, active.NoProgressUntilUtc);
                        active.LastObservedAtUtc = now;
                        active.LastProgressKind = "segment_waiting_no_progress_deadline";
                        ActiveByBotProfileId[key] = active;
                    }
                }
                else
                {
                    active.LastObservedAtUtc = now;
                    ActiveByBotProfileId[key] = active;
                }
            }
            else
            {
                bool maxExpired = active.MaxUntilUtc != DateTimeOffset.MinValue && active.MaxUntilUtc <= now;
                bool noProgressExpired = active.NoProgressUntilUtc != DateTimeOffset.MinValue
                    && active.NoProgressUntilUtc <= now
                    && (active.MinUntilUtc == DateTimeOffset.MinValue || active.MinUntilUtc <= now);
                if (maxExpired || noProgressExpired)
                {
                    expired = active;
                    timeoutReason = maxExpired ? "MaxWindowExpired" : "NoProgress";
                    ActiveByBotProfileId.Remove(key);
                }
            }
        }

        if (renewed != null)
        {
            string eventName = renewed.LastProgressKind.IndexOf("target_chain", StringComparison.OrdinalIgnoreCase) >= 0
                || renewed.LastProgressKind.IndexOf("combat_chain_roll", StringComparison.OrdinalIgnoreCase) >= 0
                ? "VANGUARD_COMBAT_TARGET_CHAINED"
                : "VANGUARD_SAIN_WINDOW_SEGMENT_RENEWED";
            LogThrottled("CombatRenew|" + key + "|" + renewed.TargetGeneration + "|" + renewed.SegmentIndex, now, ProgressLogInterval,
                $"{eventName} {renewed.Summary}; reason={Safe(combatReason)}; doctrine=local_and_group_acquisition_chain_before_combat_release_bounded_per_target_generation; chainTag={VanguardPrimaryExecutionContract.TargetChainIdempotenceStatusTag}; legacyChainTag={VanguardPrimaryExecutionContract.CombatTargetChainStatusTag}; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; schedulerTag={StatusTag}");
            return;
        }

        if (expired == null)
        {
            return;
        }

        string travelTerminalSummary = "not_applicable";
        bool travelCorridorExpired = expired.IsCloseCohesionMovement
            && (expired.State.IndexOf("TravelCorridor", StringComparison.OrdinalIgnoreCase) >= 0
                || expired.WindowId.StartsWith("travelcorridor_", StringComparison.OrdinalIgnoreCase));
        if (travelCorridorExpired)
        {
            bool executorTerminated = VanguardSquadTravelCohesionExecutor.TryTerminateSchedulerExpiredWindow(
                key,
                expired.WindowId,
                now,
                timeoutReason,
                out travelTerminalSummary);
            if (!executorTerminated
                && !string.IsNullOrWhiteSpace(expired.BackendLeaseId)
                && !string.Equals(expired.BackendLeaseId, "none", StringComparison.OrdinalIgnoreCase))
            {
                // The scheduler retains the exact backend lease identity written at start. If the
                // executor state disappeared first, retire only that exact command; a replacement
                // generation with a different lease remains protected by ClearOwned.
                string commandCleanup = VanguardReturnMovementCommandStore.ClearOwned(
                    key,
                    expired.BackendLeaseId,
                    expired.StartedAtUtc,
                    "scheduler_terminal_exact_backend_fallback:" + timeoutReason);
                VanguardSquadTravelCohesionAuthority.ClearHold(
                    key,
                    now,
                    "scheduler_terminal_exact_backend_fallback:" + timeoutReason);
                travelTerminalSummary += ";exactBackendFallback=" + Safe(commandCleanup);
            }
        }

        string worldContainerLootTerminalSummary = "not_applicable";
        if (expired.IsWorldContainerLoot)
        {
            VanguardWorldLootContainerApproachExecutor.TryTerminateSchedulerExpiredWindow(key, expired.WindowId, now, timeoutReason, out worldContainerLootTerminalSummary);
        }

        string corpseLootTerminalSummary = "not_applicable";
        if (expired.IsCorpseLoot)
        {
            VanguardCorpseLootApproachExecutor.TryTerminateSchedulerExpiredWindow(
                key,
                expired.WindowId,
                now,
                timeoutReason,
                out corpseLootTerminalSummary);
        }

        lock (Sync)
        {
            OutcomeByBotProfileId[key] = new VanguardSchedulerOutcomeRecord
            {
                BotProfileId = key,
                WindowId = expired.WindowId,
                WindowKind = expired.WindowKind,
                IntentKey = expired.IntentKey,
                Outcome = string.Equals(timeoutReason, "CombatTargetsResolved", StringComparison.OrdinalIgnoreCase) ? "Completed" : "Timeout",
                Reason = "scheduler_window_expired:" + timeoutReason,
                BackendSummary = Safe(expired.Summary),
                RecordedAtUtc = now
            };
        }

        if (timeoutReason.StartsWith("TerminalSnapshot:", StringComparison.OrdinalIgnoreCase))
        {
            VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.CombatLifecycleStatusTag,
                $"VANGUARD_WINDOW_TERMINAL_CLEANUP botProfile={Safe(key)}; timeoutReason={Safe(timeoutReason)}; expired={expired.Summary}; doctrine=dead_or_missing_operator_never_preserves_primary_window; tag={VanguardPrimaryExecutionContract.CombatLifecycleStatusTag}; schedulerTag={StatusTag}");
        }

        string squadQuarantineSummary = "not_applicable";
        if (combatNoProductionCleanup && snapshot != null)
        {
            VanguardSquadTargetNoProgressQuarantine.RecordNoProgress(
                snapshot,
                expired.TargetKey,
                now,
                combatReason,
                out squadQuarantineSummary);
            InvalidatePendingCombatAssignmentsForOwnerTarget(
                snapshot.OwnerProfileId,
                expired.TargetKey,
                now,
                "squad_no_progress_quarantine");
            VanguardCombatAwarenessBridge.InvalidateCombatAuthorityReceiptsForOwnerTarget(
                snapshot.OwnerProfileId,
                expired.TargetKey,
                now,
                "scheduler_no_progress_quarantine");
        }

        if (combatNoProductionCleanup && snapshot != null
            && VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record)
            && record.BotOwner != null
            && !record.BotOwner.IsDead)
        {
            var cleanup = VanguardExternalAuthorityAdapter.RequestCombatWindowNoProductionCleanup(
                record.BotOwner,
                snapshot,
                "scheduler_combat_window_no_progress:" + combatReason,
                TimeSpan.FromSeconds(combatReopenBackoffSeconds),
                now);
            VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.SainWindowStatusTag,
                $"VANGUARD_SAIN_WINDOW_NO_PROGRESS_CLOSED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(key)}; target={Safe(expired.TargetKey)}; reason={Safe(combatReason)}; outcome={cleanup.Outcome}; reopenBackoff={combatReopenBackoffSeconds:0.0}; isolatedSeries={Bool(combatReopenBackoffSeconds > VanguardMovementAuthorityDoctrine.CombatNoProductionReopenBackoffSeconds + 0.01f)}; squadQuarantine={Safe(squadQuarantineSummary)}; doctrine=single_cleanup_then_squad_knowledge_only_then_rescore_medical_before_cohesion; isolatedBackoffTag={VanguardPrimaryExecutionContract.IsolatedCombatBackoffStatusTag}; quarantineTag={VanguardSquadTargetNoProgressQuarantine.StatusTag}; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; schedulerTag={StatusTag}");
        }

        bool shouldReleaseExternalCombatAuthority = VanguardPrimaryExecutionContract.IsCombatPrimaryKind(expired.WindowKind)
            && !timeoutReason.StartsWith("TerminalSnapshot:", StringComparison.OrdinalIgnoreCase);
        if (shouldReleaseExternalCombatAuthority
            && snapshot != null
            && VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var releaseRecord)
            && releaseRecord.BotOwner != null
            && !releaseRecord.BotOwner.IsDead)
        {
            string releaseSummary = VanguardExternalAuthorityAdapter.ReleaseOrchestratorCombatAuthority(
                releaseRecord.BotOwner,
                snapshot.BotProfileId,
                now,
                timeoutReason);
            VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.SainWindowStatusTag,
                $"VANGUARD_SAIN_WINDOW_EXTERNAL_AUTHORITY_RELEASED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(key)}; target={Safe(expired.TargetKey)}; timeoutReason={Safe(timeoutReason)}; {Safe(releaseSummary)}; doctrine=orbit_patrol_and_low_priority_systems_resume_only_after_combat_window_terminal_close; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; schedulerTag={StatusTag}");
        }

        bool combatTargetsResolved = string.Equals(timeoutReason, "CombatTargetsResolved", StringComparison.OrdinalIgnoreCase);
        string terminalEvent = combatTargetsResolved ? "VANGUARD_EXECUTION_COMPLETED" : "VANGUARD_EXECUTION_TIMEOUT";
        string terminalOutcome = combatTargetsResolved ? "Completed" : "Timeout";
        string terminalReason = combatTargetsResolved ? "all_identifiable_local_and_group_targets_resolved" : "scheduler_window_expired";
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"{terminalEvent} window={Safe(expired.WindowId)}; botProfile={Safe(key)}; windowKind={Safe(expired.WindowKind)}; intent={Safe(expired.IntentKey)}; target={Safe(expired.TargetKey)}; outcome={terminalOutcome}; reason={terminalReason}; timeoutReason={Safe(timeoutReason)}; boundedCombatWindow=true; fullWindowPayload=false; travelPayload=false; cleanAuthTag={CleanAuthStatusTag}; chainTag={VanguardPrimaryExecutionContract.TargetChainIdempotenceStatusTag}; tag={StatusTag}");
        VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
            $"VANGUARD_EXECUTION_EXPIRED_TRACE windowSummary={Safe(expired.Summary)}; timeoutReason={Safe(timeoutReason)}; travelAtomicTerminal={Safe(travelTerminalSummary)}; corpseLootTerminal={Safe(corpseLootTerminalSummary)}; worldContainerLootTerminal={Safe(worldContainerLootTerminalSummary)}; tag={StatusTag}");
        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
            $"VANGUARD_OUTCOME_MEMORY_UPDATED botProfile={Safe(key)}; outcome={terminalOutcome}; reason={terminalReason}:{Safe(timeoutReason)}; window={Safe(expired.WindowKind)}; intent={Safe(expired.IntentKey)}; backendPayload=false; cleanAuthTag={CleanAuthStatusTag}; tag={StatusTag}");
    }

    private static void InvalidatePendingCombatAssignmentsForOwnerTarget(
        string? ownerProfileId,
        string? targetId,
        DateTimeOffset now,
        string reason)
    {
        string owner = Normalize(ownerProfileId);
        string target = Normalize(targetId);
        if (string.Equals(owner, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(target, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        IReadOnlyList<VanguardRaidOperatorRuntimeRecord> siblings = VanguardRaidOperatorRuntimeRegistry.GetOperatorsForOwner(owner);
        int cleared = 0;
        lock (Sync)
        {
            foreach (VanguardRaidOperatorRuntimeRecord sibling in siblings)
            {
                string bot = Normalize(sibling.BotProfileId);
                if (string.Equals(bot, "none", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (CombatAssignmentPendingTargetByBotProfileId.TryGetValue(bot, out string pendingTarget)
                    && string.Equals(Normalize(pendingTarget), target, StringComparison.OrdinalIgnoreCase))
                {
                    CombatAssignmentPendingTargetByBotProfileId.Remove(bot);
                    CombatAssignmentPendingUntilByBotProfileId.Remove(bot);
                    cleared++;
                }
            }
        }

        VanguardClientDiagnosticsLog.Info(VanguardSquadTargetNoProgressQuarantine.StatusTag,
            $"VANGUARD_PENDING_ASSIGNMENTS_INVALIDATED owner={Safe(owner)}; target={Safe(target)}; cleared={cleared}; at={now:O}; reason={Safe(reason)}; mutation=scheduler_pending_only; tag={VanguardSquadTargetNoProgressQuarantine.StatusTag}; schedulerTag={StatusTag}");
    }

    private static bool HasFreshSnapshotEvidenceForTarget(OperatorDecisionSnapshot snapshot, string targetId, out string reason)
    {
        reason = "none";
        if (snapshot == null || string.Equals(Normalize(targetId), "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string localTarget = Normalize(snapshot.Threat.EnemyId);
        if (!string.Equals(localTarget, Normalize(targetId), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (snapshot.Threat.EnemyVisible == true)
        {
            reason = "enemy_visible";
            return true;
        }
        if (snapshot.Threat.EnemyLineOfSight == true)
        {
            reason = "enemy_los";
            return true;
        }
        if (snapshot.Threat.EnemyCanShoot == true || snapshot.Brain.VanillaGoalEnemyCanShoot == true)
        {
            reason = "enemy_can_shoot";
            return true;
        }
        if (snapshot.Threat.ShotMeRecently == true || snapshot.Threat.ShotAtMeRecently == true)
        {
            reason = "incoming_fire_recent";
            return true;
        }
        if (HasFreshDirectCombatPressure(snapshot))
        {
            reason = "fresh_direct_combat_pressure";
            return true;
        }

        bool freshSeen = snapshot.Threat.TimeSinceSeen.HasValue
            && snapshot.Threat.TimeSinceSeen.Value >= 0f
            && snapshot.Threat.TimeSinceSeen.Value <= 1.5f;
        bool sainOwnsSameTarget = snapshot.Sain.HasEnemy == true
            && (snapshot.Sain.IsInCombat == true
                || snapshot.Sain.Searching == true
                || (snapshot.Sain.CurrentAction ?? string.Empty).IndexOf("cover", StringComparison.OrdinalIgnoreCase) >= 0
                || (snapshot.Sain.CurrentAction ?? string.Empty).IndexOf("shoot", StringComparison.OrdinalIgnoreCase) >= 0);
        if (freshSeen && sainOwnsSameTarget)
        {
            reason = "fresh_seen_sain_owned";
            return true;
        }

        return false;
    }

    private static bool HasFreshDirectCombatPressure(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return false;
        }

        if (snapshot.Threat.ShotMeRecently == true
            || snapshot.Threat.ShotAtMeRecently == true
            || snapshot.Medical.Safety.IncomingFireRecent)
        {
            return true;
        }

        bool visibleOrLos = snapshot.Threat.EnemyVisible == true || snapshot.Threat.EnemyLineOfSight == true;
        bool recentlySeen = snapshot.Threat.TimeSinceSeen.HasValue
            && snapshot.Threat.TimeSinceSeen.Value >= 0f
            && snapshot.Threat.TimeSinceSeen.Value <= 4.0f;
        bool canShoot = snapshot.Threat.EnemyCanShoot == true || snapshot.Medical.Safety.EnemyCanShoot;
        return visibleOrLos || (canShoot && recentlySeen);
    }

    private static void RollCombatWindow(VanguardPrimaryExecutionWindowState active, DateTimeOffset now, string targetId, bool targetChanged, string reason)
    {
        DateTimeOffset absoluteUntil = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CombatProtectedAbsoluteMaxSeconds);
        active.WindowId = "combat_chain_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(active.BotProfileId);
        active.TargetKey = Normalize(targetId);
        active.CommittedTargetKey = active.TargetKey;
        active.CommittedTargetSource = "locally_verified_chain_roll";
        active.LastTargetTransitionAtUtc = now;
        active.TargetApplicationState = "Verified";
        active.LastTargetAppliedAtUtc = now;
        active.LastTargetVerifiedAtUtc = now;
        active.LastTargetVerificationAttemptAtUtc = now;
        active.TargetRepairAttempts = 0;
        active.TargetMissingSinceUtc = DateTimeOffset.MinValue;
        active.TargetMissingSnapshotCount = 0;
        active.LastTargetLivenessReason = "combat_window_roll_local_target_preserved";
        if (targetChanged)
        {
            active.TargetGeneration = Math.Max(1, active.TargetGeneration + 1);
        }
        active.SegmentIndex = 1;
        active.StartedAtUtc = now;
        active.MinUntilUtc = now + TimeSpan.FromSeconds(3.5d);
        active.MaxUntilUtc = absoluteUntil;
        active.HardUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CombatProtectedSegmentSeconds);
        active.AbsoluteUntilUtc = absoluteUntil;
        active.NoProgressUntilUtc = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.CombatNoProductionCleanupSeconds);
        active.LastProgressAtUtc = now;
        active.LastObservedAtUtc = now;
        active.LastProgressKind = reason;
        active.State = "RunningSainAuthority";
    }

    private static double NoProgressSecondsFor(VanguardPrimaryExecutionWindowState active)
    {
        if (active == null)
        {
            return 2.0d;
        }

        if (active.IsGrenadeEmergency)
        {
            return VanguardGrenadeEmergencyPolicy.NativeStallSeconds;
        }

        if (active.IsHardReturn)
        {
            return VanguardMovementAuthorityDoctrine.MovementLeaseNoProgressSeconds;
        }

        if (active.IsCloseCohesionMovement)
        {
            bool travelCorridorWindow = active.State.IndexOf("TravelCorridor", StringComparison.OrdinalIgnoreCase) >= 0
                || active.WindowId.StartsWith("travelcorridor_", StringComparison.OrdinalIgnoreCase)
                || string.Equals(active.IntentKey, "MovementBrokerTravelCohesionFollowThrough", StringComparison.OrdinalIgnoreCase);
            return travelCorridorWindow
                ? VanguardMovementAuthorityDoctrine.TravelSchedulerHeartbeatTimeoutSeconds
                : CloseCohesionWindowNoProgressSecondsFor(active.IntentKey);
        }

        if (active.IsTacticalMovement)
        {
            return VanguardMovementAuthorityDoctrine.TacticalRepositionNoProgressSeconds;
        }

        if (active.IsCorpseLoot || active.IsWorldContainerLoot)
        {
            return VanguardCorpseLootApproachDoctrine.NoProgressSeconds + 1.0d;
        }

        if (string.Equals(active.WindowKind, VanguardPrimaryExecutionWindowKinds.SainCombatRelease, StringComparison.OrdinalIgnoreCase))
        {
            return VanguardMovementAuthorityDoctrine.CombatNoProductionCleanupSeconds;
        }

        return 3.0d;
    }

    private static string ClassifyWindowKind(VanguardIntentCandidate candidate)
    {
        if (candidate == null)
        {
            return VanguardPrimaryExecutionWindowKinds.Recovery;
        }

        if (IsHardReturnMovementIntent(candidate))
        {
            return VanguardPrimaryExecutionWindowKinds.HardReturnMovement;
        }

        if (IsCloseCohesionMovementIntent(candidate))
        {
            return VanguardPrimaryExecutionWindowKinds.CloseCohesionMovement;
        }

        if (IsTacticalMovementIntent(candidate))
        {
            return VanguardPrimaryExecutionWindowKinds.TacticalMovement;
        }

        if (IsCorpseLootApproachIntent(candidate))
        {
            return VanguardPrimaryExecutionWindowKinds.CorpseLoot;
        }

        if (string.Equals(candidate.IntentKey, "YieldToSainCombat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.IntentKey, "MovementBrokerYieldSainDirectThreatReadOnly", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.IntentKey, "OrchestratorCombatAuthorityRelease", StringComparison.OrdinalIgnoreCase)
            || candidate.IntentKey.IndexOf("PromoteImmediateThreat", StringComparison.OrdinalIgnoreCase) >= 0
            || candidate.IntentKey.IndexOf("AwarenessPromote", StringComparison.OrdinalIgnoreCase) >= 0
            || candidate.IntentKey.IndexOf("AwarenessRelease", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return VanguardPrimaryExecutionWindowKinds.SainCombatRelease;
        }

        if (candidate.Domain == "Medical" || candidate.IntentKey.IndexOf("Medical", StringComparison.OrdinalIgnoreCase) >= 0 || candidate.IntentKey.IndexOf("Surgery", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (candidate.IntentKey.IndexOf("Mobile", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return VanguardPrimaryExecutionWindowKinds.MobileMedical;
            }

            return VanguardPrimaryExecutionWindowKinds.StationaryMedical;
        }

        if (candidate.Domain == "Follow" || candidate.Domain == "SquadCohesion")
        {
            return VanguardPrimaryExecutionWindowKinds.Rejoin;
        }

        return VanguardPrimaryExecutionWindowKinds.Recovery;
    }

    private static bool IsCorpseLootApproachIntent(VanguardIntentCandidate candidate)
    {
        return candidate != null
            && candidate.Valid
            && string.Equals(candidate.Domain, "CorpseLoot", StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.IntentKey, "ApproachNearbyCorpse", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanSupersedeActiveWindowForCorpseLoot(VanguardPrimaryExecutionWindowState active, bool allowAuthoringPreviewYield, bool allowTravelCohesionYield)
    {
        if (active == null || active.IsCorpseLoot || active.IsWorldContainerLoot)
        {
            return false;
        }

        if (active.IsAuthoringPreview)
        {
            return allowAuthoringPreviewYield;
        }

        if (allowTravelCohesionYield
            && active.IsCloseCohesionMovement
            && string.Equals(active.IntentKey, "MovementBrokerTravelCohesionFollowThrough", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool nonCriticalWindowKind = string.Equals(active.WindowKind, VanguardPrimaryExecutionWindowKinds.Recovery, StringComparison.OrdinalIgnoreCase)
            || string.Equals(active.WindowKind, VanguardPrimaryExecutionWindowKinds.Rejoin, StringComparison.OrdinalIgnoreCase);
        bool nonCriticalDomain = string.Equals(active.Domain, "Recovery", StringComparison.OrdinalIgnoreCase)
            || string.Equals(active.Domain, "Cohesion", StringComparison.OrdinalIgnoreCase)
            || string.Equals(active.Domain, "SquadCohesion", StringComparison.OrdinalIgnoreCase)
            || string.Equals(active.Domain, "Follow", StringComparison.OrdinalIgnoreCase);
        return nonCriticalWindowKind && nonCriticalDomain;
    }

    private static bool CanYieldToBoundedCorpseLoot(VanguardIntentCandidate candidate)
    {
        if (candidate == null || !candidate.Valid)
        {
            return true;
        }

        return string.Equals(candidate.Domain, "Cohesion", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.Domain, "SquadCohesion", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.Domain, "Follow", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.Domain, "Recovery", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHardReturnMovementIntent(VanguardIntentCandidate candidate)
    {
        if (candidate == null || !candidate.Valid)
        {
            return false;
        }

        return string.Equals(candidate.IntentKey, "MovementBrokerBreakSainSearchReturnBubbleReadOnly", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.IntentKey, "MovementBrokerSuppressExternalReturnBubbleReadOnly", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.IntentKey, "MovementBrokerReturnHardBubbleReadOnly", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCloseCohesionMovementIntent(VanguardIntentCandidate candidate)
    {
        if (candidate == null || !candidate.Valid)
        {
            return false;
        }

        return string.Equals(candidate.IntentKey, "MovementBrokerCloseCohesionMicroAdjust", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.IntentKey, "MovementBrokerClaimedCohesionSlot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.IntentKey, "MovementBrokerTravelCohesionFollowThrough", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.IntentKey, "MovementBrokerTacticalVolumeJoin", StringComparison.OrdinalIgnoreCase);
    }

    private static double CloseCohesionWindowMaxSecondsFor(string? intentKey)
    {
        if (string.Equals(intentKey, "MovementBrokerTacticalVolumeJoin", StringComparison.OrdinalIgnoreCase))
        {
            return VanguardMovementAuthorityDoctrine.TacticalVolumeJoinMaxDurationSeconds;
        }

        if (string.Equals(intentKey, "MovementBrokerTravelCohesionFollowThrough", StringComparison.OrdinalIgnoreCase))
        {
            return VanguardMovementAuthorityDoctrine.TravelCohesionMaxDurationSeconds;
        }

        if (string.Equals(intentKey, "MovementBrokerClaimedCohesionSlot", StringComparison.OrdinalIgnoreCase))
        {
            return VanguardMovementAuthorityDoctrine.ClaimedCohesionMaxDurationSeconds;
        }

        return VanguardMovementAuthorityDoctrine.CloseCohesionMaxDurationSeconds;
    }

    private static double CloseCohesionWindowNoProgressSecondsFor(string? intentKey)
    {
        if (string.Equals(intentKey, "MovementBrokerTacticalVolumeJoin", StringComparison.OrdinalIgnoreCase))
        {
            return VanguardMovementAuthorityDoctrine.TacticalVolumeJoinNoProgressSeconds;
        }

        if (string.Equals(intentKey, "MovementBrokerTravelCohesionFollowThrough", StringComparison.OrdinalIgnoreCase))
        {
            return VanguardMovementAuthorityDoctrine.TravelCohesionNoProgressSeconds;
        }

        if (string.Equals(intentKey, "MovementBrokerClaimedCohesionSlot", StringComparison.OrdinalIgnoreCase))
        {
            return VanguardMovementAuthorityDoctrine.ClaimedCohesionNoProgressSeconds;
        }

        return VanguardMovementAuthorityDoctrine.CloseCohesionNoProgressSeconds;
    }

    private static string CloseCohesionPlanningStateFor(string? intentKey)
    {
        if (string.Equals(intentKey, "MovementBrokerTacticalVolumeJoin", StringComparison.OrdinalIgnoreCase))
        {
            return "PlanningTacticalVolumeAnchor";
        }

        if (string.Equals(intentKey, "MovementBrokerTravelCohesionFollowThrough", StringComparison.OrdinalIgnoreCase))
        {
            return "PlanningTravelCohesionAnchor";
        }

        if (string.Equals(intentKey, "MovementBrokerClaimedCohesionSlot", StringComparison.OrdinalIgnoreCase))
        {
            return "PlanningClaimedCohesionAnchor";
        }

        return "PlanningCloseAnchor";
    }

    private static string CloseCohesionProgressKindFor(string? intentKey)
    {
        if (string.Equals(intentKey, "MovementBrokerTacticalVolumeJoin", StringComparison.OrdinalIgnoreCase))
        {
            return "planning_tactical_volume_anchor";
        }

        if (string.Equals(intentKey, "MovementBrokerTravelCohesionFollowThrough", StringComparison.OrdinalIgnoreCase))
        {
            return "planning_travel_cohesion_anchor";
        }

        if (string.Equals(intentKey, "MovementBrokerClaimedCohesionSlot", StringComparison.OrdinalIgnoreCase))
        {
            return "planning_claimed_cohesion_anchor";
        }

        return "planning_close_cohesion_anchor";
    }

    private static bool IsTacticalMovementIntent(VanguardIntentCandidate candidate)
    {
        if (candidate == null || !candidate.Valid)
        {
            return false;
        }

        return string.Equals(candidate.IntentKey, "MovementBrokerTacticalRepositionReadOnly", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCombatProgressSignal(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        if (VanguardMovementAuthorityDoctrine.IsCombatProductive(snapshot, out var productiveReason))
        {
            reason = productiveReason;
            return true;
        }

        if (snapshot.ThreatScan.WouldPromote || snapshot.Awareness.WouldPromoteSainTarget || snapshot.Awareness.WouldPropagateConfirmedThreat)
        {
            reason = "awareness_or_scan_support_not_productivity";
            return false;
        }

        reason = "no_combat_progress_signal";
        return false;
    }

    private static float SetCombatReopenBackoff(OperatorDecisionSnapshot snapshot, string targetKey, DateTimeOffset now, bool freshDirectPressure)
    {
        float standard = VanguardMovementAuthorityDoctrine.CombatNoProductionReopenBackoffSeconds;
        string bot = Normalize(snapshot?.BotProfileId);
        string target = Normalize(targetKey);
        string seriesKey = bot + "|" + target;
        bool isolated = snapshot != null && snapshot.SquadCohesion.OperatorDistanceToOwner >= 75.0f;
        bool targetFarOrUnconfirmed = snapshot == null
            || !snapshot.Threat.Distance.HasValue
            || snapshot.Threat.Distance.Value >= 60.0f
            || (snapshot.Threat.EnemyVisible != true && snapshot.Threat.EnemyLineOfSight != true);

        if (!isolated || freshDirectPressure || !targetFarOrUnconfirmed || string.Equals(target, "none", StringComparison.OrdinalIgnoreCase))
        {
            CombatNoProgressSeriesByBotAndTarget.Remove(seriesKey);
            return standard;
        }

        int count = 1;
        if (CombatNoProgressSeriesByBotAndTarget.TryGetValue(seriesKey, out var previous)
            && now - previous.LastAtUtc <= TimeSpan.FromSeconds(90.0d))
        {
            count = Math.Min(4, previous.Count + 1);
        }

        float seconds = Math.Min(45.0f, 15.0f + ((count - 1) * 10.0f));
        CombatNoProgressSeriesByBotAndTarget[seriesKey] = new CombatNoProgressSeriesState(count, now);
        return Math.Max(standard, seconds);
    }

    private static bool IsCombatReopenBlocked(OperatorDecisionSnapshot snapshot, string? targetKey, DateTimeOffset now, out string reason)
    {
        reason = "none";
        string key = Normalize(snapshot.BotProfileId);
        string target = Normalize(targetKey);
        if (VanguardSquadTargetNoProgressQuarantine.IsCombatAuthorityBlocked(snapshot, target, now, out string squadQuarantineReason))
        {
            reason = squadQuarantineReason;
            return true;
        }

        lock (Sync)
        {
            if (!CombatReopenBlockedUntilByBotProfileId.TryGetValue(key, out var until) || until <= now)
            {
                CombatReopenBlockedUntilByBotProfileId.Remove(key);
                CombatReopenBlockedTargetByBotProfileId.Remove(key);
                return false;
            }

            CombatReopenBlockedTargetByBotProfileId.TryGetValue(key, out var blockedTarget);
            bool sameTarget = string.Equals(Normalize(blockedTarget), target, StringComparison.OrdinalIgnoreCase)
                || string.Equals(target, "none", StringComparison.OrdinalIgnoreCase);
            if (!sameTarget || HasFreshDirectCombatPressure(snapshot))
            {
                CombatReopenBlockedUntilByBotProfileId.Remove(key);
                CombatReopenBlockedTargetByBotProfileId.Remove(key);
                return false;
            }

            reason = "same_target_no_progress_backoff_until=" + until.ToString("O", CultureInfo.InvariantCulture);
            return true;
        }
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right)
    {
        if (left == DateTimeOffset.MinValue)
        {
            return right;
        }

        if (right == DateTimeOffset.MinValue)
        {
            return left;
        }

        return left <= right ? left : right;
    }

    private static string TopCandidates(VanguardIntentDryRunBoard board)
    {
        if (board?.Candidates == null)
        {
            return "none";
        }

        return string.Join(",", board.Candidates
            .Where(candidate => candidate.Valid)
            .OrderByDescending(candidate => candidate.FinalScore)
            .ThenBy(candidate => candidate.IntentKey)
            .Take(4)
            .Select(candidate => Safe(candidate.IntentKey) + ":" + candidate.FinalScore.ToString("0.0", CultureInfo.InvariantCulture)));
    }

    private static string ActiveSummary(string? botProfileId, DateTimeOffset now)
    {
        string key = Normalize(botProfileId);
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(key, out var active) && active.IsActive(now))
            {
                return active.WindowKind + ":" + active.State + ":" + active.IntentKey;
            }
        }

        return "none";
    }

    private static bool ShouldLog(string key, DateTimeOffset now, TimeSpan interval)
    {
        lock (Sync)
        {
            if (LastLogAtByKey.TryGetValue(key, out var last) && now - last < interval)
            {
                return false;
            }

            LastLogAtByKey[key] = now;
            return true;
        }
    }

    private static void LogThrottled(string key, DateTimeOffset now, TimeSpan interval, Func<string> messageFactory)
    {
        if (!VanguardClientDiagnosticsLog.IsEnabled(VanguardAuditLevel.Trace))
        {
            return;
        }

        if (ShouldLog(key, now, interval))
        {
            VanguardClientDiagnosticsLog.Trace(StatusTag, messageFactory);
        }
    }

    private static void LogThrottled(string key, DateTimeOffset now, TimeSpan interval, string message)
    {
        if (ShouldLog(key, now, interval))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag, message);
        }
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }

    private readonly struct CombatNoProgressSeriesState
    {
        public CombatNoProgressSeriesState(int count, DateTimeOffset lastAtUtc)
        {
            Count = count;
            LastAtUtc = lastAtUtc;
        }

        public int Count { get; }
        public DateTimeOffset LastAtUtc { get; }
    }

    private sealed class VanguardSchedulerOutcomeRecord
    {
        public string BotProfileId = "none";
        public string WindowId = "none";
        public string WindowKind = "none";
        public string IntentKey = "none";
        public string Outcome = "none";
        public string Reason = "none";
        public string BackendSummary = "none";
        public DateTimeOffset RecordedAtUtc = DateTimeOffset.MinValue;
    }
}
#endif
