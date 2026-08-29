#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Runtime.Execution;

// Responsibility: Provides Medical Execution Result Bridge support for the medical runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Medical.Execution;

/// <summary>
/// Transfers the terminal result produced by the medical executor to the scheduler-owned primary
/// window. Records are keyed by bot and lease so sequential procedures cannot overwrite each other,
/// and a lease mismatch cannot consume a valid terminal result belonging to a later step.
/// </summary>
internal static class VanguardMedicalExecutionResultBridge
{
    public const string StatusTag = "VANGUARD_MEDICAL_OUTCOME_TRUTH_STATUS";
    private static readonly object Sync = new();
    private static readonly Dictionary<string, MedicalExecutionTerminalRecord> ByBotAndLease = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(20.0d);

    public static void Reset()
    {
        lock (Sync)
        {
            ByBotAndLease.Clear();
        }
    }

    public static void Publish(VanguardExecutionLeaseState lease, VanguardMedicalActionOutcomeKind outcome, string reason, string backendSummary, DateTimeOffset now)
    {
        if (lease == null || string.IsNullOrWhiteSpace(lease.BotProfileId) || string.IsNullOrWhiteSpace(lease.LeaseId))
        {
            return;
        }

        var record = new MedicalExecutionTerminalRecord(
            lease.BotProfileId,
            lease.LeaseId,
            lease.WindowKind,
            lease.IntentKey,
            outcome.ToString(),
            reason,
            backendSummary,
            now,
            now + Retention);
        lock (Sync)
        {
            PurgeExpiredUnsafe(now);
            ByBotAndLease[BuildKey(lease.BotProfileId, lease.LeaseId)] = record;
        }
    }

    public static bool TryConsume(string? botProfileId, string? expectedLeaseId, DateTimeOffset now, out MedicalExecutionTerminalRecord record)
    {
        string bot = Normalize(botProfileId);
        string lease = Normalize(expectedLeaseId);
        lock (Sync)
        {
            PurgeExpiredUnsafe(now);
            if (!string.Equals(bot, "none", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(lease, "none", StringComparison.OrdinalIgnoreCase)
                && ByBotAndLease.TryGetValue(BuildKey(bot, lease), out record))
            {
                ByBotAndLease.Remove(BuildKey(bot, lease));
                return record.ExpiresAtUtc > now;
            }
        }

        record = default;
        return false;
    }

    private static void PurgeExpiredUnsafe(DateTimeOffset now)
    {
        foreach (string key in ByBotAndLease
            .Where(pair => pair.Value.ExpiresAtUtc <= now)
            .Select(pair => pair.Key)
            .ToArray())
        {
            ByBotAndLease.Remove(key);
        }
    }

    private static string BuildKey(string? botProfileId, string? leaseId) => Normalize(botProfileId) + "|" + Normalize(leaseId);
    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
}

internal readonly struct MedicalExecutionTerminalRecord
{
    public MedicalExecutionTerminalRecord(string botProfileId, string leaseId, string windowKind, string intentKey, string outcome, string reason, string backendSummary, DateTimeOffset recordedAtUtc, DateTimeOffset expiresAtUtc)
    {
        BotProfileId = botProfileId;
        LeaseId = leaseId;
        WindowKind = windowKind;
        IntentKey = intentKey;
        Outcome = outcome;
        Reason = reason;
        BackendSummary = backendSummary;
        RecordedAtUtc = recordedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public string BotProfileId { get; }
    public string LeaseId { get; }
    public string WindowKind { get; }
    public string IntentKey { get; }
    public string Outcome { get; }
    public string Reason { get; }
    public string BackendSummary { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
}
#endif
