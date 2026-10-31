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
using Vanguard.Client.Runtime.External;
using Vanguard.Client.Runtime.Integrations.Looting;
using Vanguard.Client.Runtime.Medical.Execution;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Arbitrates opportunistic loot against combat, medical, cohesion, authored-position and other higher-priority Operator work.
// Flow: Qualified loot plans enter the broker, which checks current safety/lease ownership, acquires a bounded loot opportunity window and dispatches corpse/container execution while yielding immediately to stronger intents.
// Authority boundary: The broker owns loot scheduling only; inventory transaction authority and movement execution remain in their dedicated components.
// Invariant: Loot never starves emergency work, HOLD excursions preserve authored context for reacquisition, and abandoned opportunities release all temporary claims.
namespace Vanguard.Client.Runtime.Loot;

/// <summary>
/// This broker serves only as an authority-suppression boundary. Vanguard's
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
    private static readonly TimeSpan PreventDuration = TimeSpan.FromSeconds(3.00d);

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

    public static string ForgetRuntimePresence(string? botProfileId, string reason)
    {
        string key = string.IsNullOrWhiteSpace(botProfileId) ? "none" : botProfileId.Trim();
        if (string.Equals(key, "none", StringComparison.OrdinalIgnoreCase))
        {
            return "lootRuntime=ignored_missing_profile";
        }

        int localRemoved = 0;
        lock (Sync)
        {
            if (ScanCooldownByBotProfileId.Remove(key)) localRemoved++;
            if (PreventRefreshByBotProfileId.Remove(key)) localRemoved++;
            if (activeGrant.HasValue && string.Equals(activeGrant.Value.BotProfileId, key, StringComparison.OrdinalIgnoreCase))
            {
                activeGrant = null;
                localRemoved++;
            }

            foreach (string logKey in LastLogByKey.Keys.ToArray())
            {
                if (logKey.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    LastLogByKey.Remove(logKey);
                    localRemoved++;
                }
            }
        }

        string corpseSession = VanguardCorpseLootSessionExecutor.ForgetRuntimePresence(key, reason);
        string corpseApproach = VanguardCorpseLootApproachExecutor.ForgetRuntimePresence(key, reason);
        string containerSession = VanguardWorldLootContainerSessionExecutor.ForgetRuntimePresence(key, reason);
        string containerApproach = VanguardWorldLootContainerApproachExecutor.ForgetRuntimePresence(key, reason);
        int containerReadModel = VanguardWorldLootContainerReadOnlyEvaluator.ForgetRuntimePresence(key, reason);
        VanguardLootItemClaimStore.ReleaseByBot(key, "runtime_presence_lost:" + reason);
        bool targetClaimReleased = VanguardLootTargetClaimStore.ReleaseByBot(
            key,
            "runtime_presence_lost:" + reason,
            out VanguardLootTargetClaim _);

        return "lootRuntime=forgotten;local=" + localRemoved
            + ";corpseSession=" + Safe(corpseSession)
            + ";corpseApproach=" + Safe(corpseApproach)
            + ";containerSession=" + Safe(containerSession)
            + ";containerApproach=" + Safe(containerApproach)
            + ";containerReadModel=" + containerReadModel
            + ";itemClaimsReleased=true;targetClaimReleased=" + Bool(targetClaimReleased)
            + ";normalTerminal=false;outcomeMemoryMutation=false;externalResume=false;reason=" + Safe(reason);
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
                $"VANGUARD_LOOT_BROKER_BOOT legacyGlobalAdmissionIgnored=true; playerScopedAdmission=corpse_snapshot_owner_resolved; mode=operator_lootingbots_suppression_only; maxDistance=player_scoped_owner_resolved; activeLootPath=vanguard_corpse_qualification_claim_approach; selectedClaimantAlsoSuppressed=true; f12DisabledStillSuppressed=true; preventBackend=VanguardExternalAuthorityAdapter_continuous_refresh_guard; noAutonomousLootDriver=true; noForceScan=true; tag={StatusTag}; clientBuildTag={ClientBuildStatusTag}; build={VanguardBuildVersion.BuildLabel}");
        }

        var snapshots = VanguardOperatorDecisionSnapshotService.GetLatestSnapshots();
        if (snapshots == null || snapshots.Count == 0)
        {
            return;
        }

        bool approachExecutionEnabled = VanguardCorpseLootApproachDoctrine.ApproachExecutionEnabled;
        if (approachExecutionEnabled)
        {
            lock (Sync)
            {
                activeGrant = null;
            }

            // The runtime movement is entirely Vanguard-owned. LootingBots remains suppressed for every
            // Operator, including the selected corpse-loot claimant; it never receives a temporary grant.
            RefreshLootingBotsSuppression(snapshots, now, diagnosticSelectedBotProfileId: null, "claim_and_approach_boundary");
            return;
        }

        MaintainOrClearGrant(snapshots, now);
        string? selected = null;
        lock (Sync)
        {
            selected = activeGrant?.BotProfileId;
        }

        RefreshLootingBotsSuppression(snapshots, now, selected, "single_looter_policy");
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
            DateTimeOffset until = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.ForOwner(snapshot.OwnerProfileId).OpportunisticLootGrantSeconds);
            lock (Sync)
            {
                activeGrant = new LootGrant(snapshot.OperatorId, snapshot.BotProfileId, until, forceScan, scanSummary);
                ScanCooldownByBotProfileId[snapshot.BotProfileId] = now + TimeSpan.FromSeconds(VanguardMovementAuthorityDoctrine.ForOwner(snapshot.OwnerProfileId).OpportunisticLootScanCooldownSeconds);
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

        string prevent = botOwner == null
            ? "prevent=skipped_no_botowner"
            : VanguardExternalAuthorityAdapter.RequestLootingBotsAutonomySuppression(
                botOwner,
                "grant_clear:" + reason,
                TimeSpan.FromSeconds(2.0d),
                cleanupActiveState: true);
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_LOOT_GRANT_CLEARED operator={Safe(snapshot?.OperatorId ?? grant.Value.OperatorId)}; botProfile={Safe(grant.Value.BotProfileId)}; reason={Safe(reason)}; prevent={Safe(prevent)}; forceScanAtStart={Bool(grant.Value.ForceScanIssued)}; scan={Safe(grant.Value.ScanSummary)}; tag={StatusTag}");
    }

    private static void RefreshLootingBotsSuppression(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now, string? diagnosticSelectedBotProfileId, string reason)
    {
        // LootingBots suppression is a standing Operator firewall, not a response to unsafe state.
        // Every live Operator receives a refresh before the previous TTL expires, including a stable
        // idle Operator with no current loot target. This prevents LootingBots' own scheduled scan
        // from ever becoming a competing movement authority. A selected Vanguard loot claimant is
        // diagnostic context only and is never exempted from the external-autonomy suppression.
        foreach (var snapshot in snapshots)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            {
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record) || record.BotOwner == null || record.BotOwner.IsDead)
            {
                continue;
            }

            // Do not consume the refresh cadence until a live runtime owner exists; otherwise an
            // attach-order race could spend the firewall window without actually extending LootingBots suppression.
            if (!ShouldRefreshPrevent(snapshot.BotProfileId, now))
            {
                continue;
            }

            bool activeLoot = snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true || snapshot.Looting.HasActiveLootable == true;
            string prevent = VanguardExternalAuthorityAdapter.RequestLootingBotsAutonomySuppression(
                record.BotOwner,
                "operator_firewall:" + reason,
                PreventDuration,
                cleanupActiveState: activeLoot);
            LogThrottled("prevent|" + snapshot.BotProfileId + "|" + reason, now,
                () => $"VANGUARD_LOOT_PREVENTED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; reason={Safe(reason)}; selectedDiagnostic={Safe(diagnosticSelectedBotProfileId)}; prevent={Safe(prevent)}; lootActive={Bool(activeLoot)}; continuousSuppression=true; cleanupActiveState={Bool(activeLoot)}; refreshSeconds={PreventRefreshInterval.TotalSeconds:0.00}; ttlSeconds={PreventDuration.TotalSeconds:0.00}; directThreat={Bool(VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot))}; inBubble={Bool(snapshot.SquadCohesion.InBubble)}; medical={Safe(snapshot.Medical.Need.DominantNeed.ToString())}; tag={StatusTag}");
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
        var ownerPolicy = VanguardMovementAuthorityDoctrine.ForOwner(snapshot.OwnerProfileId);
        if (!ownerPolicy.OpportunisticLootBrokerEnabled)
        {
            reason = "owner_loot_broker_disabled";
            return false;
        }

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

        if (snapshot.Looting.DistanceToLoot.HasValue && snapshot.Looting.DistanceToLoot.Value > ownerPolicy.OpportunisticLootMaxDistanceMeters)
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

        if (snapshot.SquadCohesion.OperatorDistanceToOwner > Math.Max(8.0f, VanguardMovementAuthorityDoctrine.ForOwner(snapshot.OwnerProfileId).TacticalBubbleMeters - 8.0f))
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
            if (distance > VanguardMovementAuthorityDoctrine.ForOwner(subject.OwnerProfileId).TacticalSquadPressureBlockMeters)
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
        if (snapshot.Looting.DistanceToLoot.HasValue) score += Math.Max(0f, VanguardMovementAuthorityDoctrine.ForOwner(snapshot.OwnerProfileId).OpportunisticLootMaxDistanceMeters - snapshot.Looting.DistanceToLoot.Value);
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
        return VanguardExternalAuthorityAdapter.RequestLootingBotsAutonomySuppression(
            botOwner,
            "vanguard_owned_window:" + reason,
            TimeSpan.FromSeconds(Math.Max(1.0f, seconds)),
            cleanupActiveState: true);
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
