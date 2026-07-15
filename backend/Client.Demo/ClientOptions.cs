namespace Client.Demo;

// Everything the CLIENT owns. Bound from the "Client" section of appsettings.json.
// Note what is NOT here: no session store, no reward-redemption state, nothing about how
// Aspire validates anything. The client only knows how to reach Aspire and how to authenticate.
public sealed class ClientOptions
{
    public string Issuer { get; set; } = "";
    public string SigningKeyId { get; set; } = "";
    public string SamlEntityId { get; set; } = "";   // our IdP identity, only used for SAML

    public AspireIntegrationOptions Aspire { get; set; } = new();
    public TokenOptions Tokens { get; set; } = new();
    public List<UserOptions> Users { get; set; } = new();
    public List<RewardOptions> Rewards { get; set; } = new();

    // What Aspire gave us during onboarding. This is the whole integration contract.
    public sealed class AspireIntegrationOptions
    {
        public string SsoEndpoint { get; set; } = "";     // where we POST the token (JWT)
        public string SamlAcsUrl { get; set; } = "";      // where the browser POSTs the assertion (SAML)
        public string Audience { get; set; } = "";        // the `aud` Aspire expects
        public string ClientId { get; set; } = "";        // issued by Aspire
        public string ClientSecret { get; set; } = "";    // issued by Aspire — secret
    }

    public sealed class TokenOptions
    {
        public int JwtLifetimeSeconds { get; set; } = 120;
    }

    public sealed class UserOptions
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string Sub { get; set; } = "";
        public string Email { get; set; } = "";
        public string GivenName { get; set; } = "";
        public string FamilyName { get; set; } = "";
        public string Country { get; set; } = "";
        public string Program { get; set; } = "";
        public string MemberId { get; set; } = "";
        public bool Active { get; set; } = true;
    }

    public sealed class RewardOptions
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public int Points { get; set; }
        public string Icon { get; set; } = "";
        public string Detail { get; set; } = "";
        public bool Featured { get; set; }
        public string? Tag { get; set; }
    }
}

public record ClientUser(string Username, string Password, string Sub, string Email,
    string GivenName, string FamilyName, string Country, string Program, string MemberId, bool Active);

public record Reward(string Id, string Title, int Points, string Icon, string Detail,
    bool Featured = false, string? Tag = null);
