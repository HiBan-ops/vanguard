#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EFT;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Integrations.Looting;
using Vanguard.Client.Runtime.Medical.Execution;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Arbitrates opportunistic loot against combat, medical, cohesion, authored-position and other higher-priority Operator work.
// Flow: Qualified loot plans enter the broker, which checks current safety/lease ownership, acquires a bounded loot opportunity window and dispatches corpse/container execution while yielding immediately to stronger intents.
// Authority boundary: The broker owns loot scheduling only; inventory transaction authority and movement execution remain in their dedicated components.
// Invariant: Loot never starves emergency work, HOLD excursions preserve authored context for reacquisition, and abandoned opportunities release all temporary claims.
namespace Vanguard.Client.Runtime.Loot;

/// <summary>
/// The historical broker remains only as an authority-suppression boundary. Vanguard's
/// corpse qualification, claim and approach chain is the sole active loot path for Operators. The
/// autonomous LootingBots scan driver is never granted authority, including when F12 admission is off.
/// </summary>
internal static class VanguardOpportunisticLootBroker
{
    public const string StatusTag = "VANGUARD_OPPORTUNISTIC_LOOT_BROKER_OK";
    public const string ClientBuildStatusTag = "VANGUARD_LOOT_BROKER_CLIENT_BUILD_OK";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, DateTimeOffset> ScanCooldownByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> PreventRefreshByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogByKey = new(StringComparer.OrdinalIgnoreCase);
    private static LootGrant? activeGrant;
    private static DateTimeOffset nextTickAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(0.85d);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(2.00d);
    private static readonly TimeSpan PreventRefreshInterval = TimeSpan.FromSeconds(1.75d);

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            ScanCooldownByBotProfileId.Clear();
            PreventRefreshByBotProfileId.Clear();
            LastLogByKey.Clear();
            activeGrant = null;
        }

        nextTickAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_LOOT_BROKER_RESET reason={Safe(reason)}; activeGrant=none; oneLooter=true; cooldowns=cleared; tag={StatusTag}; hardReturnBackoffTag={VanguardMovementAuthorityDoctrine.HardReturnCombatBackoffStatusTag}");
    }

    public static void Tick()
    {
        if (!VanguardOperatorRuntimeAuditLoadGuard.IsOpen() || !VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }

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
                $"VANGUARD_LOOT_BROKER_BOOT legacyGlobalAdmissionIgnored=true; playerScopedAdmission=corpse_snapshot_owner_resolved; mode=operator_lootingbots_suppression_only; maxDistance=player_scoped_owner_resolved; activeLootPath=vanguard_corpse_qualification_claim_approach; selectedClaimantAlsoSuppressed=true; f12DisabledStillSuppressed=true; preventBackend=LootingBots.External.PreventBotFromLooting_transition_guard; noAutonomousLootDriver=true; noForceScan=true; tag={StatusTag}; clientBuildTag={ClientBuildStatusTag}; build={VanguardBuildVersion.BuildLabel}");
        }

        var snapshots = VanguardOperatorDecisionSnapshotService.GetLatestSnapshots();
        if (snapshots == null || snapshots.Count == 0)
        {
            return;
        }

        if (VanguardCorpseLootApproachDoctrine.ApproachExecutionEnabled
            && VanguardMovementAuthorityDoctrine.OpportunisticLootBrokerEnabled)
        {
            lock (Sync)
            {
                activeGrant = null;
            }

            // The runtime movement is entirely Vanguard-owned. LootingBots remains suppressed for every
            // Operator, including the selected corpse-loot claimant; it never receives a temporary grant.
            PreventUnsafeLooters(snapshots, now, selectedBotProfileId: null, "claim_and_approach_boundary");
            return;
        }

        if (!VanguardMovementAuthorityDoctrine.OpportunisticLootBrokerEnabled)
        {
            // F12 disables Vanguard corpse-loot admission only. LootingBots remains suppressed for
            // Operators until a future explicit integration phase grants it a reviewed authority.
            ClearGrantIfAny(now, "f12_disabled", snapshots);
            PreventUnsafeLooters(snapshots, now, selectedBotProfileId: null, "f12_disabled_lootingbots_still_suppressed");
            return;
        }

        MaintainOrClearGrant(snapshots, now);
        string? selected = null;
        lock (Sync)
        {
            selected = activeGrant?.BotProfileId;
        }

        PreventUnsafeLooters(snapshots, now, selected, "single_looter_policy");
        if (selected != null)
        {
            return;
        }

        TryStartGrant(snapshots, now);
    }

    private static void TryStartGrant(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        var candidates = snapshots
            .Where(snapshot => snapshot != null && !string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            .Select(snapshot => new Candidate(snapshot, Score(snapshot)))
            .Where(candidate => candidate.Score > 0f)
            .OrderByDescending(candidate => candidate.Score)
            .ToArray();

        foreach (var candidate in candidates)
        {
            var snapshot = candidate.Snapshot;
            if (!IsGrantEligible(snapshot, snapshots, now, out var reason))
            {
                LogReject(snapshot, now, reason);
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record) || record.BotOwner == null || record.BotOwner.IsDead)
            {
                LogReject(snapshot, now, "botowner_missing");
                continue;
            }

            bool forceScan = TryForceLootScan(record.BotOwner, snapshot, out var scanSummary);
            DateTimeOffset until = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.OpportunisticLootGrantSeconds);
            lock (Sync)
            {
                activeGrant = new LootGrant(snapshot.OperatorId, snapshot.BotProfileId, until, forceScan, scanSummary);
                ScanCooldownByBotProfileId[snapshot.BotProfileId] = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.OpportunisticLootScanCooldownSeconds);
            }

            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_LOOT_GRANT_STARTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; score={candidate.Score:0.0}; forceScan={Bool(forceScan)}; scan={Safe(scanSummary)}; grantUntil={until:O}; distanceToLoot={Float(snapshot.Looting.DistanceToLoot)}; activeLootable={Tri(snapshot.Looting.HasActiveLootable)}; freeSpace={Tri(snapshot.Looting.HasFreeSpace)}; useful={Bool(snapshot.SquadCohesion.UsefulPosition)}; sector={Safe(snapshot.SquadCohesion.Sector)}; env={Safe(snapshot.SquadCohesion.TacticalEnvironmentKind)}; oneLooter=true; tag={StatusTag}");
            return;
        }
    }

    private static void MaintainOrClearGrant(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        LootGrant? grant;
        lock (Sync)
        {
            grant = activeGrant;
        }

        if (!grant.HasValue)
        {
            return;
        }

        var snapshot = snapshots.FirstOrDefault(item => string.Equals(item.BotProfileId, grant.Value.BotProfileId, StringComparison.OrdinalIgnoreCase));
        if (snapshot == null)
        {
            ClearGrant(now, "snapshot_missing", null, null);
            return;
        }

        if (now >= grant.Value.UntilUtc)
        {
            ClearGrant(now, "grant_expired", snapshot, null);
            return;
        }

        if (!IsGrantStillSafe(snapshot, snapshots, out var reason))
        {
            BotOwner? botOwner = null;
            if (VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record))
            {
                botOwner = record.BotOwner;
            }

            ClearGrant(now, "unsafe:" + reason, snapshot, botOwner);
            return;
        }

        LogThrottled("grantActive|" + grant.Value.BotProfileId, now,
            $"VANGUARD_LOOT_GRANT_ACTIVE operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; until={grant.Value.UntilUtc:O}; remaining={(grant.Value.UntilUtc - now).TotalSeconds:0.0}; lootActive={Tri(snapshot.Looting.BotLooting)}; lootTask={Tri(snapshot.Looting.LootTaskRunning)}; activeLootable={Tri(snapshot.Looting.HasActiveLootable)}; distanceToLoot={Float(snapshot.Looting.DistanceToLoot)}; orbitActive={Bool(snapshot.Orbit.Active)}; tag={StatusTag}");
    }

    private static void ClearGrantIfAny(DateTimeOffset now, string reason, IReadOnlyList<OperatorDecisionSnapshot> snapshots)
    {
        LootGrant? grant;
        lock (Sync)
        {
            grant = activeGrant;
        }

        if (!grant.HasValue)
        {
            return;
        }

        var snapshot = snapshots.FirstOrDefault(item => string.Equals(item.BotProfileId, grant.Value.BotProfileId, StringComparison.OrdinalIgnoreCase));
        BotOwner? botOwner = null;
        if (snapshot != null && VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record))
        {
            botOwner = record.BotOwner;
        }

        ClearGrant(now, reason, snapshot, botOwner);
    }

    private static void ClearGrant(DateTimeOffset now, string reason, OperatorDecisionSnapshot? snapshot, BotOwner? botOwner)
    {
        LootGrant? grant;
        lock (Sync)
        {
            grant = activeGrant;
            activeGrant = null;
        }

        if (!grant.HasValue)
        {
            return;
        }

        string prevent = botOwner == null ? "prevent=skipped_no_botowner" : PreventLoot(botOwner, 2.0f);
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_LOOT_GRANT_CLEARED operator={Safe(snapshot?.OperatorId ?? grant.Value.OperatorId)}; botProfile={Safe(grant.Value.BotProfileId)}; reason={Safe(reason)}; prevent={Safe(prevent)}; forceScanAtStart={Bool(grant.Value.ForceScanIssued)}; scan={Safe(grant.Value.ScanSummary)}; tag={StatusTag}");
    }

    private static void PreventUnsafeLooters(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now, string? selectedBotProfileId, string reason)
    {
        foreach (var snapshot in snapshots)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            {
                continue;
            }

            if (string.Equals(snapshot.BotProfileId, selectedBotProfileId, StringComparison.OrdinalIgnoreCase) && IsGrantStillSafe(snapshot, snapshots, out _))
            {
                continue;
            }

            bool activeLoot = snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true || snapshot.Looting.HasActiveLootable == true;
            if (!activeLoot && IsGrantStillSafe(snapshot, snapshots, out _))
            {
                continue;
            }

            if (!ShouldRefreshPrevent(snapshot.BotProfileId, now))
            {
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record) || record.BotOwner == null || record.BotOwner.IsDead)
            {
                continue;
            }

            string prevent = PreventLoot(record.BotOwner, 3.0f);
            LogThrottled("prevent|" + snapshot.BotProfileId + "|" + reason, now,
                () => $"VANGUARD_LOOT_PREVENTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; selected={Safe(selectedBotProfileId)}; prevent={Safe(prevent)}; lootActive={Bool(activeLoot)}; directThreat={Bool(VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot))}; inBubble={Bool(snapshot.SquadCohesion.InBubble)}; medical={Safe(snapshot.Medical.Need.DominantNeed.ToString())}; tag={StatusTag}");
        }
    }

    private static bool ShouldRefreshPrevent(string botProfileId, DateTimeOffset now)
    {
        lock (Sync)
        {
            if (PreventRefreshByBotProfileId.TryGetValue(botProfileId, out var until) && until > now)
            {
                return false;
            }

            PreventRefreshByBotProfileId[botProfileId] = now + PreventRefreshInterval;
            return true;
        }
    }

    private static bool IsGrantEligible(OperatorDecisionSnapshot snapshot, IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now, out string reason)
    {
        if (!IsGrantStillSafe(snapshot, snapshots, out reason))
        {
            return false;
        }

        if (!snapshot.Looting.TypeLoaded || !snapshot.Looting.ComponentPresent)
        {
            reason = "loot_component_missing";
            return false;
        }

        if (snapshot.Looting.HasFreeSpace == false)
        {
            reason = "inventory_full";
            return false;
        }

        if (snapshot.Looting.DistanceToLoot.HasValue && snapshot.Looting.DistanceToLoot.Value > VanguardMovementAuthorityDoctrine.OpportunisticLootMaxDistanceMeters)
        {
            reason = "loot_too_far:" + snapshot.Looting.DistanceToLoot.Value.ToString("0.0", CultureInfo.InvariantCulture);
            return false;
        }

        lock (Sync)
        {
            if (ScanCooldownByBotProfileId.TryGetValue(snapshot.BotProfileId, out var cooldown) && cooldown > now)
            {
                reason = "scan_cooldown:" + (cooldown - now).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
                return false;
            }
        }

        if (snapshot.Looting.ScanRunning == true)
        {
            reason = "scan_already_running";
            return false;
        }

        if (snapshot.RealSpeed > 0.85f || snapshot.Movement.RealSpeed > 0.85f)
        {
            reason = "operator_moving";
            return false;
        }

        reason = "eligible";
        return true;
    }

    private static bool IsGrantStillSafe(OperatorDecisionSnapshot snapshot, IReadOnlyList<OperatorDecisionSnapshot> snapshots, out string reason)
    {
        if (snapshot == null || !snapshot.Alive)
        {
            reason = "not_alive";
            return false;
        }

        if (!snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement || !snapshot.SquadCohesion.InBubble)
        {
            reason = "outside_or_unreliable_bubble";
            return false;
        }

        if (snapshot.SquadCohesion.OperatorDistanceToOwner > Math.Max(8.0f, VanguardMovementAuthorityDoctrine.TacticalBubbleMeters - 8.0f))
        {
            reason = "near_bubble_edge";
            return false;
        }

        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot))
        {
            reason = "direct_threat";
            return false;
        }

        if (HasNearbySquadCombatPressure(snapshots, snapshot, out var pressureReason))
        {
            reason = "squad_pressure:" + pressureReason;
            return false;
        }

        if (VanguardMovementAuthorityDoctrine.IsStationaryMedicalAuthority(snapshot))
        {
            reason = "medical_authority:stationary_medical";
            return false;
        }

        if (VanguardExecutionLeaseCoordinator.HasActiveLease(snapshot.BotProfileId))
        {
            reason = "medical_authority:active_medical_lease";
            return false;
        }

        if (VanguardSurgeryDebtService.HasDueDebt(snapshot, out var debtReason))
        {
            reason = "medical_authority:" + debtReason;
            return false;
        }

        if (VanguardMainIntentScheduler.HasBlockingPrimaryWindow(snapshot.BotProfileId, DateTimeOffset.UtcNow, out var primaryReason))
        {
            reason = "primary_window:" + primaryReason;
            return false;
        }

        if (snapshot.Orbit.Active)
        {
            reason = "orbit_active";
            return false;
        }

        if (snapshot.Movement.HasPath == true && (snapshot.Movement.DistanceToDestination ?? snapshot.Movement.GoToDistance ?? 0f) > 1.5f)
        {
            reason = "path_active";
            return false;
        }

        if (!snapshot.SquadCohesion.UsefulPosition || snapshot.SquadCohesion.SectorDuplicate || snapshot.SquadCohesion.RearOverstacked || !snapshot.SquadCohesion.SectorTopologyValid)
        {
            reason = "cohesion_not_stable";
            return false;
        }

        reason = "safe";
        return true;
    }

    private static bool HasNearbySquadCombatPressure(IReadOnlyList<OperatorDecisionSnapshot> snapshots, OperatorDecisionSnapshot subject, out string reason)
    {
        foreach (var other in snapshots)
        {
            if (other == null || string.Equals(other.BotProfileId, subject.BotProfileId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(other.OwnerProfileId, subject.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!other.Alive)
            {
                continue;
            }

            float distance = HorizontalDistance(other.Position, subject.Position);
            if (distance > VanguardMovementAuthorityDoctrine.TacticalSquadPressureBlockMeters)
            {
                continue;
            }

            if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(other) || other.Sain.IsInCombat == true || other.Threat.DirectThreat)
            {
                reason = "near_operator_combat:" + Safe(other.BotProfileId) + ":dist=" + distance.ToString("0.0", CultureInfo.InvariantCulture);
                return true;
            }
        }

        reason = "none";
        return false;
    }

    private static float Score(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.Alive || !snapshot.SquadCohesion.InBubble)
        {
            return -1000f;
        }

        float score = 10f;
        if (snapshot.Looting.HasActiveLootable == true) score += 25f;
        if (snapshot.Looting.HasFreeSpace == true) score += 8f;
        if (snapshot.SquadCohesion.UsefulPosition) score += 8f;
        if (snapshot.RealSpeed < 0.35f) score += 4f;
        if (snapshot.Looting.DistanceToLoot.HasValue) score += Math.Max(0f, VanguardMovementAuthorityDoctrine.OpportunisticLootMaxDistanceMeters - snapshot.Looting.DistanceToLoot.Value);
        if (snapshot.Medical.Need.HasAnyNeed) score -= 30f;
        if (snapshot.SquadCohesion.SectorDuplicate) score -= 10f;
        if (!snapshot.SquadCohesion.SectorTopologyValid) score -= 20f;
        return score;
    }

    private static bool TryForceLootScan(BotOwner botOwner, OperatorDecisionSnapshot snapshot, out string summary)
    {
        VanguardOperatorLootAuthorityPolicy.ShouldAllowExternalForceScan(botOwner, snapshot, out var policyReason);
        summary = policyReason + ":future_loot=vanguard_owned_window";
        return false;
    }

    public static string PreventForVanguardOwnedWindow(BotOwner botOwner, float seconds, string reason)
    {
        string result = PreventLoot(botOwner, seconds);
        return "reason=" + Safe(reason) + ";" + result;
    }

    private static string PreventLoot(BotOwner botOwner, float seconds)
    {
        Type? externalType = VanguardOperatorRuntimeAuditReflection.FindType("LootingBots.External");
        if (externalType == null)
        {
            return "external_type_missing";
        }

        object? result = VanguardOperatorRuntimeAuditReflection.InvokeStatic(externalType, "PreventBotFromLooting", botOwner, seconds);
        return result is bool b ? "PreventBotFromLooting=" + Bool(b) + ":seconds=" + seconds.ToString("0.0", CultureInfo.InvariantCulture) : "PreventBotFromLooting_result=" + Safe(result?.ToString());
    }

    private static void LogReject(OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason)
    {
        LogThrottled("reject|" + snapshot.BotProfileId + "|" + reason, now, TimeSpan.FromSeconds(8.0d),
            () => $"VANGUARD_LOOT_GRANT_REJECTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; inBubble={Bool(snapshot.SquadCohesion.InBubble)}; useful={Bool(snapshot.SquadCohesion.UsefulPosition)}; duplicate={Bool(snapshot.SquadCohesion.SectorDuplicate)}; lootComp={Bool(snapshot.Looting.ComponentPresent)}; freeSpace={Tri(snapshot.Looting.HasFreeSpace)}; activeLootable={Tri(snapshot.Looting.HasActiveLootable)}; distanceToLoot={Float(snapshot.Looting.DistanceToLoot)}; tag={StatusTag}");
    }

    private static void LogThrottled(string key, DateTimeOffset now, Func<string> messageFactory)
    {
        LogThrottled(key, now, LogInterval, messageFactory);
    }

    private static void LogThrottled(string key, DateTimeOffset now, TimeSpan interval, Func<string> messageFactory)
    {
        if (!VanguardClientDiagnosticsLog.IsEnabled(VanguardAuditLevel.Trace))
        {
            return;
        }

        lock (Sync)
        {
            if (LastLogByKey.TryGetValue(key, out var last) && now - last < interval)
            {
                return;
            }

            LastLogByKey[key] = now;
        }

        VanguardClientDiagnosticsLog.Trace(StatusTag, messageFactory);
    }

    private static void LogThrottled(string key, DateTimeOffset now, string message)
    {
        LogThrottled(key, now, LogInterval, message);
    }

    private static void LogThrottled(string key, DateTimeOffset now, TimeSpan interval, string message)
    {
        lock (Sync)
        {
            if (LastLogByKey.TryGetValue(key, out var last) && now - last < interval)
            {
                return;
            }

            LastLogByKey[key] = now;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag, message);
    }

    private static float HorizontalDistance(UnityEngine.Vector3 a, UnityEngine.Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Tri(bool? value) => value.HasValue ? Bool(value.Value) : "unknown";
    private static string Float(float? value) => value.HasValue ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) : "unknown";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private readonly struct Candidate
    {
        public Candidate(OperatorDecisionSnapshot snapshot, float score)
        {
            Snapshot = snapshot;
            Score = score;
        }

        public OperatorDecisionSnapshot Snapshot { get; }
        public float Score { get; }
    }

    private readonly struct LootGrant
    {
        public LootGrant(string operatorId, string botProfileId, DateTimeOffset untilUtc, bool forceScanIssued, string scanSummary)
        {
            OperatorId = operatorId;
            BotProfileId = botProfileId;
            UntilUtc = untilUtc;
            ForceScanIssued = forceScanIssued;
            ScanSummary = scanSummary;
        }

        public string OperatorId { get; }
        public string BotProfileId { get; }
        public DateTimeOffset UntilUtc { get; }
        public bool ForceScanIssued { get; }
        public string ScanSummary { get; }
    }
}
#endif
