#if SPT_CLIENT
using BepInEx.Configuration;
using UnityEngine;

// Responsibility: Defines the user/configuration surface for Tactical Authoring Options in the F12/runtime options.
// Flow: BepInEx/F12 values are bound, normalized and exposed through getters/snapshots; raid-scoped settings are synchronized to the process that owns runtime execution.
// Authority boundary: Configuration supplies policy inputs only; changing a value does not itself perform gameplay or persistence mutation.
// Invariant: Defaults preserve the established public behavior and synchronized values remain bounded to their declared scope.
namespace Vanguard.Client.Options;

internal static class VanguardTacticalAuthoringOptions
{
    private const string Section = "Vanguard - Tactical Authoring";
    private static bool isBound;
    private static readonly KeyboardShortcut EmptyShortcut = new(KeyCode.None);

    private static ConfigEntry<bool>? editorHotkeysEnabled;
    private static ConfigEntry<bool>? automaticAuthoredZoneOccupancyEnabled;
    private static ConfigEntry<KeyboardShortcut>? toggleEditor;
    private static ConfigEntry<KeyboardShortcut>? createZone;
    private static ConfigEntry<KeyboardShortcut>? createNestedZone;
    private static ConfigEntry<KeyboardShortcut>? renameSelectedZone;
    private static ConfigEntry<KeyboardShortcut>? nextZone;
    private static ConfigEntry<KeyboardShortcut>? applyZoneMetadata;
    private static ConfigEntry<KeyboardShortcut>? captureFloorMin;
    private static ConfigEntry<KeyboardShortcut>? captureFloorMax;
    private static ConfigEntry<KeyboardShortcut>? addSlot;
    private static ConfigEntry<KeyboardShortcut>? cycleSlotType;
    private static ConfigEntry<KeyboardShortcut>? addAccess;
    private static ConfigEntry<KeyboardShortcut>? nextAccess;
    private static ConfigEntry<KeyboardShortcut>? linkNearestSlot;
    private static ConfigEntry<KeyboardShortcut>? applyNearestSlotSettings;
    private static ConfigEntry<KeyboardShortcut>? recaptureNearestSlot;
    private static ConfigEntry<KeyboardShortcut>? updateSelectedAccess;
    private static ConfigEntry<KeyboardShortcut>? toggleNearestSlotEnabled;
    private static ConfigEntry<KeyboardShortcut>? deleteNearestSlot;
    private static ConfigEntry<KeyboardShortcut>? deleteSelectedZone;
    private static ConfigEntry<KeyboardShortcut>? save;
    private static ConfigEntry<KeyboardShortcut>? reload;
    private static ConfigEntry<KeyboardShortcut>? revalidate;
    private static ConfigEntry<KeyboardShortcut>? exportInvalid;

    private static ConfigEntry<string>? displayZoneName;
    private static ConfigEntry<string>? buildingId;
    private static ConfigEntry<string>? floorId;
    private static ConfigEntry<float>? zoneRadiusMeters;
    private static ConfigEntry<float>? defaultFloorBelowMeters;
    private static ConfigEntry<float>? defaultFloorAboveMeters;
    private static ConfigEntry<int>? priority;
    private static ConfigEntry<int>? minimumSquadSize;
    private static ConfigEntry<float>? maximumOwnerDistanceMeters;
    private static ConfigEntry<string>? roleAffinity;
    private static ConfigEntry<string>? mutualExclusionGroup;
    private static ConfigEntry<float>? watchArcDegrees;
    private static ConfigEntry<float>? navMeshProjectionMeters;
    private static ConfigEntry<float>? validationCapsuleRadiusMeters;
    private static ConfigEntry<float>? validationCapsuleHeightMeters;
    private static ConfigEntry<float>? nearbyEditRadiusMeters;

