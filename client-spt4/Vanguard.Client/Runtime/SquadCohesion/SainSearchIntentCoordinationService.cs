#if SPT_CLIENT
using System;
using System.Globalization;
using System.Collections.Generic;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Movement;
using System.Runtime.CompilerServices;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;

// Responsibility: characterizes SAIN Search movement intentions before a native forward path can be submitted, including exact enemy, owner, route and role-discipline evidence.
// Flow: SAINSearchClass.SwitchSearchModes prefix -> strictly resolved live Operator -> current snapshot and real SAIN path -> bounded TC assessment and instrumentation.
// Authority boundary: diagnostic ADVISE/CONSTRAIN_CANDIDATE only; no SAIN argument rewrite, movement veto, tactical lease, target change, path cancellation or translation writer.
// Invariant: an offensive-role violation alone never proves a safe alternative; unknown survival, threat, path, owner or competing authority yields to SAIN, without mutating its combat.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class SainSearchIntentCoordinationService
{
    internal const string StatusTag = "VANGUARD_TC_SAIN_SEARCH_INTENT_STATUS";
    private const float MinimumProbeIntervalSeconds = 1.1f;
    private const int MaximumReportsPerIncarnation = 150;
    private static readonly ConditionalWeakTable<object, SearchIntentProbe> Probes = new();

    internal static void ObserveBeforeNativeSearchMovement(object? search, object? enemy, bool shallSprint)
    {
        if (search == null || enemy == null || !VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }

        // All SAIN bots hit this hook: apply a cheap per-object gate before any registry, awareness or reflection work.
        SearchIntentProbe probe = Probes.GetValue(search, _ => new SearchIntentProbe());
        // Every SwitchSearchModes invocation starts a NEW admission scope, even if the
        // expensive probe is throttled. A candidate from an earlier call in this Unity
        // frame must never be mistaken for this call's current native Search intention.
        probe.AdmittedPath = null;
        probe.AdmittedEnemyId = string.Empty;
        probe.AdmittedFrame = -1;
        probe.LastAdmissionFrame = Time.frameCount;
        probe.SampledCurrentInvocation = false;
        probe.LastAdmissionStatus = "sample_throttled_or_budget_exhausted";
        float monotonicNow = Time.time;
        // A reporting quota is not a gameplay or shadow-admission quota. Keep the
        // bounded probe alive throughout long raids; Emit retains its own log cap.
        if (monotonicNow < probe.NextAt)
        {
            return;
        }
        probe.NextAt = monotonicNow + MinimumProbeIntervalSeconds;
        probe.SampledCurrentInvocation = true;
        probe.LastSampleAt = monotonicNow;
        probe.HasSample = true;
        probe.LastAdmissionStatus = "probe_not_resolved";
        try
        {
            object? botOwnerValue = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(search, "BotOwner")
                ?? VanguardOperatorRuntimeAuditReflection.GetDeep(search, "Bot", "BotOwner");
            if (botOwnerValue is not BotOwner botOwner || botOwner.IsDead
                || string.IsNullOrWhiteSpace(botOwner.ProfileId)
                || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botOwner.ProfileId, out VanguardRaidOperatorRuntimeRecord runtime)
                || runtime.BotOwner == null || !ReferenceEquals(runtime.BotOwner, botOwner)
                || string.IsNullOrWhiteSpace(runtime.OwnerProfileId))
            {
                return;
            }

            string incarnationKey = runtime.RaidSessionId + ":" + runtime.RuntimeIncarnation.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(probe.IncarnationKey, incarnationKey, StringComparison.Ordinal))
            {
                probe.IncarnationKey = incarnationKey;
                probe.Reports = 0;
            }

            if (!MarksmanForwardRoleDisciplinePolicy.IsDesignatedMarksmanIdentity(runtime.TacticalRole, runtime.TacticalSpecialty))
            {
                return;
            }
            DateTimeOffset now = DateTimeOffset.UtcNow;
            bool snapshotReady = VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(runtime.BotProfileId, out OperatorDecisionSnapshot snapshot)
                && snapshot.Alive
                && string.Equals(snapshot.OwnerProfileId, runtime.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && VanguardOperatorDecisionSnapshotService.IsFresh(snapshot, now, out _);
            if (!snapshotReady)
            {
                probe.LastAdmissionStatus = "snapshot_unavailable_or_stale";
                Emit(probe, runtime, "YIELD", "snapshot_unavailable_or_stale", "none", "unknown", shallSprint, Vector3.zero, "unknown", -1, false, "none", "none");
                return;
            }

            string enemyId = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "EnemyProfileId") as string ?? string.Empty;
            string activeEnemyId = snapshot.Threat.EnemyId;
            bool exactEnemy = !string.IsNullOrWhiteSpace(enemyId)
                && string.Equals(enemyId, activeEnemyId, StringComparison.OrdinalIgnoreCase);
            bool nativeVisibilityUnknownOrActive = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "IsVisible") is not bool nativeVisible
                || nativeVisible;
            // Disaggregate protections instead of treating every active primary window as a survival emergency.
            // The existing scheduler contract treats SainCombatReleaseWindow as COMBAT ownership,
            // not as an independent loot/medical/movement executor. Executive movement remains disabled.
            string guardFactors = DescribeSearchGuardFactors(snapshot, nativeVisibilityUnknownOrActive, enemyId);
            bool survivalUnknownOrActive = guardFactors != "none";
            bool combatPrimaryCompatible = IsCompatibleCombatPrimary(snapshot);
            string nativeVisibility = nativeVisibilityUnknownOrActive ? "visible_or_unknown" : "not_visible";

            object? enemyPath = VanguardOperatorRuntimeAuditReflection.GetDeep(enemy, "Path", "PathToEnemy");
            NavMeshPath? nativePath = enemyPath as NavMeshPath;
            Vector3[]? corners = nativePath?.corners;
            int count = corners?.Length ?? 0;
            bool routeQualified = nativePath != null && nativePath.status == NavMeshPathStatus.PathComplete
                && corners != null && count >= 2 && count <= 80;
            Vector3 destination = routeQualified ? corners![count - 1] : Vector3.zero;
            routeQualified = routeQualified && IsFinite(destination) && IsFinite(botOwner.Position)
                && Mathf.Abs(destination.y - botOwner.Position.y) <= 2.5f;
            string pathStatus = nativePath?.status.ToString() ?? "unavailable";
            // Passive, ordered explanation of the existing native path predicate.
            // No new NavMesh query, routing decision, or mutation of SAIN state.
            string nativeRouteQualification = routeQualified ? "qualified"
                : nativePath == null ? "path_missing"
                : nativePath.status != NavMeshPathStatus.PathComplete ? "path_not_complete"
                : corners == null ? "corners_unavailable"
                : count < 2 ? "corners_insufficient"
                : count > 80 ? "corners_excessive"
                : !IsFinite(destination) ? "destination_nonfinite"
                : !IsFinite(botOwner.Position) ? "operator_position_nonfinite"
                : Mathf.Abs(destination.y - botOwner.Position.y) > 2.5f ? "vertical_delta_exceeded"
                : "unclassified";
            string currentPathId = "none";
            string currentPathStarted = "none";
            object? currentPath = VanguardOperatorRuntimeAuditReflection.GetDeep(search, "Bot", "Mover", "ActivePath");
            if (currentPath != null)
            {
                currentPathId = Safe(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(currentPath, "Id")?.ToString());
                currentPathStarted = Safe(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(currentPath, "TimeStarted")?.ToString());
            }

            string alternateRouteProof = "NOT_EVALUATED";
            string disposition = "YIELD";
            string reason = !exactEnemy ? "search_target_not_exact_snapshot_threat" :
                survivalUnknownOrActive ? "survival_or_authority_not_proven_nonurgent" :
                !routeQualified ? "route_unqualified_or_multistorey" : "policy_not_evaluated";

            // Policy assessment is READ-ONLY, distinct from permission to redirect a native path.
            // HardReturn may forbid a correction but must not hide a role violation in diagnostics.
            // Exact target, SearchAction and a qualified native endpoint are required even for shadow.
            bool searchAction = string.Equals(snapshot.Sain.CurrentAction, "SearchAction", StringComparison.OrdinalIgnoreCase);
            bool policyEvaluated = false;
            bool policyWouldConstrain = false;
            string policyAssessment = "NOT_EVALUATED";
            string policyReasonForReceipt = "none";
            bool routeProofEligible = false;
            bool routeCandidateReady = false;
            if (exactEnemy && searchAction && routeQualified)
            {
                bool allowed = MarksmanForwardRoleDisciplinePolicy.CanAuthorizeForwardTranslation(
                    snapshot, enemyId, destination, "altdevelop", now, out string policyReason);
                policyEvaluated = true;
                policyReasonForReceipt = policyReason;
                policyWouldConstrain = !allowed
                    && policyReason.StartsWith("blocked_marksman_forward_high_threat:", StringComparison.Ordinal);
                policyAssessment = policyWouldConstrain ? "WOULD_CONSTRAIN"
                    : allowed ? "PERMISSIVE" : "INDETERMINATE";

                // Actual admission is still gated by ALL survival/authority guards, including
                // HardOutsideBubble. No classification can waive those guards or grant movement ownership.
                if (!survivalUnknownOrActive)
                {
                    reason = policyReason;
                    disposition = policyWouldConstrain ? "CONSTRAIN_CANDIDATE_NOT_EXECUTED"
                        : policyReason.StartsWith("marksman_forward_discipline_ready:", StringComparison.Ordinal)
                            ? "STANDOFF_POLICY_ASSESSMENT_ONLY" : "YIELD";
                    routeProofEligible = policyWouldConstrain;
                    if (routeProofEligible && SainSearchAlternativeRouteProofService.TryFindStandoffRoute(
                            snapshot, botOwner, enemyId, now, out NavMeshPath? admittedPath,
                            out alternateRouteProof) && admittedPath != null)
                    {
                        // Only for this exact native Search invocation; no lease and no cross-tick route authority.
                        probe.AdmittedPath = admittedPath;
                        probe.AdmittedEnemyId = enemyId;
                        probe.AdmittedFrame = Time.frameCount;
                        routeCandidateReady = true;
                    }
                }
            }

            // Cohesion geometry is telemetry, never a substitute NavMesh path or a movement lease.
            // The owner anchor must be both present and trustworthy before projecting the native destination.
            bool ownerAnchorReliable = snapshot.SquadCohesion.OwnerKnown
                && snapshot.SquadCohesion.OwnerReliableForActiveMovement
                && snapshot.SquadCohesion.OwnerPosition.HasValue
                && IsFinite(snapshot.SquadCohesion.OwnerPosition.Value);
            float hardReturnThreshold = VanguardMovementAuthorityDoctrine.ForOwner(snapshot.OwnerProfileId).HardCorrectionMeters;
            float ownerDistance = ownerAnchorReliable && IsFinite(snapshot.Position)
                ? HorizontalDistance(snapshot.Position, snapshot.SquadCohesion.OwnerPosition!.Value)
                : float.NaN;
            float destinationOwnerDistance = ownerAnchorReliable && routeQualified
                ? HorizontalDistance(destination, snapshot.SquadCohesion.OwnerPosition!.Value)
                : float.NaN;
            string priority = snapshot.MovementAuthority.HardOutsideBubble ? "HARD_RETURN_FIRST"
                : BlocksSearchForNoncombatPrimary(snapshot) ? "NONCOMBAT_PRIMARY_FIRST"
                : survivalUnknownOrActive ? "SURVIVAL_OR_UNKNOWN_FIRST"
                : "SEARCH_SHADOW_ELIGIBLE";
            // Compact cause travels to the Walk/Run receipt for the SAME invocation.
            // It is not an authority token; the native bridge still rechecks all safety gates.
            probe.LastAdmissionStatus = routeCandidateReady ? "same_invocation_candidate_ready"
                : !exactEnemy ? "snapshot_threat_mismatch"
                : !searchAction ? "not_search_action"
                : !routeQualified ? "native_route_unqualified"
                : snapshot.MovementAuthority.HardOutsideBubble ? "hard_return_guarded"
                : survivalUnknownOrActive ? "survival_or_authority_guarded"
                : !policyEvaluated ? "role_policy_not_evaluated"
                : !policyWouldConstrain ? "role_policy_not_constraining"
                : routeProofEligible ? "routeproof_" + CompactRouteProofStatus(alternateRouteProof)
                : "routeproof_not_eligible";
            Emit(probe, runtime, disposition, reason, enemyId, Safe(snapshot.Sain.CurrentAction), shallSprint,
                routeQualified ? destination : Vector3.zero, pathStatus, count, routeQualified,
                currentPathId, currentPathStarted, alternateRouteProof,
                guardFactors, Safe(snapshot.PrimaryExecution.WindowKind), combatPrimaryCompatible, nativeVisibility,
                policyEvaluated, policyWouldConstrain, policyAssessment, policyReasonForReceipt,
                routeProofEligible, routeCandidateReady, priority, ownerAnchorReliable,
                ownerDistance, hardReturnThreshold, destinationOwnerDistance,
                nativeRouteQualification);
        }
        catch (Exception exception)
        {
            probe.LastAdmissionStatus = "probe_exception_fail_open";
            // Never let diagnostics disturb the native Search / movement flow.
            if (!probe.FailureReported)
            {
                probe.FailureReported = true;
                VanguardClientDiagnosticsLog.Warning(StatusTag,
                    "SAIN_SEARCH_INTENT_PROBE_FAILED type=" + Safe(exception.GetType().Name) + "; failOpen=true; mutation=false");
            }
        }
    }

    // Exposes a cause only for an invocation sampled or intentionally skipped in this frame.
    // A diagnostic string must NEVER make an alternative executable.
    internal static string GetCurrentAdmissionStatus(object? search)
    {
        if (search == null || !Probes.TryGetValue(search, out SearchIntentProbe probe)
            || probe.LastAdmissionFrame != Time.frameCount)
        {
            return "no_current_invocation_probe";
        }
        return probe.LastAdmissionStatus;
    }

    // Read-only timing witness captured immediately by the matching native Search window.
    // A skipped invocation is not replayed and cannot inherit a previous candidate.
    internal static void GetCurrentProbeTiming(object? search, out bool sampledCurrentInvocation, out float ageSeconds)
    {
        sampledCurrentInvocation = false;
        ageSeconds = float.NaN;
        if (search == null || !Probes.TryGetValue(search, out SearchIntentProbe probe)
            || probe.LastAdmissionFrame != Time.frameCount)
        {
            return;
        }
        sampledCurrentInvocation = probe.SampledCurrentInvocation;
        float monotonicNow = Time.time;
        if (probe.HasSample && !float.IsNaN(monotonicNow) && !float.IsInfinity(monotonicNow)
            && monotonicNow >= probe.LastSampleAt)
        {
            ageSeconds = monotonicNow - probe.LastSampleAt;
        }
    }

    private static string CompactRouteProofStatus(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "NOT_EVALUATED") return "not_evaluated";
        string token = value;
        int separator = token.IndexOf(':');
        if (separator >= 0 && separator + 1 < token.Length) token = token.Substring(separator + 1);
        separator = token.IndexOfAny(new[] { ';', ',' });
        if (separator >= 0) token = token.Substring(0, separator);
        return token.Length <= 96 ? Safe(token) : Safe(token.Substring(0, 96));
    }

    internal static bool TryGetCurrentStandoffCandidate(object search, string enemyId, out NavMeshPath? path)
    {
        path = null;
        if (search == null || string.IsNullOrWhiteSpace(enemyId)
            || !Probes.TryGetValue(search, out SearchIntentProbe probe)
            || probe.AdmittedFrame != Time.frameCount || probe.AdmittedPath == null
            || !string.Equals(probe.AdmittedEnemyId, enemyId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        path = probe.AdmittedPath;
        return true;
    }

    private static void Emit(SearchIntentProbe probe, VanguardRaidOperatorRuntimeRecord runtime,
        string disposition, string reason, string enemyId, string currentAction,
        bool shallSprint, Vector3 destination, string pathStatus, int cornerCount,
        bool routeQualified, string currentPathId, string currentPathStarted,
        string alternateRouteProof = "NOT_EVALUATED", string guardFactors = "unavailable",
        string primaryKind = "unknown", bool combatPrimaryCompatible = false,
        string nativeVisibility = "unknown", bool policyEvaluated = false,
        bool policyWouldConstrain = false, string policyAssessment = "NOT_EVALUATED",
        string policyReason = "none", bool routeProofEligible = false, bool routeCandidateReady = false,
        string arbitrationPriority = "UNKNOWN", bool ownerAnchorReliable = false,
        float ownerDistance = float.NaN, float hardReturnThreshold = float.NaN,
        float destinationOwnerDistance = float.NaN,
        string nativeRouteQualification = "not_evaluated")
    {
        if (probe.Reports >= MaximumReportsPerIncarnation)
        {
            return;
        }
        probe.Reports++;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            "SAIN_SEARCH_INTENT operator=" + Safe(runtime.OperatorId)
            + ";botProfile=" + Safe(runtime.BotProfileId)
            + ";owner=" + Safe(runtime.OwnerProfileId)
            + ";incarnation=" + runtime.RuntimeIncarnation.ToString(CultureInfo.InvariantCulture)
            + ";role=" + Safe(runtime.TacticalRole)
            + ";enemy=" + Safe(enemyId)
            + ";action=" + Safe(currentAction)
            + ";nativeSource=SAINSearchClass.SwitchSearchModes"
            + ";sprintRequested=" + Bool(shallSprint)
            + ";nativePathStatus=" + Safe(pathStatus)
            + ";nativePathCorners=" + cornerCount.ToString(CultureInfo.InvariantCulture)
            + ";routeQualified=" + Bool(routeQualified)
            + ";nativeRouteQualification=" + Safe(nativeRouteQualification)
            + ";destination=" + (routeQualified ? Pos(destination) : "unknown")
            + ";activePathId=" + Safe(currentPathId)
            + ";activePathStarted=" + Safe(currentPathStarted)
            + ";advice=" + disposition
            + ";reason=" + Safe(reason)
            + ";guardFactors=" + Safe(guardFactors)
            + ";primaryKind=" + Safe(primaryKind)
            + ";combatPrimaryCompatible=" + Bool(combatPrimaryCompatible)
            + ";nativeVisibility=" + Safe(nativeVisibility)
            + ";policyEvaluated=" + Bool(policyEvaluated)
            + ";policyWouldConstrain=" + Bool(policyWouldConstrain)
            + ";policyAssessment=" + Safe(policyAssessment)
            + ";policyReason=" + Safe(policyReason)
            + ";routeProofEligible=" + Bool(routeProofEligible)
            + ";routeCandidateReady=" + Bool(routeCandidateReady)
            + ";arbitrationPriority=" + Safe(arbitrationPriority)
            + ";ownerAnchorReliable=" + Bool(ownerAnchorReliable)
            + ";ownerDistanceMeters=" + Metric(ownerDistance)
            + ";hardReturnThresholdMeters=" + Metric(hardReturnThreshold)
            + ";combatCohesionLimitMeters=" + Metric(VanguardMovementAuthorityDoctrine.CombatCohesionHardReturnMeters)
            + ";nativeDestinationOwnerDistanceMeters=" + Metric(destinationOwnerDistance)
            + ";nativeDestinationClosesOwnerGap=" + Bool(IsMetric(ownerDistance) && IsMetric(destinationOwnerDistance)
                && destinationOwnerDistance + 0.5f < ownerDistance)
            + ";alternateNavMeshRouteProof=" + Safe(alternateRouteProof)
            + ";survivalVetoAuthority=NOT_GRANTED"
            + ";executiveMutation=false; failOpen=true; nativeSainPreserved=true");
    }

    // Shared narrow interpretation of the already-authoritative scheduler contract.
    // An active noncombat primary always blocks Search admission. Combat release alone does not
    // prove that the Search is safe: visibility, cover, damage, grenade, medical and target guards remain.
    internal static bool IsCompatibleCombatPrimary(OperatorDecisionSnapshot snapshot) =>
        snapshot != null && snapshot.PrimaryExecution.Active
        && VanguardPrimaryExecutionContract.IsCombatPrimaryKind(snapshot.PrimaryExecution.WindowKind);

    internal static bool BlocksSearchForNoncombatPrimary(OperatorDecisionSnapshot snapshot) =>
        snapshot == null || (snapshot.PrimaryExecution.Active && !IsCompatibleCombatPrimary(snapshot));

    private static string DescribeSearchGuardFactors(OperatorDecisionSnapshot snapshot,
        bool nativeVisibilityUnknownOrActive, string enemyId)
    {
        var factors = new List<string>(12);
        if (nativeVisibilityUnknownOrActive) factors.Add("native_visibility_visible_or_unknown");
        if (snapshot.Sain.Active != true) factors.Add("sain_inactive_or_unknown");
        if (snapshot.Sain.LayersActive != true) factors.Add("sain_layers_inactive_or_unknown");
        if (!string.Equals(snapshot.Sain.CurrentAction, "SearchAction", StringComparison.OrdinalIgnoreCase))
            factors.Add("not_search_action");
        if (snapshot.Sain.RunningToCover != false) factors.Add("cover_run_or_unknown");
        if (snapshot.Threat.EnemyVisible != false) factors.Add("snapshot_visible_or_unknown");
        if (snapshot.Threat.ShotMeRecently != false) factors.Add("recent_hit_or_unknown");
        if (snapshot.Threat.ShotAtMeRecently != false) factors.Add("recent_shot_at_or_unknown");
        if (snapshot.GrenadeHazard.HasRelevantHazard) factors.Add("grenade_hazard");
        if (snapshot.Medical.Actionability.AnyMedicineUsing) factors.Add("medicine_active");
        if (snapshot.Medical.Actionability.GrenadeThrowing) factors.Add("grenade_throwing");
        if (snapshot.Medical.Actionability.Reloading) factors.Add("reloading");
        if (snapshot.MovementAuthority.HardOutsideBubble) factors.Add("hard_bubble_return");
        if (BlocksSearchForNoncombatPrimary(snapshot)) factors.Add("noncombat_primary:" + Safe(snapshot.PrimaryExecution.WindowKind));
        if (snapshot.ThreatScan.CandidatePosition.HasValue
            && !string.IsNullOrWhiteSpace(snapshot.ThreatScan.CandidateThreatId)
            && !string.Equals(snapshot.ThreatScan.CandidateThreatId, "none", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(snapshot.ThreatScan.CandidateThreatId, enemyId, StringComparison.OrdinalIgnoreCase))
            factors.Add("secondary_threat");
        return factors.Count == 0 ? "none" : string.Join(",", factors);
    }

    // Flat XZ projection only: not a navigability, safety or authority witness.
    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }
    private static bool IsMetric(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f;
    private static string Metric(float value) => IsMetric(value) ? value.ToString("0.0", CultureInfo.InvariantCulture) : "unknown";

    private static bool IsFinite(Vector3 p) =>
        !float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsNaN(p.z)
        && !float.IsInfinity(p.x) && !float.IsInfinity(p.y) && !float.IsInfinity(p.z);
    private static string Pos(Vector3 p) =>
        p.x.ToString("0.0", CultureInfo.InvariantCulture) + ","
        + p.y.ToString("0.0", CultureInfo.InvariantCulture) + ","
        + p.z.ToString("0.0", CultureInfo.InvariantCulture);
    private static string Safe(string? s) => string.IsNullOrWhiteSpace(s) ? "none"
        : s.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\n', '_').Replace('\r', '_');
    private static string Bool(bool b) => b ? "true" : "false";
    private sealed class SearchIntentProbe
    {
        internal float NextAt;
        internal int Reports;
        internal string IncarnationKey = string.Empty;
        internal bool FailureReported;
        internal NavMeshPath? AdmittedPath;
        internal string AdmittedEnemyId = string.Empty;
        internal int AdmittedFrame = -1;
        internal int LastAdmissionFrame = -1;
        internal string LastAdmissionStatus = "none";
        internal bool SampledCurrentInvocation;
        internal bool HasSample;
        internal float LastSampleAt;
    }
}
#endif
