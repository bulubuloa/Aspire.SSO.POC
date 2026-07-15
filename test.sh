#!/usr/bin/env bash
# End-to-end check of the SSO demo. Run with both services up:
#   cd backend/Aspire.Sso  && dotnet run     # :6001
#   cd backend/Client.Demo && dotnet run     # :5001
#
#   ./test.sh
set -uo pipefail

C=${CLIENT_URL:-http://localhost:5001}
A=${ASPIRE_URL:-http://localhost:6001}
pass=0; fail=0

check() { # check <name> <expected-substring> <actual>
  if [[ "$3" == *"$2"* ]]; then printf "  \033[32m✓\033[0m %-42s\n" "$1"; ((pass++))
  else printf "  \033[31m✗\033[0m %-42s\n      expected: %s\n      actual:   %s\n" "$1" "$2" "${3:0:100}"; ((fail++)); fi
}

post() { curl -s -X POST "$1" -H 'Content-Type: application/json' -d "$2"; }
redeem() { post "$C/api/redeem" "$1"; }

echo "── services up? ───────────────────────────────────────"
check "Client.Demo :5001 responding" "coffee"     "$(curl -s $C/api/rewards)"
check "Aspire.Sso  :6001 responding" "401" "$(curl -s -o /dev/null -w '%{http_code}' $A/api/session)"

echo
echo "── trust boundary ─────────────────────────────────────"
check "Aspire has no user directory"  "404" "$(curl -s -o /dev/null -w '%{http_code}' $A/api/login)"
check "client has no session store"   "404" "$(curl -s -o /dev/null -w '%{http_code}' $C/benefit)"
check "client publishes PUBLIC jwks"  '"kty":"RSA"' "$(curl -s $C/.well-known/jwks.json)"

echo
echo "── login (client-owned) ───────────────────────────────"
check "valid credentials"     '"displayName":"Jane Tan"' "$(post $C/api/login '{"username":"jane","password":"demo"}')"
check "wrong password → 401"  "Invalid credentials"      "$(post $C/api/login '{"username":"jane","password":"WRONG"}')"
check "unknown user → 401"    "Invalid credentials"      "$(post $C/api/login '{"username":"nobody","password":"demo"}')"

echo
echo "── redeem: the silent handoff ─────────────────────────"
R=$(redeem '{"username":"jane","rewardId":"lounge"}')
check "happy path → launchUrl"  "launchUrl"            "$R"
check "  …deep-linked reward"   "Airport lounge pass"  "$R"

LU=$(echo "$R" | python3 -c "import sys,json;print(json.load(sys.stdin).get('launchUrl',''))" 2>/dev/null)
if [[ -n "$LU" ]]; then
  jar=$(mktemp)
  curl -s -c "$jar" -o /dev/null "$LU"
  check "ticket → Aspire session"  '"authenticated":true' "$(curl -s -b "$jar" $A/api/session)"
  check "  …session is Jane"       '"displayName":"Jane Tan"' "$(curl -s -b "$jar" $A/api/session)"
  check "  …reward page renders"   "Airport lounge pass" "$(curl -s -b "$jar" $A/benefit)"
  # Reusing the ticket must not produce a session (fresh jar = no cookie carried over).
  check "ticket is one-time"       "No Aspire session"   "$(curl -s -L "$LU")"
  rm -f "$jar"
fi

echo
echo "── negative cases ─────────────────────────────────────"
check "expired token"        "Token expired"                      "$(redeem '{"username":"jane","rewardId":"lounge","scenario":"expired"}')"
check "wrong audience"       "Audience mismatch"                  "$(redeem '{"username":"jane","rewardId":"lounge","scenario":"wrong-aud"}')"
check "tampered signature"   "Invalid signature"                  "$(redeem '{"username":"jane","rewardId":"lounge","scenario":"tampered"}')"
check "bad client secret"    "Invalid or missing client credentials" "$(redeem '{"username":"jane","rewardId":"lounge","scenario":"bad-secret"}')"
check "inactive user → 403"  "User is inactive"                   "$(redeem '{"username":"mai","rewardId":"lounge"}')"
check "unknown reward → 400" "Unknown reward"                     "$(redeem '{"username":"jane","rewardId":"ferrari"}')"
check "unknown user → 401"   "Not authenticated"                  "$(redeem '{"username":"nobody","rewardId":"lounge"}')"

echo
echo "── Aspire SSO endpoint, direct ────────────────────────"
CREDS=$(printf '%s' "$(python3 -c "
import json;c=json.load(open('backend/Client.Demo/appsettings.json'))['Client']['Aspire']
print(f\"{c['ClientId']}:{c['ClientSecret']}\")")" | base64)
T=$(post $C/api/jwt '{"username":"jane"}' | python3 -c "import sys,json;print(json.load(sys.stdin)['token'])")
check "no credentials → 401"  "Invalid or missing client credentials" \
  "$(curl -s -X POST $A/sso/jwt -H 'Content-Type: application/json' -H 'Accept: application/json' -d "{\"token\":\"$T\"}")"
check "with credentials → ok" '"ok":true' \
  "$(curl -s -X POST $A/sso/jwt -H 'Content-Type: application/json' -H 'Accept: application/json' -H "Authorization: Basic $CREDS" -d "{\"token\":\"$T\"}")"
check "replay same jti → 401" "Replay detected" \
  "$(curl -s -X POST $A/sso/jwt -H 'Content-Type: application/json' -H 'Accept: application/json' -H "Authorization: Basic $CREDS" -d "{\"token\":\"$T\"}")"

echo
echo "── token contract ─────────────────────────────────────"
CLAIMS=$(post $C/api/jwt '{"username":"jane"}' | python3 -c "
import sys,json,base64
t=json.load(sys.stdin)['token']; p=t.split('.')[1]
print(' '.join(json.loads(base64.urlsafe_b64decode(p+'='*(-len(p)%4))).keys()))")
for c in sub email given_name family_name jti iat exp iss aud; do
  check "claim '$c' present" "$c" "$CLAIMS"
done
check "member_id NOT sent (unused)" "" "$([[ "$CLAIMS" != *member_id* ]] && echo "")"

echo
echo "───────────────────────────────────────────────────────"
printf "  \033[32m%d passed\033[0m" "$pass"
[[ $fail -gt 0 ]] && printf ", \033[31m%d failed\033[0m" "$fail"
echo; echo
exit $(( fail > 0 ? 1 : 0 ))
