#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Comfort.Common;
using EFT;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Patches;
using Vanguard.Client.Raid.Services;

// Responsibility: Keeps temporary Operator runtime-binding gaps from being mistaken for permanent loss of identity or ownership during raid startup/reconciliation.
// Flow: Pending runtime records are rechecked against live raid/profile evidence on a bounded cadence; successful binds are accepted, stale candidates expire, and diagnostics report prolonged gaps.
// Authority boundary: The guard reconciles references only; the runtime registry/profile manifests remain identity authority and no Operator is fabricated merely to satisfy a missing bind.
// Invariant: A transient missing bind may recover, but unresolved state must stay bounded, observable and removable without contaminating another Operator/profile.
namespace Vanguard.Client.Raid.Runtime;

/// <summary>
/// Late runtime-binding safety net for Operators whose normal ActivateBot/finalize callback
/// completed without attaching the BotOwner to Vanguard.
///
/// Vanguard invariant:
/// - the canonical path is still ActivateBot -> group callback -> finalize callback -> BindOperator;
/// - no recursive reflection, scene scan, arbitrary property getter or unknown IEnumerable is allowed;
/// - while an Operator activation is in flight the guard performs no owner lookup at all;
/// - fallback lookup is event-assisted and limited to typed EFT collections with a strict item/time budget.
/// </summary>
internal static class VanguardRuntimeBindGuardService
{
    public const string StatusTag = "VANGUARD_COMBAT_BIND_COHESION_RECOVERY_STATUS";
    public const string SpawnSyncGuardStatusTag = "VANGUARD_SPAWN_SYNC_GUARD_STATUS";

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PostActivationGrace = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PendingCriticalDelay = TimeSpan.FromSeconds(10.0);
    private static readonly TimeSpan PendingLogInterval = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan ActivationWatchdogDelay = TimeSpan.FromSeconds(12.0);
    private static readonly TimeSpan CandidateRetention = TimeSpan.FromSeconds(30.0);

    private const int MaxTypedOwnersPerSource = 192;
    private const int MaxEventCandidates = 32;
    private const double MaxLookupBudgetMilliseconds = 2.0d;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, DateTimeOffset> FirstPendingSeenUtcByOperator = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogUtcByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CandidateOwner> CandidateByProfileId = new(StringComparer.OrdinalIgnoreCase);

