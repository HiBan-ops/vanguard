#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EftWeapon = global::EFT.InventoryLogic.Weapon;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;

// Responsibility: qualifies the runtime context around an already-observed better-loot-magazine candidate without executing inventory mutation.
// Flow: LOOT-Q1 audit candidate + squad assignment + exact runtime binding + fresh decision/safety evidence + live corpse item resolution -> bounded shadow preflight telemetry.
// Authority boundary: observation only. This service never submits an inventory transaction, moves/drops an item, transfers cartridges, changes weapon preference or writes target/aim/fire state.
// Invariant: preflightReadyShadow is evidence that a future replacement canary has a coherent context; it is never equivalent to mutationAllowed or runtime mutation qualification.
namespace Vanguard.Client.Runtime.Loot;

internal static class VanguardBetterMagazineReplacementPreflightShadowService
{
    public const string StatusTag = "VANGUARD_BETTER_MAGAZINE_REPLACEMENT_PREFLIGHT_SHADOW_STATUS";
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20d);
    private const int MaxDiagnosticFieldLength = 180;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, ShadowState> ByCandidateKey = new(StringComparer.OrdinalIgnoreCase);

    internal static void Observe(
        VanguardRaidOperatorRuntimeRecord record,
        VanguardUnifiedLootReadModelObservation observation,
        IReadOnlyList<EftWeapon> currentWeapons,
        DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority
            || record.BotOwner == null
            || record.BotOwner.IsDead
            || currentWeapons == null
            || currentWeapons.Count == 0)
        {
            return;
        }

        foreach (EftWeapon weapon in currentWeapons)
        {
            if (weapon == null || string.IsNullOrWhiteSpace(weapon.Id))
            {
                continue;
            }

            if (!VanguardMagazineLogisticsAuditService.TryGetLatest(record.BotProfileId, weapon.Id, now, out VanguardMagazineLogisticsAuditSnapshot audit)
                || !audit.BetterLootMagazineCandidate
                || string.IsNullOrWhiteSpace(audit.BestLootMagazineItemId)
                || string.Equals(audit.BestLootMagazineItemId, "none", StringComparison.OrdinalIgnoreCase)
                || audit.TargetKind != observation.TargetKind
                || !string.Equals(audit.TargetId, observation.TargetId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool exactAssignment = VanguardSquadLootAssignmentService.IsAssignedToBot(
                observation.OwnerProfileId,
                observation.TargetKind,
                observation.TargetId,
                audit.BestLootMagazineItemId,
                observation.BotProfileId,
                observation.ManifestRevision,
                now,
                out VanguardSquadLootItemAssignment assignment);
            bool assignmentIsMagazine = exactAssignment && string.Equals(assignment.Category, "magazine", StringComparison.OrdinalIgnoreCase);

            // Mirror the already-qualified corpse-loot executor's executable assignment ordering without taking mutation authority.
            // The transaction preflight consumes the same assignment authority, permission policy and MaximumCandidates window.
            // This makes Better-Mag orchestration observable as delegation into the existing executor instead of inventing a second writer.
            IReadOnlyList<VanguardSquadLootItemAssignment> orderedAssignments = VanguardSquadLootAssignmentService.GetAssignmentsForBot(
                observation.OwnerProfileId,
                observation.TargetKind,
                observation.TargetId,
                observation.BotProfileId,
                observation.ManifestRevision,
                now);
            VanguardOperatorLootPermissionSnapshot permissions = VanguardOperatorLootPermissionSnapshot.CaptureRuntime(record);
            var currentAssignments = new List<VanguardSquadLootItemAssignment>(orderedAssignments.Count);
            foreach (VanguardSquadLootItemAssignment candidateAssignment in orderedAssignments)
            {
                if (candidateAssignment.InterestRevision != observation.InterestRevision
                    || !string.Equals(candidateAssignment.NeedSignature, observation.NeedSignature, StringComparison.Ordinal)
                    || !VanguardCorpseLootTransactionPreflight.IsAssignmentExecutable(candidateAssignment, permissions))
                {
                    continue;
                }
                currentAssignments.Add(candidateAssignment);
            }

            int candidateAssignmentRank = 0;
            for (int index = 0; index < currentAssignments.Count; index++)
            {
                if (string.Equals(currentAssignments[index].ItemId, audit.BestLootMagazineItemId, StringComparison.OrdinalIgnoreCase))
                {
                    candidateAssignmentRank = index + 1;
                    break;
                }
            }
            VanguardSquadLootItemAssignment? firstAssignment = currentAssignments.Count > 0 ? currentAssignments[0] : null;
            int higherPriorityAssignmentCount = candidateAssignmentRank > 0 ? candidateAssignmentRank - 1 : currentAssignments.Count;
            bool candidateFirstForCurrentSession = candidateAssignmentRank == 1;
            bool candidateWithinExecutorWindow = candidateAssignmentRank > 0
                && candidateAssignmentRank <= VanguardCorpseLootTransactionPreflight.MaximumCandidates;
            bool deferredBehindHigherPriorityAssignment = candidateAssignmentRank > 1;

            bool exactRuntimeBinding = VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(record.BotProfileId, out VanguardRaidOperatorRuntimeRecord rebound)
                && ReferenceEquals(rebound.BotOwner, record.BotOwner)
                && rebound.RuntimeIncarnation == record.RuntimeIncarnation
                && string.Equals(rebound.OwnerProfileId, record.OwnerProfileId, StringComparison.OrdinalIgnoreCase);
            bool inventoryControllerObserved = record.BotOwner.GetPlayer?.InventoryController != null;

            bool snapshotFound = VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(record.BotProfileId, out OperatorDecisionSnapshot decision);
            string snapshotFreshnessReason = "decision_snapshot_missing";
            bool snapshotFresh = snapshotFound && VanguardOperatorDecisionSnapshotService.IsFresh(decision, now, out snapshotFreshnessReason);
            bool opportunisticLootWindow = snapshotFresh && decision.PrimaryExecution.IsOpportunisticLoot;
            string safetyGate = snapshotFresh
                ? VanguardCorpseLootApproachExecutor.CheckActiveSessionSafetyGate(decision, now, opportunisticLootWindow)
                : "snapshot_unavailable:" + snapshotFreshnessReason;
            bool safetyClear = snapshotFresh && string.Equals(safetyGate, "none", StringComparison.OrdinalIgnoreCase);

            bool liveLootItemResolvable = false;
            string liveSourceReason;
            if (observation.TargetKind == VanguardLootTargetKind.Corpse)
            {
                if (VanguardCorpseRegistry.TryGet(observation.TargetId, now, out VanguardCorpseRegistryEntry entry)
                    && entry.Corpse != null
                    && VanguardCorpseLootLiveItemResolver.TryResolve(entry.Corpse, audit.BestLootMagazineItemId, out _, out string sourcePath, out string sourceAddress))
                {
                    liveLootItemResolvable = true;
                    liveSourceReason = "corpse_live_item_exact:" + Safe(sourcePath) + ":" + Safe(sourceAddress);
                }
                else
                {
                    liveSourceReason = "corpse_live_item_unresolved";
                }
            }
            else
            {
                liveSourceReason = "world_container_live_context_not_passed_to_this_atomic_node";
            }

            bool preflightReadyShadow = exactRuntimeBinding
                && inventoryControllerObserved
                && snapshotFresh
                && opportunisticLootWindow
                && safetyClear
                && exactAssignment
                && assignmentIsMagazine
                && liveLootItemResolvable;

            string fingerprint = string.Join("|",
                audit.BestLootMagazineItemId,
                exactAssignment,
                assignmentIsMagazine,
                exactRuntimeBinding,
                inventoryControllerObserved,
                snapshotFresh,
                opportunisticLootWindow,
                safetyGate,
                liveLootItemResolvable,
                preflightReadyShadow,
                candidateAssignmentRank,
                currentAssignments.Count,
                candidateFirstForCurrentSession,
                candidateWithinExecutorWindow,
                firstAssignment?.ItemId ?? "none",
                firstAssignment?.Tier.ToString() ?? "none",
                observation.ManifestRevision,
                observation.InterestRevision);
            string key = CandidateKey(record.BotProfileId, observation.TargetKind, observation.TargetId, weapon.Id, audit.BestLootMagazineItemId);
            bool emit;
            string emission;
            lock (Sync)
            {
                if (!ByCandidateKey.TryGetValue(key, out ShadowState? state))
                {
                    ByCandidateKey[key] = new ShadowState(fingerprint, now + HeartbeatInterval);
                    emit = true;
                    emission = "INITIAL";
                }
                else if (!string.Equals(state.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    state.Fingerprint = fingerprint;
                    state.NextHeartbeatAtUtc = now + HeartbeatInterval;
                    emit = true;
                    emission = "MATERIAL_CHANGE";
                }
                else if (now >= state.NextHeartbeatAtUtc)
                {
                    state.NextHeartbeatAtUtc = now + HeartbeatInterval;
                    emit = true;
                    emission = "HEARTBEAT";
                }
                else
                {
                    emit = false;
                    emission = string.Empty;
                }
            }

            if (!emit)
            {
                continue;
            }

            VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
                $"BETTER_MAGAZINE_REPLACEMENT_PREFLIGHT_SHADOW bot={Bounded(record.BotProfileId)}; owner={Bounded(record.OwnerProfileId)}; emission={emission}; targetKind={TargetKindToken(observation.TargetKind)}; target={Bounded(observation.TargetId)}; weapon={Bounded(weapon.Id)}; candidateMagazine={Bounded(audit.BestLootMagazineItemId)}; betterLootCandidate=true; exactAssignmentToBot={Bool(exactAssignment)}; assignmentCategoryMagazine={Bool(assignmentIsMagazine)}; assignmentReason={Bounded(exactAssignment ? assignment.Reason : "none")}; assignmentRank={candidateAssignmentRank}; assignmentCount={currentAssignments.Count}; higherPriorityAssignments={higherPriorityAssignmentCount}; candidateFirstForCurrentSession={Bool(candidateFirstForCurrentSession)}; candidateWithinExecutorWindow={Bool(candidateWithinExecutorWindow)}; deferredBehindHigherPriorityAssignment={Bool(deferredBehindHigherPriorityAssignment)}; firstAssignmentItem={Bounded(firstAssignment?.ItemId)}; firstAssignmentCategory={Bounded(firstAssignment?.Category)}; firstAssignmentTier={Bounded(firstAssignment?.Tier.ToString())}; firstAssignmentReason={Bounded(firstAssignment?.Reason)}; delegatedExecutor=VanguardCorpseLootSessionExecutor; delegatedExecutorPathQualified=true; directWriterRequired=false; directWriterQualified=false; exactRuntimeBinding={Bool(exactRuntimeBinding)}; inventoryControllerObserved={Bool(inventoryControllerObserved)}; decisionSnapshotFresh={Bool(snapshotFresh)}; snapshotFreshness={Bounded(snapshotFreshnessReason)}; opportunisticLootWindow={Bool(opportunisticLootWindow)}; safetyGate={Bounded(safetyGate)}; liveLootItemResolvable={Bool(liveLootItemResolvable)}; liveSourceReason={Bounded(liveSourceReason)}; preflightReadyShadow={Bool(preflightReadyShadow)}; runtimeOwnerAuthorityQualified=false; replacementRuntimeQualified=false; topUpPromotion=false; discardPromotion=false; mutationAllowed=false; networkTransactionSubmitted=false; itemMoved=false; itemDropped=false; cartridgeTransferred=false; weaponPreferenceMutation=false; targetAimFireMutation=false");
        }
    }

    internal static void ResetForRaidLifecycle()
    {
        lock (Sync)
        {
            ByCandidateKey.Clear();
        }
    }

    private static string CandidateKey(string? botProfileId, VanguardLootTargetKind targetKind, string? targetId, string? weaponItemId, string? candidateMagazineItemId)
        => Safe(botProfileId) + "|" + TargetKindToken(targetKind) + "|" + Safe(targetId) + "|" + Safe(weaponItemId) + "|" + Safe(candidateMagazineItemId);

    private static string TargetKindToken(VanguardLootTargetKind targetKind)
        => targetKind == VanguardLootTargetKind.WorldContainer ? "world_container" : "corpse";

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private static string Bounded(string? value)
    {
        string safe = Safe(value);
        return safe.Length <= MaxDiagnosticFieldLength ? safe : safe[..MaxDiagnosticFieldLength];
    }

    private sealed class ShadowState
    {
        public ShadowState(string fingerprint, DateTimeOffset nextHeartbeatAtUtc)
        {
            Fingerprint = fingerprint;
            NextHeartbeatAtUtc = nextHeartbeatAtUtc;
        }

        public string Fingerprint { get; set; }
        public DateTimeOffset NextHeartbeatAtUtc { get; set; }
    }
}
#endif
