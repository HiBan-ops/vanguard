#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Decision;

// Responsibility: maintains one exact, context-sensitive preferred carried weapon for each live Vanguard Operator.
// Flow: current equipment material + already-qualified Decision/Awareness threat context -> bounded preferred slot/item sidecar -> one native selector convergence request when preference materially changes; later native "return to main" requests are routed to the same exact preference.
// Authority boundary: Vanguard never rewrites EFT BotWeaponSelector.MainWeapon/SupportWeapon topology and never selects a target, aims or fires. EFT/SAIN still owns the actual slot transition and all local weapon/reload execution.
// Invariant: baseline preference only corrects stale spawn-time main semantics (notably pistol-only spawn followed by a usable looted long weapon); close/long context only specializes already-carried usable weapons and preserves the existing owner-scoped 25-35 m hysteresis envelope. Sustainment is a bounded tie-break inside an already-credible context, never an autonomous low-magazine force-swap trigger.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class VanguardDynamicWeaponPreferenceService
{
    public const string StatusTag = "VANGUARD_DYNAMIC_WEAPON_PREFERENCE";

    internal const int CriticalLowReadyRounds = 2;
    private const int CloseWeaponComfortReadyRounds = 5;
    private static readonly TimeSpan EquipmentCacheLifetime = TimeSpan.FromSeconds(1.25d);
    private static readonly TimeSpan PreferenceFreshness = TimeSpan.FromSeconds(2.0d);
    private static readonly TimeSpan ConvergenceRetry = TimeSpan.FromSeconds(1.25d);
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(10d);

    private static readonly object Sync = new();
    private static readonly Dictionary<string, ActiveWeaponPreference> PreferenceByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CachedEquipmentObservation> EquipmentCacheByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> NextConvergenceAttemptAtByBot = new(StringComparer.OrdinalIgnoreCase);

    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;
    private static int refreshesTotal;
    private static int changesTotal;
    private static int baselineLongPromotionsTotal;
    private static int closeContextPreferencesTotal;
    private static int longContextPreferencesTotal;
    private static int hysteresisKeepsTotal;
    private static int decisionThreatContextsTotal;
    private static int awarenessThreatContextsTotal;
    private static int convergenceRequestsTotal;
    private static int convergenceAcceptedTotal;
    private static int convergenceDeferredTotal;
    private static int nativeMainRoutesTotal;
    private static int nativeMainRedundantSuppressionsTotal;
    private static int releasesTotal;

    /// <summary>
    /// Refreshes exact preferred-weapon state for current Operators. Returns true only when one native selector
    /// transition was accepted this tick so TacticalOpportunity can avoid starting another sidecar action simultaneously.
    /// </summary>
    public static bool Tick(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            ResetForRaidLifecycle("not_raid_authority");
            return false;
        }

        IReadOnlyList<VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation> qualifiedContacts =
            VanguardCombatAwarenessBridge.GetFreshQualifiedTacticalCoordinationThreats(now);

        bool nativeSwitchAccepted = false;
        foreach (OperatorDecisionSnapshot snapshot in snapshots)
        {
            if (snapshot == null
                || !snapshot.Alive
                || string.IsNullOrWhiteSpace(snapshot.BotProfileId)
                || string.IsNullOrWhiteSpace(snapshot.OwnerProfileId)
                || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out VanguardRaidOperatorRuntimeRecord runtime)
                || runtime.BotOwner == null
                || runtime.BotOwner.IsDead)
            {
                continue;
            }

            BotOwner botOwner = runtime.BotOwner;
            BotWeaponSelector? selector = botOwner.WeaponManager?.Selector;
            if (selector == null)
            {
                continue;
            }

            VanguardLiveEquipmentCapabilitySnapshot equipment = CaptureEquipmentCached(botOwner, now);
            if (!equipment.Observed || equipment.Weapons.Count == 0)
            {
                Release(snapshot.BotProfileId, "equipment_unobserved_or_empty");
                continue;
            }

            VanguardWeaponSustainmentShadowService.Observe(
                snapshot.BotProfileId,
                snapshot.OwnerProfileId,
                equipment,
                now);

            if (!TryResolvePreference(
                    snapshot,
                    botOwner,
                    selector,
                    equipment,
                    qualifiedContacts,
                    now,
                    out PreferenceResolution resolution))
            {
                Release(snapshot.BotProfileId, "no_operational_preference");
                continue;
            }

            bool changed = PublishPreference(snapshot, resolution, now);
            if (!nativeSwitchAccepted
                && TryConvergeNativeSelector(snapshot, botOwner, selector, resolution, changed, now))
            {
                nativeSwitchAccepted = true;
            }
        }

        EmitSummaryIfDue(now);
        return nativeSwitchAccepted;
    }

    /// <summary>
    /// Shared bounded equipment read-model for tactical weapon consumers. This prevents DynamicPreference and
    /// TacticalOpportunity/Overwatch from traversing the same live inventory twice inside one acquisition window.
    /// </summary>
    internal static VanguardLiveEquipmentCapabilitySnapshot GetEquipmentSnapshot(BotOwner botOwner, DateTimeOffset now)
        => CaptureEquipmentCached(botOwner, now);

    /// <summary>
    /// Handles only EFT/SAIN's semantic "return to main" request. Exact support/secondary/emergency
    /// TryChangeToSlot calls never cross this boundary. Returns true only when Vanguard handled the
    /// ChangeToMain request and the Harmony prefix must skip the static native MainWeapon call.
    /// </summary>
    internal static bool TryHandleNativeMainRequest(
        BotWeaponSelector selector,
        DateTimeOffset now,
        out bool nativeResult,
        out string reason)
    {
        nativeResult = false;
        reason = "no_dynamic_main_override";

        if (selector == null
            || selector.BotOwner_0 == null
            || selector.BotOwner_0.IsDead
            || string.IsNullOrWhiteSpace(selector.BotOwner_0.ProfileId))
        {
            return false;
        }

        string botProfileId = selector.BotOwner_0.ProfileId.Trim();
        ActiveWeaponPreference? preference = FindPreference(botProfileId);
        if (preference == null)
        {
            reason = "no_dynamic_preference";
            return false;
        }

        if (now - preference.ObservedAtUtc > PreferenceFreshness)
        {
            Release(botProfileId, "preference_stale_at_native_main_route");
            reason = "preference_stale";
            return false;
        }

        if (!TryGetExactWeapon(selector.BotOwner_0, preference.PreferredSlot, preference.PreferredWeaponItemId, out EFT.InventoryLogic.Weapon? preferredWeapon)
            || preferredWeapon == null)
        {
            Release(botProfileId, "preferred_weapon_identity_changed");
            reason = "preferred_weapon_identity_changed";
            return false;
        }

        int liveReadyRounds = Math.Max(0, preferredWeapon.GetCurrentMagazineCount()) + Math.Max(0, preferredWeapon.ChamberAmmoCount);
        if (!string.IsNullOrWhiteSpace(preference.ThreatId) && liveReadyRounds <= 0)
        {
            reason = "preferred_weapon_not_live_ready_for_combat";
            return false;
        }

        if (preference.PreferredSlot == selector.MainWeapon)
        {
            reason = "dynamic_preference_matches_native_main";
            return false;
        }

        if (selector.EquipmentSlot == preference.PreferredSlot)
        {
            lock (Sync)
            {
                nativeMainRedundantSuppressionsTotal++;
            }
            nativeResult = true;
            reason = "already_on_dynamic_preferred_weapon";
            VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                $"DYNAMIC_WEAPON_PREFERENCE_NATIVE_MAIN_REDUNDANT_SUPPRESSED bot={Safe(botProfileId)}; nativeMain={selector.MainWeapon}; preferred={preference.PreferredSlot}; preferredItem={Safe(preference.PreferredWeaponItemId)}; mode={Safe(preference.Mode)}; targetAimFireMutation=false; nativeTopologyMutation=false");
            return true;
        }

        selector.UpdateWeaponsList();
        nativeResult = selector.TryChangeToSlot(preference.PreferredSlot, changeToMain: true);
        bool accepted = nativeResult;
        lock (Sync)
        {
            nativeMainRoutesTotal++;
        }
        reason = accepted
            ? "native_main_routed_to_dynamic_preference"
            : "dynamic_preference_native_selector_rejected";
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"DYNAMIC_WEAPON_PREFERENCE_NATIVE_MAIN_ROUTED bot={Safe(botProfileId)}; nativeMain={selector.MainWeapon}; routed={preference.PreferredSlot}; preferredItem={Safe(preference.PreferredWeaponItemId)}; family={Safe(preference.PreferredFamily)}; mode={Safe(preference.Mode)}; threat={Safe(preference.ThreatId)}; accepted={accepted}; nativeTopologyMutation=false; targetAimFireMutation=false");
        return true;
    }

    /// <summary>
    /// Emergency close-reload swap already happened through EFT. This notification aligns the exact accepted
    /// alternative immediately and remembers the departed weapon so close-threat preference cannot bounce back
    /// while that weapon remains critically low. The native selector still owns the physical transition.
    /// </summary>
    internal static bool NotifyCloseCombatNativeSwap(
        BotOwner botOwner,
        string threatId,
        EquipmentSlot fromSlot,
        EquipmentSlot toSlot,
        DateTimeOffset now)
    {
        if (botOwner == null
            || botOwner.IsDead
            || string.IsNullOrWhiteSpace(botOwner.ProfileId)
            || string.IsNullOrWhiteSpace(threatId)
            || fromSlot == toSlot
            || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botOwner.ProfileId, out VanguardRaidOperatorRuntimeRecord runtime))
        {
            return false;
        }

        InvalidateEquipmentCache(botOwner.ProfileId);
        VanguardLiveEquipmentCapabilitySnapshot equipment = CaptureEquipmentCached(botOwner, now);
        VanguardLiveWeaponCapabilityObservation? source = FindWeapon(equipment, fromSlot);
        VanguardLiveWeaponCapabilityObservation? target = FindWeapon(equipment, toSlot);
        if (source == null
            || target == null
            || !IsCloseCombatReady(target))
        {
            return false;
        }

        VanguardTacticalCoordinationTuningPolicy.OwnerTacticalCoordinationTuning tuning =
            VanguardTacticalCoordinationTuningPolicy.ForOwner(runtime.OwnerProfileId);
        var resolution = new PreferenceResolution(
            target,
            toSlot,
            "CRITICAL_LOW_AMMO_CLOSE",
            "critical_low_ammo_native_swap",
            threatId,
            tuning.DangerCloseWeaponMeters,
            "critical_reload_native_swap",
            fromSlot,
            source.ItemId);

        PublishPreference(botOwner.ProfileId, runtime.OwnerProfileId, resolution, now);
        return true;
    }

    internal static bool ForgetRuntimePresence(string? botProfileId, string reason)
        => Release(botProfileId, "runtime_presence_lost:" + Safe(reason));

    public static void ResetForRaidLifecycle(string reason)
    {
        int cleared;
        lock (Sync)
        {
            cleared = PreferenceByBot.Count;
            PreferenceByBot.Clear();
            EquipmentCacheByBot.Clear();
            NextConvergenceAttemptAtByBot.Clear();
            nextSummaryAtUtc = DateTimeOffset.MinValue;
            bootLogged = false;
            refreshesTotal = 0;
            changesTotal = 0;
            baselineLongPromotionsTotal = 0;
            closeContextPreferencesTotal = 0;
            longContextPreferencesTotal = 0;
            hysteresisKeepsTotal = 0;
            decisionThreatContextsTotal = 0;
            awarenessThreatContextsTotal = 0;
            convergenceRequestsTotal = 0;
            convergenceAcceptedTotal = 0;
            convergenceDeferredTotal = 0;
            nativeMainRoutesTotal = 0;
            nativeMainRedundantSuppressionsTotal = 0;
            releasesTotal = 0;
        }

        VanguardWeaponSustainmentShadowService.ResetForRaidLifecycle();

        if (cleared > 0)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"DYNAMIC_WEAPON_PREFERENCE_RESET reason={Safe(reason)}; cleared={cleared}; nativeTopologyMutation=false");
        }
    }

    private static bool TryResolvePreference(
        OperatorDecisionSnapshot snapshot,
        BotOwner botOwner,
        BotWeaponSelector selector,
        VanguardLiveEquipmentCapabilitySnapshot equipment,
        IReadOnlyList<VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation> qualifiedContacts,
        DateTimeOffset now,
        out PreferenceResolution resolution)
    {
        resolution = default;
        ActiveWeaponPreference? existing = FindPreference(snapshot.BotProfileId);
        VanguardLiveWeaponCapabilityObservation? baseline = ChooseBaselineOperationalWeapon(equipment, selector, existing);
        if (baseline == null || !TryParseSlot(baseline.Slot, out EquipmentSlot baselineSlot))
        {
            return false;
        }

        bool hasThreat = TryResolveThreatContext(snapshot, botOwner, qualifiedContacts, out WeaponThreatContext threat);
        VanguardTacticalCoordinationTuningPolicy.OwnerTacticalCoordinationTuning tuning =
            VanguardTacticalCoordinationTuningPolicy.ForOwner(snapshot.OwnerProfileId);

        if (!hasThreat)
        {
            resolution = new PreferenceResolution(
                baseline,
                baselineSlot,
                "BASELINE_OPERATIONAL",
                baseline.IsLongWeapon && selector.MainWeapon == EquipmentSlot.Holster
                    ? "stale_spawn_holster_main_corrected"
                    : "stable_operational_baseline",
                string.Empty,
                -1f,
                "no_fresh_qualified_threat",
                null,
                string.Empty);
            return true;
        }

        if (threat.FromDecisionSnapshot)
        {
            lock (Sync) decisionThreatContextsTotal++;
        }
        else
        {
            lock (Sync) awarenessThreatContextsTotal++;
        }

        VanguardLiveWeaponCapabilityObservation? specialist = ChooseLongRangeSpecialist(equipment, existing, selector.EquipmentSlot);
        VanguardLiveWeaponCapabilityObservation? existingWeapon = existing != null
            ? FindExactWeapon(equipment, existing.PreferredSlot, existing.PreferredWeaponItemId)
            : null;
        VanguardLiveWeaponCapabilityObservation? currentWeapon = FindWeapon(equipment, selector.EquipmentSlot);
        VanguardLiveWeaponCapabilityObservation? priorDisfavoredWeapon = existing?.DisfavoredWeaponSlot is EquipmentSlot priorWeaponSlot
            ? FindExactWeapon(equipment, priorWeaponSlot, existing.DisfavoredWeaponItemId)
            : null;

        bool criticalLowAmmoContinuity = existing != null
            && string.Equals(existing.Mode, "CRITICAL_LOW_AMMO_CLOSE", StringComparison.OrdinalIgnoreCase)
            && priorDisfavoredWeapon != null
            && priorDisfavoredWeapon.ReadyRounds <= CriticalLowReadyRounds
            && IsCloseCombatReady(existingWeapon);

        if (threat.DistanceMeters <= tuning.ProactiveWeaponMeters && criticalLowAmmoContinuity)
        {
            VanguardLiveWeaponCapabilityObservation? close = ChooseCloseCombatWeapon(equipment, selector.EquipmentSlot);
            if (close != null && TryParseSlot(close.Slot, out EquipmentSlot closeSlot))
            {
                resolution = new PreferenceResolution(
                    close,
                    closeSlot,
                    "CRITICAL_LOW_AMMO_CLOSE",
                    "critical_low_ammo_close_continuity",
                    threat.ThreatId,
                    threat.DistanceMeters,
                    threat.Source,
                    existing!.DisfavoredWeaponSlot,
                    existing.DisfavoredWeaponItemId);
                return true;
            }
        }

        bool closeContextAnchoredToSpecialist = IsLongRangeSpecialist(existingWeapon)
            || IsLongRangeSpecialist(currentWeapon)
            || IsLongRangeSpecialist(priorDisfavoredWeapon)
            || (existing == null && IsLongRangeSpecialist(baseline));

        if (threat.DistanceMeters <= tuning.ProactiveWeaponMeters && closeContextAnchoredToSpecialist)
        {
            VanguardLiveWeaponCapabilityObservation? close = ChooseCloseCombatWeapon(equipment, selector.EquipmentSlot);
            if (close != null && TryParseSlot(close.Slot, out EquipmentSlot closeSlot))
            {
                string mode = threat.DistanceMeters <= tuning.DangerCloseWeaponMeters ? "DANGER_CLOSE" : "CLOSE_THREAT";
                resolution = new PreferenceResolution(
                    close,
                    closeSlot,
                    mode,
                    "long_range_specialist_close_context",
                    threat.ThreatId,
                    threat.DistanceMeters,
                    threat.Source,
                    specialist != null && TryParseSlot(specialist.Slot, out EquipmentSlot disfavoredSlot) ? disfavoredSlot : null,
                    specialist?.ItemId ?? string.Empty);
                return true;
            }
        }

        if (threat.DistanceMeters >= tuning.OverwatchMinimumThreatMeters
            && specialist != null
            && TryParseSlot(specialist.Slot, out EquipmentSlot specialistSlot))
        {
            resolution = new PreferenceResolution(
                specialist,
                specialistSlot,
                "LONG_RANGE_THREAT",
                "qualified_far_threat_long_range_specialist",
                threat.ThreatId,
                threat.DistanceMeters,
                threat.Source,
                null,
                string.Empty);
            return true;
        }

        if (threat.DistanceMeters > tuning.ProactiveWeaponMeters
            && threat.DistanceMeters < tuning.OverwatchMinimumThreatMeters
            && existingWeapon != null
            && TryParseSlot(existingWeapon.Slot, out EquipmentSlot hysteresisSlot))
        {
            lock (Sync) hysteresisKeepsTotal++;
            resolution = new PreferenceResolution(
                existingWeapon,
                hysteresisSlot,
                "HYSTERESIS_KEEP",
                "owner_scoped_25_35_hysteresis",
                threat.ThreatId,
                threat.DistanceMeters,
                threat.Source,
                existing?.DisfavoredWeaponSlot,
                existing?.DisfavoredWeaponItemId ?? string.Empty);
            return true;
        }

        resolution = new PreferenceResolution(
            baseline,
            baselineSlot,
            "BASELINE_OPERATIONAL",
            "qualified_threat_does_not_require_specialized_weapon_override",
            threat.ThreatId,
            threat.DistanceMeters,
            threat.Source,
            null,
            string.Empty);
        return true;
    }

    private static VanguardLiveWeaponCapabilityObservation? ChooseBaselineOperationalWeapon(
        VanguardLiveEquipmentCapabilitySnapshot equipment,
        BotWeaponSelector selector,
        ActiveWeaponPreference? existing)
    {
        // Baseline remains structural, not a universal weapon-quality score. Exact preference/current-slot
        // continuity is the switching-cost anchor. Sustainment only ranks already-operational alternatives
        // after immediate utility, and loaded magazine reserve always outranks loose-only reserve.
        VanguardLiveWeaponCapabilityObservation? exactExisting = existing != null
            ? FindExactWeapon(equipment, existing.PreferredSlot, existing.PreferredWeaponItemId)
            : null;
        VanguardLiveWeaponCapabilityObservation? current = FindWeapon(equipment, selector.EquipmentSlot);
        VanguardLiveWeaponCapabilityObservation? first = FindWeapon(equipment, EquipmentSlot.FirstPrimaryWeapon);
        VanguardLiveWeaponCapabilityObservation? second = FindWeapon(equipment, EquipmentSlot.SecondPrimaryWeapon);

        if (IsGeneralPurposeOperationalLongWeapon(exactExisting)) return exactExisting;
        if (IsGeneralPurposeOperationalLongWeapon(current)) return current;

        VanguardLiveWeaponCapabilityObservation? primaryGeneralPurpose = new[] { first, second }
            .Where(IsGeneralPurposeOperationalLongWeapon)
            .OrderByDescending(item => item!.ReadyRounds)
            .ThenByDescending(item => LoadedMagazineSustainmentRank(item!))
            .ThenByDescending(item => item!.CompatibleMagazineReloadDepthEquivalent)
            .ThenByDescending(item => item!.CompatibleMagazineAmmoCount)
            .ThenByDescending(item => item!.CompatibleLooseAmmunitionCount)
            .FirstOrDefault();
        if (primaryGeneralPurpose != null) return primaryGeneralPurpose;

        if (IsOperationalLongWeapon(exactExisting)) return exactExisting;
        if (IsOperationalLongWeapon(current)) return current;

        VanguardLiveWeaponCapabilityObservation? primaryLong = new[] { first, second }
            .Where(IsOperationalLongWeapon)
            .OrderByDescending(item => item!.ReadyRounds)
            .ThenByDescending(item => LoadedMagazineSustainmentRank(item!))
            .ThenByDescending(item => item!.CompatibleMagazineReloadDepthEquivalent)
            .ThenByDescending(item => item!.CompatibleMagazineAmmoCount)
            .ThenByDescending(item => item!.CompatibleLooseAmmunitionCount)
            .FirstOrDefault();
        if (primaryLong != null) return primaryLong;

        VanguardLiveWeaponCapabilityObservation? holster = FindWeapon(equipment, EquipmentSlot.Holster);
        if (IsOperationalWeapon(holster))
        {
            return holster;
        }

        return equipment.Weapons
            .Where(IsOperationalWeapon)
            .OrderByDescending(item => item.ReadyRounds)
            .ThenByDescending(LoadedMagazineSustainmentRank)
            .ThenByDescending(item => item.CompatibleMagazineReloadDepthEquivalent)
            .ThenByDescending(item => item.CompatibleMagazineAmmoCount)
            .ThenByDescending(item => item.CompatibleLooseAmmunitionCount)
            .FirstOrDefault();
    }

    private static VanguardLiveWeaponCapabilityObservation? ChooseCloseCombatWeapon(
        VanguardLiveEquipmentCapabilitySnapshot equipment,
        EquipmentSlot currentSlot)
        => equipment.Weapons
            .Where(item => IsCloseCombatReady(item)
                && (!TryParseSlot(item.Slot, out EquipmentSlot slot) || slot != currentSlot || !IsLongRangeSpecialist(item)))
            // Immediate CQB utility remains authoritative. Staying on the already-held credible weapon is the
            // switching-cost tie-break; only then may loaded-magazine sustainment separate otherwise-equal choices.
            .OrderByDescending(item => item.ReadyRounds >= CloseWeaponComfortReadyRounds)
            .ThenByDescending(CloseCombatFamilyRank)
            .ThenByDescending(item => item.ReadyRounds)
            .ThenByDescending(item => IsCurrentSlot(item, currentSlot))
            .ThenByDescending(LoadedMagazineSustainmentRank)
            .ThenByDescending(item => item.CompatibleMagazineReloadDepthEquivalent)
            .ThenByDescending(item => item.CompatibleMagazineAmmoCount)
            .ThenByDescending(item => item.CompatibleLooseAmmunitionCount)
            .FirstOrDefault();

    private static VanguardLiveWeaponCapabilityObservation? ChooseLongRangeSpecialist(
        VanguardLiveEquipmentCapabilitySnapshot equipment,
        ActiveWeaponPreference? existing,
        EquipmentSlot currentSlot)
    {
        VanguardLiveWeaponCapabilityObservation? exactExisting = existing != null
            ? FindExactWeapon(equipment, existing.PreferredSlot, existing.PreferredWeaponItemId)
            : null;
        if (IsLongRangeCombatReady(exactExisting))
        {
            return exactExisting;
        }

        VanguardLiveWeaponCapabilityObservation? current = FindWeapon(equipment, currentSlot);
        if (IsLongRangeCombatReady(current))
        {
            return current;
        }

        return equipment.Weapons
            .Where(IsLongRangeCombatReady)
            // Existing/current specialist continuity above is the switching-cost anchor. Among alternative
            // specialists, optic/family/readiness stay ahead of sustainment; loose ammo is only a final fallback.
            .OrderByDescending(item => item.MagnifiedSightCount > 0)
            .ThenByDescending(item => string.Equals(item.CanonicalWeaponFamily, "SNIPER_RIFLE", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.ReadyRounds)
            .ThenByDescending(LoadedMagazineSustainmentRank)
            .ThenByDescending(item => item.CompatibleMagazineReloadDepthEquivalent)
            .ThenByDescending(item => item.CompatibleMagazineAmmoCount)
            .ThenByDescending(item => item.CompatibleLooseAmmunitionCount)
            .FirstOrDefault();
    }

    private static bool TryResolveThreatContext(
        OperatorDecisionSnapshot snapshot,
        BotOwner botOwner,
        IReadOnlyList<VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation> qualifiedContacts,
        out WeaponThreatContext context)
    {
        context = WeaponThreatContext.Empty;
        if (snapshot.Threat.HasThreat
            && !snapshot.Threat.StaleThreat
            && !string.IsNullOrWhiteSpace(snapshot.Threat.EnemyId)
            && snapshot.Threat.Distance.HasValue
            && snapshot.Threat.Distance.Value >= 0f)
        {
            context = new WeaponThreatContext(
                snapshot.Threat.EnemyId,
                snapshot.Threat.Distance.Value,
                "decision_snapshot_threat",
                fromDecisionSnapshot: true);
        }

        foreach (VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation contact in qualifiedContacts
            .Where(item => string.Equals(item.OwnerProfileId, snapshot.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.ObservedAtUtc))
        {
            float distance = HorizontalDistance(botOwner.Position, contact.RuntimePosition);
            if (float.IsNaN(distance) || float.IsInfinity(distance) || distance < 0f)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(context.ThreatId) || distance < context.DistanceMeters)
            {
                context = new WeaponThreatContext(
                    contact.TargetProfileId,
                    distance,
                    "qualified_awareness_contact:" + Safe(contact.ContactKind),
                    fromDecisionSnapshot: false);
            }
        }

        return !string.IsNullOrWhiteSpace(context.ThreatId)
            && context.DistanceMeters >= 0f
            && context.DistanceMeters < float.MaxValue;
    }

    private static bool PublishPreference(
        OperatorDecisionSnapshot snapshot,
        PreferenceResolution resolution,
        DateTimeOffset now)
        => PublishPreference(snapshot.BotProfileId, snapshot.OwnerProfileId, resolution, now);

    private static bool PublishPreference(
        string botProfileId,
        string ownerProfileId,
        PreferenceResolution resolution,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(botProfileId)
            || string.IsNullOrWhiteSpace(resolution.Weapon.ItemId))
        {
            return false;
        }

        string key = botProfileId.Trim();
        bool changed;
        ActiveWeaponPreference published;
        lock (Sync)
        {
            refreshesTotal++;
            PreferenceByBot.TryGetValue(key, out ActiveWeaponPreference? existing);
            changed = existing == null
                || existing.PreferredSlot != resolution.Slot
                || !string.Equals(existing.PreferredWeaponItemId, resolution.Weapon.ItemId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.Mode, resolution.Mode, StringComparison.OrdinalIgnoreCase);

            published = new ActiveWeaponPreference(
                key,
                ownerProfileId,
                resolution.Slot,
                resolution.Weapon.ItemId,
                resolution.Weapon.CanonicalWeaponFamily,
                resolution.Mode,
                resolution.Reason,
                resolution.ThreatId,
                resolution.ThreatDistanceMeters,
                resolution.ThreatSource,
                resolution.DisfavoredWeaponSlot,
                resolution.DisfavoredWeaponItemId,
                resolution.Weapon.ReadyRounds,
                resolution.Weapon.TotalObservedRounds,
                changed ? now : (existing?.StartedAtUtc ?? now),
                now);
            PreferenceByBot[key] = published;

            if (changed)
            {
                changesTotal++;
                if (string.Equals(resolution.Mode, "CLOSE_THREAT", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(resolution.Mode, "DANGER_CLOSE", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(resolution.Mode, "CRITICAL_LOW_AMMO_CLOSE", StringComparison.OrdinalIgnoreCase))
                {
                    closeContextPreferencesTotal++;
                }
                if (string.Equals(resolution.Mode, "LONG_RANGE_THREAT", StringComparison.OrdinalIgnoreCase))
                {
                    longContextPreferencesTotal++;
                }
                if (string.Equals(resolution.Reason, "stale_spawn_holster_main_corrected", StringComparison.OrdinalIgnoreCase))
                {
                    baselineLongPromotionsTotal++;
                }
                NextConvergenceAttemptAtByBot.Remove(key);
            }
        }

        if (changed)
        {
            VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
                $"DYNAMIC_WEAPON_PREFERENCE_CHANGED bot={Safe(key)}; owner={Safe(ownerProfileId)}; preferred={published.PreferredSlot}; preferredItem={Safe(published.PreferredWeaponItemId)}; family={Safe(published.PreferredFamily)}; readyRounds={published.ReadyRounds}; totalObservedRounds={published.TotalObservedRounds}; magazineReserve={resolution.Weapon.CompatibleMagazineAmmoCount}; looseAmmoReserve={resolution.Weapon.CompatibleLooseAmmunitionCount}; reloadDepth={resolution.Weapon.CompatibleMagazineReloadDepthEquivalent:0.00}; fullMagazines={resolution.Weapon.CompatibleFullMagazineCount}; partialMagazines={resolution.Weapon.CompatiblePartialMagazineCount}; mode={Safe(published.Mode)}; reason={Safe(published.Reason)}; threat={Safe(published.ThreatId)}; threatDistance={published.ThreatDistanceMeters:0.0}; threatSource={Safe(published.ThreatSource)}; sustainmentPolicy=context_eligible_tiebreak_after_immediate_utility_and_switch_cost; lowMagazineForceSwap=false; nativeMainTopologyPreserved=true; targetAimFireMutation=false");
        }
        return changed;
    }

    private static bool TryConvergeNativeSelector(
        OperatorDecisionSnapshot snapshot,
        BotOwner botOwner,
        BotWeaponSelector selector,
        PreferenceResolution resolution,
        bool preferenceChanged,
        DateTimeOffset now)
    {
        if (selector.EquipmentSlot == resolution.Slot)
        {
            return false;
        }

        lock (Sync)
        {
            if (NextConvergenceAttemptAtByBot.TryGetValue(snapshot.BotProfileId, out DateTimeOffset next)
                && next > now)
            {
                return false;
            }
        }

        if (!selector.IsWeaponReady
            || snapshot.GrenadeHazard.HasRelevantHazard
            || snapshot.Medical.Actionability.GrenadeThrowing
            || snapshot.Medical.Actionability.AnyMedicineUsing
            || snapshot.Medical.Actionability.Reloading)
        {
            return false;
        }

        bool combatContext = !string.IsNullOrWhiteSpace(resolution.ThreatId);
        if (combatContext && resolution.Weapon.ReadyRounds <= 0)
        {
            return false;
        }
        if (!combatContext && resolution.Weapon.TotalObservedRounds <= 0)
        {
            return false;
        }

        lock (Sync)
        {
            convergenceRequestsTotal++;
            NextConvergenceAttemptAtByBot[snapshot.BotProfileId] = now + ConvergenceRetry;
        }

        selector.UpdateWeaponsList();
        bool accepted = selector.TryChangeToSlot(
            resolution.Slot,
            changeToMain: resolution.Slot == selector.MainWeapon);
        if (!accepted)
        {
            lock (Sync) convergenceDeferredTotal++;
            VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
                $"DYNAMIC_WEAPON_PREFERENCE_CONVERGENCE_DEFERRED bot={Safe(snapshot.BotProfileId)}; from={selector.EquipmentSlot}; to={resolution.Slot}; mode={Safe(resolution.Mode)}; reason=native_selector_rejected; preferenceChanged={preferenceChanged}; targetAimFireMutation=false");
            return false;
        }

        lock (Sync) convergenceAcceptedTotal++;
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"DYNAMIC_WEAPON_PREFERENCE_CONVERGENCE_STARTED bot={Safe(snapshot.BotProfileId)}; from={selector.EquipmentSlot}; to={resolution.Slot}; mode={Safe(resolution.Mode)}; reason={Safe(resolution.Reason)}; threat={Safe(resolution.ThreatId)}; nativeSelector=true; nativeMainTopologyPreserved=true; targetAimFireMutation=false");
        return true;
    }

    private static VanguardLiveEquipmentCapabilitySnapshot CaptureEquipmentCached(BotOwner botOwner, DateTimeOffset now)
    {
        string botProfileId = botOwner.ProfileId?.Trim() ?? string.Empty;
        if (botProfileId.Length == 0)
        {
            return VanguardLiveEquipmentCapabilityReader.Capture(botOwner);
        }

        lock (Sync)
        {
            if (EquipmentCacheByBot.TryGetValue(botProfileId, out CachedEquipmentObservation? cached)
                && now - cached.ObservedAtUtc <= EquipmentCacheLifetime)
            {
                return cached.Snapshot;
            }
        }

        VanguardLiveEquipmentCapabilitySnapshot captured = VanguardLiveEquipmentCapabilityReader.Capture(botOwner);
        lock (Sync)
        {
            EquipmentCacheByBot[botProfileId] = new CachedEquipmentObservation(now, captured);
        }
        return captured;
    }

    private static void InvalidateEquipmentCache(string? botProfileId)
    {
        if (string.IsNullOrWhiteSpace(botProfileId)) return;
        lock (Sync) EquipmentCacheByBot.Remove(botProfileId.Trim());
    }

    private static ActiveWeaponPreference? FindPreference(string? botProfileId)
    {
        if (string.IsNullOrWhiteSpace(botProfileId)) return null;
        lock (Sync)
        {
            PreferenceByBot.TryGetValue(botProfileId.Trim(), out ActiveWeaponPreference? preference);
            return preference;
        }
    }

    private static bool Release(string? botProfileId, string reason)
    {
        if (string.IsNullOrWhiteSpace(botProfileId)) return false;
        ActiveWeaponPreference? removed;
        lock (Sync)
        {
            string key = botProfileId.Trim();
            if (!PreferenceByBot.TryGetValue(key, out removed)) return false;
            PreferenceByBot.Remove(key);
            EquipmentCacheByBot.Remove(key);
            NextConvergenceAttemptAtByBot.Remove(key);
            releasesTotal++;
        }
        VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
            $"DYNAMIC_WEAPON_PREFERENCE_RELEASED bot={Safe(removed!.BotProfileId)}; preferred={removed.PreferredSlot}; preferredItem={Safe(removed.PreferredWeaponItemId)}; mode={Safe(removed.Mode)}; reason={Safe(reason)}; nativeSelectorAuthorityRestored=true; targetAimFireMutation=false");
        return true;
    }

    private static bool TryGetExactWeapon(BotOwner botOwner, EquipmentSlot slot, string expectedItemId, out EFT.InventoryLogic.Weapon? weapon)
    {
        weapon = botOwner.GetPlayer?.Inventory?.Equipment?.GetSlot(slot)?.ContainedItem as EFT.InventoryLogic.Weapon;
        return weapon != null
            && !string.IsNullOrWhiteSpace(expectedItemId)
            && string.Equals(weapon.Id, expectedItemId, StringComparison.OrdinalIgnoreCase);
    }

    private static VanguardLiveWeaponCapabilityObservation? FindWeapon(VanguardLiveEquipmentCapabilitySnapshot equipment, EquipmentSlot slot)
        => equipment.Weapons.FirstOrDefault(item => string.Equals(item.Slot, slot.ToString(), StringComparison.OrdinalIgnoreCase));

    private static VanguardLiveWeaponCapabilityObservation? FindExactWeapon(
        VanguardLiveEquipmentCapabilitySnapshot equipment,
        EquipmentSlot slot,
        string itemId)
        => equipment.Weapons.FirstOrDefault(item =>
            string.Equals(item.Slot, slot.ToString(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ItemId, itemId, StringComparison.OrdinalIgnoreCase));

    private static bool IsOperationalWeapon(VanguardLiveWeaponCapabilityObservation? weapon)
        => weapon != null && !weapon.Bricked && weapon.TotalObservedRounds > 0;

    private static bool IsOperationalLongWeapon(VanguardLiveWeaponCapabilityObservation? weapon)
        => IsOperationalWeapon(weapon) && weapon!.IsLongWeapon;

    private static bool IsGeneralPurposeOperationalLongWeapon(VanguardLiveWeaponCapabilityObservation? weapon)
        => IsOperationalLongWeapon(weapon) && !IsLongRangeSpecialist(weapon);

    private static bool IsCloseCombatReady(VanguardLiveWeaponCapabilityObservation? weapon)
        => weapon != null && weapon.ReadyRounds > 0 && CloseCombatFamilyRank(weapon) > 0;

    private static bool IsLongRangeCombatReady(VanguardLiveWeaponCapabilityObservation? weapon)
        => weapon != null && weapon.ReadyRounds > 0 && IsLongRangeSpecialist(weapon);

    private static bool IsLongRangeSpecialist(VanguardLiveWeaponCapabilityObservation? weapon)
    {
        if (weapon == null) return false;
        string family = weapon.CanonicalWeaponFamily?.Trim().ToUpperInvariant() ?? "UNKNOWN";
        return string.Equals(family, "SNIPER_RIFLE", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(family, "MARKSMAN_RIFLE", StringComparison.OrdinalIgnoreCase)
                && weapon.MagnifiedSightCount > 0);
    }

    private static int CloseCombatFamilyRank(VanguardLiveWeaponCapabilityObservation weapon)
    {
        string family = weapon.CanonicalWeaponFamily?.Trim().ToUpperInvariant() ?? "UNKNOWN";
        return family switch
        {
            "SMG" => 6,
            "ASSAULT_RIFLE" => 5,
            "CARBINE" => 5,
            "SHOTGUN" => 5,
            "SIDEARM" => 4,
            // A carried machine gun is a bounded close fallback, not a blanket CQB upgrade.
            // It only joins the sidearm fallback tier when it is a live-ready long weapon;
            // the existing comfort/readiness ordering then decides which fallback is preferable.
            "MACHINE_GUN" when weapon.IsLongWeapon && weapon.ReadyRounds > 0 && !weapon.Bricked => 4,
            _ => 0,
        };
    }

    private static int LoadedMagazineSustainmentRank(VanguardLiveWeaponCapabilityObservation weapon)
    {
        if (weapon.CompatibleFullMagazineCount > 0) return 3;
        if (weapon.CompatibleMagazineCount > 0 && weapon.CompatibleMagazineAmmoCount > 0) return 2;
        if (weapon.CompatibleLooseAmmunitionCount > 0) return 1;
        return 0;
    }

    private static bool IsCurrentSlot(VanguardLiveWeaponCapabilityObservation weapon, EquipmentSlot currentSlot)
        => TryParseSlot(weapon.Slot, out EquipmentSlot slot) && slot == currentSlot;

    private static bool TryParseSlot(string? value, out EquipmentSlot slot)
        => Enum.TryParse(value, ignoreCase: true, out slot);

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt((dx * dx) + (dz * dz));
    }

    private static void EmitSummaryIfDue(DateTimeOffset now)
    {
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"DYNAMIC_WEAPON_PREFERENCE_BOOT enabled=true; nativeMainTopologyMutation=false; nativeMainRouting=true; convergence=native_selector_single_request; baseline=operational_long_weapon_over_spawn_fallback_sidearm; contextualSpecialists=SNIPER_RIFLE|MAGNIFIED_MARKSMAN_RIFLE; sustainmentPreference=bounded_tiebreak_only; sustainmentOrder=immediate_utility_then_switch_cost_then_loaded_magazine_depth_then_loose_ammo; lowMagazineForceSwap=false; closeDefaultMeters={VanguardTacticalCoordinationTuningPolicy.DefaultProactiveWeaponMeters:0}; dangerCloseDefaultMeters={VanguardTacticalCoordinationTuningPolicy.DefaultDangerCloseWeaponMeters:0}; farDefaultMeters={VanguardTacticalCoordinationTuningPolicy.DefaultOverwatchMinimumThreatMeters:0}; hysteresis=owner_scoped_close_to_far_band; threatSources=DecisionSnapshot|QualifiedAwarenessContact; targetMutation=false; aimMutation=false; fireCommand=false");
        }
        if (now < nextSummaryAtUtc) return;
        nextSummaryAtUtc = now + SummaryInterval;
        int active;
        lock (Sync) active = PreferenceByBot.Count;
        VanguardClientDiagnosticsLog.DiagnosticSummary(StatusTag, "DYNAMIC_WEAPON_PREFERENCE_SUMMARY", () =>
            $"DYNAMIC_WEAPON_PREFERENCE_SUMMARY active={active}; refreshesTotal={refreshesTotal}; changesTotal={changesTotal}; baselineLongPromotionsTotal={baselineLongPromotionsTotal}; closeContextPreferencesTotal={closeContextPreferencesTotal}; longContextPreferencesTotal={longContextPreferencesTotal}; hysteresisKeepsTotal={hysteresisKeepsTotal}; decisionThreatContextsTotal={decisionThreatContextsTotal}; awarenessThreatContextsTotal={awarenessThreatContextsTotal}; convergenceRequestsTotal={convergenceRequestsTotal}; convergenceAcceptedTotal={convergenceAcceptedTotal}; convergenceDeferredTotal={convergenceDeferredTotal}; nativeMainRoutesTotal={nativeMainRoutesTotal}; nativeMainRedundantSuppressionsTotal={nativeMainRedundantSuppressionsTotal}; releasesTotal={releasesTotal}; nativeTopologyMutation=false; targetAimFireMutation=false");
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');

    private readonly struct WeaponThreatContext
    {
        public static WeaponThreatContext Empty { get; } = new(string.Empty, float.MaxValue, "none", false);
        public WeaponThreatContext(string threatId, float distanceMeters, string source, bool fromDecisionSnapshot)
        {
            ThreatId = threatId ?? string.Empty;
            DistanceMeters = distanceMeters;
            Source = source ?? "none";
            FromDecisionSnapshot = fromDecisionSnapshot;
        }
        public string ThreatId { get; }
        public float DistanceMeters { get; }
        public string Source { get; }
        public bool FromDecisionSnapshot { get; }
    }

    private readonly struct PreferenceResolution
    {
        public PreferenceResolution(
            VanguardLiveWeaponCapabilityObservation weapon,
            EquipmentSlot slot,
            string mode,
            string reason,
            string threatId,
            float threatDistanceMeters,
            string threatSource,
            EquipmentSlot? disfavoredWeaponSlot,
            string disfavoredWeaponItemId)
        {
            Weapon = weapon;
            Slot = slot;
            Mode = mode ?? "BASELINE_OPERATIONAL";
            Reason = reason ?? "none";
            ThreatId = threatId ?? string.Empty;
            ThreatDistanceMeters = threatDistanceMeters;
            ThreatSource = threatSource ?? "none";
            DisfavoredWeaponSlot = disfavoredWeaponSlot;
            DisfavoredWeaponItemId = disfavoredWeaponItemId ?? string.Empty;
        }
        public VanguardLiveWeaponCapabilityObservation Weapon { get; }
        public EquipmentSlot Slot { get; }
        public string Mode { get; }
        public string Reason { get; }
        public string ThreatId { get; }
        public float ThreatDistanceMeters { get; }
        public string ThreatSource { get; }
        public EquipmentSlot? DisfavoredWeaponSlot { get; }
        public string DisfavoredWeaponItemId { get; }
    }

    private sealed class ActiveWeaponPreference
    {
        public ActiveWeaponPreference(
            string botProfileId,
            string ownerProfileId,
            EquipmentSlot preferredSlot,
            string preferredWeaponItemId,
            string preferredFamily,
            string mode,
            string reason,
            string threatId,
            float threatDistanceMeters,
            string threatSource,
            EquipmentSlot? disfavoredWeaponSlot,
            string disfavoredWeaponItemId,
            int readyRounds,
            int totalObservedRounds,
            DateTimeOffset startedAtUtc,
            DateTimeOffset observedAtUtc)
        {
            BotProfileId = botProfileId;
            OwnerProfileId = ownerProfileId;
            PreferredSlot = preferredSlot;
            PreferredWeaponItemId = preferredWeaponItemId;
            PreferredFamily = preferredFamily;
            Mode = mode;
            Reason = reason;
            ThreatId = threatId;
            ThreatDistanceMeters = threatDistanceMeters;
            ThreatSource = threatSource;
            DisfavoredWeaponSlot = disfavoredWeaponSlot;
            DisfavoredWeaponItemId = disfavoredWeaponItemId;
            ReadyRounds = readyRounds;
            TotalObservedRounds = totalObservedRounds;
            StartedAtUtc = startedAtUtc;
            ObservedAtUtc = observedAtUtc;
        }
        public string BotProfileId { get; }
        public string OwnerProfileId { get; }
        public EquipmentSlot PreferredSlot { get; }
        public string PreferredWeaponItemId { get; }
        public string PreferredFamily { get; }
        public string Mode { get; }
        public string Reason { get; }
        public string ThreatId { get; }
        public float ThreatDistanceMeters { get; }
        public string ThreatSource { get; }
        public EquipmentSlot? DisfavoredWeaponSlot { get; }
        public string DisfavoredWeaponItemId { get; }
        public int ReadyRounds { get; }
        public int TotalObservedRounds { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public DateTimeOffset ObservedAtUtc { get; }
    }

    private sealed class CachedEquipmentObservation
    {
        public CachedEquipmentObservation(DateTimeOffset observedAtUtc, VanguardLiveEquipmentCapabilitySnapshot snapshot)
        {
            ObservedAtUtc = observedAtUtc;
            Snapshot = snapshot;
        }
        public DateTimeOffset ObservedAtUtc { get; }
        public VanguardLiveEquipmentCapabilitySnapshot Snapshot { get; }
    }
}
#endif
