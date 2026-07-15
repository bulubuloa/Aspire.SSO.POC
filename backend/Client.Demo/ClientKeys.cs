using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Client.Demo;

// The CLIENT's signing material. The private key lives here and NEVER leaves this service —
// not to Aspire, and above all not into the mobile app.
// Aspire only ever sees the public half, fetched from /.well-known/jwks.json.
public sealed class ClientKeys
{
    public RSA Rsa { get; }
    public RsaSecurityKey SigningKey { get; }
    private readonly string _kid;

    public ClientKeys(ClientOptions options)
    {
        _kid = options.SigningKeyId;
        Rsa = RSA.Create(2048);
        SigningKey = new RsaSecurityKey(Rsa) { KeyId = _kid };

    }

    // Public key only — this is what Aspire fetches to validate our signatures.
    public object BuildJwks()
    {
        var p = Rsa.ExportParameters(false);
        return new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = _kid,
                    n = Base64UrlEncoder.Encode(p.Modulus),
                    e = Base64UrlEncoder.Encode(p.Exponent)
                }
            }
        };
    }
}
