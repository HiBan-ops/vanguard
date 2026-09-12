using Vanguard.Server.Operators.Models;

// Responsibility: Classifies persistent Operator states for cumulative schema/marker migration without performing persistence or gameplay mutation.
// Flow: Callers assess one Operator, receive an ordered migration plan, and use that plan to admit only known predecessor states to the existing transactional migration steps.
// Authority boundary: The policy reads Vanguard schema and durable marker contracts only; it does not own EFT profile data, persistence writes, or migration transactions.
// Invariant: Unknown, malformed, conflicting, or newer persistent shapes are fail-closed, while supported predecessors can only advance VisualIdentity before EnrollmentBaseline when both are missing.
namespace Vanguard.Server.Operators.Services;

internal static class VanguardOperatorCumulativeMigrationPolicy
{
    public const string PolicyId = "vanguard.operator-cumulative-migration";
    public const int PolicyVersion = 1;

    public static VanguardOperatorCumulativeMigrationAssessment Assess(VanguardOperatorProfile profile)
    {
        string? unsupportedReason = ResolveUnsupportedPersistentShape(profile);
        if (!string.IsNullOrWhiteSpace(unsupportedReason))
        {
            return VanguardOperatorCumulativeMigrationAssessment.UnsupportedState(unsupportedReason);
        }

        bool worldNotYetEnrolled = profile.Career is null
            && string.Equals(profile.ContractStatus, VanguardOperatorContractStatuses.Uncontracted, StringComparison.OrdinalIgnoreCase);
        if (profile.Career is null && !worldNotYetEnrolled)
        {
            return VanguardOperatorCumulativeMigrationAssessment.UnsupportedState("career_missing_for_non_world_operator");
        }

        if (!IsSupportedVisualSide(profile.Identity.Side))
        {
            return VanguardOperatorCumulativeMigrationAssessment.UnsupportedState("visual_identity_side_unsupported");
        }

        bool visualCurrent = IsCurrentVisualIdentity(profile.VisualIdentity);
        bool enrollmentCurrent = IsCurrentEnrollmentBaseline(profile.EnrollmentBaseline);

        if (worldNotYetEnrolled)
        {
            if (profile.EnrollmentBaseline is not null)
            {
                return VanguardOperatorCumulativeMigrationAssessment.UnsupportedState("world_operator_has_enrollment_baseline");
            }

            return visualCurrent
                ? VanguardOperatorCumulativeMigrationAssessment.CurrentState("world_identity_current")
                : VanguardOperatorCumulativeMigrationAssessment.Pending(
                    "world_visual_identity_required",
                    VanguardOperatorCumulativeMigrationStep.VisualIdentity);
        }

        if (visualCurrent && enrollmentCurrent)
        {
            return VanguardOperatorCumulativeMigrationAssessment.CurrentState("enrolled_identity_and_baseline_current");
        }

        if (!visualCurrent && !enrollmentCurrent)
        {
            return VanguardOperatorCumulativeMigrationAssessment.Pending(
                "visual_then_enrollment_required",
                VanguardOperatorCumulativeMigrationStep.VisualIdentity,
                VanguardOperatorCumulativeMigrationStep.EnrollmentBaseline);
        }

        if (!visualCurrent)
        {
            return VanguardOperatorCumulativeMigrationAssessment.Pending(
                "visual_identity_required",
                VanguardOperatorCumulativeMigrationStep.VisualIdentity);
        }

        return VanguardOperatorCumulativeMigrationAssessment.Pending(
            "enrollment_baseline_required",
            VanguardOperatorCumulativeMigrationStep.EnrollmentBaseline);
    }

    public static bool IsMutationAdmitted(VanguardOperatorProfile profile)
        => Assess(profile).Supported;

    public static bool IsCurrentVisualIdentity(VanguardOperatorVisualIdentity? identity)
        => identity is not null
            && identity.SchemaVersion == VanguardOperatorVisualIdentitySchema.CurrentVersion
            && string.Equals(identity.ResolverPolicyId, VanguardOperatorVisualIdentityService.PolicyId, StringComparison.Ordinal)
            && identity.ResolverPolicyVersion == VanguardOperatorVisualIdentityService.PolicyVersion
            && !string.IsNullOrWhiteSpace(identity.Head)
            && !string.IsNullOrWhiteSpace(identity.Body)
            && !string.IsNullOrWhiteSpace(identity.Feet)
            && !string.IsNullOrWhiteSpace(identity.Hands)
            && !string.IsNullOrWhiteSpace(identity.Voice)
            && !string.IsNullOrWhiteSpace(identity.SeedFingerprint);

    public static bool IsCurrentEnrollmentBaseline(VanguardOperatorEnrollmentBaselineState? baseline)
        => baseline is not null
            && baseline.SchemaVersion == VanguardOperatorEnrollmentBaselineSchema.CurrentVersion
            && string.Equals(baseline.PolicyId, VanguardOperatorEnrollmentBaselineService.PolicyId, StringComparison.Ordinal)
            && baseline.PolicyVersion == VanguardOperatorEnrollmentBaselineService.PolicyVersion
            && baseline.CareerLevel >= 1
            && baseline.MaximumCareerLevel >= baseline.CareerLevel
            && !string.IsNullOrWhiteSpace(baseline.Origin)
            && !string.IsNullOrWhiteSpace(baseline.Archetype)
            && !string.IsNullOrWhiteSpace(baseline.Side)
            && !string.IsNullOrWhiteSpace(baseline.SeedFingerprint)
            && !string.IsNullOrWhiteSpace(baseline.CurveSource)
            && baseline.CurveAuthoritative
            && !string.IsNullOrWhiteSpace(baseline.PhysicalPolicyId);

