# Account restore runbook

Owner: Gym Notebook operator. Version: draft 2026-10-12, rewritten for UpCloud (specs/005 T062); the Neon version this replaces, exercised in T074 on 2026-09-29, is in Git history. Review this runbook after every restore exercise and every change to the database, deletion flow, logging, backups or deployment. The [restore contract](../../specs/001-privacy-account-lifecycle/contracts/operations.md#restore-contract--fail-closed) defines the required outcome; this runbook is its operator procedure, and [deploy/README.md § Restore](../../deploy/README.md#restore) holds the plain database mechanics it builds on.

**Release status:** not yet exercised on UpCloud. The restore drill (specs/005 T058) is the first run; until the owner has reviewed its result, do not treat this document as evidence that production restores are safe. Use only with a restricted incident record and an authorized operator. Never rehearse with real users' data. Keep account UUIDs, password hashes, connection details, logs and incident evidence in restricted storage, outside Git.

**The idea.** UpCloud restores a backup into a **new, temporary** database service; the live `gymnotebook` database is untouched until the last step. So the live database *is* the preserved pre-restore state: reconcile the restored copy against it inside the temporary service, then copy the reconciled database back. If the live database can't be read (often the reason for restoring), the fallback reconciles from `gym-api`'s deletion log lines instead.

## Stop conditions and incident record

Keep `gym-api` stopped if any precondition, evidence source, comparison, SQL result, secret rotation or verification is uncertain. An empty query result does not prove that its source is complete. Do not choose a different restore point, drop the temporary service or start `gym-api` to make an uncertain result disappear.

Start a restricted incident record with operator/reviewer, UTC start and restore point `T`, reason, the temporary service's name, the running image tag (`/var/lib/gymnotebook/image-tag.env`), the latest migration live and at `T`, `gym-api`'s container creation time, the evidence source and its covered interval, counts and verification results. Record the decision to reopen and the time of each action. Store only references and aggregate results in the repository's release checklist.

The restored copy must contain the `privacy_account_id` migration (`20260925063835_AddPrivacyAccountIdentity`), which every UpCloud backup does (the database arrived on 2026-10-12 with it), and a schema compatible with the image that will run. The UUID, not the integer user ID or the display name, is the account identity for this procedure. Do not generate replacement UUIDs for old data.

All commands run on the server as `admin`, with `gn` defined (deploy/README.md § Running Compose by hand), from a private working directory: `mkdir -m 700 ~/restore && cd ~/restore`.

## 1. Isolate

1. **Stop deploys.** A merge to `main` would start `gym-api` again: `gh workflow disable deploy` (from a machine with `gh`), or in GitHub, Actions → deploy → Disable workflow. Merge nothing until step 5.
2. **Stop the API**: `gn stop gym-api`. `restart: unless-stopped` keeps it stopped through a reboot. `https://api.gymnotebook.fit/health` now answers **502** from Caddy; that is the isolation. `gym-web` may keep serving the static pages; without the API they can't read or write anything.
3. **Record the isolation instant** (UTC) and confirm no other writer: `SELECT pid, usename, application_name, state FROM pg_stat_activity WHERE datname = 'gymnotebook';` as `gymnotebook` shows only your own session.
4. **Before anything else, if the fallback might be needed** (step 4), record `gym-api`'s log coverage now: `docker inspect -f '{{.Created}}' gymnotebook-gym-api-1` and the oldest kept line, `gn logs --no-log-prefix --timestamps gym-api | head -1`. A stopped container keeps its log; **never** run `gn up`, `gn down` or a deploy before step 4 is done, because recreating the container deletes it.

## 2. Restore into a temporary service

Follow deploy/README.md § Restore step 1: UpCloud console → the database → Backups → restore to a **new** service in `fi-hel1`, the same SDN network, public access off. Record the backup's UTC time as `T`. Do not retry a restore that seems to hang until the console shows whether the first one created a service.

Set two connection strings with `read -rs`, so they stay out of the shell history. Both use the `gymnotebook` role, which owns the tables; on the temporary service its password is the one it had at `T`:

- `GN_URL`: the live database, as in deploy/README.md § Cutover.
- `TMP_URL`: the same, with the temporary service's private host and port.

```sh
read -rs GN_URL; read -rs TMP_URL; export GN_URL TMP_URL
# psql in a container. $1 names the URL variable; the rest are psql arguments.
# The private directory is /work inside, deploy/bin is /bin-gn, and a terminal
# is attached only when there is one, so the same helper works in a pipe.
pg() {
    local url="$1" tty=; shift
    [ -t 0 ] && [ -t 1 ] && tty=-t
    docker run --rm -i $tty -e "$url" -v "$PWD:/work" -v /opt/gymnotebook/bin:/bin-gn:ro \
        -v /etc/gymnotebook/upcloud-ca.pem:/etc/gymnotebook/upcloud-ca.pem:ro \
        postgres:18 sh -c "psql \"\$$url\" -X -v ON_ERROR_STOP=1 \"\$@\"" psql "$@"
}
pg TMP_URL -At -c 'SELECT migration_id FROM "__EFMigrationsHistory" ORDER BY migration_id DESC LIMIT 1'
```

If the temporary service's certificate comes from a different CA, mount that service's CA instead.

## 3. Primary reconciliation: compare with the live database

Use this path when the live database can be read completely. Stage the live `users` table's `privacy_account_id`, `password_hash` and `token_version`, and nothing else, in the **temporary** database. The pipe below copies it directly from one database to the other, so no file with hashes lands on disk:

```sh
pg TMP_URL -c 'CREATE TABLE restore_preserved_users (
    privacy_account_id uuid PRIMARY KEY,
    password_hash text NOT NULL,
    token_version integer NOT NULL)'
pg GN_URL -c '\copy (SELECT privacy_account_id, password_hash, token_version FROM users ORDER BY privacy_account_id) TO STDOUT WITH (FORMAT csv)' \
  | pg TMP_URL -c '\copy restore_preserved_users FROM STDIN WITH (FORMAT csv)'
pg GN_URL  -At -c 'SELECT count(*) FROM users'
pg TMP_URL -At -c 'SELECT count(*) FROM restore_preserved_users'
```

The two counts must match. A mismatch, an error from either side of the pipe, or a live `users` table that can't be read closes this path: drop the staging table and use step 4.

A restored UUID absent from `restore_preserved_users` is an account deleted after `T` (the [User-removal invariant](../../specs/001-privacy-account-lifecycle/data-model.md#restore-evidence-and-invariant)). A UUID only in the live database belongs to an account created after `T`; the restore loses it, and it is not recreated by hand. A reused display name never matches by name.

Apply the re-deletion and credential copy as one transaction in the temporary database. `workouts` cascade to workout exercises and sets; exercises go next because a block can reference them with `RESTRICT`; `users` last, as in `AccountDeletion.cs`. Run it interactively (`pg TMP_URL`) so you can stop at the review point:

```sql
BEGIN;
CREATE TEMP TABLE restore_delete_ids AS
SELECT u.id, u.privacy_account_id
FROM users AS u
LEFT JOIN restore_preserved_users AS p USING (privacy_account_id)
WHERE p.privacy_account_id IS NULL;

SELECT count(*) AS accounts_to_redelete FROM restore_delete_ids;
-- Pause here and compare the count with the incident timeline. ROLLBACK if it can't be explained.
DELETE FROM workouts WHERE user_id IN (SELECT id FROM restore_delete_ids);
DELETE FROM exercises WHERE user_id IN (SELECT id FROM restore_delete_ids);
DELETE FROM users WHERE id IN (SELECT id FROM restore_delete_ids);

-- A password change after T must not be undone for an account that still exists.
UPDATE users AS u
SET password_hash = p.password_hash,
    token_version = p.token_version
FROM restore_preserved_users AS p
WHERE u.privacy_account_id = p.privacy_account_id
  AND (u.password_hash, u.token_version)
      IS DISTINCT FROM (p.password_hash, p.token_version);

-- Verify inside the transaction: all three must be 0.
SELECT (SELECT count(*) FROM users     WHERE id      IN (SELECT id FROM restore_delete_ids)) AS users_left,
       (SELECT count(*) FROM workouts  WHERE user_id IN (SELECT id FROM restore_delete_ids)) AS workouts_left,
       (SELECT count(*) FROM exercises WHERE user_id IN (SELECT id FROM restore_delete_ids)) AS exercises_left;
COMMIT;
```

Record the re-deleted count. If the session was interrupted before `COMMIT`, run the whole block again; it rebuilds its list from the staging table. Then drop the staging table, so no hashes from the live database end up in the copy:

```sh
pg TMP_URL -c 'DROP TABLE restore_preserved_users'
pg TMP_URL -At -c "SELECT count(*) FROM pg_tables WHERE tablename = 'restore_preserved_users'"   # must be 0
```

Continue with step 5.

## 4. Fallback when the live database is unusable

Use this only when step 3 can't be done. The application writes `deletion.intent`, `deletion.committed` and `deletion.rolled_back` lines as `<event> PrivacyAccountId=<uuid> DeletionBoundaryAt=<UTC instant>` (`AccountDeletion.cs`) to `gym-api`'s Docker log. Classify each deletion by the event and boundary in the line.

**Coverage first.** The fallback is only possible if the log reaches back to `T` without gaps:

1. **The container is older than `T`.** Docker deletes a container's log when the container is recreated, and **every deploy recreates `gym-api`**. If step 1.4's `Created` time is after `T`, deletions between `T` and that deploy are lost: stop, the fallback is unavailable.
2. **Nothing has rotated out.** The oldest kept line from step 1.4 must be before `T`. The 3 × 10 MB size limit and the daily 30-day trim (deploy/README.md § Log time limit) both remove old lines; backups are at most 3 days old, so the trim alone never reaches `T`.
3. **No gap between `T` and isolation.** The container's healthcheck calls `/health` every 10 seconds, and each call logs a `SELECT 1` line, so every minute the API ran has health lines. List the minutes that have none (`t0` = `T`, `t1` = the isolation instant, both to the minute, UTC):

   ```sh
   t0='2026-10-12T08:00'; t1='2026-10-12T10:00'
   gn logs --no-log-prefix --timestamps --since "${t0}:00Z" --until "${t1}:00Z" gym-api \
     | grep 'SELECT 1' | cut -c1-16 | sort -u > have.txt
   s=$(date -u -d "${t0}:00Z" +%s); e=$(date -u -d "${t1}:00Z" +%s)
   for ((x = s; x < e; x += 60)); do date -u -d "@$x" +%Y-%m-%dT%H:%M; done > want.txt
   comm -23 want.txt have.txt   # minutes with no health line: must print nothing
   ```

   A listed minute is a gap unless the incident record shows `gym-api` was deliberately stopped then (a restart keeps the log; the container's own restarts show in `docker inspect -f '{{.RestartCount}}'`). An unexplained gap stops the fallback.

If any of the three fails, the restore can't be reconciled: keep `gym-api` stopped and escalate without reopening.

**Collect the lines** into the private directory and remove them when the incident closes:

```sh
gn logs --no-log-prefix --timestamps --since "${t0}:00Z" gym-api \
  | grep -E 'deletion\.(intent|committed|rolled_back) PrivacyAccountId=' > deletion-lines.txt
```

Reconcile by UUID **and deletion boundary**, so a later retry can't erase the meaning of an earlier intent. For each `deletion.committed` after `T`, re-delete its UUID if present; a committed line wins even if its intent line is missing. For an intent with a matching `deletion.rolled_back`, leave the account alone for that attempt. For an intent with neither terminal line, don't guess: if its UUID exists in the restored copy, suspend sign-in (below); if it's absent, record that and keep `gym-api` stopped until the outcome is resolved. Conflicting lines or malformed UUIDs also keep it stopped. A missing committed line is never proof of rollback. Deletion lines written before T099's fix (deployed 2026-09-30) can show an intent with no terminal line after a transient error; none of those can follow an UpCloud `T`.

Load **only** the distinct UUIDs with committed lines, and run the deletion interactively (`pg TMP_URL`):

```sql
CREATE TEMP TABLE committed_delete_uuids (privacy_account_id uuid PRIMARY KEY);
-- psql client command; the file holds one UUID per line, taken from the committed lines:
\copy committed_delete_uuids FROM '/work/committed-uuids.txt'

BEGIN;
CREATE TEMP TABLE committed_delete_ids AS
SELECT u.id, u.privacy_account_id
FROM users AS u
JOIN committed_delete_uuids AS c USING (privacy_account_id);

SELECT count(*) AS matched_accounts FROM committed_delete_ids;
-- Review the count before continuing; ROLLBACK if it can't be explained.
DELETE FROM workouts WHERE user_id IN (SELECT id FROM committed_delete_ids);
DELETE FROM exercises WHERE user_id IN (SELECT id FROM committed_delete_ids);
DELETE FROM users WHERE id IN (SELECT id FROM committed_delete_ids);
COMMIT;
```

`committed-uuids.txt` is in the private directory, which `pg` mounts as `/work`. One way to build it: `grep 'deletion.committed' deletion-lines.txt | grep -o 'PrivacyAccountId=[0-9a-f-]*' | cut -d= -f2 | sort -u > committed-uuids.txt`, then keep only those whose boundary is after `T`. Repeating the transaction leaves already removed accounts absent.

For an unresolved intent whose UUID **exists**, suspend it, replacing the example UUID in the session only. Repeating it keeps the original timestamp:

```sql
BEGIN;
UPDATE users
SET sign_in_suspended_at = COALESCE(sign_in_suspended_at, now())
WHERE privacy_account_id = '00000000-0000-0000-0000-000000000000'::uuid
RETURNING privacy_account_id, sign_in_suspended_at;
COMMIT;
```

Require exactly one returned row. A correct password then gets `403 account_suspended`, and token validation rejects the account. Resolve through the privacy contact: verify the person and the intent's outcome. If deletion was intended, delete its workouts, exercises and user in that order in one transaction and verify zero rows; if not, clear only that UUID's marker after recording the resolution:

```sql
BEGIN;
UPDATE users
SET sign_in_suspended_at = NULL
WHERE privacy_account_id = '00000000-0000-0000-0000-000000000000'::uuid
  AND sign_in_suspended_at IS NOT NULL
RETURNING privacy_account_id;
COMMIT;
```

The fallback can't recover password hashes or token versions changed after `T`. Record the affected or unknown scope and the security response in the incident record and the notice review; don't claim those changes survived. The JWT rotation in step 5 still revokes old tokens.

## 5. Copy back, rotate the signing key and reopen

1. **Check the copy** before it replaces anything: `pg TMP_URL -At -f /bin-gn/verify-copy.sql > tmp.txt` gives the counts the live database will have. If `T` predates the optional-details transition clearing, run [its clearing and zero-count check](retention.md#optional-details-transition-clearing-fr-035) against `TMP_URL` now.
2. **Replace the live data** with deploy/README.md § Restore steps 2 and 3, using `TMP_URL` as the dump source and **without** its final `gn up`. Then `pg GN_URL -At -f /bin-gn/verify-copy.sql > live.txt && diff tmp.txt live.txt` must print nothing.
3. **Rotate `Jwt__Secret`** on every restore, fallback included, so a rewound user id or token version can't accept a token issued after `T`. Generate a fresh value in your password manager (at least 48 random bytes) and paste it into `sudo -e /etc/gymnotebook/.env`; never put it on a command line, which `sudo` would write to the auth log, or in a ticket. Since specs/005 the env file is the only copy: no GitHub secret needs the same change.
4. **Start and verify while still closed to deploys**: `gn up -d --wait`, then `https://api.gymnotebook.fit/health` is 200; a control account signs in; a token issued before the restore is rejected; for the fallback, a suspended account gets `403 account_suspended`. Representative surviving accounts keep their notebooks.
5. **Clean up before declaring the restore done**: delete the temporary service in the UpCloud console and confirm it's gone from the list; `rm -r ~/restore` (dump, UUID file, deletion lines); `unset GN_URL TMP_URL`. The temporary service holds other people's live records; it is never kept as a standing copy ([retention.md](retention.md)).
6. **Re-enable deploys** (`gh workflow enable deploy`) and record the evidence source, coverage, re-deleted and suspended counts, credential-copy result, rotation, retention checks, reviewer and the reopening decision.

If any check fails, run `gn stop gym-api` again and resume from the last verified step. On resumption, look at the actual state (which services exist, what the live database holds) before repeating anything: never rerun a restore blindly. The re-deletion, credential copy and suspension SQL can be repeated after their source is reverified. If neither the live comparison nor gap-free logs can be established, the restore stays unavailable: escalate without reopening.
