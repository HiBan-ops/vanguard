using System.Security.Cryptography;
using System.Text;
using Vanguard.Server.Operators.Raid.Persistence.Models;

// Responsibility: Validates, normalizes and fingerprints persisted career-ledger entries so replay/readback can distinguish equivalent truth from corruption or incompatible history.
// Flow: Ledger/terminal-death/XP-credit records are canonicalized to supported truth versions, semantic fingerprints are built from stable fields, and compatibility checks are reused by verification/commit services.
// Authority boundary: Integrity logic does not invent career events; event capture and persistence services supply the authoritative facts it verifies.
// Invariant: Compatible legacy truth remains readable, semantically identical records fingerprint identically, and malformed/conflicting truth is rejected rather than silently rewritten.
namespace Vanguard.Server.Operators.Raid.Persistence.Services;

/// <summary>
/// Identity and fingerprint authority for the durable Career raid ledger. New records use functional,
/// schema-oriented identifiers. Compatibility checks deliberately validate the semantic fields of older
/// identifiers so existing legacy profiles remain readable without carrying development lineage into new writes.
/// Raw fingerprints always remain verifiable against the exact bytes represented by a stored entry.
/// </summary>
public static class VanguardCareerRaidLedgerIntegrity
{
    public static string BuildKillEventId(string raidSessionId, string targetProfileId)
        => "career_kill_v1|" + Normalize(raidSessionId) + "|kill|" + Normalize(targetProfileId);

    public static string BuildLedgerEntryId(string raidSessionId, string ownerProfileId, string operatorId)
        => "career_raid_v1|" + Normalize(raidSessionId) + "|operator|" + Normalize(ownerProfileId) + "|" + Normalize(operatorId);

    public static string BuildXpKillCreditEventId(string raidSessionId, string xpRecipientProfileId, string targetProfileId)
        => "career_xp_v1|" + Normalize(raidSessionId) + "|xp_kill_credit|" + Normalize(xpRecipientProfileId) + "|" + Normalize(targetProfileId);

    public static bool IsCompatibleKillEventId(string? actual, string raidSessionId, string targetProfileId)
        => HasSemanticIdentity(actual, 4,
            (1, Normalize(raidSessionId)),
            (2, "kill"),
            (3, Normalize(targetProfileId)));

    public static bool IsCompatibleLedgerEntryId(string? actual, string raidSessionId, string ownerProfileId, string operatorId)
        => HasSemanticIdentity(actual, 5,
            (1, Normalize(raidSessionId)),
            (2, "operator"),
            (3, Normalize(ownerProfileId)),
            (4, Normalize(operatorId)));

    public static bool IsCompatibleXpKillCreditEventId(string? actual, string raidSessionId, string xpRecipientProfileId, string targetProfileId)
        => HasSemanticIdentity(actual, 5,
            (1, Normalize(raidSessionId)),
            (2, "xp_kill_credit"),
            (3, Normalize(xpRecipientProfileId)),
            (4, Normalize(targetProfileId)));

    public static bool IsCompatibleTerminalDeathTruthEventId(string? actual, string raidSessionId, string victimProfileId)
        => HasSemanticIdentity(actual, 4,
            (1, Normalize(raidSessionId)),
            (2, "terminal_death"),
            (3, Normalize(victimProfileId)));

    /// <summary>
    /// Accepts the canonical functional truth version and pre-normalization values whose semantic suffix
    /// is identical. The legacy prefix is intentionally not encoded as a new public constant. Fingerprint
    /// verification still uses the exact stored value, so compatibility cannot rewrite historical evidence.
    /// </summary>
    public static bool IsCompatibleTruthVersion(string? actual, string current)
    {
        string normalized = Normalize(actual);
        string canonical = Normalize(current);
        return string.Equals(normalized, canonical, StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(canonical)
                && normalized.EndsWith(canonical, StringComparison.Ordinal));
    }

    public static string BuildTerminalDeathTruthEventId(string raidSessionId, string victimProfileId)
        => "terminal_death_v1|" + Normalize(raidSessionId) + "|terminal_death|" + Normalize(victimProfileId);

