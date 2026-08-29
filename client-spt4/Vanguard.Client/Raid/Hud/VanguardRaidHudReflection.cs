#if SPT_CLIENT
using System;
using System.Collections;
using System.Linq;
using System.Reflection;

// Responsibility: Provides Raid Hud Reflection support for the raid Operator HUD.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Raid.Hud;

/// <summary>
/// Narrow reflection bridge for raid HUD read models. It deliberately has no gameplay side effect.
/// </summary>
internal static class VanguardRaidHudReflection
{
    private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    public static object? ReadMember(object? instance, string memberName)
    {
        if (instance is null || string.IsNullOrWhiteSpace(memberName))
        {
            return null;
        }

        try
        {
            var type = instance.GetType();
            return type.GetProperty(memberName, InstanceFlags)?.GetValue(instance)
                ?? type.GetField(memberName, InstanceFlags)?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    public static string? ReadNestedString(object? instance, params string[] path)
    {
        return ReadNested(instance, path)?.ToString();
    }

    public static object? ReadNested(object? instance, params string[] path)
    {
        object? current = instance;
        foreach (string memberName in path)
        {
            current = ReadMember(current, memberName);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    public static object? UnwrapCollectionItem(object? item)
    {
        if (item is null)
        {
            return null;
        }

        var type = item.GetType();
        if (type.IsGenericType && type.FullName?.StartsWith("System.Collections.Generic.KeyValuePair", StringComparison.Ordinal) == true)
        {
            return ReadMember(item, "Value") ?? item;
        }

        return item;
    }

    public static object? ReadStaticMember(Type type, string memberName)
    {
        try
        {
            return type.GetProperty(memberName, StaticFlags)?.GetValue(null)
                ?? type.GetField(memberName, StaticFlags)?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    public static object? ReadInstanceMember(object? instance, string memberName)
    {
        return ReadMember(instance, memberName);
    }

    public static Type? FindRuntimeType(string simpleOrFullName)
    {
        if (string.IsNullOrWhiteSpace(simpleOrFullName))
        {
            return null;
        }

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in assemblies)
        {
            try
            {
                var directType = assembly.GetType(simpleOrFullName, throwOnError: false, ignoreCase: false);
                if (directType is not null)
                {
                    return directType;
                }
            }
            catch
            {
            }
        }

        // A fully-qualified runtime type can be resolved without enumerating every type in every
        // loaded assembly. If it is not present yet (for example while an optional Fika assembly
        // is still loading), let the bounded caller retry later rather than doing an assembly-wide scan.
        if (simpleOrFullName.IndexOf('.') >= 0)
        {
            return null;
        }

        // Legacy simple-name fallback is retained for EFT types whose namespace is not stable.
        // Those callers cache the result and therefore pay this scan at most once per lifecycle.
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types.Where(type => type is not null).Cast<Type>().ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                string fullName = type.FullName ?? string.Empty;
                string name = type.Name ?? string.Empty;
                if (string.Equals(name, simpleOrFullName, StringComparison.Ordinal)
                    || fullName.EndsWith("." + simpleOrFullName, StringComparison.Ordinal))
                {
                    return type;
                }
            }
        }

        return null;
    }

    public static MethodInfo? FindSingleArgumentMethod(Type type, string methodName, Type argumentType)
    {
        foreach (var method in type.GetMethods(InstanceFlags))
        {
            if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == argumentType)
            {
                return method;
            }
        }

        return null;
    }

    public static IEnumerable EnumerateSafe(object? value)
    {
        return value as IEnumerable ?? Array.Empty<object>();
    }
}
#else
namespace Vanguard.Client.Raid.Hud;

internal static class VanguardRaidHudReflection
{
}
#endif
