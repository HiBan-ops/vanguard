using Vanguard.Server.Operators.Models;

// Responsibility: Defines response/projection payloads produced by the Operator API response contracts.
// Flow: Owning services project canonical state into these shapes before serialization to the client or another subsystem.
// Authority boundary: Presentation/transport contract only; canonical authority remains with the source service or persistence store.
// Invariant: Projection code must not mutate the state it describes and serialized fields remain compatibility-conscious.
namespace Vanguard.Server.Operators.Responses;

public sealed record VanguardOperatorStateResponse(
    string RequestedProfileId,
    string StorageProfileId,
    VanguardOperatorDeploymentLimits Limits,
    IReadOnlyList<VanguardOperatorProfile> Operators,
    IReadOnlyList<VanguardActiveServiceRecord> ActiveService,
    IReadOnlyList<VanguardOperatorContractOffer> Contracts,
    IReadOnlyList<VanguardOperatorContactRecord> Contacts,
    IReadOnlyList<VanguardOperatorMedicalRecord> MedicalRecords,
    IReadOnlyList<VanguardOperatorServiceProjection> ServiceProjections,
    IReadOnlyList<VanguardOperatorMedicalProjection> MedicalProjections,
    IReadOnlyList<VanguardOperatorRaidProjection> RaidProjections,
    VanguardCareerProjectionReadModel CareerProjection,
    VanguardCanonicalRaidHistoryReadModel CanonicalRaidHistory,
    VanguardOperatorBillingSnapshot Billing,
    VanguardOperatorStateMetadata Metadata);
