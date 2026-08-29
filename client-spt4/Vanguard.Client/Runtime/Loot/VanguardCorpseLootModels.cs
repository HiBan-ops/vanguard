#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EFT.InventoryLogic;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Raid.Runtime;
using UnityEngine;

// Responsibility: defines the immutable/read-mostly shapes exchanged by corpse-loot permission, need, hostility, candidate evaluation, dry-run planning and transaction stages.
// Flow: Readers/evaluators populate these snapshots from current raid/inventory facts, selectors and executors pass them forward, and telemetry records their outcomes without the models orchestrating any action themselves.
// Authority boundary: these types carry facts and plans only; the services that produce them and EFT inventory/runtime state retain all decision and mutation authority.
// Invariant: construction is side-effect free, defaults are conservative, and a serialized/runtime snapshot must preserve enough identity/context for later stages to reject stale or mismatched work.
namespace Vanguard.Client.Runtime.Loot;

internal sealed class VanguardOperatorLootPermissionSnapshot
{
    public static VanguardOperatorLootPermissionSnapshot CaptureRuntime(string? ownerProfileId = null)
        => CaptureRuntime(ownerProfileId, VanguardOperatorLootTargetPolicyMode.CorpsesOnly, "legacy_or_unbound_operator_fallback");

    public static VanguardOperatorLootPermissionSnapshot CaptureRuntime(VanguardRaidOperatorRuntimeRecord record)
        => CaptureRuntime(record?.OwnerProfileId, ParseOperatorPolicy(record?.LootTargetPolicy), "persistent_operator_profile_manifest");

    public static VanguardOperatorLootPermissionSnapshot CaptureRuntime(string? ownerProfileId, string? operatorPolicy)
        => CaptureRuntime(ownerProfileId, ParseOperatorPolicy(operatorPolicy), "explicit_operator_policy");

    private static VanguardOperatorLootPermissionSnapshot CaptureRuntime(string? ownerProfileId, VanguardOperatorLootTargetPolicyMode operatorPolicy, string policySource)
    {
        var settings = VanguardRuntimeSettingsAuthorityResolver.ResolvePlayerScoped(ownerProfileId, "loot_permission_fallback");
        bool runtimeLootEnabled = settings.MovementOpportunisticLootBrokerEnabled
            && settings.LootOperationalSessionEnabled;
        return new VanguardOperatorLootPermissionSnapshot
        {
            // Persistent Operator policy is the first authority. F12/runtime tuning is a
            // second AND gate only; it cannot turn an Operator-level deny into an allow.
            OperatorTargetPolicy = operatorPolicy,
            OperatorTargetPolicySource = policySource,
            LootAccessibleCorpses = runtimeLootEnabled,
            LootWorldContainers = runtimeLootEnabled,
            LootMedicalItems = settings.LootMedicalItemsEnabled,
            LootCompatibleMagazines = settings.LootCompatibleMagazinesEnabled,
            LootCompatibleLooseAmmunition = settings.LootCompatibleLooseAmmunitionEnabled,
            LootGrenades = settings.LootGrenadesEnabled,
            FillEmptyLongWeaponSlot = settings.LootBackupLongWeaponEnabled,
            FillEmptyHolsterSlot = settings.LootBackupPistolEnabled
        };
    }

    private static VanguardOperatorLootTargetPolicyMode ParseOperatorPolicy(string? value)
    {
        if (string.Equals(value, "ContainersOnly", StringComparison.OrdinalIgnoreCase)) return VanguardOperatorLootTargetPolicyMode.ContainersOnly;
        if (string.Equals(value, "CorpsesAndContainers", StringComparison.OrdinalIgnoreCase)) return VanguardOperatorLootTargetPolicyMode.CorpsesAndContainers;
        if (string.Equals(value, "Disabled", StringComparison.OrdinalIgnoreCase)) return VanguardOperatorLootTargetPolicyMode.Disabled;
        return VanguardOperatorLootTargetPolicyMode.CorpsesOnly;
    }

