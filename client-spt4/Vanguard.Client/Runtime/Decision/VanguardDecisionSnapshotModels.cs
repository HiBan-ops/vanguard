#if SPT_CLIENT
using System;
using UnityEngine;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Medical;
using Vanguard.Client.Runtime.Loot;
using Vanguard.Client.Runtime.Grenades;
using Vanguard.Client.Runtime.SquadCohesion;
using Vanguard.Client.Runtime.Execution;

// Responsibility: Defines the normalized decision snapshot graph shared by runtime readers, intent producers, authority arbitration and executors.
// Flow: Specialized readers capture current combat, movement, medical, loot, grenade, external and lifecycle evidence into immutable/bounded snapshots that the scheduler evaluates as one coherent decision frame.
// Authority boundary: Snapshots are observations, not authority; physical execution and persistent state changes remain in their owning components.
// Invariant: A snapshot must represent the evidence available for one decision window and must never fabricate freshness or survive as hidden authority after newer evidence arrives.
namespace Vanguard.Client.Runtime.Decision;

internal sealed class OperatorDecisionSnapshot
{
    public static OperatorDecisionSnapshot Empty { get; } = new();

    public string OperatorId { get; init; } = string.Empty;
    public string OwnerProfileId { get; init; } = string.Empty;
    public string BotProfileId { get; init; } = string.Empty;
    public string Nickname { get; init; } = string.Empty;
    public bool Alive { get; init; }
    public Vector3 Position { get; init; }
    public float RealSpeed { get; init; }
    public VanguardMovementDecisionSnapshot Movement { get; init; } = VanguardMovementDecisionSnapshot.Empty;
    public VanguardBrainDecisionSnapshot Brain { get; init; } = VanguardBrainDecisionSnapshot.Empty;
    public VanguardSainDecisionSnapshot Sain { get; init; } = VanguardSainDecisionSnapshot.Empty;
    public VanguardThreatDecisionSnapshot Threat { get; init; } = VanguardThreatDecisionSnapshot.Empty;
    public VanguardGrenadeHazardDecisionSnapshot GrenadeHazard { get; init; } = VanguardGrenadeHazardDecisionSnapshot.Empty;
    public VanguardThreatScanDecisionSnapshot ThreatScan { get; init; } = VanguardThreatScanDecisionSnapshot.Empty;
    public VanguardMedicalDecisionSnapshot Medical { get; init; } = VanguardMedicalDecisionSnapshot.Empty;
    public VanguardAwarenessSnapshot Awareness { get; init; } = VanguardAwarenessSnapshot.Empty;
    public VanguardSquadCohesionSnapshot SquadCohesion { get; init; } = VanguardSquadCohesionSnapshot.Empty;
    public VanguardMovementAuthoritySnapshot MovementAuthority { get; init; } = VanguardMovementAuthoritySnapshot.Empty;
    public VanguardPrimaryExecutionDecisionSnapshot PrimaryExecution { get; init; } = VanguardPrimaryExecutionDecisionSnapshot.Empty;
    public VanguardLootDecisionSnapshot Looting { get; init; } = VanguardLootDecisionSnapshot.Empty;
    public VanguardCorpseLootDecisionSnapshot CorpseLoot { get; init; } = VanguardCorpseLootDecisionSnapshot.Empty;
    public VanguardOrbitDecisionSnapshot Orbit { get; init; } = VanguardOrbitDecisionSnapshot.Empty;
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string DecisionSignature => string.Join("|",
        Alive ? "alive" : "dead",
        Movement.Classification,
        Brain.ActiveLayer,
        Brain.Node,
        Sain.Classification,
        Threat.Classification,
        GrenadeHazard.DecisionSignature,
        ThreatScan.Classification,
        ThreatScan.WouldPromote ? "scan_promote" : "scan_keep",
        Medical.Classification,
        Medical.Plan.PlanKey,
        Medical.Plan.NextStep,
        Awareness.Classification,
        Awareness.DecisionSignature,
        SquadCohesion.DecisionSignature,
        MovementAuthority.DecisionSignature,
        Looting.Classification,
        CorpseLoot.DecisionSignature,
        Orbit.Classification);
}

internal sealed class VanguardPrimaryExecutionDecisionSnapshot
{
    public static VanguardPrimaryExecutionDecisionSnapshot Empty { get; } = new();

