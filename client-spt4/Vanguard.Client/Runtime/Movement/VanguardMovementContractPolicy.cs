#if SPT_CLIENT
using System;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Awareness;

// Responsibility: Converts runtime movement needs into bounded movement contracts while preserving combat, medical, authored-position and squad-cohesion precedence.
// Flow: It evaluates normalized decision evidence and active leases to select contract kind/priority/limits; executors later perform pathing and movement only after final authority checks.
// Authority boundary: Policy grants eligibility but does not move the bot or fabricate target/threat evidence; execution authority remains with the movement scheduler/executors.
// Invariant: Conflicting movement domains remain mutually coherent, safety-critical preemption wins, and every contract is raid-scoped and releasable.
namespace Vanguard.Client.Runtime.Movement;

internal static class VanguardMovementContractPolicy
{
    public const string HoldCurrentAuthority = "HoldCurrentAuthority";
    public const string BreakSainSearchHoldSector = "BreakSainSearchHoldSector";
    public const string BreakSainSearchReturnBubble = "BreakSainSearchReturnBubble";
    public const string ActionRallyHardReturn = "ActionRallyHardReturn";
    public const string ReturnToBubbleHard = ActionRallyHardReturn;
    public const string MonitorSoftBubbleBreach = "MonitorSoftBubbleBreach";
    public const string SuppressExternalAuthorityOnly = "SuppressExternalAuthorityOnly";
    public const string SuppressExternalAndReturn = "SuppressExternalAndReturn";
    public const string YieldSainDirectThreat = "YieldSainDirectThreat";
    public const string YieldVanguardMedical = "YieldVanguardMedical";
    public const string BlockOwnerUnreliable = "BlockOwnerUnreliable";
    public const string ObserveIdleStall = "ObserveIdleStall";
    public const string TacticalRepositionToUsefulSector = "TacticalRepositionToUsefulSector";
    public const string CloseCohesionMicroAdjust = "CloseCohesionMicroAdjust";
    public const string TravelCohesionFollowThrough = "TravelCohesionFollowThrough";
    public const string TacticalVolumeJoin = "TacticalVolumeJoin";
    public const string ClaimedCohesionSlot = "ClaimedCohesionSlot";
    public const string CorpseLootApproach = Vanguard.Client.Runtime.Loot.VanguardCorpseLootApproachDoctrine.RequestKind;
    public const string WorldContainerLootApproach = Vanguard.Client.Runtime.Loot.VanguardWorldLootContainerApproachDoctrine.RequestKind;

