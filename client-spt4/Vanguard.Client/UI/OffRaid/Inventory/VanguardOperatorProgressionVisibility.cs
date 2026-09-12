using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using UnityEngine;
#endif

// Responsibility: exposes EFT's native progression presentation while an Operator owns the direct InventoryScreen session.
// Flow: InventoryScreen.Show completes with the already-bound Operator profile/controller; this service re-enables only native progression tabs that EFT hid for the secondary-profile presentation and records the effective Operator skill authority.
// Authority boundary: presentation only; no skill/mastery values are synthesized, normalized, persisted, or mutated here.
// Invariant: the persistent Operator Profile.Skills object remains the sole progression truth consumed by the native UI; if the expected native tab surface cannot be resolved, Vanguard fails soft and leaves EFT unchanged.
namespace Vanguard.Client.UI.OffRaid.Inventory;

internal static class VanguardOperatorProgressionVisibility
{
    public const string StatusTag = "VANGUARD_OPERATOR_PROGRESSION_VISIBILITY_STATUS";

    public static void EnsureNativeProgressionTabs(object? inventoryScreen, object? screenController, string source)
    {
#if SPT_CLIENT
        if (inventoryScreen == null
            || screenController == null
            || !VanguardOperatorInventoryModeClientState.IsActive
            || !VanguardOperatorDirectEquipmentScreenEntry.IsActiveOperatorInventoryScreenController(screenController))
        {
            return;
        }

        if (!VanguardOperatorDirectEquipmentScreenEntry.TryGetActiveOperatorRuntimeContext(
                out _,
                out _,
                out object? operatorProfile,
                out object? inventoryController,
                out _,
                out string? operatorProfileId,
                out string? operatorId,
                out int generation,
                out bool completionStarted)
            || operatorProfile == null
            || inventoryController == null
            || completionStarted)
        {
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"state=skipped; source={Safe(source)}; reason=operator_runtime_context_unavailable; sessionActive={VanguardOperatorInventoryModeClientState.IsActive}; mutation=false");
            return;
        }

        object? controllerProfile = VanguardOperatorInventoryProfileLoader.ResolveMember(inventoryController, "Profile");
        string? controllerProfileId = controllerProfile == null
            ? VanguardOperatorInventoryProfileLoader.ResolveMember(inventoryController, "ProfileId")?.ToString()
            : VanguardOperatorInventoryProfileLoader.ResolveMember(controllerProfile, "Id")?.ToString();
        bool controllerProfileMatches = string.IsNullOrWhiteSpace(operatorProfileId)
            || string.Equals(operatorProfileId, controllerProfileId, StringComparison.OrdinalIgnoreCase);

        object? skills = VanguardOperatorInventoryProfileLoader.ResolveMember(operatorProfile, "Skills");
        // Runtime EFT.Profile.Skills is a SkillManager, not the serialized SkillsDescriptor.
        // Count DisplayList (native visible Common skills) rather than looking for a nonexistent
        // SkillManager.Common member; a `common=0` diagnostic based on that member would therefore be misleading.
        int commonCount = CountCollectionMember(skills, "DisplayList");
        if (commonCount == 0)
        {
            commonCount = CountCollectionMember(skills, "Skills");
        }
        int masteringCount = CountCollectionMember(skills, "Mastering");
        int commonProgressedCount = CountProgressedCollectionMember(skills, "DisplayList");
        int masteringProgressedCount = CountProgressedMasteringMember(skills);

        if (!TryReadNativeTabs(inventoryScreen, out List<NativeTabEntry> tabs, out string tabReason))
        {
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"state=unavailable; source={Safe(source)}; operator={Safe(operatorId)}; profile={Safe(operatorProfileId)}; generation={generation}; reason={Safe(tabReason)}; controllerProfileMatches={controllerProfileMatches}; common={commonCount}; commonProgressed={commonProgressedCount}; mastering={masteringCount}; masteringProgressed={masteringProgressedCount}; persistentMutation=false; progressionMutation=false; nativeUiPreserved=true");
            return;
        }

        int progressionTabs = 0;
        int activatedTabs = 0;
        int alreadyVisibleTabs = 0;
        var progressionNames = new List<string>();
        foreach (NativeTabEntry entry in tabs)
        {
            if (!IsProgressionTab(entry.KeyName))
            {
                continue;
            }

            progressionTabs++;
            progressionNames.Add(entry.KeyName);
            if (TryEnsureTabVisible(entry.Value, out bool wasAlreadyVisible))
            {
                if (wasAlreadyVisible)
                {
                    alreadyVisibleTabs++;
                }
                else
                {
                    activatedTabs++;
                }
            }
        }

        string allTabs = tabs.Count == 0
            ? "<none>"
            : string.Join(",", tabs.Select(tab => tab.KeyName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        string exposedTabs = progressionNames.Count == 0
            ? "<none>"
            : string.Join(",", progressionNames.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

        if (progressionTabs == 0)
        {
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"state=native_progression_tab_not_found; source={Safe(source)}; operator={Safe(operatorId)}; profile={Safe(operatorProfileId)}; generation={generation}; tabs={Safe(allTabs)}; controllerProfileMatches={controllerProfileMatches}; common={commonCount}; commonProgressed={commonProgressedCount}; mastering={masteringCount}; masteringProgressed={masteringProgressedCount}; persistentMutation=false; progressionMutation=false; nativeUiPreserved=true");
            return;
        }

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"state=ready; source={Safe(source)}; operator={Safe(operatorId)}; profile={Safe(operatorProfileId)}; generation={generation}; tabs={Safe(allTabs)}; progressionTabs={Safe(exposedTabs)}; activated={activatedTabs}; alreadyVisible={alreadyVisibleTabs}; controllerProfileMatches={controllerProfileMatches}; common={commonCount}; commonProgressed={commonProgressedCount}; mastering={masteringCount}; masteringProgressed={masteringProgressedCount}; authority=operator_persistent_profile_skills; presentation=native_inventory_screen; persistentMutation=false; progressionMutation=false");
#else
        _ = inventoryScreen;
        _ = screenController;
        _ = source;
#endif
    }

