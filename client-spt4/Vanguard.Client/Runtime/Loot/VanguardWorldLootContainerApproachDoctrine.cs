#if SPT_CLIENT

// Responsibility: Encodes the deterministic rules for World Loot Container Approach Doctrine within the loot runtime.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Runtime.Loot;

internal static class VanguardWorldLootContainerApproachDoctrine
{
    public const string StatusTag = "VANGUARD_CONTAINER_CLAIM_APPROACH_OPEN_PROOF_STATUS";
    public const string TransactionStatusTag = "VANGUARD_CONTAINER_ITEM_TRANSACTION_ACTIVATION_STATUS";
    public const string RequestKind = "WorldContainerLootApproach";
    public const float InteractionDistanceMeters = 2.40f;
    public const float OpenProofTimeoutSeconds = 2.50f;
    public const float TerminalCooldownSeconds = 45.0f;
    public const int PlayerInterestMaximumCommitsPerVisit = 2;
    public const float PlayerInterestMicroSessionMaximumSeconds = 4.0f;
}
#endif
