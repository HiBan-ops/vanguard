#if SPT_CLIENT
using System;
using System.Reflection;
using EFT;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Coordinates Post Loot Stale Loot Recovery Service for the post-loot recovery runtime, delegating specialized work to its collaborators.
// Flow: Current raid/runtime evidence is normalized, applicable guards and ownership rules are evaluated, then the service updates only its bounded runtime/UI responsibility.
// Authority boundary: Service coordinates its domain but does not fabricate server persistence truth or bypass higher-priority runtime authorities.
// Invariant: State is lifecycle-scoped, stale work is releasable, and failures degrade without leaving hidden long-lived ownership.
namespace Vanguard.Client.Runtime.PostLoot;

internal static class VanguardPostLootStaleLootRecoveryService
{
    public const string StatusTag = "VANGUARD_POST_LOOT_STALE_RECOVERY_OK";
    private static readonly TimeSpan StaleBeforeRecovery = TimeSpan.FromSeconds(3.0d);
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromSeconds(15.0d);

    public static bool TryRecover(
        BotOwner botOwner,
        OperatorDecisionSnapshot snapshot,
        VanguardPostLootEpisodeState state,
        VanguardPostLootWeaponReadinessSnapshot weapon,
        bool lootStateStale,
        bool combatContext,
        DateTimeOffset now,
        out string reason)
    {
        reason = "not_needed";
        if (!IsRecoveryCandidate(snapshot, state, weapon, lootStateStale, combatContext, now, out reason))
        {
            return false;
        }

        object? lootingBrain = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "LootingBots.Components.LootingBrain");
        if (lootingBrain == null)
        {
            reason = "looting_brain_component_missing";
            return false;
        }

        bool stopped = InvokeNoArg(lootingBrain, "StopLooting");
        bool cleaned = Invoke(lootingBrain, "CleanupLoot", false, true) || InvokeNoArg(lootingBrain, "Cleanup");
        _ = InvokeNoArg(lootingBrain, "UpdateGridStats");

        state.StaleLootRecoveryAttempted = true;
        state.LastRecoveryAttemptAtUtc = now;
        reason = cleaned ? "stale_loot_cleanup_invoked" : "stale_loot_cleanup_not_available";
        VanguardClientDiagnosticsLog.Warning(
            StatusTag,
            $"VANGUARD_POST_LOOT_STALE_LOOT_RECOVERY operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; reason={reason}; stopped={Bool(stopped)}; cleaned={Bool(cleaned)}; orbitActive={Bool(snapshot.Orbit.Active)}; lootBrain={Tri(snapshot.Looting.BotLooting)}; lootTask={Tri(snapshot.Looting.LootTaskRunning)}; activeLootable={Tri(snapshot.Looting.HasActiveLootable)}; lootType={Safe(snapshot.Looting.ActiveLootType)}; combat={Bool(combatContext)}; {weapon.Summary}; mutatesLoot=true; mutatesWeapon=false; mutatesSain=false");

        return cleaned;
    }

    private static bool IsRecoveryCandidate(
        OperatorDecisionSnapshot snapshot,
        VanguardPostLootEpisodeState state,
        VanguardPostLootWeaponReadinessSnapshot weapon,
        bool lootStateStale,
        bool combatContext,
        DateTimeOffset now,
        out string reason)
    {
        if (!lootStateStale)
        {
            reason = "loot_state_not_stale";
            return false;
        }

        if (!combatContext)
        {
            reason = "not_in_combat";
            return false;
        }

        if (snapshot.Orbit.Active)
        {
            reason = "orbit_still_active";
            return false;
        }

        if (snapshot.Looting.LootTaskRunning == true)
        {
            reason = "real_loot_task_running";
            return false;
        }

        if (!weapon.WeaponReady)
        {
            reason = "weapon_not_ready_readonly_only";
            return false;
        }

        if (weapon.FirstAidUsing || snapshot.Medical.Actionability.AnyMedicineUsing)
        {
            reason = "medicine_busy";
            return false;
        }

        if (state.LootStateStaleSinceUtc == DateTimeOffset.MinValue || now - state.LootStateStaleSinceUtc < StaleBeforeRecovery)
        {
            reason = "stale_window_not_mature";
            return false;
        }

        if (state.StaleLootRecoveryAttempted && now - state.LastRecoveryAttemptAtUtc < RecoveryCooldown)
        {
            reason = "recovery_cooldown";
            return false;
        }

        reason = "candidate";
        return true;
    }

    private static bool InvokeNoArg(object instance, string name)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var method = instance.GetType().GetMethod(name, flags, null, Type.EmptyTypes, null);
            if (method == null)
            {
                return false;
            }

            method.Invoke(instance, Array.Empty<object>());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool Invoke(object instance, string name, params object?[] args)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var method in instance.GetType().GetMethods(flags))
            {
                if (!string.Equals(method.Name, name, StringComparison.Ordinal))
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != args.Length)
                {
                    continue;
                }

                method.Invoke(instance, args);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Tri(bool? value) => value.HasValue ? Bool(value.Value) : "unknown";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
}
#endif
