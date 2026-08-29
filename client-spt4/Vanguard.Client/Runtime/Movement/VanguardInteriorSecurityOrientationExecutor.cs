#if SPT_CLIENT
using System;
using System.Linq;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;

// Responsibility: Executes previously-authorized Interior Security Orientation Executor work for the movement/cohesion runtime.
// Flow: An authorized intent/plan is revalidated against current evidence, applied through the bounded EFT/runtime surface, then reconciled through state/readback telemetry.
// Authority boundary: Execution consumes authority granted elsewhere; it must not invent intent or override higher-priority safety ownership.
// Invariant: Every mutation is raid-scoped/recoverable and terminal outcomes are reconciled against current canonical evidence.
namespace Vanguard.Client.Runtime.Movement;

/// <summary>
/// Vanguard applies the watch direction owned by a persistent interior-volume assignment. It never creates movement,
/// never changes targets and remains silent while SAIN Combat, medicine or a path is active.
/// </summary>
internal static class VanguardInteriorSecurityOrientationExecutor
{
    private static readonly object Sync = new();
    private static readonly System.Collections.Generic.Dictionary<string, DateTimeOffset> LastApplyAtByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ApplyInterval = TimeSpan.FromSeconds(0.75d);

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            LastApplyAtByBot.Clear();
        }

        VanguardClientDiagnosticsLog.Info(VanguardPrimaryExecutionContract.InteriorVolumeSecurityStatusTag,
            $"VANGUARD_INTERIOR_ORIENTATION_RESET reason={Safe(reason)}; state=cleared; tag={VanguardPrimaryExecutionContract.InteriorVolumeSecurityStatusTag}; legacyTag={VanguardPrimaryExecutionContract.InteriorCoverageStatusTag}");
    }

    public static void Tick()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (OperatorDecisionSnapshot snapshot in VanguardOperatorDecisionSnapshotService.GetLatestSnapshots().Where(value => value != null && value.Alive))
        {
            if (!VanguardInteriorSecurityPlanner.TryGetAssignment(snapshot.BotProfileId, now, out var assignment)
                || HorizontalDistance(snapshot.Position, assignment.Anchor) > 3.5f
                || snapshot.Movement.HasPath == true
                || snapshot.Medical.Actionability.AnyMedicineUsing
                || VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot)
                || VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(snapshot)
                || VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out _)
                || !CanApply(snapshot.BotProfileId, now))
            {
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var runtime)
                || runtime.BotOwner == null
                || runtime.BotOwner.IsDead)
            {
                continue;
            }

            try
            {
                runtime.BotOwner.Steering?.LookToPoint(assignment.WatchPoint);
                MarkApplied(snapshot.BotProfileId, now);
            }
            catch (Exception ex)
            {
                MarkApplied(snapshot.BotProfileId, now);
                VanguardClientDiagnosticsLog.Warning(VanguardPrimaryExecutionContract.InteriorVolumeSecurityStatusTag,
                    $"VANGUARD_INTERIOR_ORIENTATION_FAILED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; portal={Safe(assignment.PortalKey)}; error={Safe(ex.GetType().Name)}; doctrine=best_effort_orientation_only_no_movement_or_sain_override; tag={VanguardPrimaryExecutionContract.InteriorVolumeSecurityStatusTag}; legacyTag={VanguardPrimaryExecutionContract.InteriorCoverageStatusTag}");
            }
        }
    }

    private static bool CanApply(string botProfileId, DateTimeOffset now)
    {
        lock (Sync)
        {
            return !LastApplyAtByBot.TryGetValue(botProfileId, out var last) || now - last >= ApplyInterval;
        }
    }

    private static void MarkApplied(string botProfileId, DateTimeOffset now)
    {
        lock (Sync)
        {
            LastApplyAtByBot[botProfileId] = now;
        }
    }

    private static float HorizontalDistance(UnityEngine.Vector3 a, UnityEngine.Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return UnityEngine.Vector3.Distance(a, b);
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }
}
#endif
