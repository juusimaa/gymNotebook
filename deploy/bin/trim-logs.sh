#!/usr/bin/env bash
# Daily time limit on Gym Notebook's container logs (specs/005 T061). Docker's
# json-file driver caps each container at 3 × 10 MB but never by age, so at
# our volume a line could outlive the 30-day maximum in
# docs/privacy/retention.md. This removes every line older than 30 days from
# the current and rotated log files of the containers in Compose project
# `gymnotebook`, and deletes rotated files that end up empty. Run as root by
# gymnotebook-log-trim.timer, because Docker's log files are root-only.
#
# Each json-file line is one JSON object with a "time" field in RFC 3339 UTC,
# for example "2026-10-12T08:10:18.123456789Z". Such strings sort in time
# order, so awk compares them with the cutoff as plain text: no jq needed.
#
# A file is rewritten in place (`cat tmp > file`, same inode), because Docker
# keeps the current file open and appends to it; replacing the file would
# leave Docker writing to a deleted one. A line Docker appends between the
# read and the rewrite is lost. That is at most a line or two, once a day,
# and the price of not stopping the container.
#
# It only ever looks at containers labelled with our Compose project, never
# the tracker's. It prints file names and line counts, never log content.
#
# GYMNOTEBOOK_LOG_CUTOFF exists only so deploy/tests/trim-logs-test.sh can
# fix the cutoff; in production it is "now minus 30 days".

set -euo pipefail

cutoff="${GYMNOTEBOOK_LOG_CUTOFF:-$(date -u -d '30 days ago' +%Y-%m-%dT%H:%M:%S)}"

tmp="$(mktemp)"
trap 'rm -f "$tmp"' EXIT

# Keeps lines whose "time" is at or after the cutoff. A line without a
# "time" field isn't Docker's format; keep it rather than guess.
filter() {
    awk -v cutoff="$cutoff" '
        match($0, /"time":"[^"]*"/) {
            t = substr($0, RSTART + 8, RLENGTH - 9)
            if (t < cutoff) next
        }
        { print }
    ' "$1"
}

trim_file() {
    local file="$1" rotated="$2" before after
    [[ -s "$file" ]] || return 0
    before=$(wc -l < "$file")
    filter "$file" > "$tmp"
    after=$(wc -l < "$tmp")
    (( after < before )) || return 0
    if (( after == 0 )) && [[ "$rotated" == yes ]]; then
        rm -f "$file"
        echo "trim-logs: $file: removed ($before old lines)"
    else
        cat "$tmp" > "$file"
        echo "trim-logs: $file: $((before - after)) of $before lines removed"
    fi
}

docker ps -a -q --filter label=com.docker.compose.project=gymnotebook |
while read -r id; do
    log="$(docker inspect -f '{{.LogPath}}' "$id")"
    [[ -n "$log" ]] || continue
    # Rotated files are log.1, log.2 (max-file 3); the glob also covers a
    # larger max-file. Unmatched globs stay literal and fail the -e test.
    for rotated in "$log".*; do
        [[ -e "$rotated" ]] && trim_file "$rotated" yes
    done
    trim_file "$log" no
done
