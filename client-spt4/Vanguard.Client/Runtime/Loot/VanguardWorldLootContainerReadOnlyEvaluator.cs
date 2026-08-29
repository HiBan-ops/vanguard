#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Medical;
using Vanguard.Client.Runtime.TacticalAuthoring;

// Responsibility: Provides World Loot Container Read Only Evaluator support for the loot runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Loot;

/// <summary>
/// The persistence path read-only bridge from the central EFT world-container snapshot into the existing opportunistic
/// loot utility evaluator and squad allocation authority. The persistence path additionally consumes its short-lived,
/// permission-gated assignment projection as a candidate for the separate physical executor.
///
/// This producer still owns no physical claim, path, movement, interaction, inventory preview,
/// transaction, outcome memory, or persistence authority. Persistent loot-target policy is resolved before per-Operator
/// planning so a persistent deny remains fail-closed and avoids unnecessary work.
/// </summary>
internal static class VanguardWorldLootContainerReadOnlyEvaluator
{
    private const int MaximumDistanceCandidates = 6;
    private const int MaximumScoredCandidates = 2;
    private static readonly object LogSync = new();
    private static readonly Dictionary<string, string> LastSummaryByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastSummaryAtByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RepeatSummaryInterval = TimeSpan.FromSeconds(12d);
    private static readonly TimeSpan ApproachCandidateLifetime = TimeSpan.FromSeconds(3d);
    private static readonly Dictionary<string, VanguardWorldLootContainerApproachCandidate> ApproachCandidateByBot = new(StringComparer.OrdinalIgnoreCase);

    public static void ResetForRaidLifecycle(string source)
    {
        lock (LogSync)
        {
            LastSummaryByBot.Clear();
            LastSummaryAtByBot.Clear();
            ApproachCandidateByBot.Clear();
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.ContainerScoringAndSquadAllocationIntegrationStatusTag,
            $"VANGUARD_CONTAINER_SCORING_RESET source={Safe(source)}; physicalClaim=false; movement=false; opening=false; transaction=false");
    }

    public static void ObserveAssignments(
        VanguardRaidOperatorRuntimeRecord record,
        bool alive,
        Vector3 operatorPosition,
        VanguardMedicalDecisionSnapshot medical,
        VanguardMedicalInventoryReadResult medicalInventory,
        DateTimeOffset now)
    {
        if (!alive || record.BotOwner == null || record.BotOwner.IsDead || !VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }

        lock (LogSync) { ApproachCandidateByBot.Remove(Normalize(record.BotProfileId)); }

        // Persistent loot-target admission is checked before per-Operator container planning. Missing/older profiles
        // are CorpsesOnly, and F12/session settings form only a second AND gate inside AllowsTarget.
        VanguardOperatorLootPermissionSnapshot permissions = VanguardOperatorLootPermissionSnapshot.CaptureRuntime(record);
        bool executionPermission = VanguardOperatorLootTargetPermissionPolicy.AllowsTarget(
            permissions,
            VanguardLootTargetKind.WorldContainer,
            out string executionPermissionReason);
        if (!executionPermission)
        {
            LogSummary(record, now, 0, 0, 0, false, executionPermissionReason, planningSkipped: true);
            return;
        }

        IReadOnlyList<VanguardWorldLootContainerSnapshot> world = VanguardWorldLootContainerSnapshotProvider.GetSnapshot(now);
        if (world.Count == 0)
        {
            LogSummary(record, now, 0, 0, 0, true, "no_world_container_snapshot");
            return;
        }

        var scopedSettings = VanguardRuntimeSettingsAuthorityResolver
            .ResolvePlayerScoped(record.OwnerProfileId, "world_container_scoring_radius");
        if (!scopedSettings.MovementOpportunisticLootBrokerEnabled || !scopedSettings.LootOperationalSessionEnabled)
        {
            LogSummary(record, now, world.Count, 0, 0, false, "runtime_loot_subsystem_disabled");
            return;
        }

        float radius = scopedSettings.MovementOpportunisticLootMaxDistanceMeters;

        var nearby = world
            .Select(container => new Candidate(container, HorizontalDistance(operatorPosition, container.Position)))
            .Where(candidate => candidate.DistanceMeters <= radius)
            .OrderBy(candidate => candidate.DistanceMeters)
            .ThenBy(candidate => candidate.Container.ContainerId, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumDistanceCandidates)
            .ToArray();

        if (nearby.Length == 0)
        {
            LogSummary(record, now, world.Count, 0, 0, executionPermission, executionPermissionReason);
            return;
        }

        VanguardOperatorLootNeedSnapshot need = VanguardOperatorLootNeedReader.Capture(record.BotOwner);
        int scored = 0;
        int useful = 0;
        foreach (Candidate candidate in nearby.Take(MaximumScoredCandidates))
        {
            VanguardUnifiedLootReadModelObservation observation = VanguardUnifiedOpportunisticLootReadModelService.Observe(
                record,
                candidate.Container,
                need,
                medical,
                medicalInventory,
                candidate.DistanceMeters,
                now);
            scored++;

            IReadOnlyList<VanguardSquadLootItemAssignment> assignments = VanguardSquadLootAssignmentService.GetAssignmentsForBot(
                record.OwnerProfileId, VanguardLootTargetKind.WorldContainer, candidate.Container.ContainerId, record.BotProfileId, observation.ManifestRevision, now);
            if (executionPermission && assignments.Count > 0)
            {
                VanguardSquadLootItemAssignment bestAssignment = assignments.OrderByDescending(value => value.ExecutionScore).First();
                var executableCandidate = new VanguardWorldLootContainerApproachCandidate(
                    record.OwnerProfileId, record.OperatorId, record.BotProfileId, candidate.Container.ContainerId,
                    candidate.Container.Position, observation.ManifestRevision, bestAssignment.ExecutionScore,
                    candidate.DistanceMeters, now, now + ApproachCandidateLifetime);
                lock (LogSync)
                {
                    string key = Normalize(record.BotProfileId);
                    if (!ApproachCandidateByBot.TryGetValue(key, out var current) || executableCandidate.ExecutionScore > current.ExecutionScore)
                        ApproachCandidateByBot[key] = executableCandidate;
                }
            }

            if (observation.Utilities.Any(value => value.Tier > VanguardLootUtilityTier.Low))
            {
                useful++;
            }
        }

        LogSummary(record, now, world.Count, nearby.Length, scored, executionPermission,
            executionPermissionReason + ";usefulScoredTargets=" + useful);
    }

