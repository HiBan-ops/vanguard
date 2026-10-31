#if SPT_CLIENT
using System;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Defines the local safety/authority boundary for TC AltDevelop translation while SAIN remains free to perceive, aim, fire and suppress.
// Flow: Hard local survival vetoes are checked first; Search/GroupSearch remains compatible, and AltDevelop additionally permits two ordinary combat actions (SeekCover/StandAndShoot) when no close visible dogfight owns movement.
// Authority boundary: This policy never mutates SAIN or opens movement by itself. AltDevelop and the dedicated TC TacticalOpportunity sidecar may reuse this bounded local-safety envelope; SAIN immediately reclaims translation for grenade/medical/reload, active cover sprint, competing threat, close dogfight or another unsupported action.
// Invariant: Combat state is not a blanket movement veto. Stationary/shared hold contracts remain Search/GroupSearch-only; DirectCommit owns a separate narrower combat-advance extension, while only explicitly opted-in bounded TC movement sidecars may reuse the broader opportunity-combat extension.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class AltDevelopAuthorityPolicy
{
    public const string StatusTag = "VANGUARD_TC_ALT_DEVELOP_AUTHORITY_POLICY_STATUS";
    public const string RequestKind = "TacticalCoordinatorAltDevelop";
    public const float OpportunityCombatCloseThreatMeters = 15f;

    public static bool IsAltDevelopRequest(string? requestKind)
        => string.Equals(Normalize(requestKind), RequestKind, StringComparison.OrdinalIgnoreCase);

    public static bool IsTranslationCompatible(OperatorDecisionSnapshot snapshot, string expectedThreatId, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(snapshot, DateTimeOffset.UtcNow, out var freshnessReason))
        {
            reason = freshnessReason;
            return false;
        }
        if (snapshot.GrenadeHazard.HasRelevantHazard || snapshot.Medical.Actionability.GrenadeThrowing)
        {
            reason = "grenade_emergency_or_throw";
            return false;
        }
        if (snapshot.Medical.Actionability.AnyMedicineUsing)
        {
            reason = "medical_controller_active";
            return false;
        }
        if (snapshot.Medical.Actionability.Reloading)
        {
            reason = "reload_active";
            return false;
        }

        var sain = snapshot.Sain;
        if (sain == null)
        {
            reason = "sain_component_missing";
            return false;
        }

        string localThreatId = NormalizeThreat(snapshot.Threat.EnemyId);
        string expectedThreat = NormalizeThreat(expectedThreatId);
        bool relevantLocalThreat = snapshot.Threat.DirectThreat || snapshot.Threat.ResidualThreat || sain.HasEnemy == true;
        if (relevantLocalThreat && string.IsNullOrWhiteSpace(localThreatId))
        {
            reason = "relevant_local_threat_identity_unknown";
            return false;
        }
        if (relevantLocalThreat
            && !string.Equals(localThreatId, expectedThreat, StringComparison.OrdinalIgnoreCase))
        {
            reason = "competing_relevant_threat_identity_changed";
            return false;
        }

        string searchContextReason = TacticalSearchPolicy.EvaluateSnapshot(sain);
        if (searchContextReason.Length > 0)
        {
            reason = searchContextReason;
            return false;
        }

        // SAIN SearchAction performs DogFightMove from inside SearchAction when its goal
        // enemy is visible. That is a concrete local translation conflict, unlike generic combat
        // awareness or incoming-fire evidence. GroupSearch followers use FollowSearchParty and may
        // continue a Vanguard-assigned translation while SAIN retains steering/fire/suppression.
        if (TacticalSearchPolicy.IsLeaderSearchAction(sain.CurrentAction)
            && snapshot.Threat.EnemyVisible == true)
        {
            reason = "sain_search_action_local_dogfight_visible_target";
            return false;
        }

        reason = "translation_compatible:combat=" + Safe(TacticalSearchPolicy.NormalizeToken(sain.CombatDecision))
            + ":squad=" + Safe(TacticalSearchPolicy.NormalizeToken(sain.SquadDecision))
            + ":action=" + Safe(TacticalSearchPolicy.NormalizeToken(sain.CurrentAction))
            + ":searching=true"
            + ":sainCombat=" + Bool(sain.IsInCombat == true)
            + ":hasEnemy=" + Bool(sain.HasEnemy == true);
        return true;
    }

    public static bool IsAltDevelopTranslationCompatible(OperatorDecisionSnapshot snapshot, string expectedThreatId, out string reason)
    {
        if (!IsAltDevelopTranslationCompatibleWithoutOffensiveSafety(snapshot, expectedThreatId, out reason))
        {
            return false;
        }

        return ApplyMultiThreatOffensiveSafety(snapshot, expectedThreatId, "AltDevelop", ref reason);
    }

    private static bool IsAltDevelopTranslationCompatibleWithoutOffensiveSafety(
        OperatorDecisionSnapshot snapshot,
        string expectedThreatId,
        out string reason)
    {
        if (IsTranslationCompatible(snapshot, expectedThreatId, out reason))
        {
            return true;
        }

        // Opportunity-first extension: the legacy contract above has already applied all hard
        // grenade/medical/reload/freshness/threat-identity and running-to-cover vetoes. Only an
        // ordinary non-Search combat action may be promoted from "not search" to bounded flank
        // translation, and a visible close dogfight still belongs entirely to SAIN.
        if (!reason.StartsWith("sain_action_not_search:", StringComparison.OrdinalIgnoreCase)
            || snapshot?.Sain == null
            || !IsOpportunityCombatFlankAction(snapshot.Sain.CurrentAction))
        {
            return false;
        }

        float? threatDistance = snapshot.Threat.Distance;
        if (snapshot.Threat.EnemyVisible == true
            && threatDistance.HasValue
            && threatDistance.Value >= 0f
            && threatDistance.Value <= OpportunityCombatCloseThreatMeters)
        {
            reason = "close_visible_threat_survival_precedes_flank";
            return false;
        }

        reason = "translation_compatible_opportunity_combat_flank:action="
            + Safe(TacticalSearchPolicy.NormalizeToken(snapshot.Sain.CurrentAction))
            + ":distance=" + (threatDistance.HasValue ? threatDistance.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "unknown")
            + ":closeVetoMeters=" + OpportunityCombatCloseThreatMeters.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            + ":sainAimFirePreserved=true";
        return true;
    }

    private static bool ApplyMultiThreatOffensiveSafety(
        OperatorDecisionSnapshot snapshot,
        string expectedThreatId,
        string movementKind,
        ref string reason)
    {
        if (MultiThreatOffensiveSafetyPolicy.IsOffensiveTranslationSafe(
                snapshot,
                expectedThreatId,
                movementKind,
                DateTimeOffset.UtcNow,
                out string multiThreatReason))
        {
            reason += ":multi_threat_offensive_safety_ready";
            return true;
        }

        reason = "multi_threat_offensive_safety:" + multiThreatReason;
        return false;
    }

    private static bool IsOpportunityCombatFlankAction(string? value)
    {
        string token = TacticalSearchPolicy.NormalizeToken(value);
        return string.Equals(token, "SeekCoverAction", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "StandAndShootAction", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBoundAdvanceTranslationCompatible(OperatorDecisionSnapshot snapshot, string expectedThreatId, out string reason)
    {
        if (IsTranslationCompatible(snapshot, expectedThreatId, out reason))
        {
            return true;
        }

        // BoundAdvance is intentionally narrower than the generic TacticalOpportunity envelope.
        // It may borrow translation from a stationary StandAndShoot context or from a distant-visible
        // SearchAction, but it never converts SAIN SeekCover/Retreat into an advance toward the threat.
        if (reason.StartsWith("sain_action_not_search:", StringComparison.OrdinalIgnoreCase)
            && snapshot?.Sain != null
            && string.Equals(
                TacticalSearchPolicy.NormalizeToken(snapshot.Sain.CurrentAction),
                "StandAndShootAction",
                StringComparison.OrdinalIgnoreCase))
        {
            float? threatDistance = snapshot.Threat.Distance;
            if (snapshot.Threat.EnemyVisible == true
                && threatDistance.HasValue
                && threatDistance.Value >= 0f
                && threatDistance.Value <= OpportunityCombatCloseThreatMeters)
            {
                reason = "close_visible_threat_survival_precedes_bound_advance";
                return false;
            }

            reason = "translation_compatible_bound_advance_stand_and_shoot:distance="
                + (threatDistance.HasValue ? threatDistance.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "unknown")
                + ":sainAimFirePreserved=true";
            return true;
        }

        if (!string.Equals(reason, "sain_search_action_local_dogfight_visible_target", StringComparison.OrdinalIgnoreCase)
            || snapshot?.Sain == null
            || !TacticalSearchPolicy.IsLeaderSearchAction(snapshot.Sain.CurrentAction)
            || snapshot.Threat.EnemyVisible != true
            || !snapshot.Threat.Distance.HasValue
            || snapshot.Threat.Distance.Value <= OpportunityCombatCloseThreatMeters)
        {
            return false;
        }

        reason = "translation_compatible_bound_advance_distant_visible_search:distance="
            + snapshot.Threat.Distance.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            + ":closeVetoMeters=" + OpportunityCombatCloseThreatMeters.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            + ":sainAimFirePreserved=true";
        return true;
    }

    internal static bool IsTacticalOpportunityTranslationCompatibleWithoutOffensiveSafety(
        OperatorDecisionSnapshot snapshot,
        string expectedThreatId,
        out string reason)
    {
        if (IsAltDevelopTranslationCompatibleWithoutOffensiveSafety(snapshot, expectedThreatId, out reason))
        {
            return true;
        }

        // A distant visible SearchAction is not treated as a danger-close dogfight for the dedicated
        // TacticalOpportunity sidecar. The hard local authority checks have already run, but no
        // multi-threat offensive policy is applied here so survival/support callers can layer the
        // movement-kind-specific safety contract without accidentally inheriting an offensive veto.
        if (!string.Equals(reason, "sain_search_action_local_dogfight_visible_target", StringComparison.OrdinalIgnoreCase)
            || snapshot?.Sain == null
            || !TacticalSearchPolicy.IsLeaderSearchAction(snapshot.Sain.CurrentAction)
            || snapshot.Threat.EnemyVisible != true
            || !snapshot.Threat.Distance.HasValue
            || snapshot.Threat.Distance.Value <= OpportunityCombatCloseThreatMeters)
        {
            return false;
        }

        reason = "translation_compatible_tactical_opportunity_distant_visible_search:distance="
            + snapshot.Threat.Distance.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            + ":closeVetoMeters=" + OpportunityCombatCloseThreatMeters.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            + ":sainAimFirePreserved=true";
        return true;
    }

    public static bool IsTacticalOpportunityTranslationCompatible(OperatorDecisionSnapshot snapshot, string expectedThreatId, out string reason)
    {
        if (!IsTacticalOpportunityTranslationCompatibleWithoutOffensiveSafety(snapshot, expectedThreatId, out reason))
        {
            return false;
        }

        return ApplyMultiThreatOffensiveSafety(snapshot, expectedThreatId, "TacticalOpportunity", ref reason);
    }

    public static bool IsSearchCompatibleSquadDecision(string? value)
        => TacticalSearchPolicy.IsSearchSquadDecision(value);

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string NormalizeThreat(string? value)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        return string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "null", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "unknown", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : normalized;
    }
    private static string Safe(string? value) => Normalize(value).Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    private static string Bool(bool value) => value ? "true" : "false";
}
#endif