    public VanguardOperatorLootTargetPolicyMode OperatorTargetPolicy { get; init; } = VanguardOperatorLootTargetPolicyMode.CorpsesOnly;
    public string OperatorTargetPolicySource { get; init; } = "legacy_or_unbound_operator_fallback";
    public bool LootAccessibleCorpses { get; init; } = true;
    public bool LootWorldContainers { get; init; }
    public bool LootMedicalItems { get; init; } = true;
    public bool LootCompatibleMagazines { get; init; } = true;
    public bool LootCompatibleLooseAmmunition { get; init; } = true;
    public bool LootGrenades { get; init; } = true;
    public bool FillEmptyLongWeaponSlot { get; init; } = true;
    public bool FillEmptyHolsterSlot { get; init; } = true;
    public bool ReplacePrimaryWeapon { get; init; }
    public bool ReplaceSecondPrimaryWeapon { get; init; }
    public bool ReplaceHolsterWeapon { get; init; }
    public bool ReplaceRig { get; init; }
    public bool ReplaceArmor { get; init; }
    public bool ReplaceBackpack { get; init; }

    public string DecisionSignature => string.Join("|",
        "operatorPolicy=" + OperatorTargetPolicy,
        "operatorPolicySource=" + OperatorTargetPolicySource,
        LootAccessibleCorpses ? "corpses" : "no_corpses",
        LootWorldContainers ? "containers" : "no_containers",
        LootMedicalItems ? "medical" : "no_medical",
        LootCompatibleMagazines ? "magazines" : "no_magazines",
        LootCompatibleLooseAmmunition ? "loose_ammunition" : "no_loose_ammunition",
        LootGrenades ? "grenades" : "no_grenades",
        FillEmptyLongWeaponSlot ? "fill_empty_long_weapon_slot" : "no_empty_long_weapon_slot",
        FillEmptyHolsterSlot ? "fill_holster" : "no_holster",
        ReplacePrimaryWeapon ? "replace_primary" : "protect_primary",
        ReplaceSecondPrimaryWeapon ? "replace_second_primary" : "protect_second_primary",
        ReplaceHolsterWeapon ? "replace_holster" : "protect_holster",
        ReplaceRig ? "replace_rig" : "protect_rig",
        ReplaceArmor ? "replace_armor" : "protect_armor",
        ReplaceBackpack ? "replace_backpack" : "protect_backpack");
}

internal sealed class VanguardOperatorLootNeedSnapshot
{
    public static VanguardOperatorLootNeedSnapshot Empty { get; } = new();

    public bool Observed { get; init; }
    public bool HasPrimaryWeapon { get; init; }
    public int LongWeaponCount { get; init; }
    public EquipmentSlot? ProtectedPrimarySlot { get; init; }
    public EquipmentSlot? EmptyLongWeaponSlot { get; init; }
    public bool HasEmptyLongWeaponSlot => EmptyLongWeaponSlot.HasValue;
    public bool HolsterSlotEmpty { get; init; }
    public int CompatibleMagazineCount { get; init; }
    public int CompatibleMagazineAmmoCount { get; init; }
    public int CompatibleLooseAmmunitionCount { get; init; }
    public int GrenadeCount { get; init; }
    public int MedicalItemCount { get; init; }
    public bool HasHeavyBleedTreatment { get; init; }
    public bool HasLightBleedTreatment { get; init; }
    public bool HasFractureTreatment { get; init; }
    public bool HasHpTreatment { get; init; }
    public bool HasPainMobilityTreatment { get; init; }
    public bool HasSurgeryTreatment { get; init; }
    public string Source { get; init; } = "none";

