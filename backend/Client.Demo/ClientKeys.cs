using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.IdentityModel.Tokens;

namespace Client.Demo;

// The CLIENT's signing material. The private key lives here and NEVER leaves this service —
// not to Aspire, and above all not into the mobile app.
// Aspire only ever sees the public half, fetched from /.well-known/jwks.json.
public sealed class ClientKeys
{
    public RSA Rsa { get; }
    public RsaSecurityKey SigningKey { get; }
    // SAML signs XML with an X509 cert. Same RSA key underneath, so JWKS x5c and the SAML
    // cert are the same key material — one key, two protocols.
    public X509Certificate2 SamlCertificate { get; }
    private readonly string _kid;

    public ClientKeys(ClientOptions options)
    {
        _kid = options.SigningKeyId;
        Rsa = RSA.Create(2048);
        SigningKey = new RsaSecurityKey(Rsa) { KeyId = _kid };

        var req = new CertificateRequest($"CN={options.Issuer}", Rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // Fixed window so the cert doesn't depend on wall-clock at boot.
        SamlCertificate = req.CreateSelfSigned(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
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
                    e = Base64UrlEncoder.Encode(p.Exponent),
                    x5c = new[] { Convert.ToBase64String(SamlCertificate.RawData) }
                }
            }
        };
    }
}
