#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;

// Responsibility: Provides Corpse Loot Outcome Memory support for the loot runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Loot;

/// <summary>
/// The persistence path removes the legacy owner/corpse raid-terminal. Failure cooldowns remain Operator/corpse scoped.
/// A corpse can additionally be exhausted for one Operator only for one exact read-model context:
/// manifest revision + owner interest revision + medical/inventory need signature. Any relevant mutation
/// naturally invalidates the exhaustion record and allows the squad to re-evaluate the corpse.
/// </summary>
internal static class VanguardCorpseLootOutcomeMemory
{
    private sealed class FailureRecord
    {
        public DateTimeOffset RetryAtUtc;
        public string Outcome = "none";
        public string Reason = "none";
    }

    private sealed class ExhaustionRecord
    {
        public long ManifestRevision;
        public long InterestRevision;
        public string NeedSignature = "none";
        public string Reason = "none";
        public DateTimeOffset RecordedAtUtc;
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, FailureRecord> FailuresByBotAndCorpse = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ExhaustionRecord> ExhaustedByBotAndCorpse = new(StringComparer.OrdinalIgnoreCase);

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            FailuresByBotAndCorpse.Clear();
            ExhaustedByBotAndCorpse.Clear();
        }
    }

    public static bool CanStart(
        string ownerProfileId,
        string botProfileId,
        string corpseId,
        DateTimeOffset now,
        out string reason)
        => CanStartContext(ownerProfileId, botProfileId, corpseId, now, 0, 0, "none", out reason);

    public static bool CanStartContext(
        string ownerProfileId,
        string botProfileId,
        string corpseId,
        DateTimeOffset now,
        long manifestRevision,
        long interestRevision,
        string needSignature,
        out string reason)
    {
        if (!TryBuildKey(botProfileId, corpseId, out string key))
        {
            reason = "invalid_operator_or_corpse_identity";
            return false;
        }

        lock (Sync)
        {
            if (FailuresByBotAndCorpse.TryGetValue(key, out FailureRecord? failure))
            {
                if (failure.RetryAtUtc > now)
                {
                    reason = string.Join(":",
                        "operator_retry_cooldown",
                        (failure.RetryAtUtc - now).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture),
                        Safe(failure.Reason));
                    return false;
                }
                FailuresByBotAndCorpse.Remove(key);
            }

            if (manifestRevision > 0
                && ExhaustedByBotAndCorpse.TryGetValue(key, out ExhaustionRecord? exhausted)
                && exhausted.ManifestRevision == manifestRevision
                && exhausted.InterestRevision == interestRevision
                && string.Equals(exhausted.NeedSignature, NormalizeSignature(needSignature), StringComparison.Ordinal))
            {
                reason = "operator_context_exhausted:manifest=" + manifestRevision
                    + ":interest=" + interestRevision
                    + ":reason=" + Safe(exhausted.Reason);
                return false;
            }
        }

        reason = manifestRevision > 0 ? "context_available" : "no_previous_failure";
        return true;
    }

    /// <summary>
    /// Compatibility entry point used by legacy failure paths. The persistence path deliberately suppresses the former
    /// terminal=true owner-squad behavior. True exhaustion must be recorded with RecordExhaustedContext.
    /// </summary>
    public static bool Record(
        string ownerProfileId,
        string operatorId,
        string botProfileId,
        string corpseId,
        DateTimeOffset now,
        string outcome,
        string reason,
        bool terminal,
        float cooldownSeconds,
        out string recordScope)
    {
        if (!TryBuildKey(botProfileId, corpseId, out string key))
        {
            recordScope = "record_rejected_invalid_operator_or_corpse_identity";
            return false;
        }

        lock (Sync)
        {
            if (terminal)
            {
                // Compatibility rule: never recreate the obsolete squad-wide raid terminal from a legacy caller.
                recordScope = "legacy_terminal_suppressed";
                return false;
            }

            FailuresByBotAndCorpse[key] = new FailureRecord
            {
                RetryAtUtc = now + TimeSpan.FromSeconds(Math.Max(1f, cooldownSeconds)),
                Outcome = Safe(outcome),
                Reason = Safe(reason)
            };
            recordScope = "operator_retry_recorded";
            return false;
        }
    }

    public static bool RecordExhaustedContext(
        string botProfileId,
        string corpseId,
        long manifestRevision,
        long interestRevision,
        string needSignature,
        DateTimeOffset now,
        string reason,
        out string recordScope)
    {
        if (!TryBuildKey(botProfileId, corpseId, out string key) || manifestRevision <= 0)
        {
            recordScope = "context_exhaustion_rejected_missing_identity_or_revision";
            return false;
        }

        lock (Sync)
        {
            ExhaustedByBotAndCorpse[key] = new ExhaustionRecord
            {
                ManifestRevision = manifestRevision,
                InterestRevision = interestRevision,
                NeedSignature = NormalizeSignature(needSignature),
                Reason = Safe(reason),
                RecordedAtUtc = now
            };
            recordScope = "operator_context_exhaustion_recorded";
            return true;
        }
    }

    public static void ClearContextExhaustion(string botProfileId, string corpseId, string reason)
    {
        if (!TryBuildKey(botProfileId, corpseId, out string key)) return;
        lock (Sync) ExhaustedByBotAndCorpse.Remove(key);
    }

    private static bool TryBuildKey(string? botProfileId, string? corpseId, out string key)
    {
        string bot = Normalize(botProfileId);
        string corpse = Normalize(corpseId);
        if (bot == "none" || corpse == "none")
        {
            key = string.Empty;
            return false;
        }
        key = bot + "|" + corpse;
        return true;
    }

    private static string NormalizeSignature(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
