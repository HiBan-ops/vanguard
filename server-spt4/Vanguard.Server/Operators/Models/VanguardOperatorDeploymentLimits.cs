// Responsibility: Defines data/state contracts used by the Operator persistence/domain models, centered on Operator Deployment Limits.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Server.Operators.Models;

public sealed record VanguardOperatorDeploymentLimits(
    int PlayerLevel,
    int MaxHiredOperators,
    int MaxDeployableOperators,
    string Tier)
{
    public static VanguardOperatorDeploymentLimits FromPlayerLevel(int playerLevel)
    {
        var level = Math.Max(playerLevel, 1);
        return level switch
        {
            <= 14 => new VanguardOperatorDeploymentLimits(level, 1, 1, "level_1_14"),
            <= 19 => new VanguardOperatorDeploymentLimits(level, 2, 1, "level_15_19"),
            <= 29 => new VanguardOperatorDeploymentLimits(level, 4, 2, "level_20_29"),
            <= 39 => new VanguardOperatorDeploymentLimits(level, 6, 3, "level_30_39"),
            _ => new VanguardOperatorDeploymentLimits(level, 8, 4, "level_40_plus"),
        };
    }
}
