# Quickstart: validating the move

Owner-run checks, in order. The full step-by-step runbooks go in
`deploy/README.md` (PR 2). This file says what to check and what counts as a
pass. `<server>` is the tracker's server; `admin` is its admin user.

## G1. Capacity gate, before any change

```sh
ssh admin@<server> 'nproc; free -m; df -h /; uptime;
  docker stats --no-stream --format "{{.Name}} {{.MemUsage}} {{.CPUPerc}}"'
```

On the managed database as `upadmin`, through a `postgres:18` container on the
server:

```sql
SHOW max_connections;
SELECT count(*) FROM pg_stat_activity;
SELECT datname, pg_size_pretty(pg_database_size(datname)) FROM pg_database;
SELECT usename, datname FROM pg_stat_activity WHERE datname IS NOT NULL;  -- tracker's role
```

On Neon: `SELECT version();` and `SELECT pg_size_pretty(pg_database_size(current_database()));`.

**Pass**: memory `available` ≥ 900 MB; disk free ≥ 5 GB; load < 0.5; connections
in use + 10 ≤ 40; tracker + Neon size < 8 GiB; the tracker does **not**
connect as `upadmin`, or the tracker-role change is added first. Record the
numbers in research.md R1/R2. **Any fail: stop** (FR-002).

## Provision (after PR 2 and the tracker PR)

Pass when:

- `api.gymnotebook.fit` resolves to the server, and
  `curl https://api.gymnotebook.fit/health` → `200` from the new stack on an
  empty `gymnotebook` database (migrations ran).
- `ssh gymdeploy@<server> 'id; ls'` runs neither. The forced command answers
  `refused` and exits 64.
- Logged in as the tracker's role, `psql -d gymnotebook` → `permission denied
  for database`. The reverse also fails.

## Rehearsal and G2

1. ~~Copy Neon → `gymnotebook` without freezing (a dry run), and time it.~~
   **Skipped** (owner decision 2026-10-12): the only user is the owner, so
   the freeze length does not matter, and the cutover checks the copy with
   `verify-copy.sql` before DNS moves. A failed cutover copy is retried with
   Neon unfrozen and Azure still serving.
2. Restore the 100,000-set reference backup through the app on a test
   account, then export it. `docker inspect -f '{{.State.OOMKilled}}'
   gymnotebook-gym-api-1` → `false`. `free -m` stays under 80%. The tracker's
   `/health` answers in under 1 s throughout.
3. Idle both stacks, then check `free -m`: `available` ≥ 30% (SC-006).
4. Deploy a known-bad tag through `workflow_dispatch` with `target: upcloud`
   (Azure is still production) → exit 1, the previous
   tag still serves, and the tracker's containers are not recreated
   (`docker ps` uptime unchanged).
5. `docker stop gymnotebook-gym-api-1` → our API answers 502, and
   `subscriptionstrack.com` stays green on UptimeRobot.
6. A login request with a forged `X-Forwarded-For` uses the same rate-limit
   bucket as one without it.
7. Delete the test account and empty the `gymnotebook` database before
   cutover.

**G2 pass** = all of the above except the skipped step 1.

## Cutover (PR 4 merged and the amendment signed)

1. Lower the apex DNS TTL a day before. Stop logging.
2. Neon: `ALTER DATABASE … SET default_transaction_read_only = on`.
3. Dump, restore, `ANALYZE`, `verify-copy.sql` on both sides → identical
   (SC-002).
4. Point the `gymnotebook.fit` A record at the server. Caddy issues its
   certificate.
5. Smoke test: sign in with the existing password; notebook and chart
   unchanged; log one set; signup → confirm email → sign in → reset with a
   second address; a Turnstile widget appears.
6. **Smoke test fails**: A record back to `20.240.228.206`, Neon read-only off.
   Anything written on UpCloud is re-entered by hand.

## Decommission (same day, PR 5)

Archive a final Neon `pg_dump -Fc`, and check it by restoring into a
throwaway `postgres:18` with identical `verify-copy.sql` output. Then delete
the Azure resource group, the Entra app registration, the Neon project and the
GitHub secrets. Pass: SC-007, plus `rg -i "azure|neon|containerapp"` finds only
historical notes.

## After 30 days

UpCloud invoice unchanged (SC-001). Tracker availability ≥ 99.5% with no
incident caused by Gym Notebook (SC-003).

## Restore drill (FR-020)

Once, after cutover: restore the service's latest backup into a new service.
`pg_dump -d gymnotebook` from it, and restore into a scratch database on the
live service. `verify-copy.sql` should match the counts as of that backup.
Then delete the temporary service and scratch database. Pass: under 1 hour.
