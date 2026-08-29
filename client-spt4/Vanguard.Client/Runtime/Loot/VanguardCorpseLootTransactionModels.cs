#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EFT.InventoryLogic;
using Vanguard.Client.Runtime.Audit;

// Responsibility: Defines data/state contracts used by the loot runtime, centered on Corpse Loot Transaction Models.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime.Loot;

internal sealed class VanguardCorpseLootSessionStart
{
    public string ClaimId { get; init; } = "none";
    public string LeaseId { get; init; } = "none";
    public string WindowId { get; init; } = "none";
    public string OwnerProfileId { get; init; } = "none";
    public string OperatorId { get; init; } = "none";
    public string BotProfileId { get; init; } = "none";
    public string CorpseId { get; init; } = "none";
    public long ManifestRevision { get; init; }
    public long InterestRevision { get; init; }
    public string NeedSignature { get; init; } = "none";
    public DateTimeOffset ApproachStartedAtUtc { get; init; }
    public DateTimeOffset SchedulerMaxUntilUtc { get; init; }
    public string ApproachSummary { get; init; } = "none";
}

/// <summary>
/// Ephemeral per-item native operation represented through EFT's common IRaiseEvents contract while retaining the
/// concrete runtime identity of move/swap operations. It exists only while the post-arrival session owns the exact
/// assignment-bound item claim and is never cached, serialized by Vanguard or reused after one submit.
/// </summary>
internal sealed class VanguardCorpseLootPreparedTransaction
{
    public InventoryController Inventory { get; init; } = null!;
    public Item Item { get; init; } = null!;
    public ItemAddress SourceAddress { get; init; } = null!;
    public IRaiseEvents Operation { get; init; } = null!;
    public VanguardLootItemClaim ItemClaim { get; init; } = null!;
    public Item? DisplacedItem { get; init; }
    public ItemAddress? DisplacedSourceAddress { get; init; }
    public EquipmentSlot? LongWeaponDestinationSlot { get; init; }
    public VanguardCorpseLootTransactionPreflightResult Preflight { get; init; } = null!;
}

internal sealed class VanguardCorpseLootTransactionPreflightResult
{
    public static VanguardCorpseLootTransactionPreflightResult Rejected(string reason)
        => new() { Ready = false, Reason = reason };

    public bool Ready { get; init; }
    public string Reason { get; init; } = "none";
    public string ItemId { get; init; } = "none";
    public string TemplateId { get; init; } = "none";
    public string Category { get; init; } = "none";
    public string SelectionReason { get; init; } = "none";
    public string SourcePath { get; init; } = "none";
    public string SourceAddressFingerprint { get; init; } = "none";
    public string DestinationFingerprint { get; init; } = "none";
    public string OperationType { get; init; } = "none";
    public string ItemClaimId { get; init; } = "none";
    public long ManifestRevision { get; init; }
    public long InterestRevision { get; init; }
    public string AssignmentTier { get; init; } = "none";
    public float AssignmentScore { get; init; }
    public bool SecondarySwap { get; init; }
    public string DisplacedItemId { get; init; } = "none";
    public bool CanExecute { get; init; }
    public bool ItemsDestroyRequired { get; init; }
    public bool MutationAttempted { get; init; }
    public bool NetworkTransactionSubmitted { get; init; }
    public int Quantity { get; init; } = 1;
    public int FreshUsefulItemCount { get; init; }
    public int FreshFeasibleItemCount { get; init; }
    public string NeedSignature { get; init; } = "none";

