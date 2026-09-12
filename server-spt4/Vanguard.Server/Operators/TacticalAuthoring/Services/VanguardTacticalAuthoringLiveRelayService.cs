using SPTarkov.DI.Annotations;
using Vanguard.Server.Operators.TacticalAuthoring.Requests;
using Vanguard.Server.Operators.TacticalAuthoring.Responses;

// Responsibility: Coordinates Tactical Authoring Live Relay Service for the server tactical-authoring relay, delegating specialized work to its collaborators.
// Flow: Caller/route input is validated and normalized, canonical Operator/profile state is read or updated through the owning store/integration, then a response and diagnostics are produced.
// Authority boundary: Server domain orchestration only; persistent truth remains explicit in the Operator/SPT stores and client in-raid execution remains separate.
// Invariant: Operations stay profile-scoped, deterministic/idempotent where required, and partial failures do not silently corrupt canonical state.
namespace Vanguard.Server.Operators.TacticalAuthoring.Services;

/// <summary>
/// Transient in-memory relay for Vanguard authoring preview. It is deliberately not persistence:
/// saved tactical authoring JSON remains authoring-only and runtime consumption stays disabled.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class VanguardTacticalAuthoringLiveRelayService
{
    private static readonly TimeSpan AuthorTtl = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ResultTtl = TimeSpan.FromSeconds(6);
    private readonly object sync = new();
    private readonly Dictionary<string, VanguardTacticalAuthoringLiveAuthorSnapshot> authors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VanguardTacticalAuthoringLiveHeadlessResult> results = new(StringComparer.OrdinalIgnoreCase);

    public VanguardTacticalAuthoringLiveExchangeResponse Exchange(VanguardTacticalAuthoringLiveExchangeRequest? request)
    {
        request ??= new VanguardTacticalAuthoringLiveExchangeRequest();
        var now = DateTimeOffset.UtcNow;
        var role = Normalize(request.Role);
        lock (sync)
        {
            Prune(now);
            if (string.Equals(role, "author", StringComparison.OrdinalIgnoreCase))
            {
                ApplyAuthor(request.Author, now);
                var owner = Normalize(request.Author?.OwnerProfileId);
                return new VanguardTacticalAuthoringLiveExchangeResponse
                {
                    Success = true,
                    Reason = "author_exchange_ok",
                    Authors = owner.Length > 0 && authors.TryGetValue(owner, out var author) ? [author] : [],
                    HeadlessResults = owner.Length > 0 && results.TryGetValue(owner, out var result) ? [result] : [],
                    ServerTimeUtc = now
                };
            }

            if (string.Equals(role, "headless", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var result in request.HeadlessResults ?? [])
                {
                    ApplyResult(result, now);
                }

                var knownOwners = new HashSet<string>(
                    (request.KnownOwnerProfileIds ?? []).Select(Normalize).Where(value => value.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
                return new VanguardTacticalAuthoringLiveExchangeResponse
                {
                    Success = true,
                    Reason = "headless_exchange_ok",
                    Authors = authors.Values
                        .Where(author => author.Active && (knownOwners.Count == 0 || knownOwners.Contains(author.OwnerProfileId)))
                        .OrderBy(author => author.OwnerProfileId, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    HeadlessResults = [],
                    ServerTimeUtc = now
                };
            }

            if (string.Equals(role, "authority", StringComparison.OrdinalIgnoreCase))
            {
                // A direct runtime authority (SPT local or Fika player-host) is both an optional
                // author and the consumer of the complete active-author set for this raid.
                // Persisted tactical-authoring state is not touched here: this remains a transient relay.
                ApplyAuthor(request.Author, now);
                foreach (var result in request.HeadlessResults ?? [])
                {
                    ApplyResult(result, now);
                }

                var knownOwners = new HashSet<string>(
                    (request.KnownOwnerProfileIds ?? []).Select(Normalize).Where(value => value.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
                var localOwner = Normalize(request.Author?.OwnerProfileId);
                if (localOwner.Length > 0)
                {
                    knownOwners.Add(localOwner);
                }

                return new VanguardTacticalAuthoringLiveExchangeResponse
                {
                    Success = true,
                    Reason = "authority_exchange_ok",
                    Authors = authors.Values
                        .Where(author => author.Active && (knownOwners.Count == 0 || knownOwners.Contains(author.OwnerProfileId)))
                        .OrderBy(author => author.OwnerProfileId, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    HeadlessResults = [],
                    ServerTimeUtc = now
                };
            }

            return new VanguardTacticalAuthoringLiveExchangeResponse
            {
                Success = false,
                Reason = "unsupported_role",
                ServerTimeUtc = now
            };
        }
    }

    private void ApplyAuthor(VanguardTacticalAuthoringLiveAuthorSnapshot? incoming, DateTimeOffset now)
    {
        if (incoming == null)
        {
            return;
        }

        var owner = Normalize(incoming.OwnerProfileId);
        var session = Normalize(incoming.LiveSessionId);
        var map = Normalize(incoming.MapId);
        if (owner.Length == 0 || session.Length == 0 || map.Length == 0)
        {
            return;
        }

        if (!incoming.Active)
        {
            authors.Remove(owner);
            results.Remove(owner);
            return;
        }

        var mapJson = incoming.MapJson ?? string.Empty;
        if (mapJson.Length == 0 && authors.TryGetValue(owner, out var previous)
            && string.Equals(previous.LiveSessionId, session, StringComparison.Ordinal)
            && string.Equals(previous.MapId, map, StringComparison.OrdinalIgnoreCase))
        {
            mapJson = previous.MapJson;
        }

        authors[owner] = incoming with
        {
            OwnerProfileId = owner,
            LiveSessionId = session,
            MapId = map,
            SelectedZoneId = Normalize(incoming.SelectedZoneId),
            MapJson = mapJson,
            UpdatedAtUtc = now
        };
    }

    private void ApplyResult(VanguardTacticalAuthoringLiveHeadlessResult? incoming, DateTimeOffset now)
    {
        if (incoming == null)
        {
            return;
        }

        var owner = Normalize(incoming.OwnerProfileId);
        if (owner.Length == 0 || !authors.TryGetValue(owner, out var author))
        {
            return;
        }

        if (!string.Equals(author.LiveSessionId, Normalize(incoming.LiveSessionId), StringComparison.Ordinal)
            || !string.Equals(author.MapId, Normalize(incoming.MapId), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        results[owner] = incoming with
        {
            OwnerProfileId = owner,
            LiveSessionId = author.LiveSessionId,
            MapId = author.MapId,
            UpdatedAtUtc = now
        };
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var owner in authors.Where(pair => now - pair.Value.UpdatedAtUtc > AuthorTtl).Select(pair => pair.Key).ToArray())
        {
            authors.Remove(owner);
            results.Remove(owner);
        }

        foreach (var owner in results.Where(pair => now - pair.Value.UpdatedAtUtc > ResultTtl).Select(pair => pair.Key).ToArray())
        {
            results.Remove(owner);
        }
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
