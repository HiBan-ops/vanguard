using System;
using System.Collections.Generic;
using System.Globalization;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;

#if SPT_CLIENT
using EFT;
using EFT.InventoryLogic;
#endif

// Responsibility: Checks that Vanguard Operators remain connected to the native EFT skill/mastery acquisition callbacks that Fika bot setup can otherwise bypass.
// Flow: Live Operator skill sources and native action counters are observed, expected acquisition hooks are verified/restored only for Operators, and parity diagnostics record which surfaces actually produced progress.
// Authority boundary: EFT owns skill rules and earned values; Vanguard only restores the missing Operator hookup and the later persistence path stores observed forward progress.
// Invariant: No synthetic skill action is generated for the sake of parity, non-Operators are untouched, and restoration cannot double-credit a native action already reaching EFT normally.
namespace Vanguard.Client.Raid.Career;

/// <summary>
/// skill acquisition compatibility restores the EFT weapon skill/mastery acquisition root event only for
/// runtime-bound Vanguard AI Operators. Strength/Endurance remain observation-only:
/// the service subscribes to EFT SprintAction/MovementAction and records the exact
/// distance/overweight payloads before any physical-skill correction is considered.
/// </summary>
internal static class VanguardOperatorSkillAcquisitionParityService
{
    public const string WeaponAcquisitionStatusTag = "VANGUARD_OPERATOR_WEAPON_SKILL_AND_MASTERY_ACQUISITION_PARITY_STATUS";
    public const string PhysicalInstrumentationStatusTag = "VANGUARD_STRENGTH_ENDURANCE_INSTRUMENTATION_STATUS";

#if SPT_CLIENT
    private static readonly object Sync = new();
    private static readonly Dictionary<string, PhysicalActionSubscription> PhysicalSubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> WeaponDispatchCounts = new(StringComparer.OrdinalIgnoreCase);
    private static string activeInstrumentationRaidSessionId = string.Empty;
    private static DateTimeOffset nextSubscriptionRefreshUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// Harmony prefix contract: true = execute EFT original; false = the Vanguard AI
    /// Operator branch was handled here and the original !IsAI no-op must be skipped.
    /// Players and every non-Vanguard AI always continue through the untouched EFT method.
    /// </summary>
    public static bool ShouldRunOriginalExecuteShotSkill(Player player, Item weapon)
    {
        if (player == null || weapon == null || !player.IsAI)
        {
            return true;
        }

        string botProfileId = Clean(player.ProfileId);
        if (string.IsNullOrWhiteSpace(botProfileId)
            || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botProfileId, out VanguardRaidOperatorRuntimeRecord runtime))
        {
            return true;
        }

        // Native ExecuteShotSkill rejects throwable items before reaching WeaponShotAction.
        // For the registered AI branch the original method is otherwise a guaranteed no-op
        // because of !IsAI, so skipping it preserves the exact current EFT semantics.
        if (weapon is ThrowWeapItemClass)
        {
            return false;
        }