    public bool Active { get; init; }
    public string WindowKind { get; init; } = "none";
    public string IntentKey { get; init; } = "none";
    public string State { get; init; } = "none";

    public bool IsCorpseLoot => Active
        && string.Equals(WindowKind, VanguardPrimaryExecutionWindowKinds.CorpseLoot, StringComparison.OrdinalIgnoreCase);

    public bool IsWorldContainerLoot => Active
        && string.Equals(WindowKind, VanguardPrimaryExecutionWindowKinds.WorldContainerLoot, StringComparison.OrdinalIgnoreCase);

    public bool IsOpportunisticLoot => IsCorpseLoot || IsWorldContainerLoot;
}

internal sealed class VanguardMovementDecisionSnapshot
{
    public static VanguardMovementDecisionSnapshot Empty { get; } = new();

    public string MoverType { get; init; } = "none";
    public float RealSpeed { get; init; }
    public float? TargetSpeed { get; init; }
    public bool? Sprinting { get; init; }
    public bool? PlayerSprintEnabled { get; init; }
    public bool? PatrolPaused { get; init; }
    public bool? HasPath { get; init; }
    public float? DistanceToDestination { get; init; }
    public Vector3? TargetPoint { get; init; }
    public Vector3? DestinationPoint { get; init; }
    public Vector3? CurrentCornerPoint { get; init; }
    public Vector3? GoToPoint { get; init; }
    public float? GoToDistance { get; init; }
    public string PlayerState { get; init; } = "none";
    public string Classification { get; init; } = "movement_unknown";
}

internal sealed class VanguardBrainDecisionSnapshot
{
    public static VanguardBrainDecisionSnapshot Empty { get; } = new();

    public string BrainType { get; init; } = "none";
    public string ActiveLayer { get; init; } = "none";
    public string Node { get; init; } = "none";
    public string Reason { get; init; } = "none";
    public string CustomLayer { get; init; } = "none";
    public string CustomAction { get; init; } = "none";
    public string CustomReason { get; init; } = "none";
    public bool? VanillaGoalEnemyVisible { get; init; }
    public bool? VanillaGoalEnemyCanShoot { get; init; }
    public float? VanillaGoalEnemyDistance { get; init; }
    public string Classification { get; init; } = "brain_unknown";
}

internal sealed class VanguardSainDecisionSnapshot
{
    public static VanguardSainDecisionSnapshot Empty { get; } = new();

    public bool TypeLoaded { get; init; }
    public bool ComponentPresent { get; init; }
    public string ComponentType { get; init; } = "none";
    public bool? Active { get; init; }
    public bool? Standby { get; init; }
    public bool? LayersActive { get; init; }
    public string ActiveLayer { get; init; } = "none";
    public bool? IsInCombat { get; init; }
    public bool? HasEnemy { get; init; }
    public string CurrentAction { get; init; } = "none";
    public bool? HasDecision { get; init; }
    public string CombatDecision { get; init; } = "none";
    public string SquadDecision { get; init; } = "none";
    public string SelfDecision { get; init; } = "none";
    public string NativeGroupId { get; init; } = "none";
    public int NativeGroupMemberCount { get; init; }
    public string SainSquadGuid { get; init; } = "none";
    public int SainSquadMemberCount { get; init; }
    public string SainSquadLeaderId { get; init; } = "none";
    public bool SainSquadReady { get; init; }
    public float? TimeSinceDecisionChange { get; init; }
    public bool? RunningToCover { get; init; }
    public bool? Searching { get; init; }
    public string Classification { get; init; } = "sain_unknown";
}

internal sealed class VanguardThreatDecisionSnapshot
{
    public static VanguardThreatDecisionSnapshot Empty { get; } = new();

    public bool HasThreat { get; init; }
    public string EnemyId { get; init; } = "none";
    public string EnemyName { get; init; } = "none";
    public bool? EnemyKnown { get; init; }
    public bool? EnemyVisible { get; init; }
    public bool? EnemyLineOfSight { get; init; }
    public bool? EnemyCanShoot { get; init; }
    public float? Distance { get; init; }
    public float? TimeSinceSeen { get; init; }
    public float? TimeSinceHeard { get; init; }
    public float? TimeSinceKnownUpdated { get; init; }
    public float? PathLength { get; init; }
    public float? BotDistanceFromLastKnown { get; init; }
    public bool? ShotMeRecently { get; init; }
    public bool? ShotAtMeRecently { get; init; }
    public string EnemyAction { get; init; } = "none";
    public bool DirectThreat { get; init; }
    public bool ResidualThreat { get; init; }
    public bool StaleThreat { get; init; }
    public string Classification { get; init; } = "threat_none";
}


