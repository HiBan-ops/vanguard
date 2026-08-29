#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EFT;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Observes whether an Operator returns to normal combat readiness after a loot episode and flags stale loot ownership without becoming another recovery executor.
// Flow: A completed/interrupted loot episode opens a short observation window; subsequent weapon, hands, target and authority facts are compared until readiness is confirmed or an anomaly is reported.
// Authority boundary: Audit is read-only except for explicitly guarded stale-loot cleanup already owned by the loot recovery contract; it never equips weapons or commands SAIN directly.
// Invariant: Audit state expires with the raid/window and cannot hold movement, combat, loot or hands authority merely because readiness was not observed.
namespace Vanguard.Client.Runtime.PostLoot;

internal static class VanguardPostLootCombatReadinessAuditService
{
    public const string StatusTag = "VANGUARD_POST_LOOT_COMBAT_READINESS_AUDIT_OK";
    private static readonly TimeSpan RecentPostLootWindow = TimeSpan.FromSeconds(25.0d);
    private static readonly Dictionary<string, VanguardPostLootEpisodeState> States = new(StringComparer.OrdinalIgnoreCase);
    private static bool bootLogged;

    public static void ResetForRaidLifecycle(string reason)
    {
        States.Clear();
        bootLogged = false;
        VanguardClientDiagnosticsLog.Info(StatusTag, $"post-loot combat readiness audit reset reason={reason}; readOnlyAudit=true; staleLootRecovery=true; mutatesLoot=guarded; mutatesWeapon=false; mutatesSain=false");
    }

