#if SPT_CLIENT
using System;
using System.Collections;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using Vanguard.Client.Runtime.Audit;

// Responsibility: Reads and normalizes live evidence for Brain Snapshot Reader in the decision snapshot pipeline.
// Flow: Live EFT/Fika/Vanguard objects are inspected defensively, normalized into a bounded snapshot, then handed to policy/decision code.
// Authority boundary: Read-only observer; it does not create missing truth or mutate the game state it inspects.
// Invariant: Missing/stale evidence degrades explicitly and reader failures must not silently fabricate an actionable state.
namespace Vanguard.Client.Runtime.Decision;

internal sealed partial class VanguardOperatorDecisionSnapshotBuilder
{
    private static VanguardBrainDecisionSnapshot CaptureBrain(BotOwner? botOwner)
    {
        if (botOwner == null)
        {
            return new VanguardBrainDecisionSnapshot { Classification = "brain_no_botowner" };
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
            Text(VanguardOperatorRuntimeAuditReflection.InvokeNoArg(brain, "ActiveLayerName")),
            Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(agent, "UsingLayer")));
        string node = VanguardOperatorRuntimeAuditReflection.FirstNonEmpty(
            Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lastResult, "Action")),
            Text(VanguardOperatorRuntimeAuditReflection.InvokeNoArg(brain, "GetLastNode")),
            Text(VanguardOperatorRuntimeAuditReflection.InvokeNoArg(agent, "GetActiveNodeName")));
        string reason = VanguardOperatorRuntimeAuditReflection.FirstNonEmpty(
            Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lastResult, "Reason")),
            Text(VanguardOperatorRuntimeAuditReflection.InvokeNoArg(brain, "GetActiveNodeReason")),
            Text(VanguardOperatorRuntimeAuditReflection.InvokeNoArg(agent, "GetActiveNodeReason")));

        bool? goalVisible = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goalEnemy, "IsVisible", "Visible"));
        bool? goalCanShoot = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goalEnemy, "CanShoot"));
        float? goalDistance = Float(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goalEnemy, "Distance", "RealDistance"));

        return new VanguardBrainDecisionSnapshot
        {
            BrainType = VanguardOperatorRuntimeAuditReflection.TypeName(brain),
            ActiveLayer = layerName,
            Node = node,
            Reason = reason,
            CustomLayer = VanguardOperatorRuntimeAuditReflection.TypeName(customLayer),
            CustomAction = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(customAction, "Type")),
            CustomReason = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(customAction, "Reason")),
            VanillaGoalEnemyVisible = goalVisible,
            VanillaGoalEnemyCanShoot = goalCanShoot,
            VanillaGoalEnemyDistance = goalDistance,
            Classification = ClassifyBrain(layerName, node, goalVisible, goalCanShoot)
        };
    }

    private static string ClassifyBrain(string layerName, string node, bool? goalVisible, bool? goalCanShoot)
    {
        if (goalVisible == true || goalCanShoot == true)
        {
            return "brain_goal_enemy_direct";
        }

        string compact = $"{layerName} {node}";
        if (compact.IndexOf("combat", StringComparison.OrdinalIgnoreCase) >= 0 || compact.IndexOf("enemy", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "brain_combat_related";
        }

        if (string.Equals(layerName, "none", StringComparison.OrdinalIgnoreCase) && string.Equals(node, "none", StringComparison.OrdinalIgnoreCase))
        {
            return "brain_unobserved";
        }

        return "brain_observed";
    }
}
#endif
