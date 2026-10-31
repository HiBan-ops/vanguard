#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;

// Responsibility: Deterministically converts recognized legacy Tactical Authoring documents into current HOLD contracts.
// Flow: Legacy Zone/Slot becomes HoldZone/HoldSlot; Legacy Access becomes an unconfirmed top-level LegacyPortalCandidate; compatibility projections are rebuilt only for the active editor.
// Authority boundary: Migration preserves authored facts inside the Tactical Authoring document only; it never creates or writes Tactical Coordination Base/User objects or physical pose truth.
// Invariant: Conversion is deterministic for the same validated legacy input and source hash; unknown schemas never enter this converter and TC persistence remains physically separate.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal static class VanguardTacticalAuthoringSchemaMigration
{
    public const int KnownLegacySchemaVersion = 1;

    public static VanguardTacticalAuthoringMapFile MigrateLegacyAuthoring(
        VanguardLegacyTacticalAuthoringMapFile legacy,
        string sourceSha256,
        string migratedAt)
    {
        if (legacy.SchemaVersion != KnownLegacySchemaVersion)
        {
            throw new InvalidOperationException($"Schema-v1 migration received schema {legacy.SchemaVersion}.");
        }
        if (legacy.RuntimeConsumptionEnabled)
        {
            throw new InvalidOperationException("Schema-v1 migration refuses RuntimeConsumptionEnabled=true.");
        }

        legacy.Zones ??= new List<VanguardLegacyTacticalAuthoringZone>();
        var result = new VanguardTacticalAuthoringMapFile
        {
            SchemaVersion = VanguardTacticalAuthoringStore.CurrentSchemaVersion,
            MapId = legacy.MapId,
            MapRevision = legacy.MapRevision,
            EftClientVersion = legacy.EftClientVersion,
            CreatedAt = legacy.CreatedAt,
            LastSavedAt = legacy.LastSavedAt,
            CreatedWithBuild = legacy.CreatedWithBuild,
            LastSavedWithBuild = legacy.LastSavedWithBuild,
            RuntimeConsumptionEnabled = false,
            HoldZones = new List<VanguardTacticalAuthoringZone>(),
            LegacyPortalCandidates = new List<VanguardTacticalAuthoringLegacyPortalCandidate>()
        };

        int holdSlotCount = 0;
        foreach (var legacyZone in legacy.Zones)
        {
            legacyZone.Slots ??= new List<VanguardLegacyTacticalAuthoringSlot>();
            legacyZone.Accesses ??= new List<VanguardLegacyTacticalAuthoringAccess>();

            var holdZone = new VanguardTacticalAuthoringZone
            {
                ZoneId = legacyZone.ZoneId,
                DisplayZoneName = legacyZone.DisplayZoneName,
                BuildingId = legacyZone.BuildingId,
                FloorId = legacyZone.FloorId,
                MinY = legacyZone.MinY,
                MaxY = legacyZone.MaxY,
                MinYExplicit = legacyZone.MinYExplicit,
                MaxYExplicit = legacyZone.MaxYExplicit,
                FloorBoundsProvisional = legacyZone.FloorBoundsProvisional,
                ZoneAnchor = legacyZone.ZoneAnchor,
                ZoneRadius = legacyZone.ZoneRadius,
                CreatedAt = legacyZone.CreatedAt,
                CreatedWithBuild = legacyZone.CreatedWithBuild,
                LastValidatedAt = legacyZone.LastValidatedAt,
                LastValidatedBuild = legacyZone.LastValidatedBuild,
                Slots = legacyZone.Slots.Select(ConvertSlot).ToList(),
                Accesses = legacyZone.Accesses.Select(ConvertCompatibilityAccess).ToList()
            };
            holdSlotCount += holdZone.Slots.Count;
            result.HoldZones.Add(holdZone);

            foreach (var legacyAccess in legacyZone.Accesses)
            {
                result.LegacyPortalCandidates.Add(new VanguardTacticalAuthoringLegacyPortalCandidate
                {
                    CandidateId = legacyAccess.AccessId,
                    SourceHoldZoneId = legacyZone.ZoneId,
                    OriginalAccessId = legacyAccess.AccessId,
                    DisplayName = legacyAccess.DisplayName,
                    Position = legacyAccess.Position,
                    NavMeshProjectedPosition = legacyAccess.NavMeshProjectedPosition,
                    ApproachDirection = legacyAccess.ApproachDirection,
                    NavMeshProjectionSucceeded = legacyAccess.NavMeshProjectionSucceeded,
                    CreatedAt = legacyAccess.CreatedAt,
                    CreatedWithBuild = legacyAccess.CreatedWithBuild,
                    ReviewState = "unconfirmed"
                });
            }
        }

        result.MigrationRecord = new VanguardTacticalAuthoringMigrationRecord
        {
            FromSchemaVersion = KnownLegacySchemaVersion,
            ToSchemaVersion = VanguardTacticalAuthoringStore.CurrentSchemaVersion,
            SourceSha256 = sourceSha256,
            MigratedAt = migratedAt,
            MigratedWithBuild = VanguardBuildVersion.BuildLabel,
            HoldZoneCount = result.HoldZones.Count,
            HoldSlotCount = holdSlotCount,
            LegacyPortalCandidateCount = result.LegacyPortalCandidates.Count,
            ActivePortalCountCreated = 0,
            PhysicalPoseSnapshotsInvented = 0,
            SourceBackupFileName = string.Empty
        };

        AssertLegacyZoneSlotParity(legacy, result);
        return result;
    }

    // Hard migration gate: the current representation must not reconstruct or invent an existing authored layout.
    // Every legacy zone must survive with identical authored geometry, and every legacy slot must survive
    // inside the same owning zone with identical placement/validation/compatibility facts.
    public static void AssertLegacyZoneSlotParity(
        VanguardLegacyTacticalAuthoringMapFile legacy,
        VanguardTacticalAuthoringMapFile migrated)
    {
        legacy.Zones ??= new List<VanguardLegacyTacticalAuthoringZone>();
        migrated.HoldZones ??= new List<VanguardTacticalAuthoringZone>();

        if (legacy.Zones.Count != migrated.HoldZones.Count)
        {
            throw new InvalidOperationException($"Schema migration zone-count regression. legacy={legacy.Zones.Count}; migrated={migrated.HoldZones.Count}");
        }

        var migratedZones = migrated.HoldZones.ToDictionary(zone => zone.ZoneId, StringComparer.Ordinal);
        foreach (var legacyZone in legacy.Zones)
        {
            if (!migratedZones.TryGetValue(legacyZone.ZoneId, out var zone))
            {
                throw new InvalidOperationException($"Schema migration lost legacy zone {legacyZone.ZoneId}.");
            }

            if (!string.Equals(legacyZone.DisplayZoneName, zone.DisplayZoneName, StringComparison.Ordinal)
                || !string.Equals(legacyZone.BuildingId, zone.BuildingId, StringComparison.Ordinal)
                || !string.Equals(legacyZone.FloorId, zone.FloorId, StringComparison.Ordinal)
                || legacyZone.MinY != zone.MinY
                || legacyZone.MaxY != zone.MaxY
                || legacyZone.MinYExplicit != zone.MinYExplicit
                || legacyZone.MaxYExplicit != zone.MaxYExplicit
                || legacyZone.FloorBoundsProvisional != zone.FloorBoundsProvisional
                || !VectorEquals(legacyZone.ZoneAnchor, zone.ZoneAnchor)
                || legacyZone.ZoneRadius != zone.ZoneRadius)
            {
                throw new InvalidOperationException($"Schema migration changed authored geometry/identity for legacy zone {legacyZone.ZoneId}.");
            }

            legacyZone.Slots ??= new List<VanguardLegacyTacticalAuthoringSlot>();
            zone.Slots ??= new List<VanguardTacticalAuthoringSlot>();
            if (legacyZone.Slots.Count != zone.Slots.Count)
            {
                throw new InvalidOperationException($"Schema migration slot-count regression in zone {legacyZone.ZoneId}. legacy={legacyZone.Slots.Count}; migrated={zone.Slots.Count}");
            }

            var migratedSlots = zone.Slots.ToDictionary(slot => slot.SlotId, StringComparer.Ordinal);
            foreach (var legacySlot in legacyZone.Slots)
            {
                if (!migratedSlots.TryGetValue(legacySlot.SlotId, out var slot))
                {
                    throw new InvalidOperationException($"Schema migration lost or moved legacy slot {legacySlot.SlotId} from zone {legacyZone.ZoneId}.");
                }

                var compatibility = slot.LegacyCompatibility;
                if (!VectorEquals(legacySlot.Position, slot.Position)
                    || !NullableVectorEquals(legacySlot.NavMeshProjectedPosition, slot.NavMeshProjectedPosition)
                    || !VectorEquals(legacySlot.WatchDirection, slot.WatchDirection)
                    || legacySlot.Enabled != slot.Enabled
                    || legacySlot.AuthoringValid != slot.AuthoringValid
                    || legacySlot.ValidationState != slot.ValidationState
                    || !string.Equals(legacySlot.ValidationNotes, slot.ValidationNotes, StringComparison.Ordinal)
                    || legacySlot.Priority != slot.CompatibilityAssignmentOrderHint
                    || compatibility == null
                    || legacySlot.SlotType != compatibility.SlotType
                    || legacySlot.WatchArc != compatibility.WatchArc
                    || !string.Equals(legacySlot.AssociatedAccessId, compatibility.AssociatedAccessId, StringComparison.Ordinal)
                    || !string.Equals(legacySlot.ConnectedZoneId, compatibility.ConnectedZoneId, StringComparison.Ordinal)
                    || legacySlot.MinimumSquadSize != compatibility.MinimumSquadSize
                    || legacySlot.MaximumOwnerDistance != compatibility.MaximumOwnerDistance
                    || !string.Equals(legacySlot.RoleAffinity, compatibility.RoleAffinity, StringComparison.Ordinal)
                    || !string.Equals(legacySlot.MutualExclusionGroup, compatibility.MutualExclusionGroup, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Schema migration changed authored facts for legacy slot {legacySlot.SlotId} in zone {legacyZone.ZoneId}.");
                }
            }
        }

        // Tactical Coordination objects are not fields of the authoring document at all. This type-level
        // separation is stronger than asserting empty collections: migration has no persistence path to TC.
    }

    private static bool VectorEquals(VanguardVector3Dto? left, VanguardVector3Dto? right)
    {
        return left != null && right != null && left.X == right.X && left.Y == right.Y && left.Z == right.Z;
    }

    private static bool NullableVectorEquals(VanguardVector3Dto? left, VanguardVector3Dto? right)
    {
        if (left == null || right == null)
        {
            return left == null && right == null;
        }
        return VectorEquals(left, right);
    }

    public static void AttachLegacyAccessCompatibilityViews(VanguardTacticalAuthoringMapFile mapFile)
    {
        mapFile.HoldZones ??= new List<VanguardTacticalAuthoringZone>();
        mapFile.LegacyPortalCandidates ??= new List<VanguardTacticalAuthoringLegacyPortalCandidate>();

        foreach (var zone in mapFile.HoldZones)
        {
            zone.Accesses = mapFile.LegacyPortalCandidates
                .Where(candidate => candidate != null
                    && string.Equals(candidate.SourceHoldZoneId, zone.ZoneId, StringComparison.Ordinal)
                    && !string.Equals(candidate.ReviewState, "promoted", StringComparison.OrdinalIgnoreCase))
                .Select(ConvertCompatibilityAccess)
                .ToList();
        }
    }

    public static void SynchronizeLegacyPortalCandidatesFromCompatibilityViews(VanguardTacticalAuthoringMapFile mapFile)
    {
        mapFile.HoldZones ??= new List<VanguardTacticalAuthoringZone>();
        mapFile.LegacyPortalCandidates ??= new List<VanguardTacticalAuthoringLegacyPortalCandidate>();

        var existingByIdentity = mapFile.LegacyPortalCandidates
            .Where(candidate => candidate != null)
            .GroupBy(candidate => Identity(candidate.SourceHoldZoneId, candidate.OriginalAccessId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var synchronized = mapFile.LegacyPortalCandidates
            .Where(candidate => candidate != null && string.Equals(candidate.ReviewState, "promoted", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var zone in mapFile.HoldZones)
        {
            zone.Accesses ??= new List<VanguardTacticalAuthoringAccess>();
            foreach (var access in zone.Accesses)
            {
                string identity = Identity(zone.ZoneId, access.AccessId);
                existingByIdentity.TryGetValue(identity, out var existing);
                synchronized.Add(new VanguardTacticalAuthoringLegacyPortalCandidate
                {
                    CandidateId = string.IsNullOrWhiteSpace(existing?.CandidateId) ? access.AccessId : existing.CandidateId,
                    SourceHoldZoneId = zone.ZoneId,
                    OriginalAccessId = access.AccessId,
                    DisplayName = access.DisplayName,
                    Position = access.Position,
                    NavMeshProjectedPosition = access.NavMeshProjectedPosition,
                    ApproachDirection = access.ApproachDirection,
                    NavMeshProjectionSucceeded = access.NavMeshProjectionSucceeded,
                    CreatedAt = access.CreatedAt,
                    CreatedWithBuild = access.CreatedWithBuild,
                    ReviewState = string.IsNullOrWhiteSpace(existing?.ReviewState) ? "unconfirmed" : existing.ReviewState
                });
            }
        }

        mapFile.LegacyPortalCandidates = synchronized;
        if (mapFile.MigrationRecord != null)
        {
            mapFile.MigrationRecord.LegacyPortalCandidateCount = synchronized.Count(candidate =>
                !string.Equals(candidate.ReviewState, "promoted", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static VanguardTacticalAuthoringSlot ConvertSlot(VanguardLegacyTacticalAuthoringSlot legacy)
    {
        return new VanguardTacticalAuthoringSlot
        {
            SlotId = legacy.SlotId,
            Position = legacy.Position,
            NavMeshProjectedPosition = legacy.NavMeshProjectedPosition,
            WatchDirection = legacy.WatchDirection,
            PhysicalPoseSnapshot = null,
            Enabled = legacy.Enabled,
            AuthoringValid = legacy.AuthoringValid,
            ValidationState = legacy.ValidationState,
            ValidationNotes = legacy.ValidationNotes,
            CreatedAt = legacy.CreatedAt,
            CreatedWithBuild = legacy.CreatedWithBuild,
            LastValidatedAt = legacy.LastValidatedAt,
            LastValidatedBuild = legacy.LastValidatedBuild,
            CompatibilityAssignmentOrderHint = legacy.Priority,
            LegacyCompatibility = new VanguardTacticalAuthoringLegacySlotCompatibility
            {
                SlotType = legacy.SlotType,
                WatchArc = legacy.WatchArc,
                AssociatedAccessId = legacy.AssociatedAccessId,
                ConnectedZoneId = legacy.ConnectedZoneId,
                MinimumSquadSize = legacy.MinimumSquadSize,
                MaximumOwnerDistance = legacy.MaximumOwnerDistance,
                RoleAffinity = legacy.RoleAffinity,
                MutualExclusionGroup = legacy.MutualExclusionGroup
            }
        };
    }

    private static VanguardTacticalAuthoringAccess ConvertCompatibilityAccess(VanguardLegacyTacticalAuthoringAccess legacy)
    {
        return new VanguardTacticalAuthoringAccess
        {
            AccessId = legacy.AccessId,
            DisplayName = legacy.DisplayName,
            Position = legacy.Position,
            NavMeshProjectedPosition = legacy.NavMeshProjectedPosition,
            ApproachDirection = legacy.ApproachDirection,
            NavMeshProjectionSucceeded = legacy.NavMeshProjectionSucceeded,
            CreatedAt = legacy.CreatedAt,
            CreatedWithBuild = legacy.CreatedWithBuild
        };
    }

    private static VanguardTacticalAuthoringAccess ConvertCompatibilityAccess(VanguardTacticalAuthoringLegacyPortalCandidate candidate)
    {
        return new VanguardTacticalAuthoringAccess
        {
            AccessId = candidate.OriginalAccessId,
            DisplayName = candidate.DisplayName,
            Position = candidate.Position,
            NavMeshProjectedPosition = candidate.NavMeshProjectedPosition,
            ApproachDirection = candidate.ApproachDirection,
            NavMeshProjectionSucceeded = candidate.NavMeshProjectionSucceeded,
            CreatedAt = candidate.CreatedAt,
            CreatedWithBuild = candidate.CreatedWithBuild
        };
    }

    private static string Identity(string zoneId, string accessId) => (zoneId ?? string.Empty) + "\n" + (accessId ?? string.Empty);
}
#endif
