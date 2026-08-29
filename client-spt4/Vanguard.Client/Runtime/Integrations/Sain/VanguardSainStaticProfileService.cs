#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using EFT;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;

// Responsibility: Applies the stable SAIN profile characteristics that make each Vanguard Operator use the intended combat persona without transferring squad ownership to SAIN.
// Flow: When an Operator SAIN component becomes available, Vanguard resolves the Operator profile/archetype, maps only supported static SAIN settings, applies them idempotently and records the applied signature.
// Authority boundary: SAIN remains combat-execution authority; Vanguard owns Operator identity/profile intent and only configures the documented integration surface.
// Invariant: Profile application must be Operator-scoped, repeat-safe and conservative when a SAIN field/version is unavailable; ordinary bots are never rewritten.
namespace Vanguard.Client.Runtime.Integrations.Sain;

/// <summary>
/// Raid-scoped owner-authority service for the integration subsystem calibration baseline. Registration is driven
/// by successful Vanguard runtime binding, with a low-frequency registry recovery pass so late
/// bind and future bind paths cannot silently bypass the invariant.
/// </summary>
internal static class VanguardSainStaticProfileService
{
    private sealed class OperatorState
    {
        public string OperatorId = string.Empty;
        public string BotProfileId = string.Empty;
        public string OwnerProfileId = string.Empty;
        public string Role = string.Empty;
        public string Doctrine = string.Empty;
        public string Temperament = string.Empty;
        public string SainTuningPlan = string.Empty;
        public VanguardSainOperatorTuningProfile Tuning = VanguardSainOperatorTuningProfile.Fallback;
        public BotOwner? BotOwner;
        public object? IndividualSettingsInstance;
        public object? LoadedPresetInstance;
        public bool PresetReapplyPending;
        public object? SquadInstance;
        public object? SquadSettingsInstance;
        public int IndividualAttempts;
        public int SquadAttempts;
        public bool IndividualApplied;
        public bool IndividualFailureLogged;
        public bool IndividualRetryExhausted;
        public bool SquadApplied;
        public bool SquadFailureLogged;
        public bool SquadRetryExhausted;
        public DateTimeOffset NextIndividualAttemptAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset NextSquadAttemptAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset NextDriftAuditAtUtc = DateTimeOffset.MinValue;
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new();

        bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);

        int IEqualityComparer<object>.GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, OperatorState> StateByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<object, object> SquadSettingsBySquad = new(ReferenceComparer.Instance);
    private static DateTimeOffset nextRegistryRecoveryAtUtc = DateTimeOffset.MinValue;

