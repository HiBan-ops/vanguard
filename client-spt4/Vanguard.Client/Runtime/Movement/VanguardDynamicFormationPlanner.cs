#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;

// Responsibility: Computes an executable plan for Dynamic Formation Planner in the movement/cohesion runtime without performing the final action itself.
// Flow: Current snapshots and doctrine are reduced to a candidate plan; the owning scheduler/executor rechecks authority before any mutation.
// Authority boundary: Planning is non-authoritative for physical execution and cannot bypass final combat, medical, loot, or movement safety checks.
// Invariant: Plans stay raid-scoped, deterministic from their inputs, and safe to discard when newer evidence supersedes them.
namespace Vanguard.Client.Runtime.Movement;

/// <summary>
/// Vanguard preserves Vanguard transient formation roles from the Operators' real positions. A lead token is
/// deliberately bounded and rotated; it is not tied to callsign order. The planner is read-only:
/// it selects lane contracts but never drives movement itself.
/// </summary>
internal static class VanguardDynamicFormationPlanner
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, LeadTokenState> LeadByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LeadTokenDuration = TimeSpan.FromSeconds(24.0d);
    private static readonly TimeSpan LeadSuccessGrace = TimeSpan.FromSeconds(7.0d);
    private const float MinimumViableLeadScore = -18.0f;
    private const float FailedLeadLongitudinalMeters = -5.0f;

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            LeadByOwner.Clear();
        }

        VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.DynamicFormationStatusTag,
            $"VANGUARD_DYNAMIC_FORMATION_RESET reason={Safe(reason)}; leadTokens=cleared; tag={VanguardPrimaryExecutionContract.DynamicFormationStatusTag}");
    }

    public static IReadOnlyDictionary<string, string> BuildLaneAssignments(
        string ownerProfileId,
        IReadOnlyList<OperatorDecisionSnapshot> operators,
        Vector3 ownerPosition,
        Vector3 ownerForward,
        string ownerMode,
        DateTimeOffset now)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (operators == null || operators.Count == 0)
        {
            return result;
        }

        var liveOperators = operators.Where(snapshot => snapshot != null && snapshot.Alive).ToArray();
        if (liveOperators.Length == 0)
        {
            return result;
        }

        Vector3 forward = Flatten(ownerForward);
        if (forward.sqrMagnitude <= 0.001f)
        {
            forward = Vector3.forward;
        }
        forward.Normalize();
        Vector3 right = new(forward.z, 0f, -forward.x);

        bool stationary = string.Equals(ownerMode, "stationary_hold", StringComparison.OrdinalIgnoreCase);
        if (stationary)
        {
            var ordered = liveOperators
                .OrderBy(snapshot => Vector3.Dot(Flatten(snapshot.Position - ownerPosition), right))
                .ToArray();
            if (ordered.Length == 1)
            {
                result[ordered[0].BotProfileId] = "front_close";
            }
            else if (ordered.Length == 2)
            {
                result[ordered[0].BotProfileId] = "left_close";
                result[ordered[1].BotProfileId] = "right_close";
            }
            else
            {
                result[ordered[0].BotProfileId] = "left_close";
                result[ordered[^1].BotProfileId] = "right_close";
                var middleOperators = ordered.Skip(1).Take(ordered.Length - 2).ToArray();
                if (middleOperators.Length > 0)
                {
                    // Three Operators compact around a stationary owner as left/right/front.
                    // A rear role is only added when a fourth live member exists.
                    result[middleOperators[0].BotProfileId] = "front_close";
                    for (int index = 1; index < middleOperators.Length; index++)
                    {
                        result[middleOperators[index].BotProfileId] = index == 1
                            ? "rear_guard_close"
                            : (index % 2 == 0 ? "front_close" : "rear_guard_close");
                    }
                }
            }
            return result;
        }

        var eligible = liveOperators
            .Where(snapshot => !snapshot.Medical.Need.HasHeavyBleed && !snapshot.Medical.Need.HasDestroyedPart)
            .ToArray();
        if (eligible.Length == 0)
        {
            eligible = liveOperators;
        }

        OperatorDecisionSnapshot? lead = SelectLead(ownerProfileId, eligible, ownerPosition, forward, now);
        if (lead == null)
        {
            var compact = liveOperators.OrderBy(snapshot => Vector3.Dot(Flatten(snapshot.Position - ownerPosition), right)).ToArray();
            if (compact.Length > 0) result[compact[0].BotProfileId] = "left_side_close";
            if (compact.Length > 1) result[compact[^1].BotProfileId] = "right_side_close";
            for (int index = 1; index < compact.Length - 1; index++)
            {
                result[compact[index].BotProfileId] = index % 2 == 0 ? "front_close" : "rear_guard_close";
            }
            return result;
        }

        float leadSide = Vector3.Dot(Flatten(lead.Position - ownerPosition), right);
        result[lead.BotProfileId] = leadSide <= 0f ? "lead_forward_left" : "lead_forward_right";

        var remaining = liveOperators
            .Where(snapshot => !string.Equals(snapshot.BotProfileId, lead.BotProfileId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(snapshot => Vector3.Dot(Flatten(snapshot.Position - ownerPosition), right))
            .ToArray();
        if (remaining.Length == 1)
        {
            result[remaining[0].BotProfileId] = leadSide <= 0f ? "right_side_close" : "left_side_close";
        }
        else if (remaining.Length >= 2)
        {
            OperatorDecisionSnapshot rear = remaining
                .OrderBy(snapshot => Vector3.Dot(Flatten(snapshot.Position - ownerPosition), forward))
                .First();
            result[rear.BotProfileId] = "rear_guard_close";
            foreach (var side in remaining.Where(snapshot => !string.Equals(snapshot.BotProfileId, rear.BotProfileId, StringComparison.OrdinalIgnoreCase)))
            {
                float sideDot = Vector3.Dot(Flatten(side.Position - ownerPosition), right);
                result[side.BotProfileId] = sideDot <= 0f ? "left_side_close" : "right_side_close";
            }
        }

        return result;
    }

    private static OperatorDecisionSnapshot? SelectLead(
        string ownerProfileId,
        IReadOnlyList<OperatorDecisionSnapshot> eligible,
        Vector3 ownerPosition,
        Vector3 forward,
        DateTimeOffset now)
    {
        string owner = Safe(ownerProfileId);
        lock (Sync)
        {
            if (LeadByOwner.TryGetValue(owner, out var existing) && existing.UntilUtc > now)
            {
                var preserved = eligible.FirstOrDefault(snapshot => string.Equals(snapshot.BotProfileId, existing.BotProfileId, StringComparison.OrdinalIgnoreCase));
                if (preserved != null)
                {
                    float longitudinal = Vector3.Dot(Flatten(preserved.Position - ownerPosition), forward);
                    bool graceExpiredBehind = now - existing.AssignedAtUtc >= LeadSuccessGrace && longitudinal < FailedLeadLongitudinalMeters;
                    if (!graceExpiredBehind)
                    {
                        return preserved;
                    }
                    LeadByOwner.Remove(owner);
                    VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.DynamicFormationStatusTag,
                        $"VANGUARD_LEAD_TOKEN_REVOKED owner={Safe(ownerProfileId)}; botProfile={Safe(preserved.BotProfileId)}; longitudinal={longitudinal:0.0}; reason=failed_to_reach_lead_after_grace; tag={VanguardPrimaryExecutionContract.DynamicFormationStatusTag}");
                }
            }

            var ordered = eligible
                .Select(snapshot => new { Snapshot = snapshot, Score = LeadScore(snapshot, ownerPosition, forward) })
                .Where(candidate => candidate.Score >= MinimumViableLeadScore)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Snapshot.BotProfileId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (ordered.Length == 0)
            {
                LeadByOwner.Remove(owner);
                return null;
            }

            var selectedCandidate = ordered[0];
            if (LeadByOwner.TryGetValue(owner, out var previous) && ordered.Length > 1)
            {
                int previousIndex = Array.FindIndex(ordered, candidate => string.Equals(candidate.Snapshot.BotProfileId, previous.BotProfileId, StringComparison.OrdinalIgnoreCase));
                if (previousIndex >= 0)
                {
                    selectedCandidate = ordered[(previousIndex + 1) % ordered.Length];
                }
            }
            OperatorDecisionSnapshot selected = selectedCandidate.Snapshot;
            LeadByOwner[owner] = new LeadTokenState(selected.BotProfileId, now, now + LeadTokenDuration);
            VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.DynamicFormationStatusTag,
                $"VANGUARD_LEAD_TOKEN_ASSIGNED owner={Safe(ownerProfileId)}; operator={Safe(selected.OperatorId)}; botProfile={Safe(selected.BotProfileId)}; duration={LeadTokenDuration.TotalSeconds:0.0}; score={LeadScore(selected, ownerPosition, forward):0.0}; doctrine=real_position_based_bounded_rotation_not_callsign_order; tag={VanguardPrimaryExecutionContract.DynamicFormationStatusTag}");
            return selected;
        }
    }

    private static float LeadScore(OperatorDecisionSnapshot snapshot, Vector3 ownerPosition, Vector3 forward)
    {
        Vector3 offset = Flatten(snapshot.Position - ownerPosition);
        float longitudinal = Vector3.Dot(offset, forward);
        float distance = offset.magnitude;
        float healthPenalty = snapshot.Medical.Need.HealthPercent > 0
            ? Math.Max(0f, 75f - snapshot.Medical.Need.HealthPercent) * 0.22f
            : 0f;
        float injuryPenalty = snapshot.Medical.Need.HasFracture || snapshot.Medical.Need.HasBlackBroken ? 12f : 0f;
        return longitudinal * 2.2f - Math.Abs(distance - 8.0f) * 0.65f - healthPenalty - injuryPenalty;
    }

    private static Vector3 Flatten(Vector3 value) => new(value.x, 0f, value.z);

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }

    private readonly struct LeadTokenState
    {
        public LeadTokenState(string botProfileId, DateTimeOffset assignedAtUtc, DateTimeOffset untilUtc)
        {
            BotProfileId = botProfileId;
            AssignedAtUtc = assignedAtUtc;
            UntilUtc = untilUtc;
        }

        public string BotProfileId { get; }
        public DateTimeOffset AssignedAtUtc { get; }
        public DateTimeOffset UntilUtc { get; }
    }
}
#endif
