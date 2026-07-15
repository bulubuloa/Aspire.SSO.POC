using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Sso;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;

// =====================================================================================
//  ASPIRE.SSO — the Aspire-owned service provider.
//
//  Owns: the SSO endpoint, session store, replay protection and the benefit/redeem
//  web-view. Holds NO user directory, NO passwords and NO signing key — it validates a
//  token the client signed, resolving their public keys from their JWKS URL.
// =====================================================================================

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection("Aspire").Get<AspireOptions>()
    ?? throw new InvalidOperationException("Missing 'Aspire' configuration section.");
if (options.RegisteredClients.Count == 0)
    throw new InvalidOperationException("Aspire:RegisteredClients is empty — no client is onboarded.");

var rewards = options.Rewards
    .Select(r => new Reward(r.Id, r.Title, r.Points, r.Icon, r.Detail))
    .ToList();

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<JwksCache>();
builder.Services.AddSingleton<JwtValidator>();
builder.Services.AddHttpClient();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Hosted behind a TLS-terminating proxy (Caddy), so the scheme arrives in a header.
// Without this req.IsHttps is false: the session cookie is issued Lax-without-Secure and
// launchUrl comes back http — which silently breaks the cross-site web handoff.
//
// KnownNetworks/KnownProxies default to loopback ONLY, and Caddy reaches us from a docker
// network address — so they must be CLEARED, not left empty. `KnownNetworks = { }` in an
// object initializer adds nothing; it does not clear. Safe here because the only route in
// is Caddy: the app port is never published and ufw allows just 22/80/443.
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
};
fwd.KnownNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);

app.UseStaticFiles();   // aspire-theme.css + favicon for the reward page
app.UseCors();

const string SessionCookie = "aspire_session";
string Base(HttpRequest r) => $"{r.Scheme}://{r.Host}";
Reward? FindReward(string? id) =>
    rewards.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

// ---- SSO endpoint: validate a client-signed JWT and create a session ----
// Back-channel (JSON) callers must present the client credentials we issued them.
// Browsers (front-channel form POST) are public clients and cannot hold a secret, so they
// are authenticated by the signed token alone.
app.MapPost("/sso/jwt", async (HttpRequest req, JwtValidator validator, SessionStore state) =>
{
    var (token, wantsJson) = await ReadToken(req);
    if (string.IsNullOrWhiteSpace(token)) return Fail(wantsJson, "Missing token");

    AspireOptions.RegisteredClient? client;
    if (wantsJson)
    {
        client = AuthenticateCaller(req);
        if (client is null)
            return Results.Json(new { ok = false, error = "Invalid or missing client credentials" }, statusCode: 401);
    }
    else
    {
        // Front-channel: no caller credentials. Identify the client by the token's issuer.
        client = ClientForToken(token);
        if (client is null) return Fail(false, "Issuer mismatch");
    }

    var r = await validator.ValidateAsync(token, client);
    if (!r.Ok) return Fail(wantsJson, r.Error!);
    if (!state.TryConsumeJti(r.Jti!, r.Exp)) return Fail(wantsJson, "Replay detected (jti already used)");

    var session = state.CreateSession(r.User!, "JWT", r.Target, client.ClientId);
    return Succeed(req, wantsJson, state, session);
});

