using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;

namespace Aspire.Sso;

// ASPIRE side: validate a token the CLIENT signed. We hold no private key and no user directory —
// the token itself is the only source of user identity, so every claim is checked.
public sealed class JwtValidator
{
    private readonly JwksCache _jwks;
    private readonly AspireOptions _o;

    // Ceiling on how old an SSO token may be, regardless of its own `exp`. The guide caps
    // the lifetime at 60-120s; this stops a client quietly issuing long-lived ones.
    private static readonly TimeSpan _maxAge = TimeSpan.FromMinutes(5);

    public JwtValidator(JwksCache jwks, AspireOptions options) => (_jwks, _o) = (jwks, options);

    public record Result(bool Ok, string? Error, ValidatedUser? User, string? Jti,
        DateTimeOffset Exp, string? Target);

    public record ValidatedUser(string Sub, string Email, string DisplayName,
        string Country, string Program);

    public async Task<Result> ValidateAsync(string token, AspireOptions.RegisteredClient client)
    {
        var r = await TryValidateAsync(token, client);

        // A signature failure can mean the client rotated key material behind the same kid,
        // leaving our cache stale. Re-fetch once and retry before calling it invalid.
        if (!r.Ok && r.Error == "Invalid signature")
        {
            _jwks.Invalidate(client.JwksUrl);
            r = await TryValidateAsync(token, client);
        }
        return r;
    }

    private async Task<Result> TryValidateAsync(string token, AspireOptions.RegisteredClient client)
    {
        var handler = new JwtSecurityTokenHandler();

        // Read the header first so we can resolve the right key by kid.
        JwtSecurityToken unverified;
        try { unverified = handler.ReadJwtToken(token); }
        catch { return Fail("Malformed token"); }

        var kid = unverified.Header.Kid;                    // kid — which of the client's keys signed this
        if (string.IsNullOrEmpty(kid)) return Fail("Missing kid in token header");

        var key = await _jwks.GetKeyAsync(client.JwksUrl, kid);
        if (key is null) return Fail($"No key '{kid}' in the client's JWKS");

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = client.Issuer,        // iss — the token really came from this client
            ValidateAudience = true,
            ValidAudience = _o.Audience,        // aud — it was meant for us, not another environment
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,             // the client's PUBLIC key, fetched from their JWKS
            ValidateLifetime = true,            // exp / nbf
            ClockSkew = TimeSpan.FromSeconds(_o.ClockSkewSeconds),
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },  // pinned: no alg:none, no HS256 downgrade
        };

        try
        {
            handler.ValidateToken(token, parameters, out var validated);
            var jwt = (JwtSecurityToken)validated;

            string? Claim(string t) => jwt.Claims.FirstOrDefault(c => c.Type == t)?.Value;

            var sub = Claim("sub");                 // subject — the customer this token is about
            var jti = Claim("jti");                 // JWT id — the caller consumes it once, see SessionStore
            if (string.IsNullOrEmpty(sub)) return Fail("Missing mandatory claim: sub");
            if (string.IsNullOrEmpty(jti)) return Fail("Missing mandatory claim: jti");

            var email = Claim("email");
            var given = Claim("given_name");
            var family = Claim("family_name");
            foreach (var (name, value) in new[] { ("email", email), ("given_name", given), ("family_name", family) })
                if (string.IsNullOrEmpty(value)) return Fail($"Missing mandatory claim: {name}");

            // The guide lists `iat` as mandatory and says Aspire validates issued-at time.
            // Without this the claim was accepted-but-ignored, so the contract was a promise
            // we did not keep. A token issued far in the past is rejected even if `exp` is valid.
            var iatRaw = Claim("iat");
            if (string.IsNullOrEmpty(iatRaw)) return Fail("Missing mandatory claim: iat");
            if (!long.TryParse(iatRaw, out var iatUnix)) return Fail("Invalid claim: iat");
            var iat = DateTimeOffset.FromUnixTimeSeconds(iatUnix);
            var skew = TimeSpan.FromSeconds(_o.ClockSkewSeconds);
            if (iat > DateTimeOffset.UtcNow + skew) return Fail("Token issued in the future");
            if (iat < DateTimeOffset.UtcNow - _maxAge - skew) return Fail("Token issued too long ago");

            var user = new ValidatedUser(sub, email!, $"{given} {family}",
                Claim("country") ?? "", Claim("program") ?? "");

            return new Result(true, null, user, jti, jwt.ValidTo, Claim("target"));
        }
        catch (SecurityTokenExpiredException) { return Fail("Token expired"); }
        catch (SecurityTokenInvalidAudienceException) { return Fail("Audience mismatch"); }
        catch (SecurityTokenInvalidIssuerException) { return Fail("Issuer mismatch"); }
        catch (SecurityTokenInvalidSignatureException) { return Fail("Invalid signature"); }
        catch (Exception ex) { return Fail($"Invalid token: {ex.GetType().Name}"); }
    }

    private static Result Fail(string error) => new(false, error, null, null, default, null);
}