        try
        {
            SkillManager skills = player.Skills;
            if (skills == null)
            {
                return true;
            }

            Type weaponType = weapon.GetType();
            if (typeof(GClass3308).IsAssignableFrom(weaponType))
            {
                weaponType = typeof(GClass3308);
            }

            // Exact EFT ExecuteShotSkill coefficient. Do not substitute a Vanguard rate:
            // native WeaponShotAction subscribers own weapon-skill and Mastering progression.
            float masteringFactor = skills.WeaponBuffs.ContainsKey(weaponType)
                ? skills.WeaponBuffs[weaponType][EBuffId.WeaponDoubleMastering].Value
                : 1f;

            skills.WeaponShotAction.Complete(weapon, masteringFactor);
            EmitWeaponDispatch(runtime, weapon, weaponType, masteringFactor);
            return false;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                WeaponAcquisitionStatusTag,
                $"VANGUARD_WEAPON_ACQUISITION_FAILED operator={Safe(runtime.OperatorId)}; botProfile={Safe(botProfileId)}; weaponTemplate={Safe(weapon.TemplateId)}; type={Safe(exception.GetType().Name)}; message={Safe(exception.Message)}; originalSkipped=true; acquisitionFailClosed=true; gameplayUnaffected=true; directSkillMutation=false; directMasteringMutation=false");
            return false;
        }
    }

    /// <summary>
    /// Called after the canonical runtime bind. This method only manages diagnostic
    /// subscriptions; it never completes SprintAction/MovementAction and never writes
    /// Endurance/Strength values.
    /// </summary>
    public static void Tick()
    {
        string raidSessionId = Clean(VanguardRaidOperatorRuntimeRegistry.ActiveRaidSessionId);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (Sync)
        {
            if (!string.Equals(activeInstrumentationRaidSessionId, raidSessionId, StringComparison.OrdinalIgnoreCase))
            {
                DetachAllLocked("raid_session_changed");
                WeaponDispatchCounts.Clear();
                activeInstrumentationRaidSessionId = raidSessionId;
                nextSubscriptionRefreshUtc = DateTimeOffset.MinValue;
            }

            if (string.IsNullOrWhiteSpace(raidSessionId) || now < nextSubscriptionRefreshUtc)
            {
                return;
            }

            nextSubscriptionRefreshUtc = now.AddSeconds(1);
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
            EnsurePhysicalSubscription(runtime, player);
        }

        lock (Sync)
        {
            if (PhysicalSubscriptions.Count == 0)
            {
                return;
            }

            var stale = new List<string>();
            foreach (KeyValuePair<string, PhysicalActionSubscription> pair in PhysicalSubscriptions)
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
        }
    }

    private static void EnsurePhysicalSubscription(VanguardRaidOperatorRuntimeRecord runtime, Player player)
    {
        string botProfileId = Clean(runtime.BotProfileId);
        lock (Sync)
        {
            if (PhysicalSubscriptions.TryGetValue(botProfileId, out PhysicalActionSubscription? existing))
            {
                if (ReferenceEquals(existing.Player, player))
                {
                    return;
                }

                DetachLocked(botProfileId, "player_instance_replaced");
            }

            SkillManager skills = player.Skills;
            var subscription = new PhysicalActionSubscription(runtime, player, skills)
            {
                BaselineEnduranceCurrent = skills.Endurance?.Current ?? 0f,
                BaselineStrengthCurrent = skills.Strength?.Current ?? 0f
            };
            subscription.SprintHandler = (distance, movement) => ObservePhysicalAction(subscription, "SprintAction", distance, movement);
            subscription.MovementHandler = (distance, movement) => ObservePhysicalAction(subscription, "MovementAction", distance, movement);

            subscription.Skills.SprintAction.InnerEvent += subscription.SprintHandler;
            subscription.Skills.MovementAction.InnerEvent += subscription.MovementHandler;
            PhysicalSubscriptions[botProfileId] = subscription;

            VanguardClientDiagnosticsLog.Operational(
                PhysicalInstrumentationStatusTag,
                () => $"VANGUARD_PHYSICAL_INSTRUMENTATION_BOUND operator={Safe(runtime.OperatorId)}; botProfile={Safe(botProfileId)}; raid={Safe(runtime.RaidSessionId)}; sprintSource=Skills.SprintAction.InnerEvent; movementSource=Skills.MovementAction.InnerEvent; physicalOverweightSource=Player.Physical.Overweight; distanceSource=native_action_value; strengthMutation=false; enduranceMutation=false; syntheticProgress=false; sampling=first3_then_every10_plus_overweight_transition");
        }
    }

    private static void ObservePhysicalAction(
        PhysicalActionSubscription subscription,
        string actionName,
        float distance,
        SkillManager.GStruct279 movement)
    {
        Player player = subscription.Player;
        SkillManager skills = subscription.Skills;
        float physicalOverweight = player.Physical != null ? player.Physical.Overweight : float.NaN;
        bool overweight = movement.Overweight > 0f;
        int ordinal;
        bool shouldEmit;

        lock (Sync)
        {
            if (string.Equals(actionName, "SprintAction", StringComparison.Ordinal))
            {
                ordinal = ++subscription.SprintCount;
                shouldEmit = ShouldEmitSample(ordinal, subscription.LastSprintOverweight, overweight);
                subscription.LastSprintOverweight = overweight;
            }
            else
            {
                ordinal = ++subscription.MovementCount;
                shouldEmit = ShouldEmitSample(ordinal, subscription.LastMovementOverweight, overweight);
                subscription.LastMovementOverweight = overweight;
            }
        }

        if (!shouldEmit)
        {
            return;
        }

        float enduranceCurrent = skills.Endurance?.Current ?? 0f;
        float endurancePointsEarned = skills.Endurance?.PointsEarned ?? 0f;
        float strengthCurrent = skills.Strength?.Current ?? 0f;
        float strengthPointsEarned = skills.Strength?.PointsEarned ?? 0f;

        VanguardClientDiagnosticsLog.Operational(
            PhysicalInstrumentationStatusTag,
            () => string.Format(
                CultureInfo.InvariantCulture,
                "VANGUARD_PHYSICAL_ACTION operator={0}; botProfile={1}; raid={2}; action={3}; ordinal={4}; distance={5:0.####}; payloadOverweight={6:0.####}; physicalOverweight={7:0.####}; fatigue={8:0.####}; noise={9:0.####}; enduranceCurrent={10:0.####}; enduranceSinceBind={11:0.####}; enduranceSessionPoints={12:0.####}; strengthCurrent={13:0.####}; strengthSinceBind={14:0.####}; strengthSessionPoints={15:0.####}; strengthMutation=false; enduranceMutation=false; syntheticProgress=false",
                Safe(subscription.Runtime.OperatorId),
                Safe(subscription.Runtime.BotProfileId),
                Safe(subscription.Runtime.RaidSessionId),
                actionName,
                ordinal,
                distance,
                movement.Overweight,
                physicalOverweight,
                movement.Fatigue,
                movement.Noise,
                enduranceCurrent,
                enduranceCurrent - subscription.BaselineEnduranceCurrent,
                endurancePointsEarned,
                strengthCurrent,
                strengthCurrent - subscription.BaselineStrengthCurrent,
                strengthPointsEarned));
    }

    private static bool ShouldEmitSample(int ordinal, bool? previousOverweight, bool currentOverweight)
        => ordinal <= 3
           || ordinal % 10 == 0
           || !previousOverweight.HasValue
           || previousOverweight.Value != currentOverweight;

    private static void EmitWeaponDispatch(
        VanguardRaidOperatorRuntimeRecord runtime,
        Item weapon,
        Type normalizedWeaponType,
        float masteringFactor)
    {
        string botProfileId = Clean(runtime.BotProfileId);
        int ordinal;
        lock (Sync)
        {
            WeaponDispatchCounts.TryGetValue(botProfileId, out int current);
            ordinal = current + 1;
            WeaponDispatchCounts[botProfileId] = ordinal;
        }

        if (ordinal > 3 && ordinal % 10 != 0)
        {
            return;
        }

        VanguardClientDiagnosticsLog.Operational(
            WeaponAcquisitionStatusTag,
            () => string.Format(
                CultureInfo.InvariantCulture,
                "VANGUARD_WEAPON_SHOT_ACTION_DISPATCH operator={0}; botProfile={1}; raid={2}; ordinal={3}; weaponTemplate={4}; weaponRuntimeType={5}; normalizedWeaponType={6}; masteringFactor={7:0.####}; source=Player.ExecuteShotSkill; triggerOwner=EFT_ManageAggressor; nativeWeaponShotAction=true; originalAiGuardBypassed=true; originalSkippedForHandledOperator=true; directSkillCurrentMutation=false; directMasteringMutation=false; syntheticProgress=false; playersUnchanged=true; ordinaryBotsUnchanged=true; sampling=first3_then_every10",
                Safe(runtime.OperatorId),
                Safe(botProfileId),
                Safe(runtime.RaidSessionId),
                ordinal,
                Safe(weapon.TemplateId),
                Safe(weapon.GetType().FullName),
                Safe(normalizedWeaponType.FullName),
                masteringFactor));
    }

    private static void DetachAllLocked(string reason)
    {
        foreach (PhysicalActionSubscription subscription in PhysicalSubscriptions.Values)
        {
            DetachSubscription(subscription, reason);
        }

        PhysicalSubscriptions.Clear();
    }

    private static void DetachLocked(string botProfileId, string reason)
    {
        if (!PhysicalSubscriptions.TryGetValue(botProfileId, out PhysicalActionSubscription? subscription))
        {
            return;
        }

        DetachSubscription(subscription, reason);
        PhysicalSubscriptions.Remove(botProfileId);
    }

    private static void DetachSubscription(PhysicalActionSubscription subscription, string reason)
    {
        try
        {
            if (subscription.SprintHandler != null)
            {
                subscription.Skills.SprintAction.InnerEvent -= subscription.SprintHandler;
            }

            if (subscription.MovementHandler != null)
            {
                subscription.Skills.MovementAction.InnerEvent -= subscription.MovementHandler;
            }

            VanguardClientDiagnosticsLog.Operational(
                PhysicalInstrumentationStatusTag,
                () => $"VANGUARD_PHYSICAL_INSTRUMENTATION_UNBOUND operator={Safe(subscription.Runtime.OperatorId)}; botProfile={Safe(subscription.Runtime.BotProfileId)}; reason={Safe(reason)}; sprintObserved={subscription.SprintCount}; movementObserved={subscription.MovementCount}; strengthMutation=false; enduranceMutation=false");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                PhysicalInstrumentationStatusTag,
                $"VANGUARD_PHYSICAL_INSTRUMENTATION_UNBIND_FAILED operator={Safe(subscription.Runtime.OperatorId)}; botProfile={Safe(subscription.Runtime.BotProfileId)}; reason={Safe(reason)}; type={Safe(exception.GetType().Name)}; message={Safe(exception.Message)}; gameplayUnaffected=true");
        }
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private static string Safe(string? value)
        => Clean(value).Replace(';', '_').Replace('\r', ' ').Replace('\n', ' ');

    private sealed class PhysicalActionSubscription
    {
        public PhysicalActionSubscription(VanguardRaidOperatorRuntimeRecord runtime, Player player, SkillManager skills)
        {
            Runtime = runtime;
            Player = player;
            Skills = skills;
        }

        public VanguardRaidOperatorRuntimeRecord Runtime { get; }
        public Player Player { get; }
        public SkillManager Skills { get; }
        public Action<float, SkillManager.GStruct279>? SprintHandler { get; set; }
        public Action<float, SkillManager.GStruct279>? MovementHandler { get; set; }
        public float BaselineEnduranceCurrent { get; set; }
        public float BaselineStrengthCurrent { get; set; }
        public int SprintCount { get; set; }
        public int MovementCount { get; set; }
        public bool? LastSprintOverweight { get; set; }
        public bool? LastMovementOverweight { get; set; }
    }
#else
    public static void Tick() { }
#endif
}
