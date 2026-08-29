#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Comfort.Common;
using EFT;
using Newtonsoft.Json;
using UnityEngine;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Combat;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Movement;
using Vanguard.Client.Runtime.Movement.Brain;

// Responsibility: Builds the temporary Headless-side preview of player-authored tactical slots so the author can see placement before committing it.
// Flow: Player-authored preview data is read from the live transport, validated against raid/world constraints, projected into transient preview objects, and removed when the preview expires or the raid resets.
// Authority boundary: The Headless runtime may validate and render the preview, but persisted Tactical Authoring state remains owned by the authoring/store path and EFT/SAIN navigation data is never rewritten here.
// Invariant: Preview state is disposable and non-authoritative: it must never survive its lifecycle window or silently turn an uncommitted edit into gameplay state.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

/// <summary>
/// Headless-authoritative, transient placement preview for player-authored Vanguard slots.
/// It never changes the persisted authoring flags and never mutates EFT/SAIN cover graphs.
/// </summary>
internal static class VanguardTacticalAuthoringHeadlessPreviewService
{
    public const string StatusTag = "VANGUARD_TACTICAL_AUTHORING_HEADLESS_PREVIEW_STATUS";
    public const string RequestKind = "TacticalAuthoringLivePreview";
    private static readonly TimeSpan SessionFreshness = TimeSpan.FromSeconds(3.25d);
    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(0.30d);
    private static readonly TimeSpan CommandLifetime = TimeSpan.FromSeconds(2.0d);
    private static readonly TimeSpan WatchApplyInterval = TimeSpan.FromSeconds(0.75d);
    private const float ArrivalRadiusMeters = 1.20f;
    private const float HoldEntryRadiusMeters = ArrivalRadiusMeters + 0.25f;
    private const float HoldExitRadiusMeters = ArrivalRadiusMeters + 1.05f;
    private const float ProjectionRadiusMeters = 3.0f;
    private const float WatchApplyRadiusMeters = HoldExitRadiusMeters;
    private const float WatchMovementYieldSpeedMetersPerSecond = 0.35f;
    private const float WatchPointDistanceMeters = 8.0f;

    private static readonly Dictionary<string, AuthorState> AuthorsByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, OwnedPreview> OwnedByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, VanguardTacticalAuthoringHeadlessPreviewResult> ResultsByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> LastResultSignatureByOwner = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastWatchApplyAtByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, WatchTelemetryState> WatchTelemetryByBot = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset nextEvaluationAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    public static void ApplyAuthorSnapshots(IReadOnlyList<VanguardTacticalAuthoringLiveAuthorSnapshotDto>? snapshots, DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsActualHeadlessProcess || snapshots == null)
        {
            return;
        }

        var seenOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            if (snapshot == null || !snapshot.Active || string.IsNullOrWhiteSpace(snapshot.OwnerProfileId)
                || string.IsNullOrWhiteSpace(snapshot.LiveSessionId) || string.IsNullOrWhiteSpace(snapshot.MapId))
            {
                continue;
            }

            var owner = snapshot.OwnerProfileId.Trim();
            seenOwners.Add(owner);
            if (!AuthorsByOwner.TryGetValue(owner, out var state)
                || !string.Equals(state.LiveSessionId, snapshot.LiveSessionId, StringComparison.Ordinal)
                || !string.Equals(state.MapId, snapshot.MapId, StringComparison.OrdinalIgnoreCase))
            {
                ReleaseOwner(owner, now, "author_session_replaced");
                state = new AuthorState
                {
                    OwnerProfileId = owner,
                    LiveSessionId = snapshot.LiveSessionId.Trim(),
                    MapId = snapshot.MapId.Trim(),
                    Revision = -1
                };
                AuthorsByOwner[owner] = state;
            }

