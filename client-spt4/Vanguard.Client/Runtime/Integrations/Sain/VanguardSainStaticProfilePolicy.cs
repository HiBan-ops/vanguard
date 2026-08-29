#if SPT_CLIENT
using System;
using Vanguard.Client.Api.Dtos;

// Responsibility: Encodes the deterministic rules for Sain Static Profile Policy within the SAIN integration.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Runtime.Integrations.Sain;

/// <summary>
/// Technical SAIN envelope for Vanguard Operators. Every Operator still starts from an isolated
/// clone of SAIN's Normal personality; Vanguard then composes bounded persona and specialty deltas
/// from the persistent Operator snapshot. Progression and dynamic doctrine remain disconnected.
/// </summary>
internal static class VanguardSainStaticProfilePolicy
{
    public const string StatusTag = "VANGUARD_STATIC_SAIN_NORMAL_BASELINE_STATUS";
    public const string PersonaSpecialtyProjectionStatusTag = "VANGUARD_SAIN_PERSONA_SPECIALTY_TUNING_STATUS";
    public const string IndividualAppliedTag = "VANGUARD_SAIN_PERSONA_PROFILE_APPLIED";
    public const string SquadAppliedTag = "VANGUARD_SAIN_STATIC_SQUAD_APPLIED";
    public const string DriftRepairedTag = "VANGUARD_SAIN_PERSONA_PROFILE_DRIFT_REPAIRED";
    public const string ApplyFailedTag = "VANGUARD_SAIN_PERSONA_PROFILE_APPLY_FAILED";

    public const string PersonalityName = "Normal";
    public const string SquadPersonalityName = "None";
    public const bool WillChaseDistantGunshots = false;
    public const float OperatorSearchBaseTimeSeconds = 18f;
    public const float OperatorMinimumTimeBeforeSearchSeconds = 15f;
    public const float OperatorMaximumTimeBeforeSearchSeconds = 22f;

    public const string OperatorSearchCadenceStatusTag = "VANGUARD_OPERATOR_SEARCH_CADENCE_STATUS";
    public const string RuntimeSearchTimingBindTag = "VANGUARD_SAIN_OPERATOR_SEARCH_TIMING_PATCH_BIND_OK";
    public const string RuntimeSearchTimingAppliedTag = "VANGUARD_SAIN_OPERATOR_SEARCH_TIMING_RUNTIME_APPLIED";
    public const string RuntimeSearchTimingFailedTag = "VANGUARD_SAIN_OPERATOR_SEARCH_TIMING_RUNTIME_FAILED";

    public const float SquadVocalizationLevel = 4f;
    public const float SquadCoordinationLevel = 5f;
    public const float SquadAggressionLevel = 3f;

    public const int MaximumIndividualApplyAttempts = 80;
    public const int MaximumSquadApplyAttempts = 120;
    public static readonly TimeSpan InitialRetryInterval = TimeSpan.FromMilliseconds(250d);
    public static readonly TimeSpan DriftAuditInterval = TimeSpan.FromSeconds(5d);
    public static readonly TimeSpan RegistryRecoveryInterval = TimeSpan.FromSeconds(1d);

    public const bool OffRaidPersonaProjectionEnabled = true;
    public const bool SpecialtyProjectionEnabled = true;
    public const bool ProgressionProjectionEnabled = false;
    public const bool DynamicDoctrineEnabled = false;

