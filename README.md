# Omnicasa Mobile SSO — Demo

**Silent SSO on Redeem.** A customer already logged into the client's app taps REDEEM and lands
inside an Aspire reward page, already authenticated — **without ever seeing an Aspire login**.

Implements the JWT path from `Client_JWT_Based_SSO_Implementation_Guide_vFinal 1.docx`.
Two flows only: **login** (client-owned) and **redeem** (the SSO handoff).

---

## What's here

| Path | What it is |
|------|-----------|
| `backend/Client.Demo` | **The client's system** (`:5001`). Own auth + users, rewards, RSA private key, signs the JWT. |
| `backend/Aspire.Sso` | **Our SSO** (`:6001`). Validates client-signed tokens via their JWKS URL, sessions + replay, reward page. No users, no passwords, no signing key. |
| `mobile/` | **Expo / React Native** — the client's app: its own login, and REDEEM triggering the silent handoff. |
| `test.sh` | 34 end-to-end checks |
| `docs/` | Architecture + how to run |

## Architecture in one picture

```
 Demo Client app ─────▶ Client.Demo  :5001              Aspire.Sso  :6001
  (blue, Expo)          ──────────────────              ──────────────────────
                        • own users + login             • NO users / passwords
                        • reward catalogue              • NO signing key
                        • RSA PRIVATE key ──┐           • validates client tokens
                        • signs the JWT     │           • sessions + jti replay
                        • JWKS (public) ◀───┼────────── fetches keys by kid over HTTP
                                            │
                        POST + client_id/secret ──────▶ • crimson reward page
```

Neither can reach the other's internals — `:6001/api/login` and `:5001/benefit` both 404.

## Quick start

```bash
cd backend/Aspire.Sso  && dotnet run    # :6001  — our SSO
cd backend/Client.Demo && dotnet run    # :5001  — the client's system

./test.sh                               # 34 checks

cd mobile && npm install
npx expo start --web        # browser — fastest; or --ios / --android
```

Sign in as `jane` / `demo`, open the **Reward** tab, tap **REDEEM** → the crimson Aspire page opens
on that reward, already signed in. Confirm, then **CLOSE AND RETURN TO THE APP** → the reward shows
**✓ REDEEMED**. The blue→crimson switch *is* the handoff.

## Demo users

| Username | Password | Notes |
|----------|----------|-------|
| `jane`   | `demo`   | TH · FWD_PREMIUM · active |
| `arjun`  | `demo`   | SG · FWD_STANDARD · active |
| `mai`    | `demo`   | VN · **inactive** → login OK, redeem `403` |

## Verified

`./test.sh` — 34 checks:

| | |
|---|---|
| Trust boundary | Aspire has no users · client has no sessions · JWKS is public-only |
| Login | valid · wrong password `401` · unknown user `401` |
| Redeem | happy → `launchUrl` · deep-linked reward · ticket → session · ticket is one-time |
| Negative | expired · wrong audience · tampered signature · bad client secret · inactive `403` · unknown reward `400` |
| Credentials | missing `401` · valid `200` · replayed `jti` `401` |
| Token | all mandatory claims present |

## Docs

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — the two flows, trust boundary, token contract, design decisions, open questions.
- [`docs/RUNNING_AND_TESTING.md`](docs/RUNNING_AND_TESTING.md) — run both services + the app, `./test.sh`, troubleshooting.

---

## Deploy (Render, free tier)

```bash
./deploy.sh check                 # builds all 3, catches problems before Render does
```

1. Push this repo to GitHub.
2. Render → **New +** → **Blueprint** → pick the repo. `render.yaml` declares all three services.
3. Get the env vars for your domain:
   ```bash
   ./deploy.sh urls yourdomain.com     # prints DNS + every env var, generates a secret
   ```
4. Paste them into each service's **Environment**, attach your domains, redeploy.
5. Verify against the live URLs:
   ```bash
   ./deploy.sh verify https://client.yourdomain.com https://aspire.yourdomain.com
   ```

| Service | Is | Public URL |
|---|---|---|
| `client` | Client.Demo (Docker) | `client.yourdomain.com` |
| `aspire` | Aspire.Sso (Docker) | `aspire.yourdomain.com` |
| `demo` | Expo web (static) | `demo.yourdomain.com` |

**Free tier sleeps after ~15 min idle** — the first request takes ~50s. Sessions and keys are
in-memory, so a sleep logs everyone out and rotates the signing key (Aspire re-fetches the JWKS
automatically).

The cookie switches itself: `SameSite=Lax` on local HTTP, `SameSite=None; Secure` over HTTPS —
which the web demo needs, because `demo.` and `aspire.` are cross-site once they are real domains.
