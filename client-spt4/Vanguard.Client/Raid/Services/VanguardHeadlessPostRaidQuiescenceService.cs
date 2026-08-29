using System;
using Vanguard.Client.Diagnostics;

// Responsibility: Coordinates Headless Post Raid Quiescence Service for the raid lifecycle services, delegating specialized work to its collaborators.
// Flow: Current raid/runtime evidence is normalized, applicable guards and ownership rules are evaluated, then the service updates only its bounded runtime/UI responsibility.
// Authority boundary: Service coordinates its domain but does not fabricate server persistence truth or bypass higher-priority runtime authorities.
// Invariant: State is lifecycle-scoped, stale work is releasable, and failures degrade without leaving hidden long-lived ownership.
namespace Vanguard.Client.Raid.Services;

/// <summary>
/// Headless-only post-raid lifecycle barrier. It is armed only after the authoritative
/// HeadlessGame.Stop boundary has attempted Vanguard persistence, then suppresses all recurring
/// Vanguard gameplay/update work until the next authoritative raid lifecycle reset.
/// </summary>
internal static class VanguardHeadlessPostRaidQuiescenceService
{
    public const string StatusTag = "VANGUARD_HEADLESS_POSTRAID_QUIESCENCE";

    private static readonly object Sync = new();
    private static bool active;

    public static bool IsActive
    {
        get
        {
            lock (Sync)
            {
                return active;
            }
        }
    }

    public static void Begin(string reason)
    {
        bool changed;
        lock (Sync)
        {
            changed = !active;
            active = true;
        }

        if (changed)
        {
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"VANGUARD_HEADLESS_POSTRAID_QUIESCENCE active=true; reason={Safe(reason)}; recurringGameplayTicksSuppressed=true; persistenceAttemptPrecedesBarrier=true");
        }
    }

    public static void ResetForRaidLifecycle(string reason)
    {
        bool wasActive;
        lock (Sync)
        {
            wasActive = active;
            active = false;
        }

        if (wasActive)
        {
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"VANGUARD_HEADLESS_POSTRAID_QUIESCENCE active=false; reason={Safe(reason)}; nextRaidRuntimeAllowed=true");
        }
    }

    private static string Safe(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(';', '_').Replace('\n', ' ').Replace('\r', ' ');
}
