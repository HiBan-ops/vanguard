#if SPT_CLIENT
using System;
using System.Linq;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;

// Responsibility: Keep passive Operator audit inspection dormant until registered runtime Operators have live BotOwner handles.
// Flow: Inspect the Operator runtime registry, open the gate on live ownership and reset one-time diagnostic flags on raid lifecycle changes.
// Authority boundary: The Operator runtime registry owns spawn registration; this guard does not probe external brains or create Operators.
// Invariant: The gate remains closed with no registered Operator or no live BotOwner handle.
namespace Vanguard.Client.Runtime.Audit;

/// <summary>
/// Protects raid loading from the passive audit layer.
/// runtime-audit subsystem rule: no audit polling, no audit sync tick and no external brain probing
/// before Vanguard has actually registered spawned Operators with live BotOwner handles.
/// The guard preserves spawn/bootstrap ordering so audit discovery cannot race Operator registration.
/// </summary>
internal static class VanguardOperatorRuntimeAuditLoadGuard
{
    private static bool openLogged;
    private static bool waitingLogged;

    public static bool IsOpen()
    {
        var records = VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators();
        if (records.Count == 0)
        {
            LogWaitingOnce("no_runtime_operator_registered");
            return false;
        }

        if (!records.Any(record => record.BotOwner != null))
        {
            LogWaitingOnce("runtime_operator_without_botowner");
            return false;
        }

        if (!openLogged)
        {
            openLogged = true;
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.AuditLoadGuardStatusTag,
                $"VANGUARD_AUDIT_LOAD_GUARD_OK: audit gate open after operator runtime registration; operators={records.Count}; liveBotOwners={records.Count(record => record.BotOwner != null)}");
        }

        return true;
    }

    public static void ResetForRaidLifecycle(string reason)
    {
        openLogged = false;
        waitingLogged = false;
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.AuditLoadGuardStatusTag,
            $"audit load guard reset reason={reason}");
    }

    private static void LogWaitingOnce(string reason)
    {
        if (waitingLogged)
        {
            return;
        }

        waitingLogged = true;
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.AuditLoadGuardStatusTag,
            $"audit dormant before operator spawn reason={reason}; no polling/probing before Vanguard runtime registration");
    }
}
#endif
