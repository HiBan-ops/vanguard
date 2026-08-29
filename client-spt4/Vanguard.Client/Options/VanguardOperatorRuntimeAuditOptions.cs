using System;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using BepInEx.Configuration;
#endif

// Responsibility: defines the F12-facing runtime diagnostics and raid-scoped behavioral tuning surface, then projects settings into the authority synchronization DTOs.
// Flow: BepInEx/F12 values are bound, normalized and exposed through getters/snapshots; raid-scoped settings are synchronized to the process that owns runtime execution.
// Authority boundary: diagnostic switches control observability only; behavioral settings become authoritative only through their documented player/raid synchronization path.
// Invariant: defaults preserve the published 0.7.0 behavior, including Headless GC troubleshooting disabled by default and combat-pursuit cohesion at 32 m.

namespace Vanguard.Client.Options;

internal enum VanguardCombatDiagnosticsScope
{
    Off = 0,
    OperatorsOnly = 1,
    AllBots = 2
}

internal enum VanguardPerformanceTelemetryMode
{
    Off = 0,
    SlowCallsOnly = 1,
    Full = 2
}

internal static class VanguardOperatorRuntimeAuditOptions
{
    private static VanguardCombatDiagnosticsScope cachedCombatDiagnosticsScope = VanguardCombatDiagnosticsScope.Off;
    private static VanguardPerformanceTelemetryMode cachedPerformanceTelemetryMode = VanguardPerformanceTelemetryMode.SlowCallsOnly;
    private static bool cachedDetailedDiagnosticPayloads;

#if SPT_CLIENT
    private static ConfigEntry<string>? auditLevel;
    private static ConfigEntry<string>? combatDiagnosticsScope;
    private static ConfigEntry<string>? performanceTelemetry;
    private static ConfigEntry<bool>? detailedDiagnosticPayloads;
    private static ConfigEntry<bool>? enabled;
    private static ConfigEntry<bool>? movementProbe;
    private static ConfigEntry<bool>? brainProbe;
    private static ConfigEntry<bool>? sainProbe;
    private static ConfigEntry<bool>? lootingBotsProbe;
    private static ConfigEntry<bool>? orbitProbe;
    private static ConfigEntry<bool>? summaryLog;
    private static ConfigEntry<bool>? decisionSnapshotLog;
    private static ConfigEntry<bool>? intentDryRun;
    private static ConfigEntry<bool>? threatScannerDryRun;
    private static ConfigEntry<bool>? firstActiveMobileMedicalLease;
    private static ConfigEntry<bool>? operatorPostRaidPersistenceEnabled;
    private static ConfigEntry<bool>? headlessKeepGcEnabledInRaid;
    private static ConfigEntry<bool>? verboseTransitionLog;
    private static ConfigEntry<float>? snapshotInterval;
    private static ConfigEntry<float>? summaryInterval;
    private static ConfigEntry<float>? transitionLogMinInterval;
    private static ConfigEntry<float>? threatScannerInterval;
    private static ConfigEntry<bool>? movementOutsideBubbleRecallEnabled;
    private static ConfigEntry<bool>? movementSainBoundaryReturnEnabled;
    private static ConfigEntry<bool>? movementSuppressExternalDuringRecallEnabled;
    private static ConfigEntry<bool>? movementVerboseDoctrineLogEnabled;
    private static ConfigEntry<bool>? movementTacticalRepositionEnabled;
    private static ConfigEntry<float>? movementTacticalRepositionCooldownSeconds;
    private static ConfigEntry<float>? movementTacticalRepositionMinDeltaMeters;
    private static ConfigEntry<float>? movementTacticalBubbleMeters;
    private static ConfigEntry<float>? movementSoftCorrectionMeters;
    private static ConfigEntry<float>? movementHardCorrectionMeters;
    private static ConfigEntry<float>? movementCombatCohesionForcedCatchupMeters;
    private static ConfigEntry<float>? movementTravelCatchUpEnterMeters;
    private static ConfigEntry<float>? movementTravelCatchUpExitMeters;
    private static ConfigEntry<float>? movementTravelModeDwellSeconds;
    private static ConfigEntry<float>? movementActionRallyClearMeters;
    private static ConfigEntry<float>? movementActionRallyAcceptMeters;
    private static ConfigEntry<float>? movementActionRallyPreferredMeters;
    private static ConfigEntry<float>? movementLeaseStartCooldownSeconds;
    private static ConfigEntry<float>? movementLeaseFailureCooldownSeconds;
    private static ConfigEntry<float>? movementLeaseNoProgressSeconds;
    private static ConfigEntry<float>? movementLeaseMaxDurationSeconds;
    private static ConfigEntry<int>? movementActionRallyMaxReanchors;
    private static ConfigEntry<bool>? movementOpportunisticLootBrokerEnabled;
    private static ConfigEntry<float>? movementOpportunisticLootMaxDistanceMeters;
    private static ConfigEntry<float>? movementOpportunisticLootScanCooldownSeconds;
    private static ConfigEntry<float>? movementOpportunisticLootGrantSeconds;
    private static ConfigEntry<bool>? lootOperationalSessionEnabled;
    private static ConfigEntry<bool>? lootBackupLongWeaponEnabled;
    private static ConfigEntry<bool>? lootBackupPistolEnabled;
    private static ConfigEntry<bool>? lootMedicalItemsEnabled;
    private static ConfigEntry<bool>? lootCompatibleMagazinesEnabled;
    private static ConfigEntry<bool>? lootCompatibleLooseAmmunitionEnabled;
    private static ConfigEntry<bool>? lootGrenadesEnabled;
    private static ConfigEntry<int>? lootMaximumTransactionsPerCorpse;
    private static ConfigEntry<float>? lootMaximumSessionSeconds;
    private static ConfigEntry<int>? lootMaximumMedicalItemsPerSession;
    private static ConfigEntry<int>? lootMaximumMagazinesPerSession;
    private static ConfigEntry<int>? lootMaximumLooseAmmunitionRoundsPerSession;
    private static ConfigEntry<int>? lootMaximumWeaponsPerSession;
    private static bool isBound;
    private static bool suppressChangedEvents;
#endif

    public static event Action? Changed;

