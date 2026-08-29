#if SPT_CLIENT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Vanguard.Client.Diagnostics;

// Responsibility: Collects the reflection helpers used by runtime diagnostics to inspect EFT/SAIN/Fika objects without coupling audit code to unstable concrete members.
// Flow: Reusable member/type lookups extract diagnostic-only values through cached reflective access and return explicit unknown/failure results when the target surface is unavailable.
// Authority boundary: Audit reflection is observation-only and must never become a hidden gameplay control path.
// Invariant: Reflection failures stay non-fatal, lookups are cached/bounded, and diagnostics never mutate the inspected object graph.
namespace Vanguard.Client.Runtime.Audit;

internal static class VanguardOperatorRuntimeAuditReflection
{
    private static readonly object CacheSync = new();
    private static readonly HashSet<string> MissingLogged = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Type?> TypeCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, MemberInfo?> MemberCache = new(StringComparer.Ordinal);

    public static object? GetMember(object? instance, params string[] names)
    {
        return GetMemberInternal(instance, allowNoArgMethods: true, names);
    }

    public static object? GetPropertyOrField(object? instance, params string[] names)
    {
        return GetMemberInternal(instance, allowNoArgMethods: false, names);
    }

    public static object? InvokeNoArg(object? instance, params string[] names)
    {
        if (instance == null)
        {
            return null;
        }

        var type = instance.GetType();
        foreach (string name in names)
        {
            try
            {
                var member = ResolveMember(type, name, allowNoArgMethods: true, methodsOnly: true);
                if (member is MethodInfo method)
                {
                    return method.Invoke(instance, Array.Empty<object>());
                }
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public static object? GetDeep(object? instance, params string[] path)
    {
        object? current = instance;
        foreach (string name in path)
        {
            current = GetMember(current, name);
            if (current == null)
            {
                return null;
            }
        }

        return current;
    }

    public static Type? FindType(params string[] fullTypeNames)
    {
        foreach (string fullTypeName in fullTypeNames)
        {
            if (string.IsNullOrWhiteSpace(fullTypeName))
            {
                continue;
            }

            if (TypeCache.TryGetValue(fullTypeName, out var cached))
            {
                return cached;
            }

            Type? resolved = Type.GetType(fullTypeName, throwOnError: false);
            if (resolved == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        resolved = assembly.GetType(fullTypeName, throwOnError: false, ignoreCase: false);
                        if (resolved != null)
                        {
                            break;
                        }
                    }
                    catch
                    {
                        // Broken or dynamic assemblies must not break passive audit.
                    }
                }
            }

            // Do not cache misses: optional integrations can finish loading after
            // Vanguard boot, and a stale null cache would hide them for the whole raid.
            if (resolved != null)
            {
                TypeCache[fullTypeName] = resolved;
                return resolved;
            }
        }

        return null;
    }

    public static object? GetComponentByTypeName(object? unityObject, params string[] fullTypeNames)
    {
        Type? componentType = FindType(fullTypeNames);
        if (componentType == null || unityObject == null)
        {
            return null;
        }