    public static VanguardSainOperatorTuningProfile ResolveTuning(VanguardRaidOperatorSnapshotDto? snapshot)
    {
        if (snapshot?.SainRuntime == null)
        {
            return VanguardSainOperatorTuningProfile.Fallback;
        }

        string persona = NormalizeKey(snapshot.SainRuntime.BasePersona);
        string specialty = NormalizeKey(snapshot.Specialty);
        VanguardSainOperatorTuningProfile profile = persona switch
        {
            // All values are relative to the user's currently loaded SAIN Normal personality.
            // Factors stay inside a deliberately narrow envelope; only explicit behavioral toggles
            // may override the Normal baseline.
            "disciplined" => new(
                "disciplined", specialty, 1.00f, 1.05f, 0.04f,
                0.98f, 0.95f, null, 1.00f, 1.00f, null,
                1.02f, 0.98f),
            "recon" => new(
                "recon", specialty, 0.94f, 1.06f, 0.03f,
                1.08f, 0.90f, true, 0.92f, 0.92f, true,
                0.94f, 0.92f),
            "support" => new(
                "support", specialty, 0.97f, 1.10f, 0.08f,
                1.03f, 0.95f, null, 1.00f, 1.00f, true,
                0.98f, 0.95f),
            "veteran" => new(
                "veteran", specialty, 1.05f, 1.10f, 0.10f,
                0.95f, 0.98f, null, 1.00f, 1.00f, true,
                1.05f, 0.93f),
            "marksman" => new(
                "marksman", specialty, 0.92f, 1.12f, 0.06f,
                1.10f, 0.90f, true, 0.90f, 0.90f, true,
                0.92f, 0.90f),
            "aggressive" => new(
                "aggressive", specialty, 1.08f, 1.02f, 0.06f,
                0.92f, 1.10f, null, 1.00f, 1.00f, false,
                1.08f, 0.98f),
            "protector" => new(
                "protector", specialty, 0.95f, 1.08f, 0.07f,
                1.07f, 0.90f, null, 1.00f, 1.00f, true,
                0.94f, 0.92f),
            _ => VanguardSainOperatorTuningProfile.Fallback with
            {
                BasePersona = string.IsNullOrWhiteSpace(persona) ? "fallback" : persona,
                Specialty = specialty,
            },
        };

        profile = specialty switch
        {
            "pointman" => profile with
            {
                AggressionFactor = profile.AggressionFactor * 1.03f,
                SprintWhileSearchFactor = profile.SprintWhileSearchFactor * 1.03f,
                MoveToCoverHasEnemySpeedFactor = profile.MoveToCoverHasEnemySpeedFactor * 1.02f,
            },
            "observation" => profile with
            {
                SearchWaitFactor = profile.SearchWaitFactor * 1.03f,
                SprintWhileSearchFactor = profile.SprintWhileSearchFactor * 0.97f,
                SneakyOverride = true,
                SneakySpeedFactor = Math.Min(profile.SneakySpeedFactor, 0.90f),
                SneakyPoseFactor = Math.Min(profile.SneakyPoseFactor, 0.90f),
                SlowAtCornersOverride = true,
            },
            "sustainment" => profile with
            {
                HoldGroundFactor = profile.HoldGroundFactor * 1.03f,
                SuppressionResistanceDelta = profile.SuppressionResistanceDelta + 0.03f,
            },
            "survival" => profile with
            {
                HoldGroundFactor = profile.HoldGroundFactor * 1.03f,
                SuppressionResistanceDelta = profile.SuppressionResistanceDelta + 0.03f,
            },
            "precision_overwatch" => profile with
            {
                HoldGroundFactor = profile.HoldGroundFactor * 1.03f,
                SprintWhileSearchFactor = profile.SprintWhileSearchFactor * 0.95f,
                SneakyOverride = true,
                SneakySpeedFactor = Math.Min(profile.SneakySpeedFactor, 0.88f),
                SneakyPoseFactor = Math.Min(profile.SneakyPoseFactor, 0.88f),
                SlowAtCornersOverride = true,
            },
            "room_entry" => profile with
            {
                AggressionFactor = profile.AggressionFactor * 1.04f,
                SprintWhileSearchFactor = profile.SprintWhileSearchFactor * 1.04f,
                SlowAtCornersOverride = false,
                MoveToCoverHasEnemySpeedFactor = profile.MoveToCoverHasEnemySpeedFactor * 1.03f,
            },
            "tactical_recovery" => profile with
            {
                AggressionFactor = profile.AggressionFactor * 0.97f,
                HoldGroundFactor = profile.HoldGroundFactor * 1.03f,
                SuppressionResistanceDelta = profile.SuppressionResistanceDelta + 0.02f,
                MoveToCoverHasEnemyPoseFactor = profile.MoveToCoverHasEnemyPoseFactor * 0.97f,
            },
            _ => profile,
        };

        return profile.Clamp();
    }

    private static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
    }
}

internal sealed record VanguardSainOperatorTuningProfile(
    string BasePersona,
    string Specialty,
    float AggressionFactor,
    float HoldGroundFactor,
    float SuppressionResistanceDelta,
    float SearchWaitFactor,
    float SprintWhileSearchFactor,
    bool? SneakyOverride,
    float SneakySpeedFactor,
    float SneakyPoseFactor,
    bool? SlowAtCornersOverride,
    float MoveToCoverHasEnemySpeedFactor,
    float MoveToCoverHasEnemyPoseFactor)
{
    public static readonly VanguardSainOperatorTuningProfile Fallback = new(
        "fallback", string.Empty, 1.00f, 1.00f, 0.00f,
        1.00f, 1.00f, null, 1.00f, 1.00f, null,
        1.00f, 1.00f);

    public string TuningKey => $"persona.{SafeKey(BasePersona)}+specialty.{SafeKey(Specialty)}";

    public VanguardSainOperatorTuningProfile Clamp() => this with
    {
        AggressionFactor = Math.Clamp(AggressionFactor, 0.85f, 1.15f),
        HoldGroundFactor = Math.Clamp(HoldGroundFactor, 0.85f, 1.15f),
        SuppressionResistanceDelta = Math.Clamp(SuppressionResistanceDelta, 0.00f, 0.15f),
        SearchWaitFactor = Math.Clamp(SearchWaitFactor, 0.85f, 1.15f),
        SprintWhileSearchFactor = Math.Clamp(SprintWhileSearchFactor, 0.85f, 1.15f),
        SneakySpeedFactor = Math.Clamp(SneakySpeedFactor, 0.85f, 1.00f),
        SneakyPoseFactor = Math.Clamp(SneakyPoseFactor, 0.85f, 1.00f),
        MoveToCoverHasEnemySpeedFactor = Math.Clamp(MoveToCoverHasEnemySpeedFactor, 0.85f, 1.15f),
        MoveToCoverHasEnemyPoseFactor = Math.Clamp(MoveToCoverHasEnemyPoseFactor, 0.85f, 1.15f),
    };

    private static string SafeKey(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().ToLowerInvariant();
}
#endif
