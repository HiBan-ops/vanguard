using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using Vanguard.Server.Diagnostics;

// Responsibility: Projects Vanguard custom USEC/BEAR Operator roles into the MoreBots/SPT bot database so the game can request valid bot templates for those roles.
// Flow: At server setup it clones/adapts the corresponding PMC source-role database entries into the custom Vanguard role keys and exposes diagnostics for missing dependencies or source data.
// Authority boundary: SPT/MoreBots remain owners of bot template generation; the bridge only supplies the custom-role database mapping required by Vanguard spawns.
// Invariant: USEC and BEAR mappings stay side-correct, source templates are not destructively modified, and missing integration data fails visibly without corrupting the base database.
namespace Vanguard.Server.Integrations.MoreBots;

/// <summary>
/// Vanguard server database fallback for Vanguard custom Operator bot roles.
///
/// The custom WildSpawnType enum lives client-side, but Fika/SPT/SAIN query server bot settings
/// during boot and raid cache hydration. Mirroring vanilla PMC bot type rows into the server DB under
/// Vanguard role keys makes standard SPT difficulty payloads and later dynamic routers boot-safe.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 12)]
public sealed class VanguardMoreBotsOperatorDatabaseBridge(
    DatabaseService databaseService,
    JsonUtil jsonUtil,
    ISptLogger<VanguardMoreBotsOperatorDatabaseBridge> logger)
    : IOnLoad
{
    public const string StatusTag = "VANGUARD_BOT_SETTINGS_FALLBACK_STATUS";

    private const string UsecSourceRole = "pmcusec";
    private const string BearSourceRole = "pmcbear";
    private const string UsecVanguardRole = "vanguardoperatorusec";
    private const string BearVanguardRole = "vanguardoperatorbear";

    public Task OnLoad()
    {
        try
        {
            var tables = databaseService.GetTables();
            var botTypes = tables?.Bots?.Types;
            if (botTypes == null)
            {
                logger.Warning(VanguardServerDiagnosticsLog.Present($"[{StatusTag}] database_bot_types_missing=true; action=skip; risk=custom_role_difficulty_missing; tag={StatusTag}"));
                return Task.CompletedTask;
            }

            EnsureMirror(botTypes, UsecSourceRole, UsecVanguardRole);
            EnsureMirror(botTypes, BearSourceRole, BearVanguardRole);

        }
        catch (Exception exception)
        {
            logger.Error(VanguardServerDiagnosticsLog.Present($"[{StatusTag}] server_bot_type_mirror_failed exception={exception.GetType().Name}:{exception.Message}; tag={StatusTag}"));
        }

        return Task.CompletedTask;
    }

    private string EnsureMirror(Dictionary<string, BotType?> botTypes, string sourceRole, string vanguardRole)
    {
        if (TryGetRole(botTypes, vanguardRole, out var existing) && existing != null)
        {
            return "already_present";
        }

        if (!TryGetRole(botTypes, sourceRole, out var source) || source == null)
        {
            return "missing_source";
        }

        botTypes[vanguardRole] = CloneBotType(source);
        return "mirrored";
    }

    private bool TryGetRole(Dictionary<string, BotType?> botTypes, string role, out BotType? botType)
    {
        if (botTypes.TryGetValue(role, out botType))
        {
            return true;
        }

        foreach (var pair in botTypes)
        {
            if (string.Equals(pair.Key, role, StringComparison.OrdinalIgnoreCase))
            {
                botType = pair.Value;
                return true;
            }
        }

        botType = null;
        return false;
    }

    private BotType CloneBotType(BotType source)
    {
        var serialized = jsonUtil.Serialize(source, indented: false)
            ?? throw new InvalidOperationException("Unable to serialize source BotType for Vanguard role mirror.");

        var clone = jsonUtil.Deserialize<BotType>(serialized)
            ?? throw new InvalidOperationException("Unable to deserialize mirrored BotType for Vanguard role mirror.");

        return clone;
    }
}
