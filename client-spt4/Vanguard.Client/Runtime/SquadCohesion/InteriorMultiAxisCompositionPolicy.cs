#if SPT_CLIENT
using System;
using System.Linq;
using UnityEngine;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.TacticalAuthoring;

// Responsibility: projects the already-current RouteAssignment multi-axis composition into interior-development candidate/support preference without creating a new maneuver state machine.
// Flow: exact owner/threat route-assignment truth -> Direct/Alternate membership -> support ordering and bounded destination bias -> context token revalidation for one disposable interior lease.
// Authority boundary: read/scoring only. This policy cannot create route assignments, move an Operator, acquire a scheduler lease, mutate SAIN, select targets, aim or fire.
// Invariant: alternate members may prefer progress on their assigned axis while Direct members never use InteriorDevelop to enter the assigned alternate establishment region; assignment loss/change removes only this bias and cannot fabricate a route.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class InteriorMultiAxisCompositionPolicy
{
    public const string StatusTag = "VANGUARD_TC_INTERIOR_MULTI_AXIS_COMPOSITION_STATUS";

    private static readonly TimeSpan MaximumAssignmentAge = TimeSpan.FromSeconds(2.5d);
    private const float AlternateRegressionToleranceMeters = 5.0f;
    private const float AlternateContextInfluenceMeters = 18.0f;
    private const float DirectAlternateRegionGuardMeters = 6.0f;

    internal static InteriorMultiAxisCompositionContext Resolve(
        OperatorDecisionSnapshot mover,
        DateTimeOffset now,
        VanguardTacticalCoordinationReadSnapshot? topology = null)
    {
        if (mover == null
            || string.IsNullOrWhiteSpace(mover.OwnerProfileId)
            || string.IsNullOrWhiteSpace(mover.Threat.EnemyId)
            || !RouteAssignmentService.TryGetSnapshot(out RouteAssignmentSnapshot snapshot)
            || !snapshot.IsAvailable
            || snapshot.PublishedAtUtc == DateTimeOffset.MinValue
            || now - snapshot.PublishedAtUtc > MaximumAssignmentAge)
        {
            return InteriorMultiAxisCompositionContext.None;
        }
        if (topology != null
            && (snapshot.TopologyRevision != topology.Revision
                || !string.Equals(snapshot.TopologyContentHash, topology.ContentHash, StringComparison.Ordinal)))
        {
            return InteriorMultiAxisCompositionContext.None;
        }

        ThreatGroupRouteAssignment? assignment = snapshot.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, mover.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeThreat(item.ThreatId), NormalizeThreat(mover.Threat.EnemyId), StringComparison.OrdinalIgnoreCase));
        if (assignment == null || string.IsNullOrWhiteSpace(assignment.AlternateCandidateId))
        {
            return InteriorMultiAxisCompositionContext.None;
        }

        string role = string.Equals(assignment.SelectedAltBotProfileId, mover.BotProfileId, StringComparison.OrdinalIgnoreCase)
            ? "alternate_lead"
            : string.Equals(assignment.SelectedAltSupportBotProfileId, mover.BotProfileId, StringComparison.OrdinalIgnoreCase)
                ? "alternate_support"
                : assignment.DirectBotProfileIds.Any(item => string.Equals(item, mover.BotProfileId, StringComparison.OrdinalIgnoreCase))
                    ? "direct"
                    : "unassigned";
        if (string.Equals(role, "unassigned", StringComparison.OrdinalIgnoreCase))
        {
            return InteriorMultiAxisCompositionContext.None;
        }

        string token = Safe(assignment.AlternateCandidateId) + ":" + role;
        return new InteriorMultiAxisCompositionContext(
            true,
            assignment.AlternateCandidateId,
            role,
            assignment.AlternateEstablishmentRegionCenter,
            Math.Max(0.5f, assignment.AlternateEstablishmentRegionRadiusMeters),
            token,
            snapshot.PublishedAtUtc);
    }

    internal static int SupportPriority(InteriorMultiAxisCompositionContext context, string supportBotProfileId, OperatorDecisionSnapshot mover)
    {
        if (!context.Valid || string.IsNullOrWhiteSpace(supportBotProfileId) || mover == null)
        {
            return 2;
        }
        if (!RouteAssignmentService.TryGetSnapshot(out RouteAssignmentSnapshot snapshot))
        {
            return 2;
        }
        ThreatGroupRouteAssignment? assignment = snapshot.ThreatGroups.FirstOrDefault(item =>
            string.Equals(item.OwnerProfileId, mover.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeThreat(item.ThreatId), NormalizeThreat(mover.Threat.EnemyId), StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.AlternateCandidateId, context.AxisCandidateId, StringComparison.OrdinalIgnoreCase));
        if (assignment == null)
        {
            return 2;
        }

        bool supportIsDirect = assignment.DirectBotProfileIds.Any(item => string.Equals(item, supportBotProfileId, StringComparison.OrdinalIgnoreCase));
        if (context.IsAlternateMember)
        {
            if (supportIsDirect) return 0;
            if (string.Equals(assignment.SelectedAltSupportBotProfileId, supportBotProfileId, StringComparison.OrdinalIgnoreCase)) return 1;
            return 2;
        }
        if (context.IsDirectMember)
        {
            return supportIsDirect ? 0 : 1;
        }
        return 2;
    }

    internal static bool TryEvaluateDestination(
        InteriorMultiAxisCompositionContext context,
        Vector3 moverPosition,
        Vector3 destination,
        bool crossesAuthoredPortal,
        out float scoreAdjustment,
        out string summary)
    {
        scoreAdjustment = 0f;
        summary = "multi_axis=none";
        if (!context.Valid)
        {
            return true;
        }

        float before = HorizontalDistance(moverPosition, context.AlternateRegionCenter);
        float after = HorizontalDistance(destination, context.AlternateRegionCenter);
        float progress = before - after;

        if (context.IsAlternateMember)
        {
            bool regionRelevant = before <= context.AlternateRegionRadiusMeters + AlternateContextInfluenceMeters;
            if (regionRelevant && after > before + AlternateRegressionToleranceMeters)
            {
                summary = "multi_axis_reject=alternate_axis_regression;before=" + Number(before) + ";after=" + Number(after);
                return false;
            }

            scoreAdjustment += Math.Max(-4f, Math.Min(18f, progress * 2.0f));
            if (after <= context.AlternateRegionRadiusMeters + 4f)
            {
                scoreAdjustment += 10f;
            }
        }
        else if (context.IsDirectMember
            && before > context.AlternateRegionRadiusMeters + DirectAlternateRegionGuardMeters
            && after <= context.AlternateRegionRadiusMeters + DirectAlternateRegionGuardMeters)
        {
            summary = "multi_axis_reject=direct_member_entering_alternate_region;before=" + Number(before) + ";after=" + Number(after);
            return false;
        }

        summary = "multi_axis=" + Safe(context.Role)
            + ";axis=" + Safe(context.AxisCandidateId)
            + ";regionBefore=" + Number(before)
            + ";regionAfter=" + Number(after)
            + ";axisProgress=" + Number(progress)
            + ";scoreAdjustment=" + Number(scoreAdjustment);
        return true;
    }

    internal static bool IsContextCurrent(
        OperatorDecisionSnapshot mover,
        string expectedToken,
        DateTimeOffset now,
        VanguardTacticalCoordinationReadSnapshot? topology,
        out string reason)
    {
        reason = "multi_axis_not_required";
        InteriorMultiAxisCompositionContext current = Resolve(mover, now, topology);
        if (string.IsNullOrWhiteSpace(expectedToken) || string.Equals(expectedToken, "none", StringComparison.OrdinalIgnoreCase))
        {
            if (current.Valid)
            {
                reason = "multi_axis_assignment_emerged:" + Safe(current.ContextToken);
                return false;
            }
            return true;
        }
        if (!current.Valid)
        {
            reason = "multi_axis_assignment_lost";
            return false;
        }
        if (!string.Equals(current.ContextToken, expectedToken, StringComparison.OrdinalIgnoreCase))
        {
            reason = "multi_axis_assignment_changed:" + Safe(current.ContextToken);
            return false;
        }
        reason = "multi_axis_assignment_current:" + Safe(current.ContextToken);
        return true;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static string NormalizeThreat(string? value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) ? string.Empty : value.Trim();
    private static string Number(float value) => value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('~', '_').Replace('\r', '_').Replace('\n', '_');
}

