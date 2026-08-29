using System.Text.Json;
using SPTarkov.DI.Annotations;
using Vanguard.Server.Operators.Models;

// Responsibility: Coordinates Deployment Limit Service for the Operator domain services, delegating specialized work to its collaborators.
// Flow: Caller/route input is validated and normalized, canonical Operator/profile state is read or updated through the owning store/integration, then a response and diagnostics are produced.
// Authority boundary: Server domain orchestration only; persistent truth remains explicit in the Operator/SPT stores and client in-raid execution remains separate.
// Invariant: Operations stay profile-scoped, deterministic/idempotent where required, and partial failures do not silently corrupt canonical state.
namespace Vanguard.Server.Operators.Services;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardDeploymentLimitService
{
    public Task<VanguardOperatorDeploymentLimits> GetLimitsAsync(string profileId)
    {
        return Task.FromResult(VanguardOperatorDeploymentLimits.FromPlayerLevel(ResolvePlayerLevel(profileId)));
    }

    private static int ResolvePlayerLevel(string profileId)
    {
        return ResolvePlayerLevelFromDisk(profileId) ?? 1;
    }

    private static int? ResolvePlayerLevelFromDisk(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        string[] candidatePaths =
        [
            Path.Combine(AppContext.BaseDirectory, "user", "profiles", $"{profileId}.json"),
            Path.Combine(AppContext.BaseDirectory, "SPT_Data", "Server", "database", "profiles", $"{profileId}.json"),
        ];

        foreach (var profilePath in candidatePaths)
        {
            var level = TryReadLevel(profilePath);
            if (level is > 0)
            {
                return level;
            }
        }

        return null;
    }

    private static int? TryReadLevel(string profilePath)
    {
        if (!File.Exists(profilePath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(profilePath));
            var root = document.RootElement;
            return TryReadInt(root, "characters", "pmc", "Info", "Level")
                ?? TryReadInt(root, "pmc", "Info", "Level")
                ?? TryReadInt(root, "Info", "Level");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? TryReadInt(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        if (current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        return null;
    }
}
