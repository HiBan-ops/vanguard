#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Coordinates Stale Sain Exit Service for the external-authority integration, delegating specialized work to its collaborators.
// Flow: Current raid/runtime evidence is normalized, applicable guards and ownership rules are evaluated, then the service updates only its bounded runtime/UI responsibility.
// Authority boundary: Service coordinates its domain but does not fabricate server persistence truth or bypass higher-priority runtime authorities.
// Invariant: State is lifecycle-scoped, stale work is releasable, and failures degrade without leaving hidden long-lived ownership.
namespace Vanguard.Client.Runtime.External;

/// <summary>
/// Vanguard general stale SAIN combat release.
/// This is intentionally not a medical-only bypass.  If SAIN remains in combat/search with no visible,
/// actionable or shooting target for long enough, Vanguard demotes the stale owner so the normal
/// scheduler can choose medical, sector-hold or cohesion without forcing surgery directly.
/// </summary>
internal static class VanguardStaleSainExitService
{
    public const string StatusTag = "VANGUARD_HOSTILE_INDOOR_MOVEMENT_PLAN_STATUS";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, StaleState> States = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogAtByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(0.80d);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(1.50d);
    private static DateTimeOffset nextTickAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            States.Clear();
            LastLogAtByKey.Clear();
        }

        bootLogged = false;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_SAIN_STALE_EXIT_RESET reason={Safe(reason)}; state=cleared; policy=general_not_medical_only; tag={StatusTag}");
    }

    public static void Tick()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now < nextTickAtUtc)
        {
            return;
        }

        nextTickAtUtc = now + TickInterval;
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_SAIN_STALE_EXIT_BOOT enabled=true; staleSeconds={VanguardMovementAuthorityDoctrine.StaleSainExitNoActionSeconds:0.00}; policy=observe_then_scheduler_owned_close; neverForceSurgery=true; tag={StatusTag}");
        }

        var snapshots = VanguardOperatorDecisionSnapshotService.GetLatestSnapshots();
        if (snapshots == null || snapshots.Count == 0)
        {
            return;
        }

        var liveIds = new HashSet<string>(snapshots.Where(s => s != null && !string.IsNullOrWhiteSpace(s.BotProfileId)).Select(s => s.BotProfileId), StringComparer.OrdinalIgnoreCase);
        lock (Sync)
        {
            foreach (string stale in States.Keys.Where(key => !liveIds.Contains(key)).ToArray())
            {
                States.Remove(stale);
            }
        }

        foreach (var snapshot in snapshots)
        {
            if (snapshot == null || !snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            {
                continue;
            }

            if (!VanguardMovementAuthorityDoctrine.IsSainCombatStaleNonActionable(snapshot, out var staleReason))
            {
                Clear(snapshot.BotProfileId, now, staleReason);
                continue;
            }

            StaleState state;
            bool newlyObserved = false;
            lock (Sync)
            {
                if (!States.TryGetValue(snapshot.BotProfileId, out state))
                {
                    state = new StaleState
                    {
                        BotProfileId = snapshot.BotProfileId,
                        OperatorId = snapshot.OperatorId,
                        FirstSeenAtUtc = now,
                        LastAppliedAtUtc = DateTimeOffset.MinValue,
                        LastReason = staleReason,
                    };
                    newlyObserved = true;
                }

                state.LastSeenAtUtc = now;
                state.LastReason = staleReason;
                state.OperatorId = snapshot.OperatorId;
                States[snapshot.BotProfileId] = state;
            }

            double age = Math.Max(0.0d, (now - state.FirstSeenAtUtc).TotalSeconds);
            if (newlyObserved)
            {
                LogThrottled("staleObserve|" + snapshot.BotProfileId, now,
                    $"VANGUARD_SAIN_STALE_EXIT_OBSERVED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(staleReason)}; age={age:0.00}; action=wait_confirm_window; tag={StatusTag}");
                continue;
            }

            if (age < VanguardMovementAuthorityDoctrine.StaleSainExitNoActionSeconds)
            {
                LogThrottled("staleConfirm|" + snapshot.BotProfileId, now,
                    $"VANGUARD_SAIN_STALE_EXIT_CONFIRMING operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(staleReason)}; age={age:0.00}; required={VanguardMovementAuthorityDoctrine.StaleSainExitNoActionSeconds:0.00}; tag={StatusTag}");
                continue;
            }

            if ((now - state.LastAppliedAtUtc).TotalSeconds < 2.50d)
            {
                continue;
            }

            bool protectedCombat = VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out var protectedReason);
            state.LastAppliedAtUtc = now;
            lock (Sync)
            {
                States[snapshot.BotProfileId] = state;
            }

            LogThrottled("staleObservedScheduler|" + snapshot.BotProfileId + "|" + staleReason, now,
                () => $"VANGUARD_STALE_SAIN_OBSERVED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; age={age:0.00}; staleReason={Safe(staleReason)}; protectedCombat={Bool(protectedCombat)}; protectedReason={Safe(protectedReason)}; mutation=false; next=scheduler_no_progress_or_hard_deadline; forcedSurgery=false; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; legacyTag={StatusTag}");
        }
    }

    private static void Clear(string botProfileId, DateTimeOffset now, string reason)
    {
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return;
        }

        bool had;
        lock (Sync)
        {
            had = States.Remove(botProfileId);
        }

        if (had)
        {
            LogThrottled("staleClear|" + botProfileId, now,
                $"VANGUARD_SAIN_STALE_EXIT_CLEARED botProfile={Safe(botProfileId)}; reason={Safe(reason)}; tag={StatusTag}");
        }
    }

    private static void LogThrottled(string key, DateTimeOffset now, Func<string> messageFactory)
    {
        if (!VanguardClientDiagnosticsLog.IsEnabled(VanguardAuditLevel.Trace))
        {
            return;
        }

        lock (Sync)
        {
            if (LastLogAtByKey.TryGetValue(key, out var last) && now - last < LogInterval)
            {
                return;
            }

            LastLogAtByKey[key] = now;
        }

        VanguardClientDiagnosticsLog.Trace(StatusTag, messageFactory);
    }

    private static void LogThrottled(string key, DateTimeOffset now, string message)
    {
        lock (Sync)
        {
            if (LastLogAtByKey.TryGetValue(key, out var last) && now - last < LogInterval)
            {
                return;
            }

            LastLogAtByKey[key] = now;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag, message);
    }

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private struct StaleState
    {
        public string BotProfileId;
        public string OperatorId;
        public DateTimeOffset FirstSeenAtUtc;
        public DateTimeOffset LastSeenAtUtc;
        public DateTimeOffset LastAppliedAtUtc;
        public string LastReason;
    }
}
#endif
