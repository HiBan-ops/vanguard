#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.TacticalAuthoring;

// Responsibility: Solves candidate squad placement points around the owner while respecting formation lanes, local geometry, pathability and anti-stacking constraints.
// Flow: Given owner/operator positions and tactical environment evidence, the solver combines validated authored PoseSlot candidates where useful with generated fallback anchors, scores/rejects blocked or overlapping positions and returns the best bounded placement for a movement plan.
// Authority boundary: Placement computation is advisory; path execution and movement authority remain with the movement executors and current lease owner.
// Invariant: Returned points must be reachable/safe enough for the current context, operators should not collapse onto one point, and failure yields no placement rather than an unsafe fabricated one.
namespace Vanguard.Client.Runtime.Movement;

internal readonly struct VanguardTacticalPlacementPlan
{
    public VanguardTacticalPlacementPlan(
        bool valid,
        Vector3 anchor,
        string desiredSector,
        string environmentKind,
        string placementMode,
        string reason,
        string pathSummary,
        float ownerPathDistance,
        float botPathDistance,
        float score)
    {
        Valid = valid;
        Anchor = anchor;
        DesiredSector = desiredSector;
        EnvironmentKind = environmentKind;
        PlacementMode = placementMode;
        Reason = reason;
        PathSummary = pathSummary;
        OwnerPathDistance = ownerPathDistance;
        BotPathDistance = botPathDistance;
        Score = score;
    }

    public static VanguardTacticalPlacementPlan Invalid(string reason) => new(false, Vector3.zero, "none", "unknown", "none", reason, "none", 0f, 0f, 0f);

    public bool Valid { get; }
    public Vector3 Anchor { get; }
    public string DesiredSector { get; }
    public string EnvironmentKind { get; }
    public string PlacementMode { get; }
    public string Reason { get; }
    public string PathSummary { get; }
    public float OwnerPathDistance { get; }
    public float BotPathDistance { get; }
    public float Score { get; }

    public string Summary => "sector=" + Safe(DesiredSector)
        + ";env=" + Safe(EnvironmentKind)
        + ";mode=" + Safe(PlacementMode)
        + ";score=" + Score.ToString("0.00", CultureInfo.InvariantCulture)
        + ";ownerPath=" + OwnerPathDistance.ToString("0.00", CultureInfo.InvariantCulture)
        + ";botPath=" + BotPathDistance.ToString("0.00", CultureInfo.InvariantCulture)
        + ";reason=" + Safe(Reason)
        + ";path=" + Safe(PathSummary);

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

internal static class VanguardTacticalPlacementSolver
{
    public const string StatusTag = "VANGUARD_TACTICAL_PLACEMENT_SOLVER_OK";
    public const string TacticalTuningStatusTag = "VANGUARD_TACTICAL_TUNING_OK";

    public static bool TryResolve(OperatorDecisionSnapshot snapshot, Vector3 botPosition, DateTimeOffset now, out VanguardTacticalPlacementPlan plan)
    {
        plan = VanguardTacticalPlacementPlan.Invalid("not_evaluated");
        if (snapshot == null || !snapshot.Alive)
        {
            plan = VanguardTacticalPlacementPlan.Invalid("snapshot_dead_or_missing");
            return false;
        }

        if (!snapshot.SquadCohesion.OwnerPosition.HasValue || !snapshot.SquadCohesion.OwnerForward.HasValue)
        {
            plan = VanguardTacticalPlacementPlan.Invalid("owner_anchor_missing");
            return false;
        }

        Vector3 owner = snapshot.SquadCohesion.OwnerPosition.Value;
        Vector3 forward = Flatten(snapshot.SquadCohesion.OwnerForward.Value);
        if (forward.sqrMagnitude <= 0.001f)
        {
            forward = Vector3.forward;
        }

        forward.Normalize();
        Vector3 right = new(forward.z, 0f, -forward.x);
        var candidates = BuildCandidates(snapshot, owner, forward, right);
        CandidateScore best = CandidateScore.Invalid("no_candidate");
        foreach (var candidate in candidates)
        {
            if (!TryScoreCandidate(snapshot, owner, botPosition, candidate, out var scored))
            {
                if (scored.Score > best.Score)
                {
                    best = scored;
                }

                continue;
            }

            if (!best.Valid || scored.Score > best.Score)
            {
                best = scored;
            }
        }

        if (!best.Valid)
        {
            plan = VanguardTacticalPlacementPlan.Invalid(best.Reason);
            return false;
        }

        plan = new VanguardTacticalPlacementPlan(
            true,
            best.Anchor,
            best.Sector,
            snapshot.SquadCohesion.TacticalEnvironmentKind,
            snapshot.SquadCohesion.TacticalPlacementMode,
            best.Reason,
            best.PathSummary,
            best.OwnerPathDistance,
            best.BotPathDistance,
            best.Score);
        return true;
    }

    private static IEnumerable<CandidateAnchor> BuildCandidates(OperatorDecisionSnapshot snapshot, Vector3 owner, Vector3 forward, Vector3 right)
    {
        string env = snapshot.SquadCohesion.TacticalEnvironmentKind;
        string current = snapshot.SquadCohesion.Sector;
        if (string.Equals(env, "corridor", StringComparison.OrdinalIgnoreCase))
        {
            yield return Candidate(owner - forward * 7.0f + right * 1.2f, "rear_stagger", "corridor_rear_stagger");
            yield return Candidate(owner - forward * 9.5f - right * 1.2f, "rear_guard", "corridor_rear_guard");
            yield return Candidate(owner + forward * 6.5f + right * 1.0f, "front_stagger", "corridor_front_stagger");
            yield return Candidate(owner + forward * 9.0f - right * 1.0f, "front_stagger", "corridor_front_stagger_far");
            yield break;
        }

        if (string.Equals(env, "urban_wraparound_risk", StringComparison.OrdinalIgnoreCase))
        {
            yield return Candidate(owner - forward * 6.5f, "close_support", "same_volume_rear_close");
            yield return Candidate(owner + forward * 5.5f, "close_support", "same_volume_front_close");
            yield return Candidate(owner - forward * 8.0f + right * 1.5f, "rear_stagger", "same_volume_rear_stagger");
            yield break;
        }

        if (string.Equals(env, "room_or_adjacent_room", StringComparison.OrdinalIgnoreCase))
        {
            yield return Candidate(owner - forward * 6.0f + right * 3.0f, "rear_right", "room_corner_rear_right");
            yield return Candidate(owner - forward * 6.0f - right * 3.0f, "rear_left", "room_corner_rear_left");
            yield return Candidate(owner + forward * 5.5f + right * 3.0f, "front_right", "room_front_right");
            yield return Candidate(owner + forward * 5.5f - right * 3.0f, "front_left", "room_front_left");
            yield return Candidate(owner - forward * 4.5f, "close_support", "room_close_support");
            yield break;
        }

        if (string.Equals(env, "outdoor_open", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in OutdoorCandidates(snapshot, owner, forward, right, current))
            {
                yield return candidate;
            }

            yield break;
        }

        yield return Candidate(owner - forward * 7.0f + right * 2.0f, "rear_right", "compressed_rear_right");
        yield return Candidate(owner - forward * 7.0f - right * 2.0f, "rear_left", "compressed_rear_left");
        yield return Candidate(owner + forward * 6.0f + right * 2.0f, "front_right", "compressed_front_right");
        yield return Candidate(owner + forward * 6.0f - right * 2.0f, "front_left", "compressed_front_left");
        yield return Candidate(owner - forward * 5.0f, "close_support", "compressed_close_support");
    }

    private static IEnumerable<CandidateAnchor> OutdoorCandidates(OperatorDecisionSnapshot snapshot, Vector3 owner, Vector3 forward, Vector3 right, string current)
    {
        bool currentLeft = current.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0;
        bool currentRight = current.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0;
        if (snapshot.SquadCohesion.SectorDuplicate || !snapshot.SquadCohesion.UsefulPosition)
        {
            if (currentLeft)
            {
                yield return Candidate(owner + right * 14.0f + forward * 2.0f, "right_flank", "outdoor_balance_right_flank");
            }
            else if (currentRight)
            {
                yield return Candidate(owner - right * 14.0f + forward * 2.0f, "left_flank", "outdoor_balance_left_flank");
            }
        }

        yield return Candidate(owner - right * 12.0f + forward * 3.0f, "left_flank", "outdoor_left_flank");
        yield return Candidate(owner + right * 12.0f + forward * 3.0f, "right_flank", "outdoor_right_flank");
        yield return Candidate(owner - forward * 12.0f + right * 4.0f, "rear_right", "outdoor_rear_right");
        yield return Candidate(owner - forward * 12.0f - right * 4.0f, "rear_left", "outdoor_rear_left");
        yield return Candidate(owner + forward * 10.0f - right * 4.0f, "front_left", "outdoor_front_left");
        yield return Candidate(owner + forward * 10.0f + right * 4.0f, "front_right", "outdoor_front_right");
    }

    private static bool TryScoreCandidate(OperatorDecisionSnapshot snapshot, Vector3 owner, Vector3 botPosition, CandidateAnchor candidate, out CandidateScore score)
    {
        var ownerPolicy = VanguardMovementAuthorityDoctrine.ForOwner(snapshot.OwnerProfileId);
        score = CandidateScore.Invalid(candidate.Reason + ":not_scored");
        if (IsWideLateral(candidate.Sector) && !snapshot.SquadCohesion.WideLateralAllowed && !snapshot.SquadCohesion.AdjacentRoomAllowed)
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_lateral_not_open");
            return false;
        }

        if (snapshot.SquadCohesion.CorridorLike && IsWideLateral(candidate.Sector))
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_wide_lateral_corridor");
            return false;
        }

        if (!TrySample(candidate.RawAnchor, 2.5f, out var sampled))
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_navmesh_sample_failed");
            return false;
        }

        float ownerDirect = HorizontalDistance(owner, sampled);
        if (ownerDirect > Math.Max(8f, ownerPolicy.TacticalBubbleMeters - 5f))
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_anchor_outside_bubble");
            return false;
        }

        float botDirect = HorizontalDistance(botPosition, sampled);
        if (botDirect < ownerPolicy.TacticalRepositionMinDeltaMeters)
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_delta_too_small_" + botDirect.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (!TryPath(owner, sampled, out var ownerPathDistance, out var ownerCorners, out var ownerPathStatus))
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_owner_path_" + ownerPathStatus);
            return false;
        }

        if (!TryPath(botPosition, sampled, out var botPathDistance, out var botCorners, out var botPathStatus))
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_bot_path_" + botPathStatus);
            return false;
        }

        float ownerRatio = SafeRatio(ownerPathDistance, ownerDirect);
        string env = snapshot.SquadCohesion.TacticalEnvironmentKind;
        float maxRatio = MaxRatio(env);
        float maxOwnerPath = MaxOwnerPath(env);
        float maxBotPath = MaxBotPath(env);
        int maxCorners = MaxCorners(env);
        if (ownerRatio > maxRatio)
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_detour_ratio_" + ownerRatio.ToString("0.00", CultureInfo.InvariantCulture));
            return false;
        }

        if (ownerPathDistance > maxOwnerPath)
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_owner_path_too_long_" + ownerPathDistance.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (botPathDistance > maxBotPath)
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_bot_path_too_long_" + botPathDistance.ToString("0.0", CultureInfo.InvariantCulture) + "_limit_" + maxBotPath.ToString("0.0", CultureInfo.InvariantCulture));
            return false;
        }

        if (botCorners > maxCorners + 2)
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_bot_corners_" + botCorners.ToString(CultureInfo.InvariantCulture));
            return false;
        }

        if (ownerCorners > maxCorners)
        {
            score = CandidateScore.Invalid(candidate.Reason + ":reject_owner_corners_" + ownerCorners.ToString(CultureInfo.InvariantCulture));
            return false;
        }

        float value = 100f;
        value -= ownerPathDistance * 1.2f;
        value -= Math.Max(0f, ownerRatio - 1f) * 35f;
        value -= ownerCorners * 1.5f;
        value -= botPathDistance * 0.60f;
        if (snapshot.SquadCohesion.SectorDuplicate)
        {
            value += 8f;
        }

        if (!snapshot.SquadCohesion.UsefulPosition)
        {
            value += 6f;
        }

        string pathSummary = "ownerDirect=" + ownerDirect.ToString("0.0", CultureInfo.InvariantCulture)
            + ";ownerPath=" + ownerPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";ownerRatio=" + ownerRatio.ToString("0.00", CultureInfo.InvariantCulture)
            + ";ownerCorners=" + ownerCorners.ToString(CultureInfo.InvariantCulture)
            + ";botDirect=" + botDirect.ToString("0.0", CultureInfo.InvariantCulture)
            + ";botPath=" + botPathDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";botCorners=" + botCorners.ToString(CultureInfo.InvariantCulture)
            + ";maxBotPath=" + maxBotPath.ToString("0.0", CultureInfo.InvariantCulture);
        score = new CandidateScore(true, sampled, candidate.Sector, candidate.Reason + ":accepted", pathSummary, ownerPathDistance, botPathDistance, value);
        return true;
    }

    private static float MaxRatio(string env)
    {
        if (string.Equals(env, "corridor", StringComparison.OrdinalIgnoreCase)) return 1.35f;
        if (string.Equals(env, "urban_wraparound_risk", StringComparison.OrdinalIgnoreCase)) return 1.25f;
        if (string.Equals(env, "room_or_adjacent_room", StringComparison.OrdinalIgnoreCase)) return 1.55f;
        if (string.Equals(env, "outdoor_open", StringComparison.OrdinalIgnoreCase)) return 1.85f;
        return 1.60f;
    }

    private static float MaxOwnerPath(string env)
    {
        if (string.Equals(env, "corridor", StringComparison.OrdinalIgnoreCase)) return 14f;
        if (string.Equals(env, "urban_wraparound_risk", StringComparison.OrdinalIgnoreCase)) return 12f;
        if (string.Equals(env, "room_or_adjacent_room", StringComparison.OrdinalIgnoreCase)) return 16f;
        if (string.Equals(env, "outdoor_open", StringComparison.OrdinalIgnoreCase)) return 32f;
        return 20f;
    }

    private static float MaxBotPath(string env)
    {
        if (string.Equals(env, "corridor", StringComparison.OrdinalIgnoreCase)) return 14f;
        if (string.Equals(env, "urban_wraparound_risk", StringComparison.OrdinalIgnoreCase)) return 14f;
        if (string.Equals(env, "room_or_adjacent_room", StringComparison.OrdinalIgnoreCase)) return 16f;
        if (string.Equals(env, "outdoor_open", StringComparison.OrdinalIgnoreCase)) return 28f;
        return 22f;
    }

    private static int MaxCorners(string env)
    {
        if (string.Equals(env, "corridor", StringComparison.OrdinalIgnoreCase)) return 4;
        if (string.Equals(env, "urban_wraparound_risk", StringComparison.OrdinalIgnoreCase)) return 3;
        if (string.Equals(env, "room_or_adjacent_room", StringComparison.OrdinalIgnoreCase)) return 5;
        if (string.Equals(env, "outdoor_open", StringComparison.OrdinalIgnoreCase)) return 8;
        return 5;
    }

    private static bool IsWideLateral(string sector)
    {
        return string.Equals(sector, "left_flank", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sector, "right_flank", StringComparison.OrdinalIgnoreCase);
    }

    private static CandidateAnchor Candidate(Vector3 raw, string sector, string reason) => new(raw, sector, reason);

    /// <summary>Canonical runtime NavMesh projection shared with transient Tactical Authoring headless validation.</summary>
    public static bool TryProjectRuntimeAnchor(Vector3 raw, float radius, out Vector3 sampled)
    {
        return TrySample(raw, radius, out sampled);
    }

    /// <summary>Canonical complete-path probe shared with transient Tactical Authoring headless validation.</summary>
    public static bool TryCalculateRuntimePath(Vector3 start, Vector3 end, out float distance, out int corners, out string status)
    {
        return TryPath(start, end, out distance, out corners, out status);
    }

    /// <summary>
    /// Resolves a bounded combat overwatch anchor. The solver deliberately prefers lateral/backward
    /// displacement that preserves or increases standoff while exposing a current line to the
    /// already-qualified threat. It never decides target/aim/fire and never emits movement itself.
    /// </summary>
    public static bool TryResolveCombatOverwatch(
        OperatorDecisionSnapshot snapshot,
        Vector3 botPosition,
        Vector3 threatPosition,
        Vector3 threatLookDirection,
        Vector3? secondaryThreatPosition,
        float minimumStandoffMeters,
        out VanguardTacticalPlacementPlan plan)
    {
        plan = VanguardTacticalPlacementPlan.Invalid("combat_overwatch_not_evaluated");
        if (snapshot == null || !snapshot.Alive || !snapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            plan = VanguardTacticalPlacementPlan.Invalid("combat_overwatch_owner_or_snapshot_missing");
            return false;
        }

        var ownerPolicy = VanguardMovementAuthorityDoctrine.ForOwner(snapshot.OwnerProfileId);
        Vector3 owner = snapshot.SquadCohesion.OwnerPosition.Value;
        Vector3 threatToBot = Flatten(botPosition - threatPosition);
        if (threatToBot.sqrMagnitude <= 0.01f)
        {
            threatToBot = Flatten(owner - threatPosition);
        }
        if (threatToBot.sqrMagnitude <= 0.01f)
        {
            plan = VanguardTacticalPlacementPlan.Invalid("combat_overwatch_direction_unresolved");
            return false;
        }
        threatToBot.Normalize();
        Vector3 lateral = new(threatToBot.z, 0f, -threatToBot.x);
        float currentStandoff = HorizontalDistance(botPosition, threatPosition);
        Vector3 normalizedThreatLook = Flatten(threatLookDirection);
        bool threatLookKnown = normalizedThreatLook.sqrMagnitude > 0.01f;
        if (threatLookKnown)
        {
            normalizedThreatLook.Normalize();
        }

        // Opportunity-first fan: prefer a short lateral/rear correction that produces a real firing lane.
        // Wider anchors remain available when the immediate neighborhood is blocked, but LOS and a valid
        // runtime path stay mandatory. This deliberately avoids requiring one exact "perfect" sniper spot.
        var rawCandidates = new[]
        {
            new CandidateAnchor(botPosition + lateral * 4f + threatToBot * 1f, "overwatch_left", "combat_overwatch_left_micro"),
            new CandidateAnchor(botPosition - lateral * 4f + threatToBot * 1f, "overwatch_right", "combat_overwatch_right_micro"),
            new CandidateAnchor(botPosition + threatToBot * 3f, "overwatch_rear", "combat_overwatch_standoff_micro"),
            new CandidateAnchor(botPosition + lateral * 8f + threatToBot * 2f, "overwatch_left", "combat_overwatch_left_near"),
            new CandidateAnchor(botPosition - lateral * 8f + threatToBot * 2f, "overwatch_right", "combat_overwatch_right_near"),
            new CandidateAnchor(botPosition + threatToBot * 6f, "overwatch_rear", "combat_overwatch_standoff_near"),
            new CandidateAnchor(botPosition + lateral * 12f + threatToBot * 4f, "overwatch_left", "combat_overwatch_left_wide"),
            new CandidateAnchor(botPosition - lateral * 12f + threatToBot * 4f, "overwatch_right", "combat_overwatch_right_wide"),
            new CandidateAnchor(owner + lateral * 9f + threatToBot * 4f, "overwatch_left", "combat_overwatch_owner_left"),
            new CandidateAnchor(owner - lateral * 9f + threatToBot * 4f, "overwatch_right", "combat_overwatch_owner_right"),
            new CandidateAnchor(botPosition + threatToBot * 10f, "overwatch_rear", "combat_overwatch_standoff_wide"),
        };

        CandidateScore best = CandidateScore.Invalid("combat_overwatch_no_candidate");
        foreach (CandidateAnchor candidate in rawCandidates)
        {
            if (!TrySample(candidate.RawAnchor, 3.0f, out Vector3 sampled))
            {
                continue;
            }

            float standoff = HorizontalDistance(sampled, threatPosition);
            if (standoff < minimumStandoffMeters || standoff + 8f < currentStandoff)
            {
                continue;
            }

            float ownerDistance = HorizontalDistance(sampled, owner);
            if (ownerDistance > VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters - 2f)
            {
                continue;
            }

            float directMove = HorizontalDistance(botPosition, sampled);
            if (directMove < ownerPolicy.TacticalRepositionMinDeltaMeters || directMove > 30f)
            {
                continue;
            }

            if (!TryPath(botPosition, sampled, out float pathDistance, out int corners, out string pathStatus)
                || pathDistance > 40f || corners > 12)
            {
                continue;
            }

            Vector3 lineStart = sampled + Vector3.up * 1.45f;
            Vector3 lineEnd = threatPosition + Vector3.up * 1.15f;
            bool clearFireLane = !Physics.Linecast(lineStart, lineEnd, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (!clearFireLane)
            {
                continue;
            }

            float lateralGain = Math.Abs(Vector3.Dot(Flatten(sampled - botPosition), lateral));
            Vector3 threatToCandidate = Flatten(sampled - threatPosition);
            float facingDot = 0f;
            if (threatLookKnown && threatToCandidate.sqrMagnitude > 0.01f)
            {
                threatToCandidate.Normalize();
                facingDot = Mathf.Clamp(Vector3.Dot(normalizedThreatLook, threatToCandidate), -1f, 1f);
            }
            float value = 120f;
            value += Math.Min(18f, Math.Max(0f, standoff - minimumStandoffMeters) * 0.35f);
            value += Math.Min(12f, lateralGain * 0.75f);
            value -= pathDistance * 1.35f;
            value -= ownerDistance * 0.25f;
            value -= corners * 1.5f;
            // Prefer a firing line the target is not already facing. This is a positive preference,
            // not a proof of safety: local SAIN survival/cover authority remains sovereign.
            if (threatLookKnown)
            {
                value -= Math.Max(0f, facingDot) * 22f;
                value += Math.Max(0f, -facingDot) * 8f;
            }
            string summary = pathStatus
                + ";standoff=" + standoff.ToString("0.0", CultureInfo.InvariantCulture)
                + ";currentStandoff=" + currentStandoff.ToString("0.0", CultureInfo.InvariantCulture)
                + ";ownerDistance=" + ownerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + ";targetFacingDot=" + facingDot.ToString("0.00", CultureInfo.InvariantCulture)
                + ";targetLookKnown=" + Bool(threatLookKnown)
                + ";fireLane=true";
            var baseScored = new CandidateScore(true, sampled, candidate.Sector, candidate.Reason + ":accepted", summary, ownerDistance, pathDistance, value);
            if (!TryApplySecondaryThreatOverwatchAdjustment(
                    baseScored,
                    botPosition,
                    secondaryThreatPosition,
                    out CandidateScore scored))
            {
                continue;
            }
            if (!best.Valid || scored.Score > best.Score)
            {
                best = scored;
            }
        }

        if (TryResolveAuthoredOverwatch(
                snapshot,
                botPosition,
                owner,
                threatPosition,
                normalizedThreatLook,
                threatLookKnown,
                minimumStandoffMeters,
                currentStandoff,
                out CandidateScore authoredOverwatch)
            && TryApplySecondaryThreatOverwatchAdjustment(
                authoredOverwatch,
                botPosition,
                secondaryThreatPosition,
                out CandidateScore adjustedAuthoredOverwatch))
        {
            authoredOverwatch = adjustedAuthoredOverwatch;
            if (!best.Valid || authoredOverwatch.Score > best.Score)
            {
                best = authoredOverwatch;
            }
        }

        if (!best.Valid)
        {
            plan = VanguardTacticalPlacementPlan.Invalid(best.Reason);
            return false;
        }

        plan = new VanguardTacticalPlacementPlan(
            true,
            best.Anchor,
            best.Sector,
            snapshot.SquadCohesion.TacticalEnvironmentKind,
            "combat_overwatch",
            best.Reason,
            best.PathSummary,
            best.OwnerPathDistance,
            best.BotPathDistance,
            best.Score);
        return true;
    }

    private static bool TryApplySecondaryThreatOverwatchAdjustment(
        CandidateScore source,
        Vector3 botPosition,
        Vector3? secondaryThreatPosition,
        out CandidateScore adjusted)
    {
        adjusted = source;
        if (!source.Valid || !secondaryThreatPosition.HasValue)
        {
            return source.Valid;
        }

        Vector3 secondary = secondaryThreatPosition.Value;
        float before = HorizontalDistance(botPosition, secondary);
        float after = HorizontalDistance(source.Anchor, secondary);
        const float secondaryDangerCloseMeters = 15f;

        // Multi-threat overwatch must not improve one firing lane by stepping materially closer into
        // another direct contact. This is a placement-side safety preference only; SAIN still owns
        // cover, target, aim and fire. A candidate that is already danger-close may still move away.
        if (after < secondaryDangerCloseMeters && after + 1.5f < before)
        {
            adjusted = CandidateScore.Invalid(source.Reason + ":reject_secondary_threat_danger_close_worsened");
            return false;
        }

        Vector3 secondaryLineStart = source.Anchor + Vector3.up * 1.45f;
        Vector3 secondaryLineEnd = secondary + Vector3.up * 1.15f;
        bool secondaryLineOpen = !Physics.Linecast(
            secondaryLineStart,
            secondaryLineEnd,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
        float standoffDelta = after - before;
        float value = source.Score;
        if (standoffDelta < 0f)
        {
            value -= Math.Min(18f, -standoffDelta * 2.0f);
        }
        else
        {
            value += Math.Min(6f, standoffDelta * 0.5f);
        }

        // An open secondary line is not automatically unsafe, but under already-qualified multi-threat
        // pressure it is less desirable than an equally useful anchor that does not expose another line.
        if (secondaryLineOpen)
        {
            value -= 12f;
        }
        else
        {
            value += 6f;
        }
        string summary = source.PathSummary
            + ";secondaryThreatKnown=true"
            + ";secondaryBefore=" + before.ToString("0.0", CultureInfo.InvariantCulture)
            + ";secondaryAfter=" + after.ToString("0.0", CultureInfo.InvariantCulture)
            + ";secondaryStandoffDelta=" + standoffDelta.ToString("0.0", CultureInfo.InvariantCulture)
            + ";secondaryLineOpen=" + Bool(secondaryLineOpen)
            + ";multiThreatAdjustment=true";
        adjusted = new CandidateScore(
            true,
            source.Anchor,
            source.Sector,
            source.Reason + ":secondary_threat_adjusted",
            summary,
            source.OwnerPathDistance,
            source.BotPathDistance,
            value);
        return true;
    }

    /// <summary>
    /// Resolves a support point near an isolated squadmate while staying on the friendly side of
    /// the threat. This creates a visible rescue/support opportunity without making Vanguard own fire.
    /// </summary>
    public static bool TryResolveBuddySupport(
        OperatorDecisionSnapshot helperSnapshot,
        Vector3 helperPosition,
        Vector3 distressedPosition,
        Vector3 threatPosition,
        out VanguardTacticalPlacementPlan plan)
    {
        plan = VanguardTacticalPlacementPlan.Invalid("buddy_support_not_evaluated");
        if (helperSnapshot == null || !helperSnapshot.Alive || !helperSnapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            return false;
        }

        var ownerPolicy = VanguardMovementAuthorityDoctrine.ForOwner(helperSnapshot.OwnerProfileId);
        Vector3 owner = helperSnapshot.SquadCohesion.OwnerPosition.Value;
        Vector3 awayFromThreat = Flatten(distressedPosition - threatPosition);
        if (awayFromThreat.sqrMagnitude <= 0.01f)
        {
            awayFromThreat = Flatten(owner - threatPosition);
        }
        if (awayFromThreat.sqrMagnitude <= 0.01f)
        {
            return false;
        }
        awayFromThreat.Normalize();
        Vector3 lateral = new(awayFromThreat.z, 0f, -awayFromThreat.x);

        var rawCandidates = new[]
        {
            new CandidateAnchor(distressedPosition + awayFromThreat * 6f + lateral * 4f, "buddy_support_left", "buddy_support_left"),
            new CandidateAnchor(distressedPosition + awayFromThreat * 6f - lateral * 4f, "buddy_support_right", "buddy_support_right"),
            new CandidateAnchor(distressedPosition + awayFromThreat * 8f, "buddy_support_rear", "buddy_support_rear"),
            new CandidateAnchor(Vector3.Lerp(owner, distressedPosition, 0.72f) + lateral * 3f, "buddy_support_left", "buddy_support_owner_bridge_left"),
            new CandidateAnchor(Vector3.Lerp(owner, distressedPosition, 0.72f) - lateral * 3f, "buddy_support_right", "buddy_support_owner_bridge_right"),
        };

        CandidateScore best = CandidateScore.Invalid("buddy_support_no_candidate");
        foreach (CandidateAnchor candidate in rawCandidates)
        {
            if (!TrySample(candidate.RawAnchor, 3.0f, out Vector3 sampled))
            {
                continue;
            }
            float threatDistance = HorizontalDistance(sampled, threatPosition);
            if (threatDistance < 16f)
            {
                continue;
            }
            float distressedDistance = HorizontalDistance(sampled, distressedPosition);
            if (distressedDistance > 12f)
            {
                continue;
            }
            float ownerDistance = HorizontalDistance(sampled, owner);
            if (ownerDistance > VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters)
            {
                continue;
            }
            float directMove = HorizontalDistance(helperPosition, sampled);
            if (directMove < ownerPolicy.TacticalRepositionMinDeltaMeters || directMove > 30f)
            {
                continue;
            }
            if (!TryPath(helperPosition, sampled, out float pathDistance, out int corners, out string pathStatus)
                || pathDistance > 38f || corners > 11)
            {
                continue;
            }

            float value = 115f;
            value -= distressedDistance * 2.0f;
            value -= pathDistance * 1.15f;
            value -= ownerDistance * 0.30f;
            value -= corners;
            string summary = pathStatus
                + ";distressedDistance=" + distressedDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + ";threatDistance=" + threatDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + ";ownerDistance=" + ownerDistance.ToString("0.0", CultureInfo.InvariantCulture);
            var scored = new CandidateScore(true, sampled, candidate.Sector, candidate.Reason + ":accepted", summary, ownerDistance, pathDistance, value);
            if (!best.Valid || scored.Score > best.Score)
            {
                best = scored;
            }
        }

        if (TryResolveAuthoredBuddySupport(
                helperSnapshot,
                helperPosition,
                owner,
                distressedPosition,
                threatPosition,
                out CandidateScore authoredSupport)
            && (!best.Valid || authoredSupport.Score > best.Score))
        {
            best = authoredSupport;
        }

        if (!best.Valid)
        {
            return false;
        }
        plan = new VanguardTacticalPlacementPlan(
            true,
            best.Anchor,
            best.Sector,
            helperSnapshot.SquadCohesion.TacticalEnvironmentKind,
            "buddy_support",
            best.Reason,
            best.PathSummary,
            best.OwnerPathDistance,
            best.BotPathDistance,
            best.Score);
        return true;
    }

    private static bool TryResolveAuthoredOverwatch(
        OperatorDecisionSnapshot snapshot,
        Vector3 botPosition,
        Vector3 owner,
        Vector3 threatPosition,
        Vector3 normalizedThreatLook,
        bool threatLookKnown,
        float minimumStandoffMeters,
        float currentStandoff,
        out CandidateScore best)
    {
        best = CandidateScore.Invalid("authored_overwatch_unavailable");
        if (!VanguardTacticalCoordinationRuntimeReadService.TryGetSnapshot(out var topology))
        {
            return false;
        }

        var ownerPolicy = VanguardMovementAuthorityDoctrine.ForOwner(snapshot.OwnerProfileId);
        foreach (VanguardTacticalCoordinationReadAnchor anchor in topology.TacticalAnchors)
        {
            if (!anchor.Enabled || anchor.ValidationState == VanguardTacticalAuthoringValidationState.Invalid)
            {
                continue;
            }

            foreach (VanguardTacticalCoordinationReadPoseSlot slot in anchor.PoseSlots)
            {
                if (!slot.Enabled || slot.ValidationState == VanguardTacticalAuthoringValidationState.Invalid)
                {
                    continue;
                }

                Vector3 raw = slot.NavMeshProjectedPosition ?? slot.Position;
                if (!TrySample(raw, 2.25f, out Vector3 sampled))
                {
                    continue;
                }

                float standoff = HorizontalDistance(sampled, threatPosition);
                if (standoff < minimumStandoffMeters || standoff + 8f < currentStandoff)
                {
                    continue;
                }

                float ownerDistance = HorizontalDistance(sampled, owner);
                if (ownerDistance > VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters - 2f)
                {
                    continue;
                }

                float directMove = HorizontalDistance(botPosition, sampled);
                if (directMove < ownerPolicy.TacticalRepositionMinDeltaMeters || directMove > 30f)
                {
                    continue;
                }

                if (!TryPath(botPosition, sampled, out float pathDistance, out int corners, out string pathStatus)
                    || pathDistance > 40f || corners > 12)
                {
                    continue;
                }

                Vector3 lineStart = sampled + Vector3.up * 1.45f;
                Vector3 lineEnd = threatPosition + Vector3.up * 1.15f;
                bool clearFireLane = !Physics.Linecast(lineStart, lineEnd, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                if (!clearFireLane)
                {
                    continue;
                }

                Vector3 viewDirection = Flatten(slot.ViewDirection);
                Vector3 toThreat = Flatten(threatPosition - sampled);
                float authoredViewThreatDot = 0f;
                bool authoredViewKnown = viewDirection.sqrMagnitude > 0.01f && toThreat.sqrMagnitude > 0.01f;
                if (authoredViewKnown)
                {
                    viewDirection.Normalize();
                    toThreat.Normalize();
                    authoredViewThreatDot = Mathf.Clamp(Vector3.Dot(viewDirection, toThreat), -1f, 1f);
                }

                Vector3 threatToCandidate = Flatten(sampled - threatPosition);
                float facingDot = 0f;
                if (threatLookKnown && threatToCandidate.sqrMagnitude > 0.01f)
                {
                    threatToCandidate.Normalize();
                    facingDot = Mathf.Clamp(Vector3.Dot(normalizedThreatLook, threatToCandidate), -1f, 1f);
                }

                float value = 126f;
                value += Math.Min(18f, Math.Max(0f, standoff - minimumStandoffMeters) * 0.35f);
                value -= pathDistance * 1.35f;
                value -= ownerDistance * 0.25f;
                value -= corners * 1.5f;
                if (threatLookKnown)
                {
                    value -= Math.Max(0f, facingDot) * 22f;
                    value += Math.Max(0f, -facingDot) * 8f;
                }
                if (authoredViewKnown)
                {
                    value += authoredViewThreatDot * 6f;
                }

                string summary = pathStatus
                    + ";source=authored_pose_slot"
                    + ";anchor=" + anchor.AnchorId
                    + ";poseSlot=" + slot.PoseSlotId
                    + ";topologyRevision=" + topology.Revision.ToString(CultureInfo.InvariantCulture)
                    + ";standoff=" + standoff.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";currentStandoff=" + currentStandoff.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";ownerDistance=" + ownerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";targetFacingDot=" + facingDot.ToString("0.00", CultureInfo.InvariantCulture)
                    + ";authoredViewThreatDot=" + authoredViewThreatDot.ToString("0.00", CultureInfo.InvariantCulture)
                    + ";authoredViewKnown=" + Bool(authoredViewKnown)
                    + ";fireLane=true;poseApplied=false;orientationApplied=false";
                var scored = new CandidateScore(
                    true,
                    sampled,
                    "authored_overwatch",
                    "authored_overwatch_pose_slot:accepted",
                    summary,
                    ownerDistance,
                    pathDistance,
                    value);
                if (!best.Valid || scored.Score > best.Score)
                {
                    best = scored;
                }
            }
        }

        return best.Valid;
    }

    private static bool TryResolveAuthoredBuddySupport(
        OperatorDecisionSnapshot snapshot,
        Vector3 botPosition,
        Vector3 owner,
        Vector3 distressedPosition,
        Vector3 threatPosition,
        out CandidateScore best)
    {
        best = CandidateScore.Invalid("authored_buddy_support_unavailable");
        if (!VanguardTacticalCoordinationRuntimeReadService.TryGetSnapshot(out var topology))
        {
            return false;
        }

        var ownerPolicy = VanguardMovementAuthorityDoctrine.ForOwner(snapshot.OwnerProfileId);
        foreach (VanguardTacticalCoordinationReadAnchor anchor in topology.TacticalAnchors)
        {
            if (!anchor.Enabled || anchor.ValidationState == VanguardTacticalAuthoringValidationState.Invalid)
            {
                continue;
            }

            foreach (VanguardTacticalCoordinationReadPoseSlot slot in anchor.PoseSlots)
            {
                if (!slot.Enabled || slot.ValidationState == VanguardTacticalAuthoringValidationState.Invalid)
                {
                    continue;
                }

                Vector3 raw = slot.NavMeshProjectedPosition ?? slot.Position;
                if (!TrySample(raw, 2.25f, out Vector3 sampled))
                {
                    continue;
                }

                float threatDistance = HorizontalDistance(sampled, threatPosition);
                if (threatDistance < 16f)
                {
                    continue;
                }

                float distressedDistance = HorizontalDistance(sampled, distressedPosition);
                if (distressedDistance > 12f)
                {
                    continue;
                }

                float ownerDistance = HorizontalDistance(sampled, owner);
                if (ownerDistance > VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters)
                {
                    continue;
                }

                float directMove = HorizontalDistance(botPosition, sampled);
                if (directMove < ownerPolicy.TacticalRepositionMinDeltaMeters || directMove > 30f)
                {
                    continue;
                }

                if (!TryPath(botPosition, sampled, out float pathDistance, out int corners, out string pathStatus)
                    || pathDistance > 38f || corners > 11)
                {
                    continue;
                }

                Vector3 viewDirection = Flatten(slot.ViewDirection);
                Vector3 toThreat = Flatten(threatPosition - sampled);
                float authoredViewThreatDot = 0f;
                bool authoredViewKnown = viewDirection.sqrMagnitude > 0.01f && toThreat.sqrMagnitude > 0.01f;
                if (authoredViewKnown)
                {
                    viewDirection.Normalize();
                    toThreat.Normalize();
                    authoredViewThreatDot = Mathf.Clamp(Vector3.Dot(viewDirection, toThreat), -1f, 1f);
                }

                float value = 121f;
                value -= distressedDistance * 2.0f;
                value -= pathDistance * 1.15f;
                value -= ownerDistance * 0.30f;
                value -= corners;
                if (authoredViewKnown)
                {
                    value += authoredViewThreatDot * 4f;
                }

                string summary = pathStatus
                    + ";source=authored_pose_slot"
                    + ";anchor=" + anchor.AnchorId
                    + ";poseSlot=" + slot.PoseSlotId
                    + ";topologyRevision=" + topology.Revision.ToString(CultureInfo.InvariantCulture)
                    + ";distressedDistance=" + distressedDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";threatDistance=" + threatDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";ownerDistance=" + ownerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";authoredViewThreatDot=" + authoredViewThreatDot.ToString("0.00", CultureInfo.InvariantCulture)
                    + ";authoredViewKnown=" + Bool(authoredViewKnown)
                    + ";poseApplied=false;orientationApplied=false";
                var scored = new CandidateScore(
                    true,
                    sampled,
                    "authored_buddy_support",
                    "authored_buddy_support_pose_slot:accepted",
                    summary,
                    ownerDistance,
                    pathDistance,
                    value);
                if (!best.Valid || scored.Score > best.Score)
                {
                    best = scored;
                }
            }
        }

        return best.Valid;
    }

    /// <summary>
    /// Resolves one short leapfrog advance for a mover while another fresh squadmate already
    /// represents the covering element. Vanguard only chooses the mover destination; the cover
    /// member is never frozen or commanded to fire and SAIN keeps all local combat authority.
    /// </summary>
    public static bool TryResolveBoundAdvance(
        OperatorDecisionSnapshot moverSnapshot,
        Vector3 moverPosition,
        Vector3 supportPosition,
        Vector3 threatPosition,
        float minimumThreatStandoffMeters,
        float maximumForwardHopMeters,
        out VanguardTacticalPlacementPlan plan)
    {
        plan = VanguardTacticalPlacementPlan.Invalid("bound_advance_not_evaluated");
        if (moverSnapshot == null || !moverSnapshot.Alive || !moverSnapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            return false;
        }
        Vector3 owner = moverSnapshot.SquadCohesion.OwnerPosition.Value;

        Vector3 towardThreat = Flatten(threatPosition - moverPosition);
        float currentThreatDistance = towardThreat.magnitude;
        if (currentThreatDistance <= minimumThreatStandoffMeters + 3.0f || towardThreat.sqrMagnitude <= 0.01f)
        {
            plan = VanguardTacticalPlacementPlan.Invalid("bound_advance_insufficient_safe_depth");
            return false;
        }
        towardThreat.Normalize();
        Vector3 lateral = new(towardThreat.z, 0f, -towardThreat.x);
        float supportSide = Vector3.Dot(Flatten(supportPosition - moverPosition), lateral);
        float lateralSign = supportSide >= 0f ? -1f : 1f;
        float normalizedMaximumForwardHop = Math.Max(5.0f, Math.Min(20.0f, maximumForwardHopMeters));
        float maximumDirectMoveMeters = normalizedMaximumForwardHop + 3.5f;
        float maximumPathMeters = normalizedMaximumForwardHop + 7.0f;
        float maxForward = Math.Min(normalizedMaximumForwardHop, currentThreatDistance - minimumThreatStandoffMeters);
        if (maxForward < 3.0f)
        {
            return false;
        }

        var rawCandidates = new[]
        {
            new CandidateAnchor(moverPosition + towardThreat * maxForward + lateral * lateralSign * 2.5f, "bound_advance_stagger", "bound_advance_stagger"),
            new CandidateAnchor(moverPosition + towardThreat * maxForward, "bound_advance_direct", "bound_advance_direct"),
            new CandidateAnchor(moverPosition + towardThreat * Math.Max(3.0f, maxForward - 1.5f) + lateral * lateralSign * 4.0f, "bound_advance_wide", "bound_advance_wide"),
        };

        CandidateScore best = CandidateScore.Invalid("bound_advance_no_candidate");
        foreach (CandidateAnchor candidate in rawCandidates)
        {
            if (!TrySample(candidate.RawAnchor, 2.75f, out Vector3 sampled))
            {
                continue;
            }
            float sampledThreatDistance = HorizontalDistance(sampled, threatPosition);
            float directMove = HorizontalDistance(moverPosition, sampled);
            float ownerDistance = HorizontalDistance(sampled, owner);
            if (sampledThreatDistance < minimumThreatStandoffMeters
                || ownerDistance > VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters - 2f
                || directMove < 2.5f
                || directMove > maximumDirectMoveMeters)
            {
                continue;
            }
            if (!TryPath(moverPosition, sampled, out float pathDistance, out int corners, out string pathStatus)
                || pathDistance > maximumPathMeters || corners > 7)
            {
                continue;
            }

            float supportSeparation = HorizontalDistance(sampled, supportPosition);
            float forwardGain = Math.Max(0f, currentThreatDistance - sampledThreatDistance);
            float value = 110f + forwardGain * 3.0f - pathDistance * 1.4f - corners * 1.5f - ownerDistance * 0.20f;
            if (supportSeparation < 2.5f) value -= 18f;
            string summary = pathStatus
                + ";threatBefore=" + currentThreatDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + ";threatAfter=" + sampledThreatDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + ";supportSeparation=" + supportSeparation.ToString("0.0", CultureInfo.InvariantCulture)
                + ";ownerDistance=" + ownerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + ";minimumStandoff=" + minimumThreatStandoffMeters.ToString("0.0", CultureInfo.InvariantCulture)
                + ";boundAdvanceDistance=" + normalizedMaximumForwardHop.ToString("0.0", CultureInfo.InvariantCulture)
                + ";maxDirectMove=" + maximumDirectMoveMeters.ToString("0.0", CultureInfo.InvariantCulture)
                + ";maxPath=" + maximumPathMeters.ToString("0.0", CultureInfo.InvariantCulture);
            var scored = new CandidateScore(true, sampled, candidate.Sector, candidate.Reason + ":accepted", summary, ownerDistance, pathDistance, value);
            if (!best.Valid || scored.Score > best.Score)
            {
                best = scored;
            }
        }

        if (!best.Valid)
        {
            return false;
        }
        plan = new VanguardTacticalPlacementPlan(
            true,
            best.Anchor,
            best.Sector,
            moverSnapshot.SquadCohesion.TacticalEnvironmentKind,
            "bound_advance",
            best.Reason,
            best.PathSummary,
            best.OwnerPathDistance,
            best.BotPathDistance,
            best.Score);
        return true;
    }

    /// <summary>
    /// Resolves a short relay position behind an already-moving Direct element. The relay may close
    /// distance to the lead but must remain materially farther from the threat and cannot overtake it.
    /// </summary>
    public static bool TryResolveAssaultRelay(
        OperatorDecisionSnapshot relaySnapshot,
        Vector3 relayPosition,
        Vector3 leadPosition,
        Vector3 threatPosition,
        out VanguardTacticalPlacementPlan plan)
    {
        plan = VanguardTacticalPlacementPlan.Invalid("assault_relay_not_evaluated");
        if (relaySnapshot == null || !relaySnapshot.Alive || !relaySnapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            return false;
        }

        const float desiredTrailMeters = 5.0f;
        const float minimumLeadSeparationMeters = 3.0f;
        const float maximumLeadSeparationMeters = 9.0f;
        const float minimumThreatDepthBehindLeadMeters = 2.5f;
        const float maximumDirectMoveMeters = 12.0f;
        const float maximumPathMeters = 16.0f;

        Vector3 awayFromThreat = Flatten(leadPosition - threatPosition);
        float leadThreatDistance = awayFromThreat.magnitude;
        if (awayFromThreat.sqrMagnitude <= 0.01f)
        {
            return false;
        }
        awayFromThreat.Normalize();
        Vector3 lateral = new(awayFromThreat.z, 0f, -awayFromThreat.x);
        Vector3 owner = relaySnapshot.SquadCohesion.OwnerPosition.Value;

        var rawCandidates = new[]
        {
            new CandidateAnchor(leadPosition + awayFromThreat * desiredTrailMeters, "assault_relay_trail", "assault_relay_trail"),
            new CandidateAnchor(leadPosition + awayFromThreat * desiredTrailMeters + lateral * 2.0f, "assault_relay_left", "assault_relay_left"),
            new CandidateAnchor(leadPosition + awayFromThreat * desiredTrailMeters - lateral * 2.0f, "assault_relay_right", "assault_relay_right"),
        };

        CandidateScore best = CandidateScore.Invalid("assault_relay_no_candidate");
        foreach (CandidateAnchor candidate in rawCandidates)
        {
            if (!TrySample(candidate.RawAnchor, 2.5f, out Vector3 sampled))
            {
                continue;
            }
            float threatDistance = HorizontalDistance(sampled, threatPosition);
            float leadSeparation = HorizontalDistance(sampled, leadPosition);
            float directMove = HorizontalDistance(relayPosition, sampled);
            float ownerDistance = HorizontalDistance(sampled, owner);
            if (threatDistance < leadThreatDistance + minimumThreatDepthBehindLeadMeters
                || leadSeparation < minimumLeadSeparationMeters
                || leadSeparation > maximumLeadSeparationMeters
                || directMove < 2.5f
                || directMove > maximumDirectMoveMeters
                || ownerDistance > VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters)
            {
                continue;
            }
            if (!TryPath(relayPosition, sampled, out float pathDistance, out int corners, out string pathStatus)
                || pathDistance > maximumPathMeters
                || corners > 8)
            {
                continue;
            }

            float approachGain = Math.Max(0f, HorizontalDistance(relayPosition, leadPosition) - leadSeparation);
            float trailError = Math.Abs(leadSeparation - desiredTrailMeters);
            float score = 112f + approachGain * 2.0f - trailError * 4.0f - pathDistance * 1.25f - corners * 1.5f - ownerDistance * 0.15f;
            string summary = pathStatus
                + ";leadThreatDistance=" + leadThreatDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + ";relayThreatDistance=" + threatDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + ";leadSeparation=" + leadSeparation.ToString("0.0", CultureInfo.InvariantCulture)
                + ";desiredTrail=" + desiredTrailMeters.ToString("0.0", CultureInfo.InvariantCulture)
                + ";ownerDistance=" + ownerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + ";cannotOvertakeLead=true";
            var scored = new CandidateScore(true, sampled, candidate.Sector, candidate.Reason + ":accepted", summary, ownerDistance, pathDistance, score);
            if (!best.Valid || scored.Score > best.Score)
            {
                best = scored;
            }
        }

        if (!best.Valid)
        {
            return false;
        }
        plan = new VanguardTacticalPlacementPlan(
            true,
            best.Anchor,
            best.Sector,
            relaySnapshot.SquadCohesion.TacticalEnvironmentKind,
            "assault_relay",
            best.Reason,
            best.PathSummary,
            best.OwnerPathDistance,
            best.BotPathDistance,
            best.Score);
        return true;
    }

    /// <summary>
    /// Resolves a short support position on the approach side of a confirmed breach portal. The
    /// selected destination and its prepared NavMesh path must remain on the pre-portal side.
    /// </summary>
    public static bool TryResolveThresholdSupport(
        OperatorDecisionSnapshot supportSnapshot,
        Vector3 supportPosition,
        Vector3 leadPosition,
        Vector3 projectedPortal,
        Vector3 entryAnchor,
        out VanguardTacticalPlacementPlan plan)
    {
        plan = VanguardTacticalPlacementPlan.Invalid("threshold_support_not_evaluated");
        if (supportSnapshot == null || !supportSnapshot.Alive || !supportSnapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            return false;
        }

        const float desiredUpstreamMeters = 3.0f;
        const float minimumUpstreamMarginMeters = 0.75f;
        const float lateralOffsetMeters = 1.75f;
        const float minimumLeadSeparationMeters = 2.5f;
        const float maximumDirectMoveMeters = 12.0f;
        const float maximumPathMeters = 16.0f;

        Vector3 entryDirection = Flatten(entryAnchor - projectedPortal);
        if (entryDirection.sqrMagnitude <= 0.01f)
        {
            return false;
        }
        entryDirection.Normalize();
        Vector3 lateral = new(entryDirection.z, 0f, -entryDirection.x);
        Vector3 owner = supportSnapshot.SquadCohesion.OwnerPosition.Value;
        float supportSide = Vector3.Dot(Flatten(supportPosition - projectedPortal), entryDirection);
        if (supportSide > -minimumUpstreamMarginMeters)
        {
            plan = VanguardTacticalPlacementPlan.Invalid("threshold_support_not_preportal");
            return false;
        }

        var rawCandidates = new[]
        {
            new CandidateAnchor(projectedPortal - entryDirection * desiredUpstreamMeters, "threshold_support_center", "threshold_support_center"),
            new CandidateAnchor(projectedPortal - entryDirection * desiredUpstreamMeters + lateral * lateralOffsetMeters, "threshold_support_left", "threshold_support_left"),
            new CandidateAnchor(projectedPortal - entryDirection * desiredUpstreamMeters - lateral * lateralOffsetMeters, "threshold_support_right", "threshold_support_right"),
        };

        CandidateScore best = CandidateScore.Invalid("threshold_support_no_candidate");
        foreach (CandidateAnchor candidate in rawCandidates)
        {
            if (!TrySample(candidate.RawAnchor, 2.25f, out Vector3 sampled))
            {
                continue;
            }
            float portalSide = Vector3.Dot(Flatten(sampled - projectedPortal), entryDirection);
            float directMove = HorizontalDistance(supportPosition, sampled);
            float leadSeparation = HorizontalDistance(sampled, leadPosition);
            float ownerDistance = HorizontalDistance(sampled, owner);
            if (portalSide > -minimumUpstreamMarginMeters
                || directMove < 2.5f
                || directMove > maximumDirectMoveMeters
                || leadSeparation < minimumLeadSeparationMeters
                || ownerDistance > VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters)
            {
                continue;
            }
            if (!TryPathBeforePortal(
                    supportPosition,
                    sampled,
                    projectedPortal,
                    entryDirection,
                    minimumUpstreamMarginMeters,
                    out float pathDistance,
                    out int corners,
                    out string pathStatus)
                || pathDistance > maximumPathMeters
                || corners > 8)
            {
                continue;
            }

            float upstreamError = Math.Abs((-portalSide) - desiredUpstreamMeters);
            float score = 110f - upstreamError * 4.0f - pathDistance * 1.25f - corners * 1.5f - ownerDistance * 0.15f;
            string summary = pathStatus
                + ";portalSide=" + portalSide.ToString("0.0", CultureInfo.InvariantCulture)
                + ";desiredUpstream=" + desiredUpstreamMeters.ToString("0.0", CultureInfo.InvariantCulture)
                + ";leadSeparation=" + leadSeparation.ToString("0.0", CultureInfo.InvariantCulture)
                + ";ownerDistance=" + ownerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + ";preparedPathPrePortal=true; crossesPortal=false";
            var scored = new CandidateScore(true, sampled, candidate.Sector, candidate.Reason + ":accepted", summary, ownerDistance, pathDistance, score);
            if (!best.Valid || scored.Score > best.Score)
            {
                best = scored;
            }
        }

        if (TryResolveAuthoredThresholdSupport(
                supportSnapshot,
                supportPosition,
                leadPosition,
                owner,
                projectedPortal,
                entryDirection,
                desiredUpstreamMeters,
                minimumUpstreamMarginMeters,
                minimumLeadSeparationMeters,
                maximumDirectMoveMeters,
                maximumPathMeters,
                out CandidateScore authoredThreshold)
            && (!best.Valid || authoredThreshold.Score > best.Score))
        {
            best = authoredThreshold;
        }

        if (!best.Valid)
        {
            return false;
        }
        plan = new VanguardTacticalPlacementPlan(
            true,
            best.Anchor,
            best.Sector,
            supportSnapshot.SquadCohesion.TacticalEnvironmentKind,
            "threshold_support",
            best.Reason,
            best.PathSummary,
            best.OwnerPathDistance,
            best.BotPathDistance,
            best.Score);
        return true;
    }

    private static bool TryResolveAuthoredThresholdSupport(
        OperatorDecisionSnapshot snapshot,
        Vector3 supportPosition,
        Vector3 leadPosition,
        Vector3 owner,
        Vector3 projectedPortal,
        Vector3 entryDirection,
        float desiredUpstreamMeters,
        float minimumUpstreamMarginMeters,
        float minimumLeadSeparationMeters,
        float maximumDirectMoveMeters,
        float maximumPathMeters,
        out CandidateScore best)
    {
        best = CandidateScore.Invalid("authored_threshold_support_unavailable");
        if (!VanguardTacticalCoordinationRuntimeReadService.TryGetSnapshot(out var topology))
        {
            return false;
        }

        const float maximumUpstreamMeters = 8.0f;
        const float maximumPortalDistanceMeters = 9.0f;
        foreach (VanguardTacticalCoordinationReadAnchor anchor in topology.TacticalAnchors)
        {
            if (!anchor.Enabled || anchor.ValidationState == VanguardTacticalAuthoringValidationState.Invalid)
            {
                continue;
            }

            foreach (VanguardTacticalCoordinationReadPoseSlot slot in anchor.PoseSlots)
            {
                if (!slot.Enabled || slot.ValidationState == VanguardTacticalAuthoringValidationState.Invalid)
                {
                    continue;
                }

                Vector3 raw = slot.NavMeshProjectedPosition ?? slot.Position;
                if (!TrySample(raw, 2.25f, out Vector3 sampled))
                {
                    continue;
                }

                float portalSide = Vector3.Dot(Flatten(sampled - projectedPortal), entryDirection);
                float upstreamMeters = -portalSide;
                float portalDistance = HorizontalDistance(sampled, projectedPortal);
                float directMove = HorizontalDistance(supportPosition, sampled);
                float leadSeparation = HorizontalDistance(sampled, leadPosition);
                float ownerDistance = HorizontalDistance(sampled, owner);
                if (portalSide > -minimumUpstreamMarginMeters
                    || upstreamMeters > maximumUpstreamMeters
                    || portalDistance > maximumPortalDistanceMeters
                    || directMove < 2.5f
                    || directMove > maximumDirectMoveMeters
                    || leadSeparation < minimumLeadSeparationMeters
                    || ownerDistance > VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters)
                {
                    continue;
                }

                if (!TryPathBeforePortal(
                        supportPosition,
                        sampled,
                        projectedPortal,
                        entryDirection,
                        minimumUpstreamMarginMeters,
                        out float pathDistance,
                        out int corners,
                        out string pathStatus)
                    || pathDistance > maximumPathMeters
                    || corners > 8)
                {
                    continue;
                }

                Vector3 viewDirection = Flatten(slot.ViewDirection);
                float authoredViewEntryDot = 0f;
                bool authoredViewKnown = viewDirection.sqrMagnitude > 0.01f;
                if (authoredViewKnown)
                {
                    viewDirection.Normalize();
                    authoredViewEntryDot = Mathf.Clamp(Vector3.Dot(viewDirection, entryDirection), -1f, 1f);
                }

                float upstreamError = Math.Abs(upstreamMeters - desiredUpstreamMeters);
                float score = 116f
                    - upstreamError * 4.0f
                    - pathDistance * 1.25f
                    - corners * 1.5f
                    - ownerDistance * 0.15f
                    - portalDistance * 0.20f;
                if (authoredViewKnown)
                {
                    score += authoredViewEntryDot * 4f;
                }

                string summary = pathStatus
                    + ";source=authored_pose_slot"
                    + ";anchor=" + anchor.AnchorId
                    + ";poseSlot=" + slot.PoseSlotId
                    + ";topologyRevision=" + topology.Revision.ToString(CultureInfo.InvariantCulture)
                    + ";portalSide=" + portalSide.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";upstream=" + upstreamMeters.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";portalDistance=" + portalDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";leadSeparation=" + leadSeparation.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";ownerDistance=" + ownerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";authoredViewEntryDot=" + authoredViewEntryDot.ToString("0.00", CultureInfo.InvariantCulture)
                    + ";authoredViewKnown=" + Bool(authoredViewKnown)
                    + ";preparedPathPrePortal=true;crossesPortal=false;poseApplied=false;orientationApplied=false";
                var scored = new CandidateScore(
                    true,
                    sampled,
                    "authored_threshold_support",
                    "authored_threshold_support_pose_slot:accepted",
                    summary,
                    ownerDistance,
                    pathDistance,
                    score);
                if (!best.Valid || scored.Score > best.Score)
                {
                    best = scored;
                }
            }
        }

        return best.Valid;
    }

    public static bool IsPreparedPathBeforePortal(
        NavMeshPath preparedPath,
        Vector3 projectedPortal,
        Vector3 entryDirection,
        float minimumUpstreamMarginMeters,
        out string reason)
    {
        reason = "prepared_path_missing";
        if (preparedPath == null || preparedPath.corners == null || preparedPath.corners.Length < 2)
        {
            return false;
        }
        Vector3 forward = Flatten(entryDirection);
        if (forward.sqrMagnitude <= 0.01f)
        {
            reason = "entry_direction_missing";
            return false;
        }
        forward.Normalize();
        for (int index = 0; index < preparedPath.corners.Length; index++)
        {
            float side = Vector3.Dot(Flatten(preparedPath.corners[index] - projectedPortal), forward);
            if (side > -minimumUpstreamMarginMeters)
            {
                reason = "prepared_path_crosses_portal:corner=" + index.ToString(CultureInfo.InvariantCulture)
                    + ";side=" + side.ToString("0.00", CultureInfo.InvariantCulture);
                return false;
            }
        }
        reason = "prepared_path_preportal";
        return true;
    }

    private static bool TryPathBeforePortal(
        Vector3 start,
        Vector3 end,
        Vector3 projectedPortal,
        Vector3 entryDirection,
        float minimumUpstreamMarginMeters,
        out float distance,
        out int corners,
        out string status)
    {
        distance = 0f;
        corners = 0;
        status = "none";
        if (!TrySample(start, 3.0f, out Vector3 sampledStart))
        {
            status = "start_sample_failed";
            return false;
        }
        if (!TrySample(end, 3.0f, out Vector3 sampledEnd))
        {
            status = "end_sample_failed";
            return false;
        }
        var path = new NavMeshPath();
        bool calculated = NavMesh.CalculatePath(sampledStart, sampledEnd, NavMesh.AllAreas, path);
        corners = path.corners == null ? 0 : path.corners.Length;
        distance = PathDistance(path);
        if (!calculated || path.status != NavMeshPathStatus.PathComplete || corners < 2)
        {
            status = "calculated=" + Bool(calculated) + ";status=" + path.status + ";corners=" + corners.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        if (!IsPreparedPathBeforePortal(path, projectedPortal, entryDirection, minimumUpstreamMarginMeters, out string boundaryReason))
        {
            status = boundaryReason;
            return false;
        }
        status = "calculated=true;status=" + path.status + ";corners=" + corners.ToString(CultureInfo.InvariantCulture)
            + ";pathPrePortal=true";
        return true;
    }

    /// <summary>
    /// Resolves a support position on an already-proven alternate route. The support element trails
    /// the FlankLead near the same establishment region without stacking on the lead anchor. This
    /// consumes only proven route geometry; it does not infer a room, choose a target or command fire.
    /// </summary>
    public static bool TryResolveFlankSupport(
        OperatorDecisionSnapshot supportSnapshot,
        Vector3 supportPosition,
        IReadOnlyList<Vector3> provenPathCorners,
        Vector3 establishmentRegionCenter,
        float establishmentRegionRadiusMeters,
        out VanguardTacticalPlacementPlan plan)
    {
        plan = VanguardTacticalPlacementPlan.Invalid("flank_support_not_evaluated");
        if (supportSnapshot == null || !supportSnapshot.Alive || provenPathCorners == null || provenPathCorners.Count < 2)
        {
            return false;
        }

        const float desiredBackoffMeters = 5.0f;
        const float minimumLeadSeparationMeters = 2.75f;
        const float maximumRegionDistanceMeters = 10.0f;
        Vector3 raw = provenPathCorners[provenPathCorners.Count - 1];
        float remaining = desiredBackoffMeters;
        for (int index = provenPathCorners.Count - 1; index > 0 && remaining > 0f; index--)
        {
            Vector3 end = provenPathCorners[index];
            Vector3 start = provenPathCorners[index - 1];
            Vector3 segment = Flatten(end - start);
            float length = segment.magnitude;
            if (length <= 0.01f)
            {
                continue;
            }
            if (length >= remaining)
            {
                raw = end - segment.normalized * remaining;
                remaining = 0f;
                break;
            }
            remaining -= length;
            raw = start;
        }

        if (!TrySample(raw, 2.75f, out Vector3 sampled))
        {
            return false;
        }
        float regionDistance = HorizontalDistance(sampled, establishmentRegionCenter);
        float directMove = HorizontalDistance(supportPosition, sampled);
        if (regionDistance < minimumLeadSeparationMeters
            || regionDistance > Math.Max(maximumRegionDistanceMeters, establishmentRegionRadiusMeters + 6f)
            || directMove < 2.5f)
        {
            return false;
        }
        if (!TryPath(supportPosition, sampled, out float pathDistance, out int corners, out string pathStatus)
            || pathDistance < 2.5f)
        {
            return false;
        }

        string summary = pathStatus
            + ";regionDistance=" + regionDistance.ToString("0.0", CultureInfo.InvariantCulture)
            + ";backoff=" + desiredBackoffMeters.ToString("0.0", CultureInfo.InvariantCulture)
            + ";corners=" + corners.ToString(CultureInfo.InvariantCulture)
            + ";source=proven_same_axis_alt_route";
        float ownerDistance = supportSnapshot.SquadCohesion.OwnerPosition.HasValue
            ? HorizontalDistance(sampled, supportSnapshot.SquadCohesion.OwnerPosition.Value)
            : 0f;
        CandidateScore best = new(
            true,
            sampled,
            "alternate_entry_support",
            "same_axis_support_anchor_behind_flank_lead",
            summary,
            ownerDistance,
            pathDistance,
            100f - pathDistance * 0.5f - regionDistance * 0.25f);

        if (TryResolveAuthoredFlankSupport(
                supportSnapshot,
                supportPosition,
                provenPathCorners,
                establishmentRegionCenter,
                establishmentRegionRadiusMeters,
                minimumLeadSeparationMeters,
                maximumRegionDistanceMeters,
                out CandidateScore authoredSupport)
            && authoredSupport.Score > best.Score)
        {
            best = authoredSupport;
        }

        plan = new VanguardTacticalPlacementPlan(
            true,
            best.Anchor,
            best.Sector,
            supportSnapshot.SquadCohesion.TacticalEnvironmentKind,
            "flank_support_alt_route",
            best.Reason,
            best.PathSummary,
            best.OwnerPathDistance,
            best.BotPathDistance,
            best.Score);
        return true;
    }

    private static bool TryResolveAuthoredFlankSupport(
        OperatorDecisionSnapshot snapshot,
        Vector3 supportPosition,
        IReadOnlyList<Vector3> provenPathCorners,
        Vector3 establishmentRegionCenter,
        float establishmentRegionRadiusMeters,
        float minimumLeadSeparationMeters,
        float maximumRegionDistanceMeters,
        out CandidateScore best)
    {
        best = CandidateScore.Invalid("authored_flank_support_unavailable");
        if (!VanguardTacticalCoordinationRuntimeReadService.TryGetSnapshot(out var topology))
        {
            return false;
        }

        const float maximumRouteCorridorMeters = 5.5f;
        float maximumRegionDistance = Math.Max(maximumRegionDistanceMeters, establishmentRegionRadiusMeters + 6f);
        foreach (VanguardTacticalCoordinationReadAnchor anchor in topology.TacticalAnchors)
        {
            if (!anchor.Enabled || anchor.ValidationState == VanguardTacticalAuthoringValidationState.Invalid)
            {
                continue;
            }

            foreach (VanguardTacticalCoordinationReadPoseSlot slot in anchor.PoseSlots)
            {
                if (!slot.Enabled || slot.ValidationState == VanguardTacticalAuthoringValidationState.Invalid)
                {
                    continue;
                }

                Vector3 raw = slot.NavMeshProjectedPosition ?? slot.Position;
                if (!TrySample(raw, 2.75f, out Vector3 sampledAuthored))
                {
                    continue;
                }

                float regionDistanceAuthored = HorizontalDistance(sampledAuthored, establishmentRegionCenter);
                float routeCorridorDistance = HorizontalDistanceToPolyline(sampledAuthored, provenPathCorners);
                float directMove = HorizontalDistance(supportPosition, sampledAuthored);
                if (regionDistanceAuthored < minimumLeadSeparationMeters
                    || regionDistanceAuthored > maximumRegionDistance
                    || routeCorridorDistance > maximumRouteCorridorMeters
                    || directMove < 2.5f)
                {
                    continue;
                }

                if (!TryPath(supportPosition, sampledAuthored, out float authoredPathDistance, out int authoredCorners, out string authoredPathStatus)
                    || authoredPathDistance < 2.5f)
                {
                    continue;
                }

                Vector3 viewDirection = Flatten(slot.ViewDirection);
                Vector3 towardEstablishment = Flatten(establishmentRegionCenter - sampledAuthored);
                float authoredViewEstablishmentDot = 0f;
                bool authoredViewKnown = viewDirection.sqrMagnitude > 0.01f && towardEstablishment.sqrMagnitude > 0.01f;
                if (authoredViewKnown)
                {
                    viewDirection.Normalize();
                    towardEstablishment.Normalize();
                    authoredViewEstablishmentDot = Mathf.Clamp(Vector3.Dot(viewDirection, towardEstablishment), -1f, 1f);
                }

                float authoredOwnerDistance = snapshot.SquadCohesion.OwnerPosition.HasValue
                    ? HorizontalDistance(sampledAuthored, snapshot.SquadCohesion.OwnerPosition.Value)
                    : 0f;
                float score = 106f
                    - authoredPathDistance * 0.5f
                    - regionDistanceAuthored * 0.25f
                    - routeCorridorDistance * 1.5f;
                if (authoredViewKnown)
                {
                    score += authoredViewEstablishmentDot * 3f;
                }

                string authoredSummary = authoredPathStatus
                    + ";source=authored_pose_slot"
                    + ";anchor=" + anchor.AnchorId
                    + ";poseSlot=" + slot.PoseSlotId
                    + ";topologyRevision=" + topology.Revision.ToString(CultureInfo.InvariantCulture)
                    + ";regionDistance=" + regionDistanceAuthored.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";routeCorridorDistance=" + routeCorridorDistance.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";directMove=" + directMove.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";corners=" + authoredCorners.ToString(CultureInfo.InvariantCulture)
                    + ";authoredViewEstablishmentDot=" + authoredViewEstablishmentDot.ToString("0.00", CultureInfo.InvariantCulture)
                    + ";authoredViewKnown=" + Bool(authoredViewKnown)
                    + ";sameAxisRouteRequired=true;poseApplied=false;orientationApplied=false";
                var scored = new CandidateScore(
                    true,
                    sampledAuthored,
                    "authored_flank_support",
                    "authored_flank_support_pose_slot:accepted",
                    authoredSummary,
                    authoredOwnerDistance,
                    authoredPathDistance,
                    score);
                if (!best.Valid || scored.Score > best.Score)
                {
                    best = scored;
                }
            }
        }

        return best.Valid;
    }

    private static float HorizontalDistanceToPolyline(Vector3 point, IReadOnlyList<Vector3> corners)
    {
        if (corners == null || corners.Count == 0)
        {
            return float.PositiveInfinity;
        }
        if (corners.Count == 1)
        {
            return HorizontalDistance(point, corners[0]);
        }

        Vector3 flatPoint = new(point.x, 0f, point.z);
        float bestDistance = float.PositiveInfinity;
        for (int index = 1; index < corners.Count; index++)
        {
            Vector3 start = new(corners[index - 1].x, 0f, corners[index - 1].z);
            Vector3 end = new(corners[index].x, 0f, corners[index].z);
            Vector3 segment = end - start;
            float lengthSquared = segment.sqrMagnitude;
            Vector3 closest = start;
            if (lengthSquared > 0.0001f)
            {
                float t = Mathf.Clamp01(Vector3.Dot(flatPoint - start, segment) / lengthSquared);
                closest = start + segment * t;
            }
            bestDistance = Math.Min(bestDistance, Vector3.Distance(flatPoint, closest));
        }
        return bestDistance;
    }

    /// <summary>
    /// Resolves one short retreat bound away from the current qualified threat, weakly biased toward
    /// a surviving cover partner. The resulting point must materially increase threat standoff.
    /// BreakContact reuses this exact geometry with a longer step; it does not own a separate brain.
    /// </summary>
    public static bool TryResolveBoundRetreat(
        OperatorDecisionSnapshot moverSnapshot,
        Vector3 moverPosition,
        Vector3 supportPosition,
        Vector3 threatPosition,
        float requestedStepMeters,
        out VanguardTacticalPlacementPlan plan)
    {
        plan = VanguardTacticalPlacementPlan.Invalid("bound_retreat_not_evaluated");
        if (moverSnapshot == null || !moverSnapshot.Alive || !moverSnapshot.SquadCohesion.OwnerPosition.HasValue)
        {
            return false;
        }
        Vector3 owner = moverSnapshot.SquadCohesion.OwnerPosition.Value;

        Vector3 awayFromThreat = Flatten(moverPosition - threatPosition);
        float currentThreatDistance = awayFromThreat.magnitude;
        if (awayFromThreat.sqrMagnitude <= 0.01f)
        {
            return false;
        }
        awayFromThreat.Normalize();
        Vector3 towardSupport = Flatten(supportPosition - moverPosition);
        if (towardSupport.sqrMagnitude > 0.01f)
        {
            towardSupport.Normalize();
        }
        Vector3 retreatDirection = awayFromThreat * 0.78f + towardSupport * 0.22f;
        if (retreatDirection.sqrMagnitude <= 0.01f)
        {
            retreatDirection = awayFromThreat;
        }
        retreatDirection.Normalize();
        Vector3 lateral = new(retreatDirection.z, 0f, -retreatDirection.x);
        float step = Math.Max(5.0f, Math.Min(12.0f, requestedStepMeters));

        var rawCandidates = new[]
        {
            new CandidateAnchor(moverPosition + retreatDirection * step, "bound_retreat", "bound_retreat_support_bias"),
            new CandidateAnchor(moverPosition + awayFromThreat * step + lateral * 2.5f, "bound_retreat_left", "bound_retreat_left"),
            new CandidateAnchor(moverPosition + awayFromThreat * step - lateral * 2.5f, "bound_retreat_right", "bound_retreat_right"),
        };

        CandidateScore best = CandidateScore.Invalid("bound_retreat_no_candidate");
        foreach (CandidateAnchor candidate in rawCandidates)
        {
            if (!TrySample(candidate.RawAnchor, 3.0f, out Vector3 sampled))
            {
                continue;
            }
            float sampledThreatDistance = HorizontalDistance(sampled, threatPosition);
            float standoffGain = sampledThreatDistance - currentThreatDistance;
            float directMove = HorizontalDistance(moverPosition, sampled);
            float ownerDistance = HorizontalDistance(sampled, owner);
            if (standoffGain < 2.5f
                || ownerDistance > VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters
                || directMove < 2.5f
                || directMove > 15f)
            {
                continue;
            }
            if (!TryPath(moverPosition, sampled, out float pathDistance, out int corners, out string pathStatus)
                || pathDistance > 20f || corners > 8)
            {
                continue;
            }

            float supportDistance = HorizontalDistance(sampled, supportPosition);
            float value = 118f + standoffGain * 3.5f - pathDistance * 1.25f - corners * 1.5f - Math.Min(12f, supportDistance * 0.25f) - ownerDistance * 0.15f;
            string summary = pathStatus
                + ";threatBefore=" + currentThreatDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + ";threatAfter=" + sampledThreatDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + ";standoffGain=" + standoffGain.ToString("0.0", CultureInfo.InvariantCulture)
                + ";supportDistance=" + supportDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + ";ownerDistance=" + ownerDistance.ToString("0.0", CultureInfo.InvariantCulture)
                + ";step=" + step.ToString("0.0", CultureInfo.InvariantCulture);
            var scored = new CandidateScore(true, sampled, candidate.Sector, candidate.Reason + ":accepted", summary, ownerDistance, pathDistance, value);
            if (!best.Valid || scored.Score > best.Score)
            {
                best = scored;
            }
        }

        if (!best.Valid)
        {
            return false;
        }
        plan = new VanguardTacticalPlacementPlan(
            true,
            best.Anchor,
            best.Sector,
            moverSnapshot.SquadCohesion.TacticalEnvironmentKind,
            "bound_retreat",
            best.Reason,
            best.PathSummary,
            best.OwnerPathDistance,
            best.BotPathDistance,
            best.Score);
        return true;
    }

    /// <summary>Returns the exact complete NavMeshPath later bound into SAIN BotPathDataManual.</summary>
    public static bool TryCalculatePreparedRuntimePath(
        Vector3 start,
        Vector3 end,
        out NavMeshPath path,
        out float distance,
        out string status)
    {
        path = new NavMeshPath();
        distance = 0f;
        status = "none";
        if (!TrySample(start, 3.0f, out Vector3 sampledStart))
        {
            status = "start_sample_failed";
            return false;
        }
        if (!TrySample(end, 3.0f, out Vector3 sampledEnd))
        {
            status = "end_sample_failed";
            return false;
        }
        bool calculated = NavMesh.CalculatePath(sampledStart, sampledEnd, NavMesh.AllAreas, path);
        int corners = path.corners == null ? 0 : path.corners.Length;
        distance = PathDistance(path);
        status = "calculated=" + Bool(calculated)
            + ";status=" + path.status
            + ";corners=" + corners.ToString(CultureInfo.InvariantCulture)
            + ";distance=" + distance.ToString("0.0", CultureInfo.InvariantCulture);
        return calculated && path.status == NavMeshPathStatus.PathComplete && corners >= 2;
    }

    private static bool TrySample(Vector3 raw, float radius, out Vector3 sampled)
    {
        if (NavMesh.SamplePosition(raw + Vector3.up * 0.30f, out var hit, radius, NavMesh.AllAreas))
        {
            sampled = hit.position;
            return true;
        }

        sampled = Vector3.zero;
        return false;
    }

    private static bool TryPath(Vector3 start, Vector3 end, out float distance, out int corners, out string status)
    {
        distance = 0f;
        corners = 0;
        status = "none";
        if (!TrySample(start, 3.0f, out var sampledStart))
        {
            status = "start_sample_failed";
            return false;
        }

        if (!TrySample(end, 3.0f, out var sampledEnd))
        {
            status = "end_sample_failed";
            return false;
        }

        var path = new NavMeshPath();
        bool calculated = NavMesh.CalculatePath(sampledStart, sampledEnd, NavMesh.AllAreas, path);
        corners = path.corners == null ? 0 : path.corners.Length;
        distance = PathDistance(path);
        status = "calculated=" + Bool(calculated) + ";status=" + path.status + ";corners=" + corners.ToString(CultureInfo.InvariantCulture);
        return calculated && path.status == NavMeshPathStatus.PathComplete && corners >= 2;
    }

    private static float PathDistance(NavMeshPath path)
    {
        if (path.corners == null || path.corners.Length < 2)
        {
            return 0f;
        }

        float distance = 0f;
        for (int index = 1; index < path.corners.Length; index++)
        {
            distance += HorizontalDistance(path.corners[index - 1], path.corners[index]);
        }

        return distance;
    }

    private static Vector3 Flatten(Vector3 vector)
    {
        vector.y = 0f;
        return vector;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static float SafeRatio(float pathDistance, float directDistance) => directDistance <= 0.25f ? 1f : pathDistance / directDistance;
    private static string Bool(bool value) => value ? "true" : "false";

    private readonly struct CandidateAnchor
    {
        public CandidateAnchor(Vector3 rawAnchor, string sector, string reason)
        {
            RawAnchor = rawAnchor;
            Sector = sector;
            Reason = reason;
        }

        public Vector3 RawAnchor { get; }
        public string Sector { get; }
        public string Reason { get; }
    }

    private readonly struct CandidateScore
    {
        public CandidateScore(bool valid, Vector3 anchor, string sector, string reason, string pathSummary, float ownerPathDistance, float botPathDistance, float score)
        {
            Valid = valid;
            Anchor = anchor;
            Sector = sector;
            Reason = reason;
            PathSummary = pathSummary;
            OwnerPathDistance = ownerPathDistance;
            BotPathDistance = botPathDistance;
            Score = score;
        }

        public static CandidateScore Invalid(string reason) => new(false, Vector3.zero, "none", reason, "none", 0f, 0f, -9999f);

        public bool Valid { get; }
        public Vector3 Anchor { get; }
        public string Sector { get; }
        public string Reason { get; }
        public string PathSummary { get; }
        public float OwnerPathDistance { get; }
        public float BotPathDistance { get; }
        public float Score { get; }
    }
}
#endif
