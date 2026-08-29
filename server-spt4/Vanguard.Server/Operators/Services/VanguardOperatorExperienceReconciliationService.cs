using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Storage;
using Vanguard.Server.Diagnostics;

// Responsibility: Repairs old Operator level/XP pairs that do not fit the authoritative EFT cumulative curve while preserving the highest historically established level.
// Flow: The service resolves the EFT curve, detects incoherent persisted progression, computes the minimum curve-consistent XP floor, stores a reversible reconciliation record, then saves the corrected profile.
// Authority boundary: Reconciliation may repair persistent progression shape, but it never invents missing historical raid XP or rewrite verified Career ledger events.
// Invariant: A coherent profile is left unchanged; a repaired profile keeps its preserved level, is reversible from stored evidence, and converges on repeated runs.
namespace Vanguard.Server.Operators.Services;

/// <summary>
/// Reconciles historical Operator Level/Experience pairs against the authoritative EFT cumulative
/// experience curve without inventing historical raid XP. The migration path covers profiles created before cumulative XP became authoritative.
/// progression convergence extends the same level-preserving floor rebase to safe, still-unmodified native progression
/// profiles whose persisted cumulative XP resolves below their preserved historical level.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorExperienceReconciliationService(
    VanguardOperatorStore store,
    VanguardEftExperienceCurveService experienceCurve,
    ISptLogger<VanguardOperatorExperienceReconciliationService> logger)
{
    public const string StatusTag = "VANGUARD_XP_RECONCILIATION_STATUS";
    public const string PolicyId = "legacy_level_floor_v1";
    public const int PolicyVersion = 1;
    public const string ReconciliationPolicyId = "incoherent_cumulative_level_floor_v2";
    public const int ReconciliationPolicyVersion = 2;

    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<VanguardOperatorProfile>> ReconcileLegacyBaselinesAsync(
        string storageProfileId,
        IReadOnlyList<VanguardOperatorProfile> operators)
    {
        if (operators.Count == 0)
        {
            return operators;
        }

        await gate.WaitAsync();
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var reconciled = operators.ToArray();
            int eligibleLegacy = 0;
            int eligibleNative = 0;
            int alreadyAudited = 0;
            int alreadyCoherent = 0;
            int pendingCurve = 0;
            int unsupportedShape = 0;
            int changed = 0;
            var changedIndexes = new List<int>();

            for (int index = 0; index < reconciled.Length; index++)
            {
                VanguardOperatorProfile profile = reconciled[index];
                VanguardOperatorCareer? career = profile.Career;
                if (career is null)
                {
                    continue;
                }

                int preservedLevel = Math.Max(profile.Progression.Level, 1);
                VanguardOperatorExperienceWindow desiredWindow = experienceCurve.ResolveLevelWindow(preservedLevel);
                if (!desiredWindow.IsAuthoritative)
                {
                    pendingCurve++;
                    continue;
                }

                if (desiredWindow.Level != preservedLevel)
                {
                    unsupportedShape++;
                    logger.Warning(VanguardServerDiagnosticsLog.Present(
                        $"[{VanguardBuildVersion.CareerXpProfileParityStatusTag}] phase=baseline_reconciliation; owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; state=skipped; reason=historical_level_not_representable_by_current_eft_curve; preservedLevel={preservedLevel}; resolvedLevel={desiredWindow.Level}; xpMutation=false; levelMutation=false; tag={VanguardBuildVersion.CareerXpProfileParityStatusTag}"));
                    continue;
                }

                VanguardOperatorExperienceWindow currentWindow = experienceCurve.ResolveWindow(Math.Max(profile.Progression.Experience, 0));
                if (currentWindow.IsAuthoritative && currentWindow.Level == preservedLevel)
                {
                    alreadyCoherent++;
                    continue;
                }

                // Never lower XP or Level. The runtime only repairs the proven historical shape where cumulative
                // XP resolves BELOW the preserved level (for example the old 56k-XP / level-48 profile).
                if (!currentWindow.IsAuthoritative || currentWindow.Level >= preservedLevel)
                {
                    unsupportedShape++;
                    logger.Warning(VanguardServerDiagnosticsLog.Present(
                        $"[{VanguardBuildVersion.CareerXpProfileParityStatusTag}] phase=baseline_reconciliation; owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; state=skipped; reason={(currentWindow.IsAuthoritative ? "xp_resolves_at_or_above_preserved_level" : "current_xp_curve_not_authoritative")}; progressionLevel={profile.Progression.Level}; progressionXp={profile.Progression.Experience}; resolvedLevel={currentWindow.Level}; xpMutation=false; levelMutation=false; tag={VanguardBuildVersion.CareerXpProfileParityStatusTag}"));
                    continue;
                }

                if (career.ExperienceReconciliation is not null)
                {
                    alreadyAudited++;
                    logger.Warning(VanguardServerDiagnosticsLog.Present(
                        $"[{VanguardBuildVersion.CareerXpProfileParityStatusTag}] phase=baseline_reconciliation; owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; state=skipped; reason=existing_reconciliation_evidence_but_pair_still_incoherent; policy={Safe(career.ExperienceReconciliation.PolicyId)}; progressionLevel={profile.Progression.Level}; progressionXp={profile.Progression.Experience}; resolvedLevel={currentWindow.Level}; mutation=false; tag={VanguardBuildVersion.CareerXpProfileParityStatusTag}"));
                    continue;
                }

                bool migratedLegacy = IsPartialLegacyHistory(career.HistoryCompleteness);
                if (migratedLegacy)
                {
                    eligibleLegacy++;
                }
                else
                {
                    eligibleNative++;
                }

                if (career.EnrollmentLevel != preservedLevel)
                {
                    unsupportedShape++;
                    logger.Warning(VanguardServerDiagnosticsLog.Present(
                        $"[{VanguardBuildVersion.CareerXpProfileParityStatusTag}] phase=baseline_reconciliation; owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; state=skipped; reason=enrollment_level_differs_from_progression_level; progressionLevel={profile.Progression.Level}; enrollmentLevel={career.EnrollmentLevel}; xpMutation=false; levelMutation=false; tag={VanguardBuildVersion.CareerXpProfileParityStatusTag}"));
                    continue;
                }

                if (career.ExperienceEarnedSinceEnrollment != 0)
                {
                    unsupportedShape++;
                    logger.Warning(VanguardServerDiagnosticsLog.Present(
                        $"[{VanguardBuildVersion.CareerXpProfileParityStatusTag}] phase=baseline_reconciliation; owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; state=skipped; reason=earned_xp_nonzero_requires_dedicated_policy; earnedXp={career.ExperienceEarnedSinceEnrollment}; xpMutation=false; levelMutation=false; tag={VanguardBuildVersion.CareerXpProfileParityStatusTag}"));
                    continue;
                }

                if (career.EnrollmentExperience != profile.Progression.Experience)
                {
                    unsupportedShape++;
                    logger.Warning(VanguardServerDiagnosticsLog.Present(
                        $"[{VanguardBuildVersion.CareerXpProfileParityStatusTag}] phase=baseline_reconciliation; owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; state=skipped; reason=enrollment_xp_differs_from_progression_xp; progressionXp={profile.Progression.Experience}; enrollmentXp={career.EnrollmentExperience}; xpMutation=false; levelMutation=false; tag={VanguardBuildVersion.CareerXpProfileParityStatusTag}"));
                    continue;
                }

                VanguardOperatorCareerXpCommitState? xpCommitState = career.XpCommitState;
                bool xpCommitAlreadyMutated = xpCommitState is not null
                    && (xpCommitState.AppliedCreditCount != 0
                        || xpCommitState.TotalCommittedExperience != 0
                        || (xpCommitState.AppliedCreditTokens?.Count ?? 0) != 0);
                if (!migratedLegacy && xpCommitAlreadyMutated)
                {
                    unsupportedShape++;
                    logger.Warning(VanguardServerDiagnosticsLog.Present(
                        $"[{VanguardBuildVersion.CareerXpProfileParityStatusTag}] phase=baseline_reconciliation; owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; state=skipped; reason=career_xp_already_committed_requires_dedicated_policy; appliedCredits={xpCommitState!.AppliedCreditCount}; committedXp={xpCommitState.TotalCommittedExperience}; xpMutation=false; levelMutation=false; tag={VanguardBuildVersion.CareerXpProfileParityStatusTag}"));
                    continue;
                }

                int previousExperience = Math.Max(profile.Progression.Experience, 0);
                int reconciledExperience = desiredWindow.CurrentLevelFloorExperience;
                string policyId = migratedLegacy ? PolicyId : ReconciliationPolicyId;
                int policyVersion = migratedLegacy ? PolicyVersion : ReconciliationPolicyVersion;
                string evidenceState = migratedLegacy
                    ? "applied_level_preserved_xp_rebased_to_eft_cumulative_floor"
                    : "applied_level_preserved_incoherent_cumulative_xp_rebased_to_eft_floor";
                string evidenceReason = migratedLegacy
                    ? "legacy_xp_was_synthetic_and_not_curve_coherent_no_historical_raid_xp_invented"
                    : "native_historical_level_preserved_low_cumulative_xp_rebased_to_eft_floor_no_historical_raid_xp_invented";

                var evidence = new VanguardOperatorExperienceReconciliation(
                    policyId,
                    policyVersion,
                    evidenceState,
                    profile.Progression.Level,
                    previousExperience,
                    career.EnrollmentLevel,
                    career.EnrollmentExperience,
                    preservedLevel,
                    reconciledExperience,
                    desiredWindow.CurrentLevelFloorExperience,
                    desiredWindow.NextLevelExperience,
                    desiredWindow.Source,
                    desiredWindow.IsAuthoritative,
                    career.ExperienceEarnedSinceEnrollment,
                    true,
                    now,
                    evidenceReason);

                VanguardOperatorCareer updatedCareer = career with
                {
                    EnrollmentExperience = reconciledExperience,
                    ExperienceReconciliation = evidence,
                    SchemaVersion = VanguardOperatorCareerSchema.CurrentVersion,
                };
                VanguardOperatorProgression updatedProgression = profile.Progression with
                {
                    Level = preservedLevel,
                    Experience = reconciledExperience,
                    SchemaVersion = VanguardOperatorSchema.CurrentVersion,
                };
                reconciled[index] = profile with
                {
                    Progression = updatedProgression,
                    Career = updatedCareer,
                    UpdatedAtUtc = now,
                    SchemaVersion = VanguardOperatorSchema.CurrentVersion,
                };
                changed++;
                changedIndexes.Add(index);
            }

            if (changed == 0)
            {
                logger.Info(VanguardServerDiagnosticsLog.Present(
                    $"[{VanguardBuildVersion.CareerXpProfileParityStatusTag}] phase=baseline_reconciliation; owner={Safe(storageProfileId)}; state=no_write; eligibleLegacy={eligibleLegacy}; eligibleNative={eligibleNative}; changed=0; alreadyAudited={alreadyAudited}; alreadyCoherent={alreadyCoherent}; pendingCurve={pendingCurve}; unsupportedShape={unsupportedShape}; cumulativeExperienceSemantics=from_level_1; currentLevelXpSemantics=derived_from_cumulative_floor; xpEarnedMutation=false; levelMutation=false; backupPresent={Bool(store.HasExperienceReconciliationBackup(storageProfileId))}; tag={VanguardBuildVersion.CareerXpProfileParityStatusTag}"));
                return operators;
            }

            VanguardOperatorExperienceReconciliationWriteResult write = await store.CommitExperienceReconciliationAsync(
                storageProfileId,
                operators,
                reconciled);
            if (!write.Success)
            {
                logger.Error(VanguardServerDiagnosticsLog.Present(
                    $"[{VanguardBuildVersion.CareerXpProfileParityStatusTag}] phase=baseline_reconciliation; owner={Safe(storageProfileId)}; state=write_failed; reason={Safe(write.Reason)}; eligibleLegacy={eligibleLegacy}; eligibleNative={eligibleNative}; candidateChanges={changed}; backupPresent={Bool(write.PermanentBackupPresent)}; readback={Bool(write.ReadBackVerified)}; rollback={Bool(write.RolledBack)}; xpEarnedMutation=false; levelMutation=false; tag={VanguardBuildVersion.CareerXpProfileParityStatusTag}"));
                return operators;
            }

            foreach (int changedIndex in changedIndexes)
            {
                VanguardOperatorProfile profile = reconciled[changedIndex];
                VanguardOperatorExperienceReconciliation? evidence = profile.Career?.ExperienceReconciliation;
                if (evidence is null)
                {
                    logger.Error(VanguardServerDiagnosticsLog.Present(
                        $"[{VanguardBuildVersion.CareerXpProfileParityStatusTag}] phase=baseline_reconciliation; owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; state=post_commit_evidence_missing; action=retain_committed_state_and_flag_diagnostic; xpEarnedMutation=false; levelMutation=false; tag={VanguardBuildVersion.CareerXpProfileParityStatusTag}"));
                    continue;
                }

                logger.Info(VanguardServerDiagnosticsLog.Present(
                    $"[{VanguardBuildVersion.CareerXpProfileParityStatusTag}] phase=baseline_reconciliation; owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; state=committed_readback_verified; policy={Safe(evidence.PolicyId)}; policyVersion={evidence.PolicyVersion}; previousLevel={evidence.PreviousProgressionLevel}; previousCumulativeXp={evidence.PreviousProgressionExperience}; preservedLevel={evidence.PreservedLevel}; reconciledCumulativeXp={evidence.ReconciledExperience}; levelFloor={evidence.CurrentLevelFloorExperience}; nextLevelCumulativeXp={evidence.NextLevelExperience}; curve={Safe(evidence.CurveSource)}; authoritative={Bool(evidence.CurveAuthoritative)}; reversible={Bool(evidence.Reversible)}; backupPresent={Bool(write.PermanentBackupPresent)}; xpEarnedPreserved={evidence.ExperienceEarnedSinceEnrollmentPreserved}; historicalXpInvented=false; xpEarnedMutation=false; levelMutation=false; tag={VanguardBuildVersion.CareerXpProfileParityStatusTag}"));
            }

            return reconciled;
        }
        catch (Exception exception)
        {
            logger.Error(VanguardServerDiagnosticsLog.Present(
                $"[{VanguardBuildVersion.CareerXpProfileParityStatusTag}] phase=baseline_reconciliation; owner={Safe(storageProfileId)}; state=unexpected_exception_fail_safe; type={Safe(exception.GetType().Name)}; xpMutation=false; levelMutation=false; action=return_pre_reconciliation_state; tag={VanguardBuildVersion.CareerXpProfileParityStatusTag}"));
            return operators;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private static bool IsPartialLegacyHistory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string normalized = value.Trim();
        return string.Equals(normalized, "partial_from_legacy_migration", StringComparison.OrdinalIgnoreCase)
            || (normalized.StartsWith("partial_from_", StringComparison.OrdinalIgnoreCase)
                && normalized.EndsWith("_migration", StringComparison.OrdinalIgnoreCase));
    }
}
