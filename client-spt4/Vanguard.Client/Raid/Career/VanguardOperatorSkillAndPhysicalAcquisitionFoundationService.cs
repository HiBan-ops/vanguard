using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;

#if SPT_CLIENT
using EFT;
using EFT.InventoryLogic;
using UnityEngine;
#endif

// Responsibility: Captures the native EFT skill/mastery/physical progression earned by Operators so it can later be persisted without inventing progression.
// Flow: Operator runtime identities are matched to their live EFT skill sources, observed forward-only deltas are collected during the raid, and a compact snapshot is handed to end-of-raid persistence.
// Authority boundary: EFT owns how progression is earned; Vanguard only restores/captures the native acquisition surfaces needed for Operators and the server owns durable persistence.
// Invariant: Only observed forward progress may be persisted: no synthetic gain, no rollback of higher persisted values, and no double attribution across the Fika/Operator boundary.
namespace Vanguard.Client.Raid.Career;

/// <summary>
/// skill acquisition compatibility restores the narrow, skill-producing portions of Player.ConnectSkillManager
/// that FikaBot deliberately suppresses, but only for runtime-bound Vanguard AI Operators.
/// It does not reconnect Player.ConnectSkillManager, restart the EFT Pedometer, mutate
/// locomotion/stamina/weight, or impose any physical gameplay constraint.
///
/// The service also records universal skill/mastering acquisition events and read-only
/// physical characteristics so later OFFRAID bricks can reason from observed EFT truth.
/// </summary>
internal static class VanguardOperatorSkillAndPhysicalAcquisitionFoundationService
{
    public const string StatusTag = "VANGUARD_OPERATOR_SKILL_AND_PHYSICAL_ACQUISITION_FOUNDATION_STATUS";
    public const string AcquisitionStatusTag = "VANGUARD_NATIVE_SKILL_ACTION_ACQUISITION_STATUS";
    public const string TelemetryStatusTag = "VANGUARD_SKILL_AND_PHYSICAL_TELEMETRY_STATUS";
    public const string CompatibilityStatusTag = "VANGUARD_FIKA_SKILL_MANAGER_COMPATIBILITY_GUARD_STATUS";

#if SPT_CLIENT
    private const float ShadowPedometerSampleSeconds = 1f;
    private const float MaxHorizontalSampleDeltaMeters = 20f;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, OperatorSubscription> Subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly FieldInfo? PlayerFatigueField = typeof(Player).GetField("Fatigue", BindingFlags.Instance | BindingFlags.NonPublic);
    private static string activeRaidSessionId = string.Empty;
    private static DateTimeOffset nextRefreshUtc = DateTimeOffset.MinValue;

    public static void Tick()
    {
        string raidSessionId = Clean(VanguardRaidOperatorRuntimeRegistry.ActiveRaidSessionId);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (Sync)
        {
            if (!string.Equals(activeRaidSessionId, raidSessionId, StringComparison.OrdinalIgnoreCase))
            {
                DetachAllLocked("raid_session_changed");
                activeRaidSessionId = raidSessionId;
                nextRefreshUtc = DateTimeOffset.MinValue;
            }
        }

        if (string.IsNullOrWhiteSpace(raidSessionId))
        {
            return;
        }

        IReadOnlyList<VanguardRaidOperatorRuntimeRecord> runtimeOperators = VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators();
        var liveBotProfileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (VanguardRaidOperatorRuntimeRecord runtime in runtimeOperators)
        {
            string botProfileId = Clean(runtime.BotProfileId);
            Player? player = runtime.BotOwner?.GetPlayer;
            if (string.IsNullOrWhiteSpace(botProfileId) || player == null || !player.IsAI || player.Skills == null)
            {
                continue;
            }

            liveBotProfileIds.Add(botProfileId);
            EnsureSubscription(runtime, player);
        }

        OperatorSubscription[] snapshot;
        lock (Sync)
        {
            var stale = new List<string>();
            foreach (KeyValuePair<string, OperatorSubscription> pair in Subscriptions)
            {
                if (!liveBotProfileIds.Contains(pair.Key))
                {
                    stale.Add(pair.Key);
                }
            }

            foreach (string botProfileId in stale)
            {
                DetachLocked(botProfileId, "runtime_operator_no_longer_bound");
            }

            snapshot = new OperatorSubscription[Subscriptions.Count];
            Subscriptions.Values.CopyTo(snapshot, 0);
        }

        foreach (OperatorSubscription subscription in snapshot)
        {
            SampleShadowPedometer(subscription);
        }

        if (now < nextRefreshUtc)
        {
            return;
        }

        nextRefreshUtc = now.AddSeconds(2);
        foreach (OperatorSubscription subscription in snapshot)
        {
            ObservePhysicalSnapshot(subscription, now);
        }
    }

