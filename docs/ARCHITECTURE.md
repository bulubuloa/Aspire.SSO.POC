# Architecture

A demo of **silent SSO on Redeem**: a customer already logged into the client's app taps REDEEM and
lands inside an Aspire reward page, already authenticated — **without ever seeing an Aspire login**.

Two flows: **login** (client-owned) and **redeem** (the SSO handoff).
Redeem runs over **either JWT or SAML 2.0** — both from the client guides, switchable at runtime.

**Live:** <https://demo.hqauth.shop>

---

## 1. The parts

```
        ┌──────────── CLIENT'S TRUST DOMAIN ────────────┐   ┌──── ASPIRE'S ────┐

          Demo Client app          Client.Demo  :5001        Aspire.Sso  :6001
          (Expo / React Native)    (ASP.NET Core 10)         (ASP.NET Core 10)
          ─────────────────────    ────────────────────      ──────────────────
          • login screen           • users + auth            • NO users, NO passwords
          • rewards list           • reward catalogue        • NO signing key
          • REDEEM        ──①──▶   • RSA PRIVATE key ──┐     • validates what they signed
          • opens web-view         • Jwt/  signs JWT   │     • session + replay store
                    │              • Saml/ signs XML   │     • Jwt/  validate + JWKS cache
                    │              • publishes JWKS ◀──┼──── • Saml/ validate + cert cache
                    │              • publishes SAML ◀──┼──── • reward page
                    │                metadata          └─②─▶ • issues client credentials
                    └──────────────────③──────────────────▶
                       (one-time ticket / signed assertion)
                    ◀─────────────────④──────────────────
                       (ReturnUrl deep link / postMessage)

        └───────────────────────────────────────────────┘   └──────────────────┘
```

| Component | Stack | Owns |
|---|---|---|
| **Demo Client app** | Expo / React Native — iOS, Android, web | UI only. No key, no token. |
| **Client.Demo** `:5001` | ASP.NET Core 10 | Users, auth, rewards, **private key**, signing (JWT + SAML IdP) |
| **Aspire.Sso** `:6001` | ASP.NET Core 10 | Validation (JWT + SAML SP), sessions, replay, reward page |

Brands differ on purpose: the app is **blue "Demo Client"**, Aspire's page is **crimson "Aspire
Lifestyles"**. The switch on Redeem *is* the handoff, visible in one second.

---

## 2. The trust boundary

Neither service can reach into the other:

```bash
curl :6001/api/login   → 404   # Aspire has no user directory
curl :5001/benefit     → 404   # the client has no sessions
```

| | Client.Demo | Aspire.Sso |
|---|---|---|
| Users & passwords | ✅ owns | ❌ none — never authenticates a customer |
| Private signing key | ✅ owns, never leaves | ❌ never sees it |
| Public keys / certs | publishes (JWKS + SAML metadata) | fetches over HTTP, caches |
| Sessions / replay | ❌ none | ✅ owns |
| Client credentials | holds the secret it was issued | issues + verifies |

**Aspire never learns the customer's password.** It receives an *assertion about* them, signed by
someone it has agreed to trust. Same model for both protocols.

---

## 3. Flow ① — Login (client-owned)

Aspire is not involved. No token is minted; nothing touches `:6001`.

```
app → POST :5001/api/login  {"username":"jane","password":"demo"}
    ← 200 {"username","sub","email","displayName","country","program","active"}
    ← 401 {"error":"Invalid credentials"}
app → stores the profile (OS keystore; localStorage on web), gate opens
```

⚠️ The response is a **profile, not a session token** — the server issues nothing it can later
verify. See *Open questions*.

---

## 4. Flow ② — Redeem

**The customer journey is identical for both protocols** — tap, land in the reward page, confirm,
return. That is the point: the client can move from SAML to JWT later with **no change to the app or
the journey**. Only the transport underneath differs.

