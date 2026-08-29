using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Bot;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;
using Vanguard.Server.Operators.Raid.Models;
using Vanguard.Server.Operators.Raid.Requests;
using Vanguard.Server.Diagnostics;

// Responsibility: Creates the raid-time bot profile payload for each persistent Operator while preserving its stable Vanguard identity and the server-selected gameplay profile.
// Flow: The raid manifest/operator profile is validated, an SPT bot template is requested/generated, Vanguard identity/role/equipment metadata is applied, and the final bot profile is returned for client spawn.
// Authority boundary: SPT owns base bot-profile generation; Vanguard owns only Operator-specific identity/profile overlay and does not spawn or drive the bot from the server.
// Invariant: Generated profiles must remain tied to the requested owner/Operator, preserve required SPT structure, and fail clearly rather than silently returning an unrelated bot profile.
namespace Vanguard.Server.Operators.Raid.Services;

public interface IVanguardRaidBotGenerator
{
    Task<IReadOnlyList<BotBase>> GenerateAsync(MongoId sessionId, GenerateBotsRequestData request);
}

[Injectable]
public sealed class VanguardRaidBotGenerator(BotController botController) : IVanguardRaidBotGenerator
{
    public async Task<IReadOnlyList<BotBase>> GenerateAsync(MongoId sessionId, GenerateBotsRequestData request)
    {
        return (await botController.Generate(sessionId, request)).OfType<BotBase>().ToArray();
    }
}