    public static void Bind(ConfigFile config)
    {
        if (isBound)
        {
            return;
        }

        editorHotkeysEnabled = config.Bind(Section, "Enable tactical authoring hotkeys", true,
            "Local-only in-raid authoring input. Editing remains owner-local; headless preview/occupancy transport is handled separately.");
        automaticAuthoredZoneOccupancyEnabled = config.Bind(Section, "Enable automatic authored zone occupancy", true,
            "When the editor is closed and a saved authoring map exists, automatically publish an owner-scoped authored zone to the Fika headless. Exact occupancy is sticky to prevent overlap churn; a strictly more-specific nested exact zone may preempt, direct handoff occurs after exact exit when another exact zone exists, and exit hysteresis applies only when no exact replacement exists. Operators still yield to grenade/combat/medical authority. Persisted RuntimeConsumptionEnabled/RuntimeEligible flags remain false.");

        toggleEditor = BindShortcut(config, "Toggle editor", KeyCode.F6, KeyCode.LeftControl,
            "Toggle the Vanguard tactical authoring overlay for the local player.");
        createZone = BindShortcut(config, "Create zone", KeyCode.Home, KeyCode.LeftControl,
            "Begin guarded creation of a new authoring zone centered on the local player. Creation is blocked when the new center is already inside an existing zone; an in-game name prompt must be confirmed before the zone exists.");
        createNestedZone = BindShortcut(config, "Create nested zone (forced)", KeyCode.Home, KeyCode.LeftControl, KeyCode.LeftShift,
            "Deliberately bypass the near-duplicate center guard to author a nested/more-specific zone. An in-game name prompt must still be confirmed before creation.");
        renameSelectedZone = BindShortcut(config, "Rename selected zone", KeyCode.N, KeyCode.LeftControl, KeyCode.LeftShift,
            "Rename the selected authored zone through the in-game text prompt. Escape cancels without changing saved/in-memory data.");
        nextZone = BindShortcut(config, "Select next zone", KeyCode.PageDown, KeyCode.LeftControl,
            "Select the next zone on the current map.");
        applyZoneMetadata = BindShortcut(config, "Apply zone metadata", KeyCode.M, KeyCode.LeftControl,
            "Apply the F12 building/floor/radius values to the selected zone. The player zone name is preserved; use Ctrl+Shift+N to rename it explicitly in-game.");
        captureFloorMin = BindShortcut(config, "Capture floor minimum", KeyCode.LeftBracket, KeyCode.LeftControl,
            "Capture selected-zone MinY from the local player standing Y minus the configured lower offset.");
        captureFloorMax = BindShortcut(config, "Capture floor maximum", KeyCode.RightBracket, KeyCode.LeftControl,
            "Capture selected-zone MaxY from the local player standing Y plus the configured upper offset.");
        addSlot = BindShortcut(config, "Add tactical slot", KeyCode.Insert, KeyCode.LeftControl,
            "Add a tactical slot at the local player's current position and capture gaze as WatchDirection.");
        cycleSlotType = BindShortcut(config, "Cycle slot type", KeyCode.PageUp, KeyCode.LeftControl,
            "Cycle the slot type used by the next created slot.");
        addAccess = BindShortcut(config, "Add access", KeyCode.End, KeyCode.LeftControl,
            "Add a zone access marker at the local player's current position.");
        nextAccess = BindShortcut(config, "Select next access", KeyCode.DownArrow, KeyCode.LeftControl,
            "Select the next access marker in the selected zone.");
        linkNearestSlot = BindShortcut(config, "Link or unlink nearest slot to nearest access", KeyCode.L, KeyCode.LeftControl,
            "Associate the nearest slot inside the nearby edit radius with the physically nearest access marker in the current zone. Access search has no distance ceiling. The selected access is ignored for linking; Ctrl+Down selection remains dedicated to access recapture/editing.");
        applyNearestSlotSettings = BindShortcut(config, "Apply slot settings to nearest slot", KeyCode.U, KeyCode.LeftControl,
            "Apply the current F12 slot type/priority/constraints/watch-arc settings to the nearest authored slot without changing its position, access link or enabled state.");
        recaptureNearestSlot = BindShortcut(config, "Recapture nearest slot position and watch", KeyCode.P, KeyCode.LeftControl,
            "Move the nearest authored slot to the local player's current position and recapture gaze as WatchDirection while preserving its tactical settings and access link.");
        updateSelectedAccess = BindShortcut(config, "Recapture selected access", KeyCode.UpArrow, KeyCode.LeftControl,
            "Move the selected access marker to the local player's current position and recapture gaze as ApproachDirection.");
        toggleNearestSlotEnabled = BindShortcut(config, "Toggle nearest slot enabled", KeyCode.Delete, KeyCode.LeftControl,
            "Reversibly disable or re-enable the nearest tactical slot.");
        deleteNearestSlot = BindShortcut(config, "Delete nearest slot", KeyCode.Delete, KeyCode.LeftControl, KeyCode.LeftShift,
            "Hard-delete the nearest tactical slot from in-memory authoring data. The deletion is not persisted until Ctrl+S; Ctrl+R can discard an accidental unsaved deletion.");
        deleteSelectedZone = BindShortcut(config, "Delete selected zone", KeyCode.Backspace, KeyCode.LeftControl, KeyCode.LeftShift,
            "Hard-delete the selected zone and its nested slots/accesses from in-memory authoring data. The deletion is not persisted until Ctrl+S; Ctrl+R can discard an accidental unsaved deletion.");
        save = BindShortcut(config, "Save authoring data", KeyCode.S, KeyCode.LeftControl,
            "Transactionally save the current map authoring file under BepInEx/config/Vanguard/TacticalAuthoring/Maps.");
        reload = BindShortcut(config, "Reload authoring data", KeyCode.R, KeyCode.LeftControl,
            "Reload the current map authoring file. Unknown schemas and map mismatches fail closed.");
        revalidate = BindShortcut(config, "Revalidate selected zone", KeyCode.V, KeyCode.LeftControl,
            "Re-run bounded NavMesh/path/capsule authoring checks for the selected zone.");
        exportInvalid = BindShortcut(config, "Export invalid report", KeyCode.I, KeyCode.LeftControl,
            "Export invalid/warning slot diagnostics for the current map without changing gameplay state.");

        displayZoneName = config.Bind(Section, "Zone name", "Unnamed Zone",
            "Legacy configuration value retained for backwards-compatible F12/config loading. New zones and renames use the explicit in-game text prompt; Ctrl+M preserves the current zone name.");
        buildingId = config.Bind(Section, "Building id", string.Empty,
            "Optional stable player-authored building identifier. This is metadata, not a runtime navigation authority.");
        floorId = config.Bind(Section, "Floor id", "floor-unknown",
            "Semantic floor identifier. Vanguard never treats the historical procedural FloorBand as a unique canonical floor identity.");
        zoneRadiusMeters = config.Bind(Section, "Zone radius meters", 18.0f,
            new ConfigDescription("Horizontal authoring radius around the zone anchor.", new AcceptableValueRange<float>(2.0f, 80.0f)));
        defaultFloorBelowMeters = config.Bind(Section, "Floor lower Y offset meters", 0.75f,
            new ConfigDescription("Offset subtracted from the local player's standing Y when a zone is created or Ctrl+[ captures its explicit lower bound.", new AcceptableValueRange<float>(0.25f, 4.0f)));
        defaultFloorAboveMeters = config.Bind(Section, "Floor upper Y offset meters", 1.25f,
            new ConfigDescription("Offset added to the local player's standing Y when a zone is created or Ctrl+] captures its explicit upper bound.", new AcceptableValueRange<float>(0.5f, 4.0f)));
        priority = config.Bind(Section, "Slot priority", 50,
            new ConfigDescription("Priority copied into new slots and applied to the nearest existing slot with Ctrl+U. It is persisted for future runtime consumption; current Operators do not consume authored slot priority.", new AcceptableValueRange<int>(0, 100)));
        minimumSquadSize = config.Bind(Section, "Minimum squad size", 1,
            new ConfigDescription("Minimum squad size copied into new slots and applied to the nearest existing slot with Ctrl+U.", new AcceptableValueRange<int>(1, 3)));
        maximumOwnerDistanceMeters = config.Bind(Section, "Maximum owner distance meters", 35.0f,
            new ConfigDescription("Maximum owner distance copied into new slots and applied to the nearest existing slot with Ctrl+U for later common-runtime validation.", new AcceptableValueRange<float>(5.0f, 120.0f)));
        roleAffinity = config.Bind(Section, "Role affinity", string.Empty,
            "Optional role affinity copied into new slots and applied to the nearest existing slot with Ctrl+U.");
        mutualExclusionGroup = config.Bind(Section, "Mutual exclusion group", string.Empty,
            "Optional mutual-exclusion group copied into new slots and applied to the nearest existing slot with Ctrl+U for future collective assignment.");
        watchArcDegrees = config.Bind(Section, "Watch arc degrees", 90.0f,
            new ConfigDescription("Watch arc copied into new slots and applied to the nearest existing slot with Ctrl+U.", new AcceptableValueRange<float>(20.0f, 180.0f)));
        navMeshProjectionMeters = config.Bind(Section, "NavMesh projection meters", 1.5f,
            new ConfigDescription("Maximum bounded projection distance used only during explicit authoring capture/revalidation.", new AcceptableValueRange<float>(0.25f, 4.0f)));
        validationCapsuleRadiusMeters = config.Bind(Section, "Validation capsule radius meters", 0.35f,
            new ConfigDescription("Approximate standing clearance radius used only by explicit authoring validation.", new AcceptableValueRange<float>(0.2f, 0.6f)));
        validationCapsuleHeightMeters = config.Bind(Section, "Validation capsule height meters", 1.75f,
            new ConfigDescription("Approximate standing clearance height used only by explicit authoring validation.", new AcceptableValueRange<float>(1.2f, 2.2f)));
        nearbyEditRadiusMeters = config.Bind(Section, "Nearby edit radius meters", 3.0f,
            new ConfigDescription("Maximum local-player distance used to identify the slot being edited for link-unlink, enable-disable, hard-delete, slot settings and slot recapture commands. Access selection for Ctrl+L is intentionally unbounded.", new AcceptableValueRange<float>(1.0f, 8.0f)));

        isBound = true;
    }

