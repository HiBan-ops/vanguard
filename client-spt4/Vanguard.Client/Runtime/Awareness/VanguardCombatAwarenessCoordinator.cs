#if SPT_CLIENT
using Comfort.Common;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Alliance;
using Vanguard.Client.Runtime.Combat;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Builds the squad contact picture and chooses which known threat, if any, each Operator should actively engage.
// Flow: Observed contacts are merged into shared knowledge; each Operator then re-evaluates distance, line of sight, pathing, freshness and cohesion before one assignment is selected and sent to the SAIN bridge.
// Authority boundary: Vanguard owns threat knowledge and assignment policy, while EFT supplies world/perception facts and SAIN remains the combat executor once a valid target is handed over.
// Invariant: Knowledge alone must never become permanent pursuit authority: stale, unreachable or out-of-cohesion assignments are released unless fresh direct threat evidence justifies them.
namespace Vanguard.Client.Runtime.Awareness;

/// <summary>
/// Single Awareness / Scanner Assignment coordinator.
///
/// The contact picture is shared by the squad, while assignment is recalculated for every Operator
/// from its own position, LOS, NavMesh path, verticality and current combat state. The coordinator has
/// one mutation exit only: the selected valid assignment is committed through the SAIN bridge.
/// </summary>
internal static partial class VanguardCombatAwarenessBridge
{
    public const string UnifiedAssignmentStatusTag = "VANGUARD_UNIFIED_SCANNER_ASSIGNMENT_STATUS";

    private const float UnifiedWorldScanMaxMeters = 110.0f;
    private const float UnifiedOwnerShotMaxMeters = 105.0f;
    private const float UnifiedOwnerShotBaseAngleDegrees = 32.0f;
    private const float UnifiedOwnerShotCloseAngleDegrees = 52.0f;
    private const float UnifiedOwnerShotCloseDistanceMeters = 22.0f;
    private const float UnifiedOwnerShotVisualEvidenceMaxAgeSeconds = 1.75f;
    private const float UnifiedOwnerShotMuzzleLineAdvanceMeters = 0.15f;
    private const float UnifiedOwnerShotSuspicionMinimumScore = 20.0f;
    private const float UnifiedNavPathMaxMeters = 115.0f;
    private const float UnifiedCloseDirectMeters = 14.0f;
    private const float UnifiedCloseNavMeters = 24.0f;
    private const float UnifiedCloseVerticalMeters = 2.60f;
    private const float UnifiedStrongCqbMeters = 14.0f;
    private const float UnifiedCandidateMinimumScore = 72.0f;
    private const float UnifiedSwitchHysteresis = 18.0f;
    private const float UnifiedDistributionAlternativeWindow = 46.0f;
    private const int UnifiedCandidateEvaluationCap = 18;
    private const float UnifiedNavCachePositionToleranceMeters = 1.50f;

    private static readonly TimeSpan UnifiedAssignmentTtl = TimeSpan.FromSeconds(5.25d);
    private static readonly TimeSpan UnifiedSquadContactTtl = TimeSpan.FromSeconds(7.50d);
    private static readonly TimeSpan UnifiedOwnerShotSuspicionTtl = TimeSpan.FromSeconds(2.75d);
    private static readonly TimeSpan UnifiedApplyCooldown = TimeSpan.FromSeconds(0.85d);
    private static readonly TimeSpan UnifiedFailureCooldown = TimeSpan.FromSeconds(1.25d);
    private static readonly TimeSpan UnifiedTransitionLogInterval = TimeSpan.FromSeconds(1.50d);
    private static readonly TimeSpan UnifiedWorldRosterCacheDuration = TimeSpan.FromSeconds(0.45d);
    private static readonly TimeSpan UnifiedNavCacheDuration = TimeSpan.FromSeconds(1.35d);
    private static readonly Dictionary<string, UnifiedAssignmentState> UnifiedAssignmentByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, UnifiedNavCacheState> UnifiedNavCacheByBotAndTarget = new(StringComparer.OrdinalIgnoreCase);

    private static readonly FieldInfo? UnifiedAllAlivePlayersListField = typeof(GameWorld).GetField(
        "AllAlivePlayersList",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly FieldInfo? UnifiedRegisteredPlayersField = typeof(GameWorld).GetField(
        "RegisteredPlayers",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static Player[] unifiedWorldRoster = Array.Empty<Player>();
    private static DateTimeOffset unifiedWorldRosterExpiresAtUtc = DateTimeOffset.MinValue;

    private static void ResetUnifiedThreatAssignment(string reason)
    {
        lock (Sync)
        {
            UnifiedAssignmentByBotProfileId.Clear();
            UnifiedNavCacheByBotAndTarget.Clear();
            unifiedWorldRoster = Array.Empty<Player>();
            unifiedWorldRosterExpiresAtUtc = DateTimeOffset.MinValue;
        }

        VanguardClientDiagnosticsLog.Info(UnifiedAssignmentStatusTag,
            $"VANGUARD_UNIFIED_ASSIGNMENT_RESET reason={Safe(reason)}; assignments=cleared; navCache=cleared; worldRoster=cleared; architecture=shared_contact_picture_plus_individual_requalification_plus_single_sain_commit; tag={UnifiedAssignmentStatusTag}");
    }

    private static bool TryRunUnifiedThreatAssignment(OperatorDecisionSnapshot snapshot, BotOwner botOwner, DateTimeOffset now)
    {
        if (snapshot == null || !snapshot.Alive || botOwner == null || botOwner.IsDead)
        {
            return false;
        }

        List<UnifiedThreatCandidate> candidates = BuildUnifiedCandidates(snapshot, botOwner, now);
        if (candidates.Count == 0)
        {
            ExpireUnifiedAssignment(snapshot.BotProfileId, now);
            return false;
        }

        ApplySquadNoProgressQuarantine(snapshot, candidates, now);
        PublishQualifiedSquadContacts(snapshot, candidates, now);
        ApplyIndividualDistribution(snapshot, candidates, now);

        List<UnifiedThreatCandidate> viable = candidates
            .Where(candidate => candidate.CanCommitToSain && candidate.Score >= UnifiedCandidateMinimumScore)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.NavDistance)
            .ThenBy(candidate => candidate.DirectDistance)
            .ToList();
        if (viable.Count == 0)
        {
            UnifiedThreatCandidate observed = candidates
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.NavDistance)
                .First();
            LogThrottled(
                "unifiedObserve|" + snapshot.BotProfileId + "|" + observed.TargetProfileId,
                now,
                SummaryLogInterval,
                $"VANGUARD_UNIFIED_ASSIGNMENT_OBSERVE operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(observed.TargetProfileId)}; intent={Safe(observed.Intent)}; score={observed.Score:0.0}; direct={observed.DirectDistance:0.0}; nav={FormatUnifiedDistance(observed.NavDistance)}; vertical={observed.VerticalDelta:0.0}; visible={Bool(observed.VisibleToOperator)}; lineToOwner={Bool(observed.LineToOwner)}; ownerShot={Bool(observed.OwnerShotRelevant)}; ownerShotSuspicion={Bool(observed.OwnerShotSuspicionOnly)}; ownerShotEvidence={Safe(observed.OwnerShotEvidence.ToString())}; ownerShotOwnerLos={Bool(observed.OwnerShotOwnerLos)}; ownerShotAge={observed.OwnerShotAgeSeconds:0.00}; ownerShotAngle={observed.OwnerShotAngle:0.0}; shared={Bool(observed.SharedContact)}; incoming={Bool(observed.IncomingFire)}; closeReachable={Bool(observed.CloseReachable)}; reason={Safe(observed.Reason)}; mutation=false; tag={UnifiedAssignmentStatusTag}");
            return false;
        }

        UnifiedThreatCandidate best = viable[0];
        string currentGoal = ResolveCurrentSainGoalId(botOwner);
        UnifiedThreatCandidate? currentCandidate = viable.FirstOrDefault(candidate => SameTarget(candidate.TargetProfileId, currentGoal));
        bool bestImmediate = best.IsImmediate;
        if (currentCandidate != null
            && !SameTarget(best.TargetProfileId, currentGoal)
            && !bestImmediate
            && best.Score < currentCandidate.Score + UnifiedSwitchHysteresis)
        {
            best = currentCandidate;
        }

        if (SameTarget(currentGoal, best.TargetProfileId))
        {
            // The runtime transition convergence: an already installed SAIN goal is stable state, not a
            // new assignment event. Keep the read-only assignment observation, but do not renew a
            // verified handoff receipt or notify the scheduler again. Actual initial/replacement
            // commits below still publish exactly one verified transition and one scheduler event.
            RecordUnifiedAssignment(snapshot, best, now, applied: true, reason: "already_effective_goal");
            return true;
        }

        if (HasSeriousStationaryMedicalNeed(snapshot)
            && !bestImmediate
            && !best.CanShootOperator
            && !best.LineToOwner
            && !best.OwnerShotRelevant)
        {
            RecordUnifiedAssignment(snapshot, best, now, applied: false, reason: "medical_hard_procedure_non_immediate_contact");
            return true;
        }

        if (IsUnifiedApplyCooldownActive(snapshot.BotProfileId, best.TargetProfileId, now, out string cooldownReason))
        {
            LogThrottled(
                "unifiedCooldown|" + snapshot.BotProfileId + "|" + best.TargetProfileId,
                now,
                UnifiedTransitionLogInterval,
                $"VANGUARD_UNIFIED_ASSIGNMENT_DEFERRED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(best.TargetProfileId)}; intent={Safe(best.Intent)}; reason={Safe(cooldownReason)}; score={best.Score:0.0}; immediate={Bool(bestImmediate)}; mutation=false; tag={UnifiedAssignmentStatusTag}");
            return true;
        }

        // A current qualified assignment supersedes retry/quarantine state from an older target episode.
        // The unified coordinator owns retry cadence; no second Awareness driver is allowed to arbitrate it.
        ClearTargetApplyCircuit(snapshot.BotProfileId, best.TargetProfileId);
        ClearTargetQuarantine(snapshot.BotProfileId, best.TargetProfileId, "unified_valid_assignment");
        lock (Sync)
        {
            PendingTargetClearByBotProfileId.Remove(snapshot.BotProfileId);
        }

        string assignmentKind = "unified_" + best.Intent + (best.IsImmediate ? "_immediate" : string.Empty);
        bool applied = TryBootstrapAndApplyTarget(
            snapshot,
            botOwner,
            best.TargetProfileId,
            "unified_assignment:" + best.Reason,
            assignmentKind,
            now,
            out SainTargetApplyResult result,
            out string before,
            out string after,
            out string bootstrapReason);

        bool verified = applied && SameTarget(after, best.TargetProfileId);
        RecordUnifiedAssignment(snapshot, best, now, verified, verified ? "verified" : "apply_failed:" + bootstrapReason);
        if (verified)
        {
            ClearTargetApplyCircuit(snapshot.BotProfileId, best.TargetProfileId);
            RecordVerifiedSainGoalHandoff(snapshot.BotProfileId, best.TargetProfileId, "unified_assignment", now);
            VanguardMainIntentScheduler.NotifyCombatTargetAssignment(snapshot.BotProfileId, best.TargetProfileId, "unified_assignment:" + best.Intent, now);
            VanguardMainIntentScheduler.NotifyCombatTargetApplied(snapshot.BotProfileId, best.TargetProfileId, "unified_assignment:" + best.Intent, now, verified: true);
            VanguardClientDiagnosticsLog.Info(ScanAssignmentStatusTag,
                $"VANGUARD_SCAN_ASSIGNMENT_APPLIED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; from={Safe(before)}; to={Safe(best.TargetProfileId)}; after={Safe(after)}; assignment={Safe(best.Intent)}; score={best.Score:0.0}; baseScore={best.BaseScore:0.0}; direct={best.DirectDistance:0.0}; ownerDistance={best.OwnerDistance:0.0}; nav={FormatUnifiedDistance(best.NavDistance)}; vertical={best.VerticalDelta:0.0}; visible={Bool(best.VisibleToOperator)}; lineToOwner={Bool(best.LineToOwner)}; ownerShot={Bool(best.OwnerShotRelevant)}; ownerShotSuspicion={Bool(best.OwnerShotSuspicionOnly)}; ownerShotEvidence={Safe(best.OwnerShotEvidence.ToString())}; ownerShotOwnerLos={Bool(best.OwnerShotOwnerLos)}; ownerShotAge={best.OwnerShotAgeSeconds:0.00}; ownerShotAngle={best.OwnerShotAngle:0.0}; shared={Bool(best.SharedContact)}; incoming={Bool(best.IncomingFire)}; source={Safe(best.Source)}; result={Safe(result.Reason)}; verified=true; doctrine=vanguard_qualifies_squad_shares_operator_requalifies_sain_executes; tag={ScanAssignmentStatusTag}; unifiedTag={UnifiedAssignmentStatusTag}; bridgeTag={StatusTag}");
            return true;
        }

        VanguardClientDiagnosticsLog.Warning(UnifiedAssignmentStatusTag,
            $"VANGUARD_UNIFIED_ASSIGNMENT_FAILED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(best.TargetProfileId)}; before={Safe(before)}; after={Safe(after)}; intent={Safe(best.Intent)}; score={best.Score:0.0}; result={Safe(result.Reason)}; bootstrap={Safe(bootstrapReason)}; retry=bounded_by_unified_coordinator; tag={UnifiedAssignmentStatusTag}");
        return true;
    }

