#if SPT_CLIENT
using System;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Raid.Runtime.Fika;

// Responsibility: Resolve raid Operator HUD activity and alerts from local decisions and received Fika telemetry.
// Flow: Prefer fresh local projection, then fresh remote telemetry, and expose stale or unavailable states explicitly.
// Authority boundary: This is a read-only presentation resolver; it cannot issue Operator gameplay commands.
// Invariant: Unavailable or stale authoritative data is visible instead of being silently treated as a live decision.

namespace Vanguard.Client.Raid.Hud;

/// <summary>
/// Presentation-only resolver over canonical local decision projection or authority-resolved Fika HUD telemetry.
/// No SAIN/BigBrain/LootingBots reflection occurs here: absence of fresh authoritative truth is rendered visibly.
/// </summary>
internal static class VanguardRaidFixedOperatorHudSemanticResolver
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(8.0d);

    public static VanguardRaidFixedOperatorHudSemanticState Resolve(VanguardRaidOperatorHudCandidate candidate)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool zeroHealth = candidate.HealthReadable && candidate.HealthPercent <= 0;
        VanguardRaidFixedOperatorHudSemanticState? localStale = null;

        if (!string.IsNullOrWhiteSpace(candidate.BotProfileId)
            && VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(candidate.BotProfileId, out OperatorDecisionSnapshot localSnapshot)
            && !ReferenceEquals(localSnapshot, OperatorDecisionSnapshot.Empty))
        {
            VanguardRaidFixedOperatorHudSemanticState local = Map(VanguardOperatorHudSemanticProjector.Project(localSnapshot, now));
            if (local.Fresh)
            {
                return local;
            }

            localStale = local;
        }

        if (!string.IsNullOrWhiteSpace(candidate.BotProfileId)
            && VanguardFikaHudTelemetryStore.TryGet(candidate.BotProfileId, out VanguardFikaHudTelemetryReceivedEntry remote))
        {
            TimeSpan receivedAge = now - remote.ReceivedAtUtc;
            if (receivedAge < TimeSpan.Zero)
            {
                receivedAge = TimeSpan.Zero;
            }

            if (receivedAge <= StaleAfter)
            {
                var severity = Enum.IsDefined(typeof(VanguardRaidFixedOperatorHudAlertSeverity), remote.Entry.AlertSeverity)
                    ? (VanguardRaidFixedOperatorHudAlertSeverity)remote.Entry.AlertSeverity
                    : VanguardRaidFixedOperatorHudAlertSeverity.None;
                return new VanguardRaidFixedOperatorHudSemanticState(
                    remote.Entry.ActivityLabel,
                    remote.Entry.AlertLabel,
                    severity,
                    remote.Entry.Detail,
                    true,
                    true,
                    remote.Entry.Urgent);
            }

            return new VanguardRaidFixedOperatorHudSemanticState(
                "ETAT OBSOLETE",
                "LIAISON...",
                VanguardRaidFixedOperatorHudAlertSeverity.Stale,
                $"authoritative telemetry age={receivedAge.TotalSeconds:0.0}s",
                true,
                false,
                true);
        }

        if (zeroHealth)
        {
            return new VanguardRaidFixedOperatorHudSemanticState(
                "HORS COMBAT",
                "KIA",
                VanguardRaidFixedOperatorHudAlertSeverity.Critical,
                "physical state: zero health",
                true,
                true,
                true);
        }

        return localStale ?? Unavailable("authoritative decision snapshot/telemetry unavailable");
    }

    private static VanguardRaidFixedOperatorHudSemanticState Map(VanguardOperatorHudSemanticProjection projection)
    {
        var severity = Enum.IsDefined(typeof(VanguardRaidFixedOperatorHudAlertSeverity), projection.AlertSeverity)
            ? (VanguardRaidFixedOperatorHudAlertSeverity)projection.AlertSeverity
            : VanguardRaidFixedOperatorHudAlertSeverity.None;
        return new VanguardRaidFixedOperatorHudSemanticState(
            projection.ActivityLabel,
            projection.AlertLabel,
            severity,
            projection.Detail,
            projection.Authoritative,
            projection.Fresh,
            projection.Urgent);
    }

    private static VanguardRaidFixedOperatorHudSemanticState Unavailable(string detail)
    {
        return new VanguardRaidFixedOperatorHudSemanticState(
            "ETAT INDISP.",
            "TELEM.",
            VanguardRaidFixedOperatorHudAlertSeverity.Stale,
            detail,
            false,
            false,
            true);
    }
}
#else
namespace Vanguard.Client.Raid.Hud;

internal static class VanguardRaidFixedOperatorHudSemanticResolver
{
}
#endif
