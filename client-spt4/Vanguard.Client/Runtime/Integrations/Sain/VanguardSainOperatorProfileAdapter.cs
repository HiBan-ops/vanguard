#if SPT_CLIENT
using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using EFT;
using Newtonsoft.Json;
using Vanguard.Client.Runtime.Audit;

// Responsibility: Projects persistent Vanguard Operator persona/specialty data into an independent SAIN personality/settings instance for the spawned bot.
// Flow: At bind time it resolves the Operator tuning plan, clones compatible SAIN settings, applies bounded persona/specialty adjustments through cached reflection and verifies the resulting profile before exposing it to the bot.
// Authority boundary: Vanguard owns the static Operator persona projection; SAIN remains owner of its runtime combat systems and unsupported SAIN versions fail open to spawn rather than blocking the Operator.
// Invariant: No shared SAIN template is mutated globally, each Operator receives independent settings, and profile-apply failure cannot corrupt other bots.
namespace Vanguard.Client.Runtime.Integrations.Sain;

internal enum VanguardSainProfileApplyDisposition
{
    Applied,
    AlreadyCurrent,
    NotReady,
    IntegrationUnavailable,
    Failed,
}

internal readonly struct VanguardSainProfileApplyResult
{
    public VanguardSainProfileApplyResult(
        VanguardSainProfileApplyDisposition disposition,
        string reason,
        object? settingsInstance,
        string personality,
        string searchBaseTime,
        string effectiveTimeBeforeSearch,
        string holdGroundDelay,
        string searchTimingPolicy,
        string sprintWhileSearchChance,
        string searchWaitMultiplier,
        string sneaky,
        string sneakySpeed,
        string sneakyPose,
        string heardFromPeaceBehavior,
        string slowAtCorners,
        string willChaseDistantGunshots)
    {
        Disposition = disposition;
        Reason = reason;
        SettingsInstance = settingsInstance;
        Personality = personality;
        SearchBaseTime = searchBaseTime;
        EffectiveTimeBeforeSearch = effectiveTimeBeforeSearch;
        HoldGroundDelay = holdGroundDelay;
        SearchTimingPolicy = searchTimingPolicy;
        SprintWhileSearchChance = sprintWhileSearchChance;
        SearchWaitMultiplier = searchWaitMultiplier;
        Sneaky = sneaky;
        SneakySpeed = sneakySpeed;
        SneakyPose = sneakyPose;
        HeardFromPeaceBehavior = heardFromPeaceBehavior;
        SlowAtCorners = slowAtCorners;
        WillChaseDistantGunshots = willChaseDistantGunshots;
    }

    public VanguardSainProfileApplyDisposition Disposition { get; }
    public string Reason { get; }
    public object? SettingsInstance { get; }
    public string Personality { get; }
    public string SearchBaseTime { get; }
    public string EffectiveTimeBeforeSearch { get; }
    public string HoldGroundDelay { get; }
    public string SearchTimingPolicy { get; }
    public string SprintWhileSearchChance { get; }
    public string SearchWaitMultiplier { get; }
    public string Sneaky { get; }
    public string SneakySpeed { get; }
    public string SneakyPose { get; }
    public string HeardFromPeaceBehavior { get; }
    public string SlowAtCorners { get; }
    public string WillChaseDistantGunshots { get; }
}

internal readonly struct VanguardSainSquadApplyResult
{
    public VanguardSainSquadApplyResult(
        VanguardSainProfileApplyDisposition disposition,
        string reason,
        object? squadInstance,
        object? settingsInstance,
        string squadGuid,
        int memberCount,
        bool squadReady)
    {
        Disposition = disposition;
        Reason = reason;
        SquadInstance = squadInstance;
        SettingsInstance = settingsInstance;
        SquadGuid = squadGuid;
        MemberCount = memberCount;
        SquadReady = squadReady;
    }

    public VanguardSainProfileApplyDisposition Disposition { get; }
    public string Reason { get; }
    public object? SquadInstance { get; }
    public object? SettingsInstance { get; }
    public string SquadGuid { get; }
    public int MemberCount { get; }
    public bool SquadReady { get; }
}

