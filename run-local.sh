#!/usr/bin/env bash
# Run both backends locally so the no-build app (http://localhost:5001/) works end to end.
#
#   ./run-local.sh          # plain HTTP — zero setup; the redeem handoff opens in a new tab
#   ./run-local.sh https    # HTTPS — the handoff embeds like the Expo app (in-app iframe)
#
# HTTPS mode needs the .NET dev cert trusted ONCE (no install — it ships with the SDK):
#   dotnet dev-certs https --trust
#
# Stop everything with Ctrl+C.
set -euo pipefail
cd "$(dirname "$0")"

MODE="${1:-http}"
if [ "$MODE" = "https" ]; then
  SCHEME=https
  # Every cross-service URL must match the scheme, or the browser/back-channel mixes http+https.
  export Aspire__RegisteredClients__0__JwksUrl="https://localhost:5001/.well-known/jwks.json"
  export Aspire__RegisteredClients__0__SamlMetadataUrl="https://localhost:5001/saml/metadata"
  export Client__Aspire__SsoEndpoint="https://localhost:6001/sso/jwt"
  export Client__Aspire__SamlAcsUrl="https://localhost:6001/sso/saml/acs"
  if ! dotnet dev-certs https --check >/dev/null 2>&1; then
    echo "⚠️  HTTPS dev cert not trusted yet. Run once:  dotnet dev-certs https --trust"
    echo "    (it's built into the .NET SDK — nothing to install)"; exit 1
  fi
else
  SCHEME=http
fi

export ASPNETCORE_ENVIRONMENT=Development
pids=()
cleanup(){ echo; echo "stopping…"; for p in "${pids[@]}"; do kill "$p" 2>/dev/null || true; done; }
trap cleanup INT TERM EXIT

echo "▶ Aspire.Sso  → $SCHEME://localhost:6001"
( cd backend/Aspire.Sso  && exec dotnet run --urls "$SCHEME://localhost:6001" ) & pids+=($!)
echo "▶ Client.Demo → $SCHEME://localhost:5001   ← open this one"
( cd backend/Client.Demo && exec dotnet run --urls "$SCHEME://localhost:5001" ) & pids+=($!)

echo
echo "Open  $SCHEME://localhost:5001/   (login jane / demo)"
[ "$MODE" = https ] && echo "Redeem embeds in-app (like Expo)." || echo "Redeem opens in a new tab (HTTP can't embed the login cookie)."
wait
