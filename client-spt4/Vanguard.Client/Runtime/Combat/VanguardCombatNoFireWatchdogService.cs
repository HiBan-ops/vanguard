#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.External;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.PostLoot;

// Responsibility: Coordinates Combat No Fire Watchdog Service for the combat safety runtime, delegating specialized work to its collaborators.
// Flow: Current raid/runtime evidence is normalized, applicable guards and ownership rules are evaluated, then the service updates only its bounded runtime/UI responsibility.
// Authority boundary: Service coordinates its domain but does not fabricate server persistence truth or bypass higher-priority runtime authorities.
// Invariant: State is lifecycle-scoped, stale work is releasable, and failures degrade without leaving hidden long-lived ownership.
namespace Vanguard.Client.Runtime.Combat;

internal static class VanguardCombatNoFireWatchdogService
{
    public const string StatusTag = "VANGUARD_COMBAT_BIND_COHESION_RECOVERY_STATUS";

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan SuspectLogInterval = TimeSpan.FromSeconds(1.75);
    private static readonly Dictionary<string, CombatNoFireState> StateByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset nextTickUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    public static void ResetForRaidLifecycle(string reason)
    {
        StateByBotProfileId.Clear();
        nextTickUtc = DateTimeOffset.MinValue;
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_COMBAT_NO_FIRE_WATCHDOG_RESET reason={Safe(reason)}; state=cleared; tag={StatusTag}");
    }