```
app → POST :5001/api/redeem {"rewardId":"lounge","mode":"jwt"|"saml"}
    ← {"launchUrl":"…","via":"JWT"|"SAML"}
app → opens launchUrl in a web-view → reward page, already signed in
    ← ④ CLOSE → ReturnUrl deep link (native) / postMessage (web) → app marks ✓ REDEEMED
```

### JWT — fully silent, back-channel

```
:5001  mints RS256 JWT (private key, target=reward)
       POST :6001/sso/jwt
         Authorization: Basic base64(client_id:client_secret)
         {"token":"<jwt>"}
:6001  fetch client JWKS by kid → verify sig, iss, aud, exp, iat → consume jti
       → session → one-time 60s ticket
:5001  ← {"launchUrl":"…/benefit?ticket=…"}
```

**The token never touches the browser.** Two independent checks: the secret proves *who is calling*,
the signature proves *who the user is*.

### SAML 2.0 — the browser carries it

```
:5001  ← {"launchUrl":":5001/saml/sso?code=<one-time>"}
app    opens it → :5001 signs an assertion → auto-POST form
browser  POST :6001/sso/saml/acs {SAMLResponse}
:6001  fetch the client's cert from their SAML metadata → verify signature, XSW guard,
       issuer, audience, recipient, expiry → consume assertion id
       → session → 302 /benefit  (real Set-Cookie)
```

**SAML cannot be back-channel** — the HTTP-POST binding requires the browser to carry the assertion.
A one-time 60s code keeps the user's identity out of the URL. No client credentials here: a browser
is a public client and cannot hold a secret, so the signature is the only proof — which is exactly
what SAML is built around.

### Redeem errors (both modes)

| Case | Status | Body |
|---|---|---|
| Unknown user | `401` | `{"error":"Not authenticated"}` |
| Inactive user | `403` | `{"error":"User is inactive / not authorised"}` |
| Unknown reward | `400` | `{"error":"Unknown reward"}` |
| Token/assertion rejected | `401` | Aspire's reason — e.g. `{"error":"Token expired"}` |
| Aspire unreachable | `502` | `{"error":"Aspire SSO is unreachable"}` |

---

## 5. Endpoints

### Client.Demo `:5001`

| Method | Path | |
|---|---|---|
| POST | `/api/login` | client-owned auth |
| GET | `/api/rewards` | reward catalogue |
| POST | `/api/redeem` | **the handoff** — `mode: jwt \| saml` |
| GET | `/.well-known/jwks.json` | **public** keys, for Aspire (JWT) |
| GET | `/saml/sso` | SAML IdP — signs + auto-POSTs the assertion |
| GET | `/saml/metadata` | IdP Entity ID + **public** signing certificate |
| POST | `/api/jwt` | demo-only — raw token so `test.sh` can call Aspire directly |

### Aspire.Sso `:6001`

| Method | Path | |
|---|---|---|
| POST | `/sso/jwt` | **JWT SSO** — validate + session (client credentials required) |
| POST | `/sso/saml/acs` | **SAML ACS** — validate assertion + session |
| GET | `/sso/saml/metadata` | SP Entity ID + ACS URL |
| GET | `/benefit?ticket=` | redeem ticket → cookie → reward page |
| GET | `/api/session` | inspect the current session |
| POST | `/logout` | end it |

---

## 6. The token / assertion

Both carry the same identity; only the encoding differs.

```json
// JWT — Client.Demo/Jwt/JwtIssuer.cs → Aspire.Sso/Jwt/JwtValidator.cs
{ "alg": "RS256", "kid": "client-demo-key-2026-015", "typ": "JWT" }
{
  "sub": "C123456789", "email": "jane.tan@client-demo.com",
  "given_name": "Jane", "family_name": "Tan",
  "country": "TH", "program": "FWD_PREMIUM",
  "jti": "<uuid>", "iat": …, "nbf": …, "exp": …,   // exp = iat + 120s
  "iss": "https://client.demo",
  "aud": "aspire-lifestyle-sso-demo-env",
  "target": "lounge"                                // ⚠️ ours, not in the contract yet
}
```

