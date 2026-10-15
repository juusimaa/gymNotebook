#!/usr/bin/env bash
# Checks deploy/bin/deploy.sh's first line of defence: only `main-<40 hex>`
# gets past the tag check (contracts/deploy-interface.md, FR-006). Every
# invalid input must exit 64, print nothing of itself, and never call docker.
# A valid tag must get as far as `docker compose pull`.
#
# It also checks the deletion evidence save (specs/005 T063): the old gym-api
# is stopped and its evidence saved before `up` recreates it, nothing else
# from the log is kept, a repeat save adds nothing, and a failed save never
# lets `up` run.
#
# docker, flock and curl are replaced by stubs on PATH that log their calls,
# and the state directory is a temp dir, so this runs anywhere without a
# server. Run from the repository root: bash deploy/tests/deploy-guard.sh

set -euo pipefail

script="$(cd "$(dirname "$0")/../bin" && pwd)/deploy.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

mkdir -p "$work/bin" "$work/state"
calls="$work/calls.log"

# Each stub appends its name and arguments to calls.log. What docker fails
# is set per run in STUB_FAIL: `pull`, `logs`, or `up-once` (the first `up`
# fails, later ones succeed). `compose ps` reports one gym-api container,
# whose log is sample-log.
uuid=11111111-2222-3333-4444-555555555555
cat > "$work/sample-log" <<EOF
2026-10-14T10:00:05.1Z info: Microsoft.EntityFrameworkCore.Database.Command[20101]
2026-10-14T10:00:05.1Z       Executed DbCommand (1ms) [Parameters=[], CommandType='Text', CommandTimeout='30']
2026-10-14T10:00:05.1Z       SELECT 1
2026-10-14T10:00:15.2Z       SELECT 1
2026-10-14T10:01:05.3Z       SELECT 1
2026-10-14T10:01:30.0Z info: GymNotebook.Api.AccountDeletion[0]
2026-10-14T10:01:30.0Z       deletion.intent PrivacyAccountId=$uuid DeletionBoundaryAt=10/14/2026 10:01:30 +00:00
2026-10-14T10:01:30.2Z       deletion.committed PrivacyAccountId=$uuid DeletionBoundaryAt=10/14/2026 10:01:30 +00:00
2026-10-14T10:01:31.0Z       sign-in failed for person@example.com
EOF
cat > "$work/bin/docker" <<EOF
#!/usr/bin/env bash
echo "docker \$*" >> "$calls"
fails() { [[ " \${STUB_FAIL-} " == *" \$1 "* ]]; }
case " \$* " in
    *" pull "*) fails pull && exit 1 ;;
    *" ps "*) echo 0123456789abcdef ;;
    *" up "*)
        if fails up-once && [[ ! -e "$work/up-failed" ]]; then
            touch "$work/up-failed"
            exit 1
        fi ;;
    " logs "*)
        fails logs && exit 1
        cat "$work/sample-log" ;;
esac
exit 0
EOF
for stub in flock curl; do
    printf '#!/usr/bin/env bash\necho "%s $*" >> "%s"\n' "$stub" "$calls" > "$work/bin/$stub"
done
chmod +x "$work/bin/"*

failures=0
valid="main-$(printf 'a%.0s' {1..40})"
previous="main-$(printf 'b%.0s' {1..40})"
evidence="$work/state/deletion-evidence.log"

# Runs deploy.sh with the given SSH_ORIGINAL_COMMAND; sets $code and $output.
# STUB_FAIL (default `pull`) picks what docker fails. KEEP_STATE=1 keeps the
# state dir from the previous run.
run() {
    : > "$calls"
    rm -f "$work/up-failed"
    [[ "${KEEP_STATE-}" == 1 ]] || rm -f "$work/state/"*
    set +e
    output="$(SSH_ORIGINAL_COMMAND="$1" \
        STUB_FAIL="${STUB_FAIL-pull}" \
        PATH="$work/bin:$PATH" \
        GYMNOTEBOOK_STATE_DIR="$work/state" \
        GYMNOTEBOOK_ENV_FILE="$work/state/missing.env" \
        GYMNOTEBOOK_COMPOSE_FILE="$work/state/missing.yml" \
        bash "$script" 2>&1)"
    code=$?
    set -e
}

fail() {
    echo "FAIL: $1"
    failures=$((failures + 1))
}

expect_refused() {
    local name="$1" input="$2"
    run "$input"
    [[ $code -eq 64 ]] || fail "$name: exit $code, want 64"
    if grep -q '^docker' "$calls"; then fail "$name: docker was called"; fi
    # The refusal message must not repeat the input. Skipped for the empty
    # input, which every string "contains".
    if [[ -n "$input" && "$output" == *"$input"* ]]; then fail "$name: input echoed"; fi
    if [[ $code -eq 64 ]]; then echo "ok: $name refused"; fi
}

expect_refused "empty" ""
expect_refused "short sha" "main-5177bdc"
expect_refused "uppercase hex" "main-$(printf 'A%.0s' {1..40})"
expect_refused "41 hex" "${valid}a"
expect_refused "old sha- format" "sha-5177bdc"
expect_refused "command appended" "$valid; id"
expect_refused "option" "--help"
expect_refused "trailing newline" "$valid"$'\n'
expect_refused "second line" "$valid"$'\nid'

