#if SPT_CLIENT
using Vanguard.Client.Runtime.Decision;

// Responsibility: Provides Movement Broker Dry Run support for the movement/cohesion runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Movement;

internal static class VanguardMovementBrokerDryRun
{
    public static VanguardMovementBrokerPlanSnapshot Build(OperatorDecisionSnapshot snapshot, bool sainEnvelopeViolation, string sainViolationReason, bool lootWouldSuppress, bool orbitWouldSuppress, bool idleStallSuspect)
    {
        var contract = VanguardMovementContractPolicy.Build(snapshot, sainEnvelopeViolation, sainViolationReason, lootWouldSuppress, orbitWouldSuppress, idleStallSuspect);
        var leasePlan = VanguardMovementLeasePlanBuilder.Build(snapshot, contract);

        return new VanguardMovementBrokerPlanSnapshot
        {
            PlanKey = PlanKeyFor(contract),
            Backend = leasePlan.Eligible ? leasePlan.Backend : contract.Backend,
            WouldOpenLease = leasePlan.Eligible,
            WouldSuppressLootingBots = contract.WouldSuppressLootingBots,
            WouldSuppressOrbit = contract.WouldSuppressOrbit,
            WouldSuppressSainSearch = contract.WouldSuppressSainSearch,
            AnchorKind = leasePlan.Eligible ? leasePlan.AnchorKind : "none",
            RequestKind = contract.RequestKind,
            Reason = contract.Reason,
            Contract = contract,
            LeasePlan = leasePlan,
            ReadOnly = true
        };
    }

    private static string PlanKeyFor(VanguardMovementContractSnapshot contract)
    {
        switch (contract.RequestKind)
        {
            case VanguardMovementContractPolicy.BlockOwnerUnreliable:
                return contract.ContractKey.Contains("unknown") ? "broker_blocked_owner_unknown" : "broker_blocked_owner_cache_readonly";
            case VanguardMovementContractPolicy.YieldSainDirectThreat:
                return "broker_yield_sain_direct_threat";
            case VanguardMovementContractPolicy.YieldVanguardMedical:
                return "broker_yield_vanguard_medical";
            case VanguardMovementContractPolicy.BreakSainSearchHoldSector:
                return "broker_would_break_sain_search_hold_sector";
            case VanguardMovementContractPolicy.BreakSainSearchReturnBubble:
                return "broker_would_break_sain_search_return_bubble";
            case VanguardMovementContractPolicy.ReturnToBubbleHard:
                return "broker_would_return_hard_outside_bubble";
            case VanguardMovementContractPolicy.MonitorSoftBubbleBreach:
                return "broker_monitor_soft_outside_bubble";
            case VanguardMovementContractPolicy.SuppressExternalAndReturn:
                return "broker_would_suppress_external_return_bubble";
            case VanguardMovementContractPolicy.CloseCohesionMicroAdjust:
                return "broker_would_close_cohesion_micro_adjust";
            case VanguardMovementContractPolicy.SuppressExternalAuthorityOnly:
                return "broker_would_suppress_external_not_relocate";
            case VanguardMovementContractPolicy.ObserveIdleStall:
                return "broker_observe_idle_stall";
            default:
                return "broker_observe_no_intervention";
        }
    }
}
#endif
