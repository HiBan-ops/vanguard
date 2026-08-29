#if SPT_CLIENT
using System;
using System.Collections;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Options;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.Loot;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Grenades;

// Responsibility: Builds Operator Decision Snapshot Builder data for the decision snapshot pipeline from already-available inputs.
// Flow: Normalized inputs are combined deterministically into a result consumed by the next policy, scheduler, UI, or transport stage.
// Authority boundary: Composition only; underlying gameplay/persistence truth remains owned by the source inputs.
// Invariant: Building a result must not perform hidden world mutation or acquire a competing authority.
namespace Vanguard.Client.Runtime.Decision;

internal sealed partial class VanguardOperatorDecisionSnapshotBuilder
{
    private sealed class LastPositionState
    {
        public Vector3 Position;
        public DateTimeOffset CapturedAtUtc = DateTimeOffset.MinValue;
    }

    private sealed class ExtendedSnapshotState
    {
        public VanguardThreatScanDecisionSnapshot ThreatScan = VanguardThreatScanDecisionSnapshot.Empty;
        public VanguardMedicalInventoryReadResult MedicalInventory = new();
        public VanguardSquadCohesionSnapshot SquadCohesion = VanguardSquadCohesionSnapshot.Empty;
        public VanguardLootDecisionSnapshot Loot = VanguardLootDecisionSnapshot.Empty;
        public VanguardCorpseLootDecisionSnapshot CorpseLoot = VanguardCorpseLootDecisionSnapshot.Empty;
        public VanguardOrbitDecisionSnapshot Orbit = VanguardOrbitDecisionSnapshot.Empty;
        public VanguardMedicalDecisionSnapshot Medical = VanguardMedicalDecisionSnapshot.Empty;
        public DateTimeOffset MedicalCapturedAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset NextHealthyMedicalRefreshAtUtc = DateTimeOffset.MinValue;
    }

    private sealed class ScanEnemyCandidate
    {
        public string Id = "none";
        public string Name = "none";
        public bool Visible;
        public bool LineOfSight;
        public bool CanShoot;
        public bool ShotMeRecently;
        public bool ShotAtMeRecently;
        public float? Distance;
        public float? TimeSinceSeen;
        public float? AngleDegrees;
        public string Arc = "none";
        public float Score;
        public bool SameAsCurrent;
    }

    private readonly Dictionary<string, LastPositionState> lastPositionsByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExtendedSnapshotState> extendedByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset lastMedicalCadenceSummaryUtc = DateTimeOffset.MinValue;
    private int medicalFullRefreshesSinceSummary;
    private int medicalSafetyOnlyRefreshesSinceSummary;

    public void ResetForRaidLifecycle()
    {
        lastPositionsByBotProfileId.Clear();
        extendedByBotProfileId.Clear();
        lastMedicalCadenceSummaryUtc = DateTimeOffset.MinValue;
        medicalFullRefreshesSinceSummary = 0;
        medicalSafetyOnlyRefreshesSinceSummary = 0;
        VanguardOwnerAnchorResolver.ResetForRaidLifecycle("decision_snapshot_builder_reset");
    }

    public OperatorDecisionSnapshot Capture(VanguardRaidOperatorRuntimeRecord record)
        => Capture(record, refreshExtended: true);