    public static void Bind(
#if SPT_CLIENT
        ConfigFile config
#endif
        )
    {
#if SPT_CLIENT
        if (isBound)
        {
            return;
        }

        const string diagnosticsSection = "Vanguard - Diagnostics";
        auditLevel = config.Bind(diagnosticsSection, "Audit level", "Operational", new ConfigDescription("Controls log volume only. Operational keeps important transitions, Diagnostic adds decisions/plans, Trace restores deep probes. Gameplay systems never depend on this value.", new AcceptableValueList<string>("Off", "Operational", "Diagnostic", "Trace")));
        combatDiagnosticsScope = config.Bind(diagnosticsSection, "Combat diagnostics scope", "Off", new ConfigDescription("Controls passive fire-production probes only. OperatorsOnly observes Vanguard Operators; AllBots is a temporary Trace investigation mode. This never changes combat behavior.", new AcceptableValueList<string>("Off", "OperatorsOnly", "AllBots")));
        performanceTelemetry = config.Bind(diagnosticsSection, "Performance telemetry", "SlowCallsOnly", new ConfigDescription("Off disables runtime timing logs, SlowCallsOnly aggregates real hotspots, Full retains detailed profiler summaries. Timing never changes gameplay.", new AcceptableValueList<string>("Off", "SlowCallsOnly", "Full")));
        detailedDiagnosticPayloads = config.Bind(diagnosticsSection, "Detailed route and lease payloads", false, "Emit large route/lease payloads in Diagnostic or Trace. Disabled by default to reduce allocations and synchronous log I/O.");
        VanguardClientDiagnosticsLog.SetAuditLevel(auditLevel.Value, "f12_bind");
        RefreshDiagnosticRuntimePolicy();

        const string section = "Vanguard - Operator Audit (Legacy Detail Switches)";
        enabled = config.Bind(section, "Enable passive Operator audit", false, "Enable read-only deep audit probes. This never drives AI actions; use Audit level=Trace for full detail.");
        movementProbe = config.Bind(section, "Probe movement", true, "Log movement/path/speed observations when passive audit is enabled.");
        brainProbe = config.Bind(section, "Probe BigBrain and vanilla brain", true, "Log active brain/layer/action observations when passive audit is enabled.");
        sainProbe = config.Bind(section, "Probe SAIN", true, "Try to read SAIN state through safe reflection when passive audit is enabled.");
        lootingBotsProbe = config.Bind(section, "Probe LootingBots", true, "Try to read LootingBots state through safe reflection when passive audit is enabled.");
        orbitProbe = config.Bind(section, "Probe ORBIT", true, "Try to read ORBIT state through safe reflection when passive audit is enabled.");
        summaryLog = config.Bind(section, "Summary logs", true, "Emit periodic compact summaries in addition to transition logs.");
        decisionSnapshotLog = config.Bind(section, "Decision snapshot logs", true, "Emit typed decision snapshot logs. The snapshot builder remains read-only.");
        intentDryRun = config.Bind(section, "Intent dry-run logs", true, "Emit read-only intent selection logs. No movement, combat, medical or loot action is executed.");
        threatScannerDryRun = config.Bind(section, "Threat scanner sidecar dry-run logs", true, "Emit anti-tunnel threat scanner diagnostic logs. This switch no longer enables or disables the active awareness/scanner runtime, which is a core Vanguard combat service.");
        firstActiveMobileMedicalLease = config.Bind(section, "Enable first active mobile medical lease", true, "Allow Vanguard to execute one short active mobile medical action for heavy/light bleeding using the Vanguard item-priority matrix. No surgery, fracture, movement, loot or SAIN target mutation is performed.");

        const string persistenceSection = "Vanguard - Persistence (Testing)";
        operatorPostRaidPersistenceEnabled = config.Bind(persistenceSection, "Enable Operator post-raid persistence", true, "ON commits final Operator equipment, medical and service state after raid. OFF skips only the durable post-raid commit; all in-raid loot, corpse recovery, medical, combat, movement and native EFT/Fika inventory transactions remain active. The player raid authority controls this value and headless instances consume it.");

        const string headlessSection = "Vanguard - Headless Stabilization";
        headlessKeepGcEnabledInRaid = config.Bind(headlessSection, "Keep GC enabled during Headless raid", false, "Enable this if the dedicated Fika Headless process shows RAM saturation or memory-related freezes/stalls. OFF keeps native Fika Headless GC behavior. ON keeps the managed garbage collector enabled during the raid to limit managed-memory and system-commit growth. The player raid authority owns this value and Vanguard pushes it to Headless through runtime settings sync.");
        verboseTransitionLog = config.Bind(section, "Verbose transition logs", false, "When disabled, logs only meaningful brain-state transitions instead of every low-level movement/brain text change.");
        snapshotInterval = config.Bind(section, "Snapshot interval seconds", 1.0f, new ConfigDescription("Minimum seconds between audit/snapshot captures.", new AcceptableValueRange<float>(0.5f, 5.0f)));
        summaryInterval = config.Bind(section, "Summary interval seconds", 60.0f, new ConfigDescription("Minimum seconds between per-Operator diagnostic summaries.", new AcceptableValueRange<float>(30.0f, 120.0f)));
        transitionLogMinInterval = config.Bind(section, "Transition log min interval seconds", 2.0f, new ConfigDescription("Minimum seconds between non-verbose transition logs for the same Operator.", new AcceptableValueRange<float>(0.5f, 10.0f)));
        threatScannerInterval = config.Bind(section, "Threat scanner interval seconds", 1.0f, new ConfigDescription("Minimum seconds between read-only threat scanner sidecar logs for the same Operator while in combat context.", new AcceptableValueRange<float>(0.5f, 3.0f)));

        const string movementSection = "Vanguard - Movement Doctrine";
        movementOutsideBubbleRecallEnabled = config.Bind(movementSection, "Enable outside bubble recall", true, "Allow Vanguard to start hard action-rally recall when an Operator is far outside the tactical bubble. Synced from client F12 to the server/headless.");
        movementSainBoundaryReturnEnabled = config.Bind(movementSection, "Enable SAIN stale boundary return", true, "Allow Vanguard to break stale/non-actionable SAIN search outside the bubble and recall the Operator. Synced to headless.");
        movementSuppressExternalDuringRecallEnabled = config.Bind(movementSection, "Suppress ORBIT/LootingBots during recall", true, "Suppress external movement/loot authority while a Vanguard hard return is pending or active.");
        movementVerboseDoctrineLogEnabled = config.Bind(movementSection, "Verbose movement doctrine logs", false, "Emit compact logs when movement doctrine F12/headless values are pushed or pulled.");
        movementTacticalRepositionEnabled = config.Bind(movementSection, "Enable tactical reposition", true, "Allow Vanguard to issue short environment-aware sector reposition leases inside the tactical bubble. Synced to headless.");
        movementTacticalRepositionCooldownSeconds = config.Bind(movementSection, "Tactical reposition cooldown seconds", 8.0f, new ConfigDescription("Cooldown after a tactical reposition outcome before another sector reposition can start for the same Operator.", new AcceptableValueRange<float>(3.0f, 30.0f)));
        movementTacticalRepositionMinDeltaMeters = config.Bind(movementSection, "Tactical reposition min delta meters", 7.0f, new ConfigDescription("Minimum meaningful distance between the Operator and a tactical anchor. Smaller moves are ignored to avoid spasms.", new AcceptableValueRange<float>(3.0f, 18.0f)));
        movementTacticalBubbleMeters = config.Bind(movementSection, "Tactical bubble meters", 75.0f, new ConfigDescription("Distance around the player that defines the normal Vanguard action bubble.", new AcceptableValueRange<float>(35.0f, 120.0f)));
        movementSoftCorrectionMeters = config.Bind(movementSection, "Soft correction meters", 80.0f, new ConfigDescription("Distance where Vanguard starts monitoring a soft bubble breach without necessarily commanding movement.", new AcceptableValueRange<float>(40.0f, 135.0f)));
        movementHardCorrectionMeters = config.Bind(movementSection, "Hard recall meters", 88.0f, new ConfigDescription("Distance where Vanguard may start a hard action-rally return, if combat/medical gates allow it.", new AcceptableValueRange<float>(45.0f, 150.0f)));
        movementCombatCohesionForcedCatchupMeters = config.Bind(movementSection, "Combat pursuit cohesion limit meters", 32.0f, new ConfigDescription("Owner distance beyond which Vanguard stops renewing stale or non-direct distant pursuit and forces combat cohesion return. Direct or recently actionable threats can still qualify combat. This raid-scoped value is synchronized from the player raid authority to Headless and clients.", new AcceptableValueRange<float>(16.0f, 80.0f)));
        movementTravelCatchUpEnterMeters = config.Bind(movementSection, "Travel catch-up enter meters", 28.0f, new ConfigDescription("Distance that must persist before FormationTravel enters CatchUp. Effective value remains below Hard recall meters.", new AcceptableValueRange<float>(18.0f, 60.0f)));
        movementTravelCatchUpExitMeters = config.Bind(movementSection, "Travel catch-up exit meters", 22.0f, new ConfigDescription("Lower distance that must persist before CatchUp returns to FormationTravel. Effective value remains at least 2 m below the enter threshold.", new AcceptableValueRange<float>(10.0f, 55.0f)));
        movementTravelModeDwellSeconds = config.Bind(movementSection, "Travel mode dwell seconds", 1.25f, new ConfigDescription("Time a non-critical mode transition must remain true. EmergencyCatchUp entry is immediate; its exit uses Soft correction meters plus this dwell.", new AcceptableValueRange<float>(0.25f, 5.0f)));
        movementActionRallyClearMeters = config.Bind(movementSection, "Recall clear meters", 38.0f, new ConfigDescription("Bubble distance that completes a hard recall as clearly returned to the action.", new AcceptableValueRange<float>(12.0f, 70.0f)));
        movementActionRallyAcceptMeters = config.Bind(movementSection, "Recall accept meters", 45.0f, new ConfigDescription("Bubble distance that accepts a hard recall as complete even if the exact anchor was not reached.", new AcceptableValueRange<float>(15.0f, 80.0f)));
        movementActionRallyPreferredMeters = config.Bind(movementSection, "Recall preferred meters", 24.0f, new ConfigDescription("Preferred radius for action-rally anchors around the player during hard return.", new AcceptableValueRange<float>(8.0f, 55.0f)));
        movementLeaseStartCooldownSeconds = config.Bind(movementSection, "Recall success cooldown seconds", 10.0f, new ConfigDescription("Cooldown after a completed hard recall before a new recall can start for the same Operator.", new AcceptableValueRange<float>(2.0f, 30.0f)));
        movementLeaseFailureCooldownSeconds = config.Bind(movementSection, "Recall failure cooldown seconds", 12.0f, new ConfigDescription("Cooldown after a failed/timeout hard recall before retrying for the same Operator.", new AcceptableValueRange<float>(4.0f, 45.0f)));
        movementLeaseNoProgressSeconds = config.Bind(movementSection, "Recall no progress timeout seconds", 7.0f, new ConfigDescription("Seconds without movement progress before Vanguard tries a controlled reanchor or times out.", new AcceptableValueRange<float>(3.0f, 20.0f)));
        movementLeaseMaxDurationSeconds = config.Bind(movementSection, "Recall max lease seconds", 45.0f, new ConfigDescription("Normal maximum hard-return lease window. Progress can extend within the hard cap.", new AcceptableValueRange<float>(18.0f, 90.0f)));
        movementActionRallyMaxReanchors = config.Bind(movementSection, "Recall max reanchors", 2, new ConfigDescription("Maximum controlled reanchors during one hard-return lease.", new AcceptableValueRange<int>(0, 5)));
        movementOpportunisticLootBrokerEnabled = config.Bind(movementSection, "Enable opportunistic loot broker", true, "Allow Vanguard to qualify, claim and approach one useful opportunistic-loot target at a time (corpse or world container), subject to each Operator persistent loot permission. LootingBots remains suppressed for Operators. Synced to headless.");
        movementOpportunisticLootMaxDistanceMeters = config.Bind(movementSection, "Opportunistic loot max distance meters", 38.0f, new ConfigDescription("Maximum direct Operator-to-opportunistic-loot-target distance used by Vanguard corpse and world-container qualification/approach planning.", new AcceptableValueRange<float>(6.0f, 42.0f)));
        movementOpportunisticLootScanCooldownSeconds = config.Bind(movementSection, "Opportunistic loot scan cooldown seconds", 18.0f, new ConfigDescription("Compatibility value retained for existing configurations. Vanguard claim-and-approach never invokes the autonomous LootingBots scan driver.", new AcceptableValueRange<float>(8.0f, 60.0f)));
        movementOpportunisticLootGrantSeconds = config.Bind(movementSection, "Opportunistic loot grant seconds", 8.0f, new ConfigDescription("Compatibility value retained for existing configurations. Vanguard uses its own bounded loot window and keeps LootingBots suppressed for Operators.", new AcceptableValueRange<float>(4.0f, 20.0f)));

        const string lootSection = "Vanguard - Opportunistic Loot";
        lootOperationalSessionEnabled = config.Bind(lootSection, "Enable operational opportunistic loot", true, "Master runtime switch for Vanguard opportunistic-loot execution. Per-Operator Cadavres/Conteneurs permissions still decide which target kinds are allowed; runtime/F12 can only narrow them. Combat and medical remain superior authorities.");
        lootBackupLongWeaponEnabled = config.Bind(lootSection, "Loot backup long weapon", true, "Allow one backup long weapon to fill an empty long-weapon slot. Existing equipped weapons are never replaced.");
        lootBackupPistolEnabled = config.Bind(lootSection, "Loot backup pistol", true, "Allow a pistol to fill an empty holster. Existing holster weapons are never replaced.");
        lootMedicalItemsEnabled = config.Bind(lootSection, "Loot medical items", true, "Allow usable HP, bleeding, fracture, pain/mobility and surgery items. Selection remains bounded and revalidated after every transaction.");
        lootCompatibleMagazinesEnabled = config.Bind(lootSection, "Loot compatible magazines", true, "Allow filled magazines compatible with currently equipped or newly acquired backup weapons.");
        lootCompatibleLooseAmmunitionEnabled = config.Bind(lootSection, "Loot compatible loose ammunition", true, "Allow loose ammunition whose caliber matches an equipped or newly acquired weapon.");
        lootGrenadesEnabled = config.Bind(lootSection, "Loot grenades", true, "Allow grenades until the normal Vanguard reserve target is satisfied.");
        lootMaximumTransactionsPerCorpse = config.Bind(lootSection, "Maximum transactions per corpse", 8, new ConfigDescription("Hard upper bound for sequential atomic item moves during one corpse session.", new AcceptableValueRange<int>(1, 12)));
        lootMaximumSessionSeconds = config.Bind(lootSection, "Maximum loot session seconds", 10.0f, new ConfigDescription("Maximum post-arrival loot session duration. Combat and medical can interrupt it at any time before a transaction submit.", new AcceptableValueRange<float>(3.0f, 20.0f)));
        lootMaximumMedicalItemsPerSession = config.Bind(lootSection, "Maximum medical items per session", 4, new ConfigDescription("Maximum medical items committed from one corpse during one session.", new AcceptableValueRange<int>(0, 8)));
        lootMaximumMagazinesPerSession = config.Bind(lootSection, "Maximum magazines per session", 4, new ConfigDescription("Maximum compatible filled magazines committed from one corpse during one session.", new AcceptableValueRange<int>(0, 8)));
        lootMaximumLooseAmmunitionRoundsPerSession = config.Bind(lootSection, "Maximum loose ammunition rounds per session", 180, new ConfigDescription("Maximum total rounds in complete loose-ammunition stacks moved during one corpse session. Stacks are never split by Vanguard.", new AcceptableValueRange<int>(0, 600)));
        lootMaximumWeaponsPerSession = config.Bind(lootSection, "Maximum weapons per session", 1, new ConfigDescription("Maximum backup weapons committed during one corpse session. Existing equipped weapons are never replaced.", new AcceptableValueRange<int>(0, 2)));

        // BepInEx exposes SettingChanged on ConfigEntry<T>, not on ConfigEntryBase in the
        // client target used by the Vanguard pipeline. Keep these subscriptions strongly
        // typed so the F12 menu remains compatible with netstandard2.1 builds.
        Subscribe(auditLevel);
        Subscribe(combatDiagnosticsScope);
        Subscribe(performanceTelemetry);
        Subscribe(detailedDiagnosticPayloads);
        Subscribe(enabled);
        Subscribe(movementProbe);
        Subscribe(brainProbe);
        Subscribe(sainProbe);
        Subscribe(lootingBotsProbe);
        Subscribe(orbitProbe);
        Subscribe(summaryLog);
        Subscribe(decisionSnapshotLog);
        Subscribe(intentDryRun);
        Subscribe(threatScannerDryRun);
        Subscribe(firstActiveMobileMedicalLease);
        Subscribe(operatorPostRaidPersistenceEnabled);
        Subscribe(headlessKeepGcEnabledInRaid);
        Subscribe(verboseTransitionLog);
        Subscribe(snapshotInterval);
        Subscribe(summaryInterval);
        Subscribe(transitionLogMinInterval);
        Subscribe(threatScannerInterval);
        Subscribe(movementOutsideBubbleRecallEnabled);
        Subscribe(movementSainBoundaryReturnEnabled);
        Subscribe(movementSuppressExternalDuringRecallEnabled);
        Subscribe(movementVerboseDoctrineLogEnabled);
        Subscribe(movementTacticalRepositionEnabled);
        Subscribe(movementTacticalRepositionCooldownSeconds);
        Subscribe(movementTacticalRepositionMinDeltaMeters);
        Subscribe(movementTacticalBubbleMeters);
        Subscribe(movementSoftCorrectionMeters);
        Subscribe(movementHardCorrectionMeters);
        Subscribe(movementCombatCohesionForcedCatchupMeters);
        Subscribe(movementTravelCatchUpEnterMeters);
        Subscribe(movementTravelCatchUpExitMeters);
        Subscribe(movementTravelModeDwellSeconds);
        Subscribe(movementActionRallyClearMeters);
        Subscribe(movementActionRallyAcceptMeters);
        Subscribe(movementActionRallyPreferredMeters);
        Subscribe(movementLeaseStartCooldownSeconds);
        Subscribe(movementLeaseFailureCooldownSeconds);
        Subscribe(movementLeaseNoProgressSeconds);
        Subscribe(movementLeaseMaxDurationSeconds);
        Subscribe(movementActionRallyMaxReanchors);
        Subscribe(movementOpportunisticLootBrokerEnabled);
        Subscribe(movementOpportunisticLootMaxDistanceMeters);
        Subscribe(movementOpportunisticLootScanCooldownSeconds);
        Subscribe(movementOpportunisticLootGrantSeconds);
        Subscribe(lootOperationalSessionEnabled);
        Subscribe(lootBackupLongWeaponEnabled);
        Subscribe(lootBackupPistolEnabled);
        Subscribe(lootMedicalItemsEnabled);
        Subscribe(lootCompatibleMagazinesEnabled);
        Subscribe(lootCompatibleLooseAmmunitionEnabled);
        Subscribe(lootGrenadesEnabled);
        Subscribe(lootMaximumTransactionsPerCorpse);
        Subscribe(lootMaximumSessionSeconds);
        Subscribe(lootMaximumMedicalItemsPerSession);
        Subscribe(lootMaximumMagazinesPerSession);
        Subscribe(lootMaximumLooseAmmunitionRoundsPerSession);
        Subscribe(lootMaximumWeaponsPerSession);

        isBound = true;
        VanguardClientDiagnosticsLog.Diagnostic(
            VanguardBuildVersion.OperatorRuntimeAuditStatusTag,
            () => "F12 diagnostics options bound; serverSync=true; headlessSync=true; gameplayUnaffected=true.");
        VanguardClientDiagnosticsLog.Diagnostic(
            VanguardBuildVersion.AuditConfigBindStatusTag,
            () => "typed BepInEx ConfigEntry subscriptions active for audit, snapshot and intent dry-run options.");
#endif
    }


#if SPT_CLIENT
    private static void Subscribe<T>(ConfigEntry<T>? entry)
    {
        if (entry == null)
        {
            return;
        }

        entry.SettingChanged += (_, _) => RaiseChanged();
    }
#endif

