# Running & Testing

Two flows: **login** and **redeem**. Redeem runs over **JWT or SAML** — switchable in the app.
See [`ARCHITECTURE.md`](ARCHITECTURE.md) for how they work.

**Live:** <https://demo.hqauth.shop>

## Prerequisites

| | Check |
|---|---|
| .NET SDK 10 | `dotnet --version` → `10.x` |
| Node 18+ | `node --version` |
| iOS simulator | Xcode installed |
| Android emulator | `~/Library/Android/sdk/emulator/emulator -list-avds` |
| or a phone | Expo Go from the App/Play Store |

---

## 1. Run the backends

**Two separate services.** Both must be up.

```bash
# terminal 1 — Aspire (our SSO)
cd backend/Aspire.Sso && dotnet run      # :6001

# terminal 2 — the client's system
cd backend/Client.Demo && dotnet run     # :5001
```

Start order doesn't matter. Each generates its own key at boot; Aspire fetches the client's
**public** key the first time it sees a token.

Sanity check:

```bash
curl http://localhost:5001/api/rewards            # → 12 rewards
curl http://localhost:5001/.well-known/jwks.json  # → the client's public key
```

Both **fail fast** on bad config, naming the field:

```
Invalid configuration:
  Client:Aspire:ClientSecret is required — issued by Aspire; keep it out of source control
```

> Running `dotnet <dll>` from another directory makes *that* directory the content root, so
> `appsettings.json` isn't found. Run from the project folder, or use `dotnet run`.

---

## 2. Run the automated tests

```bash
./test.sh
```

49 checks — trust boundary, login, the redeem handoff, negative cases, token contract, **and the
full SAML path** (metadata, signed assertion → session, replay, tampered, wrong-aud, expired).

```
── trust boundary ─────────────────────────────────────
  ✓ Aspire has no user directory     ✓ client has no session store
  ✓ client publishes PUBLIC jwks
── login (client-owned) ───────────────────────────────
  ✓ valid credentials    ✓ wrong password → 401    ✓ unknown user → 401
── redeem: the silent handoff ─────────────────────────
  ✓ happy path → launchUrl           ✓ ticket → Aspire session
  ✓ …deep-linked reward              ✓ ticket is one-time
── negative cases ─────────────────────────────────────
  ✓ expired token        ✓ tampered signature   ✓ inactive user → 403
  ✓ wrong audience       ✓ bad client secret    ✓ unknown reward → 400
── Aspire SSO endpoint, direct ────────────────────────
  ✓ no credentials → 401  ✓ with credentials → ok  ✓ replay same jti → 401
── token contract ─────────────────────────────────────
  ✓ claim 'sub' present … ✓ member_id NOT sent (unused)
── SAML ───────────────────────────────────────────────
  ✓ IdP + SP metadata     ✓ signed assertion → session (via SAML)
  ✓ form POSTs to real ACS  ✓ replay/tampered/wrong-aud/expired rejected
───────────────────────────────────────────────────────
  49 passed
```

Exits non-zero on failure, so it works in CI. Override hosts with `CLIENT_URL` / `ASPIRE_URL`.
The credential and replay checks build the `Authorization` header with `curl -u`, not a hand-rolled
`base64` — GNU `base64` wraps at 76 chars and a newline in the header silently broke CI once.

---

## 3. Run the app

Two ways to see the same demo. Pick whichever fits — they hit the same two backends.

### A. No-build web (dotnet only — no npx, no install)

For anyone who can run the backends but not the Node/Expo toolchain. The client's own backend
serves a single HTML page (`backend/Client.Demo/wwwroot/index.html`) — nothing to build.

```bash
./run-local.sh https     # runs BOTH backends over HTTPS, then open https://localhost:5001/
# or
./run-local.sh           # plain HTTP → open http://localhost:5001/
```

Then open the printed URL, sign in `jane` / `demo`, and redeem. That's the whole demo.

The redeem handoff adapts to the scheme:

