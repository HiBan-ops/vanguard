#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Limits how much expensive cohesion/NavMesh planning Vanguard may perform on one frame so several Operators cannot create a pathfinding spike together.
// Flow: Operators enter a round-robin planning window, consume bounded candidate/path counters, and resume deferred scans from stable cursors on later ticks instead of restarting.
// Authority boundary: This class budgets planning work only; it does not choose the final movement contract or move an Operator.
// Invariant: Per-tick limits are never exceeded, deferred work remains resumable, and one Operator cannot permanently monopolize the planning budget.
namespace Vanguard.Client.Runtime.Movement;

/// <summary>
/// Global main-thread budget for expensive cohesion claim planning. Only one Operator is allowed
/// to build or refresh a claim per planning tick, and candidate path validation is resumed through
/// stable cursors instead of restarting a full bubble scan every frame.
/// </summary>
internal static class VanguardCohesionPlanningBudget
{
    public const int MaxPathCalculationsPerTick = 8;
    public const int MaxCandidateEvaluationsPerTick = 16;

    private static readonly Dictionary<string, CandidateCursorState> CandidateCursorByKey = new(StringComparer.OrdinalIgnoreCase);
    private static string selectedBotProfileId = string.Empty;
    private static int roundRobinIndex;
    private static int usedPathCalculations;
    private static int usedCandidateEvaluations;
    private static bool planningScope;
    private static bool deferredThisTick;
    private static bool exhaustedThisTick;
    private static int deferralSerial;

    public static int UsedPathCalculations => usedPathCalculations;
    public static int UsedCandidateEvaluations => usedCandidateEvaluations;
    public static bool DeferredThisTick => deferredThisTick;
    public static bool ExhaustedThisTick => exhaustedThisTick;
    public static string SelectedBotProfileId => selectedBotProfileId;
    public static int DeferralSerial => deferralSerial;

    public static void Reset(string reason)
    {
        CandidateCursorByKey.Clear();
        selectedBotProfileId = string.Empty;
        roundRobinIndex = 0;
        usedPathCalculations = 0;
        usedCandidateEvaluations = 0;
        planningScope = false;
        deferredThisTick = false;
        exhaustedThisTick = false;
        deferralSerial = 0;
    }

