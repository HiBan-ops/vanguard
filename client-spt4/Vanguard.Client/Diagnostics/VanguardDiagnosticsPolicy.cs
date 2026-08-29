using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

// Responsibility: Encodes the deterministic rules for Diagnostics Policy within the client diagnostics.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Diagnostics;

internal enum VanguardAuditLevel
{
    Off = 0,
    Operational = 1,
    Diagnostic = 2,
    Trace = 3
}

/// <summary>
/// Central observability policy for the owning runtime subsystem. Gameplay code never reads this policy to decide AI actions;
/// it only controls which already-produced diagnostics are emitted. Repetitive low-level families
/// are counted and summarized instead of flooding the headless log.
/// </summary>
internal static class VanguardDiagnosticsPolicy
{
    public const string TransitionAggregationStatusTag = "VANGUARD_DIAGNOSTIC_TRANSITION_AGGREGATION_STATUS";
    private static readonly object Sync = new();
    private static readonly Dictionary<string, long> SuppressedByFamily = new(StringComparer.OrdinalIgnoreCase);
    private static int levelValue = (int)VanguardAuditLevel.Operational;
    private static DateTimeOffset lastSummaryUtc = DateTimeOffset.MinValue;

    public static VanguardAuditLevel Level => (VanguardAuditLevel)Volatile.Read(ref levelValue);

    public static void SetLevel(string? value)
    {
        Volatile.Write(ref levelValue, (int)Parse(value));
    }

    public static string LevelName => Level.ToString();

    public static bool IsEnabled(VanguardAuditLevel minimumLevel)
    {
        VanguardAuditLevel current = Level;
        return current != VanguardAuditLevel.Off && current >= minimumLevel;
    }

    public static bool ShouldEmit(string tag, VanguardAuditLevel minimumLevel)
    {
        VanguardAuditLevel current = Level;
        if (current == VanguardAuditLevel.Off)
        {
            return false;
        }

        if (current >= minimumLevel)
        {
            return true;
        }

        // Operational is the normal runtime profile. Do not acquire the suppression-counter
        // lock for every disabled Diagnostic/Trace probe; summaries are useful only while
        // actively investigating at Diagnostic level.
        if (current >= VanguardAuditLevel.Diagnostic)
        {
            RegisterSuppressed(string.IsNullOrWhiteSpace(tag) ? "untagged" : tag);
        }

        return false;
    }

    public static VanguardAuditLevel MinimumLevelForLegacy(string tag, string message) =>
        MinimumLevel(tag, message);

    public static VanguardAuditLevel MinimumLevelForOperational(string tag) =>
        IsNormalRuntimeDiagnosticFamily(tag) ? VanguardAuditLevel.Diagnostic : VanguardAuditLevel.Operational;

    public static VanguardAuditLevel MinimumLevelForWarning(string tag, string message)
    {
        // Several Warning call sites describe bounded, self-healing runtime
        // observations rather than player-actionable faults. Keep those families available in
        // Diagnostic/Trace while preserving genuinely actionable warnings and all errors.
        if (ContainsAny(tag, message,
                "VANGUARD_COMBAT_NO_FIRE_OBSERVED",
                "VANGUARD_RUNTIME_HOTSPOT",
                "VANGUARD_FRIENDLY_FIRE_BLOCKED",
                "VANGUARD_SAIN_WINDOW_STARTED",
                "VANGUARD_PATH_INVALID_FEEDBACK",
                "VANGUARD_FRAME_BUDGET_SUMMARY",
                "MEDICAL_CANONICAL_TYPED_DIVERGENCE",
                "VANGUARD_WEAPON_HANDS_STATE_SUSPECT",
                "VANGUARD_BURST_TRIGGER_RELEASED",
                "VANGUARD_RUNTIME_BIND_PENDING_SCAN",
                "VANGUARD_TARGET_CLEAR_REACQUIRED",
                "VANGUARD_TARGET_CLEAR_UNCONFIRMED_BACKOFF",
                "VANGUARD_PRIMARY_DOMAIN_PREEMPTED",
                "VANGUARD_GRENADE_WINDOW_CONVERGENCE_STATUS",
                "VANGUARD_TYPED_LOOKUP_BUDGET_YIELD",
                "VANGUARD_TRAVEL_STALE_GENERATION_RELEASED"))
        {
            return VanguardAuditLevel.Diagnostic;
        }

        return VanguardAuditLevel.Operational;
    }

