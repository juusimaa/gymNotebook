#!/usr/bin/env bash
# Checks deploy/bin/deploy.sh's first line of defence: only `main-<40 hex>`
# gets past the tag check (contracts/deploy-interface.md, FR-006). Every
# invalid input must exit 64, print nothing of itself, and never call docker.
# A valid tag must get as far as `docker compose pull`.
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

# Each stub appends its name and arguments to calls.log. docker fails on
# `pull`, so the valid-tag case stops at a known point (exit 75) instead of
# going on to `up`.
cat > "$work/bin/docker" <<EOF
#!/usr/bin/env bash
echo "docker \$*" >> "$calls"
for arg in "\$@"; do [[ "\$arg" == pull ]] && exit 1; done
exit 0
EOF
for stub in flock curl; do
    printf '#!/usr/bin/env bash\necho "%s $*" >> "%s"\n' "$stub" "$calls" > "$work/bin/$stub"
done
chmod +x "$work/bin/"*

failures=0
valid="main-$(printf 'a%.0s' {1..40})"

# Runs deploy.sh with the given SSH_ORIGINAL_COMMAND; sets $code and $output.
run() {
    : > "$calls"
    rm -f "$work/state/"*
    set +e
    output="$(SSH_ORIGINAL_COMMAND="$1" \
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

if [[ $failures -gt 0 ]]; then
    echo "$failures check(s) failed"
    exit 1
fi
echo "all deploy guard checks passed"