    public static void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_COMBAT_NO_FIRE_WATCHDOG_BOOT suspectSeconds={VanguardMovementAuthorityDoctrine.CombatNoFireSuspectSeconds:0.00}; cooldown={VanguardMovementAuthorityDoctrine.CombatNoFireRecoveryCooldownSeconds:0.00}; mode=observe_only_scheduler_owned; TargetAcquisition=true; TargetAcquisitionNoMoverStop=true; stableSignatureWindow=true; noAccumulatedTargetDrift=true; Tag={VanguardMovementAuthorityDoctrine.CombatCohesionAuthorityStatusTag}; Tag={VanguardMovementAuthorityDoctrine.AwarenessCombatSupportStatusTag}; tag={StatusTag}");
        }

        if (now < nextTickUtc)
        {
            return;
        }

        nextTickUtc = now + TickInterval;
        foreach (var snapshot in VanguardOperatorDecisionSnapshotService.GetLatestSnapshots())
        {
            TickSnapshot(snapshot, now);
        }
    }

    private static void TickSnapshot(OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot == null || !snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            return;
        }

        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record) || record.BotOwner == null)
        {
            return;
        }

        var weapon = VanguardPostLootWeaponReadinessReader.Capture(record.BotOwner);
        bool targetAcquisitionSupport = false;
        if (!VanguardMovementAuthorityDoctrine.IsCombatNoFireRecoverable(snapshot, out var recoverReason))
        {
            if (!IsCombatTargetAcquisitionRecoverable(snapshot, out recoverReason))
            {
                StateByBotProfileId.Remove(snapshot.BotProfileId);
                return;
            }

            targetAcquisitionSupport = true;
        }

        string key = snapshot.BotProfileId;
        if (!StateByBotProfileId.TryGetValue(key, out var state))
        {
            state = new CombatNoFireState { StableSinceUtc = now, LastSignature = BuildSignature(snapshot, weapon), LastObservedUtc = now };
            StateByBotProfileId[key] = state;
        }

        string signature = BuildSignature(snapshot, weapon);
        if (!string.Equals(signature, state.LastSignature, StringComparison.Ordinal))
        {
            state.LastSignature = signature;
            state.StableSinceUtc = now;
            state.LastObservedUtc = now;
        }

        double observedSeconds = (now - state.StableSinceUtc).TotalSeconds;
        if (now - state.LastLogUtc >= SuspectLogInterval)
        {
            state.LastLogUtc = now;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_COMBAT_NO_FIRE_WATCHDOG_OBSERVED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; stableObserved={observedSeconds:0.00}; reason={Safe(recoverReason)}; targetAcquisitionSupport={Bool(targetAcquisitionSupport)}; threat={Safe(snapshot.Threat.Classification)}; enemyVisible={Bool(snapshot.Threat.EnemyVisible == true)}; enemyCanShoot={Bool(snapshot.Threat.EnemyCanShoot == true)}; distance={Float(snapshot.Threat.Distance)}; sain={Safe(snapshot.Sain.Classification)}; action={Safe(snapshot.Sain.CurrentAction)}; movementHasPath={Tri(snapshot.Movement.HasPath)}; speed={snapshot.RealSpeed:0.00}; {weapon.Summary}; StableSignature=true; tag={StatusTag}");
        }

        bool criticalImmediate = (snapshot.Threat.EnemyVisible == true || snapshot.Threat.EnemyLineOfSight == true)
            && snapshot.Threat.EnemyCanShoot == true
            && (!snapshot.Threat.Distance.HasValue || snapshot.Threat.Distance.Value <= 14.0f);
        bool squadCriticalImmediate = Vanguard.Client.Runtime.Execution.VanguardOrchestratorAuthorityPolicy.IsCombatAuthority(snapshot, out var combatAuthorityReason)
            && (snapshot.Medical.Safety.IncomingFireRecent || snapshot.Threat.ShotMeRecently == true || snapshot.Threat.ShotAtMeRecently == true);
        bool enoughTime = observedSeconds >= VanguardMovementAuthorityDoctrine.CombatNoFireSuspectSeconds || criticalImmediate || squadCriticalImmediate;
        bool escalated = observedSeconds >= VanguardMovementAuthorityDoctrine.CombatNoFireEscalateSeconds || criticalImmediate;
        bool cooldownReady = state.LastRecoveryUtc == DateTimeOffset.MinValue
            || (now - state.LastRecoveryUtc).TotalSeconds >= (escalated ? Math.Max(2.25f, VanguardMovementAuthorityDoctrine.CombatNoFireRecoveryCooldownSeconds * 0.45f) : VanguardMovementAuthorityDoctrine.CombatNoFireRecoveryCooldownSeconds);
        bool weaponOrPathSuspicious = !weapon.WeaponReady
            || weapon.FirstAidUsing
            || snapshot.Medical.Actionability.AnyMedicineUsing
            || snapshot.Movement.HasPath == true
            || snapshot.Orbit.Active
            || snapshot.Looting.BotLooting == true
            || snapshot.Looting.LootTaskRunning == true;

        if (enoughTime && cooldownReady)
        {
            state.LastRecoveryUtc = now;
            string observationLevel = criticalImmediate ? "level3_immediate_close_can_shoot" : escalated ? "level2_extended_no_production" : targetAcquisitionSupport ? "level1_target_acquisition_support" : "level1_no_fire_observed";
            bool combatWindowProtected = VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out var protectedReason);
            VanguardClientDiagnosticsLog.Warning(StatusTag,
                $"VANGUARD_COMBAT_NO_FIRE_OBSERVED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; stableObserved={observedSeconds:0.00}; reason={Safe(recoverReason)}; Reason={Safe(combatAuthorityReason)}; criticalImmediate={Bool(criticalImmediate)}; squadCriticalImmediate={Bool(squadCriticalImmediate)}; weaponOrPathSuspicious={Bool(weaponOrPathSuspicious)}; targetAcquisitionSupport={Bool(targetAcquisitionSupport)}; observationLevel={observationLevel}; combatWindowProtected={Bool(combatWindowProtected)}; protectedReason={Safe(protectedReason)}; mutation=false; action=report_to_scheduler_no_hands_path_or_goal_reset; tag={VanguardPrimaryExecutionContract.SainWindowStatusTag}; legacyTag={StatusTag}");
        }
    }


    private static bool IsCombatTargetAcquisitionRecoverable(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "operator_dead_or_missing";
            return false;
        }

        if (!Vanguard.Client.Runtime.Execution.VanguardOrchestratorAuthorityPolicy.IsCombatAuthority(snapshot, out var authorityReason))
        {
            reason = "no_combat_authority";
            return false;
        }

        bool hasFreshExternalCandidate = snapshot.Awareness.IncomingFireFresh
            || snapshot.Awareness.WouldPromoteSainTarget
            || snapshot.Awareness.WouldPropagateConfirmedThreat
            || snapshot.Awareness.CandidateVisible
            || snapshot.Awareness.CandidateLineOfSight
            || snapshot.Awareness.CandidateCanShoot
            || snapshot.ThreatScan.WouldPromote
            || snapshot.ThreatScan.CandidateVisible
            || snapshot.ThreatScan.CandidateLineOfSight
            || snapshot.ThreatScan.CandidateCanShoot
            || snapshot.ThreatScan.CandidateIncomingFireFresh
            || snapshot.ThreatScan.CandidateShotAtMeRecently
            || snapshot.ThreatScan.CandidateShotMeRecently
            || snapshot.Threat.EnemyVisible == true
            || snapshot.Threat.EnemyLineOfSight == true
            || snapshot.Threat.EnemyCanShoot == true;
        if (!hasFreshExternalCandidate)
        {
            reason = "combat_authority_without_fresh_candidate:" + Safe(authorityReason);
            return false;
        }

        bool noLocalSainTarget = snapshot.Sain.HasEnemy != true
            && !ContainsCombatText(snapshot.Sain.CombatDecision);
        bool noLocalCombatAction = snapshot.Sain.IsInCombat != true
            && !ContainsCombatText(snapshot.Sain.Classification)
            && !ContainsCombatText(snapshot.Sain.CurrentAction)
            && !ContainsCombatText(snapshot.Sain.ActiveLayer)
            && !ContainsCombatText(snapshot.Brain.ActiveLayer);
        if (!noLocalSainTarget && !noLocalCombatAction)
        {
            reason = "sain_already_has_local_target_or_action:" + Safe(authorityReason);
            return false;
        }

        reason = "combat_authority_target_acquisition_support:" + Safe(authorityReason);
        return true;
    }

    private static bool ContainsCombatText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string text = value.ToLowerInvariant();
        return text.Contains("combat")
            || text.Contains("shoot")
            || text.Contains("attack")
            || text.Contains("enemy")
            || text.Contains("cover")
            || text.Contains("rush")
            || text.Contains("search")
            || text.Contains("fight");
    }

    private static string BuildSignature(OperatorDecisionSnapshot snapshot, VanguardPostLootWeaponReadinessSnapshot weapon)
    {
        return string.Join("|",
            snapshot.Threat.EnemyId,
            snapshot.Threat.EnemyVisible == true ? "vis" : "novis",
            snapshot.Threat.EnemyCanShoot == true ? "shoot" : "noshoot",
            snapshot.Awareness.CandidateId,
            snapshot.Awareness.CandidateVisible ? "aware_vis" : "aware_novis",
            snapshot.Awareness.CandidateLineOfSight ? "aware_los" : "aware_nolos",
            snapshot.ThreatScan.CandidateThreatId,
            snapshot.ThreatScan.CandidateVisible ? "scan_vis" : "scan_novis",
            snapshot.ThreatScan.CandidateLineOfSight ? "scan_los" : "scan_nolos",
            snapshot.Sain.Classification,
            snapshot.Sain.CurrentAction,
            snapshot.Brain.ActiveLayer,
            snapshot.Movement.HasPath == true ? "path" : "nopath",
            weapon.Signature);
    }

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Tri(bool? value) => value.HasValue ? Bool(value.Value) : "unknown";
    private static string Float(float? value) => value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : "unknown";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');

    private sealed class CombatNoFireState
    {
        public DateTimeOffset StableSinceUtc { get; set; }
        public DateTimeOffset LastObservedUtc { get; set; }
        public DateTimeOffset LastLogUtc { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastRecoveryUtc { get; set; } = DateTimeOffset.MinValue;
        public string LastSignature { get; set; } = "none";
    }
}
#endif
