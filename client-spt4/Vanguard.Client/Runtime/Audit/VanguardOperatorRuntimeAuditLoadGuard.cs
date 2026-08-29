#if SPT_CLIENT
using System;
using System.Linq;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;

// Responsibility: Provides Operator Runtime Audit Load Guard support for the runtime audit.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
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