    public string Summary => $"ready={Bool(Ready)}; reason={Safe(Reason)}; item={Safe(ItemId)}; template={Safe(TemplateId)}; category={Safe(Category)}; selection={Safe(SelectionReason)}; sourcePath={Safe(SourcePath)}; sourceAddress={Safe(SourceAddressFingerprint)}; destination={Safe(DestinationFingerprint)}; operation={Safe(OperationType)}; itemClaim={Safe(ItemClaimId)}; manifestRevision={ManifestRevision}; interestRevision={InterestRevision}; assignmentTier={Safe(AssignmentTier)}; assignmentScore={AssignmentScore:0.0}; secondarySwap={Bool(SecondarySwap)}; displacedItem={Safe(DisplacedItemId)}; quantity={Quantity}; canExecute={Bool(CanExecute)}; destroyRequired={Bool(ItemsDestroyRequired)}; useful={FreshUsefulItemCount}; feasible={FreshFeasibleItemCount}; need={Safe(NeedSignature)}; mutationAttempted={Bool(MutationAttempted)}; networkTransactionSubmitted={Bool(NetworkTransactionSubmitted)}";

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

internal sealed class VanguardCorpseLootSessionLimits
{
    public static VanguardCorpseLootSessionLimits CaptureRuntime(string? ownerProfileId = null)
    {
        var settings = VanguardRuntimeSettingsAuthorityResolver.ResolvePlayerScoped(ownerProfileId, "loot_session_limits_fallback");
        return new VanguardCorpseLootSessionLimits
        {
            MaximumTransactions = settings.LootMaximumTransactionsPerCorpse,
            MaximumSessionSeconds = settings.LootMaximumSessionSeconds,
            MaximumMedicalItems = settings.LootMaximumMedicalItemsPerSession,
            MaximumMagazines = settings.LootMaximumMagazinesPerSession,
            MaximumLooseAmmunitionRounds = settings.LootMaximumLooseAmmunitionRoundsPerSession,
            MaximumWeapons = settings.LootMaximumWeaponsPerSession
        };
    }

    public int MaximumTransactions { get; init; } = 8;
    public float MaximumSessionSeconds { get; init; } = 10f;
    public int MaximumMedicalItems { get; init; } = 4;
    public int MaximumMagazines { get; init; } = 4;
    public int MaximumLooseAmmunitionRounds { get; init; } = 180;
    public int MaximumWeapons { get; init; } = 1;

    public string Summary => $"transactions={MaximumTransactions}; seconds={MaximumSessionSeconds:0.0}; medical={MaximumMedicalItems}; magazines={MaximumMagazines}; looseAmmoRounds={MaximumLooseAmmunitionRounds}; weapons={MaximumWeapons}";
}

internal sealed class VanguardCorpseLootSessionProgress
{
    private readonly HashSet<string> _committedItemIds = new(StringComparer.OrdinalIgnoreCase);

    public int CommittedTransactions { get; private set; }
    public int MedicalItems { get; private set; }
    public int Magazines { get; private set; }
    public int LooseAmmunitionRounds { get; private set; }
    public int Weapons { get; private set; }
    public int Grenades { get; private set; }

    public bool CanAccept(VanguardCorpseLootItemPlanEntry entry, VanguardCorpseLootSessionLimits limits, out string reason)
    {
        reason = "none";
        if (entry == null || string.IsNullOrWhiteSpace(entry.ItemId))
        {
            reason = "entry_missing";
            return false;
        }

        if (_committedItemIds.Contains(entry.ItemId))
        {
            reason = "item_already_committed";
            return false;
        }

        if (CommittedTransactions >= limits.MaximumTransactions)
        {
            reason = "transaction_budget_exhausted";
            return false;
        }

        switch (entry.Category)
        {
            case "medical":
                if (MedicalItems >= limits.MaximumMedicalItems)
                {
                    reason = "medical_budget_exhausted";
                    return false;
                }
                break;
            case "magazine":
                if (Magazines >= limits.MaximumMagazines)
                {
                    reason = "magazine_budget_exhausted";
                    return false;
                }
                break;
            case "loose_ammunition":
                if (LooseAmmunitionRounds + Math.Max(1, entry.Quantity) > limits.MaximumLooseAmmunitionRounds)
                {
                    reason = "loose_ammunition_budget_exhausted";
                    return false;
                }
                break;
            case "long_weapon":
            case "holster_weapon":
                if (Weapons >= limits.MaximumWeapons)
                {
                    reason = "weapon_budget_exhausted";
                    return false;
                }
                break;
        }

        return true;
    }

