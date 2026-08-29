#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;

// Responsibility: Builds the compact per-Operator decision snapshot used by diagnostics and HUD telemetry to explain what Vanguard currently believes and why.
// Flow: Read-only facts from combat, medical, loot, movement and authority services are assembled into one snapshot, cached by bot profile, and emitted only when meaningfully changed or due for summary.
// Authority boundary: Snapshot construction observes decisions made elsewhere; it cannot acquire leases, move bots, mutate inventory, heal, or choose combat targets.
// Invariant: The latest snapshot must describe current raid evidence only, and logging/telemetry throttling must never alter gameplay decisions.
namespace Vanguard.Client.Runtime.Decision;

internal static class VanguardOperatorDecisionSnapshotService
{
    private sealed class LastDecisionLogState
    {
        public string Signature = string.Empty;
        public string MeaningfulSignature = string.Empty;
        public DateTimeOffset LastTransitionAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastSummaryAtUtc = DateTimeOffset.MinValue;
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, LastDecisionLogState> LastByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, OperatorDecisionSnapshot> LatestByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static volatile OperatorDecisionSnapshot[] latestSnapshots = Array.Empty<OperatorDecisionSnapshot>();
    private static readonly VanguardOperatorDecisionSnapshotBuilder Builder = new();
    private static DateTimeOffset lastSnapshotAtUtc = DateTimeOffset.MinValue;
    private static int nextOwnerIndex;
    private static readonly Dictionary<string, int> NextBotIndexByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static bool bootLogged;

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            LastByBotProfileId.Clear();
            LatestByBotProfileId.Clear();
            latestSnapshots = Array.Empty<OperatorDecisionSnapshot>();
        }

