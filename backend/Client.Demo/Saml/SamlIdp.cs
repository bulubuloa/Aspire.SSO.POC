using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace Client.Demo;

// SAML 2.0 IdP — the CLIENT authenticates the user and signs the assertion.
// Aspire is the SP and only validates; it never signs anything.
//
// Unlike the JWT path this cannot be a back-channel call: the HTTP-POST binding requires the
// browser to carry the assertion to Aspire's ACS. The customer still types nothing.
public sealed class SamlIdp
{
    private const string SamlNs = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const string SamlpNs = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string Sha256 = "http://www.w3.org/2001/04/xmlenc#sha256";
    private const string RsaSha256 = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";

    private readonly ClientKeys _keys;
    private readonly ClientOptions _o;
    public SamlIdp(ClientKeys keys, ClientOptions options) => (_keys, _o) = (keys, options);

    private static string Ts(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

    // Builds a signed SAML Response, base64'd for the HTTP-POST binding.
    // `scenario` produces deliberately-invalid assertions for negative testing — demo only.
    public string BuildSignedResponse(ClientUser user, string acsUrl, string? scenario = null, string? target = null)
    {
        var now = DateTimeOffset.UtcNow;
        var respId = "_" + Guid.NewGuid().ToString("N");
        var assertId = "_" + Guid.NewGuid().ToString("N");
        var notOnOrAfter = scenario == "expired" ? now.AddSeconds(-30) : now.AddMinutes(5);
        var audience = scenario == "wrong-aud" ? "some-other-sp" : _o.Aspire.Audience;

        // Which reward to open, same idea as the JWT `target` claim — and equally not in the
        // client-facing contract yet.
        var targetAttr = string.IsNullOrEmpty(target) ? "" :
            $"""<saml:Attribute Name="target"><saml:AttributeValue>{target}</saml:AttributeValue></saml:Attribute>""";

        var xml = $"""
        <samlp:Response xmlns:samlp="{SamlpNs}" xmlns:saml="{SamlNs}" ID="{respId}" Version="2.0" IssueInstant="{Ts(now)}" Destination="{acsUrl}">
          <saml:Issuer>{_o.SamlEntityId}</saml:Issuer>
          <samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success"/></samlp:Status>
          <saml:Assertion xmlns:saml="{SamlNs}" ID="{assertId}" Version="2.0" IssueInstant="{Ts(now)}">
            <saml:Issuer>{_o.SamlEntityId}</saml:Issuer>
            <saml:Subject>
              <saml:NameID Format="urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress">{user.Email}</saml:NameID>
              <saml:SubjectConfirmation Method="urn:oasis:names:tc:SAML:2.0:cm:bearer">
                <saml:SubjectConfirmationData NotOnOrAfter="{Ts(notOnOrAfter)}" Recipient="{acsUrl}"/>
              </saml:SubjectConfirmation>
            </saml:Subject>
            <saml:Conditions NotBefore="{Ts(now.AddMinutes(-1))}" NotOnOrAfter="{Ts(notOnOrAfter)}">
              <saml:AudienceRestriction><saml:Audience>{audience}</saml:Audience></saml:AudienceRestriction>
            </saml:Conditions>
            <saml:AuthnStatement AuthnInstant="{Ts(now)}">
              <saml:AuthnContext><saml:AuthnContextClassRef>urn:oasis:names:tc:SAML:2.0:ac:classes:Password</saml:AuthnContextClassRef></saml:AuthnContext>
            </saml:AuthnStatement>
            <saml:AttributeStatement>
              <saml:Attribute Name="email"><saml:AttributeValue>{user.Email}</saml:AttributeValue></saml:Attribute>
              <saml:Attribute Name="firstName"><saml:AttributeValue>{user.GivenName}</saml:AttributeValue></saml:Attribute>
              <saml:Attribute Name="lastName"><saml:AttributeValue>{user.FamilyName}</saml:AttributeValue></saml:Attribute>
              <saml:Attribute Name="country"><saml:AttributeValue>{user.Country}</saml:AttributeValue></saml:Attribute>
              <saml:Attribute Name="program"><saml:AttributeValue>{user.Program}</saml:AttributeValue></saml:Attribute>
              <saml:Attribute Name="sub"><saml:AttributeValue>{user.Sub}</saml:AttributeValue></saml:Attribute>
              {targetAttr}
            </saml:AttributeStatement>
          </saml:Assertion>
        </samlp:Response>
        """;

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);
        SignAssertion(doc, assertId, tamper: scenario == "tampered");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(doc.OuterXml));
    }

    private void SignAssertion(XmlDocument doc, string assertionId, bool tamper)
    {
        var signedXml = new SignedXmlWithId(doc) { SigningKey = _keys.Rsa };
        signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signedXml.SignedInfo.SignatureMethod = RsaSha256;

        var reference = new Reference("#" + assertionId) { DigestMethod = Sha256 };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signedXml.AddReference(reference);

        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(_keys.SamlCertificate));
        signedXml.KeyInfo = keyInfo;

        signedXml.ComputeSignature();
        var sig = signedXml.GetXml();

        // Schema order: the signature sits directly after the Assertion's <Issuer>.
        var nsmgr = new XmlNamespaceManager(doc.NameTable);
        nsmgr.AddNamespace("saml", SamlNs);
        var assertion = doc.SelectSingleNode($"//saml:Assertion[@ID='{assertionId}']", nsmgr)!;
        var issuer = assertion.SelectSingleNode("saml:Issuer", nsmgr)!;
        assertion.InsertAfter(doc.ImportNode(sig, true), issuer);

        if (tamper)
        {
            // Corrupt a value AFTER signing so the digest no longer matches.
            var nameId = doc.SelectSingleNode("//saml:NameID", nsmgr);
            if (nameId is not null) nameId.InnerText = "tampered@evil.com";
        }
    }

    public string BuildIdpMetadata(string ssoUrl) => $"""
        <?xml version="1.0"?>
        <EntityDescriptor xmlns="urn:oasis:names:tc:SAML:2.0:metadata" entityID="{_o.SamlEntityId}">
          <IDPSSODescriptor protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
            <KeyDescriptor use="signing"><KeyInfo xmlns="http://www.w3.org/2000/09/xmldsig#"><X509Data>
              <X509Certificate>{Convert.ToBase64String(_keys.SamlCertificate.RawData)}</X509Certificate>
            </X509Data></KeyInfo></KeyDescriptor>
            <SingleSignOnService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect" Location="{ssoUrl}"/>
          </IDPSSODescriptor>
        </EntityDescriptor>
        """;
}

// SignedXml resolves reference URIs by "Id" attribute; SAML uses "ID".
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
