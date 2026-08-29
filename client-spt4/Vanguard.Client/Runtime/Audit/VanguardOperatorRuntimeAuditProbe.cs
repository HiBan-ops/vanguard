#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;
using Vanguard.Client.Raid.Runtime;

// Responsibility: Builds the consolidated runtime audit snapshot used to diagnose Operator binding, authority, movement, combat and subsystem convergence.
// Flow: On diagnostic cadence it gathers read-only facts from runtime stores/readers, normalizes them into one Operator-centric snapshot and emits only through the configured diagnostics presentation policy.
// Authority boundary: The probe observes existing subsystem state; it cannot repair or influence the behavior it reports.
// Invariant: Audit collection remains bounded/rate-controlled and missing evidence is labeled explicitly rather than treated as successful convergence.
namespace Vanguard.Client.Runtime.Audit;

internal static class VanguardOperatorRuntimeAuditProbe
{
    private sealed class LastState
    {
        public Vector3 Position;
        public DateTimeOffset CapturedAtUtc = DateTimeOffset.MinValue;
        public string Signature = string.Empty;
        public string MeaningfulSignature = string.Empty;
        public DateTimeOffset LastTransitionAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastSummaryAtUtc = DateTimeOffset.MinValue;
    }

    private static readonly Dictionary<string, LastState> LastByBotProfileId = new(StringComparer.OrdinalIgnoreCase);

