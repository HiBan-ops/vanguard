#if SPT_CLIENT
using UnityEngine;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Encodes the deterministic rules for Squad Cohesion Doctrine within the squad-cohesion analysis.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class VanguardSquadCohesionDoctrine
{
    public const string StatusTag = "VANGUARD_COHESION_READONLY_OK";
    public static float TacticalBubbleRadiusMeters => VanguardMovementAuthorityDoctrine.TacticalBubbleMeters;
    public const float CoreBandMeters = 12f;
    public const float InnerBandMeters = 35f;
    public const float RearGuardArcDegrees = 55f;
    public const float FrontArcDegrees = 55f;
    public const float FlankArcDegrees = 125f;
    public const float UsefulStationarySpeed = 0.15f;
    public const int MaxRearGuards = 1;

    public static string BubbleBand(float distance)
    {
        if (distance <= CoreBandMeters)
        {
            return "core";
        }

        if (distance <= InnerBandMeters)
        {
            return "inner";
        }

        if (distance <= TacticalBubbleRadiusMeters)
        {
            return "extended";
        }

        return "outside";
    }

    public static string SectorFromAngle(float signedAngleDegrees)
    {
        float angle = NormalizeAngle(signedAngleDegrees);
        float abs = Mathf.Abs(angle);

        if (abs <= FrontArcDegrees)
        {
            return "front";
        }

        if (abs >= 180f - RearGuardArcDegrees)
        {
            return "rear";
        }

        if (angle < 0f && abs <= FlankArcDegrees)
        {
            return "right_flank";
        }

        if (angle > 0f && abs <= FlankArcDegrees)
        {
            return "left_flank";
        }

        return angle < 0f ? "right_rear" : "left_rear";
    }

    public static string TacticalRoleForSector(string sector)
    {
        return sector switch
        {
            "front" => "forward_probe",
            "left_flank" => "left_flank_cover",
            "right_flank" => "right_flank_cover",
            "left_rear" => "rear_left_cover",
            "right_rear" => "rear_right_cover",
            "rear" => "rear_guard",
            _ => "unassigned"
        };
    }

    public static string LocalSainEnvelope(bool directThreat, string squadOrder)
    {
        if (string.Equals(squadOrder, "go", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(squadOrder, "advance", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(squadOrder, "assault", System.StringComparison.OrdinalIgnoreCase))
        {
            return "offensive_search_allowed_readonly";
        }

        if (directThreat)
        {
            return "local_defensive_combat_readonly";
        }

        return "formation_hold_angle_readonly";
    }

    public static bool IsUsefulSectorPosition(
        bool inBubble,
        bool directThreat,
        string sector,
        int sameSectorCount,
        int rearCount,
        float realSpeed,
        bool hasPath)
    {
        if (!inBubble)
        {
            return false;
        }

        if (directThreat)
        {
            return true;
        }

        if (sameSectorCount > 1 && sector != "front")
        {
            return false;
        }

        if (IsRearLikeSector(sector) && rearCount > MaxRearGuards)
        {
            return false;
        }

        if (realSpeed <= UsefulStationarySpeed && !hasPath)
        {
            return sector != "unknown";
        }

        return true;
    }

    private static bool IsRearLikeSector(string? sector)
    {
        return !string.IsNullOrWhiteSpace(sector)
            && sector.IndexOf("rear", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static float NormalizeAngle(float angle)
    {
        while (angle > 180f)
        {
            angle -= 360f;
        }

        while (angle < -180f)
        {
            angle += 360f;
        }

        return angle;
    }
}
#endif
