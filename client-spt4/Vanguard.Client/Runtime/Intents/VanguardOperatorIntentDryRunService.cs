#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Coordinates Operator Intent Dry Run Service for the intent production pipeline, delegating specialized work to its collaborators.
// Flow: Current raid/runtime evidence is normalized, applicable guards and ownership rules are evaluated, then the service updates only its bounded runtime/UI responsibility.
// Authority boundary: Service coordinates its domain but does not fabricate server persistence truth or bypass higher-priority runtime authorities.
// Invariant: State is lifecycle-scoped, stale work is releasable, and failures degrade without leaving hidden long-lived ownership.
namespace Vanguard.Client.Runtime.Intents;

internal static partial class VanguardOperatorIntentDryRunService
{
    private sealed class LastIntentLogState
    {
        public string Signature = string.Empty;
        public DateTimeOffset LastTransitionAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastSummaryAtUtc = DateTimeOffset.MinValue;
    }

    private sealed class ThreatScanLogState
    {
        public string ImmediateSignature = string.Empty;
        public string LastCandidateKey = "none";
        public string LastDecision = "none";
        public string LastReason = "none";
        public DateTimeOffset LastImmediateLogAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastSummaryAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastWouldPromoteAtUtc = DateTimeOffset.MinValue;
        public string LastWouldPromoteSignature = string.Empty;
        public float LastWouldPromoteScore;
        public bool LastWouldPromoteVisible;
        public bool LastWouldPromoteLineOfSight;
        public bool LastWouldPromoteCanShoot;
        public long Scans;
        public long NoCandidate;
        public long KeepCurrent;
        public long WouldPromote;
        public long WouldPromoteLogged;
        public long WouldPromoteSuppressed;
        public long IncomingFireFresh;
        public long IncomingFireStale;
        public long VisibleCandidates;
        public long LineOfSightCandidates;
        public long CanShootCandidates;
        public long RearOrFlankCandidates;
        public long CooldownBlocked;
        public long CurrentTargetKept;

        public void ResetCounters()
        {
            Scans = 0;
            NoCandidate = 0;
            KeepCurrent = 0;
            WouldPromote = 0;
            WouldPromoteLogged = 0;
            WouldPromoteSuppressed = 0;
            IncomingFireFresh = 0;
            IncomingFireStale = 0;
            VisibleCandidates = 0;
            LineOfSightCandidates = 0;
            CanShootCandidates = 0;
            RearOrFlankCandidates = 0;
            CooldownBlocked = 0;
            CurrentTargetKept = 0;
        }
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, LastIntentLogState> LastByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ThreatScanLogState> LastThreatScanByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly IVanguardIntentProducer[] Producers =
    {
        new VanguardMedicalPlanIntentProducer(),
        new VanguardThreatScannerIntentProducer(),
        new VanguardAwarenessIntentProducer(),
        new VanguardThreatIntentProducer(),
        new VanguardCombatIntentProducer(),
        new VanguardSquadCohesionIntentProducer(),
        new VanguardMovementAuthorityIntentProducer(),
        new VanguardFollowIntentProducer(),
        new VanguardOpportunisticCorpseLootIntentProducer(),
        new VanguardExternalSystemIntentProducer()
    };

    private static bool bootLogged;

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            LastByBotProfileId.Clear();
            LastThreatScanByBotProfileId.Clear();
        }

