#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Produces bounded diagnostics/telemetry for Threat Scan Dry Run Logger in the intent production pipeline.
// Flow: Runtime facts are normalized, deduplicated/rate-gated where needed, then emitted according to Vanguard presentation levels.
// Authority boundary: Observation only; telemetry never changes the gameplay decision it reports.
// Invariant: Operational output stays actionable and repetitive detail remains restricted to diagnostic/trace levels.
namespace Vanguard.Client.Runtime.Intents;

internal static partial class VanguardOperatorIntentDryRunService
{
private static void LogThreatScanIfNeeded(VanguardIntentDryRunBoard board, DateTimeOffset now)
    {
        var scan = board.Snapshot.ThreatScan;
        if (!VanguardOperatorRuntimeAuditOptions.GetThreatScannerDryRunEnabled() || !scan.Enabled || !scan.CombatContext || !scan.Scanned)
        {
            return;
        }

        var state = GetOrCreateThreatScanState(board.Snapshot.BotProfileId);
        RegisterThreatScanCounters(state, scan);

        bool logImmediate = ShouldLogThreatScanImmediate(state, scan, now);
        if (scan.WouldPromote)
        {
            if (logImmediate)
            {
                state.WouldPromoteLogged++;
            }
            else
            {
                state.WouldPromoteSuppressed++;
                state.CooldownBlocked++;
            }
        }

        if (logImmediate)
        {
            VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OperatorThreatScannerPromotionLatchStatusTag, FormatThreatScan(board));
            state.ImmediateSignature = scan.DecisionSignature;
            state.LastCandidateKey = scan.CandidateThreatId;
            state.LastDecision = scan.WouldPromote ? "would_promote" : "keep_current";
            state.LastReason = scan.PromotionReason;
            state.LastImmediateLogAtUtc = now;
            if (scan.WouldPromote)
            {
                state.LastWouldPromoteAtUtc = now;
                state.LastWouldPromoteSignature = ThreatScanPromotionSignature(scan);
                state.LastWouldPromoteScore = scan.CandidateScore;
                state.LastWouldPromoteVisible = scan.CandidateVisible;
                state.LastWouldPromoteLineOfSight = scan.CandidateLineOfSight;
                state.LastWouldPromoteCanShoot = scan.CandidateCanShoot;
            }
        }

        var summaryInterval = TimeSpan.FromSeconds(Math.Max(10d, VanguardOperatorRuntimeAuditOptions.GetSummaryIntervalSeconds()));
        if (now - state.LastSummaryAtUtc >= summaryInterval)
        {
            VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OperatorThreatScannerPromotionLatchStatusTag, FormatThreatScanSummary(board, state));
            state.LastSummaryAtUtc = now;
            state.ResetCounters();
        }
    }

private static void RegisterThreatScanCounters(ThreatScanLogState state, VanguardThreatScanDecisionSnapshot scan)
    {
        state.Scans++;
        bool hasCandidate = !string.Equals(scan.CandidateThreatId, "none", StringComparison.OrdinalIgnoreCase);
        if (!hasCandidate)
        {
            state.NoCandidate++;
        }

        if (scan.WouldPromote)
        {
            state.WouldPromote++;
        }
        else
        {
            state.KeepCurrent++;
            if (hasCandidate)
            {
                state.CurrentTargetKept++;
            }
        }

        if (scan.CandidateIncomingFireFresh) state.IncomingFireFresh++;
        if (scan.CandidateIncomingFireStale) state.IncomingFireStale++;
        if (scan.CandidateVisible) state.VisibleCandidates++;
        if (scan.CandidateLineOfSight) state.LineOfSightCandidates++;
        if (scan.CandidateCanShoot) state.CanShootCandidates++;
        if (IsRearOrFlank(scan.CandidateArc)) state.RearOrFlankCandidates++;
    }