    public OperatorDecisionSnapshot Capture(
        VanguardRaidOperatorRuntimeRecord record,
        bool refreshExtended)
    {
        long fastCoreStarted = VanguardRuntimePerformanceGuard.Begin();
        var now = DateTimeOffset.UtcNow;
        BotOwner? botOwner = record.BotOwner;
        Vector3 position = ResolvePosition(botOwner);
        float realSpeed = ComputeRealSpeed(record.BotProfileId, position, now);

        bool alive = ResolveAlive(botOwner);
        var movement = CaptureMovement(botOwner, realSpeed);
        var brain = CaptureBrain(botOwner);
        var sain = CaptureSain(botOwner);
        var threat = CaptureThreat(botOwner, sain, brain, alive);
        var grenadeHazard = VanguardGrenadeHazardRegistry.CaptureDecisionSnapshot(record, position, now);
        VanguardRuntimePerformanceGuard.End("DecisionSnapshotFastCore", fastCoreStarted);

        if (!extendedByBotProfileId.TryGetValue(record.BotProfileId, out var extended))
        {
            extended = new ExtendedSnapshotState();
            extendedByBotProfileId[record.BotProfileId] = extended;
        }

        if (refreshExtended)
        {
            long threatScanStarted = VanguardRuntimePerformanceGuard.Begin();
            extended.ThreatScan = CaptureThreatScan(botOwner, threat, sain, brain, alive, position);
            VanguardRuntimePerformanceGuard.End("DecisionSnapshotThreatScanExtended", threatScanStarted);

            long inventoryStarted = VanguardRuntimePerformanceGuard.Begin();
            extended.MedicalInventory = VanguardMedicalInventoryReader.Capture(botOwner);
            VanguardRuntimePerformanceGuard.End("DecisionSnapshotMedicalInventoryExtended", inventoryStarted);

            long cohesionStarted = VanguardRuntimePerformanceGuard.Begin();
            extended.SquadCohesion = CaptureSquadCohesion(record, alive, position, realSpeed, movement, threat, sain, brain);
            VanguardRuntimePerformanceGuard.End("DecisionSnapshotCohesionExtended", cohesionStarted);

            long integrationsStarted = VanguardRuntimePerformanceGuard.Begin();
            extended.Loot = CaptureLoot(botOwner);
            extended.Orbit = CaptureOrbit(botOwner, record.BotProfileId);
            VanguardRuntimePerformanceGuard.End("DecisionSnapshotIntegrationsExtended", integrationsStarted);
        }

        var threatScan = extended.ThreatScan;
        long medicalCoreStarted = VanguardRuntimePerformanceGuard.Begin();
        bool refreshMedicalCore = ShouldRefreshMedicalCore(record, botOwner, extended, refreshExtended, alive, now);
        VanguardMedicalDecisionSnapshot medical;
        if (refreshMedicalCore)
        {
            medical = CaptureMedical(botOwner, alive, threat, threatScan, extended.MedicalInventory);
            extended.Medical = medical;
            extended.MedicalCapturedAtUtc = now;
            extended.NextHealthyMedicalRefreshAtUtc = now + HealthyMedicalRefreshCadence(record.BotProfileId);
            medicalFullRefreshesSinceSummary++;
        }
        else
        {
            medical = RefreshCachedMedicalSafety(botOwner, alive, threat, threatScan, extended.Medical);
            extended.Medical = medical;
            medicalSafetyOnlyRefreshesSinceSummary++;
        }
        VanguardRuntimePerformanceGuard.End("DecisionSnapshotMedicalCore", medicalCoreStarted);
        EmitMedicalCadenceSummaryIfDue(now);
        var awareness = VanguardAwarenessReadOnlyBuilder.Build(alive, threat, threatScan, sain, brain, medical);
        var squadCohesion = refreshExtended
            ? extended.SquadCohesion
            : CaptureSquadCohesionFast(record, alive, position, realSpeed, movement, threat, sain, brain, extended.SquadCohesion);
        var loot = extended.Loot;
        var orbit = extended.Orbit;
        if (refreshExtended)
        {
            // The persistence path captures the immutable raid-start weapon provenance before any opportunistic corpse
            // transaction can mutate the long-weapon layout. The registry is write-once per BotProfileId.
            VanguardOperatorRaidLoadoutRegistry.CaptureIfMissing(record);
            long corpseLootStarted = VanguardRuntimePerformanceGuard.Begin();
            extended.CorpseLoot = VanguardCorpseLootReadOnlyEvaluator.Capture(
                record, alive, position, threat, sain, brain, medical, extended.MedicalInventory, squadCohesion, loot, orbit, now);
            VanguardRuntimePerformanceGuard.End("DecisionSnapshotCorpseLootExtended", corpseLootStarted);

            // The persistence path shadow/read-only bridge: containers enter the same utility scorer and squad allocation
            // authority, but no physical claim/path/movement/opening/transaction is produced here.
            long containerLootStarted = VanguardRuntimePerformanceGuard.Begin();
            VanguardWorldLootContainerReadOnlyEvaluator.ObserveAssignments(
                record, alive, position, medical, extended.MedicalInventory, now);
            VanguardRuntimePerformanceGuard.End("DecisionSnapshotWorldContainerScoringExtended", containerLootStarted);
        }
        var corpseLoot = extended.CorpseLoot;
        var movementAuthority = CaptureMovementAuthority(record, alive, realSpeed, movement, brain, sain, threat, medical, squadCohesion, loot, orbit);
        var primaryExecution = CapturePrimaryExecution(record.BotProfileId, now);

        return new OperatorDecisionSnapshot
        {
            OperatorId = record.OperatorId,
            OwnerProfileId = record.OwnerProfileId,
            BotProfileId = record.BotProfileId,
            Nickname = record.BotNickname,
            Alive = medical.Alive,
            Position = position,
            RealSpeed = realSpeed,
            Movement = movement,
            Brain = brain,
            Sain = sain,
            Threat = threat,
            GrenadeHazard = grenadeHazard,
            ThreatScan = threatScan,
            Medical = medical,
            Awareness = awareness,
            SquadCohesion = squadCohesion,
            MovementAuthority = movementAuthority,
            PrimaryExecution = primaryExecution,
            Looting = loot,
            CorpseLoot = corpseLoot,
            Orbit = orbit,
            CapturedAtUtc = now
        };
    }

    private static VanguardPrimaryExecutionDecisionSnapshot CapturePrimaryExecution(string botProfileId, DateTimeOffset now)
    {
        if (!VanguardMainIntentScheduler.TryGetActivePrimaryWindowIdentity(
                botProfileId,
                now,
                out string windowKind,
                out string intentKey,
                out string state))
        {
            return VanguardPrimaryExecutionDecisionSnapshot.Empty;
        }

        return new VanguardPrimaryExecutionDecisionSnapshot
        {
            Active = true,
            WindowKind = windowKind,
            IntentKey = intentKey,
            State = state,
        };
    }