    public static void ResetForRaidLifecycle(string reason)
    {
        LastByBotProfileId.Clear();
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorRuntimeAuditStatusTag,
            $"operator runtime audit probe cache reset reason={reason}");
    }

    public static VanguardOperatorRuntimeAuditSnapshot Capture(VanguardRaidOperatorRuntimeRecord record)
    {
        var now = DateTimeOffset.UtcNow;
        var botOwner = record.BotOwner;
        Vector3 position = ResolvePosition(botOwner);
        float realSpeed = ComputeRealSpeed(record.BotProfileId, position, now);

        return new VanguardOperatorRuntimeAuditSnapshot
        {
            OperatorId = record.OperatorId,
            OwnerProfileId = record.OwnerProfileId,
            BotProfileId = record.BotProfileId,
            Nickname = record.BotNickname,
            Alive = ResolveAlive(botOwner),
            Position = position,
            RealSpeed = realSpeed,
            Movement = VanguardOperatorRuntimeAuditOptions.GetMovementProbeEnabled() ? ProbeMovement(botOwner, realSpeed) : "disabled",
            Brain = VanguardOperatorRuntimeAuditOptions.GetBrainProbeEnabled() ? ProbeBrain(botOwner) : "disabled",
            Sain = VanguardOperatorRuntimeAuditOptions.GetSainProbeEnabled() ? ProbeSain(botOwner) : "disabled",
            LootingBots = VanguardOperatorRuntimeAuditOptions.GetLootingBotsProbeEnabled() ? ProbeLootingBots(botOwner) : "disabled",
            Orbit = VanguardOperatorRuntimeAuditOptions.GetOrbitProbeEnabled() ? ProbeOrbit(botOwner, record.BotProfileId) : "disabled",
            CapturedAtUtc = now
        };
    }

    public static bool ShouldLogTransition(VanguardOperatorRuntimeAuditSnapshot snapshot)
    {
        var state = GetOrCreateState(snapshot.BotProfileId);
        bool verbose = VanguardOperatorRuntimeAuditOptions.GetVerboseTransitionLogEnabled();
        string signature = verbose ? snapshot.Signature : MeaningfulSignature(snapshot);
        string previous = verbose ? state.Signature : state.MeaningfulSignature;
        if (string.Equals(previous, signature, StringComparison.Ordinal))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (!verbose && state.LastTransitionAtUtc != DateTimeOffset.MinValue)
        {
            var minInterval = TimeSpan.FromSeconds(VanguardOperatorRuntimeAuditOptions.GetTransitionLogMinIntervalSeconds());
            if (now - state.LastTransitionAtUtc < minInterval)
            {
                state.MeaningfulSignature = signature;
                return false;
            }
        }

        if (verbose)
        {
            state.Signature = signature;
        }
        else
        {
            state.MeaningfulSignature = signature;
        }

        state.LastTransitionAtUtc = now;
        return true;
    }

    public static bool ShouldLogSummary(VanguardOperatorRuntimeAuditSnapshot snapshot)
    {
        var state = GetOrCreateState(snapshot.BotProfileId);
        var now = DateTimeOffset.UtcNow;
        if (now - state.LastSummaryAtUtc < TimeSpan.FromSeconds(VanguardOperatorRuntimeAuditOptions.GetSummaryIntervalSeconds()))
        {
            return false;
        }

        state.LastSummaryAtUtc = now;
        return true;
    }

    public static string Format(VanguardOperatorRuntimeAuditSnapshot snapshot, string kind)
    {
        return $"{kind} operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; owner={snapshot.OwnerProfileId}; nick={snapshot.Nickname}; alive={snapshot.Alive}; pos={FormatVector(snapshot.Position)}; speed={snapshot.RealSpeed:0.00}; movement={snapshot.Movement}; brain={snapshot.Brain}; sain={snapshot.Sain}; looting={snapshot.LootingBots}; orbit={snapshot.Orbit}; verbose={VanguardOperatorRuntimeAuditOptions.GetVerboseTransitionLogEnabled()}";
    }

    private static string MeaningfulSignature(VanguardOperatorRuntimeAuditSnapshot snapshot)
    {
        return string.Join("|",
            snapshot.Alive ? "alive" : "dead",
            Contains(snapshot.Brain, "goalEnemy=none") ? "brain_no_goal" : "brain_goal",
            Contains(snapshot.Sain, "combat=true") || Contains(snapshot.Sain, "hasEnemy=true") ? "sain_enemy" : "sain_no_enemy",
            Contains(snapshot.LootingBots, "taskRunning=true") || Contains(snapshot.LootingBots, "botLooting=true") ? "loot_active" : "loot_other",
            Contains(snapshot.Orbit, "active=true") ? "orbit_active" : "orbit_other");
    }

    private static bool Contains(string value, string token)
    {
        return !string.IsNullOrEmpty(value)
            && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static LastState GetOrCreateState(string botProfileId)
    {
        if (!LastByBotProfileId.TryGetValue(botProfileId, out var state))
        {
            state = new LastState();
            LastByBotProfileId[botProfileId] = state;
        }

        return state;
    }

    private static float ComputeRealSpeed(string botProfileId, Vector3 position, DateTimeOffset now)
    {
        var state = GetOrCreateState(botProfileId);
        float speed = 0f;
        if (state.CapturedAtUtc != DateTimeOffset.MinValue)
        {
            double deltaSeconds = Math.Max(0.001, (now - state.CapturedAtUtc).TotalSeconds);
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
            // Passive audit only: missing transform data must never affect runtime.
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

    private static string ProbeMovement(BotOwner? botOwner, float realSpeed)
    {
        if (botOwner == null)
        {
            return "botOwner=none";
        }

        object? mover = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Mover", "BotMover");
        object? player = VanguardOperatorRuntimeAuditReflection.GetMember(botOwner, "GetPlayer", "Player");
        object? movementContext = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player, "MovementContext");
        object? patrol = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "PatrollingData", "PatrolData");
        object? goToPoint = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "GoToSomePointData");

        string moverState = VanguardOperatorRuntimeAuditReflection.TypeName(mover);
        string targetSpeed = VanguardOperatorRuntimeAuditReflection.FloatText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "DestMoveSpeed", "TargetMoveSpeed", "MoveSpeed", "Speed"));
        string sprint = VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "Sprinting", "Sprint", "IsSprint", "IsSprinting"));
        string playerSprint = VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player, "IsSprintEnabled"));
        string pose = VanguardOperatorRuntimeAuditReflection.FloatText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "TargetPose", "Pose", "PoseLevel"));
        string patrolPaused = VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(patrol, "Paused", "IsPaused"));
        string hasPath = VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "HasPathAndNoComplete", "HasPathAndNotComplete"));
        string targetPoint = VanguardOperatorRuntimeAuditReflection.VectorText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "TargetPoint"));
        string realDest = VanguardOperatorRuntimeAuditReflection.VectorText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "RealDestPoint", "CurrentTargetPoint"));
        string corner = VanguardOperatorRuntimeAuditReflection.VectorText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "CurrentCornerPoint"));
        string dist = VanguardOperatorRuntimeAuditReflection.FloatText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "DistDestination", "SDistDestination"));
        string lastPathSet = VanguardOperatorRuntimeAuditReflection.FloatText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "LastPathSetTime"));
        string goToTarget = VanguardOperatorRuntimeAuditReflection.VectorText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goToPoint, "Point"));
        string goToDist = VanguardOperatorRuntimeAuditReflection.FloatText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goToPoint, "DistToPoint"));
        string playerState = VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(movementContext, "CurrentStateName", "CurrentState"));

        return VanguardOperatorRuntimeAuditReflection.JoinParts(
            $"mover={moverState}",
            $"speed={realSpeed:0.00}",
            $"targetSpeed={targetSpeed}",
            $"sprint={sprint}",
            $"playerSprint={playerSprint}",
            $"pose={pose}",
            $"patrolPaused={patrolPaused}",
            $"hasPath={hasPath}",
            $"dist={dist}",
            $"target={targetPoint}",
            $"dest={realDest}",
            $"corner={corner}",
            $"goTo={goToTarget}",
            $"goToDist={goToDist}",
            $"lastPathSet={lastPathSet}",
            $"playerState={playerState}");
    }

    private static string ProbeBrain(BotOwner? botOwner)
    {
        if (botOwner == null)
        {
            return "botOwner=none";
        }

        object? brain = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Brain", "BotBrain", "BotBaseBrain");
        object? baseBrain = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(brain, "BaseBrain") ?? brain;
        object? agent = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(brain, "Agent");
        object? activeLayer = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(baseBrain, "CurLayerInfo", "GClass35_0", "Gclass35_0")
            ?? VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(agent, "Gclass35_0", "GClass35_0");
        object? lastResult = VanguardOperatorRuntimeAuditReflection.InvokeNoArg(agent, "LastResult");
        object? memory = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Memory");
        object? goalEnemy = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(memory, "GoalEnemy");
        object? customLayer = VanguardOperatorRuntimeAuditReflection.InvokeNoArg(activeLayer, "CustomLayer");
        object? customAction = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(customLayer, "CurrentAction");

        string layerName = VanguardOperatorRuntimeAuditReflection.FirstNonEmpty(
            VanguardOperatorRuntimeAuditReflection.LayerName(activeLayer),
            VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.InvokeNoArg(brain, "ActiveLayerName")),
            VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(agent, "UsingLayer")));
        string node = VanguardOperatorRuntimeAuditReflection.FirstNonEmpty(
            VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lastResult, "Action")),
            VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.InvokeNoArg(brain, "GetLastNode")),
            VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.InvokeNoArg(agent, "GetActiveNodeName")));
        string reason = VanguardOperatorRuntimeAuditReflection.FirstNonEmpty(
            VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lastResult, "Reason")),
            VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.InvokeNoArg(brain, "GetActiveNodeReason")),
            VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.InvokeNoArg(agent, "GetActiveNodeReason")));
        string custom = VanguardOperatorRuntimeAuditReflection.TypeName(customLayer);
        string customActionType = VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(customAction, "Type"));
        string customReason = VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(customAction, "Reason"));
        string enemy = FormatVanillaGoalEnemy(goalEnemy);

        return VanguardOperatorRuntimeAuditReflection.JoinParts(
            $"brain={VanguardOperatorRuntimeAuditReflection.TypeName(brain)}",
            $"layer={layerName}",
            $"node={node}",
            $"reason={reason}",
            $"custom={custom}",
            $"customAction={customActionType}",
            $"customReason={customReason}",
            $"goalEnemy={enemy}");
    }

    private static string ProbeSain(BotOwner? botOwner)
    {
        if (botOwner == null)
        {
            return "botOwner=none";
        }

        object? sain = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "SAIN.Components.BotComponent");
        bool sainTypeExists = VanguardOperatorRuntimeAuditReflection.TypeExists("SAIN.Components.BotComponent");
        if (sain == null)
        {
            if (!sainTypeExists)
            {
                VanguardOperatorRuntimeAuditReflection.LogMissingOnce("sain_type", "source=SAIN reason=bot_component_type_not_loaded");
            }

            return $"component=none,typeLoaded={sainTypeExists.ToString().ToLowerInvariant()}";
        }

        object? decision = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "Decision");
        object? enemy = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "GoalEnemy");
        object? currentAction = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "CurrentAction");

        string decisionText = VanguardOperatorRuntimeAuditReflection.JoinParts(
            $"has={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "HasDecision"))}",
            $"combat={VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "CurrentCombatDecision"))}",
            $"squad={VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "CurrentSquadDecision"))}",
            $"self={VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "CurrentSelfDecision"))}",
            $"since={VanguardOperatorRuntimeAuditReflection.FloatText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "TimeSinceChangeDecision"))}",
            $"runningCover={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "RunningToCover"))}",
            $"searching={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "IsSearching"))}");

        return VanguardOperatorRuntimeAuditReflection.JoinParts(
            $"component={VanguardOperatorRuntimeAuditReflection.TypeName(sain)}",
            $"active={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "BotActive"))}",
            $"standby={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "BotInStandBy"))}",
            $"layers={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "SAINLayersActive"))}",
            $"activeLayer={VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "ActiveLayer"))}",
            $"combat={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "IsInCombat"))}",
            $"hasEnemy={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "HasEnemy"))}",
            $"action={VanguardOperatorRuntimeAuditReflection.TypeName(currentAction)}",
            $"decision[{decisionText}]",
            $"enemy[{FormatSainEnemy(enemy)}]");
    }

    private static string ProbeLootingBots(BotOwner? botOwner)
    {
        if (botOwner == null)
        {
            return "botOwner=none";
        }

        object? lootingBrain = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "LootingBots.Components.LootingBrain");
        object? lootFinder = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "LootingBots.Components.LootFinder");
        bool brainTypeLoaded = VanguardOperatorRuntimeAuditReflection.TypeExists("LootingBots.Components.LootingBrain");
        if (lootingBrain == null && lootFinder == null)
        {
            return $"active=false,typeLoaded={brainTypeLoaded.ToString().ToLowerInvariant()}";
        }

        object? stats = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "Stats");
        object? activeLoot = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "ActiveLoot");
        return VanguardOperatorRuntimeAuditReflection.JoinParts(
            $"component={VanguardOperatorRuntimeAuditReflection.TypeName(lootingBrain)}",
            $"finder={VanguardOperatorRuntimeAuditReflection.TypeName(lootFinder)}",
            $"brainEnabled={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "IsBrainEnabled"))}",
            $"botLooting={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "IsBotLooting"))}",
            $"task={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "LootTaskRunning"))}",
            $"activeLootable={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "HasActiveLootable"))}",
            $"lootType={VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "ActiveLootType"))}",
            $"loot={VanguardOperatorRuntimeAuditReflection.TypeName(activeLoot)}",
            $"dist={VanguardOperatorRuntimeAuditReflection.FloatText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "DistanceToLoot"))}",
            $"freeSpace={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "HasFreeSpace"))}",
            $"grid={VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(stats, "AvailableGridSpaces"))}",
            $"scanScheduled={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootFinder, "IsScheduledScan"))}",
            $"scanRunning={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootFinder, "IsScanRunning"))}");
    }

    private static string ProbeOrbit(BotOwner? botOwner, string botProfileId)
    {
        if (botOwner == null)
        {
            return "botOwner=none";
        }

        Type? telemetryType = VanguardOperatorRuntimeAuditReflection.FindType("Orbit.Api.OrbitTelemetry");
        bool telemetryLoaded = telemetryType != null;
        if (!telemetryLoaded)
        {
            return "active=false,telemetry=false";
        }

        string profileId = VanguardOperatorRuntimeAuditReflection.FirstNonEmpty(
            VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "ProfileId")),
            botProfileId);
        bool available = VanguardOperatorRuntimeAuditReflection.GetStaticMember(telemetryType, "IsAvailable") is bool trueValue && trueValue;
        object? objective = available ? VanguardOperatorRuntimeAuditReflection.InvokeStatic(telemetryType, "GetBotObjective", profileId) : null;
        if (objective == null)
        {
            return $"active=false,telemetry=true,available={available.ToString().ToLowerInvariant()}";
        }

        string position = $"{VanguardOperatorRuntimeAuditReflection.FloatText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objective, "ObjectiveX"))},{VanguardOperatorRuntimeAuditReflection.FloatText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objective, "ObjectiveY"))},{VanguardOperatorRuntimeAuditReflection.FloatText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objective, "ObjectiveZ"))}";
        return VanguardOperatorRuntimeAuditReflection.JoinParts(
            "active=true",
            "telemetry=true",
            $"available={available.ToString().ToLowerInvariant()}",
            $"status={VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objective, "Status"))}",
            $"category={VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objective, "Category"))}",
            $"leader={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objective, "IsLeader"))}",
            $"objective={position}",
            $"extract={VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objective, "ExtractReason"))}");
    }

    private static string FormatSainEnemy(object? enemy)
    {
        if (enemy == null)
        {
            return "none";
        }

        object? status = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "Status");
        object? path = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "Path");
        object? knownPlaces = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "KnownPlaces");
        return VanguardOperatorRuntimeAuditReflection.JoinParts(
            $"type={VanguardOperatorRuntimeAuditReflection.TypeName(enemy)}",
            $"id={VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "EnemyProfileId"))}",
            $"name={VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "EnemyName"))}",
            $"known={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "EnemyKnown"))}",
            $"visible={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "IsVisible"))}",
            $"los={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "InLineOfSight"))}",
            $"canShoot={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "CanShoot"))}",
            $"dist={VanguardOperatorRuntimeAuditReflection.FloatText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "RealDistance"))}",
            $"seenAgo={VanguardOperatorRuntimeAuditReflection.FloatText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "TimeSinceSeen"))}",
            $"heardAgo={VanguardOperatorRuntimeAuditReflection.FloatText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "TimeSinceHeard"))}",
            $"knownAgo={VanguardOperatorRuntimeAuditReflection.FloatText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "TimeSinceLastKnownUpdated"))}",
            $"pathLen={VanguardOperatorRuntimeAuditReflection.FloatText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(path, "PathLength"))}",
            $"lastKnownDist={VanguardOperatorRuntimeAuditReflection.FloatText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(knownPlaces, "BotDistanceFromLastKnown"))}",
            $"shotMe={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(status, "ShotMeRecently"))}",
            $"shotAtMe={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(status, "ShotAtMeRecently"))}",
            $"enemyAction={VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(status, "VulnerableAction"))}");
    }

    private static string FormatVanillaGoalEnemy(object? goalEnemy)
    {
        if (goalEnemy == null)
        {
            return "none";
        }

        return VanguardOperatorRuntimeAuditReflection.JoinParts(
            $"type={VanguardOperatorRuntimeAuditReflection.TypeName(goalEnemy)}",
            $"dist={VanguardOperatorRuntimeAuditReflection.FloatText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goalEnemy, "Distance", "RealDistance"))}",
            $"visible={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goalEnemy, "IsVisible", "Visible"))}",
            $"canShoot={VanguardOperatorRuntimeAuditReflection.BoolText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goalEnemy, "CanShoot"))}",
            $"addTime={VanguardOperatorRuntimeAuditReflection.FloatText(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goalEnemy, "AddTime"))}");
    }

    private static string FormatVector(Vector3 value)
    {
        return $"{value.x:0.0},{value.y:0.0},{value.z:0.0}";
    }
}
#endif