    public static VanguardOperatorRuntimeAuditSettingsDto CurrentLocalSettings(string? profileId, string source)
    {
        return new VanguardOperatorRuntimeAuditSettingsDto
        {
            AuditLevel = GetAuditLevelName(),
            CombatDiagnosticsScope = GetCombatDiagnosticsScopeName(),
            PerformanceTelemetry = GetPerformanceTelemetryName(),
            DetailedDiagnosticPayloads = GetDetailedDiagnosticPayloadsConfigured(),
            Enabled = GetEnabled(),
            MovementProbeEnabled = GetMovementProbeEnabled(),
            BrainProbeEnabled = GetBrainProbeEnabled(),
            SainProbeEnabled = GetSainProbeEnabled(),
            LootingBotsProbeEnabled = GetLootingBotsProbeEnabled(),
            OrbitProbeEnabled = GetOrbitProbeEnabled(),
            SummaryLogEnabled = GetSummaryLogEnabled(),
            DecisionSnapshotLogEnabled = GetDecisionSnapshotLogEnabled(),
            IntentDryRunEnabled = GetIntentDryRunEnabled(),
            ThreatScannerDryRunEnabled = GetThreatScannerDryRunEnabled(),
            FirstActiveMobileMedicalLeaseEnabled = GetFirstActiveMobileMedicalLeaseEnabled(),
            OperatorPostRaidPersistenceEnabled = GetOperatorPostRaidPersistenceEnabled(),
            HeadlessKeepGcEnabledInRaid = GetHeadlessKeepGcEnabledInRaid(),
            VerboseTransitionLogEnabled = GetVerboseTransitionLogEnabled(),
            SnapshotIntervalSeconds = Clamp(GetSnapshotIntervalSeconds(), 0.5f, 5.0f),
            SummaryIntervalSeconds = Clamp(GetSummaryIntervalSeconds(), 30.0f, 120.0f),
            TransitionLogMinIntervalSeconds = Clamp(GetTransitionLogMinIntervalSeconds(), 0.5f, 10.0f),
            ThreatScannerIntervalSeconds = Clamp(GetThreatScannerIntervalSeconds(), 0.5f, 3.0f),
            MovementOutsideBubbleRecallEnabled = GetMovementOutsideBubbleRecallEnabled(),
            MovementSainBoundaryReturnEnabled = GetMovementSainBoundaryReturnEnabled(),
            MovementSuppressExternalDuringRecallEnabled = GetMovementSuppressExternalDuringRecallEnabled(),
            MovementVerboseDoctrineLogEnabled = GetMovementVerboseDoctrineLogEnabled(),
            MovementTacticalRepositionEnabled = GetMovementTacticalRepositionEnabled(),
            MovementTacticalRepositionCooldownSeconds = Clamp(GetMovementTacticalRepositionCooldownSeconds(), 3.0f, 30.0f),
            MovementTacticalRepositionMinDeltaMeters = Clamp(GetMovementTacticalRepositionMinDeltaMeters(), 3.0f, 18.0f),
            MovementTacticalBubbleMeters = Clamp(GetMovementTacticalBubbleMeters(), 35.0f, 120.0f),
            MovementSoftCorrectionMeters = Clamp(GetMovementSoftCorrectionMeters(), 40.0f, 135.0f),
            MovementHardCorrectionMeters = Clamp(GetMovementHardCorrectionMeters(), 45.0f, 150.0f),
            MovementCombatCohesionForcedCatchupMeters = GetMovementCombatCohesionForcedCatchupMeters(),
            MovementTravelCatchUpEnterMeters = GetMovementTravelCatchUpEnterMeters(),
            MovementTravelCatchUpExitMeters = GetMovementTravelCatchUpExitMeters(),
            MovementTravelModeDwellSeconds = GetMovementTravelModeDwellSeconds(),
            MovementActionRallyClearMeters = Clamp(GetMovementActionRallyClearMeters(), 12.0f, 70.0f),
            MovementActionRallyAcceptMeters = Clamp(GetMovementActionRallyAcceptMeters(), 15.0f, 80.0f),
            MovementActionRallyPreferredMeters = Clamp(GetMovementActionRallyPreferredMeters(), 8.0f, 55.0f),
            MovementLeaseStartCooldownSeconds = Clamp(GetMovementLeaseStartCooldownSeconds(), 2.0f, 30.0f),
            MovementLeaseFailureCooldownSeconds = Clamp(GetMovementLeaseFailureCooldownSeconds(), 4.0f, 45.0f),
            MovementLeaseNoProgressSeconds = Clamp(GetMovementLeaseNoProgressSeconds(), 3.0f, 20.0f),
            MovementLeaseMaxDurationSeconds = Clamp(GetMovementLeaseMaxDurationSeconds(), 18.0f, 90.0f),
            MovementActionRallyMaxReanchors = ClampInt(GetMovementActionRallyMaxReanchors(), 0, 5),
            MovementOpportunisticLootBrokerEnabled = GetMovementOpportunisticLootBrokerEnabled(),
            MovementOpportunisticLootMaxDistanceMeters = Clamp(GetMovementOpportunisticLootMaxDistanceMeters(), 6.0f, 42.0f),
            MovementOpportunisticLootScanCooldownSeconds = Clamp(GetMovementOpportunisticLootScanCooldownSeconds(), 8.0f, 60.0f),
            MovementOpportunisticLootGrantSeconds = Clamp(GetMovementOpportunisticLootGrantSeconds(), 4.0f, 20.0f),
            LootOperationalSessionEnabled = GetLootOperationalSessionEnabled(),
            LootBackupLongWeaponEnabled = GetLootBackupLongWeaponEnabled(),
            LootBackupPistolEnabled = GetLootBackupPistolEnabled(),
            LootMedicalItemsEnabled = GetLootMedicalItemsEnabled(),
            LootCompatibleMagazinesEnabled = GetLootCompatibleMagazinesEnabled(),
            LootCompatibleLooseAmmunitionEnabled = GetLootCompatibleLooseAmmunitionEnabled(),
            LootGrenadesEnabled = GetLootGrenadesEnabled(),
            LootMaximumTransactionsPerCorpse = GetLootMaximumTransactionsPerCorpse(),
            LootMaximumSessionSeconds = GetLootMaximumSessionSeconds(),
            LootMaximumMedicalItemsPerSession = GetLootMaximumMedicalItemsPerSession(),
            LootMaximumMagazinesPerSession = GetLootMaximumMagazinesPerSession(),
            LootMaximumLooseAmmunitionRoundsPerSession = GetLootMaximumLooseAmmunitionRoundsPerSession(),
            LootMaximumWeaponsPerSession = GetLootMaximumWeaponsPerSession(),
            UpdatedByProfileId = profileId,
            UpdatedBySource = source,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            BuildLabel = VanguardBuildVersion.BuildLabel
        };
    }

