#if SPT_CLIENT
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using UnityEngine;
using Vanguard.Client.Runtime.Audit;

// Responsibility: Adapts a Vanguard-qualified hostile target into the concrete SAIN/EFT target state needed for the Operator to engage it.
// Flow: After awareness selects a valid target, the adapter resolves compatible SAIN/EFT members, commits target/enemy information, verifies the applied state and reports failure without inventing success.
// Authority boundary: Vanguard chooses when a target is eligible; SAIN/EFT remain owners of their internal enemy/target objects and combat execution.
// Invariant: Friendly/invalid targets are never committed, reflective drift fails open to safe recovery, and a commit is considered successful only when readback confirms the expected target.
namespace Vanguard.Client.Runtime.Awareness;

/// <summary>
/// Single mutation boundary from a Vanguard-qualified individual assignment into SAIN.
///
/// Vanguard owns contact qualification, squad propagation and per-Operator assignment. SAIN owns
/// combat execution. This adapter therefore resolves the concrete SAIN Enemy object expected by
/// SAINEnemyController.GoalEnemy, establishes only the shared last-known position (never recipient
/// LOS), synchronizes EFT BotMemory, verifies both projections and restores the previous target when
/// the transaction is incomplete.
/// </summary>
internal static partial class VanguardCombatAwarenessBridge
{
    public const string TargetCommitStatusTag = "VANGUARD_ATOMIC_SAIN_TARGET_COMMIT_STATUS";

    private static SainTargetApplyResult CommitQualifiedSainTarget(
        BotOwner botOwner,
        string targetId,
        EnemyInfo bootstrapEnemyInfo,
        bool attackImmediately)
    {
        string normalizedTarget = Normalize(targetId);
        if (botOwner == null
            || botOwner.IsDead
            || botOwner.Memory == null
            || bootstrapEnemyInfo == null
            || string.Equals(normalizedTarget, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new SainTargetApplyResult(false, false, false, false, "atomic_commit_invalid_input");
        }

        GameWorld? world = Singleton<GameWorld>.Instance;
        Player? targetPlayer = world?.GetAlivePlayerByProfileID(normalizedTarget);
        if (targetPlayer == null
            || targetPlayer.Transform == null
            || targetPlayer.HealthController?.IsAlive != true)
        {
            return new SainTargetApplyResult(false, false, false, false, "atomic_commit_target_not_alive");
        }

        object? sainComponent = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(
            botOwner,
            "SAIN.Components.BotComponent");
        object? enemyController = ResolveEnemyController(sainComponent);
        if (enemyController == null)
        {
            return new SainTargetApplyResult(false, false, false, false, "atomic_commit_enemy_controller_missing");
        }

        object? previousControllerGoal = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemyController, "GoalEnemy");
        EnemyInfo? previousMemoryGoal = botOwner.Memory.GoalEnemy;
        EnemyInfo? previousMemoryLast = botOwner.Memory.LastEnemy;
        bool previousAttackImmediately = botOwner.Memory.AttackImmediately;
        bool previousIsPeace = botOwner.Memory.IsPeace;

        bool controllerSet = false;
        bool memorySet = false;
        bool goalRecalculated = false;

        object? sainEnemy = ResolveOrCreateExactSainEnemy(
            enemyController,
            targetPlayer,
            normalizedTarget,
            out string resolveReason);
        if (sainEnemy == null)
        {
            return new SainTargetApplyResult(
                false,
                false,
                false,
                false,
                "atomic_commit_sain_enemy_missing:" + Safe(resolveReason));
        }

        string resolvedEnemyId = ResolveEnemyProfileId(sainEnemy);
        if (!SameTarget(resolvedEnemyId, normalizedTarget))
        {
            return new SainTargetApplyResult(
                false,
                false,
                false,
                false,
                "atomic_commit_sain_enemy_mismatch:" + Safe(resolvedEnemyId));
        }

        EnemyInfo? exactEnemyInfo = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainEnemy, "EnemyInfo") as EnemyInfo;
        exactEnemyInfo ??= bootstrapEnemyInfo;
        if (!SameTarget(ResolveEnemyProfileId(exactEnemyInfo), normalizedTarget))
        {
            return new SainTargetApplyResult(false, false, false, false, "atomic_commit_enemyinfo_mismatch");
        }

