#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using EFT;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;

// Responsibility: Implements spatial TacticalAnchor and PoseSlot authoring over the physically separate TC Base/User layers.
// Flow: The author previews/names a bounded User anchor, then demonstrates PoseSlots by standing inside it and looking in the intended observation direction.
// Authority boundary: Anchors and PoseSlots are authored topology only; this editor never acquires Operator movement/combat authority and never writes TC Base. Physical pose capture records author intent only.
// Invariant: PoseSlot identity is stable across enrichment; position/view/physical pose are recaptured in place, local NavMesh evidence may remain deferred, and cancelled drafts perform no TC mutation.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

internal static class VanguardTacticalCoordinationTacticalAnchorEditor
{
    private const float DefaultAnchorRadiusMeters = 5.0f;
    private const float MinimumAnchorRadiusMeters = 1.0f;
    private const float MaximumAnchorRadiusMeters = 40.0f;
    private const float PoseContainmentToleranceMeters = 0.35f;
    private const float VisualHeightOffset = 0.14f;
    private const int AnchorRingSegments = 40;
    private const int MaximumAnchorNameLength = 64;
    private const int MaximumWorldLabels = 48;
    private const string AnchorNamePromptControl = "VanguardTacticalCoordinationTacticalAnchorName";
    private const string StatusTag = "VANGUARD_TACTICAL_COORDINATION_AUTHORING_STATUS";

    private static readonly KeyboardShortcut DraftConfirmShortcut = new(KeyCode.Return);
    private static readonly KeyboardShortcut DraftConfirmKeypadShortcut = new(KeyCode.KeypadEnter);
    private static readonly KeyboardShortcut DraftCancelShortcut = new(KeyCode.Escape);
    private static readonly KeyboardShortcut RadiusIncreaseKeypadShortcut = new(KeyCode.KeypadPlus);
    private static readonly KeyboardShortcut RadiusIncreaseMainShortcut = new(KeyCode.Equals);
    private static readonly KeyboardShortcut RadiusIncreaseFallbackShortcut = new(KeyCode.UpArrow, KeyCode.LeftControl);
    private static readonly KeyboardShortcut RadiusDecreaseKeypadShortcut = new(KeyCode.KeypadMinus);
    private static readonly KeyboardShortcut RadiusDecreaseMainShortcut = new(KeyCode.Minus);
    private static readonly KeyboardShortcut RadiusDecreaseFallbackShortcut = new(KeyCode.DownArrow, KeyCode.LeftControl);

    private static PendingAnchorCreation? pendingAnchor;
    private static bool namePromptActive;
    private static string namePromptText = string.Empty;

    public static bool HasPendingAnchor => pendingAnchor is not null;
    public static bool HasNamePrompt => namePromptActive;
    public static int BaseAnchorCount => TryGetLayers(out var baseMap, out _) ? baseMap.TacticalAnchors.Count : 0;
    public static int UserAnchorCount => TryGetLayers(out _, out var userMap) ? userMap.TacticalAnchors.Count : 0;
    public static int UserPoseSlotCount => TryGetLayers(out _, out var userMap) ? userMap.TacticalAnchors.Sum(anchor => anchor.AuthoredPoseSlots?.Count ?? 0) : 0;

    public static void ResetDraft()
    {
        pendingAnchor = null;
        namePromptActive = false;
        namePromptText = string.Empty;
    }

    public static string BeginAnchorPreview(Player player)
    {
        if (!TryGetLayers(out _, out var userMap))
        {
            return "TacticalAnchor preview unavailable: TC layers are not loaded.";
        }
        if (pendingAnchor is not null)
        {
            return "TacticalAnchor preview is already active.";
        }

        var radius = userMap.TacticalAnchors.Count > 0
            ? Mathf.Clamp(userMap.TacticalAnchors[userMap.TacticalAnchors.Count - 1].Radius, MinimumAnchorRadiusMeters, MaximumAnchorRadiusMeters)
            : DefaultAnchorRadiusMeters;
        pendingAnchor = new PendingAnchorCreation(player.Position, radius);
        namePromptActive = false;
        namePromptText = string.Empty;
        return $"TacticalAnchor preview started. Center follows author; radius={radius:0}m. Use + / - in 1m steps, Enter to freeze/name, Esc to cancel.";
    }