    public static void ApplyRemoteRaidScoped(VanguardOperatorRuntimeAuditSettingsDto? settings)
    {
        if (settings == null)
        {
            return;
        }

#if SPT_CLIENT
        if (enabled == null)
        {
            return;
        }

        suppressChangedEvents = true;
        try
        {
            auditLevel!.Value = NormalizeAuditLevel(settings.AuditLevel);
            combatDiagnosticsScope!.Value = NormalizeCombatDiagnosticsScope(settings.CombatDiagnosticsScope);
            performanceTelemetry!.Value = NormalizePerformanceTelemetry(settings.PerformanceTelemetry);
            detailedDiagnosticPayloads!.Value = settings.DetailedDiagnosticPayloads;
            VanguardClientDiagnosticsLog.SetAuditLevel(auditLevel.Value, "remote_raid_scope_sync");
            enabled.Value = settings.Enabled;
            movementProbe!.Value = settings.MovementProbeEnabled;
            brainProbe!.Value = settings.BrainProbeEnabled;
            sainProbe!.Value = settings.SainProbeEnabled;
            lootingBotsProbe!.Value = settings.LootingBotsProbeEnabled;
            orbitProbe!.Value = settings.OrbitProbeEnabled;
            summaryLog!.Value = settings.SummaryLogEnabled;
            decisionSnapshotLog!.Value = settings.DecisionSnapshotLogEnabled;
            intentDryRun!.Value = settings.IntentDryRunEnabled;
            threatScannerDryRun!.Value = settings.ThreatScannerDryRunEnabled;
            firstActiveMobileMedicalLease!.Value = settings.FirstActiveMobileMedicalLeaseEnabled;
            operatorPostRaidPersistenceEnabled!.Value = settings.OperatorPostRaidPersistenceEnabled;
            headlessKeepGcEnabledInRaid!.Value = settings.HeadlessKeepGcEnabledInRaid;
            verboseTransitionLog!.Value = settings.VerboseTransitionLogEnabled;
            snapshotInterval!.Value = Clamp(settings.SnapshotIntervalSeconds, 0.5f, 5.0f);
            summaryInterval!.Value = Clamp(settings.SummaryIntervalSeconds, 30.0f, 120.0f);
            transitionLogMinInterval!.Value = Clamp(settings.TransitionLogMinIntervalSeconds, 0.5f, 10.0f);
            threatScannerInterval!.Value = Clamp(settings.ThreatScannerIntervalSeconds, 0.5f, 3.0f);
            movementOutsideBubbleRecallEnabled!.Value = settings.MovementOutsideBubbleRecallEnabled;
            movementSainBoundaryReturnEnabled!.Value = settings.MovementSainBoundaryReturnEnabled;
            movementSuppressExternalDuringRecallEnabled!.Value = settings.MovementSuppressExternalDuringRecallEnabled;
            movementVerboseDoctrineLogEnabled!.Value = settings.MovementVerboseDoctrineLogEnabled;
            movementTacticalRepositionEnabled!.Value = settings.MovementTacticalRepositionEnabled;
            movementTacticalRepositionCooldownSeconds!.Value = Clamp(settings.MovementTacticalRepositionCooldownSeconds, 3.0f, 30.0f);
            movementTacticalRepositionMinDeltaMeters!.Value = Clamp(settings.MovementTacticalRepositionMinDeltaMeters, 3.0f, 18.0f);
            movementTacticalBubbleMeters!.Value = Clamp(settings.MovementTacticalBubbleMeters, 35.0f, 120.0f);
            movementSoftCorrectionMeters!.Value = Clamp(settings.MovementSoftCorrectionMeters, 40.0f, 135.0f);
            movementHardCorrectionMeters!.Value = Clamp(settings.MovementHardCorrectionMeters, 45.0f, 150.0f);
            movementCombatCohesionForcedCatchupMeters!.Value = Clamp(settings.MovementCombatCohesionForcedCatchupMeters, 16.0f, 80.0f);
            movementTravelCatchUpEnterMeters!.Value = Clamp(settings.MovementTravelCatchUpEnterMeters, 18.0f, 60.0f);
            movementTravelCatchUpExitMeters!.Value = Clamp(settings.MovementTravelCatchUpExitMeters, 10.0f, 55.0f);
            movementTravelModeDwellSeconds!.Value = Clamp(settings.MovementTravelModeDwellSeconds, 0.25f, 5.0f);
            movementActionRallyClearMeters!.Value = Clamp(settings.MovementActionRallyClearMeters, 12.0f, 70.0f);
            movementActionRallyAcceptMeters!.Value = Clamp(settings.MovementActionRallyAcceptMeters, 15.0f, 80.0f);
            movementActionRallyPreferredMeters!.Value = Clamp(settings.MovementActionRallyPreferredMeters, 8.0f, 55.0f);
            movementLeaseStartCooldownSeconds!.Value = Clamp(settings.MovementLeaseStartCooldownSeconds, 2.0f, 30.0f);
            movementLeaseFailureCooldownSeconds!.Value = Clamp(settings.MovementLeaseFailureCooldownSeconds, 4.0f, 45.0f);
            movementLeaseNoProgressSeconds!.Value = Clamp(settings.MovementLeaseNoProgressSeconds, 3.0f, 20.0f);
            movementLeaseMaxDurationSeconds!.Value = Clamp(settings.MovementLeaseMaxDurationSeconds, 18.0f, 90.0f);
            movementActionRallyMaxReanchors!.Value = ClampInt(settings.MovementActionRallyMaxReanchors, 0, 5);
        }
        finally
        {
            suppressChangedEvents = false;
            RefreshDiagnosticRuntimePolicy();
        }
#endif
    }

