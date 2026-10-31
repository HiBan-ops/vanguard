#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
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
// Flow: The player toggles the editor in raid, map data is loaded and edited as HOLD zones/slots plus TC Portal/PassagePath/TacticalAnchor/PoseSlot topology, each change is validated and saved through the store, and the service renders both the editor UI/world guides and the bounded live author snapshot consumed by raid transport.
// Authority boundary: authoring describes HOLD geometry and TC topology; runtime consumption remains disabled for persisted maps and live execution is mediated by the dedicated raid transport/runtime services.
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

    // Draft-only shortcuts are deliberately code-owned rather than F12 settings: they are
    // transient editor controls, not persistent authoring configuration. KeyboardShortcut
    // uses Vanguard's existing BepInEx input path and avoids a direct dependency on the
    // Unity legacy Input module, which is not referenced by the netstandard2.1 client build.
    private static readonly KeyboardShortcut DraftConfirmShortcut = new(KeyCode.Return);
    private static readonly KeyboardShortcut DraftConfirmKeypadShortcut = new(KeyCode.KeypadEnter);
    private static readonly KeyboardShortcut DraftCancelShortcut = new(KeyCode.Escape);
    private static readonly KeyboardShortcut DraftRadiusIncreaseKeypadShortcut = new(KeyCode.KeypadPlus);
    private static readonly KeyboardShortcut DraftRadiusIncreaseMainShortcut = new(KeyCode.Equals);
    private static readonly KeyboardShortcut DraftRadiusIncreaseFallbackShortcut = new(KeyCode.UpArrow, KeyCode.LeftControl);
    private static readonly KeyboardShortcut DraftRadiusDecreaseKeypadShortcut = new(KeyCode.KeypadMinus);
    private static readonly KeyboardShortcut DraftRadiusDecreaseMainShortcut = new(KeyCode.Minus);
    private static readonly KeyboardShortcut DraftRadiusDecreaseFallbackShortcut = new(KeyCode.DownArrow, KeyCode.LeftControl);

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
    private static string portalValidationSetSignature = string.Empty;
    private static ZoneNamePromptMode zoneNamePromptMode;
    private static string zoneNamePromptText = string.Empty;
    private static string zoneNamePromptZoneId = string.Empty;
    private static PendingZoneCreation? pendingZoneCreation;
    private static AuthoringMode currentMode = AuthoringMode.Hold;

    public static bool IsActive => active;
    public static bool HasUnsavedChanges => dirty || VanguardTacticalCoordinationPortalEditor.HasUnsavedChanges;

    public static bool TryBuildLiveAuthorSnapshot(out VanguardTacticalAuthoringLiveAuthorSnapshotDto snapshot)
    {
        snapshot = new VanguardTacticalAuthoringLiveAuthorSnapshotDto();
        if (!active || mapFile is null || activeWorld?.MainPlayer is not Player player
            || string.IsNullOrWhiteSpace(liveSessionId) || string.IsNullOrWhiteSpace(activeMapId))
        {
            return false;
        }

        if (currentMode == AuthoringMode.PassagePath || currentMode == AuthoringMode.TacticalAnchor)
        {
            // PassagePath and TacticalAnchor/PoseSlot authoring are topology-only. Returning no active author snapshot
            // makes live sync close any prior HOLD/Portal authoring state instead of leaking movement authority.
            portalValidationSetSignature = string.Empty;
            return false;
        }

        VanguardTacticalAuthoringMapFile liveMap;
        string selectedZoneId;
        if (currentMode == AuthoringMode.Portal)
        {
            if (!VanguardTacticalCoordinationPortalEditor.TryBuildLiveValidationMap(
                    player.Position,
                    activeMapId,
                    mapFile.MapRevision,
                    mapFile.EftClientVersion,
                    out liveMap,
                    out var validationSetSignature))
            {
                portalValidationSetSignature = string.Empty;
                return false;
            }

            if (!string.Equals(portalValidationSetSignature, validationSetSignature, StringComparison.Ordinal))
            {
                portalValidationSetSignature = validationSetSignature;
                liveRevision = liveRevision == long.MaxValue ? 1 : Math.Max(1L, liveRevision + 1L);
                VanguardTacticalAuthoringLivePreviewClientState.Expect(liveSessionId, activeMapId, liveRevision);
            }
            selectedZoneId = VanguardTacticalAuthoringLiveValidationContract.PortalValidationZoneId;
        }
        else
        {
            portalValidationSetSignature = string.Empty;
            mapFile.RuntimeConsumptionEnabled = false;
            liveMap = mapFile;
            selectedZoneId = GetSelectedZone()?.ZoneId ?? string.Empty;
        }

        snapshot = new VanguardTacticalAuthoringLiveAuthorSnapshotDto
        {
            OwnerProfileId = player.ProfileId ?? string.Empty,
            LiveSessionId = liveSessionId,
            MapId = activeMapId,
            Active = true,
            Revision = Math.Max(1L, liveRevision),
            SelectedZoneId = selectedZoneId,
            MapJson = JsonConvert.SerializeObject(liveMap),
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

        if (zoneNamePromptMode != ZoneNamePromptMode.None || VanguardTacticalCoordinationPortalEditor.HasNamePrompt || VanguardTacticalCoordinationPassagePathEditor.HasNamePrompt || VanguardTacticalCoordinationTacticalAnchorEditor.HasNamePrompt)
        {
            // IMGUI naming prompts own keyboard events. While any authoring domain has an active text
            // transaction, normal authoring hotkeys stay blocked so no unrelated mutation occurs.
            return;
        }

        if (pendingZoneCreation is not null)
        {
            UpdatePendingZoneDraft(player);
            return;
        }

        if (VanguardTacticalCoordinationPortalEditor.HasPendingPortal)
        {
            if (VanguardTacticalCoordinationPortalEditor.UpdatePendingPortal(player, out var portalDraftStatus, out var portalDraftWarning)
                && !string.IsNullOrWhiteSpace(portalDraftStatus))
            {
                SetStatus(portalDraftStatus, portalDraftWarning);
            }
            return;
        }

        if (VanguardTacticalCoordinationPassagePathEditor.HasPendingPath)
        {
            if (VanguardTacticalCoordinationPassagePathEditor.UpdatePendingPath(player, out var passageDraftStatus, out var passageDraftWarning)
                && !string.IsNullOrWhiteSpace(passageDraftStatus))
            {
                SetStatus(passageDraftStatus, passageDraftWarning);
            }
            return;
        }

        if (VanguardTacticalCoordinationTacticalAnchorEditor.HasPendingAnchor)
        {
            if (VanguardTacticalCoordinationTacticalAnchorEditor.UpdatePendingAnchor(player, out var anchorDraftStatus, out var anchorDraftWarning)
                && !string.IsNullOrWhiteSpace(anchorDraftStatus))
            {
                SetStatus(anchorDraftStatus, anchorDraftWarning);
            }
            return;
        }

        if (VanguardTacticalAuthoringOptions.CycleSlotTypeShortcut.IsDown())
        {
            CycleEditorMode();
            return;
        }

        if (VanguardTacticalAuthoringOptions.SaveShortcut.IsDown())
        {
            Save();
            return;
        }

        if (VanguardTacticalAuthoringOptions.ReloadShortcut.IsDown())
        {
            Reload(player);
            return;
        }

        if (currentMode == AuthoringMode.Portal)
        {
            HandlePortalMode(player);
            _ = world;
            return;
        }

        if (currentMode == AuthoringMode.PassagePath)
        {
            HandlePassagePathMode(player);
            _ = world;
            return;
        }

        if (currentMode == AuthoringMode.TacticalAnchor)
        {
            HandleTacticalAnchorMode(player);
            _ = world;
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

        var modeText = currentMode switch
        {
            AuthoringMode.Hold => "HOLD",
            AuthoringMode.Portal => "PORTAL",
            AuthoringMode.PassagePath => "PASSAGE PATH",
            _ => "TACTICAL ANCHOR"
        };
        var saveState = HasUnsavedChanges ? "MODIFIED" : "SAVED";
        GUILayout.BeginArea(new Rect(18f, 110f, 620f, 270f), GUI.skin.box);
        GUILayout.Label($"VANGUARD TACTICAL AUTHORING  |  {modeText}  |  {activeMapId}");

        if (currentMode == AuthoringMode.Hold)
        {
            DrawHoldModeGui(saveState);
        }
        else if (currentMode == AuthoringMode.Portal)
        {
            DrawPortalModeGui(saveState);
        }
        else if (currentMode == AuthoringMode.PassagePath)
        {
            DrawPassagePathModeGui(saveState);
        }
        else
        {
            DrawTacticalAnchorModeGui(saveState);
        }

        GUILayout.Space(5f);
        GUILayout.Label("Ctrl+PgUp mode  |  Ctrl+S save  |  Ctrl+R reload");
        GUILayout.Label("STATUS  |  " + CompactHudText(lastStatus, 96));
        GUILayout.EndArea();
        DrawZoneNamePrompt();
        if (VanguardTacticalCoordinationPortalEditor.DrawNamePrompt(out var portalNameStatus, out var portalNameWarning)
            && !string.IsNullOrWhiteSpace(portalNameStatus))
        {
            SetStatus(portalNameStatus, portalNameWarning);
        }
        if (VanguardTacticalCoordinationPassagePathEditor.DrawNamePrompt(out var passageNameStatus, out var passageNameWarning)
            && !string.IsNullOrWhiteSpace(passageNameStatus))
        {
            SetStatus(passageNameStatus, passageNameWarning);
        }
        if (VanguardTacticalCoordinationTacticalAnchorEditor.DrawNamePrompt(out var anchorNameStatus, out var anchorNameWarning)
            && !string.IsNullOrWhiteSpace(anchorNameStatus))
        {
            SetStatus(anchorNameStatus, anchorNameWarning);
        }
        DrawWorldLabels();
    }

    private static void DrawHoldModeGui(string saveState)
    {
        if (mapFile is null)
        {
            return;
        }

        var selectedZone = GetSelectedZone();
        var totalSlots = mapFile.Zones.Sum(zone => zone.Slots.Count);
        var occupancyState = VanguardTacticalAuthoringOptions.AutomaticAuthoredZoneOccupancyEnabled ? "AUTO HOLD ON" : "AUTO HOLD OFF";
        GUILayout.Label($"{saveState}  |  schema {mapFile.SchemaVersion}  |  zones {mapFile.Zones.Count}  |  hold slots {totalSlots}  |  {occupancyState}");

        if (pendingZoneCreation is not null)
        {
            GUILayout.Space(4f);
            GUILayout.Label(pendingZoneCreation.ForcedNested ? "NEW HOLDZONE PREVIEW  |  FORCED/NESTED" : "NEW HOLDZONE PREVIEW  |  GUARDED");
            GUILayout.Label($"radius {pendingZoneCreation.ZoneRadius:0}m  |  center follows author  |  + / - adjusts 1m");
            GUILayout.Label("Enter freezes center/radius and opens naming  |  Esc cancels preview");
        }
        else if (selectedZone is null)
        {
            GUILayout.Space(4f);
            GUILayout.Label("No HoldZone selected");
            GUILayout.Label("Ctrl+Home starts a movable HoldZone preview.");
        }
        else
        {
            GUILayout.Space(4f);
            GUILayout.Label($"ZONE {selectedZoneIndex + 1}/{mapFile.Zones.Count}  |  {selectedZone.DisplayZoneName}");
            GUILayout.Label($"{DisplayOrDash(selectedZone.BuildingId)} / {DisplayOrDash(selectedZone.FloorId)}  |  radius {selectedZone.ZoneRadius:0.0}m  |  center {GetSelectedZoneCenterDistanceText(selectedZone)}  |  slots {selectedZone.Slots.Count}");
            GUILayout.Label(GetCompactNearestHoldSlotSummary(selectedZone));
        }

        if (mapFile.LegacyPortalCandidates.Count > 0)
        {
            GUILayout.Label($"Legacy Portal candidates: {mapFile.LegacyPortalCandidates.Count}  |  review in PORTAL mode");
        }

        GUILayout.Space(5f);
        GUILayout.Label("Ctrl+Home preview zone  |  Ctrl+Insert HOLD  |  Ctrl+PgDn next zone  |  Ctrl+Shift+N rename");
        GUILayout.Label("Ctrl+P recapture nearby HOLD  |  Ctrl+V validate");
    }

    private static void DrawPortalModeGui(string saveState)
    {
        if (mapFile is null || activeWorld?.MainPlayer is not Player player)
        {
            return;
        }

        var unpromoted = VanguardTacticalCoordinationPortalEditor.CountUnpromotedLegacyCandidates(mapFile);
        GUILayout.Label($"{saveState}  |  Base {VanguardTacticalCoordinationPortalEditor.BasePortalCount} read-only  |  User {VanguardTacticalCoordinationPortalEditor.UserPortalCount}  |  legacy review {unpromoted}");
        GUILayout.Space(4f);
        if (VanguardTacticalCoordinationPortalEditor.HasNamePrompt)
        {
            GUILayout.Label("PORTAL POSITION FROZEN  |  naming transaction active");
            GUILayout.Label("Enter creates User Portal  |  Esc resumes movable preview");
        }
        else if (VanguardTacticalCoordinationPortalEditor.HasPendingPortal)
        {
            GUILayout.Label(VanguardTacticalCoordinationPortalEditor.GetPendingPortalSummary());
            GUILayout.Label("Move to place  |  Enter freezes and names  |  Esc cancel");
        }
        else
        {
            GUILayout.Label(VanguardTacticalCoordinationPortalEditor.GetNearestPortalSummary(player.Position));
            GUILayout.Label("Ctrl+Insert preview Portal  |  Ctrl+End promote nearby legacy candidate");
            GUILayout.Label("Ctrl+Delete enable/disable User  |  Ctrl+Shift+Delete delete User  |  Ctrl+V revalidate");
        }
        GUILayout.Space(4f);
        GUILayout.Label("BASE is package-owned/read-only. USER changes never modify HOLD data.");
    }

    private static void DrawPassagePathModeGui(string saveState)
    {
        if (activeWorld?.MainPlayer is not Player player)
        {
            return;
        }

        GUILayout.Label($"{saveState}  |  Base {VanguardTacticalCoordinationPortalEditor.BasePassagePathCount} read-only  |  User {VanguardTacticalCoordinationPortalEditor.UserPassagePathCount}");
        GUILayout.Space(4f);
        if (VanguardTacticalCoordinationPassagePathEditor.HasNamePrompt)
        {
            GUILayout.Label("PASSAGE PATH FROZEN  |  naming transaction active");
            GUILayout.Label("Enter creates User PassagePath  |  Esc resumes control-point capture");
        }
        else if (VanguardTacticalCoordinationPassagePathEditor.HasPendingPath)
        {
            GUILayout.Label(VanguardTacticalCoordinationPassagePathEditor.GetPendingPathSummary());
            GUILayout.Label("Enter add point  |  Ctrl+Insert finish/name  |  Backspace undo  |  Esc cancel");
        }
        else
        {
            GUILayout.Label(VanguardTacticalCoordinationPassagePathEditor.GetNearestPathSummary(player.Position));
            GUILayout.Label("Ctrl+Insert start path  |  Ctrl+Delete enable/disable User  |  Ctrl+Shift+Delete delete User");
            GUILayout.Label("Ctrl+V re-resolve locally; authored control points are never discarded by resolution failure");
        }
        GUILayout.Space(4f);
        GUILayout.Label("PASSAGE PATH is Tactical Coordination topology only. It does not command Operator movement.");
    }

    private static void DrawTacticalAnchorModeGui(string saveState)
    {
        if (activeWorld?.MainPlayer is not Player player)
        {
            return;
        }

        GUILayout.Label($"{saveState}  |  Base {VanguardTacticalCoordinationTacticalAnchorEditor.BaseAnchorCount} read-only  |  User {VanguardTacticalCoordinationTacticalAnchorEditor.UserAnchorCount}  |  User PoseSlots {VanguardTacticalCoordinationTacticalAnchorEditor.UserPoseSlotCount}");
        GUILayout.Space(4f);
        if (VanguardTacticalCoordinationTacticalAnchorEditor.HasNamePrompt)
        {
            GUILayout.Label("TACTICAL ANCHOR FROZEN  |  naming transaction active");
            GUILayout.Label("Enter creates User TacticalAnchor  |  Esc resumes movable preview");
        }
        else if (VanguardTacticalCoordinationTacticalAnchorEditor.HasPendingAnchor)
        {
            GUILayout.Label(VanguardTacticalCoordinationTacticalAnchorEditor.GetPendingAnchorSummary());
            GUILayout.Label("Move to place  |  + / - radius  |  Enter freeze/name  |  Esc cancel");
        }
        else
        {
            GUILayout.Label(VanguardTacticalCoordinationTacticalAnchorEditor.GetNearestAnchorSummary(player.Position));
            GUILayout.Label("Ctrl+Home preview Anchor  |  Ctrl+Insert capture PoseSlot  |  Ctrl+P recapture nearest PoseSlot");
            GUILayout.Label("Ctrl+Delete enable/disable PoseSlot  |  Ctrl+Shift+Delete delete PoseSlot  |  Ctrl+End enable/disable Anchor");
            GUILayout.Label("Ctrl+Shift+Backspace delete Anchor  |  Ctrl+V revalidate PoseSlot");
        }
        GUILayout.Space(4f);
        GUILayout.Label("Pose capture stores position + view + physical pose. Ctrl+P enriches an existing PoseSlot in place without changing its id.");
    }

    public static void RenderWorld()
    {
        if (!active || mapFile is null || !CameraClass.Exist)
        {
            return;
        }

        var eftCamera = CameraClass.Instance.Camera;
        if (eftCamera == null || Camera.current != eftCamera || !EnsureLineMaterial())
        {
            return;
        }

        if (currentMode == AuthoringMode.Hold && GetSelectedZone() is null && pendingZoneCreation is null)
        {
            return;
        }

        lineMaterial!.SetPass(0);
        GL.PushMatrix();
        GL.Begin(GL.LINES);
        try
        {
            if (currentMode == AuthoringMode.Portal)
            {
                VanguardTacticalCoordinationPortalEditor.DrawWorldLines(mapFile);
                return;
            }

            if (currentMode == AuthoringMode.PassagePath)
            {
                if (activeWorld?.MainPlayer is Player passageAuthor)
                {
                    VanguardTacticalCoordinationPassagePathEditor.DrawWorldLines(passageAuthor);
                }
                return;
            }

            if (currentMode == AuthoringMode.TacticalAnchor)
            {
                if (activeWorld?.MainPlayer is Player anchorAuthor)
                {
                    VanguardTacticalCoordinationTacticalAnchorEditor.DrawWorldLines(anchorAuthor);
                }
                return;
            }

            var zone = GetSelectedZone();
            if (zone is not null)
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

            if (pendingZoneCreation is not null)
            {
                DrawPendingZoneRing(pendingZoneCreation);
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
            Deactivate(HasUnsavedChanges
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
                SetStatus((HasUnsavedChanges
                    ? "Editor resumed from same-raid in-memory state with unsaved changes preserved. Ctrl+S persists them."
                    : "Editor resumed from same-raid in-memory state.") + resumedSelection);
                return;
            }

            ResetMapState();
            var eftClientVersion = Application.version ?? string.Empty;
            var mapRevision = "eft-client-" + (string.IsNullOrWhiteSpace(eftClientVersion) ? "unknown" : eftClientVersion.Trim());
            mapFile = VanguardTacticalAuthoringStore.LoadOrCreate(mapId, mapRevision, eftClientVersion, out var loadedExisting);
            mapFile.RuntimeConsumptionEnabled = false;
            VanguardTacticalCoordinationPortalEditor.Load(mapId, mapRevision, eftClientVersion, out var baseLoaded, out var userLoaded);
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
                ? $"Editor active; schema-{mapFile.SchemaVersion} HOLD data loaded."
                : $"Editor active; new schema-{mapFile.SchemaVersion} HOLD map created in memory.")
                + $" TC Base={(baseLoaded ? "loaded" : "empty")}; TC User={(userLoaded ? "loaded" : "new")}." + selectionStatus);
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
        currentMode = AuthoringMode.Hold;
        liveSessionId = string.Empty;
        liveRevision = 0;
        portalValidationSetSignature = string.Empty;
        ClearZoneNamePromptState();
        VanguardTacticalCoordinationPortalEditor.Reset();
        VanguardTacticalCoordinationPassagePathEditor.ResetDraft();
        VanguardTacticalCoordinationTacticalAnchorEditor.ResetDraft();
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

    private static void CycleEditorMode()
    {
        currentMode = currentMode switch
        {
            AuthoringMode.Hold => AuthoringMode.Portal,
            AuthoringMode.Portal => AuthoringMode.PassagePath,
            AuthoringMode.PassagePath => AuthoringMode.TacticalAnchor,
            _ => AuthoringMode.Hold
        };
        portalValidationSetSignature = string.Empty;
        VanguardTacticalAuthoringLivePreviewClientState.Clear();
        liveRevision = Math.Max(1L, liveRevision + 1L);
        SetStatus(currentMode switch
        {
            AuthoringMode.Hold => "HOLD mode active. Ctrl+Insert captures HOLD slots; Ctrl+Home starts a HoldZone preview.",
            AuthoringMode.Portal => "PORTAL mode active. Ctrl+Insert starts a User Portal preview; TC Base remains read-only.",
            AuthoringMode.PassagePath => "PASSAGE PATH mode active. Ctrl+Insert starts a User PassagePath draft; control points are durable TC User intent and no movement authority is acquired.",
            _ => "TACTICAL ANCHOR mode active. Ctrl+Home previews a User TacticalAnchor; Ctrl+Insert captures PoseSlots by standing and looking in-world."
        });
    }

    private static void HandlePortalMode(Player player)
    {
        if (mapFile is null)
        {
            return;
        }

        if (VanguardTacticalAuthoringOptions.AddSlotShortcut.IsDown())
        {
            SetStatus(VanguardTacticalCoordinationPortalEditor.BeginPortalPreview(player));
        }
        else if (VanguardTacticalAuthoringOptions.AddAccessShortcut.IsDown())
        {
            SetStatus(VanguardTacticalCoordinationPortalEditor.PromoteNearestLegacyCandidate(mapFile, player, out var warning), warning);
        }
        else if (VanguardTacticalAuthoringOptions.DeleteNearestSlotShortcut.IsDown())
        {
            SetStatus(VanguardTacticalCoordinationPortalEditor.DeleteNearestUserPortal(player, out var warning), warning);
        }
        else if (VanguardTacticalAuthoringOptions.ToggleNearestSlotEnabledShortcut.IsDown())
        {
            SetStatus(VanguardTacticalCoordinationPortalEditor.ToggleNearestUserPortalEnabled(player, out var warning), warning);
        }
        else if (VanguardTacticalAuthoringOptions.RevalidateShortcut.IsDown())
        {
            SetStatus(VanguardTacticalCoordinationPortalEditor.RevalidateNearestUserPortal(player, out var warning), warning);
        }
    }

    private static void HandlePassagePathMode(Player player)
    {
        if (VanguardTacticalAuthoringOptions.AddSlotShortcut.IsDown())
        {
            SetStatus(VanguardTacticalCoordinationPassagePathEditor.BeginPathDraft(player));
        }
        else if (VanguardTacticalAuthoringOptions.DeleteNearestSlotShortcut.IsDown())
        {
            SetStatus(VanguardTacticalCoordinationPassagePathEditor.DeleteNearestUserPath(player, out var warning), warning);
        }
        else if (VanguardTacticalAuthoringOptions.ToggleNearestSlotEnabledShortcut.IsDown())
        {
            SetStatus(VanguardTacticalCoordinationPassagePathEditor.ToggleNearestUserPathEnabled(player, out var warning), warning);
        }
        else if (VanguardTacticalAuthoringOptions.RevalidateShortcut.IsDown())
        {
            SetStatus(VanguardTacticalCoordinationPassagePathEditor.RevalidateNearestUserPath(player, out var warning), warning);
        }
    }

    private static void HandleTacticalAnchorMode(Player player)
    {
        if (VanguardTacticalAuthoringOptions.CreateZoneShortcut.IsDown())
        {
            SetStatus(VanguardTacticalCoordinationTacticalAnchorEditor.BeginAnchorPreview(player));
        }
        else if (VanguardTacticalAuthoringOptions.AddSlotShortcut.IsDown())
        {
            SetStatus(VanguardTacticalCoordinationTacticalAnchorEditor.AddPoseSlot(player, out var warning), warning);
        }
        else if (VanguardTacticalAuthoringOptions.RecaptureNearestSlotShortcut.IsDown())
        {
            SetStatus(VanguardTacticalCoordinationTacticalAnchorEditor.RecaptureNearestPoseSlot(player, out var warning), warning);
        }
        else if (VanguardTacticalAuthoringOptions.DeleteNearestSlotShortcut.IsDown())
        {
            SetStatus(VanguardTacticalCoordinationTacticalAnchorEditor.DeleteNearestPoseSlot(player, out var warning), warning);
        }
        else if (VanguardTacticalAuthoringOptions.ToggleNearestSlotEnabledShortcut.IsDown())
        {
            SetStatus(VanguardTacticalCoordinationTacticalAnchorEditor.ToggleNearestPoseSlotEnabled(player, out var warning), warning);
        }
        else if (VanguardTacticalAuthoringOptions.AddAccessShortcut.IsDown())
        {
            SetStatus(VanguardTacticalCoordinationTacticalAnchorEditor.ToggleNearestUserAnchorEnabled(player, out var warning), warning);
        }
        else if (VanguardTacticalAuthoringOptions.DeleteSelectedZoneShortcut.IsDown())
        {
            SetStatus(VanguardTacticalCoordinationTacticalAnchorEditor.DeleteNearestUserAnchor(player, out var warning), warning);
        }
        else if (VanguardTacticalAuthoringOptions.RevalidateShortcut.IsDown())
        {
            SetStatus(VanguardTacticalCoordinationTacticalAnchorEditor.RevalidateNearestPoseSlot(player, out var warning), warning);
        }
    }

    private static void BeginCreateZone(Player player, bool forceNested)
    {
        if (mapFile is null)
        {
            return;
        }

        pendingZoneCreation = new PendingZoneCreation
        {
            Position = player.Position,
            BuildingId = VanguardTacticalAuthoringOptions.BuildingId,
            FloorId = VanguardTacticalAuthoringOptions.FloorId,
            ZoneRadius = GetInitialDraftZoneRadius(),
            DefaultFloorBelowMeters = VanguardTacticalAuthoringOptions.DefaultFloorBelowMeters,
            DefaultFloorAboveMeters = VanguardTacticalAuthoringOptions.DefaultFloorAboveMeters,
            ForcedNested = forceNested
        };
        zoneNamePromptMode = ZoneNamePromptMode.None;
        zoneNamePromptZoneId = string.Empty;
        zoneNamePromptText = string.Empty;
        SetStatus(forceNested
            ? $"FORCED/NESTED HoldZone preview started. Center follows author; radius={pendingZoneCreation.ZoneRadius:0}m. Use + / - in 1m steps, Enter to name/confirm, Esc to cancel."
            : $"Guarded HoldZone preview started. Center follows author; radius={pendingZoneCreation.ZoneRadius:0}m. Use + / - in 1m steps, Enter to name/confirm, Esc to cancel.");
    }

    private static void UpdatePendingZoneDraft(Player player)
    {
        var pending = pendingZoneCreation;
        if (pending is null)
        {
            return;
        }

        pending.Position = player.Position;

        if (IsDraftRadiusIncreasePressed())
        {
            AdjustPendingZoneRadius(+1f);
            return;
        }

        if (IsDraftRadiusDecreasePressed())
        {
            AdjustPendingZoneRadius(-1f);
            return;
        }

        if (DraftConfirmShortcut.IsDown() || DraftConfirmKeypadShortcut.IsDown())
        {
            BeginPendingZoneNaming();
            return;
        }

        if (DraftCancelShortcut.IsDown())
        {
            pendingZoneCreation = null;
            SetStatus("HoldZone preview cancelled; no authoring data was changed.");
        }
    }

    private static bool IsDraftRadiusIncreasePressed()
    {
        return DraftRadiusIncreaseKeypadShortcut.IsDown()
            || DraftRadiusIncreaseMainShortcut.IsDown()
            || DraftRadiusIncreaseFallbackShortcut.IsDown();
    }

    private static bool IsDraftRadiusDecreasePressed()
    {
        return DraftRadiusDecreaseKeypadShortcut.IsDown()
            || DraftRadiusDecreaseMainShortcut.IsDown()
            || DraftRadiusDecreaseFallbackShortcut.IsDown();
    }

    private static void AdjustPendingZoneRadius(float deltaMeters)
    {
        var pending = pendingZoneCreation;
        if (pending is null)
        {
            return;
        }

        pending.ZoneRadius = Mathf.Clamp(Mathf.Round(pending.ZoneRadius + deltaMeters), 2f, 80f);
        SetStatus($"HoldZone preview radius={pending.ZoneRadius:0}m. Center continues to follow author; Enter opens naming, Esc cancels.");
    }

    private static void BeginPendingZoneNaming()
    {
        var pending = pendingZoneCreation;
        if (pending is null)
        {
            return;
        }

        if (!pending.ForcedNested && TryFindBlockingZone(pending.Position, out var blockingZone, out var distance))
        {
            SetStatus($"HoldZone preview cannot be committed here: center is inside '{blockingZone.DisplayZoneName}' ({blockingZone.ZoneId}), centerDistance={distance:0.00}m < radius={blockingZone.ZoneRadius:0.00}m. Move the preview center outside or cancel and restart with Ctrl+Shift+Home for deliberate nested authoring.", warning: true);
            return;
        }

        zoneNamePromptMode = ZoneNamePromptMode.Create;
        zoneNamePromptZoneId = string.Empty;
        zoneNamePromptText = GetNextDefaultZoneName();
        SetStatus($"HoldZone preview frozen at radius={pending.ZoneRadius:0}m. Enter a name and press Enter to create; Escape returns to movable preview.");
    }

    private static float GetInitialDraftZoneRadius()
    {
        if (mapFile is not null && mapFile.Zones.Count > 0)
        {
            return Mathf.Clamp(Mathf.Round(mapFile.Zones[mapFile.Zones.Count - 1].ZoneRadius), 2f, 80f);
        }

        return Mathf.Clamp(Mathf.Round(VanguardTacticalAuthoringOptions.ZoneRadiusMeters), 2f, 80f);
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
            SetStatus($"Created {(forced ? "FORCED/NESTED " : string.Empty)}HoldZone '{zone.DisplayZoneName}' ({zone.ZoneId}) from the validated preview center with radius={zone.ZoneRadius:0}m. Floor bounds are provisional until Ctrl+[ and Ctrl+] are captured.");
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
        if (mode == ZoneNamePromptMode.Create && pendingZoneCreation is not null)
        {
            zoneNamePromptMode = ZoneNamePromptMode.None;
            zoneNamePromptText = string.Empty;
            zoneNamePromptZoneId = string.Empty;
            SetStatus($"Zone naming cancelled; movable HoldZone preview resumed at radius={pendingZoneCreation.ZoneRadius:0}m.");
            return;
        }

        ClearZoneNamePromptState();
        SetStatus($"Zone rename cancelled for {previousZone}; existing name preserved.");
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
        GUILayout.Label("Type the author-facing name. Enter creates the zone; Escape returns to preview; Ctrl+F6 closes the editor.");
        GUI.SetNextControlName(ZoneNamePromptControl);
        zoneNamePromptText = GUILayout.TextField(zoneNamePromptText ?? string.Empty, MaximumZoneNameLength);
        GUI.FocusControl(ZoneNamePromptControl);
        GUILayout.Label(zoneNamePromptMode == ZoneNamePromptMode.Create && pendingZoneCreation?.ForcedNested == true
            ? "Mode: FORCED/NESTED (near-duplicate center guard bypassed deliberately)."
            : "Mode: guarded/explicit.");
        GUILayout.Label("Fail-safe: Esc returns to the movable preview without mutation; Ctrl+F6 cancels the staged zone and closes Tactical Authoring.");
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
        var watchDirection = NormalizeViewDirection(player.LookDirection);
        var projected = TryProjectNavMesh(rawPosition, out var projectedPosition);
        var physicalPose = VanguardTacticalAuthoringPhysicalPoseCapture.Capture(player);
        var now = VanguardTacticalAuthoringStore.UtcNowText();
        var slot = new VanguardTacticalAuthoringSlot
        {
            SlotId = NewId("slot"),
            SlotType = selectedSlotType,
            Position = VanguardVector3Dto.FromVector3(rawPosition),
            NavMeshProjectedPosition = projected ? VanguardVector3Dto.FromVector3(projectedPosition) : null,
            WatchDirection = VanguardVector3Dto.FromVector3(watchDirection),
            PhysicalPoseSnapshot = physicalPose,
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
        SetStatus($"Added unlinked {slot.SlotType} slot {slot.SlotId}; state={slot.ValidationState}; presentation={VanguardTacticalAuthoringPhysicalPoseCapture.Describe(physicalPose)}; runtimeEligible=false. Ctrl+L explicitly links it to the nearest access in this zone (access distance is not capped).");
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

        var watchDirection = NormalizeViewDirection(player.LookDirection);
        if (watchDirection.sqrMagnitude < 0.25f)
        {
            SetStatus("Current gaze cannot produce a valid WatchDirection; slot was not moved.", warning: true);
            return;
        }

        var physicalPose = VanguardTacticalAuthoringPhysicalPoseCapture.Capture(player);
        slot.Position = VanguardVector3Dto.FromVector3(player.Position);
        slot.WatchDirection = VanguardVector3Dto.FromVector3(watchDirection);
        slot.PhysicalPoseSnapshot = physicalPose;
        slot.NavMeshProjectedPosition = null;
        slot.RuntimeEligible = false;
        ValidateSlot(zone, slot, player);
        dirty = true;
        SetStatus($"Recaptured slot {slot.SlotId} at player position with current gaze/presentation={VanguardTacticalAuthoringPhysicalPoseCapture.Describe(physicalPose)}; tactical settings/access/enabled state preserved; state={slot.ValidationState}.");
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

        var watch = NormalizeViewDirection(slot.WatchDirection.ToVector3());
        if (watch.sqrMagnitude < 0.25f)
        {
            invalid = true;
            reasons.Add("watch_direction_invalid");
        }
        else
        {
            // Preserve full authored yaw + pitch. Rendering may flatten the arrow, but persistence must not.
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
        return VanguardTacticalAuthoringNavMeshProjection.TryProjectLocal(position, out projected);
    }

    private static bool HasCompleteLocalAuthorPath(Vector3 playerPosition, Vector3 target)
    {
        return VanguardTacticalAuthoringNavMeshProjection.HasCompleteLocalAuthorPath(playerPosition, target);
    }

    private static bool HasStaticCapsuleClearance(Vector3 projected)
    {
        return VanguardTacticalAuthoringNavMeshProjection.HasStaticCapsuleClearance(projected);
    }

    private static void Save()
    {
        if (mapFile is null)
        {
            return;
        }

        try
        {
            var saved = new List<string>();
            if (dirty)
            {
                VanguardTacticalAuthoringStore.Save(mapFile);
                dirty = false;
                saved.Add("HOLD");
            }
            if (VanguardTacticalCoordinationPortalEditor.HasUnsavedChanges)
            {
                _ = VanguardTacticalCoordinationPortalEditor.Save();
                saved.Add("TC User");
            }

            SetStatus(saved.Count == 0
                ? "Nothing to save."
                : "Saved transactionally: " + string.Join(" + ", saved));
        }
        catch (Exception exception)
        {
            SetStatus($"Save failed closed; prior files preserved by their domain transactions: {exception.GetType().Name}: {exception.Message}", warning: true);
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
            _ = VanguardTacticalCoordinationPortalEditor.Reload(out var baseLoaded, out var userLoaded);
            VanguardTacticalCoordinationPassagePathEditor.ResetDraft();
            var nearestSelected = SelectNearestZoneToPlayer(player.Position, out var nearestDistance, out var nearestInside);
            dirty = false;
            var selectionStatus = nearestSelected
                ? $" Nearest zone: {selectedZoneIndex + 1}/{mapFile.Zones.Count}, center={nearestDistance:0.0}m, inside={nearestInside}."
                : string.Empty;
            SetStatus($"Reloaded HOLD + TC layers; Base={(baseLoaded ? "loaded" : "empty")}, User={(userLoaded ? "loaded" : "new")}." + selectionStatus);
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

    private static string GetCompactNearestHoldSlotSummary(VanguardTacticalAuthoringZone zone)
    {
        try
        {
            var player = activeWorld?.MainPlayer;
            if (player is null || zone.Slots.Count == 0)
            {
                return "Nearby HOLD: none";
            }

            var maxDistance = VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters;
            var nearest = FindNearestSlot(zone, player.Position, maxDistance);
            if (nearest is null)
            {
                return $"Nearby HOLD: none within {maxDistance:0.0}m edit radius";
            }

            var distance = Vector3.Distance(nearest.Position.ToVector3(), player.Position);
            var state = !nearest.Enabled ? "DISABLED" : nearest.ValidationState.ToString().ToUpperInvariant();
            var presentation = VanguardTacticalAuthoringPhysicalPoseCapture.Describe(nearest.PhysicalPoseSnapshot);
            return $"Nearby HOLD: {distance:0.0}m  |  {state}  |  {presentation}";
        }
        catch
        {
            return "Nearby HOLD: unavailable";
        }
    }

    private static string CompactHudText(string value, int maximumLength)
    {
        var normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length <= maximumLength)
        {
            return normalized;
        }

        return normalized.Substring(0, Math.Max(0, maximumLength - 3)) + "...";
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

    private static Vector3 NormalizeViewDirection(Vector3 direction)
    {
        return direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.zero;
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
        if (currentMode == AuthoringMode.PassagePath)
        {
            if (!CameraClass.Exist)
            {
                return;
            }
            var passageCamera = CameraClass.Instance.Camera;
            if (passageCamera != null)
            {
                VanguardTacticalCoordinationPassagePathEditor.DrawWorldLabels(passageCamera);
            }
            return;
        }

        if (currentMode == AuthoringMode.TacticalAnchor)
        {
            if (!CameraClass.Exist)
            {
                return;
            }
            var anchorCamera = CameraClass.Instance.Camera;
            if (anchorCamera != null)
            {
                VanguardTacticalCoordinationTacticalAnchorEditor.DrawWorldLabels(anchorCamera);
            }
            return;
        }

        if (currentMode == AuthoringMode.Portal)
        {
            if (mapFile is null || !CameraClass.Exist)
            {
                return;
            }
            var portalCamera = CameraClass.Instance.Camera;
            if (portalCamera != null)
            {
                VanguardTacticalCoordinationPortalEditor.DrawWorldLabels(mapFile, portalCamera);
            }
            return;
        }

        var zone = GetSelectedZone();
        var draft = pendingZoneCreation;
        if ((zone is null && draft is null) || !CameraClass.Exist)
        {
            return;
        }

        var camera = CameraClass.Instance.Camera;
        if (camera == null)
        {
            return;
        }

        if (zone is not null)
        {
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

                var slotState = !slot.Enabled ? "DISABLED" : slot.ValidationState.ToString().ToUpperInvariant();
                GUI.Label(
                    new Rect(screen.x + 7f, Screen.height - screen.y - 12f, 180f, 22f),
                    $"HOLD  |  {slotState}",
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
                    new Rect(screen.x + 7f, Screen.height - screen.y + 10f, 210f, 22f),
                    "LEGACY ACCESS  |  REVIEW",
                    GUI.skin.box);
            }
        }

        if (draft is not null)
        {
            var world = draft.Position + Vector3.up * 0.35f;
            var screen = camera.WorldToScreenPoint(world);
            if (screen.z > 0f && screen.x >= 0f && screen.x <= Screen.width && screen.y >= 0f && screen.y <= Screen.height)
            {
                GUI.Label(
                    new Rect(screen.x + 7f, Screen.height - screen.y - 12f, 240f, 22f),
                    $"NEW HOLDZONE  |  radius {draft.ZoneRadius:0}m",
                    GUI.skin.box);
            }
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

    private static void DrawPendingZoneRing(PendingZoneCreation draft)
    {
        var center = draft.Position + Vector3.up * VisualHeightOffset;
        GL.Color(new Color(0.25f, 0.85f, 1f, 0.95f));
        for (var segment = 0; segment < ZoneRingSegments; segment++)
        {
            var angleA = (float)(Math.PI * 2.0 * segment / ZoneRingSegments);
            var angleB = (float)(Math.PI * 2.0 * (segment + 1) / ZoneRingSegments);
            var a = center + new Vector3(Mathf.Cos(angleA) * draft.ZoneRadius, 0f, Mathf.Sin(angleA) * draft.ZoneRadius);
            var b = center + new Vector3(Mathf.Cos(angleB) * draft.ZoneRadius, 0f, Mathf.Sin(angleB) * draft.ZoneRadius);
            GL.Vertex(a);
            GL.Vertex(b);
        }

        DrawCross(center, 0.55f);
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

    private enum AuthoringMode
    {
        Hold = 0,
        Portal = 1,
        PassagePath = 2,
        TacticalAnchor = 3
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
