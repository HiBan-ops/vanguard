using System.Collections.Generic;
using Mono.Cecil;
using MoreBotsAPI;

// Responsibility: Bridges EFT/SPT/Fika callbacks into Operator Wild Spawn Type Patch for the MoreBots pre-patch bootstrap.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.MoreBotsPrePatch;

public static class VanguardOperatorWildSpawnTypePatch
{
    public const string StatusTag = "VANGUARD_OPERATOR_BOT_TYPES_STATUS";
    public const string BuildStatusTag = "VANGUARD_PREPATCH_BUILD_STATUS";
    public const string RoleSubstring = "vanguardOperator";
    public const int UsecRoleValue = 867100;
    public const int BearRoleValue = 867101;
    public const string UsecRoleName = "vanguardOperatorUSEC";
    public const string BearRoleName = "vanguardOperatorBEAR";

    private const int BaseUsecBrain = 52;
    private const int BaseBearBrain = 51;

    public static IEnumerable<string> TargetDLLs { get; } = new[] { "Assembly-CSharp.dll" };

    public static void Patch(ref AssemblyDefinition assembly)
    {
        RegisterOperatorType(assembly, UsecRoleValue, UsecRoleName, BaseUsecBrain, "PmcUsec");
        RegisterOperatorType(assembly, BearRoleValue, BearRoleName, BaseBearBrain, "PmcBear");
        CustomWildSpawnTypeManager.AddSuitableGroup(new List<int> { UsecRoleValue, BearRoleValue });
    }

    private static void RegisterOperatorType(AssemblyDefinition assembly, int value, string name, int baseBrain, string sainBrain)
    {
        if (CustomWildSpawnTypeManager.IsCustomWildSpawnType(value))
        {
            return;
        }

        var customType = new CustomWildSpawnType(value, name, "PMC", baseBrain, false, false, false);
        customType.SetCountAsBossForStatistics(false);
        customType.SetShouldUseFenceNoBossAttack(false, false);
        customType.SetExcludedDifficulties(new List<int>());
        customType.SetSAINSettings(new SAINSettings(value)
        {
            Name = name,
            Description = "Vanguard persistent Operator role. SAIN remains the individual combat brain; Vanguard owns squad drive, medical, cohesion and supervised loot windows.",
            Section = "Vanguard",
            BaseBrain = sainBrain,
            BrainsToApply = new List<string> { sainBrain, "PMC" },
            DifficultyModifier = 1.0f,
        });

        CustomWildSpawnTypeManager.RegisterWildSpawnType(customType, assembly);
    }
}
