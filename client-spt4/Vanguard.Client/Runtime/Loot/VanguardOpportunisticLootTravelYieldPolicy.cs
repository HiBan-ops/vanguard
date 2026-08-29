#if SPT_CLIENT
using System;
using System.Globalization;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Encodes the deterministic rules for Opportunistic Loot Travel Yield Policy within the loot runtime.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Runtime.Loot;

/// <summary>
/// Narrow responsiveness exception allowing bounded opportunistic-loot travel to yield. It never overrides combat, grenade, medical or hard-return
/// safety gates; callers evaluate those first. Its only purpose is to let nearby opportunistic loot
/// interrupt a residual TravelCohesionFollowThrough command after the canonical owner route proves the
/// player has stopped and the Operator is already inside the preferred action-rally envelope.
/// </summary>
internal static class VanguardOpportunisticLootTravelYieldPolicy
{
    public const string StatusTag = "VANGUARD_BOUNDED_LOOT_TRAVEL_YIELD_STATUS";
    public const float MaximumTargetDirectDistanceMeters = 12.0f;
    public const float MinimumOwnerStationarySeconds = 2.50f;
    private const float MaximumOperatorOwnerDistanceMeters = 24.0f;

    public static bool CanYield(
        OperatorDecisionSnapshot snapshot,
        string? activeRequestKind,
        float targetDirectDistanceMeters,
        DateTimeOffset now,
        out string proof)
    {
        proof = "none";
        if (snapshot == null)
        {
            proof = "snapshot_missing";
            return false;
        }

        if (!string.Equals(activeRequestKind, VanguardMovementContractPolicy.TravelCohesionFollowThrough, StringComparison.OrdinalIgnoreCase))
        {
            proof = "request_not_travel_cohesion:" + Safe(activeRequestKind);
            return false;
        }

        if (float.IsNaN(targetDirectDistanceMeters)
            || float.IsInfinity(targetDirectDistanceMeters)
            || targetDirectDistanceMeters < 0f
            || targetDirectDistanceMeters > MaximumTargetDirectDistanceMeters)
        {
            proof = "target_not_nearby:" + targetDirectDistanceMeters.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        float ownerDistanceLimit = Math.Min(MaximumOperatorOwnerDistanceMeters, VanguardMovementAuthorityDoctrine.ActionRallyPreferredMeters);
        if (!snapshot.SquadCohesion.OwnerKnown
            || !snapshot.SquadCohesion.OwnerReliableForActiveMovement
            || snapshot.SquadCohesion.OperatorDistanceToOwner > ownerDistanceLimit)
        {
            proof = "operator_not_inside_preferred_rally:distance="
                + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.00", CultureInfo.InvariantCulture)
                + ":limit=" + ownerDistanceLimit.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        if (!VanguardSquadTravelRouteMemory.TryGetOwnerStationaryState(
                snapshot.OwnerProfileId,
                now,
                out VanguardOwnerTravelStationaryState stationary,
                out string stationaryReason))
        {
            proof = "owner_stationary_truth_unavailable:" + Safe(stationaryReason);
            return false;
        }

        if (stationary.OwnerMovingRecently || stationary.StationarySeconds < MinimumOwnerStationarySeconds)
        {
            proof = "owner_not_stationary_long_enough:moving=" + Bool(stationary.OwnerMovingRecently)
                + ":stationary=" + stationary.StationarySeconds.ToString("0.00", CultureInfo.InvariantCulture)
                + ":minimum=" + MinimumOwnerStationarySeconds.ToString("0.00", CultureInfo.InvariantCulture);
            return false;
        }

        proof = "bounded_travel_yield:request=TravelCohesionFollowThrough"
            + ":targetDistance=" + targetDirectDistanceMeters.ToString("0.00", CultureInfo.InvariantCulture)
            + ":ownerDistance=" + snapshot.SquadCohesion.OperatorDistanceToOwner.ToString("0.00", CultureInfo.InvariantCulture)
            + ":rallyLimit=" + ownerDistanceLimit.ToString("0.00", CultureInfo.InvariantCulture)
            + ":ownerStationary=" + stationary.StationarySeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + ":routeEpoch=" + stationary.RouteEpoch.ToString(CultureInfo.InvariantCulture)
            + ":routeVersion=" + stationary.RouteVersion.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    private static string Bool(bool value) => value ? "true" : "false";
}
#endif
