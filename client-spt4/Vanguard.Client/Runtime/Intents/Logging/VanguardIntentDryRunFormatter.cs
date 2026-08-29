#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Provides Intent Dry Run Formatter support for the intent production pipeline.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Intents;

internal static partial class VanguardOperatorIntentDryRunService
{
private static string FormatThreatScan(VanguardIntentDryRunBoard board)
    {
        var snapshot = board.Snapshot;
        var scan = snapshot.ThreatScan;
        string decision = scan.WouldPromote ? "would_promote" : "keep_current";
        return $"VANGUARD_THREAT_SCAN_SIDECAR_DRYRUN operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; nick={snapshot.Nickname}; alive={snapshot.Alive}; combatContext={scan.CombatContext}; current={scan.CurrentThreatId}; candidate={scan.CandidateThreatId}; candidateName={scan.CandidateThreatName}; decision={decision}; reason={scan.PromotionReason}; score={scan.CandidateScore:0.00}; visible={scan.CandidateVisible}; los={scan.CandidateLineOfSight}; canShoot={scan.CandidateCanShoot}; shotMe={scan.CandidateShotMeRecently}; shotAtMe={scan.CandidateShotAtMeRecently}; incomingFresh={scan.CandidateIncomingFireFresh}; incomingStale={scan.CandidateIncomingFireStale}; dist={FormatFloat(scan.CandidateDistance)}; seenAgo={FormatFloat(scan.CandidateTimeSinceSeen)}; angle={FormatFloat(scan.CandidateAngleDegrees)}; arc={scan.CandidateArc}; known={scan.KnownCount}; visibleCount={scan.VisibleCount}; losCount={scan.LineOfSightCount}; noiseFilter=true; promotionLatch=true; readOnly=true; promotes=false; interval={VanguardOperatorRuntimeAuditOptions.GetThreatScannerIntervalSeconds():0.00}";
    }

private static string FormatThreatScanSummary(VanguardIntentDryRunBoard board, ThreatScanLogState state)
    {
        var snapshot = board.Snapshot;
        var scan = snapshot.ThreatScan;
        return $"VANGUARD_THREAT_SCAN_SIDECAR_SUMMARY operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; nick={snapshot.Nickname}; alive={snapshot.Alive}; combatContext={scan.CombatContext}; scans={state.Scans}; noCandidate={state.NoCandidate}; keepCurrent={state.KeepCurrent}; wouldPromoteRaw={state.WouldPromote}; wouldPromoteLogged={state.WouldPromoteLogged}; wouldPromoteSuppressed={state.WouldPromoteSuppressed}; incomingFireFresh={state.IncomingFireFresh}; incomingFireStale={state.IncomingFireStale}; visibleCandidates={state.VisibleCandidates}; losCandidates={state.LineOfSightCandidates}; canShootCandidates={state.CanShootCandidates}; rearOrFlankCandidates={state.RearOrFlankCandidates}; cooldownBlocked={state.CooldownBlocked}; currentTargetKept={state.CurrentTargetKept}; lastCandidate={state.LastCandidateKey}; lastDecision={state.LastDecision}; lastReason={state.LastReason}; noiseFilter=true; promotionLatch=true; readOnly=true; promotes=false";
    }

private static string FormatFloat(float? value)
    {
        return value.HasValue ? value.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "none";
    }

private static string Tri(bool? value)
    {
        return value.HasValue ? (value.Value ? "true" : "false") : "unknown";
    }

private static string FormatSelected(VanguardIntentDryRunBoard board, string kind)
    {
        var snapshot = board.Snapshot;
        var selected = board.Selected;
        int validCount = board.Candidates.Count(candidate => candidate.Valid);
        int invalidCount = board.Candidates.Count - validCount;
        string topInvalid = board.Candidates.FirstOrDefault(candidate => !candidate.Valid)?.IntentKey ?? "none";
        string medHp = snapshot.Alive ? snapshot.Medical.Need.HealthPercent.ToString("0") : "n/a";
        string selectedPlan = selected.PlanKey == "none" ? snapshot.Medical.Plan.PlanKey : selected.PlanKey;
        string selectedStep = selected.NextStep == "none" ? snapshot.Medical.Plan.NextStep : selected.NextStep;
        var execution = board.ExecutionWindow;

        return $"{kind} operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; nick={snapshot.Nickname}; alive={snapshot.Alive}; selected={selected.IntentKey}; domain={selected.Domain}; score={selected.FinalScore:0.00}; base={selected.BaseScore:0.00}; gate={selected.Gate}; reason={selected.Reason}; threat={snapshot.Threat.Classification}; scan={snapshot.ThreatScan.Classification}; scanCandidate={snapshot.ThreatScan.CandidateThreatId}; scanPromote={snapshot.ThreatScan.WouldPromote}; sain={snapshot.Sain.Classification}; move={snapshot.Movement.Classification}; medical={snapshot.Medical.Classification}; medNeed={snapshot.Medical.Need.DominantNeed}; medHp={medHp}; medTarget={snapshot.Medical.Need.TargetPart}; medItem={snapshot.Medical.Actionability.SelectedItemName}; medItemAvailable={snapshot.Medical.Actionability.RequiredItemAvailable}; medCanApply={Tri(snapshot.Medical.Actionability.CanApplyItem)}; medPlan={selectedPlan}; medStep={selectedStep}; medPlanKind={snapshot.Medical.Plan.ExecutionKind}; medSafety={snapshot.Medical.Plan.SafetyGate}; medRetry={snapshot.Medical.Plan.RetryPolicy}; medWouldExecuteIfActive={snapshot.Medical.Plan.WouldExecuteIfActive}; awareness={snapshot.Awareness.Classification}; awarenessKind={snapshot.Awareness.StimulusKind}; awarenessTarget={snapshot.Awareness.CandidateId}; awarenessScore={snapshot.Awareness.Score:0.00}; awarenessConfidence={snapshot.Awareness.Confidence:0.00}; awarenessOrient={snapshot.Awareness.ShouldOrientAttention}; awarenessPropagate={snapshot.Awareness.WouldPropagateConfirmedThreat}; awarenessPromote={snapshot.Awareness.WouldPromoteSainTarget}; awarenessRelease={snapshot.Awareness.WouldReleaseFormation}; awarenessBreakMedical={snapshot.Awareness.WouldBreakMedical}; cohesion={snapshot.SquadCohesion.Classification}; bubble={snapshot.SquadCohesion.BubbleBand}; bubbleIn={snapshot.SquadCohesion.InBubble}; bubbleDist={snapshot.SquadCohesion.OperatorDistanceToOwner:0.00}; sector={snapshot.SquadCohesion.Sector}; sectorRole={snapshot.SquadCohesion.TacticalRole}; sectorDup={snapshot.SquadCohesion.SectorDuplicate}; rearCount={snapshot.SquadCohesion.RearSectorCount}; usefulSector={snapshot.SquadCohesion.UsefulPosition}; cohesionIntent={snapshot.SquadCohesion.RecommendedIntent}; ownerAnchor={snapshot.SquadCohesion.OwnerAnchorSource}; ownerReliable={snapshot.SquadCohesion.OwnerReliableForActiveMovement}; ownerAge={snapshot.SquadCohesion.OwnerAnchorAgeSeconds:0.00}; sainEnvelope={snapshot.SquadCohesion.SainEnvelope}; squadOrder={snapshot.SquadCohesion.SquadOrder}; moveAuth={snapshot.MovementAuthority.Classification}; moveOwner={snapshot.MovementAuthority.CurrentAuthority}; moveSoftOut={snapshot.MovementAuthority.SoftOutsideBubble}; moveHardOut={snapshot.MovementAuthority.HardOutsideBubble}; sainSearchLike={snapshot.MovementAuthority.SainSearchLike}; sainEnvViolation={snapshot.MovementAuthority.SainEnvelopeViolation}; sainEnvReason={snapshot.MovementAuthority.SainEnvelopeViolationReason}; lootAllowed={snapshot.MovementAuthority.LootingBotsAllowed}; lootSuppress={snapshot.MovementAuthority.LootingBotsWouldSuppress}; corpseLoot={snapshot.CorpseLoot.Classification}; corpse={snapshot.CorpseLoot.CandidateCorpseId}; corpseGate={snapshot.CorpseLoot.Gate}; corpseReason={snapshot.CorpseLoot.Inventory.HighestPriorityReason}; corpseScore={snapshot.CorpseLoot.UtilityScore:0.00}; corpseExecution={snapshot.CorpseLoot.ExecutionEnabled}; orbitAllowed={snapshot.MovementAuthority.OrbitAllowed}; orbitSuppress={snapshot.MovementAuthority.OrbitWouldSuppress}; broker={snapshot.MovementAuthority.BrokerPlan.PlanKey}; brokerContract={snapshot.MovementAuthority.BrokerPlan.Contract.ContractKey}; brokerBackend={snapshot.MovementAuthority.BrokerPlan.Backend}; brokerLease={snapshot.MovementAuthority.BrokerPlan.WouldOpenLease}; brokerRequest={snapshot.MovementAuthority.BrokerPlan.RequestKind}; brokerAnchor={snapshot.MovementAuthority.BrokerPlan.AnchorKind}; leaseKey={snapshot.MovementAuthority.BrokerPlan.LeasePlan.LeaseKey}; leaseEligible={snapshot.MovementAuthority.BrokerPlan.LeasePlan.Eligible}; leaseApply={snapshot.MovementAuthority.BrokerPlan.LeasePlan.ApplyEnabled}; leaseRadius={snapshot.MovementAuthority.BrokerPlan.LeasePlan.AnchorRadiusMeters:0.00}; leaseReapply={snapshot.MovementAuthority.BrokerPlan.LeasePlan.ReapplyPolicy}; leaseComplete={snapshot.MovementAuthority.BrokerPlan.LeasePlan.CompletionRule}; leaseInterrupt={snapshot.MovementAuthority.BrokerPlan.LeasePlan.InterruptionRule}; execContract={execution.ContractKey}; execKind={execution.WindowKind}; execMin={execution.MinDurationSeconds}; execMax={execution.MaxDurationSeconds}; execNoProgress={execution.NoProgressTimeoutSeconds}; execProgress={execution.ProgressSignals}; execInterrupt={execution.InterruptionRules}; execFallback={execution.FallbackIntentKey}; execOutcome={execution.OutcomePreview}; execWouldOpenIfActive={execution.WouldOpenIfActive}; validCandidates={validCount}; invalidCandidates={invalidCount}; topInvalid={topInvalid}; intentBoardReadOnly=true; intentBoardExecutesActions=false; activeCorpseApproach={snapshot.CorpseLoot.ExecutionEnabled}; corpseInteraction=false; inventoryTransactions=false";
    }
}
#endif
