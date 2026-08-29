#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;

// Responsibility: turns corpse-loot registration, candidate, squad-selection, approach and transaction changes into bounded player/support diagnostics.
// Flow: Lifecycle calls record explicit events while periodic observation compares per-Operator/corpse signatures, suppresses unchanged repeats, emits meaningful transitions/heartbeats at the configured level, and flushes all remembered state on raid reset.
// Authority boundary: observation only: telemetry reads outcomes produced by loot services and never changes candidate scores, target ownership, movement or inventory transfers.
// Invariant: the same unchanged state cannot spam Operational output, terminal events remain attributable to one Operator/target, and all deduplication memory is raid-scoped.
namespace Vanguard.Client.Runtime.Loot;

internal static class VanguardCorpseLootOperationalTelemetry
{
    private sealed class TransitionState
    {
        public string Signature = "none";
        public DateTimeOffset LastLoggedAtUtc = DateTimeOffset.MinValue;
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, TransitionState> CandidateStateByOperatorAndCorpse = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, TransitionState> SquadSelectionByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastCountedSnapshotByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(45);

    private static long registeredCorpses;
    private static long registeredAiCorpses;
    private static long registeredOperatorCorpses;
    private static long registeredPlayerCorpses;
    private static long evaluatedSnapshots;
    private static long nearbyEvaluations;
    private static long playerExclusions;
    private static long friendlyExclusions;
    private static long hostilityUnverifiedExclusions;
    private static long outcomeMemoryExclusions;
    private static long notUsefulExclusions;
    private static long noDestinationExclusions;
    private static long pathIncompleteEvaluations;
    private static long pathBudgetDeferredEvaluations;
    private static long planBudgetDeferredEvaluations;
    private static long includedEvaluations;
    private static long eligibleOperatorCandidates;
    private static long selectedSquadAssignments;
    private static long plannedItems;
    private static long feasiblePlannedItems;
    private static long blockedPlannedItems;
    private static double totalEvaluationMilliseconds;
    private static double maxEvaluationMilliseconds;
    private static bool authorityObserved;
    private static long approachPlanRejected;
    private static long approachStarted;
    private static long approachProgress;
    private static long approachCompleted;
    private static long approachInterrupted;
    private static long approachFailed;
    private static long approachTimeout;
    private static long transactionSubmitAttempted;
    private static long transactionSubmitCallReturned;
    private static long transactionSubmissionUncertain;
    private static long transactionConfirmed;
    private static long transactionFailed;
    private static long transactionResultUncertain;
    private static DateTimeOffset lastObservedSnapshotGenerationUtc = DateTimeOffset.MinValue;

    public static void RecordCorpseRegistered(VanguardCorpseRegistryEntry entry)
    {
        lock (Sync)
        {
            authorityObserved |= VanguardFikaCompat.IsRaidAuthority || VanguardFikaCompat.IsHeadless || VanguardFikaCompat.IsHost;
            registeredCorpses++;
            if (entry.VictimWasOperator) registeredOperatorCorpses++;
            if (entry.VictimIsAi) registeredAiCorpses++;
            else if (!entry.VictimWasOperator) registeredPlayerCorpses++;
        }
    }

    public static void RecordApproachPlanRejected(string botProfileId, string corpseId, string reason)
    {
        lock (Sync)
        {
            approachPlanRejected++;
        }
    }

    public static void RecordApproachStarted(string botProfileId, string corpseId, float pathDistance, float detour, float ownerAnchorDistance)
    {
        lock (Sync)
        {
            authorityObserved = true;
            approachStarted++;
        }
    }

    public static void RecordApproachProgress(string botProfileId, string corpseId, float remainingDistance)
    {
        lock (Sync)
        {
            approachProgress++;
        }
    }

