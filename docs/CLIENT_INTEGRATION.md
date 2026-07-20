# Client Integration — what to ask for

The checklist for onboarding a client onto Aspire SSO. Two protocols; a client uses **one**.
For each, there's what **you ask the client for**, what **you give them back**, and what they
**send at runtime**. Field rules come straight from the validators (`Jwt/JwtValidator.cs`,
`Saml/SamlValidator.cs`).

---

## JWT

### Ask the client for

| # | Ask | Why you need it |
|---|-----|-----------------|
| 1 | **Issuer string (`iss`)** | A fixed text ID for their system. You register it and reject any token whose `iss` doesn't match — proof the token came from them. |
| 2 | **JWKS URL** | A public HTTPS link where they publish their **public** signing key. You fetch it to verify the signature. They keep the private key; you never ask for it. |
| 3 | **Key ID (`kid`)** | A label on the key. If they rotate keys, `kid` tells you which one to verify with. |
| 4 | **Confirm RS256** | You only accept RSA-SHA256; weaker algorithms and `alg:none` are rejected on purpose. |
| 5 | **Which user fields the token carries** | You require `sub`, `email`, `given_name`, `family_name`, `jti`, `iat`, `exp`. Confirm they can send them all. |
| 6 | **Token lifetime of 60–120s** | The token must be short-lived. You reject anything older than a few minutes. |

### You give them back

- **`client_id`** + **`client_secret`** — for the back-channel HTTP Basic auth
- **Audience** value to put in `aud` — identifies your environment (UAT token can't be used on PROD)
- Your **`/sso/jwt` endpoint URL**
- A **ReturnUrl / deep link** you register for them (where the customer lands after redeem)

### What they send at runtime (server-to-server — the browser never sees it)

```
POST https://aspire.../sso/jwt
Authorization: Basic base64(client_id:client_secret)
Accept: application/json
Content-Type: application/json

{ "token": "<signed JWT>" }
```

| Claim | Required | Rule |
|-------|:---:|------|
| header `alg` | ✓ | must be `RS256` |
| header `kid` | ✓ | selects the key from their JWKS |
| `iss` | ✓ | must match the registered Issuer |
| `aud` | ✓ | must match your Audience |
| `sub` | ✓ | the customer id |
| `email` | ✓ | |
| `given_name` | ✓ | |
| `family_name` | ✓ | |
| `jti` | ✓ | unique per tap — **reuse is rejected** (replay) |
| `iat` | ✓ | issued-at — rejected if in the future or > 5 min old |
| `exp` / `nbf` | ✓ | validity window, 60–120s |
| `country`, `program` | – | optional; rendered if present |

You reply `{ "ok": true, "launchUrl": "…" }` — a one-time URL the app opens.

---

## SAML

### Ask the client (the IdP) for

| # | Ask | Why you need it |
|---|-----|-----------------|
| 1 | **IdP Entity ID** | A fixed text ID for their identity system. It becomes the assertion's `<Issuer>`; you check it matches. |
| 2 | **SAML metadata URL** (or their public signing certificate) | Contains their public cert — you verify every assertion's signature against it. A metadata URL is better: you can auto-refresh if the cert rotates. |
| 3 | **Confirm signature profile** | RSA-SHA256, exclusive C14N, and the signature must cover the **`<Assertion>`** (not just the outer Response) — a common mistake. |
| 4 | **Attribute names** for email, first name, last name, user id | SAML lets everyone name fields differently. You must know exactly what they call them. **This is the #1 thing that breaks SAML integrations — pin it down.** |
| 5 | **What `NameID` contains** — email or a user id | It's the primary identifier in the assertion; you need to know which value it holds. |

### You give them back

- Your **SP Entity ID** — they set it as the assertion's `<Audience>`
- Your **ACS URL** (`/sso/saml/acs`) — where the browser POSTs the assertion (HTTP-POST binding)
- Your **SP metadata URL** — contains both of the above
- **No client secret** — a browser can't hold one, so for SAML the signature is the entire trust.

### What they send at runtime (front-channel — the browser carries it)

```
POST https://aspire.../sso/saml/acs
Content-Type: application/x-www-form-urlencoded

SAMLResponse=<base64 of the signed XML>
```

The signed `<saml:Assertion>` must contain:

| Element | Rule |
|---------|------|
| `<ds:Signature>` over the assertion | verified against their cert; **must cover the assertion** (XSW guard: signature reference = assertion ID) |
| `<saml:Issuer>` | must match their registered IdP Entity ID |
| `<AudienceRestriction><Audience>` | must equal **your** SP Entity ID |
| `<SubjectConfirmationData Recipient=…>` | must be **your** ACS URL (blocks an assertion minted for another SP) |
| `<Conditions NotOnOrAfter>` | expiry — enforced |
| Assertion `ID` | unique — **reuse is rejected** (replay) |
| `<NameID>` + attributes | the user identity |

---

## The difference in one line

| | JWT | SAML |
|---|-----|------|
| Channel | back-channel (their server → yours) | front-channel (browser → your ACS) |
| Caller auth | `client_id` + `client_secret` | none — the XML signature is the trust |
| You fetch their key from | JWKS URL (by `kid`) | SAML metadata URL (X.509 cert) |
| Identity-match keys | `iss` + `aud` | Issuer + Audience + Recipient |
| Replay unit | `jti` | Assertion `ID` |
| Touches the browser? | no | yes |

---

## One thing to fix before a real rollout

The demo isn't consistent across the two protocols:

- JWT uses `given_name` / `family_name`; SAML uses `firstName` / `lastName`.
- JWT's `sub` is the account id; SAML's `NameID` is the email, with `sub` as a separate attribute.

For a real integration, agree **one** canonical attribute contract and use it in both.