    private static bool ShouldRefreshMedicalCore(
        VanguardRaidOperatorRuntimeRecord record,
        BotOwner? botOwner,
        ExtendedSnapshotState extended,
        bool refreshExtended,
        bool alive,
        DateTimeOffset now)
    {
        if (refreshExtended
            || extended.MedicalCapturedAtUtc == DateTimeOffset.MinValue
            || ReferenceEquals(extended.Medical, VanguardMedicalDecisionSnapshot.Empty)
            || extended.Medical.Alive != alive
            || now >= extended.NextHealthyMedicalRefreshAtUtc
            || VanguardMedicalActionabilityReader.IsMedicalActivityObserved(botOwner))
        {
            return true;
        }

        VanguardMedicalDecisionSnapshot cached = extended.Medical;
        if (cached.Need.HasAnyNeed
            || cached.Actionability.AnyMedicineUsing
            || cached.Actionability.FirstAidUsing
            || cached.Actionability.SurgicalKitUsing
            || cached.Actionability.StimulatorUsing
            || VanguardExecutionLeaseCoordinator.HasActiveLease(record.BotProfileId))
        {
            return true;
        }

        return false;
    }

    private static TimeSpan HealthyMedicalRefreshCadence(string? botProfileId)
    {
        // Healthy/no-action Operators may reuse immutable need/actionability truth until less
        // than one second has elapsed since the last complete read. At the minimum configured
        // 0.5 s snapshot interval this skips at most one invocation; at slower intervals every
        // invocation is complete. The deterministic phase offset avoids synchronized reads.
        int phase = StablePhase(botProfileId, 4);
        return TimeSpan.FromMilliseconds(750d + phase * 75d);
    }

    private static int StablePhase(string? value, int modulo)
    {
        if (string.IsNullOrWhiteSpace(value) || modulo <= 1)
        {
            return 0;
        }

        unchecked
        {
            uint hash = 2166136261u;
            foreach (char character in value)
            {
                hash ^= character;
                hash *= 16777619u;
            }
            return (int)(hash % (uint)modulo);
        }
    }

    private void EmitMedicalCadenceSummaryIfDue(DateTimeOffset now)
    {
        if (lastMedicalCadenceSummaryUtc != DateTimeOffset.MinValue
            && now - lastMedicalCadenceSummaryUtc < TimeSpan.FromSeconds(60d))
        {
            return;
        }

        if (medicalFullRefreshesSinceSummary == 0 && medicalSafetyOnlyRefreshesSinceSummary == 0)
        {
            lastMedicalCadenceSummaryUtc = now;
            return;
        }

        VanguardClientDiagnosticsLog.Diagnostic(
            VanguardHeadlessRuntimeStallGuard.StatusTag,
            () => $"VANGUARD_MEDICAL_SNAPSHOT_CADENCE full={medicalFullRefreshesSinceSummary}; safetyOnly={medicalSafetyOnlyRefreshesSinceSummary}; healthyMaxCadenceMs=975; activeNeedAlwaysFull=true; activeLeaseAlwaysFull=true; threatSafetyAlwaysCurrent=true; healthyNeedFullReadMaxAgeMs=975; maxSkippedInvocationsAtMinInterval=1; tag={VanguardHeadlessRuntimeStallGuard.StatusTag}");
        medicalFullRefreshesSinceSummary = 0;
        medicalSafetyOnlyRefreshesSinceSummary = 0;
        lastMedicalCadenceSummaryUtc = now;
    }

    private float ComputeRealSpeed(string botProfileId, Vector3 position, DateTimeOffset now)
    {
        if (!lastPositionsByBotProfileId.TryGetValue(botProfileId, out var state))
        {
            state = new LastPositionState();
            lastPositionsByBotProfileId[botProfileId] = state;
        }

        float speed = 0f;
        if (state.CapturedAtUtc != DateTimeOffset.MinValue)
        {
            double deltaSeconds = Math.Max(0.001d, (now - state.CapturedAtUtc).TotalSeconds);
            speed = Vector3.Distance(state.Position, position) / (float)deltaSeconds;
        }

        state.Position = position;
        state.CapturedAtUtc = now;
        return speed;
    }

    private static Vector3 ResolvePosition(BotOwner? botOwner)
    {
        try
        {
            object? transform = VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "GetPlayer", "Transform");
            if (transform is Transform playerTransform)
            {
                return playerTransform.position;
            }

            object? position = VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "GetPlayer", "Position");
            if (position is Vector3 vector)
            {
                return vector;
            }

            object? botTransform = VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "Transform");
            if (botTransform is Transform directTransform)
            {
                return directTransform.position;
            }
        }
        catch
        {
            // Decision snapshot is passive in audit subsystem; reflection failures must not affect raid runtime.
        }

        return Vector3.zero;
    }

    private static bool ResolveAlive(BotOwner? botOwner)
    {
        object? healthController = VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "GetPlayer", "HealthController");
        object? alive = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(healthController, "IsAlive");
        if (alive is bool boolValue)
        {
            return boolValue;
        }

        object? isDead = VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "GetPlayer", "IsDead");
        return isDead is bool dead ? !dead : botOwner != null;
    }


}
#endif
