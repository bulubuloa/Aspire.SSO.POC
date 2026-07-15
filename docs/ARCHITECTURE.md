# Architecture

A demo of **silent SSO on Redeem**: a customer already logged into the client's app taps REDEEM and
lands inside an Aspire reward page, already authenticated — **without ever seeing an Aspire login**.

Two flows only: **login** (client-owned) and **redeem** (the SSO handoff).

Implements the JWT path from `Client_JWT_Based_SSO_Implementation_Guide_vFinal 1.docx`.

---

## 1. The parts

```
        ┌──────────── CLIENT'S TRUST DOMAIN ────────────┐   ┌──── ASPIRE'S ────┐

          Demo Client app          Client.Demo  :5001        Aspire.Sso  :6001
          (Expo / React Native)    (ASP.NET Core 10)         (ASP.NET Core 10)
          ─────────────────────    ────────────────────      ──────────────────
          • login screen           • users + auth            • NO users, NO passwords
          • rewards list           • reward catalogue        • NO signing key
          • REDEEM        ──①──▶   • RSA PRIVATE key ──┐     • validates client tokens
          • opens web-view         • mints + signs JWT │     • session + jti replay store
                    │              • publishes JWKS ◀──┼──── • fetches public keys by kid
                    │                                  └─②─▶ • reward page
                    └──────────────────③──────────────────▶  • issues client credentials
                       (one-time ticket — no token)

        └───────────────────────────────────────────────┘   └──────────────────┘
```

| Component | Stack | Owns |
|---|---|---|
| **Demo Client app** | Expo / React Native | UI only. No key, no token. |
| **Client.Demo** `:5001` | ASP.NET Core 10 | Users, auth, rewards, **private key**, signing |
| **Aspire.Sso** `:6001` | ASP.NET Core 10 | Validation, sessions, replay, reward page |

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
| Public keys | publishes at `/.well-known/jwks.json` | fetches by `kid` over HTTP |
| Sessions / replay | ❌ none | ✅ owns |
| Client credentials | holds the secret it was issued | issues + verifies |

**Aspire never learns the customer's password.** It receives an *assertion about* them, signed by
someone it has agreed to trust. That is the whole model.

---

## 3. Flow ① — Login (client-owned)

Aspire is not involved. No token is minted; nothing touches `:6001`.

```
app → POST :5001/api/login  {"username":"jane","password":"demo"}
    ← 200 {"username","sub","email","displayName","country","program","active"}
    ← 401 {"error":"Invalid credentials"}
app → stores the profile in the OS keystore ('client_session'), gate opens
```

⚠️ The response is a **profile, not a session token** — the server issues nothing it can later
verify. See *Open questions* below.

---

## 4. Flow ② — Redeem (the silent handoff)

```
① app   → POST :5001/api/redeem {"username":"jane","rewardId":"lounge"}

② :5001 → mints RS256 JWT with its PRIVATE key, then server-to-server:
             POST :6001/sso/jwt
             Authorization: Basic base64(client_id:client_secret)
             {"token":"<jwt>"}
   :6001 → fetch client JWKS by kid → verify sig, iss, aud, exp, iat → consume jti
         → create session → issue one-time 60s ticket
   :5001 ← {"ok":true,"launchUrl":"…?ticket=…"}
   app   ← {"launchUrl":"…","reward":"Airport lounge pass"}

③ app   → opens launchUrl in a web-view
   :6001 → 302, swaps ticket for a session cookie → reward page, already signed in
         → CONFIRM REDEMPTION → REDEEMED receipt
④ close → Aspire hands the customer back to the client's registered ReturnUrl
   app   → closes the web-view, marks the reward ✓ REDEEMED
```

| Hop | From → To | Carries | Seen by the user? |
|---|---|---|---|
| ① | app → Client.Demo | `rewardId` | no |
| ② | **Client.Demo → Aspire.Sso** | signed JWT + client credentials | **no — back-channel** |
| ③ | app's web-view → Aspire.Sso | one-time ticket | yes — the reward page opens |
| ④ | Aspire.Sso → the app | `ReturnUrl` deep link (native) / `postMessage` (web) | yes — the app updates |