    public int CompatibleAmmunitionCount => CompatibleMagazineAmmoCount + CompatibleLooseAmmunitionCount;
    public bool NeedsCompatibleMagazine => CompatibleMagazineCount < 2 || CompatibleMagazineAmmoCount < 25;
    public bool NeedsCompatibleAmmunition => CompatibleAmmunitionCount < 90;
    public bool NeedsGrenade => GrenadeCount < 2;
    public bool NeedsAnyMedicalCapability => !HasHeavyBleedTreatment
        || !HasLightBleedTreatment
        || !HasFractureTreatment
        || !HasHpTreatment
        || !HasPainMobilityTreatment
        || !HasSurgeryTreatment;

    public string DecisionSignature => string.Join("|",
        Observed ? "observed" : "unread",
        HasPrimaryWeapon ? "primary" : "no_primary",
        $"longWeapons={LongWeaponCount}",
        $"protectedPrimary={ProtectedPrimarySlot?.ToString() ?? "none"}",
        $"emptyLongSlot={EmptyLongWeaponSlot?.ToString() ?? "none"}",
        HolsterSlotEmpty ? "holster_empty" : "holster_filled",
        $"mags={CompatibleMagazineCount}",
        $"magAmmo={CompatibleMagazineAmmoCount}",
        $"looseAmmo={CompatibleLooseAmmunitionCount}",
        $"grenades={GrenadeCount}",
        HasHeavyBleedTreatment ? "heavy_ok" : "heavy_missing",
        HasLightBleedTreatment ? "light_ok" : "light_missing",
        HasFractureTreatment ? "fracture_ok" : "fracture_missing",
        HasHpTreatment ? "hp_ok" : "hp_missing",
        HasPainMobilityTreatment ? "pain_ok" : "pain_missing",
        HasSurgeryTreatment ? "surgery_ok" : "surgery_missing");
}

internal sealed class VanguardCorpseHostilityEvidence
{
    public static VanguardCorpseHostilityEvidence Unverified { get; } = new();

    public bool Verified { get; init; }
    public bool HostileConfirmed { get; init; }
    public bool AlliedAiEligible { get; init; }
    public bool DeadOperatorCorpse { get; init; }
    public bool FriendlyOperatorCorpse { get; init; }
    public bool NonFriendlyOperatorCorpse { get; init; }
    public bool FriendlyExcluded { get; init; }
    public string RelationshipKind { get; init; } = "unverified";
    public string Source { get; init; } = "unverified";
    public string Reason { get; init; } = "no_relationship_proof";
    public float AgeSeconds { get; init; } = -1f;

    public string DecisionSignature => string.Join("|",
        Verified ? "eligible" : "ineligible",
        HostileConfirmed ? "hostile_confirmed" : "not_hostile_confirmed",
        AlliedAiEligible ? "allied_ai_eligible" : "not_allied_ai",
        DeadOperatorCorpse ? "dead_operator" : "not_operator",
        FriendlyOperatorCorpse ? "friendly_operator_corpse" : "not_friendly_operator_corpse",
        NonFriendlyOperatorCorpse ? "nonfriendly_operator_corpse" : "not_nonfriendly_operator_corpse",
        FriendlyExcluded ? "friendly_excluded" : "not_friendly_excluded",
        RelationshipKind,
        Source,
        Reason,
        $"age={AgeSeconds:0.00}");
}

internal sealed class VanguardCorpseLootItemPlanEntry
{
    public string ItemId { get; init; } = "none";
    public string TemplateId { get; init; } = "none";
    public string Name { get; init; } = "none";
    public string Category { get; init; } = "none";
    public string Reason { get; init; } = "none";
    public string SourcePath { get; init; } = "none";
    public string Destination { get; init; } = "none";
    public string PlacementOperation { get; init; } = "none";
    public bool PlacementPossible { get; init; }
    public int Quantity { get; init; } = 1;
    public int CellCount { get; init; } = 1;
    public float EstimatedWeightKg { get; init; }
    public float Score { get; init; }
    public string StopCondition { get; init; } = "none";

