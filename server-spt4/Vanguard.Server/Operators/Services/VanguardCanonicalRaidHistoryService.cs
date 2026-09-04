using System.Globalization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Raid.Persistence.Models;
using Vanguard.Server.Operators.Raid.Persistence.Services;
using Vanguard.Server.Operators.Responses;
using Vanguard.Server.Diagnostics;

// Responsibility: projects player-facing raid history from the verified career ledger without mutating the ledger or legacy profile fields.
// Flow: A verified ledger snapshot is grouped by raid, enriched only with separately verified death facts, then projected into the read-only history shown to the player.
// Authority boundary: only verified persisted facts are projected; absent map/time/combat-method facts are reported as unavailable rather than invented.
// Invariant: parity compares the career projection and raid-history projection over the same verified snapshot.

namespace Vanguard.Server.Operators.Services;

/// <summary>
/// Reconstructible per-raid read model over the same verified ledger snapshot used by the Career projection.
/// KIA entries are enriched with separately fingerprinted terminal-death truth when observed.
/// Direct killer identity still comes only from the independent independent BotEventHandler.Kill event; LastAggressor is context only.
/// The read model never mutates legacy Career, XP, achievements, PersonaEvidence or raw ledger facts.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class VanguardCanonicalRaidHistoryService(ISptLogger<VanguardCanonicalRaidHistoryService> logger)
{
    public const string StatusTag = "VANGUARD_CANONICAL_RAID_HISTORY_READ_MODEL_FOUNDATION_STATUS";
    private const double SkillPointParityTolerance = 1e-9;

    public VanguardCanonicalRaidHistoryReadModel Build(
        string storageProfileId,
        IReadOnlyList<VanguardOperatorProfile> operators,
        VanguardCareerRaidLedgerVerificationSnapshot verification,
        VanguardCareerProjectionReadModel careerProjection)
    {
        var operatorIds = new HashSet<string>(
            operators.Where(value => !string.IsNullOrWhiteSpace(value.OperatorId)).Select(value => Normalize(value.OperatorId)),
            StringComparer.OrdinalIgnoreCase);
        int unprojectedVerifiedEntryCount = verification.VerifiedEntries.Count(entry => !operatorIds.Contains(Normalize(entry.OperatorId)));

        VanguardOperatorCanonicalRaidHistory[] histories = operators
            .Where(profile => !string.IsNullOrWhiteSpace(profile.OperatorId))
            .GroupBy(profile => Normalize(profile.OperatorId), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(profile => profile.Identity.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.OperatorId, StringComparer.OrdinalIgnoreCase)
            .Select(profile => BuildOperatorHistory(profile, verification))
            .ToArray();

        VanguardCanonicalRaidHistoryParityCheck parity = BuildParity(verification, histories, careerProjection);

        int terminalTruthCount = histories.Sum(value => value.Raids.Count(raid => raid.TerminalDeathTruth is not null));
        int contextualAggressorCount = histories.Sum(value => value.Raids.Count(raid => !string.IsNullOrWhiteSpace(raid.TerminalDeathTruth?.LastAggressorProfileId)));
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] owner={Safe(storageProfileId)}; coverage={verification.CoverageState}; ledgerRead={verification.LedgerReadState}; sourceEntries={verification.SourceEntryCount}; verifiedEntries={verification.VerifiedEntryCount}; rejectedEntries={verification.RejectedEntryCount}; operators={histories.Length}; raidHistoryEntries={histories.Sum(value => value.Raids.Count)}; terminalTruthEntries={terminalTruthCount}; contextualLastAggressors={contextualAggressorCount}; careerParity={Bool(parity.IsMatch)}; parityMismatches={parity.MismatchCount}; directKillerAuthority=BotEventHandler.Kill_only; terminalDeathAuthority=Player.OnPlayerDeadStatic_plus_LastDamageType; lastAggressorSemantics=context_only_not_direct_killer; lastAggressorPromotedToKiller=false; ledgerSourceFingerprintChanged=false; terminalExtensionFingerprint=true; ordering=newest_first_ledger_commit_utc; mapInvented=false; startTimeInvented=false; careerXpMutation=false; legacyCareerMutation=false; achievementsMutation=false; personaEvidenceMutation=false; sainProjectionChanged=false; ledgerMutation=false; tag={StatusTag}"));

        return new VanguardCanonicalRaidHistoryReadModel(
            VanguardCanonicalRaidHistorySchema.ProjectionVersion,
            VanguardCanonicalRaidHistorySchema.CoverageBoundary,
            verification.CoverageState,
            verification.LedgerReadState,
            VanguardCanonicalRaidHistorySchema.RaidOrderingState,
            VanguardCanonicalRaidHistorySchema.TimestampSemantics,
            VanguardCanonicalRaidHistorySchema.LocationCoverageState,
            VanguardCanonicalRaidHistorySchema.StartTimeCoverageState,
            VanguardCanonicalRaidHistorySchema.CareerXpCoverageState,
            VanguardCanonicalRaidHistorySchema.CombatMethodCoverageState,
            VanguardCanonicalRaidHistorySchema.TerminalDeathTruthCoverageState,
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
            histories,
            parity);
    }

    private static VanguardOperatorCanonicalRaidHistory BuildOperatorHistory(
        VanguardOperatorProfile profile,
        VanguardCareerRaidLedgerVerificationSnapshot verification)
    {
        string operatorId = Normalize(profile.OperatorId);
        VanguardCareerRaidLedgerEntry[] source = verification.SourceEntries
            .Where(entry => string.Equals(Normalize(entry.OperatorId), operatorId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        // RaidSessionId is an identity key, not a clock. Order by persisted ledger commit time so "last raid"
        // has a defensible meaning. Exit-boundary observation and ledger id only break ties deterministically;
        // none of these values is promoted to an authoritative raid-start time.
        VanguardCareerRaidLedgerEntry[] verified = verification.VerifiedEntries
            .Where(entry => string.Equals(Normalize(entry.OperatorId), operatorId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.CommittedAtUtc)
            .ThenByDescending(entry => entry.ExitBoundaryObservedAtUtc)
            .ThenBy(entry => Normalize(entry.LedgerEntryId), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        VanguardCanonicalRaidHistoryEntry[] raids = verified.Select(BuildRaidHistoryEntry).ToArray();
        return new VanguardOperatorCanonicalRaidHistory(
            operatorId,
            profile.Identity.DisplayName,
            source.Length,
            verified.Length,
            Math.Max(0, source.Length - verified.Length),
            raids);
    }

    private static VanguardCanonicalRaidHistoryEntry BuildRaidHistoryEntry(VanguardCareerRaidLedgerEntry entry)
    {
        VanguardCanonicalRaidHistoryKill[] kills = entry.Kills
            .Where(kill => !VanguardCareerRaidLedgerVerificationService.IsSelfKill(entry, kill))
            .OrderBy(kill => kill.Ordinal)
            .ThenBy(kill => Normalize(kill.EventId), StringComparer.OrdinalIgnoreCase)
            .Select(kill => new VanguardCanonicalRaidHistoryKill(
                kill.EventId,
                kill.Ordinal,
                kill.ObservedAtUtc,
                kill.TargetProfileId,
                kill.TargetAccountId,
                VanguardCareerCombatantPresentation.ResolveDisplayName(kill.TargetName, kill.TargetSide, kill.TargetRawRole),
                kill.TargetSide,
                kill.TargetRawRole))
            .ToArray();

        VanguardCanonicalRaidHistoryDeath? death = null;
        string deathSourceCoverage = entry.Died ? "death_source_not_observed" : "not_applicable_operator_survived";
        if (entry.Death is not null)
        {
            bool selfInflicted = VanguardCareerRaidLedgerVerificationService.IsSelfDeath(entry, entry.Death);
            death = new VanguardCanonicalRaidHistoryDeath(
                entry.Death.EventId,
                entry.Death.Ordinal,
                entry.Death.ObservedAtUtc,
                entry.Death.KillerProfileId,
                entry.Death.KillerAccountId,
                selfInflicted
                    ? "Auto-infligée"
                    : VanguardCareerCombatantPresentation.ResolveDisplayName(entry.Death.KillerName, entry.Death.KillerSide, entry.Death.KillerRawRole),
                entry.Death.KillerSide,
                entry.Death.KillerRawRole,
                selfInflicted);
            deathSourceCoverage = selfInflicted ? "verified_self_inflicted_identity" : "verified_killer_identity";
        }

        VanguardCanonicalRaidHistoryTerminalDeathTruth? terminalDeathTruth = BuildTerminalDeathTruth(entry, death);
        if (entry.Died && terminalDeathTruth is not null)
        {
            if (death is not null)
            {
                deathSourceCoverage = death.SelfInflicted
                    ? "verified_self_inflicted_identity_and_terminal_truth"
                    : "verified_direct_killer_and_terminal_truth";
            }
            else if (HasLastAggressorContext(terminalDeathTruth))
            {
                deathSourceCoverage = "verified_terminal_mechanism_last_aggressor_context_only_no_direct_killer";
            }
            else
            {
                deathSourceCoverage = "verified_terminal_mechanism_no_direct_killer";
            }
        }
        else if (entry.Died && death is not null)
        {
            deathSourceCoverage = death.SelfInflicted
                ? "verified_self_inflicted_identity_terminal_truth_not_available"
                : "verified_direct_killer_identity_terminal_truth_not_available";
        }

        VanguardCanonicalRaidHistorySkillPoint[] skillPoints = entry.SkillSessionPoints
            .OrderBy(skill => Normalize(skill.SkillId), StringComparer.OrdinalIgnoreCase)
            .Select(skill => new VanguardCanonicalRaidHistorySkillPoint(
                skill.SkillId,
                skill.Progress,
                skill.PointsEarnedDuringSession))
            .ToArray();

        return new VanguardCanonicalRaidHistoryEntry(
            BuildHistoryEventId(entry),
            entry.LedgerEntryId,
            entry.RaidSessionId,
            entry.OwnerProfileId,
            entry.OperatorId,
            entry.BotProfileId,
            entry.Participated,
            entry.AliveAtRaidEnd,
            entry.Died,
            entry.Died ? "kia" : "survived",
            entry.RaidExitStatus,
            entry.RaidExitName,
            entry.ExitBoundarySource,
            entry.ExitBoundaryProfileId,
            entry.ExitBoundaryDelay,
            entry.ExitBoundaryObservedAtUtc,
            entry.CommittedAtUtc,
            kills,
            death,
            terminalDeathTruth,
            skillPoints,
            // Schema v3 reserves a structured extension point, but the current schema has no qualified producer for
            // rescue/medical/notable-combat observations yet. Empty is intentional: Vanguard never invents
            // narrative truth merely because the presentation contract can carry it.
            Array.Empty<VanguardCanonicalRaidHistoryNotableEvent>(),
            deathSourceCoverage,
            entry.SourceFingerprint,
            entry.TerminalDeathTruthFingerprint);
    }

    private static VanguardCanonicalRaidHistoryTerminalDeathTruth? BuildTerminalDeathTruth(
        VanguardCareerRaidLedgerEntry entry,
        VanguardCanonicalRaidHistoryDeath? directDeath)
    {
        if (entry.TerminalDeathTruth is null)
        {
            return null;
        }

        VanguardCareerRaidLedgerTerminalDeathTruth terminal = entry.TerminalDeathTruth;
        bool hasLastAggressor = !string.IsNullOrWhiteSpace(terminal.LastAggressorProfileId)
            || !string.IsNullOrWhiteSpace(terminal.LastAggressorName)
            || !string.IsNullOrWhiteSpace(terminal.LastAggressorSide)
            || !string.IsNullOrWhiteSpace(terminal.LastAggressorRawRole);
        string displayName = hasLastAggressor
            ? VanguardCareerCombatantPresentation.ResolveDisplayName(terminal.LastAggressorName, terminal.LastAggressorSide, terminal.LastAggressorRawRole)
            : string.Empty;

        return new VanguardCanonicalRaidHistoryTerminalDeathTruth(
            terminal.EventId,
            terminal.ObservedAtUtc,
            terminal.TerminalDamageType,
            terminal.TerminalDamageTypeValue,
            terminal.LastDamageInfoType,
            terminal.LastDamageInfoTypeValue,
            terminal.LastDamageBodyPart,
            terminal.LastDamageBodyPartValue,
            terminal.DirectKillEventObservedAtCapture,
            BuildDirectKillCorrelationState(directDeath, terminal),
            terminal.LastAggressorProfileId,
            terminal.LastAggressorAccountId,
            displayName,
            terminal.LastAggressorSide,
            terminal.LastAggressorRawRole,
            terminal.LastAggressorInfoLevel,
            terminal.LastAggressorInfoExperience,
            terminal.LastAggressorSettingsExperience,
            "context_only_not_direct_killer",
            terminal.Source,
            terminal.TruthVersion,
            terminal.SchemaVersion);
    }

    private static string BuildDirectKillCorrelationState(
        VanguardCanonicalRaidHistoryDeath? directDeath,
        VanguardCareerRaidLedgerTerminalDeathTruth terminal)
    {
        if (directDeath is null)
        {
            return "no_direct_killer_event";
        }

        if (string.IsNullOrWhiteSpace(terminal.LastAggressorProfileId))
        {
            return "direct_killer_present_last_aggressor_absent";
        }

        return string.Equals(
                Normalize(directDeath.KillerProfileId),
                Normalize(terminal.LastAggressorProfileId),
                StringComparison.OrdinalIgnoreCase)
            ? "direct_killer_matches_last_aggressor_context"
            : "direct_killer_differs_from_last_aggressor_context";
    }

    private static bool HasLastAggressorContext(VanguardCanonicalRaidHistoryTerminalDeathTruth terminal)
        => !string.IsNullOrWhiteSpace(terminal.LastAggressorProfileId)
            || !string.IsNullOrWhiteSpace(terminal.LastAggressorDisplayName)
            || !string.IsNullOrWhiteSpace(terminal.LastAggressorSide)
            || !string.IsNullOrWhiteSpace(terminal.LastAggressorRawRole);

    private static VanguardCanonicalRaidHistoryParityCheck BuildParity(
        VanguardCareerRaidLedgerVerificationSnapshot verification,
        IReadOnlyList<VanguardOperatorCanonicalRaidHistory> histories,
        VanguardCareerProjectionReadModel careerProjection)
    {
        var mismatches = new List<VanguardCanonicalRaidHistoryParityMismatch>();

        Compare("<global>", "SourceEntryCount", careerProjection.SourceEntryCount, verification.SourceEntryCount, mismatches);
        Compare("<global>", "VerifiedEntryCount", careerProjection.VerifiedEntryCount, verification.VerifiedEntryCount, mismatches);
        Compare("<global>", "RejectedEntryCount", careerProjection.RejectedEntryCount, verification.RejectedEntryCount, mismatches);
        Compare("<global>", "DuplicateEntryCount", careerProjection.DuplicateEntryCount, verification.DuplicateEntryCount, mismatches);
        Compare("<global>", "UnsupportedEntryCount", careerProjection.UnsupportedEntryCount, verification.UnsupportedEntryCount, mismatches);
        Compare("<global>", "IntegrityRejectedEntryCount", careerProjection.IntegrityRejectedEntryCount, verification.IntegrityRejectedEntryCount, mismatches);
        Compare("<global>", "SemanticRejectedEntryCount", careerProjection.SemanticRejectedEntryCount, verification.SemanticRejectedEntryCount, mismatches);
        Compare("<global>", "OwnerMismatchEntryCount", careerProjection.OwnerMismatchEntryCount, verification.OwnerMismatchEntryCount, mismatches);
        int raidHistoryUnprojectedVerifiedEntryCount = Math.Max(0, verification.VerifiedEntryCount - histories.Sum(value => value.VerifiedEntryCount));
        Compare("<global>", "UnprojectedVerifiedEntryCount", careerProjection.UnprojectedVerifiedEntryCount, raidHistoryUnprojectedVerifiedEntryCount, mismatches);
        CompareString("<global>", "CoverageState", careerProjection.CoverageState, verification.CoverageState, mismatches);
        CompareString("<global>", "LedgerReadState", careerProjection.LedgerReadState, verification.LedgerReadState, mismatches);
        CompareString("<global>", "CoverageBoundary", careerProjection.CoverageBoundary, VanguardCanonicalRaidHistorySchema.CoverageBoundary, mismatches);
        CompareString("<global>", "CombatMethodCoverageState", careerProjection.CombatMethodCoverageState, VanguardCanonicalRaidHistorySchema.CombatMethodCoverageState, mismatches);

        var careerByOperator = careerProjection.Operators
            .Where(value => !string.IsNullOrWhiteSpace(value.OperatorId))
            .GroupBy(value => Normalize(value.OperatorId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (VanguardOperatorCanonicalRaidHistory history in histories)
        {
            if (!careerByOperator.TryGetValue(Normalize(history.OperatorId), out VanguardOperatorCareerProjection? careerOperator) || careerOperator is null)
            {
                mismatches.Add(new VanguardCanonicalRaidHistoryParityMismatch(history.OperatorId, "OperatorPresence", "missing", "present"));
                continue;
            }

            int raids = history.Raids.Count(value => value.Participated);
            int survived = history.Raids.Count(value => value.Participated && value.AliveAtRaidEnd && !value.Died);
            int kia = history.Raids.Count(value => value.Participated && value.Died && !value.AliveAtRaidEnd);
            int selfInflictedDeaths = history.Raids.Count(value => value.Death?.SelfInflicted == true);
            int kills = history.Raids.Sum(value => value.ConfirmedKills.Count);
            double skillPoints = BuildSkillPointAggregate(history.Raids);

            Compare(history.OperatorId, "SourceEntryCount", careerOperator.SourceEntryCount, history.SourceEntryCount, mismatches);
            Compare(history.OperatorId, "VerifiedEntryCount", careerOperator.VerifiedEntryCount, history.VerifiedEntryCount, mismatches);
            Compare(history.OperatorId, "RejectedEntryCount", careerOperator.RejectedEntryCount, history.RejectedEntryCount, mismatches);
            Compare(history.OperatorId, "VerifiedRaidCount", careerOperator.VerifiedRaidCount, raids, mismatches);
            Compare(history.OperatorId, "VerifiedSurvivedRaidCount", careerOperator.VerifiedSurvivedRaidCount, survived, mismatches);
            Compare(history.OperatorId, "VerifiedKiaCount", careerOperator.VerifiedKiaCount, kia, mismatches);
            Compare(history.OperatorId, "VerifiedSelfInflictedDeathCount", careerOperator.VerifiedSelfInflictedDeathCount, selfInflictedDeaths, mismatches);
            Compare(history.OperatorId, "VerifiedKillCount", careerOperator.VerifiedKillCount, kills, mismatches);
            CompareDouble(history.OperatorId, "SkillSessionPointsEarnedTotal", careerOperator.SkillSessionPointsEarnedTotal, skillPoints, mismatches);

            IReadOnlyDictionary<string, int> raidHistoryVictims = BuildConfirmedVictimAggregate(history.Raids);
            IReadOnlyDictionary<string, int> careerVictims = careerOperator.ConfirmedVictims
                .GroupBy(value => CombatantAggregateKey(value.DisplayName, value.Side, value.RawRole), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(value => value.Count), StringComparer.OrdinalIgnoreCase);
            CompareIntDictionary(history.OperatorId, "ConfirmedVictims", careerVictims, raidHistoryVictims, mismatches);

            IReadOnlyDictionary<string, int> raidHistoryDeathSources = BuildConfirmedDeathSourceAggregate(history.Raids);
            IReadOnlyDictionary<string, int> careerDeathSources = careerOperator.ConfirmedDeathSources
                .GroupBy(value => DeathSourceAggregateKey(value.DisplayName, value.Side, value.RawRole, value.SelfInflicted), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(value => value.Count), StringComparer.OrdinalIgnoreCase);
            CompareIntDictionary(history.OperatorId, "ConfirmedDeathSources", careerDeathSources, raidHistoryDeathSources, mismatches);

            IReadOnlyDictionary<string, int> raidHistoryKillRoles = history.Raids
                .SelectMany(value => value.ConfirmedKills)
                .GroupBy(value => Normalize(value.TargetRawRole, "unknown"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            CompareIntDictionary(history.OperatorId, "KillCountByTargetRawRole", careerOperator.KillCountByTargetRawRole, raidHistoryKillRoles, mismatches);

            IReadOnlyDictionary<string, int> raidHistoryDeathRoles = history.Raids
                .Where(value => value.Death is not null && !value.Death.SelfInflicted)
                .Select(value => value.Death!)
                .GroupBy(value => Normalize(value.KillerRawRole, "unknown"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            CompareIntDictionary(history.OperatorId, "DeathCountByKillerRawRole", careerOperator.DeathCountByKillerRawRole, raidHistoryDeathRoles, mismatches);

            IReadOnlyDictionary<string, double> raidHistorySkillBySkill = history.Raids
                .SelectMany(value => value.SkillSessionPoints)
                .GroupBy(value => Normalize(value.SkillId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(value => value.PointsEarnedDuringSession), StringComparer.OrdinalIgnoreCase);
            CompareDoubleDictionary(history.OperatorId, "SkillSessionPointsEarnedBySkill", careerOperator.SkillSessionPointsEarnedBySkill, raidHistorySkillBySkill, mismatches);
        }

        foreach (VanguardOperatorCareerProjection careerOperator in careerProjection.Operators)
        {
            if (!histories.Any(history => string.Equals(Normalize(history.OperatorId), Normalize(careerOperator.OperatorId), StringComparison.OrdinalIgnoreCase)))
            {
                mismatches.Add(new VanguardCanonicalRaidHistoryParityMismatch(careerOperator.OperatorId, "OperatorPresence", "present", "missing"));
            }
        }

        return new VanguardCanonicalRaidHistoryParityCheck(
            mismatches.Count == 0,
            histories.Count,
            mismatches.Count,
            SkillPointParityTolerance,
            mismatches.ToArray());
    }

    private static IReadOnlyDictionary<string, int> BuildConfirmedVictimAggregate(IReadOnlyList<VanguardCanonicalRaidHistoryEntry> raids)
        => raids
            .SelectMany(value => value.ConfirmedKills)
            .GroupBy(value => CombatantAggregateKey(value.TargetDisplayName, value.TargetSide, value.TargetRawRole), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, int> BuildConfirmedDeathSourceAggregate(IReadOnlyList<VanguardCanonicalRaidHistoryEntry> raids)
        => raids
            .Where(value => value.Death is not null)
            .Select(value => value.Death!)
            .GroupBy(
                value => DeathSourceAggregateKey(value.KillerDisplayName, value.KillerSide, value.KillerRawRole, value.SelfInflicted),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static string CombatantAggregateKey(string? displayName, string? side, string? rawRole)
        => Normalize(displayName, "none") + "|" + Normalize(side, "none") + "|" + Normalize(rawRole, "none");

    private static string DeathSourceAggregateKey(string? displayName, string? side, string? rawRole, bool selfInflicted)
        => selfInflicted ? "self_inflicted" : CombatantAggregateKey(displayName, side, rawRole);

    private static double BuildSkillPointAggregate(IReadOnlyList<VanguardCanonicalRaidHistoryEntry> raids)
        => raids
            .SelectMany(value => value.SkillSessionPoints)
            .GroupBy(value => Normalize(value.SkillId), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Sum(value => value.PointsEarnedDuringSession))
            .Sum();

    private static void Compare(
        string operatorId,
        string field,
        int careerValue,
        int raidHistoryValue,
        ICollection<VanguardCanonicalRaidHistoryParityMismatch> mismatches)
    {
        if (careerValue == raidHistoryValue) return;
        mismatches.Add(new VanguardCanonicalRaidHistoryParityMismatch(
            operatorId,
            field,
            careerValue.ToString(CultureInfo.InvariantCulture),
            raidHistoryValue.ToString(CultureInfo.InvariantCulture)));
    }

    private static void CompareString(
        string operatorId,
        string field,
        string? careerValue,
        string? raidHistoryValue,
        ICollection<VanguardCanonicalRaidHistoryParityMismatch> mismatches)
    {
        string left = Normalize(careerValue);
        string right = Normalize(raidHistoryValue);
        if (string.Equals(left, right, StringComparison.Ordinal)) return;
        mismatches.Add(new VanguardCanonicalRaidHistoryParityMismatch(operatorId, field, left, right));
    }

    private static void CompareIntDictionary(
        string operatorId,
        string field,
        IReadOnlyDictionary<string, int> careerValues,
        IReadOnlyDictionary<string, int> raidHistoryValues,
        ICollection<VanguardCanonicalRaidHistoryParityMismatch> mismatches)
    {
        string[] keys = careerValues.Keys.Concat(raidHistoryValues.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string key in keys)
        {
            int careerValue = careerValues.TryGetValue(key, out int left) ? left : 0;
            int raidHistoryValue = raidHistoryValues.TryGetValue(key, out int right) ? right : 0;
            Compare(operatorId, field + ":" + key, careerValue, raidHistoryValue, mismatches);
        }
    }

    private static void CompareDoubleDictionary(
        string operatorId,
        string field,
        IReadOnlyDictionary<string, double> careerValues,
        IReadOnlyDictionary<string, double> raidHistoryValues,
        ICollection<VanguardCanonicalRaidHistoryParityMismatch> mismatches)
    {
        string[] keys = careerValues.Keys.Concat(raidHistoryValues.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string key in keys)
        {
            double careerValue = careerValues.TryGetValue(key, out double left) ? left : 0.0;
            double raidHistoryValue = raidHistoryValues.TryGetValue(key, out double right) ? right : 0.0;
            CompareDouble(operatorId, field + ":" + key, careerValue, raidHistoryValue, mismatches);
        }
    }

    private static void CompareDouble(
        string operatorId,
        string field,
        double careerValue,
        double raidHistoryValue,
        ICollection<VanguardCanonicalRaidHistoryParityMismatch> mismatches)
    {
        double scale = Math.Max(1.0, Math.Max(Math.Abs(careerValue), Math.Abs(raidHistoryValue)));
        if (Math.Abs(careerValue - raidHistoryValue) <= SkillPointParityTolerance * scale) return;
        mismatches.Add(new VanguardCanonicalRaidHistoryParityMismatch(
            operatorId,
            field,
            careerValue.ToString("R", CultureInfo.InvariantCulture),
            raidHistoryValue.ToString("R", CultureInfo.InvariantCulture)));
    }

    private static string BuildHistoryEventId(VanguardCareerRaidLedgerEntry entry)
        => string.Join("|",
            "raid_history_v1",
            Normalize(entry.RaidSessionId),
            "operator",
            Normalize(entry.OwnerProfileId),
            Normalize(entry.OperatorId));

    private static string Normalize(string? value, string fallback = "")
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(';', '_').Replace('\n', '_').Replace('\r', '_');

    private static string Bool(bool value) => value ? "true" : "false";
}