internal sealed class VanguardThreatScanDecisionSnapshot
{
    public static VanguardThreatScanDecisionSnapshot Empty { get; } = new();

    public bool Enabled { get; init; }
    public bool TypeLoaded { get; init; }
    public bool ComponentPresent { get; init; }
    public bool CombatContext { get; init; }
    public bool Scanned { get; init; }
    public string CurrentThreatId { get; init; } = "none";
    public string CurrentThreatName { get; init; } = "none";
    public int KnownCount { get; init; }
    public int VisibleCount { get; init; }
    public int LineOfSightCount { get; init; }
    public string CandidateThreatId { get; init; } = "none";
    public string CandidateThreatName { get; init; } = "none";
    public bool CandidateVisible { get; init; }
    public bool CandidateLineOfSight { get; init; }
    public bool CandidateCanShoot { get; init; }
    public bool CandidateShotMeRecently { get; init; }
    public bool CandidateShotAtMeRecently { get; init; }
    public bool CandidateIncomingFireFresh { get; init; }
    public bool CandidateIncomingFireStale { get; init; }
    public float? CandidateDistance { get; init; }
    public float? CandidateTimeSinceSeen { get; init; }
    public float? CandidateAngleDegrees { get; init; }
    public string CandidateArc { get; init; } = "none";
    public float CandidateScore { get; init; }
    public bool WouldPromote { get; init; }
    public string PromotionReason { get; init; } = "none";
    public string Classification { get; init; } = "threat_scan_disabled";

    public string DecisionSignature => string.Join("|",
        CombatContext ? "combat" : "no_combat",
        Scanned ? "scanned" : "not_scanned",
        CurrentThreatId,
        CandidateThreatId,
        CandidateArc,
        CandidateVisible ? "visible" : "not_visible",
        CandidateCanShoot ? "can_shoot" : "cannot_shoot",
        WouldPromote ? "would_promote" : "keep_current",
        PromotionReason,
        Classification);
}

internal sealed class VanguardMedicalDecisionSnapshot
{
    public static VanguardMedicalDecisionSnapshot Empty { get; } = new();

    public bool Alive { get; init; }
    public bool ControllerObserved { get; init; }
    public string ControllerType { get; init; } = "none";
    public VanguardMedicalNeedSnapshot Need { get; init; } = VanguardMedicalNeedSnapshot.Empty;
    public VanguardMedicalInventorySnapshot Inventory { get; init; } = VanguardMedicalInventorySnapshot.Empty;
    public VanguardMedicalActionabilitySnapshot Actionability { get; init; } = VanguardMedicalActionabilitySnapshot.Empty;
    public VanguardMedicalSafetySnapshot Safety { get; init; } = VanguardMedicalSafetySnapshot.Empty;
    public VanguardMedicalPlanSnapshot Plan { get; init; } = VanguardMedicalPlanSnapshot.Empty;
    public string Classification { get; init; } = "medical_readonly_minimal";
}

internal sealed class VanguardMedicalNeedSnapshot
{
    public static VanguardMedicalNeedSnapshot Empty { get; } = new();

    public bool IsReadable { get; init; }
    public VanguardMedicalNeed DominantNeed { get; init; } = VanguardMedicalNeed.None;
    public bool HasAnyNeed => DominantNeed != VanguardMedicalNeed.None;
    public int HealthPercent { get; init; } = 100;
    public bool HasHeavyBleed { get; init; }
    public bool HasLightBleed { get; init; }
    public bool HasFracture { get; init; }
    public bool HasPain { get; init; }
    public bool HasTremor { get; init; }
    public bool HasDestroyedPart { get; init; }
    public bool HasHpDamage { get; init; }
    public bool HasBlackBroken { get; init; }
    public bool HasOperableDestroyedPart { get; init; }
    public bool HasUntreatableVitalDamage { get; init; }
    public int UntreatableVitalPartCount { get; init; }
    public string UntreatableVitalParts { get; init; } = "none";
    public int DestroyedPartCount { get; init; }
    public int DamagedPartCount { get; init; }
    public int BrokenPartCount { get; init; }
    public bool TargetKnown { get; init; }
    public string TargetPart { get; init; } = "none";
    public string Badges { get; init; } = "none";
    public string DestroyedParts { get; init; } = "none";
    public string DamagedParts { get; init; } = "none";
    public string BrokenParts { get; init; } = "none";
    public string RawEffectNames { get; init; } = "none";
    public string Source { get; init; } = "none";

