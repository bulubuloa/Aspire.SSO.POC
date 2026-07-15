namespace Aspire.Sso;

// Everything ASPIRE owns. Bound from the "Aspire" section of appsettings.json.
// Note what is NOT here: no user directory, no passwords, no signing key. Aspire never
// authenticates a customer — it only validates a token the client signed.
public sealed class AspireOptions
{
    public string Audience { get; set; } = "";
    public int ClockSkewSeconds { get; set; } = 30;
    public int LaunchTicketSeconds { get; set; } = 60;

    public List<RegisteredClient> RegisteredClients { get; set; } = new();
    public List<RewardOptions> Rewards { get; set; } = new();

    // One onboarded client. In production this is a database table, not config.
    public sealed class RegisteredClient
    {
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";   // secret — hash at rest in production
        public string Name { get; set; } = "";
        public string Issuer { get; set; } = "";         // expected `iss`
        public string JwksUrl { get; set; } = "";        // where we fetch their public keys
        public string ReturnUrl { get; set; } = "";      // deep link back to their app when done
        }

    public sealed class RewardOptions
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public int Points { get; set; }
        public string Icon { get; set; } = "";
        public string Detail { get; set; } = "";
    }
}

public record Reward(string Id, string Title, int Points, string Icon, string Detail);