    // Legacy monolithic application is retained for source compatibility only. The persistence path sync no longer
    // calls it on headless because PLAYER_SCOPED values must never be projected globally.
    public static void ApplyRemote(VanguardOperatorRuntimeAuditSettingsDto? settings)
    {
        if (settings == null)
        {
            return;
        }

#if SPT_CLIENT
        if (enabled == null)
        {
            return;
        }

        suppressChangedEvents = true;
        try
        {
            auditLevel!.Value = NormalizeAuditLevel(settings.AuditLevel);
            combatDiagnosticsScope!.Value = NormalizeCombatDiagnosticsScope(settings.CombatDiagnosticsScope);
            performanceTelemetry!.Value = NormalizePerformanceTelemetry(settings.PerformanceTelemetry);
            detailedDiagnosticPayloads!.Value = settings.DetailedDiagnosticPayloads;
            VanguardClientDiagnosticsLog.SetAuditLevel(auditLevel.Value, "remote_sync");
            enabled.Value = settings.Enabled;
            movementProbe!.Value = settings.MovementProbeEnabled;
            brainProbe!.Value = settings.BrainProbeEnabled;
            sainProbe!.Value = settings.SainProbeEnabled;
            lootingBotsProbe!.Value = settings.LootingBotsProbeEnabled;
            orbitProbe!.Value = settings.OrbitProbeEnabled;
            summaryLog!.Value = settings.SummaryLogEnabled;
            decisionSnapshotLog!.Value = settings.DecisionSnapshotLogEnabled;
            intentDryRun!.Value = settings.IntentDryRunEnabled;
            threatScannerDryRun!.Value = settings.ThreatScannerDryRunEnabled;
            verboseTransitionLog!.Value = settings.VerboseTransitionLogEnabled;
            snapshotInterval!.Value = Clamp(settings.SnapshotIntervalSeconds, 0.5f, 5.0f);
            summaryInterval!.Value = Clamp(settings.SummaryIntervalSeconds, 30.0f, 120.0f);
            transitionLogMinInterval!.Value = Clamp(settings.TransitionLogMinIntervalSeconds, 0.5f, 10.0f);
            threatScannerInterval!.Value = Clamp(settings.ThreatScannerIntervalSeconds, 0.5f, 3.0f);
            movementOutsideBubbleRecallEnabled!.Value = settings.MovementOutsideBubbleRecallEnabled;
            movementSainBoundaryReturnEnabled!.Value = settings.MovementSainBoundaryReturnEnabled;
            movementSuppressExternalDuringRecallEnabled!.Value = settings.MovementSuppressExternalDuringRecallEnabled;
            movementVerboseDoctrineLogEnabled!.Value = settings.MovementVerboseDoctrineLogEnabled;
            movementTacticalRepositionEnabled!.Value = settings.MovementTacticalRepositionEnabled;
            movementTacticalRepositionCooldownSeconds!.Value = Clamp(settings.MovementTacticalRepositionCooldownSeconds, 3.0f, 30.0f);
            movementTacticalRepositionMinDeltaMeters!.Value = Clamp(settings.MovementTacticalRepositionMinDeltaMeters, 3.0f, 18.0f);
            movementTacticalBubbleMeters!.Value = Clamp(settings.MovementTacticalBubbleMeters, 35.0f, 120.0f);
            movementSoftCorrectionMeters!.Value = Clamp(settings.MovementSoftCorrectionMeters, 40.0f, 135.0f);
            movementHardCorrectionMeters!.Value = Clamp(settings.MovementHardCorrectionMeters, 45.0f, 150.0f);
            movementCombatCohesionForcedCatchupMeters!.Value = Clamp(settings.MovementCombatCohesionForcedCatchupMeters, 16.0f, 80.0f);
            movementTravelCatchUpEnterMeters!.Value = Clamp(settings.MovementTravelCatchUpEnterMeters, 18.0f, 60.0f);
            movementTravelCatchUpExitMeters!.Value = Clamp(settings.MovementTravelCatchUpExitMeters, 10.0f, 55.0f);
            movementTravelModeDwellSeconds!.Value = Clamp(settings.MovementTravelModeDwellSeconds, 0.25f, 5.0f);
            movementActionRallyClearMeters!.Value = Clamp(settings.MovementActionRallyClearMeters, 12.0f, 70.0f);
            movementActionRallyAcceptMeters!.Value = Clamp(settings.MovementActionRallyAcceptMeters, 15.0f, 80.0f);
            movementActionRallyPreferredMeters!.Value = Clamp(settings.MovementActionRallyPreferredMeters, 8.0f, 55.0f);
            movementLeaseStartCooldownSeconds!.Value = Clamp(settings.MovementLeaseStartCooldownSeconds, 2.0f, 30.0f);
            movementLeaseFailureCooldownSeconds!.Value = Clamp(settings.MovementLeaseFailureCooldownSeconds, 4.0f, 45.0f);
            movementLeaseNoProgressSeconds!.Value = Clamp(settings.MovementLeaseNoProgressSeconds, 3.0f, 20.0f);
            movementLeaseMaxDurationSeconds!.Value = Clamp(settings.MovementLeaseMaxDurationSeconds, 18.0f, 90.0f);
            movementActionRallyMaxReanchors!.Value = ClampInt(settings.MovementActionRallyMaxReanchors, 0, 5);
            movementOpportunisticLootBrokerEnabled!.Value = settings.MovementOpportunisticLootBrokerEnabled;
            movementOpportunisticLootMaxDistanceMeters!.Value = Clamp(settings.MovementOpportunisticLootMaxDistanceMeters, 6.0f, 42.0f);
            movementOpportunisticLootScanCooldownSeconds!.Value = Clamp(settings.MovementOpportunisticLootScanCooldownSeconds, 8.0f, 60.0f);
            movementOpportunisticLootGrantSeconds!.Value = Clamp(settings.MovementOpportunisticLootGrantSeconds, 4.0f, 20.0f);
            lootOperationalSessionEnabled!.Value = settings.LootOperationalSessionEnabled;
            lootBackupLongWeaponEnabled!.Value = settings.LootBackupLongWeaponEnabled;
            lootBackupPistolEnabled!.Value = settings.LootBackupPistolEnabled;
            lootMedicalItemsEnabled!.Value = settings.LootMedicalItemsEnabled;
            lootCompatibleMagazinesEnabled!.Value = settings.LootCompatibleMagazinesEnabled;
            lootCompatibleLooseAmmunitionEnabled!.Value = settings.LootCompatibleLooseAmmunitionEnabled;
            lootGrenadesEnabled!.Value = settings.LootGrenadesEnabled;
            lootMaximumTransactionsPerCorpse!.Value = ClampInt(settings.LootMaximumTransactionsPerCorpse, 1, 12);
            lootMaximumSessionSeconds!.Value = Clamp(settings.LootMaximumSessionSeconds, 3.0f, 20.0f);
            lootMaximumMedicalItemsPerSession!.Value = ClampInt(settings.LootMaximumMedicalItemsPerSession, 0, 8);
            lootMaximumMagazinesPerSession!.Value = ClampInt(settings.LootMaximumMagazinesPerSession, 0, 8);
            lootMaximumLooseAmmunitionRoundsPerSession!.Value = ClampInt(settings.LootMaximumLooseAmmunitionRoundsPerSession, 0, 600);
            lootMaximumWeaponsPerSession!.Value = ClampInt(settings.LootMaximumWeaponsPerSession, 0, 2);
        }
        finally
        {
            suppressChangedEvents = false;
            RefreshDiagnosticRuntimePolicy();
        }
#endif
    }

