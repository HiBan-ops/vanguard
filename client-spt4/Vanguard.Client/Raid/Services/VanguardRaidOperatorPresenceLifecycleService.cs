#if SPT_CLIENT
using System;
using EFT;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Career;
using Vanguard.Client.Raid.Persistence;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Authority;
using Vanguard.Client.Runtime.External;
using Vanguard.Client.Runtime.Combat;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Alliance;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Grenades;
using Vanguard.Client.Runtime.Integrations.Sain;
using Vanguard.Client.Runtime.Loot;
using Vanguard.Client.Runtime.Medical.Execution;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.Movement.Brain;
using Vanguard.Client.Runtime.TacticalAuthoring;
using Vanguard.Client.Runtime.SquadCohesion;

// Responsibility: Seals one exact Operator runtime incarnation at a definitive BotOwner lifetime boundary mid-raid.
// Flow: An exact BotOwner removal/destruction boundary proves the disappearing incarnation, captures last-safe
// living persistence truth without committing it, drains physical/authority state while the object is available,
// invalidates snapshot/object-reference caches, then detaches only that exact runtime incarnation.
// Authority boundary: This service never decides that a network peer disconnect means despawn and never
// resets squad-level intent globally. Bots.OnBotRemove is primary; RemoveFromMap/Dispose are exact fallbacks.
// Invariant: ProfileId alone can never retire a runtime incarnation; exact BotOwner + incarnation are
// established first, stale physical state cannot resume, and persistence metadata survives the detach.
namespace Vanguard.Client.Raid.Services;

internal static class VanguardRaidOperatorPresenceLifecycleService
{
    public const string StatusTag = "VANGUARD_OPERATOR_RUNTIME_PRESENCE_LIFECYCLE_STATUS";

    public static void ObserveExactRuntimePresenceLost(BotOwner? botOwner, string source)
    {
        if (botOwner == null || !VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }

        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByExactBotOwner(botOwner, out VanguardRaidOperatorRuntimeRecord record))
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string reason = "exact_runtime_presence_lost:" + Safe(source);
        string botProfileId = record.BotProfileId;
        long incarnation = record.RuntimeIncarnation;
        // Capture terminal death truth while the exact outgoing runtime object is still intact.
        // Cleanup/Dispose paths may degrade BotOwner afterwards; replacement admission must never
        // depend on a late read of that object.
        bool wasDeadAtSealStart = botOwner.IsDead;
        Player? exactPlayer = botOwner.GetPlayer;
        string persistenceCapture = RunStep(
            "living_persistence_capture",
            () => VanguardRaidOperatorPersistenceService.CaptureLivingDefinitivePresenceLossSnapshot(
                record,
                botOwner,
                exactPlayer,
                source));