    public static bool TryGetApproachCandidate(string? botProfileId, DateTimeOffset now, out VanguardWorldLootContainerApproachCandidate candidate)
    {
        string key = Normalize(botProfileId);
        lock (LogSync)
        {
            if (ApproachCandidateByBot.TryGetValue(key, out var found) && found.ExpiresAtUtc > now)
            {
                candidate = found;
                return true;
            }
            ApproachCandidateByBot.Remove(key);
        }
        candidate = null!;
        return false;
    }

    private static void LogSummary(
        VanguardRaidOperatorRuntimeRecord record,
        DateTimeOffset now,
        int worldCount,
        int nearbyCount,
        int scoredCount,
        bool executionPermission,
        string reason,
        bool planningSkipped = false)
    {
        string bot = Normalize(record.BotProfileId);
        string signature = string.Join("|", worldCount, nearbyCount, scoredCount, executionPermission, planningSkipped, Safe(reason));
        lock (LogSync)
        {
            if (LastSummaryByBot.TryGetValue(bot, out string previous)
                && string.Equals(previous, signature, StringComparison.Ordinal)
                && LastSummaryAtByBot.TryGetValue(bot, out DateTimeOffset last)
                && now - last < RepeatSummaryInterval)
            {
                return;
            }

            LastSummaryByBot[bot] = signature;
            LastSummaryAtByBot[bot] = now;
        }

        VanguardClientDiagnosticsLog.Operational(
            VanguardBuildVersion.ContainerScoringAndSquadAllocationIntegrationStatusTag,
            () => $"VANGUARD_CONTAINER_SCORING_PASS owner={Safe(record.OwnerProfileId)}; bot={Safe(record.BotProfileId)}; world={worldCount}; nearby={nearbyCount}; scored={scoredCount}; maxDistanceCandidates={MaximumDistanceCandidates}; maxScoredCandidates={MaximumScoredCandidates}; executionPermission={Bool(executionPermission)}; executionPermissionReason={Safe(reason)}; planningSkipped={Bool(planningSkipped)}; scoring=true; squadAllocation=true; shadowReadOnly=true; physicalTargetClaim=false; navmeshPath=false; movement=false; opening=false; interaction=false; inventoryPreview=false; transaction=false; outcomeMemory=false; persistence=false");
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private static string Bool(bool value) => value ? "true" : "false";

    private sealed record Candidate(VanguardWorldLootContainerSnapshot Container, float DistanceMeters);
}

internal sealed record VanguardWorldLootContainerApproachCandidate(
    string OwnerProfileId,
    string OperatorId,
    string BotProfileId,
    string ContainerId,
    Vector3 ContainerPosition,
    long ManifestRevision,
    float ExecutionScore,
    float DirectDistanceMeters,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc);
#endif
