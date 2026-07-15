using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace Aspire.Sso;

// SAML 2.0 SP — Aspire validates an assertion the CLIENT's IdP signed.
// We hold no signing key and no user directory: identity comes from the signed assertion alone,
// exactly as it does on the JWT path.
public sealed class SamlValidator
{
    private const string SamlNs = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const string SamlpNs = "urn:oasis:names:tc:SAML:2.0:protocol";

    private readonly AspireOptions _o;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<SamlValidator> _log;
    // certs by metadata URL — the SAML equivalent of JwksCache
    private readonly Dictionary<string, (X509Certificate2 Cert, DateTimeOffset At)> _certs = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public SamlValidator(AspireOptions o, IHttpClientFactory http, ILogger<SamlValidator> log)
        => (_o, _http, _log) = (o, http, log);

    public record SamlSubject(string Sub, string Email, string DisplayName,
        string Country, string Program, string? Target);

    // Fetch the client's PUBLIC signing certificate from their SAML metadata.
    // Same trust model as JWKS: we never hold their private key.
    public async Task<X509Certificate2?> GetSigningCertAsync(AspireOptions.RegisteredClient client)
    {
        var url = client.SamlMetadataUrl;
        if (_certs.TryGetValue(url, out var hit) && DateTimeOffset.UtcNow - hit.At < Ttl)
            return hit.Cert;

        try
        {
            var xml = await _http.CreateClient().GetStringAsync(url);
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("md", "urn:oasis:names:tc:SAML:2.0:metadata");
            ns.AddNamespace("ds", "http://www.w3.org/2000/09/xmldsig#");
            var b64 = doc.SelectSingleNode("//md:KeyDescriptor[@use='signing']//ds:X509Certificate", ns)?.InnerText;
            if (b64 is null) { _log.LogError("No signing certificate in metadata at {Url}", url); return null; }

            var cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(b64.Trim()));
            _certs[url] = (cert, DateTimeOffset.UtcNow);
            _log.LogInformation("Fetched SAML signing cert from {Url}", url);
            return cert;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not fetch SAML metadata from {Url}", url);
            return null;
        }
    }

    // The client may re-key behind unchanged metadata — drop the cache and refetch on a bad signature.
    public void InvalidateCert(string metadataUrl) => _certs.Remove(metadataUrl);

    public (bool ok, string? error, SamlSubject? user, string? assertionId, DateTimeOffset exp) Validate(
        string base64Response, string expectedAcsUrl, AspireOptions.RegisteredClient client, X509Certificate2 cert)
    {
        try
        {
            var xml = Encoding.UTF8.GetString(Convert.FromBase64String(base64Response));
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.LoadXml(xml);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("samlp", SamlpNs);
            ns.AddNamespace("saml", SamlNs);
            ns.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);

            if (doc.SelectSingleNode("//samlp:StatusCode/@Value", ns)?.Value
                != "urn:oasis:names:tc:SAML:2.0:status:Success")
                return Fail("SAML status not Success");

            if (doc.SelectSingleNode("//saml:Assertion", ns) is not XmlElement assertion)
                return Fail("No assertion in response");
            var assertionId = assertion.GetAttribute("ID");

            if (assertion.SelectSingleNode("ds:Signature", ns) is not XmlElement sigNode)
                return Fail("Assertion is not signed");

            var signedXml = new SignedXmlWithId(doc);
            signedXml.LoadXml(sigNode);
            if (!signedXml.CheckSignature(cert.PublicKey.GetRSAPublicKey()!))
                return Fail("Invalid signature");

            // The signature must cover THIS assertion — otherwise an attacker can wrap a signed
            // assertion around forged content and we'd validate the wrong element (XSW).
            if (signedXml.SignedInfo!.References.Count != 1 ||
                ((Reference)signedXml.SignedInfo.References[0]!).Uri != "#" + assertionId)
                return Fail("Signature does not cover the assertion");

            if (assertion.SelectSingleNode("saml:Issuer", ns)?.InnerText != client.SamlEntityId)
                return Fail("Issuer mismatch");

            if (doc.SelectSingleNode("//saml:AudienceRestriction/saml:Audience", ns)?.InnerText != _o.Audience)
                return Fail("Audience mismatch");

            // Recipient must be OUR ACS — stops an assertion minted for another SP being replayed here.
            if (doc.SelectSingleNode("//saml:SubjectConfirmationData/@Recipient", ns)?.Value != expectedAcsUrl)
                return Fail("Recipient (ACS) mismatch");

            var notOnOrAfterRaw = doc.SelectSingleNode("//saml:Conditions/@NotOnOrAfter", ns)?.Value;
            if (!DateTimeOffset.TryParse(notOnOrAfterRaw, out var notOnOrAfter))
                return Fail("Missing Conditions/NotOnOrAfter");
            if (notOnOrAfter.AddSeconds(_o.ClockSkewSeconds) < DateTimeOffset.UtcNow)
                return Fail("Assertion expired");

            var email = doc.SelectSingleNode("//saml:Subject/saml:NameID", ns)?.InnerText;
            if (string.IsNullOrEmpty(email)) return Fail("Missing NameID");

            string Attr(string name) =>
                doc.SelectSingleNode($"//saml:Attribute[@Name='{name}']/saml:AttributeValue", ns)?.InnerText ?? "";

            var sub = Attr("sub");
            if (string.IsNullOrEmpty(sub)) return Fail("Missing mandatory attribute: sub");

            var subject = new SamlSubject(sub, email,
                $"{Attr("firstName")} {Attr("lastName")}".Trim(),
                Attr("country"), Attr("program"),
                Attr("target") is { Length: > 0 } t ? t : null);

            return (true, null, subject, assertionId, notOnOrAfter);
        }
        catch (Exception ex)
        {
            return Fail($"Malformed SAML: {ex.GetType().Name}");
        }
    }

    private static (bool, string?, SamlSubject?, string?, DateTimeOffset) Fail(string e) => (false, e, null, null, default);

    public string BuildSpMetadata(string acsUrl) => $"""
        <?xml version="1.0"?>
        <EntityDescriptor xmlns="urn:oasis:names:tc:SAML:2.0:metadata" entityID="{_o.SamlEntityId}">
          <SPSSODescriptor AuthnRequestsSigned="false" WantAssertionsSigned="true" protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
            <AssertionConsumerService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST" Location="{acsUrl}" index="0"/>
          </SPSSODescriptor>
        </EntityDescriptor>
        """;
}

// SignedXml resolves reference URIs by "Id"; SAML uses "ID".
internal sealed class SignedXmlWithId : SignedXml
{
    public SignedXmlWithId(XmlDocument doc) : base(doc) { }
    public override XmlElement? GetIdElement(XmlDocument? doc, string id)
    {
        var e = base.GetIdElement(doc, id);
        if (e is not null || doc is null) return e;
        foreach (var attr in new[] { "ID", "Id", "id" })
        {
            var nodes = doc.SelectNodes($"//*[@{attr}='{id}']");
            if (nodes is { Count: > 0 }) return nodes[0] as XmlElement;
        }
        return null;
    }
}
