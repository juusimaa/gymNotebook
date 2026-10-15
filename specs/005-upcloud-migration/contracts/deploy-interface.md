# Contract: Deploy interface

Modelled on the tracker's `deploy/bin/deploy.sh` (tracker
`contracts/deploy-interface.md`). Differences: the user, the paths, the Compose
project and the tag format.

## Caller

GitHub Actions `deploy.yml`, job `deploy-upcloud`, after a successful
`build-and-push` run on `main`, when the repository variable
`UPCLOUD_DEPLOY_ENABLED` is `true`. `workflow_dispatch` with an explicit tag
remains for a manual rollback. Concurrency group as today.

```sh
ssh -i "$key" -o StrictHostKeyChecking=yes -o UserKnownHostsFile="$known" \
  "gymdeploy@$UPCLOUD_DEPLOY_HOST" "main-<40 hex sha>"
```

Secrets: `UPCLOUD_DEPLOY_SSH_KEY`, `UPCLOUD_DEPLOY_HOST`,
`UPCLOUD_KNOWN_HOSTS` (environment `production`).

## Key

`/home/gymdeploy/.ssh/authorized_keys`:

```text
restrict,command="/opt/gymnotebook/bin/deploy.sh" ssh-ed25519 <key> gha-gymnotebook-deploy
```

The requested command only reaches the script as `$SSH_ORIGINAL_COMMAND`.

## `deploy.sh`

1. Reject anything not matching `^main-[0-9a-f]{40}$` → exit **64**, nothing
   else runs, input not echoed.
2. `flock` on `/var/lib/gymnotebook/deploy.lock`.
3. Record the running tag in `previous-tag`; write the new one to
   `image-tag.env` (temp file + rename).
4. `docker compose -p gymnotebook … pull` → on failure restore the tag file,
   exit **75**.
4a. (T063) If a `gym-api` container exists: `stop gym-api`, then merge its
   `deletion.*` lines, one `health` line per minute and a `saved gym-api
   container <id>` line into `/var/lib/gymnotebook/deletion-evidence.log`
   (0600, sorted, de-duplicated). On failure restore the tag file and
   `start gym-api` (same container, log kept) → exit **75**; if it doesn't
   start → exit **2**.
5. `up -d --wait --wait-timeout 120`, then
   `curl --resolve api.gymnotebook.fit:443:127.0.0.1 https://api.gymnotebook.fit/health`
   with retries → exit **0**.
6. Otherwise save the failed container's evidence as in 4a, then roll back
   to the previous tag with `up -d --wait` → exit **1**; if the save or the
   rollback fails, or there is no previous tag → exit **2**.

It never runs `down`, `prune`, `caddy reload` or any command naming another
Compose project, and never prints the env file.

## Guarantees

- A Gym Notebook deploy changes only containers in project `gymnotebook`
  (FR-005).
- A version that fails its health check never stays live (FR-017).
- `up` never recreates `gym-api` before its deletion evidence is saved
  (T063): a failed save stops the deploy instead.
- The key cannot open a shell or reach the tracker (FR-006). The boundary is
  the forced command, because `docker` group membership is root-equivalent (R6).