    public string DecisionSignature => string.Join("|",
        ItemId,
        Category,
        Reason,
        Destination,
        PlacementOperation,
        PlacementPossible ? "place_ok" : "place_blocked",
        $"qty={Quantity}",
        $"cells={CellCount}",
        $"weight={EstimatedWeightKg:0.000}",
        $"score={Score:0.0}",
        StopCondition);
}

internal sealed class VanguardCorpseLootDryRunPlan
{
    public static VanguardCorpseLootDryRunPlan Empty { get; } = new();

    public IReadOnlyList<VanguardCorpseLootItemPlanEntry> Entries { get; init; } = Array.Empty<VanguardCorpseLootItemPlanEntry>();
    public int UsefulItemCount { get; init; }
    public int FeasibleItemCount { get; init; }
    public int NoDestinationCount { get; init; }
    public int PlannedMedicalCount { get; init; }
    public int PlannedMagazineCount { get; init; }
    public int PlannedLooseAmmunitionStackCount { get; init; }
    public int PlannedLooseAmmunitionRoundCount { get; init; }
    public int PlannedGrenadeCount { get; init; }
    public int PlannedLongWeaponCount { get; init; }
    public int PlannedHolsterWeaponCount { get; init; }
    public int EstimatedCellCount { get; init; }
    public int PlacementPreviewCount { get; init; }
    public int PlacementPreviewBudgetTruncatedCount { get; init; }
    public float EstimatedWeightKg { get; init; }
    public float TotalScore { get; init; }
    public string HighestPriorityReason { get; init; } = "none";
    public string StopCondition { get; init; } = "none";

    public string CompactSummary => $"entries={Entries.Count}; feasible={FeasibleItemCount}; blocked={NoDestinationCount}; med={PlannedMedicalCount}; mags={PlannedMagazineCount}; ammoStacks={PlannedLooseAmmunitionStackCount}; ammoRounds={PlannedLooseAmmunitionRoundCount}; grenades={PlannedGrenadeCount}; long={PlannedLongWeaponCount}; holster={PlannedHolsterWeaponCount}; cells={EstimatedCellCount}; previews={PlacementPreviewCount}; previewTruncated={PlacementPreviewBudgetTruncatedCount}; weight={EstimatedWeightKg:0.000}; score={TotalScore:0.0}; highest={HighestPriorityReason}; stop={StopCondition}";

    public string DecisionSignature => string.Join("|",
        $"useful={UsefulItemCount}",
        $"feasible={FeasibleItemCount}",
        $"blocked={NoDestinationCount}",
        $"medical={PlannedMedicalCount}",
        $"mags={PlannedMagazineCount}",
        $"ammoStacks={PlannedLooseAmmunitionStackCount}",
        $"ammoRounds={PlannedLooseAmmunitionRoundCount}",
        $"grenades={PlannedGrenadeCount}",
        $"long={PlannedLongWeaponCount}",
        $"holster={PlannedHolsterWeaponCount}",
        $"cells={EstimatedCellCount}",
        $"previews={PlacementPreviewCount}",
        $"previewTruncated={PlacementPreviewBudgetTruncatedCount}",
        $"weight={EstimatedWeightKg:0.000}",
        $"score={TotalScore:0.0}",
        HighestPriorityReason,
        StopCondition);
}

internal sealed class VanguardCorpseLootInventorySummary
{
    public static VanguardCorpseLootInventorySummary Empty { get; } = new();

    public int MedicalItemCount { get; init; }
    public int MissingCapabilityMedicalCount { get; init; }
    public int CompatibleMagazineCount { get; init; }
    public int CompatibleMagazineAmmoCount { get; init; }
    public int CompatibleLooseAmmunitionStackCount { get; init; }
    public int CompatibleLooseAmmunitionRoundCount { get; init; }
    public int GrenadeCount { get; init; }
    public int UsableEmptyLongWeaponSlotCount { get; init; }
    public int UsableHolsterWeaponCount { get; init; }
    public int TotalUsefulItemCount { get; init; }
    public int FeasibleItemCount { get; init; }
    public int NoDestinationCount { get; init; }
    public string HighestPriorityReason { get; init; } = "none";