    // Build a movement contract in two passes. First derive plain boolean facts (owner reliability, direct
    // threat, medical block, cohesion pressure, external movement residue). Then evaluate precedence from
    // safety/authority holds down to tactical/travel corrections. Returning a contract never moves the bot;
    // the executor must recheck the live world before acting.
    public static VanguardMovementContractSnapshot Build(
        OperatorDecisionSnapshot snapshot,
        bool sainEnvelopeViolation,
        string sainViolationReason,
        bool lootWouldSuppress,
        bool orbitWouldSuppress,
        bool idleStallSuspect)
    {
        var cohesion = snapshot.SquadCohesion;
        bool ownerKnown = cohesion.OwnerKnown;
        bool ownerReliable = cohesion.OwnerReliableForActiveMovement;
        bool medicalBlocks = VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(snapshot);
        bool trueThreat = VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot);
        bool combatProductive = VanguardMovementAuthorityDoctrine.IsCombatProductive(snapshot, out var combatProductiveReason);
        bool forcedCombatCohesionCatchup = VanguardMovementAuthorityDoctrine.ShouldForceCatchupForStaleSain(snapshot, out var forcedCatchupReason);
        bool staleHoldAllowed = VanguardMovementAuthorityDoctrine.ShouldHoldSectorForStaleSain(snapshot, out var staleHoldReason);
        bool doctrineHardOutside = ownerKnown
            && cohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters
            && !combatProductive;
        bool hardOutside = ownerKnown && (cohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.HardCorrectionMeters || doctrineHardOutside);
        bool softOutside = ownerKnown && (cohesion.OperatorDistanceToOwner > VanguardMovementAuthorityDoctrine.TacticalBubbleMeters || cohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.TravelCohesionStartMeters);
        bool inBubble = ownerKnown && cohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.TacticalBubbleMeters;
        bool staleSearchInsideBubble = sainEnvelopeViolation
            && inBubble
            && string.Equals(sainViolationReason, "sain_search_stale_or_non_actionable_target", StringComparison.OrdinalIgnoreCase);
        bool generalStaleSainExit = VanguardMovementAuthorityDoctrine.IsSainCombatStaleNonActionable(snapshot, out var generalStaleReason);
        bool searchOutsideBubble = sainEnvelopeViolation && softOutside;
        bool suppressExternal = VanguardMovementAuthorityDoctrine.SuppressExternalDuringRecallEnabled;
        bool externalOutOfDoctrine = suppressExternal && (lootWouldSuppress || orbitWouldSuppress);
        bool contractLootSuppress = suppressExternal && lootWouldSuppress;
        bool contractOrbitSuppress = suppressExternal && orbitWouldSuppress;
        bool idleStall = idleStallSuspect;
        bool anyLootActive = snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true || snapshot.Looting.HasActiveLootable == true;
        bool criticalLootActive = snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true;
        bool orbitActive = snapshot.Orbit.Active;
        bool pathActive = snapshot.Movement.HasPath == true;
        bool orbitOpposesOwner = VanguardMovementAuthorityDoctrine.IsOrbitObjectiveOpposingOwner(snapshot, out var orbitOwnerAlignment);
        bool nonCriticalOrbitOrPath = VanguardMovementAuthorityDoctrine.HasNonCriticalOrbitOrPathResidue(snapshot);
        bool externalActive = anyLootActive || orbitActive || pathActive;
        bool closeCohesionDistance = inBubble
            && cohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.CloseCohesionStartMinMeters
            && cohesion.OperatorDistanceToOwner <= VanguardMovementAuthorityDoctrine.CloseCohesionStartMaxMeters;
        bool closeCohesionShapePressure = cohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.CloseCohesionForceStartMeters
            || cohesion.SectorDuplicate
            || cohesion.RearOverstacked
            || !cohesion.UsefulPosition
            || idleStall;
        bool closeCohesionExternalAllowed = !criticalLootActive
            && (!orbitActive || cohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.CloseCohesionOrbitPreemptMinMeters)
            && (!pathActive || cohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.CloseCohesionPathPreemptMinMeters);
        bool closeCohesionCandidate = VanguardMovementAuthorityDoctrine.TacticalRepositionActiveEnabled
            && closeCohesionDistance
            && closeCohesionShapePressure
            && closeCohesionExternalAllowed
            && !trueThreat
            && !medicalBlocks
            && !forcedCombatCohesionCatchup
            && !hardOutside;
        bool squadCombatContact = VanguardCombatAwarenessBridge.HasMovementAuthoritativeSquadCombatContact(snapshot, DateTimeOffset.UtcNow, out _);
        bool tacticalVolumeJoinCandidate = VanguardMovementAuthorityDoctrine.TacticalRepositionActiveEnabled
            && VanguardMovementAuthorityDoctrine.NeedsTacticalVolumeJoin(snapshot)
            && !trueThreat
            && !combatProductive
            && !squadCombatContact
            && !medicalBlocks
            && !criticalLootActive
            && !hardOutside;
        bool travelHoldActive = VanguardSquadTravelCohesionAuthority.IsPostReturnHoldActive(snapshot.BotProfileId, DateTimeOffset.UtcNow, out _);
        bool orbitQuiescePressure = nonCriticalOrbitOrPath
            && (cohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.OrbitQuiesceMinDistanceMeters
                || orbitOpposesOwner
                || (travelHoldActive && cohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.TravelCohesionPostReturnReacquireMeters));
        bool monotonicTravelPressure = VanguardSquadTravelRouteMemory.ShouldDriveTravel(snapshot, DateTimeOffset.UtcNow, out var monotonicTravelReason);
        bool travelDistancePressure = monotonicTravelPressure
            || cohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.TravelCohesionStartMeters
            || forcedCombatCohesionCatchup
            || (travelHoldActive && cohesion.OperatorDistanceToOwner >= VanguardMovementAuthorityDoctrine.TravelCohesionPostReturnReacquireMeters)
            || (softOutside && cohesion.OperatorDistanceToOwner < VanguardMovementAuthorityDoctrine.HardCorrectionMeters)
            || orbitQuiescePressure;
        bool travelExternalPressure = monotonicTravelPressure
            || orbitActive
            || pathActive
            || travelHoldActive
            || !cohesion.UsefulPosition
            || idleStall
            || orbitQuiescePressure
            || forcedCombatCohesionCatchup;
        bool travelCohesionCandidate = VanguardMovementAuthorityDoctrine.TacticalRepositionActiveEnabled
            && ownerKnown
            && ownerReliable
            && travelDistancePressure
            && travelExternalPressure
            && !trueThreat
            && (!squadCombatContact || forcedCombatCohesionCatchup)
            && !medicalBlocks
            && !criticalLootActive
            && (monotonicTravelPressure || !hardOutside)
            && (monotonicTravelPressure || cohesion.OperatorDistanceToOwner < VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters);
        bool tacticalRepositionCandidate = VanguardMovementAuthorityDoctrine.TacticalRepositionActiveEnabled
            && inBubble
            && !softOutside
            && !trueThreat
            && !medicalBlocks
            && !externalActive
            && (cohesion.SectorDuplicate
                || cohesion.RearOverstacked
                || !cohesion.UsefulPosition
                || !cohesion.SectorTopologyValid
                || idleStall);
        string claimReason = "doctrine_disabled";
        bool claimedCohesionCandidate = VanguardMovementAuthorityDoctrine.TacticalRepositionActiveEnabled
            && VanguardSquadCohesionClaimExecutor.ShouldPublishClaimContract(snapshot, out claimReason)
            && !forcedCombatCohesionCatchup
            && cohesion.OperatorDistanceToOwner < VanguardMovementAuthorityDoctrine.CombatCohesionForcedCatchupMeters
            && !hardOutside;

