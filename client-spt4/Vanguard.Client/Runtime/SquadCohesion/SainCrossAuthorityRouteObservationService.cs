#if SPT_CLIENT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using EFT;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Decision;

// Responsibility: observes SAIN's real physical-path submissions and its cover-selection outcome for Vanguard Operators.
// Flow: read-only SAIN Harmony callbacks -> exact live Operator bind -> bounded snapshot/qualified-threat geometry -> throttled transition telemetry.
// Authority boundary: no path changes, cover exclusion, SAIN decision adjustment, target promotion, primary-window admission, Stop, Resume, or movement lease.
// Invariant: source-backed path observations are NOT a risk veto; unknown LOS, multi-storey geometry and survival urgency must never be classified as safe/unsafe from raw proximity alone.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class SainCrossAuthorityRouteObservationService
{
    internal const string StatusTag = "VANGUARD_TC_SAIN_ROUTE_SAFETY_SHADOW_STATUS";
    private static readonly object Sync = new();
    private static readonly Dictionary<string, EmissionState> ByProfile = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxEmissionsPerProfile = 240;
    private const double MinimumLogSeconds = 0.75d;
    private const double SameDestinationLogSeconds = 8d;
    private const double CoverFallbackLogSeconds = 5d;
    private const double MovementIntentLogSeconds = 2d;
    private const int MaxDogFightEmissionsPerProfile = 48;
    private const int MaxPathReuseEmissionsPerProfile = 48;
    // One weak gate per SAIN mover. All other bots are skipped before any Vanguard registry read.
    // This captures execution correlation, not a permission to veto an EFT/SAIN movement writer.
    private static readonly ConditionalWeakTable<object, PhysicalPathProbeGate> PhysicalPathGates = new();
    private const float PhysicalSampleIntervalSeconds = 1.5f;
    private const int MaxPhysicalSamplesPerProfile = 380;

    internal static void ObserveNewPath(object? sainMover, Vector3[]? corners, Vector3 destination, bool sprint)
    {
        try
        {
            if (!TryResolveOperator(sainMover, out VanguardRaidOperatorRuntimeRecord runtime))
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            string profileId = runtime.BotProfileId;
            bool hasSnapshot = VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(profileId, out OperatorDecisionSnapshot snapshot)
                && VanguardOperatorDecisionSnapshotService.IsFresh(snapshot, now, out _);
            string sainAction = hasSnapshot ? snapshot.Sain.CurrentAction : "snapshot_unavailable";
            if (!ShouldEmit(EmissionIdentity(runtime), "path", destination, sainAction, now, MinimumLogSeconds, SameDestinationLogSeconds))
            {
                return;
            }

            BotOwner? liveBotOwner = runtime.BotOwner;
            if (liveBotOwner == null)
            {
                return;
            }

            Vector3 start = corners != null && corners.Length > 0 ? corners[0] : liveBotOwner.Position;
            int cornerCount = corners?.Length ?? 0;
            float pathLength = 0f;
            for (int i = 1; corners != null && i < corners.Length; i++)
            {
                pathLength += Vector3.Distance(corners[i - 1], corners[i]);
            }

            int qualifiedThreats = 0;
            int threatsWithin25OfPath = 0;
            float nearestHorizontalDistance = float.PositiveInfinity;
            float nearestVerticalOffset = float.PositiveInfinity;
            float nearestPathThreeDimensional = float.PositiveInfinity;
            VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation nearestThreat = default;
            bool nearestThreatAvailable = false;
            // No world scans: only existing Awareness-authorized contacts; LOS shares the established TC per-owner budget.
            foreach (var threat in VanguardCombatAwarenessBridge.GetFreshQualifiedTacticalCoordinationThreats(now))
            {
                if (!string.Equals(threat.OwnerProfileId, runtime.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                    || threat.ExpiresAtUtc <= now)
                {
                    continue;
                }
                qualifiedThreats++;
                float threeDimensional = MinimumThreeDimensionalDistanceToPath(threat.RuntimePosition, corners, start, destination);
                if (threeDimensional < nearestPathThreeDimensional)
                {
                    nearestPathThreeDimensional = threeDimensional;
                    nearestThreat = threat;
                    nearestThreatAvailable = true;
                }
                float horizontal = MinimumHorizontalDistanceToPath(threat.RuntimePosition, corners, start, destination);
                if (horizontal < nearestHorizontalDistance)
                {
                    nearestHorizontalDistance = horizontal;
                    nearestVerticalOffset = Mathf.Abs(threat.RuntimePosition.y - destination.y);
                }
                if (horizontal <= 25f)
                {
                    threatsWithin25OfPath++;
                }
            }
            // One previously-qualified contact, one already-selected destination, zero new paths.
            // A clear static linecast is only exposure evidence; it cannot prove a shot, trajectory safety or an alternative.
            string destinationLos = "UNRESOLVED_NO_QUALIFIED_THREAT";
            string destinationFacingDot = "unknown";
            if (nearestThreatAvailable && cornerCount >= 2)
            {
                int usedLinecasts = 0;
                bool completed = ThreatExposureObservationService.TryObserveExistingPoint(
                    runtime.OwnerProfileId, destination, nearestThreat.RuntimePosition, nearestThreat.RuntimeLookDirection,
                    ref usedLinecasts, out bool available, out bool clear, out float facingDot, out _, out _);
                destinationLos = !completed ? "UNRESOLVED_BUDGET" : !available
                    ? "UNRESOLVED_GEOMETRY" : clear ? "CLEAR_STATIC_LINECAST" : "OCCLUDED_OR_UNRESOLVED";
                if (completed && available)
                {
                    destinationFacingDot = Number(facingDot);
                }
            }
            else if (cornerCount < 2)
            {
                destinationLos = "UNRESOLVED_INSUFFICIENT_CORNERS";
            }
            // S3a: route-segment evidence from the *submitted* corners, never from a fabricated shortcut.
            // Closest-route geometry and two additional static point samples are observation only.
            // The selected threat is an already-qualified Awareness contact; other threats remain untested.
            string localSegmentLos = "UNRESOLVED_NO_QUALIFIED_THREAT";
            int localSegmentSamplesAttempted = 0;
            int localSegmentSamplesClear = 0;
            int localSegmentSamplesOccludedOrUnknown = 0;
            int localSegmentSamplesUnavailable = 0;
            float routeVerticalSpan = float.NaN;
            float routeMaximumCornerVerticalStep = float.NaN;
            float closestRouteArcMeters = float.NaN;
            string nearestThreatKind = nearestThreatAvailable ? Clean(nearestThreat.ContactKind) : "none";
            string nearestThreatContactAgeSeconds = nearestThreatAvailable
                ? Number((float)Math.Max(0d, (now - nearestThreat.ObservedAtUtc).TotalSeconds)) : "unknown";
            string nearestThreatGeometryAgeSeconds = nearestThreatAvailable
                ? Number((float)Math.Max(0d, (now - nearestThreat.CapturedAtUtc).TotalSeconds)) : "unknown";
            if (nearestThreatAvailable && corners != null && corners.Length >= 2
                && TryFindClosestSubmittedRoutePoint(corners, nearestThreat.RuntimePosition,
                    out Vector3 closestRoutePoint, out float closestArc, out float submittedLength,
                    out routeVerticalSpan, out routeMaximumCornerVerticalStep))
            {
                closestRouteArcMeters = closestArc;
                // Two samples at most, in the immediate vicinity of the closest *route* location.
                // A geometrically clear point is NOT a safe traversable alternative and does not prove danger.
                int sharedLinecastCount = 0;
                SampleLocalSegmentPoint(runtime.OwnerProfileId, closestRoutePoint, nearestThreat,
                    ref sharedLinecastCount, ref localSegmentSamplesAttempted, ref localSegmentSamplesClear,
                    ref localSegmentSamplesOccludedOrUnknown, ref localSegmentSamplesUnavailable,
                    out string closestSampleStatus);
                float secondArc = closestArc + 4f <= submittedLength ? closestArc + 4f
                    : Mathf.Max(0f, closestArc - 4f);
                string adjacentSampleStatus = "UNRESOLVED_SHORT_ROUTE";
                if (Mathf.Abs(secondArc - closestArc) >= 1f
                    && TryPointOnSubmittedRoute(corners, secondArc, out Vector3 adjacentRoutePoint))
                {
                    SampleLocalSegmentPoint(runtime.OwnerProfileId, adjacentRoutePoint, nearestThreat,
                        ref sharedLinecastCount, ref localSegmentSamplesAttempted, ref localSegmentSamplesClear,
                        ref localSegmentSamplesOccludedOrUnknown, ref localSegmentSamplesUnavailable,
                        out adjacentSampleStatus);
                }
                localSegmentLos = closestSampleStatus + "|" + adjacentSampleStatus;
            }
            else if (nearestThreatAvailable)
            {
                localSegmentLos = "UNRESOLVED_SUBMITTED_ROUTE_GEOMETRY";
            }
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"SAIN_ROUTE_SUBMISSION_SHADOW botProfile={Clean(profileId)}; operator={Clean(runtime.OperatorId)}; owner={Clean(runtime.OwnerProfileId)}; role={Clean(runtime.TacticalRole)}; specialty={Clean(runtime.TacticalSpecialty)}; sainAction={Clean(sainAction)}; primary={Clean(hasSnapshot ? snapshot.PrimaryExecution.WindowKind : "snapshot_unavailable")}; currentSainCombat={Bool(hasSnapshot && snapshot.Sain.IsInCombat == true)}; sprint={Bool(sprint)}; start={Position(start)}; destination={Position(destination)}; pathCorners={cornerCount}; pathLength={Number(pathLength)}; qualifiedThreatCount={qualifiedThreats}; threatsWithin25OfPath={threatsWithin25OfPath}; nearestThreatHorizontal={Number(nearestHorizontalDistance)}; nearestThreatDestinationVertical={Number(nearestVerticalOffset)}; nearestThreatPath3d={Number(nearestPathThreeDimensional)}; destinationLosSample={destinationLos}; destinationThreatFacingDot={destinationFacingDot}; routeVerticalSpan={Number(routeVerticalSpan)}; routeMaxCornerVerticalStep={Number(routeMaximumCornerVerticalStep)}; nearestThreatContactKind={nearestThreatKind}; nearestThreatContactAgeSeconds={nearestThreatContactAgeSeconds}; nearestThreatGeometryAgeSeconds={nearestThreatGeometryAgeSeconds}; closestRouteArcMeters={Number(closestRouteArcMeters)}; localSegmentLos={localSegmentLos}; localSegmentSamplesAttempted={localSegmentSamplesAttempted}; localSegmentSamplesClear={localSegmentSamplesClear}; localSegmentSamplesOccludedOrUnknown={localSegmentSamplesOccludedOrUnknown}; localSegmentSamplesUnavailable={localSegmentSamplesUnavailable}; submittedUtc={now:O}; submittedMono={Number(Time.time)}; segmentEvidenceScope=single_nearest_awareness_threat_submitted_corners_static_only; alternativeRouteProof=NOT_EVALUATED; survivalUrgency=UNVERIFIED; activeOwnerReceipt=UNVERIFIED_SNAPSHOT_ONLY; riskClassification=UNVERIFIED_RAW_GEOMETRY; lineOfSightProved=false; safeAlternateProved=false; interpretation=path_submission_not_safety_veto; mutation=false; tag={StatusTag}");
        }
        catch (Exception exception)
        {
            WarnOnce("path", exception);
        }
    }

    // Called *after* SAINMoverClass.CheckTickPath. Unlike the submission hook, this callback
    // samples a live path that has passed through BotPathDataManual.TickPath. The sampled
    // world position can demonstrate movement while a path is active; it does NOT prove
    // that SAIN is the exclusive physical writer or that its route is safe.
    internal static void ObservePhysicalPathTick(object? sainMover, bool pathTicked)
    {
        if (!pathTicked || sainMover == null || !VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }
        try
        {
            float mono = Time.time;
            // The tick is called for every SAIN bot. Throttle *before* reflection/registry access.
            PhysicalPathProbeGate gate = PhysicalPathGates.GetValue(sainMover, _ => new PhysicalPathProbeGate());
            if (mono < gate.NextSampleAt)
            {
                return;
            }
            gate.NextSampleAt = mono + PhysicalSampleIntervalSeconds;
            if (!TryResolveOperator(sainMover, out VanguardRaidOperatorRuntimeRecord runtime)
                || runtime.BotOwner == null)
            {
                return;
            }

            object? activePath = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainMover, "ActivePath");
            if (activePath == null)
            {
                gate.RunIdentity = string.Empty;
                return;
            }
            object? startedValue = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "TimeStarted");
            if (startedValue is not float started || (float.IsNaN(started) || float.IsInfinity(started)) || started < 0f)
            {
                gate.RunIdentity = string.Empty;
                return;
            }
            object? pathIdValue = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "Id");
            object? destinationValue = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "Destination");
            object? startValue = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "StartPosition");
            if (pathIdValue is not int pathId || destinationValue is not Vector3 destination
                || startValue is not Vector3 pathStart || !IsFinite(destination) || !IsFinite(pathStart))
            {
                return;
            }

            Vector3 current = runtime.BotOwner.Position;
            if (!IsFinite(current))
            {
                return;
            }
            string runIdentity = EmissionIdentity(runtime) + "|" + pathId.ToString(CultureInfo.InvariantCulture)
                + "|" + started.ToString("R", CultureInfo.InvariantCulture);
            bool isFirstSample = !string.Equals(gate.RunIdentity, runIdentity, StringComparison.Ordinal);
            bool discontinuity = false;
            float sincePreviousMeters = 0f;
            if (isFirstSample)
            {
                gate.RunIdentity = runIdentity;
                gate.ObservedMeters = 0f;
            }
            else
            {
                sincePreviousMeters = Vector3.Distance(gate.LastPosition, current);
                discontinuity = sincePreviousMeters > 15f;
                if (!discontinuity)
                {
                    gate.ObservedMeters += sincePreviousMeters;
                }
            }
            gate.LastPosition = current;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!ShouldEmit(EmissionIdentity(runtime), "physical", current, runIdentity, now,
                    PhysicalSampleIntervalSeconds, PhysicalSampleIntervalSeconds))
            {
                return;
            }

            bool freshSnapshot = VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(runtime.BotProfileId,
                    out OperatorDecisionSnapshot snapshot)
                && VanguardOperatorDecisionSnapshotService.IsFresh(snapshot, now, out _);
            string action = freshSnapshot ? snapshot.Sain.CurrentAction : "snapshot_unavailable";
            string primary = freshSnapshot ? snapshot.PrimaryExecution.WindowKind : "snapshot_unavailable";
            string movementOwner = freshSnapshot ? snapshot.MovementAuthority.CurrentAuthority : "snapshot_unavailable";
            string snapshotAge = freshSnapshot ? Number((float)(now - snapshot.CapturedAtUtc).TotalSeconds) : "unknown";
            string status = Clean(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "Status")?.ToString());
            string sprintUrgency = Clean(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "SprintUrgency")?.ToString());
            string requestedSprint = MaybeBool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "WantToSprint"));
            string cancelRequested = MaybeBool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "CancelRequested"));
            object? cornerValue = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "CurrentIndex");
            object? pathLengthValue = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(activePath, "PathLength");
            string corner = cornerValue is int index ? index.ToString(CultureInfo.InvariantCulture) : "unknown";
            string length = pathLengthValue is float value ? Number(value) : "unknown";
            float nearestThreat3d = float.PositiveInfinity;
            string nearestThreatKind = "none";
            int qualifiedThreats = 0;
            // Same qualified Awareness source as the submission observer; no world scan or LOS claim.
            foreach (var threat in VanguardCombatAwarenessBridge.GetFreshQualifiedTacticalCoordinationThreats(now))
            {
                if (!string.Equals(threat.OwnerProfileId, runtime.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                    || threat.ExpiresAtUtc <= now)
                {
                    continue;
                }
                qualifiedThreats++;
                float distance = Vector3.Distance(threat.RuntimePosition, current);
                if (distance < nearestThreat3d)
                {
                    nearestThreat3d = distance;
                    nearestThreatKind = Clean(threat.ContactKind);
                }
            }
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"SAIN_PHYSICAL_PATH_TICK_SHADOW utc={now:O}; botProfile={Clean(runtime.BotProfileId)}; operator={Clean(runtime.OperatorId)}; owner={Clean(runtime.OwnerProfileId)}; incarnation={runtime.RuntimeIncarnation}; role={Clean(runtime.TacticalRole)}; pathId={pathId}; pathStartedMono={Number(started)}; pathStart={Position(pathStart)}; pathDestination={Position(destination)}; pathLength={length}; cornerIndex={corner}; pathStatus={status}; pathTicked=true; pathSprintRequested={requestedSprint}; sprintUrgency={sprintUrgency}; cancelRequested={cancelRequested}; worldPosition={Position(current)}; firstSample={Bool(isFirstSample)}; measuredDeltaMeters={(isFirstSample || discontinuity ? "unknown" : Number(sincePreviousMeters))}; observedAccumulatedMeters={Number(gate.ObservedMeters)}; discontinuityExcluded={Bool(discontinuity)}; qualifiedThreatCount={qualifiedThreats}; nearestQualifiedThreat3d={Number(nearestThreat3d)}; nearestThreatContactKind={nearestThreatKind}; snapshotFresh={Bool(freshSnapshot)}; snapshotAgeSeconds={snapshotAge}; sainAction={Clean(action)}; primary={Clean(primary)}; snapshotMovementOwner={Clean(movementOwner)}; activeOwnerReceipt=UNVERIFIED_SNAPSHOT_ONLY; survivalUrgency=UNVERIFIED_SPRINT_IS_NOT_SURVIVAL_PROOF; alternativeRouteProof=NOT_EVALUATED; outcome=POSITION_DURING_ACTIVE_PATH_NOT_EXCLUSIVE_WRITER_PROOF; mutation=false; tag={StatusTag}");
        }
        catch (Exception exception)
        {
            WarnOnce("physical", exception);
        }
    }

    // SAIN's private TryUpdatePath returns true on reuse of an existing path; it does not recalculate a safe route.
    // This callback is deliberately post-return and never tries to substitute a path.
    internal static void ObservePathReuse(object? sainMover, Vector3 requestedPoint, bool reused)
    {
        if (!reused)
        {
            return;
        }
        try
        {
            if (!TryResolveOperator(sainMover, out VanguardRaidOperatorRuntimeRecord runtime))
            {
                return;
            }
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string action = ReadSainAction(runtime.BotProfileId, now);
            if (!ShouldEmit(EmissionIdentity(runtime), "reuse", requestedPoint, action, now,
                    MovementIntentLogSeconds, SameDestinationLogSeconds))
            {
                return;
            }
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"SAIN_PATH_REUSE_SHADOW botProfile={Clean(runtime.BotProfileId)}; owner={Clean(runtime.OwnerProfileId)}; sainAction={Clean(action)}; requestedPoint={Position(requestedPoint)}; reused=true; recalculated=false; actualPathSafety=UNVERIFIED; physicalMutation=false; tag={StatusTag}");
        }
        catch (Exception exception)
        {
            WarnOnce("reuse", exception);
        }
    }

    // The call to DogFightMove may be defensive (backup or stop) even with aggressive=true.
    // Prefix means intent is observed, not evidence of a completed maneuver.
    internal static void ObserveDogFightIntent(object? sainDogFight, bool aggressive, object? enemy)
    {
        try
        {
            if (!TryResolveOperator(sainDogFight, out VanguardRaidOperatorRuntimeRecord runtime)
                || runtime.BotOwner == null)
            {
                return;
            }
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Vector3 position = runtime.BotOwner.Position;
            string action = ReadSainAction(runtime.BotProfileId, now);
            string intent = aggressive ? "aggressive_requested" : "defensive_requested";
            if (!ShouldEmit(EmissionIdentity(runtime), "dogfight_" + intent, position, action, now,
                    MovementIntentLogSeconds, CoverFallbackLogSeconds))
            {
                return;
            }
            object? los = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "InLineOfSight");
            object? visible = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "IsVisible");
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"SAIN_DOGFIGHT_INTENT_SHADOW botProfile={Clean(runtime.BotProfileId)}; owner={Clean(runtime.OwnerProfileId)}; sainAction={Clean(action)}; intent={intent}; enemyPresent={Bool(enemy != null)}; enemyLineOfSight={MaybeBool(los)}; enemyVisible={MaybeBool(visible)}; movementOutcome=UNVERIFIED_PREFIX; survivalFallback=UNKNOWN; mutation=false; tag={StatusTag}");
        }
        catch (Exception exception)
        {
            WarnOnce("dogfight", exception);
        }
    }

    private static string ReadSainAction(string profileId, DateTimeOffset now) =>
        VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(profileId, out OperatorDecisionSnapshot snapshot)
        && VanguardOperatorDecisionSnapshotService.IsFresh(snapshot, now, out _)
            ? snapshot.Sain.CurrentAction : "snapshot_unavailable";

    internal static void ObserveCoverSelection(object? sainCover, object? selectedCover)
    {
        try
        {
            if (!TryResolveOperator(sainCover, out VanguardRaidOperatorRuntimeRecord runtime))
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            BotOwner? liveBotOwner = runtime.BotOwner;
            if (liveBotOwner == null)
            {
                return;
            }

            Vector3 position = liveBotOwner.Position;
            string outcome = selectedCover == null ? "NO_COVER_SELECTED" : "COVER_SELECTED";
            if (!ShouldEmit(EmissionIdentity(runtime), "cover_" + outcome, position, outcome, now,
                    CoverFallbackLogSeconds, CoverFallbackLogSeconds))
            {
                return;
            }

            object? points = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainCover, "CoverPoints");
            int candidateCount = points is ICollection collection ? collection.Count : -1;
            object? filtered = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainCover, "_coverPoints");
            int filteredCount = filtered is ICollection filteredCollection ? filteredCollection.Count : -1;
            object? selectedPosition = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(selectedCover, "Position");
            string coverPosition = selectedPosition is Vector3 point ? Position(point) : "unknown";
            string coverReason = selectedCover != null ? "ACCEPTED_MOVE_REQUEST"
                : candidateCount == 0 ? "NO_FINDER_POINTS"
                : filteredCount == 0 ? "NO_USABLE_FILTERED_POINTS"
                : filteredCount > 0 ? "NO_ACCEPTED_MOVE_REQUEST"
                : "UNRESOLVED";
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"SAIN_COVER_SELECTION_SHADOW botProfile={Clean(runtime.BotProfileId)}; operator={Clean(runtime.OperatorId)}; owner={Clean(runtime.OwnerProfileId)}; outcome={outcome}; coverFinderPointCount={candidateCount}; filteredCandidateCount={filteredCount}; coverResultReason={coverReason}; selectedPosition={coverPosition}; selectedMayHaveSubmittedMove={Bool(selectedCover != null)}; nextFallbackPossible={(selectedCover == null ? "DogFightMove_aggressive_in_UpdateCover" : "none_from_FindCoverPoint")}; candidateSafetyQualified=false; mutation=false; tag={StatusTag}");
        }
        catch (Exception exception)
        {
            WarnOnce("cover", exception);
        }
    }

    private static bool TryResolveOperator(object? sainSource, out VanguardRaidOperatorRuntimeRecord runtime)
    {
        runtime = null!;
        if (!VanguardFikaCompat.IsRaidAuthority || sainSource == null)
        {
            return false;
        }
        object? botOwnerObject = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainSource, "BotOwner")
            ?? VanguardOperatorRuntimeAuditReflection.GetDeep(sainSource, "Bot", "BotOwner");
        if (botOwnerObject is not BotOwner botOwner || botOwner.IsDead || string.IsNullOrWhiteSpace(botOwner.ProfileId))
        {
            return false;
        }
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botOwner.ProfileId, out var present)
            || !ReferenceEquals(present.BotOwner, botOwner))
        {
            return false;
        }
        runtime = present;
        return true;
    }

    private static string EmissionIdentity(VanguardRaidOperatorRuntimeRecord runtime) =>
        runtime.RaidSessionId + "|" + runtime.BotProfileId + "|" + runtime.RuntimeIncarnation.ToString(CultureInfo.InvariantCulture);

    private static bool ShouldEmit(string profileId, string kind, Vector3 destination, string action,
        DateTimeOffset now, double minimumInterval, double sameDestinationInterval)
    {
        lock (Sync)
        {
            if (!ByProfile.TryGetValue(profileId, out EmissionState? state))
            {
                // Bounded log state; no runtime BotOwner is retained across raids.
                if (ByProfile.Count >= 64)
                {
                    ByProfile.Clear();
                }
                state = new EmissionState();
                ByProfile[profileId] = state;
            }
            bool physical = kind == "physical";
            bool auxiliary = kind == "reuse" || kind.StartsWith("dogfight_", StringComparison.Ordinal);
            int auxiliaryLimit = kind == "reuse" ? MaxPathReuseEmissionsPerProfile : MaxDogFightEmissionsPerProfile;
            if (physical ? state.PhysicalEmissions >= MaxPhysicalSamplesPerProfile
                : auxiliary ? (kind == "reuse" ? state.PathReuseEmissions : state.DogFightEmissions) >= auxiliaryLimit
                : state.Emissions >= MaxEmissionsPerProfile)
            {
                return false;
            }
            if (state.LastByKind.TryGetValue(kind, out LastEmission? previous))
            {
                double elapsed = (now - previous.At).TotalSeconds;
                if (elapsed < minimumInterval || (elapsed < sameDestinationInterval
                    && (previous.Destination - destination).sqrMagnitude <= 4f
                    && string.Equals(previous.Action, action, StringComparison.Ordinal)))
                {
                    return false;
                }
            }
            state.LastByKind[kind] = new LastEmission(now, destination, action);
            if (physical)
            {
                state.PhysicalEmissions++;
            }
            else if (kind == "reuse")
            {
                state.PathReuseEmissions++;
            }
            else if (auxiliary)
            {
                state.DogFightEmissions++;
            }
            else
            {
                state.Emissions++;
            }
            return true;
        }
    }

    // S3a: geometry is derived only from SAIN's submitted path corners. No NavMesh query, no mutation.
    // Reject malformed or zero-length routes; never reinterpret their geometry as a safety verdict.
    private static bool TryFindClosestSubmittedRoutePoint(Vector3[] corners, Vector3 threatPosition,
        out Vector3 nearestPoint, out float nearestArc, out float totalLength,
        out float verticalSpan, out float maximumCornerVerticalStep)
    {
        nearestPoint = Vector3.zero;
        nearestArc = 0f;
        totalLength = 0f;
        verticalSpan = float.NaN;
        maximumCornerVerticalStep = float.NaN;
        if (corners.Length < 2 || !IsFinite(threatPosition))
        {
            return false;
        }

        float lowY = float.PositiveInfinity;
        float highY = float.NegativeInfinity;
        float maxStep = 0f;
        float bestDistanceSquared = float.PositiveInfinity;
        bool found = false;
        for (int i = 0; i < corners.Length; i++)
        {
            if (!IsFinite(corners[i]))
            {
                return false;
            }
            lowY = Mathf.Min(lowY, corners[i].y);
            highY = Mathf.Max(highY, corners[i].y);
            if (i == 0)
            {
                continue;
            }

            Vector3 start = corners[i - 1];
            Vector3 segment = corners[i] - start;
            float lengthSquared = segment.sqrMagnitude;
            float length = Mathf.Sqrt(lengthSquared);
            maxStep = Mathf.Max(maxStep, Mathf.Abs(segment.y));
            if (length <= 0.001f)
            {
                continue;
            }
            float t = Mathf.Clamp01(Vector3.Dot(threatPosition - start, segment) / lengthSquared);
            Vector3 candidate = start + t * segment;
            float squared = (candidate - threatPosition).sqrMagnitude;
            if (squared < bestDistanceSquared)
            {
                bestDistanceSquared = squared;
                nearestPoint = candidate;
                nearestArc = totalLength + t * length;
                found = true;
            }
            totalLength += length;
        }
        verticalSpan = highY - lowY;
        maximumCornerVerticalStep = maxStep;
        return found && totalLength > 0.001f;
    }

    private static bool TryPointOnSubmittedRoute(Vector3[] corners, float arc, out Vector3 point)
    {
        point = Vector3.zero;
        if (corners.Length < 2 || float.IsNaN(arc) || float.IsInfinity(arc) || arc < 0f)
        {
            return false;
        }
        float traversed = 0f;
        for (int i = 1; i < corners.Length; i++)
        {
            Vector3 start = corners[i - 1];
            Vector3 end = corners[i];
            if (!IsFinite(start) || !IsFinite(end))
            {
                return false;
            }
            float length = Vector3.Distance(start, end);
            if (length <= 0.001f)
            {
                continue;
            }
            if (arc <= traversed + length)
            {
                point = Vector3.Lerp(start, end, Mathf.Clamp01((arc - traversed) / length));
                return true;
            }
            traversed += length;
        }
        return false;
    }

    private static void SampleLocalSegmentPoint(string ownerProfileId, Vector3 point,
        VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation threat,
        ref int sharedLinecasts, ref int attempted, ref int clearCount,
        ref int obscuredOrUnknownCount, ref int unavailableCount, out string status)
    {
        attempted++;
        bool completed = ThreatExposureObservationService.TryObserveExistingPoint(
            ownerProfileId, point, threat.RuntimePosition, threat.RuntimeLookDirection,
            ref sharedLinecasts, out bool available, out bool clear, out _, out _, out _);
        if (!completed)
        {
            unavailableCount++;
            status = "UNRESOLVED_BUDGET";
        }
        else if (!available)
        {
            unavailableCount++;
            status = "UNRESOLVED_GEOMETRY";
        }
        else if (clear)
        {
            clearCount++;
            status = "CLEAR_STATIC_LINECAST";
        }
        else
        {
            // The shared primitive intentionally merges static occlusion and internal linecast failure.
            // Do not promote this observation to a cover/LOS or safe-route decision.
            obscuredOrUnknownCount++;
            status = "OCCLUDED_OR_UNRESOLVED";
        }
    }

    private static bool IsFinite(Vector3 point) =>
        !float.IsNaN(point.x) && !float.IsInfinity(point.x)
        && !float.IsNaN(point.y) && !float.IsInfinity(point.y)
        && !float.IsNaN(point.z) && !float.IsInfinity(point.z);

    private static float MinimumThreeDimensionalDistanceToPath(Vector3 target, Vector3[]? corners, Vector3 start, Vector3 destination)
    {
        if (corners == null || corners.Length < 2)
        {
            return SegmentThreeDimensionalDistance(target, start, destination);
        }
        float minimum = float.PositiveInfinity;
        for (int i = 1; i < corners.Length; i++)
        {
            minimum = Mathf.Min(minimum, SegmentThreeDimensionalDistance(target, corners[i - 1], corners[i]));
        }
        return minimum;
    }

    private static float SegmentThreeDimensionalDistance(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 segment = b - a;
        float lengthSquared = segment.sqrMagnitude;
        float t = lengthSquared > 0.0001f ? Mathf.Clamp01(Vector3.Dot(p - a, segment) / lengthSquared) : 0f;
        return Vector3.Distance(p, a + segment * t);
    }

    private static float MinimumHorizontalDistanceToPath(Vector3 target, Vector3[]? corners, Vector3 start, Vector3 destination)
    {
        if (corners == null || corners.Length < 2)
        {
            return SegmentHorizontalDistance(target, start, destination);
        }
        float minimum = float.PositiveInfinity;
        for (int i = 1; i < corners.Length; i++)
        {
            minimum = Mathf.Min(minimum, SegmentHorizontalDistance(target, corners[i - 1], corners[i]));
        }
        return minimum;
    }

    private static float SegmentHorizontalDistance(Vector3 p, Vector3 a, Vector3 b)
    {
        float dx = b.x - a.x;
        float dz = b.z - a.z;
        float lengthSquared = dx * dx + dz * dz;
        float t = lengthSquared > 0.0001f ? Mathf.Clamp01(((p.x - a.x) * dx + (p.z - a.z) * dz) / lengthSquared) : 0f;
        float x = a.x + t * dx - p.x;
        float z = a.z + t * dz - p.z;
        return Mathf.Sqrt(x * x + z * z);
    }

    private static string Position(Vector3 value) => Number(value.x) + "," + Number(value.y) + "," + Number(value.z);
    private static string Number(float value) => float.IsInfinity(value) || float.IsNaN(value)
        ? "unknown" : value.ToString("0.0", CultureInfo.InvariantCulture);
    private static string Bool(bool value) => value ? "true" : "false";
    private static string MaybeBool(object? value) => value is bool flag ? Bool(flag) : "unknown";
    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? "none"
        : value.Replace(';', '_').Replace('\n', '_').Replace('\r', '_');

    private static readonly HashSet<string> LoggedFailures = new(StringComparer.Ordinal);
    private static void WarnOnce(string kind, Exception exception)
    {
        lock (Sync)
        {
            if (!LoggedFailures.Add(kind + ":" + exception.GetType().Name))
            {
                return;
            }
        }
        VanguardClientDiagnosticsLog.Warning(StatusTag,
            $"SAIN_ROUTE_SHADOW_FAILURE hook={kind}; exception={exception.GetType().Name}; failOpen=true; mutation=false; tag={StatusTag}");
    }

    private sealed class EmissionState
    {
        public int Emissions;
        public int DogFightEmissions;
        public int PathReuseEmissions;
        public int PhysicalEmissions;
        public Dictionary<string, LastEmission> LastByKind { get; } = new(StringComparer.Ordinal);
    }

    private sealed class PhysicalPathProbeGate
    {
        public float NextSampleAt;
        public string RunIdentity = string.Empty;
        public Vector3 LastPosition;
        public float ObservedMeters;
    }

    private sealed class LastEmission
    {
        public LastEmission(DateTimeOffset at, Vector3 destination, string action)
        {
            At = at;
            Destination = destination;
            Action = action;
        }
        public DateTimeOffset At { get; }
        public Vector3 Destination { get; }
        public string Action { get; }
    }
}
#endif