| Served over | Redeem opens the Aspire page… | Why |
|---|---|---|
| **HTTPS** (`run-local.sh https`) | **embedded in-app**, like the Expo app | cookie is `SameSite=None; Secure` → survives inside an iframe |
| **HTTP** (`run-local.sh`) | in a **new browser tab** | over HTTP the cookie can only be `SameSite=Lax`, which a cross-origin iframe drops |

HTTPS mode needs the .NET dev cert trusted **once** — it ships with the SDK, nothing to download:

```bash
dotnet dev-certs https --trust
```

> In Rider: Run both `Aspire.Sso` and `Client.Demo`, then open `localhost:5001`. For the embedded
> HTTPS experience, run each on its HTTPS profile and set the four cross-service URLs to `https://…`
> (`Client__Aspire__SsoEndpoint`, `Client__Aspire__SamlAcsUrl`,
> `Aspire__RegisteredClients__0__JwksUrl`, `Aspire__RegisteredClients__0__SamlMetadataUrl`).
> `run-local.sh https` does exactly this from the terminal.

### B. Expo app (React Native — iOS / Android / web)

The full mobile app. Needs Node + the Expo toolchain.

```bash
cd mobile
npm install

npx expo start --web    # browser  → http://localhost:8081
npx expo start --ios     # then press i
npx expo start --android
```

Both apps talk **only** to `:5001`. Neither ever contacts Aspire directly.

#### Expo web shims

| | Native | Web |
|---|---|---|
| Session storage | Keychain / Keystore | **localStorage** (`src/storage.js`) |
| Alerts | `Alert.alert` | **`window.alert/confirm`** (`src/notify.js`) |
| Reward page opens in | in-app browser sheet | **in-page overlay + iframe** (`src/RewardBrowser.js`) |
| Returning to the app | deep link `democlient://redeemed` | **`postMessage`** to the parent |

Those shims exist because the native APIs have no web implementation — `expo-secure-store` throws
`_ExpoSecureStore.default.setValueWithKeyAsync is not a function`, and react-native-web's `Alert`
silently does nothing.

> **Web (either app) is demo-only.** The embedded iframe needs the app and Aspire to be same-site:
> that's automatic on `localhost` over HTTP (cookies ignore the port), and over HTTPS the cookie
> goes `SameSite=None; Secure`. Across real domains a production Aspire would refuse to be framed at
> all (`X-Frame-Options`). Native has neither constraint.

### Networking — the one thing to get right

A phone/emulator cannot reach `localhost` — that's the device itself. `src/config.js` auto-selects:

| Target | Host |
|---|---|
| iOS simulator | `localhost:5001` |
| Android emulator | `10.0.2.2:5001` (alias for your Mac) |
| Physical device | set `LAN_IP` in `src/config.js` |

Find your LAN IP: `ipconfig getifaddr en0`. The backends already bind `0.0.0.0`.

### Walkthrough