        if (!ownerKnown)
        {
            return Contract(
                "contract_block_owner_unknown",
                BlockOwnerUnreliable,
                "none",
                false,
                false,
                false,
                false,
                "owner_anchor_unavailable");
        }

        if (!ownerReliable)
        {
            return Contract(
                "contract_block_owner_unreliable",
                BlockOwnerUnreliable,
                "none",
                false,
                false,
                false,
                false,
                "owner_anchor_cached_or_stale_no_active_movement");
        }

        if (trueThreat)
        {
            return Contract(
                "contract_yield_sain_direct_threat",
                YieldSainDirectThreat,
                "SAIN",
                false,
                false,
                false,
                false,
                "true_direct_threat_keeps_sain_authority");
        }

        if (squadCombatContact && combatProductive && !forcedCombatCohesionCatchup && !hardOutside)
        {
            return Contract(
                "contract_yield_sain_squad_contact",
                YieldSainDirectThreat,
                "SAIN",
                false,
                false,
                false,
                false,
                "fresh_productive_squad_contact_keeps_combat_awareness:" + combatProductiveReason);
        }

        if (staleSearchInsideBubble || (sainEnvelopeViolation && generalStaleSainExit))
        {
            string staleReason = string.IsNullOrWhiteSpace(generalStaleReason) ? sainViolationReason : generalStaleReason;
            if (staleHoldAllowed && !forcedCombatCohesionCatchup)
            {
                return Contract(
                    "contract_break_stale_sain_search_hold_sector",
                    BreakSainSearchHoldSector,
                    "none",
                    false,
                    contractLootSuppress,
                    contractOrbitSuppress,
                    true,
                    staleReason + ":" + staleHoldReason);
            }

            return Contract(
                "contract_break_stale_sain_search_combat_cohesion_return",
                BreakSainSearchReturnBubble,
                "BIGBRAIN_GOTOSOMEPOINT",
                hardOutside || forcedCombatCohesionCatchup,
                contractLootSuppress,
                contractOrbitSuppress,
                true,
                staleReason + ":" + forcedCatchupReason + ":holdDenied=" + staleHoldReason);
        }