**The app never holds a token.** The JWT exists only between the two servers in ②. The ticket in ③
is worthless alone — that's what keeps the token out of browser history, proxies and the URL bar.

### Redeem errors

| Case | Status | Body |
|---|---|---|
| Unknown user | `401` | `{"error":"Not authenticated"}` |
| Inactive user | `403` | `{"error":"User is inactive / not authorised"}` |
| Unknown reward | `400` | `{"error":"Unknown reward"}` |
| Token rejected by Aspire | `401` | Aspire's reason — e.g. `{"error":"Token expired"}` |
| Aspire unreachable | `502` | `{"error":"Aspire SSO is unreachable"}` |

---

## 5. Endpoints

### Client.Demo `:5001`

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/login` | client-owned auth |
| GET | `/api/rewards` | reward catalogue |
| POST | `/api/redeem` | **the silent handoff** — signs + calls Aspire |
| GET | `/.well-known/jwks.json` | **public** keys, for Aspire |
| POST | `/api/jwt` | demo-only — raw token so `test.sh` can call Aspire directly |

The app registers `democlient://` as its URL scheme (`app.json`), which is what lets Aspire hand the
customer back in step ④.

### Aspire.Sso `:6001`

| Method | Path | Purpose |
|---|---|---|
| POST | `/sso/jwt` | **the SSO endpoint** — validate + create session |
| GET | `/benefit?ticket=` | redeem ticket → cookie → reward page |
| GET | `/api/session` | inspect the current session |
| POST | `/logout` | end it |

---

## 6. The token

Minted by `Client.Demo/JwtIssuer.cs`, validated by `Aspire.Sso/JwtValidator.cs`.

```json
// header
{ "alg": "RS256", "kid": "client-demo-key-2026-015", "typ": "JWT" }

// payload
{
  "sub": "C123456789", "email": "jane.tan@client-demo.com",
  "given_name": "Jane", "family_name": "Tan",
  "country": "TH", "program": "FWD_PREMIUM",
  "jti": "<uuid>", "iat": …, "nbf": …, "exp": …,     // exp = iat + 120s
  "iss": "https://client.demo",
  "aud": "aspire-lifestyle-sso-demo-env",
  "target": "lounge"                                  // ⚠️ ours, not in the contract yet
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

### What Aspire checks

1. Reads `kid` from the header.
2. Fetches the client's **public** keys from their JWKS URL (cached 10 min by `kid`).
3. Verifies the signature, **pinned to RS256** — no `alg:none`, no HS256 downgrade.
4. `iss`, `aud`, `exp`/`nbf`/`iat` (±30s skew), mandatory claims.
5. Consumes the `jti` — a second use is rejected.
6. Creates a session carrying `target`, issues a one-time ticket.

---

## 7. Keys and credentials

| | Where | Note |
|---|---|---|
| RSA private key | **Client.Demo only**, in memory | regenerated each restart ⚠️ |
| RSA public key | published as JWKS | fetched by Aspire, cached 10 min by `kid` |
| `client_secret` | both sides' config | authenticates the **caller**, does **not** sign |

**Two independent checks** on hop ②:

| | Proves | Mechanism |
|---|---|---|
| `Authorization: Basic` | **who is calling** | `base64(client_id:client_secret)`, issued by Aspire |
| the JWT signature | **who the user is** | client's own RS256 private key |

Why not a shared HS256 secret? Both parties could then mint a token for **any** customer — no
non-repudiation, and a leak from either side is total impersonation. The onboarding form says
*"shared-secret HS256 should be avoided unless explicitly approved."*

`JwksCache` resolves by `kid`, re-fetches on an unknown `kid` (≤30s anti-hammer delay), and
**invalidates + retries once on a signature failure** — because a client can rotate key material
behind the *same* `kid`, which a TTL alone would not catch.

---

## 8. Config contract

Four fields must match on both sides — **case-sensitive, not trimmed**:

| Client.Demo | Aspire.Sso | Mismatch gives |
|---|---|---|
| `Client:Issuer` | `…RegisteredClients[0].Issuer` | `Issuer mismatch` |
| `Client:Aspire:Audience` | `Aspire:Audience` | `Audience mismatch` |
| `Client:Aspire:ClientId` | `…RegisteredClients[0].ClientId` | `Invalid or missing client credentials` |
| `Client:Aspire:ClientSecret` | `…RegisteredClients[0].ClientSecret` | same |

Aspire also registers the client's **`ReturnUrl`** (`democlient://redeemed`) — where to send the
customer when they close the reward page. It has no counterpart on the client side; it is Aspire's
record of the client's deep link, agreed at onboarding.