    public string DecisionSignature => string.Join("|",
        $"medical={MedicalItemCount}",
        $"missingMed={MissingCapabilityMedicalCount}",
        $"mags={CompatibleMagazineCount}",
        $"magAmmo={CompatibleMagazineAmmoCount}",
        $"ammoStacks={CompatibleLooseAmmunitionStackCount}",
        $"ammoRounds={CompatibleLooseAmmunitionRoundCount}",
        $"grenades={GrenadeCount}",
        $"emptyLongSlotWeapons={UsableEmptyLongWeaponSlotCount}",
        $"holster={UsableHolsterWeaponCount}",
        $"useful={TotalUsefulItemCount}",
        $"feasible={FeasibleItemCount}",
        $"noDestination={NoDestinationCount}",
        HighestPriorityReason);
}

internal sealed class VanguardCorpseLootCandidateEvaluation
{
    public string CorpseId { get; init; } = "none";
    public string VictimProfileId { get; init; } = "none";
    public float DirectDistanceMeters { get; init; }
    public float PathDistanceMeters { get; init; }
    public bool PathComplete { get; init; }
    public bool Included { get; init; }
    public string Gate { get; init; } = "none";
    public VanguardCorpseHostilityEvidence Hostility { get; init; } = VanguardCorpseHostilityEvidence.Unverified;
    public VanguardCorpseLootDryRunPlan Plan { get; init; } = VanguardCorpseLootDryRunPlan.Empty;
    public float CompatibilityBonus { get; init; }
    public float Score { get; init; }

    public string DecisionSignature => string.Join("|",
        CorpseId,
        Included ? "included" : "excluded",
        Gate,
        Hostility.DecisionSignature,
        PathComplete ? "path_complete" : "path_incomplete",
        $"direct={DirectDistanceMeters:0.0}",
        $"path={PathDistanceMeters:0.0}",
        $"compatibilityBonus={CompatibilityBonus:0.0}",
        $"score={Score:0.0}",
        Plan.DecisionSignature);
}

internal sealed class VanguardCorpseLootEvaluationCounts
{
    public int RegisteredSnapshotCount { get; init; }
    public int NearbyCount { get; init; }
    public int PlayerExcludedCount { get; init; }
    public int FriendlyExcludedCount { get; init; }
    public int HostilityUnverifiedCount { get; init; }
    public int OutcomeMemoryExcludedCount { get; init; }
    public int NotUsefulCount { get; init; }
    public int NoDestinationCount { get; init; }
    public int PathIncompleteCount { get; init; }
    public int PathBudgetDeferredCount { get; init; }
    public int PlanBudgetDeferredCount { get; init; }
    public int IncludedCount { get; init; }

    public string DecisionSignature => string.Join("|",
        $"registered={RegisteredSnapshotCount}",
        $"nearby={NearbyCount}",
        $"player={PlayerExcludedCount}",
        $"friendly={FriendlyExcludedCount}",
        $"unverified={HostilityUnverifiedCount}",
        $"outcomeMemory={OutcomeMemoryExcludedCount}",
        $"notUseful={NotUsefulCount}",
        $"noDestination={NoDestinationCount}",
        $"pathIncomplete={PathIncompleteCount}",
        $"pathBudgetDeferred={PathBudgetDeferredCount}",
        $"planBudgetDeferred={PlanBudgetDeferredCount}",
        $"included={IncludedCount}");
}

internal sealed class VanguardCorpseLootDecisionSnapshot
{
    public static VanguardCorpseLootDecisionSnapshot Empty { get; } = new();

