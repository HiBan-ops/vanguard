using SPTarkov.DI.Annotations;
using Vanguard.Server.Operators.LootInterests.Requests;
using Vanguard.Server.Operators.LootInterests.Responses;

// Responsibility: Coordinates Owner Loot Interest Service for the owner loot-interest API, delegating specialized work to its collaborators.
// Flow: Caller/route input is validated and normalized, canonical Operator/profile state is read or updated through the owning store/integration, then a response and diagnostics are produced.
// Authority boundary: Server domain orchestration only; persistent truth remains explicit in the Operator/SPT stores and client in-raid execution remains separate.
// Invariant: Operations stay profile-scoped, deterministic/idempotent where required, and partial failures do not silently corrupt canonical state.
namespace Vanguard.Server.Operators.LootInterests.Services;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardOwnerLootInterestService
{
    private readonly object sync = new();
    private readonly Dictionary<string, VanguardOwnerLootInterestResponse> byOwner = new(StringComparer.OrdinalIgnoreCase);

    public VanguardOwnerLootInterestResponse Set(string sessionId, VanguardOwnerLootInterestSetRequest request)
    {
        string owner = Normalize(request.OwnerProfileId, sessionId);
        if (owner.Length == 0)
        {
            return Failure("owner_profile_missing");
        }

        var entries = (request.Entries ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.TemplateId))
            .GroupBy(entry => entry.TemplateId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new VanguardOwnerLootInterestEntry
            {
                TemplateId = group.Key,
                Group = NormalizeGroup(group.Last().Group)
            })
            .OrderBy(entry => entry.TemplateId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (sync)
        {
            byOwner.TryGetValue(owner, out VanguardOwnerLootInterestResponse? current);
            string contentHash = Normalize(request.ContentHash, "none");
            if (current != null && string.Equals(current.ContentHash, contentHash, StringComparison.Ordinal))
            {
                return Clone(current, "owner_loot_interest_unchanged", success: true);
            }

            // The server owns the monotone revision. Client-side counters are intentionally raid/process-local
            // and must never make a fresh snapshot look stale after a raid or client restart. The sync service
            // serializes SET requests, so content change + server revision is sufficient and deterministic.
            long serverRevision = (current?.Revision ?? 0) + 1;
            var next = new VanguardOwnerLootInterestResponse
            {
                Success = true,
                Reason = "owner_loot_interest_set",
                OwnerProfileId = owner,
                Revision = serverRevision,
                ContentHash = contentHash,
                Source = Normalize(request.Source, "client_wishlist_snapshot"),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                BuildLabel = VanguardBuildVersion.BuildLabel,
                Entries = entries
            };
            byOwner[owner] = next;
            return Clone(next, next.Reason, success: true);
        }
    }

    public VanguardOwnerLootInterestResponse Get(string sessionId, VanguardOwnerLootInterestGetRequest request)
    {
        string owner = Normalize(request.OwnerProfileId, sessionId);
        if (owner.Length == 0)
        {
            return Failure("owner_profile_missing");
        }

        lock (sync)
        {
            return byOwner.TryGetValue(owner, out VanguardOwnerLootInterestResponse? current)
                ? Clone(current, "owner_loot_interest_get", success: true)
                : new VanguardOwnerLootInterestResponse
                {
                    Success = true,
                    Reason = "owner_loot_interest_not_published",
                    OwnerProfileId = owner,
                    Revision = 0,
                    ContentHash = "none",
                    Source = "server_no_interest_snapshot",
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    BuildLabel = VanguardBuildVersion.BuildLabel,
                    Entries = []
                };
        }
    }

    private static VanguardOwnerLootInterestResponse Clone(VanguardOwnerLootInterestResponse source, string reason, bool success)
    {
        return new VanguardOwnerLootInterestResponse
        {
            Success = success,
            Reason = reason,
            OwnerProfileId = source.OwnerProfileId,
            Revision = source.Revision,
            ContentHash = source.ContentHash,
            Source = source.Source,
            UpdatedAtUtc = source.UpdatedAtUtc,
            BuildLabel = VanguardBuildVersion.BuildLabel,
            Entries = source.Entries.Select(entry => new VanguardOwnerLootInterestEntry { TemplateId = entry.TemplateId, Group = entry.Group }).ToList()
        };
    }

    private static VanguardOwnerLootInterestResponse Failure(string reason) => new() { Success = false, Reason = reason, BuildLabel = VanguardBuildVersion.BuildLabel };

    private static string Normalize(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return string.Empty;
    }

    private static string NormalizeGroup(string? value)
    {
        string group = Normalize(value, "Other");
        return group is "Quests" or "Hideout" or "Trading" or "Equipment" or "Other" ? group : "Other";
    }
}
