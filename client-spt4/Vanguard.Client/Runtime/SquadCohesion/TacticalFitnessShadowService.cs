#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.PostLoot;

// Responsibility: Observes contribution-specific tactical-fitness evidence for the current alternate-route candidate matrix without changing RouteAssignment or any physical executor.
// Flow: Each current feasible member/axis pair is joined with exact runtime identity, fresh decision/medical evidence, live inventory/readiness observations and the already-proven candidate-specific geometry; role-first and shortest-path references are then compared for calibration.
// Authority boundary: Shadow observer only. It performs no pathfinding, assignment mutation, hysteresis replacement, intent/lease acquisition, movement, SAIN tuning, target/aim/fire mutation, weapon switching or persistence.
// Invariant: Material geometry remains explicitly unresolved when role and geometry references disagree; raw capability facts are retained and no numeric tactical threshold or equipment-fit class is invented to force a shadow winner.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class TacticalFitnessShadowService
{
    public const string StatusTag = "VANGUARD_TACTICAL_FITNESS_SHADOW_STATUS";

    private static readonly TimeSpan ObservationInterval = TimeSpan.FromSeconds(1.0d);
    private static readonly TimeSpan OptionalMaxDeferral = TimeSpan.FromSeconds(1.5d);
    private static readonly TimeSpan SummaryLogInterval = TimeSpan.FromSeconds(15.0d);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, ulong> LastDetailFingerprintByThreatGroup = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ulong> LastVolatileEvidenceFingerprintByThreatGroup = new(StringComparer.OrdinalIgnoreCase);

    private static TacticalFitnessShadowSnapshot current = TacticalFitnessShadowSnapshot.Empty;
    private static DateTimeOffset nextObservationAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    private static int publicationsTotal;
    private static int groupsObservedTotal;
    private static int candidatePairsObservedTotal;
    private static int directCapabilityEvidenceTotal;
    private static int partialCapabilityEvidenceTotal;
    private static int unknownCapabilityEvidenceTotal;
    private static int roleGeometryAlignedTotal;
    private static int roleGeometryConflictTotal;
    private static int referenceEligibleCandidatesTotal;
    private static int referenceEvidenceUnprovenCandidatesTotal;
    private static int referenceRuntimeMissingCandidatesTotal;
    private static int groupsWithoutReferenceEligibleCandidateTotal;
    private static int budgetDeferralsTotal;
    private static int detailFormattingSkipsTotal;
    private static int volatileEvidencePublicationsTotal;

    private const double PerformanceProfileSlowThresholdMilliseconds = 20.0d;
    private static readonly TimeSpan PerformanceProfileWindow = TimeSpan.FromSeconds(15.0d);
    private static readonly TacticalFitnessPerformanceAggregate PerformanceAggregate = new();

    public static void Tick(DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            ResetInternal();
            return;
        }

        if (now < nextObservationAtUtc)
        {
            return;
        }
        nextObservationAtUtc = now + ObservationInterval;

        if (!RouteAssignmentService.TryGetSnapshot(out RouteAssignmentSnapshot assignmentSnapshot)
            || !assignmentSnapshot.IsAvailable)
        {
            InvalidatePublished();
            return;
        }

        if (!VanguardRuntimeFrameBudgetGuard.ShouldRunOptional(
                "TacticalFitnessShadow",
                now,
                OptionalMaxDeferral))
        {
            budgetDeferralsTotal++;
            return;
        }

        long performanceProfileStarted = VanguardRuntimePerformanceGuard.Begin();
        var performanceProfile = new TacticalFitnessPerformancePass(performanceProfileStarted);

        var capabilityByBotProfileId = new Dictionary<string, CandidateCapabilityRead>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<TacticalFitnessThreatGroupObservation>();
        int candidatePairsObserved = 0;
        int directCapabilityEvidence = 0;
        int partialCapabilityEvidence = 0;
        int unknownCapabilityEvidence = 0;
        int roleGeometryAligned = 0;
        int roleGeometryConflict = 0;
        int referenceEligibleCandidates = 0;
        int referenceEvidenceUnprovenCandidates = 0;
        int referenceRuntimeMissingCandidates = 0;
        int groupsWithoutReferenceEligibleCandidate = 0;
        int candidatesWithMedicalDamage = 0;
        int candidatesWithoutPrimaryWeapon = 0;
        int candidatesWithoutObservedCompatibleReserve = 0;
        int candidatesWithWeaponNotReady = 0;

        foreach (ThreatGroupRouteAssignment assignment in assignmentSnapshot.ThreatGroups
            .OrderBy(item => item.OwnerProfileId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ThreatId, StringComparer.OrdinalIgnoreCase))
        {
            long matrixStarted = ProfileStart(performanceProfile);
            AltRouteFeasibilityObservation[] feasiblePairs = assignment.AltCandidateFeasibility
                .Where(item => item != null && item.IsFeasiblePair)
                .OrderBy(item => item.AxisCandidateId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ProfileAddElapsed(ref performanceProfile.MatrixTicks, matrixStarted);

            var candidateObservations = new List<TacticalFitnessCandidateObservation>(feasiblePairs.Length);
            foreach (AltRouteFeasibilityObservation pair in feasiblePairs)
            {
                CandidateCapabilityRead capability;
                if (!capabilityByBotProfileId.TryGetValue(pair.BotProfileId, out CandidateCapabilityRead? cachedCapability)
                    || cachedCapability == null)
                {
                    capability = ReadCapability(pair.BotProfileId, now, ref performanceProfile);
                    capabilityByBotProfileId[pair.BotProfileId] = capability;
                }
                else
                {
                    capability = cachedCapability;
                }

                long candidateProjectionStarted = ProfileStart(performanceProfile);
                TacticalFitnessCandidateObservation observed = BuildCandidateObservation(pair, capability, assignment.ThreatId);
                candidateObservations.Add(observed);
                candidatePairsObserved++;

                switch (observed.EvidenceKind)
                {
                    case "DIRECT_OBSERVED": directCapabilityEvidence++; break;
                    case "PARTIAL": partialCapabilityEvidence++; break;
                    default: unknownCapabilityEvidence++; break;
                }

                if (observed.MedicalEvidenceReadable
                    && (observed.HasHeavyBleed
                        || observed.HasLightBleed
                        || observed.HasFracture
                        || observed.HasPain
                        || observed.HasDestroyedPart
                        || observed.HasUntreatableVitalDamage))
                {
                    candidatesWithMedicalDamage++;
                }
                if (observed.EquipmentEvidenceObserved && !observed.HasPrimaryWeapon)
                {
                    candidatesWithoutPrimaryWeapon++;
                }
                if (observed.EquipmentEvidenceObserved && observed.CompatibleAmmunitionCount == 0)
                {
                    candidatesWithoutObservedCompatibleReserve++;
                }
                if (observed.WeaponReadinessObserved && !observed.WeaponReady)
                {
                    candidatesWithWeaponNotReady++;
                }
                ProfileAddElapsed(ref performanceProfile.CandidateProjectionTicks, candidateProjectionStarted);
            }

            long referenceResolutionStarted = ProfileStart(performanceProfile);
            TacticalFitnessCandidateObservation[] candidates = candidateObservations.ToArray();
            TacticalFitnessCandidateObservation[] referenceCandidates = candidates
                .Where(IsReferenceEligible)
                .ToArray();
            referenceEligibleCandidates += referenceCandidates.Length;
            referenceEvidenceUnprovenCandidates += candidates.Length - referenceCandidates.Length;
            referenceRuntimeMissingCandidates += candidates.Count(item => !item.RuntimePresent);
            if (candidates.Length > 0 && referenceCandidates.Length == 0)
            {
                groupsWithoutReferenceEligibleCandidate++;
            }

            TacticalFitnessCandidateObservation? currentCandidate = candidates.FirstOrDefault(item =>
                SamePair(item.AxisCandidateId, item.BotProfileId, assignment.AlternateCandidateId, assignment.SelectedAltBotProfileId));
            bool currentReferenceEligible = currentCandidate != null && IsReferenceEligible(currentCandidate);

            TacticalFitnessCandidateObservation? roleReference = referenceCandidates
                .OrderByDescending(item => item.RolePreferenceRank)
                .ThenBy(item => item.PathLengthMeters)
                .ThenByDescending(item => item.AxisEvidenceScore)
                .ThenBy(item => item.AxisCandidateId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            TacticalFitnessCandidateObservation? geometryReference = referenceCandidates
                .OrderBy(item => item.PathLengthMeters)
                .ThenBy(item => item.PathRatio)
                .ThenByDescending(item => item.AxisEvidenceScore)
                .ThenByDescending(item => item.RolePreferenceRank)
                .ThenBy(item => item.AxisCandidateId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            bool referencesAgree = roleReference != null
                && geometryReference != null
                && SamePair(roleReference.AxisCandidateId, roleReference.BotProfileId, geometryReference.AxisCandidateId, geometryReference.BotProfileId);
            if (referencesAgree)
            {
                roleGeometryAligned++;
            }
            else if (roleReference != null && geometryReference != null)
            {
                roleGeometryConflict++;
            }

            string resolution;
            string materialityState;
            if (candidates.Length == 0)
            {
                resolution = "no_route_feasible_candidate";
                materialityState = "not_applicable";
            }
            else if (referenceCandidates.Length == 0)
            {
                resolution = "reference_eligibility_unproven";
                materialityState = "not_applicable_viability_unproven";
            }
            else if (!referencesAgree)
            {
                resolution = "role_geometry_conflict_materiality_unresolved";
                materialityState = "unresolved_no_threshold";
            }
            else if (roleReference != null && string.Equals(roleReference.EvidenceKind, "DIRECT_OBSERVED", StringComparison.Ordinal))
            {
                resolution = "role_and_geometry_reference_aligned_capability_observed";
                materialityState = "not_needed_references_aligned";
            }
            else
            {
                resolution = "role_and_geometry_reference_aligned_capability_partial";
                materialityState = "not_needed_references_aligned";
            }

            var group = new TacticalFitnessThreatGroupObservation
            {
                OwnerProfileId = assignment.OwnerProfileId,
                ThreatId = assignment.ThreatId,
                CurrentAxisCandidateId = assignment.AlternateCandidateId,
                CurrentBotProfileId = assignment.SelectedAltBotProfileId,
                CurrentRolePreferenceBand = assignment.RolePreferenceBand,
                RoleReferenceAxisCandidateId = roleReference?.AxisCandidateId ?? string.Empty,
                RoleReferenceBotProfileId = roleReference?.BotProfileId ?? string.Empty,
                GeometryReferenceAxisCandidateId = geometryReference?.AxisCandidateId ?? string.Empty,
                GeometryReferenceBotProfileId = geometryReference?.BotProfileId ?? string.Empty,
                AlignedReferenceAxisCandidateId = referencesAgree ? roleReference?.AxisCandidateId ?? string.Empty : string.Empty,
                AlignedReferenceBotProfileId = referencesAgree ? roleReference?.BotProfileId ?? string.Empty : string.Empty,
                RoleAndGeometryReferencesAgree = referencesAgree,
                CurrentReferenceEligible = currentReferenceEligible,
                ReferenceEligibleCandidateCount = referenceCandidates.Length,
                ReferenceEvidenceUnprovenCandidateCount = candidates.Length - referenceCandidates.Length,
                CurrentMatchesRoleReference = currentReferenceEligible && roleReference != null
                    && SamePair(currentCandidate!.AxisCandidateId, currentCandidate.BotProfileId, roleReference.AxisCandidateId, roleReference.BotProfileId),
                CurrentMatchesGeometryReference = currentReferenceEligible && geometryReference != null
                    && SamePair(currentCandidate!.AxisCandidateId, currentCandidate.BotProfileId, geometryReference.AxisCandidateId, geometryReference.BotProfileId),
                CurrentMinusGeometryPathMeters = currentReferenceEligible && geometryReference != null
                    ? currentCandidate!.PathLengthMeters - geometryReference.PathLengthMeters
                    : 0f,
                CurrentMinusGeometryPathRatio = currentReferenceEligible && geometryReference != null
                    ? currentCandidate!.PathRatio - geometryReference.PathRatio
                    : 0f,
                CurrentMinusGeometryAxisEvidence = currentReferenceEligible && geometryReference != null
                    ? currentCandidate!.AxisEvidenceScore - geometryReference.AxisEvidenceScore
                    : 0f,
                CurrentMinusGeometryRoleRank = currentReferenceEligible && geometryReference != null
                    ? currentCandidate!.RolePreferenceRank - geometryReference.RolePreferenceRank
                    : 0,
                MaterialityState = materialityState,
                Resolution = resolution,
                Candidates = candidates
            };

            ProfileAddElapsed(ref performanceProfile.ReferenceResolutionTicks, referenceResolutionStarted);

            groups.Add(group);
            long detailPresentationStarted = ProfileStart(performanceProfile);
            EmitDetailIfChanged(group);
            ProfileAddElapsed(ref performanceProfile.DetailPresentationTicks, detailPresentationStarted);
        }

        long publishStarted = ProfileStart(performanceProfile);
        var next = new TacticalFitnessShadowSnapshot
        {
            MapId = assignmentSnapshot.MapId,
            TopologyRevision = assignmentSnapshot.TopologyRevision,
            TopologyContentHash = assignmentSnapshot.TopologyContentHash,
            RouteAssignmentPublishedAtUtc = assignmentSnapshot.PublishedAtUtc,
            PublishedAtUtc = now,
            ThreatGroups = groups.ToArray()
        };
        lock (Sync)
        {
            current = next;
        }

        CompetitiveCandidateEnvelopeShadowService.PublishFromTacticalFitness(next, now);
        ProfileAddElapsed(ref performanceProfile.PublishTicks, publishStarted);

        publicationsTotal++;
        groupsObservedTotal += groups.Count;
        candidatePairsObservedTotal += candidatePairsObserved;
        directCapabilityEvidenceTotal += directCapabilityEvidence;
        partialCapabilityEvidenceTotal += partialCapabilityEvidence;
        unknownCapabilityEvidenceTotal += unknownCapabilityEvidence;
        roleGeometryAlignedTotal += roleGeometryAligned;
        roleGeometryConflictTotal += roleGeometryConflict;
        referenceEligibleCandidatesTotal += referenceEligibleCandidates;
        referenceEvidenceUnprovenCandidatesTotal += referenceEvidenceUnprovenCandidates;
        referenceRuntimeMissingCandidatesTotal += referenceRuntimeMissingCandidates;
        groupsWithoutReferenceEligibleCandidateTotal += groupsWithoutReferenceEligibleCandidate;

        long summaryPresentationStarted = ProfileStart(performanceProfile);
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_TACTICAL_FITNESS_SHADOW_BOOT enabled=true; contribution=FlankLead; observationOnly=true; routeAssignmentMutation=false; executableSelection=false; movementAuthority=false; intentAuthority=false; leaseAuthority=false; targetMutation=false; aimMutation=false; fireMutation=false; weaponSwitchAuthority=false; pathfinding=false; candidatePathSource=route_assignment_candidate_specific_proof; rolePreferenceSource=route_assignment_flank_lead_matrix; currentStateSource=decision_snapshot_medical_plus_single_pass_live_inventory_per_weapon_equipment_and_weapon_readiness; perWeaponEquipmentRawEvidence=true; canonicalWeaponFamilyNormalization=true; mountedSightZoomProvenance=true; highOpticThresholdClassification=false; eftEffectiveDistanceProvenance=true; eftEffectiveDistanceExecutableCeiling=false; sainEngagementDistanceConsumedByTc=false; availableWeaponAutoSwitchAssumed=false; candidateThreatDistanceProvenance=true; nativeEftWeaponDistanceAnchorsProvenance=true; nativeWeaponSwitchWindowRelation=true; threatRangeBandClassification=false; threatDistanceAffectsAdmission=false; nativeWeaponDistanceRelationAffectsAdmission=false; lootTotalScoreConsumedByTc=false; ammunitionThresholdClassification=false; behavioralBiasApplied=false; equipmentFitClassification=false; materialGeometryThresholds=false; detailFormatting=semantic_fingerprint_before_candidate_token; volatileEvidenceFormatting=compact_when_semantic_unchanged; calibrationPolicy=compare_role_reference_vs_shortest_path_reference_inside_evidence_proven_candidate_envelope_and_preserve_raw_candidates; unknownMandatoryPrerequisite=viability_unproven; build={VanguardBuildVersion.BuildLabel}");
        }

        if (now >= nextSummaryAtUtc)
        {
            nextSummaryAtUtc = now + SummaryLogInterval;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_TACTICAL_FITNESS_SHADOW_SUMMARY map={Safe(next.MapId)}; topologyRevision={next.TopologyRevision}; sourceRouteAssignmentAt={Timestamp(next.RouteAssignmentPublishedAtUtc)}; groups={groups.Count}; candidatePairs={candidatePairsObserved}; directCapabilityEvidence={directCapabilityEvidence}; partialCapabilityEvidence={partialCapabilityEvidence}; unknownCapabilityEvidence={unknownCapabilityEvidence}; roleGeometryAligned={roleGeometryAligned}; roleGeometryConflict={roleGeometryConflict}; referenceEligibleCandidates={referenceEligibleCandidates}; referenceEvidenceUnprovenCandidates={referenceEvidenceUnprovenCandidates}; referenceRuntimeMissingCandidates={referenceRuntimeMissingCandidates}; groupsWithoutReferenceEligibleCandidate={groupsWithoutReferenceEligibleCandidate}; candidatesWithMedicalDamage={candidatesWithMedicalDamage}; candidatesWithoutPrimaryWeapon={candidatesWithoutPrimaryWeapon}; candidatesWithoutObservedCompatibleReserve={candidatesWithoutObservedCompatibleReserve}; candidatesWithWeaponNotReady={candidatesWithWeaponNotReady}; publicationsTotal={publicationsTotal}; groupsObservedTotal={groupsObservedTotal}; candidatePairsObservedTotal={candidatePairsObservedTotal}; directCapabilityEvidenceTotal={directCapabilityEvidenceTotal}; partialCapabilityEvidenceTotal={partialCapabilityEvidenceTotal}; unknownCapabilityEvidenceTotal={unknownCapabilityEvidenceTotal}; roleGeometryAlignedTotal={roleGeometryAlignedTotal}; roleGeometryConflictTotal={roleGeometryConflictTotal}; referenceEligibleCandidatesTotal={referenceEligibleCandidatesTotal}; referenceEvidenceUnprovenCandidatesTotal={referenceEvidenceUnprovenCandidatesTotal}; referenceRuntimeMissingCandidatesTotal={referenceRuntimeMissingCandidatesTotal}; groupsWithoutReferenceEligibleCandidateTotal={groupsWithoutReferenceEligibleCandidateTotal}; budgetDeferralsTotal={budgetDeferralsTotal}; detailFormattingSkipsTotal={detailFormattingSkipsTotal}; volatileEvidencePublicationsTotal={volatileEvidencePublicationsTotal}; materialityThresholds=false; perWeaponEquipmentRawEvidence=true; lootTotalScoreConsumedByTc=false; ammunitionThresholdClassification=false; equipmentFitClassification=false; behavioralBiasApplied=false; assignmentMutation=false; pathfinding=false; observationOnly=true");
        }
        ProfileAddElapsed(ref performanceProfile.SummaryPresentationTicks, summaryPresentationStarted);
        RecordPerformanceProfile(now, ref performanceProfile, groups.Count, candidatePairsObserved);
    }

    public static bool TryGetSnapshot(out TacticalFitnessShadowSnapshot snapshot)
    {
        lock (Sync)
        {
            snapshot = current;
            return snapshot.IsAvailable;
        }
    }

    public static void Reset(string reason)
    {
        ResetInternal();
        if (!string.IsNullOrWhiteSpace(reason))
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_TACTICAL_FITNESS_SHADOW_RESET reason={Safe(reason)}; observationOnly=true; assignmentMutation=false; staleResume=false");
        }
    }

    private static TacticalFitnessCandidateObservation BuildCandidateObservation(
        AltRouteFeasibilityObservation pair,
        CandidateCapabilityRead capability,
        string expectedThreatId)
    {
        string threatDistanceEvidenceState = ResolveThreatDistanceEvidenceState(capability, expectedThreatId);
        bool threatDistanceAlignedToGroup = string.Equals(threatDistanceEvidenceState, "ALIGNED_DISTANCE_OBSERVED", StringComparison.Ordinal);
        string nativeWeaponDistanceAnchorState = capability.NativeWeaponDistanceAnchorsObserved
            ? "OBSERVED_NATIVE_EFT_SHOOT_SETTINGS"
            : "NATIVE_EFT_SHOOT_SETTINGS_UNAVAILABLE";
        string threatDistanceVsNativeWeaponWindowRelation = ResolveThreatDistanceVsNativeWeaponWindowRelation(
            capability,
            threatDistanceAlignedToGroup);

        return new TacticalFitnessCandidateObservation
        {
            AxisCandidateId = pair.AxisCandidateId,
            BotProfileId = pair.BotProfileId,
            OperatorId = string.IsNullOrWhiteSpace(capability.OperatorId) ? pair.OperatorId : capability.OperatorId,
            Role = string.IsNullOrWhiteSpace(capability.Role) ? pair.Role : capability.Role,
            Specialty = string.IsNullOrWhiteSpace(capability.Specialty) ? pair.Specialty : capability.Specialty,
            RolePreferenceBand = pair.RolePreferenceBand,
            RolePreferenceRank = pair.RolePreferenceRank,
            BasePersona = capability.BasePersona,
            Doctrine = capability.Doctrine,
            Temperament = capability.Temperament,
            IdentitySchemaVersion = capability.IdentitySchemaVersion,
            PlanningViable = pair.PlanningViable,
            CandidateSpecificPathProven = pair.CandidateSpecificRouteProven,
            AxisCandidateSource = pair.AxisCandidateSource,
            AxisSeedBotProfileId = pair.AxisSeedBotProfileId,
            IsAxisSeed = pair.IsAxisSeed,
            RouteEvidenceKind = pair.RouteEvidenceKind,
            EstablishmentRegionRadiusMeters = pair.EstablishmentRegionRadiusMeters,
            PathCornerCount = pair.PathCorners?.Count ?? 0,
            PathLengthMeters = pair.PathLengthMeters,
            DirectDistanceMeters = pair.DirectDistanceMeters,
            PathRatio = pair.PathRatio,
            AxisEvidenceScore = pair.AxisEvidenceScore,
            RuntimePresent = capability.RuntimePresent,
            DecisionSnapshotObserved = capability.DecisionSnapshotObserved,
            DecisionSnapshotFresh = capability.DecisionSnapshotFresh,
            DecisionFreshnessReason = capability.DecisionFreshnessReason,
            DecisionCapturedAtUtc = capability.DecisionCapturedAtUtc,
            ThreatEvidenceObserved = capability.DecisionSnapshotFresh && capability.DecisionThreatHasThreat,
            ThreatEvidenceEnemyId = capability.DecisionThreatEnemyId,
            ThreatDistanceAlignedToGroup = threatDistanceAlignedToGroup,
            ThreatDistanceMeters = threatDistanceAlignedToGroup ? capability.DecisionThreatDistanceMeters : -1f,
            ThreatDistanceEvidenceState = threatDistanceEvidenceState,
            NativeWeaponDistanceAnchorsObserved = capability.NativeWeaponDistanceAnchorsObserved,
            NativeWeaponSwitchLowDistanceMeters = capability.NativeWeaponDistanceAnchorsObserved ? capability.NativeWeaponSwitchLowDistanceMeters : -1f,
            NativeWeaponSwitchFarDistanceMeters = capability.NativeWeaponDistanceAnchorsObserved ? capability.NativeWeaponSwitchFarDistanceMeters : -1f,
            NativeWeaponDistanceAnchorState = nativeWeaponDistanceAnchorState,
            ThreatDistanceVsNativeWeaponWindowRelation = threatDistanceVsNativeWeaponWindowRelation,
            MedicalEvidenceReadable = capability.MedicalEvidenceReadable,
            HealthPercent = capability.HealthPercent,
            HasHeavyBleed = capability.HasHeavyBleed,
            HasLightBleed = capability.HasLightBleed,
            HasFracture = capability.HasFracture,
            HasPain = capability.HasPain,
            HasDestroyedPart = capability.HasDestroyedPart,
            HasUntreatableVitalDamage = capability.HasUntreatableVitalDamage,
            AnyMedicineUsing = capability.AnyMedicineUsing,
            MedicalClassification = capability.MedicalClassification,
            EquipmentEvidenceObserved = capability.EquipmentEvidenceObserved,
            HasPrimaryWeapon = capability.HasPrimaryWeapon,
            LongWeaponCount = capability.LongWeaponCount,
            CompatibleMagazineCount = capability.CompatibleMagazineCount,
            CompatibleAmmunitionCount = capability.CompatibleAmmunitionCount,
            PerWeaponEquipmentEvidenceObserved = capability.PerWeaponEquipmentEvidenceObserved,
            PerWeaponEquipmentObservationReason = capability.PerWeaponEquipmentObservationReason,
            CarriedWeaponCount = capability.CarriedWeaponCount,
            WeaponsWithRoundsCount = capability.WeaponsWithRoundsCount,
            WeaponsWithoutRoundsCount = capability.WeaponsWithoutRoundsCount,
            BrickedWeaponCount = capability.BrickedWeaponCount,
            SidearmObserved = capability.SidearmObserved,
            LiveWeaponCapabilities = capability.LiveWeaponCapabilities,
            WeaponReadinessObserved = capability.WeaponReadinessObserved,
            WeaponReadinessObservationReason = capability.WeaponReadinessObservationReason,
            CurrentWeaponKnown = capability.CurrentWeaponKnown,
            CurrentWeaponItemId = capability.CurrentWeaponItemId,
            CurrentWeaponTemplateId = capability.CurrentWeaponTemplateId,
            WeaponReady = capability.WeaponReady,
            ChangingWeapon = capability.ChangingWeapon,
            HandsSuspicious = capability.HandsSuspicious,
            EvidenceKind = capability.EvidenceKind,
            QualificationState = capability.QualificationState
        };
    }

    private static CandidateCapabilityRead ReadCapability(
        string botProfileId,
        DateTimeOffset now,
        ref TacticalFitnessPerformancePass performanceProfile)
    {
        long capabilityStarted = ProfileStart(performanceProfile);
        performanceProfile.CapabilityReads++;

        if (string.IsNullOrWhiteSpace(botProfileId)
            || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botProfileId, out var runtime)
            || !runtime.IsRuntimePresent
            || runtime.BotOwner == null)
        {
            ProfileAddElapsed(ref performanceProfile.CapabilityTicks, capabilityStarted);
            return CandidateCapabilityRead.Unknown("runtime_presence_missing");
        }

        bool decisionObserved = VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(botProfileId, out OperatorDecisionSnapshot decision)
            && decision != OperatorDecisionSnapshot.Empty;
        bool decisionFresh = false;
        string freshnessReason = "decision_snapshot_missing";
        if (decisionObserved)
        {
            decisionFresh = VanguardOperatorDecisionSnapshotService.IsFresh(decision, now, out freshnessReason);
        }

        VanguardLiveEquipmentCapabilitySnapshot liveEquipment = VanguardLiveEquipmentCapabilitySnapshot.Empty;
        bool equipmentObserved = false;
        bool perWeaponEquipmentObserved = false;
        long equipmentCaptureStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            // TacticalFitnessShadow is observation-only, so one typed equipment traversal provides both
            // aggregate carried-material facts and per-weapon facts. This avoids a duplicate
            // VanguardOperatorLootNeedReader inventory traversal without changing executable TC authority
            // or inventing any new equipment threshold/classification.
            liveEquipment = VanguardLiveEquipmentCapabilityReader.CaptureWithAggregateMaterial(runtime.BotOwner);
            equipmentObserved = liveEquipment.Observed;
            perWeaponEquipmentObserved = liveEquipment.Observed;
        }
        catch
        {
            liveEquipment = VanguardLiveEquipmentCapabilitySnapshot.Empty;
        }
        finally
        {
            ProfileAddElapsed(ref performanceProfile.EquipmentTicks, equipmentCaptureStarted);
            VanguardRuntimePerformanceGuard.End("TacticalFitnessEquipmentCapture", equipmentCaptureStarted);
        }

        VanguardPostLootWeaponReadinessSnapshot readiness = new();
        bool readinessObserved = false;
        long readinessCaptureStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            readiness = VanguardPostLootWeaponReadinessReader.Capture(runtime.BotOwner);
            readinessObserved = readiness.Observed;
        }
        catch
        {
            readiness = new VanguardPostLootWeaponReadinessSnapshot();
        }
        finally
        {
            ProfileAddElapsed(ref performanceProfile.ReadinessTicks, readinessCaptureStarted);
            VanguardRuntimePerformanceGuard.End("TacticalFitnessWeaponReadinessCapture", readinessCaptureStarted);
        }

        bool medicalReadable = decisionFresh && decision.Medical.Need.IsReadable;
        string evidenceKind = decisionFresh && medicalReadable && equipmentObserved && readinessObserved
            ? "DIRECT_OBSERVED"
            : (decisionObserved || equipmentObserved || readinessObserved ? "PARTIAL" : "UNKNOWN");

        string qualificationState = !decisionFresh
            ? "decision_evidence_unproven"
            : !medicalReadable
                ? "medical_evidence_unproven"
                : !equipmentObserved
                    ? "equipment_evidence_unproven"
                    : !readinessObserved
                        ? "weapon_readiness_evidence_unproven"
                        : "observable_candidate_ready_for_calibration";

        VanguardMedicalNeedSnapshot medical = decisionObserved ? decision.Medical.Need : VanguardMedicalNeedSnapshot.Empty;
        VanguardMedicalActionabilitySnapshot actionability = decisionObserved ? decision.Medical.Actionability : VanguardMedicalActionabilitySnapshot.Empty;
        float? decisionThreatDistance = decisionObserved ? decision.Threat.Distance : null;
        bool decisionThreatDistanceObserved = decisionFresh
            && decisionThreatDistance.HasValue
            && !float.IsNaN(decisionThreatDistance.Value)
            && !float.IsInfinity(decisionThreatDistance.Value)
            && decisionThreatDistance.Value >= 0f;

        bool nativeWeaponDistanceAnchorsObserved = false;
        float nativeWeaponSwitchLowDistanceMeters = -1f;
        float nativeWeaponSwitchFarDistanceMeters = -1f;
        try
        {
            float low = runtime.BotOwner.Settings.FileSettings.Shoot.LOW_DIST_TO_CHANGE_WEAPON;
            float far = runtime.BotOwner.Settings.FileSettings.Shoot.FAR_DIST_TO_CHANGE_WEAPON;
            if (!float.IsNaN(low) && !float.IsInfinity(low) && low >= 0f
                && !float.IsNaN(far) && !float.IsInfinity(far) && far > low)
            {
                nativeWeaponDistanceAnchorsObserved = true;
                nativeWeaponSwitchLowDistanceMeters = low;
                nativeWeaponSwitchFarDistanceMeters = far;
            }
        }
        catch
        {
            nativeWeaponDistanceAnchorsObserved = false;
        }

        var result = new CandidateCapabilityRead
        {
            RuntimePresent = true,
            OperatorId = runtime.OperatorId,
            Role = runtime.TacticalRole,
            Specialty = runtime.TacticalSpecialty,
            BasePersona = runtime.BasePersona,
            Doctrine = runtime.Doctrine,
            Temperament = runtime.Temperament,
            IdentitySchemaVersion = runtime.IdentitySchemaVersion,
            DecisionSnapshotObserved = decisionObserved,
            DecisionSnapshotFresh = decisionFresh,
            DecisionFreshnessReason = freshnessReason,
            DecisionCapturedAtUtc = decisionObserved ? decision.CapturedAtUtc : DateTimeOffset.MinValue,
            DecisionThreatHasThreat = decisionObserved && decision.Threat.HasThreat,
            DecisionThreatEnemyId = decisionObserved ? decision.Threat.EnemyId : "none",
            DecisionThreatDistanceObserved = decisionThreatDistanceObserved,
            DecisionThreatDistanceMeters = decisionThreatDistanceObserved ? decisionThreatDistance!.Value : -1f,
            NativeWeaponDistanceAnchorsObserved = nativeWeaponDistanceAnchorsObserved,
            NativeWeaponSwitchLowDistanceMeters = nativeWeaponSwitchLowDistanceMeters,
            NativeWeaponSwitchFarDistanceMeters = nativeWeaponSwitchFarDistanceMeters,
            MedicalEvidenceReadable = medicalReadable,
            HealthPercent = medicalReadable ? medical.HealthPercent : -1,
            HasHeavyBleed = medicalReadable && medical.HasHeavyBleed,
            HasLightBleed = medicalReadable && medical.HasLightBleed,
            HasFracture = medicalReadable && medical.HasFracture,
            HasPain = medicalReadable && medical.HasPain,
            HasDestroyedPart = medicalReadable && medical.HasDestroyedPart,
            HasUntreatableVitalDamage = medicalReadable && medical.HasUntreatableVitalDamage,
            AnyMedicineUsing = decisionObserved && actionability.AnyMedicineUsing,
            MedicalClassification = decisionObserved ? decision.Medical.Classification : "unknown",
            EquipmentEvidenceObserved = equipmentObserved,
            HasPrimaryWeapon = equipmentObserved && liveEquipment.LongWeaponCount > 0,
            LongWeaponCount = equipmentObserved ? liveEquipment.LongWeaponCount : -1,
            CompatibleMagazineCount = equipmentObserved ? liveEquipment.CompatibleMagazineCount : -1,
            CompatibleAmmunitionCount = equipmentObserved ? liveEquipment.CompatibleAmmunitionCount : -1,
            PerWeaponEquipmentEvidenceObserved = perWeaponEquipmentObserved,
            PerWeaponEquipmentObservationReason = liveEquipment.ObservationReason,
            CarriedWeaponCount = perWeaponEquipmentObserved ? liveEquipment.WeaponCount : -1,
            WeaponsWithRoundsCount = perWeaponEquipmentObserved ? liveEquipment.WeaponsWithRoundsCount : -1,
            WeaponsWithoutRoundsCount = perWeaponEquipmentObserved ? liveEquipment.WeaponsWithoutRoundsCount : -1,
            BrickedWeaponCount = perWeaponEquipmentObserved ? liveEquipment.BrickedWeaponCount : -1,
            SidearmObserved = perWeaponEquipmentObserved && liveEquipment.SidearmObserved,
            LiveWeaponCapabilities = perWeaponEquipmentObserved ? liveEquipment.Weapons : Array.Empty<VanguardLiveWeaponCapabilityObservation>(),
            WeaponReadinessObserved = readinessObserved,
            WeaponReadinessObservationReason = readiness.ObservationReason,
            CurrentWeaponKnown = readinessObserved && readiness.CurrentWeaponKnown,
            CurrentWeaponItemId = readinessObserved ? readiness.CurrentWeaponItemId : "none",
            CurrentWeaponTemplateId = readinessObserved ? readiness.CurrentWeaponTpl : "none",
            WeaponReady = readinessObserved && readiness.WeaponReady,
            ChangingWeapon = readinessObserved && readiness.ChangingWeapon,
            HandsSuspicious = readinessObserved && readiness.HandsSuspicious,
            EvidenceKind = evidenceKind,
            QualificationState = qualificationState
        };
        ProfileAddElapsed(ref performanceProfile.CapabilityTicks, capabilityStarted);
        return result;
    }

    private static string ResolveThreatDistanceEvidenceState(CandidateCapabilityRead capability, string expectedThreatId)
    {
        if (!capability.DecisionSnapshotFresh) return "DECISION_UNPROVEN";
        if (!capability.DecisionThreatHasThreat) return "NO_CURRENT_THREAT";
        if (string.IsNullOrWhiteSpace(expectedThreatId)) return "GROUP_THREAT_UNRESOLVED";
        if (string.IsNullOrWhiteSpace(capability.DecisionThreatEnemyId)
            || string.Equals(capability.DecisionThreatEnemyId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return "THREAT_ID_UNRESOLVED";
        }
        if (!string.Equals(capability.DecisionThreatEnemyId, expectedThreatId, StringComparison.OrdinalIgnoreCase))
        {
            return "GROUP_THREAT_MISMATCH";
        }
        if (!capability.DecisionThreatDistanceObserved) return "DISTANCE_UNAVAILABLE";
        return "ALIGNED_DISTANCE_OBSERVED";
    }

    private static string ResolveThreatDistanceVsNativeWeaponWindowRelation(
        CandidateCapabilityRead capability,
        bool threatDistanceAlignedToGroup)
    {
        if (!threatDistanceAlignedToGroup) return "THREAT_DISTANCE_UNALIGNED";
        if (!capability.NativeWeaponDistanceAnchorsObserved) return "NATIVE_EFT_SHOOT_SETTINGS_UNAVAILABLE";

        float distance = capability.DecisionThreatDistanceMeters;
        if (distance <= capability.NativeWeaponSwitchLowDistanceMeters) return "AT_OR_BELOW_NATIVE_LOW";
        if (distance >= capability.NativeWeaponSwitchFarDistanceMeters) return "AT_OR_ABOVE_NATIVE_FAR";
        return "WITHIN_NATIVE_CHANGE_WEAPON_WINDOW";
    }

    private static void EmitDetailIfChanged(TacticalFitnessThreatGroupObservation group)
    {
        string key = Normalize(group.OwnerProfileId) + "|" + Normalize(group.ThreatId);
        ulong fingerprint = ComputeDetailFingerprint(group);
        ulong volatileFingerprint = ComputeVolatileEvidenceFingerprint(group);
        if (LastDetailFingerprintByThreatGroup.TryGetValue(key, out ulong previous)
            && previous == fingerprint)
        {
            detailFormattingSkipsTotal++;
            if (!LastVolatileEvidenceFingerprintByThreatGroup.TryGetValue(key, out ulong previousVolatile)
                || previousVolatile != volatileFingerprint)
            {
                LastVolatileEvidenceFingerprintByThreatGroup[key] = volatileFingerprint;
                volatileEvidencePublicationsTotal++;
                VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                {
                    string volatileCandidates = string.Join("^", group.Candidates.Select(VolatileEvidenceToken));
                    return $"TC_TACTICAL_FITNESS_SHADOW_VOLATILE owner={Safe(group.OwnerProfileId)}; threat={Safe(group.ThreatId)}; currentMinusGeometryPathMeters={F(group.CurrentMinusGeometryPathMeters)}; currentMinusGeometryPathRatio={F(group.CurrentMinusGeometryPathRatio)}; currentMinusGeometryAxisEvidence={F(group.CurrentMinusGeometryAxisEvidence)}; candidates={Safe(volatileCandidates)}; semanticDetailUnchanged=true; observationOnly=true";
                });
            }
            return;
        }
        LastDetailFingerprintByThreatGroup[key] = fingerprint;
        LastVolatileEvidenceFingerprintByThreatGroup[key] = volatileFingerprint;

        // Deep per-candidate payloads are trace-only and lazily formatted so Diagnostic mode
        // preserves the semantic observer cadence without paying the large string/logging cost.
        VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
        {
            string candidates = string.Join("^", group.Candidates.Select(CandidateToken));
            return $"TC_TACTICAL_FITNESS_SHADOW_DETAIL owner={Safe(group.OwnerProfileId)}; threat={Safe(group.ThreatId)}; currentAxis={Safe(group.CurrentAxisCandidateId)}; currentBot={Safe(group.CurrentBotProfileId)}; currentRoleBand={Safe(group.CurrentRolePreferenceBand)}; roleReferenceAxis={Safe(group.RoleReferenceAxisCandidateId)}; roleReferenceBot={Safe(group.RoleReferenceBotProfileId)}; geometryReferenceAxis={Safe(group.GeometryReferenceAxisCandidateId)}; geometryReferenceBot={Safe(group.GeometryReferenceBotProfileId)}; referencesAgree={Bool(group.RoleAndGeometryReferencesAgree)}; currentReferenceEligible={Bool(group.CurrentReferenceEligible)}; referenceEligibleCandidates={group.ReferenceEligibleCandidateCount}; referenceEvidenceUnprovenCandidates={group.ReferenceEvidenceUnprovenCandidateCount}; currentMatchesRoleReference={Bool(group.CurrentMatchesRoleReference)}; currentMatchesGeometryReference={Bool(group.CurrentMatchesGeometryReference)}; currentMinusGeometryPathMeters={F(group.CurrentMinusGeometryPathMeters)}; currentMinusGeometryPathRatio={F(group.CurrentMinusGeometryPathRatio)}; currentMinusGeometryAxisEvidence={F(group.CurrentMinusGeometryAxisEvidence)}; currentMinusGeometryRoleRank={group.CurrentMinusGeometryRoleRank}; materiality={Safe(group.MaterialityState)}; resolution={Safe(group.Resolution)}; candidates={Safe(candidates)}; executableSelection=false; behavioralBiasApplied=false; equipmentFitClassification=false; materialGeometryThreshold=false; assignmentMutation=false; observationOnly=true";
        });
    }

    private static ulong ComputeDetailFingerprint(TacticalFitnessThreatGroupObservation group)
    {
        ulong hash = DetailFingerprintOffset;
        Mix(ref hash, group.OwnerProfileId);
        Mix(ref hash, group.ThreatId);
        Mix(ref hash, group.CurrentAxisCandidateId);
        Mix(ref hash, group.CurrentBotProfileId);
        Mix(ref hash, group.CurrentRolePreferenceBand);
        Mix(ref hash, group.RoleReferenceAxisCandidateId);
        Mix(ref hash, group.RoleReferenceBotProfileId);
        Mix(ref hash, group.GeometryReferenceAxisCandidateId);
        Mix(ref hash, group.GeometryReferenceBotProfileId);
        Mix(ref hash, group.AlignedReferenceAxisCandidateId);
        Mix(ref hash, group.AlignedReferenceBotProfileId);
        Mix(ref hash, group.RoleAndGeometryReferencesAgree);
        Mix(ref hash, group.CurrentReferenceEligible);
        Mix(ref hash, group.ReferenceEligibleCandidateCount);
        Mix(ref hash, group.ReferenceEvidenceUnprovenCandidateCount);
        Mix(ref hash, group.CurrentMatchesRoleReference);
        Mix(ref hash, group.CurrentMatchesGeometryReference);
        Mix(ref hash, group.CurrentMinusGeometryRoleRank);
        Mix(ref hash, group.MaterialityState);
        Mix(ref hash, group.Resolution);
        Mix(ref hash, group.Candidates.Count);
        foreach (TacticalFitnessCandidateObservation item in group.Candidates)
        {
            Mix(ref hash, item.HasHeavyBleed);
            Mix(ref hash, item.HasLightBleed);
            Mix(ref hash, item.HasFracture);
            Mix(ref hash, item.HasPain);
            Mix(ref hash, item.HasDestroyedPart);
            Mix(ref hash, item.HasUntreatableVitalDamage);
            Mix(ref hash, item.AxisCandidateId);
            Mix(ref hash, item.BotProfileId);
            Mix(ref hash, item.Role);
            Mix(ref hash, item.Specialty);
            Mix(ref hash, item.RolePreferenceBand);
            Mix(ref hash, item.RolePreferenceRank);
            Mix(ref hash, item.AxisCandidateSource);
            Mix(ref hash, item.AxisSeedBotProfileId);
            Mix(ref hash, item.IsAxisSeed);
            Mix(ref hash, item.RouteEvidenceKind);
            Mix(ref hash, item.EvidenceKind);
            Mix(ref hash, item.QualificationState);
            Mix(ref hash, item.DecisionSnapshotFresh);
            Mix(ref hash, item.ThreatEvidenceObserved);
            Mix(ref hash, item.ThreatEvidenceEnemyId);
            Mix(ref hash, item.ThreatDistanceAlignedToGroup);
            Mix(ref hash, item.ThreatDistanceEvidenceState);
            Mix(ref hash, item.NativeWeaponDistanceAnchorsObserved);
            Mix(ref hash, item.NativeWeaponSwitchLowDistanceMeters);
            Mix(ref hash, item.NativeWeaponSwitchFarDistanceMeters);
            Mix(ref hash, item.NativeWeaponDistanceAnchorState);
            Mix(ref hash, item.ThreatDistanceVsNativeWeaponWindowRelation);
            Mix(ref hash, item.MedicalEvidenceReadable);
            Mix(ref hash, item.HealthPercent);
            Mix(ref hash, item.AnyMedicineUsing);
            Mix(ref hash, item.EquipmentEvidenceObserved);
            Mix(ref hash, item.LongWeaponCount);
            Mix(ref hash, item.CompatibleMagazineCount);
            Mix(ref hash, item.CompatibleAmmunitionCount);
            Mix(ref hash, item.PerWeaponEquipmentEvidenceObserved);
            Mix(ref hash, item.PerWeaponEquipmentObservationReason);
            Mix(ref hash, item.CarriedWeaponCount);
            Mix(ref hash, item.WeaponsWithRoundsCount);
            Mix(ref hash, item.WeaponsWithoutRoundsCount);
            Mix(ref hash, item.BrickedWeaponCount);
            Mix(ref hash, item.SidearmObserved);
            MixWeaponCapabilities(ref hash, item.LiveWeaponCapabilities);
            Mix(ref hash, item.WeaponReadinessObserved);
            Mix(ref hash, item.WeaponReadinessObservationReason);
            Mix(ref hash, item.WeaponReady);
            Mix(ref hash, item.CurrentWeaponKnown);
            Mix(ref hash, item.CurrentWeaponItemId);
            Mix(ref hash, item.CurrentWeaponTemplateId);
            Mix(ref hash, item.ChangingWeapon);
            Mix(ref hash, item.HandsSuspicious);
            Mix(ref hash, item.BasePersona);
            Mix(ref hash, item.Doctrine);
            Mix(ref hash, item.Temperament);
        }
        return hash;
    }

    private static ulong ComputeVolatileEvidenceFingerprint(TacticalFitnessThreatGroupObservation group)
    {
        ulong hash = DetailFingerprintOffset;
        Mix(ref hash, group.OwnerProfileId);
        Mix(ref hash, group.ThreatId);
        Mix(ref hash, group.CurrentMinusGeometryPathMeters);
        Mix(ref hash, group.CurrentMinusGeometryPathRatio);
        Mix(ref hash, group.CurrentMinusGeometryAxisEvidence);
        Mix(ref hash, group.Candidates.Count);
        foreach (TacticalFitnessCandidateObservation item in group.Candidates)
        {
            Mix(ref hash, item.AxisCandidateId);
            Mix(ref hash, item.BotProfileId);
            Mix(ref hash, item.EstablishmentRegionRadiusMeters);
            Mix(ref hash, item.PathCornerCount);
            Mix(ref hash, item.PathLengthMeters);
            Mix(ref hash, item.PathRatio);
            Mix(ref hash, item.AxisEvidenceScore);
            Mix(ref hash, item.ThreatDistanceMeters);
        }
        return hash;
    }

    private const ulong DetailFingerprintOffset = 14695981039346656037UL;
    private const ulong DetailFingerprintPrime = 1099511628211UL;

    private static void Mix(ref ulong hash, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            MixText(ref hash, "none", 0, 4);
            return;
        }

        int start = 0;
        int end = value.Length - 1;
        while (start <= end && char.IsWhiteSpace(value[start])) start++;
        while (end >= start && char.IsWhiteSpace(value[end])) end--;
        if (start > end)
        {
            MixText(ref hash, "none", 0, 4);
            return;
        }

        for (int index = start; index <= end; index++)
        {
            hash ^= char.ToLowerInvariant(value[index]);
            hash *= DetailFingerprintPrime;
        }
        hash ^= 0xFFUL;
        hash *= DetailFingerprintPrime;
    }

    private static void MixText(ref ulong hash, string value, int start, int length)
    {
        int end = start + length;
        for (int index = start; index < end; index++)
        {
            hash ^= char.ToLowerInvariant(value[index]);
            hash *= DetailFingerprintPrime;
        }
        hash ^= 0xFFUL;
        hash *= DetailFingerprintPrime;
    }

    private static void Mix(ref ulong hash, bool value)
    {
        hash ^= value ? 1UL : 0UL;
        hash *= DetailFingerprintPrime;
    }

    private static void Mix(ref ulong hash, int value)
    {
        hash ^= unchecked((ulong)(uint)value);
        hash *= DetailFingerprintPrime;
    }

    private static void Mix(ref ulong hash, float value)
    {
        int hundredths = (int)Math.Round(value * 100.0f, MidpointRounding.AwayFromZero);
        Mix(ref hash, hundredths);
    }

    private static void MixWeaponCapabilities(ref ulong hash, IReadOnlyList<VanguardLiveWeaponCapabilityObservation> weapons)
    {
        if (weapons == null || weapons.Count == 0)
        {
            Mix(ref hash, "none");
            return;
        }
        Mix(ref hash, weapons.Count);
        foreach (VanguardLiveWeaponCapabilityObservation weapon in weapons)
        {
            MixWeaponCapability(ref hash, weapon);
        }
    }

    private static void MixWeaponCapability(ref ulong hash, VanguardLiveWeaponCapabilityObservation? weapon)
    {
        if (weapon == null)
        {
            Mix(ref hash, "none");
            return;
        }
        Mix(ref hash, weapon.Slot);
        Mix(ref hash, weapon.ItemId);
        Mix(ref hash, weapon.TemplateId);
        Mix(ref hash, weapon.Caliber);
        Mix(ref hash, weapon.WeaponClass);
        Mix(ref hash, weapon.CanonicalWeaponFamily);
        Mix(ref hash, weapon.WeaponFamilyQualification);
        Mix(ref hash, weapon.MountedSightCount);
        Mix(ref hash, weapon.MagnifiedSightCount);
        Mix(ref hash, weapon.MaxSupportedSightZoom);
        Mix(ref hash, weapon.SightCapabilityState);
        Mix(ref hash, weapon.ReadyRounds);
        Mix(ref hash, weapon.ReserveRounds);
        Mix(ref hash, weapon.TotalObservedRounds);
        Mix(ref hash, weapon.CompatibleMagazineCount);
        Mix(ref hash, weapon.DurabilityFraction);
        Mix(ref hash, weapon.BestPenetration);
        Mix(ref hash, weapon.BestDamage);
        Mix(ref hash, weapon.Ergonomics);
        Mix(ref hash, weapon.Recoil);
        Mix(ref hash, weapon.SightingRange);
        Mix(ref hash, weapon.EffectiveDistanceMeters);
        Mix(ref hash, weapon.Bricked);
        Mix(ref hash, weapon.RawAmmunitionPresence);
    }

    private static string VolatileEvidenceToken(TacticalFitnessCandidateObservation item)
        => string.Join(",",
            "axis=" + Normalize(item.AxisCandidateId),
            "bot=" + Normalize(item.BotProfileId),
            "establishmentRadius=" + F(item.EstablishmentRegionRadiusMeters),
            "pathCorners=" + item.PathCornerCount.ToString(CultureInfo.InvariantCulture),
            "path=" + F(item.PathLengthMeters),
            "ratio=" + F(item.PathRatio),
            "axisEvidence=" + F(item.AxisEvidenceScore),
            "threatDistance=" + F(item.ThreatDistanceMeters));

    private static string CandidateToken(TacticalFitnessCandidateObservation item)
    {
        string medicalFlags = string.Concat(
            item.HasHeavyBleed ? "HB" : string.Empty,
            item.HasLightBleed ? "LB" : string.Empty,
            item.HasFracture ? "FR" : string.Empty,
            item.HasPain ? "PN" : string.Empty,
            item.HasDestroyedPart ? "BL" : string.Empty,
            item.HasUntreatableVitalDamage ? "UV" : string.Empty);
        if (medicalFlags.Length == 0)
        {
            medicalFlags = "none";
        }

        return string.Join(",",
            "axis=" + Normalize(item.AxisCandidateId),
            "bot=" + Normalize(item.BotProfileId),
            "role=" + Normalize(item.Role),
            "specialty=" + Normalize(item.Specialty),
            "band=" + Normalize(item.RolePreferenceBand),
            "rank=" + item.RolePreferenceRank.ToString(CultureInfo.InvariantCulture),
            "axisSource=" + Normalize(item.AxisCandidateSource),
            "axisSeedBot=" + Normalize(item.AxisSeedBotProfileId),
            "isAxisSeed=" + Bool(item.IsAxisSeed),
            "routeEvidence=" + Normalize(item.RouteEvidenceKind),
            "establishmentRadius=" + F(item.EstablishmentRegionRadiusMeters),
            "pathCorners=" + item.PathCornerCount.ToString(CultureInfo.InvariantCulture),
            "path=" + F(item.PathLengthMeters),
            "ratio=" + F(item.PathRatio),
            "axisEvidence=" + F(item.AxisEvidenceScore),
            "evidence=" + Normalize(item.EvidenceKind),
            "qualification=" + Normalize(item.QualificationState),
            "decisionFresh=" + Bool(item.DecisionSnapshotFresh),
            "threatEvidenceObserved=" + Bool(item.ThreatEvidenceObserved),
            "threatEnemyId=" + Normalize(item.ThreatEvidenceEnemyId),
            "threatDistanceAligned=" + Bool(item.ThreatDistanceAlignedToGroup),
            "threatDistance=" + F(item.ThreatDistanceMeters),
            "threatDistanceState=" + Normalize(item.ThreatDistanceEvidenceState),
            "nativeWeaponDistanceAnchorsObserved=" + Bool(item.NativeWeaponDistanceAnchorsObserved),
            "nativeWeaponSwitchLow=" + F(item.NativeWeaponSwitchLowDistanceMeters),
            "nativeWeaponSwitchFar=" + F(item.NativeWeaponSwitchFarDistanceMeters),
            "nativeWeaponDistanceAnchorState=" + Normalize(item.NativeWeaponDistanceAnchorState),
            "threatVsNativeWeaponWindow=" + Normalize(item.ThreatDistanceVsNativeWeaponWindowRelation),
            "medicalReadable=" + Bool(item.MedicalEvidenceReadable),
            "hp=" + item.HealthPercent.ToString(CultureInfo.InvariantCulture),
            "medicalFlags=" + medicalFlags,
            "medicineUsing=" + Bool(item.AnyMedicineUsing),
            "equipmentObserved=" + Bool(item.EquipmentEvidenceObserved),
            "longWeapons=" + item.LongWeaponCount.ToString(CultureInfo.InvariantCulture),
            "mags=" + item.CompatibleMagazineCount.ToString(CultureInfo.InvariantCulture),
            "ammo=" + item.CompatibleAmmunitionCount.ToString(CultureInfo.InvariantCulture),
            "perWeaponEquipmentObserved=" + Bool(item.PerWeaponEquipmentEvidenceObserved),
            "perWeaponEquipmentReason=" + Normalize(item.PerWeaponEquipmentObservationReason),
            "carriedWeapons=" + item.CarriedWeaponCount.ToString(CultureInfo.InvariantCulture),
            "weaponsWithRounds=" + item.WeaponsWithRoundsCount.ToString(CultureInfo.InvariantCulture),
            "weaponsWithoutRounds=" + item.WeaponsWithoutRoundsCount.ToString(CultureInfo.InvariantCulture),
            "brickedWeapons=" + item.BrickedWeaponCount.ToString(CultureInfo.InvariantCulture),
            "sidearmObserved=" + Bool(item.SidearmObserved),
            "weaponFacts=" + Normalize(WeaponCapabilityToken(item.LiveWeaponCapabilities)),
            "weaponReadyObserved=" + Bool(item.WeaponReadinessObserved),
            "weaponReadyReason=" + Normalize(item.WeaponReadinessObservationReason),
            "weaponReady=" + Bool(item.WeaponReady),
            "currentWeaponKnown=" + Bool(item.CurrentWeaponKnown),
            "currentWeaponItemId=" + Normalize(item.CurrentWeaponItemId),
            "currentWeaponTpl=" + Normalize(item.CurrentWeaponTemplateId),
            "changingWeapon=" + Bool(item.ChangingWeapon),
            "handsSuspicious=" + Bool(item.HandsSuspicious),
            "persona=" + Normalize(item.BasePersona),
            "doctrine=" + Normalize(item.Doctrine),
            "temperament=" + Normalize(item.Temperament));
    }

    private static string WeaponCapabilityToken(IReadOnlyList<VanguardLiveWeaponCapabilityObservation> weapons)
    {
        if (weapons == null || weapons.Count == 0) return "none";
        return string.Join("~", weapons.Select(item => string.Join(":",
            Normalize(item.Slot),
            Normalize(item.ItemId),
            Normalize(item.TemplateId),
            Normalize(item.Caliber),
            Normalize(item.WeaponClass),
            Normalize(item.CanonicalWeaponFamily),
            Normalize(item.WeaponFamilyQualification),
            item.MountedSightCount.ToString(CultureInfo.InvariantCulture),
            item.MagnifiedSightCount.ToString(CultureInfo.InvariantCulture),
            F(item.MaxSupportedSightZoom),
            Normalize(item.SightCapabilityState),
            item.ReadyRounds.ToString(CultureInfo.InvariantCulture),
            item.ReserveRounds.ToString(CultureInfo.InvariantCulture),
            item.CompatibleMagazineCount.ToString(CultureInfo.InvariantCulture),
            F(item.DurabilityFraction),
            item.BestPenetration.ToString(CultureInfo.InvariantCulture),
            item.BestDamage.ToString(CultureInfo.InvariantCulture),
            F(item.Ergonomics),
            F(item.Recoil),
            F(item.SightingRange),
            item.EffectiveDistanceMeters.ToString(CultureInfo.InvariantCulture),
            Bool(item.Bricked),
            Normalize(item.RawAmmunitionPresence))));
    }

    private static bool IsReferenceEligible(TacticalFitnessCandidateObservation item)
        => item.PlanningViable
            && item.CandidateSpecificPathProven
            && item.RuntimePresent
            && item.DecisionSnapshotFresh
            && item.MedicalEvidenceReadable
            && item.EquipmentEvidenceObserved
            && item.WeaponReadinessObserved
            && string.Equals(item.EvidenceKind, "DIRECT_OBSERVED", StringComparison.Ordinal);

    private static bool SamePair(string axisA, string botA, string axisB, string botB)
        => string.Equals(axisA, axisB, StringComparison.OrdinalIgnoreCase)
            && string.Equals(botA, botB, StringComparison.OrdinalIgnoreCase);

    private static void InvalidatePublished()
    {
        lock (Sync)
        {
            current = TacticalFitnessShadowSnapshot.Empty;
        }
        LastDetailFingerprintByThreatGroup.Clear();
        LastVolatileEvidenceFingerprintByThreatGroup.Clear();
        CompetitiveCandidateEnvelopeShadowService.InvalidateFromSource();
    }

    private static void ResetInternal()
    {
        lock (Sync)
        {
            current = TacticalFitnessShadowSnapshot.Empty;
        }
        LastDetailFingerprintByThreatGroup.Clear();
        LastVolatileEvidenceFingerprintByThreatGroup.Clear();
        nextObservationAtUtc = DateTimeOffset.MinValue;
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        publicationsTotal = 0;
        groupsObservedTotal = 0;
        candidatePairsObservedTotal = 0;
        directCapabilityEvidenceTotal = 0;
        partialCapabilityEvidenceTotal = 0;
        unknownCapabilityEvidenceTotal = 0;
        roleGeometryAlignedTotal = 0;
        roleGeometryConflictTotal = 0;
        referenceEligibleCandidatesTotal = 0;
        referenceEvidenceUnprovenCandidatesTotal = 0;
        referenceRuntimeMissingCandidatesTotal = 0;
        groupsWithoutReferenceEligibleCandidateTotal = 0;
        budgetDeferralsTotal = 0;
        detailFormattingSkipsTotal = 0;
        volatileEvidencePublicationsTotal = 0;
        PerformanceAggregate.Reset();
        CompetitiveCandidateEnvelopeShadowService.ResetFromSource();
    }

    private static long ProfileStart(TacticalFitnessPerformancePass performanceProfile)
        => performanceProfile.Enabled ? Stopwatch.GetTimestamp() : 0L;

    private static void ProfileAddElapsed(ref long destination, long startedTimestamp)
    {
        if (startedTimestamp <= 0L)
        {
            return;
        }

        long elapsedTicks = Stopwatch.GetTimestamp() - startedTimestamp;
        if (elapsedTicks > 0L)
        {
            destination += elapsedTicks;
        }
    }

    private static void RecordPerformanceProfile(
        DateTimeOffset now,
        ref TacticalFitnessPerformancePass performanceProfile,
        int groupCount,
        int candidatePairCount)
    {
        if (!performanceProfile.Enabled)
        {
            return;
        }

        long totalTicks = Stopwatch.GetTimestamp() - performanceProfile.StartedTimestamp;
        if (totalTicks <= 0L)
        {
            return;
        }

        performanceProfile.TotalTicks = totalTicks;
        performanceProfile.GroupCount = groupCount;
        performanceProfile.CandidatePairCount = candidatePairCount;
        PerformanceAggregate.Record(now, performanceProfile);

        if (!PerformanceAggregate.ShouldFlush(now))
        {
            return;
        }

        TacticalFitnessPerformanceAggregateSnapshot snapshot = PerformanceAggregate.SnapshotAndReset(now);
        if (snapshot.SlowPasses <= 0)
        {
            return;
        }

        Func<string> payload = () =>
            $"TC_TACTICAL_FITNESS_PERF_PROFILE windowSeconds={snapshot.WindowSeconds:0.00}; passes={snapshot.Passes}; slowPasses={snapshot.SlowPasses}; thresholdMs={PerformanceProfileSlowThresholdMilliseconds:0.00}; avgTotalMs={TicksToMilliseconds(snapshot.TotalTicks) / Math.Max(1L, snapshot.Passes):0.00}; maxTotalMs={TicksToMilliseconds(snapshot.MaxPass.TotalTicks):0.00}; avgMatrixMs={TicksToMilliseconds(snapshot.MatrixTicks) / Math.Max(1L, snapshot.Passes):0.00}; avgCapabilityMs={TicksToMilliseconds(snapshot.CapabilityTicks) / Math.Max(1L, snapshot.Passes):0.00}; avgEquipmentMs={TicksToMilliseconds(snapshot.EquipmentTicks) / Math.Max(1L, snapshot.Passes):0.00}; avgReadinessMs={TicksToMilliseconds(snapshot.ReadinessTicks) / Math.Max(1L, snapshot.Passes):0.00}; avgCapabilityOtherMs={TicksToMilliseconds(Math.Max(0L, snapshot.CapabilityTicks - snapshot.EquipmentTicks - snapshot.ReadinessTicks)) / Math.Max(1L, snapshot.Passes):0.00}; avgCandidateProjectionMs={TicksToMilliseconds(snapshot.CandidateProjectionTicks) / Math.Max(1L, snapshot.Passes):0.00}; avgReferenceResolutionMs={TicksToMilliseconds(snapshot.ReferenceResolutionTicks) / Math.Max(1L, snapshot.Passes):0.00}; avgDetailPresentationMs={TicksToMilliseconds(snapshot.DetailPresentationTicks) / Math.Max(1L, snapshot.Passes):0.00}; avgPublishMs={TicksToMilliseconds(snapshot.PublishTicks) / Math.Max(1L, snapshot.Passes):0.00}; avgSummaryPresentationMs={TicksToMilliseconds(snapshot.SummaryPresentationTicks) / Math.Max(1L, snapshot.Passes):0.00}; avgUnattributedMs={TicksToMilliseconds(snapshot.UnattributedTicks) / Math.Max(1L, snapshot.Passes):0.00}; capabilityReads={snapshot.CapabilityReads}; groups={snapshot.Groups}; candidatePairs={snapshot.CandidatePairs}; maxPassCapabilityReads={snapshot.MaxPass.CapabilityReads}; maxPassGroups={snapshot.MaxPass.GroupCount}; maxPassCandidatePairs={snapshot.MaxPass.CandidatePairCount}; maxPassMatrixMs={TicksToMilliseconds(snapshot.MaxPass.MatrixTicks):0.00}; maxPassCapabilityMs={TicksToMilliseconds(snapshot.MaxPass.CapabilityTicks):0.00}; maxPassEquipmentMs={TicksToMilliseconds(snapshot.MaxPass.EquipmentTicks):0.00}; maxPassReadinessMs={TicksToMilliseconds(snapshot.MaxPass.ReadinessTicks):0.00}; maxPassCapabilityOtherMs={TicksToMilliseconds(Math.Max(0L, snapshot.MaxPass.CapabilityTicks - snapshot.MaxPass.EquipmentTicks - snapshot.MaxPass.ReadinessTicks)):0.00}; maxPassCandidateProjectionMs={TicksToMilliseconds(snapshot.MaxPass.CandidateProjectionTicks):0.00}; maxPassReferenceResolutionMs={TicksToMilliseconds(snapshot.MaxPass.ReferenceResolutionTicks):0.00}; maxPassDetailPresentationMs={TicksToMilliseconds(snapshot.MaxPass.DetailPresentationTicks):0.00}; maxPassPublishMs={TicksToMilliseconds(snapshot.MaxPass.PublishTicks):0.00}; maxPassSummaryPresentationMs={TicksToMilliseconds(snapshot.MaxPass.SummaryPresentationTicks):0.00}; maxPassUnattributedMs={TicksToMilliseconds(snapshot.MaxPass.UnattributedTicks):0.00}; nestedCapabilitySubphases=true; equipmentAndReadinessAreSubsetsOfCapability=true; profileCadenceSeconds={PerformanceProfileWindow.TotalSeconds:0.00}; observationOnly=true; gameplayMutation=false; thresholdsUnchanged=true";

        if (TicksToMilliseconds(snapshot.MaxPass.TotalTicks) >= 100.0d)
        {
            VanguardClientDiagnosticsLog.Warning(StatusTag, payload);
        }
        else
        {
            VanguardClientDiagnosticsLog.Info(StatusTag, payload());
        }
    }

    private static double TicksToMilliseconds(long ticks)
        => ticks <= 0L ? 0d : ticks * 1000.0d / Stopwatch.Frequency;

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().ToLowerInvariant();

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private static string F(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Timestamp(DateTimeOffset value) => value == DateTimeOffset.MinValue ? "none" : value.ToString("O", CultureInfo.InvariantCulture);

    private struct TacticalFitnessPerformancePass
    {
        public TacticalFitnessPerformancePass(long startedTimestamp)
        {
            StartedTimestamp = startedTimestamp;
            TotalTicks = 0L;
            MatrixTicks = 0L;
            CapabilityTicks = 0L;
            EquipmentTicks = 0L;
            ReadinessTicks = 0L;
            CandidateProjectionTicks = 0L;
            ReferenceResolutionTicks = 0L;
            DetailPresentationTicks = 0L;
            PublishTicks = 0L;
            SummaryPresentationTicks = 0L;
            CapabilityReads = 0;
            GroupCount = 0;
            CandidatePairCount = 0;
        }

        public bool Enabled => StartedTimestamp > 0L;
        public long StartedTimestamp;
        public long TotalTicks;
        public long MatrixTicks;
        public long CapabilityTicks;
        public long EquipmentTicks;
        public long ReadinessTicks;
        public long CandidateProjectionTicks;
        public long ReferenceResolutionTicks;
        public long DetailPresentationTicks;
        public long PublishTicks;
        public long SummaryPresentationTicks;
        public int CapabilityReads;
        public int GroupCount;
        public int CandidatePairCount;

        public long UnattributedTicks
        {
            get
            {
                long exclusiveAttributed = MatrixTicks
                    + CapabilityTicks
                    + CandidateProjectionTicks
                    + ReferenceResolutionTicks
                    + DetailPresentationTicks
                    + PublishTicks
                    + SummaryPresentationTicks;
                return Math.Max(0L, TotalTicks - exclusiveAttributed);
            }
        }
    }

    private sealed class TacticalFitnessPerformanceAggregate
    {
        private DateTimeOffset windowStartedAtUtc = DateTimeOffset.MinValue;
        private long passes;
        private long slowPasses;
        private long totalTicks;
        private long matrixTicks;
        private long capabilityTicks;
        private long equipmentTicks;
        private long readinessTicks;
        private long candidateProjectionTicks;
        private long referenceResolutionTicks;
        private long detailPresentationTicks;
        private long publishTicks;
        private long summaryPresentationTicks;
        private long unattributedTicks;
        private long capabilityReads;
        private long groups;
        private long candidatePairs;
        private TacticalFitnessPerformancePass maxPass;

        public void Record(DateTimeOffset now, TacticalFitnessPerformancePass pass)
        {
            if (windowStartedAtUtc == DateTimeOffset.MinValue)
            {
                windowStartedAtUtc = now;
            }

            passes++;
            totalTicks += pass.TotalTicks;
            matrixTicks += pass.MatrixTicks;
            capabilityTicks += pass.CapabilityTicks;
            equipmentTicks += pass.EquipmentTicks;
            readinessTicks += pass.ReadinessTicks;
            candidateProjectionTicks += pass.CandidateProjectionTicks;
            referenceResolutionTicks += pass.ReferenceResolutionTicks;
            detailPresentationTicks += pass.DetailPresentationTicks;
            publishTicks += pass.PublishTicks;
            summaryPresentationTicks += pass.SummaryPresentationTicks;
            unattributedTicks += pass.UnattributedTicks;
            capabilityReads += pass.CapabilityReads;
            groups += pass.GroupCount;
            candidatePairs += pass.CandidatePairCount;

            if (TicksToMilliseconds(pass.TotalTicks) >= PerformanceProfileSlowThresholdMilliseconds)
            {
                slowPasses++;
            }

            if (pass.TotalTicks > maxPass.TotalTicks)
            {
                maxPass = pass;
            }
        }

        public bool ShouldFlush(DateTimeOffset now)
            => windowStartedAtUtc != DateTimeOffset.MinValue
                && now - windowStartedAtUtc >= PerformanceProfileWindow;

        public TacticalFitnessPerformanceAggregateSnapshot SnapshotAndReset(DateTimeOffset now)
        {
            double windowSeconds = windowStartedAtUtc == DateTimeOffset.MinValue
                ? 0d
                : Math.Max(0d, (now - windowStartedAtUtc).TotalSeconds);
            var snapshot = new TacticalFitnessPerformanceAggregateSnapshot(
                windowSeconds,
                passes,
                slowPasses,
                totalTicks,
                matrixTicks,
                capabilityTicks,
                equipmentTicks,
                readinessTicks,
                candidateProjectionTicks,
                referenceResolutionTicks,
                detailPresentationTicks,
                publishTicks,
                summaryPresentationTicks,
                unattributedTicks,
                capabilityReads,
                groups,
                candidatePairs,
                maxPass);
            Reset();
            return snapshot;
        }

        public void Reset()
        {
            windowStartedAtUtc = DateTimeOffset.MinValue;
            passes = 0L;
            slowPasses = 0L;
            totalTicks = 0L;
            matrixTicks = 0L;
            capabilityTicks = 0L;
            equipmentTicks = 0L;
            readinessTicks = 0L;
            candidateProjectionTicks = 0L;
            referenceResolutionTicks = 0L;
            detailPresentationTicks = 0L;
            publishTicks = 0L;
            summaryPresentationTicks = 0L;
            unattributedTicks = 0L;
            capabilityReads = 0L;
            groups = 0L;
            candidatePairs = 0L;
            maxPass = default;
        }
    }

    private sealed record TacticalFitnessPerformanceAggregateSnapshot(
        double WindowSeconds,
        long Passes,
        long SlowPasses,
        long TotalTicks,
        long MatrixTicks,
        long CapabilityTicks,
        long EquipmentTicks,
        long ReadinessTicks,
        long CandidateProjectionTicks,
        long ReferenceResolutionTicks,
        long DetailPresentationTicks,
        long PublishTicks,
        long SummaryPresentationTicks,
        long UnattributedTicks,
        long CapabilityReads,
        long Groups,
        long CandidatePairs,
        TacticalFitnessPerformancePass MaxPass);

    private sealed class CandidateCapabilityRead
    {
        public bool RuntimePresent { get; init; }
        public string OperatorId { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public string Specialty { get; init; } = string.Empty;
        public string BasePersona { get; init; } = string.Empty;
        public string Doctrine { get; init; } = string.Empty;
        public string Temperament { get; init; } = string.Empty;
        public int IdentitySchemaVersion { get; init; }
        public bool DecisionSnapshotObserved { get; init; }
        public bool DecisionSnapshotFresh { get; init; }
        public string DecisionFreshnessReason { get; init; } = "unknown";
        public DateTimeOffset DecisionCapturedAtUtc { get; init; } = DateTimeOffset.MinValue;
        public bool DecisionThreatHasThreat { get; init; }
        public string DecisionThreatEnemyId { get; init; } = "none";
        public bool DecisionThreatDistanceObserved { get; init; }
        public float DecisionThreatDistanceMeters { get; init; } = -1f;
        public bool NativeWeaponDistanceAnchorsObserved { get; init; }
        public float NativeWeaponSwitchLowDistanceMeters { get; init; } = -1f;
        public float NativeWeaponSwitchFarDistanceMeters { get; init; } = -1f;
        public bool MedicalEvidenceReadable { get; init; }
        public int HealthPercent { get; init; } = -1;
        public bool HasHeavyBleed { get; init; }
        public bool HasLightBleed { get; init; }
        public bool HasFracture { get; init; }
        public bool HasPain { get; init; }
        public bool HasDestroyedPart { get; init; }
        public bool HasUntreatableVitalDamage { get; init; }
        public bool AnyMedicineUsing { get; init; }
        public string MedicalClassification { get; init; } = "unknown";
        public bool EquipmentEvidenceObserved { get; init; }
        public bool HasPrimaryWeapon { get; init; }
        public int LongWeaponCount { get; init; } = -1;
        public int CompatibleMagazineCount { get; init; } = -1;
        public int CompatibleAmmunitionCount { get; init; } = -1;
        public bool PerWeaponEquipmentEvidenceObserved { get; init; }
        public string PerWeaponEquipmentObservationReason { get; init; } = "unobserved";
        public int CarriedWeaponCount { get; init; } = -1;
        public int WeaponsWithRoundsCount { get; init; } = -1;
        public int WeaponsWithoutRoundsCount { get; init; } = -1;
        public int BrickedWeaponCount { get; init; } = -1;
        public bool SidearmObserved { get; init; }
        public IReadOnlyList<VanguardLiveWeaponCapabilityObservation> LiveWeaponCapabilities { get; init; } = Array.Empty<VanguardLiveWeaponCapabilityObservation>();
        public bool WeaponReadinessObserved { get; init; }
        public string WeaponReadinessObservationReason { get; init; } = "unobserved";
        public bool CurrentWeaponKnown { get; init; }
        public string CurrentWeaponItemId { get; init; } = "none";
        public string CurrentWeaponTemplateId { get; init; } = "none";
        public bool WeaponReady { get; init; }
        public bool ChangingWeapon { get; init; }
        public bool HandsSuspicious { get; init; }
        public string EvidenceKind { get; init; } = "UNKNOWN";
        public string QualificationState { get; init; } = "capability_unknown";

        public static CandidateCapabilityRead Unknown(string reason) => new()
        {
            DecisionFreshnessReason = reason,
            QualificationState = reason,
            EvidenceKind = "UNKNOWN"
        };
    }
}
#endif
