#if SPT_CLIENT
using System;
using System.Collections;
using System.Collections.Generic;
using EFT;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Integrations.MoreBots;

// Responsibility: Coordinates Orbit Authority Boundary Service for the external AI integration, delegating specialized work to its collaborators.
// Flow: Current raid/runtime evidence is normalized, applicable guards and ownership rules are evaluated, then the service updates only its bounded runtime/UI responsibility.
// Authority boundary: Service coordinates its domain but does not fabricate server persistence truth or bypass higher-priority runtime authorities.
// Invariant: State is lifecycle-scoped, stale work is releasable, and failures degrade without leaving hidden long-lived ownership.
namespace Vanguard.Client.Runtime.Integrations.Orbit;

/// <summary>
/// The runtime information-only ORBIT boundary cache. Operators are excluded at OrbitBrainLayer construction;
/// this service verifies that invariant once, then replaces repeated roster/component/telemetry reflection
/// with a bounded audit. Any observed Agent, handler or objective immediately re-opens the legacy preempt path.
/// </summary>
internal static class VanguardOrbitAuthorityBoundaryService
{
    public const string StatusTag = "VANGUARD_ORBIT_BOUNDARY_BUDGET_STATUS";

    private sealed class State
    {
        public bool ExclusionConfirmed;
        public DateTimeOffset NextAuditAtUtc = DateTimeOffset.MinValue;
        public string LastReason = "not_audited";
        public bool ConfirmationLogged;
        public DateTimeOffset LastAnomalyLogAtUtc = DateTimeOffset.MinValue;
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, State> ByBotProfile = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ConfirmedAuditInterval = TimeSpan.FromSeconds(45d);
    private static readonly TimeSpan AnomalyAuditInterval = TimeSpan.FromSeconds(3d);
    private static readonly TimeSpan AnomalyLogInterval = TimeSpan.FromSeconds(5d);

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync) ByBotProfile.Clear();
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_ORBIT_BOUNDARY_CACHE_RESET reason={Safe(reason)}; expected=role_excluded_before_agent_attach; auditInterval={ConfirmedAuditInterval.TotalSeconds:0}; mutation=false; tag={StatusTag}");
    }

    public static bool TryGetExcludedSnapshot(BotOwner? botOwner, string botProfileId, DateTimeOffset now, out VanguardOrbitDecisionSnapshot snapshot)
    {
        snapshot = null!;
        if (!TryConfirmExcluded(botOwner, botProfileId, now, forceAudit: false, out string reason))
        {
            return false;
        }

        snapshot = new VanguardOrbitDecisionSnapshot
        {
            TelemetryLoaded = VanguardOperatorRuntimeAuditReflection.TypeExists("Orbit.Api.OrbitTelemetry"),
            Available = true,
            Active = false,
            Status = "excluded",
            Category = "vanguard_operator",
            ExtractReason = "none",
            Classification = "orbit_excluded_confirmed:" + Safe(reason)
        };
        return true;
    }

    public static bool ShouldSkipPreempt(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason)
    {
        reason = "not_excluded";
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId)) return false;
        if (snapshot.Orbit.Active
            || Contains(snapshot.Orbit.Classification, "active")
            || Contains(snapshot.Brain.ActiveLayer, "orbit")
            || Contains(snapshot.Sain.ActiveLayer, "orbit"))
        {
            TryConfirmExcluded(botOwner, snapshot.BotProfileId, now, forceAudit: true, out string auditReason);
            reason = "snapshot_orbit_signal:" + auditReason;
            return false;
        }

        return TryConfirmExcluded(botOwner, snapshot.BotProfileId, now, forceAudit: false, out reason);
    }

    public static bool IsFastPathConfirmed(string botProfileId, DateTimeOffset now, out string reason)
    {
        reason = "not_cached";
        lock (Sync)
        {
            if (!ByBotProfile.TryGetValue(botProfileId, out State? state)
                || !state.ExclusionConfirmed
                || now >= state.NextAuditAtUtc)
            {
                return false;
            }
            reason = state.LastReason;
            return true;
        }
    }

    private static bool TryConfirmExcluded(BotOwner? botOwner, string botProfileId, DateTimeOffset now, bool forceAudit, out string reason)
    {
        reason = "not_operator_role";
        if (botOwner == null || string.IsNullOrWhiteSpace(botProfileId) || !IsVanguardRole(botOwner)) return false;
        if (!VanguardOrbitRoleExclusionBootstrap.IsRegistered)
        {
            reason = "role_exclusion_not_registered";
            return false;
        }

        State state;
        lock (Sync)
        {
            if (!ByBotProfile.TryGetValue(botProfileId, out state!))
            {
                state = new State();
                ByBotProfile[botProfileId] = state;
            }
            if (!forceAudit && state.ExclusionConfirmed && now < state.NextAuditAtUtc)
            {
                reason = state.LastReason;
                return true;
            }
        }

        bool agentPresent = TryGetOrbitAgent(botProfileId, out bool agentProbeSucceeded, out string agentReason);
        object? handler = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "Orbit.Looting.OrbitLootHandler");
        bool objectivePresent = TryGetOrbitObjective(botProfileId, out bool objectiveProbeSucceeded, out string objectiveReason);
        // The absence fast path is enabled only after a successful OrbitManager roster probe.
        // A reflection/type/instance failure is uncertainty, never proof that ORBIT is absent.
        bool excluded = agentProbeSucceeded && !agentPresent && handler == null && (!objectiveProbeSucceeded || !objectivePresent);
        string auditReason = excluded
            ? "no_agent_no_handler" + (objectiveProbeSucceeded ? "_no_objective" : "_objective_probe_unavailable")
            : "anomaly:agentProbe=" + Bool(agentProbeSucceeded)
                + ":agent=" + Bool(agentPresent) + ":" + Safe(agentReason)
                + ":handler=" + Bool(handler != null)
                + ":objectiveProbe=" + Bool(objectiveProbeSucceeded)
                + ":objective=" + Bool(objectivePresent) + ":" + Safe(objectiveReason);

        lock (Sync)
        {
            state.ExclusionConfirmed = excluded;
            state.LastReason = auditReason;
            state.NextAuditAtUtc = now + (excluded ? ConfirmedAuditInterval : AnomalyAuditInterval);
        }

        if (excluded && !state.ConfirmationLogged)
        {
            state.ConfirmationLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_ORBIT_EXCLUDED_CONFIRMED botProfile={Safe(botProfileId)}; role={Safe(ReadRole(botOwner))}; agentPresent=false; handlerPresent=false; objectivePresent=false; nextAudit={ConfirmedAuditInterval.TotalSeconds:0}; repeatedPreempt=false; mutation=false; tag={StatusTag}");
        }
        else if (!excluded && (state.LastAnomalyLogAtUtc == DateTimeOffset.MinValue || now - state.LastAnomalyLogAtUtc >= AnomalyLogInterval))
        {
            state.LastAnomalyLogAtUtc = now;
            VanguardClientDiagnosticsLog.Warning(StatusTag,
                $"VANGUARD_ORBIT_BOUNDARY_ANOMALY botProfile={Safe(botProfileId)}; role={Safe(ReadRole(botOwner))}; {auditReason}; action=allow_legacy_preempt; nextAudit={AnomalyAuditInterval.TotalSeconds:0}; tag={StatusTag}");
        }

        reason = auditReason;
        return excluded;
    }

    private static bool IsVanguardRole(BotOwner botOwner)
    {
        return ReadRole(botOwner).IndexOf(VanguardOperatorBotTypes.RoleSubstring, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string ReadRole(BotOwner botOwner)
    {
        try { return botOwner.Profile?.Info?.Settings?.Role.ToString() ?? "none"; }
        catch { return VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "Profile", "Info", "Settings", "Role")?.ToString() ?? "none"; }
    }

    private static bool TryGetOrbitObjective(string botProfileId, out bool probeSucceeded, out string reason)
    {
        probeSucceeded = false;
        reason = "telemetry_missing";
        Type? telemetry = VanguardOperatorRuntimeAuditReflection.FindType("Orbit.Api.OrbitTelemetry");
        if (telemetry == null) return false;
        bool available = VanguardOperatorRuntimeAuditReflection.GetStaticMember(telemetry, "IsAvailable") is bool value && value;
        if (!available)
        {
            reason = "telemetry_unavailable";
            return false;
        }
        object? objective = VanguardOperatorRuntimeAuditReflection.InvokeStatic(telemetry, "GetBotObjective", botProfileId);
        probeSucceeded = true;
        reason = objective == null ? "no_objective" : "objective_present";
        return objective != null;
    }

    private static bool TryGetOrbitAgent(string botProfileId, out bool probeSucceeded, out string reason)
    {
        probeSucceeded = false;
        reason = "manager_missing";
        try
        {
            Type? managerType = VanguardOperatorRuntimeAuditReflection.FindType("Orbit.Core.OrbitManager");
            Type? singletonType = VanguardOperatorRuntimeAuditReflection.FindType("Comfort.Common.Singleton`1");
            if (managerType == null || singletonType == null) return false;
            Type closed = singletonType.MakeGenericType(managerType);
            object? manager = VanguardOperatorRuntimeAuditReflection.GetStaticMember(closed, "Instance");
            if (manager == null)
            {
                reason = "manager_instance_missing";
                return false;
            }
            object? values = VanguardOperatorRuntimeAuditReflection.GetDeep(manager, "AgentData", "Entities", "Values");
            if (values is not IEnumerable enumerable)
            {
                reason = "agent_values_missing";
                return false;
            }

            foreach (object? candidate in enumerable)
            {
                string profile = VanguardOperatorRuntimeAuditReflection.GetDeep(candidate, "Player", "ProfileId")?.ToString() ?? string.Empty;
                if (string.Equals(profile, botProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    probeSucceeded = true;
                    reason = "agent_by_player_profile";
                    return true;
                }
                profile = VanguardOperatorRuntimeAuditReflection.GetDeep(candidate, "Bot", "Profile", "Id")?.ToString() ?? string.Empty;
                if (string.Equals(profile, botProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    probeSucceeded = true;
                    reason = "agent_by_bot_profile";
                    return true;
                }
            }
            probeSucceeded = true;
            reason = "agent_not_found";
            return false;
        }
        catch (Exception exception)
        {
            reason = "agent_probe_exception:" + exception.GetType().Name;
            return false;
        }
    }

    private static bool Contains(string? value, string token) => !string.IsNullOrWhiteSpace(value) && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