    private static void EnsureSubscription(VanguardRaidOperatorRuntimeRecord runtime, Player player)
    {
        string botProfileId = Clean(runtime.BotProfileId);
        lock (Sync)
        {
            if (Subscriptions.TryGetValue(botProfileId, out OperatorSubscription? existing))
            {
                if (ReferenceEquals(existing.Player, player))
                {
                    return;
                }

                DetachLocked(botProfileId, "player_instance_replaced");
            }

            bool acquisitionParityAllowed = IsSuppressedSkillManagerConnection(player, out string compatibilityReason);
            var subscription = new OperatorSubscription(runtime, player, acquisitionParityAllowed, compatibilityReason)
            {
                LastDistanceSamplePosition = player.Transform.position,
                LastMovementState = player.CurrentStateName,
                NextDistanceSampleTime = Time.time + ShadowPedometerSampleSeconds,
                LastPhysicalSnapshotUtc = DateTimeOffset.MinValue
            };

            AttachTelemetry(subscription);
            if (acquisitionParityAllowed)
            {
                AttachNativeSkillActionRoots(subscription);
            }

            Subscriptions[botProfileId] = subscription;

            VanguardClientDiagnosticsLog.Operational(
                CompatibilityStatusTag,
                () => $"VANGUARD_COMPATIBILITY_GUARD operator={Safe(runtime.OperatorId)}; botProfile={Safe(botProfileId)}; playerType={Safe(player.GetType().FullName)}; acquisitionParityAllowed={acquisitionParityAllowed.ToString().ToLowerInvariant()}; reason={Safe(compatibilityReason)}; policy=enable_only_when_ConnectSkillManager_is_trivial_noop; futureFikaDoubleAttributionGuard=true; reflectionFailClosed=true");

            VanguardClientDiagnosticsLog.Operational(
                StatusTag,
                () => $"VANGUARD_OPERATOR_ACQUISITION_BOUND operator={Safe(runtime.OperatorId)}; botProfile={Safe(botProfileId)}; raid={Safe(runtime.RaidSessionId)}; nativeRootsEnabled={acquisitionParityAllowed.ToString().ToLowerInvariant()}; roots=movement_damage_energy_hydration_search_ammo_magazine_uniqueLoot; skillTelemetry=all_experience_and_level_events; masteringTelemetry=all_experience_and_mastered_events; distanceSource=vanguard_shadow_pedometer_1s_horizontal; eftPedometerRestarted=false; baseConnectSkillManagerCalled=false; physicalGameplayEnforcement=false; staminaMutation=false; movementMutation=false; syntheticSkillCoefficient=false");
        }
    }