    public void Record(VanguardCorpseLootTransactionPreflightResult preflight)
    {
        CommittedTransactions++;
        _committedItemIds.Add(preflight.ItemId);
        switch (preflight.Category)
        {
            case "medical": MedicalItems++; break;
            case "magazine": Magazines++; break;
            case "loose_ammunition": LooseAmmunitionRounds += Math.Max(1, preflight.Quantity); break;
            case "long_weapon":
            case "holster_weapon": Weapons++; break;
            case "grenade": Grenades++; break;
        }
    }

    public string Summary => $"transactions={CommittedTransactions}; medical={MedicalItems}; magazines={Magazines}; looseAmmoRounds={LooseAmmunitionRounds}; weapons={Weapons}; grenades={Grenades}";
}

internal sealed class VanguardCorpseLootPostCommitReadBackResult
{
    public bool Success { get; init; }
    public string Reason { get; init; } = "none";
    public bool ItemInOperatorInventory { get; init; }
    public bool WeaponManagerRecognized { get; init; }
    public bool EftMedicalRecognized { get; init; }
    public bool VanguardMedicalRecognized { get; init; }
    public bool MedicalTemplateKnownToVanguard { get; init; }
    public bool EftNativeMedicalFallbackUsed { get; init; }
    public string MedicalReadBackMode { get; init; } = "none";
    public bool MagazineCompatible { get; init; }
    public bool LooseAmmunitionCompatible { get; init; }
    public bool SecondarySwapDisplacedItemObserved { get; init; }
    public bool SecondarySwapSourceRestored { get; init; }

    public string Summary => $"success={Bool(Success)}; reason={Safe(Reason)}; itemInInventory={Bool(ItemInOperatorInventory)}; weaponManagerRecognized={Bool(WeaponManagerRecognized)}; eftMedicalRecognized={Bool(EftMedicalRecognized)}; vanguardMedicalRecognized={Bool(VanguardMedicalRecognized)}; medicalTemplateKnownToVanguard={Bool(MedicalTemplateKnownToVanguard)}; eftNativeMedicalFallbackUsed={Bool(EftNativeMedicalFallbackUsed)}; medicalReadBackMode={Safe(MedicalReadBackMode)}; magazineCompatible={Bool(MagazineCompatible)}; looseAmmunitionCompatible={Bool(LooseAmmunitionCompatible)}; secondarySwapDisplacedObserved={Bool(SecondarySwapDisplacedItemObserved)}; secondarySwapSourceRestored={Bool(SecondarySwapSourceRestored)}";

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

internal sealed class VanguardCorpseLootTransactionOutcome
{
    public string State { get; init; } = "none";
    public string Reason { get; init; } = "none";
    public bool SubmitAttempted { get; init; }
    public bool SubmitCallReturned { get; init; }
    public bool NetworkSubmissionUncertain { get; init; }
    public bool CallbackReceived { get; init; }
    public bool CallbackSucceeded { get; init; }
    public bool OperatorInventoryObserved { get; init; }
    public bool CorpseInventoryObserved { get; init; }
    public bool ItemInOperatorInventory { get; init; }
    public bool ItemStillInCorpseInventory { get; init; }
    public bool MutationConfirmed { get; init; }
    public bool ResultUncertain { get; init; }
    public bool NetworkTransactionSubmitted { get; init; }

    public string Summary => $"transactionState={Safe(State)}; transactionReason={Safe(Reason)}; submitAttempted={Bool(SubmitAttempted)}; submitCallReturned={Bool(SubmitCallReturned)}; networkSubmissionUncertain={Bool(NetworkSubmissionUncertain)}; callbackReceived={Bool(CallbackReceived)}; callbackSucceeded={Bool(CallbackSucceeded)}; operatorInventoryObserved={Bool(OperatorInventoryObserved)}; corpseInventoryObserved={Bool(CorpseInventoryObserved)}; itemInOperatorInventory={Bool(ItemInOperatorInventory)}; itemStillInCorpseInventory={Bool(ItemStillInCorpseInventory)}; mutationConfirmed={Bool(MutationConfirmed)}; resultUncertain={Bool(ResultUncertain)}; networkTransactionSubmitted={Bool(NetworkTransactionSubmitted)}";

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
