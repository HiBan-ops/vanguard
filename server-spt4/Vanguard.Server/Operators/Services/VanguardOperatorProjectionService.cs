using SPTarkov.DI.Annotations;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Responses;

// Responsibility: Coordinates Operator Projection Service for the Operator domain services, delegating specialized work to its collaborators.
// Flow: Caller/route input is validated and normalized, canonical Operator/profile state is read or updated through the owning store/integration, then a response and diagnostics are produced.
// Authority boundary: Server domain orchestration only; persistent truth remains explicit in the Operator/SPT stores and client in-raid execution remains separate.
// Invariant: Operations stay profile-scoped, deterministic/idempotent where required, and partial failures do not silently corrupt canonical state.
namespace Vanguard.Server.Operators.Services;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorProjectionService(VanguardEftExperienceCurveService experienceCurve)
{
    public IReadOnlyList<VanguardOperatorServiceProjection> BuildServiceProjections(
        IReadOnlyList<VanguardOperatorProfile> operators,
        IReadOnlyList<VanguardActiveServiceRecord> activeService,
        IReadOnlyList<VanguardOperatorMedicalRecord> medicalRecords)
    {
        var activeByOperator = activeService.ToDictionary(record => record.OperatorId, StringComparer.OrdinalIgnoreCase);
        var medicalByOperator = medicalRecords.ToDictionary(record => record.OperatorId, StringComparer.OrdinalIgnoreCase);

        return operators
            .OrderBy(operatorProfile => operatorProfile.Identity.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(operatorProfile =>
            {
                activeByOperator.TryGetValue(operatorProfile.OperatorId, out var active);
                medicalByOperator.TryGetValue(operatorProfile.OperatorId, out var medical);
                var selected = active?.IsSelectedForRaid ?? false;
                var eligibility = VanguardOperatorRaidEligibilityPolicy.Evaluate(active, medical, DateTimeOffset.UtcNow);
                var experienceWindow = experienceCurve.ResolveWindow(operatorProfile.Progression.Experience);
                bool experienceLevelCoherent = experienceWindow.IsAuthoritative
                    && experienceWindow.Level == operatorProfile.Progression.Level;
                int experienceIntoLevel = experienceLevelCoherent
                    ? Math.Max(0, operatorProfile.Progression.Experience - experienceWindow.CurrentLevelFloorExperience)
                    : 0;
                int requiredForNextLevel = experienceLevelCoherent
                    ? Math.Max(0, experienceWindow.NextLevelExperience - experienceWindow.CurrentLevelFloorExperience)
                    : 0;
                bool legacyExperienceReconciled = operatorProfile.Career?.ExperienceReconciliation is not null;
                string experienceProgressState = !experienceWindow.IsAuthoritative
                    ? "eft_curve_unresolved"
                    : experienceLevelCoherent
                        ? (requiredForNextLevel > 0
                            ? legacyExperienceReconciled ? "eft_curve_coherent_legacy_reconciled" : "eft_curve_coherent"
                            : "eft_curve_max_level")
                        : "legacy_level_xp_mismatch_pending";
                return new VanguardOperatorServiceProjection(
                    operatorProfile.OperatorId,
                    operatorProfile.Identity.DisplayName,
                    operatorProfile.Identity.Side,
                    operatorProfile.Role,
                    operatorProfile.Specialty,
                    operatorProfile.Identity.VisualFamily,
                    operatorProfile.Progression.Level,
                    operatorProfile.Progression.Experience,
                    operatorProfile.ContractStatus,
                    active?.Status ?? operatorProfile.ServiceStatus,
                    selected,
                    active?.IsDeployed ?? false,
                    operatorProfile.SalaryPerRaid,
                    operatorProfile.Progression.RaidCount,
                    operatorProfile.Progression.SurvivedRaidCount,
                    operatorProfile.Progression.KillCount,
                    operatorProfile.Persona.BasePersona,
                    operatorProfile.Persona.Doctrine,
                    operatorProfile.Persona.Temperament,
                    operatorProfile.Persona.Traits,
                    operatorProfile.Persona.SainProfileFamily,
                    operatorProfile.Persona.SainTuningPlan,
                    operatorProfile.Progression.Trust,
                    operatorProfile.Progression.Loyalty,
                    eligibility.IsEligible ? "eligible" : "not_eligible",
                    eligibility.Reason,
                    experienceIntoLevel,
                    requiredForNextLevel,
                    experienceLevelCoherent ? experienceWindow.NextLevelExperience : 0,
                    experienceWindow.Source,
                    experienceWindow.Level,
                    experienceLevelCoherent,
                    experienceProgressState,
                    operatorProfile.SchemaVersion);
            })
            .ToArray();
    }

    public IReadOnlyList<VanguardOperatorMedicalProjection> BuildMedicalProjections(
        IReadOnlyList<VanguardOperatorProfile> operators,
        IReadOnlyList<VanguardActiveServiceRecord> activeService,
        IReadOnlyList<VanguardOperatorMedicalRecord> medicalRecords)
    {
        var activeByOperator = activeService.ToDictionary(record => record.OperatorId, StringComparer.OrdinalIgnoreCase);
        var operatorById = operators.ToDictionary(operatorProfile => operatorProfile.OperatorId, StringComparer.OrdinalIgnoreCase);

        return medicalRecords
            .OrderBy(record => record.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(record =>
            {
                operatorById.TryGetValue(record.OperatorId, out var operatorProfile);
                activeByOperator.TryGetValue(record.OperatorId, out var active);
                return new VanguardOperatorMedicalProjection(
                    record.OperatorId,
                    record.DisplayName,
                    operatorProfile?.Role ?? "Operator",
                    operatorProfile?.Progression.Level ?? 1,
                    active?.Status ?? VanguardOperatorServiceStatuses.Unavailable,
                    record.Status,
                    record.CurrentHealthRatio,
                    record.InjurySummary,
                    record.RecoveryUntilUtc,
                    record.HealCost,
                    record.RecoveryCost,
                    record.DiedInLastRaid,
                    record.UpdatedAtUtc,
                    ResolveRecoveryState(record, DateTimeOffset.UtcNow),
                    record.SchemaVersion);
            })
            .ToArray();
    }

    public IReadOnlyList<VanguardOperatorRaidProjection> BuildRaidProjections(
        IReadOnlyList<VanguardOperatorProfile> operators,
        IReadOnlyList<VanguardActiveServiceRecord> activeService,
        IReadOnlyList<VanguardOperatorMedicalRecord> medicalRecords)
    {
        var operatorById = operators.ToDictionary(operatorProfile => operatorProfile.OperatorId, StringComparer.OrdinalIgnoreCase);
        var medicalByOperator = medicalRecords.ToDictionary(record => record.OperatorId, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;

        return activeService
            .Where(record => record.IsSelectedForRaid)
            .OrderBy(record => record.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(record =>
            {
                operatorById.TryGetValue(record.OperatorId, out var operatorProfile);
                medicalByOperator.TryGetValue(record.OperatorId, out var medical);
                var eligibility = VanguardOperatorRaidEligibilityPolicy.Evaluate(record, medical, now);
                var health = eligibility.HealthRatio;

                return new VanguardOperatorRaidProjection(
                    $"raid-projection-{record.OperatorId}",
                    record.OperatorId,
                    record.DisplayName,
                    record.Side,
                    operatorProfile?.Progression.Level ?? 1,
                    operatorProfile?.Role ?? record.Role,
                    operatorProfile?.Specialty ?? record.Specialty,
                    operatorProfile?.Persona.BasePersona ?? "Disciplined",
                    operatorProfile?.Persona.Doctrine ?? "fire_discipline_and_squad_cohesion",
                    operatorProfile?.Persona.Temperament ?? "methodical",
                    operatorProfile?.Persona.Traits ?? Array.Empty<string>(),
                    operatorProfile?.Persona.SainProfileFamily ?? "vanguard.sain.disciplined",
                    operatorProfile?.Persona.SainTuningPlan ?? "vanguard.tuning.disciplined.standard",
                    true,
                    record.IsSelectedForRaid,
                    eligibility.IsEligible,
                    eligibility.Reason,
                    medical?.Status ?? VanguardOperatorServiceStatuses.Available,
                    health,
                    "offraid_projection_only_runtime_not_loaded",
                    VanguardBuildVersion.BuildLabel,
                    VanguardOperatorSchema.CurrentVersion,
                    now);
            })
            .ToArray();
    }

    private static string ResolveRecoveryState(VanguardOperatorMedicalRecord record, DateTimeOffset now)
    {
        if (record.RecoveryUntilUtc is DateTimeOffset until && until > now)
        {
            return "recovering";
        }

        if (record.CurrentHealthRatio >= 0.999)
        {
            return "recovered";
        }

        return "requires_treatment";
    }

}
