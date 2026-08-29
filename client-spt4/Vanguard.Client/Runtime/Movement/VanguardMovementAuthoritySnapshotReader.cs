#if SPT_CLIENT
using System;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Reads and normalizes live evidence for Movement Authority Snapshot Reader in the movement/cohesion runtime.
// Flow: Live EFT/Fika/Vanguard objects are inspected defensively, normalized into a bounded snapshot, then handed to policy/decision code.
// Authority boundary: Read-only observer; it does not create missing truth or mutate the game state it inspects.
// Invariant: Missing/stale evidence degrades explicitly and reader failures must not silently fabricate an actionable state.
namespace Vanguard.Client.Runtime.Decision;

internal sealed partial class VanguardOperatorDecisionSnapshotBuilder
{
    private VanguardMovementAuthoritySnapshot CaptureMovementAuthority(
        VanguardRaidOperatorRuntimeRecord record,
        bool alive,
        float realSpeed,
        VanguardMovementDecisionSnapshot movement,
        VanguardBrainDecisionSnapshot brain,
        VanguardSainDecisionSnapshot sain,
        VanguardThreatDecisionSnapshot threat,
        VanguardMedicalDecisionSnapshot medical,
        VanguardSquadCohesionSnapshot cohesion,
        VanguardLootDecisionSnapshot loot,
        VanguardOrbitDecisionSnapshot orbit)
    {
        if (!alive)
        {
            return new VanguardMovementAuthoritySnapshot
            {
                Enabled = true,
                ReadOnly = true,
                ActiveMovementAllowed = false,
                CurrentAuthority = "none",
                Classification = "movement_authority_dead_operator",
                Reason = "operator_dead"
            };
        }

        var partial = new OperatorDecisionSnapshot
        {
            OperatorId = record.OperatorId,
            OwnerProfileId = record.OwnerProfileId,
            BotProfileId = record.BotProfileId,
            Nickname = record.BotNickname,
            Alive = alive,
            RealSpeed = realSpeed,
            Movement = movement,
            Brain = brain,
            Sain = sain,
            Threat = threat,
            Medical = medical,
            SquadCohesion = cohesion,
            Looting = loot,
            Orbit = orbit
        };

        bool sainSearchLike = Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.IsSainSearchLike(partial);
        bool sainLocalDefensive = Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.IsSainLocalDefensiveLike(partial);
        bool sainViolation = Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.IsSainEnvelopeViolation(partial, out string sainViolationReason);
        bool medicalBlocks = Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(partial);
        bool trueDirectThreat = Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(partial);
        bool softOutside = cohesion.OwnerKnown && cohesion.OperatorDistanceToOwner > Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.TacticalBubbleMeters;
        bool hardOutside = cohesion.OwnerKnown && cohesion.OperatorDistanceToOwner >= Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.HardCorrectionMeters;
        bool eftPathActive = movement.HasPath == true;
        bool lootingActive = loot.BotLooting == true || loot.LootTaskRunning == true || loot.HasActiveLootable == true;
        bool orbitActive = orbit.Active;
        bool lootAllowed = cohesion.InBubble
            && !hardOutside
            && !trueDirectThreat
            && !medicalBlocks
            && !sainViolation
            && !Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.IsRegroupOrder(cohesion.SquadOrder);
        bool orbitAllowed = cohesion.InBubble
            && !hardOutside
            && !trueDirectThreat
            && !medicalBlocks
            && !sainViolation;
        bool lootWouldSuppress = lootingActive && !lootAllowed;
        bool orbitWouldSuppress = orbitActive && !orbitAllowed;
        bool idleStallSuspect = !trueDirectThreat
            && !medicalBlocks
            && !eftPathActive
            && realSpeed <= 0.15f
            && Contains(brain.ActiveLayer, "orbit")
            && !orbitActive
            && !lootingActive;

        string currentOwner = Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.MovementOwner(partial, sainViolation);
        var brokerPlan = Vanguard.Client.Runtime.Movement.VanguardMovementBrokerDryRun.Build(partial, sainViolation, sainViolationReason, lootWouldSuppress, orbitWouldSuppress, idleStallSuspect);
        string classification = ClassifyMovementAuthority(cohesion, sainViolation, hardOutside, softOutside, lootWouldSuppress, orbitWouldSuppress, idleStallSuspect, currentOwner);
        string reason = BuildMovementAuthorityReason(cohesion, sainViolation, sainViolationReason, medicalBlocks, trueDirectThreat, lootWouldSuppress, orbitWouldSuppress, brokerPlan);

        return new VanguardMovementAuthoritySnapshot
        {
            Enabled = true,
            ReadOnly = true,
            ActiveMovementAllowed = false,
            CurrentAuthority = currentOwner,
            CurrentAuthorityReason = reason,
            OwnerKnown = cohesion.OwnerKnown,
            OwnerReliableForActiveMovement = cohesion.OwnerReliableForActiveMovement,
            OwnerAnchorSource = cohesion.OwnerAnchorSource,
            OwnerAnchorAgeSeconds = cohesion.OwnerAnchorAgeSeconds,
            SoftOutsideBubble = softOutside,
            HardOutsideBubble = hardOutside,
            SainSearchLike = sainSearchLike,
            SainLocalDefensiveLike = sainLocalDefensive,
            SainEnvelopeViolation = sainViolation,
            SainEnvelopeViolationReason = sainViolationReason,
            LootingBotsAllowed = lootAllowed,
            LootingBotsWouldSuppress = lootWouldSuppress,
            OrbitAllowed = orbitAllowed,
            OrbitWouldSuppress = orbitWouldSuppress,
            EftPathActive = eftPathActive,
            MovementStallSuspect = idleStallSuspect,
            Classification = classification,
            Reason = reason,
            BrokerPlan = brokerPlan
        };
    }

