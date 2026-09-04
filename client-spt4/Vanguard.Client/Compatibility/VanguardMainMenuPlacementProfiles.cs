using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

#if SPT_CLIENT
using BepInEx;
using BepInEx.Bootstrap;
using Newtonsoft.Json;
using Vanguard.Client.Diagnostics;
#endif

// Responsibility: owns the user-extensible, data-driven Main Menu placement profile store.
// Flow: CONTROL reads/validates JSONC + loaded BepInEx GUIDs; QUALIFICATION chooses the most-specific
// matching profile; APPLICATION is performed separately by VanguardOffRaidUiController on Vanguard's
// button only. F12 editing writes X/Y back into the active profile without rewriting user comments/blocks.
// Authority boundary: this file can decide only Vanguard's normalized X/Y. It has no API for moving,
// resizing, reordering or restyling SPT/third-party buttons.
// Invariant: zero-GUID profiles are ignored so Vanguard's no-integration standalone two-column layout
// remains the built-in standalone default.
namespace Vanguard.Client.Compatibility;

internal readonly struct VanguardMainMenuPlacementProfile
{
    public VanguardMainMenuPlacementProfile(
        string id,
        string description,
        float xPercent,
        float yPercent,
        int requiredGuidCount,
        int priority)
    {
        Id = id;
        Description = description;
        XPercent = xPercent;
        YPercent = yPercent;
        RequiredGuidCount = requiredGuidCount;
        Priority = priority;
    }

    public string Id { get; }
    public string Description { get; }
    public float XPercent { get; }
    public float YPercent { get; }
    public int RequiredGuidCount { get; }
    public int Priority { get; }
}

