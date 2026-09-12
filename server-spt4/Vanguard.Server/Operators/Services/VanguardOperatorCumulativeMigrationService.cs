using System.Collections.Concurrent;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using Vanguard.Server.Diagnostics;
using Vanguard.Server.Operators.Models;

// Responsibility: Orchestrates ordered cumulative Operator migration from supported predecessor markers to current persistent truth.
// Flow: A bounded in-memory policy qualification runs once, supported Operators are classified, VisualIdentity convergence runs before EnrollmentBaseline convergence, and the resulting state is reclassified for diagnostics.
// Authority boundary: This service owns migration ordering only; concrete mutation, backup, expected-before, readback, rollback, and EFT profile authority remain in the existing migration/store services.
// Invariant: Enrollment migration is never allowed to bypass a missing VisualIdentity predecessor, replay is marker-idempotent, and any unsupported/newer persistent shape remains untouched.
namespace Vanguard.Server.Operators.Services;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorCumulativeMigrationService(
    VanguardOperatorEnrollmentMigrationService migrationService,
    ISptLogger<VanguardOperatorCumulativeMigrationService> logger)
{
    public const string StatusTag = "VANGUARD_OPERATOR_CUMULATIVE_MIGRATION_STATUS";

    private readonly object qualificationLock = new();
    private readonly ConcurrentDictionary<string, string> lastOwnerFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> lastOwnerPresentationFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private bool? qualificationPassed;

    public async Task<IReadOnlyList<VanguardOperatorProfile>> ConvergeAsync(
        string storageProfileId,
        IReadOnlyList<VanguardOperatorProfile> operators)
    {
        if (operators.Count == 0)
        {
            return operators;
        }

        if (!EnsurePolicyQualification())
        {
            _ = VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] phase=converge; owner={Safe(storageProfileId)}; state=skipped; reason=policy_fixture_qualification_failed; operators={operators.Count}; mutation=false; migrationAuthority=schema_markers; marketingVersionAuthority=false; tag={StatusTag}");
            logger.Error(VanguardServerDiagnosticsLog.Present(
                "[VANGUARD_MIGRATION] Operator migration: FAIL - migration policy self-check did not pass; persistent Operator data was left untouched."));
            return operators;
        }

        ChainSummary before = Summarize(operators);
        LogOwnerState(storageProfileId, "before", before);

        IReadOnlyList<VanguardOperatorProfile> current = await migrationService.MigrateWorldVisualIdentitiesAsync(storageProfileId, operators);
        current = await migrationService.MigrateEnrolledVisualIdentitiesAsync(storageProfileId, current);

        ChainSummary afterVisual = Summarize(current);
        int unexpectedEnrollmentAdvance = CountUnexpectedEnrollmentAdvance(operators, current);
        if (unexpectedEnrollmentAdvance > 0)
        {
            _ = VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] phase=visual_predecessor_gate; owner={Safe(storageProfileId)}; state=failed_closed; unexpectedEnrollmentAdvance={unexpectedEnrollmentAdvance}; action=skip_enrollment_stage; migrationAuthority=schema_markers; marketingVersionAuthority=false; tag={StatusTag}");
            logger.Error(VanguardServerDiagnosticsLog.Present(
                $"[VANGUARD_MIGRATION] Operator migration: FAIL CLOSED - {unexpectedEnrollmentAdvance} invalid migration transition(s) detected; enrollment migration was skipped and persistent data was not guessed."));
            return current;
        }

        LogOwnerState(storageProfileId, "after_visual", afterVisual);
        current = await migrationService.MigrateEnrollmentBaselinesAsync(storageProfileId, current);
        ChainSummary after = Summarize(current);
        LogOwnerState(storageProfileId, "after", after);
        LogReadableMigrationSummary(storageProfileId, before, after);
        return current;
    }

    private bool EnsurePolicyQualification()
    {
        lock (qualificationLock)
        {
            if (qualificationPassed.HasValue)
            {
                return qualificationPassed.Value;
            }

            try
            {
                int checks = RunPolicyFixtures();
                qualificationPassed = true;
                _ = VanguardServerDiagnosticsLog.Present(
                    $"[{StatusTag}] phase=fixture_qualification; state=passed; checks={checks}; policy={VanguardOperatorCumulativeMigrationPolicy.PolicyId}; policyVersion={VanguardOperatorCumulativeMigrationPolicy.PolicyVersion}; productionMutation=false; migrationAuthority=schema_markers; marketingVersionAuthority=false; tag={StatusTag}");
                logger.Success(VanguardServerDiagnosticsLog.Present(
                    $"[VANGUARD_MIGRATION] Migration engine: PASS - cumulative schema/marker policy validated ({checks} checks)."));
            }
            catch (Exception exception)
            {
                qualificationPassed = false;
                _ = VanguardServerDiagnosticsLog.Present(
                    $"[{StatusTag}] phase=fixture_qualification; state=failed; reason={Safe(exception.GetType().Name + "_" + exception.Message)}; productionMutation=false; migrationAuthority=schema_markers; marketingVersionAuthority=false; tag={StatusTag}");
                logger.Error(VanguardServerDiagnosticsLog.Present(
                    "[VANGUARD_MIGRATION] Migration engine: FAIL - cumulative migration policy self-check failed; production data will not be migrated."));
            }

            return qualificationPassed.Value;
        }
    }

    private void LogOwnerState(string storageProfileId, string phase, ChainSummary summary)
    {
        string fingerprint = string.Join('|',
            phase,
            summary.Total,
            summary.Current,
            summary.SupportedPending,
            summary.Unsupported,
            summary.RequiresVisual,
            summary.RequiresEnrollment,
            summary.RequiresBoth,
            summary.UnsupportedReasons);
        string key = storageProfileId + "|" + phase;
        if (lastOwnerFingerprints.TryGetValue(key, out string? previous)
            && string.Equals(previous, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        lastOwnerFingerprints[key] = fingerprint;
        _ = VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] phase={phase}; owner={Safe(storageProfileId)}; state=classified; operators={summary.Total}; current={summary.Current}; supportedPending={summary.SupportedPending}; unsupported={summary.Unsupported}; requiresVisual={summary.RequiresVisual}; requiresEnrollment={summary.RequiresEnrollment}; requiresBoth={summary.RequiresBoth}; unsupportedReasons={Safe(summary.UnsupportedReasons)}; policy={VanguardOperatorCumulativeMigrationPolicy.PolicyId}; policyVersion={VanguardOperatorCumulativeMigrationPolicy.PolicyVersion}; migrationAuthority=schema_markers; marketingVersionAuthority=false; tag={StatusTag}");
    }

    private void LogReadableMigrationSummary(string storageProfileId, ChainSummary before, ChainSummary after)
    {
        string fingerprint = string.Join('|',
            after.Total,
            after.Current,
            after.SupportedPending,
            after.Unsupported,
            after.RequiresVisual,
            after.RequiresEnrollment,
            after.RequiresBoth,
            after.UnsupportedReasons);
        if (lastOwnerPresentationFingerprints.TryGetValue(storageProfileId, out string? previous)
            && string.Equals(previous, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        lastOwnerPresentationFingerprints[storageProfileId] = fingerprint;
        if (after.Unsupported > 0)
        {
            logger.Warning(VanguardServerDiagnosticsLog.Present(
                $"[VANGUARD_MIGRATION] Operator data: WARN - {after.Current}/{after.Total} current; {after.Unsupported} unsupported state(s) left untouched; {after.SupportedPending} supported migration(s) still pending. Details: vanguard-server.log"));
            return;
        }

        if (after.SupportedPending > 0)
        {
            logger.Warning(VanguardServerDiagnosticsLog.Present(
                $"[VANGUARD_MIGRATION] Operator data: DEFERRED - {after.Current}/{after.Total} current; {after.SupportedPending} supported migration(s) remain pending and will be retried safely."));
            return;
        }

        if (before.SupportedPending > 0)
        {
            int converged = Math.Max(after.Current - before.Current, 0);
            logger.Success(VanguardServerDiagnosticsLog.Present(
                $"[VANGUARD_MIGRATION] Operator data: PASS - {converged} Operator(s) converged; {after.Current}/{after.Total} current; migration authority: schema/markers."));
            return;
        }

        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[VANGUARD_MIGRATION] Operator data: CURRENT - {after.Current}/{after.Total} current; migration not required; authority: schema/markers."));
    }

    private static ChainSummary Summarize(IReadOnlyList<VanguardOperatorProfile> operators)
    {
        int current = 0;
        int supportedPending = 0;
        int unsupported = 0;
        int requiresVisual = 0;
        int requiresEnrollment = 0;
        int requiresBoth = 0;
        var unsupportedReasons = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (VanguardOperatorProfile profile in operators)
        {
            VanguardOperatorCumulativeMigrationAssessment assessment = VanguardOperatorCumulativeMigrationPolicy.Assess(profile);
            if (!assessment.Supported)
            {
                unsupported++;
                unsupportedReasons[assessment.Reason] = unsupportedReasons.TryGetValue(assessment.Reason, out int count) ? count + 1 : 1;
                continue;
            }

            if (assessment.Current)
            {
                current++;
                continue;
            }

            supportedPending++;
            if (assessment.RequiresVisualIdentity) requiresVisual++;
            if (assessment.RequiresEnrollmentBaseline) requiresEnrollment++;
            if (assessment.RequiresVisualIdentity && assessment.RequiresEnrollmentBaseline) requiresBoth++;
        }

        string reasons = unsupportedReasons.Count == 0
            ? "none"
            : string.Join(',', unsupportedReasons.Select(pair => pair.Key + ":" + pair.Value));
        return new ChainSummary(
            operators.Count,
            current,
            supportedPending,
            unsupported,
            requiresVisual,
            requiresEnrollment,
            requiresBoth,
            reasons);
    }

    private static int CountUnexpectedEnrollmentAdvance(
        IReadOnlyList<VanguardOperatorProfile> before,
        IReadOnlyList<VanguardOperatorProfile> afterVisual)
    {
        var beforeById = before.ToDictionary(profile => profile.OperatorId, StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (VanguardOperatorProfile after in afterVisual)
        {
            if (!beforeById.TryGetValue(after.OperatorId, out VanguardOperatorProfile? original))
            {
                continue;
            }

            VanguardOperatorCumulativeMigrationAssessment originalAssessment = VanguardOperatorCumulativeMigrationPolicy.Assess(original);
            if (!originalAssessment.Supported
                || !originalAssessment.RequiresVisualIdentity
                || !originalAssessment.RequiresEnrollmentBaseline)
            {
                continue;
            }

            if (VanguardOperatorCumulativeMigrationPolicy.IsCurrentEnrollmentBaseline(after.EnrollmentBaseline)
                && !VanguardOperatorCumulativeMigrationPolicy.IsCurrentVisualIdentity(after.VisualIdentity))
            {
                count++;
            }
        }

        return count;
    }

    private static int RunPolicyFixtures()
    {
        int checks = 0;
        VanguardOperatorProfile legacy = CreateEnrolledFixture("legacy", visual: null, enrollment: null);
        AssertPlan(legacy, ref checks,
            VanguardOperatorCumulativeMigrationStep.VisualIdentity,
            VanguardOperatorCumulativeMigrationStep.EnrollmentBaseline);

        VanguardOperatorProfile afterVisual = legacy with { VisualIdentity = CreateVisualFixture() };
        AssertPlan(afterVisual, ref checks, VanguardOperatorCumulativeMigrationStep.EnrollmentBaseline);
        AssertPlan(afterVisual with { VisualIdentity = CreateVisualFixture() }, ref checks, VanguardOperatorCumulativeMigrationStep.EnrollmentBaseline);

        VanguardOperatorProfile afterEnrollment = afterVisual with { EnrollmentBaseline = CreateEnrollmentFixture() };
        AssertCurrent(afterEnrollment, ref checks);
        AssertCurrent(afterEnrollment with { EnrollmentBaseline = CreateEnrollmentFixture() }, ref checks);

        VanguardOperatorProfile enrollmentFirst = legacy with { EnrollmentBaseline = CreateEnrollmentFixture() };
        AssertPlan(enrollmentFirst, ref checks, VanguardOperatorCumulativeMigrationStep.VisualIdentity);
        AssertCurrent(enrollmentFirst with { VisualIdentity = CreateVisualFixture() }, ref checks);

        checks++;
        if (CountUnexpectedEnrollmentAdvance(
                new[] { legacy },
                new[] { legacy with { EnrollmentBaseline = CreateEnrollmentFixture() } }) != 1)
        {
            throw new InvalidOperationException("unexpected_enrollment_advance_not_detected");
        }

        checks++;
        if (CountUnexpectedEnrollmentAdvance(new[] { enrollmentFirst }, new[] { enrollmentFirst }) != 0)
        {
            throw new InvalidOperationException("historical_enrollment_first_state_misclassified_as_new_bypass");
        }

        VanguardOperatorProfile world = CreateWorldFixture("world", visual: null);
        AssertPlan(world, ref checks, VanguardOperatorCumulativeMigrationStep.VisualIdentity);
        AssertCurrent(world with { VisualIdentity = CreateVisualFixture() }, ref checks);

        VanguardOperatorProfile futureVisual = legacy with
        {
            VisualIdentity = CreateVisualFixture() with { SchemaVersion = VanguardOperatorVisualIdentitySchema.CurrentVersion + 1 },
        };
        AssertUnsupported(futureVisual, ref checks, "visual_identity_newer_than_supported");

        VanguardOperatorProfile futureEnrollment = afterVisual with
        {
            EnrollmentBaseline = CreateEnrollmentFixture() with { PolicyVersion = VanguardOperatorEnrollmentBaselineService.PolicyVersion + 1 },
        };
        AssertUnsupported(futureEnrollment, ref checks, "enrollment_baseline_newer_than_supported");

        VanguardOperatorProfile futureCore = legacy with { SchemaVersion = VanguardOperatorSchema.CurrentVersion + 1 };
        AssertUnsupported(futureCore, ref checks, "operator_schema_unsupported");

        VanguardOperatorProfile conflictingVisual = legacy with
        {
            VisualIdentity = CreateVisualFixture() with { ResolverPolicyId = "unknown.visual.policy" },
        };
        AssertUnsupported(conflictingVisual, ref checks, "visual_identity_marker_conflicting_or_malformed");

        VanguardOperatorProfile invalidWorld = world with { EnrollmentBaseline = CreateEnrollmentFixture() };
        AssertUnsupported(invalidWorld, ref checks, "world_operator_has_enrollment_baseline");

        VanguardOperatorProfile unsupportedSide = legacy with
        {
            Identity = legacy.Identity with { Side = "Scav" },
        };
        AssertUnsupported(unsupportedSide, ref checks, "visual_identity_side_unsupported");

        VanguardOperatorProfile nonAuthoritativeBaseline = afterVisual with
        {
            EnrollmentBaseline = CreateEnrollmentFixture() with { CurveAuthoritative = false },
        };
        AssertUnsupported(nonAuthoritativeBaseline, ref checks, "enrollment_baseline_marker_conflicting_or_malformed");
        return checks;
    }

    private static void AssertPlan(
        VanguardOperatorProfile profile,
        ref int checks,
        params VanguardOperatorCumulativeMigrationStep[] expected)
    {
        VanguardOperatorCumulativeMigrationAssessment assessment = VanguardOperatorCumulativeMigrationPolicy.Assess(profile);
        checks++;
        if (!assessment.Supported || assessment.Current || !assessment.OrderedSteps.SequenceEqual(expected))
        {
            throw new InvalidOperationException("ordered_plan_mismatch_" + assessment.Reason);
        }
    }

    private static void AssertCurrent(VanguardOperatorProfile profile, ref int checks)
    {
        VanguardOperatorCumulativeMigrationAssessment assessment = VanguardOperatorCumulativeMigrationPolicy.Assess(profile);
        checks++;
        if (!assessment.Supported || !assessment.Current || assessment.OrderedSteps.Count != 0)
        {
            throw new InvalidOperationException("current_state_mismatch_" + assessment.Reason);
        }
    }

    private static void AssertUnsupported(VanguardOperatorProfile profile, ref int checks, string expectedReason)
    {
        VanguardOperatorCumulativeMigrationAssessment assessment = VanguardOperatorCumulativeMigrationPolicy.Assess(profile);
        checks++;
        if (assessment.Supported
            || assessment.OrderedSteps.Count != 0
            || !string.Equals(assessment.Reason, expectedReason, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("unsupported_state_mismatch_" + assessment.Reason);
        }
    }

    private static VanguardOperatorProfile CreateEnrolledFixture(
        string operatorId,
        VanguardOperatorVisualIdentity? visual,
        VanguardOperatorEnrollmentBaselineState? enrollment)
    {
        DateTimeOffset when = DateTimeOffset.UnixEpoch;
        var progression = new VanguardOperatorProgression(10, 1000, 1, 1, 0, 0, 0, 0, 0, 0);
        var profile = new VanguardOperatorProfile(
            operatorId,
            CreateIdentityFixture(operatorId),
            "Assault",
            "Rifleman",
            VanguardOperatorContractStatuses.Contracted,
            "ActiveService",
            1000,
            10000,
            "5449016a4bdc2d6f028b456f",
            CreatePersonaFixture(),
            progression,
            when,
            when,
            Career: null,
            EnrollmentBaseline: enrollment,
            VisualIdentity: visual);
        return profile with { Career = VanguardOperatorCareer.MigratedLegacy(profile, when) };
    }

    private static VanguardOperatorProfile CreateWorldFixture(string operatorId, VanguardOperatorVisualIdentity? visual)
    {
        DateTimeOffset when = DateTimeOffset.UnixEpoch;
        return new VanguardOperatorProfile(
            operatorId,
            CreateIdentityFixture(operatorId),
            "Assault",
            "Rifleman",
            VanguardOperatorContractStatuses.Uncontracted,
            "WorldAvailable",
            1000,
            10000,
            "5449016a4bdc2d6f028b456f",
            CreatePersonaFixture(),
            new VanguardOperatorProgression(10, 1000, 0, 0, 0, 0, 0, 0, 0, 0),
            when,
            when,
            Career: null,
            EnrollmentBaseline: null,
            VisualIdentity: visual);
    }

    private static VanguardOperatorIdentity CreateIdentityFixture(string operatorId)
        => new(operatorId, "Fixture", "Operator", "Fixture", "Fixture", "Usec", "en", "default");

    private static VanguardOperatorPersona CreatePersonaFixture()
        => new("default", "default", "calm", "default", "default", Array.Empty<string>(), "fixture");

    private static VanguardOperatorVisualIdentity CreateVisualFixture()
        => new(
            "head-fixture",
            "body-fixture",
            "feet-fixture",
            "hands-fixture",
            "voice-fixture",
            VanguardOperatorVisualIdentityService.PolicyId,
            VanguardOperatorVisualIdentityService.PolicyVersion,
            "usec",
            "FIXTURE-VISUAL-SEED",
            DateTimeOffset.UnixEpoch);

    private static VanguardOperatorEnrollmentBaselineState CreateEnrollmentFixture()
        => new(
            VanguardOperatorEnrollmentBaselineService.PolicyId,
            VanguardOperatorEnrollmentBaselineService.PolicyVersion,
            VanguardOperatorEnrollmentBaselineService.LegacyAdditiveMigrationOrigin,
            10,
            79,
            "Assault",
            "Usec",
            "FIXTURE-ENROLLMENT-SEED",
            "fixture-curve",
            true,
            VanguardOperatorEnrollmentBaselineService.LegacyPhysicalPreservationPolicyId,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Replace(';', '_').Replace('\r', ' ').Replace('\n', ' ');

    private sealed record ChainSummary(
        int Total,
        int Current,
        int SupportedPending,
        int Unsupported,
        int RequiresVisual,
        int RequiresEnrollment,
        int RequiresBoth,
        string UnsupportedReasons);
}