/// <summary>
/// Narrow reflection adapter over SAIN. It never mutates SAIN global preset objects: the loaded
/// Normal personality is first selected through SAIN's public SetPersonality method, then cloned
/// and attached to the individual Operator before the mandatory distant-gunshot invariant and
/// derived timing values are applied.
/// </summary>
internal static class VanguardSainOperatorProfileAdapter
{
    private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    public static VanguardSainProfileApplyResult ApplyIndividual(BotOwner? botOwner, object? expectedSettingsInstance, VanguardSainOperatorTuningProfile tuning)
    {
        if (botOwner == null)
        {
            return Failure(VanguardSainProfileApplyDisposition.NotReady, "bot_owner_unavailable");
        }

        object? sainComponent = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(
            botOwner,
            "SAIN.Components.BotComponent");
        if (sainComponent == null)
        {
            return Failure(
                VanguardOperatorRuntimeAuditReflection.TypeExists("SAIN.Components.BotComponent")
                    ? VanguardSainProfileApplyDisposition.NotReady
                    : VanguardSainProfileApplyDisposition.IntegrationUnavailable,
                "sain_bot_component_unavailable");
        }

        object? info = GetMember(sainComponent, "Info");
        if (info == null)
        {
            return Failure(VanguardSainProfileApplyDisposition.NotReady, "sain_info_unavailable");
        }

        object? currentSettings = GetMember(info, "PersonalitySettingsClass");
        if (expectedSettingsInstance != null
            && ReferenceEquals(currentSettings, expectedSettingsInstance)
            && IsNormalPersonality(info)
            && IndividualSettingsMatch(info, currentSettings, tuning))
        {
            return Snapshot(VanguardSainProfileApplyDisposition.AlreadyCurrent, "already_current", info, currentSettings);
        }

        object? loadedPreset = GetLoadedPreset();
        object? difficulty = GetMember(info, "Difficulty");
        MethodInfo? setPersonality = info.GetType().GetMethods(InstanceFlags)
            .FirstOrDefault(method => method.Name == "SetPersonality" && method.GetParameters().Length == 1);
        MethodInfo? calcTimeBeforeSearch = info.GetType().GetMethod("CalcTimeBeforeSearch", InstanceFlags, null, Type.EmptyTypes, null);
        MethodInfo? calcHoldGroundDelay = info.GetType().GetMethod("CalcHoldGroundDelay", InstanceFlags, null, Type.EmptyTypes, null);
        MethodInfo? updateDifficulty = ResolveOneArgumentMethod(difficulty, "UpdateSettings", loadedPreset);
        if (loadedPreset == null
            || difficulty == null
            || setPersonality == null
            || calcTimeBeforeSearch == null
            || calcHoldGroundDelay == null
            || updateDifficulty == null)
        {
            return Failure(VanguardSainProfileApplyDisposition.NotReady, "sain_profile_dependencies_not_ready");
        }

        object? sharedNormalSettings = FindNormalSettings(loadedPreset);
        if (sharedNormalSettings == null)
        {
            return Failure(VanguardSainProfileApplyDisposition.Failed, "normal_settings_unavailable_in_loaded_preset");
        }

        object? independentSettings;
        try
        {
            independentSettings = CloneSettings(sharedNormalSettings);
        }
        catch (Exception exception)
        {
            return Failure(VanguardSainProfileApplyDisposition.Failed, $"normal_settings_clone_{exception.GetType().Name}:{Compact(exception.Message)}");
        }

        if (independentSettings == null || ReferenceEquals(sharedNormalSettings, independentSettings))
        {
            return Failure(VanguardSainProfileApplyDisposition.Failed, "normal_settings_clone_failed");
        }

        object? search = GetDeep(independentSettings, "Behavior", "Search");
        object? general = GetDeep(independentSettings, "Behavior", "General");
        object? cover = GetDeep(independentSettings, "Behavior", "Cover");
        object? rush = GetDeep(independentSettings, "Behavior", "Rush");
        object? personalityDifficulty = GetMember(independentSettings, "Difficulty");
        if (search == null || general == null || cover == null || rush == null || personalityDifficulty == null)
        {
            return Failure(VanguardSainProfileApplyDisposition.Failed, "persona_specialty_tuning_category_missing");
        }

        if (!TrySetMember(search, "WillChaseDistantGunshots", VanguardSainStaticProfilePolicy.WillChaseDistantGunshots)
            || !TrySetMember(search, "SearchBaseTime", VanguardSainStaticProfilePolicy.OperatorSearchBaseTimeSeconds)
            || !TryScaleMember(search, "SearchWaitMultiplier", tuning.SearchWaitFactor, 0.01f, 5f)
            || !TryScaleMember(search, "SprintWhileSearchChance", tuning.SprintWhileSearchFactor, 0f, 100f)
            || (tuning.SneakyOverride.HasValue && !TrySetMember(search, "Sneaky", tuning.SneakyOverride.Value))
            || !TryScaleMember(search, "SneakySpeed", tuning.SneakySpeedFactor, 0f, 1f)
            || !TryScaleMember(search, "SneakyPose", tuning.SneakyPoseFactor, 0f, 1f)
            || (tuning.SlowAtCornersOverride.HasValue && !TrySetMember(search, "SlowAtCorners", tuning.SlowAtCornersOverride.Value))
            || !TryScaleMember(personalityDifficulty, "AggressionCoef", tuning.AggressionFactor, 0.01f, 10f)
            || !TryScaleMember(general, "HoldGroundBaseTime", tuning.HoldGroundFactor, 0f, 3f)
            || !TryOffsetMember(general, "SuppressionResistance", tuning.SuppressionResistanceDelta, 0f, 1f)
            || !TrySetMember(general, "KickOpenAllDoors", false)
            || !TryScaleMember(cover, "MoveToCoverHasEnemySpeed", tuning.MoveToCoverHasEnemySpeedFactor, 0f, 1f)
            || !TryScaleMember(cover, "MoveToCoverHasEnemyPose", tuning.MoveToCoverHasEnemyPoseFactor, 0f, 1f)
            || !TrySetMember(rush, "CanRushEnemyReloadHeal", false)
            || !TrySetMember(rush, "CanJumpCorners", false)
            || !TrySetMember(rush, "CanBunnyHop", false))
        {
            return Failure(VanguardSainProfileApplyDisposition.Failed, "persona_specialty_tuning_member_missing");
        }

        object? originalPersonality = GetMember(info, "Personality");
        object? originalSettings = currentSettings;
        bool mutationStarted = false;
        try
        {
            Type personalityType = setPersonality.GetParameters()[0].ParameterType;
            object normalPersonality = Enum.Parse(personalityType, VanguardSainStaticProfilePolicy.PersonalityName, ignoreCase: false);
            setPersonality.Invoke(info, new[] { normalPersonality });
            mutationStarted = true;

            if (!TrySetMember(info, "PersonalitySettingsClass", independentSettings))
            {
                return RollbackFailure(
                    info,
                    difficulty,
                    loadedPreset,
                    originalPersonality,
                    originalSettings,
                    "individual_settings_assignment_failed");
            }

            updateDifficulty.Invoke(difficulty, new[] { loadedPreset });
            calcTimeBeforeSearch.Invoke(info, Array.Empty<object>());
            if (!TryClampOperatorTimeBeforeSearch(
                    info,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _))
            {
                return RollbackFailure(
                    info,
                    difficulty,
                    loadedPreset,
                    originalPersonality,
                    originalSettings,
                    "operator_time_before_search_clamp_failed");
            }
            calcHoldGroundDelay.Invoke(info, Array.Empty<object>());

            object? assignedSettings = GetMember(info, "PersonalitySettingsClass");
            if (!ReferenceEquals(assignedSettings, independentSettings)
                || !IsNormalPersonality(info)
                || !IndividualSettingsMatch(info, assignedSettings, tuning))
            {
                return RollbackFailure(
                    info,
                    difficulty,
                    loadedPreset,
                    originalPersonality,
                    originalSettings,
                    "post_apply_verification_failed");
            }

            return Snapshot(VanguardSainProfileApplyDisposition.Applied, "applied", info, independentSettings);
        }
        catch (TargetInvocationException exception)
        {
            Exception source = exception.InnerException ?? exception;
            return mutationStarted
                ? RollbackFailure(
                    info,
                    difficulty,
                    loadedPreset,
                    originalPersonality,
                    originalSettings,
                    $"reflection_target_{source.GetType().Name}:{Compact(source.Message)}")
                : Failure(
                    VanguardSainProfileApplyDisposition.Failed,
                    $"reflection_target_{source.GetType().Name}:{Compact(source.Message)}");
        }
        catch (Exception exception)
        {
            return mutationStarted
                ? RollbackFailure(
                    info,
                    difficulty,
                    loadedPreset,
                    originalPersonality,
                    originalSettings,
                    $"{exception.GetType().Name}:{Compact(exception.Message)}")
                : Failure(
                    VanguardSainProfileApplyDisposition.Failed,
                    $"{exception.GetType().Name}:{Compact(exception.Message)}");
        }
    }

