using Vanguard.Server.Operators.Models;

// Responsibility: Defines data/state contracts used by the Operator persistence storage, centered on Operator Storage State.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Server.Operators.Storage;

public sealed record VanguardOperatorStorageState(
    IReadOnlyList<VanguardOperatorProfile> Operators,
    IReadOnlyList<VanguardActiveServiceRecord> ActiveService,
    IReadOnlyList<VanguardOperatorContractOffer> Contracts,
    IReadOnlyList<VanguardOperatorMedicalRecord> Medical,
    IReadOnlyList<VanguardOperatorContactRecord> Contacts,
    VanguardOperatorBillingLedger BillingLedger);