    public static void Tick()
    {
        if (!VanguardOperatorRuntimeAuditLoadGuard.IsOpen() || !VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }

        if (!VanguardOperatorRuntimeAuditSyncService.EffectiveEnabled && !VanguardOperatorRuntimeAuditOptions.GetFirstActiveMobileMedicalLeaseEnabled())
        {
            return;
        }

        LogBootOnce();
        var now = DateTimeOffset.UtcNow;
        foreach (var record in VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators())
        {
            if (record.BotOwner == null || string.IsNullOrWhiteSpace(record.BotProfileId))
            {
                continue;
            }

            if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(record.BotProfileId, out var snapshot))
            {
                continue;
            }

            try
            {
                Audit(record.BotOwner, snapshot, now);
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(StatusTag, $"post-loot combat readiness audit failed operator={record.OperatorId}; botProfile={record.BotProfileId}; reason={exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private static void LogBootOnce()
    {
        if (bootLogged)
        {
            return;
        }

        bootLogged = true;
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_POST_LOOT_COMBAT_READINESS_BOOT enabled=true; readOnlyAudit=true; staleLootRecovery=true; detects=post_loot_weapon_hands_loot_stale_sain_no_fire_suspect; recovery=stale_loot_only; weaponRecovery=false; build={VanguardBuildVersion.BuildLabel}; recoveryTag={VanguardPostLootStaleLootRecoveryService.StatusTag}");
    }

    private static void Audit(BotOwner botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        var state = GetState(snapshot.BotProfileId);
        bool lootActive = IsLootActive(snapshot);
        UpdateEpisodeState(state, lootActive, now);

        bool recentPostLoot = IsRecent(now, state.LastLootActiveAtUtc) || IsRecent(now, state.LastLootEndedAtUtc);
        if (!recentPostLoot && !lootActive)
        {
            ResetStaleState(state);
            return;
        }

        var weapon = VanguardPostLootWeaponReadinessReader.Capture(botOwner);
        bool combatContext = IsCombatContext(snapshot);
        bool enemyPressure = HasEnemyPressure(snapshot);
        bool lootStateStale = recentPostLoot && combatContext && lootActive && !snapshot.Orbit.Active;
        bool weaponSuspect = recentPostLoot && combatContext && enemyPressure && !weapon.WeaponReady;
        bool noFireSuspect = recentPostLoot && combatContext && enemyPressure && (weaponSuspect || lootStateStale || snapshot.Medical.Actionability.FirstAidUsing || snapshot.Medical.Actionability.AnyMedicineUsing);

        UpdateStaleState(state, lootStateStale, now);
        LogSnapshotIfUseful(state, snapshot, weapon, now, recentPostLoot, lootActive, combatContext, enemyPressure, lootStateStale, weaponSuspect, noFireSuspect);
        LogSuspects(state, snapshot, weapon, now, recentPostLoot, combatContext, enemyPressure, lootStateStale, weaponSuspect, noFireSuspect);
        TryRecoverStaleLoot(botOwner, snapshot, state, weapon, lootStateStale, combatContext, now);
    }

    private static void UpdateEpisodeState(VanguardPostLootEpisodeState state, bool lootActive, DateTimeOffset now)
    {
        if (lootActive)
        {
            state.LastLootActiveAtUtc = now;
        }

        if (state.WasLootActive && !lootActive)
        {
            state.LastLootEndedAtUtc = now;
        }

        state.WasLootActive = lootActive;
    }

    private static void UpdateStaleState(VanguardPostLootEpisodeState state, bool lootStateStale, DateTimeOffset now)
    {
        if (lootStateStale)
        {
            if (state.LootStateStaleSinceUtc == DateTimeOffset.MinValue)
            {
                state.LootStateStaleSinceUtc = now;
            }
            return;
        }

        ResetStaleState(state);
    }

    private static void ResetStaleState(VanguardPostLootEpisodeState state)
    {
        state.LootStateStaleSinceUtc = DateTimeOffset.MinValue;
        state.StaleLootRecoveryAttempted = false;
    }

    private static void LogSnapshotIfUseful(
        VanguardPostLootEpisodeState state,
        OperatorDecisionSnapshot snapshot,
        VanguardPostLootWeaponReadinessSnapshot weapon,
        DateTimeOffset now,
        bool recentPostLoot,
        bool lootActive,
        bool combatContext,
        bool enemyPressure,
        bool lootStateStale,
        bool weaponSuspect,
        bool noFireSuspect)
    {
        if (!combatContext && !enemyPressure && !lootStateStale && !weaponSuspect && !noFireSuspect)
        {
            return;
        }

        string signature = SnapshotSignature(snapshot, lootActive, combatContext, enemyPressure, lootStateStale, weaponSuspect, noFireSuspect, weapon);
        if (!VanguardPostLootReadinessLogGate.ShouldLogSnapshot(state, now, signature, forced: false))
        {
            return;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_POST_LOOT_COMBAT_READINESS_SNAPSHOT operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; nick={snapshot.Nickname}; recentPostLoot={Bool(recentPostLoot)}; lootActive={Bool(lootActive)}; lootBrain={Tri(snapshot.Looting.BotLooting)}; lootTask={Tri(snapshot.Looting.LootTaskRunning)}; activeLootable={Tri(snapshot.Looting.HasActiveLootable)}; lootType={Safe(snapshot.Looting.ActiveLootType)}; orbitActive={Bool(snapshot.Orbit.Active)}; combat={Bool(combatContext)}; enemyPressure={Bool(enemyPressure)}; sain={Safe(snapshot.Sain.Classification)}; sainAction={Safe(snapshot.Sain.CurrentAction)}; threat={Safe(snapshot.Threat.Classification)}; enemyVisible={Tri(snapshot.Threat.EnemyVisible)}; enemyCanShoot={Tri(snapshot.Threat.EnemyCanShoot)}; enemyDist={Float(snapshot.Threat.Distance)}; {weapon.Summary}; lootStateStale={Bool(lootStateStale)}; weaponSuspect={Bool(weaponSuspect)}; noFireSuspect={Bool(noFireSuspect)}; staleSince={Age(now, state.LootStateStaleSinceUtc)}; recoveryAttempted={Bool(state.StaleLootRecoveryAttempted)}");
    }

    private static void LogSuspects(
        VanguardPostLootEpisodeState state,
        OperatorDecisionSnapshot snapshot,
        VanguardPostLootWeaponReadinessSnapshot weapon,
        DateTimeOffset now,
        bool recentPostLoot,
        bool combatContext,
        bool enemyPressure,
        bool lootStateStale,
        bool weaponSuspect,
        bool noFireSuspect)
    {
        if (lootStateStale && VanguardPostLootReadinessLogGate.ShouldLogSuspect(state, now, "loot_stale"))
        {
            VanguardClientDiagnosticsLog.Warning(StatusTag, $"VANGUARD_POST_LOOT_LOOT_STATE_STALE operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; orbitActive={Bool(snapshot.Orbit.Active)}; lootBrain={Tri(snapshot.Looting.BotLooting)}; lootTask={Tri(snapshot.Looting.LootTaskRunning)}; activeLootable={Tri(snapshot.Looting.HasActiveLootable)}; lootType={Safe(snapshot.Looting.ActiveLootType)}; combat={Bool(combatContext)}; enemyPressure={Bool(enemyPressure)}; staleSince={Age(now, state.LootStateStaleSinceUtc)}; recoveryCandidate=true");
        }

        if (weaponSuspect && VanguardPostLootReadinessLogGate.ShouldLogSuspect(state, now, "weapon_suspect|" + weapon.Signature))
        {
            VanguardClientDiagnosticsLog.Warning(StatusTag, $"VANGUARD_POST_LOOT_WEAPON_STATE_SUSPECT operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; recentPostLoot={Bool(recentPostLoot)}; combat={Bool(combatContext)}; enemyPressure={Bool(enemyPressure)}; {weapon.Summary}; recovery=readonly_weapon_future");
        }

        if (noFireSuspect && VanguardPostLootReadinessLogGate.ShouldLogSuspect(state, now, "sain_no_fire|" + weapon.Signature + "|" + lootStateStale))
        {
            VanguardClientDiagnosticsLog.Warning(StatusTag, $"VANGUARD_POST_LOOT_SAIN_NO_FIRE_SUSPECT operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; recentPostLoot={Bool(recentPostLoot)}; combat={Bool(combatContext)}; enemyPressure={Bool(enemyPressure)}; sain={Safe(snapshot.Sain.Classification)}; sainAction={Safe(snapshot.Sain.CurrentAction)}; enemyVisible={Tri(snapshot.Threat.EnemyVisible)}; enemyCanShoot={Tri(snapshot.Threat.EnemyCanShoot)}; enemyDist={Float(snapshot.Threat.Distance)}; lootStateStale={Bool(lootStateStale)}; weaponSuspect={Bool(weaponSuspect)}; medicalBusy={Bool(snapshot.Medical.Actionability.AnyMedicineUsing)}; fireProgress=unknown; recoveryCandidate=stale_loot_only_when_safe");
        }
    }

    private static void TryRecoverStaleLoot(BotOwner botOwner, OperatorDecisionSnapshot snapshot, VanguardPostLootEpisodeState state, VanguardPostLootWeaponReadinessSnapshot weapon, bool lootStateStale, bool combatContext, DateTimeOffset now)
    {
        if (!VanguardPostLootStaleLootRecoveryService.TryRecover(botOwner, snapshot, state, weapon, lootStateStale, combatContext, now, out var reason))
        {
            if (lootStateStale && VanguardPostLootReadinessLogGate.ShouldLogSuspect(state, now, "stale_recovery_skip|" + reason))
            {
                VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_POST_LOOT_STALE_LOOT_RECOVERY_SKIP operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; reason={reason}; staleSince={Age(now, state.LootStateStaleSinceUtc)}; mutatesLoot=false");
            }
        }
    }

    private static VanguardPostLootEpisodeState GetState(string botProfileId)
    {
        if (!States.TryGetValue(botProfileId, out var state))
        {
            state = new VanguardPostLootEpisodeState();
            States[botProfileId] = state;
        }

        return state;
    }

    private static string SnapshotSignature(OperatorDecisionSnapshot snapshot, bool lootActive, bool combatContext, bool enemyPressure, bool lootStateStale, bool weaponSuspect, bool noFireSuspect, VanguardPostLootWeaponReadinessSnapshot weapon)
    {
        return string.Join("|", lootActive, combatContext, enemyPressure, lootStateStale, weaponSuspect, noFireSuspect, snapshot.Looting.Classification, snapshot.Orbit.Classification, snapshot.Sain.Classification, weapon.Signature);
    }

    private static bool IsLootActive(OperatorDecisionSnapshot snapshot)
    {
        return snapshot.Looting.BotLooting == true
            || snapshot.Looting.LootTaskRunning == true
            || snapshot.Looting.HasActiveLootable == true
            || string.Equals(snapshot.Looting.Classification, "loot_active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(snapshot.Orbit.Category, "loot", StringComparison.OrdinalIgnoreCase)
            || snapshot.Orbit.Status.IndexOf("loot", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsCombatContext(OperatorDecisionSnapshot snapshot)
    {
        return snapshot.Sain.IsInCombat == true
            || snapshot.Sain.HasEnemy == true
            || snapshot.Threat.DirectThreat
            || snapshot.Threat.ResidualThreat
            || snapshot.Threat.EnemyVisible == true
            || snapshot.Threat.EnemyCanShoot == true
            || snapshot.ThreatScan.CandidateVisible
            || snapshot.ThreatScan.CandidateCanShoot
            || snapshot.Sain.Classification.IndexOf("combat", StringComparison.OrdinalIgnoreCase) >= 0
            || snapshot.Sain.Classification.IndexOf("enemy", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool HasEnemyPressure(OperatorDecisionSnapshot snapshot)
    {
        return snapshot.Threat.EnemyVisible == true
            || snapshot.Threat.EnemyCanShoot == true
            || snapshot.ThreatScan.CandidateVisible
            || snapshot.ThreatScan.CandidateCanShoot
            || IsClose(snapshot.Threat.Distance, 40f)
            || IsClose(snapshot.ThreatScan.CandidateDistance, 40f)
            || snapshot.Threat.ShotMeRecently == true
            || snapshot.Threat.ShotAtMeRecently == true
            || snapshot.ThreatScan.CandidateIncomingFireFresh;
    }

    private static bool IsRecent(DateTimeOffset now, DateTimeOffset then) => then != DateTimeOffset.MinValue && now - then <= RecentPostLootWindow;
    private static bool IsClose(float? distance, float max) => distance.HasValue && distance.Value >= 0f && distance.Value <= max;
    private static string Age(DateTimeOffset now, DateTimeOffset then) => then == DateTimeOffset.MinValue ? "none" : Math.Max(0d, (now - then).TotalSeconds).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Tri(bool? value) => value.HasValue ? Bool(value.Value) : "unknown";
    private static string Float(float? value) => value.HasValue ? value.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "unknown";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
}
#endif
