#!/usr/bin/env bash
# Checks deploy/bin/trim-logs.sh (specs/005 T061): lines older than the cutoff
# go, newer lines stay in order, a rotated file left empty is deleted, the
# current file keeps its inode (Docker holds it open), and only containers of
# Compose project `gymnotebook` are asked for. The saved deletion evidence
# (T063) is trimmed the same way, under the deploy lock, keeping its inode.
#
# docker is a stub on PATH that lists one container and points its LogPath at
# a temp dir; flock is a stub that logs its call (macOS has none). The cutoff
# and state dir are fixed, so this runs anywhere without a server.
# Run from the repository root: bash deploy/tests/trim-logs-test.sh

set -euo pipefail

script="$(cd "$(dirname "$0")/../bin" && pwd)/trim-logs.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

mkdir -p "$work/bin" "$work/logs" "$work/state"
calls="$work/calls.log"
log="$work/logs/abc-json.log"

cat > "$work/bin/docker" <<EOF
#!/usr/bin/env bash
echo "docker \$*" >> "$calls"
case "\$1" in
    ps) echo abc ;;
    inspect) echo "$log" ;;
esac
EOF
printf '#!/usr/bin/env bash\necho "flock $*" >> "%s"\n' "$calls" > "$work/bin/flock"
chmod +x "$work/bin/docker" "$work/bin/flock"

line() { printf '{"log":"%s\\n","stream":"stdout","time":"%s"}\n' "$1" "$2"; }

# log.2: all old, must be deleted. log.1: mixed. log: mixed, current file.
line old-a 2026-09-01T10:00:00.1Z > "$log.2"
line old-b 2026-09-01T11:00:00.1Z >> "$log.2"
line old-c 2026-09-11T23:59:59.9Z > "$log.1"
line new-d 2026-09-12T00:00:00.5Z >> "$log.1"
line old-e 2026-09-10T00:00:00Z > "$log"
line new-f 2026-10-01T00:00:00.000000001Z >> "$log"
line new-g 2026-10-12T08:00:00Z >> "$log"

# GNU stat (CI, server) first, BSD stat (macOS) as the fallback.
inode() { stat -c %i "$1" 2>/dev/null || stat -f %i "$1"; }
inode_before=$(inode "$log")

# Evidence lines as deploy.sh writes them: Docker's timestamp first.
evidence="$work/state/deletion-evidence.log"
uuid=11111111-2222-3333-4444-555555555555
{
    echo "2026-09-11T23:59:59.9Z       deletion.committed PrivacyAccountId=$uuid DeletionBoundaryAt=old"
    echo "2026-09-11T23:59:59.9Z health"
    echo "2026-09-12T00:00:00.5Z health"
    echo "2026-10-01T00:00:00Z       deletion.committed PrivacyAccountId=$uuid DeletionBoundaryAt=new"
    echo "2026-10-01T00:00:01Z saved gym-api container abc"
} > "$evidence"
: > "$work/state/deploy.lock"
evidence_inode_before=$(inode "$evidence")

trim() {
    PATH="$work/bin:$PATH" GYMNOTEBOOK_LOG_CUTOFF=2026-09-12T00:00:00 \
        GYMNOTEBOOK_STATE_DIR="$work/state" bash "$script"
}
trim > "$work/out"

fail=0
check() {
    if [[ "$2" == "$3" ]]; then echo "ok:   $1"; else
        echo "FAIL: $1: expected [$3], got [$2]"; fail=1; fi
}
names() { [[ -e "$1" ]] && grep -o '"log":"[a-z-]*' "$1" | cut -d'"' -f4 | tr '\n' ' '; }

check "fully old rotated file is deleted" "$([[ -e "$log.2" ]] && echo present || echo gone)" gone
check "rotated file keeps only new lines" "$(names "$log.1")" "new-d "
check "current file keeps only new lines, in order" "$(names "$log")" "new-f new-g "
check "current file keeps its inode" "$(inode "$log")" "$inode_before"
check "asks only for project gymnotebook" "$(head -1 "$calls")" \
    "docker ps -a -q --filter label=com.docker.compose.project=gymnotebook"
check "output holds no log content" "$(grep -c -e old- -e new- -e "$uuid" "$work/out" || true)" 0
check "evidence keeps only new lines, in order" "$(cut -d" " -f1 "$evidence" | tr '\n' ' ')" \
    "2026-09-12T00:00:00.5Z 2026-10-01T00:00:00Z 2026-10-01T00:00:01Z "
check "evidence keeps its inode" "$(inode "$evidence")" "$evidence_inode_before"
check "evidence trimmed under the deploy lock" "$(grep -c '^flock 9' "$calls")" 1

# A second run with nothing old changes nothing and prints nothing.
trim > "$work/out2"
check "second run is a no-op" "$(wc -c < "$work/out2" | tr -d ' ')" 0

exit "$fail"
