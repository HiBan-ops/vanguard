#if SPT_CLIENT
using System;
using System.Collections.Generic;

// Responsibility: Maintains the bounded state used by Fika Hud Telemetry Store in the Fika raid-runtime transport.
// Flow: Writers update normalized entries, readers query a stable view, and lifecycle/reset hooks clear or reconcile data at the appropriate boundary.
// Authority boundary: State cache/registry only; persistent or physical truth remains owned by the designated server/game subsystem unless explicitly documented otherwise.
// Invariant: Entries are scoped to their owner/raid/profile and stale state must be removable without forcing gameplay mutation.
namespace Vanguard.Client.Raid.Runtime.Fika;

internal static class VanguardFikaHudTelemetryStore
{
    private const int MaxEntriesPerFrame = 16;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, VanguardFikaHudTelemetryReceivedEntry> ByBotProfileId = new(StringComparer.OrdinalIgnoreCase);

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

    public static bool TryApply(VanguardFikaHudTelemetryPayload payload, DateTimeOffset receivedAtUtc, out string reason)
    {
        reason = string.Empty;
        if (payload.ProtocolVersion != VanguardFikaHudTelemetryService.ProtocolVersion)
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

        var entries = payload.Entries ?? Array.Empty<VanguardFikaHudTelemetryEntry>();
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

                // Size-safety splitting can emit independent subsets with successive sequences.
                // Accept them even if network delivery reorders subsets; never let an older subset
                // overwrite a newer value for the same Operator.
                if (ByBotProfileId.TryGetValue(entry.BotProfileId, out VanguardFikaHudTelemetryReceivedEntry existing)
                    && string.Equals(existing.SessionId, payload.SessionId, StringComparison.Ordinal)
                    && payload.Sequence <= existing.Sequence)
                {
                    continue;
                }

                ByBotProfileId[entry.BotProfileId] = new VanguardFikaHudTelemetryReceivedEntry(
                    Clone(entry),
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

    public static bool TryGet(string botProfileId, out VanguardFikaHudTelemetryReceivedEntry entry)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            entry = null!;
            return false;
        }

        lock (Sync)
        {
            return ByBotProfileId.TryGetValue(botProfileId, out entry!);
        }
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

    private static bool IsValid(VanguardFikaHudTelemetryEntry entry)
    {
        return entry is not null
            && !string.IsNullOrWhiteSpace(entry.BotProfileId)
            && entry.BotProfileId.Length <= 96
            && entry.ActivityLabel.Length <= 64
            && entry.AlertLabel.Length <= 64
            && entry.Detail.Length <= 192
            && entry.AlertSeverity >= 0
            && entry.AlertSeverity <= 3;
    }

    private static VanguardFikaHudTelemetryEntry Clone(VanguardFikaHudTelemetryEntry entry)
    {
        return new VanguardFikaHudTelemetryEntry
        {
            BotProfileId = entry.BotProfileId,
            ActivityLabel = entry.ActivityLabel,
            AlertLabel = entry.AlertLabel,
            AlertSeverity = entry.AlertSeverity,
            Detail = entry.Detail,
            Urgent = entry.Urgent,
        };
    }
}
#endif
