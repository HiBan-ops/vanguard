#if SPT_CLIENT
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

// Responsibility: Defines data/state contracts used by the Fika raid-runtime transport, centered on Fika Hud Medical Telemetry Models.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Raid.Runtime.Fika;

internal static class VanguardFikaHudMedicalMask
{
    public const byte Readable = 1 << 0;
    public const byte HeavyBleed = 1 << 1;
    public const byte LightBleed = 1 << 2;
    public const byte Fracture = 1 << 3;
    public const byte Pain = 1 << 4;
    public const byte Tremor = 1 << 5;
    public const byte KnownBits = Readable | HeavyBleed | LightBleed | Fracture | Pain | Tremor;
}

internal sealed class VanguardFikaHudMedicalTelemetryPacket
{
    public string Payload { get; set; } = string.Empty;
}

internal sealed class VanguardFikaHudMedicalTelemetryPayload
{
    [JsonProperty("v")]
    public int ProtocolVersion { get; set; }

    [JsonProperty("b")]
    public string BuildLabel { get; set; } = string.Empty;

    [JsonProperty("s")]
    public string SessionId { get; set; } = string.Empty;

    [JsonProperty("q")]
    public long Sequence { get; set; }

    [JsonProperty("t")]
    public long SentAtUnixMilliseconds { get; set; }

    [JsonProperty("e")]
    public VanguardFikaHudMedicalTelemetryEntry[] Entries { get; set; } = Array.Empty<VanguardFikaHudMedicalTelemetryEntry>();
}

internal sealed class VanguardFikaHudMedicalTelemetryEntry
{
    [JsonProperty("i")]
    public string BotProfileId { get; set; } = string.Empty;

    [JsonProperty("m")]
    public byte MedicalMask { get; set; }
}

internal sealed record VanguardFikaHudMedicalTelemetryReceivedEntry(
    string BotProfileId,
    byte MedicalMask,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset SourceSentAtUtc,
    long Sequence,
    string SessionId);

internal sealed record VanguardFikaHudMedicalState(
    bool Readable,
    bool HasHeavyBleed,
    bool HasLightBleed,
    bool HasFracture,
    bool HasPain,
    bool HasTremor,
    DateTimeOffset ReceivedAtUtc,
    long Sequence)
{
    public string[] Badges
    {
        get
        {
            var badges = new List<string>(5);
            if (HasHeavyBleed) badges.Add("HB");
            if (HasLightBleed) badges.Add("LB");
            if (HasFracture) badges.Add("FR");
            if (HasPain) badges.Add("PN");
            if (HasTremor) badges.Add("TR");
            return badges.ToArray();
        }
    }

    public string MaterialSignature => $"remote|readable={Readable}|HB={HasHeavyBleed}|LB={HasLightBleed}|FR={HasFracture}|PN={HasPain}|TR={HasTremor}";
}
#endif
