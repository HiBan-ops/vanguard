#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;

// Responsibility: composes existing TC primitives into an opportunity-first multi-axis assault observation/execution bridge.
// Flow: any viable two-axis assignment may run with one FlankLead and one Direct member; when a third same-owner member has a proven path on the same alternate axis it remains pending until the exact current FlankLead AltDevelop maneuver has actually started, then it may be recruited as optional FlankSupport while at least one Direct member remains. Existing HeldAccess/SplitFlank/DirectCommit systems retain all containment/release authority.
// Authority boundary: this service may only ask TacticalOpportunityMovementService to position the selected FlankSupport on an already-proven alternate route. It never holds Direct members, releases holds, targets an enemy, commands aim/fire/suppression or creates a second tactical intent engine.
// Invariant: the Kaban composition requires only two viable axes, never a fixed 2+2 formation. FlankSupport is optional enrichment and requires the exact current FlankLead AltDevelop maneuver epoch already seeded by physical movement, a candidate-specific proven same-axis path and >=1 remaining Direct member; short physical lease gaps do not erase that still-current maneuver, while epoch rollover invalidates it. PressureShift/SecondaryCommit are observed from SplitFlank truth; this service cannot manufacture them.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class MultiAxisAssaultService
{
    public const string StatusTag = "VANGUARD_TC_MULTI_AXIS_ASSAULT_STATUS";

    private static readonly TimeSpan AcquisitionInterval = TimeSpan.FromSeconds(0.50d);
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(10d);
    private static readonly object Sync = new();
    private const int MaxTrackedSupportRejectionReasons = 24;
    private static readonly Dictionary<string, string> LastPhaseSignatureByGroup = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> SupportRejectionsByReason = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset nextAcquisitionAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;
    private static int groupsObservedTotal;
    private static int supportSelectedTotal;
    private static int supportPendingLeadTotal;
    private static int supportEligibleTotal;
    private static int supportStartAttemptsTotal;
    private static int supportStartsTotal;
    private static int supportRejectsTotal;
    private static int containObservedTotal;
    private static int alternateEntryObservedTotal;
    private static int pressureShiftObservedTotal;
    private static int secondaryCommitObservedTotal;

    public static void Tick(DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }

        if (!RouteAssignmentService.TryGetSnapshot(out RouteAssignmentSnapshot assignments))
        {
            return;
        }
        SplitFlankStateService.TryGetSnapshot(out SplitFlankStateSnapshot splitState);

        bool mayAcquire = now >= nextAcquisitionAtUtc;
        if (mayAcquire)
        {
            nextAcquisitionAtUtc = now + AcquisitionInterval;
        }

        int groupsObserved = 0;
        int supportSelected = 0;
        int supportPendingLead = 0;
        int supportEligible = 0;
        int containObserved = 0;
        int alternateEntryObserved = 0;
        int pressureShiftObserved = 0;
        int secondaryCommitObserved = 0;
        foreach (ThreatGroupRouteAssignment assignment in assignments.ThreatGroups)
        {
            if (!assignment.HasShadowAltSelection)
            {
                continue;
            }
            groupsObserved++;

            ThreatGroupSplitFlankStateObservation? state = splitState.ThreatGroups.FirstOrDefault(item =>
                string.Equals(item.OwnerProfileId, assignment.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ThreatId, assignment.ThreatId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.AlternateCandidateId, assignment.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.AlternateBotProfileId, assignment.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase));

            bool contain = assignment.DirectBotProfileIds.Any(ChokeCommitSupportService.HasActiveContributionForBot);
            bool leadDevelopment = AltDevelopMovementService.TryGetDevelopmentEvidence(assignment, now, out AltDevelopEvidence leadEvidence)
                && (leadEvidence.IsActive || leadEvidence.IsCompleted || leadEvidence.DevelopmentObserved);
            bool supportNearAlternateEntry = IsSupportNearAlternateEntry(assignment, now);
            bool alternateEntry = leadDevelopment || supportNearAlternateEntry;
            bool pressureShift = state != null && state.PositionEstablishedObserved && state.PressureProofObserved;
            bool secondaryCommit = state != null && (state.SynchronizedApproachCommitReady
                || state.SynchronizedCommitReady
                || state.DirectCommitCycleCompletedObserved);

            if (contain) containObserved++;
            if (alternateEntry) alternateEntryObserved++;
            if (pressureShift) pressureShiftObserved++;
            if (secondaryCommit) secondaryCommitObserved++;

            if (mayAcquire
                && assignment.HasShadowAltSupportSelection
                && assignment.DirectBotProfileIds.Count >= 1
                && TryResolveSupportCandidate(assignment, out AltRouteFeasibilityObservation supportCandidate))
            {
                supportSelected++;
                if (!AltDevelopMovementService.TryGetCurrentDevelopmentManeuverProof(
                        assignment,
                        now,
                        out _,
                        out _,
                        out _))
                {
                    supportPendingLead++;
                }
                else
                {
                    supportEligible++;
                    supportStartAttemptsTotal++;
                    if (TacticalOpportunityMovementService.TryStartFlankSupport(assignment, supportCandidate, now, out string supportStartReason))
                    {
                        supportStartsTotal++;
                    }
                    else
                    {
                        RecordSupportRejection(supportStartReason);
                    }
                }
            }

            string phase = secondaryCommit
                ? "SecondaryCommit"
                : pressureShift
                    ? "PressureShift"
                    : alternateEntry
                        ? "AlternateEntry"
                        : contain
                            ? "Contain"
                            : "Observe";
            EmitPhaseIfChanged(assignment, state, leadEvidence, supportNearAlternateEntry, pressureShift, phase);
        }

        groupsObservedTotal += groupsObserved;
        supportSelectedTotal += supportSelected;
        supportPendingLeadTotal += supportPendingLead;
        supportEligibleTotal += supportEligible;
        containObservedTotal += containObserved;
        alternateEntryObservedTotal += alternateEntryObserved;
        pressureShiftObservedTotal += pressureShiftObserved;
        secondaryCommitObservedTotal += secondaryCommitObserved;

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                "TC_MULTI_AXIS_ASSAULT_BOOT enabled=true; doctrine=opportunity_first_kaban_reference; composition=Contain>AlternateEntry>PressureShift>SecondaryCommit; flankLead=existing_AltDevelop; flankSupport=existing_TacticalOpportunity_sidecar; directContainment=existing_HeldAccess; secondaryCommit=existing_SplitFlank_DirectCommit; minimumSquadMembersForTwoAxis=2; flankSupportOptional=true; minimumSquadMembersForSupport=3; minimumDirectMembersWithSupport=1; sameAxisProvenPathRequired=true; supportSelectionBeforeLead=pending_readonly; exactLeadManeuverRequiredAtSupportAcquisition=true; activeLeadPhysicalLeasePreferred=true; physicalLeaseGapContinuity=true; pendingStatePersistent=false; roomSemanticModel=false; fireCommand=false; aimCommand=false; targetMutation=false; sainMicroCombatPreserved=true");
        }
        if (now >= nextSummaryAtUtc)
        {
            nextSummaryAtUtc = now + SummaryInterval;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_MULTI_AXIS_ASSAULT_SUMMARY groupsObserved={groupsObserved}; supportSelected={supportSelected}; supportPendingLead={supportPendingLead}; supportEligible={supportEligible}; containObserved={containObserved}; alternateEntryObserved={alternateEntryObserved}; pressureShiftObserved={pressureShiftObserved}; secondaryCommitObserved={secondaryCommitObserved}; groupsObservedTotal={groupsObservedTotal}; supportSelectedTotal={supportSelectedTotal}; supportPendingLeadTotal={supportPendingLeadTotal}; supportEligibleTotal={supportEligibleTotal}; supportStartAttemptsTotal={supportStartAttemptsTotal}; supportStartsTotal={supportStartsTotal}; supportRejectsTotal={supportRejectsTotal}; supportRejectReasons={Safe(SupportRejectionSummary())}; containObservedTotal={containObservedTotal}; alternateEntryObservedTotal={alternateEntryObservedTotal}; pressureShiftObservedTotal={pressureShiftObservedTotal}; secondaryCommitObservedTotal={secondaryCommitObservedTotal}; movementBackend=existing_tactical_opportunity_sidecar; rejectionTelemetryOnly=false; flankSupportAdmissionChanged=true; flankSupportAdmission=current_exact_altdevelop_maneuver_epoch; preLeadSelection=pending_not_attempted; fireCommand=false; aimCommand=false; targetMutation=false");
        }
    }

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            LastPhaseSignatureByGroup.Clear();
            SupportRejectionsByReason.Clear();
        }
        nextAcquisitionAtUtc = DateTimeOffset.MinValue;
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        groupsObservedTotal = 0;
        supportSelectedTotal = 0;
        supportPendingLeadTotal = 0;
        supportEligibleTotal = 0;
        supportStartAttemptsTotal = 0;
        supportStartsTotal = 0;
        supportRejectsTotal = 0;
        containObservedTotal = 0;
        alternateEntryObservedTotal = 0;
        pressureShiftObservedTotal = 0;
        secondaryCommitObservedTotal = 0;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_MULTI_AXIS_ASSAULT_RESET reason={Safe(reason)}; stateCleared=true; opportunityLeaseOwnership=false; movementStart=false; fireCommand=false; targetMutation=false");
    }

    private static void RecordSupportRejection(string reason)
    {
        string bucket = SupportRejectionBucket(reason);
        lock (Sync)
        {
            supportRejectsTotal++;
            if (!SupportRejectionsByReason.ContainsKey(bucket)
                && SupportRejectionsByReason.Count >= MaxTrackedSupportRejectionReasons)
            {
                bucket = "other";
            }
            SupportRejectionsByReason[bucket] = SupportRejectionsByReason.TryGetValue(bucket, out int count)
                ? count + 1
                : 1;
        }
    }

    private static string SupportRejectionSummary()
    {
        lock (Sync)
        {
            if (SupportRejectionsByReason.Count == 0)
            {
                return "none";
            }
            return string.Join(",", SupportRejectionsByReason
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Key + "=" + item.Value));
        }
    }

    private static string SupportRejectionBucket(string reason)
    {
        string normalized = Normalize(reason);
        if (normalized.StartsWith("flank_support_start_deferred:", StringComparison.Ordinal))
        {
            normalized = normalized.Substring("flank_support_start_deferred:".Length);
        }
        if (normalized.StartsWith("flank_support_execution_safety:", StringComparison.Ordinal))
        {
            string detail = normalized.Substring("flank_support_execution_safety:".Length);
            int nestedSeparator = detail.IndexOf(':');
            if (nestedSeparator > 0)
            {
                detail = detail.Substring(0, nestedSeparator);
            }
            return "flank_support_execution_safety_" + (string.IsNullOrWhiteSpace(detail) ? "unknown" : detail);
        }
        int detailSeparator = normalized.IndexOf(':');
        if (detailSeparator > 0)
        {
            normalized = normalized.Substring(0, detailSeparator);
        }
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static bool TryResolveSupportCandidate(ThreatGroupRouteAssignment assignment, out AltRouteFeasibilityObservation supportCandidate)
    {
        supportCandidate = assignment.AltCandidateFeasibility.FirstOrDefault(item =>
            string.Equals(item.AxisCandidateId, assignment.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.BotProfileId, assignment.SelectedAltSupportBotProfileId, StringComparison.OrdinalIgnoreCase)
            && item.IsFeasiblePair
            && item.CandidateSpecificRouteProven)!;
        return supportCandidate != null;
    }

    private static bool IsSupportNearAlternateEntry(ThreatGroupRouteAssignment assignment, DateTimeOffset now)
    {
        if (!assignment.HasShadowAltSupportSelection
            || !VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(assignment.SelectedAltSupportBotProfileId, out OperatorDecisionSnapshot support)
            || support == null
            || !support.Alive
            || !VanguardOperatorDecisionSnapshotService.IsFresh(support, now, out _)
            || !string.Equals(support.OwnerProfileId, assignment.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(support.Threat.EnemyId, assignment.ThreatId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return HorizontalDistance(support.Position, assignment.AlternateEstablishmentRegionCenter)
            <= assignment.AlternateEstablishmentRegionRadiusMeters + 6f;
    }

    private static void EmitPhaseIfChanged(
        ThreatGroupRouteAssignment assignment,
        ThreatGroupSplitFlankStateObservation? state,
        AltDevelopEvidence leadEvidence,
        bool supportNearAlternateEntry,
        bool pressureShift,
        string phase)
    {
        string key = Normalize(assignment.OwnerProfileId) + "|" + Normalize(assignment.ThreatId);
        string signature = string.Join("|",
            phase,
            assignment.AlternateCandidateId,
            assignment.SelectedAltBotProfileId,
            assignment.SelectedAltSupportBotProfileId,
            supportNearAlternateEntry,
            state?.PositionEstablishedObserved ?? false,
            state?.PressureProofObserved ?? false,
            state?.SynchronizedCommitReady ?? false,
            state?.DirectCommitCycleCompletedObserved ?? false);
        lock (Sync)
        {
            if (LastPhaseSignatureByGroup.TryGetValue(key, out string existing)
                && string.Equals(existing, signature, StringComparison.Ordinal))
            {
                return;
            }
            LastPhaseSignatureByGroup[key] = signature;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_MULTI_AXIS_ASSAULT_STATE owner={Safe(assignment.OwnerProfileId)}; threat={Safe(assignment.ThreatId)}; phase={Safe(phase)}; axis={Safe(assignment.AlternateCandidateId)}; flankLead={Safe(assignment.SelectedAltBotProfileId)}; flankSupport={Safe(assignment.SelectedAltSupportBotProfileId)}; directMembers={assignment.DirectBotProfileIds.Count}; leadDevelopmentState={Safe(leadEvidence.State)}; leadProgress={Safe(leadEvidence.PhysicalProgressKind)}; supportNearAlternateEntry={supportNearAlternateEntry}; positionEstablished={state?.PositionEstablishedObserved ?? false}; pressureProof={state?.PressureProofObserved ?? false}; pressureShift={pressureShift}; pressureReason={Safe(state?.PressureProofReason)}; synchronizedApproach={state?.SynchronizedApproachCommitReady ?? false}; synchronizedCommit={state?.SynchronizedCommitReady ?? false}; directCommitCycleCompleted={state?.DirectCommitCycleCompletedObserved ?? false}; directHoldsOwnedHere=false; holdReleaseOwnedHere=false; fireCommand=false; aimCommand=false; targetMutation=false");
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        Vector3 delta = a - b;
        delta.y = 0f;
        return delta.magnitude;
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