1. **Sign in** — `jane` / `demo` (the client's own login; Aspire not involved)
2. **Dashboard** — user card, weather, news
3. **Reward** tab — 3 featured cards + 9 offers
4. **SSO MODE** — flip between `JWT` and `SAML 2.0` (top of the Reward tab)
5. **REDEEM** on any offer → the **crimson Aspire page** opens on *that* reward, already signed in
6. **CONFIRM REDEMPTION** → `REDEEMED — Confirmation sent to jane.tan@client-demo.com`
7. **CLOSE AND RETURN TO THE APP** → back in the app, that reward now shows **✓ REDEEMED**

The blue→crimson switch **is** the handoff. No Aspire login appears at any point.

Step 6 is the return path: Aspire only knows where to send the customer because a `ReturnUrl`
(`democlient://redeemed`) is registered against the client — part of the onboarding contract, not
something the app passes at runtime.

Try `mai` / `demo` — she logs in fine but Redeem returns *User is inactive / not authorised*.

| User | Password | |
|---|---|---|
| `jane` | `demo` | TH · FWD_PREMIUM · active |
| `arjun` | `demo` | SG · FWD_STANDARD · active |
| `mai` | `demo` | VN · **inactive** → redeem 403 |

---

## 4. Manual checks

### The silent handoff — JWT

```bash
curl -X POST http://localhost:5001/api/redeem \
  -H 'Content-Type: application/json' \
  -d '{"username":"jane","rewardId":"lounge","mode":"jwt"}'
# → {"launchUrl":"http://localhost:6001/benefit?ticket=…","via":"JWT"}
```

Open that `launchUrl` in a browser → the Aspire reward page, already signed in.

### The handoff — SAML

```bash
curl -X POST http://localhost:5001/api/redeem \
  -H 'Content-Type: application/json' \
  -d '{"username":"jane","rewardId":"lounge","mode":"saml"}'
# → {"launchUrl":"http://localhost:5001/saml/sso?code=…","via":"SAML"}
```

Note the launchUrl points at **the client's own IdP**, not Aspire. Open it in a browser: it signs an
assertion and auto-POSTs it to Aspire's ACS, which validates and redirects to the reward page.

**SAML needs a real browser** — curl will not follow the auto-POST form. To drive it headlessly you
have to extract `SAMLResponse` from the form and POST it yourself:

```bash
LU=$(curl -s -X POST http://localhost:5001/api/redeem -H 'Content-Type: application/json' \
  -d '{"username":"jane","rewardId":"lounge","mode":"saml"}' \
  | python3 -c "import sys,json;print(json.load(sys.stdin)['launchUrl'])")
SR=$(curl -s "$LU" | grep -oE 'value="[^"]+"' | sed 's/value="//;s/"//')
curl -s -c /tmp/j -o /dev/null -X POST http://localhost:6001/sso/saml/acs --data-urlencode "SAMLResponse=$SR"
curl -s -b /tmp/j http://localhost:6001/api/session      # → "via":"SAML"
```

### SAML metadata — what you would exchange with a real client

| URL | |
|---|---|
| <http://localhost:5001/saml/metadata> | client IdP Entity ID + **public** signing certificate |
| <http://localhost:6001/sso/saml/metadata> | Aspire SP Entity ID + ACS URL |

### Watch the real key exchange

Restart Aspire, redeem, and watch its log:

```
Fetched 1 key(s) from http://localhost:5001/.well-known/jwks.json
```

That's Aspire pulling the client's **public** key over HTTP — not a shared object.

### Prove the services are separate

```bash
curl -o /dev/null -w "%{http_code}\n" http://localhost:6001/api/login   # 404 — Aspire has no users
curl -o /dev/null -w "%{http_code}\n" http://localhost:5001/benefit     # 404 — client has no sessions
```

### Inspect the token

```bash
curl -s -X POST http://localhost:5001/api/jwt \
  -H 'Content-Type: application/json' -d '{"username":"jane"}' \
| python3 -c "
import sys,json,base64
t=json.load(sys.stdin)['token']; h,p=t.split('.')[:2]
d=lambda x: json.loads(base64.urlsafe_b64decode(x+'='*(-len(x)%4)))
print(json.dumps(d(h),indent=2)); print(json.dumps(d(p),indent=2))"
```

### Useful URLs

| URL | What |
|---|---|
| <http://localhost:5001/.well-known/jwks.json> | the client's **public** key (JWT) |
| <http://localhost:5001/saml/metadata> | the client's IdP metadata + cert (SAML) |
| <http://localhost:6001/sso/saml/metadata> | Aspire's SP metadata (SAML) |
| <http://localhost:6001/api/session> | current session (401 without one) — shows `via` |

---

## 5. Config changes

Five fields must match on **both** sides — case-sensitive, not trimmed:

```bash
python3 - <<'PY'
import json
c = json.load(open("backend/Client.Demo/appsettings.json"))["Client"]
a = json.load(open("backend/Aspire.Sso/appsettings.json"))["Aspire"]
r = a["RegisteredClients"][0]
for k, cv, av in [("Issuer",       c["Issuer"],                 r["Issuer"]),
                  ("Audience",     c["Aspire"]["Audience"],     a["Audience"]),
                  ("ClientId",     c["Aspire"]["ClientId"],     r["ClientId"]),
                  ("ClientSecret", c["Aspire"]["ClientSecret"], r["ClientSecret"]),
                  ("SamlEntityId", c["SamlEntityId"],           r["SamlEntityId"])]:
    print(f"{'OK  ' if cv==av else 'MISMATCH'} {k:<14} {cv}")
PY
```

Then run `./test.sh` **and** a manual SAML redeem — `SamlEntityId` is the one field JWT never
reads, so it can be wrong while every JWT test passes. That has already broken once.

`SigningKeyId` is client-only: Aspire reads `kid` from the token header and looks it up in the JWKS.
Change it freely — no Aspire change needed. Allow ~30s for the JWKS cache to re-fetch.

Override per environment instead of editing files:

```bash
export Client__Aspire__ClientSecret=sk_prod_xxxxx
export Aspire__RegisteredClients__0__ClientSecret=sk_prod_xxxxx
```

---

## 6. Troubleshooting

| Symptom | Cause |
|---|---|
| `Issuer mismatch` | `Issuer` differs between the two configs — **case-sensitive, not trimmed** |
| `Audience mismatch` | `Aspire:Audience` differs |
| `Invalid or missing client credentials` | `ClientId`/`ClientSecret` differ, or a back-channel caller sent none |
| `Invalid signature` | Client restarted → new key under the same `kid`. Aspire self-heals (invalidate + retry) |
| `No key '…' in the client's JWKS` | `kid` changed; wait ~30s for the refetch floor |
| `Aspire SSO is unreachable` (502) | `:6001` is down, or `SsoEndpoint` is wrong |
| `Token expired` on every redeem | Clock skew > 30s between machines |
| App shows *Guest* after login | Session lost; the profile lives in the OS keystore |
| SAML `Issuer mismatch`, JWT fine | `SamlEntityId` differs between the two configs |
| SAML redirects to a 404 | Aspire serves `/benefit`, not `/aspire/*` — a stale monolith path |
| App can't reach the backend | `localhost` on a device = the device. See §3 |
| No-build web: "Signing you in… forever" / "No Aspire session" | Ran over **HTTP** — the iframe can't carry the `SameSite=Lax` cookie. Use `./run-local.sh https`, or accept the new-tab fallback |
| No-build web: HTTPS won't start | Dev cert not trusted — run `dotnet dev-certs https --trust` once |
| Startup: `Invalid configuration: …` | Working as intended — it names the missing field |

---

## 7. Deployment

CI deploys on every push to `main` — see [`ARCHITECTURE.md` §9](ARCHITECTURE.md#9-deployment).

```bash
./deploy.sh check                      # build everything locally first
./deploy.sh urls hqauth.shop  # env vars for a domain
CLIENT_SECRET=… ./deploy.sh verify https://client.hqauth.shop https://aspire.hqauth.shop
```

`verify` checks the two things that only fail in production — `launchUrl` comes back `https`
(forwarded headers) and the cookie is `SameSite=None; Secure` (cross-site iframe) — then runs the
full suite against live. **The deployed secret is not in `appsettings.json`**, so pass
`CLIENT_SECRET` or those checks use the fake demo value.

On the box:

```bash
./deploy-vps.sh status    # containers, memory, endpoint codes
./deploy-vps.sh logs      # tail
```

---

## 8. Reset

```bash
pkill -f "Client.Demo|Aspire.Sso"     # stop both
```

Sessions, replay state and keys are all in memory — restarting clears everything. Restarting
`Client.Demo` **regenerates its signing key**, so Aspire's cached JWKS goes stale for one request;
it invalidates and retries automatically.
