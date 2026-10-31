#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.Movement.Brain;

// Responsibility: turns already-qualified combat truth into bounded, visible squad-positioning opportunities while delegating weapon preference to the dedicated dynamic preference sidecar.
// Flow: dynamic weapon preference refresh gets first refusal, then fresh qualified threat truth arbitrates emergency cohesion, SAIN-aligned break/retreat bounds, pre-portal support for an active BreachEntry, a short follower relay behind an active ordinary DirectCommit, buddy/overwatch support, one bounded authored InteriorDevelop passage step, bounded advance and soft regroup; FlankSupport binds to the exact current AltDevelop maneuver epoch so a short physical-leasing gap cannot erase a still-current alternate-axis intent; a completed BoundAdvance may publish one short reciprocal mover/support handoff preference, but the reciprocal pair must pass the same live gates and otherwise fails open to ordinary acquisition; one fresh path is issued through the existing TC/SAIN native path bridge and revalidated until arrival or fail-open release.
// Authority boundary: Vanguard selects who should reposition and where. The dedicated weapon-preference service may request native slot convergence, while SAIN/EFT retain target, perception, aim, fire, suppression, steering, reload and actual weapon-switch execution.
// Invariant: no movement opportunity starts without an existing SAIN combat primary for the exact threat; ThresholdSupport requires the exact live BreachEntry plus current Direct membership, keeps Vanguard-selected destination/path on the approach side of the confirmed portal and creates no follow-on entry; AssaultRelay requires the exact live ordinary DirectCommit plus current Direct membership and never chains from BreachEntry; InteriorDevelop is a single disposable authored PassagePath step or conservative generic same-volume fallback with exact support/context revalidation and at most one authored portal crossing, never a room-clearing chain; no close visible dogfight is displaced by Vanguard movement; FlankSupport requires an exact current AltDevelop maneuver already seeded by a physical lead lease and cannot cross maneuver-epoch rollover; BoundAdvance never converts SeekCover/Retreat into aggression and reciprocal handoff is a one-shot preference rather than a forced sequence, while BoundRetreat/BreakContact require either an existing SAIN retreat signal or pressure-qualified stationary/search action and a fresh support partner; one owner/threat has at most one short generation-stamped non-sprinting disposable lease.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class TacticalOpportunityMovementService
{
    public const string StatusTag = "VANGUARD_TC_TACTICAL_OPPORTUNITY_STATUS";
    public const string RequestKind = "TacticalCoordinatorOpportunityPosition";

    private const float SoftRegroupDistanceMeters = 30f;
    private const float CommandAnchorRadiusMeters = 1.25f;
    private const float MinimumMovementMeters = 2.5f;
    private const float ProgressDistanceMeters = 0.40f;
    internal const float BoundSupportMaxSeparationMeters = 36f;
    private const float BoundAdvanceBaselineMinimumSupportLeadMeters = 4f;
    private const float BoundAdvanceBaselineReciprocalMinimumSupportLeadMeters = 1.5f;
    private const float BoundRetreatMinimumSupportDepthMeters = 2.5f;
    private const float BoundRetreatStepMeters = 7.5f;
    private const float BreakContactStepMeters = 10.5f;
    private const int BreakContactMinimumPressuredMembers = 2;
    private const int MaxOwnerThreatGroupsPerAcquisition = 6;
    private const int MaxOverwatchCandidateProbes = 2;

    private static readonly TimeSpan AcquisitionInterval = TimeSpan.FromSeconds(0.50d);
    private static readonly TimeSpan DefaultMaximumLeaseDuration = TimeSpan.FromSeconds(7.5d);
    private static readonly TimeSpan OverwatchMaximumLeaseDuration = TimeSpan.FromSeconds(10.0d);
    private static readonly TimeSpan BoundMaximumLeaseDuration = TimeSpan.FromSeconds(6.5d);
    private static readonly TimeSpan BoundAdvanceHandoffTtl = TimeSpan.FromSeconds(9.0d);
    private static readonly TimeSpan NoProgressDuration = TimeSpan.FromSeconds(2.25d);
    private static readonly TimeSpan ReacquireCooldown = TimeSpan.FromSeconds(3.0d);
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(10d);

    private static readonly object Sync = new();
    private static readonly Dictionary<string, ActiveOpportunityLease> ActiveByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ActiveBotByOwnerThreat = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> CooldownUntilByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> CooldownUntilByOwnerThreat = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BoundAdvanceHandoff> BoundAdvanceHandoffByOwnerThreat = new(StringComparer.OrdinalIgnoreCase);

    private static DateTimeOffset nextAcquisitionAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;
    private static int startsTotal;
    private static int completesTotal;
    private static int interruptsTotal;
    private static int failuresTotal;
    private static int overwatchStartsTotal;
    private static int buddySupportStartsTotal;
    private static int regroupStartsTotal;
    private static int boundAdvanceStartsTotal;
    private static int boundAdvanceHandoffsPreparedTotal;
    private static int boundAdvanceHandoffStartsTotal;
    private static int boundAdvanceHandoffFallbacksTotal;
    private static int boundRetreatStartsTotal;
    private static int breakContactStartsTotal;
    private static int thresholdSupportStartsTotal;
    private static int thresholdSupportContextLossInterruptsTotal;
    private static int assaultRelayStartsTotal;
    private static int assaultRelayContextLossInterruptsTotal;
    private static int flankSupportStartsTotal;
    private static int flankSupportActiveLeaseProofStartsTotal;
    private static int flankSupportManeuverContinuityStartsTotal;
    private static int flankSupportContextLossInterruptsTotal;
    private static int flankSupportRouteGapContinuityTicksTotal;
    private static int interiorDevelopStartsTotal;
    private static int interiorDevelopGenericStartsTotal;
    private static int interiorDevelopContextLossInterruptsTotal;
    private static int interiorDevelopCompletionReplanRequestsTotal;
    private static int interiorDevelopCompletionReplanCoalescedTotal;
    private static int boundSupportLossInterruptsTotal;
    private static int acquisitionDeferralsTotal;
    private static int pathRejectsTotal;
    private static int schedulerRejectsTotal;
    private static int commandRejectsTotal;
    private static int nativePathTicksTotal;

    public static void Tick(DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            ResetForRaidLifecycle("not_raid_authority");
            return;
        }

        TickActiveLeases(now);
        TacticalEstablishmentShadowService.Tick(now);
        if (now >= nextAcquisitionAtUtc)
        {
            nextAcquisitionAtUtc = now + AcquisitionInterval;
            TryAcquireOpportunity(now);
        }

        EmitSummaryIfDue(now);
    }

    internal static bool IsOpportunityRequest(string? requestKind)
        => string.Equals(requestKind?.Trim(), RequestKind, StringComparison.OrdinalIgnoreCase);

    internal static bool HasActiveOpportunityForBot(string? botProfileId)
    {
        if (string.IsNullOrWhiteSpace(botProfileId)) return false;
        lock (Sync)
        {
            return ActiveByBot.ContainsKey(botProfileId.Trim());
        }
    }

    internal static bool TryGetActiveSupportContributionForBot(
        string? botProfileId,
        string expectedThreatId,
        out string kind)
    {
        kind = "none";
        ActiveOpportunityLease? lease = FindActive(botProfileId);
        if (lease == null
            || !string.Equals(NormalizeThreat(lease.ThreatId), NormalizeThreat(expectedThreatId), StringComparison.OrdinalIgnoreCase)
            || !IsSupportEnvelopeOpportunityKind(lease.Kind))
        {
            return false;
        }

        kind = lease.Kind;
        return true;
    }

    internal static bool TryGetThresholdSupportSynchronization(
        string ownerProfileId,
        string threatId,
        string leadBotProfileId,
        string sourceLeaseId,
        DateTimeOffset now,
        out string supportBotProfileId,
        out bool physicallyEstablished,
        out string reason)
    {
        supportBotProfileId = string.Empty;
        physicallyEstablished = false;
        reason = "threshold_support_missing";

        ActiveOpportunityLease? thresholdLease;
        lock (Sync)
        {
            thresholdLease = ActiveByBot.Values.FirstOrDefault(item =>
                string.Equals(item.Kind, "ThresholdSupport", StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeThreat(item.ThreatId), NormalizeThreat(threatId), StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.SupportBotProfileId, leadBotProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.SupportContextId, sourceLeaseId, StringComparison.OrdinalIgnoreCase));
        }
        if (thresholdLease == null)
        {
            return false;
        }
        if (!IsThresholdSupportContextCurrent(thresholdLease, now, out string contextReason))
        {
            reason = "threshold_support_context_not_current:" + contextReason;
            return false;
        }
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(
                thresholdLease.BotProfileId,
                out VanguardRaidOperatorRuntimeRecord runtime)
            || runtime.BotOwner == null
            || runtime.BotOwner.IsDead)
        {
            reason = "threshold_support_runtime_missing_or_dead";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(thresholdLease.BotProfileId, out OperatorDecisionSnapshot snapshot)
            || snapshot == null
            || !snapshot.Alive)
        {
            reason = "threshold_support_snapshot_missing_or_dead";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(snapshot, now, out string freshnessReason))
        {
            reason = "threshold_support_snapshot_not_current:" + Safe(freshnessReason);
            return false;
        }
        if (!IsTranslationCompatibleForKind(snapshot, thresholdLease.ThreatId, "ThresholdSupport", out string authorityReason))
        {
            reason = "threshold_support_authority_changed:" + authorityReason;
            return false;
        }

        supportBotProfileId = thresholdLease.BotProfileId;
        float distance = HorizontalDistance(runtime.BotOwner.Position, thresholdLease.Destination);
        physicallyEstablished = distance <= CommandAnchorRadiusMeters + 0.45f;
        reason = physicallyEstablished
            ? "threshold_support_physically_established"
            : "threshold_support_moving_to_preportal_anchor:distance=" + distance.ToString("0.0", CultureInfo.InvariantCulture);
        return true;
    }

    internal static bool IsLayerContextCurrent(
        string botProfileId,
        VanguardReturnMovementCommand command,
        DateTimeOffset now,
        out string reason)
    {
        reason = "opportunity_lease_missing";
        ActiveOpportunityLease? lease = FindActive(botProfileId);
        if (lease == null)
        {
            return false;
        }
        if (!string.Equals(lease.LeaseId, command.LeaseId, StringComparison.OrdinalIgnoreCase)
            || lease.Generation != command.Generation
            || !string.Equals(command.RequestKind, RequestKind, StringComparison.OrdinalIgnoreCase))
        {
            reason = "command_identity_changed";
            return false;
        }
        if (!VanguardMainIntentScheduler.IsTacticalCoordinatorCombatMovementSidecarCurrent(
                lease.BotProfileId,
                now,
                lease.ThreatId,
                lease.SchedulerWindowId,
                out string schedulerReason))
        {
            reason = "combat_sidecar_not_current:" + schedulerReason;
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(lease.BotProfileId, out OperatorDecisionSnapshot snapshot))
        {
            reason = "local_authority_not_current:snapshot_missing";
            return false;
        }
        if (!IsTranslationCompatibleForKind(snapshot, lease.ThreatId, lease.Kind, out string authorityReason))
        {
            reason = "local_authority_not_current:" + authorityReason;
            return false;
        }
        if (!MarksmanForwardRoleDisciplinePolicy.CanAuthorizeForwardTranslation(
                snapshot,
                lease.ThreatId,
                lease.Destination,
                lease.Kind,
                now,
                out string roleDisciplineReason))
        {
            reason = "marksman_role_discipline_not_current:" + roleDisciplineReason;
            return false;
        }
        if (!IsBoundSupportCurrent(lease, snapshot.Position, now, out string supportReason))
        {
            reason = "bound_support_not_current:" + supportReason;
            return false;
        }
        if (!IsThresholdSupportContextCurrent(lease, now, out string thresholdReason))
        {
            reason = "threshold_support_context_not_current:" + thresholdReason;
            return false;
        }
        if (!IsAssaultRelayContextCurrent(lease, now, out string relayReason))
        {
            reason = "assault_relay_context_not_current:" + relayReason;
            return false;
        }
        if (!IsFlankSupportContextCurrent(lease, now, false, out string flankSupportReason))
        {
            reason = "flank_support_context_not_current:" + flankSupportReason;
            return false;
        }
        if (!IsInteriorDevelopContextCurrent(lease, now, out string interiorDevelopReason))
        {
            reason = "interior_develop_context_not_current:" + interiorDevelopReason;
            return false;
        }
        reason = "current:" + lease.Kind;
        return true;
    }

    internal static SearchTranslationDispatchResult TryHandleSearchTranslationGate(
        object pathData,
        object sainBot,
        BotOwner botOwner,
        DateTimeOffset now,
        out string reason)
    {
        reason = "no_tactical_opportunity_lease";
        if (botOwner == null || string.IsNullOrWhiteSpace(botOwner.ProfileId))
        {
            return SearchTranslationDispatchResult.NoLease;
        }

        ActiveOpportunityLease? lease = FindActive(botOwner.ProfileId);
        if (lease == null)
        {
            return SearchTranslationDispatchResult.NoLease;
        }

        if (!VanguardReturnMovementCommandStore.TryGetExactOwned(
                lease.BotProfileId,
                lease.LeaseId,
                RequestKind,
                lease.Generation,
                now,
                out _,
                out string commandReason))
        {
            bool released = FinishLease(lease, now, "Interrupted", "command_changed_at_search_gate:" + commandReason, applyCooldown: false);
            reason = "command_changed:" + commandReason;
            return released ? SearchTranslationDispatchResult.ReleasedCurrentTick : SearchTranslationDispatchResult.HandledFailOpen;
        }

        if (!VanguardMainIntentScheduler.IsTacticalCoordinatorCombatMovementSidecarCurrent(
                lease.BotProfileId,
                now,
                lease.ThreatId,
                lease.SchedulerWindowId,
                out string schedulerReason))
        {
            bool released = FinishLease(lease, now, "Interrupted", "combat_sidecar_changed_at_search_gate:" + schedulerReason, applyCooldown: false);
            reason = "combat_sidecar_changed:" + schedulerReason;
            return released ? SearchTranslationDispatchResult.ReleasedCurrentTick : SearchTranslationDispatchResult.HandledFailOpen;
        }

        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(lease.BotProfileId, out OperatorDecisionSnapshot decision))
        {
            bool released = FinishLease(lease, now, "Interrupted", "local_authority_changed_at_search_gate:snapshot_missing", applyCooldown: false);
            reason = "local_authority_changed:snapshot_missing";
            return released ? SearchTranslationDispatchResult.ReleasedCurrentTick : SearchTranslationDispatchResult.HandledFailOpen;
        }
        if (!IsTranslationCompatibleForKind(decision, lease.ThreatId, lease.Kind, out string authorityReason))
        {
            bool released = FinishLease(lease, now, "Interrupted", "local_authority_changed_at_search_gate:" + authorityReason, applyCooldown: false);
            reason = "local_authority_changed:" + authorityReason;
            return released ? SearchTranslationDispatchResult.ReleasedCurrentTick : SearchTranslationDispatchResult.HandledFailOpen;
        }
        if (!MarksmanForwardRoleDisciplinePolicy.CanAuthorizeForwardTranslation(
                decision,
                lease.ThreatId,
                lease.Destination,
                lease.Kind,
                now,
                out string roleDisciplineReason))
        {
            bool released = FinishLease(lease, now, "Interrupted", "marksman_role_discipline_changed_at_search_gate:" + roleDisciplineReason, applyCooldown: false);
            reason = "marksman_role_discipline_changed:" + roleDisciplineReason;
            return released ? SearchTranslationDispatchResult.ReleasedCurrentTick : SearchTranslationDispatchResult.HandledFailOpen;
        }
        if (!IsBoundSupportCurrent(lease, decision.Position, now, out string supportReason))
        {
            boundSupportLossInterruptsTotal++;
            bool released = FinishLease(lease, now, "Interrupted", "bound_support_changed_at_search_gate:" + supportReason, applyCooldown: false);
            reason = "bound_support_changed:" + supportReason;
            return released ? SearchTranslationDispatchResult.ReleasedCurrentTick : SearchTranslationDispatchResult.HandledFailOpen;
        }
        if (!IsThresholdSupportContextCurrent(lease, now, out string thresholdReason))
        {
            thresholdSupportContextLossInterruptsTotal++;
            bool released = FinishLease(lease, now, "Interrupted", "threshold_support_context_changed_at_search_gate:" + thresholdReason, applyCooldown: false);
            reason = "threshold_support_context_changed:" + thresholdReason;
            return released ? SearchTranslationDispatchResult.ReleasedCurrentTick : SearchTranslationDispatchResult.HandledFailOpen;
        }
        if (!IsAssaultRelayContextCurrent(lease, now, out string relayReason))
        {
            assaultRelayContextLossInterruptsTotal++;
            bool released = FinishLease(lease, now, "Interrupted", "assault_relay_context_changed_at_search_gate:" + relayReason, applyCooldown: false);
            reason = "assault_relay_context_changed:" + relayReason;
            return released ? SearchTranslationDispatchResult.ReleasedCurrentTick : SearchTranslationDispatchResult.HandledFailOpen;
        }
        if (!IsFlankSupportContextCurrent(lease, now, false, out string flankSupportReason))
        {
            flankSupportContextLossInterruptsTotal++;
            bool released = FinishLease(lease, now, "Interrupted", "flank_support_context_changed_at_search_gate:" + flankSupportReason, applyCooldown: false);
            reason = "flank_support_context_changed:" + flankSupportReason;
            return released ? SearchTranslationDispatchResult.ReleasedCurrentTick : SearchTranslationDispatchResult.HandledFailOpen;
        }
        if (!IsInteriorDevelopContextCurrent(lease, now, out string interiorDevelopReason))
        {
            interiorDevelopContextLossInterruptsTotal++;
            bool released = FinishLease(lease, now, "Interrupted", "interior_develop_context_changed_at_search_gate:" + interiorDevelopReason, applyCooldown: false);
            reason = "interior_develop_context_changed:" + interiorDevelopReason;
            return released ? SearchTranslationDispatchResult.ReleasedCurrentTick : SearchTranslationDispatchResult.HandledFailOpen;
        }

        ResolveLiveTranslationFlags(lease.Kind, out bool allowOpportunityCombatFlank, out bool allowStandAndShootCombatAdvance, out bool allowDistantVisibleSearchAction);
        if (!SearchTranslationGateBridge.TryApplyTacticalMovementTranslationGate(
                pathData,
                sainBot,
                botOwner,
                lease.ThreatId,
                RequestKind,
                lease.LeaseId,
                lease.Generation,
                lease.Destination,
                CommandAnchorRadiusMeters,
                lease.PreparedPath,
                out string gateReason,
                allowOpportunityCombatFlank: allowOpportunityCombatFlank,
                allowStandAndShootCombatAdvance: allowStandAndShootCombatAdvance,
                allowDistantVisibleSearchAction: allowDistantVisibleSearchAction))
        {
            if (string.Equals(gateReason, "goal_enemy_changed", StringComparison.OrdinalIgnoreCase)
                && SearchTranslationGateBridge.HasConcreteAltDevelopLiveGoalEnemyMismatch(
                    botOwner,
                    lease.ThreatId,
                    out string liveGoalEnemyProfileId))
            {
                TacticalEngagementContextShadowService.ObserveLiveGoalEnemyRetarget(
                    lease.OwnerProfileId,
                    lease.ThreatId,
                    liveGoalEnemyProfileId,
                    lease.BotProfileId,
                    lease.Kind,
                    lease.LeaseId,
                    decision,
                    now);
            }

            bool released = FinishLease(lease, now, "Interrupted", "live_translation_gate:" + gateReason, applyCooldown: false);
            reason = gateReason;
            return released ? SearchTranslationDispatchResult.ReleasedCurrentTick : SearchTranslationDispatchResult.HandledFailOpen;
        }

        lease.NativePathTicks++;
        nativePathTicksTotal++;
        reason = "tactical_opportunity_native_sain_path_current:" + lease.Kind;
        return SearchTranslationDispatchResult.PassThroughOwned;
    }

    internal static bool DrainForHigherAuthority(string botProfileId, DateTimeOffset now, string reason)
    {
        ActiveOpportunityLease? lease = FindActive(botProfileId);
        return lease != null && FinishLease(lease, now, "Interrupted", "higher_authority:" + Safe(reason), applyCooldown: false);
    }

    internal static bool ForgetRuntimePresence(string botProfileId, DateTimeOffset now, string reason)
    {
        ActiveOpportunityLease? lease = FindActive(botProfileId);
        bool movementReleased = lease != null
            && FinishLease(lease, now, "Interrupted", "runtime_presence_lost:" + Safe(reason), applyCooldown: false);
        bool handoffReleased = ForgetBoundAdvanceHandoffPresence(botProfileId, reason);
        bool weaponPreferenceReleased = VanguardDynamicWeaponPreferenceService.ForgetRuntimePresence(
            botProfileId,
            reason);
        return movementReleased || handoffReleased || weaponPreferenceReleased;
    }

    public static void ResetForRaidLifecycle(string reason)
    {
        ActiveOpportunityLease[] active;
        lock (Sync)
        {
            active = ActiveByBot.Values.ToArray();
            ActiveByBot.Clear();
            ActiveBotByOwnerThreat.Clear();
            CooldownUntilByBot.Clear();
            CooldownUntilByOwnerThreat.Clear();
            BoundAdvanceHandoffByOwnerThreat.Clear();
        }

        VanguardDynamicWeaponPreferenceService.ResetForRaidLifecycle(reason);
        TacticalEstablishmentShadowService.ResetForRaidLifecycle(reason);
        TacticalEngagementContextShadowService.ResetForRaidLifecycle(reason);
        UnsupportedPenetrationShadowService.ResetForRaidLifecycle(reason);

        foreach (ActiveOpportunityLease lease in active)
        {
            SearchTranslationGateBridge.ReleaseTacticalMovementPathBinding(
                lease.BotProfileId,
                RequestKind,
                lease.LeaseId,
                lease.Generation,
                "tc_tactical_opportunity_raid_reset:" + Safe(reason));
            VanguardReturnMovementCommandStore.ClearOwned(
                lease.BotProfileId,
                lease.LeaseId,
                lease.StartedAtUtc,
                "tc_tactical_opportunity_raid_reset:" + Safe(reason));
        }

        nextAcquisitionAtUtc = DateTimeOffset.MinValue;
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        startsTotal = 0;
        completesTotal = 0;
        interruptsTotal = 0;
        failuresTotal = 0;
        overwatchStartsTotal = 0;
        buddySupportStartsTotal = 0;
        regroupStartsTotal = 0;
        boundAdvanceStartsTotal = 0;
        boundAdvanceHandoffsPreparedTotal = 0;
        boundAdvanceHandoffStartsTotal = 0;
        boundAdvanceHandoffFallbacksTotal = 0;
        boundRetreatStartsTotal = 0;
        breakContactStartsTotal = 0;
        thresholdSupportStartsTotal = 0;
        thresholdSupportContextLossInterruptsTotal = 0;
        assaultRelayStartsTotal = 0;
        assaultRelayContextLossInterruptsTotal = 0;
        flankSupportStartsTotal = 0;
        flankSupportActiveLeaseProofStartsTotal = 0;
        flankSupportManeuverContinuityStartsTotal = 0;
        flankSupportContextLossInterruptsTotal = 0;
        flankSupportRouteGapContinuityTicksTotal = 0;
        interiorDevelopStartsTotal = 0;
        interiorDevelopGenericStartsTotal = 0;
        interiorDevelopContextLossInterruptsTotal = 0;
        interiorDevelopCompletionReplanRequestsTotal = 0;
        interiorDevelopCompletionReplanCoalescedTotal = 0;
        boundSupportLossInterruptsTotal = 0;
        acquisitionDeferralsTotal = 0;
        pathRejectsTotal = 0;
        schedulerRejectsTotal = 0;
        commandRejectsTotal = 0;
        nativePathTicksTotal = 0;
    }

    private static void TryAcquireOpportunity(DateTimeOffset now)
    {
        PruneExpiredBoundAdvanceHandoffs(now);

        OperatorDecisionSnapshot[] freshSnapshots = VanguardOperatorDecisionSnapshotService.GetLatestSnapshots()
            .Where(item => item != null
                && item.Alive
                && !string.IsNullOrWhiteSpace(item.OwnerProfileId)
                && !string.IsNullOrWhiteSpace(item.BotProfileId))
            .Where(item => VanguardOperatorDecisionSnapshotService.IsFresh(item, now, out _))
            .ToArray();

        // Dynamic weapon preference is evaluated before movement reservations. It consumes only current carried
        // equipment plus already-qualified Decision/Awareness threat truth, preserves native EFT main/support
        // topology, and may start at most one native selector convergence before movement arbitration continues.
        if (VanguardDynamicWeaponPreferenceService.Tick(freshSnapshots, now))
        {
            return;
        }

        OperatorDecisionSnapshot[] snapshots = freshSnapshots
            .Where(item => item.Threat.HasThreat
                && !string.IsNullOrWhiteSpace(NormalizeThreat(item.Threat.EnemyId)))
            .ToArray();

        foreach (IGrouping<string, OperatorDecisionSnapshot> group in snapshots
            .GroupBy(item => OwnerThreatKey(item.OwnerProfileId, item.Threat.EnemyId), StringComparer.OrdinalIgnoreCase)
            .Take(MaxOwnerThreatGroupsPerAcquisition))
        {
            OperatorDecisionSnapshot[] members = group.ToArray();
            if (members.Length == 0)
            {
                continue;
            }
            if (IsOwnerThreatReserved(group.Key, now)
                && !TryPreemptLowerPriorityOpportunityForThresholdSupport(members, group.Key, now))
            {
                continue;
            }

            // Emergency cohesion first: a combat-aware sidecar must not leave a materially detached Operator
            // wandering alone merely because SAIN has a valid target. Close visible dogfights remain a hard veto.
            OperatorDecisionSnapshot? emergencyRegroup = members
                .Where(IsDangerExposed)
                .Where(item => !IsIntentionalTacticalSeparation(item, now))
                .Where(item => item.SquadCohesion.OwnerPosition.HasValue)
                .Where(item => item.SquadCohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters)
                .OrderByDescending(item => item.SquadCohesion.OperatorDistanceToOwner)
                .FirstOrDefault();
            if (emergencyRegroup != null
                && TryStartRegroup(emergencyRegroup, "EmergencyRegroup", now))
            {
                return;
            }

            if (TryStartBreakContact(members, now))
            {
                return;
            }

            if (TryStartBoundRetreat(members, now))
            {
                return;
            }

            if (TryStartThresholdSupport(members, now))
            {
                return;
            }

            if (TryStartAssaultRelay(members, now))
            {
                return;
            }

            if (TryStartBuddySupport(members, now))
            {
                return;
            }

            if (TryStartOverwatch(members, now))
            {
                return;
            }

            if (TryStartInteriorDevelop(members, now))
            {
                return;
            }

            if (TryStartBoundAdvance(members, now))
            {
                return;
            }

            OperatorDecisionSnapshot? softRegroup = members
                .Where(IsDangerExposed)
                .Where(item => !IsIntentionalTacticalSeparation(item, now))
                .Where(item => item.SquadCohesion.OwnerPosition.HasValue)
                .Where(item => item.SquadCohesion.OperatorDistanceToOwner >= SoftRegroupDistanceMeters)
                .OrderByDescending(item => item.SquadCohesion.OperatorDistanceToOwner)
                .FirstOrDefault();
            if (softRegroup != null
                && TryStartRegroup(softRegroup, "CombatRegroup", now))
            {
                return;
            }
        }
    }

    private static bool TryPreemptLowerPriorityOpportunityForThresholdSupport(
        IReadOnlyList<OperatorDecisionSnapshot> members,
        string ownerThreatKey,
        DateTimeOffset now)
    {
        if (members == null || members.Count < 2)
        {
            return false;
        }

        string ownerProfileId = members[0].OwnerProfileId;
        string threatId = members[0].Threat.EnemyId;
        if (!DirectCommitMovementService.TryGetActiveBreachSupportContext(
                ownerProfileId,
                threatId,
                now,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _))
        {
            return false;
        }

        ActiveOpportunityLease? existing = null;
        lock (Sync)
        {
            if (ActiveBotByOwnerThreat.TryGetValue(ownerThreatKey, out string? activeBot))
            {
                ActiveByBot.TryGetValue(activeBot, out existing);
            }
        }
        if (existing == null)
        {
            return true;
        }
        if (string.Equals(existing.Kind, "ThresholdSupport", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (IsSurvivalOpportunityKind(existing.Kind))
        {
            return false;
        }

        bool released = FinishLease(
            existing,
            now,
            "Interrupted",
            "preempted_by_breach_threshold_support_priority",
            applyCooldown: false);
        if (released)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_TACTICAL_OPPORTUNITY_PREEMPTED kind={Safe(existing.Kind)}; owner={Safe(existing.OwnerProfileId)}; threat={Safe(existing.ThreatId)}; bot={Safe(existing.BotProfileId)}; reason=breach_threshold_support_priority; survivalPreemption=false; targetMutation=false; aimMutation=false; fireCommand=false");
        }
        return released;
    }

    private static bool TryStartBreakContact(IReadOnlyList<OperatorDecisionSnapshot> members, DateTimeOffset now)
    {
        if (members.Count < BreakContactMinimumPressuredMembers)
        {
            return false;
        }

        OperatorDecisionSnapshot[] pressured = members.Where(IsDangerExposed).ToArray();
        if (pressured.Length < BreakContactMinimumPressuredMembers || !pressured.Any(HasRecentPressureEvidence))
        {
            return false;
        }

        foreach (OperatorDecisionSnapshot mover in pressured
            .Where(IsRetreatAlignedWithSain)
            .OrderByDescending(HasRecentPressureEvidence)
            .ThenBy(item => item.Threat.Distance ?? float.MaxValue)
            .ThenByDescending(item => item.SquadCohesion.OperatorDistanceToOwner))
        {
            if (!IsTranslationCompatibleForKind(mover, mover.Threat.EnemyId, "BreakContact", out _)
                || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(mover.BotProfileId, out VanguardRaidOperatorRuntimeRecord moverRuntime)
                || moverRuntime.BotOwner == null
                || moverRuntime.BotOwner.IsDead)
            {
                continue;
            }

            ResolveLiveThreatPose(mover, now, out Vector3 threatPosition, out _, out string threatPoseSource);
            OperatorDecisionSnapshot? support = FindBoundSupport(members, mover, threatPosition, now, requireSupportCloserToThreat: false);
            if (support == null)
            {
                continue;
            }

            if (!VanguardTacticalPlacementSolver.TryResolveBoundRetreat(
                    mover,
                    moverRuntime.BotOwner.Position,
                    support.Position,
                    threatPosition,
                    BreakContactStepMeters,
                    out VanguardTacticalPlacementPlan placement))
            {
                pathRejectsTotal++;
                continue;
            }

            if (TryStartLease(
                    mover,
                    moverRuntime.BotOwner,
                    "BreakContact",
                    placement.Anchor,
                    "moverContribution=Disengage;supportContribution=BoundCover;supportBot=" + Safe(support.BotProfileId)
                        + ";pressureMembers=" + pressured.Length.ToString(CultureInfo.InvariantCulture)
                        + ";pressureEvidence=true;threatPose=" + Safe(threatPoseSource)
                        + ";placement=" + Safe(placement.Summary),
                    now,
                    support.BotProfileId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryStartBoundRetreat(IReadOnlyList<OperatorDecisionSnapshot> members, DateTimeOffset now)
    {
        foreach (OperatorDecisionSnapshot mover in members
            .Where(IsDangerExposed)
            .Where(IsRetreatAlignedWithSain)
            .OrderBy(item => item.Threat.Distance ?? float.MaxValue)
            .ThenByDescending(item => item.SquadCohesion.OperatorDistanceToOwner))
        {
            if (!IsTranslationCompatibleForKind(mover, mover.Threat.EnemyId, "BoundRetreat", out _)
                || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(mover.BotProfileId, out VanguardRaidOperatorRuntimeRecord moverRuntime)
                || moverRuntime.BotOwner == null
                || moverRuntime.BotOwner.IsDead)
            {
                continue;
            }

            ResolveLiveThreatPose(mover, now, out Vector3 threatPosition, out _, out string threatPoseSource);
            OperatorDecisionSnapshot? support = FindBoundSupport(members, mover, threatPosition, now, requireSupportCloserToThreat: false);
            if (support == null)
            {
                continue;
            }

            if (!VanguardTacticalPlacementSolver.TryResolveBoundRetreat(
                    mover,
                    moverRuntime.BotOwner.Position,
                    support.Position,
                    threatPosition,
                    BoundRetreatStepMeters,
                    out VanguardTacticalPlacementPlan placement))
            {
                pathRejectsTotal++;
                continue;
            }

            if (TryStartLease(
                    mover,
                    moverRuntime.BotOwner,
                    "BoundRetreat",
                    placement.Anchor,
                    "moverContribution=BoundMove;supportContribution=BoundCover;supportBot=" + Safe(support.BotProfileId)
                        + ";sainDecision=" + Safe(mover.Sain.CombatDecision)
                        + ";sainAction=" + Safe(mover.Sain.CurrentAction)
                        + ";threatPose=" + Safe(threatPoseSource)
                        + ";placement=" + Safe(placement.Summary),
                    now,
                    support.BotProfileId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryStartThresholdSupport(IReadOnlyList<OperatorDecisionSnapshot> members, DateTimeOffset now)
    {
        if (members == null || members.Count < 2)
        {
            return false;
        }

        string ownerProfileId = members[0].OwnerProfileId;
        string threatId = members[0].Threat.EnemyId;
        if (!DirectCommitMovementService.TryGetActiveBreachSupportContext(
                ownerProfileId,
                threatId,
                now,
                out string leadBotProfileId,
                out string sourceLeaseId,
                out long planEpoch,
                out Vector3 leadPosition,
                out BreachEntryOpportunity breachEntry,
                out _))
        {
            return false;
        }
        if (!RouteAssignmentService.TryGetSnapshot(out RouteAssignmentSnapshot assignmentSnapshot)
            || !assignmentSnapshot.IsAvailable)
        {
            return false;
        }

        ThreatGroupRouteAssignment? assignment = assignmentSnapshot.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, threatId, StringComparison.OrdinalIgnoreCase));
        if (assignment == null
            || !assignment.DirectBotProfileIds.Contains(leadBotProfileId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        Vector3 entryDirection = breachEntry.EntryAnchor - breachEntry.ProjectedPortal;
        entryDirection.y = 0f;
        if (entryDirection.sqrMagnitude <= 0.01f)
        {
            return false;
        }
        entryDirection.Normalize();

        foreach (OperatorDecisionSnapshot support in members
            .Where(item => !string.Equals(item.BotProfileId, leadBotProfileId, StringComparison.OrdinalIgnoreCase))
            .Where(item => assignment.DirectBotProfileIds.Contains(item.BotProfileId, StringComparer.OrdinalIgnoreCase))
            .OrderBy(item => HorizontalDistance(item.Position, breachEntry.ProjectedPortal))
            .ThenBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase))
        {
            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(support.BotProfileId, out VanguardRaidOperatorRuntimeRecord runtime)
                || runtime.BotOwner == null
                || runtime.BotOwner.IsDead)
            {
                continue;
            }
            if (!VanguardTacticalPlacementSolver.TryResolveThresholdSupport(
                    support,
                    runtime.BotOwner.Position,
                    leadPosition,
                    breachEntry.ProjectedPortal,
                    breachEntry.EntryAnchor,
                    out VanguardTacticalPlacementPlan placement))
            {
                pathRejectsTotal++;
                continue;
            }

            if (TryStartLease(
                    support,
                    runtime.BotOwner,
                    "ThresholdSupport",
                    placement.Anchor,
                    "leadBot=" + Safe(leadBotProfileId)
                        + ";sourceBreach=" + Safe(sourceLeaseId)
                        + ";planEpoch=" + planEpoch.ToString(CultureInfo.InvariantCulture)
                        + ";choke=" + Safe(breachEntry.ChokeId)
                        + ";door=" + Safe(breachEntry.DoorId)
                        + ";nativeDoorTraversal=" + Bool(breachEntry.NativeDoorTraversalRequired)
                        + ";placement=" + Safe(placement.Summary),
                    now,
                    supportBotProfileId: leadBotProfileId,
                    supportContextId: sourceLeaseId,
                    boundaryPortal: breachEntry.ProjectedPortal,
                    boundaryForward: entryDirection))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryStartAssaultRelay(IReadOnlyList<OperatorDecisionSnapshot> members, DateTimeOffset now)
    {
        if (members == null || members.Count < 2)
        {
            return false;
        }

        string ownerProfileId = members[0].OwnerProfileId;
        string threatId = members[0].Threat.EnemyId;
        if (!DirectCommitMovementService.TryGetActiveRelayContext(
                ownerProfileId,
                threatId,
                now,
                out string leadBotProfileId,
                out string sourceLeaseId,
                out long planEpoch,
                out Vector3 leadPosition,
                out _))
        {
            return false;
        }
        if (!RouteAssignmentService.TryGetSnapshot(out RouteAssignmentSnapshot assignmentSnapshot)
            || !assignmentSnapshot.IsAvailable)
        {
            return false;
        }

        ThreatGroupRouteAssignment? assignment = assignmentSnapshot.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, threatId, StringComparison.OrdinalIgnoreCase));
        if (assignment == null
            || !assignment.DirectBotProfileIds.Contains(leadBotProfileId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (OperatorDecisionSnapshot relay in members
            .Where(item => !string.Equals(item.BotProfileId, leadBotProfileId, StringComparison.OrdinalIgnoreCase))
            .Where(item => assignment.DirectBotProfileIds.Contains(item.BotProfileId, StringComparer.OrdinalIgnoreCase))
            .OrderBy(item => HorizontalDistance(item.Position, leadPosition))
            .ThenBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase))
        {
            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(relay.BotProfileId, out VanguardRaidOperatorRuntimeRecord runtime)
                || runtime.BotOwner == null
                || runtime.BotOwner.IsDead)
            {
                continue;
            }

            ResolveLiveThreatPose(relay, now, out Vector3 threatPosition, out _, out string threatPoseSource);
            float leadThreatDistance = HorizontalDistance(leadPosition, threatPosition);
            float relayThreatDistance = HorizontalDistance(runtime.BotOwner.Position, threatPosition);
            if (relayThreatDistance < leadThreatDistance + 1.5f)
            {
                continue;
            }
            if (!VanguardTacticalPlacementSolver.TryResolveAssaultRelay(
                    relay,
                    runtime.BotOwner.Position,
                    leadPosition,
                    threatPosition,
                    out VanguardTacticalPlacementPlan placement))
            {
                pathRejectsTotal++;
                continue;
            }

            if (TryStartLease(
                    relay,
                    runtime.BotOwner,
                    "AssaultRelay",
                    placement.Anchor,
                    "leadBot=" + Safe(leadBotProfileId)
                        + ";sourceCommit=" + Safe(sourceLeaseId)
                        + ";planEpoch=" + planEpoch.ToString(CultureInfo.InvariantCulture)
                        + ";threatPose=" + Safe(threatPoseSource)
                        + ";placement=" + Safe(placement.Summary),
                    now,
                    supportBotProfileId: leadBotProfileId,
                    supportContextId: sourceLeaseId))
            {
                return true;
            }
        }

        return false;
    }

    private static void PruneExpiredBoundAdvanceHandoffs(DateTimeOffset now)
    {
        int removed = 0;
        lock (Sync)
        {
            foreach (KeyValuePair<string, BoundAdvanceHandoff> pair in BoundAdvanceHandoffByOwnerThreat
                .Where(item => now >= item.Value.ExpiresAtUtc)
                .ToArray())
            {
                if (BoundAdvanceHandoffByOwnerThreat.Remove(pair.Key))
                {
                    removed++;
                }
            }
        }
        if (removed > 0)
        {
            boundAdvanceHandoffFallbacksTotal += removed;
        }
    }

    private static bool TryStartBoundAdvance(IReadOnlyList<OperatorDecisionSnapshot> members, DateTimeOffset now)
    {
        if (TryStartReciprocalBoundAdvanceHandoff(members, now))
        {
            return true;
        }

        foreach (OperatorDecisionSnapshot mover in members
            .OrderByDescending(item => item.Threat.Distance ?? 0f)
            .ThenBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase))
        {
            if (!mover.Threat.Distance.HasValue
                || !IsTranslationCompatibleForKind(mover, mover.Threat.EnemyId, "BoundAdvance", out _)
                || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(mover.BotProfileId, out VanguardRaidOperatorRuntimeRecord moverRuntime)
                || moverRuntime.BotOwner == null
                || moverRuntime.BotOwner.IsDead)
            {
                continue;
            }

            ResolveLiveThreatPose(mover, now, out Vector3 threatPosition, out _, out _);
            OperatorDecisionSnapshot? support = FindBoundSupport(members, mover, threatPosition, now, requireSupportCloserToThreat: true);
            if (support == null)
            {
                continue;
            }

            VanguardTacticalCoordinationTuningPolicy.OwnerTacticalCoordinationTuning tuning =
                VanguardTacticalCoordinationTuningPolicy.ForOwner(mover.OwnerProfileId);
            if (TryStartBoundAdvancePair(
                    mover,
                    support,
                    now,
                    "opportunistic_pair",
                    ResolveBoundAdvanceSupportLeadMeters(tuning.BoundAdvanceDistanceMeters, reciprocal: false),
                    out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryStartReciprocalBoundAdvanceHandoff(IReadOnlyList<OperatorDecisionSnapshot> members, DateTimeOffset now)
    {
        if (members == null || members.Count < 2)
        {
            return false;
        }

        string ownerThreatKey = OwnerThreatKey(members[0].OwnerProfileId, members[0].Threat.EnemyId);
        BoundAdvanceHandoff? handoff = null;
        bool expired = false;
        lock (Sync)
        {
            if (BoundAdvanceHandoffByOwnerThreat.TryGetValue(ownerThreatKey, out BoundAdvanceHandoff? current))
            {
                if (now >= current.ExpiresAtUtc)
                {
                    BoundAdvanceHandoffByOwnerThreat.Remove(ownerThreatKey);
                    expired = true;
                }
                else
                {
                    handoff = current;
                }
            }
        }

        if (expired)
        {
            boundAdvanceHandoffFallbacksTotal++;
            VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
                $"TC_BOUND_ADVANCE_HANDOFF_DROPPED ownerThreat={Safe(ownerThreatKey)}; reason=ttl_expired; failOpen=true; automaticSequence=false; targetMutation=false; aimMutation=false; fireCommand=false");
            return false;
        }
        if (handoff == null)
        {
            return false;
        }

        OperatorDecisionSnapshot? mover = members.FirstOrDefault(item =>
            string.Equals(item.BotProfileId, handoff.NextMoverBotProfileId, StringComparison.OrdinalIgnoreCase));
        OperatorDecisionSnapshot? support = members.FirstOrDefault(item =>
            string.Equals(item.BotProfileId, handoff.NextSupportBotProfileId, StringComparison.OrdinalIgnoreCase));

        VanguardTacticalCoordinationTuningPolicy.OwnerTacticalCoordinationTuning tuning =
            VanguardTacticalCoordinationTuningPolicy.ForOwner(handoff.OwnerProfileId);
        float reciprocalSupportLeadMeters = ResolveBoundAdvanceSupportLeadMeters(tuning.BoundAdvanceDistanceMeters, reciprocal: true);
        string failureReason = "reciprocal_pair_missing";
        bool started = mover != null
            && support != null
            && TryStartBoundAdvancePair(
                mover,
                support,
                now,
                "reciprocal_handoff",
                reciprocalSupportLeadMeters,
                out failureReason);

        lock (Sync)
        {
            if (BoundAdvanceHandoffByOwnerThreat.TryGetValue(ownerThreatKey, out BoundAdvanceHandoff? current)
                && string.Equals(current.SourceLeaseId, handoff.SourceLeaseId, StringComparison.OrdinalIgnoreCase))
            {
                BoundAdvanceHandoffByOwnerThreat.Remove(ownerThreatKey);
            }
        }

        if (started)
        {
            boundAdvanceHandoffStartsTotal++;
            VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
                $"TC_BOUND_ADVANCE_HANDOFF_STARTED owner={Safe(handoff.OwnerProfileId)}; threat={Safe(handoff.ThreatId)}; mover={Safe(handoff.NextMoverBotProfileId)}; support={Safe(handoff.NextSupportBotProfileId)}; sourceLease={Safe(handoff.SourceLeaseId)}; ageSeconds={(now - handoff.PreparedAtUtc).TotalSeconds:0.00}; reciprocal=true; sameAuthoritySafetyGates=true; reciprocalSupportLeadMeters={reciprocalSupportLeadMeters:0.0}; boundAdvanceDistanceMeters={tuning.BoundAdvanceDistanceMeters:0.0}; automaticSequence=false; fireCommand=false; aimMutation=false; targetMutation=false");
            return true;
        }

        boundAdvanceHandoffFallbacksTotal++;
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"TC_BOUND_ADVANCE_HANDOFF_DROPPED owner={Safe(handoff.OwnerProfileId)}; threat={Safe(handoff.ThreatId)}; mover={Safe(handoff.NextMoverBotProfileId)}; support={Safe(handoff.NextSupportBotProfileId)}; sourceLease={Safe(handoff.SourceLeaseId)}; reason={Safe(failureReason)}; failOpen=true; ordinaryBoundAdvanceStillEligible=true; automaticSequence=false; fireCommand=false; aimMutation=false; targetMutation=false");
        return false;
    }

    private static float ResolveBoundAdvanceSupportLeadMeters(float boundAdvanceDistanceMeters, bool reciprocal)
    {
        float normalizedDistance = Math.Max(5.0f, Math.Min(20.0f, boundAdvanceDistanceMeters));
        return reciprocal
            ? Math.Max(BoundAdvanceBaselineReciprocalMinimumSupportLeadMeters, normalizedDistance - 5.5f)
            : Math.Max(BoundAdvanceBaselineMinimumSupportLeadMeters, normalizedDistance - 3.0f);
    }

    private static bool TryStartBoundAdvancePair(
        OperatorDecisionSnapshot mover,
        OperatorDecisionSnapshot support,
        DateTimeOffset now,
        string pairingMode,
        float minimumSupportLeadMeters,
        out string reason)
    {
        reason = "bound_advance_pair_not_started";
        if (mover == null || support == null)
        {
            reason = "pair_missing";
            return false;
        }
        if (!mover.Threat.Distance.HasValue)
        {
            reason = "mover_threat_distance_missing";
            return false;
        }
        if (!IsTranslationCompatibleForKind(mover, mover.Threat.EnemyId, "BoundAdvance", out string authorityReason))
        {
            reason = "mover_translation_incompatible:" + Safe(authorityReason);
            return false;
        }
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(mover.BotProfileId, out VanguardRaidOperatorRuntimeRecord moverRuntime)
            || moverRuntime.BotOwner == null
            || moverRuntime.BotOwner.IsDead)
        {
            reason = "mover_runtime_missing_or_dead";
            return false;
        }

        VanguardTacticalCoordinationTuningPolicy.OwnerTacticalCoordinationTuning tuning =
            VanguardTacticalCoordinationTuningPolicy.ForOwner(mover.OwnerProfileId);
        float minimumStandoff = Math.Max(tuning.ProactiveWeaponMeters, tuning.DangerCloseWeaponMeters + 5f);
        if (mover.Threat.Distance.Value <= minimumStandoff + 3f)
        {
            reason = "mover_inside_safe_advance_depth";
            return false;
        }

        ResolveLiveThreatPose(mover, now, out Vector3 threatPosition, out _, out string threatPoseSource);
        if (!IsBoundAdvanceSupportCandidate(mover, support, threatPosition, now, minimumSupportLeadMeters, out string supportReason))
        {
            reason = "support_not_admissible:" + Safe(supportReason);
            return false;
        }

        if (!VanguardTacticalPlacementSolver.TryResolveBoundAdvance(
                mover,
                moverRuntime.BotOwner.Position,
                support.Position,
                threatPosition,
                minimumStandoff,
                tuning.BoundAdvanceDistanceMeters,
                out VanguardTacticalPlacementPlan placement))
        {
            pathRejectsTotal++;
            reason = "placement_unavailable";
            return false;
        }

        bool started = TryStartLease(
            mover,
            moverRuntime.BotOwner,
            "BoundAdvance",
            placement.Anchor,
            "moverContribution=BoundMove;supportContribution=BoundCover;supportBot=" + Safe(support.BotProfileId)
                + ";pairingMode=" + Safe(pairingMode)
                + ";supportLeadMeters=" + minimumSupportLeadMeters.ToString("0.0", CultureInfo.InvariantCulture)
                + ";supportLineEvidence=true;minimumStandoff=" + minimumStandoff.ToString("0.0", CultureInfo.InvariantCulture)
                + ";threatPose=" + Safe(threatPoseSource)
                + ";placement=" + Safe(placement.Summary),
            now,
            out string startReason,
            support.BotProfileId);
        reason = started ? "bound_advance_started" : startReason;
        return started;
    }

    private static bool IsBoundAdvanceSupportCandidate(
        OperatorDecisionSnapshot mover,
        OperatorDecisionSnapshot support,
        Vector3 threatPosition,
        DateTimeOffset now,
        float minimumSupportLeadMeters,
        out string reason)
    {
        reason = "unknown";
        if (mover == null || support == null
            || string.Equals(support.BotProfileId, mover.BotProfileId, StringComparison.OrdinalIgnoreCase)
            || !support.Alive)
        {
            reason = "support_missing_or_same_bot";
            return false;
        }
        if (!string.Equals(support.OwnerProfileId, mover.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(NormalizeThreat(support.Threat.EnemyId), NormalizeThreat(mover.Threat.EnemyId), StringComparison.OrdinalIgnoreCase))
        {
            reason = "support_owner_or_threat_changed";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(support, now, out string freshnessReason))
        {
            reason = "support_" + freshnessReason;
            return false;
        }
        if (support.Sain.RunningToCover == true)
        {
            reason = "support_running_to_cover";
            return false;
        }
        if (IsIntentionalTacticalSeparation(support, now))
        {
            reason = "support_other_tactical_intent";
            return false;
        }
        if (support.GrenadeHazard.HasRelevantHazard
            || support.Medical.Actionability.GrenadeThrowing
            || support.Medical.Actionability.AnyMedicineUsing
            || support.Medical.Actionability.Reloading)
        {
            reason = "support_local_survival_action";
            return false;
        }
        if (!MultiThreatOffensiveSafetyPolicy.IsSupportMateriallyReady(support, now, out string supportMaterialReason))
        {
            reason = "support_material_not_ready:" + Safe(supportMaterialReason);
            return false;
        }
        if (HorizontalDistance(support.Position, mover.Position) > BoundSupportMaxSeparationMeters)
        {
            reason = "support_separation_exceeded";
            return false;
        }
        if (VanguardReturnMovementCommandStore.TryGetActive(support.BotProfileId, now, out _)
            || AntiCongaService.HasActiveAdvanceGateForBot(support.BotProfileId)
            || ChokeCommitSupportService.HasActiveContributionForBot(support.BotProfileId))
        {
            reason = "support_existing_physical_contribution";
            return false;
        }

        float moverThreatDistance = HorizontalDistance(mover.Position, threatPosition);
        if (moverThreatDistance - HorizontalDistance(support.Position, threatPosition) < minimumSupportLeadMeters)
        {
            reason = "support_not_ahead_by_" + minimumSupportLeadMeters.ToString("0.0", CultureInfo.InvariantCulture) + "m";
            return false;
        }
        if (!CanRepresentBoundAdvanceCover(support, threatPosition))
        {
            reason = "support_line_evidence_missing";
            return false;
        }

        reason = "support_current";
        return true;
    }

    private static OperatorDecisionSnapshot? FindBoundSupport(
        IReadOnlyList<OperatorDecisionSnapshot> members,
        OperatorDecisionSnapshot mover,
        Vector3 threatPosition,
        DateTimeOffset now,
        bool requireSupportCloserToThreat)
    {
        float moverThreatDistance = HorizontalDistance(mover.Position, threatPosition);
        IEnumerable<OperatorDecisionSnapshot> candidates = members
            .Where(item => !string.Equals(item.BotProfileId, mover.BotProfileId, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.Alive
                && string.Equals(item.OwnerProfileId, mover.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeThreat(item.Threat.EnemyId), NormalizeThreat(mover.Threat.EnemyId), StringComparison.OrdinalIgnoreCase))
            .Where(item => VanguardOperatorDecisionSnapshotService.IsFresh(item, now, out _))
            .Where(item => item.Sain.RunningToCover != true)
            .Where(item => !IsIntentionalTacticalSeparation(item, now))
            .Where(item => !item.GrenadeHazard.HasRelevantHazard
                && !item.Medical.Actionability.GrenadeThrowing
                && !item.Medical.Actionability.AnyMedicineUsing
                && !item.Medical.Actionability.Reloading)
            .Where(item => MultiThreatOffensiveSafetyPolicy.IsSupportMateriallyReady(item, now, out _))
            .Where(item => HorizontalDistance(item.Position, mover.Position) <= BoundSupportMaxSeparationMeters)
            .Where(item => !VanguardReturnMovementCommandStore.TryGetActive(item.BotProfileId, now, out _))
            .Where(item => !AntiCongaService.HasActiveAdvanceGateForBot(item.BotProfileId)
                && !ChokeCommitSupportService.HasActiveContributionForBot(item.BotProfileId));

        if (requireSupportCloserToThreat)
        {
            VanguardTacticalCoordinationTuningPolicy.OwnerTacticalCoordinationTuning tuning =
                VanguardTacticalCoordinationTuningPolicy.ForOwner(mover.OwnerProfileId);
            float minimumSupportLeadMeters = ResolveBoundAdvanceSupportLeadMeters(tuning.BoundAdvanceDistanceMeters, reciprocal: false);
            return candidates
                .Where(item => IsBoundAdvanceSupportCandidate(mover, item, threatPosition, now, minimumSupportLeadMeters, out _))
                .OrderBy(item => HorizontalDistance(item.Position, threatPosition))
                .ThenBy(item => HorizontalDistance(item.Position, mover.Position))
                .FirstOrDefault();
        }

        return candidates
            .Where(item => HorizontalDistance(item.Position, threatPosition) - moverThreatDistance >= BoundRetreatMinimumSupportDepthMeters)
            .OrderByDescending(item => HorizontalDistance(item.Position, threatPosition))
            .ThenBy(item => HorizontalDistance(item.Position, mover.Position))
            .FirstOrDefault();
    }

    private static bool CanRepresentBoundAdvanceCover(OperatorDecisionSnapshot support, Vector3 threatPosition)
    {
        if (support == null || !support.Alive)
        {
            return false;
        }
        if (support.Threat.EnemyVisible == true
            && support.Threat.Distance.HasValue
            && support.Threat.Distance.Value <= AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters)
        {
            return false;
        }
        if (support.Threat.EnemyVisible == true || support.Threat.EnemyCanShoot == true)
        {
            return true;
        }
        return string.Equals(
            TacticalSearchPolicy.NormalizeToken(support.Sain.CurrentAction),
            "StandAndShootAction",
            StringComparison.OrdinalIgnoreCase)
            && HorizontalDistance(support.Position, threatPosition) > AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters;
    }

    private static bool IsRetreatAlignedWithSain(OperatorDecisionSnapshot snapshot)
    {
        string action = TacticalSearchPolicy.NormalizeToken(snapshot.Sain.CurrentAction);
        string decision = TacticalSearchPolicy.NormalizeToken(snapshot.Sain.CombatDecision);
        if (string.Equals(action, "SeekCoverAction", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, "Retreat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, "SeekCover", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A stationary SAIN shooter under recorded incoming fire can request a bounded,
        // supported repositioning without first waiting for SAIN to choose Retreat.
        // The existing snapshot/live gates still veto grenades, medicine, reload,
        // running-to-cover, close visible dogfights and competing SAIN locomotion.
        bool stationaryOrSearching = string.Equals(action, "StandAndShootAction", StringComparison.OrdinalIgnoreCase)
            || TacticalSearchPolicy.IsSearchAction(action);
        return stationaryOrSearching
            && HasRecentPressureEvidence(snapshot)
            && snapshot.Threat.Distance.HasValue
            && snapshot.Threat.Distance.Value > AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters;
    }

    private static bool HasRecentPressureEvidence(OperatorDecisionSnapshot snapshot)
        => snapshot.Threat.ShotAtMeRecently == true
            || snapshot.Threat.ShotMeRecently == true
            || snapshot.Medical.Safety.IncomingFireRecent
            || snapshot.ThreatScan.CandidateIncomingFireFresh;

    private static bool TryStartOverwatch(IReadOnlyList<OperatorDecisionSnapshot> members, DateTimeOffset now)
    {
        var candidates = new List<OverwatchCandidate>();
        foreach (OperatorDecisionSnapshot snapshot in members)
        {
            VanguardTacticalCoordinationTuningPolicy.OwnerTacticalCoordinationTuning tuning =
                VanguardTacticalCoordinationTuningPolicy.ForOwner(snapshot.OwnerProfileId);
            if (!snapshot.Threat.Distance.HasValue
                || snapshot.Threat.Distance.Value < tuning.OverwatchMinimumThreatMeters
                || !snapshot.Threat.ThreatAnchorPosition.HasValue
                || !TryBasicAdmission(snapshot, "OverwatchSupport", now, out _)
                || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out VanguardRaidOperatorRuntimeRecord runtime)
                || runtime.BotOwner == null
                || runtime.BotOwner.IsDead
                || !TryGetOverwatchCapability(runtime.BotOwner, now, out int equipmentRank, out string equipmentSummary))
            {
                continue;
            }

            int roleRank = OverwatchRoleRank(runtime.TacticalRole);
            // Overwatch is a role-aware contribution, not a generic "best rifle" selector. Equipment
            // must make the contribution actionable and the Operator must already belong to a role
            // whose doctrine can reasonably consume a distant support/cover opportunity.
            if (roleRank <= 0)
            {
                continue;
            }

            candidates.Add(new OverwatchCandidate(snapshot, runtime, equipmentRank, roleRank, equipmentSummary));
        }

        // Examine at most two already-admissible candidates in ranked order. The previously
        // highest-ranked Operator may lack a currently navigable standoff; do not erase an
        // otherwise safe Overwatch opportunity for a second member. Placement probes are
        // observational; only one fully-qualified candidate reaches TryStartLease.
        // Never iterate after a lease attempt: scheduler/command acquisition may have side effects.
        int candidateAttempt = 0;
        foreach (OverwatchCandidate selected in candidates
            .OrderByDescending(item => item.RoleRank)
            .ThenByDescending(item => item.EquipmentRank)
            .ThenByDescending(item => item.Snapshot.Threat.Distance ?? 0f)
            .ThenBy(item => item.Snapshot.BotProfileId, StringComparer.OrdinalIgnoreCase)
            .Take(MaxOverwatchCandidateProbes))
        {
            candidateAttempt++;
            BotOwner botOwner = selected.Runtime.BotOwner!;

            // DynamicWeaponPreference retains weapon-selection authority. A hands-unsettled
            // candidate should not consume a potentially expensive NavMesh placement probe.
            if (botOwner.WeaponManager?.Selector?.IsWeaponReady != true)
            {
                acquisitionDeferralsTotal++;
                VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                    $"TC_TACTICAL_OPPORTUNITY_DEFERRED kind=OverwatchSupport; bot={Safe(selected.Snapshot.BotProfileId)}; attempt={candidateAttempt}; reason=native_selector_not_ready; placementProbeSkipped=true; weaponMutation=false");
                continue;
            }

            // Each solver can test several NavMesh paths. Do not convert a squad-size increase
            // into unbounded per-frame probes; lack of budget only skips this optional TC sidecar.
            if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner(
                    "TCOverwatchCandidatePlacement",
                    selected.Snapshot.OwnerProfileId,
                    1,
                    MaxOverwatchCandidateProbes,
                    out string probeBudgetReason))
            {
                acquisitionDeferralsTotal++;
                VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                    $"TC_TACTICAL_OPPORTUNITY_DEFERRED kind=OverwatchSupport; bot={Safe(selected.Snapshot.BotProfileId)}; attempt={candidateAttempt}; reason=placement_probe_budget; detail={Safe(probeBudgetReason)}; movementMutation=false");
                return false;
            }

            VanguardTacticalCoordinationTuningPolicy.OwnerTacticalCoordinationTuning selectedTuning =
                VanguardTacticalCoordinationTuningPolicy.ForOwner(selected.Snapshot.OwnerProfileId);
            ResolveLiveThreatPose(selected.Snapshot, now, out Vector3 overwatchThreatPosition, out Vector3 overwatchThreatLook, out string threatPoseSource);
            Vector3? secondaryThreatPosition = ResolveSecondaryOverwatchThreatPosition(selected.Snapshot);
            if (!VanguardTacticalPlacementSolver.TryResolveCombatOverwatch(
                    selected.Snapshot,
                    botOwner.Position,
                    overwatchThreatPosition,
                    overwatchThreatLook,
                    secondaryThreatPosition,
                    selectedTuning.OverwatchStandoffMeters,
                    out VanguardTacticalPlacementPlan placement))
            {
                pathRejectsTotal++;
                VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                    $"TC_TACTICAL_OPPORTUNITY_DEFERRED kind=OverwatchSupport; bot={Safe(selected.Snapshot.BotProfileId)}; attempt={candidateAttempt}; reason=placement_not_viable; movementMutation=false");
                continue;
            }

            // Exactly one authorization attempt. If scheduler, preflight, command publication or
            // latest-snapshot validation refuses, fail open rather than acquiring another lease.
            return TryStartLease(
                selected.Snapshot,
                botOwner,
                "OverwatchSupport",
                placement.Anchor,
                "role=" + Safe(selected.Runtime.TacticalRole)
                    + ";equipment=" + Safe(selected.EquipmentSummary)
                    + ";threatPose=" + Safe(threatPoseSource)
                    + ";secondaryThreat=" + (secondaryThreatPosition.HasValue ? "position_known" : "none")
                    + ";candidateAttempt=" + candidateAttempt.ToString(CultureInfo.InvariantCulture)
                    + ";placement=" + Safe(placement.Summary),
                now);
        }

        return false;
    }

    private static Vector3? ResolveSecondaryOverwatchThreatPosition(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot == null
            || !snapshot.ThreatScan.CandidatePosition.HasValue
            || string.IsNullOrWhiteSpace(snapshot.ThreatScan.CandidateThreatId)
            || string.Equals(NormalizeThreat(snapshot.ThreatScan.CandidateThreatId), NormalizeThreat(snapshot.Threat.EnemyId), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        bool directSecondary = snapshot.ThreatScan.CandidateLineOfSight
            || snapshot.ThreatScan.CandidateIncomingFireFresh
            || (snapshot.ThreatScan.CandidateVisible && snapshot.ThreatScan.CandidateCanShoot);
        return directSecondary ? snapshot.ThreatScan.CandidatePosition : null;
    }

    private static bool TryStartBuddySupport(IReadOnlyList<OperatorDecisionSnapshot> members, DateTimeOffset now)
    {
        if (members.Count == 0)
        {
            return false;
        }

        VanguardTacticalCoordinationTuningPolicy.OwnerTacticalCoordinationTuning tuning =
            VanguardTacticalCoordinationTuningPolicy.ForOwner(members[0].OwnerProfileId);
        OperatorDecisionSnapshot[] distressed = members
            .Where(IsDangerExposed)
            .Where(item => !IsIntentionalTacticalSeparation(item, now))
            .Where(item => item.SquadCohesion.OwnerPosition.HasValue)
            .Where(item => item.SquadCohesion.OperatorDistanceToOwner >= tuning.BuddyIsolationMeters)
            .OrderByDescending(item => item.Threat.EnemyCanShoot == true)
            .ThenByDescending(item => item.Threat.EnemyVisible == true)
            .ThenByDescending(item => item.SquadCohesion.OperatorDistanceToOwner)
            .ToArray();

        foreach (OperatorDecisionSnapshot isolated in distressed)
        {
            if (!isolated.Threat.ThreatAnchorPosition.HasValue)
            {
                continue;
            }

            var helpers = members
                .Where(item => !string.Equals(item.BotProfileId, isolated.BotProfileId, StringComparison.OrdinalIgnoreCase))
                .Where(item => Vector3.Distance(Flatten(item.Position), Flatten(isolated.Position)) <= tuning.BuddyHelperMaxMeters)
                .Select(item => new { Snapshot = item, Rank = BuddySupportRoleRank(item.BotProfileId) })
                .OrderByDescending(item => item.Rank)
                .ThenBy(item => Vector3.Distance(Flatten(item.Snapshot.Position), Flatten(isolated.Position)))
                .ToArray();

            foreach (var helper in helpers)
            {
                if (!TryBasicAdmission(helper.Snapshot, "BuddySupport", now, out _)
                    || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(helper.Snapshot.BotProfileId, out VanguardRaidOperatorRuntimeRecord helperRuntime)
                    || helperRuntime.BotOwner == null
                    || helperRuntime.BotOwner.IsDead)
                {
                    continue;
                }

                ResolveLiveThreatPose(isolated, now, out Vector3 buddyThreatPosition, out _, out string buddyThreatPoseSource);
                if (!VanguardTacticalPlacementSolver.TryResolveBuddySupport(
                        helper.Snapshot,
                        helperRuntime.BotOwner.Position,
                        isolated.Position,
                        buddyThreatPosition,
                        out VanguardTacticalPlacementPlan placement))
                {
                    continue;
                }

                if (TryStartLease(
                        helper.Snapshot,
                        helperRuntime.BotOwner,
                        "BuddySupport",
                        placement.Anchor,
                        "distressedBot=" + Safe(isolated.BotProfileId)
                            + ";distressedOwnerDistance=" + isolated.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", CultureInfo.InvariantCulture)
                            + ";helperRole=" + Safe(helperRuntime.TacticalRole)
                            + ";threatPose=" + Safe(buddyThreatPoseSource)
                            + ";placement=" + Safe(placement.Summary),
                        now))
                {
                    return true;
                }
            }
        }
        return false;
    }


    private static bool TryStartInteriorDevelop(IReadOnlyList<OperatorDecisionSnapshot> members, DateTimeOffset now)
    {
        bool genericFallback = false;
        bool resolved = InteriorDevelopOpportunityService.TryResolve(
            members,
            now,
            out InteriorDevelopOpportunity opportunity,
            out string authoredResolveReason);
        string resolveReason = "authored:" + Safe(authoredResolveReason);
        if (!resolved)
        {
            resolved = InteriorDevelopGenericFallbackService.TryResolve(
                members,
                now,
                out opportunity,
                out string genericResolveReason);
            genericFallback = resolved;
            resolveReason += ";generic:" + Safe(genericResolveReason);
        }
        if (!resolved)
        {
            return false;
        }

        OperatorDecisionSnapshot mover = opportunity.Mover;
        if (!TryBasicAdmission(mover, InteriorDevelopOpportunityService.MovementKind, now, out string admissionReason)
            || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(
                mover.BotProfileId,
                out VanguardRaidOperatorRuntimeRecord runtime)
            || runtime.BotOwner == null
            || runtime.BotOwner.IsDead)
        {
            return false;
        }

        bool started = TryStartLease(
            mover,
            runtime.BotOwner,
            InteriorDevelopOpportunityService.MovementKind,
            opportunity.Destination,
            "geometry=" + Safe(opportunity.GeometryKind)
                + ";passage=" + Safe(opportunity.PassagePathId)
                + ";portal=" + Safe(opportunity.PortalId)
                + ";viableEntries=" + opportunity.ViableEntryCount.ToString(CultureInfo.InvariantCulture)
                + ";verticalDelta=" + opportunity.VerticalDeltaMeters.ToString("0.0", CultureInfo.InvariantCulture)
                + ";threatBefore=" + opportunity.ThreatDistanceBeforeMeters.ToString("0.0", CultureInfo.InvariantCulture)
                + ";threatAfter=" + opportunity.ThreatDistanceAfterMeters.ToString("0.0", CultureInfo.InvariantCulture)
                + ";score=" + opportunity.Score.ToString("0.0", CultureInfo.InvariantCulture)
                + ";genericFallback=" + Bool(genericFallback)
                + ";resolve=" + Safe(resolveReason)
                + ";admission=" + Safe(admissionReason)
                + ";planner=" + Safe(opportunity.Summary),
            now,
            out string startReason,
            supportBotProfileId: opportunity.SupportBotProfileId,
            supportContextId: opportunity.ContextId);
        if (started && genericFallback)
        {
            interiorDevelopGenericStartsTotal++;
        }
        if (!started)
        {
            VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                $"TC_INTERIOR_DEVELOP_DEFERRED owner={Safe(mover.OwnerProfileId)}; threat={Safe(mover.Threat.EnemyId)}; bot={Safe(mover.BotProfileId)}; support={Safe(opportunity.SupportBotProfileId)}; geometry={Safe(opportunity.GeometryKind)}; passage={Safe(opportunity.PassagePathId)}; portal={Safe(opportunity.PortalId)}; genericFallback={Bool(genericFallback)}; reason={Safe(startReason)}");
        }
        return started;
    }

    private static bool TryStartRegroup(OperatorDecisionSnapshot snapshot, string kind, DateTimeOffset now)
    {
        if (!TryBasicAdmission(snapshot, kind, now, out _)
            || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out VanguardRaidOperatorRuntimeRecord runtime)
            || runtime.BotOwner == null
            || runtime.BotOwner.IsDead
            || !VanguardTacticalPlacementSolver.TryResolve(snapshot, runtime.BotOwner.Position, now, out VanguardTacticalPlacementPlan placement))
        {
            return false;
        }

        return TryStartLease(
            snapshot,
            runtime.BotOwner,
            kind,
            placement.Anchor,
            "ownerDistance=" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.0", CultureInfo.InvariantCulture)
                + ";placement=" + Safe(placement.Summary),
            now);
    }

    internal static int ReleaseFlankSupportFailOpen(DateTimeOffset now, string reason)
    {
        ActiveOpportunityLease[] leases;
        lock (Sync)
        {
            leases = ActiveByBot.Values
                .Where(item => string.Equals(item.Kind, "FlankSupport", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        int released = 0;
        foreach (ActiveOpportunityLease lease in leases)
        {
            if (FinishLease(lease, now, "Interrupted", "flank_support_fail_open:" + Safe(reason), applyCooldown: false))
            {
                released++;
            }
        }
        return released;
    }

    internal static bool TryStartFlankSupport(
        ThreatGroupRouteAssignment assignment,
        AltRouteFeasibilityObservation supportCandidate,
        DateTimeOffset now,
        out string reason)
    {
        reason = "not_started";
        if (assignment == null || supportCandidate == null
            || !assignment.HasShadowAltSelection
            || !assignment.HasShadowAltSupportSelection
            || !string.Equals(assignment.SelectedAltSupportBotProfileId, supportCandidate.BotProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(assignment.AlternateCandidateId, supportCandidate.AxisCandidateId, StringComparison.OrdinalIgnoreCase)
            || !supportCandidate.IsFeasiblePair
            || !supportCandidate.CandidateSpecificRouteProven)
        {
            reason = "assignment_or_support_path_not_current";
            return false;
        }
        // Never start a second TC opportunity while any same-owner/threat physical lease is
        // active. The old shared cooldown is checked separately below: a confirmed active
        // AltDevelop lead may recruit its exact support during the previous unrelated
        // opportunity's cooldown, without clearing cooldowns or preempting any movement.
        string ownerThreatKey = OwnerThreatKey(assignment.OwnerProfileId, assignment.ThreatId);
        if (IsOwnerThreatActivelyReserved(ownerThreatKey))
        {
            reason = "owner_threat_opportunity_active";
            return false;
        }
        // Preserve the pre-maneuver owner/threat reservation contract while
        // separating its cooldown result from the hard active-lease veto above.
        // Sampling may expire a stale cooldown but cannot grant a movement.
        bool ownerThreatReserved = IsOwnerThreatReserved(OwnerThreatKey(assignment.OwnerProfileId, assignment.ThreatId), now);
        if (!AltDevelopMovementService.TryGetCurrentDevelopmentManeuverProof(
                assignment,
                now,
                out string leadManeuverEpochId,
                out bool leadPhysicalArrivalObserved,
                out AltDevelopEvidence leadEvidence))
        {
            reason = "exact_flank_lead_alt_develop_maneuver_missing";
            return false;
        }

        bool activeLeadLeaseProof = AltDevelopMovementService.TryGetActiveDevelopmentLeaseProof(
            assignment,
            now,
            out string leadLeaseId,
            out long leadLeaseGeneration,
            out DateTimeOffset leadLeaseMaxUntilUtc,
            out AltDevelopEvidence activeLeadEvidence);
        if (activeLeadLeaseProof)
        {
            leadEvidence = activeLeadEvidence;
        }
        // A group cooldown created by an unrelated completed TC opportunity must not
        // block an already-physically-developing FlankLead from recruiting its one
        // current same-axis support. Without that live lead lease the ordinary group
        // cooldown is still authoritative. Individual mover cooldown is ALWAYS
        // enforced later by TryBasicAdmission; all geometry, threat, support,
        // scheduler, SAIN path and physical-owner gates remain unchanged.
        if (!activeLeadLeaseProof && ownerThreatReserved)
        {
            reason = "owner_threat_cooldown_without_active_lead";
            return false;
        }
        string leadProofMode = activeLeadLeaseProof ? "active_physical_lease" : "current_maneuver_continuity";
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(assignment.SelectedAltSupportBotProfileId, out OperatorDecisionSnapshot support)
            || support == null
            || !support.Alive)
        {
            reason = "support_snapshot_missing_or_dead";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(support, now, out string freshnessReason))
        {
            reason = "support_snapshot_not_current:" + freshnessReason;
            return false;
        }
        if (!string.Equals(support.OwnerProfileId, assignment.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(NormalizeThreat(support.Threat.EnemyId), NormalizeThreat(assignment.ThreatId), StringComparison.OrdinalIgnoreCase))
        {
            reason = "support_owner_or_threat_changed";
            return false;
        }
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(support.BotProfileId, out VanguardRaidOperatorRuntimeRecord runtime)
            || runtime.BotOwner == null
            || runtime.BotOwner.IsDead)
        {
            reason = "support_runtime_missing_or_dead";
            return false;
        }
        if (!VanguardTacticalPlacementSolver.TryResolveFlankSupport(
                support,
                runtime.BotOwner.Position,
                supportCandidate.PathCorners,
                assignment.AlternateEstablishmentRegionCenter,
                assignment.AlternateEstablishmentRegionRadiusMeters,
                out VanguardTacticalPlacementPlan placement))
        {
            reason = "flank_support_placement_unavailable";
            return false;
        }

        bool started = TryStartLease(
            support,
            runtime.BotOwner,
            "FlankSupport",
            placement.Anchor,
            "axis=" + Safe(assignment.AlternateCandidateId)
                + ";leadBot=" + Safe(assignment.SelectedAltBotProfileId)
                + ";leadManeuver=" + Safe(leadManeuverEpochId)
                + ";leadProofMode=" + Safe(leadProofMode)
                + ";leadLease=" + Safe(activeLeadLeaseProof ? leadLeaseId : "none")
                + ";leadLeaseGeneration=" + (activeLeadLeaseProof ? leadLeaseGeneration : 0L).ToString(CultureInfo.InvariantCulture)
                + ";leadLeaseRemainingSeconds=" + (activeLeadLeaseProof ? Math.Max(0d, (leadLeaseMaxUntilUtc - now).TotalSeconds) : 0d).ToString("0.00", CultureInfo.InvariantCulture)
                + ";leadPhysicalArrivalObserved=" + Bool(leadPhysicalArrivalObserved)
                + ";leadProgress=" + Safe(leadEvidence.PhysicalProgressKind)
                + ";placement=" + Safe(placement.Summary),
            now,
            out string startReason,
            supportBotProfileId: assignment.SelectedAltBotProfileId,
            supportContextId: leadManeuverEpochId);
        if (started)
        {
            if (activeLeadLeaseProof) flankSupportActiveLeaseProofStartsTotal++;
            else flankSupportManeuverContinuityStartsTotal++;
        }
        reason = started ? "flank_support_started" : "flank_support_start_deferred:" + startReason;
        return started;
    }

    private static bool TryStartLease(
        OperatorDecisionSnapshot snapshot,
        BotOwner botOwner,
        string kind,
        Vector3 destination,
        string opportunitySummary,
        DateTimeOffset now,
        string supportBotProfileId = "",
        string supportContextId = "",
        Vector3? boundaryPortal = null,
        Vector3? boundaryForward = null)
    {
        return TryStartLease(
            snapshot,
            botOwner,
            kind,
            destination,
            opportunitySummary,
            now,
            out _,
            supportBotProfileId,
            supportContextId,
            boundaryPortal,
            boundaryForward);
    }

    private static bool TryStartLease(
        OperatorDecisionSnapshot snapshot,
        BotOwner botOwner,
        string kind,
        Vector3 destination,
        string opportunitySummary,
        DateTimeOffset now,
        out string startReason,
        string supportBotProfileId = "",
        string supportContextId = "",
        Vector3? boundaryPortal = null,
        Vector3? boundaryForward = null)
    {
        startReason = "not_started";
        string continuityKey = BuildOpportunityContinuityKey(
            kind,
            snapshot,
            destination,
            supportBotProfileId,
            supportContextId,
            boundaryPortal,
            boundaryForward);
        if (!TacticalIntentContinuityService.CanStartIntent(
                snapshot,
                kind,
                continuityKey,
                now,
                out bool qualifiedResume,
                out string intentReason))
        {
            acquisitionDeferralsTotal++;
            startReason = "intent_conflict:" + Safe(intentReason);
            VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                $"TC_TACTICAL_OPPORTUNITY_DEFERRED kind={Safe(kind)}; bot={Safe(snapshot.BotProfileId)}; threat={Safe(snapshot.Threat.EnemyId)}; reason={Safe(intentReason)}; tacticalIntentConflict=true; movementMutation=false");
            return false;
        }

        if (!TryBasicAdmission(snapshot, kind, now, out string admissionReason))
        {
            acquisitionDeferralsTotal++;
            startReason = "basic_admission:" + Safe(admissionReason);
            return false;
        }

        float directDelta = HorizontalDistance(botOwner.Position, destination);
        if (directDelta < MinimumMovementMeters)
        {
            acquisitionDeferralsTotal++;
            startReason = "movement_below_minimum";
            return false;
        }

        if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner(
                "TCTacticalOpportunityPath",
                snapshot.OwnerProfileId,
                1,
                2,
                out string budgetReason))
        {
            acquisitionDeferralsTotal++;
            startReason = "frame_budget:" + Safe(budgetReason);
            return false;
        }

        if (!VanguardTacticalPlacementSolver.TryCalculatePreparedRuntimePath(
                botOwner.Position,
                destination,
                out NavMeshPath preparedPath,
                out float pathLength,
                out string pathSummary)
            || pathLength < MinimumMovementMeters)
        {
            pathRejectsTotal++;
            startReason = "path_unavailable:" + Safe(pathSummary);
            return false;
        }
        destination = preparedPath.corners[preparedPath.corners.Length - 1];
        if (string.Equals(kind, "ThresholdSupport", StringComparison.OrdinalIgnoreCase))
        {
            string boundaryReason = "boundary_context_missing";
            if (!boundaryPortal.HasValue
                || !boundaryForward.HasValue
                || !VanguardTacticalPlacementSolver.IsPreparedPathBeforePortal(
                    preparedPath,
                    boundaryPortal.Value,
                    boundaryForward.Value,
                    0.75f,
                    out boundaryReason))
            {
                pathRejectsTotal++;
                startReason = "threshold_boundary:" + Safe(boundaryReason);
                return false;
            }
        }

        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(snapshot.BotProfileId, out OperatorDecisionSnapshot current))
        {
            acquisitionDeferralsTotal++;
            startReason = "snapshot_refresh_missing";
            return false;
        }
        if (!IsTranslationCompatibleForKind(current, snapshot.Threat.EnemyId, kind, out string authorityReason))
        {
            acquisitionDeferralsTotal++;
            startReason = "translation_incompatible:" + Safe(authorityReason);
            return false;
        }
        // Check the actual fresh NavMesh path, not only a target-independent action gate.
        // Survival moves and defensive regroup remain exempt from the offensive veto.
        if (!IsSurvivalOpportunityKind(kind)
            && !MultiThreatOffensiveSafetyPolicy.IsOffensivePreparedRouteSafe(
                current, snapshot.Threat.EnemyId, kind, preparedPath.corners, now,
                out string offensiveRouteReason))
        {
            acquisitionDeferralsTotal++;
            startReason = "offensive_route_safety:" + Safe(offensiveRouteReason);
            return false;
        }
        if (!MarksmanForwardRoleDisciplinePolicy.CanAuthorizeForwardTranslation(
                current,
                snapshot.Threat.EnemyId,
                destination,
                kind,
                now,
                out string roleDisciplineReason))
        {
            acquisitionDeferralsTotal++;
            startReason = "marksman_role_discipline:" + Safe(roleDisciplineReason);
            return false;
        }
        if (string.Equals(kind, "FlankSupport", StringComparison.OrdinalIgnoreCase)
            && !FlankSupportExecutionSafetyPolicy.IsExecutionSafe(
                current,
                supportBotProfileId,
                destination,
                now,
                requireExposureSample: true,
                out string flankSupportSafetyReason))
        {
            acquisitionDeferralsTotal++;
            startReason = "flank_support_execution_safety:" + Safe(flankSupportSafetyReason);
            return false;
        }

        ResolveLiveTranslationFlags(kind, out bool allowOpportunityCombatFlank, out bool allowStandAndShootCombatAdvance, out bool allowDistantVisibleSearchAction);
        if (!SearchTranslationGateBridge.TryPreflightTacticalMovementLiveTranslation(
                botOwner,
                snapshot.Threat.EnemyId,
                out _,
                out string liveSummary,
                allowOpportunityCombatFlank: allowOpportunityCombatFlank,
                allowStandAndShootCombatAdvance: allowStandAndShootCombatAdvance,
                allowDistantVisibleSearchAction: allowDistantVisibleSearchAction))
        {
            acquisitionDeferralsTotal++;
            startReason = "live_preflight:" + Safe(liveSummary);
            return false;
        }

        if (!VanguardMainIntentScheduler.TryGrantTacticalCoordinatorCombatMovementSidecar(
                current,
                now,
                snapshot.Threat.EnemyId,
                out string schedulerWindowId,
                out string schedulerReason))
        {
            schedulerRejectsTotal++;
            startReason = "scheduler_rejected:" + Safe(schedulerReason);
            return false;
        }

        string leaseId = "tc_opportunity_" + kind.ToLowerInvariant() + "_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(snapshot.BotProfileId);
        DateTimeOffset maxUntil = now + (string.Equals(kind, "OverwatchSupport", StringComparison.OrdinalIgnoreCase)
            ? OverwatchMaximumLeaseDuration
            : IsBoundOpportunityKind(kind)
                ? BoundMaximumLeaseDuration
                : DefaultMaximumLeaseDuration);
        if (!VanguardReturnMovementCommandStore.Issue(
                leaseId,
                snapshot.OperatorId,
                snapshot.BotProfileId,
                destination,
                CommandAnchorRadiusMeters,
                false,
                now,
                maxUntil,
                RequestKind,
                pathSummary,
                pathLength,
                out string commandResult)
            || !VanguardReturnMovementCommandStore.TryGetActive(snapshot.BotProfileId, now, out VanguardReturnMovementCommand command)
            || !string.Equals(command.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(command.RequestKind, RequestKind, StringComparison.OrdinalIgnoreCase))
        {
            commandRejectsTotal++;
            startReason = "command_rejected:" + Safe(commandResult);
            return false;
        }

        string ownerThreatKey = OwnerThreatKey(snapshot.OwnerProfileId, snapshot.Threat.EnemyId);
        var lease = new ActiveOpportunityLease(
            snapshot.OwnerProfileId,
            snapshot.Threat.EnemyId,
            ownerThreatKey,
            snapshot.OperatorId,
            snapshot.BotProfileId,
            kind,
            leaseId,
            continuityKey,
            schedulerWindowId,
            command.Generation,
            destination,
            preparedPath,
            opportunitySummary,
            pathSummary,
            supportBotProfileId,
            supportContextId,
            now,
            maxUntil,
            now + NoProgressDuration,
            botOwner.Position,
            pathLength);

        lock (Sync)
        {
            if (ActiveByBot.ContainsKey(snapshot.BotProfileId) || ActiveBotByOwnerThreat.ContainsKey(ownerThreatKey))
            {
                VanguardReturnMovementCommandStore.ClearOwned(snapshot.BotProfileId, leaseId, now, "tc_tactical_opportunity_race_lost");
                startReason = "lease_race_lost";
                return false;
            }
            ActiveByBot[snapshot.BotProfileId] = lease;
            ActiveBotByOwnerThreat[ownerThreatKey] = snapshot.BotProfileId;
        }

        TacticalIntentContinuityService.BeginOrRefresh(
            snapshot.BotProfileId,
            snapshot.OwnerProfileId,
            snapshot.Threat.EnemyId,
            kind,
            continuityKey,
            now,
            "tactical_opportunity_physical_lease_started",
            protectsFromCohesion: IsProtectedOpportunityKind(kind),
            qualifiedResume: qualifiedResume);

        TacticalEstablishmentShadowService.TrackMoving(
            snapshot.OwnerProfileId,
            snapshot.Threat.EnemyId,
            snapshot.BotProfileId,
            kind,
            leaseId,
            supportBotProfileId,
            supportContextId,
            destination,
            CommandAnchorRadiusMeters + 0.45f,
            now);

        TacticalPlacementQualityShadowService.ObserveStartedLease(
            current,
            kind,
            leaseId,
            preparedPath.corners,
            destination,
            supportBotProfileId,
            supportContextId,
            now);

        UnsupportedPenetrationShadowService.TrackStartedLease(
            current,
            kind,
            leaseId,
            destination,
            supportBotProfileId,
            supportContextId,
            now);

        startsTotal++;
        if (string.Equals(kind, "OverwatchSupport", StringComparison.OrdinalIgnoreCase)) overwatchStartsTotal++;
        else if (string.Equals(kind, "BuddySupport", StringComparison.OrdinalIgnoreCase)) buddySupportStartsTotal++;
        else if (string.Equals(kind, "BoundAdvance", StringComparison.OrdinalIgnoreCase)) boundAdvanceStartsTotal++;
        else if (string.Equals(kind, "BoundRetreat", StringComparison.OrdinalIgnoreCase)) boundRetreatStartsTotal++;
        else if (string.Equals(kind, "BreakContact", StringComparison.OrdinalIgnoreCase)) breakContactStartsTotal++;
        else if (string.Equals(kind, "ThresholdSupport", StringComparison.OrdinalIgnoreCase)) thresholdSupportStartsTotal++;
        else if (string.Equals(kind, "AssaultRelay", StringComparison.OrdinalIgnoreCase)) assaultRelayStartsTotal++;
        else if (string.Equals(kind, "FlankSupport", StringComparison.OrdinalIgnoreCase)) flankSupportStartsTotal++;
        else if (string.Equals(kind, InteriorDevelopOpportunityService.MovementKind, StringComparison.OrdinalIgnoreCase)) interiorDevelopStartsTotal++;
        else regroupStartsTotal++;

        startReason = "started";
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"TC_TACTICAL_OPPORTUNITY_STARTED kind={Safe(kind)}; owner={Safe(snapshot.OwnerProfileId)}; threat={Safe(snapshot.Threat.EnemyId)}; bot={Safe(snapshot.BotProfileId)}; lease={Safe(leaseId)}; schedulerWindow={Safe(schedulerWindowId)}; destination={VectorText(destination)}; pathLength={pathLength:0.0}; admission={Safe(admissionReason)}; authority={Safe(authorityReason)}; live={Safe(liveSummary)}; scheduler={Safe(schedulerReason)}; budget={Safe(budgetReason)}; command={Safe(commandResult)}; opportunity={Safe(opportunitySummary)}; supportBot={Safe(supportBotProfileId)}; supportContext={Safe(supportContextId)}; movementBackend=SAIN_BotPathDataManual; combatPrimaryPreserved=true; sprint=false; targetMutation=false; aimMutation=false; fireCommand=false");
        return true;
    }

    private static void TickActiveLeases(DateTimeOffset now)
    {
        ActiveOpportunityLease[] active;
        lock (Sync)
        {
            active = ActiveByBot.Values.ToArray();
        }

        foreach (ActiveOpportunityLease lease in active)
        {
            TacticalIntentContinuityService.BeginOrRefresh(
                lease.BotProfileId,
                lease.OwnerProfileId,
                lease.ThreatId,
                lease.Kind,
                lease.ContinuityKey,
                now,
                "tactical_opportunity_physical_lease_active",
                protectsFromCohesion: IsProtectedOpportunityKind(lease.Kind));

            if (now >= lease.MaxUntilUtc)
            {
                FinishLease(lease, now, "Interrupted", "maximum_lease_duration", applyCooldown: true);
                continue;
            }
            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(lease.BotProfileId, out VanguardRaidOperatorRuntimeRecord runtime)
                || runtime.BotOwner == null
                || runtime.BotOwner.IsDead)
            {
                FinishLease(lease, now, "Interrupted", "runtime_missing_or_dead", applyCooldown: false);
                continue;
            }
            if (!VanguardReturnMovementCommandStore.TryGetExactOwned(
                    lease.BotProfileId,
                    lease.LeaseId,
                    RequestKind,
                    lease.Generation,
                    now,
                    out _,
                    out string commandReason))
            {
                FinishLease(lease, now, "Interrupted", "command_changed:" + commandReason, applyCooldown: false);
                continue;
            }
            if (!VanguardMainIntentScheduler.IsTacticalCoordinatorCombatMovementSidecarCurrent(
                    lease.BotProfileId,
                    now,
                    lease.ThreatId,
                    lease.SchedulerWindowId,
                    out string schedulerReason))
            {
                FinishLease(lease, now, "Interrupted", "combat_sidecar_changed:" + schedulerReason, applyCooldown: false);
                continue;
            }
            if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(lease.BotProfileId, out OperatorDecisionSnapshot decision))
            {
                FinishLease(lease, now, "Interrupted", "local_authority_changed:snapshot_missing", applyCooldown: false);
                continue;
            }
            TacticalEngagementContextShadowService.ObserveLeaseDecision(
                lease.OwnerProfileId,
                lease.ThreatId,
                lease.BotProfileId,
                lease.Kind,
                lease.LeaseId,
                decision,
                now);
            if (!IsTranslationCompatibleForKind(decision, lease.ThreatId, lease.Kind, out string authorityReason))
            {
                FinishLease(lease, now, "Interrupted", "local_authority_changed:" + authorityReason, applyCooldown: false);
                continue;
            }
            if (!MarksmanForwardRoleDisciplinePolicy.CanAuthorizeForwardTranslation(
                    decision,
                    lease.ThreatId,
                    lease.Destination,
                    lease.Kind,
                    now,
                    out string roleDisciplineReason))
            {
                FinishLease(lease, now, "Interrupted", "marksman_role_discipline_changed:" + roleDisciplineReason, applyCooldown: false);
                continue;
            }
            if (!IsBoundSupportCurrent(lease, runtime.BotOwner.Position, now, out string supportReason))
            {
                boundSupportLossInterruptsTotal++;
                FinishLease(lease, now, "Interrupted", "bound_support_changed:" + supportReason, applyCooldown: false);
                continue;
            }
            if (!IsThresholdSupportContextCurrent(lease, now, out string thresholdReason))
            {
                thresholdSupportContextLossInterruptsTotal++;
                FinishLease(lease, now, "Interrupted", "threshold_support_context_changed:" + thresholdReason, applyCooldown: false);
                continue;
            }
            if (!IsAssaultRelayContextCurrent(lease, now, out string relayReason))
            {
                assaultRelayContextLossInterruptsTotal++;
                FinishLease(lease, now, "Interrupted", "assault_relay_context_changed:" + relayReason, applyCooldown: false);
                continue;
            }
            if (!IsFlankSupportContextCurrent(lease, now, true, out string flankSupportReason))
            {
                flankSupportContextLossInterruptsTotal++;
                FinishLease(lease, now, "Interrupted", "flank_support_context_changed:" + flankSupportReason, applyCooldown: false);
                continue;
            }
            if (!IsInteriorDevelopContextCurrent(lease, now, out string interiorDevelopReason))
            {
                interiorDevelopContextLossInterruptsTotal++;
                FinishLease(lease, now, "Interrupted", "interior_develop_context_changed:" + interiorDevelopReason, applyCooldown: false);
                continue;
            }

            UnsupportedPenetrationShadowService.ObserveActiveLease(
                decision,
                lease.Kind,
                lease.LeaseId,
                lease.Destination,
                lease.SupportBotProfileId,
                lease.SupportContextId,
                now);

            float distance = HorizontalDistance(runtime.BotOwner.Position, lease.Destination);
            if (distance <= CommandAnchorRadiusMeters + 0.45f)
            {
                FinishLease(lease, now, "Completed", "destination_reached", applyCooldown: true);
                continue;
            }

            float moved = HorizontalDistance(runtime.BotOwner.Position, lease.LastProgressPosition);
            if (moved >= ProgressDistanceMeters)
            {
                lease.LastProgressPosition = runtime.BotOwner.Position;
                lease.NoProgressUntilUtc = now + NoProgressDuration;
            }
            else if (now >= lease.NoProgressUntilUtc)
            {
                FinishLease(lease, now, "Failed", "no_progress", applyCooldown: true);
                continue;
            }

            if (VanguardReturnMovementCommandStore.TryConsumePathInvalid(lease.BotProfileId, lease.LeaseId, lease.Generation, now, out string pathInvalid))
            {
                FinishLease(lease, now, "Failed", "physical_path_invalid:" + pathInvalid, applyCooldown: true);
                continue;
            }
            if (VanguardReturnMovementCommandStore.TryConsumePhysicalBackendFailure(lease.BotProfileId, lease.LeaseId, lease.Generation, now, out string backendFailure))
            {
                FinishLease(lease, now, "Failed", "physical_backend_failure:" + backendFailure, applyCooldown: true);
            }
        }
    }

    private static bool FinishLease(ActiveOpportunityLease lease, DateTimeOffset now, string outcome, string reason, bool applyCooldown)
    {
        bool removed;
        lock (Sync)
        {
            removed = ActiveByBot.TryGetValue(lease.BotProfileId, out ActiveOpportunityLease current)
                && string.Equals(current.LeaseId, lease.LeaseId, StringComparison.OrdinalIgnoreCase);
            if (removed)
            {
                ActiveByBot.Remove(lease.BotProfileId);
                if (ActiveBotByOwnerThreat.TryGetValue(lease.OwnerThreatKey, out string activeBot)
                    && string.Equals(activeBot, lease.BotProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    ActiveBotByOwnerThreat.Remove(lease.OwnerThreatKey);
                }
                if (applyCooldown)
                {
                    TimeSpan cooldown = string.Equals(lease.Kind, "OverwatchSupport", StringComparison.OrdinalIgnoreCase)
                        ? TimeSpan.FromSeconds(8d)
                        : ReacquireCooldown;
                    CooldownUntilByBot[lease.BotProfileId] = now + cooldown;
                    CooldownUntilByOwnerThreat[lease.OwnerThreatKey] = now + cooldown;
                }
            }
        }
        if (!removed)
        {
            return false;
        }

        bool pathReleased = SearchTranslationGateBridge.ReleaseTacticalMovementPathBinding(
            lease.BotProfileId,
            RequestKind,
            lease.LeaseId,
            lease.Generation,
            "tc_tactical_opportunity_finish:" + Safe(reason));
        string clearResult = VanguardReturnMovementCommandStore.ClearOwned(
            lease.BotProfileId,
            lease.LeaseId,
            lease.StartedAtUtc,
            "tc_tactical_opportunity_finish:" + Safe(reason));

        bool qualifiedSoftSuspended = string.Equals(outcome, "Interrupted", StringComparison.OrdinalIgnoreCase)
            && TacticalIntentContinuityService.TrySuspendQualifiedSoftInterruption(
                lease.BotProfileId,
                lease.OwnerProfileId,
                lease.ThreatId,
                lease.Kind,
                now,
                reason,
                out _);
        if (!qualifiedSoftSuspended)
        {
            TacticalIntentContinuityService.EndIntent(
                lease.BotProfileId,
                lease.Kind,
                now,
                "physical_lease_terminal:" + outcome + ":" + reason);
        }

        if (string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            completesTotal++;
            if (string.Equals(lease.Kind, "BoundAdvance", StringComparison.OrdinalIgnoreCase))
            {
                PrepareReciprocalBoundAdvanceHandoff(lease, now);
            }
            else if (string.Equals(lease.Kind, InteriorDevelopOpportunityService.MovementKind, StringComparison.OrdinalIgnoreCase))
            {
                RequestFreshReplanAfterInteriorCompletion(lease, now);
            }
        }
        else if (string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase)) failuresTotal++;
        else interruptsTotal++;

        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"TC_TACTICAL_OPPORTUNITY_FINISHED kind={Safe(lease.Kind)}; owner={Safe(lease.OwnerProfileId)}; threat={Safe(lease.ThreatId)}; bot={Safe(lease.BotProfileId)}; supportBot={Safe(lease.SupportBotProfileId)}; supportContext={Safe(lease.SupportContextId)}; lease={Safe(lease.LeaseId)}; outcome={Safe(outcome)}; reason={Safe(reason)}; nativePathTicks={lease.NativePathTicks}; pathReleased={Bool(pathReleased)}; commandClear={Safe(clearResult)}; cooldown={Bool(applyCooldown)}; qualifiedSoftSuspended={Bool(qualifiedSoftSuspended)}; staleResume=false; combatPrimaryPreserved=true; targetMutation=false; aimMutation=false; fireCommand=false");

        TacticalOpportunityEndClassificationShadowService.ObserveEnd(
            lease.Kind,
            lease.OwnerProfileId,
            lease.ThreatId,
            lease.BotProfileId,
            lease.LeaseId,
            outcome,
            reason,
            lease.StartedAtUtc,
            now,
            lease.NativePathTicks,
            applyCooldown);
        TacticalEstablishmentShadowService.ObserveEnd(
            lease.BotProfileId,
            lease.LeaseId,
            outcome,
            reason,
            now);
        TacticalEngagementContextShadowService.ObserveEnd(
            lease.BotProfileId,
            lease.LeaseId,
            outcome,
            reason,
            now);
        UnsupportedPenetrationShadowService.ObserveEnd(
            lease.BotProfileId,
            lease.LeaseId,
            outcome,
            reason,
            now);
        return true;
    }

    private static void RequestFreshReplanAfterInteriorCompletion(ActiveOpportunityLease lease, DateTimeOffset now)
    {
        if (!TacticalReplanService.TryGetSnapshot(out TacticalReplanSnapshot replanSnapshot)
            || !replanSnapshot.IsAvailable)
        {
            VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                $"TC_INTERIOR_DEVELOP_COMPLETION_REFRESH owner={Safe(lease.OwnerProfileId)}; threat={Safe(lease.ThreatId)}; bot={Safe(lease.BotProfileId)}; lease={Safe(lease.LeaseId)}; planEpoch=0; freshReplanRequested=false; reason=replan_snapshot_unavailable; automaticFollowOn=false");
            return;
        }

        PlanLifecycleObservation? plan = replanSnapshot.Plans
            .Where(item => string.Equals(item.OwnerProfileId, lease.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ThreatId, lease.ThreatId, StringComparison.OrdinalIgnoreCase)
                && item.PlanEpoch > 0)
            .OrderByDescending(item => item.PlanEpoch)
            .FirstOrDefault();
        if (plan == null)
        {
            VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                $"TC_INTERIOR_DEVELOP_COMPLETION_REFRESH owner={Safe(lease.OwnerProfileId)}; threat={Safe(lease.ThreatId)}; bot={Safe(lease.BotProfileId)}; lease={Safe(lease.LeaseId)}; planEpoch=0; freshReplanRequested=false; reason=no_current_plan_epoch; automaticFollowOn=false");
            return;
        }

        bool requested = RouteAssignmentService.RequestFreshTacticalReplan(
            lease.OwnerProfileId,
            lease.ThreatId,
            plan.PlanEpoch,
            now,
            "interior_segment_completed:" + Safe(lease.SupportContextId));
        if (requested)
        {
            interiorDevelopCompletionReplanRequestsTotal++;
        }
        else
        {
            interiorDevelopCompletionReplanCoalescedTotal++;
        }
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"TC_INTERIOR_DEVELOP_COMPLETION_REFRESH owner={Safe(lease.OwnerProfileId)}; threat={Safe(lease.ThreatId)}; bot={Safe(lease.BotProfileId)}; lease={Safe(lease.LeaseId)}; planEpoch={plan.PlanEpoch}; freshReplanRequested={Bool(requested)}; coalescedWithPending={Bool(!requested)}; automaticFollowOn=false; cooldownPreserved=true; movementStart=false; targetMutation=false; aimMutation=false; fireCommand=false");
    }

    private static void PrepareReciprocalBoundAdvanceHandoff(ActiveOpportunityLease lease, DateTimeOffset now)
    {
        if (lease == null
            || string.IsNullOrWhiteSpace(lease.SupportBotProfileId)
            || string.Equals(lease.SupportBotProfileId, lease.BotProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var handoff = new BoundAdvanceHandoff(
            lease.OwnerProfileId,
            lease.ThreatId,
            lease.OwnerThreatKey,
            lease.SupportBotProfileId,
            lease.BotProfileId,
            lease.LeaseId,
            now,
            now + BoundAdvanceHandoffTtl);
        lock (Sync)
        {
            BoundAdvanceHandoffByOwnerThreat[lease.OwnerThreatKey] = handoff;
        }
        boundAdvanceHandoffsPreparedTotal++;
        VanguardTacticalCoordinationTuningPolicy.OwnerTacticalCoordinationTuning tuning =
            VanguardTacticalCoordinationTuningPolicy.ForOwner(lease.OwnerProfileId);
        float reciprocalSupportLeadMeters = ResolveBoundAdvanceSupportLeadMeters(tuning.BoundAdvanceDistanceMeters, reciprocal: true);
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"TC_BOUND_ADVANCE_HANDOFF_PREPARED owner={Safe(lease.OwnerProfileId)}; threat={Safe(lease.ThreatId)}; nextMover={Safe(lease.SupportBotProfileId)}; nextSupport={Safe(lease.BotProfileId)}; sourceLease={Safe(lease.LeaseId)}; ttlSeconds={BoundAdvanceHandoffTtl.TotalSeconds:0.0}; reciprocalPreference=true; sameAuthoritySafetyGatesRequired=true; reciprocalSupportLeadMeters={reciprocalSupportLeadMeters:0.0}; boundAdvanceDistanceMeters={tuning.BoundAdvanceDistanceMeters:0.0}; automaticSequence=false; fireCommand=false; aimMutation=false; targetMutation=false");
    }

    private static bool ForgetBoundAdvanceHandoffPresence(string botProfileId, string reason)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return false;
        }

        List<BoundAdvanceHandoff> removed = new();
        lock (Sync)
        {
            foreach (KeyValuePair<string, BoundAdvanceHandoff> pair in BoundAdvanceHandoffByOwnerThreat
                .Where(item => string.Equals(item.Value.NextMoverBotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Value.NextSupportBotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase))
                .ToArray())
            {
                if (BoundAdvanceHandoffByOwnerThreat.Remove(pair.Key))
                {
                    removed.Add(pair.Value);
                }
            }
        }

        if (removed.Count <= 0)
        {
            return false;
        }

        boundAdvanceHandoffFallbacksTotal += removed.Count;
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"TC_BOUND_ADVANCE_HANDOFF_PRESENCE_FORGOTTEN bot={Safe(botProfileId)}; handoffsRemoved={removed.Count}; reason={Safe(reason)}; failOpen=true; staleHandoff=false; automaticSequence=false");
        return true;
    }

    private static string BuildOpportunityContinuityKey(
        string kind,
        OperatorDecisionSnapshot snapshot,
        Vector3 destination,
        string supportBotProfileId,
        string supportContextId,
        Vector3? boundaryPortal,
        Vector3? boundaryForward)
    {
        // Logical continuity is intentionally separate from the disposable physical lease id.
        // A short grenade/cover interruption may resume only the same contribution geometry:
        // same movement kind, same support relationship, same coarse destination cell and same
        // optional boundary frame. A materially different placement is a fresh intent, not a resume.
        string destinationCell = QuantizedContinuityVector(destination, 2.0f);
        string portalCell = boundaryPortal.HasValue
            ? QuantizedContinuityVector(boundaryPortal.Value, 1.5f)
            : "none";
        string forwardCell = boundaryForward.HasValue
            ? QuantizedContinuityVector(boundaryForward.Value, 0.25f)
            : "none";

        return string.Join("|",
            Safe(kind).ToLowerInvariant(),
            Safe(snapshot?.OwnerProfileId).ToLowerInvariant(),
            Safe(snapshot?.Threat.EnemyId).ToLowerInvariant(),
            Safe(supportBotProfileId).ToLowerInvariant(),
            Safe(supportContextId).ToLowerInvariant(),
            destinationCell,
            portalCell,
            forwardCell);
    }

    private static string QuantizedContinuityVector(Vector3 value, float quantum)
    {
        float safeQuantum = Math.Max(0.1f, quantum);
        int x = (int)Math.Round(value.x / safeQuantum, MidpointRounding.AwayFromZero);
        int y = (int)Math.Round(value.y / safeQuantum, MidpointRounding.AwayFromZero);
        int z = (int)Math.Round(value.z / safeQuantum, MidpointRounding.AwayFromZero);
        return x.ToString(CultureInfo.InvariantCulture)
            + "," + y.ToString(CultureInfo.InvariantCulture)
            + "," + z.ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryBasicAdmission(OperatorDecisionSnapshot snapshot, string kind, DateTimeOffset now, out string reason)
    {
        reason = "unknown";
        if (snapshot == null || !snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            reason = "snapshot_dead_or_missing";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(snapshot, now, out string freshnessReason))
        {
            reason = freshnessReason;
            return false;
        }
        if (IsCooldownActive(snapshot.BotProfileId, now))
        {
            reason = "opportunity_reacquire_cooldown";
            return false;
        }
        if (VanguardReturnMovementCommandStore.TryGetActive(snapshot.BotProfileId, now, out _)
            || AntiCongaService.HasActiveAdvanceGateForBot(snapshot.BotProfileId)
            || ChokeCommitSupportService.HasActiveContributionForBot(snapshot.BotProfileId))
        {
            reason = "existing_physical_contribution_or_command";
            return false;
        }
        if (!IsTranslationCompatibleForKind(snapshot, snapshot.Threat.EnemyId, kind, out string authorityReason))
        {
            reason = authorityReason;
            return false;
        }
        reason = "admitted_existing_combat_sidecar_candidate:" + Safe(kind);
        return true;
    }

    private static bool TryGetOverwatchCapability(BotOwner botOwner, DateTimeOffset now, out int equipmentRank, out string summary)
    {
        equipmentRank = 0;
        summary = "no_long_range_ready_weapon";
        VanguardLiveEquipmentCapabilitySnapshot equipment = CaptureEquipmentCached(botOwner, now);
        if (!equipment.Observed)
        {
            summary = equipment.ObservationReason;
            return false;
        }

        VanguardLiveWeaponCapabilityObservation? best = null;
        int bestRank = 0;
        foreach (VanguardLiveWeaponCapabilityObservation weapon in equipment.Weapons.Where(item => item.ReadyRounds > 0))
        {
            int rank = OverwatchEquipmentRank(weapon);
            if (rank > bestRank)
            {
                best = weapon;
                bestRank = rank;
            }
        }
        if (best == null || bestRank < 4)
        {
            return false;
        }

        equipmentRank = bestRank;
        summary = "slot=" + best.Slot
            + ":family=" + best.CanonicalWeaponFamily
            + ":ready=" + best.ReadyRounds.ToString(CultureInfo.InvariantCulture)
            + ":magnified=" + best.MagnifiedSightCount.ToString(CultureInfo.InvariantCulture)
            + ":zoom=" + best.MaxSupportedSightZoom.ToString("0.0", CultureInfo.InvariantCulture);
        return true;
    }

    private static VanguardLiveEquipmentCapabilitySnapshot CaptureEquipmentCached(BotOwner botOwner, DateTimeOffset now)
        => VanguardDynamicWeaponPreferenceService.GetEquipmentSnapshot(botOwner, now);

    private static int OverwatchEquipmentRank(VanguardLiveWeaponCapabilityObservation weapon)
    {
        string family = weapon.CanonicalWeaponFamily?.Trim().ToUpperInvariant() ?? "UNKNOWN";
        int familyRank = family switch
        {
            "SNIPER_RIFLE" => 6,
            "MARKSMAN_RIFLE" => 6,
            "MACHINE_GUN" => 5,
            "ASSAULT_RIFLE" => 3,
            "CARBINE" => 2,
            _ => 0,
        };
        if (weapon.MagnifiedSightCount > 0 && familyRank >= 2)
        {
            familyRank += 2;
        }
        return familyRank;
    }

    private static int OverwatchRoleRank(string? role)
        => NormalizeRole(role) switch
        {
            "marksman" => 5,
            "support" => 4,
            "recon" => 3,
            "veteran" => 2,
            "assault" => 1,
            _ => 0,
        };

    private static int BuddySupportRoleRank(string botProfileId)
    {
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botProfileId, out VanguardRaidOperatorRuntimeRecord runtime))
        {
            return 0;
        }
        return NormalizeRole(runtime.TacticalRole) switch
        {
            "support" => 5,
            "veteran" => 4,
            "assault" => 4,
            "medic" => 3,
            "recon" => 2,
            "marksman" => 2,
            "breacher" => 2,
            _ => 1,
        };
    }

    private static bool IsIntentionalTacticalSeparation(OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        // Use the same material-change validator as scheduler/travel. A target identity change or
        // danger-close contact immediately drops protection instead of leaving the member hidden
        // from Buddy/Regroup until a passive timeout expires.
        return TacticalIntentContinuityService.ShouldDeferCohesion(snapshot, now, out _);
    }

    private static bool IsProtectedOpportunityKind(string kind)
        => string.Equals(kind, "OverwatchSupport", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "FlankSupport", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "ThresholdSupport", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "AssaultRelay", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, InteriorDevelopOpportunityService.MovementKind, StringComparison.OrdinalIgnoreCase)
            || IsBoundOpportunityKind(kind);

    private static bool IsSupportEnvelopeOpportunityKind(string kind)
        => string.Equals(kind, "OverwatchSupport", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "BuddySupport", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "FlankSupport", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "ThresholdSupport", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "AssaultRelay", StringComparison.OrdinalIgnoreCase);

    private static bool IsTranslationCompatibleForKind(
        OperatorDecisionSnapshot snapshot,
        string threatId,
        string kind,
        out string reason)
    {
        if (string.Equals(kind, "BoundAdvance", StringComparison.OrdinalIgnoreCase))
        {
            if (!AltDevelopAuthorityPolicy.IsBoundAdvanceTranslationCompatible(snapshot, threatId, out reason))
            {
                return false;
            }

            if (!MultiThreatOffensiveSafetyPolicy.IsOffensiveTranslationSafe(
                    snapshot,
                    threatId,
                    "BoundAdvance",
                    DateTimeOffset.UtcNow,
                    out string multiThreatReason))
            {
                reason = "multi_threat_offensive_safety:" + multiThreatReason;
                return false;
            }

            reason += ":multi_threat_offensive_safety_ready";
            return true;
        }

        if (IsSurvivalOpportunityKind(kind))
        {
            if (!AltDevelopAuthorityPolicy.IsTacticalOpportunityTranslationCompatibleWithoutOffensiveSafety(snapshot, threatId, out reason))
            {
                return false;
            }

            reason += ":multi_threat_offensive_veto_not_applicable_survival";
            return true;
        }

        if (IsCombatSupportOpportunityKind(kind))
        {
            if (!AltDevelopAuthorityPolicy.IsTacticalOpportunityTranslationCompatibleWithoutOffensiveSafety(snapshot, threatId, out reason))
            {
                return false;
            }
            if (!MultiThreatOffensiveSafetyPolicy.IsSupportMateriallyReady(snapshot, DateTimeOffset.UtcNow, out string materialReason))
            {
                reason = "support_material_not_ready:" + Safe(materialReason);
                return false;
            }

            reason += ":support_material_credible";
            return true;
        }

        return AltDevelopAuthorityPolicy.IsTacticalOpportunityTranslationCompatible(snapshot, threatId, out reason);
    }

    private static bool IsSurvivalOpportunityKind(string kind)
        => string.Equals(kind, "EmergencyRegroup", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "CombatRegroup", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "BoundRetreat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "BreakContact", StringComparison.OrdinalIgnoreCase);

    private static bool IsCombatSupportOpportunityKind(string kind)
        => string.Equals(kind, "OverwatchSupport", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "BuddySupport", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "FlankSupport", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "ThresholdSupport", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "AssaultRelay", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, InteriorDevelopOpportunityService.MovementKind, StringComparison.OrdinalIgnoreCase);

    private static void ResolveLiveTranslationFlags(
        string kind,
        out bool allowOpportunityCombatFlank,
        out bool allowStandAndShootCombatAdvance,
        out bool allowDistantVisibleSearchAction)
    {
        bool boundAdvance = string.Equals(kind, "BoundAdvance", StringComparison.OrdinalIgnoreCase);
        allowOpportunityCombatFlank = !boundAdvance;
        allowStandAndShootCombatAdvance = boundAdvance;
        allowDistantVisibleSearchAction = true;
    }

    private static bool IsBoundOpportunityKind(string kind)
        => string.Equals(kind, "BoundAdvance", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "BoundRetreat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "BreakContact", StringComparison.OrdinalIgnoreCase);

    private static bool IsBoundSupportCurrent(
        ActiveOpportunityLease lease,
        Vector3 moverPosition,
        DateTimeOffset now,
        out string reason)
    {
        reason = "not_required";
        if (!IsBoundOpportunityKind(lease.Kind))
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(lease.SupportBotProfileId))
        {
            reason = "support_identity_missing";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(lease.SupportBotProfileId, out OperatorDecisionSnapshot support)
            || support == null
            || !support.Alive)
        {
            reason = "support_snapshot_missing_or_dead";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(support, now, out string freshnessReason))
        {
            reason = "support_" + freshnessReason;
            return false;
        }
        if (!string.Equals(support.OwnerProfileId, lease.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "support_owner_changed";
            return false;
        }
        if (!string.Equals(NormalizeThreat(support.Threat.EnemyId), NormalizeThreat(lease.ThreatId), StringComparison.OrdinalIgnoreCase))
        {
            reason = "support_threat_changed";
            return false;
        }
        if (support.Sain.RunningToCover == true)
        {
            reason = "support_running_to_cover";
            return false;
        }
        if (support.GrenadeHazard.HasRelevantHazard
            || support.Medical.Actionability.GrenadeThrowing
            || support.Medical.Actionability.AnyMedicineUsing
            || support.Medical.Actionability.Reloading)
        {
            reason = "support_local_survival_action";
            return false;
        }
        if (IsIntentionalTacticalSeparation(support, now))
        {
            reason = "support_committed_to_other_tactical_intent";
            return false;
        }
        if (!MultiThreatOffensiveSafetyPolicy.IsSupportMateriallyReady(support, now, out string supportMaterialReason))
        {
            reason = "support_material_not_ready:" + Safe(supportMaterialReason);
            return false;
        }
        if (string.Equals(lease.Kind, "BoundAdvance", StringComparison.OrdinalIgnoreCase)
            && support.Threat.EnemyVisible == true
            && support.Threat.Distance.HasValue
            && support.Threat.Distance.Value <= AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters)
        {
            reason = "support_close_visible_dogfight";
            return false;
        }
        if (string.Equals(lease.Kind, "BoundAdvance", StringComparison.OrdinalIgnoreCase)
            && support.Threat.EnemyVisible != true
            && support.Threat.EnemyCanShoot != true
            && !string.Equals(TacticalSearchPolicy.NormalizeToken(support.Sain.CurrentAction), "StandAndShootAction", StringComparison.OrdinalIgnoreCase))
        {
            reason = "support_line_evidence_lost";
            return false;
        }
        if (HorizontalDistance(moverPosition, support.Position) > BoundSupportMaxSeparationMeters)
        {
            reason = "support_separation_exceeded";
            return false;
        }

        reason = "support_current";
        return true;
    }


    private static bool IsInteriorDevelopContextCurrent(ActiveOpportunityLease lease, DateTimeOffset now, out string reason)
    {
        reason = "not_required";
        if (!string.Equals(lease.Kind, InteriorDevelopOpportunityService.MovementKind, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(lease.SupportBotProfileId)
            || string.IsNullOrWhiteSpace(lease.SupportContextId))
        {
            reason = "support_or_topology_context_missing";
            return false;
        }

        if (InteriorDevelopGenericFallbackService.IsGenericContextId(lease.SupportContextId))
        {
            return InteriorDevelopGenericFallbackService.IsContextCurrent(
                lease.OwnerProfileId,
                lease.ThreatId,
                lease.BotProfileId,
                lease.SupportBotProfileId,
                lease.SupportContextId,
                lease.Destination,
                now,
                out reason);
        }
        return InteriorDevelopOpportunityService.IsContextCurrent(
            lease.OwnerProfileId,
            lease.ThreatId,
            lease.BotProfileId,
            lease.SupportBotProfileId,
            lease.SupportContextId,
            lease.Destination,
            now,
            out reason);
    }

    private static bool IsThresholdSupportContextCurrent(ActiveOpportunityLease lease, DateTimeOffset now, out string reason)
    {
        reason = "not_required";
        if (!string.Equals(lease.Kind, "ThresholdSupport", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(lease.SupportBotProfileId) || string.IsNullOrWhiteSpace(lease.SupportContextId))
        {
            reason = "source_breach_identity_missing";
            return false;
        }
        if (!DirectCommitMovementService.TryGetActiveBreachSupportContext(
                lease.OwnerProfileId,
                lease.ThreatId,
                now,
                out string leadBotProfileId,
                out string sourceLeaseId,
                out _,
                out _,
                out _,
                out string breachReason))
        {
            reason = "source_breach_not_current:" + breachReason;
            return false;
        }
        if (!string.Equals(leadBotProfileId, lease.SupportBotProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(sourceLeaseId, lease.SupportContextId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "source_breach_identity_changed";
            return false;
        }
        if (!RouteAssignmentService.TryGetSnapshot(out RouteAssignmentSnapshot assignmentSnapshot)
            || !assignmentSnapshot.IsAvailable)
        {
            reason = "route_assignment_not_current";
            return false;
        }
        ThreatGroupRouteAssignment? assignment = assignmentSnapshot.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, lease.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, lease.ThreatId, StringComparison.OrdinalIgnoreCase));
        if (assignment == null
            || !assignment.DirectBotProfileIds.Contains(lease.SupportBotProfileId, StringComparer.OrdinalIgnoreCase)
            || !assignment.DirectBotProfileIds.Contains(lease.BotProfileId, StringComparer.OrdinalIgnoreCase))
        {
            reason = "direct_membership_changed";
            return false;
        }
        reason = "current_exact_breach_entry_and_direct_membership";
        return true;
    }

    private static bool IsAssaultRelayContextCurrent(ActiveOpportunityLease lease, DateTimeOffset now, out string reason)
    {
        reason = "not_required";
        if (!string.Equals(lease.Kind, "AssaultRelay", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(lease.SupportBotProfileId) || string.IsNullOrWhiteSpace(lease.SupportContextId))
        {
            reason = "source_commit_identity_missing";
            return false;
        }
        if (!DirectCommitMovementService.TryGetActiveRelayContext(
                lease.OwnerProfileId,
                lease.ThreatId,
                now,
                out string leadBotProfileId,
                out string sourceLeaseId,
                out _,
                out _,
                out string commitReason))
        {
            reason = "source_commit_not_current:" + commitReason;
            return false;
        }
        if (!string.Equals(leadBotProfileId, lease.SupportBotProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(sourceLeaseId, lease.SupportContextId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "source_commit_identity_changed";
            return false;
        }
        if (!RouteAssignmentService.TryGetSnapshot(out RouteAssignmentSnapshot assignmentSnapshot)
            || !assignmentSnapshot.IsAvailable)
        {
            reason = "route_assignment_not_current";
            return false;
        }
        ThreatGroupRouteAssignment? assignment = assignmentSnapshot.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, lease.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, lease.ThreatId, StringComparison.OrdinalIgnoreCase));
        if (assignment == null
            || !assignment.DirectBotProfileIds.Contains(lease.SupportBotProfileId, StringComparer.OrdinalIgnoreCase)
            || !assignment.DirectBotProfileIds.Contains(lease.BotProfileId, StringComparer.OrdinalIgnoreCase))
        {
            reason = "direct_membership_changed";
            return false;
        }
        reason = "current_exact_source_commit_and_direct_membership";
        return true;
    }

    private static bool IsFlankSupportContextCurrent(ActiveOpportunityLease lease, DateTimeOffset now, bool recordRouteGapContinuityTick, out string reason)
    {
        reason = "not_required";
        if (!string.Equals(lease.Kind, "FlankSupport", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(lease.SupportContextId))
        {
            reason = "flank_lead_maneuver_identity_missing";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(lease.BotProfileId, out OperatorDecisionSnapshot mover)
            || mover == null)
        {
            reason = "flank_support_execution_safety_not_current:mover_snapshot_missing";
            return false;
        }
        if (!FlankSupportExecutionSafetyPolicy.IsExecutionSafe(
                mover,
                lease.SupportBotProfileId,
                lease.Destination,
                now,
                requireExposureSample: false,
                out string executionSafetyReason))
        {
            reason = "flank_support_execution_safety_not_current:" + Safe(executionSafetyReason);
            return false;
        }

        if (!RouteAssignmentService.TryGetSnapshot(out RouteAssignmentSnapshot assignments))
        {
            return TryKeepFlankSupportAcrossRoutePublicationGap(
                lease,
                now,
                "route_assignment_missing",
                recordRouteGapContinuityTick,
                out reason);
        }

        ThreatGroupRouteAssignment? assignment = assignments.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, lease.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, lease.ThreatId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.SelectedAltSupportBotProfileId, lease.BotProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.SelectedAltBotProfileId, lease.SupportBotProfileId, StringComparison.OrdinalIgnoreCase));
        if (assignment == null)
        {
            bool replacementPlanningIntentExists = assignments.ThreatGroups.Any(item =>
                string.Equals(item.OwnerProfileId, lease.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && item.HasShadowAltSelection
                && item.CandidateMatrixComplete
                && item.SelectedPairPathProven
                && item.SelectedAltPlanningViable);
            if (replacementPlanningIntentExists)
            {
                reason = "assignment_or_support_identity_changed";
                return false;
            }

            return TryKeepFlankSupportAcrossRoutePublicationGap(
                lease,
                now,
                "route_assignment_owner_omission",
                recordRouteGapContinuityTick,
                out reason);
        }
        if (!assignment.HasShadowAltSupportSelection)
        {
            reason = "assignment_or_support_identity_changed";
            return false;
        }

        bool currentManeuver = AltDevelopMovementService.TryGetCurrentDevelopmentManeuverProof(
            assignment,
            now,
            out string currentManeuverEpochId,
            out bool physicalArrivalObserved,
            out AltDevelopEvidence evidence);
        if (currentManeuver
            && !string.Equals(currentManeuverEpochId, lease.SupportContextId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "flank_lead_maneuver_epoch_changed";
            return false;
        }
        bool stableCompletion = !currentManeuver
            && AltDevelopMovementService.TryGetStableDevelopmentCompletionProof(
                assignment,
                lease.SupportContextId,
                now,
                out evidence);
        if (!currentManeuver && !stableCompletion)
        {
            reason = "flank_lead_exact_maneuver_not_current_or_completed";
            return false;
        }
        if (!IsFlankLeadSnapshotCurrent(lease, now))
        {
            reason = "flank_lead_snapshot_not_current";
            return false;
        }
        reason = stableCompletion
            ? "flank_lead_stable_completion_same_maneuver"
            : physicalArrivalObserved
                ? "flank_lead_current_maneuver_arrived"
                : "flank_lead_current_maneuver";
        return true;
    }

    private static bool TryKeepFlankSupportAcrossRoutePublicationGap(
        ActiveOpportunityLease lease,
        DateTimeOffset now,
        string routeGapReason,
        bool recordRouteGapContinuityTick,
        out string reason)
    {
        if (!AltDevelopMovementService.TryGetRecentExactDevelopmentManeuverContinuityProof(
                lease.OwnerProfileId,
                lease.ThreatId,
                lease.SupportBotProfileId,
                lease.SupportContextId,
                now,
                out double gapSeconds,
                out string continuityReason))
        {
            reason = routeGapReason + ":" + continuityReason;
            return false;
        }
        if (!IsFlankLeadSnapshotCurrent(lease, now))
        {
            reason = "flank_lead_snapshot_not_current";
            return false;
        }

        if (recordRouteGapContinuityTick)
        {
            flankSupportRouteGapContinuityTicksTotal++;
        }
        reason = routeGapReason
            + "_same_maneuver_grace:gap="
            + gapSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + ":" + continuityReason;
        return true;
    }

    private static bool IsFlankLeadSnapshotCurrent(ActiveOpportunityLease lease, DateTimeOffset now)
        => VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(lease.SupportBotProfileId, out OperatorDecisionSnapshot lead)
            && lead != null
            && lead.Alive
            && VanguardOperatorDecisionSnapshotService.IsFresh(lead, now, out _)
            && string.Equals(lead.OwnerProfileId, lease.OwnerProfileId, StringComparison.OrdinalIgnoreCase);

    private static bool IsDangerExposed(OperatorDecisionSnapshot snapshot)
        => snapshot.Threat.DirectThreat
            || snapshot.Threat.EnemyVisible == true
            || snapshot.Threat.EnemyCanShoot == true
            || snapshot.Threat.ShotAtMeRecently == true
            || snapshot.Threat.ShotMeRecently == true;

    // Active owner/threat reservations and completed-opportunity cooldowns are distinct.
    // FlankSupport may only bypass the latter after proving a live AltDevelop lease;
    // this hard active-reservation gate is never bypassed, including on race/retry.
    private static bool IsOwnerThreatActivelyReserved(string ownerThreatKey)
    {
        lock (Sync)
        {
            return ActiveBotByOwnerThreat.ContainsKey(ownerThreatKey);
        }
    }

    private static bool IsOwnerThreatReserved(string ownerThreatKey, DateTimeOffset now)
    {
        lock (Sync)
        {
            if (ActiveBotByOwnerThreat.ContainsKey(ownerThreatKey))
            {
                return true;
            }
            if (CooldownUntilByOwnerThreat.TryGetValue(ownerThreatKey, out DateTimeOffset until))
            {
                if (until > now)
                {
                    return true;
                }
                CooldownUntilByOwnerThreat.Remove(ownerThreatKey);
            }
            return false;
        }
    }

    private static bool IsCooldownActive(string botProfileId, DateTimeOffset now)
    {
        lock (Sync)
        {
            if (!CooldownUntilByBot.TryGetValue(botProfileId, out DateTimeOffset until)) return false;
            if (until <= now)
            {
                CooldownUntilByBot.Remove(botProfileId);
                return false;
            }
            return true;
        }
    }

    private static ActiveOpportunityLease? FindActive(string? botProfileId)
    {
        if (string.IsNullOrWhiteSpace(botProfileId)) return null;
        lock (Sync)
        {
            ActiveByBot.TryGetValue(botProfileId.Trim(), out ActiveOpportunityLease? lease);
            return lease;
        }
    }

    private static void EmitSummaryIfDue(DateTimeOffset now)
    {
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_TACTICAL_OPPORTUNITY_BOOT enabled=true; request={RequestKind}; contributions=EmergencyRegroup|BreakContact|BoundRetreat|ThresholdSupport|AssaultRelay|BuddySupport|OverwatchSupport|InteriorDevelop|FlankSupport|BoundAdvance|CombatRegroup; thresholdSupportSource=active_breach_entry_exact_lease; thresholdSupportPortalCrossing=false; thresholdSupportFollowOn=false; assaultRelaySource=active_ordinary_direct_commit_exact_lease; assaultRelayBreachChaining=false; interiorDevelopAuthoredFirst=true; interiorDevelopGenericSameVolumeFallback=true; interiorDevelopAutomaticFollowOn=false; interiorDevelopCompletionFreshReplan=true; flankSupportLeadProof=current_exact_altdevelop_maneuver_epoch; flankSupportActivePhysicalLeasePreferred=true; flankSupportPhysicalLeaseGapContinuity=true; flankSupportRouteAssignmentGapContinuity=exact_maneuver_epoch_bounded; routeAssignmentPhysicalContinuityGraceSeconds={RouteAssignmentService.PhysicalConsumerContinuityGraceDuration.TotalSeconds:0.0}; boundAdvanceReciprocalHandoff=opportunistic_current_gates_ttl; boundAdvanceDistanceDefaultMeters={VanguardTacticalCoordinationTuningPolicy.DefaultBoundAdvanceDistanceMeters:0.0}; boundAdvanceCoverLeadDerived=true; boundAdvanceReciprocalCoverLeadDerived=true; dynamicWeaponPreference=dedicated_sidecar; overwatchWeaponAuthority=dynamic_preference_only; existingCombatPrimaryRequired=true; schedulerMode=bounded_combat_sidecar; movementBackend=SAIN_BotPathDataManual; opportunityCombatActions=SearchAction|FollowSearchParty|SeekCoverAction|StandAndShootAction; closeVisibleThreatVetoMeters={AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters:0}; tacticalTuningScope=PLAYER_SCOPED; proactiveWeaponSuitabilityDefaultMeters={VanguardTacticalCoordinationTuningPolicy.DefaultProactiveWeaponMeters:0}; dynamicWeaponPreferenceThreatSources=DecisionSnapshot|QualifiedAwarenessContact; awarenessContactTargetPromotion=false; dangerCloseWeaponSuitabilityDefaultMeters={VanguardTacticalCoordinationTuningPolicy.DefaultDangerCloseWeaponMeters:0}; overwatchMinThreatDefaultMeters={VanguardTacticalCoordinationTuningPolicy.DefaultOverwatchMinimumThreatMeters:0}; weaponSuitabilityDefaultHysteresisMeters={(VanguardTacticalCoordinationTuningPolicy.DefaultOverwatchMinimumThreatMeters - VanguardTacticalCoordinationTuningPolicy.DefaultProactiveWeaponMeters):0}; buddyIsolationDefaultMeters={VanguardTacticalCoordinationTuningPolicy.DefaultBuddyIsolationMeters:0}; hardRegroupMeters={VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters:0}; oneOwnerThreatOpportunity=true; sprint=false; targetMutation=false; aimMutation=false; fireCommand=false; localCombatAuthority=SAIN");
        }
        if (now < nextSummaryAtUtc) return;
        nextSummaryAtUtc = now + SummaryInterval;
        int active;
        lock (Sync) active = ActiveByBot.Count;
        VanguardClientDiagnosticsLog.DiagnosticSummary(StatusTag, "TC_TACTICAL_OPPORTUNITY_SUMMARY", () =>
            $"TC_TACTICAL_OPPORTUNITY_SUMMARY active={active}; startsTotal={startsTotal}; completesTotal={completesTotal}; interruptsTotal={interruptsTotal}; failuresTotal={failuresTotal}; overwatchStartsTotal={overwatchStartsTotal}; buddySupportStartsTotal={buddySupportStartsTotal}; regroupStartsTotal={regroupStartsTotal}; boundAdvanceStartsTotal={boundAdvanceStartsTotal}; boundAdvanceHandoffsPreparedTotal={boundAdvanceHandoffsPreparedTotal}; boundAdvanceHandoffStartsTotal={boundAdvanceHandoffStartsTotal}; boundAdvanceHandoffFallbacksTotal={boundAdvanceHandoffFallbacksTotal}; boundRetreatStartsTotal={boundRetreatStartsTotal}; breakContactStartsTotal={breakContactStartsTotal}; thresholdSupportStartsTotal={thresholdSupportStartsTotal}; thresholdSupportContextLossInterruptsTotal={thresholdSupportContextLossInterruptsTotal}; assaultRelayStartsTotal={assaultRelayStartsTotal}; assaultRelayContextLossInterruptsTotal={assaultRelayContextLossInterruptsTotal}; flankSupportStartsTotal={flankSupportStartsTotal}; flankSupportActiveLeaseProofStartsTotal={flankSupportActiveLeaseProofStartsTotal}; flankSupportManeuverContinuityStartsTotal={flankSupportManeuverContinuityStartsTotal}; flankSupportContextLossInterruptsTotal={flankSupportContextLossInterruptsTotal}; flankSupportRouteGapContinuityTicksTotal={flankSupportRouteGapContinuityTicksTotal}; interiorDevelopStartsTotal={interiorDevelopStartsTotal}; interiorDevelopGenericStartsTotal={interiorDevelopGenericStartsTotal}; interiorDevelopContextLossInterruptsTotal={interiorDevelopContextLossInterruptsTotal}; interiorDevelopCompletionReplanRequestsTotal={interiorDevelopCompletionReplanRequestsTotal}; interiorDevelopCompletionReplanCoalescedTotal={interiorDevelopCompletionReplanCoalescedTotal}; boundSupportLossInterruptsTotal={boundSupportLossInterruptsTotal}; overwatchWeaponAuthority=dynamic_preference_only; dynamicWeaponPreference=external_summary; acquisitionDeferralsTotal={acquisitionDeferralsTotal}; pathRejectsTotal={pathRejectsTotal}; schedulerRejectsTotal={schedulerRejectsTotal}; commandRejectsTotal={commandRejectsTotal}; nativePathTicksTotal={nativePathTicksTotal}; movementBackend=SAIN_BotPathDataManual; combatPrimaryPreserved=true; targetMutation=false; aimMutation=false; fireCommand=false");
    }

    private static void ResolveLiveThreatPose(
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        out Vector3 position,
        out Vector3 lookDirection,
        out string source)
    {
        position = snapshot.Threat.ThreatAnchorPosition ?? snapshot.Position;
        lookDirection = Vector3.zero;
        source = "decision_threat_anchor";
        if (!ThreatExposureObservationService.TryGetSnapshot(out ThreatExposureObservationSnapshot exposure)
            || !exposure.IsAvailable)
        {
            return;
        }
        ThreatGroupExposureObservation? group = exposure.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, snapshot.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, snapshot.Threat.EnemyId, StringComparison.OrdinalIgnoreCase)
            && item.ContactExpiresAtUtc > now);
        if (group == null)
        {
            return;
        }
        position = group.RuntimeThreatPosition;
        lookDirection = group.RuntimeThreatLookDirection;
        source = "threat_exposure_runtime_pose";
    }

    private static string OwnerThreatKey(string? ownerProfileId, string? threatId)
        => Normalize(ownerProfileId) + "|" + NormalizeThreat(threatId);

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    private static string NormalizeThreat(string? value)
    {
        string token = Normalize(value);
        return token is "none" or "null" or "unknown" ? string.Empty : token;
    }
    private static string NormalizeRole(string? value) => Normalize(value).Replace("_", string.Empty).Replace(" ", string.Empty);
    private static Vector3 Flatten(Vector3 value) { value.y = 0f; return value; }
    private static float HorizontalDistance(Vector3 a, Vector3 b) => Vector3.Distance(Flatten(a), Flatten(b));
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    private static string VectorText(Vector3 value) => value.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + value.y.ToString("0.0", CultureInfo.InvariantCulture) + "," + value.z.ToString("0.0", CultureInfo.InvariantCulture);
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Tri(bool? value) => value.HasValue ? Bool(value.Value) : "none";

    private sealed class BoundAdvanceHandoff
    {
        public BoundAdvanceHandoff(
            string ownerProfileId,
            string threatId,
            string ownerThreatKey,
            string nextMoverBotProfileId,
            string nextSupportBotProfileId,
            string sourceLeaseId,
            DateTimeOffset preparedAtUtc,
            DateTimeOffset expiresAtUtc)
        {
            OwnerProfileId = ownerProfileId ?? string.Empty;
            ThreatId = threatId ?? string.Empty;
            OwnerThreatKey = ownerThreatKey ?? string.Empty;
            NextMoverBotProfileId = nextMoverBotProfileId ?? string.Empty;
            NextSupportBotProfileId = nextSupportBotProfileId ?? string.Empty;
            SourceLeaseId = sourceLeaseId ?? string.Empty;
            PreparedAtUtc = preparedAtUtc;
            ExpiresAtUtc = expiresAtUtc;
        }

        public string OwnerProfileId { get; }
        public string ThreatId { get; }
        public string OwnerThreatKey { get; }
        public string NextMoverBotProfileId { get; }
        public string NextSupportBotProfileId { get; }
        public string SourceLeaseId { get; }
        public DateTimeOffset PreparedAtUtc { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
    }

    private sealed class ActiveOpportunityLease
    {
        public ActiveOpportunityLease(
            string ownerProfileId,
            string threatId,
            string ownerThreatKey,
            string operatorId,
            string botProfileId,
            string kind,
            string leaseId,
            string continuityKey,
            string schedulerWindowId,
            long generation,
            Vector3 destination,
            NavMeshPath preparedPath,
            string opportunitySummary,
            string pathSummary,
            string supportBotProfileId,
            string supportContextId,
            DateTimeOffset startedAtUtc,
            DateTimeOffset maxUntilUtc,
            DateTimeOffset noProgressUntilUtc,
            Vector3 lastProgressPosition,
            float pathLength)
        {
            OwnerProfileId = ownerProfileId;
            ThreatId = threatId;
            OwnerThreatKey = ownerThreatKey;
            OperatorId = operatorId;
            BotProfileId = botProfileId;
            Kind = kind;
            LeaseId = leaseId;
            ContinuityKey = continuityKey ?? string.Empty;
            SchedulerWindowId = schedulerWindowId;
            Generation = generation;
            Destination = destination;
            PreparedPath = preparedPath;
            OpportunitySummary = opportunitySummary;
            PathSummary = pathSummary;
            SupportBotProfileId = supportBotProfileId ?? string.Empty;
            SupportContextId = supportContextId ?? string.Empty;
            StartedAtUtc = startedAtUtc;
            MaxUntilUtc = maxUntilUtc;
            NoProgressUntilUtc = noProgressUntilUtc;
            LastProgressPosition = lastProgressPosition;
            PathLength = pathLength;
        }

        public string OwnerProfileId { get; }
        public string ThreatId { get; }
        public string OwnerThreatKey { get; }
        public string OperatorId { get; }
        public string BotProfileId { get; }
        public string Kind { get; }
        public string LeaseId { get; }
        public string ContinuityKey { get; }
        public string SchedulerWindowId { get; }
        public long Generation { get; }
        public Vector3 Destination { get; }
        public NavMeshPath PreparedPath { get; }
        public string OpportunitySummary { get; }
        public string PathSummary { get; }
        public string SupportBotProfileId { get; }
        public string SupportContextId { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public DateTimeOffset MaxUntilUtc { get; }
        public DateTimeOffset NoProgressUntilUtc { get; set; }
        public Vector3 LastProgressPosition { get; set; }
        public float PathLength { get; }
        public int NativePathTicks { get; set; }
    }

    private sealed class OverwatchCandidate
    {
        public OverwatchCandidate(
            OperatorDecisionSnapshot snapshot,
            VanguardRaidOperatorRuntimeRecord runtime,
            int equipmentRank,
            int roleRank,
            string equipmentSummary)
        {
            Snapshot = snapshot;
            Runtime = runtime;
            EquipmentRank = equipmentRank;
            RoleRank = roleRank;
            EquipmentSummary = equipmentSummary;
        }
        public OperatorDecisionSnapshot Snapshot { get; }
        public VanguardRaidOperatorRuntimeRecord Runtime { get; }
        public int EquipmentRank { get; }
        public int RoleRank { get; }
        public string EquipmentSummary { get; }
    }
}

// Responsibility: Resolves the bounded PLAYER_SCOPED Tactical Coordination distance policy for one Operator owner.
// Flow: OwnerProfileId -> owner-keyed runtime settings cache/local exact-owner fallback -> normalized immutable tuning snapshot.
// Authority boundary: Tuning changes distance preferences only. This policy owns no target, aim, fire, movement command, lease or SAIN authority.
// Invariant: Cross-threshold relationships are defensively normalized even if transport data is stale or malformed.
internal static class VanguardTacticalCoordinationTuningPolicy
{
    public const float DefaultDangerCloseWeaponMeters = 15.0f;
    public const float DefaultProactiveWeaponMeters = 25.0f;
    public const float DefaultOverwatchMinimumThreatMeters = 35.0f;
    public const float DefaultOverwatchStandoffMeters = 32.0f;
    public const float DefaultBuddyIsolationMeters = 26.0f;
    public const float DefaultBuddyHelperMaxMeters = 42.0f;
    public const float DefaultFlankLeadCloseMeters = 20.0f;
    public const float DefaultFlankLeadFarMeters = 60.0f;
    public const float DefaultBoundAdvanceDistanceMeters = 7.0f;

    public static OwnerTacticalCoordinationTuning ForOwner(string? ownerProfileId)
    {
        VanguardOperatorRuntimeAuditSettingsDto settings =
            VanguardRuntimeSettingsAuthorityResolver.ResolvePlayerScoped(ownerProfileId, "tactical_coordination_tuning");

        float dangerClose = Clamp(settings.TacticalCoordinationDangerCloseWeaponMeters, 5.0f, 20.0f, DefaultDangerCloseWeaponMeters);
        float proactive = Math.Max(
            Clamp(settings.TacticalCoordinationProactiveWeaponMeters, 10.0f, 35.0f, DefaultProactiveWeaponMeters),
            dangerClose + 2.0f);
        float overwatchMinimumThreat = Math.Max(
            Clamp(settings.TacticalCoordinationOverwatchMinimumThreatMeters, 25.0f, 80.0f, DefaultOverwatchMinimumThreatMeters),
            proactive + 2.0f);
        float overwatchStandoff = Math.Min(
            Clamp(settings.TacticalCoordinationOverwatchStandoffMeters, 20.0f, 80.0f, DefaultOverwatchStandoffMeters),
            Math.Max(20.0f, overwatchMinimumThreat - 2.0f));
        float buddyIsolation = Clamp(settings.TacticalCoordinationBuddyIsolationMeters, 15.0f, 45.0f, DefaultBuddyIsolationMeters);
        float buddyHelperMax = Math.Max(
            Clamp(settings.TacticalCoordinationBuddyHelperMaxMeters, 20.0f, 70.0f, DefaultBuddyHelperMaxMeters),
            buddyIsolation + 4.0f);
        float flankLeadClose = Clamp(settings.TacticalCoordinationFlankLeadCloseMeters, 10.0f, 35.0f, DefaultFlankLeadCloseMeters);
        float flankLeadFar = Math.Max(
            Clamp(settings.TacticalCoordinationFlankLeadFarMeters, 35.0f, 100.0f, DefaultFlankLeadFarMeters),
            flankLeadClose + 15.0f);
        float boundAdvanceDistance = Clamp(
            settings.TacticalCoordinationBoundAdvanceDistanceMeters,
            5.0f,
            20.0f,
            DefaultBoundAdvanceDistanceMeters);

        return new OwnerTacticalCoordinationTuning(
            dangerClose,
            proactive,
            overwatchMinimumThreat,
            overwatchStandoff,
            buddyIsolation,
            buddyHelperMax,
            flankLeadClose,
            flankLeadFar,
            boundAdvanceDistance);
    }

    private static float Clamp(float value, float min, float max, float fallback)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            value = fallback;
        }

        return Math.Max(min, Math.Min(max, value));
    }

    internal readonly struct OwnerTacticalCoordinationTuning
    {
        public OwnerTacticalCoordinationTuning(
            float dangerCloseWeaponMeters,
            float proactiveWeaponMeters,
            float overwatchMinimumThreatMeters,
            float overwatchStandoffMeters,
            float buddyIsolationMeters,
            float buddyHelperMaxMeters,
            float flankLeadCloseMeters,
            float flankLeadFarMeters,
            float boundAdvanceDistanceMeters)
        {
            DangerCloseWeaponMeters = dangerCloseWeaponMeters;
            ProactiveWeaponMeters = proactiveWeaponMeters;
            OverwatchMinimumThreatMeters = overwatchMinimumThreatMeters;
            OverwatchStandoffMeters = overwatchStandoffMeters;
            BuddyIsolationMeters = buddyIsolationMeters;
            BuddyHelperMaxMeters = buddyHelperMaxMeters;
            FlankLeadCloseMeters = flankLeadCloseMeters;
            FlankLeadFarMeters = flankLeadFarMeters;
            BoundAdvanceDistanceMeters = boundAdvanceDistanceMeters;
        }

        public float DangerCloseWeaponMeters { get; }
        public float ProactiveWeaponMeters { get; }
        public float OverwatchMinimumThreatMeters { get; }
        public float OverwatchStandoffMeters { get; }
        public float BuddyIsolationMeters { get; }
        public float BuddyHelperMaxMeters { get; }
        public float FlankLeadCloseMeters { get; }
        public float FlankLeadFarMeters { get; }
        public float BoundAdvanceDistanceMeters { get; }
    }
}

#endif
