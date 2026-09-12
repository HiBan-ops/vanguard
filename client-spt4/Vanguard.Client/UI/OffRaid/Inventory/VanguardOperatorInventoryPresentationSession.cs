using System;
using System.Linq;
using System.Reflection;
using Vanguard.Client.Diagnostics;

// Responsibility: provides InventoryScreen with an Operator-profile presentation view of EFT's live session without mutating the real session object.
// Flow: the direct Operator InventoryScreen receives a DispatchProxy implementing the exact native ISession interface; Profile reads return the already-built Operator composite while every other session member delegates to the live player session.
// Authority boundary: presentation only. Backend calls, network ownership, ItemUiContext and the Operator InventoryController continue to use the original live session; the real session Profile reference is never replaced.
// Invariant: failure to build the projection fails soft to the original native session and is diagnosed; no persistent/profile mutation is performed by this adapter.
namespace Vanguard.Client.UI.OffRaid.Inventory;

internal static class VanguardOperatorInventoryPresentationSession
{
    public const string StatusTag = "VANGUARD_OPERATOR_PROGRESSION_SESSION_BINDING_STATUS";

    public static bool TryCreate(
        object realSession,
        object operatorProfile,
        Type expectedSessionType,
        out object? projectedSession,
        out string reason)
    {
        projectedSession = null;
        reason = "unknown";

        if (realSession == null || operatorProfile == null)
        {
            reason = "session_or_operator_profile_missing";
            return false;
        }

        if (expectedSessionType == null || !expectedSessionType.IsInterface)
        {
            reason = "expected_session_type_not_interface:" + (expectedSessionType?.FullName ?? "<null>");
            return false;
        }

        if (!expectedSessionType.IsInstanceOfType(realSession))
        {
            reason = "real_session_not_assignable_to_expected_interface:" + expectedSessionType.FullName;
            return false;
        }

        PropertyInfo? profileProperty = ResolveInterfaceProperty(expectedSessionType, "Profile");
        if (profileProperty == null || !profileProperty.CanRead)
        {
            reason = "expected_session_profile_property_missing";
            return false;
        }

        if (!profileProperty.PropertyType.IsInstanceOfType(operatorProfile))
        {
            reason = "operator_profile_not_assignable_to_session_profile:" + profileProperty.PropertyType.FullName;
            return false;
        }

        try
        {
            MethodInfo? createMethod = typeof(DispatchProxy)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method =>
                    string.Equals(method.Name, "Create", StringComparison.Ordinal)
                    && method.IsGenericMethodDefinition
                    && method.GetGenericArguments().Length == 2
                    && method.GetParameters().Length == 0);
            if (createMethod == null)
            {
                reason = "dispatch_proxy_create_method_missing";
                return false;
            }

            MethodInfo closedCreate = createMethod.MakeGenericMethod(
                expectedSessionType,
                typeof(VanguardOperatorInventoryPresentationSessionProxy));
            object? candidate = closedCreate.Invoke(null, null);
            if (candidate == null || !expectedSessionType.IsInstanceOfType(candidate))
            {
                reason = "dispatch_proxy_create_returned_invalid_instance";
                return false;
            }

            if (candidate is not VanguardOperatorInventoryPresentationSessionProxy proxy)
            {
                reason = "dispatch_proxy_runtime_type_not_projection_proxy:" + candidate.GetType().FullName;
                return false;
            }

            proxy.Initialize(realSession, operatorProfile, profileProperty.PropertyType);
            projectedSession = candidate;
            reason = "operator_profile_read_projection_ready";

            string? realProfileId = ResolveProfileId(realSession);
            string? operatorProfileId = ResolveMember(operatorProfile, "Id")?.ToString();
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"state=ready; interface={Safe(expectedSessionType.FullName)}; realSession={Safe(realSession.GetType().FullName)}; realProfile={Safe(realProfileId)}; projectedProfile={Safe(operatorProfileId)}; realSessionProfileMutation=false; delegatedSessionAuthority=true; presentationProfileAuthority=operator");
            return true;
        }
        catch (Exception exception)
        {
            Exception root = exception is TargetInvocationException && exception.InnerException != null
                ? exception.InnerException
                : exception;
            reason = "dispatch_proxy_failed:" + root.GetType().Name + ":" + root.Message;
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"state=unavailable; reason={Safe(reason)}; interface={Safe(expectedSessionType.FullName)}; realSessionProfileMutation=false; nativeSessionPreserved=true");
            projectedSession = null;
            return false;
        }
    }

    private static PropertyInfo? ResolveInterfaceProperty(Type interfaceType, string name)
    {
        PropertyInfo? direct = interfaceType.GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (direct != null)
        {
            return direct;
        }

        foreach (Type inheritedInterface in interfaceType.GetInterfaces())
        {
            PropertyInfo? inherited = ResolveInterfaceProperty(inheritedInterface, name);
            if (inherited != null)
            {
                return inherited;
            }
        }

        return null;
    }

    private static string? ResolveProfileId(object session)
    {
        object? profile = ResolveMember(session, "Profile");
        return profile == null ? null : ResolveMember(profile, "Id")?.ToString();
    }

    private static object? ResolveMember(object target, string name)
    {
        Type? type = target.GetType();
        while (type != null)
        {
            PropertyInfo? property = type.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                try
                {
                    return property.GetValue(target);
                }
                catch
                {
                    return null;
                }
            }

            FieldInfo? field = type.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (field != null)
            {
                try
                {
                    return field.GetValue(target);
                }
                catch
                {
                    return null;
                }
            }

            type = type.BaseType;
        }

        return null;
    }

    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<none>";
        }

        return value.Replace(";", ",").Replace("\r", " ").Replace("\n", " ").Trim();
    }
}

// Must be public and non-sealed because DispatchProxy generates a runtime subclass in a dynamic assembly.
public class VanguardOperatorInventoryPresentationSessionProxy : DispatchProxy
{
    private object? realSession;
    private object? operatorProfile;
    private Type? profileType;

    internal void Initialize(object sourceSession, object projectedProfile, Type projectedProfileType)
    {
        realSession = sourceSession ?? throw new ArgumentNullException(nameof(sourceSession));
        operatorProfile = projectedProfile ?? throw new ArgumentNullException(nameof(projectedProfile));
        profileType = projectedProfileType ?? throw new ArgumentNullException(nameof(projectedProfileType));
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
        {
            throw new MissingMethodException("DispatchProxy target method was null.");
        }

        object sourceSession = realSession ?? throw new InvalidOperationException("Operator presentation session proxy is not initialized.");
        object projectedProfile = operatorProfile ?? throw new InvalidOperationException("Operator presentation profile is not initialized.");
        Type expectedProfileType = profileType ?? throw new InvalidOperationException("Operator presentation profile type is not initialized.");

        if (string.Equals(targetMethod.Name, "get_Profile", StringComparison.Ordinal)
            && targetMethod.GetParameters().Length == 0
            && expectedProfileType.IsInstanceOfType(projectedProfile)
            && targetMethod.ReturnType.IsInstanceOfType(projectedProfile))
        {
            return projectedProfile;
        }

        try
        {
            return targetMethod.Invoke(sourceSession, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            throw exception.InnerException!;
        }
    }
}