`SigningKeyId` is **client-only** — Aspire reads `kid` from the token header and looks it up in the
JWKS. Change it freely; that's what makes rotation possible with no Aspire change.

Both services **fail fast at startup** on missing/invalid config, naming the field and why.

---

## 9. Design decisions

| Decision | Why |
|---|---|
| Signing on the client's **server**, not the app | A key in an APK/IPA will be extracted → anyone could mint a token for any `sub` |
| Two services, not one | The trust boundary is real, not a convention. Sharing a process hid an authorization bug |
| Back-channel POST, not a redirect | Guide §5: token-in-URL is "not preferred" — URLs get logged |
| One-time launch ticket | Lets the web-view open Aspire without the JWT in the URL |
| 120s expiry + `jti` | Guide §4: short-lived, one-time-use |
| Credentials separate from signing | Two independent facts: *who calls* vs *who the user is* |

---

## Open questions for the real build

Demo shortcuts (in-memory state, HTTP, config secrets, hardcoded points) are not listed — they are
obvious. These three are **design decisions that outlive the demo**:

**1. Where does identity come from on Redeem?**
Today `/api/redeem` takes `username` from the request body and trusts it. That is fine for a demo,
but it is the step where a human's authentication becomes a cryptographic assertion — the client's
real endpoint must read identity from the caller's **authenticated session**, never from the body.
Everything downstream (RS256, JWKS, `jti`, client credentials) is only as strong as this one check.

**2. Who owns eligibility?**
Aspire has no user directory, so it cannot check whether a customer may redeem — the **client**
gates it (`403` for an inactive user). That means Aspire trusts the client to be the gate. The guide
implies Aspire denies too, which needs a basis: a `status` claim, or an Aspire-side entitlement
lookup by `sub`. **Undecided — raise at onboarding.**

**3. How is the reward deep-linked?**
"Auto login *and can redeem*" only works if the customer lands on **that** reward. We use a `target`
claim, which is **not in the client-facing claim contract**. Agree it with the client — as a claim,
or as a parameter alongside the token.

---

## Where things live

```
backend/Client.Demo/          :5001 — the client's system
  Program.cs                  endpoints + startup validation
  ClientOptions.cs            config shape
  ClientKeys.cs               RSA key (PRIVATE — never leaves)
  JwtIssuer.cs                mints the signed token
  appsettings.json            issuer, kid, Aspire contract, users, rewards

backend/Aspire.Sso/           :6001 — our SSO
  Program.cs                  endpoints + client authentication
  AspireOptions.cs            config shape
  JwtValidator.cs             validates the token
  JwksCache.cs                fetches client public keys by kid
  SessionStore.cs             sessions, replay, one-time tickets
  Pages.cs                    Aspire-branded reward / error pages
  appsettings.json            audience, registered clients, rewards

mobile/                       the client's app
  App.js                      login gate, Dashboard, Reward
  src/api.js                  talks ONLY to :5001
  src/config.js               backend URL (auto-selects per platform)
  src/theme.js                "Demo Client" tokens (blue)

test.sh                       34 end-to-end checks
```

See [`RUNNING_AND_TESTING.md`](RUNNING_AND_TESTING.md) to run it.