    public static void RegisterBoundOperator(
        string? operatorId,
        string? botProfileId,
        string? ownerProfileId,
        BotOwner? botOwner,
        string source,
        VanguardRaidOperatorSnapshotDto? snapshot = null)
    {
        if (!VanguardFikaCompat.IsRaidAuthority || botOwner == null)
        {
            return;
        }

        string botProfile = Normalize(botProfileId, botOwner.ProfileId);
        if (string.IsNullOrWhiteSpace(botProfile))
        {
            return;
        }

        VanguardSainOperatorTuningProfile resolvedTuning;
        string resolvedRole;
        string resolvedDoctrine;
        string resolvedTemperament;
        string resolvedTuningPlan;
        lock (Sync)
        {
            if (!StateByBotProfileId.TryGetValue(botProfile, out OperatorState? state))
            {
                state = new OperatorState();
                StateByBotProfileId.Add(botProfile, state);
            }

            bool ownerInstanceChanged = state.BotOwner != null && !ReferenceEquals(state.BotOwner, botOwner);
            bool tuningChanged = false;
            state.OperatorId = Normalize(operatorId, state.OperatorId);
            state.BotProfileId = botProfile;
            state.OwnerProfileId = Normalize(ownerProfileId, state.OwnerProfileId);
            state.BotOwner = botOwner;
            if (snapshot != null)
            {
                state.Role = Normalize(snapshot.Role, state.Role);
                state.Doctrine = Normalize(snapshot.SainRuntime?.Doctrine, state.Doctrine);
                state.Temperament = Normalize(snapshot.SainRuntime?.Temperament, state.Temperament);
                state.SainTuningPlan = Normalize(snapshot.SainRuntime?.SainTuningPlan, state.SainTuningPlan);
                VanguardSainOperatorTuningProfile nextTuning = VanguardSainStaticProfilePolicy.ResolveTuning(snapshot);
                tuningChanged = !EqualityComparer<VanguardSainOperatorTuningProfile>.Default.Equals(state.Tuning, nextTuning);
                state.Tuning = nextTuning;
            }
            state.IndividualRetryExhausted = false;
            state.SquadRetryExhausted = false;
            state.NextIndividualAttemptAtUtc = DateTimeOffset.MinValue;
            if (ownerInstanceChanged || tuningChanged)
            {
                state.IndividualApplied = false;
                state.IndividualSettingsInstance = null;
            }
            if (ownerInstanceChanged)
            {
                state.LoadedPresetInstance = null;
                state.PresetReapplyPending = false;
                state.SquadApplied = false;
                state.SquadInstance = null;
                state.SquadSettingsInstance = null;
            }

            resolvedTuning = state.Tuning;
            resolvedRole = state.Role;
            resolvedDoctrine = state.Doctrine;
            resolvedTemperament = state.Temperament;
            resolvedTuningPlan = state.SainTuningPlan;
        }

        VanguardClientDiagnosticsLog.Diagnostic(
            VanguardSainStaticProfilePolicy.PersonaSpecialtyProjectionStatusTag,
            () => $"VANGUARD_SAIN_PERSONA_PROFILE_QUEUED operator={Safe(operatorId)}; botProfile={Safe(botProfile)}; owner={Safe(ownerProfileId)}; source={Safe(source)}; role={Safe(resolvedRole)}; basePersona={Safe(resolvedTuning.BasePersona)}; specialty={Safe(resolvedTuning.Specialty)}; doctrine={Safe(resolvedDoctrine)}; temperament={Safe(resolvedTemperament)}; sainTuningPlan={Safe(resolvedTuningPlan)}; tuning={Safe(resolvedTuning.TuningKey)}; authority=true; progressionProjection=false; dynamicDoctrine=false");
    }

    public static void Tick()
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        RecoverRegistryBindings(now);

        OperatorState[] states;
        lock (Sync)
        {
            states = StateByBotProfileId.Values.ToArray();
        }

