#if SPT_CLIENT
using System;

// Responsibility: Defines data/state contracts used by the Fika raid-runtime transport, centered on Fika Hud Telemetry Models.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Raid.Runtime.Fika;

internal sealed class VanguardFikaHudTelemetryPacket
{
    public string Payload { get; set; } = string.Empty;
}

internal sealed class VanguardFikaHudTelemetryPayload
{
    public int ProtocolVersion { get; set; }
    public string BuildLabel { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public long SentAtUnixMilliseconds { get; set; }
    public VanguardFikaHudTelemetryEntry[] Entries { get; set; } = Array.Empty<VanguardFikaHudTelemetryEntry>();
}

internal sealed class VanguardFikaHudTelemetryEntry
{
    public string BotProfileId { get; set; } = string.Empty;
    public string ActivityLabel { get; set; } = string.Empty;
    public string AlertLabel { get; set; } = string.Empty;
    public int AlertSeverity { get; set; }
    public string Detail { get; set; } = string.Empty;
    public bool Urgent { get; set; }
}

internal sealed record VanguardFikaHudTelemetryReceivedEntry(
    VanguardFikaHudTelemetryEntry Entry,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset SourceSentAtUtc,
    long Sequence,
    string SessionId);
#endif
