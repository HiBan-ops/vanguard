#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.Movement.Brain;
using Vanguard.Client.Runtime.SquadCohesion;

// Responsibility: Moves one member of an active Medic Support pair toward a qualified rendezvous/pickup anchor while respecting the existing movement authority stack.
// Flow: Non-combat movement uses a short Medic medical primary window and the normal BigBrain movement bridge; Search/GroupSearch combat movement borrows SAIN BotPathDataManual through the same translation bridge used by TC.
// Authority boundary: It never owns threat/aim/fire/suppression. Grenade, true direct lethal threat, RunningToCover/hard recovery and self-medical remain higher authorities.
// Invariant: One exact session/actor lease owns one exact movement command/path binding; TC physical contributions are synchronously drained before Medic physical acquisition and may only reacquire fresh afterwards.
namespace Vanguard.Client.Runtime.Medical.Support;

internal static class VanguardMedicRendezvousMovementService
{
    public const string StatusTag = "VANGUARD_MEDIC_RENDEZVOUS_MOVEMENT_STATUS";
    public const string CombatRequestKind = "MedicSupportCombatRendezvous";
    public const string NonCombatRequestKind = "MedicSupportRendezvous";
    public const string PickupRequestKind = "MedicSupportTargetedPickup";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, MovementLease> ActiveByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, StationaryHoldLease> StationaryByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> StationaryPhysicalGenerationByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(7.5d);
    // EFT BotMover automatically releases a timed pause. This bounds the residue even if a
    // different primary takes over between admission and release; unlike Stop, the path stays.
    private const float StationaryHoldPauseSeconds = 0.60f;
    private const float StationaryPauseEndTolerance = 0.03f;
    private static long nextStationaryGeneration;
    private static readonly TimeSpan RendezvousRetargetCooldown = TimeSpan.FromSeconds(0.75d);
    private const float RendezvousMaterialRetargetMeters = 1.75f;