# A valid tag passes the check, takes the lock and reaches the pull, which
# the stub fails: exit 75 and the tag file restored (removed, as there was no
# previous tag).
run "$valid"
if [[ $code -ne 75 ]]; then
    fail "valid tag: exit $code, want 75 (stubbed pull failure); output: $output"
elif ! grep -q '^docker compose -p gymnotebook .* pull' "$calls"; then
    fail "valid tag: no 'docker compose -p gymnotebook ... pull' call"
elif ! grep -q '^flock' "$calls"; then
    fail "valid tag: lock not taken"
elif [[ -e "$work/state/image-tag.env" ]]; then
    fail "valid tag: image-tag.env not restored after the failed pull"
else
    echo "ok: valid tag reaches pull"
fi

# Line number of the first call matching a pattern, or 0.
call_at() { grep -n -m1 -e "$1" "$calls" | cut -d: -f1 | grep . || echo 0; }

# Each case below starts with a previous tag, as on a running server.
seed_previous() {
    rm -f "$work/state/"*
    echo "IMAGE_TAG=$previous" > "$work/state/image-tag.env"
}

# A successful deploy over a running container: stop, save, then up.
seed_previous
STUB_FAIL="" KEEP_STATE=1 run "$valid"
stop_at=$(call_at 'compose .* stop gym-api$')
logs_at=$(call_at '^docker logs --timestamps 0123456789abcdef$')
up_at=$(call_at 'compose .* up ')
# GNU stat (CI, server) first, BSD stat (macOS) as the fallback.
mode="$(stat -c %a "$evidence" 2>/dev/null || stat -f %Lp "$evidence" 2>/dev/null || echo missing)"
if [[ $code -ne 0 ]]; then
    fail "save: exit $code, want 0; output: $output"
elif ! (( stop_at > 0 && stop_at < logs_at && logs_at < up_at )); then
    fail "save: want stop, then logs, then up; calls: $(tr '\n' '|' < "$calls")"
elif [[ "$mode" != 600 ]]; then
    fail "save: evidence file mode $mode, want 600"
elif [[ "$(grep -cE " deletion\.(intent|committed) PrivacyAccountId=$uuid" "$evidence")" != 2 ]]; then
    fail "save: deletion lines missing from the evidence"
elif [[ "$(grep -c ' health$' "$evidence")" != 2 ]]; then
    fail "save: want one health line per minute (2)"
elif [[ "$(grep -c ' saved gym-api container 0123456789ab$' "$evidence")" != 1 ]]; then
    fail "save: no saved-container line"
elif [[ "$(wc -l < "$evidence" | tr -d ' ')" != 5 ]]; then
    fail "save: other log lines kept: $(cat "$evidence")"
elif [[ "$output" == *"$uuid"* ]]; then
    fail "save: evidence printed to the CI log"
else
    echo "ok: old gym-api saved before up, only evidence kept"
fi

# Saving the same container again (a second deploy before the log changes)
# adds no duplicate deletion or health lines.
STUB_FAIL="" KEEP_STATE=1 run "$valid"
if [[ "$(grep -vc ' saved gym-api container ' "$evidence")" != 4 ]]; then
    fail "repeat save: duplicate lines: $(cat "$evidence")"
else
    echo "ok: repeat save adds no duplicates"
fi

# The log can't be read: no up, the old container started again, the tag
# file back to the previous tag, exit 75.
seed_previous
STUB_FAIL="logs" KEEP_STATE=1 run "$valid"
if [[ $code -ne 75 ]]; then
    fail "failed save: exit $code, want 75; output: $output"
elif [[ $(call_at 'compose .* up ') != 0 ]]; then
    fail "failed save: up ran"
elif [[ $(call_at 'compose .* start gym-api$') == 0 ]]; then
    fail "failed save: gym-api not started again"
elif [[ "$(cat "$work/state/image-tag.env")" != "IMAGE_TAG=$previous" ]]; then
    fail "failed save: tag file not restored"
elif [[ -e "$evidence" || -e "$evidence.tmp" ]]; then
    fail "failed save: partial evidence file left"
else
    echo "ok: failed save leaves the old container and its log"
fi

# The new version fails its health check: the failed container is saved too
# before the rollback's up, exit 1.
seed_previous
STUB_FAIL="up-once" KEEP_STATE=1 run "$valid"
if [[ $code -ne 1 ]]; then
    fail "rollback: exit $code, want 1; output: $output"
elif [[ "$(grep -c 'compose .* stop gym-api$' "$calls")" != 2 ]]; then
    fail "rollback: failed container not stopped and saved before rollback"
elif [[ "$(cat "$work/state/image-tag.env")" != "IMAGE_TAG=$previous" ]]; then
    fail "rollback: tag file not back to previous"
else
    echo "ok: rollback saves the failed container first"
fi

if [[ $failures -gt 0 ]]; then
    echo "$failures check(s) failed"
    exit 1
fi
echo "all deploy guard checks passed"