    public static string GetAuditLevelName()
    {
#if SPT_CLIENT
        return NormalizeAuditLevel(auditLevel?.Value);
#else
        return "Operational";
#endif
    }

    public static string GetCombatDiagnosticsScopeName()
    {
#if SPT_CLIENT
        return NormalizeCombatDiagnosticsScope(combatDiagnosticsScope?.Value);
#else
        return "Off";
#endif
    }

    public static VanguardCombatDiagnosticsScope GetCombatDiagnosticsScope() => cachedCombatDiagnosticsScope;

    public static string GetPerformanceTelemetryName()
    {
#if SPT_CLIENT
        return NormalizePerformanceTelemetry(performanceTelemetry?.Value);
#else
        return "SlowCallsOnly";
#endif
    }

    public static VanguardPerformanceTelemetryMode GetPerformanceTelemetryMode() => cachedPerformanceTelemetryMode;

    public static bool GetDetailedDiagnosticPayloadsConfigured() => cachedDetailedDiagnosticPayloads;

    public static bool GetDetailedDiagnosticPayloadsEnabled() =>
        IsDiagnosticOrHigher() && cachedDetailedDiagnosticPayloads;

    public static bool IsDiagnosticOrHigher() => VanguardDiagnosticsPolicy.Parse(GetAuditLevelName()) >= VanguardAuditLevel.Diagnostic;
    public static bool IsTrace() => VanguardDiagnosticsPolicy.Parse(GetAuditLevelName()) >= VanguardAuditLevel.Trace;

