#if SPT_CLIENT
using Comfort.Common;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using EFT;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Combat;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Grenades;
using Vanguard.Client.Runtime.External;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Movement;

// Responsibility: builds the squad contact read model, requalifies contacts per Operator and commits only selected targets into SAIN.
// Flow: Scanner/contact evidence is merged into squad knowledge, each Operator rechecks that contact from its own position and current threat state, stale/distant pursuit is released when required, and only the surviving assignment is handed to SAIN.
// Authority boundary: Vanguard owns qualification/cohesion policy while SAIN remains the combat executor; knowledge alone never grants endless pursuit.
// Invariant: distant/stale contacts remain knowledge until direct/recent evidence satisfies the configured cohesion and validity gates.

namespace Vanguard.Client.Runtime.Awareness;

/// <summary>
/// Central Awareness / Scanner Assignment bridge.
/// Vanguard owns target qualification and squad contact propagation; each Operator then requalifies
/// the shared contact picture from its own position, LOS, verticality, NavMesh path, current target and
/// local danger. Only the selected assignment is committed into SAIN, which remains the combat executor.
/// Distance may limit pursuit or assignment quality, but never erases a valid hostile contact or requires
/// every Operator to rediscover the same target independently.
/// </summary>
internal static partial class VanguardCombatAwarenessBridge
{
    public const string StatusTag = "VANGUARD_COMBAT_AWARENESS_BRIDGE_OK";
    public const string ScanAssignmentStatusTag = "VANGUARD_SCAN_ASSIGNMENT_ACTIVE_OK";
    public const string GenerationIdempotenceStatusTag = "VANGUARD_AWARENESS_GENERATION_IDEMPOTENCE_STATUS";
    public const string StaleTargetReleaseStatusTag = "VANGUARD_STALE_TARGET_RELEASE_OK";
    public const string TargetClearConfirmStatusTag = "VANGUARD_TARGET_CLEAR_CONFIRM_OK";
    public const string StaleTargetQuarantineStatusTag = "VANGUARD_STALE_TARGET_QUARANTINE_OK";
    public const string IsolatedCombatReleaseStatusTag = "VANGUARD_ISOLATED_COMBAT_RELEASE_OK";
    public const string EmergencyScanContactStatusTag = "VANGUARD_EMERGENCY_SCAN_CONTACT_OK";
    public const string ClientBuildStatusTag = "VANGUARD_CLIENT_BUILD_STATUS";
    public const string SquadTravelCombatAuthorityStatusTag = "VANGUARD_SQUAD_TRAVEL_COMBAT_AUTHORITY_STATUS";
    public const string AwarenessCommitStatusTag = "VANGUARD_AWARENESS_COMMIT_STATUS";
    public const string VerifiedGoalHandoffStatusTag = "VANGUARD_VERIFIED_SAIN_GOAL_HANDOFF_STATUS";
    public const string DistantPursuitKnowledgeOnlyStatusTag = "VANGUARD_DISTANT_PURSUIT_KNOWLEDGE_ONLY_STATUS";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, DateTimeOffset> DropCooldownUntilByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PendingTargetClearState> PendingTargetClearByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, QuarantinedTargetState> QuarantinedTargetByBotAndTarget = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogAtByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Dictionary<string, SquadCombatContactState>> SquadContactsByOwnerProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, TargetApplyGenerationState> TargetApplyGenerationByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, TargetApplyCircuitState> TargetApplyCircuitByBotAndTarget = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, VerifiedSainGoalHandoffState> VerifiedGoalHandoffByBotProfileId = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(0.75d);
    private static readonly TimeSpan DropCooldown = TimeSpan.FromSeconds(4.00d);
    private static readonly TimeSpan TargetClearVerifyDelay = TimeSpan.FromSeconds(1.50d);
    private static readonly TimeSpan TargetClearVerifyTimeout = TimeSpan.FromSeconds(5.50d);
    private static readonly TimeSpan TargetClearUnconfirmedBackoff = TimeSpan.FromSeconds(18.00d);
    private static readonly TimeSpan StaleTargetQuarantine = TimeSpan.FromSeconds(12.00d);
    private static readonly TimeSpan RejectLogInterval = TimeSpan.FromSeconds(2.50d);
    private static readonly TimeSpan SummaryLogInterval = TimeSpan.FromSeconds(10.00d);
    private static readonly TimeSpan TargetApplyDirectFailureBackoff = TimeSpan.FromSeconds(6.00d);
    private static readonly TimeSpan TargetApplyUnprovenFailureBackoff = TimeSpan.FromSeconds(20.00d);
    private static readonly TimeSpan VerifiedGoalHandoffWindow = TimeSpan.FromSeconds(2.25d);

