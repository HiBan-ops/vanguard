using Vanguard.Server.Operators.Models;

// Responsibility: Normalize admitted legacy Operator profiles and their versioned subrecords while preserving enrollment state.
// Flow: Check migration admission, normalize career and core component schemas, then flag only actual changes.
// Authority boundary: The cumulative migration policy controls admission; this normalizer does not enroll Operators or change gameplay.
// Invariant: Pre-enrollment world Operators may legitimately have Career == null; only legacy enrolled/service profiles are synthesized into MigratedLegacy career state.
namespace Vanguard.Server.Operators.Services;

internal static class VanguardOperatorProfileMigrator
{
    public static VanguardOperatorProfile Normalize(VanguardOperatorProfile profile, DateTimeOffset now, out bool changed)
    {
        changed = false;

        // Compatibility normalization is only valid for explicitly supported predecessor schemas/markers.
        // Never coerce a future or conflicting persistent shape down to the current schema merely because it deserialized.
        if (!VanguardOperatorCumulativeMigrationPolicy.IsCoreNormalizationAdmitted(profile))
        {
            return profile;
        }

        var career = profile.Career;
        bool isPreEnrollmentWorldOperator = career is null
            && string.Equals(profile.ContractStatus, VanguardOperatorContractStatuses.Uncontracted, StringComparison.OrdinalIgnoreCase);

        if (career is null && !isPreEnrollmentWorldOperator)
        {
            career = VanguardOperatorCareer.MigratedLegacy(profile, now);
            changed = true;
        }

        if (career is not null)
        {
            VanguardOperatorCareerStatistics statistics = career.Statistics.SchemaVersion == VanguardOperatorCareerSchema.CurrentVersion
                ? career.Statistics
                : career.Statistics with { SchemaVersion = VanguardOperatorCareerSchema.CurrentVersion };

            VanguardOperatorExperienceReconciliation? reconciliation = career.ExperienceReconciliation;
            if (reconciliation is not null
                && reconciliation.SchemaVersion != VanguardOperatorExperienceReconciliationSchema.CurrentVersion)
            {
                reconciliation = reconciliation with { SchemaVersion = VanguardOperatorExperienceReconciliationSchema.CurrentVersion };
            }

            VanguardOperatorCareerXpCommitState? xpCommitState = career.XpCommitState;
            if (xpCommitState is not null
                && xpCommitState.SchemaVersion != VanguardOperatorCareerXpCommitSchema.CurrentVersion)
            {
                xpCommitState = xpCommitState with { SchemaVersion = VanguardOperatorCareerXpCommitSchema.CurrentVersion };
            }

            if (career.SchemaVersion != VanguardOperatorCareerSchema.CurrentVersion
                || !ReferenceEquals(statistics, career.Statistics)
                || !ReferenceEquals(reconciliation, career.ExperienceReconciliation)
                || !ReferenceEquals(xpCommitState, career.XpCommitState))
            {
                career = career with
                {
                    Statistics = statistics,
                    ExperienceReconciliation = reconciliation,
                    XpCommitState = xpCommitState,
                    SchemaVersion = VanguardOperatorCareerSchema.CurrentVersion,
                };
                changed = true;
            }
        }

        var identity = profile.Identity.SchemaVersion == VanguardOperatorSchema.CurrentVersion
            ? profile.Identity
            : profile.Identity with { SchemaVersion = VanguardOperatorSchema.CurrentVersion };
        var persona = profile.Persona.SchemaVersion == VanguardOperatorSchema.CurrentVersion
            ? profile.Persona
            : profile.Persona with { SchemaVersion = VanguardOperatorSchema.CurrentVersion };
        var progression = profile.Progression.SchemaVersion == VanguardOperatorSchema.CurrentVersion
            ? profile.Progression
            : profile.Progression with { SchemaVersion = VanguardOperatorSchema.CurrentVersion };

        changed |= !ReferenceEquals(identity, profile.Identity)
            || !ReferenceEquals(persona, profile.Persona)
            || !ReferenceEquals(progression, profile.Progression)
            || profile.SchemaVersion != VanguardOperatorSchema.CurrentVersion;

        return profile with
        {
            Identity = identity,
            Persona = persona,
            Progression = progression,
            Career = career,
            SchemaVersion = VanguardOperatorSchema.CurrentVersion,
            UpdatedAtUtc = changed ? now : profile.UpdatedAtUtc,
        };
    }
}
