#if SPT_CLIENT
using System;
using System.Collections;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using Vanguard.Client.Runtime.Audit;

// Responsibility: Reads and normalizes live evidence for Threat Snapshot Reader in the decision snapshot pipeline.
// Flow: Live EFT/Fika/Vanguard objects are inspected defensively, normalized into a bounded snapshot, then handed to policy/decision code.
// Authority boundary: Read-only observer; it does not create missing truth or mutate the game state it inspects.
// Invariant: Missing/stale evidence degrades explicitly and reader failures must not silently fabricate an actionable state.
namespace Vanguard.Client.Runtime.Decision;

internal sealed partial class VanguardOperatorDecisionSnapshotBuilder
{
    private static VanguardThreatDecisionSnapshot CaptureThreat(BotOwner? botOwner, VanguardSainDecisionSnapshot sain, VanguardBrainDecisionSnapshot brain, bool alive)
    {
        if (!alive)
        {
            // audit subsystem: a dead Operator is a terminal per-Operator state. Do not keep
            // stale SAIN/brain enemy data as an active threat for the future scheduler.
            return new VanguardThreatDecisionSnapshot
            {
                HasThreat = false,
                EnemyId = "none",
                EnemyName = "none",
                DirectThreat = false,
                ResidualThreat = false,
                StaleThreat = false,
                Classification = "threat_terminal_dead"
            };
        }

        if (botOwner == null)
        {
            return new VanguardThreatDecisionSnapshot { Classification = "threat_no_botowner" };
        }

        object? sainComponent = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "SAIN.Components.BotComponent");
        object? enemy = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainComponent, "GoalEnemy");
        object? status = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "Status");
        object? path = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "Path");
        object? knownPlaces = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "KnownPlaces");

        bool hasThreat = enemy != null || sain.HasEnemy == true || brain.VanillaGoalEnemyVisible == true || brain.VanillaGoalEnemyCanShoot == true;
        bool? visible = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "IsVisible"));
        bool? los = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "InLineOfSight"));
        bool? canShoot = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "CanShoot"));
        float? distance = Float(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "RealDistance"));
        float? seenAgo = Float(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "TimeSinceSeen"));
        float? heardAgo = Float(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "TimeSinceHeard"));
        float? knownAgo = Float(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "TimeSinceLastKnownUpdated"));
        bool? shotMe = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(status, "ShotMeRecently"));
        bool? shotAtMe = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(status, "ShotAtMeRecently"));

        var classification = ClassifyThreat(hasThreat, visible, los, canShoot, distance, seenAgo, heardAgo, knownAgo, shotMe, shotAtMe, brain);
        return new VanguardThreatDecisionSnapshot
        {
            HasThreat = hasThreat,
            EnemyId = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "EnemyProfileId")),
            EnemyName = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "EnemyName")),
            EnemyKnown = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "EnemyKnown")),
            EnemyVisible = visible,
            EnemyLineOfSight = los,
            EnemyCanShoot = canShoot,
            Distance = distance,
            TimeSinceSeen = seenAgo,
            TimeSinceHeard = heardAgo,
            TimeSinceKnownUpdated = knownAgo,
            PathLength = Float(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(path, "PathLength")),
            BotDistanceFromLastKnown = Float(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(knownPlaces, "BotDistanceFromLastKnown")),
            ShotMeRecently = shotMe,
            ShotAtMeRecently = shotAtMe,
            EnemyAction = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(status, "VulnerableAction")),
            DirectThreat = classification == "threat_direct",
            ResidualThreat = classification == "threat_residual",
            StaleThreat = classification == "threat_stale",
            Classification = classification
        };
    }

    private static string ClassifyThreat(
        bool hasThreat,
        bool? visible,
        bool? los,
        bool? canShoot,
        float? distance,
        float? seenAgo,
        float? heardAgo,
        float? knownAgo,
        bool? shotMe,
        bool? shotAtMe,
        VanguardBrainDecisionSnapshot brain)
    {
        if (!hasThreat)
        {
            return "threat_none";
        }

        // audit subsystem: direct threat now requires direct proof, not only a broad
        // combat-related brain/SAIN state. This prevents long-distance/no-LOS
        // residual memories from suppressing follow or future medical windows.
        if (shotMe == true || shotAtMe == true || canShoot == true || los == true || visible == true || brain.VanillaGoalEnemyCanShoot == true || brain.VanillaGoalEnemyVisible == true)
        {
            return "threat_direct";
        }

        if (distance.HasValue && distance.Value <= 25f && IsRecentPositiveAge(seenAgo, 8f))
        {
            return "threat_direct";
        }

        if (IsOldPositiveAge(seenAgo, 20f) && IsOldPositiveAge(heardAgo, 20f) && IsOldPositiveAge(knownAgo, 20f))
        {
            return "threat_stale";
        }

        return "threat_residual";
    }

    private static bool IsRecentPositiveAge(float? value, float maxSeconds)
    {
        return value.HasValue && value.Value >= 0f && value.Value <= maxSeconds;
    }

    private static bool IsOldPositiveAge(float? value, float minSeconds)
    {
        return value.HasValue && value.Value >= minSeconds;
    }
}
#endif
