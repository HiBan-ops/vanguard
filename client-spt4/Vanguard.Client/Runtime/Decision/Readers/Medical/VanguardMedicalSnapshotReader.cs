#if SPT_CLIENT
using System;
using EFT;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Medical;
using Vanguard.Client.Runtime.Combat;

// Responsibility: turns live Operator health, medical debt, item/actionability and threat context into the medical portion of a decision snapshot.
// Flow: Canonical medical need/effect facts are read first, available treatment/actionability is combined with current threat and cover context, and the result is classified into a read-only safety/actionability snapshot for the scheduler.
// Authority boundary: this reader observes canonical medical and threat state only; it never starts treatment, changes health or upgrades unknown evidence into an actionable need.
// Invariant: canonical need remains the medical truth, unsafe/unknown conditions stay explicit, and missing evidence must degrade to a conservative non-fabricated snapshot.
namespace Vanguard.Client.Runtime.Decision;

internal sealed partial class VanguardOperatorDecisionSnapshotBuilder
{
    private static VanguardMedicalDecisionSnapshot CaptureMedical(
        BotOwner? botOwner,
        bool alive,
        VanguardThreatDecisionSnapshot threat,
        VanguardThreatScanDecisionSnapshot threatScan)
        => CaptureMedical(botOwner, alive, threat, threatScan, VanguardMedicalInventoryReader.Capture(botOwner));