    private static bool IsSuppressedSkillManagerConnection(Player player, out string reason)
    {
        try
        {
            MethodInfo? method = player.GetType().GetMethod(nameof(Player.ConnectSkillManager), BindingFlags.Instance | BindingFlags.Public);
            if (method == null)
            {
                reason = "ConnectSkillManager_method_missing";
                return false;
            }

            if (method.DeclaringType == typeof(Player))
            {
                reason = "Player_native_implementation_visible_assume_already_connected";
                return false;
            }

            byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null || il.Length == 0)
            {
                reason = "override_il_unavailable";
                return false;
            }

            int meaningful = 0;
            bool hasRet = false;
            foreach (byte opcode in il)
            {
                if (opcode == 0x00) // nop
                {
                    continue;
                }

                if (opcode == 0x2A) // ret
                {
                    hasRet = true;
                    continue;
                }

                meaningful++;
            }

            if (meaningful == 0 && hasRet)
            {
                reason = "derived_ConnectSkillManager_trivial_noop_confirmed";
                return true;
            }

            reason = "derived_ConnectSkillManager_nontrivial_assume_external_or_native_acquisition";
            return false;
        }
        catch (Exception exception)
        {
            reason = "compatibility_probe_failed_" + exception.GetType().Name;
            return false;
        }
    }

    private static void AttachTelemetry(OperatorSubscription subscription)
    {
        SkillManager skills = subscription.Skills;
        subscription.SkillExperienceHandler = skill => ObserveSkillEvent(subscription, "experience", skill);
        subscription.SkillLevelHandler = skill => ObserveSkillEvent(subscription, "level", skill);
        subscription.MasteringExperienceHandler = mastery => ObserveMasteringEvent(subscription, "experience", mastery);
        subscription.WeaponMasteredHandler = mastery => ObserveMasteringEvent(subscription, "mastered", mastery);

        skills.OnSkillExperienceChanged += subscription.SkillExperienceHandler;
        skills.OnSkillLevelChanged += subscription.SkillLevelHandler;
        skills.OnMasteringExperienceChanged += subscription.MasteringExperienceHandler;
        skills.WeaponMastered += subscription.WeaponMasteredHandler;
    }

    private static void AttachNativeSkillActionRoots(OperatorSubscription subscription)
    {
        Player player = subscription.Player;

        subscription.MovementStateHandler = (previous, next) => ObserveMovementStateChanged(subscription, previous, next);
        subscription.DamageHandler = (bodyPart, damage, info) => ObserveDamageTaken(subscription, bodyPart, damage, info);
        subscription.EnergyHandler = diff => CompleteEnergyChanged(subscription, diff);
        subscription.HydrationHandler = diff => CompleteHydrationChanged(subscription, diff);
        subscription.ItemFoundHandler = item => CompleteItemFound(subscription, item);
        subscription.SearchCompletedHandler = () => CompleteNoArgAction(subscription, "SearchAction", () => subscription.Skills.SearchAction.Complete());
        subscription.AmmoLoadedHandler = count => CompleteCountAction(subscription, "RaidLoadedAmmoAction", count, () => subscription.Skills.RaidLoadedAmmoAction.Complete(count));
        subscription.AmmoUnloadedHandler = count => CompleteCountAction(subscription, "RaidUnloadedAmmoAction", count, () => subscription.Skills.RaidUnloadedAmmoAction.Complete(count));
        subscription.MagazineCheckHandler = () => CompleteNoArgAction(subscription, "MagazineCheckAction", () => subscription.Skills.MagazineCheckAction.Complete());
        subscription.UniqueLootHandler = () => CompleteNoArgAction(subscription, "UniqueLoot", () => subscription.Skills.UniqueLoot.Complete());

        player.MovementContext.OnStateChanged += subscription.MovementStateHandler;
        player.HealthController.ApplyDamageEvent += subscription.DamageHandler;
        player.HealthController.EnergyChangedEvent += subscription.EnergyHandler;
        player.HealthController.HydrationChangedEvent += subscription.HydrationHandler;
        player.SearchController.OnItemFound += subscription.ItemFoundHandler;
        player.SearchController.OnItemFullySearchedEvent += subscription.SearchCompletedHandler;
        player.InventoryController.OnAmmoLoaded += subscription.AmmoLoadedHandler;
        player.InventoryController.OnAmmoUnloaded += subscription.AmmoUnloadedHandler;
        player.InventoryController.OnMagazineCheck += subscription.MagazineCheckHandler;
        if (player.StatisticsManager != null)
        {
            player.StatisticsManager.OnUniqueLoot += subscription.UniqueLootHandler;
        }
    }

    private static void SampleShadowPedometer(OperatorSubscription subscription)
    {
        float now = Time.time;
        if (now < subscription.NextDistanceSampleTime)
        {
            return;
        }

        Vector3 current = subscription.Player.Transform.position;
        Vector3 previous = subscription.LastDistanceSamplePosition;
        subscription.LastDistanceSamplePosition = current;
        subscription.NextDistanceSampleTime = now + ShadowPedometerSampleSeconds;

        float dx = current.x - previous.x;
        float dz = current.z - previous.z;
        float horizontal = Mathf.Sqrt(dx * dx + dz * dz);
        if (!IsFinite(horizontal) || horizontal <= 0f)
        {
            return;
        }

        if (horizontal > MaxHorizontalSampleDeltaMeters)
        {
            subscription.RejectedTeleportDistance += horizontal;
            subscription.RejectedTeleportSamples++;
            if (subscription.RejectedTeleportSamples <= 3)
            {
                VanguardClientDiagnosticsLog.Warning(
                    AcquisitionStatusTag,
                    $"VANGUARD_DISTANCE_SAMPLE_REJECTED operator={Safe(subscription.Runtime.OperatorId)}; botProfile={Safe(subscription.Runtime.BotProfileId)}; state={subscription.LastMovementState}; horizontalDelta={horizontal.ToString("0.####", CultureInfo.InvariantCulture)}; threshold={MaxHorizontalSampleDeltaMeters.ToString("0.####", CultureInfo.InvariantCulture)}; sampleSeconds={ShadowPedometerSampleSeconds.ToString("0.####", CultureInfo.InvariantCulture)}; reason=teleport_or_discontinuity_guard; skillActionCompleted=false; gameplayUnaffected=true");
            }
            return;
        }

        subscription.DistanceTotals.TryGetValue(subscription.LastMovementState, out float total);
        subscription.DistanceTotals[subscription.LastMovementState] = total + horizontal;
    }

    private static float ReadDistanceFromMark(OperatorSubscription subscription, EPlayerState state)
    {
        subscription.DistanceTotals.TryGetValue(state, out float total);
        subscription.DistanceMarks.TryGetValue(state, out float mark);
        return Math.Max(0f, total - mark);
    }

    private static void MakeDistanceMark(OperatorSubscription subscription, EPlayerState state)
    {
        subscription.DistanceTotals.TryGetValue(state, out float total);
        subscription.DistanceMarks[state] = total;
    }

    private static void ObserveMovementStateChanged(OperatorSubscription subscription, EPlayerState previousState, EPlayerState nextState)
    {
        try
        {
            // Mirror the EFT Pedometer ownership rule without restarting Fika's stopped coroutine:
            // CurrentState changes immediately, while distance itself is sampled on a 1-second cadence.
            subscription.LastMovementState = nextState;
            if (IsDistanceSkillState(nextState))
            {
                MakeDistanceMark(subscription, nextState);
            }

            float distance = ReadDistanceFromMark(subscription, previousState);
            if (!subscription.Player.MovementContext.IsGrounded || distance <= 0f || !IsFinite(distance))
            {
                return;
            }

            switch (previousState)
            {
                case EPlayerState.ProneMove:
                    subscription.Skills.ProneAction.Complete(distance);
                    EmitNativeAction(subscription, "ProneAction", distance, null);
                    break;
                case EPlayerState.Run:
                {
                    SkillManager.GStruct279 movement = BuildMovementPayload(subscription.Player, includeNoise: true);
                    subscription.Skills.MovementAction.Complete(movement, distance);
                    EmitNativeAction(subscription, "MovementAction", distance, movement);
                    break;
                }
                case EPlayerState.Sprint:
                {
                    SkillManager.GStruct279 movement = BuildMovementPayload(subscription.Player, includeNoise: false);
                    subscription.Skills.SprintAction.Complete(movement, distance);
                    EmitNativeAction(subscription, "SprintAction", distance, movement);
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            EmitAcquisitionFailure(subscription, "MovementState", exception);
        }
    }

    private static SkillManager.GStruct279 BuildMovementPayload(Player player, bool includeNoise)
        => new()
        {
            Noise = includeNoise ? player.MovementContext.CovertNoiseLevel : 0f,
            Overweight = player.Physical?.Overweight ?? 0f,
            Fatigue = ReadNativeFatigueStrength(player)
        };

    private static float ReadNativeFatigueStrength(Player player)
    {
        try
        {
            object? effect = PlayerFatigueField?.GetValue(player);
            if (effect == null)
            {
                return 0f;
            }

            PropertyInfo? strength = effect.GetType().GetProperty("Strength", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object? value = strength?.GetValue(effect);
            return value is float result && IsFinite(result) ? result : 0f;
        }
        catch
        {
            return 0f;
        }
    }

    private static void ObserveDamageTaken(OperatorSubscription subscription, EBodyPart bodyPart, float damage, DamageInfoStruct info)
    {
        try
        {
            if (GClass3051.IsSelfInflicted(info.DamageType))
            {
                return;
            }

            subscription.Skills.DamageTakenAction.Complete(damage);
            EmitNativeAction(subscription, "DamageTakenAction", damage, null, "bodyPart=" + bodyPart);
        }
        catch (Exception exception)
        {
            EmitAcquisitionFailure(subscription, "DamageTakenAction", exception);
        }
    }

    private static void CompleteEnergyChanged(OperatorSubscription subscription, float diff)
        => CompleteScalarAction(subscription, "EnergyChanged", diff, () => subscription.Skills.EnergyChanged.Complete(diff, diff));

    private static void CompleteHydrationChanged(OperatorSubscription subscription, float diff)
        => CompleteScalarAction(subscription, "HydrationChanged", diff, () => subscription.Skills.HydrationChanged.Complete(diff, diff));

    private static void CompleteItemFound(OperatorSubscription subscription, Item item)
    {
        if (item == null || item is StackableItemItemClass)
        {
            return;
        }

        try
        {
            IItemOwner owner = GClass3113.GetOwner(item.Parent);
            bool onCorpse = owner?.RootItem is InventoryEquipment;
            subscription.Skills.FindAction.Complete(onCorpse);
            EmitNativeAction(subscription, "FindAction", 1f, null, $"onCorpse={onCorpse.ToString().ToLowerInvariant()}; itemTemplate={Safe(item.TemplateId)}");
        }
        catch (Exception exception)
        {
            EmitAcquisitionFailure(subscription, "FindAction", exception);
        }
    }

    private static void CompleteNoArgAction(OperatorSubscription subscription, string actionName, Action completion)
    {
        try
        {
            completion();
            EmitNativeAction(subscription, actionName, 1f, null);
        }
        catch (Exception exception)
        {
            EmitAcquisitionFailure(subscription, actionName, exception);
        }
    }

    private static void CompleteCountAction(OperatorSubscription subscription, string actionName, int count, Action completion)
    {
        try
        {
            completion();
            EmitNativeAction(subscription, actionName, count, null, "count=" + count.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception exception)
        {
            EmitAcquisitionFailure(subscription, actionName, exception);
        }
    }

    private static void CompleteScalarAction(OperatorSubscription subscription, string actionName, float value, Action completion)
    {
        try
        {
            completion();
            EmitNativeAction(subscription, actionName, value, null);
        }
        catch (Exception exception)
        {
            EmitAcquisitionFailure(subscription, actionName, exception);
        }
    }

    private static void IncrementRootAction(OperatorSubscription subscription, string actionName)
    {
        subscription.RootActionCounts.TryGetValue(actionName, out int count);
        subscription.RootActionCounts[actionName] = count + 1;
    }

    private static void EmitNativeAction(
        OperatorSubscription subscription,
        string actionName,
        float value,
        SkillManager.GStruct279? movement,
        string extra = "")
    {
        IncrementRootAction(subscription, actionName);
        int ordinal = subscription.RootActionCounts[actionName];
        if (ordinal > 3 && ordinal % 10 != 0)
        {
            return;
        }

        string movementFields = movement.HasValue
            ? string.Format(CultureInfo.InvariantCulture, "; overweight={0:0.####}; fatigue={1:0.####}; noise={2:0.####}", movement.Value.Overweight, movement.Value.Fatigue, movement.Value.Noise)
            : string.Empty;
        string extraFields = string.IsNullOrWhiteSpace(extra) ? string.Empty : "; " + extra;

        VanguardClientDiagnosticsLog.Operational(
            AcquisitionStatusTag,
            () => string.Format(
                CultureInfo.InvariantCulture,
                "VANGUARD_NATIVE_SKILL_ACTION operator={0}; botProfile={1}; raid={2}; action={3}; ordinal={4}; value={5:0.####}{6}{7}; source=EFT_native_action_root_restored; nativeCoefficient=true; directSkillCurrentMutation=false; gameplayEnforcement=false",
                Safe(subscription.Runtime.OperatorId), Safe(subscription.Runtime.BotProfileId), Safe(subscription.Runtime.RaidSessionId), actionName, ordinal, value, movementFields, extraFields));
    }

    private static void EmitAcquisitionFailure(OperatorSubscription subscription, string actionName, Exception exception)
    {
        subscription.AcquisitionFailureCount++;
        VanguardClientDiagnosticsLog.Warning(
            AcquisitionStatusTag,
            $"VANGUARD_NATIVE_SKILL_ACTION_FAILED operator={Safe(subscription.Runtime.OperatorId)}; botProfile={Safe(subscription.Runtime.BotProfileId)}; action={Safe(actionName)}; type={Safe(exception.GetType().Name)}; message={Safe(exception.Message)}; failOpen=true; gameplayUnaffected=true; directSkillCurrentMutation=false");
    }

    private static void ObserveSkillEvent(OperatorSubscription subscription, string eventKind, AbstractSkillClass skill)
    {
        if (skill == null)
        {
            return;
        }

        string key = skill.Id + ":" + eventKind;
        subscription.SkillEventCounts.TryGetValue(key, out int current);
        int ordinal = current + 1;
        subscription.SkillEventCounts[key] = ordinal;
        if (ordinal > 3 && ordinal % 10 != 0)
        {
            return;
        }

        VanguardClientDiagnosticsLog.Operational(
            TelemetryStatusTag,
            () => string.Format(
                CultureInfo.InvariantCulture,
                "VANGUARD_SKILL_EVENT operator={0}; botProfile={1}; raid={2}; kind={3}; skill={4}; ordinal={5}; current={6:0.####}; level={7}; pointsEarned={8:0.####}; effectiveness={9:0.####}; observerOnly=true; persistenceOwner=Vanguard_0_6_1_existing_skill_persistence",
                Safe(subscription.Runtime.OperatorId), Safe(subscription.Runtime.BotProfileId), Safe(subscription.Runtime.RaidSessionId), eventKind, skill.Id, ordinal, skill.Current, skill.Level, skill.PointsEarned, skill.Effectiveness));
    }

    private static void ObserveMasteringEvent(OperatorSubscription subscription, string eventKind, MasterSkillClass mastery)
    {
        if (mastery == null)
        {
            return;
        }

        string key = mastery.Id + ":" + eventKind;
        subscription.MasteringEventCounts.TryGetValue(key, out int current);
        int ordinal = current + 1;
        subscription.MasteringEventCounts[key] = ordinal;
        if (ordinal > 3 && ordinal % 10 != 0)
        {
            return;
        }

        VanguardClientDiagnosticsLog.Operational(
            TelemetryStatusTag,
            () => string.Format(
                CultureInfo.InvariantCulture,
                "VANGUARD_MASTERING_EVENT operator={0}; botProfile={1}; raid={2}; kind={3}; mastery={4}; ordinal={5}; current={6:0.####}; level={7}; levelProgress={8:0.####}; observerOnly=true; persistenceOwner=Vanguard_0_6_1_existing_skill_persistence",
                Safe(subscription.Runtime.OperatorId), Safe(subscription.Runtime.BotProfileId), Safe(subscription.Runtime.RaidSessionId), eventKind, mastery.Id, ordinal, mastery.Current, mastery.Level, mastery.LevelProgress));
    }

    private static void ObservePhysicalSnapshot(OperatorSubscription subscription, DateTimeOffset now)
    {
        Player player = subscription.Player;
        BasePhysicalClass? physical = player.Physical;
        if (physical == null)
        {
            return;
        }

        string state = player.CurrentStateName.ToString();
        float stamina = physical.Stamina?.Current ?? float.NaN;
        float staminaCapacity = physical.Stamina != null ? physical.Stamina.TotalCapacity.Value : float.NaN;
        bool staminaExhausted = physical.Stamina?.Exhausted ?? false;
        float hands = physical.HandsStamina?.Current ?? float.NaN;
        float handsCapacity = physical.HandsStamina != null ? physical.HandsStamina.TotalCapacity.Value : float.NaN;
        bool handsExhausted = physical.HandsStamina?.Exhausted ?? false;
        float overweight = physical.Overweight;
        float walkOverweight = physical.WalkOverweight;
        float walkSpeedLimit = physical.WalkSpeedLimit;
        bool canSprint = physical.CanSprint;
        bool sprinting = physical.Sprinting;
        bool encumberDisabled = physical.EncumberDisabled;

        bool stateChanged = !string.Equals(subscription.LastLoggedMovementState, state, StringComparison.Ordinal);
        bool staminaThresholdChanged = subscription.LastStaminaExhausted != staminaExhausted;
        bool handsThresholdChanged = subscription.LastHandsExhausted != handsExhausted;
        bool overweightChanged = !subscription.LastOverweight.HasValue || Math.Abs(subscription.LastOverweight.Value - overweight) >= 0.05f;
        bool periodic = now - subscription.LastPhysicalSnapshotUtc >= TimeSpan.FromSeconds(15);
        if (!stateChanged && !staminaThresholdChanged && !handsThresholdChanged && !overweightChanged && !periodic)
        {
            return;
        }

        subscription.LastPhysicalSnapshotUtc = now;
        subscription.LastLoggedMovementState = state;
        subscription.LastStaminaExhausted = staminaExhausted;
        subscription.LastHandsExhausted = handsExhausted;
        subscription.LastOverweight = overweight;
        subscription.PhysicalSnapshotCount++;

        VanguardClientDiagnosticsLog.Operational(
            TelemetryStatusTag,
            () => string.Format(
                CultureInfo.InvariantCulture,
                "VANGUARD_PHYSICAL_SNAPSHOT operator={0}; botProfile={1}; raid={2}; ordinal={3}; state={4}; stamina={5:0.####}; staminaCapacity={6:0.####}; staminaExhausted={7}; handsStamina={8:0.####}; handsCapacity={9:0.####}; handsExhausted={10}; overweight={11:0.####}; walkOverweight={12:0.####}; walkSpeedLimit={13:0.####}; canSprint={14}; sprinting={15}; encumberDisabled={16}; observerOnly=true; physicalGameplayEnforcement=false",
                Safe(subscription.Runtime.OperatorId), Safe(subscription.Runtime.BotProfileId), Safe(subscription.Runtime.RaidSessionId), subscription.PhysicalSnapshotCount, state, stamina, staminaCapacity, staminaExhausted.ToString().ToLowerInvariant(), hands, handsCapacity, handsExhausted.ToString().ToLowerInvariant(), overweight, walkOverweight, walkSpeedLimit, canSprint.ToString().ToLowerInvariant(), sprinting.ToString().ToLowerInvariant(), encumberDisabled.ToString().ToLowerInvariant()));
    }

    private static bool IsDistanceSkillState(EPlayerState state)
        => state == EPlayerState.Run || state == EPlayerState.Sprint || state == EPlayerState.ProneMove;

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static void DetachAllLocked(string reason)
    {
        foreach (OperatorSubscription subscription in Subscriptions.Values)
        {
            DetachSubscription(subscription, reason);
        }
        Subscriptions.Clear();
    }

    private static void DetachLocked(string botProfileId, string reason)
    {
        if (!Subscriptions.TryGetValue(botProfileId, out OperatorSubscription? subscription))
        {
            return;
        }
        DetachSubscription(subscription, reason);
        Subscriptions.Remove(botProfileId);
    }

    private static void DetachSubscription(OperatorSubscription subscription, string reason)
    {
        try
        {
            SkillManager skills = subscription.Skills;
            if (subscription.SkillExperienceHandler != null) skills.OnSkillExperienceChanged -= subscription.SkillExperienceHandler;
            if (subscription.SkillLevelHandler != null) skills.OnSkillLevelChanged -= subscription.SkillLevelHandler;
            if (subscription.MasteringExperienceHandler != null) skills.OnMasteringExperienceChanged -= subscription.MasteringExperienceHandler;
            if (subscription.WeaponMasteredHandler != null) skills.WeaponMastered -= subscription.WeaponMasteredHandler;

            if (subscription.AcquisitionParityAllowed)
            {
                Player player = subscription.Player;
                if (subscription.MovementStateHandler != null) player.MovementContext.OnStateChanged -= subscription.MovementStateHandler;
                if (subscription.DamageHandler != null) player.HealthController.ApplyDamageEvent -= subscription.DamageHandler;
                if (subscription.EnergyHandler != null) player.HealthController.EnergyChangedEvent -= subscription.EnergyHandler;
                if (subscription.HydrationHandler != null) player.HealthController.HydrationChangedEvent -= subscription.HydrationHandler;
                if (subscription.ItemFoundHandler != null) player.SearchController.OnItemFound -= subscription.ItemFoundHandler;
                if (subscription.SearchCompletedHandler != null) player.SearchController.OnItemFullySearchedEvent -= subscription.SearchCompletedHandler;
                if (subscription.AmmoLoadedHandler != null) player.InventoryController.OnAmmoLoaded -= subscription.AmmoLoadedHandler;
                if (subscription.AmmoUnloadedHandler != null) player.InventoryController.OnAmmoUnloaded -= subscription.AmmoUnloadedHandler;
                if (subscription.MagazineCheckHandler != null) player.InventoryController.OnMagazineCheck -= subscription.MagazineCheckHandler;
                if (subscription.UniqueLootHandler != null && player.StatisticsManager != null) player.StatisticsManager.OnUniqueLoot -= subscription.UniqueLootHandler;
            }

            string rootCounts = FormatCounts(subscription.RootActionCounts);
            VanguardClientDiagnosticsLog.Operational(
                StatusTag,
                () => $"VANGUARD_OPERATOR_ACQUISITION_UNBOUND operator={Safe(subscription.Runtime.OperatorId)}; botProfile={Safe(subscription.Runtime.BotProfileId)}; reason={Safe(reason)}; nativeRootsEnabled={subscription.AcquisitionParityAllowed.ToString().ToLowerInvariant()}; rootActions={Safe(rootCounts)}; skillEventKinds={subscription.SkillEventCounts.Count}; masteringEventKinds={subscription.MasteringEventCounts.Count}; physicalSnapshots={subscription.PhysicalSnapshotCount}; rejectedDistanceSamples={subscription.RejectedTeleportSamples}; acquisitionFailures={subscription.AcquisitionFailureCount}; physicalGameplayEnforcement=false");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"VANGUARD_OPERATOR_ACQUISITION_UNBIND_FAILED operator={Safe(subscription.Runtime.OperatorId)}; botProfile={Safe(subscription.Runtime.BotProfileId)}; type={Safe(exception.GetType().Name)}; message={Safe(exception.Message)}; gameplayUnaffected=true");
        }
    }

    private static string FormatCounts(Dictionary<string, int> counts)
    {
        if (counts.Count == 0)
        {
            return "none";
        }

        var parts = new List<string>(counts.Count);
        foreach (KeyValuePair<string, int> pair in counts)
        {
            parts.Add(pair.Key + ":" + pair.Value.ToString(CultureInfo.InvariantCulture));
        }
        parts.Sort(StringComparer.Ordinal);
        return string.Join(",", parts);
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
    private static string Safe(string? value) => Clean(value).Replace(';', '_').Replace('\r', ' ').Replace('\n', ' ');

    private sealed class OperatorSubscription
    {
        public OperatorSubscription(VanguardRaidOperatorRuntimeRecord runtime, Player player, bool acquisitionParityAllowed, string compatibilityReason)
        {
            Runtime = runtime;
            Player = player;
            Skills = player.Skills;
            AcquisitionParityAllowed = acquisitionParityAllowed;
            CompatibilityReason = compatibilityReason;
        }

        public VanguardRaidOperatorRuntimeRecord Runtime { get; }
        public Player Player { get; }
        public SkillManager Skills { get; }
        public bool AcquisitionParityAllowed { get; }
        public string CompatibilityReason { get; }
        public MovementContext.GDelegate72? MovementStateHandler { get; set; }
        public Action<EBodyPart, float, DamageInfoStruct>? DamageHandler { get; set; }
        public Action<float>? EnergyHandler { get; set; }
        public Action<float>? HydrationHandler { get; set; }
        public Action<Item>? ItemFoundHandler { get; set; }
        public Action? SearchCompletedHandler { get; set; }
        public Action<int>? AmmoLoadedHandler { get; set; }
        public Action<int>? AmmoUnloadedHandler { get; set; }
        public Action? MagazineCheckHandler { get; set; }
        public Action? UniqueLootHandler { get; set; }
        public Action<AbstractSkillClass>? SkillExperienceHandler { get; set; }
        public Action<AbstractSkillClass>? SkillLevelHandler { get; set; }
        public Action<MasterSkillClass>? MasteringExperienceHandler { get; set; }
        public Action<MasterSkillClass>? WeaponMasteredHandler { get; set; }
        public Vector3 LastDistanceSamplePosition { get; set; }
        public EPlayerState LastMovementState { get; set; }
        public float NextDistanceSampleTime { get; set; }
        public Dictionary<EPlayerState, float> DistanceTotals { get; } = new();
        public Dictionary<EPlayerState, float> DistanceMarks { get; } = new();
        public int RejectedTeleportSamples { get; set; }
        public float RejectedTeleportDistance { get; set; }
        public int AcquisitionFailureCount { get; set; }
        public Dictionary<string, int> RootActionCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> SkillEventCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> MasteringEventCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset LastPhysicalSnapshotUtc { get; set; }
        public string LastLoggedMovementState { get; set; } = string.Empty;
        public bool? LastStaminaExhausted { get; set; }
        public bool? LastHandsExhausted { get; set; }
        public float? LastOverweight { get; set; }
        public int PhysicalSnapshotCount { get; set; }
    }
#else
    public static void Tick() { }
#endif
}
