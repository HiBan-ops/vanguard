#if SPT_CLIENT
using System;
using System.Linq;
using System.Reflection;
using EFT;

// Responsibility: Provides Eft Reflection support for the Vanguard client.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Raid.Interop;

/// <summary>
/// Small, centralized reflection bridge for EFT APIs whose signatures can expose
/// optional runtime assemblies such as DissonanceVoip. Keeping those calls here
/// prevents accidental compile-time dependencies in the Vanguard client project.
/// </summary>
internal static class VanguardEftReflection
{
    public static void InvokeSingleArgumentMethod(object? target, string methodName, object argument)
    {
        if (target is null || argument is null)
        {
            return;
        }

        var method = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, methodName, StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(argument);
            });

        method?.Invoke(target, new[] { argument });
    }

    public static bool TryAddEnemy(object? group, object? player, EBotEnemyCause cause)
    {
        if (group is null || player is null)
        {
            return false;
        }

        var method = group.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, "AddEnemy", StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length == 2
                    && parameters[0].ParameterType.IsInstanceOfType(player)
                    && parameters[1].ParameterType.IsEnum;
            });

        if (method is null)
        {
            return false;
        }

        var causeParameterType = method.GetParameters()[1].ParameterType;
        object causeValue = causeParameterType == typeof(EBotEnemyCause)
            ? cause
            : Enum.ToObject(causeParameterType, (int)cause);

        object? result = method.Invoke(group, new[] { player, causeValue });
        return result is not bool boolResult || boolResult;
    }

    public static object? ReadFirstMember(object? root, params string[] paths)
    {
        foreach (string path in paths)
        {
            var value = ReadMemberPath(root, path);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    public static string? TryResolveProfileId(object? instance)
    {
        if (instance is null)
        {
            return null;
        }

        if (instance is IPlayer iPlayer && !string.IsNullOrWhiteSpace(iPlayer.ProfileId))
        {
            return iPlayer.ProfileId;
        }

        foreach (string memberName in new[] { "ProfileId", "ProfileID", "Id", "id", "_id" })
        {
            var value = ReadFirstMember(instance, memberName)?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        foreach (string nestedMemberName in new[] { "Player", "IPlayer", "Profile" })
        {
            var nested = ReadFirstMember(instance, nestedMemberName);
            var nestedId = TryResolveProfileId(nested);
            if (!string.IsNullOrWhiteSpace(nestedId))
            {
                return nestedId;
            }
        }

        return null;
    }

    private static object? ReadMemberPath(object? root, string path)
    {
        object? current = root;
        foreach (string segment in path.Split('.'))
        {
            if (current is null)
            {
                return null;
            }

            var type = current.GetType();
            var property = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, segment, StringComparison.Ordinal)
                    && candidate.GetIndexParameters().Length == 0);
            if (property is not null)
            {
                current = property.GetValue(current);
                continue;
            }

            var field = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, segment, StringComparison.Ordinal));
            if (field is not null)
            {
                current = field.GetValue(current);
                continue;
            }

            return null;
        }

        return current;
    }
}
#else
namespace Vanguard.Client.Raid.Interop;

internal static class VanguardEftReflection
{
}
#endif
