#if SPT_CLIENT
using System;
using System.Globalization;
using UnityEngine;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Decision;

// Responsibility: proves that the exact FlankSupport movement being authorized has physically credible support at its destination instead of relying on same-owner/same-threat membership counts.
// Flow: fresh mover -> exact Awareness-qualified threat pose -> exact declared lead/support snapshot -> material/readiness checks -> destination-relative support geometry -> bounded LOS sample for forward penetration -> allow/block classification.
// Authority boundary: classification only. This policy does not plan routes, choose targets, move bots, aim, fire, suppress, infer hard cover from one linecast, or create tactical state; rejection yields the existing movement authority back to SAIN/EFT.
// Invariant: a forward FlankSupport destination is never admitted or continued when explicit support is materially remote, locally unavailable, unable to influence the exact threat, or when required destination exposure evidence cannot be resolved.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class FlankSupportExecutionSafetyPolicy
{
    private const float ForwardDistanceEpsilonMeters = 0.75f;
    private const float CloseSupportDestinationMeters = 24f;
    private const float RecentSupportThreatInfluenceSeconds = 4.0f;

    internal static bool IsExecutionSafe(
        OperatorDecisionSnapshot mover,
        string explicitSupportBotProfileId,
        Vector3 destination,
        DateTimeOffset now,
        bool requireExposureSample,
        out string reason)
    {
        reason = "flank_support_safety_not_evaluated";
        if (mover == null || !mover.Alive)
        {
            reason = "mover_dead_or_missing";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(mover, now, out string moverFreshness))
        {
            reason = "mover_snapshot_not_current:" + Safe(moverFreshness);
            return false;
        }
        if (string.IsNullOrWhiteSpace(explicitSupportBotProfileId)
            || !VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(explicitSupportBotProfileId, out OperatorDecisionSnapshot support)
            || support == null
            || !support.Alive)
        {
            reason = "explicit_support_snapshot_missing_or_dead";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(support, now, out string supportFreshness))
        {
            reason = "explicit_support_snapshot_not_current:" + Safe(supportFreshness);
            return false;
        }
        if (!string.Equals(support.OwnerProfileId, mover.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(NormalizeThreat(support.Threat.EnemyId), NormalizeThreat(mover.Threat.EnemyId), StringComparison.OrdinalIgnoreCase))
        {
            reason = "explicit_support_owner_or_threat_mismatch";
            return false;
        }
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(support.BotProfileId, out VanguardRaidOperatorRuntimeRecord supportRuntime)
            || supportRuntime.BotOwner == null
            || supportRuntime.BotOwner.IsDead)
        {
            reason = "explicit_support_runtime_missing_or_dead";
            return false;
        }
        if (support.Sain.Active != true
            || support.Sain.IsInCombat != true
            || support.Sain.HasEnemy != true
            || support.Sain.RunningToCover == true
            || support.GrenadeHazard.HasRelevantHazard
            || support.Medical.Actionability.GrenadeThrowing
            || support.Medical.Actionability.AnyMedicineUsing
            || support.Medical.Actionability.Reloading)
        {
            reason = "explicit_support_local_combat_readiness_unavailable";
            return false;
        }
        if (!MultiThreatOffensiveSafetyPolicy.IsSupportMateriallyReady(support, now, out string materialSummary))
        {
            reason = "explicit_support_material_not_ready:" + Safe(materialSummary);
            return false;
        }

        if (!TryResolveExactQualifiedThreatPose(
                mover.OwnerProfileId,
                mover.Threat.EnemyId,
                now,
                out Vector3 threatPosition,
                out Vector3 threatLookDirection,
                out string threatPoseReason))
        {
            reason = "exact_threat_pose_unresolved_fail_open_to_sain:" + Safe(threatPoseReason);
            return false;
        }

        float moverThreatDistance = HorizontalDistance(mover.Position, threatPosition);
        float destinationThreatDistance = HorizontalDistance(destination, threatPosition);
        float supportToDestination = HorizontalDistance(support.Position, destination);
        float supportToThreat = HorizontalDistance(support.Position, threatPosition);
        bool forward = destinationThreatDistance + ForwardDistanceEpsilonMeters < moverThreatDistance;
        if (supportToDestination > TacticalOpportunityMovementService.BoundSupportMaxSeparationMeters)
        {
            reason = "explicit_support_remote_from_destination"
                + ":supportToDestination=" + supportToDestination.ToString("0.0", CultureInfo.InvariantCulture)
                + ":max=" + TacticalOpportunityMovementService.BoundSupportMaxSeparationMeters.ToString("0.0", CultureInfo.InvariantCulture)
                + ":forward=" + Bool(forward);
            return false;
        }

        bool supportLineInfluence = HasCredibleThreatInfluenceEvidence(support, out string supportInfluenceProof);

        bool sampleAvailable = false;
        bool destinationLosClear = false;
        float facingDot = float.NaN;
        float facingAngle = float.NaN;
        float sampledThreatDistance = float.NaN;
        int linecasts = 0;
        if (forward)
        {
            if (requireExposureSample)
            {
                bool sampleCompleted = ThreatExposureObservationService.TryObserveExistingPoint(
                    mover.OwnerProfileId,
                    destination,
                    threatPosition,
                    threatLookDirection,
                    ref linecasts,
                    out sampleAvailable,
                    out destinationLosClear,
                    out facingDot,
                    out facingAngle,
                    out sampledThreatDistance);
                if (!sampleCompleted || !sampleAvailable)
                {
                    reason = "forward_destination_exposure_unresolved_fail_open_to_sain"
                        + ":supportToDestination=" + supportToDestination.ToString("0.0", CultureInfo.InvariantCulture)
                        + ":destinationThreatDistance=" + destinationThreatDistance.ToString("0.0", CultureInfo.InvariantCulture);
                    return false;
                }
            }
            if (!supportLineInfluence)
            {
                reason = "forward_destination_without_material_support_line"
                    + ":supportToDestination=" + supportToDestination.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":supportToThreat=" + supportToThreat.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":destinationLos=" + Bool(destinationLosClear)
                    + ":supportInfluenceProof=" + Safe(supportInfluenceProof);
                return false;
            }

            bool closeDestination = destinationThreatDistance <= AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters;
            if (closeDestination && supportToDestination > CloseSupportDestinationMeters)
            {
                reason = "close_forward_destination_support_too_remote"
                    + ":supportToDestination=" + supportToDestination.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":closeSupportMax=" + CloseSupportDestinationMeters.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":destinationThreatDistance=" + destinationThreatDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + ":destinationLos=" + Bool(destinationLosClear)
                    + ":singleOcclusionIsNotHardCover=true";
                return false;
            }
        }

        reason = "flank_support_execution_safe"
            + ":forward=" + Bool(forward)
            + ":moverThreatDistance=" + moverThreatDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ":destinationThreatDistance=" + destinationThreatDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ":supportToDestination=" + supportToDestination.ToString("0.0", CultureInfo.InvariantCulture)
            + ":supportToThreat=" + supportToThreat.ToString("0.0", CultureInfo.InvariantCulture)
            + ":supportLineInfluence=" + Bool(supportLineInfluence)
            + ":supportInfluenceProof=" + Safe(supportInfluenceProof)
            + ":destinationExposureRequired=" + Bool(requireExposureSample)
            + ":destinationExposureSample=" + Bool(sampleAvailable)
            + ":destinationLos=" + Bool(destinationLosClear)
            + ":singleOcclusionIsNotHardCover=true"
            + ":material=" + Safe(materialSummary);
        return true;
    }

    // FlankLead is itself moving while FlankSupport follows it. Requiring an instantaneous LOS/fire
    // bit on every acquisition tick makes a legitimate same-axis pair disappear whenever the lead
    // crosses cover. Preserve threat qualification by accepting only explicit threat read-model evidence: current
    // influence, or a very recent non-stale visual contact. SAIN action labels are never evidence.
    internal static bool HasCredibleThreatInfluenceEvidence(OperatorDecisionSnapshot support, out string proof)
    {
        proof = "none";
        if (support == null)
        {
            proof = "support_missing";
            return false;
        }
        if (MultiThreatOffensiveSafetyPolicy.HasCurrentThreatInfluenceEvidence(support))
        {
            proof = "current_explicit_threat_influence";
            return true;
        }

        float? seenAgo = support.Threat.TimeSinceSeen;
        if (support.Threat.HasThreat
            && support.Threat.EnemyKnown != false
            && !support.Threat.StaleThreat
            && seenAgo.HasValue
            && seenAgo.Value >= 0f
            && seenAgo.Value <= RecentSupportThreatInfluenceSeconds)
        {
            proof = "recent_explicit_seen:age=" + seenAgo.Value.ToString("0.00", CultureInfo.InvariantCulture)
                + ":max=" + RecentSupportThreatInfluenceSeconds.ToString("0.00", CultureInfo.InvariantCulture);
            return true;
        }

        proof = "no_current_or_recent_explicit_threat_influence"
            + ":seenAge=" + (seenAgo.HasValue ? seenAgo.Value.ToString("0.00", CultureInfo.InvariantCulture) : "unknown")
            + ":stale=" + Bool(support.Threat.StaleThreat);
        return false;
    }

    private static bool TryResolveExactQualifiedThreatPose(
        string ownerProfileId,
        string threatId,
        DateTimeOffset now,
        out Vector3 threatPosition,
        out Vector3 threatLookDirection,
        out string reason)
    {
        threatPosition = Vector3.zero;
        threatLookDirection = Vector3.zero;
        string owner = Normalize(ownerProfileId);
        string threat = NormalizeThreat(threatId);
        DateTimeOffset newest = DateTimeOffset.MinValue;
        bool found = false;
        foreach (VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation observation
            in VanguardCombatAwarenessBridge.GetFreshQualifiedTacticalCoordinationThreats(now))
        {
            if (!string.Equals(Normalize(observation.OwnerProfileId), owner, StringComparison.Ordinal)
                || !string.Equals(NormalizeThreat(observation.TargetProfileId), threat, StringComparison.Ordinal)
                || observation.ExpiresAtUtc <= now
                || observation.ObservedAtUtc < newest)
            {
                continue;
            }
            threatPosition = observation.RuntimePosition;
            threatLookDirection = observation.RuntimeLookDirection;
            newest = observation.ObservedAtUtc;
            found = true;
        }

        reason = found ? "qualified_awareness_runtime_pose" : "qualified_awareness_runtime_pose_missing";
        return found;
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    private static string NormalizeThreat(string? value)
    {
        string token = Normalize(value);
        return token is "none" or "null" or "unknown" ? string.Empty : token;
    }
    private static Vector3 Flatten(Vector3 value) { value.y = 0f; return value; }
    private static float HorizontalDistance(Vector3 a, Vector3 b) => Vector3.Distance(Flatten(a), Flatten(b));
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
    private static string Bool(bool value) => value ? "true" : "false";
}
#endif
