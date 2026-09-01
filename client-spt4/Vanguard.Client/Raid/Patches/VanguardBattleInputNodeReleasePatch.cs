using System;
using System.Reflection;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Services;

#if SPT_CLIENT
using HarmonyLib;
using SPT.Reflection.Patching;
#endif

// Responsibility: Bridges EFT/SPT/Fika callbacks into Battle Input Node Release Patch for the raid lifecycle patch bridge.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Client.Raid.Patches;

#if SPT_CLIENT
internal sealed class VanguardBattleInputNodeReleasePatch : ModulePatch
{
    private const string BattleUiAssemblyQualifiedTypeName = "EFT.UI.EftBattleUIScreen, Assembly-CSharp";
    private const string BattleUiTypeName = "EFT.UI.EftBattleUIScreen";
    private const string PlayerOwnerAssemblyQualifiedTypeName = "EFT.GamePlayerOwner, Assembly-CSharp";
    private const string PlayerOwnerTypeName = "EFT.GamePlayerOwner";
    private const string TargetMethodName = "Show";

    protected override MethodBase GetTargetMethod()
    {
        // Runtime evidence proved that EftGamePlayerOwner.ShowBattleUIScreen() can be
        // bypassed/missed on a Fika headless-client transition even though the real
        // BattleUI is presented. Bind instead to the lower UI boundary used by EFT
        // itself (and independently observed by DynamicMaps): EftBattleUIScreen.Show
        // with a single EFT.GamePlayerOwner parameter.
        //
        // AccessTools.Method(EftBattleUIScreen, "Show", GamePlayerOwner) resolves the
        // inherited implementation. Harmony warns when a patch is attached through an
        // inherited MethodInfo, so normalize to the actual declaring method before
        // returning it. This also avoids hard-coding the nested obfuscated GClass used
        // in BattleUIScreen<,>'s closed generic base type.
        Type battleUiType = Type.GetType(BattleUiAssemblyQualifiedTypeName, throwOnError: false)
            ?? AccessTools.TypeByName(BattleUiTypeName)
            ?? throw new InvalidOperationException("EFT.UI.EftBattleUIScreen type not found for Vanguard BattleUI input-node release patch.");
        Type playerOwnerType = Type.GetType(PlayerOwnerAssemblyQualifiedTypeName, throwOnError: false)
            ?? AccessTools.TypeByName(PlayerOwnerTypeName)
            ?? throw new InvalidOperationException("EFT.GamePlayerOwner type not found for Vanguard BattleUI input-node release patch.");

        MethodInfo resolved = AccessTools.Method(battleUiType, TargetMethodName, new[] { playerOwnerType })
            ?? throw new InvalidOperationException("EFT.UI.EftBattleUIScreen.Show(EFT.GamePlayerOwner) not found for Vanguard BattleUI input-node release patch.");

        Type? declaringType = resolved.DeclaringType;
        if (declaringType != null)
        {
            MethodInfo? declared = AccessTools.DeclaredMethod(declaringType, TargetMethodName, new[] { playerOwnerType });
            if (declared != null)
            {
                VanguardClientDiagnosticsLog.Info(
                    VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
                    $"battle_input_node_release_patch_target_resolved reflectedType={battleUiType.FullName}; declaringType={declared.DeclaringType?.FullName}; method={declared.Name}; parameter={playerOwnerType.FullName}; boundary=effective_battle_ui_show");
                return declared;
            }
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
            $"battle_input_node_release_patch_target_resolved reflectedType={battleUiType.FullName}; declaringType={resolved.DeclaringType?.FullName}; method={resolved.Name}; parameter={playerOwnerType.FullName}; boundary=effective_battle_ui_show; normalization=declared_method_unavailable");
        return resolved;
    }

    [PatchPostfix]
    private static void PatchPostfix()
    {
        try
        {
            VanguardBattleInputNodeReleaseService.NotifyBattleUiShown("eft_battle_ui_show_presented");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
                $"battle_input_node_release_notify_failed reason={exception.GetType().Name}: {exception.Message}");
        }
    }
}
#else
internal sealed class VanguardBattleInputNodeReleasePatch
{
    public void Enable() { }
}
#endif
