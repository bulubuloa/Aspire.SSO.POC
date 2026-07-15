using System.Collections.Concurrent;
using Microsoft.IdentityModel.Tokens;

namespace Aspire.Sso;

// Fetches a registered client's PUBLIC keys from their JWKS URL and caches them by kid.
// This is the real integration: Aspire never holds the client's private key, and picks up
// key rotation by re-fetching when it sees an unknown kid.
public sealed class JwksCache
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<JwksCache> _log;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(); // jwksUrl -> keys

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RefetchFloor = TimeSpan.FromSeconds(30); // anti-hammer

    private sealed record CacheEntry(IReadOnlyList<JsonWebKey> Keys, DateTimeOffset FetchedUtc);

    public JwksCache(IHttpClientFactory http, ILogger<JwksCache> log) => (_http, _log) = (http, log);

    // Resolve the signing key for a kid, re-fetching once if the kid is unknown (key rotation).
    public async Task<SecurityKey?> GetKeyAsync(string jwksUrl, string kid)
    {
        var entry = _cache.GetValueOrDefault(jwksUrl);
        var fresh = entry is not null && DateTimeOffset.UtcNow - entry.FetchedUtc < Ttl;

        if (entry is not null && fresh)
        {
            var hit = Find(entry.Keys, kid);
            if (hit is not null) return hit;
            // Unknown kid on a fresh cache -> the client may have rotated. Re-fetch, but not too often.
            if (DateTimeOffset.UtcNow - entry.FetchedUtc < RefetchFloor) return null;
        }

        var fetched = await FetchAsync(jwksUrl);
        if (fetched is null) return entry is not null ? Find(entry.Keys, kid) : null;  // fall back to stale

        _cache[jwksUrl] = new CacheEntry(fetched, DateTimeOffset.UtcNow);
        return Find(fetched, kid);
    }

    private static SecurityKey? Find(IReadOnlyList<JsonWebKey> keys, string kid) =>
        keys.FirstOrDefault(k => k.Kid == kid);

    // Drop the cached keys for a URL so the next lookup re-fetches.
    // Used when a signature fails: the client may have rotated the key material behind the
    // same kid, which a TTL alone would not catch until it expired.
    public void Invalidate(string jwksUrl)
    {
        if (_cache.TryRemove(jwksUrl, out _))
            _log.LogInformation("Invalidated JWKS cache for {Url}", jwksUrl);
    }

    private async Task<IReadOnlyList<JsonWebKey>?> FetchAsync(string jwksUrl)
    {
        try
        {
            var json = await _http.CreateClient().GetStringAsync(jwksUrl);
            var set = new JsonWebKeySet(json);
            _log.LogInformation("Fetched {Count} key(s) from {Url}", set.Keys.Count, jwksUrl);
            return set.Keys.ToList();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not fetch JWKS from {Url}", jwksUrl);
            return null;
        }
    }
}