    public static bool UpdatePendingAnchor(Player player, out string status, out bool warning)
    {
        status = string.Empty;
        warning = false;
        if (pendingAnchor is null || namePromptActive)
        {
            return false;
        }

        pendingAnchor.Position = player.Position;
        if (IsRadiusIncreasePressed())
        {
            pendingAnchor.Radius = Mathf.Clamp(pendingAnchor.Radius + 1.0f, MinimumAnchorRadiusMeters, MaximumAnchorRadiusMeters);
            status = $"TacticalAnchor preview radius={pendingAnchor.Radius:0}m.";
            return true;
        }
        if (IsRadiusDecreasePressed())
        {
            pendingAnchor.Radius = Mathf.Clamp(pendingAnchor.Radius - 1.0f, MinimumAnchorRadiusMeters, MaximumAnchorRadiusMeters);
            status = $"TacticalAnchor preview radius={pendingAnchor.Radius:0}m.";
            return true;
        }
        if (DraftCancelShortcut.IsDown())
        {
            ResetDraft();
            status = "TacticalAnchor preview cancelled; no TC data changed.";
            return true;
        }
        if (!DraftConfirmShortcut.IsDown() && !DraftConfirmKeypadShortcut.IsDown())
        {
            return false;
        }

        namePromptActive = true;
        namePromptText = GetNextAnchorName();
        status = "TacticalAnchor geometry frozen. Enter a name and press Enter to create; Escape resumes movable preview.";
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
                return CommitNamedPendingAnchor(out status, out warning);
            }
            if (guiEvent.keyCode == KeyCode.Escape)
            {
                guiEvent.Use();
                GUIUtility.keyboardControl = 0;
                namePromptActive = false;
                namePromptText = string.Empty;
                status = "TacticalAnchor naming cancelled; movable preview resumed with no TC mutation.";
                return true;
            }
        }

        GUILayout.BeginArea(new Rect(190f, 275f, 560f, 168f), GUI.skin.window);
        GUILayout.Label("NAME NEW TACTICAL ANCHOR");
        GUILayout.Label("The anchor is a bounded tactical area. PoseSlots are captured separately by standing and looking in-world.");
        GUI.SetNextControlName(AnchorNamePromptControl);
        namePromptText = GUILayout.TextField(namePromptText ?? string.Empty, MaximumAnchorNameLength);
        GUI.FocusControl(AnchorNamePromptControl);
        GUILayout.Label("Enter creates TC User only; Escape resumes preview. No runtime movement authority is acquired.");
        GUILayout.EndArea();
        return false;
    }

    public static string AddPoseSlot(Player player, out bool warning)
    {
        warning = false;
        if (!TryGetLayers(out _, out var userMap))
        {
            warning = true;
            return "PoseSlot capture unavailable: TC User layer is not loaded.";
        }

        var anchor = FindContainingUserAnchor(userMap.TacticalAnchors, player.Position, requireEnabled: true, out var centerDistance);
        if (anchor is null)
        {
            warning = true;
            return "PoseSlot capture requires standing inside an enabled User TacticalAnchor.";
        }

        var view = NormalizeViewDirection(player.LookDirection);
        if (view.sqrMagnitude < 0.5f)
        {
            warning = true;
            return "PoseSlot capture refused: current gaze cannot produce a stable view direction.";
        }

        EvaluatePoseSlot(player.Position, out var projected, out var state, out var notes);
        var physicalPose = VanguardTacticalAuthoringPhysicalPoseCapture.Capture(player);
        var now = UtcNowText();
        var slot = new VanguardTacticalAuthoringPoseSlot
        {
            PoseSlotId = NewId("pose"),
            Position = VanguardVector3Dto.FromVector3(player.Position),
            NavMeshProjectedPosition = projected.HasValue ? VanguardVector3Dto.FromVector3(projected.Value) : null,
            ViewDirection = VanguardVector3Dto.FromVector3(view),
            PhysicalPoseSnapshot = physicalPose,
            Enabled = true,
            ValidationState = state,
            ValidationNotes = notes + ";physical_pose_snapshot=captured_v" + physicalPose.CaptureContractVersion,
            CreatedAt = now,
            CreatedWithBuild = VanguardBuildVersion.BuildLabel,
            LastValidatedAt = now,
            LastValidatedBuild = VanguardBuildVersion.BuildLabel
        };
        anchor.AuthoredPoseSlots ??= new List<VanguardTacticalAuthoringPoseSlot>();
        anchor.AuthoredPoseSlots.Add(slot);
        RefreshAnchorValidation(anchor);
        VanguardTacticalCoordinationPortalEditor.MarkUserLayerDirty();
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"event=TC_USER_POSE_SLOT_CREATED; anchor={anchor.AnchorId}; poseSlot={slot.PoseSlotId}; centerDistance={centerDistance:0.00}; localState={slot.ValidationState}; physicalPoseSnapshot=true; pose={VanguardTacticalAuthoringPhysicalPoseCapture.Describe(physicalPose)}; persisted=false; baseWrite=false; movementAuthority=false");
        warning = state != VanguardTacticalAuthoringValidationState.Valid;
        return $"PoseSlot {slot.PoseSlotId} captured in '{DisplayName(anchor)}' at {centerDistance:0.0}m from center; state={GetValidationLabel(state)}; pose={VanguardTacticalAuthoringPhysicalPoseCapture.Describe(physicalPose)}. Ctrl+S persists TC User.";
    }

    public static string RecaptureNearestPoseSlot(Player player, out bool warning)
    {
        warning = false;
        if (!TryFindNearestUserPoseSlot(player.Position, out var anchor, out var slot, out var distance))
        {
            warning = true;
            return $"No User PoseSlot within {VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters:0.0}m edit radius.";
        }
        if (!Contains(anchor!, player.Position))
        {
            warning = true;
            return "PoseSlot recapture refused: stand inside its owning TacticalAnchor.";
        }

        var view = NormalizeViewDirection(player.LookDirection);
        if (view.sqrMagnitude < 0.5f)
        {
            warning = true;
            return "PoseSlot recapture refused: current gaze cannot produce a stable view direction.";
        }

        EvaluatePoseSlot(player.Position, out var projected, out var state, out var notes);
        var physicalPose = VanguardTacticalAuthoringPhysicalPoseCapture.Capture(player);
        slot!.Position = VanguardVector3Dto.FromVector3(player.Position);
        slot.NavMeshProjectedPosition = projected.HasValue ? VanguardVector3Dto.FromVector3(projected.Value) : null;
        slot.ViewDirection = VanguardVector3Dto.FromVector3(view);
        slot.PhysicalPoseSnapshot = physicalPose;
        slot.ValidationState = state;
        slot.ValidationNotes = notes + ";physical_pose_snapshot=recaptured_v" + physicalPose.CaptureContractVersion;
        slot.LastValidatedAt = UtcNowText();
        slot.LastValidatedBuild = VanguardBuildVersion.BuildLabel;
        RefreshAnchorValidation(anchor!);
        VanguardTacticalCoordinationPortalEditor.MarkUserLayerDirty();
        warning = state != VanguardTacticalAuthoringValidationState.Valid;
        return $"Recaptured PoseSlot {slot.PoseSlotId} from {distance:0.0}m away without changing its id; position/view/pose updated to {VanguardTacticalAuthoringPhysicalPoseCapture.Describe(physicalPose)}. Ctrl+S persists TC User.";
    }

    public static string ToggleNearestPoseSlotEnabled(Player player, out bool warning)
    {
        warning = false;
        if (!TryFindNearestUserPoseSlot(player.Position, out var anchor, out var slot, out var distance))
        {
            warning = true;
            return $"No User PoseSlot within {VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters:0.0}m edit radius.";
        }

        slot!.Enabled = !slot.Enabled;
        RefreshAnchorValidation(anchor!);
        VanguardTacticalCoordinationPortalEditor.MarkUserLayerDirty();
        return $"PoseSlot {slot.PoseSlotId} {(slot.Enabled ? "enabled" : "disabled")} at {distance:0.0}m. Ctrl+S persists TC User.";
    }

    public static string DeleteNearestPoseSlot(Player player, out bool warning)
    {
        warning = false;
        if (!TryFindNearestUserPoseSlot(player.Position, out var anchor, out var slot, out var distance))
        {
            warning = true;
            return $"No User PoseSlot within {VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters:0.0}m edit radius.";
        }

        anchor!.AuthoredPoseSlots.Remove(slot!);
        RefreshAnchorValidation(anchor);
        VanguardTacticalCoordinationPortalEditor.MarkUserLayerDirty();
        return $"Deleted User PoseSlot {slot!.PoseSlotId} at {distance:0.0}m in memory. Ctrl+S persists TC User.";
    }

    public static string ToggleNearestUserAnchorEnabled(Player player, out bool warning)
    {
        warning = false;
        if (!TryGetLayers(out _, out var userMap))
        {
            warning = true;
            return "TacticalAnchor edit unavailable: TC User layer is not loaded.";
        }
        var anchor = FindNearestAnchor(userMap.TacticalAnchors, player.Position, VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters, out var distance);
        if (anchor is null)
        {
            warning = true;
            return $"No User TacticalAnchor center within {VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters:0.0}m edit radius.";
        }

        anchor.Enabled = !anchor.Enabled;
        VanguardTacticalCoordinationPortalEditor.MarkUserLayerDirty();
        return $"User TacticalAnchor '{DisplayName(anchor)}' {(anchor.Enabled ? "enabled" : "disabled")} at {distance:0.0}m. Ctrl+S persists TC User.";
    }

    public static string DeleteNearestUserAnchor(Player player, out bool warning)
    {
        warning = false;
        if (!TryGetLayers(out _, out var userMap))
        {
            warning = true;
            return "TacticalAnchor delete unavailable: TC User layer is not loaded.";
        }
        var anchor = FindNearestAnchor(userMap.TacticalAnchors, player.Position, VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters, out var distance);
        if (anchor is null)
        {
            warning = true;
            return $"No User TacticalAnchor center within {VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters:0.0}m edit radius.";
        }

        var poseCount = anchor.AuthoredPoseSlots?.Count ?? 0;
        userMap.TacticalAnchors.Remove(anchor);
        VanguardTacticalCoordinationPortalEditor.MarkUserLayerDirty();
        return $"Deleted User TacticalAnchor '{DisplayName(anchor)}' and its {poseCount} PoseSlot(s) at {distance:0.0}m in memory. Ctrl+S persists TC User.";
    }

    public static string RevalidateNearestPoseSlot(Player player, out bool warning)
    {
        warning = false;
        if (!TryFindNearestUserPoseSlot(player.Position, out var anchor, out var slot, out var distance))
        {
            warning = true;
            return $"No User PoseSlot within {VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters:0.0}m to revalidate.";
        }

        EvaluatePoseSlot(slot!.Position.ToVector3(), out var projected, out var state, out var notes);
        slot.NavMeshProjectedPosition = projected.HasValue ? VanguardVector3Dto.FromVector3(projected.Value) : null;
        slot.ValidationState = state;
        slot.ValidationNotes = notes + ";physical_pose_snapshot=" + VanguardTacticalAuthoringPhysicalPoseCapture.Describe(slot.PhysicalPoseSnapshot);
        slot.LastValidatedAt = UtcNowText();
        slot.LastValidatedBuild = VanguardBuildVersion.BuildLabel;
        RefreshAnchorValidation(anchor!);
        VanguardTacticalCoordinationPortalEditor.MarkUserLayerDirty();
        warning = state != VanguardTacticalAuthoringValidationState.Valid;
        return $"PoseSlot {slot.PoseSlotId} revalidated at {distance:0.0}m; state={GetValidationLabel(state)}. Position/view/pose were preserved ({VanguardTacticalAuthoringPhysicalPoseCapture.Describe(slot.PhysicalPoseSnapshot)}). Ctrl+S persists TC User.";
    }

    public static string GetNearestAnchorSummary(Vector3 playerPosition)
    {
        if (!TryGetLayers(out var baseMap, out var userMap))
        {
            return "Nearest TacticalAnchor: TC layers unavailable";
        }

        VanguardTacticalCoordinationResolvedAnchor? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var resolved in VanguardTacticalCoordinationAuthoringStore.ResolveTacticalAnchors(baseMap, userMap))
        {
            var distance = HorizontalDistance(playerPosition, resolved.TacticalAnchor.Center.ToVector3());
            if (distance < nearestDistance)
            {
                nearest = resolved;
                nearestDistance = distance;
            }
        }
        if (nearest is null)
        {
            return "Nearest TacticalAnchor: none";
        }

        var anchor = nearest.TacticalAnchor;
        string poseSummary = GetNearestPoseSlotSummary(anchor, playerPosition);
        return $"Nearest TacticalAnchor: {DisplayName(anchor)}  |  {nearest.Origin}  |  center {nearestDistance:0.0}m  |  radius {anchor.Radius:0.0}m  |  PoseSlots {anchor.AuthoredPoseSlots.Count}  |  {GetAnchorStateLabel(anchor)}  |  {poseSummary}";
    }

    private static string GetNearestPoseSlotSummary(VanguardTacticalAuthoringAnchor anchor, Vector3 playerPosition)
    {
        anchor.AuthoredPoseSlots ??= new List<VanguardTacticalAuthoringPoseSlot>();
        VanguardTacticalAuthoringPoseSlot? nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (var slot in anchor.AuthoredPoseSlots)
        {
            float distance = HorizontalDistance(playerPosition, slot.Position.ToVector3());
            if (distance < nearestDistance)
            {
                nearest = slot;
                nearestDistance = distance;
            }
        }
        return nearest == null
            ? "Nearest PoseSlot none"
            : $"Nearest PoseSlot {nearest.PoseSlotId} {nearestDistance:0.0}m pose={VanguardTacticalAuthoringPhysicalPoseCapture.Describe(nearest.PhysicalPoseSnapshot)}";
    }

    public static string GetPendingAnchorSummary()
    {
        return pendingAnchor is null
            ? "No TacticalAnchor preview"
            : $"NEW TACTICAL ANCHOR  |  radius {pendingAnchor.Radius:0}m  |  center follows author  |  + / - adjusts 1m";
    }

    public static void DrawWorldLines(Player player)
    {
        _ = player;
        if (!TryGetLayers(out var baseMap, out var userMap))
        {
            return;
        }

        foreach (var resolved in VanguardTacticalCoordinationAuthoringStore.ResolveTacticalAnchors(baseMap, userMap))
        {
            DrawAnchor(resolved.TacticalAnchor, resolved.ReadOnly);
        }
        if (pendingAnchor is not null)
        {
            DrawRing(pendingAnchor.Position + Vector3.up * VisualHeightOffset, pendingAnchor.Radius, new Color(0.9f, 0.35f, 1f, 0.95f));
            DrawCross(pendingAnchor.Position + Vector3.up * VisualHeightOffset, 0.45f);
        }
    }

    public static void DrawWorldLabels(Camera camera)
    {
        if (!TryGetLayers(out var baseMap, out var userMap))
        {
            return;
        }

        var labels = 0;
        foreach (var resolved in VanguardTacticalCoordinationAuthoringStore.ResolveTacticalAnchors(baseMap, userMap))
        {
            if (labels >= MaximumWorldLabels)
            {
                break;
            }
            var anchor = resolved.TacticalAnchor;
            var world = anchor.Center.ToVector3() + Vector3.up * 0.34f;
            if (!TryGetScreen(camera, world, out var screen))
            {
                continue;
            }
            GUI.Label(
                new Rect(screen.x + 7f, Screen.height - screen.y - 12f, 430f, 22f),
                $"ANCHOR  |  {(resolved.ReadOnly ? "BASE" : "USER")}  |  {GetAnchorStateLabel(anchor)}  |  {DisplayName(anchor)}  |  PoseSlots {anchor.AuthoredPoseSlots.Count}",
                GUI.skin.box);
            labels++;
        }

        if (pendingAnchor is not null && TryGetScreen(camera, pendingAnchor.Position + Vector3.up * 0.38f, out var pendingScreen))
        {
            GUI.Label(
                new Rect(pendingScreen.x + 7f, Screen.height - pendingScreen.y - 12f, 320f, 22f),
                $"NEW TACTICAL ANCHOR  |  radius {pendingAnchor.Radius:0}m",
                GUI.skin.box);
        }
    }

    private static bool CommitNamedPendingAnchor(out string status, out bool warning)
    {
        status = string.Empty;
        warning = false;
        if (pendingAnchor is null || !TryGetLayers(out _, out var userMap))
        {
            ResetDraft();
            status = "TacticalAnchor naming commit failed closed: staged preview or TC User layer is missing.";
            warning = true;
            return true;
        }

        var draft = pendingAnchor;
        var anchor = new VanguardTacticalAuthoringAnchor
        {
            AnchorId = NewId("anchor"),
            Name = NormalizeName(namePromptText, GetNextAnchorName()),
            Center = VanguardVector3Dto.FromVector3(draft.Position),
            Radius = draft.Radius,
            AuthoredPoseSlots = new List<VanguardTacticalAuthoringPoseSlot>(),
            Enabled = true,
            ValidationState = VanguardTacticalAuthoringValidationState.NotValidated,
            ValidationNotes = "anchor_created;pose_slots_pending;physical_pose_contract=v2_capture_on_pose_slot",
            CreatedAt = UtcNowText(),
            CreatedWithBuild = VanguardBuildVersion.BuildLabel
        };
        userMap.TacticalAnchors.Add(anchor);
        VanguardTacticalCoordinationPortalEditor.MarkUserLayerDirty();
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"event=TC_USER_TACTICAL_ANCHOR_CREATED; anchor={anchor.AnchorId}; map={userMap.MapId}; displayName={anchor.Name}; radius={anchor.Radius:0.00}; poseSlots=0; persisted=false; baseWrite=false; movementAuthority=false");
        ResetDraft();
        status = $"User TacticalAnchor '{anchor.Name}' created in memory with radius {anchor.Radius:0}m. Stand inside it and Ctrl+Insert to capture PoseSlots; Ctrl+S persists TC User.";
        return true;
    }

    private static void EvaluatePoseSlot(Vector3 authoredPosition, out Vector3? projected, out VanguardTacticalAuthoringValidationState state, out string notes)
    {
        var reasons = new List<string>();
        if (!VanguardTacticalAuthoringNavMeshProjection.TryProjectLocal(authoredPosition, out var navPoint))
        {
            projected = null;
            state = VanguardTacticalAuthoringValidationState.Warning;
            notes = "local_projection_deferred_to_authoritative_runtime;authoritative_runtime_revalidation_required";
            return;
        }

        projected = navPoint;
        reasons.Add("local_projection_ok");
        if (!VanguardTacticalAuthoringNavMeshProjection.HasStaticCapsuleClearance(navPoint))
        {
            state = VanguardTacticalAuthoringValidationState.Warning;
            reasons.Add("local_static_capsule_warning_deferred_to_authoritative_runtime");
        }
        else
        {
            state = VanguardTacticalAuthoringValidationState.Valid;
            reasons.Add("local_static_capsule_ok");
        }
        reasons.Add("authoritative_runtime_revalidation_required");
        notes = string.Join(";", reasons);
    }

    private static void RefreshAnchorValidation(VanguardTacticalAuthoringAnchor anchor)
    {
        anchor.AuthoredPoseSlots ??= new List<VanguardTacticalAuthoringPoseSlot>();
        var enabled = anchor.AuthoredPoseSlots.Where(slot => slot.Enabled).ToList();
        if (enabled.Count == 0)
        {
            anchor.ValidationState = VanguardTacticalAuthoringValidationState.NotValidated;
            anchor.ValidationNotes = "no_enabled_pose_slots;physical_pose_contract_v2_supported";
            return;
        }
        if (enabled.Any(slot => slot.ValidationState == VanguardTacticalAuthoringValidationState.Invalid))
        {
            anchor.ValidationState = VanguardTacticalAuthoringValidationState.Invalid;
        }
        else if (enabled.Any(slot => slot.ValidationState != VanguardTacticalAuthoringValidationState.Valid))
        {
            anchor.ValidationState = VanguardTacticalAuthoringValidationState.Warning;
        }
        else
        {
            anchor.ValidationState = VanguardTacticalAuthoringValidationState.Valid;
        }
        int poseV2Count = enabled.Count(slot => VanguardTacticalAuthoringPhysicalPoseCapture.IsRuntimeUsable(slot.PhysicalPoseSnapshot));
        anchor.ValidationNotes = $"enabled_pose_slots={enabled.Count};physical_pose_v2={poseV2Count}/{enabled.Count};legacy_or_unset_pose_slots={enabled.Count - poseV2Count}";
    }

    private static bool TryFindNearestUserPoseSlot(Vector3 position, out VanguardTacticalAuthoringAnchor? anchor, out VanguardTacticalAuthoringPoseSlot? slot, out float distance)
    {
        anchor = null;
        slot = null;
        distance = float.MaxValue;
        if (!TryGetLayers(out _, out var userMap))
        {
            return false;
        }

        foreach (var candidateAnchor in userMap.TacticalAnchors)
        {
            candidateAnchor.AuthoredPoseSlots ??= new List<VanguardTacticalAuthoringPoseSlot>();
            foreach (var candidateSlot in candidateAnchor.AuthoredPoseSlots)
            {
                var candidateDistance = HorizontalDistance(position, (candidateSlot.NavMeshProjectedPosition ?? candidateSlot.Position).ToVector3());
                if (candidateDistance < distance && candidateDistance <= VanguardTacticalAuthoringOptions.NearbyEditRadiusMeters)
                {
                    anchor = candidateAnchor;
                    slot = candidateSlot;
                    distance = candidateDistance;
                }
            }
        }
        return slot is not null;
    }

    private static VanguardTacticalAuthoringAnchor? FindContainingUserAnchor(IReadOnlyList<VanguardTacticalAuthoringAnchor> anchors, Vector3 position, bool requireEnabled, out float distance)
    {
        VanguardTacticalAuthoringAnchor? best = null;
        var bestRadius = float.MaxValue;
        distance = float.MaxValue;
        foreach (var anchor in anchors)
        {
            if (requireEnabled && !anchor.Enabled)
            {
                continue;
            }
            var d = HorizontalDistance(position, anchor.Center.ToVector3());
            if (d > anchor.Radius + PoseContainmentToleranceMeters)
            {
                continue;
            }
            if (anchor.Radius < bestRadius || (Math.Abs(anchor.Radius - bestRadius) < 0.01f && d < distance))
            {
                best = anchor;
                bestRadius = anchor.Radius;
                distance = d;
            }
        }
        return best;
    }

    private static VanguardTacticalAuthoringAnchor? FindNearestAnchor(IReadOnlyList<VanguardTacticalAuthoringAnchor> anchors, Vector3 position, float maximumDistance, out float distance)
    {
        VanguardTacticalAuthoringAnchor? nearest = null;
        distance = float.MaxValue;
        foreach (var anchor in anchors)
        {
            var d = HorizontalDistance(position, anchor.Center.ToVector3());
            if (d < distance && d <= maximumDistance)
            {
                nearest = anchor;
                distance = d;
            }
        }
        return nearest;
    }

    private static bool Contains(VanguardTacticalAuthoringAnchor anchor, Vector3 position)
    {
        return HorizontalDistance(position, anchor.Center.ToVector3()) <= anchor.Radius + PoseContainmentToleranceMeters;
    }

    private static void DrawAnchor(VanguardTacticalAuthoringAnchor anchor, bool readOnly)
    {
        var center = anchor.Center.ToVector3() + Vector3.up * VisualHeightOffset;
        var ringColor = !anchor.Enabled
            ? new Color(0.95f, 0.2f, 0.2f, 0.75f)
            : readOnly
                ? new Color(0.65f, 0.45f, 0.9f, 0.65f)
                : new Color(0.85f, 0.35f, 1f, 0.9f);
        DrawRing(center, anchor.Radius, ringColor);
        GL.Color(ringColor);
        DrawCross(center, readOnly ? 0.28f : 0.36f);

        anchor.AuthoredPoseSlots ??= new List<VanguardTacticalAuthoringPoseSlot>();
        foreach (var slot in anchor.AuthoredPoseSlots)
        {
            DrawPoseSlot(slot, readOnly);
        }
    }

    private static void DrawPoseSlot(VanguardTacticalAuthoringPoseSlot slot, bool readOnly)
    {
        var position = (slot.NavMeshProjectedPosition ?? slot.Position).ToVector3() + Vector3.up * (VisualHeightOffset + 0.03f);
        var color = GetPoseSlotColor(slot, readOnly);
        GL.Color(color);
        DrawCross(position, readOnly ? 0.22f : 0.28f);
        var direction = FlattenAndNormalize(slot.ViewDirection.ToVector3());
        if (direction.sqrMagnitude > 0.1f)
        {
            var end = position + direction * 1.8f;
            GL.Vertex(position);
            GL.Vertex(end);
            var side = Vector3.Cross(Vector3.up, direction).normalized;
            GL.Vertex(end);
            GL.Vertex(end - direction * 0.38f + side * 0.20f);
            GL.Vertex(end);
            GL.Vertex(end - direction * 0.38f - side * 0.20f);
        }
    }

    private static Color GetPoseSlotColor(VanguardTacticalAuthoringPoseSlot slot, bool readOnly)
    {
        var alpha = readOnly ? 0.65f : 0.95f;
        if (!slot.Enabled || slot.ValidationState == VanguardTacticalAuthoringValidationState.Invalid)
        {
            return new Color(0.95f, 0.2f, 0.2f, alpha);
        }
        if (slot.ValidationState == VanguardTacticalAuthoringValidationState.Valid)
        {
            return new Color(0.25f, 0.9f, 0.9f, alpha);
        }
        return new Color(1f, 0.58f, 0.12f, alpha);
    }

    private static void DrawRing(Vector3 center, float radius, Color color)
    {
        GL.Color(color);
        for (var segment = 0; segment < AnchorRingSegments; segment++)
        {
            var angleA = (float)(Math.PI * 2.0 * segment / AnchorRingSegments);
            var angleB = (float)(Math.PI * 2.0 * (segment + 1) / AnchorRingSegments);
            GL.Vertex(center + new Vector3(Mathf.Cos(angleA) * radius, 0f, Mathf.Sin(angleA) * radius));
            GL.Vertex(center + new Vector3(Mathf.Cos(angleB) * radius, 0f, Mathf.Sin(angleB) * radius));
        }
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

    private static bool TryGetLayers(out VanguardTacticalCoordinationAuthoringMapFile baseMap, out VanguardTacticalCoordinationAuthoringMapFile userMap)
    {
        return VanguardTacticalCoordinationPortalEditor.TryGetCoordinationLayers(out baseMap, out userMap);
    }

    private static string GetNextAnchorName()
    {
        return "Anchor " + (BaseAnchorCount + UserAnchorCount + 1).ToString();
    }

    private static string NormalizeName(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized.Length <= MaximumAnchorNameLength ? normalized : normalized.Substring(0, MaximumAnchorNameLength);
    }

    private static string DisplayName(VanguardTacticalAuthoringAnchor anchor)
    {
        return string.IsNullOrWhiteSpace(anchor.Name) ? anchor.AnchorId : anchor.Name;
    }

    private static string GetAnchorStateLabel(VanguardTacticalAuthoringAnchor anchor)
    {
        if (!anchor.Enabled)
        {
            return "DISABLED";
        }
        return GetValidationLabel(anchor.ValidationState);
    }

    private static string GetValidationLabel(VanguardTacticalAuthoringValidationState state)
    {
        return state switch
        {
            VanguardTacticalAuthoringValidationState.Valid => "LOCAL OK",
            VanguardTacticalAuthoringValidationState.Invalid => "INVALID",
            VanguardTacticalAuthoringValidationState.Warning => "WAIT AUTH",
            _ => "UNVALIDATED"
        };
    }

    private static Vector3 NormalizeViewDirection(Vector3 direction)
    {
        return direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.zero;
    }

    private static Vector3 FlattenAndNormalize(Vector3 direction)
    {
        direction.y = 0f;
        return direction.sqrMagnitude <= 0.0001f ? Vector3.zero : direction.normalized;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        return new Vector2(a.x - b.x, a.z - b.z).magnitude;
    }

    private static bool IsRadiusIncreasePressed()
    {
        return RadiusIncreaseKeypadShortcut.IsDown() || RadiusIncreaseMainShortcut.IsDown() || RadiusIncreaseFallbackShortcut.IsDown();
    }

    private static bool IsRadiusDecreasePressed()
    {
        return RadiusDecreaseKeypadShortcut.IsDown() || RadiusDecreaseMainShortcut.IsDown() || RadiusDecreaseFallbackShortcut.IsDown();
    }

    private static string NewId(string prefix)
    {
        return prefix + "-" + Guid.NewGuid().ToString("N")[..12];
    }

    private static string UtcNowText()
    {
        return DateTimeOffset.UtcNow.ToString("O");
    }

    private sealed class PendingAnchorCreation
    {
        public PendingAnchorCreation(Vector3 position, float radius)
        {
            Position = position;
            Radius = radius;
        }

        public Vector3 Position;
        public float Radius;
    }
}
#endif
