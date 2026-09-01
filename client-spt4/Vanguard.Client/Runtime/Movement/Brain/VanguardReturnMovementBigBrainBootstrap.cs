#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Integrations.MoreBots;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Provides Return Movement Big Brain Bootstrap support for the movement/cohesion runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Movement.Brain;

internal static class VanguardReturnMovementBigBrainBootstrap
{
    private static bool registered;

    public static void Register()
    {
        if (registered)
        {
            return;
        }

        var brains = new List<string>
        {
            "PMC",
            "PmcUsec",
            "PmcBear",
            "ExUsec",
            "ArenaFighter",
            "assault",
            "bossKilla",
            "bossKnight",
            "followerBigPipe",
            "followerBirdEye",
            "followerBully",
            "followerGluharAssault",
            "followerGluharScout",
            "followerGluharSecurity",
            "followerGluharSnipe"
        };

        VanguardOperatorBigBrainAuthorityBoundary.EnsureInstalled();
        var operatorRoles = new List<EFT.WildSpawnType>(VanguardOperatorBigBrainAuthorityBoundary.OperatorRoles);
        BrainManager.AddCustomLayer(typeof(VanguardReturnMovementLayer), brains, VanguardReturnMovementLayer.LayerPriority, operatorRoles);
        registered = true;
        VanguardClientDiagnosticsLog.Diagnostic(VanguardReturnMovementCommandStore.StatusTag,
            () => $"VANGUARD_MOVE_BRIDGE_LAYER_REGISTERED layer={nameof(VanguardReturnMovementLayer)}; logic={nameof(VanguardReturnMovementLogic)}; priority={VanguardReturnMovementLayer.LayerPriority}; brains={string.Join(",", brains)}; roles={VanguardOperatorBotTypes.UsecRoleName},{VanguardOperatorBotTypes.BearRoleName}; operatorRolesOnly=true; backend=terminal_GoToSomePoint_or_continuous_direct_GoToPoint_slowAtEnd_false; continuousTag={VanguardContinuousCohesionLocomotionPolicy.StatusTag}; tag={VanguardReturnMovementCommandStore.StatusTag}; goToSomePointTag={VanguardReturnMovementCommandStore.GoToSomePointStatusTag}");
    }

    public static void RegisterSafe()
    {
        try
        {
            Register();
        }
        catch (Exception ex)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardReturnMovementCommandStore.StatusTag,
                $"VANGUARD_MOVE_BRIDGE_LAYER_REGISTER_FAILED reason={ex.GetType().Name}; message={Safe(ex.Message)}; tag={VanguardReturnMovementCommandStore.StatusTag}; goToSomePointTag={VanguardReturnMovementCommandStore.GoToSomePointStatusTag}");
        }
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }
}

internal static class VanguardOperatorBigBrainAuthorityBoundary
{
    public const string StatusTag = "VANGUARD_OPERATOR_BIGBRAIN_AUTHORITY_BOUNDARY_STATUS";
    private const string SainPluginGuid = "me.sol.sain";

    private static readonly object Sync = new();
    private static readonly HashSet<string> LoggedDeniedLayerTypes = new(StringComparer.Ordinal);
    private static readonly WildSpawnType UsecRole = (WildSpawnType)VanguardOperatorBotTypes.UsecRoleValue;
    private static readonly WildSpawnType BearRole = (WildSpawnType)VanguardOperatorBotTypes.BearRoleValue;

    private static bool initialized;
    private static bool enforcementEnabled;
    private static Assembly? sainAssembly;
    private static FieldInfo? rolesField;

    public static IReadOnlyList<WildSpawnType> OperatorRoles { get; } = new[] { UsecRole, BearRole };

