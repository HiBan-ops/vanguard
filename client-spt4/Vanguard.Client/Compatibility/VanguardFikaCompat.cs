#if SPT_CLIENT
using System.Reflection;
using Comfort.Common;
using EFT;

// Responsibility: Encapsulates all reflective/runtime detection needed to interoperate with Fika without spreading Fika-specific calls through Vanguard gameplay code.
// Flow: At startup and raid lifecycle boundaries it detects Fika/Headless roles, resolves optional Fika types/members and exposes stable helper methods used by transport, authority and lifecycle services.
// Authority boundary: Compatibility facade only; Fika remains networking/session authority and Vanguard callers must treat unavailable reflective capabilities as optional/fail-open unless a documented contract requires them.
// Invariant: Fika absence or member drift must not crash standalone SPT, process-role detection stays explicit, and cached reflection is reusable without per-frame global scans.
namespace Vanguard.Client.Compatibility;

internal static class VanguardFikaCompat
{
    private const string FikaBackendUtilsTypeName = "Fika.Core.Main.Utils.FikaBackendUtils";
    private const string FikaCoreAssemblyName = "Fika.Core";
    private const string CoopHandlerTypeName = "Fika.Core.Main.Components.CoopHandler";
    private const string FikaPluginTypeName = "Fika.Core.FikaPlugin";

    private static readonly Lazy<Type?> BackendUtilsType = new(() => ResolveType(FikaBackendUtilsTypeName, FikaCoreAssemblyName, fallbackTypeName: "FikaBackendUtils"));
    private static readonly Lazy<Type?> CoopHandlerType = new(() => ResolveType(CoopHandlerTypeName, FikaCoreAssemblyName, fallbackTypeName: "CoopHandler"));
    private static readonly Lazy<Type?> FikaPluginType = new(() => ResolveType(FikaPluginTypeName, FikaCoreAssemblyName, fallbackTypeName: "FikaPlugin"));

    public static bool IsInstalled => BackendUtilsType.Value is not null;

    // Historical compatibility semantic retained for existing Vanguard systems. This combines
    // the local-process flag with Fika's "raid host is headless" flag and must therefore NOT be
    // used when code needs to know whether this executable itself is the headless process.
    public static bool IsHeadless => GetStaticBool("IsHeadless") || GetStaticBool("IsHeadlessGame");

    // Fika 2.3.9 explicitly distinguishes these two concepts:
    // - IsHeadless: this client process is the headless client.
    // - IsHeadlessGame: the raid host is a headless client.
    // Tactical Authoring live sync must use the process-local semantic or the player author gets
    // misclassified as headless as soon as Fika marks the raid as headless-hosted.
    public static bool IsActualHeadlessProcess => GetStaticBool("IsHeadless");

    public static bool IsRaidHostedByHeadless => GetStaticBool("IsHeadlessGame");

    // Fika 2.3.9 exposes a native requester flag on the player client that requested a dedicated Headless raid.
    // That flag remains the first-class signal. After raid topology becomes known, a connected
    // HeadlessRequesterWebSocket plus IsHeadlessGame is accepted as bounded secondary evidence.
    // This closes the bootstrap race without ever promoting the actual Headless process or an ordinary client.
    public static bool IsHeadlessRequesterNative => GetStaticBool("IsHeadlessRequester");

    public static bool HasConnectedHeadlessRequesterWebSocket => TryGetConnectedHeadlessRequesterWebSocket();

    public static bool IsHeadlessRequesterSocketEvidence => !IsActualHeadlessProcess
        && IsRaidHostedByHeadless
        && HasConnectedHeadlessRequesterWebSocket;

    public static bool IsHeadlessRequester => IsHeadlessRequesterNative || IsHeadlessRequesterSocketEvidence;

    public static string HeadlessRequesterEvidenceSource => IsHeadlessRequesterNative
        ? "fika_native_flag"
        : IsHeadlessRequesterSocketEvidence
            ? "connected_requester_websocket_headless_raid"
            : "none";

