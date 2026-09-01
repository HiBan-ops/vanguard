#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using Vanguard.Client.Api;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Integrations.MoreBots;
using UnityEngine;

using Vanguard.Client;

// Responsibility: Resolves which runtime bots correspond to the persistent Operators that should occupy HUD slots on the current client.
// Flow: Persistent manifest identity and live bot/profile/Fika evidence are correlated, duplicate/ambiguous matches are rejected, and stable candidates are returned to the HUD projection/binding layer.
// Authority boundary: HUD resolution is observational; it does not bind gameplay authority or change bot identity.
// Invariant: One live bot maps to at most one Operator slot, stale/disconnected candidates disappear, and uncertain identity is shown as unresolved rather than guessed.
namespace Vanguard.Client.Raid.Hud;

/// <summary>
/// Resolves visible Vanguard Operators on every client. The resolver deliberately matches
/// Vanguard identities before any player/PMC rejection so Fika observer clients do not discard
/// replicated Operator actors too early.
/// </summary>
internal sealed class VanguardRaidOperatorHudCandidateResolver
{
    private const float ObserverManifestRefreshSeconds = 8f;
    private const float ResolverSummarySeconds = 12f;

    private readonly VanguardApiClient apiClient = new();
    private readonly Dictionary<string, VanguardRaidOperatorSnapshotDto> snapshotsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VanguardRaidOperatorSnapshotDto> snapshotsByCompactName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VanguardRaidOperatorSnapshotDto> snapshotsByProfileId = new(StringComparer.OrdinalIgnoreCase);
    private float nextObserverManifestRefreshTime;
    private float nextResolverSummaryTime;
    private string lastObserverManifestRequestSignature = string.Empty;
    private string lastObserverManifestResultSignature = string.Empty;
    private string lastResolverSummarySignature = string.Empty;
    private string observerRaidSessionId = string.Empty;
    private Task<ObserverManifestLoadResult>? observerManifestTask;

