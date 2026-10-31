#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Decision;

// Responsibility: records raw placement-quality evidence for the exact Tactical Opportunity lease that has already passed canonical admission and started physical movement.
// Flow: one started lease contributes its already-prepared path/destination, the exact fresh Awareness-qualified threat supplies runtime pose, and the existing ThreatExposure observation primitive samples bounded route/destination exposure while support geometry is read from the current runtime registry.
// Authority boundary: shadow diagnostics only. No score, threshold, route preference, lease decision, replan, movement, target, aim, fire or SAIN mutation is authorized here.
// Invariant: missing/budget-deferred evidence remains UNRESOLVED; raw occlusion is never promoted to hard-cover proof and angular geometry is never promoted to tactical advantage without later runtime calibration.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class TacticalPlacementQualityShadowService
{
    public const string StatusTag = "VANGUARD_TC_PLACEMENT_QUALITY_SHADOW_STATUS";
    private const int MaxDiagnosticFieldLength = 160;

    internal static void ObserveStartedLease(
        OperatorDecisionSnapshot? snapshot,
        string? kind,
        string? leaseId,
        IReadOnlyList<Vector3>? preparedPathCorners,
        Vector3 destination,
        string? supportBotProfileId,
        string? supportContextId,
        DateTimeOffset now)
    {
        if (snapshot == null || !snapshot.Alive)
        {
            return;
        }

        string owner = Normalize(snapshot.OwnerProfileId);
        string threatId = Normalize(snapshot.Threat?.EnemyId);
        string bot = Normalize(snapshot.BotProfileId);
        string lease = Normalize(leaseId);
        if (owner.Length == 0 || threatId.Length == 0 || bot.Length == 0 || lease.Length == 0)
        {
            return;
        }

        IReadOnlyList<VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation> qualified =
            VanguardCombatAwarenessBridge.GetFreshQualifiedTacticalCoordinationThreats(now);
        if (!TryFindFreshQualified(qualified, owner, threatId, out VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation threat))
        {
            Emit(
                snapshot,
                kind,
                lease,
                destination,
                supportBotProfileId,
                supportContextId,
                "UNRESOLVED_EXACT_AWARENESS_THREAT_MISSING",
                null,
                "UNRESOLVED_NO_EXACT_AWARENESS_THREAT",
                false,
                false,
                float.NaN,
                float.NaN,
                float.NaN,
                0,
                now,
                default);
            return;
        }

        int linecasts = 0;
        RouteExposureObservation? route = null;
        bool routeSamplingCompleted = preparedPathCorners != null
            && preparedPathCorners.Count >= 2
            && ThreatExposureObservationService.TryObserveExistingRoute(
                owner,
                "tactical_opportunity_started_path",
                lease,
                bot,
                preparedPathCorners,
                threat.RuntimePosition,
                threat.RuntimeLookDirection,
                ref linecasts,
                out route);

        string routeAssessment;
        if (preparedPathCorners == null || preparedPathCorners.Count < 2)
        {
            routeAssessment = "UNRESOLVED_NO_PREPARED_PATH_GEOMETRY";
        }
        else if (!routeSamplingCompleted)
        {
            routeAssessment = "UNRESOLVED_ROUTE_OBSERVATION_BUDGET_DEFERRED";
        }
        else if (route == null)
        {
            routeAssessment = "UNRESOLVED_NO_USABLE_ROUTE_SAMPLES";
        }
        else
        {
            routeAssessment = "RAW_ROUTE_EXPOSURE_AVAILABLE";
        }

        bool pointSamplingCompleted = ThreatExposureObservationService.TryObserveExistingPoint(
            owner,
            destination,
            threat.RuntimePosition,
            threat.RuntimeLookDirection,
            ref linecasts,
            out bool destinationSampleAvailable,
            out bool destinationLineOfSightClear,
            out float destinationFacingDot,
            out float destinationFacingAngleDegrees,
            out float destinationThreatDistanceMeters);

        string destinationAssessment = !pointSamplingCompleted
            ? "UNRESOLVED_DESTINATION_OBSERVATION_BUDGET_DEFERRED"
            : !destinationSampleAvailable
                ? "UNRESOLVED_DESTINATION_GEOMETRY"
                : destinationLineOfSightClear
                    ? "RAW_DESTINATION_LOS_CLEAR"
                    : "RAW_DESTINATION_OCCLUDED";

        Emit(
            snapshot,
            kind,
            lease,
            destination,
            supportBotProfileId,
            supportContextId,
            routeAssessment,
            route,
            destinationAssessment,
            destinationSampleAvailable,
            destinationLineOfSightClear,
            destinationFacingDot,
            destinationFacingAngleDegrees,
            destinationThreatDistanceMeters,
            linecasts,
            now,
            threat);
    }

    private static void Emit(
        OperatorDecisionSnapshot snapshot,
        string? kind,
        string lease,
        Vector3 destination,
        string? supportBotProfileId,
        string? supportContextId,
        string routeAssessment,
        RouteExposureObservation? route,
        string destinationAssessment,
        bool destinationSampleAvailable,
        bool destinationLineOfSightClear,
        float destinationFacingDot,
        float destinationFacingAngleDegrees,
        float destinationThreatDistanceMeters,
        int linecasts,
        DateTimeOffset now,
        VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation threat)
    {
        string supportBot = Normalize(supportBotProfileId);
        bool supportDeclared = supportBot.Length > 0;
        bool supportRuntimeAvailable = false;
        float supportToDestinationMeters = float.NaN;
        float supportToThreatMeters = float.NaN;
        float moverToSupportMeters = float.NaN;
        if (supportDeclared
            && VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(supportBot, out VanguardRaidOperatorRuntimeRecord supportRuntime)
            && supportRuntime.BotOwner != null
            && !supportRuntime.BotOwner.IsDead)
        {
            supportRuntimeAvailable = true;
            Vector3 supportPosition = supportRuntime.BotOwner.Position;
            supportToDestinationMeters = HorizontalDistance(supportPosition, destination);
            if (!string.IsNullOrWhiteSpace(threat.TargetProfileId))
            {
                supportToThreatMeters = HorizontalDistance(supportPosition, threat.RuntimePosition);
            }
            moverToSupportMeters = HorizontalDistance(snapshot.Position, supportPosition);
        }

        string hardCoverEvidence = !destinationSampleAvailable
            ? "UNRESOLVED"
            : destinationLineOfSightClear
                ? "NO_OCCLUSION_AT_DESTINATION_NO_HARD_COVER_PROOF"
                : "OCCLUDED_SINGLE_LINECAST_RAW_ONLY_NOT_HARD_COVER_PROOF";

        string fallbackEvidence = "UNRESOLVED_NO_STRUCTURED_FALLBACK_PROOF";
        string angularAssessment = destinationSampleAvailable
            ? "RAW_ONLY_NO_PROMOTED_ANGULAR_THRESHOLD"
            : "UNRESOLVED";
        string supportAssessment = !supportDeclared
            ? "NO_EXPLICIT_SUPPORT_REQUIRED"
            : supportRuntimeAvailable
                ? "RAW_SUPPORT_GEOMETRY_AVAILABLE"
                : "UNRESOLVED_DECLARED_SUPPORT_RUNTIME_MISSING";

        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
            $"TC_PLACEMENT_QUALITY_SHADOW kind={Bounded(kind)}; owner={Bounded(snapshot.OwnerProfileId)}; threat={Bounded(snapshot.Threat?.EnemyId)}; bot={Bounded(snapshot.BotProfileId)}; lease={Bounded(lease)}; destination={Vector(destination)}; routeAssessment={Bounded(routeAssessment)}; routeSamples={(route?.SampleCount ?? 0)}; routeUnobstructedRatio={Number(route?.UnobstructedSampleRatio)}; routeForwardVisibleRatio={Number(route?.ForwardVisibleSampleRatio)}; routeFacingWeightedLosMean={Number(route?.FacingWeightedLosEvidenceMean)}; routeFacingWeightedLosMax={Number(route?.FacingWeightedLosEvidenceMax)}; routeVisibleFacingAngleMean={Number(route?.VisibleFacingAngleDegreesMean)}; routeThreatDistanceMin={Number(route?.ThreatDistanceMetersMin)}; routeThreatDistanceMean={Number(route?.ThreatDistanceMetersMean)}; routeThreatDistanceMax={Number(route?.ThreatDistanceMetersMax)}; destinationAssessment={Bounded(destinationAssessment)}; destinationLos={(destinationSampleAvailable ? Bool(destinationLineOfSightClear) : "unknown")}; destinationFacingDot={Number(destinationSampleAvailable ? destinationFacingDot : float.NaN)}; destinationFacingAngleDegrees={Number(destinationSampleAvailable ? destinationFacingAngleDegrees : float.NaN)}; destinationThreatDistanceMeters={Number(destinationSampleAvailable ? destinationThreatDistanceMeters : float.NaN)}; hardCoverEvidence={Bounded(hardCoverEvidence)}; fallbackEvidence={Bounded(fallbackEvidence)}; threatSectorAssessment=RAW_FACING_GEOMETRY_ONLY; angularAdvantageAssessment={Bounded(angularAssessment)}; supportAssessment={Bounded(supportAssessment)}; supportBot={Bounded(supportBotProfileId)}; supportContext={Bounded(supportContextId)}; supportToDestinationMeters={Number(supportToDestinationMeters)}; supportToThreatMeters={Number(supportToThreatMeters)}; moverToSupportMeters={Number(moverToSupportMeters)}; contactKind={Bounded(threat.ContactKind)}; contactAgeSeconds={Number(string.IsNullOrWhiteSpace(threat.TargetProfileId) ? double.NaN : Math.Max(0d, (now - threat.ObservedAtUtc).TotalSeconds))}; linecasts={linecasts}; placementSolverPath=true; preparedPath=true; opaqueQualityScore=false; promotedThreshold=false; shadowOnly=true; executionAllowed=false; routeMutation=false; leaseMutation=false; replanMutation=false; targetMutation=false; aimMutation=false; fireCommand=false; sainMutation=false");
    }

    private static bool TryFindFreshQualified(
        IReadOnlyList<VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation> observations,
        string ownerProfileId,
        string targetProfileId,
        out VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation observation)
    {
        for (int index = 0; index < observations.Count; index++)
        {
            VanguardCombatAwarenessBridge.TacticalCoordinationThreatRuntimeObservation current = observations[index];
            if (string.Equals(current.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.TargetProfileId, targetProfileId, StringComparison.OrdinalIgnoreCase))
            {
                observation = current;
                return true;
            }
        }

        observation = default;
        return false;
    }

    private static float HorizontalDistance(Vector3 first, Vector3 second)
    {
        float dx = first.x - second.x;
        float dz = first.z - second.z;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    private static string Normalize(string? value)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        return string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "null", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "unknown", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : normalized;
    }

    private static string Bounded(string? value)
    {
        string safe = Normalize(value).Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
        if (safe.Length == 0)
        {
            return "none";
        }
        return safe.Length <= MaxDiagnosticFieldLength ? safe : safe.Substring(0, MaxDiagnosticFieldLength);
    }

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Number(float value) => !float.IsNaN(value) && !float.IsInfinity(value) ? value.ToString("0.00", CultureInfo.InvariantCulture) : "unknown";
    private static string Number(float? value) => value.HasValue && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value) ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : "unknown";
    private static string Number(double value) => !double.IsNaN(value) && !double.IsInfinity(value) ? value.ToString("0.00", CultureInfo.InvariantCulture) : "unknown";
    private static string Vector(Vector3 value) => Number(value.x) + "," + Number(value.y) + "," + Number(value.z);
}
#endif
