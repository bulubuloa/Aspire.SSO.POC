# Aspire SSO — Demo

**Silent SSO on Redeem.** A customer already logged into the client's app taps REDEEM and lands
inside an Aspire reward page, already authenticated — **without ever seeing an Aspire login**.

Implements **both** paths from the client guides — **JWT** and **SAML 2.0** — switchable at runtime.
Two flows: **login** (client-owned) and **redeem** (the SSO handoff).

**Live:** <https://demo.aspireservice.online>

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

Sign in as `jane` / `demo`, open the **Reward** tab, pick **SSO MODE** (`JWT` or `SAML 2.0`), tap
**REDEEM** → the crimson Aspire page opens on that reward, already signed in. Confirm, then
**CLOSE AND RETURN TO THE APP** → the reward shows **✓ REDEEMED**.

The blue→crimson switch *is* the handoff. The journey is **identical for both protocols** — that is
the point; the reward page shows a `VIA JWT` / `VIA SAML` badge so you can tell which ran.

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

## Deploy

Live at **https://demo.aspireservice.online** (client `client.` · Aspire `aspire.`).

Hosted on a small VPS behind **Caddy** (automatic Let's Encrypt). Images are built by CI and
pulled by the box — a 1GB VPS cannot compile .NET without OOMing.

**One-time, on the VPS:**
```bash
curl -fsSL https://raw.githubusercontent.com/bulubuloa/Aspire.SSO.POC/main/deploy-vps.sh | sudo bash
```
Sets up swap, Docker, ufw, and `/opt/sso/.env` (generates the client secret). Then point three
A records at the box and add `VPS_HOST` / `VPS_USER` / `VPS_SSH_KEY` as GitHub secrets.

**After that it is automatic** — push to `main` → CI runs the 34 tests → images build → the VPS
pulls → `deploy.sh verify` confirms it is live.

```bash
./deploy-vps.sh status     # on the box: containers, memory, endpoint codes
./deploy-vps.sh logs       # tail
./deploy.sh verify https://client.aspireservice.online https://aspire.aspireservice.online
```