    private static DateTimeOffset nextTickUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset lastActivationEndedUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset activationStartedUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;
    private static int activeActivationCount;
    private static int lastSpawned;
    private static int lastRuntimeRegistered;
    private static int lastRemaining;
    private static BotSpawner? subscribedSpawner;

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            DetachSpawnerSubscriptionLocked();
            FirstPendingSeenUtcByOperator.Clear();
            LastLogUtcByKey.Clear();
            CandidateByProfileId.Clear();
            nextTickUtc = DateTimeOffset.MinValue;
            lastActivationEndedUtc = DateTimeOffset.MinValue;
            activationStartedUtc = DateTimeOffset.MinValue;
            activeActivationCount = 0;
            lastSpawned = 0;
            lastRuntimeRegistered = 0;
            lastRemaining = 0;
        }

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_RUNTIME_BIND_GUARD_RESET reason={Safe(reason)}; pendingState=cleared; lookup=typed_bounded_event_assisted; tag={StatusTag}");
    }

    public static void RecordSpawnSummary(int spawned, int runtimeRegistered, int remaining, string reason)
    {
        lastSpawned = spawned;
        lastRuntimeRegistered = runtimeRegistered;
        lastRemaining = remaining;
        if (spawned > 0 && runtimeRegistered < spawned)
        {
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"VANGUARD_RUNTIME_BIND_CRITICAL spawned={spawned}; runtimeRegistered={runtimeRegistered}; remaining={remaining}; reason={Safe(reason)}; action=bounded_event_assisted_late_bind; tag={StatusTag}");
        }
        else
        {
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"VANGUARD_RUNTIME_BIND_HEALTHY spawned={spawned}; runtimeRegistered={runtimeRegistered}; remaining={remaining}; reason={Safe(reason)}; tag={StatusTag}");
        }
    }

    public static void BeginOperatorActivation(
        Vanguard.Client.Api.Dtos.VanguardRaidOperatorSnapshotDto snapshot,
        string expectedProfileId,
        BotSpawner? preferredSpawner)
    {
        var now = DateTimeOffset.UtcNow;
        int activationDepth;
        lock (Sync)
        {
            EnsureTypedSpawnerSubscriptionLocked(preferredSpawner);
            activeActivationCount++;
            activationDepth = activeActivationCount;
            if (activeActivationCount == 1)
            {
                activationStartedUtc = now;
            }
        }

        VanguardClientDiagnosticsLog.Info(
            SpawnSyncGuardStatusTag,
            $"VANGUARD_ACTIVATE_BOT_REQUESTED operator={Safe(snapshot.OperatorId)}; expectedProfile={Safe(expectedProfileId)}; activationDepth={activationDepth}; guardLookup=suspended; tag={SpawnSyncGuardStatusTag}");
    }

    public static void RecordActivationStage(
        Vanguard.Client.Api.Dtos.VanguardRaidOperatorSnapshotDto snapshot,
        string expectedProfileId,
        string stage,
        BotOwner? owner = null)
    {
        if (owner is not null)
        {
            CacheCandidate(owner, "activate_stage_" + Safe(stage));
        }

        int activationDepth;
        lock (Sync)
        {
            activationDepth = activeActivationCount;
        }

        VanguardClientDiagnosticsLog.Info(
            SpawnSyncGuardStatusTag,
            $"VANGUARD_ACTIVATE_BOT_STAGE operator={Safe(snapshot.OperatorId)}; expectedProfile={Safe(expectedProfileId)}; botProfile={Safe(owner?.ProfileId)}; stage={Safe(stage)}; activationDepth={activationDepth}; tag={SpawnSyncGuardStatusTag}");
    }

    public static void EndOperatorActivation(
        Vanguard.Client.Api.Dtos.VanguardRaidOperatorSnapshotDto snapshot,
        string expectedProfileId,
        bool taskCompleted,
        string reason)
    {
        int remainingDepth;
        lock (Sync)
        {
            activeActivationCount = Math.Max(0, activeActivationCount - 1);
            remainingDepth = activeActivationCount;
            if (activeActivationCount == 0)
            {
                lastActivationEndedUtc = DateTimeOffset.UtcNow;
                activationStartedUtc = DateTimeOffset.MinValue;
            }
        }

        string eventName = taskCompleted
            ? "VANGUARD_ACTIVATE_BOT_RETURNED"
            : "VANGUARD_ACTIVATE_BOT_ABORTED";
        VanguardClientDiagnosticsLog.Info(
            SpawnSyncGuardStatusTag,
            $"{eventName} operator={Safe(snapshot.OperatorId)}; expectedProfile={Safe(expectedProfileId)}; activationDepth={remainingDepth}; taskCompleted={taskCompleted}; reason={Safe(reason)}; guardLookup={(remainingDepth == 0 ? "post_activation_grace" : "suspended")}; tag={SpawnSyncGuardStatusTag}");
    }

    public static void NotifyLateBindPending(Vanguard.Client.Api.Dtos.VanguardRaidOperatorSnapshotDto snapshot, string expectedProfileId, string reason)
    {
        string key = Normalize(snapshot.OperatorId);
        lock (Sync)
        {
            if (!string.IsNullOrWhiteSpace(key) && !FirstPendingSeenUtcByOperator.ContainsKey(key))
            {
                FirstPendingSeenUtcByOperator[key] = DateTimeOffset.UtcNow;
            }
        }

        VanguardClientDiagnosticsLog.Warning(
            StatusTag,
            $"VANGUARD_RUNTIME_BIND_PENDING operator={Safe(snapshot.OperatorId)}; expectedProfile={Safe(expectedProfileId)}; owner={Safe(snapshot.OwnerProfileId)}; reason={Safe(reason)}; action=guard_will_use_typed_bounded_sources; recursiveReflection=false; sceneScan=false; tag={StatusTag}");
    }

    public static void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"VANGUARD_RUNTIME_BIND_GUARD_BOOT interval={TickInterval.TotalSeconds:0.00}; criticalDelay={PendingCriticalDelay.TotalSeconds:0.00}; mode=event_assisted_typed_lookup; recursiveReflection=false; sceneScan=false; maxOwnersPerSource={MaxTypedOwnersPerSource}; budgetMs={MaxLookupBudgetMilliseconds:0.0}; tag={StatusTag}");
        }

        if (now < nextTickUtc)
        {
            return;
        }

        nextTickUtc = now + TickInterval;
        if (IsActivationInProgress(now))
        {
            return;
        }

        lock (Sync)
        {
            EnsureTypedSpawnerSubscriptionLocked();
        }

        if (lastActivationEndedUtc != DateTimeOffset.MinValue && now - lastActivationEndedUtc < PostActivationGrace)
        {
            return;
        }

        PruneExpiredCandidates(now);
        var pending = VanguardRaidOperatorRuntimeRegistry.GetPendingRuntimeBindings();
        if (pending.Count == 0)
        {
            return;
        }

        var budget = Stopwatch.StartNew();
        foreach (var item in pending)
        {
            if (budget.Elapsed.TotalMilliseconds >= MaxLookupBudgetMilliseconds)
            {
                LogBudgetYield(now, pending.Count, budget.Elapsed.TotalMilliseconds);
                break;
            }

            TryResolvePending(item, now, budget);
        }
    }

    /// <summary>
    /// Used by the bounded late-bind loop after ActivateBot has returned.
    /// This method never performs reflection or scene-wide discovery.
    /// </summary>
    internal static bool TryFindExpectedBotOwnerByProfileId(string expectedProfileId, out BotOwner? owner, out string source)
    {
        return TryFindExpectedBotOwnerByProfileIdCore(
            expectedProfileId,
            null,
            null,
            Stopwatch.StartNew(),
            out owner,
            out source);
    }

    internal static bool TryFindExpectedBotOwnerByProfileId(
        string expectedProfileId,
        BotsController? preferredController,
        BotSpawner? preferredSpawner,
        out BotOwner? owner,
        out string source)
    {
        return TryFindExpectedBotOwnerByProfileIdCore(
            expectedProfileId,
            preferredController,
            preferredSpawner,
            Stopwatch.StartNew(),
            out owner,
            out source);
    }

    private static bool TryFindExpectedBotOwnerByProfileIdCore(
        string expectedProfileId,
        BotsController? preferredController,
        BotSpawner? preferredSpawner,
        Stopwatch budget,
        out BotOwner? owner,
        out string source)
    {
        owner = null;
        source = "none";
        string expected = Normalize(expectedProfileId);
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        lock (Sync)
        {
            EnsureTypedSpawnerSubscriptionLocked(preferredSpawner);
            if (CandidateByProfileId.TryGetValue(expected, out var candidate) && IsUsable(candidate.Owner, expected))
            {
                owner = candidate.Owner;
                source = "bot_created_event_cache:" + Safe(candidate.Source);
                return true;
            }
        }

        if (budget.Elapsed.TotalMilliseconds >= MaxLookupBudgetMilliseconds)
        {
            source = "typed_lookup_budget_exhausted_before_sources";
            return false;
        }

        var controller = preferredController ?? VanguardBotsControllerStatePatch.ActiveController;
        var spawner = preferredSpawner ?? controller?.BotSpawner;
        var controllerBots = controller?.Bots;
        if (TryFindInBotOwners(controllerBots?.BotOwners, expected, "bots_controller.BotOwners", budget, out owner, out source))
        {
            CacheCandidate(owner!, source);
            return true;
        }

        var spawnerBots = spawner?.Bots;
        if (!ReferenceEquals(controllerBots, spawnerBots)
            && TryFindInBotOwners(spawnerBots?.BotOwners, expected, "bot_spawner.BotOwners", budget, out owner, out source))
        {
            CacheCandidate(owner!, source);
            return true;
        }

        if (TryFindInPlayers(spawner?.AllPlayers, expected, "bot_spawner.AllPlayers", budget, out owner, out source))
        {
            CacheCandidate(owner!, source);
            return true;
        }

        GameWorld? gameWorld = null;
        try
        {
            gameWorld = Singleton<GameWorld>.Instance;
        }
        catch
        {
            gameWorld = null;
        }

        if (TryFindInPlayers(gameWorld?.AllAlivePlayersList, expected, "game_world.AllAlivePlayersList", budget, out owner, out source))
        {
            CacheCandidate(owner!, source);
            return true;
        }

        if (TryFindInRegisteredPlayers(gameWorld?.RegisteredPlayers, expected, "game_world.RegisteredPlayers", budget, out owner, out source))
        {
            CacheCandidate(owner!, source);
            return true;
        }

        owner = null;
        source = budget.Elapsed.TotalMilliseconds >= MaxLookupBudgetMilliseconds ? "typed_lookup_budget_exhausted" : "typed_sources_no_match";
        return false;
    }

    private static bool IsActivationInProgress(DateTimeOffset now)
    {
        int count;
        DateTimeOffset started;
        lock (Sync)
        {
            count = activeActivationCount;
            started = activationStartedUtc;
        }

        if (count <= 0)
        {
            return false;
        }

        if (started != DateTimeOffset.MinValue && now - started >= ActivationWatchdogDelay)
        {
            string key = "activation_watchdog";
            if (ShouldLog(key, now, PendingLogInterval))
            {
                VanguardClientDiagnosticsLog.Warning(
                    SpawnSyncGuardStatusTag,
                    $"VANGUARD_ACTIVATE_BOT_WATCHDOG activationDepth={count}; elapsed={(now - started).TotalSeconds:0.00}; action=observe_only_no_scan_no_forced_cancel; tag={SpawnSyncGuardStatusTag}");
            }
        }

        return true;
    }

    private static void TryResolvePending(
        VanguardRaidOperatorRuntimeRegistry.VanguardPendingRuntimeBinding item,
        DateTimeOffset now,
        Stopwatch budget)
    {
        var snapshot = item.Snapshot;
        string operatorId = Normalize(snapshot.OperatorId);
        string expectedProfileId = Normalize(item.ExpectedBotProfileId);
        if (string.IsNullOrWhiteSpace(operatorId))
        {
            return;
        }

        DateTimeOffset firstSeenUtc;
        lock (Sync)
        {
            if (!FirstPendingSeenUtcByOperator.TryGetValue(operatorId, out firstSeenUtc))
            {
                firstSeenUtc = now;
                FirstPendingSeenUtcByOperator[operatorId] = firstSeenUtc;
            }
        }

        if (string.IsNullOrWhiteSpace(expectedProfileId))
        {
            LogPending(now, operatorId, snapshot, expectedProfileId, "missing_expected_profile", firstSeenUtc, critical: false);
            return;
        }

        if (budget.Elapsed.TotalMilliseconds >= MaxLookupBudgetMilliseconds)
        {
            return;
        }

        if (TryFindExpectedBotOwnerByProfileIdCore(
                expectedProfileId,
                null,
                null,
                budget,
                out var owner,
                out var source)
            && owner is not null)
        {
            bool adopted = VanguardRaidOperatorSpawnService.TryBindExpectedOperatorByBotOwner(
                owner,
                snapshot,
                expectedProfileId,
                "runtime_bind_guard_" + source);
            if (adopted)
            {
                lock (Sync)
                {
                    FirstPendingSeenUtcByOperator.Remove(operatorId);
                    CandidateByProfileId.Remove(expectedProfileId);
                }

                VanguardClientDiagnosticsLog.Info(
                    StatusTag,
                    $"VANGUARD_RUNTIME_BIND_REPAIRED operator={Safe(snapshot.OperatorId)}; expectedProfile={Safe(expectedProfileId)}; botProfile={Safe(owner.ProfileId)}; source={Safe(source)}; spawned={lastSpawned}; runtimeRegisteredBefore={lastRuntimeRegistered}; remainingBefore={lastRemaining}; tag={StatusTag}");
                return;
            }
        }

        bool critical = now - firstSeenUtc >= PendingCriticalDelay;
        LogPending(
            now,
            operatorId,
            snapshot,
            expectedProfileId,
            owner is null ? source : "adoption_failed",
            firstSeenUtc,
            critical);
    }

    private static void EnsureTypedSpawnerSubscriptionLocked(BotSpawner? preferredSpawner = null)
    {
        BotSpawner? current = preferredSpawner ?? VanguardBotsControllerStatePatch.ActiveController?.BotSpawner;
        if (ReferenceEquals(current, subscribedSpawner))
        {
            return;
        }

        DetachSpawnerSubscriptionLocked();
        subscribedSpawner = current;
        if (subscribedSpawner is null)
        {
            return;
        }

        try
        {
            subscribedSpawner.OnBotCreated += OnBotCreated;
            subscribedSpawner.OnBotRemoved += OnBotRemoved;
            VanguardClientDiagnosticsLog.Info(
                SpawnSyncGuardStatusTag,
                $"VANGUARD_TYPED_BIND_SUBSCRIBED source=BotSpawner.OnBotCreated; recursiveReflection=false; sceneScan=false; tag={SpawnSyncGuardStatusTag}");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                SpawnSyncGuardStatusTag,
                $"VANGUARD_TYPED_BIND_SUBSCRIBE_FAILED type={exception.GetType().Name}; message={Safe(exception.Message)}; tag={SpawnSyncGuardStatusTag}");
            subscribedSpawner = null;
        }
    }

    private static void DetachSpawnerSubscriptionLocked()
    {
        if (subscribedSpawner is null)
        {
            return;
        }

        try
        {
            subscribedSpawner.OnBotCreated -= OnBotCreated;
            subscribedSpawner.OnBotRemoved -= OnBotRemoved;
        }
        catch
        {
            // Raid teardown may destroy the underlying EFT object before Vanguard resets.
        }

        subscribedSpawner = null;
    }

    private static void OnBotCreated(BotOwner owner)
    {
        if (owner is null || string.IsNullOrWhiteSpace(owner.ProfileId))
        {
            return;
        }

        string profileId = Normalize(owner.ProfileId);
        bool expected = false;
        foreach (var pending in VanguardRaidOperatorRuntimeRegistry.GetPendingRuntimeBindings())
        {
            if (string.Equals(Normalize(pending.ExpectedBotProfileId), profileId, StringComparison.OrdinalIgnoreCase))
            {
                expected = true;
                break;
            }
        }

        if (!expected)
        {
            return;
        }

        CacheCandidate(owner, "BotSpawner.OnBotCreated");
        VanguardClientDiagnosticsLog.Info(
            SpawnSyncGuardStatusTag,
            $"VANGUARD_TYPED_BIND_CANDIDATE profile={Safe(profileId)}; source=BotSpawner.OnBotCreated; action=cache_only_normal_callback_remains_canonical; tag={SpawnSyncGuardStatusTag}");
    }

    private static void OnBotRemoved(BotOwner owner)
    {
        string profileId = Normalize(owner?.ProfileId);
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        lock (Sync)
        {
            CandidateByProfileId.Remove(profileId);
        }
    }

    private static void CacheCandidate(BotOwner owner, string source)
    {
        if (owner is null || string.IsNullOrWhiteSpace(owner.ProfileId))
        {
            return;
        }

        string profileId = Normalize(owner.ProfileId);
        lock (Sync)
        {
            if (CandidateByProfileId.Count >= MaxEventCandidates && !CandidateByProfileId.ContainsKey(profileId))
            {
                string? oldestKey = null;
                DateTimeOffset oldest = DateTimeOffset.MaxValue;
                foreach (var pair in CandidateByProfileId)
                {
                    if (pair.Value.SeenUtc < oldest)
                    {
                        oldest = pair.Value.SeenUtc;
                        oldestKey = pair.Key;
                    }
                }

                if (!string.IsNullOrWhiteSpace(oldestKey))
                {
                    CandidateByProfileId.Remove(oldestKey);
                }
            }

            CandidateByProfileId[profileId] = new CandidateOwner(owner, DateTimeOffset.UtcNow, source);
        }
    }

    private static void PruneExpiredCandidates(DateTimeOffset now)
    {
        lock (Sync)
        {
            var expired = new List<string>();
            foreach (var pair in CandidateByProfileId)
            {
                if (now - pair.Value.SeenUtc > CandidateRetention || !IsUsable(pair.Value.Owner, pair.Key))
                {
                    expired.Add(pair.Key);
                }
            }

            foreach (string key in expired)
            {
                CandidateByProfileId.Remove(key);
            }
        }
    }

    private static bool TryFindInBotOwners(
        IEnumerable<BotOwner>? owners,
        string expectedProfileId,
        string sourceName,
        Stopwatch budget,
        out BotOwner? owner,
        out string source)
    {
        owner = null;
        source = sourceName;
        if (owners is null || budget.Elapsed.TotalMilliseconds >= MaxLookupBudgetMilliseconds)
        {
            return false;
        }

        if (owners is not ICollection<BotOwner> materializedOwners)
        {
            source = sourceName + ":non_materialized_source_rejected";
            return false;
        }

        int inspected = 0;
        try
        {
            foreach (var candidate in materializedOwners)
            {
                if (++inspected > MaxTypedOwnersPerSource || budget.Elapsed.TotalMilliseconds >= MaxLookupBudgetMilliseconds)
                {
                    source = sourceName + ":budget_or_count_limit";
                    return false;
                }

                if (IsUsable(candidate, expectedProfileId))
                {
                    owner = candidate;
                    source = sourceName;
                    return true;
                }
            }
        }
        catch (InvalidOperationException)
        {
            source = sourceName + ":collection_changed";
        }
        catch (Exception exception)
        {
            source = sourceName + ":" + exception.GetType().Name;
        }

        return false;
    }

    private static bool TryFindInPlayers(
        IList<Player>? players,
        string expectedProfileId,
        string sourceName,
        Stopwatch budget,
        out BotOwner? owner,
        out string source)
    {
        owner = null;
        source = sourceName;
        if (players is null || budget.Elapsed.TotalMilliseconds >= MaxLookupBudgetMilliseconds)
        {
            return false;
        }

        int count;
        try
        {
            count = Math.Min(players.Count, MaxTypedOwnersPerSource);
        }
        catch
        {
            return false;
        }

        for (int index = 0; index < count && budget.Elapsed.TotalMilliseconds < MaxLookupBudgetMilliseconds; index++)
        {
            Player? player;
            try
            {
                player = players[index];
            }
            catch
            {
                continue;
            }

            var candidate = player?.AIData?.BotOwner;
            if (IsUsable(candidate, expectedProfileId))
            {
                owner = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindInRegisteredPlayers(
        IList<IPlayer>? players,
        string expectedProfileId,
        string sourceName,
        Stopwatch budget,
        out BotOwner? owner,
        out string source)
    {
        owner = null;
        source = sourceName;
        if (players is null || budget.Elapsed.TotalMilliseconds >= MaxLookupBudgetMilliseconds)
        {
            return false;
        }

        int count;
        try
        {
            count = Math.Min(players.Count, MaxTypedOwnersPerSource);
        }
        catch
        {
            return false;
        }

        for (int index = 0; index < count && budget.Elapsed.TotalMilliseconds < MaxLookupBudgetMilliseconds; index++)
        {
            Player? player;
            try
            {
                player = players[index] as Player;
            }
            catch
            {
                continue;
            }

            var candidate = player?.AIData?.BotOwner;
            if (IsUsable(candidate, expectedProfileId))
            {
                owner = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsUsable(BotOwner? owner, string expectedProfileId)
    {
        try
        {
            return owner is not null
                && !string.IsNullOrWhiteSpace(owner.ProfileId)
                && string.Equals(owner.ProfileId, expectedProfileId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void LogBudgetYield(DateTimeOffset now, int pendingCount, double elapsedMilliseconds)
    {
        if (!ShouldLog("lookup_budget_yield", now, PendingLogInterval))
        {
            return;
        }

        VanguardClientDiagnosticsLog.Warning(
            SpawnSyncGuardStatusTag,
            $"VANGUARD_TYPED_LOOKUP_BUDGET_YIELD pending={pendingCount}; elapsedMs={elapsedMilliseconds:0.00}; budgetMs={MaxLookupBudgetMilliseconds:0.00}; action=continue_next_tick; tag={SpawnSyncGuardStatusTag}");
    }

    private static void LogPending(
        DateTimeOffset now,
        string operatorId,
        Vanguard.Client.Api.Dtos.VanguardRaidOperatorSnapshotDto snapshot,
        string expectedProfileId,
        string reason,
        DateTimeOffset firstSeenUtc,
        bool critical)
    {
        string key = operatorId + "|" + reason + "|" + critical;
        if (!ShouldLog(key, now, PendingLogInterval))
        {
            return;
        }

        string eventName = critical ? "VANGUARD_RUNTIME_BIND_UNRESOLVED_CRITICAL" : "VANGUARD_RUNTIME_BIND_PENDING_SCAN";
        string elapsed = (now - firstSeenUtc).TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        VanguardClientDiagnosticsLog.Warning(
            StatusTag,
            $"{eventName} operator={Safe(snapshot.OperatorId)}; expectedProfile={Safe(expectedProfileId)}; owner={Safe(snapshot.OwnerProfileId)}; raid={Safe(snapshot.RaidSessionId)}; elapsed={elapsed}; reason={Safe(reason)}; action=continue_typed_bounded_lookup; recursiveReflection=false; sceneScan=false; spawned={lastSpawned}; runtimeRegistered={lastRuntimeRegistered}; remaining={lastRemaining}; tag={StatusTag}");
    }

    private static bool ShouldLog(string key, DateTimeOffset now, TimeSpan interval)
    {
        lock (Sync)
        {
            if (LastLogUtcByKey.TryGetValue(key, out var last) && now - last < interval)
            {
                return false;
            }

            LastLogUtcByKey[key] = now;
            return true;
        }
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(';', ',');

    private readonly record struct CandidateOwner(BotOwner Owner, DateTimeOffset SeenUtc, string Source);
}
#endif
