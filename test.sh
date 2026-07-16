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

# SAML needs a browser: the IdP returns a self-posting form. curl will not follow it, so pull the
# assertion out and POST it ourselves — the same two hops the browser would make.
saml_launch() { # saml_launch <scenario> -> the signed SAMLResponse
  local sc="${1:-}"
  local lu
  lu=$(redeem "{\"username\":\"jane\",\"rewardId\":\"lounge\",\"mode\":\"saml\",\"scenario\":\"$sc\"}" \
       | python3 -c "import sys,json;print(json.load(sys.stdin).get('launchUrl',''))" 2>/dev/null)
  [ -z "$lu" ] && return 1
  curl -s "$lu" | grep -oE 'value="[^"]+"' | sed 's/value="//;s/"//'
}
saml_acs() { curl -s -X POST "$A/sso/saml/acs" --data-urlencode "SAMLResponse=$1"; }

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
# Locally the demo values in appsettings.json are the live ones. Against a real deployment they
# are not — the secret comes from the environment there — so allow an override.
#   CLIENT_ID=… CLIENT_SECRET=… ./test.sh
_ID=${CLIENT_ID:-$(python3 -c "import json;print(json.load(open('backend/Client.Demo/appsettings.json'))['Client']['Aspire']['ClientId'])")}
_SECRET=${CLIENT_SECRET:-$(python3 -c "import json;print(json.load(open('backend/Client.Demo/appsettings.json'))['Client']['Aspire']['ClientSecret'])")}
T=$(post $C/api/jwt '{"username":"jane"}' | python3 -c "import sys,json;print(json.load(sys.stdin)['token'])")
# Let curl build the Basic header. Hand-rolling `base64` wraps at 76 chars on GNU coreutils, and a
# newline in the header makes curl fail silently — the real 48-char secret tripped this, the short
# demo one never did.
sso_jwt() { # sso_jwt [auth]
  local creds=(); [ -n "${1:-}" ] && creds=(-u "$_ID:$_SECRET")   # array: a secret with spaces must survive
  # ${a[@]+"${a[@]}"} — bash 3.2 (macOS) calls a plain "${a[@]}" unbound when empty under set -u
  curl -s -X POST $A/sso/jwt -H 'Content-Type: application/json' -H 'Accept: application/json' \
       ${creds[@]+"${creds[@]}"} -d "{\"token\":\"$T\"}"
}
check "no credentials → 401"  "Invalid or missing client credentials" "$(sso_jwt)"
check "with credentials → ok" '"ok":true'      "$(sso_jwt auth)"
check "replay same jti → 401" "Replay detected" "$(sso_jwt auth)"

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
echo "── SAML ───────────────────────────────────────────────"
check "IdP publishes metadata + cert" "X509Certificate" "$(curl -s $C/saml/metadata)"
check "SP publishes metadata + ACS"   "AssertionConsumerService" "$(curl -s $A/sso/saml/metadata)"

SR=$(saml_launch "")
# The IdP hands back a signed <saml:Assertion> wrapped in a <samlp:Response>. Decode the whole
# thing — the Assertion sits well past the Response header and Status.
check "redeem mode=saml → signed assertion" "<saml:Assertion" "$(echo "$SR" | base64 -d 2>/dev/null)"
check "  …assertion is signed"              "SignatureValue"  "$(echo "$SR" | base64 -d 2>/dev/null)"

# saml_launch POSTs the assertion itself, so it would still pass with a form pointing at
# localhost. Check the action the BROWSER is handed — that is what shipped broken to prod.
SFORM=$(curl -s "$(redeem '{"username":"jane","rewardId":"lounge","mode":"saml"}' \
        | python3 -c "import sys,json;print(json.load(sys.stdin)['launchUrl'])")" | grep -oE '<form[^>]*>')
check "  …form POSTs to the real ACS"       "$A/sso/saml/acs" "$SFORM"

if [ -n "$SR" ]; then
  sjar=$(mktemp)
  curl -s -c "$sjar" -o /dev/null -X POST "$A/sso/saml/acs" --data-urlencode "SAMLResponse=$SR"
  check "signed assertion → session"  '"authenticated":true'      "$(curl -s -b "$sjar" $A/api/session)"
  check "  …session says via SAML"    '"via":"SAML"'              "$(curl -s -b "$sjar" $A/api/session)"
  check "  …session is Jane"          '"displayName":"Jane Tan"'  "$(curl -s -b "$sjar" $A/api/session)"
  check "  …deep-linked reward page"  "Airport lounge pass"       "$(curl -s -b "$sjar" $A/benefit)"
  check "  …page shows VIA SAML"      "VIA SAML"                  "$(curl -s -b "$sjar" $A/benefit)"
  rm -f "$sjar"
  # the same assertion must not work twice
  check "assertion replay rejected"   "Replay detected"           "$(saml_acs "$SR")"
fi

check "saml tampered → rejected"   "Invalid signature"  "$(saml_acs "$(saml_launch tampered)")"
check "saml wrong audience → rejected" "Audience mismatch"  "$(saml_acs "$(saml_launch wrong-aud)")"
check "saml expired → rejected"    "Assertion expired"  "$(saml_acs "$(saml_launch expired)")"
check "saml inactive user → 403"   "User is inactive"   "$(redeem '{"username":"mai","rewardId":"lounge","mode":"saml"}')"

echo
echo "───────────────────────────────────────────────────────"
printf "  \033[32m%d passed\033[0m" "$pass"
[[ $fail -gt 0 ]] && printf ", \033[31m%d failed\033[0m" "$fail"
echo; echo
exit $(( fail > 0 ? 1 : 0 ))
