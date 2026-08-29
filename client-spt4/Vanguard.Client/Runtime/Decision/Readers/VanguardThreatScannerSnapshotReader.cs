#if SPT_CLIENT
using System;
using System.Collections;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using Vanguard.Client.Runtime.Audit;

// Responsibility: converts the Operator scanner/known-enemy surfaces into a compact read-only threat candidate snapshot for decision policy.
// Flow: Visible, line-of-sight and known-enemy collections are merged by identity, direct/incoming-fire evidence plus range/freshness/arc are scored, and the best candidate receives an explicit promotion reason for downstream combat policy.
// Authority boundary: scanning observes EFT/SAIN threat evidence only; it cannot assign a target, make an enemy visible or promote knowledge without the direct/fresh evidence encoded by policy.
// Invariant: duplicate candidates converge to one identity, stale/weak knowledge stays non-direct, and unavailable scanner fields degrade explicitly instead of producing a false combat contact.
namespace Vanguard.Client.Runtime.Decision;

internal sealed partial class VanguardOperatorDecisionSnapshotBuilder
{
    private static VanguardThreatScanDecisionSnapshot CaptureThreatScan(
        BotOwner? botOwner,
        VanguardThreatDecisionSnapshot currentThreat,
        VanguardSainDecisionSnapshot sain,
        VanguardBrainDecisionSnapshot brain,
        bool alive,
        Vector3 botPosition)
    {
        if (!alive)
        {
            return new VanguardThreatScanDecisionSnapshot { Enabled = true, Classification = "threat_scan_terminal_dead" };
        }

        // The scanner must be able to create the first contact. Combat context changes scoring and
        // classification only; it never suppresses the scan itself. Waiting for SAIN to enter combat
        // here recreates the circular standby regression fixed by the runtime path.
        bool combatContext = IsThreatScanCombatContext(currentThreat, sain, brain);

        if (botOwner == null)
        {
            return new VanguardThreatScanDecisionSnapshot
            {
                Enabled = true,
                CombatContext = combatContext,
                Classification = "threat_scan_no_botowner"
            };
        }

        object? sainComponent = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "SAIN.Components.BotComponent");
        bool typeLoaded = VanguardOperatorRuntimeAuditReflection.TypeExists("SAIN.Components.BotComponent");
        if (sainComponent == null)
        {
            return new VanguardThreatScanDecisionSnapshot
            {
                Enabled = true,
                TypeLoaded = typeLoaded,
                ComponentPresent = false,
                CombatContext = combatContext,
                CurrentThreatId = currentThreat.EnemyId,
                CurrentThreatName = currentThreat.EnemyName,
                Classification = typeLoaded ? "threat_scan_component_missing" : "threat_scan_sain_missing"
            };
        }