    private static string ClassifyMovementAuthority(VanguardSquadCohesionSnapshot cohesion, bool sainViolation, bool hardOutside, bool softOutside, bool lootSuppress, bool orbitSuppress, bool idleStall, string currentOwner)
    {
        if (!cohesion.OwnerKnown)
        {
            return "move_auth_owner_unknown";
        }

        if (!cohesion.OwnerReliableForActiveMovement)
        {
            return "move_auth_owner_cached_readonly";
        }

        if (sainViolation)
        {
            return "move_auth_sain_envelope_violation";
        }

        if (hardOutside)
        {
            return "move_auth_hard_outside_bubble";
        }

        if (softOutside)
        {
            return "move_auth_soft_outside_bubble";
        }

        if (lootSuppress || orbitSuppress)
        {
            return "move_auth_external_out_of_doctrine";
        }

        if (idleStall)
        {
            return "move_auth_idle_stall_suspect";
        }

        return "move_auth_observe_" + currentOwner.ToLowerInvariant();
    }

    private static string BuildMovementAuthorityReason(VanguardSquadCohesionSnapshot cohesion, bool sainViolation, string sainViolationReason, bool medicalBlocks, bool trueDirectThreat, bool lootSuppress, bool orbitSuppress, VanguardMovementBrokerPlanSnapshot brokerPlan)
    {
        return "owner=" + cohesion.OwnerAnchorSource
            + ";ownerReliable=" + cohesion.OwnerReliableForActiveMovement
            + ";bubbleDist=" + cohesion.OperatorDistanceToOwner.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            + ";medicalBlocks=" + medicalBlocks
            + ";trueThreat=" + trueDirectThreat
            + ";sainViolation=" + sainViolation
            + ";sainReason=" + sainViolationReason
            + ";lootSuppress=" + lootSuppress
            + ";orbitSuppress=" + orbitSuppress
            + ";broker=" + brokerPlan.PlanKey;
    }

    private static bool Contains(string? text, string token)
    {
        return !string.IsNullOrWhiteSpace(text) && text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
#endif