internal static class VanguardMainMenuPlacementProfiles
{
    public const string FileName = "MainMenuPlacementProfiles.jsonc";

#if SPT_CLIENT
    private sealed class ProfileDocument
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        [JsonProperty("profiles")]
        public List<ProfileDefinition?>? Profiles { get; set; }
    }

    private sealed class ProfileDefinition
    {
        [JsonProperty("id")]
        public string? Id { get; set; }

        [JsonProperty("description")]
        public string? Description { get; set; }

        [JsonProperty("enabled")]
        public bool? Enabled { get; set; }

        [JsonProperty("requiredGuids")]
        public List<string>? RequiredGuids { get; set; }

        [JsonProperty("priority")]
        public int? Priority { get; set; }

        [JsonProperty("x")]
        public float? X { get; set; }

        [JsonProperty("y")]
        public float? Y { get; set; }
    }

    private sealed class ValidatedProfile
    {
        public string Id { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string[] RequiredGuids { get; init; } = Array.Empty<string>();
        public float X { get; set; }
        public float Y { get; set; }
        public int Priority { get; init; }
        public int SourceOrder { get; init; }
    }

    // These are the official release defaults validated through the release runtime calibration matrix.
    // Existing user files are never overwritten on update, so custom profiles and user-adjusted coordinates
    // remain authoritative once MainMenuPlacementProfiles.jsonc already exists.
    private static readonly string DefaultDocument = """
{
  // Vanguard Main Menu Placement Profiles
  //
  // This file controls ONLY the position of the Vanguard Main Menu button when one or more
  // matching BepInEx plugin GUIDs are loaded. Vanguard will never move SPT or third-party
  // buttons while one of these profiles is active.
  //
  // HOW MATCHING WORKS
  // - Every GUID in "requiredGuids" must be loaded for a profile to match.
  // - If several profiles match, Vanguard uses the most specific one: the profile with the
  //   greatest number of required GUIDs.
  // - "priority" is only a tie-breaker between profiles with the same specificity.
  // - Profiles with an empty "requiredGuids" list are ignored intentionally. When none of
  //   the configured GUIDs are present, Vanguard keeps its built-in standalone two-column
  //   menu layout exactly as shipped.
  //
  // ADDING YOUR OWN SCENARIO
  // 1. Copy one profile block below and give it a unique "id".
  // 2. Put the BepInEx GUID(s) of the menu mod(s) that define your scenario in "requiredGuids".
  // 3. Launch SPT and open the F12 configuration menu.
  // 4. Adjust only these two Vanguard settings:
  //      Vanguard Menu X Position (%)
  //      Vanguard Menu Y Position (%)
  // 5. Vanguard saves those values back into the currently active profile in this file.
  //
  // COORDINATE SYSTEM
  // - X = 0 is the left edge of the stable Main Menu reference rect; X = 100 is the right edge.
  // - Y = 0 is the bottom edge; Y = 100 is the top edge.
  // - Coordinates are absolute normalized targets, so repeated menu rebuilds cannot accumulate drift.
  //
  // You may add as many profile blocks as you need. Vanguard creates this file only when it
  // does not exist; updates never overwrite your custom blocks or comments.

  "schemaVersion": 1,
  "profiles": [
    {
      "id": "menu-overhaul",
      "description": "Vanguard + Menu Overhaul",
      "enabled": true,
      "requiredGuids": [
        "com.moxopixel.menuoverhaul"
      ],
      "priority": 0,
      "x": 12.0,
      "y": 55.6
    },
    {
      "id": "career-log",
      "description": "Vanguard + Career Log",
      "enabled": true,
      "requiredGuids": [
        "com.softwyx.careerlog"
      ],
      "priority": 0,
      "x": 50.0,
      "y": 57.0
    },
    {
      "id": "career-log-menu-overhaul",
      "description": "Vanguard + Career Log + Menu Overhaul",
      "enabled": true,
      "requiredGuids": [
        "com.softwyx.careerlog",
        "com.moxopixel.menuoverhaul"
      ],
      "priority": 0,
      "x": 11.6,
      "y": 55.0
    }
  ]
}
""";

    private static readonly List<ValidatedProfile> Profiles = new();
    private static readonly HashSet<string> ConfiguredGuids = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static bool initialized;
    private static DateTime loadedWriteTimeUtc = DateTime.MinValue;
    private static DateTime nextFileProbeUtc = DateTime.MinValue;
    private static string lastResolutionDiagnostic = string.Empty;

    public static string FilePath => Path.Combine(BepInEx.Paths.BepInExRootPath, "config", "Vanguard", FileName);

    public static void Initialize()
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        EnsureProfileFileExists();
        ReloadProfiles(force: true);
    }

    public static bool TryResolveActive(out VanguardMainMenuPlacementProfile profile)
    {
        Initialize();
        ReloadProfiles(force: false);

        HashSet<string> loadedGuids = ResolveLoadedPluginGuids();
        ValidatedProfile? selected = Profiles
            .Where(candidate => candidate.RequiredGuids.All(loadedGuids.Contains))
            .OrderByDescending(candidate => candidate.RequiredGuids.Length)
            .ThenByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.SourceOrder)
            .FirstOrDefault();

        if (selected == null)
        {
            profile = default;
            WriteResolutionDiagnosticOnce("standalone_legacy", "none", 0, 0);
            return false;
        }

        profile = new VanguardMainMenuPlacementProfile(
            selected.Id,
            selected.Description,
            selected.X,
            selected.Y,
            selected.RequiredGuids.Length,
            selected.Priority);
        WriteResolutionDiagnosticOnce("profile", selected.Id, selected.RequiredGuids.Length, selected.Priority);
        return true;
    }

    public static bool HasAnyConfiguredPluginLoaded()
    {
        Initialize();
        ReloadProfiles(force: false);
        HashSet<string> loadedGuids = ResolveLoadedPluginGuids();
        return ConfiguredGuids.Any(loadedGuids.Contains);
    }

    public static bool TrySaveCoordinates(string profileId, float xPercent, float yPercent)
    {
        Initialize();
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return false;
        }

        xPercent = Math.Clamp(xPercent, 0f, 100f);
        yPercent = Math.Clamp(yPercent, 0f, 100f);

        try
        {
            string path = FilePath;
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (!TryFindProfileObjectRange(text, profileId, out int objectStart, out int objectLength))
            {
                VanguardClientDiagnosticsLog.Warning(
                    VanguardBuildVersion.OffRaidUiStatusTag,
                    $"Main-menu placement profile save skipped; profile={profileId}; reason=profile_object_not_found; file={FileName}");
                return false;
            }

            string profileObject = text.Substring(objectStart, objectLength);
            string updatedObject = ReplaceNumericProperty(profileObject, "x", xPercent, out bool xUpdated);
            updatedObject = ReplaceNumericProperty(updatedObject, "y", yPercent, out bool yUpdated);
            if (!xUpdated || !yUpdated)
            {
                VanguardClientDiagnosticsLog.Warning(
                    VanguardBuildVersion.OffRaidUiStatusTag,
                    $"Main-menu placement profile save skipped; profile={profileId}; reason=x_or_y_property_missing; file={FileName}");
                return false;
            }

            string updatedText = text.Substring(0, objectStart)
                + updatedObject
                + text.Substring(objectStart + objectLength);
            string temporaryPath = path + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, updatedText, Utf8NoBom);
                File.Copy(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            loadedWriteTimeUtc = DateTime.MinValue;
            ReloadProfiles(force: true);
            return true;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.OffRaidUiStatusTag,
                $"Main-menu placement profile save failed; profile={profileId}; error={exception.GetType().Name}: {exception.Message}; file={FileName}; failOpen=true");
            return false;
        }
    }

    private static void EnsureProfileFileExists()
    {
        try
        {
            string path = FilePath;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(path))
            {
                File.WriteAllText(path, DefaultDocument, Utf8NoBom);
                VanguardClientDiagnosticsLog.Info(
                    VanguardBuildVersion.OffRaidUiStatusTag,
                    $"Main-menu placement profile file created; path={path}; officialProfiles=3; standaloneLayout=built_in_legacy_two_column");
            }
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.OffRaidUiStatusTag,
                $"Main-menu placement profile file creation failed; error={exception.GetType().Name}: {exception.Message}; failOpen=true");
        }
    }

    private static void ReloadProfiles(bool force)
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (!force && nowUtc < nextFileProbeUtc)
        {
            return;
        }

        nextFileProbeUtc = nowUtc.AddSeconds(1);
        EnsureProfileFileExists();

        try
        {
            string path = FilePath;
            if (!File.Exists(path))
            {
                LoadEmbeddedOfficialFallback("profile_file_unavailable");
                return;
            }

            DateTime writeTimeUtc = File.GetLastWriteTimeUtc(path);
            if (!force && writeTimeUtc == loadedWriteTimeUtc)
            {
                return;
            }

            string text = File.ReadAllText(path, Encoding.UTF8);
            if (!TryParseValidatedProfiles(text, out List<ValidatedProfile> validated, out string reason))
            {
                // Preserve the last valid in-memory set if an edit is temporarily incomplete. On first-load
                // failure, fall back to the same official data document rather than letting known integrations
                // trigger standalone geometry that may move externally-owned rows.
                if (Profiles.Count == 0)
                {
                    LoadEmbeddedOfficialFallback("first_load_parse_failure");
                }

                loadedWriteTimeUtc = writeTimeUtc;
                VanguardClientDiagnosticsLog.Warning(
                    VanguardBuildVersion.OffRaidUiStatusTag,
                    $"Main-menu placement profile reload rejected; reason={reason}; retainedProfiles={Profiles.Count}; file={FileName}; failOpen=true");
                return;
            }

            Profiles.Clear();
            Profiles.AddRange(validated);
            RefreshConfiguredGuidSet(document: JsonConvert.DeserializeObject<ProfileDocument>(text));
            loadedWriteTimeUtc = writeTimeUtc;
            lastResolutionDiagnostic = string.Empty;
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.OffRaidUiStatusTag,
                $"Main-menu placement profiles loaded; file={FileName}; validProfiles={Profiles.Count}; standaloneLayout=built_in_legacy_two_column");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.OffRaidUiStatusTag,
                $"Main-menu placement profile reload failed; error={exception.GetType().Name}: {exception.Message}; retainedProfiles={Profiles.Count}; file={FileName}; failOpen=true");
        }
    }

    private static bool TryParseValidatedProfiles(
        string text,
        out List<ValidatedProfile> validated,
        out string reason)
    {
        validated = new List<ValidatedProfile>();
        reason = "ok";

        ProfileDocument? document;
        try
        {
            document = JsonConvert.DeserializeObject<ProfileDocument>(text);
        }
        catch (Exception exception)
        {
            reason = $"jsonc_parse_{exception.GetType().Name}";
            return false;
        }

        if (document?.Profiles == null)
        {
            reason = "profiles_array_missing";
            return false;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < document.Profiles.Count; index++)
        {
            ProfileDefinition? definition = document.Profiles[index];
            if (definition == null)
            {
                LogInvalidBlock(index, string.Empty, "null_profile_block");
                continue;
            }

            if (definition.Enabled == false)
            {
                continue;
            }

            string id = definition.Id?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id) || !seenIds.Add(id))
            {
                LogInvalidBlock(index, id, string.IsNullOrWhiteSpace(id) ? "id_missing" : "duplicate_id");
                continue;
            }

            string[] requiredGuids = (definition.RequiredGuids ?? new List<string>())
                .Select(value => value?.Trim() ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (requiredGuids.Length == 0)
            {
                LogInvalidBlock(index, id, "zero_guid_profile_ignored_to_preserve_standalone_layout");
                continue;
            }

            if (!definition.X.HasValue || !definition.Y.HasValue
                || definition.X.Value < 0f || definition.X.Value > 100f
                || definition.Y.Value < 0f || definition.Y.Value > 100f)
            {
                LogInvalidBlock(index, id, "x_y_must_be_0_to_100");
                continue;
            }

            validated.Add(new ValidatedProfile
            {
                Id = id,
                Description = string.IsNullOrWhiteSpace(definition.Description) ? id : definition.Description!.Trim(),
                RequiredGuids = requiredGuids,
                X = definition.X.Value,
                Y = definition.Y.Value,
                Priority = definition.Priority ?? 0,
                SourceOrder = index
            });
        }

        return true;
    }

    private static void LoadEmbeddedOfficialFallback(string reason)
    {
        if (!TryParseValidatedProfiles(DefaultDocument, out List<ValidatedProfile> fallback, out string fallbackReason))
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.OffRaidUiStatusTag,
                $"Main-menu placement embedded fallback unavailable; reason={reason}; fallbackReason={fallbackReason}; failOpen=true");
            return;
        }

        Profiles.Clear();
        Profiles.AddRange(fallback);
        RefreshConfiguredGuidSet(JsonConvert.DeserializeObject<ProfileDocument>(DefaultDocument));
        lastResolutionDiagnostic = string.Empty;
        VanguardClientDiagnosticsLog.Warning(
            VanguardBuildVersion.OffRaidUiStatusTag,
            $"Main-menu placement using embedded official fallback; reason={reason}; profiles={Profiles.Count}; file={FileName}; standaloneProtected=true");
    }

    private static void RefreshConfiguredGuidSet(ProfileDocument? document)
    {
        ConfiguredGuids.Clear();
        if (document?.Profiles == null)
        {
            return;
        }

        foreach (ProfileDefinition? definition in document.Profiles)
        {
            if (definition?.RequiredGuids == null)
            {
                continue;
            }

            foreach (string? rawGuid in definition.RequiredGuids)
            {
                string guid = rawGuid?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(guid))
                {
                    ConfiguredGuids.Add(guid);
                }
            }
        }
    }

    private static HashSet<string> ResolveLoadedPluginGuids()
    {
        return new HashSet<string>(Chainloader.PluginInfos.Keys, StringComparer.OrdinalIgnoreCase);
    }

    private static void LogInvalidBlock(int index, string id, string reason)
    {
        VanguardClientDiagnosticsLog.Warning(
            VanguardBuildVersion.OffRaidUiStatusTag,
            $"Main-menu placement profile ignored; index={index}; id={(string.IsNullOrWhiteSpace(id) ? "<none>" : id)}; reason={reason}; file={FileName}; failOpen=true");
    }

    private static void WriteResolutionDiagnosticOnce(string mode, string profileId, int specificity, int priority)
    {
        string signature = $"{mode}|{profileId}|{specificity}|{priority}";
        if (string.Equals(signature, lastResolutionDiagnostic, StringComparison.Ordinal))
        {
            return;
        }

        lastResolutionDiagnostic = signature;
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OffRaidUiStatusTag,
            $"Main-menu placement qualification; mode={mode}; profile={profileId}; specificity={specificity}; priority={priority}; file={FileName}");
    }

    private static string ReplaceNumericProperty(string objectText, string propertyName, float value, out bool replaced)
    {
        string pattern = $"(\\\"{Regex.Escape(propertyName)}\\\"\\s*:\\s*)([-+]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)(?:[eE][-+]?\\d+)?)";
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        int replacementCount = 0;
        string formatted = value.ToString("0.###", CultureInfo.InvariantCulture);
        string result = regex.Replace(
            objectText,
            match =>
            {
                replacementCount++;
                return match.Groups[1].Value + formatted;
            },
            1);
        replaced = replacementCount == 1;
        return result;
    }

    private static bool TryFindProfileObjectRange(string text, string profileId, out int objectStart, out int objectLength)
    {
        objectStart = -1;
        objectLength = 0;
        var objectStack = new Stack<int>();
        bool inString = false;
        bool escaped = false;
        bool inLineComment = false;
        bool inBlockComment = false;

        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            char next = index + 1 < text.Length ? text[index + 1] : '\0';

            if (inLineComment)
            {
                if (current == '\r' || current == '\n')
                {
                    inLineComment = false;
                }
                continue;
            }

            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    index++;
                }
                continue;
            }

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (current == '/' && next == '/')
            {
                inLineComment = true;
                index++;
                continue;
            }

            if (current == '/' && next == '*')
            {
                inBlockComment = true;
                index++;
                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current == '{')
            {
                objectStack.Push(index);
                continue;
            }

            if (current != '}' || objectStack.Count == 0)
            {
                continue;
            }

            int start = objectStack.Pop();
            int length = index - start + 1;
            string candidate = text.Substring(start, length);
            if (candidate.IndexOf("\"id\"", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            try
            {
                ProfileDefinition? definition = JsonConvert.DeserializeObject<ProfileDefinition>(candidate);
                if (definition?.Id != null
                    && string.Equals(definition.Id.Trim(), profileId, StringComparison.OrdinalIgnoreCase))
                {
                    objectStart = start;
                    objectLength = length;
                    return true;
                }
            }
            catch
            {
                // A nested non-profile object or a temporarily incomplete user edit is not a match.
            }
        }

        return false;
    }
#else
    public static string FilePath => FileName;
    public static void Initialize() { }
    public static bool TryResolveActive(out VanguardMainMenuPlacementProfile profile) { profile = default; return false; }
    public static bool HasAnyConfiguredPluginLoaded() => false;
    public static bool TrySaveCoordinates(string profileId, float xPercent, float yPercent) => false;
#endif
}