        if (medicalBlocks)
        {
            return Contract(
                "contract_yield_vanguard_medical",
                YieldVanguardMedical,
                "VanguardMedical",
                false,
                true,
                true,
                false,
                "stationary_or_critical_medical_authority");
        }

        if (travelCohesionCandidate)
        {
            string travelReason = "travel_cohesion_follow_through"
                + ":distance=" + cohesion.OperatorDistanceToOwner.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + ":softOutside=" + Bool(softOutside)
                + ":postReturnHold=" + Bool(travelHoldActive)
                + ":orbit=" + Bool(orbitActive)
                + ":path=" + Bool(pathActive)
                + ":useful=" + Bool(cohesion.UsefulPosition)
                + ":orbitQuiesce=" + Bool(orbitQuiescePressure)
                + ":orbitOpposesOwner=" + Bool(orbitOpposesOwner)
                + ":orbitOwnerDot=" + orbitOwnerAlignment.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                + ":env=" + cohesion.TacticalEnvironmentKind
                + ":CombatProductive=" + Bool(combatProductive)
                + ":forcedCatchup=" + Bool(forcedCombatCohesionCatchup)
                + ":monotonicRoute=" + Bool(monotonicTravelPressure)
                + ":routeReason=" + monotonicTravelReason;
            return Contract(
                "contract_travel_cohesion_follow_through",
                TravelCohesionFollowThrough,
                "BIGBRAIN_GOTOSOMEPOINT",
                true,
                false,
                contractOrbitSuppress || orbitActive,
                false,
                travelReason);
        }

        if (searchOutsideBubble)
        {
            return Contract(
                (hardOutside || forcedCombatCohesionCatchup) ? "contract_break_sain_search_action_rally_hard" : "contract_break_sain_search_action_rally_soft",
                BreakSainSearchReturnBubble,
                "BIGBRAIN_GOTOSOMEPOINT",
                hardOutside || forcedCombatCohesionCatchup,
                contractLootSuppress,
                contractOrbitSuppress,
                true,
                sainViolationReason);
        }

        if (claimedCohesionCandidate)
        {
            return Contract(
                "contract_claimed_cohesion_slot",
                ClaimedCohesionSlot,
                "BIGBRAIN_GOTOSOMEPOINT",
                true,
                false,
                contractOrbitSuppress || orbitActive,
                false,
                "claimed_cohesion_slot:" + claimReason);
        }

        if (tacticalVolumeJoinCandidate)
        {
            string volumeReason = "tactical_volume_join"
                + ":distance=" + cohesion.OperatorDistanceToOwner.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + ":vertical=" + cohesion.VerticalDelta.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + ":path=" + cohesion.OwnerToOperatorPathDistance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + ":ratio=" + cohesion.OwnerToOperatorPathRatio.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                + ":corners=" + cohesion.OwnerToOperatorPathCorners.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":topology=" + cohesion.SectorTopologyReason
                + ":env=" + cohesion.TacticalEnvironmentKind
                + ":CombatProductive=" + Bool(combatProductive)
                + ":forcedCatchup=" + Bool(forcedCombatCohesionCatchup);
            return Contract(
                "contract_tactical_volume_join",
                TacticalVolumeJoin,
                "BIGBRAIN_GOTOSOMEPOINT",
                true,
                false,
                contractOrbitSuppress || orbitActive,
                false,
                volumeReason);
        }

        if (closeCohesionCandidate)
        {
            string closeReason = "close_cohesion_micro_adjust"
                + ":distance=" + cohesion.OperatorDistanceToOwner.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + ":sector=" + cohesion.Sector
                + ":useful=" + Bool(cohesion.UsefulPosition)
                + ":duplicate=" + Bool(cohesion.SectorDuplicate)
                + ":rearOverstacked=" + Bool(cohesion.RearOverstacked)
                + ":orbit=" + Bool(orbitActive)
                + ":path=" + Bool(pathActive)
                + ":env=" + cohesion.TacticalEnvironmentKind
                + ":CombatProductive=" + Bool(combatProductive)
                + ":forcedCatchup=" + Bool(forcedCombatCohesionCatchup);
            return Contract(
                "contract_close_cohesion_micro_adjust",
                CloseCohesionMicroAdjust,
                "BIGBRAIN_GOTOSOMEPOINT",
                true,
                false,
                contractOrbitSuppress || orbitActive,
                false,
                closeReason);
        }

