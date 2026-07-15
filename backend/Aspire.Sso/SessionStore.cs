using System.Collections.Concurrent;

namespace Aspire.Sso;

// Aspire's own state: sessions, replay protection and one-time launch tickets.
// In-memory for the demo; a real deployment needs a distributed cache so replay protection
// and sessions work across instances.
public sealed class SessionStore
{
    private readonly int _ticketSeconds;
    public SessionStore(AspireOptions options) => _ticketSeconds = options.LaunchTicketSeconds;

    private readonly ConcurrentDictionary<string, AspireSession> _sessions = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _usedJti = new();     // jti -> expiry
    private readonly ConcurrentDictionary<string, DateTimeOffset> _usedSaml = new();    // assertion id -> expiry
    private readonly ConcurrentDictionary<string, LaunchTicket> _tickets = new();       // one-time browser bootstrap

    public record AspireSession(string Id, string Sub, string Email, string DisplayName,
        string Country, string Program, string Via, DateTimeOffset CreatedUtc, string? Target = null,
        string? ClientId = null);

    public record LaunchTicket(string Code, string SessionId, DateTimeOffset ExpiresUtc);


    // --- Replay protection (jti) ---
    // Returns false if the jti was already seen (replay).
    public bool TryConsumeJti(string jti, DateTimeOffset expiryUtc)
    {
        Sweep();
        return _usedJti.TryAdd(jti, expiryUtc);
    }

    // --- Replay protection (SAML assertion id) — same idea, different protocol ---
    public bool TryConsumeSamlId(string assertionId, DateTimeOffset expiryUtc)
    {
        Sweep();
        return _usedSaml.TryAdd(assertionId, expiryUtc);
    }

    // --- Sessions ---
    // SAML path: identity arrives as assertion attributes rather than JWT claims.
    public AspireSession CreateSession(SamlValidator.SamlSubject u, string via, string? clientId = null) =>
        CreateSession(new JwtValidator.ValidatedUser(u.Sub, u.Email, u.DisplayName, u.Country, u.Program),
                      via, u.Target, clientId);

    public AspireSession CreateSession(JwtValidator.ValidatedUser user, string via, string? target = null, string? clientId = null)
    {
        var s = new AspireSession(
            Guid.NewGuid().ToString("N"),
            user.Sub, user.Email, user.DisplayName,
            user.Country, user.Program, via, DateTimeOffset.UtcNow, target, clientId);
        _sessions[s.Id] = s;
        return s;
    }


    public AspireSession? GetSession(string? id) =>
        id is not null && _sessions.TryGetValue(id, out var s) ? s : null;

    public void EndSession(string? id)
    {
        if (id is not null) _sessions.TryRemove(id, out _);
    }

    // --- One-time launch ticket (keeps the JWT/SAML out of the browser URL for mobile) ---
    public LaunchTicket IssueTicket(string sessionId)
    {
        var t = new LaunchTicket(Guid.NewGuid().ToString("N"),
            sessionId, DateTimeOffset.UtcNow.AddSeconds(_ticketSeconds));
        _tickets[t.Code] = t;
        return t;
    }

    public string? RedeemTicket(string? code)
    {
        Sweep();
        if (code is null || !_tickets.TryRemove(code, out var t)) return null;
        return t.ExpiresUtc >= DateTimeOffset.UtcNow ? t.SessionId : null;
    }

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _usedJti) if (kv.Value < now) _usedJti.TryRemove(kv.Key, out _);
        foreach (var kv in _usedSaml) if (kv.Value < now) _usedSaml.TryRemove(kv.Key, out _);
        foreach (var kv in _tickets) if (kv.Value.ExpiresUtc < now) _tickets.TryRemove(kv.Key, out _);
    }
}