    public string Summary => "readable=" + Bool(IsReadable)
        + ";need=" + DominantNeed
        + ";hp=" + HealthPercent.ToString("0")
        + ";HB=" + Bool(HasHeavyBleed)
        + ";LB=" + Bool(HasLightBleed)
        + ";FR=" + Bool(HasFracture)
        + ";PN=" + Bool(HasPain)
        + ";TR=" + Bool(HasTremor)
        + ";black=" + Bool(HasDestroyedPart)
        + ";blackBroken=" + Bool(HasBlackBroken)
        + ";operableBlack=" + Bool(HasOperableDestroyedPart)
        + ";untreatableVital=" + Bool(HasUntreatableVitalDamage)
        + ";untreatableVitalCount=" + UntreatableVitalPartCount.ToString("0")
        + ";untreatableVitalParts=" + Safe(UntreatableVitalParts)
        + ";hpDamage=" + Bool(HasHpDamage)
        + ";targetKnown=" + Bool(TargetKnown)
        + ";target=" + Safe(TargetPart)
        + ";blackCount=" + DestroyedPartCount.ToString("0")
        + ";damagedCount=" + DamagedPartCount.ToString("0")
        + ";brokenCount=" + BrokenPartCount.ToString("0")
        + ";badges=" + Safe(Badges)
        + ";blackParts=" + Safe(DestroyedParts)
        + ";damagedParts=" + Safe(DamagedParts)
        + ";brokenParts=" + Safe(BrokenParts)
        + ";effects=" + Safe(RawEffectNames)
        + ";source=" + Safe(Source);

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
}

internal sealed class VanguardMedicalInventorySnapshot
{
    public static VanguardMedicalInventorySnapshot Empty { get; } = new();

    public bool Observed { get; init; }
    public int AcceptableItemCount { get; init; }
    public int MedicalTemplateCount { get; init; }
    public string CandidateTemplateIds { get; init; } = "none";
    public string CandidateNames { get; init; } = "none";
    public string Source { get; init; } = "none";
}

internal sealed class VanguardMedicalActionabilitySnapshot
{
    public static VanguardMedicalActionabilitySnapshot Empty { get; } = new();

    public bool ItemCatalogKnown { get; init; }
    public bool RequiredItemAvailable { get; init; }
    public string SelectedItemName { get; init; } = "none";
    public string SelectedItemTemplateId { get; init; } = "none";
    public string SelectedItemRole { get; init; } = "none";
    public string SelectedItemActionKind { get; init; } = "none";
    public string SelectedItemNotes { get; init; } = "none";
    public bool TargetKnown { get; init; }
    public string TargetPart { get; init; } = "none";
    public bool? CanApplyItem { get; init; }
    public bool PersistentCapabilityAvailable { get; init; }
    public bool HandsReadyForMedicalAction { get; init; }
    public bool CanApplyProbeDeferredByHands { get; init; }
    public bool AnyMedicineUsing { get; init; }
    public bool FirstAidUsing { get; init; }
    public bool SurgicalKitUsing { get; init; }
    public bool StimulatorUsing { get; init; }
    public bool Reloading { get; init; }
    public bool GrenadeThrowing { get; init; }
    public string Classification { get; init; } = "medical_actionability_unknown";

    public string Summary => "itemAvailable=" + Bool(RequiredItemAvailable)
        + ";item=" + Safe(SelectedItemName)
        + ";tpl=" + Safe(SelectedItemTemplateId)
        + ";role=" + Safe(SelectedItemRole)
        + ";action=" + Safe(SelectedItemActionKind)
        + ";targetKnown=" + Bool(TargetKnown)
        + ";target=" + Safe(TargetPart)
        + ";canApply=" + NullableBool(CanApplyItem)
        + ";persistentCapability=" + Bool(PersistentCapabilityAvailable)
        + ";handsReady=" + Bool(HandsReadyForMedicalAction)
        + ";applyProbeDeferredByHands=" + Bool(CanApplyProbeDeferredByHands)
        + ";using=" + Bool(AnyMedicineUsing)
        + ";firstAidUsing=" + Bool(FirstAidUsing)
        + ";surgeryUsing=" + Bool(SurgicalKitUsing)
        + ";stimUsing=" + Bool(StimulatorUsing)
        + ";reloading=" + Bool(Reloading)
        + ";grenade=" + Bool(GrenadeThrowing)
        + ";class=" + Safe(Classification);