        foreach (OperatorState state in states)
        {
            ProcessIndividual(state, now);
            if (state.IndividualApplied)
            {
                ProcessSquad(state, now);
            }
        }
    }

    public static bool TryEnforceRuntimeSearchTiming(object? sainInfo, string source, out string summary)
    {
        summary = "not_vanguard_operator";
        if (sainInfo == null
            || !VanguardSainOperatorProfileAdapter.TryResolveBotOwner(sainInfo, out BotOwner? botOwner)
            || botOwner == null
            || string.IsNullOrWhiteSpace(botOwner.ProfileId))
        {
            return false;
        }

        OperatorState? state;
        lock (Sync)
        {
            StateByBotProfileId.TryGetValue(botOwner.ProfileId, out state);
        }

        if (state == null
            || !state.IndividualApplied
            || state.BotOwner == null
            || !ReferenceEquals(state.BotOwner, botOwner))
        {
            return false;
        }

        if (!VanguardSainOperatorProfileAdapter.TryClampOperatorTimeBeforeSearch(
                sainInfo,
                out float calculatedTimeBeforeSearch,
                out float effectiveTimeBeforeSearch,
                out float holdGroundDelay,
                out float forgetEnemyTimeBefore,
                out float forgetEnemyTimeAfter,
                out bool clampApplied,
                out bool forgetTimingAdjusted))
        {
            summary = $"operator={Safe(state.OperatorId)}; botProfile={Safe(state.BotProfileId)}; owner={Safe(state.OwnerProfileId)}; source={Safe(source)}; reason=time_before_search_member_unavailable; failOpen=true; operatorsOnly=true";
            VanguardClientDiagnosticsLog.Warning(VanguardSainStaticProfilePolicy.RuntimeSearchTimingFailedTag, summary);
            return false;
        }

        summary = $"operator={Safe(state.OperatorId)}; botProfile={Safe(state.BotProfileId)}; owner={Safe(state.OwnerProfileId)}; source={Safe(source)}; tuning={Safe(state.Tuning.TuningKey)}; searchBaseTime={VanguardSainStaticProfilePolicy.OperatorSearchBaseTimeSeconds:0.00}; calculatedTimeBeforeSearch={calculatedTimeBeforeSearch:0.00}; effectiveTimeBeforeSearch={effectiveTimeBeforeSearch:0.00}; clampMin={VanguardSainStaticProfilePolicy.OperatorMinimumTimeBeforeSearchSeconds:0.00}; clampMax={VanguardSainStaticProfilePolicy.OperatorMaximumTimeBeforeSearchSeconds:0.00}; clampApplied={Bool(clampApplied)}; holdGroundDelay={Number(holdGroundDelay)}; forgetEnemyTimeBefore={Number(forgetEnemyTimeBefore)}; forgetEnemyTimeAfter={Number(forgetEnemyTimeAfter)}; forgetTimingAdjusted={Bool(forgetTimingAdjusted)}; personality=Normal; settingsInstance=independent; willChaseDistantGunshots=false; operatorsOnly=true; nonOperatorsChanged=false; tag={VanguardSainStaticProfilePolicy.OperatorSearchCadenceStatusTag}";
        return true;
    }

    public static void ResetForRaidLifecycle(string source)
    {
        int operatorCount;
        int squadCount;
        lock (Sync)
        {
            operatorCount = StateByBotProfileId.Count;
            squadCount = SquadSettingsBySquad.Count;
            StateByBotProfileId.Clear();
            SquadSettingsBySquad.Clear();
            nextRegistryRecoveryAtUtc = DateTimeOffset.MinValue;
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardSainStaticProfilePolicy.StatusTag,
            $"VANGUARD_SAIN_PERSONA_PROFILE_RESET source={Safe(source)}; operatorsCleared={operatorCount}; squadsCleared={squadCount}; offRaidPersonaProjection=true; specialtyProjection=true; progressionProjection=false; willChaseDistantGunshots=false");
    }

    private static void RecoverRegistryBindings(DateTimeOffset now)
    {
        if (now < nextRegistryRecoveryAtUtc)
        {
            return;
        }

        nextRegistryRecoveryAtUtc = now + VanguardSainStaticProfilePolicy.RegistryRecoveryInterval;
        foreach (VanguardRaidOperatorRuntimeRecord runtime in VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators())
        {
            if (runtime.BotOwner == null)
            {
                continue;
            }

            bool missing;
            lock (Sync)
            {
                missing = !StateByBotProfileId.ContainsKey(runtime.BotProfileId);
            }

            if (missing)
            {
                RegisterBoundOperator(runtime.OperatorId, runtime.BotProfileId, runtime.OwnerProfileId, runtime.BotOwner, "registry_recovery");
            }
        }
    }

    private static void ProcessIndividual(OperatorState state, DateTimeOffset now)
    {
        if (state.BotOwner == null || state.IndividualRetryExhausted)
        {
            return;
        }

        object? currentPresetInstance = VanguardSainOperatorProfileAdapter.GetCurrentLoadedPresetInstance();
        bool presetChanged = state.IndividualApplied
            && currentPresetInstance != null
            && state.LoadedPresetInstance != null
            && !ReferenceEquals(currentPresetInstance, state.LoadedPresetInstance);
        if (presetChanged)
        {
            state.IndividualApplied = false;
            state.IndividualSettingsInstance = null;
            state.PresetReapplyPending = true;
        }

        bool auditDue = state.IndividualApplied && now >= state.NextDriftAuditAtUtc;
        if (!auditDue && (state.IndividualApplied || now < state.NextIndividualAttemptAtUtc))
        {
            return;
        }

        VanguardSainProfileApplyResult result = VanguardSainOperatorProfileAdapter.ApplyIndividual(
            state.BotOwner,
            state.IndividualSettingsInstance,
            state.Tuning);

        switch (result.Disposition)
        {
            case VanguardSainProfileApplyDisposition.Applied:
            {
                bool repaired = state.IndividualApplied || state.PresetReapplyPending;
                state.IndividualApplied = true;
                state.IndividualSettingsInstance = result.SettingsInstance;
                state.LoadedPresetInstance = currentPresetInstance;
                state.PresetReapplyPending = false;
                state.IndividualAttempts = 0;
                state.IndividualFailureLogged = false;
                state.IndividualRetryExhausted = false;
                state.NextDriftAuditAtUtc = now + VanguardSainStaticProfilePolicy.DriftAuditInterval;
                state.NextSquadAttemptAtUtc = DateTimeOffset.MinValue;

                string tag = repaired
                    ? VanguardSainStaticProfilePolicy.DriftRepairedTag
                    : VanguardSainStaticProfilePolicy.IndividualAppliedTag;
                VanguardClientDiagnosticsLog.Info(
                    tag,
                    $"operator={Safe(state.OperatorId)}; botProfile={Safe(state.BotProfileId)}; owner={Safe(state.OwnerProfileId)}; role={Safe(state.Role)}; basePersona={Safe(state.Tuning.BasePersona)}; specialty={Safe(state.Tuning.Specialty)}; doctrine={Safe(state.Doctrine)}; temperament={Safe(state.Temperament)}; sainTuningPlan={Safe(state.SainTuningPlan)}; tuning={Safe(state.Tuning.TuningKey)}; personality={result.Personality}; settingsInstance=independent; aggressionFactor={Number(state.Tuning.AggressionFactor)}; holdGroundFactor={Number(state.Tuning.HoldGroundFactor)}; suppressionResistanceDelta={Number(state.Tuning.SuppressionResistanceDelta)}; searchWaitFactor={Number(state.Tuning.SearchWaitFactor)}; sprintWhileSearchFactor={Number(state.Tuning.SprintWhileSearchFactor)}; moveToCoverHasEnemySpeedFactor={Number(state.Tuning.MoveToCoverHasEnemySpeedFactor)}; moveToCoverHasEnemyPoseFactor={Number(state.Tuning.MoveToCoverHasEnemyPoseFactor)}; willChaseDistantGunshots={result.WillChaseDistantGunshots}; searchBaseTime={result.SearchBaseTime}; effectiveTimeBeforeSearch={result.EffectiveTimeBeforeSearch}; holdGroundDelay={result.HoldGroundDelay}; searchTimingPolicy={result.SearchTimingPolicy}; sprintWhileSearchChance={result.SprintWhileSearchChance}; searchWaitMultiplier={result.SearchWaitMultiplier}; sneaky={result.Sneaky}; sneakySpeed={result.SneakySpeed}; sneakyPose={result.SneakyPose}; heardFromPeaceBehavior={result.HeardFromPeaceBehavior}; slowAtCorners={result.SlowAtCorners}; offRaidPersonaProjection=true; specialtyProjection=true; progressionProjection=false; dynamicDoctrine=false; rushAuthorityChanged=false; doorAuthorityChanged=false");
                break;
            }

            case VanguardSainProfileApplyDisposition.AlreadyCurrent:
                state.IndividualApplied = true;
                state.IndividualSettingsInstance ??= result.SettingsInstance;
                state.LoadedPresetInstance = currentPresetInstance;
                state.PresetReapplyPending = false;
                state.NextDriftAuditAtUtc = now + VanguardSainStaticProfilePolicy.DriftAuditInterval;
                break;

            case VanguardSainProfileApplyDisposition.NotReady:
            case VanguardSainProfileApplyDisposition.IntegrationUnavailable:
                ScheduleIndividualRetry(state, now, result.Reason);
                break;

            case VanguardSainProfileApplyDisposition.Failed:
                ScheduleIndividualRetry(state, now, result.Reason);
                break;
        }
    }

    private static void ScheduleIndividualRetry(OperatorState state, DateTimeOffset now, string reason)
    {
        state.IndividualApplied = false;
        state.IndividualSettingsInstance = null;
        state.IndividualAttempts++;
        state.NextIndividualAttemptAtUtc = now + VanguardSainStaticProfilePolicy.InitialRetryInterval;
        if (state.IndividualAttempts < VanguardSainStaticProfilePolicy.MaximumIndividualApplyAttempts || state.IndividualFailureLogged)
        {
            return;
        }

        state.IndividualFailureLogged = true;
        state.IndividualRetryExhausted = true;
        VanguardClientDiagnosticsLog.Warning(
            VanguardSainStaticProfilePolicy.ApplyFailedTag,
            $"scope=individual; operator={Safe(state.OperatorId)}; botProfile={Safe(state.BotProfileId)}; owner={Safe(state.OwnerProfileId)}; attempts={state.IndividualAttempts}; reason={Safe(reason)}; failOpen=true; spawnUnaffected=true");
    }

    private static void ProcessSquad(OperatorState state, DateTimeOffset now)
    {
        if (state.BotOwner == null || state.SquadRetryExhausted || now < state.NextSquadAttemptAtUtc)
        {
            return;
        }

        object? expectedSettings = null;
        if (state.SquadInstance != null)
        {
            lock (Sync)
            {
                SquadSettingsBySquad.TryGetValue(state.SquadInstance, out expectedSettings);
            }
        }

        VanguardSainSquadApplyResult result = VanguardSainOperatorProfileAdapter.ApplySquad(state.BotOwner, expectedSettings);
        state.SquadInstance = result.SquadInstance;

        switch (result.Disposition)
        {
            case VanguardSainProfileApplyDisposition.Applied:
                state.SquadApplied = true;
                state.SquadSettingsInstance = result.SettingsInstance;
                state.SquadAttempts = 0;
                state.SquadFailureLogged = false;
                state.SquadRetryExhausted = false;
                state.NextSquadAttemptAtUtc = now + VanguardSainStaticProfilePolicy.DriftAuditInterval;
                if (result.SquadInstance != null && result.SettingsInstance != null)
                {
                    lock (Sync)
                    {
                        SquadSettingsBySquad[result.SquadInstance] = result.SettingsInstance;
                    }
                }

                VanguardClientDiagnosticsLog.Info(
                    VanguardSainStaticProfilePolicy.SquadAppliedTag,
                    $"owner={Safe(state.OwnerProfileId)}; sainSquadGuid={Safe(result.SquadGuid)}; members={result.MemberCount}; ready={Bool(result.SquadReady)}; squadPersonality=None; vocalization={VanguardSainStaticProfilePolicy.SquadVocalizationLevel:0}; coordination={VanguardSainStaticProfilePolicy.SquadCoordinationLevel:0}; aggression={VanguardSainStaticProfilePolicy.SquadAggressionLevel:0}; aggressionRuntimeConsumption=none_in_current_sain; settingsInstance=independent");
                break;

            case VanguardSainProfileApplyDisposition.AlreadyCurrent:
                state.SquadApplied = true;
                state.SquadSettingsInstance ??= result.SettingsInstance;
                state.NextSquadAttemptAtUtc = now + VanguardSainStaticProfilePolicy.DriftAuditInterval;
                if (result.SquadInstance != null && result.SettingsInstance != null)
                {
                    lock (Sync)
                    {
                        SquadSettingsBySquad[result.SquadInstance] = result.SettingsInstance;
                    }
                }
                break;

            case VanguardSainProfileApplyDisposition.NotReady:
            case VanguardSainProfileApplyDisposition.IntegrationUnavailable:
            case VanguardSainProfileApplyDisposition.Failed:
                state.SquadApplied = false;
                state.SquadAttempts++;
                state.NextSquadAttemptAtUtc = now + VanguardSainStaticProfilePolicy.InitialRetryInterval;
                if (state.SquadAttempts >= VanguardSainStaticProfilePolicy.MaximumSquadApplyAttempts && !state.SquadFailureLogged)
                {
                    state.SquadFailureLogged = true;
                    state.SquadRetryExhausted = true;
                    VanguardClientDiagnosticsLog.Warning(
                        VanguardSainStaticProfilePolicy.ApplyFailedTag,
                        $"scope=squad; operator={Safe(state.OperatorId)}; botProfile={Safe(state.BotProfileId)}; owner={Safe(state.OwnerProfileId)}; attempts={state.SquadAttempts}; reason={Safe(result.Reason)}; members={result.MemberCount}; ready={Bool(result.SquadReady)}; failOpen=true; individualProfilePreserved=true");
                }
                break;
        }
    }

    private static string Normalize(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private static string Number(float value) => float.IsNaN(value) || float.IsInfinity(value)
        ? "unknown"
        : value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";
}
#endif
