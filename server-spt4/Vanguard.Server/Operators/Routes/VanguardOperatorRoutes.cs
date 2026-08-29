using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;
using Vanguard.Server.Operators.Requests;
using Vanguard.Server.Operators.Inventory.Requests;
using Vanguard.Server.Operators.Raid.Requests;
using Vanguard.Server.Operators.Raid.Persistence.Models;
using Vanguard.Server.Operators.Services;
using Vanguard.Server.Operators.Audit.Requests;
using Vanguard.Server.Operators.TacticalAuthoring.Requests;
using Vanguard.Server.Operators.LootInterests.Requests;

// Responsibility: Registers the HTTP route surface for Operator Routes in the Operator HTTP routes.
// Flow: SPT route callbacks deserialize input, delegate validation/business work to domain services, and serialize the resulting response.
// Authority boundary: Routing owns transport only; domain services and the Operator store remain authoritative for business and persistence state.
// Invariant: Routes do not duplicate domain logic and profile/session identity is forwarded explicitly to the owning service.
namespace Vanguard.Server.Operators.Routes;

[Injectable]
public sealed class VanguardOperatorRoutes(JsonUtil jsonUtil, VanguardOperatorRouteCallbacks callbacks)
    : StaticRouter(jsonUtil,
    [
        new RouteAction<EmptyRequestData>(
            "/vanguard/operators/state",
            async (url, info, sessionId, output) => await callbacks.GetStateAsync(url, info, sessionId)
        ),
        new RouteAction<EmptyRequestData>(
            "/vanguard/operators/limits",
            async (url, info, sessionId, output) => await callbacks.GetLimitsAsync(url, info, sessionId)
        ),
        new RouteAction<VanguardHireContractRequest>(
            "/vanguard/operators/contracts/hire",
            async (url, info, sessionId, output) => await callbacks.HireContractAsync(url, info, sessionId)
        ),
        new RouteAction<VanguardDismissOperatorRequest>(
            "/vanguard/operators/active-service/dismiss",
            async (url, info, sessionId, output) => await callbacks.DismissOperatorAsync(url, info, sessionId)
        ),
        new RouteAction<VanguardSetOperatorRaidSelectionRequest>(
            "/vanguard/operators/active-service/raid-selection",
            async (url, info, sessionId, output) => await callbacks.SetOperatorRaidSelectionAsync(url, info, sessionId)
        ),
        new RouteAction<VanguardSetOperatorLootTargetPolicyRequest>(
            "/vanguard/operators/loot-policy/set",
            async (url, info, sessionId, output) => await callbacks.SetOperatorLootTargetPolicyAsync(url, info, sessionId)
        ),
        new RouteAction<VanguardOperatorMedicalTreatmentRequest>(
            "/vanguard/operators/medical/treat",
            async (url, info, sessionId, output) => await callbacks.TreatOperatorMedicalAsync(url, info, sessionId)
        ),
        new RouteAction<VanguardSignBillingRequest>(
            "/vanguard/operators/billing/sign",
            async (url, info, sessionId, output) => await callbacks.SignBillingAsync(url, info, sessionId)
        ),
        new RouteAction<EmptyRequestData>(
            "/vanguard/operators/billing/reconcile",
            async (url, info, sessionId, output) => await callbacks.ReconcileBillingAsync(url, info, sessionId)
        ),

        new RouteAction<VanguardOperatorInventoryModeRequest>(
            "/vanguard/operators/inventory-mode/enter",
            async (url, info, sessionId, output) => await callbacks.EnterInventoryModeAsync(url, info, sessionId)
        ),
        new RouteAction<VanguardOperatorInventoryModeRequest>(
            "/vanguard/operators/inventory-mode/exit",
            async (url, info, sessionId, output) => await callbacks.ExitInventoryModeAsync(url, info, sessionId)
        ),
        new RouteAction<VanguardOperatorInventoryDirectCommitRequest>(
            "/vanguard/operators/inventory-mode/direct-commit",
            async (url, info, sessionId, output) => await callbacks.DirectCommitInventoryModeAsync(url, info, sessionId)
        ),
        new RouteAction<EmptyRequestData>(
            "/vanguard/operators/inventory-mode/status",
            async (url, info, sessionId, output) => await callbacks.GetInventoryModeStatusAsync(url, info, sessionId)
        ),
        new RouteAction<EmptyRequestData>(
            "/vanguard/operators/inventory-mode/profiles",
            async (url, info, sessionId, output) => await callbacks.GetInventoryModeProfilesAsync(url, info, sessionId)
        ),
        new RouteAction<EmptyRequestData>(
            "/vanguard/operators/inventory/summary",
            async (url, info, sessionId, output) => await callbacks.GetInventorySummaryAsync(url, info, sessionId)
        ),

        new RouteAction<EmptyRequestData>(
            "/vanguard/operators/raid/manifest",
            async (url, info, sessionId, output) => await callbacks.GetRaidManifestAsync(url, info, sessionId)
        ),
        new RouteAction<VanguardRaidManifestForProfilesRequest>(
            "/vanguard/operators/raid/manifest-for-profiles",
            async (url, info, sessionId, output) => await callbacks.GetRaidManifestForProfilesAsync(url, info, sessionId)
        ),
        new RouteAction<VanguardRaidOperatorPersistenceBatchRequest>(
            "/vanguard/operators/raid/persistence/commit",
            async (url, info, sessionId, output) => await callbacks.CommitRaidOperatorPersistenceAsync(url, info, sessionId)
        ),
        new RouteAction<VanguardGenerateOperatorBotRequest>(
            "/client/game/bot/vanguardoperatorgenerate",
            async (url, info, sessionId, output) => await callbacks.GenerateVanguardOperatorBotAsync(url, info, sessionId)
        ),


        new RouteAction<VanguardOperatorRuntimeAuditSettingsRequest>(
            "/vanguard/operators/runtime-audit/config/set",
            async (url, info, sessionId, output) => await callbacks.SetRuntimeAuditConfigAsync(url, info, sessionId)
        ),
        new RouteAction<VanguardOperatorRuntimeAuditSettingsGetRequest>(
            "/vanguard/operators/runtime-audit/config/get",
            async (url, info, sessionId, output) => await callbacks.GetRuntimeAuditConfigAsync(url, info, sessionId)
        ),
        new RouteAction<VanguardTacticalAuthoringLiveExchangeRequest>(
            "/vanguard/operators/tactical-authoring/live/exchange",
            async (url, info, sessionId, output) => await callbacks.ExchangeTacticalAuthoringLiveAsync(url, info, sessionId)
        ),
        new RouteAction<VanguardOwnerLootInterestSetRequest>(
            "/vanguard/operators/loot-interest/set",
            async (url, info, sessionId, output) => await callbacks.SetOwnerLootInterestAsync(url, info, sessionId)
        ),
        new RouteAction<VanguardOwnerLootInterestGetRequest>(
            "/vanguard/operators/loot-interest/get",
            async (url, info, sessionId, output) => await callbacks.GetOwnerLootInterestAsync(url, info, sessionId)
        ),
        new RouteAction<EmptyRequestData>(
            "/vanguard/operators/diagnostics/storage",
            async (url, info, sessionId, output) => await callbacks.GetStorageDiagnosticsAsync(url, info, sessionId)
        ),
    ])
{
}
