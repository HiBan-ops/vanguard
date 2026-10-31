#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.Movement.Brain;

// Responsibility: Executes bounded AltDevelop movement from a runtime-qualified route assignment toward its alternate establishment region.
// Flow: Stable route assignment -> persistent maneuver epoch -> current local/scheduler/TC admission -> one fresh NavMesh path proof per physical lease -> exact SAIN search-path translation arbitration -> controlled current-solution retarget between leases -> progress/abort/completion against the current establishment region consumed by split-flank state.
// Authority boundary: Vanguard may request one non-sprinting AltDevelop translation lease per owner during compatible SAIN Search/GroupSearch. SAIN keeps perception/aim/fire/suppression/steering and immediately reclaims translation for a higher-priority individual movement or survival action; the maneuver epoch may survive that local cycle when its structural intent remains current.
// Invariant: No DirectCommit, FullAltEstablished, SAIN mutation, teleport/recovery, in-place path retarget or forced locomotion is introduced. Every resumed/retargeted translation uses a fresh path/window/lease; suspicious vertical discontinuity fails open and yields physical recovery to SAIN/ORBIT/native systems.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class AltDevelopMovementService
{
    public const string StatusTag = "VANGUARD_ALT_DEVELOP_MOVEMENT_STATUS";
    public const string RequestKind = "TacticalCoordinatorAltDevelop";

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(0.20d);
    private static readonly TimeSpan AssignmentStableDuration = TimeSpan.FromSeconds(0.75d);
    private static readonly TimeSpan SiblingFixationStableDuration = TimeSpan.FromSeconds(0.20d);
    private static readonly TimeSpan MinimumLeaseDuration = TimeSpan.FromSeconds(10.0d);
    private static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromSeconds(60.0d);
    private const double PlannedTravelSecondsPerMeter = 0.75d;
    private const double LeaseSettleAllowanceSeconds = 5.0d;
    private static readonly TimeSpan NoProgressDuration = TimeSpan.FromSeconds(3.0d);
    private static readonly TimeSpan UpstreamSnapshotGraceDuration = RouteAssignmentService.PhysicalConsumerContinuityGraceDuration;
    private static readonly TimeSpan ProgressingUpstreamSnapshotGraceDuration = TimeSpan.FromSeconds(5.0d);
    // A suspended tactical decision may outlive its last physical authorization; no stale route is reused.
    private static readonly TimeSpan SuspendedManeuverMemoryDuration = TimeSpan.FromSeconds(12.0d);
    private static readonly TimeSpan AlternateAxisSettleDuration = TimeSpan.FromSeconds(8.0d);
    private static readonly TimeSpan SearchHandoffMaxDuration = TimeSpan.FromSeconds(2.0d);
    private static readonly TimeSpan SearchHandoffDecisionSettleDuration = TimeSpan.FromSeconds(0.75d);
    private static readonly TimeSpan SearchHandoffRetryCooldownDuration = TimeSpan.FromSeconds(2.0d);
    private static readonly TimeSpan NormalRestartCooldown = TimeSpan.FromSeconds(2.0d);
    private static readonly TimeSpan SafetyRestartCooldown = TimeSpan.FromSeconds(6.0d);
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(15.0d);
    private static readonly TimeSpan DevelopmentEvidenceRetention = TimeSpan.FromSeconds(12.0d);
    private const int MinimumStableSamples = 3;
    private const int SiblingFixationStableSamples = 1;
    private const float CommandAnchorRadiusMeters = 0.65f;
    private const float VerticalDiscontinuityMeters = 6.0f;
    private const double VerticalDiscontinuityMaxSampleSeconds = 1.25d;
    private const double VerticalDiscontinuityMinSampleSeconds = 0.10d;
    private const double TranslationCompatibleTickGapCapSeconds = 0.25d;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, AssignmentStabilityState> StabilityByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, RetainedPlanContinuityState> RetainedPlanByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, AltDevelopManeuverEpochState> ManeuverByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, AltDevelopLeaseState> ActiveByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PendingSearchHandoffState> PendingSearchHandoffByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> CooldownByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, AltDevelopEvidenceState> DevelopmentEvidenceByAssignment = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset nextTickAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    private static int startsTotal;
    private static int completesTotal;
    private static int interruptsTotal;
    private static int failuresTotal;
    private static int timeoutsTotal;
    private static int pathRejectsTotal;
    private static int schedulerRejectsTotal;
    private static int commandRejectsTotal;
    private static int executionDeferralsTotal;
    private static int verticalSafetyAbortsTotal;
    private static int assignmentChangeAbortsTotal;
    private static int assignmentOwnerOmissionGraceTicksTotal;
    private static int assignmentOwnerOmissionAbortsTotal;
    private static int localVetoAbortsTotal;
    private static int primaryWindowAbortsTotal;
    private static int spacingSupportConflictAbortsTotal;
    private static int budgetDeferralsTotal;
    private static int upstreamSnapshotCurrentConfirmationsTotal;
    private static int upstreamSnapshotUnavailableGraceTicksTotal;
    private static int upstreamSnapshotUnavailableAbortsTotal;
    private static int upstreamSnapshotProgressGraceTicksTotal;
    private static int sainSearchPathSuppressedTicksTotal;
    private static int sainSearchPathNativeTicksTotal;
    private static int sainSearchPathBindFailuresTotal;
    private static int sainSearchPathFailOpenReleasesTotal;
    private static int sainLivePreflightChecksTotal;
    private static int sainLivePreflightReadyTotal;
    private static int sainLivePreflightDeferralsTotal;
    private const int SainLivePreflightDiagnosticSampleLimit = 64;
    private const int SainLivePreflightReasonBucketLimit = 24;
    private static readonly Dictionary<string, int> SainLivePreflightDeferralsByReason = new(StringComparer.OrdinalIgnoreCase);
    private static int sainLivePreflightDiagnosticSamplesTotal;
    private static int sainLivePreflightReadyDiagnosticSamplesTotal;
    private static double sainTranslationCompatibleSecondsTotal;
    private static double sainTranslationCompatibleLongestLeaseSeconds;
    private static int planContinuityRetainedTotal;
    private static int planContinuityResumedTotal;
    private static int planContinuityInvalidatedTotal;
    private static int stabilitySampleAdvancesTotal;
    private static int stabilityGapRetainedTotal;
    private static int stabilityGapResumedTotal;
    private static int stabilityGapExpiredTotal;
    private static int stabilityIdentityResetTotal;
    private static int stabilityQualifiedTotal;
    private static int maxStableSamplesObserved;
    private static int searchHandoffsStartedTotal;
    private static int searchHandoffsReadyTotal;
    private static int searchHandoffsExpiredTotal;
    private static int searchHandoffsAbortedTotal;
    private static int maneuverEpochsStartedTotal;
    private static int maneuverEpochsCompletedTotal;
    private static int maneuverEpochAbortsTotal;
    private static int maneuverEpochProgressGraceTicksTotal;
    private static int maneuverSolutionRetargetsTotal;
    private static int maneuverLeaseResumesTotal;
    private static int maneuverLeaseTimeoutRolloversTotal;
    private static int maneuverCurrentSolutionConfirmationsTotal;
    private static int establishmentArrivalsTotal;
    private static int establishmentSettleWaitTicksTotal;
    private static int establishmentSettleLossesTotal;
    private static int stableEstablishmentCompletionsTotal;

    public static void Tick(DateTimeOffset now)
    {
        if (now < nextTickAtUtc)
        {
            return;
        }
        nextTickAtUtc = now + TickInterval;
        PruneDevelopmentEvidence(now);
        PruneRetainedPlanContinuity(now);
        PruneStabilityEvidence(now);
        PrunePendingSearchHandoffs(now);
        PruneManeuverEpochs(now);

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_BOOT enabled=true; mode=first_bounded_movement; requestKind={RequestKind}; maxActivePerOwner=1; sprint=false; stableSamples={MinimumStableSamples}; stableSeconds={AssignmentStableDuration.TotalSeconds:0.00}; siblingFixationStableSamples={SiblingFixationStableSamples}; siblingFixationStableSeconds={SiblingFixationStableDuration.TotalSeconds:0.00}; siblingFixationAccelerator=true; siblingFixationRequired=false; minLeaseSeconds={MinimumLeaseDuration.TotalSeconds:0.0}; maxLeaseSeconds={MaximumLeaseDuration.TotalSeconds:0.0}; pathAwareLease=true; plannedTravelSecondsPerMeter={PlannedTravelSecondsPerMeter:0.00}; leaseSettleAllowanceSeconds={LeaseSettleAllowanceSeconds:0.0}; noProgressSeconds={NoProgressDuration.TotalSeconds:0.0}; upstreamSnapshotGraceSeconds={UpstreamSnapshotGraceDuration.TotalSeconds:0.0}; activeLeaseOwnerOmissionGrace=true; searchHandoffMaxSeconds={SearchHandoffMaxDuration.TotalSeconds:0.00}; searchHandoffSettleSeconds={SearchHandoffDecisionSettleDuration.TotalSeconds:0.00}; searchHandoffRetryCooldownSeconds={SearchHandoffRetryCooldownDuration.TotalSeconds:0.00}; commandAnchorRadius={CommandAnchorRadiusMeters:0.00}; verticalDiscontinuityMeters={VerticalDiscontinuityMeters:0.0}; teleportRecoveryAuthority=false; directCommit=false; fullAltEstablished=false; sainMutation=false; searchGroupSearchTranslationCompatible=true; singleTranslationOwner=true; sainSteeringPreserved=true; liveSainPreflight=true; liveSainPreflightMutation=false; translationCompatibilityTelemetry=true; maneuverEpoch=true; suspendedManeuverMemorySeconds={SuspendedManeuverMemoryDuration.TotalSeconds:0.0}; axisSettleSeconds={AlternateAxisSettleDuration.TotalSeconds:0.0}; maneuverRetarget=current_solution_between_fresh_leases; completionUsesCurrentRegion=true; completionRequiresStableEstablishment=true; physicalArrivalReleasesPathBeforeSettle=true; splitFlankObserver=true");
        }

        TickActiveLeases(now);

        if (!RouteAssignmentService.TryGetSnapshot(out var assignmentSnapshot) || !assignmentSnapshot.IsAvailable)
        {
            // A transient observer/publication gap must not erase a plan that was already accumulating
            // distinct stability evidence. No physical execution can start from this branch because a
            // fresh current RouteAssignment snapshot remains mandatory below.
            MarkStabilityUpstreamGap(now, "route_assignment_snapshot_unavailable");
            MarkManeuverUpstreamGap(now, "route_assignment_snapshot_unavailable");
            EmitSummaryIfDue(now);
            return;
        }

        var candidatesByOwner = assignmentSnapshot.ThreatGroups
            .Where(group => group.HasShadowAltSelection
                && group.CandidateMatrixComplete
                && group.SelectedPairPathProven
                && group.SelectedAltPlanningViable
                && !string.IsNullOrWhiteSpace(group.OwnerProfileId))
            .GroupBy(group => group.OwnerProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var planningOwners = new HashSet<string>(candidatesByOwner.Select(group => group.Key), StringComparer.OrdinalIgnoreCase);

        // Missing from one authoritative publication is treated as a bounded upstream gap, not an
        // immediate structural invalidation. A different current identity still resets immediately
        // in ObserveStableAssignmentAndMaybeStart. Retained active-lease continuity is pruned by its
        // own bounded grace and is never invalidated solely because one publication omitted the owner.
        MarkMissingPlanningOwners(planningOwners, now);
        MarkMissingManeuverOwners(planningOwners, now);

        foreach (var ownerGroup in candidatesByOwner)
        {
            string owner = ownerGroup.Key;
            lock (Sync)
            {
                if (ActiveByOwner.ContainsKey(owner))
                {
                    continue;
                }
                if (CooldownByOwner.TryGetValue(owner, out var cooldownUntil) && cooldownUntil > now)
                {
                    continue;
                }
            }

            // A live maneuver epoch owns tactical intent continuity, not execution authority. Prefer its
            // still-current threat/member/side solution over a newly higher-scored unrelated plan so local
            // SAIN cycles cannot silently flip the maneuver. A short-lived opposite-side plan
            // suspends admission; an enduring change is requalified before replacing the epoch.
            ThreatGroupRouteAssignment? selected = SelectAssignmentForOwner(ownerGroup, owner, now);

            if (selected == null)
            {
                ResetStability(owner);
                continue;
            }

            ObserveStableAssignmentAndMaybeStart(assignmentSnapshot, selected, now);
        }

        EmitSummaryIfDue(now);
    }

    internal static bool TryGetDevelopmentEvidence(
        ThreatGroupRouteAssignment assignment,
        DateTimeOffset now,
        out AltDevelopEvidence evidence)
    {
        string key = DevelopmentEvidenceKey(
            assignment.OwnerProfileId,
            assignment.ThreatId,
            assignment.AlternateCandidateId,
            assignment.SelectedAltBotProfileId);
        lock (Sync)
        {
            if (DevelopmentEvidenceByAssignment.TryGetValue(key, out var state)
                && now - state.LastObservedAtUtc <= DevelopmentEvidenceRetention)
            {
                bool progressCurrent = IsDevelopmentProgressCurrentSolution(state, assignment, out string progressProvenanceReason);
                evidence = state.ToSnapshot(progressCurrent, progressProvenanceReason);
                return true;
            }
        }

        evidence = AltDevelopEvidence.Empty;
        return false;
    }

    // Bounded consumers may use this only as proof that the exact selected Alt member/axis currently owns
    // one live physical AltDevelop lease. Historical DevelopmentEvidence retention is intentionally not enough,
    // and callers receive the exact lease identity/expiry so a downstream hold cannot outlive or cross a lease rollover.
    internal static bool TryGetActiveDevelopmentLeaseProof(
        ThreatGroupRouteAssignment assignment,
        DateTimeOffset now,
        out string leaseId,
        out long leaseGeneration,
        out DateTimeOffset leaseMaxUntilUtc,
        out AltDevelopEvidence evidence)
    {
        leaseId = string.Empty;
        leaseGeneration = 0;
        leaseMaxUntilUtc = DateTimeOffset.MinValue;
        evidence = AltDevelopEvidence.Empty;
        if (assignment == null)
        {
            return false;
        }

        lock (Sync)
        {
            if (!ActiveByOwner.TryGetValue(assignment.OwnerProfileId, out AltDevelopLeaseState? active)
                || active == null
                || now >= active.MaxUntilUtc
                || !string.Equals(active.ThreatId, assignment.ThreatId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(active.CandidateId, assignment.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(active.BotProfileId, assignment.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(active.OperatorId, assignment.SelectedAltOperatorId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            leaseId = active.LeaseId;
            leaseGeneration = active.Generation;
            leaseMaxUntilUtc = active.MaxUntilUtc;
        }

        return !string.IsNullOrWhiteSpace(leaseId)
            && leaseGeneration > 0
            && leaseMaxUntilUtc > now
            && TryGetDevelopmentEvidence(assignment, now, out evidence)
            && evidence.IsActive;
    }

    // HeldAccess containment is a logical tactical-intent consumer, not a physical-path-lease consumer.
    // It may therefore bind to the exact current AltDevelop maneuver epoch while the physical lease is
    // active OR while the flanker has arrived and is settling for stable PositionEstablished. The exact
    // current assignment identity and existing upstream-gap grace remain mandatory.
    internal static bool TryGetCurrentDevelopmentManeuverProof(
        ThreatGroupRouteAssignment assignment,
        DateTimeOffset now,
        out string maneuverEpochId,
        out bool physicalArrivalObserved,
        out AltDevelopEvidence evidence)
    {
        maneuverEpochId = string.Empty;
        physicalArrivalObserved = false;
        evidence = AltDevelopEvidence.Empty;
        if (assignment == null)
        {
            return false;
        }

        string currentAssignmentIdentity = AssignmentIdentity(assignment);
        lock (Sync)
        {
            if (!ManeuverByOwner.TryGetValue(assignment.OwnerProfileId, out AltDevelopManeuverEpochState? maneuver)
                || maneuver == null
                || maneuver.UpstreamGapActive
                || now - maneuver.LastConfirmedAtUtc > UpstreamSnapshotGraceDuration
                || !IsManeuverIntentCompatible(maneuver, assignment)
                || !string.Equals(maneuver.CurrentAssignmentIdentity, currentAssignmentIdentity, StringComparison.Ordinal))
            {
                return false;
            }

            maneuverEpochId = maneuver.EpochId;
            physicalArrivalObserved = maneuver.PhysicalArrivalObserved;
        }

        return !string.IsNullOrWhiteSpace(maneuverEpochId)
            && TryGetDevelopmentEvidence(assignment, now, out evidence)
            && evidence.DevelopmentObserved;
    }

    // Active downstream physical consumers may bridge a short RouteAssignment publication gap only when
    // the exact already-started AltDevelop maneuver identity remains recent. This never authorizes a new
    // maneuver or a new FlankSupport start; it is continuity proof for an already-active physical lease.
    internal static bool TryGetRecentExactDevelopmentManeuverContinuityProof(
        string ownerProfileId,
        string threatId,
        string leadBotProfileId,
        string expectedManeuverEpochId,
        DateTimeOffset now,
        out double upstreamGapSeconds,
        out string reason)
    {
        upstreamGapSeconds = double.PositiveInfinity;
        reason = "exact_maneuver_continuity_missing";
        if (string.IsNullOrWhiteSpace(ownerProfileId)
            || string.IsNullOrWhiteSpace(threatId)
            || string.IsNullOrWhiteSpace(leadBotProfileId)
            || string.IsNullOrWhiteSpace(expectedManeuverEpochId))
        {
            reason = "exact_maneuver_continuity_identity_missing";
            return false;
        }

        lock (Sync)
        {
            if (!ManeuverByOwner.TryGetValue(ownerProfileId, out AltDevelopManeuverEpochState? maneuver)
                || maneuver == null
                || !string.Equals(maneuver.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(maneuver.ThreatId, threatId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(maneuver.BotProfileId, leadBotProfileId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(maneuver.EpochId, expectedManeuverEpochId, StringComparison.OrdinalIgnoreCase))
            {
                reason = "exact_maneuver_continuity_identity_changed";
                return false;
            }

            upstreamGapSeconds = Math.Max(0d, (now - maneuver.LastConfirmedAtUtc).TotalSeconds);
            if (upstreamGapSeconds > UpstreamSnapshotGraceDuration.TotalSeconds)
            {
                reason = "exact_maneuver_continuity_expired";
                return false;
            }

            string evidenceKey = DevelopmentEvidenceKey(
                maneuver.OwnerProfileId,
                maneuver.ThreatId,
                maneuver.CurrentCandidateId,
                maneuver.BotProfileId);
            if (!DevelopmentEvidenceByAssignment.TryGetValue(evidenceKey, out AltDevelopEvidenceState? evidence)
                || evidence == null
                || evidence.StartedAtUtc == DateTimeOffset.MinValue
                || string.Equals(evidence.State, "none", StringComparison.OrdinalIgnoreCase)
                || now - evidence.LastObservedAtUtc > DevelopmentEvidenceRetention)
            {
                reason = "exact_maneuver_development_evidence_missing";
                return false;
            }

            reason = maneuver.UpstreamGapActive
                ? "exact_maneuver_upstream_gap_grace"
                : "exact_maneuver_recent_confirmation_grace";
            return true;
        }
    }

    // After stable establishment the maneuver epoch is intentionally ended, but downstream containment
    // may still need exact proof that THIS maneuver reached the current establishment region before the
    // pressure/commit handshake releases the Direct element. This proof is read-only and bounded by the
    // existing development-evidence retention; once a HeldAccess plan has consumed it, current SplitFlank
    // PositionEstablished truth becomes the continuing authority.
    internal static bool TryGetStableDevelopmentCompletionProof(
        ThreatGroupRouteAssignment assignment,
        string expectedManeuverEpochId,
        DateTimeOffset now,
        out AltDevelopEvidence evidence)
    {
        evidence = AltDevelopEvidence.Empty;
        if (assignment == null || string.IsNullOrWhiteSpace(expectedManeuverEpochId))
        {
            return false;
        }

        string key = DevelopmentEvidenceKey(
            assignment.OwnerProfileId,
            assignment.ThreatId,
            assignment.AlternateCandidateId,
            assignment.SelectedAltBotProfileId);
        string currentAssignmentIdentity = AssignmentIdentity(assignment);
        lock (Sync)
        {
            if (!DevelopmentEvidenceByAssignment.TryGetValue(key, out AltDevelopEvidenceState? state)
                || state == null
                || now - state.LastObservedAtUtc > DevelopmentEvidenceRetention
                || !string.Equals(state.State, "Completed", StringComparison.OrdinalIgnoreCase)
                || !state.PhysicalProgressObserved
                || !string.Equals(state.PhysicalProgressKind, "stable_position_established", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(state.ProgressManeuverEpochId, expectedManeuverEpochId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(state.ProgressSolutionIdentity, currentAssignmentIdentity, StringComparison.Ordinal))
            {
                return false;
            }

            // A completed maneuver no longer exists in ManeuverByOwner, so the live-progress
            // geometry gate cannot validate its retained completion evidence. The recorded
            // arrival region must still be compatible with the current tactical region:
            // cap center drift and ensure the physically observed completion point remains
            // inside the new radius even under the worst-case displacement of its center.
            // Do not reject a harmless radius expansion or an inward resize when the
            // previously observed position remains provably inside the new envelope.
            float completedRadius = state.ProgressRegionRadiusMeters;
            float currentRadius = assignment.AlternateEstablishmentRegionRadiusMeters;
            float completedDistance = state.ClosestRegionDistanceMeters;
            float centerDrift = Vector3.Distance(
                state.ProgressRegionCenter,
                assignment.AlternateEstablishmentRegionCenter);
            if (float.IsNaN(completedRadius) || float.IsInfinity(completedRadius)
                || float.IsNaN(currentRadius) || float.IsInfinity(currentRadius)
                || float.IsNaN(completedDistance) || float.IsInfinity(completedDistance)
                || float.IsNaN(centerDrift) || float.IsInfinity(centerDrift)
                || completedRadius < 0f || currentRadius < 0f || completedDistance < 0f)
            {
                return false;
            }

            completedRadius = Math.Max(0.5f, completedRadius);
            currentRadius = Math.Max(0.5f, currentRadius);
            float safeCenterDrift = Math.Max(
                0.5f,
                Math.Min(completedRadius, currentRadius) - CommandAnchorRadiusMeters);
            const float geometryToleranceMeters = 0.05f;
            if (centerDrift > safeCenterDrift
                || completedDistance > completedRadius + geometryToleranceMeters
                || completedDistance + centerDrift > currentRadius + geometryToleranceMeters)
            {
                return false;
            }

            evidence = state.ToSnapshot(progressCurrent: true, progressProvenanceReason: "exact_stable_maneuver_completion");
            return true;
        }
    }

    internal static bool ForgetRuntimePresence(
        string botProfileId,
        DateTimeOffset now,
        string reason,
        out bool activeLeaseRemoved,
        out bool pendingSearchHandoffRemoved,
        out bool maneuverEpochRemoved)
    {
        activeLeaseRemoved = false;
        pendingSearchHandoffRemoved = false;
        maneuverEpochRemoved = false;
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return false;
        }

        AltDevelopLeaseState? activeLease;
        PendingSearchHandoffState? pendingHandoff;
        AltDevelopManeuverEpochState? maneuver;
        int evidenceRemoved = 0;
        lock (Sync)
        {
            activeLease = ActiveByOwner.Values.FirstOrDefault(item =>
                string.Equals(item.BotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase));
            pendingHandoff = PendingSearchHandoffByOwner.Values.FirstOrDefault(item =>
                string.Equals(item.BotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase));
            maneuver = ManeuverByOwner.Values.FirstOrDefault(item =>
                string.Equals(item.BotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase));

            if (activeLease != null
                && ActiveByOwner.TryGetValue(activeLease.OwnerProfileId, out AltDevelopLeaseState currentLease)
                && string.Equals(currentLease.LeaseId, activeLease.LeaseId, StringComparison.OrdinalIgnoreCase))
            {
                ActiveByOwner.Remove(activeLease.OwnerProfileId);
                StabilityByOwner.Remove(activeLease.OwnerProfileId);
                RetainedPlanByOwner.Remove(activeLease.OwnerProfileId);
                activeLeaseRemoved = true;
            }

            if (pendingHandoff != null
                && PendingSearchHandoffByOwner.TryGetValue(pendingHandoff.OwnerProfileId, out PendingSearchHandoffState currentPending)
                && string.Equals(currentPending.WindowId, pendingHandoff.WindowId, StringComparison.OrdinalIgnoreCase))
            {
                PendingSearchHandoffByOwner.Remove(pendingHandoff.OwnerProfileId);
                StabilityByOwner.Remove(pendingHandoff.OwnerProfileId);
                RetainedPlanByOwner.Remove(pendingHandoff.OwnerProfileId);
                pendingSearchHandoffRemoved = true;
            }

            if (maneuver != null
                && ManeuverByOwner.TryGetValue(maneuver.OwnerProfileId, out AltDevelopManeuverEpochState currentManeuver)
                && string.Equals(currentManeuver.EpochId, maneuver.EpochId, StringComparison.OrdinalIgnoreCase))
            {
                ManeuverByOwner.Remove(maneuver.OwnerProfileId);
                StabilityByOwner.Remove(maneuver.OwnerProfileId);
                RetainedPlanByOwner.Remove(maneuver.OwnerProfileId);
                maneuverEpochRemoved = true;
            }

            foreach (string evidenceKey in DevelopmentEvidenceByAssignment
                .Where(pair => string.Equals(pair.Value.BotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToArray())
            {
                if (DevelopmentEvidenceByAssignment.Remove(evidenceKey))
                {
                    evidenceRemoved++;
                }
            }
        }

        bool releasedBoundPath = false;
        string clearResult = "none";
        if (activeLease != null)
        {
            releasedBoundPath = SearchTranslationGateBridge.ReleaseAltDevelopPathBinding(
                activeLease.BotProfileId,
                activeLease.LeaseId,
                activeLease.Generation,
                "tc_alt_develop_presence_lost:" + Safe(reason));
            clearResult = VanguardReturnMovementCommandStore.ClearOwned(
                activeLease.BotProfileId,
                activeLease.LeaseId,
                activeLease.StartedAtUtc,
                "tc_alt_develop_presence_lost:" + Safe(reason));
        }

        bool removed = activeLeaseRemoved || pendingSearchHandoffRemoved || maneuverEpochRemoved || evidenceRemoved > 0;
        if (removed)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_RUNTIME_PRESENCE_FORGOTTEN bot={Safe(botProfileId)}; activeLeaseRemoved={Bool(activeLeaseRemoved)}; pendingSearchHandoffRemoved={Bool(pendingSearchHandoffRemoved)}; maneuverEpochRemoved={Bool(maneuverEpochRemoved)}; evidenceRemoved={evidenceRemoved}; commandClear={Safe(clearResult)}; sainPathReleased={Bool(releasedBoundPath)}; reason={Safe(reason)}; normalFinish=false; ownerCooldown=false; terminalEvidence=false; schedulerHandback=false; staleResume=false; freshReplanAllowed=true");
        }
        return removed;
    }

    internal static bool DrainForHigherAuthority(
        string botProfileId,
        DateTimeOffset now,
        string reason,
        out bool activeLeaseDrained,
        out bool pendingSearchHandoffDrained)
    {
        activeLeaseDrained = false;
        pendingSearchHandoffDrained = false;
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return false;
        }

        AltDevelopLeaseState? activeLease;
        PendingSearchHandoffState? pendingHandoff;
        lock (Sync)
        {
            activeLease = ActiveByOwner.Values.FirstOrDefault(item =>
                string.Equals(item.BotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase));
            pendingHandoff = PendingSearchHandoffByOwner.Values.FirstOrDefault(item =>
                string.Equals(item.BotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase));
        }

        string drainReason = "higher_authority:" + Safe(reason);
        if (activeLease != null)
        {
            // Local higher authority owns translation now, but the squad maneuver intent may remain
            // valid. Retain only the maneuver epoch; exact path/command/window/lease ownership is
            // destroyed synchronously and may never be resumed. Any later TC execution must acquire
            // a fresh path, window, lease and generation.
            FinishLease(
                activeLease,
                now,
                "Interrupted",
                drainReason,
                safetyCooldown: false,
                countTerminal: true,
                retainPlanContinuity: false,
                retainManeuverEpoch: true);
            activeLeaseDrained = true;
        }

        if (pendingHandoff != null)
        {
            AbortPendingSearchHandoff(pendingHandoff, now, drainReason, finishWindow: true);
            pendingSearchHandoffDrained = true;
        }

        bool drained = activeLeaseDrained || pendingSearchHandoffDrained;
        if (drained)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_HIGHER_AUTHORITY_DRAIN bot={Safe(botProfileId)}; activeLeaseDrained={Bool(activeLeaseDrained)}; pendingSearchHandoffDrained={Bool(pendingSearchHandoffDrained)}; reason={Safe(reason)}; maneuverIntentMayPersist=true; oldPathResume=false; oldLeaseResume=false; oldWindowResume=false; freshPhysicalReacquireRequired=true");
        }
        return drained;
    }

    internal static bool CanPreserveCompatibleManeuverAcrossAssignmentRefresh(
        string ownerProfileId,
        string threatId,
        DateTimeOffset now,
        out string summary)
    {
        summary = "compatibleManeuver=false";
        if (string.IsNullOrWhiteSpace(ownerProfileId) || string.IsNullOrWhiteSpace(threatId))
        {
            summary += ";reason=identity_missing";
            return false;
        }
        if (!RouteAssignmentService.TryGetSnapshot(out RouteAssignmentSnapshot assignments) || !assignments.IsAvailable)
        {
            summary += ";reason=route_assignment_not_current";
            return false;
        }

        ThreatGroupRouteAssignment? assignment = assignments.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, threatId, StringComparison.OrdinalIgnoreCase));
        if (assignment == null || !assignment.HasShadowAltSelection || !assignment.SelectedAltPlanningViable)
        {
            summary += ";reason=current_alt_assignment_missing_or_not_viable";
            return false;
        }

        AltDevelopManeuverEpochState? maneuver;
        lock (Sync)
        {
            ManeuverByOwner.TryGetValue(ownerProfileId, out maneuver);
        }
        if (maneuver == null)
        {
            summary += ";reason=maneuver_epoch_missing";
            return false;
        }
        if (!IsManeuverIntentCompatible(maneuver, assignment))
        {
            summary += ";reason=" + DescribeManeuverIntentBreak(maneuver, assignment);
            return false;
        }

        summary = "compatibleManeuver=true"
            + ";epoch=" + Safe(maneuver.EpochId)
            + ";axisFamily=" + Safe(maneuver.AxisFamily)
            + ";oldCandidate=" + Safe(maneuver.CurrentCandidateId)
            + ";newCandidate=" + Safe(assignment.AlternateCandidateId)
            + ";ageSeconds=" + Math.Max(0d, (now - maneuver.LastConfirmedAtUtc).TotalSeconds).ToString("0.00", CultureInfo.InvariantCulture)
            + ";sameOwnerThreatBotOperatorAxis=true";
        return true;
    }

    // An opposite-side publication can be a temporary scoring preference, not a new
    // tactical commitment. Preserve only a parked epoch whose physical authority has
    // already been released; current route viability does not authorize movement here.
    internal static bool CanPreserveDeferredAxisManeuverAcrossAssignmentRefresh(
        string ownerProfileId,
        string threatId,
        DateTimeOffset now,
        out string summary)
    {
        // Report the first failed admission gate without relaxing its protection. A parked
        // epoch needs current assignment and ownership proof, not an old movement lease.
        summary = "deferredAxisManeuver=false";
        if (string.IsNullOrWhiteSpace(ownerProfileId) || string.IsNullOrWhiteSpace(threatId))
        {
            summary += ";reason=identity_missing";
            return false;
        }
        if (!RouteAssignmentService.TryGetSnapshot(out RouteAssignmentSnapshot assignments))
        {
            summary += ";reason=assignment_snapshot_missing";
            return false;
        }
        if (!assignments.IsAvailable)
        {
            summary += ";reason=assignment_unavailable";
            return false;
        }
        if (assignments.PublishedAtUtc == DateTimeOffset.MinValue)
        {
            summary += ";reason=assignment_publication_missing";
            return false;
        }
        if (now < assignments.PublishedAtUtc)
        {
            summary += ";reason=assignment_publication_in_future";
            return false;
        }
        if (now - assignments.PublishedAtUtc > RouteAssignmentService.PhysicalConsumerContinuityGraceDuration)
        {
            summary += ";reason=assignment_publication_expired";
            return false;
        }

        ThreatGroupRouteAssignment? assignment = assignments.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ThreatId, threatId, StringComparison.OrdinalIgnoreCase));
        if (assignment == null)
        {
            summary += ";reason=threat_assignment_missing";
            return false;
        }
        if (!assignment.HasShadowAltSelection)
        {
            summary += ";reason=alt_selection_missing";
            return false;
        }
        if (!assignment.SelectedAltPlanningViable)
        {
            summary += ";reason=alt_planning_not_viable";
            return false;
        }
        if (!assignment.SelectedPairPathProven)
        {
            summary += ";reason=selected_pair_path_unproven";
            return false;
        }

        lock (Sync)
        {
            if (!ManeuverByOwner.TryGetValue(ownerProfileId, out var maneuver))
            {
                summary += ";reason=maneuver_epoch_missing";
                return false;
            }
            if (!IsManeuverSameParticipants(maneuver, assignment))
            {
                summary += ";reason=maneuver_participants_changed";
                return false;
            }
            if (IsManeuverIntentCompatible(maneuver, assignment))
            {
                summary += ";reason=axis_already_compatible";
                return false;
            }
            if (maneuver.AxisFamily.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase)
                || AxisFamily(assignment).StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
            {
                summary += ";reason=semantic_axis_unavailable";
                return false;
            }
            // A gap remains a veto unless the same participants have a demonstrably
            // newer, current publication after it. This preserves only the parked
            // decision epoch; no old physical lease, path or movement authority survives.
            bool recoveredGapPublication = maneuver.UpstreamGapActive
                && maneuver.UpstreamGapStartedAtUtc != DateTimeOffset.MinValue
                && now >= maneuver.UpstreamGapStartedAtUtc
                && now - maneuver.UpstreamGapStartedAtUtc <= SuspendedManeuverMemoryDuration
                && assignments.PublishedAtUtc > maneuver.UpstreamGapStartedAtUtc
                && assignments.PublishedAtUtc > maneuver.LastAssignmentPublishedAtUtc;
            if (maneuver.UpstreamGapActive && !recoveredGapPublication)
            {
                summary += ";reason=maneuver_upstream_gap_active";
                return false;
            }
            if (!maneuver.NeedsFreshStability)
            {
                summary += ";reason=fresh_stability_not_requested";
                return false;
            }
            if (maneuver.AlternateAxisObservedAtUtc == DateTimeOffset.MinValue)
            {
                summary += ";reason=alternate_axis_observation_missing";
                return false;
            }
            if (now < maneuver.AlternateAxisObservedAtUtc)
            {
                summary += ";reason=alternate_axis_observation_in_future";
                return false;
            }
            if (now - maneuver.AlternateAxisObservedAtUtc >= AlternateAxisSettleDuration)
            {
                summary += ";reason=alternate_axis_settle_expired";
                return false;
            }
            if (now < maneuver.LastConfirmedAtUtc)
            {
                summary += ";reason=maneuver_confirmation_in_future";
                return false;
            }
            if (now - maneuver.LastConfirmedAtUtc > SuspendedManeuverMemoryDuration)
            {
                summary += ";reason=maneuver_memory_expired";
                return false;
            }
            if (ActiveByOwner.ContainsKey(ownerProfileId))
            {
                summary += ";reason=physical_lease_still_registered";
                return false;
            }
            if (PendingSearchHandoffByOwner.ContainsKey(ownerProfileId))
            {
                summary += ";reason=search_handoff_pending";
                return false;
            }

            summary = "deferredAxisManeuver=true"
                + ";epoch=" + Safe(maneuver.EpochId)
                + ";oldAxisFamily=" + Safe(maneuver.AxisFamily)
                + ";newAxisFamily=" + Safe(AxisFamily(assignment))
                + ";physicalLeaseActive=false;pendingHandoff=false"
                + ";upstreamGapRecoveredForDormantEpoch=" + Bool(recoveredGapPublication)
                + ";freshPhysicalAdmissionRequired=true";
            return true;
        }
    }

    internal static bool DrainForTacticalReplan(
        string ownerProfileId,
        string threatId,
        DateTimeOffset now,
        string reason,
        out bool activeLeaseDrained,
        out bool pendingSearchHandoffDrained,
        out bool maneuverEpochDrained)
    {
        activeLeaseDrained = false;
        pendingSearchHandoffDrained = false;
        maneuverEpochDrained = false;
        if (string.IsNullOrWhiteSpace(ownerProfileId) || string.IsNullOrWhiteSpace(threatId))
        {
            return false;
        }

        AltDevelopLeaseState? activeLease = null;
        PendingSearchHandoffState? pendingHandoff = null;
        bool matchingManeuver;
        lock (Sync)
        {
            if (ActiveByOwner.TryGetValue(ownerProfileId, out var active)
                && string.Equals(active.ThreatId, threatId, StringComparison.OrdinalIgnoreCase))
            {
                activeLease = active;
            }
            if (PendingSearchHandoffByOwner.TryGetValue(ownerProfileId, out var pending)
                && string.Equals(pending.ThreatId, threatId, StringComparison.OrdinalIgnoreCase))
            {
                pendingHandoff = pending;
            }
            matchingManeuver = ManeuverByOwner.TryGetValue(ownerProfileId, out var maneuver)
                && string.Equals(maneuver.ThreatId, threatId, StringComparison.OrdinalIgnoreCase);
        }

        string drainReason = "tactical_replan:" + Safe(reason);
        if (activeLease != null)
        {
            FinishLease(
                activeLease,
                now,
                "Interrupted",
                drainReason,
                safetyCooldown: false,
                countTerminal: true,
                retainPlanContinuity: false,
                retainManeuverEpoch: false);
            activeLeaseDrained = true;
            maneuverEpochDrained = matchingManeuver;
        }

        if (pendingHandoff != null)
        {
            AbortPendingSearchHandoff(pendingHandoff, now, drainReason, finishWindow: true);
            pendingSearchHandoffDrained = true;
        }

        if (matchingManeuver && !activeLeaseDrained)
        {
            EndManeuverEpoch(ownerProfileId, now, drainReason, completed: false);
            maneuverEpochDrained = true;
        }

        lock (Sync)
        {
            if (StabilityByOwner.TryGetValue(ownerProfileId, out var stability)
                && PlanningIdentityMatchesThreat(stability.Identity, threatId))
            {
                StabilityByOwner.Remove(ownerProfileId);
            }
            if (RetainedPlanByOwner.TryGetValue(ownerProfileId, out var retained)
                && PlanningIdentityMatchesThreat(retained.Identity, threatId))
            {
                RetainedPlanByOwner.Remove(ownerProfileId);
            }
        }

        bool drained = activeLeaseDrained || pendingSearchHandoffDrained || maneuverEpochDrained;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_ALT_DEVELOP_REPLAN_DRAIN owner={Safe(ownerProfileId)}; threat={Safe(threatId)}; activeLeaseDrained={Bool(activeLeaseDrained)}; pendingSearchHandoffDrained={Bool(pendingSearchHandoffDrained)}; maneuverEpochDrained={Bool(maneuverEpochDrained)}; reason={Safe(reason)}; staleResume=false; pathReused=false; leaseReused=false; windowReused=false; freshStabilityRequired=true");
        return drained;
    }

    public static void ResetForRaidLifecycle(string reason)
    {
        TacticalReplanExecutionService.Reset("alt_develop_lifecycle:" + Safe(reason));
        AltDevelopLeaseState[] active;
        PendingSearchHandoffState[] pendingSearchHandoffs;
        lock (Sync)
        {
            active = ActiveByOwner.Values.ToArray();
            pendingSearchHandoffs = PendingSearchHandoffByOwner.Values.ToArray();
            ActiveByOwner.Clear();
            PendingSearchHandoffByOwner.Clear();
            StabilityByOwner.Clear();
            RetainedPlanByOwner.Clear();
            ManeuverByOwner.Clear();
            CooldownByOwner.Clear();
            DevelopmentEvidenceByAssignment.Clear();
            SainLivePreflightDeferralsByReason.Clear();
            nextTickAtUtc = DateTimeOffset.MinValue;
            nextSummaryAtUtc = DateTimeOffset.MinValue;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (var lease in active)
        {
            SearchTranslationGateBridge.ReleaseAltDevelopPathBinding(lease.BotProfileId, lease.LeaseId, lease.Generation, "tc_alt_develop_raid_reset:" + Safe(reason));
            VanguardReturnMovementCommandStore.ClearOwned(lease.BotProfileId, lease.LeaseId, lease.StartedAtUtc, "tc_alt_develop_raid_reset:" + Safe(reason));
            VanguardMainIntentScheduler.FinishPrimaryWindow(lease.BotProfileId, now, "Interrupted", "tc_alt_develop_raid_reset:" + Safe(reason), lease.Summary, lease.WindowId);
        }
        foreach (var pending in pendingSearchHandoffs)
        {
            VanguardMainIntentScheduler.FinishPrimaryWindow(pending.BotProfileId, now, "Interrupted", "tc_alt_develop_search_handoff_raid_reset:" + Safe(reason), pending.Summary, pending.WindowId);
        }

        startsTotal = 0;
        completesTotal = 0;
        interruptsTotal = 0;
        failuresTotal = 0;
        timeoutsTotal = 0;
        pathRejectsTotal = 0;
        schedulerRejectsTotal = 0;
        commandRejectsTotal = 0;
        executionDeferralsTotal = 0;
        verticalSafetyAbortsTotal = 0;
        assignmentChangeAbortsTotal = 0;
        assignmentOwnerOmissionGraceTicksTotal = 0;
        assignmentOwnerOmissionAbortsTotal = 0;
        localVetoAbortsTotal = 0;
        primaryWindowAbortsTotal = 0;
        spacingSupportConflictAbortsTotal = 0;
        budgetDeferralsTotal = 0;
        upstreamSnapshotCurrentConfirmationsTotal = 0;
        upstreamSnapshotUnavailableGraceTicksTotal = 0;
        upstreamSnapshotUnavailableAbortsTotal = 0;
        upstreamSnapshotProgressGraceTicksTotal = 0;
        sainSearchPathSuppressedTicksTotal = 0;
        sainSearchPathNativeTicksTotal = 0;
        sainSearchPathBindFailuresTotal = 0;
        sainSearchPathFailOpenReleasesTotal = 0;
        sainLivePreflightChecksTotal = 0;
        sainLivePreflightReadyTotal = 0;
        sainLivePreflightDeferralsTotal = 0;
        sainLivePreflightDiagnosticSamplesTotal = 0;
        sainLivePreflightReadyDiagnosticSamplesTotal = 0;
        sainTranslationCompatibleSecondsTotal = 0d;
        sainTranslationCompatibleLongestLeaseSeconds = 0d;
        planContinuityRetainedTotal = 0;
        planContinuityResumedTotal = 0;
        planContinuityInvalidatedTotal = 0;
        stabilitySampleAdvancesTotal = 0;
        stabilityGapRetainedTotal = 0;
        stabilityGapResumedTotal = 0;
        stabilityGapExpiredTotal = 0;
        stabilityIdentityResetTotal = 0;
        stabilityQualifiedTotal = 0;
        maxStableSamplesObserved = 0;
        searchHandoffsStartedTotal = 0;
        searchHandoffsReadyTotal = 0;
        searchHandoffsExpiredTotal = 0;
        searchHandoffsAbortedTotal = 0;
        maneuverEpochsStartedTotal = 0;
        maneuverEpochsCompletedTotal = 0;
        maneuverEpochAbortsTotal = 0;
        maneuverEpochProgressGraceTicksTotal = 0;
        maneuverSolutionRetargetsTotal = 0;
        maneuverLeaseResumesTotal = 0;
        maneuverLeaseTimeoutRolloversTotal = 0;
        maneuverCurrentSolutionConfirmationsTotal = 0;
        establishmentArrivalsTotal = 0;
        establishmentSettleWaitTicksTotal = 0;
        establishmentSettleLossesTotal = 0;
        stableEstablishmentCompletionsTotal = 0;
        bootLogged = false;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_ALT_DEVELOP_RESET reason={Safe(reason)}; activeCleared={active.Length}; pendingSearchHandoffsCleared={pendingSearchHandoffs.Length}; movementRecoveryMutation=false; directCommit=false");
    }

    private static void ObserveStableAssignmentAndMaybeStart(
        RouteAssignmentSnapshot assignmentSnapshot,
        ThreatGroupRouteAssignment assignment,
        DateTimeOffset now)
    {
        string owner = assignment.OwnerProfileId;
        string identity = AssignmentIdentity(assignment);
        AssignmentStabilityState stability;
        bool continuityRestored = false;
        string continuityReason = "none";
        bool stabilityGapRestored = false;
        string stabilityGapReason = "none";
        double stabilityGapSeconds = 0d;
        bool stabilityIdentityReset = false;
        string previousStabilityIdentity = "none";
        bool stabilityQualifiedNow = false;
        bool siblingFixationObserved = TryFindSiblingFixingExactThreat(assignment, now, out string fixingBotProfileId);
        int requiredStableSamples = siblingFixationObserved ? SiblingFixationStableSamples : MinimumStableSamples;
        TimeSpan requiredStableDuration = siblingFixationObserved ? SiblingFixationStableDuration : AssignmentStableDuration;
        bool maneuverResume = TryRefreshExistingManeuverEpoch(assignment, assignmentSnapshot, now, out var maneuverEpoch, out _);
        if (maneuverResume && maneuverEpoch != null)
        {
            SeedManeuverResumeProofIfNeeded(maneuverEpoch, assignment, assignmentSnapshot, now);
        }

        lock (Sync)
        {
            if (RetainedPlanByOwner.TryGetValue(owner, out var retained))
            {
                if (!string.Equals(retained.Identity, identity, StringComparison.Ordinal))
                {
                    RetainedPlanByOwner.Remove(owner);
                    planContinuityInvalidatedTotal++;
                }
                else if (now - retained.RetainedAtUtc > UpstreamSnapshotGraceDuration)
                {
                    RetainedPlanByOwner.Remove(owner);
                    planContinuityInvalidatedTotal++;
                }
                else if (retained.SampleCount < MinimumStableSamples
                    || retained.LastEvidencePublishedAtUtc < retained.FirstSeenAtUtc)
                {
                    // Never manufacture stability from a corrupt/incomplete retained proof.
                    RetainedPlanByOwner.Remove(owner);
                    planContinuityInvalidatedTotal++;
                }
                else if (assignmentSnapshot.PublishedAtUtc < retained.LastEvidencePublishedAtUtc)
                {
                    // A retained proof must never be replayed against an older upstream publication.
                    // Wait for the authoritative route-assignment stream to catch up or expire the proof.
                    return;
                }
                else
                {
                    stability = new AssignmentStabilityState(
                        identity,
                        retained.FirstSeenAtUtc,
                        retained.LastEvidencePublishedAtUtc,
                        retained.SampleCount)
                    {
                        QualificationObserved = true
                    };
                    if (assignmentSnapshot.PublishedAtUtc > stability.LastEvidencePublishedAtUtc)
                    {
                        stability.LastEvidencePublishedAtUtc = assignmentSnapshot.PublishedAtUtc;
                        stability.SampleCount++;
                        stabilitySampleAdvancesTotal++;
                    }
                    maxStableSamplesObserved = Math.Max(maxStableSamplesObserved, stability.SampleCount);
                    stability.LastSeenAtUtc = now;
                    StabilityByOwner[owner] = stability;
                    RetainedPlanByOwner.Remove(owner);
                    planContinuityResumedTotal++;
                    continuityReason = retained.Reason;
                    continuityRestored = true;
                }
            }

            if (!continuityRestored)
            {
                if (!StabilityByOwner.TryGetValue(owner, out var existingStability))
                {
                    stability = new AssignmentStabilityState(identity, now, assignmentSnapshot.PublishedAtUtc, 1);
                    stabilitySampleAdvancesTotal++;
                    maxStableSamplesObserved = Math.Max(maxStableSamplesObserved, stability.SampleCount);
                }
                else if (!string.Equals(existingStability.Identity, identity, StringComparison.Ordinal))
                {
                    previousStabilityIdentity = existingStability.Identity;
                    stabilityIdentityReset = true;
                    stabilityIdentityResetTotal++;
                    stability = new AssignmentStabilityState(identity, now, assignmentSnapshot.PublishedAtUtc, 1);
                    stabilitySampleAdvancesTotal++;
                    maxStableSamplesObserved = Math.Max(maxStableSamplesObserved, stability.SampleCount);
                }
                else
                {
                    stability = existingStability;
                    if (stability.UpstreamGapActive)
                    {
                        stabilityGapRestored = true;
                        stabilityGapReason = stability.UpstreamGapReason;
                        stabilityGapSeconds = Math.Max(0d, (now - stability.UpstreamGapStartedAtUtc).TotalSeconds);
                        stability.UpstreamGapActive = false;
                        stability.UpstreamGapStartedAtUtc = DateTimeOffset.MinValue;
                        stability.UpstreamGapReason = "none";
                        stabilityGapResumedTotal++;
                    }
                    if (assignmentSnapshot.PublishedAtUtc > stability.LastEvidencePublishedAtUtc)
                    {
                        stability.LastEvidencePublishedAtUtc = assignmentSnapshot.PublishedAtUtc;
                        stability.SampleCount++;
                        stabilitySampleAdvancesTotal++;
                        maxStableSamplesObserved = Math.Max(maxStableSamplesObserved, stability.SampleCount);
                    }
                }
                stability.LastSeenAtUtc = now;
                StabilityByOwner[owner] = stability;
            }
            else
            {
                stability = StabilityByOwner[owner];
            }

            if (!stability.QualificationObserved
                && stability.SampleCount >= requiredStableSamples
                && now - stability.FirstSeenAtUtc >= requiredStableDuration)
            {
                stability.QualificationObserved = true;
                stabilityQualifiedTotal++;
                stabilityQualifiedNow = true;
            }
        }

        if (stabilityIdentityReset)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_STABILITY_IDENTITY_RESET owner={Safe(owner)}; previousIdentity={Safe(previousStabilityIdentity)}; currentIdentity={Safe(identity)}; stableSamples={stability.SampleCount}; currentPublication={assignmentSnapshot.PublishedAtUtc:O}; reason=current_route_assignment_identity_changed; execution=false");
        }

        if (stabilityGapRestored)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_STABILITY_GAP_RESTORED owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; planningIdentity={Safe(identity)}; gapSeconds={stabilityGapSeconds:0.00}; gapReason={Safe(stabilityGapReason)}; stableSamples={stability.SampleCount}; lastDistinctPublication={stability.LastEvidencePublishedAtUtc:O}; currentSnapshot=true; reusedPath=false; reusedLease=false; reusedWindow=false");
        }

        if (continuityRestored)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_PLAN_CONTINUITY_RESTORED owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; planningIdentity={Safe(identity)}; stableSamples={stability.SampleCount}; lastDistinctPublication={stability.LastEvidencePublishedAtUtc:O}; retainedReason={Safe(continuityReason)}; reusedPath=false; reusedLease=false; reusedWindow=false");
        }

        if (stabilityQualifiedNow)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_STABILITY_QUALIFIED owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; planningIdentity={Safe(identity)}; stableSamples={stability.SampleCount}; requiredSamples={requiredStableSamples}; stableSeconds={Math.Max(0d, (now - stability.FirstSeenAtUtc).TotalSeconds):0.00}; requiredSeconds={requiredStableDuration.TotalSeconds:0.00}; siblingFixation={Bool(siblingFixationObserved)}; fixingBot={Safe(fixingBotProfileId)}; lastDistinctPublication={stability.LastEvidencePublishedAtUtc:O}; currentSnapshot=true; executionAdmissionPending=true; fixationIsAcceleratorOnly=true");
        }

        if (stability.SampleCount < requiredStableSamples || now - stability.FirstSeenAtUtc < requiredStableDuration)
        {
            return;
        }

        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(assignment.SelectedAltBotProfileId, out var startDecision))
        {
            executionDeferralsTotal++;
            VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                $"TC_ALT_DEVELOP_START_DEFERRED owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; reason=decision_snapshot_missing; planRetained=true; stableSamples={stability.SampleCount}");
            return;
        }

        // A retained epoch must reject a concrete live SAIN GoalEnemy identity change
        // before the Search-handoff branch is allowed to mutate scheduler or command ownership.
        // The full live translation preflight remains later because Search handoff may be needed
        // precisely to let SAIN reacquire a compatible Search/GroupSearch translation state.
        if (maneuverResume
            && VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(assignment.SelectedAltBotProfileId, out var resumeRuntime)
            && resumeRuntime.BotOwner != null
            && SearchTranslationGateBridge.HasConcreteAltDevelopLiveGoalEnemyMismatch(
                resumeRuntime.BotOwner,
                assignment.ThreatId,
                out string liveResumeGoalEnemyProfileId))
        {
            AbortPendingSearchHandoffForOwner(owner, now, "live_goal_enemy_changed_before_resume_mutation");
            EndManeuverEpoch(
                owner,
                now,
                "live_resume_goal_enemy_changed_before_scheduler_command_mutation:" + Safe(liveResumeGoalEnemyProfileId),
                completed: false);
            executionDeferralsTotal++;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_MANEUVER_RESUME_GOAL_ENEMY_HARD_BREAK owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; liveGoalEnemy={Safe(liveResumeGoalEnemyProfileId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; schedulerPromotion=false; commandIssue=false; physicalTranslation=false; staleThreatMutationPrevented=true");
            return;
        }

        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(assignment.SelectedAltBotProfileId, out var runtime)
            || runtime.BotOwner == null
            || runtime.BotOwner.IsDead)
        {
            AbortPendingSearchHandoffForOwner(owner, now, "runtime_missing_or_dead_after_search_handoff");
            return;
        }

        Vector3 botPosition = ResolveBotPosition(runtime.BotOwner);
        float regionDistance = Vector3.Distance(botPosition, assignment.AlternateEstablishmentRegionCenter);
        if (regionDistance <= Math.Max(0.5f, assignment.AlternateEstablishmentRegionRadiusMeters))
        {
            AbortPendingSearchHandoffForOwner(owner, now, "translation_not_required_already_inside_alt_establishment_region");
            if (maneuverResume && maneuverEpoch != null)
            {
                if (SplitFlankReadinessService.TryGetCurrentEstablishedObservation(
                        assignment,
                        assignmentSnapshot.PublishedAtUtc,
                        out var establishedObservation,
                        out string establishmentReason))
                {
                    bool boundPhysicalArrival = maneuverEpoch.PhysicalArrivalObserved;
                    if (boundPhysicalArrival)
                    {
                        RecordStableEstablishedCompletion(assignment, maneuverEpoch, establishedObservation, now);
                        completesTotal++;
                        stableEstablishmentCompletionsTotal++;
                    }
                    EndManeuverEpoch(
                        owner,
                        now,
                        boundPhysicalArrival
                            ? "stable_position_established_after_bound_physical_arrival"
                            : "stable_position_established_without_bound_physical_arrival",
                        completed: true);
                    ResetStability(owner);
                    VanguardClientDiagnosticsLog.Info(StatusTag,
                        $"TC_ALT_DEVELOP_ESTABLISHMENT_CONFIRMED owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; regionDistance={regionDistance:0.00}; regionRadius={assignment.AlternateEstablishmentRegionRadiusMeters:0.00}; stableSamples={establishedObservation.EstablishmentStableSamples}; dwellSeconds={establishedObservation.EstablishmentDwellSeconds:0.00}; boundPhysicalArrival={Bool(boundPhysicalArrival)}; physicalTranslationActive=false; completionRequiresStableEstablishment=true");
                    return;
                }

                establishmentSettleWaitTicksTotal++;
                TacticalIntentContinuityService.BeginOrRefresh(
                    maneuverEpoch.BotProfileId,
                    maneuverEpoch.OwnerProfileId,
                    maneuverEpoch.ThreatId,
                    TacticalIntentContinuityService.AltDevelopIntent,
                    maneuverEpoch.EpochId,
                    now,
                    "alt_develop_arrival_waiting_stable_establishment",
                    protectsFromCohesion: true);
                VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                    $"TC_ALT_DEVELOP_ESTABLISHMENT_WAIT owner={Safe(owner)}; epoch={Safe(maneuverEpoch.EpochId)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; regionDistance={regionDistance:0.00}; regionRadius={assignment.AlternateEstablishmentRegionRadiusMeters:0.00}; reason={Safe(establishmentReason)}; boundPhysicalArrival={Bool(maneuverEpoch.PhysicalArrivalObserved)}; physicalTranslationActive=false; lowerPriorityCohesionBlocked=true");
                return;
            }

            ResetStability(owner);
            VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                $"TC_ALT_DEVELOP_START_SKIPPED owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; reason=already_inside_alt_establishment_region_without_active_maneuver; regionDistance={regionDistance:0.00}; regionRadius={assignment.AlternateEstablishmentRegionRadiusMeters:0.00}");
            return;
        }

        if (maneuverResume && maneuverEpoch != null && maneuverEpoch.PhysicalArrivalObserved)
        {
            if (ClearManeuverPhysicalArrival(owner, maneuverEpoch.EpochId))
            {
                establishmentSettleLossesTotal++;
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"TC_ALT_DEVELOP_ESTABLISHMENT_LOST owner={Safe(owner)}; epoch={Safe(maneuverEpoch.EpochId)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; regionDistance={regionDistance:0.00}; regionRadius={assignment.AlternateEstablishmentRegionRadiusMeters:0.00}; reason=left_region_before_stable_establishment; next=fresh_alt_develop_lease");
            }
        }

        if (!MarksmanForwardRoleDisciplinePolicy.CanAuthorizeForwardTranslation(
                startDecision,
                assignment.ThreatId,
                assignment.AlternateEstablishmentAnchor,
                "AltDevelop",
                now,
                out string roleDisciplineReason))
        {
            AbortPendingSearchHandoffForOwner(owner, now, "marksman_role_discipline:" + roleDisciplineReason);
            if (maneuverResume && maneuverEpoch != null)
            {
                EndManeuverEpoch(owner, now, "marksman_role_discipline_structural_break:" + roleDisciplineReason, completed: false);
            }
            ResetStability(owner);
            executionDeferralsTotal++;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_START_DEFERRED owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; reason=marksman_role_discipline:{Safe(roleDisciplineReason)}; schedulerMutation=false; commandMutation=false; physicalTranslation=false; localCombatAuthority=SAIN");
            return;
        }

        string startAdmission = EvaluateCurrentAdmission(assignment, startDecision, now, checkPrimaryWindow: true);
        if (maneuverResume && startAdmission.Length != 0 && !ShouldRetainManeuverEpochForAdmission(startAdmission))
        {
            EndManeuverEpoch(owner, now, "start_admission_structural_break:" + startAdmission, completed: false);
            maneuverResume = false;
            maneuverEpoch = null;
        }
        if (TryGetPendingSearchHandoff(owner, out var pendingSearchHandoff))
        {
            if (!string.Equals(pendingSearchHandoff.AssignmentIdentity, identity, StringComparison.Ordinal)
                || !string.Equals(pendingSearchHandoff.BotProfileId, assignment.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase))
            {
                AbortPendingSearchHandoff(pendingSearchHandoff, now, "assignment_identity_changed", finishWindow: true);
                return;
            }

            bool exactPendingWindow = VanguardMainIntentScheduler.TryGetActiveTacticalCoordinatorAltDevelopWindow(
                    pendingSearchHandoff.BotProfileId,
                    now,
                    out var activePendingWindowId,
                    out var activePendingState,
                    out var activePendingThreat,
                    out var activePendingCandidate)
                && string.Equals(activePendingWindowId, pendingSearchHandoff.WindowId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(activePendingState, "AwaitingSainSearchHandoff", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Normalize(activePendingThreat), Normalize(assignment.ThreatId), StringComparison.Ordinal)
                && string.Equals(Normalize(activePendingCandidate), Normalize(assignment.AlternateCandidateId), StringComparison.Ordinal);
            if (!exactPendingWindow)
            {
                AbortPendingSearchHandoff(pendingSearchHandoff, now, "scheduler_window_preempted_or_expired", finishWindow: false);
                return;
            }

            string pendingSafety = EvaluateSearchHandoffSafety(assignment, startDecision, now);
            if (pendingSafety.Length != 0)
            {
                AbortPendingSearchHandoff(pendingSearchHandoff, now, "search_handoff_safety_changed:" + pendingSafety, finishWindow: true);
                return;
            }

            if (startAdmission.Length == 0)
            {
                if (!pendingSearchHandoff.ReadyObserved)
                {
                    pendingSearchHandoff.ReadyObserved = true;
                    lock (Sync)
                    {
                        PendingSearchHandoffByOwner[owner] = pendingSearchHandoff;
                    }
                    searchHandoffsReadyTotal++;
                    VanguardClientDiagnosticsLog.Info(StatusTag,
                        $"TC_ALT_DEVELOP_SEARCH_HANDOFF_READY owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; window={Safe(pendingSearchHandoff.WindowId)}; waitSeconds={Math.Max(0d, (now - pendingSearchHandoff.StartedAtUtc).TotalSeconds):0.00}; previousRequest={Safe(pendingSearchHandoff.PreviousRequestKind)}; sainSearchOrGroupSearch=true; physicalTranslation=false; next=fresh_path_proof");
                }
            }
            else
            {
                executionDeferralsTotal++;
                if (ShouldAbortPendingSearchHandoffImmediately(startAdmission)
                    || (now - pendingSearchHandoff.StartedAtUtc >= SearchHandoffDecisionSettleDuration
                        && startAdmission.StartsWith("execution_authority_unavailable:", StringComparison.OrdinalIgnoreCase)))
                {
                    AbortPendingSearchHandoff(pendingSearchHandoff, now, "sain_resumed_with_local_authority:" + startAdmission, finishWindow: true);
                    return;
                }
                if (now >= pendingSearchHandoff.MaxUntilUtc)
                {
                    ExpirePendingSearchHandoff(pendingSearchHandoff, now, "search_context_not_observed_before_deadline:" + startAdmission);
                    return;
                }

                VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                    $"TC_ALT_DEVELOP_SEARCH_HANDOFF_WAITING owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; window={Safe(pendingSearchHandoff.WindowId)}; reason={Safe(startAdmission)}; age={Math.Max(0d, (now - pendingSearchHandoff.StartedAtUtc).TotalSeconds):0.00}; physicalTranslation=false; planRetained=true");
                return;
            }
        }
        else if (startAdmission.Length != 0)
        {
            if (TryBeginPendingSearchHandoff(assignment, startDecision, identity, now, startAdmission, out var handoffReason))
            {
                executionDeferralsTotal++;
                return;
            }

            executionDeferralsTotal++;
            VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                $"TC_ALT_DEVELOP_START_DEFERRED owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; reason={Safe(startAdmission)}; searchHandoff={Safe(handoffReason)}; planRetained=true; stableSamples={stability.SampleCount}; planningIdentity={Safe(identity)}");
            return;
        }

        if (!TacticalIntentContinuityService.CanStartIntent(
                assignment.SelectedAltBotProfileId,
                assignment.ThreatId,
                TacticalIntentContinuityService.AltDevelopIntent,
                now,
                out string intentAdmissionReason))
        {
            executionDeferralsTotal++;
            VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                $"TC_ALT_DEVELOP_START_DEFERRED owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; reason={Safe(intentAdmissionReason)}; tacticalIntentConflict=true; mutation=false");
            return;
        }

        if (!VanguardRuntimeFrameBudgetGuard.TryConsumeHeavyWorkForOwner("TCAltDevelopStartPath", owner, 1, 1, out var budgetReason))
        {
            budgetDeferralsTotal++;
            VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                $"TC_ALT_DEVELOP_START_DEFERRED owner={Safe(owner)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; reason={Safe(budgetReason)}; mutation=false");
            return;
        }

        if (!TryCalculateFreshPath(botPosition, assignment.AlternateEstablishmentAnchor, out var preparedPath, out float pathLength, out string pathSummary))
        {
            AbortPendingSearchHandoffForOwner(owner, now, "fresh_path_not_complete");
            pathRejectsTotal++;
            SetCooldown(owner, now, NormalRestartCooldown);
            ResetStability(owner);
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_START_REJECTED owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; reason=fresh_path_not_complete; path={Safe(pathSummary)}; mutation=false");
            return;
        }

        // Re-read the current decision after the potentially expensive path proof so a safety state
        // that changed during the query can still veto before any scheduler/movement mutation.
        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(assignment.SelectedAltBotProfileId, out var currentDecision))
        {
            AbortPendingSearchHandoffForOwner(owner, now, "post_path_decision_snapshot_missing");
            return;
        }
        string finalAdmission = EvaluateCurrentAdmission(assignment, currentDecision, now, checkPrimaryWindow: true);
        if (finalAdmission.Length != 0)
        {
            AbortPendingSearchHandoffForOwner(owner, now, "post_path_admission_changed:" + finalAdmission);
            return;
        }

        // A geometrically complete path may still cross a current, unsupported firing lane.
        // Keep the logical plan, but do not acquire any physical authority for that route.
        if (!MultiThreatOffensiveSafetyPolicy.IsOffensivePreparedRouteSafe(
                currentDecision,
                assignment.ThreatId,
                "AltDevelop",
                preparedPath.corners,
                now,
                out string offensiveRouteReason))
        {
            executionDeferralsTotal++;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_START_DEFERRED owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; reason=offensive_route_safety:{Safe(offensiveRouteReason)}; planRetained=true; leaseCreated=false; sainMutation=false");
            return;
        }
        if (!MarksmanForwardRoleDisciplinePolicy.CanAuthorizeForwardTranslation(
                currentDecision,
                assignment.ThreatId,
                assignment.AlternateEstablishmentAnchor,
                "AltDevelop",
                now,
                out string finalRoleDisciplineReason))
        {
            AbortPendingSearchHandoffForOwner(owner, now, "post_path_marksman_role_discipline:" + finalRoleDisciplineReason);
            if (maneuverResume && maneuverEpoch != null)
            {
                EndManeuverEpoch(owner, now, "post_path_marksman_role_discipline_structural_break:" + finalRoleDisciplineReason, completed: false);
            }
            ResetStability(owner);
            executionDeferralsTotal++;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_START_REJECTED owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; reason=post_path_marksman_role_discipline:{Safe(finalRoleDisciplineReason)}; freshPath=true; schedulerMutation=false; commandMutation=false; physicalTranslation=false; localCombatAuthority=SAIN");
            return;
        }

        if (!RouteAssignmentService.TryResolveMemberEvidenceCapturedAtUtc(
                assignment,
                assignment.SelectedAltBotProfileId,
                out var selectedMemberEvidenceCapturedAtUtc))
        {
            AbortPendingSearchHandoffForOwner(owner, now, "member_evidence_generation_missing_after_path");
            ResetStability(owner);
            VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                $"TC_ALT_DEVELOP_START_REJECTED owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; reason=member_evidence_generation_missing; mutation=false");
            return;
        }

        sainLivePreflightChecksTotal++;
        if (!SearchTranslationGateBridge.TryPreflightAltDevelopLiveTranslation(
                runtime.BotOwner,
                assignment.ThreatId,
                out string livePreflightReason,
                out string livePreflightSummary))
        {
            bool retainManeuverEpoch = !maneuverResume || ShouldRetainManeuverEpochForLiveGate(livePreflightReason);
            if (maneuverResume && !retainManeuverEpoch)
            {
                EndManeuverEpoch(owner, now, "live_preflight_structural_break:" + livePreflightReason, completed: false);
            }
            sainLivePreflightDeferralsTotal++;
            executionDeferralsTotal++;
            RecordSainLivePreflightDeferralDiagnostic(
                owner,
                assignment,
                assignmentSnapshot.PublishedAtUtc,
                now,
                livePreflightReason,
                livePreflightSummary,
                pathSummary);
            VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                $"TC_ALT_DEVELOP_LIVE_SAIN_PREFLIGHT_DEFERRED owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; reason={Safe(livePreflightReason)}; live={Safe(livePreflightSummary)}; mutation=false; schedulerMutation=false; commandMutation=false; planRetained=true; maneuverEpochRetained={Bool(retainManeuverEpoch)}; stableSamples={stability.SampleCount}");
            return;
        }
        sainLivePreflightReadyTotal++;
        if (sainLivePreflightReadyDiagnosticSamplesTotal < 12)
        {
            sainLivePreflightReadyDiagnosticSamplesTotal++;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_LIVE_SAIN_PREFLIGHT_READY_SAMPLE ordinal={sainLivePreflightChecksTotal}; owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; publication={assignmentSnapshot.PublishedAtUtc:O}; live={Safe(livePreflightSummary)}; freshPath={Safe(pathSummary)}; mutation=false; next=scheduler_promotion");
        }
        VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
            $"TC_ALT_DEVELOP_LIVE_SAIN_PREFLIGHT_READY owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; live={Safe(livePreflightSummary)}; mutation=false; next=scheduler_promotion");

        TimeSpan physicalLeaseDuration = ComputeBoundedPhysicalLeaseDuration(pathLength);
        if (!VanguardMainIntentScheduler.TryOpenTacticalCoordinatorAltDevelop(
                currentDecision,
                assignment.ThreatId,
                assignment.AlternateCandidateId,
                now,
                physicalLeaseDuration,
                NoProgressDuration,
                out var windowId,
                out var openReason))
        {
            AbortPendingSearchHandoffForOwner(owner, now, "scheduler_promotion_rejected:" + openReason);
            schedulerRejectsTotal++;
            SetCooldown(owner, now, NormalRestartCooldown);
            VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                $"TC_ALT_DEVELOP_START_DEFERRED owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; reason=scheduler_rejected:{Safe(openReason)}; mutation=false; planRetained=true; stableSamples={stability.SampleCount}");
            return;
        }

        lock (Sync)
        {
            if (PendingSearchHandoffByOwner.TryGetValue(owner, out var promotedPending)
                && string.Equals(promotedPending.WindowId, windowId, StringComparison.OrdinalIgnoreCase))
            {
                PendingSearchHandoffByOwner.Remove(owner);
            }
        }

        string leaseId = "tc_alt_develop_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(assignment.SelectedAltBotProfileId);
        DateTimeOffset maxUntil = now + physicalLeaseDuration;
        bool issued = VanguardReturnMovementCommandStore.Issue(
            leaseId,
            assignment.SelectedAltOperatorId,
            assignment.SelectedAltBotProfileId,
            assignment.AlternateEstablishmentAnchor,
            CommandAnchorRadiusMeters,
            false,
            now,
            maxUntil,
            RequestKind,
            pathSummary,
            pathLength,
            out var commandResult);
        if (!issued
            || !VanguardReturnMovementCommandStore.TryGetActive(assignment.SelectedAltBotProfileId, now, out var command)
            || !string.Equals(command.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(command.RequestKind, RequestKind, StringComparison.OrdinalIgnoreCase))
        {
            commandRejectsTotal++;
            VanguardMainIntentScheduler.FinishPrimaryWindow(assignment.SelectedAltBotProfileId, now, "Failed", "tc_alt_develop_command_rejected:" + Safe(commandResult), pathSummary, windowId);
            SetCooldown(owner, now, NormalRestartCooldown);
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_START_DEFERRED owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; reason=command_rejected:{Safe(commandResult)}; mutation=false; planRetained=true; stableSamples={stability.SampleCount}");
            return;
        }

        string plannedManeuverEpochId = PlanManeuverEpochIdForLease(assignment, now, out bool predictedManeuverResume);
        var lease = new AltDevelopLeaseState(
            owner,
            assignment.ThreatId,
            assignment.AlternateCandidateId,
            assignment.SelectedAltBotProfileId,
            assignment.SelectedAltOperatorId,
            leaseId,
            windowId,
            command.Generation,
            assignment.AlternateEstablishmentAnchor,
            assignment.AlternateEstablishmentRegionCenter,
            assignment.AlternateEstablishmentRegionRadiusMeters,
            pathLength,
            preparedPath,
            selectedMemberEvidenceCapturedAtUtc,
            assignmentSnapshot.PublishedAtUtc,
            now,
            maxUntil,
            now + NoProgressDuration,
            botPosition,
            Vector3.Distance(botPosition, assignment.AlternateEstablishmentRegionCenter),
            identity,
            stability.FirstSeenAtUtc,
            stability.LastEvidencePublishedAtUtc,
            stability.SampleCount,
            startAdmission);
        lease.ManeuverEpochId = plannedManeuverEpochId;
        lease.ManeuverResume = predictedManeuverResume;

        if (!VanguardMainIntentScheduler.MarkTacticalCoordinatorAltDevelopStarted(assignment.SelectedAltBotProfileId, leaseId, now, lease.Summary, windowId))
        {
            commandRejectsTotal++;
            VanguardReturnMovementCommandStore.ClearOwned(assignment.SelectedAltBotProfileId, leaseId, now, "tc_alt_develop_scheduler_start_not_confirmed");
            VanguardMainIntentScheduler.FinishPrimaryWindow(assignment.SelectedAltBotProfileId, now, "Failed", "tc_alt_develop_scheduler_start_not_confirmed", lease.Summary, windowId);
            SetCooldown(owner, now, NormalRestartCooldown);
            VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                $"TC_ALT_DEVELOP_START_DEFERRED owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; reason=scheduler_start_not_confirmed; mutation=false; planRetained=true; stableSamples={stability.SampleCount}");
            return;
        }

        AltDevelopManeuverEpochState maneuverState = EnsureManeuverEpochForStartedLease(assignment, assignmentSnapshot, now, plannedManeuverEpochId, out bool resumedManeuver);
        lease.ManeuverEpochId = maneuverState.EpochId;
        TacticalIntentContinuityService.BeginOrRefresh(
            lease.BotProfileId,
            lease.OwnerProfileId,
            lease.ThreatId,
            TacticalIntentContinuityService.AltDevelopIntent,
            maneuverState.EpochId,
            now,
            resumedManeuver ? "alt_develop_physical_lease_resumed" : "alt_develop_physical_lease_started",
            protectsFromCohesion: true);
        lease.ManeuverResume = resumedManeuver;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_ALT_DEVELOP_MANEUVER_LEASE_STARTED owner={Safe(owner)}; epoch={Safe(maneuverState.EpochId)}; threat={Safe(maneuverState.ThreatId)}; bot={Safe(maneuverState.BotProfileId)}; axisFamily={Safe(maneuverState.AxisFamily)}; candidate={Safe(maneuverState.CurrentCandidateId)}; lease={Safe(lease.LeaseId)}; generation={lease.Generation}; resumed={Bool(resumedManeuver)}; leaseCount={maneuverState.LeaseCount}; retargets={maneuverState.RetargetCount}; freshPath=true; freshWindow=true; freshLease=true");

        lock (Sync)
        {
            ActiveByOwner[owner] = lease;
            StabilityByOwner.Remove(owner);
        }

        RecordDevelopmentEvidence(lease, now, "Active", "movement_started");
        startsTotal++;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_ALT_DEVELOP_STARTED {lease.Summary}; path={Safe(pathSummary)}; live={Safe(livePreflightSummary)}; sourceAssignment=route_assignment; selectedPathAtAssignment={assignment.SelectedPathLengthMeters:0.00}; freshPathLength={pathLength:0.00}; physicalLeaseSeconds={physicalLeaseDuration.TotalSeconds:0.00}; leaseHorizon=path_aware_bounded; sprint=false; physicalBackend=SAIN_BotPathDataManual; directCommit=false; fullAltEstablished=false; localCombatAuthority=SAIN; opportunityCombatTranslation=true; recoveryAuthority=external");
    }

    internal static SearchTranslationDispatchResult TryHandleSearchTranslationGate(
        object pathData,
        object sainBot,
        BotOwner botOwner,
        DateTimeOffset now,
        out string reason)
    {
        reason = "no_alt_develop_lease_for_bot";
        if (pathData == null || sainBot == null || botOwner == null || string.IsNullOrWhiteSpace(botOwner.ProfileId))
        {
            return SearchTranslationDispatchResult.NoLease;
        }

        AltDevelopLeaseState? lease;
        lock (Sync)
        {
            lease = ActiveByOwner.Values.FirstOrDefault(item =>
                string.Equals(item.BotProfileId, botOwner.ProfileId, StringComparison.OrdinalIgnoreCase));
        }
        if (lease == null)
        {
            return SearchTranslationDispatchResult.NoLease;
        }

        try
        {
            if (now >= lease.MaxUntilUtc)
            {
                reason = "lease_expired";
                maneuverLeaseTimeoutRolloversTotal++;
                return FinishLeaseAtSearchGate(lease, now, "Timeout", "max_alt_develop_window_expired_at_search_gate", safetyCooldown: false, retainPlanContinuity: false, retainManeuverEpoch: true);
            }
            if (!VanguardMainIntentScheduler.IsTacticalCoordinatorAltDevelopCurrent(
                    lease.BotProfileId,
                    now,
                    lease.WindowId,
                    lease.LeaseId,
                    out string schedulerReason))
            {
                sainSearchPathFailOpenReleasesTotal++;
                reason = schedulerReason;
                return FinishLeaseAtSearchGate(lease, now, "Interrupted", "search_gate_scheduler_changed:" + schedulerReason, safetyCooldown: false, retainPlanContinuity: false, retainManeuverEpoch: true);
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
                sainSearchPathFailOpenReleasesTotal++;
                reason = commandReason;
                return FinishLeaseAtSearchGate(lease, now, "Interrupted", "search_gate_command_changed:" + commandReason, safetyCooldown: false, retainPlanContinuity: false, retainManeuverEpoch: true);
            }
            if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(lease.BotProfileId, out var decision) || decision == null)
            {
                sainSearchPathFailOpenReleasesTotal++;
                const string staleReason = "decision_snapshot_missing";
                reason = staleReason;
                return FinishLeaseAtSearchGate(lease, now, "Interrupted", "search_gate_" + staleReason, safetyCooldown: false, retainPlanContinuity: false, retainManeuverEpoch: true);
            }
            if (!VanguardOperatorDecisionSnapshotService.IsFresh(decision, now, out string freshnessReason))
            {
                sainSearchPathFailOpenReleasesTotal++;
                reason = freshnessReason;
                return FinishLeaseAtSearchGate(lease, now, "Interrupted", "search_gate_" + freshnessReason, safetyCooldown: false, retainPlanContinuity: false, retainManeuverEpoch: true);
            }
            string admission = EvaluateLeaseAdmission(lease, decision, now);
            if (admission.Length > 0)
            {
                sainSearchPathFailOpenReleasesTotal++;
                reason = admission;
                return FinishLeaseAtSearchGate(lease, now, "Interrupted", "search_gate_local_authority:" + admission, safetyCooldown: false, retainPlanContinuity: false, retainManeuverEpoch: ShouldRetainManeuverEpochForAdmission(admission));
            }
            if (!MarksmanForwardRoleDisciplinePolicy.CanAuthorizeForwardTranslation(
                    decision,
                    lease.ThreatId,
                    lease.EstablishmentAnchor,
                    "AltDevelop",
                    now,
                    out string roleDisciplineReason))
            {
                sainSearchPathFailOpenReleasesTotal++;
                reason = "marksman_role_discipline:" + roleDisciplineReason;
                return FinishLeaseAtSearchGate(lease, now, "Interrupted", "search_gate_marksman_role_discipline:" + roleDisciplineReason, safetyCooldown: false, retainPlanContinuity: false, retainManeuverEpoch: false);
            }
            if (!SearchTranslationGateBridge.TryApplyAltDevelopTranslationGate(
                    pathData,
                    sainBot,
                    botOwner,
                    lease.ThreatId,
                    lease.LeaseId,
                    lease.Generation,
                    lease.EstablishmentAnchor,
                    CommandAnchorRadiusMeters,
                    lease.PreparedPath,
                    out string liveGateReason))
            {
                sainSearchPathBindFailuresTotal++;
                sainSearchPathFailOpenReleasesTotal++;
                reason = liveGateReason;
                return FinishLeaseAtSearchGate(lease, now, "Interrupted", "search_gate_live_sain:" + liveGateReason, safetyCooldown: false, retainPlanContinuity: false, retainManeuverEpoch: ShouldRetainManeuverEpochForLiveGate(liveGateReason));
            }

            ObserveTranslationCompatibleTick(lease, now);
            sainSearchPathNativeTicksTotal++;
            reason = "sain_native_path_translation_tc_destination_owned";
            return SearchTranslationDispatchResult.PassThroughOwned;
        }
        catch (Exception exception)
        {
            sainSearchPathFailOpenReleasesTotal++;
            reason = "exception:" + exception.GetType().Name;
            return FinishLeaseAtSearchGate(lease, now, "Interrupted", "search_gate_exception:" + exception.GetType().Name, safetyCooldown: false, retainPlanContinuity: false, retainManeuverEpoch: false);
        }
    }

    private static void ObserveTranslationCompatibleTick(AltDevelopLeaseState lease, DateTimeOffset now)
    {
        double deltaSeconds = Math.Max(0d, (now - lease.LastTranslationCompatibleAtUtc).TotalSeconds);
        double acceptedSeconds = Math.Min(deltaSeconds, TranslationCompatibleTickGapCapSeconds);
        if (acceptedSeconds > 0d)
        {
            lease.TranslationCompatibleSeconds += acceptedSeconds;
            sainTranslationCompatibleSecondsTotal += acceptedSeconds;
            if (lease.TranslationCompatibleSeconds > sainTranslationCompatibleLongestLeaseSeconds)
            {
                sainTranslationCompatibleLongestLeaseSeconds = lease.TranslationCompatibleSeconds;
            }
            lock (Sync)
            {
                if (ManeuverByOwner.TryGetValue(lease.OwnerProfileId, out var maneuver)
                    && string.Equals(maneuver.EpochId, lease.ManeuverEpochId, StringComparison.OrdinalIgnoreCase))
                {
                    maneuver.TranslationCompatibleSeconds += acceptedSeconds;
                }
            }
        }
        lease.LastTranslationCompatibleAtUtc = now;
        lease.TranslationCompatibleNativeTicks++;
    }

    private static SearchTranslationDispatchResult FinishLeaseAtSearchGate(
        AltDevelopLeaseState lease,
        DateTimeOffset now,
        string outcome,
        string finishReason,
        bool safetyCooldown,
        bool retainPlanContinuity,
        bool retainManeuverEpoch)
    {
        bool releasedBoundPath = FinishLease(lease, now, outcome, finishReason, safetyCooldown, retainPlanContinuity: retainPlanContinuity, retainManeuverEpoch: retainManeuverEpoch);
        if (releasedBoundPath)
        {
            // The current Harmony invocation still points at the path object we just disposed.
            // Skip that one stale tick; SAIN regains normal path ownership on the next mover update.
            sainSearchPathSuppressedTicksTotal++;
            return SearchTranslationDispatchResult.ReleasedCurrentTick;
        }
        return SearchTranslationDispatchResult.HandledFailOpen;
    }

    private static void TickActiveLeases(DateTimeOffset now)
    {
        AltDevelopLeaseState[] active;
        lock (Sync)
        {
            active = ActiveByOwner.Values.ToArray();
        }

        foreach (var lease in active)
        {
            RecordDevelopmentEvidence(lease, now, "Active", "movement_active");
            if (!VanguardFikaCompat.IsRaidAuthority)
            {
                FinishLease(lease, now, "Interrupted", "raid_authority_lost", safetyCooldown: false);
                continue;
            }

            bool assignmentSnapshotAvailable = RouteAssignmentService.TryGetSnapshot(out var assignmentSnapshot)
                && assignmentSnapshot.IsAvailable;
            bool currentAssignmentAvailable = false;
            ThreatGroupRouteAssignment? assignment = null;
            AltDevelopManeuverEpochState? maneuver = null;

            if (assignmentSnapshotAvailable)
            {
                assignment = assignmentSnapshot.ThreatGroups.FirstOrDefault(group =>
                    string.Equals(group.OwnerProfileId, lease.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(group.ThreatId, lease.ThreatId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(group.SelectedAltBotProfileId, lease.BotProfileId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(group.SelectedAltOperatorId, lease.OperatorId, StringComparison.OrdinalIgnoreCase));

                if (assignment == null)
                {
                    // The planner already treats an owner omitted from one change-driven publication as a
                    // bounded upstream gap. An active physical lease must follow the same doctrine instead
                    // of immediately tearing down a still-current maneuver. A genuinely different viable
                    // assignment for this owner remains a material intent change and hard-breaks immediately.
                    bool replacementPlanningIntentExists = assignmentSnapshot.ThreatGroups.Any(group =>
                        string.Equals(group.OwnerProfileId, lease.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                        && group.HasShadowAltSelection
                        && group.CandidateMatrixComplete
                        && group.SelectedPairPathProven
                        && group.SelectedAltPlanningViable);
                    if (replacementPlanningIntentExists)
                    {
                        assignmentChangeAbortsTotal++;
                        FinishLease(lease, now, "Interrupted", "assignment_replaced_by_current_viable_intent", safetyCooldown: false);
                        continue;
                    }

                    TimeSpan ownerOmissionGap = now - lease.LastUpstreamConfirmedAtUtc;
                    bool recentlyProgressingDuringOwnerOmission = lease.LastProgressAtUtc != DateTimeOffset.MinValue
                        && now - lease.LastProgressAtUtc <= NoProgressDuration;
                    TimeSpan effectiveOwnerOmissionGrace = recentlyProgressingDuringOwnerOmission
                        ? ProgressingUpstreamSnapshotGraceDuration
                        : UpstreamSnapshotGraceDuration;
                    if (ownerOmissionGap > effectiveOwnerOmissionGrace)
                    {
                        assignmentOwnerOmissionAbortsTotal++;
                        FinishLease(
                            lease,
                            now,
                            "Interrupted",
                            "assignment_owner_omission_timeout:gap=" + ownerOmissionGap.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
                                + ":grace=" + effectiveOwnerOmissionGrace.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
                                + ":recentProgress=" + Bool(recentlyProgressingDuringOwnerOmission),
                            safetyCooldown: false);
                        continue;
                    }

                    assignmentOwnerOmissionGraceTicksTotal++;
                    if (recentlyProgressingDuringOwnerOmission && ownerOmissionGap > UpstreamSnapshotGraceDuration)
                    {
                        upstreamSnapshotProgressGraceTicksTotal++;
                    }
                    lock (Sync)
                    {
                        if (ManeuverByOwner.TryGetValue(lease.OwnerProfileId, out var retainedManeuver)
                            && string.Equals(retainedManeuver.EpochId, lease.ManeuverEpochId, StringComparison.OrdinalIgnoreCase))
                        {
                            maneuver = retainedManeuver;
                        }
                    }
                    if (maneuver == null)
                    {
                        FinishLease(lease, now, "Interrupted", "maneuver_epoch_missing_during_owner_omission_grace", safetyCooldown: false);
                        continue;
                    }
                }
                else
                {
                    if (!assignment.HasShadowAltSelection || !assignment.SelectedAltPlanningViable)
                    {
                        assignmentChangeAbortsTotal++;
                        FinishLease(lease, now, "Interrupted", "assignment_changed_or_withdrawn", safetyCooldown: false);
                        continue;
                    }

                    currentAssignmentAvailable = true;
                    // A re-ranked opposite flank is not proof that the committed route is safe to keep
                    // following, nor proof that the squad should immediately switch sides. Yield this
                    // lease and let the logical maneuver await a compatible fresh publication.
                    bool oppositeAxisForSameParticipants;
                    lock (Sync)
                    {
                        oppositeAxisForSameParticipants = ManeuverByOwner.TryGetValue(lease.OwnerProfileId, out var established)
                            && string.Equals(established.EpochId, lease.ManeuverEpochId, StringComparison.OrdinalIgnoreCase)
                            && IsManeuverSameParticipants(established, assignment)
                            && !IsManeuverIntentCompatible(established, assignment);
                    }
                    if (oppositeAxisForSameParticipants)
                    {
                        FinishLease(lease, now, "Interrupted", "assignment_axis_change_requires_fresh_decision", safetyCooldown: false, retainManeuverEpoch: true);
                        continue;
                    }
                    if (!TryRefreshExistingManeuverEpoch(assignment, assignmentSnapshot, now, out maneuver, out bool materialRetarget)
                        || maneuver == null
                        || !string.Equals(maneuver.EpochId, lease.ManeuverEpochId, StringComparison.OrdinalIgnoreCase))
                    {
                        assignmentChangeAbortsTotal++;
                        FinishLease(lease, now, "Interrupted", "maneuver_intent_changed_or_missing", safetyCooldown: false);
                        continue;
                    }

                    if (materialRetarget)
                    {
                        FinishLease(
                            lease,
                            now,
                            "Interrupted",
                            "maneuver_solution_retarget_current_assignment",
                            safetyCooldown: false,
                            retainManeuverEpoch: true);
                        continue;
                    }

                    if (assignmentSnapshot.PublishedAtUtc > lease.LastUpstreamPublishedAtUtc)
                    {
                        lease.LastUpstreamPublishedAtUtc = assignmentSnapshot.PublishedAtUtc;
                    }

                    // RouteAssignment is change-driven: an unchanged publication remains current truth while
                    // the snapshot is available and still resolves to the same maneuver intent. The maneuver
                    // solution itself may drift inside its establishment envelope without invalidating the lease.
                    lease.LastUpstreamConfirmedAtUtc = now;
                    upstreamSnapshotCurrentConfirmationsTotal++;
                }
            }
            else
            {
                TimeSpan upstreamGap = now - lease.LastUpstreamConfirmedAtUtc;
                bool recentlyProgressing = lease.LastProgressAtUtc != DateTimeOffset.MinValue
                    && now - lease.LastProgressAtUtc <= NoProgressDuration;
                TimeSpan effectiveUpstreamGrace = recentlyProgressing
                    ? ProgressingUpstreamSnapshotGraceDuration
                    : UpstreamSnapshotGraceDuration;
                if (upstreamGap > effectiveUpstreamGrace)
                {
                    upstreamSnapshotUnavailableAbortsTotal++;
                    FinishLease(
                        lease,
                        now,
                        "Interrupted",
                        "assignment_snapshot_unavailable_timeout:gap=" + upstreamGap.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
                            + ":grace=" + effectiveUpstreamGrace.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
                            + ":recentProgress=" + Bool(recentlyProgressing),
                        safetyCooldown: false);
                    continue;
                }

                upstreamSnapshotUnavailableGraceTicksTotal++;
                if (recentlyProgressing && upstreamGap > UpstreamSnapshotGraceDuration)
                {
                    upstreamSnapshotProgressGraceTicksTotal++;
                }
                lock (Sync)
                {
                    if (ManeuverByOwner.TryGetValue(lease.OwnerProfileId, out var retainedManeuver)
                        && string.Equals(retainedManeuver.EpochId, lease.ManeuverEpochId, StringComparison.OrdinalIgnoreCase))
                    {
                        maneuver = retainedManeuver;
                    }
                }
                if (maneuver == null)
                {
                    FinishLease(lease, now, "Interrupted", "maneuver_epoch_missing_during_upstream_grace", safetyCooldown: false);
                    continue;
                }
            }

            if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(lease.BotProfileId, out var decision))
            {
                FinishLease(lease, now, "Interrupted", "decision_snapshot_missing", safetyCooldown: false, retainManeuverEpoch: true);
                continue;
            }

            string admission = currentAssignmentAvailable && assignment != null
                ? EvaluateCurrentAdmission(assignment, decision, now, checkPrimaryWindow: false)
                : EvaluateLeaseAdmission(lease, decision, now);
            if (admission.Length != 0)
            {
                if (admission.StartsWith("tc_anti_conga_", StringComparison.OrdinalIgnoreCase)
                    || admission.StartsWith("tc_choke_commit_support_", StringComparison.OrdinalIgnoreCase))
                {
                    spacingSupportConflictAbortsTotal++;
                }
                else
                {
                    localVetoAbortsTotal++;
                }
                FinishLease(
                    lease,
                    now,
                    "Interrupted",
                    "local_or_tc_veto:" + admission,
                    safetyCooldown: false,
                    retainManeuverEpoch: ShouldRetainManeuverEpochForAdmission(admission));
                continue;
            }

            if (!MarksmanForwardRoleDisciplinePolicy.CanAuthorizeForwardTranslation(
                    decision,
                    lease.ThreatId,
                    lease.EstablishmentAnchor,
                    "AltDevelop",
                    now,
                    out string roleDisciplineReason))
            {
                localVetoAbortsTotal++;
                FinishLease(
                    lease,
                    now,
                    "Interrupted",
                    "marksman_role_discipline_changed:" + roleDisciplineReason,
                    safetyCooldown: false,
                    retainManeuverEpoch: false);
                continue;
            }

            if (!VanguardMainIntentScheduler.TryGetActivePrimaryWindowIdentity(lease.BotProfileId, now, out var windowKind, out var intentKey, out var state)
                || !string.Equals(windowKind, VanguardPrimaryExecutionWindowKinds.TacticalMovement, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(intentKey, "TacticalCoordinatorAltDevelop", StringComparison.OrdinalIgnoreCase))
            {
                primaryWindowAbortsTotal++;
                FinishLease(lease, now, "Interrupted", "scheduler_window_lost_or_preempted:" + Safe(windowKind) + ":" + Safe(intentKey) + ":" + Safe(state), safetyCooldown: false, retainManeuverEpoch: true);
                continue;
            }

            if (!VanguardReturnMovementCommandStore.TryGetExactOwned(
                    lease.BotProfileId,
                    lease.LeaseId,
                    RequestKind,
                    lease.Generation,
                    now,
                    out _,
                    out var commandOwnershipReason))
            {
                FinishLease(lease, now, "Interrupted", "movement_command_lost_or_preempted:" + Safe(commandOwnershipReason), safetyCooldown: false, retainManeuverEpoch: true);
                continue;
            }

            if (VanguardReturnMovementCommandStore.TryConsumePathInvalid(lease.BotProfileId, lease.LeaseId, lease.Generation, now, out var pathInvalid))
            {
                pathRejectsTotal++;
                FinishLease(lease, now, "Failed", "runtime_path_invalid:" + Safe(pathInvalid), safetyCooldown: false);
                continue;
            }

            if (VanguardReturnMovementCommandStore.TryConsumePhysicalBackendFailure(lease.BotProfileId, lease.LeaseId, lease.Generation, now, out var backendFailure))
            {
                FinishLease(lease, now, "Failed", "physical_backend_failure:" + Safe(backendFailure), safetyCooldown: false);
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(lease.BotProfileId, out var runtime)
                || runtime.BotOwner == null
                || runtime.BotOwner.IsDead)
            {
                FinishLease(lease, now, "Interrupted", "botowner_missing_or_dead", safetyCooldown: false);
                continue;
            }

            Vector3 position = ResolveBotPosition(runtime.BotOwner);
            TimeSpan sampleAge = now - lease.LastWorldSampleAtUtc;
            float verticalDelta = Math.Abs(position.y - lease.LastWorldPosition.y);
            if (sampleAge.TotalSeconds >= VerticalDiscontinuityMinSampleSeconds
                && sampleAge.TotalSeconds <= VerticalDiscontinuityMaxSampleSeconds
                && verticalDelta >= VerticalDiscontinuityMeters)
            {
                verticalSafetyAbortsTotal++;
                FinishLease(lease, now, "Interrupted", "vertical_discontinuity_recovery_guard:dy=" + verticalDelta.ToString("0.00", CultureInfo.InvariantCulture) + ":dt=" + sampleAge.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture), safetyCooldown: true);
                continue;
            }

            Vector3 currentRegionCenter = maneuver?.CurrentRegionCenter ?? lease.EstablishmentRegionCenter;
            float currentRegionRadius = maneuver?.CurrentRegionRadiusMeters ?? lease.EstablishmentRegionRadiusMeters;
            float regionDistance = Vector3.Distance(position, currentRegionCenter);
            if (regionDistance <= Math.Max(0.5f, currentRegionRadius))
            {
                bool boundArrival = MarkManeuverPhysicalArrival(lease, position, currentRegionCenter, currentRegionRadius, now);
                if (boundArrival)
                {
                    establishmentArrivalsTotal++;
                    FinishLease(
                        lease,
                        now,
                        "Arrived",
                        "entered_current_alt_establishment_region_waiting_stable_establishment",
                        safetyCooldown: false,
                        countTerminal: false,
                        retainManeuverEpoch: true,
                        schedulerOutcome: "Completed");
                }
                else
                {
                    completesTotal++;
                    FinishLease(lease, now, "Completed", "entered_current_alt_establishment_region_without_maneuver_epoch", safetyCooldown: false, countTerminal: false);
                }
                continue;
            }

            float previousRegionDistance = Vector3.Distance(lease.LastWorldPosition, currentRegionCenter);
            var physical = VanguardMovementProgressEvaluator.EvaluatePhysical(
                lease.LastWorldPosition,
                position,
                previousRegionDistance,
                regionDistance,
                decision.RealSpeed,
                true,
                sampleAge);

            if (sampleAge >= TimeSpan.FromSeconds(0.45d))
            {
                lease.LastWorldPosition = position;
                lease.LastWorldSampleAtUtc = now;
            }

            if (physical.HasProgress)
            {
                lease.LastRegionDistanceMeters = Math.Min(lease.LastRegionDistanceMeters, regionDistance);
                RecordDevelopmentProgress(
                    lease,
                    now,
                    regionDistance,
                    physical.ProgressKind,
                    maneuver?.CurrentAssignmentIdentity ?? lease.AssignmentIdentity,
                    currentRegionCenter,
                    currentRegionRadius);
                lease.LastProgressAtUtc = now;
                lease.NoProgressUntilUtc = now + NoProgressDuration;
                lease.LastWorldPosition = position;
                lease.LastWorldSampleAtUtc = now;
                lock (Sync)
                {
                    ActiveByOwner[lease.OwnerProfileId] = lease;
                }
                VanguardMainIntentScheduler.ReportPrimaryProgress(lease.BotProfileId, now, "tc_alt_develop_" + physical.ProgressKind, lease.Summary, lease.WindowId);
                continue;
            }

            lock (Sync)
            {
                ActiveByOwner[lease.OwnerProfileId] = lease;
            }

            if (now >= lease.NoProgressUntilUtc)
            {
                timeoutsTotal++;
                FinishLease(lease, now, "Timeout", "no_physical_progress:" + Safe(physical.Summary), safetyCooldown: false, countTerminal: false);
                continue;
            }

            if (now >= lease.MaxUntilUtc)
            {
                timeoutsTotal++;
                maneuverLeaseTimeoutRolloversTotal++;
                FinishLease(lease, now, "Timeout", "max_alt_develop_window_expired", safetyCooldown: false, countTerminal: false, retainManeuverEpoch: true);
            }
        }
    }

    private static TimeSpan ComputeBoundedPhysicalLeaseDuration(float freshPathLengthMeters)
    {
        // The physical lease remains strictly bounded and is still revoked immediately by
        // SAIN/scheduler/command/no-progress gates. Its horizon is only long enough for a genuinely
        // progressing flanker to traverse the fresh path without an arbitrary 10-second rollover.
        double safePathMeters = Math.Max(0d, freshPathLengthMeters);
        double plannedSeconds = LeaseSettleAllowanceSeconds + (safePathMeters * PlannedTravelSecondsPerMeter);
        double boundedSeconds = Math.Max(
            MinimumLeaseDuration.TotalSeconds,
            Math.Min(MaximumLeaseDuration.TotalSeconds, plannedSeconds));
        return TimeSpan.FromSeconds(boundedSeconds);
    }

    private static bool TryBeginPendingSearchHandoff(
        ThreatGroupRouteAssignment assignment,
        OperatorDecisionSnapshot decision,
        string assignmentIdentity,
        DateTimeOffset now,
        string currentAdmission,
        out string reason)
    {
        reason = "not_eligible";
        string safety = EvaluateSearchHandoffSafety(assignment, decision, now);
        if (safety.Length != 0)
        {
            reason = "safety_rejected:" + safety;
            return false;
        }

        if (!VanguardMainIntentScheduler.TryOpenTacticalCoordinatorAltDevelopSearchHandoff(
                decision,
                assignment.ThreatId,
                assignment.AlternateCandidateId,
                now,
                SearchHandoffMaxDuration,
                out var windowId,
                out var schedulerReason))
        {
            reason = "scheduler_rejected:" + schedulerReason;
            return false;
        }

        if (!VanguardReturnMovementCommandStore.TryYieldSupersedableForTacticalCoordinatorSearchHandoff(
                assignment.SelectedAltBotProfileId,
                now,
                out var previousLeaseId,
                out var previousRequestKind,
                out var commandYieldReason))
        {
            VanguardMainIntentScheduler.FinishPrimaryWindow(
                assignment.SelectedAltBotProfileId,
                now,
                "Failed",
                "tc_alt_develop_search_handoff_command_yield_rejected:" + Safe(commandYieldReason),
                currentAdmission,
                windowId);
            reason = "command_yield_rejected:" + commandYieldReason;
            return false;
        }

        var pending = new PendingSearchHandoffState(
            assignment.OwnerProfileId,
            assignmentIdentity,
            assignment.ThreatId,
            assignment.AlternateCandidateId,
            assignment.SelectedAltBotProfileId,
            windowId,
            previousLeaseId,
            previousRequestKind,
            now,
            now + SearchHandoffMaxDuration,
            currentAdmission);
        lock (Sync)
        {
            PendingSearchHandoffByOwner[assignment.OwnerProfileId] = pending;
        }
        searchHandoffsStartedTotal++;
        reason = "started:" + Safe(previousRequestKind);
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_ALT_DEVELOP_SEARCH_HANDOFF_STARTED owner={Safe(assignment.OwnerProfileId)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; window={Safe(windowId)}; previousLease={Safe(previousLeaseId)}; previousRequest={Safe(previousRequestKind)}; initialAdmission={Safe(currentAdmission)}; schedulerReason={Safe(schedulerReason)}; commandYield={Safe(commandYieldReason)}; maxWaitSeconds={SearchHandoffMaxDuration.TotalSeconds:0.00}; physicalTranslation=false; planRetained=true; nextAuthority=SAIN");
        return true;
    }

    private static string EvaluateSearchHandoffSafety(
        ThreatGroupRouteAssignment assignment,
        OperatorDecisionSnapshot decision,
        DateTimeOffset now)
    {
        if (decision == null || !decision.Alive)
        {
            return "operator_dead_or_missing";
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(decision, now, out var freshnessReason))
        {
            return freshnessReason;
        }
        if (!SearchTranslationGateBridge.IsPatchReady(out var patchState))
        {
            return "search_translation_patch_unavailable:" + Safe(patchState);
        }
        if (!RouteAssignmentService.TryResolveMemberEvidenceCapturedAtUtc(assignment, decision.BotProfileId, out var memberEvidenceCapturedAtUtc))
        {
            return "member_evidence_generation_missing";
        }
        string planning = RouteAssignmentService.ValidatePlanningCandidate(decision, assignment.OwnerProfileId, memberEvidenceCapturedAtUtc);
        if (planning.Length != 0)
        {
            return "planning_not_viable:" + planning;
        }
        if (decision.GrenadeHazard.HasRelevantHazard || decision.Medical.Actionability.GrenadeThrowing)
        {
            return "grenade_emergency_or_throw";
        }
        if (decision.Medical.Actionability.AnyMedicineUsing)
        {
            return "medical_controller_active";
        }
        if (decision.Medical.Actionability.Reloading)
        {
            return "reload_active";
        }
        if (decision.Sain == null)
        {
            return "sain_component_missing";
        }
        string localThreat = Normalize(decision.Threat.EnemyId);
        string expectedThreat = Normalize(assignment.ThreatId);
        bool relevantLocalThreat = decision.Threat.DirectThreat || decision.Threat.ResidualThreat || decision.Sain.HasEnemy == true;
        if (relevantLocalThreat && string.Equals(localThreat, "none", StringComparison.Ordinal))
        {
            return "relevant_local_threat_identity_unknown";
        }
        if (relevantLocalThreat && !string.Equals(localThreat, expectedThreat, StringComparison.Ordinal))
        {
            return "competing_relevant_threat_identity_changed";
        }
        if (!decision.SquadCohesion.OwnerKnown || !decision.SquadCohesion.OwnerReliableForActiveMovement)
        {
            return "owner_unreliable_for_active_movement";
        }
        if (decision.MovementAuthority.HardOutsideBubble)
        {
            return "hard_outside_bubble_higher_priority";
        }
        if (AntiCongaService.HasActiveAdvanceGateForBot(decision.BotProfileId))
        {
            return "tc_anti_conga_advance_gate_active";
        }
        if (ChokeCommitSupportService.HasActiveContributionForBot(decision.BotProfileId))
        {
            return "tc_choke_commit_support_contribution_active";
        }
        return string.Empty;
    }

    private static bool TryGetPendingSearchHandoff(string ownerProfileId, out PendingSearchHandoffState pending)
    {
        lock (Sync)
        {
            if (PendingSearchHandoffByOwner.TryGetValue(ownerProfileId, out var found))
            {
                pending = found;
                return true;
            }
        }
        pending = null!;
        return false;
    }

    private static bool ShouldAbortPendingSearchHandoffImmediately(string admission)
    {
        if (string.IsNullOrWhiteSpace(admission))
        {
            return false;
        }
        return admission.IndexOf("operator_dead_or_missing", StringComparison.OrdinalIgnoreCase) >= 0
            || admission.IndexOf("grenade_emergency_or_throw", StringComparison.OrdinalIgnoreCase) >= 0
            || admission.IndexOf("medical_controller_active", StringComparison.OrdinalIgnoreCase) >= 0
            || admission.IndexOf("reload_active", StringComparison.OrdinalIgnoreCase) >= 0
            || admission.IndexOf("competing_relevant_threat_identity_changed", StringComparison.OrdinalIgnoreCase) >= 0
            || admission.IndexOf("relevant_local_threat_identity_unknown", StringComparison.OrdinalIgnoreCase) >= 0
            || admission.IndexOf("hard_outside_bubble_higher_priority", StringComparison.OrdinalIgnoreCase) >= 0
            || admission.IndexOf("tc_anti_conga_advance_gate_active", StringComparison.OrdinalIgnoreCase) >= 0
            || admission.IndexOf("tc_choke_commit_support_contribution_active", StringComparison.OrdinalIgnoreCase) >= 0
            || admission.StartsWith("planning_not_viable:", StringComparison.OrdinalIgnoreCase)
            || admission.StartsWith("member_evidence_generation_missing", StringComparison.OrdinalIgnoreCase)
            || admission.StartsWith("search_translation_patch_unavailable:", StringComparison.OrdinalIgnoreCase);
    }

    private static void AbortPendingSearchHandoffForOwner(string ownerProfileId, DateTimeOffset now, string reason)
    {
        if (TryGetPendingSearchHandoff(ownerProfileId, out var pending))
        {
            AbortPendingSearchHandoff(pending, now, reason, finishWindow: true);
        }
    }

    private static void AbortPendingSearchHandoff(PendingSearchHandoffState pending, DateTimeOffset now, string reason, bool finishWindow)
    {
        lock (Sync)
        {
            if (!PendingSearchHandoffByOwner.TryGetValue(pending.OwnerProfileId, out var current)
                || !string.Equals(current.WindowId, pending.WindowId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            PendingSearchHandoffByOwner.Remove(pending.OwnerProfileId);
        }
        if (finishWindow)
        {
            VanguardMainIntentScheduler.FinishPrimaryWindow(pending.BotProfileId, now, "Interrupted", "tc_alt_develop_search_handoff_aborted:" + Safe(reason), pending.Summary, pending.WindowId);
        }
        SetCooldown(pending.OwnerProfileId, now, SearchHandoffRetryCooldownDuration);
        searchHandoffsAbortedTotal++;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_ALT_DEVELOP_SEARCH_HANDOFF_ABORTED owner={Safe(pending.OwnerProfileId)}; threat={Safe(pending.ThreatId)}; bot={Safe(pending.BotProfileId)}; axis={Safe(pending.CandidateId)}; window={Safe(pending.WindowId)}; age={Math.Max(0d, (now - pending.StartedAtUtc).TotalSeconds):0.00}; reason={Safe(reason)}; retryCooldownSeconds={SearchHandoffRetryCooldownDuration.TotalSeconds:0.00}; physicalTranslation=false; localAuthority=SAIN; authoringTravelMayReacquire=true");
    }

    private static void ExpirePendingSearchHandoff(PendingSearchHandoffState pending, DateTimeOffset now, string reason)
    {
        lock (Sync)
        {
            if (!PendingSearchHandoffByOwner.TryGetValue(pending.OwnerProfileId, out var current)
                || !string.Equals(current.WindowId, pending.WindowId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            PendingSearchHandoffByOwner.Remove(pending.OwnerProfileId);
        }
        VanguardMainIntentScheduler.FinishPrimaryWindow(pending.BotProfileId, now, "Timeout", "tc_alt_develop_search_handoff_expired:" + Safe(reason), pending.Summary, pending.WindowId);
        SetCooldown(pending.OwnerProfileId, now, SearchHandoffRetryCooldownDuration);
        searchHandoffsExpiredTotal++;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_ALT_DEVELOP_SEARCH_HANDOFF_EXPIRED owner={Safe(pending.OwnerProfileId)}; threat={Safe(pending.ThreatId)}; bot={Safe(pending.BotProfileId)}; axis={Safe(pending.CandidateId)}; window={Safe(pending.WindowId)}; age={Math.Max(0d, (now - pending.StartedAtUtc).TotalSeconds):0.00}; reason={Safe(reason)}; retryCooldownSeconds={SearchHandoffRetryCooldownDuration.TotalSeconds:0.00}; physicalTranslation=false; planRetained=true; authoringTravelMayReacquire=true");
    }

    private static void PrunePendingSearchHandoffs(DateTimeOffset now)
    {
        PendingSearchHandoffState[] expired;
        lock (Sync)
        {
            expired = PendingSearchHandoffByOwner.Values.Where(item => now >= item.MaxUntilUtc).ToArray();
        }
        foreach (var pending in expired)
        {
            ExpirePendingSearchHandoff(pending, now, "bounded_wait_deadline");
        }
    }

    // Opportunity-first accelerator only: one sibling already holding the exact same threat
    // lets the selected flanker move from planning to normal execution admission sooner.
    // Absence of fixation never rejects, cancels or downgrades the flank plan.
    private static bool TryFindSiblingFixingExactThreat(
        ThreatGroupRouteAssignment assignment,
        DateTimeOffset now,
        out string fixingBotProfileId)
    {
        fixingBotProfileId = string.Empty;
        if (assignment?.DirectBotProfileIds == null || assignment.DirectBotProfileIds.Count == 0)
        {
            return false;
        }

        foreach (string botProfileId in assignment.DirectBotProfileIds)
        {
            if (string.IsNullOrWhiteSpace(botProfileId)
                || string.Equals(botProfileId, assignment.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase)
                || !VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(botProfileId, out var sibling)
                || sibling == null
                || !sibling.Alive
                || !VanguardOperatorDecisionSnapshotService.IsFresh(sibling, now, out _)
                || !string.Equals(Normalize(sibling.Threat.EnemyId), Normalize(assignment.ThreatId), StringComparison.OrdinalIgnoreCase)
                || (sibling.Threat.EnemyVisible != true && sibling.Threat.EnemyCanShoot != true))
            {
                continue;
            }

            fixingBotProfileId = botProfileId;
            return true;
        }

        return false;
    }

    private static string EvaluateCurrentAdmission(
        ThreatGroupRouteAssignment assignment,
        OperatorDecisionSnapshot decision,
        DateTimeOffset now,
        bool checkPrimaryWindow)
    {
        if (!RouteAssignmentService.TryResolveMemberEvidenceCapturedAtUtc(
                assignment,
                decision.BotProfileId,
                out var memberEvidenceCapturedAtUtc))
        {
            return "member_evidence_generation_missing";
        }

        return EvaluateAdmissionCore(
            decision,
            assignment.OwnerProfileId,
            assignment.ThreatId,
            assignment.SelectedAltBotProfileId,
            memberEvidenceCapturedAtUtc,
            now,
            checkPrimaryWindow);
    }

    private static string EvaluateLeaseAdmission(
        AltDevelopLeaseState lease,
        OperatorDecisionSnapshot decision,
        DateTimeOffset now)
        => EvaluateAdmissionCore(
            decision,
            lease.OwnerProfileId,
            lease.ThreatId,
            lease.BotProfileId,
            lease.SourceMemberEvidenceCapturedAtUtc,
            now,
            checkPrimaryWindow: false);

    private static string EvaluateAdmissionCore(
        OperatorDecisionSnapshot decision,
        string ownerProfileId,
        string threatId,
        string botProfileId,
        DateTimeOffset memberEvidenceCapturedAtUtc,
        DateTimeOffset now,
        bool checkPrimaryWindow)
    {
        if (!SearchTranslationGateBridge.IsPatchReady(out string searchTranslationPatchState))
        {
            return "search_translation_patch_unavailable:" + Safe(searchTranslationPatchState);
        }

        string planning = RouteAssignmentService.ValidatePlanningCandidate(
            decision,
            ownerProfileId,
            memberEvidenceCapturedAtUtc);
        if (planning.Length != 0)
        {
            return "planning_not_viable:" + planning;
        }
        if (!AltDevelopAuthorityPolicy.IsAltDevelopTranslationCompatible(decision, threatId, out string executionReason))
        {
            return "execution_authority_unavailable:" + executionReason;
        }

        if (!decision.SquadCohesion.OwnerKnown || !decision.SquadCohesion.OwnerReliableForActiveMovement)
        {
            return "owner_unreliable_for_active_movement";
        }
        if (decision.MovementAuthority.HardOutsideBubble)
        {
            return "hard_outside_bubble_higher_priority";
        }
        if (AntiCongaService.HasActiveAdvanceGateForBot(botProfileId))
        {
            return "tc_anti_conga_advance_gate_active";
        }
        if (ChokeCommitSupportService.HasActiveContributionForBot(botProfileId))
        {
            return "tc_choke_commit_support_contribution_active";
        }
        if (checkPrimaryWindow && VanguardMainIntentScheduler.HasBlockingPrimaryWindowForTacticalCoordinatorAltDevelop(decision, threatId, now, out var blocking))
        {
            return "primary_window_busy:" + blocking;
        }
        if (checkPrimaryWindow && VanguardReturnMovementCommandStore.TryGetActive(botProfileId, now, out var activeCommand))
        {
            return "movement_command_already_active:" + Safe(activeCommand.RequestKind);
        }
        return string.Empty;
    }

    private static bool TryCalculateFreshPath(Vector3 start, Vector3 anchor, out NavMeshPath preparedPath, out float length, out string summary)
    {
        preparedPath = new NavMeshPath();
        length = 0f;
        if (!NavMesh.SamplePosition(start + Vector3.up * 0.25f, out var startHit, 2.0f, NavMesh.AllAreas))
        {
            summary = "start_navmesh_sample_failed";
            return false;
        }
        if (!NavMesh.SamplePosition(anchor + Vector3.up * 0.25f, out var anchorHit, 1.5f, NavMesh.AllAreas))
        {
            summary = "anchor_navmesh_sample_failed";
            return false;
        }

        bool calculated;
        try
        {
            calculated = NavMesh.CalculatePath(startHit.position, anchorHit.position, NavMesh.AllAreas, preparedPath);
        }
        catch (Exception exception)
        {
            summary = "calculate_exception:" + exception.GetType().Name;
            return false;
        }

        if (!calculated || preparedPath.status != NavMeshPathStatus.PathComplete || preparedPath.corners == null || preparedPath.corners.Length < 2)
        {
            summary = "path_not_complete:calculated=" + Bool(calculated) + ";status=" + preparedPath.status + ";corners=" + (preparedPath.corners?.Length ?? 0).ToString(CultureInfo.InvariantCulture);
            return false;
        }

        for (int index = 1; index < preparedPath.corners.Length; index++)
        {
            length += Vector3.Distance(preparedPath.corners[index - 1], preparedPath.corners[index]);
        }
        summary = "fresh_complete_path;corners=" + preparedPath.corners.Length.ToString(CultureInfo.InvariantCulture) + ";length=" + length.ToString("0.00", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool FinishLease(
        AltDevelopLeaseState lease,
        DateTimeOffset now,
        string outcome,
        string reason,
        bool safetyCooldown,
        bool countTerminal = true,
        bool retainPlanContinuity = false,
        bool retainManeuverEpoch = false,
        string? schedulerOutcome = null)
    {
        bool matchingManeuverExists;
        lock (Sync)
        {
            matchingManeuverExists = ManeuverByOwner.TryGetValue(lease.OwnerProfileId, out var maneuver)
                && string.Equals(maneuver.EpochId, lease.ManeuverEpochId, StringComparison.OrdinalIgnoreCase);
            ActiveByOwner.Remove(lease.OwnerProfileId);
            StabilityByOwner.Remove(lease.OwnerProfileId);
            if (retainManeuverEpoch)
            {
                RetainedPlanByOwner.Remove(lease.OwnerProfileId);
            }
            else if (retainPlanContinuity)
            {
                RetainedPlanByOwner[lease.OwnerProfileId] = new RetainedPlanContinuityState(
                    lease.AssignmentIdentity,
                    lease.PlanStableFirstSeenAtUtc,
                    lease.PlanStableEvidencePublishedAtUtc,
                    lease.PlanStableSampleCount,
                    now,
                    reason);
                planContinuityRetainedTotal++;
            }
            else
            {
                RetainedPlanByOwner.Remove(lease.OwnerProfileId);
            }
        }

        RecordDevelopmentEvidence(lease, now, outcome, reason);
        bool releasedBoundPath = SearchTranslationGateBridge.ReleaseAltDevelopPathBinding(lease.BotProfileId, lease.LeaseId, lease.Generation, "tc_alt_develop_finished:" + reason);
        string clearResult = VanguardReturnMovementCommandStore.ClearOwned(lease.BotProfileId, lease.LeaseId, lease.StartedAtUtc, "tc_alt_develop_finished:" + reason);
        string schedulerTerminalOutcome = string.IsNullOrWhiteSpace(schedulerOutcome) ? outcome : schedulerOutcome;
        VanguardMainIntentScheduler.FinishPrimaryWindow(lease.BotProfileId, now, schedulerTerminalOutcome, reason, lease.Summary + ";clear=" + Safe(clearResult), lease.WindowId);
        if (!retainPlanContinuity && !retainManeuverEpoch)
        {
            SetCooldown(lease.OwnerProfileId, now, safetyCooldown ? SafetyRestartCooldown : NormalRestartCooldown);
        }
        if (!retainManeuverEpoch && matchingManeuverExists)
        {
            EndManeuverEpoch(lease.OwnerProfileId, now, reason, string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase));
        }

        if (countTerminal)
        {
            if (string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase)) completesTotal++;
            else if (string.Equals(outcome, "Timeout", StringComparison.OrdinalIgnoreCase)) timeoutsTotal++;
            else if (string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase)) failuresTotal++;
            else interruptsTotal++;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_ALT_DEVELOP_FINISHED {lease.Summary}; outcome={Safe(outcome)}; reason={Safe(reason)}; commandClear={Safe(clearResult)}; sainPathReleased={Bool(releasedBoundPath)}; safetyCooldown={Bool(safetyCooldown)}; planContinuityRetained={Bool(retainPlanContinuity)}; maneuverEpochRetained={Bool(retainManeuverEpoch)}; schedulerOutcome={Safe(schedulerTerminalOutcome)}; reusedPath=false; reusedLease=false; reusedWindow=false; teleportRecoveryMutation=false; directCommit=false; translationCompatibleSeconds={lease.TranslationCompatibleSeconds:0.000}; translationCompatibleNativeTicks={lease.TranslationCompatibleNativeTicks}; fullAltEstablished=false");
        return releasedBoundPath;
    }

    private static bool MarkManeuverPhysicalArrival(
        AltDevelopLeaseState lease,
        Vector3 arrivalPosition,
        Vector3 currentRegionCenter,
        float currentRegionRadiusMeters,
        DateTimeOffset now)
    {
        lock (Sync)
        {
            if (!ManeuverByOwner.TryGetValue(lease.OwnerProfileId, out var maneuver)
                || !string.Equals(maneuver.EpochId, lease.ManeuverEpochId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            maneuver.PhysicalArrivalObserved = true;
            maneuver.PhysicalArrivalAtUtc = now;
            maneuver.PhysicalArrivalPosition = arrivalPosition;
            maneuver.PhysicalArrivalRegionCenter = currentRegionCenter;
            maneuver.PhysicalArrivalRegionRadiusMeters = Math.Max(0.5f, currentRegionRadiusMeters);
            return true;
        }
    }

    private static bool ClearManeuverPhysicalArrival(string ownerProfileId, string maneuverEpochId)
    {
        lock (Sync)
        {
            if (!ManeuverByOwner.TryGetValue(ownerProfileId, out var maneuver)
                || !string.Equals(maneuver.EpochId, maneuverEpochId, StringComparison.OrdinalIgnoreCase)
                || !maneuver.PhysicalArrivalObserved)
            {
                return false;
            }

            maneuver.PhysicalArrivalObserved = false;
            maneuver.PhysicalArrivalAtUtc = DateTimeOffset.MinValue;
            maneuver.PhysicalArrivalPosition = default;
            maneuver.PhysicalArrivalRegionCenter = default;
            maneuver.PhysicalArrivalRegionRadiusMeters = 0f;
            return true;
        }
    }

    private static void RecordStableEstablishedCompletion(
        ThreatGroupRouteAssignment assignment,
        AltDevelopManeuverEpochState maneuver,
        ThreatGroupReadinessObservation establishedObservation,
        DateTimeOffset now)
    {
        string key = DevelopmentEvidenceKey(
            assignment.OwnerProfileId,
            assignment.ThreatId,
            assignment.AlternateCandidateId,
            assignment.SelectedAltBotProfileId);

        lock (Sync)
        {
            AltDevelopEvidenceState? source = DevelopmentEvidenceByAssignment.Values
                .Where(item => string.Equals(item.OwnerProfileId, assignment.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.ThreatId, assignment.ThreatId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.BotProfileId, assignment.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.ProgressManeuverEpochId, maneuver.EpochId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.LastObservedAtUtc)
                .FirstOrDefault();

            if (!DevelopmentEvidenceByAssignment.TryGetValue(key, out var evidence))
            {
                evidence = new AltDevelopEvidenceState(
                    assignment.OwnerProfileId,
                    assignment.ThreatId,
                    assignment.AlternateCandidateId,
                    assignment.SelectedAltBotProfileId,
                    assignment.SelectedAltOperatorId,
                    source?.Generation ?? 0L,
                    source?.StartedAtUtc ?? maneuver.StartedAtUtc);
            }

            evidence.State = "Completed";
            evidence.Reason = "stable_position_established_after_bound_physical_arrival";
            evidence.LastObservedAtUtc = now;
            evidence.CompletedAtUtc = now;
            evidence.PhysicalProgressObserved = true;
            evidence.PhysicalProgressKind = "stable_position_established";
            evidence.ClosestRegionDistanceMeters = Math.Max(0f, establishedObservation.AlternateDistanceToRegionCenterMeters);
            evidence.ProgressManeuverEpochId = maneuver.EpochId;
            evidence.ProgressSolutionIdentity = AssignmentIdentity(assignment);
            evidence.ProgressRegionCenter = assignment.AlternateEstablishmentRegionCenter;
            evidence.ProgressRegionRadiusMeters = Math.Max(0.5f, assignment.AlternateEstablishmentRegionRadiusMeters);
            DevelopmentEvidenceByAssignment[key] = evidence;
        }
    }

    private static void RecordDevelopmentEvidence(AltDevelopLeaseState lease, DateTimeOffset now, string state, string reason)
    {
        string key = DevelopmentEvidenceKey(lease.OwnerProfileId, lease.ThreatId, lease.CandidateId, lease.BotProfileId);
        lock (Sync)
        {
            if (!DevelopmentEvidenceByAssignment.TryGetValue(key, out var evidence))
            {
                evidence = new AltDevelopEvidenceState(
                    lease.OwnerProfileId,
                    lease.ThreatId,
                    lease.CandidateId,
                    lease.BotProfileId,
                    lease.OperatorId,
                    lease.Generation,
                    lease.StartedAtUtc);
            }

            evidence.State = state;
            evidence.Reason = reason;
            evidence.LastObservedAtUtc = now;
            if (string.Equals(state, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                evidence.CompletedAtUtc = now;
            }
            DevelopmentEvidenceByAssignment[key] = evidence;
        }
    }

    private static void RecordDevelopmentProgress(
        AltDevelopLeaseState lease,
        DateTimeOffset now,
        float regionDistanceMeters,
        string progressKind,
        string currentSolutionIdentity,
        Vector3 currentRegionCenter,
        float currentRegionRadiusMeters)
    {
        string key = DevelopmentEvidenceKey(lease.OwnerProfileId, lease.ThreatId, lease.CandidateId, lease.BotProfileId);
        lock (Sync)
        {
            if (!DevelopmentEvidenceByAssignment.TryGetValue(key, out var evidence)
                || !string.Equals(evidence.ProgressManeuverEpochId, lease.ManeuverEpochId, StringComparison.OrdinalIgnoreCase))
            {
                evidence = new AltDevelopEvidenceState(
                    lease.OwnerProfileId,
                    lease.ThreatId,
                    lease.CandidateId,
                    lease.BotProfileId,
                    lease.OperatorId,
                    lease.Generation,
                    lease.StartedAtUtc);
            }

            bool sameSolutionProvenance = evidence.PhysicalProgressObserved
                && string.Equals(evidence.ProgressManeuverEpochId, lease.ManeuverEpochId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(evidence.ProgressSolutionIdentity, currentSolutionIdentity, StringComparison.Ordinal)
                && Vector3.Distance(evidence.ProgressRegionCenter, currentRegionCenter) <= 0.05f;

            evidence.PhysicalProgressObserved = true;
            evidence.PhysicalProgressKind = string.IsNullOrWhiteSpace(progressKind) ? "physical_progress" : progressKind;
            evidence.ClosestRegionDistanceMeters = sameSolutionProvenance
                ? Math.Min(evidence.ClosestRegionDistanceMeters, regionDistanceMeters)
                : regionDistanceMeters;
            evidence.ProgressManeuverEpochId = lease.ManeuverEpochId;
            evidence.ProgressSolutionIdentity = currentSolutionIdentity;
            evidence.ProgressRegionCenter = currentRegionCenter;
            evidence.ProgressRegionRadiusMeters = Math.Max(0.5f, currentRegionRadiusMeters);
            evidence.LastObservedAtUtc = now;
            DevelopmentEvidenceByAssignment[key] = evidence;
        }
    }

    private static bool IsDevelopmentProgressCurrentSolution(
        AltDevelopEvidenceState evidence,
        ThreatGroupRouteAssignment assignment,
        out string reason)
    {
        if (!evidence.PhysicalProgressObserved)
        {
            reason = "no_physical_progress";
            return false;
        }

        if (!ManeuverByOwner.TryGetValue(assignment.OwnerProfileId, out var maneuver)
            || !string.Equals(maneuver.EpochId, evidence.ProgressManeuverEpochId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "maneuver_epoch_not_current";
            return false;
        }

        string currentSolutionIdentity = AssignmentIdentity(assignment);
        if (!string.Equals(evidence.ProgressSolutionIdentity, currentSolutionIdentity, StringComparison.Ordinal)
            || !string.Equals(maneuver.CurrentAssignmentIdentity, currentSolutionIdentity, StringComparison.Ordinal))
        {
            reason = "tactical_solution_identity_changed";
            return false;
        }

        float currentRadius = Math.Max(0.5f, assignment.AlternateEstablishmentRegionRadiusMeters);
        float proofRadius = Math.Max(0.5f, evidence.ProgressRegionRadiusMeters);
        float safeCenterDrift = Math.Max(0.5f, Math.Min(currentRadius, proofRadius) - CommandAnchorRadiusMeters);
        float centerDrift = Vector3.Distance(evidence.ProgressRegionCenter, assignment.AlternateEstablishmentRegionCenter);
        if (centerDrift > safeCenterDrift)
        {
            reason = "tactical_solution_region_retargeted:drift=" + centerDrift.ToString("0.00", CultureInfo.InvariantCulture)
                + ":safe=" + safeCenterDrift.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        reason = "current_solution_physical_progress";
        return true;
    }

    private static void PruneDevelopmentEvidence(DateTimeOffset now)
    {
        lock (Sync)
        {
            string[] stale = DevelopmentEvidenceByAssignment
                .Where(pair => now - pair.Value.LastObservedAtUtc > DevelopmentEvidenceRetention)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (string key in stale)
            {
                DevelopmentEvidenceByAssignment.Remove(key);
            }
        }
    }

    private static string DevelopmentEvidenceKey(string ownerProfileId, string threatId, string candidateId, string botProfileId)
        => string.Join("|", Normalize(ownerProfileId), Normalize(threatId), Normalize(candidateId), Normalize(botProfileId));

    private static void MarkStabilityUpstreamGap(DateTimeOffset now, string reason)
    {
        var retained = new List<(string Owner, string Identity, int Samples, DateTimeOffset LastPublication)>();
        lock (Sync)
        {
            foreach (var pair in StabilityByOwner)
            {
                AssignmentStabilityState stability = pair.Value;
                if (stability.UpstreamGapActive)
                {
                    continue;
                }

                stability.UpstreamGapActive = true;
                stability.UpstreamGapStartedAtUtc = now;
                stability.UpstreamGapReason = reason;
                stabilityGapRetainedTotal++;
                retained.Add((pair.Key, stability.Identity, stability.SampleCount, stability.LastEvidencePublishedAtUtc));
            }
        }

        foreach (var item in retained)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_STABILITY_GAP_RETAINED owner={Safe(item.Owner)}; planningIdentity={Safe(item.Identity)}; stableSamples={item.Samples}; lastDistinctPublication={item.LastPublication:O}; reason={Safe(reason)}; graceSeconds={UpstreamSnapshotGraceDuration.TotalSeconds:0.0}; currentSnapshot=false; execution=false");
        }
    }

    private static void MarkMissingPlanningOwners(HashSet<string> planningOwners, DateTimeOffset now)
    {
        var retained = new List<(string Owner, string Identity, int Samples, DateTimeOffset LastPublication)>();
        lock (Sync)
        {
            foreach (var pair in StabilityByOwner)
            {
                if (planningOwners.Contains(pair.Key) || pair.Value.UpstreamGapActive)
                {
                    continue;
                }

                pair.Value.UpstreamGapActive = true;
                pair.Value.UpstreamGapStartedAtUtc = now;
                pair.Value.UpstreamGapReason = "owner_missing_from_current_planning_candidates";
                stabilityGapRetainedTotal++;
                retained.Add((pair.Key, pair.Value.Identity, pair.Value.SampleCount, pair.Value.LastEvidencePublishedAtUtc));
            }
        }

        foreach (var item in retained)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_STABILITY_GAP_RETAINED owner={Safe(item.Owner)}; planningIdentity={Safe(item.Identity)}; stableSamples={item.Samples}; lastDistinctPublication={item.LastPublication:O}; reason=owner_missing_from_current_planning_candidates; graceSeconds={UpstreamSnapshotGraceDuration.TotalSeconds:0.0}; currentSnapshot=true; execution=false");
        }
    }

    private static void PruneStabilityEvidence(DateTimeOffset now)
    {
        var expired = new List<(string Owner, string Identity, int Samples, string Reason, double GapSeconds)>();
        lock (Sync)
        {
            string[] staleOwners = StabilityByOwner
                .Where(pair => pair.Value.UpstreamGapActive
                    && now - pair.Value.LastSeenAtUtc > UpstreamSnapshotGraceDuration)
                .Select(pair => pair.Key)
                .ToArray();

            foreach (string owner in staleOwners)
            {
                AssignmentStabilityState stability = StabilityByOwner[owner];
                double gapSeconds = stability.UpstreamGapStartedAtUtc == DateTimeOffset.MinValue
                    ? Math.Max(0d, (now - stability.LastSeenAtUtc).TotalSeconds)
                    : Math.Max(0d, (now - stability.UpstreamGapStartedAtUtc).TotalSeconds);
                expired.Add((owner, stability.Identity, stability.SampleCount, stability.UpstreamGapReason, gapSeconds));
                StabilityByOwner.Remove(owner);
                stabilityGapExpiredTotal++;
            }
        }

        foreach (var item in expired)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_STABILITY_GAP_EXPIRED owner={Safe(item.Owner)}; planningIdentity={Safe(item.Identity)}; stableSamples={item.Samples}; gapSeconds={item.GapSeconds:0.00}; reason={Safe(item.Reason)}; graceSeconds={UpstreamSnapshotGraceDuration.TotalSeconds:0.0}; execution=false");
        }
    }

    private static void PruneRetainedPlanContinuity(DateTimeOffset now)
    {
        lock (Sync)
        {
            string[] staleOwners = RetainedPlanByOwner
                .Where(pair => now - pair.Value.RetainedAtUtc > UpstreamSnapshotGraceDuration)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (string owner in staleOwners)
            {
                RetainedPlanByOwner.Remove(owner);
                planContinuityInvalidatedTotal++;
            }
        }
    }

    // Capture the already-computed preflight outcome only. No additional SAIN reflection,
    // NavMesh query, path binding, scheduler window, or movement authority is introduced.
    private static void RecordSainLivePreflightDeferralDiagnostic(
        string owner,
        ThreatGroupRouteAssignment assignment,
        DateTimeOffset publishedAtUtc,
        DateTimeOffset observedAtUtc,
        string reason,
        string liveSummary,
        string pathSummary)
    {
        string reasonClass = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Split(':')[0].Trim();
        if (reasonClass.Length > 64)
        {
            reasonClass = reasonClass.Substring(0, 64);
        }
        lock (Sync)
        {
            if (!SainLivePreflightDeferralsByReason.ContainsKey(reasonClass)
                && SainLivePreflightDeferralsByReason.Count >= SainLivePreflightReasonBucketLimit)
            {
                reasonClass = "other";
            }
            SainLivePreflightDeferralsByReason.TryGetValue(reasonClass, out int count);
            SainLivePreflightDeferralsByReason[reasonClass] = count + 1;
        }

        if (sainLivePreflightDiagnosticSamplesTotal >= SainLivePreflightDiagnosticSampleLimit)
        {
            return;
        }
        sainLivePreflightDiagnosticSamplesTotal++;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_ALT_DEVELOP_LIVE_SAIN_PREFLIGHT_SAMPLE ordinal={sainLivePreflightChecksTotal}; owner={Safe(owner)}; threat={Safe(assignment.ThreatId)}; bot={Safe(assignment.SelectedAltBotProfileId)}; axis={Safe(assignment.AlternateCandidateId)}; publication={publishedAtUtc:O}; observed={observedAtUtc:O}; reason={Safe(reason)}; reasonClass={Safe(reasonClass)}; live={Safe(liveSummary)}; freshPath={Safe(pathSummary)}; stage=after_fresh_path_admission; mutation=false; schedulerMutation=false; commandMutation=false; readOnly=true");
    }

    private static void EmitSummaryIfDue(DateTimeOffset now)
    {
        if (now < nextSummaryAtUtc)
        {
            return;
        }
        nextSummaryAtUtc = now + SummaryInterval;
        int active;
        int stability;
        int stabilityGapActive;
        int stabilityQualifiedCurrent;
        int retainedContinuity;
        int pendingSearchHandoffs;
        int activeManeuvers;
        lock (Sync)
        {
            active = ActiveByOwner.Count;
            stability = StabilityByOwner.Count;
            stabilityGapActive = StabilityByOwner.Values.Count(item => item.UpstreamGapActive);
            stabilityQualifiedCurrent = StabilityByOwner.Values.Count(item => item.QualificationObserved);
            retainedContinuity = RetainedPlanByOwner.Count;
            pendingSearchHandoffs = PendingSearchHandoffByOwner.Count;
            activeManeuvers = ManeuverByOwner.Count;
        }
        string livePreflightReasonCounts;
        lock (Sync)
        {
            livePreflightReasonCounts = string.Join(",", SainLivePreflightDeferralsByReason
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => Safe(pair.Key) + ":" + pair.Value.ToString(CultureInfo.InvariantCulture)));
        }
        VanguardClientDiagnosticsLog.DiagnosticSummary(StatusTag, "TC_ALT_DEVELOP_SUMMARY", () =>
            $"TC_ALT_DEVELOP_SUMMARY active={active}; pendingSearchHandoffs={pendingSearchHandoffs}; stabilityCandidates={stability}; stabilityGapActive={stabilityGapActive}; stabilityQualifiedCurrent={stabilityQualifiedCurrent}; retainedPlanContinuity={retainedContinuity}; startsTotal={startsTotal}; completesTotal={completesTotal}; interruptsTotal={interruptsTotal}; failuresTotal={failuresTotal}; timeoutsTotal={timeoutsTotal}; pathRejectsTotal={pathRejectsTotal}; schedulerRejectsTotal={schedulerRejectsTotal}; commandRejectsTotal={commandRejectsTotal}; executionDeferralsTotal={executionDeferralsTotal}; verticalSafetyAbortsTotal={verticalSafetyAbortsTotal}; assignmentChangeAbortsTotal={assignmentChangeAbortsTotal}; assignmentOwnerOmissionGraceTicksTotal={assignmentOwnerOmissionGraceTicksTotal}; assignmentOwnerOmissionAbortsTotal={assignmentOwnerOmissionAbortsTotal}; localVetoAbortsTotal={localVetoAbortsTotal}; primaryWindowAbortsTotal={primaryWindowAbortsTotal}; spacingSupportConflictAbortsTotal={spacingSupportConflictAbortsTotal}; budgetDeferralsTotal={budgetDeferralsTotal}; upstreamSnapshotCurrentConfirmationsTotal={upstreamSnapshotCurrentConfirmationsTotal}; upstreamSnapshotUnavailableGraceTicksTotal={upstreamSnapshotUnavailableGraceTicksTotal}; upstreamSnapshotUnavailableAbortsTotal={upstreamSnapshotUnavailableAbortsTotal}; upstreamSnapshotProgressGraceTicksTotal={upstreamSnapshotProgressGraceTicksTotal}; sainSearchPathSuppressedTicksTotal={sainSearchPathSuppressedTicksTotal}; sainSearchPathNativeTicksTotal={sainSearchPathNativeTicksTotal}; sainSearchPathBindFailuresTotal={sainSearchPathBindFailuresTotal}; sainSearchPathFailOpenReleasesTotal={sainSearchPathFailOpenReleasesTotal}; sainLivePreflightChecksTotal={sainLivePreflightChecksTotal}; sainLivePreflightReadyTotal={sainLivePreflightReadyTotal}; sainLivePreflightDeferralsTotal={sainLivePreflightDeferralsTotal}; sainLivePreflightDiagnosticSamplesTotal={sainLivePreflightDiagnosticSamplesTotal}; sainLivePreflightReadyDiagnosticSamplesTotal={sainLivePreflightReadyDiagnosticSamplesTotal}; sainLivePreflightReasonCounts={Safe(livePreflightReasonCounts)}; sainTranslationCompatibleSecondsTotal={sainTranslationCompatibleSecondsTotal:0.000}; sainTranslationCompatibleLongestLeaseSeconds={sainTranslationCompatibleLongestLeaseSeconds:0.000}; planContinuityRetainedTotal={planContinuityRetainedTotal}; planContinuityResumedTotal={planContinuityResumedTotal}; planContinuityInvalidatedTotal={planContinuityInvalidatedTotal}; stabilitySampleAdvancesTotal={stabilitySampleAdvancesTotal}; stabilityGapRetainedTotal={stabilityGapRetainedTotal}; stabilityGapResumedTotal={stabilityGapResumedTotal}; stabilityGapExpiredTotal={stabilityGapExpiredTotal}; stabilityIdentityResetTotal={stabilityIdentityResetTotal}; stabilityQualifiedTotal={stabilityQualifiedTotal}; maxStableSamplesObserved={maxStableSamplesObserved}; searchHandoffsStartedTotal={searchHandoffsStartedTotal}; searchHandoffsReadyTotal={searchHandoffsReadyTotal}; searchHandoffsExpiredTotal={searchHandoffsExpiredTotal}; searchHandoffsAbortedTotal={searchHandoffsAbortedTotal}; activeManeuvers={activeManeuvers}; maneuverEpochsStartedTotal={maneuverEpochsStartedTotal}; maneuverEpochsCompletedTotal={maneuverEpochsCompletedTotal}; maneuverEpochAbortsTotal={maneuverEpochAbortsTotal}; maneuverEpochProgressGraceTicksTotal={maneuverEpochProgressGraceTicksTotal}; maneuverSolutionRetargetsTotal={maneuverSolutionRetargetsTotal}; maneuverLeaseResumesTotal={maneuverLeaseResumesTotal}; maneuverLeaseTimeoutRolloversTotal={maneuverLeaseTimeoutRolloversTotal}; maneuverCurrentSolutionConfirmationsTotal={maneuverCurrentSolutionConfirmationsTotal}; establishmentArrivalsTotal={establishmentArrivalsTotal}; establishmentSettleWaitTicksTotal={establishmentSettleWaitTicksTotal}; establishmentSettleLossesTotal={establishmentSettleLossesTotal}; stableEstablishmentCompletionsTotal={stableEstablishmentCompletionsTotal}; movementAuthority=bounded_alt_develop_only; sprint=false; retarget=controlled_current_solution_between_fresh_leases; restart=false; recoveryMutation=false; directCommit=false; fullAltEstablished=false");
    }

    private static ThreatGroupRouteAssignment? SelectAssignmentForOwner(
        IEnumerable<ThreatGroupRouteAssignment> ownerGroup,
        string ownerProfileId,
        DateTimeOffset now)
    {
        AltDevelopManeuverEpochState? maneuver;
        lock (Sync)
        {
            ManeuverByOwner.TryGetValue(ownerProfileId, out maneuver);
        }

        if (maneuver != null)
        {
            ThreatGroupRouteAssignment? continued = ownerGroup
                .Where(item => IsManeuverIntentCompatible(maneuver, item))
                // Fresh geometry may change the candidate within the same flank family.
                .OrderByDescending(item => item.AlternateAxisEvidenceScore)
                .ThenBy(item => item.SelectedPathLengthMeters)
                .ThenBy(item => item.AlternateCandidateId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (continued != null)
            {
                lock (Sync)
                {
                    if (ManeuverByOwner.TryGetValue(ownerProfileId, out var current)
                        && string.Equals(current.EpochId, maneuver.EpochId, StringComparison.OrdinalIgnoreCase))
                    {
                        current.AlternateAxisObservedAtUtc = DateTimeOffset.MinValue;
                    }
                }
                return continued;
            }

            // A short-lived opposite-side preference should not erase an already chosen flank.
            // Do not launch a path while the only published alternative is incompatible.
            if (ownerGroup.Any(item => IsManeuverSameParticipants(maneuver, item)))
            {
                bool firstObservation = false;
                bool deferReplacement = false;
                double observedSeconds = 0d;
                lock (Sync)
                {
                    if (ManeuverByOwner.TryGetValue(ownerProfileId, out var current)
                        && string.Equals(current.EpochId, maneuver.EpochId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (current.AlternateAxisObservedAtUtc == DateTimeOffset.MinValue)
                        {
                            current.AlternateAxisObservedAtUtc = now;
                            current.NeedsFreshStability = true;
                            firstObservation = true;
                        }
                        observedSeconds = Math.Max(0d, (now - current.AlternateAxisObservedAtUtc).TotalSeconds);
                        deferReplacement = now - current.AlternateAxisObservedAtUtc < AlternateAxisSettleDuration;
                    }
                }
                if (firstObservation)
                {
                    // Release any previous cohesion protection; a parked epoch is not an active move.
                    TacticalIntentContinuityService.EndIntent(maneuver.BotProfileId, TacticalIntentContinuityService.AltDevelopIntent, now, "alternate_axis_waiting_for_fresh_compatible_plan");
                    VanguardClientDiagnosticsLog.Info(StatusTag,
                        $"TC_ALT_DEVELOP_AXIS_CHANGE_DEFERRED owner={Safe(ownerProfileId)}; epoch={Safe(maneuver.EpochId)}; bot={Safe(maneuver.BotProfileId)}; originalAxis={Safe(maneuver.AxisFamily)}; settleSeconds={AlternateAxisSettleDuration.TotalSeconds:0.0}; physicalAuthorization=false; sameParticipants=true");
                }
                if (deferReplacement)
                {
                    return null;
                }
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"TC_ALT_DEVELOP_AXIS_CHANGE_REQUALIFIED owner={Safe(ownerProfileId)}; epoch={Safe(maneuver.EpochId)}; observedSeconds={observedSeconds:0.00}; freshReplanRequired=true; physicalAuthorization=false");
            }
        }

        return ownerGroup
            .OrderByDescending(item => item.AlternateAxisEvidenceScore)
            .ThenBy(item => item.SelectedPathLengthMeters)
            .ThenBy(item => item.ThreatId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.AlternateCandidateId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool TryRefreshExistingManeuverEpoch(
        ThreatGroupRouteAssignment assignment,
        RouteAssignmentSnapshot assignmentSnapshot,
        DateTimeOffset now,
        out AltDevelopManeuverEpochState? maneuver,
        out bool materialRetarget)
    {
        maneuver = null;
        materialRetarget = false;
        AltDevelopManeuverEpochState? hardBroken = null;
        string hardBreakReason = "none";
        string previousCandidate = "none";
        Vector3 previousCenter = default;
        float activeDestinationToCurrentRegion = 0f;
        float retargetThreshold = 0f;

        lock (Sync)
        {
            if (!ManeuverByOwner.TryGetValue(assignment.OwnerProfileId, out var current))
            {
                return false;
            }

            if (!IsManeuverIntentCompatible(current, assignment))
            {
                ManeuverByOwner.Remove(assignment.OwnerProfileId);
                RetainedPlanByOwner.Remove(assignment.OwnerProfileId);
                StabilityByOwner.Remove(assignment.OwnerProfileId);
                maneuverEpochAbortsTotal++;
                hardBroken = current;
                hardBreakReason = DescribeManeuverIntentBreak(current, assignment);
            }
            else
            {
                previousCandidate = current.CurrentCandidateId;
                previousCenter = current.CurrentRegionCenter;

                if (current.PhysicalArrivalObserved)
                {
                    float arrivalToCurrentRegion = Vector3.Distance(current.PhysicalArrivalPosition, assignment.AlternateEstablishmentRegionCenter);
                    float arrivalCompatibilityRadius = Math.Max(0.5f, assignment.AlternateEstablishmentRegionRadiusMeters);
                    if (arrivalToCurrentRegion > arrivalCompatibilityRadius)
                    {
                        current.PhysicalArrivalObserved = false;
                        current.PhysicalArrivalAtUtc = DateTimeOffset.MinValue;
                        current.PhysicalArrivalPosition = default;
                        current.PhysicalArrivalRegionCenter = default;
                        current.PhysicalArrivalRegionRadiusMeters = 0f;
                    }
                }

                // Current-solution publications are allowed to drift while the maneuver survives, but an
                // active physical lease must never compare only publication N to N-1: many sub-threshold
                // movements could otherwise accumulate into a materially stale path. The lease-origin
                // candidate/region is therefore the anti-stale reference for the entire lifetime of that
                // physical lease. Between leases there is no stale path to protect, so the maneuver may
                // freely absorb the newest compatible solution and the next lease will calculate a fresh path.
                if (ActiveByOwner.TryGetValue(assignment.OwnerProfileId, out var activeLease)
                    && string.Equals(activeLease.ManeuverEpochId, current.EpochId, StringComparison.OrdinalIgnoreCase))
                {
                    previousCandidate = activeLease.CandidateId;
                    previousCenter = activeLease.EstablishmentRegionCenter;
                    activeDestinationToCurrentRegion = Vector3.Distance(activeLease.EstablishmentAnchor, assignment.AlternateEstablishmentRegionCenter);
                    // The old physical destination remains relevant only while the point that produced
                    // this lease still falls inside the newest establishment region after accounting for
                    // the command reach radius. This compares against the lease origin, so gradual threat
                    // motion cannot accumulate behind a sequence of individually small publications.
                    // Intent continuity: a compatible maneuver remains the same tactical intent
                    // when RouteAssignment picks a neighboring candidate inside the same semantic axis.
                    // Restart the physical lease only when the current destination is no longer inside
                    // the newest establishment region. Candidate-id churn alone must not make the
                    // Operator reverse/restart while the flank side, member and threat are unchanged.
                    retargetThreshold = Math.Max(0.5f, assignment.AlternateEstablishmentRegionRadiusMeters);
                    materialRetarget = activeDestinationToCurrentRegion > retargetThreshold;
                }

                current.CurrentCandidateId = assignment.AlternateCandidateId;
                current.CurrentAssignmentIdentity = AssignmentIdentity(assignment);
                current.CurrentAnchor = assignment.AlternateEstablishmentAnchor;
                current.CurrentRegionCenter = assignment.AlternateEstablishmentRegionCenter;
                current.CurrentRegionRadiusMeters = assignment.AlternateEstablishmentRegionRadiusMeters;
                current.LastConfirmedAtUtc = now;
                current.LastAssignmentPublishedAtUtc = assignmentSnapshot.PublishedAtUtc > current.LastAssignmentPublishedAtUtc
                    ? assignmentSnapshot.PublishedAtUtc
                    : current.LastAssignmentPublishedAtUtc;
                current.UpstreamGapActive = false;
                current.UpstreamGapStartedAtUtc = DateTimeOffset.MinValue;
                current.UpstreamGapReason = "none";
                current.AlternateAxisObservedAtUtc = DateTimeOffset.MinValue;
                maneuverCurrentSolutionConfirmationsTotal++;
                if (materialRetarget)
                {
                    current.RetargetCount++;
                    maneuverSolutionRetargetsTotal++;
                }
                maneuver = current;
            }
        }

        if (hardBroken != null)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_MANEUVER_HARD_BREAK owner={Safe(hardBroken.OwnerProfileId)}; epoch={Safe(hardBroken.EpochId)}; oldThreat={Safe(hardBroken.ThreatId)}; newThreat={Safe(assignment.ThreatId)}; oldBot={Safe(hardBroken.BotProfileId)}; newBot={Safe(assignment.SelectedAltBotProfileId)}; oldAxisFamily={Safe(hardBroken.AxisFamily)}; newAxisFamily={Safe(AxisFamily(assignment))}; oldCandidate={Safe(hardBroken.CurrentCandidateId)}; newCandidate={Safe(assignment.AlternateCandidateId)}; reason={Safe(hardBreakReason)}; physicalLeaseReused=false; pathReused=false; windowReused=false");
            return false;
        }

        if (maneuver != null)
        {
            TacticalIntentContinuityService.BeginOrRefresh(
                maneuver.BotProfileId,
                maneuver.OwnerProfileId,
                maneuver.ThreatId,
                TacticalIntentContinuityService.AltDevelopIntent,
                maneuver.EpochId,
                now,
                materialRetarget ? "maneuver_solution_materially_retargeted" : "maneuver_current_solution_confirmed",
                protectsFromCohesion: true);
        }

        if (maneuver != null && materialRetarget)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_MANEUVER_SOLUTION_RETARGETED owner={Safe(maneuver.OwnerProfileId)}; epoch={Safe(maneuver.EpochId)}; threat={Safe(maneuver.ThreatId)}; bot={Safe(maneuver.BotProfileId)}; axisFamily={Safe(maneuver.AxisFamily)}; previousCandidate={Safe(previousCandidate)}; currentCandidate={Safe(maneuver.CurrentCandidateId)}; activeDestinationToCurrentRegion={activeDestinationToCurrentRegion:0.00}; retargetThreshold={retargetThreshold:0.00}; leaseOriginCenter={VectorText(previousCenter)}; currentCenter={VectorText(maneuver.CurrentRegionCenter)}; currentRadius={maneuver.CurrentRegionRadiusMeters:0.00}; nextPhysicalLeaseRequiresFreshPath=true");
        }

        return maneuver != null;
    }

    private static void SeedManeuverResumeProofIfNeeded(
        AltDevelopManeuverEpochState maneuver,
        ThreatGroupRouteAssignment assignment,
        RouteAssignmentSnapshot assignmentSnapshot,
        DateTimeOffset now)
    {
        // Retention after a long upstream gap or axis disagreement preserves identity, not
        // historical stability samples. Rebuild the admission proof from fresh publications.
        if (maneuver.NeedsFreshStability)
        {
            return;
        }
        string identity = AssignmentIdentity(assignment);
        lock (Sync)
        {
            if (StabilityByOwner.TryGetValue(assignment.OwnerProfileId, out var existing)
                && string.Equals(existing.Identity, identity, StringComparison.Ordinal)
                && existing.QualificationObserved)
            {
                return;
            }

            StabilityByOwner.Remove(assignment.OwnerProfileId);
            RetainedPlanByOwner[assignment.OwnerProfileId] = new RetainedPlanContinuityState(
                identity,
                assignmentSnapshot.PublishedAtUtc - AssignmentStableDuration,
                assignmentSnapshot.PublishedAtUtc,
                MinimumStableSamples,
                now,
                "maneuver_epoch_current_solution:" + maneuver.EpochId);
        }
    }

    private static string PlanManeuverEpochIdForLease(
        ThreatGroupRouteAssignment assignment,
        DateTimeOffset now,
        out bool resumedExisting)
    {
        lock (Sync)
        {
            if (ManeuverByOwner.TryGetValue(assignment.OwnerProfileId, out var current)
                && IsManeuverIntentCompatible(current, assignment))
            {
                resumedExisting = true;
                return current.EpochId;
            }
        }

        resumedExisting = false;
        return "tc_alt_maneuver_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Safe(assignment.SelectedAltBotProfileId);
    }

    private static AltDevelopManeuverEpochState EnsureManeuverEpochForStartedLease(
        ThreatGroupRouteAssignment assignment,
        RouteAssignmentSnapshot assignmentSnapshot,
        DateTimeOffset now,
        string plannedEpochId,
        out bool resumedExisting)
    {
        lock (Sync)
        {
            if (ManeuverByOwner.TryGetValue(assignment.OwnerProfileId, out var current)
                && IsManeuverIntentCompatible(current, assignment))
            {
                current.CurrentCandidateId = assignment.AlternateCandidateId;
                current.CurrentAssignmentIdentity = AssignmentIdentity(assignment);
                current.CurrentAnchor = assignment.AlternateEstablishmentAnchor;
                current.CurrentRegionCenter = assignment.AlternateEstablishmentRegionCenter;
                current.CurrentRegionRadiusMeters = assignment.AlternateEstablishmentRegionRadiusMeters;
                current.LastConfirmedAtUtc = now;
                current.LastAssignmentPublishedAtUtc = assignmentSnapshot.PublishedAtUtc;
                current.NeedsFreshStability = false;
                current.LeaseCount++;
                resumedExisting = true;
                maneuverLeaseResumesTotal++;
                return current;
            }

            var created = new AltDevelopManeuverEpochState(
                assignment.OwnerProfileId,
                plannedEpochId,
                assignment.ThreatId,
                AxisFamily(assignment),
                assignment.SelectedAltBotProfileId,
                assignment.SelectedAltOperatorId,
                assignment.AlternateCandidateId,
                AssignmentIdentity(assignment),
                assignment.AlternateEstablishmentAnchor,
                assignment.AlternateEstablishmentRegionCenter,
                assignment.AlternateEstablishmentRegionRadiusMeters,
                assignmentSnapshot.PublishedAtUtc,
                now);
            ManeuverByOwner[assignment.OwnerProfileId] = created;
            maneuverEpochsStartedTotal++;
            resumedExisting = false;
            return created;
        }
    }

    private static bool IsManeuverSameParticipants(AltDevelopManeuverEpochState maneuver, ThreatGroupRouteAssignment assignment)
        => string.Equals(maneuver.OwnerProfileId, assignment.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(maneuver.ThreatId, assignment.ThreatId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(maneuver.BotProfileId, assignment.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(maneuver.OperatorId, assignment.SelectedAltOperatorId, StringComparison.OrdinalIgnoreCase);

    private static bool IsManeuverIntentCompatible(AltDevelopManeuverEpochState maneuver, ThreatGroupRouteAssignment assignment)
    {
        if (!IsManeuverSameParticipants(maneuver, assignment))
        {
            return false;
        }

        string incomingFamily = AxisFamily(assignment);
        if (incomingFamily.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
        {
            // Fail closed when an assignment carries no semantic sector: only the exact candidate may
            // continue. Normal RouteAssignment publications always carry AlternateApproachSector, so this
            // path is a conservative compatibility fallback rather than the normal maneuver contract.
            return string.Equals(maneuver.CurrentCandidateId, assignment.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(maneuver.AxisFamily, incomingFamily, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(maneuver.AxisFamily, incomingFamily, StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeManeuverIntentBreak(AltDevelopManeuverEpochState maneuver, ThreatGroupRouteAssignment assignment)
    {
        if (!string.Equals(maneuver.ThreatId, assignment.ThreatId, StringComparison.OrdinalIgnoreCase)) return "threat_identity_changed";
        if (!string.Equals(maneuver.BotProfileId, assignment.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase)) return "selected_bot_changed";
        if (!string.Equals(maneuver.OperatorId, assignment.SelectedAltOperatorId, StringComparison.OrdinalIgnoreCase)) return "selected_operator_changed";
        if (!string.Equals(maneuver.AxisFamily, AxisFamily(assignment), StringComparison.OrdinalIgnoreCase)) return "axis_family_changed";
        return "maneuver_intent_changed";
    }

    private static string AxisFamily(ThreatGroupRouteAssignment assignment)
    {
        // RouteAssignment carries the exact ApproachSector from the RouteIndependence observation that
        // produced this assignment. Do not reinterpret an assignment through a newer neighboring snapshot:
        // authored anchors may keep the same candidate id while their left/right relationship to a moving
        // threat changes. The compound-id suffix is therefore only a fallback for dynamic-sector records.
        string sector = Normalize(assignment.AlternateApproachSector);
        if (sector is "left_oblique" or "left_opposite") return "left";
        if (sector is "right_oblique" or "right_opposite") return "right";
        if (sector == "rear_opposite") return "rear";

        string token = CandidateAxisToken(assignment.AlternateCandidateId);
        if (string.Equals(assignment.AlternateCandidateSource, "dynamic_objective_sector", StringComparison.OrdinalIgnoreCase)
            && IsKnownAxisSector(token))
        {
            if (token is "left_oblique" or "left_opposite") return "left";
            if (token is "right_oblique" or "right_opposite") return "right";
            if (token == "rear_opposite") return "rear";
        }
        return "candidate:" + Normalize(assignment.AlternateCandidateId);
    }

    private static string CandidateAxisToken(string? candidateId)
    {
        string normalized = Normalize(candidateId);
        int separator = normalized.LastIndexOf(':');
        return separator >= 0 && separator + 1 < normalized.Length
            ? normalized.Substring(separator + 1)
            : normalized;
    }

    private static bool IsKnownAxisSector(string value)
        => value is "left_oblique" or "left_opposite" or "right_oblique" or "right_opposite" or "rear_opposite";

    private static void MarkManeuverUpstreamGap(DateTimeOffset now, string reason)
    {
        lock (Sync)
        {
            foreach (var maneuver in ManeuverByOwner.Values)
            {
                if (maneuver.UpstreamGapActive) continue;
                maneuver.UpstreamGapActive = true;
                maneuver.UpstreamGapStartedAtUtc = now;
                maneuver.UpstreamGapReason = reason;
            }
        }
    }

    private static void MarkMissingManeuverOwners(HashSet<string> planningOwners, DateTimeOffset now)
    {
        lock (Sync)
        {
            foreach (var pair in ManeuverByOwner)
            {
                if (planningOwners.Contains(pair.Key) || pair.Value.UpstreamGapActive) continue;
                pair.Value.UpstreamGapActive = true;
                pair.Value.UpstreamGapStartedAtUtc = now;
                pair.Value.UpstreamGapReason = "owner_missing_from_current_planning_candidates";
            }
        }
    }

    private static void PruneManeuverEpochs(DateTimeOffset now)
    {
        var expired = new List<AltDevelopManeuverEpochState>();
        var newlySuspended = new List<AltDevelopManeuverEpochState>();
        lock (Sync)
        {
            var staleOwners = new List<string>();
            foreach (var pair in ManeuverByOwner)
            {
                AltDevelopManeuverEpochState maneuver = pair.Value;
                if (!maneuver.UpstreamGapActive)
                {
                    continue;
                }

                TimeSpan upstreamGap = now - maneuver.UpstreamGapStartedAtUtc;
                bool activeLeaseRecentlyProgressing = ActiveByOwner.TryGetValue(pair.Key, out var activeLease)
                    && string.Equals(activeLease.ManeuverEpochId, maneuver.EpochId, StringComparison.OrdinalIgnoreCase)
                    && activeLease.LastProgressAtUtc != DateTimeOffset.MinValue
                    && now - activeLease.LastProgressAtUtc <= NoProgressDuration;
                // Existing physical continuity expires at the original progress-sensitive boundary.
                // Only the dormant decision identity survives longer; resumption needs a new
                // stability qualification, fresh route, lease and scheduler permission.
                TimeSpan effectiveManeuverEpochGrace = activeLeaseRecentlyProgressing
                    ? ProgressingUpstreamSnapshotGraceDuration
                    : UpstreamSnapshotGraceDuration;
                if (upstreamGap > effectiveManeuverEpochGrace
                    && upstreamGap <= SuspendedManeuverMemoryDuration
                    && !maneuver.NeedsFreshStability)
                {
                    maneuver.NeedsFreshStability = true;
                    newlySuspended.Add(maneuver);
                }
                if (activeLeaseRecentlyProgressing
                    && upstreamGap > UpstreamSnapshotGraceDuration
                    && upstreamGap <= effectiveManeuverEpochGrace)
                {
                    maneuverEpochProgressGraceTicksTotal++;
                }

                if (upstreamGap > SuspendedManeuverMemoryDuration)
                {
                    staleOwners.Add(pair.Key);
                }
            }

            foreach (string owner in staleOwners)
            {
                expired.Add(ManeuverByOwner[owner]);
                ManeuverByOwner.Remove(owner);
                RetainedPlanByOwner.Remove(owner);
                StabilityByOwner.Remove(owner);
                maneuverEpochAbortsTotal++;
            }
        }

        foreach (var maneuver in newlySuspended)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_MANEUVER_MEMORY_RETAINED owner={Safe(maneuver.OwnerProfileId)}; epoch={Safe(maneuver.EpochId)}; threat={Safe(maneuver.ThreatId)}; bot={Safe(maneuver.BotProfileId)}; gapReason={Safe(maneuver.UpstreamGapReason)}; memorySeconds={SuspendedManeuverMemoryDuration.TotalSeconds:0.0}; freshStabilityRequired=true; staleRouteAllowed=false; staleLeaseAllowed=false");
        }

        foreach (var maneuver in expired)
        {
            TacticalIntentContinuityService.EndIntent(
                maneuver.BotProfileId,
                TacticalIntentContinuityService.AltDevelopIntent,
                now,
                "maneuver_epoch_terminal:upstream_gap_expired:" + maneuver.UpstreamGapReason);
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_MANEUVER_HARD_BREAK owner={Safe(maneuver.OwnerProfileId)}; epoch={Safe(maneuver.EpochId)}; oldThreat={Safe(maneuver.ThreatId)}; oldBot={Safe(maneuver.BotProfileId)}; oldAxisFamily={Safe(maneuver.AxisFamily)}; reason=upstream_gap_expired:{Safe(maneuver.UpstreamGapReason)}; gapSeconds={Math.Max(0d, (now - maneuver.UpstreamGapStartedAtUtc).TotalSeconds):0.00}; physicalLeaseReused=false; pathReused=false; windowReused=false");
        }
    }

    private static void EndManeuverEpoch(string ownerProfileId, DateTimeOffset now, string reason, bool completed)
    {
        AltDevelopManeuverEpochState? ended = null;
        lock (Sync)
        {
            if (ManeuverByOwner.TryGetValue(ownerProfileId, out ended))
            {
                ManeuverByOwner.Remove(ownerProfileId);
                RetainedPlanByOwner.Remove(ownerProfileId);
                StabilityByOwner.Remove(ownerProfileId);
                if (completed) maneuverEpochsCompletedTotal++;
                else maneuverEpochAbortsTotal++;
            }
        }
        if (ended != null)
        {
            TacticalIntentContinuityService.EndIntent(
                ended.BotProfileId,
                TacticalIntentContinuityService.AltDevelopIntent,
                now,
                "maneuver_epoch_terminal:" + reason);
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_ALT_DEVELOP_MANEUVER_ENDED owner={Safe(ended.OwnerProfileId)}; epoch={Safe(ended.EpochId)}; threat={Safe(ended.ThreatId)}; bot={Safe(ended.BotProfileId)}; axisFamily={Safe(ended.AxisFamily)}; candidate={Safe(ended.CurrentCandidateId)}; leases={ended.LeaseCount}; retargets={ended.RetargetCount}; compatibleSeconds={ended.TranslationCompatibleSeconds:0.000}; age={Math.Max(0d, (now - ended.StartedAtUtc).TotalSeconds):0.00}; completed={Bool(completed)}; reason={Safe(reason)}");
        }
    }

    private static bool ShouldRetainManeuverEpochForAdmission(string admission)
    {
        if (string.IsNullOrWhiteSpace(admission)) return false;
        return !admission.StartsWith("planning_not_viable:", StringComparison.OrdinalIgnoreCase)
            && !admission.StartsWith("member_evidence_generation_missing", StringComparison.OrdinalIgnoreCase)
            && admission.IndexOf("operator_dead_or_missing", StringComparison.OrdinalIgnoreCase) < 0
            && admission.IndexOf("competing_relevant_threat_identity_changed", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static bool ShouldRetainManeuverEpochForLiveGate(string liveGateReason)
    {
        if (string.IsNullOrWhiteSpace(liveGateReason)) return false;
        return !liveGateReason.StartsWith("bot_dead", StringComparison.OrdinalIgnoreCase)
            && !liveGateReason.StartsWith("live_botowner_unavailable", StringComparison.OrdinalIgnoreCase)
            && !liveGateReason.StartsWith("goal_enemy_changed", StringComparison.OrdinalIgnoreCase)
            && !liveGateReason.StartsWith("sain_path_bind_failed", StringComparison.OrdinalIgnoreCase)
            && !liveGateReason.StartsWith("sprint_bridge_failed", StringComparison.OrdinalIgnoreCase)
            && !liveGateReason.StartsWith("steering_bridge_failed", StringComparison.OrdinalIgnoreCase);
    }

    private static string VectorText(Vector3 value)
        => value.x.ToString("0.00", CultureInfo.InvariantCulture) + "," + value.y.ToString("0.00", CultureInfo.InvariantCulture) + "," + value.z.ToString("0.00", CultureInfo.InvariantCulture);

    private static void ResetStability(string ownerProfileId)
    {
        lock (Sync)
        {
            StabilityByOwner.Remove(ownerProfileId);
        }
    }

    private static void SetCooldown(string ownerProfileId, DateTimeOffset now, TimeSpan duration)
    {
        lock (Sync)
        {
            CooldownByOwner[ownerProfileId] = now + duration;
        }
    }

    private static string AssignmentIdentity(ThreatGroupRouteAssignment assignment)
        => string.Join("|",
            Normalize(assignment.OwnerProfileId),
            Normalize(assignment.ThreatId),
            Normalize(assignment.AlternateCandidateId),
            Normalize(assignment.AlternateApproachSector),
            Normalize(assignment.SelectedAltBotProfileId),
            Normalize(assignment.SelectedAltOperatorId));

    private static Vector3 ResolveBotPosition(BotOwner botOwner)
    {
        object? player = VanguardOperatorRuntimeAuditReflection.GetMember(botOwner, "GetPlayer", "Player");
        object? transform = VanguardOperatorRuntimeAuditReflection.GetDeep(player, "PlayerBones", "BodyTransform");
        object? position = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(transform, "position");
        if (position is Vector3 vector)
        {
            return vector;
        }
        return botOwner.Position;
    }

    private static bool PlanningIdentityMatchesThreat(string? identity, string threatId)
    {
        if (string.IsNullOrWhiteSpace(identity)) return false;
        string[] parts = identity.Split('|');
        return parts.Length >= 2
            && string.Equals(parts[1], Normalize(threatId), StringComparison.Ordinal);
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().ToLowerInvariant();
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private sealed class AltDevelopEvidenceState
    {
        public AltDevelopEvidenceState(
            string ownerProfileId,
            string threatId,
            string candidateId,
            string botProfileId,
            string operatorId,
            long generation,
            DateTimeOffset startedAtUtc)
        {
            OwnerProfileId = ownerProfileId;
            ThreatId = threatId;
            CandidateId = candidateId;
            BotProfileId = botProfileId;
            OperatorId = operatorId;
            Generation = generation;
            StartedAtUtc = startedAtUtc;
            LastObservedAtUtc = startedAtUtc;
            ClosestRegionDistanceMeters = float.PositiveInfinity;
        }

        public string OwnerProfileId { get; }
        public string ThreatId { get; }
        public string CandidateId { get; }
        public string BotProfileId { get; }
        public string OperatorId { get; }
        public long Generation { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public DateTimeOffset LastObservedAtUtc { get; set; }
        public DateTimeOffset CompletedAtUtc { get; set; } = DateTimeOffset.MinValue;
        public string State { get; set; } = "Active";
        public string Reason { get; set; } = "movement_started";
        public bool PhysicalProgressObserved { get; set; }
        public string PhysicalProgressKind { get; set; } = "none";
        public float ClosestRegionDistanceMeters { get; set; }
        public string ProgressManeuverEpochId { get; set; } = "none";
        public string ProgressSolutionIdentity { get; set; } = "none";
        public Vector3 ProgressRegionCenter { get; set; }
        public float ProgressRegionRadiusMeters { get; set; }

        public AltDevelopEvidence ToSnapshot(bool progressCurrent, string progressProvenanceReason)
            => new(
                OwnerProfileId,
                ThreatId,
                CandidateId,
                BotProfileId,
                OperatorId,
                Generation,
                StartedAtUtc,
                LastObservedAtUtc,
                CompletedAtUtc,
                State,
                Reason,
                PhysicalProgressObserved && progressCurrent,
                PhysicalProgressObserved && !progressCurrent
                    ? "stale_solution_provenance:" + progressProvenanceReason
                    : PhysicalProgressKind,
                PhysicalProgressObserved && progressCurrent
                    ? ClosestRegionDistanceMeters
                    : float.PositiveInfinity);
    }

    private sealed class PendingSearchHandoffState
    {
        public PendingSearchHandoffState(
            string ownerProfileId,
            string assignmentIdentity,
            string threatId,
            string candidateId,
            string botProfileId,
            string windowId,
            string previousLeaseId,
            string previousRequestKind,
            DateTimeOffset startedAtUtc,
            DateTimeOffset maxUntilUtc,
            string initialAdmission)
        {
            OwnerProfileId = ownerProfileId;
            AssignmentIdentity = assignmentIdentity;
            ThreatId = threatId;
            CandidateId = candidateId;
            BotProfileId = botProfileId;
            WindowId = windowId;
            PreviousLeaseId = previousLeaseId;
            PreviousRequestKind = previousRequestKind;
            StartedAtUtc = startedAtUtc;
            MaxUntilUtc = maxUntilUtc;
            InitialAdmission = initialAdmission;
        }

        public string OwnerProfileId { get; }
        public string AssignmentIdentity { get; }
        public string ThreatId { get; }
        public string CandidateId { get; }
        public string BotProfileId { get; }
        public string WindowId { get; }
        public string PreviousLeaseId { get; }
        public string PreviousRequestKind { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public DateTimeOffset MaxUntilUtc { get; }
        public string InitialAdmission { get; }
        public bool ReadyObserved { get; set; }
        public string Summary => "owner=" + Safe(OwnerProfileId)
            + ";threat=" + Safe(ThreatId)
            + ";axis=" + Safe(CandidateId)
            + ";bot=" + Safe(BotProfileId)
            + ";window=" + Safe(WindowId)
            + ";previousLease=" + Safe(PreviousLeaseId)
            + ";previousRequest=" + Safe(PreviousRequestKind)
            + ";initialAdmission=" + Safe(InitialAdmission);
    }

    private sealed class AssignmentStabilityState
    {
        public AssignmentStabilityState(string identity, DateTimeOffset firstSeenAtUtc, DateTimeOffset lastEvidencePublishedAtUtc, int sampleCount)
        {
            Identity = identity;
            FirstSeenAtUtc = firstSeenAtUtc;
            LastSeenAtUtc = firstSeenAtUtc;
            LastEvidencePublishedAtUtc = lastEvidencePublishedAtUtc;
            SampleCount = sampleCount;
        }

        public string Identity { get; }
        public DateTimeOffset FirstSeenAtUtc { get; }
        public DateTimeOffset LastSeenAtUtc { get; set; }
        public DateTimeOffset LastEvidencePublishedAtUtc { get; set; }
        public int SampleCount { get; set; }
        public bool UpstreamGapActive { get; set; }
        public DateTimeOffset UpstreamGapStartedAtUtc { get; set; }
        public string UpstreamGapReason { get; set; } = "none";
        public bool QualificationObserved { get; set; }
    }

    private sealed class RetainedPlanContinuityState
    {
        public RetainedPlanContinuityState(
            string identity,
            DateTimeOffset firstSeenAtUtc,
            DateTimeOffset lastEvidencePublishedAtUtc,
            int sampleCount,
            DateTimeOffset retainedAtUtc,
            string reason)
        {
            Identity = identity;
            FirstSeenAtUtc = firstSeenAtUtc;
            LastEvidencePublishedAtUtc = lastEvidencePublishedAtUtc;
            SampleCount = sampleCount;
            RetainedAtUtc = retainedAtUtc;
            Reason = reason;
        }

        public string Identity { get; }
        public DateTimeOffset FirstSeenAtUtc { get; }
        public DateTimeOffset LastEvidencePublishedAtUtc { get; }
        public int SampleCount { get; }
        public DateTimeOffset RetainedAtUtc { get; }
        public string Reason { get; }
    }

    private sealed class AltDevelopManeuverEpochState
    {
        public AltDevelopManeuverEpochState(
            string ownerProfileId,
            string epochId,
            string threatId,
            string axisFamily,
            string botProfileId,
            string operatorId,
            string currentCandidateId,
            string currentAssignmentIdentity,
            Vector3 currentAnchor,
            Vector3 currentRegionCenter,
            float currentRegionRadiusMeters,
            DateTimeOffset lastAssignmentPublishedAtUtc,
            DateTimeOffset startedAtUtc)
        {
            OwnerProfileId = ownerProfileId;
            EpochId = epochId;
            ThreatId = threatId;
            AxisFamily = axisFamily;
            BotProfileId = botProfileId;
            OperatorId = operatorId;
            CurrentCandidateId = currentCandidateId;
            CurrentAssignmentIdentity = currentAssignmentIdentity;
            CurrentAnchor = currentAnchor;
            CurrentRegionCenter = currentRegionCenter;
            CurrentRegionRadiusMeters = currentRegionRadiusMeters;
            LastAssignmentPublishedAtUtc = lastAssignmentPublishedAtUtc;
            StartedAtUtc = startedAtUtc;
            LastConfirmedAtUtc = startedAtUtc;
            LeaseCount = 1;
        }

        public string OwnerProfileId { get; }
        public string EpochId { get; }
        public string ThreatId { get; }
        public string AxisFamily { get; }
        public string BotProfileId { get; }
        public string OperatorId { get; }
        public string CurrentCandidateId { get; set; }
        public string CurrentAssignmentIdentity { get; set; }
        public Vector3 CurrentAnchor { get; set; }
        public Vector3 CurrentRegionCenter { get; set; }
        public float CurrentRegionRadiusMeters { get; set; }
        public DateTimeOffset LastAssignmentPublishedAtUtc { get; set; }
        public DateTimeOffset StartedAtUtc { get; }
        public DateTimeOffset LastConfirmedAtUtc { get; set; }
        public int LeaseCount { get; set; }
        public int RetargetCount { get; set; }
        public double TranslationCompatibleSeconds { get; set; }
        public bool PhysicalArrivalObserved { get; set; }
        public DateTimeOffset PhysicalArrivalAtUtc { get; set; } = DateTimeOffset.MinValue;
        public Vector3 PhysicalArrivalPosition { get; set; }
        public Vector3 PhysicalArrivalRegionCenter { get; set; }
        public float PhysicalArrivalRegionRadiusMeters { get; set; }
        public bool UpstreamGapActive { get; set; }
        public DateTimeOffset UpstreamGapStartedAtUtc { get; set; }
        public string UpstreamGapReason { get; set; } = "none";
        public DateTimeOffset AlternateAxisObservedAtUtc { get; set; } = DateTimeOffset.MinValue;
        public bool NeedsFreshStability { get; set; }
    }

    private sealed class AltDevelopLeaseState
    {
        public AltDevelopLeaseState(
            string ownerProfileId,
            string threatId,
            string candidateId,
            string botProfileId,
            string operatorId,
            string leaseId,
            string windowId,
            long generation,
            Vector3 establishmentAnchor,
            Vector3 establishmentRegionCenter,
            float establishmentRegionRadiusMeters,
            float pathLengthMeters,
            NavMeshPath preparedPath,
            DateTimeOffset sourceMemberEvidenceCapturedAtUtc,
            DateTimeOffset sourceAssignmentPublishedAtUtc,
            DateTimeOffset startedAtUtc,
            DateTimeOffset maxUntilUtc,
            DateTimeOffset noProgressUntilUtc,
            Vector3 lastWorldPosition,
            float lastRegionDistanceMeters,
            string assignmentIdentity,
            DateTimeOffset planStableFirstSeenAtUtc,
            DateTimeOffset planStableEvidencePublishedAtUtc,
            int planStableSampleCount,
            string admissionAtStart)
        {
            OwnerProfileId = ownerProfileId;
            ThreatId = threatId;
            CandidateId = candidateId;
            BotProfileId = botProfileId;
            OperatorId = operatorId;
            LeaseId = leaseId;
            WindowId = windowId;
            Generation = generation;
            EstablishmentAnchor = establishmentAnchor;
            EstablishmentRegionCenter = establishmentRegionCenter;
            EstablishmentRegionRadiusMeters = establishmentRegionRadiusMeters;
            PathLengthMeters = pathLengthMeters;
            PreparedPath = preparedPath;
            SourceMemberEvidenceCapturedAtUtc = sourceMemberEvidenceCapturedAtUtc;
            LastUpstreamPublishedAtUtc = sourceAssignmentPublishedAtUtc;
            LastUpstreamConfirmedAtUtc = startedAtUtc;
            StartedAtUtc = startedAtUtc;
            MaxUntilUtc = maxUntilUtc;
            NoProgressUntilUtc = noProgressUntilUtc;
            LastProgressAtUtc = startedAtUtc;
            LastWorldPosition = lastWorldPosition;
            LastWorldSampleAtUtc = startedAtUtc;
            LastRegionDistanceMeters = lastRegionDistanceMeters;
            AssignmentIdentity = assignmentIdentity;
            PlanStableFirstSeenAtUtc = planStableFirstSeenAtUtc;
            PlanStableEvidencePublishedAtUtc = planStableEvidencePublishedAtUtc;
            PlanStableSampleCount = planStableSampleCount;
            AdmissionAtStart = admissionAtStart;
            LastTranslationCompatibleAtUtc = startedAtUtc;
        }

        public string OwnerProfileId { get; }
        public string ThreatId { get; }
        public string CandidateId { get; }
        public string BotProfileId { get; }
        public string OperatorId { get; }
        public string LeaseId { get; }
        public string WindowId { get; }
        public long Generation { get; }
        public Vector3 EstablishmentAnchor { get; }
        public Vector3 EstablishmentRegionCenter { get; }
        public float EstablishmentRegionRadiusMeters { get; }
        public float PathLengthMeters { get; }
        public NavMeshPath PreparedPath { get; }
        public DateTimeOffset SourceMemberEvidenceCapturedAtUtc { get; }
        public DateTimeOffset LastUpstreamPublishedAtUtc { get; set; }
        public DateTimeOffset LastUpstreamConfirmedAtUtc { get; set; }
        public DateTimeOffset StartedAtUtc { get; }
        public DateTimeOffset MaxUntilUtc { get; }
        public DateTimeOffset NoProgressUntilUtc { get; set; }
        public DateTimeOffset LastProgressAtUtc { get; set; }
        public Vector3 LastWorldPosition { get; set; }
        public DateTimeOffset LastWorldSampleAtUtc { get; set; }
        public float LastRegionDistanceMeters { get; set; }
        public string AssignmentIdentity { get; }
        public DateTimeOffset PlanStableFirstSeenAtUtc { get; }
        public DateTimeOffset PlanStableEvidencePublishedAtUtc { get; }
        public int PlanStableSampleCount { get; }
        public string AdmissionAtStart { get; }
        public DateTimeOffset LastTranslationCompatibleAtUtc { get; set; }
        public double TranslationCompatibleSeconds { get; set; }
        public int TranslationCompatibleNativeTicks { get; set; }
        public string ManeuverEpochId { get; set; } = "pending";
        public bool ManeuverResume { get; set; }

        public string Summary => "owner=" + Safe(OwnerProfileId)
            + ";maneuver=" + Safe(ManeuverEpochId)
            + ";threat=" + Safe(ThreatId)
            + ";axis=" + Safe(CandidateId)
            + ";bot=" + Safe(BotProfileId)
            + ";operator=" + Safe(OperatorId)
            + ";lease=" + Safe(LeaseId)
            + ";window=" + Safe(WindowId)
            + ";generation=" + Generation.ToString(CultureInfo.InvariantCulture)
            + ";regionRadius=" + EstablishmentRegionRadiusMeters.ToString("0.00", CultureInfo.InvariantCulture)
            + ";pathLength=" + PathLengthMeters.ToString("0.00", CultureInfo.InvariantCulture)
            + ";regionDist=" + LastRegionDistanceMeters.ToString("0.00", CultureInfo.InvariantCulture)
            + ";upstreamAge=" + Math.Max(0d, (DateTimeOffset.UtcNow - LastUpstreamConfirmedAtUtc).TotalSeconds).ToString("0.00", CultureInfo.InvariantCulture)
            + ";translationCompatibleSeconds=" + TranslationCompatibleSeconds.ToString("0.000", CultureInfo.InvariantCulture)
            + ";translationCompatibleNativeTicks=" + TranslationCompatibleNativeTicks.ToString(CultureInfo.InvariantCulture)
            + ";age=" + Math.Max(0d, (DateTimeOffset.UtcNow - StartedAtUtc).TotalSeconds).ToString("0.00", CultureInfo.InvariantCulture);
    }
}

internal sealed class AltDevelopEvidence
{
    public static AltDevelopEvidence Empty { get; } = new(
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0,
        DateTimeOffset.MinValue, DateTimeOffset.MinValue, DateTimeOffset.MinValue, "none", "none",
        false, "none", float.PositiveInfinity);

    public AltDevelopEvidence(
        string ownerProfileId,
        string threatId,
        string candidateId,
        string botProfileId,
        string operatorId,
        long generation,
        DateTimeOffset startedAtUtc,
        DateTimeOffset lastObservedAtUtc,
        DateTimeOffset completedAtUtc,
        string state,
        string reason,
        bool physicalProgressObserved,
        string physicalProgressKind,
        float closestRegionDistanceMeters)
    {
        OwnerProfileId = ownerProfileId;
        ThreatId = threatId;
        CandidateId = candidateId;
        BotProfileId = botProfileId;
        OperatorId = operatorId;
        Generation = generation;
        StartedAtUtc = startedAtUtc;
        LastObservedAtUtc = lastObservedAtUtc;
        CompletedAtUtc = completedAtUtc;
        State = state;
        Reason = reason;
        PhysicalProgressObserved = physicalProgressObserved;
        PhysicalProgressKind = physicalProgressKind;
        ClosestRegionDistanceMeters = closestRegionDistanceMeters;
    }

    public string OwnerProfileId { get; }
    public string ThreatId { get; }
    public string CandidateId { get; }
    public string BotProfileId { get; }
    public string OperatorId { get; }
    public long Generation { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset LastObservedAtUtc { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public string State { get; }
    public string Reason { get; }
    public bool PhysicalProgressObserved { get; }
    public string PhysicalProgressKind { get; }
    public float ClosestRegionDistanceMeters { get; }
    public bool IsActive => string.Equals(State, "Active", StringComparison.OrdinalIgnoreCase);
    public bool IsCompleted => string.Equals(State, "Completed", StringComparison.OrdinalIgnoreCase);
    public bool DevelopmentObserved => StartedAtUtc != DateTimeOffset.MinValue && !string.Equals(State, "none", StringComparison.OrdinalIgnoreCase);
}
#endif
