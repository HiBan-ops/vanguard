#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using EFT;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Authority;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.External;
using Vanguard.Client.Runtime.Movement.Brain;

// Responsibility: Owns temporary medical isolation state while a hard medical procedure needs exclusive hands/movement protection.
// Flow: Procedure start acquires isolation for one Operator/action, active ticks expose that ownership to competing domains, and completion/cancellation releases it through lifecycle-safe reset paths.
// Authority boundary: Isolation protects an already-authorized medical action; it does not create medical need or outrank a genuine emergency cancellation rule.
// Invariant: Isolation is Operator/raid scoped, has an explicit terminal release path, and cannot remain latched after the underlying procedure ends.
namespace Vanguard.Client.Runtime.Medical.Execution;

internal static class VanguardMedicalIsolationController
{
    public const string StatusTag = "VANGUARD_MEDICAL_ISOLATION_OK";
    private const string AuthorityLeaseStatusTag = VanguardOperatorAuthorityLeaseController.StatusTag;
    private const string GoCoverOnlyStatusTag = "VANGUARD_SURGERY_GO_COVER_ONLY_OK";
    private const string HardOrbitExitStatusTag = "VANGUARD_MEDICAL_HARD_ORBIT_EXIT_OK";
    private const string OrbitLocalHoldLockStatusTag = VanguardSurgeryCoverPrepareExecutor.OrbitLocalHoldLockStatusTag;
    private const string ExternalAuthorityAdapterStatusTag = VanguardExternalAuthorityAdapter.StatusTag;
    private const string ExternalMovementPreemptStatusTag = VanguardExternalAuthorityAdapter.MovementPreemptStatusTag;
    private const string CombatAwareGateStatusTag = VanguardExternalAuthorityAdapter.CombatAwareGateStatusTag;
    private const string OrbitLayerQuiesceStatusTag = VanguardExternalAuthorityAdapter.OrbitLayerQuiesceStatusTag;
    private const string CoverArrivalGrantStatusTag = VanguardExternalAuthorityAdapter.CoverArrivalGrantStatusTag;
    private const string MedicalAuthorityHoldStatusTag = VanguardExternalAuthorityAdapter.MedicalAuthorityHoldStatusTag;
    private const string MedicalCoverCommitStatusTag = VanguardExternalAuthorityAdapter.MedicalCoverCommitStatusTag;
    private const string MedicalHardProcedureAuthorityStatusTag = VanguardExternalAuthorityAdapter.MedicalHardProcedureAuthorityStatusTag;
    private const string MedicalProcedureCompletionGateStatusTag = VanguardExternalAuthorityAdapter.MedicalProcedureCompletionGateStatusTag;

    private static readonly TimeSpan QuiesceTimeout = TimeSpan.FromSeconds(45.00d);
    private static readonly TimeSpan QuiesceRetryInterval = TimeSpan.FromSeconds(0.45d);
    private static readonly TimeSpan ReadyTtl = TimeSpan.FromSeconds(45.00d);
    private static readonly TimeSpan IsolationMaxDuration = TimeSpan.FromSeconds(65.00d);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(1.50d);
    private const float SurgeryCrouchPoseLevel = 0.00f;
    private const float SurgeryObservedCrouchPoseMax = 0.20f;
    private const float SurgeryObservedSpeedMax = 0.35f;
    private static readonly TimeSpan SurgeryStationarySettleDuration = TimeSpan.FromSeconds(0.75d);
    private static readonly Dictionary<string, IsolationState> IsolationByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogAtByKey = new(StringComparer.OrdinalIgnoreCase);

    public static void Reset(string reason)
    {
        IsolationByBotProfileId.Clear();
        LastLogAtByKey.Clear();
        VanguardOperatorAuthorityLeaseController.Reset(reason);
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_MEDICAL_ISOLATION_RESET reason={Safe(reason)}; patientOnly=true; externalBrainQuiesce=true; singleMovementAuthority=true; strictOrbitPathResidueLock=true; hardOrbitExit=true; combatOwnerDeferStrict=true; orbitLayerIdleQuiesce=true; coverArrivalGrant=true; medicalAuthorityHold=true; coverCommit=true; releaseRequired=true; authorityTag={AuthorityLeaseStatusTag}; externalAdapterTag={ExternalAuthorityAdapterStatusTag}; externalMovementTag={ExternalMovementPreemptStatusTag}; combatGateTag={CombatAwareGateStatusTag}; orbitLayerTag={OrbitLayerQuiesceStatusTag}; coverArrivalTag={CoverArrivalGrantStatusTag}; authorityHoldTag={MedicalAuthorityHoldStatusTag}; coverCommitTag={MedicalCoverCommitStatusTag}; hardOrbitExitTag={HardOrbitExitStatusTag}; goCoverOnlyTag={GoCoverOnlyStatusTag}; orbitLockTag={OrbitLocalHoldLockStatusTag}; tag={StatusTag}");
    }

