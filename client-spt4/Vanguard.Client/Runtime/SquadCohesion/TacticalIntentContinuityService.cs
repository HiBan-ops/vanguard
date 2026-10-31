#if SPT_CLIENT
using System;
using System.Collections.Generic;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;

// Responsibility: preserves one coherent logical Tactical Coordination intention per Operator across short physical lease interruptions.
// Flow: tactical executors register/refresh their logical intent; cohesion/fallback systems query this service before interpreting deliberate tactical separation as drift; terminal or materially-changed intent explicitly releases the protection.
// Authority boundary: this service never moves an Operator, never selects a target and never issues combat commands. It only arbitrates compatibility between already-qualified Vanguard intentions.
// Invariant: a lower-priority cohesion/fallback intention cannot contradict an active protected tactical intention for the same Operator; target change, death, danger-close contact or explicit terminal release ends continuity fail-open.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class TacticalIntentContinuityService
{
    public const string StatusTag = "VANGUARD_TC_INTENT_CONTINUITY_STATUS";
    public const string AltDevelopIntent = "AltDevelop";
    public const string DirectCommitIntent = "DirectCommit";
    public const string OverwatchSupportIntent = "OverwatchSupport";
    public const string BuddySupportIntent = "BuddySupport";
    public const string CombatRegroupIntent = "CombatRegroup";
    public const string EmergencyRegroupIntent = "EmergencyRegroup";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, TacticalIntentContinuityState> ByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ActiveRefreshGrace = TimeSpan.FromSeconds(2.75d);
    private static readonly TimeSpan LocalInterruptionGrace = TimeSpan.FromSeconds(3.5d);

    private static int startsTotal;
    private static int refreshesTotal;
    private static int suspendsTotal;
    private static int resumesTotal;
    private static int resumeRevalidationRejectsTotal;
    private static int terminalsTotal;
    private static int conflictBlocksTotal;
    private static int cohesionBlocksTotal;
    private static int materialInvalidationsTotal;

    internal static bool CanStartIntent(
        string? botProfileId,
        string? threatId,
        string intentKind,
        DateTimeOffset now,
        out string reason)
    {
        string bot = Normalize(botProfileId);
        string threat = Normalize(threatId);
        string kind = NormalizeKind(intentKind);
        if (bot.Length == 0 || kind.Length == 0)
        {
            reason = "intent_identity_missing";
            return false;
        }

        lock (Sync)
        {
            PruneExpiredLocked(bot, now);
            if (!ByBotProfileId.TryGetValue(bot, out var current))
            {
                reason = "no_existing_tactical_intent";
                return true;
            }

            if (string.Equals(current.IntentKind, kind, StringComparison.OrdinalIgnoreCase)
                && (threat.Length == 0 || string.Equals(current.ThreatId, threat, StringComparison.OrdinalIgnoreCase)))
            {
                reason = "same_logical_intent_continuation";
                return true;
            }

            if (!current.ProtectsFromCohesion)
            {
                reason = "existing_fallback_intent_not_protected";
                return true;
            }

            conflictBlocksTotal++;
            reason = "protected_intent_conflict:" + Safe(current.IntentKind) + ":" + Safe(current.ThreatId);
            return false;
        }
    }

    internal static bool CanStartIntent(
        OperatorDecisionSnapshot snapshot,
        string intentKind,
        string logicalKey,
        DateTimeOffset now,
        out bool qualifiedResume,
        out string reason)
    {
        qualifiedResume = false;
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            reason = "snapshot_identity_missing";
            return false;
        }

        if (!CanStartIntent(snapshot.BotProfileId, snapshot.Threat.EnemyId, intentKind, now, out reason))
        {
            return false;
        }

        TacticalIntentContinuityState? current;
        string bot = Normalize(snapshot.BotProfileId);
        string requestedKey = NormalizeLogicalKey(logicalKey, intentKind);
        lock (Sync)
        {
            PruneExpiredLocked(bot, now);
            ByBotProfileId.TryGetValue(bot, out current);
        }

        if (current == null
            || !string.Equals(current.Phase, "SuspendedQualifiedSoft", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(current.LogicalKey, requestedKey, StringComparison.OrdinalIgnoreCase))
        {
            resumeRevalidationRejectsTotal++;
            EndIntent(snapshot.BotProfileId, current.IntentKind, now, "qualified_soft_resume_logical_context_changed");
            reason = "qualified_soft_resume_logical_context_changed:fresh_start_required";
            return true;
        }

        if (!snapshot.Alive)
        {
            resumeRevalidationRejectsTotal++;
            EndIntent(snapshot.BotProfileId, current.IntentKind, now, "qualified_soft_resume_snapshot_dead");
            reason = "qualified_soft_resume_snapshot_dead";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(snapshot, now, out string freshnessReason))
        {
            resumeRevalidationRejectsTotal++;
            reason = "qualified_soft_resume_snapshot_not_current:" + Safe(freshnessReason);
            return false;
        }

        string owner = Normalize(snapshot.OwnerProfileId);
        string threat = Normalize(snapshot.Threat.EnemyId);
        if (!string.Equals(current.OwnerProfileId, owner, StringComparison.OrdinalIgnoreCase)
            || current.ThreatId.Length == 0
            || threat.Length == 0
            || !string.Equals(current.ThreatId, threat, StringComparison.OrdinalIgnoreCase))
        {
            resumeRevalidationRejectsTotal++;
            EndIntent(snapshot.BotProfileId, current.IntentKind, now, "qualified_soft_resume_owner_or_exact_threat_changed");
            reason = "qualified_soft_resume_owner_or_exact_threat_changed";
            return false;
        }

        if (snapshot.Threat.EnemyVisible == true
            && snapshot.Threat.Distance.HasValue
            && snapshot.Threat.Distance.Value >= 0f
            && snapshot.Threat.Distance.Value <= AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters)
        {
            resumeRevalidationRejectsTotal++;
            EndIntent(snapshot.BotProfileId, current.IntentKind, now, "qualified_soft_resume_danger_close_contact");
            reason = "qualified_soft_resume_danger_close_contact";
            return false;
        }

        qualifiedResume = true;
        reason = "qualified_soft_resume_revalidated:" + Safe(current.LastReason);
        return true;
    }

    internal static bool BeginOrRefresh(
        string? botProfileId,
        string? ownerProfileId,
        string? threatId,
        string intentKind,
        string logicalKey,
        DateTimeOffset now,
        string reason,
        bool protectsFromCohesion,
        bool qualifiedResume = false)
    {
        string bot = Normalize(botProfileId);
        string owner = Normalize(ownerProfileId);
        string threat = Normalize(threatId);
        string kind = NormalizeKind(intentKind);
        string key = string.IsNullOrWhiteSpace(logicalKey) ? kind : logicalKey.Trim();
        if (bot.Length == 0 || kind.Length == 0)
        {
            return false;
        }

        bool started = false;
        bool resumed = false;
        TacticalIntentContinuityState state;
        lock (Sync)
        {
            PruneExpiredLocked(bot, now);
            if (ByBotProfileId.TryGetValue(bot, out var current)
                && current.IsCurrent(now)
                && (!string.Equals(current.IntentKind, kind, StringComparison.OrdinalIgnoreCase)
                    || (threat.Length > 0 && !string.Equals(current.ThreatId, threat, StringComparison.OrdinalIgnoreCase))))
            {
                if (current.ProtectsFromCohesion)
                {
                    conflictBlocksTotal++;
                    return false;
                }
            }

            if (current != null
                && current.IsCurrent(now)
                && qualifiedResume
                && string.Equals(current.Phase, "SuspendedQualifiedSoft", StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.OwnerProfileId, owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.IntentKind, kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.ThreatId, threat, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.LogicalKey, key, StringComparison.OrdinalIgnoreCase))
            {
                current.OwnerProfileId = owner;
                current.ProtectsFromCohesion = protectsFromCohesion;
                current.LastRefreshAtUtc = now;
                current.ProtectedUntilUtc = now + ActiveRefreshGrace;
                current.Phase = "Active";
                current.LastReason = reason;
                state = current;
                resumesTotal++;
                resumed = true;
            }
            else if (current == null
                || !string.Equals(current.IntentKind, kind, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.ThreatId, threat, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.LogicalKey, key, StringComparison.OrdinalIgnoreCase))
            {
                state = new TacticalIntentContinuityState(
                    bot,
                    owner,
                    threat,
                    kind,
                    key,
                    protectsFromCohesion,
                    now,
                    now,
                    now + ActiveRefreshGrace,
                    "Active",
                    reason);
                ByBotProfileId[bot] = state;
                startsTotal++;
                started = true;
            }
            else
            {
                current.OwnerProfileId = owner;
                current.ProtectsFromCohesion = protectsFromCohesion;
                current.LastRefreshAtUtc = now;
                current.ProtectedUntilUtc = now + ActiveRefreshGrace;
                current.Phase = "Active";
                current.LastReason = reason;
                state = current;
                refreshesTotal++;
            }
        }

        if (resumed)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_INTENT_CONTINUITY_RESUMED bot={Safe(state.BotProfileId)}; owner={Safe(state.OwnerProfileId)}; threat={Safe(state.ThreatId)}; intent={Safe(state.IntentKind)}; logicalKey={Safe(state.LogicalKey)}; protectsFromCohesion={Bool(state.ProtectsFromCohesion)}; reason={Safe(reason)}; exactThreatRevalidated=true; physicalMutation=false; targetMutation=false");
        }
        else if (started)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_INTENT_CONTINUITY_STARTED bot={Safe(state.BotProfileId)}; owner={Safe(state.OwnerProfileId)}; threat={Safe(state.ThreatId)}; intent={Safe(state.IntentKind)}; logicalKey={Safe(state.LogicalKey)}; protectsFromCohesion={Bool(state.ProtectsFromCohesion)}; reason={Safe(reason)}; physicalMutation=false; targetMutation=false");
        }
        return true;
    }

    internal static bool TrySuspendQualifiedSoftInterruption(
        string? botProfileId,
        string? ownerProfileId,
        string? threatId,
        string intentKind,
        DateTimeOffset now,
        string sourceReason,
        out string reason)
    {
        reason = "not_qualified";
        if (!IsRuntimeCalibratedSoftResumeReason(sourceReason))
        {
            reason = "soft_resume_reason_not_promoted";
            return false;
        }

        string bot = Normalize(botProfileId);
        string owner = Normalize(ownerProfileId);
        string threat = Normalize(threatId);
        string kind = NormalizeKind(intentKind);
        if (bot.Length == 0 || owner.Length == 0 || threat.Length == 0 || kind.Length == 0)
        {
            reason = "soft_resume_identity_missing";
            return false;
        }

        if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(bot, out OperatorDecisionSnapshot snapshot)
            || snapshot == null
            || !snapshot.Alive)
        {
            reason = "soft_resume_snapshot_missing_or_dead";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(snapshot, now, out string freshnessReason))
        {
            reason = "soft_resume_snapshot_not_current:" + Safe(freshnessReason);
            return false;
        }

        string snapshotOwner = Normalize(snapshot.OwnerProfileId);
        string snapshotThreat = Normalize(snapshot.Threat.EnemyId);
        if (!string.Equals(snapshotOwner, owner, StringComparison.OrdinalIgnoreCase)
            || snapshotThreat.Length == 0
            || !string.Equals(snapshotThreat, threat, StringComparison.OrdinalIgnoreCase))
        {
            reason = "soft_resume_owner_or_exact_threat_changed";
            return false;
        }

        if (snapshot.Threat.EnemyVisible == true
            && snapshot.Threat.Distance.HasValue
            && snapshot.Threat.Distance.Value >= 0f
            && snapshot.Threat.Distance.Value <= AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters)
        {
            reason = "soft_resume_danger_close_contact";
            return false;
        }

        TacticalIntentContinuityState state;
        lock (Sync)
        {
            PruneExpiredLocked(bot, now);
            if (!ByBotProfileId.TryGetValue(bot, out var current)
                || !string.Equals(current.OwnerProfileId, owner, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.ThreatId, threat, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.IntentKind, kind, StringComparison.OrdinalIgnoreCase))
            {
                reason = "soft_resume_current_intent_mismatch";
                return false;
            }

            current.LastRefreshAtUtc = now;
            current.ProtectedUntilUtc = now + LocalInterruptionGrace;
            current.Phase = "SuspendedQualifiedSoft";
            current.LastReason = sourceReason;
            state = current;
            suspendsTotal++;
        }

        reason = "qualified_soft_interruption_suspended";
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_INTENT_CONTINUITY_SUSPENDED bot={Safe(state.BotProfileId)}; owner={Safe(state.OwnerProfileId)}; threat={Safe(state.ThreatId)}; intent={Safe(state.IntentKind)}; logicalKey={Safe(state.LogicalKey)}; phase=SuspendedQualifiedSoft; graceSeconds={LocalInterruptionGrace.TotalSeconds:0.00}; reason={Safe(sourceReason)}; exactThreatRevalidated=true; exactLogicalContextRequiredOnResume=true; lowerPriorityCohesionBlocked={Bool(state.ProtectsFromCohesion)}; physicalMutation=false; targetMutation=false");
        return true;
    }

    internal static bool EndIntent(
        string? botProfileId,
        string intentKind,
        DateTimeOffset now,
        string reason)
    {
        string bot = Normalize(botProfileId);
        string kind = NormalizeKind(intentKind);
        TacticalIntentContinuityState? ended = null;
        lock (Sync)
        {
            if (ByBotProfileId.TryGetValue(bot, out var current)
                && string.Equals(current.IntentKind, kind, StringComparison.OrdinalIgnoreCase))
            {
                ended = current;
                ByBotProfileId.Remove(bot);
                terminalsTotal++;
            }
        }

        if (ended != null)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_INTENT_CONTINUITY_ENDED bot={Safe(ended.BotProfileId)}; owner={Safe(ended.OwnerProfileId)}; threat={Safe(ended.ThreatId)}; intent={Safe(ended.IntentKind)}; logicalKey={Safe(ended.LogicalKey)}; ageSeconds={Math.Max(0d, (now - ended.StartedAtUtc).TotalSeconds):0.00}; reason={Safe(reason)}; physicalMutation=false");
            return true;
        }
        return false;
    }

    internal static bool ShouldDeferCohesion(
        OperatorDecisionSnapshot snapshot,
        DateTimeOffset now,
        out string reason)
    {
        reason = "none";
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            return false;
        }

        TacticalIntentContinuityState? current;
        lock (Sync)
        {
            PruneExpiredLocked(Normalize(snapshot.BotProfileId), now);
            ByBotProfileId.TryGetValue(Normalize(snapshot.BotProfileId), out current);
        }
        if (current == null || !current.ProtectsFromCohesion)
        {
            return false;
        }

        if (!snapshot.Alive)
        {
            EndIntent(snapshot.BotProfileId, current.IntentKind, now, "operator_dead_or_missing");
            materialInvalidationsTotal++;
            return false;
        }

        string currentThreat = Normalize(snapshot.Threat.EnemyId);
        if (current.ThreatId.Length > 0
            && currentThreat.Length > 0
            && !string.Equals(current.ThreatId, currentThreat, StringComparison.OrdinalIgnoreCase))
        {
            EndIntent(snapshot.BotProfileId, current.IntentKind, now, "material_threat_identity_changed");
            materialInvalidationsTotal++;
            return false;
        }

        if (snapshot.Threat.EnemyVisible == true
            && snapshot.Threat.Distance.HasValue
            && snapshot.Threat.Distance.Value >= 0f
            && snapshot.Threat.Distance.Value <= AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters)
        {
            EndIntent(snapshot.BotProfileId, current.IntentKind, now, "material_danger_close_contact");
            materialInvalidationsTotal++;
            return false;
        }

        cohesionBlocksTotal++;
        reason = "protected_tactical_intent:" + Safe(current.IntentKind)
            + ":threat=" + Safe(current.ThreatId)
            + ":phase=" + Safe(current.Phase)
            + ":logicalKey=" + Safe(current.LogicalKey);
        return true;
    }

    internal static bool IsCurrentIntentForThreat(
        string? botProfileId,
        string? threatId,
        string intentKind,
        DateTimeOffset now,
        out string reason)
    {
        string bot = Normalize(botProfileId);
        string threat = Normalize(threatId);
        string kind = NormalizeKind(intentKind);
        lock (Sync)
        {
            PruneExpiredLocked(bot, now);
            if (!ByBotProfileId.TryGetValue(bot, out var current))
            {
                reason = "no_current_tactical_intent";
                return false;
            }
            if (!string.Equals(current.IntentKind, kind, StringComparison.OrdinalIgnoreCase))
            {
                reason = "different_intent:" + Safe(current.IntentKind);
                return false;
            }
            if (threat.Length > 0
                && current.ThreatId.Length > 0
                && !string.Equals(threat, current.ThreatId, StringComparison.OrdinalIgnoreCase))
            {
                reason = "different_threat:" + Safe(current.ThreatId);
                return false;
            }

            reason = "current_intent:" + Safe(current.IntentKind) + ":" + Safe(current.Phase);
            return true;
        }
    }

    internal static bool IsIntentionalTacticalSeparation(
        string? botProfileId,
        string? threatId,
        DateTimeOffset now,
        out string reason)
    {
        string bot = Normalize(botProfileId);
        string threat = Normalize(threatId);
        lock (Sync)
        {
            PruneExpiredLocked(bot, now);
            if (!ByBotProfileId.TryGetValue(bot, out var current)
                || !current.ProtectsFromCohesion)
            {
                reason = "none";
                return false;
            }
            if (threat.Length > 0 && current.ThreatId.Length > 0
                && !string.Equals(threat, current.ThreatId, StringComparison.OrdinalIgnoreCase))
            {
                reason = "different_threat";
                return false;
            }
            reason = "intentional_tactical_separation:" + Safe(current.IntentKind) + ":" + Safe(current.Phase);
            return true;
        }
    }

    internal static bool ForgetRuntimePresence(string? botProfileId, DateTimeOffset now, string reason)
    {
        string bot = Normalize(botProfileId);
        TacticalIntentContinuityState? removed = null;
        lock (Sync)
        {
            if (ByBotProfileId.TryGetValue(bot, out removed))
            {
                ByBotProfileId.Remove(bot);
            }
        }
        if (removed != null)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_INTENT_CONTINUITY_FORGOTTEN bot={Safe(bot)}; intent={Safe(removed.IntentKind)}; reason={Safe(reason)}; ageSeconds={Math.Max(0d, (now - removed.StartedAtUtc).TotalSeconds):0.00}; staleResume=false");
            return true;
        }
        return false;
    }

    internal static void ResetForRaidLifecycle(string reason)
    {
        int cleared;
        lock (Sync)
        {
            cleared = ByBotProfileId.Count;
            ByBotProfileId.Clear();
        }
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TC_INTENT_CONTINUITY_RESET reason={Safe(reason)}; cleared={cleared}; starts={startsTotal}; refreshes={refreshesTotal}; suspends={suspendsTotal}; resumes={resumesTotal}; resumeRevalidationRejects={resumeRevalidationRejectsTotal}; terminals={terminalsTotal}; conflictBlocks={conflictBlocksTotal}; cohesionBlocks={cohesionBlocksTotal}; materialInvalidations={materialInvalidationsTotal}; priorRaidContinuity=false");
        startsTotal = 0;
        refreshesTotal = 0;
        suspendsTotal = 0;
        resumesTotal = 0;
        resumeRevalidationRejectsTotal = 0;
        terminalsTotal = 0;
        conflictBlocksTotal = 0;
        cohesionBlocksTotal = 0;
        materialInvalidationsTotal = 0;
    }

    private static void PruneExpiredLocked(string botProfileId, DateTimeOffset now)
    {
        if (botProfileId.Length == 0
            || !ByBotProfileId.TryGetValue(botProfileId, out var current))
        {
            return;
        }
        if (!current.IsCurrent(now))
        {
            ByBotProfileId.Remove(botProfileId);
        }
    }

    private static bool IsRuntimeCalibratedSoftResumeReason(string? sourceReason)
    {
        string reason = sourceReason ?? string.Empty;
        return reason.IndexOf("grenade_emergency_or_throw", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.StartsWith("higher_authority:grenade_emergency:", StringComparison.OrdinalIgnoreCase)
            || reason.IndexOf("sain_running_to_cover", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static string NormalizeLogicalKey(string? logicalKey, string? intentKind)
    {
        string key = string.IsNullOrWhiteSpace(logicalKey) ? string.Empty : logicalKey.Trim();
        return key.Length > 0 ? key : NormalizeKind(intentKind);
    }

    private static string NormalizeKind(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private static string Bool(bool value) => value ? "true" : "false";

    private sealed class TacticalIntentContinuityState
    {
        public TacticalIntentContinuityState(
            string botProfileId,
            string ownerProfileId,
            string threatId,
            string intentKind,
            string logicalKey,
            bool protectsFromCohesion,
            DateTimeOffset startedAtUtc,
            DateTimeOffset lastRefreshAtUtc,
            DateTimeOffset protectedUntilUtc,
            string phase,
            string lastReason)
        {
            BotProfileId = botProfileId;
            OwnerProfileId = ownerProfileId;
            ThreatId = threatId;
            IntentKind = intentKind;
            LogicalKey = logicalKey;
            ProtectsFromCohesion = protectsFromCohesion;
            StartedAtUtc = startedAtUtc;
            LastRefreshAtUtc = lastRefreshAtUtc;
            ProtectedUntilUtc = protectedUntilUtc;
            Phase = phase;
            LastReason = lastReason;
        }

        public string BotProfileId { get; }
        public string OwnerProfileId { get; set; }
        public string ThreatId { get; }
        public string IntentKind { get; }
        public string LogicalKey { get; set; }
        public bool ProtectsFromCohesion { get; set; }
        public DateTimeOffset StartedAtUtc { get; }
        public DateTimeOffset LastRefreshAtUtc { get; set; }
        public DateTimeOffset ProtectedUntilUtc { get; set; }
        public string Phase { get; set; }
        public string LastReason { get; set; }

        public bool IsCurrent(DateTimeOffset now)
            => ProtectedUntilUtc > now || now - LastRefreshAtUtc <= ActiveRefreshGrace;
    }
}
#endif
