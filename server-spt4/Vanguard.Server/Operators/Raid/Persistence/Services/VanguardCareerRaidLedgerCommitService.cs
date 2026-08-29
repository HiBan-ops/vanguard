using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using Vanguard.Server.Operators.Raid.Persistence.Models;
using Vanguard.Server.Operators.Storage;
using Vanguard.Server.Diagnostics;

// Responsibility: Prepares and commits the immutable per-raid Career fact ledger that later Career/Raid History views can safely reconstruct.
// Flow: Client-observed events are schema/identity validated, normalized and deduplicated into a prepared owner-scoped batch; commit writes atomically, readback verifies fingerprints, and rollback participates in the parent persistence transaction.
// Authority boundary: The server ledger/store is durable authority; client evidence can propose facts but cannot mutate aggregate Career state or bypass admission checks.
// Invariant: Replays must be idempotent, invalid/unverifiable facts are skipped rather than invented, and an admitted batch must be atomically readable after commit.
namespace Vanguard.Server.Operators.Raid.Persistence.Services;

/// <summary>
/// Transaction boundary for the durable raw Career ledger.
/// It persists only versioned observed raid facts. Aggregate Career statistics, XP,
/// achievements and PersonaEvidence remain untouched. Ledger admission is fail-open relative to the
/// primary Operator persistence transaction; once admitted, writes are atomic per owner, read back,
/// and participate in the same batch rollback boundary.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class VanguardCareerRaidLedgerCommitService(
    VanguardOperatorStore store,
    ISptLogger<VanguardCareerRaidLedgerCommitService> logger)
{
    public const string StatusTag = "VANGUARD_VERSIONED_CAREER_RAID_LEDGER_AND_ATOMIC_COMMIT_FOUNDATION_STATUS";
    private const int MaximumKillEvents = 512;
    private const int MaximumTerminalDeathTruthEvents = 64;
    private const int MaximumXpKillCreditEvents = 512;

    // Preparation is intentionally split from commit. First reject malformed raid/participant identity,
    // then normalize each bounded event family, and only then build per-owner ledger entries. This makes
    // the later transaction mechanical: it receives a fully checked batch or a clearly skipped result.
    public async Task<VanguardCareerRaidLedgerPreparedBatch> PrepareAsync(
        string raidSessionId,
        VanguardCareerRaidLedgerCommitRequest? request,
        IReadOnlyList<VanguardCareerRaidLedgerOperatorTruth> operators,
        DateTimeOffset committedAtUtc)
    {
        string raid = Normalize(raidSessionId);
        if (request is null)
        {
            return Skipped(raid, "client_payload_missing_preserved", operators.Count, 0);
        }

        if (request.SchemaVersion != VanguardCareerRaidLedgerSchema.CurrentVersion)
        {
            return Skipped(raid, "unsupported_payload_schema_" + request.SchemaVersion, operators.Count, request.KillEvents?.Count ?? 0);
        }

        if (string.IsNullOrWhiteSpace(raid)
            || !string.Equals(raid, Normalize(request.RaidSessionId), StringComparison.OrdinalIgnoreCase))
        {
            return Skipped(raid, "raid_session_mismatch", operators.Count, request.KillEvents?.Count ?? 0);
        }

        if (string.IsNullOrWhiteSpace(request.StopSource)
            || string.IsNullOrWhiteSpace(request.ExitStatus)
            || request.StopObservedAtUtc == default)
        {
            return Skipped(raid, "stop_boundary_incomplete", operators.Count, request.KillEvents?.Count ?? 0);
        }

        VanguardCareerRaidLedgerOperatorTruth[] participantTruth = operators
            .Where(value => !string.IsNullOrWhiteSpace(value.OwnerProfileId)
                && !string.IsNullOrWhiteSpace(value.OperatorId)
                && !string.IsNullOrWhiteSpace(value.BotProfileId))
            .ToArray();
        if (participantTruth.Length != operators.Count || participantTruth.Length == 0)
        {
            return Skipped(raid, "participant_truth_invalid", operators.Count, request.KillEvents?.Count ?? 0);
        }

        var participantByBotProfile = new Dictionary<string, VanguardCareerRaidLedgerOperatorTruth>(StringComparer.OrdinalIgnoreCase);
        var participantKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VanguardCareerRaidLedgerOperatorTruth participant in participantTruth)
        {
            string participantKey = Normalize(participant.OwnerProfileId) + ":" + Normalize(participant.OperatorId);
            if (!participantKeys.Add(participantKey)
                || !participantByBotProfile.TryAdd(Normalize(participant.BotProfileId), participant))
            {
                return Skipped(raid, "participant_identity_duplicate", operators.Count, request.KillEvents?.Count ?? 0);
            }
        }

        VanguardCareerRaidLedgerKillEventRequest[] killEvents = (request.KillEvents ?? Array.Empty<VanguardCareerRaidLedgerKillEventRequest>()).ToArray();
        if (killEvents.Length > MaximumKillEvents)
        {
            return Skipped(raid, "kill_event_limit_exceeded", operators.Count, killEvents.Length);
        }

        var seenEventIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VanguardCareerRaidLedgerKillEventRequest kill in killEvents)
        {
            string targetProfile = Normalize(kill.TargetProfileId);
            string killerProfile = Normalize(kill.KillerProfileId);
            string eventId = Normalize(kill.EventId);
            if (string.IsNullOrWhiteSpace(targetProfile)
                || string.IsNullOrWhiteSpace(eventId)
                || kill.ObservedAtUtc == default
                || !string.Equals(Normalize(kill.RaidSessionId), raid, StringComparison.OrdinalIgnoreCase)
                || !VanguardCareerRaidLedgerIntegrity.IsCompatibleKillEventId(eventId, raid, targetProfile))
            {
                return Skipped(raid, "kill_event_invalid", operators.Count, killEvents.Length);
            }

            if (!seenEventIds.Add(eventId) || !seenTargets.Add(targetProfile))
            {
                return Skipped(raid, "kill_event_duplicate", operators.Count, killEvents.Length);
            }

            if (!participantByBotProfile.ContainsKey(killerProfile)
                && !participantByBotProfile.ContainsKey(targetProfile))
            {
                return Skipped(raid, "kill_event_has_no_operator_participant", operators.Count, killEvents.Length);
            }
        }

        VanguardCareerRaidTerminalDeathTruthEventRequest[] terminalDeathTruthEvents = (request.TerminalDeathTruthEvents ?? Array.Empty<VanguardCareerRaidTerminalDeathTruthEventRequest>()).ToArray();
        if (terminalDeathTruthEvents.Length > MaximumTerminalDeathTruthEvents)
        {
            return Skipped(raid, "terminal_death_truth_event_limit_exceeded", operators.Count, killEvents.Length);
        }

        var seenTerminalVictims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VanguardCareerRaidTerminalDeathTruthEventRequest terminal in terminalDeathTruthEvents)
        {
            string victimProfile = Normalize(terminal.VictimProfileId);
            string eventId = Normalize(terminal.EventId);
            if (string.IsNullOrWhiteSpace(victimProfile)
                || string.IsNullOrWhiteSpace(eventId)
                || terminal.ObservedAtUtc == default
                || string.IsNullOrWhiteSpace(terminal.TerminalDamageType)
                || string.IsNullOrWhiteSpace(terminal.LastDamageInfoType)
                || string.IsNullOrWhiteSpace(terminal.LastDamageBodyPart)
                || !string.Equals(Normalize(terminal.RaidSessionId), raid, StringComparison.OrdinalIgnoreCase)
                || !VanguardCareerRaidLedgerIntegrity.IsCompatibleTerminalDeathTruthEventId(eventId, raid, victimProfile)
                || !string.Equals(Normalize(terminal.Source), "Player.OnPlayerDeadStatic", StringComparison.Ordinal))
            {
                return Skipped(raid, "terminal_death_truth_event_invalid", operators.Count, killEvents.Length);
            }

            if (!seenTerminalVictims.Add(victimProfile))
            {
                return Skipped(raid, "terminal_death_truth_event_duplicate", operators.Count, killEvents.Length);
            }

            if (!participantByBotProfile.ContainsKey(victimProfile))
            {
                return Skipped(raid, "terminal_death_truth_event_not_operator", operators.Count, killEvents.Length);
            }
        }

        VanguardCareerRaidXpKillCreditEventRequest[] xpKillCreditEvents = (request.XpKillCreditEvents ?? Array.Empty<VanguardCareerRaidXpKillCreditEventRequest>()).ToArray();
        if (xpKillCreditEvents.Length > MaximumXpKillCreditEvents)
        {
            return Skipped(raid, "xp_kill_credit_event_limit_exceeded", operators.Count, killEvents.Length);
        }

        var seenXpCreditIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VanguardCareerRaidXpKillCreditEventRequest credit in xpKillCreditEvents)
        {
            string xpRecipientProfile = Normalize(credit.XpRecipientProfileId);
            string targetProfile = Normalize(credit.TargetProfileId);
            string eventId = Normalize(credit.EventId);
            if (string.IsNullOrWhiteSpace(xpRecipientProfile)
                || string.IsNullOrWhiteSpace(targetProfile)
                || string.IsNullOrWhiteSpace(eventId)
                || credit.ObservedAtUtc == default
                || credit.KillSequence <= 0
                || !string.Equals(Normalize(credit.RaidSessionId), raid, StringComparison.OrdinalIgnoreCase)
                || !participantByBotProfile.ContainsKey(xpRecipientProfile)
                || !VanguardCareerRaidLedgerIntegrity.IsCompatibleXpKillCreditEventId(eventId, raid, xpRecipientProfile, targetProfile)
                || !string.Equals(Normalize(credit.Source), "Player.OnBeenKilledByAggressor+BackendConfigSettingsClass.Experience.Kill", StringComparison.Ordinal))
            {
                return Skipped(raid, "xp_kill_credit_event_invalid", operators.Count, killEvents.Length);
            }

            if (!seenXpCreditIds.Add(eventId))
            {
                return Skipped(raid, "xp_kill_credit_event_duplicate", operators.Count, killEvents.Length);
            }

            if (!float.IsFinite(credit.MarkOfUnknownScavKillExpPenalty)
                || credit.MarkOfUnknownScavKillExpPenalty < 0f
                || credit.BaseXp < 0
                || credit.BodyPartBonusXp < 0
                || credit.StreakBonusXp < 0
                || credit.KillXpSubtotal < 0
                || credit.KillXpSubtotal != credit.BaseXp + credit.BodyPartBonusXp + credit.StreakBonusXp
                || (!credit.CalculationAvailable && (credit.Awarded || credit.KillXpSubtotal != 0))
                || (credit.SameGroup && (credit.Awarded || credit.KillXpSubtotal != 0))
                || (credit.Awarded != (credit.CalculationAvailable && !credit.SameGroup && credit.KillXpSubtotal > 0)))
            {
                return Skipped(raid, "xp_kill_credit_semantics_invalid", operators.Count, killEvents.Length);
            }
        }

        var candidateEntries = new List<VanguardCareerRaidLedgerEntry>(participantTruth.Length);
        foreach (VanguardCareerRaidLedgerOperatorTruth participant in participantTruth)
        {
            string botProfileId = Normalize(participant.BotProfileId);
            VanguardCareerRaidLedgerKillEvent[] kills = killEvents
                .Where(kill => string.Equals(Normalize(kill.KillerProfileId), botProfileId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(kill => kill.Ordinal)
                .ThenBy(kill => Normalize(kill.EventId), StringComparer.OrdinalIgnoreCase)
                .Select(ToLedgerKillEvent)
                .ToArray();

            VanguardCareerRaidLedgerKillEventRequest? deathSource = killEvents
                .Where(kill => string.Equals(Normalize(kill.TargetProfileId), botProfileId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(kill => kill.Ordinal)
                .FirstOrDefault();
            if (!participant.Died && deathSource is not null)
            {
                return Skipped(raid, "death_event_conflicts_with_alive_truth_" + Normalize(participant.OperatorId), operators.Count, killEvents.Length);
            }

            VanguardCareerRaidLedgerDeathEvent? death = deathSource is null ? null : ToLedgerDeathEvent(deathSource);
            VanguardCareerRaidTerminalDeathTruthEventRequest? terminalDeathSource = terminalDeathTruthEvents
                .SingleOrDefault(value => string.Equals(Normalize(value.VictimProfileId), botProfileId, StringComparison.OrdinalIgnoreCase));
            if (!participant.Died && terminalDeathSource is not null)
            {
                return Skipped(raid, "terminal_death_truth_conflicts_with_alive_truth_" + Normalize(participant.OperatorId), operators.Count, killEvents.Length);
            }

            VanguardCareerRaidLedgerTerminalDeathTruth? terminalDeathTruth = terminalDeathSource is null
                ? null
                : ToLedgerTerminalDeathTruth(terminalDeathSource);
            VanguardCareerRaidLedgerXpKillCredit[] xpKillCredits = xpKillCreditEvents
                .Where(value => string.Equals(Normalize(value.XpRecipientProfileId), botProfileId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(value => value.KillSequence)
                .ThenBy(value => Normalize(value.EventId), StringComparer.OrdinalIgnoreCase)
                .Select(ToLedgerXpKillCredit)
                .ToArray();

            VanguardCareerRaidLedgerSkillSessionPoint[] skills = participant.CareerTruthProbe.SkillsWithSessionPointEntries
                .Where(skill => !string.IsNullOrWhiteSpace(skill.Id) && skill.PointsEarnedDuringSession > 0.0)
                .OrderBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
                .Select(skill => new VanguardCareerRaidLedgerSkillSessionPoint(
                    Normalize(skill.Id),
                    skill.Progress,
                    skill.PointsEarnedDuringSession))
                .ToArray();

            string ledgerEntryId = VanguardCareerRaidLedgerIntegrity.BuildLedgerEntryId(raid, participant.OwnerProfileId, participant.OperatorId);
            var draft = new VanguardCareerRaidLedgerEntry(
                ledgerEntryId,
                raid,
                Normalize(participant.OwnerProfileId),
                Normalize(participant.OperatorId),
                botProfileId,
                true,
                !participant.Died,
                participant.Died,
                Normalize(request.ExitStatus, "unknown"),
                Normalize(request.ExitName, "none"),
                Normalize(request.StopSource, "unknown"),
                Normalize(request.StopProfileId, "none"),
                request.StopDelay,
                request.StopObservedAtUtc,
                kills,
                death,
                skills,
                string.Empty,
                committedAtUtc);
            if (terminalDeathTruth is not null)
            {
                draft = draft with
                {
                    TerminalDeathTruth = terminalDeathTruth,
                    TerminalDeathTruthFingerprint = VanguardCareerRaidLedgerIntegrity.ComputeTerminalDeathTruthFingerprint(terminalDeathTruth)
                };
            }
            if (xpKillCredits.Length > 0)
            {
                draft = draft with
                {
                    XpKillCredits = xpKillCredits,
                    XpKillCreditsFingerprint = VanguardCareerRaidLedgerIntegrity.ComputeXpKillCreditsFingerprint(xpKillCredits)
                };
            }

            candidateEntries.Add(draft with { SourceFingerprint = VanguardCareerRaidLedgerIntegrity.ComputeSourceFingerprint(draft) });
        }

        var preparedOwners = new List<VanguardCareerRaidLedgerPreparedOwner>();
        int added = 0;
        int existing = 0;
        foreach (IGrouping<string, VanguardCareerRaidLedgerEntry> ownerGroup in candidateEntries.GroupBy(entry => entry.OwnerProfileId, StringComparer.OrdinalIgnoreCase))
        {
            string owner = ownerGroup.Key;
            IReadOnlyList<VanguardCareerRaidLedgerEntry> before = await store.LoadCareerRaidLedgerAsync(owner);
            var after = before.ToList();
            var expected = new List<VanguardCareerRaidLedgerEntry>();
            bool requiresWrite = false;

            foreach (VanguardCareerRaidLedgerEntry candidate in ownerGroup)
            {
                VanguardCareerRaidLedgerEntry? prior = before.FirstOrDefault(entry =>
                    VanguardCareerRaidLedgerIntegrity.IsCompatibleLedgerEntryId(
                        entry.LedgerEntryId, candidate.RaidSessionId, candidate.OwnerProfileId, candidate.OperatorId));
                if (prior is not null)
                {
                    if (!HasValidRawFingerprints(prior)
                        || !HasValidRawFingerprints(candidate)
                        || !string.Equals(
                            VanguardCareerRaidLedgerIntegrity.ComputeSemanticSourceFingerprint(prior),
                            VanguardCareerRaidLedgerIntegrity.ComputeSemanticSourceFingerprint(candidate),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return Skipped(raid, "ledger_entry_conflict_" + candidate.OperatorId, operators.Count, killEvents.Length);
                    }

                    if (!TerminalDeathTruthMatches(prior, candidate))
                    {
                        return Skipped(raid, "terminal_death_truth_conflict_" + candidate.OperatorId, operators.Count, killEvents.Length);
                    }
                    if (!XpKillCreditsMatch(prior, candidate))
                    {
                        return Skipped(raid, "xp_kill_credit_conflict_" + candidate.OperatorId, operators.Count, killEvents.Length);
                    }

                    existing++;
                    expected.Add(prior);
                    continue;
                }

                after.Add(candidate);
                expected.Add(candidate);
                added++;
                requiresWrite = true;
            }

            preparedOwners.Add(new VanguardCareerRaidLedgerPreparedOwner(
                owner,
                before,
                after.OrderBy(entry => entry.CommittedAtUtc).ThenBy(entry => entry.LedgerEntryId, StringComparer.OrdinalIgnoreCase).ToArray(),
                expected,
                requiresWrite));
        }

        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] phase=preflight; raid={raid}; admitted=true; operators={participantTruth.Length}; owners={preparedOwners.Count}; killEvents={killEvents.Length}; terminalDeathTruthEvents={terminalDeathTruthEvents.Length}; terminalTruthEntries={candidateEntries.Count(value => value.TerminalDeathTruth is not null)}; xpKillCreditEvents={xpKillCreditEvents.Length}; xpKillCreditEntries={candidateEntries.Count(value => (value.XpKillCredits?.Count ?? 0) > 0)}; xpKillShadowSubtotal={candidateEntries.Sum(value => value.XpKillCredits?.Sum(credit => credit.KillXpSubtotal) ?? 0)}; lastAggressorSemantics=context_only_not_direct_killer; added={added}; existing={existing}; exitStatus={Normalize(request.ExitStatus, "unknown")}; aggregateMutation=false; xpMutation=false; achievementsMutation=false; personaEvidenceMutation=false; tag={StatusTag}"));
        return new VanguardCareerRaidLedgerPreparedBatch(
            raid,
            true,
            "career_ledger_preflight_ok",
            preparedOwners,
            added,
            existing,
            participantTruth.Length,
            killEvents.Length);
    }

    public async Task<VanguardCareerRaidLedgerCommitResult> CommitAsync(VanguardCareerRaidLedgerPreparedBatch prepared)
    {
        if (!prepared.Admitted)
        {
            return new VanguardCareerRaidLedgerCommitResult(
                "skipped",
                false,
                false,
                false,
                0,
                0,
                0,
                prepared.Reason);
        }

        foreach (VanguardCareerRaidLedgerPreparedOwner owner in prepared.Owners.Where(value => value.RequiresWrite))
        {
            await store.SaveCareerRaidLedgerAtomicAsync(owner.OwnerProfileId, owner.After);
        }

        foreach (VanguardCareerRaidLedgerPreparedOwner owner in prepared.Owners)
        {
            IReadOnlyList<VanguardCareerRaidLedgerEntry> readback = await store.LoadCareerRaidLedgerAsync(owner.OwnerProfileId);
            foreach (VanguardCareerRaidLedgerEntry expected in owner.ExpectedEntries)
            {
                VanguardCareerRaidLedgerEntry? actual = readback.FirstOrDefault(entry =>
                    VanguardCareerRaidLedgerIntegrity.IsCompatibleLedgerEntryId(
                        entry.LedgerEntryId, expected.RaidSessionId, expected.OwnerProfileId, expected.OperatorId));
                if (actual is null
                    || !HasValidRawFingerprints(actual)
                    || !HasValidRawFingerprints(expected)
                    || !string.Equals(
                        VanguardCareerRaidLedgerIntegrity.ComputeSemanticSourceFingerprint(actual),
                        VanguardCareerRaidLedgerIntegrity.ComputeSemanticSourceFingerprint(expected),
                        StringComparison.OrdinalIgnoreCase)
                    || !TerminalDeathTruthMatches(actual, expected)
                    || !XpKillCreditsMatch(actual, expected))
                {
                    throw new InvalidOperationException("career_ledger_readback_mismatch_" + expected.OperatorId);
                }
            }
        }

        bool replay = prepared.AddedEntryCount == 0 && prepared.ExistingEntryCount == prepared.OperatorCount;
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] phase=commit; raid={prepared.RaidSessionId}; admitted=true; committed=true; replay={Bool(replay)}; operators={prepared.OperatorCount}; owners={prepared.Owners.Count}; killEvents={prepared.KillEventCount}; terminalTruthEntries={prepared.Owners.SelectMany(value => value.ExpectedEntries).Count(value => value.TerminalDeathTruth is not null)}; xpKillCreditEvents={prepared.Owners.SelectMany(value => value.ExpectedEntries).Sum(value => value.XpKillCredits?.Count ?? 0)}; xpKillShadowSubtotal={prepared.Owners.SelectMany(value => value.ExpectedEntries).Sum(value => value.XpKillCredits?.Sum(credit => credit.KillXpSubtotal) ?? 0)}; lastAggressorSemantics=context_only_not_direct_killer; added={prepared.AddedEntryCount}; existing={prepared.ExistingEntryCount}; atomicReplace=true; readback=true; aggregateMutation=false; xpMutation=false; tag={StatusTag}"));
        return new VanguardCareerRaidLedgerCommitResult(
            "committed",
            true,
            true,
            replay,
            prepared.AddedEntryCount,
            prepared.ExistingEntryCount,
            prepared.Owners.Count,
            replay ? "career_ledger_idempotent_replay" : "career_ledger_committed_readback_verified");
    }

    public async Task RollbackAsync(VanguardCareerRaidLedgerPreparedBatch prepared)
    {
        if (!prepared.Admitted)
        {
            return;
        }

        foreach (VanguardCareerRaidLedgerPreparedOwner owner in prepared.Owners.Where(value => value.RequiresWrite))
        {
            await store.SaveCareerRaidLedgerAtomicAsync(owner.OwnerProfileId, owner.Before);
        }
    }

    private VanguardCareerRaidLedgerPreparedBatch Skipped(string raidSessionId, string reason, int operatorCount, int killEventCount)
    {
        logger.Warning(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] phase=preflight; raid={Normalize(raidSessionId, "none")}; admitted=false; reason={reason}; operators={operatorCount}; killEvents={killEventCount}; persistenceFailOpen=true; durableCareerMutation=false; aggregateMutation=false; xpMutation=false; tag={StatusTag}"));
        return new VanguardCareerRaidLedgerPreparedBatch(
            Normalize(raidSessionId),
            false,
            reason,
            Array.Empty<VanguardCareerRaidLedgerPreparedOwner>(),
            0,
            0,
            operatorCount,
            killEventCount);
    }

    private static VanguardCareerRaidLedgerKillEvent ToLedgerKillEvent(VanguardCareerRaidLedgerKillEventRequest source)
        => new(
            Normalize(source.EventId),
            source.Ordinal,
            source.ObservedAtUtc,
            Normalize(source.TargetProfileId),
            Normalize(source.TargetAccountId, "none"),
            Normalize(source.TargetName, "none"),
            Normalize(source.TargetSide, "none"),
            Normalize(source.TargetRawRole, "none"),
            source.TargetInfoLevel,
            source.TargetInfoExperience,
            source.TargetSettingsExperience);

    private static VanguardCareerRaidLedgerDeathEvent ToLedgerDeathEvent(VanguardCareerRaidLedgerKillEventRequest source)
        => new(
            Normalize(source.EventId),
            source.Ordinal,
            source.ObservedAtUtc,
            Normalize(source.KillerProfileId, "none"),
            Normalize(source.KillerAccountId, "none"),
            Normalize(source.KillerName, "none"),
            Normalize(source.KillerSide, "none"),
            Normalize(source.KillerRawRole, "none"),
            source.KillerInfoLevel,
            source.KillerInfoExperience,
            source.KillerSettingsExperience);

    private static VanguardCareerRaidLedgerXpKillCredit ToLedgerXpKillCredit(VanguardCareerRaidXpKillCreditEventRequest source)
        => new(
            Normalize(source.EventId),
            source.ObservedAtUtc,
            Normalize(source.XpRecipientProfileId),
            Normalize(source.TargetProfileId),
            source.KillSequence,
            Normalize(source.TargetSide),
            Normalize(source.TargetRawRole),
            source.TargetLevel,
            source.KillExpInput,
            Normalize(source.BodyPart),
            source.BodyPartValue,
            source.SameGroup,
            source.TargetIsAi,
            source.XpRecipientHasMarkOfUnknown,
            source.MarkOfUnknownScavKillExpPenalty,
            source.CalculationAvailable,
            source.Awarded,
            Normalize(source.CalculationReason),
            source.BaseXp,
            source.BodyPartBonusXp,
            source.StreakBonusXp,
            source.KillXpSubtotal,
            Normalize(source.Source));

    private static VanguardCareerRaidLedgerTerminalDeathTruth ToLedgerTerminalDeathTruth(VanguardCareerRaidTerminalDeathTruthEventRequest source)
        => new(
            Normalize(source.EventId),
            source.ObservedAtUtc,
            Normalize(source.TerminalDamageType),
            source.TerminalDamageTypeValue,
            Normalize(source.LastDamageInfoType),
            source.LastDamageInfoTypeValue,
            Normalize(source.LastDamageBodyPart),
            source.LastDamageBodyPartValue,
            source.DirectKillEventObservedAtCapture,
            Normalize(source.LastAggressorProfileId),
            Normalize(source.LastAggressorAccountId),
            Normalize(source.LastAggressorName),
            Normalize(source.LastAggressorSide),
            Normalize(source.LastAggressorRawRole),
            source.LastAggressorInfoLevel,
            source.LastAggressorInfoExperience,
            source.LastAggressorSettingsExperience,
            Normalize(source.Source));

    private static bool HasValidRawFingerprints(VanguardCareerRaidLedgerEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.SourceFingerprint)
            || !string.Equals(
                VanguardCareerRaidLedgerIntegrity.ComputeSourceFingerprint(entry),
                entry.SourceFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (entry.TerminalDeathTruth is null)
        {
            if (!string.IsNullOrWhiteSpace(entry.TerminalDeathTruthFingerprint)) return false;
        }
        else if (string.IsNullOrWhiteSpace(entry.TerminalDeathTruthFingerprint)
            || !string.Equals(
                VanguardCareerRaidLedgerIntegrity.ComputeTerminalDeathTruthFingerprint(entry.TerminalDeathTruth),
                entry.TerminalDeathTruthFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        IReadOnlyList<VanguardCareerRaidLedgerXpKillCredit> credits = entry.XpKillCredits ?? Array.Empty<VanguardCareerRaidLedgerXpKillCredit>();
        return credits.Count == 0
            ? string.IsNullOrWhiteSpace(entry.XpKillCreditsFingerprint)
            : !string.IsNullOrWhiteSpace(entry.XpKillCreditsFingerprint)
                && string.Equals(
                    VanguardCareerRaidLedgerIntegrity.ComputeXpKillCreditsFingerprint(credits),
                    entry.XpKillCreditsFingerprint,
                    StringComparison.OrdinalIgnoreCase);
    }

    private static bool TerminalDeathTruthMatches(VanguardCareerRaidLedgerEntry left, VanguardCareerRaidLedgerEntry right)
    {
        if ((left.TerminalDeathTruth is null) != (right.TerminalDeathTruth is null)) return false;
        if (left.TerminalDeathTruth is null || right.TerminalDeathTruth is null) return true;
        return string.Equals(
            VanguardCareerRaidLedgerIntegrity.ComputeSemanticTerminalDeathTruthFingerprint(
                left.TerminalDeathTruth, left.RaidSessionId, left.BotProfileId),
            VanguardCareerRaidLedgerIntegrity.ComputeSemanticTerminalDeathTruthFingerprint(
                right.TerminalDeathTruth, right.RaidSessionId, right.BotProfileId),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool XpKillCreditsMatch(VanguardCareerRaidLedgerEntry left, VanguardCareerRaidLedgerEntry right)
    {
        IReadOnlyList<VanguardCareerRaidLedgerXpKillCredit> leftCredits = left.XpKillCredits ?? Array.Empty<VanguardCareerRaidLedgerXpKillCredit>();
        IReadOnlyList<VanguardCareerRaidLedgerXpKillCredit> rightCredits = right.XpKillCredits ?? Array.Empty<VanguardCareerRaidLedgerXpKillCredit>();
        if (leftCredits.Count != rightCredits.Count) return false;
        if (leftCredits.Count == 0) return true;
        return string.Equals(
            VanguardCareerRaidLedgerIntegrity.ComputeSemanticXpKillCreditsFingerprint(leftCredits, left.RaidSessionId),
            VanguardCareerRaidLedgerIntegrity.ComputeSemanticXpKillCreditsFingerprint(rightCredits, right.RaidSessionId),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value, string fallback = "")
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Bool(bool value) => value ? "true" : "false";
}
