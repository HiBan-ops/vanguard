// Responsibility: Registers the HTTP route surface for Api Routes in the client API transport.
// Flow: SPT route callbacks deserialize input, delegate validation/business work to domain services, and serialize the resulting response.
// Authority boundary: Routing owns transport only; domain services and the Operator store remain authoritative for business and persistence state.
// Invariant: Routes do not duplicate domain logic and profile/session identity is forwarded explicitly to the owning service.
namespace Vanguard.Client.Api;

internal static class VanguardApiRoutes
{
    public const string State = "/vanguard/operators/state";
    public const string Limits = "/vanguard/operators/limits";
    public const string HireContract = "/vanguard/operators/contracts/hire";
    public const string DismissOperator = "/vanguard/operators/active-service/dismiss";
    public const string SetRaidSelection = "/vanguard/operators/active-service/raid-selection";
    public const string SetLootTargetPolicy = "/vanguard/operators/loot-policy/set";
    public const string TreatMedical = "/vanguard/operators/medical/treat";
    public const string SignBilling = "/vanguard/operators/billing/sign";
    public const string ReconcileBilling = "/vanguard/operators/billing/reconcile";
    public const string EnterInventoryMode = "/vanguard/operators/inventory-mode/enter";
    public const string ExitInventoryMode = "/vanguard/operators/inventory-mode/exit";
    public const string InventoryModeStatus = "/vanguard/operators/inventory-mode/status";
    public const string InventoryModeProfiles = "/vanguard/operators/inventory-mode/profiles";
    public const string DirectCommitInventoryMode = "/vanguard/operators/inventory-mode/direct-commit";
    public const string InventorySummary = "/vanguard/operators/inventory/summary";
    public const string RaidManifest = "/vanguard/operators/raid/manifest";
    public const string RaidManifestForProfiles = "/vanguard/operators/raid/manifest-for-profiles";
    public const string RaidPersistenceCommit = "/vanguard/operators/raid/persistence/commit";
    public const string VanguardOperatorGenerate = "/client/game/bot/vanguardoperatorgenerate";
    // Runtime-audit settings use the same explicit config/set + config/get POST contract as the rest of the authority synchronization path.
    // The ambiguous /settings and /settings/set compatibility routes were removed from
    // the server baseline to avoid future accidental reuse.
    public const string RuntimeAuditConfigSet = "/vanguard/operators/runtime-audit/config/set";
    public const string RuntimeAuditConfigGet = "/vanguard/operators/runtime-audit/config/get";
    public const string TacticalAuthoringLiveExchange = "/vanguard/operators/tactical-authoring/live/exchange";
    public const string OwnerLootInterestSet = "/vanguard/operators/loot-interest/set";
    public const string OwnerLootInterestGet = "/vanguard/operators/loot-interest/get";
}
