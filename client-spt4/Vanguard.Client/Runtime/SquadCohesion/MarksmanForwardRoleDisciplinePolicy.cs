#if SPT_CLIENT
using Comfort.Common;
using System;
using System.Globalization;
using EFT;
using UnityEngine;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Decision;

// Responsibility: prevents a designated Marksman / Precision-overwatch Operator from becoming the unsupported forward assault element against a high-threat contact when a viable non-marksman assault element exists.
// Flow: exact runtime identity -> exact qualified threat classification/pose -> fresh same-owner assault-alternative evidence -> destination standoff/point comparison -> bounded allow/block classification consumed by existing TC movement authorities.
// Authority boundary: classification only. This policy never selects targets, creates routes, opens movement leases, changes SAIN decisions, aims, fires, reloads or mutates inventory; a rejection simply leaves ordinary combat translation with SAIN/EFT.
// Invariant: ordinary Scav-class contacts and no-alternative cases remain permissive, while high-threat forward movement may not carry a designated Marksman inside the owner-scoped Overwatch standoff or ahead of a viable assault-capable sibling.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class MarksmanForwardRoleDisciplinePolicy
{
    private const float ForwardDistanceEpsilonMeters = 0.75f;

    internal static bool CanAuthorizeForwardTranslation(
        OperatorDecisionSnapshot snapshot,
        string expectedThreatId,
        Vector3 destination,
        string movementKind,
        DateTimeOffset now,
        out string reason)
    {
        reason = "marksman_role_discipline_not_applicable";
        if (!IsForwardAssaultMovementKind(movementKind))
        {
            return true;
        }
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out VanguardRaidOperatorRuntimeRecord runtime)
            || runtime.BotOwner == null
            || runtime.BotOwner.IsDead)
        {
            reason = "runtime_missing_or_dead";
            return false;
        }
        if (!IsDesignatedMarksmanIdentity(runtime.TacticalRole, runtime.TacticalSpecialty))
        {
            reason = "operator_not_designated_marksman";
            return true;
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(snapshot, now, out string freshnessReason))
        {
            reason = "marksman_snapshot_not_current_fail_open_to_sain:" + Safe(freshnessReason);
            return false;
        }

        if (!TryResolveHighThreatContact(expectedThreatId, out bool highThreat, out string threatClass))
        {
            reason = "threat_classification_unresolved_fail_open_to_sain";
            return false;
        }
        if (!highThreat)
        {
            reason = "ordinary_scav_class_contact:" + Safe(threatClass);
            return true;
        }
        if (!TryResolveExactQualifiedThreatPose(snapshot.OwnerProfileId, expectedThreatId, now, out Vector3 threatPosition, out string poseReason))
        {
            reason = "high_threat_pose_unresolved_fail_open_to_sain:" + Safe(poseReason) + ";class=" + Safe(threatClass);
            return false;
        }

        bool viableAlternative = HasViableNonMarksmanAssaultAlternative(
            snapshot,
            expectedThreatId,
            threatPosition,
            now,
            out float nearestAlternativeThreatDistance,
            out string alternativeSummary);
        if (!viableAlternative)
        {
            reason = "marksman_forward_discipline_ready:highThreat=true;class=" + Safe(threatClass) + ";" + alternativeSummary;
            return true;
        }

        float currentThreatDistance = HorizontalDistance(snapshot.Position, threatPosition);
        float destinationThreatDistance = HorizontalDistance(destination, threatPosition);
        float standoff = VanguardTacticalCoordinationTuningPolicy.ForOwner(snapshot.OwnerProfileId).OverwatchStandoffMeters;
        bool forward = destinationThreatDistance + ForwardDistanceEpsilonMeters < currentThreatDistance;
        bool insideStandoff = destinationThreatDistance < standoff;
        bool wouldBecomePoint = destinationThreatDistance + ForwardDistanceEpsilonMeters < nearestAlternativeThreatDistance;
        bool violation = forward && (insideStandoff || wouldBecomePoint);

        string geometrySummary = "highThreat=true"
            + ";class=" + Safe(threatClass)
            + ";forward=" + Bool(forward)
            + ";currentThreatDistance=" + currentThreatDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";destinationThreatDistance=" + destinationThreatDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";overwatchStandoff=" + standoff.ToString("0.0", CultureInfo.InvariantCulture)
            + ";nearestAssaultThreatDistance=" + nearestAlternativeThreatDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";insideStandoff=" + Bool(insideStandoff)
            + ";wouldBecomePoint=" + Bool(wouldBecomePoint)
            + ";violation=" + Bool(violation);
        reason = violation
            ? "blocked_marksman_forward_high_threat:" + geometrySummary + ";" + alternativeSummary
            : "marksman_forward_discipline_ready:" + geometrySummary + ";" + alternativeSummary;
        return !violation;
    }

    internal static bool IsDesignatedMarksmanIdentity(string? role, string? specialty)
    {
        string roleKey = NormalizeKey(role);
        string specialtyKey = NormalizeKey(specialty);
        return roleKey == "marksman"
            || specialtyKey.Contains("precisionoverwatch", StringComparison.Ordinal)
            || specialtyKey == "overwatch";
    }

    internal static bool IsAssaultCapableRole(string? role)
    {
        string roleKey = NormalizeKey(role);
        return roleKey is "assault" or "veteran" or "breacher" or "recon";
    }

    private static bool HasViableNonMarksmanAssaultAlternative(
        OperatorDecisionSnapshot mover,
        string expectedThreatId,
        Vector3 threatPosition,
        DateTimeOffset now,
        out float nearestThreatDistance,
        out string summary)
    {
        string expectedThreat = NormalizeThreat(expectedThreatId);
        int sameOwnerLive = 0;
        int sameThreatAnyAge = 0;
        int sameThreatStale = 0;
        int sameThreatFresh = 0;
        int assaultCapable = 0;
        int runtimeUnavailable = 0;
        int roleIneligible = 0;
        int combatNotReady = 0;
        int actionGuarded = 0;
        int hardReturnGuarded = 0;
        int nonFiniteDistance = 0;
        nearestThreatDistance = float.PositiveInfinity;
        foreach (OperatorDecisionSnapshot sibling in VanguardOperatorDecisionSnapshotService.GetLatestSnapshots())
        {
            if (sibling == null
                || !sibling.Alive
                || string.IsNullOrWhiteSpace(sibling.BotProfileId)
                || string.Equals(sibling.BotProfileId, mover.BotProfileId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(sibling.OwnerProfileId, mover.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // Diagnostic counters describe the same ordered gates as before. Neither the
            // admitted sibling set nor the nearest threat distance changes.
            sameOwnerLive++;
            if (!string.Equals(NormalizeThreat(sibling.Threat.EnemyId), expectedThreat, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            sameThreatAnyAge++;
            if (!VanguardOperatorDecisionSnapshotService.IsFresh(sibling, now, out _))
            {
                sameThreatStale++;
                continue;
            }

            sameThreatFresh++;
            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(sibling.BotProfileId, out VanguardRaidOperatorRuntimeRecord siblingRuntime)
                || siblingRuntime.BotOwner == null
                || siblingRuntime.BotOwner.IsDead)
            {
                runtimeUnavailable++;
                continue;
            }
            if (IsDesignatedMarksmanIdentity(siblingRuntime.TacticalRole, siblingRuntime.TacticalSpecialty)
                || !IsAssaultCapableRole(siblingRuntime.TacticalRole))
            {
                roleIneligible++;
                continue;
            }
            if (sibling.Sain.Active != true
                || sibling.Sain.IsInCombat != true
                || sibling.Sain.HasEnemy != true)
            {
                combatNotReady++;
                continue;
            }
            if (sibling.Sain.RunningToCover == true
                || sibling.GrenadeHazard.HasRelevantHazard
                || sibling.Medical.Actionability.GrenadeThrowing
                || sibling.Medical.Actionability.AnyMedicineUsing
                || sibling.Medical.Actionability.Reloading)
            {
                actionGuarded++;
                continue;
            }
            if (sibling.MovementAuthority.HardOutsideBubble)
            {
                hardReturnGuarded++;
                continue;
            }

            float siblingThreatDistance = HorizontalDistance(sibling.Position, threatPosition);
            if (float.IsNaN(siblingThreatDistance) || float.IsInfinity(siblingThreatDistance))
            {
                nonFiniteDistance++;
                continue;
            }

            assaultCapable++;
            nearestThreatDistance = Math.Min(nearestThreatDistance, siblingThreatDistance);
        }

        bool available = assaultCapable > 0 && !float.IsPositiveInfinity(nearestThreatDistance);
        summary = "viableNonMarksmanAssaultAlternative=" + Bool(available)
            + ";sameThreatFreshSiblings=" + sameThreatFresh.ToString(CultureInfo.InvariantCulture)
            + ";assaultCapableReady=" + assaultCapable.ToString(CultureInfo.InvariantCulture)
            + ";sameOwnerLiveSiblings=" + sameOwnerLive.ToString(CultureInfo.InvariantCulture)
            + ";sameThreatSiblingsAnyAge=" + sameThreatAnyAge.ToString(CultureInfo.InvariantCulture)
            + ";sameThreatStaleSiblings=" + sameThreatStale.ToString(CultureInfo.InvariantCulture)
            + ";assaultExcludedRuntime=" + runtimeUnavailable.ToString(CultureInfo.InvariantCulture)
            + ";assaultExcludedRole=" + roleIneligible.ToString(CultureInfo.InvariantCulture)
            + ";assaultExcludedCombat=" + combatNotReady.ToString(CultureInfo.InvariantCulture)
            + ";assaultExcludedActivity=" + actionGuarded.ToString(CultureInfo.InvariantCulture)
            + ";assaultExcludedHardReturn=" + hardReturnGuarded.ToString(CultureInfo.InvariantCulture)
            + ";assaultExcludedDistance=" + nonFiniteDistance.ToString(CultureInfo.InvariantCulture)
            + ";nearestAssaultThreatDistance=" + (available
                ? nearestThreatDistance.ToString("0.0", CultureInfo.InvariantCulture)
                : "none");
        return available;
    }

    internal static bool TryClassifyHighThreatContact(string expectedThreatId, out bool highThreat, out string classification)
        => TryResolveHighThreatContact(expectedThreatId, out highThreat, out classification);

    private static bool TryResolveHighThreatContact(string expectedThreatId, out bool highThreat, out string classification)
    {
        highThreat = false;
        classification = "unresolved";
        string threat = NormalizeThreat(expectedThreatId);
        if (threat.Length == 0)
        {
            return false;
        }

        Player? target = null;
        try
        {
            target = Singleton<GameWorld>.Instance?.GetAlivePlayerByProfileID(expectedThreatId);
        }
        catch
        {
            target = null;
        }
        if (target == null || !string.Equals(NormalizeThreat(target.ProfileId), threat, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string role = target.Profile?.Info?.Settings?.Role.ToString() ?? string.Empty;
        string roleKey = NormalizeKey(role);
        if (target.Side is EPlayerSide.Usec or EPlayerSide.Bear)
        {
            highThreat = true;
            classification = "pmc:" + Safe(role);
            return true;
        }
        if (target.Side == EPlayerSide.Savage && IsOrdinaryScavRole(roleKey))
        {
            classification = "ordinary_scav:" + Safe(role);
            return true;
        }

        highThreat = true;
        classification = "special_or_high_threat:" + Safe(target.Side.ToString()) + ":" + Safe(role);
        return true;
    }

    // Shadow alternative-route assessment reuses precisely the same live PMC/boss and qualified
    // owner/threat pose contract as the Marksman TC admission policy. Not an authorization token.
    internal static bool TryGetExactHighThreatPoseForSearchShadow(
        string ownerProfileId, string enemyId, DateTimeOffset now,
        out Vector3 threatPosition, out string reason)
    {
        threatPosition = Vector3.zero;
        if (!TryResolveHighThreatContact(enemyId, out bool highThreat, out string threatClass) || !highThreat)
        {
            reason = "search_shadow_not_qualified_high_threat:" + Safe(threatClass);
            return false;
        }
        return TryResolveExactQualifiedThreatPose(ownerProfileId, enemyId, now, out threatPosition, out reason);
    }

    private static bool TryResolveExactQualifiedThreatPose(
        string ownerProfileId,
        string expectedThreatId,
        DateTimeOffset now,
        out Vector3 threatPosition,
        out string reason)
    {
        threatPosition = Vector3.zero;
        string owner = NormalizeKey(ownerProfileId);
        string threat = NormalizeThreat(expectedThreatId);
        DateTimeOffset newest = DateTimeOffset.MinValue;
        bool found = false;
        foreach (VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation observation
            in VanguardCombatAwarenessBridge.GetFreshQualifiedTacticalCoordinationThreats(now))
        {
            if (!string.Equals(NormalizeKey(observation.OwnerProfileId), owner, StringComparison.Ordinal)
                || !string.Equals(NormalizeThreat(observation.TargetProfileId), threat, StringComparison.Ordinal)
                || observation.ExpiresAtUtc <= now
                || observation.ObservedAtUtc < newest)
            {
                continue;
            }
            threatPosition = observation.RuntimePosition;
            newest = observation.ObservedAtUtc;
            found = true;
        }

        reason = found ? "qualified_awareness_runtime_pose" : "qualified_awareness_runtime_pose_missing";
        return found;
    }

    private static bool IsForwardAssaultMovementKind(string? movementKind)
    {
        string kind = NormalizeKey(movementKind);
        return kind is "altdevelop" or "directcommit" or "flanksupport" or "boundadvance" or "interiordevelop" or "assaultrelay";
    }

    private static bool IsOrdinaryScavRole(string roleKey)
        => roleKey is "assault" or "marksman" or "cursedassault" or "assaultgroup" or "crazyassaultevent";

    private static string NormalizeThreat(string? value)
    {
        string token = NormalizeKey(value);
        return token is "none" or "null" or "unknown" ? string.Empty : token;
    }

    private static string NormalizeKey(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant().Replace("_", string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty);

    private static Vector3 Flatten(Vector3 value) { value.y = 0f; return value; }
    private static float HorizontalDistance(Vector3 a, Vector3 b) => Vector3.Distance(Flatten(a), Flatten(b));
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
    private static string Bool(bool value) => value ? "true" : "false";
}
#endif