    public static void RecordApproachTerminal(string outcome, string reason, string botProfileId, string corpseId)
    {
        lock (Sync)
        {
            if (string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase)) approachCompleted++;
            else if (string.Equals(outcome, "Timeout", StringComparison.OrdinalIgnoreCase)) approachTimeout++;
            else if (string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase)) approachFailed++;
            else approachInterrupted++;
        }
    }

    public static void RecordTransactionTerminal(VanguardCorpseLootTransactionOutcome? outcome)
    {
        if (outcome == null || !outcome.SubmitAttempted)
        {
            return;
        }

        lock (Sync)
        {
            transactionSubmitAttempted++;
            if (outcome.SubmitCallReturned) transactionSubmitCallReturned++;
            if (outcome.NetworkSubmissionUncertain) transactionSubmissionUncertain++;
            if (outcome.MutationConfirmed) transactionConfirmed++;
            else if (outcome.ResultUncertain) transactionResultUncertain++;
            else transactionFailed++;
        }
    }

    public static void Observe(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority || snapshots == null)
        {
            return;
        }

        DateTimeOffset generation = snapshots
            .Where(snapshot => snapshot != null)
            .Select(snapshot => snapshot.CapturedAtUtc)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        lock (Sync)
        {
            authorityObserved = true;
            if (generation <= lastObservedSnapshotGenerationUtc)
            {
                return;
            }
            lastObservedSnapshotGenerationUtc = generation;
        }

        foreach (OperatorDecisionSnapshot snapshot in snapshots)
        {
            if (snapshot == null || !snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            {
                continue;
            }

            CountSnapshotOnce(snapshot);
            ObserveCandidateTransitions(snapshot, now);
        }

        ObserveSquadSelections(snapshots, now);
    }

    public static void FlushAndReset(string reason)
    {
        string summary;
        bool hasData;
        bool emitSummary;
        lock (Sync)
        {
            hasData = registeredCorpses > 0 || evaluatedSnapshots > 0 || selectedSquadAssignments > 0 || approachStarted > 0;
            emitSummary = authorityObserved;
            double averageMs = evaluatedSnapshots > 0 ? totalEvaluationMilliseconds / evaluatedSnapshots : 0d;
            summary = $"VANGUARD_CORPSE_LOOT_RAID_SUMMARY reason={Safe(reason)}; registered={registeredCorpses}; ai={registeredAiCorpses}; operators={registeredOperatorCorpses}; player={registeredPlayerCorpses}; snapshots={evaluatedSnapshots}; nearby={nearbyEvaluations}; playerExcluded={playerExclusions}; friendlyExcluded={friendlyExclusions}; hostilityUnverified={hostilityUnverifiedExclusions}; outcomeMemoryExcluded={outcomeMemoryExclusions}; notUseful={notUsefulExclusions}; noDestination={noDestinationExclusions}; pathIncomplete={pathIncompleteEvaluations}; candidateScanTruncated={pathBudgetDeferredEvaluations}; nativePlanTruncated={planBudgetDeferredEvaluations}; included={includedEvaluations}; eligibleOperators={eligibleOperatorCandidates}; squadAssignmentSelections={selectedSquadAssignments}; plannedItems={plannedItems}; feasibleItems={feasiblePlannedItems}; blockedItems={blockedPlannedItems}; approachPlanRejected={approachPlanRejected}; approachStarted={approachStarted}; approachProgress={approachProgress}; approachCompleted={approachCompleted}; approachInterrupted={approachInterrupted}; approachFailed={approachFailed}; approachTimeout={approachTimeout}; transactionSubmitAttempted={transactionSubmitAttempted}; transactionSubmitCallReturned={transactionSubmitCallReturned}; transactionSubmissionUncertain={transactionSubmissionUncertain}; transactionConfirmed={transactionConfirmed}; transactionFailed={transactionFailed}; transactionResultUncertain={transactionResultUncertain}; evalAverageMs={averageMs:0.000}; evalMaxMs={maxEvaluationMilliseconds:0.000}; authorityObserved={authorityObserved}; inventoryPlanReadOnly=false; movement=true; corpseLease=true; utilityItemClaims=true; corpseInteraction=true; transactions=true; atomicPerItem=true; sequentialSession=false; singleUtilityClaimPerVisit=true; ownerSquadTerminal=false; contextRevisionBound=true; genericItems=true; secondaryAtomicSwap=true; weaponModExtraction=true; looseAmmunition=true; runtimeReadBack=true; operatorCorpseCommit=false; persistence=false";

            CandidateStateByOperatorAndCorpse.Clear();
            SquadSelectionByOwner.Clear();
            LastCountedSnapshotByBot.Clear();
            lastObservedSnapshotGenerationUtc = DateTimeOffset.MinValue;
            registeredCorpses = 0;
            registeredAiCorpses = 0;
            registeredOperatorCorpses = 0;
            registeredPlayerCorpses = 0;
            evaluatedSnapshots = 0;
            nearbyEvaluations = 0;
            playerExclusions = 0;
            friendlyExclusions = 0;
            hostilityUnverifiedExclusions = 0;
            outcomeMemoryExclusions = 0;
            notUsefulExclusions = 0;
            noDestinationExclusions = 0;
            pathIncompleteEvaluations = 0;
            pathBudgetDeferredEvaluations = 0;
            planBudgetDeferredEvaluations = 0;
            includedEvaluations = 0;
            eligibleOperatorCandidates = 0;
            selectedSquadAssignments = 0;
            plannedItems = 0;
            feasiblePlannedItems = 0;
            blockedPlannedItems = 0;
            totalEvaluationMilliseconds = 0d;
            maxEvaluationMilliseconds = 0d;
            authorityObserved = false;
            approachPlanRejected = 0;
            approachStarted = 0;
            approachProgress = 0;
            approachCompleted = 0;
            approachInterrupted = 0;
            approachFailed = 0;
            approachTimeout = 0;
            transactionSubmitAttempted = 0;
            transactionSubmitCallReturned = 0;
            transactionSubmissionUncertain = 0;
            transactionConfirmed = 0;
            transactionFailed = 0;
            transactionResultUncertain = 0;
        }

        if (hasData && emitSummary)
        {
            VanguardClientDiagnosticsLog.Operational(VanguardCorpseRegistry.StatusTag, () => summary);
        }
    }

    private static void CountSnapshotOnce(OperatorDecisionSnapshot snapshot)
    {
        lock (Sync)
        {
            DateTimeOffset evaluatedAt = snapshot.CorpseLoot.EvaluatedAtUtc;
            if (evaluatedAt == DateTimeOffset.MinValue
                || (LastCountedSnapshotByBot.TryGetValue(snapshot.BotProfileId, out DateTimeOffset last)
                    && last >= evaluatedAt))
            {
                return;
            }

            LastCountedSnapshotByBot[snapshot.BotProfileId] = evaluatedAt;
            VanguardCorpseLootDecisionSnapshot loot = snapshot.CorpseLoot;
            evaluatedSnapshots++;
            nearbyEvaluations += loot.Counts.NearbyCount;
            playerExclusions += loot.Counts.PlayerExcludedCount;
            friendlyExclusions += loot.Counts.FriendlyExcludedCount;
            hostilityUnverifiedExclusions += loot.Counts.HostilityUnverifiedCount;
            outcomeMemoryExclusions += loot.Counts.OutcomeMemoryExcludedCount;
            notUsefulExclusions += loot.Counts.NotUsefulCount;
            noDestinationExclusions += loot.Counts.NoDestinationCount;
            pathIncompleteEvaluations += loot.Counts.PathIncompleteCount;
            pathBudgetDeferredEvaluations += loot.Counts.PathBudgetDeferredCount;
            planBudgetDeferredEvaluations += loot.Counts.PlanBudgetDeferredCount;
            includedEvaluations += loot.Counts.IncludedCount;
            if (loot.CandidateFound && loot.EligibleIfActivated) eligibleOperatorCandidates++;
            plannedItems += loot.Plan.Entries.Count;
            feasiblePlannedItems += loot.Plan.FeasibleItemCount;
            blockedPlannedItems += loot.Plan.NoDestinationCount;
            totalEvaluationMilliseconds += Math.Max(0f, loot.EvaluationDurationMilliseconds);
            maxEvaluationMilliseconds = Math.Max(maxEvaluationMilliseconds, loot.EvaluationDurationMilliseconds);
        }
    }

    private static void ObserveCandidateTransitions(OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VanguardCorpseLootCandidateEvaluation evaluation in snapshot.CorpseLoot.CandidateEvaluations)
        {
            string key = snapshot.BotProfileId + "|" + evaluation.CorpseId;
            currentKeys.Add(key);
            string signature = OperationalTransitionSignature(evaluation);
            bool emit;
            lock (Sync)
            {
                if (!CandidateStateByOperatorAndCorpse.TryGetValue(key, out TransitionState state))
                {
                    state = new TransitionState();
                    CandidateStateByOperatorAndCorpse[key] = state;
                }

                emit = !string.Equals(state.Signature, signature, StringComparison.Ordinal)
                    || now - state.LastLoggedAtUtc >= HeartbeatInterval;
                if (emit)
                {
                    state.Signature = signature;
                    state.LastLoggedAtUtc = now;
                }
            }

            if (!emit)
            {
                continue;
            }

            string kind = evaluation.Included ? "VANGUARD_CORPSE_CANDIDATE_SELECTED" : "VANGUARD_CORPSE_EXCLUDED";
            VanguardClientDiagnosticsLog.Operational(VanguardCorpseRegistry.StatusTag, () =>
                $"{kind} operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; owner={Safe(snapshot.OwnerProfileId)}; corpse={Safe(evaluation.CorpseId)}; victim={Safe(evaluation.VictimProfileId)}; included={evaluation.Included}; gate={Safe(evaluation.Gate)}; relationship={Safe(evaluation.Hostility.RelationshipKind)}; relationshipSource={Safe(evaluation.Hostility.Source)}; relationshipReason={Safe(evaluation.Hostility.Reason)}; hostileConfirmed={evaluation.Hostility.HostileConfirmed}; deadOperator={evaluation.Hostility.DeadOperatorCorpse}; relationshipAge={evaluation.Hostility.AgeSeconds:0.00}; direct={evaluation.DirectDistanceMeters:0.00}; path={evaluation.PathDistanceMeters:0.00}; pathComplete={evaluation.PathComplete}; compatibilityBonus={evaluation.CompatibilityBonus:0.0}; score={evaluation.Score:0.0}; plan={Safe(evaluation.Plan.CompactSummary)}; executionCandidate=true; itemClaimRequired=true; mutationAtObservation=false");
        }

        List<string> lostKeys;
        lock (Sync)
        {
            lostKeys = CandidateStateByOperatorAndCorpse.Keys
                .Where(key => key.StartsWith(snapshot.BotProfileId + "|", StringComparison.OrdinalIgnoreCase) && !currentKeys.Contains(key))
                .ToList();
            foreach (string key in lostKeys)
            {
                CandidateStateByOperatorAndCorpse.Remove(key);
            }
        }

        foreach (string key in lostKeys)
        {
            string corpse = key[(key.IndexOf('|') + 1)..];
            VanguardClientDiagnosticsLog.Operational(VanguardCorpseRegistry.StatusTag, () =>
                $"VANGUARD_CORPSE_CANDIDATE_LOST operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; corpse={Safe(corpse)}; reason=no_longer_in_bounded_candidate_set; executionCandidate=true; itemClaimRequired=true; mutationAtObservation=false");
        }
    }

    private static void ObserveSquadSelections(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        foreach (IGrouping<string, OperatorDecisionSnapshot> squad in snapshots
                     .Where(snapshot => snapshot != null && snapshot.Alive && !string.IsNullOrWhiteSpace(snapshot.OwnerProfileId))
                     .GroupBy(snapshot => snapshot.OwnerProfileId, StringComparer.OrdinalIgnoreCase))
        {
            OperatorDecisionSnapshot? winner = squad
                .Where(snapshot => snapshot.CorpseLoot.CandidateFound
                    && snapshot.CorpseLoot.EligibleIfActivated
                    && snapshot.CorpseLoot.PathComplete
                    && snapshot.CorpseLoot.Plan.FeasibleItemCount > 0)
                .OrderByDescending(LootSelectionScore)
                .ThenBy(snapshot => snapshot.CorpseLoot.PathDistanceMeters)
                .ThenBy(snapshot => snapshot.SquadCohesion.OperatorDistanceToOwner)
                .FirstOrDefault();

            string signature = winner == null
                ? "none"
                : string.Join("|", winner.BotProfileId, winner.CorpseLoot.CandidateCorpseId, winner.CorpseLoot.Gate, winner.CorpseLoot.Plan.DecisionSignature);
            bool emit;
            bool signatureChanged;
            lock (Sync)
            {
                if (!SquadSelectionByOwner.TryGetValue(squad.Key, out TransitionState state))
                {
                    state = new TransitionState();
                    SquadSelectionByOwner[squad.Key] = state;
                }

                signatureChanged = !string.Equals(state.Signature, signature, StringComparison.Ordinal);
                emit = signatureChanged
                    || (winner != null && now - state.LastLoggedAtUtc >= HeartbeatInterval);
                if (emit)
                {
                    state.Signature = signature;
                    state.LastLoggedAtUtc = now;
                    if (winner != null) selectedSquadAssignments++;
                }
            }

            if (!emit)
            {
                continue;
            }

            if (winner == null)
            {
                VanguardClientDiagnosticsLog.Operational(VanguardCorpseRegistry.StatusTag, () =>
                    $"VANGUARD_CORPSE_LOOTER_ASSIGNMENT_NONE owner={Safe(squad.Key)}; operators={squad.Count()}; reason=no_eligible_complete_path_feasible_assignment; inventoryPlanReadOnly=false; approachMovementEnabled=true; corpseLeaseEnabled=true; itemClaimsEnabled=true; transactionsEnabled=true; singleUtilityClaimPerVisit=true; mutationAtObservation=false");
                continue;
            }

            OperatorDecisionSnapshot selectedWinner = winner;
            VanguardClientDiagnosticsLog.Operational(VanguardCorpseRegistry.StatusTag, () =>
                $"VANGUARD_CORPSE_LOOTER_ASSIGNMENT_SELECTED owner={Safe(squad.Key)}; operator={Safe(selectedWinner.OperatorId)}; botProfile={Safe(selectedWinner.BotProfileId)}; corpse={Safe(selectedWinner.CorpseLoot.CandidateCorpseId)}; victim={Safe(selectedWinner.CorpseLoot.VictimProfileId)}; relationship={Safe(selectedWinner.CorpseLoot.RelationshipKind)}; deadOperator={selectedWinner.CorpseLoot.DeadOperatorCorpse}; compatibilityBonus={selectedWinner.CorpseLoot.EquipmentCompatibilityBonus:0.0}; utilityScore={selectedWinner.CorpseLoot.UtilityScore:0.0}; selectionScore={LootSelectionScore(selectedWinner):0.0}; detour={selectedWinner.CorpseLoot.PathDistanceMeters:0.00}; ownerDistance={selectedWinner.SquadCohesion.OperatorDistanceToOwner:0.00}; usefulPosition={selectedWinner.SquadCohesion.UsefulPosition}; sectorDuplicate={selectedWinner.SquadCohesion.SectorDuplicate}; rearOverstacked={selectedWinner.SquadCohesion.RearOverstacked}; gate={Safe(selectedWinner.CorpseLoot.Gate)}; relationshipSource={Safe(selectedWinner.CorpseLoot.HostilitySource)}; plan={Safe(selectedWinner.CorpseLoot.Plan.CompactSummary)}; singlePhysicalLooterPerOwner=true; formationTopologyHardGate=false; corpseLeaseEnabled=true; itemClaimsEnabled=true; inventoryPlanReadOnly=false; movementApproachEnabled=true; transactionsEnabled=true; singleUtilityClaimPerVisit=true; mutationAtObservation=false");

            if (!signatureChanged)
            {
                continue;
            }

            foreach (VanguardCorpseLootItemPlanEntry entry in selectedWinner.CorpseLoot.Plan.Entries.Take(8))
            {
                VanguardClientDiagnosticsLog.Operational(VanguardCorpseRegistry.StatusTag, () =>
                    $"VANGUARD_CORPSE_LOOT_ASSIGNMENT_PLAN_ITEM owner={Safe(squad.Key)}; operator={Safe(selectedWinner.OperatorId)}; corpse={Safe(selectedWinner.CorpseLoot.CandidateCorpseId)}; item={Safe(entry.ItemId)}; template={Safe(entry.TemplateId)}; name={Safe(entry.Name)}; category={Safe(entry.Category)}; reason={Safe(entry.Reason)}; source={Safe(entry.SourcePath)}; destination={Safe(entry.Destination)}; operation={Safe(entry.PlacementOperation)}; possible={entry.PlacementPossible}; quantity={entry.Quantity}; cells={entry.CellCount}; weight={entry.EstimatedWeightKg:0.000}; score={entry.Score:0.0}; stop={Safe(entry.StopCondition)}; executionCandidate=true; itemClaimRequired=true; mutationAtObservation=false");
            }
        }
    }


    private static float LootSelectionScore(OperatorDecisionSnapshot snapshot)
    {
        float score = Math.Max(0f, snapshot.CorpseLoot.UtilityScore);
        if (snapshot.SquadCohesion.SectorDuplicate) score += 7f;
        if (snapshot.SquadCohesion.RearOverstacked) score += 5f;
        if (!snapshot.SquadCohesion.UsefulPosition) score += 3f;
        if (!snapshot.SquadCohesion.SectorTopologyValid) score += 1f;
        score -= Math.Min(12f, Math.Max(0f, snapshot.SquadCohesion.OperatorDistanceToOwner) * 0.12f);
        return score;
    }


    private static string OperationalTransitionSignature(VanguardCorpseLootCandidateEvaluation evaluation)
        => string.Join("|",
            evaluation.Included ? "included" : "excluded",
            evaluation.Gate,
            evaluation.Hostility.Source,
            evaluation.Hostility.Reason,
            evaluation.PathComplete ? "path_complete" : "path_incomplete",
            evaluation.Plan.DecisionSignature);

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif
