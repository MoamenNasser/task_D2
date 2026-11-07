#!/usr/bin/env bash
# End-to-end check against a freshly started service (initial state W100=100, W200=50).
# Usage: scripts/smoke.sh [base_url]     default: http://127.0.0.1:8080
set -euo pipefail

BASE="${1:-http://127.0.0.1:8080}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
PASS=0

fail() { echo "FAIL: $*" >&2; exit 1; }

# request METHOD PATH BODY -> prints status, body saved to $TMP/body ("-" means no body)
request() {
    local method="$1" path="$2" body="$3"
    local args=(-s -o "$TMP/body" -w '%{http_code}' -X "$method" "$BASE$path")
    if [[ "$body" != "-" ]]; then
        args+=(-H 'Content-Type: application/json' --data-binary "$body")
    fi
    curl "${args[@]}"
}

# check NAME EXPECTED_STATUS METHOD PATH BODY [EXPECTED_BODY_SUBSTRING]
check() {
    local name="$1" expected="$2" method="$3" path="$4" body="$5" substring="${6-}"
    local status
    status="$(request "$method" "$path" "$body")"
    [[ "$status" == "$expected" ]] || fail "$name: expected $expected, got $status, body: $(head -c 300 "$TMP/body")"
    if [[ -n "$substring" ]]; then
        grep -qF -- "$substring" "$TMP/body" || fail "$name: body missing '$substring': $(head -c 300 "$TMP/body")"
    fi
    PASS=$((PASS + 1))
    echo "ok   $status  $name"
}

award() { printf '{"sourceId":"%s","externalTransactionId":"%s","walletId":"%s","points":%s}' "$@"; }

curl -s -o /dev/null "$BASE/wallets/W100" || fail "service not reachable at $BASE"

check "initial W100"                 200 GET /wallets/W100 - '"balance":100'
check "initial W200"                 200 GET /wallets/W200 - '"balance":50'
check "unknown wallet lookup"        404 GET /wallets/W999 - '"message"'
check "non-UTF-8 wallet id"          404 GET /wallets/%FF - '"message"'

check "new award"                    201 POST /awards "$(award POS_A T1 W100 25)" '"awardId"'
first_award="$(cat "$TMP/body")"
check "matching retry"               200 POST /awards "$(award POS_A T1 W100 25)"
[[ "$(cat "$TMP/body")" == "$first_award" ]] || fail "retry did not return the original record"
check "balance credited once"        200 GET /wallets/W100 - '"balance":125'

check "same txn, other source"       201 POST /awards "$(award POS_B T1 W100 25)"
check "conflict: other points"       409 POST /awards "$(award POS_A T1 W100 30)" '"message"'
check "conflict: other wallet"       409 POST /awards "$(award POS_A T1 W200 25)"
check "W100 unchanged by conflicts"  200 GET /wallets/W100 - '"balance":150'
check "W200 unchanged by conflicts"  200 GET /wallets/W200 - '"balance":50'

check "unknown wallet award"         404 POST /awards "$(award POS_A T2 W999 25)" '"message"'
check "identity reusable after 404"  201 POST /awards "$(award POS_A T2 W200 10)"

check "malformed JSON"               400 POST /awards '{bad json' '"message"'
check "non-object body"              400 POST /awards '[1,2]' '"message"'
check "float points"                 400 POST /awards "$(award POS_A T3 W100 25.0)" '"points"'
check "string points"                400 POST /awards "$(award POS_A T3 W100 '"25"')" '"points"'
check "points out of range"          400 POST /awards "$(award POS_A T3 W100 1001)" '"points"'
check "bad identifier"               400 POST /awards "$(award 'POS A' T3 W100 5)" '"sourceId"'
check "invalid UTF-8 in body"        400 POST /awards $'{"sourceId":"\xff","externalTransactionId":"U1","walletId":"W100","points":5}' '"message"'
check "duplicate key"               400 POST /awards '{"sourceId":"A","externalTransactionId":"D1","walletId":"W100","points":5,"points":500}' '"points"'
check "wrong method on /awards"      405 PUT /awards '{}' '"message"'
check "wrong method on wallet"       405 DELETE /wallets/W100 - '"message"'
check "unknown route"                404 GET /nope - '"message"'
check "body over 64 KiB"             413 POST /awards "$(head -c 70000 /dev/zero | tr '\0' 'a')" '"message"'

# Concurrent burst: 20 identical requests at once -> exactly one 201, nineteen 200, credited once.
for i in $(seq 1 20); do
    curl -s -o /dev/null -w '%{http_code}\n' -X POST "$BASE/awards" \
         -H 'Content-Type: application/json' --data-binary "$(award POS_C BURST1 W200 5)" > "$TMP/burst.$i" &
done
wait
created="$(cat "$TMP"/burst.* | grep -c '^201$' || true)"
replayed="$(cat "$TMP"/burst.* | grep -c '^200$' || true)"
[[ "$created" == 1 && "$replayed" == 19 ]] || fail "burst: expected 1x201 + 19x200, got ${created}x201 ${replayed}x200"
PASS=$((PASS + 1)); echo "ok   burst  20 concurrent identical requests -> 1x201, 19x200"
check "burst credited once"          200 GET /wallets/W200 - '"balance":65'

echo "All $PASS checks passed."
