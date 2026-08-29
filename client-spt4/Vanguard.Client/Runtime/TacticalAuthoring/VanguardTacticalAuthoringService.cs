#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Comfort.Common;
using EFT;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;

// Responsibility: owns the local tactical-authoring editor state, visualization and live author snapshot produced by the player client.
// Flow: The player toggles the editor in raid, map data is loaded and edited as zones/slots/access points, each change is validated and saved through the store, and the service renders both the editor UI/world guides and the bounded live author snapshot consumed by raid transport.
// Authority boundary: authoring describes slots/zones; runtime consumption remains disabled for persisted maps and live execution is mediated by the dedicated raid transport/runtime services.
// Invariant: the editor is silent off-raid for live exchange, keeps snapshots raid-scoped and never turns unsaved authoring geometry directly into unbounded AI movement authority.

namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal static class VanguardTacticalAuthoringService
{
    private const float VisualHeightOffset = 0.08f;
    private const int ZoneRingSegments = 48;
    private const int MaximumVisualizedSlots = 96;
    private const int MaximumVisualizedAccesses = 48;
    private const int MaximumWorldLabels = 48;
    private const int MaximumZoneNameLength = 64;
    private const string ZoneNamePromptControl = "VanguardTacticalAuthoringZoneName";

    private static bool active;
    private static bool dirty;
    private static string activeMapId = string.Empty;
    private static string lastStatus = "Editor inactive.";
    private static VanguardTacticalAuthoringMapFile? mapFile;
    private static GameWorld? activeWorld;
    private static int selectedZoneIndex = -1;
    private static int selectedAccessIndex = -1;
    private static VanguardTacticalSlotType selectedSlotType = VanguardTacticalSlotType.EntryGuard;
    private static Material? lineMaterial;
    private static bool? headlessRuntime;
    private static string liveSessionId = string.Empty;
    private static long liveRevision;
    private static ZoneNamePromptMode zoneNamePromptMode;
    private static string zoneNamePromptText = string.Empty;
    private static string zoneNamePromptZoneId = string.Empty;
    private static PendingZoneCreation? pendingZoneCreation;

    public static bool IsActive => active;
    public static bool HasUnsavedChanges => dirty;

    public static bool TryBuildLiveAuthorSnapshot(out VanguardTacticalAuthoringLiveAuthorSnapshotDto snapshot)
    {
        snapshot = new VanguardTacticalAuthoringLiveAuthorSnapshotDto();
        if (!active || mapFile is null || activeWorld?.MainPlayer is not Player player
            || string.IsNullOrWhiteSpace(liveSessionId) || string.IsNullOrWhiteSpace(activeMapId))
        {
            return false;
        }

        mapFile.RuntimeConsumptionEnabled = false;
        snapshot = new VanguardTacticalAuthoringLiveAuthorSnapshotDto
        {
            OwnerProfileId = player.ProfileId ?? string.Empty,
            LiveSessionId = liveSessionId,
            MapId = activeMapId,
            Active = true,
            Revision = Math.Max(1L, liveRevision),
            SelectedZoneId = GetSelectedZone()?.ZoneId ?? string.Empty,
            MapJson = JsonConvert.SerializeObject(mapFile),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ClientBuild = VanguardBuildVersion.BuildLabel
        };
        return !string.IsNullOrWhiteSpace(snapshot.OwnerProfileId);
    }

    public static bool TryGetLiveIdentity(out string sessionId, out string mapId)
    {
        sessionId = liveSessionId;
        mapId = activeMapId;
        return !string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(mapId);
    }

    public static void Tick()
    {
        if (IsHeadlessRuntime())
        {
            if (active)
            {
                Deactivate("Headless runtime detected; tactical authoring is local-player only.");
            }
            return;
        }

        if (!VanguardTacticalAuthoringOptions.HotkeysEnabled)
        {
            if (active)
            {
                Deactivate("Tactical authoring hotkeys disabled in F12 configuration.");
            }
            return;
        }

        if (VanguardTacticalAuthoringOptions.ToggleEditorShortcut.IsDown())
        {
            ToggleEditor();
        }

        if (!active)
        {
            return;
        }

        if (!TryGetRaidContext(out var world, out var player, out var mapId, out var failure))
        {
            Deactivate(failure);
            return;
        }

        if (!ReferenceEquals(activeWorld, world))
        {
            active = false;
            ResetMapState();
            SetStatus("Raid GameWorld changed; prior in-memory authoring state was discarded to prevent cross-raid carry-over.", warning: true);
            return;
        }

        if (!string.Equals(activeMapId, mapId, StringComparison.OrdinalIgnoreCase))
        {
            Deactivate($"Map changed from {activeMapId} to {mapId}; editor closed fail-closed to prevent cross-map writes.");
            return;
        }

        if (mapFile is null)
        {
            Deactivate("Authoring map state is missing; editor closed fail-closed.");
            return;
        }

        if (zoneNamePromptMode != ZoneNamePromptMode.None)
        {
            // The modal text field owns keyboard events through Unity IMGUI. Prompt-local
            // Enter/Escape/emergency-close handling therefore lives in DrawZoneNamePrompt(),
            // before GUILayout.TextField can consume the KeyDown event. While the prompt is open,
            // normal authoring hotkeys stay blocked here so no unrelated mutation can occur.
            return;
        }

        if (VanguardTacticalAuthoringOptions.CreateNestedZoneShortcut.IsDown())
        {
            BeginCreateZone(player, forceNested: true);
        }
        else if (VanguardTacticalAuthoringOptions.CreateZoneShortcut.IsDown())
        {
            BeginCreateZone(player, forceNested: false);
        }
        else if (VanguardTacticalAuthoringOptions.RenameSelectedZoneShortcut.IsDown())
        {
            BeginRenameSelectedZone();
        }
        else if (VanguardTacticalAuthoringOptions.NextZoneShortcut.IsDown())
        {
            SelectNextZone();
        }
        else if (VanguardTacticalAuthoringOptions.ApplyZoneMetadataShortcut.IsDown())
        {
            ApplyZoneMetadata();
        }
        else if (VanguardTacticalAuthoringOptions.CaptureFloorMinShortcut.IsDown())
        {
            CaptureFloorBound(player, lower: true);
        }
        else if (VanguardTacticalAuthoringOptions.CaptureFloorMaxShortcut.IsDown())
        {
            CaptureFloorBound(player, lower: false);
        }
        else if (VanguardTacticalAuthoringOptions.CycleSlotTypeShortcut.IsDown())
        {
            CycleSlotType();
        }
        else if (VanguardTacticalAuthoringOptions.AddSlotShortcut.IsDown())
        {
            AddSlot(player);
        }
        else if (VanguardTacticalAuthoringOptions.AddAccessShortcut.IsDown())
        {
            AddAccess(player);
        }
        else if (VanguardTacticalAuthoringOptions.NextAccessShortcut.IsDown())
        {
            SelectNextAccess();
        }
        else if (VanguardTacticalAuthoringOptions.LinkNearestSlotShortcut.IsDown())
        {
            LinkNearestSlotToNearestAccess(player);
        }
        else if (VanguardTacticalAuthoringOptions.ApplyNearestSlotSettingsShortcut.IsDown())
        {
            ApplyNearestSlotSettings(player);
        }
        else if (VanguardTacticalAuthoringOptions.RecaptureNearestSlotShortcut.IsDown())
        {
            RecaptureNearestSlot(player);
        }
        else if (VanguardTacticalAuthoringOptions.UpdateSelectedAccessShortcut.IsDown())
        {
            RecaptureSelectedAccess(player);
        }
        else if (VanguardTacticalAuthoringOptions.DeleteNearestSlotShortcut.IsDown())
        {
            DeleteNearestSlot(player);
        }
        else if (VanguardTacticalAuthoringOptions.DeleteSelectedZoneShortcut.IsDown())
        {
            DeleteSelectedZone(player);
        }
        else if (VanguardTacticalAuthoringOptions.ToggleNearestSlotEnabledShortcut.IsDown())
        {
            ToggleNearestSlotEnabled(player);
        }
        else if (VanguardTacticalAuthoringOptions.RevalidateShortcut.IsDown())
        {
            RevalidateSelectedZone(player);
        }
        else if (VanguardTacticalAuthoringOptions.SaveShortcut.IsDown())
        {
            Save();
        }
        else if (VanguardTacticalAuthoringOptions.ReloadShortcut.IsDown())
        {
            Reload(player);
        }
        else if (VanguardTacticalAuthoringOptions.ExportInvalidShortcut.IsDown())
        {
            ExportInvalidReport();
        }

        _ = world;
    }

    public static void DrawGui()
    {
        if (!active || mapFile is null)
        {
            return;
        }

        var selectedZone = GetSelectedZone();
        GUILayout.BeginArea(new Rect(18f, 110f, 760f, 590f), GUI.skin.box);
        GUILayout.Label($"VANGUARD TACTICAL AUTHORING - map={activeMapId}");
        GUILayout.Label("AUTHORING DATA remains non-runtime; while this editor is active, the selected zone drives transient headless preview. When closed, saved zones can auto-occupy by owner position if enabled in F12.");
        GUILayout.Label($"File: {VanguardTacticalAuthoringStore.GetMapPath(activeMapId)}");
        GUILayout.Label($"Dirty: {dirty} | Slot type: {selectedSlotType} | Zones: {mapFile.Zones.Count} | Auto occupancy outside editor: {VanguardTacticalAuthoringOptions.AutomaticAuthoredZoneOccupancyEnabled}");

        if (selectedZone is null)
        {
            GUILayout.Label("Selected zone: none. Ctrl+Home starts guarded creation at the current player position.");
        }
        else
        {
            var selectedAccess = GetSelectedAccess(selectedZone);
            GUILayout.Label($"ZONE NAME: {selectedZone.DisplayZoneName}");
            GUILayout.Label($"Zone [{selectedZoneIndex + 1}/{mapFile.Zones.Count}] id={selectedZone.ZoneId}");
            GUILayout.Label($"Building={DisplayOrDash(selectedZone.BuildingId)} | Floor={selectedZone.FloorId} | Y=[{selectedZone.MinY:0.00}, {selectedZone.MaxY:0.00}] | explicitMin={selectedZone.MinYExplicit} | explicitMax={selectedZone.MaxYExplicit}");
            GUILayout.Label($"Radius={selectedZone.ZoneRadius:0.0}m | CenterDist={GetSelectedZoneCenterDistanceText(selectedZone)} | Slots={selectedZone.Slots.Count} | Accesses={selectedZone.Accesses.Count} | Selected access={selectedAccess?.AccessId ?? "none"}");
            GUILayout.Label(GetNearestSlotSummary(selectedZone));
            GUILayout.Label(GetNearestAccessSummary(selectedZone));
        }

        GUILayout.Label(VanguardTacticalAuthoringLivePreviewClientState.OverallSummary);
        GUILayout.Label($"F12 slot template: type={selectedSlotType} | priority={VanguardTacticalAuthoringOptions.Priority} | minSquad={VanguardTacticalAuthoringOptions.MinimumSquadSize} | maxOwner={VanguardTacticalAuthoringOptions.MaximumOwnerDistanceMeters:0.0}m | arc={VanguardTacticalAuthoringOptions.WatchArcDegrees:0}deg");
        GUILayout.Label($"F12 affinities: role={DisplayOrDash(VanguardTacticalAuthoringOptions.RoleAffinity)} | exclusion={DisplayOrDash(VanguardTacticalAuthoringOptions.MutualExclusionGroup)} | nearbyEdit={VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters:0.0}m");
        GUILayout.Space(4f);
        GUILayout.Label("Ctrl+F6 toggle | Ctrl+Home guarded new zone | Ctrl+Shift+Home FORCE nested zone | Ctrl+Shift+N rename");
        GUILayout.Label("Ctrl+PgDn next zone | Ctrl+M apply F12 building/floor/radius (name preserved)");
        GUILayout.Label("Ctrl+[ / Ctrl+] floor min/max | Ctrl+PgUp slot type | Ctrl+Insert add slot | Ctrl+U apply F12 slot settings");
        GUILayout.Label("Ctrl+P recapture nearest slot pos/watch | Ctrl+Delete enable/disable | Ctrl+L link/unlink nearest slot + nearest access");
        GUILayout.Label("Ctrl+End add access | Ctrl+Down next access | Ctrl+Up recapture selected access");
        GUILayout.Label("Ctrl+Shift+Delete HARD DELETE nearest slot | Ctrl+Shift+Backspace HARD DELETE selected zone");
        GUILayout.Label("Ctrl+V revalidate | Ctrl+S save | Ctrl+R reload/undo unsaved deletes | Ctrl+I export invalid/warning report");
        GUILayout.Space(5f);
        GUILayout.Label("Status: " + lastStatus);
        GUILayout.EndArea();
        DrawZoneNamePrompt();
        DrawWorldLabels();
    }

    public static void RenderWorld()
    {
        if (!active || mapFile is null)
        {
            return;
        }

        var zone = GetSelectedZone();
        if (zone is null || !CameraClass.Exist)
        {
            return;
        }

        var eftCamera = CameraClass.Instance.Camera;
        if (eftCamera == null || Camera.current != eftCamera || !EnsureLineMaterial())
        {
            return;
        }

        lineMaterial!.SetPass(0);
        GL.PushMatrix();
        GL.Begin(GL.LINES);
        try
        {
            DrawZoneRing(zone);

            var slotLimit = Math.Min(zone.Slots.Count, MaximumVisualizedSlots);
            for (var index = 0; index < slotLimit; index++)
            {
                DrawSlot(zone.Slots[index]);
            }
            for (var index = 0; index < slotLimit; index++)
            {
                DrawSlotAccessLink(zone, zone.Slots[index]);
            }

            var accessLimit = Math.Min(zone.Accesses.Count, MaximumVisualizedAccesses);
            for (var index = 0; index < accessLimit; index++)
            {
                DrawAccess(zone.Accesses[index], index == selectedAccessIndex);
            }
        }
        finally
        {
            GL.End();
            GL.PopMatrix();
        }
    }

    public static void Shutdown()
    {
        active = false;
        ResetMapState();

        if (lineMaterial != null)
        {
            UnityEngine.Object.Destroy(lineMaterial);
            lineMaterial = null;
        }
    }

    private static void ToggleEditor()
    {
        if (active)
        {
            Deactivate(dirty
                ? "Editor disabled with unsaved changes still in memory. Re-enable in the same raid and Ctrl+S to persist them."
                : "Editor disabled.");
            return;
        }

        if (!TryGetRaidContext(out var world, out var player, out var mapId, out var failure))
        {
            SetStatus("Cannot activate tactical authoring: " + failure, warning: true);
            return;
        }

        try
        {
            if (mapFile is not null
                && ReferenceEquals(activeWorld, world)
                && string.Equals(activeMapId, mapId, StringComparison.OrdinalIgnoreCase))
            {
                mapFile.RuntimeConsumptionEnabled = false;
                liveSessionId = NewId("live");
                liveRevision = 1;
                VanguardTacticalAuthoringLivePreviewClientState.Clear();
                active = true;
                var resumedNearest = SelectNearestZoneToPlayer(player.Position, out var resumedDistance, out var resumedInside);
                var resumedSelection = resumedNearest
                    ? $" Nearest zone selected automatically: {selectedZoneIndex + 1}/{mapFile.Zones.Count}, center={resumedDistance:0.0}m, inside={resumedInside}."
                    : string.Empty;
                SetStatus((dirty
                    ? "Editor resumed from same-raid in-memory state with unsaved changes preserved. Ctrl+S persists them."
                    : "Editor resumed from same-raid in-memory state.") + resumedSelection);
                return;
            }

            ResetMapState();
            var eftClientVersion = Application.version ?? string.Empty;
            var mapRevision = "eft-client-" + (string.IsNullOrWhiteSpace(eftClientVersion) ? "unknown" : eftClientVersion.Trim());
            mapFile = VanguardTacticalAuthoringStore.LoadOrCreate(mapId, mapRevision, eftClientVersion, out var loadedExisting);
            mapFile.RuntimeConsumptionEnabled = false;
            activeWorld = world;
            activeMapId = mapId;
            var nearestSelected = SelectNearestZoneToPlayer(player.Position, out var nearestDistance, out var nearestInside);
            dirty = false;
            liveSessionId = NewId("live");
            liveRevision = 1;
            VanguardTacticalAuthoringLivePreviewClientState.Clear();
            active = true;
            var selectionStatus = nearestSelected
                ? $" Nearest zone selected automatically: {selectedZoneIndex + 1}/{mapFile.Zones.Count}, center={nearestDistance:0.0}m, inside={nearestInside}."
                : string.Empty;
            SetStatus((loadedExisting
                ? $"Editor active; existing schema-{mapFile.SchemaVersion} data loaded. Runtime consumption remains disabled."
                : $"Editor active; new schema-{mapFile.SchemaVersion} map document created in memory. Ctrl+S persists it.") + selectionStatus);
        }
        catch (Exception exception)
        {
            ResetMapState();
            active = false;
            SetStatus($"Authoring activation failed closed: {exception.GetType().Name}: {exception.Message}", warning: true);
        }
    }

    private static void Deactivate(string status)
    {
        ClearZoneNamePromptState();
        active = false;
        SetStatus(status);
    }

    private static void ResetMapState()
    {
        mapFile = null;
        activeWorld = null;
        activeMapId = string.Empty;
        selectedZoneIndex = -1;
        selectedAccessIndex = -1;
        dirty = false;
        liveSessionId = string.Empty;
        liveRevision = 0;
        ClearZoneNamePromptState();
        VanguardTacticalAuthoringLivePreviewClientState.Clear();
    }

    private static bool IsHeadlessRuntime()
    {
        if (!headlessRuntime.HasValue)
        {
            headlessRuntime = VanguardFikaCompat.IsActualHeadlessProcess;
        }

        return headlessRuntime.Value;
    }

    private static bool TryGetRaidContext(out GameWorld? world, out Player player, out string mapId, out string failure)
    {
        player = null!;
        mapId = string.Empty;
        failure = string.Empty;
        try
        {
            world = Singleton<GameWorld>.Instance;
        }
        catch (Exception exception)
        {
            world = null;
            failure = $"GameWorld lookup failed: {exception.GetType().Name}: {exception.Message}";
            return false;
        }

        if (world is null)
        {
            failure = "GameWorld is unavailable.";
            return false;
        }

        var mainPlayer = world.MainPlayer;
        if (mainPlayer is null)
        {
            failure = "GameWorld.MainPlayer is unavailable.";
            return false;
        }

        if (mainPlayer.HealthController?.IsAlive != true)
        {
            failure = "Local main player is not alive.";
            return false;
        }

        var locationId = world.LocationId?.Trim() ?? string.Empty;
        if (locationId.Length == 0)
        {
            failure = "GameWorld.LocationId is empty.";
            return false;
        }

        player = mainPlayer;
        mapId = locationId;
        return true;
    }

    private static void BeginCreateZone(Player player, bool forceNested)
    {
        if (mapFile is null)
        {
            return;
        }

        var position = player.Position;
        if (!forceNested && TryFindBlockingZone(position, out var blockingZone, out var distance))
        {
            SetStatus($"Zone creation blocked: center is inside '{blockingZone.DisplayZoneName}' ({blockingZone.ZoneId}), centerDistance={distance:0.00}m < radius={blockingZone.ZoneRadius:0.00}m, centerY={position.y:0.00} within floorY=[{blockingZone.MinY:0.00}, {blockingZone.MaxY:0.00}]. Move to the horizontal/vertical boundary or outside, or use Ctrl+Shift+Home for deliberate same-volume nested authoring.", warning: true);
            return;
        }

        pendingZoneCreation = new PendingZoneCreation
        {
            Position = position,
            BuildingId = VanguardTacticalAuthoringOptions.BuildingId,
            FloorId = VanguardTacticalAuthoringOptions.FloorId,
            ZoneRadius = VanguardTacticalAuthoringOptions.ZoneRadiusMeters,
            DefaultFloorBelowMeters = VanguardTacticalAuthoringOptions.DefaultFloorBelowMeters,
            DefaultFloorAboveMeters = VanguardTacticalAuthoringOptions.DefaultFloorAboveMeters,
            ForcedNested = forceNested
        };
        zoneNamePromptMode = ZoneNamePromptMode.Create;
        zoneNamePromptZoneId = string.Empty;
        zoneNamePromptText = GetNextDefaultZoneName();
        SetStatus(forceNested
            ? $"FORCED/NESTED zone creation staged at player position. Enter a name and press Enter to create; Escape cancels entirely. Proposed name: {zoneNamePromptText}."
            : $"Guarded zone creation staged at player position. Enter a name and press Enter to create; Escape cancels entirely. Proposed name: {zoneNamePromptText}.");
    }

    private static void BeginRenameSelectedZone()
    {
        var zone = GetSelectedZone();
        if (zone is null)
        {
            SetStatus("Create or select a zone before renaming it.", warning: true);
            return;
        }

        pendingZoneCreation = null;
        zoneNamePromptMode = ZoneNamePromptMode.Rename;
        zoneNamePromptZoneId = zone.ZoneId;
        zoneNamePromptText = NormalizeZoneName(zone.DisplayZoneName, GetNextDefaultZoneName());
        SetStatus($"Renaming '{zone.DisplayZoneName}'. Edit the in-game text, Enter commits, Escape keeps the existing name unchanged.");
    }

    private static void ConfirmZoneNamePrompt()
    {
        if (mapFile is null || zoneNamePromptMode == ZoneNamePromptMode.None)
        {
            ClearZoneNamePromptState();
            return;
        }

        if (zoneNamePromptMode == ZoneNamePromptMode.Create)
        {
            var pending = pendingZoneCreation;
            if (pending is null)
            {
                ClearZoneNamePromptState();
                SetStatus("Zone creation prompt lost its staged data; no zone was created.", warning: true);
                return;
            }

            var name = NormalizeZoneName(zoneNamePromptText, GetNextDefaultZoneName());
            var now = VanguardTacticalAuthoringStore.UtcNowText();
            var zone = new VanguardTacticalAuthoringZone
            {
                ZoneId = NewId("zone"),
                DisplayZoneName = name,
                BuildingId = pending.BuildingId,
                FloorId = pending.FloorId,
                MinY = pending.Position.y - pending.DefaultFloorBelowMeters,
                MaxY = pending.Position.y + pending.DefaultFloorAboveMeters,
                MinYExplicit = false,
                MaxYExplicit = false,
                FloorBoundsProvisional = true,
                ZoneAnchor = VanguardVector3Dto.FromVector3(pending.Position),
                ZoneRadius = pending.ZoneRadius,
                CreatedAt = now,
                CreatedWithBuild = VanguardBuildVersion.BuildLabel,
                LastValidatedAt = string.Empty,
                LastValidatedBuild = string.Empty
            };
            mapFile.Zones.Add(zone);
            selectedZoneIndex = mapFile.Zones.Count - 1;
            selectedAccessIndex = -1;
            dirty = true;
            var forced = pending.ForcedNested;
            ClearZoneNamePromptState();
            SetStatus($"Created {(forced ? "FORCED/NESTED " : string.Empty)}zone '{zone.DisplayZoneName}' ({zone.ZoneId}) at the staged player position. Floor bounds are provisional until Ctrl+[ and Ctrl+] are captured.");
            return;
        }

        var renamedZone = mapFile.Zones.FirstOrDefault(zone => string.Equals(zone.ZoneId, zoneNamePromptZoneId, StringComparison.Ordinal));
        if (renamedZone is null)
        {
            ClearZoneNamePromptState();
            SetStatus("Rename target no longer exists; no data was changed.", warning: true);
            return;
        }

        var previousName = renamedZone.DisplayZoneName;
        var renamed = NormalizeZoneName(zoneNamePromptText, previousName);
        if (!string.Equals(previousName, renamed, StringComparison.Ordinal))
        {
            renamedZone.DisplayZoneName = renamed;
            dirty = true;
        }
        ClearZoneNamePromptState();
        SetStatus(string.Equals(previousName, renamed, StringComparison.Ordinal)
            ? $"Zone name unchanged: '{previousName}'."
            : $"Renamed zone '{previousName}' to '{renamed}'. Ctrl+S persists the change.");
    }

    private static void CancelZoneNamePrompt()
    {
        var mode = zoneNamePromptMode;
        var previousZone = zoneNamePromptZoneId;
        ClearZoneNamePromptState();
        SetStatus(mode == ZoneNamePromptMode.Create
            ? "Zone creation cancelled; no partial zone was created."
            : $"Zone rename cancelled for {previousZone}; existing name preserved.");
    }

    private static void DrawZoneNamePrompt()
    {
        if (zoneNamePromptMode == ZoneNamePromptMode.None)
        {
            return;
        }

        // This prompt is an IMGUI text-input modal. KeyboardShortcut/Input polling from Tick
        // is not authoritative once GUILayout.TextField owns keyboard focus: the OnGUI KeyDown can
        // be consumed before the polling path observes it. Handle the modal keys here, BEFORE the
        // TextField call, consume the event so EFT does not also act on it, and explicitly release
        // keyboardControl so cancel/close can never strand gameplay behind the authoring prompt.
        if (HandleZoneNamePromptGuiKeyDown())
        {
            return;
        }

        GUILayout.BeginArea(new Rect(190f, 275f, 540f, 168f), GUI.skin.window);
        GUILayout.Label(zoneNamePromptMode == ZoneNamePromptMode.Create
            ? "NAME NEW TACTICAL ZONE"
            : "RENAME TACTICAL ZONE");
        GUILayout.Label("Type the reader-friendly name. Enter confirms; Escape cancels; Ctrl+F6 closes the editor.");
        GUI.SetNextControlName(ZoneNamePromptControl);
        zoneNamePromptText = GUILayout.TextField(zoneNamePromptText ?? string.Empty, MaximumZoneNameLength);
        GUI.FocusControl(ZoneNamePromptControl);
        GUILayout.Label(zoneNamePromptMode == ZoneNamePromptMode.Create && pendingZoneCreation?.ForcedNested == true
            ? "Mode: FORCED/NESTED (near-duplicate center guard bypassed deliberately)."
            : "Mode: guarded/explicit.");
        GUILayout.Label("Fail-safe: Esc cancels without mutation; Ctrl+F6 cancels the staged prompt and closes Tactical Authoring.");
        GUILayout.EndArea();
    }

    private static bool HandleZoneNamePromptGuiKeyDown()
    {
        var guiEvent = Event.current;
        if (guiEvent is null || guiEvent.type != EventType.KeyDown)
        {
            return false;
        }

        if (guiEvent.keyCode == KeyCode.Return || guiEvent.keyCode == KeyCode.KeypadEnter)
        {
            guiEvent.Use();
            GUIUtility.keyboardControl = 0;
            ConfirmZoneNamePrompt();
            return true;
        }

        if (guiEvent.keyCode == KeyCode.Escape)
        {
            guiEvent.Use();
            GUIUtility.keyboardControl = 0;
            CancelZoneNamePrompt();
            return true;
        }

        if (guiEvent.keyCode == KeyCode.F6 && (guiEvent.modifiers & EventModifiers.Control) != 0)
        {
            guiEvent.Use();
            GUIUtility.keyboardControl = 0;
            Deactivate(dirty
                ? "Editor disabled from zone-name prompt; staged naming transaction cancelled. Existing unsaved authoring changes remain in memory and can be saved after re-enabling."
                : "Editor disabled from zone-name prompt; staged naming transaction cancelled with no partial zone mutation.");
            return true;
        }

        return false;
    }

    private static void ClearZoneNamePromptState()
    {
        zoneNamePromptMode = ZoneNamePromptMode.None;
        zoneNamePromptText = string.Empty;
        zoneNamePromptZoneId = string.Empty;
        pendingZoneCreation = null;
    }

    private static bool TryFindBlockingZone(Vector3 newCenter, out VanguardTacticalAuthoringZone blockingZone, out float centerDistance)
    {
        blockingZone = null!;
        centerDistance = float.MaxValue;
        if (mapFile is null)
        {
            return false;
        }

        VanguardTacticalAuthoringZone? best = null;
        var bestBoundaryDelta = float.MaxValue;
        foreach (var zone in mapFile.Zones)
        {
            // Authoring creation guards must use the same vertical occupancy truth as live authored slots.
            // Horizontal overlap alone must not prevent authoring another floor.
            // Keep the original strict horizontal boundary contract (distance == radius is
            // allowed), while MinY/MaxY boundaries are inclusive exactly like occupancy.
            var distance = HorizontalDistance(zone.ZoneAnchor.ToVector3(), newCenter);
            if (distance >= zone.ZoneRadius
                || newCenter.y < zone.MinY
                || newCenter.y > zone.MaxY)
            {
                continue;
            }

            var boundaryDelta = distance - zone.ZoneRadius;
            if (best is null
                || boundaryDelta < bestBoundaryDelta
                || (Mathf.Approximately(boundaryDelta, bestBoundaryDelta)
                    && string.CompareOrdinal(zone.ZoneId, best.ZoneId) < 0))
            {
                best = zone;
                bestBoundaryDelta = boundaryDelta;
                centerDistance = distance;
            }
        }

        if (best is null)
        {
            return false;
        }

        blockingZone = best;
        return true;
    }

    private static string GetNextDefaultZoneName()
    {
        var start = Math.Max(1, (mapFile?.Zones.Count ?? 0) + 1);
        for (var ordinal = start; ordinal < start + 10000; ordinal++)
        {
            var candidate = $"Zone {ordinal:00}";
            if (mapFile?.Zones.Any(zone => string.Equals(zone.DisplayZoneName, candidate, StringComparison.OrdinalIgnoreCase)) != true)
            {
                return candidate;
            }
        }
        return "Zone " + Guid.NewGuid().ToString("N").Substring(0, 6);
    }

    private static string NormalizeZoneName(string? value, string fallback)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            normalized = fallback.Trim();
        }
        return normalized.Length <= MaximumZoneNameLength ? normalized : normalized.Substring(0, MaximumZoneNameLength);
    }

    private static void SelectNextZone()
    {
        if (mapFile is null || mapFile.Zones.Count == 0)
        {
            SetStatus("No zones exist on this map.", warning: true);
            return;
        }

        selectedZoneIndex = (selectedZoneIndex + 1 + mapFile.Zones.Count) % mapFile.Zones.Count;
        selectedAccessIndex = mapFile.Zones[selectedZoneIndex].Accesses.Count > 0 ? 0 : -1;
        SetStatus($"Selected zone {mapFile.Zones[selectedZoneIndex].ZoneId}.");
    }

    private static void ApplyZoneMetadata()
    {
        var zone = GetSelectedZone();
        if (zone is null)
        {
            SetStatus("Create or select a zone before applying metadata.", warning: true);
            return;
        }

        // DisplayZoneName has its own explicit in-game rename transaction (Ctrl+Shift+N).
        // Structural F12 tuning must never silently erase a deliberately authored name.
        zone.BuildingId = VanguardTacticalAuthoringOptions.BuildingId;
        zone.FloorId = VanguardTacticalAuthoringOptions.FloorId;
        zone.ZoneRadius = VanguardTacticalAuthoringOptions.ZoneRadiusMeters;
        dirty = true;
        SetStatus($"Applied F12 building/floor/radius metadata to {zone.ZoneId}. DisplayZoneName and existing slot priorities/constraints are intentionally preserved.");
    }

    private static void CaptureFloorBound(Player player, bool lower)
    {
        var zone = GetSelectedZone();
        if (zone is null)
        {
            SetStatus("Create or select a zone before capturing floor bounds.", warning: true);
            return;
        }

        var authorY = player.Position.y;
        var y = lower
            ? authorY - VanguardTacticalAuthoringOptions.DefaultFloorBelowMeters
            : authorY + VanguardTacticalAuthoringOptions.DefaultFloorAboveMeters;

        if (lower)
        {
            if (y >= zone.MaxY - 0.20f)
            {
                SetStatus($"Rejected floor minimum {y:0.00}: it must remain at least 0.20m below current MaxY={zone.MaxY:0.00}.", warning: true);
                return;
            }
            zone.MinY = y;
        }
        else
        {
            if (y <= zone.MinY + 0.20f)
            {
                SetStatus($"Rejected floor maximum {y:0.00}: it must remain at least 0.20m above current MinY={zone.MinY:0.00}.", warning: true);
                return;
            }
            zone.MaxY = y;
        }

        if (lower)
        {
            zone.MinYExplicit = true;
        }
        else
        {
            zone.MaxYExplicit = true;
        }

        zone.FloorBoundsProvisional = !(zone.MinYExplicit && zone.MaxYExplicit);
        dirty = true;
        SetStatus(zone.FloorBoundsProvisional
            ? $"Captured {(lower ? "MinY" : "MaxY")}={y:0.00} from standingY={authorY:0.00} for {zone.ZoneId}; capture the other bound before this floor becomes explicit."
            : $"Captured {(lower ? "MinY" : "MaxY")}={y:0.00} from standingY={authorY:0.00} for {zone.ZoneId}; both floor bounds are now explicit.");
    }

    private static void CycleSlotType()
    {
        var values = (VanguardTacticalSlotType[])Enum.GetValues(typeof(VanguardTacticalSlotType));
        var currentIndex = Array.IndexOf(values, selectedSlotType);
        selectedSlotType = values[(currentIndex + 1 + values.Length) % values.Length];
        SetStatus("Selected slot type: " + selectedSlotType + ".");
    }

    private static void AddSlot(Player player)
    {
        var zone = GetSelectedZone();
        if (zone is null)
        {
            SetStatus("Create or select a zone before adding a tactical slot.", warning: true);
            return;
        }

        var rawPosition = player.Position;
        var watchDirection = FlattenAndNormalize(player.LookDirection);
        var projected = TryProjectNavMesh(rawPosition, out var projectedPosition);
        var now = VanguardTacticalAuthoringStore.UtcNowText();
        var slot = new VanguardTacticalAuthoringSlot
        {
            SlotId = NewId("slot"),
            SlotType = selectedSlotType,
            Position = VanguardVector3Dto.FromVector3(rawPosition),
            NavMeshProjectedPosition = projected ? VanguardVector3Dto.FromVector3(projectedPosition) : null,
            WatchDirection = VanguardVector3Dto.FromVector3(watchDirection),
            WatchArc = VanguardTacticalAuthoringOptions.WatchArcDegrees,
            // Slot creation is intentionally unlinked. Ctrl+L is the sole explicit
            // nearest-slot/nearest-access link authority, avoiding stale hidden selection.
            AssociatedAccessId = string.Empty,
            ConnectedZoneId = string.Empty,
            Priority = VanguardTacticalAuthoringOptions.Priority,
            MinimumSquadSize = VanguardTacticalAuthoringOptions.MinimumSquadSize,
            MaximumOwnerDistance = VanguardTacticalAuthoringOptions.MaximumOwnerDistanceMeters,
            RoleAffinity = VanguardTacticalAuthoringOptions.RoleAffinity,
            MutualExclusionGroup = VanguardTacticalAuthoringOptions.MutualExclusionGroup,
            CreatedAt = now,
            CreatedWithBuild = VanguardBuildVersion.BuildLabel,
            LastValidatedAt = string.Empty,
            LastValidatedBuild = string.Empty,
            Enabled = true,
            AuthoringValid = false,
            RuntimeEligible = false,
            ValidationState = VanguardTacticalAuthoringValidationState.NotValidated,
            ValidationNotes = projected ? "captured; explicit zone revalidation pending" : "captured; NavMesh projection failed"
        };
        zone.Slots.Add(slot);
        ValidateSlot(zone, slot, player);
        dirty = true;
        SetStatus($"Added unlinked {slot.SlotType} slot {slot.SlotId}; state={slot.ValidationState}; runtimeEligible=false. Ctrl+L explicitly links it to the nearest access in this zone (access distance is not capped).");
    }

    private static void AddAccess(Player player)
    {
        var zone = GetSelectedZone();
        if (zone is null)
        {
            SetStatus("Create or select a zone before adding an access marker.", warning: true);
            return;
        }

        var rawPosition = player.Position;
        var projected = TryProjectNavMesh(rawPosition, out var projectedPosition);
        var access = new VanguardTacticalAuthoringAccess
        {
            AccessId = NewId("access"),
            DisplayName = "Access " + (zone.Accesses.Count + 1).ToString(CultureInfo.InvariantCulture),
            Position = VanguardVector3Dto.FromVector3(rawPosition),
            NavMeshProjectedPosition = projected ? VanguardVector3Dto.FromVector3(projectedPosition) : null,
            ApproachDirection = VanguardVector3Dto.FromVector3(FlattenAndNormalize(player.LookDirection)),
            NavMeshProjectionSucceeded = projected,
            CreatedAt = VanguardTacticalAuthoringStore.UtcNowText(),
            CreatedWithBuild = VanguardBuildVersion.BuildLabel
        };
        zone.Accesses.Add(access);
        selectedAccessIndex = zone.Accesses.Count - 1;
        dirty = true;
        SetStatus($"Added access {access.AccessId}; navMeshProjected={projected}.");
    }

    private static void SelectNextAccess()
    {
        var zone = GetSelectedZone();
        if (zone is null || zone.Accesses.Count == 0)
        {
            selectedAccessIndex = -1;
            SetStatus("Selected zone has no access markers.", warning: true);
            return;
        }

        selectedAccessIndex = (selectedAccessIndex + 1 + zone.Accesses.Count) % zone.Accesses.Count;
        SetStatus("Selected access " + zone.Accesses[selectedAccessIndex].AccessId + ".");
    }

    private static void LinkNearestSlotToNearestAccess(Player player)
    {
        var zone = GetSelectedZone();
        if (zone is null)
        {
            SetStatus("Create or select a zone before linking.", warning: true);
            return;
        }

        var editRadius = VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters;
        var slot = FindNearestSlot(zone, player.Position, editRadius);
        if (slot is null)
        {
            SetStatus($"No tactical slot is within the {editRadius:0.0}m nearby edit radius.", warning: true);
            return;
        }

        // Proximity radius identifies only the slot being edited. Access selection is
        // always the physically nearest access in the current zone, with no maximum distance.
        var access = FindNearestAccessUnbounded(zone, player.Position, out var accessIndex);
        if (access is null)
        {
            if (!string.IsNullOrWhiteSpace(slot.AssociatedAccessId))
            {
                var previousAccess = slot.AssociatedAccessId;
                slot.AssociatedAccessId = string.Empty;
                ValidateSlot(zone, slot, player);
                dirty = true;
                SetStatus($"Unlinked nearest slot {slot.SlotId} from missing access {previousAccess}; selected zone has no access markers.");
                return;
            }

            SetStatus($"Selected zone has no access markers. Nearest slot {slot.SlotId} remains unlinked.", warning: true);
            return;
        }

        selectedAccessIndex = accessIndex;
        var accessDistance = Vector3.Distance(access.Position.ToVector3(), player.Position);
        if (string.Equals(slot.AssociatedAccessId, access.AccessId, StringComparison.Ordinal))
        {
            slot.AssociatedAccessId = string.Empty;
            ValidateSlot(zone, slot, player);
            dirty = true;
            SetStatus($"Unlinked nearest slot {slot.SlotId} from nearest access {access.AccessId} ({accessDistance:0.0}m); state={slot.ValidationState}.");
            return;
        }

        var previous = slot.AssociatedAccessId;
        slot.AssociatedAccessId = access.AccessId;
        ValidateSlot(zone, slot, player);
        dirty = true;
        SetStatus(string.IsNullOrWhiteSpace(previous)
            ? $"Linked nearest slot {slot.SlotId} to nearest access {access.AccessId} ({accessDistance:0.0}m, unbounded access search); state={slot.ValidationState}."
            : $"Relinked nearest slot {slot.SlotId} from {previous} to nearest access {access.AccessId} ({accessDistance:0.0}m, unbounded access search); state={slot.ValidationState}.");
    }

    private static void ApplyNearestSlotSettings(Player player)
    {
        var zone = GetSelectedZone();
        if (zone is null)
        {
            SetStatus("Create or select a zone before editing a tactical slot.", warning: true);
            return;
        }

        var slot = FindNearestSlot(zone, player.Position, VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters);
        if (slot is null)
        {
            SetStatus("No tactical slot is within the nearby edit radius.", warning: true);
            return;
        }

        slot.SlotType = selectedSlotType;
        slot.WatchArc = VanguardTacticalAuthoringOptions.WatchArcDegrees;
        slot.Priority = VanguardTacticalAuthoringOptions.Priority;
        slot.MinimumSquadSize = VanguardTacticalAuthoringOptions.MinimumSquadSize;
        slot.MaximumOwnerDistance = VanguardTacticalAuthoringOptions.MaximumOwnerDistanceMeters;
        slot.RoleAffinity = VanguardTacticalAuthoringOptions.RoleAffinity;
        slot.MutualExclusionGroup = VanguardTacticalAuthoringOptions.MutualExclusionGroup;
        slot.RuntimeEligible = false;
        ValidateSlot(zone, slot, player);
        dirty = true;
        SetStatus($"Applied F12 tactical settings to nearest slot {slot.SlotId}; type={slot.SlotType}; priority={slot.Priority}; minSquad={slot.MinimumSquadSize}; maxOwner={slot.MaximumOwnerDistance:0.0}m; runtimeEligible=false.");
    }

    private static void RecaptureNearestSlot(Player player)
    {
        var zone = GetSelectedZone();
        if (zone is null)
        {
            SetStatus("Create or select a zone before recapturing a tactical slot.", warning: true);
            return;
        }

        var slot = FindNearestSlot(zone, player.Position, VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters);
        if (slot is null)
        {
            SetStatus("No tactical slot is within the nearby edit radius.", warning: true);
            return;
        }

        var watchDirection = FlattenAndNormalize(player.LookDirection);
        if (watchDirection.sqrMagnitude < 0.25f)
        {
            SetStatus("Current gaze cannot produce a valid horizontal WatchDirection; slot was not moved.", warning: true);
            return;
        }

        slot.Position = VanguardVector3Dto.FromVector3(player.Position);
        slot.WatchDirection = VanguardVector3Dto.FromVector3(watchDirection);
        slot.NavMeshProjectedPosition = null;
        slot.RuntimeEligible = false;
        ValidateSlot(zone, slot, player);
        dirty = true;
        SetStatus($"Recaptured slot {slot.SlotId} at player position with current gaze; tactical settings/access/enabled state preserved; state={slot.ValidationState}.");
    }

    private static void RecaptureSelectedAccess(Player player)
    {
        var zone = GetSelectedZone();
        var access = zone is null ? null : GetSelectedAccess(zone);
        if (zone is null || access is null)
        {
            SetStatus("A selected zone and selected access are required before recapturing an access marker.", warning: true);
            return;
        }

        var approachDirection = FlattenAndNormalize(player.LookDirection);
        if (approachDirection.sqrMagnitude < 0.25f)
        {
            SetStatus("Current gaze cannot produce a valid horizontal ApproachDirection; access was not moved.", warning: true);
            return;
        }

        var rawPosition = player.Position;
        var projected = TryProjectNavMesh(rawPosition, out var projectedPosition);
        access.Position = VanguardVector3Dto.FromVector3(rawPosition);
        access.ApproachDirection = VanguardVector3Dto.FromVector3(approachDirection);
        access.NavMeshProjectionSucceeded = projected;
        access.NavMeshProjectedPosition = projected ? VanguardVector3Dto.FromVector3(projectedPosition) : null;
        dirty = true;
        SetStatus($"Recaptured access {access.AccessId} at player position; nav={(projected ? "LOCAL_OK" : "DEFERRED")}.");
    }

    private static void ToggleNearestSlotEnabled(Player player)
    {
        var zone = GetSelectedZone();
        if (zone is null)
        {
            SetStatus("Create or select a zone first.", warning: true);
            return;
        }

        var slot = FindNearestSlot(zone, player.Position, VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters);
        if (slot is null)
        {
            SetStatus("No tactical slot is within the nearby edit radius.", warning: true);
            return;
        }

        slot.Enabled = !slot.Enabled;
        slot.RuntimeEligible = false;
        ValidateSlot(zone, slot, player);
        dirty = true;
        SetStatus($"Slot {slot.SlotId} enabled={slot.Enabled}; state={slot.ValidationState}. The operation is reversible and does not delete data.");
    }

    private static void DeleteNearestSlot(Player player)
    {
        var zone = GetSelectedZone();
        if (zone is null)
        {
            SetStatus("Create or select a zone before deleting a tactical slot.", warning: true);
            return;
        }

        var editRadius = VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters;
        var slot = FindNearestSlot(zone, player.Position, editRadius);
        if (slot is null)
        {
            SetStatus($"No tactical slot is within the {editRadius:0.0}m nearby edit radius.", warning: true);
            return;
        }

        var slotId = slot.SlotId;
        var linkedAccess = slot.AssociatedAccessId;
        if (!zone.Slots.Remove(slot))
        {
            SetStatus($"Failed to delete slot {slotId}; authoring state was left unchanged.", warning: true);
            return;
        }

        dirty = true;
        var accessSuffix = string.IsNullOrWhiteSpace(linkedAccess) ? string.Empty : " (access=" + linkedAccess + ")";
        SetStatus($"HARD DELETED slot {slotId}{accessSuffix} from in-memory zone {zone.ZoneId}. Ctrl+S persists; Ctrl+R restores the last saved file if this was accidental.");
    }

    private static void DeleteSelectedZone(Player player)
    {
        if (mapFile is null)
        {
            return;
        }

        var zone = GetSelectedZone();
        if (zone is null)
        {
            SetStatus("No zone is selected for deletion.", warning: true);
            return;
        }

        var deletedZoneId = zone.ZoneId;
        var deletedSlotCount = zone.Slots.Count;
        var deletedAccessCount = zone.Accesses.Count;
        var deletedAccessIds = new HashSet<string>(
            zone.Accesses
                .Where(access => !string.IsNullOrWhiteSpace(access.AccessId))
                .Select(access => access.AccessId),
            StringComparer.Ordinal);

        if (!mapFile.Zones.Remove(zone))
        {
            SetStatus($"Failed to delete zone {deletedZoneId}; authoring state was left unchanged.", warning: true);
            return;
        }

        // Current schema scopes accesses under zones. Clean any unexpected cross-zone
        // references defensively so a hard delete cannot leave dangling authored ids.
        var cleanedAccessLinks = 0;
        var cleanedZoneLinks = 0;
        foreach (var remainingZone in mapFile.Zones)
        {
            foreach (var remainingSlot in remainingZone.Slots)
            {
                if (!string.IsNullOrWhiteSpace(remainingSlot.AssociatedAccessId)
                    && deletedAccessIds.Contains(remainingSlot.AssociatedAccessId))
                {
                    remainingSlot.AssociatedAccessId = string.Empty;
                    cleanedAccessLinks++;
                }

                if (string.Equals(remainingSlot.ConnectedZoneId, deletedZoneId, StringComparison.Ordinal))
                {
                    remainingSlot.ConnectedZoneId = string.Empty;
                    cleanedZoneLinks++;
                }
            }
        }

        selectedZoneIndex = -1;
        selectedAccessIndex = -1;
        if (mapFile.Zones.Count > 0)
        {
            SelectNearestZoneToPlayer(player.Position, out _, out _);
        }

        dirty = true;
        SetStatus($"HARD DELETED zone {deletedZoneId} with {deletedSlotCount} slot(s) and {deletedAccessCount} access(es) from memory; cleanedLinks={cleanedAccessLinks + cleanedZoneLinks}. Ctrl+S persists; Ctrl+R restores the last saved file if this was accidental.");
    }

    private static void RevalidateSelectedZone(Player player)
    {
        var zone = GetSelectedZone();
        if (zone is null)
        {
            SetStatus("Create or select a zone before revalidation.", warning: true);
            return;
        }

        foreach (var access in zone.Accesses)
        {
            var raw = access.Position.ToVector3();
            access.NavMeshProjectionSucceeded = TryProjectNavMesh(raw, out var projectedPosition);
            access.NavMeshProjectedPosition = access.NavMeshProjectionSucceeded
                ? VanguardVector3Dto.FromVector3(projectedPosition)
                : null;
        }

        var valid = 0;
        var warning = 0;
        var invalid = 0;
        foreach (var slot in zone.Slots)
        {
            ValidateSlot(zone, slot, player);
            switch (slot.ValidationState)
            {
                case VanguardTacticalAuthoringValidationState.Valid:
                    valid++;
                    break;
                case VanguardTacticalAuthoringValidationState.Warning:
                    warning++;
                    break;
                case VanguardTacticalAuthoringValidationState.Invalid:
                    invalid++;
                    break;
            }
        }

        zone.LastValidatedAt = VanguardTacticalAuthoringStore.UtcNowText();
        zone.LastValidatedBuild = VanguardBuildVersion.BuildLabel;
        dirty = true;
        SetStatus($"Revalidated zone {zone.ZoneId}: valid={valid}; warning={warning}; invalid={invalid}; runtimeEligible=0; reason=authoring_preview_not_consumed_at_runtime.");
    }

    private static void ValidateSlot(VanguardTacticalAuthoringZone zone, VanguardTacticalAuthoringSlot slot, Player player)
    {
        var reasons = new List<string>();
        var invalid = false;
        var warning = false;
        var raw = slot.Position.ToVector3();

        if (zone.MaxY <= zone.MinY + 0.20f)
        {
            invalid = true;
            reasons.Add("zone_floor_bounds_invalid");
        }
        else
        {
            if (raw.y < zone.MinY || raw.y > zone.MaxY)
            {
                invalid = true;
                reasons.Add("slot_outside_zone_floor_bounds");
            }
            if (zone.FloorBoundsProvisional)
            {
                warning = true;
                reasons.Add("zone_floor_bounds_provisional");
            }
        }

        var anchor = zone.ZoneAnchor.ToVector3();
        var horizontalDelta = new Vector2(raw.x - anchor.x, raw.z - anchor.z);
        if (horizontalDelta.magnitude > zone.ZoneRadius)
        {
            invalid = true;
            reasons.Add("slot_outside_zone_radius");
        }

        var watch = FlattenAndNormalize(slot.WatchDirection.ToVector3());
        if (watch.sqrMagnitude < 0.25f)
        {
            invalid = true;
            reasons.Add("watch_direction_invalid");
        }
        else
        {
            slot.WatchDirection = VanguardVector3Dto.FromVector3(watch);
        }

        if (!string.IsNullOrWhiteSpace(slot.AssociatedAccessId)
            && zone.Accesses.All(access => !string.Equals(access.AccessId, slot.AssociatedAccessId, StringComparison.Ordinal)))
        {
            invalid = true;
            reasons.Add("associated_access_missing");
        }

        if (!TryProjectNavMesh(raw, out var projected))
        {
            // Authoring runs on the local player client while Operator AI/navigation can be
            // authoritative on the Fika headless instance. A local NavMesh miss is therefore
            // diagnostic only and must never reject a player-authored tactical point.
            slot.NavMeshProjectedPosition = null;
            warning = true;
            reasons.Add("navmesh_validation_deferred_to_authoritative_runtime");
        }
        else
        {
            slot.NavMeshProjectedPosition = VanguardVector3Dto.FromVector3(projected);
            if (!HasCompleteLocalAuthorPath(player.Position, projected))
            {
                warning = true;
                reasons.Add("local_author_navmesh_path_incomplete_deferred_to_authoritative_runtime");
            }

            if (!HasStaticCapsuleClearance(projected))
            {
                warning = true;
                reasons.Add("local_static_capsule_check_failed_deferred_to_authoritative_runtime");
            }
        }

        if (!slot.Enabled)
        {
            warning = true;
            reasons.Add("slot_disabled");
        }

        reasons.Add("doorway_clearance_deferred_to_runtime_integration");
        reasons.Add("operator_path_owner_distance_occupancy_and_authority_revalidation_deferred_to_common_runtime_pipeline");
        reasons.Add("runtime_consumption_disabled");

        slot.AuthoringValid = !invalid;
        slot.RuntimeEligible = false;
        slot.ValidationState = invalid
            ? VanguardTacticalAuthoringValidationState.Invalid
            : warning
                ? VanguardTacticalAuthoringValidationState.Warning
                : VanguardTacticalAuthoringValidationState.Valid;
        slot.ValidationNotes = string.Join(";", reasons);
        slot.LastValidatedAt = VanguardTacticalAuthoringStore.UtcNowText();
        slot.LastValidatedBuild = VanguardBuildVersion.BuildLabel;
    }

    private static bool TryProjectNavMesh(Vector3 position, out Vector3 projected)
    {
        // EFT.Player.Position is PlayerBones.BodyTransform.position, not a ground/NavMesh point.
        // The authoring probe therefore needs enough vertical reach to project the body anchor
        // down to the walkable surface. Do not add an upward offset here: that moves the probe
        // farther away from the floor for the local player.
        var sampleRadius = Math.Max(VanguardTacticalAuthoringOptions.NavMeshProjectionMeters, 2.5f);
        if (NavMesh.SamplePosition(position, out var hit, sampleRadius, NavMesh.AllAreas))
        {
            var horizontal = new Vector2(hit.position.x - position.x, hit.position.z - position.z).magnitude;
            var vertical = Math.Abs(hit.position.y - position.y);
            if (horizontal <= 1.75f && vertical <= 2.25f)
            {
                projected = hit.position;
                return true;
            }
        }

        projected = position;
        return false;
    }

    private static bool HasCompleteLocalAuthorPath(Vector3 playerPosition, Vector3 target)
    {
        if (!TryProjectNavMesh(playerPosition, out var start))
        {
            return false;
        }

        var path = new NavMeshPath();
        return NavMesh.CalculatePath(start, target, NavMesh.AllAreas, path)
            && path.status == NavMeshPathStatus.PathComplete;
    }

    private static bool HasStaticCapsuleClearance(Vector3 projected)
    {
        var radius = VanguardTacticalAuthoringOptions.ValidationCapsuleRadiusMeters;
        var height = Math.Max(VanguardTacticalAuthoringOptions.ValidationCapsuleHeightMeters, radius * 2.0f + 0.05f);
        var bottom = projected + Vector3.up * (radius + 0.06f);
        var top = projected + Vector3.up * (height - radius);
        var colliders = Physics.OverlapCapsule(
            bottom,
            top,
            radius,
            LayerMaskClass.HighPolyWithTerrainMask,
            QueryTriggerInteraction.Ignore);

        foreach (var collider in colliders)
        {
            if (collider == null || collider.isTrigger)
            {
                continue;
            }

            if (collider.GetComponentInParent<Player>() != null)
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static void Save()
    {
        if (mapFile is null)
        {
            return;
        }

        try
        {
            VanguardTacticalAuthoringStore.Save(mapFile);
            dirty = false;
            SetStatus($"Saved transactionally: {VanguardTacticalAuthoringStore.GetMapPath(mapFile.MapId)}");
        }
        catch (Exception exception)
        {
            SetStatus($"Save failed closed; prior file preserved: {exception.GetType().Name}: {exception.Message}", warning: true);
        }
    }

    private static void Reload(Player player)
    {
        if (mapFile is null)
        {
            return;
        }

        try
        {
            var reloaded = VanguardTacticalAuthoringStore.Reload(activeMapId);
            mapFile = reloaded;
            var nearestSelected = SelectNearestZoneToPlayer(player.Position, out var nearestDistance, out var nearestInside);
            dirty = false;
            var selectionStatus = nearestSelected
                ? $" Nearest zone selected automatically: {selectedZoneIndex + 1}/{mapFile.Zones.Count}, center={nearestDistance:0.0}m, inside={nearestInside}."
                : string.Empty;
            SetStatus("Reloaded authoring file with exact schema/map identity checks; runtime consumption remains disabled." + selectionStatus);
        }
        catch (Exception exception)
        {
            SetStatus($"Reload failed closed; in-memory data retained: {exception.GetType().Name}: {exception.Message}", warning: true);
        }
    }

    private static void ExportInvalidReport()
    {
        if (mapFile is null)
        {
            return;
        }

        try
        {
            var path = VanguardTacticalAuthoringStore.ExportInvalidReport(mapFile);
            SetStatus("Exported authoring invalid/warning report: " + path);
        }
        catch (Exception exception)
        {
            SetStatus($"Invalid report export failed: {exception.GetType().Name}: {exception.Message}", warning: true);
        }
    }

    private static bool SelectNearestZoneToPlayer(Vector3 playerPosition, out float centerDistance, out bool playerInsideZone)
    {
        centerDistance = 0f;
        playerInsideZone = false;
        if (mapFile is null || mapFile.Zones.Count == 0)
        {
            selectedZoneIndex = -1;
            selectedAccessIndex = -1;
            return false;
        }

        var bestAnyIndex = -1;
        var bestAnySqr = float.PositiveInfinity;
        var bestContainingIndex = -1;
        var bestContainingSqr = float.PositiveInfinity;

        for (var index = 0; index < mapFile.Zones.Count; index++)
        {
            var zone = mapFile.Zones[index];
            var anchor = zone.ZoneAnchor.ToVector3();
            var delta = anchor - playerPosition;
            var anchorSqr = delta.sqrMagnitude;

            if (anchorSqr < bestAnySqr)
            {
                bestAnySqr = anchorSqr;
                bestAnyIndex = index;
            }

            var horizontalSqr = (anchor.x - playerPosition.x) * (anchor.x - playerPosition.x)
                              + (anchor.z - playerPosition.z) * (anchor.z - playerPosition.z);
            var radius = Mathf.Max(0.5f, zone.ZoneRadius);
            var withinHorizontalRadius = horizontalSqr <= radius * radius;
            var withinFloorBand = playerPosition.y >= zone.MinY && playerPosition.y <= zone.MaxY;
            if (withinHorizontalRadius && withinFloorBand && anchorSqr < bestContainingSqr)
            {
                bestContainingSqr = anchorSqr;
                bestContainingIndex = index;
            }
        }

        selectedZoneIndex = bestContainingIndex >= 0 ? bestContainingIndex : bestAnyIndex;
        if (selectedZoneIndex < 0)
        {
            selectedAccessIndex = -1;
            return false;
        }

        var selected = mapFile.Zones[selectedZoneIndex];
        selectedAccessIndex = selected.Accesses.Count > 0 ? 0 : -1;
        playerInsideZone = bestContainingIndex >= 0;
        centerDistance = Mathf.Sqrt(playerInsideZone ? bestContainingSqr : bestAnySqr);
        return true;
    }

    private static string GetNearestSlotSummary(VanguardTacticalAuthoringZone zone)
    {
        try
        {
            var player = activeWorld?.MainPlayer;
            if (player is null || zone.Slots.Count == 0)
            {
                return "Nearest slot: none";
            }

            var maxDistance = VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters;
            var nearest = FindNearestSlot(zone, player.Position, maxDistance);
            if (nearest is null)
            {
                return $"Nearest slot: none within {maxDistance:0.0}m edit radius";
            }

            var distance = Vector3.Distance(nearest.Position.ToVector3(), player.Position);
            var access = string.IsNullOrWhiteSpace(nearest.AssociatedAccessId) ? "-" : nearest.AssociatedAccessId;
            return $"Nearest slot: {nearest.SlotId} | {distance:0.0}m | {nearest.SlotType} | enabled={nearest.Enabled} | state={nearest.ValidationState} | access={access} | priority={nearest.Priority}";
        }
        catch
        {
            return "Nearest slot: unavailable";
        }
    }

    private static string GetNearestAccessSummary(VanguardTacticalAuthoringZone zone)
    {
        try
        {
            var player = activeWorld?.MainPlayer;
            if (player is null || zone.Accesses.Count == 0)
            {
                return "Nearest access: none";
            }

            var nearest = FindNearestAccessUnbounded(zone, player.Position, out var index);
            if (nearest is null)
            {
                return "Nearest access: none";
            }

            var distance = Vector3.Distance(nearest.Position.ToVector3(), player.Position);
            var selected = index == selectedAccessIndex ? "selected" : "not-selected";
            return $"Nearest access: {nearest.AccessId} | {distance:0.0}m | {selected} | nav={(nearest.NavMeshProjectionSucceeded ? "LOCAL_OK" : "DEFERRED")}";
        }
        catch
        {
            return "Nearest access: unavailable";
        }
    }

    private static string GetSelectedZoneCenterDistanceText(VanguardTacticalAuthoringZone zone)
    {
        try
        {
            var player = activeWorld?.MainPlayer;
            if (player is null)
            {
                return "-";
            }

            return Vector3.Distance(zone.ZoneAnchor.ToVector3(), player.Position).ToString("0.0", CultureInfo.InvariantCulture) + "m";
        }
        catch
        {
            return "-";
        }
    }

    private static VanguardTacticalAuthoringZone? GetSelectedZone()
    {
        if (mapFile is null || selectedZoneIndex < 0 || selectedZoneIndex >= mapFile.Zones.Count)
        {
            return null;
        }

        return mapFile.Zones[selectedZoneIndex];
    }

    private static VanguardTacticalAuthoringAccess? GetSelectedAccess(VanguardTacticalAuthoringZone zone)
    {
        if (selectedAccessIndex < 0 || selectedAccessIndex >= zone.Accesses.Count)
        {
            return null;
        }

        return zone.Accesses[selectedAccessIndex];
    }

    private static VanguardTacticalAuthoringSlot? FindNearestSlot(VanguardTacticalAuthoringZone zone, Vector3 playerPosition, float maxDistance)
    {
        VanguardTacticalAuthoringSlot? best = null;
        var bestSqr = maxDistance * maxDistance;
        foreach (var slot in zone.Slots)
        {
            var delta = slot.Position.ToVector3() - playerPosition;
            var sqr = delta.sqrMagnitude;
            if (sqr <= bestSqr)
            {
                bestSqr = sqr;
                best = slot;
            }
        }

        return best;
    }

    private static VanguardTacticalAuthoringAccess? FindNearestAccess(
        VanguardTacticalAuthoringZone zone,
        Vector3 playerPosition,
        float maxDistance,
        out int bestIndex)
    {
        VanguardTacticalAuthoringAccess? best = null;
        bestIndex = -1;
        var bestSqr = maxDistance * maxDistance;
        for (var index = 0; index < zone.Accesses.Count; index++)
        {
            var access = zone.Accesses[index];
            var delta = access.Position.ToVector3() - playerPosition;
            var sqr = delta.sqrMagnitude;
            if (sqr <= bestSqr)
            {
                bestSqr = sqr;
                best = access;
                bestIndex = index;
            }
        }

        return best;
    }

    private static VanguardTacticalAuthoringAccess? FindNearestAccessUnbounded(
        VanguardTacticalAuthoringZone zone,
        Vector3 playerPosition,
        out int bestIndex)
    {
        VanguardTacticalAuthoringAccess? best = null;
        bestIndex = -1;
        var bestSqr = float.PositiveInfinity;
        for (var index = 0; index < zone.Accesses.Count; index++)
        {
            var access = zone.Accesses[index];
            var sqr = (access.Position.ToVector3() - playerPosition).sqrMagnitude;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                best = access;
                bestIndex = index;
            }
        }

        return best;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static Vector3 FlattenAndNormalize(Vector3 direction)
    {
        direction.y = 0f;
        return direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.zero;
    }

    private static string NewId(string prefix)
    {
        return prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 12);
    }

    private static string DisplayOrDash(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static void SetStatus(string message, bool warning = false)
    {
        lastStatus = message;
        if (active)
        {
            liveRevision = liveRevision == long.MaxValue ? 1 : liveRevision + 1;
            if (!string.IsNullOrWhiteSpace(liveSessionId) && !string.IsNullOrWhiteSpace(activeMapId))
            {
                // Immediately invalidate stale headless feedback after any authoring/selection change.
                VanguardTacticalAuthoringLivePreviewClientState.Expect(liveSessionId, activeMapId, liveRevision);
            }
        }
        if (warning)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.TacticalAuthoringStatusTag,
                "TACTICAL_AUTHORING: " + message);
        }
        else
        {
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.TacticalAuthoringStatusTag,
                "TACTICAL_AUTHORING: " + message);
        }
    }

    private static bool EnsureLineMaterial()
    {
        if (lineMaterial != null)
        {
            return true;
        }

        var shader = Shader.Find("Hidden/Internal-Colored");
        if (shader == null)
        {
            return false;
        }

        lineMaterial = new Material(shader)
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        return true;
    }

    private static void DrawWorldLabels()
    {
        var zone = GetSelectedZone();
        if (zone is null || !CameraClass.Exist)
        {
            return;
        }

        var camera = CameraClass.Instance.Camera;
        if (camera == null)
        {
            return;
        }

        var slotLimit = Math.Min(zone.Slots.Count, MaximumWorldLabels);
        for (var index = 0; index < slotLimit; index++)
        {
            var slot = zone.Slots[index];
            var world = (slot.NavMeshProjectedPosition ?? slot.Position).ToVector3() + Vector3.up * 0.35f;
            var screen = camera.WorldToScreenPoint(world);
            if (screen.z <= 0f || screen.x < 0f || screen.x > Screen.width || screen.y < 0f || screen.y > Screen.height)
            {
                continue;
            }

            var accessText = string.IsNullOrWhiteSpace(slot.AssociatedAccessId) ? "-" : slot.AssociatedAccessId;
            var navText = slot.NavMeshProjectedPosition is null ? "nav:DEFERRED" : "nav:LOCAL_OK";
            var pathText = slot.NavMeshProjectedPosition is null
                || slot.ValidationNotes.Contains("local_author_navmesh_path_incomplete_deferred_to_authoritative_runtime", StringComparison.Ordinal)
                ? "path:DEFERRED"
                : "path:LOCAL_OK";
            var capsuleText = slot.NavMeshProjectedPosition is null
                || slot.ValidationNotes.Contains("local_static_capsule_check_failed_deferred_to_authoritative_runtime", StringComparison.Ordinal)
                ? "caps:DEFERRED"
                : "caps:LOCAL_OK";
            var headlessText = VanguardTacticalAuthoringLivePreviewClientState.GetSlotSummary(slot.SlotId);
            GUI.Label(
                new Rect(screen.x + 7f, Screen.height - screen.y - 12f, 660f, string.IsNullOrWhiteSpace(headlessText) ? 22f : 42f),
                string.IsNullOrWhiteSpace(headlessText)
                    ? $"{slot.SlotType} | {slot.ValidationState} | {navText} | {pathText} | {capsuleText} | access={accessText}"
                    : $"{slot.SlotType} | {slot.ValidationState} | {navText} | {pathText} | {capsuleText} | access={accessText}\n{headlessText}",
                GUI.skin.box);
        }

        var accessLimit = Math.Min(zone.Accesses.Count, MaximumWorldLabels);
        for (var index = 0; index < accessLimit; index++)
        {
            var access = zone.Accesses[index];
            var world = (access.NavMeshProjectedPosition ?? access.Position).ToVector3() + Vector3.up * 0.25f;
            var screen = camera.WorldToScreenPoint(world);
            if (screen.z <= 0f || screen.x < 0f || screen.x > Screen.width || screen.y < 0f || screen.y > Screen.height)
            {
                continue;
            }

            GUI.Label(
                new Rect(screen.x + 7f, Screen.height - screen.y + 10f, 260f, 22f),
                $"ACCESS {access.AccessId} | nav={(access.NavMeshProjectionSucceeded ? "LOCAL_OK" : "DEFERRED")}",
                GUI.skin.box);
        }
    }

    private static void DrawZoneRing(VanguardTacticalAuthoringZone zone)
    {
        var center = zone.ZoneAnchor.ToVector3();
        center.y += VisualHeightOffset;
        var color = zone.FloorBoundsProvisional ? new Color(1f, 0.55f, 0.1f, 0.9f) : new Color(0.55f, 0.8f, 0.55f, 0.9f);
        GL.Color(color);
        for (var segment = 0; segment < ZoneRingSegments; segment++)
        {
            var angleA = (float)(Math.PI * 2.0 * segment / ZoneRingSegments);
            var angleB = (float)(Math.PI * 2.0 * (segment + 1) / ZoneRingSegments);
            var a = center + new Vector3(Mathf.Cos(angleA) * zone.ZoneRadius, 0f, Mathf.Sin(angleA) * zone.ZoneRadius);
            var b = center + new Vector3(Mathf.Cos(angleB) * zone.ZoneRadius, 0f, Mathf.Sin(angleB) * zone.ZoneRadius);
            GL.Vertex(a);
            GL.Vertex(b);
        }
    }

    private static void DrawSlot(VanguardTacticalAuthoringSlot slot)
    {
        var position = (slot.NavMeshProjectedPosition ?? slot.Position).ToVector3() + Vector3.up * VisualHeightOffset;
        var color = GetSlotColor(slot);
        GL.Color(color);
        DrawCross(position, 0.35f);

        var direction = FlattenAndNormalize(slot.WatchDirection.ToVector3());
        if (direction.sqrMagnitude > 0.1f)
        {
            var arrowEnd = position + direction * 2.0f;
            GL.Vertex(position);
            GL.Vertex(arrowEnd);
            var side = Vector3.Cross(Vector3.up, direction).normalized;
            GL.Vertex(arrowEnd);
            GL.Vertex(arrowEnd - direction * 0.45f + side * 0.24f);
            GL.Vertex(arrowEnd);
            GL.Vertex(arrowEnd - direction * 0.45f - side * 0.24f);
        }
    }

    private static void DrawSlotAccessLink(VanguardTacticalAuthoringZone zone, VanguardTacticalAuthoringSlot slot)
    {
        if (string.IsNullOrWhiteSpace(slot.AssociatedAccessId))
        {
            return;
        }

        VanguardTacticalAuthoringAccess? linkedAccess = null;
        for (var index = 0; index < zone.Accesses.Count; index++)
        {
            if (string.Equals(zone.Accesses[index].AccessId, slot.AssociatedAccessId, StringComparison.Ordinal))
            {
                linkedAccess = zone.Accesses[index];
                break;
            }
        }

        if (linkedAccess is null)
        {
            return;
        }

        var from = (slot.NavMeshProjectedPosition ?? slot.Position).ToVector3() + Vector3.up * (VisualHeightOffset + 0.03f);
        var to = (linkedAccess.NavMeshProjectedPosition ?? linkedAccess.Position).ToVector3() + Vector3.up * (VisualHeightOffset + 0.03f);
        GL.Color(new Color(0.25f, 0.65f, 1f, 0.65f));
        GL.Vertex(from);
        GL.Vertex(to);
    }

    private static void DrawAccess(VanguardTacticalAuthoringAccess access, bool selected)
    {
        var position = (access.NavMeshProjectedPosition ?? access.Position).ToVector3() + Vector3.up * (VisualHeightOffset + 0.05f);
        GL.Color(selected ? new Color(0.35f, 0.8f, 1f, 1f) : new Color(0.25f, 0.55f, 0.95f, 0.9f));
        const float size = 0.45f;
        GL.Vertex(position + Vector3.forward * size);
        GL.Vertex(position + Vector3.right * size);
        GL.Vertex(position + Vector3.right * size);
        GL.Vertex(position - Vector3.forward * size);
        GL.Vertex(position - Vector3.forward * size);
        GL.Vertex(position - Vector3.right * size);
        GL.Vertex(position - Vector3.right * size);
        GL.Vertex(position + Vector3.forward * size);

        // Access approach is authored data too: render it so Ctrl+Up recapture can be
        // verified visually without logs or a second diagnostic surface.
        var direction = FlattenAndNormalize(access.ApproachDirection.ToVector3());
        if (direction.sqrMagnitude > 0.1f)
        {
            var arrowEnd = position + direction * 1.5f;
            GL.Vertex(position);
            GL.Vertex(arrowEnd);
            var side = Vector3.Cross(Vector3.up, direction).normalized;
            GL.Vertex(arrowEnd);
            GL.Vertex(arrowEnd - direction * 0.35f + side * 0.20f);
            GL.Vertex(arrowEnd);
            GL.Vertex(arrowEnd - direction * 0.35f - side * 0.20f);
        }
    }

    private static void DrawCross(Vector3 position, float size)
    {
        GL.Vertex(position - Vector3.right * size);
        GL.Vertex(position + Vector3.right * size);
        GL.Vertex(position - Vector3.forward * size);
        GL.Vertex(position + Vector3.forward * size);
    }

    private static Color GetSlotColor(VanguardTacticalAuthoringSlot slot)
    {
        if (!slot.Enabled || slot.ValidationState == VanguardTacticalAuthoringValidationState.Invalid)
        {
            return new Color(0.95f, 0.2f, 0.2f, 0.95f);
        }

        // During an active live preview the headless validation is the NavMesh/path authority.
        // Keep persisted/local authoring state untouched, but surface authority feedback directly
        // in the world visualization so tuning a point does not require reading logs.
        if (VanguardTacticalAuthoringLivePreviewClientState.TryGetSlotAuthorityState(slot.SlotId, out var headlessState))
        {
            if (string.Equals(headlessState, "HEADLESS_OK", StringComparison.OrdinalIgnoreCase))
            {
                return new Color(0.2f, 0.9f, 0.35f, 0.95f);
            }

            if (!string.Equals(headlessState, "PENDING", StringComparison.OrdinalIgnoreCase))
            {
                return new Color(0.95f, 0.2f, 0.2f, 0.95f);
            }
        }

        if (slot.ValidationState == VanguardTacticalAuthoringValidationState.Warning
            || slot.ValidationState == VanguardTacticalAuthoringValidationState.NotValidated)
        {
            return new Color(1f, 0.55f, 0.1f, 0.95f);
        }

        return new Color(0.2f, 0.9f, 0.35f, 0.95f);
    }

    private enum ZoneNamePromptMode
    {
        None = 0,
        Create = 1,
        Rename = 2
    }

    private sealed class PendingZoneCreation
    {
        public Vector3 Position;
        public string BuildingId = string.Empty;
        public string FloorId = string.Empty;
        public float ZoneRadius;
        public float DefaultFloorBelowMeters;
        public float DefaultFloorAboveMeters;
        public bool ForcedNested;
    }
}
#endif
