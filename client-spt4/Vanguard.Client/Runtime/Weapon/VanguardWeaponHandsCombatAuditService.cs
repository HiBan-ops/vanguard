#if SPT_CLIENT
using System;
using System.Collections.Generic;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.PostLoot;

// Responsibility: Coordinates Weapon Hands Combat Audit Service for the weapon-readiness runtime, delegating specialized work to its collaborators.
// Flow: Current raid/runtime evidence is normalized, applicable guards and ownership rules are evaluated, then the service updates only its bounded runtime/UI responsibility.
// Authority boundary: Service coordinates its domain but does not fabricate server persistence truth or bypass higher-priority runtime authorities.
// Invariant: State is lifecycle-scoped, stale work is releasable, and failures degrade without leaving hidden long-lived ownership.
namespace Vanguard.Client.Runtime.Weapon;

/// <summary>
/// The runtime read-only audit for the visual/runtime case observed on Vector:
/// SAIN/combat pressure is active but the Operator appears to have no weapon in hand.
/// This service does not recover or mutate hands/weapon state; it only logs compact
/// suspect snapshots so the next pass can decide a safe recovery strategy.
/// </summary>
internal static class VanguardWeaponHandsCombatAuditService
{
    public const string StatusTag = "VANGUARD_WEAPON_HANDS_AUDIT_OK";
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1.00d);
    private static readonly TimeSpan SuspectLogInterval = TimeSpan.FromSeconds(5.00d);
    private static readonly Dictionary<string, DateTimeOffset> LastSuspectLogAtByKey = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset nextTickUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    public static void ResetForRaidLifecycle(string reason)
    {
        nextTickUtc = DateTimeOffset.MinValue;
        LastSuspectLogAtByKey.Clear();
        bootLogged = false;
        VanguardClientDiagnosticsLog.Info(StatusTag, $"weapon hands combat audit reset reason={Safe(reason)}; readOnly=true; mutatesWeapon=false; mutatesHands=false; mutatesSain=false");
    }

    public static void Tick()
    {
        if (!VanguardOperatorRuntimeAuditLoadGuard.IsOpen() || !VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < nextTickUtc)
        {
            return;
        }

        nextTickUtc = now + TickInterval;
        LogBootOnce();

        foreach (var record in VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators())
        {
            if (record.BotOwner == null || string.IsNullOrWhiteSpace(record.BotProfileId) || record.BotOwner.IsDead)
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
                VanguardClientDiagnosticsLog.Warning(StatusTag, $"weapon hands combat audit failed operator={Safe(record.OperatorId)}; botProfile={Safe(record.BotProfileId)}; reason={exception.GetType().Name}:{Safe(exception.Message)}");
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
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_WEAPON_HANDS_COMBAT_AUDIT_BOOT enabled=true; readOnly=true; detects=combat_without_firearm_hands; mutatesWeapon=false; mutatesHands=false; mutatesSain=false; build={VanguardBuildVersion.BuildLabel}; tag={StatusTag}");
    }

    private static void Audit(EFT.BotOwner botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        bool combatContext = IsCombatContext(snapshot);
        bool enemyPressure = HasEnemyPressure(snapshot);
        bool medicalBusy = snapshot.Medical.Actionability.AnyMedicineUsing
            || snapshot.Medical.Actionability.FirstAidUsing
            || snapshot.Medical.Actionability.SurgicalKitUsing
            || snapshot.Medical.Actionability.StimulatorUsing;

        if (!combatContext || !enemyPressure || medicalBusy)
        {
            return;
        }

        var weapon = VanguardPostLootWeaponReadinessReader.Capture(botOwner);
        if (weapon.WeaponReady)
        {
            return;
        }

        string key = snapshot.BotProfileId + "|" + weapon.Signature + "|" + snapshot.Sain.Classification + "|" + snapshot.Threat.Classification;
        if (LastSuspectLogAtByKey.TryGetValue(key, out var last) && now - last < SuspectLogInterval)
        {
            return;
        }

        LastSuspectLogAtByKey[key] = now;
        VanguardClientDiagnosticsLog.Warning(StatusTag, $"VANGUARD_WEAPON_HANDS_STATE_SUSPECT operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; nick={Safe(snapshot.Nickname)}; combat={Bool(combatContext)}; enemyPressure={Bool(enemyPressure)}; medicalBusy={Bool(medicalBusy)}; sain={Safe(snapshot.Sain.Classification)}; sainAction={Safe(snapshot.Sain.CurrentAction)}; threat={Safe(snapshot.Threat.Classification)}; enemyVisible={Tri(snapshot.Threat.EnemyVisible)}; enemyCanShoot={Tri(snapshot.Threat.EnemyCanShoot)}; enemyDist={Float(snapshot.Threat.Distance)}; {weapon.Summary}; recovery=readonly_future; tag={StatusTag}");
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
            || IsClose(snapshot.Threat.Distance, 60f)
            || IsClose(snapshot.ThreatScan.CandidateDistance, 60f)
            || snapshot.Threat.ShotMeRecently == true
            || snapshot.Threat.ShotAtMeRecently == true
            || snapshot.ThreatScan.CandidateIncomingFireFresh;
    }

    private static bool IsClose(float? distance, float max) => distance.HasValue && distance.Value >= 0f && distance.Value <= max;
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Tri(bool? value) => value.HasValue ? Bool(value.Value) : "unknown";
    private static string Float(float? value) => value.HasValue ? value.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "unknown";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