internal readonly struct InteriorMultiAxisCompositionContext
{
    public static readonly InteriorMultiAxisCompositionContext None = new(false, string.Empty, string.Empty, Vector3.zero, 0f, "none", DateTimeOffset.MinValue);

    public InteriorMultiAxisCompositionContext(
        bool valid,
        string axisCandidateId,
        string role,
        Vector3 alternateRegionCenter,
        float alternateRegionRadiusMeters,
        string contextToken,
        DateTimeOffset publishedAtUtc)
    {
        Valid = valid;
        AxisCandidateId = axisCandidateId;
        Role = role;
        AlternateRegionCenter = alternateRegionCenter;
        AlternateRegionRadiusMeters = alternateRegionRadiusMeters;
        ContextToken = contextToken;
        PublishedAtUtc = publishedAtUtc;
    }

    public bool Valid { get; }
    public string AxisCandidateId { get; }
    public string Role { get; }
    public Vector3 AlternateRegionCenter { get; }
    public float AlternateRegionRadiusMeters { get; }
    public string ContextToken { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public bool IsAlternateMember => string.Equals(Role, "alternate_lead", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Role, "alternate_support", StringComparison.OrdinalIgnoreCase);
    public bool IsDirectMember => string.Equals(Role, "direct", StringComparison.OrdinalIgnoreCase);
}
#endif