**Claim names are fixed** by the guide §6 + RFC 7519 — `iss`/`sub`/`aud`/`exp`/`nbf`/`iat`/`jti` are
standard and validated *by name*. Renaming `exp` would silently disable expiry checking.

| Claim | Plain English |
|---|---|
| `iss` | issuer — who made this token (the client) |
| `sub` | subject — which customer it's about |
| `aud` | audience — who it's for. Stops a UAT token working on PROD |
| `exp` / `nbf` | dead after / not valid before |
| `iat` | issued at |
| `jti` | unique per tap → replay protection |
| `kid` | which key signed it → enables rotation |

**Enforced** (verified by removing each): `alg`+`kid`, `iss`, `aud`, `sub`, `email`, `given_name`,
`family_name`, `jti`, `exp`, `iat`. **Optional**: `country`, `program` (rendered), `target`, `nbf`,
`typ`. `member_id` is not sent — Aspire ignored it.

SAML carries the same values as assertion attributes (`sub`, `email`, `firstName`, `lastName`,
`country`, `program`, `target`), with `NameID` = email.

---

## 7. Keys, certs and credentials

| | Where | Note |
|---|---|---|
| RSA private key | **Client.Demo only**, in memory | regenerated each restart ⚠️ |
| RSA public key | published as JWKS | fetched by Aspire, cached 10 min by `kid` |
| X509 cert (SAML) | **Client.Demo only**, self-signed | same RSA key; fetched from their metadata, cached 10 min |
| `client_secret` | both sides | authenticates the **caller** (JWT only), does **not** sign |

Both caches **invalidate and retry once on a signature failure** — a client can rotate key material
behind an unchanged `kid`/metadata URL, which a TTL alone would not catch.

Why not a shared HS256 secret? Both parties could then mint a token for **any** customer — no
non-repudiation, and a leak from either side is total impersonation. The onboarding form says
*"shared-secret HS256 should be avoided unless explicitly approved."*

---

## 8. Config contract

Must match on both sides — **case-sensitive, not trimmed**:

| Client.Demo | Aspire.Sso | Mismatch gives |
|---|---|---|
| `Client:Issuer` | `…RegisteredClients[0].Issuer` | `Issuer mismatch` |
| `Client:Aspire:Audience` | `Aspire:Audience` | `Audience mismatch` |
| `Client:Aspire:ClientId` | `…RegisteredClients[0].ClientId` | `Invalid or missing client credentials` |
| `Client:Aspire:ClientSecret` | `…RegisteredClients[0].ClientSecret` | same |
| `Client:SamlEntityId` | `…RegisteredClients[0].SamlEntityId` | SAML `Issuer mismatch` **(JWT unaffected)** |

Aspire-only: `RegisteredClients[0].ReturnUrl` (`democlient://redeemed`) — where to send the customer
on close. Client-only: `SigningKeyId` — Aspire reads `kid` from the token and looks it up, which is
what allows rotation with no Aspire change.

> ⚠️ **`SamlEntityId` is the one field JWT never reads.** It can be wrong while every JWT redeem
> passes — this has already broken once. Test both paths after a config change.

Both services **fail fast at startup** on missing/invalid config, naming the field and why.

---

## 9. Deployment

