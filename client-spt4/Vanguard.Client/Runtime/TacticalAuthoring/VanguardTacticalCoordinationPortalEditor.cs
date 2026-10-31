#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;

// Responsibility: Implements the shared-editor Portal workflow over physically separate TC Base/User persistence.
// Flow: Portal mode loads both layers, stages a player-following point preview, commits only to User, and can explicitly promote nearby legacy TA candidates without mutating HOLD data.
// Authority boundary: Portal authoring is local map knowledge only; Base stays read-only and no portal is consumed by Tactical Coordination runtime in this milestone.
// Invariant: Cancelled previews leave no data, every write targets TC User only, and legacy candidates require an explicit spatial promotion action before becoming User Portals.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal static class VanguardTacticalCoordinationPortalEditor
{
    private const float VisualHeightOffset = 0.10f;
    private const float LegacyPromotionRadiusMeters = 8.0f;
    private const int MaximumWorldLabels = 64;
    private const int MaximumLiveValidationPortals = 16;
    private const float LiveValidationRadiusMeters = 80.0f;
    private const int MaximumPortalNameLength = 64;
    private const string PortalNamePromptControl = "VanguardTacticalCoordinationPortalName";
    private const string StatusTag = "VANGUARD_TACTICAL_COORDINATION_AUTHORING_STATUS";
    private static readonly KeyboardShortcut DraftConfirmShortcut = new(KeyCode.Return);
    private static readonly KeyboardShortcut DraftConfirmKeypadShortcut = new(KeyCode.KeypadEnter);
    private static readonly KeyboardShortcut DraftCancelShortcut = new(KeyCode.Escape);

    private static VanguardTacticalCoordinationAuthoringMapFile? baseMap;
    private static VanguardTacticalCoordinationAuthoringMapFile? userMap;
    private static PendingPortalCreation? pendingPortal;
    private static bool namePromptActive;
    private static string namePromptText = string.Empty;
    private static string mapRevision = string.Empty;
    private static string eftClientVersion = string.Empty;
    private static bool dirty;

    public static bool HasLoadedState => baseMap is not null && userMap is not null;
    public static bool HasUnsavedChanges => dirty;
    public static bool HasPendingPortal => pendingPortal is not null;
    public static bool HasNamePrompt => namePromptActive;
    public static int BasePortalCount => baseMap?.Portals.Count ?? 0;
    public static int UserPortalCount => userMap?.Portals.Count ?? 0;
    public static int BasePassagePathCount => baseMap?.PassagePaths.Count ?? 0;
    public static int UserPassagePathCount => userMap?.PassagePaths.Count ?? 0;
    public static int BaseTacticalAnchorCount => baseMap?.TacticalAnchors.Count ?? 0;
    public static int UserTacticalAnchorCount => userMap?.TacticalAnchors.Count ?? 0;
    public static int UserPromotionCount => userMap?.LegacyPortalPromotions.Count ?? 0;

    public static bool TryGetCoordinationLayers(
        out VanguardTacticalCoordinationAuthoringMapFile baseLayer,
        out VanguardTacticalCoordinationAuthoringMapFile userLayer)
    {
        if (baseMap is null || userMap is null)
        {
            baseLayer = new VanguardTacticalCoordinationAuthoringMapFile();
            userLayer = new VanguardTacticalCoordinationAuthoringMapFile();
            return false;
        }

        baseLayer = baseMap;
        userLayer = userMap;
        return true;
    }

    public static void MarkUserLayerDirty()
    {
        if (userMap is null)
        {
            throw new InvalidOperationException("TC User layer is not loaded.");
        }
        dirty = true;
    }

    public static void Load(string mapId, string currentMapRevision, string currentEftClientVersion, out bool baseLoaded, out bool userLoaded)
    {
        mapRevision = currentMapRevision ?? string.Empty;
        eftClientVersion = currentEftClientVersion ?? string.Empty;
        baseMap = VanguardTacticalCoordinationAuthoringStore.LoadBaseOrEmpty(mapId, mapRevision, eftClientVersion, out baseLoaded);
        userMap = VanguardTacticalCoordinationAuthoringStore.LoadUserOrCreate(mapId, mapRevision, eftClientVersion, out userLoaded);
        pendingPortal = null;
        namePromptActive = false;
        namePromptText = string.Empty;
        dirty = false;
        VanguardTacticalCoordinationRuntimeReadService.PublishCommittedLocalLayers(baseMap, userMap, "editor_load");

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"event=TC_EDITOR_LAYERS_LOADED; map={mapId}; baseLoaded={baseLoaded}; basePortals={BasePortalCount}; basePassagePaths={BasePassagePathCount}; baseTacticalAnchors={BaseTacticalAnchorCount}; userLoaded={userLoaded}; userPortals={UserPortalCount}; userPassagePaths={UserPassagePathCount}; userTacticalAnchors={UserTacticalAnchorCount}; legacyPromotions={UserPromotionCount}; baseReadOnly=true; userWritable=true");
    }

    public static void Reset()
    {
        baseMap = null;
        userMap = null;
        pendingPortal = null;
        namePromptActive = false;
        namePromptText = string.Empty;
        mapRevision = string.Empty;
        eftClientVersion = string.Empty;
        dirty = false;
    }

    public static string BeginPortalPreview(Player player)
    {
        if (userMap is null)
        {
            return "Portal preview unavailable: TC User layer is not loaded.";
        }
        if (pendingPortal is not null)
        {
            return "Portal preview is already active.";
        }

        namePromptActive = false;
        namePromptText = string.Empty;
        pendingPortal = BuildPendingPortal(player);
        return "Portal preview started. Move to place it; Enter freezes position and opens naming, Esc cancels.";
    }

    public static bool UpdatePendingPortal(Player player, out string status, out bool warning)
    {
        status = string.Empty;
        warning = false;
        if (pendingPortal is null)
        {
            return false;
        }

        pendingPortal = BuildPendingPortal(player);
        if (DraftCancelShortcut.IsDown())
        {
            pendingPortal = null;
            status = "Portal preview cancelled; no TC data changed.";
            return true;
        }

        if (!DraftConfirmShortcut.IsDown() && !DraftConfirmKeypadShortcut.IsDown())
        {
            return false;
        }

        if (userMap is null)
        {
            pendingPortal = null;
            status = "Portal commit failed closed: TC User layer is missing.";
            warning = true;
            return true;
        }

        namePromptActive = true;
        namePromptText = GetNextPortalName();
        status = pendingPortal.LocalValidationState == VanguardTacticalAuthoringValidationState.Valid
            ? "Portal position frozen with local NavMesh/path/clearance proof. Enter a name and press Enter to create; Escape resumes movable preview."
            : "Portal position frozen with local validation deferred to runtime authority. Enter a name and press Enter to create; Escape resumes movable preview.";
        warning = false;
        return true;
    }

    public static bool DrawNamePrompt(out string status, out bool warning)
    {
        status = string.Empty;
        warning = false;
        if (!namePromptActive)
        {
            return false;
        }

        var guiEvent = Event.current;
        if (guiEvent is not null && guiEvent.type == EventType.KeyDown)
        {
            if (guiEvent.keyCode == KeyCode.Return || guiEvent.keyCode == KeyCode.KeypadEnter)
            {
                guiEvent.Use();
                GUIUtility.keyboardControl = 0;
                return CommitNamedPendingPortal(out status, out warning);
            }

            if (guiEvent.keyCode == KeyCode.Escape)
            {
                guiEvent.Use();
                GUIUtility.keyboardControl = 0;
                namePromptActive = false;
                namePromptText = string.Empty;
                status = "Portal naming cancelled; movable preview resumed with no TC mutation.";
                return true;
            }
        }

        GUILayout.BeginArea(new Rect(190f, 275f, 540f, 154f), GUI.skin.window);
        GUILayout.Label("NAME NEW TACTICAL PORTAL");
        GUILayout.Label("Author-facing name. Enter creates the User Portal; Escape resumes movable preview.");
        GUI.SetNextControlName(PortalNamePromptControl);
        namePromptText = GUILayout.TextField(namePromptText ?? string.Empty, MaximumPortalNameLength);
        GUI.FocusControl(PortalNamePromptControl);
        GUILayout.Label("TC Base remains read-only; this transaction can only create a TC User Portal.");
        GUILayout.EndArea();
        return false;
    }

    private static bool CommitNamedPendingPortal(out string status, out bool warning)
    {
        status = string.Empty;
        warning = false;
        if (pendingPortal is null || userMap is null)
        {
            namePromptActive = false;
            namePromptText = string.Empty;
            pendingPortal = null;
            status = "Portal naming commit failed closed: staged preview or TC User layer is missing.";
            warning = true;
            return true;
        }

        var draft = pendingPortal;
        var portalName = NormalizePortalName(namePromptText, GetNextPortalName());
        var portal = new VanguardTacticalAuthoringPortal
        {
            PortalId = NewId("portal"),
            DisplayName = portalName,
            Position = VanguardVector3Dto.FromVector3(draft.Position),
            NavMeshProjectedPosition = draft.NavMeshProjectedPosition.HasValue
                ? VanguardVector3Dto.FromVector3(draft.NavMeshProjectedPosition.Value)
                : null,
            Enabled = true,
            ValidationState = draft.LocalValidationState,
            ValidationNotes = draft.LocalValidationNotes,
            CreatedAt = UtcNowText(),
            CreatedWithBuild = VanguardBuildVersion.BuildLabel
        };

        userMap.Portals.Add(portal);
        pendingPortal = null;
        namePromptActive = false;
        namePromptText = string.Empty;
        dirty = true;
        status = draft.LocalValidationState == VanguardTacticalAuthoringValidationState.Valid
            ? $"User Portal '{portal.DisplayName}' created in memory with local navigation proof. Runtime authority validation is active in PORTAL mode. Ctrl+S persists TC User."
            : $"User Portal '{portal.DisplayName}' created in memory with local validation deferred. Runtime authority validation is active in PORTAL mode. Ctrl+S persists TC User.";

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"event=TC_USER_PORTAL_CREATED; portal={portal.PortalId}; map={userMap.MapId}; displayName={portal.DisplayName}; localState={draft.LocalValidationState}; localProjection={(draft.NavMeshProjectedPosition.HasValue ? "ok" : "deferred")}; persisted=false; baseWrite=false");
        return true;
    }

    public static string PromoteNearestLegacyCandidate(VanguardTacticalAuthoringMapFile authoringMap, Player player, out bool warning)
    {
        warning = false;
        if (userMap is null)
        {
            warning = true;
            return "Legacy Portal promotion unavailable: TC User layer is not loaded.";
        }

        var candidate = FindNearestUnpromotedLegacyCandidate(authoringMap, player.Position, LegacyPromotionRadiusMeters, out var distance);
        if (candidate is null)
        {
            warning = true;
            return $"No unpromoted legacy Portal candidate within {LegacyPromotionRadiusMeters:0}m.";
        }

        var portal = new VanguardTacticalAuthoringPortal
        {
            PortalId = NewId("portal"),
            DisplayName = string.IsNullOrWhiteSpace(candidate.DisplayName) ? GetNextPortalName() : candidate.DisplayName.Trim(),
            Position = CloneVector(candidate.Position),
            NavMeshProjectedPosition = candidate.NavMeshProjectedPosition is null ? null : CloneVector(candidate.NavMeshProjectedPosition),
            Enabled = true,
            ValidationState = candidate.NavMeshProjectedPosition is not null || candidate.NavMeshProjectionSucceeded
                ? VanguardTacticalAuthoringValidationState.Warning
                : VanguardTacticalAuthoringValidationState.NotValidated,
            ValidationNotes = "explicit_legacy_candidate_promotion;recapture_or_revalidate_recommended;runtime_revalidation_required",
            CreatedAt = UtcNowText(),
            CreatedWithBuild = VanguardBuildVersion.BuildLabel
        };
        userMap.Portals.Add(portal);
        userMap.LegacyPortalPromotions.Add(new VanguardTacticalCoordinationLegacyPortalPromotion
        {
            CandidateId = candidate.CandidateId,
            PortalId = portal.PortalId,
            PromotedAt = UtcNowText(),
            PromotedWithBuild = VanguardBuildVersion.BuildLabel
        });
        dirty = true;

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"event=TC_LEGACY_PORTAL_PROMOTED; candidate={candidate.CandidateId}; portal={portal.PortalId}; distance={distance:0.00}; explicit=true; taMutation=false; persisted=false; baseWrite=false");
        return $"Legacy candidate promoted explicitly to User Portal {portal.DisplayName} ({distance:0.0}m). Ctrl+S persists TC User.";
    }

    public static string ToggleNearestUserPortalEnabled(Player player, out bool warning)
    {
        warning = false;
        var portal = FindNearestUserPortal(player.Position, VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters, out var distance);
        if (portal is null)
        {
            warning = true;
            return $"No User Portal within {VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters:0.0}m edit radius.";
        }

        portal.Enabled = !portal.Enabled;
        dirty = true;
        return $"User Portal {portal.DisplayName} {(portal.Enabled ? "enabled" : "disabled")} at {distance:0.0}m. Ctrl+S persists TC User.";
    }

    public static string DeleteNearestUserPortal(Player player, out bool warning)
    {
        warning = false;
        if (userMap is null)
        {
            warning = true;
            return "Portal delete unavailable: TC User layer is not loaded.";
        }

        var portal = FindNearestUserPortal(player.Position, VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters, out var distance);
        if (portal is null)
        {
            warning = true;
            return $"No User Portal within {VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters:0.0}m edit radius.";
        }

        userMap.Portals.Remove(portal);
        userMap.LegacyPortalPromotions.RemoveAll(record => string.Equals(record.PortalId, portal.PortalId, StringComparison.Ordinal));
        dirty = true;
        return $"Deleted User Portal {portal.DisplayName} at {distance:0.0}m in memory. Ctrl+S persists TC User.";
    }

    public static string RevalidateNearestUserPortal(Player player, out bool warning)
    {
        warning = false;
        var portal = FindNearestUserPortal(player.Position, LegacyPromotionRadiusMeters, out var distance);
        if (portal is null)
        {
            warning = true;
            return $"No User Portal within {LegacyPromotionRadiusMeters:0}m to revalidate.";
        }

        var authoredPosition = portal.Position.ToVector3();
        EvaluateLocalPortal(player.Position, authoredPosition, out var projected, out var state, out var notes);
        portal.NavMeshProjectedPosition = projected.HasValue ? VanguardVector3Dto.FromVector3(projected.Value) : null;
        portal.ValidationState = state;
        portal.ValidationNotes = notes;
        dirty = true;
        warning = state != VanguardTacticalAuthoringValidationState.Valid;
        return state == VanguardTacticalAuthoringValidationState.Valid
            ? $"User Portal {portal.DisplayName} locally revalidated at {distance:0.0}m; runtime authority validation remains active. Ctrl+S persists TC User."
            : $"User Portal {portal.DisplayName}: local validation deferred at {distance:0.0}m; runtime authority result remains decisive for the editor display. Ctrl+S persists TC User.";
    }

    public static string Save()
    {
        if (userMap is null)
        {
            return "TC User layer is not loaded.";
        }
        if (!dirty)
        {
            return "TC User layer has no unsaved changes.";
        }

        VanguardTacticalCoordinationAuthoringStore.SaveUser(userMap);
        dirty = false;
        if (baseMap is not null)
        {
            VanguardTacticalCoordinationRuntimeReadService.PublishCommittedLocalLayers(baseMap, userMap, "editor_save");
        }
        return "Saved TC User transactionally: " + VanguardTacticalCoordinationAuthoringStore.GetUserMapPath(userMap.MapId);
    }

    public static string Reload(out bool baseLoaded, out bool userLoaded)
    {
        if (userMap is null)
        {
            throw new InvalidOperationException("TC User layer is not loaded.");
        }

        var mapId = userMap.MapId;
        baseMap = VanguardTacticalCoordinationAuthoringStore.ReloadBaseOrEmpty(mapId, mapRevision, eftClientVersion, out baseLoaded);
        userMap = VanguardTacticalCoordinationAuthoringStore.LoadUserOrCreate(mapId, mapRevision, eftClientVersion, out userLoaded);
        pendingPortal = null;
        namePromptActive = false;
        namePromptText = string.Empty;
        dirty = false;
        VanguardTacticalCoordinationRuntimeReadService.PublishCommittedLocalLayers(baseMap, userMap, "editor_reload");
        return $"Reloaded TC layers: Base portals={BasePortalCount}, Base passage paths={BasePassagePathCount}, User portals={UserPortalCount}, User passage paths={UserPassagePathCount}.";
    }

    public static int CountUnpromotedLegacyCandidates(VanguardTacticalAuthoringMapFile authoringMap)
    {
        if (userMap is null)
        {
            return authoringMap.LegacyPortalCandidates.Count;
        }

        var promoted = new HashSet<string>(userMap.LegacyPortalPromotions.Select(record => record.CandidateId), StringComparer.Ordinal);
        return authoringMap.LegacyPortalCandidates.Count(candidate => !promoted.Contains(candidate.CandidateId));
    }

    public static bool TryBuildLiveValidationMap(
        Vector3 authorPosition,
        string mapId,
        string currentMapRevision,
        string currentEftClientVersion,
        out VanguardTacticalAuthoringMapFile validationMap,
        out string validationSetSignature)
    {
        validationMap = new VanguardTacticalAuthoringMapFile();
        validationSetSignature = string.Empty;
        if (baseMap is null || userMap is null
            || !string.Equals(userMap.MapId, mapId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Validate only a bounded nearest set. This keeps Portal mode observation-only and
        // prevents future Base starter packs from turning editor feedback into unbounded path work.
        // User entries are ordered after Base in the resolved view, so a duplicate id would favor
        // the User record in this transient validation projection.
        var candidates = VanguardTacticalCoordinationAuthoringStore.ResolvePortals(baseMap, userMap)
            .Where(item => item.Portal.Enabled && item.Portal.ValidationState != VanguardTacticalAuthoringValidationState.Invalid)
            .GroupBy(item => item.Portal.PortalId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .Select(item => new
            {
                item.Portal,
                Distance = HorizontalDistance(authorPosition, item.Portal.Position.ToVector3())
            })
            .Where(item => item.Distance <= LiveValidationRadiusMeters)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Portal.PortalId, StringComparer.Ordinal)
            .Take(MaximumLiveValidationPortals)
            .ToList();

        if (candidates.Count == 0)
        {
            return false;
        }

        var now = UtcNowText();
        var zone = new VanguardTacticalAuthoringZone
        {
            ZoneId = VanguardTacticalAuthoringLiveValidationContract.PortalValidationZoneId,
            DisplayZoneName = "TC Portal validation only",
            BuildingId = string.Empty,
            FloorId = string.Empty,
            MinY = -10000f,
            MaxY = 10000f,
            MinYExplicit = true,
            MaxYExplicit = true,
            FloorBoundsProvisional = false,
            ZoneAnchor = VanguardVector3Dto.FromVector3(authorPosition),
            ZoneRadius = 10000f,
            CreatedAt = now,
            CreatedWithBuild = VanguardBuildVersion.BuildLabel,
            LastValidatedAt = now,
            LastValidatedBuild = VanguardBuildVersion.BuildLabel
        };

        foreach (var candidate in candidates)
        {
            zone.Slots.Add(new VanguardTacticalAuthoringSlot
            {
                SlotId = candidate.Portal.PortalId,
                Position = CloneVector(candidate.Portal.Position),
                NavMeshProjectedPosition = null,
                WatchDirection = new VanguardVector3Dto { X = 0f, Y = 0f, Z = 1f },
                Enabled = true,
                AuthoringValid = true,
                ValidationState = VanguardTacticalAuthoringValidationState.Valid,
                ValidationNotes = "tc_portal_transient_runtime_validation_only",
                CreatedAt = now,
                CreatedWithBuild = VanguardBuildVersion.BuildLabel,
                LastValidatedAt = now,
                LastValidatedBuild = VanguardBuildVersion.BuildLabel,
                CompatibilityAssignmentOrderHint = 0
            });
        }

        validationMap = new VanguardTacticalAuthoringMapFile
        {
            MapId = mapId,
            MapRevision = currentMapRevision ?? string.Empty,
            EftClientVersion = currentEftClientVersion ?? string.Empty,
            CreatedAt = now,
            LastSavedAt = string.Empty,
            CreatedWithBuild = VanguardBuildVersion.BuildLabel,
            LastSavedWithBuild = string.Empty,
            HoldZones = new List<VanguardTacticalAuthoringZone> { zone },
            LegacyPortalCandidates = new List<VanguardTacticalAuthoringLegacyPortalCandidate>(),
            RuntimeConsumptionEnabled = false
        };

        validationSetSignature = string.Join("|", candidates
            .Select(item => item.Portal.PortalId)
            .OrderBy(id => id, StringComparer.Ordinal));
        return true;
    }

    public static string GetNearestPortalSummary(Vector3 playerPosition)
    {
        if (baseMap is null || userMap is null)
        {
            return "Nearest Portal: TC layers not loaded";
        }

        VanguardTacticalCoordinationResolvedPortal? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var item in VanguardTacticalCoordinationAuthoringStore.ResolvePortals(baseMap, userMap))
        {
            var distance = HorizontalDistance(playerPosition, (item.Portal.NavMeshProjectedPosition ?? item.Portal.Position).ToVector3());
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = item;
            }
        }

        if (nearest is null)
        {
            return "Nearest Portal: none";
        }

        var state = GetPortalValidationLabel(nearest.Portal);
        return $"Nearest Portal: {nearest.Portal.DisplayName}  |  {nearest.Origin}  |  {nearestDistance:0.0}m  |  {state}";
    }

    public static string GetPendingPortalSummary()
    {
        if (pendingPortal is null)
        {
            return string.Empty;
        }

        return pendingPortal.LocalValidationState == VanguardTacticalAuthoringValidationState.Valid
            ? "NEW PORTAL PREVIEW  |  follows author  |  LOCAL NAV/PATH OK"
            : "NEW PORTAL PREVIEW  |  follows author  |  LOCAL DEFERRED";
    }

    public static void DrawWorldLines(VanguardTacticalAuthoringMapFile authoringMap)
    {
        if (baseMap is null || userMap is null)
        {
            return;
        }

        foreach (var portal in baseMap.Portals)
        {
            DrawPortal(portal, readOnly: true);
        }
        foreach (var portal in userMap.Portals)
        {
            DrawPortal(portal, readOnly: false);
        }

        foreach (var candidate in authoringMap.LegacyPortalCandidates)
        {
            if (IsLegacyCandidatePromoted(candidate.CandidateId))
            {
                continue;
            }
            var position = (candidate.NavMeshProjectedPosition ?? candidate.Position).ToVector3() + Vector3.up * VisualHeightOffset;
            GL.Color(new Color(1f, 0.65f, 0.15f, 0.80f));
            DrawDiamond(position, 0.28f);
        }

        if (pendingPortal is not null)
        {
            var position = (pendingPortal.NavMeshProjectedPosition ?? pendingPortal.Position) + Vector3.up * VisualHeightOffset;
            GL.Color(pendingPortal.LocalValidationState == VanguardTacticalAuthoringValidationState.Valid
                ? new Color(0.20f, 0.75f, 1f, 1f)
                : new Color(1f, 0.55f, 0.10f, 1f));
            DrawDiamond(position, 0.55f);
            DrawCross(position, 0.30f);
        }
    }

    public static void DrawWorldLabels(VanguardTacticalAuthoringMapFile authoringMap, Camera camera)
    {
        if (baseMap is null || userMap is null)
        {
            return;
        }

        var labels = 0;
        foreach (var item in VanguardTacticalCoordinationAuthoringStore.ResolvePortals(baseMap, userMap))
        {
            if (labels >= MaximumWorldLabels)
            {
                break;
            }
            var world = (item.Portal.NavMeshProjectedPosition ?? item.Portal.Position).ToVector3() + Vector3.up * 0.30f;
            if (!TryGetScreen(camera, world, out var screen))
            {
                continue;
            }
            var state = GetPortalValidationLabel(item.Portal);
            GUI.Label(
                new Rect(screen.x + 7f, Screen.height - screen.y - 12f, 250f, 22f),
                $"PORTAL  |  {(item.ReadOnly ? "BASE" : "USER")}  |  {state}",
                GUI.skin.box);
            labels++;
        }

        foreach (var candidate in authoringMap.LegacyPortalCandidates)
        {
            if (labels >= MaximumWorldLabels || IsLegacyCandidatePromoted(candidate.CandidateId))
            {
                continue;
            }
            var world = (candidate.NavMeshProjectedPosition ?? candidate.Position).ToVector3() + Vector3.up * 0.25f;
            if (!TryGetScreen(camera, world, out var screen))
            {
                continue;
            }
            GUI.Label(
                new Rect(screen.x + 7f, Screen.height - screen.y + 10f, 220f, 22f),
                "LEGACY PORTAL  |  REVIEW",
                GUI.skin.box);
            labels++;
        }

        if (pendingPortal is not null)
        {
            var world = (pendingPortal.NavMeshProjectedPosition ?? pendingPortal.Position) + Vector3.up * 0.35f;
            if (TryGetScreen(camera, world, out var screen))
            {
                GUI.Label(
                    new Rect(screen.x + 7f, Screen.height - screen.y - 12f, 250f, 22f),
                    pendingPortal.LocalValidationState == VanguardTacticalAuthoringValidationState.Valid ? "NEW PORTAL  |  LOCAL NAV/PATH OK" : "NEW PORTAL  |  LOCAL DEFERRED",
                    GUI.skin.box);
            }
        }
    }

    private static PendingPortalCreation BuildPendingPortal(Player player)
    {
        var authoredPosition = player.Position;
        EvaluateLocalPortal(player.Position, authoredPosition, out var projected, out var state, out var notes);
        return new PendingPortalCreation(authoredPosition, projected, state, notes);
    }

    private static VanguardTacticalAuthoringPortal? FindNearestUserPortal(Vector3 playerPosition, float maxDistance, out float distance)
    {
        distance = float.MaxValue;
        if (userMap is null)
        {
            return null;
        }

        VanguardTacticalAuthoringPortal? nearest = null;
        foreach (var portal in userMap.Portals)
        {
            var current = HorizontalDistance(playerPosition, (portal.NavMeshProjectedPosition ?? portal.Position).ToVector3());
            if (current <= maxDistance && current < distance)
            {
                distance = current;
                nearest = portal;
            }
        }
        return nearest;
    }

    private static VanguardTacticalAuthoringLegacyPortalCandidate? FindNearestUnpromotedLegacyCandidate(
        VanguardTacticalAuthoringMapFile authoringMap,
        Vector3 playerPosition,
        float maxDistance,
        out float distance)
    {
        distance = float.MaxValue;
        VanguardTacticalAuthoringLegacyPortalCandidate? nearest = null;
        foreach (var candidate in authoringMap.LegacyPortalCandidates)
        {
            if (IsLegacyCandidatePromoted(candidate.CandidateId))
            {
                continue;
            }
            var current = HorizontalDistance(playerPosition, (candidate.NavMeshProjectedPosition ?? candidate.Position).ToVector3());
            if (current <= maxDistance && current < distance)
            {
                distance = current;
                nearest = candidate;
            }
        }
        return nearest;
    }

    private static bool IsLegacyCandidatePromoted(string candidateId)
    {
        return userMap?.LegacyPortalPromotions.Any(record => string.Equals(record.CandidateId, candidateId, StringComparison.Ordinal)) == true;
    }

    private static string GetNextPortalName()
    {
        var existing = UserPortalCount + BasePortalCount;
        return "Portal " + (existing + 1).ToString();
    }

    private static bool TryProjectNavMesh(Vector3 position, out Vector3 projected)
    {
        return VanguardTacticalAuthoringNavMeshProjection.TryProjectLocal(position, out projected);
    }

    private static void EvaluateLocalPortal(
        Vector3 authorPosition,
        Vector3 authoredPosition,
        out Vector3? projected,
        out VanguardTacticalAuthoringValidationState state,
        out string notes)
    {
        var reasons = new List<string>();
        var warning = false;
        if (!TryProjectNavMesh(authoredPosition, out var navPoint))
        {
            projected = null;
            warning = true;
            reasons.Add("navmesh_validation_deferred_to_authoritative_runtime");
        }
        else
        {
            projected = navPoint;
            reasons.Add("author_local_navmesh_projection_ok");
            if (!VanguardTacticalAuthoringNavMeshProjection.HasCompleteLocalAuthorPath(authorPosition, navPoint))
            {
                warning = true;
                reasons.Add("local_author_navmesh_path_incomplete_deferred_to_authoritative_runtime");
            }
            else
            {
                reasons.Add("author_local_navmesh_path_ok");
            }

            if (!VanguardTacticalAuthoringNavMeshProjection.HasStaticCapsuleClearance(navPoint))
            {
                warning = true;
                reasons.Add("local_static_capsule_check_failed_deferred_to_authoritative_runtime");
            }
            else
            {
                reasons.Add("author_local_static_capsule_ok");
            }
        }

        reasons.Add("authoritative_runtime_revalidation_required");
        state = warning ? VanguardTacticalAuthoringValidationState.Warning : VanguardTacticalAuthoringValidationState.Valid;
        notes = string.Join(";", reasons);
    }

    private static string NormalizePortalName(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized.Length <= MaximumPortalNameLength
            ? normalized
            : normalized.Substring(0, MaximumPortalNameLength);
    }

    private static VanguardVector3Dto CloneVector(VanguardVector3Dto value)
    {
        return new VanguardVector3Dto { X = value.X, Y = value.Y, Z = value.Z };
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        return new Vector2(a.x - b.x, a.z - b.z).magnitude;
    }

    private static string NewId(string prefix)
    {
        return prefix + "-" + Guid.NewGuid().ToString("N")[..12];
    }

    private static string UtcNowText()
    {
        return DateTimeOffset.UtcNow.ToString("O");
    }

    private static void DrawPortal(VanguardTacticalAuthoringPortal portal, bool readOnly)
    {
        var position = (portal.NavMeshProjectedPosition ?? portal.Position).ToVector3() + Vector3.up * VisualHeightOffset;
        GL.Color(GetPortalColor(portal));
        var diamondSize = readOnly ? 0.36f : 0.42f;
        DrawDiamond(position, diamondSize);
        DrawCross(position, readOnly ? 0.16f : 0.20f);
    }

    private static Color GetPortalColor(VanguardTacticalAuthoringPortal portal)
    {
        if (!portal.Enabled || portal.ValidationState == VanguardTacticalAuthoringValidationState.Invalid)
        {
            return new Color(0.95f, 0.2f, 0.2f, 0.95f);
        }

        if (VanguardTacticalAuthoringLivePreviewClientState.TryGetSlotAuthorityState(portal.PortalId, out var authorityState))
        {
            if (string.Equals(authorityState, "HEADLESS_OK", StringComparison.OrdinalIgnoreCase))
            {
                return new Color(0.20f, 0.75f, 1f, 0.95f);
            }

            if (!string.Equals(authorityState, "PENDING", StringComparison.OrdinalIgnoreCase))
            {
                return new Color(0.95f, 0.2f, 0.2f, 0.95f);
            }
        }

        if (portal.ValidationState == VanguardTacticalAuthoringValidationState.Valid)
        {
            return new Color(0.20f, 0.75f, 1f, 0.95f);
        }

        return new Color(1f, 0.60f, 0.15f, 0.95f);
    }

    private static string GetPortalValidationLabel(VanguardTacticalAuthoringPortal portal)
    {
        if (!portal.Enabled)
        {
            return "DISABLED";
        }
        if (portal.ValidationState == VanguardTacticalAuthoringValidationState.Invalid)
        {
            return "INVALID";
        }
        if (VanguardTacticalAuthoringLivePreviewClientState.TryGetSlotAuthorityState(portal.PortalId, out var authorityState))
        {
            if (string.Equals(authorityState, "HEADLESS_OK", StringComparison.OrdinalIgnoreCase))
            {
                return "AUTH OK";
            }
            if (string.Equals(authorityState, "PENDING", StringComparison.OrdinalIgnoreCase))
            {
                return "AUTH PENDING";
            }
            return "AUTH " + authorityState.ToUpperInvariant();
        }
        return portal.ValidationState == VanguardTacticalAuthoringValidationState.Valid ? "LOCAL OK" : "WAIT AUTH";
    }

    private static void DrawDiamond(Vector3 position, float size)
    {
        GL.Vertex(position + Vector3.forward * size);
        GL.Vertex(position + Vector3.right * size);
        GL.Vertex(position + Vector3.right * size);
        GL.Vertex(position - Vector3.forward * size);
        GL.Vertex(position - Vector3.forward * size);
        GL.Vertex(position - Vector3.right * size);
        GL.Vertex(position - Vector3.right * size);
        GL.Vertex(position + Vector3.forward * size);
    }

    private static void DrawCross(Vector3 position, float size)
    {
        GL.Vertex(position - Vector3.right * size);
        GL.Vertex(position + Vector3.right * size);
        GL.Vertex(position - Vector3.forward * size);
        GL.Vertex(position + Vector3.forward * size);
    }

    private static bool TryGetScreen(Camera camera, Vector3 world, out Vector3 screen)
    {
        screen = camera.WorldToScreenPoint(world);
        return screen.z > 0f && screen.x >= 0f && screen.x <= Screen.width && screen.y >= 0f && screen.y <= Screen.height;
    }

    private sealed class PendingPortalCreation
    {
        public PendingPortalCreation(
            Vector3 position,
            Vector3? navMeshProjectedPosition,
            VanguardTacticalAuthoringValidationState localValidationState,
            string localValidationNotes)
        {
            Position = position;
            NavMeshProjectedPosition = navMeshProjectedPosition;
            LocalValidationState = localValidationState;
            LocalValidationNotes = localValidationNotes ?? string.Empty;
        }

        public Vector3 Position { get; }
        public Vector3? NavMeshProjectedPosition { get; }
        public VanguardTacticalAuthoringValidationState LocalValidationState { get; }
        public string LocalValidationNotes { get; }
    }
}
#endif