    public IReadOnlyList<VanguardRaidOperatorHudIdentity> Resolve(Player localPlayer)
    {
        string localProfileId = localPlayer.ProfileId ?? string.Empty;
        var playerProfileIds = ResolvePlayerProfileIds(localPlayer);
        EnsureObserverManifest(localPlayer, playerProfileIds);

        var identities = new List<VanguardRaidOperatorHudIdentity>();
        int registeredCount = 0;
        int resolvedCount = 0;
        int playerRejectedCount = 0;
        int noManifestMatchCount = 0;
        int nonPmcNonOperatorCount = 0;
        int noProfileIdCount = 0;

        foreach (var player in EnumerateRegisteredPlayers())
        {
            registeredCount++;
            if (string.IsNullOrWhiteSpace(player.ProfileId))
            {
                noProfileIdCount++;
                continue;
            }

            // Critical ordering for Fika clients:
            // 1) first try to prove this actor is a Vanguard Operator from runtime/manifest;
            // 2) only then reject normal players or unrelated bots.
            // Player-looking/observed actors must not be rejected before manifest matching, because replicated
            // Operators on non-authority clients were never candidates for the HUD.
            if (TryResolveOperatorIdentity(player, localProfileId, playerProfileIds, out var identity))
            {
                resolvedCount++;
                identities.Add(identity);
                continue;
            }

            if (VanguardRaidOperatorHudVisibilityPolicy.ShouldRejectPlayerOrLocal(player, localProfileId, playerProfileIds))
            {
                playerRejectedCount++;
                continue;
            }

            if (!IsPmc(player))
            {
                nonPmcNonOperatorCount++;
                continue;
            }

            noManifestMatchCount++;
        }

        LogResolverSummary(
            registeredCount,
            resolvedCount,
            playerRejectedCount,
            noManifestMatchCount,
            nonPmcNonOperatorCount,
            noProfileIdCount,
            playerProfileIds.Count);

        return identities
            .GroupBy(identity => identity.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(identity => identity.Nickname, StringComparer.OrdinalIgnoreCase)
            .ThenBy(identity => identity.BotProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool TryResolveOperatorIdentity(
        IPlayer player,
        string localProfileId,
        ISet<string> playerProfileIds,
        out VanguardRaidOperatorHudIdentity identity)
    {
        string botProfileId = player.ProfileId ?? string.Empty;
        if (VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botProfileId, out var runtime))
        {
            if (VanguardRaidOperatorHudVisibilityPolicy.AllowsOperator(runtime.OwnerProfileId, localProfileId, playerProfileIds))
            {
                identity = new VanguardRaidOperatorHudIdentity(
                    botProfileId,
                    runtime.OperatorId,
                    runtime.OwnerProfileId,
                    botProfileId,
                    ResolveNickname(player, runtime.BotNickname, runtime.OperatorId),
                    "runtime_registry",
                    player);
                return true;
            }
        }

        if (TryResolveManifestSnapshot(player, out var snapshot)
            && VanguardRaidOperatorHudVisibilityPolicy.AllowsOperator(snapshot.OwnerProfileId ?? string.Empty, localProfileId, playerProfileIds))
        {
            string operatorId = Normalize(snapshot.OperatorId);
            identity = new VanguardRaidOperatorHudIdentity(
                string.IsNullOrWhiteSpace(botProfileId) ? operatorId : botProfileId,
                operatorId,
                Normalize(snapshot.OwnerProfileId),
                botProfileId,
                ResolveNickname(player, snapshot.Callsign, snapshot.DisplayName, operatorId),
                "observer_manifest",
                player);
            return true;
        }

        identity = null!;
        return false;
    }

    private bool TryResolveManifestSnapshot(IPlayer player, out VanguardRaidOperatorSnapshotDto snapshot)
    {
        string profileId = player.ProfileId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(profileId) && snapshotsByProfileId.TryGetValue(profileId, out snapshot!))
        {
            return true;
        }

        foreach (string alias in ResolvePlayerNameAliases(player))
        {
            if (snapshotsByName.TryGetValue(alias, out snapshot!))
            {
                return true;
            }

            string compactAlias = CompactKey(alias);
            if (!string.IsNullOrWhiteSpace(compactAlias) && snapshotsByCompactName.TryGetValue(compactAlias, out snapshot!))
            {
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    private void EnsureObserverManifest(Player localPlayer, ISet<string> playerProfileIds)
    {
        // The observer manifest is presentation-only data. SPT RequestHandler is synchronous, so
        // performing this read inside RaidHud.Tick can block the Unity main thread for an entire
        // HTTP round-trip. Mirror Vanguard's existing background-I/O pattern: do transport work
        // off-thread, then consume the completed immutable DTO on a later HUD tick.
        DrainObserverManifestTask();
        if (observerManifestTask is not null)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (now < nextObserverManifestRefreshTime)
        {
            return;
        }

        nextObserverManifestRefreshTime = now + ObserverManifestRefreshSeconds;

        var owners = playerProfileIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (owners.Length == 0)
        {
            string localProfileId = localPlayer.ProfileId ?? string.Empty;
            owners = string.IsNullOrWhiteSpace(localProfileId) ? Array.Empty<string>() : new[] { localProfileId };
        }

        if (owners.Length == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(observerRaidSessionId))
        {
            observerRaidSessionId = VanguardRaidOperatorRuntimeRegistry.ActiveRaidSessionId
                ?? "hud-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        string requestSignature = string.Join(";", owners) + "|" + observerRaidSessionId;
        bool signatureChanged = !string.Equals(requestSignature, lastObserverManifestRequestSignature, StringComparison.Ordinal);
        if (!signatureChanged && snapshotsByName.Count > 0)
        {
            return;
        }

        lastObserverManifestRequestSignature = requestSignature;
        string[] capturedOwners = owners;
        string capturedRaidSessionId = observerRaidSessionId;
        observerManifestTask = Task.Run(() => LoadObserverManifest(capturedOwners, capturedRaidSessionId));
    }

    private ObserverManifestLoadResult LoadObserverManifest(string[] owners, string raidSessionId)
    {
        try
        {
            return ObserverManifestLoadResult.Completed(apiClient.LoadRaidManifestForProfiles(owners, raidSessionId), owners.Length);
        }
        catch (Exception exception)
        {
            return ObserverManifestLoadResult.Failed(owners.Length, exception);
        }
    }

    private void DrainObserverManifestTask()
    {
        Task<ObserverManifestLoadResult>? task = observerManifestTask;
        if (task is null || !task.IsCompleted)
        {
            return;
        }

        observerManifestTask = null;
        ObserverManifestLoadResult result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            result = ObserverManifestLoadResult.Failed(0, exception);
        }

        if (result.ExceptionType is not null)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.OperatorHudStatusTag,
                $"manifest observer failed: {result.ExceptionType}: {result.ExceptionMessage}");
            return;
        }

        VanguardRaidOperatorManifestForProfilesResponseDto response = result.Response!;
        if (!response.Success)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.OperatorHudStatusTag,
                $"manifest observer skipped reason={response.Reason ?? "unknown"}; owners={result.OwnerCount}");
            return;
        }

        RebuildManifestIndex(response);
    }