    internal static object? GetCurrentLoadedPresetInstance() => GetLoadedPreset();

    public static VanguardSainSquadApplyResult ApplySquad(BotOwner? botOwner, object? expectedSquadSettingsInstance)
    {
        if (botOwner == null)
        {
            return new VanguardSainSquadApplyResult(VanguardSainProfileApplyDisposition.NotReady, "bot_owner_unavailable", null, null, "none", 0, false);
        }

        object? sainComponent = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "SAIN.Components.BotComponent");
        object? squadInfo = GetDeep(sainComponent, "Squad", "SquadInfo");
        if (squadInfo == null)
        {
            return new VanguardSainSquadApplyResult(VanguardSainProfileApplyDisposition.NotReady, "sain_squad_unavailable", null, null, "none", 0, false);
        }

        bool squadReady = ToBool(GetMember(squadInfo, "SquadReady"));
        int memberCount = GetCount(GetMember(squadInfo, "Members"));
        string squadGuid = Text(GetMember(squadInfo, "GUID", "Id"));
        if (memberCount <= 1)
        {
            return new VanguardSainSquadApplyResult(VanguardSainProfileApplyDisposition.AlreadyCurrent, "single_operator_squad_neutral", squadInfo, null, squadGuid, memberCount, squadReady);
        }