        try
        {
            if (unityObject is Component component)
            {
                return component.gameObject.GetComponent(componentType);
            }

            if (unityObject is GameObject gameObject)
            {
                return gameObject.GetComponent(componentType);
            }

            object? gameObjectMember = GetPropertyOrField(unityObject, "gameObject", "GameObject");
            if (gameObjectMember is GameObject nestedGameObject)
            {
                return nestedGameObject.GetComponent(componentType);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public static object? GetComponentFromBotOrPlayer(object? botOwner, params string[] fullTypeNames)
    {
        object? component = GetComponentByTypeName(botOwner, fullTypeNames);
        if (component != null)
        {
            return component;
        }

        object? player = GetMember(botOwner, "GetPlayer", "Player");
        return GetComponentByTypeName(player, fullTypeNames);
    }

    public static object? GetStaticMember(Type? type, params string[] names)
    {
        if (type == null)
        {
            return null;
        }

        foreach (string name in names)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                var property = type.GetProperty(name, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return property.GetValue(null);
                }

                var field = type.GetField(name, flags);
                if (field != null)
                {
                    return field.GetValue(null);
                }
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public static object? InvokeStatic(Type? type, string name, params object?[] args)
    {
        if (type == null)
        {
            return null;
        }

        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (var method in type.GetMethods(flags).Where(candidate => candidate.Name == name))
            {
                var parameters = method.GetParameters();
                if (parameters.Length != args.Length)
                {
                    continue;
                }

                return method.Invoke(null, args);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public static bool TypeExists(params string[] fullTypeNames)
    {
        return FindType(fullTypeNames) != null;
    }

    public static string Text(object? value)
    {
        if (value == null)
        {
            return "none";
        }

        if (value is Type type)
        {
            return Compact(type.Name, 80);
        }

        string text = value.ToString() ?? "none";
        return Compact(text, 80);
    }

    public static string BoolText(object? value)
    {
        if (value is bool boolValue)
        {
            return boolValue ? "true" : "false";
        }

        return Text(value);
    }

    public static string FloatText(object? value)
    {
        try
        {
            if (value == null)
            {
                return "none";
            }

            float number = Convert.ToSingle(value);
            if (float.IsNaN(number) || float.IsInfinity(number))
            {
                return "none";
            }

            return number.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return Text(value);
        }
    }

    public static string TypeName(object? value)
    {
        return value?.GetType().Name ?? "none";
    }

    public static string LayerName(object? layer)
    {
        if (layer == null)
        {
            return "none";
        }

        return FirstNonEmpty(
            Text(InvokeNoArg(layer, "Name")),
            Text(GetPropertyOrField(layer, "Name")),
            TypeName(layer));
    }

    public static string VectorText(object? value)
    {
        if (value is Vector3 vector)
        {
            return $"{vector.x:0.0},{vector.y:0.0},{vector.z:0.0}";
        }

        object? hasValue = GetPropertyOrField(value, "HasValue");
        if (hasValue is bool trueValue && trueValue)
        {
            object? nested = GetPropertyOrField(value, "Value");
            if (nested is Vector3 nestedVector)
            {
                return $"{nestedVector.x:0.0},{nestedVector.y:0.0},{nestedVector.z:0.0}";
            }
        }

        return Text(value);
    }

    public static string CountText(object? value)
    {
        if (value == null)
        {
            return "none";
        }

        if (value is ICollection collection)
        {
            return collection.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        object? count = GetPropertyOrField(value, "Count", "Length");
        return Text(count);
    }

    public static string Compact(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "none";
        }

        string compact = text.Replace('\r', ' ').Replace('\n', ' ').Replace(';', ',').Trim();
        compact = compact.Replace("  ", " ");
        return compact.Length <= max ? compact : compact[..max];
    }

    public static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value) && !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return "none";
    }

    public static string JoinParts(params string[] parts)
    {
        return string.Join(",", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    public static void LogMissingOnce(string key, string message)
    {
        if (MissingLogged.Add(key))
        {
            VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OperatorRuntimeAuditStatusTag, $"VANGUARD_OPERATOR_AUDIT_REFLECTION_MISSING {message}");
        }
    }

    private static object? GetMemberInternal(object? instance, bool allowNoArgMethods, params string[] names)
    {
        if (instance == null)
        {
            return null;
        }

        var type = instance.GetType();
        foreach (string name in names)
        {
            try
            {
                var member = ResolveMember(type, name, allowNoArgMethods, methodsOnly: false);
                if (member is PropertyInfo property)
                {
                    return property.GetValue(instance);
                }

                if (member is FieldInfo field)
                {
                    return field.GetValue(instance);
                }

                if (member is MethodInfo method)
                {
                    return method.Invoke(instance, Array.Empty<object>());
                }
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static MemberInfo? ResolveMember(Type type, string name, bool allowNoArgMethods, bool methodsOnly)
    {
        string key = type.AssemblyQualifiedName + "|" + name + "|" + (allowNoArgMethods ? "1" : "0") + "|" + (methodsOnly ? "1" : "0");
        lock (CacheSync)
        {
            if (MemberCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        MemberInfo? resolved = null;
        if (!methodsOnly)
        {
            var property = type.GetProperty(name, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                resolved = property;
            }
            else
            {
                resolved = type.GetField(name, flags);
            }
        }

        if (resolved == null && allowNoArgMethods)
        {
            resolved = type.GetMethod(name, flags, null, Type.EmptyTypes, null);
        }

        lock (CacheSync)
        {
            MemberCache[key] = resolved;
        }

        return resolved;
    }
}
#endif