    public static VanguardMedicalIsolationTickResult BeginOrUpdatePrepareIsolation(VanguardExecutionLeaseState lease, BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        if (lease == null || string.IsNullOrWhiteSpace(lease.BotProfileId))
        {
            return VanguardMedicalIsolationTickResult.Fail("lease_or_bot_profile_missing", "isolation=failed");
        }

        if (botOwner == null)
        {
            return VanguardMedicalIsolationTickResult.Fail("botowner_missing", "isolation=failed;botOwner=false");
        }

        if (HasCriticalThreat(snapshot, out var threatReason))
        {
            Release(lease.BotProfileId, botOwner, now, "critical_threat_before_or_during_isolation:" + threatReason, keepReady: false);
            return VanguardMedicalIsolationTickResult.Fail("critical_threat:" + threatReason, "isolation=failed;criticalThreat=" + Safe(threatReason));
        }

        if (VanguardExternalAuthorityAdapter.ShouldDeferMedicalMovementForCombat(botOwner, snapshot, now, "medical_isolation_prepare", out _, out var combatGateSummary))
        {
            Release(lease.BotProfileId, botOwner, now, "deferred_by_combat_owner", keepReady: false);
            lease.MedicalIsolationPhase = "DeferredByCombatOwner";
            lease.MedicalIsolationAcquired = false;
            lease.LastMedicalIsolationSummary = combatGateSummary;
            LogThrottled(Normalize(lease.BotProfileId) + "|defer_combat", now, $"VANGUARD_MEDICAL_PREPARE_DEFERRED_BY_COMBAT {lease.Summary}; phase=DeferredByCombatOwner; {combatGateSummary}; isolationAcquired=false; canDriveMovement=false; next=yield_to_sain_combat; patientOnly=true; tag={CombatAwareGateStatusTag}; isolationTag={StatusTag}");
            LogThrottled(Normalize(lease.BotProfileId) + "|not_acquired_combat", now, $"VANGUARD_MEDICAL_ISOLATION_NOT_ACQUIRED {lease.Summary}; reason=deferred_by_combat_owner; phase=DeferredByCombatOwner; {combatGateSummary}; noGoCover=true; noStationaryFallback=true; tag={StatusTag}; combatGateTag={CombatAwareGateStatusTag}");
            return VanguardMedicalIsolationTickResult.Fail("deferred_by_combat_owner", "isolation=deferred;reason=deferred_by_combat_owner;" + combatGateSummary);
        }

        string authoritySummary = VanguardOperatorAuthorityLeaseController.StartOrRefreshMedical(lease, botOwner, snapshot, now, "surgery_go_cover_window");

        string key = Normalize(lease.BotProfileId);
        if (!IsolationByBotProfileId.TryGetValue(key, out var state) || state.ExpiresAtUtc <= now || !SameMedicalTarget(state, lease))
        {
            state = new IsolationState
            {
                OperatorId = lease.OperatorId,
                BotProfileId = lease.BotProfileId,
                OwnerLeaseId = lease.LeaseId,
                TargetPart = lease.TargetPart,
                ItemTemplateId = lease.ItemTemplateId,
                Phase = "QuiescingExternalSystems",
                StartedAtUtc = now,
                LastProgressAtUtc = now,
                LastQuiesceAtUtc = DateTimeOffset.MinValue,
                ExpiresAtUtc = now + IsolationMaxDuration
            };
            IsolationByBotProfileId[key] = state;
            lease.MedicalIsolationStartedAtUtc = now;
            lease.MedicalIsolationPhase = state.Phase;
            lease.MedicalIsolationAcquired = false;
            lease.LastProgressKind = "medical_isolation_started";
            LogThrottled(key + "|start", now, $"VANGUARD_MEDICAL_ISOLATION_STARTED {lease.Summary}; phase={state.Phase}; target={Safe(lease.TargetPart)}; item={Safe(lease.ItemName)}; max={IsolationMaxDuration.TotalSeconds:0.00}; quiesceTimeout={QuiesceTimeout.TotalSeconds:0.00}; patientOnly=true; {authoritySummary}; tag={StatusTag}");
        }

        if (now - state.StartedAtUtc > IsolationMaxDuration)
        {
            Release(lease.BotProfileId, botOwner, now, "isolation_max_duration_expired", keepReady: false);
            return VanguardMedicalIsolationTickResult.Fail("isolation_max_duration_expired", "isolation=failed;phase=" + Safe(state.Phase));
        }

        if (string.Equals(state.Phase, "ReadyForMedicalAction", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state.Phase, "MovingToCover", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state.Phase, "ArrivedAtCover", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state.Phase, "StabilizingPosture", StringComparison.OrdinalIgnoreCase))
        {
            lease.MedicalIsolationPhase = state.Phase;
            lease.MedicalIsolationAcquired = true;
            return VanguardMedicalIsolationTickResult.MoveAllowed("isolation=acquired;phase=" + Safe(state.Phase) + ";external=" + SnapshotExternalSummary(snapshot));
        }

        if (now - state.LastQuiesceAtUtc >= QuiesceRetryInterval)
        {
            state.LastQuiesceAtUtc = now;
            var quiesceSummary = QuiesceExternalSystems(botOwner, snapshot, now, "prepare_isolation");
            lease.LastMedicalIsolationSummary = quiesceSummary;
            LogThrottled(key + "|quiesce", now, $"VANGUARD_MEDICAL_ISOLATION_QUIESCE {lease.Summary}; phase={state.Phase}; {quiesceSummary}; externalBefore={SnapshotExternalSummary(snapshot)}; externalAdapterTag={ExternalAuthorityAdapterStatusTag}; tag={StatusTag}");
        }

        if (IsExternalBrainStillActive(snapshot, out var externalReason))
        {
            lease.MedicalIsolationPhase = state.Phase;
            lease.MedicalIsolationAcquired = false;
            lease.LastProgressKind = "medical_authority_suppressing_external";
            lease.LastProgressAtUtc = now;
            lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(1.25d);

            bool authorityActive = VanguardOperatorAuthorityLeaseController.HasActiveMedicalAuthority(lease.BotProfileId, lease.TargetPart, lease.ItemTemplateId, now, out var activeAuthoritySummary);
            if (!authorityActive)
            {
                Release(lease.BotProfileId, botOwner, now, "authority_missing_while_external_active:" + externalReason, keepReady: false);
                return VanguardMedicalIsolationTickResult.Fail("authority_missing_while_external_active:" + externalReason, "isolation=failed;external=" + Safe(externalReason));
            }

            var elapsed = now - state.StartedAtUtc;
            var hardProcedureRefresh = VanguardExternalAuthorityAdapter.RefreshHardMedicalProcedureAuthority(botOwner, snapshot, "prepare_external_still_blocking:" + Safe(externalReason), now);
            LogThrottled(key + "|override_external", now, $"VANGUARD_AUTHORITY_EXTERNAL_STILL_BLOCKING {lease.Summary}; external={Safe(externalReason)}; elapsed={elapsed.TotalSeconds:0.00}; timeout={QuiesceTimeout.TotalSeconds:0.00}; hardProcedure=true; {activeAuthoritySummary}; {hardProcedureRefresh.CompactSummary}; next=continue_suppress_until_quiesced_or_long_timeout; tag={AuthorityLeaseStatusTag}; hardOrbitExitTag={HardOrbitExitStatusTag}; orbitLayerTag={OrbitLayerQuiesceStatusTag}; hardProcedureTag={MedicalHardProcedureAuthorityStatusTag}");

            if (elapsed >= IsolationMaxDuration)
            {
                Release(lease.BotProfileId, botOwner, now, "hard_procedure_isolation_timeout:" + externalReason, keepReady: false);
                return VanguardMedicalIsolationTickResult.Fail("hard_procedure_isolation_timeout:" + externalReason, "isolation=failed;external=" + Safe(externalReason) + ";elapsed=" + elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + ";tag=" + MedicalHardProcedureAuthorityStatusTag);
            }

            return VanguardMedicalIsolationTickResult.Wait("isolation=await_external_authority_quiesce;external=" + Safe(externalReason) + ";elapsed=" + elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + ";timeout=" + QuiesceTimeout.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + ";hardProcedure=true;" + activeAuthoritySummary + ";phase=" + Safe(state.Phase));
        }

        state.Phase = "ExternalAuthorityQuiescedConfirmed";
        state.LastProgressAtUtc = now;
        lease.MedicalIsolationPhase = state.Phase;
        lease.MedicalIsolationAcquired = false;
        lease.LastProgressKind = "medical_external_authority_quiesced_confirmed";
        LogThrottled(key + "|quiesced", now, $"VANGUARD_MEDICAL_EXTERNAL_QUIESCE_CONFIRMED {lease.Summary}; phase={state.Phase}; external={SnapshotExternalSummary(snapshot)}; hardOrbitExit=true; orbitLayerIdleAllowed=true; externalAdapterTag={ExternalAuthorityAdapterStatusTag}; orbitLayerTag={OrbitLayerQuiesceStatusTag}; tag={HardOrbitExitStatusTag}");

        state.Phase = "MovingToCover";
        state.LastProgressAtUtc = now;
        lease.MedicalIsolationPhase = state.Phase;
        lease.MedicalIsolationAcquired = true;
        lease.LastProgressAtUtc = now;
        lease.LastProgressKind = "medical_isolation_acquired";
        lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(3.50d);
        LogThrottled(key + "|acquired", now, $"VANGUARD_MEDICAL_ISOLATION_ACQUIRED {lease.Summary}; phase={state.Phase}; external={SnapshotExternalSummary(snapshot)}; singleMovementAuthority=true; strictOrbitPathResidueLock=true; orbitLayerIdleAllowed=true; next=move_to_cover; {authoritySummary}; orbitLockTag={OrbitLocalHoldLockStatusTag}; externalAdapterTag={ExternalAuthorityAdapterStatusTag}; orbitLayerTag={OrbitLayerQuiesceStatusTag}; coverArrivalTag={CoverArrivalGrantStatusTag}; tag={StatusTag}");
        return VanguardMedicalIsolationTickResult.MoveAllowed("isolation=acquired;phase=MovingToCover;external=inactive");
    }

