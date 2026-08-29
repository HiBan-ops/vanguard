using SPTarkov.DI.Annotations;
using Vanguard.Server.Operators.Raid.Persistence.Models;
using Vanguard.Server.Operators.Storage;

// Responsibility: Verifies persisted Career raid-ledger entries before any projection or XP commit is allowed to trust them.
// Flow: The service loads one owner’s ledger, rejects malformed/duplicate/wrong-owner entries, validates semantic identity and fingerprints, then returns one accepted snapshot plus rejection counts.
// Authority boundary: Verification is read-only; it decides what existing persisted facts are trustworthy but never rewrites the ledger or invents replacement facts.
// Invariant: Every downstream consumer must see the same accepted ledger set for the same stored input, and rejected evidence stays rejected until the stored data changes.
namespace Vanguard.Server.Operators.Raid.Persistence.Services;

/// <summary>
/// Read-only admission authority for durable Career ledger facts. All Career and Raid History projections
/// consume this verifier so schema, ownership, semantic identity and fingerprint rules cannot diverge.
/// Pre-normalization identifiers are accepted only when their semantic fields match the stored raid fact.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class VanguardCareerRaidLedgerVerificationService(VanguardOperatorStore store)
{
    public async Task<VanguardCareerRaidLedgerVerificationSnapshot> ReadAsync(string storageProfileId)
    {
        VanguardCareerRaidLedgerReadSnapshot snapshot = await store.LoadCareerRaidLedgerSnapshotAsync(storageProfileId);
        int sourceEntryCount = snapshot.Entries.Count;
        VanguardCareerRaidLedgerEntry[] source = snapshot.Entries.Where(entry => entry is not null).ToArray();
        var accepted = new List<VanguardCareerRaidLedgerEntry>(source.Length);

        int duplicateEntryCount = 0;
        int unsupportedEntryCount = 0;
        int integrityRejectedEntryCount = 0;
        int semanticRejectedEntryCount = Math.Max(0, sourceEntryCount - source.Length);
        int ownerMismatchEntryCount = 0;

        foreach (IGrouping<string, VanguardCareerRaidLedgerEntry> identityGroup in source
            .GroupBy(BuildSemanticEntryKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            VanguardCareerRaidLedgerEntry[] identityEntries = identityGroup.ToArray();
            if (string.IsNullOrWhiteSpace(identityGroup.Key) || identityEntries.Length != 1)
            {
                duplicateEntryCount += identityEntries.Length;
                continue;
            }

            VanguardCareerRaidLedgerEntry entry = identityEntries[0];
            if (entry.SchemaVersion != VanguardCareerRaidLedgerSchema.CurrentVersion
                || !VanguardCareerRaidLedgerIntegrity.IsCompatibleTruthVersion(entry.TruthVersion, VanguardCareerRaidLedgerSchema.TruthVersion))
            {
                unsupportedEntryCount++;
                continue;
            }

            if (!string.Equals(Normalize(entry.OwnerProfileId), Normalize(storageProfileId), StringComparison.OrdinalIgnoreCase))
            {
                ownerMismatchEntryCount++;
                continue;
            }

            if (entry.Kills is null || entry.SkillSessionPoints is null || string.IsNullOrWhiteSpace(entry.SourceFingerprint))
            {
                semanticRejectedEntryCount++;
                continue;
            }

            if (!IsSemanticShapeValid(entry))
            {
                semanticRejectedEntryCount++;
                continue;
            }

            string expectedFingerprint = VanguardCareerRaidLedgerIntegrity.ComputeSourceFingerprint(entry);
            if (!string.Equals(expectedFingerprint, entry.SourceFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                integrityRejectedEntryCount++;
                continue;
            }

            if (!IsTerminalDeathTruthSemanticShapeValid(entry))
            {
                semanticRejectedEntryCount++;
                continue;
            }

            if (entry.TerminalDeathTruth is not null)
            {
                string expectedTerminalFingerprint = VanguardCareerRaidLedgerIntegrity.ComputeTerminalDeathTruthFingerprint(entry.TerminalDeathTruth);
                if (!string.Equals(expectedTerminalFingerprint, entry.TerminalDeathTruthFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    integrityRejectedEntryCount++;
                    continue;
                }
            }

            if (!IsXpKillCreditTruthSemanticShapeValid(entry))
            {
                semanticRejectedEntryCount++;
                continue;
            }
            if ((entry.XpKillCredits?.Count ?? 0) > 0)
            {
                string expectedXpFingerprint = VanguardCareerRaidLedgerIntegrity.ComputeXpKillCreditsFingerprint(entry.XpKillCredits!);
                if (!string.Equals(expectedXpFingerprint, entry.XpKillCreditsFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    integrityRejectedEntryCount++;
                    continue;
                }
            }

            accepted.Add(entry);
        }

        int rejectedEntryCount = duplicateEntryCount
            + unsupportedEntryCount
            + integrityRejectedEntryCount
            + semanticRejectedEntryCount
            + ownerMismatchEntryCount;

        return new VanguardCareerRaidLedgerVerificationSnapshot(
            ResolveCoverageState(snapshot, sourceEntryCount, accepted.Count, rejectedEntryCount),
            snapshot.ReadState,
            snapshot.ActiveFilePresent,
            snapshot.QuarantineEvidencePresent,
            sourceEntryCount,
            accepted.Count,
            rejectedEntryCount,
            duplicateEntryCount,
            unsupportedEntryCount,
            integrityRejectedEntryCount,
            semanticRejectedEntryCount,
            ownerMismatchEntryCount,
            source,
            accepted.ToArray());
    }

    public static bool IsSelfKill(VanguardCareerRaidLedgerEntry entry, VanguardCareerRaidLedgerKillEvent kill)
        => string.Equals(Normalize(kill.TargetProfileId), Normalize(entry.BotProfileId), StringComparison.OrdinalIgnoreCase);

    public static bool IsSelfDeath(VanguardCareerRaidLedgerEntry entry, VanguardCareerRaidLedgerDeathEvent death)
        => string.Equals(Normalize(death.KillerProfileId), Normalize(entry.BotProfileId), StringComparison.OrdinalIgnoreCase);

    private static bool IsSemanticShapeValid(VanguardCareerRaidLedgerEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.LedgerEntryId)
            || string.IsNullOrWhiteSpace(entry.RaidSessionId)
            || string.IsNullOrWhiteSpace(entry.OwnerProfileId)
            || string.IsNullOrWhiteSpace(entry.OperatorId)
            || string.IsNullOrWhiteSpace(entry.BotProfileId)
            || !entry.Participated
            || entry.ExitBoundaryObservedAtUtc == default
            || entry.CommittedAtUtc == default
            || entry.AliveAtRaidEnd == entry.Died)
        {
            return false;
        }

        if (!VanguardCareerRaidLedgerIntegrity.IsCompatibleLedgerEntryId(
            entry.LedgerEntryId, entry.RaidSessionId, entry.OwnerProfileId, entry.OperatorId))
        {
            return false;
        }

        if (!entry.Died && entry.Death is not null)
        {
            return false;
        }

        if (entry.Kills.Any(kill => kill is null
                || string.IsNullOrWhiteSpace(kill.EventId)
                || string.IsNullOrWhiteSpace(kill.TargetProfileId)
                || kill.ObservedAtUtc == default
                || !VanguardCareerRaidLedgerIntegrity.IsCompatibleKillEventId(kill.EventId, entry.RaidSessionId, kill.TargetProfileId))
            || entry.Kills.GroupBy(kill => Normalize(kill.EventId), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
        {
            return false;
        }

        if (entry.Death is not null
            && (string.IsNullOrWhiteSpace(entry.Death.EventId)
                || entry.Death.ObservedAtUtc == default
                || !VanguardCareerRaidLedgerIntegrity.IsCompatibleKillEventId(entry.Death.EventId, entry.RaidSessionId, entry.BotProfileId)))
        {
            return false;
        }

        VanguardCareerRaidLedgerKillEvent[] selfKillEvents = entry.Kills
            .Where(kill => IsSelfKill(entry, kill))
            .ToArray();
        bool selfInflictedDeath = entry.Death is not null && IsSelfDeath(entry, entry.Death);
        if (selfKillEvents.Length > 1)
        {
            return false;
        }

        if (selfKillEvents.Length == 1)
        {
            VanguardCareerRaidLedgerKillEvent selfKill = selfKillEvents[0];
            if (!entry.Died
                || entry.Death is null
                || !selfInflictedDeath
                || !string.Equals(selfKill.EventId, entry.Death.EventId, StringComparison.OrdinalIgnoreCase)
                || selfKill.Ordinal != entry.Death.Ordinal
                || selfKill.ObservedAtUtc != entry.Death.ObservedAtUtc)
            {
                return false;
            }
        }
        else if (selfInflictedDeath)
        {
            return false;
        }

        if (entry.SkillSessionPoints.Any(skill => skill is null
                || string.IsNullOrWhiteSpace(skill.SkillId)
                || !double.IsFinite(skill.Progress)
                || !double.IsFinite(skill.PointsEarnedDuringSession)
                || skill.PointsEarnedDuringSession <= 0.0))
        {
            return false;
        }

        return true;
    }

    private static bool IsTerminalDeathTruthSemanticShapeValid(VanguardCareerRaidLedgerEntry entry)
    {
        if (entry.TerminalDeathTruth is null)
        {
            return string.IsNullOrWhiteSpace(entry.TerminalDeathTruthFingerprint);
        }

        VanguardCareerRaidLedgerTerminalDeathTruth terminal = entry.TerminalDeathTruth;
        if (!entry.Died
            || string.IsNullOrWhiteSpace(entry.TerminalDeathTruthFingerprint)
            || terminal.SchemaVersion != VanguardCareerTerminalDeathTruthSchema.CurrentVersion
            || !VanguardCareerRaidLedgerIntegrity.IsCompatibleTruthVersion(terminal.TruthVersion, VanguardCareerTerminalDeathTruthSchema.TruthVersion)
            || terminal.ObservedAtUtc == default
            || string.IsNullOrWhiteSpace(terminal.EventId)
            || string.IsNullOrWhiteSpace(terminal.TerminalDamageType)
            || string.IsNullOrWhiteSpace(terminal.LastDamageInfoType)
            || string.IsNullOrWhiteSpace(terminal.LastDamageBodyPart)
            || !string.Equals(terminal.Source, "Player.OnPlayerDeadStatic", StringComparison.Ordinal)
            || !VanguardCareerRaidLedgerIntegrity.IsCompatibleTerminalDeathTruthEventId(terminal.EventId, entry.RaidSessionId, entry.BotProfileId))
        {
            return false;
        }

        // LastAggressor is contextual provenance only. Empty values are valid for delayed/environmental deaths.
        // Direct killer truth remains exclusively represented by the independently captured kill/death event.
        return true;
    }

    private static bool IsXpKillCreditTruthSemanticShapeValid(VanguardCareerRaidLedgerEntry entry)
    {
        IReadOnlyList<VanguardCareerRaidLedgerXpKillCredit> credits = entry.XpKillCredits ?? Array.Empty<VanguardCareerRaidLedgerXpKillCredit>();
        if (credits.Count == 0)
        {
            return string.IsNullOrWhiteSpace(entry.XpKillCreditsFingerprint);
        }
        if (string.IsNullOrWhiteSpace(entry.XpKillCreditsFingerprint)
            || credits.GroupBy(value => Normalize(value.EventId), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
        {
            return false;
        }

        foreach (VanguardCareerRaidLedgerXpKillCredit credit in credits)
        {
            if (credit.SchemaVersion != VanguardCareerXpKillCreditTruthSchema.CurrentVersion
                || !VanguardCareerRaidLedgerIntegrity.IsCompatibleTruthVersion(credit.TruthVersion, VanguardCareerXpKillCreditTruthSchema.TruthVersion)
                || credit.ObservedAtUtc == default
                || credit.KillSequence <= 0
                || string.IsNullOrWhiteSpace(credit.EventId)
                || string.IsNullOrWhiteSpace(credit.XpRecipientProfileId)
                || string.IsNullOrWhiteSpace(credit.TargetProfileId)
                || !string.Equals(credit.XpRecipientProfileId, entry.BotProfileId, StringComparison.OrdinalIgnoreCase)
                || !VanguardCareerRaidLedgerIntegrity.IsCompatibleXpKillCreditEventId(credit.EventId, entry.RaidSessionId, credit.XpRecipientProfileId, credit.TargetProfileId)
                || !string.Equals(credit.Source, "Player.OnBeenKilledByAggressor+BackendConfigSettingsClass.Experience.Kill", StringComparison.Ordinal)
                || !float.IsFinite(credit.MarkOfUnknownScavKillExpPenalty)
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
                return false;
            }
        }
        return true;
    }

    private static string ResolveCoverageState(
        VanguardCareerRaidLedgerReadSnapshot snapshot,
        int sourceEntryCount,
        int verifiedEntryCount,
        int rejectedEntryCount)
    {
        if (snapshot.ReadState.StartsWith("unreadable_", StringComparison.OrdinalIgnoreCase))
        {
            return "degraded_ledger_unreadable";
        }

        if (sourceEntryCount == 0)
        {
            return snapshot.QuarantineEvidencePresent
                ? "degraded_no_active_entries_quarantine_history_present"
                : "no_committed_entries";
        }

        if (verifiedEntryCount == 0)
        {
            return "no_verified_supported_entries";
        }

        if (snapshot.QuarantineEvidencePresent)
        {
            return "partial_verified_entries_quarantine_history_present";
        }

        return rejectedEntryCount > 0
            ? "partial_verified_committed_entries"
            : "verified_committed_entries_only";
    }

    private static string BuildSemanticEntryKey(VanguardCareerRaidLedgerEntry entry)
        => string.Join("|",
            Normalize(entry.RaidSessionId),
            Normalize(entry.OwnerProfileId),
            Normalize(entry.OperatorId));

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
