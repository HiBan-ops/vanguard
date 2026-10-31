#if SPT_CLIENT
using System;
using System.Linq;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;

// Responsibility: Centralizes the non-mutating local safety truth shared by split-flank alternate establishment and Direct hold-release readiness.
// Flow: Fresh Operator decision evidence plus owner/threat provenance is checked against survival, execution, SAIN and competing-threat vetoes before split-flank state may be treated as locally safe; Direct-member callers may preserve only an exact already-owned DirectCommit primary window for the same threat.
// Authority boundary: This policy only classifies current evidence; it cannot request translation, open scheduler windows, release holds, mutate SAIN or move/fire an Operator.
// Invariant: Direct readiness is never inferred from AltDevelop translation compatibility, and missing/stale SAIN or decision evidence fails closed; the owned-DirectCommit exemption cannot admit any foreign primary window, different threat or missing scheduler context.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class SplitFlankSafetyPolicy
{
    public static string Evaluate(
        OperatorDecisionSnapshot snapshot,
        string ownerProfileId,
        string threatId,
        DateTimeOffset memberEvidenceCapturedAtUtc,
        DateTimeOffset now,
        bool allowExactOwnedDirectCommitPrimary = false)
    {
        if (snapshot == null || !snapshot.Alive) return "operator_dead_or_missing";
        if (!string.Equals(snapshot.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)) return "owner_identity_changed";
        if (memberEvidenceCapturedAtUtc == DateTimeOffset.MinValue) return "member_evidence_generation_missing";
        if (snapshot.CapturedAtUtc < memberEvidenceCapturedAtUtc) return "decision_snapshot_precedes_member_evidence";
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(snapshot, now, out var freshnessReason)) return freshnessReason;
        if (snapshot.Sain == null || !snapshot.Sain.ComponentPresent) return "sain_component_missing";
        if (snapshot.Sain.Active != true) return "sain_inactive_or_unknown";
        if (snapshot.MovementAuthority.HardOutsideBubble) return "hard_outside_bubble_recovery_authority";
        if (snapshot.GrenadeHazard.HasRelevantHazard && !snapshot.GrenadeHazard.IsSafetyEnvelopeSatisfied) return "grenade_hazard_active";
        if (VanguardExecutionLeaseCoordinator.HasActiveLease(snapshot.BotProfileId)) return "execution_lease_active";
        if (snapshot.Medical.Need.HasAnyNeed && snapshot.Medical.Plan.WouldExecuteIfActive) return "medical_execution_pending";
        if (snapshot.PrimaryExecution.Active
            && !VanguardPrimaryExecutionContract.IsCombatPrimaryKind(snapshot.PrimaryExecution.WindowKind)
            && !IsYieldableAutomaticAuthoringPreview(snapshot, now)
            && (!allowExactOwnedDirectCommitPrimary || !IsExactOwnedDirectCommitPrimary(snapshot, threatId, now)))
        {
            return "noncombat_primary_execution_active:" + Safe(snapshot.PrimaryExecution.WindowKind);
        }
        if (IsReloading(snapshot.BotProfileId)) return "reload_active";
        if (!IsNoneToken(snapshot.Sain.SelfDecision)) return "sain_self_decision_active:" + Safe(snapshot.Sain.SelfDecision);
        if (snapshot.Sain.RunningToCover == true) return "sain_running_to_cover";
        if (IsHardDefensiveCombatDecision(snapshot.Sain.CombatDecision)) return "sain_local_defensive_decision:" + Safe(snapshot.Sain.CombatDecision);
        return CompetingThreatReason(snapshot, threatId);
    }

    private static bool IsYieldableAutomaticAuthoringPreview(OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        return snapshot.PrimaryExecution.Active
            && string.Equals(snapshot.PrimaryExecution.WindowKind, VanguardPrimaryExecutionWindowKinds.AuthoringPreviewMovement, StringComparison.OrdinalIgnoreCase)
            && string.Equals(snapshot.PrimaryExecution.IntentKey, "TacticalAuthoringLivePreview", StringComparison.OrdinalIgnoreCase)
            && VanguardMainIntentScheduler.HasActiveAutomaticAuthoringPreview(snapshot.BotProfileId, now);
    }

    private static bool IsExactOwnedDirectCommitPrimary(OperatorDecisionSnapshot snapshot, string threatId, DateTimeOffset now)
    {
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            return false;
        }
        if (!VanguardMainIntentScheduler.TryGetActiveTacticalCoordinatorDirectCommitContext(
                snapshot.BotProfileId,
                now,
                out string activeThreatId,
                out long activePlanEpoch,
                out _))
        {
            return false;
        }
        return activePlanEpoch > 0
            && string.Equals(Normalize(activeThreatId), Normalize(threatId), StringComparison.OrdinalIgnoreCase);
    }

    private static string CompetingThreatReason(OperatorDecisionSnapshot snapshot, string threatId)
    {
        string expected = Normalize(threatId);
        string local = Normalize(snapshot.Threat.EnemyId);
        bool locallyRelevant = snapshot.Threat.DirectThreat || snapshot.Threat.ResidualThreat;
        if (locallyRelevant && local.Length == 0) return "relevant_local_threat_identity_unknown";
        if (locallyRelevant && !string.Equals(local, expected, StringComparison.OrdinalIgnoreCase)) return "competing_relevant_threat_identity_changed";
        if (snapshot.Sain.HasEnemy == true && local.Length > 0 && !string.Equals(local, expected, StringComparison.OrdinalIgnoreCase)) return "competing_sain_goal_identity_changed";
        return string.Empty;
    }

    private static bool IsReloading(string botProfileId)
    {
        return VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botProfileId, out var runtime)
            && runtime.BotOwner?.WeaponManager?.Reload?.Reloading == true;
    }

    private static bool IsHardDefensiveCombatDecision(string? value)
    {
        string normalized = NormalizeToken(value);
        return normalized is "seekcover" or "runningtocover" or "avoidgrenade" or "runaway" or "retreat";
    }

    private static bool IsNoneToken(string? value)
    {
        string normalized = NormalizeToken(value);
        return normalized.Length == 0 || normalized is "none" or "nodecision" or "null";
    }

    private static string NormalizeToken(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