    private static string Bool(bool value) => value ? "true" : "false";
    private static string NullableBool(bool? value) => value.HasValue ? Bool(value.Value) : "unknown";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
}

internal sealed class VanguardMedicalSafetySnapshot
{
    public static VanguardMedicalSafetySnapshot Empty { get; } = new();

    public bool DirectThreat { get; init; }
    public bool ResidualThreat { get; init; }
    public bool StaleThreat { get; init; }
    public bool EnemyVisible { get; init; }
    public bool EnemyCanShoot { get; init; }
    public bool ThreatScanWouldPromote { get; init; }
    public bool IncomingFireRecent { get; init; }
    public bool ImmediateCombatBlock { get; init; }
    public bool CoveredSuppressionOpportunity { get; init; }
    public float? ThreatDistance { get; init; }
    public bool SafeForMobileAid { get; init; }
    public bool SafeForStationarySurgery { get; init; }
    public bool SafeForStationaryAid { get; init; }
    public bool CoveredOrHoldingAngle { get; init; }
    public bool SurgeryAreaClear { get; init; }
    public bool SurgeryRequiresCover { get; init; }
    public bool SurgeryThreatRecentlySeen { get; init; }
    public bool SurgeryThreatRecentlyKnown { get; init; }
    public bool SurgeryThreatPathTooClose { get; init; }
    public bool SurgeryThreatDistanceTooClose { get; init; }
    public string SurgeryAreaClearReason { get; init; } = "none";
    public string Reason { get; init; } = "none";
}


internal sealed class VanguardSquadCohesionSnapshot
{
    public static VanguardSquadCohesionSnapshot Empty { get; } = new();

    public bool Enabled { get; init; }
    public bool ReadOnly { get; init; } = true;
    public bool OwnerKnown { get; init; }
    public string OwnerProfileId { get; init; } = "none";
    public bool OwnerReliableForActiveMovement { get; init; }
    public string OwnerAnchorSource { get; init; } = "unknown";
    public float OwnerAnchorAgeSeconds { get; init; }
    public Vector3? OwnerPosition { get; init; }
    public Vector3? OwnerForward { get; init; }
    public float OperatorDistanceToOwner { get; init; }
    public float VerticalDelta { get; init; }
    public float BubbleRadius { get; init; } = VanguardSquadCohesionDoctrine.TacticalBubbleRadiusMeters;
    public string BubbleBand { get; init; } = "unknown";
    public bool InBubble { get; init; }
    public string Sector { get; init; } = "unknown";
    public string TacticalRole { get; init; } = "unassigned";
    public string TacticalEnvironmentKind { get; init; } = "environment_unknown";
    public string TacticalPlacementMode { get; init; } = "placement_observe_readonly";
    public bool CorridorLike { get; init; }
    public bool WideLateralAllowed { get; init; }
    public bool AdjacentRoomAllowed { get; init; }
    public bool SectorTopologyValid { get; init; }
    public string SectorTopologyReason { get; init; } = "none";
    public float OwnerToOperatorDirectDistance { get; init; }
    public float OwnerToOperatorPathDistance { get; init; }
    public float OwnerToOperatorPathRatio { get; init; }
    public int OwnerToOperatorPathCorners { get; init; }
    public float SignedAngleFromOwnerForward { get; init; }
    public int SameOwnerOperatorCount { get; init; }
    public int SameSectorCount { get; init; }
    public int RearSectorCount { get; init; }
    public bool SectorDuplicate { get; init; }
    public bool RearOverstacked { get; init; }
    public bool UsefulPosition { get; init; }
    public bool DirectThreat { get; init; }
    public string SainEnvelope { get; init; } = "formation_hold_angle_readonly";
    public string SquadOrder { get; init; } = "tactical";
    public string RecommendedIntent { get; init; } = "MaintainTacticalBubbleReadOnly";
    public string Classification { get; init; } = "cohesion_unread";
    public string Reason { get; init; } = "none";

