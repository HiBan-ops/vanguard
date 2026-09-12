using System;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
#endif

// Responsibility: converts the renderer-only preview transport payload into EFT's native LastPlayerStateClass.
// Flow: a GClass2214-compatible JSON object is deserialized through the exact EFT descriptor type and then
// passed to the native LastPlayerStateClass descriptor constructor.
// Authority boundary: conversion is read-only and client-local; it never enters Operator Inventory Mode,
// repairs a profile, persists state or synthesizes equipment/customization outside the supplied payload.
// Invariant: malformed/unavailable visual truth fails closed and leaves persistent Operator truth untouched.
namespace Vanguard.Client.UI.SquadPreview;

internal sealed class VanguardSquadPreviewVisualAdapter
{
    public const string StatusTag = "SQUAD_PREVIEW_VISUAL_ADAPTER";

    public bool TryCreateNativeState(
        VanguardOperatorPreviewVisualDto? projection,
        out object? nativeState,
        out VanguardSquadPreviewVisualEvidence evidence,
        out string reason)
    {
        nativeState = null;
        evidence = VanguardSquadPreviewVisualEvidence.Empty;
        reason = "unknown";

#if SPT_CLIENT
        try
        {
            if (projection == null)
            {
                reason = "projection_missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(projection.OperatorId))
            {
                reason = "operator_id_missing";
                return false;
            }

            JObject? visualObject = projection.Visual switch
            {
                JObject obj => obj,
                JToken token when token.Type == JTokenType.Object => (JObject)token,
                _ => null
            };
            if (visualObject == null)
            {
                reason = "visual_payload_missing_or_not_object";
                return false;
            }

            Type? descriptorType = ResolveType("GClass2214");
            Type? stateType = ResolveType("LastPlayerStateClass");
            if (descriptorType == null || stateType == null)
            {
                reason = $"native_visual_types_missing_descriptor_{descriptorType != null}_state_{stateType != null}";
                return false;
            }

            object? descriptor = visualObject.ToObject(descriptorType);
            if (descriptor == null)
            {
                reason = "native_visual_descriptor_deserialization_null";
                return false;
            }

            ConstructorInfo? constructor = stateType
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(ctor =>
                {
                    ParameterInfo[] parameters = ctor.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(descriptorType);
                });
            if (constructor == null)
            {
                reason = "last_player_state_descriptor_constructor_missing";
                return false;
            }

            nativeState = constructor.Invoke(new[] { descriptor });
            object? info = ResolveMember(nativeState, "Info");
            object? customization = ResolveMember(nativeState, "Customization");
            object? equipment = ResolveMember(nativeState, "Equipment");
            if (info == null || customization == null || equipment == null)
            {
                nativeState = null;
                reason = $"native_visual_incomplete_info_{info != null}_customization_{customization != null}_equipment_{equipment != null}";
                return false;
            }

            evidence = BuildEvidence(customization, equipment);
            reason = "native_last_player_state_ready";
            VanguardSquadPreviewVisualEvidence diagnosticEvidence = evidence;
            string diagnosticReason = reason;
            VanguardClientDiagnosticsLog.Diagnostic(
                StatusTag,
                () => $"operator={projection.OperatorId}; displayName={projection.DisplayName ?? "<none>"}; visualRevision={projection.VisualRevision ?? "<none>"}; fingerprint={projection.VisualFingerprint ?? "<none>"}; customization={diagnosticEvidence.CustomizationPresent}; equipment={diagnosticEvidence.EquipmentPresent}; weaponSlots={diagnosticEvidence.PopulatedWeaponSlots}; reason={diagnosticReason}; mutation=false");
            return true;
        }
        catch (Exception exception)
        {
            Exception root = Unwrap(exception);
            nativeState = null;
            evidence = VanguardSquadPreviewVisualEvidence.Empty;
            reason = root.GetType().Name + ":" + root.Message;
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"native_visual_conversion_failed operator={projection?.OperatorId ?? "<none>"}; reason={reason}; mutation=false; persistentTruthUntouched=true");
            return false;
        }
#else
        reason = "spt_client_not_defined";
        return false;
#endif
    }

#if SPT_CLIENT
    private static VanguardSquadPreviewVisualEvidence BuildEvidence(object customization, object equipment)
    {
        int populatedWeaponSlots = 0;
        Type? equipmentSlotType = ResolveType("EFT.InventoryLogic.EquipmentSlot") ?? ResolveType("EquipmentSlot");
        MethodInfo? getSlot = equipmentSlotType == null
            ? null
            : equipment.GetType().GetMethod(
                "GetSlot",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new[] { equipmentSlotType },
                modifiers: null);

        if (equipmentSlotType != null && getSlot != null)
        {
            foreach (string slotName in new[] { "FirstPrimaryWeapon", "SecondPrimaryWeapon", "Holster" })
            {
                try
                {
                    object slotValue = Enum.Parse(equipmentSlotType, slotName, ignoreCase: false);
                    object? slot = getSlot.Invoke(equipment, new[] { slotValue });
                    if (ResolveMember(slot, "ContainedItem") != null)
                    {
                        populatedWeaponSlots++;
                    }
                }
                catch
                {
                    // Evidence is diagnostic only. Loader admission is based on the native state itself.
                }
            }
        }

        return new VanguardSquadPreviewVisualEvidence(
            customizationPresent: customization != null,
            equipmentPresent: equipment != null,
            populatedWeaponSlots: populatedWeaponSlots);
    }

    private static object? ResolveMember(object? instance, string name)
    {
        if (instance == null)
        {
            return null;
        }

        Type type = instance.GetType();
        return type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance)
            ?? type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);
    }

    private static Type? ResolveType(string fullOrSimpleName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? exact = assembly.GetType(fullOrSimpleName, throwOnError: false, ignoreCase: false);
            if (exact != null)
            {
                return exact;
            }

            try
            {
                Type? simple = assembly.GetTypes().FirstOrDefault(type => string.Equals(type.Name, fullOrSimpleName, StringComparison.Ordinal));
                if (simple != null)
                {
                    return simple;
                }
            }
            catch (ReflectionTypeLoadException)
            {
                // Optional/mod assemblies may not fully enumerate. Continue bounded discovery.
            }
        }

        return null;
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } target)
        {
            exception = target.InnerException;
        }

        return exception;
    }
#endif
}

internal readonly struct VanguardSquadPreviewVisualEvidence
{
    public static readonly VanguardSquadPreviewVisualEvidence Empty = new(false, false, 0);

    public VanguardSquadPreviewVisualEvidence(bool customizationPresent, bool equipmentPresent, int populatedWeaponSlots)
    {
        CustomizationPresent = customizationPresent;
        EquipmentPresent = equipmentPresent;
        PopulatedWeaponSlots = populatedWeaponSlots;
    }

    public bool CustomizationPresent { get; }
    public bool EquipmentPresent { get; }
    public int PopulatedWeaponSlots { get; }
}