    public static bool GetEnabled()
    {
#if SPT_CLIENT
        return (enabled?.Value ?? false) || IsTrace();
#else
        return false;
#endif
    }

    public static bool GetMovementProbeEnabled() =>
#if SPT_CLIENT
        movementProbe?.Value ?? true;
#else
        true;
#endif

    public static bool GetBrainProbeEnabled() =>
#if SPT_CLIENT
        brainProbe?.Value ?? true;
#else
        true;
#endif

    public static bool GetSainProbeEnabled() =>
#if SPT_CLIENT
        sainProbe?.Value ?? true;
#else
        true;
#endif

    public static bool GetLootingBotsProbeEnabled() =>
#if SPT_CLIENT
        lootingBotsProbe?.Value ?? true;
#else
        true;
#endif

    public static bool GetOrbitProbeEnabled() =>
#if SPT_CLIENT
        orbitProbe?.Value ?? true;
#else
        true;
#endif

    public static bool GetSummaryLogEnabled() =>
#if SPT_CLIENT
        summaryLog?.Value ?? true;
#else
        true;
#endif

    public static bool GetDecisionSnapshotLogEnabled() =>
#if SPT_CLIENT
        IsDiagnosticOrHigher() && (decisionSnapshotLog?.Value ?? true);
#else
        true;
#endif

    public static bool GetIntentDryRunEnabled() =>
#if SPT_CLIENT
        IsDiagnosticOrHigher() && (intentDryRun?.Value ?? true);
#else
        true;
#endif


    public static bool GetThreatScannerDryRunEnabled() =>
#if SPT_CLIENT
        IsTrace() && (threatScannerDryRun?.Value ?? true);
#else
        true;
#endif

    public static bool GetFirstActiveMobileMedicalLeaseEnabled() =>
#if SPT_CLIENT
        firstActiveMobileMedicalLease?.Value ?? true;
#else
        false;
#endif

    public static bool GetOperatorPostRaidPersistenceEnabled() =>
#if SPT_CLIENT
        operatorPostRaidPersistenceEnabled?.Value ?? true;
#else
        true;
#endif

    public static bool GetHeadlessKeepGcEnabledInRaid()
    {
#if SPT_CLIENT
        return headlessKeepGcEnabledInRaid?.Value ?? false;
#else
        return false;
#endif
    }

    public static bool GetVerboseTransitionLogEnabled() =>
#if SPT_CLIENT
        verboseTransitionLog?.Value ?? false;
#else
        false;
#endif

    public static float GetSnapshotIntervalSeconds() => Clamp(
#if SPT_CLIENT
        snapshotInterval?.Value ?? 1.0f,
#else
        1.0f,
#endif
        0.5f, 5.0f);

    public static float GetSummaryIntervalSeconds() => Clamp(
#if SPT_CLIENT
        summaryInterval?.Value ?? 60.0f,
#else
        60.0f,
#endif
        30.0f, 120.0f);

    public static float GetTransitionLogMinIntervalSeconds() => Clamp(
#if SPT_CLIENT
        transitionLogMinInterval?.Value ?? 2.0f,
#else
        2.0f,
#endif
        0.5f, 10.0f);


    public static float GetThreatScannerIntervalSeconds() => Clamp(
#if SPT_CLIENT
        threatScannerInterval?.Value ?? 1.0f,
#else
        1.0f,
#endif
        0.5f, 3.0f);


    public static bool GetMovementOutsideBubbleRecallEnabled() =>
#if SPT_CLIENT
        movementOutsideBubbleRecallEnabled?.Value ?? true;
#else
        true;
#endif

    public static bool GetMovementSainBoundaryReturnEnabled() =>
#if SPT_CLIENT
        movementSainBoundaryReturnEnabled?.Value ?? true;
#else
        true;
#endif

    public static bool GetMovementSuppressExternalDuringRecallEnabled() =>
#if SPT_CLIENT
        movementSuppressExternalDuringRecallEnabled?.Value ?? true;
#else
        true;
#endif

    public static bool GetMovementVerboseDoctrineLogEnabled() =>
#if SPT_CLIENT
        movementVerboseDoctrineLogEnabled?.Value ?? false;
#else
        false;
#endif

    public static bool GetMovementTacticalRepositionEnabled() =>
#if SPT_CLIENT
        movementTacticalRepositionEnabled?.Value ?? true;
#else
        true;
#endif

    public static float GetMovementTacticalRepositionCooldownSeconds() => Clamp(
#if SPT_CLIENT
        movementTacticalRepositionCooldownSeconds?.Value ?? 8.0f,
#else
        8.0f,
#endif
        3.0f, 30.0f);

    public static float GetMovementTacticalRepositionMinDeltaMeters() => Clamp(
#if SPT_CLIENT
        movementTacticalRepositionMinDeltaMeters?.Value ?? 7.0f,
#else
        7.0f,
#endif
        3.0f, 18.0f);

    public static float GetMovementTacticalBubbleMeters() => Clamp(
#if SPT_CLIENT
        movementTacticalBubbleMeters?.Value ?? 75.0f,
#else
        75.0f,
#endif
        35.0f, 120.0f);

    public static float GetMovementSoftCorrectionMeters() => Clamp(
#if SPT_CLIENT
        movementSoftCorrectionMeters?.Value ?? 80.0f,
#else
        80.0f,
#endif
        40.0f, 135.0f);

    public static float GetMovementHardCorrectionMeters() => Clamp(
#if SPT_CLIENT
        movementHardCorrectionMeters?.Value ?? 88.0f,
#else
        88.0f,
#endif
        45.0f, 150.0f);

    public static float GetMovementCombatCohesionForcedCatchupMeters() => Clamp(
#if SPT_CLIENT
        movementCombatCohesionForcedCatchupMeters?.Value ?? 32.0f,
#else
        32.0f,
#endif
        16.0f, 80.0f);

    public static float GetMovementTravelCatchUpEnterMeters()
    {
        float configured = Clamp(
#if SPT_CLIENT
            movementTravelCatchUpEnterMeters?.Value ?? 28.0f,
#else
            28.0f,
#endif
            18.0f, 60.0f);
        float effectiveHardCorrection = Math.Max(GetMovementSoftCorrectionMeters() + 1.0f, GetMovementHardCorrectionMeters());
        return Math.Min(configured, Math.Max(18.0f, effectiveHardCorrection - 4.0f));
    }

    public static float GetMovementTravelCatchUpExitMeters()
    {
        float configured = Clamp(
#if SPT_CLIENT
            movementTravelCatchUpExitMeters?.Value ?? 22.0f,
#else
            22.0f,
#endif
            10.0f, 55.0f);
        return Math.Min(configured, Math.Max(10.0f, GetMovementTravelCatchUpEnterMeters() - 2.0f));
    }

