#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using EFT;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;

// Responsibility: Records the complete grenade detection-to-reaction chain so grenade behavior can be diagnosed without changing what an Operator does.
// Flow: Grenade events enter the raid-scoped registry, nearby Operator hazard snapshots are sampled, and detection/reaction milestones are correlated into bounded diagnostic observations.
// Authority boundary: This service is observation only; the hazard registry and emergency-evasion executor own their respective state, while EFT owns the physical grenade.
// Invariant: Audit data may explain a reaction but must never create movement, targets, SAIN decisions or execution leases.
namespace Vanguard.Client.Runtime.Grenades;

/// <summary>
/// Event-driven, read-only grenade audit. grenade subsystem records the complete detection and reaction chain
/// without moving Operators, changing targets, assigning SAIN decisions or opening execution leases.
/// </summary>
internal static class VanguardGrenadeHazardAuditService
{
    private static readonly object Sync = new();
    private static readonly Dictionary<Grenade, VanguardGrenadeObservation> Observations = new(ReferenceComparer<Grenade>.Instance);
    private static float nextTickAt;
    private static bool bootLogged;

    public static void ResetForRaidLifecycle(string source)
    {
        VanguardGrenadeHazardRegistry.ResetForRaidLifecycle(source);
        VanguardGrenadeObservation[] observations;
        lock (Sync)
        {
            observations = Observations.Values.ToArray();
            Observations.Clear();
            nextTickAt = 0f;
            bootLogged = false;
        }

        foreach (VanguardGrenadeObservation observation in observations)
        {
            TryUnsubscribe(observation);
        }

        VanguardClientDiagnosticsLog.Operational(
            VanguardGrenadeHazardPolicy.StatusTag,
            () => $"VANGUARD_GRENADE_DIAGNOSTIC_RESET source={Safe(source)}; grenadesCleared={observations.Length}; readOnly=true; movementMutation=false; targetMutation=false; sainDecisionMutation=false");
    }

    public static void Tick()
    {
        if (!VanguardFikaCompat.IsRaidAuthority || Time.time < nextTickAt)
        {
            return;
        }

        nextTickAt = Time.time + VanguardGrenadeHazardPolicy.TickIntervalSeconds;
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Diagnostic(
                VanguardGrenadeHazardPolicy.StatusTag,
                () => $"VANGUARD_GRENADE_HAZARD_DIAGNOSTIC_BOOT readOnly=true; eventDriven=true; transitionLogsOnly=true; authority=headless_or_host; sourceResolution=true; friendlyAndHostileSources=true; nativeBewareGrenadeAudit=true; sainTrackerAudit=true; sainDecisionAudit=true; movementAuthorityAudit=true; movementMutation=false; targetMutation=false; sainDecisionMutation=false; executionLeaseMutation=false; tickSeconds={VanguardGrenadeHazardPolicy.TickIntervalSeconds:0.00}; build={VanguardBuildVersion.BuildLabel}");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        VanguardGrenadeObservation[] grenades;
        lock (Sync)
        {
            grenades = Observations.Values.ToArray();
        }

        if (grenades.Length == 0)
        {
            return;
        }

        IReadOnlyList<VanguardRaidOperatorRuntimeRecord> operators = VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators();
        foreach (VanguardGrenadeObservation observation in grenades)
        {
            ProcessGrenade(observation, operators, now);
        }

        CleanupTerminal(now);
    }

    public static void ObserveThrow(Grenade? grenade, Vector3 position, Vector3 force, float mass)
    {
        if (!CanObserve(grenade))
        {
            return;
        }

        VanguardGrenadeObservation observation = GetOrCreateGrenade(grenade!, position);
        observation.ThrowPosition = position;
        observation.ThrowForce = force;
        observation.ThrowMass = mass;
        observation.CurrentPosition = grenade!.transform.position;
        ResolveSource(observation, grenade.ProfileId);
        SubscribeDestroy(observation);
        VanguardGrenadeHazardRegistry.ObserveThrow(grenade, position, force, mass, DateTimeOffset.UtcNow);

        VanguardClientDiagnosticsLog.Operational(
            VanguardGrenadeHazardPolicy.ThrowObservedTag,
            () => $"grenade={GrenadeKey(observation)}; type={Safe(observation.GrenadeType)}; source={Safe(observation.SourceProfileId)}; sourceName={Safe(observation.SourceName)}; sourceRelation={observation.SourceRelation}; throwPos={VectorText(position)}; force={VectorText(force)}; forceMagnitude={force.magnitude:0.00}; mass={mass:0.00}; smoke={Bool(VanguardGrenadeRuntimeResolver.IsSmoke(grenade))}; authority=headless_or_host; readOnly=true");
        VanguardClientDiagnosticsLog.Operational(
            VanguardGrenadeHazardPolicy.SourceResolvedTag,
            () => $"grenade={GrenadeKey(observation)}; source={Safe(observation.SourceProfileId)}; sourceName={Safe(observation.SourceName)}; relation={observation.SourceRelation}; sourceUnknownDoesNotSuppressDanger=true");
    }

    public static void ObserveExplosion(Vector3 explosionPosition, string? sourceProfileId, int throwableId)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        VanguardGrenadeHazardRegistry.ObserveExplosion(explosionPosition, sourceProfileId, throwableId, now);
        VanguardGrenadeObservation? observation = FindObservationForExplosion(throwableId, sourceProfileId, explosionPosition);
        if (observation == null)
        {
            VanguardClientDiagnosticsLog.Operational(
                VanguardGrenadeHazardPolicy.ExplosionTag,
                () => $"grenadeId={throwableId}; source={Safe(sourceProfileId)}; explosionPos={VectorText(explosionPosition)}; tracked=false; readOnly=true");
            return;
        }