// ---- The benefit / redeem web-view ----
app.MapGet("/benefit", (HttpRequest req, HttpResponse res, [FromQuery] string? ticket, SessionStore state) =>
{
    if (!string.IsNullOrEmpty(ticket))
    {
        var sid = state.RedeemTicket(ticket);
        if (sid is not null)
        {
            // Over HTTPS the reward page may be framed by the client's web app on a different
            // domain, and a Lax cookie would be dropped there. None+Secure is the only combination
            // browsers send cross-site — and it requires HTTPS, so keep Lax when running locally.
            var crossSite = req.IsHttps;
            res.Cookies.Append(SessionCookie, sid, new CookieOptions
            {
                HttpOnly = true,
                Path = "/",
                Secure = crossSite,
                SameSite = crossSite ? SameSiteMode.None : SameSiteMode.Lax,
            });
            return Results.Redirect("/benefit");   // drop the ticket from the URL
        }
    }

    var session = state.GetSession(req.Cookies[SessionCookie]);
    if (session is null) return Results.Content(Pages.Error("No Aspire session"), "text/html");

    var reward = FindReward(session.Target);
    if (reward is null) return Results.Content(Pages.Benefit(session), "text/html");

    // Where to send the customer when they're done — agreed with the client at onboarding.
    var returnUrl = options.RegisteredClients
        .FirstOrDefault(c => c.ClientId == session.ClientId)?.ReturnUrl ?? "";
    return Results.Content(Pages.Redeem(session, reward, returnUrl), "text/html");
});

app.MapGet("/api/session", (HttpRequest req, SessionStore state) =>
{
    var s = state.GetSession(req.Cookies[SessionCookie]);
    return s is null ? Results.Json(new { authenticated = false }, statusCode: 401)
                     : Results.Json(new { authenticated = true, s.Sub, s.Email, s.DisplayName, s.Country, s.Program, s.Via });
});

app.MapPost("/logout", (HttpRequest req, HttpResponse res, SessionStore state) =>
{
    state.EndSession(req.Cookies[SessionCookie]);
    res.Cookies.Delete(SessionCookie);
    return Results.Ok(new { loggedOut = true });
});

app.Run();

// =====================================================================================
//  Helpers
// =====================================================================================

async Task<(string token, bool wantsJson)> ReadToken(HttpRequest req)
{
    var wantsJson = req.Headers.Accept.ToString().Contains("application/json")
                    || req.ContentType?.Contains("application/json") == true;
    if (req.ContentType?.Contains("application/json") == true)
    {
        var body = await JsonSerializer.DeserializeAsync<TokenDto>(req.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return (body?.Token ?? "", true);
    }
    var form = await req.ReadFormAsync();
    return (form["token"].ToString(), wantsJson);
}

// HTTP Basic: Authorization: Basic base64(client_id:client_secret)
AspireOptions.RegisteredClient? AuthenticateCaller(HttpRequest req)
{
    var header = req.Headers.Authorization.ToString();
    if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
    string decoded;
    try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim())); }
    catch { return null; }

    var sep = decoded.IndexOf(':');
    if (sep < 0) return null;
    var id = decoded[..sep];
    var secret = decoded[(sep + 1)..];

    var client = options.RegisteredClients.FirstOrDefault(c => c.ClientId == id);
    if (client is null) return null;
    // Fixed-time compare so the secret can't be recovered byte-by-byte.
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(client.ClientSecret)) ? client : null;
}

// Front-channel only: match the token's `iss` to an onboarded client (signature still decides).
AspireOptions.RegisteredClient? ClientForToken(string token)
{
    try
    {
        var payload = token.Split('.')[1];
        payload = payload.Replace('-', '+').Replace('_', '/').PadRight((payload.Length + 3) / 4 * 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        var iss = doc.RootElement.GetProperty("iss").GetString();
        return options.RegisteredClients.FirstOrDefault(c => c.Issuer == iss);
    }
    catch { return null; }
}

IResult Succeed(HttpRequest req, bool wantsJson, SessionStore state, SessionStore.AspireSession session)
{
    if (wantsJson)
    {
        var ticket = state.IssueTicket(session.Id);
        return Results.Json(new { ok = true, launchUrl = $"{Base(req)}/benefit?ticket={ticket.Code}", via = session.Via });
    }
    return Results.Content(Pages.SigningIn(session), "text/html");
}

IResult Fail(bool wantsJson, string error) =>
    wantsJson ? Results.Json(new { ok = false, error }, statusCode: 401)
              : Results.Content(Pages.Error(error), "text/html");

record TokenDto(string? Token);
