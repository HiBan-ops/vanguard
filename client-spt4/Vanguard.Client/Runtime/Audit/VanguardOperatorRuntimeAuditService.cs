#if SPT_CLIENT
using System;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;
using Vanguard.Client.Raid.Runtime;

// Responsibility: Coordinates Operator Runtime Audit Service for the runtime audit, delegating specialized work to its collaborators.
// Flow: Current raid/runtime evidence is normalized, applicable guards and ownership rules are evaluated, then the service updates only its bounded runtime/UI responsibility.
// Authority boundary: Service coordinates its domain but does not fabricate server persistence truth or bypass higher-priority runtime authorities.
// Invariant: State is lifecycle-scoped, stale work is releasable, and failures degrade without leaving hidden long-lived ownership.
namespace Vanguard.Client.Runtime.Audit;

internal static class VanguardOperatorRuntimeAuditService
{
    private static DateTimeOffset lastSnapshotAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    public static void ResetForRaidLifecycle(string reason)
    {
        lastSnapshotAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        VanguardOperatorRuntimeAuditLoadGuard.ResetForRaidLifecycle(reason);
        VanguardOperatorRuntimeAuditProbe.ResetForRaidLifecycle(reason);
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.AuditStableBaselineStatusTag,
            $"VANGUARD_AUDIT_STABLE_BASELINE_OK: audit runtime state reset for raid lifecycle reason={reason}");
    }

    public static void Tick()
    {
        // runtime-audit subsystem: the passive audit probes must not do any work during raid loading.
        // F12 settings sync is handled separately and is safe before spawn; only brain
        // probes stay dormant until Vanguard has registered at least one spawned Operator
        // with a live BotOwner. This preserves the validated spawn order: registration first, live-owner audit second.
        if (!VanguardOperatorRuntimeAuditLoadGuard.IsOpen())
        {
            return;
        }

        if (!VanguardOperatorRuntimeAuditSyncService.EffectiveEnabled)
        {
            return;
        }

        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - lastSnapshotAtUtc < TimeSpan.FromSeconds(VanguardOperatorRuntimeAuditOptions.GetSnapshotIntervalSeconds()))
        {
            return;
        }

        lastSnapshotAtUtc = now;
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.OperatorRuntimeAuditStatusTag,
                $"VANGUARD_OPERATOR_AUDIT_BOOT enabled=true; readOnly=true; authority=headless_or_host; headless={VanguardFikaCompat.IsHeadless}; host={VanguardFikaCompat.IsHost}; build={VanguardBuildVersion.BuildLabel}");
        }

        var records = VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators();
        foreach (var record in records)
        {
            if (record.BotOwner == null)
            {
                continue;
            }

            try
            {
                var snapshot = VanguardOperatorRuntimeAuditProbe.Capture(record);
                if (VanguardOperatorRuntimeAuditProbe.ShouldLogTransition(snapshot))
                {
                    VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OperatorRuntimeAuditStatusTag, VanguardOperatorRuntimeAuditProbe.Format(snapshot, "VANGUARD_OPERATOR_AUDIT_CHANGED"));
                }

                if (VanguardOperatorRuntimeAuditOptions.GetSummaryLogEnabled() && VanguardOperatorRuntimeAuditProbe.ShouldLogSummary(snapshot))
                {
                    VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OperatorRuntimeAuditStatusTag, VanguardOperatorRuntimeAuditProbe.Format(snapshot, "VANGUARD_OPERATOR_AUDIT_SUMMARY"));
                }
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.OperatorRuntimeAuditStatusTag, $"audit snapshot failed operator={record.OperatorId}; type={exception.GetType().Name}; message={exception.Message}");
            }
        }
    }
}
#endif
