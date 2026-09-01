using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using HarmonyLib;
#endif

// Responsibility: Loads the temporary SPT player-profile representation used to edit an Operator inventory and restores the real player profile after the editing session ends.
// Flow: The loader enters server inventory mode, requests/reloads the redirected profile for the UI, and on exit forces player-profile reconstruction whenever the server exit converges successfully, independent of the best-effort direct-commit result.
// Authority boundary: The server inventory-mode service owns Operator inventory persistence; this client component only drives profile presentation/reload and never synthesizes wallet state.
// Invariant: A successful exit must return the client to the real player profile, and failed direct commit must not strand the menu in redirected/stale state.
namespace Vanguard.Client.UI.OffRaid.Inventory;

internal static class VanguardOperatorInventoryProfileLoader
{
    public static bool TryLoadFirstOperatorProfile(out object? profile, out string? profileId, out string reason)
    {
        profile = null;
        profileId = null;
        reason = "unknown";

#if SPT_CLIENT
        if (!TryBuildProfilesFromServer(out Array? profiles, out string? activeProfileId, out reason) || profiles == null || profiles.Length == 0)
        {
            return false;
        }

        profile = profiles.GetValue(0);
        profileId = activeProfileId ?? ResolveMember(profile!, "Id")?.ToString();
        reason = "ok";
        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS",
            $"operator_profile_loaded descriptors={profiles.Length}; profile={profileId ?? "<none>"}; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}");
        return true;
#else
        reason = "spt_client_not_defined";
        return false;
#endif
    }

