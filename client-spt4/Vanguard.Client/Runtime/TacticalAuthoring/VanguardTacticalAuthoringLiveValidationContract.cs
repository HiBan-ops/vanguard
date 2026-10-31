#if SPT_CLIENT
using System;

// Responsibility: Defines the transient contract that distinguishes read-only Tactical Coordination validation snapshots from ordinary HOLD live previews.
// Flow: Portal authoring emits a synthetic HOLD-shaped validation envelope tagged with this zone id, and the runtime authority recognizes the tag before evaluating points.
// Authority boundary: The contract grants validation-only authority; it never authorizes assignment, movement, persisted mutation, or TC runtime consumption.
// Invariant: A Portal validation snapshot may produce navigation evidence only and must release any prior HOLD movement ownership before evaluation.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal static class VanguardTacticalAuthoringLiveValidationContract
{
    public const string PortalValidationZoneId = "__VANGUARD_TC_PORTAL_VALIDATION_ONLY__";

    public static bool IsPortalValidationZone(string? zoneId)
    {
        return string.Equals(zoneId, PortalValidationZoneId, StringComparison.Ordinal);
    }
}
#endif