        bool cleanupSucceeded = true;
        string tcDrain = RunCleanupStep(
            "tc_drain",
            () => TacticalCoordinationPhysicalAuthorityBarrier
                .DrainForPresenceLoss(botProfileId, now, reason)
                .Summary,
            ref cleanupSucceeded);
        string grenade = RunCleanupStep(
            "grenade",
            () => VanguardGrenadeEmergencyEvasionService.ForgetRuntimePresence(record, now, reason),
            ref cleanupSucceeded);
        string careerFoundation = RunCleanupStep(
            "career_foundation",
            () => VanguardOperatorSkillAndPhysicalAcquisitionFoundationService.ForgetRuntimePresence(record, exactPlayer, reason),
            ref cleanupSucceeded);
        string careerParity = RunCleanupStep(
            "career_parity",
            () => VanguardOperatorSkillAcquisitionParityService.ForgetRuntimePresence(record, exactPlayer, reason),
            ref cleanupSucceeded);
        string awareness = RunCleanupStep(
            "awareness",
            () => VanguardCombatAwarenessBridge.ForgetRuntimePresence(botProfileId, reason),
            ref cleanupSucceeded);
        string combatAuthority = RunCleanupStep(
            "combat_authority",
            () => VanguardSainSquadCombatAuthority.ForgetRuntimePresence(botProfileId, reason),
            ref cleanupSucceeded);
        string combatNoFire = RunCleanupStep(
            "combat_no_fire",
            () => VanguardCombatNoFireWatchdogService.ForgetRuntimePresence(botProfileId, reason),
            ref cleanupSucceeded);
        string nearMiss = RunCleanupStep(
            "near_miss",
            () => VanguardNearMissSuppressionService.ForgetRuntimePresence(botProfileId, reason),
            ref cleanupSucceeded);
        string staleSain = RunCleanupStep(
            "stale_sain",
            () => VanguardStaleSainExitService.ForgetRuntimePresence(botProfileId, reason),
            ref cleanupSucceeded);
        string extractGuard = RunCleanupStep(
            "extract_guard",
            () => VanguardSainAutonomousExtractGuardService.ForgetRuntimePresence(botProfileId, reason),
            ref cleanupSucceeded);
        string friendlyGuard = RunCleanupStep(
            "friendly_guard",
            () => VanguardOperatorFriendlyTargetGuard.ForgetRuntimePresence(botProfileId, reason),
            ref cleanupSucceeded);
        string hardReturn = RunCleanupStep(
            "hard_return",
            () => VanguardHardReturnMovementExecutor.ForgetRuntimePresence(botProfileId, reason),
            ref cleanupSucceeded);
        string cohesionClaim = RunCleanupStep(
            "cohesion_claim",
            () => VanguardSquadCohesionClaimExecutor.ForgetRuntimePresence(botProfileId, now, reason),
            ref cleanupSucceeded);
        string closeCohesion = RunCleanupStep(
            "close_cohesion",
            () => VanguardCloseCohesionExecutor.ForgetRuntimePresence(botProfileId, reason),
            ref cleanupSucceeded);
        string tacticalReposition = RunCleanupStep(
            "tactical_reposition",
            () => VanguardTacticalRepositionExecutor.ForgetRuntimePresence(botProfileId, reason),
            ref cleanupSucceeded);
        string travel = RunCleanupStep(
            "travel",
            () => VanguardSquadTravelCohesionExecutor.ForgetRuntimePresence(botProfileId, now, reason),
            ref cleanupSucceeded);
        string authoring = RunCleanupStep(
            "authoring",
            () => VanguardTacticalAuthoringHeadlessPreviewService.ForgetRuntimePresence(botProfileId, botOwner, now, reason),
            ref cleanupSucceeded);
        string lootRuntime = RunCleanupStep(
            "loot_runtime",
            () => VanguardOpportunisticLootBroker.ForgetRuntimePresence(botProfileId, reason),
            ref cleanupSucceeded);
        string movement = RunCleanupStep(
            "movement",
            () => VanguardReturnMovementCommandStore.ForgetRuntimePresence(botProfileId, reason),
            ref cleanupSucceeded);
        string medicalRuntime = RunCleanupStep(
            "medical_runtime",
            () => VanguardMobileMedicalLeaseExecutor.ForgetRuntimePresence(botProfileId, reason),
            ref cleanupSucceeded);
        string medicalLease = RunCleanupStep(
            "medical_lease",
            () => VanguardExecutionLeaseStore.ForgetRuntimePresence(botProfileId, reason),
            ref cleanupSucceeded);
        string authority = RunCleanupStep(
            "authority",
            () => VanguardOperatorAuthorityLeaseController.ForgetRuntimePresence(botProfileId, reason),
            ref cleanupSucceeded);
        string scheduler = RunCleanupStep(
            "scheduler",
            () => VanguardMainIntentScheduler.ForgetRuntimePresence(botProfileId, reason),
            ref cleanupSucceeded);
        string sainStatic = RunCleanupStep(
            "sain_static",
            () => "removed=" + Bool(VanguardSainStaticProfileService.ForgetRuntimePresence(botProfileId, botOwner, reason)),
            ref cleanupSucceeded);
        string snapshot = RunCleanupStep(
            "snapshot",
            () =>
            {
                VanguardOperatorDecisionSnapshotService.ForgetRuntimePresence(botProfileId, reason);
                return "forgotten=true";
            },
            ref cleanupSucceeded);