    public static string ComputeSourceFingerprint(VanguardCareerRaidLedgerEntry entry)
    {
        var builder = new StringBuilder();
        AppendFingerprintField(builder, "ledgerEntryId", entry.LedgerEntryId);
        AppendFingerprintField(builder, "raidSessionId", entry.RaidSessionId);
        AppendFingerprintField(builder, "ownerProfileId", entry.OwnerProfileId);
        AppendFingerprintField(builder, "operatorId", entry.OperatorId);
        AppendFingerprintField(builder, "botProfileId", entry.BotProfileId);
        AppendFingerprintField(builder, "participated", Bool(entry.Participated));
        AppendFingerprintField(builder, "aliveAtRaidEnd", Bool(entry.AliveAtRaidEnd));
        AppendFingerprintField(builder, "died", Bool(entry.Died));
        AppendFingerprintField(builder, "raidExitStatus", entry.RaidExitStatus);
        AppendFingerprintField(builder, "raidExitName", entry.RaidExitName);
        AppendFingerprintField(builder, "exitBoundarySource", entry.ExitBoundarySource);
        AppendFingerprintField(builder, "exitBoundaryProfileId", entry.ExitBoundaryProfileId);
        AppendFingerprintField(builder, "exitBoundaryDelay", entry.ExitBoundaryDelay.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        AppendFingerprintField(builder, "exitBoundaryObservedAtUtc", entry.ExitBoundaryObservedAtUtc.ToUniversalTime().ToString("O"));
        AppendFingerprintField(builder, "truthVersion", entry.TruthVersion);
        AppendFingerprintField(builder, "schemaVersion", entry.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendFingerprintField(builder, "killCount", entry.Kills.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

        foreach (VanguardCareerRaidLedgerKillEvent kill in entry.Kills.OrderBy(value => value.Ordinal).ThenBy(value => value.EventId, StringComparer.OrdinalIgnoreCase))
        {
            AppendFingerprintField(builder, "kill.eventId", kill.EventId);
            AppendFingerprintField(builder, "kill.ordinal", kill.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendFingerprintField(builder, "kill.observedAtUtc", kill.ObservedAtUtc.ToUniversalTime().ToString("O"));
            AppendFingerprintField(builder, "kill.targetProfileId", kill.TargetProfileId);
            AppendFingerprintField(builder, "kill.targetAccountId", kill.TargetAccountId);
            AppendFingerprintField(builder, "kill.targetName", kill.TargetName);
            AppendFingerprintField(builder, "kill.targetSide", kill.TargetSide);
            AppendFingerprintField(builder, "kill.targetRawRole", kill.TargetRawRole);
            AppendFingerprintField(builder, "kill.targetInfoLevel", kill.TargetInfoLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendFingerprintField(builder, "kill.targetInfoExperience", kill.TargetInfoExperience.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendFingerprintField(builder, "kill.targetSettingsExperience", kill.TargetSettingsExperience.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        AppendFingerprintField(builder, "deathPresent", Bool(entry.Death is not null));
        if (entry.Death is not null)
        {
            VanguardCareerRaidLedgerDeathEvent death = entry.Death;
            AppendFingerprintField(builder, "death.eventId", death.EventId);
            AppendFingerprintField(builder, "death.ordinal", death.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendFingerprintField(builder, "death.observedAtUtc", death.ObservedAtUtc.ToUniversalTime().ToString("O"));
            AppendFingerprintField(builder, "death.killerProfileId", death.KillerProfileId);
            AppendFingerprintField(builder, "death.killerAccountId", death.KillerAccountId);
            AppendFingerprintField(builder, "death.killerName", death.KillerName);
            AppendFingerprintField(builder, "death.killerSide", death.KillerSide);
            AppendFingerprintField(builder, "death.killerRawRole", death.KillerRawRole);
            AppendFingerprintField(builder, "death.killerInfoLevel", death.KillerInfoLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendFingerprintField(builder, "death.killerInfoExperience", death.KillerInfoExperience.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendFingerprintField(builder, "death.killerSettingsExperience", death.KillerSettingsExperience.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        AppendFingerprintField(builder, "skillCount", entry.SkillSessionPoints.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (VanguardCareerRaidLedgerSkillSessionPoint skill in entry.SkillSessionPoints.OrderBy(value => value.SkillId, StringComparer.OrdinalIgnoreCase))
        {
            AppendFingerprintField(builder, "skill.id", skill.SkillId);
            AppendFingerprintField(builder, "skill.progress", skill.Progress.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            AppendFingerprintField(builder, "skill.pointsEarnedDuringSession", skill.PointsEarnedDuringSession.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>
    /// Compares ledger content independently of the identifier prefix generation. This is used only when
    /// deciding whether a pre-normalization record and a newly generated record describe the same raid fact.
    /// The stored raw SourceFingerprint is still verified separately before this compatibility fingerprint is used.
    /// </summary>
    public static string ComputeSemanticSourceFingerprint(VanguardCareerRaidLedgerEntry entry)
    {
        VanguardCareerRaidLedgerKillEvent[] kills = entry.Kills
            .Select(kill => kill with { EventId = BuildKillEventId(entry.RaidSessionId, kill.TargetProfileId) })
            .ToArray();
        VanguardCareerRaidLedgerDeathEvent? death = entry.Death is null
            ? null
            : entry.Death with { EventId = BuildKillEventId(entry.RaidSessionId, entry.BotProfileId) };
        VanguardCareerRaidLedgerEntry canonical = entry with
        {
            LedgerEntryId = BuildLedgerEntryId(entry.RaidSessionId, entry.OwnerProfileId, entry.OperatorId),
            Kills = kills,
            Death = death,
            TruthVersion = VanguardCareerRaidLedgerSchema.TruthVersion,
            SourceFingerprint = string.Empty
        };
        return ComputeSourceFingerprint(canonical);
    }

    public static string ComputeTerminalDeathTruthFingerprint(VanguardCareerRaidLedgerTerminalDeathTruth truth)
    {
        var builder = new StringBuilder();
        AppendFingerprintField(builder, "eventId", truth.EventId);
        AppendFingerprintField(builder, "observedAtUtc", truth.ObservedAtUtc.ToUniversalTime().ToString("O"));
        AppendFingerprintField(builder, "terminalDamageType", truth.TerminalDamageType);
        AppendFingerprintField(builder, "terminalDamageTypeValue", truth.TerminalDamageTypeValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendFingerprintField(builder, "lastDamageInfoType", truth.LastDamageInfoType);
        AppendFingerprintField(builder, "lastDamageInfoTypeValue", truth.LastDamageInfoTypeValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendFingerprintField(builder, "lastDamageBodyPart", truth.LastDamageBodyPart);
        AppendFingerprintField(builder, "lastDamageBodyPartValue", truth.LastDamageBodyPartValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendFingerprintField(builder, "directKillEventObservedAtCapture", Bool(truth.DirectKillEventObservedAtCapture));
        AppendFingerprintField(builder, "lastAggressorProfileId", truth.LastAggressorProfileId);
        AppendFingerprintField(builder, "lastAggressorAccountId", truth.LastAggressorAccountId);
        AppendFingerprintField(builder, "lastAggressorName", truth.LastAggressorName);
        AppendFingerprintField(builder, "lastAggressorSide", truth.LastAggressorSide);
        AppendFingerprintField(builder, "lastAggressorRawRole", truth.LastAggressorRawRole);
        AppendFingerprintField(builder, "lastAggressorInfoLevel", truth.LastAggressorInfoLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendFingerprintField(builder, "lastAggressorInfoExperience", truth.LastAggressorInfoExperience.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendFingerprintField(builder, "lastAggressorSettingsExperience", truth.LastAggressorSettingsExperience.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendFingerprintField(builder, "source", truth.Source);
        AppendFingerprintField(builder, "truthVersion", truth.TruthVersion);
        AppendFingerprintField(builder, "schemaVersion", truth.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public static string ComputeXpKillCreditsFingerprint(IReadOnlyList<VanguardCareerRaidLedgerXpKillCredit> credits)
    {
        var builder = new StringBuilder();
        foreach (VanguardCareerRaidLedgerXpKillCredit credit in credits.OrderBy(value => value.KillSequence).ThenBy(value => value.EventId, StringComparer.OrdinalIgnoreCase))
        {
            AppendFingerprintField(builder, "eventId", credit.EventId);
            AppendFingerprintField(builder, "observedAtUtc", credit.ObservedAtUtc.ToUniversalTime().ToString("O"));
            AppendFingerprintField(builder, "xpRecipientProfileId", credit.XpRecipientProfileId);
            AppendFingerprintField(builder, "targetProfileId", credit.TargetProfileId);
            AppendFingerprintField(builder, "killSequence", credit.KillSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendFingerprintField(builder, "targetSide", credit.TargetSide);
            AppendFingerprintField(builder, "targetRawRole", credit.TargetRawRole);
            AppendFingerprintField(builder, "targetLevel", credit.TargetLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendFingerprintField(builder, "killExpInput", credit.KillExpInput.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendFingerprintField(builder, "bodyPart", credit.BodyPart);
            AppendFingerprintField(builder, "bodyPartValue", credit.BodyPartValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendFingerprintField(builder, "sameGroup", Bool(credit.SameGroup));
            AppendFingerprintField(builder, "targetIsAi", Bool(credit.TargetIsAi));
            AppendFingerprintField(builder, "xpRecipientHasMarkOfUnknown", Bool(credit.XpRecipientHasMarkOfUnknown));
            AppendFingerprintField(builder, "markPenalty", credit.MarkOfUnknownScavKillExpPenalty.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            AppendFingerprintField(builder, "calculationAvailable", Bool(credit.CalculationAvailable));
            AppendFingerprintField(builder, "awarded", Bool(credit.Awarded));
            AppendFingerprintField(builder, "calculationReason", credit.CalculationReason);
            AppendFingerprintField(builder, "baseXp", credit.BaseXp.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendFingerprintField(builder, "bodyPartBonusXp", credit.BodyPartBonusXp.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendFingerprintField(builder, "streakBonusXp", credit.StreakBonusXp.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendFingerprintField(builder, "killXpSubtotal", credit.KillXpSubtotal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendFingerprintField(builder, "source", credit.Source);
            AppendFingerprintField(builder, "truthVersion", credit.TruthVersion);
            AppendFingerprintField(builder, "schemaVersion", credit.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public static string ComputeSemanticTerminalDeathTruthFingerprint(
        VanguardCareerRaidLedgerTerminalDeathTruth truth,
        string raidSessionId,
        string victimProfileId)
        => ComputeTerminalDeathTruthFingerprint(truth with
        {
            EventId = BuildTerminalDeathTruthEventId(raidSessionId, victimProfileId),
            TruthVersion = VanguardCareerTerminalDeathTruthSchema.TruthVersion
        });

    public static string ComputeSemanticXpKillCreditsFingerprint(
        IReadOnlyList<VanguardCareerRaidLedgerXpKillCredit> credits,
        string raidSessionId)
    {
        VanguardCareerRaidLedgerXpKillCredit[] canonical = credits
            .Select(credit => credit with
            {
                EventId = BuildXpKillCreditEventId(raidSessionId, credit.XpRecipientProfileId, credit.TargetProfileId),
                TruthVersion = VanguardCareerXpKillCreditTruthSchema.TruthVersion
            })
            .ToArray();
        return ComputeXpKillCreditsFingerprint(canonical);
    }

    private static bool HasSemanticIdentity(string? actual, int partCount, params (int Index, string Value)[] expected)
    {
        string normalized = Normalize(actual);
        string[] parts = normalized.Split('|');
        if (parts.Length != partCount || string.IsNullOrWhiteSpace(parts[0])) return false;
        foreach ((int index, string value) in expected)
        {
            if (!string.Equals(parts[index], value, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static void AppendFingerprintField(StringBuilder builder, string name, string? value)
    {
        string normalized = value ?? string.Empty;
        builder.Append(name)
            .Append('#')
            .Append(normalized.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(':')
            .Append(normalized)
            .Append('|');
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string Bool(bool value) => value ? "true" : "false";
}
