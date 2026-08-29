using System;
using System.Collections.Generic;
using System.Reflection;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;

#if SPT_CLIENT
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: Bridges EFT/SPT/Fika callbacks into Fika Operator Dogtag Guard Patch for the raid lifecycle patch bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.Raid.Patches;

#if SPT_CLIENT
/// <summary>
/// Vanguard Operators currently carry no PMC dogtag item by design. Fika's corpse sync remains
/// valid, but its private dogtag metadata helper logs an error for that intentional inventory.
/// Skip only that helper for registered Operators; all other Fika death/corpse serialization runs.
/// </summary>
internal sealed class VanguardFikaOperatorDogtagGuardPatch : ModulePatch
{
    private const string TargetTypeName = "Fika.Core.Main.Players.FikaPlayer";
    private const string TargetMethodName = "GenerateDogtagDetails";
    private static readonly HashSet<string> LoggedProfiles = new(StringComparer.OrdinalIgnoreCase);

    public static void ResetForRaidLifecycle(string reason)
    {
        LoggedProfiles.Clear();
    }

    protected override MethodBase GetTargetMethod()
    {
        Type targetType = AccessTools.TypeByName(TargetTypeName)
            ?? throw new InvalidOperationException(TargetTypeName + " not found for Vanguard Operator dogtag guard.");
        MethodInfo method = AccessTools.Method(targetType, TargetMethodName)
            ?? throw new InvalidOperationException(TargetTypeName + "." + TargetMethodName + " not found for Vanguard Operator dogtag guard.");
        VanguardClientDiagnosticsLog.Info(VanguardRuntimeConvergenceStatusTags.FikaDogtagGuard,
            $"VANGUARD_FIKA_DOGTAG_GUARD_BIND_OK type={targetType.FullName}; method={method.Name}; operatorsOnly=true; corpseSyncPreserved=true; tag={VanguardRuntimeConvergenceStatusTags.FikaDogtagGuard}");
        return method;
    }

    [PatchPrefix]
    private static bool PatchPrefix(object __instance)
    {
        try
        {
            string profileId = ResolveProfileId(__instance);
            if (string.IsNullOrWhiteSpace(profileId)
                || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(profileId, out var runtime))
            {
                return true;
            }

            if (!IsDogtagMetadataMissing(__instance, out var missingReason))
            {
                // Future-proofing: when an Operator legitimately carries a valid dogtag component,
                // let Fika populate it normally instead of permanently suppressing that feature.
                return true;
            }

            if (LoggedProfiles.Add(profileId))
            {
                VanguardClientDiagnosticsLog.Info(VanguardRuntimeConvergenceStatusTags.FikaDogtagGuard,
                    $"VANGUARD_FIKA_DOGTAG_DETAILS_SKIPPED operator={Safe(runtime.OperatorId)}; botProfile={Safe(profileId)}; reason={Safe(missingReason)}; corpseInventorySerialization=true; deathReplication=true; validFutureDogtagPassThrough=true; nonOperatorsUnaffected=true; tag={VanguardRuntimeConvergenceStatusTags.FikaDogtagGuard}");
            }

            return false;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardRuntimeConvergenceStatusTags.FikaDogtagGuard,
                $"VANGUARD_FIKA_DOGTAG_GUARD_EXCEPTION type={exception.GetType().Name}; message={Safe(exception.Message)}; failOpen=true; tag={VanguardRuntimeConvergenceStatusTags.FikaDogtagGuard}");
            return true;
        }
    }

    private static bool IsDogtagMetadataMissing(object instance, out string reason)
    {
        reason = "unknown_player_type";
        if (instance is not Player player)
        {
            return false;
        }

        object? item = player.Equipment?.GetSlot(EquipmentSlot.Dogtag)?.ContainedItem;
        if (item == null)
        {
            reason = "operator_inventory_has_no_dogtag_item_by_design";
            return true;
        }

        // The dogtag component contract lives in an optional client assembly that Vanguard does not
        // otherwise require at compile time. A strongly typed generic lookup would therefore add
        // a fragile client-only dependency.
        // Inspect both mutable and readonly component collections by reflection instead.
        // Failure to inspect is fail-open: Fika retains its native behavior.
        if (!TryInspectDogtagComponent(item, out bool componentPresent))
        {
            reason = "dogtag_component_inspection_unavailable_fail_open";
            return false;
        }

        if (!componentPresent)
        {
            reason = "operator_dogtag_item_has_no_dogtag_component";
            return true;
        }

        reason = "valid_dogtag_component_present";
        return false;
    }

    private static bool TryInspectDogtagComponent(object item, out bool componentPresent)
    {
        componentPresent = false;
        bool inspectedAnyCollection = false;

        try
        {
            object? mutableComponents = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(item, "Components");
            inspectedAnyCollection |= InspectComponentCollection(mutableComponents, ref componentPresent);
            if (componentPresent)
            {
                return true;
            }

            object? template = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(item, "Template");
            object? readonlyComponents = VanguardOperatorRuntimeAuditReflection.InvokeNoArg(template, "GetReadonlyComponents");
            inspectedAnyCollection |= InspectComponentCollection(readonlyComponents, ref componentPresent);
            return inspectedAnyCollection;
        }
        catch
        {
            componentPresent = false;
            return false;
        }
    }

    private static bool InspectComponentCollection(object? components, ref bool componentPresent)
    {
        if (components is not System.Collections.IEnumerable enumerable)
        {
            return false;
        }

        foreach (object? component in enumerable)
        {
            Type? componentType = component?.GetType();
            if (componentType == null)
            {
                continue;
            }

            if (string.Equals(componentType.FullName, "EFT.InventoryLogic.DogtagComponent", StringComparison.Ordinal)
                || string.Equals(componentType.Name, "DogtagComponent", StringComparison.Ordinal))
            {
                componentPresent = true;
                break;
            }
        }

        return true;
    }

    private static string ResolveProfileId(object instance)
    {
        string direct = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(instance, "ProfileId", "ProfileID")?.ToString()?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        object? profile = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(instance, "Profile");
        return VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(profile, "Id", "ProfileId", "ProfileID")?.ToString()?.Trim() ?? string.Empty;
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#else
internal sealed class VanguardFikaOperatorDogtagGuardPatch { public void Enable() { } }
#endif
