using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using Vanguard.Server.Diagnostics;
using Vanguard.Server.Operators.Models;

// Responsibility: Generates and applies the one-time deterministic pre-Vanguard skill/physical baseline for a newly enrolled Operator.
// Flow: Recruitment creates persistent baseline evidence from stable Operator facts; first EFT profile materialization applies Common/Mastering values and a pristine PMC physical state from that evidence.
// Authority boundary: EFT/SPT owns the level curve, Common skill cap and Mastering thresholds. Vanguard only supplies a versioned statistical background policy inside those constraints.
// Invariant: Stable Operator facts + the same policy produce the same per-skill values; no current date/session/player progression participates in randomization.
namespace Vanguard.Server.Operators.Services;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorEnrollmentBaselineService(
    VanguardEftExperienceCurveService experienceCurve,
    DatabaseService databaseService,
    ISptLogger<VanguardOperatorEnrollmentBaselineService> logger)
{
    public const string StatusTag = "VANGUARD_OPERATOR_ENROLLMENT_BASELINE_STATUS";
    public const string PolicyId = "vanguard.operator-enrollment-baseline";
    public const int PolicyVersion = 1;
    public const string PhysicalPolicyId = "eft-pmc-pristine-v1";
    public const string LegacyAdditiveMigrationOrigin = "legacy_additive_migration";
    public const string LegacyPhysicalPreservationPolicyId = "legacy-physical-state-preserved-v1";
    public const double CommonSkillCap = 5100d;

    private static readonly IReadOnlyDictionary<string, double> FieldSkillScales = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["Endurance"] = 1.00, ["Strength"] = 0.95, ["Vitality"] = 0.95, ["Health"] = 0.90,
        ["StressResistance"] = 0.95, ["Metabolism"] = 0.60, ["Immunity"] = 0.50,
        ["Perception"] = 0.95, ["Intellect"] = 0.45, ["Attention"] = 0.85, ["Memory"] = 0.45,
        ["MagDrills"] = 0.90, ["Pistol"] = 0.75, ["Revolver"] = 0.55, ["SMG"] = 0.85,
        ["Assault"] = 1.00, ["Shotgun"] = 0.80, ["Sniper"] = 0.75, ["LMG"] = 0.70,
        ["HMG"] = 0.35, ["Launcher"] = 0.25, ["AttachedLauncher"] = 0.25, ["Throwing"] = 0.65,
        ["Misc"] = 0.30, ["Melee"] = 0.50, ["DMR"] = 0.80, ["AimDrills"] = 0.95,
        ["RecoilControl"] = 0.95, ["TroubleShooting"] = 0.75, ["Sniping"] = 0.75,
        ["CovertMovement"] = 0.80, ["ProneMovement"] = 0.55, ["FirstAid"] = 0.75,
        ["FieldMedicine"] = 0.55, ["Surgery"] = 0.45, ["LightVests"] = 0.65,
        ["HeavyVests"] = 0.65, ["WeaponModding"] = 0.55, ["AdvancedModding"] = 0.35,
        ["NightOps"] = 0.65, ["SilentOps"] = 0.60, ["Lockpicking"] = 0.20,
        ["Search"] = 0.55, ["WeaponTreatment"] = 0.65,
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> ArchetypeSkillBonuses =
        new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Assault"] = Bonus(("Assault", .22), ("RecoilControl", .18), ("AimDrills", .14), ("MagDrills", .14), ("Endurance", .10), ("Strength", .08), ("Vitality", .08)),
            ["Recon"] = Bonus(("Perception", .22), ("Attention", .16), ("CovertMovement", .20), ("Endurance", .12), ("NightOps", .18), ("SilentOps", .16), ("DMR", .10), ("Assault", .08)),
            ["Support"] = Bonus(("LMG", .22), ("MagDrills", .15), ("Strength", .10), ("Endurance", .08), ("WeaponTreatment", .16), ("FirstAid", .10)),
            ["Veteran"] = Bonus(("StressResistance", .12), ("Vitality", .10), ("Health", .10), ("Perception", .10), ("RecoilControl", .10), ("AimDrills", .08), ("MagDrills", .08)),
            ["Marksman"] = Bonus(("Sniper", .26), ("Sniping", .26), ("DMR", .20), ("AimDrills", .20), ("Perception", .16), ("Attention", .15), ("RecoilControl", .08)),
            ["Breacher"] = Bonus(("Shotgun", .26), ("SMG", .20), ("Assault", .12), ("Strength", .12), ("Vitality", .12), ("StressResistance", .16), ("MagDrills", .15)),
            ["Medic"] = Bonus(("FirstAid", .30), ("FieldMedicine", .30), ("Surgery", .26), ("Health", .16), ("Vitality", .16), ("StressResistance", .12), ("Endurance", .08)),
        };

    private readonly object masteringSync = new();
    private IReadOnlyDictionary<string, double>? masteringCaps;
    private bool masteringResolutionAttempted;

    public VanguardOperatorEnrollmentBaselineState CreateNativeEnrollmentState(VanguardOperatorProfile profile, DateTimeOffset now)
        => CreateEnrollmentState(
            profile,
            Math.Max(profile.Progression.Level, 1),
            "native_enrollment",
            PhysicalPolicyId,
            now,
            clampLevel: true);

    public VanguardOperatorEnrollmentBaselineState CreateLegacyAdditiveMigrationState(VanguardOperatorProfile profile, DateTimeOffset now)
    {
        VanguardOperatorCareer career = profile.Career
            ?? throw new InvalidOperationException("operator_enrollment_migration_career_missing");
        return CreateEnrollmentState(
            profile,
            career.EnrollmentLevel,
            LegacyAdditiveMigrationOrigin,
            LegacyPhysicalPreservationPolicyId,
            now,
            clampLevel: false);
    }

    public VanguardOperatorEnrollmentBaselineApplication ApplyToSkills(JsonObject skills, VanguardOperatorProfile profile)
    {
        VanguardOperatorEnrollmentBaselineState state = profile.EnrollmentBaseline
            ?? throw new InvalidOperationException("operator_enrollment_baseline_evidence_missing");

        if (!string.Equals(state.PolicyId, PolicyId, StringComparison.Ordinal)
            || state.PolicyVersion != PolicyVersion
            || !state.CurveAuthoritative
            || state.MaximumCareerLevel < 1)
        {
            throw new InvalidOperationException("operator_enrollment_baseline_evidence_unsupported");
        }

        string seedMaterial = BuildSeedMaterial(profile.OperatorId, state.CareerLevel, state.Archetype, state.Side);
        string expectedFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seedMaterial)));
        if (!string.Equals(expectedFingerprint, state.SeedFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("operator_enrollment_baseline_seed_mismatch");
        }

        double maturity = ResolveMaturity(state.CareerLevel, state.MaximumCareerLevel);
        int commonSeen = 0;
        int commonSeeded = 0;
        int masteringSeen = 0;
        int masteringSeeded = 0;
        var commonFingerprintParts = new List<string>();
        var masteringFingerprintParts = new List<string>();

        JsonArray common = GetOrCreateArray(skills, "Common");
        foreach (JsonObject skill in common.OfType<JsonObject>())
        {
            string id = GetString(skill, "Id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            commonSeen++;
            double progress = GenerateCommonProgress(seedMaterial, state.Archetype, id, maturity);
            SetNumber(skill, "Progress", progress);
            SetNumber(skill, "PointsEarnedDuringSession", 0d);
            SetNumber(skill, "LastAccess", 0d);
            if (progress > 0.0001d)
            {
                commonSeeded++;
            }
            commonFingerprintParts.Add($"{id}:{progress:0.####}");
        }

        IReadOnlyDictionary<string, double> caps = ResolveMasteringCaps();
        JsonArray mastering = GetOrCreateArray(skills, "Mastering");
        foreach (JsonObject skill in mastering.OfType<JsonObject>())
        {
            string id = GetString(skill, "Id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            masteringSeen++;
            double progress = caps.TryGetValue(id, out double cap)
                ? GenerateMasteringProgress(seedMaterial, id, maturity, cap)
                : 0d;
            SetNumber(skill, "Progress", progress);
            if (progress > 0.0001d)
            {
                masteringSeeded++;
            }
            masteringFingerprintParts.Add($"{id}:{progress:0.####}");
        }

        SetNumber(skills, "Points", 0d);
        string commonFingerprint = Fingerprint(commonFingerprintParts);
        string masteringFingerprint = Fingerprint(masteringFingerprintParts);
        bool masteringAuthority = caps.Count > 0;

        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] phase=apply; operator={profile.OperatorId}; policy={state.PolicyId}; policyVersion={state.PolicyVersion}; level={state.CareerLevel}; maximumLevel={state.MaximumCareerLevel}; archetype={state.Archetype}; maturity={maturity:0.####}; commonSeen={commonSeen}; commonSeeded={commonSeeded}; commonFingerprint={commonFingerprint}; masteringSeen={masteringSeen}; masteringSeeded={masteringSeeded}; masteringAuthority={masteringAuthority.ToString().ToLowerInvariant()}; masteringFingerprint={masteringFingerprint}; physicalPolicy={state.PhysicalPolicyId}; tag={StatusTag}"));

        return new VanguardOperatorEnrollmentBaselineApplication(
            commonSeen,
            commonSeeded,
            masteringSeen,
            masteringSeeded,
            commonFingerprint,
            masteringFingerprint,
            masteringAuthority);
    }

    public VanguardOperatorEnrollmentAdditiveMigrationApplication ApplyAdditiveMigrationToSkills(
        JsonObject skills,
        VanguardOperatorProfile profile)
    {
        VanguardOperatorEnrollmentBaselineState state = profile.EnrollmentBaseline
            ?? throw new InvalidOperationException("operator_enrollment_baseline_evidence_missing");
        VanguardOperatorCareer career = profile.Career
            ?? throw new InvalidOperationException("operator_enrollment_migration_career_missing");

        if (!string.Equals(state.PolicyId, PolicyId, StringComparison.Ordinal)
            || state.PolicyVersion != PolicyVersion
            || state.SchemaVersion != VanguardOperatorEnrollmentBaselineSchema.CurrentVersion
            || !state.CurveAuthoritative
            || state.MaximumCareerLevel < 1
            || !string.Equals(state.Origin, LegacyAdditiveMigrationOrigin, StringComparison.Ordinal)
            || !string.Equals(state.PhysicalPolicyId, LegacyPhysicalPreservationPolicyId, StringComparison.Ordinal)
            || state.CareerLevel != career.EnrollmentLevel)
        {
            throw new InvalidOperationException("operator_enrollment_migration_evidence_unsupported");
        }

        string seedMaterial = BuildSeedMaterial(profile.OperatorId, state.CareerLevel, state.Archetype, state.Side);
        string expectedFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seedMaterial)));
        if (!string.Equals(expectedFingerprint, state.SeedFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("operator_enrollment_baseline_seed_mismatch");
        }

        double maturity = ResolveMaturity(state.CareerLevel, state.MaximumCareerLevel);
        JsonArray common = GetRequiredArray(skills, "Common", "operator_enrollment_migration_common_shape_missing");
        JsonArray mastering = GetRequiredArray(skills, "Mastering", "operator_enrollment_migration_mastering_shape_missing");
        var commonBefore = new List<string>();
        var commonAfter = new List<string>();
        var masteringBefore = new List<string>();
        var masteringAfter = new List<string>();
        int commonSeen = 0;
        int commonBaselineEligible = 0;
        int commonChanged = 0;
        double commonBaselineGeneratedTotal = 0d;
        double commonAddedTotal = 0d;

        foreach (JsonNode? node in common)
        {
            if (node is not JsonObject skill)
            {
                throw new InvalidOperationException("operator_enrollment_migration_common_entry_unreadable");
            }

            string id = GetString(skill, "Id");
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException("operator_enrollment_migration_common_id_missing");
            }

            double before = GetRequiredProgress(skill, "operator_enrollment_migration_common_progress_unreadable");
            if (before < 0d || before > CommonSkillCap)
            {
                throw new InvalidOperationException("operator_enrollment_migration_common_progress_out_of_bounds_" + Normalize(id, "unknown"));
            }

            double generated = GenerateCommonProgress(seedMaterial, state.Archetype, id, maturity);
            double after = RoundProgress(Math.Min(CommonSkillCap, before + generated));
            commonSeen++;
            if (generated > 0.0001d) commonBaselineEligible++;
            if (after + 0.0001d < before)
            {
                throw new InvalidOperationException("operator_enrollment_migration_common_regression_" + Normalize(id, "unknown"));
            }
            if (after > before + 0.0001d)
            {
                SetNumber(skill, "Progress", after);
                commonChanged++;
            }

            commonBaselineGeneratedTotal += generated;
            commonAddedTotal += Math.Max(0d, after - before);
            commonBefore.Add(FingerprintPart(id, before));
            commonAfter.Add(FingerprintPart(id, after));
        }

        if (commonSeen == 0 || commonBaselineEligible == 0)
        {
            throw new InvalidOperationException("operator_enrollment_migration_common_baseline_unresolved");
        }

        IReadOnlyDictionary<string, double> caps = ResolveMasteringCaps();
        int masteringSeen = 0;
        int masteringBaselineEligible = 0;
        int masteringChanged = 0;
        double masteringBaselineGeneratedTotal = 0d;
        double masteringAddedTotal = 0d;
        foreach (JsonNode? node in mastering)
        {
            if (node is not JsonObject skill)
            {
                throw new InvalidOperationException("operator_enrollment_migration_mastering_entry_unreadable");
            }

            string id = GetString(skill, "Id");
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException("operator_enrollment_migration_mastering_id_missing");
            }

            double before = GetRequiredProgress(skill, "operator_enrollment_migration_mastering_progress_unreadable");
            if (before < 0d)
            {
                throw new InvalidOperationException("operator_enrollment_migration_mastering_progress_negative_" + Normalize(id, "unknown"));
            }

            double generated = 0d;
            double after = before;
            if (caps.TryGetValue(id, out double cap))
            {
                double normalizedCap = Math.Clamp(cap, 0d, CommonSkillCap);
                if (before > normalizedCap + 0.0001d)
                {
                    throw new InvalidOperationException("operator_enrollment_migration_mastering_progress_out_of_bounds_" + Normalize(id, "unknown"));
                }

                generated = GenerateMasteringProgress(seedMaterial, id, maturity, normalizedCap);
                after = RoundProgress(Math.Min(normalizedCap, before + generated));
                if (generated > 0.0001d) masteringBaselineEligible++;
                if (after > before + 0.0001d)
                {
                    SetNumber(skill, "Progress", after);
                    masteringChanged++;
                }
            }

            if (after + 0.0001d < before)
            {
                throw new InvalidOperationException("operator_enrollment_migration_mastering_regression_" + Normalize(id, "unknown"));
            }

            masteringSeen++;
            masteringBaselineGeneratedTotal += generated;
            masteringAddedTotal += Math.Max(0d, after - before);
            masteringBefore.Add(FingerprintPart(id, before));
            masteringAfter.Add(FingerprintPart(id, after));
        }

        string commonBeforeFingerprint = Fingerprint(commonBefore);
        string commonAfterFingerprint = Fingerprint(commonAfter);
        string masteringBeforeFingerprint = Fingerprint(masteringBefore);
        string masteringAfterFingerprint = Fingerprint(masteringAfter);
        bool masteringAuthority = caps.Count > 0;

        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] phase=legacy_additive_apply; operator={profile.OperatorId}; origin={state.Origin}; policy={state.PolicyId}; policyVersion={state.PolicyVersion}; enrollmentLevel={state.CareerLevel}; maximumLevel={state.MaximumCareerLevel}; commonSeen={commonSeen}; commonBaselineEligible={commonBaselineEligible}; commonChanged={commonChanged}; commonGeneratedTotal={commonBaselineGeneratedTotal:0.####}; commonAddedTotal={commonAddedTotal:0.####}; commonBefore={commonBeforeFingerprint}; commonAfter={commonAfterFingerprint}; masteringSeen={masteringSeen}; masteringBaselineEligible={masteringBaselineEligible}; masteringChanged={masteringChanged}; masteringGeneratedTotal={masteringBaselineGeneratedTotal:0.####}; masteringAddedTotal={masteringAddedTotal:0.####}; masteringBefore={masteringBeforeFingerprint}; masteringAfter={masteringAfterFingerprint}; masteringAuthority={masteringAuthority.ToString().ToLowerInvariant()}; physicalMutation=false; tag={StatusTag}"));

        return new VanguardOperatorEnrollmentAdditiveMigrationApplication(
            commonSeen,
            commonBaselineEligible,
            commonChanged,
            RoundProgress(commonBaselineGeneratedTotal),
            RoundProgress(commonAddedTotal),
            masteringSeen,
            masteringBaselineEligible,
            masteringChanged,
            RoundProgress(masteringBaselineGeneratedTotal),
            RoundProgress(masteringAddedTotal),
            commonBeforeFingerprint,
            commonAfterFingerprint,
            masteringBeforeFingerprint,
            masteringAfterFingerprint,
            masteringAuthority);
    }

    private VanguardOperatorEnrollmentBaselineState CreateEnrollmentState(
        VanguardOperatorProfile profile,
        int requestedLevel,
        string origin,
        string physicalPolicyId,
        DateTimeOffset now,
        bool clampLevel)
    {
        VanguardOperatorExperienceCurveAuthority authority = experienceCurve.ResolveAuthority();
        if (!authority.IsAuthoritative || authority.MaximumLevel < 1)
        {
            throw new InvalidOperationException("eft_experience_curve_not_authoritative");
        }

        int level = clampLevel
            ? Math.Clamp(requestedLevel, 1, authority.MaximumLevel)
            : requestedLevel;
        if (level < 1 || level > authority.MaximumLevel)
        {
            throw new InvalidOperationException("operator_enrollment_baseline_level_outside_authority");
        }

        string archetype = Normalize(profile.Role, "Unknown");
        string side = Normalize(profile.Identity.Side, "Unknown");
        string seedMaterial = BuildSeedMaterial(profile.OperatorId, level, archetype, side);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seedMaterial)));
        var state = new VanguardOperatorEnrollmentBaselineState(
            PolicyId,
            PolicyVersion,
            origin,
            level,
            authority.MaximumLevel,
            archetype,
            side,
            fingerprint,
            authority.Source,
            true,
            physicalPolicyId,
            now,
            now,
            VanguardOperatorEnrollmentBaselineSchema.CurrentVersion);

        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] phase=evidence; operator={profile.OperatorId}; origin={state.Origin}; policy={state.PolicyId}; policyVersion={state.PolicyVersion}; level={state.CareerLevel}; maximumLevel={state.MaximumCareerLevel}; archetype={state.Archetype}; side={state.Side}; curve={state.CurveSource}; authoritative=true; physicalPolicy={state.PhysicalPolicyId}; seed={state.SeedFingerprint}; tag={StatusTag}"));
        return state;
    }

    private static double GenerateCommonProgress(string seedMaterial, string archetype, string skillId, double maturity)
    {
        if (!FieldSkillScales.TryGetValue(skillId, out double scale))
        {
            return 0d;
        }

        double general = 180d + 3300d * Math.Pow(maturity, 1.03d);
        double aptitude = 0.55d + 0.75d * DeterministicUnit(seedMaterial, "common", skillId, "aptitude");
        double noise = (DeterministicUnit(seedMaterial, "common", skillId, "noise") * 2d - 1d) * (220d + 420d * maturity);
        double archetypeBonus = ResolveArchetypeBonus(archetype, skillId);
        double roleWide = string.Equals(archetype, "Veteran", StringComparison.OrdinalIgnoreCase) ? 0.06d : 0d;
        double specialization = (archetypeBonus + roleWide) * (1000d + 1800d * maturity);
        double progress = general * scale * aptitude + specialization + noise;
        return RoundProgress(Math.Clamp(progress, 0d, CommonSkillCap));
    }

    private static double GenerateMasteringProgress(string seedMaterial, string masteringId, double maturity, double cap)
    {
        double normalizedCap = Math.Clamp(cap, 0d, CommonSkillCap);
        if (normalizedCap <= 0d)
        {
            return 0d;
        }

        double aptitude = 0.35d + 0.60d * DeterministicUnit(seedMaterial, "mastering", masteringId, "aptitude");
        double noise = (DeterministicUnit(seedMaterial, "mastering", masteringId, "noise") * 2d - 1d) * 0.08d;
        double fraction = Math.Clamp(0.03d + 0.62d * Math.Pow(maturity, 1.08d) * aptitude + noise, 0d, 0.88d);
        return RoundProgress(normalizedCap * fraction);
    }

    private IReadOnlyDictionary<string, double> ResolveMasteringCaps()
    {
        lock (masteringSync)
        {
            if (masteringResolutionAttempted)
            {
                return masteringCaps ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            }

            masteringResolutionAttempted = true;
            var resolved = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            try
            {
                object configuration = databaseService.GetGlobals().Configuration;
                object? mastering = ResolveMember(configuration, "Mastering");
                if (mastering is IEnumerable entries)
                {
                    foreach (object? entry in entries)
                    {
                        if (entry is null)
                        {
                            continue;
                        }

                        string id = ResolveText(entry, "Name", "Id");
                        int level2 = ResolveInt(entry, "Level2");
                        int level3 = ResolveInt(entry, "Level3");
                        int total = Math.Max(0, level2) + Math.Max(0, level3);
                        if (!string.IsNullOrWhiteSpace(id) && total > 0)
                        {
                            resolved[id] = Math.Min(total, (int)CommonSkillCap);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                logger.Warning(VanguardServerDiagnosticsLog.Present(
                    $"[{StatusTag}] phase=mastering_authority; resolved=false; type={exception.GetType().Name}; message={exception.Message}; action=leave_unresolved_mastering_zero; tag={StatusTag}"));
            }

            masteringCaps = resolved;
            logger.Info(VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] phase=mastering_authority; resolved={(resolved.Count > 0).ToString().ToLowerInvariant()}; groups={resolved.Count}; source=spt_globals_configuration_mastering; fallback=false; tag={StatusTag}"));
            return masteringCaps;
        }
    }

    private static IReadOnlyDictionary<string, double> Bonus(params (string Skill, double Bonus)[] values)
        => values.ToDictionary(value => value.Skill, value => value.Bonus, StringComparer.OrdinalIgnoreCase);

    private static double ResolveArchetypeBonus(string archetype, string skillId)
        => ArchetypeSkillBonuses.TryGetValue(archetype, out IReadOnlyDictionary<string, double>? values)
            && values.TryGetValue(skillId, out double bonus)
                ? bonus
                : 0d;

    private static double ResolveMaturity(int level, int maximumLevel)
    {
        if (maximumLevel <= 1)
        {
            return 0d;
        }

        double t = Math.Clamp((level - 1d) / (maximumLevel - 1d), 0d, 1d);
        return Math.Pow(t, 0.82d);
    }

    private static string BuildSeedMaterial(string operatorId, int level, string archetype, string side)
        => $"vanguard|operator-enrollment-baseline|v1|{Normalize(operatorId, "unknown")}|{level}|{Normalize(archetype, "unknown")}|{Normalize(side, "unknown")}";

    private static double DeterministicUnit(string seedMaterial, params string[] parts)
    {
        string material = seedMaterial + "|" + string.Join("|", parts.Select(part => Normalize(part, "unknown")));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        ulong value = 0UL;
        for (int index = 0; index < sizeof(ulong); index++)
        {
            value = (value << 8) | hash[index];
        }
        return value / (double)ulong.MaxValue;
    }

    private static string Fingerprint(IEnumerable<string> values)
    {
        string canonical = string.Join("|", values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static double RoundProgress(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static string FingerprintPart(string id, double progress)
        => Normalize(id, "unknown").ToLowerInvariant() + ":" + progress.ToString("0.####", CultureInfo.InvariantCulture);

    private static JsonArray GetRequiredArray(JsonObject parent, string name, string failureReason)
    {
        string? actualName = parent.Select(property => property.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        if (actualName is null || parent[actualName] is not JsonArray array)
        {
            throw new InvalidOperationException(failureReason);
        }
        return array;
    }

    private static double GetRequiredProgress(JsonObject parent, string failureReason)
    {
        string? actualName = parent.Select(property => property.Key)
            .FirstOrDefault(key => string.Equals(key, "Progress", StringComparison.OrdinalIgnoreCase));
        if (actualName is null || parent[actualName] is not JsonValue scalar)
        {
            throw new InvalidOperationException(failureReason);
        }

        double value;
        if (scalar.TryGetValue<double>(out double doubleValue)) value = doubleValue;
        else if (scalar.TryGetValue<int>(out int intValue)) value = intValue;
        else if (scalar.TryGetValue<long>(out long longValue)) value = longValue;
        else if (!double.TryParse(scalar.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            throw new InvalidOperationException(failureReason);
        }

        if (!double.IsFinite(value))
        {
            throw new InvalidOperationException(failureReason);
        }
        return value;
    }

    private static JsonArray GetOrCreateArray(JsonObject parent, string name)
    {
        string actualName = parent.Select(property => property.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) ?? name;
        if (parent[actualName] is JsonArray array)
        {
            return array;
        }

        array = new JsonArray();
        parent[actualName] = array;
        return array;
    }

    private static string GetString(JsonObject parent, string name)
    {
        string? actualName = parent.Select(property => property.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        if (actualName is null || parent[actualName] is not JsonValue value)
        {
            return string.Empty;
        }

        return value.TryGetValue<string>(out string? result) ? result ?? string.Empty : value.ToString();
    }

    private static void SetNumber(JsonObject parent, string name, double value)
    {
        string actualName = parent.Select(property => property.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) ?? name;
        parent[actualName] = JsonValue.Create(value);
    }

    private static object? ResolveMember(object source, string name)
    {
        Type type = source.GetType();
        PropertyInfo? property = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (property is not null)
        {
            return property.GetValue(source);
        }

        FieldInfo? field = type.GetFields(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        return field?.GetValue(source);
    }

    private static string ResolveText(object source, params string[] names)
    {
        foreach (string name in names)
        {
            object? value = ResolveMember(source, name);
            if (value is not null && !string.IsNullOrWhiteSpace(value.ToString()))
            {
                return value.ToString()!;
            }
        }
        return string.Empty;
    }

    private static int ResolveInt(object source, string name)
    {
        object? value = ResolveMember(source, name);
        if (value is null)
        {
            return 0;
        }

        return value switch
        {
            int typed => typed,
            long typed => (int)Math.Clamp(typed, int.MinValue, int.MaxValue),
            _ => int.TryParse(value.ToString(), out int parsed) ? parsed : 0,
        };
    }

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

public sealed record VanguardOperatorEnrollmentBaselineApplication(
    int CommonSkillCount,
    int CommonSeededCount,
    int MasteringSkillCount,
    int MasteringSeededCount,
    string CommonFingerprint,
    string MasteringFingerprint,
    bool MasteringAuthorityResolved);

public sealed record VanguardOperatorEnrollmentAdditiveMigrationApplication(
    int CommonSkillCount,
    int CommonBaselineEligibleCount,
    int CommonChangedCount,
    double CommonBaselineGeneratedTotal,
    double CommonAddedTotal,
    int MasteringSkillCount,
    int MasteringBaselineEligibleCount,
    int MasteringChangedCount,
    double MasteringBaselineGeneratedTotal,
    double MasteringAddedTotal,
    string CommonBeforeFingerprint,
    string CommonAfterFingerprint,
    string MasteringBeforeFingerprint,
    string MasteringAfterFingerprint,
    bool MasteringAuthorityResolved);
