using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vanguard.Client;
using Vanguard.Client.Api;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Career;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Raid.Persistence;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Alliance;
#if SPT_CLIENT
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Intents;
using Vanguard.Client.Runtime.Loot;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Combat;
using Vanguard.Client.Runtime.Medical.Execution;
using Vanguard.Client.Runtime.Medical;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.PostLoot;
using Vanguard.Client.Runtime.Weapon;
using Vanguard.Client.Runtime.External;
using Vanguard.Client.Runtime.Integrations.Sain;
using Vanguard.Client.Runtime.Grenades;
using Vanguard.Client.Raid.Runtime.Fika;
#endif

// Responsibility: Coordinates the client-side lifecycle of the Operator squad for a raid, from manifest acquisition through spawn/runtime registration to teardown.
// Flow: Raid start resolves the authoritative manifest and settings, initializes runtime services and spawn/bind tracking, then lifecycle ticks/stop hooks hand work to specialized subsystems and clean them in a deterministic order.
// Authority boundary: The controller sequences lifecycle only; server persistence owns durable Operator state and specialized runtime systems own combat, medical, loot and movement behavior.
// Invariant: One raid owns one coherent controller lifecycle, repeated hooks remain idempotent, and teardown must release runtime state before the next raid begins.
namespace Vanguard.Client.Raid.Services;

internal static class VanguardRaidOperatorController
{
    private static readonly object Sync = new();
    private static readonly VanguardApiClient ApiClient = new();
#if SPT_CLIENT
    private static readonly VanguardRaidOperatorSpawnService SpawnService = new();
#endif
    private static bool manifestPrimed;
    private static bool spawnQueued;
    private static bool spawnRunning;

    /// <summary>
    /// Resets raid-scoped spawn controller latches before a new PMC raid is primed.
    /// The first-raid spawn path is validated, but the static manifestPrimed latch
    /// could survive a return-to-menu cycle and prevent the next raid from loading its
    /// fresh manifest.  The reset is intentionally limited to controller state; it does
    /// not touch spawn placement, group binding, SAIN, HUD rendering or server data.
    /// </summary>
    public static void ResetForRaidStart(string source)
    {
#if SPT_CLIENT
        VanguardClientDiagnosticsLog.ResetAuditSession(source);
        lock (Sync)
        {
            manifestPrimed = false;
            spawnQueued = false;
            spawnRunning = false;
        }

        VanguardRaidOperatorSpawnService.ResetStaticCachesForRaidLifecycle(source);
        VanguardSainStaticProfileService.ResetForRaidLifecycle(source);
        VanguardGrenadeHazardAuditService.ResetForRaidLifecycle(source);
        VanguardGrenadeEmergencyEvasionService.ResetForRaidLifecycle(source);
        VanguardOperatorRuntimeAuditService.ResetForRaidLifecycle(source);
        VanguardOperatorRuntimeAuditSyncService.ResetForRaidLifecycle(source);
        VanguardRuntimeSettingsAuthorityResolver.ResetForRaidLifecycle(source);
        VanguardOperatorDecisionSnapshotService.ResetForRaidLifecycle(source);
        VanguardFikaHudTelemetryService.ResetForRaidLifecycle(source);
        VanguardSainAutonomousExtractGuardService.ResetForRaidLifecycle(source);
        Vanguard.Client.Raid.Patches.VanguardFikaOperatorDogtagGuardPatch.ResetForRaidLifecycle(source);
        VanguardOperatorIntentDryRunService.ResetForRaidLifecycle(source);
        VanguardMainIntentScheduler.ResetForRaidLifecycle(source);
        VanguardMobileMedicalLeaseExecutor.ResetForRaidLifecycle(source);
        VanguardHardReturnMovementExecutor.ResetForRaidLifecycle(source);
        VanguardCloseCohesionExecutor.ResetForRaidLifecycle(source);
        VanguardSquadTravelCohesionExecutor.ResetForRaidLifecycle(source);
        VanguardSquadCohesionClaimExecutor.ResetForRaidLifecycle(source);
        VanguardTacticalRepositionExecutor.ResetForRaidLifecycle(source);
        VanguardCorpseLootApproachExecutor.ResetForRaidLifecycle(source);
        VanguardOpportunisticLootBroker.ResetForRaidLifecycle(source);
        VanguardOwnerLootInterestSyncService.ResetForRaidLifecycle(source);
        VanguardWorldLootContainerReadModelService.ResetForRaidLifecycle(source);
        VanguardWorldLootContainerReadOnlyEvaluator.ResetForRaidLifecycle(source);
        VanguardWorldLootContainerApproachExecutor.ResetForRaidLifecycle(source);
        VanguardUnifiedOpportunisticLootReadModelService.ResetForRaidLifecycle(source);
        VanguardCorpseRegistry.ResetForRaidLifecycle(source);
        VanguardCombatAwarenessBridge.ResetForRaidLifecycle(source);
        VanguardOperatorFriendlyTargetGuard.ResetForRaidLifecycle(source);
        VanguardRuntimeBindGuardService.ResetForRaidLifecycle(source);
        VanguardCombatNoFireWatchdogService.ResetForRaidLifecycle(source);
        VanguardSainSquadCombatAuthority.ResetForRaidLifecycle(source);
        VanguardGlobalCombatProductionDiagnosticsService.ResetForRaidLifecycle(source);
        VanguardStaleSainExitService.ResetForRaidLifecycle(source);
        VanguardPostLootCombatReadinessAuditService.ResetForRaidLifecycle(source);
        VanguardWeaponHandsCombatAuditService.ResetForRaidLifecycle(source);
        VanguardCoopStructuralSquadBinder.Reset(source);
        VanguardFriendlyFireSafetyService.Reset(source);
        VanguardFriendlyDamageVetoService.Reset(source);
        VanguardOwnerShotMemoryService.Reset(source);
        VanguardNearMissSuppressionService.Reset(source);
        VanguardRuntimePerformanceGuard.Reset(source);
        VanguardRuntimeFrameBudgetGuard.Reset(source);
        VanguardHeadlessRuntimeStallGuard.Reset(source);
        VanguardCareerEventTruthProbeService.ResetForRaidLifecycle(source);
        VanguardRaidOperatorPersistenceService.ResetForRaidLifecycle(source);
        VanguardHeadlessPostRaidQuiescenceService.ResetForRaidLifecycle(source);
        VanguardHeadlessGcPolicyService.ResetForRaidLifecycle(source);
        VanguardHeadlessMemoryTelemetryService.ResetForRaidLifecycle(source);

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.RaidSpawnStatusTag,
            $"spawn controller reset source={source}; manifestPrimed=false; spawnQueued=false; spawnRunning=false");
#endif
    }


    /// <summary>
    /// Headless does not always pass through the local matchmaker raid-start screen between
    /// two raids.  The authoritative signal that a new raid runtime exists is the first
    /// fresh BotsController instance captured by the spawn patches.  Resetting here fixes
    /// the source of the second-raid no-spawn issue: stale spawn latches from the previous
    /// authoritative raid, not a missing retry after the fact.
    /// </summary>
    public static void ResetForNewAuthorityRaidCycle(string source)
    {
#if SPT_CLIENT
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.RaidSpawnStatusTag,
                $"authority raid-cycle reset skipped source={source}; reason=not_raid_authority; fikaInstalled={VanguardFikaCompat.IsInstalled}; headless={VanguardFikaCompat.IsHeadless}; client={VanguardFikaCompat.IsClient}; host={VanguardFikaCompat.IsHost}");
            return;
        }

        ResetForRaidStart(source);
        VanguardRaidOperatorRuntimeRegistry.Reset(source);
        VanguardFriendlyIdentityRegistry.Reset(source);
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.RaidSpawnStatusTag,
            $"authority raid-cycle reset completed source={source}; authority=headless_or_host");
