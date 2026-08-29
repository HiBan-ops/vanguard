using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Servers;

// Responsibility: Provides Spt40 Lost On Death Config Provider support for the server Operator inventory mode.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Server.Operators.Inventory.Services;

/// <summary>
/// SPT 4.0.x compatibility boundary for configuration access.
///
/// SPT 4.0.13 registers ConfigServer in DI but does not register each concrete
/// configuration model (including LostOnDeathConfig) as an independently
/// resolvable service. ConfigServer/GetConfig is obsolete only for the future
/// SPT 4.1 API, so the obsolete surface is intentionally isolated here.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class VanguardSpt40LostOnDeathConfigProvider
{
    public LostOnDeathConfig Value { get; }

#pragma warning disable CS0618 // SPT 4.0.13 canonical config access; remove with the future SPT 4.1 migration.
    public VanguardSpt40LostOnDeathConfigProvider(ConfigServer configServer)
    {
        Value = configServer.GetConfig<LostOnDeathConfig>();
    }
#pragma warning restore CS0618
}
