#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;

// Responsibility: performs the calibration-sequence shadow-selection step over the exact assignment-aligned threat-exposure publication without influencing gameplay.
// Flow: one current assignment-aligned publication -> exact coherent choke evidence -> three provisional sensitivity probes -> stability keyed to the exact selected Alt member/axis plus Direct composition.
// Authority boundary: Shadow observation only. No profile is doctrine and no candidate may change route choice, contribution assignment, TacticalReplan, locomotion, target state, SAIN or BigBrain.
// Invariant: stale/mismatched alignment, expired threat contact or a changed assignment identity immediately invalidates shadow stability; KillLane remains undefined/deferred.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class ThreatExposureShadowClassificationService
{
    public const string StatusTag = "VANGUARD_TC_THREAT_EXPOSURE_SHADOW_STATUS";

    // These thresholds intentionally form a sensitivity sweep. They are NOT frozen tactical policy.
    // The same provisional sensitivity probes are evaluated over assignment-aligned evidence so profile results
    // reflect the exact selected Direct/Alt assignment geometry rather than differences in calibration thresholds.
    private static readonly CalibrationProfile[] Profiles =
    {
        new("strict_probe", 0.75f, 0.90f, 0.75f, 0.25f, 0.50f),
        new("balanced_probe", 0.55f, 0.75f, 0.50f, 0.35f, 0.25f),
        new("sensitive_probe", 0.35f, 0.50f, 0.50f, 0.50f, 0.15f),
    };

    // Reuse the already-proven TC planning stability convention as a calibration gate, not as final exposure doctrine.
    private static readonly TimeSpan MinimumStableDuration = TimeSpan.FromSeconds(0.75d);
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(15.0d);
    private const int MinimumStableSamples = 3;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, ProfileState> StateByKey = new(StringComparer.OrdinalIgnoreCase);
    private static ThreatExposureShadowSnapshot current = ThreatExposureShadowSnapshot.Empty;
    private static DateTimeOffset lastAlignmentPublishedAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    private static int publicationsProcessedTotal;
    private static int groupsEvaluatedTotal;
    private static int directStableEntriesTotal;
    private static int heldAccessStableEntriesTotal;
    private static int splitStableEntriesTotal;
    private static int stableExitsTotal;
    private static int currentnessRejectTicksTotal;
    private static int expiredContactRejectsTotal;
    private static int assignmentIdentityGapExitsTotal;

    public static void Tick(DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            ClearPublishedAndTransientState(clearPublicationCursor: true);
            return;
        }

        if (!ThreatExposureAssignmentAlignmentService.TryGetSnapshot(out ThreatExposureAssignmentAlignmentSnapshot alignment))
        {
            InvalidateCurrentEvidence("assignment_alignment_unavailable", clearPublicationCursor: true);
            return;
        }

        if (!TryValidateAlignmentCurrent(alignment, out string currentnessReason))
        {
            currentnessRejectTicksTotal++;
            InvalidateCurrentEvidence("assignment_alignment_not_current:" + currentnessReason, clearPublicationCursor: false);
            return;
        }

        if (alignment.PublishedAtUtc == lastAlignmentPublishedAtUtc)
        {
            return;
        }

        lastAlignmentPublishedAtUtc = alignment.PublishedAtUtc;
        publicationsProcessedTotal++;

        bool chokeCoherent = TryGetCoherentChokeSnapshot(alignment, out ChokeDetectionSnapshot chokeSnapshot);
        var chokeByThreat = chokeCoherent
            ? chokeSnapshot.Chokes
                .GroupBy(item => OwnerThreatKey(item.OwnerProfileId, item.ThreatId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ChokeObservation[]>(StringComparer.OrdinalIgnoreCase);

        var seenStateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<ThreatExposureShadowGroupObservation>();

        foreach (ThreatExposureAssignmentAlignmentGroup group in alignment.ThreatGroups)
        {
            if (group.ContactExpiresAtUtc <= now)
            {
                expiredContactRejectsTotal++;
                continue;
            }

            if (group.DirectBotProfileIds == null
                || group.DirectBotProfileIds.Count == 0
                || group.MatchedDirectRouteCount != group.DirectBotProfileIds.Count
                || group.SelectedAltRouteExposure.SampleCount <= 0)
            {
                continue;
            }

            groupsEvaluatedTotal++;
            float directMean = group.DirectFacingWeightedLosMean;
            float directMax = group.DirectFacingWeightedLosMax;
            float directForwardMean = group.DirectForwardVisibleMean;
            float altMean = group.SelectedAltRouteExposure.FacingWeightedLosEvidenceMean;
            float delta = group.DirectVsSelectedAltFacingWeightedDelta;

            string threatKey = OwnerThreatKey(group.OwnerProfileId, group.ThreatId);
            ChokeObservation[] chokes = Array.Empty<ChokeObservation>();
            bool chokePresent = false;
            if (chokeCoherent
                && chokeByThreat.TryGetValue(threatKey, out ChokeObservation[]? matchedChokes)
                && matchedChokes != null
                && matchedChokes.Length > 0)
            {
                chokes = matchedChokes;
                chokePresent = true;
            }

            bool confirmedHardChokePresent = chokePresent && chokes.Any(item => item.IsHardChoke && item.IsConfirmed);
            string assignmentIdentity = AssignmentIdentity(group);

            var profileObservations = new List<ThreatExposureShadowProfileObservation>(Profiles.Length);
            foreach (CalibrationProfile profile in Profiles)
            {
                bool directCandidate = directMean >= profile.DirectMeanFloor
                    && directMax >= profile.DirectMaxFloor
                    && directForwardMean >= profile.DirectForwardVisibleFloor;
                bool altReliefCandidate = altMean <= profile.AltMeanCeiling
                    && delta >= profile.DirectVsAltDeltaFloor;
                bool heldAccessCandidate = directCandidate && chokePresent;
                bool splitOpportunityCandidate = directCandidate && altReliefCandidate;

                string stateKey = ProfileKey(assignmentIdentity, profile.Id);
                seenStateKeys.Add(stateKey);
                if (!StateByKey.TryGetValue(stateKey, out ProfileState? state))
                {
                    state = new ProfileState(
                        group.OwnerProfileId,
                        group.ThreatId,
                        group.AlternateCandidateId,
                        group.SelectedAltBotProfileId,
                        DirectCompositionToken(group.DirectBotProfileIds),
                        assignmentIdentity,
                        profile.Id);
                    StateByKey[stateKey] = state;
                }

                UpdateCandidateRun(
                    state.DirectExposure,
                    directCandidate,
                    now,
                    group,
                    profile,
                    "direct_exposure",
                    directMean,
                    directMax,
                    directForwardMean,
                    altMean,
                    delta,
                    chokeCoherent,
                    chokePresent,
                    confirmedHardChokePresent);
                UpdateCandidateRun(
                    state.HeldAccess,
                    heldAccessCandidate,
                    now,
                    group,
                    profile,
                    "held_access_candidate",
                    directMean,
                    directMax,
                    directForwardMean,
                    altMean,
                    delta,
                    chokeCoherent,
                    chokePresent,
                    confirmedHardChokePresent);
                UpdateCandidateRun(
                    state.SplitOpportunity,
                    splitOpportunityCandidate,
                    now,
                    group,
                    profile,
                    "split_opportunity_candidate",
                    directMean,
                    directMax,
                    directForwardMean,
                    altMean,
                    delta,
                    chokeCoherent,
                    chokePresent,
                    confirmedHardChokePresent);

                profileObservations.Add(new ThreatExposureShadowProfileObservation(
                    profile.Id,
                    directCandidate,
                    altReliefCandidate,
                    heldAccessCandidate,
                    splitOpportunityCandidate,
                    state.DirectExposure.ConsecutiveSamples,
                    state.DirectExposure.StableSeconds(now),
                    state.DirectExposure.StableQualified,
                    state.HeldAccess.ConsecutiveSamples,
                    state.HeldAccess.StableSeconds(now),
                    state.HeldAccess.StableQualified,
                    state.SplitOpportunity.ConsecutiveSamples,
                    state.SplitOpportunity.StableSeconds(now),
                    state.SplitOpportunity.StableQualified));
            }

            groups.Add(new ThreatExposureShadowGroupObservation(
                group.OwnerProfileId,
                group.ThreatId,
                group.AlternateCandidateId,
                group.SelectedAltBotProfileId,
                group.SelectedAltOperatorId,
                group.DirectBotProfileIds.ToArray(),
                group.ContactExpiresAtUtc,
                group.SelectedPairSourceEvidenceCapturedAtUtc,
                directMean,
                directMax,
                directForwardMean,
                altMean,
                delta,
                chokeCoherent,
                chokePresent,
                confirmedHardChokePresent,
                assignmentIdentity,
                profileObservations.ToArray()));
        }

        DropAbsentStates(seenStateKeys, now);

        var next = new ThreatExposureShadowSnapshot(
            alignment.MapId,
            alignment.TopologyRevision,
            alignment.TopologyContentHash,
            alignment.OperatorBatchAtUtc,
            alignment.ConvergencePublishedAtUtc,
            alignment.ChokePublishedAtUtc,
            alignment.ExposurePublishedAtUtc,
            alignment.RouteAssignmentPublishedAtUtc,
            alignment.PublishedAtUtc,
            now,
            chokeCoherent,
            groups.ToArray());
        lock (Sync)
        {
            current = next;
        }

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"TC_THREAT_EXPOSURE_SHADOW_BOOT enabled=true; mode=assignment_aligned_calibration_probe_sweep; source=exact_selected_assignment; profiles=strict_probe,balanced_probe,sensitive_probe; probeOnly=true; thresholdsNotDoctrine=true; minimumStableSamples={MinimumStableSamples}; minimumStableSeconds={MinimumStableDuration.TotalSeconds:0.00}; stabilityIdentity=owner_threat_selected_axis_selected_alt_bot_direct_composition; currentAlignmentRequired=true; expiredContactFailOpen=true; directExposureClassification=shadow_candidate_only; heldAccessClassification=shadow_candidate_only_requires_coherent_choke; splitOpportunityClassification=shadow_candidate_only_requires_selected_alt_relief; killLaneClassification=false; executionAllowed=false; routeMutation=false; assignmentMutation=false; replanMutation=false; movementStart=false; targetMutation=false; sainMutation=false");
        }

        if (groups.Count > 0 && now >= nextSummaryAtUtc)
        {
            nextSummaryAtUtc = now + SummaryInterval;
            EmitSummary(groups, chokeCoherent);
        }
    }

    public static bool TryGetSnapshot(out ThreatExposureShadowSnapshot snapshot)
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
            current = ThreatExposureShadowSnapshot.Empty;
        }

        StateByKey.Clear();
        lastAlignmentPublishedAtUtc = DateTimeOffset.MinValue;
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        publicationsProcessedTotal = 0;
        groupsEvaluatedTotal = 0;
        directStableEntriesTotal = 0;
        heldAccessStableEntriesTotal = 0;
        splitStableEntriesTotal = 0;
        stableExitsTotal = 0;
        currentnessRejectTicksTotal = 0;
        expiredContactRejectsTotal = 0;
        assignmentIdentityGapExitsTotal = 0;

        if (!string.IsNullOrWhiteSpace(reason))
        {
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"TC_THREAT_EXPOSURE_SHADOW_RESET reason={Safe(reason)}; source=assignment_aligned; shadowStateCleared=true; probeOnly=true; thresholdsNotDoctrine=true; killLaneClassification=false; executionAllowed=false; routeMutation=false; assignmentMutation=false; replanMutation=false; movementStart=false; targetMutation=false; sainMutation=false");
        }
    }

    private static bool TryValidateAlignmentCurrent(
        ThreatExposureAssignmentAlignmentSnapshot alignment,
        out string reason)
    {
        reason = "current";
        if (!ThreatExposureObservationService.TryGetSnapshot(out ThreatExposureObservationSnapshot exposure))
        {
            reason = "raw_exposure_unavailable";
            return false;
        }
        if (!RouteIndependenceService.TryGetSnapshot(out RouteIndependenceSnapshot independence))
        {
            reason = "route_independence_unavailable";
            return false;
        }
        if (!RouteAssignmentService.TryGetSnapshot(out RouteAssignmentSnapshot assignment))
        {
            reason = "route_assignment_unavailable";
            return false;
        }

        if (alignment.TopologyRevision != exposure.TopologyRevision
            || alignment.TopologyRevision != independence.TopologyRevision
            || alignment.TopologyRevision != assignment.TopologyRevision
            || !string.Equals(alignment.MapId, exposure.MapId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(alignment.MapId, independence.MapId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(alignment.MapId, assignment.MapId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(alignment.TopologyContentHash, exposure.TopologyContentHash, StringComparison.Ordinal)
            || !string.Equals(alignment.TopologyContentHash, independence.TopologyContentHash, StringComparison.Ordinal)
            || !string.Equals(alignment.TopologyContentHash, assignment.TopologyContentHash, StringComparison.Ordinal)
            || alignment.OperatorBatchAtUtc != exposure.OperatorBatchAtUtc
            || alignment.OperatorBatchAtUtc != independence.OperatorBatchAtUtc
            || alignment.OperatorBatchAtUtc != assignment.OperatorBatchAtUtc)
        {
            reason = "topology_or_operator_batch_mismatch";
            return false;
        }

        if (alignment.ExposurePublishedAtUtc != exposure.PublishedAtUtc)
        {
            reason = "raw_exposure_publication_advanced";
            return false;
        }
        if (alignment.IndependencePublishedAtUtc != independence.PublishedAtUtc)
        {
            reason = "route_independence_publication_advanced";
            return false;
        }
        if (alignment.RouteAssignmentPublishedAtUtc != assignment.PublishedAtUtc)
        {
            reason = "route_assignment_publication_advanced";
            return false;
        }
        if (alignment.ConvergencePublishedAtUtc != exposure.ConvergencePublishedAtUtc
            || alignment.ConvergencePublishedAtUtc != independence.ConvergencePublishedAtUtc
            || alignment.ChokePublishedAtUtc != independence.ChokePublishedAtUtc
            || assignment.RoutePublishedAtUtc != alignment.IndependencePublishedAtUtc)
        {
            reason = "upstream_provenance_mismatch";
            return false;
        }

        return true;
    }

    private static bool TryGetCoherentChokeSnapshot(
        ThreatExposureAssignmentAlignmentSnapshot alignment,
        out ChokeDetectionSnapshot choke)
    {
        choke = ChokeDetectionSnapshot.Empty;
        if (!ChokeDetectionService.TryGetSnapshot(out choke))
        {
            return false;
        }

        return choke.TopologyRevision == alignment.TopologyRevision
            && string.Equals(choke.MapId, alignment.MapId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(choke.TopologyContentHash, alignment.TopologyContentHash, StringComparison.Ordinal)
            && choke.OperatorBatchAtUtc == alignment.OperatorBatchAtUtc
            && choke.ConvergencePublishedAtUtc == alignment.ConvergencePublishedAtUtc
            && choke.PublishedAtUtc == alignment.ChokePublishedAtUtc;
    }

    private static void UpdateCandidateRun(
        CandidateRunState state,
        bool matches,
        DateTimeOffset now,
        ThreatExposureAssignmentAlignmentGroup group,
        CalibrationProfile profile,
        string candidateKind,
        float directMean,
        float directMax,
        float directForwardMean,
        float altMean,
        float delta,
        bool chokeCoherent,
        bool chokePresent,
        bool confirmedHardChokePresent)
    {
        bool wasStable = state.StableQualified;
        if (!matches)
        {
            if (wasStable)
            {
                stableExitsTotal++;
                EmitTransition(
                    "EXIT",
                    candidateKind,
                    group,
                    profile,
                    state,
                    now,
                    directMean,
                    directMax,
                    directForwardMean,
                    altMean,
                    delta,
                    chokeCoherent,
                    chokePresent,
                    confirmedHardChokePresent,
                    "current_assignment_aligned_evidence_no_longer_matches_probe");
            }

            state.Reset();
            return;
        }

        if (state.ConsecutiveSamples == 0)
        {
            state.FirstMatchAtUtc = now;
        }

        state.ConsecutiveSamples++;
        state.LastMatchAtUtc = now;
        state.MaxConsecutiveSamples = Math.Max(state.MaxConsecutiveSamples, state.ConsecutiveSamples);

        bool stableNow = state.ConsecutiveSamples >= MinimumStableSamples
            && now - state.FirstMatchAtUtc >= MinimumStableDuration;
        state.StableQualified = stableNow;
        if (!wasStable && stableNow)
        {
            if (string.Equals(candidateKind, "direct_exposure", StringComparison.Ordinal))
            {
                directStableEntriesTotal++;
            }
            else if (string.Equals(candidateKind, "held_access_candidate", StringComparison.Ordinal))
            {
                heldAccessStableEntriesTotal++;
            }
            else if (string.Equals(candidateKind, "split_opportunity_candidate", StringComparison.Ordinal))
            {
                splitStableEntriesTotal++;
            }

            EmitTransition(
                "ENTER",
                candidateKind,
                group,
                profile,
                state,
                now,
                directMean,
                directMax,
                directForwardMean,
                altMean,
                delta,
                chokeCoherent,
                chokePresent,
                confirmedHardChokePresent,
                "assignment_aligned_probe_match_stable");
        }
    }

    private static void DropAbsentStates(HashSet<string> seenStateKeys, DateTimeOffset now)
    {
        if (StateByKey.Count == 0)
        {
            return;
        }

        foreach (string key in StateByKey.Keys.Where(key => !seenStateKeys.Contains(key)).ToArray())
        {
            ProfileState state = StateByKey[key];
            int stableCount = CountStable(state);
            stableExitsTotal += stableCount;
            if (stableCount > 0)
            {
                assignmentIdentityGapExitsTotal += stableCount;
                VanguardClientDiagnosticsLog.Info(
                    StatusTag,
                    $"TC_THREAT_EXPOSURE_SHADOW_GAP_EXIT owner={Safe(state.OwnerProfileId)}; threat={Safe(state.ThreatId)}; axis={Safe(state.AlternateCandidateId)}; selectedAltBot={Safe(state.SelectedAltBotProfileId)}; directComposition={Safe(state.DirectComposition)}; profile={Safe(state.ProfileId)}; directStable={Bool(state.DirectExposure.StableQualified)}; heldAccessStable={Bool(state.HeldAccess.StableQualified)}; splitStable={Bool(state.SplitOpportunity.StableQualified)}; reason=assignment_identity_changed_contact_expired_or_group_absent; staleStateRetained=false; safetyClaim=false; executionAllowed=false");
            }

            StateByKey.Remove(key);
        }
    }

    private static int CountStable(ProfileState state)
        => (state.DirectExposure.StableQualified ? 1 : 0)
            + (state.HeldAccess.StableQualified ? 1 : 0)
            + (state.SplitOpportunity.StableQualified ? 1 : 0);

    private static void EmitTransition(
        string transition,
        string candidateKind,
        ThreatExposureAssignmentAlignmentGroup group,
        CalibrationProfile profile,
        CandidateRunState state,
        DateTimeOffset now,
        float directMean,
        float directMax,
        float directForwardMean,
        float altMean,
        float delta,
        bool chokeCoherent,
        bool chokePresent,
        bool confirmedHardChokePresent,
        string reason)
    {
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"TC_THREAT_EXPOSURE_SHADOW_TRANSITION transition={Safe(transition)}; candidate={Safe(candidateKind)}; profile={Safe(profile.Id)}; owner={Safe(group.OwnerProfileId)}; threat={Safe(group.ThreatId)}; axis={Safe(group.AlternateCandidateId)}; selectedAltBot={Safe(group.SelectedAltBotProfileId)}; selectedAltOperator={Safe(group.SelectedAltOperatorId)}; directBots={Safe(string.Join(",", group.DirectBotProfileIds))}; stableSamples={state.ConsecutiveSamples}; stableSeconds={state.StableSeconds(now):0.00}; directMean={Number(directMean)}; directMax={Number(directMax)}; directForwardVisibleMean={Number(directForwardMean)}; selectedAltMean={Number(altMean)}; directVsSelectedAltDelta={Number(delta)}; contactExpiresAt={group.ContactExpiresAtUtc:O}; selectedPairEvidenceAt={group.SelectedPairSourceEvidenceCapturedAtUtc:O}; chokeEvidenceCoherent={Bool(chokeCoherent)}; chokePresent={Bool(chokePresent)}; confirmedHardChoke={Bool(confirmedHardChokePresent)}; reason={Safe(reason)}; source=assignment_aligned; probeOnly=true; thresholdsNotDoctrine=true; killLaneClassification=false; executionAllowed=false; routeMutation=false; assignmentMutation=false; replanMutation=false; movementStart=false; targetMutation=false; sainMutation=false");
    }

    private static void EmitSummary(
        IReadOnlyList<ThreatExposureShadowGroupObservation> groups,
        bool chokeCoherent)
    {
        foreach (CalibrationProfile profile in Profiles)
        {
            int directCurrent = groups.Count(group => FindProfile(group, profile.Id)?.DirectExposureCandidate == true);
            int heldCurrent = groups.Count(group => FindProfile(group, profile.Id)?.HeldAccessCandidate == true);
            int splitCurrent = groups.Count(group => FindProfile(group, profile.Id)?.SplitOpportunityCandidate == true);
            int directStable = groups.Count(group => FindProfile(group, profile.Id)?.DirectStableQualified == true);
            int heldStable = groups.Count(group => FindProfile(group, profile.Id)?.HeldAccessStableQualified == true);
            int splitStable = groups.Count(group => FindProfile(group, profile.Id)?.SplitStableQualified == true);

            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"TC_THREAT_EXPOSURE_SHADOW_SUMMARY profile={Safe(profile.Id)}; source=assignment_aligned; groups={groups.Count}; directCurrent={directCurrent}; heldAccessCurrent={heldCurrent}; splitOpportunityCurrent={splitCurrent}; directStable={directStable}; heldAccessStable={heldStable}; splitOpportunityStable={splitStable}; chokeEvidenceCoherent={Bool(chokeCoherent)}; publicationsProcessedTotal={publicationsProcessedTotal}; groupsEvaluatedTotal={groupsEvaluatedTotal}; directStableEntriesTotal={directStableEntriesTotal}; heldAccessStableEntriesTotal={heldAccessStableEntriesTotal}; splitStableEntriesTotal={splitStableEntriesTotal}; stableExitsTotal={stableExitsTotal}; assignmentIdentityGapExitsTotal={assignmentIdentityGapExitsTotal}; currentnessRejectTicksTotal={currentnessRejectTicksTotal}; expiredContactRejectsTotal={expiredContactRejectsTotal}; probeOnly=true; thresholdsNotDoctrine=true; killLaneClassification=false; executionAllowed=false");
        }
    }

    private static ThreatExposureShadowProfileObservation? FindProfile(
        ThreatExposureShadowGroupObservation group,
        string profileId)
        => group.Profiles.FirstOrDefault(item => string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));

    private static void InvalidateCurrentEvidence(string reason, bool clearPublicationCursor)
    {
        bool hadState = StateByKey.Count > 0;
        if (hadState)
        {
            foreach (ProfileState state in StateByKey.Values)
            {
                int stableCount = CountStable(state);
                stableExitsTotal += stableCount;
                if (stableCount > 0)
                {
                    VanguardClientDiagnosticsLog.Info(
                        StatusTag,
                        $"TC_THREAT_EXPOSURE_SHADOW_STALE_EXIT owner={Safe(state.OwnerProfileId)}; threat={Safe(state.ThreatId)}; axis={Safe(state.AlternateCandidateId)}; selectedAltBot={Safe(state.SelectedAltBotProfileId)}; directComposition={Safe(state.DirectComposition)}; profile={Safe(state.ProfileId)}; directStable={Bool(state.DirectExposure.StableQualified)}; heldAccessStable={Bool(state.HeldAccess.StableQualified)}; splitStable={Bool(state.SplitOpportunity.StableQualified)}; reason={Safe(reason)}; staleStateRetained=false; safetyClaim=false; executionAllowed=false");
                }
            }
        }

        lock (Sync)
        {
            current = ThreatExposureShadowSnapshot.Empty;
        }
        StateByKey.Clear();
        if (clearPublicationCursor)
        {
            lastAlignmentPublishedAtUtc = DateTimeOffset.MinValue;
        }
    }

    private static void ClearPublishedAndTransientState(bool clearPublicationCursor)
    {
        lock (Sync)
        {
            current = ThreatExposureShadowSnapshot.Empty;
        }

        StateByKey.Clear();
        if (clearPublicationCursor)
        {
            lastAlignmentPublishedAtUtc = DateTimeOffset.MinValue;
        }
    }

    private static string AssignmentIdentity(ThreatExposureAssignmentAlignmentGroup group)
        => string.Join("|",
            Normalize(group.OwnerProfileId),
            Normalize(group.ThreatId),
            Normalize(group.AlternateCandidateId),
            Normalize(group.SelectedAltBotProfileId),
            Normalize(group.SelectedAltOperatorId),
            DirectCompositionToken(group.DirectBotProfileIds));

    private static string DirectCompositionToken(IReadOnlyList<string> directBotProfileIds)
        => string.Join(",",
            directBotProfileIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

    private static string OwnerThreatKey(string? ownerProfileId, string? threatId)
        => Normalize(ownerProfileId) + "|" + Normalize(threatId);

    private static string ProfileKey(string assignmentIdentity, string profileId)
        => assignmentIdentity + "|" + Normalize(profileId);

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().ToLowerInvariant();

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private static string Number(float value)
        => float.IsNaN(value) || float.IsInfinity(value)
            ? "unknown"
            : value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";

    private sealed class CalibrationProfile
    {
        public CalibrationProfile(
            string id,
            float directMeanFloor,
            float directMaxFloor,
            float directForwardVisibleFloor,
            float altMeanCeiling,
            float directVsAltDeltaFloor)
        {
            Id = id;
            DirectMeanFloor = directMeanFloor;
            DirectMaxFloor = directMaxFloor;
            DirectForwardVisibleFloor = directForwardVisibleFloor;
            AltMeanCeiling = altMeanCeiling;
            DirectVsAltDeltaFloor = directVsAltDeltaFloor;
        }

        public string Id { get; }
        public float DirectMeanFloor { get; }
        public float DirectMaxFloor { get; }
        public float DirectForwardVisibleFloor { get; }
        public float AltMeanCeiling { get; }
        public float DirectVsAltDeltaFloor { get; }
    }

    private sealed class ProfileState
    {
        public ProfileState(
            string ownerProfileId,
            string threatId,
            string alternateCandidateId,
            string selectedAltBotProfileId,
            string directComposition,
            string assignmentIdentity,
            string profileId)
        {
            OwnerProfileId = ownerProfileId;
            ThreatId = threatId;
            AlternateCandidateId = alternateCandidateId;
            SelectedAltBotProfileId = selectedAltBotProfileId;
            DirectComposition = directComposition;
            AssignmentIdentity = assignmentIdentity;
            ProfileId = profileId;
        }

        public string OwnerProfileId { get; }
        public string ThreatId { get; }
        public string AlternateCandidateId { get; }
        public string SelectedAltBotProfileId { get; }
        public string DirectComposition { get; }
        public string AssignmentIdentity { get; }
        public string ProfileId { get; }
        public CandidateRunState DirectExposure { get; } = new();
        public CandidateRunState HeldAccess { get; } = new();
        public CandidateRunState SplitOpportunity { get; } = new();
        public bool AnyStable => DirectExposure.StableQualified
            || HeldAccess.StableQualified
            || SplitOpportunity.StableQualified;
    }

    private sealed class CandidateRunState
    {
        public DateTimeOffset FirstMatchAtUtc { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastMatchAtUtc { get; set; } = DateTimeOffset.MinValue;
        public int ConsecutiveSamples { get; set; }
        public int MaxConsecutiveSamples { get; set; }
        public bool StableQualified { get; set; }

        public double StableSeconds(DateTimeOffset now)
            => ConsecutiveSamples <= 0 || FirstMatchAtUtc == DateTimeOffset.MinValue
                ? 0d
                : Math.Max(0d, (now - FirstMatchAtUtc).TotalSeconds);

        public void Reset()
        {
            FirstMatchAtUtc = DateTimeOffset.MinValue;
            LastMatchAtUtc = DateTimeOffset.MinValue;
            ConsecutiveSamples = 0;
            StableQualified = false;
        }
    }
}
#endif