    public string DecisionSignature => string.Join("|",
        Enabled ? "enabled" : "disabled",
        OwnerKnown ? "owner_known" : "owner_unknown",
        OwnerReliableForActiveMovement ? "owner_reliable" : "owner_readonly_or_unknown",
        OwnerAnchorSource,
        InBubble ? "in_bubble" : "outside_bubble",
        BubbleBand,
        Sector,
        TacticalRole,
        TacticalEnvironmentKind,
        TacticalPlacementMode,
        SectorTopologyValid ? "topology_valid" : "topology_invalid",
        SectorTopologyReason,
        CorridorLike ? "corridor_like" : "not_corridor_like",
        WideLateralAllowed ? "wide_lateral" : "no_wide_lateral",
        AdjacentRoomAllowed ? "adjacent_room" : "no_adjacent_room",
        SectorDuplicate ? "sector_duplicate" : "sector_ok",
        RearOverstacked ? "rear_overstacked" : "rear_ok",
        UsefulPosition ? "useful" : "review",
        SainEnvelope,
        SquadOrder,
        RecommendedIntent,
        Classification);
}



internal sealed class VanguardMovementContractSnapshot
{
    public static VanguardMovementContractSnapshot Empty { get; } = new();

    public string ContractKey { get; init; } = "none";
    public string RequestKind { get; init; } = "none";
    public string Backend { get; init; } = "none";
    public bool MovementLeaseEligible { get; init; }
    public bool WouldSuppressLootingBots { get; init; }
    public bool WouldSuppressOrbit { get; init; }
    public bool WouldSuppressSainSearch { get; init; }
    public string Reason { get; init; } = "none";
    public bool ReadOnly { get; init; } = true;

    public string DecisionSignature => string.Join("|",
        ContractKey,
        RequestKind,
        Backend,
        MovementLeaseEligible ? "lease_eligible" : "no_lease",
        WouldSuppressLootingBots ? "suppress_loot" : "loot_ok",
        WouldSuppressOrbit ? "suppress_orbit" : "orbit_ok",
        WouldSuppressSainSearch ? "suppress_sain_search" : "sain_ok",
        Reason);
}

internal sealed class VanguardMovementLeasePlanSnapshot
{
    public static VanguardMovementLeasePlanSnapshot Empty { get; } = new();

    public string LeaseKey { get; init; } = "none";
    public string Backend { get; init; } = "none";
    public string AnchorKind { get; init; } = "none";
    public float AnchorRadiusMeters { get; init; }
    public bool Eligible { get; init; }
    public bool ApplyEnabled { get; init; }
    public bool SuppressLootingBots { get; init; }
    public bool SuppressOrbit { get; init; }
    public bool SuppressSainSearch { get; init; }
    public float MinDurationSeconds { get; init; }
    public float MaxDurationSeconds { get; init; }
    public float NoProgressTimeoutSeconds { get; init; }
    public string ReapplyPolicy { get; init; } = "none";
    public string CompletionRule { get; init; } = "none";
    public string InterruptionRule { get; init; } = "none";
    public string Reason { get; init; } = "none";
    public bool ReadOnly { get; init; } = true;

    public string DecisionSignature => string.Join("|",
        LeaseKey,
        Backend,
        AnchorKind,
        AnchorRadiusMeters.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
        Eligible ? "eligible" : "not_eligible",
        ApplyEnabled ? "apply_enabled" : "apply_disabled",
        SuppressLootingBots ? "suppress_loot" : "loot_ok",
        SuppressOrbit ? "suppress_orbit" : "orbit_ok",
        SuppressSainSearch ? "suppress_sain_search" : "sain_ok",
        ReapplyPolicy,
        CompletionRule,
        InterruptionRule,
        Reason);
}

internal sealed class VanguardMovementBrokerPlanSnapshot
{
    public static VanguardMovementBrokerPlanSnapshot Empty { get; } = new();

    public string PlanKey { get; init; } = "none";
    public string Backend { get; init; } = "none";
    public bool WouldOpenLease { get; init; }
    public bool WouldSuppressLootingBots { get; init; }
    public bool WouldSuppressOrbit { get; init; }
    public bool WouldSuppressSainSearch { get; init; }
    public string AnchorKind { get; init; } = "none";
    public string RequestKind { get; init; } = "none";
    public string Reason { get; init; } = "none";
    public bool ReadOnly { get; init; } = true;
    public VanguardMovementContractSnapshot Contract { get; init; } = VanguardMovementContractSnapshot.Empty;
    public VanguardMovementLeasePlanSnapshot LeasePlan { get; init; } = VanguardMovementLeasePlanSnapshot.Empty;