#if SPT_CLIENT
    public static bool TryBuildProfilesFromServer(out Array? profiles, out string? activeProfileId, out string reason)
    {
        profiles = null;
        activeProfileId = null;
        reason = "unknown";

        try
        {
            string raw = VanguardOperatorInventoryModeClientState.GetProfileDescriptorsJson();
            JToken payload = SelectPayloadToken(UnwrapStringToken(JToken.Parse(raw)));
            if (payload is not JArray descriptors || descriptors.Count == 0)
            {
                reason = "empty_operator_profile_descriptors";
                VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_SESSION_PROFILE_NORMALIZATION_STATUS", reason);
                return false;
            }

            Type profileType = AccessTools.TypeByName("EFT.Profile") ?? AccessTools.TypeByName("Profile")
                ?? throw new InvalidOperationException("Profile type not found.");
            ConstructorInfo constructor = profileType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .First(ctor => ctor.GetParameters().Length == 1);
            Type descriptorType = constructor.GetParameters()[0].ParameterType;
            profiles = Array.CreateInstance(profileType, descriptors.Count);

            for (int i = 0; i < descriptors.Count; i++)
            {
                if (descriptors[i] is not JObject descriptorObject)
                {
                    reason = $"descriptor_{i}_not_object";
                    return false;
                }

                JObject normalized = NormalizeProfileDescriptor(descriptorObject, i);
                string normalizedProfileId = ReadString(normalized, "_id") ?? "<none>";
                int normalizedItemCount = ReadArray(ReadObject(normalized, "Inventory"), "items")?.Count ?? 0;
                VanguardClientDiagnosticsLog.Info(
                    "VANGUARD_OPERATOR_SESSION_PROFILE_NORMALIZATION_STATUS",
                    $"descriptor_normalized index={i}; profile={normalizedProfileId}; items={normalizedItemCount}");

                object? descriptor = normalized.ToObject(descriptorType);
                if (descriptor == null)
                {
                    reason = $"descriptor_{i}_deserialization_null";
                    return false;
                }

                VanguardClientDiagnosticsLog.Info(
                    "VANGUARD_OPERATOR_SESSION_PROFILE_NORMALIZATION_STATUS",
                    $"profile_ctor_begin index={i}; descriptorType={descriptorType.FullName ?? descriptorType.Name}");

                object profile;
                try
                {
                    profile = InvokeProfileConstructor(constructor, descriptor, i);
                }
                catch (TargetInvocationException)
                {
                    JObject retryNormalized = (JObject)normalized.DeepClone();
                    StripDogTagCustomization(retryNormalized);
                    SanitizeInventoryReferences(EnsureObject(retryNormalized, "Inventory"));
                    VanguardClientDiagnosticsLog.Warning(
                        "VANGUARD_OPERATOR_INVENTORY_TREE_REPAIR_STATUS",
                        $"profile_ctor_retry_without_dogtag index={i}; profile={normalizedProfileId}");
                    object? retryDescriptor = retryNormalized.ToObject(descriptorType);
                    if (retryDescriptor == null)
                    {
                        reason = $"descriptor_{i}_retry_deserialization_null";
                        return false;
                    }

                    profile = InvokeProfileConstructor(constructor, retryDescriptor, i);
                }

                profiles.SetValue(profile, i);
                if (i == 0)
                {
                    activeProfileId = ResolveMember(profile, "Id")?.ToString();
                }

                object? ctorProfileId = ResolveMember(profile, "Id") ?? "<none>";
                VanguardClientDiagnosticsLog.Info(
                    "VANGUARD_OPERATOR_SESSION_PROFILE_NORMALIZATION_STATUS",
                    $"profile_ctor_success index={i}; profile={ctorProfileId}");
            }

            reason = "ok";
            return true;
        }
        catch (Exception exception)
        {
            Exception root = Unwrap(exception);
            reason = root.GetType().Name + ":" + root.Message;
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_SESSION_PROFILE_NORMALIZATION_STATUS",
                $"operator_profile_build_failed reason={reason}; wrapper={exception.GetType().Name}; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; stack={CompactStack(root)}");
            return false;
        }
    }

    private static object InvokeProfileConstructor(ConstructorInfo constructor, object descriptor, int index)
    {
        try
        {
            return constructor.Invoke(new[] { descriptor });
        }
        catch (TargetInvocationException exception)
        {
            Exception root = Unwrap(exception);
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_SESSION_PROFILE_NORMALIZATION_STATUS",
                $"profile_ctor_inner_exception index={index}; type={root.GetType().Name}; message={root.Message}; stack={CompactStack(root)}");
            throw;
        }
    }

    private static JObject NormalizeProfileDescriptor(JObject descriptor, int index)
    {
        JObject result = (JObject)descriptor.DeepClone();
        string profileId = FirstNonEmpty(ReadString(result, "_id"), VanguardOperatorInventoryModeClientState.InventoryProfileId, "vanguard-operator-session-profile");
        SetIfMissing(result, "_id", profileId);
        SetIfMissing(result, "aid", profileId);
        SetIfMissing(result, "savage", profileId + "scav");
        SetIfMissing(result, "karmaValue", 0);

        JObject info = EnsureObject(result, "Info");
        SetIfMissing(info, "Nickname", VanguardOperatorInventoryModeClientState.OperatorDisplayName ?? VanguardOperatorInventoryModeClientState.OperatorId ?? "Operator");
        SetIfMissing(info, "LowerNickname", (ReadString(info, "Nickname") ?? "operator").ToLowerInvariant());
        SetIfMissing(info, "Side", "Usec");
        SetIfMissing(info, "Voice", "usec_1");
        SetIfMissing(info, "Level", 1);
        SetIfMissing(info, "Experience", 0);
        SetIfMissing(info, "RegistrationDate", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        EnsureObject(result, "Customization");
        EnsureObject(result, "Encyclopedia");
        EnsureHealth(result);
        EnsureInventory(result, profileId, index);
        EnsureArray(result, "InsuredItems");
        EnsureSkills(result);
        EnsureObject(result, "Notes");
        EnsureObject(result, "TaskConditionCounters");
        EnsureArray(result, "Quests");
        EnsureObject(result, "Achievements");
        EnsureObject(result, "Prestige");
        EnsureObject(result, "Variables");
        JObject unlocked = EnsureObject(result, "UnlockedInfo");
        EnsureArray(unlocked, "unlockedProductionRecipe");
        JObject moneyLimit = EnsureObject(result, "moneyTransferLimitData");
        SetIfMissing(moneyLimit, "nextResetTime", 0);
        SetIfMissing(moneyLimit, "remainingLimit", 0);
        SetIfMissing(moneyLimit, "totalLimit", 0);
        SetIfMissing(moneyLimit, "resetInterval", 0);
        EnsureArray(result, "Bonuses");
        EnsureObject(result, "Hideout");
        NormalizeRagfairInfoForTechnicalProfile(result, index);
        EnsureObject(result, "WishList");
        EnsureStats(result);
        EnsureObject(result, "CheckedMagazines");
        EnsureArray(result, "CheckedChambers");
        EnsureObject(result, "TradersInfo");
        return result;
    }

    private static void NormalizeRagfairInfoForTechnicalProfile(JObject descriptor, int index)
    {
        JObject ragfairInfo = EnsureObject(descriptor, "RagfairInfo");
        JArray offers = EnsureArray(ragfairInfo, "offers");
        int normalizedDateCount = 0;

        foreach (JObject offer in offers.OfType<JObject>())
        {
            normalizedDateCount += NormalizeUnixDateField(offer, "startTime");
            normalizedDateCount += NormalizeUnixDateField(offer, "endTime");
        }

        if (normalizedDateCount == 0)
        {
            return;
        }

        // Preserve the player's complete Ragfair authority projection, including live
        // offers. SPT can expose offer timestamps as Unix integers while EFT's strict
        // CompleteProfileDescriptor expects DateTime values. Coerce only those numeric
        // timestamp fields at the client-deserialization boundary; no offer or market
        // entitlement data is removed or rewritten.
        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_SESSION_PROFILE_NORMALIZATION_STATUS",
            $"ragfair_offer_dates_normalized index={index}; normalized={normalizedDateCount}; offersPreserved={offers.Count}; reason=unix_timestamp_to_eft_datetime_bridge");
    }

    private static int NormalizeUnixDateField(JObject owner, string fieldName)
    {
        JProperty? property = owner.Properties()
            .FirstOrDefault(candidate => string.Equals(candidate.Name, fieldName, StringComparison.OrdinalIgnoreCase));
        if (property?.Value is not JValue value || value.Type != JTokenType.Integer)
        {
            return 0;
        }

        long unixValue;
        try
        {
            unixValue = value.Value<long>();
        }
        catch
        {
            return 0;
        }

        try
        {
            // Avoid Math.Abs(long.MinValue): malformed external data must not turn the
            // technical-profile bridge itself into an overflow source. Compare the signed
            // value directly while preserving the existing seconds/milliseconds heuristic.
            bool useMilliseconds = unixValue >= 100000000000L || unixValue <= -100000000000L;
            DateTimeOffset timestamp = useMilliseconds
                ? DateTimeOffset.FromUnixTimeMilliseconds(unixValue)
                : DateTimeOffset.FromUnixTimeSeconds(unixValue);
            property.Value = new JValue(timestamp.UtcDateTime);
            return 1;
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    private static void StripDogTagCustomization(JObject descriptor)
    {
        JObject customization = EnsureObject(descriptor, "Customization");
        foreach (string key in customization.Properties().Select(property => property.Name).ToArray())
        {
            if (string.Equals(key, "Dogtag", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "DogTag", StringComparison.OrdinalIgnoreCase))
            {
                customization.Remove(key);
            }
        }
    }

    private static void EnsureHealth(JObject descriptor)
    {
        JObject health = EnsureObject(descriptor, "Health");
        JObject bodyParts = EnsureObject(health, "BodyParts");
        EnsureBodyPart(bodyParts, "Head", 35);
        EnsureBodyPart(bodyParts, "Chest", 85);
        EnsureBodyPart(bodyParts, "Stomach", 70);
        EnsureBodyPart(bodyParts, "LeftArm", 60);
        EnsureBodyPart(bodyParts, "RightArm", 60);
        EnsureBodyPart(bodyParts, "LeftLeg", 65);
        EnsureBodyPart(bodyParts, "RightLeg", 65);
        EnsureValueInfo(health, "Energy", 100, 0, 100);
        EnsureValueInfo(health, "Hydration", 100, 0, 100);
        EnsureValueInfo(health, "Temperature", 36.6, 0, 100);
        EnsureValueInfo(health, "Poison", 0, 0, 100);
        SetIfMissing(health, "UpdateTime", 0);
    }

    private static void EnsureBodyPart(JObject bodyParts, string name, double maximum)
    {
        JObject part = EnsureObject(bodyParts, name);
        EnsureValueInfo(part, "Health", maximum, 0, maximum);
        EnsureObject(part, "Effects");
    }

    private static void EnsureValueInfo(JObject parent, string name, double current, double minimum, double maximum)
    {
        JObject value = EnsureObject(parent, name);
        SetIfMissing(value, "Current", current);
        SetIfMissing(value, "Minimum", minimum);
        SetIfMissing(value, "Maximum", maximum);
        SetIfMissing(value, "OverDamageReceivedMultiplier", 0);
        SetIfMissing(value, "EnvironmentDamageMultiplier", 0);
    }

    private static void EnsureInventory(JObject descriptor, string profileId, int index)
    {
        JObject inventory = EnsureObject(descriptor, "Inventory");
        JArray items = EnsureArray(inventory, "items");
        if (items.Count == 0)
        {
            string equipmentId = profileId + "equipment" + index;
            string stashId = profileId + "stash" + index;
            items.Add(new JObject { ["_id"] = equipmentId, ["_tpl"] = "55d7217a4bdc2d86028b456d" });
            items.Add(new JObject { ["_id"] = stashId, ["_tpl"] = "5811ce772459770e9e5f9532" });
            SetIfMissing(inventory, "equipment", equipmentId);
            SetIfMissing(inventory, "stash", stashId);
        }

        RemoveItemsWithMissingParents(items);
        string equipment = FirstNonEmpty(ReadString(inventory, "equipment"), FindRootByTpl(items, "55d7217a4bdc2d86028b456d"), profileId + "equipment" + index);
        EnsureRootItem(items, equipment, "55d7217a4bdc2d86028b456d");
        SetToken(inventory, "equipment", equipment);

        string stash = FirstNonEmpty(ReadString(inventory, "stash"), FindRootByTpl(items, "5811ce772459770e9e5f9532"), profileId + "stash" + index);
        EnsureRootItem(items, stash, "5811ce772459770e9e5f9532");
        SetToken(inventory, "stash", stash);

        EnsureInventoryRoot(inventory, items, "sortingTable", profileId + "sorting" + index, "602543c13fee350cd564d032");
        EnsureInventoryRoot(inventory, items, "questRaidItems", profileId + "questraid" + index, "5963866286f7747bf429b572");
        EnsureInventoryRoot(inventory, items, "questStashItems", profileId + "queststash" + index, "5963866b86f7747bfa1c4462");
        EnsureInventoryRoot(inventory, items, "hideoutCustomizationStashId", profileId + "hideoutcustom" + index, "673c7b00cbf4b984b5099181");
        EnsureObject(inventory, "hideoutAreaStashes");
        EnsureObject(inventory, "fastPanel");
        EnsureArray(inventory, "favoriteItems");
        SanitizeInventoryReferences(inventory);
    }

    private static void EnsureInventoryRoot(JObject inventory, JArray items, string fieldName, string fallbackId, string tpl)
    {
        string id = FirstNonEmpty(ReadString(inventory, fieldName), FindRootByTpl(items, tpl), fallbackId);
        EnsureRootItem(items, id, tpl);
        SetToken(inventory, fieldName, id);
    }

    private static void EnsureRootItem(JArray items, string id, string tpl)
    {
        if (items.OfType<JObject>().Any(item => string.Equals(ReadString(item, "_id"), id, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        items.Add(new JObject { ["_id"] = id, ["_tpl"] = tpl });
    }

    private static string? FindRootByTpl(JArray items, string tpl)
    {
        foreach (JObject item in items.OfType<JObject>())
        {
            if (string.Equals(ReadString(item, "_tpl"), tpl, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(ReadString(item, "parentId")))
            {
                return ReadString(item, "_id");
            }
        }

        return null;
    }

    private static void RemoveItemsWithMissingParents(JArray items)
    {
        bool changed;
        do
        {
            changed = false;
            var ids = items.OfType<JObject>()
                .Select(item => ReadString(item, "_id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (items[i] is not JObject item)
                {
                    items.RemoveAt(i);
                    changed = true;
                    continue;
                }

                string? id = ReadString(item, "_id");
                string? parentId = ReadString(item, "parentId");
                if (string.IsNullOrWhiteSpace(id) || (!string.IsNullOrWhiteSpace(parentId) && !string.Equals(parentId, "hideout", StringComparison.OrdinalIgnoreCase) && !ids.Contains(parentId)))
                {
                    items.RemoveAt(i);
                    changed = true;
                }
            }
        }
        while (changed);
    }

    private static void SanitizeInventoryReferences(JObject inventory)
    {
        JArray items = EnsureArray(inventory, "items");
        var ids = items.OfType<JObject>()
            .Select(item => ReadString(item, "_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        JObject fastPanel = EnsureObject(inventory, "fastPanel");
        foreach (string key in fastPanel.Properties().Select(property => property.Name).ToArray())
        {
            string? value = fastPanel[key]?.Type == JTokenType.String ? fastPanel[key]?.Value<string>() : fastPanel[key]?.ToString();
            if (string.IsNullOrWhiteSpace(value) || !ids.Contains(value))
            {
                fastPanel.Remove(key);
            }
        }

        JArray favorites = EnsureArray(inventory, "favoriteItems");
        for (int i = favorites.Count - 1; i >= 0; i--)
        {
            string? value = favorites[i]?.Type == JTokenType.String ? favorites[i]?.Value<string>() : favorites[i]?.ToString();
            if (string.IsNullOrWhiteSpace(value) || !ids.Contains(value))
            {
                favorites.RemoveAt(i);
            }
        }

        JObject hideoutAreaStashes = EnsureObject(inventory, "hideoutAreaStashes");
        foreach (string key in hideoutAreaStashes.Properties().Select(property => property.Name).ToArray())
        {
            string? value = hideoutAreaStashes[key]?.Type == JTokenType.String ? hideoutAreaStashes[key]?.Value<string>() : hideoutAreaStashes[key]?.ToString();
            if (string.IsNullOrWhiteSpace(value) || !ids.Contains(value))
            {
                hideoutAreaStashes.Remove(key);
            }
        }
    }

    private static void EnsureSkills(JObject descriptor)
    {
        JObject skills = EnsureObject(descriptor, "Skills");
        EnsureArray(skills, "Common");
        EnsureArray(skills, "Mastering");
        SetIfMissing(skills, "Points", 0);
    }

    private static void EnsureStats(JObject descriptor)
    {
        JObject stats = EnsureObject(descriptor, "Stats");
        JObject eft = EnsureObject(stats, "Eft");
        JObject sessionCounters = EnsureObject(eft, "SessionCounters");
        EnsureArray(sessionCounters, "Items");
        JObject overallCounters = EnsureObject(eft, "OverallCounters");
        EnsureArray(overallCounters, "Items");
        SetIfMissing(eft, "SessionExperienceMult", 0);
        SetIfMissing(eft, "ExperienceBonusMult", 0);
        SetIfMissing(eft, "TotalSessionExperience", 0);
        SetIfMissing(eft, "LastSessionDate", 0);
        EnsureArray(eft, "DroppedItems");
        EnsureArray(eft, "FoundInRaidItems");
        EnsureArray(eft, "Victims");
        EnsureArray(eft, "CarriedQuestItems");
        SetIfMissing(eft, "TotalInGameTime", 0);
        SetIfMissing(eft, "SurvivorClass", "Unknown");
    }
#endif

    public static object? ResolveMember(object target, string name)
    {
        Type? type = target.GetType();
        while (type != null)
        {
            PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                try
                {
                    return property.GetValue(target);
                }
                catch
                {
                    return null;
                }
            }

            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (field != null)
            {
                try
                {
                    return field.GetValue(target);
                }
                catch
                {
                    return null;
                }
            }

            type = type.BaseType;
        }

        return null;
    }

    public static bool SetMember(object target, string name, object? value)
    {
        Type? type = target.GetType();
        while (type != null)
        {
            PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (property != null && property.GetIndexParameters().Length == 0 && property.CanWrite)
            {
                try
                {
                    property.SetValue(target, value);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (field != null)
            {
                try
                {
                    field.SetValue(target, value);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            type = type.BaseType;
        }

        return false;
    }

    private static JObject EnsureObject(JObject parent, string name)
    {
        JProperty? property = FindProperty(parent, name);
        if (property?.Value is JObject obj)
        {
            return obj;
        }

        obj = new JObject();
        SetToken(parent, name, obj);
        return obj;
    }

    private static JArray EnsureArray(JObject parent, string name)
    {
        JProperty? property = FindProperty(parent, name);
        if (property?.Value is JArray array)
        {
            return array;
        }

        array = new JArray();
        SetToken(parent, name, array);
        return array;
    }

    private static JObject? ReadObject(JObject parent, string name) => FindProperty(parent, name)?.Value as JObject;

    private static JArray? ReadArray(JObject? parent, string name) => parent == null ? null : FindProperty(parent, name)?.Value as JArray;

    private static string? ReadString(JObject parent, string name)
    {
        JToken? token = FindProperty(parent, name)?.Value;
        if (token == null || token.Type == JTokenType.Null)
        {
            return null;
        }

        return token.Value<string>() ?? token.ToString();
    }

    private static void SetIfMissing(JObject parent, string name, object value)
    {
        JProperty? property = FindProperty(parent, name);
        if (property == null || property.Value.Type == JTokenType.Null)
        {
            SetToken(parent, name, JToken.FromObject(value));
            return;
        }

        if (property.Value.Type == JTokenType.String && string.IsNullOrWhiteSpace(property.Value.Value<string>()))
        {
            SetToken(parent, name, JToken.FromObject(value));
        }
    }

    private static void SetToken(JObject parent, string name, object? value)
    {
        JProperty? property = FindProperty(parent, name);
        if (property != null)
        {
            property.Value = value is JToken token ? token : JToken.FromObject(value ?? string.Empty);
            return;
        }

        parent[name] = value is JToken newToken ? newToken : JToken.FromObject(value ?? string.Empty);
    }

    private static JProperty? FindProperty(JObject parent, string name)
    {
        return parent.Properties().FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string FirstNonEmpty(params string?[] values)
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

    private static Exception Unwrap(Exception exception)
    {
        Exception current = exception;
        while (current is TargetInvocationException && current.InnerException != null)
        {
            current = current.InnerException;
        }

        return current;
    }

    private static string CompactStack(Exception exception)
    {
        string? stack = exception.StackTrace;
        if (string.IsNullOrWhiteSpace(stack))
        {
            return "<none>";
        }

        string firstLine = stack.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? stack;
        return firstLine.Replace(';', ',');
    }

    private static JToken SelectPayloadToken(JToken token)
    {
        if (token is not JObject obj)
        {
            return token;
        }

        foreach (string propertyName in new[] { "data", "Data", "response", "Response", "result", "Result" })
        {
            if (obj.TryGetValue(propertyName, StringComparison.OrdinalIgnoreCase, out JToken? payload) && payload != null && payload.Type != JTokenType.Null)
            {
                return UnwrapStringToken(payload);
            }
        }

        return token;
    }

    private static JToken UnwrapStringToken(JToken token)
    {
        if (token.Type != JTokenType.String)
        {
            return token;
        }

        string? value = token.Value<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return token;
        }

        string trimmed = value.Trim();
        if ((!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
            && (!trimmed.StartsWith("[", StringComparison.Ordinal) || !trimmed.EndsWith("]", StringComparison.Ordinal)))
        {
            return token;
        }

        try
        {
            return JToken.Parse(trimmed);
        }
        catch
        {
            return token;
        }
    }
}
