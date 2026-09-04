#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EftWeapon = global::EFT.InventoryLogic.Weapon;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Raid.Persistence;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Medical;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.TacticalAuthoring;

// Responsibility: Ranks corpse loot sources against the Operator current needs while enforcing combat, medical, safety, ownership and policy gates.
// Flow: Corpse contents and live Operator readiness are inspected read-only, candidate value/armament/medical utility is scored, blocked surgery is distinguished from currently actionable surgery, and eligible targets are returned to the planner/broker.
// Authority boundary: Evaluation never moves items or the bot; corpse registry, medical truth and downstream transaction/movement systems retain their own authority.
// Invariant: Unsafe/claimed/stale corpses are rejected, actionable medical emergencies keep precedence, and unrealizable surgery cannot starve a qualified urgent armament fallback.
namespace Vanguard.Client.Runtime.Loot;

internal static class VanguardCorpseLootReadOnlyEvaluator
{
    private const int MaximumDistanceCandidates = 8;
    private const int MaximumPathCandidates = 4;
    private const int MaximumPlanCandidates = 3;
    private const int MaximumOperatorReadModelCandidates = 2;
    private static readonly object CacheSync = new();
    private static readonly HashSet<string> FriendlyOperatorEligibilityLogKeys = new(StringComparer.OrdinalIgnoreCase);

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (CacheSync)
        {
            FriendlyOperatorEligibilityLogKeys.Clear();
        }
    }

    public static VanguardCorpseLootDecisionSnapshot Capture(
        VanguardRaidOperatorRuntimeRecord record,
        bool alive,
        Vector3 operatorPosition,
        VanguardThreatDecisionSnapshot threat,
        VanguardSainDecisionSnapshot sain,
        VanguardBrainDecisionSnapshot brain,
        VanguardMedicalDecisionSnapshot medical,
        VanguardMedicalInventoryReadResult medicalInventory,
        VanguardSquadCohesionSnapshot cohesion,
        VanguardLootDecisionSnapshot externalLoot,
        VanguardOrbitDecisionSnapshot orbit,
        DateTimeOffset now)
    {
        var stopwatch = Stopwatch.StartNew();
        var permissions = VanguardOperatorLootPermissionSnapshot.CaptureRuntime(record);
        var need = VanguardOperatorLootNeedReader.Capture(record.BotOwner);
        if (!alive || record.BotOwner == null)
        {
            return Blocked(permissions, need, "operator_unavailable", "operator_dead_or_botowner_missing", stopwatch, now);
        }

        if (!VanguardOperatorLootTargetPermissionPolicy.AllowsTarget(
                permissions, VanguardLootTargetKind.Corpse, out string targetPermissionReason))
        {
            return Blocked(permissions, need, "permission_disabled", targetPermissionReason, stopwatch, now);
        }

        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            return Blocked(permissions, need, "non_authority_observer", "authority_only_corpse_qualification_skips_heavy_work", stopwatch, now);
        }

        float opportunisticRadius = VanguardRuntimeSettingsAuthorityResolver.ResolvePlayerScoped(record.OwnerProfileId, "corpse_loot_radius_fallback").MovementOpportunisticLootMaxDistanceMeters;
        IReadOnlyList<VanguardCorpseRegistryEntry> registrySnapshot = VanguardCorpseRegistry.GetSnapshot(now);
        var nearby = registrySnapshot
            .Where(entry => entry.Corpse != null)
            .Select(entry => new { Entry = entry, Distance = HorizontalDistance(operatorPosition, entry.Corpse.transform.position) })
            .Where(candidate => candidate.Distance <= opportunisticRadius)
            .OrderBy(candidate => candidate.Distance)
            .Take(MaximumDistanceCandidates)
            .ToArray();

        if (nearby.Length == 0)
        {
            return Blocked(
                permissions,
                need,
                "no_nearby_corpse",
                "no_registered_corpse_inside_opportunistic_radius",
                stopwatch,
                now,
                new VanguardCorpseLootEvaluationCounts { RegisteredSnapshotCount = registrySnapshot.Count });
        }

        Candidate? best = null;
        int pathBudget = MaximumPathCandidates;
        int planBudget = MaximumPlanCandidates;
        int unifiedReadModelBudget = MaximumPlanCandidates;
        int operatorReadModelBudget = MaximumOperatorReadModelCandidates;
        int playerExcluded = 0;
        int friendlyExcluded = 0;
        int hostilityUnverified = 0;
        int outcomeMemoryExcluded = 0;
        int notUseful = 0;
        int noDestination = 0;
        int pathIncomplete = 0;
        int pathBudgetDeferred = 0;
        int planBudgetDeferred = 0;
        int included = 0;
        var evaluations = new List<VanguardCorpseLootCandidateEvaluation>(nearby.Length);

        foreach (var candidate in nearby)
        {
            VanguardCorpseRegistryEntry entry = candidate.Entry;
            if (!entry.VictimIsAi && !entry.VictimWasOperator)
            {
                playerExcluded++;
                evaluations.Add(BuildRejected(entry, candidate.Distance, "excluded_player_corpse_policy", new VanguardCorpseHostilityEvidence
                {
                    Source = "player_policy",
                    Reason = "player_corpses_excluded_by_policy"
                }));
                continue;
            }

            VanguardCorpseHostilityEvidence hostility = VanguardCorpseHostilityResolver.Resolve(record, entry, threat, now);

            // Friendly-Operator corpse evaluation may cross the former read-only boundary only for protected squad corpses
            // and only while strict host/headless persistence authority is armed for this raid/build.
            // Non-friendly Operator corpses remain outside this branch; ordinary hostile AI corpse semantics are unchanged.
            bool friendlyOperatorCorpse = hostility.FriendlyOperatorCorpse && entry.VictimWasOperator;
            bool operatorPersistenceArmed = entry.VictimWasOperator && VanguardRaidOperatorPersistenceService.IsArmedForOperatorCorpseTransactions;
            bool friendlyOperatorReadOnly = friendlyOperatorCorpse && !operatorPersistenceArmed;
            bool readModelRelationshipEligible = hostility.Verified || friendlyOperatorCorpse;
            VanguardUnifiedLootReadModelObservation? unifiedObservation = null;
            bool contextCanStart = true;
            string outcomeGate = "read_model_relationship_not_evaluated";
            bool readModelBudgetAvailable = entry.VictimWasOperator
                ? operatorReadModelBudget > 0
                : unifiedReadModelBudget > 0;
            if (readModelRelationshipEligible && readModelBudgetAvailable)
            {
                if (entry.VictimWasOperator) operatorReadModelBudget--;
                else unifiedReadModelBudget--;
                unifiedObservation = VanguardUnifiedOpportunisticLootReadModelService.Observe(
                    record,
                    entry,
                    need,
                    medical,
                    medicalInventory,
                    candidate.Distance,
                    now,
                    legacyOwnerCorpseTerminal: false,
                    friendlyOperatorReadOnly: friendlyOperatorReadOnly,
                    relationshipKind: hostility.RelationshipKind);
                contextCanStart = VanguardCorpseLootOutcomeMemory.CanStartContext(
                    record.OwnerProfileId,
                    record.BotProfileId,
                    entry.CorpseId,
                    now,
                    unifiedObservation.ManifestRevision,
                    unifiedObservation.InterestRevision,
                    unifiedObservation.NeedSignature,
                    out outcomeGate);
            }
            else if (readModelRelationshipEligible)
            {
                contextCanStart = VanguardCorpseLootOutcomeMemory.CanStart(
                    record.OwnerProfileId, record.BotProfileId, entry.CorpseId, now, out outcomeGate);
            }

            if (entry.VictimWasOperator)
            {
                if (!friendlyOperatorCorpse)
                {
                    evaluations.Add(BuildRejected(entry, candidate.Distance, "excluded_nonfriendly_operator_corpse_policy", hostility));
                    continue;
                }

                if (!operatorPersistenceArmed)
                {
                    evaluations.Add(BuildRejected(entry, candidate.Distance, "excluded_operator_corpse_persistence_gate", hostility));
                    continue;
                }

                string eligibilityLogKey = $"{record.BotProfileId}|{entry.CorpseId}|{entry.VictimProfileId}";
                bool firstEligibilityObservation;
                lock (CacheSync)
                {
                    firstEligibilityObservation = FriendlyOperatorEligibilityLogKeys.Add(eligibilityLogKey);
                }

                if (firstEligibilityObservation)
                {
                    VanguardClientDiagnosticsLog.Operational(VanguardRaidOperatorPersistenceService.StatusTag, () =>
                        $"VANGUARD_FRIENDLY_OPERATOR_CORPSE_EXECUTION_ELIGIBLE owner={Safe(record.OwnerProfileId)}; operator={Safe(record.OperatorId)}; bot={Safe(record.BotProfileId)}; corpse={Safe(entry.CorpseId)}; victim={Safe(entry.VictimProfileId)}; relationship={Safe(hostility.RelationshipKind)}; persistenceArmed=true; logMode=first_eligibility_per_looter_corpse; scoringAndAssignment=shared_opportunistic_loot; transaction=shared_native_pipeline");
                }
            }

            if (hostility.FriendlyExcluded)
            {
                friendlyExcluded++;
                evaluations.Add(BuildRejected(entry, candidate.Distance, "excluded_protected_friendly", hostility));
                continue;
            }

            if (!hostility.Verified)
            {
                hostilityUnverified++;
                evaluations.Add(BuildRejected(entry, candidate.Distance, "excluded_hostility_unverified", hostility));
                continue;
            }

            if (!contextCanStart)
            {
                outcomeMemoryExcluded++;
                evaluations.Add(BuildRejected(
                    entry,
                    candidate.Distance,
                    "excluded_outcome_memory:" + outcomeGate,
                    hostility));
                continue;
            }

            if (pathBudget <= 0)
            {
                pathBudgetDeferred++;
                evaluations.Add(new VanguardCorpseLootCandidateEvaluation
                {
                    CorpseId = entry.CorpseId,
                    VictimProfileId = entry.VictimProfileId,
                    DirectDistanceMeters = candidate.Distance,
                    PathDistanceMeters = candidate.Distance,
                    Included = false,
                    Gate = "excluded_navmesh_budget",
                    Hostility = hostility
                });
                continue;
            }

            pathBudget--;
            bool usedSharedAnchorFallback = false;
            bool pathComplete = TryCalculatePath(operatorPosition, entry.Corpse.transform.position, out float pathDistance);
            if (!pathComplete && cohesion.OwnerPosition.HasValue)
            {
                // The exact corpse Transform may be off-mesh even when the approach planner can resolve a nearby reachable interaction point.
                // reach a valid interaction anchor around it. Fall back to the same bounded planner used
                // by the executor before classifying the corpse as physically unreachable.
                pathComplete = VanguardCorpseLootApproachPlanner.TryBuild(
                    record.OwnerProfileId,
                    cohesion.OwnerPosition,
                    operatorPosition,
                    entry.Corpse.transform.position,
                    out VanguardCorpseLootApproachPlan reachabilityPlan);
                if (pathComplete)
                {
                    usedSharedAnchorFallback = true;
                    pathDistance = reachabilityPlan.PathDistance;
                }
            }
            if (!pathComplete)
            {
                pathIncomplete++;
                evaluations.Add(new VanguardCorpseLootCandidateEvaluation
                {
                    CorpseId = entry.CorpseId,
                    VictimProfileId = entry.VictimProfileId,
                    DirectDistanceMeters = candidate.Distance,
                    PathDistanceMeters = pathDistance,
                    PathComplete = false,
                    Included = false,
                    Gate = "excluded_path_incomplete_after_shared_anchor_fallback",
                    Hostility = hostility
                });
                continue;
            }

            if (planBudget <= 0)
            {
                planBudgetDeferred++;
                evaluations.Add(new VanguardCorpseLootCandidateEvaluation
                {
                    CorpseId = entry.CorpseId,
                    VictimProfileId = entry.VictimProfileId,
                    DirectDistanceMeters = candidate.Distance,
                    PathDistanceMeters = pathDistance,
                    PathComplete = true,
                    Included = false,
                    Gate = "excluded_plan_budget",
                    Hostility = hostility
                });
                continue;
            }

            planBudget--;
            if (unifiedObservation == null)
            {
                planBudgetDeferred++;
                evaluations.Add(new VanguardCorpseLootCandidateEvaluation
                {
                    CorpseId = entry.CorpseId,
                    VictimProfileId = entry.VictimProfileId,
                    DirectDistanceMeters = candidate.Distance,
                    PathDistanceMeters = pathDistance,
                    PathComplete = true,
                    Included = false,
                    Gate = "excluded_unified_read_model_budget",
                    Hostility = hostility
                });
                continue;
            }
            VanguardCorpseLootDryRunPlan plan = VanguardUtilityLootActivationPlanner.Build(entry.Corpse, record.BotOwner, unifiedObservation, permissions);
            if (plan.UsefulItemCount <= 0)
            {
                bool hasOperatorUtilityBeforeSquadAssignment = unifiedObservation.Utilities.Any(utility =>
                    VanguardUtilityLootActivationPolicy.IsExecutable(utility, permissions));
                if (!hasOperatorUtilityBeforeSquadAssignment)
                {
                    _ = VanguardCorpseLootOutcomeMemory.RecordExhaustedContext(
                        record.BotProfileId, entry.CorpseId, unifiedObservation.ManifestRevision, unifiedObservation.InterestRevision,
                        unifiedObservation.NeedSignature, now, "no_operator_executable_utility", out _);
                }
                notUseful++;
                evaluations.Add(new VanguardCorpseLootCandidateEvaluation
                {
                    CorpseId = entry.CorpseId,
                    VictimProfileId = entry.VictimProfileId,
                    DirectDistanceMeters = candidate.Distance,
                    PathDistanceMeters = pathDistance,
                    PathComplete = true,
                    Included = false,
                    Gate = "excluded_no_useful_mvp_item",
                    Hostility = hostility,
                    Plan = plan
                });
                continue;
            }

            if (plan.FeasibleItemCount <= 0)
            {
                noDestination++;
                evaluations.Add(new VanguardCorpseLootCandidateEvaluation
                {
                    CorpseId = entry.CorpseId,
                    VictimProfileId = entry.VictimProfileId,
                    DirectDistanceMeters = candidate.Distance,
                    PathDistanceMeters = pathDistance,
                    PathComplete = true,
                    Included = false,
                    Gate = "excluded_no_feasible_destination",
                    Hostility = hostility,
                    Plan = plan,
                    Score = plan.TotalScore * 0.1f
                });
                continue;
            }

            float compatibilityBonus = EquipmentCompatibilityBonus(plan, need, hostility.DeadOperatorCorpse);
            float score = Score(plan, need, candidate.Distance, opportunisticRadius, compatibilityBonus, pathComplete: true);
            var evaluated = new Candidate(entry, candidate.Distance, pathDistance, true, hostility, plan, unifiedObservation, compatibilityBonus, score);
            included++;
            evaluations.Add(new VanguardCorpseLootCandidateEvaluation
            {
                CorpseId = entry.CorpseId,
                VictimProfileId = entry.VictimProfileId,
                DirectDistanceMeters = candidate.Distance,
                PathDistanceMeters = pathDistance,
                PathComplete = true,
                Included = true,
                Gate = usedSharedAnchorFallback
                    ? "candidate_shared_anchor_fallback"
                    : "candidate_complete_path",
                Hostility = hostility,
                Plan = plan,
                CompatibilityBonus = compatibilityBonus,
                Score = score
            });

            if (best == null || evaluated.Score > best.Score)
            {
                best = evaluated;
            }
        }

        var counts = new VanguardCorpseLootEvaluationCounts
        {
            RegisteredSnapshotCount = registrySnapshot.Count,
            NearbyCount = nearby.Length,
            PlayerExcludedCount = playerExcluded,
            FriendlyExcludedCount = friendlyExcluded,
            HostilityUnverifiedCount = hostilityUnverified,
            OutcomeMemoryExcludedCount = outcomeMemoryExcluded,
            NotUsefulCount = notUseful,
            NoDestinationCount = noDestination,
            PathIncompleteCount = pathIncomplete,
            PathBudgetDeferredCount = pathBudgetDeferred,
            PlanBudgetDeferredCount = planBudgetDeferred,
            IncludedCount = included
        };

        if (best == null)
        {
            return Blocked(
                permissions,
                need,
                "no_qualified_feasible_corpse",
                "nearby_corpses_excluded_by_player_relationship_outcome_utility_destination_or_path_policy",
                stopwatch,
                now,
                counts,
                evaluations);
        }

        string gate = EvaluateGate(threat, sain, brain, medical, medicalInventory, cohesion, externalLoot, orbit, best.PathComplete, out bool eligible);
        stopwatch.Stop();
        VanguardCorpseLootInventorySummary inventory = VanguardCorpseLootDryRunPlanner.Summarize(best.Plan);
        return new VanguardCorpseLootDecisionSnapshot
        {
            Enabled = true,
            Observed = true,
            ReadOnly = false,
            ExecutionEnabled = VanguardCorpseLootApproachDoctrine.ApproachExecutionEnabled
                && VanguardCorpseLootApproachDoctrine.ClaimAuthorityEnabled
                && VanguardOperatorLootTargetPermissionPolicy.AllowsTarget(
                    permissions, VanguardLootTargetKind.Corpse, out _),
            Permissions = permissions,
            OperatorNeed = need,
            CandidateFound = true,
            EligibleIfActivated = eligible,
            CandidateCorpseId = best.Entry.CorpseId,
            VictimProfileId = best.Entry.VictimProfileId,
            VictimName = best.Entry.VictimName,
            VictimSide = best.Entry.VictimSide,
            CorpsePosition = best.Entry.Corpse.transform.position,
            DirectDistanceMeters = best.DirectDistance,
            PathDistanceMeters = best.PathDistance,
            PathComplete = best.PathComplete,
            RelationshipEligible = best.Hostility.Verified,
            HostileVerified = best.Hostility.HostileConfirmed,
            DeadOperatorCorpse = best.Hostility.DeadOperatorCorpse,
            RelationshipKind = best.Hostility.RelationshipKind,
            HostilitySource = best.Hostility.Source,
            HostilityReason = best.Hostility.Reason,
            FriendlyExcluded = false,
            EquipmentCompatibilityBonus = best.CompatibilityBonus,
            UtilityScore = best.Score,
            ManifestRevision = best.Observation.ManifestRevision,
            InterestRevision = best.Observation.InterestRevision,
            LootNeedSignature = best.Observation.NeedSignature,
            Gate = gate,
            Reason = best.Plan.HighestPriorityReason + ";utility_claim_activation",
            Inventory = inventory,
            Plan = best.Plan,
            Counts = counts,
            CandidateEvaluations = evaluations,
            EvaluatedAtUtc = now,
            EvaluationDurationMilliseconds = (float)stopwatch.Elapsed.TotalMilliseconds
        };
    }

    private static VanguardCorpseLootDecisionSnapshot Blocked(
        VanguardOperatorLootPermissionSnapshot permissions,
        VanguardOperatorLootNeedSnapshot need,
        string gate,
        string reason,
        Stopwatch stopwatch,
        DateTimeOffset evaluatedAtUtc,
        VanguardCorpseLootEvaluationCounts? counts = null,
        IReadOnlyList<VanguardCorpseLootCandidateEvaluation>? evaluations = null)
    {
        stopwatch.Stop();
        return new VanguardCorpseLootDecisionSnapshot
        {
            Enabled = true,
            Observed = true,
            ReadOnly = true,
            ExecutionEnabled = false,
            Permissions = permissions,
            OperatorNeed = need,
            CandidateFound = false,
            EligibleIfActivated = false,
            Gate = gate,
            Reason = reason,
            Counts = counts ?? new VanguardCorpseLootEvaluationCounts(),
            CandidateEvaluations = evaluations ?? Array.Empty<VanguardCorpseLootCandidateEvaluation>(),
            EvaluatedAtUtc = evaluatedAtUtc,
            EvaluationDurationMilliseconds = (float)stopwatch.Elapsed.TotalMilliseconds
        };
    }

    private static VanguardCorpseLootCandidateEvaluation BuildRejected(
        VanguardCorpseRegistryEntry entry,
        float distance,
        string gate,
        VanguardCorpseHostilityEvidence hostility)
        => new()
        {
            CorpseId = entry.CorpseId,
            VictimProfileId = entry.VictimProfileId,
            DirectDistanceMeters = distance,
            PathDistanceMeters = distance,
            Included = false,
            Gate = gate,
            Hostility = hostility
        };

    private static string EvaluateGate(
        VanguardThreatDecisionSnapshot threat,
        VanguardSainDecisionSnapshot sain,
        VanguardBrainDecisionSnapshot brain,
        VanguardMedicalDecisionSnapshot medical,
        VanguardMedicalInventoryReadResult medicalInventory,
        VanguardSquadCohesionSnapshot cohesion,
        VanguardLootDecisionSnapshot externalLoot,
        VanguardOrbitDecisionSnapshot orbit,
        bool pathComplete,
        out bool eligible)
    {
        eligible = false;
        if (!pathComplete) return "blocked_path_incomplete";
        if (threat.DirectThreat
            || threat.EnemyVisible == true
            || threat.EnemyCanShoot == true
            || threat.ShotMeRecently == true
            || threat.ShotAtMeRecently == true)
            return "blocked_direct_or_visible_threat";
        if (sain.IsInCombat == true
            || sain.HasEnemy == true
            || string.Equals(sain.Classification, "sain_combat", StringComparison.OrdinalIgnoreCase))
            return "blocked_sain_combat_or_enemy";
        if (medical.Safety.ImmediateCombatBlock || medical.Need.HasHeavyBleed)
            return "blocked_urgent_medical_or_combat";
        bool unrealizableSurgeryLootFallback = medical.Need.HasOperableDestroyedPart
            && !medical.Actionability.RequiredItemAvailable;
        if (medical.Need.HasOperableDestroyedPart && !unrealizableSurgeryLootFallback)
            return "blocked_due_surgery_debt";
        if (medical.Actionability.AnyMedicineUsing)
            return "blocked_medical_action_active";
        if (medical.Actionability.Reloading || medical.Actionability.GrenadeThrowing)
            return "blocked_hands_busy";
        if (!cohesion.OwnerKnown || !cohesion.OwnerReliableForActiveMovement)
            return "blocked_owner_anchor_unreliable";
        if (cohesion.OperatorDistanceToOwner > VanguardCorpseLootApproachDoctrine.MaximumStartOwnerDistanceMeters)
            return "blocked_owner_distance_hard_limit";
        if (externalLoot.BotLooting == true || externalLoot.LootTaskRunning == true)
            return "blocked_external_looting_active";
        if (orbit.Active && !string.Equals(orbit.Status, "none", StringComparison.OrdinalIgnoreCase))
            return "blocked_orbit_objective_active";
        if (!string.IsNullOrWhiteSpace(brain.Node)
            && brain.Node.IndexOf("combat", StringComparison.OrdinalIgnoreCase) >= 0)
            return "blocked_brain_combat_node";

        eligible = true;
        return unrealizableSurgeryLootFallback
            ? "eligible_unrealizable_surgery_loot_fallback"
            : "eligible_for_precheck";
    }

    private static float Score(
        VanguardCorpseLootDryRunPlan plan,
        VanguardOperatorLootNeedSnapshot need,
        float distance,
        float opportunisticRadius,
        float compatibilityBonus,
        bool pathComplete)
    {
        float score = plan.TotalScore;
        score += Math.Max(0f, opportunisticRadius - distance) * 1.5f;
        score += compatibilityBonus;
        if (need.NeedsAnyMedicalCapability && plan.PlannedMedicalCount > 0) score += 18f;
        if (need.NeedsCompatibleMagazine && plan.PlannedMagazineCount > 0) score += 16f;
        if (!pathComplete) score *= 0.35f;
        return score;
    }

    private static float EquipmentCompatibilityBonus(
        VanguardCorpseLootDryRunPlan plan,
        VanguardOperatorLootNeedSnapshot need,
        bool operatorCorpse)
    {
        int compatibleMagazineAmmo = plan.Entries
            .Where(entry => entry.PlacementPossible && entry.Category == "magazine")
            .Sum(entry => Math.Max(0, entry.Quantity));
        float bonus = plan.PlannedMagazineCount * 14f + Math.Min(60, compatibleMagazineAmmo) * 0.35f;
        if (need.NeedsCompatibleMagazine && plan.PlannedMagazineCount > 0) bonus += 18f;
        if (plan.PlannedLongWeaponCount > 0 && plan.PlannedMagazineCount > 0) bonus += 16f;
        if (operatorCorpse && bonus > 0f) bonus += 24f;
        else if (operatorCorpse) bonus += 6f;
        return Math.Min(90f, bonus);
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static bool TryCalculatePath(Vector3 start, Vector3 end, out float distance)
    {
        distance = HorizontalDistance(start, end);
        if (!NavMesh.SamplePosition(start, out NavMeshHit sampledStart, 2.5f, NavMesh.AllAreas))
        {
            return false;
        }

        if (!NavMesh.SamplePosition(end, out NavMeshHit sampledEnd, 3.5f, NavMesh.AllAreas))
        {
            return false;
        }

        var path = new NavMeshPath();
        bool calculated = NavMesh.CalculatePath(sampledStart.position, sampledEnd.position, NavMesh.AllAreas, path);
        Vector3[]? pathCorners = path.corners;
        if (!calculated
            || path.status != NavMeshPathStatus.PathComplete
            || pathCorners is null
            || pathCorners.Length < 2)
        {
            return false;
        }

        float total = 0f;
        for (int index = 1; index < pathCorners.Length; index++)
        {
            total += Vector3.Distance(pathCorners[index - 1], pathCorners[index]);
        }
        distance = total;
        return total <= VanguardCorpseLootApproachDoctrine.MaximumPathDistanceMeters;
    }

    private sealed record Candidate(
        VanguardCorpseRegistryEntry Entry,
        float DirectDistance,
        float PathDistance,
        bool PathComplete,
        VanguardCorpseHostilityEvidence Hostility,
        VanguardCorpseLootDryRunPlan Plan,
        VanguardUnifiedLootReadModelObservation Observation,
        float CompatibilityBonus,
        float Score);
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

}
#endif