[Injectable(InjectionType.Singleton)]
public sealed class VanguardRaidBotGenerationService(
    VanguardRaidOperatorManifestService manifestService,
    IVanguardRaidBotGenerator botGenerator,
    JsonUtil jsonUtil,
    ISptLogger<VanguardRaidBotGenerationService> logger)
{
    public async Task<IReadOnlyList<BotBase>> GenerateOperatorBotsAsync(MongoId sessionId, VanguardGenerateOperatorBotRequest request)
    {
        if (request.Info is null)
        {
            logger.Warning(VanguardServerDiagnosticsLog.Present("[VANGUARD_OPERATOR_PROFILE_GENERATED] skipped=missing_info"));
            return Array.Empty<BotBase>();
        }

        var generatedBots = (await botGenerator.GenerateAsync(sessionId, request.Info)).ToArray();
        if (generatedBots.Length == 0)
        {
            logger.Warning(VanguardServerDiagnosticsLog.Present("[VANGUARD_OPERATOR_PROFILE_GENERATED] skipped=spt_generated_zero_bots"));
            return generatedBots;
        }

        string ownerProfileId = Normalize(request.OwnerProfileId, sessionId.ToString());
        string operatorId = Normalize(request.OperatorId);
        if (string.IsNullOrWhiteSpace(ownerProfileId) || string.IsNullOrWhiteSpace(operatorId))
        {
            logger.Warning(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PROFILE_GENERATED] skipped=missing_owner_or_operator owner={ownerProfileId} operator={operatorId}"));
            return generatedBots;
        }

        var spawnProfile = await manifestService.TryLoadSpawnProfileAsync(ownerProfileId, operatorId, request.RaidSessionId);
        if (spawnProfile is null)
        {
            logger.Warning(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PROFILE_GENERATED] owner={ownerProfileId} operator={operatorId} skipped=manifest_or_profile_unavailable"));
            return generatedBots;
        }

        ApplyOperatorSnapshot(generatedBots[0], spawnProfile);
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PROFILE_GENERATED] owner={ownerProfileId} operator={operatorId} nickname={generatedBots[0].Info?.Nickname ?? spawnProfile.Snapshot.DisplayName} items={generatedBots[0].Inventory?.Items?.Count ?? 0} inventoryRoot={generatedBots[0].Inventory?.Equipment} reason={spawnProfile.Reason}"));
        return generatedBots;
    }

    private void ApplyOperatorSnapshot(BotBase generatedBot, VanguardRaidOperatorSpawnProfile spawnProfile)
    {
        var snapshot = spawnProfile.Snapshot;
        ApplyJsonBackedProfileFields(generatedBot, spawnProfile);

        int generatedLevelBefore = generatedBot.Info?.Level ?? 0;
        int generatedExperienceBefore = generatedBot.Info?.Experience ?? 0;
        ApplyIdentity(generatedBot, snapshot);
        int generatedLevelAfter = generatedBot.Info?.Level ?? 0;
        int generatedExperienceAfter = generatedBot.Info?.Experience ?? 0;
        bool progressionParity = generatedLevelAfter == Math.Max(snapshot.Level, 1)
            && generatedExperienceAfter == Math.Max(snapshot.Experience, 0);
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[{VanguardBuildVersion.CareerXpProfileParityStatusTag}] phase=raid_profile_progression_apply; operator={snapshot.OperatorId}; generatedLevelBefore={generatedLevelBefore}; generatedCumulativeXpBefore={generatedExperienceBefore}; persistentLevel={snapshot.Level}; persistentCumulativeXp={snapshot.Experience}; generatedLevelAfter={generatedLevelAfter}; generatedCumulativeXpAfter={generatedExperienceAfter}; exactPersistentProgression={Bool(progressionParity)}; cumulativeExperienceSemantics=from_level_1; randomGeneratedXpPreserved=false; tag={VanguardBuildVersion.CareerXpProfileParityStatusTag}"));

        ApplyInventory(generatedBot, spawnProfile);
    }

    private void ApplyJsonBackedProfileFields(BotBase generatedBot, VanguardRaidOperatorSpawnProfile spawnProfile)
    {
        if (spawnProfile.OperatorPmcJson is null)
        {
            return;
        }

        try
        {
            string generatedJson = jsonUtil.Serialize(generatedBot, indented: true) ?? "{}";
            JsonObject generatedRoot = JsonNode.Parse(generatedJson)?.AsObject() ?? new JsonObject();
            CopyProfileSection(spawnProfile.OperatorPmcJson, generatedRoot, "Health");
            CopyProfileSection(spawnProfile.OperatorPmcJson, generatedRoot, "Skills");
            CopyProfileSection(spawnProfile.OperatorPmcJson, generatedRoot, "Customization");

            BotBase? materialized = jsonUtil.Deserialize<BotBase>(generatedRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            if (materialized is null)
            {
                return;
            }

            generatedBot.Health = materialized.Health;
            generatedBot.Skills = materialized.Skills;
            generatedBot.Customization = materialized.Customization;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            logger.Warning(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PROFILE_GENERATED] operator={spawnProfile.Snapshot.OperatorId} json_profile_sections=skipped reason={exception.GetType().Name}:{Compact(exception.Message)}"));
        }
    }

    private static void CopyProfileSection(JsonObject sourcePmc, JsonObject generatedRoot, string sectionName)
    {
        JsonNode? source = GetCaseInsensitive(sourcePmc, sectionName);
        if (source is null)
        {
            return;
        }

        RemoveCaseInsensitive(generatedRoot, sectionName);
        generatedRoot[sectionName] = source.DeepClone();
    }

    private static JsonNode? GetCaseInsensitive(JsonObject source, string name)
    {
        string? actual = source.Select(property => property.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        return actual is null ? null : source[actual];
    }

    private static void RemoveCaseInsensitive(JsonObject obj, string name)
    {
        string? actual = obj.Select(property => property.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        if (actual is not null)
        {
            obj.Remove(actual);
        }
    }

    private static void ApplyIdentity(BotBase generatedBot, VanguardRaidOperatorSnapshot snapshot)
    {
        generatedBot.Info ??= new Info();
        generatedBot.Info.Nickname = Normalize(snapshot.Callsign, snapshot.DisplayName, snapshot.OperatorId);
        generatedBot.Info.Side = Normalize(snapshot.Side, generatedBot.Info.Side, "Usec");

        // VANGUARD_RAID_SPAWN_STATUS
        // progression convergence: persistent Operator progression is authoritative. Info.Experience is EFT cumulative
        // profile XP from level 1; the randomly generated SPT bot value must never override it.
        generatedBot.Info.Level = Math.Max(snapshot.Level, 1);
        generatedBot.Info.Experience = Math.Max(snapshot.Experience, 0);
    }

    private void ApplyInventory(BotBase generatedBot, VanguardRaidOperatorSpawnProfile spawnProfile)
    {
        if (spawnProfile.OperatorInventoryJson is null)
        {
            return;
        }

        string? equipmentId = VanguardRaidOperatorManifestService.GetString(spawnProfile.OperatorInventoryJson, "equipment")
            ?? VanguardRaidOperatorManifestService.GetString(spawnProfile.OperatorInventoryJson, "Equipment");
        JsonArray? items = VanguardRaidOperatorManifestService.GetArray(spawnProfile.OperatorInventoryJson, "items")
            ?? VanguardRaidOperatorManifestService.GetArray(spawnProfile.OperatorInventoryJson, "Items");
        if (string.IsNullOrWhiteSpace(equipmentId) || items is null || items.Count == 0)
        {
            return;
        }

        var itemSnapshots = new List<VanguardRaidInventoryItemSnapshot>();
        foreach (JsonObject item in items.OfType<JsonObject>())
        {
            string? id = VanguardRaidOperatorManifestService.GetItemId(item);
            string? template = VanguardRaidOperatorManifestService.GetTemplateId(item);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(template))
            {
                continue;
            }

            itemSnapshots.Add(new VanguardRaidInventoryItemSnapshot(
                id,
                template,
                VanguardRaidOperatorManifestService.GetString(item, "parentId") ?? VanguardRaidOperatorManifestService.GetString(item, "ParentId"),
                VanguardRaidOperatorManifestService.GetString(item, "slotId") ?? VanguardRaidOperatorManifestService.GetString(item, "SlotId"),
                VanguardRaidOperatorManifestService.Raw(GetCaseInsensitive(item, "location")),
                VanguardRaidOperatorManifestService.Raw(GetCaseInsensitive(item, "upd"))));
        }

        if (!itemSnapshots.Any(item => string.Equals(item.Id, equipmentId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // Raid bot construction only needs the equipment root and items attached to it.  A direct
        // Operator equipment session is built from a vanilla inventory screen and can contain player
        // stash, sorting table and UI-only roots in the descriptor snapshot.  Passing the full tree to
        // The raid bot Profile constructor can trip EFT clone logic on unrelated stash content, so the current constructor guard
        // materializes a raid-safe equipment subtree while keeping the persistent off-raid profile intact.
        var raidItems = BuildRaidEquipmentTree(itemSnapshots, equipmentId).ToArray();
        if (raidItems.Length == 0)
        {
            return;
        }

        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PROFILE_GENERATED] operator={spawnProfile.Snapshot.OperatorId} raid_inventory_pruned sourceItems={itemSnapshots.Count} raidItems={raidItems.Length} equipment={equipmentId}"));
        generatedBot.Inventory ??= new BotBaseInventory();
        generatedBot.Inventory.Equipment = new MongoId(equipmentId);
        generatedBot.Inventory.Items = raidItems.Select(CreateInventoryItem).ToList();
    }

    private static IReadOnlyList<VanguardRaidInventoryItemSnapshot> BuildRaidEquipmentTree(IReadOnlyList<VanguardRaidInventoryItemSnapshot> sourceItems, string equipmentId)
    {
        var byId = sourceItems
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (!byId.ContainsKey(equipmentId))
        {
            return Array.Empty<VanguardRaidInventoryItemSnapshot>();
        }

        var childrenByParent = sourceItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ParentId))
            .GroupBy(item => item.ParentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var selected = new Dictionary<string, VanguardRaidInventoryItemSnapshot>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(equipmentId);

        while (queue.Count > 0)
        {
            string id = queue.Dequeue();
            if (!byId.TryGetValue(id, out var item) || selected.ContainsKey(id))
            {
                continue;
            }

            selected[id] = item;
            if (!childrenByParent.TryGetValue(id, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                queue.Enqueue(child.Id);
            }
        }

        return selected.Values.ToArray();
    }

    private Item CreateInventoryItem(VanguardRaidInventoryItemSnapshot snapshot)
    {
        return new Item
        {
            Id = new MongoId(snapshot.Id),
            Template = new MongoId(snapshot.TemplateId),
            ParentId = snapshot.ParentId!,
            SlotId = snapshot.SlotId!,
            Location = DeserializeLocation(snapshot.LocationJson),
            Upd = DeserializeUpd(snapshot.UpdJson),
        };
    }

    private static object? DeserializeLocation(string? locationJson)
    {
        if (string.IsNullOrWhiteSpace(locationJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(locationJson);
        return ConvertJsonElement(document.RootElement);
    }

    private Upd? DeserializeUpd(string? updJson)
    {
        // Upd contains SPT types such as MongoId (for example sptPresetId). Reuse the
        // canonical SPT JsonUtil converters instead of raw System.Text.Json so an
        // otherwise valid persistent Operator item cannot abort raid bot generation.
        return string.IsNullOrWhiteSpace(updJson) ? null : jsonUtil.Deserialize<Upd>(updJson);
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => ConvertJsonElement(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => ConvertJsonNumber(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        };
    }

    private static object ConvertJsonNumber(JsonElement element)
    {
        if (element.TryGetInt32(out int intValue))
        {
            return intValue;
        }

        if (element.TryGetInt64(out long longValue))
        {
            return longValue;
        }

        if (element.TryGetDecimal(out decimal decimalValue))
        {
            return decimalValue;
        }

        return element.GetDouble();
    }

    private static string Normalize(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Compact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        string compact = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return compact.Length <= 180 ? compact : compact[..180] + "...";
    }
}
