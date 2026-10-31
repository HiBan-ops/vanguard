#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using EFT;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;

// Responsibility: Implements spatial PassagePath authoring over the shared TC Base/User layers using durable authored control points plus regenerable resolved navigation data.
// Flow: The author starts a User path, captures ordered control points while moving through the world, previews local resolution, names the frozen draft and commits only to TC User.
// Authority boundary: Local resolution is authoring evidence only; this editor never acquires Operator movement ownership, never writes TC Base, and never treats a local NavMesh miss as universal runtime truth.
// Invariant: Authored control points are the durable player intent, derived resolution may be regenerated or cleared without losing that intent, and cancelled drafts perform no persistence mutation.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal static class VanguardTacticalCoordinationPassagePathEditor
{
    private const int MaximumPassageNameLength = 64;
    private const int MaximumControlPoints = 32;
    private const int MaximumWorldLabels = 48;
    private const float MinimumControlPointSpacingMeters = 0.75f;
    private const float VisualHeightOffset = 0.12f;
    private const string PassageNamePromptControl = "VanguardTacticalCoordinationPassagePathName";
    private const string StatusTag = "VANGUARD_TACTICAL_COORDINATION_AUTHORING_STATUS";

    private static readonly KeyboardShortcut DraftConfirmShortcut = new(KeyCode.Return);
    private static readonly KeyboardShortcut DraftConfirmKeypadShortcut = new(KeyCode.KeypadEnter);
    private static readonly KeyboardShortcut DraftCancelShortcut = new(KeyCode.Escape);
    private static readonly KeyboardShortcut DraftUndoShortcut = new(KeyCode.Backspace);

    private static PendingPassagePathCreation? pendingPath;
    private static bool namePromptActive;
    private static string namePromptText = string.Empty;

    public static bool HasPendingPath => pendingPath is not null;
    public static bool HasNamePrompt => namePromptActive;

    public static void ResetDraft()
    {
        pendingPath = null;
        namePromptActive = false;
        namePromptText = string.Empty;
    }

    public static string BeginPathDraft(Player player)
    {
        if (!VanguardTacticalCoordinationPortalEditor.TryGetCoordinationLayers(out _, out _))
        {
            return "PassagePath draft unavailable: TC layers are not loaded.";
        }
        if (pendingPath is not null)
        {
            return "PassagePath draft is already active.";
        }

        namePromptActive = false;
        namePromptText = string.Empty;
        pendingPath = new PendingPassagePathCreation();
        pendingPath.ControlPoints.Add(player.Position);
        RecalculatePendingResolution();
        return "PassagePath draft started with control point 1. Move and press Enter to add points; Ctrl+Insert freezes/names; Backspace removes the last point; Esc cancels.";
    }

    public static bool UpdatePendingPath(Player player, out string status, out bool warning)
    {
        status = string.Empty;
        warning = false;
        if (pendingPath is null || namePromptActive)
        {
            return false;
        }

        if (DraftCancelShortcut.IsDown())
        {
            ResetDraft();
            status = "PassagePath draft cancelled; no TC data changed.";
            return true;
        }

        if (DraftUndoShortcut.IsDown())
        {
            if (pendingPath.ControlPoints.Count <= 1)
            {
                status = "PassagePath keeps its first control point; Esc cancels the draft.";
                warning = true;
                return true;
            }

            pendingPath.ControlPoints.RemoveAt(pendingPath.ControlPoints.Count - 1);
            RecalculatePendingResolution();
            status = $"Removed last PassagePath control point; {pendingPath.ControlPoints.Count} authored point(s) remain.";
            return true;
        }

        if (VanguardTacticalAuthoringOptions.AddSlotShortcut.IsDown())
        {
            if (pendingPath.ControlPoints.Count < MaximumControlPoints)
            {
                TryAppendCurrentPosition(player.Position, allowNearDuplicate: false, out _);
            }
            if (pendingPath.ControlPoints.Count < 2)
            {
                status = "PassagePath requires at least two distinct control points before naming.";
                warning = true;
                return true;
            }

            namePromptActive = true;
            namePromptText = GetNextPassageName();
            status = pendingPath.Resolution.HasCompletePolyline
                ? "PassagePath geometry frozen with a complete local resolved polyline. Enter a name and press Enter to create; Escape resumes point capture."
                : "PassagePath geometry frozen with local navigation resolution deferred. Authored control points are preserved; Enter a name and press Enter to create, or Escape to resume capture.";
            warning = false;
            return true;
        }

        if (!DraftConfirmShortcut.IsDown() && !DraftConfirmKeypadShortcut.IsDown())
        {
            return false;
        }

        if (pendingPath.ControlPoints.Count >= MaximumControlPoints)
        {
            status = $"PassagePath already contains the maximum {MaximumControlPoints} control points; Ctrl+Insert freezes/names it.";
            warning = true;
            return true;
        }

        if (!TryAppendCurrentPosition(player.Position, allowNearDuplicate: false, out var distance))
        {
            status = $"Control point not added: move at least {MinimumControlPointSpacingMeters:0.00}m from the previous point (current {distance:0.00}m).";
            warning = true;
            return true;
        }

        status = $"PassagePath control point {pendingPath.ControlPoints.Count} captured. {GetResolutionSummary(pendingPath.Resolution)}";
        warning = pendingPath.Resolution.State != VanguardTacticalAuthoringValidationState.Valid;
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
                return CommitNamedPendingPath(out status, out warning);
            }

            if (guiEvent.keyCode == KeyCode.Escape)
            {
                guiEvent.Use();
                GUIUtility.keyboardControl = 0;
                namePromptActive = false;
                namePromptText = string.Empty;
                status = "PassagePath naming cancelled; control-point draft resumed with no TC mutation.";
                return true;
            }
        }

        GUILayout.BeginArea(new Rect(190f, 275f, 560f, 168f), GUI.skin.window);
        GUILayout.Label("NAME NEW PASSAGE PATH");
        GUILayout.Label("Author-facing name. Authored control points remain durable even if local resolution is deferred.");
        GUI.SetNextControlName(PassageNamePromptControl);
        namePromptText = GUILayout.TextField(namePromptText ?? string.Empty, MaximumPassageNameLength);
        GUI.FocusControl(PassageNamePromptControl);
        GUILayout.Label("Enter creates the TC User PassagePath; Escape resumes control-point capture. TC Base remains read-only.");
        GUILayout.EndArea();
        return false;
    }

    public static string ToggleNearestUserPathEnabled(Player player, out bool warning)
    {
        warning = false;
        if (!TryGetUserMap(out var userMap))
        {
            warning = true;
            return "PassagePath edit unavailable: TC User layer is not loaded.";
        }

        var path = FindNearestPath(userMap.PassagePaths, player.Position, VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters, out var distance);
        if (path is null)
        {
            warning = true;
            return $"No User PassagePath within {VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters:0.0}m edit radius.";
        }

        path.Enabled = !path.Enabled;
        VanguardTacticalCoordinationPortalEditor.MarkUserLayerDirty();
        return $"User PassagePath {DisplayName(path)} {(path.Enabled ? "enabled" : "disabled")} at {distance:0.0}m. Ctrl+S persists TC User.";
    }

    public static string DeleteNearestUserPath(Player player, out bool warning)
    {
        warning = false;
        if (!TryGetUserMap(out var userMap))
        {
            warning = true;
            return "PassagePath delete unavailable: TC User layer is not loaded.";
        }

        var path = FindNearestPath(userMap.PassagePaths, player.Position, VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters, out var distance);
        if (path is null)
        {
            warning = true;
            return $"No User PassagePath within {VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters:0.0}m edit radius.";
        }

        userMap.PassagePaths.Remove(path);
        VanguardTacticalCoordinationPortalEditor.MarkUserLayerDirty();
        return $"Deleted User PassagePath {DisplayName(path)} at {distance:0.0}m in memory. Ctrl+S persists TC User.";
    }

    public static string RevalidateNearestUserPath(Player player, out bool warning)
    {
        warning = false;
        if (!TryGetUserMap(out var userMap))
        {
            warning = true;
            return "PassagePath revalidation unavailable: TC User layer is not loaded.";
        }

        var path = FindNearestPath(userMap.PassagePaths, player.Position, 8f, out var distance);
        if (path is null)
        {
            warning = true;
            return "No User PassagePath within 8m to revalidate.";
        }

        ApplyResolution(path, ResolvePath(path));
        VanguardTacticalCoordinationPortalEditor.MarkUserLayerDirty();
        warning = path.ValidationState != VanguardTacticalAuthoringValidationState.Valid;
        return path.ValidationState == VanguardTacticalAuthoringValidationState.Valid
            ? $"User PassagePath {DisplayName(path)} locally re-resolved at {distance:0.0}m; authored control points were preserved. Ctrl+S persists TC User."
            : $"User PassagePath {DisplayName(path)} local resolution remains deferred at {distance:0.0}m; authored control points were preserved. Ctrl+S persists TC User.";
    }

    public static string GetNearestPathSummary(Vector3 playerPosition)
    {
        if (!VanguardTacticalCoordinationPortalEditor.TryGetCoordinationLayers(out var baseMap, out var userMap))
        {
            return "Nearest PassagePath: TC layers unavailable";
        }

        var resolved = VanguardTacticalCoordinationAuthoringStore.ResolvePassagePaths(baseMap, userMap);
        VanguardTacticalCoordinationResolvedPassagePath? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var item in resolved)
        {
            var distance = DistanceToPath(playerPosition, item.PassagePath);
            if (distance < nearestDistance)
            {
                nearest = item;
                nearestDistance = distance;
            }
        }

        if (nearest is null)
        {
            return "Nearest PassagePath: none";
        }

        return $"Nearest PassagePath: {DisplayName(nearest.PassagePath)}  |  {nearest.Origin}  |  {nearestDistance:0.0}m  |  {GetPathStateLabel(nearest.PassagePath)}";
    }

    public static string GetPendingPathSummary()
    {
        if (pendingPath is null)
        {
            return string.Empty;
        }

        return $"NEW PASSAGE PATH  |  {pendingPath.ControlPoints.Count} control point(s)  |  {GetResolutionSummary(pendingPath.Resolution)}";
    }

    public static void DrawWorldLines(Player player)
    {
        if (VanguardTacticalCoordinationPortalEditor.TryGetCoordinationLayers(out var baseMap, out var userMap))
        {
            foreach (var path in baseMap.PassagePaths)
            {
                DrawPersistedPath(path, readOnly: true);
            }
            foreach (var path in userMap.PassagePaths)
            {
                DrawPersistedPath(path, readOnly: false);
            }
        }

        if (pendingPath is null)
        {
            return;
        }

        var previewPoints = pendingPath.Resolution.HasCompletePolyline
            ? pendingPath.Resolution.ResolvedPolyline
            : pendingPath.ControlPoints;
        DrawPolyline(previewPoints, GetResolutionColor(pendingPath.Resolution.State, enabled: true), VisualHeightOffset);
        DrawControlPointMarkers(pendingPath.ControlPoints, new Color(0.95f, 0.9f, 0.25f, 1f), 0.22f);

        if (!namePromptActive && pendingPath.ControlPoints.Count > 0)
        {
            var last = pendingPath.ControlPoints[pendingPath.ControlPoints.Count - 1] + Vector3.up * VisualHeightOffset;
            var current = player.Position + Vector3.up * VisualHeightOffset;
            GL.Color(new Color(0.95f, 0.9f, 0.25f, 0.65f));
            GL.Vertex(last);
            GL.Vertex(current);
        }
    }

    public static void DrawWorldLabels(Camera camera)
    {
        var labels = 0;
        if (VanguardTacticalCoordinationPortalEditor.TryGetCoordinationLayers(out var baseMap, out var userMap))
        {
            foreach (var item in VanguardTacticalCoordinationAuthoringStore.ResolvePassagePaths(baseMap, userMap))
            {
                if (labels >= MaximumWorldLabels || item.PassagePath.ControlPoints.Count == 0)
                {
                    break;
                }

                var world = item.PassagePath.ControlPoints[0].Position.ToVector3() + Vector3.up * 0.30f;
                if (!TryGetScreen(camera, world, out var screen))
                {
                    continue;
                }

                GUI.Label(
                    new Rect(screen.x + 7f, Screen.height - screen.y - 12f, 310f, 22f),
                    $"PASSAGE  |  {(item.ReadOnly ? "BASE" : "USER")}  |  {GetPathStateLabel(item.PassagePath)}  |  {DisplayName(item.PassagePath)}",
                    GUI.skin.box);
                labels++;
            }
        }

        if (pendingPath is not null && pendingPath.ControlPoints.Count > 0)
        {
            var world = pendingPath.ControlPoints[0] + Vector3.up * 0.35f;
            if (TryGetScreen(camera, world, out var screen))
            {
                GUI.Label(
                    new Rect(screen.x + 7f, Screen.height - screen.y - 12f, 300f, 22f),
                    $"NEW PASSAGE  |  CP {pendingPath.ControlPoints.Count}  |  {GetResolutionSummary(pendingPath.Resolution)}",
                    GUI.skin.box);
            }
        }
    }

    private static bool CommitNamedPendingPath(out string status, out bool warning)
    {
        status = string.Empty;
        warning = false;
        if (pendingPath is null || !TryGetUserMap(out var userMap))
        {
            ResetDraft();
            status = "PassagePath naming commit failed closed: staged draft or TC User layer is missing.";
            warning = true;
            return true;
        }
        if (pendingPath.ControlPoints.Count < 2)
        {
            namePromptActive = false;
            namePromptText = string.Empty;
            status = "PassagePath commit refused: at least two authored control points are required.";
            warning = true;
            return true;
        }

        var path = new VanguardTacticalAuthoringPassagePath
        {
            PassagePathId = NewId("passage"),
            DisplayName = NormalizeName(namePromptText, GetNextPassageName()),
            Enabled = true,
            CreatedAt = UtcNowText(),
            CreatedWithBuild = VanguardBuildVersion.BuildLabel
        };

        for (var index = 0; index < pendingPath.ControlPoints.Count; index++)
        {
            var authored = pendingPath.ControlPoints[index];
            var projected = index < pendingPath.Resolution.ProjectedControlPoints.Count
                ? pendingPath.Resolution.ProjectedControlPoints[index]
                : null;
            path.ControlPoints.Add(new VanguardTacticalAuthoringPassageControlPoint
            {
                ControlPointId = NewId("control"),
                Position = VanguardVector3Dto.FromVector3(authored),
                NavMeshProjectedPosition = projected.HasValue ? VanguardVector3Dto.FromVector3(projected.Value) : null
            });
        }

        ApplyResolution(path, pendingPath.Resolution, initialRevision: true);
        userMap.PassagePaths.Add(path);
        VanguardTacticalCoordinationPortalEditor.MarkUserLayerDirty();
        var localState = path.ValidationState;
        ResetDraft();

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"event=TC_USER_PASSAGE_PATH_CREATED; passage={path.PassagePathId}; map={userMap.MapId}; displayName={path.DisplayName}; controlPoints={path.ControlPoints.Count}; resolvedPoints={path.ResolvedPolyline.Count}; localState={localState}; persisted=false; baseWrite=false; movementAuthority=false");

        status = localState == VanguardTacticalAuthoringValidationState.Valid
            ? $"User PassagePath '{path.DisplayName}' created in memory with complete local resolution. Authored intent remains independent of derived navigation. Ctrl+S persists TC User."
            : $"User PassagePath '{path.DisplayName}' created in memory with local resolution deferred; authored control points were preserved. Ctrl+S persists TC User.";
        warning = false;
        return true;
    }

    private static bool TryAppendCurrentPosition(Vector3 position, bool allowNearDuplicate, out float distance)
    {
        distance = float.MaxValue;
        if (pendingPath is null)
        {
            return false;
        }
        if (pendingPath.ControlPoints.Count == 0)
        {
            pendingPath.ControlPoints.Add(position);
            RecalculatePendingResolution();
            distance = 0f;
            return true;
        }

        var last = pendingPath.ControlPoints[pendingPath.ControlPoints.Count - 1];
        distance = Vector3.Distance(last, position);
        if (!allowNearDuplicate && distance < MinimumControlPointSpacingMeters)
        {
            return false;
        }

        pendingPath.ControlPoints.Add(position);
        RecalculatePendingResolution();
        return true;
    }

    private static void RecalculatePendingResolution()
    {
        if (pendingPath is null)
        {
            return;
        }
        pendingPath.Resolution = VanguardTacticalCoordinationPassagePathResolver.Resolve(pendingPath.ControlPoints);
    }

    private static VanguardTacticalCoordinationPassageResolution ResolvePath(VanguardTacticalAuthoringPassagePath path)
    {
        var authored = path.ControlPoints.Select(point => point.Position.ToVector3()).ToList();
        return VanguardTacticalCoordinationPassagePathResolver.Resolve(authored);
    }

    private static void ApplyResolution(
        VanguardTacticalAuthoringPassagePath path,
        VanguardTacticalCoordinationPassageResolution resolution,
        bool initialRevision = false)
    {
        for (var index = 0; index < path.ControlPoints.Count; index++)
        {
            var projected = index < resolution.ProjectedControlPoints.Count
                ? resolution.ProjectedControlPoints[index]
                : null;
            path.ControlPoints[index].NavMeshProjectedPosition = projected.HasValue
                ? VanguardVector3Dto.FromVector3(projected.Value)
                : null;
        }

        path.ResolvedPolyline = resolution.ResolvedPolyline.Select(VanguardVector3Dto.FromVector3).ToList();
        path.ResolutionRevision = initialRevision
            ? 1
            : (path.ResolutionRevision == int.MaxValue ? 1 : Math.Max(1, path.ResolutionRevision + 1));
        path.ResolutionSource = resolution.Source;
        path.ResolvedAt = UtcNowText();
        path.ResolvedWithBuild = VanguardBuildVersion.BuildLabel;
        path.ValidationState = resolution.State;
        path.ValidationNotes = resolution.Notes;
    }

    private static bool TryGetUserMap(out VanguardTacticalCoordinationAuthoringMapFile userMap)
    {
        if (VanguardTacticalCoordinationPortalEditor.TryGetCoordinationLayers(out _, out var userLayer))
        {
            userMap = userLayer;
            return true;
        }

        userMap = new VanguardTacticalCoordinationAuthoringMapFile();
        return false;
    }

    private static VanguardTacticalAuthoringPassagePath? FindNearestPath(
        IEnumerable<VanguardTacticalAuthoringPassagePath> paths,
        Vector3 playerPosition,
        float maxDistance,
        out float distance)
    {
        distance = float.MaxValue;
        VanguardTacticalAuthoringPassagePath? nearest = null;
        foreach (var path in paths)
        {
            var current = DistanceToPath(playerPosition, path);
            if (current <= maxDistance && current < distance)
            {
                distance = current;
                nearest = path;
            }
        }
        return nearest;
    }

    private static float DistanceToPath(Vector3 playerPosition, VanguardTacticalAuthoringPassagePath path)
    {
        var points = path.ControlPoints.Count > 0
            ? path.ControlPoints.Select(point => point.Position.ToVector3())
            : path.ResolvedPolyline.Select(point => point.ToVector3());
        var distance = float.MaxValue;
        foreach (var point in points)
        {
            var current = new Vector2(playerPosition.x - point.x, playerPosition.z - point.z).magnitude;
            if (current < distance)
            {
                distance = current;
            }
        }
        return distance;
    }

    private static void DrawPersistedPath(VanguardTacticalAuthoringPassagePath path, bool readOnly)
    {
        var points = path.ResolvedPolyline.Count >= 2
            ? path.ResolvedPolyline.Select(point => point.ToVector3()).ToList()
            : path.ControlPoints.Select(point => point.Position.ToVector3()).ToList();
        var color = GetResolutionColor(path.ValidationState, path.Enabled);
        if (readOnly)
        {
            color.a *= 0.70f;
        }
        DrawPolyline(points, color, VisualHeightOffset);
        DrawControlPointMarkers(
            path.ControlPoints.Select(point => point.Position.ToVector3()).ToList(),
            readOnly ? new Color(0.45f, 0.75f, 0.9f, 0.70f) : new Color(0.35f, 0.9f, 0.95f, 0.95f),
            readOnly ? 0.13f : 0.17f);
    }

    private static void DrawPolyline(IReadOnlyList<Vector3> points, Color color, float heightOffset)
    {
        if (points.Count < 2)
        {
            return;
        }
        GL.Color(color);
        for (var index = 0; index < points.Count - 1; index++)
        {
            GL.Vertex(points[index] + Vector3.up * heightOffset);
            GL.Vertex(points[index + 1] + Vector3.up * heightOffset);
        }
    }

    private static void DrawControlPointMarkers(IReadOnlyList<Vector3> points, Color color, float halfSize)
    {
        GL.Color(color);
        for (var index = 0; index < points.Count; index++)
        {
            var center = points[index] + Vector3.up * VisualHeightOffset;
            GL.Vertex(center + Vector3.left * halfSize);
            GL.Vertex(center + Vector3.right * halfSize);
            GL.Vertex(center + Vector3.back * halfSize);
            GL.Vertex(center + Vector3.forward * halfSize);
        }
    }

    private static Color GetResolutionColor(VanguardTacticalAuthoringValidationState state, bool enabled)
    {
        if (!enabled || state == VanguardTacticalAuthoringValidationState.Invalid)
        {
            return new Color(0.95f, 0.2f, 0.2f, 0.95f);
        }
        if (state == VanguardTacticalAuthoringValidationState.Valid)
        {
            return new Color(0.25f, 0.9f, 0.9f, 0.95f);
        }
        return new Color(1f, 0.58f, 0.12f, 0.95f);
    }

    private static string GetResolutionSummary(VanguardTacticalCoordinationPassageResolution resolution)
    {
        return resolution.State == VanguardTacticalAuthoringValidationState.Valid && resolution.HasCompletePolyline
            ? $"LOCAL RESOLVED ({resolution.ResolvedPolyline.Count} pts)"
            : "LOCAL DEFERRED / AUTHORITATIVE REVALIDATION REQUIRED";
    }

    private static string GetPathStateLabel(VanguardTacticalAuthoringPassagePath path)
    {
        if (!path.Enabled)
        {
            return "DISABLED";
        }
        return path.ValidationState switch
        {
            VanguardTacticalAuthoringValidationState.Valid => "LOCAL RESOLVED",
            VanguardTacticalAuthoringValidationState.Invalid => "INVALID",
            VanguardTacticalAuthoringValidationState.Warning => "DEFERRED",
            _ => "NOT VALIDATED"
        };
    }

    private static string GetNextPassageName()
    {
        return "Passage " + (VanguardTacticalCoordinationPortalEditor.BasePassagePathCount + VanguardTacticalCoordinationPortalEditor.UserPassagePathCount + 1).ToString();
    }

    private static string NormalizeName(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized.Length <= MaximumPassageNameLength
            ? normalized
            : normalized.Substring(0, MaximumPassageNameLength);
    }

    private static string DisplayName(VanguardTacticalAuthoringPassagePath path)
    {
        return string.IsNullOrWhiteSpace(path.DisplayName) ? path.PassagePathId : path.DisplayName;
    }

    private static string NewId(string prefix)
    {
        return prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 12);
    }

    private static string UtcNowText()
    {
        return DateTimeOffset.UtcNow.ToString("O");
    }

    private static bool TryGetScreen(Camera camera, Vector3 world, out Vector3 screen)
    {
        screen = camera.WorldToScreenPoint(world);
        return screen.z > 0f;
    }

    private sealed class PendingPassagePathCreation
    {
        public List<Vector3> ControlPoints { get; } = new();
        public VanguardTacticalCoordinationPassageResolution Resolution { get; set; } = new();
    }
}
#endif