    private static DateTimeOffset nextTickAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            DropCooldownUntilByBotProfileId.Clear();
            PendingTargetClearByBotProfileId.Clear();
            QuarantinedTargetByBotAndTarget.Clear();
            LastLogAtByKey.Clear();
            SquadContactsByOwnerProfileId.Clear();
            TargetApplyGenerationByBotProfileId.Clear();
            TargetApplyCircuitByBotAndTarget.Clear();
            VerifiedGoalHandoffByBotProfileId.Clear();
        }

        nextTickAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        VanguardOwnerImmediateThreatService.Reset(reason);
        ResetUnifiedThreatAssignment(reason);
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_AWARENESS_BRIDGE_RESET reason={Safe(reason)}; targetMutationCooldown=cleared; dropCooldown=cleared; pendingClear=cleared; quarantine=cleared; targetApplyCircuit=cleared; verifiedGoalHandoff=cleared; scanAssignmentTag={ScanAssignmentStatusTag}; staleReleaseTag={StaleTargetReleaseStatusTag}; clearConfirmTag={TargetClearConfirmStatusTag}; quarantineTag={StaleTargetQuarantineStatusTag}; isolatedReleaseTag={IsolatedCombatReleaseStatusTag}; emergencyScanTag={EmergencyScanContactStatusTag}; squadCombatTag={SquadTravelCombatAuthorityStatusTag}; handoffTag={VerifiedGoalHandoffStatusTag}; tag={StatusTag}");
    }

    /// <summary>
    /// Refreshes only the bounded squad-contact read model. This method performs no SAIN, hands,
    /// movement or target mutation and is safe for the scheduler to call before arbitration.
    /// </summary>
    public static void RefreshSquadCombatContactReadModel(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        if (snapshots == null || snapshots.Count == 0)
        {
            return;
        }

        foreach (OperatorDecisionSnapshot snapshot in snapshots)
        {
            if (snapshot == null || !snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            {
                continue;
            }

            // This is a read-model producer only. It publishes direct snapshot evidence before the
            // The coordinator performs the richer world scan and individual assignment.
            PublishSquadCombatContact(snapshot, now);
        }

        var expired = new List<SquadCombatContactState>();
        lock (Sync)
        {
            foreach (var ownerEntry in SquadContactsByOwnerProfileId.ToArray())
            {
                foreach (SquadCombatContactState contact in ownerEntry.Value.Values.ToArray())
                {
                    if (contact.ExpiresAtUtc <= now)
                    {
                        ownerEntry.Value.Remove(contact.TargetId);
                        expired.Add(contact);
                    }
                }

                if (ownerEntry.Value.Count == 0)
                {
                    SquadContactsByOwnerProfileId.Remove(ownerEntry.Key);
                }
            }
        }

        foreach (SquadCombatContactState contact in expired)
        {
            LogThrottled(
                "squadContactExpired|" + contact.OwnerProfileId + "|" + contact.TargetId,
                now,
                SummaryLogInterval,
                $"VANGUARD_SQUAD_CONTACT_EXPIRED owner={Safe(contact.OwnerProfileId)}; sourceOperator={Safe(contact.SourceOperatorId)}; sourceBot={Safe(contact.SourceBotProfileId)}; target={Safe(contact.TargetId)}; reason=bounded_memory_elapsed; sourceDeathDoesNotEraseQualifiedContact=true; mutation=contact_removed; doctrine=qualified_contact_survives_reporter_loss_until_ttl_or_target_resolution; tag={UnifiedAssignmentStatusTag}; bridgeTag={StatusTag}");
        }
    }


    public static bool TryVerifyOrRepairCommittedTarget(
        OperatorDecisionSnapshot snapshot,
        string targetId,
        int generation,
        bool allowRepair,
        DateTimeOffset now,
        out string reason)
    {
        reason = "none";
        string target = Normalize(targetId);
        if (snapshot == null || !snapshot.Alive || string.Equals(target, "none", StringComparison.OrdinalIgnoreCase))
        {
            reason = "invalid_snapshot_or_target";
            return false;
        }

        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record)
            || record.BotOwner == null
            || record.BotOwner.IsDead)
        {
            reason = "bot_owner_missing_or_dead";
            return false;
        }

        BotOwner botOwner = record.BotOwner;
        string currentGoal = ResolveCurrentSainGoalId(botOwner);
        if (SameTarget(currentGoal, target))
        {
            RecordVerifiedSainGoalHandoff(snapshot.BotProfileId, target, "goal_readback_generation_" + generation.ToString(CultureInfo.InvariantCulture), now);
            VanguardMainIntentScheduler.NotifyCombatTargetApplied(snapshot.BotProfileId, target, "goal_readback_generation_" + generation.ToString(CultureInfo.InvariantCulture), now, verified: true);
            reason = "verified_current_sain_goal";
            return true;
        }

        string localTarget = Normalize(snapshot.Threat.EnemyId);
        bool differentLocalDirect = !string.Equals(localTarget, "none", StringComparison.OrdinalIgnoreCase)
            && !SameTarget(localTarget, target)
            && (HasCurrentImmediateProof(snapshot) || HasCurrentDirectProof(snapshot));
        if (differentLocalDirect)
        {
            reason = "different_live_local_direct_target_preserved:" + Safe(localTarget);
            return false;
        }

        if (!allowRepair)
        {
            reason = "committed_target_not_verified:goal=" + Safe(currentGoal);
            return false;
        }

        if (!HasIndividualQualifiedAssignmentOrLocalEvidence(snapshot, target)
            && !HasFreshUnifiedAssignmentForTarget(snapshot.BotProfileId, target, now, out string assignmentReason))
        {
            reason = "repair_denied_without_individual_vanguard_assignment:" + Safe(assignmentReason);
            return false;
        }

        if (!IsLiveCombatTarget(target, out var liveReason))
        {
            reason = "committed_target_not_live:" + Safe(liveReason);
            return false;
        }

        if (TryBootstrapAndApplyTarget(
            snapshot,
            botOwner,
            target,
            "scheduler_target_verification_repair:generation=" + generation.ToString(CultureInfo.InvariantCulture),
            "unified_scheduler_target_verification_repair",
            now,
            out var result,
            out var before,
            out var after,
            out var bootstrapReason))
        {
            RecordVerifiedSainGoalHandoff(snapshot.BotProfileId, target, "scheduler_repair_generation_" + generation.ToString(CultureInfo.InvariantCulture), now);
            VanguardMainIntentScheduler.NotifyCombatTargetApplied(snapshot.BotProfileId, target, "scheduler_repair_generation_" + generation.ToString(CultureInfo.InvariantCulture), now, verified: true);
            VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.SainTargetVerificationStatusTag,
                $"VANGUARD_TARGET_REPAIR_APPLIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(target)}; generation={generation}; before={Safe(before)}; after={Safe(after)}; result={Safe(result.Reason)}; bootstrap={Safe(bootstrapReason)}; doctrine=one_idempotent_repair_without_delaying_initial_sain_release; regressionGuardTag={VanguardPrimaryExecutionContract.RegressionGuardStatusTag}; tag={VanguardPrimaryExecutionContract.SainTargetVerificationStatusTag}; bridgeTag={StatusTag}");
            reason = "repair_verified:" + Safe(result.Reason);
            return true;
        }

        reason = "repair_failed:before=" + Safe(before) + ":after=" + Safe(after) + ":result=" + Safe(result.Reason) + ":bootstrap=" + Safe(bootstrapReason);
        return false;
    }

    public static void Tick()
    {
        if (!VanguardOperatorRuntimeAuditLoadGuard.IsOpen())
        {
            return;
        }

        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < nextTickAtUtc)
        {
            return;
        }

        nextTickAtUtc = now + TickInterval;
        LogBootOnce(now);

        var snapshots = VanguardOperatorDecisionSnapshotService.GetLatestSnapshots();
        if (snapshots.Count == 0)
        {
            return;
        }

        foreach (var snapshot in snapshots)
        {
            try
            {
                Evaluate(snapshot, now);
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(StatusTag,
                    $"VANGUARD_AWARENESS_BRIDGE_TICK_FAILED operator={Safe(snapshot?.OperatorId)}; botProfile={Safe(snapshot?.BotProfileId)}; reason={exception.GetType().Name}:{Safe(exception.Message)}; tag={StatusTag}");
            }
        }
    }

    private static void LogBootOnce(DateTimeOffset now)
    {
        if (bootLogged)
        {
            return;
        }

        bootLogged = true;
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.RuntimeBoundaryConvergenceStatusTag,
            $"VANGUARD_RUNTIME_BOUNDARY_CONVERGENCE_BOOT enabled=true; base=runtime_boundary_convergence; travelConsumedAnchorRelease={VanguardMovementAuthorityDoctrine.TravelConsumedAnchorStaleGenerationReleaseSeconds:0.00}; verifiedGoalHandoff={VerifiedGoalHandoffWindow.TotalSeconds:0.00}; surgeryIsolationAdmissionCooldown=true; globalCombatDiagnosticsPreserved=true; noFakeLos=true; noFireMutation=true; noNewAuthority=true; build={VanguardBuildVersion.BuildLabel}; tag={VanguardBuildVersion.RuntimeBoundaryConvergenceStatusTag}");
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_COMBAT_AWARENESS_BRIDGE_BOOT enabled=true; mode=single_unified_coordinator; sources=independent_world_scan+current_sain+scanner+awareness+owner_shot+incoming_fire+squad_contacts; qualification=vanguard; contactPicture=shared_multi_target; assignment=per_operator_geometry_los_navmesh_verticality_hysteresis_distribution; commit=single_sain_bridge; worldScanMax=110; candidateEvaluationCap=18; ownerShotMax=105; closeDirect=14; closeNav=24; closeVertical=2.6; groupTargetMemory={UnifiedSquadContactTtl.TotalSeconds:0.0}; sourceDeathDoesNotEraseQualifiedContact=true; distancesLimitAssignmentAndPursuitNotAwareness=true; localProofPerRecipientRequired=false; noFakeLos=true; controlsFormation=false; schedulerOwnsPrimaryWindow=true; sainOwnsTactics=true; commitTag={AwarenessCommitStatusTag}; unifiedTag={UnifiedAssignmentStatusTag}; ownerShotTag={VanguardOwnerShotMemoryService.StatusTag}; dropCooldown={DropCooldown.TotalSeconds:0.00}; clearVerifyDelay={TargetClearVerifyDelay.TotalSeconds:0.00}; clearVerifyTimeout={TargetClearVerifyTimeout.TotalSeconds:0.00}; unconfirmedBackoff={TargetClearUnconfirmedBackoff.TotalSeconds:0.00}; quarantine={StaleTargetQuarantine.TotalSeconds:0.00}; build={VanguardBuildVersion.BuildLabel}; scanAssignmentTag={ScanAssignmentStatusTag}; staleReleaseTag={StaleTargetReleaseStatusTag}; clearConfirmTag={TargetClearConfirmStatusTag}; quarantineTag={StaleTargetQuarantineStatusTag}; isolatedReleaseTag={IsolatedCombatReleaseStatusTag}; emergencyScanTag={EmergencyScanContactStatusTag}; squadCombatTag={SquadTravelCombatAuthorityStatusTag}; tag={StatusTag}");
    }

    private static void Evaluate(OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot == null || !snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            return;
        }

        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var runtime)
            || runtime.BotOwner == null
            || runtime.BotOwner.IsDead)
        {
            return;
        }

        // One coordinator owns the world scan and assignment path to avoid duplicate promotion authorities:
        //  1) Vanguard builds/refreshes the squad contact picture,
        //  2) each Operator re-scores that common picture from its own geometry,
        //  3) the selected valid target is committed once into SAIN.
        // The retired runtime squad-broadcast, close-guard and promotion drivers are removed. Scheduler APIs
        // read the same contact picture but never compete with this mutation path.
        ProcessPendingTargetClear(snapshot, runtime.BotOwner, now);
        if (TryRunUnifiedThreatAssignment(snapshot, runtime.BotOwner, now))
        {
            return;
        }

        if (ShouldDropCurrentTarget(snapshot, out var dropReason, out var dropKind))
        {
            TryDropStaleTarget(snapshot, runtime.BotOwner, dropReason, dropKind, now);
            return;
        }

        LogThrottled("kept|" + snapshot.BotProfileId + "|" + snapshot.Threat.EnemyId + "|" + snapshot.Awareness.CandidateId, now, SummaryLogInterval,
            $"VANGUARD_TARGET_ASSIGNMENT_KEPT operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; current={Safe(snapshot.Threat.EnemyId)}; candidate={Safe(snapshot.Awareness.CandidateId)}; reason=no_active_bridge_needed; threat={Safe(snapshot.Threat.Classification)}; sain={Safe(snapshot.Sain.Classification)}; awareness={Safe(snapshot.Awareness.Classification)}; bubble={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; tag={StatusTag}");
    }


    public static bool HasFreshSquadCombatContact(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason)
    {
        if (!TryGetBestFreshSquadCombatContact(
                snapshot,
                now,
                includeSourceOperator: true,
                excludedTargetId: "none",
                secondaryExcludedTargetId: "none",
                out SquadCombatContactState contact,
                out string contactReason))
        {
            reason = contactReason;
            return false;
        }

        if (HasIndividualQualifiedAssignmentOrLocalEvidence(snapshot, contact.TargetId))
        {
            reason = "individual_assignment_or_local_evidence:" + contactReason;
            return true;
        }

        reason = "shared_contact_awaiting_individual_assignment:" + contactReason;
        return false;
    }

    /// <summary>
    /// Returns true when this Operator has either direct local evidence for the target or a fresh
    /// individual assignment produced by the unified Vanguard scanner. The squad contact is already
    /// valid knowledge; this predicate confirms only that the per-Operator assignment stage selected it.
    /// </summary>
    public static bool HasIndividualQualifiedAssignmentOrLocalEvidence(OperatorDecisionSnapshot snapshot, string? targetId)
    {
        string target = Normalize(targetId);
        if (snapshot == null || !snapshot.Alive || string.Equals(target, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (VanguardSquadTargetNoProgressQuarantine.IsCombatAuthorityBlocked(
            snapshot,
            target,
            DateTimeOffset.UtcNow,
            out _))
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (HasDirectLocalSensorEvidenceForTarget(snapshot, target))
        {
            return true;
        }

        // A distant contact remains valid squad knowledge, but a prior unified assignment cannot
        // renew autonomous pursuit after local actionability disappears. Only the short verified
        // post-commit handoff may bridge the snapshot latency of a genuinely qualified assignment.
        if (IsDistantPursuitKnowledgeOnly(snapshot, target, out _, out _))
        {
            return HasFreshDistantAuthorityAssignmentForTarget(
                    snapshot.BotProfileId,
                    target,
                    now,
                    out _)
                || TryResolveVerifiedSainGoalHandoff(
                    snapshot.BotProfileId,
                    target,
                    now,
                    out _,
                    out _);
        }

        return HasFreshUnifiedAssignmentForTarget(snapshot.BotProfileId, target, now, out _);
    }

    private static bool HasDirectLocalSensorEvidenceForTarget(OperatorDecisionSnapshot snapshot, string target)
    {
        if (SameTarget(snapshot.Threat.EnemyId, target)
            && (HasCurrentImmediateProof(snapshot) || HasCurrentDirectProof(snapshot)))
        {
            return true;
        }

        if (SameTarget(snapshot.Awareness.CandidateId, target)
            && (snapshot.Awareness.CandidateVisible
                || snapshot.Awareness.CandidateLineOfSight
                || snapshot.Awareness.CandidateCanShoot
                || snapshot.Awareness.IncomingFireFresh))
        {
            return true;
        }

        return SameTarget(snapshot.ThreatScan.CandidateThreatId, target)
            && (snapshot.ThreatScan.CandidateVisible
                || snapshot.ThreatScan.CandidateLineOfSight
                || snapshot.ThreatScan.CandidateCanShoot
                || snapshot.ThreatScan.CandidateIncomingFireFresh);
    }

    /// <summary>
    /// Classifies a hostile contact as knowledge-only for movement/combat authority when this Operator
    /// no longer has target-specific direct proof and either the target is beyond the tactical pursuit
    /// bubble or the Operator has already crossed the formation catch-up boundary. This prevents
    /// pursuit-created proximity from renewing its own authority while preserving hostile knowledge.
    /// </summary>
    private static bool IsDistantPursuitKnowledgeOnly(
        OperatorDecisionSnapshot snapshot,
        string? targetId,
        out float distance,
        out string reason)
    {
        distance = float.MaxValue;
        reason = "none";
        string target = Normalize(targetId);
        if (snapshot == null
            || !snapshot.Alive
            || string.Equals(target, "none", StringComparison.OrdinalIgnoreCase))
        {
            reason = "invalid_snapshot_or_target";
            return false;
        }

        if (HasDirectLocalSensorEvidenceForTarget(snapshot, target))
        {
            reason = "target_specific_direct_proof";
            return false;
        }

        bool hasTargetDistance = TryResolveTargetDistanceForPursuit(snapshot, target, out distance, out string distanceSource);
        bool ownerAnchorReliable = snapshot.SquadCohesion.OwnerKnown
            && snapshot.SquadCohesion.OwnerReliableForActiveMovement
            && snapshot.SquadCohesion.OwnerPosition.HasValue;
        float cohesionDistance = snapshot.SquadCohesion.OperatorDistanceToOwner;
        float cohesionThreshold = VanguardMovementAuthorityDoctrine.CombatCohesionForcedCatchupMeters;
        bool formationDetached = ownerAnchorReliable && cohesionDistance >= cohesionThreshold;
        if (formationDetached)
        {
            if (!hasTargetDistance)
            {
                distance = -1f;
            }

            reason = "formation_detached_without_target_specific_direct_proof:cohesion="
                + cohesionDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + ":threshold=" + cohesionThreshold.ToString("0.0", CultureInfo.InvariantCulture)
                + ":targetDistance=" + (hasTargetDistance ? distance.ToString("0.0", CultureInfo.InvariantCulture) : "unknown")
                + ":source=" + Safe(distanceSource);
            return true;
        }

        if (!hasTargetDistance)
        {
            reason = "target_distance_unknown";
            return false;
        }

        float threshold = VanguardMovementAuthorityDoctrine.StaleSearchDistanceMeters;
        if (distance < threshold)
        {
            reason = "inside_tactical_pursuit_bubble:distance=" + distance.ToString("0.0", CultureInfo.InvariantCulture)
                + ":threshold=" + threshold.ToString("0.0", CultureInfo.InvariantCulture)
                + ":source=" + Safe(distanceSource);
            return false;
        }

        reason = "distant_contact_without_target_specific_direct_proof:distance="
            + distance.ToString("0.0", CultureInfo.InvariantCulture)
            + ":threshold=" + threshold.ToString("0.0", CultureInfo.InvariantCulture)
            + ":source=" + Safe(distanceSource);
        return true;
    }

    private static bool TryResolveTargetDistanceForPursuit(
        OperatorDecisionSnapshot snapshot,
        string target,
        out float distance,
        out string source)
    {
        distance = float.MaxValue;
        source = "none";

        if (SameTarget(snapshot.Threat.EnemyId, target)
            && TryUsePursuitDistance(snapshot.Threat.Distance, "threat", out distance, out source))
        {
            return true;
        }

        if (SameTarget(snapshot.Awareness.CandidateId, target)
            && TryUsePursuitDistance(snapshot.Awareness.CandidateDistance, "awareness_candidate", out distance, out source))
        {
            return true;
        }

        if (SameTarget(snapshot.ThreatScan.CandidateThreatId, target)
            && TryUsePursuitDistance(snapshot.ThreatScan.CandidateDistance, "threat_scan_candidate", out distance, out source))
        {
            return true;
        }

        if (VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record)
            && record.BotOwner != null
            && !record.BotOwner.IsDead
            && SameTarget(ResolveCurrentSainGoalId(record.BotOwner), target)
            && TryUsePursuitDistance(snapshot.Brain.VanillaGoalEnemyDistance, "brain_goal_enemy", out distance, out source))
        {
            return true;
        }

        Player? targetPlayer = VanguardFikaCompat.FindRaidPlayerByProfileId(target);
        if (targetPlayer != null)
        {
            float worldDistance = Vector3.Distance(snapshot.Position, targetPlayer.Position);
            if (!float.IsNaN(worldDistance) && !float.IsInfinity(worldDistance) && worldDistance >= 0f)
            {
                distance = worldDistance;
                source = "world_player";
                return true;
            }
        }

        return false;
    }

    private static bool TryUsePursuitDistance(
        float? candidate,
        string candidateSource,
        out float distance,
        out string source)
    {
        distance = float.MaxValue;
        source = "none";
        if (!candidate.HasValue
            || float.IsNaN(candidate.Value)
            || float.IsInfinity(candidate.Value)
            || candidate.Value < 0f)
        {
            return false;
        }

        distance = candidate.Value;
        source = candidateSource;
        return true;
    }


    /// <summary>
    /// Returns the live hostile target that is already installed in this Operator's own SAIN goal.
    /// Shared contacts and scanner candidates are qualified by Vanguard, but combat authority begins
    /// only after the selected assignment is read back from this Operator's own SAIN goal.
    /// </summary>
    public static bool TryResolveLocallyAppliedSainTarget(
        OperatorDecisionSnapshot snapshot,
        string? preferredTargetId,
        out string targetId,
        out string reason)
    {
        targetId = "none";
        reason = "none";
        if (snapshot == null || !snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            reason = "invalid_snapshot";
            return false;
        }

        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record)
            || record.BotOwner == null
            || record.BotOwner.IsDead)
        {
            reason = "bot_owner_missing_or_dead";
            return false;
        }

        string currentGoal = ResolveCurrentSainGoalId(record.BotOwner);
        if (string.Equals(currentGoal, "none", StringComparison.OrdinalIgnoreCase))
        {
            reason = "local_sain_goal_missing";
            return false;
        }

        string preferred = Normalize(preferredTargetId);

        if (IsProtectedFriendlyTarget(currentGoal, out var friendlyReason))
        {
            reason = "local_sain_goal_is_protected_friendly:" + Safe(friendlyReason);
            return false;
        }

        if (!IsLiveCombatTarget(currentGoal, out var liveReason))
        {
            reason = "local_sain_goal_not_live:" + Safe(liveReason);
            return false;
        }

        if (VanguardSquadTargetNoProgressQuarantine.IsCombatAuthorityBlocked(
            snapshot,
            currentGoal,
            DateTimeOffset.UtcNow,
            out string squadQuarantineReason))
        {
            reason = "local_sain_goal_quarantined_knowledge_only:" + Safe(squadQuarantineReason);
            return false;
        }

        bool individualAssignmentOrLocalEvidence = HasIndividualQualifiedAssignmentOrLocalEvidence(snapshot, currentGoal);
        bool snapshotOwnsGoal = SameTarget(snapshot.Threat.EnemyId, currentGoal);
        bool sainOwnsGoal = snapshot.Sain.HasEnemy == true
            && (snapshot.Sain.IsInCombat == true
                || snapshot.Sain.Searching == true
                || ContainsText(snapshot.Sain.CurrentAction, "shoot")
                || ContainsText(snapshot.Sain.CurrentAction, "cover")
                || ContainsText(snapshot.Sain.CombatDecision, "attack"));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool verifiedGoalHandoff = TryResolveVerifiedSainGoalHandoff(
            snapshot.BotProfileId,
            currentGoal,
            now,
            out _,
            out var verifiedHandoffReason);
        bool distantKnowledgeOnly = IsDistantPursuitKnowledgeOnly(
            snapshot,
            currentGoal,
            out float distantKnowledgeDistance,
            out string distantKnowledgeReason);
        string distantAuthorityReason = "not_distant";
        bool distantAuthorityAssignment = distantKnowledgeOnly
            && HasFreshDistantAuthorityAssignmentForTarget(
                snapshot.BotProfileId,
                currentGoal,
                now,
                out distantAuthorityReason);
        if (distantKnowledgeOnly
            && !HasDirectLocalSensorEvidenceForTarget(snapshot, currentGoal)
            && !verifiedGoalHandoff
            && !distantAuthorityAssignment)
        {
            reason = "local_sain_goal_distant_knowledge_only:" + Safe(distantKnowledgeReason) + ":assignment=" + Safe(distantAuthorityReason);
            LogThrottled(
                "LocalGoalKnowledgeOnly|" + snapshot.BotProfileId + "|" + currentGoal,
                now,
                RejectLogInterval,
                $"VANGUARD_LOCAL_SAIN_GOAL_KNOWLEDGE_ONLY operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(currentGoal)}; distance={distantKnowledgeDistance:0.0}; reason={Safe(distantKnowledgeReason)}; verifiedHandoff=false; distantAuthorityAssignment=false; combatAuthority=false; movementAuthority=false; contactMemoryPreserved=true; tag={DistantPursuitKnowledgeOnlyStatusTag}");
            return false;
        }

        // Vanguard: a concrete multi-member SAIN squad decision is itself proof that this
        // Operator's local SAIN component owns the already-installed goal. Friendly and
        // liveness checks above still apply, so shared squad knowledge never bypasses the
        // protected-target boundary. This allows the existing combat-release route to
        // preempt LootingBots/ORBIT before SAIN's Squad layer executes.
        bool sainSquadOwnsGoal = VanguardSainSquadCombatAuthority.IsSnapshotAuthority(
            snapshot.Sain,
            out var sainSquadAuthorityReason);
        bool locallyOwnedAndActionable = individualAssignmentOrLocalEvidence
            || verifiedGoalHandoff
            || sainSquadOwnsGoal
            || (snapshotOwnsGoal && sainOwnsGoal && !snapshot.Threat.StaleThreat);
        if (!locallyOwnedAndActionable)
        {
            reason = "local_sain_goal_without_verified_assignment_or_combat_ownership:goal=" + Safe(currentGoal);
            return false;
        }

        bool preferredDiffers = !string.Equals(preferred, "none", StringComparison.OrdinalIgnoreCase)
            && !SameTarget(currentGoal, preferred);
        targetId = currentGoal;
        reason = preferredDiffers
            ? "local_sain_goal_verified_and_supersedes_stale_selected_target:selected=" + Safe(preferred)
            : individualAssignmentOrLocalEvidence
                ? "local_sain_goal_verified_with_individual_assignment_or_local_evidence"
                : verifiedGoalHandoff
                    ? "local_sain_goal_verified_with_bounded_post_apply_handoff:" + Safe(verifiedHandoffReason)
                    : sainSquadOwnsGoal
                        ? "local_sain_goal_verified_with_sain_squad_authority:" + Safe(sainSquadAuthorityReason)
                        : "local_sain_goal_verified_with_snapshot_combat_ownership";
        return true;
    }

    /// <summary>
    /// Bridges only an immediately re-read SAIN GoalEnemy across the short snapshot latency
    /// between Awareness and scheduler arbitration. This receipt is target-specific, bounded,
    /// and never manufactures visibility, line of sight, incoming fire or can-shoot truth.
    /// </summary>
    public static bool TryResolveVerifiedSainGoalHandoff(
        string? botProfileId,
        string? expectedTargetId,
        DateTimeOffset now,
        out string targetId,
        out string reason)
    {
        targetId = "none";
        reason = "none";
        string botKey = Normalize(botProfileId);
        string expected = Normalize(expectedTargetId);
        if (string.Equals(botKey, "none", StringComparison.OrdinalIgnoreCase))
        {
            reason = "bot_profile_missing";
            return false;
        }

        lock (Sync)
        {
            if (!VerifiedGoalHandoffByBotProfileId.TryGetValue(botKey, out var receipt))
            {
                reason = "verified_goal_handoff_missing";
                return false;
            }

            if (receipt.ExpiresAtUtc <= now)
            {
                VerifiedGoalHandoffByBotProfileId.Remove(botKey);
                reason = "verified_goal_handoff_expired";
                return false;
            }

            if (!string.Equals(expected, "none", StringComparison.OrdinalIgnoreCase)
                && !SameTarget(receipt.TargetId, expected))
            {
                reason = "verified_goal_handoff_target_mismatch:receipt=" + Safe(receipt.TargetId)
                    + ":expected=" + Safe(expected);
                return false;
            }

            targetId = receipt.TargetId;
            reason = "verified_goal_handoff_active:source=" + Safe(receipt.Source)
                + ":expires=" + receipt.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture);
            return true;
        }
    }

    private static void RecordVerifiedSainGoalHandoff(
        string? botProfileId,
        string? targetId,
        string source,
        DateTimeOffset now)
    {
        string botKey = Normalize(botProfileId);
        string target = Normalize(targetId);
        if (string.Equals(botKey, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(target, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(botKey, out OperatorDecisionSnapshot quarantineSnapshot)
            && VanguardSquadTargetNoProgressQuarantine.IsCombatAuthorityBlocked(
                quarantineSnapshot,
                target,
                now,
                out string quarantineReason))
        {
            LogThrottled(
                "VerifiedHandoffBlocked|" + botKey + "|" + target,
                now,
                RejectLogInterval,
                $"VANGUARD_VERIFIED_HANDOFF_BLOCKED operator={Safe(quarantineSnapshot.OperatorId)}; botProfile={Safe(botKey)}; owner={Safe(quarantineSnapshot.OwnerProfileId)}; target={Safe(target)}; source={Safe(source)}; reason={Safe(quarantineReason)}; mutation=false; tag={VanguardSquadTargetNoProgressQuarantine.StatusTag}; handoffTag={VerifiedGoalHandoffStatusTag}");
            return;
        }

        DateTimeOffset expiresAtUtc = now + VerifiedGoalHandoffWindow;
        lock (Sync)
        {
            VerifiedGoalHandoffByBotProfileId[botKey] = new VerifiedSainGoalHandoffState(
                target,
                Safe(source),
                now,
                expiresAtUtc);
        }

        LogThrottled(
            "VerifiedGoalHandoff|" + botKey + "|" + target,
            now,
            RejectLogInterval,
            $"VANGUARD_VERIFIED_SAIN_GOAL_HANDOFF botProfile={Safe(botKey)}; target={Safe(target)}; source={Safe(source)}; expiresAt={expiresAtUtc:O}; blocksTravelUntilSchedulerObservation=true; localLosFabricated=false; visibilityFabricated=false; doctrine=immediate_goal_readback_is_bounded_local_installation_truth; tag={VerifiedGoalHandoffStatusTag}");
    }

    /// <summary>
    /// Shared contacts are squad knowledge only. Movement authority is granted after this Operator's
    /// unified Scanner Assignment selects the target, or when direct local evidence already exists.
    /// This keeps qualification, propagation and individual assignment in one non-competing chain.
    /// </summary>
    public static bool HasMovementAuthoritativeSquadCombatContact(OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason)
    {
        reason = "none";
        if (!TryGetBestFreshSquadCombatContact(snapshot, now, includeSourceOperator: true, excludedTargetId: "none", secondaryExcludedTargetId: "none", out SquadCombatContactState contact, out string contactReason))
        {
            reason = contactReason;
            return false;
        }

        if (HasIndividualQualifiedAssignmentOrLocalEvidence(snapshot, contact.TargetId))
        {
            reason = "individual_assignment_or_local_evidence:" + contactReason;
            return true;
        }

        reason = "shared_contact_without_individual_assignment:" + contactReason;
        return false;
    }


    /// <summary>
    /// Vanguard resolves the next combat target without mutating SAIN. Direct local acquisition wins;
    /// scan/awareness follows; a group contact is eligible only after this Operator's unified
    /// Scanner Assignment selected it. The scheduler never becomes a competing target selector.
    /// </summary>
    public static bool TryResolveCombatContinuationTarget(
        OperatorDecisionSnapshot snapshot,
        string? currentTargetId,
        string? temporarilyExcludedGroupTargetId,
        DateTimeOffset now,
        out string targetId,
        out string source,
        out string reason)
    {
        targetId = "none";
        source = "none";
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        string current = Normalize(currentTargetId);
        string local = Normalize(snapshot.Threat.EnemyId);
        if (!SameTarget(local, current)
            && !string.Equals(local, "none", StringComparison.OrdinalIgnoreCase)
            && HasCurrentDirectProof(snapshot)
            && IsLiveWorldTarget(local, out var localLiveReason)
            && !IsProtectedFriendlyTarget(local, out _))
        {
            targetId = local;
            source = "local_independent_acquisition";
            reason = "local_direct_target:" + Safe(localLiveReason) + ":" + Safe(snapshot.Threat.Classification);
            return true;
        }

        string awarenessCandidate = Normalize(snapshot.Awareness.CandidateId);
        if (!SameTarget(awarenessCandidate, current)
            && !string.Equals(awarenessCandidate, "none", StringComparison.OrdinalIgnoreCase)
            && (snapshot.Awareness.CandidateVisible
                || snapshot.Awareness.CandidateLineOfSight
                || snapshot.Awareness.CandidateCanShoot
                || snapshot.Awareness.IncomingFireFresh)
            && IsLiveWorldTarget(awarenessCandidate, out var awarenessLiveReason)
            && !IsProtectedFriendlyTarget(awarenessCandidate, out _))
        {
            targetId = awarenessCandidate;
            source = "awareness_independent_acquisition";
            reason = "awareness_direct_candidate:" + Safe(awarenessLiveReason) + ":" + Safe(snapshot.Awareness.Reason);
            return true;
        }

        string scanCandidate = Normalize(snapshot.ThreatScan.CandidateThreatId);
        if (!SameTarget(scanCandidate, current)
            && !string.Equals(scanCandidate, "none", StringComparison.OrdinalIgnoreCase)
            && (snapshot.ThreatScan.CandidateVisible
                || snapshot.ThreatScan.CandidateLineOfSight
                || snapshot.ThreatScan.CandidateCanShoot
                || snapshot.ThreatScan.CandidateIncomingFireFresh
                || snapshot.ThreatScan.WouldPromote)
            && IsLiveWorldTarget(scanCandidate, out var scanLiveReason)
            && !IsProtectedFriendlyTarget(scanCandidate, out _))
        {
            targetId = scanCandidate;
            source = "scanner_independent_acquisition";
            reason = "scanner_candidate:" + Safe(scanLiveReason) + ":" + Safe(snapshot.ThreatScan.PromotionReason);
            return true;
        }

        string temporaryGroupExclusion = Normalize(temporarilyExcludedGroupTargetId);
        if (TryGetBestFreshSquadCombatContact(snapshot, now, includeSourceOperator: true, excludedTargetId: current, secondaryExcludedTargetId: temporaryGroupExclusion, out SquadCombatContactState groupContact, out string groupReason)
            && HasIndividualQualifiedAssignmentOrLocalEvidence(snapshot, groupContact.TargetId))
        {
            targetId = Normalize(groupContact.TargetId);
            source = "group_contact_individually_assigned:" + Safe(groupContact.SourceOperatorId);
            reason = groupReason + ";individualAssignment=true";
            return true;
        }

        reason = "no_live_local_scan_or_group_continuation";
        return false;
    }

    public static bool IsLiveCombatTarget(string? targetId, out string reason)
    {
        return IsLiveWorldTarget(targetId, out reason);
    }

    public static void InvalidateCombatAuthorityReceiptsForOwnerTarget(
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
        int assignmentsCleared = 0;
        int handoffsCleared = 0;
        int generationsCleared = 0;
        lock (Sync)
        {
            foreach (VanguardRaidOperatorRuntimeRecord sibling in siblings)
            {
                string bot = Normalize(sibling.BotProfileId);
                if (string.Equals(bot, "none", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (UnifiedAssignmentByBotProfileId.TryGetValue(bot, out UnifiedAssignmentState assignment)
                    && SameTarget(assignment.TargetProfileId, target))
                {
                    UnifiedAssignmentByBotProfileId.Remove(bot);
                    assignmentsCleared++;
                }

                if (VerifiedGoalHandoffByBotProfileId.TryGetValue(bot, out VerifiedSainGoalHandoffState receipt)
                    && SameTarget(receipt.TargetId, target))
                {
                    VerifiedGoalHandoffByBotProfileId.Remove(bot);
                    handoffsCleared++;
                }

                if (TargetApplyGenerationByBotProfileId.TryGetValue(bot, out TargetApplyGenerationState generation)
                    && SameTarget(generation.TargetId, target))
                {
                    TargetApplyGenerationByBotProfileId.Remove(bot);
                    generationsCleared++;
                }
            }
        }

        VanguardClientDiagnosticsLog.Info(VanguardSquadTargetNoProgressQuarantine.StatusTag,
            $"VANGUARD_COMBAT_RECEIPTS_INVALIDATED owner={Safe(owner)}; target={Safe(target)}; assignments={assignmentsCleared}; handoffs={handoffsCleared}; generations={generationsCleared}; at={now:O}; reason={Safe(reason)}; squadContactRetained=true; mutation=authority_receipts_only; tag={VanguardSquadTargetNoProgressQuarantine.StatusTag}; bridgeTag={StatusTag}");
    }

    public static void InvalidateSquadCombatTarget(string? ownerProfileId, string? targetId, DateTimeOffset now, string reason)
    {
        string owner = Normalize(ownerProfileId);
        string target = Normalize(targetId);
        if (string.Equals(owner, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(target, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool removed = false;
        lock (Sync)
        {
            if (SquadContactsByOwnerProfileId.TryGetValue(owner, out var contacts))
            {
                removed = contacts.Remove(target);
                if (contacts.Count == 0)
                {
                    SquadContactsByOwnerProfileId.Remove(owner);
                }
            }
        }

        if (removed)
        {
            VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.TargetChainIdempotenceStatusTag,
                $"VANGUARD_GROUP_TARGET_INVALIDATED owner={Safe(owner)}; target={Safe(target)}; reason={Safe(reason)}; at={now:O}; doctrine=remove_only_resolved_target_keep_other_group_contacts; tag={VanguardPrimaryExecutionContract.TargetChainIdempotenceStatusTag}; legacyChainTag={VanguardPrimaryExecutionContract.CombatTargetChainStatusTag}; bridgeTag={StatusTag}");
        }
    }

    private static bool TryGetBestFreshSquadCombatContact(
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        bool includeSourceOperator,
        string? excludedTargetId,
        string? secondaryExcludedTargetId,
        out SquadCombatContactState selected,
        out string reason)
    {
        selected = default;
        reason = "none";
        if (snapshot == null || !snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.OwnerProfileId))
        {
            reason = "operator_or_owner_missing";
            return false;
        }

        string excluded = Normalize(excludedTargetId);
        string secondaryExcluded = Normalize(secondaryExcludedTargetId);
        List<SquadCombatContactState> candidates;
        lock (Sync)
        {
            if (!SquadContactsByOwnerProfileId.TryGetValue(snapshot.OwnerProfileId, out var contacts))
            {
                reason = "no_group_contacts";
                return false;
            }

            foreach (var stale in contacts.Values.Where(contact => contact.ExpiresAtUtc <= now).Select(contact => contact.TargetId).ToArray())
            {
                contacts.Remove(stale);
            }

            if (contacts.Count == 0)
            {
                SquadContactsByOwnerProfileId.Remove(snapshot.OwnerProfileId);
                reason = "all_group_contacts_expired";
                return false;
            }

            candidates = contacts.Values
                .Where(contact => !IsSquadSuspicionKind(contact.Kind))
                .Where(contact => includeSourceOperator || !string.Equals(contact.SourceBotProfileId, snapshot.BotProfileId, StringComparison.OrdinalIgnoreCase))
                .Where(contact => string.Equals(excluded, "none", StringComparison.OrdinalIgnoreCase) || !SameTarget(contact.TargetId, excluded))
                .Where(contact => string.Equals(secondaryExcluded, "none", StringComparison.OrdinalIgnoreCase) || !SameTarget(contact.TargetId, secondaryExcluded))
                .ToList();
        }

        // Selection must rank the contact as it applies to this recipient, not only by its historical
        // kind. An aged immediate report is soft and must not hide a newer actionable direct report.
        candidates = candidates
            .OrderByDescending(contact => HasIndividualQualifiedAssignmentOrLocalEvidence(snapshot, contact.TargetId))
            .ThenByDescending(contact => contact.Kind.IndexOf("immediate", StringComparison.OrdinalIgnoreCase) >= 0)
            .ThenBy(contact => contact.Distance)
            .ThenByDescending(contact => contact.ObservedAtUtc)
            .ToList();

        foreach (var contact in candidates)
        {

            if (string.Equals(contact.TargetId, "none", StringComparison.OrdinalIgnoreCase)
                || IsProtectedFriendlyTarget(contact.TargetId, out _))
            {
                continue;
            }

            if (VanguardSquadTargetNoProgressQuarantine.IsCombatAuthorityBlocked(
                snapshot,
                contact.TargetId,
                now,
                out string quarantineReason))
            {
                LogThrottled(
                    "SquadContactKnowledgeOnly|" + snapshot.OwnerProfileId + "|" + snapshot.BotProfileId + "|" + contact.TargetId,
                    now,
                    RejectLogInterval,
                    $"VANGUARD_SQUAD_CONTACT_KNOWLEDGE_ONLY owner={Safe(snapshot.OwnerProfileId)}; operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(contact.TargetId)}; sourceOperator={Safe(contact.SourceOperatorId)}; kind={Safe(contact.Kind)}; reason={Safe(quarantineReason)}; mutation=false; contactRetained=true; combatAuthority=false; tag={VanguardSquadTargetNoProgressQuarantine.StatusTag}; bridgeTag={StatusTag}");
                continue;
            }

            if (!IsLiveWorldTarget(contact.TargetId, out var liveReason))
            {
                InvalidateSquadCombatTarget(snapshot.OwnerProfileId, contact.TargetId, now, "fresh_contact_target_invalid:" + liveReason);
                continue;
            }

            selected = contact;
            reason = "target=" + Safe(contact.TargetId)
                + ";source=" + Safe(contact.SourceOperatorId)
                + ";kind=" + Safe(contact.Kind)
                + ";remaining=" + (contact.ExpiresAtUtc - now).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)
                + ";sourceReason=" + Safe(contact.Reason);
            return true;
        }

        reason = "no_live_group_contact_after_filter";
        return false;
    }

    public static bool PublishHostileGrenadeSourceContact(
        OperatorDecisionSnapshot snapshot,
        string sourceProfileId,
        float grenadeDistance,
        DateTimeOffset now,
        string reason)
    {
        if (snapshot == null || !snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.OwnerProfileId))
        {
            return false;
        }

        string targetId = Normalize(sourceProfileId);
        if (string.Equals(targetId, "none", StringComparison.OrdinalIgnoreCase)
            || IsProtectedFriendlyTarget(targetId, out _)
            || !IsLiveWorldTarget(targetId, out _))
        {
            return false;
        }

        DateTimeOffset until = now + UnifiedSquadContactTtl;
        bool changed;
        lock (Sync)
        {
            if (!SquadContactsByOwnerProfileId.TryGetValue(snapshot.OwnerProfileId, out var contacts))
            {
                contacts = new Dictionary<string, SquadCombatContactState>(StringComparer.OrdinalIgnoreCase);
                SquadContactsByOwnerProfileId[snapshot.OwnerProfileId] = contacts;
            }

            bool continuingEpisode = contacts.TryGetValue(targetId, out var previous)
                && previous.ExpiresAtUtc > now
                && now - previous.ObservedAtUtc <= UnifiedSquadContactTtl;
            DateTimeOffset episodeStartedAtUtc = continuingEpisode ? previous.EpisodeStartedAtUtc : now;
            contacts[targetId] = new SquadCombatContactState(
                snapshot.OwnerProfileId,
                snapshot.OperatorId,
                snapshot.BotProfileId,
                targetId,
                "hostile_grenade_source",
                "grenade_terminal:" + Safe(reason),
                grenadeDistance,
                observedAtUtc: now,
                episodeStartedAtUtc,
                expiresAtUtc: until);
            changed = !continuingEpisode;
        }

        VanguardClientDiagnosticsLog.Operational(VanguardGrenadeEmergencyPolicy.HostileSourcePropagatedTag, () =>
            $"owner={Safe(snapshot.OwnerProfileId)}; sourceOperator={Safe(snapshot.OperatorId)}; sourceBot={Safe(snapshot.BotProfileId)}; hostileSource={Safe(targetId)}; distance={(float.IsInfinity(grenadeDistance) || float.IsNaN(grenadeDistance) ? "unknown" : grenadeDistance.ToString("0.0", CultureInfo.InvariantCulture))}; contactKind=hostile_grenade_source; expiresIn={UnifiedSquadContactTtl.TotalSeconds:0.0}; changed={Bool(changed)}; evasionWindowAlreadyTerminal=true; assignment=individual_requalification; targetCommit=separate_from_evasion; reason={Safe(reason)}; tag={VanguardGrenadeEmergencyPolicy.StatusTag}; bridgeTag={StatusTag}");
        return true;
    }

    private static void PublishSquadCombatContact(OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot == null || !snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.OwnerProfileId))
        {
            return;
        }

        string targetId = "none";
        string kind = "none";
        string reason = "none";
        float distance = float.MaxValue;
        if (!string.Equals(Normalize(snapshot.Threat.EnemyId), "none", StringComparison.OrdinalIgnoreCase) && HasCurrentDirectProof(snapshot))
        {
            targetId = Normalize(snapshot.Threat.EnemyId);
            kind = snapshot.Threat.EnemyCanShoot == true || snapshot.Threat.ShotMeRecently == true || snapshot.Threat.ShotAtMeRecently == true
                ? "current_immediate_threat"
                : "current_direct_contact";
            reason = "current=" + Safe(snapshot.Threat.Classification);
            distance = snapshot.Threat.Distance ?? float.MaxValue;
        }
        else
        {
            string candidate = Normalize(snapshot.Awareness.CandidateId);
            if (string.Equals(candidate, "none", StringComparison.OrdinalIgnoreCase))
            {
                candidate = Normalize(snapshot.ThreatScan.CandidateThreatId);
            }

            if (!string.Equals(candidate, "none", StringComparison.OrdinalIgnoreCase) && HasCandidateDirectProof(snapshot))
            {
                targetId = candidate;
                kind = CandidateCanDefendNow(snapshot) ? "candidate_immediate_threat" : "candidate_direct_contact";
                reason = "candidate=" + Safe(snapshot.Awareness.Reason) + ";scan=" + Safe(snapshot.ThreatScan.PromotionReason);
                distance = CandidateDistance(snapshot);
            }
        }

        if (string.Equals(targetId, "none", StringComparison.OrdinalIgnoreCase)
            || IsProtectedFriendlyTarget(targetId, out _)
            || !IsLiveWorldTarget(targetId, out _))
        {
            return;
        }

        if (VanguardSquadTargetNoProgressQuarantine.IsCombatAuthorityBlocked(
            snapshot,
            targetId,
            now,
            out string quarantineReason))
        {
            LogThrottled(
                "DirectPublishBlocked|" + snapshot.OwnerProfileId + "|" + snapshot.BotProfileId + "|" + targetId,
                now,
                RejectLogInterval,
                $"VANGUARD_SQUAD_CONTACT_REFRESH_BLOCKED owner={Safe(snapshot.OwnerProfileId)}; operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(targetId)}; reason={Safe(quarantineReason)}; mutation=false; contactRetainedUntilExistingTtl=true; tag={VanguardSquadTargetNoProgressQuarantine.StatusTag}; bridgeTag={StatusTag}");
            return;
        }

        DateTimeOffset until = now + UnifiedSquadContactTtl;
        bool changed;
        SquadCombatContactState contact;
        lock (Sync)
        {
            if (!SquadContactsByOwnerProfileId.TryGetValue(snapshot.OwnerProfileId, out var contacts))
            {
                contacts = new Dictionary<string, SquadCombatContactState>(StringComparer.OrdinalIgnoreCase);
                SquadContactsByOwnerProfileId[snapshot.OwnerProfileId] = contacts;
            }

            bool continuingEpisode = contacts.TryGetValue(targetId, out var previous)
                && previous.ExpiresAtUtc > now
                // Target episodes survive reporter handoff and direct/immediate kind changes.
                // Otherwise two Operators observing the same hostile would reset the circuit on
                // every refresh and make the retry limit ineffective.
                && now - previous.ObservedAtUtc <= UnifiedSquadContactTtl;
            DateTimeOffset episodeStartedAtUtc = continuingEpisode ? previous.EpisodeStartedAtUtc : now;
            contact = new SquadCombatContactState(
                snapshot.OwnerProfileId,
                snapshot.OperatorId,
                snapshot.BotProfileId,
                targetId,
                kind,
                reason,
                distance,
                observedAtUtc: now,
                episodeStartedAtUtc,
                expiresAtUtc: until);
            changed = !continuingEpisode;
            contacts[targetId] = contact;
        }

        if (changed)
        {
            VanguardClientDiagnosticsLog.Info(SquadTravelCombatAuthorityStatusTag,
                $"VANGUARD_SQUAD_CONTACT_BROADCAST owner={Safe(snapshot.OwnerProfileId)}; sourceOperator={Safe(snapshot.OperatorId)}; sourceBot={Safe(snapshot.BotProfileId)}; target={Safe(targetId)}; kind={Safe(kind)}; distance={(distance == float.MaxValue ? "unknown" : distance.ToString("0.0", CultureInfo.InvariantCulture))}; expiresIn={UnifiedSquadContactTtl.TotalSeconds:0.0}; reason={Safe(reason)}; tag={SquadTravelCombatAuthorityStatusTag}; bridgeTag={StatusTag}");
        }
    }

    private static bool IsLiveWorldTarget(string? targetId, out string reason)
    {
        reason = "none";
        string normalized = Normalize(targetId);
        if (string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase))
        {
            reason = "target_none";
            return false;
        }

        var world = Singleton<GameWorld>.Instance;
        if (world == null)
        {
            // Do not destroy a valid contact merely because GameWorld is transiently unavailable.
            reason = "world_temporarily_missing_preserve_contact";
            return true;
        }

        var player = world.GetAlivePlayerByProfileID(normalized);
        if (player == null || player.HealthController?.IsAlive != true)
        {
            reason = "target_dead_or_unspawned";
            return false;
        }

        if (IsProtectedFriendlyTarget(normalized, out var friendlyReason))
        {
            reason = "protected_friendly:" + friendlyReason;
            return false;
        }

        reason = "live_hostile_candidate";
        return true;
    }

    private static bool TryBootstrapAndApplyTarget(OperatorDecisionSnapshot snapshot, BotOwner botOwner, string targetId, string reason, string assignmentKind, DateTimeOffset now, out SainTargetApplyResult result, out string before, out string after, out string bootstrapReason)
    {
        result = new SainTargetApplyResult(false, false, false, false, "not_attempted");
        before = ResolveCurrentSainGoalId(botOwner);
        after = before;
        bootstrapReason = "not_attempted";

        if (snapshot == null || botOwner == null || string.Equals(Normalize(targetId), "none", StringComparison.OrdinalIgnoreCase))
        {
            bootstrapReason = "invalid_input";
            return false;
        }

        string normalizedTarget = Normalize(targetId);
        if (VanguardSquadTargetNoProgressQuarantine.IsCombatAuthorityBlocked(
            snapshot,
            normalizedTarget,
            now,
            out string squadQuarantineReason))
        {
            bootstrapReason = "squad_target_quarantined_knowledge_only:" + Safe(squadQuarantineReason);
            result = new SainTargetApplyResult(false, false, false, false, bootstrapReason);
            return false;
        }

        bool directLocalSensorEvidence = HasDirectLocalSensorEvidenceForTarget(snapshot, normalizedTarget);
        bool individualAssignmentOrLocalEvidence = directLocalSensorEvidence
            || HasFreshUnifiedAssignmentForTarget(snapshot.BotProfileId, normalizedTarget, now, out _);
        bool trustedCloseThreatProof = string.Equals(assignmentKind, "post_kill_close_threat", StringComparison.OrdinalIgnoreCase);
        bool trustedOwnerImmediateProof = string.Equals(assignmentKind, "owner_immediate_threat", StringComparison.OrdinalIgnoreCase);
        bool trustedUnifiedAssignment = assignmentKind.StartsWith("unified_", StringComparison.OrdinalIgnoreCase);
        bool trustedUnifiedImmediateProof = trustedUnifiedAssignment
            && assignmentKind.EndsWith("_immediate", StringComparison.OrdinalIgnoreCase);
        bool targetApplicationProof = individualAssignmentOrLocalEvidence || trustedCloseThreatProof || trustedOwnerImmediateProof || trustedUnifiedAssignment;
        if (IsTargetApplyCircuitOpen(snapshot.BotProfileId, normalizedTarget, now, targetApplicationProof, out var circuitReason, out var circuitRemaining))
        {
            bootstrapReason = "target_apply_circuit_open:" + circuitReason + ":remaining=" + circuitRemaining.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);
            result = new SainTargetApplyResult(false, false, false, false, bootstrapReason);
            return false;
        }

        bool nearMissPressure = VanguardNearMissSuppressionService.IsRecent(snapshot.BotProfileId, now, out _);
        bool knownIncomingEvidence = snapshot.Threat.ShotMeRecently == true
            || snapshot.Threat.ShotAtMeRecently == true
            || snapshot.Awareness.IncomingFireFresh
            || HasCandidateIncomingFireFresh(snapshot);
        bool emergencyEvidence = trustedOwnerImmediateProof
            || trustedUnifiedImmediateProof
            || (targetApplicationProof && (HasCurrentImmediateProof(snapshot)
                || knownIncomingEvidence
                || (snapshot.Medical.Safety.IncomingFireRecent && !nearMissPressure)));

        if (TargetApplyGenerationByBotProfileId.TryGetValue(snapshot.BotProfileId, out var generationState)
            && string.Equals(generationState.TargetId, normalizedTarget, StringComparison.OrdinalIgnoreCase)
            && now - generationState.LastAttemptUtc < TimeSpan.FromSeconds(1.25d)
            && !emergencyEvidence)
        {
            bootstrapReason = "same_target_generation_deferred";
            result = new SainTargetApplyResult(false, false, false, false, bootstrapReason);
            return false;
        }

        if (!emergencyEvidence
            && !VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner("AwarenessTargetMutation", snapshot.OwnerProfileId, 1, 2, out var mutationBudgetReason))
        {
            bootstrapReason = "awareness_target_mutation_frame_budget_pending:" + mutationBudgetReason;
            result = new SainTargetApplyResult(false, false, false, false, bootstrapReason);
            return false;
        }

        TargetApplyGenerationByBotProfileId[snapshot.BotProfileId] = new TargetApplyGenerationState
        {
            TargetId = normalizedTarget,
            LastAttemptUtc = now,
            AssignmentKind = assignmentKind
        };

        // Vanguard qualification may request SAIN acquisition/attack without fabricating recipient LOS.
        // Visibility and under-fire flags remain local-only even when the target came from squad knowledge.
        bool markVisible = directLocalSensorEvidence && (HasCurrentImmediateProof(snapshot)
            || CandidateCanDefendNow(snapshot)
            || CandidateDistance(snapshot) <= 65.0f);
        bool markUnderFire = directLocalSensorEvidence && (knownIncomingEvidence
            || (snapshot.Medical.Safety.IncomingFireRecent && !nearMissPressure));

        if (!VanguardEnemyInfoBootstrapper.TryBootstrapTarget(botOwner, targetId, markVisible: markVisible, attackImmediately: targetApplicationProof, markUnderFire: markUnderFire, out var enemyInfo, out bootstrapReason))
        {
            OpenTargetApplyCircuit(snapshot.BotProfileId, normalizedTarget, "bootstrap_failed:" + bootstrapReason, now, targetApplicationProof);
            result = new SainTargetApplyResult(false, false, false, false, "bootstrap_failed:" + Safe(bootstrapReason));
            return false;
        }

        result = CommitQualifiedSainTarget(botOwner, targetId, enemyInfo, attackImmediately: targetApplicationProof);
        after = ResolveCurrentSainGoalId(botOwner);
        bool verified = result.Applied && SameTarget(after, targetId);
        if (verified)
        {
            ClearTargetApplyCircuit(snapshot.BotProfileId, normalizedTarget);
            if (TargetApplyGenerationByBotProfileId.TryGetValue(snapshot.BotProfileId, out var verifiedState))
            {
                verifiedState.LastVerifiedUtc = now;
            }
            RecordVerifiedSainGoalHandoff(snapshot.BotProfileId, targetId, "awareness_bootstrap:" + assignmentKind, now);
            VanguardMainIntentScheduler.NotifyCombatTargetApplied(snapshot.BotProfileId, targetId, "awareness_bootstrap:" + assignmentKind, now, verified: true);
            return true;
        }

        if (result.Applied)
        {
            result = new SainTargetApplyResult(false, result.SetEnemyController, result.SetMemory, result.CalcGoal, "write_without_verified_goal:after=" + Safe(after));
        }
        OpenTargetApplyCircuit(snapshot.BotProfileId, normalizedTarget, result.Reason, now, targetApplicationProof);
        return false;
    }

    // This is the final "may this remembered target still drive combat?" check. Direct local proof wins.
    // Without direct proof, the target survives only when a current individual/squad authority path still
    // justifies it; friendly, quarantined, stale or distant knowledge-only targets are released while the
    // contact memory itself is preserved for future reacquisition.
    private static bool ShouldDropCurrentTarget(OperatorDecisionSnapshot snapshot, out string reason, out string dropKind)
    {
        reason = "none";
        dropKind = "none";

        string currentTarget = Normalize(snapshot.Threat.EnemyId);
        if (string.Equals(currentTarget, "none", StringComparison.OrdinalIgnoreCase)
            && VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var targetRecord)
            && targetRecord.BotOwner != null
            && !targetRecord.BotOwner.IsDead)
        {
            currentTarget = ResolveCurrentSainGoalId(targetRecord.BotOwner);
        }

        bool hasKnownTarget = !string.Equals(currentTarget, "none", StringComparison.OrdinalIgnoreCase) || snapshot.Sain.HasEnemy == true || snapshot.Sain.Searching == true;
        if (!hasKnownTarget)
        {
            return false;
        }

        if (!string.Equals(currentTarget, "none", StringComparison.OrdinalIgnoreCase) && IsProtectedFriendlyTarget(currentTarget, out var friendlyReason))
        {
            reason = "protected_friendly_target_in_memory:" + friendlyReason;
            dropKind = "friendly_guard_release";
            return true;
        }

        if (HasCurrentDirectProof(snapshot))
        {
            return false;
        }

        DateTimeOffset targetDropNow = DateTimeOffset.UtcNow;
        if (!string.Equals(currentTarget, "none", StringComparison.OrdinalIgnoreCase)
            && IsDistantPursuitKnowledgeOnly(snapshot, currentTarget, out float distantDistance, out string distantReason)
            && !HasFreshDistantAuthorityAssignmentForTarget(
                snapshot.BotProfileId,
                currentTarget,
                targetDropNow,
                out _)
            && !TryResolveVerifiedSainGoalHandoff(
                snapshot.BotProfileId,
                currentTarget,
                targetDropNow,
                out _,
                out _))
        {
            reason = "distant_contact_knowledge_only:" + distantReason;
            dropKind = "distant_contact_knowledge_only";
            LogThrottled(
                "DropDistantGoal|" + snapshot.BotProfileId + "|" + currentTarget,
                targetDropNow,
                RejectLogInterval,
                $"VANGUARD_DISTANT_GOAL_RELEASE operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(currentTarget)}; distance={distantDistance:0.0}; reason={Safe(distantReason)}; directProof=false; verifiedHandoff=false; dropRequested=true; contactMemoryPreserved=true; tag={DistantPursuitKnowledgeOnlyStatusTag}");
            return true;
        }

        if (!string.Equals(currentTarget, "none", StringComparison.OrdinalIgnoreCase)
            && HasIndividualQualifiedAssignmentOrLocalEvidence(snapshot, currentTarget))
        {
            return false;
        }

        if (!string.Equals(currentTarget, "none", StringComparison.OrdinalIgnoreCase)
            && VanguardSquadTargetNoProgressQuarantine.IsCombatAuthorityBlocked(
                snapshot,
                currentTarget,
                DateTimeOffset.UtcNow,
                out string squadNoProgressReason))
        {
            reason = "squad_target_no_progress_quarantine:" + squadNoProgressReason;
            dropKind = "target_dropped_stale";
            return true;
        }

        if (!string.Equals(currentTarget, "none", StringComparison.OrdinalIgnoreCase)
            && IsTargetQuarantined(snapshot.BotProfileId, currentTarget, DateTimeOffset.UtcNow, out var quarantineReason, out var quarantineRemaining))
        {
            reason = "target_quarantined_reacquired_no_direct_proof:" + quarantineReason + ";remaining=" + quarantineRemaining.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);
            dropKind = "target_dropped_stale";
            return true;
        }

        float bubble = snapshot.SquadCohesion.OperatorDistanceToOwner;
        bool searchOrKnown = snapshot.Sain.Searching == true
            || string.Equals(snapshot.Sain.Classification, "sain_search", StringComparison.OrdinalIgnoreCase)
            || string.Equals(snapshot.Sain.Classification, "sain_enemy_known", StringComparison.OrdinalIgnoreCase)
            || string.Equals(snapshot.Brain.Classification, "brain_combat_related", StringComparison.OrdinalIgnoreCase);

        if (snapshot.Threat.StaleThreat)
        {
            reason = "target_stale_no_direct_proof";
            dropKind = "target_dropped_stale";
            return true;
        }

        if (searchOrKnown && bubble > Vanguard.Client.Runtime.SquadCohesion.VanguardSquadCohesionDoctrine.TacticalBubbleRadiusMeters)
        {
            reason = "sain_search_outside_tactical_bubble_no_direct_proof;bubble=" + bubble.ToString("0.00", CultureInfo.InvariantCulture);
            dropKind = "sain_search_released_for_formation";
            return true;
        }

        if (searchOrKnown && bubble > Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.SoftCorrectionMeters && IsOldOrUnknownSeen(snapshot.Threat.TimeSinceSeen, 8.0f))
        {
            reason = "sain_search_soft_out_of_envelope_stale_seen;bubble=" + bubble.ToString("0.00", CultureInfo.InvariantCulture);
            dropKind = "target_dropped_out_of_envelope";
            return true;
        }

        if (IsInvalidPathLength(snapshot.Threat.PathLength) && IsOldOrUnknownSeen(snapshot.Threat.TimeSinceSeen, 6.0f))
        {
            reason = "target_path_invalid_without_recent_proof";
            dropKind = "target_dropped_out_of_envelope";
            return true;
        }

        if (snapshot.Threat.PathLength.HasValue && snapshot.Threat.PathLength.Value > 125f && IsOldOrUnknownSeen(snapshot.Threat.TimeSinceSeen, 6.0f))
        {
            reason = "target_path_too_long_without_recent_proof:path=" + snapshot.Threat.PathLength.Value.ToString("0.00", CultureInfo.InvariantCulture);
            dropKind = "target_dropped_out_of_envelope";
            return true;
        }

        if (snapshot.Threat.BotDistanceFromLastKnown.HasValue && snapshot.Threat.BotDistanceFromLastKnown.Value > 55f && IsOldOrUnknownSeen(snapshot.Threat.TimeSinceSeen, 6.0f))
        {
            reason = "bot_far_from_last_known_without_recent_proof:dist=" + snapshot.Threat.BotDistanceFromLastKnown.Value.ToString("0.00", CultureInfo.InvariantCulture);
            dropKind = "target_dropped_out_of_envelope";
            return true;
        }

        return false;
    }

    private static void TryDropStaleTarget(OperatorDecisionSnapshot snapshot, BotOwner botOwner, string reason, string dropKind, DateTimeOffset now)
    {
        if (IsCooldownActive(DropCooldownUntilByBotProfileId, snapshot.BotProfileId, now, out var remaining))
        {
            LogThrottled("dropCooldown|" + snapshot.BotProfileId, now, RejectLogInterval,
                $"VANGUARD_AWARENESS_BRIDGE_REJECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(snapshot.Threat.EnemyId)}; reason=drop_cooldown; remaining={remaining.TotalSeconds:0.00}; tag={StatusTag}");
            return;
        }

        string before = ResolveCurrentSainGoalId(botOwner);
        string targetToClear = ResolveTargetToClear(snapshot, before);
        var result = ClearStaleTarget(botOwner, before, escalated: false);
        string after = ResolveCurrentSainGoalId(botOwner);
        MarkCooldown(DropCooldownUntilByBotProfileId, snapshot.BotProfileId, now + DropCooldown);
        AddTargetQuarantine(snapshot.BotProfileId, targetToClear, reason, now);
        AddTargetQuarantine(snapshot.BotProfileId, before, "clear_before:" + reason, now);
        StartPendingTargetClear(snapshot, targetToClear, before, reason, dropKind, now);

        string eventName = string.Equals(dropKind, "sain_search_released_for_formation", StringComparison.OrdinalIgnoreCase)
            ? "VANGUARD_SAIN_SEARCH_RELEASED_FOR_FORMATION"
            : "VANGUARD_TARGET_DROPPED_STALE";

        VanguardClientDiagnosticsLog.Info(StaleTargetReleaseStatusTag,
            $"{eventName} operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(targetToClear)}; before={Safe(before)}; after={Safe(after)}; reason={Safe(reason)}; result={Safe(result.Reason)}; clearEnemyController={Bool(result.SetEnemyController)}; clearMemory={Bool(result.SetMemory)}; calcGoal={Bool(result.CalcGoal)}; pendingVerify=true; quarantineSeconds={StaleTargetQuarantine.TotalSeconds:0.00}; bubble={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; threat={Safe(snapshot.Threat.Classification)}; sain={Safe(snapshot.Sain.Classification)}; tag={StaleTargetReleaseStatusTag}; clearConfirmTag={TargetClearConfirmStatusTag}; quarantineTag={StaleTargetQuarantineStatusTag}; bridgeTag={StatusTag}");
    }

    private static bool ProcessPendingTargetClear(OperatorDecisionSnapshot snapshot, BotOwner botOwner, DateTimeOffset now)
    {
        PendingTargetClearState pending;
        lock (Sync)
        {
            if (!PendingTargetClearByBotProfileId.TryGetValue(snapshot.BotProfileId, out pending))
            {
                return false;
            }
        }

        string freshCandidate = Normalize(snapshot.Awareness.CandidateId);
        if (string.Equals(freshCandidate, "none", StringComparison.OrdinalIgnoreCase))
        {
            freshCandidate = Normalize(snapshot.ThreatScan.CandidateThreatId);
        }

        bool differentFreshCandidate = !string.Equals(freshCandidate, "none", StringComparison.OrdinalIgnoreCase)
            && !PendingMatches(pending, freshCandidate)
            && (snapshot.Awareness.CandidateVisible
                || snapshot.Awareness.CandidateLineOfSight
                || snapshot.Awareness.IncomingFireFresh
                || snapshot.ThreatScan.CandidateVisible
                || snapshot.ThreatScan.CandidateLineOfSight
                || snapshot.ThreatScan.CandidateIncomingFireFresh);
        if (differentFreshCandidate)
        {
            lock (Sync)
            {
                PendingTargetClearByBotProfileId.Remove(snapshot.BotProfileId);
            }

            VanguardClientDiagnosticsLog.Info(TargetClearConfirmStatusTag,
                $"VANGUARD_TARGET_CLEAR_CANCELLED_FOR_NEW_CONTACT operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; staleTarget={Safe(pending.TargetId)}; newTarget={Safe(freshCandidate)}; mutation=false; doctrine=new_contact_always_preempts_pending_clear; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; clearTag={TargetClearConfirmStatusTag}");
            return false;
        }

        if (HasIndividualQualifiedAssignmentOrLocalEvidence(snapshot, pending.TargetId))
        {
            lock (Sync)
            {
                PendingTargetClearByBotProfileId.Remove(snapshot.BotProfileId);
            }

            ClearTargetQuarantine(snapshot.BotProfileId, pending.TargetId, "individual_assignment_preserves_target");
            VanguardClientDiagnosticsLog.Info(VanguardSharedContactAssistStatusTags.TargetClearProtection,
                $"VANGUARD_TARGET_CLEAR_ABORTED_FOR_INDIVIDUAL_ASSIGNMENT operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(pending.TargetId)}; attempts={pending.Attempts}; mutation=clear_cancelled; doctrine=individual_assignment_prevents_clear_reacquire_loop; tag={VanguardSharedContactAssistStatusTags.TargetClearProtection}; clearTag={TargetClearConfirmStatusTag}; bridgeTag={StatusTag}");
            return false;
        }

        if (now < pending.NextCheckAtUtc)
        {
            return false;
        }

        string current = ResolveCurrentSainGoalId(botOwner);
        string snapshotTarget = Normalize(snapshot.Threat.EnemyId);
        bool reacquired = PendingMatches(pending, current) || PendingMatches(pending, snapshotTarget);
        if (!reacquired)
        {
            lock (Sync)
            {
                PendingTargetClearByBotProfileId.Remove(snapshot.BotProfileId);
            }

            VanguardClientDiagnosticsLog.Info(TargetClearConfirmStatusTag,
                $"VANGUARD_TARGET_CLEAR_CONFIRMED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(pending.TargetId)}; current={Safe(current)}; snapshotTarget={Safe(snapshotTarget)}; attempts={pending.Attempts}; reason={Safe(pending.Reason)}; dropKind={Safe(pending.DropKind)}; tag={TargetClearConfirmStatusTag}; bridgeTag={StatusTag}");
            return false;
        }

        if (HasIndividualQualifiedAssignmentOrLocalEvidence(snapshot, pending.TargetId))
        {
            lock (Sync)
            {
                PendingTargetClearByBotProfileId.Remove(snapshot.BotProfileId);
            }

            VanguardClientDiagnosticsLog.Info(TargetClearConfirmStatusTag,
                $"VANGUARD_TARGET_CLEAR_ABORTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(pending.TargetId)}; current={Safe(current)}; reason=individual_assignment_or_direct_evidence_reappeared_preserve_sain; attempts={pending.Attempts}; tag={TargetClearConfirmStatusTag}; bridgeTag={StatusTag}");
            return false;
        }

        if (pending.Attempts <= 0 && now <= pending.ExpiresAtUtc)
        {
            var result = ClearStaleTarget(botOwner, current, escalated: true);
            string after = ResolveCurrentSainGoalId(botOwner);
            var next = pending.WithAttempt(now + TargetClearVerifyDelay);
            lock (Sync)
            {
                PendingTargetClearByBotProfileId[snapshot.BotProfileId] = next;
            }

            AddTargetQuarantine(snapshot.BotProfileId, current, "reacquired_after_clear:" + pending.Reason, now);
            VanguardClientDiagnosticsLog.Warning(TargetClearConfirmStatusTag,
                $"VANGUARD_TARGET_CLEAR_REACQUIRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(pending.TargetId)}; current={Safe(current)}; snapshotTarget={Safe(snapshotTarget)}; action=escalated_clear; result={Safe(result.Reason)}; after={Safe(after)}; attempts={next.Attempts}; reason={Safe(pending.Reason)}; tag={TargetClearConfirmStatusTag}; quarantineTag={StaleTargetQuarantineStatusTag}; bridgeTag={StatusTag}");
            return false;
        }

        lock (Sync)
        {
            PendingTargetClearByBotProfileId.Remove(snapshot.BotProfileId);
        }

        // A target that SAIN/group memory immediately rebuilds twice is not cleared again every few
        // seconds. Keep the quarantine/search state, sleep the destructive clear path, and release
        // the guard immediately when a new individually qualified assignment appears.
        MarkCooldown(DropCooldownUntilByBotProfileId, snapshot.BotProfileId, now + TargetClearUnconfirmedBackoff);
        AddTargetQuarantine(snapshot.BotProfileId, pending.TargetId, "unconfirmed_clear_backoff:" + pending.Reason, now);

        VanguardClientDiagnosticsLog.Warning(VanguardRuntimeConvergenceStatusTags.TargetClearBackoff,
            $"VANGUARD_TARGET_CLEAR_UNCONFIRMED_BACKOFF operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(pending.TargetId)}; current={Safe(current)}; snapshotTarget={Safe(snapshotTarget)}; attempts={pending.Attempts}; reason={Safe(pending.Reason)}; backoffSeconds={TargetClearUnconfirmedBackoff.TotalSeconds:0.0}; action=preserve_search_quarantine_without_repeated_clear; individualAssignmentBypass=true; tag={VanguardRuntimeConvergenceStatusTags.TargetClearBackoff}; clearTag={TargetClearConfirmStatusTag}; quarantineTag={StaleTargetQuarantineStatusTag}; bridgeTag={StatusTag}");
        return false;
    }

    private static void StartPendingTargetClear(OperatorDecisionSnapshot snapshot, string targetToClear, string before, string reason, string dropKind, DateTimeOffset now)
    {
        var state = new PendingTargetClearState(targetToClear, before, reason, dropKind, now + TargetClearVerifyDelay, now + TargetClearVerifyTimeout, 0);
        lock (Sync)
        {
            PendingTargetClearByBotProfileId[snapshot.BotProfileId] = state;
        }

        VanguardClientDiagnosticsLog.Info(TargetClearConfirmStatusTag,
            $"VANGUARD_TARGET_CLEAR_PENDING operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(targetToClear)}; before={Safe(before)}; verifyIn={TargetClearVerifyDelay.TotalSeconds:0.00}; timeout={TargetClearVerifyTimeout.TotalSeconds:0.00}; reason={Safe(reason)}; dropKind={Safe(dropKind)}; tag={TargetClearConfirmStatusTag}; bridgeTag={StatusTag}");
    }

    private static string ResolveTargetToClear(OperatorDecisionSnapshot snapshot, string before)
    {
        string current = Normalize(snapshot.Threat.EnemyId);
        if (!string.Equals(current, "none", StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        if (!string.Equals(Normalize(before), "none", StringComparison.OrdinalIgnoreCase))
        {
            return Normalize(before);
        }

        return Normalize(snapshot.Awareness.CandidateId);
    }

    private static bool PendingMatches(PendingTargetClearState pending, string targetId)
    {
        return SameTarget(pending.TargetId, targetId) || SameTarget(pending.BeforeId, targetId);
    }

    private static string TargetCircuitKey(string botProfileId, string targetId)
        => Normalize(botProfileId) + "|" + Normalize(targetId);

    private static void OpenTargetApplyCircuit(string botProfileId, string targetId, string reason, DateTimeOffset now, bool qualifiedAssignment)
    {
        string key = TargetCircuitKey(botProfileId, targetId);
        TimeSpan backoff = qualifiedAssignment ? TargetApplyDirectFailureBackoff : TargetApplyUnprovenFailureBackoff;
        lock (Sync)
        {
            int failures = 1;
            if (TargetApplyCircuitByBotAndTarget.TryGetValue(key, out var existing) && existing.UntilUtc > now)
            {
                failures = existing.Failures + 1;
                backoff += TimeSpan.FromSeconds(Math.Min(12.0d, failures * 1.5d));
            }
            TargetApplyCircuitByBotAndTarget[key] = new TargetApplyCircuitState(Normalize(targetId), Safe(reason), now + backoff, failures, qualifiedAssignment);
        }
    }

    private static bool IsTargetApplyCircuitOpen(string botProfileId, string targetId, DateTimeOffset now, out string reason, out TimeSpan remaining)
        => IsTargetApplyCircuitOpen(botProfileId, targetId, now, false, out reason, out remaining);

    private static bool IsTargetApplyCircuitOpen(string botProfileId, string targetId, DateTimeOffset now, bool currentQualifiedAssignment, out string reason, out TimeSpan remaining)
    {
        string key = TargetCircuitKey(botProfileId, targetId);
        lock (Sync)
        {
            if (TargetApplyCircuitByBotAndTarget.TryGetValue(key, out var state))
            {
                if (state.UntilUtc > now)
                {
                    // A circuit from an older episode is immediately released by genuinely new
                    // individual qualification. A failed apply on the same qualified episode remains briefly bounded.
                    if (currentQualifiedAssignment && !state.QualifiedAssignmentAtOpen)
                    {
                        TargetApplyCircuitByBotAndTarget.Remove(key);
                        reason = "released_by_new_individual_qualified_assignment";
                        remaining = TimeSpan.Zero;
                        return false;
                    }

                    reason = state.Reason + ":failures=" + state.Failures.ToString(CultureInfo.InvariantCulture);
                    remaining = state.UntilUtc - now;
                    return true;
                }
                TargetApplyCircuitByBotAndTarget.Remove(key);
            }
        }
        reason = "none";
        remaining = TimeSpan.Zero;
        return false;
    }

    public static bool IsTargetApplyDeferredReason(string? reason)
    {
        string value = Normalize(reason);
        return value.IndexOf("target_apply_circuit_open", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("same_target_generation_deferred", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("awareness_target_mutation_frame_budget_pending", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void ClearTargetApplyCircuit(string botProfileId, string targetId)
    {
        lock (Sync)
        {
            TargetApplyCircuitByBotAndTarget.Remove(TargetCircuitKey(botProfileId, targetId));
        }
    }

    private static void AddTargetQuarantine(string botProfileId, string targetId, string reason, DateTimeOffset now)
    {
        targetId = Normalize(targetId);
        if (string.Equals(targetId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var until = now + StaleTargetQuarantine;
        string rootReason = ReasonRoot(reason);
        string reasonCode = ReasonCode(rootReason);
        string key = QuarantineKey(botProfileId, targetId);
        bool materialTransition = true;
        lock (Sync)
        {
            if (QuarantinedTargetByBotAndTarget.TryGetValue(key, out var existing)
                && existing.UntilUtc > now
                && string.Equals(existing.ReasonCode, reasonCode, StringComparison.OrdinalIgnoreCase))
            {
                // Runtime invariant: the same quarantine episode is stable state, not a transition. Do not
                // rebuild the reason or rewrite/log it every tick. Extend only when the previous
                // episode is close to expiry, which preserves the original stale-target safety.
                materialTransition = existing.UntilUtc - now <= TimeSpan.FromSeconds(3.0d);
                if (!materialTransition)
                {
                    return;
                }
            }

            QuarantinedTargetByBotAndTarget[key] = new QuarantinedTargetState(targetId, reasonCode, rootReason, until);
        }

        LogThrottled("quarantine|" + key + "|" + rootReason, now, TimeSpan.FromSeconds(2.5d),
            $"VANGUARD_TARGET_QUARANTINED botProfile={Safe(botProfileId)}; target={Safe(targetId)}; untilUtc={until:O}; seconds={StaleTargetQuarantine.TotalSeconds:0.00}; reasonCode={Safe(reasonCode)}; reasonDetail={Safe(rootReason)}; transition={Bool(materialTransition)}; bypass=direct_proof_or_immediate_contact; typedEpisodeTag={VanguardPrimaryExecutionContract.AwarenessTypedEpisodeStatusTag}; typedChurnTag={VanguardPrimaryExecutionContract.TypedAwarenessChurnStatusTag}; tag={StaleTargetQuarantineStatusTag}; bridgeTag={StatusTag}");
    }

    private static void ClearTargetQuarantine(string botProfileId, string targetId, string reason)
    {
        string target = Normalize(targetId);
        if (string.Equals(target, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool removed;
        lock (Sync)
        {
            removed = QuarantinedTargetByBotAndTarget.Remove(QuarantineKey(botProfileId, target));
        }

        if (removed)
        {
            VanguardClientDiagnosticsLog.Info(VanguardSharedContactAssistStatusTags.TargetClearProtection,
                $"VANGUARD_TARGET_QUARANTINE_RELEASED botProfile={Safe(botProfileId)}; target={Safe(target)}; reason={Safe(reason)}; doctrine=new_individual_assignment_or_local_evidence_releases_stale_clear_guard; tag={VanguardSharedContactAssistStatusTags.TargetClearProtection}; quarantineTag={StaleTargetQuarantineStatusTag}; bridgeTag={StatusTag}");
        }
    }

    private static bool IsTargetQuarantined(string botProfileId, string targetId, DateTimeOffset now, out string reason, out TimeSpan remaining)
    {
        reason = "none";
        remaining = TimeSpan.Zero;
        targetId = Normalize(targetId);
        if (string.Equals(targetId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (Sync)
        {
            string key = QuarantineKey(botProfileId, targetId);
            if (!QuarantinedTargetByBotAndTarget.TryGetValue(key, out var quarantine))
            {
                return false;
            }

            if (quarantine.UntilUtc <= now)
            {
                QuarantinedTargetByBotAndTarget.Remove(key);
                return false;
            }

            reason = quarantine.ReasonCode;
            remaining = quarantine.UntilUtc - now;
            return true;
        }
    }

    private static string QuarantineKey(string botProfileId, string targetId)
    {
        return Normalize(botProfileId) + "|" + Normalize(targetId);
    }

    private static SainTargetApplyResult ClearStaleTarget(BotOwner botOwner, string targetId, bool escalated)
    {
        bool enemyControllerSet = false;
        bool memorySet = false;
        bool calcGoal = false;
        string reason = "none";

        try
        {
            object? sainComponent = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "SAIN.Components.BotComponent");
            object? enemyController = ResolveEnemyController(sainComponent);
            enemyControllerSet = TrySetMember(enemyController, "GoalEnemy", null) | enemyControllerSet;
            if (escalated)
            {
                enemyControllerSet = TrySetMember(enemyController, "LastGoalEnemy", null) | enemyControllerSet;
                enemyControllerSet = TrySetMember(enemyController, "LastEnemy", null) | enemyControllerSet;
                enemyControllerSet = TrySetMember(enemyController, "Enemy", null) | enemyControllerSet;
            }

            TryInvokeOneArg(enemyController, "setGoalEnemy", null);

            object? memory = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Memory");
            memorySet = TrySetMember(memory, "GoalEnemy", null) | memorySet;
            memorySet = TrySetMember(memory, "LastEnemy", null) | memorySet;
            memorySet = TrySetMember(memory, "AttackImmediately", false) | memorySet;
            if (escalated)
            {
                memorySet = TrySetMember(memory, "LastGoalEnemy", null) | memorySet;
                memorySet = TrySetMember(memory, "Enemy", null) | memorySet;
                memorySet = TrySetMember(memory, "DangerPlace", null) | memorySet;
            }

            calcGoal = TryInvokeNoArg(botOwner, "CalcGoal");
            reason = enemyControllerSet || memorySet ? (escalated ? "cleared_escalated" : "cleared") : "no_writable_target_member";
            return new SainTargetApplyResult(enemyControllerSet || memorySet, enemyControllerSet, memorySet, calcGoal, reason);
        }
        catch (Exception exception)
        {
            return new SainTargetApplyResult(false, enemyControllerSet, memorySet, calcGoal, exception.GetType().Name + ":" + Safe(exception.Message));
        }
    }

    private static object? ResolveEnemyController(object? sainComponent)
    {
        return VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainComponent, "EnemyController")
            ?? VanguardOperatorRuntimeAuditReflection.GetDeep(sainComponent, "Bot", "EnemyController")
            ?? VanguardOperatorRuntimeAuditReflection.GetDeep(sainComponent, "BotOwner", "EnemyController")
            ?? VanguardOperatorRuntimeAuditReflection.GetDeep(sainComponent, "SAINBot", "EnemyController");
    }

    private static string ResolveCurrentSainGoalId(BotOwner botOwner)
    {
        object? sainComponent = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "SAIN.Components.BotComponent");
        object? enemyController = ResolveEnemyController(sainComponent);
        object? goal = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemyController, "GoalEnemy");
        string id = ResolveEnemyProfileId(goal);
        if (!string.Equals(id, "none", StringComparison.OrdinalIgnoreCase))
        {
            return id;
        }

        object? memory = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Memory");
        object? memoryGoal = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(memory, "GoalEnemy");
        return ResolveEnemyProfileId(memoryGoal);
    }

    private static string ResolveEnemyProfileId(object? enemyLike)
    {
        if (enemyLike == null)
        {
            return "none";
        }

        if (enemyLike is IPlayer player)
        {
            string playerId = Normalize(player.ProfileId);
            if (!string.Equals(playerId, "none", StringComparison.OrdinalIgnoreCase))
            {
                return playerId;
            }
        }

        string id = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemyLike, "EnemyProfileId", "ProfileId", "Id"));
        if (!string.Equals(id, "none", StringComparison.OrdinalIgnoreCase))
        {
            return id;
        }

        id = Text(VanguardOperatorRuntimeAuditReflection.GetDeep(enemyLike, "EnemyInfo", "ProfileId"));
        if (!string.Equals(id, "none", StringComparison.OrdinalIgnoreCase))
        {
            return id;
        }

        id = Text(VanguardOperatorRuntimeAuditReflection.GetDeep(enemyLike, "EnemyInfo", "Person", "ProfileId"));
        if (!string.Equals(id, "none", StringComparison.OrdinalIgnoreCase))
        {
            return id;
        }

        id = Text(VanguardOperatorRuntimeAuditReflection.GetDeep(enemyLike, "Person", "ProfileId"));
        if (!string.Equals(id, "none", StringComparison.OrdinalIgnoreCase))
        {
            return id;
        }

        id = Text(VanguardOperatorRuntimeAuditReflection.GetDeep(enemyLike, "Profile", "Id"));
        if (!string.Equals(id, "none", StringComparison.OrdinalIgnoreCase))
        {
            return id;
        }

        return Text(VanguardOperatorRuntimeAuditReflection.GetDeep(enemyLike, "Person", "Profile", "Id"));
    }

    private static IEnumerable<object> Enumerate(object? values)
    {
        if (values is IEnumerable enumerable)
        {
            foreach (object value in enumerable)
            {
                if (value != null)
                {
                    yield return value;
                }
            }
        }
    }

    private static bool CandidateCanDefendNow(OperatorDecisionSnapshot snapshot)
    {
        return snapshot.Awareness.CandidateCanShoot
            || snapshot.ThreatScan.CandidateCanShoot
            || snapshot.Awareness.IncomingFireFresh
            || snapshot.ThreatScan.CandidateIncomingFireFresh
            || snapshot.ThreatScan.CandidateShotMeRecently
            || snapshot.ThreatScan.CandidateShotAtMeRecently;
    }

    private static bool HasCandidateDirectProof(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot.Awareness.CandidateVisible || snapshot.Awareness.CandidateLineOfSight || snapshot.Awareness.CandidateCanShoot || snapshot.Awareness.IncomingFireFresh)
        {
            return true;
        }

        return snapshot.ThreatScan.CandidateVisible
            || snapshot.ThreatScan.CandidateLineOfSight
            || snapshot.ThreatScan.CandidateCanShoot
            || snapshot.ThreatScan.CandidateIncomingFireFresh;
    }

    private static bool HasCandidateIncomingFireFresh(OperatorDecisionSnapshot snapshot)
    {
        return snapshot.Awareness.IncomingFireFresh || snapshot.ThreatScan.CandidateIncomingFireFresh;
    }

    private static float CandidateDistance(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot.Awareness.CandidateDistance.HasValue && snapshot.Awareness.CandidateDistance.Value >= 0f)
        {
            return snapshot.Awareness.CandidateDistance.Value;
        }

        if (snapshot.ThreatScan.CandidateDistance.HasValue && snapshot.ThreatScan.CandidateDistance.Value >= 0f)
        {
            return snapshot.ThreatScan.CandidateDistance.Value;
        }

        return float.MaxValue;
    }

    private static bool HasCurrentDirectProof(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot.Threat.EnemyVisible == true || snapshot.Threat.EnemyLineOfSight == true || snapshot.Threat.EnemyCanShoot == true || snapshot.Threat.ShotMeRecently == true || snapshot.Threat.ShotAtMeRecently == true)
        {
            return true;
        }

        if (snapshot.Brain.VanillaGoalEnemyVisible == true || snapshot.Brain.VanillaGoalEnemyCanShoot == true)
        {
            return true;
        }

        return snapshot.Threat.Distance.HasValue
            && snapshot.Threat.Distance.Value <= 25f
            && snapshot.Threat.TimeSinceSeen.HasValue
            && snapshot.Threat.TimeSinceSeen.Value >= 0f
            && snapshot.Threat.TimeSinceSeen.Value <= 4.0f;
    }

    private static bool HasCurrentImmediateProof(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot.Medical.Safety.EnemyCanShoot || snapshot.Medical.Safety.IncomingFireRecent || snapshot.Threat.EnemyCanShoot == true || snapshot.Threat.ShotMeRecently == true || snapshot.Threat.ShotAtMeRecently == true)
        {
            return true;
        }

        bool closeFreshVisible = snapshot.Threat.Distance.HasValue
            && snapshot.Threat.Distance.Value <= 12.0f
            && (snapshot.Threat.EnemyVisible == true || snapshot.Threat.EnemyLineOfSight == true)
            && snapshot.Threat.TimeSinceSeen.HasValue
            && snapshot.Threat.TimeSinceSeen.Value >= 0f
            && snapshot.Threat.TimeSinceSeen.Value <= 2.0f;
        return closeFreshVisible;
    }

    private static bool IsInvalidPathLength(float? pathLength)
    {
        return pathLength.HasValue && (float.IsNaN(pathLength.Value) || float.IsInfinity(pathLength.Value) || pathLength.Value >= 1000000f || pathLength.Value < 0f);
    }

    private static string ReasonRoot(string? reason)
    {
        string text = Safe(reason);
        string[] prefixes =
        {
            "target_quarantined_reacquired_no_direct_proof:",
            "target_quarantined_without_direct_proof:",
            "reacquired_after_clear:",
            "clear_before:"
        };

        for (int pass = 0; pass < 12; pass++)
        {
            int cut = FindVolatileReasonSuffix(text);
            if (cut >= 0)
            {
                text = text.Substring(0, cut);
            }

            bool changed = false;
            foreach (string prefix in prefixes)
            {
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    text = text.Substring(prefix.Length);
                    changed = true;
                    break;
                }
            }

            if (!changed && cut < 0)
            {
                break;
            }
        }

        text = text.Trim(' ', ';', ':', ',', '|', '_');
        if (text.Length > 180)
        {
            text = text.Substring(0, 180) + "_truncated";
        }

        return string.IsNullOrWhiteSpace(text) ? "unknown_quarantine_reason" : text;
    }

    private static int FindVolatileReasonSuffix(string text)
    {
        string[] markers =
        {
            ";remaining=", ":remaining=", ",remaining=", "|remaining=", " remaining=",
            ";rema_truncated", ":rema_truncated", ",rema_truncated", "|rema_truncated", " rema_truncated"
        };
        int earliest = -1;
        foreach (string marker in markers)
        {
            int index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (earliest < 0 || index < earliest))
            {
                earliest = index;
            }
        }
        return earliest;
    }

    private static string ReasonCode(string rootReason)
    {
        string text = ReasonRoot(rootReason);
        int separator = text.IndexOfAny(new[] { ':', ';', ',', '|' });
        if (separator > 0)
        {
            text = text.Substring(0, separator);
        }
        return string.IsNullOrWhiteSpace(text) ? "unknown_quarantine_reason" : text;
    }

    private static bool HasSeriousStationaryMedicalNeed(OperatorDecisionSnapshot snapshot)
    {
        var need = snapshot.Medical.Need;
        if (!need.HasAnyNeed)
        {
            return false;
        }

        return need.HasDestroyedPart || need.HasBlackBroken || need.HasFracture || need.HealthPercent <= 45;
    }

    private static bool IsOldOrUnknownSeen(float? seenAgo, float thresholdSeconds)
    {
        return !seenAgo.HasValue || seenAgo.Value < 0f || seenAgo.Value >= thresholdSeconds;
    }

    private static bool IsProtectedFriendlyTarget(string targetId, out string reason)
    {
        reason = "none";
        if (string.Equals(targetId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var record in VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators())
        {
            if (SameTarget(record.BotProfileId, targetId) || SameTarget(record.OperatorId, targetId))
            {
                reason = "operator_runtime";
                return true;
            }
        }

        foreach (string ownerId in VanguardRaidOperatorRuntimeRegistry.GetKnownOwnerProfileIds())
        {
            if (SameTarget(ownerId, targetId))
            {
                reason = "player_owner";
                return true;
            }
        }

        return false;
    }

    private static bool TrySetMember(object? instance, string name, object? value)
    {
        if (instance == null || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var type = instance.GetType();
            var property = type.GetProperty(name, flags);
            if (property != null && property.CanWrite && property.GetIndexParameters().Length == 0)
            {
                if (value == null && property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) == null)
                {
                    return false;
                }

                property.SetValue(instance, value);
                return true;
            }

            var field = type.GetField(name, flags);
            if (field != null)
            {
                if (value == null && field.FieldType.IsValueType && Nullable.GetUnderlyingType(field.FieldType) == null)
                {
                    return false;
                }

                field.SetValue(instance, value);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryInvokeNoArg(object? instance, string name)
    {
        if (instance == null || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var method = instance.GetType().GetMethod(name, flags, null, Type.EmptyTypes, null);
            if (method == null)
            {
                return false;
            }

            method.Invoke(instance, Array.Empty<object>());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryInvokeOneArg(object? instance, string name, object? arg)
    {
        if (instance == null || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var method in instance.GetType().GetMethods(flags).Where(candidate => candidate.Name == name))
            {
                var parameters = method.GetParameters();
                if (parameters.Length != 1)
                {
                    continue;
                }

                if (arg == null && parameters[0].ParameterType.IsValueType && Nullable.GetUnderlyingType(parameters[0].ParameterType) == null)
                {
                    continue;
                }

                if (arg != null && !parameters[0].ParameterType.IsInstanceOfType(arg))
                {
                    continue;
                }

                method.Invoke(instance, new[] { arg });
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool IsCooldownActive(Dictionary<string, DateTimeOffset> cooldowns, string botProfileId, DateTimeOffset now, out TimeSpan remaining)
    {
        lock (Sync)
        {
            if (cooldowns.TryGetValue(botProfileId, out var until) && until > now)
            {
                remaining = until - now;
                return true;
            }
        }

        remaining = TimeSpan.Zero;
        return false;
    }

    private static void MarkCooldown(Dictionary<string, DateTimeOffset> cooldowns, string botProfileId, DateTimeOffset until)
    {
        lock (Sync)
        {
            cooldowns[botProfileId] = until;
        }
    }

    private static void LogThrottled(string key, DateTimeOffset now, TimeSpan interval, string message)
    {
        bool due;
        lock (Sync)
        {
            due = !LastLogAtByKey.TryGetValue(key, out var last) || now - last >= interval;
            if (due)
            {
                LastLogAtByKey[key] = now;
            }
        }

        if (due)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag, message);
        }
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    }

    private static bool SameTarget(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && !string.Equals(left, "none", StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsText(string? value, string token)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string Text(object? value)
    {
        if (value == null)
        {
            return "none";
        }

        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text) ? "none" : text.Trim();
    }

    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        return value.Replace('\r', ' ').Replace('\n', ' ').Replace(';', ',').Replace('|', ',').Trim();
    }

    private static string Bool(bool value) => value ? "true" : "false";


    private sealed class TargetApplyGenerationState
    {
        public string TargetId = "none";
        public string AssignmentKind = "none";
        public DateTimeOffset LastAttemptUtc;
        public DateTimeOffset LastVerifiedUtc;
    }

    private readonly struct VerifiedSainGoalHandoffState
    {
        public VerifiedSainGoalHandoffState(
            string targetId,
            string source,
            DateTimeOffset observedAtUtc,
            DateTimeOffset expiresAtUtc)
        {
            TargetId = Normalize(targetId);
            Source = Safe(source);
            ObservedAtUtc = observedAtUtc;
            ExpiresAtUtc = expiresAtUtc;
        }

        public string TargetId { get; }
        public string Source { get; }
        public DateTimeOffset ObservedAtUtc { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
    }

    private readonly struct SquadCombatContactState
    {
        public SquadCombatContactState(
            string ownerProfileId,
            string sourceOperatorId,
            string sourceBotProfileId,
            string targetId,
            string kind,
            string reason,
            float distance,
            DateTimeOffset observedAtUtc,
            DateTimeOffset episodeStartedAtUtc,
            DateTimeOffset expiresAtUtc)
        {
            OwnerProfileId = ownerProfileId;
            SourceOperatorId = sourceOperatorId;
            SourceBotProfileId = sourceBotProfileId;
            TargetId = targetId;
            Kind = kind;
            Reason = reason;
            Distance = distance;
            ObservedAtUtc = observedAtUtc;
            EpisodeStartedAtUtc = episodeStartedAtUtc;
            ExpiresAtUtc = expiresAtUtc;
        }

        public string OwnerProfileId { get; }
        public string SourceOperatorId { get; }
        public string SourceBotProfileId { get; }
        public string TargetId { get; }
        public string Kind { get; }
        public string Reason { get; }
        public float Distance { get; }
        public DateTimeOffset ObservedAtUtc { get; }
        public DateTimeOffset EpisodeStartedAtUtc { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
    }

    private readonly struct PendingTargetClearState
    {
        public PendingTargetClearState(string targetId, string beforeId, string reason, string dropKind, DateTimeOffset nextCheckAtUtc, DateTimeOffset expiresAtUtc, int attempts)
        {
            TargetId = Normalize(targetId);
            BeforeId = Normalize(beforeId);
            Reason = Safe(reason);
            DropKind = Safe(dropKind);
            NextCheckAtUtc = nextCheckAtUtc;
            ExpiresAtUtc = expiresAtUtc;
            Attempts = attempts;
        }

        public string TargetId { get; }
        public string BeforeId { get; }
        public string Reason { get; }
        public string DropKind { get; }
        public DateTimeOffset NextCheckAtUtc { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
        public int Attempts { get; }

        public PendingTargetClearState WithAttempt(DateTimeOffset nextCheckAtUtc)
        {
            return new PendingTargetClearState(TargetId, BeforeId, Reason, DropKind, nextCheckAtUtc, ExpiresAtUtc, Attempts + 1);
        }
    }

    private readonly struct QuarantinedTargetState
    {
        public QuarantinedTargetState(string targetId, string reasonCode, string reasonDetail, DateTimeOffset untilUtc)
        {
            TargetId = Normalize(targetId);
            ReasonCode = Safe(reasonCode);
            ReasonDetail = Safe(reasonDetail);
            UntilUtc = untilUtc;
        }

        public string TargetId { get; }
        public string ReasonCode { get; }
        public string ReasonDetail { get; }
        public DateTimeOffset UntilUtc { get; }
    }
    private readonly struct TargetApplyCircuitState
    {
        public TargetApplyCircuitState(string targetId, string reason, DateTimeOffset untilUtc, int failures, bool qualifiedAssignmentAtOpen)
        {
            TargetId = targetId;
            Reason = reason;
            UntilUtc = untilUtc;
            Failures = failures;
            QualifiedAssignmentAtOpen = qualifiedAssignmentAtOpen;
        }

        public string TargetId { get; }
        public string Reason { get; }
        public DateTimeOffset UntilUtc { get; }
        public int Failures { get; }
        public bool QualifiedAssignmentAtOpen { get; }
    }

    private readonly struct SainTargetApplyResult
    {
        public SainTargetApplyResult(bool applied, bool setEnemyController, bool setMemory, bool calcGoal, string reason)
        {
            Applied = applied;
            SetEnemyController = setEnemyController;
            SetMemory = setMemory;
            CalcGoal = calcGoal;
            Reason = reason;
        }

        public bool Applied { get; }
        public bool SetEnemyController { get; }
        public bool SetMemory { get; }
        public bool CalcGoal { get; }
        public string Reason { get; }
    }
}
#endif