#if SPT_CLIENT
    private static bool TryReadNativeTabs(object inventoryScreen, out List<NativeTabEntry> tabs, out string reason)
    {
        tabs = new List<NativeTabEntry>();
        object? dictionary = ResolveFieldValue(inventoryScreen, "_tabDictionary");
        if (dictionary == null)
        {
            dictionary = ResolveLikelyTabDictionary(inventoryScreen);
        }

        if (dictionary == null)
        {
            reason = "tab_dictionary_not_found";
            return false;
        }

        if (dictionary is not IEnumerable enumerable || dictionary is string)
        {
            reason = "tab_dictionary_not_enumerable:" + dictionary.GetType().Name;
            return false;
        }

        try
        {
            foreach (object? item in enumerable)
            {
                if (item == null)
                {
                    continue;
                }

                object? key = ResolveMember(item, "Key");
                object? value = ResolveMember(item, "Value");
                if (key == null || value == null)
                {
                    continue;
                }

                tabs.Add(new NativeTabEntry(key.ToString() ?? "<unknown>", value));
            }
        }
        catch (Exception exception)
        {
            reason = "tab_dictionary_enumeration_failed:" + exception.GetType().Name + ":" + exception.Message;
            return false;
        }

        reason = tabs.Count > 0 ? "ok" : "tab_dictionary_empty_or_unreadable";
        return tabs.Count > 0;
    }

    private static object? ResolveLikelyTabDictionary(object inventoryScreen)
    {
        Type? type = inventoryScreen.GetType();
        while (type != null)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.Name.IndexOf("tab", StringComparison.OrdinalIgnoreCase) < 0
                    || field.Name.IndexOf("dictionary", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                try
                {
                    object? value = field.GetValue(inventoryScreen);
                    if (value is IEnumerable && value is not string)
                    {
                        return value;
                    }
                }
                catch
                {
                    // Compatibility probe only. Keep looking for the native tab dictionary.
                }
            }

            type = type.BaseType;
        }

        return null;
    }

    private static bool TryEnsureTabVisible(object tab, out bool wasAlreadyVisible)
    {
        wasAlreadyVisible = false;
        try
        {
            GameObject? gameObject = tab is Component component
                ? component.gameObject
                : ResolveMember(tab, "gameObject") as GameObject;
            if (gameObject == null)
            {
                return false;
            }

            wasAlreadyVisible = gameObject.activeSelf;
            if (!wasAlreadyVisible)
            {
                // SPT itself uses SetActive(true) on InventoryScreen's native tab dictionary
                // for Prestige. Reuse the same presentation-only mechanism for progression.
                gameObject.SetActive(true);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProgressionTab(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName))
        {
            return false;
        }

        return keyName.IndexOf("skill", StringComparison.OrdinalIgnoreCase) >= 0
            || keyName.IndexOf("master", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int CountCollectionMember(object? owner, string memberName)
    {
        object? value = owner == null ? null : ResolveMember(owner, memberName);
        if (value == null)
        {
            return 0;
        }

        object? count = ResolveMember(value, "Count");
        if (count != null && int.TryParse(count.ToString(), out int parsed))
        {
            return parsed;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            int total = 0;
            try
            {
                foreach (object? _ in enumerable)
                {
                    total++;
                }
            }
            catch
            {
                return 0;
            }

            return total;
        }

        return 0;
    }

    private static int CountProgressedCollectionMember(object? owner, string memberName)
    {
        object? value = owner == null ? null : ResolveMember(owner, memberName);
        if (value is not IEnumerable enumerable || value is string)
        {
            return 0;
        }

        int progressed = 0;
        try
        {
            foreach (object? item in enumerable)
            {
                if (item == null)
                {
                    continue;
                }

                object? current = ResolveMember(item, "Current") ?? ResolveMember(item, "Progress");
                if (current != null
                    && double.TryParse(current.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)
                    && parsed > 0.0001d)
                {
                    progressed++;
                }
            }
        }
        catch
        {
            return 0;
        }

        return progressed;
    }

    private static int CountProgressedMasteringMember(object? skills)
    {
        object? mastering = skills == null ? null : ResolveMember(skills, "Mastering");
        if (mastering is not IEnumerable enumerable || mastering is string)
        {
            return 0;
        }

        int progressed = 0;
        try
        {
            foreach (object? entry in enumerable)
            {
                if (entry == null)
                {
                    continue;
                }

                object? value = ResolveMember(entry, "Value") ?? entry;
                object? current = value == null ? null : ResolveMember(value, "Current");
                if (current != null
                    && double.TryParse(current.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)
                    && parsed > 0.0001d)
                {
                    progressed++;
                }
            }
        }
        catch
        {
            return 0;
        }

        return progressed;
    }

    private static object? ResolveFieldValue(object target, string fieldName)
    {
        Type? type = target.GetType();
        while (type != null)
        {
            FieldInfo? field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
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

    private static object? ResolveMember(object target, string name)
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

    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<none>";
        }

        return value.Replace(";", ",").Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private sealed record NativeTabEntry(string KeyName, object Value);
#endif
}