    public bool Enabled { get; init; } = true;
    public bool Observed { get; init; }
    public bool ReadOnly { get; init; } = true;
    public bool ExecutionEnabled { get; init; }
    public VanguardOperatorLootPermissionSnapshot Permissions { get; init; } = VanguardOperatorLootPermissionSnapshot.CaptureRuntime();
    public VanguardOperatorLootNeedSnapshot OperatorNeed { get; init; } = VanguardOperatorLootNeedSnapshot.Empty;
    public bool CandidateFound { get; init; }
    public bool EligibleIfActivated { get; init; }
    public string CandidateCorpseId { get; init; } = "none";
    public string VictimProfileId { get; init; } = "none";
    public string VictimName { get; init; } = "none";
    public string VictimSide { get; init; } = "none";
    public Vector3? CorpsePosition { get; init; }
    public float DirectDistanceMeters { get; init; }
    public float PathDistanceMeters { get; init; }
    public bool PathComplete { get; init; }
    public bool RelationshipEligible { get; init; }
    public bool HostileVerified { get; init; }
    public bool DeadOperatorCorpse { get; init; }
    public string RelationshipKind { get; init; } = "unverified";
    public string HostilitySource { get; init; } = "unverified";
    public string HostilityReason { get; init; } = "none";
    public bool FriendlyExcluded { get; init; }
    public float EquipmentCompatibilityBonus { get; init; }
    public float UtilityScore { get; init; }
    public long ManifestRevision { get; init; }
    public long InterestRevision { get; init; }
    public string LootNeedSignature { get; init; } = "none";
    public string Gate { get; init; } = "no_candidate";
    public string Reason { get; init; } = "none";
    public VanguardCorpseLootInventorySummary Inventory { get; init; } = VanguardCorpseLootInventorySummary.Empty;
    public VanguardCorpseLootDryRunPlan Plan { get; init; } = VanguardCorpseLootDryRunPlan.Empty;
    public VanguardCorpseLootEvaluationCounts Counts { get; init; } = new();
    public IReadOnlyList<VanguardCorpseLootCandidateEvaluation> CandidateEvaluations { get; init; } = Array.Empty<VanguardCorpseLootCandidateEvaluation>();
    public DateTimeOffset EvaluatedAtUtc { get; init; } = DateTimeOffset.MinValue;
    public float EvaluationDurationMilliseconds { get; init; }

    public string Classification => !Observed
        ? "corpse_loot_not_observed"
        : CandidateFound
            ? EligibleIfActivated
                ? ExecutionEnabled ? "corpse_loot_approach_candidate" : "corpse_loot_candidate_readonly"
                : "corpse_loot_candidate_blocked"
            : "corpse_loot_no_candidate";

    public string DecisionSignature => string.Join("|",
        Classification,
        Observed ? "observed" : "not_observed",
        CandidateCorpseId,
        Gate,
        RelationshipEligible ? "relationship_eligible" : "relationship_ineligible",
        HostileVerified ? "hostile" : "not_hostile_confirmed",
        DeadOperatorCorpse ? "dead_operator" : "non_operator",
        RelationshipKind,
        HostilitySource,
        PathComplete ? "path_complete" : "path_incomplete",
        $"direct={DirectDistanceMeters:0.0}",
        $"path={PathDistanceMeters:0.0}",
        $"compatibilityBonus={EquipmentCompatibilityBonus:0.0}",
        $"score={UtilityScore:0.0}",
        $"manifestRevision={ManifestRevision}",
        $"interestRevision={InterestRevision}",
        $"lootNeed={LootNeedSignature}",
        $"evaluatedAt={EvaluatedAtUtc:O}",
        $"evalMs={EvaluationDurationMilliseconds:0.000}",
        Inventory.DecisionSignature,
        Plan.DecisionSignature,
        Counts.DecisionSignature,
        OperatorNeed.DecisionSignature,
        Permissions.DecisionSignature,
        ExecutionEnabled ? "execution_enabled" : "execution_disabled");
}
#endif
