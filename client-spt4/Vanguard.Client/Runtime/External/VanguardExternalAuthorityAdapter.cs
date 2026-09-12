#if SPT_CLIENT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using EFT;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Grenades;
using Vanguard.Client.Runtime.Integrations.Orbit;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.Movement.Brain;

// Responsibility: mediates Vanguard movement/medical ownership against external executors such as SAIN, Looting Bots and Orbit without replacing their unrelated behavior.
// Flow: When Vanguard needs temporary movement or medical control, this adapter pauses only the conflicting external behavior, records what was changed, and restores it as soon as the Vanguard lease ends.
// Authority boundary: suppression is narrow, explicit and lease-like; SAIN keeps combat authority while Vanguard may temporarily own qualified medical/return movement.
// Invariant: every external suppression path has a deterministic release/reset path so no third-party AI layer remains disabled after Vanguard authority ends.

namespace Vanguard.Client.Runtime.External;

internal static class VanguardExternalAuthorityAdapter
{
    public const string StatusTag = "VANGUARD_MEDICAL_ORBIT_HARD_PREEMPT_OK";
    public const string MovementPreemptStatusTag = "VANGUARD_MEDICAL_ORBIT_MOVEMENT_PREEMPT_OK";
    public const string CombatAwareGateStatusTag = "VANGUARD_MEDICAL_COMBAT_AWARE_COVER_GATE_OK";
    public const string TypedCoverFailureStatusTag = "VANGUARD_MEDICAL_COVER_FAILURE_TYPING_OK";
    public const string OrbitLayerQuiesceStatusTag = "VANGUARD_MEDICAL_ORBIT_LAYER_QUIESCE_OK";
    public const string CoverArrivalGrantStatusTag = "VANGUARD_MEDICAL_COVER_ARRIVAL_GRANT_OK";
    public const string MedicalAuthorityHoldStatusTag = "VANGUARD_MEDICAL_AUTHORITY_HOLD_OK";
    public const string MedicalCoverCommitStatusTag = "VANGUARD_MEDICAL_COVER_COMMIT_OK";
    public const string MedicalCoverCommitUnificationStatusTag = "VANGUARD_MEDICAL_COVER_COMMIT_UNIFICATION_OK";
    public const string MedicalCoverMovementStabilizationStatusTag = "VANGUARD_MEDICAL_COVER_MOVEMENT_STABILIZATION_OK";
    public const string MedicalHardProcedureAuthorityStatusTag = "VANGUARD_MEDICAL_HARD_PROCEDURE_AUTHORITY_OK";
    public const string MedicalProcedureCompletionGateStatusTag = "VANGUARD_MEDICAL_PROCEDURE_COMPLETION_GATE_OK";
    public const string MedicalSurgeryDirectChainStatusTag = "VANGUARD_MEDICAL_SURGERY_DIRECT_CHAIN_OK";
    public const string MedicalSurgerySameLeaseStartStatusTag = "VANGUARD_MEDICAL_SURGERY_SAME_PROCEDURE_START_OK";
    public const string MedicalValidSurgeryTargetsStatusTag = "VANGUARD_MEDICAL_VALID_SURGERY_TARGETS_OK";
    public const string MedicalCriticalTriageFastSurgeryStatusTag = "VANGUARD_MEDICAL_CRITICAL_TRIAGE_FAST_SURGERY_OK";
    public const string MedicalSurgeryHardHoldStatusTag = "VANGUARD_MEDICAL_SURGERY_HARD_HOLD_OK";
    public const string MedicalOrbitLootFreezeDuringSurgeryStatusTag = "VANGUARD_MEDICAL_ORBIT_LOOT_FREEZE_DURING_SURGERY_OK";
    public const string MovementHardReturnSuppressStatusTag = "VANGUARD_EXTERNAL_SUPPRESS_HARD_RETURN_OK";
    public const string SainSearchSuppressStatusTag = "VANGUARD_SAIN_SEARCH_SUPPRESS_OK";
    public const string ReturnContinuationStatusTag = "VANGUARD_RETURN_CONTINUATION_OK";
    public const string CleanAuthStatusTag = "VANGUARD_CLEAN_AUTH_OK";
    public const string OrbitAuthorityQuiesceStatusTag = "VANGUARD_ORBIT_AUTHORITY_QUIESCE_STATUS";
    public const string CombatHoldMedicalCatchupStatusTag = "VANGUARD_COMBAT_HOLD_MEDICAL_CATCHUP_STATUS";
    public const string HostileIndoorMovementPlanStatusTag = "VANGUARD_HOSTILE_INDOOR_MOVEMENT_PLAN_STATUS";
    public const string CombatBindCohesionRecoveryStatusTag = "VANGUARD_COMBAT_BIND_COHESION_RECOVERY_STATUS";
    public const string HardReturnAlertStatusTag = "VANGUARD_HARD_RETURN_ALERT_STATUS";

    private static readonly TimeSpan SnapshotLogInterval = TimeSpan.FromSeconds(1.50d);
    private static readonly TimeSpan PreemptLogInterval = TimeSpan.FromSeconds(0.75d);
    private static readonly TimeSpan HardProcedureRefreshLogInterval = TimeSpan.FromSeconds(5.00d);
    private static readonly TimeSpan MedicalPreemptMutationInterval = TimeSpan.FromMilliseconds(500.00d);
    private static readonly TimeSpan MedicalPreemptSnapshotFastPathInterval = TimeSpan.FromMilliseconds(350.00d);
    private static readonly Dictionary<string, DateTimeOffset> LastLogAtByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ExternalSuppressionState> SuppressedByBotProfileId = new(StringComparer.OrdinalIgnoreCase);

    public static void Reset(string reason)
    {
        LastLogAtByKey.Clear();
        SuppressedByBotProfileId.Clear();
        VanguardOrbitAuthorityBoundaryService.ResetForRaidLifecycle(reason);
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_EXTERNAL_AUTHORITY_RESET reason={Safe(reason)}; patientOnly=true; owns=Orbit_LootingBots_EFTPath; doesNotOwn=SAINCombat;vetoes=SAINAutonomousExtract; usesLootingBotsExternal=true; orbitAgentPreempt=reflection_guarded; release=explicit; combatAwareGate=true; orbitLayerIdleQuiesce=true; coverArrivalGrant=true; medicalAuthorityHold=true; coverCommit=true; hardProcedureAuthority=true; completionGate=true; directSurgeryChain=true; sameProcedureStart=true; validSurgeryTargets=true; criticalFastSurgery=true; surgeryHardHold=true; orbitLootFreezeDuringSurgery=true; releaseOnlyOnHealedOrLongTimeout=true; returnContinuation=true; cleanAuth=true; orbitNonDriveHardReturnClear=true; residualCombatMedicalOverride=removed_general_sain_stale_exit; generalSainStaleExit=true; PathAlertRecovery=true; tag={StatusTag}; Tag={CombatHoldMedicalCatchupStatusTag}; continuationTag={ReturnContinuationStatusTag}; cleanAuthTag={CleanAuthStatusTag}; movementTag={MovementPreemptStatusTag}; combatGateTag={CombatAwareGateStatusTag}; typedFailureTag={TypedCoverFailureStatusTag}; orbitLayerTag={OrbitLayerQuiesceStatusTag}; coverArrivalTag={CoverArrivalGrantStatusTag}; authorityHoldTag={MedicalAuthorityHoldStatusTag}; coverCommitTag={MedicalCoverCommitStatusTag}; commitUnificationTag={MedicalCoverCommitUnificationStatusTag}; movementStabilizationTag={MedicalCoverMovementStabilizationStatusTag}; hardProcedureTag={MedicalHardProcedureAuthorityStatusTag}; completionGateTag={MedicalProcedureCompletionGateStatusTag}; directChainTag={MedicalSurgeryDirectChainStatusTag}; sameProcedureStartTag={MedicalSurgerySameLeaseStartStatusTag}; validSurgeryTargetTag={MedicalValidSurgeryTargetsStatusTag}; criticalFastSurgeryTag={MedicalCriticalTriageFastSurgeryStatusTag}; surgeryHardHoldTag={MedicalSurgeryHardHoldStatusTag}; orbitLootFreezeTag={MedicalOrbitLootFreezeDuringSurgeryStatusTag}");
    }