    public static bool HasCompatibleStationaryIsolation(
        string? botProfileId,
        string? targetPart,
        string? itemTemplateId,
        DateTimeOffset now,
        out string reason)
    {
        reason = "none";
        string key = Normalize(botProfileId);
        if (!IsolationByBotProfileId.TryGetValue(key, out var state))
        {
            reason = "isolation_state_missing";
            return false;
        }

        if (state.ExpiresAtUtc <= now)
        {
            reason = "isolation_state_expired";
            return false;
        }

        if (!string.Equals(Normalize(state.TargetPart), Normalize(targetPart), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Normalize(state.ItemTemplateId), Normalize(itemTemplateId), StringComparison.OrdinalIgnoreCase))
        {
            reason = "isolation_target_mismatch:stateTarget=" + Safe(state.TargetPart)
                + ":requestedTarget=" + Safe(targetPart)
                + ":stateItem=" + Safe(state.ItemTemplateId)
                + ":requestedItem=" + Safe(itemTemplateId);
            return false;
        }

        bool stationaryReady = string.Equals(state.Phase, "ReadyForMedicalAction", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state.Phase, "ArrivedAtCover", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state.Phase, "StabilizingPosture", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state.Phase, "ExecutingMedicalAction", StringComparison.OrdinalIgnoreCase);
        if (!stationaryReady)
        {
            reason = "isolation_phase_not_stationary_ready:" + Safe(state.Phase);
            return false;
        }

        reason = "compatible_stationary_isolation:phase=" + Safe(state.Phase)
            + ":expires=" + state.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture);
        return true;
    }

    public static bool TryBeginStationaryMedicalAction(VanguardExecutionLeaseState lease, BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string summary)
    {
        summary = "isolation=none";
        if (lease == null || string.IsNullOrWhiteSpace(lease.BotProfileId) || botOwner == null)
        {
            summary = "isolation=failed;reason=lease_or_botowner_missing";
            return false;
        }

        if (HasCriticalThreat(snapshot, out var threatReason))
        {
            Release(lease.BotProfileId, botOwner, now, "critical_threat_before_stationary_action:" + threatReason, keepReady: false);
            summary = "isolation=failed;reason=critical_threat:" + Safe(threatReason);
            return false;
        }

        string key = Normalize(lease.BotProfileId);
        if (!IsolationByBotProfileId.TryGetValue(key, out var state) || state.ExpiresAtUtc <= now || !SameMedicalTarget(state, lease))
        {
            summary = "isolation=failed;reason=go_cover_required_no_direct_stationary_isolation";
            return false;
        }

        if (!string.Equals(state.Phase, "ReadyForMedicalAction", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state.Phase, "ArrivedAtCover", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state.Phase, "StabilizingPosture", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state.Phase, "ExecutingMedicalAction", StringComparison.OrdinalIgnoreCase))
        {
            summary = "isolation=failed;reason=phase_not_ready:" + Safe(state.Phase);
            return false;
        }

        if (VanguardExternalAuthorityAdapter.ShouldDeferMedicalMovementForCombat(botOwner, snapshot, now, "stationary_medical_action", out _, out var stationaryCombatSummary))
        {
            Release(lease.BotProfileId, botOwner, now, "stationary_action_deferred_by_combat_owner", keepReady: false);
            summary = "isolation=failed;reason=deferred_by_combat_owner;" + stationaryCombatSummary;
            return false;
        }

        if (IsExternalBrainStillActive(snapshot, out var externalReason))
        {
            bool authorityActive = VanguardOperatorAuthorityLeaseController.HasActiveMedicalAuthority(lease.BotProfileId, lease.TargetPart, lease.ItemTemplateId, now, out var activeAuthoritySummary);
            if (!authorityActive)
            {
                summary = "isolation=failed;reason=authority_missing_during_stationary_recheck:" + Safe(externalReason);
                return false;
            }

            if (!VanguardExternalAuthorityAdapter.TrySuppressExternalDuringStationaryMedicalAction(botOwner, snapshot, lease, "stationary_action_recheck:" + Safe(externalReason), now, out var stationarySuppressionSummary))
            {
                summary = "isolation=failed;reason=stationary_external_suppression_failed:" + Safe(externalReason) + ";" + stationarySuppressionSummary;
                return false;
            }

            state.ExpiresAtUtc = now + IsolationMaxDuration;
            state.LastProgressAtUtc = now;
            summary = "isolation=authority_hold_refreshed;" + activeAuthoritySummary + ";" + stationarySuppressionSummary;
        }

        string movementCommandClear = VanguardReturnMovementCommandStore.Clear(lease.BotProfileId, "stationary_medical_action_begin");
        if (!TryStabilizeStationaryPosture(botOwner, out var postureSummary))
        {
            summary = "isolation=failed;reason=posture_stabilization_unavailable;movementCommand=" + Safe(movementCommandClear) + ";" + postureSummary;
            return false;
        }

        if (!TryObserveStationaryPosture(botOwner, snapshot, out var observedPostureSummary))
        {
            state.Phase = "StabilizingPosture";
            state.StationaryObservedSinceUtc = DateTimeOffset.MinValue;
            state.LastProgressAtUtc = now;
            state.ExpiresAtUtc = now + IsolationMaxDuration;
            lease.MedicalIsolationPhase = state.Phase;
            lease.MedicalIsolationAcquired = true;
            lease.LastProgressAtUtc = now;
            lease.LastProgressKind = "stationary_posture_stabilizing";
            lease.LastMedicalIsolationSummary = postureSummary + ";" + observedPostureSummary;
            lease.NoProgressUntilUtc = now + TimeSpan.FromSeconds(1.25d);
            LogThrottled(key + "|posture_wait", now, $"VANGUARD_MEDICAL_STATIONARY_POSTURE_WAIT {lease.Summary}; phase={state.Phase}; {postureSummary}; {observedPostureSummary}; next=retry_direct_chain_after_crouch_observed; noRelease=true; patientOnly=true; tag={StatusTag}; postureRetryTag=VANGUARD_MEDICAL_POSTURE_RETRY_OK");
            summary = "isolation=failed;reason=posture_not_ready;movementCommand=" + Safe(movementCommandClear) + ";" + postureSummary + ";" + observedPostureSummary;
            return false;
        }

        if (state.StationaryObservedSinceUtc == DateTimeOffset.MinValue)
        {
            state.StationaryObservedSinceUtc = now;
            state.Phase = "StabilizingPosture";
            state.LastProgressAtUtc = now;
            lease.MedicalIsolationPhase = state.Phase;
            lease.LastProgressKind = "stationary_settle_started";
            lease.NoProgressUntilUtc = now + SurgeryStationarySettleDuration;
            summary = "isolation=failed;reason=stationary_settle_started;movementCommand=" + Safe(movementCommandClear) + ";" + postureSummary + ";" + observedPostureSummary;
            return false;
        }

        TimeSpan stationaryFor = now - state.StationaryObservedSinceUtc;
        if (stationaryFor < SurgeryStationarySettleDuration)
        {
            lease.MedicalIsolationPhase = "StabilizingPosture";
            lease.LastProgressKind = "stationary_settle_waiting";
            lease.NoProgressUntilUtc = state.StationaryObservedSinceUtc + SurgeryStationarySettleDuration;
            summary = "isolation=failed;reason=stationary_settle_waiting;stationaryFor=" + stationaryFor.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + ";movementCommand=" + Safe(movementCommandClear) + ";" + postureSummary + ";" + observedPostureSummary;
            return false;
        }

        lease.StationaryPostureObserved = true;
        lease.SurgeryStationaryAnchorCaptured = true;
        lease.SurgeryStationaryAnchorX = botOwner.Position.x;
        lease.SurgeryStationaryAnchorZ = botOwner.Position.z;
        state.Phase = "ExecutingMedicalAction";
        state.LastProgressAtUtc = now;
        state.ExpiresAtUtc = now + IsolationMaxDuration;
        lease.MedicalIsolationPhase = state.Phase;
        lease.MedicalIsolationAcquired = true;
        lease.LastMedicalIsolationSummary = postureSummary + ";" + observedPostureSummary;
        LogThrottled(key + "|execute", now, $"VANGUARD_MEDICAL_ISOLATION_EXECUTING {lease.Summary}; phase={state.Phase}; {postureSummary}; {observedPostureSummary}; external={SnapshotExternalSummary(snapshot)}; postureConfirmed=true; tag={StatusTag}; postureRetryTag=VANGUARD_MEDICAL_POSTURE_RETRY_OK");
        summary = "isolation=executing;phase=ExecutingMedicalAction;stationaryFor=" + stationaryFor.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + ";movementCommand=" + Safe(movementCommandClear) + ";" + postureSummary + ";" + observedPostureSummary;
        return true;
    }

    public static void MarkCoverReady(VanguardExecutionLeaseState lease, BotOwner? botOwner, DateTimeOffset now, string reason)
    {
        if (lease == null || string.IsNullOrWhiteSpace(lease.BotProfileId))
        {
            return;
        }

        string key = Normalize(lease.BotProfileId);
        if (!IsolationByBotProfileId.TryGetValue(key, out var state))
        {
            return;
        }

        state.Phase = "ReadyForMedicalAction";
        state.LastProgressAtUtc = now;
        state.ReadyAtUtc = now;
        state.CoverCommitUntilUtc = now + ReadyTtl;
        state.CoverCommitReason = reason;
        state.ExpiresAtUtc = now + ReadyTtl;
        lease.MedicalIsolationPhase = state.Phase;
        lease.MedicalIsolationAcquired = true;
        lease.LastMedicalIsolationSummary = "coverCommit=" + Safe(reason) + ";until=" + state.CoverCommitUntilUtc.ToString("O", CultureInfo.InvariantCulture);
        TryStabilizeStationaryPosture(botOwner, out var postureSummary);
        TryObserveStationaryPosture(botOwner, new OperatorDecisionSnapshot { BotProfileId = lease.BotProfileId, OperatorId = lease.OperatorId }, out var observedPostureSummary);
        LogThrottled(key + "|ready", now, $"VANGUARD_MEDICAL_ISOLATION_READY {lease.Summary}; reason={Safe(reason)}; phase={state.Phase}; ttl={ReadyTtl.TotalSeconds:0.00}; coverCommitUntil={state.CoverCommitUntilUtc:O}; {postureSummary}; {observedPostureSummary}; next=stationary_medical_action; releaseCondition=target_resolved_or_true_threat_or_retry_cap_no_effect_or_max_window; tag={StatusTag}; coverArrivalTag={CoverArrivalGrantStatusTag}; coverCommitTag={MedicalCoverCommitStatusTag}; hardProcedureTag={MedicalHardProcedureAuthorityStatusTag}; postureRetryTag=VANGUARD_MEDICAL_POSTURE_RETRY_OK");
        LogThrottled(key + "|cover_commit", now, $"VANGUARD_MEDICAL_COVER_COMMITTED {lease.Summary}; reason={Safe(reason)}; phase={state.Phase}; commitUntil={state.CoverCommitUntilUtc:O}; target={Safe(lease.TargetPart)}; item={Safe(lease.ItemName)}; patientOnly=true; noLocalHold=true; noStationaryFallback=true; releaseCondition=target_resolved_or_true_threat_or_retry_cap_no_effect_or_max_window; next=StationaryMedicalSurgery; tag={MedicalCoverCommitStatusTag}; isolationTag={StatusTag}; authorityHoldTag={MedicalAuthorityHoldStatusTag}; hardProcedureTag={MedicalHardProcedureAuthorityStatusTag}");
    }

    public static void MarkCoverMovementProgress(VanguardExecutionLeaseState lease, DateTimeOffset now, string progressKind)
    {
        if (lease == null || string.IsNullOrWhiteSpace(lease.BotProfileId))
        {
            return;
        }

        if (IsolationByBotProfileId.TryGetValue(Normalize(lease.BotProfileId), out var state))
        {
            state.Phase = "MovingToCover";
            state.LastProgressAtUtc = now;
            lease.MedicalIsolationPhase = state.Phase;
            lease.MedicalIsolationAcquired = true;
            lease.LastMedicalIsolationSummary = Safe(progressKind);
        }
    }


    public static bool RefreshStationaryMedicalHold(VanguardExecutionLeaseState lease, BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason, out string summary)
    {
        summary = "stationaryHold=none";
        if (lease == null || string.IsNullOrWhiteSpace(lease.BotProfileId) || botOwner == null)
        {
            summary = "stationaryHold=failed;reason=lease_or_botowner_missing";
            return false;
        }

        string key = Normalize(lease.BotProfileId);
        if (!IsolationByBotProfileId.TryGetValue(key, out var state) || !SameMedicalTarget(state, lease))
        {
            summary = "stationaryHold=failed;reason=isolation_state_missing";
            return false;
        }

        if (HasCriticalThreat(snapshot, out var threatReason))
        {
            summary = "stationaryHold=failed;reason=critical_threat:" + Safe(threatReason);
            return false;
        }

        bool authorityActive = VanguardOperatorAuthorityLeaseController.HasActiveMedicalAuthority(lease.BotProfileId, lease.TargetPart, lease.ItemTemplateId, now, out var activeAuthoritySummary);
        if (!authorityActive)
        {
            summary = "stationaryHold=failed;reason=authority_missing;" + activeAuthoritySummary;
            return false;
        }

        bool surgeryHold = Vanguard.Client.Runtime.Medical.VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(lease.MedicalNeed);
        if (!surgeryHold)
        {
            // Preserve the pre-runtime refresh contract for non-surgical stationary care.
            VanguardExternalAuthorityAdapter.RefreshHardMedicalProcedureAuthority(botOwner, snapshot, "stationary_hold:" + Safe(reason), now);
        }
        // Hard surgery is already force-refreshed inside TrySuppress on every hold tick to prevent
        // ORBIT/LootingBots/path reacquisition. Avoid only the duplicate immediately preceding call.
        if (!VanguardExternalAuthorityAdapter.TrySuppressExternalDuringStationaryMedicalAction(botOwner, snapshot, lease, reason, now, out var externalSummary))
        {
            summary = "stationaryHold=failed;reason=external_suppression_failed;" + externalSummary;
            return false;
        }

        TryStabilizeStationaryPosture(botOwner, out var postureSummary);
        TryObserveStationaryPosture(botOwner, snapshot, out var observedPostureSummary);
        lease.StationaryPostureObserved = lease.StationaryPostureObserved || observedPostureSummary.IndexOf("observed=true", StringComparison.OrdinalIgnoreCase) >= 0;
        state.Phase = "ExecutingMedicalAction";
        state.LastProgressAtUtc = now;
        state.ExpiresAtUtc = now + IsolationMaxDuration;
        lease.MedicalIsolationPhase = state.Phase;
        lease.MedicalIsolationAcquired = true;
        lease.LastMedicalIsolationSummary = "stationaryHoldRefreshed=" + Safe(reason) + ";" + postureSummary + ";" + observedPostureSummary;
        summary = "stationaryHold=refreshed;" + activeAuthoritySummary + ";" + externalSummary + ";" + postureSummary + ";" + observedPostureSummary;
        LogThrottledLazy(key + "|stationary_hold|" + Safe(reason), now, VanguardAuditLevel.Trace, () =>
            $"VANGUARD_MEDICAL_AUTHORITY_HOLD_REFRESHED lease={Safe(lease.LeaseId)}; operator={Safe(lease.OperatorId)}; botProfile={Safe(lease.BotProfileId)}; window={Safe(lease.WindowKind)}; need={lease.MedicalNeed}; target={Safe(lease.TargetPart)}; reason={Safe(reason)}; phase={state.Phase}; patientOnly=true; fullLeasePayload=false; tag={MedicalAuthorityHoldStatusTag}; isolationTag={StatusTag}");
        return true;
    }

    public static void ReleaseForLease(VanguardExecutionLeaseState lease, BotOwner? botOwner, DateTimeOffset now, string reason)
    {
        if (lease == null)
        {
            return;
        }

        Release(lease.BotProfileId, botOwner, now, reason, keepReady: false);
    }

    private static void Release(string? botProfileId, BotOwner? botOwner, DateTimeOffset now, string reason, bool keepReady)
    {
        string key = Normalize(botProfileId);
        if (keepReady)
        {
            return;
        }

        IsolationByBotProfileId.Remove(key);
        string releaseSummary = ReleaseExternalSystems(botOwner, botProfileId, now, reason);
        if (!string.IsNullOrWhiteSpace(botProfileId))
        {
            var pseudoLease = new VanguardExecutionLeaseState { BotProfileId = botProfileId };
            VanguardOperatorAuthorityLeaseController.ReleaseMedical(pseudoLease, botOwner, now, reason);
        }
        LogThrottled(key + "|release|" + Safe(reason), now, $"VANGUARD_MEDICAL_ISOLATION_RELEASED botProfile={key}; reason={Safe(reason)}; {releaseSummary}; externalAuthority=returned; tag={StatusTag}");
    }


    private static bool TryAcquireDirectStationaryIsolation(VanguardExecutionLeaseState lease, BotOwner botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, out IsolationState state, out string summary)
    {
        state = null!;
        summary = "isolation=failed;reason=go_cover_required_direct_stationary_disabled";
        return false;
    }

    private static bool SameMedicalTarget(IsolationState state, VanguardExecutionLeaseState lease)
    {
        return string.Equals(Normalize(state.TargetPart), Normalize(lease.TargetPart), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Normalize(state.ItemTemplateId), Normalize(lease.ItemTemplateId), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExternalBrainStillActive(OperatorDecisionSnapshot snapshot, out string reason)
    {
        return VanguardExternalAuthorityAdapter.IsMedicalBlockingActivity(null, snapshot, DateTimeOffset.UtcNow, out reason);
    }

    private static bool HasCriticalThreat(OperatorDecisionSnapshot snapshot, out string reason)
    {
        var safety = snapshot.Medical.Safety;
        if (safety.EnemyCanShoot || snapshot.Threat.EnemyCanShoot == true || snapshot.ThreatScan.CandidateCanShoot)
        {
            reason = "enemy_can_shoot";
            return true;
        }

        if (safety.IncomingFireRecent)
        {
            reason = "incoming_fire_recent";
            return true;
        }

        reason = "none";
        return false;
    }

    private static string QuiesceExternalSystems(BotOwner botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason)
    {
        long started = VanguardRuntimePerformanceGuard.Begin();
        var result = VanguardExternalAuthorityAdapter.RefreshHardMedicalProcedureAuthority(botOwner, snapshot, "medical_isolation:" + Safe(reason), now);
        VanguardRuntimePerformanceGuard.End("MedicalIsolationQuiesceExternal", started);
        return result.CompactSummary;
    }

    private static bool TryStabilizeStationaryPosture(BotOwner? botOwner, out string summary)
    {
        var parts = new List<string>(12);
        object? mover = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Mover", "BotMover");
        object? player = VanguardOperatorRuntimeAuditReflection.GetMember(botOwner, "GetPlayer", "Player");
        object? movementContext = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player, "MovementContext");
        object? activePath = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "ActivePath");
        bool cancelPath = TryInvoke(activePath, "Cancel", 0.1f) || TryInvoke(activePath, "Cancel");
        bool speedZero = TryInvoke(mover, "SetTargetMoveSpeed", 0f) || TryInvoke(botOwner, "SetTargetMoveSpeed", 0f);
        bool pause = TryInvoke(mover, "PauseMovement", 3.00f) || TryInvoke(mover, "MovementPause", 3.00f, true);
        bool crouch = TryInvoke(movementContext, "SetPoseLevel", SurgeryCrouchPoseLevel, false)
            || TryInvoke(movementContext, "SetPoseLevel", SurgeryCrouchPoseLevel, true)
            || TrySetPropertyOrField(movementContext, "PoseLevel_1", SurgeryCrouchPoseLevel)
            || TrySetPropertyOrField(movementContext, "PoseLevel", SurgeryCrouchPoseLevel)
            || TryInvoke(mover, "SetPose", SurgeryCrouchPoseLevel)
            || TrySetPropertyOrField(mover, "TargetPose", SurgeryCrouchPoseLevel);
        TrySetPropertyOrField(player, "IsSprintEnabled", false);
        parts.Add("cancelPath=" + Bool(cancelPath));
        parts.Add("speedZero=" + Bool(speedZero));
        parts.Add("pause=" + Bool(pause));
        parts.Add("crouchRequested=" + Bool(crouch));
        parts.Add("targetPose=" + SurgeryCrouchPoseLevel.ToString("0.00", CultureInfo.InvariantCulture));
        summary = "stationaryPosture=" + string.Join(",", parts);
        return cancelPath || speedZero || pause || crouch;
    }

    private static bool TryObserveStationaryPosture(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, out string summary)
    {
        object? mover = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Mover", "BotMover");
        object? player = VanguardOperatorRuntimeAuditReflection.GetMember(botOwner, "GetPlayer", "Player");
        object? movementContext = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player, "MovementContext");
        float speed = Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed);
        float? pose = Float(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(movementContext, "PoseLevel", "PoseLevel_1"));
        float? smoothedPose = Float(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(movementContext, "SmoothedPoseLevel", "SmoothedPoseLevel_1"));
        bool? prone = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(movementContext, "IsInPronePose", "IsInPronePose_1"));
        bool? hasPath = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "HasPathAndNoComplete", "HasPathAndNotComplete"));
        float? dist = Float(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "DistDestination", "SDistDestination"));
        float? targetSpeed = Float(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "DestMoveSpeed", "TargetMoveSpeed", "MoveSpeed", "Speed"));
        bool crouched = pose.HasValue && pose.Value <= SurgeryObservedCrouchPoseMax && prone != true;
        bool speedOk = speed <= SurgeryObservedSpeedMax;
        bool pathOk = hasPath != true || (dist.HasValue && dist.Value <= 0.80f) || (targetSpeed.HasValue && targetSpeed.Value <= 0.05f) || speed <= 0.05f;
        bool observed = crouched && speedOk && pathOk;
        summary = "stationaryPostureObserved=observed=" + Bool(observed)
            + ",crouched=" + Bool(crouched)
            + ",speedOk=" + Bool(speedOk)
            + ",pathOk=" + Bool(pathOk)
            + ",speed=" + speed.ToString("0.00", CultureInfo.InvariantCulture)
            + ",pose=" + FloatText(pose)
            + ",smoothedPose=" + FloatText(smoothedPose)
            + ",prone=" + Tri(prone)
            + ",hasPath=" + Tri(hasPath)
            + ",dist=" + FloatText(dist)
            + ",targetSpeed=" + FloatText(targetSpeed);
        return observed;
    }

    private static string ReleaseExternalSystems(BotOwner? botOwner, string? botProfileId, DateTimeOffset now, string reason)
    {
        return VanguardExternalAuthorityAdapter.ReleaseMedicalPreempt(botOwner, botProfileId, now, "medical_isolation_release:" + Safe(reason));
    }

    private static string SnapshotExternalSummary(OperatorDecisionSnapshot snapshot)
    {
        snapshot ??= OperatorDecisionSnapshot.Empty;
        float? pathDistance = snapshot.Movement.DistanceToDestination ?? snapshot.Movement.GoToDistance;
        bool pathActive = snapshot.Movement.HasPath == true && pathDistance.HasValue && pathDistance.Value > 1.00f;
        bool moving = Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed) > 0.35f;
        bool looting = snapshot.Looting.BotLooting == true
            || snapshot.Looting.LootTaskRunning == true
            || snapshot.Looting.HasActiveLootable == true;
        bool orbit = !string.Equals(snapshot.Orbit.Classification, "none", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(snapshot.Orbit.Classification, "orbit_idle", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(snapshot.Orbit.Status, "idle", StringComparison.OrdinalIgnoreCase);
        return "snapshotExternal=path=" + Bool(pathActive)
            + ",pathDist=" + FloatText(pathDistance)
            + ",moving=" + Bool(moving)
            + ",loot=" + Bool(looting)
            + ",orbit=" + Bool(orbit)
            + ",directThreat=" + Bool(snapshot.Medical.Safety.EnemyCanShoot || snapshot.Threat.EnemyCanShoot == true || snapshot.Medical.Safety.IncomingFireRecent);
    }

    private static void LogThrottled(string key, DateTimeOffset now, string message)
    {
        if (LastLogAtByKey.TryGetValue(key, out var last) && now - last < LogInterval)
        {
            return;
        }

        LastLogAtByKey[key] = now;
        VanguardClientDiagnosticsLog.Info(StatusTag, message);
    }

    private static void LogThrottledLazy(
        string key,
        DateTimeOffset now,
        VanguardAuditLevel minimumLevel,
        Func<string> messageFactory)
    {
        if (!VanguardClientDiagnosticsLog.IsEnabled(minimumLevel))
        {
            return;
        }

        if (LastLogAtByKey.TryGetValue(key, out var last) && now - last < LogInterval)
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
            foreach (var method in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
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
            var type = target.GetType();
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, value);
                return true;
            }

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
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


    private static float? Float(object? value)
    {
        try
        {
            return value switch
            {
                float f => f,
                double d => (float)d,
                int i => i,
                long l => l,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool? Bool(object? value)
    {
        return value is bool b ? b : null;
    }

    private static string FloatText(float? value)
    {
        return value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : "unknown";
    }

    private static string Tri(bool? value)
    {
        return value.HasValue ? Bool(value.Value) : "unknown";
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().ToLowerInvariant();
    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }

    private sealed class IsolationState
    {
        public string OperatorId { get; init; } = "none";
        public string BotProfileId { get; init; } = "none";
        public string OwnerLeaseId { get; init; } = "none";
        public string TargetPart { get; init; } = "none";
        public string ItemTemplateId { get; init; } = "none";
        public string Phase { get; set; } = "None";
        public DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset LastProgressAtUtc { get; set; }
        public DateTimeOffset LastQuiesceAtUtc { get; set; }
        public DateTimeOffset ReadyAtUtc { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset StationaryObservedSinceUtc { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset CoverCommitUntilUtc { get; set; } = DateTimeOffset.MinValue;
        public string CoverCommitReason { get; set; } = "none";
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }
}

internal readonly struct VanguardMedicalIsolationTickResult
{
    private VanguardMedicalIsolationTickResult(bool canDriveMovement, bool shouldFail, string failureReason, string summary)
    {
        CanDriveMovement = canDriveMovement;
        ShouldFail = shouldFail;
        FailureReason = failureReason;
        Summary = summary;
    }

    public bool CanDriveMovement { get; }
    public bool ShouldFail { get; }
    public string FailureReason { get; }
    public string Summary { get; }

    public static VanguardMedicalIsolationTickResult MoveAllowed(string summary) => new(true, false, "none", summary);
    public static VanguardMedicalIsolationTickResult Wait(string summary) => new(false, false, "none", summary);
    public static VanguardMedicalIsolationTickResult Fail(string reason, string summary) => new(false, true, reason, summary);
}
#endif