private static bool ShouldLogThreatScanImmediate(ThreatScanLogState state, VanguardThreatScanDecisionSnapshot scan, DateTimeOffset now)
    {
        if (scan.WouldPromote)
        {
            return ShouldLogThreatScanWouldPromote(state, scan, now);
        }

        if (VanguardOperatorRuntimeAuditOptions.GetVerboseTransitionLogEnabled())
        {
            var verboseInterval = TimeSpan.FromSeconds(VanguardOperatorRuntimeAuditOptions.GetThreatScannerIntervalSeconds());
            return now - state.LastImmediateLogAtUtc >= verboseInterval;
        }

        if (!HasThreatScanCandidate(scan))
        {
            return false;
        }

        if (!IsThreatScanCandidateInteresting(scan))
        {
            return false;
        }

        if (!string.Equals(state.LastCandidateKey, scan.CandidateThreatId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(state.LastReason, scan.PromotionReason, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string decision = scan.WouldPromote ? "would_promote" : "keep_current";
        if (!string.Equals(state.LastDecision, decision, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(state.ImmediateSignature, scan.DecisionSignature, StringComparison.Ordinal))
        {
            var minInterval = TimeSpan.FromSeconds(Math.Max(5d, VanguardOperatorRuntimeAuditOptions.GetThreatScannerIntervalSeconds() * 5d));
            return now - state.LastImmediateLogAtUtc >= minInterval;
        }

        return false;
    }

private static bool ShouldLogThreatScanWouldPromote(ThreatScanLogState state, VanguardThreatScanDecisionSnapshot scan, DateTimeOffset now)
    {
        if (VanguardOperatorRuntimeAuditOptions.GetVerboseTransitionLogEnabled())
        {
            var verboseInterval = TimeSpan.FromSeconds(VanguardOperatorRuntimeAuditOptions.GetThreatScannerIntervalSeconds());
            return now - state.LastImmediateLogAtUtc >= verboseInterval;
        }

        string signature = ThreatScanPromotionSignature(scan);
        if (!string.Equals(state.LastWouldPromoteSignature, signature, StringComparison.Ordinal))
        {
            return true;
        }

        if (!state.LastWouldPromoteVisible && scan.CandidateVisible)
        {
            return true;
        }

        if (!state.LastWouldPromoteLineOfSight && scan.CandidateLineOfSight)
        {
            return true;
        }

        if (!state.LastWouldPromoteCanShoot && scan.CandidateCanShoot)
        {
            return true;
        }

        if (scan.CandidateScore - state.LastWouldPromoteScore >= 25f)
        {
            return true;
        }

        var relogInterval = TimeSpan.FromSeconds(Math.Max(8d, VanguardOperatorRuntimeAuditOptions.GetThreatScannerIntervalSeconds() * 8d));
        return now - state.LastWouldPromoteAtUtc >= relogInterval;
    }

private static string ThreatScanPromotionSignature(VanguardThreatScanDecisionSnapshot scan)
    {
        return string.Join("|",
            scan.CandidateThreatId,
            scan.PromotionReason,
            scan.CandidateArc,
            scan.CandidateIncomingFireFresh ? "fresh_fire" : "no_fresh_fire");
    }

private static bool HasThreatScanCandidate(VanguardThreatScanDecisionSnapshot scan)
    {
        return !string.Equals(scan.CandidateThreatId, "none", StringComparison.OrdinalIgnoreCase);
    }

private static bool IsThreatScanCandidateInteresting(VanguardThreatScanDecisionSnapshot scan)
    {
        return scan.CandidateVisible
            || scan.CandidateLineOfSight
            || scan.CandidateCanShoot
            || scan.CandidateShotMeRecently
            || scan.CandidateShotAtMeRecently
            || IsRearOrFlank(scan.CandidateArc);
    }

private static bool IsRearOrFlank(string? arc)
    {
        if (string.IsNullOrWhiteSpace(arc))
        {
            return false;
        }

        return string.Equals(arc, "rear", StringComparison.OrdinalIgnoreCase)
            || arc.IndexOf("flank", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
#endif