    public static void EnsureInstalled()
    {
        lock (Sync)
        {
            if (initialized)
            {
                return;
            }

            initialized = true;
            if (!TryResolveAuthorityAssemblies(out string authorityReason))
            {
                enforcementEnabled = false;
                VanguardClientDiagnosticsLog.Warning(
                    StatusTag,
                    $"VANGUARD_OPERATOR_BIGBRAIN_AUTHORITY_BOUNDARY_DISABLED reason={Safe(authorityReason)}; failOpen=true; operatorsOnly=true; nativeLayersChanged=false; prioritiesChanged=false; perTickPatch=false; tag={StatusTag}");
                return;
            }

            try
            {
                enforcementEnabled = true;
                new VanguardOperatorBigBrainAddCustomLayerPatch().Enable();

                int scanned = 0;
                int filteredRegistrations = 0;
                int removedRoleReferences = 0;
                foreach (BrainManager.LayerInfo layerInfo in BrainManager.CustomLayersReadOnly.Values)
                {
                    scanned++;
                    if (TryFilterExistingRegistration(layerInfo, out int removed))
                    {
                        filteredRegistrations++;
                        removedRoleReferences += removed;
                    }
                }

                VanguardClientDiagnosticsLog.Operational(
                    StatusTag,
                    () => $"VANGUARD_OPERATOR_BIGBRAIN_AUTHORITY_BOUNDARY_ENABLED operators={VanguardOperatorBotTypes.UsecRoleName},{VanguardOperatorBotTypes.BearRoleName}; allowCustomAssemblies=Vanguard.Client{(sainAssembly != null ? ",SAIN" : string.Empty)}; denyPolicy=other_custom_layers; existingScanned={scanned}; existingFiltered={filteredRegistrations}; operatorRoleReferencesRemoved={removedRoleReferences}; futureRegistrationGuard=true; nativeLayersChanged=false; prioritiesChanged=false; perTickPatch=false; sainInstalled={Bool(IsSainPluginRegistered())}; sainAssemblyResolved={Bool(sainAssembly != null)}; tag={StatusTag}");
            }
            catch (Exception exception)
            {
                // A compatibility failure in the hardening layer must not take down Vanguard's own movement
                // bridge. Disable enforcement and continue with the role-scoped Vanguard registration.
                enforcementEnabled = false;
                VanguardClientDiagnosticsLog.Warning(
                    StatusTag,
                    $"VANGUARD_OPERATOR_BIGBRAIN_AUTHORITY_BOUNDARY_DISABLED reason=installation_failed:{exception.GetType().Name}:{Safe(exception.Message)}; failOpen=true; vanguardMovementRegistrationContinues=true; operatorsOnly=true; nativeLayersChanged=false; prioritiesChanged=false; perTickPatch=false; tag={StatusTag}");
            }
        }
    }

    public static void FilterRegistration(Type? customLayerType, ref List<WildSpawnType>? roles)
    {
        if (!enforcementEnabled || customLayerType == null || roles == null || roles.Count == 0)
        {
            return;
        }

        if (IsAllowedCustomLayer(customLayerType) || !ContainsOperatorRole(roles))
        {
            return;
        }

        List<WildSpawnType> filtered = roles.Where(static role => !VanguardOperatorBotTypes.IsVanguardOperatorRole(role)).ToList();
        int removed = roles.Count - filtered.Count;
        if (removed <= 0)
        {
            return;
        }

        // Never mutate BigBrain's shared AllWildSpawnTypes list in-place. Replacing the argument with a
        // private filtered copy keeps every non-Operator role and every other mod registration unchanged.
        roles = filtered;
        LogDeniedRegistration(customLayerType, removed, "registration_guard");
    }

