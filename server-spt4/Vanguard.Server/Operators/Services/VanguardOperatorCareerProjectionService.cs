using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Raid.Persistence.Models;
using Vanguard.Server.Operators.Raid.Persistence.Services;
using Vanguard.Server.Operators.Responses;
using Vanguard.Server.Diagnostics;

// Responsibility: Coordinates Operator Career Projection Service for the Operator domain services, delegating specialized work to its collaborators.
// Flow: Caller/route input is validated and normalized, canonical Operator/profile state is read or updated through the owning store/integration, then a response and diagnostics are produced.
// Authority boundary: Server domain orchestration only; persistent truth remains explicit in the Operator/SPT stores and client in-raid execution remains separate.
// Invariant: Operations stay profile-scoped, deterministic/idempotent where required, and partial failures do not silently corrupt canonical state.
namespace Vanguard.Server.Operators.Services;

/// <summary>
/// Read-only Career aggregate projection over the canonical verified ledger snapshot.
/// Ledger admission is centralized in VanguardCareerRaidLedgerVerificationService so this service
/// no longer owns a second implementation of schema/identity/semantic/fingerprint validation.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorCareerProjectionService(
    VanguardCareerRaidLedgerVerificationService verificationService,
    ISptLogger<VanguardOperatorCareerProjectionService> logger)
{
    public const string StatusTag = "VANGUARD_CAREER_SOURCE_PRESENTATION_STATUS";

    public async Task<VanguardCareerProjectionReadModel> BuildAsync(
        string storageProfileId,
        IReadOnlyList<VanguardOperatorProfile> operators)
    {
        VanguardCareerRaidLedgerVerificationSnapshot verification = await verificationService.ReadAsync(storageProfileId);
        return BuildFromVerifiedLedger(storageProfileId, operators, verification);
    }

    public VanguardCareerProjectionReadModel BuildFromVerifiedLedger(
        string storageProfileId,
        IReadOnlyList<VanguardOperatorProfile> operators,
        VanguardCareerRaidLedgerVerificationSnapshot verification)
    {
        var operatorIds = new HashSet<string>(
            operators.Where(value => !string.IsNullOrWhiteSpace(value.OperatorId)).Select(value => Normalize(value.OperatorId)),
            StringComparer.OrdinalIgnoreCase);
        int unprojectedVerifiedEntryCount = verification.VerifiedEntries.Count(entry => !operatorIds.Contains(Normalize(entry.OperatorId)));

        VanguardOperatorCareerProjection[] projections = operators
            .Where(profile => !string.IsNullOrWhiteSpace(profile.OperatorId))
            .GroupBy(profile => Normalize(profile.OperatorId), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(profile => profile.Identity.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.OperatorId, StringComparer.OrdinalIgnoreCase)
            .Select(profile => BuildOperatorProjection(profile, verification.SourceEntries, verification.VerifiedEntries))
            .ToArray();

        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] owner={Safe(storageProfileId)}; coverage={verification.CoverageState}; ledgerRead={verification.LedgerReadState}; sourceEntries={verification.SourceEntryCount}; verifiedEntries={verification.VerifiedEntryCount}; rejectedEntries={verification.RejectedEntryCount}; duplicates={verification.DuplicateEntryCount}; unsupported={verification.UnsupportedEntryCount}; integrityRejected={verification.IntegrityRejectedEntryCount}; semanticRejected={verification.SemanticRejectedEntryCount}; ownerMismatch={verification.OwnerMismatchEntryCount}; unprojectedVerified={unprojectedVerifiedEntryCount}; operators={projections.Length}; sharedVerifiedLedgerAdmission=true; xpMutation=false; legacyCareerMutation=false; achievementsMutation=false; personaEvidenceMutation=false; sainProjectionChanged=false; tag={StatusTag}"));

        return new VanguardCareerProjectionReadModel(
            VanguardCareerProjectionSchema.ProjectionVersion,
            VanguardCareerProjectionSchema.CoverageBoundary,
            VanguardCareerProjectionSchema.CombatMethodCoverageState,
            verification.CoverageState,
            verification.LedgerReadState,
            verification.ActiveLedgerFilePresent,
            verification.QuarantineEvidencePresent,
            VanguardCareerRaidLedgerSchema.CurrentVersion,
            VanguardCareerRaidLedgerSchema.TruthVersion,
            verification.SourceEntryCount,
            verification.VerifiedEntryCount,
            verification.RejectedEntryCount,
            verification.DuplicateEntryCount,
            verification.UnsupportedEntryCount,
            verification.IntegrityRejectedEntryCount,
            verification.SemanticRejectedEntryCount,
            verification.OwnerMismatchEntryCount,
            unprojectedVerifiedEntryCount,
            projections);
    }

    private static VanguardOperatorCareerProjection BuildOperatorProjection(
        VanguardOperatorProfile profile,
        IReadOnlyList<VanguardCareerRaidLedgerEntry> source,
        IReadOnlyList<VanguardCareerRaidLedgerEntry> accepted)
    {
        string operatorId = Normalize(profile.OperatorId);
        VanguardCareerRaidLedgerEntry[] sourceForOperator = source
            .Where(entry => string.Equals(Normalize(entry.OperatorId), operatorId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        VanguardCareerRaidLedgerEntry[] verified = accepted
            .Where(entry => string.Equals(Normalize(entry.OperatorId), operatorId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => Normalize(entry.RaidSessionId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => Normalize(entry.LedgerEntryId), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        VanguardCareerRaidLedgerKillEvent[] verifiedExternalKills = verified
            .SelectMany(entry => entry.Kills.Where(kill => !VanguardCareerRaidLedgerVerificationService.IsSelfKill(entry, kill)))
            .ToArray();
        int selfInflictedDeaths = verified.Count(entry =>
            entry.Participated
            && entry.Died
            && entry.Death is not null
            && VanguardCareerRaidLedgerVerificationService.IsSelfDeath(entry, entry.Death));

        IReadOnlyList<VanguardCareerNamedCombatantProjection> confirmedVictims = BuildConfirmedVictims(verifiedExternalKills);
        IReadOnlyList<VanguardCareerDeathSourceProjection> confirmedDeathSources = BuildConfirmedDeathSources(verified);

        IReadOnlyDictionary<string, int> killRoles = verifiedExternalKills
            .GroupBy(kill => Normalize(kill.TargetRawRole, "unknown"), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, int> deathRoles = verified
            .Where(entry => entry.Died && entry.Death is not null && !VanguardCareerRaidLedgerVerificationService.IsSelfDeath(entry, entry.Death))
            .Select(entry => entry.Death!)
            .GroupBy(death => Normalize(death.KillerRawRole, "unknown"), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, double> skillPoints = verified
            .SelectMany(entry => entry.SkillSessionPoints)
            .GroupBy(skill => Normalize(skill.SkillId), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(skill => skill.PointsEarnedDuringSession),
                StringComparer.OrdinalIgnoreCase);

        int verifiedRaidCount = verified
            .Where(entry => entry.Participated)
            .Select(entry => Normalize(entry.RaidSessionId))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        int survived = verified.Count(entry => entry.Participated && entry.AliveAtRaidEnd && !entry.Died);
        int kia = verified.Count(entry => entry.Participated && entry.Died && !entry.AliveAtRaidEnd);
        int kills = verifiedExternalKills.Length;
        double totalSkillPoints = skillPoints.Values.Sum();

        return new VanguardOperatorCareerProjection(
            operatorId,
            profile.Identity.DisplayName,
            sourceForOperator.Length,
            verified.Length,
            Math.Max(0, sourceForOperator.Length - verified.Length),
            verifiedRaidCount,
            survived,
            kia,
            selfInflictedDeaths,
            kills,
            confirmedVictims,
            confirmedDeathSources,
            killRoles,
            deathRoles,
            totalSkillPoints,
            skillPoints);
    }

    private static IReadOnlyList<VanguardCareerNamedCombatantProjection> BuildConfirmedVictims(
        IReadOnlyList<VanguardCareerRaidLedgerKillEvent> kills)
        => kills
            .GroupBy(
                kill => CombatantKey(kill.TargetName, kill.TargetSide, kill.TargetRawRole),
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                VanguardCareerRaidLedgerKillEvent first = group.First();
                return new VanguardCareerNamedCombatantProjection(
                    VanguardCareerCombatantPresentation.ResolveDisplayName(first.TargetName, first.TargetSide, first.TargetRawRole),
                    Normalize(first.TargetSide, "unknown"),
                    Normalize(first.TargetRawRole, "unknown"),
                    group.Count());
            })
            .OrderByDescending(value => value.Count)
            .ThenBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.RawRole, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<VanguardCareerDeathSourceProjection> BuildConfirmedDeathSources(
        IReadOnlyList<VanguardCareerRaidLedgerEntry> entries)
        => entries
            .Where(entry => entry.Died && entry.Death is not null)
            .Select(entry => new
            {
                Entry = entry,
                Death = entry.Death!,
                SelfInflicted = VanguardCareerRaidLedgerVerificationService.IsSelfDeath(entry, entry.Death!)
            })
            .GroupBy(
                value => value.SelfInflicted
                    ? "self_inflicted"
                    : CombatantKey(value.Death.KillerName, value.Death.KillerSide, value.Death.KillerRawRole),
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new VanguardCareerDeathSourceProjection(
                    first.SelfInflicted
                        ? "Auto-infligée"
                        : VanguardCareerCombatantPresentation.ResolveDisplayName(first.Death.KillerName, first.Death.KillerSide, first.Death.KillerRawRole),
                    first.SelfInflicted ? "self" : Normalize(first.Death.KillerSide, "unknown"),
                    first.SelfInflicted ? "self_inflicted" : Normalize(first.Death.KillerRawRole, "unknown"),
                    first.SelfInflicted,
                    group.Count());
            })
            .OrderByDescending(value => value.Count)
            .ThenBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string CombatantKey(string? name, string? side, string? rawRole)
        => Normalize(name, "none") + "|" + Normalize(side, "none") + "|" + Normalize(rawRole, "none");

    private static string Normalize(string? value, string fallback = "")
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(';', '_').Replace('\n', '_').Replace('\r', '_');
}
