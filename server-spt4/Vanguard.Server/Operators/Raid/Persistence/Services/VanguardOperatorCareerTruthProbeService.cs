using System.Globalization;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Raid.Persistence.Models;
using Vanguard.Server.Operators.Services;
using Vanguard.Server.Diagnostics;

// Responsibility: Reads Career-relevant facts already present in the transported EFT/Fika profile descriptor and exposes them for parity/diagnostics.
// Flow: The exact CompleteProfileDescriptor JSON is inspected, supported fields are normalized into a bounded probe result, and missing fields remain explicitly unknown.
// Authority boundary: This is read-only observation of transported profile truth; it never writes Career, Operator storage or EFT profile data.
// Invariant: Absence is not evidence: unsupported or missing fields must remain unavailable rather than being inferred from unrelated state.
namespace Vanguard.Server.Operators.Raid.Persistence.Services;

/// <summary>
/// Read-only Career truth probe over the exact CompleteProfileDescriptor JSON already transported by raid persistence.
/// It never writes Career state, never gates the persistence transaction and never synthesizes missing EFT/Fika truth.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorCareerTruthProbeService(
    VanguardEftExperienceCurveService experienceCurve,
    ISptLogger<VanguardOperatorCareerTruthProbeService> logger)
{
    public const string StatusTag = "VANGUARD_CAREER_TRUTH_PROBE_STATUS";

    public VanguardOperatorCareerTruthProbe Probe(
        string? descriptorJson,
        VanguardOperatorProfile persistentOperator,
        bool diedRuntimeTruth,
        string raidSessionId,
        string operatorId,
        string? statisticsManagerType)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(descriptorJson))
            {
                return Failed(
                    "descriptor_json_missing",
                    persistentOperator,
                    diedRuntimeTruth,
                    raidSessionId,
                    operatorId,
                    statisticsManagerType);
            }

            using JsonDocument document = JsonDocument.Parse(descriptorJson);
            JsonElement root = document.RootElement;

            bool infoPresent = TryGetProperty(root, "Info", out JsonElement info) && info.ValueKind == JsonValueKind.Object;
            int descriptorExperience = infoPresent ? ReadInt32(info, "Experience") : 0;
            int descriptorReportedLevel = infoPresent ? ReadInt32(info, "Level") : 0;
            VanguardOperatorExperienceWindow experienceWindow = experienceCurve.ResolveWindow(descriptorExperience);
            int descriptorExperienceDelta = descriptorExperience - persistentOperator.Progression.Experience;
            bool experienceLevelCoherent = experienceWindow.IsAuthoritative
                && descriptorReportedLevel > 0
                && descriptorReportedLevel == experienceWindow.Level;

            bool statsEftPresent = TryGetPath(root, out JsonElement eftStats, "Stats", "Eft")
                && eftStats.ValueKind == JsonValueKind.Object;
            CounterProbe sessionCounters = statsEftPresent
                ? ReadCounters(eftStats, "SessionCounters")
                : CounterProbe.Absent;
            CounterProbe overallCounters = statsEftPresent
                ? ReadCounters(eftStats, "OverallCounters")
                : CounterProbe.Absent;

            int totalSessionExperience = statsEftPresent ? ReadInt32(eftStats, "TotalSessionExperience") : 0;
            string observedStatisticsManagerType = NormalizeStatisticsManagerType(statisticsManagerType);
            string nativeSessionExperienceAuthorityState = ResolveNativeSessionExperienceAuthorityState(
                observedStatisticsManagerType,
                totalSessionExperience,
                out bool nativeSessionExperienceAuthorityAvailable);
            IReadOnlyList<VanguardOperatorCareerTruthVictim> victims = statsEftPresent
                ? ReadVictims(eftStats)
                : Array.Empty<VanguardOperatorCareerTruthVictim>();
            string victimsState = !statsEftPresent
                ? "absent"
                : !TryGetProperty(eftStats, "Victims", out JsonElement victimsElement) || victimsElement.ValueKind == JsonValueKind.Null
                    ? "absent"
                    : victimsElement.ValueKind != JsonValueKind.Array
                        ? "present_invalid_shape"
                        : victims.Count == 0 ? "present_empty" : "present_populated";

            JsonElement deathCause = default;
            bool deathCausePresent = statsEftPresent
                && TryGetProperty(eftStats, "DeathCause", out deathCause)
                && deathCause.ValueKind == JsonValueKind.Object;
            string deathCauseDamageType = deathCausePresent ? ReadScalar(deathCause, "DamageType") : "none";
            string deathCauseSide = deathCausePresent ? ReadScalar(deathCause, "Side") : "none";
            string deathCauseRole = deathCausePresent ? ReadScalar(deathCause, "Role") : "none";
            string deathCauseWeaponId = deathCausePresent ? ReadScalar(deathCause, "WeaponId") : "none";

            JsonElement aggressor = default;
            bool aggressorPresent = statsEftPresent
                && TryGetProperty(eftStats, "Aggressor", out aggressor)
                && aggressor.ValueKind == JsonValueKind.Object;
            string aggressorProfileId = aggressorPresent ? ReadScalar(aggressor, "ProfileId") : "none";
            string aggressorAccountId = aggressorPresent ? ReadScalar(aggressor, "AccountId") : "none";
            string aggressorName = aggressorPresent ? ReadScalar(aggressor, "Name") : "none";
            string aggressorSide = aggressorPresent ? ReadScalar(aggressor, "Side") : "none";
            string aggressorRole = aggressorPresent ? ReadScalar(aggressor, "Role") : "none";

            bool directExitStatusPresent = TryFindDirectExitStatus(root, out JsonElement exitStatusElement);
            string directExitStatusValue = directExitStatusPresent ? Scalar(exitStatusElement) : "none";
            string exitStatusState = directExitStatusPresent
                ? "unexpected_direct_descriptor_field_present"
                : "not_directly_exposed_by_complete_profile_descriptor";
            string raidOutcomeState = diedRuntimeTruth
                ? "kia_from_runtime_truth"
                : directExitStatusPresent
                    ? "alive_at_raid_end_with_direct_exit_status_observed"
                    : "alive_at_raid_end_exit_status_unknown";

            SkillProbe skills = ReadSkills(root);
            var missingOrUnreliable = new List<string>();
            if (!infoPresent) missingOrUnreliable.Add("info_missing");
            if (!experienceWindow.IsAuthoritative) missingOrUnreliable.Add("xp_curve_non_authoritative");
            if (infoPresent && experienceWindow.IsAuthoritative && !experienceLevelCoherent) missingOrUnreliable.Add("descriptor_level_vs_curve_mismatch");
            missingOrUnreliable.Add("descriptor_info_experience_is_cumulative_generated_profile_xp_not_vanguard_career_authority");
            if (!nativeSessionExperienceAuthorityAvailable)
            {
                missingOrUnreliable.Add("native_session_experience_authority_" + nativeSessionExperienceAuthorityState);
            }
            if (!statsEftPresent) missingOrUnreliable.Add("stats_eft_missing");
            if (sessionCounters.State != "present_populated") missingOrUnreliable.Add("session_counters_" + sessionCounters.State);
            if (overallCounters.State != "absent") missingOrUnreliable.Add("overall_counters_are_cumulative_not_session_truth");
            if (totalSessionExperience == 0) missingOrUnreliable.Add("total_session_experience_zero_or_unpopulated");
            if (victims.Count == 0) missingOrUnreliable.Add("victims_empty_or_unpopulated");
            if (skills.SessionPointSkills.Count == 0) missingOrUnreliable.Add("skill_points_earned_during_session_empty_or_zero");
            if (!directExitStatusPresent) missingOrUnreliable.Add("exit_status_not_directly_exposed_by_complete_profile_descriptor");
            if (!diedRuntimeTruth && !directExitStatusPresent) missingOrUnreliable.Add("survived_runner_mia_not_derivable_from_alive_flag_alone");

            var probe = new VanguardOperatorCareerTruthProbe(
                "observed",
                true,
                "ok",
                persistentOperator.Progression.Level,
                persistentOperator.Progression.Experience,
                infoPresent,
                descriptorReportedLevel,
                descriptorExperience,
                descriptorExperienceDelta,
                experienceWindow.Level,
                experienceWindow.IsAuthoritative,
                experienceWindow.Source,
                experienceLevelCoherent,
                "cumulative_profile_experience_from_level_1",
                false,
                observedStatisticsManagerType,
                nativeSessionExperienceAuthorityState,
                nativeSessionExperienceAuthorityAvailable,
                statsEftPresent,
                sessionCounters.State,
                sessionCounters.ItemCount,
                sessionCounters.NonZeroCount,
                sessionCounters.Kills,
                sessionCounters.Deaths,
                sessionCounters.ExpKill,
                sessionCounters.ExpExitStatus,
                overallCounters.State,
                overallCounters.ItemCount,
                overallCounters.NonZeroCount,
                totalSessionExperience,
                victimsState,
                victims.Count,
                victims,
                deathCausePresent ? "present" : "absent_or_unpopulated",
                deathCauseDamageType,
                deathCauseSide,
                deathCauseRole,
                deathCauseWeaponId,
                aggressorPresent ? "present" : "absent_or_unpopulated",
                aggressorProfileId,
                aggressorAccountId,
                aggressorName,
                aggressorSide,
                aggressorRole,
                diedRuntimeTruth,
                "botowner_corpse_health_composite",
                exitStatusState,
                directExitStatusValue,
                raidOutcomeState,
                skills.State,
                skills.CommonCount,
                skills.SessionPointSkills.Count,
                skills.TotalSessionPoints,
                skills.SessionPointSkills,
                missingOrUnreliable);

            logger.Info(VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] gate=A_read_only; raid={Safe(raidSessionId)}; operator={Safe(operatorId)}; descriptorParsed=true; descriptorXp={descriptorExperience}; descriptorXpSemantics=cumulative_profile_experience_from_level_1; descriptorXpCareerAuthority=false; reportedLevel={descriptorReportedLevel}; curveResolvedLevel={experienceWindow.Level}; xpCurveAuthoritative={Bool(experienceWindow.IsAuthoritative)}; statisticsManager={Safe(observedStatisticsManagerType)}; nativeSessionXpAuthority={Safe(nativeSessionExperienceAuthorityState)}; nativeSessionXpAvailable={Bool(nativeSessionExperienceAuthorityAvailable)}; sessionCounters={sessionCounters.State}; sessionCounterItems={sessionCounters.ItemCount}; totalSessionExperience={totalSessionExperience}; victims={victims.Count}; died={Bool(diedRuntimeTruth)}; exitStatus={exitStatusState}; skillSessionPointEntries={skills.SessionPointSkills.Count}; careerMutation=false; persistenceSemanticsChanged=false; tag={StatusTag}"));
            return probe;
        }
        catch (Exception exception)
        {
            return Failed(
                "descriptor_probe_exception_" + exception.GetType().Name,
                persistentOperator,
                diedRuntimeTruth,
                raidSessionId,
                operatorId,
                statisticsManagerType);
        }
    }

    private VanguardOperatorCareerTruthProbe Failed(
        string reason,
        VanguardOperatorProfile persistentOperator,
        bool diedRuntimeTruth,
        string raidSessionId,
        string operatorId,
        string? statisticsManagerType)
    {
        string observedStatisticsManagerType = NormalizeStatisticsManagerType(statisticsManagerType);
        string nativeSessionExperienceAuthorityState = ResolveNativeSessionExperienceAuthorityState(
            observedStatisticsManagerType,
            0,
            out bool nativeSessionExperienceAuthorityAvailable);
        logger.Warning(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] gate=A_read_only; raid={Safe(raidSessionId)}; operator={Safe(operatorId)}; descriptorParsed=false; reason={Safe(reason)}; careerMutation=false; persistenceSemanticsChanged=false; action=diagnostic_only_do_not_gate_persistence; tag={StatusTag}"));
        return new VanguardOperatorCareerTruthProbe(
            "unavailable",
            false,
            reason,
            persistentOperator.Progression.Level,
            persistentOperator.Progression.Experience,
            false,
            0,
            0,
            0,
            0,
            false,
            "unavailable",
            false,
            "cumulative_profile_experience_from_level_1",
            false,
            observedStatisticsManagerType,
            nativeSessionExperienceAuthorityState,
            nativeSessionExperienceAuthorityAvailable,
            false,
            "absent",
            0,
            0,
            null,
            null,
            null,
            null,
            "absent",
            0,
            0,
            0,
            "absent",
            0,
            Array.Empty<VanguardOperatorCareerTruthVictim>(),
            "absent_or_unpopulated",
            "none",
            "none",
            "none",
            "none",
            "absent_or_unpopulated",
            "none",
            "none",
            "none",
            "none",
            "none",
            diedRuntimeTruth,
            "botowner_corpse_health_composite",
            "unavailable",
            "none",
            diedRuntimeTruth ? "kia_from_runtime_truth" : "alive_at_raid_end_exit_status_unknown",
            "absent",
            0,
            0,
            0.0,
            Array.Empty<VanguardOperatorCareerTruthSkill>(),
            new[]
            {
                reason,
                "career_truth_probe_unavailable_no_synthesis",
                "descriptor_info_experience_is_cumulative_generated_profile_xp_not_vanguard_career_authority",
                "native_session_experience_authority_" + nativeSessionExperienceAuthorityState
            });
    }

    private static CounterProbe ReadCounters(JsonElement eftStats, string propertyName)
    {
        if (!TryGetProperty(eftStats, propertyName, out JsonElement counters) || counters.ValueKind == JsonValueKind.Null)
        {
            return CounterProbe.Absent;
        }
        if (counters.ValueKind != JsonValueKind.Object
            || !TryGetProperty(counters, "Items", out JsonElement items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return new CounterProbe("present_invalid_shape", 0, 0, null, null, null, null);
        }

        int itemCount = 0;
        int nonZeroCount = 0;
        long? kills = null;
        long? deaths = null;
        long? expKill = null;
        long? expExitStatus = null;
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            itemCount++;
            long value = ReadInt64(item, "Value");
            if (value != 0) nonZeroCount++;
            IReadOnlyList<string> keys = ReadStringArray(item, "Key");
            if (MatchesKeySet(keys, "Kills")) kills = AddNullable(kills, value);
            if (MatchesKeySet(keys, "Deaths")) deaths = AddNullable(deaths, value);
            if (MatchesKeySet(keys, "ExpKill")) expKill = AddNullable(expKill, value);
            if (MatchesKeySet(keys, "Exp", "ExpExitStatus")) expExitStatus = AddNullable(expExitStatus, value);
        }

        string state = itemCount == 0 ? "present_empty" : nonZeroCount == 0 ? "present_zero_only" : "present_populated";
        return new CounterProbe(state, itemCount, nonZeroCount, kills, deaths, expKill, expExitStatus);
    }

    private static IReadOnlyList<VanguardOperatorCareerTruthVictim> ReadVictims(JsonElement eftStats)
    {
        if (!TryGetProperty(eftStats, "Victims", out JsonElement victims) || victims.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<VanguardOperatorCareerTruthVictim>();
        }

        var result = new List<VanguardOperatorCareerTruthVictim>();
        foreach (JsonElement victim in victims.EnumerateArray())
        {
            if (victim.ValueKind != JsonValueKind.Object) continue;
            result.Add(new VanguardOperatorCareerTruthVictim(
                ReadScalar(victim, "ProfileId"),
                ReadScalar(victim, "AccountId"),
                ReadScalar(victim, "Name"),
                ReadScalar(victim, "Side"),
                ReadInt32(victim, "Level"),
                ReadScalar(victim, "Role"),
                ReadScalar(victim, "Weapon"),
                ReadScalar(victim, "BodyPart"),
                ReadDouble(victim, "Distance"),
                ReadScalar(victim, "Location"),
                ReadScalar(victim, "Time")));
        }
        return result;
    }

    private static SkillProbe ReadSkills(JsonElement root)
    {
        if (!TryGetPath(root, out JsonElement common, "Skills", "Common") || common.ValueKind == JsonValueKind.Null)
        {
            return new SkillProbe("absent", 0, 0.0, Array.Empty<VanguardOperatorCareerTruthSkill>());
        }
        if (common.ValueKind != JsonValueKind.Array)
        {
            return new SkillProbe("present_invalid_shape", 0, 0.0, Array.Empty<VanguardOperatorCareerTruthSkill>());
        }

        int commonCount = 0;
        double total = 0.0;
        var earned = new List<VanguardOperatorCareerTruthSkill>();
        foreach (JsonElement skill in common.EnumerateArray())
        {
            if (skill.ValueKind != JsonValueKind.Object) continue;
            commonCount++;
            double points = ReadDouble(skill, "PointsEarnedDuringSession");
            if (Math.Abs(points) <= 0.0001d) continue;
            total += points;
            earned.Add(new VanguardOperatorCareerTruthSkill(
                ReadScalar(skill, "Id"),
                ReadDouble(skill, "Progress"),
                points));
        }

        string state = commonCount == 0 ? "present_empty" : earned.Count == 0 ? "present_zero_session_points" : "present_session_points";
        return new SkillProbe(state, commonCount, total, earned);
    }

    private static bool TryGetPath(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (string segment in path)
        {
            if (!TryGetProperty(value, segment, out JsonElement next))
            {
                value = default;
                return false;
            }
            value = next;
        }
        return true;
    }

    private static bool TryGetProperty(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object) return false;
        foreach (JsonProperty property in parent.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        return false;
    }

    private static bool TryFindDirectExitStatus(JsonElement root, out JsonElement value)
    {
        // CompleteProfileDescriptorClass does not expose ExitStatus in the supplied EFT source.
        // Keep the runtime probe bounded to plausible direct descriptor/stat locations so an
        // unrelated nested object cannot be misclassified as the raid-end outcome.
        if (TryGetProperty(root, "ExitStatus", out value)) return true;
        return TryGetPath(root, out value, "Stats", "Eft", "ExitStatus");
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string name)
    {
        if (!TryGetProperty(parent, name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }
        return array.EnumerateArray().Select(Scalar).Where(value => value != "none").ToArray();
    }

    private static bool MatchesKeySet(IReadOnlyList<string> actual, params string[] expected)
        => actual.Count == expected.Length
            && expected.All(value => actual.Any(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase)));

    private static long? AddNullable(long? current, long value) => current.HasValue ? current.Value + value : value;

    private static int ReadInt32(JsonElement parent, string name)
    {
        if (!TryGetProperty(parent, name, out JsonElement value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)) return result;
        return int.TryParse(Scalar(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0;
    }

    private static long ReadInt64(JsonElement parent, string name)
    {
        if (!TryGetProperty(parent, name, out JsonElement value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result)) return result;
        return long.TryParse(Scalar(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0;
    }

    private static double ReadDouble(JsonElement parent, string name)
    {
        if (!TryGetProperty(parent, name, out JsonElement value)) return 0.0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double result)) return result;
        return double.TryParse(Scalar(value), NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : 0.0;
    }

    private static string ReadScalar(JsonElement parent, string name)
        => TryGetProperty(parent, name, out JsonElement value) ? Scalar(value) : "none";

    private static string Scalar(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? "none" : value.GetString()!,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => "none",
            _ => value.GetRawText()
        };

    private static string NormalizeStatisticsManagerType(string? statisticsManagerType)
        => string.IsNullOrWhiteSpace(statisticsManagerType) ? "unknown" : statisticsManagerType.Trim();

    private static string ResolveNativeSessionExperienceAuthorityState(
        string statisticsManagerType,
        int totalSessionExperience,
        out bool available)
    {
        if (statisticsManagerType.EndsWith("Fika.Core.Main.ObservedClasses.ObservedStatisticsManager", StringComparison.Ordinal)
            || string.Equals(statisticsManagerType, "ObservedStatisticsManager", StringComparison.Ordinal))
        {
            available = false;
            return "unavailable_fika_observed_statistics_manager_stub";
        }

        available = false;
        if (totalSessionExperience > 0)
        {
            return "candidate_non_stub_total_session_experience_requires_source_confirmation";
        }

        return string.Equals(statisticsManagerType, "unknown", StringComparison.Ordinal)
            ? "unresolved_statistics_manager_type_unknown"
            : "unresolved_non_stub_manager_zero_or_unpopulated";
    }

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private sealed record CounterProbe(
        string State,
        int ItemCount,
        int NonZeroCount,
        long? Kills,
        long? Deaths,
        long? ExpKill,
        long? ExpExitStatus)
    {
        public static readonly CounterProbe Absent = new("absent", 0, 0, null, null, null, null);
    }

    private sealed record SkillProbe(
        string State,
        int CommonCount,
        double TotalSessionPoints,
        IReadOnlyList<VanguardOperatorCareerTruthSkill> SessionPointSkills);
}
