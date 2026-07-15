#!/usr/bin/env bash
#
# Deploy the SSO demo to Render (free tier), or verify a deployment.
#
#   ./deploy.sh check      — validate everything before you push (no network)
#   ./deploy.sh urls       — print the env vars to set, given your domain
#   ./deploy.sh verify     — run the full test suite against the live URLs
#
# Render itself deploys from git: push this repo, then New + → Blueprint → pick it.
# render.yaml declares all three services. This script handles everything around that.
#
set -uo pipefail
cd "$(dirname "$0")"

BOLD=$'\033[1m'; GREEN=$'\033[32m'; RED=$'\033[31m'; YELLOW=$'\033[33m'; OFF=$'\033[0m'
ok(){   printf "  ${GREEN}✓${OFF} %s\n" "$1"; }
bad(){  printf "  ${RED}✗${OFF} %s\n" "$1"; FAILED=1; }
warn(){ printf "  ${YELLOW}!${OFF} %s\n" "$1"; }
FAILED=0

# ── check ─────────────────────────────────────────────────────────────────────
cmd_check() {
  echo "${BOLD}Pre-flight${OFF}"

  [ -f render.yaml ] && ok "render.yaml present" || bad "render.yaml missing"
  for f in backend/Client.Demo/Dockerfile backend/Aspire.Sso/Dockerfile; do
    [ -f "$f" ] && ok "$f" || bad "$f missing"
  done

  # Both services must build before Render wastes 5 minutes discovering they don't.
  for p in backend/Client.Demo backend/Aspire.Sso; do
    if (cd "$p" && dotnet build -c Release 2>&1 | grep -q "Build succeeded"); then
      ok "$(basename $p) builds"
    else
      bad "$(basename $p) does NOT build — fix before deploying"
    fi
  done

  # The web export is what Render will run for the static site.
  if (cd mobile && CI=1 npx expo export --platform web --output-dir /tmp/_deploy_check >/dev/null 2>&1); then
    ok "mobile web export builds"; rm -rf /tmp/_deploy_check
  else
    bad "mobile web export FAILS — 'cd mobile && npx expo export --platform web' to see why"
  fi

  echo
  echo "${BOLD}Secrets${OFF}"
  # The demo secret is committed. Harmless (it is fake) but it must not be the one used live.
  if grep -q "sk_uat_9f3c1e7a842b4d05a6e8c2b71d904f36" backend/*/appsettings.json 2>/dev/null; then
    warn "the demo ClientSecret is in appsettings.json — override it per environment:"
    echo "      Client__Aspire__ClientSecret / Aspire__RegisteredClients__0__ClientSecret"
    echo "      generate one:  openssl rand -hex 24"
  fi
  grep -q "sync: false" render.yaml && ok "render.yaml marks secrets as sync:false (never committed)"

  echo
  [ $FAILED -eq 0 ] && echo "${GREEN}Ready to deploy.${OFF}" || echo "${RED}Fix the above first.${OFF}"
  return $FAILED
}

# ── urls ──────────────────────────────────────────────────────────────────────
cmd_urls() {
  local domain="${1:-}"
  if [ -z "$domain" ]; then
    echo "Usage: ./deploy.sh urls <yourdomain.com>"
    echo "   or: ./deploy.sh urls onrender   # use Render's default *.onrender.com hostnames"
    return 1
  fi

  local client aspire demo
  if [ "$domain" = "onrender" ]; then
    client="https://client.onrender.com"; aspire="https://aspire.onrender.com"; demo="https://demo.onrender.com"
    warn "Render appends a suffix to free hostnames — copy the real ones from the dashboard."
  else
    client="https://client.$domain"; aspire="https://aspire.$domain"; demo="https://demo.$domain"
  fi

  local secret; secret="$(openssl rand -hex 24 2>/dev/null || echo 'run: openssl rand -hex 24')"

  cat <<EOF

${BOLD}DNS${OFF} — three CNAMEs to Render (values come from each service's Settings → Custom Domain)
  client.$domain
  aspire.$domain
  demo.$domain

${BOLD}Environment variables${OFF}

  ${BOLD}aspire${OFF}
    Aspire__RegisteredClients__0__JwksUrl      $client/.well-known/jwks.json
    Aspire__RegisteredClients__0__ClientSecret $secret
    Aspire__RegisteredClients__0__ReturnUrl    democlient://redeemed

  ${BOLD}client${OFF}
    Client__Aspire__SsoEndpoint                $aspire/sso/jwt
    Client__Aspire__ClientSecret               $secret     ${YELLOW}← must match aspire's${OFF}

  ${BOLD}demo${OFF} (build-time)
    EXPO_PUBLIC_BACKEND_URL                    $client

${BOLD}Then${OFF}
  ./deploy.sh verify $client $aspire

EOF
}

# ── verify ────────────────────────────────────────────────────────────────────
cmd_verify() {
  local client="${1:-}" aspire="${2:-}"
  if [ -z "$client" ] || [ -z "$aspire" ]; then
    echo "Usage: ./deploy.sh verify https://client.example.com https://aspire.example.com"
    return 1
  fi

  echo "${BOLD}Waking the services${OFF} (free tier sleeps after ~15 min idle — first hit is slow)"
  for u in "$client/api/rewards" "$aspire/api/session"; do
    local code; code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 90 "$u")
    [[ "$code" =~ ^(200|401)$ ]] && ok "$u → $code" || bad "$u → $code"
  done
  [ $FAILED -ne 0 ] && return 1

  echo
  echo "${BOLD}Cross-site cookie${OFF} (the reward page is framed from another domain)"
  local R LU
  R=$(curl -s -X POST "$client/api/redeem" -H 'Content-Type: application/json' \
        -d '{"username":"jane","rewardId":"lounge"}')
  LU=$(echo "$R" | python3 -c "import sys,json;print(json.load(sys.stdin).get('launchUrl',''))" 2>/dev/null)
  if [ -z "$LU" ]; then bad "redeem failed: $R"; return 1; fi
  ok "redeem → launchUrl"

  case "$LU" in https://*) ok "launchUrl is https (forwarded headers working)" ;;
                *) bad "launchUrl is NOT https — X-Forwarded-Proto is not reaching Aspire" ;; esac

  local sc; sc=$(curl -s -o /dev/null -D - "$LU" | grep -i '^set-cookie' | tr -d '\r')
  echo "$sc" | grep -qi 'SameSite=None' && ok "cookie is SameSite=None (works cross-site)" \
                                        || bad "cookie is not SameSite=None — the web iframe will break"
  echo "$sc" | grep -qi 'secure'        && ok "cookie is Secure" || bad "cookie is not Secure"

  echo
  echo "${BOLD}Full suite${OFF}"
  CLIENT_URL="$client" ASPIRE_URL="$aspire" ./test.sh
}

case "${1:-check}" in
  check)  cmd_check ;;
  urls)   cmd_urls "${2:-}" ;;
  verify) cmd_verify "${2:-}" "${3:-}" ;;
  *) echo "Usage: ./deploy.sh [check|urls <domain>|verify <client-url> <aspire-url>]"; exit 1 ;;
esac
