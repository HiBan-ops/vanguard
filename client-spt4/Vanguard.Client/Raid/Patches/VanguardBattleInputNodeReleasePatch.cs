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
    private const string TargetAssemblyQualifiedTypeName = "EFT.EftGamePlayerOwner, Assembly-CSharp";
    private const string TargetTypeName = "EFT.EftGamePlayerOwner";
    private const string FallbackTargetTypeName = "EftGamePlayerOwner";
    private const string TargetMethodName = "ShowBattleUIScreen";

    protected override MethodBase GetTargetMethod()
    {
        // Do not use typeof(EftGamePlayerOwner). In SPT 4.0.x this type inherits
        // through Sirenix serialized behaviours, which would add a compile-time
        // dependency the client project deliberately does not carry. Runtime
        // string/reflection lookup keeps the patch bound without dirty references.
        Type ownerType = Type.GetType(TargetAssemblyQualifiedTypeName, throwOnError: false)
            ?? AccessTools.TypeByName(TargetTypeName)
            ?? AccessTools.TypeByName(FallbackTargetTypeName)
            ?? throw new InvalidOperationException("EFT.EftGamePlayerOwner type not found for Vanguard BattleUI input-node release patch.");

        return AccessTools.Method(ownerType, TargetMethodName, Array.Empty<Type>())
            ?? AccessTools.Method(ownerType, TargetMethodName)
            ?? throw new InvalidOperationException("EFT.EftGamePlayerOwner.ShowBattleUIScreen() not found for Vanguard BattleUI input-node release patch.");
    }

    [PatchPostfix]
    private static void PatchPostfix()
    {
        try
        {
            VanguardBattleInputNodeReleaseService.NotifyBattleUiShown("eft_battle_ui_shown");
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
