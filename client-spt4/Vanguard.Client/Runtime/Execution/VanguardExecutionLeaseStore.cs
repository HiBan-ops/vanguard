#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vanguard.Client.Runtime.Medical;

// Responsibility: Holds the short-lived execution leases that make mutually exclusive Operator actions explicit and cancellable.
// Flow: Schedulers open generation-stamped leases with domain/priority/timeout metadata; executors read/update progress and all terminal paths close the same lease state.
// Authority boundary: The store records authority granted by schedulers but does not decide eligibility itself and does not perform the physical action.
// Invariant: One incompatible action domain cannot silently coexist with another, stale generations cannot reclaim authority, and terminal metadata survives only as long as needed for safe reconciliation.
namespace Vanguard.Client.Runtime.Execution;

internal static class VanguardExecutionLeaseStore
{
    public const string AtomicReplacementStatusTag = "VANGUARD_ATOMIC_MEDICAL_TERMINAL_STATUS";
    public const string MedicalEffectCircuitBreakerStatusTag = "VANGUARD_MEDICAL_EFFECT_CIRCUIT_BREAKER_STATUS";
    private const int DefaultEffectCircuitFailureThreshold = 2;
    private const int BleedEffectCircuitFailureThreshold = 1;
    public const string StateBoundOutcomeStatusTag = "VANGUARD_MEDICAL_STATE_BOUND_OUTCOME_STATUS";
    private static readonly object Sync = new();
    private static readonly Dictionary<string, VanguardExecutionLeaseState> ActiveByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> CooldownUntilByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, VanguardExecutionOutcomeMemoryRecord> OutcomeByKey = new(StringComparer.OrdinalIgnoreCase);

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            ActiveByBotProfileId.Clear();
            CooldownUntilByKey.Clear();
            OutcomeByKey.Clear();
        }
    }

    public static IReadOnlyList<VanguardExecutionLeaseState> GetActiveLeases()
    {
        lock (Sync)
        {
            return ActiveByBotProfileId.Values.ToArray();
        }
    }

    public static void CopyActiveLeasesTo(List<VanguardExecutionLeaseState> destination)
    {
        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        lock (Sync)
        {
            destination.Clear();
            foreach (var lease in ActiveByBotProfileId.Values)
            {
                destination.Add(lease);
            }
        }
    }

    public static bool TryGetActive(string? botProfileId, out VanguardExecutionLeaseState lease)
    {
        string key = Normalize(botProfileId);
        lock (Sync)
        {
            if (ActiveByBotProfileId.TryGetValue(key, out var found))
            {
                lease = found;
                return true;
            }
        }

        lease = null!;
        return false;
    }

    public static bool TryStart(VanguardExecutionLeaseState lease)
    {
        if (lease == null || string.IsNullOrWhiteSpace(lease.BotProfileId))
        {
            return false;
        }

        lock (Sync)
        {
            if (ActiveByBotProfileId.ContainsKey(lease.BotProfileId))
            {
                return false;
            }

            ActiveByBotProfileId[lease.BotProfileId] = lease;
            return true;
        }
    }

    public static bool TryReplace(string? botProfileId, string? expectedLeaseId, VanguardExecutionLeaseState nextLease)
    {
        string key = Normalize(botProfileId);
        string expected = Normalize(expectedLeaseId);
        if (nextLease == null || string.IsNullOrWhiteSpace(nextLease.BotProfileId) || !string.Equals(key, Normalize(nextLease.BotProfileId), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (Sync)
        {
            if (!ActiveByBotProfileId.TryGetValue(key, out var current)
                || !string.Equals(Normalize(current.LeaseId), expected, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            ActiveByBotProfileId[key] = nextLease;
            return true;
        }
    }

    public static void Release(string? botProfileId)
    {
        string key = Normalize(botProfileId);
        lock (Sync)
        {
            ActiveByBotProfileId.Remove(key);
        }
    }

    public static bool IsCooldownBlocked(string? botProfileId, VanguardMedicalNeed need, string? targetPart, string? itemTemplateId, DateTimeOffset now, out DateTimeOffset untilUtc)
    {
        return IsCooldownBlocked(botProfileId, need, targetPart, itemTemplateId, null, now, out untilUtc);
    }

    public static bool IsCooldownBlocked(string? botProfileId, VanguardMedicalNeed need, string? targetPart, string? itemTemplateId, string? itemInstanceId, DateTimeOffset now, out DateTimeOffset untilUtc)
    {
        string exactKey = BuildCooldownKey(botProfileId, need, targetPart, itemTemplateId, itemInstanceId);
        string templateKey = BuildCooldownKey(botProfileId, need, targetPart, itemTemplateId);
        lock (Sync)
        {
            if (CooldownUntilByKey.TryGetValue(exactKey, out untilUtc) && untilUtc > now)
            {
                return true;
            }

            // Pre-start/controller failures can be recorded before a stable item instance is
            // available. Keep that template-level cooldown authoritative as a bounded admission
            // gate, while effect/no-effect memory remains exact-instance and state-bound.
            if (!string.Equals(exactKey, templateKey, StringComparison.OrdinalIgnoreCase)
                && CooldownUntilByKey.TryGetValue(templateKey, out untilUtc)
                && untilUtc > now)
            {
                return true;
            }
        }

        untilUtc = DateTimeOffset.MinValue;
        return false;
    }

    public static bool IsEffectCircuitBlocked(
        string? botProfileId,
        VanguardMedicalNeed need,
        string? targetPart,
        string? itemTemplateId,
        int healthPercent,
        float targetHealth,
        float targetMaxHealth,
        DateTimeOffset now,
        out VanguardExecutionOutcomeMemoryRecord outcome)
    {
        return IsEffectCircuitBlocked(botProfileId, need, targetPart, itemTemplateId, null, -1f, -1f,
            healthPercent, targetHealth, targetMaxHealth, now, out outcome);
    }

    public static bool IsEffectCircuitBlocked(
        string? botProfileId,
        VanguardMedicalNeed need,
        string? targetPart,
        string? itemTemplateId,
        string? itemInstanceId,
        float itemResource,
        float itemMaxResource,
        int healthPercent,
        float targetHealth,
        float targetMaxHealth,
        DateTimeOffset now,
        out VanguardExecutionOutcomeMemoryRecord outcome)
    {
        string key = BuildCooldownKey(botProfileId, need, targetPart, itemTemplateId, itemInstanceId);
        string signature = BuildEffectSignature(botProfileId, need, targetPart, itemTemplateId, itemInstanceId,
            itemResource, itemMaxResource, healthPercent, targetHealth, targetMaxHealth);
        lock (Sync)
        {
            if (OutcomeByKey.TryGetValue(key, out var found) && found.CircuitBreakerArmed)
            {
                if (string.Equals(found.EffectSignature, signature, StringComparison.OrdinalIgnoreCase))
                {
                    outcome = found;
                    return true;
                }

                // The circuit is state-bound, not timer-bound. A real HP/target/item-resource
                // transition releases it immediately; elapsed wall-clock time alone never does.
                OutcomeByKey[key] = found.WithStateChangeReleased(signature, now);
            }
        }

        outcome = null!;
        return false;
    }

    public static void RegisterOutcome(string? botProfileId, VanguardMedicalNeed need, string? targetPart, string? itemTemplateId, DateTimeOffset retryAllowedAtUtc)
    {
        RegisterOutcomeDetailed(botProfileId, need, targetPart, itemTemplateId, "unknown", "legacy_register_outcome", "none", retryAllowedAtUtc);
    }

    public static void RegisterOutcomeDetailed(string? botProfileId, VanguardMedicalNeed need, string? targetPart, string? itemTemplateId, string outcome, string reason, string progressKind, DateTimeOffset retryAllowedAtUtc)
    {
        string key = BuildCooldownKey(botProfileId, need, targetPart, itemTemplateId);
        lock (Sync)
        {
            // Legacy/non-terminal callers are not allowed to erase state-bound circuit metadata.
            // The circuit deadline is deliberately kept separate from the generic retry cooldown:
            // a real medical-state change must release the circuit without waiting for its old timer.
            OutcomeByKey.TryGetValue(key, out var previous);
            DateTimeOffset effectiveRetry = retryAllowedAtUtc;

            CooldownUntilByKey[key] = effectiveRetry;
            OutcomeByKey[key] = new VanguardExecutionOutcomeMemoryRecord
            {
                Key = key,
                BotProfileId = Normalize(botProfileId),
                Need = need,
                TargetPart = Normalize(targetPart),
                ItemTemplateId = Normalize(itemTemplateId),
                ItemInstanceId = previous?.ItemInstanceId ?? "none",
                InitialItemResource = previous?.InitialItemResource ?? -1f,
                InitialItemMaxResource = previous?.InitialItemMaxResource ?? -1f,
                Outcome = Safe(outcome),
                Reason = Safe(reason),
                ProgressKind = Safe(progressKind),
                RecordedAtUtc = DateTimeOffset.UtcNow,
                RetryAllowedAtUtc = effectiveRetry,
                EffectSignature = previous?.EffectSignature ?? "none",
                ConsecutiveNoEffectCount = previous?.ConsecutiveNoEffectCount ?? 0,
                CircuitBreakerArmed = previous?.CircuitBreakerArmed ?? false,
                CircuitBreakerUntilUtc = previous?.CircuitBreakerUntilUtc ?? DateTimeOffset.MinValue
            };
        }
    }

    public static VanguardExecutionOutcomeMemoryRecord RegisterLeaseOutcomeDetailed(
        VanguardExecutionLeaseState lease,
        string outcome,
        string reason,
        string progressKind,
        DateTimeOffset now,
        DateTimeOffset retryAllowedAtUtc,
        bool countAsNoEffect,
        bool medicalEffectSucceeded)
    {
        string key = BuildCooldownKey(lease.BotProfileId, lease.MedicalNeed, lease.TargetPart, lease.ItemTemplateId, lease.ItemInstanceId);
        string signature = string.IsNullOrWhiteSpace(lease.EffectSignature) || string.Equals(lease.EffectSignature, "none", StringComparison.OrdinalIgnoreCase)
            ? BuildEffectSignature(lease.BotProfileId, lease.MedicalNeed, lease.TargetPart, lease.ItemTemplateId, lease.ItemInstanceId, lease.InitialItemResource, lease.InitialItemMaxResource, lease.InitialHealthPercent, lease.InitialTargetHealth, lease.InitialTargetMaxHealth)
            : lease.EffectSignature;

        lock (Sync)
        {
            OutcomeByKey.TryGetValue(key, out var previous);
            int noEffectCount = 0;
            bool circuitArmed = false;
            DateTimeOffset circuitUntil = DateTimeOffset.MinValue;

            if (medicalEffectSucceeded)
            {
                noEffectCount = 0;
            }
            else if (CanCircuitBreak(lease.MedicalNeed) && countAsNoEffect)
            {
                bool sameState = previous != null && string.Equals(previous.EffectSignature, signature, StringComparison.OrdinalIgnoreCase);
                noEffectCount = sameState ? Math.Max(0, previous!.ConsecutiveNoEffectCount) + 1 : 1;
                if (noEffectCount >= EffectCircuitFailureThresholdFor(lease.MedicalNeed))
                {
                    circuitArmed = true;
                    circuitUntil = DateTimeOffset.MaxValue;
                }
            }
            else if (previous != null && string.Equals(previous.EffectSignature, signature, StringComparison.OrdinalIgnoreCase))
            {
                // Interruptions and authority deferrals neither advance nor erase an existing no-effect sequence.
                noEffectCount = previous.ConsecutiveNoEffectCount;
                circuitArmed = previous.CircuitBreakerArmed;
                circuitUntil = circuitArmed ? DateTimeOffset.MaxValue : DateTimeOffset.MinValue;
            }

            // Keep ordinary retry cadence independent from the state-bound circuit. The admission
            // path evaluates the current effect signature after this short cooldown; changed HP,
            // target or item state therefore re-enables treatment immediately and safely.
            DateTimeOffset effectiveRetry = retryAllowedAtUtc;

            var record = new VanguardExecutionOutcomeMemoryRecord
            {
                Key = key,
                BotProfileId = Normalize(lease.BotProfileId),
                Need = lease.MedicalNeed,
                TargetPart = Normalize(lease.TargetPart),
                ItemTemplateId = Normalize(lease.ItemTemplateId),
                ItemInstanceId = Normalize(lease.ItemInstanceId),
                InitialItemResource = lease.InitialItemResource,
                InitialItemMaxResource = lease.InitialItemMaxResource,
                Outcome = Safe(outcome),
                Reason = Safe(reason),
                ProgressKind = Safe(progressKind),
                RecordedAtUtc = now,
                RetryAllowedAtUtc = effectiveRetry,
                EffectSignature = signature,
                ConsecutiveNoEffectCount = noEffectCount,
                CircuitBreakerArmed = circuitArmed,
                CircuitBreakerUntilUtc = circuitUntil
            };
            CooldownUntilByKey[key] = effectiveRetry;
            OutcomeByKey[key] = record;
            if (UsesTemplateWideCooldown(lease.MedicalNeed))
            {
                // Surgery admission is evaluated before the exact kit instance is selected. Mirror
                // only stationary surgery cooldowns to the template key; mobile/fracture alternatives
                // remain instance-scoped so one faulty item cannot suppress another viable item.
                string templateKey = BuildCooldownKey(lease.BotProfileId, lease.MedicalNeed, lease.TargetPart, lease.ItemTemplateId);
                CooldownUntilByKey[templateKey] = effectiveRetry;
            }
            return record;
        }
    }

    public static bool TryGetOutcome(string? botProfileId, VanguardMedicalNeed need, string? targetPart, string? itemTemplateId, out VanguardExecutionOutcomeMemoryRecord outcome)
    {
        string key = BuildCooldownKey(botProfileId, need, targetPart, itemTemplateId);
        lock (Sync)
        {
            if (OutcomeByKey.TryGetValue(key, out var found))
            {
                outcome = found;
                return true;
            }
        }

        outcome = null!;
        return false;
    }

    public static string BuildCooldownKey(string? botProfileId, VanguardMedicalNeed need, string? targetPart, string? itemTemplateId, string? itemInstanceId = null)
    {
        return Normalize(botProfileId) + "|" + need + "|" + Normalize(targetPart) + "|" + Normalize(itemTemplateId)
            + "|instance=" + Normalize(itemInstanceId);
    }

    public static string BuildEffectSignature(string? botProfileId, VanguardMedicalNeed need, string? targetPart, string? itemTemplateId, int healthPercent, float targetHealth, float targetMaxHealth)
    {
        return BuildEffectSignature(botProfileId, need, targetPart, itemTemplateId, null, -1f, -1f,
            healthPercent, targetHealth, targetMaxHealth);
    }

    public static string BuildEffectSignature(string? botProfileId, VanguardMedicalNeed need, string? targetPart, string? itemTemplateId,
        string? itemInstanceId, float itemResource, float itemMaxResource, int healthPercent, float targetHealth, float targetMaxHealth)
    {
        string exactActionKey = BuildCooldownKey(botProfileId, need, targetPart, itemTemplateId, itemInstanceId);
        if (need == VanguardMedicalNeed.HeavyBleed || need == VanguardMedicalNeed.LightBleed)
        {
            // HP and item-resource telemetry are not a reliable episode boundary for a still-active
            // bleed: HP keeps falling while some remote/Fika item resources are unreadable. Keeping
            // the signature stable prevents that background loss from re-enabling the same failed
            // native action every snapshot. A successful treatment explicitly clears the record.
            return exactActionKey + "|bleedEpisode=unresolved";
        }

        return exactActionKey
            + "|itemResource=" + FloatSignature(itemResource) + "/" + FloatSignature(itemMaxResource)
            + "|hp=" + healthPercent.ToString(CultureInfo.InvariantCulture)
            + "|target=" + FloatSignature(targetHealth)
            + "/" + FloatSignature(targetMaxHealth);
    }

    private static bool CanCircuitBreak(VanguardMedicalNeed need)
    {
        // Urgency cannot justify replaying a native controller cycle that already completed with
        // no physical effect. The key is exact-item-instance and state-bound, so another viable
        // hemostatic item remains selectable while surgery keeps its dedicated debt lifecycle.
        return need == VanguardMedicalNeed.HeavyBleed
            || need == VanguardMedicalNeed.LightBleed
            || need == VanguardMedicalNeed.HpHeal
            || need == VanguardMedicalNeed.Fracture;
    }

    private static int EffectCircuitFailureThresholdFor(VanguardMedicalNeed need)
    {
        return need == VanguardMedicalNeed.HeavyBleed || need == VanguardMedicalNeed.LightBleed
            ? BleedEffectCircuitFailureThreshold
            : DefaultEffectCircuitFailureThreshold;
    }

    private static bool UsesTemplateWideCooldown(VanguardMedicalNeed need)
    {
        return need == VanguardMedicalNeed.SurgeryDestroyedPart || need == VanguardMedicalNeed.BlackBroken;
    }

    private static string FloatSignature(float value)
    {
        return value < 0f ? "unknown" : value.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().ToLowerInvariant();
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }
}

internal sealed class VanguardExecutionOutcomeMemoryRecord
{
    public string Key { get; init; } = "none";
    public string BotProfileId { get; init; } = "none";
    public VanguardMedicalNeed Need { get; init; } = VanguardMedicalNeed.None;
    public string TargetPart { get; init; } = "none";
    public string ItemTemplateId { get; init; } = "none";
    public string ItemInstanceId { get; init; } = "none";
    public float InitialItemResource { get; init; } = -1f;
    public float InitialItemMaxResource { get; init; } = -1f;
    public string Outcome { get; init; } = "none";
    public string Reason { get; init; } = "none";
    public string ProgressKind { get; init; } = "none";
    public DateTimeOffset RecordedAtUtc { get; init; } = DateTimeOffset.MinValue;
    public DateTimeOffset RetryAllowedAtUtc { get; init; } = DateTimeOffset.MinValue;
    public string EffectSignature { get; init; } = "none";
    public int ConsecutiveNoEffectCount { get; init; }
    public bool CircuitBreakerArmed { get; init; }
    public DateTimeOffset CircuitBreakerUntilUtc { get; init; } = DateTimeOffset.MinValue;

    public VanguardExecutionOutcomeMemoryRecord WithStateChangeReleased(string nextSignature, DateTimeOffset now)
    {
        return new VanguardExecutionOutcomeMemoryRecord
        {
            Key = Key,
            BotProfileId = BotProfileId,
            Need = Need,
            TargetPart = TargetPart,
            ItemTemplateId = ItemTemplateId,
            ItemInstanceId = ItemInstanceId,
            InitialItemResource = InitialItemResource,
            InitialItemMaxResource = InitialItemMaxResource,
            Outcome = "state_changed",
            Reason = "effect_signature_changed",
            ProgressKind = "state_change_released_circuit",
            RecordedAtUtc = now,
            RetryAllowedAtUtc = DateTimeOffset.MinValue,
            EffectSignature = nextSignature,
            ConsecutiveNoEffectCount = 0,
            CircuitBreakerArmed = false,
            CircuitBreakerUntilUtc = DateTimeOffset.MinValue
        };
    }

    public string Summary => "outcome=" + Outcome
        + ";reason=" + Reason
        + ";progress=" + ProgressKind
        + ";retryAt=" + RetryAllowedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        + ";itemInstance=" + ItemInstanceId
        + ";itemResource0=" + InitialItemResource.ToString("0.0", CultureInfo.InvariantCulture) + "/" + InitialItemMaxResource.ToString("0.0", CultureInfo.InvariantCulture)
        + ";effectSignature=" + EffectSignature
        + ";noEffectCount=" + ConsecutiveNoEffectCount.ToString(CultureInfo.InvariantCulture)
        + ";circuitArmed=" + (CircuitBreakerArmed ? "true" : "false")
        + ";circuitUntil=" + CircuitBreakerUntilUtc.ToString("O", CultureInfo.InvariantCulture);
}
#endif
