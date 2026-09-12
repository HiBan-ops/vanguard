using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using Vanguard.Server.Diagnostics;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Responses;
using Vanguard.Server.Operators.Storage;

// Responsibility: builds strict read-only Squad Preview visual projections from existing persistent Operator truth.
// Flow: session-scoped raw store reads -> known/ActiveService membership validation -> existing EFT profile read ->
// GClass2214-compatible Info/Customization/Equipment projection -> source readback stability check -> response.
// Authority boundary: read-only presentation. This service never creates a profile, normalizes data, runs migration,
// enters Operator Inventory Mode, generates a raid bot/manifest, quarantines files or writes any persistent state.
// Invariant: unavailable/corrupt/inconsistent truth is omitted and diagnosed; persistent truth remains untouched.
namespace Vanguard.Server.Operators.Services;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorPreviewVisualService(
    VanguardOperatorStore store,
    VanguardOperatorVisualIdentityService visualIdentityService,
    ISptLogger<VanguardOperatorPreviewVisualService> logger)
{
    public const string StatusTag = "SQUAD_PREVIEW_READ_MODEL";
    private const int MaxRequestedOperators = 4;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<VanguardOperatorPreviewVisualResponse> GetAsync(
        string requestedProfileId,
        IReadOnlyCollection<string>? requestedOperatorIds)
    {
        string profileId = (requestedProfileId ?? string.Empty).Trim();
        var requested = (requestedOperatorIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxRequestedOperators + 1)
            .ToArray();

        var response = new VanguardOperatorPreviewVisualResponse
        {
            RequestedProfileId = profileId,
            StorageProfileId = profileId,
            RequestedOperatorCount = requested.Length,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            BuildLabel = VanguardBuildVersion.BuildLabel
        };

        if (string.IsNullOrWhiteSpace(profileId))
        {
            return Fail(response, "requested_profile_missing");
        }

        if (requested.Length == 0)
        {
            response.Success = true;
            response.Reason = "no_operators_requested";
            return response;
        }

        if (requested.Length > MaxRequestedOperators)
        {
            return Fail(response, "requested_operator_limit_exceeded");
        }

        // Do not call ResolveStorageProfileIdAsync/EnsureProfileStorageInitializedAsync here: preview reads must never create storage.
        if (!store.HasOperatorStore(profileId))
        {
            return Fail(response, "operator_store_missing");
        }

        string? operatorsJsonBefore = await store.ReadOperatorsJsonReadOnlyAsync(profileId);
        string? activeServiceJsonBefore = await store.ReadActiveServiceJsonReadOnlyAsync(profileId);
        if (operatorsJsonBefore is null || activeServiceJsonBefore is null)
        {
            return Fail(response, "preview_authority_file_missing_or_unreadable");
        }

        IReadOnlyList<VanguardOperatorProfile?>? operators;
        IReadOnlyList<VanguardActiveServiceRecord?>? activeService;
        try
        {
            operators = JsonSerializer.Deserialize<List<VanguardOperatorProfile?>>(operatorsJsonBefore, ReadOptions);
            activeService = JsonSerializer.Deserialize<List<VanguardActiveServiceRecord?>>(activeServiceJsonBefore, ReadOptions);
        }
        catch (JsonException exception)
        {
            logger.Warning(VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] phase=authority_parse_rejected; storage={profileId}; type={exception.GetType().Name}; mutation=false; quarantine=false; action=omit_preview"));
            return Fail(response, "preview_authority_json_invalid");
        }

        if (operators is null || activeService is null)
        {
            return Fail(response, "preview_authority_deserialization_null");
        }

        var operatorById = operators
            .Where(op => op is not null && !string.IsNullOrWhiteSpace(op.OperatorId))
            .Select(op => op!)
            .GroupBy(op => op.OperatorId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var activeMembershipCounts = activeService
            .Where(record => record is not null && !string.IsNullOrWhiteSpace(record.OperatorId))
            .Select(record => record!.OperatorId)
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (string operatorId in requested)
        {
            if (!operatorById.TryGetValue(operatorId, out var operatorProfile))
            {
                Reject(response, profileId, operatorId, "operator_not_owned_by_profile");
                continue;
            }

            // Preview membership is ActiveService-scoped. Released/world-only people keep persistent truth but are not rendered.
            if (!activeMembershipCounts.TryGetValue(operatorId, out int activeMembershipCount))
            {
                Reject(response, profileId, operatorId, "operator_not_in_active_service");
                continue;
            }
            if (activeMembershipCount != 1)
            {
                Reject(response, profileId, operatorId, "operator_active_service_membership_ambiguous");
                continue;
            }

            if (operatorProfile.VisualIdentity is null)
            {
                Reject(response, profileId, operatorId, "persistent_visual_identity_missing");
                continue;
            }

            try
            {
                visualIdentityService.ValidatePersistentIdentity(operatorProfile.VisualIdentity, operatorProfile.Identity.Side);
            }
            catch (Exception exception)
            {
                Reject(response, profileId, operatorId, "persistent_visual_identity_invalid_" + exception.GetType().Name);
                continue;
            }

            string? profileJsonBefore = await store.ReadOperatorInventoryProfileJsonReadOnlyAsync(profileId, operatorId);
            if (profileJsonBefore is null)
            {
                Reject(response, profileId, operatorId, "persistent_operator_profile_missing_or_unreadable");
                continue;
            }

            if (!TryBuildVisualProjection(operatorProfile, profileJsonBefore, out JsonObject? visual, out string fingerprint, out string reason))
            {
                Reject(response, profileId, operatorId, reason);
                continue;
            }

            // Writers use atomic replacement. A second exact read prevents us from publishing a projection while that authority changed.
            string? profileJsonAfter = await store.ReadOperatorInventoryProfileJsonReadOnlyAsync(profileId, operatorId);
            if (!string.Equals(profileJsonBefore, profileJsonAfter, StringComparison.Ordinal))
            {
                Reject(response, profileId, operatorId, "persistent_operator_profile_changed_during_read");
                continue;
            }

            response.Operators.Add(new VanguardOperatorPreviewVisualProjection
            {
                OperatorId = operatorProfile.OperatorId,
                DisplayName = FirstNonEmpty(operatorProfile.Identity.DisplayName, operatorProfile.Identity.Callsign, operatorProfile.OperatorId),
                Side = operatorProfile.Identity.Side,
                VisualRevision = "preview-v1-" + fingerprint[..16],
                VisualFingerprint = fingerprint,
                Visual = visual!
            });
        }

        // Prove the owner/membership authorities were stable across the complete projection read.
        string? operatorsJsonAfter = await store.ReadOperatorsJsonReadOnlyAsync(profileId);
        string? activeServiceJsonAfter = await store.ReadActiveServiceJsonReadOnlyAsync(profileId);
        if (!string.Equals(operatorsJsonBefore, operatorsJsonAfter, StringComparison.Ordinal)
            || !string.Equals(activeServiceJsonBefore, activeServiceJsonAfter, StringComparison.Ordinal))
        {
            response.Operators.Clear();
            response.UnavailableOperatorIds = requested.ToList();
            return Fail(response, "preview_authority_changed_during_read");
        }

        response.ReturnedOperatorCount = response.Operators.Count;
        response.Success = response.ReturnedOperatorCount > 0;
        response.Reason = response.ReturnedOperatorCount == response.RequestedOperatorCount
            ? "preview_read_only_complete"
            : response.ReturnedOperatorCount > 0
                ? "preview_read_only_partial"
                : "preview_unavailable";

        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] phase=read_complete; requested={profileId}; storage={profileId}; requestedOperators={response.RequestedOperatorCount}; returned={response.ReturnedOperatorCount}; unavailable={response.UnavailableOperatorIds.Count}; reason={response.Reason}; persistentReadsStable=true; mutation=false; profileCreate=false; normalize=false; migration=false; quarantine=false; inventoryMode=false; raidGeneration=false"));
        return response;
    }

    private bool TryBuildVisualProjection(
        VanguardOperatorProfile operatorProfile,
        string profileJson,
        out JsonObject? visual,
        out string fingerprint,
        out string reason)
    {
        visual = null;
        fingerprint = string.Empty;
        reason = "unknown";

        try
        {
            JsonObject? root = JsonNode.Parse(profileJson) as JsonObject;
            if (root is null
                || !TryGetObject(root, "characters", out JsonObject? characters)
                || !TryGetObject(characters!, "pmc", out JsonObject? pmc)
                || !TryGetObject(pmc!, "Info", out JsonObject? profileInfo)
                || !TryGetObject(pmc!, "Health", out JsonObject? health)
                || !TryGetObject(pmc!, "Customization", out JsonObject? customization)
                || !TryGetObject(pmc!, "Inventory", out JsonObject? inventory)
                || !TryGetArray(inventory!, "items", out JsonArray? items))
            {
                reason = "persistent_operator_profile_visual_roots_missing";
                return false;
            }

            VanguardOperatorVisualIdentity identity = operatorProfile.VisualIdentity!;
            if (!CustomizationMatches(customization!, identity))
            {
                reason = "persistent_visual_identity_profile_mismatch";
                return false;
            }

            string? equipmentId = GetString(inventory!, "equipment");
            if (string.IsNullOrWhiteSpace(equipmentId))
            {
                reason = "persistent_equipment_root_missing";
                return false;
            }

            JsonArray equipmentItems = CollectEquipmentTree(items!, equipmentId);
            if (equipmentItems.Count == 0)
            {
                reason = "persistent_equipment_tree_missing";
                return false;
            }

            JsonObject info = BuildRendererInfo(profileInfo!, health!);
            JsonObject equipment = new()
            {
                ["Id"] = equipmentId,
                ["Items"] = equipmentItems
            };
            visual = new JsonObject
            {
                ["Info"] = info,
                ["Customization"] = BuildRendererCustomization(identity),
                ["Equipment"] = equipment
            };

            string canonical = visual.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            reason = "preview_visual_ready";
            return true;
        }
        catch (Exception exception)
        {
            reason = "preview_visual_parse_exception_" + exception.GetType().Name;
            return false;
        }
    }

    private static JsonObject BuildRendererCustomization(VanguardOperatorVisualIdentity identity) => new()
    {
        ["Head"] = identity.Head,
        ["Body"] = identity.Body,
        ["Feet"] = identity.Feet,
        ["Hands"] = identity.Hands,
        ["Voice"] = identity.Voice
    };

    private static JsonObject BuildRendererInfo(JsonObject profileInfo, JsonObject health)
    {
        var info = new JsonObject();
        foreach (string key in new[]
        {
            "Nickname", "Side", "Level", "PrestigeLevel", "MemberCategory", "SelectedMemberCategory",
            "SavageLockTime", "SavageNickname", "GameVersion", "HasCoopExtension"
        })
        {
            string? actual = FindPropertyName(profileInfo, key);
            if (actual is not null && profileInfo[actual] is JsonNode value)
            {
                info[key] = value.DeepClone();
            }
        }

        info["Health"] = health.DeepClone();
        return info;
    }

    private static JsonArray CollectEquipmentTree(JsonArray allItems, string equipmentId)
    {
        var byParent = new Dictionary<string, List<JsonObject>>(StringComparer.OrdinalIgnoreCase);
        JsonObject? root = null;
        foreach (JsonObject item in allItems.OfType<JsonObject>())
        {
            string? id = GetString(item, "_id");
            if (string.Equals(id, equipmentId, StringComparison.OrdinalIgnoreCase))
            {
                root = item;
            }

            string? parentId = GetString(item, "parentId");
            if (string.IsNullOrWhiteSpace(parentId))
            {
                continue;
            }

            if (!byParent.TryGetValue(parentId, out List<JsonObject>? children))
            {
                children = [];
                byParent[parentId] = children;
            }
            children.Add(item);
        }

        if (root is null)
        {
            return new JsonArray();
        }

        var output = new JsonArray();
        var pending = new Queue<JsonObject>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            JsonObject item = pending.Dequeue();
            string? id = GetString(item, "_id");
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                continue;
            }

            output.Add(item.DeepClone());
            if (byParent.TryGetValue(id, out List<JsonObject>? children))
            {
                foreach (JsonObject child in children.OrderBy(child => GetString(child, "_id") ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                {
                    pending.Enqueue(child);
                }
            }
        }

        return output;
    }

    private static bool CustomizationMatches(JsonObject customization, VanguardOperatorVisualIdentity identity) =>
        string.Equals(GetString(customization, "Head"), identity.Head, StringComparison.OrdinalIgnoreCase)
        && string.Equals(GetString(customization, "Body"), identity.Body, StringComparison.OrdinalIgnoreCase)
        && string.Equals(GetString(customization, "Feet"), identity.Feet, StringComparison.OrdinalIgnoreCase)
        && string.Equals(GetString(customization, "Hands"), identity.Hands, StringComparison.OrdinalIgnoreCase)
        && string.Equals(GetString(customization, "Voice"), identity.Voice, StringComparison.OrdinalIgnoreCase);

    private void Reject(VanguardOperatorPreviewVisualResponse response, string profileId, string operatorId, string reason)
    {
        response.UnavailableOperatorIds.Add(operatorId);
        logger.Warning(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] phase=operator_omitted; storage={profileId}; operator={operatorId}; reason={reason}; mutation=false; persistentTruthUntouched=true"));
    }

    private static VanguardOperatorPreviewVisualResponse Fail(VanguardOperatorPreviewVisualResponse response, string reason)
    {
        response.Success = false;
        response.Reason = reason;
        response.ReturnedOperatorCount = 0;
        return response;
    }

    private static bool TryGetObject(JsonObject parent, string name, out JsonObject? value)
    {
        value = null;
        string? actual = FindPropertyName(parent, name);
        if (actual is null || parent[actual] is not JsonObject obj)
        {
            return false;
        }
        value = obj;
        return true;
    }

    private static bool TryGetArray(JsonObject parent, string name, out JsonArray? value)
    {
        value = null;
        string? actual = FindPropertyName(parent, name);
        if (actual is null || parent[actual] is not JsonArray array)
        {
            return false;
        }
        value = array;
        return true;
    }

    private static string? GetString(JsonObject parent, string name)
    {
        string? actual = FindPropertyName(parent, name);
        if (actual is null || parent[actual] is null)
        {
            return null;
        }
        try { return parent[actual]!.GetValue<string>(); }
        catch { return parent[actual]!.ToString(); }
    }

    private static string? FindPropertyName(JsonObject parent, string name)
    {
        foreach (KeyValuePair<string, JsonNode?> property in parent)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Key;
            }
        }
        return null;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "Unknown Operator";
}