    public static bool DrainSuppressionSummary(string reason, out string summary)
    {
        summary = string.Empty;
        lock (Sync)
        {
            if (SuppressedByFamily.Count == 0)
            {
                lastSummaryUtc = DateTimeOffset.MinValue;
                return false;
            }

            long total = SuppressedByFamily.Values.Sum();
            string families = string.Join(",", SuppressedByFamily
                .OrderByDescending(pair => pair.Value)
                .Take(12)
                .Select(pair => Sanitize(pair.Key) + ":" + pair.Value));
            summary = $"VANGUARD_AUDIT_SUPPRESSION_SUMMARY level={Level}; total={total}; families={families}; final=true; reason={Sanitize(reason)}; gameplayUnaffected=true";
            SuppressedByFamily.Clear();
            lastSummaryUtc = DateTimeOffset.MinValue;
            return true;
        }
    }

    public static bool TryBuildSuppressionSummary(DateTimeOffset now, out string summary)
    {
        summary = string.Empty;
        lock (Sync)
        {
            if (SuppressedByFamily.Count == 0 || now - lastSummaryUtc < TimeSpan.FromSeconds(60.0d))
            {
                return false;
            }

            long total = SuppressedByFamily.Values.Sum();
            string families = string.Join(",", SuppressedByFamily
                .OrderByDescending(pair => pair.Value)
                .Take(8)
                .Select(pair => Sanitize(pair.Key) + ":" + pair.Value));
            summary = $"VANGUARD_AUDIT_SUPPRESSION_SUMMARY level={Level}; total={total}; families={families}; windowSeconds=60; gameplayUnaffected=true";
            SuppressedByFamily.Clear();
            lastSummaryUtc = now;
            return true;
        }
    }

    public static VanguardAuditLevel Parse(string? value)
    {
        if (Enum.TryParse(value, ignoreCase: true, out VanguardAuditLevel parsed))
        {
            return parsed;
        }

        return VanguardAuditLevel.Operational;
    }

