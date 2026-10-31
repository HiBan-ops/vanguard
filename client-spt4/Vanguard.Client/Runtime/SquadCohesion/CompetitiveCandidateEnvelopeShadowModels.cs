#if SPT_CLIENT
using System;
using System.Collections.Generic;

// Responsibility: Defines immutable calibration-only observations for the Competitive Candidate Envelope over evidence-proven FlankLead candidates.
// Flow: TacticalFitnessShadow reference-eligible candidates are projected against the shortest-path geometry reference and retain raw capability/geometry deltas for later materiality calibration.
// Authority boundary: Data contract only; these models cannot select an executable assignment, classify MATERIAL, apply equipment/persona bias, acquire authority, move an Operator or mutate SAIN.
// Invariant: Competitive membership remains unresolved until contribution-specific materiality/equipment policy is calibrated; no raw delta becomes a threshold by existing in this telemetry.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal sealed class CompetitiveCandidateEnvelopeShadowSnapshot
{
    public static readonly CompetitiveCandidateEnvelopeShadowSnapshot Empty = new();

    public string MapId { get; init; } = string.Empty;
    public long TopologyRevision { get; init; }
    public string TopologyContentHash { get; init; } = string.Empty;
    public DateTimeOffset TacticalFitnessPublishedAtUtc { get; init; } = DateTimeOffset.MinValue;
    public DateTimeOffset PublishedAtUtc { get; init; } = DateTimeOffset.MinValue;
    public IReadOnlyList<CompetitiveCandidateEnvelopeThreatGroupObservation> ThreatGroups { get; init; } = Array.Empty<CompetitiveCandidateEnvelopeThreatGroupObservation>();

    public bool IsAvailable => TopologyRevision > 0 && !string.IsNullOrWhiteSpace(MapId);
    public bool ObservationOnly => true;
    public bool CompetitiveMembershipResolved => false;
    public bool ExecutableSelectionProduced => false;
    public bool AssignmentMutationAllowed => false;
    public bool MovementAuthority => false;
}

internal sealed class CompetitiveCandidateEnvelopeThreatGroupObservation
{
    public string OwnerProfileId { get; init; } = string.Empty;
    public string ThreatId { get; init; } = string.Empty;
    public string CurrentAxisCandidateId { get; init; } = string.Empty;
    public string CurrentBotProfileId { get; init; } = string.Empty;
    public bool CurrentEvidenceEligible { get; init; }

    public string GeometryReferenceAxisCandidateId { get; init; } = string.Empty;
    public string GeometryReferenceBotProfileId { get; init; } = string.Empty;
    public string CapabilityGeometryReferenceAxisCandidateId { get; init; } = string.Empty;
    public string CapabilityGeometryReferenceBotProfileId { get; init; } = string.Empty;
    public bool CurrentFloorEvidenceProven { get; init; }
    public bool CurrentFloorEligibleShadow { get; init; }
    public int RawCandidateCount { get; init; }
    public int EvidenceEligibleCandidateCount { get; init; }
    public int EvidenceUnprovenCandidateCount { get; init; }
    public int FloorEvidenceProvenCandidateCount { get; init; }
    public int FloorEvidenceUnprovenCandidateCount { get; init; }
    public int FloorEligibleCandidateCount { get; init; }
    public int FloorHardFailedCandidateCount { get; init; }
    public bool CapabilityGeometryReferenceDiffersFromLegacyReference { get; init; }
    public int CapabilityComparableChallengerCount { get; init; }
    public int CapabilityReferenceNonWorseGeometryChallengerCount { get; init; }
    public int CapabilityCountervailingGeometryChallengerCount { get; init; }
    public int CapabilityRawGeometryUnresolvedChallengerCount { get; init; }
    public int ComparableChallengerCount { get; init; }
    public int ReferenceNonWorseGeometryChallengerCount { get; init; }
    public int CountervailingGeometryChallengerCount { get; init; }
    public int RawGeometryUnresolvedChallengerCount { get; init; }
    public int RoleAheadOfGeometryReferenceChallengerCount { get; init; }
    public int RoleSameAsGeometryReferenceChallengerCount { get; init; }
    public int RoleBehindGeometryReferenceChallengerCount { get; init; }
    public string EnvelopeState { get; init; } = "no_candidate";
    public string CapabilityCohortState { get; init; } = "no_candidate";