        bootLogged = false;
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorIntentDryRunStatusTag,
            $"intent dry-run runtime state reset reason={reason}; readOnly=true; executesActions=false");
    }

    public static void Tick()
    {
        // audit subsystem: dry-run only. It consumes audit subsystem snapshots and audit subsystem intent data after
        // the load guard opens, keeps scanner promotion logs latched, and clarifies summary counters without calling executors.
        if (!VanguardOperatorRuntimeAuditLoadGuard.IsOpen())
        {
            return;
        }

        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }

        bool intentDryRunEnabled = VanguardOperatorRuntimeAuditOptions.GetIntentDryRunEnabled();
        bool threatScannerEnabled = VanguardOperatorRuntimeAuditOptions.GetThreatScannerDryRunEnabled();
        if (!VanguardOperatorRuntimeAuditSyncService.EffectiveEnabled || (!intentDryRunEnabled && !threatScannerEnabled))
        {
            return;
        }

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.OperatorIntentDryRunStatusTag,
                $"VANGUARD_OPERATOR_INTENT_DRYRUN_BOOT enabled={intentDryRunEnabled}; readOnly=true; executesActions=false; source=snapshots; authority=headless_or_host; headless={VanguardFikaCompat.IsHeadless}; host={VanguardFikaCompat.IsHost}; build={VanguardBuildVersion.BuildLabel}");
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.OperatorThreatScannerPromotionLatchStatusTag,
                $"VANGUARD_THREAT_SCAN_SIDECAR_BOOT enabled={VanguardOperatorRuntimeAuditOptions.GetThreatScannerDryRunEnabled()}; interval={VanguardOperatorRuntimeAuditOptions.GetThreatScannerIntervalSeconds():0.00}; mode=readmodel; noiseFilter=true; promotionLatch=true; readOnly=true; promotesDirectly=false; BridgeConsumesCandidates=true; scope=per_operator_combat_sidecar; source=sain_enemy_controller_lists");
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.AwarenessReadModelStatusTag,
                $"VANGUARD_AWARENESS_READMODEL_BOOT enabled=true; readOnly=true; source=threat_snapshot+threat_scan_sidecar; imports=awareness_concepts_with_active_bootstrap_bridge; promotes=false_readmodel_only; releasesFormation=false_readmodel_only; propagates=false_readmodel_only; activeBridge=target_bootstrap_and_squad_propagation; activeBridgePromotes=true; activeBridgePropagates=true; build={VanguardBuildVersion.BuildLabel}");
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.ExecutionWindowStatusTag,
                $"VANGUARD_EXECUTION_WINDOW_BOOT enabled=true; readOnly=true; source=selected_intent+decision_snapshot; opensWindows=false; evaluatesProgress=false; updatesOutcomeMemory=false; executesActions=false; build={VanguardBuildVersion.BuildLabel}");
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.SquadCohesionReadOnlyStatusTag,
                $"VANGUARD_COHESION_READONLY_BOOT enabled=true; readOnly=true; activeMovement=false; bubbleRadius=75; slots=preferences; sectors=dynamic; appliesMovement=false; controlsSain=false; build={VanguardBuildVersion.BuildLabel}");
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.MovementAuthorityReadOnlyStatusTag,
                $"VANGUARD_MOVE_AUTHORITY_READONLY_BOOT enabled=true; readOnly=true; activeMovement=false; movementBroker=dry_run; oneMovementAuthority=true; bubbleRadius=75; softCorrection=80; hardCorrection=88; backendApply=false; build={VanguardBuildVersion.BuildLabel}");
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.MovementBrokerDryRunStatusTag,
                $"VANGUARD_MOVEMENT_BROKER_DRYRUN_BOOT enabled=true; readOnly=true; leaseApply=false; backend=evaluate_only; canSelectBackend=false; wouldSuppressOnly=true; doctrine=single_authority_no_multi_apply; build={VanguardBuildVersion.BuildLabel}");
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.MovementContractsStatusTag,
                $"VANGUARD_MOVEMENT_CONTRACTS_BOOT enabled=true; readOnly=true; activeMovement=false; separatesHoldSectorFromReturnBubble=true; contracts=HoldCurrent,BreakSainHoldSector,BreakSainActionRally,ActionRallyHardReturn,SuppressExternal,YieldMedical,YieldSain,BlockOwner; build={VanguardBuildVersion.BuildLabel}");
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.MovementLeasePlanStatusTag,
                $"VANGUARD_MOVEMENT_LEASE_PLAN_BOOT enabled=true; readOnly=true; applyEnabled={Vanguard.Client.Runtime.Movement.VanguardMovementAuthorityDoctrine.ActiveBackendApplyEnabled}; backendApply=hard_return_plus_sain_boundary_executor; leaseModel=true; reapplyPolicy=apply_once_no_periodic_reapply; bubbleRadius=75; hardCorrection=88; build={VanguardBuildVersion.BuildLabel}");
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.MovementHardReturnActiveStatusTag,
                $"VANGUARD_HARD_RETURN_ACTIVE_BOOT enabled=true; activeMovement=true; dryRunStillReadOnly=true; scope=hard_outside_action_rally_return; backend=BigBrain_GoToSomePoint; applyPolicy=apply_once; suppressExternal=once; build={VanguardBuildVersion.BuildLabel}");
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.MovementExternalSuppressHardReturnStatusTag,
                $"VANGUARD_EXTERNAL_SUPPRESS_HARD_RETURN_BOOT enabled=true; movementOnly=true; suppresses=ORBIT_LootingBots_EFTPath; rejects=SAINDirectCombat; build={VanguardBuildVersion.BuildLabel}");
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.MovementSainBoundaryReturnActiveStatusTag,
                $"VANGUARD_SAIN_BOUNDARY_RETURN_ACTIVE_BOOT enabled=true; activeMovement=true; scope=hard_outside_sain_search_action_rally_return; backend=BigBrain_GoToSomePoint; applyPolicy=apply_once; suppressSainSearch=true; preserveDirectThreat=true; build={VanguardBuildVersion.BuildLabel}");
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.MovementSainSearchSuppressStatusTag,
                $"VANGUARD_SAIN_SEARCH_SUPPRESS_BOOT enabled=true; boundaryOnly=true; rejects=true_direct_threat; neverClearsFreshVisibleCanShootThreat=true; build={VanguardBuildVersion.BuildLabel}");
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.MovementReturnAuthorityStatusTag,
                $"VANGUARD_RETURN_AUTHORITY_BOOT_READMODEL enabled=true; activeMovement=true; authorityHeldUntilOutcome=true; externalQuiesceRequired=true; noAnchorOnlyCompletion=true; build={VanguardBuildVersion.BuildLabel}");
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.MovementActionRallyStatusTag,
                $"VANGUARD_ACTION_RALLY_RETURN_BOOT enabled=true; rallyClear=38; rallyAccept=45; rallyRadii=24,18,32,12,40; returnsToPlayerAction=true; build={VanguardBuildVersion.BuildLabel}");
        }

        var snapshots = VanguardOperatorDecisionSnapshotService.GetLatestSnapshots();
        var now = DateTimeOffset.UtcNow;
        foreach (var snapshot in snapshots)
        {
            try
            {
                var board = BuildBoard(snapshot);
                LogThreatScanIfNeeded(board, now);

                if (intentDryRunEnabled && ShouldLogSelected(board, now))
                {
                    VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OperatorIntentDryRunStatusTag, FormatSelected(board, "VANGUARD_OPERATOR_INTENT_DRYRUN_SELECTED"));
                }

                if (intentDryRunEnabled && VanguardOperatorRuntimeAuditOptions.GetSummaryLogEnabled() && ShouldLogSummary(board, now))
                {
                    VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OperatorIntentDryRunStatusTag, FormatSelected(board, "VANGUARD_OPERATOR_INTENT_DRYRUN_SUMMARY"));
                }
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(
                    VanguardBuildVersion.OperatorIntentDryRunStatusTag,
                    $"intent dry-run failed operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; reason={exception.GetType().Name}: {exception.Message}");
            }
        }
    }


}
#endif
