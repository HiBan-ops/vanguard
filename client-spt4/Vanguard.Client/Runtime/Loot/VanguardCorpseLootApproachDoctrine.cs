#if SPT_CLIENT
using System;
using Vanguard.Client.Options;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Encodes the deterministic rules for Corpse Loot Approach Doctrine within the loot runtime.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Runtime.Loot;

/// <summary>
/// Extends the validated atomic corpse approach into one bounded sequential corpse session. Every
/// item remains an independent EFT transaction; combat and medical are revalidated before each
/// submit, and squad terminal memory is still committed before final claim release.
/// </summary>
internal static class VanguardCorpseLootApproachDoctrine
{
    public const string StatusTag = "VANGUARD_CORPSE_LOOT_CLAIM_AND_APPROACH_STATUS";
    public const string PreflightStatusTag = "VANGUARD_TYPED_CORPSE_TRANSACTION_PREFLIGHT_AND_INTERRUPTION_PROOF_STATUS";
    public const string TransactionStatusTag = "VANGUARD_ATOMIC_SINGLE_ITEM_CORPSE_LOOT_COMMIT_STATUS";
    public const string OperationalLootStatusTag = "VANGUARD_OPERATIONAL_CORPSE_LOOT_FOUNDATION_STATUS";
    public const string RequestKind = "CorpseLootApproach";
    public const bool ApproachExecutionEnabled = true;
    public const bool ClaimAuthorityEnabled = true;
    public const bool InventoryTransactionsEnabled = true;
    public const bool CorpseInteractionEnabled = false;
    public const bool EquipmentMutationEnabled = true;
    public const bool PersistenceEnabled = false;
    public const bool TypedTransactionPreflightEnabled = true;
    public const bool ActiveApproachInterruptionProofEnabled = true;
    public const bool AtomicSingleItemTransactionEnabled = true;
    public const bool SequentialMultiItemSessionEnabled = false; // One utility-claimed item per physical corpse visit.
    public const bool OperatorCorpseTransactionsEnabled = false;

    public const float TickSeconds = 0.25f;
    public const float MinimumWindowSeconds = 1.00f;
    public const float MaximumWindowSeconds = 45.0f;
    public const float SchedulerMaximumWindowSeconds = 67.0f;
    public const float PreflightSettleSeconds = 0.20f;
    public const float SequentialRescanDelaySeconds = 0.20f; // Compatibility timing constant; inactive while sequential sessions remain disabled.
    public const float TransactionPreparationMaximumSeconds = 1.50f;
    public const float TransactionMaximumSeconds = 4.00f;
    public const float TransactionReconciliationGraceSeconds = 0.75f;
    public const float NoProgressSeconds = 4.50f;
    public const float ClaimLifetimeSeconds = 49.0f;
    public const float SuccessCooldownSeconds = 300.0f;
    public const float FailureCooldownSeconds = 12.0f;
    public const float ThreatInterruptCooldownSeconds = 4.0f;

    public const float MaximumStartOwnerDistanceMeters = 80.0f;
    public const float MaximumActiveOwnerDistanceMeters = 80.0f;
    public const float MaximumOwnerAnchorDistanceMeters = 80.0f;
    public static float MaximumDirectCorpseDistanceMeters => VanguardMovementAuthorityDoctrine.OpportunisticLootMaxDistanceMeters;
    public static float MaximumDirectCorpseDistanceMetersForOwner(string? ownerProfileId)
        => VanguardRuntimeSettingsAuthorityResolver.ResolvePlayerScoped(ownerProfileId, "loot_direct_distance_fallback").MovementOpportunisticLootMaxDistanceMeters;
    public const float MaximumPathDistanceMeters = 42.0f;
    public const float MaximumAddedDetourMeters = 7.0f;
    public const float MaximumPathRatio = 1.65f;
    public const float CorpseInteractionDistanceMeters = 2.20f;
    public const float ApproachAnchorRadiusMeters = 1.10f;
    public const float ApproachAnchorOffsetMeters = 1.45f;
    public const float ProgressGainMeters = 0.18f;
    public const float PhysicalRestartAfterSeconds = 1.25f;
    public const float PhysicalFailAfterRestartSeconds = 4.00f;

    public static int MaximumCommittedItemsPerCorpseSession => VanguardOperatorRuntimeAuditOptions.GetLootMaximumTransactionsPerCorpse();
    public static float MaximumOperationalSessionSeconds => VanguardOperatorRuntimeAuditOptions.GetLootMaximumSessionSeconds();
    public static TimeSpan TickInterval => TimeSpan.FromSeconds(TickSeconds);
    public static TimeSpan PreflightSettleDuration => TimeSpan.FromSeconds(PreflightSettleSeconds);
    public static TimeSpan SequentialRescanDelay => TimeSpan.FromSeconds(SequentialRescanDelaySeconds);
    public static TimeSpan TransactionPreparationMaximumDuration => TimeSpan.FromSeconds(TransactionPreparationMaximumSeconds);
    public static TimeSpan TransactionMaximumDuration => TimeSpan.FromSeconds(TransactionMaximumSeconds);
    public static TimeSpan TransactionReconciliationGrace => TimeSpan.FromSeconds(TransactionReconciliationGraceSeconds);
}
#endif