        object? controller = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainComponent, "EnemyController");
        object? visibleEnemies = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(controller, "VisibleEnemies");
        object? losEnemies = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(controller, "EnemiesInLineOfSight");
        object? knownEnemies = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(controller, "KnownEnemies");

        int visibleCount = CountEnumerable(visibleEnemies);
        int losCount = CountEnumerable(losEnemies);
        int knownCount = CountEnumerable(knownEnemies);

        Vector3? forward = ResolveForward(botOwner);
        var best = PickBestThreatScanCandidate(visibleEnemies, losEnemies, knownEnemies, currentThreat.EnemyId, botPosition, forward);
        if (best == null)
        {
            return new VanguardThreatScanDecisionSnapshot
            {
                Enabled = true,
                TypeLoaded = typeLoaded,
                ComponentPresent = true,
                CombatContext = combatContext,
                Scanned = true,
                CurrentThreatId = currentThreat.EnemyId,
                CurrentThreatName = currentThreat.EnemyName,
                KnownCount = knownCount,
                VisibleCount = visibleCount,
                LineOfSightCount = losCount,
                WouldPromote = false,
                PromotionReason = "no_secondary_candidate",
                Classification = "threat_scan_no_candidate"
            };
        }

        string promotionReason = EvaluateThreatPromotionReason(currentThreat, best);
        bool wouldPromote = IsPromotionReasonActive(promotionReason);
        return new VanguardThreatScanDecisionSnapshot
        {
            Enabled = true,
            TypeLoaded = typeLoaded,
            ComponentPresent = true,
            CombatContext = combatContext,
            Scanned = true,
            CurrentThreatId = currentThreat.EnemyId,
            CurrentThreatName = currentThreat.EnemyName,
            KnownCount = knownCount,
            VisibleCount = visibleCount,
            LineOfSightCount = losCount,
            CandidateThreatId = best.Id,
            CandidateThreatName = best.Name,
            CandidateVisible = best.Visible,
            CandidateLineOfSight = best.LineOfSight,
            CandidateCanShoot = best.CanShoot,
            CandidateShotMeRecently = best.ShotMeRecently,
            CandidateShotAtMeRecently = best.ShotAtMeRecently,
            CandidateIncomingFireFresh = IsFreshIncomingFireCandidate(best),
            CandidateIncomingFireStale = HasIncomingFireSignal(best) && !IsFreshIncomingFireCandidate(best),
            CandidateDistance = best.Distance,
            CandidateTimeSinceSeen = best.TimeSinceSeen,
            CandidateAngleDegrees = best.AngleDegrees,
            CandidateArc = best.Arc,
            CandidateScore = best.Score,
            WouldPromote = wouldPromote,
            PromotionReason = promotionReason,
            Classification = wouldPromote ? "threat_scan_would_promote" : "threat_scan_keep_current"
        };
    }

    private static bool IsThreatScanCombatContext(VanguardThreatDecisionSnapshot threat, VanguardSainDecisionSnapshot sain, VanguardBrainDecisionSnapshot brain)
    {
        if (threat.DirectThreat || threat.ResidualThreat)
        {
            return true;
        }

        if (sain.IsInCombat == true || sain.HasEnemy == true)
        {
            return true;
        }

        if (string.Equals(sain.Classification, "sain_direct_combat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sain.Classification, "sain_enemy_known", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sain.Classification, "sain_search", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(brain.Classification, "brain_goal_enemy_direct", StringComparison.OrdinalIgnoreCase)
            || string.Equals(brain.Classification, "brain_combat_related", StringComparison.OrdinalIgnoreCase);
    }

    private static ScanEnemyCandidate? PickBestThreatScanCandidate(object? visibleEnemies, object? losEnemies, object? knownEnemies, string currentThreatId, Vector3 botPosition, Vector3? forward)
    {
        var byId = new Dictionary<string, ScanEnemyCandidate>(StringComparer.OrdinalIgnoreCase);
        MergeThreatScanCandidates(byId, visibleEnemies, currentThreatId, botPosition, forward, 24f);
        MergeThreatScanCandidates(byId, losEnemies, currentThreatId, botPosition, forward, 18f);
        MergeThreatScanCandidates(byId, knownEnemies, currentThreatId, botPosition, forward, 0f);

        ScanEnemyCandidate? best = null;
        foreach (var candidate in byId.Values)
        {
            if (candidate.SameAsCurrent)
            {
                continue;
            }

            if (!IsThreatScanCandidateRelevant(candidate))
            {
                continue;
            }

            if (best == null || candidate.Score > best.Score)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static bool IsThreatScanCandidateRelevant(ScanEnemyCandidate candidate)
    {
        if (IsFreshIncomingFireCandidate(candidate) || HasCandidateDirectProofForPromotion(candidate))
        {
            return true;
        }

        if (candidate.Distance.HasValue && candidate.Distance.Value <= 18f && IsRecentPositiveAge(candidate.TimeSinceSeen, 3.0f))
        {
            return true;
        }

        if (candidate.Distance.HasValue
            && candidate.Distance.Value <= 35f
            && IsRecentPositiveAge(candidate.TimeSinceSeen, 4.0f)
            && (string.Equals(candidate.Arc, "rear", StringComparison.OrdinalIgnoreCase) || candidate.Arc.IndexOf("flank", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return true;
        }

        return false;
    }

    private static void MergeThreatScanCandidates(Dictionary<string, ScanEnemyCandidate> byId, object? enumerable, string currentThreatId, Vector3 botPosition, Vector3? forward, float sourceBonus)
    {
        foreach (object enemy in Enumerate(enumerable))
        {
            var candidate = ReadThreatScanCandidate(enemy, currentThreatId, botPosition, forward);
            if (candidate == null)
            {
                continue;
            }

            candidate.Score += sourceBonus;
            if (byId.TryGetValue(candidate.Id, out var existing))
            {
                if (candidate.Score > existing.Score)
                {
                    byId[candidate.Id] = candidate;
                }
            }
            else
            {
                byId[candidate.Id] = candidate;
            }
        }
    }

    private static ScanEnemyCandidate? ReadThreatScanCandidate(object? enemy, string currentThreatId, Vector3 botPosition, Vector3? forward)
    {
        if (enemy == null)
        {
            return null;
        }

        string id = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "EnemyProfileId"));
        if (string.Equals(id, "none", StringComparison.OrdinalIgnoreCase))
        {
            id = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "ProfileId", "Id"));
        }

        if (string.Equals(id, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        bool sameAsCurrent = !string.Equals(id, "none", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(currentThreatId, "none", StringComparison.OrdinalIgnoreCase)
            && string.Equals(id, currentThreatId, StringComparison.OrdinalIgnoreCase);
        bool visible = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "IsVisible", "Visible")) == true;
        bool los = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "InLineOfSight", "LineOfSight")) == true;
        bool canShoot = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "CanShoot")) == true;
        object? status = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "Status");
        bool shotMe = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(status, "ShotMeRecently")) == true;
        bool shotAtMe = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(status, "ShotAtMeRecently")) == true;
        float? distance = Float(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "RealDistance", "Distance"));
        float? seenAgo = Float(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "TimeSinceSeen"));
        Vector3? enemyPosition = Vector(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "EnemyPosition", "Position"));
        if (!enemyPosition.HasValue)
        {
            object? enemyTransform = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "EnemyTransform", "Transform");
            enemyPosition = Vector(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemyTransform, "Position"));
        }

        float? angle = null;
        string arc = "unknown_arc";
        if (enemyPosition.HasValue && forward.HasValue)
        {
            var direction = enemyPosition.Value - botPosition;
            if (direction.sqrMagnitude > 0.01f)
            {
                angle = Vector3.SignedAngle(forward.Value, direction, Vector3.up);
                arc = ClassifyThreatArc(angle.Value);
            }
        }

        float score = ScoreThreatScanCandidate(visible, los, canShoot, shotMe, shotAtMe, distance, seenAgo, arc);
        return new ScanEnemyCandidate
        {
            Id = id,
            Name = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "EnemyName", "Name")),
            Visible = visible,
            LineOfSight = los,
            CanShoot = canShoot,
            ShotMeRecently = shotMe,
            ShotAtMeRecently = shotAtMe,
            Distance = distance,
            TimeSinceSeen = seenAgo,
            AngleDegrees = angle,
            Arc = arc,
            Score = score,
            SameAsCurrent = sameAsCurrent
        };
    }

    private static float ScoreThreatScanCandidate(bool visible, bool los, bool canShoot, bool shotMe, bool shotAtMe, float? distance, float? seenAgo, string arc)
    {
        float score = 0f;
        if (shotMe) score += 120f;
        if (shotAtMe) score += 95f;
        if (canShoot) score += 80f;
        if (los) score += 68f;
        if (visible) score += 58f;

        if (distance.HasValue)
        {
            if (distance.Value <= 12f) score += 55f;
            else if (distance.Value <= 20f) score += 42f;
            else if (distance.Value <= 35f) score += 28f;
            else if (distance.Value <= 60f) score += 12f;
        }

        if (IsRecentPositiveAge(seenAgo, 1.5f)) score += 18f;
        else if (IsRecentPositiveAge(seenAgo, 4.0f)) score += 8f;

        if (string.Equals(arc, "rear", StringComparison.OrdinalIgnoreCase)) score += 32f;
        else if (arc.IndexOf("flank", StringComparison.OrdinalIgnoreCase) >= 0) score += 22f;

        return score;
    }

    private static string EvaluateThreatPromotionReason(VanguardThreatDecisionSnapshot currentThreat, ScanEnemyCandidate candidate)
    {
        if (HasIncomingFireSignal(candidate))
        {
            return IsFreshIncomingFireCandidate(candidate)
                ? "incoming_fire_from_secondary_threat"
                : "incoming_fire_stale_secondary_observe_only";
        }

        bool currentHasDirectProof = currentThreat.EnemyVisible == true || currentThreat.EnemyCanShoot == true || currentThreat.EnemyLineOfSight == true || currentThreat.ShotMeRecently == true || currentThreat.ShotAtMeRecently == true;
        if (!currentHasDirectProof && candidate.CanShoot && HasCandidateDirectProofForPromotion(candidate))
        {
            return "candidate_can_shoot_current_not_direct";
        }

        if (!currentHasDirectProof && (candidate.Visible || candidate.LineOfSight) && IsRecentPositiveAge(candidate.TimeSinceSeen, 2.5f))
        {
            return "current_target_stale_candidate_visible";
        }

        if ((candidate.Visible || candidate.LineOfSight)
            && candidate.Distance.HasValue
            && candidate.Distance.Value <= 25f
            && (string.Equals(candidate.Arc, "rear", StringComparison.OrdinalIgnoreCase) || candidate.Arc.IndexOf("flank", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return "closer_visible_flank_threat";
        }

        if (string.Equals(currentThreat.EnemyId, "none", StringComparison.OrdinalIgnoreCase) && HasCandidateDirectProofForPromotion(candidate))
        {
            return "candidate_direct_no_current_target";
        }

        return "keep_current";
    }

    private static bool HasIncomingFireSignal(ScanEnemyCandidate candidate)
    {
        return candidate.ShotMeRecently || candidate.ShotAtMeRecently;
    }

    private static bool IsFreshIncomingFireCandidate(ScanEnemyCandidate candidate)
    {
        if (!HasIncomingFireSignal(candidate))
        {
            return false;
        }

        if (candidate.Visible || candidate.LineOfSight || IsRecentPositiveAge(candidate.TimeSinceSeen, 3.0f))
        {
            return true;
        }

        if (candidate.Distance.HasValue
            && candidate.Distance.Value <= 25f
            && (string.Equals(candidate.Arc, "rear", StringComparison.OrdinalIgnoreCase) || candidate.Arc.IndexOf("flank", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return true;
        }

        return false;
    }

    private static bool HasCandidateDirectProofForPromotion(ScanEnemyCandidate candidate)
    {
        if (candidate.Visible || candidate.LineOfSight)
        {
            return true;
        }

        if (candidate.CanShoot && IsRecentPositiveAge(candidate.TimeSinceSeen, 3.0f))
        {
            return true;
        }

        if (candidate.CanShoot && candidate.Distance.HasValue && candidate.Distance.Value <= 30f)
        {
            return true;
        }

        return false;
    }

    private static bool IsPromotionReasonActive(string promotionReason)
    {
        return !string.Equals(promotionReason, "keep_current", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(promotionReason, "incoming_fire_stale_secondary_observe_only", StringComparison.OrdinalIgnoreCase);
    }

    private static Vector3? ResolveForward(BotOwner? botOwner)
    {
        try
        {
            object? transform = VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "GetPlayer", "Transform");
            if (transform is Transform playerTransform)
            {
                return playerTransform.forward;
            }

            object? botTransform = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Transform");
            if (botTransform is Transform directTransform)
            {
                return directTransform.forward;
            }
        }
        catch
        {
            // Passive scan only. Missing transform data must not affect raid runtime.
        }

        return null;
    }

    private static IEnumerable<object> Enumerate(object? enumerable)
    {
        if (enumerable is IEnumerable values)
        {
            foreach (object value in values)
            {
                if (value != null)
                {
                    yield return value;
                }
            }
        }
    }

    private static int CountEnumerable(object? enumerable)
    {
        if (enumerable is ICollection collection)
        {
            return collection.Count;
        }

        int count = 0;
        foreach (object _ in Enumerate(enumerable))
        {
            count++;
        }

        return count;
    }

    private static string ClassifyThreatArc(float signedAngle)
    {
        float absolute = Math.Abs(signedAngle);
        if (absolute <= 45f)
        {
            return "front";
        }

        if (absolute >= 135f)
        {
            return "rear";
        }

        return signedAngle < 0f ? "flank_left" : "flank_right";
    }
}
#endif
