#if SPT_CLIENT
using System;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Raid.Runtime.Fika;

// Responsibility: Provides Raid Fixed Operator Hud Semantic Resolver support for the raid Operator HUD.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
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
