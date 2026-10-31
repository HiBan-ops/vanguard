#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vanguard.Client.Diagnostics;

// Responsibility: Publishes a threshold-free Competitive Candidate Envelope calibration surface for FlankLead without selecting or mutating an assignment.
// Flow: Consumes TacticalFitnessShadow only, preserves the evidence-eligible calibration surface, then forms a second fail-closed FlankLead equipment-floor-proven cohort before choosing its own shortest-path calibration reference.
// Authority boundary: Shadow observer only. No new reader, pathfinding, threat scan, reserve-band/equipment-fit classification, persona bias, hysteresis, assignment mutation, intent/lease, movement, target, aim, fire or weapon switch authority.
// Invariant: Evidence eligibility is never rewritten; UNKNOWN equipment-floor evidence is viability-unproven, proven no-weapon/empty states are excluded only from the shadow capability cohort, and no cohort can classify NEGLIGIBLE/MINOR/MATERIAL, mutate RouteAssignment or produce a winner.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class CompetitiveCandidateEnvelopeShadowService
{
    public const string StatusTag = "VANGUARD_COMPETITIVE_CANDIDATE_ENVELOPE_SHADOW_STATUS";

    private static readonly TimeSpan SummaryLogInterval = TimeSpan.FromSeconds(15.0d);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, ulong> LastDetailFingerprintByThreatGroup = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ulong> LastVolatileEvidenceFingerprintByThreatGroup = new(StringComparer.OrdinalIgnoreCase);

    private static CompetitiveCandidateEnvelopeShadowSnapshot current = CompetitiveCandidateEnvelopeShadowSnapshot.Empty;
    private static DateTimeOffset lastSourcePublicationAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    private static int publicationsTotal;
    private static int groupsObservedTotal;
    private static int rawCandidatesTotal;
    private static int evidenceEligibleCandidatesTotal;
    private static int evidenceUnprovenCandidatesTotal;
    private static int comparableCandidatesTotal;
    private static int comparableChallengersTotal;
    private static int referenceNonWorseGeometryChallengersTotal;
    private static int countervailingGeometryChallengersTotal;
    private static int rawGeometryUnresolvedChallengersTotal;
    private static int roleAheadOfGeometryReferenceChallengersTotal;
    private static int roleSameAsGeometryReferenceChallengersTotal;
    private static int roleBehindGeometryReferenceChallengersTotal;
    private static int groupsWithoutEvidenceEligibleCandidateTotal;
    private static int singleEvidenceEligibleCandidateGroupsTotal;
    private static int multiEvidenceEligibleCandidateGroupsTotal;
    private static int materialFloorUnknownCandidatesTotal;
    private static int materialFloorNoWeaponFailuresTotal;
    private static int materialFloorEmptyFailuresTotal;
    private static int materialFloorNonemptyCandidatesTotal;
    private static int immediateReadyCandidatesTotal;
    private static int immediateNotReadyCandidatesTotal;
    private static int immediateTransitionalCandidatesTotal;
    private static int immediateUnknownCandidatesTotal;
    private static int floorEvidenceProvenCandidatesTotal;
    private static int floorEvidenceUnprovenCandidatesTotal;
    private static int floorEligibleCandidatesTotal;
    private static int floorHardFailedCandidatesTotal;
    private static int groupsWithoutFloorEligibleCandidateTotal;
    private static int groupsAllFloorFailedTotal;
    private static int groupsFloorViabilityUnprovenTotal;
    private static int capabilityGeometryReferenceChangedGroupsTotal;
    private static int capabilityComparableChallengersTotal;
    private static int capabilityReferenceNonWorseGeometryChallengersTotal;
    private static int capabilityCountervailingGeometryChallengersTotal;
    private static int capabilityRawGeometryUnresolvedChallengersTotal;
    private static int immediateWeaponExactMatchesTotal;
    private static int immediateWeaponTemplateFallbackMatchesTotal;
    private static int immediateWeaponCapabilityUnresolvedTotal;
    private static int candidatesWithAlternativeCarriedWeaponsTotal;
    private static int detailFormattingSkipsTotal;
    private static int volatileEvidencePublicationsTotal;

    public static void PublishFromTacticalFitness(TacticalFitnessShadowSnapshot source, DateTimeOffset now)
    {
        if (!source.IsAvailable)
        {
            InvalidateFromSource();
            return;
        }

        if (source.PublishedAtUtc == lastSourcePublicationAtUtc)
        {
            return;
        }
        lastSourcePublicationAtUtc = source.PublishedAtUtc;

        var groups = new List<CompetitiveCandidateEnvelopeThreatGroupObservation>(source.ThreatGroups.Count);
        int rawCandidates = 0;
        int evidenceEligibleCandidates = 0;
        int evidenceUnprovenCandidates = 0;
        int comparableCandidates = 0;
        int comparableChallengers = 0;
        int referenceNonWorseGeometryChallengers = 0;
        int countervailingGeometryChallengers = 0;
        int rawGeometryUnresolvedChallengers = 0;
        int roleAheadOfGeometryReferenceChallengers = 0;
        int roleSameAsGeometryReferenceChallengers = 0;
        int roleBehindGeometryReferenceChallengers = 0;
        int groupsWithoutEligible = 0;
        int singleEligibleGroups = 0;
        int multiEligibleGroups = 0;
        int materialFloorUnknownCandidates = 0;
        int materialFloorNoWeaponFailures = 0;
        int materialFloorEmptyFailures = 0;
        int materialFloorNonemptyCandidates = 0;
        int immediateReadyCandidates = 0;
        int immediateNotReadyCandidates = 0;
        int immediateTransitionalCandidates = 0;
        int immediateUnknownCandidates = 0;
        int floorEvidenceProvenCandidates = 0;
        int floorEvidenceUnprovenCandidates = 0;
        int floorEligibleCandidates = 0;
        int floorHardFailedCandidates = 0;
        int groupsWithoutFloorEligibleCandidate = 0;
        int groupsAllFloorFailed = 0;
        int groupsFloorViabilityUnproven = 0;
        int capabilityGeometryReferenceChangedGroups = 0;
        int capabilityComparableChallengers = 0;
        int capabilityReferenceNonWorseGeometryChallengers = 0;
        int capabilityCountervailingGeometryChallengers = 0;
        int capabilityRawGeometryUnresolvedChallengers = 0;
        int immediateWeaponExactMatches = 0;
        int immediateWeaponTemplateFallbackMatches = 0;
        int immediateWeaponCapabilityUnresolved = 0;
        int candidatesWithAlternativeCarriedWeapons = 0;

        foreach (TacticalFitnessThreatGroupObservation sourceGroup in source.ThreatGroups
            .OrderBy(item => item.OwnerProfileId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ThreatId, StringComparer.OrdinalIgnoreCase))
        {
            TacticalFitnessCandidateObservation[] allCandidates = sourceGroup.Candidates
                .Where(item => item != null)
                .OrderBy(item => item.AxisCandidateId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            TacticalFitnessCandidateObservation[] eligibleCandidates = allCandidates
                .Where(IsEvidenceEligible)
                .ToArray();

            TacticalFitnessCandidateObservation? geometryReference = eligibleCandidates
                .OrderBy(item => item.PathLengthMeters)
                .ThenBy(item => item.PathRatio)
                .ThenByDescending(item => item.AxisEvidenceScore)
                .ThenByDescending(item => item.RolePreferenceRank)
                .ThenBy(item => item.AxisCandidateId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            TacticalFitnessCandidateObservation[] floorEvidenceProven = eligibleCandidates
                .Where(IsFlankLeadFloorEvidenceProven)
                .ToArray();

            TacticalFitnessCandidateObservation[] floorEligible = floorEvidenceProven
                .Where(IsFlankLeadFloorEligible)
                .ToArray();

            TacticalFitnessCandidateObservation? capabilityGeometryReference = floorEligible
                .OrderBy(item => item.PathLengthMeters)
                .ThenBy(item => item.PathRatio)
                .ThenByDescending(item => item.AxisEvidenceScore)
                .ThenByDescending(item => item.RolePreferenceRank)
                .ThenBy(item => item.AxisCandidateId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.BotProfileId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            bool capabilityGeometryReferenceDiffersFromLegacy = geometryReference != null
                && capabilityGeometryReference != null
                && !SamePair(geometryReference.AxisCandidateId, geometryReference.BotProfileId, capabilityGeometryReference.AxisCandidateId, capabilityGeometryReference.BotProfileId);
            if (capabilityGeometryReferenceDiffersFromLegacy) capabilityGeometryReferenceChangedGroups++;

            int floorHardFailed = floorEvidenceProven.Length - floorEligible.Length;
            int floorUnproven = eligibleCandidates.Length - floorEvidenceProven.Length;
            string capabilityCohortState = ResolveCapabilityCohortState(
                allCandidates.Length,
                eligibleCandidates.Length,
                floorEligible.Length,
                floorHardFailed,
                floorUnproven);

            if (eligibleCandidates.Length > 0 && floorEligible.Length == 0) groupsWithoutFloorEligibleCandidate++;
            if (eligibleCandidates.Length > 0 && floorEligible.Length == 0 && floorHardFailed == eligibleCandidates.Length) groupsAllFloorFailed++;
            if (eligibleCandidates.Length > 0 && floorEligible.Length == 0 && floorUnproven > 0) groupsFloorViabilityUnproven++;

            floorEvidenceProvenCandidates += floorEvidenceProven.Length;
            floorEvidenceUnprovenCandidates += floorUnproven;
            floorEligibleCandidates += floorEligible.Length;
            floorHardFailedCandidates += floorHardFailed;

            string envelopeState = eligibleCandidates.Length switch
            {
                0 => allCandidates.Length == 0 ? "no_candidate" : "no_assignment_viability_unproven",
                1 => "single_proven_candidate_calibration_only",
                _ => "competitive_membership_unresolved_materiality_not_calibrated"
            };

            if (eligibleCandidates.Length == 0 && allCandidates.Length > 0) groupsWithoutEligible++;
            if (eligibleCandidates.Length == 1) singleEligibleGroups++;
            if (eligibleCandidates.Length > 1) multiEligibleGroups++;

            int groupComparableChallengers = 0;
            int groupReferenceNonWorseGeometryChallengers = 0;
            int groupCountervailingGeometryChallengers = 0;
            int groupRawGeometryUnresolvedChallengers = 0;
            int groupRoleAheadOfGeometryReferenceChallengers = 0;
            int groupRoleSameAsGeometryReferenceChallengers = 0;
            int groupRoleBehindGeometryReferenceChallengers = 0;
            int groupCapabilityComparableChallengers = 0;
            int groupCapabilityReferenceNonWorseGeometryChallengers = 0;
            int groupCapabilityCountervailingGeometryChallengers = 0;
            int groupCapabilityRawGeometryUnresolvedChallengers = 0;
            bool currentFloorEvidenceProven = false;
            bool currentFloorEligibleShadow = false;

            var projected = new List<CompetitiveCandidateEnvelopeCandidateObservation>(allCandidates.Length);
            foreach (TacticalFitnessCandidateObservation candidate in allCandidates)
            {
                bool eligible = IsEvidenceEligible(candidate);
                bool comparable = eligible && geometryReference != null;
                bool isGeometryReference = comparable && SamePair(
                    candidate.AxisCandidateId,
                    candidate.BotProfileId,
                    geometryReference!.AxisCandidateId,
                    geometryReference.BotProfileId);

                string pathLengthRawRelation = ResolveFloatRelation(candidate.PathLengthMeters, geometryReference?.PathLengthMeters ?? 0f, comparable, isGeometryReference);
                string directDistanceRawRelation = ResolveFloatRelation(candidate.DirectDistanceMeters, geometryReference?.DirectDistanceMeters ?? 0f, comparable, isGeometryReference);
                string pathRatioRawRelation = ResolveFloatRelation(candidate.PathRatio, geometryReference?.PathRatio ?? 0f, comparable, isGeometryReference);
                string axisEvidenceRawRelation = ResolveFloatRelation(candidate.AxisEvidenceScore, geometryReference?.AxisEvidenceScore ?? 0f, comparable, isGeometryReference);
                string roleRankRawRelation = ResolveRoleRankRelation(candidate.RolePreferenceRank, geometryReference, comparable, isGeometryReference);
                string rawGeometryRelationState = ResolveRawGeometryRelation(candidate, geometryReference, comparable, isGeometryReference);
                string calibrationCohort = ResolveCalibrationCohort(roleRankRawRelation, rawGeometryRelationState, comparable, isGeometryReference);
                string ammunitionBoundaryState = ResolveAmmunitionBoundaryState(candidate);
                string immediateWeaponState = ResolveImmediateWeaponState(candidate);
                string materialFloorState = ResolveFlankLeadMaterialFloorShadowState(ammunitionBoundaryState, immediateWeaponState);
                bool hardMaterialFloorFailureObserved =
                    string.Equals(materialFloorState, "HARD_FLOOR_FAIL_NO_CARRIED_WEAPON", StringComparison.Ordinal)
                    || string.Equals(materialFloorState, "HARD_FLOOR_FAIL_EMPTY_OR_BRICKED", StringComparison.Ordinal);
                bool floorEvidenceProvenCandidate = eligible && !string.Equals(materialFloorState, "UNKNOWN", StringComparison.Ordinal);
                bool floorEligibleCandidate = floorEvidenceProvenCandidate && !hardMaterialFloorFailureObserved;
                string floorAdmissionState = ResolveFloorAdmissionState(eligible, materialFloorState, hardMaterialFloorFailureObserved);
                VanguardLiveWeaponCapabilityObservation? immediateWeaponCapability = ResolveImmediateWeaponCapability(
                    candidate,
                    out string immediateWeaponCapabilityMatchState,
                    out int alternativeCarriedWeaponCount);
                bool immediateWeaponCapabilityObserved = immediateWeaponCapability != null;
                float immediateWeaponSightingRangeMeters = immediateWeaponCapability != null
                    && IsFinite(immediateWeaponCapability.SightingRange)
                    && immediateWeaponCapability.SightingRange > 0f
                        ? immediateWeaponCapability.SightingRange
                        : -1f;
                string threatDistanceVsImmediateWeaponSightingRangeRelation = ResolveThreatDistanceVsImmediateWeaponSightingRangeRelation(
                    candidate,
                    immediateWeaponCapability);
                int immediateWeaponEffectiveDistanceMeters = immediateWeaponCapability?.EffectiveDistanceMeters ?? -1;
                string threatDistanceVsImmediateWeaponEffectiveDistanceRelation = ResolveThreatDistanceVsImmediateWeaponEffectiveDistanceRelation(
                    candidate,
                    immediateWeaponCapability);
                switch (immediateWeaponCapabilityMatchState)
                {
                    case "EXACT_ITEM_MATCH": immediateWeaponExactMatches++; break;
                    case "UNIQUE_TEMPLATE_FALLBACK_MATCH": immediateWeaponTemplateFallbackMatches++; break;
                    default: immediateWeaponCapabilityUnresolved++; break;
                }
                if (alternativeCarriedWeaponCount > 0) candidatesWithAlternativeCarriedWeapons++;

                bool capabilityGeometryReferenceCandidate = floorEligibleCandidate
                    && capabilityGeometryReference != null
                    && SamePair(candidate.AxisCandidateId, candidate.BotProfileId, capabilityGeometryReference.AxisCandidateId, capabilityGeometryReference.BotProfileId);
                bool comparableWithinCapabilityCohort = floorEligibleCandidate && capabilityGeometryReference != null;
                string capabilityPathLengthRawRelation = ResolveFloatRelation(candidate.PathLengthMeters, capabilityGeometryReference?.PathLengthMeters ?? 0f, comparableWithinCapabilityCohort, capabilityGeometryReferenceCandidate);
                string capabilityDirectDistanceRawRelation = ResolveFloatRelation(candidate.DirectDistanceMeters, capabilityGeometryReference?.DirectDistanceMeters ?? 0f, comparableWithinCapabilityCohort, capabilityGeometryReferenceCandidate);
                string capabilityPathRatioRawRelation = ResolveFloatRelation(candidate.PathRatio, capabilityGeometryReference?.PathRatio ?? 0f, comparableWithinCapabilityCohort, capabilityGeometryReferenceCandidate);
                string capabilityAxisEvidenceRawRelation = ResolveFloatRelation(candidate.AxisEvidenceScore, capabilityGeometryReference?.AxisEvidenceScore ?? 0f, comparableWithinCapabilityCohort, capabilityGeometryReferenceCandidate);
                string capabilityEstablishmentRadiusRawRelation = ResolveFloatRelation(candidate.EstablishmentRegionRadiusMeters, capabilityGeometryReference?.EstablishmentRegionRadiusMeters ?? 0f, comparableWithinCapabilityCohort, capabilityGeometryReferenceCandidate);
                string capabilityPathCornerCountRawRelation = ResolveIntRelation(candidate.PathCornerCount, capabilityGeometryReference?.PathCornerCount ?? 0, comparableWithinCapabilityCohort, capabilityGeometryReferenceCandidate);
                string capabilityAxisSourceRelation = ResolveStringEqualityRelation(candidate.AxisCandidateSource, capabilityGeometryReference?.AxisCandidateSource, comparableWithinCapabilityCohort, capabilityGeometryReferenceCandidate);
                string capabilityRouteEvidenceKindRelation = ResolveStringEqualityRelation(candidate.RouteEvidenceKind, capabilityGeometryReference?.RouteEvidenceKind, comparableWithinCapabilityCohort, capabilityGeometryReferenceCandidate);
                string capabilityRawGeometryRelationState = ResolveRawGeometryRelation(candidate, capabilityGeometryReference, comparableWithinCapabilityCohort, capabilityGeometryReferenceCandidate);

                if (SamePair(candidate.AxisCandidateId, candidate.BotProfileId, sourceGroup.CurrentAxisCandidateId, sourceGroup.CurrentBotProfileId))
                {
                    currentFloorEvidenceProven = floorEvidenceProvenCandidate;
                    currentFloorEligibleShadow = floorEligibleCandidate;
                }

                switch (materialFloorState)
                {
                    case "HARD_FLOOR_FAIL_NO_CARRIED_WEAPON":
                        materialFloorNoWeaponFailures++;
                        break;
                    case "HARD_FLOOR_FAIL_EMPTY_OR_BRICKED":
                        materialFloorEmptyFailures++;
                        break;
                    case "UNKNOWN":
                        materialFloorUnknownCandidates++;
                        break;
                    default:
                        materialFloorNonemptyCandidates++;
                        break;
                }

                switch (immediateWeaponState)
                {
                    case "READY": immediateReadyCandidates++; break;
                    case "NOT_READY": immediateNotReadyCandidates++; break;
                    case "TRANSITIONAL": immediateTransitionalCandidates++; break;
                    default: immediateUnknownCandidates++; break;
                }

                if (comparableWithinCapabilityCohort && !capabilityGeometryReferenceCandidate)
                {
                    groupCapabilityComparableChallengers++;
                    capabilityComparableChallengers++;
                    switch (capabilityRawGeometryRelationState)
                    {
                        case "REFERENCE_NONWORSE_ALL_PROVEN_AXES":
                            groupCapabilityReferenceNonWorseGeometryChallengers++;
                            capabilityReferenceNonWorseGeometryChallengers++;
                            break;
                        case "CANDIDATE_HAS_COUNTERVAILING_PROVEN_AXIS":
                            groupCapabilityCountervailingGeometryChallengers++;
                            capabilityCountervailingGeometryChallengers++;
                            break;
                        default:
                            groupCapabilityRawGeometryUnresolvedChallengers++;
                            capabilityRawGeometryUnresolvedChallengers++;
                            break;
                    }
                }

                if (comparable && !isGeometryReference)
                {
                    groupComparableChallengers++;
                    comparableChallengers++;
                    switch (rawGeometryRelationState)
                    {
                        case "REFERENCE_NONWORSE_ALL_PROVEN_AXES":
                            groupReferenceNonWorseGeometryChallengers++;
                            referenceNonWorseGeometryChallengers++;
                            break;
                        case "CANDIDATE_HAS_COUNTERVAILING_PROVEN_AXIS":
                            groupCountervailingGeometryChallengers++;
                            countervailingGeometryChallengers++;
                            break;
                        default:
                            groupRawGeometryUnresolvedChallengers++;
                            rawGeometryUnresolvedChallengers++;
                            break;
                    }

                    switch (roleRankRawRelation)
                    {
                        case "AHEAD":
                            groupRoleAheadOfGeometryReferenceChallengers++;
                            roleAheadOfGeometryReferenceChallengers++;
                            break;
                        case "SAME":
                            groupRoleSameAsGeometryReferenceChallengers++;
                            roleSameAsGeometryReferenceChallengers++;
                            break;
                        case "BEHIND":
                            groupRoleBehindGeometryReferenceChallengers++;
                            roleBehindGeometryReferenceChallengers++;
                            break;
                    }
                }

                projected.Add(new CompetitiveCandidateEnvelopeCandidateObservation
                {
                    AxisCandidateId = candidate.AxisCandidateId,
                    BotProfileId = candidate.BotProfileId,
                    OperatorId = candidate.OperatorId,
                    Role = candidate.Role,
                    Specialty = candidate.Specialty,
                    RolePreferenceBand = candidate.RolePreferenceBand,
                    RolePreferenceRank = candidate.RolePreferenceRank,
                    EvidenceEligible = eligible,
                    AdmissionState = AdmissionState(candidate),
                    EvidenceKind = candidate.EvidenceKind,
                    QualificationState = candidate.QualificationState,
                    ThreatEvidenceObserved = candidate.ThreatEvidenceObserved,
                    ThreatEvidenceEnemyId = candidate.ThreatEvidenceEnemyId,
                    ThreatDistanceAlignedToGroup = candidate.ThreatDistanceAlignedToGroup,
                    ThreatDistanceMeters = candidate.ThreatDistanceMeters,
                    ThreatDistanceEvidenceState = candidate.ThreatDistanceEvidenceState,
                    NativeWeaponDistanceAnchorsObserved = candidate.NativeWeaponDistanceAnchorsObserved,
                    NativeWeaponSwitchLowDistanceMeters = candidate.NativeWeaponSwitchLowDistanceMeters,
                    NativeWeaponSwitchFarDistanceMeters = candidate.NativeWeaponSwitchFarDistanceMeters,
                    NativeWeaponDistanceAnchorState = candidate.NativeWeaponDistanceAnchorState,
                    ThreatDistanceVsNativeWeaponWindowRelation = candidate.ThreatDistanceVsNativeWeaponWindowRelation,
                    GeometryReference = isGeometryReference,
                    ComparableToGeometryReference = comparable,
                    AxisCandidateSource = candidate.AxisCandidateSource,
                    AxisSeedBotProfileId = candidate.AxisSeedBotProfileId,
                    IsAxisSeed = candidate.IsAxisSeed,
                    RouteEvidenceKind = candidate.RouteEvidenceKind,
                    EstablishmentRegionRadiusMeters = candidate.EstablishmentRegionRadiusMeters,
                    PathCornerCount = candidate.PathCornerCount,
                    PathLengthMeters = candidate.PathLengthMeters,
                    DirectDistanceMeters = candidate.DirectDistanceMeters,
                    PathRatio = candidate.PathRatio,
                    AxisEvidenceScore = candidate.AxisEvidenceScore,
                    DeltaPathMetersFromGeometryReference = comparable ? candidate.PathLengthMeters - geometryReference!.PathLengthMeters : 0f,
                    DeltaDirectDistanceMetersFromGeometryReference = comparable ? candidate.DirectDistanceMeters - geometryReference!.DirectDistanceMeters : 0f,
                    DeltaPathRatioFromGeometryReference = comparable ? candidate.PathRatio - geometryReference!.PathRatio : 0f,
                    DeltaAxisEvidenceFromGeometryReference = comparable ? candidate.AxisEvidenceScore - geometryReference!.AxisEvidenceScore : 0f,
                    DeltaRoleRankFromGeometryReference = comparable ? candidate.RolePreferenceRank - geometryReference!.RolePreferenceRank : 0,
                    PathLengthRawRelation = pathLengthRawRelation,
                    DirectDistanceRawRelation = directDistanceRawRelation,
                    PathRatioRawRelation = pathRatioRawRelation,
                    AxisEvidenceRawRelation = axisEvidenceRawRelation,
                    RoleRankRawRelation = roleRankRawRelation,
                    RawGeometryRelationState = rawGeometryRelationState,
                    CalibrationCohort = calibrationCohort,
                    MedicalEvidenceReadable = candidate.MedicalEvidenceReadable,
                    HealthPercent = candidate.HealthPercent,
                    HasMedicalDamageObserved = candidate.MedicalEvidenceReadable && HasMedicalDamage(candidate),
                    AnyMedicineUsing = candidate.AnyMedicineUsing,
                    EquipmentEvidenceObserved = candidate.EquipmentEvidenceObserved,
                    HasPrimaryWeapon = candidate.HasPrimaryWeapon,
                    LongWeaponCount = candidate.LongWeaponCount,
                    CompatibleMagazineCount = candidate.CompatibleMagazineCount,
                    CompatibleAmmunitionCount = candidate.CompatibleAmmunitionCount,
                    PerWeaponEquipmentEvidenceObserved = candidate.PerWeaponEquipmentEvidenceObserved,
                    PerWeaponEquipmentObservationReason = candidate.PerWeaponEquipmentObservationReason,
                    CarriedWeaponCount = candidate.CarriedWeaponCount,
                    WeaponsWithRoundsCount = candidate.WeaponsWithRoundsCount,
                    WeaponsWithoutRoundsCount = candidate.WeaponsWithoutRoundsCount,
                    BrickedWeaponCount = candidate.BrickedWeaponCount,
                    SidearmObserved = candidate.SidearmObserved,
                    LiveWeaponCapabilities = candidate.LiveWeaponCapabilities,
                    WeaponReadinessObserved = candidate.WeaponReadinessObserved,
                    WeaponReadinessObservationReason = candidate.WeaponReadinessObservationReason,
                    CurrentWeaponKnown = candidate.CurrentWeaponKnown,
                    CurrentWeaponItemId = candidate.CurrentWeaponItemId,
                    CurrentWeaponTemplateId = candidate.CurrentWeaponTemplateId,
                    WeaponReady = candidate.WeaponReady,
                    ChangingWeapon = candidate.ChangingWeapon,
                    HandsSuspicious = candidate.HandsSuspicious,
                    AmmunitionBoundaryState = ammunitionBoundaryState,
                    ImmediateWeaponState = immediateWeaponState,
                    FlankLeadMaterialFloorShadowState = materialFloorState,
                    HardMaterialFloorFailureObserved = hardMaterialFloorFailureObserved,
                    EquipmentFloorEvidenceProven = floorEvidenceProvenCandidate,
                    EquipmentFloorEligibleShadow = floorEligibleCandidate,
                    EquipmentFloorAdmissionState = floorAdmissionState,
                    ImmediateWeaponCapabilityMatchState = immediateWeaponCapabilityMatchState,
                    ImmediateWeaponCapabilityObserved = immediateWeaponCapabilityObserved,
                    ImmediateWeaponCapability = immediateWeaponCapability,
                    AlternativeCarriedWeaponCount = alternativeCarriedWeaponCount,
                    ImmediateWeaponSightingRangeMeters = immediateWeaponSightingRangeMeters,
                    ThreatDistanceVsImmediateWeaponSightingRangeRelation = threatDistanceVsImmediateWeaponSightingRangeRelation,
                    ImmediateWeaponEffectiveDistanceMeters = immediateWeaponEffectiveDistanceMeters,
                    ThreatDistanceVsImmediateWeaponEffectiveDistanceRelation = threatDistanceVsImmediateWeaponEffectiveDistanceRelation,
                    CapabilityGeometryReference = capabilityGeometryReferenceCandidate,
                    ComparableWithinCapabilityCohort = comparableWithinCapabilityCohort,
                    CapabilityDeltaPathMeters = comparableWithinCapabilityCohort ? candidate.PathLengthMeters - capabilityGeometryReference!.PathLengthMeters : 0f,
                    CapabilityDeltaDirectDistanceMeters = comparableWithinCapabilityCohort ? candidate.DirectDistanceMeters - capabilityGeometryReference!.DirectDistanceMeters : 0f,
                    CapabilityDeltaPathRatio = comparableWithinCapabilityCohort ? candidate.PathRatio - capabilityGeometryReference!.PathRatio : 0f,
                    CapabilityDeltaAxisEvidence = comparableWithinCapabilityCohort ? candidate.AxisEvidenceScore - capabilityGeometryReference!.AxisEvidenceScore : 0f,
                    CapabilityDeltaEstablishmentRegionRadiusMeters = comparableWithinCapabilityCohort ? candidate.EstablishmentRegionRadiusMeters - capabilityGeometryReference!.EstablishmentRegionRadiusMeters : 0f,
                    CapabilityDeltaPathCornerCount = comparableWithinCapabilityCohort ? candidate.PathCornerCount - capabilityGeometryReference!.PathCornerCount : 0,
                    CapabilityPathLengthRawRelation = capabilityPathLengthRawRelation,
                    CapabilityDirectDistanceRawRelation = capabilityDirectDistanceRawRelation,
                    CapabilityPathRatioRawRelation = capabilityPathRatioRawRelation,
                    CapabilityAxisEvidenceRawRelation = capabilityAxisEvidenceRawRelation,
                    CapabilityEstablishmentRadiusRawRelation = capabilityEstablishmentRadiusRawRelation,
                    CapabilityPathCornerCountRawRelation = capabilityPathCornerCountRawRelation,
                    CapabilityAxisSourceRelation = capabilityAxisSourceRelation,
                    CapabilityRouteEvidenceKindRelation = capabilityRouteEvidenceKindRelation,
                    CapabilityRawGeometryRelationState = capabilityRawGeometryRelationState,
                    SituationDifferenceClass = comparable && !isGeometryReference ? "UNRESOLVED_NO_THRESHOLD" : "REFERENCE_OR_NOT_APPLICABLE",
                    EquipmentFitState = "UNCLASSIFIED_RAW_EVIDENCE_ONLY"
                });

                rawCandidates++;
                if (eligible) evidenceEligibleCandidates++; else evidenceUnprovenCandidates++;
                if (comparable) comparableCandidates++;
            }

            var group = new CompetitiveCandidateEnvelopeThreatGroupObservation
            {
                OwnerProfileId = sourceGroup.OwnerProfileId,
                ThreatId = sourceGroup.ThreatId,
                CurrentAxisCandidateId = sourceGroup.CurrentAxisCandidateId,
                CurrentBotProfileId = sourceGroup.CurrentBotProfileId,
                CurrentEvidenceEligible = sourceGroup.CurrentReferenceEligible,
                GeometryReferenceAxisCandidateId = geometryReference?.AxisCandidateId ?? string.Empty,
                GeometryReferenceBotProfileId = geometryReference?.BotProfileId ?? string.Empty,
                CapabilityGeometryReferenceAxisCandidateId = capabilityGeometryReference?.AxisCandidateId ?? string.Empty,
                CapabilityGeometryReferenceBotProfileId = capabilityGeometryReference?.BotProfileId ?? string.Empty,
                CurrentFloorEvidenceProven = currentFloorEvidenceProven,
                CurrentFloorEligibleShadow = currentFloorEligibleShadow,
                RawCandidateCount = allCandidates.Length,
                EvidenceEligibleCandidateCount = eligibleCandidates.Length,
                EvidenceUnprovenCandidateCount = allCandidates.Length - eligibleCandidates.Length,
                FloorEvidenceProvenCandidateCount = floorEvidenceProven.Length,
                FloorEvidenceUnprovenCandidateCount = floorUnproven,
                FloorEligibleCandidateCount = floorEligible.Length,
                FloorHardFailedCandidateCount = floorHardFailed,
                CapabilityGeometryReferenceDiffersFromLegacyReference = capabilityGeometryReferenceDiffersFromLegacy,
                CapabilityComparableChallengerCount = groupCapabilityComparableChallengers,
                CapabilityReferenceNonWorseGeometryChallengerCount = groupCapabilityReferenceNonWorseGeometryChallengers,
                CapabilityCountervailingGeometryChallengerCount = groupCapabilityCountervailingGeometryChallengers,
                CapabilityRawGeometryUnresolvedChallengerCount = groupCapabilityRawGeometryUnresolvedChallengers,
                ComparableChallengerCount = groupComparableChallengers,
                ReferenceNonWorseGeometryChallengerCount = groupReferenceNonWorseGeometryChallengers,
                CountervailingGeometryChallengerCount = groupCountervailingGeometryChallengers,
                RawGeometryUnresolvedChallengerCount = groupRawGeometryUnresolvedChallengers,
                RoleAheadOfGeometryReferenceChallengerCount = groupRoleAheadOfGeometryReferenceChallengers,
                RoleSameAsGeometryReferenceChallengerCount = groupRoleSameAsGeometryReferenceChallengers,
                RoleBehindGeometryReferenceChallengerCount = groupRoleBehindGeometryReferenceChallengers,
                EnvelopeState = envelopeState,
                CapabilityCohortState = capabilityCohortState,
                Candidates = projected.ToArray()
            };

            groups.Add(group);
            EmitDetailIfChanged(group);
        }

        var next = new CompetitiveCandidateEnvelopeShadowSnapshot
        {
            MapId = source.MapId,
            TopologyRevision = source.TopologyRevision,
            TopologyContentHash = source.TopologyContentHash,
            TacticalFitnessPublishedAtUtc = source.PublishedAtUtc,
            PublishedAtUtc = now,
            ThreatGroups = groups.ToArray()
        };

        lock (Sync)
        {
            current = next;
        }

        publicationsTotal++;
        groupsObservedTotal += groups.Count;
        rawCandidatesTotal += rawCandidates;
        evidenceEligibleCandidatesTotal += evidenceEligibleCandidates;
        evidenceUnprovenCandidatesTotal += evidenceUnprovenCandidates;
        comparableCandidatesTotal += comparableCandidates;
        comparableChallengersTotal += comparableChallengers;
        referenceNonWorseGeometryChallengersTotal += referenceNonWorseGeometryChallengers;
        countervailingGeometryChallengersTotal += countervailingGeometryChallengers;
        rawGeometryUnresolvedChallengersTotal += rawGeometryUnresolvedChallengers;
        roleAheadOfGeometryReferenceChallengersTotal += roleAheadOfGeometryReferenceChallengers;
        roleSameAsGeometryReferenceChallengersTotal += roleSameAsGeometryReferenceChallengers;
        roleBehindGeometryReferenceChallengersTotal += roleBehindGeometryReferenceChallengers;
        groupsWithoutEvidenceEligibleCandidateTotal += groupsWithoutEligible;
        singleEvidenceEligibleCandidateGroupsTotal += singleEligibleGroups;
        multiEvidenceEligibleCandidateGroupsTotal += multiEligibleGroups;
        materialFloorUnknownCandidatesTotal += materialFloorUnknownCandidates;
        materialFloorNoWeaponFailuresTotal += materialFloorNoWeaponFailures;
        materialFloorEmptyFailuresTotal += materialFloorEmptyFailures;
        materialFloorNonemptyCandidatesTotal += materialFloorNonemptyCandidates;
        immediateReadyCandidatesTotal += immediateReadyCandidates;
        immediateNotReadyCandidatesTotal += immediateNotReadyCandidates;
        immediateTransitionalCandidatesTotal += immediateTransitionalCandidates;
        immediateUnknownCandidatesTotal += immediateUnknownCandidates;
        floorEvidenceProvenCandidatesTotal += floorEvidenceProvenCandidates;
        floorEvidenceUnprovenCandidatesTotal += floorEvidenceUnprovenCandidates;
        floorEligibleCandidatesTotal += floorEligibleCandidates;
        floorHardFailedCandidatesTotal += floorHardFailedCandidates;
        groupsWithoutFloorEligibleCandidateTotal += groupsWithoutFloorEligibleCandidate;
        groupsAllFloorFailedTotal += groupsAllFloorFailed;
        groupsFloorViabilityUnprovenTotal += groupsFloorViabilityUnproven;
        capabilityGeometryReferenceChangedGroupsTotal += capabilityGeometryReferenceChangedGroups;
        capabilityComparableChallengersTotal += capabilityComparableChallengers;
        capabilityReferenceNonWorseGeometryChallengersTotal += capabilityReferenceNonWorseGeometryChallengers;
        capabilityCountervailingGeometryChallengersTotal += capabilityCountervailingGeometryChallengers;
        capabilityRawGeometryUnresolvedChallengersTotal += capabilityRawGeometryUnresolvedChallengers;
        immediateWeaponExactMatchesTotal += immediateWeaponExactMatches;
        immediateWeaponTemplateFallbackMatchesTotal += immediateWeaponTemplateFallbackMatches;
        immediateWeaponCapabilityUnresolvedTotal += immediateWeaponCapabilityUnresolved;
        candidatesWithAlternativeCarriedWeaponsTotal += candidatesWithAlternativeCarriedWeapons;

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                "TC_COMPETITIVE_CANDIDATE_ENVELOPE_SHADOW_BOOT contribution=FlankLead; source=tactical_fitness_shadow; observationOnly=true; competitiveMembershipResolved=false; shadowWinner=false; materialityThresholds=false; rawRelationTolerance=false; rawGeometryCohorts=true; geometryCohortsResolveMateriality=false; perWeaponEquipmentRawEvidence=true; equipmentBoundarySemantics=true; emptyOrBrickedBoundary=true; reserveBandsCalibrated=false; equipmentHardFloorShadowOnly=true; floorProvenCapabilityCohort=true; floorUnknownFailsClosed=true; floorCohortMutatesEvidenceEligibility=false; floorCohortMutatesAssignment=false; capabilityCohortGeometryCalibration=true; routeStructureRawEvidence=true; routeStructureMaterialityClassification=false; immediateVsAvailableWeaponCapability=true; currentWeaponExactItemMatchPreferred=true; uniqueTemplateFallbackBounded=true; immediateCapabilityAffectsFloor=false; canonicalEftWeaponClassEvidence=true; weaponClassFitMapping=false; canonicalWeaponFamilyNormalization=true; weaponFamilyQualificationFailClosed=true; weaponFamilyFitMapping=false; mountedSightZoomProvenance=true; eftMagnificationBoundaryGtOne=true; highOpticThresholdClassification=false; weaponRangeRegimeFitMapping=false; candidateThreatDistanceProvenance=true; nativeEftWeaponDistanceAnchorsProvenance=true; nativeWeaponSwitchWindowRelation=true; immediateWeaponSightingRangeRelation=true; immediateWeaponSightingRangeFitMapping=false; immediateWeaponSightingRangeRelationAffectsAdmission=false; eftEffectiveDistanceProvenance=true; eftEffectiveDistanceExecutableCeiling=false; sainEngagementDistanceConsumedByTc=false; availableWeaponAutoSwitchAssumed=false; immediateWeaponEffectiveDistanceRelation=true; immediateWeaponEffectiveDistanceFitMapping=false; immediateWeaponEffectiveDistanceRelationAffectsAdmission=false; threatRangeBandClassification=false; threatDistanceAffectsAdmission=false; nativeWeaponDistanceRelationAffectsAdmission=false; lootTotalScoreConsumedByTc=false; ammunitionThresholdClassification=false; equipmentFitClassification=false; behavioralBiasApplied=false; hysteresisApplied=false; candidateSpecificThreatExposure=false; assignmentMutation=false; pathfinding=false; newCapabilityReaders=true; unknownMandatoryPrerequisite=viability_unproven; detailFormatting=semantic_fingerprint_before_candidate_token; volatileEvidenceFormatting=compact_when_semantic_unchanged; noAssignmentAllowed=true");
        }

        if (now >= nextSummaryAtUtc)
        {
            nextSummaryAtUtc = now + SummaryLogInterval;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_COMPETITIVE_CANDIDATE_ENVELOPE_SHADOW_SUMMARY map={Safe(next.MapId)}; topologyRevision={next.TopologyRevision}; sourceTacticalFitnessAt={Timestamp(next.TacticalFitnessPublishedAtUtc)}; groups={groups.Count}; rawCandidates={rawCandidates}; evidenceEligibleCandidates={evidenceEligibleCandidates}; evidenceUnprovenCandidates={evidenceUnprovenCandidates}; comparableCandidates={comparableCandidates}; comparableChallengers={comparableChallengers}; referenceNonWorseGeometryChallengers={referenceNonWorseGeometryChallengers}; countervailingGeometryChallengers={countervailingGeometryChallengers}; rawGeometryUnresolvedChallengers={rawGeometryUnresolvedChallengers}; roleAheadOfGeometryReferenceChallengers={roleAheadOfGeometryReferenceChallengers}; roleSameAsGeometryReferenceChallengers={roleSameAsGeometryReferenceChallengers}; roleBehindGeometryReferenceChallengers={roleBehindGeometryReferenceChallengers}; groupsWithoutEvidenceEligibleCandidate={groupsWithoutEligible}; singleEvidenceEligibleCandidateGroups={singleEligibleGroups}; multiEvidenceEligibleCandidateGroups={multiEligibleGroups}; materialFloorUnknownCandidates={materialFloorUnknownCandidates}; materialFloorNoWeaponFailures={materialFloorNoWeaponFailures}; materialFloorEmptyFailures={materialFloorEmptyFailures}; materialFloorNonemptyCandidates={materialFloorNonemptyCandidates}; immediateReadyCandidates={immediateReadyCandidates}; immediateNotReadyCandidates={immediateNotReadyCandidates}; immediateTransitionalCandidates={immediateTransitionalCandidates}; immediateUnknownCandidates={immediateUnknownCandidates}; publicationsTotal={publicationsTotal}; groupsObservedTotal={groupsObservedTotal}; rawCandidatesTotal={rawCandidatesTotal}; evidenceEligibleCandidatesTotal={evidenceEligibleCandidatesTotal}; evidenceUnprovenCandidatesTotal={evidenceUnprovenCandidatesTotal}; comparableCandidatesTotal={comparableCandidatesTotal}; comparableChallengersTotal={comparableChallengersTotal}; referenceNonWorseGeometryChallengersTotal={referenceNonWorseGeometryChallengersTotal}; countervailingGeometryChallengersTotal={countervailingGeometryChallengersTotal}; rawGeometryUnresolvedChallengersTotal={rawGeometryUnresolvedChallengersTotal}; roleAheadOfGeometryReferenceChallengersTotal={roleAheadOfGeometryReferenceChallengersTotal}; roleSameAsGeometryReferenceChallengersTotal={roleSameAsGeometryReferenceChallengersTotal}; roleBehindGeometryReferenceChallengersTotal={roleBehindGeometryReferenceChallengersTotal}; groupsWithoutEvidenceEligibleCandidateTotal={groupsWithoutEvidenceEligibleCandidateTotal}; singleEvidenceEligibleCandidateGroupsTotal={singleEvidenceEligibleCandidateGroupsTotal}; multiEvidenceEligibleCandidateGroupsTotal={multiEvidenceEligibleCandidateGroupsTotal}; materialFloorUnknownCandidatesTotal={materialFloorUnknownCandidatesTotal}; materialFloorNoWeaponFailuresTotal={materialFloorNoWeaponFailuresTotal}; materialFloorEmptyFailuresTotal={materialFloorEmptyFailuresTotal}; materialFloorNonemptyCandidatesTotal={materialFloorNonemptyCandidatesTotal}; immediateReadyCandidatesTotal={immediateReadyCandidatesTotal}; immediateNotReadyCandidatesTotal={immediateNotReadyCandidatesTotal}; immediateTransitionalCandidatesTotal={immediateTransitionalCandidatesTotal}; immediateUnknownCandidatesTotal={immediateUnknownCandidatesTotal}; floorEvidenceProvenCandidates={floorEvidenceProvenCandidates}; floorEvidenceUnprovenCandidates={floorEvidenceUnprovenCandidates}; floorEligibleCandidates={floorEligibleCandidates}; floorHardFailedCandidates={floorHardFailedCandidates}; groupsWithoutFloorEligibleCandidate={groupsWithoutFloorEligibleCandidate}; groupsAllFloorFailed={groupsAllFloorFailed}; groupsFloorViabilityUnproven={groupsFloorViabilityUnproven}; floorEvidenceProvenCandidatesTotal={floorEvidenceProvenCandidatesTotal}; floorEvidenceUnprovenCandidatesTotal={floorEvidenceUnprovenCandidatesTotal}; floorEligibleCandidatesTotal={floorEligibleCandidatesTotal}; floorHardFailedCandidatesTotal={floorHardFailedCandidatesTotal}; groupsWithoutFloorEligibleCandidateTotal={groupsWithoutFloorEligibleCandidateTotal}; groupsAllFloorFailedTotal={groupsAllFloorFailedTotal}; groupsFloorViabilityUnprovenTotal={groupsFloorViabilityUnprovenTotal}; capabilityGeometryReferenceChangedGroups={capabilityGeometryReferenceChangedGroups}; capabilityComparableChallengers={capabilityComparableChallengers}; capabilityReferenceNonWorseGeometryChallengers={capabilityReferenceNonWorseGeometryChallengers}; capabilityCountervailingGeometryChallengers={capabilityCountervailingGeometryChallengers}; capabilityRawGeometryUnresolvedChallengers={capabilityRawGeometryUnresolvedChallengers}; capabilityGeometryReferenceChangedGroupsTotal={capabilityGeometryReferenceChangedGroupsTotal}; capabilityComparableChallengersTotal={capabilityComparableChallengersTotal}; capabilityReferenceNonWorseGeometryChallengersTotal={capabilityReferenceNonWorseGeometryChallengersTotal}; capabilityCountervailingGeometryChallengersTotal={capabilityCountervailingGeometryChallengersTotal}; capabilityRawGeometryUnresolvedChallengersTotal={capabilityRawGeometryUnresolvedChallengersTotal}; immediateWeaponExactMatches={immediateWeaponExactMatches}; immediateWeaponTemplateFallbackMatches={immediateWeaponTemplateFallbackMatches}; immediateWeaponCapabilityUnresolved={immediateWeaponCapabilityUnresolved}; candidatesWithAlternativeCarriedWeapons={candidatesWithAlternativeCarriedWeapons}; immediateWeaponExactMatchesTotal={immediateWeaponExactMatchesTotal}; immediateWeaponTemplateFallbackMatchesTotal={immediateWeaponTemplateFallbackMatchesTotal}; immediateWeaponCapabilityUnresolvedTotal={immediateWeaponCapabilityUnresolvedTotal}; candidatesWithAlternativeCarriedWeaponsTotal={candidatesWithAlternativeCarriedWeaponsTotal}; detailFormattingSkipsTotal={detailFormattingSkipsTotal}; volatileEvidencePublicationsTotal={volatileEvidencePublicationsTotal}; competitiveMembershipResolved=false; shadowWinner=false; materialityThresholds=false; rawRelationTolerance=false; geometryCohortsResolveMateriality=false; perWeaponEquipmentRawEvidence=true; mountedSightZoomProvenance=true; highOpticThresholdClassification=false; candidateThreatDistanceProvenance=true; nativeEftWeaponDistanceAnchorsProvenance=true; nativeWeaponSwitchWindowRelation=true; immediateWeaponSightingRangeRelation=true; immediateWeaponSightingRangeFitMapping=false; immediateWeaponSightingRangeRelationAffectsAdmission=false; eftEffectiveDistanceProvenance=true; eftEffectiveDistanceExecutableCeiling=false; sainEngagementDistanceConsumedByTc=false; availableWeaponAutoSwitchAssumed=false; immediateWeaponEffectiveDistanceRelation=true; immediateWeaponEffectiveDistanceFitMapping=false; immediateWeaponEffectiveDistanceRelationAffectsAdmission=false; threatRangeBandClassification=false; threatDistanceAffectsAdmission=false; nativeWeaponDistanceRelationAffectsAdmission=false; equipmentBoundarySemantics=true; emptyOrBrickedBoundary=true; reserveBandsCalibrated=false; equipmentHardFloorShadowOnly=true; floorProvenCapabilityCohort=true; floorUnknownFailsClosed=true; floorCohortMutatesEvidenceEligibility=false; floorCohortMutatesAssignment=false; capabilityCohortGeometryCalibration=true; routeStructureRawEvidence=true; routeStructureMaterialityClassification=false; lootTotalScoreConsumedByTc=false; ammunitionThresholdClassification=false; equipmentFitClassification=false; behavioralBiasApplied=false; hysteresisApplied=false; candidateSpecificThreatExposure=false; assignmentMutation=false; pathfinding=false; observationOnly=true");
        }
    }

    public static bool TryGetSnapshot(out CompetitiveCandidateEnvelopeShadowSnapshot snapshot)
    {
        lock (Sync)
        {
            snapshot = current;
            return snapshot.IsAvailable;
        }
    }

    internal static void InvalidateFromSource()
    {
        lock (Sync)
        {
            current = CompetitiveCandidateEnvelopeShadowSnapshot.Empty;
        }
        LastDetailFingerprintByThreatGroup.Clear();
        LastVolatileEvidenceFingerprintByThreatGroup.Clear();
        lastSourcePublicationAtUtc = DateTimeOffset.MinValue;
    }

    internal static void ResetFromSource()
    {
        lock (Sync)
        {
            current = CompetitiveCandidateEnvelopeShadowSnapshot.Empty;
        }
        LastDetailFingerprintByThreatGroup.Clear();
        LastVolatileEvidenceFingerprintByThreatGroup.Clear();
        lastSourcePublicationAtUtc = DateTimeOffset.MinValue;
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        publicationsTotal = 0;
        groupsObservedTotal = 0;
        rawCandidatesTotal = 0;
        evidenceEligibleCandidatesTotal = 0;
        evidenceUnprovenCandidatesTotal = 0;
        comparableCandidatesTotal = 0;
        comparableChallengersTotal = 0;
        referenceNonWorseGeometryChallengersTotal = 0;
        countervailingGeometryChallengersTotal = 0;
        rawGeometryUnresolvedChallengersTotal = 0;
        roleAheadOfGeometryReferenceChallengersTotal = 0;
        roleSameAsGeometryReferenceChallengersTotal = 0;
        roleBehindGeometryReferenceChallengersTotal = 0;
        groupsWithoutEvidenceEligibleCandidateTotal = 0;
        singleEvidenceEligibleCandidateGroupsTotal = 0;
        multiEvidenceEligibleCandidateGroupsTotal = 0;
        materialFloorUnknownCandidatesTotal = 0;
        materialFloorNoWeaponFailuresTotal = 0;
        materialFloorEmptyFailuresTotal = 0;
        materialFloorNonemptyCandidatesTotal = 0;
        immediateReadyCandidatesTotal = 0;
        immediateNotReadyCandidatesTotal = 0;
        immediateTransitionalCandidatesTotal = 0;
        immediateUnknownCandidatesTotal = 0;
        floorEvidenceProvenCandidatesTotal = 0;
        floorEvidenceUnprovenCandidatesTotal = 0;
        floorEligibleCandidatesTotal = 0;
        floorHardFailedCandidatesTotal = 0;
        groupsWithoutFloorEligibleCandidateTotal = 0;
        groupsAllFloorFailedTotal = 0;
        groupsFloorViabilityUnprovenTotal = 0;
        capabilityGeometryReferenceChangedGroupsTotal = 0;
        capabilityComparableChallengersTotal = 0;
        capabilityReferenceNonWorseGeometryChallengersTotal = 0;
        capabilityCountervailingGeometryChallengersTotal = 0;
        capabilityRawGeometryUnresolvedChallengersTotal = 0;
        immediateWeaponExactMatchesTotal = 0;
        immediateWeaponTemplateFallbackMatchesTotal = 0;
        immediateWeaponCapabilityUnresolvedTotal = 0;
        candidatesWithAlternativeCarriedWeaponsTotal = 0;
        detailFormattingSkipsTotal = 0;
        volatileEvidencePublicationsTotal = 0;
    }

    private static void EmitDetailIfChanged(CompetitiveCandidateEnvelopeThreatGroupObservation group)
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
                    return $"TC_COMPETITIVE_CANDIDATE_ENVELOPE_SHADOW_VOLATILE owner={Safe(group.OwnerProfileId)}; threat={Safe(group.ThreatId)}; candidates={Safe(volatileCandidates)}; semanticDetailUnchanged=true; observationOnly=true";
                });
            }
            return;
        }
        LastDetailFingerprintByThreatGroup[key] = fingerprint;
        LastVolatileEvidenceFingerprintByThreatGroup[key] = volatileFingerprint;

        // Deep per-candidate payloads are trace-only and lazily formatted so Diagnostic mode
        // preserves the shadow publication cadence without paying the large string/logging cost.
        VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
        {
            string candidates = string.Join("^", group.Candidates.Select(CandidateToken));
            return $"TC_COMPETITIVE_CANDIDATE_ENVELOPE_SHADOW_DETAIL owner={Safe(group.OwnerProfileId)}; threat={Safe(group.ThreatId)}; currentAxis={Safe(group.CurrentAxisCandidateId)}; currentBot={Safe(group.CurrentBotProfileId)}; currentEvidenceEligible={Bool(group.CurrentEvidenceEligible)}; geometryReferenceAxis={Safe(group.GeometryReferenceAxisCandidateId)}; geometryReferenceBot={Safe(group.GeometryReferenceBotProfileId)}; capabilityGeometryReferenceAxis={Safe(group.CapabilityGeometryReferenceAxisCandidateId)}; capabilityGeometryReferenceBot={Safe(group.CapabilityGeometryReferenceBotProfileId)}; currentFloorEvidenceProven={Bool(group.CurrentFloorEvidenceProven)}; currentFloorEligibleShadow={Bool(group.CurrentFloorEligibleShadow)}; rawCandidates={group.RawCandidateCount}; evidenceEligibleCandidates={group.EvidenceEligibleCandidateCount}; evidenceUnprovenCandidates={group.EvidenceUnprovenCandidateCount}; floorEvidenceProvenCandidates={group.FloorEvidenceProvenCandidateCount}; floorEvidenceUnprovenCandidates={group.FloorEvidenceUnprovenCandidateCount}; floorEligibleCandidates={group.FloorEligibleCandidateCount}; floorHardFailedCandidates={group.FloorHardFailedCandidateCount}; capabilityGeometryReferenceDiffersFromLegacy={Bool(group.CapabilityGeometryReferenceDiffersFromLegacyReference)}; capabilityComparableChallengers={group.CapabilityComparableChallengerCount}; capabilityReferenceNonWorseGeometryChallengers={group.CapabilityReferenceNonWorseGeometryChallengerCount}; capabilityCountervailingGeometryChallengers={group.CapabilityCountervailingGeometryChallengerCount}; capabilityRawGeometryUnresolvedChallengers={group.CapabilityRawGeometryUnresolvedChallengerCount}; comparableChallengers={group.ComparableChallengerCount}; referenceNonWorseGeometryChallengers={group.ReferenceNonWorseGeometryChallengerCount}; countervailingGeometryChallengers={group.CountervailingGeometryChallengerCount}; rawGeometryUnresolvedChallengers={group.RawGeometryUnresolvedChallengerCount}; roleAheadOfGeometryReferenceChallengers={group.RoleAheadOfGeometryReferenceChallengerCount}; roleSameAsGeometryReferenceChallengers={group.RoleSameAsGeometryReferenceChallengerCount}; roleBehindGeometryReferenceChallengers={group.RoleBehindGeometryReferenceChallengerCount}; envelopeState={Safe(group.EnvelopeState)}; capabilityCohortState={Safe(group.CapabilityCohortState)}; candidates={Safe(candidates)}; competitiveMembershipResolved=false; shadowWinner=false; materialityThreshold=false; rawRelationTolerance=false; geometryCohortsResolveMateriality=false; perWeaponEquipmentRawEvidence=true; mountedSightZoomProvenance=true; highOpticThresholdClassification=false; immediateWeaponSightingRangeRelation=true; immediateWeaponSightingRangeFitMapping=false; eftEffectiveDistanceProvenance=true; eftEffectiveDistanceExecutableCeiling=false; sainEngagementDistanceConsumedByTc=false; availableWeaponAutoSwitchAssumed=false; immediateWeaponEffectiveDistanceRelation=true; immediateWeaponEffectiveDistanceFitMapping=false; equipmentBoundarySemantics=true; emptyOrBrickedBoundary=true; reserveBandsCalibrated=false; equipmentHardFloorShadowOnly=true; floorProvenCapabilityCohort=true; floorUnknownFailsClosed=true; floorCohortMutatesEvidenceEligibility=false; floorCohortMutatesAssignment=false; capabilityCohortGeometryCalibration=true; routeStructureRawEvidence=true; routeStructureMaterialityClassification=false; lootTotalScoreConsumedByTc=false; ammunitionThresholdClassification=false; equipmentFitClassification=false; behavioralBiasApplied=false; hysteresisApplied=false; candidateSpecificThreatExposure=false; executableSelection=false; assignmentMutation=false; observationOnly=true";
        });
    }

    private static ulong ComputeDetailFingerprint(CompetitiveCandidateEnvelopeThreatGroupObservation group)
    {
        ulong hash = DetailFingerprintOffset;
        Mix(ref hash, group.OwnerProfileId);
        Mix(ref hash, group.ThreatId);
        Mix(ref hash, group.CurrentAxisCandidateId);
        Mix(ref hash, group.CurrentBotProfileId);
        Mix(ref hash, group.CurrentEvidenceEligible);
        Mix(ref hash, group.GeometryReferenceAxisCandidateId);
        Mix(ref hash, group.GeometryReferenceBotProfileId);
        Mix(ref hash, group.CapabilityGeometryReferenceAxisCandidateId);
        Mix(ref hash, group.CapabilityGeometryReferenceBotProfileId);
        Mix(ref hash, group.CurrentFloorEvidenceProven);
        Mix(ref hash, group.CurrentFloorEligibleShadow);
        Mix(ref hash, group.RawCandidateCount);
        Mix(ref hash, group.EvidenceEligibleCandidateCount);
        Mix(ref hash, group.EvidenceUnprovenCandidateCount);
        Mix(ref hash, group.FloorEvidenceProvenCandidateCount);
        Mix(ref hash, group.FloorEvidenceUnprovenCandidateCount);
        Mix(ref hash, group.FloorEligibleCandidateCount);
        Mix(ref hash, group.FloorHardFailedCandidateCount);
        Mix(ref hash, group.CapabilityGeometryReferenceDiffersFromLegacyReference);
        Mix(ref hash, group.CapabilityComparableChallengerCount);
        Mix(ref hash, group.CapabilityReferenceNonWorseGeometryChallengerCount);
        Mix(ref hash, group.CapabilityCountervailingGeometryChallengerCount);
        Mix(ref hash, group.CapabilityRawGeometryUnresolvedChallengerCount);
        Mix(ref hash, group.ComparableChallengerCount);
        Mix(ref hash, group.ReferenceNonWorseGeometryChallengerCount);
        Mix(ref hash, group.CountervailingGeometryChallengerCount);
        Mix(ref hash, group.RawGeometryUnresolvedChallengerCount);
        Mix(ref hash, group.RoleAheadOfGeometryReferenceChallengerCount);
        Mix(ref hash, group.RoleSameAsGeometryReferenceChallengerCount);
        Mix(ref hash, group.RoleBehindGeometryReferenceChallengerCount);
        Mix(ref hash, group.EnvelopeState);
        Mix(ref hash, group.CapabilityCohortState);
        Mix(ref hash, group.Candidates.Count);
        foreach (CompetitiveCandidateEnvelopeCandidateObservation item in group.Candidates)
        {
            Mix(ref hash, item.AxisCandidateId);
            Mix(ref hash, item.BotProfileId);
            Mix(ref hash, item.Role);
            Mix(ref hash, item.Specialty);
            Mix(ref hash, item.RolePreferenceBand);
            Mix(ref hash, item.RolePreferenceRank);
            Mix(ref hash, item.EvidenceEligible);
            Mix(ref hash, item.AdmissionState);
            Mix(ref hash, item.EvidenceKind);
            Mix(ref hash, item.QualificationState);
            Mix(ref hash, item.ThreatEvidenceObserved);
            Mix(ref hash, item.ThreatEvidenceEnemyId);
            Mix(ref hash, item.ThreatDistanceAlignedToGroup);
            Mix(ref hash, item.ThreatDistanceEvidenceState);
            Mix(ref hash, item.NativeWeaponDistanceAnchorsObserved);
            Mix(ref hash, item.NativeWeaponSwitchLowDistanceMeters);
            Mix(ref hash, item.NativeWeaponSwitchFarDistanceMeters);
            Mix(ref hash, item.NativeWeaponDistanceAnchorState);
            Mix(ref hash, item.ThreatDistanceVsNativeWeaponWindowRelation);
            Mix(ref hash, item.GeometryReference);
            Mix(ref hash, item.ComparableToGeometryReference);
            Mix(ref hash, item.AxisCandidateSource);
            Mix(ref hash, item.AxisSeedBotProfileId);
            Mix(ref hash, item.IsAxisSeed);
            Mix(ref hash, item.RouteEvidenceKind);
            Mix(ref hash, item.DeltaRoleRankFromGeometryReference);
            Mix(ref hash, item.PathLengthRawRelation);
            Mix(ref hash, item.DirectDistanceRawRelation);
            Mix(ref hash, item.PathRatioRawRelation);
            Mix(ref hash, item.AxisEvidenceRawRelation);
            Mix(ref hash, item.RoleRankRawRelation);
            Mix(ref hash, item.RawGeometryRelationState);
            Mix(ref hash, item.CalibrationCohort);
            Mix(ref hash, item.MedicalEvidenceReadable);
            Mix(ref hash, item.HealthPercent);
            Mix(ref hash, item.HasMedicalDamageObserved);
            Mix(ref hash, item.AnyMedicineUsing);
            Mix(ref hash, item.EquipmentEvidenceObserved);
            Mix(ref hash, item.HasPrimaryWeapon);
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
            Mix(ref hash, item.CurrentWeaponKnown);
            Mix(ref hash, item.CurrentWeaponItemId);
            Mix(ref hash, item.CurrentWeaponTemplateId);
            Mix(ref hash, item.WeaponReady);
            Mix(ref hash, item.ChangingWeapon);
            Mix(ref hash, item.HandsSuspicious);
            Mix(ref hash, item.AmmunitionBoundaryState);
            Mix(ref hash, item.ImmediateWeaponState);
            Mix(ref hash, item.FlankLeadMaterialFloorShadowState);
            Mix(ref hash, item.HardMaterialFloorFailureObserved);
            Mix(ref hash, item.EquipmentFloorEvidenceProven);
            Mix(ref hash, item.EquipmentFloorEligibleShadow);
            Mix(ref hash, item.EquipmentFloorAdmissionState);
            Mix(ref hash, item.ImmediateWeaponCapabilityMatchState);
            Mix(ref hash, item.ImmediateWeaponCapabilityObserved);
            MixWeaponCapability(ref hash, item.ImmediateWeaponCapability);
            Mix(ref hash, item.AlternativeCarriedWeaponCount);
            Mix(ref hash, item.ImmediateWeaponSightingRangeMeters);
            Mix(ref hash, item.ThreatDistanceVsImmediateWeaponSightingRangeRelation);
            Mix(ref hash, item.ImmediateWeaponEffectiveDistanceMeters);
            Mix(ref hash, item.ThreatDistanceVsImmediateWeaponEffectiveDistanceRelation);
            Mix(ref hash, item.CapabilityGeometryReference);
            Mix(ref hash, item.ComparableWithinCapabilityCohort);
            Mix(ref hash, item.CapabilityPathLengthRawRelation);
            Mix(ref hash, item.CapabilityDirectDistanceRawRelation);
            Mix(ref hash, item.CapabilityPathRatioRawRelation);
            Mix(ref hash, item.CapabilityAxisEvidenceRawRelation);
            Mix(ref hash, item.CapabilityEstablishmentRadiusRawRelation);
            Mix(ref hash, item.CapabilityPathCornerCountRawRelation);
            Mix(ref hash, item.CapabilityAxisSourceRelation);
            Mix(ref hash, item.CapabilityRouteEvidenceKindRelation);
            Mix(ref hash, item.CapabilityRawGeometryRelationState);
            Mix(ref hash, item.SituationDifferenceClass);
            Mix(ref hash, item.EquipmentFitState);
        }
        return hash;
    }

    private static ulong ComputeVolatileEvidenceFingerprint(CompetitiveCandidateEnvelopeThreatGroupObservation group)
    {
        ulong hash = DetailFingerprintOffset;
        Mix(ref hash, group.OwnerProfileId);
        Mix(ref hash, group.ThreatId);
        Mix(ref hash, group.Candidates.Count);
        foreach (CompetitiveCandidateEnvelopeCandidateObservation item in group.Candidates)
        {
            Mix(ref hash, item.AxisCandidateId);
            Mix(ref hash, item.BotProfileId);
            Mix(ref hash, item.ThreatDistanceMeters);
            Mix(ref hash, item.EstablishmentRegionRadiusMeters);
            Mix(ref hash, item.PathCornerCount);
            Mix(ref hash, item.PathLengthMeters);
            Mix(ref hash, item.DirectDistanceMeters);
            Mix(ref hash, item.PathRatio);
            Mix(ref hash, item.AxisEvidenceScore);
            Mix(ref hash, item.DeltaPathMetersFromGeometryReference);
            Mix(ref hash, item.DeltaDirectDistanceMetersFromGeometryReference);
            Mix(ref hash, item.DeltaPathRatioFromGeometryReference);
            Mix(ref hash, item.DeltaAxisEvidenceFromGeometryReference);
            Mix(ref hash, item.CapabilityDeltaPathMeters);
            Mix(ref hash, item.CapabilityDeltaDirectDistanceMeters);
            Mix(ref hash, item.CapabilityDeltaPathRatio);
            Mix(ref hash, item.CapabilityDeltaAxisEvidence);
            Mix(ref hash, item.CapabilityDeltaEstablishmentRegionRadiusMeters);
            Mix(ref hash, item.CapabilityDeltaPathCornerCount);
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

    private static string VolatileEvidenceToken(CompetitiveCandidateEnvelopeCandidateObservation item)
        => string.Join(",",
            "axis=" + Normalize(item.AxisCandidateId),
            "bot=" + Normalize(item.BotProfileId),
            "threatDistance=" + F(item.ThreatDistanceMeters),
            "establishmentRadius=" + F(item.EstablishmentRegionRadiusMeters),
            "pathCorners=" + item.PathCornerCount.ToString(CultureInfo.InvariantCulture),
            "path=" + F(item.PathLengthMeters),
            "direct=" + F(item.DirectDistanceMeters),
            "ratio=" + F(item.PathRatio),
            "axisEvidence=" + F(item.AxisEvidenceScore),
            "dPath=" + F(item.DeltaPathMetersFromGeometryReference),
            "dDirect=" + F(item.DeltaDirectDistanceMetersFromGeometryReference),
            "dRatio=" + F(item.DeltaPathRatioFromGeometryReference),
            "dAxisEvidence=" + F(item.DeltaAxisEvidenceFromGeometryReference),
            "capDPath=" + F(item.CapabilityDeltaPathMeters),
            "capDDirect=" + F(item.CapabilityDeltaDirectDistanceMeters),
            "capDRatio=" + F(item.CapabilityDeltaPathRatio),
            "capDAxisEvidence=" + F(item.CapabilityDeltaAxisEvidence),
            "capDEstablishmentRadius=" + F(item.CapabilityDeltaEstablishmentRegionRadiusMeters),
            "capDPathCorners=" + item.CapabilityDeltaPathCornerCount.ToString(CultureInfo.InvariantCulture));

    private static string CandidateToken(CompetitiveCandidateEnvelopeCandidateObservation item)
        => string.Join(",",
            "axis=" + Normalize(item.AxisCandidateId),
            "bot=" + Normalize(item.BotProfileId),
            "role=" + Normalize(item.Role),
            "specialty=" + Normalize(item.Specialty),
            "band=" + Normalize(item.RolePreferenceBand),
            "rank=" + item.RolePreferenceRank.ToString(CultureInfo.InvariantCulture),
            "eligible=" + Bool(item.EvidenceEligible),
            "admission=" + Normalize(item.AdmissionState),
            "evidence=" + Normalize(item.EvidenceKind),
            "qualification=" + Normalize(item.QualificationState),
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
            "geometryReference=" + Bool(item.GeometryReference),
            "comparable=" + Bool(item.ComparableToGeometryReference),
            "axisSource=" + Normalize(item.AxisCandidateSource),
            "axisSeedBot=" + Normalize(item.AxisSeedBotProfileId),
            "isAxisSeed=" + Bool(item.IsAxisSeed),
            "routeEvidence=" + Normalize(item.RouteEvidenceKind),
            "establishmentRadius=" + F(item.EstablishmentRegionRadiusMeters),
            "pathCorners=" + item.PathCornerCount.ToString(CultureInfo.InvariantCulture),
            "path=" + F(item.PathLengthMeters),
            "direct=" + F(item.DirectDistanceMeters),
            "ratio=" + F(item.PathRatio),
            "axisEvidence=" + F(item.AxisEvidenceScore),
            "dPath=" + F(item.DeltaPathMetersFromGeometryReference),
            "dDirect=" + F(item.DeltaDirectDistanceMetersFromGeometryReference),
            "dRatio=" + F(item.DeltaPathRatioFromGeometryReference),
            "dAxisEvidence=" + F(item.DeltaAxisEvidenceFromGeometryReference),
            "dRoleRank=" + item.DeltaRoleRankFromGeometryReference.ToString(CultureInfo.InvariantCulture),
            "pathRelation=" + Normalize(item.PathLengthRawRelation),
            "directRelation=" + Normalize(item.DirectDistanceRawRelation),
            "ratioRelation=" + Normalize(item.PathRatioRawRelation),
            "axisEvidenceRelation=" + Normalize(item.AxisEvidenceRawRelation),
            "roleRankRelation=" + Normalize(item.RoleRankRawRelation),
            "rawGeometryRelation=" + Normalize(item.RawGeometryRelationState),
            "calibrationCohort=" + Normalize(item.CalibrationCohort),
            "medicalReadable=" + Bool(item.MedicalEvidenceReadable),
            "hp=" + item.HealthPercent.ToString(CultureInfo.InvariantCulture),
            "medicalDamage=" + Bool(item.HasMedicalDamageObserved),
            "medicineUsing=" + Bool(item.AnyMedicineUsing),
            "equipmentObserved=" + Bool(item.EquipmentEvidenceObserved),
            "primary=" + Bool(item.HasPrimaryWeapon),
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
            "currentWeaponKnown=" + Bool(item.CurrentWeaponKnown),
            "currentWeaponItemId=" + Normalize(item.CurrentWeaponItemId),
            "currentWeaponTpl=" + Normalize(item.CurrentWeaponTemplateId),
            "weaponReady=" + Bool(item.WeaponReady),
            "changingWeapon=" + Bool(item.ChangingWeapon),
            "handsSuspicious=" + Bool(item.HandsSuspicious),
            "ammoBoundary=" + Normalize(item.AmmunitionBoundaryState),
            "immediateWeaponState=" + Normalize(item.ImmediateWeaponState),
            "flankLeadMaterialFloor=" + Normalize(item.FlankLeadMaterialFloorShadowState),
            "hardMaterialFloorFailure=" + Bool(item.HardMaterialFloorFailureObserved),
            "floorEvidenceProven=" + Bool(item.EquipmentFloorEvidenceProven),
            "floorEligibleShadow=" + Bool(item.EquipmentFloorEligibleShadow),
            "floorAdmission=" + Normalize(item.EquipmentFloorAdmissionState),
            "immediateCapabilityMatch=" + Normalize(item.ImmediateWeaponCapabilityMatchState),
            "immediateCapabilityObserved=" + Bool(item.ImmediateWeaponCapabilityObserved),
            "immediateWeaponFacts=" + Normalize(ImmediateWeaponCapabilityToken(item.ImmediateWeaponCapability)),
            "alternativeCarriedWeapons=" + item.AlternativeCarriedWeaponCount.ToString(CultureInfo.InvariantCulture),
            "immediateWeaponSightingRange=" + F(item.ImmediateWeaponSightingRangeMeters),
            "threatVsImmediateWeaponSightingRange=" + Normalize(item.ThreatDistanceVsImmediateWeaponSightingRangeRelation),
            "immediateWeaponEffectiveDistance=" + item.ImmediateWeaponEffectiveDistanceMeters.ToString(CultureInfo.InvariantCulture),
            "threatVsImmediateWeaponEffectiveDistance=" + Normalize(item.ThreatDistanceVsImmediateWeaponEffectiveDistanceRelation),
            "capabilityGeometryReference=" + Bool(item.CapabilityGeometryReference),
            "capabilityComparable=" + Bool(item.ComparableWithinCapabilityCohort),
            "capDPath=" + F(item.CapabilityDeltaPathMeters),
            "capDDirect=" + F(item.CapabilityDeltaDirectDistanceMeters),
            "capDRatio=" + F(item.CapabilityDeltaPathRatio),
            "capDAxisEvidence=" + F(item.CapabilityDeltaAxisEvidence),
            "capDEstablishmentRadius=" + F(item.CapabilityDeltaEstablishmentRegionRadiusMeters),
            "capDPathCorners=" + item.CapabilityDeltaPathCornerCount.ToString(CultureInfo.InvariantCulture),
            "capPathRelation=" + Normalize(item.CapabilityPathLengthRawRelation),
            "capDirectRelation=" + Normalize(item.CapabilityDirectDistanceRawRelation),
            "capRatioRelation=" + Normalize(item.CapabilityPathRatioRawRelation),
            "capAxisEvidenceRelation=" + Normalize(item.CapabilityAxisEvidenceRawRelation),
            "capEstablishmentRadiusRelation=" + Normalize(item.CapabilityEstablishmentRadiusRawRelation),
            "capPathCornerRelation=" + Normalize(item.CapabilityPathCornerCountRawRelation),
            "capAxisSourceRelation=" + Normalize(item.CapabilityAxisSourceRelation),
            "capRouteEvidenceRelation=" + Normalize(item.CapabilityRouteEvidenceKindRelation),
            "capRawGeometryRelation=" + Normalize(item.CapabilityRawGeometryRelationState),
            "situationClass=" + Normalize(item.SituationDifferenceClass),
            "equipmentFit=" + Normalize(item.EquipmentFitState));

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

    private static string ImmediateWeaponCapabilityToken(VanguardLiveWeaponCapabilityObservation? weapon)
    {
        if (weapon == null) return "none";
        return string.Join(":",
            Normalize(weapon.Slot),
            Normalize(weapon.ItemId),
            Normalize(weapon.TemplateId),
            Normalize(weapon.Caliber),
            Normalize(weapon.WeaponClass),
            Normalize(weapon.CanonicalWeaponFamily),
            Normalize(weapon.WeaponFamilyQualification),
            weapon.MountedSightCount.ToString(CultureInfo.InvariantCulture),
            weapon.MagnifiedSightCount.ToString(CultureInfo.InvariantCulture),
            F(weapon.MaxSupportedSightZoom),
            Normalize(weapon.SightCapabilityState),
            weapon.ReadyRounds.ToString(CultureInfo.InvariantCulture),
            weapon.ReserveRounds.ToString(CultureInfo.InvariantCulture),
            weapon.TotalObservedRounds.ToString(CultureInfo.InvariantCulture),
            weapon.CompatibleMagazineCount.ToString(CultureInfo.InvariantCulture),
            F(weapon.DurabilityFraction),
            weapon.BestPenetration.ToString(CultureInfo.InvariantCulture),
            weapon.BestDamage.ToString(CultureInfo.InvariantCulture),
            F(weapon.Ergonomics),
            F(weapon.Recoil),
            F(weapon.SightingRange),
            weapon.EffectiveDistanceMeters.ToString(CultureInfo.InvariantCulture),
            Bool(weapon.Bricked),
            Normalize(weapon.RawAmmunitionPresence));
    }

    private static VanguardLiveWeaponCapabilityObservation? ResolveImmediateWeaponCapability(
        TacticalFitnessCandidateObservation candidate,
        out string matchState,
        out int alternativeCarriedWeaponCount)
    {
        alternativeCarriedWeaponCount = -1;
        if (!candidate.PerWeaponEquipmentEvidenceObserved)
        {
            matchState = "EQUIPMENT_UNOBSERVED";
            return null;
        }
        if (!candidate.WeaponReadinessObserved)
        {
            matchState = "READINESS_UNOBSERVED";
            return null;
        }
        if (candidate.ChangingWeapon || candidate.HandsSuspicious)
        {
            matchState = "TRANSITIONAL";
            return null;
        }
        if (!candidate.CurrentWeaponKnown)
        {
            matchState = "CURRENT_WEAPON_UNKNOWN";
            return null;
        }

        IReadOnlyList<VanguardLiveWeaponCapabilityObservation> weapons = candidate.LiveWeaponCapabilities ?? Array.Empty<VanguardLiveWeaponCapabilityObservation>();
        string currentItemId = Normalize(candidate.CurrentWeaponItemId);
        if (currentItemId != "none")
        {
            VanguardLiveWeaponCapabilityObservation? exact = weapons.FirstOrDefault(item =>
                string.Equals(Normalize(item.ItemId), currentItemId, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                alternativeCarriedWeaponCount = Math.Max(0, weapons.Count - 1);
                matchState = "EXACT_ITEM_MATCH";
                return exact;
            }
        }

        string currentTemplateId = Normalize(candidate.CurrentWeaponTemplateId);
        if (currentTemplateId != "none")
        {
            VanguardLiveWeaponCapabilityObservation[] templateMatches = weapons
                .Where(item => string.Equals(Normalize(item.TemplateId), currentTemplateId, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (templateMatches.Length == 1)
            {
                alternativeCarriedWeaponCount = Math.Max(0, weapons.Count - 1);
                matchState = "UNIQUE_TEMPLATE_FALLBACK_MATCH";
                return templateMatches[0];
            }
            if (templateMatches.Length > 1)
            {
                matchState = "AMBIGUOUS_TEMPLATE_MATCH";
                return null;
            }
        }

        matchState = "CURRENT_WEAPON_NOT_IN_CARRIED_SET";
        return null;
    }

    private static string ResolveThreatDistanceVsImmediateWeaponSightingRangeRelation(
        TacticalFitnessCandidateObservation candidate,
        VanguardLiveWeaponCapabilityObservation? immediateWeaponCapability)
    {
        if (!candidate.ThreatDistanceAlignedToGroup
            || !IsFinite(candidate.ThreatDistanceMeters)
            || candidate.ThreatDistanceMeters < 0f)
        {
            return "THREAT_DISTANCE_UNALIGNED";
        }

        if (immediateWeaponCapability == null)
        {
            return "IMMEDIATE_WEAPON_CAPABILITY_UNRESOLVED";
        }

        float sightingRange = immediateWeaponCapability.SightingRange;
        if (!IsFinite(sightingRange) || sightingRange <= 0f)
        {
            return "IMMEDIATE_WEAPON_SIGHTING_RANGE_UNAVAILABLE";
        }

        return candidate.ThreatDistanceMeters <= sightingRange
            ? "WITHIN_OR_AT_IMMEDIATE_WEAPON_SIGHTING_RANGE"
            : "BEYOND_IMMEDIATE_WEAPON_SIGHTING_RANGE";
    }

    private static string ResolveThreatDistanceVsImmediateWeaponEffectiveDistanceRelation(
        TacticalFitnessCandidateObservation candidate,
        VanguardLiveWeaponCapabilityObservation? immediateWeaponCapability)
    {
        if (!candidate.ThreatDistanceAlignedToGroup
            || !IsFinite(candidate.ThreatDistanceMeters)
            || candidate.ThreatDistanceMeters < 0f)
        {
            return "THREAT_DISTANCE_UNALIGNED";
        }

        if (immediateWeaponCapability == null)
        {
            return "IMMEDIATE_WEAPON_CAPABILITY_UNRESOLVED";
        }

        // Authority boundary: EFT EffectiveDist remains raw item provenance only.
        // Under the active SAIN integration it is not treated as an executable firing-range ceiling,
        // and SAIN-owned engagement-distance tuning is deliberately excluded from TC fitness evidence.
        int effectiveDistance = immediateWeaponCapability.EffectiveDistanceMeters;
        if (effectiveDistance <= 0)
        {
            return "IMMEDIATE_WEAPON_EFFECTIVE_DISTANCE_UNAVAILABLE";
        }

        return candidate.ThreatDistanceMeters <= effectiveDistance
            ? "WITHIN_OR_AT_IMMEDIATE_WEAPON_EFFECTIVE_DISTANCE"
            : "BEYOND_IMMEDIATE_WEAPON_EFFECTIVE_DISTANCE";
    }

    private static string ResolveFloatRelation(
        float candidateValue,
        float referenceValue,
        bool comparable,
        bool isReference)
    {
        if (!comparable) return "NOT_COMPARABLE";
        if (isReference) return "REFERENCE";
        if (!IsFinite(candidateValue) || !IsFinite(referenceValue)) return "UNRESOLVED_NONFINITE";
        if (candidateValue < referenceValue) return "LOWER";
        if (candidateValue > referenceValue) return "HIGHER";
        return "EQUAL";
    }

    private static string ResolveIntRelation(
        int candidateValue,
        int referenceValue,
        bool comparable,
        bool isReference)
    {
        if (!comparable) return "NOT_COMPARABLE";
        if (isReference) return "REFERENCE";
        if (candidateValue < referenceValue) return "LOWER";
        if (candidateValue > referenceValue) return "HIGHER";
        return "EQUAL";
    }

    private static string ResolveStringEqualityRelation(
        string candidateValue,
        string? referenceValue,
        bool comparable,
        bool isReference)
    {
        if (!comparable) return "NOT_COMPARABLE";
        if (isReference) return "REFERENCE";
        return string.Equals(candidateValue ?? string.Empty, referenceValue ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            ? "SAME"
            : "DIFFERENT";
    }

    private static string ResolveRoleRankRelation(
        int candidateRoleRank,
        TacticalFitnessCandidateObservation? reference,
        bool comparable,
        bool isReference)
    {
        if (!comparable || reference == null) return "NOT_COMPARABLE";
        if (isReference) return "REFERENCE";
        if (candidateRoleRank > reference.RolePreferenceRank) return "AHEAD";
        if (candidateRoleRank < reference.RolePreferenceRank) return "BEHIND";
        return "SAME";
    }

    private static string ResolveRawGeometryRelation(
        TacticalFitnessCandidateObservation candidate,
        TacticalFitnessCandidateObservation? reference,
        bool comparable,
        bool isReference)
    {
        if (!comparable || reference == null) return "NOT_COMPARABLE";
        if (isReference) return "REFERENCE";
        if (!HasFiniteGeometry(candidate) || !HasFiniteGeometry(reference)) return "UNRESOLVED_NONFINITE_GEOMETRY";

        bool referenceNonWorseOnAllProvenAxes =
            reference.PathLengthMeters <= candidate.PathLengthMeters
            && reference.DirectDistanceMeters <= candidate.DirectDistanceMeters
            && reference.PathRatio <= candidate.PathRatio
            && reference.AxisEvidenceScore >= candidate.AxisEvidenceScore;

        return referenceNonWorseOnAllProvenAxes
            ? "REFERENCE_NONWORSE_ALL_PROVEN_AXES"
            : "CANDIDATE_HAS_COUNTERVAILING_PROVEN_AXIS";
    }

    private static string ResolveCalibrationCohort(
        string roleRankRelation,
        string rawGeometryRelationState,
        bool comparable,
        bool isReference)
    {
        if (!comparable) return "NOT_COMPARABLE";
        if (isReference) return "REFERENCE";
        return roleRankRelation + "__" + rawGeometryRelationState;
    }

    private static string ResolveAmmunitionBoundaryState(TacticalFitnessCandidateObservation candidate)
    {
        if (!candidate.PerWeaponEquipmentEvidenceObserved) return "UNKNOWN";
        if (candidate.CarriedWeaponCount <= 0) return "NO_CARRIED_WEAPON";
        if (candidate.WeaponsWithRoundsCount <= 0) return "EMPTY_OR_BRICKED";
        return "NONEMPTY_BAND_UNRESOLVED";
    }

    private static string ResolveImmediateWeaponState(TacticalFitnessCandidateObservation candidate)
    {
        if (!candidate.WeaponReadinessObserved) return "UNKNOWN";
        if (candidate.ChangingWeapon || candidate.HandsSuspicious) return "TRANSITIONAL";
        if (!candidate.CurrentWeaponKnown) return "CURRENT_WEAPON_UNKNOWN";
        return candidate.WeaponReady ? "READY" : "NOT_READY";
    }

    private static string ResolveFlankLeadMaterialFloorShadowState(
        string ammunitionBoundaryState,
        string immediateWeaponState)
    {
        if (string.Equals(ammunitionBoundaryState, "UNKNOWN", StringComparison.Ordinal)) return "UNKNOWN";
        if (string.Equals(ammunitionBoundaryState, "NO_CARRIED_WEAPON", StringComparison.Ordinal)) return "HARD_FLOOR_FAIL_NO_CARRIED_WEAPON";
        if (string.Equals(ammunitionBoundaryState, "EMPTY_OR_BRICKED", StringComparison.Ordinal)) return "HARD_FLOOR_FAIL_EMPTY_OR_BRICKED";

        return immediateWeaponState switch
        {
            "READY" => "HARD_FLOOR_NOT_FAILED__IMMEDIATE_READY",
            "NOT_READY" => "HARD_FLOOR_NOT_FAILED__IMMEDIATE_NOT_READY",
            "TRANSITIONAL" => "HARD_FLOOR_NOT_FAILED__IMMEDIATE_TRANSITIONAL",
            _ => "HARD_FLOOR_NOT_FAILED__IMMEDIATE_UNRESOLVED"
        };
    }

    private static bool IsFlankLeadFloorEvidenceProven(TacticalFitnessCandidateObservation candidate)
    {
        if (!IsEvidenceEligible(candidate)) return false;
        string ammunitionBoundaryState = ResolveAmmunitionBoundaryState(candidate);
        string immediateWeaponState = ResolveImmediateWeaponState(candidate);
        string floorState = ResolveFlankLeadMaterialFloorShadowState(ammunitionBoundaryState, immediateWeaponState);
        return !string.Equals(floorState, "UNKNOWN", StringComparison.Ordinal);
    }

    private static bool IsFlankLeadFloorEligible(TacticalFitnessCandidateObservation candidate)
    {
        if (!IsFlankLeadFloorEvidenceProven(candidate)) return false;
        string ammunitionBoundaryState = ResolveAmmunitionBoundaryState(candidate);
        string immediateWeaponState = ResolveImmediateWeaponState(candidate);
        string floorState = ResolveFlankLeadMaterialFloorShadowState(ammunitionBoundaryState, immediateWeaponState);
        return !string.Equals(floorState, "HARD_FLOOR_FAIL_NO_CARRIED_WEAPON", StringComparison.Ordinal)
            && !string.Equals(floorState, "HARD_FLOOR_FAIL_EMPTY_OR_BRICKED", StringComparison.Ordinal);
    }

    private static string ResolveFloorAdmissionState(
        bool evidenceEligible,
        string materialFloorState,
        bool hardMaterialFloorFailureObserved)
    {
        if (!evidenceEligible) return "mandatory_evidence_unproven";
        if (string.Equals(materialFloorState, "UNKNOWN", StringComparison.Ordinal)) return "equipment_floor_viability_unproven";
        if (hardMaterialFloorFailureObserved) return "equipment_hard_floor_failed";
        return "equipment_floor_proven_candidate";
    }

    private static string ResolveCapabilityCohortState(
        int rawCandidateCount,
        int evidenceEligibleCount,
        int floorEligibleCount,
        int floorHardFailedCount,
        int floorUnprovenCount)
    {
        if (rawCandidateCount == 0) return "no_candidate";
        if (evidenceEligibleCount == 0) return "no_assignment_viability_unproven";
        if (floorEligibleCount == 0 && floorUnprovenCount > 0) return "no_assignment_equipment_floor_viability_unproven";
        if (floorEligibleCount == 0 && floorHardFailedCount == evidenceEligibleCount) return "no_assignment_equipment_hard_floor_failed";
        if (floorEligibleCount == 1) return "single_floor_proven_candidate_calibration_only";
        return "floor_proven_membership_unresolved_materiality_not_calibrated";
    }

    private static bool HasFiniteGeometry(TacticalFitnessCandidateObservation item)
        => IsFinite(item.PathLengthMeters)
            && IsFinite(item.DirectDistanceMeters)
            && IsFinite(item.PathRatio)
            && IsFinite(item.AxisEvidenceScore);

    private static bool IsFinite(float value)
        => !float.IsNaN(value) && !float.IsInfinity(value);

    private static bool IsEvidenceEligible(TacticalFitnessCandidateObservation item)
        => item.PlanningViable
            && item.CandidateSpecificPathProven
            && item.RuntimePresent
            && item.DecisionSnapshotFresh
            && item.MedicalEvidenceReadable
            && item.EquipmentEvidenceObserved
            && item.WeaponReadinessObserved
            && string.Equals(item.EvidenceKind, "DIRECT_OBSERVED", StringComparison.Ordinal);

    private static string AdmissionState(TacticalFitnessCandidateObservation item)
    {
        if (!item.PlanningViable) return "planning_viability_unproven";
        if (!item.CandidateSpecificPathProven) return "candidate_path_unproven";
        if (!item.RuntimePresent) return "runtime_presence_missing";
        if (!item.DecisionSnapshotFresh) return "decision_evidence_unproven";
        if (!item.MedicalEvidenceReadable) return "medical_evidence_unproven";
        if (!item.EquipmentEvidenceObserved) return "equipment_evidence_unproven";
        if (!item.WeaponReadinessObserved) return "weapon_readiness_evidence_unproven";
        if (!string.Equals(item.EvidenceKind, "DIRECT_OBSERVED", StringComparison.Ordinal)) return "direct_evidence_unproven";
        return "evidence_proven_candidate";
    }

    private static bool HasMedicalDamage(TacticalFitnessCandidateObservation item)
        => item.HasHeavyBleed
            || item.HasLightBleed
            || item.HasFracture
            || item.HasPain
            || item.HasDestroyedPart
            || item.HasUntreatableVitalDamage;

    private static bool SamePair(string axisA, string botA, string axisB, string botB)
        => string.Equals(axisA, axisB, StringComparison.OrdinalIgnoreCase)
            && string.Equals(botA, botB, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().ToLowerInvariant();

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private static string F(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Timestamp(DateTimeOffset value) => value == DateTimeOffset.MinValue ? "none" : value.ToString("O", CultureInfo.InvariantCulture);
}
#endif