VPS + **Caddy** (automatic Let's Encrypt). Images are built by CI and pulled by the box — a 1GB VPS
cannot compile .NET without OOMing.

```
push main → CI (34 tests) → images → ghcr.io → VPS pulls → deploy.sh verify
```

| Host | |
|---|---|
| `client.hqauth.shop` | Client.Demo |
| `aspire.hqauth.shop` | Aspire.Sso |
| `demo.hqauth.shop` | Expo web export (static, served by Caddy) |

`DOTNET_gcServer=0` on both — Server GC pre-allocates per-core heaps, pointless on 1 core. Each
service idles at ~16MB.

Caddy sets `X-Forwarded-Proto`, and both services honour it. Without that `req.IsHttps` is false, the
session cookie is issued `Lax`-without-`Secure`, and `launchUrl` comes back `http` — silently
breaking the cross-site handoff. `ForwardedHeadersOptions.KnownNetworks/KnownProxies` must be
**cleared** (they default to loopback only, and Caddy reaches the apps from a docker address).

---

## 10. Web vs native

One codebase, three targets. The native APIs have no web implementation, so:

| Concern | Native | Web |
|---|---|---|
| Session storage | Keychain / Keystore | localStorage (`src/storage.js`) |
| Alerts | `Alert.alert` | `window.alert/confirm` (`src/notify.js`) |
| Reward page opens in | in-app browser sheet | in-page iframe overlay (`src/RewardBrowser.js`) |
| Return to the app | `democlient://redeemed` deep link | `postMessage` |

**Web is demo-only.** localStorage is not secure storage, and the iframe works locally only because
the ports share `localhost` (cookies ignore ports). Across real domains the cookie needs
`SameSite=None; Secure` — which is why Aspire switches on `req.IsHttps` — and a production Aspire
would refuse to be framed at all.

---

## Open questions for the real build

Demo shortcuts (in-memory state, config secrets, hardcoded points) are not listed — they are obvious.
These three are **design decisions that outlive the demo**:

**1. Where does identity come from on Redeem?**
Today `/api/redeem` takes `username` from the request body and trusts it. That is the step where a
human's authentication becomes a cryptographic assertion — the client's real endpoint must read
identity from the caller's **authenticated session**, never from the body. Everything downstream
(RS256, JWKS, `jti`, client credentials) is only as strong as this one check.

**2. Who owns eligibility?**
Aspire has no user directory, so it cannot check whether a customer may redeem — the **client**
gates it (`403` for an inactive user). That means Aspire trusts the client to be the gate. The guide
implies Aspire denies too, which needs a basis: a `status` claim, or an Aspire-side entitlement
lookup by `sub`. **Undecided — raise at onboarding.**

**3. How is the reward deep-linked?**
"Auto login *and can redeem*" only works if the customer lands on **that** reward. We use a `target`
claim/attribute, which is **not in the client-facing contract**. Agree it with the client.

---

## Where things live

```
backend/Client.Demo/          :5001 — the client's system
  Program.cs                  endpoints + startup validation
  ClientOptions.cs            config shape
  ClientKeys.cs               RSA key + X509 cert (PRIVATE — never leave)
  Jwt/JwtIssuer.cs            mints the signed token
  Saml/SamlIdp.cs             signs assertions (XML-DSig, exclusive C14N)
  appsettings.json            issuer, kid, Aspire contract, users, rewards

backend/Aspire.Sso/           :6001 — our SSO
  Program.cs                  endpoints + client authentication + cookie policy
  AspireOptions.cs            config shape
  Jwt/JwtValidator.cs         validates the token
  Jwt/JwksCache.cs            fetches client public keys by kid
  Saml/SamlValidator.cs       validates assertions (XSW guard) + cert cache
  SessionStore.cs             sessions, jti + assertion replay, one-time tickets
  Pages.cs                    Aspire-branded reward / benefit / error pages

mobile/                       the client's app
  App.js                      login gate, Dashboard, Reward, demo switches
  src/api.js                  talks ONLY to :5001
  src/config.js               backend URL (per platform / EXPO_PUBLIC_BACKEND_URL)
  src/storage.js              keystore ↔ localStorage
  src/notify.js               Alert ↔ window.alert
  src/RewardBrowser.js        web-only in-page overlay
  src/theme.js                "Demo Client" tokens (blue)

deploy/                       Caddyfile + docker-compose.yml (run on the VPS)
deploy-vps.sh                 one-time box bootstrap
deploy.sh                     check / urls / verify
test.sh                       34 end-to-end checks
```

See [`RUNNING_AND_TESTING.md`](RUNNING_AND_TESTING.md) to run it.