        if (!EnsureExactSainEnemyKnown(
                sainEnemy,
                targetPlayer.Position,
                out string knownReason))
        {
            return new SainTargetApplyResult(
                false,
                false,
                false,
                false,
                "atomic_commit_enemy_not_known:" + Safe(knownReason));
        }

        try
        {
            // Refresh vanilla bookkeeping before the final SAIN write. CalcGoal may alter EFT memory,
            // therefore memory is reasserted once afterwards and the concrete SAIN Enemy is committed
            // last. No second coordinator is allowed to write target authority.
            memorySet = TrySetMemoryProjection(
                botOwner,
                exactEnemyInfo,
                attackImmediately,
                out string memoryReasonBefore);

            goalRecalculated = TryInvokeNoArg(botOwner, "CalcGoal")
                || TryInvokeNoArg(botOwner, "UpdateGoal")
                || TryInvokeNoArg(botOwner, "RecalcGoal");

            memorySet = TrySetMemoryProjection(
                    botOwner,
                    exactEnemyInfo,
                    attackImmediately,
                    out string memoryReasonAfter)
                || memorySet;

            controllerSet = TrySetExactSainGoal(
                enemyController,
                sainEnemy,
                previousControllerGoal,
                out string controllerReason);

            string afterController = ResolveExactSainControllerGoalId(enemyController);
            string afterMemory = ResolveExactMemoryGoalId(botOwner);
            bool verified = controllerSet
                && memorySet
                && SameTarget(afterController, normalizedTarget)
                && SameTarget(afterMemory, normalizedTarget);

            if (verified)
            {
                TryAlertMemoryAfterVerifiedCommit(botOwner);
                return new SainTargetApplyResult(
                    true,
                    true,
                    true,
                    goalRecalculated,
                    "atomic_verified"
                        + ":controller=" + Safe(controllerReason)
                        + ":memoryBefore=" + Safe(memoryReasonBefore)
                        + ":memoryAfter=" + Safe(memoryReasonAfter)
                        + ":afterController=" + Safe(afterController)
                        + ":afterMemory=" + Safe(afterMemory)
                        + ":resolve=" + Safe(resolveReason)
                        + ":known=" + Safe(knownReason));
            }

            bool controllerRolledBack = RestoreExactSainGoal(
                enemyController,
                previousControllerGoal,
                out string rollbackControllerReason);
            bool memoryRolledBack = RestoreMemoryProjection(
                botOwner,
                previousMemoryGoal,
                previousMemoryLast,
                previousAttackImmediately,
                previousIsPeace,
                out string rollbackMemoryReason);

            return new SainTargetApplyResult(
                false,
                controllerSet,
                memorySet,
                goalRecalculated,
                "atomic_unverified"
                    + ":afterController=" + Safe(afterController)
                    + ":afterMemory=" + Safe(afterMemory)
                    + ":controller=" + Safe(controllerReason)
                    + ":memoryBefore=" + Safe(memoryReasonBefore)
                    + ":memoryAfter=" + Safe(memoryReasonAfter)
                    + ":rollbackController=" + Bool(controllerRolledBack)
                    + ":rollbackControllerReason=" + Safe(rollbackControllerReason)
                    + ":rollbackMemory=" + Bool(memoryRolledBack)
                    + ":rollbackMemoryReason=" + Safe(rollbackMemoryReason)
                    + ":rollbackControllerAfter=" + Safe(ResolveExactSainControllerGoalId(enemyController))
                    + ":rollbackMemoryAfter=" + Safe(ResolveExactMemoryGoalId(botOwner)));
        }
        catch (Exception exception)
        {
            RestoreExactSainGoal(enemyController, previousControllerGoal, out _);
            RestoreMemoryProjection(
                botOwner,
                previousMemoryGoal,
                previousMemoryLast,
                previousAttackImmediately,
                previousIsPeace,
                out _);

            return new SainTargetApplyResult(
                false,
                controllerSet,
                memorySet,
                goalRecalculated,
                "atomic_commit_exception:"
                    + exception.GetType().Name
                    + ":"
                    + Safe(exception.Message));
        }
    }

    private static string ResolveExactSainControllerGoalId(object enemyController)
    {
        return ResolveEnemyProfileId(
            VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemyController, "GoalEnemy"));
    }

    private static string ResolveExactMemoryGoalId(BotOwner botOwner)
    {
        return ResolveEnemyProfileId(botOwner.Memory?.GoalEnemy);
    }

    private static object? ResolveOrCreateExactSainEnemy(
        object enemyController,
        Player targetPlayer,
        string targetId,
        out string reason)
    {
        object? existing = ResolveSainEnemyById(enemyController, targetId);
        if (existing != null && SameTarget(ResolveEnemyProfileId(existing), targetId))
        {
            reason = "existing_sain_enemy";
            return existing;
        }

        // Player implements IPlayer at runtime. Reflection avoids importing SAIN and its optional
        // DissonanceVoip dependency into Vanguard's compile-time graph.
        object? created = TryInvokeCompatible(enemyController, "CheckAddEnemy", targetPlayer);
        if (created != null && SameTarget(ResolveEnemyProfileId(created), targetId))
        {
            reason = "created_by_check_add_enemy";
            return created;
        }

        existing = ResolveSainEnemyById(enemyController, targetId);
        if (existing != null && SameTarget(ResolveEnemyProfileId(existing), targetId))
        {
            reason = "resolved_after_check_add_enemy";
            return existing;
        }

        reason = "sain_enemy_not_created";
        return null;
    }

    private static object? ResolveSainEnemyById(object enemyController, string targetId)
    {
        return TryInvokeCompatible(enemyController, "GetEnemy", targetId, false)
            ?? TryInvokeCompatible(enemyController, "GetEnemy", targetId, true)
            ?? TryReadStringDictionaryValue(
                VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemyController, "Enemies"),
                targetId);
    }

    private static bool EnsureExactSainEnemyKnown(
        object sainEnemy,
        Vector3 targetPosition,
        out string reason)
    {
        bool positionReady = false;
        bool knownReady = false;

        object? knownPlaces = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainEnemy, "KnownPlaces");
        object? lastKnown = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(knownPlaces, "LastKnownPosition");
        if (lastKnown != null)
        {
            positionReady = true;
        }
        else
        {
            TryInvokeCompatible(knownPlaces, "UpdateSeenPlace", targetPosition, Time.time);
            lastKnown = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(knownPlaces, "LastKnownPosition");
            positionReady = lastKnown != null;
        }

        bool alreadyKnown = ReadBooleanMember(sainEnemy, "EnemyKnown");
        if (alreadyKnown)
        {
            knownReady = true;
        }
        else
        {
            object? eventsObject = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainEnemy, "Events");
            object? knownToggle = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(eventsObject, "OnEnemyKnownChanged");
            TryInvokeCompatible(knownToggle, "CheckToggle", true, Time.time);
            knownReady = ReadBooleanMember(sainEnemy, "EnemyKnown");
        }

        reason = "position=" + Bool(positionReady) + ":known=" + Bool(knownReady);
        return positionReady && knownReady;
    }

    private static bool TrySetMemoryProjection(
        BotOwner botOwner,
        EnemyInfo exactEnemyInfo,
        bool attackImmediately,
        out string reason)
    {
        try
        {
            botOwner.Memory.GoalEnemy = exactEnemyInfo;
            botOwner.Memory.LastEnemy = exactEnemyInfo;
            botOwner.Memory.AttackImmediately = attackImmediately;
            botOwner.Memory.IsPeace = false;

            bool verified = SameTarget(
                ResolveEnemyProfileId(botOwner.Memory.GoalEnemy),
                ResolveEnemyProfileId(exactEnemyInfo));
            reason = verified ? "memory_verified" : "memory_goal_mismatch";
            return verified;
        }
        catch (Exception exception)
        {
            reason = "memory_exception:" + exception.GetType().Name;
            return false;
        }
    }

    private static void TryAlertMemoryAfterVerifiedCommit(BotOwner botOwner)
    {
        try
        {
            // Alert state only. It does not fabricate visibility, line of sight or a seen timestamp.
            botOwner.Memory.Spotted(byHit: false);
        }
        catch
        {
            // The target transaction is already verified. Failure to raise this optional EFT alert
            // must not invalidate the exact SAIN and BotMemory target projections.
        }
    }

    private static bool TrySetExactSainGoal(
        object enemyController,
        object sainEnemy,
        object? previousGoal,
        out string reason)
    {
        Type controllerType = enemyController.GetType();
        Type enemyType = sainEnemy.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        string requestedId = ResolveEnemyProfileId(sainEnemy);

        PropertyInfo? goalProperty = controllerType.GetProperty("GoalEnemy", flags);
        MethodInfo? setter = goalProperty?.GetSetMethod(nonPublic: true);
        if (goalProperty != null
            && setter != null
            && goalProperty.PropertyType.IsAssignableFrom(enemyType))
        {
            try
            {
                setter.Invoke(enemyController, new[] { sainEnemy });
                if (SameTarget(
                        ResolveEnemyProfileId(goalProperty.GetValue(enemyController)),
                        requestedId))
                {
                    reason = "private_goal_property";
                    return true;
                }
            }
            catch
            {
                // An event subscriber can throw after the private setter has already committed the
                // target. Read back before falling through so we do not emit the change twice.
                if (SameTarget(
                        ResolveEnemyProfileId(goalProperty.GetValue(enemyController)),
                        requestedId))
                {
                    reason = "private_goal_property_committed_event_threw";
                    return true;
                }
            }
        }

        FieldInfo? goalField = controllerType.GetField("_goalEnemy", flags);
        if (goalField != null && goalField.FieldType.IsAssignableFrom(enemyType))
        {
            try
            {
                TrySetAssignableMember(enemyController, "LastGoalEnemy", previousGoal, out _);
                goalField.SetValue(enemyController, sainEnemy);
                TryNotifySainEnemyChanged(enemyController, sainEnemy, previousGoal);
                if (SameTarget(
                        ResolveEnemyProfileId(goalField.GetValue(enemyController)),
                        requestedId))
                {
                    reason = "private_goal_field_with_event";
                    return true;
                }
            }
            catch
            {
                // Report a clean failure below. The outer transaction will restore both projections.
            }
        }

        reason = "no_verified_assignable_sain_goal_member";
        return false;
    }

    private static bool RestoreExactSainGoal(
        object enemyController,
        object? previousGoal,
        out string reason)
    {
        Type controllerType = enemyController.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        PropertyInfo? goalProperty = controllerType.GetProperty("GoalEnemy", flags);
        MethodInfo? setter = goalProperty?.GetSetMethod(nonPublic: true);
        if (goalProperty != null
            && setter != null
            && CanAssign(goalProperty.PropertyType, previousGoal))
        {
            try
            {
                setter.Invoke(enemyController, new[] { previousGoal });
                if (SameOptionalTarget(
                        ResolveEnemyProfileId(goalProperty.GetValue(enemyController)),
                        ResolveEnemyProfileId(previousGoal)))
                {
                    reason = "restored_goal_property";
                    return true;
                }
            }
            catch
            {
                if (SameOptionalTarget(
                        ResolveEnemyProfileId(goalProperty.GetValue(enemyController)),
                        ResolveEnemyProfileId(previousGoal)))
                {
                    reason = "restored_goal_property_event_threw";
                    return true;
                }
            }
        }

        FieldInfo? goalField = controllerType.GetField("_goalEnemy", flags);
        if (goalField != null && CanAssign(goalField.FieldType, previousGoal))
        {
            try
            {
                object? current = goalField.GetValue(enemyController);
                TrySetAssignableMember(enemyController, "LastGoalEnemy", current, out _);
                goalField.SetValue(enemyController, previousGoal);
                TryNotifySainEnemyChanged(enemyController, previousGoal, current);
                if (SameOptionalTarget(
                        ResolveEnemyProfileId(goalField.GetValue(enemyController)),
                        ResolveEnemyProfileId(previousGoal)))
                {
                    reason = "restored_goal_field_with_event";
                    return true;
                }
            }
            catch (Exception exception)
            {
                reason = "restore_exception:" + exception.GetType().Name;
                return false;
            }
        }

        reason = "restore_member_missing_or_unverified";
        return false;
    }

    private static bool RestoreMemoryProjection(
        BotOwner botOwner,
        EnemyInfo? previousGoal,
        EnemyInfo? previousLast,
        bool previousAttackImmediately,
        bool previousIsPeace,
        out string reason)
    {
        try
        {
            botOwner.Memory.GoalEnemy = previousGoal;
            botOwner.Memory.LastEnemy = previousLast;
            botOwner.Memory.AttackImmediately = previousAttackImmediately;
            botOwner.Memory.IsPeace = previousIsPeace;

            bool verified = SameOptionalTarget(
                ResolveEnemyProfileId(botOwner.Memory.GoalEnemy),
                ResolveEnemyProfileId(previousGoal));
            reason = verified ? "memory_projection_restored" : "memory_restore_goal_mismatch";
            return verified;
        }
        catch (Exception exception)
        {
            reason = "memory_restore_exception:" + exception.GetType().Name;
            return false;
        }
    }

    private static void TryNotifySainEnemyChanged(
        object enemyController,
        object? current,
        object? previous)
    {
        object? eventsObject = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemyController, "Events");
        TryInvokeCompatible(eventsObject, "EnemyChanged", current, previous);
    }

    private static bool TrySetAssignableMember(
        object? instance,
        string name,
        object? value,
        out string reason)
    {
        if (instance == null || string.IsNullOrWhiteSpace(name))
        {
            reason = "missing_instance_or_name";
            return false;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = instance.GetType();
        try
        {
            PropertyInfo? property = type.GetProperty(name, flags);
            MethodInfo? setter = property?.GetSetMethod(nonPublic: true);
            if (property != null
                && setter != null
                && CanAssign(property.PropertyType, value))
            {
                setter.Invoke(instance, new[] { value });
                reason = "property";
                return true;
            }

            FieldInfo? field = type.GetField(name, flags);
            if (field != null && CanAssign(field.FieldType, value))
            {
                field.SetValue(instance, value);
                reason = "field";
                return true;
            }
        }
        catch (Exception exception)
        {
            reason = "set_exception:" + exception.GetType().Name;
            return false;
        }

        reason = "member_missing_or_incompatible";
        return false;
    }

    private static bool ReadBooleanMember(object? instance, string name)
    {
        object? value = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(instance, name);
        return value is bool flag && flag;
    }

    private static bool SameOptionalTarget(string first, string second)
    {
        bool firstNone = string.Equals(Normalize(first), "none", StringComparison.OrdinalIgnoreCase);
        bool secondNone = string.Equals(Normalize(second), "none", StringComparison.OrdinalIgnoreCase);
        return (firstNone && secondNone) || SameTarget(first, second);
    }

    private static bool CanAssign(Type targetType, object? value)
    {
        return value == null
            ? !targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null
            : targetType.IsInstanceOfType(value);
    }

    private static object? TryInvokeCompatible(
        object? instance,
        string name,
        params object?[] arguments)
    {
        if (instance == null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (MethodInfo method in instance.GetType()
            .GetMethods(flags)
            .Where(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal)))
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != arguments.Length)
            {
                continue;
            }

            bool compatible = true;
            for (int index = 0; index < parameters.Length; index++)
            {
                if (!CanAssign(parameters[index].ParameterType, arguments[index]))
                {
                    compatible = false;
                    break;
                }
            }

            if (!compatible)
            {
                continue;
            }

            try
            {
                return method.Invoke(instance, arguments);
            }
            catch
            {
                // Another exact overload may still be usable. The caller always verifies the
                // resulting target and never treats invocation alone as success.
            }
        }

        return null;
    }

    private static object? TryReadStringDictionaryValue(object? dictionaryLike, string key)
    {
        if (dictionaryLike is IDictionary dictionary && dictionary.Contains(key))
        {
            return dictionary[key];
        }

        return null;
    }
}
#endif