    public static bool IsCoreNormalizationAdmitted(VanguardOperatorProfile profile)
        => string.IsNullOrWhiteSpace(ResolveUnsupportedPersistentShape(profile));

    private static bool IsSupportedVisualSide(string? side)
        => string.Equals(side, "Bear", StringComparison.OrdinalIgnoreCase)
            || string.Equals(side, "Usec", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveUnsupportedPersistentShape(VanguardOperatorProfile profile)
    {
        string? core = ResolveUnsupportedCoreSchema(profile);
        if (core is not null)
        {
            return core;
        }

        VanguardOperatorVisualIdentity? visual = profile.VisualIdentity;
        if (visual is not null)
        {
            if (visual.SchemaVersion > VanguardOperatorVisualIdentitySchema.CurrentVersion
                || visual.ResolverPolicyVersion > VanguardOperatorVisualIdentityService.PolicyVersion)
            {
                return "visual_identity_newer_than_supported";
            }

            if (!IsCurrentVisualIdentity(visual))
            {
                return "visual_identity_marker_conflicting_or_malformed";
            }
        }

        VanguardOperatorEnrollmentBaselineState? enrollment = profile.EnrollmentBaseline;
        if (enrollment is not null)
        {
            if (enrollment.SchemaVersion > VanguardOperatorEnrollmentBaselineSchema.CurrentVersion
                || enrollment.PolicyVersion > VanguardOperatorEnrollmentBaselineService.PolicyVersion)
            {
                return "enrollment_baseline_newer_than_supported";
            }

            if (!IsCurrentEnrollmentBaseline(enrollment))
            {
                return "enrollment_baseline_marker_conflicting_or_malformed";
            }
        }

        return null;
    }

    private static string? ResolveUnsupportedCoreSchema(VanguardOperatorProfile profile)
    {
        if (!IsSupportedCoreSchema(profile.SchemaVersion)) return "operator_schema_unsupported";
        if (!IsSupportedCoreSchema(profile.Identity.SchemaVersion)) return "identity_schema_unsupported";
        if (!IsSupportedCoreSchema(profile.Persona.SchemaVersion)) return "persona_schema_unsupported";
        if (!IsSupportedCoreSchema(profile.Progression.SchemaVersion)) return "progression_schema_unsupported";

        VanguardOperatorCareer? career = profile.Career;
        if (career is null)
        {
            return null;
        }

        if (!IsSupportedCareerSchema(career.SchemaVersion)) return "career_schema_unsupported";
        if (!IsSupportedCareerSchema(career.Statistics.SchemaVersion)) return "career_statistics_schema_unsupported";
        if (career.ExperienceReconciliation is not null
            && !IsExactSupportedSchema(career.ExperienceReconciliation.SchemaVersion, VanguardOperatorExperienceReconciliationSchema.CurrentVersion))
        {
            return "experience_reconciliation_schema_unsupported";
        }

        if (career.XpCommitState is not null
            && !IsExactSupportedSchema(career.XpCommitState.SchemaVersion, VanguardOperatorCareerXpCommitSchema.CurrentVersion))
        {
            return "career_xp_commit_schema_unsupported";
        }

        return null;
    }

    private static bool IsSupportedCoreSchema(int schemaVersion)
        => schemaVersion >= 1 && schemaVersion <= VanguardOperatorSchema.CurrentVersion;

    private static bool IsSupportedCareerSchema(int schemaVersion)
        => schemaVersion >= 1 && schemaVersion <= VanguardOperatorCareerSchema.CurrentVersion;

    private static bool IsExactSupportedSchema(int schemaVersion, int currentVersion)
        => schemaVersion >= 1 && schemaVersion <= currentVersion;
}

internal enum VanguardOperatorCumulativeMigrationStep
{
    VisualIdentity,
    EnrollmentBaseline,
}

internal sealed record VanguardOperatorCumulativeMigrationAssessment(
    bool Supported,
    bool Current,
    string Reason,
    IReadOnlyList<VanguardOperatorCumulativeMigrationStep> OrderedSteps)
{
    public bool RequiresVisualIdentity => OrderedSteps.Contains(VanguardOperatorCumulativeMigrationStep.VisualIdentity);
    public bool RequiresEnrollmentBaseline => OrderedSteps.Contains(VanguardOperatorCumulativeMigrationStep.EnrollmentBaseline);

    public static VanguardOperatorCumulativeMigrationAssessment Pending(
        string reason,
        params VanguardOperatorCumulativeMigrationStep[] orderedSteps)
        => new(true, false, reason, orderedSteps);

    public static VanguardOperatorCumulativeMigrationAssessment CurrentState(string reason)
        => new(true, true, reason, Array.Empty<VanguardOperatorCumulativeMigrationStep>());

    public static VanguardOperatorCumulativeMigrationAssessment UnsupportedState(string reason)
        => new(false, false, reason, Array.Empty<VanguardOperatorCumulativeMigrationStep>());
}