        Builder.ResetForRaidLifecycle();
        VanguardMedicalEffectReader.ResetForRaidLifecycle(reason);
        lastSnapshotAtUtc = DateTimeOffset.MinValue;
        nextOwnerIndex = 0;
        NextBotIndexByOwner.Clear();
        bootLogged = false;
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorDecisionSnapshotStatusTag,
            $"decision snapshot runtime state reset reason={reason}; readOnly=true");
    }

    public static IReadOnlyList<OperatorDecisionSnapshot> GetLatestSnapshots() => latestSnapshots;

    public static bool TryGetLatestSnapshot(string botProfileId, out OperatorDecisionSnapshot snapshot)
    {
        lock (Sync)
        {
            if (LatestByBotProfileId.TryGetValue(botProfileId, out var value))
            {
                snapshot = value;
                return true;
            }
        }

        snapshot = OperatorDecisionSnapshot.Empty;
        return false;
    }

    public static void Tick()
    {
        // audit subsystem foundation: decision snapshots are explicitly post-spawn and read-only.
        // The load guard only opens once Vanguard has registered at least one Operator
        // with a live BotOwner, preserving the validated spawn/owner binding order.
        if (!VanguardOperatorRuntimeAuditLoadGuard.IsOpen())
        {
            return;
        }

        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }

        // Vanguard: snapshots are a core runtime dependency of the scheduler, awareness,
        // medical and cohesion systems. Diagnostic switches only control log emission.
        bool auditEnabled = VanguardOperatorRuntimeAuditSyncService.EffectiveEnabled;
        bool activeMobileMedicalEnabled = VanguardOperatorRuntimeAuditOptions.GetFirstActiveMobileMedicalLeaseEnabled();
        bool snapshotLogsEnabled = auditEnabled && VanguardOperatorRuntimeAuditOptions.GetDecisionSnapshotLogEnabled();
        bool intentDryRunEnabled = auditEnabled && VanguardOperatorRuntimeAuditOptions.GetIntentDryRunEnabled();
        bool threatScannerLogsEnabled = auditEnabled && VanguardOperatorRuntimeAuditOptions.GetThreatScannerDryRunEnabled();
        bool runtimeDecisionSnapshotsEnabled = true;

        var now = DateTimeOffset.UtcNow;
        if (now - lastSnapshotAtUtc < TimeSpan.FromSeconds(VanguardOperatorRuntimeAuditOptions.GetSnapshotIntervalSeconds()))
        {
            return;
        }

        lastSnapshotAtUtc = now;
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.OperatorDecisionSnapshotStatusTag,
                $"VANGUARD_OPERATOR_DECISION_SNAPSHOT_BOOT enabled=true; readOnly=true; authority=headless_or_host; postSpawnOnly=true; headless={VanguardFikaCompat.IsHeadless}; host={VanguardFikaCompat.IsHost}; build={VanguardBuildVersion.BuildLabel}; snapshotLogs={snapshotLogsEnabled}; intentDryRun={intentDryRunEnabled}; threatScannerRuntime=true; threatScannerLogs={threatScannerLogsEnabled}; runtimeDecisionSnapshots={runtimeDecisionSnapshotsEnabled}; firstActiveMobileMedicalLease={activeMobileMedicalEnabled}; verboseTransitions={VanguardOperatorRuntimeAuditOptions.GetVerboseTransitionLogEnabled()}");
        }

        var records = VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators();
        string extendedBotProfileId = SelectExtendedRefreshBotProfileId(records);
        var nextSnapshots = new List<OperatorDecisionSnapshot>(records.Count);
        foreach (var record in records)
        {
            if (record.BotOwner == null)
            {
                continue;
            }

            try
            {
                bool refreshExtended = string.Equals(record.BotProfileId, extendedBotProfileId, StringComparison.OrdinalIgnoreCase);
                var snapshot = Builder.Capture(record, refreshExtended);
                nextSnapshots.Add(snapshot);

                if (!snapshotLogsEnabled)
                {
                    continue;
                }

                if (ShouldLogTransition(snapshot, now))
                {
                    VanguardClientDiagnosticsLog.Diagnostic(
                        VanguardBuildVersion.OperatorDecisionSnapshotStatusTag,
                        () => Format(snapshot, "VANGUARD_OPERATOR_DECISION_SNAPSHOT_CHANGED"));
                }

                if (VanguardOperatorRuntimeAuditOptions.GetSummaryLogEnabled() && ShouldLogSummary(snapshot, now))
                {
                    VanguardClientDiagnosticsLog.Diagnostic(
                        VanguardBuildVersion.OperatorDecisionSnapshotStatusTag,
                        () => Format(snapshot, "VANGUARD_OPERATOR_DECISION_SNAPSHOT_SUMMARY"));
                }
            }
            catch (Exception exception)
            {
                if (TryGetLatestSnapshot(record.BotProfileId, out OperatorDecisionSnapshot previous)
                    && previous != OperatorDecisionSnapshot.Empty)
                {
                    nextSnapshots.Add(previous);
                }

                VanguardClientDiagnosticsLog.Warning(
                    VanguardBuildVersion.OperatorDecisionSnapshotStatusTag,
                    () => $"decision snapshot capture failed operator={record.OperatorId}; botProfile={record.BotProfileId}; reason={exception.GetType().Name}: {exception.Message}");
            }
        }

        OperatorDecisionSnapshot[] immutableView = nextSnapshots.ToArray();
        lock (Sync)
        {
            LatestByBotProfileId.Clear();
            foreach (OperatorDecisionSnapshot snapshot in immutableView)
            {
                LatestByBotProfileId[snapshot.BotProfileId] = snapshot;
            }
            latestSnapshots = immutableView;
        }
    }

    private static string SelectExtendedRefreshBotProfileId(IReadOnlyList<VanguardRaidOperatorRuntimeRecord> records)
    {
        var owners = records
            .Where(record => record.BotOwner != null && !string.IsNullOrWhiteSpace(record.OwnerProfileId))
            .Select(record => record.OwnerProfileId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (owners.Length == 0)
        {
            return "none";
        }

        nextOwnerIndex = Math.Abs(nextOwnerIndex) % owners.Length;
        string owner = owners[nextOwnerIndex];
        nextOwnerIndex = (nextOwnerIndex + 1) % owners.Length;
        var ownerRecords = records
            .Where(record => record.BotOwner != null && string.Equals(record.OwnerProfileId, owner, StringComparison.OrdinalIgnoreCase))
            .OrderBy(record => record.BotProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ownerRecords.Length == 0)
        {
            return "none";
        }

        NextBotIndexByOwner.TryGetValue(owner, out int index);
        index = Math.Abs(index) % ownerRecords.Length;
        NextBotIndexByOwner[owner] = (index + 1) % ownerRecords.Length;
        return ownerRecords[index].BotProfileId;
    }

    private static bool ShouldLogTransition(OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        var state = GetOrCreateState(snapshot.BotProfileId);
        bool verbose = VanguardOperatorRuntimeAuditOptions.GetVerboseTransitionLogEnabled();
        string signature = verbose ? snapshot.DecisionSignature : MeaningfulSignature(snapshot);
        string previous = verbose ? state.Signature : state.MeaningfulSignature;
        if (string.Equals(previous, signature, StringComparison.Ordinal))
        {
            return false;
        }

        if (!verbose && state.LastTransitionAtUtc != DateTimeOffset.MinValue)
        {
            var minInterval = TimeSpan.FromSeconds(VanguardOperatorRuntimeAuditOptions.GetTransitionLogMinIntervalSeconds());
            if (now - state.LastTransitionAtUtc < minInterval)
            {
                if (verbose)
                {
                    state.Signature = signature;
                }
                else
                {
                    state.MeaningfulSignature = signature;
                }

                return false;
            }
        }

        if (verbose)
        {
            state.Signature = signature;
        }
        else
        {
            state.MeaningfulSignature = signature;
        }

        state.LastTransitionAtUtc = now;
        return true;
    }

    private static bool ShouldLogSummary(OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        var state = GetOrCreateState(snapshot.BotProfileId);
        if (now - state.LastSummaryAtUtc < SummaryIntervalFor(snapshot))
        {
            return false;
        }

        state.LastSummaryAtUtc = now;
        return true;
    }

    private static TimeSpan SummaryIntervalFor(OperatorDecisionSnapshot snapshot)
    {
        double seconds = VanguardOperatorRuntimeAuditOptions.GetSummaryIntervalSeconds();
        if (!snapshot.Alive)
        {
            seconds = Math.Max(60d, seconds * 3d);
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static LastDecisionLogState GetOrCreateState(string botProfileId)
    {
        lock (Sync)
        {
            if (!LastByBotProfileId.TryGetValue(botProfileId, out var state))
            {
                state = new LastDecisionLogState();
                LastByBotProfileId[botProfileId] = state;
            }

            return state;
        }
    }

    private static string MeaningfulSignature(OperatorDecisionSnapshot snapshot)
    {
        return string.Join("|",
            snapshot.Alive ? "alive" : "dead",
            snapshot.Alive ? snapshot.Threat.Classification : "threat_terminal_dead",
            snapshot.Alive ? snapshot.Sain.Classification : "sain_terminal_dead",
            snapshot.Alive ? snapshot.ThreatScan.Classification : "threat_scan_terminal_dead",
            snapshot.Alive && snapshot.ThreatScan.WouldPromote ? snapshot.ThreatScan.CandidateThreatId : "threat_scan_keep",
            snapshot.Alive ? snapshot.Awareness.Classification : "awareness_terminal_dead",
            snapshot.Alive && snapshot.Awareness.WouldReleaseFormation ? snapshot.Awareness.CandidateId : "awareness_keep",
            snapshot.Medical.Classification,
            snapshot.Alive && snapshot.Movement.Classification == "movement_path_stalled" ? "movement_path_stalled" : "movement_other",
            snapshot.Alive && snapshot.Looting.Classification == "loot_active" ? "loot_active" : "loot_other",
            snapshot.CorpseLoot.Classification,
            snapshot.CorpseLoot.CandidateFound ? snapshot.CorpseLoot.CandidateCorpseId : "corpse_none",
            snapshot.CorpseLoot.Gate,
            snapshot.Alive && snapshot.Orbit.Active ? snapshot.Orbit.Classification : "orbit_inactive");
    }

    private static string Format(OperatorDecisionSnapshot snapshot, string kind)
    {
        return $"{kind} operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; owner={snapshot.OwnerProfileId}; nick={snapshot.Nickname}; alive={snapshot.Alive}; pos={FormatVector(snapshot.Position)}; speed={snapshot.RealSpeed:0.00}; move={snapshot.Movement.Classification}; path={Tri(snapshot.Movement.HasPath)}; dist={Float(snapshot.Movement.DistanceToDestination)}; brain={snapshot.Brain.Classification}; layer={snapshot.Brain.ActiveLayer}; node={snapshot.Brain.Node}; sain={snapshot.Sain.Classification}; sainCombat={Tri(snapshot.Sain.IsInCombat)}; threat={snapshot.Threat.Classification}; enemyVisible={Tri(snapshot.Threat.EnemyVisible)}; enemyCanShoot={Tri(snapshot.Threat.EnemyCanShoot)}; enemyDist={Float(snapshot.Threat.Distance)}; seenAgo={Float(snapshot.Threat.TimeSinceSeen)}; threatScan={snapshot.ThreatScan.Classification}; scanCandidate={snapshot.ThreatScan.CandidateThreatId}; scanPromote={snapshot.ThreatScan.WouldPromote}; scanReason={snapshot.ThreatScan.PromotionReason}; awareness={snapshot.Awareness.Classification}; awarenessKind={snapshot.Awareness.StimulusKind}; awarenessTarget={snapshot.Awareness.CandidateId}; awarenessOrient={snapshot.Awareness.ShouldOrientAttention}; awarenessPromote={snapshot.Awareness.WouldPromoteSainTarget}; awarenessRelease={snapshot.Awareness.WouldReleaseFormation}; medical={snapshot.Medical.Classification}; medNeed={snapshot.Medical.Need.DominantNeed}; medHp={(snapshot.Alive ? snapshot.Medical.Need.HealthPercent.ToString("0") : "n/a")}; medTarget={snapshot.Medical.Need.TargetPart}; medItem={snapshot.Medical.Actionability.SelectedItemName}; medItemAvailable={snapshot.Medical.Actionability.RequiredItemAvailable}; medCanApply={Tri(snapshot.Medical.Actionability.CanApplyItem)}; medSafety={snapshot.Medical.Safety.Reason}; medImmediateBlock={snapshot.Medical.Safety.ImmediateCombatBlock}; medCoveredSuppression={snapshot.Medical.Safety.CoveredSuppressionOpportunity}; medIncomingFire={snapshot.Medical.Safety.IncomingFireRecent}; medThreatDist={Float(snapshot.Medical.Safety.ThreatDistance)}; loot={snapshot.Looting.Classification}; corpseLoot={snapshot.CorpseLoot.Classification}; corpse={snapshot.CorpseLoot.CandidateCorpseId}; corpseGate={snapshot.CorpseLoot.Gate}; corpseReason={snapshot.CorpseLoot.Inventory.HighestPriorityReason}; corpseScore={snapshot.CorpseLoot.UtilityScore:0.0}; orbit={snapshot.Orbit.Classification}; snapshotReadOnly=true; corpseApproachExecution={snapshot.CorpseLoot.ExecutionEnabled}; corpseInteraction=false; inventoryTransactions=false; verbose={VanguardOperatorRuntimeAuditOptions.GetVerboseTransitionLogEnabled()}";
    }

    private static string Tri(bool? value)
    {
        return value.HasValue ? (value.Value ? "true" : "false") : "unknown";
    }

    private static string Float(float? value)
    {
        return value.HasValue ? value.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "unknown";
    }

    private static string FormatVector(UnityEngine.Vector3 value)
    {
        return $"{value.x:0.0},{value.y:0.0},{value.z:0.0}";
    }
}
#endif