    private static bool TryFilterExistingRegistration(BrainManager.LayerInfo layerInfo, out int removed)
    {
        removed = 0;
        Type? customLayerType = layerInfo.customLayerType;
        if (customLayerType == null || IsAllowedCustomLayer(customLayerType) || !ContainsOperatorRole(layerInfo.CustomLayerRoles))
        {
            return false;
        }

        try
        {
            FieldInfo? field = rolesField ??= ResolveRolesField(layerInfo.GetType());
            if (field?.GetValue(layerInfo) is not List<WildSpawnType> currentRoles)
            {
                VanguardClientDiagnosticsLog.Warning(
                    StatusTag,
                    $"VANGUARD_OPERATOR_BIGBRAIN_AUTHORITY_EXISTING_FILTER_FAILED layer={Safe(customLayerType.FullName)}; assembly={Safe(customLayerType.Assembly.GetName().Name)}; reason=roles_field_unavailable; failOpenForLayer=true; tag={StatusTag}");
                return false;
            }

            List<WildSpawnType> filtered = currentRoles.Where(static role => !VanguardOperatorBotTypes.IsVanguardOperatorRole(role)).ToList();
            removed = currentRoles.Count - filtered.Count;
            if (removed <= 0)
            {
                return false;
            }

            field.SetValue(layerInfo, filtered);
            LogDeniedRegistration(customLayerType, removed, "existing_registry_sweep");
            return true;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"VANGUARD_OPERATOR_BIGBRAIN_AUTHORITY_EXISTING_FILTER_FAILED layer={Safe(customLayerType.FullName)}; assembly={Safe(customLayerType.Assembly.GetName().Name)}; reason={exception.GetType().Name}:{Safe(exception.Message)}; failOpenForLayer=true; tag={StatusTag}");
            removed = 0;
            return false;
        }
    }

    private static bool TryResolveAuthorityAssemblies(out string reason)
    {
        sainAssembly = null;
        try
        {
            if (!Chainloader.PluginInfos.TryGetValue(SainPluginGuid, out var pluginInfo))
            {
                reason = "sain_not_installed:vanguard_only_custom_authority";
                return true;
            }

            sainAssembly = pluginInfo.Instance?.GetType().Assembly;
            if (sainAssembly == null)
            {
                reason = "sain_plugin_instance_or_assembly_missing";
                return false;
            }

            reason = "sain_assembly_resolved:" + Safe(sainAssembly.GetName().Name);
            return true;
        }
        catch (Exception exception)
        {
            // The boundary must never guess that SAIN is absent when BepInEx state itself is unreadable.
            // Global fail-open is safer than accidentally stripping SAIN combat authority from Operators.
            reason = "sain_plugin_state_unreadable:" + exception.GetType().Name;
            return false;
        }
    }

    private static bool IsAllowedCustomLayer(Type customLayerType)
    {
        Assembly assembly = customLayerType.Assembly;
        if (assembly == typeof(VanguardOperatorBigBrainAuthorityBoundary).Assembly)
        {
            return true;
        }

        return sainAssembly != null && ReferenceEquals(assembly, sainAssembly);
    }

    private static bool IsSainPluginRegistered()
    {
        try
        {
            return Chainloader.PluginInfos.ContainsKey(SainPluginGuid);
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsOperatorRole(IEnumerable<WildSpawnType> roles)
    {
        foreach (WildSpawnType role in roles)
        {
            if (VanguardOperatorBotTypes.IsVanguardOperatorRole(role))
            {
                return true;
            }
        }

        return false;
    }

    private static FieldInfo? ResolveRolesField(Type layerInfoType)
    {
        for (Type? current = layerInfoType; current != null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField("roles", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null && typeof(List<WildSpawnType>).IsAssignableFrom(field.FieldType))
            {
                return field;
            }
        }

        return null;
    }

    private static void LogDeniedRegistration(Type customLayerType, int removedRoles, string source)
    {
        string key = customLayerType.AssemblyQualifiedName ?? customLayerType.FullName ?? customLayerType.Name;
        if (!LoggedDeniedLayerTypes.Add(key))
        {
            return;
        }

        VanguardClientDiagnosticsLog.Diagnostic(
            StatusTag,
            () => $"VANGUARD_OPERATOR_BIGBRAIN_CUSTOM_LAYER_DENIED layer={Safe(customLayerType.FullName)}; assembly={Safe(customLayerType.Assembly.GetName().Name)}; source={Safe(source)}; removedOperatorRoles={removedRoles}; operatorsOnly=true; otherRolesPreserved=true; nativeLayersChanged=false; priorityChanged=false; tag={StatusTag}");
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }
}

internal sealed class VanguardOperatorBigBrainAddCustomLayerPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        MethodInfo? target = typeof(BrainManager)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(static method =>
            {
                if (!string.Equals(method.Name, nameof(BrainManager.AddCustomLayer), StringComparison.Ordinal))
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 4
                    && parameters[0].ParameterType == typeof(Type)
                    && parameters[1].ParameterType == typeof(List<string>)
                    && parameters[2].ParameterType == typeof(int)
                    && parameters[3].ParameterType == typeof(List<WildSpawnType>);
            });

        return target ?? throw new InvalidOperationException("BigBrain BrainManager.AddCustomLayer(Type,List<string>,int,List<WildSpawnType>) not found for Vanguard Operator authority boundary.");
    }

    [PatchPrefix]
    private static void PatchPrefix(Type __0, ref List<WildSpawnType> __3)
    {
        try
        {
            List<WildSpawnType>? roles = __3;
            VanguardOperatorBigBrainAuthorityBoundary.FilterRegistration(__0, ref roles);
            if (roles != null)
            {
                __3 = roles;
            }
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardOperatorBigBrainAuthorityBoundary.StatusTag,
                $"VANGUARD_OPERATOR_BIGBRAIN_REGISTRATION_GUARD_FAILED layer={Safe(__0?.FullName)}; reason={exception.GetType().Name}:{Safe(exception.Message)}; failOpenForRegistration=true; tag={VanguardOperatorBigBrainAuthorityBoundary.StatusTag}");
        }
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }
}
#endif
