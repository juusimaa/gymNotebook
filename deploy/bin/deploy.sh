#!/usr/bin/env bash
# The gymdeploy key's forced command: every SSH login with CI's key runs this,
# whatever command the client asked for (contracts/deploy-interface.md). The
# requested command arrives only as $SSH_ORIGINAL_COMMAND and must be an image
# tag CI built: `main-` plus the full 40-hex commit sha. Anything else exits
# 64 before any other command runs, so the key cannot run arbitrary commands
# or reach the tracker (FR-006). Ported from the tracker's deploy/bin/deploy.sh;
# differences are the user, paths, Compose project and tag format.
#
# Then: take the lock, remember the running tag, start the new one, check it
# through Caddy, and roll back to the remembered tag if anything fails.
#
# Exit codes, which CI shows as the job result:
#   0  new tag live and healthy
#   1  new tag failed; previous tag restored and running
#   2  rollback failed too, or there was no previous tag (deploy/README.md
#      § Deploy, rolling back by hand)
#   64 invalid tag; nothing ran
#   75 image pull failed; nothing changed
#
# It only ever touches Compose project `gymnotebook`: never `down`, `prune`,
# `caddy reload` or another project (FR-005). It never prints the env file or
# any value from it, because this output goes to the CI log.
#
# Paths default to production. The GYMNOTEBOOK_* overrides exist only so
# deploy/tests/deploy-guard.sh can point them at a temp dir. An SSH client
# cannot set them: the key's `restrict` option and sshd's default
# `PermitUserEnvironment no` keep the client's environment out of this process.

set -euo pipefail

tag="${SSH_ORIGINAL_COMMAND-}"
# Bash's =~ uses POSIX extended regexes, where `$` matches only at the very
# end, so a trailing newline or a second line is rejected too. The value is
# not echoed back: it is attacker-chosen and the log gains nothing from it.
if [[ ! "$tag" =~ ^main-[0-9a-f]{40}$ ]]; then
    echo "deploy: refused; expected an image tag like main-<40 hex sha>" >&2
    exit 64
fi

STATE_DIR="${GYMNOTEBOOK_STATE_DIR:-/var/lib/gymnotebook}"
ENV_FILE="${GYMNOTEBOOK_ENV_FILE:-/etc/gymnotebook/.env}"
COMPOSE_FILE="${GYMNOTEBOOK_COMPOSE_FILE:-/opt/gymnotebook/compose.prod.yml}"
TAG_FILE="$STATE_DIR/image-tag.env"
# compose.prod.yml reads it for gym-api's env_file.
export ENV_FILE

# The env file supplies TURNSTILE_SITE_KEY for interpolation, the tag file
# IMAGE_TAG. The tag lives under STATE_DIR, which gymdeploy owns, because the
# env file is root:gymdeploy 0640: this script can read secrets, not write them.
compose() {
    docker compose -p gymnotebook -f "$COMPOSE_FILE" \
        --env-file "$ENV_FILE" --env-file "$TAG_FILE" "$@"
}

# Written to a temp file and renamed, so a crash mid-write never leaves an
# empty tag file that the next `up` would refuse.
write_tag() {
    printf 'IMAGE_TAG=%s\n' "$1" > "$TAG_FILE.tmp"
    mv "$TAG_FILE.tmp" "$TAG_FILE"
}

# Two merges in quick succession queue here instead of running compose at
# the same time. CI also serializes deploys (concurrency group), but a
# manual run over the deploy key would not.
exec 9> "$STATE_DIR/deploy.lock"
flock 9

previous="$(sed -n 's/^IMAGE_TAG=//p' "$TAG_FILE" 2>/dev/null || true)"
printf '%s\n' "$previous" > "$STATE_DIR/previous-tag"

restore_tag() {
    if [[ -n "$previous" ]]; then
        write_tag "$previous"
    else
        rm -f "$TAG_FILE"
    fi
}

echo "deploy: ${previous:-nothing} -> $tag"
write_tag "$tag"

if ! compose pull --quiet; then
    restore_tag
    echo "deploy: could not pull $tag; nothing changed" >&2
    exit 75
fi

# --wait returns once both services report healthy; gym-api's check includes
# the database, and 120 s covers start plus migrations. The public check then
# goes through Caddy with the hostname pinned to this machine, so it tests
# what users reach (TLS, proxy, API, database) without depending on DNS,
# which points elsewhere until the cutover.
if compose up -d --wait --wait-timeout 120 \
    && curl -fsS -o /dev/null --max-time 10 --retry 5 --retry-delay 2 --retry-all-errors \
        --resolve api.gymnotebook.fit:443:127.0.0.1 https://api.gymnotebook.fit/health; then
    echo "deploy: $tag is live"
    exit 0
fi

echo "deploy: $tag failed its health checks; rolling back to ${previous:-nothing}" >&2
if [[ -z "$previous" ]]; then
    echo "deploy: no previous tag to roll back to" >&2
    exit 2
fi
restore_tag
# No pull first: `up` pulls the previous image itself if the tracker's weekly
# image prune removed it.
if compose up -d --wait --wait-timeout 120; then
    echo "deploy: rolled back to $previous" >&2
    exit 1
fi
echo "deploy: rollback to $previous failed; see deploy/README.md § Deploy" >&2
exit 2
