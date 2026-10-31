#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;

// Responsibility: closes the semantic observation gap between "a relieved Alt exists" and "the exact Alt Operator/path selected by RouteAssignment is relieved".
// Flow: coherent qualified threat exposure + RouteIndependence + RouteAssignment publications are joined; Direct exposure is restricted to the assignment's Direct members and the exact candidate-specific selected Alt path is sampled with the existing LOS/facing primitive.
// Authority boundary: Observation/alignment only. This service never classifies HeldAccess/KillLane, changes assignment, requests replan, starts/stops movement, opens scheduler windows, mutates targets or mutates SAIN.
// Invariant: selected-Alt relief can only be published from the exact (AlternateCandidateId, SelectedAltBotProfileId) feasibility record and its proven current path corners.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class ThreatExposureAssignmentAlignmentService
{
    public const string StatusTag = "VANGUARD_TC_THREAT_EXPOSURE_ASSIGNMENT_ALIGNMENT_STATUS";

    private static readonly object Sync = new();
    private static readonly TimeSpan OptionalMaxDeferral = TimeSpan.FromSeconds(1.0d);
    private static readonly TimeSpan SummaryLogInterval = TimeSpan.FromSeconds(10.0d);

    private static ThreatExposureAssignmentAlignmentSnapshot current = ThreatExposureAssignmentAlignmentSnapshot.Empty;
    private static string publishedInputIdentity = string.Empty;
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;
    private static int publicationsTotal;
    private static int alignedGroupsTotal;
    private static int selectedAxisMatchedRawAltTotal;
    private static int selectedAxisDifferentFromRawAltTotal;
    private static int selectedPairPathUnavailableTotal;
    private static int directMemberRouteMismatchTotal;
    private static int budgetDeferralsTotal;
    private static int linecastsTotal;

    public static void Tick(DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            ClearPublished();
            return;
        }

        if (!TryGetCoherentInputs(
                out ThreatExposureObservationSnapshot exposure,
                out RouteIndependenceSnapshot independence,
                out RouteAssignmentSnapshot assignment))
        {
            ClearPublished();
            return;
        }

        string inputIdentity = InputIdentity(exposure, independence, assignment);
        if (string.Equals(inputIdentity, publishedInputIdentity, StringComparison.Ordinal))
        {
            return;
        }

        if (!VanguardRuntimeFrameBudgetGuard.ShouldRunOptional(
                "ThreatExposureAssignmentAlignment",
                now,
                OptionalMaxDeferral,
                out string optionalBudgetReason))
        {
            return;
        }

        var exposureByThreat = exposure.ThreatGroups
            .GroupBy(item => OwnerThreatKey(item.OwnerProfileId, item.ThreatId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var aligned = new List<ThreatExposureAssignmentAlignmentGroup>();
        int selectedAxisMatchedRawAlt = 0;
        int selectedAxisDifferentFromRawAlt = 0;
        int selectedPairPathUnavailable = 0;
        int directMemberRouteMismatch = 0;
        int linecasts = 0;

        foreach (ThreatGroupRouteAssignment group in assignment.ThreatGroups)
        {
            if (!group.HasShadowAltSelection
                || !group.CandidateMatrixComplete
                || !group.SelectedPairPathProven
                || !group.SelectedAltPlanningViable)
            {
                continue;
            }

            string key = OwnerThreatKey(group.OwnerProfileId, group.ThreatId);
            if (!exposureByThreat.TryGetValue(key, out ThreatGroupExposureObservation? raw)
                || raw.ContactExpiresAtUtc <= now)
            {
                continue;
            }

            AltRouteFeasibilityObservation? selectedPair = group.AltCandidateFeasibility
                .Where(item => item != null
                    && string.Equals(item.AxisCandidateId, group.AlternateCandidateId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.BotProfileId, group.SelectedAltBotProfileId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.SourceEvidenceCapturedAtUtc)
                .FirstOrDefault();

            if (selectedPair == null
                || !selectedPair.CandidateSpecificRouteProven
                || selectedPair.PathCorners == null
                || selectedPair.PathCorners.Count < 2)
            {
                selectedPairPathUnavailable++;
                continue;
            }

            var directMemberIds = new HashSet<string>(
                group.DirectBotProfileIds.Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            RouteExposureObservation[] directRoutes = raw.DirectRoutes
                .Where(route => directMemberIds.Contains(route.BotProfileId))
                .ToArray();
            if (directMemberIds.Count == 0 || directRoutes.Length != directMemberIds.Count)
            {
                directMemberRouteMismatch++;
                continue;
            }

            if (!ThreatExposureObservationService.TryObserveExistingRoute(
                    group.OwnerProfileId,
                    "selected_alt_assignment_path",
                    group.AlternateCandidateId,
                    group.SelectedAltBotProfileId,
                    selectedPair.PathCorners,
                    raw.RuntimeThreatPosition,
                    raw.RuntimeThreatLookDirection,
                    ref linecasts,
                    out RouteExposureObservation? selectedAltExposure))
            {
                budgetDeferralsTotal++;
                return;
            }

            if (selectedAltExposure == null)
            {
                selectedPairPathUnavailable++;
                continue;
            }

            RouteExposureObservation? rawAlt = raw.AlternateAxes?.FirstOrDefault();
            string rawAltId = rawAlt?.RouteId ?? string.Empty;
            bool rawAltMatchesSelected = !string.IsNullOrWhiteSpace(rawAltId)
                && string.Equals(rawAltId, group.AlternateCandidateId, StringComparison.OrdinalIgnoreCase);
            if (rawAltMatchesSelected)
            {
                selectedAxisMatchedRawAlt++;
            }
            else if (!string.IsNullOrWhiteSpace(rawAltId))
            {
                selectedAxisDifferentFromRawAlt++;
            }

            float directMean = directRoutes.Average(route => route.FacingWeightedLosEvidenceMean);
            float directMax = directRoutes.Max(route => route.FacingWeightedLosEvidenceMean);
            float directForwardMean = directRoutes.Average(route => route.ForwardVisibleSampleRatio);
            float selectedAltMean = selectedAltExposure.FacingWeightedLosEvidenceMean;

            aligned.Add(new ThreatExposureAssignmentAlignmentGroup(
                group.OwnerProfileId,
                group.ThreatId,
                group.AlternateCandidateId,
                group.SelectedAltBotProfileId,
                group.SelectedAltOperatorId,
                group.Role,
                group.Specialty,
                raw.ContactObservedAtUtc,
                raw.ContactExpiresAtUtc,
                selectedPair.SourceEvidenceCapturedAtUtc,
                selectedPair.CacheHit,
                selectedPair.PathLengthMeters,
                rawAltId,
                rawAltMatchesSelected,
                rawAlt?.FacingWeightedLosEvidenceMean ?? float.NaN,
                directMemberIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                directRoutes.Length,
                directMean,
                directMax,
                directForwardMean,
                selectedAltExposure,
                directMean - selectedAltMean));
        }

        if (!TryGetCoherentInputs(
                out ThreatExposureObservationSnapshot latestExposure,
                out RouteIndependenceSnapshot latestIndependence,
                out RouteAssignmentSnapshot latestAssignment)
            || !string.Equals(inputIdentity, InputIdentity(latestExposure, latestIndependence, latestAssignment), StringComparison.Ordinal))
        {
            return;
        }

        var next = new ThreatExposureAssignmentAlignmentSnapshot(
            exposure.MapId,
            exposure.TopologyRevision,
            exposure.TopologyContentHash,
            exposure.OperatorBatchAtUtc,
            exposure.ConvergencePublishedAtUtc,
            independence.ChokePublishedAtUtc,
            independence.PublishedAtUtc,
            exposure.PublishedAtUtc,
            assignment.PublishedAtUtc,
            now,
            aligned.ToArray());

        lock (Sync)
        {
            current = next;
        }
        publishedInputIdentity = inputIdentity;
        publicationsTotal++;
        alignedGroupsTotal += aligned.Count;
        selectedAxisMatchedRawAltTotal += selectedAxisMatchedRawAlt;
        selectedAxisDifferentFromRawAltTotal += selectedAxisDifferentFromRawAlt;
        selectedPairPathUnavailableTotal += selectedPairPathUnavailable;
        directMemberRouteMismatchTotal += directMemberRouteMismatch;
        linecastsTotal += linecasts;

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                "TC_THREAT_EXPOSURE_ASSIGNMENT_ALIGNMENT_BOOT enabled=true; observationOnly=true; exactSelectedPair=true; directScope=route_assignment_direct_members_only; selectedAltScope=candidate_specific_proven_path; selectedAltSource=route_assignment_feasibility_path_corners; sharedLosFacingSampler=true; additionalPathfinding=false; heldAccessClassification=false; killLaneClassification=false; assignmentMutation=false; replanMutation=false; movementStart=false; sainMutation=false; executionAllowed=false");
        }

        if (aligned.Count > 0 && now >= nextSummaryAtUtc)
        {
            nextSummaryAtUtc = now + SummaryLogInterval;
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"TC_THREAT_EXPOSURE_ASSIGNMENT_ALIGNMENT_SUMMARY map={Safe(exposure.MapId)}; topologyRevision={exposure.TopologyRevision}; groups={aligned.Count}; selectedAxisMatchedRawAlt={selectedAxisMatchedRawAlt}; selectedAxisDifferentFromRawAlt={selectedAxisDifferentFromRawAlt}; selectedPairPathUnavailable={selectedPairPathUnavailable}; directMemberRouteMismatch={directMemberRouteMismatch}; linecasts={linecasts}; optionalBudget={Safe(optionalBudgetReason)}; publicationsTotal={publicationsTotal}; alignedGroupsTotal={alignedGroupsTotal}; matchedRawAltTotal={selectedAxisMatchedRawAltTotal}; differentRawAltTotal={selectedAxisDifferentFromRawAltTotal}; pathUnavailableTotal={selectedPairPathUnavailableTotal}; directMismatchTotal={directMemberRouteMismatchTotal}; budgetDeferralsTotal={budgetDeferralsTotal}; linecastsTotal={linecastsTotal}; observationOnly=true; executionAllowed=false");
            EmitDetails(aligned);
        }
    }

    public static bool TryGetSnapshot(out ThreatExposureAssignmentAlignmentSnapshot snapshot)
    {
        lock (Sync)
        {
            snapshot = current;
            return snapshot.IsAvailable;
        }
    }

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            current = ThreatExposureAssignmentAlignmentSnapshot.Empty;
        }
        publishedInputIdentity = string.Empty;
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        publicationsTotal = 0;
        alignedGroupsTotal = 0;
        selectedAxisMatchedRawAltTotal = 0;
        selectedAxisDifferentFromRawAltTotal = 0;
        selectedPairPathUnavailableTotal = 0;
        directMemberRouteMismatchTotal = 0;
        budgetDeferralsTotal = 0;
        linecastsTotal = 0;

        if (!string.IsNullOrWhiteSpace(reason))
        {
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"TC_THREAT_EXPOSURE_ASSIGNMENT_ALIGNMENT_RESET reason={Safe(reason)}; publication=cleared; observationOnly=true; heldAccessClassification=false; assignmentMutation=false; replanMutation=false; movementStart=false; sainMutation=false; executionAllowed=false");
        }
    }

    private static bool TryGetCoherentInputs(
        out ThreatExposureObservationSnapshot exposure,
        out RouteIndependenceSnapshot independence,
        out RouteAssignmentSnapshot assignment)
    {
        exposure = ThreatExposureObservationSnapshot.Empty;
        independence = RouteIndependenceSnapshot.Empty;
        assignment = RouteAssignmentSnapshot.Empty;

        if (!ThreatExposureObservationService.TryGetSnapshot(out exposure)
            || !RouteIndependenceService.TryGetSnapshot(out independence)
            || !RouteAssignmentService.TryGetSnapshot(out assignment))
        {
            return false;
        }

        return exposure.TopologyRevision == independence.TopologyRevision
            && exposure.TopologyRevision == assignment.TopologyRevision
            && string.Equals(exposure.MapId, independence.MapId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(exposure.MapId, assignment.MapId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(exposure.TopologyContentHash, independence.TopologyContentHash, StringComparison.Ordinal)
            && string.Equals(exposure.TopologyContentHash, assignment.TopologyContentHash, StringComparison.Ordinal)
            && exposure.OperatorBatchAtUtc == independence.OperatorBatchAtUtc
            && exposure.OperatorBatchAtUtc == assignment.OperatorBatchAtUtc
            && exposure.IndependencePublishedAtUtc == independence.PublishedAtUtc
            && assignment.RoutePublishedAtUtc == independence.PublishedAtUtc
            && exposure.ConvergencePublishedAtUtc == independence.ConvergencePublishedAtUtc;
    }

    private static string InputIdentity(
        ThreatExposureObservationSnapshot exposure,
        RouteIndependenceSnapshot independence,
        RouteAssignmentSnapshot assignment)
        => string.Join("|",
            exposure.MapId,
            exposure.TopologyRevision.ToString(CultureInfo.InvariantCulture),
            exposure.TopologyContentHash,
            exposure.OperatorBatchAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            independence.PublishedAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            exposure.PublishedAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            assignment.PublishedAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture));

    private static void EmitDetails(IReadOnlyList<ThreatExposureAssignmentAlignmentGroup> groups)
    {
        foreach (ThreatExposureAssignmentAlignmentGroup group in groups)
        {
            RouteExposureObservation alt = group.SelectedAltRouteExposure;
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"TC_THREAT_EXPOSURE_ASSIGNMENT_ALIGNMENT_GROUP owner={Safe(group.OwnerProfileId)}; threat={Safe(group.ThreatId)}; axis={Safe(group.AlternateCandidateId)}; selectedAltBot={Safe(group.SelectedAltBotProfileId)}; selectedAltOperator={Safe(group.SelectedAltOperatorId)}; role={Safe(group.Role)}; specialty={Safe(group.Specialty)}; directMembers={group.DirectMemberCount}; matchedDirectRoutes={group.MatchedDirectRouteCount}; directMean={Number(group.DirectFacingWeightedLosMean)}; directMax={Number(group.DirectFacingWeightedLosMax)}; directForwardVisibleMean={Number(group.DirectForwardVisibleMean)}; selectedAltMean={Number(alt.FacingWeightedLosEvidenceMean)}; selectedAltMax={Number(alt.FacingWeightedLosEvidenceMax)}; selectedAltForwardVisible={Number(alt.ForwardVisibleSampleRatio)}; directVsSelectedAltDelta={Number(group.DirectVsSelectedAltFacingWeightedDelta)}; selectedPathSamples={alt.SampleCount}; selectedPathLinecasts={alt.LinecastCount}; selectedPathLength={Number(group.SelectedPairPathLengthMeters)}; selectedPathCacheHit={group.SelectedPairPathCacheHit}; selectedPairEvidenceAt={group.SelectedPairSourceEvidenceCapturedAtUtc:O}; contactObservedAt={group.ContactObservedAtUtc:O}; contactExpiresAt={group.ContactExpiresAtUtc:O}; directBots={Safe(string.Join(",", group.DirectBotProfileIds))}; rawObservedAltAxis={Safe(group.RawObservedAltAxisId)}; rawObservedAltMatchesSelectedAxis={group.RawObservedAltMatchesSelectedAxis}; rawObservedAltMean={Number(group.RawObservedAltFacingWeightedLosMean)}; classification=assignment_aligned_raw_only; heldAccess=false; killLane=false; executionAllowed=false; assignmentMutation=false; replanMutation=false; movementStart=false; sainMutation=false");
        }
    }

    private static string OwnerThreatKey(string? ownerProfileId, string? threatId)
        => SafeKey(ownerProfileId) + "|" + SafeKey(threatId);

    private static string SafeKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private static string Number(float value)
        => float.IsNaN(value)
            ? "nan"
            : value.ToString("0.00", CultureInfo.InvariantCulture);

    private static void ClearPublished()
    {
        lock (Sync)
        {
            current = ThreatExposureAssignmentAlignmentSnapshot.Empty;
        }
        publishedInputIdentity = string.Empty;
    }
}
#endif
