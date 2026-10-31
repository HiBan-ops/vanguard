using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Servers;

// Responsibility: Expose the SPT LostOnDeathConfig through the server configuration service.
// Flow: Resolve LostOnDeathConfig from ConfigServer in the injectable provider constructor.
// Authority boundary: SPT ConfigServer owns configuration data; this provider does not modify loss-on-death rules.
// Invariant: The supported SPT configuration access path remains isolated behind the existing CS0618 compatibility pragma.
namespace Vanguard.Server.Operators.Inventory.Services;

/// <summary>
/// SPT compatibility boundary for configuration access.
///
/// SPT registers ConfigServer in DI but does not register each concrete
/// configuration model (including LostOnDeathConfig) as an independently
/// resolvable service. ConfigServer/GetConfig is obsolete only for the future
/// a later SPT API, so the obsolete surface is intentionally isolated here.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class VanguardLostOnDeathConfigProvider
{
    public LostOnDeathConfig Value { get; }

#pragma warning disable CS0618 // SPT canonical config access; remove when migrating to a later SPT API.
    public VanguardLostOnDeathConfigProvider(ConfigServer configServer)
    {
        Value = configServer.GetConfig<LostOnDeathConfig>();
    }
#pragma warning restore CS0618
}
