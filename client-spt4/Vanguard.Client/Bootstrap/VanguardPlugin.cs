using System;
using System.IO;
using Vanguard.Client;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.UI.OffRaid;
using Vanguard.Client.UI.OffRaid.Inventory;
using Vanguard.Client.UI.InRaid.Localization;
using Vanguard.Client.Raid.Career;
using Vanguard.Client.Raid.Patches;
using Vanguard.Client.Raid.Services;
using Vanguard.Client.Raid.Hud;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Options;

#if SPT_CLIENT
using BepInEx;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Intents;
using Vanguard.Client.Runtime.Medical.Execution;
using Vanguard.Client.Runtime.Medical;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.Movement.Brain;
using Vanguard.Client.Runtime.Loot;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Combat;
using Vanguard.Client.Runtime.PostLoot;
using Vanguard.Client.Runtime.Alliance;
using Vanguard.Client.Runtime.Weapon;
using Vanguard.Client.Runtime.Integrations.Looting;
using Vanguard.Client.Runtime.Integrations.MoreBots;
using Vanguard.Client.Runtime.Integrations.Orbit;
using Vanguard.Client.Runtime.Integrations.Sain;
using Vanguard.Client.Runtime.Grenades;
using Vanguard.Client.Raid.Runtime.Fika;
using Vanguard.Client.Runtime.External;
using Vanguard.Client.Runtime.TacticalAuthoring;
#endif

// Responsibility: composes Vanguard client services, patches and raid lifecycle ticks after SPT.Custom/Fika integration has established the host environment.
// Flow: The host loader registers dependencies/patches/services once, then hands ongoing behavior to dedicated runtime/domain components.
// Authority boundary: bootstrap wires subsystem order only; each runtime service retains its own combat, movement, medical, loot, HUD or persistence authority contract.
// Invariant: lifecycle reset/register/start ordering must remain deterministic so no recurrent subsystem observes a stale Operator registry across raids.

namespace Vanguard.Client.Bootstrap;

