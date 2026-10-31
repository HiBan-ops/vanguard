using System;

#if SPT_CLIENT
using EFT;
#endif

// Responsibility: Declare and classify the stable MoreBotsAPI role identifiers reserved for Vanguard Operators.
// Flow: Map Operator faction to the expected WildSpawnType and recognize Operators by reserved name or value.
// Authority boundary: This type contract identifies Operators to MoreBotsAPI and integration consumers; it does not spawn bots.
// Invariant: USEC/BEAR role values and the vanguardOperator name substring remain stable for external role filters.
namespace Vanguard.Client.Runtime.Integrations.MoreBots;

/// <summary>
/// Canonical MoreBotsAPI WildSpawnType contract for Vanguard Operators.
/// The names intentionally share the stable substring "vanguardOperator" so ORBIT and other external
/// drivers can exclude Operators by role-name without knowing Vanguard internals.
/// </summary>
internal static class VanguardOperatorBotTypes
{
    public const string StatusTag = "VANGUARD_OPERATOR_BOT_TYPES_STATUS";
    public const string OrbitBoundaryStatusTag = "VANGUARD_ORBIT_ROLE_EXCLUSION_STATUS";
    public const string LootBoundaryStatusTag = "VANGUARD_OPERATOR_LOOT_BOUNDARY_STATUS";
    public const string RoleSubstring = "vanguardOperator";

    // VG -> ASCII 86/71 -> 867100+ range. Values are intentionally outside vanilla EFT 0-200.
    public const int UsecRoleValue = 867100;
    public const int BearRoleValue = 867101;
    public const string UsecRoleName = "vanguardOperatorUSEC";
    public const string BearRoleName = "vanguardOperatorBEAR";

    public static string ResolveExpectedName(int value)
    {
        return value switch
        {
            UsecRoleValue => UsecRoleName,
            BearRoleValue => BearRoleName,
            _ => string.Empty,
        };
    }

    public static bool IsVanguardOperatorRoleName(string? roleName)
    {
        return !string.IsNullOrWhiteSpace(roleName)
            && roleName.IndexOf(RoleSubstring, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static bool IsVanguardOperatorRoleValue(int value)
    {
        return value == UsecRoleValue || value == BearRoleValue;
    }

#if SPT_CLIENT
    public static bool TryResolveRole(EPlayerSide side, out WildSpawnType role, out string diagnostic)
    {
        int value = side == EPlayerSide.Bear ? BearRoleValue : UsecRoleValue;
        string expected = ResolveExpectedName(value);
        string? actual = Enum.GetName(typeof(WildSpawnType), value);
        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            role = (WildSpawnType)value;
            diagnostic = "custom_role_available:" + expected + ":" + value;
            return true;
        }

        role = side switch
        {
            EPlayerSide.Bear => WildSpawnType.pmcBEAR,
            EPlayerSide.Usec => WildSpawnType.pmcUSEC,
            _ => WildSpawnType.assault,
        };
        diagnostic = "custom_role_missing:fallback=" + role + ":expected=" + expected + ":value=" + value + ":actual=" + (actual ?? "none");
        return false;
    }

    public static bool IsVanguardOperatorRole(WildSpawnType role)
    {
        int value = (int)role;
        if (IsVanguardOperatorRoleValue(value))
        {
            return true;
        }

        return IsVanguardOperatorRoleName(role.ToString());
    }

    public static bool IsVanguardOperatorRole(BotOwner? botOwner)
    {
        var role = botOwner?.Profile?.Info?.Settings?.Role;
        return role.HasValue && IsVanguardOperatorRole(role.Value);
    }

    public static string DescribeRole(BotOwner? botOwner)
    {
        var role = botOwner?.Profile?.Info?.Settings?.Role;
        return role.HasValue ? role.Value + ":" + ((int)role.Value) : "unknown";
    }
#endif
}
