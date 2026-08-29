using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using Vanguard.Server.Diagnostics;

// Responsibility: Registers the HTTP route surface for More Bots Difficulty Bridge Routes in the server MoreBots integration.
// Flow: SPT route callbacks deserialize input, delegate validation/business work to domain services, and serialize the resulting response.
// Authority boundary: Routing owns transport only; domain services and the Operator store remain authoritative for business and persistence state.
// Invariant: Routes do not duplicate domain logic and profile/session identity is forwarded explicitly to the owning service.
namespace Vanguard.Server.Integrations.MoreBots;

/// <summary>
/// Vanguard server-side difficulty bridge for Vanguard custom Operator WildSpawnTypes.
///
/// MoreBotsAPI adds the enum values on the client prepatch side. Fika then enumerates every
/// WildSpawnType/difficulty pair during client boot, before the menu is reached. If the server
/// response for /singleplayer/settings/bot/difficulties does not contain entries for the custom
/// roles, Fika can fail to populate LocalBotSettingsProviderClass and the client can remain on the
/// initial loading screen indefinitely.
///
/// Vanguard Operators are still generated from vanilla PMC data, then retyped client-side. For the
/// difficulty contract, the safest boundary is therefore to mirror vanilla PMC difficulty settings:
/// vanguardOperatorUSEC -> pmcusec, vanguardOperatorBEAR -> pmcbear.
/// </summary>
[Injectable]
public sealed class VanguardMoreBotsDifficultyBridgeRoutes : DynamicRouter
{
    public const string StatusTag = "VANGUARD_BOT_DIFFICULTY_BRIDGE_STATUS";

    private const string UsecSourceRole = "pmcusec";
    private const string BearSourceRole = "pmcbear";
    private const string UsecVanguardRole = "vanguardoperatorusec";
    private const string BearVanguardRole = "vanguardoperatorbear";

    private static JsonUtil? _jsonUtil;
    private static HttpResponseUtil? _httpResponseUtil;
    private static DatabaseService? _databaseService;
    private static ISptLogger<VanguardMoreBotsDifficultyBridgeRoutes>? _logger;
    private static bool _loggedMissingSource;
    private static bool _loggedException;

    public VanguardMoreBotsDifficultyBridgeRoutes(
        JsonUtil jsonUtil,
        HttpResponseUtil httpResponseUtil,
        DatabaseService databaseService,
        ISptLogger<VanguardMoreBotsDifficultyBridgeRoutes> logger)
        : base(jsonUtil, GetRoutes())
    {
        _jsonUtil = jsonUtil;
        _httpResponseUtil = httpResponseUtil;
        _databaseService = databaseService;
        _logger = logger;
    }

    private static List<RouteAction> GetRoutes()
    {
        return
        [
            new RouteAction(
                "/singleplayer/settings/bot/difficulties",
                async (url, info, sessionId, output) =>
                {
                    var result = BuildPatchedDifficultyPayload(output);
                    return await new ValueTask<string>((_httpResponseUtil ?? throw new InvalidOperationException("Vanguard difficulty bridge HTTP utility is not initialized.")).NoBody(result));
                })
        ];
    }

    private static Dictionary<string, Dictionary<string, DifficultyCategories>> BuildPatchedDifficultyPayload(string? output)
    {
        try
        {
            var result = DeserializeExistingPayload(output);

            var usecAdded = AddVanguardRole(result, UsecSourceRole, UsecVanguardRole);
            var bearAdded = AddVanguardRole(result, BearSourceRole, BearVanguardRole);

            if (!usecAdded || !bearAdded)
            {
                LogMissingSourceOnce(usecAdded, bearAdded, result);
            }

            return result;
        }
        catch (Exception exception)
        {
            if (!_loggedException)
            {
                _loggedException = true;
                _logger?.Error(VanguardServerDiagnosticsLog.Present($"[{StatusTag}] route=/singleplayer/settings/bot/difficulties exception={exception.GetType().Name}:{exception.Message}; action=return_original_or_empty; tag={StatusTag}"));
            }

            return DeserializeExistingPayload(output);
        }
    }

    private static Dictionary<string, Dictionary<string, DifficultyCategories>> DeserializeExistingPayload(string? output)
    {
        if (!string.IsNullOrWhiteSpace(output) && _jsonUtil != null)
        {
            var existing = _jsonUtil.Deserialize<Dictionary<string, Dictionary<string, DifficultyCategories>>>(output);
            if (existing != null)
            {
                return existing;
            }
        }

        return BuildFallbackPayloadFromDatabase();
    }