            state.ReceivedAtUtc = now;
            state.SelectedZoneId = snapshot.SelectedZoneId?.Trim() ?? string.Empty;
            if (snapshot.Revision != state.Revision || state.Map == null)
            {
                if (string.IsNullOrWhiteSpace(snapshot.MapJson))
                {
                    state.ParseError = "map_json_missing_for_revision";
                }
                else
                {
                    try
                    {
                        var parsed = JsonConvert.DeserializeObject<VanguardTacticalAuthoringMapFile>(snapshot.MapJson);
                        if (parsed == null || parsed.RuntimeConsumptionEnabled
                            || !string.Equals(parsed.MapId, state.MapId, StringComparison.OrdinalIgnoreCase))
                        {
                            state.ParseError = "map_envelope_rejected";
                        }
                        else
                        {
                            state.Map = parsed;
                            state.Revision = snapshot.Revision;
                            state.ParseError = string.Empty;
                        }
                    }
                    catch (Exception exception)
                    {
                        state.ParseError = "map_parse_failed:" + exception.GetType().Name;
                    }
                }
            }
        }

        foreach (var owner in AuthorsByOwner.Keys.Where(owner => !seenOwners.Contains(owner)).ToArray())
        {
            // A successful headless exchange returns the complete active-author set for owners
            // known by this authority. Absence is therefore an authoritative close/removal, not
            // a reason to keep steering for several extra seconds. Network failure is handled
            // separately by SessionFreshness because ApplyAuthorSnapshots is not called then.
            ReleaseOwner(owner, now, "author_absent_from_authoritative_relay");
            AuthorsByOwner.Remove(owner);
            ResultsByOwner.Remove(owner);
        }
    }

    public static void Tick(DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsActualHeadlessProcess)
        {
            return;
        }

        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TACTICAL_AUTHORING_HEADLESS_PREVIEW_BOOT transient=true; persistedRuntimeConsumption=false; movementBackend=BigBrain; navAuthority=headless; actualHeadlessProcess={VanguardFikaCompat.IsActualHeadlessProcess}; raidHostedByHeadless={VanguardFikaCompat.IsRaidHostedByHeadless}; combatMedicalGrenadePreempt=true; build={VanguardBuildVersion.BuildLabel}");
        }

        CleanupPreemptedOrStale(now);
        ApplyOwnedWatchOrientations(now);
        if (now < nextEvaluationAtUtc)
        {
            return;
        }
        nextEvaluationAtUtc = now + EvaluationInterval;

        foreach (var state in AuthorsByOwner.Values.ToArray())
        {
            if (now - state.ReceivedAtUtc > SessionFreshness)
            {
                ReleaseOwner(state.OwnerProfileId, now, "author_session_stale");
                ResultsByOwner.Remove(state.OwnerProfileId);
                AuthorsByOwner.Remove(state.OwnerProfileId);
                continue;
            }

            EvaluateOwner(state, now);
        }
    }

    public static List<VanguardTacticalAuthoringLiveHeadlessResultDto> BuildRelayResults()
    {
        if (!VanguardFikaCompat.IsActualHeadlessProcess)
        {
            return new List<VanguardTacticalAuthoringLiveHeadlessResultDto>();
        }

        return ResultsByOwner.Values.Select(result => new VanguardTacticalAuthoringLiveHeadlessResultDto
        {
            OwnerProfileId = result.OwnerProfileId,
            LiveSessionId = result.LiveSessionId,
            MapId = result.MapId,
            AuthorRevision = result.AuthorRevision,
            ResultJson = JsonConvert.SerializeObject(result),
            UpdatedAtUtc = result.GeneratedAtUtc,
            HeadlessBuild = VanguardBuildVersion.BuildLabel
        }).ToList();
    }

    internal static bool TryGetLootExcursionContext(
        string botProfileId,
        DateTimeOffset now,
        out VanguardTacticalAuthoringLootExcursionContext context,
        out string reason)
    {
        context = default;
        reason = "none";
        if (!VanguardFikaCompat.IsActualHeadlessProcess)
        {
            reason = "not_actual_headless_process";
            return false;
        }

        if (!OwnedByBot.TryGetValue(botProfileId, out var owned))
        {
            reason = "authoring_assignment_missing";
            return false;
        }

        if (!WatchTelemetryByBot.TryGetValue(botProfileId, out var telemetry)
            || !string.Equals(telemetry.OwnerProfileId, owned.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(telemetry.LiveSessionId, owned.LiveSessionId, StringComparison.Ordinal)
            || !string.Equals(telemetry.SlotId, owned.SlotId, StringComparison.Ordinal)
            || !telemetry.InHold)
        {
            reason = "authoring_assignment_not_in_stationary_hold";
            return false;
        }

        if (!AuthorsByOwner.TryGetValue(owned.OwnerProfileId, out var author)
            || author.Map == null
            || now - author.ReceivedAtUtc > SessionFreshness
            || !string.Equals(author.LiveSessionId, owned.LiveSessionId, StringComparison.Ordinal)
            || !string.Equals(author.SelectedZoneId, owned.ZoneId, StringComparison.Ordinal))
        {
            reason = "authoring_session_or_zone_not_current";
            return false;
        }

        if (!VanguardMainIntentScheduler.TryGetActivePrimaryWindow(botProfileId, now, out var activeKind, out _, out _, out _)
            || !string.Equals(activeKind, VanguardPrimaryExecutionWindowKinds.AuthoringPreviewMovement, StringComparison.OrdinalIgnoreCase))
        {
            reason = "authoring_primary_window_not_active";
            return false;
        }

        if (!VanguardReturnMovementCommandStore.TryGetActive(botProfileId, now, out var command)
            || !string.Equals(command.RequestKind, RequestKind, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(command.LeaseId, owned.WindowId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "authoring_stationary_hold_command_not_owned";
            return false;
        }

        var zone = author.Map.Zones.FirstOrDefault(item => string.Equals(item.ZoneId, owned.ZoneId, StringComparison.Ordinal));
        if (zone == null)
        {
            reason = "authoring_assigned_zone_missing";
            return false;
        }

        context = new VanguardTacticalAuthoringLootExcursionContext(
            owned.OwnerProfileId,
            owned.LiveSessionId,
            owned.ZoneId,
            owned.SlotId,
            zone.ZoneAnchor.ToVector3(),
            Mathf.Max(0.5f, zone.ZoneRadius),
            zone.MinY,
            zone.MaxY);
        reason = "authoring_stationary_hold_excursion_eligible:" + context.Summary;
        return true;
    }

    // Historical corpse wrapper retained so the validated call sites and diagnostics remain stable.
    // WorldContainer loot consumes the same bounded authored-zone excursion authority through the
    // target-neutral query above; this does not widen combat/medical or cross-zone movement authority.
    internal static bool TryGetCorpseLootExcursionContext(
        string botProfileId,
        DateTimeOffset now,
        out VanguardTacticalAuthoringLootExcursionContext context,
        out string reason)
        => TryGetLootExcursionContext(botProfileId, now, out context, out reason);

    public static void Reset(string reason)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var owner in AuthorsByOwner.Keys.ToArray())
        {
            ReleaseOwner(owner, now, "reset:" + reason);
        }
        AuthorsByOwner.Clear();
        OwnedByBot.Clear();
        ResultsByOwner.Clear();
        LastResultSignatureByOwner.Clear();
        LastWatchApplyAtByBot.Clear();
        WatchTelemetryByBot.Clear();
        nextEvaluationAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
    }

    private static void EvaluateOwner(AuthorState state, DateTimeOffset now)
    {
        var result = NewResult(state, now);
        if (!string.IsNullOrWhiteSpace(state.ParseError) || state.Map == null)
        {
            result.State = "MAP_REJECTED";
            result.Reason = string.IsNullOrWhiteSpace(state.ParseError) ? "map_missing" : state.ParseError;
            ReleaseOwner(state.OwnerProfileId, now, result.Reason);
            PublishResult(state.OwnerProfileId, result);
            return;
        }

        if (!IsCurrentMap(state.MapId))
        {
            result.State = "MAP_MISMATCH";
            result.Reason = "headless_current_map_differs";
            ReleaseOwner(state.OwnerProfileId, now, result.Reason);
            PublishResult(state.OwnerProfileId, result);
            return;
        }

        var zone = state.Map.Zones.FirstOrDefault(item => string.Equals(item.ZoneId, state.SelectedZoneId, StringComparison.Ordinal));
        if (zone == null)
        {
            result.State = "ZONE_MISSING";
            result.Reason = "selected_zone_not_found";
            ReleaseOwner(state.OwnerProfileId, now, result.Reason);
            PublishResult(state.OwnerProfileId, result);
            return;
        }

        var operators = VanguardRaidOperatorRuntimeRegistry.GetOperatorsForOwner(state.OwnerProfileId)
            .Where(record => record.BotOwner != null && !record.BotOwner.IsDead)
            .OrderBy(record => record.OperatorId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.OperatorCount = operators.Count;
        if (operators.Count == 0)
        {
            result.State = "WAIT_OPERATORS";
            result.Reason = "no_bound_alive_operators_for_owner";
            ReleaseOwner(state.OwnerProfileId, now, result.Reason);
            PublishResult(state.OwnerProfileId, result);
            return;
        }

        var ownerAnchor = VanguardOwnerAnchorResolver.Resolve(state.OwnerProfileId, now);

        // Validation and assignment are deliberately separate. Every authoring-valid slot is
        // headless-validated even when there are more authored slots than Operators. Assignment
        // capacity must never make a geometrically/path-valid slot look invalid to the author.
        // OrderByDescending is stable, therefore equal-priority slots retain authoring/JSON order.
        var candidateSlots = zone.Slots
            .Where(slot => slot.Enabled && slot.AuthoringValid)
            .OrderByDescending(slot => slot.Priority)
            .ToList();
        result.CandidateSlotCount = candidateSlots.Count;

        var validated = new List<SlotEvaluation>();
        foreach (var slot in candidateSlots)
        {
            var evaluation = ValidateSlot(slot, operators.Count, operators, ownerAnchor);
            result.Slots.Add(evaluation.Result);
            if (!string.Equals(evaluation.Result.State, "HEADLESS_OK", StringComparison.Ordinal))
            {
                continue;
            }

            result.HeadlessValidSlotCount++;
            validated.Add(evaluation);
        }

        // Mutual-exclusion groups are an assignment rule, not a validation rule. Alternative
        // slots in one group are therefore all validated and visible to the author, while only
        // the first/highest-priority member can receive an Operator during this preview cycle.
        var assignmentCandidates = ApplyMutualExclusion(validated);
        var availableBotIds = new HashSet<string>(operators.Select(record => record.BotProfileId), StringComparer.OrdinalIgnoreCase);
        var assignedBotIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var evaluation in assignmentCandidates)
        {
            OperatorPath? reservedPath = null;
            bool hasProtectedLootReservation = TryGetProtectedCorpseLootStickyAssignment(
                state,
                evaluation.Slot,
                availableBotIds,
                now,
                out var reservedRecord);
            if (hasProtectedLootReservation
                && !evaluation.PathsByBotProfileId.TryGetValue(reservedRecord.BotProfileId, out reservedPath))
            {
                // A looting Operator keeps its authored slot reserved even if its temporary corpse position
                // makes the return-path probe unavailable for one preview cycle. Do not
                // hand the slot to a squadmate and do not issue an unvalidated return command.
                availableBotIds.Remove(reservedRecord.BotProfileId);
                assignedBotIds.Add(reservedRecord.BotProfileId);
                evaluation.Result.AssignedOperatorId = reservedRecord.OperatorId;
                evaluation.Result.AssignedBotProfileId = reservedRecord.BotProfileId;
                evaluation.Result.AssignedCallsign = reservedRecord.BotNickname;
                evaluation.Result.MovementState = "reserved_yield:corpse_loot";
                result.AssignedOperatorCount++;
                continue;
            }

            var assignedPath = reservedPath ?? SelectOperatorForSlot(state, evaluation, availableBotIds);
            if (assignedPath == null)
            {
                evaluation.Result.MovementState = "validated_unassigned:no_operator_capacity";
                continue;
            }

            availableBotIds.Remove(assignedPath.Record.BotProfileId);
            assignedBotIds.Add(assignedPath.Record.BotProfileId);
            evaluation.Result.AssignedOperatorId = assignedPath.Record.OperatorId;
            evaluation.Result.AssignedBotProfileId = assignedPath.Record.BotProfileId;
            evaluation.Result.AssignedCallsign = assignedPath.Record.BotNickname;
            evaluation.Result.BestPathDistanceMeters = assignedPath.DistanceMeters;
            DriveAssignment(
                state,
                evaluation.Slot,
                assignedPath.Record,
                evaluation.Anchor,
                assignedPath.DistanceMeters,
                assignedPath.PathSummary,
                now,
                evaluation.Result);
            result.AssignedOperatorCount++;
        }

        foreach (var record in operators)
        {
            if (!assignedBotIds.Contains(record.BotProfileId))
            {
                if (OwnedByBot.TryGetValue(record.BotProfileId, out var owned)
                    && IsProtectedCorpseLootAssignment(state, owned, record.BotProfileId, now))
                {
                    continue;
                }
                ReleaseOperator(record.BotProfileId, now, "not_assigned_in_current_preview");
            }
        }

        result.State = result.HeadlessValidSlotCount > 0 ? "ACTIVE" : "NO_VALID_SLOTS";
        result.Reason = $"zone={zone.ZoneId}; candidates={result.CandidateSlotCount}; valid={result.HeadlessValidSlotCount}; assigned={result.AssignedOperatorCount}";
        PublishResult(state.OwnerProfileId, result);
    }

    private static void PublishResult(string ownerProfileId, VanguardTacticalAuthoringHeadlessPreviewResult result)
    {
        ResultsByOwner[ownerProfileId] = result;
        var signature = $"{result.AuthorRevision}|{result.State}|{result.Reason}|{result.HeadlessValidSlotCount}|{result.AssignedOperatorCount}";
        if (LastResultSignatureByOwner.TryGetValue(ownerProfileId, out var previous)
            && string.Equals(previous, signature, StringComparison.Ordinal))
        {
            return;
        }

        LastResultSignatureByOwner[ownerProfileId] = signature;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TACTICAL_AUTHORING_HEADLESS_RESULT owner={ownerProfileId}; session={result.LiveSessionId}; revision={result.AuthorRevision}; state={result.State}; reason={result.Reason}; operators={result.OperatorCount}; candidates={result.CandidateSlotCount}; valid={result.HeadlessValidSlotCount}; assigned={result.AssignedOperatorCount}");
    }

    private static SlotEvaluation ValidateSlot(
        VanguardTacticalAuthoringSlot slot,
        int squadSize,
        IReadOnlyList<VanguardRaidOperatorRuntimeRecord> operators,
        VanguardOwnerAnchor ownerAnchor)
    {
        var evaluation = new SlotEvaluation(slot);
        var result = evaluation.Result;
        var anchor = slot.Position.ToVector3();

        if (slot.MinimumSquadSize > squadSize)
        {
            result.State = "SQUAD_TOO_SMALL";
            result.Reason = $"minimum={slot.MinimumSquadSize};actual={squadSize}";
            return evaluation;
        }

        if (!VanguardTacticalPlacementSolver.TryProjectRuntimeAnchor(slot.Position.ToVector3(), ProjectionRadiusMeters, out anchor))
        {
            result.State = "NAV_NO";
            result.Reason = "headless_navmesh_projection_failed";
            return evaluation;
        }
        evaluation.Anchor = anchor;
        result.ProjectedPosition = VanguardVector3Dto.FromVector3(anchor);

        if (!HasHeadlessStaticCapsuleClearance(anchor))
        {
            result.State = "CAPSULE_BLOCKED";
            result.Reason = "headless_static_capsule_blocked";
            return evaluation;
        }

        if (slot.MaximumOwnerDistance > 0.1f)
        {
            if (!ownerAnchor.Known)
            {
                result.State = "OWNER_UNKNOWN";
                result.Reason = "owner_anchor_unavailable_for_distance_constraint";
                return evaluation;
            }

            var ownerDistance = HorizontalDistance(ownerAnchor.Position, anchor);
            if (ownerDistance > slot.MaximumOwnerDistance)
            {
                result.State = "OWNER_TOO_FAR";
                result.Reason = $"distance={ownerDistance:0.0};max={slot.MaximumOwnerDistance:0.0};source={ownerAnchor.Source}";
                return evaluation;
            }
        }

        foreach (var candidate in operators)
        {
            if (candidate.BotOwner == null || !TryGetBotPosition(candidate.BotOwner, out var botPosition))
            {
                continue;
            }

            if (!VanguardTacticalPlacementSolver.TryCalculateRuntimePath(botPosition, anchor, out var distance, out var corners, out var status))
            {
                continue;
            }

            evaluation.PathsByBotProfileId[candidate.BotProfileId] = new OperatorPath(
                candidate,
                distance,
                status + ";corners=" + corners.ToString(CultureInfo.InvariantCulture));
        }

        if (evaluation.PathsByBotProfileId.Count == 0)
        {
            result.State = "NO_PATH";
            result.Reason = "no_owned_operator_has_complete_path";
            return evaluation;
        }

        var best = evaluation.PathsByBotProfileId.Values
            .OrderBy(path => path.DistanceMeters)
            .ThenBy(path => path.Record.OperatorId, StringComparer.OrdinalIgnoreCase)
            .First();
        result.State = "HEADLESS_OK";
        result.Reason = string.IsNullOrWhiteSpace(slot.RoleAffinity)
            ? "headless_nav_caps_path_valid"
            : "headless_nav_caps_path_valid;role_affinity_informational_live_preview";
        result.BestPathDistanceMeters = best.DistanceMeters;
        result.MovementState = "validated_waiting_assignment";
        return evaluation;
    }

    private static OperatorPath? SelectOperatorForSlot(
        AuthorState state,
        SlotEvaluation evaluation,
        HashSet<string> availableBotIds)
    {
        // Preserve a still-valid assignment whenever possible. This prevents Operators from
        // swapping slots merely because their path distances cross while they are already moving.
        var stickyBotProfileId = OwnedByBot
            .Where(pair => string.Equals(pair.Value.OwnerProfileId, state.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pair.Value.LiveSessionId, state.LiveSessionId, StringComparison.Ordinal)
                && string.Equals(pair.Value.SlotId, evaluation.Slot.SlotId, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(stickyBotProfileId)
            && availableBotIds.Contains(stickyBotProfileId)
            && evaluation.PathsByBotProfileId.TryGetValue(stickyBotProfileId, out var sticky))
        {
            return sticky;
        }

        return evaluation.PathsByBotProfileId.Values
            .Where(path => availableBotIds.Contains(path.Record.BotProfileId))
            .OrderBy(path => path.DistanceMeters)
            .ThenBy(path => path.Record.OperatorId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool TryGetProtectedCorpseLootStickyAssignment(
        AuthorState state,
        VanguardTacticalAuthoringSlot slot,
        HashSet<string> availableBotIds,
        DateTimeOffset now,
        out VanguardRaidOperatorRuntimeRecord record)
    {
        record = null!;
        var sticky = OwnedByBot.FirstOrDefault(pair =>
            string.Equals(pair.Value.OwnerProfileId, state.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.Value.LiveSessionId, state.LiveSessionId, StringComparison.Ordinal)
            && string.Equals(pair.Value.ZoneId, state.SelectedZoneId, StringComparison.Ordinal)
            && string.Equals(pair.Value.SlotId, slot.SlotId, StringComparison.Ordinal)
            && availableBotIds.Contains(pair.Key));
        if (string.IsNullOrWhiteSpace(sticky.Key)
            || !IsProtectedCorpseLootAssignment(state, sticky.Value, sticky.Key, now)
            || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(sticky.Key, out record)
            || record.BotOwner == null
            || record.BotOwner.IsDead)
        {
            record = null!;
            return false;
        }

        return true;
    }

    private static bool IsProtectedCorpseLootAssignment(
        AuthorState state,
        OwnedPreview owned,
        string botProfileId,
        DateTimeOffset now)
    {
        if (!IsOwnedAssignmentCurrent(state, owned))
        {
            return false;
        }

        bool hasPrimaryWindow = VanguardMainIntentScheduler.TryGetActivePrimaryWindow(
            botProfileId,
            now,
            out var kind,
            out _,
            out _,
            out _);
        if (hasPrimaryWindow
            && (string.Equals(kind, VanguardPrimaryExecutionWindowKinds.CorpseLoot, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, VanguardPrimaryExecutionWindowKinds.WorldContainerLoot, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // CorpseLoot can reach its terminal later in the frame than the authoring tick. Preserve
        // the sticky assignment across that one scheduler-empty handoff so the next evaluation
        // reopens AuthoringPreview for the same slot instead of redistributing the squad.
        return !hasPrimaryWindow
            && TryGetMatchingWatchTelemetry(botProfileId, owned, out var telemetry)
            && telemetry.Yielded
            && string.Equals(telemetry.LastYieldReason, "loot", StringComparison.Ordinal);
    }

    private static bool IsOwnedAssignmentCurrent(AuthorState state, OwnedPreview owned)
    {
        if (state.Map == null
            || !string.Equals(state.OwnerProfileId, owned.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(state.LiveSessionId, owned.LiveSessionId, StringComparison.Ordinal)
            || !string.Equals(state.SelectedZoneId, owned.ZoneId, StringComparison.Ordinal))
        {
            return false;
        }

        var zone = state.Map.Zones.FirstOrDefault(item => string.Equals(item.ZoneId, owned.ZoneId, StringComparison.Ordinal));
        return zone != null
            && zone.Slots.Any(slot => slot.Enabled
                && slot.AuthoringValid
                && string.Equals(slot.SlotId, owned.SlotId, StringComparison.Ordinal));
    }

    private static void DriveAssignment(
        AuthorState state,
        VanguardTacticalAuthoringSlot slot,
        VanguardRaidOperatorRuntimeRecord record,
        Vector3 anchor,
        float pathDistance,
        string pathSummary,
        DateTimeOffset now,
        VanguardTacticalAuthoringHeadlessSlotResult result)
    {
        if (record.BotOwner == null)
        {
            result.MovementState = "bot_owner_missing";
            return;
        }

        OwnedByBot.TryGetValue(record.BotProfileId, out var prior);
        if (prior != null
            && (!string.Equals(prior.LiveSessionId, state.LiveSessionId, StringComparison.Ordinal)
                || !string.Equals(prior.SlotId, slot.SlotId, StringComparison.Ordinal)))
        {
            ReleaseOperator(record.BotProfileId, now, "preview_assignment_changed");
            prior = null;
        }

        var watchTelemetry = GetOrResetWatchTelemetry(record.BotProfileId, state, slot, record.OperatorId);

        if (VanguardMainIntentScheduler.IsSainCombatExecutionProtected(record.BotProfileId, now, out var combatProtectedReason))
        {
            MarkWatchYield(record.BotProfileId, watchTelemetry, "combat");
            ReleaseOperator(record.BotProfileId, now, "combat_protected:" + combatProtectedReason, preserveWatchTelemetry: true);
            result.MovementState = "yield:combat_protected";
            return;
        }

        if (VanguardSainSquadCombatAuthority.TryGetCachedAuthority(record.BotProfileId, now, out _, out var squadCombatReason))
        {
            MarkWatchYield(record.BotProfileId, watchTelemetry, "sain");
            ReleaseOperator(record.BotProfileId, now, "sain_squad_combat:" + squadCombatReason, preserveWatchTelemetry: true);
            result.MovementState = "yield:sain_squad_combat";
            return;
        }

        if (!VanguardMainIntentScheduler.TryOpenOrRefreshAuthoringPreview(
            record.OperatorId,
            record.BotProfileId,
            state.LiveSessionId,
            slot.SlotId,
            now,
            out var windowId,
            out var openReason))
        {
            string yieldReason = MapPreviewPreemptionToWatchYield(openReason);
            MarkWatchYield(record.BotProfileId, watchTelemetry, yieldReason);
            if (prior != null && string.Equals(yieldReason, "loot", StringComparison.Ordinal))
            {
                // CorpseLoot may temporarily own execution while the authored assignment remains sticky.
                // Do not release OwnedByBot here: the preserved assignment is the canonical
                // return target once the bounded loot window reaches a terminal. The corpse movement
                // command has a different lease, so Tactical Authoring must neither clear nor replace it.
                result.MovementState = "yield:corpse_loot_excursion";
                return;
            }

            ReleaseOperator(record.BotProfileId, now, "preview_preempted:" + openReason, finishWindow: false, preserveWatchTelemetry: true);
            result.MovementState = "yield:" + openReason;
            return;
        }

        var watch = slot.WatchDirection.ToVector3();
        watch.y = 0f;
        var watchPoint = watch.sqrMagnitude > 0.01f
            ? anchor + watch.normalized * WatchPointDistanceMeters + Vector3.up * 1.1f
            : anchor + Vector3.forward * WatchPointDistanceMeters + Vector3.up * 1.1f;

        var owned = new OwnedPreview
        {
            OwnerProfileId = state.OwnerProfileId,
            OperatorId = record.OperatorId,
            LiveSessionId = state.LiveSessionId,
            ZoneId = state.SelectedZoneId,
            SlotId = slot.SlotId,
            WindowId = windowId,
            StartedAtUtc = prior?.StartedAtUtc ?? now,
            Anchor = anchor,
            WatchPoint = watchPoint,
            WatchDirection = watch.sqrMagnitude > 0.01f ? watch.normalized : Vector3.forward
        };
        OwnedByBot[record.BotProfileId] = owned;

        // Arrival is an authored stationary HOLD owned by the same BigBrain movement command, not a command
        // release. The movement bridge recognizes TacticalAuthoringLivePreview and
        // quiesces the physical mover while the command remains alive, preventing normal follow or
        // patrol from reclaiming the bot between evaluations. A small exit hysteresis prevents
        // positional noise from turning HOLD into repeated reapproach cycles. Combat/SAIN/medical/
        // grenade still preempt through the scheduler before this point.
        var hasPosition = TryGetBotPosition(record.BotOwner, out var position);
        var anchorDistance = hasPosition ? HorizontalDistance(position, anchor) : float.MaxValue;
        bool insideHoldEnvelope = hasPosition
            && anchorDistance <= (watchTelemetry.InHold ? HoldExitRadiusMeters : HoldEntryRadiusMeters);
        bool sprint = !insideHoldEnvelope && pathDistance > 12f;

        if (!EnsurePreviewMovementCommand(
            prior,
            windowId,
            record,
            anchor,
            pathDistance,
            pathSummary,
            now,
            sprint,
            out var movementState))
        {
            result.MovementState = movementState;
            return;
        }

        if (insideHoldEnvelope)
        {
            if (!watchTelemetry.InHold)
            {
                watchTelemetry.InHold = true;
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    $"TACTICAL_AUTHORING_SLOT_ARRIVED_HOLD_ENTER owner={state.OwnerProfileId}; operator={record.OperatorId}; bot={record.BotProfileId}; slot={slot.SlotId}; distance={anchorDistance:0.00}; watch=({owned.WatchDirection.x:0.00},{owned.WatchDirection.y:0.00},{owned.WatchDirection.z:0.00}); commandRetained=true; holdExitRadius={HoldExitRadiusMeters:0.00}");
            }

            result.MovementState = "arrived_stationary_hold";
            if (TryApplyOwnedWatchOrientation(record.BotProfileId, record.BotOwner, owned, now))
            {
                MarkWatchApplied(record.BotProfileId, owned);
            }
            return;
        }

        if (watchTelemetry.InHold && hasPosition)
        {
            watchTelemetry.InHold = false;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TACTICAL_AUTHORING_SLOT_REACQUIRE owner={state.OwnerProfileId}; operator={record.OperatorId}; bot={record.BotProfileId}; slot={slot.SlotId}; distance={anchorDistance:0.00}; holdExitRadius={HoldExitRadiusMeters:0.00}; movementState={movementState}");
        }
        result.MovementState = movementState;
    }

    private static bool EnsurePreviewMovementCommand(
        OwnedPreview? prior,
        string windowId,
        VanguardRaidOperatorRuntimeRecord record,
        Vector3 anchor,
        float pathDistance,
        string pathSummary,
        DateTimeOffset now,
        bool sprint,
        out string movementState)
    {
        movementState = "none";
        var expires = now + CommandLifetime;

        // Live Ctrl+P edits preserve slot/session identity. Retarget the exact same lease first; if
        // the command is merely absent (for example after a legitimate preemption), recreate it.
        if (prior != null && string.Equals(prior.WindowId, windowId, StringComparison.OrdinalIgnoreCase))
        {
            var retarget = VanguardReturnMovementCommandStore.TryRetargetActive(
                windowId,
                record.BotProfileId,
                anchor,
                ArrivalRadiusMeters,
                sprint,
                now,
                expires,
                pathSummary,
                pathDistance,
                "tactical_authoring_live_slot_update",
                0.50f,
                TimeSpan.FromSeconds(0.10d));
            if (retarget.Accepted)
            {
                movementState = retarget.Applied ? "retargeted" : "commanded";
                return true;
            }
            if (retarget.Outcome != VanguardMovementRetargetOutcome.RejectedMissingCommand)
            {
                movementState = "retarget_pending:" + retarget.Summary;
                return false;
            }
        }

        if (!VanguardReturnMovementCommandStore.Issue(
            windowId,
            record.OperatorId,
            record.BotProfileId,
            anchor,
            ArrivalRadiusMeters,
            sprint,
            now,
            expires,
            RequestKind,
            pathSummary,
            pathDistance,
            out var issueResult))
        {
            movementState = "command_rejected:" + issueResult;
            return false;
        }

        movementState = "commanded";
        return true;
    }

    private static void ApplyOwnedWatchOrientations(DateTimeOffset now)
    {
        foreach (var pair in OwnedByBot.ToArray())
        {
            if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(pair.Key, out var snapshot)
                || snapshot == null
                || !snapshot.Alive)
            {
                continue;
            }

            var yieldReason = GetWatchYieldReason(snapshot, pair.Value, now);
            if (yieldReason.Length > 0)
            {
                MarkWatchYield(pair.Key, pair.Value, yieldReason);
                continue;
            }

            if (LastWatchApplyAtByBot.TryGetValue(pair.Key, out var lastApply)
                && now - lastApply < WatchApplyInterval)
            {
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(pair.Key, out var runtime)
                || runtime.BotOwner == null
                || runtime.BotOwner.IsDead)
            {
                continue;
            }

            if (TryApplyOwnedWatchOrientation(pair.Key, runtime.BotOwner, pair.Value, now))
            {
                MarkWatchApplied(pair.Key, pair.Value);
            }
        }
    }

    private static string GetWatchYieldReason(OperatorDecisionSnapshot snapshot, OwnedPreview owned, DateTimeOffset now)
    {
        if (snapshot.GrenadeHazard.HasRelevantHazard)
        {
            return "grenade";
        }
        if (snapshot.Medical.Actionability.AnyMedicineUsing)
        {
            return "medical";
        }
        if (VanguardMovementAuthorityDoctrine.IsTrueDirectThreat(snapshot)
            || VanguardMovementAuthorityDoctrine.HasImmediateCombatAwareness(snapshot))
        {
            return "combat";
        }
        if (VanguardSainSquadCombatAuthority.TryGetCachedAuthority(snapshot.BotProfileId, now, out _, out _)
            || VanguardMainIntentScheduler.IsSainCombatExecutionProtected(snapshot.BotProfileId, now, out _))
        {
            return "sain";
        }
        if (VanguardMainIntentScheduler.TryGetActivePrimaryWindow(snapshot.BotProfileId, now, out var activeKind, out _, out _, out _)
            && (string.Equals(activeKind, VanguardPrimaryExecutionWindowKinds.CorpseLoot, StringComparison.OrdinalIgnoreCase)
                || string.Equals(activeKind, VanguardPrimaryExecutionWindowKinds.WorldContainerLoot, StringComparison.OrdinalIgnoreCase)))
        {
            return "loot";
        }
        if (HorizontalDistance(snapshot.Position, owned.Anchor) > WatchApplyRadiusMeters
            || snapshot.RealSpeed > WatchMovementYieldSpeedMetersPerSecond)
        {
            return "movement";
        }
        return string.Empty;
    }

    private static bool TryApplyOwnedWatchOrientation(string botProfileId, BotOwner botOwner, OwnedPreview owned, DateTimeOffset now)
    {
        if (LastWatchApplyAtByBot.TryGetValue(botProfileId, out var lastApply)
            && now - lastApply < WatchApplyInterval)
        {
            return false;
        }

        var applied = false;
        try
        {
            var steering = botOwner.Steering;
            if (steering != null)
            {
                steering.LookToPoint(owned.WatchPoint);
                applied = true;
            }
        }
        catch
        {
            // Best-effort orientation only. Movement/SAIN/combat authority is never changed here.
        }
        finally
        {
            LastWatchApplyAtByBot[botProfileId] = now;
        }
        return applied;
    }

    private static WatchTelemetryState GetOrResetWatchTelemetry(
        string botProfileId,
        AuthorState state,
        VanguardTacticalAuthoringSlot slot,
        string operatorId)
    {
        if (!WatchTelemetryByBot.TryGetValue(botProfileId, out var telemetry)
            || !string.Equals(telemetry.OwnerProfileId, state.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(telemetry.LiveSessionId, state.LiveSessionId, StringComparison.Ordinal)
            || !string.Equals(telemetry.SlotId, slot.SlotId, StringComparison.Ordinal))
        {
            telemetry = new WatchTelemetryState
            {
                OwnerProfileId = state.OwnerProfileId,
                OperatorId = operatorId,
                LiveSessionId = state.LiveSessionId,
                SlotId = slot.SlotId
            };
            WatchTelemetryByBot[botProfileId] = telemetry;
        }
        else
        {
            telemetry.OperatorId = operatorId;
        }
        return telemetry;
    }

    private static void MarkWatchYield(string botProfileId, OwnedPreview owned, string reason)
    {
        if (!TryGetMatchingWatchTelemetry(botProfileId, owned, out var telemetry))
        {
            return;
        }
        MarkWatchYield(botProfileId, telemetry, reason);
    }

    private static void MarkWatchYield(string botProfileId, WatchTelemetryState telemetry, string reason)
    {
        if ((!telemetry.InHold && !telemetry.FirstWatchApplied)
            || (telemetry.Yielded && string.Equals(telemetry.LastYieldReason, reason, StringComparison.Ordinal)))
        {
            return;
        }

        telemetry.Yielded = true;
        telemetry.LastYieldReason = reason;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"TACTICAL_AUTHORING_WATCH_YIELD owner={telemetry.OwnerProfileId}; operator={telemetry.OperatorId}; bot={botProfileId}; slot={telemetry.SlotId}; reason={reason}");
    }

    private static string MapPreviewPreemptionToWatchYield(string openReason)
    {
        var normalized = openReason?.ToLowerInvariant() ?? string.Empty;
        if (normalized.Contains("grenade")) return "grenade";
        if (normalized.Contains("medical") || normalized.Contains("medicine") || normalized.Contains("heal")) return "medical";
        if (normalized.Contains("sain")) return "sain";
        if (normalized.Contains("combat") || normalized.Contains("threat")) return "combat";
        if (normalized.Contains("corpse") || normalized.Contains("loot")) return "loot";
        return "movement";
    }

    private static void MarkWatchApplied(string botProfileId, OwnedPreview owned)
    {
        if (!TryGetMatchingWatchTelemetry(botProfileId, owned, out var telemetry))
        {
            return;
        }

        if (telemetry.Yielded)
        {
            var priorReason = telemetry.LastYieldReason;
            telemetry.Yielded = false;
            telemetry.LastYieldReason = string.Empty;
            telemetry.FirstWatchApplied = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TACTICAL_AUTHORING_WATCH_RESUME owner={owned.OwnerProfileId}; operator={owned.OperatorId}; bot={botProfileId}; slot={owned.SlotId}; priorReason={priorReason}");
            return;
        }

        if (!telemetry.FirstWatchApplied)
        {
            telemetry.FirstWatchApplied = true;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TACTICAL_AUTHORING_WATCH_APPLIED owner={owned.OwnerProfileId}; operator={owned.OperatorId}; bot={botProfileId}; slot={owned.SlotId}; watch=({owned.WatchDirection.x:0.00},{owned.WatchDirection.y:0.00},{owned.WatchDirection.z:0.00})");
        }
    }

    private static bool TryGetMatchingWatchTelemetry(string botProfileId, OwnedPreview owned, out WatchTelemetryState telemetry)
    {
        if (WatchTelemetryByBot.TryGetValue(botProfileId, out var state)
            && string.Equals(state.OwnerProfileId, owned.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(state.LiveSessionId, owned.LiveSessionId, StringComparison.Ordinal)
            && string.Equals(state.SlotId, owned.SlotId, StringComparison.Ordinal))
        {
            telemetry = state;
            return true;
        }
        telemetry = null!;
        return false;
    }

    private static List<SlotEvaluation> ApplyMutualExclusion(List<SlotEvaluation> slots)
    {
        var result = new List<SlotEvaluation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var evaluation in slots)
        {
            var group = evaluation.Slot.MutualExclusionGroup?.Trim() ?? string.Empty;
            if (group.Length > 0 && !seen.Add(group))
            {
                evaluation.Result.MovementState = "validated_unassigned:mutual_exclusion";
                continue;
            }
            result.Add(evaluation);
        }
        return result;
    }

    private static void CleanupPreemptedOrStale(DateTimeOffset now)
    {
        foreach (var pair in OwnedByBot.ToArray())
        {
            var owned = pair.Value;
            bool sessionFresh = AuthorsByOwner.TryGetValue(owned.OwnerProfileId, out var author)
                && string.Equals(author.LiveSessionId, owned.LiveSessionId, StringComparison.Ordinal)
                && now - author.ReceivedAtUtc <= SessionFreshness;
            bool assignmentCurrent = sessionFresh && author != null && IsOwnedAssignmentCurrent(author, owned);
            bool hasPrimaryWindow = VanguardMainIntentScheduler.TryGetActivePrimaryWindow(pair.Key, now, out var kind, out _, out _, out _);
            bool windowOwned = hasPrimaryWindow
                && string.Equals(kind, VanguardPrimaryExecutionWindowKinds.AuthoringPreviewMovement, StringComparison.OrdinalIgnoreCase);
            bool corpseLootAssignmentProtected = assignmentCurrent
                && IsProtectedCorpseLootAssignment(author!, owned, pair.Key, now);
            if (!sessionFresh || !assignmentCurrent || (!windowOwned && !corpseLootAssignmentProtected))
            {
                string releaseReason = !sessionFresh
                    ? "author_session_stale"
                    : !assignmentCurrent
                        ? "author_assignment_changed_during_preemption"
                        : "scheduler_preempted_preview";
                ReleaseOperator(pair.Key, now, releaseReason, finishWindow: windowOwned, preserveWatchTelemetry: sessionFresh);
            }
        }
    }

    private static void ReleaseOwner(string ownerProfileId, DateTimeOffset now, string reason)
    {
        foreach (var botProfileId in OwnedByBot.Where(pair => string.Equals(pair.Value.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray())
        {
            ReleaseOperator(botProfileId, now, reason);
        }
        foreach (var botProfileId in WatchTelemetryByBot.Where(pair => string.Equals(pair.Value.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray())
        {
            WatchTelemetryByBot.Remove(botProfileId);
        }
    }

    private static void ReleaseOperator(
        string botProfileId,
        DateTimeOffset now,
        string reason,
        bool finishWindow = true,
        bool preserveWatchTelemetry = false)
    {
        if (!OwnedByBot.TryGetValue(botProfileId, out var owned))
        {
            if (!preserveWatchTelemetry)
            {
                WatchTelemetryByBot.Remove(botProfileId);
            }
            return;
        }

        VanguardReturnMovementCommandStore.ClearOwned(botProfileId, owned.WindowId, owned.StartedAtUtc, "tactical_authoring_preview_release:" + reason);
        if (finishWindow)
        {
            VanguardMainIntentScheduler.FinishPrimaryWindow(botProfileId, now, "Interrupted", "tactical_authoring_preview_release:" + reason, expectedWindowId: owned.WindowId);
        }
        OwnedByBot.Remove(botProfileId);
        LastWatchApplyAtByBot.Remove(botProfileId);
        if (!preserveWatchTelemetry)
        {
            WatchTelemetryByBot.Remove(botProfileId);
        }
    }

    private static bool HasHeadlessStaticCapsuleClearance(Vector3 projected)
    {
        const float radius = 0.34f;
        const float height = 1.75f;
        var bottom = projected + Vector3.up * (radius + 0.06f);
        var top = projected + Vector3.up * (height - radius);
        foreach (var collider in Physics.OverlapCapsule(bottom, top, radius, LayerMaskClass.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore))
        {
            if (collider == null || collider.isTrigger || collider.GetComponentInParent<Player>() != null)
            {
                continue;
            }
            return false;
        }
        return true;
    }

    private static bool TryGetBotPosition(BotOwner botOwner, out Vector3 position)
    {
        position = Vector3.zero;
        try
        {
            object? player = VanguardOperatorRuntimeAuditReflection.GetMember(botOwner, "GetPlayer", "Player");
            object? transform = VanguardOperatorRuntimeAuditReflection.GetDeep(player, "PlayerBones", "BodyTransform");
            object? value = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(transform, "position");
            if (value is Vector3 vector)
            {
                position = vector;
                return true;
            }
            object? playerTransform = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player, "Transform", "transform");
            value = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(playerTransform, "position");
            if (value is Vector3 transformed)
            {
                position = transformed;
                return true;
            }
        }
        catch
        {
            // Read-only probe.
        }
        return false;
    }

    private static bool IsCurrentMap(string mapId)
    {
        try
        {
            var world = Singleton<GameWorld>.Instance;
            return world != null && string.Equals(world.LocationId, mapId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static VanguardTacticalAuthoringHeadlessPreviewResult NewResult(AuthorState state, DateTimeOffset now) => new()
    {
        OwnerProfileId = state.OwnerProfileId,
        LiveSessionId = state.LiveSessionId,
        MapId = state.MapId,
        AuthorRevision = state.Revision,
        SelectedZoneId = state.SelectedZoneId,
        GeneratedAtUtc = now,
        HeadlessBuild = VanguardBuildVersion.BuildLabel
    };

    private sealed class SlotEvaluation
    {
        public SlotEvaluation(VanguardTacticalAuthoringSlot slot)
        {
            Slot = slot;
            Anchor = slot.Position.ToVector3();
            Result = new VanguardTacticalAuthoringHeadlessSlotResult { SlotId = slot.SlotId };
        }

        public VanguardTacticalAuthoringSlot Slot { get; }
        public Vector3 Anchor { get; set; }
        public VanguardTacticalAuthoringHeadlessSlotResult Result { get; }
        public Dictionary<string, OperatorPath> PathsByBotProfileId { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class OperatorPath
    {
        public OperatorPath(VanguardRaidOperatorRuntimeRecord record, float distanceMeters, string pathSummary)
        {
            Record = record;
            DistanceMeters = distanceMeters;
            PathSummary = pathSummary;
        }

        public VanguardRaidOperatorRuntimeRecord Record { get; }
        public float DistanceMeters { get; }
        public string PathSummary { get; }
    }

    private sealed class AuthorState
    {
        public string OwnerProfileId = string.Empty;
        public string LiveSessionId = string.Empty;
        public string MapId = string.Empty;
        public long Revision;
        public string SelectedZoneId = string.Empty;
        public VanguardTacticalAuthoringMapFile? Map;
        public string ParseError = string.Empty;
        public DateTimeOffset ReceivedAtUtc = DateTimeOffset.MinValue;
    }

    private sealed class OwnedPreview
    {
        public string OwnerProfileId = string.Empty;
        public string OperatorId = string.Empty;
        public string LiveSessionId = string.Empty;
        public string ZoneId = string.Empty;
        public string SlotId = string.Empty;
        public string WindowId = string.Empty;
        public DateTimeOffset StartedAtUtc;
        public Vector3 Anchor;
        public Vector3 WatchPoint;
        public Vector3 WatchDirection;
    }

    private sealed class WatchTelemetryState
    {
        public string OwnerProfileId = string.Empty;
        public string OperatorId = string.Empty;
        public string LiveSessionId = string.Empty;
        public string SlotId = string.Empty;
        public bool InHold;
        public bool FirstWatchApplied;
        public bool Yielded;
        public string LastYieldReason = string.Empty;
    }
}

internal readonly struct VanguardTacticalAuthoringLootExcursionContext
{
    public VanguardTacticalAuthoringLootExcursionContext(
        string ownerProfileId,
        string liveSessionId,
        string zoneId,
        string slotId,
        Vector3 zoneAnchor,
        float zoneRadius,
        float minY,
        float maxY)
    {
        OwnerProfileId = ownerProfileId;
        LiveSessionId = liveSessionId;
        ZoneId = zoneId;
        SlotId = slotId;
        ZoneAnchor = zoneAnchor;
        ZoneRadius = zoneRadius;
        MinY = minY;
        MaxY = maxY;
    }

    public string OwnerProfileId { get; }
    public string LiveSessionId { get; }
    public string ZoneId { get; }
    public string SlotId { get; }
    public Vector3 ZoneAnchor { get; }
    public float ZoneRadius { get; }
    public float MinY { get; }
    public float MaxY { get; }

    public bool Contains(Vector3 position)
    {
        var a = ZoneAnchor;
        var b = position;
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b) <= ZoneRadius
            && position.y >= MinY
            && position.y <= MaxY;
    }

    public string Summary => $"owner={OwnerProfileId};session={LiveSessionId};zone={ZoneId};slot={SlotId};radius={ZoneRadius:0.00};floorY=[{MinY:0.00},{MaxY:0.00}]";
}
#endif
