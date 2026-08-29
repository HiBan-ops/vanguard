#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EFT;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;

// Responsibility: Coordinates Operator Bot Type Diagnostics Service for the MoreBots integration, delegating specialized work to its collaborators.
// Flow: Current raid/runtime evidence is normalized, applicable guards and ownership rules are evaluated, then the service updates only its bounded runtime/UI responsibility.
// Authority boundary: Service coordinates its domain but does not fabricate server persistence truth or bypass higher-priority runtime authorities.
// Invariant: State is lifecycle-scoped, stale work is releasable, and failures degrade without leaving hidden long-lived ownership.
namespace Vanguard.Client.Runtime.Integrations.MoreBots;

internal static class VanguardOperatorBotTypeDiagnosticsService
{
    private static readonly Dictionary<string, string> LastRoleByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10.0d);
    private static readonly TimeSpan SummaryHeartbeat = TimeSpan.FromSeconds(60.0d);
    private static DateTimeOffset nextSummaryAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset lastSummaryLogAtUtc = DateTimeOffset.MinValue;
    private static string lastSummarySignature = string.Empty;

    public static void Tick()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now < nextSummaryAtUtc)
        {
            return;
        }

        nextSummaryAtUtc = now + PollInterval;
        int total = 0;
        int custom = 0;
        int fallback = 0;

        foreach (var record in VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators())
        {
            if (record.BotOwner == null || string.IsNullOrWhiteSpace(record.BotProfileId))
            {
                continue;
            }

            total++;
            string role = VanguardOperatorBotTypes.DescribeRole(record.BotOwner);
            bool isCustom = VanguardOperatorBotTypes.IsVanguardOperatorRole(record.BotOwner);
            if (isCustom) custom++; else fallback++;

            if (LastRoleByBotProfileId.TryGetValue(record.BotProfileId, out var previous) && string.Equals(previous, role, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            LastRoleByBotProfileId[record.BotProfileId] = role;
            VanguardClientDiagnosticsLog.Info(
                VanguardOperatorBotTypes.StatusTag,
                $"VANGUARD_OPERATOR_ROLE_BOUND operator={Safe(record.OperatorId)}; botProfile={Safe(record.BotProfileId)}; role={Safe(role)}; custom={Bool(isCustom)}; expectedSubstring={VanguardOperatorBotTypes.RoleSubstring}; moreBotsValues={VanguardOperatorBotTypes.UsecRoleValue},{VanguardOperatorBotTypes.BearRoleValue}; tag={VanguardOperatorBotTypes.StatusTag}");
        }

        string summarySignature = total.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "|" + custom.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "|" + fallback.ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool summaryChanged = !string.Equals(lastSummarySignature, summarySignature, StringComparison.Ordinal);
        bool heartbeatDue = lastSummaryLogAtUtc == DateTimeOffset.MinValue || now - lastSummaryLogAtUtc >= SummaryHeartbeat;
        if (total > 0 && (summaryChanged || heartbeatDue))
        {
            lastSummarySignature = summarySignature;
            lastSummaryLogAtUtc = now;
            VanguardClientDiagnosticsLog.Info(
                VanguardOperatorBotTypes.StatusTag,
                $"VANGUARD_OPERATOR_ROLE_SUMMARY total={total}; custom={custom}; fallback={fallback}; cadence={PollInterval.TotalSeconds:0}; heartbeat={SummaryHeartbeat.TotalSeconds:0}; transitionOrHeartbeat=true; expectedSubstring={VanguardOperatorBotTypes.RoleSubstring}; usec={VanguardOperatorBotTypes.UsecRoleName}:{VanguardOperatorBotTypes.UsecRoleValue}; bear={VanguardOperatorBotTypes.BearRoleName}:{VanguardOperatorBotTypes.BearRoleValue}; tag={VanguardOperatorBotTypes.StatusTag}");
        }
    }

    public static void Reset(string reason)
    {
        LastRoleByBotProfileId.Clear();
        nextSummaryAtUtc = DateTimeOffset.MinValue;
        lastSummaryLogAtUtc = DateTimeOffset.MinValue;
        lastSummarySignature = string.Empty;
        VanguardClientDiagnosticsLog.Info(
            VanguardOperatorBotTypes.StatusTag,
            $"VANGUARD_OPERATOR_ROLE_DIAGNOSTICS_RESET reason={Safe(reason)}; tag={VanguardOperatorBotTypes.StatusTag}");
    }

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