    public static float GetMovementTravelModeDwellSeconds() => Clamp(
#if SPT_CLIENT
        movementTravelModeDwellSeconds?.Value ?? 1.25f,
#else
        1.25f,
#endif
        0.25f, 5.0f);

    public static float GetMovementActionRallyClearMeters() => Clamp(
#if SPT_CLIENT
        movementActionRallyClearMeters?.Value ?? 38.0f,
#else
        38.0f,
#endif
        12.0f, 70.0f);

    public static float GetMovementActionRallyAcceptMeters() => Clamp(
#if SPT_CLIENT
        movementActionRallyAcceptMeters?.Value ?? 45.0f,
#else
        45.0f,
#endif
        15.0f, 80.0f);

    public static float GetMovementActionRallyPreferredMeters() => Clamp(
#if SPT_CLIENT
        movementActionRallyPreferredMeters?.Value ?? 24.0f,
#else
        24.0f,
#endif
        8.0f, 55.0f);

    public static float GetMovementLeaseStartCooldownSeconds() => Clamp(
#if SPT_CLIENT
        movementLeaseStartCooldownSeconds?.Value ?? 10.0f,
#else
        10.0f,
#endif
        2.0f, 30.0f);

    public static float GetMovementLeaseFailureCooldownSeconds() => Clamp(
#if SPT_CLIENT
        movementLeaseFailureCooldownSeconds?.Value ?? 12.0f,
#else
        12.0f,
#endif
        4.0f, 45.0f);

    public static float GetMovementLeaseNoProgressSeconds() => Clamp(
#if SPT_CLIENT
        movementLeaseNoProgressSeconds?.Value ?? 7.0f,
#else
        7.0f,
#endif
        3.0f, 20.0f);

    public static float GetMovementLeaseMaxDurationSeconds() => Clamp(
#if SPT_CLIENT
        movementLeaseMaxDurationSeconds?.Value ?? 45.0f,
#else
        45.0f,
#endif
        18.0f, 90.0f);

    public static int GetMovementActionRallyMaxReanchors() => ClampInt(
#if SPT_CLIENT
        movementActionRallyMaxReanchors?.Value ?? 2,
#else
        2,
#endif
        0, 5);

    public static bool GetMovementOpportunisticLootBrokerEnabled() =>
#if SPT_CLIENT
        movementOpportunisticLootBrokerEnabled?.Value ?? true;
#else
        true;
#endif

    public static float GetMovementOpportunisticLootMaxDistanceMeters() => Clamp(
#if SPT_CLIENT
        movementOpportunisticLootMaxDistanceMeters?.Value ?? 38.0f,
#else
        38.0f,
#endif
        6.0f, 42.0f);

    public static float GetMovementOpportunisticLootScanCooldownSeconds() => Clamp(
#if SPT_CLIENT
        movementOpportunisticLootScanCooldownSeconds?.Value ?? 18.0f,
#else
        18.0f,
#endif
        8.0f, 60.0f);

    public static float GetMovementOpportunisticLootGrantSeconds() => Clamp(
#if SPT_CLIENT
        movementOpportunisticLootGrantSeconds?.Value ?? 8.0f,
#else
        8.0f,
#endif
        4.0f, 20.0f);

    public static bool GetLootOperationalSessionEnabled() =>
#if SPT_CLIENT
        lootOperationalSessionEnabled?.Value ?? true;
#else
        true;
#endif

    public static bool GetLootBackupLongWeaponEnabled() =>
#if SPT_CLIENT
        lootBackupLongWeaponEnabled?.Value ?? true;
#else
        true;
#endif

    public static bool GetLootBackupPistolEnabled() =>
#if SPT_CLIENT
        lootBackupPistolEnabled?.Value ?? true;
#else
        true;
#endif

    public static bool GetLootMedicalItemsEnabled() =>
#if SPT_CLIENT
        lootMedicalItemsEnabled?.Value ?? true;
#else
        true;
#endif

    public static bool GetLootCompatibleMagazinesEnabled() =>
#if SPT_CLIENT
        lootCompatibleMagazinesEnabled?.Value ?? true;
#else
        true;
#endif

    public static bool GetLootCompatibleLooseAmmunitionEnabled() =>
#if SPT_CLIENT
        lootCompatibleLooseAmmunitionEnabled?.Value ?? true;
#else
        true;
#endif

    public static bool GetLootGrenadesEnabled() =>
#if SPT_CLIENT
        lootGrenadesEnabled?.Value ?? true;
#else
        true;
#endif

    public static int GetLootMaximumTransactionsPerCorpse() => ClampInt(
#if SPT_CLIENT
        lootMaximumTransactionsPerCorpse?.Value ?? 8,
#else
        8,
#endif
        1, 12);

    public static float GetLootMaximumSessionSeconds() => Clamp(
#if SPT_CLIENT
        lootMaximumSessionSeconds?.Value ?? 10.0f,
#else
        10.0f,
#endif
        3.0f, 20.0f);

    public static int GetLootMaximumMedicalItemsPerSession() => ClampInt(
#if SPT_CLIENT
        lootMaximumMedicalItemsPerSession?.Value ?? 4,
#else
        4,
#endif
        0, 8);

    public static int GetLootMaximumMagazinesPerSession() => ClampInt(
#if SPT_CLIENT
        lootMaximumMagazinesPerSession?.Value ?? 4,
#else
        4,
#endif
        0, 8);

    public static int GetLootMaximumLooseAmmunitionRoundsPerSession() => ClampInt(
#if SPT_CLIENT
        lootMaximumLooseAmmunitionRoundsPerSession?.Value ?? 180,
#else
        180,
#endif
        0, 600);

    public static int GetLootMaximumWeaponsPerSession() => ClampInt(
#if SPT_CLIENT
        lootMaximumWeaponsPerSession?.Value ?? 1,
#else
        1,
#endif
        0, 2);

    private static void RaiseChanged()
    {
#if SPT_CLIENT
        RefreshDiagnosticRuntimePolicy();
        if (auditLevel != null)
        {
            VanguardClientDiagnosticsLog.SetAuditLevel(auditLevel.Value, "f12_change");
        }
#endif
#if SPT_CLIENT
        if (suppressChangedEvents)
        {
            return;
        }
#endif
        Changed?.Invoke();
    }

    private static void RefreshDiagnosticRuntimePolicy()
    {
#if SPT_CLIENT
        cachedCombatDiagnosticsScope = Enum.TryParse(
            NormalizeCombatDiagnosticsScope(combatDiagnosticsScope?.Value),
            true,
            out VanguardCombatDiagnosticsScope combatScope)
            ? combatScope
            : VanguardCombatDiagnosticsScope.Off;
        cachedPerformanceTelemetryMode = Enum.TryParse(
            NormalizePerformanceTelemetry(performanceTelemetry?.Value),
            true,
            out VanguardPerformanceTelemetryMode telemetryMode)
            ? telemetryMode
            : VanguardPerformanceTelemetryMode.SlowCallsOnly;
        cachedDetailedDiagnosticPayloads = detailedDiagnosticPayloads?.Value ?? false;
#endif
    }

    private static string NormalizeAuditLevel(string? value)
    {
        return VanguardDiagnosticsPolicy.Parse(value).ToString();
    }

    private static string NormalizeCombatDiagnosticsScope(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "operatorsonly" => "OperatorsOnly",
            "allbots" => "AllBots",
            _ => "Off"
        };
    }

    private static string NormalizePerformanceTelemetry(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "off" => "Off",
            "full" => "Full",
            _ => "SlowCallsOnly"
        };
    }

    private static float Clamp(float value, float min, float max)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return min;
        }

        return Math.Max(min, Math.Min(max, value));
    }

    private static int ClampInt(int value, int min, int max)
    {
        return Math.Max(min, Math.Min(max, value));
    }
}
