using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace Client.Demo;

// Mints the short-lived signed JWT that Aspire will validate.
// This runs on the CLIENT's server — never on the device.
public sealed class JwtIssuer
{
    private readonly ClientKeys _keys;
    private readonly ClientOptions _o;

    public JwtIssuer(ClientKeys keys, ClientOptions options) => (_keys, _o) = (keys, options);

    // `scenario` produces deliberately-invalid tokens for negative testing. Demo only — remove for production.
    public string Issue(ClientUser user, string? scenario = null, string? target = null)
    {
        var now = DateTimeOffset.UtcNow;
        // For "expired", backdate iat/nbf so exp is still after nbf (a token that WAS valid, then expired).
        var issued = scenario == "expired" ? now.AddSeconds(-180) : now;
        var exp = scenario == "expired"
            ? now.AddSeconds(-30)
            : now.AddSeconds(_o.Tokens.JwtLifetimeSeconds);
        var aud = scenario == "wrong-aud" ? "some-other-audience" : _o.Aspire.Audience;

        // Claim names are fixed by the client guide §6 + RFC 7519 — do not rename them.
        // `member_id` is the guide's optional reconciliation field; Aspire ignores it, so we omit it.
        var claims = new List<Claim>
        {
            new("sub", user.Sub),                       // subject — which customer this is about
            new("email", user.Email),
            new("given_name", user.GivenName),
            new("family_name", user.FamilyName),
            new("country", user.Country),               // conditional — Aspire renders it
            new("program", user.Program),               // conditional — Aspire renders it
            new("jti", Guid.NewGuid().ToString()),      // JWT id, unique per tap — Aspire rejects a reuse
            new("iat", issued.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64), // issued-at
        };
        // Which reward to open. Ours, not in the client-facing contract yet — see docs/REDEEM_SSO_ANALYSIS.md.
        if (!string.IsNullOrEmpty(target)) claims.Add(new Claim("target", target));

        var token = new JwtSecurityToken(
            issuer: _o.Issuer,                          // iss — must match what Aspire registered for us
            audience: aud,                              // aud — Aspire + environment; stops a UAT token working on PROD
            claims: claims,
            notBefore: issued.UtcDateTime,              // nbf — not valid before this
            expires: exp.UtcDateTime,                   // exp — 120s; the guide caps this at 60-120s
            signingCredentials: new SigningCredentials(_keys.SigningKey, SecurityAlgorithms.RsaSha256));
        token.Header["kid"] = _o.SigningKeyId;          // kid — tells Aspire which JWKS key to verify with

        var jwt = new JwtSecurityTokenHandler().WriteToken(token);

        if (scenario == "tampered")
        {
            var parts = jwt.Split('.');
            var sig = parts[2].ToCharArray();
            sig[0] = sig[0] == 'A' ? 'B' : 'A';
            parts[2] = new string(sig);
            jwt = string.Join('.', parts);
        }
        return jwt;
    }
}