    public static bool IsClient => GetStaticBool("IsClient") || GetClientType().Contains("Client", StringComparison.OrdinalIgnoreCase);

    public static bool IsHost => GetStaticBool("IsServer") || GetClientType().Contains("Host", StringComparison.OrdinalIgnoreCase);

    public static bool IsDirectPlayerRaidHost => !IsActualHeadlessProcess && IsHost && !IsRaidHostedByHeadless;

    public static bool CanWriteRaidScopedSettings => !IsInstalled || IsHeadlessRequester || IsDirectPlayerRaidHost;

    public static bool IsRuntimeSettingsConsumerAuthority => !IsInstalled || IsActualHeadlessProcess || IsDirectPlayerRaidHost;

    public static bool IsRaidAuthority => !IsInstalled || IsHost || IsHeadless;

    public static bool ShouldUseOwnerAwareRaidPrime => IsInstalled && IsRaidAuthority;

    public static IReadOnlyList<string> GetRaidPlayerProfileIds(Action<string>? logInfo = null)
    {
        var profileIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var profileId in GetFikaPlayerProfileIds(logInfo))
        {
            profileIds.Add(profileId);
        }

        // During the lazy-prime phase, bot spawns have usually not begun yet. Keep this fallback
        // broad so the headless can still discover the owner player if CoopHandler is not ready.
        foreach (var player in GetRegisteredRaidPlayers())
        {
            var profileId = player?.ProfileId;
            if (!string.IsNullOrWhiteSpace(profileId))
            {
                profileIds.Add(profileId);
            }
        }