    private static ConfigEntry<KeyboardShortcut> BindShortcut(
        ConfigFile config,
        string name,
        KeyCode mainKey,
        KeyCode modifier,
        string description)
    {
        return config.Bind(Section, name, new KeyboardShortcut(mainKey, modifier), description);
    }

    private static ConfigEntry<KeyboardShortcut> BindShortcut(
        ConfigFile config,
        string name,
        KeyCode mainKey,
        KeyCode modifierA,
        KeyCode modifierB,
        string description)
    {
        return config.Bind(Section, name, new KeyboardShortcut(mainKey, modifierA, modifierB), description);
    }

    public static bool HotkeysEnabled => editorHotkeysEnabled?.Value ?? true;
    public static bool AutomaticAuthoredZoneOccupancyEnabled => automaticAuthoredZoneOccupancyEnabled?.Value ?? true;
    public static KeyboardShortcut ToggleEditorShortcut => toggleEditor?.Value ?? EmptyShortcut;
    public static KeyboardShortcut CreateZoneShortcut => createZone?.Value ?? EmptyShortcut;
    public static KeyboardShortcut CreateNestedZoneShortcut => createNestedZone?.Value ?? EmptyShortcut;
    public static KeyboardShortcut RenameSelectedZoneShortcut => renameSelectedZone?.Value ?? EmptyShortcut;
    public static KeyboardShortcut NextZoneShortcut => nextZone?.Value ?? EmptyShortcut;
    public static KeyboardShortcut ApplyZoneMetadataShortcut => applyZoneMetadata?.Value ?? EmptyShortcut;
    public static KeyboardShortcut CaptureFloorMinShortcut => captureFloorMin?.Value ?? EmptyShortcut;
    public static KeyboardShortcut CaptureFloorMaxShortcut => captureFloorMax?.Value ?? EmptyShortcut;
    public static KeyboardShortcut AddSlotShortcut => addSlot?.Value ?? EmptyShortcut;
    public static KeyboardShortcut CycleSlotTypeShortcut => cycleSlotType?.Value ?? EmptyShortcut;
    public static KeyboardShortcut AddAccessShortcut => addAccess?.Value ?? EmptyShortcut;
    public static KeyboardShortcut NextAccessShortcut => nextAccess?.Value ?? EmptyShortcut;
    public static KeyboardShortcut LinkNearestSlotShortcut => linkNearestSlot?.Value ?? EmptyShortcut;
    public static KeyboardShortcut ApplyNearestSlotSettingsShortcut => applyNearestSlotSettings?.Value ?? EmptyShortcut;
    public static KeyboardShortcut RecaptureNearestSlotShortcut => recaptureNearestSlot?.Value ?? EmptyShortcut;
    public static KeyboardShortcut UpdateSelectedAccessShortcut => updateSelectedAccess?.Value ?? EmptyShortcut;
    public static KeyboardShortcut ToggleNearestSlotEnabledShortcut => toggleNearestSlotEnabled?.Value ?? EmptyShortcut;
    public static KeyboardShortcut DeleteNearestSlotShortcut => deleteNearestSlot?.Value ?? EmptyShortcut;
    public static KeyboardShortcut DeleteSelectedZoneShortcut => deleteSelectedZone?.Value ?? EmptyShortcut;
    public static KeyboardShortcut SaveShortcut => save?.Value ?? EmptyShortcut;
    public static KeyboardShortcut ReloadShortcut => reload?.Value ?? EmptyShortcut;
    public static KeyboardShortcut RevalidateShortcut => revalidate?.Value ?? EmptyShortcut;
    public static KeyboardShortcut ExportInvalidShortcut => exportInvalid?.Value ?? EmptyShortcut;

