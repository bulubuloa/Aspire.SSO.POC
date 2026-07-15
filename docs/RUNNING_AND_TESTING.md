# Running & Testing

Two flows: **login** and **redeem**. See [`ARCHITECTURE.md`](ARCHITECTURE.md) for how they work.

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

34 checks — trust boundary, login, the redeem handoff, negative cases, token contract:

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
───────────────────────────────────────────────────────
  34 passed
```

Exits non-zero on failure, so it works in CI. Override hosts with `CLIENT_URL` / `ASPIRE_URL`.

---

## 3. Run the app

```bash
cd mobile
npm install

npx expo start --web   # browser  → http://localhost:8081  (fastest to test)
npx expo start --ios    # then press i
npx expo start --android
```

The app talks **only** to `:5001`. It never contacts Aspire directly.

### Testing in a browser

`--web` is the quickest way to run the whole demo — no simulator needed.

| | Native | Web |
|---|---|---|
| Session storage | Keychain / Keystore | **localStorage** (`src/storage.js`) |
| Alerts | `Alert.alert` | **`window.alert/confirm`** (`src/notify.js`) |
| Reward page opens in | in-app browser sheet | **in-page overlay + iframe** (`src/RewardBrowser.js`) |
| Returning to the app | deep link `democlient://redeemed` | **`postMessage`** to the parent |

Those shims exist because the native APIs have no web implementation — `expo-secure-store` throws
`_ExpoSecureStore.default.setValueWithKeyAsync is not a function`, and react-native-web's `Alert`
silently does nothing.

> **Web is demo-only.** localStorage is not secure storage, and the iframe only works because
> `:8081` and `:6001` are both `localhost` — cookies ignore the port, so the `SameSite=Lax` session
> cookie counts as same-site. Across real domains it would be blocked, and a production Aspire would
> refuse to be framed at all (`X-Frame-Options`). Native has neither problem.

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
4. **REDEEM** on any offer → the **crimson Aspire page** opens on *that* reward, already signed in
5. **CONFIRM REDEMPTION** → `REDEEMED — Confirmation sent to jane.tan@client-demo.com`
6. **CLOSE AND RETURN TO THE APP** → you land back in the app and that reward now shows **✓ REDEEMED**

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

### The silent handoff

```bash
curl -X POST http://localhost:5001/api/redeem \
  -H 'Content-Type: application/json' \
  -d '{"username":"jane","rewardId":"lounge"}'
# → {"launchUrl":"http://localhost:6001/benefit?ticket=…","reward":"Airport lounge pass"}
```

Open that `launchUrl` in a browser → the Aspire reward page, already signed in.

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
| <http://localhost:5001/.well-known/jwks.json> | the client's **public** key |
| <http://localhost:6001/api/session> | current session (401 without one) |

---

## 5. Config changes

Four fields must match on **both** sides — case-sensitive, not trimmed:

```bash
python3 - <<'PY'
import json
c = json.load(open("backend/Client.Demo/appsettings.json"))["Client"]
a = json.load(open("backend/Aspire.Sso/appsettings.json"))["Aspire"]
r = a["RegisteredClients"][0]
for k, cv, av in [("Issuer",       c["Issuer"],                 r["Issuer"]),
                  ("Audience",     c["Aspire"]["Audience"],     a["Audience"]),
                  ("ClientId",     c["Aspire"]["ClientId"],     r["ClientId"]),
                  ("ClientSecret", c["Aspire"]["ClientSecret"], r["ClientSecret"])]:
    print(f"{'OK  ' if cv==av else 'MISMATCH'} {k:<14} {cv}")
PY
```

Then run `./test.sh`.

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
| App can't reach the backend | `localhost` on a device = the device. See §3 |
| Startup: `Invalid configuration: …` | Working as intended — it names the missing field |

---

## 7. Reset

```bash
pkill -f "Client.Demo|Aspire.Sso"     # stop both
```

Sessions, replay state and keys are all in memory — restarting clears everything. Restarting
`Client.Demo` **regenerates its signing key**, so Aspire's cached JWKS goes stale for one request;
it invalidates and retries automatically.