    private static VanguardMedicalDecisionSnapshot CaptureMedical(
        BotOwner? botOwner,
        bool alive,
        VanguardThreatDecisionSnapshot threat,
        VanguardThreatScanDecisionSnapshot threatScan,
        VanguardMedicalInventoryReadResult inventory)
    {
        object? healthController = VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "GetPlayer", "HealthController");
        object? activeHealthController = VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "GetPlayer", "ActiveHealthController") ?? healthController;

        if (!alive)
        {
            return new VanguardMedicalDecisionSnapshot
            {
                Alive = false,
                ControllerObserved = healthController != null || activeHealthController != null,
                ControllerType = VanguardOperatorRuntimeAuditReflection.TypeName(activeHealthController ?? healthController),
                Need = new VanguardMedicalNeedSnapshot { IsReadable = healthController != null || activeHealthController != null, DominantNeed = VanguardMedicalNeed.None, HealthPercent = 0, Source = "dead_operator" },
                Safety = BuildSafety(botOwner, threat, threatScan),
                Plan = VanguardMedicalPlanReadOnlyBuilder.ForDead(),
                Classification = "medical_dead"
            };
        }

        long effectStarted = VanguardRuntimePerformanceGuard.Begin();
        var originalNeed = VanguardMedicalEffectReader.Capture(botOwner, activeHealthController, "decision_snapshot");
        VanguardRuntimePerformanceGuard.End("DecisionSnapshotMedicalEffect", effectStarted);
        var need = originalNeed;
        long actionabilityStarted = VanguardRuntimePerformanceGuard.Begin();
        var actionability = VanguardMedicalActionabilityReader.Capture(botOwner, need, inventory);
        VanguardRuntimePerformanceGuard.End("DecisionSnapshotMedicalActionability", actionabilityStarted);
        string actionableFallback = "none";
        if (TryBuildActionableFractureFallback(botOwner, originalNeed, inventory, actionability, out var fallbackNeed, out var fallbackActionability, out var fallbackReason))
        {
            need = fallbackNeed;
            actionability = fallbackActionability;
            actionableFallback = fallbackReason;
        }

        long safetyStarted = VanguardRuntimePerformanceGuard.Begin();
        var safety = BuildSafety(botOwner, threat, threatScan);
        VanguardRuntimePerformanceGuard.End("DecisionSnapshotMedicalSafety", safetyStarted);
        long planStarted = VanguardRuntimePerformanceGuard.Begin();
        var plan = VanguardMedicalPlanReadOnlyBuilder.Build(need, actionability, safety);
        VanguardRuntimePerformanceGuard.End("DecisionSnapshotMedicalPlan", planStarted);
        string classification = ClassifyMedical(need, actionability, safety);
        if (!string.Equals(actionableFallback, "none", StringComparison.OrdinalIgnoreCase))
        {
            classification += ";deferredSurgeryDebt=true;actionableFallback=" + actionableFallback;
        }

        return new VanguardMedicalDecisionSnapshot
        {
            Alive = true,
            ControllerObserved = healthController != null || activeHealthController != null,
            ControllerType = VanguardOperatorRuntimeAuditReflection.TypeName(activeHealthController ?? healthController),
            Need = need,
            Inventory = inventory.Snapshot,
            Actionability = actionability,
            Safety = safety,
            Plan = plan,
            Classification = classification
        };
    }

    private static VanguardMedicalDecisionSnapshot RefreshCachedMedicalSafety(
        BotOwner? botOwner,
        bool alive,
        VanguardThreatDecisionSnapshot threat,
        VanguardThreatScanDecisionSnapshot threatScan,
        VanguardMedicalDecisionSnapshot cached)
    {
        if (!alive || cached == VanguardMedicalDecisionSnapshot.Empty || !cached.Alive)
        {
            return cached;
        }

        long safetyStarted = VanguardRuntimePerformanceGuard.Begin();
        VanguardMedicalSafetySnapshot safety = BuildSafety(botOwner, threat, threatScan);
        VanguardRuntimePerformanceGuard.End("DecisionSnapshotMedicalSafetyCached", safetyStarted);
        VanguardMedicalPlanSnapshot plan = VanguardMedicalPlanReadOnlyBuilder.Build(cached.Need, cached.Actionability, safety);
        string classification = ClassifyMedical(cached.Need, cached.Actionability, safety);
        int debtIndex = cached.Classification.IndexOf(";deferredSurgeryDebt=true", StringComparison.OrdinalIgnoreCase);
        if (debtIndex >= 0)
        {
            classification += cached.Classification.Substring(debtIndex);
        }

        return new VanguardMedicalDecisionSnapshot
        {
            Alive = cached.Alive,
            ControllerObserved = cached.ControllerObserved,
            ControllerType = cached.ControllerType,
            Need = cached.Need,
            Inventory = cached.Inventory,
            Actionability = cached.Actionability,
            Safety = safety,
            Plan = plan,
            Classification = classification
        };
    }

    private static bool TryBuildActionableFractureFallback(
        BotOwner? botOwner,
        VanguardMedicalNeedSnapshot originalNeed,
        VanguardMedicalInventoryReadResult inventory,
        VanguardMedicalActionabilitySnapshot originalActionability,
        out VanguardMedicalNeedSnapshot fallbackNeed,
        out VanguardMedicalActionabilitySnapshot fallbackActionability,
        out string reason)
    {
        fallbackNeed = originalNeed;
        fallbackActionability = originalActionability;
        reason = "none";
        if (botOwner == null
            || !VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(originalNeed.DominantNeed)
            || originalActionability.RequiredItemAvailable
            || !originalNeed.HasFracture
            || originalNeed.HasHeavyBleed
            || originalNeed.HasLightBleed)
        {
            return false;
        }

        string fractureTarget = FirstBodyPart(originalNeed.BrokenParts);
        if (string.Equals(fractureTarget, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fallbackNeed = new VanguardMedicalNeedSnapshot
        {
            IsReadable = originalNeed.IsReadable,
            DominantNeed = VanguardMedicalNeed.Fracture,
            HealthPercent = originalNeed.HealthPercent,
            HasHeavyBleed = originalNeed.HasHeavyBleed,
            HasLightBleed = originalNeed.HasLightBleed,
            HasFracture = originalNeed.HasFracture,
            HasPain = originalNeed.HasPain,
            HasTremor = originalNeed.HasTremor,
            HasDestroyedPart = originalNeed.HasDestroyedPart,
            HasHpDamage = originalNeed.HasHpDamage,
            HasBlackBroken = originalNeed.HasBlackBroken,
            HasOperableDestroyedPart = originalNeed.HasOperableDestroyedPart,
            HasUntreatableVitalDamage = originalNeed.HasUntreatableVitalDamage,
            UntreatableVitalPartCount = originalNeed.UntreatableVitalPartCount,
            UntreatableVitalParts = originalNeed.UntreatableVitalParts,
            DestroyedPartCount = originalNeed.DestroyedPartCount,
            DamagedPartCount = originalNeed.DamagedPartCount,
            BrokenPartCount = originalNeed.BrokenPartCount,
            TargetKnown = true,
            TargetPart = fractureTarget,
            Badges = originalNeed.Badges,
            DestroyedParts = originalNeed.DestroyedParts,
            DamagedParts = originalNeed.DamagedParts,
            BrokenParts = originalNeed.BrokenParts,
            RawEffectNames = originalNeed.RawEffectNames,
            Source = originalNeed.Source + ";actionableFallback=fracture_while_surgery_item_missing"
        };
        fallbackActionability = VanguardMedicalActionabilityReader.Capture(botOwner, fallbackNeed, inventory);
        if (!fallbackActionability.RequiredItemAvailable)
        {
            fallbackNeed = originalNeed;
            fallbackActionability = originalActionability;
            return false;
        }

        reason = "fracture_selected_while_surgery_item_missing";
        return true;
    }

    private static string FirstBodyPart(string? list)
    {
        if (string.IsNullOrWhiteSpace(list) || string.Equals(list, "none", StringComparison.OrdinalIgnoreCase))
        {
            return "none";
        }

        foreach (string token in list.Split(','))
        {
            string candidate = token.Trim();
            if (Enum.TryParse(candidate, true, out EBodyPart part)
                && part != EBodyPart.Head
                && part != EBodyPart.Chest
                && part != EBodyPart.Common)
            {
                return part.ToString();
            }
        }

        return "none";
    }

    private static VanguardMedicalSafetySnapshot BuildSafety(BotOwner? botOwner, VanguardThreatDecisionSnapshot threat, VanguardThreatScanDecisionSnapshot threatScan)
    {
        bool enemyVisible = threat.EnemyVisible == true || threat.EnemyLineOfSight == true || threatScan.CandidateVisible || threatScan.CandidateLineOfSight;
        bool enemyCanShoot = threat.EnemyCanShoot == true || threatScan.CandidateCanShoot;
        bool nearMissPressure = VanguardNearMissSuppressionService.IsRecent(
            botOwner?.ProfileId,
            DateTimeOffset.UtcNow,
            out _);
        bool incomingFireRecent = threat.ShotMeRecently == true
            || threat.ShotAtMeRecently == true
            || threatScan.CandidateIncomingFireFresh
            || threatScan.CandidateShotMeRecently
            || threatScan.CandidateShotAtMeRecently
            || nearMissPressure;
        float? threatDistance = threat.Distance ?? threatScan.CandidateDistance;
        bool closeThreat = IsCloseMedicalThreat(threat.Distance) || IsCloseMedicalThreat(threatScan.CandidateDistance);
        bool promotedImmediate = threatScan.WouldPromote && (threatScan.CandidateVisible || threatScan.CandidateLineOfSight || threatScan.CandidateCanShoot || IsCloseMedicalThreat(threatScan.CandidateDistance));

        // Runtime invariant: a fresh hit/suppression signal should not always block bleed stabilization.
        // Dogfight, visible LOS, can-shoot and close threat still block.  Distant suppression
        // without visible/can-shoot proof opens a short mobile bleed window, especially for heavy bleed.
        bool immediateCombatBlock = enemyVisible || enemyCanShoot || closeThreat || promotedImmediate;
        bool coveredSuppressionOpportunity = incomingFireRecent && !immediateCombatBlock && IsDistantOrUnknown(threatDistance);
        bool direct = threat.DirectThreat || enemyVisible || enemyCanShoot || threatScan.WouldPromote;
        bool residual = !direct && threat.ResidualThreat;
        bool stale = threat.StaleThreat;
        bool coveredOrHoldingAngle = IsCoveredOrHoldingAngle(botOwner);
        bool safeForMobile = !immediateCombatBlock && (!threatScan.WouldPromote || coveredSuppressionOpportunity);

        // Runtime invariant: surgery is no longer treated like a generic stationary aid.
        // SAIN's BotSurgery.CheckEnemies() blocks surgery around visible enemies,
        // recently seen/known enemies and short paths. Vanguard mirrors that doctrine
        // without mutating SAIN: hard danger blocks, recent-but-distant contact requires
        // real cover/hold-angle, and stale/no-contact can proceed.
        bool surgeryThreatRecentlySeen = IsRecent(threat.TimeSinceSeen, 60f) || IsRecent(threatScan.CandidateTimeSinceSeen, 60f);
        bool surgeryThreatRecentlyKnown = IsRecent(threat.TimeSinceKnownUpdated, 60f)
            || (threat.HasThreat && !threat.StaleThreat && !threat.TimeSinceKnownUpdated.HasValue && (direct || residual));
        bool surgeryThreatPathTooClose = IsCloseSurgeryThreat(threat.PathLength);
        bool surgeryThreatDistanceTooClose = IsCloseSurgeryThreat(threat.Distance) || IsCloseSurgeryThreat(threatScan.CandidateDistance);
        bool surgeryHardBlock = enemyVisible
            || enemyCanShoot
            || incomingFireRecent
            || promotedImmediate;
        bool surgeryRequiresCover = surgeryThreatRecentlySeen
            || surgeryThreatRecentlyKnown
            || surgeryThreatPathTooClose
            || surgeryThreatDistanceTooClose
            || threatScan.WouldPromote
            || direct
            || residual;
        bool surgeryAreaClear = !surgeryHardBlock && (!surgeryRequiresCover || coveredOrHoldingAngle);
        string surgeryAreaReason = SurgeryAreaReason(
            surgeryAreaClear,
            surgeryHardBlock,
            surgeryRequiresCover,
            coveredOrHoldingAngle,
            enemyVisible,
            enemyCanShoot,
            incomingFireRecent,
            promotedImmediate,
            surgeryThreatRecentlySeen,
            surgeryThreatRecentlyKnown,
            surgeryThreatPathTooClose,
            surgeryThreatDistanceTooClose);

        bool safeForSurgery = surgeryAreaClear;
        bool safeForStationaryAid = !enemyCanShoot
            && !closeThreat
            && !promotedImmediate
            && (safeForSurgery
                || (coveredOrHoldingAngle && !incomingFireRecent)
                || (!enemyVisible && IsDistantOrUnknown(threatDistance))
                || (stale && !enemyVisible));
        string reason = immediateCombatBlock ? "immediate_combat_block"
            : coveredOrHoldingAngle ? "covered_or_holding_angle"
            : coveredSuppressionOpportunity ? "covered_suppression_mobile_bleed_window"
            : direct ? "direct_or_promoted_threat"
            : residual ? "residual_threat"
            : stale ? "stale_threat" : "no_threat";

        return new VanguardMedicalSafetySnapshot
        {
            DirectThreat = direct,
            ResidualThreat = residual,
            StaleThreat = stale,
            EnemyVisible = enemyVisible,
            EnemyCanShoot = enemyCanShoot,
            ThreatScanWouldPromote = threatScan.WouldPromote,
            IncomingFireRecent = incomingFireRecent,
            ImmediateCombatBlock = immediateCombatBlock,
            CoveredSuppressionOpportunity = coveredSuppressionOpportunity,
            ThreatDistance = threatDistance,
            SafeForMobileAid = safeForMobile,
            SafeForStationarySurgery = safeForSurgery,
            SafeForStationaryAid = safeForStationaryAid,
            CoveredOrHoldingAngle = coveredOrHoldingAngle,
            SurgeryAreaClear = surgeryAreaClear,
            SurgeryRequiresCover = surgeryRequiresCover,
            SurgeryThreatRecentlySeen = surgeryThreatRecentlySeen,
            SurgeryThreatRecentlyKnown = surgeryThreatRecentlyKnown,
            SurgeryThreatPathTooClose = surgeryThreatPathTooClose,
            SurgeryThreatDistanceTooClose = surgeryThreatDistanceTooClose,
            SurgeryAreaClearReason = surgeryAreaReason,
            Reason = reason
        };
    }

    private static bool IsCoveredOrHoldingAngle(BotOwner? botOwner)
    {
        if (botOwner == null)
        {
            return false;
        }

        object? memory = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Memory");
        if (VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(memory, "IsInCover") is bool inCover && inCover)
        {
            return true;
        }

        object? sainBot = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "SAIN.Components.BotComponent");
        object? sainCover = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBot, "Cover");
        object? coverInUse = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainCover, "CoverInUse");
        string sainCoverState = VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainCover, "CoverSeekingState"));
        if (coverInUse != null || sainCoverState.Contains("HoldInCover", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string state = string.Join("|", new[]
        {
            VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "Brain", "BaseBrain", "Node")?.ToString(),
            VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "Brain", "ActiveLayer")?.ToString(),
            VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "Memory", "CurCustomCoverPoint")?.ToString(),
            coverInUse?.ToString(),
        });

        return state.Contains("cover", StringComparison.OrdinalIgnoreCase)
            || state.Contains("holdangle", StringComparison.OrdinalIgnoreCase)
            || state.Contains("hold_angle", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCloseMedicalThreat(float? distance)
    {
        return distance.HasValue && distance.Value >= 0f && distance.Value <= 18f;
    }

    private static bool IsDistantOrUnknown(float? distance)
    {
        return !distance.HasValue || distance.Value >= 25f;
    }

    private static bool IsRecent(float? seconds, float thresholdSeconds)
    {
        return seconds.HasValue && seconds.Value >= 0f && seconds.Value < thresholdSeconds;
    }

    private static bool IsCloseSurgeryThreat(float? distanceOrPath)
    {
        return distanceOrPath.HasValue && distanceOrPath.Value >= 0f && distanceOrPath.Value < 80f;
    }

    private static string SurgeryAreaReason(
        bool areaClear,
        bool hardBlock,
        bool requiresCover,
        bool coveredOrHoldingAngle,
        bool enemyVisible,
        bool enemyCanShoot,
        bool incomingFireRecent,
        bool promotedImmediate,
        bool recentlySeen,
        bool recentlyKnown,
        bool pathTooClose,
        bool distanceTooClose)
    {
        if (areaClear)
        {
            return requiresCover ? "sain_like_area_clear_with_cover_or_hold" : "sain_like_area_clear_no_recent_threat";
        }

        if (enemyVisible) return "enemy_visible";
        if (enemyCanShoot) return "enemy_can_shoot";
        if (incomingFireRecent) return "incoming_fire_recent";
        if (promotedImmediate) return "promoted_immediate_threat";
        if (pathTooClose) return "enemy_path_under_80m";
        if (distanceTooClose) return "enemy_distance_under_80m";
        if (requiresCover && !coveredOrHoldingAngle)
        {
            if (recentlySeen) return "enemy_seen_under_60s_without_cover";
            if (recentlyKnown) return "enemy_last_known_under_60s_without_cover";
            return "recent_or_residual_threat_without_cover";
        }

        return hardBlock ? "sain_like_hard_block" : "sain_like_area_not_clear";
    }

    private static string ClassifyMedical(VanguardMedicalNeedSnapshot need, VanguardMedicalActionabilitySnapshot actionability, VanguardMedicalSafetySnapshot safety)
    {
        if (!need.IsReadable)
        {
            return "medical_unreadable";
        }

        if (!need.HasAnyNeed)
        {
            return "medical_healthy";
        }

        if (need.DominantNeed == VanguardMedicalNeed.UntreatableVitalDestroyedPart)
        {
            return "medical_terminal_untreatable_vital_damage";
        }

        string needKey = SanitizeKey(need.DominantNeed.ToString());
        if (safety.DirectThreat && IsSurgeryNeed(need.DominantNeed))
        {
            return "medical_need_" + needKey + "_blocked_by_direct_threat";
        }

        if (IsSurgeryNeed(need.DominantNeed) && VanguardMedicalSurgeryTargetPolicy.TryResolveTarget(actionability.TargetPart, need.TargetPart, out var surgeryTarget))
        {
            if (VanguardMedicalSurgeryTargetPolicy.IsUntreatableVitalTarget(surgeryTarget))
            {
                return "medical_need_" + needKey + "_untreatable_vital_part";
            }

            if (!VanguardMedicalSurgeryTargetPolicy.IsValidSurgeryTarget(surgeryTarget))
            {
                return "medical_need_" + needKey + "_invalid_surgery_target";
            }
        }

        if (!actionability.RequiredItemAvailable)
        {
            return "medical_need_" + needKey + "_item_missing";
        }

        if (!actionability.TargetKnown)
        {
            return "medical_need_" + needKey + "_target_unknown";
        }

        if (actionability.CanApplyItem == false)
        {
            return "medical_need_" + needKey + "_controller_rejected";
        }

        if (IsSurgeryNeed(need.DominantNeed) && !safety.SafeForStationarySurgery)
        {
            return "medical_need_" + needKey + "_await_safe_window";
        }

        if (need.DominantNeed == VanguardMedicalNeed.Fracture && !safety.SafeForStationaryAid)
        {
            return "medical_need_" + needKey + "_await_stationary_aid_safe_window";
        }

        return "medical_need_" + needKey + "_ready_readonly";
    }
    private static bool IsSurgeryNeed(VanguardMedicalNeed need)
    {
        return VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(need);
    }
}
#endif
