#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;

// Responsibility: prevents Vanguard-owned offensive translation from sending one Operator into a materially multi-threat contact without a credible squad support envelope and combat-ready carried material.
// Flow: fresh DecisionSnapshot threat multiplicity -> direct secondary-pressure proof -> exact owner/threat support evidence -> live carried-weapon readiness -> bounded allow/block classification reused by DirectCommit, AltDevelop and BoundAdvance.
// Authority boundary: classification only. This policy never selects a target, aims, fires, opens a movement lease, changes SAIN decisions or mutates inventory; retreat, break-contact, regroup and other survival movement remain outside this veto.
// Invariant: known-enemy count alone never blocks movement; an offensive veto requires direct multi-threat evidence, and once that pressure clears the policy immediately fails open to the owning TC lifecycle.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class MultiThreatOffensiveSafetyPolicy
{
    public const string StatusTag = "VANGUARD_TC_MULTI_THREAT_OFFENSIVE_SAFETY";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, EmissionState> LastEmissionByBotKind = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan UnchangedBlockHeartbeat = TimeSpan.FromSeconds(10d);

    // A complete NavMesh path alone cannot certify an offensive approach through a known
    // enemy firing lane. Reuse the existing, frame-budgeted four-point exposure sampler;
    // unknown/budget-deferred geometry never becomes a positive kill-lane classification.
    // This is a start-admission veto, not a new planner, observer or SAIN locomotion owner.
    internal static bool IsOffensivePreparedRouteSafe(
        OperatorDecisionSnapshot snapshot,
        string expectedThreatId,
        string movementKind,
        IReadOnlyList<Vector3> pathCorners,
        DateTimeOffset now,
        out string reason)
    {
        reason = "route_exposure_not_proven";
        if (snapshot == null || !snapshot.Alive
            || !VanguardOperatorDecisionSnapshotService.IsFresh(snapshot, now, out _)
            || pathCorners == null || pathCorners.Count < 2)
        {
            return true; // Existing freshness and NavMesh gates remain authoritative.
        }

        string expectedThreat = NormalizeThreat(expectedThreatId);
        if (expectedThreat.Length == 0
            || !string.Equals(NormalizeThreat(snapshot.Threat.EnemyId), expectedThreat, StringComparison.OrdinalIgnoreCase)
            || (snapshot.Threat.ShotMeRecently != true && snapshot.Threat.ShotAtMeRecently != true)
            || (snapshot.Threat.EnemyCanShoot != true && snapshot.Threat.EnemyLineOfSight != true && snapshot.Threat.ShotMeRecently != true))
        {
            return true;
        }

        // The solo-push veto must not suppress a genuinely supported maneuver.
        if (TryResolveSupportEnvelope(snapshot, expectedThreat, now, out _))
        {
            reason = "credible_support_envelope_present";
            return true;
        }

        var qualifiedThreats = VanguardCombatAwarenessBridge.GetFreshQualifiedTacticalCoordinationThreats(now);
        foreach (var threat in qualifiedThreats)
        {
            if (!string.Equals(threat.OwnerProfileId, snapshot.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(threat.TargetProfileId, expectedThreat, StringComparison.OrdinalIgnoreCase)
                || threat.RuntimeLookDirection.sqrMagnitude <= 0.01f)
            {
                continue;
            }

            int linecasts = 0;
            if (!ThreatExposureObservationService.TryObserveExistingRoute(
                    snapshot.OwnerProfileId,
                    "offensive_prepared_route_admission",
                    movementKind,
                    snapshot.BotProfileId,
                    pathCorners,
                    threat.RuntimePosition,
                    threat.RuntimeLookDirection,
                    ref linecasts,
                    out RouteExposureObservation? route)
                || route == null)
            {
                return true; // Budget exhaustion and incomplete samples are inconclusive.
            }

            Vector3 approachStart = pathCorners[0] - threat.RuntimePosition;
            Vector3 approachEnd = pathCorners[pathCorners.Count - 1] - threat.RuntimePosition;
            approachStart.y = 0f;
            approachEnd.y = 0f;
            bool materiallyApproachingThreat = approachStart.magnitude - approachEnd.magnitude >= 3.0f;
            bool qualifiedDominatedLane = materiallyApproachingThreat
                && route.SampleCount == 4
                && route.LinecastCount == 4
                && route.UnobstructedSampleCount >= 3
                && route.ForwardVisibleSampleCount >= 3
                && route.FacingWeightedLosEvidenceMean >= 0.45f
                && route.ThreatDistanceMetersMin <= 35.0f;
            if (!qualifiedDominatedLane)
            {
                reason = "no_qualified_dominated_offensive_lane";
                return true;
            }

            reason = "blocked_unsupported_dominated_route:under_fire=true:four_point_route_proof=true";
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_OFFENSIVE_ROUTE_BLOCKED owner={Safe(snapshot.OwnerProfileId)}; bot={Safe(snapshot.BotProfileId)}; threat={Safe(expectedThreat)}; kind={Safe(movementKind)}; routeSamples={route.SampleCount}; clearSamples={route.UnobstructedSampleCount}; forwardVisibleSamples={route.ForwardVisibleSampleCount}; facingWeightedMean={route.FacingWeightedLosEvidenceMean:0.00}; minThreatMeters={route.ThreatDistanceMetersMin:0.0}; support=not_credible; incomingFire=true; movementMutation=false; sainAuthorityPreserved=true");
            return false;
        }

        return true; // No matching fresh awareness-qualified pose.
    }

    internal static bool IsOffensiveTranslationSafe(
        OperatorDecisionSnapshot snapshot,
        string expectedThreatId,
        string movementKind,
        DateTimeOffset now,
        out string reason)
    {
        reason = "multi_threat_safety_not_evaluated";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        if (!VanguardOperatorDecisionSnapshotService.IsFresh(snapshot, now, out string freshnessReason))
        {
            reason = "snapshot_not_current:" + Safe(freshnessReason);
            return false;
        }

        string expectedThreat = NormalizeThreat(expectedThreatId);
        if (expectedThreat.Length == 0)
        {
            reason = "expected_threat_missing";
            return false;
        }

        ThreatPressure pressure = ObservePressure(snapshot, expectedThreat);
        if (!pressure.MultiThreatDirect)
        {
            reason = "multi_threat_direct_pressure_absent:" + pressure.Summary;
            EmitTransition(snapshot, movementKind, blocked: false, reason, pressure, "support_not_required", "material_not_required", now);
            return true;
        }

        bool supportReady = TryResolveSupportEnvelope(snapshot, expectedThreat, now, out string supportSummary);
        bool materialReady = TryResolveMaterialReadiness(snapshot, now, out string materialSummary);
        if (supportReady && materialReady)
        {
            reason = "multi_threat_pressure_supported:" + pressure.Summary;
            EmitTransition(snapshot, movementKind, blocked: false, reason, pressure, supportSummary, materialSummary, now);
            return true;
        }

        reason = !supportReady && !materialReady
            ? "blocked_multi_threat_solo_push:support_and_material_not_ready"
            : !supportReady
                ? "blocked_multi_threat_solo_push:support_envelope_missing"
                : "blocked_multi_threat_solo_push:material_not_ready";
        EmitTransition(snapshot, movementKind, blocked: true, reason, pressure, supportSummary, materialSummary, now);
        return false;
    }

    internal static bool IsOffensiveTranslationSafeWithProspectiveSupport(
        OperatorDecisionSnapshot mover,
        OperatorDecisionSnapshot prospectiveSupport,
        string expectedThreatId,
        string movementKind,
        DateTimeOffset now,
        out string reason)
    {
        reason = "prospective_support_safety_not_evaluated";
        if (mover == null || !mover.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(mover, now, out string freshnessReason))
        {
            reason = "snapshot_not_current:" + Safe(freshnessReason);
            return false;
        }

        string expectedThreat = NormalizeThreat(expectedThreatId);
        if (expectedThreat.Length == 0)
        {
            reason = "expected_threat_missing";
            return false;
        }

        ThreatPressure pressure = ObservePressure(mover, expectedThreat);
        if (!pressure.MultiThreatDirect)
        {
            reason = "multi_threat_direct_pressure_absent:" + pressure.Summary;
            EmitTransition(mover, movementKind, blocked: false, reason, pressure, "prospective_support_not_required", "material_not_required", now);
            return true;
        }

        bool supportReady = TryResolveProspectiveSupportEnvelope(
            mover,
            prospectiveSupport,
            expectedThreat,
            now,
            out string supportSummary);
        bool materialReady = TryResolveMaterialReadiness(mover, now, out string materialSummary);
        if (supportReady && materialReady)
        {
            reason = "multi_threat_pressure_prospectively_supported:" + pressure.Summary;
            EmitTransition(mover, movementKind, blocked: false, reason, pressure, supportSummary, materialSummary, now);
            return true;
        }

        reason = !supportReady && !materialReady
            ? "blocked_multi_threat_solo_push:prospective_support_and_material_not_ready"
            : !supportReady
                ? "blocked_multi_threat_solo_push:prospective_support_envelope_missing"
                : "blocked_multi_threat_solo_push:material_not_ready";
        EmitTransition(mover, movementKind, blocked: true, reason, pressure, supportSummary, materialSummary, now);
        return false;
    }

    internal static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            LastEmissionByBotKind.Clear();
        }

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"TC_MULTI_THREAT_OFFENSIVE_SAFETY_RESET reason={Safe(reason)}; carryForward=false; movementMutation=false; targetMutation=false; aimMutation=false; fireCommand=false");
    }

    private static ThreatPressure ObservePressure(OperatorDecisionSnapshot snapshot, string expectedThreat)
    {
        VanguardThreatScanDecisionSnapshot scan = snapshot.ThreatScan ?? VanguardThreatScanDecisionSnapshot.Empty;
        bool currentThreatMatches = string.Equals(NormalizeThreat(snapshot.Threat.EnemyId), expectedThreat, StringComparison.OrdinalIgnoreCase);
        bool currentDirect = currentThreatMatches
            && (snapshot.Threat.DirectThreat
                || snapshot.Threat.EnemyVisible == true
                || snapshot.Threat.EnemyLineOfSight == true
                || snapshot.Threat.EnemyCanShoot == true
                || snapshot.Threat.ShotMeRecently == true
                || snapshot.Threat.ShotAtMeRecently == true);

        string candidateThreat = NormalizeThreat(scan.CandidateThreatId);
        bool distinctSecondary = candidateThreat.Length > 0
            && !string.Equals(candidateThreat, expectedThreat, StringComparison.OrdinalIgnoreCase);
        bool secondaryDirect = distinctSecondary
            && (scan.CandidateLineOfSight
                || scan.CandidateIncomingFireFresh
                || (scan.CandidateVisible && scan.CandidateCanShoot));

        bool countProven = scan.LineOfSightCount >= 2 || scan.VisibleCount >= 2;
        bool directPairProven = currentDirect && secondaryDirect;
        bool knownUnderFire = currentDirect
            && scan.KnownCount >= 3
            && (scan.CandidateIncomingFireFresh
                || snapshot.Threat.ShotMeRecently == true
                || snapshot.Threat.ShotAtMeRecently == true);
        bool multiThreatDirect = countProven || directPairProven || knownUnderFire;

        string source = countProven
            ? (scan.LineOfSightCount >= 2 ? "los_count" : "visible_count")
            : directPairProven
                ? "current_plus_secondary_direct"
                : knownUnderFire
                    ? "known_under_direct_fire"
                    : "none";

        return new ThreatPressure(
            multiThreatDirect,
            currentDirect,
            secondaryDirect,
            Math.Max(0, scan.KnownCount),
            Math.Max(0, scan.VisibleCount),
            Math.Max(0, scan.LineOfSightCount),
            source);
    }

    private static bool TryResolveSupportEnvelope(
        OperatorDecisionSnapshot mover,
        string expectedThreat,
        DateTimeOffset now,
        out string summary)
    {
        int eligible = 0;
        int materiallyReady = 0;
        int materialRejected = 0;
        int activeSupportContributions = 0;
        int lineSupport = 0;
        string strongestKind = "none";
        string firstMaterialReject = "none";
        string strongestMaterial = "none";

        foreach (OperatorDecisionSnapshot support in VanguardOperatorDecisionSnapshotService.GetLatestSnapshots())
        {
            if (support == null
                || !support.Alive
                || string.IsNullOrWhiteSpace(support.BotProfileId)
                || string.Equals(support.BotProfileId, mover.BotProfileId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(support.OwnerProfileId, mover.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(NormalizeThreat(support.Threat.EnemyId), expectedThreat, StringComparison.OrdinalIgnoreCase)
                || !VanguardOperatorDecisionSnapshotService.IsFresh(support, now, out _)
                || support.Sain == null
                || support.Sain.Active != true
                || support.Sain.HasEnemy != true
                || support.Sain.IsInCombat != true
                || support.Sain.RunningToCover == true
                || support.GrenadeHazard.HasRelevantHazard
                || support.Medical.Actionability.GrenadeThrowing
                || support.Medical.Actionability.AnyMedicineUsing
                || support.Medical.Actionability.Reloading)
            {
                continue;
            }

            float separation = HorizontalDistance(mover.Position, support.Position);
            if (separation > TacticalOpportunityMovementService.BoundSupportMaxSeparationMeters)
            {
                continue;
            }

            eligible++;
            if (!TryResolveSupportMaterialReadiness(support, now, out string supportMaterialSummary))
            {
                materialRejected++;
                if (string.Equals(firstMaterialReject, "none", StringComparison.OrdinalIgnoreCase))
                {
                    firstMaterialReject = supportMaterialSummary;
                }
                continue;
            }

            materiallyReady++;
            strongestMaterial = supportMaterialSummary;
            if (TacticalOpportunityMovementService.TryGetActiveSupportContributionForBot(
                    support.BotProfileId,
                    expectedThreat,
                    out string opportunityKind))
            {
                activeSupportContributions++;
                strongestKind = opportunityKind;
                continue;
            }

            if (ChokeCommitSupportService.HasActiveContributionForBot(support.BotProfileId))
            {
                activeSupportContributions++;
                strongestKind = "ChokeCommitSupport";
                continue;
            }

            bool sameThreatLine = HasCurrentThreatInfluenceEvidence(support);
            if (sameThreatLine)
            {
                lineSupport++;
                if (string.Equals(strongestKind, "none", StringComparison.OrdinalIgnoreCase))
                {
                    strongestKind = "same_threat_line";
                }
            }
        }

        bool ready = activeSupportContributions > 0 || lineSupport > 0;
        summary = "supportEnvelope=" + Bool(ready)
            + ";eligible=" + eligible.ToString(CultureInfo.InvariantCulture)
            + ";materiallyReady=" + materiallyReady.ToString(CultureInfo.InvariantCulture)
            + ";materialRejected=" + materialRejected.ToString(CultureInfo.InvariantCulture)
            + ";activeContributions=" + activeSupportContributions.ToString(CultureInfo.InvariantCulture)
            + ";lineSupport=" + lineSupport.ToString(CultureInfo.InvariantCulture)
            + ";strongest=" + Safe(strongestKind)
            + ";strongestMaterial=" + Safe(strongestMaterial)
            + ";firstMaterialReject=" + Safe(firstMaterialReject)
            + ";maxSeparation=" + TacticalOpportunityMovementService.BoundSupportMaxSeparationMeters.ToString("0.0", CultureInfo.InvariantCulture);
        return ready;
    }

    // A passive support line requires current threat influence evidence. SAIN action labels describe
    // selected behavior, not current LOS/visibility/fire capability, and therefore must never
    // substitute for the explicit threat read-model signals below.
    internal static bool HasCurrentThreatInfluenceEvidence(OperatorDecisionSnapshot support)
        => support != null
            && (support.Threat.EnemyVisible == true
                || support.Threat.EnemyLineOfSight == true
                || support.Threat.EnemyCanShoot == true);

    internal static bool IsSupportMateriallyReady(
        OperatorDecisionSnapshot support,
        DateTimeOffset now,
        out string summary)
        => TryResolveSupportMaterialReadiness(support, now, out summary);

    private static bool TryResolveProspectiveSupportEnvelope(
        OperatorDecisionSnapshot mover,
        OperatorDecisionSnapshot support,
        string expectedThreat,
        DateTimeOffset now,
        out string summary)
    {
        if (support == null
            || !support.Alive
            || string.IsNullOrWhiteSpace(support.BotProfileId)
            || string.Equals(support.BotProfileId, mover.BotProfileId, StringComparison.OrdinalIgnoreCase))
        {
            summary = "prospectiveSupport=false;reason=support_dead_missing_or_same_operator";
            return false;
        }
        if (!string.Equals(support.OwnerProfileId, mover.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
        {
            summary = "prospectiveSupport=false;reason=owner_mismatch";
            return false;
        }
        if (!string.Equals(NormalizeThreat(support.Threat.EnemyId), expectedThreat, StringComparison.OrdinalIgnoreCase))
        {
            summary = "prospectiveSupport=false;reason=exact_threat_mismatch";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(support, now, out string freshnessReason))
        {
            summary = "prospectiveSupport=false;reason=snapshot_not_current:" + Safe(freshnessReason);
            return false;
        }
        if (support.Sain == null
            || support.Sain.Active != true
            || support.Sain.HasEnemy != true
            || support.Sain.IsInCombat != true)
        {
            summary = "prospectiveSupport=false;reason=sain_combat_context_missing";
            return false;
        }
        if (support.Sain.RunningToCover == true
            || support.GrenadeHazard.HasRelevantHazard
            || support.Medical.Actionability.GrenadeThrowing
            || support.Medical.Actionability.AnyMedicineUsing
            || support.Medical.Actionability.Reloading)
        {
            summary = "prospectiveSupport=false;reason=local_survival_action";
            return false;
        }

        float separation = HorizontalDistance(mover.Position, support.Position);
        if (separation > TacticalOpportunityMovementService.BoundSupportMaxSeparationMeters)
        {
            summary = "prospectiveSupport=false;reason=separation_exceeded;separation="
                + separation.ToString("0.0", CultureInfo.InvariantCulture)
                + ";max=" + TacticalOpportunityMovementService.BoundSupportMaxSeparationMeters.ToString("0.0", CultureInfo.InvariantCulture);
            return false;
        }
        if (!TryResolveSupportMaterialReadiness(support, now, out string materialSummary))
        {
            summary = "prospectiveSupport=false;reason=material_not_ready;" + materialSummary;
            return false;
        }

        summary = "prospectiveSupport=true;separation="
            + separation.ToString("0.0", CultureInfo.InvariantCulture)
            + ";" + materialSummary;
        return true;
    }

    private static bool TryResolveSupportMaterialReadiness(
        OperatorDecisionSnapshot support,
        DateTimeOffset now,
        out string summary)
    {
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(
                support.BotProfileId,
                out VanguardRaidOperatorRuntimeRecord runtime)
            || runtime.BotOwner == null
            || runtime.BotOwner.IsDead)
        {
            summary = "supportMaterialReady=false;reason=runtime_missing_or_dead";
            return false;
        }

        VanguardLiveEquipmentCapabilitySnapshot equipment =
            VanguardDynamicWeaponPreferenceService.GetEquipmentSnapshot(runtime.BotOwner, now);
        if (!equipment.Observed)
        {
            summary = "supportMaterialReady=false;reason=equipment_unobserved:"
                + Safe(equipment.ObservationReason);
            return false;
        }

        VanguardLiveWeaponCapabilityObservation? selected = FindActuallySelectedWeapon(
            runtime,
            equipment,
            out bool selectorReady,
            out string selectedSlot);

        if (!selectorReady)
        {
            summary = "supportMaterialReady=false;reason=selected_weapon_not_ready_or_switching"
                + ";selectedSlot=" + Safe(selectedSlot)
                + ";dwpConvergenceMustFinish=true";
            return false;
        }

        if (selected == null)
        {
            summary = "supportMaterialReady=false;reason=selected_weapon_not_observed"
                + ";selectedSlot=" + Safe(selectedSlot)
                + ";weaponCount=" + Math.Max(0, equipment.WeaponCount).ToString(CultureInfo.InvariantCulture);
            return false;
        }

        bool selectedOperational = !selected.Bricked
            && selected.ReadyRounds > 0
            && selected.TotalObservedRounds > VanguardDynamicWeaponPreferenceService.CriticalLowReadyRounds;
        if (!selectedOperational)
        {
            summary = "supportMaterialReady=false;reason=selected_weapon_not_sustained"
                + ";selectedSlot=" + Safe(selected.Slot)
                + ";family=" + Safe(selected.CanonicalWeaponFamily)
                + ";ready=" + selected.ReadyRounds.ToString(CultureInfo.InvariantCulture)
                + ";total=" + selected.TotalObservedRounds.ToString(CultureInfo.InvariantCulture)
                + ";bricked=" + Bool(selected.Bricked)
                + ";dwpAlternativeDoesNotCountUntilSelected=true";
            return false;
        }

        bool closeThreat = support.Threat.Distance.HasValue
            && support.Threat.Distance.Value <= AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters;
        if (!closeThreat && !selected.IsLongWeapon)
        {
            int readyLongAlternatives = equipment.Weapons.Count(weapon => weapon != null
                && weapon.IsLongWeapon
                && !weapon.Bricked
                && weapon.ReadyRounds > 0
                && weapon.TotalObservedRounds > VanguardDynamicWeaponPreferenceService.CriticalLowReadyRounds);
            summary = "supportMaterialReady=false;reason=range_credible_long_weapon_not_selected"
                + ";selectedSlot=" + Safe(selected.Slot)
                + ";selectedFamily=" + Safe(selected.CanonicalWeaponFamily)
                + ";threatDistance=" + (support.Threat.Distance.HasValue
                    ? support.Threat.Distance.Value.ToString("0.0", CultureInfo.InvariantCulture)
                    : "unknown")
                + ";readyLongAlternatives=" + readyLongAlternatives.ToString(CultureInfo.InvariantCulture)
                + ";dwpConvergenceMustFinish=true";
            return false;
        }

        summary = "supportMaterialReady=true"
            + ";mode=" + (closeThreat ? "close_selected_operational" : "range_selected_long_weapon")
            + ";selectedInHands=true"
            + ";selectorReady=true"
            + ";slot=" + Safe(selected.Slot)
            + ";family=" + Safe(selected.CanonicalWeaponFamily)
            + ";ready=" + selected.ReadyRounds.ToString(CultureInfo.InvariantCulture)
            + ";total=" + selected.TotalObservedRounds.ToString(CultureInfo.InvariantCulture)
            + ";longWeapon=" + Bool(selected.IsLongWeapon)
            + ";bricked=false";
        return true;
    }

    private static bool TryResolveMaterialReadiness(
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        out string summary)
    {
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(
                snapshot.BotProfileId,
                out VanguardRaidOperatorRuntimeRecord runtime)
            || runtime.BotOwner == null
            || runtime.BotOwner.IsDead)
        {
            summary = "materialReady=false;reason=runtime_missing_or_dead";
            return false;
        }

        VanguardLiveEquipmentCapabilitySnapshot equipment =
            VanguardDynamicWeaponPreferenceService.GetEquipmentSnapshot(runtime.BotOwner, now);
        if (!equipment.Observed)
        {
            summary = "materialReady=false;reason=equipment_unobserved:" + Safe(equipment.ObservationReason);
            return false;
        }

        VanguardLiveWeaponCapabilityObservation? selected = FindActuallySelectedWeapon(
            runtime,
            equipment,
            out bool selectorReady,
            out string selectedSlot);

        if (!selectorReady)
        {
            summary = "materialReady=false;reason=selected_weapon_not_ready_or_switching"
                + ";selectedSlot=" + Safe(selectedSlot)
                + ";dwpConvergenceMustFinish=true";
            return false;
        }

        if (selected == null)
        {
            summary = "materialReady=false;reason=selected_weapon_not_observed"
                + ";selectedSlot=" + Safe(selectedSlot)
                + ";weaponCount=" + Math.Max(0, equipment.WeaponCount).ToString(CultureInfo.InvariantCulture);
            return false;
        }

        bool selectedMultiThreatReady = selected.IsLongWeapon
            && !selected.Bricked
            && selected.ReadyRounds > 0
            && selected.TotalObservedRounds > VanguardDynamicWeaponPreferenceService.CriticalLowReadyRounds;
        if (!selectedMultiThreatReady)
        {
            int readyLongAlternatives = equipment.Weapons.Count(weapon => weapon != null
                && weapon.IsLongWeapon
                && !weapon.Bricked
                && weapon.ReadyRounds > 0
                && weapon.TotalObservedRounds > VanguardDynamicWeaponPreferenceService.CriticalLowReadyRounds);
            summary = "materialReady=false;reason=multi_threat_ready_long_weapon_not_selected"
                + ";selectedSlot=" + Safe(selected.Slot)
                + ";selectedFamily=" + Safe(selected.CanonicalWeaponFamily)
                + ";selectedLongWeapon=" + Bool(selected.IsLongWeapon)
                + ";selectedReady=" + selected.ReadyRounds.ToString(CultureInfo.InvariantCulture)
                + ";selectedTotal=" + selected.TotalObservedRounds.ToString(CultureInfo.InvariantCulture)
                + ";selectedBricked=" + Bool(selected.Bricked)
                + ";readyLongAlternatives=" + readyLongAlternatives.ToString(CultureInfo.InvariantCulture)
                + ";dwpConvergenceMustFinish=true";
            return false;
        }

        summary = "materialReady=true"
            + ";selectedInHands=true"
            + ";selectorReady=true"
            + ";slot=" + Safe(selected.Slot)
            + ";family=" + Safe(selected.CanonicalWeaponFamily)
            + ";ready=" + selected.ReadyRounds.ToString(CultureInfo.InvariantCulture)
            + ";total=" + selected.TotalObservedRounds.ToString(CultureInfo.InvariantCulture)
            + ";longWeapon=true"
            + ";bricked=false";
        return true;
    }

    private static VanguardLiveWeaponCapabilityObservation? FindActuallySelectedWeapon(
        VanguardRaidOperatorRuntimeRecord runtime,
        VanguardLiveEquipmentCapabilitySnapshot equipment,
        out bool selectorReady,
        out string selectedSlot)
    {
        var selector = runtime.BotOwner?.WeaponManager?.Selector;
        selectorReady = selector?.IsWeaponReady == true;
        selectedSlot = selector == null ? string.Empty : selector.EquipmentSlot.ToString();
        if (selector == null || string.IsNullOrWhiteSpace(selectedSlot))
        {
            return null;
        }

        string selectedSlotSnapshot = selectedSlot;
        return equipment.Weapons.FirstOrDefault(weapon =>
            weapon != null
            && string.Equals(weapon.Slot, selectedSlotSnapshot, StringComparison.OrdinalIgnoreCase));
    }

    private static void EmitTransition(
        OperatorDecisionSnapshot snapshot,
        string movementKind,
        bool blocked,
        string decisionReason,
        ThreatPressure pressure,
        string supportSummary,
        string materialSummary,
        DateTimeOffset now)
    {
        string key = Safe(snapshot.BotProfileId) + "|" + Safe(movementKind);
        string signature = (blocked ? "blocked" : "ready")
            + "|" + pressure.Source
            + "|" + pressure.KnownCount.ToString(CultureInfo.InvariantCulture)
            + "|" + pressure.VisibleCount.ToString(CultureInfo.InvariantCulture)
            + "|" + pressure.LineOfSightCount.ToString(CultureInfo.InvariantCulture)
            + "|" + supportSummary
            + "|" + materialSummary;

        bool emit = false;
        bool restored = false;
        lock (Sync)
        {
            if (!LastEmissionByBotKind.TryGetValue(key, out EmissionState? state))
            {
                state = new EmissionState
                {
                    Signature = signature,
                    Blocked = blocked
                };
                LastEmissionByBotKind[key] = state;
                if (!blocked)
                {
                    return;
                }
            }

            restored = state.Blocked && !blocked;
            if (!string.Equals(state.Signature, signature, StringComparison.Ordinal)
                || restored
                || (blocked && now - state.LastEmittedAtUtc >= UnchangedBlockHeartbeat))
            {
                emit = true;
                state.Signature = signature;
                state.LastEmittedAtUtc = now;
            }
            state.Blocked = blocked;
        }

        if (!emit)
        {
            return;
        }

        string eventName = blocked
            ? "TC_OFFENSIVE_SAFETY_BLOCKED"
            : restored
                ? "TC_OFFENSIVE_SAFETY_RESTORED"
                : "TC_OFFENSIVE_SAFETY_READY";
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"{eventName} bot={Safe(snapshot.BotProfileId)}; owner={Safe(snapshot.OwnerProfileId)}; movement={Safe(movementKind)}; threat={Safe(snapshot.Threat.EnemyId)}; reason={Safe(decisionReason)}; multiThreatDirect={Bool(pressure.MultiThreatDirect)}; pressureSource={Safe(pressure.Source)}; known={pressure.KnownCount}; visible={pressure.VisibleCount}; los={pressure.LineOfSightCount}; currentDirect={Bool(pressure.CurrentDirect)}; secondaryDirect={Bool(pressure.SecondaryDirect)}; {supportSummary}; {materialSummary}; blocked_multi_threat_solo_push={Bool(blocked)}; support_envelope_present={Bool(supportSummary.Contains("supportEnvelope=true", StringComparison.Ordinal))}; offensive_translation_released={Bool(restored)}; movementMutation=false; targetMutation=false; aimMutation=false; fireCommand=false");
    }

    private static float HorizontalDistance(UnityEngine.Vector3 a, UnityEngine.Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    private static string NormalizeThreat(string? value)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        return string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "null", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "unknown", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : normalized;
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private static string Bool(bool value) => value ? "true" : "false";

    private sealed class EmissionState
    {
        public string Signature = string.Empty;
        public DateTimeOffset LastEmittedAtUtc = DateTimeOffset.MinValue;
        public bool Blocked;
    }

    private readonly struct ThreatPressure
    {
        public ThreatPressure(
            bool multiThreatDirect,
            bool currentDirect,
            bool secondaryDirect,
            int knownCount,
            int visibleCount,
            int lineOfSightCount,
            string source)
        {
            MultiThreatDirect = multiThreatDirect;
            CurrentDirect = currentDirect;
            SecondaryDirect = secondaryDirect;
            KnownCount = knownCount;
            VisibleCount = visibleCount;
            LineOfSightCount = lineOfSightCount;
            Source = source;
        }

        public bool MultiThreatDirect { get; }
        public bool CurrentDirect { get; }
        public bool SecondaryDirect { get; }
        public int KnownCount { get; }
        public int VisibleCount { get; }
        public int LineOfSightCount { get; }
        public string Source { get; }
        public string Summary => "source=" + Safe(Source)
            + ";known=" + KnownCount.ToString(CultureInfo.InvariantCulture)
            + ";visible=" + VisibleCount.ToString(CultureInfo.InvariantCulture)
            + ";los=" + LineOfSightCount.ToString(CultureInfo.InvariantCulture)
            + ";currentDirect=" + Bool(CurrentDirect)
            + ";secondaryDirect=" + Bool(SecondaryDirect);
    }
}
#endif
