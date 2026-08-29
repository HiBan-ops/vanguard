#if SPT_CLIENT
using System.Globalization;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Builds Movement Lease Plan Builder data for the movement/cohesion runtime from already-available inputs.
// Flow: Normalized inputs are combined deterministically into a result consumed by the next policy, scheduler, UI, or transport stage.
// Authority boundary: Composition only; underlying gameplay/persistence truth remains owned by the source inputs.
// Invariant: Building a result must not perform hidden world mutation or acquire a competing authority.
namespace Vanguard.Client.Runtime.Movement;

internal static class VanguardMovementLeasePlanBuilder
{
    public static VanguardMovementLeasePlanSnapshot Build(OperatorDecisionSnapshot snapshot, VanguardMovementContractSnapshot contract)
    {
        if (!contract.MovementLeaseEligible)
        {
            return NoLease(contract, "contract_not_movement_eligible");
        }

        if (!snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement)
        {
            return NoLease(contract, "owner_anchor_unreliable_no_lease");
        }

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot))
        {
            return NoLease(contract, "true_direct_threat_no_lease");
        }

        if (VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(snapshot))
        {
            return NoLease(contract, "medical_authority_no_lease");
        }

        string anchorKind = AnchorKindFor(contract.RequestKind);
        float anchorRadius = AnchorRadiusFor(contract.RequestKind);
        bool applyEnabled = IsActiveApplyContract(snapshot, contract);
        bool closeCohesion = contract.RequestKind == VanguardMovementContractPolicy.CloseCohesionMicroAdjust;
        bool travelCohesion = contract.RequestKind == VanguardMovementContractPolicy.TravelCohesionFollowThrough;
        bool tacticalVolumeJoin = contract.RequestKind == VanguardMovementContractPolicy.TacticalVolumeJoin;
        float minDuration = contract.RequestKind == VanguardMovementContractPolicy.TacticalRepositionToUsefulSector
            ? 2.25f
            : closeCohesion
                ? 1.75f
                : travelCohesion
                    ? 2.00f
                    : tacticalVolumeJoin
                        ? 3.00f
                        : VanguardMovementAuthorityDoctrine.MovementLeaseMinDurationSeconds;
        float maxDuration = contract.RequestKind == VanguardMovementContractPolicy.TacticalRepositionToUsefulSector
            ? VanguardMovementAuthorityDoctrine.TacticalRepositionMaxDurationSeconds
            : closeCohesion
                ? VanguardMovementAuthorityDoctrine.CloseCohesionMaxDurationSeconds
                : travelCohesion
                    ? VanguardMovementAuthorityDoctrine.TravelCohesionMaxDurationSeconds
                    : tacticalVolumeJoin
                        ? VanguardMovementAuthorityDoctrine.TacticalVolumeJoinMaxDurationSeconds
                        : VanguardMovementAuthorityDoctrine.MovementLeaseMaxDurationSeconds;
        float noProgress = contract.RequestKind == VanguardMovementContractPolicy.TacticalRepositionToUsefulSector
            ? VanguardMovementAuthorityDoctrine.TacticalRepositionNoProgressSeconds
            : closeCohesion
                ? VanguardMovementAuthorityDoctrine.CloseCohesionNoProgressSeconds
                : travelCohesion
                    ? VanguardMovementAuthorityDoctrine.TravelCohesionNoProgressSeconds
                    : tacticalVolumeJoin
                        ? VanguardMovementAuthorityDoctrine.TacticalVolumeJoinNoProgressSeconds
                        : VanguardMovementAuthorityDoctrine.MovementLeaseNoProgressSeconds;
        string completionRule = contract.RequestKind == VanguardMovementContractPolicy.TacticalRepositionToUsefulSector
            ? "anchor_reached_or_useful_sector_stable"
            : closeCohesion
                ? "anchor_reached_or_owner_distance_recovered"
                : travelCohesion
                    ? "anchor_reached_or_reentered_travel_band"
                    : tacticalVolumeJoin
                        ? "same_tactical_volume_or_anchor_reached"
                        : "inside_action_rally_accept_or_clear";

        return new VanguardMovementLeasePlanSnapshot
        {
            LeaseKey = "lease_plan_" + contract.RequestKind,
            Backend = contract.Backend,
            AnchorKind = anchorKind,
            AnchorRadiusMeters = anchorRadius,
            Eligible = true,
            ApplyEnabled = applyEnabled,
            SuppressLootingBots = contract.WouldSuppressLootingBots,
            SuppressOrbit = contract.WouldSuppressOrbit,
            SuppressSainSearch = contract.WouldSuppressSainSearch,
            MinDurationSeconds = minDuration,
            MaxDurationSeconds = maxDuration,
            NoProgressTimeoutSeconds = noProgress,
            ReapplyPolicy = "apply_once_no_periodic_reapply",
            CompletionRule = completionRule,
            InterruptionRule = "true_threat_or_medical_or_owner_unreliable",
            Reason = "contract=" + contract.ContractKey + ";request=" + contract.RequestKind + ";backend=" + contract.Backend + ";bubbleDist=" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.00", CultureInfo.InvariantCulture),
            ReadOnly = true
        };
    }

    private static VanguardMovementLeasePlanSnapshot NoLease(VanguardMovementContractSnapshot contract, string reason)
    {
        return new VanguardMovementLeasePlanSnapshot
        {
            LeaseKey = "none",
            Backend = contract.Backend,
            AnchorKind = "none",
            AnchorRadiusMeters = 0f,
            Eligible = false,
            ApplyEnabled = false,
            SuppressLootingBots = contract.WouldSuppressLootingBots,
            SuppressOrbit = contract.WouldSuppressOrbit,
            SuppressSainSearch = contract.WouldSuppressSainSearch,
            MinDurationSeconds = 0f,
            MaxDurationSeconds = 0f,
            NoProgressTimeoutSeconds = 0f,
            ReapplyPolicy = "no_lease",
            CompletionRule = "none",
            InterruptionRule = "none",
            Reason = reason,
            ReadOnly = true
        };
    }

    private static bool IsActiveApplyContract(OperatorDecisionSnapshot snapshot, VanguardMovementContractSnapshot contract)
    {
        if (contract.RequestKind == VanguardMovementContractPolicy.SuppressExternalAndReturn
            || contract.RequestKind == VanguardMovementContractPolicy.ReturnToBubbleHard)
        {
            return VanguardMovementAuthorityDoctrine.ActiveBackendApplyEnabled;
        }

        if (contract.RequestKind == VanguardMovementContractPolicy.TacticalRepositionToUsefulSector)
        {
            return VanguardMovementAuthorityDoctrine.TacticalRepositionActiveEnabled;
        }

        if (contract.RequestKind == VanguardMovementContractPolicy.CloseCohesionMicroAdjust
            || contract.RequestKind == VanguardMovementContractPolicy.TravelCohesionFollowThrough
            || contract.RequestKind == VanguardMovementContractPolicy.TacticalVolumeJoin)
        {
            return VanguardMovementAuthorityDoctrine.TacticalRepositionActiveEnabled && VanguardMovementAuthorityDoctrine.ActiveBackendApplyEnabled;
        }

        if (contract.RequestKind == VanguardMovementContractPolicy.BreakSainSearchReturnBubble
            && VanguardMovementAuthorityDoctrine.ActiveSainBoundaryReturnEnabled
            && VanguardMovementAuthorityDoctrine.IsSainBoundaryReturnEligible(snapshot, out _))
        {
            return true;
        }

        return false;
    }

    private static string AnchorKindFor(string requestKind)
    {
        if (requestKind == VanguardMovementContractPolicy.BreakSainSearchReturnBubble)
        {
            return "ActionRallyAnchorVolume";
        }

        if (requestKind == VanguardMovementContractPolicy.SuppressExternalAndReturn)
        {
            return "ActionRallyAnchorVolume";
        }

        if (requestKind == VanguardMovementContractPolicy.ReturnToBubbleHard)
        {
            return "ActionRallyAnchorVolume";
        }

        if (requestKind == VanguardMovementContractPolicy.TacticalRepositionToUsefulSector)
        {
            return "EnvironmentAwareSectorAnchor";
        }

        if (requestKind == VanguardMovementContractPolicy.CloseCohesionMicroAdjust)
        {
            return "RadialCloseCohesionAnchor";
        }

        if (requestKind == VanguardMovementContractPolicy.TravelCohesionFollowThrough)
        {
            return "TravelCohesionAnchor";
        }

        if (requestKind == VanguardMovementContractPolicy.TacticalVolumeJoin)
        {
            return "TacticalVolumeJoinAnchor";
        }

        return "none";
    }

    private static float AnchorRadiusFor(string requestKind)
    {
        if (requestKind == VanguardMovementContractPolicy.ReturnToBubbleHard
            || requestKind == VanguardMovementContractPolicy.BreakSainSearchReturnBubble
            || requestKind == VanguardMovementContractPolicy.SuppressExternalAndReturn)
        {
            return VanguardMovementAuthorityDoctrine.HardReturnAnchorRadiusMeters;
        }

        if (requestKind == VanguardMovementContractPolicy.TacticalRepositionToUsefulSector)
        {
            return VanguardMovementAuthorityDoctrine.TacticalAnchorRadiusMeters;
        }

        if (requestKind == VanguardMovementContractPolicy.CloseCohesionMicroAdjust)
        {
            return VanguardMovementAuthorityDoctrine.CloseCohesionAnchorRadiusMeters;
        }

        if (requestKind == VanguardMovementContractPolicy.TravelCohesionFollowThrough)
        {
            return VanguardMovementAuthorityDoctrine.TravelCohesionAnchorRadiusMeters;
        }

        if (requestKind == VanguardMovementContractPolicy.TacticalVolumeJoin)
        {
            return VanguardMovementAuthorityDoctrine.TacticalVolumeJoinAnchorRadiusMeters;
        }

        return 0f;
    }
}
#endif
