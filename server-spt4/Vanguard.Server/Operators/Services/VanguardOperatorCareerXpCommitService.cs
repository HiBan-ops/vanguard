using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Raid.Persistence.Models;
using Vanguard.Server.Operators.Raid.Persistence.Services;
using Vanguard.Server.Operators.Storage;

using Vanguard.Server.Diagnostics;

// Responsibility: Applies only verified forward-only Career XP credits to Operator progression and records enough commit evidence to make retries safe.
// Flow: The service reads the verified raid ledger, derives unapplied credit tokens, resolves the EFT level curve, updates Operator XP/level under a profile lock, then persists the applied-token ledger.
// Authority boundary: Only verified Career-ledger kill-credit components may mutate progression here; full session XP, client estimates and unverified history are never promoted to truth.
// Invariant: A credit token can be committed at most once, pre-activation history is never back-paid, and persistence failure must not leave an untracked XP mutation.
namespace Vanguard.Server.Operators.Services;

/// <summary>
/// Forward-only Career XP commit. The only mutable XP in this policy is the exact,
/// verified kill-credit subtotal already persisted in the canonical Career raid ledger.
/// Full EFT TotalSessionExperience is deliberately not claimed.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorCareerXpCommitService(
    VanguardOperatorStore store,
    VanguardCareerRaidLedgerVerificationService ledgerVerificationService,
    VanguardEftExperienceCurveService experienceCurve,
    ISptLogger<VanguardOperatorCareerXpCommitService> logger)
{
    public const string StatusTag = VanguardBuildVersion.CareerXpCommitAndLevelProgressionStatusTag;

    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<VanguardOperatorCareerXpSyncResult> SynchronizeAsync(
        string storageProfileId,
        IReadOnlyList<VanguardOperatorProfile>? knownOperators = null,
        VanguardCareerRaidLedgerVerificationSnapshot? knownVerification = null)
    {
        await gate.WaitAsync();
        try
        {
            IReadOnlyList<VanguardOperatorProfile> before = knownOperators ?? await store.LoadOperatorsAsync(storageProfileId);
            VanguardCareerRaidLedgerVerificationSnapshot verification = knownVerification
                ?? await ledgerVerificationService.ReadAsync(storageProfileId);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var after = before.ToArray();
            bool changed = false;
            int activated = 0;
            int appliedCredits = 0;
            int deferredCredits = 0;
            long committedXp = 0;
            int levelUps = 0;

            for (int index = 0; index < after.Length; index++)
            {
                VanguardOperatorProfile profile = after[index];
                VanguardOperatorCareer? career = profile.Career;
                if (career is null || string.IsNullOrWhiteSpace(profile.OperatorId))
                {
                    continue;
                }

                VanguardCareerRaidLedgerEntry[] verifiedEntries = verification.VerifiedEntries
                    .Where(entry => string.Equals(entry.OperatorId, profile.OperatorId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                VanguardOperatorCareerXpCommitState? state = career.XpCommitState;
                if (state is null)
                {
                    if (!CanEstablishHistoricalCutover(verification))
                    {
                        logger.Warning(VanguardServerDiagnosticsLog.Present(
                            $"[{StatusTag}] phase=activation_guard; owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; reason=ledger_coverage_not_safe_for_historical_cutover; coverageState={Safe(verification.CoverageState)}; sourceEntries={verification.SourceEntryCount}; verifiedEntries={verification.VerifiedEntryCount}; rejectedEntries={verification.RejectedEntryCount}; quarantine={Bool(verification.QuarantineEvidencePresent)}; mutation=false; failClosed=true; tag={StatusTag}"));
                        return new VanguardOperatorCareerXpSyncResult(false, false, "ledger_coverage_not_safe_for_historical_cutover", before, activated, 0, deferredCredits, 0, 0);
                    }

                    VanguardCareerRaidLedgerXpKillCredit[] historicalCredits = verifiedEntries
                        .SelectMany(entry => entry.XpKillCredits ?? Array.Empty<VanguardCareerRaidLedgerXpKillCredit>())
                        .Where(IsExactAwardedCredit)
                        .ToArray();
                    long historicalSubtotal = historicalCredits.Sum(value => (long)value.KillXpSubtotal);
                    string[] historicalExcludedTokens = verifiedEntries
                        .SelectMany(entry => (entry.XpKillCredits ?? Array.Empty<VanguardCareerRaidLedgerXpKillCredit>())
                            .Where(IsExactAwardedCredit)
                            .Select(credit => BuildCreditToken(entry, credit)))
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    VanguardOperatorExperienceWindow activationWindow = experienceCurve.ResolveWindow(Math.Max(profile.Progression.Experience, 0));

                    state = new VanguardOperatorCareerXpCommitState(
                        VanguardOperatorCareerXpCommitPolicy.PolicyId,
                        VanguardOperatorCareerXpCommitPolicy.PolicyVersion,
                        VanguardOperatorCareerXpCommitPolicy.ActiveState,
                        now,
                        VanguardOperatorCareerXpCommitPolicy.CoverageBoundary,
                        activationWindow.Source,
                        activationWindow.IsAuthoritative,
                        false,
                        verifiedEntries.Length,
                        historicalCredits.Length,
                        historicalSubtotal,
                        historicalExcludedTokens,
                        0,
                        0,
                        Array.Empty<string>(),
                        string.Empty,
                        null);

                    career = career with
                    {
                        XpCommitState = state,
                        SchemaVersion = VanguardOperatorCareerSchema.CurrentVersion,
                    };
                    after[index] = profile with { Career = career, UpdatedAtUtc = now };
                    activated++;
                    changed = true;

                    logger.Info(VanguardServerDiagnosticsLog.Present(
                        $"[{StatusTag}] phase=activation; owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; policy={VanguardOperatorCareerXpCommitPolicy.PolicyId}; forwardOnly=true; lifetimeCoverage=false; preActivationVerifiedEntries={verifiedEntries.Length}; preActivationAwardedCredits={historicalCredits.Length}; preActivationXpNotCommitted={historicalSubtotal}; preActivationExcludedTokens={historicalExcludedTokens.Length}; historicalRetroactiveAward=false; curveSource={Safe(activationWindow.Source)}; curveAuthoritative={Bool(activationWindow.IsAuthoritative)}; xpMutation=false; levelMutation=false; tag={StatusTag}"));
                    continue;
                }

                if (!IsSupportedPolicyId(state.PolicyId)
                    || state.PolicyVersion != VanguardOperatorCareerXpCommitPolicy.PolicyVersion
                    || !string.Equals(state.State, VanguardOperatorCareerXpCommitPolicy.ActiveState, StringComparison.Ordinal)
                    || !IsSupportedCoverageBoundary(state.CoverageBoundary))
                {
                    logger.Warning(VanguardServerDiagnosticsLog.Present(
                        $"[{StatusTag}] phase=policy_guard; owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; reason=unsupported_persisted_policy; persistedPolicy={Safe(state.PolicyId)}; persistedVersion={state.PolicyVersion}; persistedState={Safe(state.State)}; persistedCoverage={Safe(state.CoverageBoundary)}; mutation=false; failClosed=true; tag={StatusTag}"));
                    return new VanguardOperatorCareerXpSyncResult(false, false, "unsupported_persisted_xp_commit_policy", before, activated, 0, deferredCredits, 0, 0);
                }

                VanguardOperatorExperienceWindow currentWindow = experienceCurve.ResolveWindow(Math.Max(profile.Progression.Experience, 0));
                if (currentWindow.IsAuthoritative
                    && (!state.CurveAuthoritative || !string.Equals(state.CurveSource, currentWindow.Source, StringComparison.Ordinal)))
                {
                    state = state with
                    {
                        CurveSource = currentWindow.Source,
                        CurveAuthoritative = true,
                        SchemaVersion = VanguardOperatorCareerXpCommitSchema.CurrentVersion,
                    };
                    career = career with { XpCommitState = state, SchemaVersion = VanguardOperatorCareerSchema.CurrentVersion };
                    profile = profile with { Career = career, UpdatedAtUtc = now };
                    after[index] = profile;
                    changed = true;
                }

                HashSet<string> applied = (state.AppliedCreditTokens ?? Array.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                HashSet<string> historicalExcluded = (state.PreActivationExcludedCreditTokens ?? Array.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var pending = verifiedEntries
                    .SelectMany(entry => (entry.XpKillCredits ?? Array.Empty<VanguardCareerRaidLedgerXpKillCredit>())
                        .Where(IsExactAwardedCredit)
                        .Select(credit => new PendingCredit(entry, credit, BuildCreditToken(entry, credit))))
                    .Where(value => !applied.Contains(value.Token) && !historicalExcluded.Contains(value.Token))
                    .GroupBy(value => value.Token, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(value => value.Entry.CommittedAtUtc)
                    .ThenBy(value => value.Entry.LedgerEntryId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(value => value.Credit.EventId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (pending.Length == 0)
                {
                    continue;
                }

                long xpGain = pending.Sum(value => (long)value.Credit.KillXpSubtotal);
                if (xpGain <= 0 || xpGain > int.MaxValue - (long)Math.Max(profile.Progression.Experience, 0))
                {
                    deferredCredits += pending.Length;
                    logger.Warning(VanguardServerDiagnosticsLog.Present(
                        $"[{StatusTag}] phase=deferred; owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; reason=xp_range_invalid; pendingCredits={pending.Length}; xpGain={xpGain}; xpBefore={profile.Progression.Experience}; mutation=false; tag={StatusTag}"));
                    continue;
                }

                int xpBefore = Math.Max(profile.Progression.Experience, 0);
                int xpAfter = xpBefore + (int)xpGain;
                VanguardOperatorExperienceWindow window = experienceCurve.ResolveWindow(xpAfter);
                int levelBefore = Math.Max(profile.Progression.Level, 1);
                if (!window.IsAuthoritative || window.Level < levelBefore)
                {
                    deferredCredits += pending.Length;
                    logger.Warning(VanguardServerDiagnosticsLog.Present(
                        $"[{StatusTag}] phase=deferred; owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; reason={(window.IsAuthoritative ? "resolved_level_regression" : "xp_curve_not_authoritative")}; pendingCredits={pending.Length}; xpGain={xpGain}; xpBefore={xpBefore}; xpAfter={xpAfter}; xpSemantics=cumulative_from_level_1; levelBefore={levelBefore}; resolvedLevel={window.Level}; curveSource={Safe(window.Source)}; mutation=false; tag={StatusTag}"));
                    continue;
                }

                long earnedAfter;
                long stateTotalAfter;
                try
                {
                    earnedAfter = checked(career.ExperienceEarnedSinceEnrollment + xpGain);
                    stateTotalAfter = checked(state.TotalCommittedExperience + xpGain);
                }
                catch (OverflowException)
                {
                    deferredCredits += pending.Length;
                    logger.Warning(VanguardServerDiagnosticsLog.Present(
                        $"[{StatusTag}] phase=deferred; owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; reason=career_xp_long_overflow; pendingCredits={pending.Length}; mutation=false; tag={StatusTag}"));
                    continue;
                }

                string[] nextApplied = applied
                    .Concat(pending.Select(value => value.Token))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                string lastRaid = pending
                    .OrderBy(value => value.Entry.CommittedAtUtc)
                    .ThenBy(value => value.Entry.LedgerEntryId, StringComparer.OrdinalIgnoreCase)
                    .Last().Entry.RaidSessionId;

                VanguardOperatorCareerXpCommitState nextState = state with
                {
                    CurveSource = window.Source,
                    CurveAuthoritative = true,
                    AppliedCreditCount = nextApplied.Length,
                    TotalCommittedExperience = stateTotalAfter,
                    AppliedCreditTokens = nextApplied,
                    LastAppliedRaidSessionId = lastRaid,
                    LastAppliedAtUtc = now,
                    SchemaVersion = VanguardOperatorCareerXpCommitSchema.CurrentVersion,
                };
                VanguardOperatorProgression progression = profile.Progression with
                {
                    Level = window.Level,
                    Experience = xpAfter,
                };
                VanguardOperatorCareer nextCareer = career with
                {
                    ExperienceEarnedSinceEnrollment = earnedAfter,
                    XpCommitState = nextState,
                    SchemaVersion = VanguardOperatorCareerSchema.CurrentVersion,
                };
                after[index] = profile with
                {
                    Progression = progression,
                    Career = nextCareer,
                    UpdatedAtUtc = now,
                };

                changed = true;
                appliedCredits += pending.Length;
                committedXp += xpGain;
                levelUps += Math.Max(0, window.Level - levelBefore);

                logger.Info(VanguardServerDiagnosticsLog.Present(
                    $"[{StatusTag}] phase=apply; owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; pendingCredits={pending.Length}; appliedCredits={pending.Length}; xpBefore={xpBefore}; xpGain={xpGain}; xpAfter={xpAfter}; xpSemantics=cumulative_from_level_1; currentLevelXpDerived=true; levelBefore={levelBefore}; levelAfter={window.Level}; levelUps={Math.Max(0, window.Level - levelBefore)}; earnedSinceEnrollmentAfter={earnedAfter}; coverage={VanguardOperatorCareerXpCommitPolicy.CoverageBoundary}; totalSessionExperienceClaimed=false; nonKillXpApplied=false; historicalRetroactiveAward=false; curveSource={Safe(window.Source)}; curveAuthoritative=true; tag={StatusTag}"));
            }

            if (changed)
            {
                VanguardOperatorProfilesAtomicWriteResult write = await store.CommitOperatorProfilesAtomicAsync(storageProfileId, before, after);
                if (!write.Success || !write.ReadBackVerified)
                {
                    logger.Warning(VanguardServerDiagnosticsLog.Present(
                        $"[{StatusTag}] phase=write; owner={Safe(storageProfileId)}; success=false; reason={Safe(write.Reason)}; activated={activated}; appliedCredits={appliedCredits}; xp={committedXp}; levelUps={levelUps}; tag={StatusTag}"));
                    return new VanguardOperatorCareerXpSyncResult(false, false, write.Reason, before, activated, 0, deferredCredits, 0, 0);
                }
            }

            logger.Info(VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] phase=sync; owner={Safe(storageProfileId)}; success=true; changed={Bool(changed)}; activated={activated}; appliedCredits={appliedCredits}; deferredCredits={deferredCredits}; committedXp={committedXp}; levelUps={levelUps}; verifiedEntries={verification.VerifiedEntryCount}; policy={VanguardOperatorCareerXpCommitPolicy.PolicyId}; coverage={VanguardOperatorCareerXpCommitPolicy.CoverageBoundary}; forwardOnly=true; historicalRetroactiveAward=false; atomicOperatorsWrite=true; readback=true; tag={StatusTag}"));
            return new VanguardOperatorCareerXpSyncResult(true, changed, changed ? "career_xp_sync_committed" : "career_xp_sync_no_change", after, activated, appliedCredits, deferredCredits, committedXp, levelUps);
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool CanEstablishHistoricalCutover(VanguardCareerRaidLedgerVerificationSnapshot verification)
        => string.Equals(verification.CoverageState, "verified_committed_entries_only", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verification.CoverageState, "no_committed_entries", StringComparison.OrdinalIgnoreCase);

    private static bool IsExactAwardedCredit(VanguardCareerRaidLedgerXpKillCredit credit)
        => credit.CalculationAvailable
            && credit.Awarded
            && !credit.SameGroup
            && credit.KillXpSubtotal > 0;

    private static string BuildCreditToken(VanguardCareerRaidLedgerEntry entry, VanguardCareerRaidLedgerXpKillCredit credit)
        => Normalize(entry.LedgerEntryId) + "|" + Normalize(credit.EventId);

    private static bool IsSupportedPolicyId(string? value)
    {
        string normalized = Normalize(value);
        return string.Equals(normalized, VanguardOperatorCareerXpCommitPolicy.PolicyId, StringComparison.Ordinal)
            || normalized.EndsWith("verified_eft_kill_xp_forward_only_v1", StringComparison.Ordinal);
    }

    private static bool IsSupportedCoverageBoundary(string? value)
    {
        string normalized = Normalize(value);
        return string.Equals(normalized, VanguardOperatorCareerXpCommitPolicy.CoverageBoundary, StringComparison.Ordinal)
            || normalized.EndsWith("eft_kill_components_only", StringComparison.Ordinal);
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(';', '_').Replace('\n', '_').Replace('\r', '_');
    private static string Bool(bool value) => value ? "true" : "false";

    private sealed record PendingCredit(
        VanguardCareerRaidLedgerEntry Entry,
        VanguardCareerRaidLedgerXpKillCredit Credit,
        string Token);
}

public sealed record VanguardOperatorCareerXpSyncResult(
    bool Success,
    bool Changed,
    string Reason,
    IReadOnlyList<VanguardOperatorProfile> Operators,
    int ActivatedOperatorCount,
    int AppliedCreditCount,
    int DeferredCreditCount,
    long CommittedExperience,
    int LevelUps);
