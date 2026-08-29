#if SPT_CLIENT
using System;
using System.Collections;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using Vanguard.Client.Runtime.Audit;

// Responsibility: Reads and normalizes live evidence for Movement Snapshot Reader in the decision snapshot pipeline.
// Flow: Live EFT/Fika/Vanguard objects are inspected defensively, normalized into a bounded snapshot, then handed to policy/decision code.
// Authority boundary: Read-only observer; it does not create missing truth or mutate the game state it inspects.
// Invariant: Missing/stale evidence degrades explicitly and reader failures must not silently fabricate an actionable state.
namespace Vanguard.Client.Runtime.Decision;

internal sealed partial class VanguardOperatorDecisionSnapshotBuilder
{
    private static VanguardMovementDecisionSnapshot CaptureMovement(BotOwner? botOwner, float realSpeed)
    {
        if (botOwner == null)
        {
            return new VanguardMovementDecisionSnapshot { RealSpeed = realSpeed, Classification = "movement_no_botowner" };
        }

        object? mover = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Mover", "BotMover");
        object? player = VanguardOperatorRuntimeAuditReflection.GetMember(botOwner, "GetPlayer", "Player");
        object? movementContext = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player, "MovementContext");
        object? patrol = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "PatrollingData", "PatrolData");
        object? goToPoint = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "GoToSomePointData");

        bool? hasPath = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "HasPathAndNoComplete", "HasPathAndNotComplete"));
        bool? patrolPaused = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(patrol, "Paused", "IsPaused"));
        float? distance = Float(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "DistDestination", "SDistDestination"));
        string playerState = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(movementContext, "CurrentStateName", "CurrentState"));

        return new VanguardMovementDecisionSnapshot
        {
            MoverType = VanguardOperatorRuntimeAuditReflection.TypeName(mover),
            RealSpeed = realSpeed,
            TargetSpeed = Float(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "DestMoveSpeed", "TargetMoveSpeed", "MoveSpeed", "Speed")),
            Sprinting = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "Sprinting", "Sprint", "IsSprint", "IsSprinting")),
            PlayerSprintEnabled = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player, "IsSprintEnabled")),
            PatrolPaused = patrolPaused,
            HasPath = hasPath,
            DistanceToDestination = distance,
            TargetPoint = Vector(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "TargetPoint")),
            DestinationPoint = Vector(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "RealDestPoint", "CurrentTargetPoint")),
            CurrentCornerPoint = Vector(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "CurrentCornerPoint")),
            GoToPoint = Vector(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goToPoint, "Point")),
            GoToDistance = Float(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(goToPoint, "DistToPoint")),
            PlayerState = playerState,
            Classification = ClassifyMovement(realSpeed, hasPath, patrolPaused, distance, playerState)
        };
    }

    private static string ClassifyMovement(float realSpeed, bool? hasPath, bool? patrolPaused, float? distance, string playerState)
    {
        if (patrolPaused == true)
        {
            return "movement_paused";
        }

        if (hasPath == true && realSpeed > 0.35f)
        {
            return "movement_path_progress";
        }

        if (hasPath == true && distance.HasValue && distance.Value > 2.5f && realSpeed <= 0.12f)
        {
            return "movement_path_stalled";
        }

        if (realSpeed > 0.35f)
        {
            return "movement_free_progress";
        }

        if (!string.Equals(playerState, "none", StringComparison.OrdinalIgnoreCase) && playerState.IndexOf("idle", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return "movement_playerstate_active";
        }

        return "movement_idle";
    }
}
#endif
