#if SPT_CLIENT
using System;
using System.Collections.Generic;

// Responsibility: Maintains the bounded state used by Fika Hud Medical Telemetry Store in the Fika raid-runtime transport.
// Flow: Writers update normalized entries, readers query a stable view, and lifecycle/reset hooks clear or reconcile data at the appropriate boundary.
// Authority boundary: State cache/registry only; persistent or physical truth remains owned by the designated server/game subsystem unless explicitly documented otherwise.
// Invariant: Entries are scoped to their owner/raid/profile and stale state must be removable without forcing gameplay mutation.
namespace Vanguard.Client.Raid.Runtime.Fika;

internal static class VanguardFikaHudMedicalTelemetryStore
{
    private const int MaxEntriesPerFrame = 16;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, VanguardFikaHudMedicalTelemetryReceivedEntry> ByBotProfileId = new(StringComparer.OrdinalIgnoreCase);

    private static string currentSessionId = string.Empty;
    private static long lastSequence = -1;

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            ByBotProfileId.Clear();
            currentSessionId = string.Empty;
            lastSequence = -1;
        }
    }

    public static bool TryApply(VanguardFikaHudMedicalTelemetryPayload payload, DateTimeOffset receivedAtUtc, out string reason)
    {
        reason = string.Empty;
        if (payload.ProtocolVersion != VanguardFikaHudTelemetryService.MedicalProtocolVersion)
        {
            reason = $"protocol_mismatch:{payload.ProtocolVersion}";
            return false;
        }

        if (!string.Equals(payload.BuildLabel, VanguardBuildVersion.BuildLabel, StringComparison.Ordinal))
        {
            reason = "build_label_mismatch";
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload.SessionId) || payload.SessionId.Length > 64)
        {
            reason = "invalid_session";
            return false;
        }

        var entries = payload.Entries ?? Array.Empty<VanguardFikaHudMedicalTelemetryEntry>();
        if (entries.Length > MaxEntriesPerFrame)
        {
            reason = $"too_many_entries:{entries.Length}";
            return false;
        }

        DateTimeOffset sourceSentAtUtc;
        try
        {
            sourceSentAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(payload.SentAtUnixMilliseconds);
        }
        catch
        {
            reason = "invalid_source_timestamp";
            return false;
        }

        bool appliedAny = false;
        lock (Sync)
        {
            bool newSession = !string.Equals(currentSessionId, payload.SessionId, StringComparison.Ordinal);
            if (newSession)
            {
                ByBotProfileId.Clear();
                currentSessionId = payload.SessionId;
                lastSequence = -1;
            }

            foreach (var entry in entries)
            {
                if (!IsValid(entry))
                {
                    continue;
                }

                if (ByBotProfileId.TryGetValue(entry.BotProfileId, out VanguardFikaHudMedicalTelemetryReceivedEntry existing)
                    && string.Equals(existing.SessionId, payload.SessionId, StringComparison.Ordinal)
                    && payload.Sequence <= existing.Sequence)
                {
                    continue;
                }

                ByBotProfileId[entry.BotProfileId] = new VanguardFikaHudMedicalTelemetryReceivedEntry(
                    entry.BotProfileId,
                    entry.MedicalMask,
                    receivedAtUtc,
                    sourceSentAtUtc,
                    payload.Sequence,
                    payload.SessionId);
                appliedAny = true;
            }

            lastSequence = Math.Max(lastSequence, payload.Sequence);
        }

        reason = appliedAny ? "accepted" : "duplicate_or_out_of_order";
        return appliedAny;
    }

    public static bool TryGetFreshState(string botProfileId, DateTimeOffset now, TimeSpan staleAfter, out VanguardFikaHudMedicalState state)
    {
        state = null!;
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return false;
        }

        VanguardFikaHudMedicalTelemetryReceivedEntry received;
        lock (Sync)
        {
            if (!ByBotProfileId.TryGetValue(botProfileId, out received!))
            {
                return false;
            }
        }

        TimeSpan age = now - received.ReceivedAtUtc;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age > staleAfter)
        {
            return false;
        }

        byte mask = received.MedicalMask;
        state = new VanguardFikaHudMedicalState(
            (mask & VanguardFikaHudMedicalMask.Readable) != 0,
            (mask & VanguardFikaHudMedicalMask.HeavyBleed) != 0,
            (mask & VanguardFikaHudMedicalMask.LightBleed) != 0,
            (mask & VanguardFikaHudMedicalMask.Fracture) != 0,
            (mask & VanguardFikaHudMedicalMask.Pain) != 0,
            (mask & VanguardFikaHudMedicalMask.Tremor) != 0,
            received.ReceivedAtUtc,
            received.Sequence);
        return true;
    }

    public static int Count
    {
        get
        {
            lock (Sync)
            {
                return ByBotProfileId.Count;
            }
        }
    }

    private static bool IsValid(VanguardFikaHudMedicalTelemetryEntry entry)
    {
        return entry is not null
            && !string.IsNullOrWhiteSpace(entry.BotProfileId)
            && entry.BotProfileId.Length <= 96
            && (entry.MedicalMask & ~VanguardFikaHudMedicalMask.KnownBits) == 0;
    }
}
#endif
