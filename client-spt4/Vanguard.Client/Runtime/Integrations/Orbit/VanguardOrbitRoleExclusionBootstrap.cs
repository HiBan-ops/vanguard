#if SPT_CLIENT
using System;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Integrations.MoreBots;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Provides Orbit Role Exclusion Bootstrap support for the external AI integration.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Integrations.Orbit;

/// <summary>
/// Registers Vanguard Operator role substrings into ORBIT's own opt-out surface.
/// This is deliberately boot-time and idempotent: the desired boundary is exclusion before ORBIT builds an Agent,
/// not repeated runtime muting after an Agent already exists.
/// </summary>
internal static class VanguardOrbitRoleExclusionBootstrap
{
    private static readonly object Sync = new();
    private static DateTimeOffset nextAttemptAtUtc = DateTimeOffset.MinValue;
    private static bool registered;
    private static bool bootLogged;
    private static int attemptCount;
    private static bool absenceBackoffLogged;
    private static bool stoppedBecauseOrbitAbsent;
    private const int MaxMissingTypeAttempts = 24;

    public static bool IsRegistered
    {
        get
        {
            lock (Sync) return registered;
        }
    }

    public static void RegisterOrDefer(string reason)
    {
        lock (Sync)
        {
            if (registered || stoppedBecauseOrbitAbsent)
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now < nextAttemptAtUtc)
            {
                return;
            }

            nextAttemptAtUtc = now + TimeSpan.FromSeconds(1.25d);
            attemptCount++;
        }

        TryRegister(reason);
    }

    public static void Tick()
    {
        if (!registered && !stoppedBecauseOrbitAbsent)
        {
            RegisterOrDefer("tick_until_orbit_type_loaded");
        }
    }

    private static void TryRegister(string reason)
    {
        Type? layerType = VanguardOperatorRuntimeAuditReflection.FindType("Orbit.Brain.OrbitBrainLayer");
        if (layerType == null)
        {
            LogDeferred(reason, "orbit_layer_type_missing");
            MaybeStopAbsentRetries(reason);
            return;
        }

        object? result = VanguardOperatorRuntimeAuditReflection.InvokeStatic(
            layerType,
            "AddExcludedRoleSubstring",
            VanguardOperatorBotTypes.RoleSubstring);

        lock (Sync)
        {
            registered = true;
        }

        VanguardClientDiagnosticsLog.Diagnostic(
            VanguardOperatorBotTypes.OrbitBoundaryStatusTag,
            () => $"VANGUARD_ORBIT_ROLE_EXCLUSION_REGISTERED substring={VanguardOperatorBotTypes.RoleSubstring}; reason={Safe(reason)}; layer={layerType.FullName}; result={Safe(result?.ToString())}; attempts={attemptCount}; boundary=pre_attach_role_exclusion; fallback=legacy_quiesce_only; tag={VanguardOperatorBotTypes.OrbitBoundaryStatusTag}");
    }


    private static void MaybeStopAbsentRetries(string reason)
    {
        lock (Sync)
        {
            if (registered || stoppedBecauseOrbitAbsent || attemptCount < MaxMissingTypeAttempts)
            {
                return;
            }

            stoppedBecauseOrbitAbsent = true;
        }

        if (!absenceBackoffLogged)
        {
            absenceBackoffLogged = true;
            VanguardClientDiagnosticsLog.Diagnostic(
                VanguardMovementAuthorityDoctrine.OrbitAbsentBackoffStatusTag,
                () => $"VANGUARD_ORBIT_ABSENT_BACKOFF_STATUS substring={VanguardOperatorBotTypes.RoleSubstring}; reason={Safe(reason)}; attempts={attemptCount}; outcome=orbit_layer_missing_stop_retry; boundary=no_orbit_layer_on_this_instance; Tag={VanguardOperatorBotTypes.OrbitBoundaryStatusTag}; tag={VanguardMovementAuthorityDoctrine.OrbitAbsentBackoffStatusTag}");
        }
    }

    private static void LogDeferred(string reason, string detail)
    {
        if (bootLogged && attemptCount % 8 != 0)
        {
            return;
        }

        bootLogged = true;
        VanguardClientDiagnosticsLog.Diagnostic(
            VanguardOperatorBotTypes.OrbitBoundaryStatusTag,
            () => $"VANGUARD_ORBIT_ROLE_EXCLUSION_DEFERRED substring={VanguardOperatorBotTypes.RoleSubstring}; reason={Safe(reason)}; detail={Safe(detail)}; attempts={attemptCount}; boundary=waiting_for_orbit_assembly; tag={VanguardOperatorBotTypes.OrbitBoundaryStatusTag}");
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }
}
#endif