    public static string DisplayZoneName => NormalizeText(displayZoneName?.Value, "Unnamed Zone");
    public static string BuildingId => NormalizeText(buildingId?.Value, string.Empty);
    public static string FloorId => NormalizeText(floorId?.Value, "floor-unknown");
    public static float ZoneRadiusMeters => zoneRadiusMeters?.Value ?? 18.0f;
    public static float DefaultFloorBelowMeters => defaultFloorBelowMeters?.Value ?? 0.75f;
    public static float DefaultFloorAboveMeters => defaultFloorAboveMeters?.Value ?? 1.25f;
    public static int Priority => priority?.Value ?? 50;
    public static int MinimumSquadSize => minimumSquadSize?.Value ?? 1;
    public static float MaximumOwnerDistanceMeters => maximumOwnerDistanceMeters?.Value ?? 35.0f;
    public static string RoleAffinity => NormalizeText(roleAffinity?.Value, string.Empty);
    public static string MutualExclusionGroup => NormalizeText(mutualExclusionGroup?.Value, string.Empty);
    public static float WatchArcDegrees => watchArcDegrees?.Value ?? 90.0f;
    public static float NavMeshProjectionMeters => navMeshProjectionMeters?.Value ?? 1.5f;
    public static float ValidationCapsuleRadiusMeters => validationCapsuleRadiusMeters?.Value ?? 0.35f;
    public static float ValidationCapsuleHeightMeters => validationCapsuleHeightMeters?.Value ?? 1.75f;
    public static float NearbyEditRadiusMeters => nearbyEditRadiusMeters?.Value ?? 3.0f;

    private static string NormalizeText(string? value, string fallback)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? fallback : normalized;
    }
}
#endif