    public static void ResetForRaidLifecycle(string reason)
    {
        MovementLease[] leases;
        StationaryHoldLease[] holds;
        lock (Sync)
        {
            leases = new List<MovementLease>(ActiveByBot.Values).ToArray();
            ActiveByBot.Clear();
            holds = new List<StationaryHoldLease>(StationaryByBot.Values).ToArray();
            StationaryByBot.Clear();
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (MovementLease lease in leases)
        {
            ReleasePhysical(lease, now, "raid_reset:" + Safe(reason), closeWindow: true);
        }
        foreach (StationaryHoldLease hold in holds)
        {
            ReleaseStationaryPhysical(hold, now, "raid_reset:" + Safe(reason), closeWindow: true);
        }
        lock (Sync)
        {
            StationaryPhysicalGenerationByBot.Clear();
        }

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_MEDIC_RENDEZVOUS_RESET reason={Safe(reason)}; leasesReleased={leases.Length}; stationaryHoldsReleased={holds.Length}; combatSearchTranslation=true; staleResume=false");
    }

    public static bool TryMove(
        string sessionId,
        OperatorDecisionSnapshot snapshot,
        BotOwner botOwner,
        string role,
        Vector3 destination,
        float reachDistanceMeters,
        DateTimeOffset now,
        bool allowCombatTranslation,
        out bool arrived,
        out string reason)
    {
        arrived = false;
        reason = "none";
        if (snapshot == null || botOwner == null || botOwner.IsDead || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            reason = "actor_snapshot_or_runtime_missing";
            return false;
        }

        // Stationary/non-combat medical movement requires an already
        // committed pair; never release an existing hold before pair admission.
        if (!IsCombatSearchContext(snapshot)
            && (!VanguardMedicSupportSessionStore.TryGetActive(out VanguardMedicSupportSession pair)
                || !VanguardMainIntentScheduler.IsOwnMedicPairCurrent(
                    sessionId, pair.CaregiverBotProfileId, pair.PatientBotProfileId, now)))
        {
            reason = "medical_pair_primary_not_committed";
            return false;
        }
        // Preserve an already-owned Medical stationary hold when the actor has arrived.
        // Releasing it before this reach check caused the caller to re-admit the same hold
        // on every rendezvous tick (new_movement_request), even under an unchanged pair window.
        float distance = HorizontalDistance(snapshot.Position, destination);
        if (distance <= Math.Max(0.75f, reachDistanceMeters))
        {
            // A genuine survival veto still releases an existing Medical pause immediately,
            // including when the rendezvous destination is already within reach.
            if (IsHardSurvivalBlock(snapshot, out _))
                ReleaseHold(snapshot.BotProfileId, sessionId, now, "arrival_survival_preempt", preservePrimaryWindow: !IsCombatSearchContext(snapshot));
            ReleaseMovementPreservingWindow(snapshot.BotProfileId, sessionId, now, "destination_reached");
            arrived = true;
            reason = "destination_reached:" + distance.ToString("0.00", CultureInfo.InvariantCulture);
            return true;
        }

        // Only an actual new movement is allowed to retire a stationary hold.
        // The exact-owned hold release preserves the pair's primary Medical window.
        ReleaseHold(snapshot.BotProfileId, sessionId, now, "new_movement_request", preservePrimaryWindow: !IsCombatSearchContext(snapshot));

        if (IsHardSurvivalBlock(snapshot, out string survivalReason))
        {
            Release(snapshot.BotProfileId, sessionId, now, "survival_preempt:" + survivalReason);
            reason = "survival_preempts_medic_movement:" + survivalReason;
            return false;
        }

        bool combatContext = IsCombatSearchContext(snapshot);
        if (combatContext && !allowCombatTranslation)
        {
            Release(snapshot.BotProfileId, sessionId, now, "combat_blocks_noncombat_medic_movement");
            reason = "combat_context_blocks_noncombat_movement";
            return false;
        }

        if (!VanguardTacticalPlacementSolver.TryCalculatePreparedRuntimePath(
                snapshot.Position,
                destination,
                out NavMeshPath preparedPath,
                out float pathDistance,
                out string pathSummary))
        {
            reason = "rendezvous_path_unavailable:" + Safe(pathSummary);
            return false;
        }

        string botProfileId = snapshot.BotProfileId.Trim();
        MovementLease? existing;
        lock (Sync)
        {
            ActiveByBot.TryGetValue(botProfileId, out existing);
        }

        if (existing != null
            && string.Equals(existing.SessionId, Normalize(sessionId), StringComparison.OrdinalIgnoreCase)
            && existing.ExpiresAtUtc > now
            && VanguardReturnMovementCommandStore.TryGetExactOwned(
                botProfileId,
                existing.LeaseId,
                existing.RequestKind,
                existing.Generation,
                now,
                out _,
                out _))
        {
            string authorityReason;
            bool authorityCurrent = existing.CombatSidecar
                ? VanguardMainIntentScheduler.IsMedicSupportCombatMovementSidecarCurrent(
                    botProfileId,
                    now,
                    existing.ThreatId,
                    existing.SchedulerWindowId,
                    existing.SessionId,
                    out authorityReason)
                : VanguardMainIntentScheduler.IsMedicSupportAuxiliaryWindowCurrent(
                    botProfileId,
                    now,
                    existing.SchedulerWindowId,
                    existing.SessionId,
                    out authorityReason);

            if (authorityCurrent)
            {
                // Physical progress alone extends the pair's bounded Medical window.
                if (!existing.CombatSidecar && distance + 0.40f < existing.LastMeasuredDistanceMeters)
                {
                    existing.LastMeasuredDistanceMeters = distance;
                    VanguardMainIntentScheduler.ReportMedicPairPhysicalProgress(
                        sessionId, botProfileId, existing.SchedulerWindowId, now);
                }
                DateTimeOffset refreshedExpiresAt = now + LeaseDuration;
                VanguardMovementRetargetResult retarget = VanguardReturnMovementCommandStore.TryRetargetActive(
                    existing.LeaseId,
                    botProfileId,
                    destination,
                    Math.Max(0.75f, reachDistanceMeters),
                    false,
                    now,
                    refreshedExpiresAt,
                    pathSummary,
                    pathDistance,
                    "medic_support_rendezvous:" + Safe(role),
                    RendezvousMaterialRetargetMeters,
                    RendezvousRetargetCooldown);

                if (retarget.Accepted)
                {
                    existing.ExpiresAtUtc = refreshedExpiresAt;
                    existing.ReachDistanceMeters = reachDistanceMeters;
                    if (retarget.Applied)
                    {
                        existing.PreparedPath = preparedPath;
                        existing.Destination = destination;
                        existing.PathSummary = pathSummary;
                        existing.PathDistanceMeters = pathDistance;
                    }
                    lock (Sync)
                    {
                        ActiveByBot[botProfileId] = existing;
                    }
                    reason = "existing_medic_movement_retarget:" + retarget.Outcome + ":" + Safe(retarget.Summary);
                    return true;
                }

                if (retarget.Outcome == VanguardMovementRetargetOutcome.RejectedCooldown
                    && VanguardReturnMovementCommandStore.TryUpdateActiveParameters(
                        existing.LeaseId,
                        botProfileId,
                        Math.Max(0.75f, reachDistanceMeters),
                        false,
                        now,
                        refreshedExpiresAt,
                        "medic_support_retarget_cooldown_extension",
                        out _))
                {
                    existing.ExpiresAtUtc = refreshedExpiresAt;
                    existing.ReachDistanceMeters = reachDistanceMeters;
                    lock (Sync)
                    {
                        ActiveByBot[botProfileId] = existing;
                    }
                    reason = "existing_medic_movement_retarget_deferred:" + Safe(retarget.Summary);
                    return true;
                }
            }

            Release(botProfileId, existing.SessionId, now, "medic_movement_authority_stale:" + Safe(authorityReason));
            existing = null;
        }

        if (existing != null)
        {
            Release(botProfileId, existing.SessionId, now, "medic_destination_or_session_changed");
        }

        string schedulerWindowId = "none";
        string threatId = "none";
        string requestKind;
        bool combatSidecar = combatContext;
        if (combatSidecar)
        {
            threatId = ResolveThreatId(snapshot);
            if (string.Equals(threatId, "none", StringComparison.OrdinalIgnoreCase))
            {
                reason = "combat_context_without_current_threat_identity";
                return false;
            }

            if (!SearchTranslationGateBridge.TryPreflightTacticalMovementLiveTranslation(
                    botOwner,
                    threatId,
                    out string translationReason,
                    out string translationSummary,
                    allowOpportunityCombatFlank: true,
                    allowStandAndShootCombatAdvance: true,
                    allowDistantVisibleSearchAction: true))
            {
                reason = "combat_translation_preflight_denied:" + Safe(translationReason) + ":" + Safe(translationSummary);
                return false;
            }

            if (!VanguardMainIntentScheduler.TryGrantMedicSupportCombatMovementSidecar(
                    snapshot,
                    now,
                    threatId,
                    sessionId,
                    out schedulerWindowId,
                    out string sidecarReason))
            {
                reason = "combat_sidecar_denied:" + Safe(sidecarReason);
                return false;
            }

            requestKind = CombatRequestKind;
        }
        else
        {
            string windowKind = string.Equals(role, "pickup", StringComparison.OrdinalIgnoreCase)
                ? VanguardPrimaryExecutionWindowKinds.MedicSupportPickup
                : VanguardPrimaryExecutionWindowKinds.MedicSupportRendezvous;
            if (!VanguardMainIntentScheduler.TryOpenMedicSupportAuxiliaryWindow(
                    snapshot,
                    sessionId,
                    windowKind,
                    role,
                    now,
                    out schedulerWindowId,
                    out string windowReason))
            {
                reason = "medic_movement_window_denied:" + Safe(windowReason);
                return false;
            }

            requestKind = string.Equals(role, "pickup", StringComparison.OrdinalIgnoreCase)
                ? PickupRequestKind
                : NonCombatRequestKind;
        }

        // A TC physical path can be retired only after a native sidecar or the
        // complete Medical pair has received the scheduler's authority.
        if (!combatSidecar
            && (!VanguardMedicSupportSessionStore.TryGetActive(out VanguardMedicSupportSession currentPair)
                || !VanguardMainIntentScheduler.IsOwnMedicPairCurrent(
                    sessionId, currentPair.CaregiverBotProfileId, currentPair.PatientBotProfileId, now)))
        {
            reason = "medical_pair_lost_before_tc_handoff";
            return false;
        }
        TacticalCoordinationPhysicalAuthorityBarrier.DrainForHigherAuthority(
            botProfileId, now, "medical_support_pair_rendezvous:" + Safe(role));

        string leaseId = "medic_move_" + Safe(sessionId) + "_" + Safe(role) + "_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        DateTimeOffset expiresAt = now + LeaseDuration;
        if (!VanguardReturnMovementCommandStore.Issue(
                leaseId,
                snapshot.OperatorId,
                botProfileId,
                destination,
                Math.Max(0.75f, reachDistanceMeters),
                sprint: false,
                now,
                expiresAt,
                requestKind,
                pathSummary,
                pathDistance,
                out string issueReason))
        {
            if (!combatSidecar)
            {
                VanguardMainIntentScheduler.TryCloseMedicSupportAuxiliaryWindow(botProfileId, schedulerWindowId, "Failed", "move_bridge_rejected", issueReason, now);
            }
            reason = "move_bridge_rejected:" + Safe(issueReason);
            return false;
        }

        if (!VanguardReturnMovementCommandStore.TryGetActive(botProfileId, now, out VanguardReturnMovementCommand command)
            || !string.Equals(command.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(command.RequestKind, requestKind, StringComparison.OrdinalIgnoreCase))
        {
            VanguardReturnMovementCommandStore.ClearOwned(botProfileId, leaseId, now, "medic_command_identity_not_confirmed");
            if (!combatSidecar)
            {
                VanguardMainIntentScheduler.TryCloseMedicSupportAuxiliaryWindow(botProfileId, schedulerWindowId, "Failed", "command_identity_not_confirmed", pathSummary, now);
            }
            reason = "medic_command_identity_not_confirmed";
            return false;
        }

        var lease = new MovementLease
        {
            SessionId = Normalize(sessionId),
            BotProfileId = botProfileId,
            OperatorId = snapshot.OperatorId,
            Role = Safe(role),
            LeaseId = leaseId,
            RequestKind = requestKind,
            Generation = command.Generation,
            SchedulerWindowId = schedulerWindowId,
            ThreatId = threatId,
            Destination = destination,
            ReachDistanceMeters = reachDistanceMeters,
            PreparedPath = preparedPath,
            PathSummary = pathSummary,
            PathDistanceMeters = pathDistance,
            CombatSidecar = combatSidecar,
            StartedAtUtc = now,
            ExpiresAtUtc = expiresAt,
            LastMeasuredDistanceMeters = distance
        };
        lock (Sync)
        {
            ActiveByBot[botProfileId] = lease;
        }

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_MEDIC_RENDEZVOUS_MOVE_STARTED session={Safe(sessionId)}; operator={Safe(snapshot.OperatorId)}; bot={Safe(botProfileId)}; role={Safe(role)}; combatSidecar={Bool(combatSidecar)}; request={Safe(requestKind)}; threat={Safe(threatId)}; schedulerWindow={Safe(schedulerWindowId)}; destination={Format(destination)}; reach={reachDistanceMeters:0.00}; pathDistance={pathDistance:0.00}; tcPhysicalDrained=true; sainTargetAimFirePreserved={Bool(combatSidecar)}");
        reason = combatSidecar ? "combat_rendezvous_move_started" : "noncombat_rendezvous_move_started";
        return true;
    }


    public static bool TryHoldStationary(
        string sessionId,
        OperatorDecisionSnapshot snapshot,
        BotOwner botOwner,
        string role,
        DateTimeOffset now,
        out string reason)
    {
        reason = "none";
        if (snapshot == null || botOwner == null || botOwner.IsDead || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            reason = "hold_actor_snapshot_or_runtime_missing";
            return false;
        }
        if (!string.Equals(snapshot.BotProfileId, botOwner.ProfileId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "stationary_hold_actor_incarnation_mismatch";
            return false;
        }
        if (IsHardSurvivalBlock(snapshot, out string survivalReason))
        {
            reason = "survival_preempts_medic_hold:" + survivalReason;
            return false;
        }
        if (IsCombatSearchContext(snapshot))
        {
            reason = "combat_context_blocks_stationary_rendezvous_hold";
            return false;
        }

        // A non-destructive timed pause may slow an approaching anchor without erasing its
        // still-live native path. Existing externally owned pauses are never extended.
        ReleaseMovementPreservingWindow(snapshot.BotProfileId, sessionId, now, "stationary_anchor_replaces_movement");

        if (!VanguardMainIntentScheduler.TryOpenMedicSupportAuxiliaryWindow(
                snapshot,
                sessionId,
                VanguardPrimaryExecutionWindowKinds.MedicSupportRendezvous,
                role,
                now,
                out string windowId,
                out string windowReason))
        {
            reason = "medic_stationary_hold_window_denied:" + Safe(windowReason);
            return false;
        }

        bool newlyOpened = string.Equals(windowReason, "medic_support_auxiliary_window_granted", StringComparison.OrdinalIgnoreCase);
        StationaryHoldLease? previous;
        lock (Sync)
        {
            StationaryByBot.TryGetValue(snapshot.BotProfileId, out previous);
        }
        bool sameHold = previous != null
            && string.Equals(previous.SessionId, Normalize(sessionId), StringComparison.OrdinalIgnoreCase)
            && string.Equals(previous.WindowId, windowId, StringComparison.OrdinalIgnoreCase)
            && ReferenceEquals(previous.BotOwner, botOwner);

        if (!sameHold && previous != null)
        {
            ReleaseHold(snapshot.BotProfileId, previous.SessionId, now, "hold_identity_changed");
        }

        // The scheduler grants a Medical auxiliary window, not ownership of a SAIN path.
        // MovementPause is non-destructive; a timed, exact-owned pause replaces Mover.Stop().
        // A successor is never resumed unless this precise window and pause are still ours.
        if (!VanguardMainIntentScheduler.IsMedicSupportAuxiliaryWindowCurrent(
                snapshot.BotProfileId, now, windowId, sessionId, out string authorityReason))
        {
            if (newlyOpened)
            {
                VanguardMainIntentScheduler.TryCloseMedicSupportAuxiliaryWindow(
                    snapshot.BotProfileId, windowId, "Failed", "stationary_hold_admission_stale", authorityReason, now);
            }
            reason = "stationary_hold_window_not_current:" + Safe(authorityReason);
            return false;
        }

        try
        {
            // Do not mutate TC path ownership before the Medical window is actually admitted.
            TacticalCoordinationPhysicalAuthorityBarrier.DrainForHigherAuthority(
                snapshot.BotProfileId,
                now,
                "medical_support_stationary_rendezvous_hold:" + Safe(role));
            if (!VanguardMainIntentScheduler.IsMedicSupportAuxiliaryWindowCurrent(
                    snapshot.BotProfileId, now, windowId, sessionId, out string postDrainAuthority))
            {
                if (newlyOpened)
                {
                    VanguardMainIntentScheduler.TryCloseMedicSupportAuxiliaryWindow(
                        snapshot.BotProfileId, windowId, "Failed", "stationary_hold_post_drain_window_lost", postDrainAuthority, now);
                }
                reason = "stationary_hold_window_lost_after_tc_drain:" + Safe(postDrainAuthority);
                return false;
            }

            var mover = botOwner.Mover;
            if (mover == null || (mover.Pause && (!sameHold
                    || Math.Abs(mover.PauseEndTime - previous!.PauseEndTime) > StationaryPauseEndTolerance)))
            {
                if (newlyOpened)
                {
                    VanguardMainIntentScheduler.TryCloseMedicSupportAuxiliaryWindow(
                        snapshot.BotProfileId, windowId, "Failed", "stationary_hold_mover_busy", "not_self_owned_pause", now);
                }
                reason = "stationary_hold_mover_unavailable_or_external_pause";
                return false;
            }

            if (sameHold && Math.Abs(mover.LastPathSetTime - previous!.PathSetTime) > 0.001f)
            {
                reason = "stationary_hold_path_changed_since_last_admission";
                return false;
            }

            // Reuse the current self-owned pause while its remaining duration is sufficient.
            // This avoids redundant native MovementPause calls and generation churn.
            if (sameHold && mover.Pause && mover.PauseEndTime - Time.time > 0.20f)
            {
                reason = "medic_stationary_hold_reused:window=" + Safe(windowId) + ":path_preserved=true";
                return true;
            }

            bool hadPath = mover.HasPathAndNoComplete;
            float pathSetTime = mover.LastPathSetTime;
            mover.MovementPause(StationaryHoldPauseSeconds, pauseOnlyIfNot: false);
            long generation = System.Threading.Interlocked.Increment(ref nextStationaryGeneration);
            var lease = new StationaryHoldLease
            {
                SessionId = Normalize(sessionId),
                BotProfileId = snapshot.BotProfileId,
                BotOwner = botOwner,
                WindowId = windowId,
                LeaseId = "medic_hold_" + Safe(snapshot.BotProfileId) + "_" + generation.ToString(CultureInfo.InvariantCulture),
                Generation = generation,
                PauseEndTime = mover.PauseEndTime,
                HadPath = hadPath,
                PathSetTime = pathSetTime
            };
            lock (Sync)
            {
                StationaryByBot[snapshot.BotProfileId] = lease;
                StationaryPhysicalGenerationByBot[snapshot.BotProfileId] = lease.Generation;
            }
            if (!sameHold)
            {
                VanguardClientDiagnosticsLog.Info(
                    StatusTag,
                    $"VANGUARD_MEDIC_PHYSICAL_ADMISSION session={Safe(sessionId)}; bot={Safe(snapshot.BotProfileId)}; role={Safe(role)}; domain=Medical; window={Safe(windowId)}; lease={Safe(lease.LeaseId)}; generation={lease.Generation}; kind=stationary_hold; pathBefore={Bool(lease.HadPath)}; pathPreserved=true; effect=timed_pause; pauseSeconds={StationaryHoldPauseSeconds:0.00}; newlyOpened={Bool(newlyOpened)}");
            }
        }
        catch (Exception exception)
        {
            if (newlyOpened)
            {
                VanguardMainIntentScheduler.TryCloseMedicSupportAuxiliaryWindow(
                    snapshot.BotProfileId, windowId, "Failed", "stationary_hold_pause_exception", exception.GetType().Name, now);
            }
            reason = "medic_stationary_hold_pause_failed:" + exception.GetType().Name;
            return false;
        }

        reason = "medic_stationary_hold_current:window=" + Safe(windowId) + ":path_preserved=true";
        return true;
    }

    internal static SearchTranslationDispatchResult TryHandleSearchTranslationGate(
        object pathData,
        object sainBot,
        BotOwner botOwner,
        DateTimeOffset now,
        out string reason)
    {
        reason = "no_medic_combat_rendezvous";
        if (botOwner == null || string.IsNullOrWhiteSpace(botOwner.ProfileId))
        {
            return SearchTranslationDispatchResult.NoLease;
        }

        MovementLease? lease;
        lock (Sync)
        {
            ActiveByBot.TryGetValue(botOwner.ProfileId, out lease);
        }
        if (lease == null || !lease.CombatSidecar || lease.ExpiresAtUtc <= now)
        {
            return SearchTranslationDispatchResult.NoLease;
        }

        if (!VanguardMedicSupportSessionStore.TryGetActive(out VanguardMedicSupportSession session)
            || !string.Equals(session.SessionId, lease.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            Release(botOwner.ProfileId, lease.SessionId, now, "session_not_current_at_search_gate");
            reason = "medic_session_not_current";
            return SearchTranslationDispatchResult.HandledFailOpen;
        }

        if (!VanguardMainIntentScheduler.IsMedicSupportCombatMovementSidecarCurrent(
                botOwner.ProfileId,
                now,
                lease.ThreatId,
                lease.SchedulerWindowId,
                lease.SessionId,
                out string schedulerReason))
        {
            Release(botOwner.ProfileId, lease.SessionId, now, "combat_sidecar_not_current:" + schedulerReason);
            reason = "combat_sidecar_not_current:" + schedulerReason;
            return SearchTranslationDispatchResult.HandledFailOpen;
        }

        if (!VanguardReturnMovementCommandStore.TryGetExactOwned(
                botOwner.ProfileId,
                lease.LeaseId,
                lease.RequestKind,
                lease.Generation,
                now,
                out _,
                out string commandReason))
        {
            Release(botOwner.ProfileId, lease.SessionId, now, "movement_command_not_current:" + commandReason);
            reason = "movement_command_not_current:" + commandReason;
            return SearchTranslationDispatchResult.HandledFailOpen;
        }

        if (!SearchTranslationGateBridge.TryApplyTacticalMovementTranslationGate(
                pathData,
                sainBot,
                botOwner,
                lease.ThreatId,
                lease.RequestKind,
                lease.LeaseId,
                lease.Generation,
                lease.Destination,
                Math.Max(0.75f, lease.ReachDistanceMeters),
                lease.PreparedPath,
                out string bindReason,
                allowOpportunityCombatFlank: true,
                allowStandAndShootCombatAdvance: true,
                allowDistantVisibleSearchAction: true))
        {
            reason = "medic_translation_bind_denied:" + bindReason;
            return SearchTranslationDispatchResult.HandledFailOpen;
        }

        reason = "medic_combat_rendezvous_native_sain_path:" + Safe(lease.Role);
        return SearchTranslationDispatchResult.PassThroughOwned;
    }

    public static void Release(string botProfileId, string sessionId, DateTimeOffset now, string reason)
    {
        MovementLease? lease = null;
        lock (Sync)
        {
            if (ActiveByBot.TryGetValue(botProfileId, out MovementLease? current)
                && string.Equals(current.SessionId, Normalize(sessionId), StringComparison.OrdinalIgnoreCase))
            {
                lease = current;
                ActiveByBot.Remove(botProfileId);
            }
        }
        if (lease != null)
        {
            ReleasePhysical(lease, now, reason, closeWindow: true);
        }
    }

    // A medical MOVE -> HOLD or MOVE -> native care transition retires only its
    // own physical command, not the pair primary window that also protects the
    // partner from TA/Cohesion reacquisition.
    private static void ReleaseMovementPreservingWindow(
        string botProfileId, string sessionId, DateTimeOffset now, string reason)
    {
        MovementLease? lease = null;
        lock (Sync)
        {
            if (ActiveByBot.TryGetValue(botProfileId, out var current)
                && string.Equals(current.SessionId, Normalize(sessionId), StringComparison.OrdinalIgnoreCase))
            {
                lease = current;
                ActiveByBot.Remove(botProfileId);
            }
        }
        if (lease != null) ReleasePhysical(lease, now, reason, closeWindow: false);
    }

    public static void ReleaseSession(string sessionId, DateTimeOffset now, string reason, bool preservePrimaryWindows = false)
    {
        List<MovementLease> leases = new();
        List<StationaryHoldLease> holds = new();
        lock (Sync)
        {
            foreach (MovementLease lease in ActiveByBot.Values)
            {
                if (string.Equals(lease.SessionId, Normalize(sessionId), StringComparison.OrdinalIgnoreCase))
                {
                    leases.Add(lease);
                }
            }
            foreach (MovementLease lease in leases)
            {
                ActiveByBot.Remove(lease.BotProfileId);
            }
            foreach (StationaryHoldLease hold in StationaryByBot.Values)
            {
                if (string.Equals(hold.SessionId, Normalize(sessionId), StringComparison.OrdinalIgnoreCase))
                {
                    holds.Add(hold);
                }
            }
            foreach (StationaryHoldLease hold in holds)
            {
                StationaryByBot.Remove(hold.BotProfileId);
            }
        }
        foreach (MovementLease lease in leases)
        {
            ReleasePhysical(lease, now, reason, closeWindow: !preservePrimaryWindows);
        }
        foreach (StationaryHoldLease hold in holds)
        {
            ReleaseStationaryPhysical(hold, now, reason, closeWindow: !preservePrimaryWindows);
        }
    }

    private static void ReleaseHold(string botProfileId, string sessionId, DateTimeOffset now, string reason, bool preservePrimaryWindow = false)
    {
        StationaryHoldLease? hold = null;
        lock (Sync)
        {
            if (StationaryByBot.TryGetValue(botProfileId, out var current)
                && string.Equals(current.SessionId, Normalize(sessionId), StringComparison.OrdinalIgnoreCase))
            {
                hold = current;
                StationaryByBot.Remove(botProfileId);
            }
        }
        if (hold != null)
        {
            ReleaseStationaryPhysical(hold, now, reason, closeWindow: !preservePrimaryWindow);
        }
    }

    private static void ReleaseStationaryPhysical(
        StationaryHoldLease hold, DateTimeOffset now, string reason, bool closeWindow)
    {
        bool generationCurrent;
        lock (Sync)
        {
            generationCurrent = StationaryPhysicalGenerationByBot.TryGetValue(hold.BotProfileId, out long generation)
                && generation == hold.Generation;
            if (generationCurrent)
            {
                StationaryPhysicalGenerationByBot.Remove(hold.BotProfileId);
            }
        }
        bool current = VanguardMainIntentScheduler.IsMedicSupportAuxiliaryWindowCurrent(
            hold.BotProfileId, now, hold.WindowId, hold.SessionId, out _);
        bool incarnationCurrent = VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(
                hold.BotProfileId, out VanguardRaidOperatorRuntimeRecord runtime)
            && ReferenceEquals(runtime.BotOwner, hold.BotOwner);
        bool resumed = false;
        if (generationCurrent && current && incarnationCurrent && !hold.BotOwner.IsDead)
        {
            var mover = hold.BotOwner.Mover;
            // Never undo a pause that a successor or another authority has refreshed.
            if (mover != null && mover.Pause
                && Math.Abs(mover.PauseEndTime - hold.PauseEndTime) <= StationaryPauseEndTolerance
                && Math.Abs(mover.LastPathSetTime - hold.PathSetTime) <= 0.001f)
            {
                mover.MovementResume();
                resumed = true;
            }
        }
        // A stale callback from an older pause generation must not close an auxiliary window
        // that the same session has already refreshed for a newer physical generation.
        if (closeWindow && generationCurrent)
        {
            VanguardMainIntentScheduler.TryCloseMedicSupportAuxiliaryWindow(
                hold.BotProfileId, hold.WindowId, "Interrupted", reason,
                "timedPauseResumed=" + Bool(resumed) + ";stale=" + Bool(!current), now);
        }
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_MEDIC_PHYSICAL_TERMINAL session={Safe(hold.SessionId)}; bot={Safe(hold.BotProfileId)}; domain=Medical; window={Safe(hold.WindowId)}; lease={Safe(hold.LeaseId)}; generation={hold.Generation}; incarnationCurrent={Bool(incarnationCurrent)}; outcome={Safe(reason)}; released={Bool(resumed)}; ignoredStale={Bool(!current || !generationCurrent || !incarnationCurrent)}; pathPreserved=true; pauseAutoExpiry=true");
    }

    private static void ReleasePhysical(MovementLease lease, DateTimeOffset now, string reason, bool closeWindow)
    {
        string commandCleanup = VanguardReturnMovementCommandStore.ClearOwned(
            lease.BotProfileId,
            lease.LeaseId,
            lease.StartedAtUtc,
            "medic_support_move_release:" + reason);
        bool pathReleased = lease.CombatSidecar && SearchTranslationGateBridge.ReleaseTacticalMovementPathBinding(
            lease.BotProfileId,
            lease.RequestKind,
            lease.LeaseId,
            lease.Generation,
            "medic_support_move_release:" + reason);
        if (closeWindow && !lease.CombatSidecar && !string.Equals(lease.SchedulerWindowId, "none", StringComparison.OrdinalIgnoreCase))
        {
            VanguardMainIntentScheduler.TryCloseMedicSupportAuxiliaryWindow(
                lease.BotProfileId,
                lease.SchedulerWindowId,
                "Interrupted",
                reason,
                "commandCleanup=" + commandCleanup + ";pathReleased=" + Bool(pathReleased),
                now);
        }

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_MEDIC_RENDEZVOUS_MOVE_RELEASED session={Safe(lease.SessionId)}; bot={Safe(lease.BotProfileId)}; role={Safe(lease.Role)}; combatSidecar={Bool(lease.CombatSidecar)}; commandCleanup={Safe(commandCleanup)}; pathReleased={Bool(pathReleased)}; reason={Safe(reason)}; staleResume=false");
    }

    private static bool IsCombatSearchContext(OperatorDecisionSnapshot snapshot)
        => snapshot.Sain.IsInCombat == true
            || snapshot.Threat.ResidualThreat
            || snapshot.ThreatScan.CombatContext
            || (snapshot.PrimaryExecution.Active
                && string.Equals(snapshot.PrimaryExecution.WindowKind, VanguardPrimaryExecutionWindowKinds.SainCombatRelease, StringComparison.OrdinalIgnoreCase));

    private static bool IsHardSurvivalBlock(OperatorDecisionSnapshot snapshot, out string reason)
    {
        if (!snapshot.Alive)
        {
            reason = "actor_dead";
            return true;
        }
        if (snapshot.Sain.RunningToCover == true)
        {
            reason = "sain_running_to_cover";
            return true;
        }
        bool unprotectedIncomingFire = snapshot.Medical.Safety.IncomingFireRecent
            && !snapshot.Medical.Safety.CoveredSuppressionOpportunity;
        if (snapshot.Threat.DirectThreat
            || snapshot.Medical.Safety.EnemyCanShoot
            || snapshot.Medical.Safety.ImmediateCombatBlock
            || unprotectedIncomingFire)
        {
            reason = "direct_or_unprotected_incoming_fire_survival";
            return true;
        }
        // An older snapshot may still describe HardReturn after the scheduler
        // has committed the medical pair and retired the previous movement.
        // Never disregard grenade or actual danger above; if the pair is not
        // current, the HardReturn snapshot remains a movement veto.
        if (snapshot.PrimaryExecution.Active
            && string.Equals(snapshot.PrimaryExecution.WindowKind, VanguardPrimaryExecutionWindowKinds.EmergencyGrenadeEvasion, StringComparison.OrdinalIgnoreCase))
        {
            reason = "exclusive_survival_primary:" + Safe(snapshot.PrimaryExecution.WindowKind);
            return true;
        }
        if (snapshot.PrimaryExecution.Active
            && string.Equals(snapshot.PrimaryExecution.WindowKind, VanguardPrimaryExecutionWindowKinds.HardReturnMovement, StringComparison.OrdinalIgnoreCase)
            && (!VanguardMedicSupportSessionStore.TryGetActive(out VanguardMedicSupportSession pair)
                || !VanguardMainIntentScheduler.IsOwnMedicPairCurrent(
                    pair.SessionId, pair.CaregiverBotProfileId, pair.PatientBotProfileId, DateTimeOffset.UtcNow)
                || (!string.Equals(snapshot.BotProfileId, pair.CaregiverBotProfileId, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(snapshot.BotProfileId, pair.PatientBotProfileId, StringComparison.OrdinalIgnoreCase))))
        {
            reason = "hard_return_yield_not_committed";
            return true;
        }
        reason = "none";
        return false;
    }

    private static string ResolveThreatId(OperatorDecisionSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Threat.EnemyId)
            && !string.Equals(snapshot.Threat.EnemyId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return snapshot.Threat.EnemyId.Trim();
        }
        if (!string.IsNullOrWhiteSpace(snapshot.ThreatScan.CurrentThreatId)
            && !string.Equals(snapshot.ThreatScan.CurrentThreatId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return snapshot.ThreatScan.CurrentThreatId.Trim();
        }
        return "none";
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        left.y = 0f;
        right.y = 0f;
        return Vector3.Distance(left, right);
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Format(Vector3 value) => value.x.ToString("0.00", CultureInfo.InvariantCulture)
        + "," + value.y.ToString("0.00", CultureInfo.InvariantCulture)
        + "," + value.z.ToString("0.00", CultureInfo.InvariantCulture);

    private sealed class MovementLease
    {
        public string SessionId { get; init; } = "none";
        public string BotProfileId { get; init; } = "none";
        public string OperatorId { get; init; } = "none";
        public string Role { get; init; } = "none";
        public string LeaseId { get; init; } = "none";
        public string RequestKind { get; init; } = "none";
        public long Generation { get; init; }
        public string SchedulerWindowId { get; init; } = "none";
        public string ThreatId { get; init; } = "none";
        public Vector3 Destination { get; set; }
        public float ReachDistanceMeters { get; set; }
        public NavMeshPath PreparedPath { get; set; } = new();
        public string PathSummary { get; set; } = "none";
        public float PathDistanceMeters { get; set; }
        public bool CombatSidecar { get; init; }
        public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.MinValue;
        public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.MinValue;
        public float LastMeasuredDistanceMeters { get; set; } = float.MaxValue;
    }

    private sealed class StationaryHoldLease
    {
        public string SessionId { get; init; } = "none";
        public string BotProfileId { get; init; } = "none";
        public BotOwner BotOwner { get; init; } = null!;
        public string WindowId { get; init; } = "none";
        public string LeaseId { get; init; } = "none";
        public long Generation { get; init; }
        public float PauseEndTime { get; init; }
        public float PathSetTime { get; init; }
        public bool HadPath { get; init; }
    }
}
#endif
