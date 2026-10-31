#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
/// Read-only runtime audit for the visual/runtime case observed on Vector: SAIN/combat pressure is
/// active but the Operator appears to have no ready weapon in hand. The service never recovers,
/// changes or selects weapons. It tracks the lifetime of each observed suspect episode so runtime
/// evidence can distinguish a transient native weapon swap from a prolonged/unresolved condition.
/// </summary>
internal static class VanguardWeaponHandsCombatAuditService
{
    public const string StatusTag = "VANGUARD_WEAPON_HANDS_AUDIT_OK";
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1.00d);
    private static readonly TimeSpan SuspectLogInterval = TimeSpan.FromSeconds(5.00d);
    private static readonly Dictionary<string, DateTimeOffset> LastSuspectLogAtByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SuspectEpisode> SuspectEpisodeByBot = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset nextTickUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;
    private static long episodesStartedTotal;
    private static long episodesRecoveredTotal;
    private static long episodesCensoredTotal;
    private static double longestRecoveredSeconds;

    public static void ResetForRaidLifecycle(string reason)
    {
        int activeBeforeReset = SuspectEpisodeByBot.Count;
        DateTimeOffset resetNow = DateTimeOffset.UtcNow;
        foreach (string botProfileId in SuspectEpisodeByBot.Keys.ToArray())
        {
            CensorEpisode(botProfileId, resetNow, "raid_lifecycle_reset:" + Safe(reason), "none");
        }

        long startedBeforeReset = episodesStartedTotal;
        long recoveredBeforeReset = episodesRecoveredTotal;
        long censoredBeforeReset = episodesCensoredTotal;
        double longestBeforeReset = longestRecoveredSeconds;

        nextTickUtc = DateTimeOffset.MinValue;
        LastSuspectLogAtByKey.Clear();
        SuspectEpisodeByBot.Clear();
        bootLogged = false;
        episodesStartedTotal = 0;
        episodesRecoveredTotal = 0;
        episodesCensoredTotal = 0;
        longestRecoveredSeconds = 0.0d;

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"weapon hands combat audit reset reason={Safe(reason)}; readOnly=true; mutatesWeapon=false; mutatesHands=false; mutatesSain=false; activeEpisodesBeforeReset={activeBeforeReset}; episodesStartedBeforeReset={startedBeforeReset}; episodesRecoveredBeforeReset={recoveredBeforeReset}; episodesCensoredBeforeReset={censoredBeforeReset}; longestRecoveredSecondsBeforeReset={longestBeforeReset:0.000}");
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
            string botProfileId = Normalize(record.BotProfileId);
            if (record.BotOwner == null || string.Equals(botProfileId, "none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (record.BotOwner.IsDead)
            {
                CensorEpisode(botProfileId, now, "operator_dead", record.OperatorId);
                continue;
            }

            if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(botProfileId, out var snapshot))
            {
                continue;
            }

            try
            {
                Audit(record.BotOwner, snapshot, now);
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(StatusTag,
                    $"weapon hands combat audit failed operator={Safe(record.OperatorId)}; botProfile={Safe(record.BotProfileId)}; reason={exception.GetType().Name}:{Safe(exception.Message)}");
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
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_WEAPON_HANDS_COMBAT_AUDIT_BOOT enabled=true; readOnly=true; detects=combat_without_firearm_hands; tracksEpisodeDuration=true; emitsRecoveryEvidence=true; emitsLifecycleCensorEvidence=true; mutatesWeapon=false; mutatesHands=false; mutatesSain=false; recoveryAction=false; build={VanguardBuildVersion.BuildLabel}; tag={StatusTag}");
    }

    private static void Audit(EFT.BotOwner botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        string botProfileId = Normalize(snapshot.BotProfileId);
        bool combatContext = IsCombatContext(snapshot);
        bool enemyPressure = HasEnemyPressure(snapshot);
        bool medicalBusy = snapshot.Medical.Actionability.AnyMedicineUsing
            || snapshot.Medical.Actionability.FirstAidUsing
            || snapshot.Medical.Actionability.SurgicalKitUsing
            || snapshot.Medical.Actionability.StimulatorUsing;
        bool episodeActive = SuspectEpisodeByBot.TryGetValue(botProfileId, out SuspectEpisode? episode);

        // Outside the combat-pressure admission envelope there is nothing to audit unless a prior
        // suspect episode is still open. Active episodes continue to be sampled read-only so we can
        // observe native recovery even if pressure ends before the weapon transition completes.
        if ((!combatContext || !enemyPressure || medicalBusy) && !episodeActive)
        {
            return;
        }

        VanguardPostLootWeaponReadinessSnapshot weapon = VanguardPostLootWeaponReadinessReader.Capture(botOwner);
        if (weapon.WeaponReady)
        {
            if (episodeActive && episode != null)
            {
                RecoverEpisode(snapshot, episode, weapon, now, combatContext, enemyPressure, medicalBusy);
            }
            return;
        }

        if (!episodeActive || episode == null)
        {
            if (!combatContext || !enemyPressure || medicalBusy)
            {
                return;
            }

            episode = new SuspectEpisode(
                botProfileId,
                now,
                weapon.Signature,
                snapshot.Sain.Classification,
                snapshot.Threat.Classification);
            SuspectEpisodeByBot[botProfileId] = episode;
            episodesStartedTotal++;
        }

        episode.LastObservedUtc = now;
        episode.LastWeaponSignature = weapon.Signature;
        episode.SampleCount++;

        // Repeated warning snapshots remain limited to active combat-pressure observations. The
        // episode itself continues silently outside that envelope until native recovery or lifecycle
        // termination, which avoids manufacturing a timeout/recovery policy.
        if (!combatContext || !enemyPressure || medicalBusy)
        {
            return;
        }

        string key = botProfileId + "|" + weapon.Signature + "|" + snapshot.Sain.Classification + "|" + snapshot.Threat.Classification;
        if (LastSuspectLogAtByKey.TryGetValue(key, out var last) && now - last < SuspectLogInterval)
        {
            return;
        }

        LastSuspectLogAtByKey[key] = now;
        double episodeAgeSeconds = Math.Max(0.0d, (now - episode.StartedAtUtc).TotalSeconds);
        VanguardClientDiagnosticsLog.Warning(StatusTag,
            $"VANGUARD_WEAPON_HANDS_STATE_SUSPECT operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; nick={Safe(snapshot.Nickname)}; combat={Bool(combatContext)}; enemyPressure={Bool(enemyPressure)}; medicalBusy={Bool(medicalBusy)}; sain={Safe(snapshot.Sain.Classification)}; sainAction={Safe(snapshot.Sain.CurrentAction)}; threat={Safe(snapshot.Threat.Classification)}; enemyVisible={Tri(snapshot.Threat.EnemyVisible)}; enemyCanShoot={Tri(snapshot.Threat.EnemyCanShoot)}; enemyDist={Float(snapshot.Threat.Distance)}; episodeAgeSeconds={episodeAgeSeconds:0.000}; episodeSamples={episode.SampleCount}; episodeFirstWeaponSignature={Safe(episode.FirstWeaponSignature)}; {weapon.Summary}; recovery=readonly_observed_only; tag={StatusTag}");
    }

    private static void RecoverEpisode(
        OperatorDecisionSnapshot snapshot,
        SuspectEpisode episode,
        VanguardPostLootWeaponReadinessSnapshot weapon,
        DateTimeOffset now,
        bool combatContext,
        bool enemyPressure,
        bool medicalBusy)
    {
        SuspectEpisodeByBot.Remove(episode.BotProfileId);
        ClearSuspectLogKeys(episode.BotProfileId);
        episodesRecoveredTotal++;
        double durationSeconds = Math.Max(0.0d, (now - episode.StartedAtUtc).TotalSeconds);
        if (durationSeconds > longestRecoveredSeconds)
        {
            longestRecoveredSeconds = durationSeconds;
        }

        VanguardClientDiagnosticsLog.Warning(StatusTag,
            $"VANGUARD_WEAPON_HANDS_STATE_RECOVERED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; nick={Safe(snapshot.Nickname)}; durationSeconds={durationSeconds:0.000}; episodeSamples={episode.SampleCount}; firstWeaponSignature={Safe(episode.FirstWeaponSignature)}; lastSuspectWeaponSignature={Safe(episode.LastWeaponSignature)}; firstSain={Safe(episode.SainClassificationAtStart)}; firstThreat={Safe(episode.ThreatClassificationAtStart)}; combatAtRecovery={Bool(combatContext)}; enemyPressureAtRecovery={Bool(enemyPressure)}; medicalBusyAtRecovery={Bool(medicalBusy)}; weaponReady=true; {weapon.Summary}; recoveryAction=false; nativeStateObserved=true; episodesStartedTotal={episodesStartedTotal}; episodesRecoveredTotal={episodesRecoveredTotal}; episodesCensoredTotal={episodesCensoredTotal}; longestRecoveredSeconds={longestRecoveredSeconds:0.000}; tag={StatusTag}");
    }

    private static void CensorEpisode(string? botProfileId, DateTimeOffset now, string reason, string? operatorId)
    {
        string normalized = Normalize(botProfileId);
        if (!SuspectEpisodeByBot.TryGetValue(normalized, out SuspectEpisode? episode) || episode == null)
        {
            return;
        }

        SuspectEpisodeByBot.Remove(normalized);
        ClearSuspectLogKeys(normalized);
        episodesCensoredTotal++;
        double durationSeconds = Math.Max(0.0d, (now - episode.StartedAtUtc).TotalSeconds);
        double lastObservedAgeSeconds = Math.Max(0.0d, (now - episode.LastObservedUtc).TotalSeconds);
        VanguardClientDiagnosticsLog.Warning(StatusTag,
            $"VANGUARD_WEAPON_HANDS_STATE_CENSORED operator={Safe(operatorId)}; botProfile={Safe(normalized)}; durationSeconds={durationSeconds:0.000}; lastObservedAgeSeconds={lastObservedAgeSeconds:0.000}; episodeSamples={episode.SampleCount}; firstWeaponSignature={Safe(episode.FirstWeaponSignature)}; lastSuspectWeaponSignature={Safe(episode.LastWeaponSignature)}; firstSain={Safe(episode.SainClassificationAtStart)}; firstThreat={Safe(episode.ThreatClassificationAtStart)}; reason={Safe(reason)}; recoveryObserved=false; recoveryAction=false; nativeStateMutation=false; episodesStartedTotal={episodesStartedTotal}; episodesRecoveredTotal={episodesRecoveredTotal}; episodesCensoredTotal={episodesCensoredTotal}; tag={StatusTag}");
    }

    private static void ClearSuspectLogKeys(string botProfileId)
    {
        string prefix = Normalize(botProfileId) + "|";
        foreach (string key in LastSuspectLogAtByKey.Keys.Where(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            LastSuspectLogAtByKey.Remove(key);
        }
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

    private sealed class SuspectEpisode
    {
        public SuspectEpisode(
            string botProfileId,
            DateTimeOffset startedAtUtc,
            string firstWeaponSignature,
            string sainClassification,
            string threatClassification)
        {
            BotProfileId = botProfileId;
            StartedAtUtc = startedAtUtc;
            LastObservedUtc = startedAtUtc;
            FirstWeaponSignature = firstWeaponSignature;
            LastWeaponSignature = firstWeaponSignature;
            SainClassificationAtStart = sainClassification;
            ThreatClassificationAtStart = threatClassification;
        }

        public string BotProfileId { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public DateTimeOffset LastObservedUtc { get; set; }
        public string FirstWeaponSignature { get; }
        public string LastWeaponSignature { get; set; }
        public string SainClassificationAtStart { get; }
        public string ThreatClassificationAtStart { get; }
        public long SampleCount { get; set; }
    }

    private static bool IsClose(float? distance, float max) => distance.HasValue && distance.Value >= 0f && distance.Value <= max;
    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Tri(bool? value) => value.HasValue ? Bool(value.Value) : "unknown";
    private static string Float(float? value) => value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : "unknown";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
