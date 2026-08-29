using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
#endif

// Responsibility: Bridges EFT/SPT/Fika callbacks into Operator Inventory Get Profiles Patch for the Off-Raid Operator inventory bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.UI.OffRaid.Inventory;

#if SPT_CLIENT
internal sealed class VanguardOperatorInventoryGetProfilesPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        MethodInfo? method = ResolveGetProfilesMethod();
        if (method == null)
        {
            throw new InvalidOperationException("Vanguard inventory mode patch failed: no compatible GetProfiles target found.");
        }

        VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_INVENTORY_PROFILE_REDIRECT_STATUS", $"GetProfiles patch target resolved: {method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? "<unknown>"}.{method.Name}");
        return method;
    }

    private static MethodInfo? ResolveGetProfilesMethod()
    {
        MethodInfo? namedSessionMethod = ResolveGetProfilesMethodOnType(FindLoadedTypeByName("SessionBackendClass"));
        if (namedSessionMethod != null)
        {
            return namedSessionMethod;
        }

        Type? sessionInterface = FindLoadedTypeByName("ISession");

        // SPT 4 / EFT builds can rename the concrete session backend. Resolve by shape
        // instead of relying only on SessionBackendClass, but only patch concrete methods
        // with a body. Interface-only GetProfiles targets are diagnostic noise, not safe.
        MethodInfo? concreteSessionMethod = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(type => type != null && !type.IsInterface && !type.IsAbstract)
            .Select(type => ResolveGetProfilesMethodOnType(type))
            .Where(method => method != null)
            .OrderByDescending(method => sessionInterface != null && method!.DeclaringType != null && sessionInterface.IsAssignableFrom(method.DeclaringType))
            .ThenByDescending(method => method!.DeclaringType?.Name.IndexOf("Session", StringComparison.OrdinalIgnoreCase) >= 0)
            .FirstOrDefault();
        if (concreteSessionMethod != null)
        {
            return concreteSessionMethod;
        }

        return null;
    }

    private static MethodInfo? ResolveGetProfilesMethodOnType(Type? type)
    {
        if (type == null)
        {
            return null;
        }

        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
                string.Equals(method.Name, "GetProfiles", StringComparison.Ordinal)
                && !method.IsAbstract
                && method.GetParameters().Length == 0
                && typeof(Task).IsAssignableFrom(method.ReturnType));
    }

    private static Type? FindLoadedTypeByName(string typeName)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(type =>
                string.Equals(type.FullName, typeName, StringComparison.Ordinal)
                || string.Equals(type.Name, typeName, StringComparison.Ordinal));
    }

    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type != null).Cast<Type>().ToArray();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    [PatchPrefix]
    private static bool Prefix(object __instance, ref Task __result)
    {
        if (!VanguardOperatorInventoryModeClientState.IsActive)
        {
            return true;
        }

        VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_PROFILE_REBIND_STATUS", $"getprofiles_prefix_invoked operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; inventoryProfile={VanguardOperatorInventoryModeClientState.InventoryProfileId ?? "<none>"}");
        __result = LoadOperatorProfilesAsync(__instance);
        return false;
    }

    private static async Task LoadOperatorProfilesAsync(object session)
    {
        try
        {
            if (!VanguardOperatorInventoryProfileLoader.TryBuildProfilesFromServer(out Array? profileArray, out string? appliedProfileId, out string reason) || profileArray == null || profileArray.Length == 0)
            {
                VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_INVENTORY_OFFRAID_STATUS", $"operator profile load failed: {reason}; leaving inventory mode client state.");
                VanguardOperatorInventoryModeClientState.Exit(skipProfileReload: true);
                return;
            }

            SetMember(session, "ProfilesUpdateTime", Time.time);
            SetMember(session, "AllProfiles", profileArray);
            object? activeProfile = profileArray.GetValue(0);
            SetMember(session, "Profile", activeProfile);
            object? profileStatuses = BuildProfileStatuses(profileArray);
            if (profileStatuses != null)
            {
                SetMember(session, "AllProfileStatus", profileStatuses);
            }

            VanguardOperatorInventoryModeClientState.MarkOperatorProfileApplied(appliedProfileId);
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_INVENTORY_OFFRAID_STATUS", $"operator profiles loaded count={profileArray.Length}; operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; appliedProfile={appliedProfileId ?? "<none>"}");
        }
        catch (Exception exception)
        {
            Exception root = exception is TargetInvocationException && exception.InnerException != null ? exception.InnerException : exception;
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_INVENTORY_OFFRAID_STATUS", $"operator profile load failed: {root.GetType().Name}: {root.Message}");
            VanguardOperatorInventoryModeClientState.Exit(skipProfileReload: true);
        }

        await Task.CompletedTask;
    }

    private static object? BuildProfileStatuses(Array profiles)
    {
        Type? statusType = AccessTools.TypeByName("ProfileStatusClass");
        Type? enumType = AccessTools.TypeByName("EProfileStatus");
        if (statusType == null || enumType == null)
        {
            return null;
        }

        Array statuses = Array.CreateInstance(statusType, profiles.Length);
        object freeValue = Enum.Parse(enumType, "Free");
        for (int i = 0; i < profiles.Length; i++)
        {
            object? profile = profiles.GetValue(i);
            object? profileId = profile == null ? null : ResolveMember(profile, "Id");
            object status = Activator.CreateInstance(statusType) ?? throw new InvalidOperationException("ProfileStatusClass could not be created.");
            SetMember(status, "profileid", profileId);
            SetMember(status, "status", freeValue);
            statuses.SetValue(status, i);
        }

        return statuses;
    }

    private static object? ResolveMember(object target, string name)
    {
        Type type = target.GetType();
        PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        if (property != null)
        {
            return property.GetValue(target);
        }

        FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        return field?.GetValue(target);
    }

    private static void SetMember(object target, string name, object? value)
    {
        Type type = target.GetType();
        PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        if (property != null && property.CanWrite)
        {
            property.SetValue(target, value);
            return;
        }

        FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        field?.SetValue(target, value);
    }

    private static JToken SelectPayloadToken(JToken token)
    {
        if (token is not JObject obj)
        {
            return token;
        }

        foreach (string propertyName in new[] { "data", "Data", "response", "Response", "result", "Result" })
        {
            if (obj.TryGetValue(propertyName, StringComparison.OrdinalIgnoreCase, out JToken? payload) && payload != null && payload.Type != JTokenType.Null)
            {
                return UnwrapStringToken(payload);
            }
        }

        return token;
    }

    private static JToken UnwrapStringToken(JToken token)
    {
        if (token.Type != JTokenType.String)
        {
            return token;
        }

        string? value = token.Value<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return token;
        }

        string trimmed = value.Trim();
        if ((!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
            && (!trimmed.StartsWith("[", StringComparison.Ordinal) || !trimmed.EndsWith("]", StringComparison.Ordinal)))
        {
            return token;
        }

        try
        {
            return JToken.Parse(trimmed);
        }
        catch
        {
            return token;
        }
    }
}
#else
internal sealed class VanguardOperatorInventoryGetProfilesPatch
{
    public void Enable()
    {
    }
}
#endif
