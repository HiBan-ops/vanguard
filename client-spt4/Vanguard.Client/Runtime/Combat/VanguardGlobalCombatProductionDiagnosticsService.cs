#if SPT_CLIENT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Patches;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.PostLoot;
using Vanguard.Client.Options;

// Responsibility: Traces why any bot did or did not produce a shot by following the combat pipeline from perception through trigger and native shot execution.
// Flow: Live BotOwners are sampled, the inspected hostile encounter is correlated with SAIN/EFT state, then request/result/trigger/InitiateShot boundaries are timestamped and summarized.
// Authority boundary: The service is passive: EFT and SAIN own perception and firing, and Vanguard adds readback/timing probes only.
// Invariant: Diagnostics must never alter GoalEnemy, visibility, decisions, hands, movement or fire state, even when the observed combat path appears broken.
namespace Vanguard.Client.Runtime.Combat;

/// <summary>
/// The runtime passive, raid-wide combat-production observability.
///
/// The service observes every live EFT BotOwner exposed by the authoritative BotsController,
/// including Operators, Scavs, PMCs, bosses and followers. It correlates the hostile encounter
/// actually being inspected with SAIN/EFT perception state and the successive fire-production
/// boundaries: ShootData request, ShootData result, trigger hold, InitiateShot entry and execution
/// of the original InitiateShot method.
///
/// It never writes GoalEnemy, visibility, decisions, hands, movement or fire state. Its only
/// Harmony additions are readback/timing probes around methods that are already patched by
/// Vanguard or dynamically resolved from SAIN.
/// </summary>
internal static class VanguardGlobalCombatProductionDiagnosticsService
{
    public const string StatusTag = "VANGUARD_GLOBAL_COMBAT_PRODUCTION_DIAGNOSTICS_STATUS";

    private const float MonitorDistanceMeters = 35.0f;
    private const float CloseEncounterDistanceMeters = 15.0f;
    private const float MaximumCloseVerticalSeparationMeters = 4.50f;
    private const float CriticalAcquisitionDistanceMeters = 8.0f;
    private const float MaximumCriticalVerticalSeparationMeters = 3.25f;
    private const float SuspectObservedSeconds = 2.40f;
    private const float AnomalyLogCooldownSeconds = 2.75f;
    private const float DetailedSummarySeconds = 5.0f;
    private const float OperationalHeartbeatSeconds = 15.0f;
    private const int MaximumBotsPerTick = 96;
    private const int MaximumAnomalyLogsPerTick = 2;
    private const int BotLookTimingSampleFrameStride = 4;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, BotProductionState> StateByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SeenThisTick = new(StringComparer.OrdinalIgnoreCase);
    private static ConditionalWeakTable<ShootData, TriggerHeartbeatGate> triggerHeartbeatGates = new();

    private static DateTimeOffset nextTickUtc = DateTimeOffset.MinValue;
    private static float lastTickGameTime = -1f;
    private static float lastDetailedSummaryGameTime = -1f;
    private static float lastOperationalHeartbeatGameTime = -1f;
    private static bool bootLogged;
    private static bool harmonyTopologyLogged;

    private static long shootRequests;
    private static long shootAccepted;
    private static long shootRejected;
    private static long triggerHeartbeats;
    private static long initiateShotEntries;
    private static long initiateShotOriginalRuns;
    private static long initiateShotSuppressedRuns;
    private static long vanguardTriggerVetoes;
    private static long vanguardProjectileVetoes;

    private static long sainVisionCreateCalls;
    private static long sainVisionAnalyzeCalls;
    private static long sainVisionEnemyRelations;
    private static long sainVisionRaycasts;
    private static long sainVisionLatencySamples;
    private static double sainVisionCreateTotalMs;
    private static double sainVisionCreateMaxMs;
    private static double sainVisionAnalyzeTotalMs;
    private static double sainVisionAnalyzeMaxMs;
    private static double sainVisionCreateToAnalyzeTotalMs;
    private static double sainVisionCreateToAnalyzeMaxMs;
    private static int sainVisionCreateToAnalyzeMaxFrames;
    private static long lastSainVisionCreateCompletedTimestamp;
    private static int lastSainVisionCreateFrame = -1;