#if SPT_CLIENT
[BepInPlugin(PluginGuid, PluginName, VanguardBuildVersion.Value)]
[BepInDependency("com.morebotsapi.tacticaltoaster", BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency("me.sol.sain", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("com.chazut.orbit", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("me.skwizzy.lootingbots", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("com.SPT.custom", BepInDependency.DependencyFlags.SoftDependency)]
// Menu Overhaul is optional. The soft dependency gives BepInEx a deterministic load-order hint when
// both plugins are present, while Vanguard still compiles and runs without referencing its assembly.
[BepInDependency(Vanguard.Client.Compatibility.VanguardMenuOverhaulCompat.PluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
public sealed class VanguardPlugin : BaseUnityPlugin
#else
public sealed class VanguardPlugin
#endif
{
    public const string PluginGuid = "com.hiban.vanguard";
    private const string LegacyPluginGuid = "com.hounderd.vanguard.client";
    public const string PluginName = "Vanguard";

#if SPT_CLIENT
    private void Awake()
#else
    public void Awake()
#endif
    {
        try
        {
            VanguardClientDiagnosticsLog.Startup(
                $"Vanguard client loaded; version={VanguardBuildVersion.Value}");
#if SPT_CLIENT
            TryMigrateLegacyBepInExConfig();
#endif
            VanguardOrbitRoleExclusionBootstrap.RegisterOrDefer("client_awake");
#if SPT_CLIENT
            VanguardOperatorRuntimeAuditOptions.Bind(Config);
            VanguardTacticalAuthoringOptions.Bind(Config);
            VanguardOperatorRuntimeAuditSyncService.Initialize();
            VanguardClientDiagnosticsLog.Operational(
                "VANGUARD_STARTUP",
                () => $"Runtime services configured; operators=true; tactical=true; medical=true; loot=true; hud=true; integrations=MoreBots_required,SAIN_optional,ORBIT_optional,LootingBots_optional,SPT.Custom_optional; build={VanguardBuildVersion.BuildLabel}");
            VanguardReturnMovementBigBrainBootstrap.RegisterSafe();
            EnablePatch("VanguardOffRaidMenuPatch", () => new VanguardOffRaidMenuPatch().Enable());
            EnablePatch("VanguardOperatorInventoryGetProfilesPatch", () => new VanguardOperatorInventoryGetProfilesPatch().Enable());
            EnablePatch("VanguardOperatorInventoryProfileRebindPatch", () => new VanguardOperatorInventoryProfileRebindPatch().Enable());
            EnablePatch("VanguardOperatorEditBuildControllerPatch", () => new VanguardOperatorEditBuildControllerPatch().Enable());
            EnablePatch("VanguardOperatorEquipmentBuildsControllerPatch", () => new VanguardOperatorEquipmentBuildsControllerPatch().Enable());
            EnablePatch("VanguardOperatorInventoryScreenReturnPatch", () => new VanguardOperatorInventoryScreenReturnPatch().Enable());
            EnablePatch("VanguardOperatorInventoryMenuGuardPatch", () => new VanguardOperatorInventoryMenuGuardPatch().Enable());
            EnablePatch("VanguardFikaBotDifficultiesFallbackPatch", () => new VanguardFikaBotDifficultiesFallbackPatch().Enable());
            EnablePatch("VanguardSptCustomAiVanguardRoleBypassPatch", () => new VanguardSptCustomAiVanguardRoleBypassPatch().Enable());
            EnablePatch("VanguardSainAutonomousExtractVetoPatch", () => new VanguardSainAutonomousExtractVetoPatch().Enable());
            EnablePatch("VanguardSainExtractLayerIsActiveVetoPatch", () => new VanguardSainExtractLayerIsActiveVetoPatch().Enable());
            EnablePatch("VanguardSainPeacefulLayerIsActiveVetoPatch", () => new VanguardSainPeacefulLayerIsActiveVetoPatch().Enable());
            EnablePatch("VanguardNativePmcExfiltrationLayerVetoPatch", () => new VanguardNativePmcExfiltrationLayerVetoPatch().Enable());
            EnablePatch("VanguardSainExtractTimeUpdateVetoPatch", () => new VanguardSainExtractTimeUpdateVetoPatch().Enable());
            EnablePatch("VanguardSainOperatorSearchTimingPatch", () => new VanguardSainOperatorSearchTimingPatch().Enable());
            EnablePatch("VanguardFikaOperatorDogtagGuardPatch", () => new VanguardFikaOperatorDogtagGuardPatch().Enable());
            EnablePatch("VanguardRaidStartPatch", () => new VanguardRaidStartPatch().Enable());
            EnablePatch("VanguardRaidPersistenceLocalStopPatch", () => new VanguardRaidPersistenceLocalStopPatch().Enable());
            EnablePatch("VanguardCareerEventTruthKillPatch", () => new VanguardCareerEventTruthKillPatch().Enable());
            EnablePatch("VanguardCareerXpShadowKillCreditPatch", () => new VanguardCareerXpShadowKillCreditPatch().Enable());
            EnablePatch("VanguardOperatorWeaponSkillAcquisitionPatch", () => new VanguardOperatorWeaponSkillAcquisitionPatch().Enable());
            if (Vanguard.Client.Compatibility.VanguardFikaCompat.IsInstalled)
            {
                EnablePatch("VanguardRaidPersistenceFikaStopPatch", () => new VanguardRaidPersistenceFikaStopPatch().Enable());
                if (VanguardRaidPersistenceFikaHeadlessStopPatch.IsRuntimeTypeAvailable)
                {
                    EnablePatch("VanguardRaidPersistenceFikaHeadlessStopPatch", () => new VanguardRaidPersistenceFikaHeadlessStopPatch().Enable());
                }
            }
            EnablePatch("VanguardCorpseRegistrationPatch", () => new VanguardCorpseRegistrationPatch().Enable());
            EnablePatch("VanguardOwnerImmediateThreatPatch", () => new VanguardOwnerImmediateThreatPatch().Enable());
            EnablePatch("VanguardBattleInputNodeReleasePatch", () => new VanguardBattleInputNodeReleasePatch().Enable());
            EnablePatch("VanguardBotsControllerStatePatch", () => new VanguardBotsControllerStatePatch().Enable());
            EnablePatch("VanguardBotsEventsControllerSpawnPatch", () => new VanguardBotsEventsControllerSpawnPatch().Enable());
            EnablePatch("VanguardOperatorGroupEnemySyncPatch", () => new VanguardOperatorGroupEnemySyncPatch().Enable());
            EnablePatch("VanguardBotMemoryFriendlyGuardPatch", () => new VanguardBotMemoryFriendlyGuardPatch().Enable());
            EnablePatch("VanguardBotEnemiesControllerFriendlyGuardPatch", () => new VanguardBotEnemiesControllerFriendlyGuardPatch().Enable());
            EnablePatch("VanguardBotsGroupFriendlyEnemyCheckPatch", () => new VanguardBotsGroupFriendlyEnemyCheckPatch().Enable());
            EnablePatch("VanguardShootDataFriendlyCorridorPatch", () => new VanguardShootDataFriendlyCorridorPatch().Enable());
            EnablePatch("VanguardShootDataBurstFriendlyCorridorPatch", () => new VanguardShootDataBurstFriendlyCorridorPatch().Enable());
            EnablePatch("VanguardActualProjectileFriendlyCorridorPatch", () => new VanguardActualProjectileFriendlyCorridorPatch().Enable());
            EnablePatch("VanguardShootDataProductionDiagnosticsPatch", () => new VanguardShootDataProductionDiagnosticsPatch().Enable());
            EnablePatch("VanguardShootDataTriggerDiagnosticsPatch", () => new VanguardShootDataTriggerDiagnosticsPatch().Enable());
            EnablePatch("VanguardInitiateShotProductionDiagnosticsPatch", () => new VanguardInitiateShotProductionDiagnosticsPatch().Enable());
            EnablePatch("VanguardSainVisionCreateCommandsDiagnosticsPatch", () => new VanguardSainVisionCreateCommandsDiagnosticsPatch().Enable());
            EnablePatch("VanguardSainVisionAnalyzeHitsDiagnosticsPatch", () => new VanguardSainVisionAnalyzeHitsDiagnosticsPatch().Enable());
            EnablePatch("VanguardSainBotLookDiagnosticsPatch", () => new VanguardSainBotLookDiagnosticsPatch().Enable());
            EnablePatch("VanguardSainGrenadeFriendlyRadiusPatch", () => new VanguardSainGrenadeFriendlyRadiusPatch().Enable());
            EnablePatch("VanguardSainGrenadeThrownDiagnosticPatch", () => new VanguardSainGrenadeThrownDiagnosticPatch().Enable());
            EnablePatch("VanguardSainGrenadeExplosionDiagnosticPatch", () => new VanguardSainGrenadeExplosionDiagnosticPatch().Enable());
            EnablePatch("VanguardSainGrenadeCollisionDiagnosticPatch", () => new VanguardSainGrenadeCollisionDiagnosticPatch().Enable());
            EnablePatch("VanguardSainGrenadeReactionDiagnosticPatch", () => new VanguardSainGrenadeReactionDiagnosticPatch().Enable());
            EnablePatch("VanguardSainGrenadeDangerUpdateDiagnosticPatch", () => new VanguardSainGrenadeDangerUpdateDiagnosticPatch().Enable());
            EnablePatch("VanguardSainGrenadeTrackerSpottedDiagnosticPatch", () => new VanguardSainGrenadeTrackerSpottedDiagnosticPatch().Enable());
            EnablePatch("VanguardSainGrenadeTrackerUpdateDiagnosticPatch", () => new VanguardSainGrenadeTrackerUpdateDiagnosticPatch().Enable());
            EnablePatch("VanguardNativeGrenadeDangerDiagnosticPatch", () => new VanguardNativeGrenadeDangerDiagnosticPatch().Enable());
            EnablePatch("VanguardNativeGrenadeShallRunAwayDiagnosticPatch", () => new VanguardNativeGrenadeShallRunAwayDiagnosticPatch().Enable());
            EnablePatch("VanguardNativeGrenadeExecutionDiagnosticPatch", () => new VanguardNativeGrenadeExecutionDiagnosticPatch().Enable());
            EnablePatch("VanguardSainGrenadeDecisionDiagnosticPatch", () => new VanguardSainGrenadeDecisionDiagnosticPatch().Enable());
#endif
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Error("VANGUARD_RUNTIME_ERROR", exception);
        }
    }


#if SPT_CLIENT
    private void Update()
    {
        DateTimeOffset lifecycleNow = DateTimeOffset.UtcNow;
        VanguardHeadlessGcPolicyService.Tick();
        VanguardHeadlessMemoryTelemetryService.Tick(lifecycleNow);

        if (VanguardHeadlessPostRaidQuiescenceService.IsActive)
        {
            return;
        }

        try
        {
            VanguardTacticalAuthoringService.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.TacticalAuthoringStatusTag,
                $"tactical authoring tick failed closed: {exception.GetType().Name}: {exception.Message}; gameplayUnaffected=true; runtimeConsumption=false");
        }

        try
        {
            VanguardTacticalAuthoredZoneOccupancyService.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardTacticalAuthoredZoneOccupancyService.StatusTag,
                $"automatic authored-zone occupancy publisher tick failed: {exception.GetType().Name}: {exception.Message}; failOpen=true; normalVanguardBehaviorPreserved=true");
        }

        try
        {
            VanguardTacticalAuthoringLiveSyncService.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardTacticalAuthoringLiveSyncService.StatusTag,
                $"tactical authoring live sync tick failed: {exception.GetType().Name}: {exception.Message}; failOpen=true; persistedRuntimeConsumption=false");
        }

        DateTimeOffset frameNow = DateTimeOffset.UtcNow;
        VanguardRuntimeFrameBudgetGuard.BeginFrame(frameNow);
        VanguardHeadlessRuntimeStallGuard.BeginFrame(frameNow);
        long updateStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
        long preSnapshotServicesStarted = VanguardRuntimePerformanceGuard.Begin();
        long raidRegistrationStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            // Order invariant: spawn/register first. Runtime audit, decision snapshots
            // and any future brain brick must only see Operators after BotOwner binding.
            VanguardRaidOperatorController.Tick();
            VanguardRuntimeBindGuardService.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.RaidSpawnStatusTag, $"raid spawn tick failed: {exception}; canonicalTag={VanguardPrimaryExecutionContract.SpawnDiagnosticsStatusTag}");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("PreSnapshotRaidRegistration", raidRegistrationStarted);
        }

        long operatorSkillAcquisitionStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            // Runtime invariant: bind diagnostic physical-skill observers only after the canonical
            // Operator registry is current. The service never mutates Strength/Endurance.
            VanguardOperatorSkillAcquisitionParityService.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardOperatorSkillAcquisitionParityService.PhysicalInstrumentationStatusTag,
                $"skill acquisition compatibility skill acquisition instrumentation tick failed: {exception.GetType().Name}: {exception.Message}; failOpen=true; gameplayUnaffected=true; strengthMutation=false; enduranceMutation=false");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("PreSnapshotSkillAcquisitionParity", operatorSkillAcquisitionStarted);
        }

        long operatorSkillPhysicalFoundationStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            // The runtime scheduler starts after diagnostic observers are bound so restored SprintAction/MovementAction
            // emissions remain visible to the already runtime-validated diagnostics.
            VanguardOperatorSkillAndPhysicalAcquisitionFoundationService.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardOperatorSkillAndPhysicalAcquisitionFoundationService.StatusTag,
                $"skill acquisition compatibility skill/physical acquisition foundation tick failed: {exception.GetType().Name}: {exception.Message}; failOpen=true; gameplayUnaffected=true; physicalGameplayEnforcement=false");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("PreSnapshotSkillPhysicalAcquisitionFoundation", operatorSkillPhysicalFoundationStarted);
        }

        long sainStaticProfileStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            // integration subsystem calibration invariant: after the canonical runtime bind, every Operator receives
            // one independent Normal settings clone before snapshots or behavior consumers run.
            VanguardSainStaticProfileService.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardSainStaticProfilePolicy.StatusTag,
                $"static SAIN profile tick failed: {exception.GetType().Name}: {exception.Message}; failOpen=true; spawnUnaffected=true");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("PreSnapshotSainStaticProfile", sainStaticProfileStarted);
        }

        long orbitExclusionStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            VanguardOrbitRoleExclusionBootstrap.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardOperatorBotTypes.OrbitBoundaryStatusTag, $"orbit role exclusion tick failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("PreSnapshotOrbitRoleExclusion", orbitExclusionStarted);
        }

        long botTypeDiagnosticsStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            VanguardOperatorBotTypeDiagnosticsService.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardOperatorBotTypes.StatusTag, $"operator bot type diagnostics tick failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("PreSnapshotBotTypeDiagnostics", botTypeDiagnosticsStarted);
        }

        long coopBinderStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            VanguardCoopStructuralSquadBinder.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.CoopStructuralSquadBindStatusTag, $"coop structural squad bind tick failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("PreSnapshotCoopStructuralBinder", coopBinderStarted);
        }

        long friendlyTargetGuardStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            VanguardOperatorFriendlyTargetGuard.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.CoopFriendlyTargetGuardStatusTag, $"coop friendly target guard tick failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("PreSnapshotFriendlyTargetGuard", friendlyTargetGuardStarted);
        }

        long auditSyncStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            // F12/server config sync is safe before the raid load guard opens because
            // it never reads GameWorld, players, BotOwner, SAIN, BigBrain, LootingBots or ORBIT.
            VanguardOperatorRuntimeAuditSyncService.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.AuditSettingsSyncStatusTag, $"operator runtime audit settings sync tick failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("PreSnapshotAuditSync", auditSyncStarted);
        }

        if (VanguardOperatorRuntimeAuditSyncService.EffectiveEnabled &&
            VanguardRuntimeFrameBudgetGuard.ShouldRunOptional("RuntimeAudit", frameNow, TimeSpan.FromSeconds(2.0d), out _))
        {
            try
            {
                VanguardOperatorRuntimeAuditService.Tick();
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.OperatorRuntimeAuditStatusTag, $"operator runtime audit tick failed: {exception.GetType().Name}: {exception.Message}");
            }
        }

        try
        {
            // Wishlist sync is event-like by content hash and performs network I/O on a background task.
            // Player clients read only their own EFT Profile.WishlistManager; headless pulls only
            // OwnerProfileIds already known by the Vanguard raid registry.
            VanguardHandbookPriceCache.Tick();
            VanguardOwnerLootInterestSyncService.Tick();
            // World-container discovery uses one central cached read-only pass over EFT GameWorld.LootList. No per-Operator
            // physics scan and no scoring/opening/claim/transaction authority are activated here.
            VanguardWorldLootContainerReadModelService.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.UnifiedOpportunisticLootReadModelStatusTag, $"owner loot interest sync tick failed: {exception.GetType().Name}: {exception.Message}");
        }

        VanguardRuntimePerformanceGuard.End("PreSnapshotServices", preSnapshotServicesStarted);
        VanguardRuntimeFrameBudgetGuard.MarkMandatory("PreSnapshotServices", preSnapshotServicesStarted);

        long decisionSnapshotStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            // audit subsystem: typed post-spawn snapshot foundation. It is read-only and is the
            // first per-Operator brain brick after validated BotOwner registration.
            VanguardOperatorDecisionSnapshotService.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.OperatorDecisionSnapshotStatusTag, $"operator decision snapshot tick failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("DecisionSnapshot", decisionSnapshotStarted);
            VanguardRuntimeFrameBudgetGuard.MarkMandatory("DecisionSnapshot", decisionSnapshotStarted);
        }

        var latestDecisionSnapshots = VanguardOperatorDecisionSnapshotService.GetLatestSnapshots();

        long hudTelemetryStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            // HUD-001B: authority-only semantic telemetry. The bridge never mutates AI state and
            // clients only cache a presentation-neutral read model. Fika remains an optional
            // runtime dependency because the transport binding itself is reflection-based/fail-open.
            VanguardFikaHudTelemetryService.Tick(latestDecisionSnapshots, frameNow);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardFikaHudTelemetryService.StatusTag,
                $"HUD telemetry tick failed fail-open: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("HudFikaTelemetry", hudTelemetryStarted);
        }

        long grenadeHazardDiagnosticStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            VanguardGrenadeHazardAuditService.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardGrenadeHazardPolicy.StatusTag,
                () => $"grenade hazard diagnostic tick failed: {exception.GetType().Name}: {exception.Message}; failOpen=true; gameplayUnaffected=true");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("GrenadeHazardDiagnostic", grenadeHazardDiagnosticStarted);
        }

        long grenadeEmergencyStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            VanguardGrenadeEmergencyEvasionService.Tick(latestDecisionSnapshots, DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardGrenadeEmergencyPolicy.StatusTag,
                () => $"grenade emergency evasion tick failed: {exception.GetType().Name}: {exception.Message}; failOpen=true; nativeBrainUnaffected=true");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("GrenadeEmergencyEvasion", grenadeEmergencyStarted);
            VanguardRuntimeFrameBudgetGuard.MarkMandatory("GrenadeEmergencyEvasion", grenadeEmergencyStarted);
        }

        long corpseLootTelemetryStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            VanguardCorpseLootOperationalTelemetry.Observe(latestDecisionSnapshots, DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardCorpseRegistry.StatusTag, () => $"the runtime corpse loot operational telemetry failed: {exception.GetType().Name}: {exception.Message}; failOpen=true; gameplayUnaffected=true");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("CorpseLootOperationalTelemetry", corpseLootTelemetryStarted);
        }
        long sainExtractGuardStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            // Runtime invariant: remove SAIN's generic PMC exfil capability before the scheduler sees the
            // immutable authority state. This is a narrow Operator-only veto; SAIN remains the
            // individual combat owner and no Vanguard exfil intent is fabricated.
            VanguardSainAutonomousExtractGuardService.Tick(latestDecisionSnapshots, DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardAuthorityCircuitBreakerStatusTags.SainAutonomousExtractVeto, $"SAIN autonomous extract guard tick failed: {exception.GetType().Name}: {exception.Message}; Tag={VanguardCombatTruthStatusTags.ExtractGuardOneShot}");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("SainAutonomousExtractGuard", sainExtractGuardStarted);
            VanguardRuntimeFrameBudgetGuard.MarkMandatory("SainAutonomousExtractGuard", sainExtractGuardStarted);
        }

        long schedulerStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            // Vanguard: the scheduler arbitrates immediately after the immutable snapshot.
            // Observer/watchdog services run only after the primary authority has been opened,
            // so they cannot preempt SAIN or start a competing movement/medical driver first.
            VanguardMainIntentScheduler.Tick(latestDecisionSnapshots, DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.MainSchedulerStatusTag, $"main intent scheduler tick failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("MainScheduler", schedulerStarted);
            VanguardRuntimeFrameBudgetGuard.MarkMandatory("MainScheduler", schedulerStarted);
        }

        long tacticalAuthoringPreviewStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            // Only the Fika headless authority consumes the transient live preview. It runs
            // after scheduler arbitration so grenade/combat/medical windows remain authoritative,
            // and before normal movement executors so an admitted preview window blocks them.
            VanguardTacticalAuthoringHeadlessPreviewService.Tick(frameNow);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardTacticalAuthoringHeadlessPreviewService.StatusTag,
                $"tactical authoring headless preview tick failed: {exception.GetType().Name}: {exception.Message}; failOpen=true; persistedRuntimeConsumption=false");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("TacticalAuthoringHeadlessPreview", tacticalAuthoringPreviewStarted);
        }

        long watchdogsStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            try
            {
                VanguardStaleSainExitService.Tick();
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(VanguardMovementAuthorityDoctrine.HostileIndoorMovementPlanStatusTag, $"stale SAIN observation tick failed: {exception.GetType().Name}: {exception.Message}");
            }

            try
            {
                if (VanguardRuntimeFrameBudgetGuard.ShouldRunOptional("CombatNoFireWatchdog", frameNow, TimeSpan.FromSeconds(0.75d), out _))
                {
                    VanguardCombatNoFireWatchdogService.Tick();
                }
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(VanguardMovementAuthorityDoctrine.CombatBindCohesionRecoveryStatusTag, $"combat no-fire observation tick failed: {exception.GetType().Name}: {exception.Message}");
            }

            long globalCombatDiagnosticsStarted = 0L;
            try
            {
                if (VanguardGlobalCombatProductionDiagnosticsService.FireBoundariesEnabled &&
                    VanguardRuntimeFrameBudgetGuard.ShouldRunOptional("GlobalCombatProductionDiagnostics", frameNow, TimeSpan.FromSeconds(0.80d), out _))
                {
                    globalCombatDiagnosticsStarted = VanguardRuntimePerformanceGuard.Begin();
                    VanguardGlobalCombatProductionDiagnosticsService.Tick();
                }
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(VanguardGlobalCombatProductionDiagnosticsService.StatusTag, $"global combat production diagnostics tick failed: {exception.GetType().Name}: {exception.Message}; mutation=false");
            }
            finally
            {
                if (globalCombatDiagnosticsStarted > 0L)
                {
                    VanguardRuntimePerformanceGuard.End("GlobalCombatProductionDiagnostics", globalCombatDiagnosticsStarted);
                }
            }
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("PostSchedulerWatchdogs", watchdogsStarted);
            VanguardRuntimeFrameBudgetGuard.MarkMandatory("PostSchedulerWatchdogs", watchdogsStarted);
        }

        if (VanguardOperatorRuntimeAuditOptions.GetIntentDryRunEnabled() &&
            VanguardRuntimeFrameBudgetGuard.ShouldRunOptional("IntentDryRun", frameNow, TimeSpan.FromSeconds(2.0d), out _))
        {
            long intentDryRunStarted = VanguardRuntimePerformanceGuard.Begin();
            try
            {
                // Diagnostic candidate board only. The active scheduler has already consumed the snapshot.
                VanguardOperatorIntentDryRunService.Tick();
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.OperatorIntentDryRunStatusTag, $"operator intent dry-run tick failed: {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                VanguardRuntimePerformanceGuard.End("IntentDryRun", intentDryRunStarted);
            }
        }

        bool awarenessUrgent = HasUrgentAwarenessWork(latestDecisionSnapshots);
        if (awarenessUrgent || VanguardRuntimeFrameBudgetGuard.ShouldRunOptional("AwarenessBridge", frameNow, TimeSpan.FromSeconds(1.0d), out _))
        {
            long awarenessStarted = VanguardRuntimePerformanceGuard.Begin();
            try
            {
                VanguardCombatAwarenessBridge.Tick();
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(VanguardCombatAwarenessBridge.StatusTag, $"combat awareness bridge tick failed: {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                VanguardRuntimePerformanceGuard.End("AwarenessBridge", awarenessStarted);
                VanguardRuntimeFrameBudgetGuard.MarkMandatory("AwarenessBridge", awarenessStarted);
            }
        }

        long medicalStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            VanguardMobileMedicalLeaseExecutor.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.MobileMedicalLeaseStatusTag, $"mobile medical lease tick failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("MedicalExecutor", medicalStarted);
            VanguardRuntimeFrameBudgetGuard.MarkMandatory("MedicalExecutor", medicalStarted);
        }

        long corpseLootApproachStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            VanguardCorpseLootApproachExecutor.Tick(latestDecisionSnapshots, DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardCorpseLootApproachExecutor.StatusTag, $"corpse loot approach executor tick failed: {exception.GetType().Name}: {exception.Message}; failOpen=true; inventoryMutation=false");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("CorpseLootApproach", corpseLootApproachStarted);
            VanguardRuntimeFrameBudgetGuard.MarkMandatory("CorpseLootApproach", corpseLootApproachStarted);
        }

        long containerLootApproachStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            VanguardWorldLootContainerApproachExecutor.Tick(latestDecisionSnapshots, DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardWorldLootContainerApproachDoctrine.StatusTag, $"world container loot approach executor tick failed: {exception.GetType().Name}: {exception.Message}; failClosed=true; itemMutation=false");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("WorldContainerLootApproach", containerLootApproachStarted);
            VanguardRuntimeFrameBudgetGuard.MarkMandatory("WorldContainerLootApproach", containerLootApproachStarted);
        }

        long travelCohesionStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            VanguardSquadTravelCohesionExecutor.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardSquadTravelCohesionExecutor.StatusTag, $"squad travel cohesion executor tick failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("TravelCohesion", travelCohesionStarted);
            VanguardRuntimeFrameBudgetGuard.MarkMandatory("TravelCohesion", travelCohesionStarted);
        }

        long hardReturnStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            VanguardHardReturnMovementExecutor.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.MovementHardReturnActiveStatusTag, $"hard-return movement executor tick failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("HardReturn", hardReturnStarted);
            VanguardRuntimeFrameBudgetGuard.MarkMandatory("HardReturn", hardReturnStarted);
        }

        long closeCohesionStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            VanguardCloseCohesionExecutor.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardCloseCohesionExecutor.StatusTag, $"close cohesion executor tick failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("CloseCohesion", closeCohesionStarted);
            VanguardRuntimeFrameBudgetGuard.MarkMandatory("CloseCohesion", closeCohesionStarted);
        }

        long cohesionClaimsStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            VanguardSquadCohesionClaimExecutor.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardSquadCohesionClaimExecutor.StatusTag, $"squad cohesion claim executor tick failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("CohesionClaims", cohesionClaimsStarted);
            VanguardRuntimeFrameBudgetGuard.MarkMandatory("CohesionClaims", cohesionClaimsStarted);
        }

        long tacticalRepositionStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            VanguardTacticalRepositionExecutor.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardTacticalRepositionExecutor.StatusTag, $"tactical reposition executor tick failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("TacticalReposition", tacticalRepositionStarted);
            VanguardRuntimeFrameBudgetGuard.MarkMandatory("TacticalReposition", tacticalRepositionStarted);
        }

        if (VanguardRuntimeFrameBudgetGuard.ShouldRunOptional("LootBroker", frameNow, TimeSpan.FromSeconds(1.5d), out _))
        {
            long lootBrokerStarted = VanguardRuntimePerformanceGuard.Begin();
            try
            {
                VanguardOpportunisticLootBroker.Tick();
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(VanguardOpportunisticLootBroker.StatusTag, $"opportunistic loot broker tick failed: {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                VanguardRuntimePerformanceGuard.End("LootBroker", lootBrokerStarted);
            }
        }

        if (VanguardRuntimeFrameBudgetGuard.ShouldRunOptional("PostLootAudit", frameNow, TimeSpan.FromSeconds(2.5d), out _))
        {
            long postLootAuditStarted = VanguardRuntimePerformanceGuard.Begin();
            try
            {
                VanguardPostLootCombatReadinessAuditService.Tick();
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.PostLootCombatReadinessAuditStatusTag, $"post-loot combat readiness audit tick failed: {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                VanguardRuntimePerformanceGuard.End("PostLootAudit", postLootAuditStarted);
            }
        }

        if (VanguardRuntimeFrameBudgetGuard.ShouldRunOptional("WeaponHandsAudit", frameNow, TimeSpan.FromSeconds(2.5d), out _))
        {
            long weaponHandsAuditStarted = VanguardRuntimePerformanceGuard.Begin();
            try
            {
                VanguardWeaponHandsCombatAuditService.Tick();
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.WeaponHandsAuditStatusTag, $"weapon hands combat audit tick failed: {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                VanguardRuntimePerformanceGuard.End("WeaponHandsAudit", weaponHandsAuditStarted);
            }
        }

        if (VanguardRuntimeFrameBudgetGuard.ShouldRunOptional("RaidHud", frameNow, TimeSpan.FromSeconds(0.75d), out _))
        {
            long raidHudStarted = VanguardRuntimePerformanceGuard.Begin();
            try
            {
                VanguardRaidOperatorHudService.Tick(this);
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.OperatorHudStatusTag, $"raid HUD tick failed: {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                VanguardRuntimePerformanceGuard.End("RaidHud", raidHudStarted);
            }
        }

        long battleInputReleaseStarted = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            VanguardBattleInputNodeReleaseService.Tick();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag, $"battle input node release tick failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("BattleInputRelease", battleInputReleaseStarted);
            VanguardRuntimeFrameBudgetGuard.MarkMandatory("BattleInputRelease", battleInputReleaseStarted);
        }
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("PluginUpdateTotal", updateStarted);
            VanguardRuntimeFrameBudgetGuard.MarkMandatory("PluginUpdateTotal", updateStarted);
            VanguardHeadlessRuntimeStallGuard.EndFrame(DateTimeOffset.UtcNow);
        }
    }

    private void OnGUI()
    {
        if (VanguardHeadlessPostRaidQuiescenceService.IsActive)
        {
            return;
        }

        try
        {
            VanguardTacticalAuthoringService.DrawGui();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.TacticalAuthoringStatusTag,
                $"tactical authoring GUI failed closed: {exception.GetType().Name}: {exception.Message}; gameplayUnaffected=true");
        }
    }

    private void OnRenderObject()
    {
        if (VanguardHeadlessPostRaidQuiescenceService.IsActive)
        {
            return;
        }

        try
        {
            VanguardTacticalAuthoringService.RenderWorld();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.TacticalAuthoringStatusTag,
                $"tactical authoring visualization failed closed: {exception.GetType().Name}: {exception.Message}; gameplayUnaffected=true");
        }
    }

    private void OnDestroy()
    {
        VanguardTacticalAuthoringLiveSyncService.Reset("plugin_destroy");
        VanguardTacticalAuthoringService.Shutdown();
    }

    private static bool HasUrgentAwarenessWork(System.Collections.Generic.IReadOnlyList<OperatorDecisionSnapshot> snapshots)
    {
        if (snapshots == null)
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (var snapshot in snapshots)
        {
            if (snapshot == null || !snapshot.Alive)
            {
                continue;
            }

            bool targetSpecificProjectileEvidence = VanguardNearMissSuppressionService.IsRecent(snapshot.BotProfileId, now, out _);
            bool recentOwnerShotEvidence = VanguardOwnerShotMemoryService.TryGetRecentShot(snapshot.OwnerProfileId, now, out _);
            if (targetSpecificProjectileEvidence
                || recentOwnerShotEvidence
                || snapshot.Threat.DirectThreat
                || snapshot.Threat.EnemyVisible == true
                || snapshot.Threat.EnemyCanShoot == true
                || snapshot.Awareness.CandidateVisible
                || snapshot.Awareness.CandidateLineOfSight
                || snapshot.Awareness.CandidateCanShoot
                || snapshot.ThreatScan.CandidateVisible
                || snapshot.ThreatScan.CandidateLineOfSight
                || snapshot.ThreatScan.CandidateCanShoot
                || snapshot.Medical.Safety.IncomingFireRecent
                || snapshot.Awareness.IncomingFireFresh
                || snapshot.ThreatScan.CandidateIncomingFireFresh
                || snapshot.Threat.ShotMeRecently == true
                || snapshot.Threat.ShotAtMeRecently == true)
            {
                return true;
            }
        }

        return false;
    }
