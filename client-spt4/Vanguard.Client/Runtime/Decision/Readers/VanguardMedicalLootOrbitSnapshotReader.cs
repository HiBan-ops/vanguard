#if SPT_CLIENT
using System;
using System.Collections;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Integrations.Orbit;

// Responsibility: Reads and normalizes live evidence for Medical Loot Orbit Snapshot Reader in the decision snapshot pipeline.
// Flow: Live EFT/Fika/Vanguard objects are inspected defensively, normalized into a bounded snapshot, then handed to policy/decision code.
// Authority boundary: Read-only observer; it does not create missing truth or mutate the game state it inspects.
// Invariant: Missing/stale evidence degrades explicitly and reader failures must not silently fabricate an actionable state.
namespace Vanguard.Client.Runtime.Decision;

internal sealed partial class VanguardOperatorDecisionSnapshotBuilder
{
    private static VanguardLootDecisionSnapshot CaptureLoot(BotOwner? botOwner)
    {
        if (botOwner == null)
        {
            return new VanguardLootDecisionSnapshot { Classification = "loot_no_botowner" };
        }

        object? lootingBrain = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "LootingBots.Components.LootingBrain");
        object? lootFinder = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "LootingBots.Components.LootFinder");
        bool typeLoaded = VanguardOperatorRuntimeAuditReflection.TypeExists("LootingBots.Components.LootingBrain");
        bool componentPresent = lootingBrain != null || lootFinder != null;
        object? stats = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "Stats");
        bool? taskRunning = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "LootTaskRunning"));
        bool? botLooting = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "IsBotLooting"));

        return new VanguardLootDecisionSnapshot
        {
            TypeLoaded = typeLoaded,
            ComponentPresent = componentPresent,
            ComponentType = VanguardOperatorRuntimeAuditReflection.TypeName(lootingBrain),
            FinderType = VanguardOperatorRuntimeAuditReflection.TypeName(lootFinder),
            BrainEnabled = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "IsBrainEnabled")),
            BotLooting = botLooting,
            LootTaskRunning = taskRunning,
            HasActiveLootable = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "HasActiveLootable")),
            ActiveLootType = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "ActiveLootType")),
            DistanceToLoot = Float(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "DistanceToLoot")),
            HasFreeSpace = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootingBrain, "HasFreeSpace")),
            AvailableGridSpaces = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(stats, "AvailableGridSpaces")),
            ScanScheduled = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootFinder, "IsScheduledScan")),
            ScanRunning = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(lootFinder, "IsScanRunning")),
            Classification = ClassifyLoot(typeLoaded, componentPresent, taskRunning, botLooting)
        };
    }

    private static VanguardOrbitDecisionSnapshot CaptureOrbit(BotOwner? botOwner, string botProfileId)
    {
        if (botOwner == null)
        {
            return new VanguardOrbitDecisionSnapshot { Classification = "orbit_no_botowner" };
        }

        if (VanguardOrbitAuthorityBoundaryService.TryGetExcludedSnapshot(botOwner, botProfileId, DateTimeOffset.UtcNow, out VanguardOrbitDecisionSnapshot excludedSnapshot))
        {
            return excludedSnapshot;
        }

        Type? telemetryType = VanguardOperatorRuntimeAuditReflection.FindType("Orbit.Api.OrbitTelemetry");
        bool telemetryLoaded = telemetryType != null;
        if (!telemetryLoaded)
        {
            return new VanguardOrbitDecisionSnapshot { TelemetryLoaded = false, Classification = "orbit_telemetry_missing" };
        }

        string profileId = VanguardOperatorRuntimeAuditReflection.FirstNonEmpty(
            Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "ProfileId")),
            botProfileId);
        bool available = VanguardOperatorRuntimeAuditReflection.GetStaticMember(telemetryType, "IsAvailable") is bool trueValue && trueValue;
        object? objective = available ? VanguardOperatorRuntimeAuditReflection.InvokeStatic(telemetryType, "GetBotObjective", profileId) : null;
        if (objective == null)
        {
            return new VanguardOrbitDecisionSnapshot
            {
                TelemetryLoaded = true,
                Available = available,
                Active = false,
                Classification = available ? "orbit_no_objective" : "orbit_unavailable"
            };
        }

        var objectiveVector = VectorFromComponents(
            VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objective, "ObjectiveX"),
            VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objective, "ObjectiveY"),
            VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objective, "ObjectiveZ"));
        string status = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objective, "Status"));
        string category = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objective, "Category"));

        return new VanguardOrbitDecisionSnapshot
        {
            TelemetryLoaded = true,
            Available = available,
            Active = true,
            Status = status,
            Category = category,
            IsLeader = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objective, "IsLeader")),
            Objective = objectiveVector,
            ExtractReason = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(objective, "ExtractReason")),
            Classification = string.Equals(status, "none", StringComparison.OrdinalIgnoreCase) ? "orbit_active" : $"orbit_{SanitizeKey(status)}"
        };
    }

    private static string ClassifyLoot(bool typeLoaded, bool componentPresent, bool? taskRunning, bool? botLooting)
    {
        if (!typeLoaded)
        {
            return "loot_type_missing";
        }

        if (!componentPresent)
        {
            return "loot_component_missing";
        }

        if (taskRunning == true || botLooting == true)
        {
            return "loot_active";
        }

        return "loot_available_idle";
    }
}
#endif
