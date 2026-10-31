#if SPT_CLIENT
using System;
using System.Collections.Generic;

// Responsibility: Defines immutable read-only observations for contribution-specific tactical-fitness calibration over the existing alternate-route candidate matrix.
// Flow: Route-assignment feasibility, current Operator capability evidence, stable tactical identity and raw geometry facts are normalized into a shadow snapshot that later policy work can compare without changing the active plan.
// Authority boundary: Data contract only; these models cannot select an executable assignment, acquire a lease, mutate SAIN, start movement, change targets, switch weapons or persist tactical fitness.
// Invariant: Raw evidence and uncertainty remain explicit; no missing capability, role/persona field or geometry difference is silently converted into permission or a materiality threshold.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal sealed class TacticalFitnessShadowSnapshot
{
    public static readonly TacticalFitnessShadowSnapshot Empty = new();

    public string MapId { get; init; } = string.Empty;
    public long TopologyRevision { get; init; }
    public string TopologyContentHash { get; init; } = string.Empty;
    public DateTimeOffset RouteAssignmentPublishedAtUtc { get; init; } = DateTimeOffset.MinValue;
    public DateTimeOffset PublishedAtUtc { get; init; } = DateTimeOffset.MinValue;
    public IReadOnlyList<TacticalFitnessThreatGroupObservation> ThreatGroups { get; init; } = Array.Empty<TacticalFitnessThreatGroupObservation>();

    public bool IsAvailable => TopologyRevision > 0 && !string.IsNullOrWhiteSpace(MapId);
    public bool ObservationOnly => true;
    public bool AssignmentMutationAllowed => false;
    public bool MovementAuthority => false;
}

internal sealed class TacticalFitnessThreatGroupObservation
{
    public string OwnerProfileId { get; init; } = string.Empty;
    public string ThreatId { get; init; } = string.Empty;

    public string CurrentAxisCandidateId { get; init; } = string.Empty;
    public string CurrentBotProfileId { get; init; } = string.Empty;
    public string CurrentRolePreferenceBand { get; init; } = "N";

    // Reference candidates are calibration facts only. They are deliberately not an executable selection.
    public string RoleReferenceAxisCandidateId { get; init; } = string.Empty;
    public string RoleReferenceBotProfileId { get; init; } = string.Empty;
    public string GeometryReferenceAxisCandidateId { get; init; } = string.Empty;
    public string GeometryReferenceBotProfileId { get; init; } = string.Empty;
    public string AlignedReferenceAxisCandidateId { get; init; } = string.Empty;
    public string AlignedReferenceBotProfileId { get; init; } = string.Empty;

    public bool RoleAndGeometryReferencesAgree { get; init; }
    public bool CurrentReferenceEligible { get; init; }
    public int ReferenceEligibleCandidateCount { get; init; }
    public int ReferenceEvidenceUnprovenCandidateCount { get; init; }
    public bool CurrentMatchesRoleReference { get; init; }
    public bool CurrentMatchesGeometryReference { get; init; }
    public float CurrentMinusGeometryPathMeters { get; init; }
    public float CurrentMinusGeometryPathRatio { get; init; }
    public float CurrentMinusGeometryAxisEvidence { get; init; }
    public int CurrentMinusGeometryRoleRank { get; init; }

    public string MaterialityState { get; init; } = "unresolved_no_threshold";
    public string Resolution { get; init; } = "no_candidate";
    public IReadOnlyList<TacticalFitnessCandidateObservation> Candidates { get; init; } = Array.Empty<TacticalFitnessCandidateObservation>();

    public bool ObservationOnly => true;
    public bool ExecutableSelectionProduced => false;
}

internal sealed class TacticalFitnessCandidateObservation
{
    public string AxisCandidateId { get; init; } = string.Empty;
    public string BotProfileId { get; init; } = string.Empty;
    public string OperatorId { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;
    public string Specialty { get; init; } = string.Empty;
    public string RolePreferenceBand { get; init; } = "N";
    public int RolePreferenceRank { get; init; }
    public string BasePersona { get; init; } = string.Empty;
    public string Doctrine { get; init; } = string.Empty;
    public string Temperament { get; init; } = string.Empty;
    public int IdentitySchemaVersion { get; init; }

    public bool PlanningViable { get; init; }
    public bool CandidateSpecificPathProven { get; init; }
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

    public bool RuntimePresent { get; init; }
    public bool DecisionSnapshotObserved { get; init; }
    public bool DecisionSnapshotFresh { get; init; }
    public string DecisionFreshnessReason { get; init; } = "unknown";
    public DateTimeOffset DecisionCapturedAtUtc { get; init; } = DateTimeOffset.MinValue;

    // Candidate-aligned threat-range provenance comes from the existing decision-snapshot
    // evidence only; it is not a range band, materiality threshold or equipment-fit decision.
    public bool ThreatEvidenceObserved { get; init; }
    public string ThreatEvidenceEnemyId { get; init; } = "none";
    public bool ThreatDistanceAlignedToGroup { get; init; }
    public float ThreatDistanceMeters { get; init; } = -1f;
    public string ThreatDistanceEvidenceState { get; init; } = "UNRESOLVED";

    // Native EFT weapon-selector distance anchors are per-bot Shoot settings
    // used by EFT itself for distance-gated weapon switching; they are not Vanguard CQB/short/medium/long bands.
    public bool NativeWeaponDistanceAnchorsObserved { get; init; }
    public float NativeWeaponSwitchLowDistanceMeters { get; init; } = -1f;
    public float NativeWeaponSwitchFarDistanceMeters { get; init; } = -1f;
    public string NativeWeaponDistanceAnchorState { get; init; } = "UNRESOLVED";
    public string ThreatDistanceVsNativeWeaponWindowRelation { get; init; } = "NOT_COMPARABLE";

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

    // Frozen policy intentionally keeps these inactive until their contribution-specific calibration exists.
    public bool BehavioralBiasApplied => false;
    public bool EquipmentFitClassificationApplied => false;
    public bool MaterialGeometryThresholdApplied => false;
    public bool ObservationOnly => true;
}
#endif