        if (!squadReady)
        {
            return new VanguardSainSquadApplyResult(VanguardSainProfileApplyDisposition.NotReady, "sain_squad_not_ready", squadInfo, null, squadGuid, memberCount, false);
        }

        object? currentSettings = GetMember(squadInfo, "SquadPersonalitySettings");
        if (currentSettings != null
            && SquadSettingsMatch(currentSettings)
            && (expectedSquadSettingsInstance == null || ReferenceEquals(currentSettings, expectedSquadSettingsInstance)))
        {
            return new VanguardSainSquadApplyResult(VanguardSainProfileApplyDisposition.AlreadyCurrent, "already_current", squadInfo, currentSettings, squadGuid, memberCount, true);
        }

        try
        {
            Type? settingsType = currentSettings?.GetType()
                ?? VanguardOperatorRuntimeAuditReflection.FindType("SAIN.BotController.Classes.SquadPersonalitySettings");
            if (settingsType == null)
            {
                return new VanguardSainSquadApplyResult(VanguardSainProfileApplyDisposition.Failed, "squad_settings_type_missing", squadInfo, null, squadGuid, memberCount, true);
            }

            object? independentSettings = Activator.CreateInstance(settingsType);
            if (independentSettings == null
                || !TrySetMember(independentSettings, "VocalizationLevel", VanguardSainStaticProfilePolicy.SquadVocalizationLevel)
                || !TrySetMember(independentSettings, "CoordinationLevel", VanguardSainStaticProfilePolicy.SquadCoordinationLevel)
                || !TrySetMember(independentSettings, "AggressionLevel", VanguardSainStaticProfilePolicy.SquadAggressionLevel))
            {
                return new VanguardSainSquadApplyResult(VanguardSainProfileApplyDisposition.Failed, "squad_settings_creation_failed", squadInfo, null, squadGuid, memberCount, true);
            }

            if (!TrySetMember(squadInfo, "SquadPersonalitySettings", independentSettings))
            {
                return new VanguardSainSquadApplyResult(VanguardSainProfileApplyDisposition.Failed, "squad_settings_assignment_failed", squadInfo, null, squadGuid, memberCount, true);
            }

            TrySetEnumMember(squadInfo, "SquadPersonality", VanguardSainStaticProfilePolicy.SquadPersonalityName);

            object? assigned = GetMember(squadInfo, "SquadPersonalitySettings");
            if (!ReferenceEquals(assigned, independentSettings) || !SquadSettingsMatch(assigned))
            {
                return new VanguardSainSquadApplyResult(VanguardSainProfileApplyDisposition.Failed, "squad_post_apply_verification_failed", squadInfo, null, squadGuid, memberCount, true);
            }

            return new VanguardSainSquadApplyResult(VanguardSainProfileApplyDisposition.Applied, "applied", squadInfo, independentSettings, squadGuid, memberCount, true);
        }
        catch (Exception exception)
        {
            return new VanguardSainSquadApplyResult(
                VanguardSainProfileApplyDisposition.Failed,
                $"{exception.GetType().Name}:{Compact(exception.Message)}",
                squadInfo,
                null,
                squadGuid,
                memberCount,
                true);
        }
    }

    private static VanguardSainProfileApplyResult Snapshot(
        VanguardSainProfileApplyDisposition disposition,
        string reason,
        object info,
        object settings)
    {
        object? search = GetDeep(settings, "Behavior", "Search");
        return new VanguardSainProfileApplyResult(
            disposition,
            reason,
            settings,
            Text(GetMember(info, "Personality")),
            Number(GetMember(search, "SearchBaseTime")),
            Number(GetMember(info, "TimeBeforeSearch")),
            Number(GetMember(info, "HoldGroundDelay")),
            $"base={VanguardSainStaticProfilePolicy.OperatorSearchBaseTimeSeconds:0.00};clamp={VanguardSainStaticProfilePolicy.OperatorMinimumTimeBeforeSearchSeconds:0.00}-{VanguardSainStaticProfilePolicy.OperatorMaximumTimeBeforeSearchSeconds:0.00}",
            Number(GetMember(search, "SprintWhileSearchChance")),
            Number(GetMember(search, "SearchWaitMultiplier")),
            Bool(GetMember(search, "Sneaky")),
            Number(GetMember(search, "SneakySpeed")),
            Number(GetMember(search, "SneakyPose")),
            Text(GetMember(search, "HeardFromPeaceBehavior")),
            Bool(GetMember(search, "SlowAtCorners")),
            Bool(GetMember(search, "WillChaseDistantGunshots")));
    }

    private static VanguardSainProfileApplyResult Failure(VanguardSainProfileApplyDisposition disposition, string reason) =>
        new(disposition, reason, null, "none", "none", "none", "none", "none", "none", "none", "none", "none", "none", "none", "none", "none");

    private static VanguardSainProfileApplyResult RollbackFailure(
        object info,
        object difficulty,
        object loadedPreset,
        object? originalPersonality,
        object? originalSettings,
        string reason)
    {
        bool rollbackSucceeded = false;
        try
        {
            bool settingsRestored = originalSettings != null
                && TrySetMember(info, "PersonalitySettingsClass", originalSettings);
            bool personalityRestored = originalPersonality != null
                && TrySetMember(info, "Personality", originalPersonality);
            MethodInfo? updateDifficulty = ResolveOneArgumentMethod(difficulty, "UpdateSettings", loadedPreset);
            bool difficultyRestored = updateDifficulty != null;
            if (updateDifficulty != null)
            {
                updateDifficulty.Invoke(difficulty, new[] { loadedPreset });
            }

            bool timingsRestored = TryInvokeNoArguments(info, "CalcTimeBeforeSearch")
                && TryInvokeNoArguments(info, "CalcHoldGroundDelay");
            rollbackSucceeded = settingsRestored && personalityRestored && difficultyRestored && timingsRestored;
        }
        catch
        {
            rollbackSucceeded = false;
        }

        string rollback = rollbackSucceeded ? "rollback_ok" : "rollback_incomplete";
        return Failure(VanguardSainProfileApplyDisposition.Failed, $"{reason}:{rollback}");
    }

    private static object? FindNormalSettings(object loadedPreset)
    {
        object? dictionary = GetDeep(loadedPreset, "PersonalityManager", "PersonalityDictionary");
        if (dictionary is not IDictionary entries)
        {
            return null;
        }

        foreach (DictionaryEntry entry in entries)
        {
            if (string.Equals(entry.Key?.ToString(), VanguardSainStaticProfilePolicy.PersonalityName, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        return null;
    }

    private static object? CloneSettings(object source)
    {
        string json = JsonConvert.SerializeObject(source, Formatting.None);
        return JsonConvert.DeserializeObject(json, source.GetType());
    }

    private static object? GetLoadedPreset()
    {
        Type? pluginType = VanguardOperatorRuntimeAuditReflection.FindType("SAIN.SAINPlugin");
        PropertyInfo? property = pluginType?.GetProperty("LoadedPreset", StaticFlags);
        return property?.GetValue(null);
    }

    private static bool IsNormalPersonality(object info) =>
        string.Equals(Text(GetMember(info, "Personality")), VanguardSainStaticProfilePolicy.PersonalityName, StringComparison.Ordinal);

    private static bool IsDistantGunshotChaseDisabled(object? settings)
    {
        object? search = GetDeep(settings, "Behavior", "Search");
        return GetMember(search, "WillChaseDistantGunshots") is bool value && !value;
    }

    private static bool IndividualSettingsMatch(object info, object? settings, VanguardSainOperatorTuningProfile tuning)
    {
        object? loadedPreset = GetLoadedPreset();
        object? normalSettings = loadedPreset == null ? null : FindNormalSettings(loadedPreset);
        object? search = GetDeep(settings, "Behavior", "Search");
        object? general = GetDeep(settings, "Behavior", "General");
        object? cover = GetDeep(settings, "Behavior", "Cover");
        object? rush = GetDeep(settings, "Behavior", "Rush");
        object? personalityDifficulty = GetMember(settings, "Difficulty");
        object? normalSearch = GetDeep(normalSettings, "Behavior", "Search");
        object? normalGeneral = GetDeep(normalSettings, "Behavior", "General");
        object? normalCover = GetDeep(normalSettings, "Behavior", "Cover");
        object? normalDifficulty = GetMember(normalSettings, "Difficulty");
        if (search == null || general == null || cover == null || rush == null || personalityDifficulty == null
            || normalSearch == null || normalGeneral == null || normalCover == null || normalDifficulty == null
            || !IsDistantGunshotChaseDisabled(settings)
            || !NearlyEqual(GetMember(search, "SearchBaseTime"), VanguardSainStaticProfilePolicy.OperatorSearchBaseTimeSeconds)
            || !NearlyEqualScaled(GetMember(search, "SearchWaitMultiplier"), GetMember(normalSearch, "SearchWaitMultiplier"), tuning.SearchWaitFactor, 0.01f, 5f)
            || !NearlyEqualScaled(GetMember(search, "SprintWhileSearchChance"), GetMember(normalSearch, "SprintWhileSearchChance"), tuning.SprintWhileSearchFactor, 0f, 100f)
            || !NearlyEqualScaled(GetMember(search, "SneakySpeed"), GetMember(normalSearch, "SneakySpeed"), tuning.SneakySpeedFactor, 0f, 1f)
            || !NearlyEqualScaled(GetMember(search, "SneakyPose"), GetMember(normalSearch, "SneakyPose"), tuning.SneakyPoseFactor, 0f, 1f)
            || !NearlyEqualScaled(GetMember(personalityDifficulty, "AggressionCoef"), GetMember(normalDifficulty, "AggressionCoef"), tuning.AggressionFactor, 0.01f, 10f)
            || !NearlyEqualScaled(GetMember(general, "HoldGroundBaseTime"), GetMember(normalGeneral, "HoldGroundBaseTime"), tuning.HoldGroundFactor, 0f, 3f)
            || !NearlyEqualOffset(GetMember(general, "SuppressionResistance"), GetMember(normalGeneral, "SuppressionResistance"), tuning.SuppressionResistanceDelta, 0f, 1f)
            || !NearlyEqualScaled(GetMember(cover, "MoveToCoverHasEnemySpeed"), GetMember(normalCover, "MoveToCoverHasEnemySpeed"), tuning.MoveToCoverHasEnemySpeedFactor, 0f, 1f)
            || !NearlyEqualScaled(GetMember(cover, "MoveToCoverHasEnemyPose"), GetMember(normalCover, "MoveToCoverHasEnemyPose"), tuning.MoveToCoverHasEnemyPoseFactor, 0f, 1f)
            || GetMember(general, "KickOpenAllDoors") is not bool kickOpenAllDoors || kickOpenAllDoors
            || GetMember(rush, "CanRushEnemyReloadHeal") is not bool canRush || canRush
            || GetMember(rush, "CanJumpCorners") is not bool canJumpCorners || canJumpCorners
            || GetMember(rush, "CanBunnyHop") is not bool canBunnyHop || canBunnyHop)
        {
            return false;
        }

        if (!TryReadBool(GetMember(normalSearch, "Sneaky"), out bool normalSneaky)
            || !TryReadBool(GetMember(search, "Sneaky"), out bool currentSneaky)
            || currentSneaky != (tuning.SneakyOverride ?? normalSneaky)
            || !TryReadBool(GetMember(normalSearch, "SlowAtCorners"), out bool normalSlowAtCorners)
            || !TryReadBool(GetMember(search, "SlowAtCorners"), out bool currentSlowAtCorners)
            || currentSlowAtCorners != (tuning.SlowAtCornersOverride ?? normalSlowAtCorners))
        {
            return false;
        }

        return TryReadSingle(GetMember(info, "TimeBeforeSearch"), out float timeBeforeSearch)
            && timeBeforeSearch >= VanguardSainStaticProfilePolicy.OperatorMinimumTimeBeforeSearchSeconds - 0.001f
            && timeBeforeSearch <= VanguardSainStaticProfilePolicy.OperatorMaximumTimeBeforeSearchSeconds + 0.001f;
    }

    internal static bool TryResolveBotOwner(object? info, out BotOwner? botOwner)
    {
        botOwner = GetMember(info, "BotOwner") as BotOwner
            ?? GetDeep(info, "Bot", "BotOwner") as BotOwner;
        return botOwner != null;
    }

    internal static bool TryClampOperatorTimeBeforeSearch(
        object info,
        out float calculatedTimeBeforeSearch,
        out float effectiveTimeBeforeSearch,
        out float holdGroundDelay,
        out float forgetEnemyTimeBefore,
        out float forgetEnemyTimeAfter,
        out bool clampApplied,
        out bool forgetTimingAdjusted)
    {
        calculatedTimeBeforeSearch = 0f;
        effectiveTimeBeforeSearch = 0f;
        holdGroundDelay = float.NaN;
        forgetEnemyTimeBefore = float.NaN;
        forgetEnemyTimeAfter = float.NaN;
        clampApplied = false;
        forgetTimingAdjusted = false;

        if (!TryReadSingle(GetMember(info, "TimeBeforeSearch"), out calculatedTimeBeforeSearch))
        {
            return false;
        }

        TryReadSingle(GetMember(info, "HoldGroundDelay"), out holdGroundDelay);
        bool forgetKnown = TryReadSingle(GetMember(info, "ForgetEnemyTime"), out forgetEnemyTimeBefore);
        effectiveTimeBeforeSearch = Math.Clamp(
            calculatedTimeBeforeSearch,
            VanguardSainStaticProfilePolicy.OperatorMinimumTimeBeforeSearchSeconds,
            VanguardSainStaticProfilePolicy.OperatorMaximumTimeBeforeSearchSeconds);
        clampApplied = Math.Abs(effectiveTimeBeforeSearch - calculatedTimeBeforeSearch) > 0.001f;
        if (!TrySetMember(info, "TimeBeforeSearch", effectiveTimeBeforeSearch))
        {
            return false;
        }

        if (!clampApplied || !forgetKnown)
        {
            forgetEnemyTimeAfter = forgetEnemyTimeBefore;
            return true;
        }

        float additionalForgetWindow = Math.Max(0f, forgetEnemyTimeBefore - calculatedTimeBeforeSearch);
        forgetEnemyTimeAfter = effectiveTimeBeforeSearch + additionalForgetWindow;
        bool infoForgetAdjusted = TrySetMember(info, "ForgetEnemyTime", forgetEnemyTimeAfter);
        object? mindSettings = GetDeep(info, "BotOwner", "Settings", "FileSettings", "Mind")
            ?? GetDeep(info, "Bot", "BotOwner", "Settings", "FileSettings", "Mind");
        bool nativeForgetAdjusted = mindSettings != null
            && TrySetMember(mindSettings, "TIME_TO_FORGOR_ABOUT_ENEMY_SEC", forgetEnemyTimeAfter);
        forgetTimingAdjusted = infoForgetAdjusted && nativeForgetAdjusted;
        return true;
    }

    private static bool TryScaleMember(object instance, string name, float factor, float minimum, float maximum)
    {
        if (!TryReadSingle(GetMember(instance, name), out float baseline))
        {
            return false;
        }

        return TrySetMember(instance, name, Math.Clamp(baseline * factor, minimum, maximum));
    }

    private static bool TryOffsetMember(object instance, string name, float delta, float minimum, float maximum)
    {
        if (!TryReadSingle(GetMember(instance, name), out float baseline))
        {
            return false;
        }

        return TrySetMember(instance, name, Math.Clamp(baseline + delta, minimum, maximum));
    }

    private static bool NearlyEqualScaled(object? currentValue, object? baselineValue, float factor, float minimum, float maximum)
    {
        return TryReadSingle(currentValue, out float current)
            && TryReadSingle(baselineValue, out float baseline)
            && Math.Abs(current - Math.Clamp(baseline * factor, minimum, maximum)) <= 0.001f;
    }

    private static bool NearlyEqualOffset(object? currentValue, object? baselineValue, float delta, float minimum, float maximum)
    {
        return TryReadSingle(currentValue, out float current)
            && TryReadSingle(baselineValue, out float baseline)
            && Math.Abs(current - Math.Clamp(baseline + delta, minimum, maximum)) <= 0.001f;
    }

    private static bool TryReadBool(object? value, out bool result)
    {
        if (value is bool flag)
        {
            result = flag;
            return true;
        }

        result = false;
        return false;
    }

    private static bool TryReadSingle(object? value, out float result)
    {
        try
        {
            result = Convert.ToSingle(value, CultureInfo.InvariantCulture);
            return !float.IsNaN(result) && !float.IsInfinity(result);
        }
        catch
        {
            result = 0f;
            return false;
        }
    }

    private static bool SquadSettingsMatch(object? settings)
    {
        if (settings == null)
        {
            return false;
        }

        return NearlyEqual(GetMember(settings, "VocalizationLevel"), VanguardSainStaticProfilePolicy.SquadVocalizationLevel)
            && NearlyEqual(GetMember(settings, "CoordinationLevel"), VanguardSainStaticProfilePolicy.SquadCoordinationLevel)
            && NearlyEqual(GetMember(settings, "AggressionLevel"), VanguardSainStaticProfilePolicy.SquadAggressionLevel);
    }

    private static bool NearlyEqual(object? value, float expected)
    {
        try
        {
            return Math.Abs(Convert.ToSingle(value, CultureInfo.InvariantCulture) - expected) <= 0.001f;
        }
        catch
        {
            return false;
        }
    }

    private static object? GetDeep(object? instance, params string[] path)
    {
        object? current = instance;
        foreach (string name in path)
        {
            current = GetMember(current, name);
            if (current == null)
            {
                return null;
            }
        }

        return current;
    }

    private static object? GetMember(object? instance, params string[] names)
    {
        if (instance == null)
        {
            return null;
        }

        Type type = instance.GetType();
        foreach (string name in names)
        {
            PropertyInfo? property = type.GetProperty(name, InstanceFlags);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(instance);
            }

            FieldInfo? field = type.GetField(name, InstanceFlags);
            if (field != null)
            {
                return field.GetValue(instance);
            }
        }

        return null;
    }

    private static bool TrySetMember(object instance, string name, object? value)
    {
        Type type = instance.GetType();
        PropertyInfo? property = type.GetProperty(name, InstanceFlags);
        MethodInfo? setter = property?.GetSetMethod(nonPublic: true);
        if (setter != null)
        {
            setter.Invoke(instance, new[] { ConvertFor(value, property!.PropertyType) });
            return true;
        }

        FieldInfo? field = type.GetField(name, InstanceFlags)
            ?? type.GetField($"<{name}>k__BackingField", InstanceFlags);
        if (field == null)
        {
            return false;
        }

        field.SetValue(instance, ConvertFor(value, field.FieldType));
        return true;
    }

    private static bool TrySetEnumMember(object instance, string name, string enumName)
    {
        Type type = instance.GetType();
        PropertyInfo? property = type.GetProperty(name, InstanceFlags);
        Type? enumType = property?.PropertyType;
        if (enumType == null)
        {
            FieldInfo? field = type.GetField(name, InstanceFlags) ?? type.GetField($"<{name}>k__BackingField", InstanceFlags);
            enumType = field?.FieldType;
        }

        return enumType?.IsEnum == true && TrySetMember(instance, name, Enum.Parse(enumType, enumName, ignoreCase: false));
    }

    private static object? ConvertFor(object? value, Type targetType)
    {
        if (value == null || targetType.IsInstanceOfType(value))
        {
            return value;
        }

        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    private static bool TryInvokeNoArguments(object instance, string methodName)
    {
        MethodInfo? method = instance.GetType().GetMethod(methodName, InstanceFlags, null, Type.EmptyTypes, null);
        if (method == null)
        {
            return false;
        }

        method.Invoke(instance, Array.Empty<object>());
        return true;
    }

    private static MethodInfo? ResolveOneArgumentMethod(object? instance, string methodName, object? argument)
    {
        if (instance == null || argument == null)
        {
            return null;
        }

        return instance.GetType().GetMethods(InstanceFlags)
            .FirstOrDefault(candidate =>
            {
                if (candidate.Name != methodName)
                {
                    return false;
                }

                ParameterInfo[] parameters = candidate.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(argument);
            });
    }

    private static bool ToBool(object? value) => value is bool flag && flag;

    private static int GetCount(object? value)
    {
        if (value is ICollection collection)
        {
            return collection.Count;
        }

        try
        {
            return Convert.ToInt32(GetMember(value, "Count", "Length"), CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static string Text(object? value) => value?.ToString() ?? "none";

    private static string Bool(object? value) => value is bool flag ? (flag ? "true" : "false") : "none";

    private static string Number(object? value)
    {
        try
        {
            return Convert.ToSingle(value, CultureInfo.InvariantCulture).ToString("0.00", CultureInfo.InvariantCulture);
        }
        catch
        {
            return "none";
        }
    }

    private static string Compact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        string compact = value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
        return compact.Length <= 160 ? compact : compact[..160];
    }
}
#endif