    private static List<UnifiedThreatCandidate> BuildUnifiedCandidates(OperatorDecisionSnapshot snapshot, BotOwner botOwner, DateTimeOffset now)
    {
        var seeds = new Dictionary<string, UnifiedThreatSeed>(StringComparer.OrdinalIgnoreCase);
        AddSnapshotSeeds(snapshot, seeds);
        AddInstalledSainGoalSeed(snapshot, seeds);
        AddIncomingFireSeed(snapshot, now, seeds);
        AddOwnerHitSeed(snapshot, botOwner, now, seeds);
        AddSquadContactSeeds(snapshot, now, seeds);

        Player? ownerPlayer = VanguardFikaCompat.FindRaidPlayerByProfileId(snapshot.OwnerProfileId);
        Vector3 ownerPosition = ownerPlayer?.Transform?.position
            ?? snapshot.SquadCohesion.OwnerPosition
            ?? snapshot.Position;
        Vector3 operatorPosition = botOwner.Position;
        bool hasOwnerShot = VanguardOwnerShotMemoryService.TryGetRecentShot(snapshot.OwnerProfileId, now, out VanguardOwnerShotSnapshot ownerShot);

        List<UnifiedWorldEntry> entries = ResolveUnifiedWorldEntries(
            snapshot,
            botOwner,
            operatorPosition,
            ownerPosition,
            seeds,
            hasOwnerShot,
            ownerShot,
            now);

        var result = new List<UnifiedThreatCandidate>(entries.Count);
        foreach (UnifiedWorldEntry entry in entries)
        {
            string targetId = Normalize(entry.Player.ProfileId);
            UnifiedThreatSeed seed = seeds.TryGetValue(targetId, out UnifiedThreatSeed existingSeed)
                ? existingSeed
                : new UnifiedThreatSeed();
            UnifiedThreatCandidate? candidate = BuildUnifiedCandidate(
                snapshot,
                botOwner,
                entry,
                ownerPosition,
                seed,
                now);
            if (candidate != null)
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    private static List<UnifiedWorldEntry> ResolveUnifiedWorldEntries(
        OperatorDecisionSnapshot snapshot,
        BotOwner botOwner,
        Vector3 operatorPosition,
        Vector3 ownerPosition,
        IReadOnlyDictionary<string, UnifiedThreatSeed> seeds,
        bool hasOwnerShot,
        VanguardOwnerShotSnapshot ownerShot,
        DateTimeOffset now)
    {
        IEnumerable<Player> roster = GetUnifiedWorldRoster(now);
        var byProfile = new Dictionary<string, UnifiedWorldEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (Player player in roster)
        {
            if (!IsUnifiedHostileCandidate(snapshot, botOwner, player))
            {
                continue;
            }

            string targetId = Normalize(player.ProfileId);
            float direct = Vector3.Distance(operatorPosition, player.Position);
            float ownerDistance = Vector3.Distance(ownerPosition, player.Position);
            bool seeded = seeds.TryGetValue(targetId, out UnifiedThreatSeed? ownerShotSeed);
            float ownerShotAngle = 999f;
            float ownerShotAgeSeconds = 999f;
            bool ownerShotOwnerLos = false;
            OwnerShotEvidenceLevel ownerShotEvidence = OwnerShotEvidenceLevel.None;
            // A projectile-sector match is only geometry. The runtime upgrades it to actionable evidence
            // only when the owner had fresh physical LOS or another hostile source already knew the target.
            bool ownerShotGeometric = hasOwnerShot
                && TryResolveOwnerShotEvidence(
                    ownerShot,
                    player.Position,
                    ownerDistance,
                    ownerShotSeed,
                    now,
                    out ownerShotEvidence,
                    out ownerShotAngle,
                    out ownerShotOwnerLos,
                    out ownerShotAgeSeconds);
            bool ownerShotRelevant = ownerShotEvidence == OwnerShotEvidenceLevel.OwnerVisual
                || ownerShotEvidence == OwnerShotEvidenceLevel.Corroborated;
            bool ownerShotSuspicion = ownerShotEvidence == OwnerShotEvidenceLevel.GeometricSuspicion;
            if (!seeded
                && !ownerShotGeometric
                && direct > UnifiedWorldScanMaxMeters
                && ownerDistance > UnifiedWorldScanMaxMeters)
            {
                continue;
            }

            byProfile[targetId] = new UnifiedWorldEntry(
                player,
                direct,
                ownerDistance,
                seeded,
                ownerShotRelevant,
                ownerShotSuspicion,
                ownerShotOwnerLos,
                ownerShotGeometric ? ownerShotAngle : 999f,
                ownerShotAgeSeconds,
                ownerShotEvidence);
        }

        // A seeded contact must never disappear merely because a large raid filled the nearest-world
        // candidate cap. Resolve it directly from GameWorld and place it before unseeded discoveries.
        GameWorld? world = Singleton<GameWorld>.Instance;
        if (world != null)
        {
            foreach (string targetId in seeds.Keys)
            {
                if (byProfile.ContainsKey(targetId))
                {
                    continue;
                }

                Player? player = world.GetAlivePlayerByProfileID(targetId);
                if (player == null || !IsUnifiedHostileCandidate(snapshot, botOwner, player))
                {
                    continue;
                }

                float direct = Vector3.Distance(operatorPosition, player.Position);
                float ownerDistance = Vector3.Distance(ownerPosition, player.Position);
                seeds.TryGetValue(targetId, out UnifiedThreatSeed? ownerShotSeed);
                float ownerShotAngle = 999f;
                float ownerShotAgeSeconds = 999f;
                bool ownerShotOwnerLos = false;
                OwnerShotEvidenceLevel ownerShotEvidence = OwnerShotEvidenceLevel.None;
                bool ownerShotGeometric = hasOwnerShot
                    && TryResolveOwnerShotEvidence(
                        ownerShot,
                        player.Position,
                        ownerDistance,
                        ownerShotSeed,
                        now,
                        out ownerShotEvidence,
                        out ownerShotAngle,
                        out ownerShotOwnerLos,
                        out ownerShotAgeSeconds);
                bool ownerShotRelevant = ownerShotEvidence == OwnerShotEvidenceLevel.OwnerVisual
                    || ownerShotEvidence == OwnerShotEvidenceLevel.Corroborated;
                byProfile[targetId] = new UnifiedWorldEntry(
                    player,
                    direct,
                    ownerDistance,
                    seeded: true,
                    ownerShotRelevant,
                    ownerShotEvidence == OwnerShotEvidenceLevel.GeometricSuspicion,
                    ownerShotOwnerLos,
                    ownerShotGeometric ? ownerShotAngle : 999f,
                    ownerShotAgeSeconds,
                    ownerShotEvidence);
            }
        }

        List<UnifiedWorldEntry> priority = byProfile.Values
            .Where(entry => entry.Seeded || entry.OwnerShotRelevant || entry.OwnerShotSuspicion)
            .OrderByDescending(entry => ResolveUnifiedSeedPriority(entry, seeds))
            .ThenBy(entry => Math.Min(entry.DirectDistance, entry.OwnerDistance))
            .Take(UnifiedCandidateEvaluationCap)
            .ToList();
        HashSet<string> priorityIds = priority
            .Select(entry => Normalize(entry.Player.ProfileId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int remaining = Math.Max(0, UnifiedCandidateEvaluationCap - priority.Count);
        IEnumerable<UnifiedWorldEntry> discovery = byProfile.Values
            .Where(entry => !priorityIds.Contains(Normalize(entry.Player.ProfileId)))
            .OrderBy(entry => Math.Min(entry.DirectDistance, entry.OwnerDistance))
            .Take(remaining);

        priority.AddRange(discovery);
        return priority;
    }

    private static int ResolveUnifiedSeedPriority(
        UnifiedWorldEntry entry,
        IReadOnlyDictionary<string, UnifiedThreatSeed> seeds)
    {
        string targetId = Normalize(entry.Player.ProfileId);
        if (!seeds.TryGetValue(targetId, out UnifiedThreatSeed seed))
        {
            if (entry.OwnerShotRelevant) return 80;
            if (entry.OwnerShotSuspicion) return 20;
            return 0;
        }

        if (seed.IncomingFire || seed.OwnerHitReceipt || seed.ThreatenedOwner) return 100;
        if (seed.CurrentDirect || seed.CurrentAssigned) return 95;
        if (entry.OwnerShotRelevant) return 90;
        if (seed.ScannerDirect) return 85;
        if (seed.SharedImmediate) return 80;
        if (seed.SharedContact) return 65;
        if (seed.CurrentThreat) return 55;
        if (seed.SharedSuspicion) return 20;
        if (seed.ScannerCandidate || seed.AwarenessCandidate) return 45;
        return 10;
    }

    private static void AddSnapshotSeeds(OperatorDecisionSnapshot snapshot, Dictionary<string, UnifiedThreatSeed> seeds)
    {
        string current = Normalize(snapshot.Threat.EnemyId);
        if (!string.Equals(current, "none", StringComparison.OrdinalIgnoreCase))
        {
            UnifiedThreatSeed seed = GetOrCreateSeed(seeds, current);
            seed.CurrentThreat = true;
            seed.CurrentDirect = HasCurrentDirectProof(snapshot) || HasCurrentImmediateProof(snapshot);
            seed.IncomingFire |= snapshot.Threat.ShotMeRecently == true || snapshot.Threat.ShotAtMeRecently == true;
            seed.CanShootOperator |= snapshot.Threat.EnemyCanShoot == true;
            seed.Source = AppendUnifiedSource(seed.Source, "current_threat");
        }

        string scan = Normalize(snapshot.ThreatScan.CandidateThreatId);
        if (!string.Equals(scan, "none", StringComparison.OrdinalIgnoreCase))
        {
            UnifiedThreatSeed seed = GetOrCreateSeed(seeds, scan);
            seed.ScannerCandidate = true;
            seed.ScannerDirect = snapshot.ThreatScan.CandidateVisible
                || snapshot.ThreatScan.CandidateLineOfSight
                || snapshot.ThreatScan.CandidateCanShoot
                || snapshot.ThreatScan.CandidateIncomingFireFresh;
            seed.IncomingFire |= snapshot.ThreatScan.CandidateIncomingFireFresh;
            seed.CanShootOperator |= snapshot.ThreatScan.CandidateCanShoot;
            seed.Source = AppendUnifiedSource(seed.Source, "sain_scanner");
        }

        string awareness = Normalize(snapshot.Awareness.CandidateId);
        if (!string.Equals(awareness, "none", StringComparison.OrdinalIgnoreCase))
        {
            UnifiedThreatSeed seed = GetOrCreateSeed(seeds, awareness);
            seed.AwarenessCandidate = true;
            seed.ScannerDirect |= snapshot.Awareness.CandidateVisible
                || snapshot.Awareness.CandidateLineOfSight
                || snapshot.Awareness.CandidateCanShoot
                || snapshot.Awareness.IncomingFireFresh;
            seed.IncomingFire |= snapshot.Awareness.IncomingFireFresh;
            seed.CanShootOperator |= snapshot.Awareness.CandidateCanShoot;
            seed.Source = AppendUnifiedSource(seed.Source, "awareness_snapshot");
        }
    }


    private static void AddInstalledSainGoalSeed(OperatorDecisionSnapshot snapshot, Dictionary<string, UnifiedThreatSeed> seeds)
    {
        if (!TryResolveIndependentInstalledSainGoalSeed(snapshot, out string targetId, out string reason))
        {
            return;
        }

        string target = Normalize(targetId);
        if (string.Equals(target, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        UnifiedThreatSeed seed = GetOrCreateSeed(seeds, target);
        seed.CurrentAssigned = true;
        seed.Source = AppendUnifiedSource(seed.Source, "verified_sain_goal:" + ReasonCode(reason));
    }

    /// <summary>
    /// Reads an installed SAIN GoalEnemy as an independent evidence source. A previous unified
    /// assignment and the short scheduler handoff are deliberately not sufficient here, otherwise
    /// the coordinator could refresh its own TTL forever after all combat evidence became stale.
    /// </summary>
    private static bool TryResolveIndependentInstalledSainGoalSeed(
        OperatorDecisionSnapshot snapshot,
        out string targetId,
        out string reason)
    {
        targetId = "none";
        reason = "none";
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out VanguardRaidOperatorRuntimeRecord record)
            || record.BotOwner == null
            || record.BotOwner.IsDead)
        {
            reason = "bot_owner_missing_or_dead";
            return false;
        }

        string currentGoal = ResolveCurrentSainGoalId(record.BotOwner);
        if (string.Equals(currentGoal, "none", StringComparison.OrdinalIgnoreCase))
        {
            reason = "goal_missing";
            return false;
        }

        if (IsProtectedFriendlyTarget(currentGoal, out string friendlyReason))
        {
            reason = "goal_protected_friendly:" + Safe(friendlyReason);
            return false;
        }

        if (!IsLiveCombatTarget(currentGoal, out string liveReason))
        {
            reason = "goal_invalid:" + Safe(liveReason);
            return false;
        }

        bool directLocalEvidence = HasDirectLocalSensorEvidenceForTarget(snapshot, currentGoal);
        if (!directLocalEvidence
            && IsDistantPursuitKnowledgeOnly(snapshot, currentGoal, out _, out string distantKnowledgeReason))
        {
            reason = "goal_distant_knowledge_only:" + Safe(distantKnowledgeReason);
            return false;
        }

        bool snapshotOwnsGoal = SameTarget(snapshot.Threat.EnemyId, currentGoal);
        bool sainActivelyOwnsGoal = snapshot.Sain.HasEnemy == true
            && (snapshot.Sain.IsInCombat == true
                || snapshot.Sain.Searching == true
                || ContainsText(snapshot.Sain.CurrentAction, "shoot")
                || ContainsText(snapshot.Sain.CurrentAction, "cover")
                || ContainsText(snapshot.Sain.CombatDecision, "attack"));
        bool freshSnapshotOwnership = snapshotOwnsGoal
            && sainActivelyOwnsGoal
            && !snapshot.Threat.StaleThreat;
        if (!directLocalEvidence && !freshSnapshotOwnership)
        {
            reason = "goal_without_independent_fresh_evidence";
            return false;
        }

        targetId = currentGoal;
        reason = directLocalEvidence
            ? "goal_with_direct_local_evidence"
            : "goal_with_fresh_sain_snapshot_ownership";
        return true;
    }

    private static void AddIncomingFireSeed(OperatorDecisionSnapshot snapshot, DateTimeOffset now, Dictionary<string, UnifiedThreatSeed> seeds)
    {
        foreach (VanguardNearMissSuppressionSnapshot receipt in VanguardNearMissSuppressionService.GetRecentContacts(snapshot.BotProfileId, now))
        {
            string target = Normalize(receipt.ShooterProfileId);
            if (string.Equals(target, "none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            UnifiedThreatSeed seed = GetOrCreateSeed(seeds, target);
            seed.IncomingFire |= receipt.ThreatenedOperator;
            seed.ThreatenedOwner |= receipt.ThreatenedOwner;
            string source = receipt.ThreatenedOperator && receipt.ThreatenedOwner
                ? "incoming_fire_operator_and_owner"
                : receipt.ThreatenedOperator
                    ? "incoming_fire_operator"
                    : "incoming_fire_owner";
            seed.Source = AppendUnifiedSource(seed.Source, source);
        }
    }

    private static void AddOwnerHitSeed(OperatorDecisionSnapshot snapshot, BotOwner botOwner, DateTimeOffset now, Dictionary<string, UnifiedThreatSeed> seeds)
    {
        foreach (VanguardOwnerImmediateThreatSnapshot receipt in VanguardOwnerImmediateThreatService.GetRecentForRecipient(snapshot, botOwner, now))
        {
            string targetId = Normalize(receipt.TargetProfileId);
            if (string.Equals(targetId, "none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            UnifiedThreatSeed seed = GetOrCreateSeed(seeds, targetId);
            seed.OwnerHitReceipt = true;
            seed.ThreatenedOwner = true;
            seed.Source = AppendUnifiedSource(seed.Source, "confirmed_owner_hit");
        }
    }

    private static void AddSquadContactSeeds(OperatorDecisionSnapshot snapshot, DateTimeOffset now, Dictionary<string, UnifiedThreatSeed> seeds)
    {
        List<SquadCombatContactState> contacts;
        lock (Sync)
        {
            if (!SquadContactsByOwnerProfileId.TryGetValue(snapshot.OwnerProfileId, out Dictionary<string, SquadCombatContactState> byTarget))
            {
                return;
            }

            contacts = byTarget.Values.Where(contact => contact.ExpiresAtUtc > now).ToList();
        }

        foreach (SquadCombatContactState contact in contacts)
        {
            string target = Normalize(contact.TargetId);
            if (string.Equals(target, "none", StringComparison.OrdinalIgnoreCase)
                || IsProtectedFriendlyTarget(target, out _)
                || !IsLiveWorldTarget(target, out _))
            {
                continue;
            }

            UnifiedThreatSeed seed = GetOrCreateSeed(seeds, target);
            bool suspicionOnly = IsSquadSuspicionKind(contact.Kind);
            seed.SharedSuspicion |= suspicionOnly;
            if (!suspicionOnly)
            {
                seed.SharedContact = true;
                seed.SharedImmediate |= contact.Kind.IndexOf("immediate", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            seed.Source = AppendUnifiedSource(
                seed.Source,
                (suspicionOnly ? "squad_suspicion:" : "squad:") + contact.SourceOperatorId);
        }
    }

    private static UnifiedThreatCandidate? BuildUnifiedCandidate(
        OperatorDecisionSnapshot snapshot,
        BotOwner botOwner,
        UnifiedWorldEntry entry,
        Vector3 ownerPosition,
        UnifiedThreatSeed seed,
        DateTimeOffset now)
    {
        try
        {
            Player target = entry.Player;
            Vector3 operatorPosition = botOwner.Position;
            Vector3 targetPosition = target.Position;
            float direct = entry.DirectDistance;
            float ownerDistance = entry.OwnerDistance;
            float vertical = Math.Abs(targetPosition.y - operatorPosition.y);
            bool visible = HasUnifiedLineOfSight(operatorPosition, targetPosition, 1.45f, 1.30f);
            bool lineToOwner = HasUnifiedLineOfSight(targetPosition, ownerPosition, 1.45f, 1.45f);
            bool hasPreNavIndependentSignal = seed.IncomingFire
                || seed.ThreatenedOwner
                || seed.OwnerHitReceipt
                || seed.CurrentDirect
                || seed.CurrentAssigned
                || seed.ScannerDirect
                || seed.SharedContact
                || visible
                || lineToOwner;
            bool potentialCloseReachable = direct <= UnifiedCloseDirectMeters
                && vertical <= UnifiedCloseVerticalMeters;
            bool suspicionBeforeNav = (entry.OwnerShotSuspicion || seed.SharedSuspicion)
                && !hasPreNavIndependentSignal;
            bool requiresNavEvaluation = !suspicionBeforeNav || potentialCloseReachable;
            float navDistance = requiresNavEvaluation
                ? ResolveUnifiedNavDistanceCached(
                    snapshot.BotProfileId,
                    target.ProfileId,
                    operatorPosition,
                    targetPosition,
                    now)
                : float.PositiveInfinity;
            bool closeReachable = potentialCloseReachable
                && navDistance <= UnifiedCloseNavMeters;

            bool currentGoal = SameTarget(ResolveCurrentSainGoalId(botOwner), target.ProfileId);
            bool hasIndependentActionableSignal = hasPreNavIndependentSignal
                || closeReachable;
            // SharedSuspicion is deliberately non-circular: it cannot promote itself. A later LOS,
            // incoming-fire receipt, direct scanner proof or valid shared contact may promote it normally.
            bool ownerShotSuspicionOnly = (entry.OwnerShotSuspicion || seed.SharedSuspicion)
                && !hasIndependentActionableSignal;
            bool hasQualifiedSignal = hasIndependentActionableSignal
                || entry.OwnerShotRelevant
                || entry.OwnerShotSuspicion
                || seed.SharedSuspicion;
            if (!hasQualifiedSignal)
            {
                return null;
            }

            float score = 0f;
            var reasons = new List<string>(16);
            if (seed.IncomingFire) { score += 250f; reasons.Add("incomingFireOperator"); }
            if (seed.OwnerHitReceipt) { score += 235f; reasons.Add("ownerHit"); }
            else if (seed.ThreatenedOwner) { score += 220f; reasons.Add("incomingFireOwner"); }
            if (seed.CurrentDirect) { score += 185f; reasons.Add("currentDirect"); }
            if (seed.CurrentAssigned && !seed.CurrentDirect) { score += 105f; reasons.Add("verifiedCurrentAssignment"); }
            if (entry.OwnerShotRelevant)
            {
                score += 170f;
                reasons.Add(entry.OwnerShotEvidence == OwnerShotEvidenceLevel.OwnerVisual
                    ? "ownerShotVisual"
                    : "ownerShotCorroborated");
            }
            else if (entry.OwnerShotSuspicion)
            {
                score += 28f;
                reasons.Add("ownerShotGeometricSuspicion");
            }
            else if (seed.SharedSuspicion)
            {
                score += 20f;
                reasons.Add("sharedOwnerShotSuspicion");
            }
            if (lineToOwner) { score += 155f; reasons.Add("lineToOwner"); }
            if (visible) { score += 145f; reasons.Add("localLos"); }
            if (seed.ScannerDirect) { score += 145f; reasons.Add("scannerDirect"); }
            if (seed.SharedImmediate) { score += 145f; reasons.Add("sharedImmediate"); }
            else if (seed.SharedContact) { score += 118f; reasons.Add("sharedContact"); }
            if (closeReachable) { score += 78f; reasons.Add("reachableClose"); }
            if (seed.CurrentThreat && !seed.CurrentDirect) { score += 44f; reasons.Add("currentKnown"); }
            if (seed.ScannerCandidate && !seed.ScannerDirect) { score += 28f; reasons.Add("scannerKnown"); }
            if (seed.AwarenessCandidate && !seed.ScannerDirect) { score += 28f; reasons.Add("awarenessKnown"); }
            if (currentGoal) { score += 32f; reasons.Add("currentGoalHysteresis"); }

            if ((lineToOwner || entry.OwnerShotRelevant || seed.IncomingFire || seed.ThreatenedOwner) && ownerDistance <= 100f)
            {
                score += 45f;
                reasons.Add("ownerThreatWindow");
            }

            if (direct <= 4f) { score += 55f; reasons.Add("pointBlank"); }
            else if (direct <= 10f) { score += 38f; reasons.Add("near"); }
            else if (direct <= 20f) { score += 18f; reasons.Add("mid"); }

            if (ownerDistance <= 18f && (lineToOwner || visible || seed.IncomingFire || seed.ThreatenedOwner))
            {
                score += 80f;
                reasons.Add("closeOwnerContact");
            }
            else if (ownerDistance <= 45f && lineToOwner)
            {
                score += 65f;
                reasons.Add("nearOwnerLine");
            }

            if (!ownerShotSuspicionOnly)
            {
                if (navDistance <= 8f) { score += 35f; reasons.Add("shortPath"); }
                else if (navDistance <= 18f) { score += 22f; reasons.Add("goodPath"); }
                else if (navDistance <= 35f) { score += 6f; reasons.Add("longPath"); }
                else if (float.IsPositiveInfinity(navDistance))
                {
                    score -= lineToOwner || entry.OwnerShotRelevant || seed.IncomingFire || seed.ThreatenedOwner || seed.SharedContact ? 25f : 60f;
                    reasons.Add("pathInvalid");
                }
                else
                {
                    score -= lineToOwner || entry.OwnerShotRelevant || seed.ThreatenedOwner || seed.SharedContact ? 8f : 28f;
                    reasons.Add("veryLongPath");
                }

                if (vertical > 3.5f && (float.IsPositiveInfinity(navDistance) || navDistance > direct * 3f))
                {
                    score -= 75f;
                    reasons.Add("unreachableVertical");
                }
            }
            else
            {
                // Suspicion is an observation, not a movement proposal. Nav/vertical pursuit penalties
                // must not erase it before the short squad TTL, while the commit gate stays closed.
                reasons.Add(requiresNavEvaluation
                    ? "observeOnlyNoPursuitPenalty"
                    : "observeOnlyNavSkipped");
            }

            if (lineToOwner && !visible && direct > 20f)
            {
                score += 35f;
                reasons.Add("distantShooterShape");
            }

            bool publishToSquad = seed.IncomingFire
                || seed.ThreatenedOwner
                || seed.OwnerHitReceipt
                || seed.CurrentDirect
                || seed.CurrentAssigned
                || seed.ScannerDirect
                || entry.OwnerShotRelevant
                || entry.OwnerShotSuspicion
                || visible
                || lineToOwner
                || closeReachable;
            string intent = ResolveUnifiedIntent(
                seed,
                entry.OwnerShotRelevant,
                ownerShotSuspicionOnly,
                visible,
                lineToOwner,
                closeReachable,
                direct);
            bool combatIntent = IsUnifiedCombatIntent(intent);
            bool immediate = seed.IncomingFire
                || closeReachable
                || (visible && direct <= 20f);

            float pursuitThreshold = VanguardMovementAuthorityDoctrine.StaleSearchDistanceMeters;
            bool qualifiedPursuitEvidence = seed.IncomingFire
                || seed.ThreatenedOwner
                || seed.OwnerHitReceipt
                || seed.CurrentDirect
                || seed.ScannerDirect
                || entry.OwnerShotRelevant
                || visible
                || seed.CanShootOperator;
            bool ownerAnchorReliable = snapshot.SquadCohesion.OwnerKnown
                && snapshot.SquadCohesion.OwnerReliableForActiveMovement
                && snapshot.SquadCohesion.OwnerPosition.HasValue;
            float cohesionDistance = snapshot.SquadCohesion.OperatorDistanceToOwner;
            float cohesionThreshold = VanguardMovementAuthorityDoctrine.CombatCohesionForcedCatchupMeters;
            bool formationDetached = ownerAnchorReliable && cohesionDistance >= cohesionThreshold;

            bool distantByTargetGeometry = direct >= pursuitThreshold
                && ownerDistance >= pursuitThreshold
                && (float.IsPositiveInfinity(navDistance) || navDistance >= pursuitThreshold)
                && !visible
                && !closeReachable
                && !seed.IncomingFire
                && !seed.ThreatenedOwner
                && !seed.OwnerHitReceipt
                && !seed.CurrentDirect
                && !seed.ScannerDirect
                && !entry.OwnerShotRelevant
                && !seed.CanShootOperator;
            bool formationDetachedKnowledgeOnly = formationDetached && !qualifiedPursuitEvidence;
            bool distantKnowledgeOnly = distantByTargetGeometry || formationDetachedKnowledgeOnly;
            if (distantKnowledgeOnly)
            {
                combatIntent = false;
                publishToSquad = false;
                immediate = false;
                intent = "observe_only_distant_knowledge";
                reasons.Add("distantKnowledgeOnly");
                reasons.Add(distantByTargetGeometry ? "targetGeometryDistant" : "formationDetachedNoDirectProof");
                reasons.Add("pursuitThreshold=" + pursuitThreshold.ToString("0.0", CultureInfo.InvariantCulture));
                reasons.Add("cohesionThreshold=" + cohesionThreshold.ToString("0.0", CultureInfo.InvariantCulture));
                LogThrottled(
                    "DistantPursuitSuppressed|" + snapshot.BotProfileId + "|" + Normalize(target.ProfileId),
                    now,
                    UnifiedTransitionLogInterval,
                    $"VANGUARD_DISTANT_PURSUIT_SUPPRESSED owner={Safe(snapshot.OwnerProfileId)}; operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(target.ProfileId)}; direct={direct:0.0}; ownerDistance={ownerDistance:0.0}; nav={FormatUnifiedDistance(navDistance)}; threshold={pursuitThreshold:0.0}; cohesion={cohesionDistance:0.0}; cohesionThreshold={cohesionThreshold:0.0}; formationDetached={Bool(formationDetached)}; targetGeometryDistant={Bool(distantByTargetGeometry)}; visible={Bool(visible)}; lineToOwner={Bool(lineToOwner)}; incoming={Bool(seed.IncomingFire)}; threatenedOwner={Bool(seed.ThreatenedOwner)}; ownerHit={Bool(seed.OwnerHitReceipt)}; scannerDirect={Bool(seed.ScannerDirect)}; currentDirect={Bool(seed.CurrentDirect)}; canShoot={Bool(seed.CanShootOperator)}; contactMemoryPreserved=true; sainCommit=false; movementAuthority=false; publishRefresh=false; tag={DistantPursuitKnowledgeOnlyStatusTag}; unifiedTag={UnifiedAssignmentStatusTag}");
            }

            return new UnifiedThreatCandidate
            {
                TargetProfileId = Normalize(target.ProfileId),
                DirectDistance = direct,
                OwnerDistance = ownerDistance,
                NavDistance = navDistance,
                VerticalDelta = vertical,
                VisibleToOperator = visible,
                LineToOwner = lineToOwner,
                CloseReachable = closeReachable,
                CurrentDirect = seed.CurrentDirect,
                ScannerDirect = seed.ScannerDirect,
                SharedContact = seed.SharedContact,
                SharedImmediate = seed.SharedImmediate,
                IncomingFire = seed.IncomingFire,
                OwnerHitReceipt = seed.OwnerHitReceipt,
                OwnerShotRelevant = entry.OwnerShotRelevant,
                OwnerShotSuspicion = entry.OwnerShotSuspicion,
                OwnerShotSuspicionOnly = ownerShotSuspicionOnly,
                OwnerShotOwnerLos = entry.OwnerShotOwnerLos,
                OwnerShotAngle = entry.OwnerShotAngle,
                OwnerShotAgeSeconds = entry.OwnerShotAgeSeconds,
                OwnerShotEvidence = entry.OwnerShotEvidence,
                ThreatenedOwner = seed.ThreatenedOwner,
                CanShootOperator = seed.CanShootOperator,
                BaseScore = score,
                Score = score,
                Intent = intent,
                Source = string.IsNullOrWhiteSpace(seed.Source)
                    ? ResolveOwnerShotSource(entry)
                    : AppendUnifiedSource(seed.Source, ResolveOwnerShotSource(entry)),
                Reason = string.Join("+", reasons),
                CanCommitToSain = combatIntent && !ownerShotSuspicionOnly && !distantKnowledgeOnly,
                PublishToSquad = publishToSquad && !distantKnowledgeOnly,
                // A received squad suspicion cannot refresh itself. Only a fresh local projection may
                // publish the typed suspicion again; independent proof promotes it as a valid contact.
                PublishAsSuspicion = ownerShotSuspicionOnly && entry.OwnerShotSuspicion,
                IsImmediate = immediate
            };
        }
        catch (Exception exception)
        {
            LogThrottled(
                "unifiedCandidateFailed|" + snapshot.BotProfileId + "|" + Normalize(entry.Player.ProfileId),
                now,
                SummaryLogInterval,
                $"VANGUARD_UNIFIED_CANDIDATE_FAILED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(entry.Player.ProfileId)}; reason={exception.GetType().Name}:{Safe(exception.Message)}; mutation=false; tag={UnifiedAssignmentStatusTag}");
            return null;
        }
    }

    private static void ApplySquadNoProgressQuarantine(
        OperatorDecisionSnapshot snapshot,
        List<UnifiedThreatCandidate> candidates,
        DateTimeOffset now)
    {
        foreach (UnifiedThreatCandidate candidate in candidates)
        {
            bool currentSnapshotEvidence = VanguardSquadTargetNoProgressQuarantine.HasFreshLocalRearmEvidence(
                snapshot,
                candidate.TargetProfileId,
                out string currentSnapshotEvidenceReason);
            bool freshCandidateEvidence = currentSnapshotEvidence
                || candidate.VisibleToOperator
                || candidate.IncomingFire
                || candidate.CanShootOperator
                || candidate.ScannerDirect
                || candidate.OwnerHitReceipt
                || candidate.ThreatenedOwner
                || (candidate.OwnerShotRelevant && candidate.OwnerShotEvidence >= OwnerShotEvidenceLevel.Corroborated);
            string evidenceReason = currentSnapshotEvidence
                ? currentSnapshotEvidenceReason
                : candidate.VisibleToOperator
                    ? "candidate_visible"
                    : candidate.IncomingFire
                        ? "candidate_incoming_fire"
                        : candidate.CanShootOperator
                            ? "candidate_can_shoot_operator"
                            : candidate.ScannerDirect
                                ? "candidate_scanner_direct"
                                : candidate.OwnerHitReceipt
                                    ? "candidate_owner_hit_receipt"
                                    : candidate.ThreatenedOwner
                                        ? "candidate_threatened_owner"
                                        : candidate.OwnerShotRelevant && candidate.OwnerShotEvidence >= OwnerShotEvidenceLevel.Corroborated
                                            ? "candidate_corroborated_owner_shot"
                                            : "none";

            VanguardSquadTargetNoProgressQuarantine.TryRearmByCandidateEvidence(
                snapshot.OwnerProfileId,
                candidate.TargetProfileId,
                now,
                freshCandidateEvidence,
                evidenceReason,
                out _);

            if (!VanguardSquadTargetNoProgressQuarantine.IsCombatAuthorityBlocked(
                    snapshot.OwnerProfileId,
                    candidate.TargetProfileId,
                    now,
                    out string quarantineReason))
            {
                continue;
            }

            candidate.CanCommitToSain = false;
            candidate.PublishToSquad = false;
            candidate.Intent = "observe_only";
            candidate.Reason += "+squadNoProgressKnowledgeOnly=" + Safe(quarantineReason);
            LogThrottled(
                "CandidateKnowledgeOnly|" + snapshot.OwnerProfileId + "|" + snapshot.BotProfileId + "|" + candidate.TargetProfileId,
                now,
                UnifiedTransitionLogInterval,
                $"VANGUARD_UNIFIED_CANDIDATE_KNOWLEDGE_ONLY owner={Safe(snapshot.OwnerProfileId)}; operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; target={Safe(candidate.TargetProfileId)}; direct={candidate.DirectDistance:0.0}; visible={Bool(candidate.VisibleToOperator)}; incoming={Bool(candidate.IncomingFire)}; canShoot={Bool(candidate.CanShootOperator)}; source={Safe(candidate.Source)}; reason={Safe(quarantineReason)}; commit=false; publishRefresh=false; contactMemoryPreserved=true; tag={VanguardSquadTargetNoProgressQuarantine.StatusTag}; unifiedTag={UnifiedAssignmentStatusTag}");
        }
    }

    private static void ApplyIndividualDistribution(OperatorDecisionSnapshot snapshot, List<UnifiedThreatCandidate> candidates, DateTimeOffset now)
    {
        List<UnifiedThreatCandidate> viable = candidates
            .Where(candidate => candidate.CanCommitToSain && candidate.BaseScore >= UnifiedCandidateMinimumScore)
            .ToList();
        if (viable.Count < 2)
        {
            return;
        }

        Dictionary<string, int> siblingAssignments = ResolveSiblingAssignmentCounts(snapshot.OwnerProfileId, snapshot.BotProfileId, now);
        foreach (UnifiedThreatCandidate candidate in viable)
        {
            if (candidate.IncomingFire || candidate.CurrentDirect || candidate.VisibleToOperator)
            {
                continue;
            }

            int assigned = siblingAssignments.TryGetValue(candidate.TargetProfileId, out int count) ? count : 0;
            if (assigned <= 0)
            {
                continue;
            }

            bool credibleAlternative = viable.Any(other =>
                !SameTarget(other.TargetProfileId, candidate.TargetProfileId)
                && other.BaseScore >= candidate.BaseScore - UnifiedDistributionAlternativeWindow
                && (!siblingAssignments.TryGetValue(other.TargetProfileId, out int otherCount) || otherCount < assigned));
            if (!credibleAlternative)
            {
                continue;
            }

            float penalty = Math.Min(42f, assigned * (candidate.ThreatenedOwner ? 10f : 18f));
            candidate.Score -= penalty;
            candidate.Reason += "+individualDistributionPenalty=" + penalty.ToString("0", CultureInfo.InvariantCulture);
        }
    }

    private static string ResolveUnifiedIntent(
        UnifiedThreatSeed seed,
        bool ownerShotRelevant,
        bool ownerShotSuspicionOnly,
        bool visible,
        bool lineToOwner,
        bool closeReachable,
        float direct)
    {
        if (seed.IncomingFire)
        {
            return "return_fire_direct";
        }

        if (seed.OwnerHitReceipt || seed.ThreatenedOwner)
        {
            return visible || closeReachable
                ? (direct <= UnifiedStrongCqbMeters ? "return_fire_direct" : "confirmed_combat")
                : "shared_combat_target";
        }

        if (seed.CurrentDirect || seed.CurrentAssigned || visible || seed.ScannerDirect)
        {
            return direct <= UnifiedStrongCqbMeters ? "return_fire_direct" : "confirmed_combat";
        }

        if (ownerShotRelevant || lineToOwner || seed.SharedContact)
        {
            return "shared_combat_target";
        }

        if (ownerShotSuspicionOnly || seed.SharedSuspicion)
        {
            return "observe_only";
        }

        if (closeReachable)
        {
            return "return_fire_direct";
        }

        return "search_last_known";
    }

    private static bool IsUnifiedCombatIntent(string intent)
        => string.Equals(intent, "return_fire_direct", StringComparison.OrdinalIgnoreCase)
            || string.Equals(intent, "confirmed_combat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(intent, "shared_combat_target", StringComparison.OrdinalIgnoreCase);

    private static void PublishQualifiedSquadContacts(
        OperatorDecisionSnapshot snapshot,
        IEnumerable<UnifiedThreatCandidate> candidates,
        DateTimeOffset now)
    {
        foreach (UnifiedThreatCandidate candidate in candidates)
        {
            float publishThreshold = candidate.PublishAsSuspicion
                ? UnifiedOwnerShotSuspicionMinimumScore
                : UnifiedCandidateMinimumScore;
            if (!candidate.PublishToSquad
                || candidate.BaseScore < publishThreshold
                || string.Equals(candidate.TargetProfileId, "none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            PublishUnifiedSquadContact(snapshot, candidate, now);
        }
    }

    private static void PublishUnifiedSquadContact(OperatorDecisionSnapshot snapshot, UnifiedThreatCandidate candidate, DateTimeOffset now)
    {
        DateTimeOffset until = now + (candidate.PublishAsSuspicion
            ? UnifiedOwnerShotSuspicionTtl
            : UnifiedSquadContactTtl);
        string kind = candidate.PublishAsSuspicion
            ? "unified_owner_shot_suspicion"
            : candidate.IsImmediate
                ? "unified_immediate_contact"
                : "unified_valid_contact";
        bool changed;
        lock (Sync)
        {
            if (!SquadContactsByOwnerProfileId.TryGetValue(snapshot.OwnerProfileId, out Dictionary<string, SquadCombatContactState> contacts))
            {
                contacts = new Dictionary<string, SquadCombatContactState>(StringComparer.OrdinalIgnoreCase);
                SquadContactsByOwnerProfileId[snapshot.OwnerProfileId] = contacts;
            }

            bool continuing = contacts.TryGetValue(candidate.TargetProfileId, out SquadCombatContactState previous)
                && previous.ExpiresAtUtc > now;
            bool kindChanged = continuing
                && !string.Equals(previous.Kind, kind, StringComparison.OrdinalIgnoreCase);
            DateTimeOffset episodeStarted = continuing ? previous.EpisodeStartedAtUtc : now;
            contacts[candidate.TargetProfileId] = new SquadCombatContactState(
                snapshot.OwnerProfileId,
                snapshot.OperatorId,
                snapshot.BotProfileId,
                candidate.TargetProfileId,
                kind,
                "unified:" + candidate.Intent + ":" + candidate.Reason,
                candidate.DirectDistance,
                now,
                episodeStarted,
                until);
            changed = !continuing || kindChanged;
        }

        if (changed)
        {
            VanguardClientDiagnosticsLog.Info(SquadTravelCombatAuthorityStatusTag,
                $"VANGUARD_SQUAD_CONTACT_QUALIFIED owner={Safe(snapshot.OwnerProfileId)}; sourceOperator={Safe(snapshot.OperatorId)}; sourceBot={Safe(snapshot.BotProfileId)}; target={Safe(candidate.TargetProfileId)}; intent={Safe(candidate.Intent)}; score={candidate.BaseScore:0.0}; direct={candidate.DirectDistance:0.0}; ownerDistance={candidate.OwnerDistance:0.0}; nav={FormatUnifiedDistance(candidate.NavDistance)}; vertical={candidate.VerticalDelta:0.0}; visible={Bool(candidate.VisibleToOperator)}; lineToOwner={Bool(candidate.LineToOwner)}; ownerShot={Bool(candidate.OwnerShotRelevant)}; ownerShotSuspicion={Bool(candidate.OwnerShotSuspicionOnly)}; ownerShotEvidence={Safe(candidate.OwnerShotEvidence.ToString())}; ownerShotOwnerLos={Bool(candidate.OwnerShotOwnerLos)}; ownerShotAge={candidate.OwnerShotAgeSeconds:0.00}; ownerShotAngle={candidate.OwnerShotAngle:0.0}; incoming={Bool(candidate.IncomingFire)}; source={Safe(candidate.Source)}; doctrine=all_valid_contacts_shared_then_each_operator_assigns_individually; tag={SquadTravelCombatAuthorityStatusTag}; unifiedTag={UnifiedAssignmentStatusTag}");
        }
    }

    private static Dictionary<string, int> ResolveSiblingAssignmentCounts(string ownerProfileId, string currentBotProfileId, DateTimeOffset now)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (VanguardRaidOperatorRuntimeRecord record in VanguardRaidOperatorRuntimeRegistry.GetOperatorsForOwner(ownerProfileId))
        {
            if (record.BotOwner == null
                || record.BotOwner.IsDead
                || SameTarget(record.BotProfileId, currentBotProfileId))
            {
                continue;
            }

            string target = "none";
            lock (Sync)
            {
                if (UnifiedAssignmentByBotProfileId.TryGetValue(record.BotProfileId, out UnifiedAssignmentState state)
                    && state.Applied
                    && state.ExpiresAtUtc > now)
                {
                    target = state.TargetProfileId;
                }
            }

            if (string.Equals(target, "none", StringComparison.OrdinalIgnoreCase)
                && VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(record.BotProfileId, out OperatorDecisionSnapshot siblingSnapshot)
                && TryResolveLocallyAppliedSainTarget(siblingSnapshot, "none", out string installedTarget, out _))
            {
                target = installedTarget;
            }

            if (!string.Equals(target, "none", StringComparison.OrdinalIgnoreCase))
            {
                counts[target] = counts.TryGetValue(target, out int count) ? count + 1 : 1;
            }
        }

        return counts;
    }

    private static bool HasFreshUnifiedAssignmentForTarget(string? botProfileId, string? targetId, DateTimeOffset now, out string reason)
    {
        reason = "none";
        string bot = Normalize(botProfileId);
        string target = Normalize(targetId);
        lock (Sync)
        {
            if (!UnifiedAssignmentByBotProfileId.TryGetValue(bot, out UnifiedAssignmentState state))
            {
                reason = "unified_assignment_missing";
                return false;
            }

            if (state.ExpiresAtUtc <= now)
            {
                UnifiedAssignmentByBotProfileId.Remove(bot);
                reason = "unified_assignment_expired";
                return false;
            }

            if (!SameTarget(state.TargetProfileId, target))
            {
                reason = "unified_assignment_target_mismatch:" + Safe(state.TargetProfileId);
                return false;
            }

            if (!state.Applied)
            {
                reason = "unified_assignment_not_committed:" + Safe(state.Intent);
                return false;
            }

            reason = "unified_assignment_committed:" + Safe(state.Intent);
            return true;
        }
    }

    private static bool HasFreshDistantAuthorityAssignmentForTarget(
        string? botProfileId,
        string? targetId,
        DateTimeOffset now,
        out string reason)
    {
        reason = "none";
        string bot = Normalize(botProfileId);
        string target = Normalize(targetId);
        lock (Sync)
        {
            if (!UnifiedAssignmentByBotProfileId.TryGetValue(bot, out UnifiedAssignmentState state))
            {
                reason = "distant_authority_assignment_missing";
                return false;
            }

            if (state.ExpiresAtUtc <= now)
            {
                UnifiedAssignmentByBotProfileId.Remove(bot);
                reason = "distant_authority_assignment_expired";
                return false;
            }

            if (!SameTarget(state.TargetProfileId, target))
            {
                reason = "distant_authority_assignment_target_mismatch:" + Safe(state.TargetProfileId);
                return false;
            }

            if (!state.Applied || !state.DistantAuthoritySupported)
            {
                reason = "distant_authority_assignment_not_supported:" + Safe(state.Intent);
                return false;
            }

            reason = "distant_authority_assignment_supported:" + Safe(state.Intent);
            return true;
        }
    }

    private static void RecordUnifiedAssignment(OperatorDecisionSnapshot snapshot, UnifiedThreatCandidate candidate, DateTimeOffset now, bool applied, string reason)
    {
        UnifiedAssignmentState? previous;
        lock (Sync)
        {
            UnifiedAssignmentByBotProfileId.TryGetValue(snapshot.BotProfileId, out previous);
            UnifiedAssignmentByBotProfileId[snapshot.BotProfileId] = new UnifiedAssignmentState
            {
                TargetProfileId = candidate.TargetProfileId,
                Intent = candidate.Intent,
                NextAttemptAtUtc = now + (applied ? UnifiedApplyCooldown : UnifiedFailureCooldown),
                ExpiresAtUtc = now + UnifiedAssignmentTtl,
                Applied = applied,
                DistantAuthoritySupported = candidate.IncomingFire
                    || candidate.ThreatenedOwner
                    || candidate.OwnerHitReceipt
                    || candidate.CurrentDirect
                    || candidate.ScannerDirect
                    || candidate.OwnerShotRelevant
                    || candidate.VisibleToOperator
                    || candidate.CanShootOperator,
            };
        }

        bool changed = previous == null
            || !SameTarget(previous.TargetProfileId, candidate.TargetProfileId)
            || previous.Applied != applied;
        if (changed)
        {
            VanguardClientDiagnosticsLog.Operational(UnifiedAssignmentStatusTag, () =>
                $"VANGUARD_OPERATOR_ASSIGNMENT operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; owner={Safe(snapshot.OwnerProfileId)}; target={Safe(candidate.TargetProfileId)}; intent={Safe(candidate.Intent)}; score={candidate.Score:0.0}; baseScore={candidate.BaseScore:0.0}; applied={Bool(applied)}; source={Safe(candidate.Source)}; direct={candidate.DirectDistance:0.0}; ownerDistance={candidate.OwnerDistance:0.0}; nav={FormatUnifiedDistance(candidate.NavDistance)}; vertical={candidate.VerticalDelta:0.0}; visible={Bool(candidate.VisibleToOperator)}; ownerShot={Bool(candidate.OwnerShotRelevant)}; ownerShotSuspicion={Bool(candidate.OwnerShotSuspicionOnly)}; ownerShotEvidence={Safe(candidate.OwnerShotEvidence.ToString())}; ownerShotOwnerLos={Bool(candidate.OwnerShotOwnerLos)}; ownerShotAge={candidate.OwnerShotAgeSeconds:0.00}; ownerShotAngle={candidate.OwnerShotAngle:0.0}; reason={Safe(reason)}; doctrine=assignment_is_individual_contact_picture_is_shared; tag={UnifiedAssignmentStatusTag}");
        }
    }

    private static bool IsUnifiedApplyCooldownActive(string botProfileId, string targetId, DateTimeOffset now, out string reason)
    {
        reason = "none";
        lock (Sync)
        {
            if (!UnifiedAssignmentByBotProfileId.TryGetValue(botProfileId, out UnifiedAssignmentState state)
                || state.ExpiresAtUtc <= now)
            {
                UnifiedAssignmentByBotProfileId.Remove(botProfileId);
                return false;
            }

            // Retry cadence is target-specific. A failed or medically deferred assignment for one
            // target must never delay a newly selected valid target, even when the new contact is
            // medium-range rather than CQB. Hysteresis already controls ordinary target switching.
            if (!SameTarget(state.TargetProfileId, targetId) || state.NextAttemptAtUtc <= now)
            {
                return false;
            }

            reason = "unified_apply_cooldown:target=" + Safe(state.TargetProfileId)
                + ":remaining=" + (state.NextAttemptAtUtc - now).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);
            return true;
        }
    }

    private static void ExpireUnifiedAssignment(string botProfileId, DateTimeOffset now)
    {
        lock (Sync)
        {
            if (UnifiedAssignmentByBotProfileId.TryGetValue(botProfileId, out UnifiedAssignmentState state)
                && state.ExpiresAtUtc <= now)
            {
                UnifiedAssignmentByBotProfileId.Remove(botProfileId);
            }
        }
    }

    private static bool IsUnifiedHostileCandidate(OperatorDecisionSnapshot snapshot, BotOwner botOwner, Player? player)
    {
        if (player == null
            || player.Transform == null
            || player.HealthController?.IsAlive != true
            || string.IsNullOrWhiteSpace(player.ProfileId)
            || SameTarget(player.ProfileId, snapshot.BotProfileId)
            || SameTarget(player.ProfileId, snapshot.OwnerProfileId)
            || VanguardFriendlyIdentityRegistry.IsProtectedFriendlyTargetProfileId(player.ProfileId))
        {
            return false;
        }

        try
        {
            object rawPlayer = player;
            if (rawPlayer is IPlayer target
                && botOwner.BotsGroup != null
                && (botOwner.BotsGroup.IsEnemy(target) || botOwner.BotsGroup.IsPlayerEnemy(target)))
            {
                return true;
            }
        }
        catch
        {
            // Early Fika replication can temporarily hide group relations. The canonical protected-
            // The friendly registry remains the safety boundary for candidate admission.
        }

        return true;
    }

    private static Player[] GetUnifiedWorldRoster(DateTimeOffset now)
    {
        lock (Sync)
        {
            if (unifiedWorldRosterExpiresAtUtc > now)
            {
                return unifiedWorldRoster;
            }
        }

        GameWorld? world = Singleton<GameWorld>.Instance;
        Player[] next = world == null
            ? Array.Empty<Player>()
            : ReadUnifiedPlayers(world, UnifiedAllAlivePlayersListField)
                .Concat(ReadUnifiedPlayers(world, UnifiedRegisteredPlayersField))
                .Where(player => player != null && !string.IsNullOrWhiteSpace(player.ProfileId))
                .GroupBy(player => player.ProfileId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();

        lock (Sync)
        {
            unifiedWorldRoster = next;
            unifiedWorldRosterExpiresAtUtc = now + UnifiedWorldRosterCacheDuration;
            return unifiedWorldRoster;
        }
    }

    private static IEnumerable<Player> ReadUnifiedPlayers(GameWorld world, FieldInfo? field)
    {
        if (field?.GetValue(world) is not IEnumerable enumerable)
        {
            return Array.Empty<Player>();
        }

        return enumerable.OfType<Player>();
    }

    private static bool TryResolveOwnerShotEvidence(
        VanguardOwnerShotSnapshot shot,
        Vector3 targetPosition,
        float ownerDistance,
        UnifiedThreatSeed? seed,
        DateTimeOffset now,
        out OwnerShotEvidenceLevel evidence,
        out float angle,
        out bool ownerLineOfSight,
        out float ageSeconds)
    {
        evidence = OwnerShotEvidenceLevel.None;
        angle = 999f;
        ownerLineOfSight = false;
        ageSeconds = (float)Math.Max(0d, (now - shot.ObservedAtUtc).TotalSeconds);
        if (ownerDistance > UnifiedOwnerShotMaxMeters || shot.Direction.sqrMagnitude <= 0.001f)
        {
            return false;
        }

        Vector3 normalizedDirection = shot.Direction.normalized;
        Vector3 targetChest = targetPosition + Vector3.up * 1.25f;
        Vector3 toTarget = targetChest - shot.Origin;
        if (toTarget.sqrMagnitude <= 0.001f)
        {
            angle = 0f;
        }
        else
        {
            angle = Vector3.Angle(normalizedDirection, toTarget.normalized);
            float allowed = ownerDistance <= UnifiedOwnerShotCloseDistanceMeters
                ? UnifiedOwnerShotCloseAngleDegrees
                : UnifiedOwnerShotBaseAngleDegrees;
            if (angle > allowed)
            {
                return false;
            }
        }

        if (ageSeconds <= UnifiedOwnerShotVisualEvidenceMaxAgeSeconds)
        {
            Vector3 lineStart = shot.Origin + normalizedDirection * UnifiedOwnerShotMuzzleLineAdvanceMeters;
            ownerLineOfSight = HasUnifiedLineOfSight(lineStart, targetPosition, 0f, 1.25f);
        }

        // Preserve the useful owner-shot mechanic without fabricating player knowledge.
        // The order is intentional: fresh LOS is strongest, independent hostility corroborates,
        // and a bare angular match remains an observe-only suspicion.
        if (ownerLineOfSight)
        {
            evidence = OwnerShotEvidenceLevel.OwnerVisual;
        }
        else if (HasOwnerShotCorroboration(seed))
        {
            evidence = OwnerShotEvidenceLevel.Corroborated;
        }
        else
        {
            evidence = OwnerShotEvidenceLevel.GeometricSuspicion;
        }

        return true;
    }

    private static bool HasOwnerShotCorroboration(UnifiedThreatSeed? seed)
        => seed != null
            && (seed.IncomingFire
                || seed.OwnerHitReceipt
                || seed.ThreatenedOwner
                || seed.CurrentDirect
                || seed.CurrentAssigned
                || seed.CurrentThreat
                || seed.ScannerDirect
                || seed.SharedImmediate
                || seed.SharedContact);

    private static string ResolveOwnerShotSource(UnifiedWorldEntry entry)
    {
        if (entry.OwnerShotEvidence == OwnerShotEvidenceLevel.OwnerVisual)
        {
            return "owner_shot_visual";
        }

        if (entry.OwnerShotEvidence == OwnerShotEvidenceLevel.Corroborated)
        {
            return "owner_shot_corroborated";
        }

        if (entry.OwnerShotEvidence == OwnerShotEvidenceLevel.GeometricSuspicion)
        {
            return "owner_shot_geometric_suspicion";
        }

        return "world_scan";
    }

    private static bool IsSquadSuspicionKind(string? kind)
        => !string.IsNullOrWhiteSpace(kind)
            && kind.IndexOf("suspicion", StringComparison.OrdinalIgnoreCase) >= 0;

    private static float ResolveUnifiedNavDistanceCached(
        string botProfileId,
        string targetProfileId,
        Vector3 start,
        Vector3 end,
        DateTimeOffset now)
    {
        string key = Normalize(botProfileId) + "|" + Normalize(targetProfileId);
        lock (Sync)
        {
            if (UnifiedNavCacheByBotAndTarget.TryGetValue(key, out UnifiedNavCacheState cached)
                && cached.ExpiresAtUtc > now
                && Vector3.Distance(cached.Start, start) <= UnifiedNavCachePositionToleranceMeters
                && Vector3.Distance(cached.End, end) <= UnifiedNavCachePositionToleranceMeters)
            {
                return cached.Distance;
            }
        }

        float distance = ResolveUnifiedNavDistance(start, end, UnifiedNavPathMaxMeters);
        lock (Sync)
        {
            UnifiedNavCacheByBotAndTarget[key] = new UnifiedNavCacheState
            {
                Start = start,
                End = end,
                Distance = distance,
                ExpiresAtUtc = now + UnifiedNavCacheDuration
            };

            if (UnifiedNavCacheByBotAndTarget.Count > 384)
            {
                foreach (string staleKey in UnifiedNavCacheByBotAndTarget
                    .Where(entry => entry.Value.ExpiresAtUtc <= now)
                    .Select(entry => entry.Key)
                    .ToArray())
                {
                    UnifiedNavCacheByBotAndTarget.Remove(staleKey);
                }
            }
        }

        return distance;
    }

    private static float ResolveUnifiedNavDistance(Vector3 start, Vector3 end, float maxDistance)
    {
        try
        {
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(start, end, NavMesh.AllAreas, path)
                || path.status == NavMeshPathStatus.PathInvalid
                || path.corners == null
                || path.corners.Length < 2)
            {
                return float.PositiveInfinity;
            }

            float total = 0f;
            for (int index = 1; index < path.corners.Length; index++)
            {
                total += Vector3.Distance(path.corners[index - 1], path.corners[index]);
                if (total > maxDistance)
                {
                    break;
                }
            }
            return total;
        }
        catch
        {
            return float.PositiveInfinity;
        }
    }

    private static bool HasUnifiedLineOfSight(Vector3 from, Vector3 to, float fromEye, float toEye)
    {
        try
        {
            Vector3 start = from + Vector3.up * fromEye;
            Vector3 end = to + Vector3.up * toEye;
            return !Physics.Linecast(start, end, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        }
        catch
        {
            return false;
        }
    }

    private static UnifiedThreatSeed GetOrCreateSeed(Dictionary<string, UnifiedThreatSeed> seeds, string targetId)
    {
        if (!seeds.TryGetValue(targetId, out UnifiedThreatSeed seed))
        {
            seed = new UnifiedThreatSeed();
            seeds[targetId] = seed;
        }

        return seed;
    }

    private static string AppendUnifiedSource(string? current, string next)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return next;
        }
        return current.IndexOf(next, StringComparison.OrdinalIgnoreCase) >= 0 ? current : current + "+" + next;
    }

    private static string FormatUnifiedDistance(float value)
        => float.IsPositiveInfinity(value) || value == float.MaxValue
            ? "unreachable"
            : value.ToString("0.0", CultureInfo.InvariantCulture);

    private enum OwnerShotEvidenceLevel
    {
        None = 0,
        GeometricSuspicion = 1,
        Corroborated = 2,
        OwnerVisual = 3
    }

    private sealed class UnifiedThreatSeed
    {
        public bool CurrentThreat;
        public bool CurrentDirect;
        public bool CurrentAssigned;
        public bool ScannerCandidate;
        public bool ScannerDirect;
        public bool AwarenessCandidate;
        public bool SharedContact;
        public bool SharedImmediate;
        public bool SharedSuspicion;
        public bool IncomingFire;
        public bool OwnerHitReceipt;
        public bool ThreatenedOwner;
        public bool CanShootOperator;
        public string Source = string.Empty;
    }

    private readonly struct UnifiedWorldEntry
    {
        public UnifiedWorldEntry(
            Player player,
            float directDistance,
            float ownerDistance,
            bool seeded,
            bool ownerShotRelevant,
            bool ownerShotSuspicion,
            bool ownerShotOwnerLos,
            float ownerShotAngle,
            float ownerShotAgeSeconds,
            OwnerShotEvidenceLevel ownerShotEvidence)
        {
            Player = player;
            DirectDistance = directDistance;
            OwnerDistance = ownerDistance;
            Seeded = seeded;
            OwnerShotRelevant = ownerShotRelevant;
            OwnerShotSuspicion = ownerShotSuspicion;
            OwnerShotOwnerLos = ownerShotOwnerLos;
            OwnerShotAngle = ownerShotAngle;
            OwnerShotAgeSeconds = ownerShotAgeSeconds;
            OwnerShotEvidence = ownerShotEvidence;
        }

        public Player Player { get; }
        public float DirectDistance { get; }
        public float OwnerDistance { get; }
        public bool Seeded { get; }
        public bool OwnerShotRelevant { get; }
        public bool OwnerShotSuspicion { get; }
        public bool OwnerShotOwnerLos { get; }
        public float OwnerShotAngle { get; }
        public float OwnerShotAgeSeconds { get; }
        public OwnerShotEvidenceLevel OwnerShotEvidence { get; }
    }

    private sealed class UnifiedThreatCandidate
    {
        public string TargetProfileId = "none";
        public float DirectDistance;
        public float OwnerDistance;
        public float NavDistance;
        public float VerticalDelta;
        public bool VisibleToOperator;
        public bool LineToOwner;
        public bool CloseReachable;
        public bool CurrentDirect;
        public bool ScannerDirect;
        public bool SharedContact;
        public bool SharedImmediate;
        public bool IncomingFire;
        public bool OwnerHitReceipt;
        public bool OwnerShotRelevant;
        public bool OwnerShotSuspicion;
        public bool OwnerShotSuspicionOnly;
        public bool OwnerShotOwnerLos;
        public float OwnerShotAngle;
        public float OwnerShotAgeSeconds;
        public OwnerShotEvidenceLevel OwnerShotEvidence;
        public bool ThreatenedOwner;
        public bool CanShootOperator;
        public float BaseScore;
        public float Score;
        public string Intent = "observe_only";
        public string Source = "none";
        public string Reason = "none";
        public bool CanCommitToSain;
        public bool PublishToSquad;
        public bool PublishAsSuspicion;
        public bool IsImmediate;
    }

    private sealed class UnifiedAssignmentState
    {
        public string TargetProfileId = "none";
        public string Intent = "none";
        public DateTimeOffset NextAttemptAtUtc;
        public DateTimeOffset ExpiresAtUtc;
        public bool Applied;
        public bool DistantAuthoritySupported;
    }

    private sealed class UnifiedNavCacheState
    {
        public Vector3 Start;
        public Vector3 End;
        public float Distance;
        public DateTimeOffset ExpiresAtUtc;
    }
}
#endif
