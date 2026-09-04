using System;
using System.Linq;
using System.Reflection;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: catches native item-context navigation paths that bypass MainMenuController.ShowScreen while an Operator inventory session is active.
// Flow: contextual Flea searches and Weapon Modding arm the same preserved-navigation lease used by normal qualified Off-Raid routes before InventoryScreen can emit OnClose.
// Authority boundary: navigation/lifecycle coordination only; these patches never commit, exit inventory mode, or replace server authority.
// Invariant: a right-click/context action opening a qualified Off-Raid screen cannot implicitly complete the Operator transaction.
namespace Vanguard.Client.UI.OffRaid.Inventory;

#if SPT_CLIENT
internal sealed class VanguardOperatorContextualRagfairNavigationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        Type ragfairType = AccessTools.TypeByName("RagFairClass")
            ?? AccessTools.TypeByName("EFT.UI.Ragfair.RagFairClass")
            ?? throw new MissingMemberException("RagFairClass type not found.");

        MethodInfo? target = ragfairType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
                string.Equals(method.Name, "ExternalRagfairSearch", StringComparison.Ordinal)
                && method.GetParameters().Length == 1);
        if (target == null)
        {
            throw new MissingMethodException(ragfairType.FullName ?? ragfairType.Name, "ExternalRagfairSearch(1 arg)");
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
            $"contextual_ragfair_patch_target_resolved target={target.DeclaringType?.FullName ?? ragfairType.FullName}.{target.Name}; signature={string.Join(",", target.GetParameters().Select(parameter => parameter.ParameterType.Name))}");
        return target;
    }

    [PatchPrefix]
    private static void Prefix(object[] __args)
    {
        if (!VanguardOperatorInventoryModeClientState.IsActive)
        {
            return;
        }

        string searchType = ResolveSearchType(__args.Length > 0 ? __args[0] : null);
        VanguardOperatorInventoryContextualNavigationLease.EnsurePreserved(
            "RagFair",
            "contextual_ragfair_search",
            $"searchType={searchType}");
    }

    private static string ResolveSearchType(object? search)
    {
        if (search == null)
        {
            return "<none>";
        }

        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type searchType = search.GetType();
            object? value = searchType.GetProperty("Type", flags)?.GetValue(search)
                ?? searchType.GetField("Type", flags)?.GetValue(search);
            return value?.ToString() ?? searchType.Name;
        }
        catch
        {
            return search.GetType().Name;
        }
    }
}

internal sealed class VanguardOperatorWeaponModdingNavigationPatch : ModulePatch
{
    private static Type? targetControllerType;

    protected override MethodBase GetTargetMethod()
    {
        Type screenType = AccessTools.TypeByName("EFT.UI.WeaponModding.WeaponModdingScreen")
            ?? AccessTools.TypeByName("WeaponModdingScreen")
            ?? throw new MissingMemberException("WeaponModdingScreen type not found.");

        targetControllerType = screenType.GetNestedType("GClass3922", BindingFlags.Public | BindingFlags.NonPublic)
            ?? screenType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(type => type.Name.StartsWith("GClass", StringComparison.Ordinal));
        if (targetControllerType == null)
        {
            throw new MissingMemberException(screenType.FullName ?? screenType.Name, "Weapon Modding screen controller");
        }

        MethodInfo? reflectedTarget = targetControllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
                string.Equals(method.Name, "ShowScreen", StringComparison.Ordinal)
                && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType.Name.IndexOf("EScreenState", StringComparison.OrdinalIgnoreCase) >= 0);
        if (reflectedTarget == null)
        {
            throw new MissingMethodException(targetControllerType.FullName ?? targetControllerType.Name, "ShowScreen(EScreenState)");
        }

        // Harmony must patch the method where its implementation is actually declared.
        // Returning the inherited MethodInfo as reflected through the concrete WeaponModding
        // controller causes HarmonyX to warn and, in practice, the Prefix is not reached.
        Type declaringType = reflectedTarget.DeclaringType ?? targetControllerType;
        Type[] parameterTypes = reflectedTarget.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        MethodInfo? declaredTarget = declaringType.GetMethod(
            reflectedTarget.Name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            binder: null,
            types: parameterTypes,
            modifiers: null);
        declaredTarget ??= reflectedTarget.GetBaseDefinition();

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
            $"weapon_modding_patch_target_resolved controller={targetControllerType.FullName ?? targetControllerType.Name}; reflectedTarget={reflectedTarget.ReflectedType?.FullName ?? "<none>"}.{reflectedTarget.Name}; declaredTarget={declaredTarget.DeclaringType?.FullName ?? declaringType.FullName}.{declaredTarget.Name}; declaredOnly={declaredTarget.DeclaringType == declaringType}");
        return declaredTarget;
    }

    [PatchPrefix]
    private static void Prefix(object __instance)
    {
        if (!VanguardOperatorInventoryModeClientState.IsActive
            || targetControllerType == null
            || __instance == null
            || !targetControllerType.IsInstanceOfType(__instance))
        {
            return;
        }

        VanguardOperatorInventoryContextualNavigationLease.EnsurePreserved(
            "WeaponModding",
            "weapon_modding_show_screen",
            $"controller={__instance.GetType().FullName ?? __instance.GetType().Name}");
    }
}

internal static class VanguardOperatorInventoryContextualNavigationLease
{
    public static void EnsurePreserved(string route, string source, string detail)
    {
        if (!VanguardOperatorInventoryModeClientState.IsActive)
        {
            return;
        }

        if (VanguardOperatorInventorySessionNavigation.HasPreservedNavigationLease)
        {
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
                $"contextual_navigation_lease_already_active operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; route={route}; source={source}; {detail}; session_preserved=True; commit_triggered=False; exit_triggered=False; reload_triggered=False");
            return;
        }

        VanguardOperatorInventorySessionNavigation.BeginPreservedNavigation(route, source);
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorInventoryNavigationGuardStatusTag,
            $"contextual_navigation_session_preserved operator={VanguardOperatorInventoryModeClientState.OperatorId ?? "<none>"}; route={route}; source={source}; {detail}; session_preserved=True; commit_triggered=False; exit_triggered=False; reload_triggered=False");
    }
}
#else
internal sealed class VanguardOperatorContextualRagfairNavigationPatch
{
    public void Enable()
    {
    }
}

internal sealed class VanguardOperatorWeaponModdingNavigationPatch
{
    public void Enable()
    {
    }
}
#endif