    private static long sainBotLookCalls;
    private static long sainBotLookTimedSamples;
    private static long sainBotLookEnemiesUpdated;
    private static double sainBotLookTotalMs;
    private static double sainBotLookMaxMs;
    private static float lastAnyBotLookGameTime = -1f;
    private static int lastAnyBotLookEnemiesUpdated;

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            StateByBotProfileId.Clear();
            triggerHeartbeatGates = new ConditionalWeakTable<ShootData, TriggerHeartbeatGate>();
            ResetIntervalCountersNoLock();
            nextTickUtc = DateTimeOffset.MinValue;
            lastTickGameTime = -1f;
            lastDetailedSummaryGameTime = -1f;
            lastOperationalHeartbeatGameTime = -1f;
            lastAnyBotLookGameTime = -1f;
            lastAnyBotLookEnemiesUpdated = 0;
            lastSainVisionCreateCompletedTimestamp = 0L;
            lastSainVisionCreateFrame = -1;
        }

        VanguardClientDiagnosticsLog.Diagnostic(StatusTag,
            () => $"VANGUARD_GLOBAL_COMBAT_PRODUCTION_DIAGNOSTICS_RESET reason={Safe(reason)}; allBotStates=cleared; mutations=false; tag={StatusTag}");
    }

    public static bool FireBoundariesEnabled => EffectiveScope() != VanguardCombatDiagnosticsScope.Off;
    public static bool GlobalVisionBoundariesEnabled => EffectiveScope() == VanguardCombatDiagnosticsScope.AllBots;

    public static long BeginTimedBoundary() => GlobalVisionBoundariesEnabled
        ? Stopwatch.GetTimestamp()
        : 0L;

    public static bool ShouldTimeSainBotLook(int frameCount)
    {
        if (EffectiveScope() != VanguardCombatDiagnosticsScope.AllBots)
        {
            return false;
        }

        int normalized = frameCount < 0 ? -frameCount : frameCount;
        return normalized % BotLookTimingSampleFrameStride == 0;
    }

    public static void ObserveShootRequest(ShootData? shootData)
    {
        BotOwner? owner = shootData?.Owner;
        if (!TryGetBotKey(owner, out string key))
        {
            return;
        }

        float now = Time.time;
        RequestGateSnapshot gate = CaptureRequestGate(shootData!, now);
        lock (Sync)
        {
            BotProductionState state = GetOrCreateStateNoLock(key);
            state.LastShootRequestGameTime = now;
            state.LastShootRequestGate = gate.Gate;
            state.LastRequestWeaponReady = gate.WeaponReady;
            state.LastRequestReloading = gate.Reloading;
            state.LastRequestHaveBullets = gate.HaveBullets;
            state.LastRequestCanShootByState = gate.CanShootByState;
            state.LastRequestAlreadyShooting = gate.AlreadyShooting;
            state.LastRequestNextFingerDelay = gate.NextFingerDelay;
            state.ShootRequestCount++;
            shootRequests++;
        }
    }

    public static void ObserveShootResult(ShootData? shootData, bool accepted)
    {
        BotOwner? owner = shootData?.Owner;
        if (!TryGetBotKey(owner, out string key))
        {
            return;
        }

        float now = Time.time;
        lock (Sync)
        {
            BotProductionState state = GetOrCreateStateNoLock(key);
            state.LastShootResultGameTime = now;
            state.LastShootAccepted = accepted;
            if (accepted)
            {
                state.LastShootAcceptedGameTime = now;
                state.ShootAcceptedCount++;
                shootAccepted++;
            }
            else
            {
                state.ShootRejectedCount++;
                shootRejected++;
            }
        }
    }

    public static void ObserveVanguardTriggerVeto(ShootData? shootData, string? friendlyProfileId)
    {
        BotOwner? owner = shootData?.Owner;
        if (!TryGetBotKey(owner, out string key))
        {
            return;
        }

        float now = Time.time;
        lock (Sync)
        {
            BotProductionState state = GetOrCreateStateNoLock(key);
            state.LastVanguardVetoGameTime = now;
            state.LastVanguardVetoKind = "trigger_corridor";
            state.LastVanguardVetoTarget = Safe(friendlyProfileId);
            vanguardTriggerVetoes++;
        }
    }

    public static void ObserveShootDataHeartbeat(ShootData? shootData)
    {
        if (shootData == null)
        {
            return;
        }

        BotOwner? owner = shootData.Owner;
        if (owner == null || !shootData.Shooting || !TryGetBotKey(owner, out string key))
        {
            return;
        }

        float now = Time.time;
        TriggerHeartbeatGate gate = triggerHeartbeatGates.GetValue(shootData, _ => new TriggerHeartbeatGate());
        // ManualUpdate is invoked every frame while the trigger is held. The per-instance gate
        // rejects 80%+ of calls before entering the shared state lock.
        if (gate.LastSampleGameTime >= 0f && now - gate.LastSampleGameTime < 0.20f)
        {
            return;
        }
        gate.LastSampleGameTime = now;

        lock (Sync)
        {
            BotProductionState state = GetOrCreateStateNoLock(key);
            state.LastTriggerHeldGameTime = now;
            state.TriggerHeartbeatCount++;
            triggerHeartbeats++;
        }
    }

    public static void ObserveInitiateShotEntry(Player? shooter)
    {
        if (!TryGetAiShooterKey(shooter, out string key))
        {
            return;
        }

        float now = Time.time;
        lock (Sync)
        {
            BotProductionState state = GetOrCreateStateNoLock(key);
            state.LastInitiateShotEntryGameTime = now;
            state.InitiateShotEntryCount++;
            initiateShotEntries++;
        }
    }

    public static void ObserveVanguardProjectileVeto(Player? shooter)
    {
        if (!TryGetAiShooterKey(shooter, out string key))
        {
            return;
        }

        float now = Time.time;
        lock (Sync)
        {
            BotProductionState state = GetOrCreateStateNoLock(key);
            state.LastVanguardVetoGameTime = now;
            state.LastVanguardVetoKind = "actual_projectile";
            state.LastVanguardVetoTarget = "protected_friendly";
            vanguardProjectileVetoes++;
        }
    }

    public static void ObserveInitiateShotCompletion(Player? shooter, bool originalRan)
    {
        if (!TryGetAiShooterKey(shooter, out string key))
        {
            return;
        }

        float now = Time.time;
        lock (Sync)
        {
            BotProductionState state = GetOrCreateStateNoLock(key);
            if (originalRan)
            {
                state.LastInitiateShotOriginalGameTime = now;
                state.InitiateShotOriginalCount++;
                initiateShotOriginalRuns++;
            }
            else
            {
                state.LastInitiateShotSuppressedGameTime = now;
                state.InitiateShotSuppressedCount++;
                initiateShotSuppressedRuns++;
            }
        }
    }

    public static void ObserveSainVisionCreate(int enemyCount, int partCount, long started)
    {
        if (EffectiveScope() != VanguardCombatDiagnosticsScope.AllBots)
        {
            return;
        }

        int enemies = Math.Max(0, enemyCount);
        int parts = Math.Max(0, partCount);
        int raycasts = SafeMultiply(SafeMultiply(enemies, parts), 3);
        double elapsedMs = ElapsedMilliseconds(started);
        long completed = Stopwatch.GetTimestamp();
        lock (Sync)
        {
            sainVisionCreateCalls++;
            sainVisionEnemyRelations += enemies;
            sainVisionRaycasts += raycasts;
            sainVisionCreateTotalMs += elapsedMs;
            sainVisionCreateMaxMs = Math.Max(sainVisionCreateMaxMs, elapsedMs);
            lastSainVisionCreateCompletedTimestamp = completed;
            lastSainVisionCreateFrame = Time.frameCount;
        }
    }

    public static void ObserveSainVisionAnalyze(int enemyCount, int partCount, long started)
    {
        if (EffectiveScope() != VanguardCombatDiagnosticsScope.AllBots)
        {
            return;
        }

        double elapsedMs = ElapsedMilliseconds(started);
        long nowTimestamp = Stopwatch.GetTimestamp();
        int nowFrame = Time.frameCount;
        lock (Sync)
        {
            sainVisionAnalyzeCalls++;
            sainVisionAnalyzeTotalMs += elapsedMs;
            sainVisionAnalyzeMaxMs = Math.Max(sainVisionAnalyzeMaxMs, elapsedMs);

            if (lastSainVisionCreateCompletedTimestamp > 0L)
            {
                double latencyMs = (nowTimestamp - lastSainVisionCreateCompletedTimestamp) * 1000d / Stopwatch.Frequency;
                int latencyFrames = lastSainVisionCreateFrame < 0 ? 0 : Math.Max(0, nowFrame - lastSainVisionCreateFrame);
                sainVisionLatencySamples++;
                sainVisionCreateToAnalyzeTotalMs += Math.Max(0d, latencyMs);
                sainVisionCreateToAnalyzeMaxMs = Math.Max(sainVisionCreateToAnalyzeMaxMs, latencyMs);
                sainVisionCreateToAnalyzeMaxFrames = Math.Max(sainVisionCreateToAnalyzeMaxFrames, latencyFrames);
            }
        }
    }

    public static void ObserveSainBotLook(int enemiesUpdated, long started)
    {
        if (EffectiveScope() != VanguardCombatDiagnosticsScope.AllBots)
        {
            return;
        }

        // UpdateLook is high-frequency and runs on the Unity simulation thread. Calls and update
        // counts remain exact, while Stopwatch timing is sampled every fourth frame to avoid
        // making the probe itself a material contributor to global perception latency.
        double elapsedMs = started > 0L ? ElapsedMilliseconds(started) : 0d;
        sainBotLookCalls++;
        sainBotLookEnemiesUpdated += Math.Max(0, enemiesUpdated);
        if (started > 0L)
        {
            sainBotLookTimedSamples++;
            sainBotLookTotalMs += elapsedMs;
            sainBotLookMaxMs = Math.Max(sainBotLookMaxMs, elapsedMs);
        }
        lastAnyBotLookGameTime = Time.time;
        lastAnyBotLookEnemiesUpdated = enemiesUpdated;
    }

    public static void Tick()
    {
        VanguardCombatDiagnosticsScope scope = EffectiveScope();
        if (scope == VanguardCombatDiagnosticsScope.Off)
        {
            return;
        }

        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Diagnostic(StatusTag,
                () => $"VANGUARD_GLOBAL_COMBAT_PRODUCTION_DIAGNOSTICS_BOOT scope={scope}; monitorDistance={MonitorDistanceMeters:0.0}; closeDistance={CloseEncounterDistanceMeters:0.0}; suspectObserved={SuspectObservedSeconds:0.00}; allBotVisionProbes={Bool(scope == VanguardCombatDiagnosticsScope.AllBots)}; mutations=false; tag={StatusTag}");
        }

        if (scope == VanguardCombatDiagnosticsScope.AllBots && !harmonyTopologyLogged)
        {
            harmonyTopologyLogged = true;
            LogHarmonyTopology();
        }

        if (utcNow < nextTickUtc)
        {
            return;
        }

        nextTickUtc = utcNow + TimeSpan.FromMilliseconds(800.0d);
        long scanStarted = Stopwatch.GetTimestamp();
        float gameNow = Time.time;
        float tickGapSeconds = lastTickGameTime >= 0f ? Math.Max(0f, gameNow - lastTickGameTime) : 0f;
        lastTickGameTime = gameNow;

        BotOwner[] botOwners = SnapshotBotOwners(scope);
        WorldSnapshot world = SnapshotWorld();
        SeenThisTick.Clear();
        int scanned = 0;
        int monitoredEncounters = 0;
        int closeEncounters = 0;
        int closeWithGoal = 0;
        int closeVisible = 0;
        int closeCanShoot = 0;
        int closeShooting = 0;
        int anomalies = 0;
        int readFailures = 0;
        string firstReadFailure = "none";

        foreach (BotOwner botOwner in botOwners)
        {
            if (botOwner == null || botOwner.IsDead || scanned >= MaximumBotsPerTick)
            {
                continue;
            }

            if (scope == VanguardCombatDiagnosticsScope.OperatorsOnly
                && !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botOwner.ProfileId, out _))
            {
                continue;
            }

            scanned++;
            string botProfileId = Normalize(botOwner.ProfileId);
            if (string.Equals(botProfileId, "none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SeenThisTick.Add(botProfileId);
            try
            {
                CombatProductionRead read = Capture(botOwner, world, gameNow);
                if (!read.HasEncounter || read.EncounterDistance > MonitorDistanceMeters)
                {
                    ResetSuspect(botProfileId, gameNow, "no_monitored_encounter");
                    continue;
                }

                monitoredEncounters++;
                if (read.EncounterDistance <= CloseEncounterDistanceMeters
                    && read.VerticalSeparation <= MaximumCloseVerticalSeparationMeters)
                {
                    closeEncounters++;
                    if (read.HasGoal) closeWithGoal++;
                    if (read.Visible) closeVisible++;
                    if (read.CanShoot) closeCanShoot++;
                    if (read.Shooting) closeShooting++;
                }

                // Anomaly payloads are deliberately large because they carry every causal
                // boundary needed for offline diagnosis. Bound them globally per scan so a raid-wide
                // failure cannot turn the diagnostic patch itself into a logging stall. Per-bot
                // cooldowns naturally rotate coverage across actors on subsequent scans.
                if (anomalies < MaximumAnomalyLogsPerTick
                    && EvaluateAndLog(read, gameNow, tickGapSeconds))
                {
                    anomalies++;
                }
            }
            catch (Exception exception)
            {
                readFailures++;
                if (string.Equals(firstReadFailure, "none", StringComparison.Ordinal))
                {
                    firstReadFailure = exception.GetType().Name;
                }
                ResetSuspect(botProfileId, gameNow, "read_failure");
            }
        }

        CleanupDeadStates();
        double scanMs = ElapsedMilliseconds(scanStarted);
        bool detailedPayloadsEnabled = VanguardOperatorRuntimeAuditOptions.GetDetailedDiagnosticPayloadsEnabled();
        bool detailedDue = detailedPayloadsEnabled
            && (lastDetailedSummaryGameTime < 0f || gameNow - lastDetailedSummaryGameTime >= DetailedSummarySeconds);
        bool heartbeatDue = lastOperationalHeartbeatGameTime < 0f || gameNow - lastOperationalHeartbeatGameTime >= OperationalHeartbeatSeconds;
        IntervalCounters counters = detailedDue || heartbeatDue ? CaptureIntervalCounters(reset: true) : default;

        if (detailedDue)
        {
            lastDetailedSummaryGameTime = gameNow;
            VanguardClientDiagnosticsLog.Trace(StatusTag,
                () => $"VANGUARD_GLOBAL_COMBAT_PRODUCTION_DETAILED_SUMMARY scope={scope}; bots={scanned}; monitoredEncounters={monitoredEncounters}; closeEncounters={closeEncounters}; closeWithGoal={closeWithGoal}; closeVisible={closeVisible}; closeCanShoot={closeCanShoot}; closeShooting={closeShooting}; anomalies={anomalies}; readFailures={readFailures}; firstReadFailure={Safe(firstReadFailure)}; tickGapMs={tickGapSeconds * 1000f:0.0}; unityFrameMs={Time.unscaledDeltaTime * 1000f:0.0}; scanMs={scanMs:0.00}; {counters.Summary}; mutations=false; tag={StatusTag}");
        }

        if (heartbeatDue)
        {
            lastOperationalHeartbeatGameTime = gameNow;
            VanguardClientDiagnosticsLog.Diagnostic(StatusTag,
                () => $"VANGUARD_GLOBAL_COMBAT_PRODUCTION_HEARTBEAT scope={scope}; bots={scanned}; monitoredEncounters={monitoredEncounters}; closeEncounters={closeEncounters}; anomalies={anomalies}; readFailures={readFailures}; firstReadFailure={Safe(firstReadFailure)}; scanMs={scanMs:0.00}; tickGapMs={tickGapSeconds * 1000f:0.0}; {counters.CompactSummary}; readOnly=true; tag={StatusTag}");
        }
    }

    private static bool EvaluateAndLog(CombatProductionRead read, float now, float tickGapSeconds)
    {
        string logLine;
        lock (Sync)
        {
            BotProductionState state = GetOrCreateStateNoLock(read.BotProfileId);
            state.LastObservedGameTime = now;

            // Action/layer/visibility churn must not continually erase proof of a non-productive
            // close encounter. Only actor identity and weapon availability open a fresh bounded
            // observation window; current perception values remain part of the classified readback.
            string signature = read.EncounterTargetId + "|" + read.GoalTargetId + "|" + read.WeaponReady + "|" + read.Reloading;
            bool newEncounter = !string.Equals(state.EncounterSignature, signature, StringComparison.Ordinal)
                || state.LastInitiateShotOriginalGameTime >= state.SuspectSinceGameTime
                || tickGapSeconds > 1.60f;
            if (newEncounter)
            {
                state.EncounterSignature = signature;
                state.SuspectSinceGameTime = now;
            }

            float observed = Math.Max(0f, now - state.SuspectSinceGameTime);
            bool close = read.EncounterDistance <= CloseEncounterDistanceMeters
                && read.VerticalSeparation <= MaximumCloseVerticalSeparationMeters;
            bool acquisitionCritical = read.EncounterDistance <= CriticalAcquisitionDistanceMeters
                && read.VerticalSeparation <= MaximumCriticalVerticalSeparationMeters
                && !read.GoalMatchesEncounter
                && read.DirectPhysicsProof
                && (IsFacing(read.BotFacingAngle, 115f) || IsFacing(read.TargetFacingAngle, 115f));
            bool localProof = read.Visible || read.LineOfSight || read.CanShoot;
            bool recentSensoryProof = IsRecent(read.TimeSinceSeen, 2.50f)
                || IsRecent(read.TimeSinceHeard, 1.25f)
                || IsRecent(read.TimeSinceKnown, 2.50f);
            bool productiveSinceWindow = state.LastInitiateShotOriginalGameTime >= state.SuspectSinceGameTime;
            bool eligible = close
                && read.WeaponReady
                && !read.Reloading
                && !productiveSinceWindow
                && observed >= SuspectObservedSeconds
                && (localProof || recentSensoryProof || acquisitionCritical);
            if (!eligible || now - state.LastAnomalyLogGameTime < AnomalyLogCooldownSeconds)
            {
                return false;
            }

            state.LastAnomalyLogGameTime = now;
            string boundary = ClassifyMissingBoundary(read, state);
            float shootRequestAge = Age(now, state.LastShootRequestGameTime);
            float shootAcceptedAge = Age(now, state.LastShootAcceptedGameTime);
            float triggerAge = Age(now, state.LastTriggerHeldGameTime);
            float initiateEntryAge = Age(now, state.LastInitiateShotEntryGameTime);
            float initiateOriginalAge = Age(now, state.LastInitiateShotOriginalGameTime);
            float botLookAge = Age(now, lastAnyBotLookGameTime);
            float vanguardVetoAge = Age(now, state.LastVanguardVetoGameTime);
            logLine =
                $"VANGUARD_GLOBAL_COMBAT_PRODUCTION_ANOMALY bot={Safe(read.BotName)}; botProfile={Safe(read.BotProfileId)}; side={Safe(read.Side)}; role={Safe(read.Role)}; operator={Bool(read.IsOperator)}; botState={Safe(read.BotState)}; standby={Safe(read.StandbyType)}; encounterTarget={Safe(read.EncounterTargetId)}; encounterTargetName={Safe(read.EncounterTargetName)}; encounterTargetAI={Bool(read.EncounterTargetIsAi)}; encounterSource={Safe(read.EncounterSource)}; encounterTrackedBySain={Bool(read.EncounterTrackedBySain)}; directPhysicsProof={Bool(read.DirectPhysicsProof)}; encounterDistance={read.EncounterDistance:0.00}; verticalSeparation={read.VerticalSeparation:0.00}; botFacingAngle={Float(read.BotFacingAngle)}; targetFacingAngle={Float(read.TargetFacingAngle)}; goalTarget={Safe(read.GoalTargetId)}; goalMatchesEncounter={Bool(read.GoalMatchesEncounter)}; visible={Bool(read.Visible)}; lineOfSight={Bool(read.LineOfSight)}; canShoot={Bool(read.CanShoot)}; enemyKnown={Bool(read.EnemyKnown)}; seenAgo={Float(read.TimeSinceSeen)}; heardAgo={Float(read.TimeSinceHeard)}; knownAgo={Float(read.TimeSinceKnown)}; sainActive={Bool(read.SainActive)}; sainStandby={Bool(read.SainStandby)}; sainCombat={Bool(read.SainInCombat)}; sainHasEnemy={Bool(read.SainHasEnemy)}; sainAimRead={Bool(read.SainAimRead)}; sainCanAim={Bool(read.SainCanAim)}; sainAimStatus={Safe(read.SainAimStatus)}; sainFriendlyFireRead={Bool(read.SainFriendlyFireRead)}; sainClearShot={Bool(read.SainClearShot)}; sainFriendlyFireStatus={Safe(read.SainFriendlyFireStatus)}; sainLayer={Safe(read.SainLayer)}; sainAction={Safe(read.SainAction)}; sainDecision={Safe(read.SainDecision)}; brainLayer={Safe(read.BrainLayer)}; brainNode={Safe(read.BrainNode)}; weaponReady={Bool(read.WeaponReady)}; reloading={Bool(read.Reloading)}; haveBullets={Bool(read.HaveBullets)}; shooting={Bool(read.Shooting)}; canShootByState={Bool(read.CanShootByState)}; nextFingerDelay={read.NextFingerDelay:0.00}; lastRequestGate={Safe(state.LastShootRequestGate)}; lastRequestWeaponReady={Bool(state.LastRequestWeaponReady)}; lastRequestReloading={Bool(state.LastRequestReloading)}; lastRequestHaveBullets={Bool(state.LastRequestHaveBullets)}; lastRequestCanShootByState={Bool(state.LastRequestCanShootByState)}; lastRequestAlreadyShooting={Bool(state.LastRequestAlreadyShooting)}; lastRequestNextFingerDelay={state.LastRequestNextFingerDelay:0.00}; shootRequestAge={Float(shootRequestAge)}; shootAcceptedAge={Float(shootAcceptedAge)}; triggerAge={Float(triggerAge)}; initiateEntryAge={Float(initiateEntryAge)}; initiateOriginalAge={Float(initiateOriginalAge)}; globalBotLookAge={Float(botLookAge)}; globalBotLookUpdatedLastCall={lastAnyBotLookEnemiesUpdated}; vanguardVetoAge={Float(vanguardVetoAge)}; vanguardVetoKind={Safe(state.LastVanguardVetoKind)}; stableObserved={observed:0.00}; missingBoundary={Safe(boundary)}; tickGapMs={tickGapSeconds * 1000f:0.0}; unityFrameMs={Time.unscaledDeltaTime * 1000f:0.0}; mutation=false; action=diagnose_only; tag={StatusTag}";
        }

        // Logging outside the state lock prevents file I/O from extending the critical section used
        // by high-frequency fire-boundary observations.
        VanguardClientDiagnosticsLog.Warning(StatusTag, logLine);
        return true;
    }

    private static string ClassifyMissingBoundary(CombatProductionRead read, BotProductionState state)
    {
        if (!read.GoalMatchesEncounter && read.EncounterDistance <= CriticalAcquisitionDistanceMeters)
        {
            return read.HasGoal
                ? "target_acquisition_diverged_from_critical_hostile"
                : "perception_or_target_acquisition_before_goal";
        }

        if (state.LastVanguardVetoGameTime >= state.SuspectSinceGameTime)
        {
            return "vanguard_friendly_fire_veto_observed";
        }

        if (state.LastShootRequestGameTime < state.SuspectSinceGameTime)
        {
            if (read.SainAimRead && !read.SainCanAim)
            {
                return "sain_aim_gate_blocked_before_shoot_request";
            }
            if (read.SainFriendlyFireRead && !read.SainClearShot)
            {
                return "sain_friendly_fire_gate_blocked_before_shoot_request";
            }
            return read.Visible || read.LineOfSight || read.CanShoot
                ? "sain_decision_or_shoot_request_not_produced"
                : "perception_not_converged_before_shoot_request";
        }

        if (state.LastShootAcceptedGameTime < state.SuspectSinceGameTime)
        {
            return "shootdata_request_rejected:" + Normalize(state.LastShootRequestGate);
        }

        if (state.LastTriggerHeldGameTime < state.SuspectSinceGameTime)
        {
            return "shoot_accepted_without_trigger_heartbeat";
        }

        if (state.LastInitiateShotEntryGameTime < state.SuspectSinceGameTime)
        {
            return "trigger_held_without_initiate_shot";
        }

        if (state.LastInitiateShotOriginalGameTime < state.SuspectSinceGameTime)
        {
            return "initiate_shot_suppressed_before_original";
        }

        return "unknown_after_initiate_original";
    }

    private static CombatProductionRead Capture(BotOwner botOwner, WorldSnapshot world, float gameNow)
    {
        object? sain = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "SAIN.Components.BotComponent");
        object? sainGoalEnemy = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "GoalEnemy");
        object? memory = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Memory");
        object? eftGoal = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(memory, "GoalEnemy");
        object? eftPerson = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(eftGoal, "Person", "EnemyPerson", "Player");

        string sainGoalId = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainGoalEnemy, "EnemyProfileId"));
        string eftGoalId = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(eftPerson, "ProfileId", "Id"));
        string goalId = First(sainGoalId, eftGoalId);

        IPlayer? nearest = FindNearestHostile(botOwner, world.AlivePlayers, MonitorDistanceMeters, out float nearestDistance, out float nearestVertical);
        string nearestId = nearest == null ? "none" : Normalize(nearest.ProfileId);
        world.ByProfileId.TryGetValue(goalId, out IPlayer? goalPlayer);
        float goalDistance = goalPlayer?.Transform != null
            ? Vector3.Distance(botOwner.Position, goalPlayer.Transform.position)
            : Number(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainGoalEnemy, "RealDistance", "Distance"), float.MaxValue);
        float goalVertical = goalPlayer?.Transform != null
            ? Math.Abs(botOwner.Position.y - goalPlayer.Transform.position.y)
            : float.MaxValue;

        bool nearestPreferred = nearest != null && (goalPlayer == null || nearestDistance + 1.0f < goalDistance);
        string encounterId = nearestPreferred ? nearestId : goalId;
        float encounterDistance = nearestPreferred ? nearestDistance : goalDistance;
        float encounterVertical = nearestPreferred ? nearestVertical : goalVertical;
        IPlayer? encounterPlayer = nearestPreferred ? nearest : goalPlayer;
        string encounterSource = nearestPreferred
            ? (string.Equals(goalId, "none", StringComparison.OrdinalIgnoreCase) ? "nearest_group_hostile_no_goal" : "nearest_group_hostile_goal_diverged")
            : !string.Equals(sainGoalId, "none", StringComparison.OrdinalIgnoreCase) ? "sain_goal"
            : !string.Equals(eftGoalId, "none", StringComparison.OrdinalIgnoreCase) ? "eft_goal"
            : "none";

        bool hasEncounter = !string.Equals(encounterId, "none", StringComparison.OrdinalIgnoreCase) && IsFinite(encounterDistance);
        bool isOperator = VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botOwner.ProfileId, out _);
        var minimal = new CombatProductionRead
        {
            BotProfileId = Normalize(botOwner.ProfileId),
            BotName = First(
                Text(VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "Profile", "Nickname")),
                Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner.GetPlayer, "Nickname")),
                botOwner.name),
            Side = First(
                Text(VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "Profile", "Info", "Side")),
                Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner.GetPlayer, "Side"))),
            Role = First(
                Text(VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "Profile", "Info", "Settings", "Role")),
                Text(VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "Profile", "Info", "Role"))),
            IsOperator = isOperator,
            HasEncounter = hasEncounter,
            EncounterTargetId = encounterId,
            EncounterTargetName = encounterPlayer == null ? "none" : First(
                Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(encounterPlayer, "Nickname")),
                Text(VanguardOperatorRuntimeAuditReflection.GetDeep(encounterPlayer, "Profile", "Nickname"))),
            EncounterTargetIsAi = encounterPlayer != null && Truth(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(encounterPlayer, "IsAI")),
            EncounterSource = encounterSource,
            EncounterDistance = IsFinite(encounterDistance) ? encounterDistance : float.MaxValue,
            VerticalSeparation = IsFinite(encounterVertical) ? encounterVertical : float.MaxValue,
            HasGoal = !string.Equals(goalId, "none", StringComparison.OrdinalIgnoreCase),
            GoalTargetId = goalId,
            GoalMatchesEncounter = Same(goalId, encounterId),
            BotState = Text(botOwner.BotState),
            StandbyType = Text(VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "StandBy", "StandByType"))
        };

        if (!hasEncounter || encounterDistance > MonitorDistanceMeters)
        {
            return minimal;
        }

        object? encounterEnemy = ResolveSainEnemy(sain, encounterId, sainGoalEnemy, sainGoalId);
        object? decision = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "Decision");
        object? sainAim = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "Aim");
        object? sainFriendlyFire = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "FriendlyFire");
        object? brain = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Brain", "BotBrain", "BotBaseBrain");
        object? baseBrain = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(brain, "BaseBrain") ?? brain;
        object? agent = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(brain, "Agent");
        object? activeLayer = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(baseBrain, "CurLayerInfo", "GClass35_0", "Gclass35_0")
            ?? VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(agent, "Gclass35_0", "GClass35_0");
        object? lastResult = VanguardOperatorRuntimeAuditReflection.InvokeNoArg(agent, "LastResult");
        object? weaponManager = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "WeaponManager", "BotWeaponManager");
        object? shootData = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(weaponManager, "ShootData", "ShootDataClass", "shootData");
        VanguardPostLootWeaponReadinessSnapshot weapon = VanguardPostLootWeaponReadinessReader.Capture(botOwner);

        Vector3 encounterDirection = encounterPlayer?.Transform == null
            ? Vector3.zero
            : encounterPlayer.Transform.position - botOwner.Position;
        Vector3 targetToBot = encounterPlayer?.Transform == null
            ? Vector3.zero
            : botOwner.Position - encounterPlayer.Transform.position;

        minimal.EncounterTrackedBySain = encounterEnemy != null;
        minimal.DirectPhysicsProof = encounterPlayer?.Transform != null
            && encounterDistance <= CriticalAcquisitionDistanceMeters
            && encounterVertical <= MaximumCriticalVerticalSeparationMeters
            && HasDirectPhysicsProof(botOwner, encounterPlayer.Transform);
        minimal.BotFacingAngle = HorizontalAngle(botOwner.GetPlayer?.Transform?.forward ?? Vector3.zero, encounterDirection);
        minimal.TargetFacingAngle = HorizontalAngle(encounterPlayer?.Transform?.forward ?? Vector3.zero, targetToBot);
        minimal.EnemyKnown = Truth(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(encounterEnemy, "EnemyKnown"));
        minimal.Visible = Truth(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(encounterEnemy, "IsVisible", "Visible"))
            || (minimal.GoalMatchesEncounter && Truth(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(eftGoal, "IsVisible", "Visible")));
        minimal.LineOfSight = Truth(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(encounterEnemy, "InLineOfSight", "LineOfSight"));
        minimal.CanShoot = Truth(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(encounterEnemy, "CanShoot"))
            || (minimal.GoalMatchesEncounter && Truth(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(eftGoal, "CanShoot")));
        minimal.TimeSinceSeen = NullableNumber(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(encounterEnemy, "TimeSinceSeen"));
        minimal.TimeSinceHeard = NullableNumber(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(encounterEnemy, "TimeSinceHeard"));
        minimal.TimeSinceKnown = NullableNumber(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(encounterEnemy, "TimeSinceLastKnownUpdated"));
        minimal.SainActive = Truth(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "BotActive"));
        minimal.SainStandby = Truth(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "BotInStandBy"));
        minimal.SainInCombat = Truth(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "IsInCombat"));
        minimal.SainHasEnemy = Truth(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "HasEnemy"));
        minimal.SainAimRead = sainAim != null;
        minimal.SainCanAim = Truth(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainAim, "CanAim"));
        minimal.SainAimStatus = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainAim, "AimStatus"));
        minimal.SainFriendlyFireRead = sainFriendlyFire != null;
        minimal.SainClearShot = Truth(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainFriendlyFire, "ClearShot"));
        minimal.SainFriendlyFireStatus = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainFriendlyFire, "FriendlyFireStatus"));
        minimal.SainLayer = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "ActiveLayer"));
        minimal.SainAction = VanguardOperatorRuntimeAuditReflection.TypeName(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "CurrentAction"));
        minimal.SainDecision = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "CurrentCombatDecision"));
        minimal.BrainLayer = First(VanguardOperatorRuntimeAuditReflection.LayerName(activeLayer), Text(VanguardOperatorRuntimeAuditReflection.InvokeNoArg(brain, "ActiveLayerName")));
        minimal.BrainNode = First(Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lastResult, "Action")), Text(VanguardOperatorRuntimeAuditReflection.InvokeNoArg(brain, "GetLastNode")));
        minimal.WeaponReady = weapon.WeaponReady;
        minimal.Reloading = Truth(VanguardOperatorRuntimeAuditReflection.GetDeep(weaponManager, "Reload", "Reloading"));
        minimal.HaveBullets = Truth(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(weaponManager, "HaveBullets"));
        minimal.Shooting = Truth(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(shootData, "Shooting"));
        minimal.CanShootByState = Truth(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(shootData, "CanShootByState"));
        minimal.NextFingerDelay = Number(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(shootData, "NextFingerDownCan"), gameNow) - gameNow;
        return minimal;
    }

    private static bool HasDirectPhysicsProof(BotOwner botOwner, BifacialTransform targetTransform)
    {
        try
        {
            // EFT exposes player space through BifacialTransform. Read its logical position directly,
            // and unwrap Original only for Unity collider hierarchy comparisons.
            Transform? shooterUnityTransform = botOwner.GetPlayer?.Transform?.Original;
            Transform? targetUnityTransform = targetTransform.Original;
            Vector3 from = botOwner.WeaponRoot != null
                ? botOwner.WeaponRoot.position
                : botOwner.Position + Vector3.up * 1.35f;
            Vector3 to = targetTransform.position + Vector3.up * 1.10f;
            Vector3 segment = to - from;
            float remaining = segment.magnitude;
            if (remaining <= 0.05f)
            {
                return true;
            }

            Vector3 direction = segment / remaining;
            // Offset outside the firing actor before the first query. Unity does not report a
            // collider when the ray origin is already inside it, so the bounded skip loop also
            // advances past any remaining shooter geometry without treating it as an obstacle.
            const float advanceMeters = 0.08f;
            Vector3 origin = from + direction * advanceMeters;
            remaining = Math.Max(0f, remaining - advanceMeters);
            for (int skip = 0; skip < 3 && remaining > 0.01f; skip++)
            {
                if (!Physics.Raycast(origin, direction, out RaycastHit hit, remaining, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    return true;
                }

                Transform? hitTransform = hit.transform;
                if (hitTransform == null
                    || (targetUnityTransform != null && IsTransformPartOf(hitTransform, targetUnityTransform)))
                {
                    return true;
                }

                bool shooterGeometry = shooterUnityTransform != null
                    && IsTransformPartOf(hitTransform, shooterUnityTransform);
                if (!shooterGeometry)
                {
                    return false;
                }

                float step = Math.Max(advanceMeters, hit.distance + advanceMeters);
                origin += direction * step;
                remaining -= step;
            }

            return remaining <= 0.01f;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTransformPartOf(Transform candidate, Transform root)
    {
        Transform canonicalRoot = root.root;
        return candidate == root
            || candidate.root == canonicalRoot
            || candidate.IsChildOf(root)
            || root.IsChildOf(candidate);
    }

    private static object? ResolveSainEnemy(object? sain, string profileId, object? goalEnemy, string goalProfileId)
    {
        if (sain == null || string.Equals(profileId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Same(profileId, goalProfileId))
        {
            return goalEnemy;
        }

        object? controller = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "EnemyController");
        object? collection = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(controller, "EnemiesArray", "Enemies", "EnemyArray");
        if (collection is not IEnumerable enumerable)
        {
            return null;
        }

        foreach (object? enemy in enumerable)
        {
            if (enemy != null && Same(Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "EnemyProfileId")), profileId))
            {
                return enemy;
            }
        }

        return null;
    }

    private static BotOwner[] SnapshotBotOwners(VanguardCombatDiagnosticsScope scope)
    {
        try
        {
            if (scope == VanguardCombatDiagnosticsScope.OperatorsOnly)
            {
                return VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators()
                    .Where(record => record.BotOwner != null && !record.BotOwner.IsDead)
                    .Select(record => record.BotOwner!)
                    .Take(MaximumBotsPerTick)
                    .ToArray();
            }

            return VanguardBotsControllerStatePatch.ActiveController?.Bots?.BotOwners?
                .Where(owner => owner != null)
                .Take(MaximumBotsPerTick)
                .ToArray() ?? Array.Empty<BotOwner>();
        }
        catch
        {
            return Array.Empty<BotOwner>();
        }
    }

    private static WorldSnapshot SnapshotWorld()
    {
        try
        {
            IPlayer[] players = Singleton<GameWorld>.Instance?.RegisteredPlayers?
                .Where(player => player != null && player.Transform != null && player.HealthController?.IsAlive == true)
                .ToArray() ?? Array.Empty<IPlayer>();
            var byProfile = new Dictionary<string, IPlayer>(StringComparer.OrdinalIgnoreCase);
            foreach (IPlayer player in players)
            {
                string profileId = Normalize(player.ProfileId);
                if (!string.Equals(profileId, "none", StringComparison.OrdinalIgnoreCase))
                {
                    byProfile[profileId] = player;
                }
            }
            return new WorldSnapshot(players, byProfile);
        }
        catch
        {
            return new WorldSnapshot(Array.Empty<IPlayer>(), new Dictionary<string, IPlayer>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static IPlayer? FindNearestHostile(
        BotOwner botOwner,
        IReadOnlyList<IPlayer> alivePlayers,
        float maxDistance,
        out float distance,
        out float verticalSeparation)
    {
        distance = float.MaxValue;
        verticalSeparation = float.MaxValue;
        if (alivePlayers == null || botOwner.GetPlayer == null)
        {
            return null;
        }

        IPlayer? best = null;
        foreach (IPlayer candidate in alivePlayers)
        {
            if (candidate == null || candidate.Transform == null || Same(candidate.ProfileId, botOwner.ProfileId))
            {
                continue;
            }

            float candidateDistance = Vector3.Distance(botOwner.Position, candidate.Transform.position);
            if (candidateDistance > maxDistance || candidateDistance >= distance)
            {
                continue;
            }

            bool hostile;
            try
            {
                hostile = botOwner.BotsGroup != null
                    && (botOwner.BotsGroup.IsEnemy(candidate) || botOwner.BotsGroup.IsPlayerEnemy(candidate));
            }
            catch
            {
                hostile = false;
            }

            if (!hostile)
            {
                continue;
            }

            best = candidate;
            distance = candidateDistance;
            verticalSeparation = Math.Abs(botOwner.Position.y - candidate.Transform.position.y);
        }

        return best;
    }

    private static void ResetSuspect(string botProfileId, float now, string reason)
    {
        lock (Sync)
        {
            if (!StateByBotProfileId.TryGetValue(botProfileId, out BotProductionState? state))
            {
                return;
            }

            state.EncounterSignature = reason;
            state.SuspectSinceGameTime = now;
            state.LastObservedGameTime = now;
        }
    }

    private static void CleanupDeadStates()
    {
        lock (Sync)
        {
            foreach (string key in StateByBotProfileId.Keys.Where(key => !SeenThisTick.Contains(key)).ToArray())
            {
                StateByBotProfileId.Remove(key);
            }
        }
    }

    private static IntervalCounters CaptureIntervalCounters(bool reset)
    {
        lock (Sync)
        {
            var result = new IntervalCounters(
                shootRequests,
                shootAccepted,
                shootRejected,
                triggerHeartbeats,
                initiateShotEntries,
                initiateShotOriginalRuns,
                initiateShotSuppressedRuns,
                vanguardTriggerVetoes,
                vanguardProjectileVetoes,
                sainVisionCreateCalls,
                sainVisionAnalyzeCalls,
                sainVisionEnemyRelations,
                sainVisionRaycasts,
                sainVisionLatencySamples,
                sainBotLookCalls,
                sainBotLookTimedSamples,
                sainBotLookEnemiesUpdated,
                sainVisionCreateTotalMs,
                sainVisionCreateMaxMs,
                sainVisionAnalyzeTotalMs,
                sainVisionAnalyzeMaxMs,
                sainVisionCreateToAnalyzeTotalMs,
                sainVisionCreateToAnalyzeMaxMs,
                sainVisionCreateToAnalyzeMaxFrames,
                sainBotLookTotalMs,
                sainBotLookMaxMs);
            if (reset)
            {
                ResetIntervalCountersNoLock();
            }
            return result;
        }
    }

    private static void ResetIntervalCountersNoLock()
    {
        shootRequests = 0;
        shootAccepted = 0;
        shootRejected = 0;
        triggerHeartbeats = 0;
        initiateShotEntries = 0;
        initiateShotOriginalRuns = 0;
        initiateShotSuppressedRuns = 0;
        vanguardTriggerVetoes = 0;
        vanguardProjectileVetoes = 0;
        sainVisionCreateCalls = 0;
        sainVisionAnalyzeCalls = 0;
        sainVisionEnemyRelations = 0;
        sainVisionRaycasts = 0;
        sainVisionLatencySamples = 0;
        sainBotLookCalls = 0;
        sainBotLookTimedSamples = 0;
        sainBotLookEnemiesUpdated = 0;
        sainVisionCreateTotalMs = 0d;
        sainVisionCreateMaxMs = 0d;
        sainVisionAnalyzeTotalMs = 0d;
        sainVisionAnalyzeMaxMs = 0d;
        sainVisionCreateToAnalyzeTotalMs = 0d;
        sainVisionCreateToAnalyzeMaxMs = 0d;
        sainVisionCreateToAnalyzeMaxFrames = 0;
        sainBotLookTotalMs = 0d;
        sainBotLookMaxMs = 0d;
    }

    private static BotProductionState GetOrCreateStateNoLock(string key)
    {
        if (!StateByBotProfileId.TryGetValue(key, out BotProductionState? state))
        {
            state = new BotProductionState
            {
                SuspectSinceGameTime = Time.time,
                LastObservedGameTime = Time.time
            };
            StateByBotProfileId[key] = state;
        }
        return state;
    }

    private static RequestGateSnapshot CaptureRequestGate(ShootData shootData, float now)
    {
        try
        {
            BotOwner? owner = shootData.Owner;
            BotWeaponManager? weaponManager = owner?.WeaponManager;
            bool weaponReady = weaponManager?.IsWeaponReady == true;
            bool reloading = weaponManager?.Reload?.Reloading == true;
            bool haveBullets = weaponManager?.HaveBullets == true;
            bool melee = weaponManager?.IsMelee == true;
            bool alreadyShooting = shootData.Shooting;
            bool canShootByState = shootData.CanShootByState;
            float nextFingerDelay = shootData.NextFingerDownCan - now;
            bool controllerNull = owner == null || shootData.ShootController == null;
            bool underbarrelActive = weaponManager?.UnderbarrelLauncherController?.IsActive == true;

            string gate = weaponManager == null ? "weapon_manager_null"
                : melee ? "melee_weapon"
                : !weaponReady ? "weapon_not_ready"
                : reloading ? "reloading"
                : !canShootByState ? "can_shoot_by_state_false"
                : alreadyShooting ? "already_shooting"
                : nextFingerDelay >= 0f ? "next_finger_cooldown"
                : controllerNull ? "shoot_controller_null"
                : !haveBullets && !underbarrelActive ? "no_bullets_reload_requested"
                : underbarrelActive ? "underbarrel_internal_gate_possible"
                : "no_entry_gate_observed";

            return new RequestGateSnapshot(
                gate,
                weaponReady,
                reloading,
                haveBullets,
                canShootByState,
                alreadyShooting,
                nextFingerDelay);
        }
        catch (Exception exception)
        {
            return new RequestGateSnapshot(
                "gate_capture_failed:" + exception.GetType().Name,
                false,
                false,
                false,
                false,
                false,
                0f);
        }
    }

    private static bool TryGetBotKey(BotOwner? owner, out string key)
    {
        key = owner == null ? "none" : Normalize(owner.ProfileId);
        VanguardCombatDiagnosticsScope scope = EffectiveScope();
        if (scope == VanguardCombatDiagnosticsScope.Off
            || owner == null
            || owner.IsDead
            || string.Equals(key, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return scope == VanguardCombatDiagnosticsScope.AllBots
            || VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(key, out _);
    }

    private static bool TryGetAiShooterKey(Player? shooter, out string key)
    {
        key = shooter == null ? "none" : Normalize(shooter.ProfileId);
        VanguardCombatDiagnosticsScope scope = EffectiveScope();
        if (scope == VanguardCombatDiagnosticsScope.Off
            || shooter == null
            || !shooter.IsAI
            || string.Equals(key, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return scope == VanguardCombatDiagnosticsScope.AllBots
            || VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(key, out _);
    }

    private static VanguardCombatDiagnosticsScope EffectiveScope()
    {
        VanguardCombatDiagnosticsScope configured = VanguardOperatorRuntimeAuditOptions.GetCombatDiagnosticsScope();
        if (configured == VanguardCombatDiagnosticsScope.AllBots && !VanguardOperatorRuntimeAuditOptions.IsTrace())
        {
            return VanguardCombatDiagnosticsScope.Off;
        }

        return configured;
    }

    private static void LogHarmonyTopology()
    {
        LogPatchTopology("ShootData.Shoot", AccessTools.Method(typeof(ShootData), nameof(ShootData.Shoot)));
        LogPatchTopology("ShootData.ManualUpdate", AccessTools.Method(typeof(ShootData), nameof(ShootData.ManualUpdate)));
        LogPatchTopology("FirearmController.InitiateShot", AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.InitiateShot)));
        LogPatchTopology("BotsGroup.IsPlayerEnemy", AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.IsPlayerEnemy)));
        Type? botEnemiesType = AccessTools.TypeByName("BotEnemiesController");
        LogPatchTopology("BotEnemiesController.IsEnemy", botEnemiesType == null ? null : AccessTools.Method(botEnemiesType, "IsEnemy"));

        Type? visionType = AccessTools.TypeByName("SAIN.Components.VisionRaycastJob");
        Type? lookType = AccessTools.TypeByName("SAIN.SAINComponent.Classes.SAINBotLookClass");
        LogPatchTopology("SAIN.VisionRaycastJob.CreateCommands", visionType == null ? null : AccessTools.Method(visionType, "CreateCommands"));
        LogPatchTopology("SAIN.VisionRaycastJob.AnalyzeHits", visionType == null ? null : AccessTools.Method(visionType, "AnalyzeHits"));
        LogPatchTopology("SAIN.SAINBotLookClass.UpdateLook", lookType == null ? null : AccessTools.Method(lookType, "UpdateLook", new[] { typeof(float) }));
    }

    private static void LogPatchTopology(string label, MethodBase? target)
    {
        if (target == null)
        {
            VanguardClientDiagnosticsLog.Warning(StatusTag,
                $"VANGUARD_GLOBAL_COMBAT_PATCH_TOPOLOGY method={Safe(label)}; target=missing; readOnly=true; tag={StatusTag}");
            return;
        }

        Patches? patchInfo = Harmony.GetPatchInfo(target);
        string prefixes = FormatPatches(patchInfo?.Prefixes);
        string postfixes = FormatPatches(patchInfo?.Postfixes);
        string transpilers = FormatPatches(patchInfo?.Transpilers);
        string finalizers = FormatPatches(patchInfo?.Finalizers);
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_GLOBAL_COMBAT_PATCH_TOPOLOGY method={Safe(label)}; declaringType={Safe(target.DeclaringType?.FullName)}; prefixes={prefixes}; postfixes={postfixes}; transpilers={transpilers}; finalizers={finalizers}; mutation=false; tag={StatusTag}");
    }

    private static string FormatPatches(IEnumerable<Patch>? patches)
    {
        if (patches == null)
        {
            return "none";
        }

        string[] values = patches
            .Select(patch => Safe(patch.owner) + "@" + patch.priority.ToString(CultureInfo.InvariantCulture) + ":" + Safe(patch.PatchMethod?.DeclaringType?.FullName) + "." + Safe(patch.PatchMethod?.Name))
            .ToArray();
        return values.Length == 0 ? "none" : string.Join(",", values);
    }

    private static int SafeMultiply(int left, int right)
    {
        if (left <= 0 || right <= 0) return 0;
        long product = (long)left * right;
        return product > int.MaxValue ? int.MaxValue : (int)product;
    }

    private static double ElapsedMilliseconds(long started)
    {
        if (started <= 0L) return 0d;
        return (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
    }

    private static float Age(float now, float timestamp) => timestamp < 0f ? -1f : Math.Max(0f, now - timestamp);
    private static bool IsRecent(float? age, float maximumSeconds) => age.HasValue && age.Value >= 0f && age.Value <= maximumSeconds;
    private static bool IsFacing(float angle, float maximumDegrees) => angle >= 0f && angle <= maximumDegrees;
    private static bool Truth(object? value) => value is bool b && b;
    private static float Number(object? value, float fallback)
    {
        try
        {
            if (value == null) return fallback;
            float number = Convert.ToSingle(value, CultureInfo.InvariantCulture);
            return IsFinite(number) ? number : fallback;
        }
        catch { return fallback; }
    }

    private static float? NullableNumber(object? value)
    {
        float number = Number(value, float.NaN);
        return IsFinite(number) ? number : null;
    }

    private static float HorizontalAngle(Vector3 forward, Vector3 direction)
    {
        forward.y = 0f;
        direction.y = 0f;
        if (forward.sqrMagnitude < 0.0001f || direction.sqrMagnitude < 0.0001f)
        {
            return -1f;
        }
        return Vector3.Angle(forward, direction);
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value < float.MaxValue;
    private static bool Same(string? left, string? right) => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    private static string First(params string?[] values)
    {
        foreach (string? value in values)
        {
            string normalized = Normalize(value);
            if (!string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase)) return normalized;
        }
        return "none";
    }

    private static string Text(object? value) => Normalize(VanguardOperatorRuntimeAuditReflection.Text(value));
    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Safe(string? value) => Normalize(value).Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Float(float value) => value < 0f || !IsFinite(value) ? "none" : value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Float(float? value) => !value.HasValue || !IsFinite(value.Value) ? "none" : value.Value.ToString("0.00", CultureInfo.InvariantCulture);

    private sealed class TriggerHeartbeatGate
    {
        public float LastSampleGameTime { get; set; } = -1f;
    }

    private sealed class BotProductionState
    {
        public string EncounterSignature { get; set; } = "none";
        public float SuspectSinceGameTime { get; set; } = -1f;
        public float LastObservedGameTime { get; set; } = -1f;
        public float LastAnomalyLogGameTime { get; set; } = -1000f;
        public float LastShootRequestGameTime { get; set; } = -1f;
        public float LastShootResultGameTime { get; set; } = -1f;
        public float LastShootAcceptedGameTime { get; set; } = -1f;
        public bool LastShootAccepted { get; set; }
        public string LastShootRequestGate { get; set; } = "none";
        public bool LastRequestWeaponReady { get; set; }
        public bool LastRequestReloading { get; set; }
        public bool LastRequestHaveBullets { get; set; }
        public bool LastRequestCanShootByState { get; set; }
        public bool LastRequestAlreadyShooting { get; set; }
        public float LastRequestNextFingerDelay { get; set; }
        public float LastTriggerHeldGameTime { get; set; } = -1f;
        public float LastInitiateShotEntryGameTime { get; set; } = -1f;
        public float LastInitiateShotOriginalGameTime { get; set; } = -1f;
        public float LastInitiateShotSuppressedGameTime { get; set; } = -1f;
        public float LastVanguardVetoGameTime { get; set; } = -1f;
        public string LastVanguardVetoKind { get; set; } = "none";
        public string LastVanguardVetoTarget { get; set; } = "none";
        public long ShootRequestCount { get; set; }
        public long ShootAcceptedCount { get; set; }
        public long ShootRejectedCount { get; set; }
        public long TriggerHeartbeatCount { get; set; }
        public long InitiateShotEntryCount { get; set; }
        public long InitiateShotOriginalCount { get; set; }
        public long InitiateShotSuppressedCount { get; set; }
    }

    private sealed class CombatProductionRead
    {
        public string BotProfileId { get; init; } = "none";
        public string BotName { get; init; } = "none";
        public string Side { get; init; } = "none";
        public string Role { get; init; } = "none";
        public bool IsOperator { get; init; }
        public string BotState { get; init; } = "none";
        public string StandbyType { get; init; } = "none";
        public bool HasEncounter { get; init; }
        public string EncounterTargetId { get; init; } = "none";
        public string EncounterTargetName { get; init; } = "none";
        public bool EncounterTargetIsAi { get; init; }
        public string EncounterSource { get; init; } = "none";
        public bool EncounterTrackedBySain { get; set; }
        public bool DirectPhysicsProof { get; set; }
        public float EncounterDistance { get; init; } = float.MaxValue;
        public float VerticalSeparation { get; init; } = float.MaxValue;
        public float BotFacingAngle { get; set; } = -1f;
        public float TargetFacingAngle { get; set; } = -1f;
        public bool HasGoal { get; init; }
        public string GoalTargetId { get; init; } = "none";
        public bool GoalMatchesEncounter { get; init; }
        public bool EnemyKnown { get; set; }
        public bool Visible { get; set; }
        public bool LineOfSight { get; set; }
        public bool CanShoot { get; set; }
        public float? TimeSinceSeen { get; set; }
        public float? TimeSinceHeard { get; set; }
        public float? TimeSinceKnown { get; set; }
        public bool SainActive { get; set; }
        public bool SainStandby { get; set; }
        public bool SainInCombat { get; set; }
        public bool SainHasEnemy { get; set; }
        public bool SainAimRead { get; set; }
        public bool SainCanAim { get; set; }
        public string SainAimStatus { get; set; } = "none";
        public bool SainFriendlyFireRead { get; set; }
        public bool SainClearShot { get; set; }
        public string SainFriendlyFireStatus { get; set; } = "none";
        public string SainLayer { get; set; } = "none";
        public string SainAction { get; set; } = "none";
        public string SainDecision { get; set; } = "none";
        public string BrainLayer { get; set; } = "none";
        public string BrainNode { get; set; } = "none";
        public bool WeaponReady { get; set; }
        public bool Reloading { get; set; }
        public bool HaveBullets { get; set; }
        public bool Shooting { get; set; }
        public bool CanShootByState { get; set; }
        public float NextFingerDelay { get; set; }
    }

    private readonly struct RequestGateSnapshot
    {
        public RequestGateSnapshot(
            string gate,
            bool weaponReady,
            bool reloading,
            bool haveBullets,
            bool canShootByState,
            bool alreadyShooting,
            float nextFingerDelay)
        {
            Gate = gate;
            WeaponReady = weaponReady;
            Reloading = reloading;
            HaveBullets = haveBullets;
            CanShootByState = canShootByState;
            AlreadyShooting = alreadyShooting;
            NextFingerDelay = nextFingerDelay;
        }

        public string Gate { get; }
        public bool WeaponReady { get; }
        public bool Reloading { get; }
        public bool HaveBullets { get; }
        public bool CanShootByState { get; }
        public bool AlreadyShooting { get; }
        public float NextFingerDelay { get; }
    }

    private readonly struct WorldSnapshot
    {
        public WorldSnapshot(IPlayer[] alivePlayers, Dictionary<string, IPlayer> byProfileId)
        {
            AlivePlayers = alivePlayers;
            ByProfileId = byProfileId;
        }

        public IPlayer[] AlivePlayers { get; }
        public Dictionary<string, IPlayer> ByProfileId { get; }
    }

    private readonly struct IntervalCounters
    {
        public IntervalCounters(
            long requests,
            long accepted,
            long rejected,
            long triggerBeats,
            long initiateEntries,
            long initiateOriginals,
            long initiateSuppressed,
            long triggerVetoes,
            long projectileVetoes,
            long visionCreate,
            long visionAnalyze,
            long enemyRelations,
            long raycasts,
            long visionLatencySamples,
            long botLook,
            long botLookTimedSamples,
            long botLookUpdated,
            double createTotalMs,
            double createMaxMs,
            double analyzeTotalMs,
            double analyzeMaxMs,
            double latencyTotalMs,
            double latencyMaxMs,
            int latencyMaxFrames,
            double botLookTotalMs,
            double botLookMaxMs)
        {
            Requests = requests;
            Accepted = accepted;
            Rejected = rejected;
            TriggerBeats = triggerBeats;
            InitiateEntries = initiateEntries;
            InitiateOriginals = initiateOriginals;
            InitiateSuppressed = initiateSuppressed;
            TriggerVetoes = triggerVetoes;
            ProjectileVetoes = projectileVetoes;
            VisionCreate = visionCreate;
            VisionAnalyze = visionAnalyze;
            EnemyRelations = enemyRelations;
            Raycasts = raycasts;
            VisionLatencySamples = visionLatencySamples;
            BotLook = botLook;
            BotLookTimedSamples = botLookTimedSamples;
            BotLookUpdated = botLookUpdated;
            CreateTotalMs = createTotalMs;
            CreateMaxMs = createMaxMs;
            AnalyzeTotalMs = analyzeTotalMs;
            AnalyzeMaxMs = analyzeMaxMs;
            LatencyTotalMs = latencyTotalMs;
            LatencyMaxMs = latencyMaxMs;
            LatencyMaxFrames = latencyMaxFrames;
            BotLookTotalMs = botLookTotalMs;
            BotLookMaxMs = botLookMaxMs;
        }

        public long Requests { get; }
        public long Accepted { get; }
        public long Rejected { get; }
        public long TriggerBeats { get; }
        public long InitiateEntries { get; }
        public long InitiateOriginals { get; }
        public long InitiateSuppressed { get; }
        public long TriggerVetoes { get; }
        public long ProjectileVetoes { get; }
        public long VisionCreate { get; }
        public long VisionAnalyze { get; }
        public long EnemyRelations { get; }
        public long Raycasts { get; }
        public long VisionLatencySamples { get; }
        public long BotLook { get; }
        public long BotLookTimedSamples { get; }
        public long BotLookUpdated { get; }
        public double CreateTotalMs { get; }
        public double CreateMaxMs { get; }
        public double AnalyzeTotalMs { get; }
        public double AnalyzeMaxMs { get; }
        public double LatencyTotalMs { get; }
        public double LatencyMaxMs { get; }
        public int LatencyMaxFrames { get; }
        public double BotLookTotalMs { get; }
        public double BotLookMaxMs { get; }

        public string Summary =>
            $"shootRequests={Requests}; shootAccepted={Accepted}; shootRejected={Rejected}; triggerHeartbeats={TriggerBeats}; initiateShotEntries={InitiateEntries}; initiateShotOriginalRuns={InitiateOriginals}; initiateShotSuppressed={InitiateSuppressed}; vanguardTriggerVetoes={TriggerVetoes}; vanguardProjectileVetoes={ProjectileVetoes}; visionCreateCalls={VisionCreate}; visionAnalyzeCalls={VisionAnalyze}; visionPendingDelta={VisionCreate - VisionAnalyze}; visionEnemyRelations={EnemyRelations}; visionRaycasts={Raycasts}; visionCreateAvgMs={Average(CreateTotalMs, VisionCreate):0.000}; visionCreateMaxMs={CreateMaxMs:0.000}; visionAnalyzeAvgMs={Average(AnalyzeTotalMs, VisionAnalyze):0.000}; visionAnalyzeMaxMs={AnalyzeMaxMs:0.000}; visionCreateToAnalyzeAvgMs={Average(LatencyTotalMs, VisionLatencySamples):0.000}; visionCreateToAnalyzeMaxMs={LatencyMaxMs:0.000}; visionCreateToAnalyzeMaxFrames={LatencyMaxFrames}; botLookCalls={BotLook}; botLookTimedSamples={BotLookTimedSamples}; botLookEnemiesUpdated={BotLookUpdated}; botLookAvgMs={Average(BotLookTotalMs, BotLookTimedSamples):0.000}; botLookMaxMs={BotLookMaxMs:0.000}";

        public string CompactSummary =>
            $"requests={Requests}; accepted={Accepted}; initiateEntries={InitiateEntries}; initiateOriginals={InitiateOriginals}; initiateSuppressed={InitiateSuppressed}; triggerVetoes={TriggerVetoes}; projectileVetoes={ProjectileVetoes}; visionBatches={VisionAnalyze}; visionRaycasts={Raycasts}; visionLatencyMaxMs={LatencyMaxMs:0.000}; botLookCalls={BotLook}; visionCpuMaxMs={Math.Max(CreateMaxMs, AnalyzeMaxMs):0.000}; botLookMaxMs={BotLookMaxMs:0.000}";

        private static double Average(double total, long count) => count <= 0 ? 0d : total / count;
    }
}
#else
namespace Vanguard.Client.Runtime.Combat;
internal static class VanguardGlobalCombatProductionDiagnosticsService
{
    public const string StatusTag = "VANGUARD_GLOBAL_COMBAT_PRODUCTION_DIAGNOSTICS_STATUS";
    public static bool FireBoundariesEnabled => false;
    public static bool GlobalVisionBoundariesEnabled => false;
    public static void ResetForRaidLifecycle(string reason) { }
    public static void Tick() { }
}
#endif