    public IReadOnlyList<CompetitiveCandidateEnvelopeCandidateObservation> Candidates { get; init; } = Array.Empty<CompetitiveCandidateEnvelopeCandidateObservation>();

    public bool ObservationOnly => true;
    public bool CompetitiveMembershipResolved => false;
    public bool ShadowWinnerProduced => false;
    public bool EquipmentFitClassificationApplied => false;
    public bool BehavioralBiasApplied => false;
    public bool HysteresisApplied => false;
    public bool MaterialityThresholdApplied => false;
    public bool CandidateSpecificThreatExposureApplied => false;
}

internal sealed class CompetitiveCandidateEnvelopeCandidateObservation
{
    public string AxisCandidateId { get; init; } = string.Empty;
    public string BotProfileId { get; init; } = string.Empty;
    public string OperatorId { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string Specialty { get; init; } = string.Empty;
    public string RolePreferenceBand { get; init; } = "N";
    public int RolePreferenceRank { get; init; }

    public bool EvidenceEligible { get; init; }
    public string AdmissionState { get; init; } = "viability_unproven";
    public string EvidenceKind { get; init; } = "UNKNOWN";
    public string QualificationState { get; init; } = "capability_unknown";

    // Candidate-aligned threat distance is preserved as raw provenance only.
    public bool ThreatEvidenceObserved { get; init; }
    public string ThreatEvidenceEnemyId { get; init; } = "none";
    public bool ThreatDistanceAlignedToGroup { get; init; }
    public float ThreatDistanceMeters { get; init; } = -1f;
    public string ThreatDistanceEvidenceState { get; init; } = "UNRESOLVED";

    // Native EFT weapon-selector distance anchors and the raw relation of the
    // aligned threat distance to that native switch window. This is not a Vanguard range band.
    public bool NativeWeaponDistanceAnchorsObserved { get; init; }
    public float NativeWeaponSwitchLowDistanceMeters { get; init; } = -1f;
    public float NativeWeaponSwitchFarDistanceMeters { get; init; } = -1f;
    public string NativeWeaponDistanceAnchorState { get; init; } = "UNRESOLVED";
    public string ThreatDistanceVsNativeWeaponWindowRelation { get; init; } = "NOT_COMPARABLE";

    public bool GeometryReference { get; init; }
    public bool ComparableToGeometryReference { get; init; }
    public string AxisCandidateSource { get; init; } = string.Empty;
    public string AxisSeedBotProfileId { get; init; } = string.Empty;
    public bool IsAxisSeed { get; init; }
    public string RouteEvidenceKind { get; init; } = string.Empty;
    public float EstablishmentRegionRadiusMeters { get; init; }
    public int PathCornerCount { get; init; }
    public float PathLengthMeters { get; init; }
    public float DirectDistanceMeters { get; init; }
    public float PathRatio { get; init; }
    public float AxisEvidenceScore { get; init; }
    public float DeltaPathMetersFromGeometryReference { get; init; }
    public float DeltaDirectDistanceMetersFromGeometryReference { get; init; }
    public float DeltaPathRatioFromGeometryReference { get; init; }
    public float DeltaAxisEvidenceFromGeometryReference { get; init; }
    public int DeltaRoleRankFromGeometryReference { get; init; }

    // Threshold-free comparison vocabulary. These are raw directional relations only;
    // they do not classify NEGLIGIBLE/MINOR/MATERIAL or resolve competitive membership.
    public string PathLengthRawRelation { get; init; } = "NOT_COMPARABLE";
    public string DirectDistanceRawRelation { get; init; } = "NOT_COMPARABLE";
    public string PathRatioRawRelation { get; init; } = "NOT_COMPARABLE";
    public string AxisEvidenceRawRelation { get; init; } = "NOT_COMPARABLE";
    public string RoleRankRawRelation { get; init; } = "NOT_COMPARABLE";
    public string RawGeometryRelationState { get; init; } = "NOT_COMPARABLE";
    public string CalibrationCohort { get; init; } = "NOT_COMPARABLE";

    public bool MedicalEvidenceReadable { get; init; }
    public int HealthPercent { get; init; } = -1;
    public bool HasMedicalDamageObserved { get; init; }
    public bool AnyMedicineUsing { get; init; }
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

    // Boundary-only equipment semantics expose only hard facts that
    // require no reserve threshold or weapon-family classification. They do not resolve
    // contribution equipment fit or competitive membership.
    public string AmmunitionBoundaryState { get; init; } = "UNKNOWN";
    public string ImmediateWeaponState { get; init; } = "UNKNOWN";
    public string FlankLeadMaterialFloorShadowState { get; init; } = "UNKNOWN";
    public bool HardMaterialFloorFailureObserved { get; init; }
    public bool EquipmentFloorEvidenceProven { get; init; }
    public bool EquipmentFloorEligibleShadow { get; init; }
    public string EquipmentFloorAdmissionState { get; init; } = "floor_viability_unproven";

    // Immediate capability is the exact/derived carried-weapon
    // observation corresponding to the current weapon. No ranking, fit class or switch authority.
    public string ImmediateWeaponCapabilityMatchState { get; init; } = "UNRESOLVED";
    public bool ImmediateWeaponCapabilityObserved { get; init; }
    public VanguardLiveWeaponCapabilityObservation? ImmediateWeaponCapability { get; init; }
    public int AlternativeCarriedWeaponCount { get; init; } = -1;

    // This relation composes only already-proven facts: exact/unique-fallback immediate weapon capability
    // plus candidate-aligned threat distance. GetSightingRange is retained as EFT-reported raw range
    // evidence; this relation is not an effective-range band, equipment-fit class or switch request.
    public float ImmediateWeaponSightingRangeMeters { get; init; } = -1f;
    public string ThreatDistanceVsImmediateWeaponSightingRangeRelation { get; init; } = "NOT_COMPARABLE";

    // EFT WeaponTemplate.bEffDist is retained as a distinct raw provenance axis. EFT labels this
    // EffectiveDist; Vanguard does not reinterpret it as a tactical band or equipment-fit verdict.
    public int ImmediateWeaponEffectiveDistanceMeters { get; init; } = -1;
    public string ThreatDistanceVsImmediateWeaponEffectiveDistanceRelation { get; init; } = "NOT_COMPARABLE";

    public bool CapabilityGeometryReference { get; init; }
    public bool ComparableWithinCapabilityCohort { get; init; }
    public float CapabilityDeltaPathMeters { get; init; }
    public float CapabilityDeltaDirectDistanceMeters { get; init; }
    public float CapabilityDeltaPathRatio { get; init; }
    public float CapabilityDeltaAxisEvidence { get; init; }
    public float CapabilityDeltaEstablishmentRegionRadiusMeters { get; init; }
    public int CapabilityDeltaPathCornerCount { get; init; }
    public string CapabilityPathLengthRawRelation { get; init; } = "NOT_COMPARABLE";
    public string CapabilityDirectDistanceRawRelation { get; init; } = "NOT_COMPARABLE";
    public string CapabilityPathRatioRawRelation { get; init; } = "NOT_COMPARABLE";
    public string CapabilityAxisEvidenceRawRelation { get; init; } = "NOT_COMPARABLE";
    public string CapabilityEstablishmentRadiusRawRelation { get; init; } = "NOT_COMPARABLE";
    public string CapabilityPathCornerCountRawRelation { get; init; } = "NOT_COMPARABLE";
    public string CapabilityAxisSourceRelation { get; init; } = "NOT_COMPARABLE";
    public string CapabilityRouteEvidenceKindRelation { get; init; } = "NOT_COMPARABLE";
    public string CapabilityRawGeometryRelationState { get; init; } = "NOT_COMPARABLE";

    public string SituationDifferenceClass { get; init; } = "UNRESOLVED_NO_THRESHOLD";
    public string EquipmentFitState { get; init; } = "UNCLASSIFIED_RAW_EVIDENCE_ONLY";

    public bool ObservationOnly => true;
    public bool CompetitiveMembershipResolved => false;
}
#endif
