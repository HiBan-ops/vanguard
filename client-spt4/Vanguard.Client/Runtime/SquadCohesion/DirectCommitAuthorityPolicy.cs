#if SPT_CLIENT
using System;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;

// Responsibility: Defines the non-mutating local safety and SAIN translation-compatibility boundary for bounded synchronized DirectCommit movement.
// Flow: Current Direct-member evidence first passes split-flank hard-safety checks, then a dedicated DirectCommit translation contract accepts strict Search/GroupSearch plus only StandAndShoot or distant-visible Search when the exact planned threat remains outside the close-dogfight envelope. During an already-running DirectCommit lease, only that exact owned primary window is exempted from the generic noncombat-primary veto.
// Authority boundary: This policy cannot open scheduler windows, issue movement, mutate SAIN, choose a Direct member or change target identity; it only classifies whether a selected Direct member may receive or retain bounded TC translation authority now.
// Invariant: Synchronized readiness is necessary but never sufficient for physical DirectCommit; SeekCover, close visible dogfight, stale evidence, competing threats, foreign execution authority or incompatible SAIN movement always fail closed before mutation or on the next lease check.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class DirectCommitAuthorityPolicy
{
    public const string RequestKind = "TacticalCoordinatorDirectCommit";

    public static bool IsDirectCommitRequest(string? requestKind)
        => string.Equals(Normalize(requestKind), RequestKind, StringComparison.OrdinalIgnoreCase);

    public static bool IsTranslationCompatible(
        OperatorDecisionSnapshot snapshot,
        string ownerProfileId,
        string threatId,
        DateTimeOffset memberEvidenceCapturedAtUtc,
        DateTimeOffset now,
        out string reason)
    {
        reason = SplitFlankSafetyPolicy.Evaluate(
            snapshot,
            ownerProfileId,
            threatId,
            memberEvidenceCapturedAtUtc,
            now);
        if (reason.Length > 0 && !IsExactOwnedDirectCommitPrimaryVeto(reason, snapshot, threatId, now))
        {
            return false;
        }

        string ownedPrimaryQualification = reason.Length > 0
            ? "owned_direct_commit_primary_allowed"
            : "no_foreign_primary_conflict";

        if (!MultiThreatOffensiveSafetyPolicy.IsOffensiveTranslationSafe(
                snapshot,
                threatId,
                "DirectCommit",
                now,
                out string multiThreatReason))
        {
            reason = "multi_threat_offensive_safety:" + multiThreatReason;
            return false;
        }

        if (!IsDirectCommitTranslationCompatible(snapshot, threatId, out string translationReason))
        {
            reason = "translation:" + translationReason;
            return false;
        }

        reason = "direct_commit_translation_ready:" + ownedPrimaryQualification + ":" + translationReason + ":multi_threat_offensive_safety_ready";
        return true;
    }

    private static bool IsDirectCommitTranslationCompatible(
        OperatorDecisionSnapshot snapshot,
        string expectedThreatId,
        out string reason)
    {
        if (AltDevelopAuthorityPolicy.IsTranslationCompatible(snapshot, expectedThreatId, out reason))
        {
            return true;
        }

        if (snapshot?.Sain == null)
        {
            return false;
        }

        float? threatDistance = snapshot.Threat.Distance;
        if (string.Equals(reason, "sain_search_action_local_dogfight_visible_target", StringComparison.OrdinalIgnoreCase))
        {
            if (snapshot.Threat.EnemyVisible == true
                && threatDistance.HasValue
                && threatDistance.Value > AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters)
            {
                reason = "translation_compatible_direct_commit_distant_visible_search:distance="
                    + threatDistance.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + ":closeVetoMeters=" + AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + ":sainAimFirePreserved=true";
                return true;
            }

            return false;
        }

        if (!reason.StartsWith("sain_action_not_search:", StringComparison.OrdinalIgnoreCase)
            || !IsStandAndShootAction(snapshot.Sain.CurrentAction))
        {
            return false;
        }

        if (snapshot.Threat.EnemyVisible == true
            && threatDistance.HasValue
            && threatDistance.Value >= 0f
            && threatDistance.Value <= AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters)
        {
            reason = "close_visible_threat_survival_precedes_direct_commit";
            return false;
        }

        reason = "translation_compatible_direct_commit_stand_and_shoot:distance="
            + (threatDistance.HasValue ? threatDistance.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "unknown")
            + ":closeVetoMeters=" + AltDevelopAuthorityPolicy.OpportunityCombatCloseThreatMeters.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            + ":sainAimFirePreserved=true";
        return true;
    }

    private static bool IsStandAndShootAction(string? value)
        => string.Equals(
            TacticalSearchPolicy.NormalizeToken(value),
            "StandAndShootAction",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsExactOwnedDirectCommitPrimaryVeto(
        string safetyReason,
        OperatorDecisionSnapshot snapshot,
        string threatId,
        DateTimeOffset now)
    {
        if (!safetyReason.StartsWith("noncombat_primary_execution_active:", StringComparison.OrdinalIgnoreCase)
            || snapshot == null
            || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            return false;
        }

        if (!VanguardMainIntentScheduler.TryGetActiveTacticalCoordinatorDirectCommitContext(
                snapshot.BotProfileId,
                now,
                out string activeThreatId,
                out long activePlanEpoch,
                out _)
            || activePlanEpoch <= 0)
        {
            return false;
        }

        return string.Equals(NormalizeThreat(activeThreatId), NormalizeThreat(threatId), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();

    private static string NormalizeThreat(string? value)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        return string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "null", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "unknown", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : normalized;
    }
}
#endif