    private sealed class ObserverManifestLoadResult
    {
        private ObserverManifestLoadResult(
            VanguardRaidOperatorManifestForProfilesResponseDto? response,
            int ownerCount,
            string? exceptionType,
            string? exceptionMessage)
        {
            Response = response;
            OwnerCount = ownerCount;
            ExceptionType = exceptionType;
            ExceptionMessage = exceptionMessage;
        }

        public VanguardRaidOperatorManifestForProfilesResponseDto? Response { get; }
        public int OwnerCount { get; }
        public string? ExceptionType { get; }
        public string? ExceptionMessage { get; }

        public static ObserverManifestLoadResult Completed(VanguardRaidOperatorManifestForProfilesResponseDto response, int ownerCount)
        {
            return new ObserverManifestLoadResult(response, ownerCount, null, null);
        }

        public static ObserverManifestLoadResult Failed(int ownerCount, Exception exception)
        {
            return new ObserverManifestLoadResult(null, ownerCount, exception.GetType().Name, exception.Message);
        }
    }

    private void RebuildManifestIndex(VanguardRaidOperatorManifestForProfilesResponseDto response)
    {
        snapshotsByName.Clear();
        snapshotsByCompactName.Clear();
        snapshotsByProfileId.Clear();

        foreach (var manifest in response.ManifestsByOwnerProfileId?.Values ?? Enumerable.Empty<VanguardRaidOperatorManifestResponseDto>())
        {
            foreach (var snapshot in manifest.Operators ?? new List<VanguardRaidOperatorSnapshotDto>())
            {
                AddSnapshotByName(snapshot, snapshot.Callsign);
                AddSnapshotByName(snapshot, snapshot.DisplayName);
                AddSnapshotByName(snapshot, snapshot.OperatorId);

                // The off-raid inventory profile id is the best stable profile hint available from
                // the server manifest. The live Fika actor can still expose a different replicated
                // profile id, so name aliases remain the primary observer-client fallback.
                AddSnapshotByProfileId(snapshot, snapshot.OperatorInventoryProfileId);
                AddSnapshotByProfileId(snapshot, snapshot.OperatorId);
            }
        }

        string resultSignature = $"owners={response.OwnerCount};operators={response.OperatorCount};names={snapshotsByName.Count};compact={snapshotsByCompactName.Count};profiles={snapshotsByProfileId.Count};raid={response.RaidSessionId ?? "<none>"}";
        if (!string.Equals(resultSignature, lastObserverManifestResultSignature, StringComparison.Ordinal))
        {
            lastObserverManifestResultSignature = resultSignature;
            VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OperatorHudStatusTag, "manifest observer loaded " + resultSignature + "; visibility=" + VanguardRaidOperatorHudVisibilityPolicy.CurrentMode);
        }
    }

    private void AddSnapshotByName(VanguardRaidOperatorSnapshotDto snapshot, string? value)
    {
        string key = Normalize(value);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!snapshotsByName.ContainsKey(key))
        {
            snapshotsByName[key] = snapshot;
        }

        string compactKey = CompactKey(key);
        if (!string.IsNullOrWhiteSpace(compactKey) && !snapshotsByCompactName.ContainsKey(compactKey))
        {
            snapshotsByCompactName[compactKey] = snapshot;
        }
    }

    private void AddSnapshotByProfileId(VanguardRaidOperatorSnapshotDto snapshot, string? value)
    {
        string key = Normalize(value);
        if (!string.IsNullOrWhiteSpace(key) && !snapshotsByProfileId.ContainsKey(key))
        {
            snapshotsByProfileId[key] = snapshot;
        }
    }

    private static HashSet<string> ResolvePlayerProfileIds(Player localPlayer)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string localProfileId = localPlayer.ProfileId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(localProfileId))
        {
            ids.Add(localProfileId);
        }

        foreach (string profileId in VanguardFikaCompat.GetFikaPlayerProfileIds(message => VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OperatorHudStatusTag, message)))
        {
            if (!string.IsNullOrWhiteSpace(profileId))
            {
                ids.Add(profileId.Trim());
            }
        }

        return ids;
    }

    private static IEnumerable<IPlayer> EnumerateRegisteredPlayers()
    {
        GameWorld? gameWorld;
        try
        {
            gameWorld = Singleton<GameWorld>.Instance;
        }
        catch
        {
            yield break;
        }

        if (gameWorld?.RegisteredPlayers is null)
        {
            yield break;
        }

        foreach (var player in gameWorld.RegisteredPlayers)
        {
            if (player is IPlayer eftPlayer)
            {
                yield return eftPlayer;
            }
        }
    }

    private static bool IsPmc(IPlayer player)
    {
        string role = player.Profile?.Info?.Settings?.Role.ToString() ?? string.Empty;
        return VanguardOperatorBotTypes.IsVanguardOperatorRoleName(role)
               || string.Equals(role, "pmcUSEC", StringComparison.OrdinalIgnoreCase)
               || string.Equals(role, "pmcBEAR", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ResolvePlayerNameAliases(IPlayer player)
    {
        var aliases = new List<string?>
        {
            player.Profile?.Info?.Nickname,
            VanguardRaidHudReflection.ReadNestedString(player.Profile, "Info", "Nickname"),
            VanguardRaidHudReflection.ReadNestedString(player.Profile, "Info", "LowerNickname"),
            VanguardRaidHudReflection.ReadNestedString(player.Profile, "Info", "MainProfileNickname"),
            VanguardRaidHudReflection.ReadNestedString(player.Profile, "Nickname"),
            VanguardRaidHudReflection.ReadNestedString(player, "Nickname"),
            VanguardRaidHudReflection.ReadNestedString(player, "Player", "Profile", "Info", "Nickname"),
            VanguardRaidHudReflection.ReadNestedString(player, "ObservedPlayer", "Profile", "Info", "Nickname"),
            player.ProfileId,
        };

        return aliases
            .Select(alias => Normalize(alias))
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveNickname(IPlayer player, params string?[] fallback)
    {
        var values = new List<string?>
        {
            player.Profile?.Info?.Nickname,
            VanguardRaidHudReflection.ReadNestedString(player.Profile, "Info", "Nickname"),
            VanguardRaidHudReflection.ReadNestedString(player.Profile, "Nickname"),
            VanguardRaidHudReflection.ReadNestedString(player, "Nickname"),
        };
        values.AddRange(fallback);
        values.Add("Operator");
        return Normalize(values.ToArray());
    }

    private static string Normalize(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string CompactKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (char c in value.Trim())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private void LogResolverSummary(
        int registeredCount,
        int resolvedCount,
        int playerRejectedCount,
        int noManifestMatchCount,
        int nonPmcNonOperatorCount,
        int noProfileIdCount,
        int playerProfileIdCount)
    {
        float now = Time.realtimeSinceStartup;
        if (now < nextResolverSummaryTime)
        {
            return;
        }

        nextResolverSummaryTime = now + ResolverSummarySeconds;
        string signature = $"registered={registeredCount};resolved={resolvedCount};playersRejected={playerRejectedCount};noManifest={noManifestMatchCount};nonPmc={nonPmcNonOperatorCount};noProfile={noProfileIdCount};manifestNames={snapshotsByName.Count};compact={snapshotsByCompactName.Count};manifestProfiles={snapshotsByProfileId.Count};playerIds={playerProfileIdCount};visibility={VanguardRaidOperatorHudVisibilityPolicy.CurrentMode}";
        if (string.Equals(signature, lastResolverSummarySignature, StringComparison.Ordinal))
        {
            return;
        }

        lastResolverSummarySignature = signature;
        VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OperatorHudStatusTag, "resolver " + signature);
    }
}
#else
namespace Vanguard.Client.Raid.Hud;

internal sealed class VanguardRaidOperatorHudCandidateResolver
{
}
#endif