#endif
    }

    public static void PrimeFromRaidPlayers(string source)
    {
#if SPT_CLIENT
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            VanguardClientDiagnosticsLog.Info("VANGUARD_RAID_SPAWN_STATUS", $"prime skipped source={source}; reason=not_raid_authority; fikaInstalled={VanguardFikaCompat.IsInstalled}; headless={VanguardFikaCompat.IsHeadless}; client={VanguardFikaCompat.IsClient}; host={VanguardFikaCompat.IsHost}");
            return;
        }

        var ownerProfileIds = VanguardFikaCompat.GetRaidPlayerProfileIds(message => VanguardClientDiagnosticsLog.Info("VANGUARD_RAID_SPAWN_STATUS", message))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ownerProfileIds.Length == 0)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_RAID_SPAWN_STATUS", $"prime skipped source={source}; reason=no_player_owner_profile_ids");
            return;
        }

        string raidSessionId = "raid-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var response = ApiClient.LoadRaidManifestForProfiles(ownerProfileIds, raidSessionId);
        if (!response.Success)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_RAID_SPAWN_STATUS", $"prime failed source={source}; reason={response.Reason ?? "unknown"}; owners={ownerProfileIds.Length}");
            return;
        }

        VanguardRaidOperatorRuntimeRegistry.SetManifestOwners(response);
        VanguardRaidOperatorPersistenceService.ArmFromManifest(response, source);
        VanguardFriendlyIdentityRegistry.RefreshNow($"manifest_prime:{source}");
        lock (Sync)
        {
            manifestPrimed = true;
        }

        VanguardClientDiagnosticsLog.Info("VANGUARD_RAID_MANIFEST_OWNER", $"prime completed source={source}; owners={response.OwnerCount}; operators={response.OperatorCount}; authority=headless_or_host; ownership=player_owner_profile_id");
#endif
    }

    public static void QueueSpawn(string source)
    {
#if SPT_CLIENT
        lock (Sync)
        {
            spawnQueued = true;
        }

        VanguardClientDiagnosticsLog.Info("VANGUARD_RAID_SPAWN_STATUS", $"spawn queued source={source}");
#endif
    }

    public static void Tick()
    {
#if SPT_CLIENT
        bool shouldStart;
        lock (Sync)
        {
            shouldStart = spawnQueued && !spawnRunning;
            if (shouldStart)
            {
                spawnQueued = false;
                spawnRunning = true;
            }
        }

        if (!shouldStart)
        {
            return;
        }

        var context = SynchronizationContext.Current;
        _ = SpawnQueuedAsync(context);
#endif
    }

#if SPT_CLIENT
    private static async Task SpawnQueuedAsync(SynchronizationContext? context)
    {
        try
        {
            if (!manifestPrimed)
            {
                PrimeFromRaidPlayers("spawn_lazy_prime");
            }

            if (context is null)
            {
                VanguardClientDiagnosticsLog.Warning("VANGUARD_RAID_SPAWN_STATUS", "spawn aborted reason=no_unity_synchronization_context");
                return;
            }

            await SpawnService.SpawnPendingOperatorsAsync(context);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Error("VANGUARD_RAID_SPAWN_STATUS", exception);
        }
        finally
        {
            lock (Sync)
            {
                spawnRunning = false;
            }
        }
    }
#endif
}