#endif

#if SPT_CLIENT
    private void TryMigrateLegacyBepInExConfig()
    {
        try
        {
            string bepinExRoot = BepInEx.Paths.BepInExRootPath;
            if (string.IsNullOrWhiteSpace(bepinExRoot))
            {
                throw new InvalidOperationException("BepInEx root path is unavailable; legacy Vanguard configuration migration cannot be resolved safely.");
            }

            string configRoot = Path.Combine(bepinExRoot, "config");
            string legacyPath = Path.Combine(configRoot, LegacyPluginGuid + ".cfg");
            string currentPath = Path.Combine(configRoot, PluginGuid + ".cfg");
            if (!File.Exists(legacyPath) || File.Exists(currentPath))
            {
                return;
            }

            File.Copy(legacyPath, currentPath, overwrite: false);
            var reloadMethod = Config.GetType().GetMethod("Reload", Type.EmptyTypes);
            if (reloadMethod == null)
            {
                throw new MissingMethodException(Config.GetType().FullName, "Reload");
            }

            reloadMethod.Invoke(Config, null);
            VanguardClientDiagnosticsLog.Operational(
                "VANGUARD_CONFIG_MIGRATION",
                () => $"Legacy Vanguard BepInEx configuration migrated to {Path.GetFileName(currentPath)}; source retained unchanged.");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_CONFIG_MIGRATION",
                $"legacy BepInEx configuration migration skipped: {exception.GetType().Name}: {exception.Message}; action=continue_with_current_config");
        }
    }

    private static void EnablePatch(string patchName, Action enable)
    {
        try
        {
            enable();
            VanguardClientDiagnosticsLog.Diagnostic(
                VanguardBuildVersion.ClientBootStatusTag,
                () => $"client patch enabled: {patchName}");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.ClientBootStatusTag,
                $"client patch not enabled: {patchName}; reason={exception.GetType().Name}: {exception.Message}");
        }
    }
#endif
}
