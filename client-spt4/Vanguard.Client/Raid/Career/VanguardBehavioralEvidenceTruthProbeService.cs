using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;
using Vanguard.Client.Raid.Runtime;

// Responsibility: Aggregates read-only behavioral facts already qualified by Vanguard runtime systems so they can be persisted as durable Career evidence without changing AI behavior.
// Flow: Runtime services submit bounded factual observations, this raid-scoped collector aggregates them by semantic identity, and the Career ledger payload snapshots the result at raid stop.
// Authority boundary: Runtime observation is evidence only; durable authority remains the server Career ledger and no PersonaEvidence/SAIN projection is mutated here.
// Invariant: Evidence is deterministic, bounded, raid-scoped and interpretation-free; missing observations remain missing and no score/personality delta is invented.
namespace Vanguard.Client.Raid.Career;

/// <summary>
/// Behavioral-evidence foundation. The first admitted fact family is qualified incoming-fire
/// near-miss exposure already produced by VanguardNearMissSuppressionService. Facts are aggregated
/// by Operator and hostile shooter to avoid high-cardinality event spam while preserving observed
/// burst/shot counts, closest passage and threat target semantics.
/// </summary>
internal static class VanguardBehavioralEvidenceTruthProbeService
{
    public const string StatusTag = "VANGUARD_BEHAVIORAL_EVIDENCE_FOUNDATION_STATUS";
    public const string SuppressionSource = "VanguardNearMissSuppressionService.Register";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, SuppressionExposureState> SuppressionByIdentity = new(StringComparer.OrdinalIgnoreCase);

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            SuppressionByIdentity.Clear();
        }

        VanguardClientDiagnosticsLog.Operational(
            StatusTag,
            () => $"VANGUARD_BEHAVIORAL_EVIDENCE_RESET reason={Safe(reason)}; suppressionEntries=0; personaEvidenceMutation=false; sainProjectionMutation=false; behaviorMutation=false; durableMutation=false");
    }

    public static void RecordNearMissShot(
        string? operatorProfileId,
        string? hostileShooterProfileId,
        bool startsNewBurst,
        float closestMeters,
        bool threatenedOperator,
        bool threatenedOwner,
        DateTimeOffset observedAtUtc)
    {
        if (!ShouldObserve()
            || string.IsNullOrWhiteSpace(operatorProfileId)
            || string.IsNullOrWhiteSpace(hostileShooterProfileId)
            || observedAtUtc == default
            || float.IsNaN(closestMeters)
            || float.IsInfinity(closestMeters)
            || closestMeters < 0f)
        {
            return;
        }

        string raidSessionId = Clean(VanguardRaidOperatorRuntimeRegistry.ActiveRaidSessionId);
        string operatorProfile = Clean(operatorProfileId);
        string shooterProfile = Clean(hostileShooterProfileId);
        if (string.IsNullOrWhiteSpace(raidSessionId)
            || string.IsNullOrWhiteSpace(operatorProfile)
            || string.IsNullOrWhiteSpace(shooterProfile))
        {
            return;
        }

        string identity = raidSessionId + "|" + operatorProfile + "|" + shooterProfile;
        lock (Sync)
        {
            if (!SuppressionByIdentity.TryGetValue(identity, out SuppressionExposureState state))
            {
                state = new SuppressionExposureState
                {
                    RaidSessionId = raidSessionId,
                    OperatorProfileId = operatorProfile,
                    HostileShooterProfileId = shooterProfile,
                    FirstObservedAtUtc = observedAtUtc,
                    LastObservedAtUtc = observedAtUtc,
                    ClosestMeters = closestMeters
                };
                SuppressionByIdentity.Add(identity, state);
            }

            state.ShotCount++;
            if (startsNewBurst || state.BurstCount == 0)
            {
                state.BurstCount++;
            }
            state.ClosestMeters = Math.Min(state.ClosestMeters, closestMeters);
            if (observedAtUtc < state.FirstObservedAtUtc)
            {
                state.FirstObservedAtUtc = observedAtUtc;
            }
            if (observedAtUtc > state.LastObservedAtUtc)
            {
                state.LastObservedAtUtc = observedAtUtc;
            }
            state.ThreatenedOperator |= threatenedOperator;
            state.ThreatenedOwner |= threatenedOwner;
        }
    }

    public static IReadOnlyList<VanguardCareerRaidSuppressionExposureEventDto> SnapshotSuppressionExposureEvents(string? expectedRaidSessionId)
    {
        string expectedRaid = Clean(expectedRaidSessionId);
        SuppressionExposureState[] states;
        lock (Sync)
        {
            states = SuppressionByIdentity.Values
                .Where(value => string.Equals(value.RaidSessionId, expectedRaid, StringComparison.OrdinalIgnoreCase))
                .OrderBy(value => value.OperatorProfileId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.HostileShooterProfileId, StringComparer.OrdinalIgnoreCase)
                .Select(value => value.Clone())
                .ToArray();
        }

        return states.Select(value => new VanguardCareerRaidSuppressionExposureEventDto
        {
            EventId = BuildSuppressionEventId(value.RaidSessionId, value.OperatorProfileId, value.HostileShooterProfileId),
            RaidSessionId = value.RaidSessionId,
            FirstObservedAtUtc = value.FirstObservedAtUtc,
            LastObservedAtUtc = value.LastObservedAtUtc,
            OperatorProfileId = value.OperatorProfileId,
            HostileShooterProfileId = value.HostileShooterProfileId,
            BurstCount = value.BurstCount,
            ShotCount = value.ShotCount,
            ClosestMeters = value.ClosestMeters,
            ThreatenedOperator = value.ThreatenedOperator,
            ThreatenedOwner = value.ThreatenedOwner,
            Source = SuppressionSource
        }).ToArray();
    }

    private static bool ShouldObserve()
        => VanguardFikaCompat.IsRaidAuthority
            && VanguardOperatorRuntimeAuditOptions.GetOperatorPostRaidPersistenceEnabled()
            && !string.IsNullOrWhiteSpace(VanguardRaidOperatorRuntimeRegistry.ActiveRaidSessionId);

    private static string BuildSuppressionEventId(string raidSessionId, string operatorProfileId, string hostileShooterProfileId)
        => "behavior_suppression_v1|" + Clean(raidSessionId) + "|suppression_exposure|" + Clean(operatorProfileId) + "|" + Clean(hostileShooterProfileId);

    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private sealed class SuppressionExposureState
    {
        public string RaidSessionId = string.Empty;
        public string OperatorProfileId = string.Empty;
        public string HostileShooterProfileId = string.Empty;
        public DateTimeOffset FirstObservedAtUtc;
        public DateTimeOffset LastObservedAtUtc;
        public int BurstCount;
        public int ShotCount;
        public float ClosestMeters = float.MaxValue;
        public bool ThreatenedOperator;
        public bool ThreatenedOwner;

        public SuppressionExposureState Clone() => new()
        {
            RaidSessionId = RaidSessionId,
            OperatorProfileId = OperatorProfileId,
            HostileShooterProfileId = HostileShooterProfileId,
            FirstObservedAtUtc = FirstObservedAtUtc,
            LastObservedAtUtc = LastObservedAtUtc,
            BurstCount = BurstCount,
            ShotCount = ShotCount,
            ClosestMeters = ClosestMeters,
            ThreatenedOperator = ThreatenedOperator,
            ThreatenedOwner = ThreatenedOwner
        };
    }
}
