using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using HarmonyLib;
#endif

// Responsibility: reports the Harmony topology that shares Vanguard's critical Off-Raid UI / Operator Equipment patch targets.
// Flow: the first real Vanguard Off-Raid menu initialization triggers one read-only scan of the exact methods Vanguard patched and records every co-located patch owner/order constraint.
// Authority boundary: diagnostics only; this service never creates, removes, reorders, suppresses or otherwise mutates another mod's Harmony registrations.
// Invariant: a shared target is evidence of interop surface overlap, not proof of conflict; logs must preserve that distinction explicitly.
namespace Vanguard.Client.UI.OffRaid;

internal static class VanguardOffRaidHarmonyInteropAuditService
{
    public const string StatusTag = "VANGUARD_OFFRAID_INTEROP_TOPOLOGY";

#if SPT_CLIENT
    private static readonly HashSet<string> CriticalVanguardPatchTypes = new(StringComparer.Ordinal)
    {
        "Vanguard.Client.UI.OffRaid.VanguardOffRaidMenuPatch",
        "Vanguard.Client.UI.OffRaid.Inventory.VanguardOperatorInventoryGetProfilesPatch",
        "Vanguard.Client.UI.OffRaid.Inventory.VanguardOperatorInventoryProfileRebindPatch",
        "Vanguard.Client.UI.OffRaid.Inventory.VanguardOperatorContextualRagfairNavigationPatch",
        "Vanguard.Client.UI.OffRaid.Inventory.VanguardOperatorWeaponModdingNavigationPatch",
        "Vanguard.Client.UI.OffRaid.Inventory.VanguardOperatorEditBuildControllerPatch",
        "Vanguard.Client.UI.OffRaid.Inventory.VanguardOperatorEquipmentBuildsControllerPatch",
        "Vanguard.Client.UI.OffRaid.Inventory.VanguardOperatorInventoryScreenReturnPatch",
        "Vanguard.Client.UI.OffRaid.Inventory.VanguardOperatorInventoryMenuGuardPatch"
    };

    private static bool _captured;

    public static void CaptureOnce()
    {
        if (_captured)
        {
            return;
        }

        // MenuScreen is reached after startup patch registration, so this boundary observes
        // the effective Off-Raid topology without arbitrary delays or frame-loop polling.
        _captured = true;
        try
        {
            CaptureTopology();
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"offraid_harmony_topology_scan_failed type={exception.GetType().Name}; reason={Safe(exception.Message)}; readOnly=true; mutation=false; conflictInference=false; failOpen=true");
        }
    }

    private static void CaptureTopology()
    {
        var targets = Harmony.GetAllPatchedMethods()
            .Select(target => new TargetTopology(target, Harmony.GetPatchInfo(target)))
            .Where(topology => topology.Patches != null && EnumeratePatches(topology.Patches).Any(IsCriticalVanguardPatch))
            .OrderBy(topology => topology.Target.DeclaringType?.FullName ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(topology => topology.Target.Name, StringComparer.Ordinal)
            .ToArray();

        int sharedTargets = 0;
        int externalPatchCount = 0;

        foreach (TargetTopology topology in targets)
        {
            Patch[] entries = EnumeratePatches(topology.Patches!).ToArray();
            int externalOnTarget = entries.Count(patch => !IsVanguardPatch(patch));
            externalPatchCount += externalOnTarget;
            if (externalOnTarget > 0)
            {
                sharedTargets++;
            }

            string classification = externalOnTarget > 0 ? "shared_external_observed" : "vanguard_only";
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"offraid_harmony_target target={FormatTarget(topology.Target)}; classification={classification}; externalPatchCount={externalOnTarget.ToString(CultureInfo.InvariantCulture)}; prefixes={FormatPatches(topology.Patches!.Prefixes)}; postfixes={FormatPatches(topology.Patches.Postfixes)}; transpilers={FormatPatches(topology.Patches.Transpilers)}; finalizers={FormatPatches(topology.Patches.Finalizers)}; readOnly=true; mutation=false; conflictInference=false");
        }

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"offraid_harmony_summary criticalTargets={targets.Length.ToString(CultureInfo.InvariantCulture)}; sharedTargets={sharedTargets.ToString(CultureInfo.InvariantCulture)}; externalPatchCount={externalPatchCount.ToString(CultureInfo.InvariantCulture)}; sharedTargetMeansConflict=false; readOnly=true; mutation=false");
    }

    private static IEnumerable<Patch> EnumeratePatches(Patches patches)
    {
        foreach (Patch patch in patches.Prefixes)
        {
            yield return patch;
        }

        foreach (Patch patch in patches.Postfixes)
        {
            yield return patch;
        }

        foreach (Patch patch in patches.Transpilers)
        {
            yield return patch;
        }

        foreach (Patch patch in patches.Finalizers)
        {
            yield return patch;
        }
    }

    private static bool IsCriticalVanguardPatch(Patch patch)
    {
        string? declaringType = patch.PatchMethod?.DeclaringType?.FullName;
        return declaringType != null && CriticalVanguardPatchTypes.Contains(declaringType);
    }

    private static bool IsVanguardPatch(Patch patch)
    {
        string? declaringType = patch.PatchMethod?.DeclaringType?.FullName;
        return declaringType != null && declaringType.StartsWith("Vanguard.Client.", StringComparison.Ordinal);
    }

    private static string FormatPatches(IEnumerable<Patch> patches)
    {
        string[] values = patches.Select(FormatPatch).ToArray();
        return values.Length == 0 ? "none" : string.Join(",", values);
    }

    private static string FormatPatch(Patch patch)
    {
        string before = patch.before == null || patch.before.Length == 0
            ? "none"
            : string.Join("+", patch.before.Select(Safe));
        string after = patch.after == null || patch.after.Length == 0
            ? "none"
            : string.Join("+", patch.after.Select(Safe));
        return $"{Safe(patch.owner)}@{patch.priority.ToString(CultureInfo.InvariantCulture)}:{Safe(patch.PatchMethod?.DeclaringType?.FullName)}.{Safe(patch.PatchMethod?.Name)}[before={before}|after={after}]";
    }

    private static string FormatTarget(MethodBase target)
    {
        string parameters = string.Join(",", target.GetParameters().Select(parameter => Safe(parameter.ParameterType.FullName ?? parameter.ParameterType.Name)));
        return $"{Safe(target.DeclaringType?.FullName)}.{Safe(target.Name)}({parameters})";
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace(',', '_').Replace('|', '_').Replace('[', '_').Replace(']', '_').Replace('\r', '_').Replace('\n', '_');

    private sealed record TargetTopology(MethodBase Target, Patches? Patches);
#else
    public static void CaptureOnce()
    {
    }
#endif
}