    private static Dictionary<string, Dictionary<string, DifficultyCategories>> BuildFallbackPayloadFromDatabase()
    {
        var result = new Dictionary<string, Dictionary<string, DifficultyCategories>>(StringComparer.OrdinalIgnoreCase);
        var tables = _databaseService?.GetTables();
        var botTypes = tables?.Bots?.Types;
        if (botTypes == null)
        {
            return result;
        }

        foreach (var role in new[] { UsecSourceRole, BearSourceRole })
        {
            if (TryGetDatabaseRole(botTypes, role, out var difficulties))
            {
                result[role] = CloneDifficultyMap(difficulties);
            }
        }

        return result;
    }

    private static bool AddVanguardRole(Dictionary<string, Dictionary<string, DifficultyCategories>> result, string sourceRole, string vanguardRole)
    {
        if (TryGetRole(result, vanguardRole, out _))
        {
            return true;
        }

        if (!TryGetRole(result, sourceRole, out var sourceDifficulties))
        {
            var tables = _databaseService?.GetTables();
            var botTypes = tables?.Bots?.Types;
            if (botTypes == null || !TryGetDatabaseRole(botTypes, sourceRole, out sourceDifficulties))
            {
                return false;
            }
        }

        result[vanguardRole] = NormalizeDifficultyMap(sourceDifficulties);
        return true;
    }

    private static bool TryGetRole(
        Dictionary<string, Dictionary<string, DifficultyCategories>> result,
        string role,
        out Dictionary<string, DifficultyCategories> difficulties)
    {
        if (result.TryGetValue(role, out difficulties!))
        {
            return true;
        }

        foreach (var pair in result)
        {
            if (string.Equals(pair.Key, role, StringComparison.OrdinalIgnoreCase))
            {
                difficulties = pair.Value;
                return true;
            }
        }

        difficulties = null!;
        return false;
    }

    private static bool TryGetDatabaseRole(
        Dictionary<string, BotType?> botTypes,
        string role,
        out Dictionary<string, DifficultyCategories> difficulties)
    {
        if (botTypes.TryGetValue(role, out var botType) && botType?.BotDifficulty != null)
        {
            difficulties = CloneDifficultyMap(botType.BotDifficulty);
            return true;
        }

        foreach (var pair in botTypes)
        {
            if (string.Equals(pair.Key, role, StringComparison.OrdinalIgnoreCase) && pair.Value?.BotDifficulty != null)
            {
                difficulties = CloneDifficultyMap(pair.Value.BotDifficulty);
                return true;
            }
        }

        difficulties = null!;
        return false;
    }

    private static Dictionary<string, DifficultyCategories> NormalizeDifficultyMap(Dictionary<string, DifficultyCategories> source)
    {
        var normalized = CloneDifficultyMap(source);

        if (!TryGetDifficulty(normalized, "normal", out var normal))
        {
            normal = normalized.Values.FirstOrDefault();
        }

        if (normal == null)
        {
            return normalized;
        }

        EnsureDifficulty(normalized, "easy", normal);
        EnsureDifficulty(normalized, "normal", normal);
        EnsureDifficulty(normalized, "hard", normal);
        EnsureDifficulty(normalized, "impossible", normal);
        return normalized;
    }

    private static Dictionary<string, DifficultyCategories> CloneDifficultyMap(Dictionary<string, DifficultyCategories> source)
    {
        var clone = new Dictionary<string, DifficultyCategories>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            clone[pair.Key.ToLowerInvariant()] = pair.Value;
        }

        return clone;
    }

    private static bool TryGetDifficulty(Dictionary<string, DifficultyCategories> difficulties, string key, out DifficultyCategories? value)
    {
        if (difficulties.TryGetValue(key, out value))
        {
            return true;
        }

        foreach (var pair in difficulties)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static void EnsureDifficulty(Dictionary<string, DifficultyCategories> difficulties, string key, DifficultyCategories fallback)
    {
        if (!TryGetDifficulty(difficulties, key, out _))
        {
            difficulties[key] = fallback;
        }
    }

    private static void LogMissingSourceOnce(bool usecAdded, bool bearAdded, Dictionary<string, Dictionary<string, DifficultyCategories>> result)
    {
        if (_loggedMissingSource)
        {
            return;
        }

        _loggedMissingSource = true;
        _logger?.Warning(VanguardServerDiagnosticsLog.Present($"[{StatusTag}] route=/singleplayer/settings/bot/difficulties patched=partial; usecAdded={usecAdded}; bearAdded={bearAdded}; source={UsecSourceRole},{BearSourceRole}; totalRoles={result.Count}; risk=fika_missing_custom_difficulties; tag={StatusTag}"));
    }
}