    private static VanguardAuditLevel MinimumLevel(string tag, string message)
    {
        // An internal execution lease reported as "failed" can still be a normal,
        // self-healing interruption. Keep the known high-frequency interruption paths in
        // Diagnostic so the generic failure guard below does not turn routine lease churn
        // into player-facing Operational noise.
        if (ContainsAny(tag, message, "VANGUARD_EXECUTION_FAILED")
            && ContainsAny(tag, message, "outcome=Interrupted")
            && ContainsAny(tag, message,
                "route_target_anchor_jump_requires_fresh_lease",
                "tactical_authoring_preview_release:"))
        {
            return VanguardAuditLevel.Diagnostic;
        }

        // Known candidate/lease rejections are qualification telemetry, not player-action
        // refusals. They must win before the generic _REJECTED failure preservation below.
        if (ContainsAny(tag, message,
                "VANGUARD_AWARENESS_BRIDGE_REJECTED",
                "VANGUARD_LOOT_GRANT_REJECTED"))
        {
            return VanguardAuditLevel.Trace;
        }

        // Operational keeps only transitions with direct player/support value.
        // Generic scheduler windows, target handoffs, interior volume changes and lease lifecycle
        // events are internal orchestration and remain available in Diagnostic/Trace.
        if (ContainsAny(tag, message,
                "VANGUARD_STATIONARY_SURGERY_STARTED"))
        {
            return VanguardAuditLevel.Operational;
        }

        // Interrupted execution windows are normal arbitration/replanning outcomes. A real
        // execution failure still reaches the actionable-failure classifier below.
        if (ContainsAny(tag, message, "VANGUARD_EXECUTION_FAILED")
            && ContainsAny(tag, message, "outcome=Interrupted"))
        {
            return VanguardAuditLevel.Diagnostic;
        }

        // Corpse-loot prepare rejection and surgery-cover move rejection are bounded candidate
        // qualification outcomes. They are useful for support diagnostics but not normal-player
        // Operational warnings.
        if (ContainsAny(tag, message,
                "VANGUARD_CORPSE_LOOT_TRANSACTION_PREPARE_REJECTED",
                "VANGUARD_SURGERY_COVER_MOVE_REJECTED"))
        {
            return VanguardAuditLevel.Diagnostic;
        }

        // Repeated medical external-authority retries can legitimately report
        // FailedPathStillActive while another path is quiescing. They are self-healing
        // arbitration progress, not a player-actionable failure. Keep them in Diagnostic;
        // any other external-authority failure still falls through to the actionable guard.
        if (ContainsAny(tag, message,
                "VANGUARD_EXTERNAL_AUTHORITY_FAILED",
                "VANGUARD_AUTHORITY_EXTERNAL_FAILED",
                "VANGUARD_RETURN_CONTINUATION_PREEMPT_FAILED")
            && ContainsAny(tag, message, "externalPreempt=FailedPathStillActive"))
        {
            return VanguardAuditLevel.Diagnostic;
        }

        // Failures and explicit user-action refusals remain Operational. The semantic guard identifies
        // the primary event/result rather than scanning every metadata token: fields such as
        // typedFailureTag=..._FAILURE_TYPING_OK or reason=selection_failed must not promote an
        // otherwise routine Diagnostic event back into Operational. Warning/Error overloads
        // remain always visible independently of this Info-path classifier.
        if (HasActionableFailureSignal(tag, message))
        {
            return VanguardAuditLevel.Operational;
        }

        // One-time feature boot declarations remain available for support, while normal Operational startup stays concise.
        // Actionable failures/refusals are classified above and therefore remain visible.
        if ((message?.StartsWith("VANGUARD_", StringComparison.OrdinalIgnoreCase) ?? false)
            && message.IndexOf("_BOOT", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return VanguardAuditLevel.Diagnostic;
        }

        // Public operational profile: repeated progression, arbitration and read-model observations remain
        // available in Diagnostic/Trace, but are not normal-player Operational events. Explicit
        // failures/refusals and the small operational transition allow-list above still win first.
        if (ContainsAny(tag, message,
                "VANGUARD_STATIONARY_MEDICAL_DEFERRED_TRANSITION",
                "VANGUARD_ARMAMENT_DEFICIT_SQUAD_PRIORITY",
                "VANGUARD_EXTERNAL_AUTHORITY_GRANTED",
                "VANGUARD_AUTHORITY_EXTERNAL_GRANTED",
                "VANGUARD_EXTERNAL_AUTHORITY_RELEASED",
                "VANGUARD_EXTERNAL_SUPPRESS_RELEASED",
                "VANGUARD_COMBAT_AUTHORITY_RELEASE_APPLIED",
                "VANGUARD_COMBAT_WINDOW_CLEANUP_APPLIED",
                "VANGUARD_OWNER_ENVIRONMENT_STABILIZED",
                "VANGUARD_INTERIOR_ASSIGNMENT_APPLIED",
                "VANGUARD_SQUAD_CONTACT_QUALIFIED",
                "VANGUARD_MOVE_BRIDGE_COMMAND_ACCEPTED",
                "VANGUARD_MOVE_BRIDGE_COMMAND_CLEARED",
                "VANGUARD_MOVE_COMMAND_OWNED_CLEAR",
                "VANGUARD_MOVE_COMMAND_HANDOFF",
                "VANGUARD_MOVE_COMMAND_CLEAR_PROTECTED",
                "VANGUARD_TRAVEL_MODE_HYSTERESIS",
                "VANGUARD_TRAVEL_PARAMETERS_UPDATED",
                "VANGUARD_TRAVEL_CORRIDOR_EXECUTION_STARTED",
                "VANGUARD_LEAD_TOKEN_ASSIGNED",
                "VANGUARD_LEAD_TOKEN_REVOKED",
                "VANGUARD_SAIN_STALE_EXIT_CONFIRMING",
                "VANGUARD_SAIN_STALE_EXIT_READINESS",
                "VANGUARD_SAIN_STALE_EXIT_OBSERVED",
                "VANGUARD_SAIN_STALE_EXIT_CLEARED",
                "VANGUARD_CANONICAL_MEDICAL_CONVERGENCE_STATUS",
                "VANGUARD_MEDICAL_CANONICAL_SELECTION_OVERLAY",
                "VANGUARD_MEDICAL_FOREIGN_ACTIVITY_PRESERVED",
                "VANGUARD_CORPSE_LOOT_APPROACH_PROGRESS",
                "VANGUARD_CORPSE_LOOT_APPROACH_BLOCKED",
                "VANGUARD_CORPSE_LOOT_WINDOW_OPENED",
                "VANGUARD_CORPSE_LOOT_APPROACH_STARTED",
                "VANGUARD_CORPSE_LOOT_SESSION_ACQUIRED",
                "VANGUARD_CORPSE_LOOT_APPROACH_HANDOFF",
                "VANGUARD_CORPSE_LOOT_TRANSACTION_PREPARED",
                "VANGUARD_CORPSE_LOOT_TRANSACTION_SUBMIT_ATTEMPTED",
                "VANGUARD_CORPSE_LOOT_TRANSACTION_SUBMIT_CALL_RETURNED",
                "VANGUARD_CORPSE_LOOT_TRANSACTION_CALLBACK",
                "VANGUARD_CORPSE_ARRIVAL_COMMAND_TERMINAL_GRACE",
                "VANGUARD_CORPSE_ARRIVAL_HANDOFF_CONVERGED",
                "VANGUARD_CORPSE_LOOT_NONCRITICAL_WINDOW_SUPERSEDED",
                "VANGUARD_SECONDARY_REPLACEMENT_PREVIEW",
                "VANGUARD_COMBAT_NO_FIRE_WATCHDOG_OBSERVED",
                "VANGUARD_COMBAT_NO_FIRE_OBSERVED",
                "VANGUARD_PATH_INVALID_FEEDBACK",
                "VANGUARD_DISTANT_PURSUIT_SUPPRESSED",
                "VANGUARD_DISTANT_GOAL_RELEASE",
                "VANGUARD_UNIFIED_ASSIGNMENT_OBSERVE",
                "VANGUARD_OPERATOR_ASSIGNMENT",
                "VANGUARD_WEAPON_SHOT_ACTION_DISPATCH",
                "VANGUARD_OPERATOR_EVENT_SUMMARY",
                "VANGUARD_CAREER_TRUTH_XP",
                "VANGUARD_CAREER_TRUTH_STATS",
                "VANGUARD_CAREER_TRUTH_SKILLS",
                "VANGUARD_CAREER_TRUTH_RELIABILITY"))
        {
            return VanguardAuditLevel.Diagnostic;
        }

        // Recurrent qualification/telemetry families remain available when
        // Diagnostic is explicitly selected, but they no longer flood the normal Operational log.
        if (IsNormalRuntimeDiagnosticFamily(tag))
        {
            return VanguardAuditLevel.Diagnostic;
        }

        // Detailed boot declarations remain available only when Diagnostic is explicitly selected.
        // Operational emits the current version/parity line and real runtime events instead of startup detail.
        if ((message?.StartsWith("VANGUARD_", StringComparison.OrdinalIgnoreCase) ?? false)
            && (message.IndexOf("_OK:", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf(": active;", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return VanguardAuditLevel.Diagnostic;
        }

        // High-volume invariant probes remain available in Diagnostic/Trace for troubleshooting,
        // but normal Operational raids pay no write cost for those repetitive details.
        if (ContainsAny(tag, message,
                "VANGUARD_OPERATOR_INVENTORY_REFRESH_APPLIED",
                "VANGUARD_SAIN_AUTONOMOUS_EXTRACT_VETO",
                "VANGUARD_COOP_FRIENDLY_TARGET_GUARD_SUMMARY",
                "VANGUARD_MEDICAL_FAST_PROCEDURE_ENTRY"))
        {
            return VanguardAuditLevel.Diagnostic;
        }

        if (ContainsAny(tag, message,
                "VANGUARD_LOOT_PREVENTED",
                "VANGUARD_LOOT_GRANT_REJECTED",
                "VANGUARD_MOVE_COMMAND_RETARGETED",
                "VANGUARD_MOVE_BRIDGE_COMMAND_REFRESHED",
                "VANGUARD_MOVE_BRIDGE_LAYER_ACTIVE",
                "VANGUARD_ACTIVE_RETURN_COMMAND_PRESERVED",
                "VANGUARD_TRAVEL_ROUTE_RETARGET_APPLIED",
                "VANGUARD_TRAVEL_PHYSICAL_PROGRESS",
                "VANGUARD_SAIN_WINDOW_OBSERVED",
                "VANGUARD_MEDICAL_PRIMARY_DEFERRED",
                "VANGUARD_STALE_SAIN_OBSERVED",
                "VANGUARD_COHESION_CLAIM_FREEZE",
                "VANGUARD_CLAIM_ASSIGNED",
                "VANGUARD_AWARENESS_BRIDGE_REJECTED",
                "VANGUARD_ORBIT_AUTHORITY_QUIESCE"))
        {
            return VanguardAuditLevel.Trace;
        }

        // Runtime invariant: these are high-frequency state-refresh diagnostics, not gameplay transitions.
        // Keep terminal outcomes and safety vetoes Operational; expose refresh detail only when
        // Diagnostic/Trace is explicitly selected through F12.
        if (ContainsAny(tag, message,
                "VANGUARD_MEDICAL_HARD_PROCEDURE_AUTHORITY_REFRESHED",
                "VANGUARD_MEDICAL_AUTHORITY_HOLD_REFRESHED",
                "VANGUARD_MEDICAL_HARD_PROCEDURE_CURRENT_POSITION_COMMIT",
                "VANGUARD_MEDICAL_COVER_COMMITTED_FROM_READY",
                "VANGUARD_STATIONARY_MEDICAL_PREPARE_DEFERRED_EXECUTOR_BOUNDARY",
                "VANGUARD_MEDICAL_PREPARE_SURGERY_COVER_ATTEMPTED",
                "VANGUARD_MEDICAL_PREPARE_SURGERY_COVER_WAITING",
                "VANGUARD_MEDICAL_PREPARE_SURGERY_COVER_SKIP",
                "VANGUARD_EXTERNAL_ACTIVITY_SNAPSHOT",
                "VANGUARD_CLAIM_LEASE_PROGRESS",
                "VANGUARD_GOTOSOMEPOINT_DRIVE",
                "VANGUARD_GLOBAL_COMBAT_PRODUCTION_DETAILED_SUMMARY"))
        {
            return VanguardAuditLevel.Diagnostic;
        }

        if (ContainsAny(tag, message,
                "VANGUARD_INTENT_SELECTED",
                "VANGUARD_EXECUTION_PROGRESS"))
        {
            return VanguardAuditLevel.Trace;
        }

        if (ContainsAny(tag, message,
                "VANGUARD_HOSTILITY_MATRIX_FORCE",
                "VANGUARD_HOSTILITY_AUDIT",
                "VANGUARD_SQUAD_CONTACT_BROADCAST",
                "VANGUARD_MEDICAL_SURGERY_PREFLIGHT_UNAVAILABLE",
                "VANGUARD_CLAIM_LEASE_BLOCKED",
                "VANGUARD_COHESION_FREEZE",
                "VANGUARD_CLAIM_REUSED",
                "VANGUARD_CLAIM_REFRESH_NEEDED",
                "VANGUARD_SQUAD_CONTACT_ALREADY_ASSIGNED",
                "VANGUARD_CLOSE_THREAT_ALREADY_ASSIGNED",
                "VANGUARD_COMBAT_CHAIN_IDEMPOTENT_HOLD",
                "VANGUARD_INTERIOR_VOLUME_HOLD_PRESERVED",
                "VANGUARD_INTERIOR_VOLUME_LEASE_PRESERVED",
                "VANGUARD_THREAT_SCAN_SIDECAR_DRYRUN",
                "VANGUARD_OPERATOR_DECISION_SNAPSHOT",
                "VANGUARD_SNAPSHOT_SUMMARY"))
        {
            return VanguardAuditLevel.Trace;
        }

        if (ContainsAny(tag, message,
                "_PLANNED",
                "_CANDIDATE",
                "_PREFLIGHT",
                "_RECHECK",
                "_BLOCKED",
                "_DEFERRED",
                "_THROTTLED",
                "_KEPT",
                "_PENDING",
                "_SEGMENT_RENEWED",
                "_CONTROLLER_USING_GRACE",
                "_ORIENTATION_",
                "VANGUARD_INTERIOR_VOLUME_SCAN_EMPTY"))
        {
            return VanguardAuditLevel.Diagnostic;
        }

        // Operational is allow-list driven. Unknown/new Info telemetry defaults to
        // Diagnostic so future instrumentation cannot silently reintroduce multi-line-per-second
        // player-facing noise. Explicit Operational transitions, actionable failures and all
        // Warning/Error overloads have already been preserved above.
        return VanguardAuditLevel.Diagnostic;
    }

    private static bool HasActionableFailureSignal(string? tag, string? message)
    {
        // Info-path failures must be identified from the primary semantic event, not from nested
        // diagnostic snapshots. Warning/Error overloads are governed separately.
        string primaryEvent = PrimaryEventToken(tag, message);
        return ContainsAny(primaryEvent, null,
            "_FAILED",
            "_FAILURE",
            "_REFUSED",
            "_REJECTED",
            "_ROLLBACK",
            "_CORRUPT");
    }

    private static string PrimaryEventToken(string? tag, string? message)
    {
        string safeMessage = message?.TrimStart() ?? string.Empty;
        const string VanguardPrefix = "VANGUARD_";

        // Prefer the first semantic Vanguard event in the payload. Some call sites start
        // with event=<status-family>; followed by the actual VANGUARD_* transition.
        int vanguardIndex = safeMessage.IndexOf(VanguardPrefix, StringComparison.OrdinalIgnoreCase);
        if (vanguardIndex >= 0)
        {
            int end = vanguardIndex;
            while (end < safeMessage.Length
                   && (char.IsLetterOrDigit(safeMessage[end]) || safeMessage[end] == '_'))
            {
                end++;
            }

            if (end > vanguardIndex)
            {
                return safeMessage.Substring(vanguardIndex, end - vanguardIndex);
            }
        }

        const string EventPrefix = "event=";
        int eventIndex = safeMessage.IndexOf(EventPrefix, StringComparison.OrdinalIgnoreCase);
        if (eventIndex >= 0)
        {
            int start = eventIndex + EventPrefix.Length;
            int end = start;
            while (end < safeMessage.Length
                   && safeMessage[end] != ';'
                   && !char.IsWhiteSpace(safeMessage[end]))
            {
                end++;
            }

            if (end > start)
            {
                return safeMessage.Substring(start, end - start);
            }
        }

        return tag ?? string.Empty;
    }

    private static bool IsNormalRuntimeDiagnosticFamily(string? tag) =>
        ContainsAny(tag, null,
            // In-raid presentation and Fika transport summaries.
            "VANGUARD_OPERATOR_HUD_STATUS",
            "VANGUARD_AUTHORITATIVE_FIKA_HUD_TELEMETRY_STATUS",
            "VANGUARD_HUD_MEDICAL_TRANSPORT_ISOLATION_STATUS",

            // Off-raid successful lifecycle/state refresh traces. Failures/refusals are preserved above.
            "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
            "VANGUARD_OFFRAID_UI_STATUS",
            "VANGUARD_OPERATOR_EQUIPMENT_BUILDS_STATUS",
            "VANGUARD_OPERATOR_INVENTORY_EXIT_RELOAD_STATUS",
            "VANGUARD_OFFRAID_UI_FOUNDATION_STATUS",
            "VANGUARD_OPERATOR_IDENTITY_CANONICAL_STATUS",
            "VANGUARD_OFFRAID_BILLING_FLOW_STATUS",
            "VANGUARD_OFFRAID_SERVICE_STATE_LABEL_STATUS",
            "VANGUARD_OFFRAID_UI_THEME_STATUS",
            "VANGUARD_OPERATOR_INVENTORY_UI_ENTRY_STATUS",
            "VANGUARD_OPERATOR_SESSION_PROFILE_NORMALIZATION_STATUS",
            "VANGUARD_OPERATOR_PLAYER_STASH_REFRESH_STATUS",

            // High-volume read-only qualification and telemetry probes.
            "VANGUARD_CONTAINER_SCORING_AND_SQUAD_ALLOCATION_INTEGRATION_STATUS",
            "VANGUARD_SKILL_AND_PHYSICAL_TELEMETRY_STATUS",
            "VANGUARD_TACTICAL_AUTHORING_HEADLESS_PREVIEW_STATUS",
            "VANGUARD_CONTAINER_CLAIM_APPROACH_OPEN_PROOF_STATUS",
            "VANGUARD_WORLD_CONTAINER_READ_MODEL_STATUS",
            "VANGUARD_UTILITY_CLAIMED_LOOT_ACTIVATION_STATUS",
            "VANGUARD_UNIFIED_OPPORTUNISTIC_LOOT_READ_MODEL_STATUS",
            "VANGUARD_NATIVE_SKILL_ACTION_ACQUISITION_STATUS",
            "VANGUARD_STRENGTH_ENDURANCE_INSTRUMENTATION_STATUS",
            "VANGUARD_OPERATOR_BOT_TYPES_STATUS",
            "VANGUARD_OPERATOR_BRAIN_BIND_STATUS",
            "VANGUARD_CORPSE_LOOT_QUALIFICATION_STATUS",
            "VANGUARD_NATIVE_SAIN_SQUAD_FOUNDATION_STATUS",
            "VANGUARD_ZERO_LONG_WEAPON_ACQUISITION_CAPACITY_STATUS",
            "VANGUARD_ARMAMENT_DEFICIT_SQUAD_PRIORITY_STATUS",
            "VANGUARD_SECONDARY_WEAPON_REPLACEMENT_STATUS",
            "VANGUARD_MEDICAL_EPISODE_IDEMPOTENCE_STATUS",
            "VANGUARD_MOVE_BRIDGE_LAYER_OK",
            "VANGUARD_SQUAD_TRAVEL_COMBAT_AUTHORITY_STATUS");

    private static void RegisterSuppressed(string family)
    {
        lock (Sync)
        {
            SuppressedByFamily.TryGetValue(family, out long count);
            SuppressedByFamily[family] = count + 1;
        }
    }

    private static bool ContainsAny(string? tag, string? message, params string[] needles)
    {
        string safeTag = tag ?? string.Empty;
        string safeMessage = message ?? string.Empty;
        foreach (string needle in needles)
        {
            if (MatchesNeedle(safeTag, needle) || MatchesNeedle(safeMessage, needle))
            {
                return true;
            }
        }
        return false;
    }

    private static bool MatchesNeedle(string value, string needle)
    {
        if (value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        // Diagnostic payloads can expose either a full VANGUARD_ event or its semantic suffix.
        // Classification recognizes both representations. This affects diagnostics only and never feeds gameplay decisions.
        const string VanguardPrefix = "VANGUARD_";
        if (needle.StartsWith(VanguardPrefix, StringComparison.OrdinalIgnoreCase)
            && needle.Length > VanguardPrefix.Length)
        {
            string semanticNeedle = needle.Substring(VanguardPrefix.Length);
            return value.IndexOf(semanticNeedle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        return false;
    }

    private static string Sanitize(string value) => value.Replace(' ', '_').Replace(';', '_').Replace(',', '_');
}