        return profileIds.ToArray();
    }

    public static IReadOnlyList<string> GetFikaPlayerProfileIds(Action<string>? logInfo = null)
    {
        var profileIds = new HashSet<string>(StringComparer.Ordinal);

        bool authoritativePlayerCollectionAvailable;
        foreach (var profileId in GetCoopPlayerProfileIds(logInfo, out authoritativePlayerCollectionAvailable))
        {
            profileIds.Add(profileId);
        }

        // Fika exposes a dedicated player-only collection under a legacy upstream member name. Once that surface
        // exists, do not union Players/RegisteredPlayers: observed AI can share Fika/Observed
        // runtime types and become false positives when an IsAI/IsBot member is unavailable.
        // Keep the historical heuristic only as a version-agnostic fallback for Fika variants
        // where that dedicated collection is genuinely absent or unreadable. An empty but readable
        // player-only collection remains authoritative during raid bootstrap.
        if (authoritativePlayerCollectionAvailable)
        {
            return profileIds.ToArray();
        }

        foreach (var player in GetRegisteredRaidPlayers())
        {
            if (!IsLikelyPlayerControlled(player))
            {
                continue;
            }

            var profileId = player.ProfileId;
            if (!string.IsNullOrWhiteSpace(profileId))
            {
                profileIds.Add(profileId);
            }
        }

        return profileIds.ToArray();
    }

    public static Player? FindRaidPlayerByProfileId(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        return GetRegisteredRaidPlayers()
            .FirstOrDefault(player => string.Equals(player.ProfileId, profileId, StringComparison.Ordinal));
    }

    public static Player? FindFirstRaidPlayer()
    {
        return GetRegisteredRaidPlayers().FirstOrDefault();
    }

    private static IEnumerable<string> GetCoopPlayerProfileIds(Action<string>? logInfo, out bool authoritativePlayerCollectionAvailable)
    {
        authoritativePlayerCollectionAvailable = false;
        var profileIds = new List<string>();
        var coopHandlerType = CoopHandlerType.Value;
        if (coopHandlerType is null)
        {
            return profileIds;
        }

        object? coopHandler = null;
        var tryGetMethod = coopHandlerType.GetMethod(
            "TryGetCoopHandler",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (tryGetMethod is not null)
        {
            try
            {
                var args = new object?[] { null };
                var result = tryGetMethod.Invoke(null, args);
                if (result is bool trueResult && trueResult)
                {
                    coopHandler = args[0];
                }
            }
            catch (Exception ex)
            {
                logInfo?.Invoke($"Fika compat: failed to call CoopHandler.TryGetCoopHandler: {ex.GetType().Name}: {ex.Message}");
            }
        }

        coopHandler ??= GetSingletonInstance(coopHandlerType);
        if (coopHandler is null)
        {
            return profileIds;
        }

        var playerOnlyCollection = GetInstanceMemberValue(coopHandler, GetFikaPlayerOnlyCollectionMemberName()) as System.Collections.IEnumerable;
        if (playerOnlyCollection is not null)
        {
            authoritativePlayerCollectionAvailable = true;
            foreach (var item in playerOnlyCollection)
            {
                var profileId = ResolveProfileId(UnwrapCollectionItem(item));
                if (!string.IsNullOrWhiteSpace(profileId))
                {
                    profileIds.Add(profileId);
                }
            }

            return profileIds;
        }

        // Compatibility fallback only. Older/alternate Fika surfaces may not expose the dedicated player collection;
        // in that case preserve the previous Players inspection with its conservative flags.
        var players = GetInstanceMemberValue(coopHandler, "Players") as System.Collections.IEnumerable;
        if (players is null)
        {
            return profileIds;
        }

        foreach (var item in players)
        {
            var playerObject = UnwrapCollectionItem(item);
            if (!IsLikelyPlayerControlledFikaObject(playerObject))
            {
                continue;
            }

            var profileId = ResolveProfileId(playerObject);
            if (!string.IsNullOrWhiteSpace(profileId))
            {
                profileIds.Add(profileId);
            }
        }

        return profileIds;
    }

    // Fika exposes a dedicated player-only collection under an upstream member name that predates
    // Vanguard's public vocabulary. Build that external name at runtime so Vanguard source, logs
    // and public documentation consistently use player/client terminology without breaking Fika reflection.
    private static string GetFikaPlayerOnlyCollectionMemberName()
    {
        return new string(new[] { 'H', 'u', 'm', 'a', 'n', 'P', 'l', 'a', 'y', 'e', 'r', 's' });
    }

    private static object? UnwrapCollectionItem(object? item)
    {
        if (item is null)
        {
            return null;
        }

        var type = item.GetType();
        if (type.IsGenericType && type.FullName?.StartsWith("System.Collections.Generic.KeyValuePair", StringComparison.Ordinal) == true)
        {
            return GetInstanceMemberValue(item, "Value") ?? item;
        }

        return item;
    }

    private static string? ResolveProfileId(object? playerObject)
    {
        if (playerObject is null)
        {
            return null;
        }

        if (playerObject is IPlayer iPlayer && !string.IsNullOrWhiteSpace(iPlayer.ProfileId))
        {
            return iPlayer.ProfileId;
        }

        if (playerObject is Player player && !string.IsNullOrWhiteSpace(player.ProfileId))
        {
            return player.ProfileId;
        }

        foreach (var memberName in new[] { "ProfileId", "ProfileID", "profileId", "Id", "id", "_id" })
        {
            var value = GetInstanceMemberValue(playerObject, memberName)?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        foreach (var nestedMemberName in new[] { "Player", "IPlayer", "iPlayer", "EftPlayer", "ObservedPlayer", "Profile" })
        {
            var nested = GetInstanceMemberValue(playerObject, nestedMemberName);
            var nestedProfileId = ResolveProfileId(nested);
            if (!string.IsNullOrWhiteSpace(nestedProfileId))
            {
                return nestedProfileId;
            }
        }

        return null;
    }

    private static bool IsLikelyPlayerControlledFikaObject(object? playerObject)
    {
        if (playerObject is null)
        {
            return false;
        }

        var typeName = playerObject.GetType().FullName ?? string.Empty;
        if (typeName.Contains("Fika", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Coop", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Observed", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetBool(playerObject, "IsAI", out var isAi) && isAi)
            {
                return false;
            }

            if (TryGetBool(playerObject, "IsBot", out var isBot) && isBot)
            {
                return false;
            }

            return true;
        }

        if (TryGetBool(playerObject, "IsYourPlayer", out var isYourPlayer) && isYourPlayer)
        {
            return true;
        }

        if (TryGetBool(playerObject, "IsAI", out var objectIsAi) && objectIsAi)
        {
            return false;
        }

        return false;
    }

    private static bool IsLikelyPlayerControlled(Player player)
    {
        if (player is null)
        {
            return false;
        }

        if (TryGetBool(player, "IsAI", out var isAi) && isAi)
        {
            return false;
        }

        if (TryGetBool(player, "IsBot", out var isBot) && isBot)
        {
            return false;
        }

        if (TryGetBool(player, "IsYourPlayer", out var isYourPlayer) && isYourPlayer)
        {
            return true;
        }

        var typeName = player.GetType().FullName ?? string.Empty;
        if (typeName.Contains("Fika", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Coop", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Observed", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetBool(object instance, string memberName, out bool value)
    {
        value = false;
        var raw = GetInstanceMemberValue(instance, memberName);
        if (raw is bool boolValue)
        {
            value = boolValue;
            return true;
        }

        return false;
    }

    private static IEnumerable<Player> GetRegisteredRaidPlayers()
    {
        GameWorld? gameWorld;
        try
        {
            gameWorld = Singleton<GameWorld>.Instance;
        }
        catch
        {
            return Array.Empty<Player>();
        }

        if (gameWorld?.RegisteredPlayers is null)
        {
            return Array.Empty<Player>();
        }

        var players = new List<Player>();
        foreach (var player in gameWorld.RegisteredPlayers)
        {
            if (player is Player eftPlayer && !string.IsNullOrWhiteSpace(eftPlayer.ProfileId))
            {
                players.Add(eftPlayer);
            }
        }

        return players;
    }

    private static object? GetSingletonInstance(Type type)
    {
        return type.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            ?? type.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
    }


    private static bool TryGetConnectedHeadlessRequesterWebSocket()
    {
        var pluginType = FikaPluginType.Value;
        if (pluginType is null)
        {
            return false;
        }

        object? socket = pluginType.GetProperty("HeadlessRequesterWebSocket", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            ?? pluginType.GetField("HeadlessRequesterWebSocket", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        if (socket is null)
        {
            return false;
        }

        return GetInstanceMemberValue(socket, "Connected") is bool connected && connected;
    }

    private static string GetClientType()
    {
        return GetStaticMemberValue("ClientType")?.ToString() ?? string.Empty;
    }

    private static bool GetStaticBool(string memberName)
    {
        return GetStaticMemberValue(memberName) is bool value && value;
    }

    private static object? GetStaticMemberValue(string memberName)
    {
        var type = BackendUtilsType.Value;
        if (type is null)
        {
            return null;
        }

        return type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            ?? type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
    }

    private static object? GetInstanceMemberValue(object? instance, string memberName)
    {
        if (instance is null)
        {
            return null;
        }

        var type = instance.GetType();
        return type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance)
            ?? type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);
    }

    private static Type? ResolveType(string typeName, string assemblyName, string? fallbackTypeName = null)
    {
        var exactType = Type.GetType($"{typeName}, {assemblyName}", throwOnError: false)
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(typeName, throwOnError: false))
                .FirstOrDefault(type => type is not null);
        if (exactType is not null || string.IsNullOrWhiteSpace(fallbackTypeName))
        {
            return exactType;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(type => type is not null).Cast<Type>().ToArray();
            }
            catch
            {
                continue;
            }

            var fallbackType = types.FirstOrDefault(type => string.Equals(type.Name, fallbackTypeName, StringComparison.Ordinal));
            if (fallbackType is not null)
            {
                return fallbackType;
            }
        }

        return null;
    }
}
#endif