    public string DecisionSignature => string.Join("|",
        PlanKey,
        Backend,
        WouldOpenLease ? "would_open" : "no_lease",
        WouldSuppressLootingBots ? "suppress_loot" : "loot_ok",
        WouldSuppressOrbit ? "suppress_orbit" : "orbit_ok",
        WouldSuppressSainSearch ? "suppress_sain_search" : "sain_ok",
        RequestKind,
        AnchorKind,
        Contract.DecisionSignature,
        LeasePlan.DecisionSignature,
        Reason);
}

internal sealed class VanguardMovementAuthoritySnapshot
{
    public static VanguardMovementAuthoritySnapshot Empty { get; } = new();

    public bool Enabled { get; init; }
    public bool ReadOnly { get; init; } = true;
    public bool ActiveMovementAllowed { get; init; }
    public string CurrentAuthority { get; init; } = "unknown";
    public string CurrentAuthorityReason { get; init; } = "none";
    public bool OwnerKnown { get; init; }
    public bool OwnerReliableForActiveMovement { get; init; }
    public string OwnerAnchorSource { get; init; } = "unknown";
    public float OwnerAnchorAgeSeconds { get; init; }
    public bool SoftOutsideBubble { get; init; }
    public bool HardOutsideBubble { get; init; }
    public bool SainSearchLike { get; init; }
    public bool SainLocalDefensiveLike { get; init; }
    public bool SainEnvelopeViolation { get; init; }
    public string SainEnvelopeViolationReason { get; init; } = "none";
    public bool LootingBotsAllowed { get; init; }
    public bool LootingBotsWouldSuppress { get; init; }
    public bool OrbitAllowed { get; init; }
    public bool OrbitWouldSuppress { get; init; }
    public bool EftPathActive { get; init; }
    public bool MovementStallSuspect { get; init; }
    public string Classification { get; init; } = "movement_authority_unread";
    public string Reason { get; init; } = "none";
    public VanguardMovementBrokerPlanSnapshot BrokerPlan { get; init; } = VanguardMovementBrokerPlanSnapshot.Empty;

    public string DecisionSignature => string.Join("|",
        Enabled ? "enabled" : "disabled",
        CurrentAuthority,
        OwnerKnown ? "owner_known" : "owner_unknown",
        OwnerReliableForActiveMovement ? "owner_reliable" : "owner_readonly_or_unknown",
        SoftOutsideBubble ? "soft_outside" : "not_soft_outside",
        HardOutsideBubble ? "hard_outside" : "not_hard_outside",
        SainEnvelopeViolation ? "sain_violation" : "sain_ok",
        LootingBotsWouldSuppress ? "loot_suppress" : "loot_ok",
        OrbitWouldSuppress ? "orbit_suppress" : "orbit_ok",
        Classification,
        BrokerPlan.DecisionSignature);
}

internal sealed class VanguardLootDecisionSnapshot
{
    public static VanguardLootDecisionSnapshot Empty { get; } = new();

    public bool TypeLoaded { get; init; }
    public bool ComponentPresent { get; init; }
    public string ComponentType { get; init; } = "none";
    public string FinderType { get; init; } = "none";
    public bool? BrainEnabled { get; init; }
    public bool? BotLooting { get; init; }
    public bool? LootTaskRunning { get; init; }
    public bool? HasActiveLootable { get; init; }
    public string ActiveLootType { get; init; } = "none";
    public float? DistanceToLoot { get; init; }
    public bool? HasFreeSpace { get; init; }
    public string AvailableGridSpaces { get; init; } = "none";
    public bool? ScanScheduled { get; init; }
    public bool? ScanRunning { get; init; }
    public string Classification { get; init; } = "loot_unknown";
}

internal sealed class VanguardOrbitDecisionSnapshot
{
    public static VanguardOrbitDecisionSnapshot Empty { get; } = new();

    public bool TelemetryLoaded { get; init; }
    public bool Available { get; init; }
    public bool Active { get; init; }
    public string Status { get; init; } = "none";
    public string Category { get; init; } = "none";
    public bool? IsLeader { get; init; }
    public Vector3? Objective { get; init; }
    public string ExtractReason { get; init; } = "none";
    public string Classification { get; init; } = "orbit_unknown";
}
#endif
