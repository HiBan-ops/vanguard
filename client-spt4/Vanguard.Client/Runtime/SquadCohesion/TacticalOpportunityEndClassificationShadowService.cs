#if SPT_CLIENT
using System;
using Vanguard.Client.Diagnostics;

// Responsibility: Classifies completed Tactical Coordination opportunity leases for read-only runtime calibration.
// Flow: A terminal opportunity outcome and its already-produced reason are mapped into a bounded semantic category and emitted lazily at Diagnostic level.
// Authority boundary: Observation only; this service never changes movement, target, aim, fire, scheduler, lease, continuity or support state.
// Invariant: Classification cannot alter the existing opportunity outcome; ambiguous interruption evidence is conservatively classified as hard invalidation until runtime evidence supports promotion.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class TacticalOpportunityEndClassificationShadowService
{
    public const string StatusTag = "VANGUARD_TC_OPPORTUNITY_END_CLASSIFICATION_SHADOW";

    private const int MaxDiagnosticFieldLength = 160;

    internal static void ObserveEnd(
        string? kind,
        string? ownerProfileId,
        string? threatId,
        string? botProfileId,
        string? leaseId,
        string? outcome,
        string? reason,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        int nativePathTicks,
        bool cooldownRequested)
    {
        string classification = Classify(outcome, reason, out string basis);
        double ageSeconds = Math.Max(0d, (endedAtUtc - startedAtUtc).TotalSeconds);

        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
            $"TC_OPPORTUNITY_END_CLASSIFICATION_SHADOW classification={classification}; basis={Bounded(basis)}; kind={Bounded(kind)}; owner={Bounded(ownerProfileId)}; threat={Bounded(threatId)}; bot={Bounded(botProfileId)}; lease={Bounded(leaseId)}; sourceOutcome={Bounded(outcome)}; sourceReason={Bounded(reason)}; leaseAgeSeconds={ageSeconds:0.00}; nativePathTicks={nativePathTicks}; cooldownRequested={Bool(cooldownRequested)}; shadowOnly=true; behaviorMutation=false; continuityMutation=false; targetMutation=false; aimMutation=false; fireCommand=false");
    }

    internal static string Classify(string? outcome, string? reason, out string basis)
    {
        string normalizedOutcome = Normalize(outcome);
        string normalizedReason = Normalize(reason);

        if (string.Equals(normalizedOutcome, "Completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedReason, "destination_reached", StringComparison.OrdinalIgnoreCase))
        {
            basis = "destination_reached";
            return "ARRIVAL";
        }

        if (string.Equals(normalizedOutcome, "Failed", StringComparison.OrdinalIgnoreCase)
            || HasAny(normalizedReason,
                "no_progress",
                "physical_path_invalid",
                "physical_backend_failure",
                "sprint_bridge_failed",
                "steering_bridge_failed",
                "sain_path_bind_failed",
                "path_start_failed",
                "bind_exception",
                "prepared_path_not_complete",
                "initialize_method_missing",
                "initialize_signature_unexpected",
                "destination_reach_method_missing"))
        {
            basis = "physical_execution_or_path_failure";
            return "EXECUTION_FAILURE";
        }

        if (IsClearlyTransientLocalInterruption(normalizedReason, out basis))
        {
            return "SOFT_INTERRUPTION";
        }

        if (HasAny(normalizedReason,
                "runtime_missing_or_dead",
                "runtime_presence_lost",
                "bot_dead",
                "operator_dead_or_missing"))
        {
            basis = "runtime_identity_or_liveness_lost";
            return "HARD_INVALIDATION";
        }

        if (HasAny(normalizedReason,
                "competing_relevant_threat_identity_changed",
                "goal_enemy_changed",
                "goal_enemy_missing",
                "relevant_local_threat_identity_unknown"))
        {
            basis = "exact_threat_context_lost";
            return "HARD_INVALIDATION";
        }

        if (HasAny(normalizedReason,
                "bound_support_changed",
                "threshold_support_context_changed",
                "assault_relay_context_changed",
                "flank_support_context_changed",
                "flank_support_fail_open"))
        {
            basis = "required_support_or_exact_context_lost";
            return "HARD_INVALIDATION";
        }

        if (HasAny(normalizedReason,
                "snapshot_missing",
                "sain_component_missing",
                "command_changed",
                "combat_sidecar_changed",
                "scheduler_sidecar_changed",
                "sain_active_path_identity_changed",
                "tc_destination_changed_inside_lease"))
        {
            basis = "exact_runtime_or_lease_context_replaced";
            return "HARD_INVALIDATION";
        }

        basis = "conservative_unclassified_interruption";
        return "HARD_INVALIDATION";
    }

    private static bool IsClearlyTransientLocalInterruption(string reason, out string basis)
    {
        if (string.Equals(reason, "maximum_lease_duration", StringComparison.OrdinalIgnoreCase))
        {
            basis = "bounded_lease_window_elapsed";
            return true;
        }

        if (HasAny(reason,
                "reload_active",
                "reload_became_active",
                "medical_controller_active",
                "grenade_emergency_or_throw",
                "execution_lease_became_active",
                "sain_running_to_cover",
                "sain_search_action_local_dogfight_visible_target",
                "close_visible_threat_survival_precedes_",
                "action_not_search:"))
        {
            basis = "temporary_local_survival_or_execution_authority";
            return true;
        }

        if (reason.StartsWith("higher_authority:medical_preempt:", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("higher_authority:grenade_emergency:", StringComparison.OrdinalIgnoreCase))
        {
            basis = "temporary_higher_priority_medical_or_grenade_authority";
            return true;
        }

        basis = "not_clearly_transient";
        return false;
    }

    private static bool HasAny(string value, params string[] tokens)
    {
        foreach (string token in tokens)
        {
            if (value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static string Bounded(string? value)
    {
        string safe = string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
        return safe.Length <= MaxDiagnosticFieldLength
            ? safe
            : safe.Substring(0, MaxDiagnosticFieldLength) + "~";
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    private static string Bool(bool value) => value ? "true" : "false";
}
#endif