        bool detached = false;
        string detach = RunStep(
            "registry_detach",
            () =>
            {
                detached = VanguardRaidOperatorRuntimeRegistry.DetachRuntimePresenceExact(
                    botOwner,
                    incarnation,
                    wasDeadAtSealStart,
                    cleanupSucceeded,
                    reason,
                    out VanguardRaidOperatorRuntimeRecord detachedRecord);
                return detached
                    ? "detached=true;incarnation=" + detachedRecord.RuntimeIncarnation
                    : "detached=false;exact_identity_or_incarnation_changed=true";
            });

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_OPERATOR_RUNTIME_PRESENCE_SEALED operator={Safe(record.OperatorId)}; botProfile={Safe(botProfileId)}; incarnation={incarnation}; source={Safe(source)}; botWasDead={Bool(wasDeadAtSealStart)}; persistenceCapture={Safe(persistenceCapture)}; cleanupSucceeded={Bool(cleanupSucceeded)}; quarantined={Bool(!cleanupSucceeded)}; tc={Safe(tcDrain)}; grenade={Safe(grenade)}; careerFoundation={Safe(careerFoundation)}; careerParity={Safe(careerParity)}; awareness={Safe(awareness)}; combatAuthority={Safe(combatAuthority)}; combatNoFire={Safe(combatNoFire)}; nearMiss={Safe(nearMiss)}; staleSain={Safe(staleSain)}; extractGuard={Safe(extractGuard)}; friendlyGuard={Safe(friendlyGuard)}; hardReturn={Safe(hardReturn)}; cohesionClaim={Safe(cohesionClaim)}; closeCohesion={Safe(closeCohesion)}; tacticalReposition={Safe(tacticalReposition)}; travel={Safe(travel)}; authoring={Safe(authoring)}; lootRuntime={Safe(lootRuntime)}; movement={Safe(movement)}; medicalRuntime={Safe(medicalRuntime)}; medicalLease={Safe(medicalLease)}; authority={Safe(authority)}; scheduler={Safe(scheduler)}; sainStatic={Safe(sainStatic)}; snapshot={Safe(snapshot)}; registry={Safe(detach)}; runtimeDetached={Bool(detached)}; persistentRecordRetained=true; globalSquadReset=false; peerDisconnectIsNotDespawn=true; staleResume=false; implicitReplacementAllowed=false; tag={StatusTag}");
    }

    private static string RunCleanupStep(string step, Func<string> action, ref bool cleanupSucceeded)
    {
        try
        {
            return action();
        }
        catch (Exception exception)
        {
            cleanupSucceeded = false;
            string failure = "step=" + Safe(step) + ";failed=" + exception.GetType().Name + ":" + Safe(exception.Message);
            VanguardClientDiagnosticsLog.Warning(StatusTag,
                $"VANGUARD_OPERATOR_RUNTIME_PRESENCE_SEAL_STEP_FAILED {failure}; continueCleanup=true; replacementQuarantined=true; failOpenToEftRemoval=true; tag={StatusTag}");
            return failure;
        }
    }

    private static string RunStep(string step, Func<string> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception)
        {
            string failure = "step=" + Safe(step) + ";failed=" + exception.GetType().Name + ":" + Safe(exception.Message);
            VanguardClientDiagnosticsLog.Warning(StatusTag,
                $"VANGUARD_OPERATOR_RUNTIME_PRESENCE_SEAL_STEP_FAILED {failure}; continueCleanup=true; failOpenToEftRemoval=true; tag={StatusTag}");
            return failure;
        }
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private static string Bool(bool value) => value ? "true" : "false";
}
#endif