    public static void BeginTick(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        usedPathCalculations = 0;
        usedCandidateEvaluations = 0;
        deferredThisTick = false;
        exhaustedThisTick = false;
        deferralSerial = 0;
        planningScope = false;

        string[] live = snapshots == null
            ? Array.Empty<string>()
            : snapshots
                .Where(snapshot => snapshot != null
                    && snapshot.Alive
                    && !string.IsNullOrWhiteSpace(snapshot.BotProfileId)
                    && snapshot.SquadCohesion.OwnerKnown
                    && snapshot.SquadCohesion.OwnerReliableForActiveMovement
                    && snapshot.SquadCohesion.OwnerPosition.HasValue)
                .Select(snapshot => snapshot.BotProfileId.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        if (live.Length == 0)
        {
            CandidateCursorByKey.Clear();
            selectedBotProfileId = string.Empty;
            roundRobinIndex = 0;
            return;
        }

        var liveSet = new HashSet<string>(live, StringComparer.OrdinalIgnoreCase);
        foreach (string staleKey in CandidateCursorByKey.Keys
            .Where(key => !liveSet.Contains(BotProfileFromCursorKey(key)))
            .ToArray())
        {
            CandidateCursorByKey.Remove(staleKey);
        }

        if (roundRobinIndex < 0 || roundRobinIndex >= live.Length)
        {
            roundRobinIndex = 0;
        }
        selectedBotProfileId = live[roundRobinIndex];
        roundRobinIndex = (roundRobinIndex + 1) % live.Length;
    }

    public static void EnterPlanningScope() => planningScope = true;
    public static void ExitPlanningScope() => planningScope = false;

    public static bool ShouldPlanBot(string? botProfileId)
    {
        return !string.IsNullOrWhiteSpace(botProfileId)
            && !string.IsNullOrWhiteSpace(selectedBotProfileId)
            && string.Equals(botProfileId.Trim(), selectedBotProfileId, StringComparison.OrdinalIgnoreCase);
    }

    public static bool CanStartCandidate(int requiredPathCalculations)
    {
        if (!planningScope)
        {
            return true;
        }

        int required = Math.Max(1, requiredPathCalculations);
        if (usedCandidateEvaluations >= MaxCandidateEvaluationsPerTick
            || usedPathCalculations + required > MaxPathCalculationsPerTick)
        {
            exhaustedThisTick = true;
            SetDeferred();
            return false;
        }

        usedCandidateEvaluations++;
        return true;
    }

    public static bool TryConsumePathCalculation(out string reason)
    {
        reason = "none";
        if (!planningScope)
        {
            return true;
        }

        if (usedPathCalculations >= MaxPathCalculationsPerTick)
        {
            exhaustedThisTick = true;
            SetDeferred();
            reason = "cohesion_path_budget_exhausted:" + usedPathCalculations + "/" + MaxPathCalculationsPerTick;
            return false;
        }

        usedPathCalculations++;
        reason = "cohesion_path_budget_used:" + usedPathCalculations + "/" + MaxPathCalculationsPerTick;
        return true;
    }

    public static int GetCandidateStart(string botProfileId, string phase, string generation, int candidateCount)
    {
        if (candidateCount <= 0)
        {
            return 0;
        }

        string key = CursorKey(botProfileId, phase);
        if (!CandidateCursorByKey.TryGetValue(key, out var state)
            || !string.Equals(state.Generation, NormalizeGeneration(generation), StringComparison.Ordinal)
            || state.CandidateCount != candidateCount
            || state.Cursor < 0)
        {
            CandidateCursorByKey[key] = new CandidateCursorState(NormalizeGeneration(generation), candidateCount, 0, 0);
            return 0;
        }
        return state.Cursor % candidateCount;
    }

    /// <summary>
    /// Advances a stable cursor for one immutable candidate generation. Cumulative progress is
    /// tracked explicitly, so variable per-frame budgets cannot loop forever without ever proving
    /// that the whole generation was evaluated.
    /// </summary>
    public static bool AdvanceCandidateCursor(
        string botProfileId,
        string phase,
        string generation,
        int candidateCount,
        int startIndex,
        int evaluatedCount)
    {
        string key = CursorKey(botProfileId, phase);
        if (candidateCount <= 0)
        {
            CandidateCursorByKey.Remove(key);
            return true;
        }

        string normalizedGeneration = NormalizeGeneration(generation);
        if (!CandidateCursorByKey.TryGetValue(key, out var state)
            || !string.Equals(state.Generation, normalizedGeneration, StringComparison.Ordinal)
            || state.CandidateCount != candidateCount)
        {
            state = new CandidateCursorState(normalizedGeneration, candidateCount, Math.Max(0, startIndex) % candidateCount, 0);
        }

        int evaluated = Math.Max(0, Math.Min(candidateCount, evaluatedCount));
        int cumulative = Math.Min(candidateCount, state.EvaluatedCount + evaluated);
        int next = (Math.Max(0, startIndex) + evaluated) % candidateCount;
        bool completed = cumulative >= candidateCount;
        if (completed)
        {
            CandidateCursorByKey.Remove(key);
        }
        else
        {
            CandidateCursorByKey[key] = new CandidateCursorState(normalizedGeneration, candidateCount, next, cumulative);
            SetDeferred();
        }
        return completed;
    }

    public static void CompleteCandidateSequence(string botProfileId, string phase)
    {
        CandidateCursorByKey.Remove(CursorKey(botProfileId, phase));
    }

    public static void MarkDeferred() => SetDeferred();

    private static void SetDeferred()
    {
        deferredThisTick = true;
        deferralSerial++;
    }

    private static string NormalizeGeneration(string? generation)
    {
        return string.IsNullOrWhiteSpace(generation) ? "none" : generation.Trim();
    }

    private readonly struct CandidateCursorState
    {
        public CandidateCursorState(string generation, int candidateCount, int cursor, int evaluatedCount)
        {
            Generation = generation;
            CandidateCount = candidateCount;
            Cursor = cursor;
            EvaluatedCount = evaluatedCount;
        }

        public string Generation { get; }
        public int CandidateCount { get; }
        public int Cursor { get; }
        public int EvaluatedCount { get; }
    }

    private static string CursorKey(string? botProfileId, string? phase)
    {
        return (string.IsNullOrWhiteSpace(botProfileId) ? "none" : botProfileId.Trim().ToLowerInvariant())
            + "|"
            + (string.IsNullOrWhiteSpace(phase) ? "generic" : phase.Trim().ToLowerInvariant());
    }

    private static string BotProfileFromCursorKey(string key)
    {
        int separator = key.IndexOf('|');
        return separator <= 0 ? key : key.Substring(0, separator);
    }
}
#endif
