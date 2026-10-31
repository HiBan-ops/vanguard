#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Movement.Brain;
using Vanguard.Client.Runtime.Medical.Support;

// Responsibility: Provides the one low-level SAIN path-translation arbitration point shared by Tactical Coordinator movement and short stationary contributions.
// Flow: The BotPathDataManual.TickPath prefix resolves the live SAIN bot and dispatches to the exact owning TC lease. AltDevelop binds a fresh TC destination into the native SAIN path; bounded stationary contributions suppress translation only after revalidation.
// Authority boundary: The bridge never chooses roles, routes or holds. SAIN retains target, perception, aim, fire, suppression and steering. Search/GroupSearch remains the shared baseline; AltDevelop and the bounded Tactical Opportunity sidecar may additionally borrow translation during qualified SeekCover/StandAndShoot combat.
// Invariant: One physical translation owner exists per tick. A behavior with no exact current lease cannot suppress/cancel SAIN movement, and fail-open always returns translation to SAIN.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class SearchTranslationGateBridge
{
    public const string StatusTag = "VANGUARD_TC_SEARCH_TRANSLATION_GATE_STATUS";

    private const int MaxMethodCacheEntries = 32;
    private static readonly TimeSpan AltDevelopGoalEnemyTransientGrace = TimeSpan.FromSeconds(1.0d);
    private static readonly object MethodCacheSync = new();
    private static readonly object PathBindingSync = new();
    private static readonly Dictionary<string, MethodInfo> MethodCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, TacticalMovementPathBindingState> TacticalMovementPathBindings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Type[] BoolParameterTypes = { typeof(bool) };
    private static readonly object[] FalseBoolArguments = { false };
    private static readonly object[] TrueBoolArguments = { true };
    private static bool patchReady;
    private static string patchState = "not_configured";

    public static void ConfigurePatch(bool ready, string state)
    {
        patchReady = ready;
        patchState = string.IsNullOrWhiteSpace(state) ? (ready ? "ready" : "unavailable") : state.Trim();
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"TC_SEARCH_TRANSLATION_GATE_PATCH ready={ready}; state={Safe(patchState)}; target=SAIN.SAINComponent.Classes.Mover.BotPathDataManual.TickPath; translationOwner=single; searchGroupSearchCompatible=true; opportunityCombatActions=AltDevelop|TacticalOpportunity:SeekCoverAction|StandAndShootAction; closeVisibleThreatVetoMeters=15; altDevelopGoalEnemyTransientGraceSeconds={AltDevelopGoalEnemyTransientGrace.TotalSeconds:0.00}; sainSteeringPreserved=true; failOpen=true");
    }

    public static bool IsPatchReady(out string state)
    {
        state = patchState;
        return patchReady;
    }

    internal static void ResetForRaidLifecycle(string reason)
    {
        int clearedBindings;
        lock (PathBindingSync)
        {
            clearedBindings = TacticalMovementPathBindings.Count;
            TacticalMovementPathBindings.Clear();
        }

        // Active AltDevelop/DirectCommit executors release their exact SAIN path bindings before
        // this lifecycle reset is called. Any entry left here is therefore only orphaned bridge
        // bookkeeping and must not survive into the next raid. Patch discovery and reflection
        // method caches are process-scoped and deliberately preserved.
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"TC_SEARCH_TRANSLATION_GATE_RESET reason={Safe(reason)}; bindingsCleared={clearedBindings}; patchConfigurationPreserved=true; reflectionCachePreserved=true; pathMutation=false; staleResume=false");
    }

    public static bool TrySuppressAny(object pathData, DateTimeOffset now, out string reason)
    {
        reason = "no_tc_search_translation_gate";
        if (pathData == null)
        {
            return false;
        }

        if (!TryResolveLiveBot(pathData, out object? sainBot, out BotOwner? botOwner, out string resolveReason))
        {
            reason = resolveReason;
            return false;
        }

        var altDevelopResult = AltDevelopMovementService.TryHandleSearchTranslationGate(
            pathData,
            sainBot!,
            botOwner!,
            now,
            out string altDevelopReason);
        if (altDevelopResult == SearchTranslationDispatchResult.Suppressed)
        {
            reason = "alt_develop:" + altDevelopReason;
            return true;
        }
        if (altDevelopResult == SearchTranslationDispatchResult.ReleasedCurrentTick)
        {
            reason = "alt_develop_released_current_tick:" + altDevelopReason;
            return true;
        }
        if (altDevelopResult == SearchTranslationDispatchResult.PassThroughOwned)
        {
            // AltDevelop owns the SAIN path destination for this exact lease. Let SAIN execute its
            // native BotPathDataManual tick so door handling, movement direction and steering stay
            // inside SAIN rather than trying to revive the vanilla combat mover.
            reason = "alt_develop_native_sain_path:" + altDevelopReason;
            return false;
        }
        if (altDevelopResult == SearchTranslationDispatchResult.HandledFailOpen)
        {
            reason = "alt_develop_fail_open:" + altDevelopReason;
            return false;
        }

        var directCommitResult = DirectCommitMovementService.TryHandleSearchTranslationGate(
            pathData,
            sainBot!,
            botOwner!,
            now,
            out string directCommitReason);
        if (directCommitResult == SearchTranslationDispatchResult.Suppressed)
        {
            reason = "direct_commit:" + directCommitReason;
            return true;
        }
        if (directCommitResult == SearchTranslationDispatchResult.ReleasedCurrentTick)
        {
            reason = "direct_commit_released_current_tick:" + directCommitReason;
            return true;
        }
        if (directCommitResult == SearchTranslationDispatchResult.PassThroughOwned)
        {
            reason = "direct_commit_native_sain_path:" + directCommitReason;
            return false;
        }
        if (directCommitResult == SearchTranslationDispatchResult.HandledFailOpen)
        {
            reason = "direct_commit_fail_open:" + directCommitReason;
            return false;
        }

        var medicSupportResult = VanguardMedicRendezvousMovementService.TryHandleSearchTranslationGate(
            pathData,
            sainBot!,
            botOwner!,
            now,
            out string medicSupportReason);
        if (medicSupportResult == SearchTranslationDispatchResult.Suppressed)
        {
            reason = "medic_support:" + medicSupportReason;
            return true;
        }
        if (medicSupportResult == SearchTranslationDispatchResult.ReleasedCurrentTick)
        {
            reason = "medic_support_released_current_tick:" + medicSupportReason;
            return true;
        }
        if (medicSupportResult == SearchTranslationDispatchResult.PassThroughOwned)
        {
            reason = "medic_support_native_sain_path:" + medicSupportReason;
            return false;
        }
        if (medicSupportResult == SearchTranslationDispatchResult.HandledFailOpen)
        {
            reason = "medic_support_fail_open:" + medicSupportReason;
            return false;
        }

        var tacticalOpportunityResult = TacticalOpportunityMovementService.TryHandleSearchTranslationGate(
            pathData,
            sainBot!,
            botOwner!,
            now,
            out string tacticalOpportunityReason);
        if (tacticalOpportunityResult == SearchTranslationDispatchResult.Suppressed)
        {
            reason = tacticalOpportunityReason;
            return true;
        }
        if (tacticalOpportunityResult == SearchTranslationDispatchResult.ReleasedCurrentTick)
        {
            reason = tacticalOpportunityReason;
            return false;
        }
        if (tacticalOpportunityResult == SearchTranslationDispatchResult.PassThroughOwned)
        {
            reason = tacticalOpportunityReason;
            return false;
        }
        if (tacticalOpportunityResult == SearchTranslationDispatchResult.HandledFailOpen)
        {
            reason = tacticalOpportunityReason;
            return false;
        }

        var antiCongaResult = AntiCongaService.TryHandleSharedSearchTranslationGate(
            pathData,
            sainBot!,
            botOwner!,
            now,
            out string antiCongaReason);
        if (antiCongaResult == SearchTranslationDispatchResult.Suppressed)
        {
            reason = "anti-conga:" + antiCongaReason;
            return true;
        }
        if (antiCongaResult == SearchTranslationDispatchResult.HandledFailOpen)
        {
            reason = "anti_conga_fail_open:" + antiCongaReason;
            return false;
        }

        var supportResult = ChokeCommitSupportService.TryHandleSharedSearchTranslationGate(
            pathData,
            sainBot!,
            botOwner!,
            now,
            out string supportReason);
        if (supportResult == SearchTranslationDispatchResult.Suppressed)
        {
            reason = "commit support:" + supportReason;
            return true;
        }
        if (supportResult == SearchTranslationDispatchResult.HandledFailOpen)
        {
            reason = "commit_support_fail_open:" + supportReason;
            return false;
        }

        reason = "no_tc_search_translation_gate";
        return false;
    }

    internal static bool TryApplyExactSearchGate(
        object pathData,
        object sainBot,
        BotOwner botOwner,
        DateTimeOffset now,
        string threatId,
        string schedulerWindowId,
        out bool organicSuppressionActive,
        out string reason)
    {
        organicSuppressionActive = false;
        reason = "unknown";

        string pathStateGate = ValidateSainManualPathGateState(pathData, out _);
        if (pathStateGate.Length > 0)
        {
            reason = pathStateGate;
            return false;
        }
        if (!VanguardMainIntentScheduler.IsTacticalCoordinatorCombatMovementSidecarCurrent(
                botOwner.ProfileId,
                now,
                threatId,
                schedulerWindowId,
                out string schedulerReason))
        {
            reason = "scheduler_sidecar_changed:" + Safe(schedulerReason);
            return false;
        }
        if (VanguardExecutionLeaseCoordinator.HasActiveLease(botOwner.ProfileId))
        {
            reason = "execution_lease_became_active";
            return false;
        }
        if (botOwner.WeaponManager?.Reload?.Reloading == true)
        {
            reason = "reload_became_active";
            return false;
        }
        if (!ValidateLiveSearchContext(sainBot, out object? currentAction, out string liveSearchReason))
        {
            reason = liveSearchReason;
            return false;
        }

        object? goalEnemy = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBot, "GoalEnemy");
        if (goalEnemy == null)
        {
            reason = "goal_enemy_missing";
            return false;
        }
        string enemyProfileId = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goalEnemy, "EnemyProfileId", "ProfileId"));
        if (!string.Equals(Normalize(enemyProfileId), Normalize(threatId), StringComparison.OrdinalIgnoreCase))
        {
            reason = "goal_enemy_changed";
            return false;
        }
        // Visibility, line-of-sight, CanShoot and incoming-fire evidence are combat awareness,
        // not translation ownership. SAIN Search/GroupSearch is allowed to aim/fire/suppress while
        // TC holds translation. The concrete conflict is SearchAction switching internally to
        // DogFightMove when its goal becomes visible; FollowSearchParty does not make that switch.
        if (TacticalSearchPolicy.IsLeaderSearchAction(VanguardOperatorRuntimeAuditReflection.TypeName(currentAction))
            && Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goalEnemy, "IsVisible", "Visible")) == true)
        {
            reason = "sain_search_action_local_dogfight_visible_target";
            return false;
        }
        if (botOwner.IsDead)
        {
            reason = "bot_dead";
            return false;
        }

        // Preserve SAIN's path-level WantToSprint. Only the physical sprint request and forward
        // translation for this exact tick are suppressed; native TickPath owns the next tick again.
        if (!TrySetPhysicalSprintRequest(sainBot, false))
        {
            reason = "sprint_bridge_failed";
            return false;
        }

        botOwner.SetTargetMoveSpeed(0f);
        botOwner.Mover?.SetTargetMoveSpeed(0f);

        object? steering = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBot, "Steering");
        if (currentAction == null || steering == null
            || !TryInvokeNoArgStrict(currentAction, "OnSteeringTicked")
            || !TryInvokeNoArgStrict(steering, "TickPlayerSteering"))
        {
            reason = "steering_bridge_failed";
            return false;
        }

        object? suppression = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBot, "Suppression");
        organicSuppressionActive = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(suppression, "SuppressingTarget")) == true;
        reason = "translation_suppressed_sain_steering_preserved";
        return true;
    }

    // A retained maneuver may enter the non-translating Search handoff before the full
    // live translation preflight is applicable. Detect only a concrete live GoalEnemy identity
    // change here so a resumed epoch cannot mutate scheduler/command ownership for a stale threat.
    // Missing GoalEnemy remains transient/fail-open and is handled by the normal handoff/preflight flow.
    internal static bool HasConcreteAltDevelopLiveGoalEnemyMismatch(
        BotOwner botOwner,
        string threatId,
        out string currentEnemyProfileId)
    {
        currentEnemyProfileId = "none";
        if (botOwner == null || botOwner.IsDead || string.IsNullOrWhiteSpace(botOwner.ProfileId))
        {
            return false;
        }

        object? sainBot = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(
            botOwner,
            "SAIN.Components.BotComponent");
        if (sainBot == null)
        {
            return false;
        }

        object? goalEnemy = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBot, "GoalEnemy");
        if (goalEnemy == null)
        {
            return false;
        }

        string enemyProfileId = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goalEnemy, "EnemyProfileId", "ProfileId"));
        if (string.IsNullOrWhiteSpace(enemyProfileId))
        {
            return false;
        }

        currentEnemyProfileId = enemyProfileId;
        return !string.Equals(Normalize(enemyProfileId), Normalize(threatId), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryPreflightAltDevelopLiveTranslation(
        BotOwner botOwner,
        string threatId,
        out string reason,
        out string summary)
        => TryPreflightTacticalMovementLiveTranslation(
            botOwner, threatId, out reason, out summary, allowOpportunityCombatFlank: true);

    internal static bool TryPreflightTacticalMovementLiveTranslation(
        BotOwner botOwner,
        string threatId,
        out string reason,
        out string summary,
        bool allowOpportunityCombatFlank = false,
        bool allowStandAndShootCombatAdvance = false,
        bool allowDistantVisibleSearchAction = false)
    {
        reason = "unknown";
        summary = "none";
        if (botOwner == null || botOwner.IsDead || string.IsNullOrWhiteSpace(botOwner.ProfileId))
        {
            reason = "live_botowner_unavailable";
            return false;
        }

        object? sainBot = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(
            botOwner,
            "SAIN.Components.BotComponent");
        if (sainBot == null)
        {
            reason = "sain_component_missing";
            return false;
        }
        if (!ValidateLiveTranslationContext(sainBot, allowOpportunityCombatFlank, allowStandAndShootCombatAdvance, out object? currentAction, out string liveSearchReason))
        {
            reason = liveSearchReason;
            summary = "action=" + Safe(VanguardOperatorRuntimeAuditReflection.TypeName(currentAction));
            return false;
        }

        object? goalEnemy = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBot, "GoalEnemy");
        if (goalEnemy == null)
        {
            reason = "goal_enemy_missing";
            return false;
        }
        string enemyProfileId = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goalEnemy, "EnemyProfileId", "ProfileId"));
        if (!string.Equals(Normalize(enemyProfileId), Normalize(threatId), StringComparison.OrdinalIgnoreCase))
        {
            reason = "goal_enemy_changed";
            summary = "enemy=" + Safe(enemyProfileId);
            return false;
        }
        if ((allowOpportunityCombatFlank
                && IsOpportunityCombatFlankAction(VanguardOperatorRuntimeAuditReflection.TypeName(currentAction))
                || allowStandAndShootCombatAdvance
                && IsStandAndShootCombatAdvanceAction(VanguardOperatorRuntimeAuditReflection.TypeName(currentAction)))
            && IsVisibleCloseGoalEnemy(goalEnemy, AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters, out float closeDistance))
        {
            reason = allowStandAndShootCombatAdvance && !allowOpportunityCombatFlank
                ? "close_visible_threat_survival_precedes_direct_commit"
                : "close_visible_threat_survival_precedes_flank";
            summary = "action=" + Safe(VanguardOperatorRuntimeAuditReflection.TypeName(currentAction))
                + ";enemy=" + Safe(enemyProfileId)
                + ";distance=" + closeDistance.ToString("0.0", CultureInfo.InvariantCulture);
            return false;
        }
        if (TacticalSearchPolicy.IsLeaderSearchAction(VanguardOperatorRuntimeAuditReflection.TypeName(currentAction))
            && Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goalEnemy, "IsVisible", "Visible")) == true
            && (!allowDistantVisibleSearchAction
                || IsVisibleCloseGoalEnemy(goalEnemy, AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters, out _)))
        {
            reason = "sain_search_action_local_dogfight_visible_target";
            summary = "action=" + Safe(VanguardOperatorRuntimeAuditReflection.TypeName(currentAction)) + ";enemyVisible=true;distantVisibleSearchAllowed=" + Bool(allowDistantVisibleSearchAction);
            return false;
        }

        object? mover = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBot, "Mover");
        object? activePath = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "ActivePath");
        if (activePath == null)
        {
            reason = "sain_search_active_path_missing";
            summary = "action=" + Safe(VanguardOperatorRuntimeAuditReflection.TypeName(currentAction)) + ";path=none";
            return false;
        }
        string pathGate = ValidateAltDevelopSainManualPathGateState(activePath, exactBindingCurrent: false, out string pathSummary);
        if (pathGate.Length > 0)
        {
            reason = pathGate;
            summary = "action=" + Safe(VanguardOperatorRuntimeAuditReflection.TypeName(currentAction)) + ";" + pathSummary;
            return false;
        }

        object? playerComponent = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBot, "PlayerComponent");
        object? characterController = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(playerComponent, "CharacterController");
        if (characterController == null
            || ResolveCachedMethod(characterController.GetType(), "SetWantToSprint", BoolParameterTypes) == null)
        {
            reason = "sprint_bridge_unavailable";
            summary = "action=" + Safe(VanguardOperatorRuntimeAuditReflection.TypeName(currentAction)) + ";" + pathSummary;
            return false;
        }

        reason = "live_translation_ready";
        summary = "action=" + Safe(VanguardOperatorRuntimeAuditReflection.TypeName(currentAction))
            + ";enemy=" + Safe(enemyProfileId)
            + ";" + pathSummary;
        return true;
    }

    internal static bool TryApplyAltDevelopTranslationGate(
        object pathData,
        object sainBot,
        BotOwner botOwner,
        string threatId,
        string leaseId,
        long generation,
        Vector3 destination,
        float destinationReachDistance,
        NavMeshPath preparedPath,
        out string reason)
        => TryApplyTacticalMovementTranslationGate(
            pathData, sainBot, botOwner, threatId, AltDevelopMovementService.RequestKind, leaseId, generation,
            destination, destinationReachDistance, preparedPath, out reason, allowOpportunityCombatFlank: true);

    internal static bool TryApplyTacticalMovementTranslationGate(
        object pathData,
        object sainBot,
        BotOwner botOwner,
        string threatId,
        string requestKind,
        string leaseId,
        long generation,
        Vector3 destination,
        float destinationReachDistance,
        NavMeshPath preparedPath,
        out string reason,
        bool allowOpportunityCombatFlank = false,
        bool allowStandAndShootCombatAdvance = false,
        bool allowDistantVisibleSearchAction = false)
    {
        reason = "unknown";
        bool exactBindingCurrent = IsExactTacticalMovementBinding(pathData, botOwner.ProfileId, requestKind, leaseId, generation);
        string pathStateGate = ValidateAltDevelopSainManualPathGateState(pathData, exactBindingCurrent, out _);
        if (pathStateGate.Length > 0)
        {
            reason = pathStateGate;
            return false;
        }
        if (botOwner.IsDead)
        {
            reason = "bot_dead";
            return false;
        }
        if (!ValidateLiveTranslationContext(sainBot, allowOpportunityCombatFlank, allowStandAndShootCombatAdvance, out object? currentAction, out string liveSearchReason))
        {
            reason = liveSearchReason;
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        object? goalEnemy = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBot, "GoalEnemy");
        if (goalEnemy == null)
        {
            if (!TryUseAltDevelopGoalEnemyTransientGrace(
                    pathData,
                    botOwner.ProfileId,
                    threatId,
                    requestKind,
                    leaseId,
                    generation,
                    now,
                    out double gapSeconds,
                    out bool graceStarted,
                    out string graceReason))
            {
                reason = "goal_enemy_missing";
                return false;
            }

            if (graceStarted)
            {
                VanguardClientDiagnosticsLog.Info(
                    StatusTag,
                    $"TC_ALT_DEVELOP_GOAL_ENEMY_TRANSIENT_GRACE_STARTED bot={Safe(botOwner.ProfileId)}; threat={Safe(threatId)}; lease={Safe(leaseId)}; generation={generation}; gapSeconds={gapSeconds:0.000}; graceSeconds={AltDevelopGoalEnemyTransientGrace.TotalSeconds:0.00}; logicalIntent={Safe(graceReason)}; pathBindingExact=true; targetMutation=false; physicalOwner=SAIN_BotPathDataManual");
            }
        }
        else
        {
            string enemyProfileId = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goalEnemy, "EnemyProfileId", "ProfileId"));
            if (!string.Equals(Normalize(enemyProfileId), Normalize(threatId), StringComparison.OrdinalIgnoreCase))
            {
                reason = "goal_enemy_changed";
                return false;
            }

            if (ObserveConfirmedTacticalMovementGoalEnemy(
                    pathData,
                    botOwner.ProfileId,
                    requestKind,
                    leaseId,
                    generation,
                    now,
                    out double recoveredGapSeconds))
            {
                VanguardClientDiagnosticsLog.Info(
                    StatusTag,
                    $"TC_ALT_DEVELOP_GOAL_ENEMY_TRANSIENT_GRACE_RECOVERED bot={Safe(botOwner.ProfileId)}; threat={Safe(threatId)}; lease={Safe(leaseId)}; generation={generation}; missingGapSeconds={recoveredGapSeconds:0.000}; pathBindingExact=true; targetMutation=false");
            }

            if ((allowOpportunityCombatFlank
                    && IsOpportunityCombatFlankAction(VanguardOperatorRuntimeAuditReflection.TypeName(currentAction))
                    || allowStandAndShootCombatAdvance
                    && IsStandAndShootCombatAdvanceAction(VanguardOperatorRuntimeAuditReflection.TypeName(currentAction)))
                && IsVisibleCloseGoalEnemy(goalEnemy, AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters, out _))
            {
                reason = allowStandAndShootCombatAdvance && !allowOpportunityCombatFlank
                    ? "close_visible_threat_survival_precedes_direct_commit"
                    : "close_visible_threat_survival_precedes_flank";
                return false;
            }

            // SearchAction itself invokes DogFightMove when its goal becomes visible. That is a real
            // local movement request and immediately preempts TC. FollowSearchParty does not make that
            // switch, so TC may keep the route destination while SAIN still aims/fires/suppresses.
            if (TacticalSearchPolicy.IsLeaderSearchAction(VanguardOperatorRuntimeAuditReflection.TypeName(currentAction))
                && Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goalEnemy, "IsVisible", "Visible")) == true
                && (!allowDistantVisibleSearchAction
                    || IsVisibleCloseGoalEnemy(goalEnemy, AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters, out _)))
            {
                reason = "sain_search_action_local_dogfight_visible_target";
                return false;
            }
        }

        if (!TrySetPhysicalSprintRequest(sainBot, false))
        {
            reason = "sprint_bridge_failed";
            return false;
        }

        // SAIN disables the vanilla BotMover fixed-update path while its combat layers are
        // active. Therefore AltDevelop must not drive BotOwner.GoToPoint in combat. Instead, TC owns
        // only the destination/path proof and injects that exact fresh path into the already active
        // SAIN BotPathDataManual object. The original TickPath then remains the sole physical
        // locomotion owner and preserves SAIN door, steering, firing and suppression semantics.
        if (!TryBindTacticalMovementPath(
                pathData,
                sainBot,
                botOwner,
                threatId,
                requestKind,
                leaseId,
                generation,
                destination,
                destinationReachDistance,
                preparedPath,
                out string bindReason))
        {
            reason = "sain_path_bind_failed:" + bindReason;
            return false;
        }

        reason = "sain_native_path_bound_tc_destination_current";
        return true;
    }

    private static bool TryUseAltDevelopGoalEnemyTransientGrace(
        object pathData,
        string botProfileId,
        string threatId,
        string requestKind,
        string leaseId,
        long generation,
        DateTimeOffset now,
        out double gapSeconds,
        out bool graceStarted,
        out string logicalIntentReason)
    {
        gapSeconds = double.MaxValue;
        graceStarted = false;
        logicalIntentReason = "none";
        if (!string.Equals(Normalize(requestKind), Normalize(AltDevelopMovementService.RequestKind), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!TacticalIntentContinuityService.IsCurrentIntentForThreat(
                botProfileId,
                threatId,
                TacticalIntentContinuityService.AltDevelopIntent,
                now,
                out logicalIntentReason))
        {
            return false;
        }

        string key = Normalize(botProfileId);
        string normalizedLease = Normalize(leaseId);
        lock (PathBindingSync)
        {
            if (!TacticalMovementPathBindings.TryGetValue(key, out var binding)
                || !string.Equals(binding.RequestKind, Normalize(requestKind), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(binding.LeaseId, normalizedLease, StringComparison.OrdinalIgnoreCase)
                || binding.Generation != generation
                || !ReferenceEquals(binding.PathData, pathData)
                || !string.Equals(binding.ThreatId, Normalize(threatId), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            TimeSpan gap = now - binding.LastConfirmedGoalEnemyAtUtc;
            gapSeconds = Math.Max(0d, gap.TotalSeconds);
            if (gap < TimeSpan.Zero || gap > AltDevelopGoalEnemyTransientGrace)
            {
                return false;
            }

            graceStarted = !binding.GoalEnemyMissingGraceActive;
            if (graceStarted)
            {
                binding.GoalEnemyMissingGraceActive = true;
                binding.GoalEnemyMissingGraceStartedAtUtc = now;
            }
            return true;
        }
    }

    private static bool ObserveConfirmedTacticalMovementGoalEnemy(
        object pathData,
        string botProfileId,
        string requestKind,
        string leaseId,
        long generation,
        DateTimeOffset now,
        out double recoveredGapSeconds)
    {
        recoveredGapSeconds = 0d;
        string key = Normalize(botProfileId);
        string normalizedLease = Normalize(leaseId);
        lock (PathBindingSync)
        {
            if (!TacticalMovementPathBindings.TryGetValue(key, out var binding)
                || !string.Equals(binding.RequestKind, Normalize(requestKind), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(binding.LeaseId, normalizedLease, StringComparison.OrdinalIgnoreCase)
                || binding.Generation != generation
                || !ReferenceEquals(binding.PathData, pathData))
            {
                return false;
            }

            bool recovered = binding.GoalEnemyMissingGraceActive;
            if (recovered)
            {
                recoveredGapSeconds = Math.Max(0d, (now - binding.GoalEnemyMissingGraceStartedAtUtc).TotalSeconds);
            }
            binding.LastConfirmedGoalEnemyAtUtc = now;
            binding.GoalEnemyMissingGraceActive = false;
            binding.GoalEnemyMissingGraceStartedAtUtc = DateTimeOffset.MinValue;
            return recovered;
        }
    }

    private static bool IsExactTacticalMovementBinding(object pathData, string botProfileId, string requestKind, string leaseId, long generation)
    {
        string key = Normalize(botProfileId);
        string normalizedLease = Normalize(leaseId);
        lock (PathBindingSync)
        {
            return TacticalMovementPathBindings.TryGetValue(key, out var binding)
                && string.Equals(binding.RequestKind, Normalize(requestKind), StringComparison.OrdinalIgnoreCase)
                && string.Equals(binding.LeaseId, normalizedLease, StringComparison.OrdinalIgnoreCase)
                && binding.Generation == generation
                && ReferenceEquals(binding.PathData, pathData);
        }
    }

    private static string ValidateAltDevelopSainManualPathGateState(object activePath, bool exactBindingCurrent, out string summary)
    {
        string typeName = VanguardOperatorRuntimeAuditReflection.TypeName(activePath);
        string status = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "Status"));
        bool pausedRequested = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "PausedRequested")) == true;
        bool cancelRequested = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "CancelRequested")) == true;
        summary = "type=" + Safe(typeName)
            + ":status=" + Safe(status)
            + ":pausedRequested=" + BoolText(pausedRequested)
            + ":cancelRequested=" + BoolText(cancelRequested)
            + ":exactBinding=" + BoolText(exactBindingCurrent);

        if (!IsBotPathDataManual(typeName))
        {
            return "sain_search_path_type_unexpected";
        }
        if (cancelRequested)
        {
            // Cancel is native SAIN state and therefore wins immediately. TC never clears private
            // SAIN cancellation flags; it releases its logical binding and fails open.
            return exactBindingCurrent
                ? "sain_tc_path_cancel_requested"
                : "sain_search_path_cancel_pending";
        }
        if (pausedRequested || string.Equals(status, "Paused", StringComparison.OrdinalIgnoreCase))
        {
            return "sain_search_path_paused_by_sain";
        }
        if (exactBindingCurrent && string.Equals(status, "DoorInteraction", StringComparison.OrdinalIgnoreCase))
        {
            // Door handling is part of SAIN's native physical path executor and must be allowed to
            // finish under the same TC destination lease.
            return string.Empty;
        }
        if (!string.Equals(status, "Moving", StringComparison.OrdinalIgnoreCase))
        {
            return "sain_search_path_not_normal_moving:" + Safe(status);
        }
        return string.Empty;
    }

    internal static bool ReleaseResidualTacticalMovementPathBindingForHigherAuthority(
        string botProfileId,
        string releaseReason,
        out string releasedRequestKind)
    {
        releasedRequestKind = "none";
        string key = Normalize(botProfileId);
        TacticalMovementPathBindingState? binding;
        lock (PathBindingSync)
        {
            TacticalMovementPathBindings.TryGetValue(key, out binding);
        }
        if (binding == null)
        {
            return false;
        }

        releasedRequestKind = binding.RequestKind;
        // The bridge already owns the exact request/lease/generation tuple. This fallback is only
        // reached when an executor-specific drain did not consume the binding (for example after a
        // partial lifecycle failure). ReleaseTacticalMovementPathBinding preserves any newer SAIN
        // ActivePath and therefore cannot clobber the acquiring authority.
        bool activePathRetired = ReleaseTacticalMovementPathBinding(
            key,
            binding.RequestKind,
            binding.LeaseId,
            binding.Generation,
            "higher_authority_residual_binding:" + Safe(releaseReason));
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"TC_RESIDUAL_SAIN_PATH_BINDING_DRAIN bot={Safe(key)}; request={Safe(binding.RequestKind)}; lease={Safe(binding.LeaseId)}; generation={binding.Generation}; activePathRetired={BoolText(activePathRetired)}; reason={Safe(releaseReason)}; exactBindingMetadata=true; newerSainPathPreserved=true; staleResume=false");
        return true;
    }

    internal static bool ReleaseAltDevelopPathBinding(
        string botProfileId,
        string leaseId,
        long generation,
        string releaseReason)
        => ReleaseTacticalMovementPathBinding(botProfileId, AltDevelopMovementService.RequestKind, leaseId, generation, releaseReason);

    internal static bool ReleaseTacticalMovementPathBinding(
        string botProfileId,
        string requestKind,
        string leaseId,
        long generation,
        string releaseReason)
    {
        string key = Normalize(botProfileId);
        TacticalMovementPathBindingState? binding = null;
        lock (PathBindingSync)
        {
            if (TacticalMovementPathBindings.TryGetValue(key, out var current)
                && string.Equals(current.RequestKind, Normalize(requestKind), StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.LeaseId, Normalize(leaseId), StringComparison.OrdinalIgnoreCase)
                && current.Generation == generation)
            {
                binding = current;
                TacticalMovementPathBindings.Remove(key);
            }
        }
        if (binding == null)
        {
            return false;
        }

        // Only terminate the SAIN path if this exact object is still PathFinder.ActivePath. If SAIN
        // has already installed a successor path, disposing the old object would call PathComplete
        // and could clobber that newer authority. In that case the logical TC binding is simply
        // forgotten and the newer SAIN path is left untouched.
        object? pathFinder = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(binding.PathData, "PathFinder");
        object? activePath = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(pathFinder, "ActivePath");
        bool activeAtRelease = ReferenceEquals(activePath, binding.PathData);
        bool disposed = activeAtRelease && TryInvokeNoArgStrict(binding.PathData, "Dispose");

        string releaseEvent = DirectCommitAuthorityPolicy.IsDirectCommitRequest(requestKind)
            ? "TC_DIRECT_COMMIT_SAIN_PATH_RELEASED"
            : "TC_ALT_DEVELOP_SAIN_PATH_RELEASED";
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"{releaseEvent} bot={Safe(key)}; request={Safe(requestKind)}; lease={Safe(leaseId)}; generation={generation}; activeAtRelease={BoolText(activeAtRelease)}; disposed={BoolText(disposed)}; reason={Safe(releaseReason)}; newerSainPathPreserved={BoolText(!activeAtRelease)}; staleTickAllowed=false");
        // Suppress the current Harmony invocation whenever it still points at the retiring active
        // TC path, even if reflective Dispose unexpectedly failed. The next mover tick is then
        // entirely SAIN-owned and may resume/replace its path without one extra stale TC step.
        return activeAtRelease;
    }

    private static bool TryBindTacticalMovementPath(
        object pathData,
        object sainBot,
        BotOwner botOwner,
        string threatId,
        string requestKind,
        string leaseId,
        long generation,
        Vector3 destination,
        float destinationReachDistance,
        NavMeshPath preparedPath,
        out string reason)
    {
        reason = "unknown";
        string key = Normalize(botOwner.ProfileId);
        string normalizedLease = Normalize(leaseId);
        if (string.Equals(key, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedLease, "none", StringComparison.OrdinalIgnoreCase))
        {
            reason = "binding_identity_missing";
            return false;
        }

        TacticalMovementPathBindingState? foreignBinding = null;
        lock (PathBindingSync)
        {
            if (TacticalMovementPathBindings.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing.RequestKind, Normalize(requestKind), StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.LeaseId, normalizedLease, StringComparison.OrdinalIgnoreCase)
                    || existing.Generation != generation
                    || !string.Equals(existing.ThreatId, Normalize(threatId), StringComparison.OrdinalIgnoreCase))
                {
                    foreignBinding = existing;
                }
                else
                {
                    if (!ReferenceEquals(existing.PathData, pathData))
                    {
                        reason = "sain_active_path_identity_changed";
                        return false;
                    }
                    if ((existing.Destination - destination).sqrMagnitude > 0.25f)
                    {
                        reason = "tc_destination_changed_inside_lease";
                        return false;
                    }
                    reason = "binding_current";
                    return true;
                }
            }
        }

        if (foreignBinding != null)
        {
            bool foreignCommandCurrent = VanguardReturnMovementCommandStore.TryGetExactOwned(
                key,
                foreignBinding.LeaseId,
                foreignBinding.RequestKind,
                foreignBinding.Generation,
                DateTimeOffset.UtcNow,
                out _,
                out _);
            if (foreignCommandCurrent)
            {
                reason = "foreign_tc_binding_present";
                return false;
            }

            lock (PathBindingSync)
            {
                if (TacticalMovementPathBindings.TryGetValue(key, out var current)
                    && ReferenceEquals(current, foreignBinding))
                {
                    TacticalMovementPathBindings.Remove(key);
                }
            }
            string staleBindingEvent = DirectCommitAuthorityPolicy.IsDirectCommitRequest(requestKind)
                ? "TC_DIRECT_COMMIT_STALE_SAIN_PATH_BINDING_EVICTED"
                : "TC_ALT_DEVELOP_STALE_SAIN_PATH_BINDING_EVICTED";
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"{staleBindingEvent} bot={Safe(key)}; oldRequest={Safe(foreignBinding.RequestKind)}; oldLease={Safe(foreignBinding.LeaseId)}; oldGeneration={foreignBinding.Generation}; newRequest={Safe(requestKind)}; newLease={Safe(normalizedLease)}; newGeneration={generation}; oldCommandCurrent=false; pathMutation=false");
        }

        if (preparedPath == null
            || preparedPath.status != NavMeshPathStatus.PathComplete
            || preparedPath.corners == null
            || preparedPath.corners.Length < 2)
        {
            reason = "prepared_path_not_complete";
            return false;
        }

        Vector3 navStart = botOwner.Position;
        object? transform = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBot, "Transform");
        object? navData = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(transform, "NavData");
        object? navPosition = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(navData, "Position");
        if (navPosition is Vector3 typedNavPosition)
        {
            navStart = typedNavPosition;
        }

        MethodInfo? initialize = ResolvePathInitializeMethod(pathData.GetType());
        if (initialize == null)
        {
            reason = "initialize_method_missing";
            return false;
        }
        ParameterInfo[] parameters = initialize.GetParameters();
        if (parameters.Length != 6 || !parameters[3].ParameterType.IsEnum)
        {
            reason = "initialize_signature_unexpected";
            return false;
        }

        try
        {
            object noSprintUrgency = Enum.ToObject(parameters[3].ParameterType, 0);
            initialize.Invoke(pathData, new object[]
            {
                navStart,
                destination,
                false,
                noSprintUrgency,
                preparedPath.corners,
                preparedPath
            });

            MethodInfo? setReach = ResolveCachedMethod(pathData.GetType(), "SetDestinationReachDistance", new[] { typeof(float) });
            if (setReach == null)
            {
                reason = "destination_reach_method_missing";
                return false;
            }
            setReach.Invoke(pathData, new object[] { Math.Max(0.35f, destinationReachDistance) });

            if (!TryInvokeNoArgStrict(pathData, "Start"))
            {
                reason = "path_start_failed";
                return false;
            }
        }
        catch (Exception exception)
        {
            reason = "bind_exception:" + exception.GetType().Name;
            return false;
        }

        lock (PathBindingSync)
        {
            TacticalMovementPathBindings[key] = new TacticalMovementPathBindingState(
                Normalize(requestKind),
                normalizedLease,
                generation,
                Normalize(threatId),
                pathData,
                destination);
        }
        string boundEvent = DirectCommitAuthorityPolicy.IsDirectCommitRequest(requestKind)
            ? "TC_DIRECT_COMMIT_SAIN_PATH_BOUND"
            : "TC_ALT_DEVELOP_SAIN_PATH_BOUND";
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"{boundEvent} bot={Safe(key)}; request={Safe(requestKind)}; lease={Safe(normalizedLease)}; generation={generation}; destination=({destination.x:0.00},{destination.y:0.00},{destination.z:0.00}); reach={destinationReachDistance:0.00}; corners={preparedPath.corners.Length}; pathStatus={preparedPath.status}; backend=SAIN_BotPathDataManual; vanillaGoToPoint=false");
        reason = "binding_created";
        return true;
    }

    private static MethodInfo? ResolvePathInitializeMethod(Type type)
    {
        string key = (type.AssemblyQualifiedName ?? type.FullName ?? type.Name) + "|Initialize|alt_develop_path_shape";
        lock (MethodCacheSync)
        {
            if (MethodCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        MethodInfo? resolved = null;
        foreach (MethodInfo candidate in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (!string.Equals(candidate.Name, "Initialize", StringComparison.Ordinal))
            {
                continue;
            }
            ParameterInfo[] parameters = candidate.GetParameters();
            if (parameters.Length == 6
                && parameters[0].ParameterType == typeof(Vector3)
                && parameters[1].ParameterType == typeof(Vector3)
                && parameters[2].ParameterType == typeof(bool)
                && parameters[3].ParameterType.IsEnum
                && parameters[4].ParameterType == typeof(Vector3[])
                && parameters[5].ParameterType == typeof(NavMeshPath))
            {
                resolved = candidate;
                break;
            }
        }
        if (resolved == null)
        {
            return null;
        }

        lock (MethodCacheSync)
        {
            if (MethodCache.Count >= MaxMethodCacheEntries)
            {
                MethodCache.Clear();
            }
            MethodCache[key] = resolved;
        }
        return resolved;
    }

    private static bool ValidateLiveSearchContext(object sainBot, out object? currentAction, out string reason)
        => ValidateLiveTranslationContext(sainBot, allowOpportunityCombatFlank: false, allowStandAndShootCombatAdvance: false, out currentAction, out reason);

    private static bool ValidateLiveTranslationContext(
        object sainBot,
        bool allowOpportunityCombatFlank,
        bool allowStandAndShootCombatAdvance,
        out object? currentAction,
        out string reason)
    {
        currentAction = null;
        if (Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBot, "BotActive")) != true)
        {
            reason = "sain_bot_not_active";
            return false;
        }
        if (Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBot, "SAINLayersActive")) != true)
        {
            reason = "sain_layers_not_active";
            return false;
        }

        currentAction = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBot, "CurrentAction");
        string currentActionName = VanguardOperatorRuntimeAuditReflection.TypeName(currentAction);
        object? decision = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBot, "Decision");
        string selfDecision = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "CurrentSelfDecision"));
        if (!TacticalSearchPolicy.IsNoneToken(selfDecision))
        {
            reason = "self_decision_changed:" + Safe(selfDecision);
            return false;
        }
        if (Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "RunningToCover")) == true)
        {
            reason = "sain_running_to_cover";
            return false;
        }

        if (TacticalSearchPolicy.IsSearchAction(currentActionName))
        {
            string combatDecision = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "CurrentCombatDecision"));
            string squadDecision = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "CurrentSquadDecision"));
            if (!TacticalSearchPolicy.IsSearchCombatDecision(combatDecision))
            {
                reason = "combat_decision_changed:" + Safe(combatDecision);
                return false;
            }
            if (!TacticalSearchPolicy.IsSearchSquadDecision(squadDecision))
            {
                reason = "squad_decision_changed:" + Safe(squadDecision);
                return false;
            }
            if (Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "IsSearching")) != true)
            {
                reason = "search_state_changed";
                return false;
            }

            reason = "search_context_current";
            return true;
        }

        if (allowStandAndShootCombatAdvance && IsStandAndShootCombatAdvanceAction(currentActionName))
        {
            reason = "stand_and_shoot_combat_advance_context_current:" + Safe(currentActionName);
            return true;
        }

        if (allowOpportunityCombatFlank && IsOpportunityCombatFlankAction(currentActionName))
        {
            reason = "opportunity_combat_flank_context_current:" + Safe(currentActionName);
            return true;
        }

        reason = "action_not_search:" + Safe(currentActionName);
        return false;
    }

    private static bool IsOpportunityCombatFlankAction(string? currentActionName)
    {
        string token = TacticalSearchPolicy.NormalizeToken(currentActionName);
        return string.Equals(token, "SeekCoverAction", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "StandAndShootAction", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStandAndShootCombatAdvanceAction(string? currentActionName)
        => string.Equals(
            TacticalSearchPolicy.NormalizeToken(currentActionName),
            "StandAndShootAction",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsVisibleCloseGoalEnemy(object goalEnemy, float maxDistanceMeters, out float distance)
    {
        distance = -1f;
        if (Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goalEnemy, "IsVisible", "Visible")) != true)
        {
            return false;
        }

        object? rawDistance = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goalEnemy, "RealDistance", "Distance");
        if (rawDistance == null)
        {
            return false;
        }
        try
        {
            distance = Convert.ToSingle(rawDistance, CultureInfo.InvariantCulture);
        }
        catch
        {
            return false;
        }

        return !float.IsNaN(distance)
            && !float.IsInfinity(distance)
            && distance >= 0f
            && distance <= maxDistanceMeters;
    }

    internal static string ValidateSainManualPathGateState(object activePath, out string summary)
    {
        string typeName = VanguardOperatorRuntimeAuditReflection.TypeName(activePath);
        string status = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "Status"));
        bool pausedRequested = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "PausedRequested")) == true;
        bool cancelRequested = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "CancelRequested")) == true;
        bool running = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "Running")) == true;
        summary = "type=" + Safe(typeName)
            + ":status=" + Safe(status)
            + ":pausedRequested=" + BoolText(pausedRequested)
            + ":cancelRequested=" + BoolText(cancelRequested)
            + ":running=" + BoolText(running);

        if (!IsBotPathDataManual(typeName))
        {
            return "sain_search_path_type_unexpected";
        }
        if (cancelRequested)
        {
            return "sain_search_path_cancel_pending";
        }
        if (pausedRequested || string.Equals(status, "Paused", StringComparison.OrdinalIgnoreCase))
        {
            return "sain_search_path_paused_by_sain";
        }
        if (string.Equals(status, "DoorInteraction", StringComparison.OrdinalIgnoreCase))
        {
            return "sain_search_path_door_interaction";
        }
        if (!string.Equals(status, "Moving", StringComparison.OrdinalIgnoreCase))
        {
            return "sain_search_path_not_normal_moving:" + Safe(status);
        }
        return string.Empty;
    }

    private static bool TryResolveLiveBot(object pathData, out object? sainBot, out BotOwner? botOwner, out string reason)
    {
        sainBot = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(pathData, "Bot");
        botOwner = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBot, "BotOwner") as BotOwner;
        if (sainBot == null || botOwner == null || botOwner.IsDead || string.IsNullOrWhiteSpace(botOwner.ProfileId))
        {
            reason = "live_botowner_unavailable";
            return false;
        }
        reason = "ok";
        return true;
    }

    private static bool TrySetPhysicalSprintRequest(object sainBot, bool value)
    {
        try
        {
            object? playerComponent = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBot, "PlayerComponent");
            object? characterController = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(playerComponent, "CharacterController");
            if (characterController == null)
            {
                return false;
            }

            MethodInfo? method = ResolveCachedMethod(characterController.GetType(), "SetWantToSprint", BoolParameterTypes);
            if (method == null)
            {
                return false;
            }
            method.Invoke(characterController, value ? TrueBoolArguments : FalseBoolArguments);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryInvokeNoArgStrict(object target, string methodName)
    {
        try
        {
            MethodInfo? method = ResolveCachedMethod(target.GetType(), methodName, Type.EmptyTypes);
            if (method == null)
            {
                return false;
            }
            method.Invoke(target, Array.Empty<object>());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static MethodInfo? ResolveCachedMethod(Type type, string methodName, Type[] parameterTypes)
    {
        string parameterKey = parameterTypes.Length == 0
            ? "none"
            : string.Join(",", Array.ConvertAll(parameterTypes, item => item.AssemblyQualifiedName ?? item.FullName ?? item.Name));
        string key = (type.AssemblyQualifiedName ?? type.FullName ?? type.Name)
            + "|" + methodName
            + "|" + parameterKey;
        lock (MethodCacheSync)
        {
            if (MethodCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        MethodInfo? resolved = type.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: parameterTypes,
            modifiers: null);
        if (resolved == null)
        {
            return null;
        }

        lock (MethodCacheSync)
        {
            if (MethodCache.Count >= MaxMethodCacheEntries)
            {
                MethodCache.Clear();
            }
            MethodCache[key] = resolved;
        }
        return resolved;
    }

    private static bool? Bool(object? value)
    {
        if (value is bool typed)
        {
            return typed;
        }
        return bool.TryParse(value?.ToString(), out bool parsed) ? parsed : null;
    }

    private static string Text(object? value) => value?.ToString()?.Trim() ?? "none";
    private static bool IsBotPathDataManual(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        string trimmed = value.Trim();
        int separator = Math.Max(trimmed.LastIndexOf('.'), trimmed.LastIndexOf('+'));
        string token = separator >= 0 && separator + 1 < trimmed.Length ? trimmed[(separator + 1)..] : trimmed;
        return string.Equals(token, "BotPathDataManual", StringComparison.Ordinal);
    }
    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    private static string BoolText(bool value) => value ? "true" : "false";

    private sealed class TacticalMovementPathBindingState
    {
        public TacticalMovementPathBindingState(string requestKind, string leaseId, long generation, string threatId, object pathData, Vector3 destination)
        {
            RequestKind = Normalize(requestKind);
            LeaseId = leaseId;
            Generation = generation;
            ThreatId = threatId;
            PathData = pathData;
            Destination = destination;
            LastConfirmedGoalEnemyAtUtc = DateTimeOffset.UtcNow;
            GoalEnemyMissingGraceStartedAtUtc = DateTimeOffset.MinValue;
        }

        public string RequestKind { get; }
        public string LeaseId { get; }
        public long Generation { get; }
        public string ThreatId { get; }
        public object PathData { get; }
        public Vector3 Destination { get; }
        public DateTimeOffset LastConfirmedGoalEnemyAtUtc { get; set; }
        public bool GoalEnemyMissingGraceActive { get; set; }
        public DateTimeOffset GoalEnemyMissingGraceStartedAtUtc { get; set; }
    }
}

internal enum SearchTranslationDispatchResult
{
    NoLease = 0,
    Suppressed = 1,
    HandledFailOpen = 2,
    PassThroughOwned = 3,
    ReleasedCurrentTick = 4,
}
#endif
