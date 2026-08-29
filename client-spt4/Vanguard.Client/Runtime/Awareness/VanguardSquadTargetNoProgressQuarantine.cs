#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Temporarily quarantines hostile targets that repeatedly fail to produce meaningful squad combat progress, preventing endless reacquisition loops.
// Flow: Combat observations update per-target progress/failure history; repeated no-progress outcomes open a bounded quarantine window that awareness/target selection consults before recommitting.
// Authority boundary: Quarantine is a local target-selection guard, not hostility authority; fresh direct threat evidence may still be handled by the higher-priority combat truth path.
// Invariant: Quarantine expires automatically, is raid scoped, and cannot permanently blacklist a target from stale historical failures.
namespace Vanguard.Client.Runtime.Awareness;

/// <summary>
/// Vanguard converts a squad-wide combat episode that ended without productive evidence into
/// bounded knowledge-only memory. The hostile remains known, but the same stale shared contact
/// cannot immediately recreate GoalEnemy/combat authority for sibling Operators. A new target-
/// specific local proof clears the quarantine immediately; distance alone never does.
/// </summary>
internal static class VanguardSquadTargetNoProgressQuarantine
{
    public const string StatusTag = "VANGUARD_SQUAD_TARGET_NO_PROGRESS_QUARANTINE_STATUS";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, QuarantineState> ByOwnerAndTarget = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DuplicateEpisodeMergeWindow = TimeSpan.FromSeconds(4.0d);
    private static readonly TimeSpan RepeatSeriesWindow = TimeSpan.FromSeconds(150.0d);
    private static readonly TimeSpan FirstQuarantine = TimeSpan.FromSeconds(45.0d);
    private static readonly TimeSpan SecondQuarantine = TimeSpan.FromSeconds(75.0d);
    private static readonly TimeSpan MaximumQuarantine = TimeSpan.FromSeconds(90.0d);

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            ByOwnerAndTarget.Clear();
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_SQUAD_TARGET_QUARANTINE_RESET reason={Safe(reason)}; entries=0; doctrine=shared_contact_memory_preserved_but_combat_authority_requires_new_local_proof; tag={StatusTag}");
    }

    public static bool RecordNoProgress(
        OperatorDecisionSnapshot snapshot,
        string? targetId,
        DateTimeOffset now,
        string reason,
        out string summary)
    {
        summary = "not_recorded";
        if (!TryNormalize(snapshot.OwnerProfileId, out string owner)
            || !TryNormalize(targetId, out string target))
        {
            summary = "owner_or_target_missing";
            return false;
        }

        if (HasFreshLocalRearmEvidence(snapshot, target, out string freshEvidence))
        {
            summary = "fresh_local_evidence_prevents_quarantine:" + freshEvidence;
            return false;
        }

        string key = BuildKey(owner, target);
        QuarantineState state;
        bool newSeries;
        lock (Sync)
        {
            int count = 1;
            newSeries = true;
            DateTimeOffset episodeStartedAtUtc = now;
            DateTimeOffset existingUntil = DateTimeOffset.MinValue;
            if (ByOwnerAndTarget.TryGetValue(key, out QuarantineState previous))
            {
                bool duplicateSameClosure = now - previous.LastRecordedAtUtc <= DuplicateEpisodeMergeWindow;
                bool sameSeries = now - previous.LastRecordedAtUtc <= RepeatSeriesWindow;
                count = duplicateSameClosure
                    ? previous.ConsecutiveNoProgressCount
                    : sameSeries
                        ? Math.Min(3, previous.ConsecutiveNoProgressCount + 1)
                        : 1;
                newSeries = !duplicateSameClosure && !sameSeries;
                episodeStartedAtUtc = sameSeries ? previous.EpisodeStartedAtUtc : now;
                existingUntil = previous.QuarantinedUntilUtc;
            }

            TimeSpan duration = count <= 1
                ? FirstQuarantine
                : count == 2
                    ? SecondQuarantine
                    : MaximumQuarantine;
            DateTimeOffset until = now + duration;
            if (existingUntil > until)
            {
                until = existingUntil;
            }

            state = new QuarantineState(
                owner,
                target,
                snapshot.OperatorId ?? "none",
                snapshot.BotProfileId ?? "none",
                Math.Max(1, count),
                episodeStartedAtUtc,
                now,
                until,
                Safe(reason));
            ByOwnerAndTarget[key] = state;
        }

        summary = state.Summary;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_SQUAD_TARGET_QUARANTINED {state.Summary}; newSeries={Bool(newSeries)}; mutation=knowledge_only_for_owner_squad; rearm=local_los_visible_can_shoot_incoming_fire_direct_scanner_owner_hit_or_corroborated_owner_shot; distanceOnly=false; tag={StatusTag}");
        return true;
    }

    public static bool IsCombatAuthorityBlocked(
        OperatorDecisionSnapshot snapshot,
        string? targetId,
        DateTimeOffset now,
        out string reason)
    {
        reason = "none";
        if (!TryNormalize(snapshot.OwnerProfileId, out string owner)
            || !TryNormalize(targetId, out string target))
        {
            return false;
        }

        if (HasFreshLocalRearmEvidence(snapshot, target, out string evidence))
        {
            TryRearm(owner, target, now, evidence, out _);
            return false;
        }

        return IsBlockedWithoutRearm(owner, target, now, out reason);
    }

    public static bool IsCombatAuthorityBlocked(
        string? ownerProfileId,
        string? targetId,
        DateTimeOffset now,
        out string reason)
    {
        reason = "none";
        if (!TryNormalize(ownerProfileId, out string owner)
            || !TryNormalize(targetId, out string target))
        {
            return false;
        }

        return IsBlockedWithoutRearm(owner, target, now, out reason);
    }

    public static bool TryRearmByCandidateEvidence(
        string? ownerProfileId,
        string? targetId,
        DateTimeOffset now,
        bool freshEvidence,
        string evidenceReason,
        out string reason)
    {
        reason = "none";
        if (!freshEvidence
            || !TryNormalize(ownerProfileId, out string owner)
            || !TryNormalize(targetId, out string target))
        {
            return false;
        }

        return TryRearm(owner, target, now, evidenceReason, out reason);
    }

    public static bool HasFreshLocalRearmEvidence(
        OperatorDecisionSnapshot snapshot,
        string? targetId,
        out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive || !TryNormalize(targetId, out string target))
        {
            return false;
        }

        if (SameTarget(snapshot.Threat.EnemyId, target))
        {
            if (snapshot.Threat.EnemyVisible == true)
            {
                reason = "current_enemy_visible";
                return true;
            }
            if (snapshot.Threat.EnemyLineOfSight == true)
            {
                reason = "current_enemy_los";
                return true;
            }
            if (snapshot.Threat.EnemyCanShoot == true || snapshot.Brain.VanillaGoalEnemyCanShoot == true)
            {
                reason = "current_enemy_can_shoot";
                return true;
            }
            if (snapshot.Threat.ShotMeRecently == true || snapshot.Threat.ShotAtMeRecently == true)
            {
                reason = "current_enemy_incoming_fire";
                return true;
            }
        }

        if (SameTarget(snapshot.Awareness.CandidateId, target))
        {
            if (snapshot.Awareness.CandidateVisible)
            {
                reason = "awareness_candidate_visible";
                return true;
            }
            if (snapshot.Awareness.CandidateLineOfSight)
            {
                reason = "awareness_candidate_los";
                return true;
            }
            if (snapshot.Awareness.CandidateCanShoot)
            {
                reason = "awareness_candidate_can_shoot";
                return true;
            }
            if (snapshot.Awareness.IncomingFireFresh)
            {
                reason = "awareness_candidate_incoming_fire";
                return true;
            }
        }

        if (SameTarget(snapshot.ThreatScan.CandidateThreatId, target))
        {
            if (snapshot.ThreatScan.CandidateVisible)
            {
                reason = "scanner_candidate_visible";
                return true;
            }
            if (snapshot.ThreatScan.CandidateLineOfSight)
            {
                reason = "scanner_candidate_los";
                return true;
            }
            if (snapshot.ThreatScan.CandidateCanShoot)
            {
                reason = "scanner_candidate_can_shoot";
                return true;
            }
            if (snapshot.ThreatScan.CandidateIncomingFireFresh
                || snapshot.ThreatScan.CandidateShotMeRecently
                || snapshot.ThreatScan.CandidateShotAtMeRecently)
            {
                reason = "scanner_candidate_incoming_fire";
                return true;
            }
        }

        return false;
    }

    private static bool IsBlockedWithoutRearm(string owner, string target, DateTimeOffset now, out string reason)
    {
        reason = "none";
        string key = BuildKey(owner, target);
        lock (Sync)
        {
            if (!ByOwnerAndTarget.TryGetValue(key, out QuarantineState state))
            {
                return false;
            }

            if (state.QuarantinedUntilUtc <= now)
            {
                ByOwnerAndTarget.Remove(key);
                return false;
            }

            reason = "squad_target_knowledge_only_until="
                + state.QuarantinedUntilUtc.ToString("O", CultureInfo.InvariantCulture)
                + ";series=" + state.ConsecutiveNoProgressCount.ToString(CultureInfo.InvariantCulture)
                + ";sourceBot=" + Safe(state.SourceBotProfileId);
            return true;
        }
    }

    private static bool TryRearm(string owner, string target, DateTimeOffset now, string evidenceReason, out string reason)
    {
        reason = "none";
        QuarantineState removed;
        lock (Sync)
        {
            string key = BuildKey(owner, target);
            if (!ByOwnerAndTarget.TryGetValue(key, out removed))
            {
                return false;
            }

            ByOwnerAndTarget.Remove(key);
        }

        reason = "rearmed_by_new_local_evidence:" + Safe(evidenceReason);
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_SQUAD_TARGET_REARMED owner={Safe(owner)}; target={Safe(target)}; at={now:O}; evidence={Safe(evidenceReason)}; priorSeries={removed.ConsecutiveNoProgressCount}; priorUntil={removed.QuarantinedUntilUtc:O}; mutation=combat_authority_eligible_again; tag={StatusTag}");
        return true;
    }

    private static string BuildKey(string owner, string target) => owner + "|" + target;

    private static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
        return !string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameTarget(string? left, string right)
        => !string.IsNullOrWhiteSpace(left)
            && string.Equals(left.Trim(), right, StringComparison.OrdinalIgnoreCase);

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');

    private static string Bool(bool value) => value ? "true" : "false";

    private sealed class QuarantineState
    {
        public QuarantineState(
            string ownerProfileId,
            string targetId,
            string sourceOperatorId,
            string sourceBotProfileId,
            int consecutiveNoProgressCount,
            DateTimeOffset episodeStartedAtUtc,
            DateTimeOffset lastRecordedAtUtc,
            DateTimeOffset quarantinedUntilUtc,
            string reason)
        {
            OwnerProfileId = ownerProfileId;
            TargetId = targetId;
            SourceOperatorId = sourceOperatorId;
            SourceBotProfileId = sourceBotProfileId;
            ConsecutiveNoProgressCount = consecutiveNoProgressCount;
            EpisodeStartedAtUtc = episodeStartedAtUtc;
            LastRecordedAtUtc = lastRecordedAtUtc;
            QuarantinedUntilUtc = quarantinedUntilUtc;
            Reason = reason;
        }

        public string OwnerProfileId { get; }
        public string TargetId { get; }
        public string SourceOperatorId { get; }
        public string SourceBotProfileId { get; }
        public int ConsecutiveNoProgressCount { get; }
        public DateTimeOffset EpisodeStartedAtUtc { get; }
        public DateTimeOffset LastRecordedAtUtc { get; }
        public DateTimeOffset QuarantinedUntilUtc { get; }
        public string Reason { get; }

        public string Summary => "owner=" + Safe(OwnerProfileId)
            + ";target=" + Safe(TargetId)
            + ";sourceOperator=" + Safe(SourceOperatorId)
            + ";sourceBot=" + Safe(SourceBotProfileId)
            + ";series=" + ConsecutiveNoProgressCount.ToString(CultureInfo.InvariantCulture)
            + ";episodeStarted=" + EpisodeStartedAtUtc.ToString("O", CultureInfo.InvariantCulture)
            + ";lastRecorded=" + LastRecordedAtUtc.ToString("O", CultureInfo.InvariantCulture)
            + ";until=" + QuarantinedUntilUtc.ToString("O", CultureInfo.InvariantCulture)
            + ";reason=" + Safe(Reason);
    }
}
#endif