        if (staleSearchInsideBubble || sainEnvelopeViolation)
        {
            if (staleHoldAllowed && !forcedCombatCohesionCatchup)
            {
                return Contract(
                    "contract_break_sain_search_hold_sector",
                    BreakSainSearchHoldSector,
                    "none",
                    false,
                    contractLootSuppress,
                    contractOrbitSuppress,
                    true,
                    sainViolationReason + ":" + staleHoldReason);
            }

            return Contract(
                "contract_break_sain_search_combat_cohesion_return",
                BreakSainSearchReturnBubble,
                "BIGBRAIN_GOTOSOMEPOINT",
                hardOutside || forcedCombatCohesionCatchup,
                contractLootSuppress,
                contractOrbitSuppress,
                true,
                sainViolationReason + ":" + forcedCatchupReason + ":holdDenied=" + staleHoldReason);
        }

        if (hardOutside && externalOutOfDoctrine)
        {
            return Contract(
                "contract_suppress_external_action_rally_hard",
                SuppressExternalAndReturn,
                "BIGBRAIN_GOTOSOMEPOINT",
                true,
                contractLootSuppress,
                contractOrbitSuppress,
                false,
                "external_authority_hard_outside_tactical_bubble_action_rally_return");
        }

        if (hardOutside)
        {
            return Contract(
                "contract_action_rally_hard_return",
                ReturnToBubbleHard,
                "BIGBRAIN_GOTOSOMEPOINT",
                true,
                contractLootSuppress,
                contractOrbitSuppress,
                false,
                "outside_hard_tactical_bubble_action_rally_return");
        }

        if (softOutside)
        {
            return Contract(
                "contract_monitor_soft_bubble",
                MonitorSoftBubbleBreach,
                "none",
                false,
                contractLootSuppress,
                contractOrbitSuppress,
                false,
                "outside_soft_hysteresis_monitor");
        }

        if (externalOutOfDoctrine)
        {
            return Contract(
                "contract_suppress_external_only",
                SuppressExternalAuthorityOnly,
                "none",
                false,
                contractLootSuppress,
                contractOrbitSuppress,
                false,
                "external_authority_out_of_doctrine_inside_bubble");
        }

        if (tacticalRepositionCandidate)
        {
            string tacticalReason = "tactical_sector_reposition"
                + ":sector=" + cohesion.Sector
                + ":useful=" + Bool(cohesion.UsefulPosition)
                + ":duplicate=" + Bool(cohesion.SectorDuplicate)
                + ":rearOverstacked=" + Bool(cohesion.RearOverstacked)
                + ":topology=" + cohesion.SectorTopologyReason
                + ":env=" + cohesion.TacticalEnvironmentKind;
            return Contract(
                "contract_tactical_reposition_sector",
                TacticalRepositionToUsefulSector,
                "BIGBRAIN_GOTOSOMEPOINT",
                true,
                false,
                false,
                false,
                tacticalReason);
        }

        if (idleStall)
        {
            return Contract(
                "contract_observe_idle_stall",
                ObserveIdleStall,
                "none",
                false,
                false,
                false,
                false,
                "idle_stall_suspect_observe_only");
        }

        return Contract(
            "contract_hold_current_authority",
            HoldCurrentAuthority,
            "none",
            false,
            false,
            false,
            false,
            "operator_within_tactical_contract");
    }

    private static VanguardMovementContractSnapshot Contract(
        string key,
        string requestKind,
        string backend,
        bool movementLeaseEligible,
        bool suppressLoot,
        bool suppressOrbit,
        bool suppressSainSearch,
        string reason)
    {
        return new VanguardMovementContractSnapshot
        {
            ContractKey = key,
            RequestKind = requestKind,
            Backend = backend,
            MovementLeaseEligible = movementLeaseEligible,
            WouldSuppressLootingBots = suppressLoot,
            WouldSuppressOrbit = suppressOrbit,
            WouldSuppressSainSearch = suppressSainSearch,
            Reason = reason,
            ReadOnly = true
        };
    }

    private static string Bool(bool value) => value ? "true" : "false";
}
#endif
