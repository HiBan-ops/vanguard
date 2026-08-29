using System;
using Vanguard.Client.Diagnostics;

// Responsibility: Defines data/state contracts used by the Off-Raid Operator inventory bridge, centered on Operator Inventory Exit Reload State.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.UI.OffRaid.Inventory;

/// <summary>
/// Tracks the local UI contamination risk created by opening a Vanguard Operator
/// inventory through a direct vanilla InventoryScreen controller.
///
/// This state is intentionally client-only and does not touch Operator inventory,
/// the player stash or server persistence.  It is cleared only after the Operator
/// commit, inventory-mode exit and player-menu reload sequence has completed.
/// </summary>
internal static class VanguardOperatorInventoryExitReloadState
{
    private static readonly object Gate = new();
    private static int openedCount;
    private static string? lastOperatorId;
    private static DateTimeOffset lastOpenedUtc = DateTimeOffset.MinValue;
    private static bool dirty;
    private static bool reloadInProgress;
    private static bool pendingBattleInputReleaseForNextRaid;
    private static int pendingBattleInputReleaseSequence;
    private static string? pendingBattleInputReleaseOperatorId;
    private static string lastResult = "idle";

    public static bool IsDirty
    {
        get
        {
            lock (Gate)
            {
                return dirty;
            }
        }
    }

    public static void MarkOperatorInventoryOpened(string source, string? operatorId)
    {
        int count;
        lock (Gate)
        {
            openedCount++;
            count = openedCount;
            lastOperatorId = operatorId;
            lastOpenedUtc = DateTimeOffset.UtcNow;
            dirty = true;
            reloadInProgress = false;
            pendingBattleInputReleaseForNextRaid = false;
            pendingBattleInputReleaseOperatorId = null;
            lastResult = "operator_inventory_opened";
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorInventoryExitReloadStatusTag,
            $"exit_reload_dirty source={source}; operator={operatorId ?? "<none>"}; openedCount={count}");
    }

    public static void MarkExitReloadStarted(string source)
    {
        lock (Gate)
        {
            reloadInProgress = true;
            lastResult = "reload_started";
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorInventoryExitReloadStatusTag,
            $"exit_reload_started source={source}; {Describe()}");
    }

    public static void MarkExitReloadCompleted(string source, bool success, string reason)
    {
        int consumedCount;
        string? operatorId;
        lock (Gate)
        {
            consumedCount = openedCount;
            operatorId = lastOperatorId;
            reloadInProgress = false;
            lastResult = reason;

            if (success)
            {
                if (consumedCount > 0)
                {
                    pendingBattleInputReleaseForNextRaid = true;
                    pendingBattleInputReleaseOperatorId = operatorId;
                    pendingBattleInputReleaseSequence++;
                }

                openedCount = 0;
                lastOperatorId = null;
                lastOpenedUtc = DateTimeOffset.MinValue;
                dirty = false;
            }
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorInventoryExitReloadStatusTag,
            $"exit_reload_completed source={source}; success={success}; reason={reason}; consumedCount={consumedCount}; operator={operatorId ?? "<none>"}; state={Describe()}");
    }


    public static bool TryConsumePendingBattleInputReleaseForRaid(string source, out string reason)
    {
        lock (Gate)
        {
            if (!pendingBattleInputReleaseForNextRaid)
            {
                reason = $"pending=False; source={source}; {DescribeLocked()}";
                return false;
            }

            string operatorId = pendingBattleInputReleaseOperatorId ?? "<none>";
            int sequence = pendingBattleInputReleaseSequence;
            pendingBattleInputReleaseForNextRaid = false;
            pendingBattleInputReleaseOperatorId = null;
            reason = $"pending=True; source={source}; sequence={sequence}; operator={operatorId}; lastResult={lastResult}";
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
            $"battle_input_node_release_consumed_for_raid {reason}");
        return true;
    }

    public static string Describe()
    {
        lock (Gate)
        {
            return DescribeLocked();
        }
    }

    private static string DescribeLocked()
    {
        int ageSeconds = lastOpenedUtc == DateTimeOffset.MinValue
            ? -1
            : (int)Math.Max(0, (DateTimeOffset.UtcNow - lastOpenedUtc).TotalSeconds);
        return $"dirty={dirty}; reloadInProgress={reloadInProgress}; openedCount={openedCount}; lastOperator={lastOperatorId ?? "<none>"}; ageSeconds={ageSeconds}; pendingBattleInputRelease={pendingBattleInputReleaseForNextRaid}; pendingBattleInputReleaseSequence={pendingBattleInputReleaseSequence}; lastResult={lastResult}";
    }
}