        if (!string.IsNullOrWhiteSpace(sourceProfileId))
        {
            ResolveSource(observation, sourceProfileId);
        }
        observation.ExplosionPosition = explosionPosition;
        observation.CurrentPosition = explosionPosition;
        observation.TerminalKind = VanguardGrenadeTerminalKind.Exploded;
        observation.TerminalAtUtc = now;
        VanguardClientDiagnosticsLog.Operational(
            VanguardGrenadeHazardPolicy.ExplosionTag,
            () => $"grenade={GrenadeKey(observation)}; source={Safe(observation.SourceProfileId)}; sourceRelation={observation.SourceRelation}; explosionPos={VectorText(explosionPosition)}; tracked=true; operatorPairs={observation.Operators.Count}; readOnly=true");
    }

    public static void ObserveCollision(Grenade? grenade, float maxRange)
    {
        if (!CanObserve(grenade))
        {
            return;
        }

        VanguardGrenadeObservation observation = GetOrCreateGrenade(grenade!, grenade!.transform.position);
        observation.CurrentPosition = grenade.transform.position;
        observation.CollisionCount++;
        observation.LastCollisionMaxRange = maxRange;
        VanguardGrenadeHazardRegistry.ObserveCollision(grenade, observation.CurrentPosition, DateTimeOffset.UtcNow);
        if (observation.CollisionCount == 1)
        {
            VanguardClientDiagnosticsLog.Operational(
                VanguardGrenadeHazardPolicy.CollisionObservedTag,
                () => $"grenade={GrenadeKey(observation)}; source={Safe(observation.SourceProfileId)}; collisionCount=1; grenadePos={VectorText(observation.CurrentPosition)}; maxHearingRange={maxRange:0.00}; subsequentCollisionsAggregated=true");
        }
    }

    public static void ObserveDangerPointUpdate(Grenade? grenade, Vector3 dangerPoint, string source)
    {
        if (!CanObserve(grenade))
        {
            return;
        }

        VanguardGrenadeObservation observation = GetOrCreateGrenade(grenade!, grenade!.transform.position);
        bool first = !observation.DangerPointKnown;
        float delta = first ? float.PositiveInfinity : Vector3.Distance(observation.DangerPoint, dangerPoint);
        observation.DangerPoint = dangerPoint;
        observation.DangerPointKnown = true;
        observation.CurrentPosition = grenade.transform.position;
        VanguardGrenadeHazardRegistry.ObserveDangerPoint(grenade, dangerPoint, DateTimeOffset.UtcNow);
        if (!first && delta < VanguardGrenadeHazardPolicy.SignificantDangerPointChangeMeters)
        {
            return;
        }

        VanguardClientDiagnosticsLog.Operational(
            VanguardGrenadeHazardPolicy.DangerUpdatedTag,
            () => $"grenade={GrenadeKey(observation)}; sourceEvent={Safe(source)}; dangerPoint={VectorText(dangerPoint)}; grenadePos={VectorText(observation.CurrentPosition)}; delta={(float.IsPositiveInfinity(delta) ? "initial" : delta.ToString("0.00", CultureInfo.InvariantCulture))}; transitionOnly=true");
    }

    public static void ObserveSainReaction(object? reaction, Grenade? grenade, Vector3 dangerPoint, string? sourceProfileId, bool afterCall)
    {
        if (!CanObserve(grenade))
        {
            return;
        }

        BotOwner? owner = VanguardGrenadeRuntimeResolver.ResolveBotOwner(reaction);
        if (!TryGetOperator(owner, out VanguardRaidOperatorRuntimeRecord runtime))
        {
            return;
        }

        VanguardGrenadeObservation observation = GetOrCreateGrenade(grenade!, grenade!.transform.position);
        ResolveSource(observation, sourceProfileId);
        observation.DangerPoint = dangerPoint;
        observation.DangerPointKnown = true;
        VanguardGrenadeOperatorObservation pair = GetOrCreatePair(observation, runtime);
        pair.SainReactionObserved = true;

        if (!afterCall)
        {
            return;
        }

        object? trackers = VanguardGrenadeRuntimeResolver.GetMember(reaction, "EnemyGrenadesList");
        bool trackerCreated = ContainsGrenadeKey(trackers, grenade!);
        bool nativeFallback = !trackerCreated && pair.NativeDangerRequestObserved;
        bool returnedWithoutTrackerOrNative = !trackerCreated && !pair.NativeDangerRequestObserved;
        pair.SainTrackerCreated |= trackerCreated;
        pair.SainTrackerFallbackToNative |= nativeFallback;
        pair.SainReactionReturnedWithoutTrackerOrNative |= returnedWithoutTrackerOrNative;
        VanguardClientDiagnosticsLog.Operational(
            VanguardGrenadeHazardPolicy.TrackerCreatedTag,
            () => $"grenade={GrenadeKey(observation)}; operator={Safe(pair.OperatorId)}; botProfile={Safe(pair.BotProfileId)}; source={Safe(observation.SourceProfileId)}; dangerPoint={VectorText(dangerPoint)}; trackerCreated={Bool(trackerCreated)}; nativeFallback={Bool(nativeFallback)}; returnedWithoutTrackerOrNative={Bool(returnedWithoutTrackerOrNative)}; reactionObserved=true");
    }

    public static void ObserveTrackerSpotted(object? tracker)
    {
        BotOwner? owner = VanguardGrenadeRuntimeResolver.ResolveBotOwner(tracker);
        Grenade? grenade = VanguardGrenadeRuntimeResolver.ResolveGrenade(tracker);
        if (!CanObserve(grenade) || !TryGetOperator(owner, out VanguardRaidOperatorRuntimeRecord runtime))
        {
            return;
        }

        VanguardGrenadeObservation observation = GetOrCreateGrenade(grenade!, grenade!.transform.position);
        VanguardGrenadeOperatorObservation pair = GetOrCreatePair(observation, runtime);
        VanguardGrenadeRuntimeResolver.TryReadTrackerState(tracker, out bool spotted, out bool canReact, out Vector3 dangerPoint);
        pair.SainTrackerCreated = true;
        pair.SainSpotted |= spotted;
        pair.SainCanReactObserved |= canReact;
        if (dangerPoint != default)
        {
            observation.DangerPoint = dangerPoint;
            observation.DangerPointKnown = true;
        }

        VanguardClientDiagnosticsLog.Operational(
            VanguardGrenadeHazardPolicy.SpottedTag,
            () => $"grenade={GrenadeKey(observation)}; operator={Safe(pair.OperatorId)}; botProfile={Safe(pair.BotProfileId)}; spotted={Bool(spotted)}; canReact={Bool(canReact)}; dangerPoint={VectorText(observation.DangerPointKnown ? observation.DangerPoint : grenade.transform.position)}; trackerCreated=true");
    }

    public static void ObserveTrackerUpdate(object? tracker)
    {
        BotOwner? owner = VanguardGrenadeRuntimeResolver.ResolveBotOwner(tracker);
        Grenade? grenade = VanguardGrenadeRuntimeResolver.ResolveGrenade(tracker);
        if (!CanObserve(grenade) || !TryGetOperator(owner, out VanguardRaidOperatorRuntimeRecord runtime))
        {
            return;
        }

        VanguardGrenadeObservation observation = GetOrCreateGrenade(grenade!, grenade!.transform.position);
        VanguardGrenadeOperatorObservation pair = GetOrCreatePair(observation, runtime);
        VanguardGrenadeRuntimeResolver.TryReadTrackerState(tracker, out bool spotted, out bool canReact, out Vector3 dangerPoint);
        pair.SainTrackerCreated = true;
        pair.SainSpotted |= spotted;
        if (dangerPoint != default)
        {
            observation.DangerPoint = dangerPoint;
            observation.DangerPointKnown = true;
        }

        if (!canReact || pair.SainCanReactObserved)
        {
            return;
        }

        pair.SainCanReactObserved = true;
        RefreshAuthority(pair);
        VanguardClientDiagnosticsLog.Operational(
            VanguardGrenadeHazardPolicy.ReactionReadyTag,
            () => $"grenade={GrenadeKey(observation)}; operator={Safe(pair.OperatorId)}; botProfile={Safe(pair.BotProfileId)}; spotted={Bool(spotted)}; canReact=true; dangerPoint={VectorText(observation.DangerPointKnown ? observation.DangerPoint : grenade.transform.position)}; nativeDanger={Bool(pair.NativeDangerPointPresent)}; sainDecision={Safe(pair.LastSainDecision)}; movementAuthority={Safe(pair.LastMovementAuthority)}");
    }

    public static void ObserveNativeDanger(object? bewareGrenade, Vector3 dangerPoint, Grenade? grenade, bool afterCall)
    {
        BotOwner? owner = VanguardGrenadeRuntimeResolver.ResolveBotOwner(bewareGrenade);
        if (!CanObserve(grenade) || !TryGetOperator(owner, out VanguardRaidOperatorRuntimeRecord runtime))
        {
            return;
        }

        VanguardGrenadeObservation observation = GetOrCreateGrenade(grenade!, grenade!.transform.position);
        VanguardGrenadeOperatorObservation pair = GetOrCreatePair(observation, runtime);
        if (!afterCall)
        {
            pair.NativeDangerRequestObserved = true;
            return;
        }

        bool present = VanguardGrenadeRuntimeResolver.TryReadNativeDangerState(bewareGrenade, out bool dangerPresent, out Grenade? nativeGrenade, out Vector3 nativeDanger)
            && dangerPresent
            && (nativeGrenade == null || ReferenceEquals(nativeGrenade, grenade));
        pair.NativeDangerRequestObserved = true;
        pair.NativeDangerWritten |= present;
        bool nativePresenceChanged = present && !pair.NativeDangerPointPresent;
        pair.NativeDangerPointPresent |= present;
        if (nativeDanger != default)
        {
            observation.DangerPoint = nativeDanger;
            observation.DangerPointKnown = true;
        }

        if (pair.NativeDangerLogged && !nativePresenceChanged)
        {
            return;
        }

        pair.NativeDangerLogged = true;
        RefreshAuthority(pair);
        VanguardClientDiagnosticsLog.Operational(
            VanguardGrenadeHazardPolicy.NativeDangerWrittenTag,
            () => $"grenade={GrenadeKey(observation)}; operator={Safe(pair.OperatorId)}; botProfile={Safe(pair.BotProfileId)}; requestedDanger={VectorText(dangerPoint)}; requestObserved=true; writeConfirmed={Bool(present)}; nativeDangerPresent={Bool(pair.NativeDangerPointPresent)}; nativeDanger={VectorText(nativeDanger)}; sainDecision={Safe(pair.LastSainDecision)}; brain={Safe(pair.LastBrainLayer)}/{Safe(pair.LastBrainNode)}; movementClass={Safe(pair.LastMovementClassification)}; movementAuthority={Safe(pair.LastMovementAuthority)}; execution={Safe(pair.LastExecutionIntent)}/{Safe(pair.LastExecutionWindow)}");
    }

    public static void ObserveNativeShallRunAway(
        object? bewareGrenade,
        Grenade? capturedGrenade,
        Vector3 capturedDangerPoint,
        bool capturedDangerPresent,
        bool result)
    {
        BotOwner? owner = VanguardGrenadeRuntimeResolver.ResolveBotOwner(bewareGrenade);
        bool currentPresent = VanguardGrenadeRuntimeResolver.TryReadNativeDangerState(
            bewareGrenade,
            out bool dangerPresent,
            out Grenade? currentGrenade,
            out Vector3 currentDangerPoint) && dangerPresent;
        Grenade? grenade = currentGrenade ?? capturedGrenade;
        Vector3 dangerPoint = currentDangerPoint != default ? currentDangerPoint : capturedDangerPoint;
        if ((!currentPresent && !capturedDangerPresent) || !CanObserve(grenade) || !TryGetOperator(owner, out VanguardRaidOperatorRuntimeRecord runtime))
        {
            return;
        }

        VanguardGrenadeObservation observation = GetOrCreateGrenade(grenade!, grenade!.transform.position);
        VanguardGrenadeOperatorObservation pair = GetOrCreatePair(observation, runtime);
        if (pair.NativeRunAwayObserved && pair.NativeShallRunAway == result)
        {
            return;
        }

        pair.NativeRunAwayObserved = true;
        pair.NativeShallRunAway = result;
        if (result)
        {
            pair.NativeShallRunAwayAtUtc = DateTimeOffset.UtcNow;
        }
        if (dangerPoint != default)
        {
            observation.DangerPoint = dangerPoint;
            observation.DangerPointKnown = true;
        }
        RefreshAuthority(pair);
        VanguardClientDiagnosticsLog.Operational(
            VanguardGrenadeHazardPolicy.NativeRunAwayTag,
            () => $"grenade={GrenadeKey(observation)}; operator={Safe(pair.OperatorId)}; botProfile={Safe(pair.BotProfileId)}; shallRunAway={Bool(result)}; capturedDanger={Bool(capturedDangerPresent)}; currentDanger={Bool(currentPresent)}; relevant={Bool(pair.Relevant)}; critical={Bool(pair.Critical)}; dangerDistance={Meters(pair.DangerDistance)}; grenadeDistance={Meters(pair.GrenadeDistance)}; sainDecision={Safe(pair.LastSainDecision)}; movementAuthority={Safe(pair.LastMovementAuthority)}");
    }

    public static void ObserveNativeExecution(
        object? bewareGrenade,
        Grenade? capturedGrenade,
        Vector3 capturedDangerPoint,
        bool capturedDangerPresent)
    {
        BotOwner? owner = VanguardGrenadeRuntimeResolver.ResolveBotOwner(bewareGrenade);
        bool currentPresent = VanguardGrenadeRuntimeResolver.TryReadNativeDangerState(
            bewareGrenade,
            out bool dangerPresent,
            out Grenade? currentGrenade,
            out Vector3 currentDangerPoint) && dangerPresent;
        Grenade? grenade = currentGrenade ?? capturedGrenade;
        Vector3 dangerPoint = currentDangerPoint != default ? currentDangerPoint : capturedDangerPoint;
        if ((!currentPresent && !capturedDangerPresent) || !CanObserve(grenade) || !TryGetOperator(owner, out VanguardRaidOperatorRuntimeRecord runtime))
        {
            return;
        }

        VanguardGrenadeObservation observation = GetOrCreateGrenade(grenade!, grenade!.transform.position);
        VanguardGrenadeOperatorObservation pair = GetOrCreatePair(observation, runtime);
        if (pair.NativeEvasionExecutionObserved)
        {
            return;
        }

        pair.NativeEvasionExecutionObserved = true;
        pair.NativeEvasionExecutionAtUtc = DateTimeOffset.UtcNow;
        if (dangerPoint != default)
        {
            observation.DangerPoint = dangerPoint;
            observation.DangerPointKnown = true;
        }
        RefreshAuthority(pair);
        VanguardClientDiagnosticsLog.Operational(
            VanguardGrenadeHazardPolicy.NativeExecutionTag,
            () => $"grenade={GrenadeKey(observation)}; operator={Safe(pair.OperatorId)}; botProfile={Safe(pair.BotProfileId)}; nativeUpdateByNode=true; capturedDanger={Bool(capturedDangerPresent)}; currentDanger={Bool(currentPresent)}; shallRunAway={Bool(pair.NativeShallRunAway)}; sainDecision={Safe(pair.LastSainDecision)}; brain={Safe(pair.LastBrainLayer)}/{Safe(pair.LastBrainNode)}; movementClass={Safe(pair.LastMovementClassification)}; movementAuthority={Safe(pair.LastMovementAuthority)}; execution={Safe(pair.LastExecutionIntent)}/{Safe(pair.LastExecutionWindow)}");
    }

    public static void ObserveSainDecision(object? decisionManager, object[]? args)
    {
        if (!HasActiveObservations())
        {
            return;
        }

        BotOwner? owner = VanguardGrenadeRuntimeResolver.ResolveBotOwner(decisionManager);
        if (!TryGetOperator(owner, out VanguardRaidOperatorRuntimeRecord runtime))
        {
            return;
        }

        string solo = VanguardGrenadeRuntimeResolver.ReadSainDecision(decisionManager, 0, args);
        string squad = VanguardGrenadeRuntimeResolver.ReadSainDecision(decisionManager, 1, args);
        string self = VanguardGrenadeRuntimeResolver.ReadSainDecision(decisionManager, 2, args);
        foreach ((VanguardGrenadeObservation observation, VanguardGrenadeOperatorObservation pair) in GetActivePairs(runtime.BotProfileId))
        {
            if (!pair.Relevant && !pair.NativeDangerRequestObserved && !pair.SainTrackerCreated)
            {
                continue;
            }
            if (string.Equals(pair.LastSainDecision, solo, StringComparison.Ordinal)
                && string.Equals(pair.LastSainSquadDecision, squad, StringComparison.Ordinal)
                && string.Equals(pair.LastSainSelfDecision, self, StringComparison.Ordinal))
            {
                continue;
            }

            pair.SainDecisionEventObserved = true;
            if (string.Equals(solo, "AvoidGrenade", StringComparison.OrdinalIgnoreCase))
            {
                pair.SainAvoidGrenadeObserved = true;
                pair.SainAvoidGrenadeAtUtc = DateTimeOffset.UtcNow;
            }
            pair.LastSainDecision = solo;
            pair.LastSainSquadDecision = squad;
            pair.LastSainSelfDecision = self;
            RefreshAuthority(pair);
            VanguardClientDiagnosticsLog.Operational(
                VanguardGrenadeHazardPolicy.SainDecisionTag,
                () => $"grenade={GrenadeKey(observation)}; operator={Safe(pair.OperatorId)}; botProfile={Safe(pair.BotProfileId)}; combat={Safe(solo)}; squad={Safe(squad)}; self={Safe(self)}; avoidGrenade={Bool(string.Equals(solo, "AvoidGrenade", StringComparison.OrdinalIgnoreCase))}; relevant={Bool(pair.Relevant)}; critical={Bool(pair.Critical)}; nativeDanger={Bool(pair.NativeDangerPointPresent)}; movementClass={Safe(pair.LastMovementClassification)}; movementAuthority={Safe(pair.LastMovementAuthority)}; execution={Safe(pair.LastExecutionIntent)}/{Safe(pair.LastExecutionWindow)}");
        }
    }

    public static void ObserveDestroyed(Throwable throwable)
    {
        if (!VanguardFikaCompat.IsRaidAuthority || throwable is not Grenade grenade)
        {
            return;
        }

        VanguardGrenadeHazardRegistry.ObserveDestroyed(grenade, DateTimeOffset.UtcNow);
        lock (Sync)
        {
            if (!Observations.TryGetValue(grenade, out VanguardGrenadeObservation? observation))
            {
                return;
            }
            if (observation.TerminalKind == VanguardGrenadeTerminalKind.None)
            {
                observation.TerminalKind = VanguardGrenadeTerminalKind.Destroyed;
                observation.TerminalAtUtc = DateTimeOffset.UtcNow;
            }
            }
    }

    private static void ProcessGrenade(
        VanguardGrenadeObservation observation,
        IReadOnlyList<VanguardRaidOperatorRuntimeRecord> operators,
        DateTimeOffset now)
    {
        if (ReferenceEquals(observation.Grenade, null))
        {
            return;
        }

        if (observation.TerminalKind == VanguardGrenadeTerminalKind.None
            && (now - observation.ObservedAtUtc).TotalSeconds > VanguardGrenadeHazardPolicy.LongLivedGrenadeTimeoutSeconds)
        {
            observation.TerminalKind = VanguardGrenadeTerminalKind.TimedOut;
            observation.TerminalAtUtc = now;
        }

        if (observation.TerminalKind == VanguardGrenadeTerminalKind.None)
        {
            try
            {
                observation.CurrentPosition = observation.Grenade.transform.position;
            }
            catch
            {
                observation.TerminalKind = VanguardGrenadeTerminalKind.Destroyed;
                observation.TerminalAtUtc = now;
            }
        }

        foreach (VanguardRaidOperatorRuntimeRecord runtime in operators)
        {
            if (runtime.BotOwner == null)
            {
                continue;
            }
            VanguardGrenadeOperatorObservation pair = GetOrCreatePair(observation, runtime);
            ProcessPair(observation, pair, runtime, now);
        }

        if (observation.TerminalKind != VanguardGrenadeTerminalKind.None
            && (observation.TerminalKind != VanguardGrenadeTerminalKind.Destroyed
                || observation.TerminalAtUtc == DateTimeOffset.MinValue
                || (now - observation.TerminalAtUtc).TotalSeconds >= VanguardGrenadeHazardPolicy.DestroyedExplosionSettleSeconds))
        {
            EmitTerminalSummaries(observation, now);
        }
    }

    private static void ProcessPair(
        VanguardGrenadeObservation observation,
        VanguardGrenadeOperatorObservation pair,
        VanguardRaidOperatorRuntimeRecord runtime,
        DateTimeOffset now)
    {
        BotOwner? owner = runtime.BotOwner;
        if (owner == null)
        {
            return;
        }

        bool alive;
        Vector3 operatorPosition;
        try
        {
            alive = !owner.IsDead && owner.HealthController?.IsAlive == true;
            operatorPosition = owner.Position;
        }
        catch
        {
            alive = false;
            operatorPosition = pair.LastOperatorPosition;
        }
        pair.Alive = alive;
        RefreshNativeDangerReadback(observation, pair, owner);

        Vector3 dangerPoint = observation.DangerPointKnown ? observation.DangerPoint : observation.CurrentPosition;
        float grenadeDistance = Vector3.Distance(operatorPosition, observation.CurrentPosition);
        float dangerDistance = Vector3.Distance(operatorPosition, dangerPoint);
        pair.GrenadeDistance = grenadeDistance;
        pair.DangerDistance = dangerDistance;
        pair.MinimumGrenadeDistance = Math.Min(pair.MinimumGrenadeDistance, grenadeDistance);
        pair.MinimumDangerDistance = Math.Min(pair.MinimumDangerDistance, dangerDistance);

        float effectiveCritical = Mathf.Clamp(
            pair.NativeRunAwayThreshold,
            VanguardGrenadeHazardPolicy.ImmediatePhysicalProximityMeters,
            Math.Max(VanguardGrenadeHazardPolicy.ImmediatePhysicalProximityMeters, pair.NativeAddDangerThreshold));
        bool relevant = !VanguardGrenadeRuntimeResolver.IsSmoke(observation.Grenade)
            && Math.Min(grenadeDistance, dangerDistance) <= pair.NativeAddDangerThreshold;
        bool critical = relevant && (Math.Min(grenadeDistance, dangerDistance) <= effectiveCritical
            || grenadeDistance <= VanguardGrenadeHazardPolicy.ImmediatePhysicalProximityMeters);

        if (relevant && now >= pair.NextGeometryProbeAtUtc)
        {
            pair.NextGeometryProbeAtUtc = now + TimeSpan.FromSeconds(VanguardGrenadeHazardPolicy.GeometryProbeIntervalSeconds);
            pair.LineOfEffectBlocked = VanguardGrenadeRuntimeResolver.ProbeLineOfEffect(dangerPoint, operatorPosition);
            pair.LineOfEffectKnown = true;
        }

        if (relevant != pair.Relevant || critical != pair.Critical)
        {
            bool entered = relevant && !pair.Relevant;
            pair.Relevant = relevant;
            pair.Critical = critical;
            pair.EverRelevant |= relevant;
            pair.EverCritical |= critical;
            if (entered)
            {
                DateTimeOffset recentReaction = EarliestReactionEvidenceAtUtc(pair);
                pair.RelevantSinceUtc = recentReaction != DateTimeOffset.MinValue
                    && (now - recentReaction).TotalSeconds <= VanguardGrenadeHazardPolicy.PreRelevanceReactionLookbackSeconds
                        ? recentReaction
                        : now;
                pair.RelevantEntryOperatorPosition = operatorPosition;
                pair.RelevantEntryOperatorPositionKnown = true;
                pair.MaximumAwayDisplacementMeters = 0f;
                pair.MaximumOperatorDisplacementMeters = 0f;
            }
            if (relevant)
            {
                pair.LastRelevantAtUtc = now;
            }
            RefreshAuthority(pair);
            string tag = relevant ? VanguardGrenadeHazardPolicy.RelevanceEnteredTag : VanguardGrenadeHazardPolicy.RelevanceExitedTag;
            VanguardClientDiagnosticsLog.Operational(
                tag,
                () => $"grenade={GrenadeKey(observation)}; operator={Safe(pair.OperatorId)}; botProfile={Safe(pair.BotProfileId)}; alive={Bool(alive)}; relevant={Bool(relevant)}; critical={Bool(critical)}; grenadeDistance={Meters(grenadeDistance)}; dangerDistance={Meters(dangerDistance)}; addDanger={pair.NativeAddDangerThreshold:0.00}; runAway={pair.NativeRunAwayThreshold:0.00}; runAwaySqrValue={pair.NativeRunAwaySqrValue:0.00}; lineOfEffectKnown={Bool(pair.LineOfEffectKnown)}; lineOfEffectBlocked={Bool(pair.LineOfEffectBlocked)}; source={Safe(observation.SourceProfileId)}; sourceRelation={observation.SourceRelation}; sainTracker={Bool(pair.SainTrackerCreated)}; sainSpotted={Bool(pair.SainSpotted)}; nativeDanger={Bool(pair.NativeDangerPointPresent)}; sainDecision={Safe(pair.LastSainDecision)}; brain={Safe(pair.LastBrainLayer)}/{Safe(pair.LastBrainNode)}; movementClass={Safe(pair.LastMovementClassification)}; movementAuthority={Safe(pair.LastMovementAuthority)}; execution={Safe(pair.LastExecutionIntent)}/{Safe(pair.LastExecutionWindow)}");
        }
        else if (relevant)
        {
            pair.LastRelevantAtUtc = now;
        }

        if (pair.LastOperatorPositionKnown)
        {
            float movement = Vector3.Distance(pair.LastOperatorPosition, operatorPosition);
            float awayDisplacement = 0f;
            if (relevant && pair.RelevantEntryOperatorPositionKnown)
            {
                float entryDistanceToCurrentDanger = Vector3.Distance(pair.RelevantEntryOperatorPosition, dangerPoint);
                awayDisplacement = dangerDistance - entryDistanceToCurrentDanger;
                pair.MaximumAwayDisplacementMeters = Math.Max(pair.MaximumAwayDisplacementMeters, awayDisplacement);
                float operatorDisplacement = Vector3.Distance(pair.RelevantEntryOperatorPosition, operatorPosition);
                pair.MaximumOperatorDisplacementMeters = Math.Max(pair.MaximumOperatorDisplacementMeters, operatorDisplacement);
            }
            bool increasedDistance = pair.MaximumAwayDisplacementMeters >= VanguardGrenadeHazardPolicy.EvasionProgressMeters;
            if (relevant && movement >= VanguardGrenadeHazardPolicy.MovementTransitionMeters && !pair.MovementObserved)
            {
                pair.MovementObserved = true;
                pair.EvasionProgressObserved |= increasedDistance;
                if (increasedDistance)
                {
                    pair.EvasionProgressAtUtc = now;
                }
                RefreshAuthority(pair);
                VanguardClientDiagnosticsLog.Operational(
                    VanguardGrenadeHazardPolicy.MovementTag,
                    () => $"grenade={GrenadeKey(observation)}; operator={Safe(pair.OperatorId)}; botProfile={Safe(pair.BotProfileId)}; movementMeters={movement:0.00}; speed={pair.LastRealSpeed:0.00}; dangerDistance={Meters(dangerDistance)}; minDangerDistance={Meters(pair.MinimumDangerDistance)}; awayDisplacement={awayDisplacement:0.00}; maxAwayDisplacement={pair.MaximumAwayDisplacementMeters:0.00}; evasionProgress={Bool(increasedDistance)}; nativeRunAway={Bool(pair.NativeShallRunAway)}; nativeExecution={Bool(pair.NativeEvasionExecutionObserved)}; sainDecision={Safe(pair.LastSainDecision)}; brain={Safe(pair.LastBrainLayer)}/{Safe(pair.LastBrainNode)}; movementClass={Safe(pair.LastMovementClassification)}; movementAuthority={Safe(pair.LastMovementAuthority)}; execution={Safe(pair.LastExecutionIntent)}/{Safe(pair.LastExecutionWindow)}");
            }
            else if (relevant && movement > 0.01f && increasedDistance)
            {
                pair.EvasionProgressObserved = true;
                pair.EvasionProgressAtUtc = now;
            }
        }
        pair.LastOperatorPosition = operatorPosition;
        pair.LastOperatorPositionKnown = true;

        if (critical
            && !pair.ReactionMissedLogged
            && pair.RelevantSinceUtc != DateTimeOffset.MinValue
            && (now - pair.RelevantSinceUtc).TotalSeconds >= VanguardGrenadeHazardPolicy.MissingReactionGraceSeconds
            && !HasReactionSince(pair, pair.RelevantSinceUtc))
        {
            pair.ReactionMissedLogged = true;
            RefreshAuthority(pair);
            VanguardClientDiagnosticsLog.Warning(
                VanguardGrenadeHazardPolicy.MissedReactionTag,
                () => $"grenade={GrenadeKey(observation)}; operator={Safe(pair.OperatorId)}; botProfile={Safe(pair.BotProfileId)}; critical=true; graceSeconds={VanguardGrenadeHazardPolicy.MissingReactionGraceSeconds:0.00}; grenadeDistance={Meters(grenadeDistance)}; dangerDistance={Meters(dangerDistance)}; tracker={Bool(pair.SainTrackerCreated)}; spotted={Bool(pair.SainSpotted)}; canReact={Bool(pair.SainCanReactObserved)}; nativeDangerRequest={Bool(pair.NativeDangerRequestObserved)}; nativeDangerWritten={Bool(pair.NativeDangerWritten)}; nativeDangerPresent={Bool(pair.NativeDangerPointPresent)}; nativeShallRunAway={Bool(pair.NativeShallRunAway)}; nativeExecution={Bool(pair.NativeEvasionExecutionObserved)}; sainDecisionEvent={Bool(pair.SainDecisionEventObserved)}; sainDecision={Safe(pair.LastSainDecision)}; movementObserved={Bool(pair.MovementObserved)}; evasionProgress={Bool(pair.EvasionProgressObserved)}; brain={Safe(pair.LastBrainLayer)}/{Safe(pair.LastBrainNode)}; movementClass={Safe(pair.LastMovementClassification)}; movementAuthority={Safe(pair.LastMovementAuthority)}; authorityReason={Safe(pair.LastMovementAuthorityReason)}; execution={Safe(pair.LastExecutionIntent)}/{Safe(pair.LastExecutionWindow)}; diagnosticOnly=true");
        }
    }

    private static DateTimeOffset EarliestReactionEvidenceAtUtc(VanguardGrenadeOperatorObservation pair)
    {
        DateTimeOffset earliest = DateTimeOffset.MaxValue;
        foreach (DateTimeOffset candidate in new[]
        {
            pair.NativeShallRunAwayAtUtc,
            pair.NativeEvasionExecutionAtUtc,
            pair.SainAvoidGrenadeAtUtc,
            pair.EvasionProgressAtUtc,
        })
        {
            if (candidate != DateTimeOffset.MinValue && candidate < earliest)
            {
                earliest = candidate;
            }
        }
        return earliest == DateTimeOffset.MaxValue ? DateTimeOffset.MinValue : earliest;
    }

    private static bool HasReactionSince(VanguardGrenadeOperatorObservation pair, DateTimeOffset sinceUtc)
    {
        if (sinceUtc == DateTimeOffset.MinValue)
        {
            return false;
        }
        return pair.NativeShallRunAwayAtUtc >= sinceUtc
            || pair.NativeEvasionExecutionAtUtc >= sinceUtc
            || pair.SainAvoidGrenadeAtUtc >= sinceUtc
            || pair.EvasionProgressAtUtc >= sinceUtc;
    }

    private static void RefreshNativeDangerReadback(
        VanguardGrenadeObservation observation,
        VanguardGrenadeOperatorObservation pair,
        BotOwner owner)
    {
        if (!pair.NativeDangerRequestObserved || pair.NativeDangerPointPresent)
        {
            return;
        }

        object? bewareGrenade = VanguardGrenadeRuntimeResolver.GetMember(owner, "BewareGrenade");
        bool present = VanguardGrenadeRuntimeResolver.TryReadNativeDangerState(
            bewareGrenade,
            out bool dangerPresent,
            out Grenade? nativeGrenade,
            out Vector3 nativeDanger) && dangerPresent;
        if (!present || nativeGrenade == null || !ReferenceEquals(nativeGrenade, observation.Grenade))
        {
            return;
        }

        pair.NativeDangerWritten = true;
        pair.NativeDangerPointPresent = true;
        if (nativeDanger != default)
        {
            observation.DangerPoint = nativeDanger;
            observation.DangerPointKnown = true;
        }
        RefreshAuthority(pair);
        VanguardClientDiagnosticsLog.Operational(
            VanguardGrenadeHazardPolicy.NativeDangerWrittenTag,
            () => $"grenade={GrenadeKey(observation)}; operator={Safe(pair.OperatorId)}; botProfile={Safe(pair.BotProfileId)}; requestObserved=true; writeConfirmed=true; delayedWriteReadback=true; nativeDanger={VectorText(nativeDanger)}; sainDecision={Safe(pair.LastSainDecision)}; brain={Safe(pair.LastBrainLayer)}/{Safe(pair.LastBrainNode)}; movementClass={Safe(pair.LastMovementClassification)}; movementAuthority={Safe(pair.LastMovementAuthority)}; execution={Safe(pair.LastExecutionIntent)}/{Safe(pair.LastExecutionWindow)}");
    }

    private static void EmitTerminalSummaries(VanguardGrenadeObservation observation, DateTimeOffset now)
    {
        foreach (VanguardGrenadeOperatorObservation pair in observation.Operators.Values)
        {
            if (pair.TerminalLogged || (!pair.EverRelevant && !pair.SainReactionObserved && !pair.NativeDangerRequestObserved))
            {
                continue;
            }

            pair.TerminalLogged = true;
            RefreshAuthority(pair);
            bool decisionReaction = pair.NativeShallRunAwayAtUtc != DateTimeOffset.MinValue
                || pair.NativeEvasionExecutionAtUtc != DateTimeOffset.MinValue
                || pair.SainAvoidGrenadeObserved;
            bool distanceGainObserved = pair.EvasionProgressAtUtc != DateTimeOffset.MinValue;
            bool ownMovementObserved = pair.MaximumOperatorDisplacementMeters >= VanguardGrenadeHazardPolicy.EvasionProgressMeters;
            bool effectiveEvasion = distanceGainObserved && ownMovementObserved;
            bool reacted = decisionReaction || effectiveEvasion;
            if (pair.EverCritical && !reacted && !pair.ReactionMissedLogged)
            {
                pair.ReactionMissedLogged = true;
                VanguardClientDiagnosticsLog.Warning(
                    VanguardGrenadeHazardPolicy.MissedReactionTag,
                    () => $"grenade={GrenadeKey(observation)}; operator={Safe(pair.OperatorId)}; botProfile={Safe(pair.BotProfileId)}; terminal={observation.TerminalKind}; criticalEver=true; reacted=false; tracker={Bool(pair.SainTrackerCreated)}; spotted={Bool(pair.SainSpotted)}; nativeDangerRequest={Bool(pair.NativeDangerRequestObserved)}; nativeDangerWritten={Bool(pair.NativeDangerWritten)}; nativeDangerPresent={Bool(pair.NativeDangerPointPresent)}; nativeShallRunAway={Bool(pair.NativeShallRunAway)}; nativeExecution={Bool(pair.NativeEvasionExecutionObserved)}; sainDecisionEvent={Bool(pair.SainDecisionEventObserved)}; sainDecision={Safe(pair.LastSainDecision)}; movementObserved={Bool(pair.MovementObserved)}; evasionProgress={Bool(pair.EvasionProgressObserved)}; diagnosticOnly=true");
            }

            if (pair.EverCritical && decisionReaction && !effectiveEvasion && !pair.EvasionNoProgressLogged)
            {
                pair.EvasionNoProgressLogged = true;
                VanguardClientDiagnosticsLog.Warning(
                    VanguardGrenadeHazardPolicy.EvasionNoProgressTag,
                    () => $"grenade={GrenadeKey(observation)}; operator={Safe(pair.OperatorId)}; botProfile={Safe(pair.BotProfileId)}; terminal={observation.TerminalKind}; decisionReaction=true; effectiveEvasion=false; nativeShallRunAway={Bool(pair.NativeShallRunAway)}; nativeExecution={Bool(pair.NativeEvasionExecutionObserved)}; avoidGrenadeObserved={Bool(pair.SainAvoidGrenadeObserved)}; movementObserved={Bool(pair.MovementObserved)}; minDangerDistance={Meters(pair.MinimumDangerDistance)}; maxAwayDisplacement={pair.MaximumAwayDisplacementMeters:0.00}; lineOfEffectKnown={Bool(pair.LineOfEffectKnown)}; lineOfEffectBlocked={Bool(pair.LineOfEffectBlocked)}; movementAuthority={Safe(pair.LastMovementAuthority)}; execution={Safe(pair.LastExecutionIntent)}/{Safe(pair.LastExecutionWindow)}; diagnosticOnly=true");
            }

            VanguardClientDiagnosticsLog.Operational(
                VanguardGrenadeHazardPolicy.TerminalTag,
                () => $"grenade={GrenadeKey(observation)}; terminal={observation.TerminalKind}; explosionPos={VectorText(observation.ExplosionPosition)}; operator={Safe(pair.OperatorId)}; botProfile={Safe(pair.BotProfileId)}; alive={Bool(pair.Alive)}; source={Safe(observation.SourceProfileId)}; sourceName={Safe(observation.SourceName)}; sourceRelation={observation.SourceRelation}; collisions={observation.CollisionCount}; lastCollisionHearingRange={observation.LastCollisionMaxRange:0.00}; everRelevant={Bool(pair.EverRelevant)}; everCritical={Bool(pair.EverCritical)}; minGrenadeDistance={Meters(pair.MinimumGrenadeDistance)}; minDangerDistance={Meters(pair.MinimumDangerDistance)}; maxAwayDisplacement={pair.MaximumAwayDisplacementMeters:0.00}; maxOperatorDisplacement={pair.MaximumOperatorDisplacementMeters:0.00}; distanceGainObserved={Bool(distanceGainObserved)}; ownMovementObserved={Bool(ownMovementObserved)}; trackerCreated={Bool(pair.SainTrackerCreated)}; nativeFallback={Bool(pair.SainTrackerFallbackToNative)}; returnedWithoutTrackerOrNative={Bool(pair.SainReactionReturnedWithoutTrackerOrNative)}; spotted={Bool(pair.SainSpotted)}; canReact={Bool(pair.SainCanReactObserved)}; nativeDangerRequest={Bool(pair.NativeDangerRequestObserved)}; nativeDangerWritten={Bool(pair.NativeDangerWritten)}; nativeDangerPresent={Bool(pair.NativeDangerPointPresent)}; nativeShallRunAway={Bool(pair.NativeShallRunAway)}; nativeExecution={Bool(pair.NativeEvasionExecutionObserved)}; sainDecisionEvent={Bool(pair.SainDecisionEventObserved)}; sainDecision={Safe(pair.LastSainDecision)}; movementObserved={Bool(pair.MovementObserved)}; evasionProgress={Bool(pair.EvasionProgressObserved)}; decisionReaction={Bool(decisionReaction)}; effectiveEvasion={Bool(effectiveEvasion)}; reacted={Bool(reacted)}; reactionMissed={Bool(pair.ReactionMissedLogged)}; evasionNoProgress={Bool(pair.EvasionNoProgressLogged)}; brain={Safe(pair.LastBrainLayer)}/{Safe(pair.LastBrainNode)}; movementClass={Safe(pair.LastMovementClassification)}; movementAuthority={Safe(pair.LastMovementAuthority)}; authorityReason={Safe(pair.LastMovementAuthorityReason)}; execution={Safe(pair.LastExecutionIntent)}/{Safe(pair.LastExecutionWindow)}; diagnosticOnly=true");
        }
    }

    private static void CleanupTerminal(DateTimeOffset now)
    {
        List<VanguardGrenadeObservation> removed = new();
        lock (Sync)
        {
            foreach (KeyValuePair<Grenade, VanguardGrenadeObservation> entry in Observations.ToArray())
            {
                VanguardGrenadeObservation observation = entry.Value;
                if (observation.TerminalKind == VanguardGrenadeTerminalKind.None
                    || observation.TerminalAtUtc == DateTimeOffset.MinValue
                    || (now - observation.TerminalAtUtc).TotalSeconds < VanguardGrenadeHazardPolicy.TerminalRetentionSeconds)
                {
                    continue;
                }
                Observations.Remove(entry.Key);
                removed.Add(observation);
            }
        }

        foreach (VanguardGrenadeObservation observation in removed)
        {
            TryUnsubscribe(observation);
        }
    }

    private static VanguardGrenadeObservation GetOrCreateGrenade(Grenade grenade, Vector3 initialPosition)
    {
        lock (Sync)
        {
            if (Observations.TryGetValue(grenade, out VanguardGrenadeObservation? existing))
            {
                return existing;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            VanguardGrenadeRuntimeResolver.ResolveSource(grenade.ProfileId, out string sourceProfileId, out string sourceName, out VanguardGrenadeSourceRelation relation);
            var created = new VanguardGrenadeObservation
            {
                Grenade = grenade,
                GrenadeId = grenade.Id,
                GrenadeType = grenade.GetType().Name,
                SourceProfileId = sourceProfileId,
                SourceName = sourceName,
                SourceRelation = relation,
                ThrowPosition = initialPosition,
                CurrentPosition = initialPosition,
                ObservedAtUtc = now,
            };
            Observations[grenade] = created;
            SubscribeDestroy(created);
            return created;
        }
    }

    private static VanguardGrenadeOperatorObservation GetOrCreatePair(
        VanguardGrenadeObservation observation,
        VanguardRaidOperatorRuntimeRecord runtime)
    {
        lock (Sync)
        {
            if (observation.Operators.TryGetValue(runtime.BotProfileId, out VanguardGrenadeOperatorObservation? existing))
            {
                return existing;
            }

            VanguardGrenadeRuntimeResolver.TryReadGrenadeThresholds(runtime.BotOwner, out float addDanger, out float runAway, out float runAwaySqr);
            var created = new VanguardGrenadeOperatorObservation
            {
                OperatorId = Safe(runtime.OperatorId),
                BotProfileId = Safe(runtime.BotProfileId),
                OwnerProfileId = Safe(runtime.OwnerProfileId),
                Nickname = Safe(runtime.BotNickname),
                NativeAddDangerThreshold = Math.Max(1f, addDanger),
                NativeRunAwayThreshold = Math.Max(1f, runAway),
                NativeRunAwaySqrValue = Math.Max(1f, runAwaySqr),
            };
            RefreshAuthority(created);
            observation.Operators[runtime.BotProfileId] = created;
            return created;
        }
    }

    private static bool HasActiveObservations()
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            return false;
        }
        lock (Sync)
        {
            foreach (VanguardGrenadeObservation observation in Observations.Values)
            {
                if (observation.TerminalKind == VanguardGrenadeTerminalKind.None)
                {
                    return true;
                }
            }
            return false;
        }
    }

    private static IReadOnlyList<(VanguardGrenadeObservation observation, VanguardGrenadeOperatorObservation pair)> GetActivePairs(string botProfileId)
    {
        lock (Sync)
        {
            return Observations.Values
                .Where(observation => observation.TerminalKind == VanguardGrenadeTerminalKind.None
                    && observation.Operators.TryGetValue(botProfileId, out _))
                .Select(observation => (observation, observation.Operators[botProfileId]))
                .ToArray();
        }
    }

    private static void RefreshAuthority(VanguardGrenadeOperatorObservation pair)
    {
        if (VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(pair.BotProfileId, out OperatorDecisionSnapshot snapshot)
            && snapshot != OperatorDecisionSnapshot.Empty)
        {
            if (!pair.SainDecisionEventObserved)
            {
                pair.LastSainDecision = Safe(snapshot.Sain.CombatDecision);
                pair.LastSainSquadDecision = Safe(snapshot.Sain.SquadDecision);
                pair.LastSainSelfDecision = Safe(snapshot.Sain.SelfDecision);
            }
            pair.LastBrainLayer = Safe(snapshot.Brain.ActiveLayer);
            pair.LastBrainNode = Safe(snapshot.Brain.Node);
            pair.LastMovementAuthority = Safe(snapshot.MovementAuthority.CurrentAuthority);
            pair.LastMovementAuthorityReason = Safe(snapshot.MovementAuthority.CurrentAuthorityReason);
            pair.LastMovementClassification = Safe(snapshot.Movement.Classification);
            pair.LastRealSpeed = snapshot.RealSpeed;
        }

        if (VanguardExecutionLeaseCoordinator.TryGetActiveLease(pair.BotProfileId, out VanguardExecutionLeaseState lease))
        {
            pair.LastExecutionIntent = Safe(lease.IntentKey);
            pair.LastExecutionWindow = Safe(lease.WindowKind);
        }
        else
        {
            pair.LastExecutionIntent = "none";
            pair.LastExecutionWindow = "none";
        }
    }

    private static bool TryGetOperator(BotOwner? owner, out VanguardRaidOperatorRuntimeRecord runtime)
    {
        runtime = null!;
        return owner != null
            && VanguardFikaCompat.IsRaidAuthority
            && VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(owner.ProfileId, out runtime);
    }

    private static bool CanObserve(Grenade? grenade)
    {
        return VanguardFikaCompat.IsRaidAuthority && grenade != null;
    }

    private static void ResolveSource(VanguardGrenadeObservation observation, string? sourceProfileId)
    {
        VanguardGrenadeRuntimeResolver.ResolveSource(sourceProfileId, out string normalized, out string name, out VanguardGrenadeSourceRelation relation);
        if (string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int currentConfidence = SourceConfidence(observation.SourceRelation);
        int incomingConfidence = SourceConfidence(relation);
        bool currentUnknown = string.Equals(observation.SourceProfileId, "none", StringComparison.OrdinalIgnoreCase);
        bool sameSource = string.Equals(observation.SourceProfileId, normalized, StringComparison.OrdinalIgnoreCase);
        // Preserve the first known physical source. A later event may only enrich the same profile;
        // it may not replace source identity after terminal correlation.
        if (currentUnknown || sameSource && incomingConfidence >= currentConfidence)
        {
            observation.SourceProfileId = normalized;
            observation.SourceName = name;
            observation.SourceRelation = relation;
        }
    }

    private static int SourceConfidence(VanguardGrenadeSourceRelation relation)
    {
        return relation switch
        {
            VanguardGrenadeSourceRelation.Operator => 40,
            VanguardGrenadeSourceRelation.PlayerOwner => 30,
            VanguardGrenadeSourceRelation.PlayerClient => 20,
            VanguardGrenadeSourceRelation.HostileOrNeutral => 10,
            _ => 0,
        };
    }

    private static void SubscribeDestroy(VanguardGrenadeObservation observation)
    {
        if (observation.DestroySubscribed || observation.Grenade == null)
        {
            return;
        }
        try
        {
            observation.Grenade.DestroyEvent += ObserveDestroyed;
            observation.DestroySubscribed = true;
        }
        catch
        {
            observation.DestroySubscribed = false;
        }
    }

    private static void TryUnsubscribe(VanguardGrenadeObservation observation)
    {
        if (!observation.DestroySubscribed || observation.Grenade == null)
        {
            return;
        }
        try
        {
            observation.Grenade.DestroyEvent -= ObserveDestroyed;
        }
        catch
        {
            // Grenade destruction can invalidate the Unity object before raid cleanup.
        }
        observation.DestroySubscribed = false;
    }

    private static VanguardGrenadeObservation? FindObservationForExplosion(int throwableId, string? sourceProfileId, Vector3 position)
    {
        string normalizedSource = Safe(sourceProfileId);
        lock (Sync)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            VanguardGrenadeObservation? byId = Observations.Values
                .Where(observation => IsExplosionMatchCandidate(observation, now)
                    && throwableId >= 0
                    && observation.GrenadeId == throwableId)
                .OrderBy(observation => Vector3.Distance(observation.CurrentPosition, position))
                .FirstOrDefault();
            if (byId != null)
            {
                return byId;
            }

            return Observations.Values
                .Where(observation => IsExplosionMatchCandidate(observation, now)
                    && (normalizedSource == "none" || string.Equals(observation.SourceProfileId, normalizedSource, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(observation => Vector3.Distance(observation.CurrentPosition, position))
                .FirstOrDefault(observation => Vector3.Distance(observation.CurrentPosition, position) <= 12f);
        }
    }

    private static bool IsExplosionMatchCandidate(VanguardGrenadeObservation observation, DateTimeOffset now)
    {
        return observation.TerminalKind == VanguardGrenadeTerminalKind.None
            || (observation.TerminalKind == VanguardGrenadeTerminalKind.Destroyed
                && observation.TerminalAtUtc != DateTimeOffset.MinValue
                && (now - observation.TerminalAtUtc).TotalSeconds <= VanguardGrenadeHazardPolicy.DestroyedExplosionSettleSeconds);
    }

    private static bool ContainsGrenadeKey(object? dictionary, Grenade grenade)
    {
        if (dictionary is System.Collections.IDictionary nonGeneric)
        {
            return nonGeneric.Contains(grenade);
        }

        object? keys = VanguardGrenadeRuntimeResolver.GetMember(dictionary, "Keys");
        if (keys is System.Collections.IEnumerable enumerable)
        {
            foreach (object? key in enumerable)
            {
                if (ReferenceEquals(key, grenade))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static string GrenadeKey(VanguardGrenadeObservation observation) => $"{observation.GrenadeId}:{RuntimeHelpers.GetHashCode(observation.Grenade):X8}";
    private static string Safe(string? value) => VanguardGrenadeRuntimeResolver.Safe(value);
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Meters(float value) => float.IsInfinity(value) || float.IsNaN(value) ? "unknown" : value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string VectorText(Vector3 value) => $"{value.x:0.00},{value.y:0.00},{value.z:0.00}";

    private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        public static ReferenceComparer<T> Instance { get; } = new();
        bool IEqualityComparer<T>.Equals(T? x, T? y) => ReferenceEquals(x, y);
        int IEqualityComparer<T>.GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
#endif