    public static VanguardExternalActivitySnapshot ReadActivity(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, bool log = false, string reason = "snapshot")
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        bool botOwnerPresent = botOwner != null;
        // Runtime invariant: null BotOwner explicitly means immutable snapshot-only classification. Several
        // medical policies intentionally call this read model without an owner; performing type,
        // component and static telemetry reflection in that mode duplicated work already completed
        // by OperatorDecisionSnapshotService and could stall the headless thread for diagnostics.
        bool allowLiveReflection = botOwnerPresent;
        bool lootExternalPresent = snapshot.Looting.TypeLoaded
            || (allowLiveReflection && VanguardOperatorRuntimeAuditReflection.FindType("LootingBots.External") != null);
        object? lootingBrain = allowLiveReflection
            ? VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "LootingBots.Components.LootingBrain")
            : null;
        object? lootFinder = allowLiveReflection
            ? VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "LootingBots.Components.LootFinder")
            : null;
        bool lootComponentPresent = lootingBrain != null || lootFinder != null || snapshot.Looting.ComponentPresent;

        bool lootingActive = snapshot.Looting.BotLooting == true
            || (allowLiveReflection && StringBool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "BotIsLooting", "IsLooting", "IsActive")) == true);
        bool lootTaskRunning = snapshot.Looting.LootTaskRunning == true;
        bool hasActiveLootable = snapshot.Looting.HasActiveLootable == true
            || (allowLiveReflection && StringBool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "HasActiveLoot", "HasActiveLootable", "ActiveLoot")) == true);

        bool orbitBoundaryFastPath = allowLiveReflection
            && VanguardOrbitAuthorityBoundaryService.IsFastPathConfirmed(snapshot.BotProfileId, now, out _);
        bool orbitTelemetryAvailable = snapshot.Orbit.TelemetryLoaded
            || (allowLiveReflection && !orbitBoundaryFastPath && VanguardOperatorRuntimeAuditReflection.FindType("Orbit.Api.OrbitTelemetry") != null);
        bool orbitActive = snapshot.Orbit.Active;
        bool orbitLayerActive = Contains(snapshot.Brain.ActiveLayer, "orbit") || Contains(snapshot.Sain.ActiveLayer, "orbit");
        string orbitStatus = snapshot.Orbit.Status;
        string orbitCategory = snapshot.Orbit.Category;
        string orbitClassification = snapshot.Orbit.Classification;
        string orbitExtractReason = snapshot.Orbit.ExtractReason;
        Vector3? orbitObjective = snapshot.Orbit.Objective;

        if (allowLiveReflection && !orbitBoundaryFastPath
            && TryReadOrbitTelemetry(snapshot.BotProfileId, out var telemetrySummary, out var telemetryStatus, out var telemetryCategory, out var telemetryObjective, out var telemetryExtract))
        {
            orbitTelemetryAvailable = true;
            orbitActive = true;
            if (!string.IsNullOrWhiteSpace(telemetryStatus))
            {
                orbitStatus = telemetryStatus;
            }
            if (!string.IsNullOrWhiteSpace(telemetryCategory))
            {
                orbitCategory = telemetryCategory;
            }
            if (!string.IsNullOrWhiteSpace(telemetryExtract))
            {
                orbitExtractReason = telemetryExtract;
            }
            orbitObjective = telemetryObjective;
            if (string.Equals(orbitClassification, "orbit_unknown", StringComparison.OrdinalIgnoreCase))
            {
                orbitClassification = telemetrySummary;
            }
        }

        float? pathDistance = snapshot.Movement.DistanceToDestination ?? snapshot.Movement.GoToDistance;
        bool pathActive = snapshot.Movement.HasPath == true && pathDistance.HasValue && pathDistance.Value > 1.00f;
        float speed = Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed);
        bool moverMoving = speed > 0.35f;
        bool orbitSemanticActive = IsOrbitSemanticActive(orbitActive, orbitStatus, orbitCategory, orbitClassification, orbitExtractReason);
        bool orbitLayerIdleQuiesced = orbitLayerActive
            && !orbitSemanticActive
            && !lootingActive
            && !lootTaskRunning
            && !hasActiveLootable
            && !pathActive
            && !moverMoving;
        bool directThreatLikely = snapshot.Medical.Safety.EnemyCanShoot
            || snapshot.Medical.Safety.IncomingFireRecent
            || snapshot.Medical.Safety.ImmediateCombatBlock
            || (snapshot.Threat.DirectThreat && snapshot.Medical.Safety.EnemyVisible && !snapshot.Medical.Safety.CoveredOrHoldingAngle);
        bool sainExtractLikely = VanguardMovementAuthorityDoctrine.IsSainExtractLike(snapshot);
        string sainExtractReason = sainExtractLikely
            ? "brain=" + Safe(snapshot.Brain.ActiveLayer) + ":node=" + Safe(snapshot.Brain.Node) + ":sainLayer=" + Safe(snapshot.Sain.ActiveLayer) + ":action=" + Safe(snapshot.Sain.CurrentAction)
            : "none";
        bool sainCombatLikely = !sainExtractLikely && (snapshot.Sain.IsInCombat == true
            || snapshot.Sain.HasEnemy == true
            || Contains(snapshot.Sain.ActiveLayer, "combat")
            || Contains(snapshot.Sain.CurrentAction, "shoot")
            || Contains(snapshot.Sain.CurrentAction, "cover")
            || Contains(snapshot.Sain.CombatDecision, "combat"));
        string sainCombatStaleReason = "not_evaluated";
        bool sainCombatStaleNonActionable = false;
        if (sainCombatLikely)
        {
            sainCombatStaleNonActionable = VanguardMovementAuthorityDoctrine.IsSainCombatStaleNonActionable(snapshot, out sainCombatStaleReason);
        }

        var owner = ClassifyOwner(lootingActive, lootTaskRunning, hasActiveLootable, orbitSemanticActive, orbitLayerIdleQuiesced, pathActive, directThreatLikely, sainExtractLikely, sainCombatLikely, sainCombatStaleNonActionable);
        string blockingReason = BuildBlockingReason(owner, lootingActive, lootTaskRunning, hasActiveLootable, orbitLayerActive, orbitLayerIdleQuiesced, orbitSemanticActive, orbitActive, orbitStatus, orbitCategory, orbitClassification, orbitExtractReason, pathActive, pathDistance);

        var activity = new VanguardExternalActivitySnapshot
        {
            OperatorId = snapshot.OperatorId,
            BotProfileId = snapshot.BotProfileId,
            BotOwnerPresent = botOwnerPresent,
            LootingBotsComponentPresent = lootComponentPresent,
            LootingBotsExternalApiPresent = lootExternalPresent,
            LootingBotsActive = lootingActive,
            LootingBotsTaskRunning = lootTaskRunning,
            LootingBotsHasActiveLootable = hasActiveLootable,
            LootingBotsClassification = snapshot.Looting.Classification,
            OrbitTelemetryAvailable = orbitTelemetryAvailable,
            OrbitActive = orbitActive,
            OrbitBrainLayerActive = orbitLayerActive,
            OrbitSemanticActive = orbitSemanticActive,
            OrbitLayerIdleQuiesced = orbitLayerIdleQuiesced,
            OrbitStatus = orbitStatus,
            OrbitCategory = orbitCategory,
            OrbitClassification = orbitClassification,
            OrbitExtractReason = orbitExtractReason,
            OrbitObjective = orbitObjective,
            EftPathActive = pathActive,
            PathRemainingDistance = pathDistance,
            MoverMoving = moverMoving,
            RealSpeed = speed,
            SainExtractLikely = sainExtractLikely,
            SainExtractReason = sainExtractReason,
            SainCombatLikely = sainCombatLikely,
            SainCombatStaleNonActionable = sainCombatStaleNonActionable,
            SainCombatStaleReason = sainCombatStaleReason,
            DirectThreatLikely = directThreatLikely,
            MovementOwner = owner,
            BlockingReason = blockingReason,
            CapturedAtUtc = now
        };

        if (log)
        {
            LogThrottled("snapshot|" + Safe(snapshot.BotProfileId) + "|" + Safe(reason) + "|" + owner, now, SnapshotLogInterval, $"VANGUARD_EXTERNAL_ACTIVITY_SNAPSHOT operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; {activity.Summary}; tag={StatusTag}; orbitLayerTag={OrbitLayerQuiesceStatusTag}");
            if (activity.OrbitLayerIdleQuiesced)
            {
                LogThrottled("orbitLayerIdle|" + Safe(snapshot.BotProfileId) + "|" + Safe(reason), now, SnapshotLogInterval, $"VANGUARD_ORBIT_LAYER_IDLE_QUIESCED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; orbitLayer=true; orbitSemantic=false; path=false; moving=false; blocking=false; canDriveMovement={Bool(activity.CanDriveMedicalMovement)}; patientOnly=true; tag={OrbitLayerQuiesceStatusTag}; adapterTag={StatusTag}");
            }

            if (activity.SainCombatStaleNonActionable)
            {
                LogThrottled("sainStaleExitRead|" + Safe(snapshot.BotProfileId) + "|" + Safe(reason), now, SnapshotLogInterval, $"VANGUARD_SAIN_STALE_EXIT_READINESS operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; staleReason={Safe(activity.SainCombatStaleReason)}; combatOwned={Bool(activity.IsCombatOwned)}; movementOwner={activity.MovementOwner}; canDriveMovement={Bool(activity.CanDriveMedicalMovement)}; tag={HostileIndoorMovementPlanStatusTag}; adapterTag={StatusTag}");
            }
        }

        return activity;
    }


    public static bool ShouldDeferMedicalMovementForCombat(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason, out VanguardExternalActivitySnapshot activity, out string summary)
    {
        activity = ReadActivity(botOwner, snapshot, now, log: true, reason: Safe(reason) + "_combat_gate");
        if (!activity.IsCombatOwned)
        {
            summary = "combatGate=clear;" + activity.Summary;
            return false;
        }

        summary = "combatGate=defer;reason=" + Safe(activity.BlockingReason)
            + ";canDriveMovement=false;owner=" + activity.MovementOwner
            + ";" + activity.Summary;
        LogThrottled("combatGate|" + Safe(snapshot.BotProfileId) + "|" + Safe(reason), now, PreemptLogInterval,
            $"VANGUARD_EXTERNAL_AUTHORITY_DEFERRED_COMBAT_OWNER operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requestReason={Safe(reason)}; {summary}; patientOnly=true; noMutation=true; next=yield_to_sain_combat; tag={CombatAwareGateStatusTag}; adapterTag={StatusTag}; movementTag={MovementPreemptStatusTag}");
        return true;
    }

    public static bool IsMedicalBlockingActivity(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason)
    {
        var activity = ReadActivity(botOwner, snapshot, now);
        if (activity.IsCombatOwned)
        {
            reason = "combat_owned:" + Safe(activity.BlockingReason);
            return false;
        }

        if (activity.BlocksMedicalPrepare)
        {
            reason = activity.BlockingReason;
            return true;
        }

        reason = "inactive";
        return false;
    }

    public static VanguardExternalPreemptResult RequestMedicalPreempt(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, string reason, TimeSpan ttl, DateTimeOffset now)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        if (botOwner == null)
        {
            var empty = VanguardExternalActivitySnapshot.Empty;
            var failed = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.FailedBotOwnerMissing, empty, empty, "mutations=none", "botowner_missing");
            LogPreempt(snapshot, now, reason, failed);
            return failed;
        }

        if (VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out var schedulerCombatReason))
        {
            var empty = VanguardExternalActivitySnapshot.Empty;
            var rejected = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.RejectedCombatOwner, empty, empty, "mutations=none", "scheduler_combat_protected:" + Safe(schedulerCombatReason));
            LogPreempt(snapshot, now, reason, rejected);
            return rejected;
        }

        string key = Normalize(snapshot.BotProfileId);
        if (!SuppressedByBotProfileId.TryGetValue(key, out var state))
        {
            state = new ExternalSuppressionState
            {
                BotProfileId = snapshot.BotProfileId,
                StartedAtUtc = now,
                OrbitAgentWasActive = null
            };
            SuppressedByBotProfileId[key] = state;
        }
        var requestedUntil = now + ttl;
        state.ExpiresAtUtc = state.ExpiresAtUtc > requestedUntil ? state.ExpiresAtUtc : requestedUntil;
        state.LastReason = reason;

        // The runtime fast path: a clean authority grant is a lease-local fact for a very short cadence.
        // Reuse it before entering reflection-heavy integration reads when the immutable decision
        // snapshot contains no renewed loot, ORBIT, EFT path, locomotion or combat signal. This keeps
        // stationary medical safe while preventing a single synchronous adapter pass from stalling the
        // headless thread. Any material snapshot change immediately falls through to a full read/preempt.
        if (state.LastMedicalPreemptResult.HasValue
            && state.LastMedicalPreemptResult.Value.CanDriveMovement
            && now - state.LastMedicalMutationAtUtc < MedicalPreemptSnapshotFastPathInterval
            && !HasSnapshotMedicalAuthorityResidue(snapshot))
        {
            var clean = state.LastMedicalPreemptResult.Value.After;
            return new VanguardExternalPreemptResult(
                VanguardExternalPreemptOutcome.Granted,
                clean,
                clean,
                "mutations=none;snapshotFastPath=true;fastPathCadenceMs=" + MedicalPreemptSnapshotFastPathInterval.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
                "existing_medical_authority_clean_snapshot_stable");
        }

        long beforeReadStarted = VanguardRuntimePerformanceGuard.Begin();
        var before = ReadActivity(botOwner, snapshot, now, log: false, reason: reason + "_before");
        VanguardRuntimePerformanceGuard.End("MedicalExternalActivityReadBefore", beforeReadStarted);
        if (before.IsCombatOwned)
        {
            var rejected = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.RejectedCombatOwner, before, before, "mutations=none", before.BlockingReason);
            LogPreempt(snapshot, now, reason, rejected);
            return rejected;
        }

        // The runtime idempotence: when the previous preempt already established a clean patient-only
        // authority state, do not repeat the same mutations. The full read above is retained after the
        // snapshot fast path window, so renewed external residue remains observable and is suppressed.
        if (state.LastMedicalPreemptResult.HasValue
            && state.LastMedicalPreemptResult.Value.CanDriveMovement
            && now - state.LastMedicalMutationAtUtc < MedicalPreemptMutationInterval
            && !before.BlocksMedicalPrepare
            && !before.IsCombatOwned
            && !before.MoverMoving
            && !before.EftPathActive)
        {
            return new VanguardExternalPreemptResult(
                VanguardExternalPreemptOutcome.Granted,
                before,
                before,
                "mutations=none;idempotentHold=true;mutationCadenceMs=" + MedicalPreemptMutationInterval.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
                "existing_medical_authority_clean");
        }

        var mutations = new List<string>(24);
        long mutationStarted = VanguardRuntimePerformanceGuard.Begin();
        bool lootingActive = before.LootingBotsActive || before.LootingBotsTaskRunning || before.LootingBotsHasActiveLootable;
        ApplyLootingBotsPreempt(botOwner, ttl, mutations, cleanupActiveState: lootingActive);
        if (before.OrbitActive || before.OrbitBrainLayerActive || before.OrbitSemanticActive || before.IsOrbitObjectiveResidue)
        {
            ApplyOrbitPreempt(botOwner, snapshot, state, mutations);
        }
        else
        {
            mutations.Add("orbitPreemptSkipped=inactive_snapshot");
        }
        ApplyPathPreempt(botOwner, mutations);
        VanguardRuntimePerformanceGuard.End("MedicalExternalPreemptMutation", mutationStarted);

        long afterReadStarted = VanguardRuntimePerformanceGuard.Begin();
        var after = ReadActivity(botOwner, snapshot, now, log: false, reason: reason + "_after");
        VanguardRuntimePerformanceGuard.End("MedicalExternalActivityReadAfter", afterReadStarted);
        var outcome = ClassifyPreemptOutcome(before, after);
        var result = new VanguardExternalPreemptResult(outcome, before, after, "mutations=" + string.Join(",", mutations), after.BlockingReason);
        state.LastMedicalMutationAtUtc = now;
        state.LastMedicalPreemptResult = result;
        LogPreempt(snapshot, now, reason, result);
        return result;
    }

    private static bool HasSnapshotMedicalAuthorityResidue(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return true;
        }

        float? pathDistance = snapshot.Movement.DistanceToDestination ?? snapshot.Movement.GoToDistance;
        bool pathActive = snapshot.Movement.HasPath == true && pathDistance.HasValue && pathDistance.Value > 1.00f;
        bool moving = Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed) > 0.35f;
        bool looting = snapshot.Looting.BotLooting == true
            || snapshot.Looting.LootTaskRunning == true
            || snapshot.Looting.HasActiveLootable == true;
        bool orbit = IsOrbitSemanticActive(
            snapshot.Orbit.Active,
            snapshot.Orbit.Status,
            snapshot.Orbit.Category,
            snapshot.Orbit.Classification,
            snapshot.Orbit.ExtractReason);
        bool combat = snapshot.Medical.Safety.EnemyCanShoot
            || snapshot.Medical.Safety.IncomingFireRecent
            || snapshot.Medical.Safety.ImmediateCombatBlock
            || VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot);
        return pathActive || moving || looting || orbit || combat;
    }



    public static VanguardExternalPreemptResult RequestGrenadeEmergencyPreempt(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, string reason, TimeSpan ttl, DateTimeOffset now)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        var before = ReadActivity(botOwner, snapshot, now, log: true, reason: "grenade_emergency_" + Safe(reason) + "_before");
        if (botOwner == null)
        {
            var failed = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.FailedBotOwnerMissing, before, before, "mutations=none", "botowner_missing");
            VanguardClientDiagnosticsLog.Warning(VanguardGrenadeEmergencyPolicy.StatusTag,
                $"VANGUARD_EXTERNAL_PREEMPT_FAILED botProfile={Safe(snapshot.BotProfileId)}; reason=botowner_missing; mutation=false; tag={VanguardGrenadeEmergencyPolicy.StatusTag}");
            return failed;
        }

        string key = Normalize(snapshot.BotProfileId);
        if (!SuppressedByBotProfileId.TryGetValue(key, out var state))
        {
            state = new ExternalSuppressionState
            {
                BotProfileId = snapshot.BotProfileId,
                StartedAtUtc = now,
                OrbitAgentWasActive = null
            };
            SuppressedByBotProfileId[key] = state;
        }

        DateTimeOffset requestedUntil = now + ttl;
        state.ExpiresAtUtc = state.ExpiresAtUtc > requestedUntil ? state.ExpiresAtUtc : requestedUntil;
        state.LastReason = "grenade_emergency:" + reason;

        var mutations = new List<string>(24);
        ApplyLootingBotsPreempt(botOwner, ttl, mutations);
        ApplyOrbitPreempt(botOwner, snapshot, state, mutations);
        bool directPathAuthorityAcquired = ApplyGrenadeEmergencyPathPreempt(botOwner, mutations);
        mutations.Add("sainTargetMutation=false");
        mutations.Add("sainDecisionMutation=false");
        mutations.Add("handsForceCancel=false");

        var after = ReadActivity(botOwner, snapshot, now, log: true, reason: "grenade_emergency_" + Safe(reason) + "_after");
        var outcome = ClassifyGrenadeEmergencyPreemptOutcome(after, directPathAuthorityAcquired);
        var result = new VanguardExternalPreemptResult(outcome, before, after, "mutations=" + string.Join(",", mutations), after.BlockingReason);
        VanguardClientDiagnosticsLog.Operational(VanguardGrenadeEmergencyPolicy.ActivityPreemptedTag, () =>
            $"botProfile={Safe(snapshot.BotProfileId)}; scope=external_drivers; outcome={result.Outcome}; before={Safe(before.Summary)}; after={Safe(after.Summary)}; mutations={Safe(result.MutationSummary)}; directPathAuthorityAcquired={Bool(directPathAuthorityAcquired)}; staleSnapshotPathResidueIgnored={Bool(directPathAuthorityAcquired && after.IsPathResidue)}; sainCombatStatePreserved=true; emergencyBigBrainLayerOwnershipRequested=true; physicalOwnershipProofSeparate=true; reason={Safe(reason)}; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}; foundationTag={VanguardGrenadeEmergencyPolicy.StatusTag}; adapterTag={StatusTag}");
        if (directPathAuthorityAcquired)
        {
            VanguardClientDiagnosticsLog.Operational(VanguardGrenadeEmergencyPolicy.PathAuthorityAcquiredTag, () =>
                $"botProfile={Safe(snapshot.BotProfileId)}; pathBackendsStopped=true; staleSnapshotPathResidue={Bool(after.IsPathResidue)}; next=native_or_exact_fallback_atomic_replace; sainTargetPreserved=true; sainDecisionPreserved=true; tag={VanguardGrenadeEmergencyPolicy.SafetyContinuityStatusTag}");
        }
        return result;
    }

    public static VanguardExternalPreemptResult RequestMovementHardReturnPreempt(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, string reason, TimeSpan ttl, DateTimeOffset now)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        var before = ReadActivity(botOwner, snapshot, now, log: true, reason: "movement_hard_return_" + Safe(reason) + "_before");
        if (botOwner == null)
        {
            var failed = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.FailedBotOwnerMissing, before, before, "mutations=none", "botowner_missing");
            LogMovementHardReturnPreempt(snapshot, now, reason, failed);
            return failed;
        }

        if (VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out var schedulerCombatReason))
        {
            var rejected = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.RejectedCombatOwner, before, before, "mutations=none", "scheduler_combat_protected:" + Safe(schedulerCombatReason));
            LogMovementHardReturnPreempt(snapshot, now, reason, rejected);
            return rejected;
        }

        if (before.IsCombatOwned)
        {
            var rejected = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.RejectedCombatOwner, before, before, "mutations=none", before.BlockingReason);
            LogMovementHardReturnPreempt(snapshot, now, reason, rejected);
            return rejected;
        }

        string key = Normalize(snapshot.BotProfileId);
        if (!SuppressedByBotProfileId.TryGetValue(key, out var state))
        {
            state = new ExternalSuppressionState
            {
                BotProfileId = snapshot.BotProfileId,
                StartedAtUtc = now,
                OrbitAgentWasActive = null
            };
            SuppressedByBotProfileId[key] = state;
        }

        var requestedUntil = now + ttl;
        state.ExpiresAtUtc = state.ExpiresAtUtc > requestedUntil ? state.ExpiresAtUtc : requestedUntil;
        state.LastReason = "movement_hard_return:" + reason;

        var mutations = new List<string>(24);
        ApplyLootingBotsPreempt(botOwner, ttl, mutations);
        ApplyOrbitPreempt(botOwner, snapshot, state, mutations);
        ApplyPathPreempt(botOwner, mutations);

        var after = ReadActivity(botOwner, snapshot, now, log: true, reason: "movement_hard_return_" + Safe(reason) + "_after");
        var outcome = ClassifyPreemptOutcome(before, after);
        var result = new VanguardExternalPreemptResult(outcome, before, after, "mutations=" + string.Join(",", mutations), after.BlockingReason);
        LogMovementHardReturnPreempt(snapshot, now, reason, result);
        return result;
    }


    public static VanguardExternalPreemptResult RequestOrbitAuthorityQuiesce(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, string reason, TimeSpan ttl, DateTimeOffset now)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        string safeReason = "orbit_authority_quiesce:" + Safe(reason);
        var before = ReadActivity(botOwner, snapshot, now, log: true, reason: safeReason + "_before");
        if (botOwner == null)
        {
            var failed = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.FailedBotOwnerMissing, before, before, "mutations=none", "botowner_missing");
            LogOrbitAuthorityQuiesce(snapshot, now, safeReason, failed, "bot_owner_missing");
            return failed;
        }

        if (VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out var schedulerCombatReason))
        {
            var rejected = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.RejectedCombatOwner, before, before, "mutations=none", "scheduler_combat_protected:" + Safe(schedulerCombatReason));
            LogOrbitAuthorityQuiesce(snapshot, now, safeReason, rejected, "scheduler_combat_protected");
            return rejected;
        }

        if (before.IsCombatOwned || VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot) || VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(snapshot))
        {
            var rejected = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.RejectedCombatOwner, before, before, "mutations=none", before.BlockingReason);
            LogOrbitAuthorityQuiesce(snapshot, now, safeReason, rejected, "combat_owner_respected");
            return rejected;
        }

        if (VanguardMovementAuthorityDoctrine.HasCriticalLootActivity(snapshot))
        {
            var rejectedLoot = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.FailedLootingBotsStillActive, before, before, "mutations=none", "critical_loot_activity_respected");
            LogOrbitAuthorityQuiesce(snapshot, now, safeReason, rejectedLoot, "critical_loot_respected");
            return rejectedLoot;
        }

        string key = Normalize(snapshot.BotProfileId);
        if (!SuppressedByBotProfileId.TryGetValue(key, out var state))
        {
            state = new ExternalSuppressionState
            {
                BotProfileId = snapshot.BotProfileId,
                StartedAtUtc = now,
                OrbitAgentWasActive = null
            };
            SuppressedByBotProfileId[key] = state;
        }

        var requestedUntil = now + ttl;
        state.ExpiresAtUtc = state.ExpiresAtUtc > requestedUntil ? state.ExpiresAtUtc : requestedUntil;
        state.LastReason = safeReason;

        var mutations = new List<string>(32);
        ApplyLootingBotsPreempt(botOwner, ttl, mutations);
        ApplyOrbitPreempt(botOwner, snapshot, state, mutations);
        ApplyPathPreempt(botOwner, mutations);

        var after = ReadActivity(botOwner, snapshot, now, log: true, reason: safeReason + "_after");
        var outcome = ClassifyOrbitAuthorityQuiesceOutcome(snapshot, after);
        var result = new VanguardExternalPreemptResult(outcome, before, after, "mutations=" + string.Join(",", mutations), after.BlockingReason);
        LogOrbitAuthorityQuiesce(snapshot, now, safeReason, result, "vanguard_default_movement_authority");
        return result;
    }



    public static VanguardExternalPreemptResult RequestCombatWindowNoProductionCleanup(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, string reason, TimeSpan ttl, DateTimeOffset now)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        var before = ReadActivity(botOwner, snapshot, now, log: true, reason: "combat_no_production_cleanup_" + Safe(reason) + "_before");
        if (botOwner == null)
        {
            var failed = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.FailedBotOwnerMissing, before, before, "mutations=none", "botowner_missing");
            LogThrottled("CombatCleanupMissing|" + Normalize(snapshot.BotProfileId), now, PreemptLogInterval,
                $"VANGUARD_COMBAT_WINDOW_CLEANUP_SKIPPED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; outcome={failed.Outcome}; tag={VanguardMovementAuthorityDoctrine.CombatWindowClosureStatusTag}; adapterTag={StatusTag}");
            return failed;
        }

        if (VanguardMovementAuthorityDoctrine.IsCombatProductive(snapshot, out var productiveReason)
            || VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot))
        {
            var rejected = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.RejectedCombatOwner, before, before, "mutations=none", "productive_or_true_direct:" + productiveReason);
            LogThrottled("CombatCleanupReject|" + Normalize(snapshot.BotProfileId) + "|" + Safe(productiveReason), now, PreemptLogInterval,
                $"VANGUARD_SAIN_WINDOW_CLEANUP_SKIPPED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; productiveReason={Safe(productiveReason)}; trueDirectThreat={Bool(VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot))}; outcome={rejected.Outcome}; doctrine=shared_awareness_is_relevance_not_productivity_but_true_direct_threat_preserves_sain; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; legacyTag={VanguardMovementAuthorityDoctrine.CombatWindowClosureStatusTag}; adapterTag={StatusTag}");
            return rejected;
        }

        var mutations = new List<string>(24);
        ApplySainSearchBoundaryPreempt(botOwner, ttl, mutations);
        mutations.Add("combatNoProductionCleanup=true");
        mutations.Add("activeTargetDemotedButKnowledgeKept=true");
        mutations.Add("sameTargetReopenBackoffExpected=true");
        mutations.Add("freshDifferentTargetMayReengage=true");

        var after = ReadActivity(botOwner, snapshot, now, log: true, reason: "combat_no_production_cleanup_" + Safe(reason) + "_after");
        var result = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.Granted, before, after, "mutations=" + string.Join(",", mutations), reason);
        LogThrottledLazy("CombatCleanup|" + Normalize(snapshot.BotProfileId) + "|" + Safe(reason), now, PreemptLogInterval, VanguardAuditLevel.Operational, () =>
            $"VANGUARD_COMBAT_WINDOW_CLEANUP_APPLIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requestReason={Safe(reason)}; {result.CompactSummary}; fullActivityPayload=false; doctrine=single_terminal_cleanup_after_bounded_no_progress_then_same_target_backoff; tag={VanguardMovementAuthorityDoctrine.CombatWindowClosureStatusTag}; adapterTag={StatusTag}");
        LogThrottledLazy("CombatCleanupTrace|" + Normalize(snapshot.BotProfileId) + "|" + Safe(reason), now, PreemptLogInterval, VanguardAuditLevel.Trace, () =>
            $"VANGUARD_COMBAT_WINDOW_CLEANUP_APPLIED_TRACE botProfile={Safe(snapshot.BotProfileId)}; {result.Summary}; tag={StatusTag}");
        return result;
    }

    public static VanguardExternalPreemptResult RequestOrchestratorCombatAuthorityRelease(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, string reason, TimeSpan ttl, DateTimeOffset now)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        var before = ReadActivity(botOwner, snapshot, now, log: true, reason: "combat_authority_" + Safe(reason) + "_before");
        if (botOwner == null)
        {
            var failed = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.FailedBotOwnerMissing, before, before, "mutations=none", "botowner_missing");
            LogThrottledLazy("CombatMissing|" + Normalize(snapshot.BotProfileId), now, PreemptLogInterval, VanguardAuditLevel.Operational, () =>
                $"VANGUARD_COMBAT_AUTHORITY_RELEASE_APPLIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; {failed.CompactSummary}; fullActivityPayload=false; tag={VanguardOrchestratorAuthorityPolicy.StatusTag}; adapterTag={StatusTag}");
            return failed;
        }

        if (!VanguardOrchestratorAuthorityPolicy.IsCombatAuthority(snapshot, out var authorityReason))
        {
            var rejected = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.RejectedCombatOwner, before, before, "mutations=none", "not_combat_authority:" + authorityReason);
            LogThrottled("CombatReject|" + Normalize(snapshot.BotProfileId) + "|" + Safe(authorityReason), now, PreemptLogInterval,
                $"VANGUARD_COMBAT_AUTHORITY_RELEASE_SKIPPED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; authorityReason={Safe(authorityReason)}; outcome={rejected.Outcome}; tag={VanguardOrchestratorAuthorityPolicy.StatusTag}; adapterTag={StatusTag}");
            return rejected;
        }

        var mutations = new List<string>(40);
        ApplyLootingBotsPreempt(botOwner, ttl, mutations);
        ApplyOrbitPreempt(botOwner, snapshot, GetOrCreateSuppressionState(snapshot, now, "combat_authority:" + reason, ttl), mutations);
        VanguardReturnMovementCommandStore.Clear(snapshot.BotProfileId, "combat_authority_release");
        mutations.Add("vanguardCommandClear=true");
        bool alreadySainCombatOwned = before.IsCombatOwned || snapshot.Sain.IsInCombat == true || Contains(snapshot.Sain.ActiveLayer, "combat") || Contains(snapshot.Brain.ActiveLayer, "combat");
        ApplyCombatMovementHandoff(botOwner, snapshot, mutations, alreadySainCombatOwned ? "sain_already_owns_combat_movement" : "combat_authority_handoff");
        mutations.Add("combatHandsMutation=false");
        mutations.Add("combatManualShootReset=false");
        mutations.Add("combatSuppressionReset=false");
        mutations.Add("combatReloadProbe=false");
        mutations.Add("combatUnderFireSynthesis=false");
        mutations.Add("combatGoalHeartbeatRecalc=false");
        mutations.Add("EntryReleaseOnly=true");
        mutations.Add("PrimaryDomain=Combat");
        mutations.Add("ExclusiveDomain=Combat");
        mutations.Add("cohesionSuspended=true");
        mutations.Add("stationaryMedicalQuiet=true");
        mutations.Add("mobileMedicalSidecarAllowed=true");
        mutations.Add("awarenessCombatSupportActive=true");
        mutations.Add("scanAssignmentActiveCombatSupport=true");

        var after = ReadActivity(botOwner, snapshot, now, log: true, reason: "combat_authority_" + Safe(reason) + "_after");
        var result = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.Granted, before, after, "mutations=" + string.Join(",", mutations), authorityReason);
        LogThrottledLazy("CombatAuthority|" + Normalize(snapshot.BotProfileId) + "|" + Safe(reason), now, PreemptLogInterval, VanguardAuditLevel.Operational, () =>
            $"VANGUARD_COMBAT_AUTHORITY_RELEASE_APPLIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requestReason={Safe(reason)}; authorityReason={Safe(authorityReason)}; {result.CompactSummary}; fullActivityPayload=false; tag={VanguardOrchestratorAuthorityPolicy.StatusTag}; adapterTag={StatusTag}");
        LogThrottledLazy("CombatAuthority|" + Normalize(snapshot.BotProfileId) + "|" + Safe(reason), now, TimeSpan.FromSeconds(0.85d), VanguardAuditLevel.Diagnostic, () =>
            $"VANGUARD_COMBAT_LOCK_APPLIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requestReason={Safe(reason)}; authorityReason={Safe(authorityReason)}; outcome={result.Outcome}; fullActivityPayload=false; doctrine=edge_triggered_external_driver_release_then_sain_sovereign; tag={VanguardOrchestratorAuthorityPolicy.ExclusiveAuthorityStatusTag}; Tag={VanguardOrchestratorAuthorityPolicy.StatusTag}; adapterTag={StatusTag}");
        LogThrottledLazy("CombatAuthorityTrace|" + Normalize(snapshot.BotProfileId) + "|" + Safe(reason), now, PreemptLogInterval, VanguardAuditLevel.Trace, () =>
            $"VANGUARD_COMBAT_AUTHORITY_RELEASE_APPLIED_TRACE botProfile={Safe(snapshot.BotProfileId)}; {result.Summary}; tag={StatusTag}");
        return result;
    }


    public static VanguardExternalPreemptResult RequestMedicalBreakContact(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, string reason, TimeSpan ttl, DateTimeOffset now)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        var before = ReadActivity(botOwner, snapshot, now, log: true, reason: "medical_break_contact_" + Safe(reason) + "_before");
        if (botOwner == null)
        {
            return new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.FailedBotOwnerMissing, before, before, "mutations=none", "botowner_missing");
        }

        if (VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out var schedulerCombatReason))
        {
            var rejected = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.RejectedCombatOwner, before, before, "mutations=none", "scheduler_combat_protected:" + Safe(schedulerCombatReason));
            LogThrottled("medicalBreakContactSchedulerReject|" + Normalize(snapshot.BotProfileId), now, PreemptLogInterval,
                $"VANGUARD_MEDICAL_BREAK_CONTACT_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requestReason={Safe(reason)}; schedulerReason={Safe(schedulerCombatReason)}; outcome={rejected.Outcome}; mutation=false; doctrine=sain_window_is_exclusive; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; adapterTag={StatusTag}");
            return rejected;
        }

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot))
        {
            var rejected = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.RejectedCombatOwner, before, before, "mutations=none", "true_direct_threat_preserves_sain");
            LogThrottled("medicalBreakContactCombatReject|" + Normalize(snapshot.BotProfileId), now, PreemptLogInterval,
                $"VANGUARD_MEDICAL_BREAK_CONTACT_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requestReason={Safe(reason)}; outcome={rejected.Outcome}; mutation=false; doctrine=medical_never_cancels_hands_or_path_during_true_direct_combat; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; adapterTag={StatusTag}");
            return rejected;
        }

        var mutations = new List<string>(20);
        ApplyLootingBotsPreempt(botOwner, ttl, mutations);
        VanguardReturnMovementCommandStore.Clear(snapshot.BotProfileId, "medical_break_contact");
        mutations.Add("vanguardCommandClear=true");
        ApplyPathPreempt(botOwner, mutations);

        var after = ReadActivity(botOwner, snapshot, now, log: true, reason: "medical_break_contact_" + Safe(reason) + "_after");
        var result = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.Granted, before, after, "mutations=" + string.Join(",", mutations), reason);
        LogThrottled("medicalBreakContact|" + Normalize(snapshot.BotProfileId) + "|" + Safe(reason), now, PreemptLogInterval,
            $"VANGUARD_MEDICAL_BREAK_CONTACT_APPLIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requestReason={Safe(reason)}; outcome={result.Outcome}; {result.Summary}; tag={CombatBindCohesionRecoveryStatusTag}; adapterTag={StatusTag}");
        return result;
    }

    public static VanguardExternalPreemptResult RequestSainBoundaryReturnPreempt(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, string reason, TimeSpan ttl, DateTimeOffset now)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        var before = ReadActivity(botOwner, snapshot, now, log: true, reason: "sain_boundary_return_" + Safe(reason) + "_before");
        if (botOwner == null)
        {
            var failed = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.FailedBotOwnerMissing, before, before, "mutations=none", "botowner_missing");
            LogSainBoundaryPreempt(snapshot, now, reason, failed);
            return failed;
        }

        if (VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out var schedulerCombatReason))
        {
            var rejected = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.RejectedCombatOwner, before, before, "mutations=none", "scheduler_combat_protected:" + Safe(schedulerCombatReason));
            LogSainBoundaryPreempt(snapshot, now, reason, rejected);
            return rejected;
        }

        if (!VanguardMovementAuthorityDoctrine.IsSainBoundaryReturnEligible(snapshot, out var boundaryReason))
        {
            var rejected = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.RejectedCombatOwner, before, before, "mutations=none", "boundary_not_eligible:" + boundaryReason);
            LogSainBoundaryPreempt(snapshot, now, reason, rejected);
            return rejected;
        }

        if (before.DirectThreatLikely || snapshot.Medical.Safety.EnemyCanShoot || snapshot.Medical.Safety.IncomingFireRecent || snapshot.Threat.EnemyCanShoot == true)
        {
            var rejected = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.RejectedCombatOwner, before, before, "mutations=none", "true_direct_threat");
            LogSainBoundaryPreempt(snapshot, now, reason, rejected);
            return rejected;
        }

        string key = Normalize(snapshot.BotProfileId);
        if (!SuppressedByBotProfileId.TryGetValue(key, out var state))
        {
            state = new ExternalSuppressionState
            {
                BotProfileId = snapshot.BotProfileId,
                StartedAtUtc = now,
                OrbitAgentWasActive = null
            };
            SuppressedByBotProfileId[key] = state;
        }

        var requestedUntil = now + ttl;
        state.ExpiresAtUtc = state.ExpiresAtUtc > requestedUntil ? state.ExpiresAtUtc : requestedUntil;
        state.LastReason = "sain_boundary_return:" + reason;

        var mutations = new List<string>(32);
        ApplySainSearchBoundaryPreempt(botOwner, ttl, mutations);
        ApplyLootingBotsPreempt(botOwner, ttl, mutations);
        ApplyOrbitPreempt(botOwner, snapshot, state, mutations);
        ApplyPathPreempt(botOwner, mutations);

        var after = ReadActivity(botOwner, snapshot, now, log: true, reason: "sain_boundary_return_" + Safe(reason) + "_after");
        var outcome = ClassifySainBoundaryPreemptOutcome(after);
        var result = new VanguardExternalPreemptResult(outcome, before, after, "mutations=" + string.Join(",", mutations), after.BlockingReason);
        LogSainBoundaryPreempt(snapshot, now, reason + ":" + boundaryReason, result);
        return result;
    }


    public static VanguardExternalPreemptResult RequestScheduledMovementHardReturnPreempt(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, string reason, TimeSpan ttl, DateTimeOffset now)
    {
        return RequestScheduledHardReturnStartPreempt(botOwner, snapshot, reason, ttl, now, includeSainBoundarySuppression: false);
    }

    public static VanguardExternalPreemptResult RequestScheduledSainBoundaryReturnPreempt(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, string reason, TimeSpan ttl, DateTimeOffset now)
    {
        return RequestScheduledHardReturnStartPreempt(botOwner, snapshot, reason, ttl, now, includeSainBoundarySuppression: true);
    }

    private static VanguardExternalPreemptResult RequestScheduledHardReturnStartPreempt(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, string reason, TimeSpan ttl, DateTimeOffset now, bool includeSainBoundarySuppression)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        string scope = includeSainBoundarySuppression ? "scheduled_sain_boundary_return" : "scheduled_movement_hard_return";
        var before = ReadActivity(botOwner, snapshot, now, log: true, reason: scope + "_" + Safe(reason) + "_before");
        if (botOwner == null)
        {
            var failed = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.FailedBotOwnerMissing, before, before, "mutations=none", "botowner_missing");
            LogMovementContinuationPreempt(snapshot, now, reason, failed, includeSainBoundarySuppression, allowActiveVanguardPathResidue: false);
            return failed;
        }

        if (VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out var schedulerCombatReason))
        {
            var rejected = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.RejectedCombatOwner, before, before, "mutations=none", "scheduler_combat_protected:" + Safe(schedulerCombatReason));
            LogMovementContinuationPreempt(snapshot, now, reason, rejected, includeSainBoundarySuppression, allowActiveVanguardPathResidue: false);
            return rejected;
        }

        bool isolatedCombatRelease = IsIsolatedCombatReleaseReason(reason);
        if ((before.DirectThreatLikely || VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot)) && !isolatedCombatRelease)
        {
            var rejected = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.RejectedCombatOwner, before, before, "mutations=none", "true_direct_threat");
            LogMovementContinuationPreempt(snapshot, now, reason, rejected, includeSainBoundarySuppression, allowActiveVanguardPathResidue: false);
            return rejected;
        }

        if (includeSainBoundarySuppression && !isolatedCombatRelease && !VanguardMovementAuthorityDoctrine.IsSainBoundaryReturnEligible(snapshot, out var boundaryReason))
        {
            var rejected = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.RejectedCombatOwner, before, before, "mutations=none", "boundary_not_eligible:" + boundaryReason);
            LogMovementContinuationPreempt(snapshot, now, reason, rejected, includeSainBoundarySuppression, allowActiveVanguardPathResidue: false);
            return rejected;
        }

        string key = Normalize(snapshot.BotProfileId);
        if (!SuppressedByBotProfileId.TryGetValue(key, out var state))
        {
            state = new ExternalSuppressionState
            {
                BotProfileId = snapshot.BotProfileId,
                StartedAtUtc = now,
                OrbitAgentWasActive = null
            };
            SuppressedByBotProfileId[key] = state;
        }

        var requestedUntil = now + ttl;
        state.ExpiresAtUtc = state.ExpiresAtUtc > requestedUntil ? state.ExpiresAtUtc : requestedUntil;
        state.LastReason = scope + ":" + reason;

        var mutations = new List<string>(36);
        if (includeSainBoundarySuppression || isolatedCombatRelease)
        {
            ApplySainSearchBoundaryPreempt(botOwner, ttl, mutations);
            if (isolatedCombatRelease && !includeSainBoundarySuppression)
            {
                mutations.Add("isolatedCombatSainBoundarySuppression=true");
            }
        }

        ApplyLootingBotsPreempt(botOwner, ttl, mutations);
        ApplyOrbitPreempt(botOwner, snapshot, state, mutations);
        ApplyPathPreempt(botOwner, mutations);
        mutations.Add("schedulerPrimaryWindow=true");
        mutations.Add("pathOrMoverResidueMayBecomePending=true");

        var after = ReadActivity(botOwner, snapshot, now, log: true, reason: scope + "_" + Safe(reason) + "_after");
        var outcome = ClassifyScheduledMovementPreemptOutcome(after, isolatedCombatRelease);
        var result = new VanguardExternalPreemptResult(outcome, before, after, "mutations=" + string.Join(",", mutations), after.BlockingReason);
        LogMovementContinuationPreempt(snapshot, now, reason + ":scheduled_start", result, includeSainBoundarySuppression, allowActiveVanguardPathResidue: false);
        return result;
    }

    private static VanguardExternalPreemptOutcome ClassifyScheduledMovementPreemptOutcome(VanguardExternalActivitySnapshot after, bool isolatedCombatRelease)
    {
        if (after.DirectThreatLikely && !isolatedCombatRelease)
        {
            return VanguardExternalPreemptOutcome.RejectedCombatOwner;
        }

        if (after.LootingBotsActive || after.LootingBotsTaskRunning || after.LootingBotsHasActiveLootable)
        {
            return VanguardExternalPreemptOutcome.FailedLootingBotsStillActive;
        }

        if (IsOrbitBlockingForHardReturn(after))
        {
            return VanguardExternalPreemptOutcome.FailedOrbitStillActive;
        }

        if (after.IsPathResidue || after.MoverMoving)
        {
            return VanguardExternalPreemptOutcome.Pending;
        }

        return VanguardExternalPreemptOutcome.Granted;
    }


    public static VanguardExternalPreemptResult RequestMovementHardReturnContinuationPreempt(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, string reason, TimeSpan ttl, DateTimeOffset now, bool allowActiveVanguardPathResidue)
    {
        return RequestHardReturnContinuationPreempt(botOwner, snapshot, reason, ttl, now, includeSainBoundarySuppression: false, allowActiveVanguardPathResidue: allowActiveVanguardPathResidue);
    }

    public static VanguardExternalPreemptResult RequestSainBoundaryReturnContinuationPreempt(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, string reason, TimeSpan ttl, DateTimeOffset now, bool allowActiveVanguardPathResidue)
    {
        return RequestHardReturnContinuationPreempt(botOwner, snapshot, reason, ttl, now, includeSainBoundarySuppression: true, allowActiveVanguardPathResidue: allowActiveVanguardPathResidue);
    }

    private static VanguardExternalPreemptResult RequestHardReturnContinuationPreempt(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, string reason, TimeSpan ttl, DateTimeOffset now, bool includeSainBoundarySuppression, bool allowActiveVanguardPathResidue)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        string scope = includeSainBoundarySuppression ? "sain_boundary_return_continuation" : "movement_hard_return_continuation";
        var before = ReadActivity(botOwner, snapshot, now, log: true, reason: scope + "_" + Safe(reason) + "_before");
        if (botOwner == null)
        {
            var failed = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.FailedBotOwnerMissing, before, before, "mutations=none", "botowner_missing");
            LogMovementContinuationPreempt(snapshot, now, reason, failed, includeSainBoundarySuppression, allowActiveVanguardPathResidue);
            return failed;
        }

        if (VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out var schedulerCombatReason))
        {
            var rejected = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.RejectedCombatOwner, before, before, "mutations=none", "scheduler_combat_protected:" + Safe(schedulerCombatReason));
            LogMovementContinuationPreempt(snapshot, now, reason, rejected, includeSainBoundarySuppression, allowActiveVanguardPathResidue);
            return rejected;
        }

        // Runtime invariant: active return continuation must not reuse SAIN boundary start eligibility and must not reject
        // merely because the Operator improved from >88m to an intermediate distance.  Only true direct threat
        // is allowed to take the movement authority back from Vanguard at this stage.
        bool isolatedCombatRelease = IsIsolatedCombatReleaseReason(reason);
        if ((before.DirectThreatLikely || VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot)) && !isolatedCombatRelease)
        {
            var rejected = new VanguardExternalPreemptResult(VanguardExternalPreemptOutcome.RejectedCombatOwner, before, before, "mutations=none", "true_direct_threat");
            LogMovementContinuationPreempt(snapshot, now, reason, rejected, includeSainBoundarySuppression, allowActiveVanguardPathResidue);
            return rejected;
        }

        string key = Normalize(snapshot.BotProfileId);
        if (!SuppressedByBotProfileId.TryGetValue(key, out var state))
        {
            state = new ExternalSuppressionState
            {
                BotProfileId = snapshot.BotProfileId,
                StartedAtUtc = now,
                OrbitAgentWasActive = null
            };
            SuppressedByBotProfileId[key] = state;
        }

        var requestedUntil = now + ttl;
        state.ExpiresAtUtc = state.ExpiresAtUtc > requestedUntil ? state.ExpiresAtUtc : requestedUntil;
        state.LastReason = scope + ":" + reason;

        var mutations = new List<string>(32);
        if (includeSainBoundarySuppression || isolatedCombatRelease)
        {
            ApplySainSearchBoundaryPreempt(botOwner, ttl, mutations);
            if (isolatedCombatRelease && !includeSainBoundarySuppression)
            {
                mutations.Add("isolatedCombatSainBoundarySuppression=true");
            }
        }

        ApplyLootingBotsPreempt(botOwner, ttl, mutations);
        ApplyOrbitPreempt(botOwner, snapshot, state, mutations);
        if (allowActiveVanguardPathResidue)
        {
            mutations.Add("activeVanguardPathPreserved=true");
            mutations.Add("activeVanguardMoverPreserved=true");
        }
        else
        {
            ApplyPathPreempt(botOwner, mutations);
        }

        var after = ReadActivity(botOwner, snapshot, now, log: true, reason: scope + "_" + Safe(reason) + "_after");
        var outcome = ClassifyMovementContinuationPreemptOutcome(after, allowActiveVanguardPathResidue, isolatedCombatRelease);
        var result = new VanguardExternalPreemptResult(outcome, before, after, "mutations=" + string.Join(",", mutations), after.BlockingReason);
        LogMovementContinuationPreempt(snapshot, now, reason, result, includeSainBoundarySuppression, allowActiveVanguardPathResidue);
        return result;
    }

    public static string ReleaseMovementHardReturnPreempt(BotOwner? botOwner, string? botProfileId, DateTimeOffset now, string reason)
    {
        string summary = ReleaseMedicalPreempt(botOwner, botProfileId, now, "movement_hard_return:" + Safe(reason));
        LogThrottled("movementHardReturnRelease|" + Normalize(botProfileId) + "|" + Safe(reason), now, SnapshotLogInterval,
            $"VANGUARD_EXTERNAL_SUPPRESS_RELEASED botProfile={Normalize(botProfileId)}; reason={Safe(reason)}; {summary}; tag={MovementHardReturnSuppressStatusTag}; adapterTag={StatusTag}");
        return summary;
    }

    public static string ReleaseMedicalPreempt(BotOwner? botOwner, string? botProfileId, DateTimeOffset now, string reason)
    {
        string key = Normalize(botProfileId);
        if (VanguardMainIntentScheduler.IsSainCombatExecutionProtected(key, now, out var combatProtectionReason))
        {
            string deferred = "externalRelease=deferred_sain_combat_protected;reason=" + Safe(combatProtectionReason);
            LogThrottled("releaseDeferredCombat|" + key + "|" + Safe(reason), now, SnapshotLogInterval,
                $"VANGUARD_EXTERNAL_RELEASE_DEFERRED botProfile={key}; reason={Safe(reason)}; combatReason={Safe(combatProtectionReason)}; mutation=false; orbitResume=false; patrolUnpause=false; doctrine=medical_or_movement_release_cannot_reactivate_external_driver_inside_sain_window; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; adapterTag={StatusTag}");
            return deferred;
        }

        bool had = SuppressedByBotProfileId.TryGetValue(key, out var state);
        if (had)
        {
            SuppressedByBotProfileId.Remove(key);
        }

        var parts = new List<string>(8)
        {
            "hadState=" + Bool(had)
        };
        if (botOwner != null)
        {
            object? mover = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Mover");
            bool medicalRelease = reason.IndexOf("medical", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("surgery", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("fracture", StringComparison.OrdinalIgnoreCase) >= 0;

            parts.Add("sprintFalse=" + Bool(TryInvoke(mover, "Sprint", false)));
            if (medicalRelease)
            {
                // Vanguard: ending a medical lease must not hand the Operator back to patrol/ORBIT.
                // Clear every residual movement driver and leave the next scheduler cycle to commit
                // either combat, cohesion or a new medical action. Passive medical debt is not authority.
                var cleanup = new List<string>(12);
                ApplyPathPreempt(botOwner, cleanup);
                parts.Add("pathCleanup=" + Safe(string.Join(",", cleanup)));
                parts.Add("vanguardCommand=" + Safe(VanguardReturnMovementCommandStore.Clear(botProfileId ?? string.Empty, "medical_release:" + Safe(reason))));
                parts.Add("moverPauseKept=" + Bool(TrySetPropertyOrField(mover, "Pause", true)));
                parts.Add("patrolUnpause=false_scheduler_reacquire");
                parts.Add("orbitAgentResume=false_scheduler_reacquire");
            }
            else
            {
                parts.Add("moverPauseFalse=" + Bool(TrySetPropertyOrField(mover, "Pause", false)));
                parts.Add("patrolUnpause=" + Bool(TryInvoke(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "PatrollingData"), "Unpause")));

                bool shouldResumeOrbitAgent = (had && state != null && state.OrbitAgentWasActive != false) || ShouldResumeOrbitAgent(botOwner);
                if (shouldResumeOrbitAgent)
                {
                    parts.Add("orbitAgentResume=" + Bool(TrySetOrbitAgentActive(botProfileId, true, out _)));
                }
                else
                {
                    parts.Add("orbitAgentResume=false_no_prior_active_agent_or_layer");
                }
            }
        }
        else
        {
            parts.Add("botOwner=false");
        }

        string summary = "externalRelease=" + string.Join(",", parts);
        LogThrottled("release|" + key + "|" + Safe(reason), now, SnapshotLogInterval, $"VANGUARD_EXTERNAL_AUTHORITY_RELEASED botProfile={key}; reason={Safe(reason)}; {summary}; tag={StatusTag}");
        return summary;
    }

    public static string ReleaseOrchestratorCombatAuthority(BotOwner? botOwner, string? botProfileId, DateTimeOffset now, string reason)
    {
        return ReleaseMedicalPreempt(botOwner, botProfileId, now, "combat_window_release:" + Safe(reason));
    }

    public static string DescribeActivity(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        return ReadActivity(botOwner, snapshot, now).Summary;
    }


    public static VanguardExternalPreemptResult RefreshMedicalAuthorityHold(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, string reason, TimeSpan ttl, DateTimeOffset now)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        var result = RequestMedicalPreempt(botOwner, snapshot, "medical_authority_hold:" + Safe(reason), ttl, now);
        string logName = result.IsCombatDefer
            ? "VANGUARD_MEDICAL_AUTHORITY_HOLD_DEFERRED_COMBAT"
            : result.CanDriveMovement
                ? "VANGUARD_MEDICAL_AUTHORITY_HOLD_REFRESHED"
                : "VANGUARD_MEDICAL_AUTHORITY_HOLD_SUPPRESSING_EXTERNAL";
        LogThrottled("authorityHold|" + Safe(snapshot.BotProfileId) + "|" + result.Outcome, now, PreemptLogInterval,
            $"{logName} operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; ttl={ttl.TotalSeconds:0.00}; {result.Summary}; patientOnly=true; surgeryOrPostUseProtected=true; noOrbitLootReacquire=true; tag={MedicalAuthorityHoldStatusTag}; adapterTag={StatusTag}; movementTag={MovementPreemptStatusTag}");
        return result;
    }


    public static VanguardExternalPreemptResult RefreshHardMedicalProcedureAuthority(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, string reason, DateTimeOffset now)
    {
        var result = RequestMedicalPreempt(botOwner, snapshot, "hard_medical_procedure:" + Safe(reason), TimeSpan.FromSeconds(45.00d), now);
        string logName = result.IsCombatDefer
            ? "VANGUARD_MEDICAL_HARD_PROCEDURE_DEFERRED_COMBAT"
            : result.CanDriveMovement
                ? "VANGUARD_MEDICAL_HARD_PROCEDURE_AUTHORITY_REFRESHED"
                : "VANGUARD_MEDICAL_HARD_PROCEDURE_SUPPRESSING_EXTERNAL";
        LogThrottledLazy("hardProcedure|" + Safe(snapshot.BotProfileId) + "|" + result.Outcome, now, HardProcedureRefreshLogInterval, VanguardAuditLevel.Diagnostic, () =>
            $"{logName} operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; ttl=45.00; {result.CompactSummary}; patientOnly=true; fullActivityPayload=false; releaseCondition=target_resolved_or_true_threat_or_retry_cap_no_effect_or_max_window; noOrbitLootReacquire=true; tag={MedicalHardProcedureAuthorityStatusTag}; adapterTag={StatusTag}; completionGateTag={MedicalProcedureCompletionGateStatusTag}");
        LogThrottledLazy("hardProcedureTrace|" + Safe(snapshot.BotProfileId) + "|" + result.Outcome, now, HardProcedureRefreshLogInterval, VanguardAuditLevel.Trace, () =>
            $"{logName}_TRACE botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; {result.Summary}; tag={MedicalHardProcedureAuthorityStatusTag}; adapterTag={StatusTag}");
        return result;
    }

    public static bool TrySuppressExternalDuringStationaryMedicalAction(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, VanguardExecutionLeaseState lease, string reason, DateTimeOffset now, out string summary)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        var activity = ReadActivity(botOwner, snapshot, now, log: false, reason: "stationary_hold_" + Safe(reason));
        string compactActivity = "externalActivity=owner=" + activity.MovementOwner
            + ";blocking=" + Bool(activity.BlocksMedicalPrepare)
            + ";combatOwned=" + Bool(activity.IsCombatOwned)
            + ";moving=" + Bool(activity.MoverMoving)
            + ";reason=" + Safe(activity.BlockingReason);
        bool hardSurgeryHold = IsHardSurgeryHoldReason(reason, lease);
        string compactLease = "lease=" + Safe(lease.LeaseId)
            + ";operator=" + Safe(lease.OperatorId)
            + ";botProfile=" + Safe(lease.BotProfileId)
            + ";window=" + Safe(lease.WindowKind)
            + ";need=" + lease.MedicalNeed
            + ";target=" + Safe(lease.TargetPart);
        bool trueSurgeryAbortThreat = snapshot.Medical.Safety.EnemyCanShoot
            || snapshot.Medical.Safety.IncomingFireRecent
            || snapshot.Threat.EnemyCanShoot == true
            || snapshot.ThreatScan.CandidateCanShoot;
        if (activity.IsCombatOwned && (!hardSurgeryHold || trueSurgeryAbortThreat))
        {
            summary = "stationaryHold=deferred_by_combat_owner;" + compactActivity;
            LogThrottled("stationaryHoldCombat|" + Safe(snapshot.BotProfileId) + "|" + Safe(reason), now, PreemptLogInterval,
                $"VANGUARD_MEDICAL_AUTHORITY_HOLD_DEFERRED_COMBAT {compactLease}; reason={Safe(reason)}; noMutation=true; next=yield_to_sain_combat; tag={MedicalAuthorityHoldStatusTag}; combatGateTag={CombatAwareGateStatusTag}");
            return false;
        }

        if (activity.IsCombatOwned && hardSurgeryHold && !trueSurgeryAbortThreat)
        {
            LogThrottled("stationaryHoldCombatIgnored|" + Safe(snapshot.BotProfileId) + "|" + Safe(reason), now, PreemptLogInterval,
                $"VANGUARD_MEDICAL_SURGERY_HARD_LOCK_IGNORED_COMBAT_OWNER {compactLease}; reason={Safe(reason)}; enemyCanShoot={Bool(snapshot.Medical.Safety.EnemyCanShoot)}; incomingFire={Bool(snapshot.Medical.Safety.IncomingFireRecent)}; onlyEnemyFireOrCanShootCanAbort=true; continueSuppression=true; tag=VANGUARD_MEDICAL_HARD_LOCK_ABORT_GATE_OK; surgeryHardHoldTag={MedicalSurgeryHardHoldStatusTag}");
        }

        if (!activity.BlocksMedicalPrepare && !activity.MoverMoving && !hardSurgeryHold)
        {
            summary = "stationaryHold=clear;" + compactActivity;
            return true;
        }

        // Runtime invariant: a clear snapshot is not enough during an active CMS/Surv12 lease.
        // Runtime qualification showed ORBIT assigning a new LooseLoot objective while
        // Vanguard still logged surgical_kit_using_heartbeat. Therefore every hard
        // surgery hold tick actively re-applies patient-only ORBIT/Looting/path
        // suppression instead of returning a passive clear state.
        var result = RefreshHardMedicalProcedureAuthority(botOwner, snapshot, reason, now);
        summary = (hardSurgeryHold ? "stationaryHold=hard_surgery_forced_refresh;" : "stationaryHold=suppressed;") + result.CompactSummary;
        LogThrottledLazy("stationaryHoldSuppress|" + Safe(snapshot.BotProfileId) + "|" + result.Outcome, now, PreemptLogInterval, VanguardAuditLevel.Trace, () =>
            $"VANGUARD_MEDICAL_STATIONARY_EXTERNAL_SUPPRESSED {compactLease}; reason={Safe(reason)}; outcome={result.Outcome}; canDriveMovement={Bool(result.CanDriveMovement)}; hardSurgeryHold={Bool(hardSurgeryHold)}; keepSurgeryWindow=true; patientOnly=true; fullExternalPayload=false; tag={MedicalAuthorityHoldStatusTag}; adapterTag={StatusTag}");
        if (hardSurgeryHold && result.Outcome != VanguardExternalPreemptOutcome.RejectedCombatOwner && result.Outcome != VanguardExternalPreemptOutcome.FailedBotOwnerMissing)
        {
            LogThrottledLazy("hardSurgeryHoldRefresh|" + Safe(snapshot.BotProfileId), now, PreemptLogInterval, VanguardAuditLevel.Trace, () =>
                $"VANGUARD_MEDICAL_SURGERY_HARD_HOLD_REFRESHED {compactLease}; reason={Safe(reason)}; outcome={result.Outcome}; passiveClearForbidden=true; externalDriversSuppressedEveryTick=true; fullExternalPayload=false; tag={MedicalSurgeryHardHoldStatusTag}; adapterTag={StatusTag}");
            return true;
        }

        return result.Outcome != VanguardExternalPreemptOutcome.RejectedCombatOwner
            && result.Outcome != VanguardExternalPreemptOutcome.FailedBotOwnerMissing
            && result.Outcome != VanguardExternalPreemptOutcome.FailedNoAuthority;
    }

    private static bool IsHardSurgeryHoldReason(string? reason, VanguardExecutionLeaseState lease)
    {
        string text = (reason ?? string.Empty) + "|" + (lease?.WindowKind ?? string.Empty) + "|" + (lease?.IntentKey ?? string.Empty);
        return text.IndexOf("StationaryMedicalSurgery", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("surgery_using", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("direct_chain_pre_apply", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("surgery_post_use", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("hard_procedure", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("surgery_reapply", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static VanguardExternalMovementOwner ClassifyOwner(bool lootingActive, bool lootTaskRunning, bool hasActiveLootable, bool orbitSemanticActive, bool orbitLayerIdleQuiesced, bool pathActive, bool directThreatLikely, bool sainExtractLikely, bool sainCombatLikely, bool sainCombatStaleNonActionable)
    {
        if (directThreatLikely || (sainCombatLikely && !sainCombatStaleNonActionable))
        {
            return VanguardExternalMovementOwner.SainCombat;
        }

        if (sainExtractLikely)
        {
            return VanguardExternalMovementOwner.SainExtract;
        }

        if (lootingActive || lootTaskRunning || hasActiveLootable)
        {
            return VanguardExternalMovementOwner.LootingBots;
        }

        if (orbitSemanticActive && !orbitLayerIdleQuiesced)
        {
            return VanguardExternalMovementOwner.Orbit;
        }

        if (pathActive)
        {
            return VanguardExternalMovementOwner.ExternalPathResidue;
        }

        return VanguardExternalMovementOwner.VanguardOrIdle;
    }


    private static VanguardExternalPreemptOutcome ClassifyOrbitAuthorityQuiesceOutcome(OperatorDecisionSnapshot snapshot, VanguardExternalActivitySnapshot after)
    {
        if (after == null || !after.BotOwnerPresent)
        {
            return VanguardExternalPreemptOutcome.FailedBotOwnerMissing;
        }

        if (after.DirectThreatLikely || (after.SainCombatLikely && !after.SainCombatStaleNonActionable) || VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot) || VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(snapshot))
        {
            return VanguardExternalPreemptOutcome.RejectedCombatOwner;
        }

        if (after.LootingBotsActive || after.LootingBotsTaskRunning || after.LootingBotsHasActiveLootable || VanguardMovementAuthorityDoctrine.HasCriticalLootActivity(snapshot))
        {
            return VanguardExternalPreemptOutcome.FailedLootingBotsStillActive;
        }

        // Vanguard doctrine: ORBIT/path residue is not allowed to veto Vanguard travel/follow cohesion.
        // If there is no true combat or critical loot, Vanguard may drive movement even when reflection
        // could not fully clear an ORBIT semantic token or an EFT path residue during this tick.
        return VanguardExternalPreemptOutcome.Granted;
    }

    private static void LogOrbitAuthorityQuiesce(OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason, VanguardExternalPreemptResult result, string policy)
    {
        LogThrottled("orbitAuthorityQuiesce|" + Normalize(snapshot.BotProfileId) + "|" + Safe(reason) + "|" + result.Outcome, now, PreemptLogInterval,
            $"VANGUARD_ORBIT_AUTHORITY_QUIESCE operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; policy={Safe(policy)}; requestReason={Safe(reason)}; outcome={result.Outcome}; canDriveMovement={Bool(result.CanDriveMovement)}; distance={snapshot.SquadCohesion.OperatorDistanceToOwner:0.0}; orbit={Bool(snapshot.Orbit.Active)}; path={Bool(snapshot.Movement.HasPath == true)}; lootCritical={Bool(VanguardMovementAuthorityDoctrine.HasCriticalLootActivity(snapshot))}; {result.Summary}; tag={OrbitAuthorityQuiesceStatusTag}; adapterTag={StatusTag}; movementTag={MovementPreemptStatusTag}");
    }

    private static VanguardExternalPreemptOutcome ClassifySainBoundaryPreemptOutcome(VanguardExternalActivitySnapshot after)
    {
        if (after.DirectThreatLikely)
        {
            return VanguardExternalPreemptOutcome.RejectedCombatOwner;
        }

        if (!after.BlocksMedicalPrepare && !after.EftPathActive)
        {
            return VanguardExternalPreemptOutcome.Granted;
        }

        if (after.IsPathResidue)
        {
            return VanguardExternalPreemptOutcome.FailedPathStillActive;
        }

        if (after.LootingBotsActive || after.LootingBotsTaskRunning || after.LootingBotsHasActiveLootable)
        {
            return VanguardExternalPreemptOutcome.FailedLootingBotsStillActive;
        }

        if (IsOrbitBlockingForHardReturn(after))
        {
            return VanguardExternalPreemptOutcome.FailedOrbitStillActive;
        }

        return VanguardExternalPreemptOutcome.Pending;
    }

    private static VanguardExternalPreemptOutcome ClassifyGrenadeEmergencyPreemptOutcome(
        VanguardExternalActivitySnapshot after,
        bool directPathAuthorityAcquired)
    {
        if (after.LootingBotsActive || after.LootingBotsTaskRunning || after.LootingBotsHasActiveLootable)
        {
            return VanguardExternalPreemptOutcome.FailedLootingBotsStillActive;
        }
        if (IsOrbitBlockingForHardReturn(after))
        {
            return VanguardExternalPreemptOutcome.FailedOrbitStillActive;
        }
        return directPathAuthorityAcquired
            ? VanguardExternalPreemptOutcome.Granted
            : VanguardExternalPreemptOutcome.FailedPathStillActive;
    }

    private static VanguardExternalPreemptOutcome ClassifyPreemptOutcome(VanguardExternalActivitySnapshot before, VanguardExternalActivitySnapshot after, bool allowResidualCombatMedicalOverride = false)
    {
        if (after.IsCombatOwned && !allowResidualCombatMedicalOverride)
        {
            return VanguardExternalPreemptOutcome.RejectedCombatOwner;
        }

        if (!after.BlocksMedicalPrepare)
        {
            return VanguardExternalPreemptOutcome.Granted;
        }

        if (after.LootingBotsActive || after.LootingBotsTaskRunning || after.LootingBotsHasActiveLootable)
        {
            return VanguardExternalPreemptOutcome.FailedLootingBotsStillActive;
        }

        if (IsOrbitBlockingForHardReturn(after))
        {
            return VanguardExternalPreemptOutcome.FailedOrbitStillActive;
        }

        if (after.IsPathResidue)
        {
            return VanguardExternalPreemptOutcome.FailedPathStillActive;
        }

        if (after.MoverMoving)
        {
            return VanguardExternalPreemptOutcome.FailedMoverBusy;
        }

        return before.BlocksMedicalPrepare ? VanguardExternalPreemptOutcome.Pending : VanguardExternalPreemptOutcome.Granted;
    }

    private static VanguardExternalPreemptOutcome ClassifyMovementContinuationPreemptOutcome(VanguardExternalActivitySnapshot after, bool allowActiveVanguardPathResidue, bool isolatedCombatRelease)
    {
        if (after.DirectThreatLikely && !isolatedCombatRelease)
        {
            return VanguardExternalPreemptOutcome.RejectedCombatOwner;
        }

        if (after.LootingBotsActive || after.LootingBotsTaskRunning || after.LootingBotsHasActiveLootable)
        {
            return VanguardExternalPreemptOutcome.FailedLootingBotsStillActive;
        }

        if (IsOrbitBlockingForHardReturn(after))
        {
            return VanguardExternalPreemptOutcome.FailedOrbitStillActive;
        }

        if (!allowActiveVanguardPathResidue && after.IsPathResidue)
        {
            return VanguardExternalPreemptOutcome.FailedPathStillActive;
        }

        if (!allowActiveVanguardPathResidue && after.MoverMoving)
        {
            return VanguardExternalPreemptOutcome.FailedMoverBusy;
        }

        return VanguardExternalPreemptOutcome.Granted;
    }

    private static bool IsOrbitBlockingForHardReturn(VanguardExternalActivitySnapshot after)
    {
        if (after == null || after.OrbitLayerIdleQuiesced)
        {
            return false;
        }

        bool orbitLooksActive = after.OrbitSemanticActive || after.IsOrbitObjectiveResidue;
        if (!orbitLooksActive)
        {
            return false;
        }

        bool nonDrivingOrbitResidue = after.OrbitBrainLayerActive
            && !after.DirectThreatLikely
            && !after.LootingBotsActive
            && !after.LootingBotsTaskRunning
            && !after.LootingBotsHasActiveLootable
            && !after.MoverMoving;
        return !nonDrivingOrbitResidue;
    }

    private static string BuildBlockingReason(VanguardExternalMovementOwner owner, bool lootingActive, bool lootTaskRunning, bool hasActiveLootable, bool orbitLayerActive, bool orbitLayerIdleQuiesced, bool orbitSemanticActive, bool orbitActive, string orbitStatus, string orbitCategory, string orbitClassification, string orbitExtractReason, bool pathActive, float? pathDistance)
    {
        if (owner == VanguardExternalMovementOwner.SainCombat)
        {
            return "sain_combat_or_direct_threat";
        }

        if (owner == VanguardExternalMovementOwner.SainExtract)
        {
            return "sain_autonomous_extract_veto_pending";
        }

        if (lootingActive)
        {
            return "looting_bots_active";
        }

        if (lootTaskRunning || hasActiveLootable)
        {
            return "loot_task_or_objective_active";
        }

        if (orbitLayerActive && orbitLayerIdleQuiesced)
        {
            return "orbit_brain_layer_idle_quiesced";
        }

        if (orbitLayerActive && orbitSemanticActive)
        {
            return "orbit_brain_layer_active";
        }

        string orbit = (orbitStatus + "|" + orbitCategory + "|" + orbitClassification + "|" + orbitExtractReason).ToLowerInvariant();
        if (orbitActive && IsOrbitSemanticActive(orbitActive, orbitStatus, orbitCategory, orbitClassification, orbitExtractReason))
        {
            return "orbit_objective_residue:" + Safe(orbit);
        }

        if (pathActive)
        {
            return "external_path_residue:dist=" + (pathDistance.HasValue ? pathDistance.Value.ToString("0.00", CultureInfo.InvariantCulture) : "unknown");
        }

        return "inactive";
    }

    private static bool IsIsolatedCombatReleaseReason(string? reason)
    {
        return !string.IsNullOrWhiteSpace(reason)
            && reason.IndexOf("isolated_combat_release", StringComparison.OrdinalIgnoreCase) >= 0;
    }


    private static ExternalSuppressionState GetOrCreateSuppressionState(OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason, TimeSpan ttl)
    {
        string key = Normalize(snapshot.BotProfileId);
        if (!SuppressedByBotProfileId.TryGetValue(key, out var state))
        {
            state = new ExternalSuppressionState
            {
                BotProfileId = snapshot.BotProfileId,
                StartedAtUtc = now,
                OrbitAgentWasActive = null
            };
            SuppressedByBotProfileId[key] = state;
        }

        var requestedUntil = now + ttl;
        state.ExpiresAtUtc = state.ExpiresAtUtc > requestedUntil ? state.ExpiresAtUtc : requestedUntil;
        state.LastReason = reason;
        return state;
    }

    private static void ApplySainSearchBoundaryPreempt(BotOwner botOwner, TimeSpan ttl, List<string> mutations)
    {
        object? sainComponent = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "SAIN.Components.BotComponent");
        object? bot = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainComponent, "Bot");
        object? decision = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainComponent, "Decision")
            ?? VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(bot, "Decision");
        object? mover = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainComponent, "Mover")
            ?? VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(bot, "Mover");
        object? activePath = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "ActivePath");
        object? manualShoot = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainComponent, "ManualShoot")
            ?? VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(bot, "ManualShoot");
        object? suppression = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainComponent, "Suppression")
            ?? VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(bot, "Suppression");

        mutations.Add("sainComponent=" + Bool(sainComponent != null));
        mutations.Add("sainDecisionReset=" + Bool(TryInvoke(decision, "ResetDecisions", true) || TryInvoke(decision, "ResetDecisions", false) || TryInvoke(decision, "Reset")));
        mutations.Add("sainMoverStop=" + Bool(TryInvoke(mover, "Stop")));
        mutations.Add("sainActivePathCancel=" + Bool(TryInvoke(activePath, "Cancel", 0.10f) || TryInvoke(activePath, "Cancel")));
        mutations.Add("sainManualShootReset=" + Bool(TryInvoke(manualShoot, "Reset")));
        mutations.Add("sainSuppressReset=" + Bool(TryInvoke(suppression, "ResetSuppressing")));
        mutations.Add("boundaryTtl=" + Math.Max(1.0d, ttl.TotalSeconds).ToString("0.00", CultureInfo.InvariantCulture));
    }

    private static void ApplyLootingBotsPreempt(BotOwner botOwner, TimeSpan ttl, List<string> mutations, bool cleanupActiveState = true)
    {
        bool external = false;
        Type? externalType = VanguardOperatorRuntimeAuditReflection.FindType("LootingBots.External");
        if (externalType != null)
        {
            object? result = VanguardOperatorRuntimeAuditReflection.InvokeStatic(externalType, "PreventBotFromLooting", botOwner, (float)Math.Max(1.0d, ttl.TotalSeconds));
            external = result is bool b && b;
        }
        mutations.Add("lootingExternalPrevent=" + Bool(external));
        if (!cleanupActiveState)
        {
            mutations.Add("lootingCleanupSkipped=inactive_snapshot");
            return;
        }

        object? lootingBrain = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "LootingBots.Components.LootingBrain");
        if (lootingBrain != null)
        {
            mutations.Add("lootingBrainStop=" + Bool(TryInvoke(lootingBrain, "StopLooting")));
            mutations.Add("lootingBrainCleanup=" + Bool(TryInvoke(lootingBrain, "CleanupLoot", false, true) || TryInvoke(lootingBrain, "CleanupLoot", false) || TryInvoke(lootingBrain, "Cleanup")));
            mutations.Add("lootingBrainActiveLootClear=" + Bool(TrySetPropertyOrField(lootingBrain, "ActiveLoot", null)));
        }
        else
        {
            mutations.Add("lootingBrain=none");
        }

        object? lootFinder = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "LootingBots.Components.LootFinder");
        if (lootFinder != null)
        {
            mutations.Add("lootFinderStop=" + Bool(TryInvoke(lootFinder, "StopFindingLoot")));
            mutations.Add("lootFinderClear=" + Bool(TryInvoke(lootFinder, "Clear") || TryInvoke(lootFinder, "Reset")));
            mutations.Add("lootFinderOverrideScan=" + Bool(TryInvoke(lootFinder, "OverrideNextScanTime", (float)Math.Max(1.0d, ttl.TotalSeconds))));
        }
        else
        {
            mutations.Add("lootFinder=none");
        }
    }

    private static void ApplyOrbitPreempt(BotOwner botOwner, OperatorDecisionSnapshot snapshot, ExternalSuppressionState state, List<string> mutations)
    {
        if (VanguardOrbitAuthorityBoundaryService.ShouldSkipPreempt(botOwner, snapshot, DateTimeOffset.UtcNow, out _))
        {
            // Confirmation is logged once by the boundary service. Do not append a per-acquisition
            // no-op mutation: the objective is to remove both reflection cost and misleading ORBIT chatter.
            return;
        }

        object? orbitLoot = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "Orbit.Looting.OrbitLootHandler");
        if (orbitLoot != null)
        {
            mutations.Add("orbitLootCancel=" + Bool(TryInvoke(orbitLoot, "Cancel")));
            mutations.Add("orbitLootStop=" + Bool(TryInvoke(orbitLoot, "StopLooting")));
            mutations.Add("orbitLootClear=" + Bool(TryInvoke(orbitLoot, "Clear") || TryInvoke(orbitLoot, "Reset") || TryInvoke(orbitLoot, "ClearObjective") || TryInvoke(orbitLoot, "ResetObjective")));
        }
        else
        {
            mutations.Add("orbitLoot=none");
        }

        mutations.Add("orbitObjectiveClear=" + Bool(ClearOrbitObjectiveFields(botOwner)));
        bool agentFound = TryGetOrbitAgent(snapshot.BotProfileId, out var agent, out var agentLookup);
        if (agentFound)
        {
            if (!state.OrbitAgentWasActive.HasValue)
            {
                state.OrbitAgentWasActive = StringBool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(agent, "IsActive"));
            }

            mutations.Add("orbitAgentActiveFalse=" + Bool(TrySetPropertyOrField(agent, "IsActive", false)));
            object? objective = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(agent, "Objective");
            object? movement = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(agent, "Movement");
            object? guard = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(agent, "Guard");
            mutations.Add("orbitAgentObjectiveClear=" + Bool(ClearOrbitObjectiveObject(objective)));
            mutations.Add("orbitAgentMovementClear=" + Bool(ClearOrbitMovementObject(movement, botOwner)));
            mutations.Add("orbitAgentGuardClear=" + Bool(ClearOrbitGuardObject(guard)));
        }
        else
        {
            mutations.Add("orbitAgent=none:" + Safe(agentLookup));
        }
    }

    private static void ApplyCombatMovementHandoff(BotOwner botOwner, OperatorDecisionSnapshot snapshot, List<string> mutations, string reason)
    {
        // Vanguard: combat handoff must remove Vanguard/vanilla patrol interference without zeroing the mover.
        // SAIN remains sovereign over movement once combat is the primary domain.
        mutations.Add("combatMovementHandoff=" + Safe(reason));
        mutations.Add("moverStopSkipped=true");
        mutations.Add("targetSpeedZeroSkipped=true");
        object? patrolling = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "PatrollingData");
        mutations.Add("patrolStop=" + Bool(TryInvoke(patrolling, "Stop") || TryInvoke(patrolling, "Cancel") || TryInvoke(patrolling, "Clear")));
        mutations.Add("patrolPause=" + Bool(TryInvoke(patrolling, "Pause") || TrySetPropertyOrField(patrolling, "Paused", true) || TrySetPropertyOrField(patrolling, "Pause", true)));
        mutations.Add("patrolUnpauseSkippedForSain=true");
        mutations.Add("SainMovementDriver=true");
        mutations.Add("driverTag=" + VanguardMovementAuthorityDoctrine.DriverDominanceStatusTag);
        mutations.Add("vanguardMovementDriverSuspendedForCombat=true");
        mutations.Add("sainCombatMovementPreserved=true");
    }

    private static bool ApplyGrenadeEmergencyPathPreempt(BotOwner botOwner, List<string> mutations)
    {
        bool goToStopped = false;
        bool moverStopped = false;
        bool sprintStopped = false;
        try
        {
            if (botOwner.GoToSomePointData != null)
            {
                botOwner.GoToSomePointData.UpdateToGo(false);
                goToStopped = true;
            }
        }
        catch
        {
            // The direct mover stop below remains the decisive path authority boundary.
        }
        try
        {
            if (botOwner.Mover != null)
            {
                botOwner.Mover.Stop();
                moverStopped = true;
            }
        }
        catch
        {
            // Reported through the returned authority result.
        }
        try
        {
            botOwner.Sprint(false, true);
            botOwner.Mover?.Sprint(false, false);
            sprintStopped = true;
        }
        catch
        {
            // Sprint state is secondary to path replacement.
        }

        // Keep the reflection path cleanup as a compatibility supplement for wrappers that expose a
        // separate ActivePath object, but do not let a stale same-tick snapshot override the direct
        // backend stop proof above.
        ApplyPathPreempt(botOwner, mutations);
        mutations.Add("directGoToSomePointStop=" + Bool(goToStopped));
        mutations.Add("directMoverStop=" + Bool(moverStopped));
        mutations.Add("directSprintStop=" + Bool(sprintStopped));
        mutations.Add("pathAuthorityProof=" + Bool(goToStopped || moverStopped));
        return goToStopped || moverStopped;
    }

    private static void ApplyPathPreempt(BotOwner botOwner, List<string> mutations)
    {
        object? mover = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Mover");
        object? activePath = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "ActivePath");
        mutations.Add("activePathCancel=" + Bool(TryInvoke(activePath, "Cancel", 0.1f) || TryInvoke(activePath, "Cancel") || TryInvoke(activePath, "Stop") || TryInvoke(activePath, "Clear")));
        mutations.Add("moverStop=" + Bool(TryInvoke(mover, "Stop") || TryInvoke(mover, "StopMove") || TryInvoke(mover, "StopMoving") || TryInvoke(mover, "Cancel") || TryInvoke(mover, "ClearPath")));
        mutations.Add("moverSprintFalse=" + Bool(TryInvoke(mover, "Sprint", false)));
        mutations.Add("moverPauseFalse=" + Bool(TrySetPropertyOrField(mover, "Pause", false) || TrySetPropertyOrField(mover, "Paused", false)));
        mutations.Add("targetSpeedZero=" + Bool(TryInvoke(mover, "SetTargetMoveSpeed", 0f) || TryInvoke(mover, "SetTargetSpeed", 0f) || TryInvoke(botOwner, "SetTargetMoveSpeed", 0f)));
        object? patrolling = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "PatrollingData");
        mutations.Add("patrolStop=" + Bool(TryInvoke(patrolling, "Stop") || TryInvoke(patrolling, "Cancel") || TryInvoke(patrolling, "Clear")));
        mutations.Add("patrolPause=" + Bool(TryInvoke(patrolling, "Pause") || TrySetPropertyOrField(patrolling, "Paused", true) || TrySetPropertyOrField(patrolling, "Pause", true)));
        mutations.Add("patrolUnpauseSkippedForVanguardDriver=true");
        mutations.Add("VanguardMovementDriver=true");
        mutations.Add("driverTag=" + VanguardMovementAuthorityDoctrine.DriverDominanceStatusTag);
    }

    private static bool ClearOrbitObjectiveFields(BotOwner botOwner)
    {
        bool changed = false;
        changed |= TrySetPropertyOrField(botOwner, "OrbitObjective", null);
        changed |= TrySetPropertyOrField(botOwner, "CurrentOrbitObjective", null);
        changed |= TrySetPropertyOrField(botOwner, "CurrentObjective", null);
        changed |= TrySetPropertyOrField(botOwner, "LootObjective", null);
        object? player = VanguardOperatorRuntimeAuditReflection.GetMember(botOwner, "GetPlayer", "Player");
        changed |= TrySetPropertyOrField(player, "OrbitObjective", null);
        changed |= TrySetPropertyOrField(player, "CurrentOrbitObjective", null);
        changed |= TrySetPropertyOrField(player, "CurrentObjective", null);
        return changed;
    }

    private static bool ClearOrbitObjectiveObject(object? objective)
    {
        if (objective == null)
        {
            return false;
        }

        bool changed = false;
        changed |= TrySetEnumPropertyOrField(objective, "Status", "None");
        changed |= TrySetPropertyOrField(objective, "Location", null);
        changed |= TrySetPropertyOrField(objective, "ArrivalPath", null);
        changed |= TrySetPropertyOrField(objective, "SplinterParent", null);
        changed |= TrySetPropertyOrField(objective, "DispatchTime", 0f);
        changed |= TryInvoke(objective, "Clear") || TryInvoke(objective, "Reset");
        return changed;
    }

    private static bool ClearOrbitMovementObject(object? movement, BotOwner botOwner)
    {
        if (movement == null)
        {
            return false;
        }

        bool changed = false;
        changed |= TrySetEnumPropertyOrField(movement, "Status", "Stopped");
        changed |= TrySetPropertyOrField(movement, "Path", null);
        changed |= TrySetPropertyOrField(movement, "CurrentCorner", 0);
        changed |= TrySetPropertyOrField(movement, "Retry", 0);
        changed |= TrySetPropertyOrField(movement, "Sprint", false);
        changed |= TrySetPropertyOrField(movement, "Speed", 0f);
        changed |= TrySetPropertyOrField(movement, "Target", GetBotPosition(botOwner));
        return changed;
    }

    private static bool ClearOrbitGuardObject(object? guard)
    {
        if (guard == null)
        {
            return false;
        }

        bool changed = false;
        changed |= TrySetEnumPropertyOrField(guard, "Status", "None");
        changed |= TrySetPropertyOrField(guard, "CoverPoint", null);
        changed |= TrySetPropertyOrField(guard, "AreaSweepJob", null);
        changed |= TrySetPropertyOrField(guard, "WatchTimeout", 0f);
        return changed;
    }


    private static bool IsOrbitSemanticActive(bool orbitActive, string orbitStatus, string orbitCategory, string orbitClassification, string orbitExtractReason)
    {
        if (!orbitActive)
        {
            return false;
        }

        string orbit = (orbitStatus + "|" + orbitCategory + "|" + orbitClassification + "|" + orbitExtractReason).ToLowerInvariant();
        bool hasActiveObjectiveToken = orbit.Contains("loot")
            || orbit.Contains("corpse")
            || orbit.Contains("container")
            || orbit.Contains("loose")
            || orbit.Contains("moving")
            || orbit.Contains("extract")
            || orbit.Contains("orbit_moving")
            || orbit.Contains("objective");
        bool finishedOrIdle = orbit.Contains("idle")
            || orbit.Contains("quiesc")
            || orbit.Contains("finished")
            || orbit.Contains("complete")
            || orbit.Contains("completed")
            || orbit.Contains("done")
            || orbit.Contains("success")
            || orbit.Contains("failed");

        // Vanguard: ORBIT finished/idle residue is not movement authority.  It must not block a
        // HardReturn continuation unless it still carries an active loot/move/extract/objective token.
        return hasActiveObjectiveToken && !finishedOrIdle;
    }

    private static bool TryReadOrbitTelemetry(string? botProfileId, out string summary, out string status, out string category, out Vector3? objective, out string extractReason)
    {
        summary = "none";
        status = "none";
        category = "none";
        objective = null;
        extractReason = "none";
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            summary = "profile_missing";
            return false;
        }

        Type? telemetry = VanguardOperatorRuntimeAuditReflection.FindType("Orbit.Api.OrbitTelemetry");
        if (telemetry == null)
        {
            summary = "telemetry_type_missing";
            return false;
        }

        object? objectiveDto = VanguardOperatorRuntimeAuditReflection.InvokeStatic(telemetry, "GetBotObjective", botProfileId);
        if (objectiveDto == null)
        {
            summary = "telemetry_no_objective";
            return false;
        }

        status = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objectiveDto, "Status"));
        category = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objectiveDto, "Category"));
        extractReason = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objectiveDto, "ExtractReason"));
        float? x = FloatValue(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objectiveDto, "ObjectiveX"));
        float? y = FloatValue(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objectiveDto, "ObjectiveY"));
        float? z = FloatValue(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objectiveDto, "ObjectiveZ"));
        if (x.HasValue && y.HasValue && z.HasValue)
        {
            objective = new Vector3(x.Value, y.Value, z.Value);
        }

        summary = "telemetry:" + Safe(status) + ":" + Safe(category);
        return true;
    }

    private static bool TryGetOrbitAgent(string? botProfileId, out object? agent, out string diagnostic)
    {
        agent = null;
        diagnostic = "none";
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            diagnostic = "profile_missing";
            return false;
        }

        object? manager = TryGetOrbitManager();
        if (manager == null)
        {
            diagnostic = "manager_missing";
            return false;
        }

        object? agentData = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(manager, "AgentData");
        object? entities = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(agentData, "Entities");
        object? values = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(entities, "Values");
        if (values is not IEnumerable enumerable)
        {
            diagnostic = "agent_values_missing";
            return false;
        }

        foreach (var candidate in enumerable)
        {
            string profile = Text(VanguardOperatorRuntimeAuditReflection.GetDeep(candidate, "Player", "ProfileId"));
            if (string.Equals(profile, botProfileId, StringComparison.OrdinalIgnoreCase))
            {
                agent = candidate;
                diagnostic = "agent_by_player_profile";
                return true;
            }

            profile = Text(VanguardOperatorRuntimeAuditReflection.GetDeep(candidate, "Bot", "Profile", "Id"));
            if (string.Equals(profile, botProfileId, StringComparison.OrdinalIgnoreCase))
            {
                agent = candidate;
                diagnostic = "agent_by_bot_profile";
                return true;
            }
        }

        diagnostic = "agent_not_found";
        return false;
    }

    private static object? TryGetOrbitManager()
    {
        try
        {
            Type? managerType = VanguardOperatorRuntimeAuditReflection.FindType("Orbit.Core.OrbitManager");
            Type? singletonType = VanguardOperatorRuntimeAuditReflection.FindType("Comfort.Common.Singleton`1");
            if (managerType == null || singletonType == null)
            {
                return null;
            }

            Type closed = singletonType.MakeGenericType(managerType);
            return VanguardOperatorRuntimeAuditReflection.GetStaticMember(closed, "Instance");
        }
        catch
        {
            return null;
        }
    }

    private static bool TrySetOrbitAgentActive(string? botProfileId, bool active, out string diagnostic)
    {
        diagnostic = "none";
        if (!TryGetOrbitAgent(botProfileId, out var agent, out diagnostic) || agent == null)
        {
            return false;
        }

        return TrySetPropertyOrField(agent, "IsActive", active);
    }

    private static bool ShouldResumeOrbitAgent(BotOwner botOwner)
    {
        object? brain = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Brain");
        object? baseBrain = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(brain, "BaseBrain");
        object? activeLayer = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(baseBrain, "ActiveLayer", "CurrentLayer");
        string layerName = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activeLayer, "Name"));
        if (string.Equals(layerName, "none", StringComparison.OrdinalIgnoreCase))
        {
            layerName = Text(VanguardOperatorRuntimeAuditReflection.InvokeNoArg(activeLayer, "Name"));
        }

        return Contains(layerName, "orbit");
    }

    private static Vector3 GetBotPosition(BotOwner botOwner)
    {
        object? player = VanguardOperatorRuntimeAuditReflection.GetMember(botOwner, "GetPlayer", "Player");
        object? transform = VanguardOperatorRuntimeAuditReflection.GetDeep(player, "PlayerBones", "BodyTransform");
        object? position = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(transform, "position");
        return position is Vector3 vector ? vector : Vector3.zero;
    }



    private static void LogMovementContinuationPreempt(OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason, VanguardExternalPreemptResult result, bool includeSainBoundarySuppression, bool allowActiveVanguardPathResidue)
    {
        string logName = result.Outcome == VanguardExternalPreemptOutcome.Granted
            ? "VANGUARD_RETURN_CONTINUATION_PREEMPT_GRANTED"
            : result.Outcome == VanguardExternalPreemptOutcome.Pending
                ? "VANGUARD_RETURN_CONTINUATION_PREEMPT_PENDING"
                : result.Outcome == VanguardExternalPreemptOutcome.RejectedCombatOwner
                    ? "VANGUARD_RETURN_CONTINUATION_DEFERRED_COMBAT"
                    : "VANGUARD_RETURN_CONTINUATION_PREEMPT_FAILED";
        LogThrottled("movementContinuationPreempt|" + Safe(snapshot.BotProfileId) + "|" + Safe(reason) + "|" + result.Outcome, now, PreemptLogInterval,
            $"{logName} operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requestReason={Safe(reason)}; {result.Summary}; includeSainBoundarySuppression={Bool(includeSainBoundarySuppression)}; allowActiveVanguardPathResidue={Bool(allowActiveVanguardPathResidue)}; startEligibilityNotReused=true; orbitIdleQuiescedNonBlocking=true; tag={ReturnContinuationStatusTag}; hardReturnTag={MovementHardReturnSuppressStatusTag}; sainSearchTag={SainSearchSuppressStatusTag}; adapterTag={StatusTag}");
    }

    private static void LogSainBoundaryPreempt(OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason, VanguardExternalPreemptResult result)
    {
        string logName = result.Outcome == VanguardExternalPreemptOutcome.Granted
            ? "VANGUARD_SAIN_SEARCH_SUPPRESS_GRANTED"
            : result.Outcome == VanguardExternalPreemptOutcome.Pending
                ? "VANGUARD_SAIN_SEARCH_SUPPRESS_PENDING"
                : result.Outcome == VanguardExternalPreemptOutcome.RejectedCombatOwner
                    ? "VANGUARD_SAIN_SEARCH_SUPPRESS_DEFERRED_COMBAT"
                    : "VANGUARD_SAIN_SEARCH_SUPPRESS_FAILED";
        LogThrottled("sainBoundaryPreempt|" + Safe(snapshot.BotProfileId) + "|" + Safe(reason) + "|" + result.Outcome, now, PreemptLogInterval,
            $"{logName} operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requestReason={Safe(reason)}; {result.Summary}; boundaryOnly=true; hardOutside=true; staleOrNonActionable=true; tag={SainSearchSuppressStatusTag}; adapterTag={StatusTag}; hardReturnTag={MovementHardReturnSuppressStatusTag}; noMultiApply=true");
    }

    private static void LogMovementHardReturnPreempt(OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason, VanguardExternalPreemptResult result)
    {
        string logName = result.Outcome == VanguardExternalPreemptOutcome.Granted
            ? "VANGUARD_EXTERNAL_SUPPRESS_GRANTED"
            : result.Outcome == VanguardExternalPreemptOutcome.Pending
                ? "VANGUARD_EXTERNAL_SUPPRESS_PENDING"
                : result.Outcome == VanguardExternalPreemptOutcome.RejectedCombatOwner
                    ? "VANGUARD_EXTERNAL_SUPPRESS_DEFERRED_COMBAT"
                    : "VANGUARD_EXTERNAL_SUPPRESS_FAILED";
        LogThrottled("movementHardReturnPreempt|" + Safe(snapshot.BotProfileId) + "|" + Safe(reason) + "|" + result.Outcome, now, PreemptLogInterval,
            $"{logName} operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requestReason={Safe(reason)}; {result.Summary}; patientOnly=true; movementOnly=true; tag={MovementHardReturnSuppressStatusTag}; adapterTag={StatusTag}; noMultiApply=true");
    }

    private static void LogPreempt(OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason, VanguardExternalPreemptResult result)
    {
        string logName = result.Outcome == VanguardExternalPreemptOutcome.Granted
            ? "VANGUARD_EXTERNAL_AUTHORITY_GRANTED"
            : result.Outcome == VanguardExternalPreemptOutcome.Pending
                ? "VANGUARD_EXTERNAL_AUTHORITY_PENDING"
                : result.Outcome == VanguardExternalPreemptOutcome.RejectedCombatOwner
                    ? "VANGUARD_EXTERNAL_AUTHORITY_DEFERRED_COMBAT_OWNER"
                    : "VANGUARD_EXTERNAL_AUTHORITY_FAILED";
        string primaryTag = result.Outcome == VanguardExternalPreemptOutcome.RejectedCombatOwner ? CombatAwareGateStatusTag : StatusTag;
        // Runtime invariant: operational medical logs retain typed ownership/outcome data without serializing
        // both complete activity snapshots on the headless thread. Full snapshots remain available
        // through explicit activity diagnostics and the phase profiler.
        LogThrottled("preempt|" + Safe(snapshot.BotProfileId) + "|" + Safe(reason) + "|" + result.Outcome, now, PreemptLogInterval, $"{logName} operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; requestReason={Safe(reason)}; {result.CompactSummary}; patientOnly=true; tag={primaryTag}; adapterTag={StatusTag}; movementTag={MovementPreemptStatusTag}; typedFailureTag={TypedCoverFailureStatusTag}");
    }

    private static void LogThrottled(string key, DateTimeOffset now, TimeSpan interval, string message)
    {
        if (LastLogAtByKey.TryGetValue(key, out var last) && now - last < interval)
        {
            return;
        }

        LastLogAtByKey[key] = now;
        VanguardClientDiagnosticsLog.Info(StatusTag, message);
    }

    private static void LogThrottledLazy(
        string key,
        DateTimeOffset now,
        TimeSpan interval,
        VanguardAuditLevel minimumLevel,
        Func<string> messageFactory)
    {
        if (!VanguardClientDiagnosticsLog.IsEnabled(minimumLevel))
        {
            return;
        }

        if (LastLogAtByKey.TryGetValue(key, out var last) && now - last < interval)
        {
            return;
        }

        LastLogAtByKey[key] = now;
        VanguardClientDiagnosticsLog.Info(StatusTag, minimumLevel, messageFactory);
    }

    private static bool TryInvoke(object? target, string methodName, params object?[] args)
    {
        if (target == null)
        {
            return false;
        }

        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            foreach (var method in target.GetType().GetMethods(flags))
            {
                if (!string.Equals(method.Name, methodName, StringComparison.Ordinal) || method.GetParameters().Length != args.Length)
                {
                    continue;
                }

                method.Invoke(target, args);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TrySetPropertyOrField(object? target, string name, object? value)
    {
        if (target == null)
        {
            return false;
        }

        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var type = target.GetType();
            var property = type.GetProperty(name, flags);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, value);
                return true;
            }

            var field = type.GetField(name, flags);
            if (field != null)
            {
                field.SetValue(target, value);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TrySetEnumPropertyOrField(object? target, string name, string enumName)
    {
        if (target == null)
        {
            return false;
        }

        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var type = target.GetType();
            var property = type.GetProperty(name, flags);
            if (property != null && property.CanWrite && property.PropertyType.IsEnum)
            {
                property.SetValue(target, Enum.Parse(property.PropertyType, enumName));
                return true;
            }

            var field = type.GetField(name, flags);
            if (field != null && field.FieldType.IsEnum)
            {
                field.SetValue(target, Enum.Parse(field.FieldType, enumName));
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool? StringBool(object? value)
    {
        if (value is bool b)
        {
            return b;
        }

        if (value == null)
        {
            return null;
        }

        string text = value.ToString() ?? string.Empty;
        if (bool.TryParse(text, out var parsed))
        {
            return parsed;
        }

        if (!string.Equals(text, "none", StringComparison.OrdinalIgnoreCase) && !string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return null;
    }

    private static float? FloatValue(object? value)
    {
        try
        {
            if (value == null)
            {
                return null;
            }

            return Convert.ToSingle(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static string Text(object? value)
    {
        return VanguardOperatorRuntimeAuditReflection.Text(value);
    }

    private static bool Contains(string? text, string needle)
    {
        return !string.IsNullOrWhiteSpace(text) && text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().ToLowerInvariant();
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private sealed class ExternalSuppressionState
    {
        public string BotProfileId = "none";
        public DateTimeOffset StartedAtUtc;
        public DateTimeOffset ExpiresAtUtc;
        public bool? OrbitAgentWasActive;
        public string LastReason = "none";
        public DateTimeOffset LastMedicalMutationAtUtc = DateTimeOffset.MinValue;
        public VanguardExternalPreemptResult? LastMedicalPreemptResult;
    }
}
#endif
