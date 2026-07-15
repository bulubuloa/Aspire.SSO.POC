using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Client.Demo;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;

// =====================================================================================
//  CLIENT.DEMO — the customer-owned system.
//
//  Owns: its own user directory + authentication, the reward catalogue, and the RSA
//  private key used to sign SSO tokens. Knows nothing about how Aspire stores sessions.
//  Talks to Aspire only over HTTP, authenticated with the client credentials Aspire issued.
// =====================================================================================

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection("Client").Get<ClientOptions>()
    ?? throw new InvalidOperationException("Missing 'Client' configuration section.");
Validate(options);

var users = options.Users.Select(u => new ClientUser(u.Username, u.Password, u.Sub, u.Email,
    u.GivenName, u.FamilyName, u.Country, u.Program, u.MemberId, u.Active)).ToList();
var rewards = options.Rewards.Select(r => new Reward(r.Id, r.Title, r.Points, r.Icon, r.Detail,
    r.Featured, r.Tag)).ToList();

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ClientKeys>();
builder.Services.AddSingleton<JwtIssuer>();
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

app.UseCors();

ClientUser? FindUser(string? username) =>
    users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

// ---- The client's OWN authentication. Aspire is not involved. ----
app.MapPost("/api/login", (LoginDto dto) =>
{
    var user = users.FirstOrDefault(u =>
        string.Equals(u.Username, dto.Username, StringComparison.OrdinalIgnoreCase) && u.Password == dto.Password);
    if (user is null) return Results.Json(new { error = "Invalid credentials" }, statusCode: 401);
    return Results.Json(new
    {
        user.Username, user.Sub, user.Email,
        displayName = $"{user.GivenName} {user.FamilyName}",
        user.Country, user.Program, user.Active
    });
});

// ---- The reward catalogue the client app renders. ----
app.MapGet("/api/rewards", () => Results.Json(rewards));

// ---- JWKS: the PUBLIC half of our signing key. Aspire fetches this to validate our tokens. ----
app.MapGet("/.well-known/jwks.json", (ClientKeys keys) => Results.Json(keys.BuildJwks()));

// ---- SILENT REDEEM HANDOFF ----
// The app calls this on "Redeem". We sign the token and hand it to Aspire back-channel,
// then return only a one-time launch URL. The app never sees the token.
app.MapPost("/api/redeem", async (RedeemDto dto, JwtIssuer issuer, IHttpClientFactory httpFactory, ILogger<Program> log) =>
{
    // In production this is the caller's authenticated session, not a username in the body.
    var user = FindUser(dto.Username);
    if (user is null) return Results.Json(new { error = "Not authenticated" }, statusCode: 401);

    // Eligibility is the CLIENT's call — we own the user's status, Aspire does not have a user
    // directory to check it against. Do not mint a token for someone who may not redeem.
    // See docs/SERVICE_SEPARATION.md ("who owns eligibility").
    if (!user.Active)
        return Results.Json(new { error = "User is inactive / not authorised" }, statusCode: 403);

    var reward = rewards.FirstOrDefault(r => string.Equals(r.Id, dto.RewardId, StringComparison.OrdinalIgnoreCase));
    if (reward is null) return Results.Json(new { error = "Unknown reward" }, statusCode: 400);

    // 1. Sign a short-lived JWT with OUR private key, deep-linked to this reward.
    var token = issuer.Issue(user, dto.Scenario, target: reward.Id);

    // 2. Back-channel POST to Aspire, authenticated with the credentials Aspire issued us.
    var secret = dto.Scenario == "bad-secret" ? "wrong-secret" : options.Aspire.ClientSecret;
    var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Aspire.ClientId}:{secret}"));

    var req = new HttpRequestMessage(HttpMethod.Post, options.Aspire.SsoEndpoint)
    {
        Content = JsonContent.Create(new { token }),
    };
    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    try
    {
        var res = await httpFactory.CreateClient().SendAsync(req);
        var body = await res.Content.ReadFromJsonAsync<AspireSsoResult>();
        if (!res.IsSuccessStatusCode || body?.LaunchUrl is null)
            return Results.Json(new { error = body?.Error ?? "SSO handoff failed" }, statusCode: 401);

        // 3. Hand the app a URL to open — already an authenticated Aspire session.
        return Results.Json(new { launchUrl = body.LaunchUrl, reward = reward.Title });
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
    {
        // Unreachable, bad endpoint URI, or timeout — never leak a 500 to the caller.
        log.LogError(ex, "Aspire SSO call failed for endpoint {Endpoint}", options.Aspire.SsoEndpoint);
        return Results.Json(new { error = "Aspire SSO is unreachable" }, statusCode: 502);
    }
});

// ---- Demo-only: mint a raw token so test.sh can call Aspire directly. Remove for production. ----
app.MapPost("/api/jwt", (JwtReqDto dto, JwtIssuer issuer) =>
{
    var user = FindUser(dto.Username);
    if (user is null) return Results.Json(new { error = "Unknown user" }, statusCode: 400);
    return Results.Json(new { token = issuer.Issue(user, dto.Scenario), scenario = dto.Scenario });
});

app.Run();

// Fail fast at startup rather than on every redeem. A missing value here is a deployment
// mistake, and the runtime symptom ("Issuer mismatch", HTTP 500) points at the wrong thing.
static void Validate(ClientOptions o)
{
    var errors = new List<string>();

    void Required(string? value, string key, string why)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"  Client:{key} is required — {why}");
    }

    Required(o.Issuer, "Issuer", "becomes the JWT `iss`; must match what Aspire registered");
    Required(o.SigningKeyId, "SigningKeyId", "becomes the JWT `kid`; Aspire uses it to pick the key from your JWKS");
    Required(o.Aspire.Audience, "Aspire:Audience", "becomes the JWT `aud`; issued by Aspire, per environment");
    Required(o.Aspire.ClientId, "Aspire:ClientId", "issued by Aspire; authenticates the back-channel call");
    Required(o.Aspire.ClientSecret, "Aspire:ClientSecret", "issued by Aspire; keep it out of source control");
    Required(o.Aspire.SsoEndpoint, "Aspire:SsoEndpoint", "issued by Aspire; where the token is POSTed");

    if (!string.IsNullOrWhiteSpace(o.Aspire.SsoEndpoint)
        && !Uri.TryCreate(o.Aspire.SsoEndpoint, UriKind.Absolute, out _))
        errors.Add($"  Client:Aspire:SsoEndpoint must be an absolute URI (got '{o.Aspire.SsoEndpoint}')");

    if (o.Tokens.JwtLifetimeSeconds is < 30 or > 300)
        errors.Add($"  Client:Tokens:JwtLifetimeSeconds should be 30-300 (guide recommends 60-120); got {o.Tokens.JwtLifetimeSeconds}");

    if (o.Users.Count == 0) errors.Add("  Client:Users is empty — nobody can log in");
    if (o.Rewards.Count == 0) errors.Add("  Client:Rewards is empty — nothing to redeem");

    if (errors.Count > 0)
        throw new InvalidOperationException(
            "Invalid configuration:\n" + string.Join("\n", errors) +
            "\nOverride per environment with env vars, e.g. Client__Aspire__ClientSecret=…");
}

record LoginDto(string? Username, string? Password);
record JwtReqDto(string? Username, string? Scenario);
record RedeemDto(string? Username, string? RewardId, string? Scenario);
record AspireSsoResult(bool Ok, string? LaunchUrl, string? Via, string? Error);
